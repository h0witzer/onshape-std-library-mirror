// dialog-shot.mjs — open a feature's edit dialog and photograph the graphics area.
//
// The reason this exists: error entities registered with `setErrorEntities`, and the `entities`
// passed to `regenError`, are drawn red ONLY while the feature's dialog is open (error.fs says so
// of regenError, and it holds for the rest). A screenshot of the feature merely selected in the
// list shows nothing, which reads as "the marks did not work" when they simply are not being drawn
// yet. So this double-clicks the row to open the dialog before shooting.
//
//   PS_EID=<eid> FEATURE="Y overrideIndexOutOfRange" node dialog-shot.mjs
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, docUrl, OUT_DIR } from './lib.mjs';

const PS_EID = process.env.PS_EID;
if (!PS_EID) { console.error('set PS_EID'); process.exit(1); }
const FEATURE = process.env.FEATURE ?? '';
const WAIT = Number(process.env.WAIT_SECONDS ?? 35);
const SHOT = process.env.SHOT ?? 'dialog-shot.png';

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

  if (!box) {
    console.log('feature row not found in the list');
  } else {
    console.log(`opening "${box.text}"`);
    await page.mouse.dblclick(box.x, box.y);
    await page.waitForTimeout(6000);
    // Zoom to fit so whatever is marked is actually in frame.
    await page.keyboard.press('f');
    await page.waitForTimeout(3000);
  }

  await page.screenshot({ path: join(OUT_DIR, SHOT) });
  console.log(`-> out/${SHOT}`);
} finally {
  await browser.close();
}
