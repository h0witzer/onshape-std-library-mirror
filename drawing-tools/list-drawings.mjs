// list-drawings.mjs — show every drawing element in the document, so probe leftovers can be
// spotted and removed. Pass DELETE_EIDS to remove specific ones.
import { openSession, apiGet } from './lib.mjs';
import { deleteElement } from './drawing.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';

const { browser, page } = await openSession();
try {
  for (const dead of (process.env.DELETE_EIDS ?? '').split(',').filter(Boolean)) {
    console.log(`deleting ${dead}: ${await deleteElement(page, { did: DID, wid: WID, eid: dead })}`);
  }
  const r = await apiGet(page, `/api/v6/documents/d/${DID}/w/${WID}/elements`);
  const draws = (r.body ?? []).filter((e) => e.dataType === 'onshape-app/drawing');
  console.log(`\n${draws.length} drawing element(s):`);
  for (const e of draws) console.log(`  ${e.name}  eid=${e.id}`);
} finally {
  await browser.close();
}
