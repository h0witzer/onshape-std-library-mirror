// tweep-fixture.mjs — build Tweep cases in a throwaway tab and measure each one.
//
// For behavior that only a real feature insert can show: a bare lambda cannot import a custom
// feature, and iterating on the owner's own tabs is both slow and rude. This makes its own
// geometry, inserts one twistedSweep per case, measures the bodies, and deletes the tab.
//
//   path     a line along Y from -50 to +50 on the Top plane
//   profile  a 20 x 10 rectangle on the Front plane -- world y = 0, the path's midpoint, and NOT
//            rotationally symmetric, so a twist shows up in the bounding box
//
// Onshape assigns its own featureId on insert, so every query is built from the id the server
// hands back. Building them from the ids sent up is what made the first version of this measure
// four empty contexts in a row.
//
//   TEMPLATE=<features.json> node tweep-fixture.mjs
import { openSession, apiGet, apiPost, DID, WID, docUrl } from './lib.mjs';
import { readFileSync } from 'node:fs';

const TEMPLATE = process.env.TEMPLATE;
const KEEP = process.env.KEEP === '1';
if (!TEMPLATE) throw new Error('TEMPLATE=<features.json holding a twistedSweep and an override item> is required');

const template = JSON.parse(readFileSync(TEMPLATE, 'utf8'));
const sweepTemplate = template.features.find((f) => f.featureType === 'twistedSweep');

if (!sweepTemplate) throw new Error('no twistedSweep in the template');

const strip = (v) => Array.isArray(v) ? v.map(strip)
  : (v && typeof v === 'object'
    ? Object.fromEntries(Object.entries(v).filter(([k]) => k !== 'nodeId').map(([k, x]) => [k, strip(x)]))
    : v);

const fsQuery = (source) => ({ btType: 'BTMIndividualQuery-138', queryStatement: null, queryString: source });

const HALF = 0.05;      // path half length, metres
const WIDE = 0.010;     // profile half width along world X
const TALL = 0.005;     // profile half height along world Z

const line = (id, pntX, pntY, dirX, dirY, startParam, endParam) => ({
  btType: 'BTMSketchCurveSegment-155',
  entityId: id,
  geometry: { btType: 'BTCurveGeometryLine-117', pntX, pntY, dirX, dirY },
  startParam, endParam,
  startPointId: `${id}.start`, endPointId: `${id}.end`,
  isConstruction: false,
});

const arc = (id, xCenter, yCenter, radius, startAngle, endAngle) => ({
  btType: 'BTMSketchCurveSegment-155',
  entityId: id,
  geometry: {
    btType: 'BTCurveGeometryCircle-115',
    clockwise: false, radius, xCenter, yCenter, xDir: 1, yDir: 0,
  },
  startParam: startAngle,
  endParam: endAngle,
  startPointId: `${id}.start`, endPointId: `${id}.end`,
  isConstruction: false,
});

const sketch = (name, planeId, entities) => ({
  btType: 'BTMSketch-151',
  featureType: 'newSketch',
  name,
  suppressed: false,
  parameters: [{
    btType: 'BTMParameterQueryList-148',
    parameterId: 'sketchPlane',
    queries: [fsQuery(`query=qCreatedBy(makeId("${planeId}"), EntityType.FACE);`)],
  }],
  entities,
  constraints: [],
});

// Rectangle as four segments meeting exactly at the corners.
const rectangle = [
  line('bottom', -WIDE, -TALL, 1, 0, 0, 2 * WIDE),
  line('right', WIDE, -TALL, 0, 1, 0, 2 * TALL),
  line('top', WIDE, TALL, -1, 0, 0, 2 * WIDE),
  line('left', -WIDE, TALL, 0, -1, 0, 2 * TALL),
];

const integerParam = (parameterId, value) => ({
  btType: 'BTMParameterQuantity-147', parameterId,
  isInteger: true, value: 0, units: '', expression: String(value),
});
const realParam = (parameterId, expression) => ({
  btType: 'BTMParameterQuantity-147', parameterId,
  isInteger: false, value: 0, units: '', expression,
});
const boolParam = (parameterId, value) => ({
  btType: 'BTMParameterBoolean-144', parameterId, value,
});

const makeOverride = ({ vertexIndex, twist, angleDeg, scale, factor }) => ({
  btType: 'BTMArrayParameterItem-1843',
  parameters: [
    integerParam('index', vertexIndex),
    boolParam('overridesTwist', twist),
    realParam('angleOverride', `${angleDeg}*deg`),
    boolParam('oppositeAngleOverride', false),
    boolParam('overridesScale', scale),
    realParam('scaleFactorOverride', String(factor)),
  ],
});

const makeSelection = (vertexIndex) => ({
  btType: 'BTMArrayParameterItem-1843',
  parameters: [integerParam('indexValue', vertexIndex)],
});

const measureScript = (endY) => `function(context is Context, queries)
{
    const solids = qBodyType(qEverything(EntityType.BODY), BodyType.SOLID);
    const found = evaluateQuery(context, solids);
    if (size(found) == 0)
    {
        println({ "solids" : 0 });
        return "done";
    }
    const bounds = evBox3d(context, { "topology" : solids });
    println({
                "solids" : size(found),
                "volume" : roundToPrecision(evVolume(context, { "entities" : solids }) /
                        (millimeter * millimeter * millimeter), 3),
                "xHalf" : roundToPrecision(bounds.maxCorner[0] / millimeter, 3),
                "yMin" : roundToPrecision(bounds.minCorner[1] / millimeter, 3),
                "yMax" : roundToPrecision(bounds.maxCorner[1] / millimeter, 3),
                "zHalf" : roundToPrecision(bounds.maxCorner[2] / millimeter, 3)
            });

    // The face in the path's end plane: its own box says how far the section has rolled there.
    if ("${endY}" == "skip")
    {
        return "done";
    }
    const endFace = qCoincidesWithPlane(qOwnedByBody(solids, EntityType.FACE),
            plane(vector(0, ${endY}, 0) * meter, vector(0, 1, 0)));
    if (size(evaluateQuery(context, endFace)) > 0)
    {
        const faceBounds = evBox3d(context, { "topology" : endFace });
        println({
                    "endFaceWide" : roundToPrecision((faceBounds.maxCorner[0] - faceBounds.minCorner[0]) / millimeter, 2),
                    "endFaceTall" : roundToPrecision((faceBounds.maxCorner[2] - faceBounds.minCorner[2]) / millimeter, 2)
                });
    }
    return "done";
}`;

const { browser, page } = await openSession();
try {
  await page.goto('https://cad.onshape.com/documents', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  const created = await apiPost(page, `/api/v14/partstudios/d/${DID}/w/${WID}`, { name: 'Tweep Fixture' });
  const PS_EID = created.body?.id;
  if (!PS_EID) throw new Error(`no tab: ${JSON.stringify(created.body).slice(0, 300)}`);
  console.log(`tab ${PS_EID}`);

  const featurePath = `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`;
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(8000);

  // Returns the id the SERVER chose, which is what every query has to name.
  const add = async (feature, label) => {
    const res = await apiPost(page, featurePath, { feature, btType: 'BTFeatureDefinitionCall-1406' });
    const id = res.body?.feature?.featureId;
    console.log(`  ${label.padEnd(16)} ${res.status}  ${id ?? JSON.stringify(res.body).slice(0, 200)}`);
    await page.waitForTimeout(1500);
    return id;
  };
  const drop = async (featureId) => {
    await page.evaluate(async (p) => {
      const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
      await fetch(p, { method: 'DELETE', credentials: 'same-origin',
        headers: { accept: 'application/json', 'x-xsrf-token': raw.slice(raw.indexOf('=') + 1) } });
    }, `${featurePath}/featureid/${encodeURIComponent(featureId)}`);
    await page.waitForTimeout(1500);
  };

  // The profile sits at world y = 0 either way. One path BEGINS there, the other straddles it,
  // which is the only difference between them -- so any case that behaves differently across the
  // two is telling us the profile's position along the path is what matters.
  const startPathId = await add(sketch('Path From Profile', 'Top',
    [line('spine', 0, 0, 0, 1, 0, 2 * HALF)]), 'start path');
  const midPathId = await add(sketch('Path Through Profile', 'Top',
    [line('spine', 0, 0, 0, 1, -HALF, HALF)]), 'mid path');
  const profileId = await add(sketch('Fixture Profile', 'Front', rectangle), 'profile sketch');
  // Two-segment versions, whose junction gives a genuinely interior vertex to pin. The profile
  // sits at world y = 0 throughout: it is the START of twoStart and the JUNCTION of twoMid.
  const twoStartPathId = await add(sketch('Two Span From Profile', 'Top',
    [line('spineA', 0, 0, 0, 1, 0, HALF), line('spineB', 0, 0, 0, 1, HALF, 2 * HALF)]), 'two-span start');
  const twoMidPathId = await add(sketch('Two Span Through Profile', 'Top',
    [line('spineA', 0, 0, 0, 1, -HALF, 0), line('spineB', 0, 0, 0, 1, 0, HALF)]), 'two-span mid');

  // Three spans, so the profile can sit mid-SEGMENT while the overridden vertex is a junction
  // somewhere else -- which is the owner's actual model, and the case neither L nor M covers.
  const threeMidPathId = await add(sketch('Three Span Through Profile', 'Top',
    [line('spineA', 0, 0, 0, 1, -0.075, -0.025),
     line('spineB', 0, 0, 0, 1, -0.025, 0.025),
     line('spineC', 0, 0, 0, 1, 0.025, 0.075)]), 'three-span mid');

  // A path that actually CURVES, in two segments: a 50 mm run up +Y from the profile, then a
  // quarter arc turning into +X. Straight-line fixtures cannot tell "follows the path" from
  // "straight shot start to end", because both have nearly the same bounding box -- the volume
  // over the true arc length is what separates them.
  const curvedPathId = await add(sketch('Curved Two Span', 'Top', [
    line('spineA', 0, 0, 0, 1, 0, HALF),
    arc('arcB', HALF, HALF, HALF, Math.PI / 2, Math.PI),
  ]), 'curved path');

  const PATHS = {
    curved: { id: curvedPathId, far: 2, end: null },
    threeMid: { id: threeMidPathId, far: 2, end: 0.075 },
    start: { id: startPathId, far: 1, end: 2 * HALF },
    mid: { id: midPathId, far: 1, end: HALF },
    // `far` is the vertex an override pins: the JUNCTION, not the end.
    twoStart: { id: twoStartPathId, far: 1, end: 2 * HALF },
    twoMid: { id: twoMidPathId, far: 1, end: HALF },
  };

  // Sanity first: the sweep can only be read once the sketch actually yields a region.
  const preflight = await apiPost(page, `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/featurescript`, {
    script: `function(context is Context, queries)
{
    const paths = {
            "start" : makeId("${startPathId}"),
            "mid" : makeId("${midPathId}"),
            "twoStart" : makeId("${twoStartPathId}"),
            "twoMid" : makeId("${twoMidPathId}"),
            "threeMid" : makeId("${threeMidPathId}"),
            "curved" : makeId("${curvedPathId}")
        };
    var report = { "profileRegions" : size(evaluateQuery(context, qSketchRegion(makeId("${profileId}")))) };
    for (var name, sketchId in paths)
    {
        const path = constructPath(context, qCreatedBy(sketchId, EntityType.EDGE));
        const total = evPathLength(context, path);
        var fractions = [0];
        var travelled = 0 * meter;
        for (var edgeIndex = 0; edgeIndex < size(path.edges) - 1; edgeIndex += 1)
        {
            travelled += evLength(context, { "entities" : path.edges[edgeIndex] });
            fractions = append(fractions, travelled / total);
        }
        if (!path.closed)
        {
            fractions = append(fractions, 1);
        }
        const lines = evPathTangentLines(context, path, fractions).tangentLines;
        var spots = [];
        for (var line in lines)
        {
            spots = append(spots, roundToPrecision(line.origin[1] / millimeter, 1));
        }
        report[name] = spots;
    }
    println(report);
    return "done";
}`, queries: {} });
  console.log(`\npreflight: ${(preflight.body?.console ?? '').trim()}`);

  // Every case is the same profile and the same 100 mm of path. What varies is where the path
  // starts relative to the profile, and how the scale is asked for.
  const CASES = [
    ['A baseline', { path: 'mid', hasTwist: false, hasScale: false, factor: 1, overrides: [] }],
    ['B twistOverrideOnly', { path: 'mid', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: true, angleDeg: 90, scale: false, factor: 1 }] }],
    ['C scaleOverrideMid', { path: 'mid', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: false, angleDeg: 0, scale: true, factor: 2 }] }],
    ['D scaleOverrideStart', { path: 'start', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: false, angleDeg: 0, scale: true, factor: 2 }] }],
    ['E plainTaperMid', { path: 'mid', hasTwist: false, hasScale: true, factor: 2, overrides: [] }],
    ['F plainTaperStart', { path: 'start', hasTwist: false, hasScale: true, factor: 2, overrides: [] }],
    // The deviation property: with a global twist running, an override left at its default must
    // change NOTHING (H identical to G), while a non-default one still bites (I differs).
    ['G quarterTurnBare', { path: 'mid', hasTwist: true, turns: 0.25, hasScale: false, factor: 1,
      overrides: [] }],
    ['H quarterTurnZeroOverride', { path: 'mid', hasTwist: true, turns: 0.25, hasScale: false, factor: 1,
      overrides: [{ twist: true, angleDeg: 0, scale: false, factor: 1 }] }],
    ['I quarterTurnPlusOverride', { path: 'mid', hasTwist: true, turns: 0.25, hasScale: false, factor: 1,
      overrides: [{ twist: true, angleDeg: 90, scale: false, factor: 1 }] }],
    // Scale keys are ABSOLUTE, so unlike twist a 1.0 override against a 1->2 taper is a real edit:
    // it pins that vertex to unit size. K should therefore NOT match J -- it should come out
    // untapered (20000), which is what absolute means.
    ['J taperStartBare', { path: 'start', hasTwist: false, hasScale: true, factor: 2, overrides: [] }],
    ['K taperStartUnitPin', { path: 'start', hasTwist: false, hasScale: true, factor: 2,
      overrides: [{ twist: false, angleDeg: 0, scale: true, factor: 1 }] }],
    // The reported symptom. Same 90 deg override on the same interior junction; the ONLY difference
    // is whether the profile sits at the path's start or on that junction. If the anchor is what
    // decides, L rolls the junction and leaves its end alone (end face 20 x 10) while M leaves the
    // junction alone and rolls everything else (end face 10 x 20).
    ['L midVertexProfileAtStart', { path: 'twoStart', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: true, angleDeg: 90, scale: false, factor: 1 }] }],
    ['M midVertexProfileOnIt', { path: 'twoMid', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: true, angleDeg: 90, scale: false, factor: 1 }] }],
    // The owner's configuration: profile mid-SEGMENT, override on a junction elsewhere. The
    // overridden vertex should take the 90 deg and the far end should come back square (20 x 10).
    // Un-anchored, the profile's own value rode up with the override and the far end came out
    // rolled instead.
    // Zero scale: a section that closes to nothing should become a point and the sweep should come
    // to a tip there. O reaches zero by the global factor, P by an override, Q with the profile
    // part way along, and R pinches to a point in the MIDDLE of the path and back out again.
    ['O zeroTaperStart', { path: 'start', hasTwist: false, hasScale: true, factor: 0, overrides: [] }],
    ['P zeroOverrideFarFromStart', { path: 'start', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: false, angleDeg: 0, scale: true, factor: 0 }] }],
    ['Q zeroOverrideFarFromMid', { path: 'mid', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: false, angleDeg: 0, scale: true, factor: 0 }] }],
    ['R zeroOverrideMidVertex', { path: 'threeMid', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: false, angleDeg: 0, scale: true, factor: 0 }] }],
    // The owner's reported case: multi-segment WITH A CURVE, zero at the far end. Path length is
    // 50 + a quarter arc of r=50 = 128.54 mm, so a body that follows it holds 200*128.54/3 =
    // 8569.4 mm^3, while a straight shot down the 111.8 mm chord would hold only 7453.6.
    ['S curvedZeroTaper', { path: 'curved', hasTwist: false, hasScale: true, factor: 0, overrides: [] }],
    ['T curvedZeroSmooth', { path: 'curved', hasTwist: false, hasScale: true, factor: 0, smooth: true,
      overrides: [] }],
    // The control that separates "the taper is wrong" from "the loft is wrong on curves": no scale
    // at all, but smooth forces the loft route anyway. A faithful loft must match U's kernel sweep
    // at 200 * 128.54 = 25708.239.
    ['V curvedPlainSmooth', { path: 'curved', hasTwist: false, hasScale: false, factor: 1,
      smooth: true, overrides: [] }],
    ['W curvedHalfTaper', { path: 'curved', hasTwist: false, hasScale: true, factor: 0.5,
      overrides: [] }],
    ['U curvedPlain', { path: 'curved', hasTwist: false, hasScale: false, factor: 1, overrides: [] }],
    // Vertices SELECTED but not yet overridden. This is the path that draws handles with nothing
    // stored behind them, so it must regenerate exactly like the untouched baseline (20000).
    ['X selectionOnlyNoOverrides', { path: 'mid', hasTwist: false, hasScale: false, factor: 1,
      select: [0, 1], overrides: [] }],
    // Deliberate failures, to check the reporting itself rather than the geometry.
    ['Y overrideIndexOutOfRange', { path: 'mid', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ at: 99, twist: true, angleDeg: 45, scale: false, factor: 1 }] }],
    ['Z twoOverridesOneVertex', { path: 'mid', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ at: 1, twist: true, angleDeg: 45, scale: false, factor: 1 },
                  { at: 1, twist: true, angleDeg: 90, scale: false, factor: 1 }] }],
    // A section far larger than the arc it is swept around, which should make the loft itself fail
    // -- the case whose highlighting is the span's own stretch of path plus the profile.
    ['AA curvedOversizeScale', { path: 'curved', hasTwist: false, hasScale: true, factor: 40,
      overrides: [] }],
    ['N profileMidSegment', { path: 'threeMid', hasTwist: false, hasScale: false, factor: 1,
      overrides: [{ twist: true, angleDeg: 90, scale: false, factor: 1 }] }],
  ];

  const results = {};
  const ONLY = process.env.ONLY;
  for (const [label, options] of CASES) {
    if (ONLY && !ONLY.split(',').some((pick) => label.startsWith(pick.trim()))) continue;
    const chosen = PATHS[options.path];
    const feature = strip(JSON.parse(JSON.stringify(sweepTemplate)));
    feature.name = label;
    const param = (id) => feature.parameters.find((p) => p.parameterId === id);
    const paramOrAdd = (id, factory) => {
      let found = feature.parameters.find((p) => p.parameterId === id);
      if (!found) { found = factory(); feature.parameters.push(found); }
      return found;
    };
    param('pathEdge').queries = [fsQuery(`query=qCreatedBy(makeId("${chosen.id}"), EntityType.EDGE);`)];
    param('profiles').queries = [fsQuery(`query=qSketchRegion(makeId("${profileId}"));`)];
    param('hasTwist').value = options.hasTwist;
    param('hasScale').value = options.hasScale;
    param('smoothOutput').value = options.smooth === true;
    param('turns').expression = String(options.turns ?? 0);
    param('scaleFactor').expression = String(options.factor);
    param('overrides').items = options.overrides.map((o) => makeOverride({ vertexIndex: o.at ?? chosen.far, ...o }));
    // Selecting the same vertices the overrides name, so the graphics selection and the stored
    // edits agree the way they would after a click.
    paramOrAdd('selectedIndices', () => ({
      btType: 'BTMParameterArray-2025', parameterId: 'selectedIndices', items: [],
    })).items = [...new Set(options.select ?? options.overrides.map(() => chosen.far))].map(makeSelection);

    const id = await add(feature, label);
    if (!id) { results[label] = 'REFUSED'; continue; }
    const measured = await apiPost(page,
      `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/featurescript`,
      { script: measureScript(chosen.end ?? 'skip'), queries: {} });

    // A case that builds nothing has a reason, and the feature list is where it is written.
    const list = await apiGet(page, featurePath);
    const state = (list.body?.featureStates ?? {})[id] ?? {};
    const status = state.featureStatus ?? '?';
    const detail = status === 'OK' ? '' :
      `  [${status}] ${JSON.stringify(Object.fromEntries(Object.entries(state)
        .filter(([k]) => !['btType', 'featureStatus', 'inactive'].includes(k)))).slice(0, 400)}`;
    results[label] = ((measured.body?.console ?? '').trim() || 'no console') + detail;
    // Kept runs leave the feature in place so its error can be read off the row; measurements
    // would then see every case's bodies at once, so KEEP is for one case at a time (ONLY=).
    if (!KEEP) await drop(id);
  }

  console.log('\nresults:');
  for (const [k, v] of Object.entries(results)) console.log(`  ${k.padEnd(22)} ${v.replace(/\s+/g, ' ')}`);

  if (!KEEP) {
    // Tab deletion is /api/v6/elements, NOT a path under /documents — the wrong one answers
    // without deleting anything, so the status is checked rather than assumed.
    const removed = await page.evaluate(async (p) => {
      const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
      const r = await fetch(p, { method: 'DELETE', credentials: 'same-origin',
        headers: { accept: 'application/json', 'x-xsrf-token': raw.slice(raw.indexOf('=') + 1) } });
      return r.status;
    }, `/api/v6/elements/d/${DID}/w/${WID}/e/${PS_EID}`);
    console.log(`\n${removed < 300 ? 'deleted' : `FAILED (${removed}) to delete`} tab ${PS_EID}`);
  } else {
    console.log(`\nkept tab ${PS_EID}`);
  }
} finally {
  await browser.close();
}
