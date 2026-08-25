// feature-params.mjs — dump one feature's parameters as the document actually stores them.
//
// For reproducing a failure instead of guessing at it: which queries are selected, which options
// are set, what the numbers are. A feature that errors tells you nothing through the notices pane,
// and its inputs are the half of the problem that is not in the source.
//
//   OS_DID=.. OS_WID=.. PS_EID=.. MATCH=twistedSweep node feature-params.mjs
import { openSession, apiGet, DID, WID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID;
const MATCH = process.env.MATCH ?? '';
if (!PS_EID) {
  console.error('PS_EID=<part studio> is required.');
  process.exit(2);
}

// Parameter values nest as btType-tagged maps; this pulls out the part a human needs.
const describe = (param, depth = 0) => {
  if (param == null || depth > 5) return String(param);
  if (Array.isArray(param)) return param.map((p) => describe(p, depth + 1));
  if (typeof param !== 'object') return param;

  const id = param.parameterId ?? '';
  const t = String(param.btType ?? '');
  if (t.includes('QueryList')) {
    const q = (param.queries ?? []).map((qq) =>
      `${qq.btType?.includes('QueryWithOccurrence') ? 'occ' : ''}${(qq.geometryIds ?? qq.deterministicIds ?? []).join(',') || qq.queryStatement || '?'}`);
    return { id, kind: 'query', count: (param.queries ?? []).length, queries: q };
  }
  if (param.expression !== undefined) return { id, kind: 'quantity', value: param.expression };
  if (param.value !== undefined) return { id, kind: 'value', value: param.value };
  if (param.parameters !== undefined) {
    return { id, kind: 'array', items: param.parameters.map((p) => describe(p, depth + 1)) };
  }
  if (param.items !== undefined) {
    return { id, kind: 'arrayItems', items: param.items.map((p) => describe(p, depth + 1)) };
  }
  return { id, kind: t.split('-')[0].replace('BT', ''), raw: JSON.stringify(param).slice(0, 160) };
};

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(9000);
  const res = await apiGet(page, `/api/v10/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`);
  if (res.status !== 200) { console.log(`features -> ${res.status}`); process.exit(1); }

  for (const entry of res.body?.features ?? []) {
    const name = entry?.message?.featureType ?? entry?.featureType ?? '';
    const label = entry?.message?.name ?? entry?.name ?? '';
    if (MATCH && !`${name} ${label}`.includes(MATCH)) continue;
    console.log(`\n=== ${name}  "${label}" ===`);
    for (const p of entry?.message?.parameters ?? entry?.parameters ?? []) {
      console.log('  ' + JSON.stringify(describe(p)));
    }
  }
} finally {
  await browser.close();
}
