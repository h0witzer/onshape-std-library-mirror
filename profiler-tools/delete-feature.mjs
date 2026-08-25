// delete-feature.mjs — remove features from a Part Studio by featureType or by featureId.
//
// A feature list that contains ANY erroring feature is refused wholesale by Onshape, so one bad
// feature - including one a stray keypress in the browser committed - turns every later insert
// into a 409 that names the wrong thing. This is the way back out.
//
//   PS_EID=<eid> node delete-feature.mjs fillet
//   PS_EID=<eid> node delete-feature.mjs --id FW5J60aegrLTNED_1
//   PS_EID=<eid> node delete-feature.mjs --all
import { openSession, apiGet, DID, WID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID;
if (!PS_EID) { console.error('set PS_EID'); process.exit(1); }
const args = process.argv.slice(2);
const all = args.includes('--all');
const byId = args.includes('--id') ? args[args.indexOf('--id') + 1] : null;
const types = args.filter((a) => !a.startsWith('--') && a !== byId);

const featurePath = `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`;
const apiDelete = (page, path) => page.evaluate(async (p) => {
  const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
  const xsrf = raw.slice(raw.indexOf('=') + 1);
  const r = await fetch(p, {
    method: 'DELETE', credentials: 'same-origin',
    headers: { accept: 'application/json', 'x-xsrf-token': xsrf },
  });
  return { status: r.status };
}, path);

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(8000);
  const before = await apiGet(page, featurePath);
  const features = before.body?.features ?? [];
  console.log(`${features.length} feature(s) present`);
  // Last first: deleting from the end never renumbers anything still to be deleted.
  for (const f of [...features].reverse()) {
    const wanted = all || (byId && f.featureId === byId) || types.includes(f.featureType);
    if (!wanted) continue;
    const res = await apiDelete(page, `${featurePath}/featureid/${encodeURIComponent(f.featureId)}`);
    console.log(`  deleted ${f.featureType} ${f.featureId}: ${res.status}`);
    await page.waitForTimeout(1200);
  }
  const after = await apiGet(page, featurePath);
  console.log(`${after.body?.features?.length ?? 0} feature(s) remain`);
} finally {
  await browser.close();
}
