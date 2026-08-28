// dump-features.mjs — write a Part Studio's raw feature list JSON to a file.
//
// For when a feature has to be built by the API rather than by hand: fetching a real one and
// mutating it beats guessing at BTMSketch/BTMParameter shapes, which are verbose and unforgiving.
//
//   PS_EID=<eid> OUT=features.json node dump-features.mjs
import { writeFileSync } from 'node:fs';
import { openSession, apiGet, DID, WID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID;
const OUT = process.env.OUT ?? 'features.json';
if (!PS_EID) throw new Error('PS_EID is required');

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(6000);
  const res = await apiGet(page, `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`);
  console.log(`status ${res.status}`);
  writeFileSync(OUT, JSON.stringify(res.body, null, 2));
  console.log(`wrote ${OUT}`);
  for (const f of res.body?.features ?? []) {
    console.log(`  ${(f.featureType ?? '?').padEnd(16)} ${f.featureId}  ${JSON.stringify(f.name ?? '')}`);
  }
} finally {
  await browser.close();
}
