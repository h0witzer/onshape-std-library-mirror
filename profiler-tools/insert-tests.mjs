// insert-tests.mjs — put named test features from the tester tab into a Part Studio.
//
// Why this exists: a test whose value is its printlns has to run as a FEATURE in this document.
// The MCP's test_feature runs in the server's own scratch document, where a same-document import
// never resolves, so the tester element cannot execute there at all. Inserting over the session
// is unmetered and the same route the rest of profiler-tools takes.
//
// The namespace of a same-document custom feature is `e<eid>::m<microversion>`, and the
// microversion MOVES every time a tab is written - so it is read fresh here and any existing
// instance is deleted and re-inserted rather than left pointing at the code it was inserted
// against. Idempotent: run it as often as the tester changes.
//
//   PS_EID=<part studio> node insert-tests.mjs sweepAnalyticProfileContactSelfTest ...
//   node insert-tests.mjs                       the two spec 6.5.1 tests, into Part Studio 2
import { openSession, apiGet, apiPost, DID, WID, OTHER_EID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID ?? 'f799a445f4f6036909ad0955'; // Part Studio 2
// An inserted BTMFeature carries only what you POST, and an omitted boolean takes the PARAMETER
// SPEC's "Default" - measured 2026-08-24, not defineFeature's defaults map, which only covers an MCP
// test_feature call. So omitting a flag whose annotation says `"Default" : true` leaves it ON, and
// BOOL_FALSE is the only way to switch one OFF. CLEAR_ALL wipes the tab first, which is what a clean
// profile needs: any other feature's extraction pollutes the evaluator's call count.
const BOOL_TRUE = (process.env.BOOL_TRUE ?? '').split(',').map((s) => s.trim()).filter(Boolean);
const BOOL_FALSE = (process.env.BOOL_FALSE ?? '').split(',').map((s) => s.trim()).filter(Boolean);
const CLEAR_ALL = process.env.CLEAR_ALL === '1';
const WANTED = process.argv.slice(2).length ? process.argv.slice(2) : [
  'sweepAnalyticProfileContactSelfTest',
  'sweepAnalyticProfileLiveTest',
];

const apiDelete = (page, path) => page.evaluate(async (p) => {
  const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
  const xsrf = raw.slice(raw.indexOf('=') + 1);
  const r = await fetch(p, {
    method: 'DELETE',
    credentials: 'same-origin',
    headers: { accept: 'application/json', 'x-xsrf-token': xsrf },
  });
  return { status: r.status };
}, path);

const featurePath = `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`;

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(8000);

  const mv = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/currentmicroversion`);
  const namespace = `e${OTHER_EID}::m${mv.body?.microversion}`;
  console.log(`namespace ${namespace}`);

  const before = await apiGet(page, featurePath);
  const existing = before.body?.features ?? [];
  console.log(`${existing.length} feature(s) already in ${PS_EID}:`);
  for (const f of existing) console.log(`   ${f.featureType}  ${JSON.stringify(f.name)}  ${f.featureId}`);

  for (const f of [...existing].reverse()) {
    if (!CLEAR_ALL && !WANTED.includes(f.featureType)) continue;
    const res = await apiDelete(page, `${featurePath}/featureid/${encodeURIComponent(f.featureId)}`);
    console.log(`deleted stale ${f.featureType} (${f.featureId}): ${res.status}`);
    await page.waitForTimeout(1500);
  }

  for (const featureType of WANTED) {
    const res = await apiPost(page, featurePath, {
      feature: {
        btType: 'BTMFeature-134',
        featureType,
        name: featureType,
        namespace,
        suppressed: false,
        parameters: [
          ...BOOL_TRUE.map((parameterId) => ({
            btType: 'BTMParameterBoolean-144', parameterId, value: true,
          })),
          ...BOOL_FALSE.map((parameterId) => ({
            btType: 'BTMParameterBoolean-144', parameterId, value: false,
          })),
        ],
        subFeatures: [],
        returnAfterSubfeatures: false,
      },
    });
    const created = res.body?.feature?.featureId ?? null;
    console.log(`insert ${featureType}: status ${res.status}${created ? `, featureId ${created}` : ''}`);
    if (res.status >= 400) console.log(`   ${JSON.stringify(res.body).slice(0, 400)}`);
    await page.waitForTimeout(2500);
  }

  const after = await apiGet(page, featurePath);
  console.log(`\nnow ${after.body?.features?.length ?? 0} feature(s):`);
  for (const f of after.body?.features ?? []) console.log(`   ${f.featureType}  ${JSON.stringify(f.name)}`);
} finally {
  await browser.close();
}
