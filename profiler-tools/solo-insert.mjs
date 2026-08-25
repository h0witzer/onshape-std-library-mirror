// solo-insert.mjs — insert each named feature ALONE into a Part Studio and report whether
// Onshape accepts it.
//
// The oracle this exists for: Onshape refuses any change that "would result in the feature list
// becoming invalid", and a list whose LAST feature errors is already invalid - so once one failing
// feature is in, every later insert is refused too and the 409 says nothing about the feature you
// were adding. Clearing the tab before each insert makes the status a per-feature verdict again.
//
//   PS_EID=<eid> node solo-insert.mjs featureTypeA featureTypeB ...
import { openSession, apiGet, apiPost, DID, WID, OTHER_EID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID ?? 'ea13555529d7dd12c3b870d3';
const WANTED = process.argv.slice(2);
if (!WANTED.length) throw new Error('name at least one feature type');
const KEEP_LAST = process.env.KEEP_LAST === '1';

const featurePath = `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`;

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

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(8000);

  const clear = async () => {
    const list = await apiGet(page, featurePath);
    // Last first: deleting from the end never invalidates what is left.
    for (const f of [...(list.body?.features ?? [])].reverse()) {
      await apiDelete(page, `${featurePath}/featureid/${encodeURIComponent(f.featureId)}`);
      await page.waitForTimeout(1200);
    }
  };

  await clear();
  const mv = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/currentmicroversion`);
  const namespace = `e${OTHER_EID}::m${mv.body?.microversion}`;
  console.log(`namespace ${namespace}\n`);

  for (const featureType of WANTED) {
    const res = await apiPost(page, featurePath, {
      feature: {
        btType: 'BTMFeature-134',
        featureType,
        name: featureType,
        namespace,
        suppressed: false,
        parameters: [],
        subFeatures: [],
        returnAfterSubfeatures: false,
      },
    });
    console.log(`${res.status === 200 ? 'ACCEPTED' : 'REFUSED '} ${featureType}`);
    await page.waitForTimeout(2000);
    if (!(KEEP_LAST && featureType === WANTED[WANTED.length - 1])) await clear();
  }
  const after = await apiGet(page, featurePath);
  console.log(`\ntab now holds ${after.body?.features?.length ?? 0} feature(s).`);
} finally {
  await browser.close();
}
