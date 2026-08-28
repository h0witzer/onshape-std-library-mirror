// shot.mjs — open a drawing in the browser and capture it, so the result is verified by
// eye rather than by a 200 response. Usage: DRAW_EID=<eid> node shot.mjs [outName]
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, OUT_DIR } from './lib.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';
const EID = process.env.DRAW_EID;
const NAME = process.argv[2] ?? 'drawing.png';
if (!EID) { console.error('set DRAW_EID'); process.exit(1); }

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  console.log('opening drawing...');
  await page.goto(`https://cad.onshape.com/documents/${DID}/w/${WID}/e/${EID}`, { waitUntil: 'domcontentloaded' });
  // The drawing app renders in a canvas well after domcontentloaded; give it time to settle.
  await page.waitForTimeout(45000);
  const out = join(OUT_DIR, NAME);
  await page.screenshot({ path: out, fullPage: false });
  console.log(`  -> out/${NAME}`);
} finally {
  await browser.close();
}
