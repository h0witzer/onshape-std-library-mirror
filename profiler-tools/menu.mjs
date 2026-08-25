// menu.mjs — open the caret next to "Monitor Part Studio 1" and read what it offers, so the
// harvester picks PROFILING deliberately rather than whatever the toolbar happened to be set to.
// Monitoring and profiling are different things and only one of them carries per-function times.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, OUT_DIR, UTILS_EID, docUrl } from './lib.mjs';

const EID = process.env.FS_EID ?? UTILS_EID;

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(22_000);

  const label = await page.locator('.os-tool-command-name').filter({ hasText: /Part Studio/ }).first().textContent().catch(() => null);
  console.log(`toolbar button currently reads: ${JSON.stringify(label?.trim())}`);

  // Print the toolbar widget's own markup: the caret is a child of it and guessing its offset
  // by pixels already missed once.
  const html = await page.evaluate(() => {
    for (const el of document.querySelectorAll('.os-tool-button')) {
      if (/Part Studio/.test(el.textContent ?? '')) return el.outerHTML.slice(0, 4000);
    }
    return null;
  });
  console.log('\ntoolbar widget markup:\n' + (html ?? '(not found)'));

  // Click whatever child looks like the dropdown arrow, by class, then fall back to the right
  // edge of the widget itself.
  const clicked = await page.evaluate(() => {
    let widget = null;
    for (const el of document.querySelectorAll('.os-tool-button')) {
      if (/Part Studio/.test(el.textContent ?? '')) { widget = el; break; }
    }
    if (!widget) return null;
    // Exactly `.os-caret` - the widget's ng-click="toggleDropdownMenu()" div. Matching loosely
    // on /context/ hits `os-tool-with-context-menu`, which is the MAIN button, and re-clicking
    // that just toggles the current tool instead of opening the menu.
    const caret = widget.querySelector('.os-caret');
    if (caret) {
      const r = caret.getBoundingClientRect();
      return { how: 'os-caret', cls: String(caret.className).slice(0, 80), x: r.x + r.width / 2, y: r.y + r.height / 2 };
    }
    const r = widget.getBoundingClientRect();
    return { how: 'right-edge', cls: '', x: r.right - 9, y: r.y + r.height / 2 };
  });
  console.log(`\ndropdown target: ${JSON.stringify(clicked)}`);
  if (!clicked) { console.log('no widget; stopping.'); }
  else {
    await page.mouse.click(clicked.x, clicked.y);
    await page.waitForTimeout(2500);

    const items = await page.evaluate(() => {
      const out = [];
      for (const el of document.querySelectorAll('li, [role="menuitem"], .os-menu-item, a, button, div')) {
        if (el.childElementCount > 2) continue;
        const t = (el.textContent ?? '').trim();
        if (!t || t.length > 70) continue;
        if (!/profil|monitor|part studio|stop|clear|off|none/i.test(t)) continue;
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) continue;
        out.push({ tag: el.tagName, text: t, cls: String(el.className ?? '').slice(0, 70),
          commandId: el.getAttribute('command-id') ?? el.querySelector?.('[command-id]')?.getAttribute('command-id') ?? '',
          details: el.getAttribute('command-details') ?? '',
          x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) });
      }
      return out;
    });
    console.log(`\nvisible menu-ish items (${items.length}):`);
    for (const i of items) console.log(`   <${i.tag}> ${JSON.stringify(i.text)}  [${i.commandId}] @${i.x},${i.y}  .${i.cls}`);
    await writeFile(join(OUT_DIR, 'menu-items.json'), JSON.stringify(items, null, 2));
    await page.screenshot({ path: join(OUT_DIR, 'menu-open.png') });
    console.log('  -> out/menu-items.json, out/menu-open.png');
  }
} finally {
  await browser.close();
}
