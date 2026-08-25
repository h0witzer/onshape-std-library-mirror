// new-partstudio.mjs — create an empty Part Studio tab and print its eid.
//
// Worth having as its own entry point: the timed fixture lives in Part Studio 1 and the owner's
// face probing in Part Studio 2, so a new test wants its own tab rather than a place in either.
//
//   NAME="Analytic Profile Tests" node new-partstudio.mjs
import { openSession, apiPost, apiGet, DID, WID } from './lib.mjs';

const NAME = process.env.NAME ?? 'Scratch Part Studio';

const { browser, page } = await openSession();
try {
  await page.goto('https://cad.onshape.com/documents', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  const res = await apiPost(page, `/api/v14/partstudios/d/${DID}/w/${WID}`, { name: NAME });
  console.log(`create: ${res.status}  ${JSON.stringify(res.body).slice(0, 300)}`);
  const els = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/elements`);
  for (const e of els.body ?? []) console.log(`  ${e.elementType.padEnd(14)} ${e.id}  ${JSON.stringify(e.name)}`);
} finally {
  await browser.close();
}
