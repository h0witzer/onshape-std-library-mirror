// probe-sheet.mjs — download the probe drawing's JSON to learn sheet size, units, and
// what the default create-from-assembly actually produced.
import { openSession } from './lib.mjs';
import { exportDrawingJson } from './drawing.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';
const EID = process.env.DRAW_EID ?? '8321a51ee359a32e41e6d97b';

const { browser, page } = await openSession();
try {
  console.log('exporting DRAWING_JSON...');
  const d = await exportDrawingJson(page, { did: DID, wid: WID, eid: EID, saveAs: 'probe-drawing.json' });
  const j = typeof d === 'string' ? null : d;
  if (j) {
    console.log('top-level keys:', Object.keys(j).join(', '));
    const sheets = j.sheets ?? j.sheet ?? [];
    console.log('sheets:', Array.isArray(sheets) ? sheets.length : typeof sheets);
    const s0 = Array.isArray(sheets) ? sheets[0] : sheets;
    if (s0) console.log('sheet[0] keys:', Object.keys(s0).join(', '));
  }
} finally {
  await browser.close();
}
