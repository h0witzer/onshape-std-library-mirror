// feature-error-text.mjs — the message behind a feature that reports ERROR, without a watcher.
//
// The feature-list API returns the word ERROR and nothing else, and the FeatureScript notices pane
// only carries printlns while a studio WATCHES the Part Studio - a limited resource. The message
// is painted into the feature-list row's tooltip, so this finds the row by its feature type name,
// hovers it, and reads whatever tooltip appears. It also captures a screenshot, because a tooltip
// that is drawn rather than placed in the DOM is still readable in the picture.
//
//   PS_EID=<part studio> FEATURE=sweepRotatingCubeLiveTest node feature-error-text.mjs
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, docUrl, OUT_DIR } from './lib.mjs';

const PS_EID = process.env.PS_EID;
if (!PS_EID) { console.error('set PS_EID'); process.exit(1); }
const FEATURE = process.env.FEATURE ?? '';
const WAIT = Number(process.env.WAIT_SECONDS ?? 35);
const SHOT = process.env.SHOT ?? 'feature-error.png';

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1400 });
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(WAIT * 1000);

  const box = await page.evaluate((needle) => {
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      const t = (el.textContent ?? '').trim();
      if (!t || (needle && !t.startsWith(needle.slice(0, 18)))) continue;
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0 && r.left < 400) {
        return { x: r.left + r.width / 2, y: r.top + r.height / 2, text: t };
      }
    }
    return null;
  }, FEATURE);
  if (!box) { console.log('feature row not found in the list'); }
  else {
    console.log(`hovering "${box.text}" at ${Math.round(box.x)},${Math.round(box.y)}`);
    await page.mouse.move(box.x, box.y);
    await page.waitForTimeout(2500);
  }
  const texts = await page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      const t = (el.textContent ?? '').trim();
      if (t.length < 20 || t.length > 2000) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      const s = getComputedStyle(el);
      if (s.visibility === 'hidden' || s.opacity === '0') continue;
      out.push(t);
    }
    return [...new Set(out)];
  });
  console.log(`\n${texts.length} visible text run(s) 20+ chars:`);
  for (const t of texts) console.log(`  ${t.slice(0, 900)}`);
  await page.screenshot({ path: join(OUT_DIR, SHOT) });
  console.log(`  -> out/${SHOT}`);
} finally {
  await browser.close();
}
