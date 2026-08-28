// probe-geom.mjs — find the endpoint that returns per-view geometry (edge/vertex ids are
// required to attach real dimensions), and test whether the drawing's length units can be
// switched to millimetres so the title block stops reporting the inch-based numerator.
import { writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, apiGet, apiPost, OUT_DIR } from './lib.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';
const EID = process.env.DRAW_EID ?? '21de1a2905a265b5a4e0073a';
const VIEWID = process.env.VIEW_ID ?? '0b00aa1f997466e8d02cd7bf';

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });

  console.log('view geometry endpoints:');
  for (const p of [
    `/api/v6/drawings/d/${DID}/w/${WID}/e/${EID}/views/${VIEWID}/jsongeometry`,
    `/api/v6/drawings/d/${DID}/w/${WID}/e/${EID}/views/${VIEWID}/geometry`,
    `/api/v6/drawings/d/${DID}/w/${WID}/e/${EID}/views`,
  ]) {
    const r = await apiGet(page, p);
    const size = JSON.stringify(r.body ?? '').length;
    console.log(`  ${r.status}  ${size} bytes  ${p.replace(`/api/v6/drawings/d/${DID}/w/${WID}/e/${EID}`, '...')}`);
    if (r.status === 200 && size > 40) {
      const name = `geom${p.endsWith('views') ? '-list' : ''}.json`;
      await writeFile(join(OUT_DIR, name), JSON.stringify(r.body, null, 2));
      console.log(`     -> out/${name}`);
    }
  }

  console.log('\nunit-setting attempts:');
  for (const [p, body] of [
    [`/api/v6/elements/d/${DID}/w/${WID}/e/${EID}`, { lengthUnits: 'millimeter' }],
    [`/api/v6/elements/d/${DID}/w/${WID}/e/${EID}/units`, { lengthUnits: 'millimeter' }],
    [`/api/v6/documents/d/${DID}/w/${WID}/e/${EID}`, { lengthUnits: 'millimeter' }],
  ]) {
    const r = await apiPost(page, p, body);
    console.log(`  ${r.status}  ${p.replace(`/d/${DID}/w/${WID}`, '/…')}  ${JSON.stringify(r.body).slice(0, 100)}`);
  }
  const after = await apiGet(page, `/api/v6/documents/d/${DID}/w/${WID}/elements?elementId=${EID}`);
  console.log('  element lengthUnits now:', JSON.stringify(after.body?.[0]?.lengthUnits));
} finally {
  await browser.close();
}
