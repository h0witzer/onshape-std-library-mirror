// status.mjs — per-feature regen status for a Part Studio, straight off the feature list.
//
// This is the answer to a 409 on insert, and it is cheap. Onshape refuses any change to a feature
// list that is ALREADY invalid, so the first feature that errors poisons every later insert and the
// 409 stops naming the feature you were adding. The `/features` response carries `featureStates`
// keyed by featureId - `featureStatus` of OK / WARNING / ERROR, plus whatever notices the feature
// reported - which says directly what the 409 will not.
//
// It is also the fallback when the notices pane reads empty: a feature that says OK here ran, so
// zero notice lines means the output did not ROUTE (nothing rebuilt, or the pane was shut), not
// that the test failed.
//
//   PS_EID=<eid> node status.mjs
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, apiGet, OUT_DIR, DID, WID } from './lib.mjs';

const PS_EID = process.env.PS_EID ?? 'ea13555529d7dd12c3b870d3';

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.goto('https://cad.onshape.com/documents', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);

  const res = await apiGet(page, `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`);
  console.log(`features: ${res.status}`);
  const body = res.body ?? {};
  const states = body.featureStates ?? {};
  const named = new Map((body.features ?? []).map((f) => [f.featureId, f.featureType]));

  console.log(`\n${named.size} feature(s) (default planes excluded):`);
  for (const [fid, type] of named) {
    const st = states[fid] ?? {};
    const extra = Object.keys(st)
      .filter((k) => !['btType', 'featureStatus', 'inactive'].includes(k))
      .map((k) => `${k}=${JSON.stringify(st[k])}`)
      .join(' ');
    console.log(`  ${String(st.featureStatus ?? '?').padEnd(8)} ${type.padEnd(38)} ${fid}${extra ? `  ${extra}` : ''}`);
  }
  // INFO is what reportFeatureInfo leaves behind - every test in this stack ends with one, so it
  // is a sign the feature RAN, not a fault. Only WARNING and ERROR invalidate a feature list.
  const bad = [...named].filter(([fid]) => !['OK', 'INFO'].includes(states[fid]?.featureStatus ?? 'OK'));
  console.log(bad.length ? `\n${bad.length} feature(s) in WARNING or ERROR - that is what invalidates the list.`
    : '\nno feature is in WARNING or ERROR.');
  await writeFile(join(OUT_DIR, 'status.json'), JSON.stringify({ states, features: body.features }, null, 2));
} finally {
  await browser.close();
}
