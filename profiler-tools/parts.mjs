// parts.mjs — the bodies a Part Studio holds, by name.
//
// A test that names its output carries its diagnostics in the parts list, and the parts list is
// one API call with no Part Studio watcher behind it. Watchers are a limited resource in a
// workspace - "too many clients watching Part Studios" is a real refusal - so anything that can
// be read this way should be.
//
//   PS_EID=<eid> node parts.mjs
//   PS_EID=<eid> FILTER=face node parts.mjs
import { openSession, apiGet, DID, WID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID;
if (!PS_EID) { console.error('set PS_EID'); process.exit(1); }
const FILTER = process.env.FILTER ? new RegExp(process.env.FILTER) : null;

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(8000);
  const res = await apiGet(page, `/api/v14/parts/d/${DID}/w/${WID}/e/${PS_EID}?withThumbnails=false`);
  const parts = res.body ?? [];
  const shown = parts.filter((p) => !FILTER || FILTER.test(p.name ?? ''));
  console.log(`${parts.length} body/bodies (${shown.length} shown), status ${res.status}`);
  for (const p of shown) console.log(`  ${(p.bodyType ?? '?').padEnd(6)} ${p.name}`);
} finally {
  await browser.close();
}
