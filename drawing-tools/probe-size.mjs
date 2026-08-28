// probe-size.mjs — can sheet size/format be set at creation time? Undocumented; test it.
import { openSession, apiPost } from './lib.mjs';
import { exportDrawingJson, deleteElement } from './drawing.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';

const variants = [
  ['size+format',        { size: 'A1', format: 'ISO' }],
  ['sheetSize',          { sheetSize: 'A1' }],
  ['size only',          { size: 'A1' }],
  ['custom graphics A1', { border: true, titleblock: true, numberHorizontalZones: 16, numberVerticalZones: 12, size: 'A1', format: 'ISO' }],
];

const { browser, page } = await openSession();
const made = [];
try {
  for (const [label, extra] of variants) {
    const payload = { drawingName: `size probe ${label}`, ...extra };
    const r = await apiPost(page, `/api/v6/drawings/d/${DID}/w/${WID}/create`, payload);
    if (r.status !== 200) { console.log(`${label}: create ${r.status} ${JSON.stringify(r.body).slice(0,120)}`); continue; }
    const eid = r.body.id; made.push(eid);
    try {
      const j = await exportDrawingJson(page, { did: DID, wid: WID, eid });
      const s = j?.sheets?.[0] ?? {};
      console.log(`${label}: size="${s.size}" format="${s.format}" scale=${JSON.stringify(s.scale)}`);
    } catch (e) { console.log(`${label}: export failed - ${e.message.slice(0,120)}`); }
  }
} finally {
  console.log('\ncleaning up probe drawings...');
  for (const eid of made) console.log('  delete', eid, await deleteElement(page, { did: DID, wid: WID, eid }));
  console.log('  delete original probe', await deleteElement(page, { did: DID, wid: WID, eid: '8321a51ee359a32e41e6d97b' }));
  await browser.close();
}
