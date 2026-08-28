// calibrate.mjs — the same view at several scales on one A1 sheet, so the relationship
// between the requested scale and the rendered size can be measured instead of guessed.
// Also probes how the general table reports its extent.
import { openSession } from './lib.mjs';
import { createDrawing, modify, exportDrawingJson, deleteElement } from './drawing.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';
const ASM = '196d1dcc6c8f1cc346a97033';

// The enclosure plan footprint, from the PDF's own dimensions.
const PART_W = 40.62, PART_H = 59.30;
const SCALES = [1, 3, 10, 30, 76];

const pt = (x, y) => ({ type: 'Onshape::Reference::Point', coordinate: [x, y, 0] });

const { browser, page } = await openSession();
let eid;
try {
  eid = await createDrawing(page, {
    did: DID, wid: WID, name: 'scale calibration - delete me',
    template: { border: true, titleblock: true, numberHorizontalZones: 16, numberVerticalZones: 12, size: 'A1', format: 'ISO' },
  });
  console.log(`calibration drawing ${eid}`);

  await modify(page, {
    did: DID, wid: WID, eid, description: 'calibration views',
    jsonRequests: [{
      messageName: 'onshapeCreateViews',
      formatVersion: '2021-01-01',
      views: SCALES.map((n, i) => ({
        logicalId: `s${n}`,
        viewType: 'TopLevel',
        orientation: 'top',
        position: { x: 100 + i * 150, y: 400 },
        scale: { scaleSource: 'Custom', numerator: n, denumerator: 1 },
        showViewLabel: false,
        reference: { elementId: ASM, idTag: '' },
      })),
    }],
  });

  // Also drop a table so its reported extent can be compared with what renders.
  await modify(page, {
    did: DID, wid: WID, eid, description: 'calibration table',
    jsonRequests: [{
      messageName: 'onshapeCreateAnnotations',
      formatVersion: '2021-01-01',
      annotations: [{
        type: 'Onshape::Table::GeneralTable',
        table: {
          logicalId: 'cal_table',
          cells: [
            { column: 0, row: 0, content: 'AAA' }, { column: 1, row: 0, content: 'BBB' },
            { column: 0, row: 1, content: '111' }, { column: 1, row: 1, content: '222' },
          ],
          columns: 2, rows: 2, showHeaderRow: false, showTitleRow: false,
          position: pt(600, 150),
        },
      }],
    }],
  });

  const j = await exportDrawingJson(page, { did: DID, wid: WID, eid, saveAs: 'calibration.json' });
  const s = j?.sheets?.[0] ?? {};
  console.log('\nrequested   bbox w x h (mm)     implied mm-per-model-mm   expected at that scale');
  for (const v of s.views ?? []) {
    const n = v.scale?.numerator;
    const bb = v.boundingBoxPoints;
    if (!bb) { console.log(`  ${n}:1  (no bbox)`); continue; }
    const w = bb.max.x - bb.min.x, h = bb.max.y - bb.min.y;
    console.log(`  ${String(n + ':1').padEnd(8)} ${w.toFixed(2)} x ${h.toFixed(2)}`.padEnd(34)
      + `${(w / PART_W).toFixed(3)} / ${(h / PART_H).toFixed(3)}`.padEnd(26)
      + `${(PART_W * n).toFixed(1)} x ${(PART_H * n).toFixed(1)}`);
  }
  for (const a of s.annotations ?? []) {
    if (a.type !== 'Onshape::Table::GeneralTable') continue;
    console.log('\ntable:', JSON.stringify({ position: a.table?.position?.coordinate, bbox: a.table?.boundingBoxPoints }));
  }
} finally {
  if (eid && !process.env.KEEP) console.log('\ndelete calibration:', await deleteElement(page, { did: DID, wid: WID, eid }));
  await browser.close();
}
