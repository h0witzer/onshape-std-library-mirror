// expand.mjs — the profile badge is a COLLAPSED widget: its own markup carries
// "Longest execution steps in this Feature Studio:", so the ranked list is behind a hover or a
// click on it, not spread down the gutter. (311 "markers" harvested by scrolling turned out to
// be the one sticky badge re-read 311 times.)
//
// Hover it, click it, and dump whatever appears, then hover each row of the list for the
// per-function detail the UI shows.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, startProfiling, OUT_DIR, UTILS_EID, docUrl } from './lib.mjs';

const EID = process.env.FS_EID ?? UTILS_EID;
const PS_NAME = process.env.PS_NAME ?? 'Part Studio 1';
const WAIT_MINUTES = Number(process.env.WAIT_MINUTES ?? 14);
const TAG = process.env.TAG ?? 'expand';
const stamp = () => new Date().toISOString().slice(11, 23);
const log = (l) => console.log(`${stamp()} ${l}`);

const snapshot = (page, note) => page.evaluate((n) => {
  const marker = document.querySelector('.fs-profile-data-meta-marker');
  const rect = (el) => { const r = el.getBoundingClientRect(); return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) }; };
  const out = { note: n, markerHtml: marker ? marker.outerHTML.slice(0, 8000) : null, panels: [], rows: [] };
  // Anything that appeared and mentions time, a call count, or a known function name.
  for (const el of document.querySelectorAll('*')) {
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) continue;
    const cls = String(el.className?.baseVal ?? el.className ?? '');
    if (/profile|regen-time|menu-description|execution|meta-marker/i.test(cls)) {
      out.panels.push({ tag: el.tagName, cls: cls.slice(0, 120), rect: rect(el), kids: el.childElementCount,
        text: (el.textContent ?? '').trim().slice(0, 400) });
    }
    if (el.childElementCount !== 0) continue;
    const t = (el.textContent ?? '').trim();
    if (!t || t.length > 120) continue;
    if (!/\d+(\.\d+)?\s*(ms|s)\b|\bcall|\bhit|\btimes\b/i.test(t)) continue;
    out.rows.push({ tag: el.tagName, cls: cls.slice(0, 100), rect: rect(el), text: t });
  }
  return out;
}, note);

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(22_000);

  const total = await startProfiling(page, PS_NAME, {
    waitMinutes: WAIT_MINUTES,
    onTick: (left) => log(`  waiting for the profile... (${left} min left)`),
  });
  if (!total) throw new Error('the profile never came back');
  log(`profile total: ${total}`);

  const states = [];
  const box = await page.locator('.fs-profile-data-meta-marker').first().boundingBox();
  log(`badge box: ${JSON.stringify(box)}`);

  states.push(await snapshot(page, 'before'));

  log('hovering the badge...');
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.waitForTimeout(3000);
  states.push(await snapshot(page, 'hover'));
  await page.screenshot({ path: join(OUT_DIR, `${TAG}-hover.png`) });

  log('clicking the badge...');
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await page.waitForTimeout(3000);
  states.push(await snapshot(page, 'click'));
  await page.screenshot({ path: join(OUT_DIR, `${TAG}-click.png`) });

  for (const s of states) {
    console.log(`\n=== state: ${s.note} ===`);
    console.log(`  panels: ${s.panels.length}`);
    for (const p of s.panels.slice(0, 15)) console.log(`    <${p.tag}> .${p.cls} ${JSON.stringify(p.rect)} kids=${p.kids} ${JSON.stringify(p.text.slice(0, 200))}`);
    console.log(`  time/count-looking leaves: ${s.rows.length}`);
    for (const r of s.rows.slice(0, 40)) console.log(`    <${r.tag}> .${r.cls} ${JSON.stringify(r.rect)} ${JSON.stringify(r.text)}`);
  }

  // If a list appeared, hover each of its rows for the per-entry detail.
  const listRows = await page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('*')) {
      const cls = String(el.className?.baseVal ?? el.className ?? '');
      if (!/profile-.*(row|entry|item|step)|execution-step|profile-list/i.test(cls)) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      out.push({ cls: cls.slice(0, 100), x: r.x + r.width / 2, y: r.y + r.height / 2, text: (el.textContent ?? '').trim().slice(0, 200) });
    }
    return out;
  });
  console.log(`\nlist rows found: ${listRows.length}`);
  const details = [];
  for (const row of listRows.slice(0, 30)) {
    await page.mouse.move(row.x, row.y);
    await page.waitForTimeout(700);
    const tip = await page.evaluate(() => {
      const bits = [];
      for (const el of document.querySelectorAll('[role="tooltip"], .tooltip, .os-tooltip, .popover, .fs-profile-tooltip')) {
        const r = el.getBoundingClientRect();
        if (r.width === 0) continue;
        bits.push((el.textContent ?? '').trim().slice(0, 400));
      }
      return bits;
    });
    details.push({ row: row.text, tooltip: tip });
    console.log(`  ${JSON.stringify(row.text.slice(0, 90))} -> ${JSON.stringify(tip).slice(0, 200)}`);
  }

  await writeFile(join(OUT_DIR, `${TAG}-states.json`), JSON.stringify({ total, states, listRows, details }, null, 2));
  log(`  -> out/${TAG}-states.json, out/${TAG}-hover.png, out/${TAG}-click.png`);
} finally {
  await browser.close();
}
