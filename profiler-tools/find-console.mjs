// find-console.mjs — the FeatureScript console is a panel that has to be OPENED before it
// renders, which is why scraping the page for println text found nothing. Enumerate every
// toggle that could open it, click the best candidate, and see whether our printlns appear.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, OUT_DIR, UTILS_EID, docUrl } from './lib.mjs';

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(process.env.FS_EID ?? UTILS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(22_000);

  const candidates = await page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('*')) {
      const attrs = [];
      for (const a of el.attributes ?? []) attrs.push(`${a.name}=${a.value}`);
      const blob = attrs.join(' ');
      if (!/console|notice|output|log/i.test(blob)) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      out.push({
        tag: el.tagName,
        cls: String(el.className?.baseVal ?? el.className ?? '').slice(0, 90),
        hit: attrs.filter((a) => /console|notice|output|log/i.test(a)).slice(0, 3).join(' | ').slice(0, 200),
        x: Math.round(r.x + r.width / 2), y: Math.round(r.y + r.height / 2),
        w: Math.round(r.width), h: Math.round(r.height),
      });
    }
    return out;
  });
  console.log(`console-ish elements: ${candidates.length}`);
  for (const c of candidates.slice(0, 30)) console.log(`  <${c.tag}> ${c.w}x${c.h} @${c.x},${c.y} .${c.cls}\n        ${c.hit}`);
  await writeFile(join(OUT_DIR, 'console-candidates.json'), JSON.stringify(candidates, null, 2));

  // The left icon rail is the usual home for it; list what is there.
  const rail = await page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('svg, use, [class*="icon"]')) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.x > 45) continue;
      const href = el.getAttribute?.('href') ?? el.getAttribute?.('xlink:href') ?? '';
      const icon = el.getAttribute?.('icon') ?? '';
      const title = el.closest('[title],[data-bs-original-title]')?.getAttribute('title')
        ?? el.closest('[data-bs-original-title]')?.getAttribute('data-bs-original-title') ?? '';
      out.push({ tag: el.tagName, icon, href, title: title.slice(0, 90), y: Math.round(r.y + r.height / 2), x: Math.round(r.x + r.width / 2) });
    }
    return out;
  });
  console.log(`\nleft rail items: ${rail.length}`);
  for (const r of rail) console.log(`  <${r.tag}> @${r.x},${r.y} icon=${r.icon} href=${r.href} title=${JSON.stringify(r.title)}`);
  await writeFile(join(OUT_DIR, 'left-rail.json'), JSON.stringify(rail, null, 2));
  await page.screenshot({ path: join(OUT_DIR, 'find-console.png') });
} finally {
  await browser.close();
}
