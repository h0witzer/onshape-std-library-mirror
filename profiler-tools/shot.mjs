// shot.mjs — open a tab in the browser, fit the view, and capture it.
//
// A verdict line says whether the checks passed; it does not say whether the shell LOOKS like a
// swept solid. Colouring the emitted sheets by what produced them and then looking at them is the
// diagnostic that catches a patch in the wrong place, a hole where a segment was skipped, and a
// sliver that reads as a healthy number - none of which a tolerance can report.
//
//   PS_EID=<eid> node shot.mjs [outName]
//   PS_EID=<eid> SHOT_WAIT=60 VIEW=iso node shot.mjs cube-iso.png
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, OUT_DIR, docUrl } from './lib.mjs';

const EID = process.env.PS_EID;
const NAME = process.argv[2] ?? 'shot.png';
const WAIT_SECONDS = Number(process.env.SHOT_WAIT ?? 40);
if (!EID) { console.error('set PS_EID'); process.exit(1); }

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(WAIT_SECONDS * 1000);

  // Fit the view through the VIEW MENU, never a keyboard shortcut. Onshape's modelling shortcuts
  // live on the same keys - `f` opens Fillet - so a stray keypress aimed at the graphics area
  // starts a feature instead of moving the camera, and the screenshot then shows a dialog over
  // the geometry it was taken to inspect.
  const fitted = await page.evaluate(() => {
    const el = document.querySelector('[data-original-title="Zoom to fit"], [title="Zoom to fit"]');
    if (!el) return false;
    el.click();
    return true;
  });
  if (!fitted) {
    console.log('  (no zoom-to-fit control found; capturing the camera as it stands)');
  }
  await page.waitForTimeout(3000);

  const out = join(OUT_DIR, NAME);
  await page.screenshot({ path: out, fullPage: false });
  console.log(`  -> out/${NAME}`);
} finally {
  await browser.close();
}
