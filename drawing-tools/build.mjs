// build.mjs — generate the "big Enclosure" drawing to match the SolidWorks PDF.
//
// Sheet coordinates are MILLIMETRES, origin bottom-left; an A1 sheet spans 841 x 594. That was
// confirmed by measuring the generated border geometry.
//
// The drawing is created from Onshape's stock metric ISO_A1.dwt template. That matters: a
// sheet built with the "custom graphics area" option is millimetre-sized but keeps an
// inch-based style, so every scale reads 25.4x off and all style-driven text (dimensions in
// particular, which have no textHeight field) renders at 0.12 mm and is invisible. With the
// metric template a numerator of 3 is a true 3:1 — verified via viewToPaperMatrix = 3000.
//
// Dimensions still carry an explicit millimetre unit, since the drawing unit enum is separate
// from the sheet style.
//
// Views come from the "New Big Case" part studio rather than the "Big Case" assembly: the
// assembly places the PCB outside the enclosure (assembly bbox is 111.66 mm long against the
// enclosure's 59.00), so assembly views show a board floating beside the case.
import { openSession, apiGet } from './lib.mjs';
import { createDrawing, modify, exportDrawingJson, deleteElement } from './drawing.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';

const CASE = 'd02af6edc49b9c78d7d55b65'; // "New Big Case" part studio: 59.32 x 59.00 x 12.00 mm
const PCB = '1c51e0034963ba8ea21ce7c5'; // "PCB" part studio: 33.82 x 108.41 x 7.41 mm
const BASE_PART = 'RhBD'; // "Part 4", 10.00 mm thick — the base
const COVER_PART = 'RZBD'; // "Part 3", 2.00 mm thick — the cover

// Onshape's public metric template library.
const TEMPLATE = {
  templateDocumentId: '4dc2b3d1d578c4f74825b0c6',
  templateWorkspaceId: 'd77a252cbd4776ca33bb5086',
  templateElementId: '5e3bbff9d8780844bd634954', // ISO_A1.dwt
};
const SCALE_MM = (n, d = 1) => ({ scaleSource: 'Custom', numerator: n, denumerator: d });
const at = (x, y) => ({ x, y });
const pt = (x, y) => ({ type: 'Onshape::Reference::Point', coordinate: [x, y, 0] });
// A point that is associative to real geometry: model coordinates plus the entity it snaps to.
const geomPt = (viewId, uniqueId, coord, snapPointType) => ({
  type: 'Onshape::Reference::Point', coordinate: coord, uniqueId, viewId, snapPointType,
});

// Three plan views across the top, isometric at the right, bottom view and front elevation on
// the lower left — the PDF's arrangement. At 3:1 an enclosure plan view is 178 x 177 mm, and
// v_top is pushed right of centre to leave room for its 59.00 dimension.
// The PCB is drawn at 1.5:1 because this revision's board is 108 mm long and would otherwise
// run off the top of the sheet.
const VIEWS = [
  { logicalId: 'v_top', orientation: 'top', ref: { elementId: CASE, idTag: '' }, position: at(150, 460), scale: SCALE_MM(3) },
  { logicalId: 'v_pcb', orientation: 'top', ref: { elementId: PCB, idTag: '' }, position: at(320, 460), scale: SCALE_MM(1.5) },
  { logicalId: 'v_base', orientation: 'top', ref: { elementId: CASE, idTag: BASE_PART }, position: at(500, 460), scale: SCALE_MM(3) },
  { logicalId: 'v_iso', orientation: 'isometric', ref: { elementId: CASE, idTag: '' }, position: at(700, 430), scale: SCALE_MM(2) },
  { logicalId: 'v_bottom', orientation: 'bottom', ref: { elementId: CASE, idTag: '' }, position: at(110, 230), scale: SCALE_MM(3) },
  { logicalId: 'v_front', orientation: 'front', ref: { elementId: CASE, idTag: '' }, position: at(300, 120), scale: SCALE_MM(3) },
];

const BOM_HEADER = ['ITEM NO.', 'PART NUMBER', 'DESCRIPTION', 'thread size', 'QTY.'];
const BOM_ROWS = [
  ['1', 'SMA-KFD0702', 'SMA', '', '2'],
  ['2', 'Base', '', '', '1'],
  ['3', 'cover', '', '', '1'],
  ['4', '92000A010', 'PCB screws', 'M2 x 0.4mm Thread, 3mm Long Coarse', '6'],
  ['5', '91305A106', 'Case screws', 'M2 x 0.4 mm Thread Size, 8 mm Long Countersink Angle 90 deg', '7'],
  ['6', '92095A452', 'SMA screws', 'M2 x 0.4 mm Thread Size, 5 mm Long Coarse', '4'],
  ['7', 'Gasket', '', '', '1'],
  ['8', 'RP2354B0A4_PE43704', 'PCB', '', '1'],
  ['9', 'USB-C extension cable 0,6ft v1.1', 'USB-c cable', '', '1'],
];
const COL_WIDTH = [22, 62, 42, 120, 16];
const ROW_HEIGHT = 9;
const CELL_TEXT = 3.2;

const BALLOON_Y = 173;
const BALLOONS = [
  ['8', 300, BALLOON_Y], ['9', 328, BALLOON_Y], ['7', 356, BALLOON_Y], ['2', 384, BALLOON_Y],
  ['3', 412, BALLOON_Y], ['5', 440, BALLOON_Y], ['1', 468, BALLOON_Y], ['6', 496, BALLOON_Y],
  ['4', 620, 330],
];

// The ISO template's title block already carries the "unless otherwise specified", surface
// finish and sharp-edge boilerplate, so only the drawing title is added here — the title block's
// own TITLE field cannot be filled through the API.
const NOTES = [
  { logicalId: 'n_title', textHeight: 9, position: pt(690, 115), contents: 'big Enclosure' },
];

function tableAnnotation() {
  const cells = [];
  BOM_HEADER.forEach((h, c) => cells.push({ column: c, row: 0, content: h, textHeight: CELL_TEXT }));
  BOM_ROWS.forEach((r, ri) => r.forEach((v, c) => cells.push({ column: c, row: ri + 1, content: v, textHeight: CELL_TEXT })));
  return {
    type: 'Onshape::Table::GeneralTable',
    table: {
      logicalId: 'bom_table', cells,
      columns: BOM_HEADER.length, rows: BOM_ROWS.length + 1,
      showHeaderRow: true, showTitleRow: false,
      position: pt(523, 250),
      horizontalCellMargin: 1, verticalCellMargin: 1,
      formatting: {
        tableWidth: COL_WIDTH.reduce((a, b) => a + b, 0),
        tableHeight: ROW_HEIGHT * (BOM_ROWS.length + 1),
        tableColumnWidth: COL_WIDTH.map((w, i) => ({ columnIndex: i, columnWidth: w })),
        tableRowHeight: Array.from({ length: BOM_ROWS.length + 1 }, (_, i) => ({ rowIndex: i, rowHeight: ROW_HEIGHT })),
      },
    },
  };
}

// Collect every straight-edge endpoint belonging to the given parts, tagged with the entity it
// came from so a dimension can be made associative rather than free-floating.
function endpoints(geom, partIds) {
  const out = [];
  for (const e of geom.bodyData ?? []) {
    if (e.type !== 'line' || e.visible === false) continue;
    const owner = (e.rightRepOccurrences ?? []).concat(e.leftRepOccurrences ?? [])[0] ?? '';
    if (partIds && !partIds.some((p) => owner.startsWith(p))) continue;
    if (e.data?.start) out.push({ uniqueId: e.uniqueId, coord: e.data.start, snap: 'ModeStart' });
    if (e.data?.end) out.push({ uniqueId: e.uniqueId, coord: e.data.end, snap: 'ModeEnd' });
  }
  return out;
}
const extreme = (pts, axis, dir) =>
  pts.reduce((best, p) => (best === null || (dir > 0 ? p.coord[axis] > best.coord[axis] : p.coord[axis] < best.coord[axis]) ? p : best), null);

const MM = { unit: 'Millimeter', isUnitOverridden: true };

function linearDim({ id, viewId, a, b, rotation, textPosition }) {
  return {
    type: 'Onshape::Dimension::PointToPoint',
    pointToPointDimension: {
      logicalId: id,
      point1: geomPt(viewId, a.uniqueId, a.coord, a.snap),
      point2: geomPt(viewId, b.uniqueId, b.coord, b.snap),
      rotation,
      textPosition,
      precision: 2,
      unit: MM,
    },
  };
}

const { browser, page } = await openSession();
try {
  for (const dead of (process.env.DELETE_EIDS ?? '').split(',').filter(Boolean)) {
    console.log(`deleting ${dead}: ${await deleteElement(page, { did: DID, wid: WID, eid: dead })}`);
  }

  console.log('creating A1 drawing...');
  const eid = await createDrawing(page, {
    did: DID, wid: WID, name: 'big Enclosure',
    template: TEMPLATE,
  });
  console.log(`  drawing eid: ${eid}`);

  console.log(`creating ${VIEWS.length} views...`);
  await modify(page, {
    did: DID, wid: WID, eid, description: 'create views',
    jsonRequests: [{
      messageName: 'onshapeCreateViews',
      formatVersion: '2021-01-01',
      views: VIEWS.map((v) => ({
        logicalId: v.logicalId, viewType: 'TopLevel', orientation: v.orientation,
        position: v.position, scale: v.scale, showViewLabel: false, reference: v.ref,
      })),
    }],
  });
  console.log('  views done');

  // Resolve our own view names to the server-side view ids that dimensions must reference.
  const mid = await exportDrawingJson(page, { did: DID, wid: WID, eid });
  const viewIdByAlias = {};
  for (const v of mid?.sheets?.[0]?.views ?? []) viewIdByAlias[v.aliasLogicalId] = v.viewId;
  console.log('  view ids:', JSON.stringify(viewIdByAlias));

  console.log('reading view geometry for dimensions...');
  const geomFor = async (alias) => {
    const r = await apiGet(page, `/api/v6/drawings/d/${DID}/w/${WID}/e/${eid}/views/${viewIdByAlias[alias]}/jsongeometry`);
    if (r.status !== 200) throw new Error(`geometry ${alias} -> ${r.status}`);
    return r.body;
  };
  const gTop = await geomFor('v_top');
  const gFront = await geomFor('v_front');

  const dims = [];
  // Plan view: overall length (Y) and overall width (X) of the enclosure body, excluding the
  // SMA connectors — the pair the PDF calls out as 59.30 and 40.62.
  const topPts = endpoints(gTop, [COVER_PART, BASE_PART]);
  if (topPts.length) {
    const yLo = extreme(topPts, 1, -1), yHi = extreme(topPts, 1, 1);
    const xLo = extreme(topPts, 0, -1), xHi = extreme(topPts, 0, 1);
    console.log(`  plan extents: Y ${((yHi.coord[1] - yLo.coord[1]) * 1000).toFixed(2)} mm, X ${((xHi.coord[0] - xLo.coord[0]) * 1000).toFixed(2)} mm`);
    dims.push(linearDim({ id: 'd_length', viewId: viewIdByAlias.v_top, a: yLo, b: yHi, rotation: 90, textPosition: pt(45, 460) }));
    dims.push(linearDim({ id: 'd_width', viewId: viewIdByAlias.v_top, a: xLo, b: xHi, rotation: 0, textPosition: pt(150, 358) }));
  }
  // Front elevation: overall height, and the base height the PDF calls out as 10.00.
  // View geometry arrives in the view's own frame, not model XYZ — for a front view the
  // on-paper vertical is axis 1, with axis 2 running into the page.
  const VERT = 1;
  const allFront = endpoints(gFront, null);
  const basePts = endpoints(gFront, [BASE_PART]);
  const spanOf = (pts, ax) => (extreme(pts, ax, 1).coord[ax] - extreme(pts, ax, -1).coord[ax]) * 1000;
  if (allFront.length) {
    console.log(`  front view axis spans: ${[0, 1, 2].map((a) => `${a}=${spanOf(allFront, a).toFixed(2)}`).join('  ')} mm`);
    const lo = extreme(allFront, VERT, -1), hi = extreme(allFront, VERT, 1);
    console.log(`  front overall height: ${spanOf(allFront, VERT).toFixed(2)} mm`);
    dims.push(linearDim({ id: 'd_height', viewId: viewIdByAlias.v_front, a: lo, b: hi, rotation: 90, textPosition: pt(196, 120) }));
  }
  if (basePts.length) {
    const lo = extreme(basePts, VERT, -1), hi = extreme(basePts, VERT, 1);
    console.log(`  base height: ${spanOf(basePts, VERT).toFixed(2)} mm`);
    dims.push(linearDim({ id: 'd_base', viewId: viewIdByAlias.v_front, a: lo, b: hi, rotation: 90, textPosition: pt(415, 120) }));
  }

  console.log(`creating table, ${BALLOONS.length} balloons, ${NOTES.length} notes, ${dims.length} dimensions...`);
  await modify(page, {
    did: DID, wid: WID, eid, description: 'create annotations',
    jsonRequests: [{
      messageName: 'onshapeCreateAnnotations',
      formatVersion: '2021-01-01',
      annotations: [
        tableAnnotation(),
        ...BALLOONS.map(([n, x, y]) => ({
          type: 'Onshape::Callout',
          callout: { logicalId: `balloon_${n}`, borderShape: 'Circle', borderSize: 0, contents: n, textHeight: 4, position: pt(x, y) },
        })),
        ...NOTES.map((n) => ({ type: 'Onshape::Note', note: { logicalId: n.logicalId, contents: n.contents, position: n.position, textHeight: n.textHeight } })),
        ...dims,
      ],
    }],
  });
  console.log('  annotations done');

  const j = await exportDrawingJson(page, { did: DID, wid: WID, eid, saveAs: 'big-enclosure.json' });
  const s = j?.sheets?.[0] ?? {};
  console.log(`\nsheet size="${s.size}"  views=${(s.views ?? []).length}`);
  const kinds = {};
  for (const a of s.annotations ?? []) kinds[a.type] = (kinds[a.type] ?? 0) + 1;
  console.log('  annotation kinds:', JSON.stringify(kinds));
  for (const a of s.annotations ?? []) {
    const d = a.pointToPointDimension;
    if (d) console.log(`  dim ${a.aliasLogicalId ?? d.logicalId ?? ''}: displayed="${d.displayedValue ?? '?'}" dangling=${d.isDangling}`);
  }
  console.log(`\nOPEN: https://cad.onshape.com/documents/${DID}/w/${WID}/e/${eid}`);
  console.log(`DRAW_EID=${eid}`);
} finally {
  await browser.close();
}
