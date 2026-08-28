// probe-template.mjs — does creating from a metric ISO_A1 template give a drawing whose
// style matches its millimetre sheet? If so, view scale can be stated honestly as 3:1 and
// style-driven text (dimensions, tables) renders at a readable size without overrides.
import { openSession, apiGet, apiPost } from './lib.mjs';
import { modify, exportDrawingJson, deleteElement } from './drawing.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';
const CASE = 'd02af6edc49b9c78d7d55b65';

const TPL_DID = '4dc2b3d1d578c4f74825b0c6';
const TPL_A1_BLOB = '5e3bbff9d8780844bd634954'; // ISO_A1.dwt
const TPL_A1_DRAWING = 'eb9097b237363f932118ddbd'; // "ISO_A1.dwt Drawing 1"

const { browser, page } = await openSession();
const made = [];
try {
  const doc = await apiGet(page, `/api/v6/documents/${TPL_DID}`);
  const twid = doc.body?.defaultWorkspace?.id;
  console.log(`template doc "${doc.body?.name}" wid=${twid} public=${doc.body?.public}`);
  if (!twid) throw new Error('no template workspace');

  for (const [label, teid] of [['dwt blob', TPL_A1_BLOB], ['drawing element', TPL_A1_DRAWING]]) {
    const r = await apiPost(page, `/api/v6/drawings/d/${DID}/w/${WID}/create`, {
      drawingName: `tpl probe ${label}`,
      templateDocumentId: TPL_DID,
      templateWorkspaceId: twid,
      templateElementId: teid,
    });
    console.log(`\n${label}: create ${r.status}`);
    if (r.status !== 200) { console.log(`  ${JSON.stringify(r.body).slice(0, 200)}`); continue; }
    const eid = r.body.id; made.push(eid);

    // One view at an honest 3:1, plus a note with NO explicit text height, to see what the
    // template's own style produces.
    await modify(page, {
      did: DID, wid: WID, eid, description: 'probe',
      jsonRequests: [
        {
          messageName: 'onshapeCreateViews', formatVersion: '2021-01-01',
          views: [{
            logicalId: 'probe_view', viewType: 'TopLevel', orientation: 'top',
            position: { x: 200, y: 300 },
            scale: { scaleSource: 'Custom', numerator: 3, denumerator: 1 },
            showViewLabel: false, reference: { elementId: CASE, idTag: '' },
          }],
        },
        {
          messageName: 'onshapeCreateAnnotations', formatVersion: '2021-01-01',
          annotations: [{
            type: 'Onshape::Note',
            note: { logicalId: 'probe_note', contents: 'STYLE DEFAULT TEXT', position: { type: 'Onshape::Reference::Point', coordinate: [400, 150, 0] } },
          }],
        },
      ],
    });

    const j = await exportDrawingJson(page, { did: DID, wid: WID, eid });
    const s = j?.sheets?.[0] ?? {};
    const v = (s.views ?? [])[0];
    const bb = v?.boundingBoxPoints;
    console.log(`  sheet size="${s.size}" format="${s.format}" scale=${JSON.stringify(s.scale)}`);
    console.log(`  view paper matrix scale: ${v?.viewToPaperMatrix?.items?.[0]}`);
    console.log(`  view rendered: ${bb ? `${(bb.max.x - bb.min.x).toFixed(1)} x ${(bb.max.y - bb.min.y).toFixed(1)}` : '?'} (want ~178 x 177 for true 3:1)`);
    const note = (s.annotations ?? []).find((a) => a.aliasLogicalId === 'probe_note');
    const nb = note?.note?.boundingBoxPoints;
    console.log(`  default-style note height: ${nb ? (nb.max.y - nb.min.y).toFixed(2) : '?'} mm (want ~3-4)`);
  }
} finally {
  if (!process.env.KEEP) for (const eid of made) console.log(`delete ${eid}: ${await deleteElement(page, { did: DID, wid: WID, eid })}`);
  await browser.close();
}
