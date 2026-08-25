// net-probe.mjs — ask the kernel WHICH B-surface it will not build, by variation.
//
// `CANNOT_MAKE_BSPLINESURFACE` names neither the net nor the reason, and the three candidate
// causes - the net's degree, its shape, and its size against the kernel's own resolution - are
// not separable from a refusal alone. This offers the same control net several ways and reports
// which ones come back.
//
// It runs through the FeatureScript EVAL endpoint, which needs no Part Studio watcher: the
// "too many clients watching Part Studios" limit is a real resource and a diagnostic should not
// spend one. The endpoint takes a bare function expression and resolves the standard library
// only, so the net is passed in as literal numbers rather than imported from the sweep element.
//
//   node net-probe.mjs            the face 4 / segment 1 net the ARC fixture refuses
//   NET=path/to/net.json node net-probe.mjs
import { readFile } from 'node:fs/promises';
import { openSession, apiPost, DID, WID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID ?? 'f52300ae31a4fb83b720f044';

// Three stations by two rulings, straight off the ARC + TWO_AXIS run's grid dump for the patch
// that refuses: plane face 4, segment 1, t 0.0916 .. 0.1596.
const DEFAULT_NET = [
  [[-0.006888168067089449, -0.027119466030818593, -0.03273552816574627],
   [0.05805811635852155, -0.024630400789095192, 0.005880656070968837]],
  [[0.002640455118070647, -0.026926210687402856, -0.026380022959980477],
   [0.06756044463178816, -0.024314801869445655, 0.011727800299597586]],
  [[0.012139498253729689, -0.026686571719656778, -0.02031050919940444],
   [0.07704678528444428, -0.02390546219873725, 0.01720295588695845]],
];

const net = process.env.NET ? JSON.parse(await readFile(process.env.NET, 'utf8')) : DEFAULT_NET;

const centroid = [0, 1, 2].map((axis) =>
  net.flat().reduce((sum, p) => sum + p[axis], 0) / net.flat().length);
const scaled = net.map((row) => row.map((p) => p.map((v, axis) => centroid[axis] + 100 * (v - centroid[axis]))));
const corners = [net[0], net[net.length - 1]];

// The net the fit actually hands the kernel, which is NOT the data: a clamped quadratic through
// three points at parameters [0, u1, 1] puts its middle control point at
// (D1 - B0 D0 - B2 D2) / B1, and the overshoot that introduces is what has to be compared
// against the patch's own transverse width.
const interpolatedMiddle = (rows, u1) => {
  const b0 = (1 - u1) ** 2;
  const b1 = 2 * u1 * (1 - u1);
  const b2 = u1 ** 2;
  return [0, 1].map((column) => [0, 1, 2].map((axis) =>
    (rows[1][column][axis] - b0 * rows[0][column][axis] - b2 * rows[2][column][axis]) / b1));
};
const U1 = Number(process.env.U1 ?? 0.495746111);
const interpolated = [net[0], interpolatedMiddle(net, U1), net[2]];

// How far that middle control row strays from the data it interpolates, against how wide the
// patch is transverse to its own ruling - the ratio that decides whether the overshoot matters.
const sub = (a, b) => a.map((v, i) => v - b[i]);
const norm = (v) => Math.hypot(...v);
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const overshoot = Math.max(norm(sub(interpolated[1][0], net[1][0])), norm(sub(interpolated[1][1], net[1][1])));
const along = sub(net[2][0], net[0][0]);
const ruling = sub(net[0][1], net[0][0]);
const width = norm(cross(along, ruling)) / norm(ruling);
console.log(`patch: ruling ${norm(ruling).toFixed(6)} m, travel ${norm(along).toFixed(6)} m, ` +
  `transverse width ${width.toExponential(3)} m`);
console.log(`interpolation overshoot ${overshoot.toExponential(3)} m ` +
  `= ${(100 * overshoot / width).toFixed(1)}% of the width\n`);

const fsPoints = (rows) => `[${rows.map((row) =>
  `[${row.map((p) => `vector(${p[0]}, ${p[1]}, ${p[2]}) * meter`).join(', ')}]`).join(', ')}]`;

const variants = [
  { name: 'as measured, uDegree 2', rows: net, uDegree: 2, uKnots: '[0, 0, 0, 1, 1, 1]' },
  { name: 'as measured, uDegree 1', rows: net, uDegree: 1, uKnots: '[0, 0, 0.5, 1, 1]' },
  { name: 'corners only, uDegree 1', rows: corners, uDegree: 1, uKnots: '[0, 0, 1, 1]' },
  { name: 'scaled 100x, uDegree 2', rows: scaled, uDegree: 2, uKnots: '[0, 0, 0, 1, 1, 1]' },
  { name: 'INTERPOLATED, uDegree 2', rows: interpolated, uDegree: 2, uKnots: '[0, 0, 0, 1, 1, 1]' },
];

const script = (variant) => `
function(context is Context, queries)
{
    const rows = ${fsPoints(variant.rows)};
    var report = "";
    try
    {
        opCreateBSplineSurface(context, newId() + "probe", {
                    "bSplineSurface" : bSplineSurface({
                                "uDegree" : ${variant.uDegree},
                                "vDegree" : 1,
                                "isUPeriodic" : false,
                                "isVPeriodic" : false,
                                "controlPoints" : controlPointMatrix(rows),
                                "uKnots" : knotArray(${variant.uKnots}),
                                "vKnots" : knotArray([0, 0, 1, 1])
                            })
                });
        report = "accepted, " ~ size(evaluateQuery(context, qCreatedBy(newId() + "probe", EntityType.FACE))) ~ " face(s)";
    }
    catch (error)
    {
        report = "REFUSED " ~ toString(error);
    }
    return report;
}`;

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(6000);
  for (const variant of variants) {
    const res = await apiPost(page, `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/featurescript`,
      { script: script(variant), queries: {} });
    const body = res.body ?? {};
    const notices = (body.notices ?? []).map((n) => `[${n.level}] ${n.message}`).join(' | ');
    const value = body.result?.value ?? null;
    console.log(`${variant.name.padEnd(26)} -> ${value ?? notices ?? `status ${res.status}`}`);
  }
} finally {
  await browser.close();
}
