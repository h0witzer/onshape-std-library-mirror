FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/geometry.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0");
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");

import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs
// EXPORT import, not a plain one: MotionFrameSource is used as a dialog parameter type by the
// motion tester below, and an enum reached through a plain import is not exported into this
// element's namespace - the UI then refuses the parameter with "Enum used as parameter type
// must be exported". Same-document import, so only the PATH matters; Onshape rewrites the
// version to the tab's latest microversion on commit.
export import(path : "5c27bfb0b1dfa896edb18563", version : "88e4cd20e5be91dc37b59042");//solidSweepUtils.fs

/**
 * SOLID SWEEP - the test suite, one element (spec: docs/specs/SOLID_SWEEP_SPEC.md section 14).
 *
 * Every self test, live test, and fixture for solidSweepUtils.fs. Selection-free by
 * construction: each feature builds whatever geometry it needs, so any of them can be inserted
 * into an empty Part Studio, or executed by the MCP harness, without picking anything.
 *
 * This file is meant to be CYCLED. A test earns its place by defending an invariant the current
 * work can still break; once a layer is finished and its numbers are recorded in the spec, its
 * test either becomes a regression check for the layer above or it goes.
 *
 * Assembled by tools/consolidateSweepStack.py; edit this file directly from here on.
 */


// ============================= Constants and tolerances =============================

/**
 * Contact-loop sampling for the assembly test. This is THE knob of that test: the seam the knit
 * has to sew is the fitted patch's own boundary curve, so its distance from the true contact
 * curve - which is what this count buys down - is the gap the kernel is asked to absorb.
 */
const ASSEMBLY_LOOP_SAMPLE_BOUNDS = { (unitless) : [12, 61, 481] } as IntegerBoundSpec;

/**
 * Face extraction tolerance for the assembly test. The caps are copies of the EXACT kernel
 * tool while the patch is fitted against the APPROXIMATED one, so this tolerance is a floor
 * under the seam gap in a way it never was for a patch measured against its own extraction.
 */
const ASSEMBLY_EXTRACT_BOUNDS =
{
    (meter) : [1e-12, 1e-7, 1e-4],
    (millimeter) : 1e-4,
    (inch) : 4e-6
} as LengthBoundSpec;

/** The ellipsoid the step-7 live tests sweep: semi-axes in meters, axis along global X. */
const ELLIPSOID_SEMI_AXIAL = 0.05;

const ELLIPSOID_SEMI_RADIAL = 0.03;

/**
 * The two island motions, both translations with velocity (1, 0, wz(t)) over the bump fixture
 * islandFixtureSurface, whose z_u = 0.8 u(1-u) v(1-v) peaks at 0.05 at (0.5, 0.5). The contact
 * set is therefore the LEVEL SET z_u = wz(t), a closed loop for wz in (0, 0.05), and
 *     lambda = wz' + z_uu,   max |z_uu| on the loop = 0.2 sqrt(1 - 20 wz),
 * so lambda holds one sign exactly where |wz'| beats that.
 *
 * TWO POLES (the step-6 fixture): wz = 0.134 - 0.4t + 0.4t^2 dips below the peak on t in
 * (0.3, 0.7), so the island is born at t = 0.3 and dies at t = 0.7. |wz'| = |0.8t - 0.4| falls
 * to 0 at t = 0.5, so lambda spans both signs across t in (0.385, 0.615): the sweep is locally
 * self-intersecting there. That is not this fixture's accident - see emitIslandPatches.
 *
 * ONE POLE (the emission fixture): wz = 0.05 - 0.4t is born at t = 0 and the fit is CLIPPED at
 * t = 0.0375, where wz = 0.035 and max |z_uu| on the loop is 0.1095 - a fold margin of
 * (0.4 - 0.1095) / (0.4 + 0.1095) = 0.570 with lambda one-signed at -1 throughout. The island's
 * other end is not a pole at all: wz reaches 0 at t = 0.125, where the level set degenerates
 * onto the patch boundary, and nothing past the clip is fitted.
 */
const ISLAND_BUMP_VELOCITY_Z = [0.134, -0.066, 0.134];

const ISLAND_PEAK_HEIGHT_SLOPE = 0.05;

const ISLAND_CAP_SLOPE = 0.4;

const ISLAND_CAP_VELOCITY_Z = [0.05, -0.15, -0.35];

const ISLAND_CAP_T_END = 0.0375;

const ISLAND_CAP_TOLERANCE = 5e-4;

/** Geometry of the tube fixture: eight fundamental profile points, a [3, 11] u domain of
    period 8, and a total height of 0.1 m over v in [0, 1] - which, z being linear in v, is
    also z prime. */
const TUBE_FIXTURE_PROFILE_COUNT = 8;

const TUBE_FIXTURE_DOMAIN_START = 3;

const TUBE_FIXTURE_HEIGHT = 0.1;

/** The tube self test's certified target. The barrel's contact loop is a cubic B-spline profile
    tilted by the motion, so its q interpolation converges at h^4 off a coarse start: 2.2e-4 at
    q = 14, 2.2e-5 at q = 27, 3.8e-6 at q = 54 (measured independently). 5e-5 is the target the
    refinement loop clears in exactly one doubling, which is what the test wants to exercise. */
const TUBE_SELF_TEST_TOLERANCE = 5e-5;

/**
 * The ROTATING tube fixture (spec 7.9): the same barrel, swept by a motion whose rotation is
 * not identity - rotation about the barrel's own z axis by theta(t) plus a constant world
 * velocity with a component across that axis.
 *
 * Why those two ingredients together. For ANY one-parameter subgroup of the rigid motions the
 * pullback velocity A^T(A' p + b') is independent of t, so the contact set never moves and f_t
 * vanishes identically - a plain helix (constant angular speed along its own axis) is the
 * sliding case in disguise. Two things break the group structure: theta' VARIES with t, which
 * makes the rotation term K vary, and the translation has a component PERPENDICULAR to the
 * rotation axis, so beta = A^T b' turns under A. Measured contact-set travel goes from 5e-4 for
 * the helix to 0.168 here.
 *
 * Why rotation about z stays non-degenerate: K carries the factor d/du (cx^2 + cy^2), which is
 * identically zero on a circle - that IS the sliding case - and vanishes only at the four axis
 * points of the barrel's ELLIPSE.
 *
 * Why the contact curve is still CLOSED FORM. With w = M S + beta, M = A^T A', beta = A^T b',
 * the v-degree of f is set by M's THIRD ROW: the term g' P (M_20 g cx + M_21 g cy) is cubic in
 * v. Holding the z column of A at zhat makes that row exactly zero - zhat . x' = zhat . y' = 0
 * for columns that stay in the xy plane - with no rigidity assumption anywhere. Then, writing
 * z' for the (constant) height derivative and P = cx' cy - cy' cx,
 *
 *     f / g = K g + L + g' P beta_z
 *     K = z' (m00 cy' cx + m01 cy' cy - m10 cx' cx - m11 cx' cy)
 *     L = z' (cy' beta_x - cx' beta_y)
 *
 * and since g = 0.5 + v - v^2 with g' = 1 - 2v, f = 0 is the QUADRATIC
 *
 *     -K v^2 + (K - 2R) v + (0.5 K + L + R) = 0,   R = P beta_z
 *
 * whose K -> 0 limit is exactly the straight-translation form tubeFixtureExactV solves. Note
 * f = <A N, A' S + b'> equals <N, A^T A' S + A^T b'> by transpose alone, needing no
 * orthogonality, so the closed form matches the shipped f whatever the stored rotation's drift.
 *
 * On drift: no non-constant polynomial curve lies in SO(3) - p^2 + q^2 == 1 forces p and q
 * constant by a leading-term argument - so a non-rational B-spline rotation ALWAYS drifts, which
 * is why the motion layer certifies orthogonalityDrift instead of assuming exactness. A drifting
 * rotation is therefore the realistic input, not a compromise. Each column here is a cubic
 * Hermite of cos/sin per span over eight spans, stored in Bezier form (interior knot
 * multiplicity 3), which holds the drift near 1.2e-6 - forty times under the fit tolerance - and
 * keeps every motion spline at degree 3, the degree the motion module already produces.
 */
const ROTATING_TUBE_TOTAL_TURN = 25 * PI / 180;

const ROTATING_TUBE_LINEAR_SHARE = 0.35;

const ROTATING_TUBE_SPANS = 8;

const ROTATING_TUBE_CROSS_SPEED = 0.04;

const ROTATING_TUBE_AXIAL_SPEED = 0.15;

const ROTATING_TUBE_DRIFT_LIMIT = 3e-6;

const ROTATING_TUBE_TOLERANCE = 1e-4;

/**
 * The merge fixture (spec 7.1 strip decomposition). S = (u, v, 0.1(u^2/2 + 2u(v - 1/2)^2)) on the
 * unit square, translating with velocity (1, 0, 0.1 w(t)), w(t) = 1.2 - 0.4t. Since the surface
 * normal is (-z_u, -z_v, 1) and the velocity's y component is zero, the envelope function is
 * exactly f = 0.1(w(t) - u - 2(v - 1/2)^2): every section is the parabola
 * u = w(t) - 2(v - 1/2)^2, peaking at u = w(t) on the v = 1/2 meridian.
 *
 * That is the whole point of the fixture. While w > 1 the peak is outside the domain, so the
 * u = 1 wall cuts the section into TWO arcs; at w = 1 the section is tangent to the wall; below
 * it there is one arc. w(1/2) = 1, so the component's boundary carries two cap arcs at t = 0 and
 * one at t = 1 - six alternations - and it must decompose into two leg strips and one trunk
 * strip meeting at t = 1/2.
 */
const MERGE_FIXTURE_LEVEL_START = 1.2;

const MERGE_FIXTURE_LEVEL_RATE = 0.4;

const MERGE_FIXTURE_PROFILE = 2;

const MERGE_FIXTURE_SCALE = 0.1;

const MERGE_FIXTURE_SPLIT_TIME = 0.5;

/** What each strip of the merge fixture certifies at, at the fixed grids the strip fit self test
    uses, with a 2x margin over the independently recomputed deviation there (leg 5.5e-4 at 8x8
    uniform, trunk 1.3e-4 at 6x16). These are not tolerances the fit is being asked to reach by
    refining - see the strip fit self test on why that loop must stay off for a merging strip. */
const LEG_STRIP_DEVIATION_LIMIT = 1.2e-3;

const TRUNK_STRIP_DEVIATION_LIMIT = 3e-4;


// ============================= Envelope function layer (spec 6.1-6.2) =============================

/** A constant 3D spline (every control point equal) on the given clamped knot vector. */
function constantColumnSpline(value is Vector, knots is array) returns map
{
    const degree = 3;
    const pointCount = size(knots) - degree - 1;
    var controlPoints = makeArray(pointCount);
    for (var pointIndex = 0; pointIndex < pointCount; pointIndex += 1)
    {
        controlPoints[pointIndex] = value;
    }
    return { "degree" : degree, "knots" : knots, "controlPoints" : controlPoints, "isRational" : false };
}


// ============================= Funnel solver, masks and certified census (spec 6.3-6.4, 6.7-6.8) =============================

/** Prints one line per refinement pass, so a run records what the certificate cost. */
function printCensusPasses(name is string, certifiedCensus is map)
{
    for (var pass in certifiedCensus.passes)
    {
        println("[FUNNEL CERTIFIED CENSUS] " ~ name ~ " " ~ pass.uNodesPerPatch ~ "/" ~
            pass.vNodesPerPatch ~ "/" ~ pass.tNodesPerSpan ~ ": components " ~ pass.componentCount ~
            ", mixed " ~ pass.mixedCellCount ~ ", live " ~ pass.liveCellCount ~ ", unresolved " ~
            pass.unresolvedRegionCount ~ ", contradictions " ~ pass.contradictions ~ ", stable " ~
            pass.stable);
    }
    println("[FUNNEL CERTIFIED CENSUS] " ~ name ~ " -> certified " ~ certifiedCensus.certified ~
        " after " ~ certifiedCensus.refinements ~ " refinement(s), " ~
        size(certifiedCensus.components) ~ " component(s); isolation screens " ~
        certifiedCensus.coverage.screenedCount ~ ", dead " ~ certifiedCensus.coverage.deadCount ~
        ", leaves " ~ certifiedCensus.coverage.liveLeafCount);
}

/**
 * The node-contact fixture (spec 6.7): S(u, v) = (0.2u, 0.15v, 0.1u^2). Degrees (2, 2) on one
 * Bezier patch reproduce it exactly, since x and y are linear and z is quadratic in u alone.
 * S_u = (0.2, 0, 0.2u), S_v = (0, 0.15, 0), N = (-0.03u, 0, 0.03), so under the constant
 * velocity (1, 0, 0.5) the envelope function is exactly f = 0.03 (0.5 - u): the contact set is
 * the plane u = 0.5, which every odd-node grid puts ON a node line. The same surface is the
 * sheet the trim-loop test builds in the kernel, so the two tests read one geometry.
 */
function nodeContactFixtureSurface() returns map
{
    const xCoefficients = [0, 0.1, 0.2];
    const yCoefficients = [0, 0.075, 0.15];
    const zCoefficients = [0, 0, 0.1];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(xCoefficients[i], yCoefficients[j], zCoefficients[i]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The seam fixture: z = 0.8 ((u - 0.5)^3/3 + 1/24) v(1 - v), so that
 * z_u = 0.8 (u - 0.5)^2 v(1 - v) peaks against BOTH u edges - one contact lobe per edge under
 * constant w = (1, 0, 0.02), the same physical band on a closed face.
 */
function seamFixtureSurface() returns map
{
    const uCoefficients = [0, 1 / 12, 0, 1 / 12];
    const vCoefficients = [0, 0.5, 0];
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville3[i], greville2[j], 0.8 * uCoefficients[i] * vCoefficients[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The rotation fixture: a parabolic cylinder shifted off both parameter origins,
 * S = (x, y, 0.3 x^2) with x = u + 1 and y = v + 0.3, degrees (2, 1). Under the harness's
 * first-order rotation about +Z at unit rate - where `b'` is EXACTLY zero - the envelope
 * function is exactly
 *
 *     f = 0.6 x (y - t x)
 *
 * so the contact set is the single line v = t(u + 1) - 0.3, which enters the unit domain at
 * t = 0.15 and is still inside it at t = 1. Both offsets are load-bearing: x is kept away from
 * zero so the zero set is one line rather than a line plus the whole u = 0 edge, and y is offset
 * by 0.3 so the birth t does not land on a grid node, where the birth cell's verdict would come
 * down to the sign of a rounding error.
 */
function rotationFixtureSurface() returns map
{
    const xControls = [1, 1.5, 2];
    const yControls = [0.3, 1.3];
    const zControls = [0.3, 0.6, 1.2];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(2);
        for (var j = 0; j < 2; j += 1)
        {
            row[j] = vector(xControls[i], yControls[j], zControls[i]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 1,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/** A flat plane z = 0 at degrees (3, 2) - the sliding-audit fixture. */
function planeFixtureSurface() returns map
{
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville3[i], greville2[j], 0);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * A trim loop that WINDS the u seam: v = base + amplitude sin(2 pi u), sampled over one full
 * period with both ends included. Its first and last samples are the same point one period
 * apart, which is the stored form the cyclic mask expects of a winding loop.
 */
function sinusoidalSeamLoop(base is number, amplitude is number, segmentCount is number) returns array
{
    var samples = makeArray(segmentCount + 1, vector(0, base));
    for (var index = 0; index <= segmentCount; index += 1)
    {
        const u = index / segmentCount;
        samples[index] = vector(u, base + amplitude * sin(2 * PI * u * radian));
    }
    return samples;
}

/**
 * A small circular hole centred on the seam, UNWRAPPED so its u runs from minus to plus the
 * radius. Returned as a bare point array - the shape the census reads as a closed loop of
 * winding zero.
 */
function seamStraddlingHole(radius is number, sampleCount is number) returns array
{
    var samples = makeArray(sampleCount, vector(0, 0.5));
    for (var index = 0; index < sampleCount; index += 1)
    {
        const angle = 2 * PI * index / sampleCount * radian;
        samples[index] = vector(radius * cos(angle), 0.5 + radius * sin(angle));
    }
    return samples;
}

/**
 * The pole fixture: S(u, v) = v (u, 1, c(u)) with c = 0.3 u^2 - 0.2 u^3, degrees (3, 1). The
 * v = 0 control row is collapsed onto the origin, so S_u vanishes along it and the normal with
 * it - f is identically zero on that whole boundary, byte-exactly, because the control-row
 * differences the coefficient nets are built from are exactly zero.
 *
 * Under velocity (a(t), 0, 1) the envelope function is exactly f = v (1 - a(t) c'(u)) with
 * c' = 0.6 u (1 - u), so the contact set is two sheets at u = 0.5 +/- sqrt(0.25 - 1 / (0.6 a)) -
 * separate everywhere except along v = 0.
 */
function poleFixtureSurface() returns map
{
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const heightCoefficients = [0, 0, 0.1, 0.1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        net[i] = [vector(0, 0, 0), vector(greville3[i], 1, heightCoefficients[i])];
    }
    return {
            "uDegree" : 3, "vDegree" : 1,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}


// ============================= Extraction, caps, knit, assembly (spec 5 and 9) =============================

/**
 * Round-trip check of the point inversion on one stripped surface: evaluate three interior
 * knot-domain UVs, invert each with the seedless multi-seed entry, and return the worst 3D
 * residual and iteration count: { worstResidual {number}, worstIterations {number} }.
 */
function inversionRoundTripWorstCase(strippedSurface is map) returns map
{
    const testFractions = [vector(0.20, 0.40), vector(0.60, 0.70), vector(0.85, 0.15)];
    const domain = surfaceKnotDomain(strippedSurface);
    var worstResidual = 0;
    var worstIterations = 0;
    for (var fraction in testFractions)
    {
        const u = domain.uStart + (domain.uEnd - domain.uStart) * fraction[0];
        const v = domain.vStart + (domain.vEnd - domain.vStart) * fraction[1];
        const targetPoint = evaluateBSplineSurfacePoint(strippedSurface, u, v);
        const inversion = invertPointOnSurfaceFromGrid(strippedSurface, targetPoint, 7);
        if (inversion.residual > worstResidual)
        {
            worstResidual = inversion.residual;
        }
        if (inversion.iterations > worstIterations)
        {
            worstIterations = inversion.iterations;
        }
    }
    return { "worstResidual" : worstResidual, "worstIterations" : worstIterations };
}

/** A degree-1 2D trim curve between two uv points. */
function uvLineCurve(startPoint is Vector, endPoint is Vector) returns map
{
    return {
            "degree" : 1, "dimension" : 2, "isRational" : false, "isPeriodic" : false,
            "controlPoints" : [startPoint, endPoint], "knots" : [0, 0, 1, 1]
        };
}

/**
 * An exact circle in uv as a rational quadratic in four quarter spans - the standard NURBS
 * circle, with corner weights of sqrt(1/2).
 */
function uvCircleCurve(centre is Vector, radius is number) returns map
{
    const cornerWeight = sqrt(0.5);
    const ring = [vector(1, 0), vector(1, 1), vector(0, 1), vector(-1, 1), vector(-1, 0),
            vector(-1, -1), vector(0, -1), vector(1, -1), vector(1, 0)];
    var controlPoints = makeArray(9, centre);
    for (var index = 0; index < 9; index += 1)
    {
        controlPoints[index] = centre + radius * ring[index];
    }
    return {
            "degree" : 2, "dimension" : 2, "isRational" : true, "isPeriodic" : false,
            "controlPoints" : controlPoints,
            "weights" : [1, cornerWeight, 1, cornerWeight, 1, cornerWeight, 1, cornerWeight, 1],
            "knots" : [0, 0, 0, 0.25, 0.25, 0.5, 0.5, 0.75, 0.75, 1, 1, 1]
        };
}

/** A bilinear unit patch - just enough surface for a record to carry a uv domain of [0, 1]^2. */
function flatUnitDomainSurface() returns map
{
    return {
            "uDegree" : 1, "vDegree" : 1,
            "uKnots" : [0, 0, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : [[vector(0, 0, 0), vector(0, 1, 0)], [vector(1, 0, 0), vector(1, 1, 0)]],
            "isRational" : false, "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/** A v-constant uv sample run from one u to another - a trim arc on a closed face. */
function uvArcSamples(v is number, uFrom is number, uTo is number, segmentCount is number) returns array
{
    var samples = makeArray(segmentCount + 1, vector(uFrom, v));
    for (var index = 0; index <= segmentCount; index += 1)
    {
        samples[index] = vector(uFrom + (uTo - uFrom) * index / segmentCount, v);
    }
    return samples;
}

/**
 * Half of a small circular hole centred exactly on the seam, with its u values FOLDED into
 * [0, 1) the way an inverted pcurve arrives. `side` picks the upper or lower half.
 */
function foldedHoleSamples(radius is number, side is number, segmentCount is number) returns array
{
    var samples = makeArray(segmentCount + 1, vector(0, 0.5));
    for (var index = 0; index <= segmentCount; index += 1)
    {
        const centred = -radius + 2 * radius * index / segmentCount;
        const height = sqrt(max(radius * radius - centred * centred, 0));
        samples[index] = vector(positiveModulo(centred, 1), 0.5 + side * height);
    }
    return samples;
}


// ============================= Fitting and certification (spec 7) =============================

/**
 * Build the step-7 tool - half an ellipse revolved about the global X axis - and extract it
 * ready for the tube path: one smooth face, non-rational, with the CIRCUMFERENTIAL direction
 * transposed into u whichever way the kernel chose to parameterize the revolve (spec 7.6).
 * Returns { body {Query}, record, surface, domain }.
 */
function ellipsoidToolFixture(context is Context, id is Id, semiAxial is number, semiRadial is number,
    extractTolerance is number) returns map
{
    // The FIVE-argument extraction, so the ellipsoid is recognized for what it is: a REVOLVED face
    // whose generator is one untrimmed iso-curve away. Called with three arguments this fixture
    // handed back only an approximated net, and every consumer then took the marched path because
    // it was the only path a net can take - which is why the section 9.4 assembly test read 18.4 s
    // on a face whose contact curve is closed form.
    const sketchId = id + "sketch";
    const profileSketch = newSketchOnPlane(context, sketchId, {
                "sketchPlane" : plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0))
            });
    skEllipse(profileSketch, "profile", {
                "center" : vector(0, 0) * meter,
                "majorRadius" : semiAxial * meter,
                "minorRadius" : semiRadial * meter
            });
    skLineSegment(profileSketch, "axisCut", {
                "start" : vector(-2 * semiAxial, 0) * meter,
                "end" : vector(2 * semiAxial, 0) * meter
            });
    skSolve(profileSketch);
    opRevolve(context, id + "revolve", {
                "entities" : qNthElement(qSketchRegion(sketchId), 0),
                "axis" : line(vector(0, 0, 0) * meter, vector(1, 0, 0)),
                "angleForward" : 360 * degree
            });
    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

    const body = qCreatedBy(id + "revolve", EntityType.BODY);
    const nextId = getUnstableIncrementingId(id + "extract");
    const analyticRecords = extractToolFaceRecords(context, body, extractTolerance, false, nextId);
    if (size(analyticRecords) != 1 || analyticRecords[0].analyticFrame == undefined)
    {
        throw "the ellipsoid tool did not recognize as one REVOLVED face: " ~
            summarizeFaceRecords(analyticRecords);
    }
    const frame = analyticFrameForFaceRecord(analyticRecords[0]);

    // The net as well, for the consumers that genuinely need one - co-edge pcurves, trim loops, and
    // the older fixtures whose recorded numbers are measured on it.
    const records = extractToolFaceRecords(context, body, extractTolerance);
    if (size(records) != 1 || records[0].spline == undefined)
    {
        throw "the ellipsoid tool did not extract as one spline-bearing face.";
    }
    const record = records[0];
    // transposeSurface normalizes on the way in, which re-attaches a unit weight grid and
    // re-flags the net rational; dropping them again keeps the coefficient path's guarantee.
    const surface = record.spline.isVPeriodic == true ?
        dropUniformWeights(transposeSurface(record.spline)) : record.spline;
    const knotDomain = surfaceKnotDomain(surface);
    return {
            "body" : body,
            "record" : record,
            "surface" : surface,
            "frame" : frame,
            "domain" : { "uMin" : knotDomain.uStart, "uMax" : knotDomain.uEnd,
                "vMin" : knotDomain.vStart, "vMax" : knotDomain.vEnd }
        };
}

/**
 * The four checks the strip fit test makes per strip, and their console lines. `seamAtEnd` says
 * which t end of the strip is the seam - the merging strips end there, the merged one starts
 * there - and `deviationLimit` is the absolute ceiling for this strip at this grid.
 */
function reportStripFit(tally is map, testName is string, label is string, fit is map,
    seamTime is number, seamAtEnd is boolean, deviationLimit is number) returns map
{
    if (fit.failed)
    {
        return checkThat(tally, false, "the " ~ label ~ " strip fit failed: " ~ fit.reason);
    }
    println("[" ~ testName ~ "] " ~ label ~ ": " ~ fit.stationCount ~ "x" ~ fit.qCount ~
        " grid in " ~ fit.refinementRounds ~ " rounds, deviation " ~ fit.worstDeviation ~
        " (q " ~ fit.worstQDeviation ~ ", t " ~ fit.worstTDeviation ~ "), removal " ~
        fit.removalDeviation ~ ", certified " ~ fit.certifiedBound ~ ", budgetHit " ~
        fit.budgetHit ~ ", section residual " ~ fit.worstSectionResidual);
    println("[" ~ testName ~ "] " ~ label ~ " orientation: lambda " ~ fit.orientation.lambdaSign ~
        " consistent " ~ fit.orientation.lambdaSignConsistent ~ ", fold margin " ~
        fit.orientation.worstFoldMargin ~ ", difference " ~ fit.orientation.differenceAgreements ~
        "/" ~ fit.orientation.differenceChecked ~ " agree, q reversed " ~ fit.qReversed);

    var result = checkThat(tally, fit.refinementRounds == 1 && !fit.budgetHit,
        "the " ~ label ~ " strip did not fit its fixed grid once and certify there: rounds " ~
        fit.refinementRounds ~ ", budgetHit " ~ fit.budgetHit ~ ".");
    result = checkWithin(result, fit.certifiedBound, deviationLimit,
        "the " ~ label ~ " strip's certified bound at its fixed grid");

    // Scale-free from here: the patch has to BE the envelope, and its seam edge has to be on the
    // shared contact arc, to whatever accuracy the module certified for the surface it returned.
    const accuracy = 3 * fit.certifiedBound + 1e-6;
    const membership = worstMergeFixtureResidual(fit.surface, 6);
    println("[" ~ testName ~ "] " ~ label ~ " closed-form envelope membership: " ~ membership);
    result = checkWithin(result, membership, accuracy,
        "the " ~ label ~ " patch's closed-form envelope membership against its own deviation");

    // Both strips resample ONE marched arc at the seam (spec 2.3), so both patch edges interpolate
    // samples of the same curve - and the way to see that without fitting the neighbour is to
    // measure each edge against the arc itself, which this fixture knows in closed form.
    const domain = fitSurfaceKnotDomain(fit.surface);
    const seamU = seamAtEnd ? domain.uMax : domain.uMin;
    var worstSeamGap = 0;
    for (var index = 0; index <= 6; index += 1)
    {
        worstSeamGap = max(worstSeamGap, mergeFixtureArcDistance(
                evaluateBSplineSurfacePoint(fit.surface, seamU,
                    domain.vMin + (domain.vMax - domain.vMin) * index / 6), seamTime));
    }
    println("[" ~ testName ~ "] " ~ label ~ " worst seam-edge gap to the exact contact arc at t = " ~
        seamTime ~ ": " ~ worstSeamGap);
    return checkWithin(result, worstSeamGap, accuracy,
        "the " ~ label ~ " patch's seam edge against the exact contact arc");
}

/**
 * The tube fixture: a closed BARREL, S(u, v) = (g(v) cx(u), g(v) cy(u), z(v)) - a periodic
 * cubic profile c(u) of eight fundamental control points on a 60 x 45 mm ellipse, scaled
 * radially by g(v) = 0.5 + v - v^2 and lifted by z(v) = 0.1 v. A product of a u basis and a v
 * basis is a tensor product, so this is an exact non-rational B-spline surface, u periodic in
 * the wrap-padded convention, and it has NO poles.
 *
 * Its contact curve under a translation is CLOSED FORM. With N = S_u x S_v,
 *     N = g * ( z' cy', -z' cx', g' (cx' cy - cy' cx) ),
 * so for velocity w, f = <N, w> = g * [ z' (cy' wx - cx' wy) + g'(v) A(u) wz ], where
 * A = cx' cy - cy' cx. Since g' = 1 - 2v is LINEAR, f = 0 solves for v outright:
 *     v(u, t) = 0.5 * (1 + z' B(u) / (A(u) wz)),   B = cy' wx - cx' wy.
 * The velocities the tube self test uses hold v inside (0.26, 0.74) across t in [0, 1], so
 * every section is a graph over u: it wraps the seam exactly once and never nears v's boundary.
 */
function tubeFixtureSurface() returns map
{
    const profile = tubeFixtureProfileCurve();
    const radiusScales = [0.5, 1.0, 0.5];
    const heights = [0, 0.05, TUBE_FIXTURE_HEIGHT];
    const rowCount = size(profile.controlPoints);
    var controlPoints = makeArray(rowCount);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        var row = makeArray(3);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            row[columnIndex] = vector(radiusScales[columnIndex] * profile.controlPoints[rowIndex][0],
                radiusScales[columnIndex] * profile.controlPoints[rowIndex][1], heights[columnIndex]);
        }
        controlPoints[rowIndex] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2, "isRational" : false,
            "isUPeriodic" : true, "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : profile.knots,
            "vKnots" : [0, 0, 0, 1, 1, 1]
        };
}

/**
 * The tube fixture's periodic cubic profile, wrap-padded: eight fundamental control points on a
 * 60 x 45 mm ellipse repeated to n + degree, and n + 2*degree + 1 unit-spaced knots, which puts
 * the domain at [3, 11] with period 8.
 */
function tubeFixtureProfileCurve() returns map
{
    const storedCount = TUBE_FIXTURE_PROFILE_COUNT + 3;
    var controlPoints = makeArray(storedCount);
    for (var index = 0; index < storedCount; index += 1)
    {
        const angle = 360 * degree * (index % TUBE_FIXTURE_PROFILE_COUNT) / TUBE_FIXTURE_PROFILE_COUNT;
        controlPoints[index] = vector(0.06 * cos(angle), 0.045 * sin(angle));
    }
    var knots = makeArray(TUBE_FIXTURE_PROFILE_COUNT + 7);
    for (var index = 0; index < TUBE_FIXTURE_PROFILE_COUNT + 7; index += 1)
    {
        knots[index] = index;
    }
    return { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : controlPoints };
}

/** The tube fixture's closed-form contact v at parameter u (wrapped) and time t. */
function tubeFixtureExactV(strippedMotion is map, u is number, t is number) returns number
{
    const profile = tubeFixtureProfileCurve();
    const wrapped = u - floor((u - TUBE_FIXTURE_DOMAIN_START) / TUBE_FIXTURE_PROFILE_COUNT) *
        TUBE_FIXTURE_PROFILE_COUNT;
    const derivatives = evaluateBSplineCurveDerivatives(profile, wrapped, 1);
    const c = derivatives[0];
    const cPrime = derivatives[1];
    const velocity = evaluateMotionSample(strippedMotion, t).translationDerivative;
    const A = cPrime[0] * c[1] - cPrime[1] * c[0];
    const B = cPrime[1] * velocity[0] - cPrime[0] * velocity[1];
    return 0.5 * (1 + TUBE_FIXTURE_HEIGHT * B / (A * velocity[2]));
}

/** The rotation angle at t, in radians as a plain number. */
function rotatingTubeTurn(t is number) returns number
{
    return ROTATING_TUBE_TOTAL_TURN *
        (ROTATING_TUBE_LINEAR_SHARE * t + (1 - ROTATING_TUBE_LINEAR_SHARE) * t * t);
}

/** Its t derivative - the angular speed, which is what makes K vary. */
function rotatingTubeTurnRate(t is number) returns number
{
    return ROTATING_TUBE_TOTAL_TURN *
        (ROTATING_TUBE_LINEAR_SHARE + 2 * (1 - ROTATING_TUBE_LINEAR_SHARE) * t);
}

/**
 * The rotating fixture's motion: columnX = (cos theta, sin theta, 0), columnY = (-sin, cos, 0),
 * columnZ = zhat, each a degree-3 Bezier-form spline over ROTATING_TUBE_SPANS spans, and the
 * translation the single-span cubic whose derivative is exactly the world velocity.
 */
function rotatingTubeMotion() returns map
{
    const controlCount = 3 * ROTATING_TUBE_SPANS + 1;
    var xControls = makeArray(controlCount, vector(0, 0, 0));
    var yControls = makeArray(controlCount, vector(0, 0, 0));
    var zControls = makeArray(controlCount, vector(0, 0, 1));
    for (var span = 0; span < ROTATING_TUBE_SPANS; span += 1)
    {
        const tStart = span / ROTATING_TUBE_SPANS;
        const tEnd = (span + 1) / ROTATING_TUBE_SPANS;
        const width = tEnd - tStart;
        const angleStart = rotatingTubeTurn(tStart);
        const angleEnd = rotatingTubeTurn(tEnd);
        const cosStart = cos(angleStart * radian);
        const sinStart = sin(angleStart * radian);
        const cosEnd = cos(angleEnd * radian);
        const sinEnd = sin(angleEnd * radian);
        // Hermite cubic in Bezier form: [f0, f0 + d0/3, f1 - d1/3, f1], with d the derivative
        // with respect to the span-local parameter.
        const rateStart = rotatingTubeTurnRate(tStart) * width;
        const rateEnd = rotatingTubeTurnRate(tEnd) * width;
        const cosSegment = [cosStart, cosStart - sinStart * rateStart / 3,
                cosEnd + sinEnd * rateEnd / 3, cosEnd];
        const sinSegment = [sinStart, sinStart + cosStart * rateStart / 3,
                sinEnd - cosEnd * rateEnd / 3, sinEnd];
        for (var pointIndex = 0; pointIndex < 4; pointIndex += 1)
        {
            xControls[3 * span + pointIndex] = vector(cosSegment[pointIndex], sinSegment[pointIndex], 0);
            yControls[3 * span + pointIndex] = vector(-sinSegment[pointIndex], cosSegment[pointIndex], 0);
        }
    }
    var knots = makeArray(controlCount + 4, 1);
    for (var index = 0; index < 4; index += 1)
    {
        knots[index] = 0;
    }
    for (var span = 1; span < ROTATING_TUBE_SPANS; span += 1)
    {
        for (var repeat = 0; repeat < 3; repeat += 1)
        {
            knots[1 + 3 * span + repeat] = span / ROTATING_TUBE_SPANS;
        }
    }
    const translation = constantVelocityTranslationMotion(
        vector(ROTATING_TUBE_CROSS_SPEED, 0, ROTATING_TUBE_AXIAL_SPEED)).translation;
    return {
            "columnX" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : xControls },
            "columnY" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : yControls },
            "columnZ" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : zControls },
            "translation" : translation
        };
}

/**
 * The pullback quantities the closed form needs, read off the STORED splines rather than an
 * idealised rotation: the four upper-left entries of M = A^T A', the three of beta = A^T b',
 * and M's third row so a test can assert it really is zero.
 */
function rotatingTubePullback(strippedMotion is map, t is number) returns map
{
    const xDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnX, t, 1);
    const yDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnY, t, 1);
    const zDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnZ, t, 1);
    const translationDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.translation, t, 1);
    const velocity = translationDerivatives[1];
    return {
            "m00" : dot(xDerivatives[0], xDerivatives[1]),
            "m01" : dot(xDerivatives[0], yDerivatives[1]),
            "m10" : dot(yDerivatives[0], xDerivatives[1]),
            "m11" : dot(yDerivatives[0], yDerivatives[1]),
            "thirdRow" : [dot(zDerivatives[0], xDerivatives[1]),
                    dot(zDerivatives[0], yDerivatives[1]),
                    dot(zDerivatives[0], zDerivatives[1])],
            "betaX" : dot(xDerivatives[0], velocity),
            "betaY" : dot(yDerivatives[0], velocity),
            "betaZ" : dot(zDerivatives[0], velocity),
            "columns" : [xDerivatives[0], yDerivatives[0], zDerivatives[0]]
        };
}

/**
 * The rotating fixture's closed-form contact v at parameter u (wrapped) and time t: the root of
 * -K v^2 + (K - 2R) v + (0.5 K + L + R) nearer the mid-surface, with the linear branch taken at
 * the four u where K vanishes.
 */
function rotatingTubeExactV(strippedMotion is map, u is number, t is number) returns number
{
    const profile = tubeFixtureProfileCurve();
    const wrapped = u - floor((u - TUBE_FIXTURE_DOMAIN_START) / TUBE_FIXTURE_PROFILE_COUNT) *
        TUBE_FIXTURE_PROFILE_COUNT;
    const derivatives = evaluateBSplineCurveDerivatives(profile, wrapped, 1);
    const c = derivatives[0];
    const cPrime = derivatives[1];
    const pullback = rotatingTubePullback(strippedMotion, t);
    const P = cPrime[0] * c[1] - cPrime[1] * c[0];
    const K = TUBE_FIXTURE_HEIGHT * (pullback.m00 * cPrime[1] * c[0] + pullback.m01 * cPrime[1] * c[1] -
            pullback.m10 * cPrime[0] * c[0] - pullback.m11 * cPrime[0] * c[1]);
    const L = TUBE_FIXTURE_HEIGHT * (cPrime[1] * pullback.betaX - cPrime[0] * pullback.betaY);
    const R = P * pullback.betaZ;
    const quadratic = -K;
    const linear = K - 2 * R;
    const constant = 0.5 * K + L + R;
    // Stable root pair: q = -(b + sign(b) sqrt(disc)) / 2, whose roots are c/q and q/a. Written
    // the textbook way, the root near -c/b loses itself to cancellation at the four u where K
    // vanishes (the ellipse's axis points, where the quadratic coefficient falls to 1e-12 while
    // the linear one holds at 5e-4) - measured worst |f| 5.2e-12 naive against 1.2e-19 here.
    // c/q also covers the K = 0 case outright, so no separate linear branch is needed.
    const root = sqrt(linear * linear - 4 * quadratic * constant);
    const q = -0.5 * (linear + (linear >= 0 ? root : -root));
    if (q == 0)
    {
        return quadratic == 0 ? 0.5 : -linear / quadratic;
    }
    const nearRoot = constant / q;
    if (quadratic == 0)
    {
        return nearRoot;
    }
    const farRoot = q / quadratic;
    return abs(nearRoot - 0.5) <= abs(farRoot - 0.5) ? nearRoot : farRoot;
}

/**
 * The curved fixture: S = (u, v, 0.15u^2 + 0.05uv^2), degrees (2, 2). Under velocity
 * (1, 0, w(t)) the envelope function is f = w(t) - 0.3u - 0.05v^2, so the sections are the
 * moving parabolas u = (w(t) - 0.05v^2) / 0.3.
 */
function curvedFixtureSurface() returns map
{
    const uSquared = [0, 0, 1];
    const uLinear = [0, 0.5, 1];
    const vSquared = [0, 0, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville2[i], greville2[j], 0.15 * uSquared[i] + 0.05 * uLinear[i] * vSquared[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The curved fixture's analytic boundary branches at v = 0 and v = 1, sampled the way strip
 * marching hands branches to the fit: u(t) = (w(t) - 0.05 v^2) / 0.3 with
 * w(t) = 0.06 + 0.18t - 0.18t^2.
 */
function curvedFixtureBranchAnchor(vEdge is number) returns map
{
    const sampleCount = 41;
    var tSamples = makeArray(sampleCount);
    var uvSamples = makeArray(sampleCount);
    for (var index = 0; index < sampleCount; index += 1)
    {
        const t = index / (sampleCount - 1);
        const w = 0.06 + 0.18 * t - 0.18 * t ^ 2;
        tSamples[index] = t;
        uvSamples[index] = [(w - 0.05 * vEdge ^ 2) / 0.3, vEdge];
    }
    return { "anchorKind" : "branch", "tSamples" : tSamples, "uvSamples" : uvSamples };
}

/**
 * Analytic envelope-membership residual for the curved fixture: given a fitted point
 * (x, y, z), the tool preimage satisfies v = y and 0.3(x - t) + 0.05y^2 - w(t) = 0 (monotone
 * in t, Newton), and the residual is the z mismatch against S + the translation
 * W(t) = 0.06t + 0.09t^2 - 0.06t^3.
 */
function worstCurvedFixtureResidual(fitSurface is map, samplesPerDirection is number) returns number
{
    const domain = fitSurfaceKnotDomain(fitSurface);
    var worst = 0;
    for (var i = 1; i < samplesPerDirection; i += 1)
    {
        for (var j = 1; j < samplesPerDirection; j += 1)
        {
            const uu = domain.uMin + (domain.uMax - domain.uMin) * i / samplesPerDirection;
            const vv = domain.vMin + (domain.vMax - domain.vMin) * j / samplesPerDirection;
            const fitted = evaluateBSplineSurfacePoint(fitSurface, uu, vv);
            const y = fitted[1];
            var t = 0.5;
            for (var iteration = 0; iteration < 30; iteration += 1)
            {
                const g = 0.3 * (fitted[0] - t) + 0.05 * y ^ 2 - (0.06 + 0.18 * t - 0.18 * t ^ 2);
                const gPrime = -0.3 - (0.18 - 0.36 * t);
                t = t - g / gPrime;
            }
            const toolU = fitted[0] - t;
            const exactZ = 0.15 * toolU ^ 2 + 0.05 * toolU * y ^ 2 + 0.06 * t + 0.09 * t ^ 2 - 0.06 * t ^ 3;
            worst = max(worst, abs(fitted[2] - exactZ));
        }
    }
    return worst;
}

/**
 * Analytic envelope-membership residual for the island fixture: an envelope point makes the
 * swept family's height mismatch h(u) = z(u, v) + Wz(x - u) - zFitted have a DOUBLE root in
 * u, so min |h| over u must vanish to fit tolerance. z as in islandFixtureSurface;
 * Wz(t) = 0.134t - 0.2t^2 + 0.4t^3/3. Dense scan plus local golden-section refinement.
 */
function islandFixtureMembershipResidual(fitted is Vector, velocityZ is array) returns number
{
    const y = fitted[1];
    var best = 1e300;
    var bestU = 0;
    const scanCount = 96;
    for (var index = 0; index <= scanCount; index += 1)
    {
        const u = index / scanCount;
        const h = abs(islandFixtureHeightMismatch(u, y, fitted[0], fitted[2], velocityZ));
        if (h < best)
        {
            best = h;
            bestU = u;
        }
    }
    var low = max(0, bestU - 1 / scanCount);
    var high = min(1, bestU + 1 / scanCount);
    const golden = (sqrt(5) - 1) / 2;
    for (var iteration = 0; iteration < 40; iteration += 1)
    {
        const probeA = high - golden * (high - low);
        const probeB = low + golden * (high - low);
        const valueA = abs(islandFixtureHeightMismatch(probeA, y, fitted[0], fitted[2], velocityZ));
        const valueB = abs(islandFixtureHeightMismatch(probeB, y, fitted[0], fitted[2], velocityZ));
        best = min(best, min(valueA, valueB));
        if (valueA < valueB)
        {
            high = probeB;
        }
        else
        {
            low = probeA;
        }
    }
    return best;
}

/** The island fixture's swept-family height mismatch at tool parameter u, under the motion
    whose z velocity is the given degree-2 Bernstein triple. */
function islandFixtureHeightMismatch(u is number, v is number, x is number, z is number,
    velocityZ is array) returns number
{
    const t = x - u;
    const toolZ = 0.8 * (u ^ 2 / 2 - u ^ 3 / 3) * v * (1 - v);
    return toolZ + quadraticVelocityIntegral(velocityZ, t) - z;
}

/**
 * The merge fixture surface: an exact degree (2, 2) Bezier patch. z is quadratic in each
 * direction, so the control net represents it exactly, and z_u is LINEAR in u - which is what
 * keeps every contact curve a closed-form parabola with no Newton anywhere in the answers this
 * fixture is checked against.
 */
function mergeFixtureSurface() returns map
{
    const uSquared = [0, 0, 1];
    const uLinear = [0, 0.5, 1];
    const vShifted = [0.5, -0.5, 0.5];        // Bernstein coefficients of 2 (v - 1/2)^2
    const greville2 = [0, 0.5, 1];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville2[i], greville2[j],
                    MERGE_FIXTURE_SCALE * (0.5 * uSquared[i] + uLinear[i] * vShifted[j]));
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/** The merge fixture's motion: velocity (1, 0, SCALE w(t)) as the degree-2 Bernstein velocity
    translationMotionFromQuadraticVelocity takes - a linear a + bt is [a, a + b/2, a + b]. */
function mergeFixtureMotion() returns map
{
    const level = MERGE_FIXTURE_SCALE * MERGE_FIXTURE_LEVEL_START;
    const rate = MERGE_FIXTURE_SCALE * MERGE_FIXTURE_LEVEL_RATE;
    return translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0],
        [level, level - rate / 2, level - rate]);
}

/**
 * The merge fixture's lateral branch on the v = vEdge wall, in the shape strip marching hands a
 * branch to the fit. The crossing is u = w(t) - 2(vEdge - 1/2)^2, monotone in t, so this branch
 * never splits. Sampling it by UNIFORM t rather than uniform u is deliberate: it puts the
 * branch's end times exactly on the component's own t range ends, which is what lets a cap
 * station and a branch endpoint compare equal.
 */
function mergeFixtureCapBranch(vEdge is number) returns map
{
    const sampleCount = 21;
    var tSamples = makeArray(sampleCount);
    var uvSamples = makeArray(sampleCount);
    for (var index = 0; index < sampleCount; index += 1)
    {
        const t = 1 - index / (sampleCount - 1);
        tSamples[index] = t;
        uvSamples[index] = [MERGE_FIXTURE_LEVEL_START - MERGE_FIXTURE_LEVEL_RATE * t -
                MERGE_FIXTURE_PROFILE * (vEdge - 0.5) ^ 2, vEdge];
    }
    return { "anchorKind" : "branch", "tSamples" : tSamples, "uvSamples" : uvSamples };
}

/**
 * The merge fixture's u = 1 wall branch - the one that turns over. The two crossings
 * v = 1/2 +- h with h^2 = (w - 1) / 2 exist only while w > 1, and writing v - 1/2 = h(0) s gives
 * t = SPLIT_TIME (1 - s^2) exactly: a parabola in the branch parameter with its maximum at
 * s = 0. Sampled with an EVEN interval count so that maximum falls strictly between two samples
 * and the extremum refinement has real work to do.
 */
function mergeFixtureWallBranch() returns map
{
    const sampleCount = 20;
    const halfWidth = sqrt((MERGE_FIXTURE_LEVEL_START - 1) / MERGE_FIXTURE_PROFILE);
    var tSamples = makeArray(sampleCount);
    var uvSamples = makeArray(sampleCount);
    for (var index = 0; index < sampleCount; index += 1)
    {
        const s = -1 + 2 * index / (sampleCount - 1);
        tSamples[index] = MERGE_FIXTURE_SPLIT_TIME * (1 - s ^ 2);
        uvSamples[index] = [1, 0.5 + halfWidth * s];
    }
    return { "anchorKind" : "branch", "tSamples" : tSamples, "uvSamples" : uvSamples };
}

/**
 * The merge fixture's exact lifted contact point at (t, v): the section is
 * u = w(t) - 2(v - 1/2)^2 and the lift is S(u, v) plus the integrated translation, so the whole
 * contact arc at one station is a closed-form curve in v alone.
 */
function mergeFixtureArcPoint(t is number, v is number) returns Vector
{
    const shifted = MERGE_FIXTURE_PROFILE * (v - 0.5) ^ 2;
    const u = MERGE_FIXTURE_LEVEL_START - MERGE_FIXTURE_LEVEL_RATE * t - shifted;
    return vector(u + t, v, MERGE_FIXTURE_SCALE * (0.5 * u ^ 2 + u * shifted +
                MERGE_FIXTURE_LEVEL_START * t - 0.5 * MERGE_FIXTURE_LEVEL_RATE * t ^ 2));
}

/**
 * Distance from a point to that arc, by golden section on v. This is how a fitted patch's seam
 * edge is checked: against the arc itself, with no marched polyline in between - a chordal
 * polyline at this fixture's step size carries ~9e-4 of sagitta, which would swamp the tolerance
 * being measured.
 */
function mergeFixtureArcDistance(point is Vector, t is number) returns number
{
    const golden = (sqrt(5) - 1) / 2;
    var low = 0;
    var high = 1;
    var probeA = high - golden * (high - low);
    var probeB = low + golden * (high - low);
    var valueA = norm(mergeFixtureArcPoint(t, probeA) - point);
    var valueB = norm(mergeFixtureArcPoint(t, probeB) - point);
    for (var iteration = 0; iteration < 60; iteration += 1)
    {
        if (valueA < valueB)
        {
            high = probeB;
            probeB = probeA;
            valueB = valueA;
            probeA = high - golden * (high - low);
            valueA = norm(mergeFixtureArcPoint(t, probeA) - point);
        }
        else
        {
            low = probeA;
            probeA = probeB;
            valueA = valueB;
            probeB = low + golden * (high - low);
            valueB = norm(mergeFixtureArcPoint(t, probeB) - point);
        }
    }
    return min(valueA, valueB);
}

function mergeFixtureMembershipResidual(fitted is Vector) returns number
{
    const shifted = MERGE_FIXTURE_PROFILE * (fitted[1] - 0.5) ^ 2;
    const t = (fitted[0] - MERGE_FIXTURE_LEVEL_START + shifted) / (1 - MERGE_FIXTURE_LEVEL_RATE);
    const u = fitted[0] - t;
    const exactZ = MERGE_FIXTURE_SCALE * (0.5 * u ^ 2 + u * shifted +
            MERGE_FIXTURE_LEVEL_START * t - 0.5 * MERGE_FIXTURE_LEVEL_RATE * t ^ 2);
    return abs(fitted[2] - exactZ);
}

/** The worst analytic membership residual of a fitted merge-fixture patch, over interior
    parameters (the patch's own data rows are not sampled). */
function worstMergeFixtureResidual(fitSurface is map, samplesPerDirection is number) returns number
{
    const domain = fitSurfaceKnotDomain(fitSurface);
    var worst = 0;
    for (var i = 1; i < samplesPerDirection; i += 1)
    {
        for (var j = 1; j < samplesPerDirection; j += 1)
        {
            worst = max(worst, mergeFixtureMembershipResidual(evaluateBSplineSurfacePoint(fitSurface,
                            domain.uMin + (domain.uMax - domain.uMin) * i / samplesPerDirection,
                            domain.vMin + (domain.vMax - domain.vMin) * j / samplesPerDirection)));
        }
    }
    return worst;
}

/**
 * How many of a clipped section polyline's interior points came, bit for bit, from CONSECUTIVE
 * vertices of the polyline it was clipped out of. This is the exact-sharing check the strip
 * decomposition exists to make possible (spec 2.3): -1 means a point is not the shared
 * polyline's own number, or the run is not consecutive.
 */
function sharedPolylineRunLength(shared is array, clipped is array) returns number
{
    var previous = undefined;
    for (var index = 1; index + 1 < size(clipped); index += 1)
    {
        var found = undefined;
        for (var sharedIndex = 0; sharedIndex < size(shared); sharedIndex += 1)
        {
            if (shared[sharedIndex][0] == clipped[index][0] && shared[sharedIndex][1] == clipped[index][1])
            {
                found = sharedIndex;
                break;
            }
        }
        if (found == undefined || (previous != undefined && abs(found - previous) != 1))
        {
            return -1;
        }
        previous = found;
    }
    return max(0, size(clipped) - 2);
}

/** True when two uv sample arrays are the same numbers, element for element - not merely close. */
function uvSampleArraysIdentical(first is array, second is array) returns boolean
{
    if (size(first) != size(second))
    {
        return false;
    }
    for (var index = 0; index < size(first); index += 1)
    {
        if (first[index][0] != second[index][0] || first[index][1] != second[index][1])
        {
            return false;
        }
    }
    return true;
}


// ============================= Verdict reporting, check tallies, shared fixtures =============================

/**
 * Prints and reports the standard verdict: passSummary when failures is empty, otherwise
 * "FAIL:" followed by the accumulated notes. testName becomes the console tag.
 */
export function reportTestVerdict(context is Context, id is Id, testName is string,
    failures is string, passSummary is string)
{
    const verdict = failures == "" ? ("PASS: " ~ passSummary) : ("FAIL:" ~ failures);
    println("[" ~ testName ~ "] VERDICT: " ~ verdict);
    reportFeatureInfo(context, id, verdict);
}

/** As above, with the check count folded into the PASS line. */
export function reportTestVerdict(context is Context, id is Id, testName is string,
    checks is number, failures is string, passSummary is string)
{
    reportTestVerdict(context, id, testName, failures, checks ~ " checks - " ~ passSummary);
}

/** An empty check tally. */
export function newCheckTally() returns map
{
    return { "checks" : 0, "failures" : 0, "notes" : "" };
}

/**
 * Records one check. A failure appends its message to the notes; a pass costs nothing but the
 * count, so a test can assert unconditionally and still report how much it verified.
 */
export function checkThat(tally is map, passed is boolean, failureMessage is string) returns map
{
    return {
            "checks" : tally.checks + 1,
            "failures" : tally.failures + (passed ? 0 : 1),
            "notes" : passed ? tally.notes : (tally.notes ~ " " ~ failureMessage)
        };
}

/** Records one check on a magnitude: passes when worst is within tolerance. */
export function checkWithin(tally is map, worst is number, tolerance is number,
    description is string) returns map
{
    return checkThat(tally, abs(worst) <= tolerance,
        description ~ " is " ~ worst ~ ", over the " ~ tolerance ~ " tolerance.");
}

/** Prints and reports a tally's verdict, with the failed-of-total count on a failure. */
export function reportCheckTally(context is Context, id is Id, testName is string,
    tally is map, passSummary is string)
{
    const verdict = tally.failures == 0 ?
        ("PASS: " ~ tally.checks ~ " checks - " ~ passSummary) :
        ("FAIL (" ~ tally.failures ~ " of " ~ tally.checks ~ " checks):" ~ tally.notes);
    println("[" ~ testName ~ "] VERDICT: " ~ verdict);
    reportFeatureInfo(context, id, verdict);
}

/**
 * The worst deviation of three rotation columns from orthonormality:
 * max |dot(ci, cj) - delta_ij|.
 */
export function orthonormalityDefect(columns is array) returns number
{
    var defect = abs(dot(columns[0], columns[0]) - 1);
    defect = max(defect, abs(dot(columns[1], columns[1]) - 1));
    defect = max(defect, abs(dot(columns[2], columns[2]) - 1));
    defect = max(defect, abs(dot(columns[0], columns[1])));
    defect = max(defect, abs(dot(columns[0], columns[2])));
    defect = max(defect, abs(dot(columns[1], columns[2])));
    return defect;
}

/** True when two coefficient arrays have the same size and agree elementwise within tolerance. */
export function coefficientsNear(actual is array, expected is array, tolerance is number) returns boolean
{
    if (size(actual) != size(expected))
    {
        return false;
    }
    for (var index = 0; index < size(actual); index += 1)
    {
        if (abs(actual[index] - expected[index]) > tolerance)
        {
            return false;
        }
    }
    return true;
}

/**
 * Reattaches meters to an array of unitless control points (for handing a module-side spline
 * to kernel-facing std functions).
 */
export function attachMeters(unitlessPoints is array) returns array
{
    var withUnits = makeArray(size(unitlessPoints), 0);
    for (var index = 0; index < size(unitlessPoints); index += 1)
    {
        withUnits[index] = unitlessPoints[index] * meter;
    }
    return withUnits;
}

export function identityRotationRows() returns array
{
    return [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
}

export function zeroRows() returns array
{
    return [[0, 0, 0], [0, 0, 0], [0, 0, 0]];
}

/** A motion station in the shape evaluateMotionSample returns. */
export function constantMotionSample(rotationRows is array, rotationDerivativeRows is array,
    translationDerivative is Vector) returns map
{
    return {
            "rotation" : matrix(rotationRows),
            "rotationDerivative" : matrix(rotationDerivativeRows),
            "rotationSecondDerivative" : matrix(zeroRows()),
            "translation" : vector(0, 0, 0),
            "translationDerivative" : translationDerivative,
            "translationSecondDerivative" : vector(0, 0, 0)
        };
}

/**
 * A single-span cubic translation motion with identity rotation whose VELOCITY is the given
 * degree-2 Bernstein polynomial per component ([x0,x1,x2], [y...], [z...]): control points
 * integrate as P_{k+1} = P_k + q_k / 3 from the origin.
 */
export function translationMotionFromQuadraticVelocity(velocityX is array, velocityY is array,
    velocityZ is array, knots is array) returns map
{
    var controlPoints = makeArray(4);
    controlPoints[0] = vector(0, 0, 0);
    for (var k = 0; k < 3; k += 1)
    {
        controlPoints[k + 1] = controlPoints[k] + vector(velocityX[k], velocityY[k], velocityZ[k]) / 3;
    }
    var columns = makeArray(3);
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        var axis = vector(columnIndex == 0 ? 1 : 0, columnIndex == 1 ? 1 : 0, columnIndex == 2 ? 1 : 0);
        columns[columnIndex] = {
                "degree" : 3, "knots" : knots, "isRational" : false,
                "controlPoints" : [axis, axis, axis, axis]
            };
    }
    return {
            "columnX" : columns[0], "columnY" : columns[1], "columnZ" : columns[2],
            "translation" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : controlPoints }
        };
}

/** The single-span case of the above: knots [0, 0, 0, 0, 1, 1, 1, 1] over t in [0, 1]. */
export function translationMotionFromQuadraticVelocity(velocityX is array, velocityY is array,
    velocityZ is array) returns map
{
    return translationMotionFromQuadraticVelocity(velocityX, velocityY, velocityZ,
        [0, 0, 0, 0, 1, 1, 1, 1]);
}

/**
 * The exact integral from 0 to t of one component of the above velocity - the translation that
 * motion actually carries, for tests that check a fitted point against the swept family in
 * closed form. With B(s) the degree-2 Bernstein polynomial of `coefficients`,
 *     integral = q0 (t - t^2 + t^3/3) + q1 (t^2 - 2t^3/3) + q2 t^3/3.
 */
export function quadraticVelocityIntegral(coefficients is array, t is number) returns number
{
    return coefficients[0] * (t - t ^ 2 + t ^ 3 / 3) +
        coefficients[1] * (t ^ 2 - 2 * t ^ 3 / 3) +
        coefficients[2] * t ^ 3 / 3;
}

/**
 * The constant-velocity case of the above over t in [0, 1]: the derivative is exactly
 * velocity everywhere. Unit-stripped, meters implied, matching the records extraction
 * produces.
 */
export function constantVelocityTranslationMotion(velocity is Vector) returns map
{
    return translationMotionFromQuadraticVelocity(
        [velocity[0], velocity[0], velocity[0]],
        [velocity[1], velocity[1], velocity[1]],
        [velocity[2], velocity[2], velocity[2]]);
}

/**
 * A rotation-dominant motion: `A(t) = I + t [w]x` for angular velocity `w`, with `b` held at
 * the origin so that `b'` is EXACTLY zero - the station class spec 2.2 calls `|b'| -> 0`.
 *
 * `A` is the degree-1 Taylor polynomial of `exp(t [w]x)`: exactly orthonormal at t = 0, exactly
 * `[w]x` in its derivative there, and drifting from SO(3) as O(t^2 |w|^2) after - the regime
 * spec 2.1 already accepts, since the envelope is computed exactly with respect to the FITTED
 * motion. Being polynomial is what matters here: the coefficient path needs A's columns as
 * splines, and every quantity a test asserts against a closed form has to be exact.
 *
 * The four splines are degree 3 on one clamped knot vector, matching the translation fixtures
 * above, so a test can swap one for the other without touching the span decomposition.
 */
export function firstOrderRotationMotion(angularVelocity is Vector, knots is array) returns map
{
    var columns = makeArray(3);
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        const axis = vector(columnIndex == 0 ? 1 : 0, columnIndex == 1 ? 1 : 0, columnIndex == 2 ? 1 : 0);
        const columnDerivative = cross(angularVelocity, axis);
        var controlPoints = makeArray(4, axis);
        for (var k = 0; k < 4; k += 1)
        {
            controlPoints[k] = axis + columnDerivative * (k / 3);
        }
        columns[columnIndex] = {
                "degree" : 3, "knots" : knots, "isRational" : false,
                "controlPoints" : controlPoints
            };
    }
    return {
            "columnX" : columns[0], "columnY" : columns[1], "columnZ" : columns[2],
            "translation" : {
                    "degree" : 3, "knots" : knots, "isRational" : false,
                    "controlPoints" : makeArray(4, vector(0, 0, 0))
                }
        };
}

/** The single-span case of the above: knots [0, 0, 0, 0, 1, 1, 1, 1] over t in [0, 1]. */
export function firstOrderRotationMotion(angularVelocity is Vector) returns map
{
    return firstOrderRotationMotion(angularVelocity, [0, 0, 0, 0, 1, 1, 1, 1]);
}

/**
 * The island fixture: S = (u, v, z) with z = 0.8 (u^2/2 - u^3/3) v(1 - v), so that
 * z_u = 0.8 u(1-u) v(1-v) peaks at exactly 0.05 at the patch center. Degrees (3, 2),
 * single Bezier patch, non-rational.
 */
export function islandFixtureSurface() returns map
{
    const uCoefficients = [0, 0, 1 / 6, 1 / 6];
    const vCoefficients = [0, 0.5, 0];
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville3[i], greville2[j], 0.8 * uCoefficients[i] * vCoefficients[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The slant fixture: S = (u, v, 0.15 u^2 - 0.18 u + 0.05 u v), degrees (2, 1). Under
 * velocity (1, 0, 0.06) the envelope function is exactly f = 0.24 - 0.3 u - 0.05 v, which
 * makes it both the funnel solver section fixture and the fit module ruled fixture.
 */
export function slantFixtureSurface() returns map
{
    const zGrid = [[0, 0], [-0.09, -0.065], [-0.03, 0.02]];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(2);
        for (var j = 0; j < 2; j += 1)
        {
            row[j] = vector(greville2[i], j, zGrid[i][j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 1,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * A parabolic cylinder S = (u, v, 0.5 c u^2) as a degree (2, 1) Bezier patch: z_u = c u is
 * linear, so a translation whose velocity is polynomial in t makes the envelope function
 * polynomial in both, with contact on a single ruling.
 */
export function parabolicCylinderSurface(c is number) returns map
{
    const greville2 = [0, 0.5, 1];
    const zControls = [0, 0, 0.5 * c];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(2);
        for (var j = 0; j < 2; j += 1)
        {
            row[j] = vector(greville2[i], j, zControls[i]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 1,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The 4x4 control net of the wavy bicubic test patch, in meters: a mildly non-developable
 * freeform face with no analytic class, used wherever a fixture must NOT classify as one of
 * the five surface types.
 */
export function wavyBicubicPatchNet() returns array
{
    var rows = makeArray(4);
    for (var rowIndex = 0; rowIndex < 4; rowIndex += 1)
    {
        var row = makeArray(4);
        for (var columnIndex = 0; columnIndex < 4; columnIndex += 1)
        {
            row[columnIndex] = vector(0.1 + columnIndex * 0.01, rowIndex * 0.01,
                        0.005 * sin(90 * degree * columnIndex) + 0.004 * cos(60 * degree * rowIndex)) * meter;
        }
        rows[rowIndex] = row;
    }
    return rows;
}


// ============================= Orientation =============================

/** Where the contact ruling sits at time t: f = 0 gives u = w_z(t) / c with w_z linear. */
function contactRuling(c is number, z0 is number, z1 is number, t is number) returns number
{
    return (z0 + z1 * t) / c;
}

/** The time at which the ruling passes through u - the inverse of contactRuling. */
function contactTime(c is number, z0 is number, z1 is number, u is number) returns number
{
    return (c * u - z0) / z1;
}

/** The funnel's project-to-(u, v) chart: Psi(u, v) = Phi(u, v, t(u)). */
function funnelChartPoint(motion is map, surface is map, c is number, z0 is number, z1 is number,
    u is number, v is number) returns Vector
{
    return liftContactPoint(motion, surface, u, v, contactTime(c, z0, z1, u));
}

/**
 * A fit grid on the fixture's funnel: rows are stations, columns are the ruling's v samples -
 * which IS the section here, since f does not depend on v. `reverseQ` walks v the other way, so
 * the caller can check that the q direction alone decides the patch verdict.
 * Returns { stations, uvRows, liftedGrid }.
 */
function buildFunnelFitGrid(motion is map, surface is map, c is number, z0 is number, z1 is number,
    stationCount is number, qCount is number, reverseQ is boolean) returns map
{
    var stations = makeArray(stationCount, 0);
    var uvRows = makeArray(stationCount);
    var liftedGrid = makeArray(stationCount);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        const t = stationIndex / (stationCount - 1);
        const u = contactRuling(c, z0, z1, t);
        stations[stationIndex] = t;
        var uvRow = makeArray(qCount);
        var liftedRow = makeArray(qCount);
        for (var qIndex = 0; qIndex < qCount; qIndex += 1)
        {
            const fraction = qIndex / (qCount - 1);
            const v = reverseQ ? 1 - fraction : fraction;
            uvRow[qIndex] = [u, v];
            liftedRow[qIndex] = liftContactPoint(motion, surface, u, v, t);
        }
        uvRows[stationIndex] = uvRow;
        liftedGrid[stationIndex] = liftedRow;
    }
    return { "stations" : stations, "uvRows" : uvRows, "liftedGrid" : liftedGrid };
}

/**
 * The funnel solver's island bump: S = (u, v, z) with z = 0.8 (u^2/2 - u^3/3) v(1 - v), so that
 * z_u = 0.8 u(1 - u) v(1 - v) peaks at exactly 0.05 in the middle. Degrees (3, 2), one Bezier
 * patch, non-rational. Under velocity (1, 0, w_z) the contact set is the level set
 * z_u = w_z - a CLOSED LOOP around (0.5, 0.5) for any 0 < w_z < 0.05, which is what makes this
 * the fixture for the closed-row and pole machinery.
 */
function islandBumpSurface() returns map
{
    const uCoefficients = [0, 0, 1 / 6, 1 / 6];
    const vCoefficients = [0, 0.5, 0];
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville3[i], greville2[j], 0.8 * uCoefficients[i] * vCoefficients[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * Where the contact loop crosses the ray leaving (0.5, 0.5) at `theta`, by bisection on the
 * module's OWN envelope evaluator - f is negative at the centre (the bump's peak beats w_z) and
 * positive out near the domain edge, so the bracket is unconditional and there is exactly one
 * crossing. Returns [u, v]. A collapsed pole returns the centre itself.
 *
 * The pole test carries a TOLERANCE, learned live. At birth f(centre) is exactly zero in closed
 * form, but through de Boor it lands a few 1e-18 either side of zero - and on the negative side
 * an exact `>= 0` test misses the pole and bisection hands back a tiny loop instead of the
 * centre. The row then is not collapsed, the certificate does not skip it, and (worse) a caller
 * that reverses only the non-pole rows leaves the grid inconsistently ordered.
 */
function loopPointOnRay(motion is map, surface is map, theta is number, t is number) returns array
{
    const rayU = cos(theta * radian);
    const rayV = sin(theta * radian);
    const outer = 0.49;
    if (evaluateEnvelopePointwise(motion, surface, 0.5, 0.5, t) >= -1e-12)
    {
        return [0.5, 0.5];
    }
    var low = 0;
    var high = outer;
    for (var iteration = 0; iteration < 60; iteration += 1)
    {
        const mid = 0.5 * (low + high);
        if (evaluateEnvelopePointwise(motion, surface, 0.5 + mid * rayU, 0.5 + mid * rayV, t) < 0)
        {
            low = mid;
        }
        else
        {
            high = mid;
        }
    }
    const radius = 0.5 * (low + high);
    return [0.5 + radius * rayU, 0.5 + radius * rayV];
}

/**
 * A CLOSED-row fit grid on the bump's funnel: one row per station, q running counterclockwise
 * around the loop at n DISTINCT angles with no repeated closer - the periodic convention the
 * island and tube fits store. A station whose loop has collapsed contributes a collapsed row,
 * which is exactly the pole case the certificate has to skip.
 * Returns { stations, uvRows, liftedGrid }.
 */
function buildLoopGrid(motion is map, surface is map, stations is array, qCount is number) returns map
{
    var uvRows = makeArray(size(stations));
    var liftedGrid = makeArray(size(stations));
    for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
    {
        const t = stations[stationIndex];
        var uvRow = makeArray(qCount);
        var liftedRow = makeArray(qCount);
        for (var qIndex = 0; qIndex < qCount; qIndex += 1)
        {
            const uv = loopPointOnRay(motion, surface, 2 * PI * qIndex / qCount, t);
            uvRow[qIndex] = uv;
            liftedRow[qIndex] = liftContactPoint(motion, surface, uv[0], uv[1], t);
        }
        uvRows[stationIndex] = uvRow;
        liftedGrid[stationIndex] = liftedRow;
    }
    return { "stations" : stations, "uvRows" : uvRows, "liftedGrid" : liftedGrid };
}

/** A rational, deliberately v-asymmetric degree (2, 2) net - the reversal fixture. Asymmetry is
    the point: a symmetric net would pass a broken mirror. */
function rationalTestSurface() returns map
{
    const zGrid = [[0, 0.13, -0.07], [0.21, -0.05, 0.31], [-0.11, 0.27, 0.04]];
    const weightGrid = [[1, 0.6, 1.4], [0.8, 1, 0.7], [1.2, 0.9, 1]];
    var net = makeArray(3);
    var weights = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(0.5 * i, 0.5 * j + 0.1 * i, zGrid[i][j]);
        }
        net[i] = row;
        weights[i] = weightGrid[i];
    }
    return {
            "uDegree" : 2, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "weights" : weights, "isRational" : true,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * That reversing a net's q direction is exact: the reversed surface at mirrored v must be the
 * same point, and its parametric normal must be the exact negative there.
 * Returns { pointError, normalError }.
 */
function checkSurfaceReversal(surface is map) returns map
{
    const reversed = reverseFitSurfaceQDirection(surface);
    const vStart = surface.vKnots[0];
    const vEnd = surface.vKnots[size(surface.vKnots) - 1];
    var pointError = 0;
    var normalError = 0;
    for (var i = 0; i <= 6; i += 1)
    {
        for (var j = 0; j <= 6; j += 1)
        {
            const u = i / 6;
            const v = vStart + (vEnd - vStart) * j / 6;
            const mirroredV = vStart + vEnd - v;
            const original = evaluateBSplineSurfaceDerivatives(surface, u, v, 1, 1);
            const mirrored = evaluateBSplineSurfaceDerivatives(reversed, u, mirroredV, 1, 1);
            pointError = max(pointError, norm(original[0][0] - mirrored[0][0]));
            normalError = max(normalError, norm(cross(original[1][0], original[0][1]) +
                        cross(mirrored[1][0], mirrored[0][1])));
        }
    }
    return { "pointError" : pointError, "normalError" : normalError };
}


// ============================= Degeneracy detectors =============================

/**
 * S = (u, v, u^2 / 2 + (v - 1/2)^3 / 6) as a single Bezier patch at degrees (2, 3). The z net is
 * the sum of the two directions' own Bernstein coefficients, which is exact for a separable
 * polynomial: [0, 0, 1/2] for u^2 / 2 and [-1, 1, -1, 1] / 48 for (v - 1/2)^3 / 6.
 *
 * The cubic in v is what makes the fixture able to produce a near tangency as well as a crossing
 * one: it puts (v - 1/2)^2 into the normal, so f_t comes out with an interior extremum in v
 * rather than monotone.
 */
function tangencyFixtureSurface() returns map
{
    const grevilleU = [0, 0.5, 1];
    const grevilleV = [0, 1 / 3, 2 / 3, 1];
    const uCoefficients = [0, 0, 0.5];
    const vCoefficients = [-1 / 48, 1 / 48, -1 / 48, 1 / 48];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(4);
        for (var j = 0; j < 4; j += 1)
        {
            row[j] = vector(grevilleU[i], grevilleV[j], uCoefficients[i] + vCoefficients[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 3,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 0, 0, 1, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The translation whose velocity at t = 1/2 is (1, 0, 1/2) and whose acceleration there is
 * (0, 1, timeDerivativeOffset). A degree-2 Bernstein velocity has V(1/2) = (q0 + 2q1 + q2) / 4
 * and V'(1/2) = q2 - q0, which is what fixes each triple; the z triple is the only one that
 * moves, so one number carries the fixture through all three verdicts.
 */
function tangencyFixtureMotion(timeDerivativeOffset is number) returns map
{
    return translationMotionFromQuadraticVelocity([1, 1, 1], [-0.5, 0, 0.5],
        [-0.5 * timeDerivativeOffset, 1, 0.5 * timeDerivativeOffset]);
}

/**
 * The edge e(s) = (s, s^2 / 2, c s^3 / 6) as a single cubic Bezier, unit-stripped. Its tangent
 * e'(s) = (1, s, c s^2 / 2) is polynomial in s, which is what lets a degree-2 Bernstein velocity
 * match it exactly at one station and nowhere else.
 */
function singularEdgeCurve(curvature is number) returns map
{
    return {
            "degree" : 3,
            "knots" : [0, 0, 0, 0, 1, 1, 1, 1],
            "isRational" : false,
            "controlPoints" : [vector(0, 0, 0), vector(1 / 3, 0, 0), vector(2 / 3, 1 / 6, 0),
                    vector(1, 0.5, curvature / 6)]
        };
}

/**
 * The translation whose velocity is b'(t) = (1, t, c t^2 / 2 + eps (t - t*)). Against the edge
 * above, matching first components forces s = t and then eps (t - t*) = 0, so the parallelism is
 * an isolated point at (t*, t*) rather than the whole diagonal that eps = 0 would give.
 */
function singularEdgeMotion(curvature is number, epsilon is number, singularT is number) returns map
{
    // Monomials of the z velocity, converted to degree-2 Bernstein by
    // b_k = sum_{i <= k} C(k,i)/C(2,i) m_i.
    const m0 = -epsilon * singularT;
    const m1 = epsilon;
    const m2 = 0.5 * curvature;
    return translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0.5, 1],
        [m0, m0 + 0.5 * m1, m0 + m1 + m2]);
}

/** "a/b/c" of the piece sizes, for one readable println. */
function pieceSizes(pieces is array) returns string
{
    var text = "";
    for (var index = 0; index < size(pieces); index += 1)
    {
        text = text ~ (index == 0 ? "" : "/") ~ size(pieces[index]);
    }
    return text;
}

/** The largest magnitude in an array of numbers. */
function largestOf(values is array) returns number
{
    var largest = 0;
    for (var index = 0; index < size(values); index += 1)
    {
        largest = max(largest, abs(values[index]));
    }
    return largest;
}


// ============================= Trim loops, live =============================

/** A v-constant trim run across the whole seam of a unit-period u domain. */
function seamArcSamples(v is number, segmentCount is number) returns array
{
    var samples = makeArray(segmentCount + 1, vector(0, v));
    for (var index = 0; index <= segmentCount; index += 1)
    {
        samples[index] = vector(index / segmentCount, v);
    }
    return samples;
}

/**
 * The uv of a point on the fixture surface, from its x and y alone: S(u, v) = (0.2u, 0.15v, ...)
 * inverts by division, so the probe positions carry no inversion error of their own.
 */
function surfacePointUv(xy is Vector) returns Vector
{
    return vector(xy[0] / 0.2, xy[1] / 0.15);
}


// ============================= Test features =============================

annotation { "Feature Type Name" : "Sweep Envelope Math Self Test" }
export const sweepEnvelopeMathSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";

        // Fixture surface: bicubic, two spans per direction (four Bezier patches), wavy in z,
        // non-rational, clamped. Gentle waves keep N_z positive everywhere, which test 3 uses.
        const fixtureKnots = [0, 0, 0, 0, 0.5, 1, 1, 1, 1];
        var fixtureNet = makeArray(5);
        for (var rowIndex = 0; rowIndex < 5; rowIndex += 1)
        {
            var netRow = makeArray(5);
            for (var columnIndex = 0; columnIndex < 5; columnIndex += 1)
            {
                netRow[columnIndex] = vector(rowIndex * 0.02, columnIndex * 0.02,
                            0.004 * sin(80 * degree * rowIndex) + 0.003 * cos(70 * degree * columnIndex));
            }
            fixtureNet[rowIndex] = netRow;
        }
        const fixtureSurface = {
                "uDegree" : 3, "vDegree" : 3,
                "uKnots" : fixtureKnots, "vKnots" : fixtureKnots,
                "controlPoints" : fixtureNet,
                "isRational" : false,
                "isUPeriodic" : false, "isVPeriodic" : false
            };

        // Motion A - pure translation along a curved path (identity rotation): every rotation
        // column is a constant spline. Motion B - a varying, deliberately non-orthogonal A(t):
        // the factorization is algebra, valid for ANY coefficients, and a non-orthogonal A
        // exercises every term. Both single-span cubics; motion C is a four-span translation.
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];
        const motionA = {
                "columnX" : constantColumnSpline(vector(1, 0, 0), singleSpanKnots),
                "columnY" : constantColumnSpline(vector(0, 1, 0), singleSpanKnots),
                "columnZ" : constantColumnSpline(vector(0, 0, 1), singleSpanKnots),
                "translation" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0, 0, 0.02), vector(0, 0, 0.045), vector(0, 0, 0.06)]
                }
            };
        const motionB = {
                "columnX" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(1, 0, 0), vector(0.95, 0.15, 0.02), vector(0.88, 0.28, 0.06), vector(0.8, 0.4, 0.1)]
                },
                "columnY" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 1, 0), vector(-0.12, 0.97, 0.05), vector(-0.24, 0.92, 0.09), vector(-0.35, 0.85, 0.14)]
                },
                "columnZ" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 1), vector(0.03, -0.06, 0.99), vector(0.07, -0.11, 0.97), vector(0.12, -0.18, 0.93)]
                },
                "translation" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0.02, 0.01, 0.01), vector(0.05, 0.015, 0.03), vector(0.09, 0.02, 0.04)]
                }
            };

        // Test 1: factored materialization agrees with the independent pointwise path.
        // The pointwise path evaluates the ORIGINAL splines with splineRefinementUtils
        // evaluators end to end - a genuinely different code path from the coefficient
        // assembly, so agreement validates both.
        const patchFactors = buildEnvelopePatchFactors(fixtureSurface);
        const testFractions = [0.08, 0.31, 0.5, 0.77, 0.94];
        var worstAgreement = 0;
        for (var motionPass = 0; motionPass < 2; motionPass += 1)
        {
            const motion = motionPass == 0 ? motionA : motionB;
            const spans = buildMotionSpanPolynomials(motion);
            for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
            {
                for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
                {
                    const patch = patchFactors.patches[uSegment][vSegment];
                    const block = materializeEnvelopeBlock(buildEnvelopePatchProducts(patch), spans[0]);
                    for (var fraction in testFractions)
                    {
                        const localU = fraction;
                        const localV = 1 - fraction * 0.83;
                        const localT = 0.15 + 0.7 * fraction;
                        const materialized = evaluateMaterializedBlock(block, localU, localV, localT);
                        const globalU = patch.uStart + (patch.uEnd - patch.uStart) * localU;
                        const globalV = patch.vStart + (patch.vEnd - patch.vStart) * localV;
                        const globalT = spans[0].tStart + (spans[0].tEnd - spans[0].tStart) * localT;
                        const pointwise = evaluateEnvelopePointwise(motion, fixtureSurface, globalU, globalV, globalT);
                        const disagreement = abs(materialized - pointwise) / (1 + abs(pointwise));
                        if (disagreement > worstAgreement)
                        {
                            worstAgreement = disagreement;
                        }
                    }
                }
            }
        }
        println("[ENVELOPE MATH SELF TEST] factored vs pointwise worst relative disagreement: " ~ worstAgreement);
        if (worstAgreement > 1e-9)
        {
            failures = failures ~ " factored and pointwise paths disagree by " ~ worstAgreement ~ ".";
        }

        // Test 2: screening. Pure +Z translation against a patch whose N_z never vanishes must
        // kill every block at the LOOSE screen, before any materialization. A tilted
        // translation must leave live blocks.
        const spansA = buildMotionSpanPolynomials(motionA);
        var looseDeadCount = 0;
        var blockCount = 0;
        for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
        {
            for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
            {
                blockCount += 1;
                const screen = screenEnvelopeBlock(patchFactors.patches[uSegment][vSegment], spansA[0]);
                if (!screen.canVanish)
                {
                    looseDeadCount += 1;
                }
            }
        }
        println("[ENVELOPE MATH SELF TEST] +Z-dominated translation: " ~ looseDeadCount ~ " of " ~
            blockCount ~ " blocks killed by the loose screen");
        if (looseDeadCount != blockCount)
        {
            failures = failures ~ " +Z translation left " ~ (blockCount - looseDeadCount) ~
                " block(s) alive in the loose screen.";
        }

        const motionTilted = {
                "columnX" : motionA.columnX, "columnY" : motionA.columnY, "columnZ" : motionA.columnZ,
                "translation" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0.02, 0, 0.003), vector(0.04, 0, 0.006), vector(0.06, 0, 0.009)]
                }
            };
        const spansTilted = buildMotionSpanPolynomials(motionTilted);
        var liveCount = 0;
        var hullDeadCount = 0;
        for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
        {
            for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
            {
                const patch = patchFactors.patches[uSegment][vSegment];
                const screen = screenEnvelopeBlock(patch, spansTilted[0]);
                if (!screen.canVanish)
                {
                    hullDeadCount += 1;
                    continue;
                }
                const block = materializeEnvelopeBlock(buildEnvelopePatchProducts(patch), spansTilted[0]);
                if (envelopeBlockExcludesZero(block, 0))
                {
                    hullDeadCount += 1;
                }
                else
                {
                    liveCount += 1;
                }
            }
        }
        println("[ENVELOPE MATH SELF TEST] tilted translation: " ~ liveCount ~ " live / " ~
            hullDeadCount ~ " dead of " ~ blockCount ~ " blocks");
        if (liveCount < 1)
        {
            failures = failures ~ " tilted translation produced no live blocks (grazing set lost).";
        }

        // Test 3: multi-span motion bookkeeping - four spans, aligned across all four motion
        // splines, with factored/pointwise agreement holding on a middle span.
        const fourSpanKnots = [0, 0, 0, 0, 0.25, 0.5, 0.75, 1, 1, 1, 1];
        const motionC = {
                "columnX" : constantColumnSpline(vector(1, 0, 0), fourSpanKnots),
                "columnY" : constantColumnSpline(vector(0, 1, 0), fourSpanKnots),
                "columnZ" : constantColumnSpline(vector(0, 0, 1), fourSpanKnots),
                "translation" : {
                    "degree" : 3, "knots" : fourSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0.01, 0.002, 0.012), vector(0.025, 0.006, 0.03),
                                vector(0.045, 0.011, 0.041), vector(0.06, 0.014, 0.055), vector(0.075, 0.016, 0.06), vector(0.09, 0.017, 0.07)]
                }
            };
        const spansC = buildMotionSpanPolynomials(motionC);
        if (size(spansC) != 4)
        {
            failures = failures ~ " four-span motion decomposed into " ~ size(spansC) ~ " spans.";
        }
        else
        {
            const patch = patchFactors.patches[1][0];
            const block = materializeEnvelopeBlock(buildEnvelopePatchProducts(patch), spansC[2]);
            const materialized = evaluateMaterializedBlock(block, 0.4, 0.6, 0.3);
            const pointwise = evaluateEnvelopePointwise(motionC, fixtureSurface,
                patch.uStart + (patch.uEnd - patch.uStart) * 0.4,
                patch.vStart + (patch.vEnd - patch.vStart) * 0.6,
                spansC[2].tStart + (spansC[2].tEnd - spansC[2].tStart) * 0.3);
            const spanDisagreement = abs(materialized - pointwise) / (1 + abs(pointwise));
            println("[ENVELOPE MATH SELF TEST] four-span middle-span disagreement: " ~ spanDisagreement);
            if (spanDisagreement > 1e-9)
            {
                failures = failures ~ " multi-span materialization disagrees by " ~ spanDisagreement ~ ".";
            }
        }

        // Test 4: the strip function against a hand dot product, and the time derivative
        // against a central difference.
        const stripNormal = vector(0.2, -0.3, 0.93);
        const stripPoint = vector(0.04, 0.02, 0.01);
        const stripValue = evaluateContactFunctionAtPoint(motionB, stripNormal, stripPoint, 0.37);
        const sampleAtT = evaluateMotionSample(motionB, 0.37);
        const handValue = dot(sampleAtT.rotation * stripNormal, sampleAtT.rotationDerivative * stripPoint + sampleAtT.translationDerivative);
        if (abs(stripValue - handValue) > 1e-12 * (1 + abs(handValue)))
        {
            failures = failures ~ " strip function disagrees with the hand dot product.";
        }
        const timeDerivative = evaluateEnvelopeTimeDerivativePointwise(motionB, fixtureSurface, 0.3, 0.6, 0.5);
        const centralStep = 1e-5;
        const centralDifference = (evaluateEnvelopePointwise(motionB, fixtureSurface, 0.3, 0.6, 0.5 + centralStep) -
                evaluateEnvelopePointwise(motionB, fixtureSurface, 0.3, 0.6, 0.5 - centralStep)) / (2 * centralStep);
        const derivativeDisagreement = abs(timeDerivative - centralDifference) / (1 + abs(centralDifference));
        println("[ENVELOPE MATH SELF TEST] f_t vs central difference relative disagreement: " ~ derivativeDisagreement);
        if (derivativeDisagreement > 1e-7)
        {
            failures = failures ~ " f_t disagrees with the central difference by " ~ derivativeDisagreement ~ ".";
        }

        // Test 5: workload counters sized to match the baseline profile of the eager factor
        // build, split per stage so the profiler
        // attributes factor builds, lazy product builds, and materializations separately.
        var factorBuildCount = 0;
        var productBuildCount = 0;
        var materializedCount = 0;
        for (var repetition = 0; repetition < 12; repetition += 1)
        {
            const repeatedFactors = buildEnvelopePatchFactors(fixtureSurface);
            factorBuildCount += 1;
            for (var uSegment = 0; uSegment < repeatedFactors.uSegments; uSegment += 1)
            {
                for (var vSegment = 0; vSegment < repeatedFactors.vSegments; vSegment += 1)
                {
                    const products = buildEnvelopePatchProducts(repeatedFactors.patches[uSegment][vSegment]);
                    productBuildCount += 1;
                    for (var spanIndex = 0; spanIndex < size(spansC); spanIndex += 1)
                    {
                        const block = materializeEnvelopeBlock(products, spansC[spanIndex]);
                        materializedCount += size(block);
                    }
                }
            }
        }
        println("[ENVELOPE MATH SELF TEST] workload: " ~ factorBuildCount ~ " factor builds (4 patches each), " ~
            productBuildCount ~ " lazy product builds, " ~ materializedCount ~
            " coefficient grids materialized across " ~ (productBuildCount * size(spansC)) ~ " blocks");

        reportTestVerdict(context, id, "ENVELOPE MATH SELF TEST", failures,
            "factored coefficients match the independent pointwise path to machine precision on " ~
            "both motions and across spans; loose screen kills all blocks under +Z translation; tilted " ~
            "translation leaves a live grazing set; strip function and f_t verified.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Pointwise Self Test" }
export const sweepFunnelPointwiseSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // ---------- Pointwise layer ----------

        // Contact roots: constant-rotation motion whose velocity x-component is
        // (t - 0.3)(t - 0.7) - roots at exactly 0.3 and 0.7.
        const parabolicMotion = translationMotionFromQuadraticVelocity(
            [0.21, -0.29, 0.21], [0, 0, 0], [1, 1, 1], singleSpanKnots);
        const parabolicRoots = findContactFunctionRoots(parabolicMotion,
            vector(1, 0, 0), vector(0, 0, 0), 0, 1, 16, 1e-13);
        if (size(parabolicRoots) != 2 ||
            abs(parabolicRoots[0].t - 0.3) > 1e-10 || abs(parabolicRoots[1].t - 0.7) > 1e-10)
        {
            failures = failures ~ " contact roots expected {0.3, 0.7}, got " ~ toString(parabolicRoots) ~ ".";
        }
        else
        {
            println("[FUNNEL SELF TEST] contact roots at " ~ parabolicRoots[0].t ~ ", " ~ parabolicRoots[1].t ~
                " (errors " ~ abs(parabolicRoots[0].t - 0.3) ~ ", " ~ abs(parabolicRoots[1].t - 0.7) ~ ")");
        }

        // Contact-function time derivative against a central difference on a varying,
        // non-orthogonal motion (every term of g_t exercised).
        const twistingMotion = {
                "columnX" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(1, 0, 0), vector(0.95, 0.15, 0.02), vector(0.88, 0.28, 0.06), vector(0.8, 0.4, 0.1)]
                },
                "columnY" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 1, 0), vector(-0.12, 0.97, 0.05), vector(-0.24, 0.92, 0.09), vector(-0.35, 0.85, 0.14)]
                },
                "columnZ" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 1), vector(0.03, -0.06, 0.99), vector(0.07, -0.11, 0.97), vector(0.12, -0.18, 0.93)]
                },
                "translation" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0.02, 0.01, 0.01), vector(0.05, 0.015, 0.03), vector(0.09, 0.02, 0.04)]
                }
            };
        const derivativeNormal = vector(0.2, -0.3, 0.93);
        const derivativePoint = vector(0.04, 0.02, 0.01);
        var worstDerivativeDisagreement = 0;
        for (var tSample in [0.22, 0.5, 0.81])
        {
            const analytic = evaluateContactFunctionTimeDerivative(twistingMotion, derivativeNormal, derivativePoint, tSample);
            const step = 1e-5;
            const central = (evaluateContactFunctionAtPoint(twistingMotion, derivativeNormal, derivativePoint, tSample + step) -
                    evaluateContactFunctionAtPoint(twistingMotion, derivativeNormal, derivativePoint, tSample - step)) / (2 * step);
            const disagreement = abs(analytic - central) / (1 + abs(central));
            worstDerivativeDisagreement = max(worstDerivativeDisagreement, disagreement);
        }
        println("[FUNNEL SELF TEST] g_t vs central difference worst relative disagreement: " ~ worstDerivativeDisagreement);
        if (worstDerivativeDisagreement > 1e-7)
        {
            failures = failures ~ " g_t disagrees with the central difference by " ~ worstDerivativeDisagreement ~ ".";
        }

        // Vertex contact intervals: cone normals (1,0,0) and (0,0,1) under the parabolic
        // motion - s_1 goes negative exactly on (0.3, 0.7), s_2 stays positive.
        const vertexIntervals = solveVertexContactIntervals(parabolicMotion,
            [vector(1, 0, 0), vector(0, 0, 1)], vector(0, 0, 0), 0, 1, 16, 1e-13);
        if (size(vertexIntervals) != 1 ||
            abs(vertexIntervals[0].tStart - 0.3) > 1e-9 || abs(vertexIntervals[0].tEnd - 0.7) > 1e-9)
        {
            failures = failures ~ " vertex contact interval expected (0.3, 0.7), got " ~ toString(vertexIntervals) ~ ".";
        }
        else
        {
            println("[FUNNEL SELF TEST] vertex contact interval (" ~ vertexIntervals[0].tStart ~ ", " ~
                vertexIntervals[0].tEnd ~ ")");
        }

        // Strip marching: radial normals around a circle, velocity direction swinging from
        // (1, 0.8) to (-1, 0.8) - g(s, t) = cos(2 pi s)(1 - 2t) + 0.8 sin(2 pi s). The zero
        // set inside the strip is three branches (near s = 0, s = 0.5, s = 1); every root is
        // analytic: t = (1 + 0.8 tan(2 pi s)) / 2.
        const swingMotion = translationMotionFromQuadraticVelocity(
            [1, 0, -1], [0.8, 0.8, 0.8], [0, 0, 0], singleSpanKnots);
        const stripSampleCount = 33;
        var stripNormals = makeArray(stripSampleCount);
        var stripPoints = makeArray(stripSampleCount);
        for (var sampleIndex = 0; sampleIndex < stripSampleCount; sampleIndex += 1)
        {
            const angle = 360 * degree * sampleIndex / (stripSampleCount - 1);
            stripNormals[sampleIndex] = vector(cos(angle), sin(angle), 0);
            stripPoints[sampleIndex] = 0.02 * stripNormals[sampleIndex];
        }
        const stripBranches = marchStripZeroCurves(swingMotion, stripNormals, stripPoints, 0, 1, 21, 1e-12);
        var branchSizes = "";
        var worstStripError = 0;
        for (var branch in stripBranches)
        {
            branchSizes = branchSizes ~ " " ~ size(branch.samples);
            for (var sample in branch.samples)
            {
                const angle = 360 * degree * sample.sampleIndex / (stripSampleCount - 1);
                const exact = (1 + 0.8 * tan(angle)) / 2;
                worstStripError = max(worstStripError, abs(sample.t - exact));
            }
        }
        println("[FUNNEL SELF TEST] strip branches sizes:" ~ branchSizes ~ "; worst root error vs analytic: " ~ worstStripError);
        if (size(stripBranches) != 3 || worstStripError > 1e-9)
        {
            failures = failures ~ " strip marching expected 3 branches matching the analytic roots (sizes" ~
                branchSizes ~ ", worst error " ~ worstStripError ~ ").";
        }

        // Envelope gradient: every component against central differences of the INDEPENDENT
        // pointwise path, on a curved surface under a varying non-orthogonal motion (the
        // rotation terms of f_u and f_v are only exercised here).
        var worstGradientDisagreement = 0;
        for (var gradientProbe in [[0.35, 0.6, 0.45], [0.7, 0.3, 0.2]])
        {
            const gradient = evaluateEnvelopeGradientPointwise(twistingMotion, islandFixtureSurface(),
                gradientProbe[0], gradientProbe[1], gradientProbe[2]);
            const step = 1e-5;
            for (var axisIndex = 0; axisIndex < 3; axisIndex += 1)
            {
                var lowProbe = gradientProbe;
                var highProbe = gradientProbe;
                lowProbe[axisIndex] = lowProbe[axisIndex] - step;
                highProbe[axisIndex] = highProbe[axisIndex] + step;
                const central = (evaluateEnvelopePointwise(twistingMotion, islandFixtureSurface(),
                            highProbe[0], highProbe[1], highProbe[2]) -
                        evaluateEnvelopePointwise(twistingMotion, islandFixtureSurface(),
                            lowProbe[0], lowProbe[1], lowProbe[2])) / (2 * step);
                const analytic = axisIndex == 0 ? gradient.uDerivative :
                    (axisIndex == 1 ? gradient.vDerivative : gradient.tDerivative);
                worstGradientDisagreement = max(worstGradientDisagreement,
                    abs(analytic - central) / (1 + abs(central)));
            }
        }
        println("[FUNNEL SELF TEST] envelope gradient vs central differences worst relative disagreement: " ~
            worstGradientDisagreement);
        if (worstGradientDisagreement > 1e-6)
        {
            failures = failures ~ " envelope gradient disagrees with central differences by " ~
                worstGradientDisagreement ~ ".";
        }

        // Section marching: S = (u, v, 0.15u^2 - 0.18u + 0.05uv) under velocity (1, 0, 0.06)
        // gives f = 0.24 - 0.3u - 0.05v - the section is the exact line u = 0.8 - v/6.
        const slantSurface = slantFixtureSurface();
        const slantMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.06, 0.06], singleSpanKnots);
        const sectionMarch = marchSectionCurve(slantMotion, slantSurface, 0.5,
            [0.8, 0], [0.8 - 1 / 6, 1], { "stepSize" : 0.05, "maxSteps" : 200, "tolerance" : 1e-12 });
        var worstSectionLineError = 0;
        for (var uvPoint in sectionMarch.uvPoints)
        {
            worstSectionLineError = max(worstSectionLineError, abs(0.24 - 0.3 * uvPoint[0] - 0.05 * uvPoint[1]));
        }
        println("[FUNNEL SELF TEST] section march: " ~ size(sectionMarch.uvPoints) ~ " points, reachedEnd " ~
            sectionMarch.reachedEnd ~ ", worst |f| on line: " ~ worstSectionLineError);
        if (!sectionMarch.reachedEnd || worstSectionLineError > 1e-9)
        {
            failures = failures ~ " section marching failed (reachedEnd " ~ sectionMarch.reachedEnd ~
                ", worst |f| " ~ worstSectionLineError ~ ").";
        }

        const resampled = resampleAndPolishSection(slantMotion, slantSurface, 0.5, sectionMarch.uvPoints, 9, 1e-12);
        const expectedFirstLift = vector(1.3, 0, -0.018);
        const firstLiftError = norm(resampled.liftedSamples[0] - expectedFirstLift);
        println("[FUNNEL SELF TEST] section resample: worst residual " ~ resampled.worstResidual ~
            ", first lift error vs hand value: " ~ firstLiftError);
        if (size(resampled.uvSamples) != 9 || resampled.worstResidual > 1e-10 || firstLiftError > 1e-9)
        {
            failures = failures ~ " section resample/lift failed (residual " ~ resampled.worstResidual ~
                ", lift error " ~ firstLiftError ~ ").";
        }

        reportTestVerdict(context, id, "FUNNEL POINTWISE SELF TEST", failures,
            "contact roots, g_t, vertex intervals, strip marching, envelope gradient, and " ~
            "section march/resample/lift all match their analytic answers.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Factored Self Test" }
export const sweepFunnelFactoredSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // Sliding audit: a flat plane under in-plane translation slides everywhere; under
        // normal translation it is screen-dead with no sliding.
        const planeSurface = planeFixtureSurface();
        const planeFactors = buildEnvelopePatchFactors(planeSurface);
        const inPlaneMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0.3, 0.3, 0.3], [0, 0, 0], singleSpanKnots);
        const inPlaneAudit = auditEnvelopeSliding(planeFactors, buildMotionSpanPolynomials(inPlaneMotion), 1e-12);
        const normalMotion = translationMotionFromQuadraticVelocity(
            [0, 0, 0], [0, 0, 0], [1, 1, 1], singleSpanKnots);
        const normalAudit = auditEnvelopeSliding(planeFactors, buildMotionSpanPolynomials(normalMotion), 1e-12);
        println("[FUNNEL SELF TEST] sliding audit: in-plane slides " ~ inPlaneAudit.slides ~
            " (" ~ size(inPlaneAudit.slidingBlocks) ~ " blocks); normal slides " ~ normalAudit.slides ~
            ", live " ~ size(normalAudit.liveBlocks) ~ ", dead " ~ normalAudit.deadBlockCount);
        if (!inPlaneAudit.slides || size(inPlaneAudit.slidingBlocks) != 1)
        {
            failures = failures ~ " in-plane translation not reported as sliding.";
        }
        if (normalAudit.slides || size(normalAudit.liveBlocks) != 0 || normalAudit.deadBlockCount != 1)
        {
            failures = failures ~ " normal translation should be screen-dead with no sliding.";
        }

        // The island fixture: z_u = 0.8 u(1-u) v(1-v) peaks at 0.05 in the patch center;
        // w = (1, 0, wz(t)) with wz = 0.05 + 0.4 (t-0.3)(t-0.7) dips below the peak exactly
        // for t in (0.3, 0.7). f = wz(t) - z_u(u, v): a grazing island born and dying at
        // exactly (u, v, t) = (0.5, 0.5, 0.3) and (0.5, 0.5, 0.7).
        const islandSurface = islandFixtureSurface();
        const islandFactors = buildEnvelopePatchFactors(islandSurface);
        const islandMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.134, -0.066, 0.134], singleSpanKnots);
        const islandSpans = buildMotionSpanPolynomials(islandMotion);
        const islandPatch = islandFactors.patches[0][0];

        // Factored cell isolation: live cells must cover the four exact contact points and
        // stay inside the true t-window (plus screening slack).
        const isolation = isolateEnvelopeCells(islandPatch, islandSpans[0],
            { "minCellWidthU" : 1 / 8, "minCellWidthV" : 1 / 8, "minCellWidthT" : 1 / 8, "valueTolerance" : 0 });
        const contactAnchors = [[0.5, 0.5, 0.3], [0.5, 0.5, 0.7], [0.21716, 0.5, 0.5], [0.78284, 0.5, 0.5]];
        var anchorsCovered = 0;
        for (var anchor in contactAnchors)
        {
            for (var cell in isolation.liveCells)
            {
                if (anchor[0] >= cell.globalUStart - 1e-12 && anchor[0] <= cell.globalUEnd + 1e-12 &&
                    anchor[1] >= cell.globalVStart - 1e-12 && anchor[1] <= cell.globalVEnd + 1e-12 &&
                    anchor[2] >= cell.globalTStart - 1e-12 && anchor[2] <= cell.globalTEnd + 1e-12)
                {
                    anchorsCovered += 1;
                    break;
                }
            }
        }
        var cellsOutsideTimeWindow = 0;
        for (var cell in isolation.liveCells)
        {
            if (cell.globalTEnd < 0.15 || cell.globalTStart > 0.85)
            {
                cellsOutsideTimeWindow += 1;
            }
        }
        println("[FUNNEL SELF TEST] cell isolation: " ~ size(isolation.liveCells) ~ " live cells, " ~
            isolation.screenedCount ~ " screens, " ~ isolation.deadCount ~ " dead; anchors covered " ~
            anchorsCovered ~ "/4; cells outside t-window " ~ cellsOutsideTimeWindow);
        if (anchorsCovered != 4 || cellsOutsideTimeWindow != 0 || size(isolation.liveCells) == 0)
        {
            failures = failures ~ " cell isolation missed contact anchors (" ~ anchorsCovered ~
                "/4) or leaked cells outside the t-window (" ~ cellsOutsideTimeWindow ~ ").";
        }

        // Subdivision consistency at the block level: the native split-matrix children must
        // reproduce the parent exactly.
        const islandBlock = materializeEnvelopeBlock(buildEnvelopePatchProducts(islandPatch), islandSpans[0]);
        var worstSplitDisagreement = 0;
        for (var m = 0; m < size(islandBlock); m += 1)
        {
            const uSplit = subdivideBernsteinGridU(islandBlock[m], 0.5);
            const vSplit = subdivideBernsteinGridV(islandBlock[m], 0.5);
            for (var probePair in [[0.31, 0.77], [0.62, 0.18]])
            {
                // low(x) reproduces parent(x / 2); high(y) reproduces parent(0.5 + y / 2).
                worstSplitDisagreement = max(worstSplitDisagreement,
                    abs(evaluateBernsteinGrid(uSplit.low, probePair[0], probePair[1]) -
                        evaluateBernsteinGrid(islandBlock[m], probePair[0] / 2, probePair[1])));
                worstSplitDisagreement = max(worstSplitDisagreement,
                    abs(evaluateBernsteinGrid(vSplit.high, probePair[0], probePair[1]) -
                        evaluateBernsteinGrid(islandBlock[m], probePair[0], 0.5 + probePair[1] / 2)));
            }
        }
        println("[FUNNEL SELF TEST] block-level subdivision consistency: " ~ worstSplitDisagreement);
        if (worstSplitDisagreement > 1e-13)
        {
            failures = failures ~ " native grid subdivision disagrees with the parent by " ~ worstSplitDisagreement ~ ".";
        }

        // Island t-extremes: exact coefficient-net Newton must land on (0.5, 0.5, 0.3) and
        // (0.5, 0.5, 0.7) from nearby seeds, and the results must satisfy the INDEPENDENT
        // pointwise envelope function to machine precision.
        var worstExtremeError = 0;
        var worstExtremeResidual = 0;
        for (var extremeCase in [{ "seed" : [0.46, 0.55, 0.34], "expected" : [0.5, 0.5, 0.3] },
                { "seed" : [0.54, 0.46, 0.66], "expected" : [0.5, 0.5, 0.7] }])
        {
            const refined = refineBlockStationaryPoint(islandBlock,
                extremeCase.seed[0], extremeCase.seed[1], extremeCase.seed[2],
                { "iterationLimit" : 30, "stepTolerance" : 1e-13 });
            if (!refined.converged)
            {
                failures = failures ~ " island extreme Newton failed to converge from seed " ~
                    toString(extremeCase.seed) ~ ".";
                continue;
            }
            for (var axisIndex = 0; axisIndex < 3; axisIndex += 1)
            {
                const refinedValue = axisIndex == 0 ? refined.localU : (axisIndex == 1 ? refined.localV : refined.localT);
                worstExtremeError = max(worstExtremeError, abs(refinedValue - extremeCase.expected[axisIndex]));
            }
            worstExtremeResidual = max(worstExtremeResidual,
                abs(evaluateEnvelopePointwise(islandMotion, islandSurface, refined.localU, refined.localV, refined.localT)));
        }
        println("[FUNNEL SELF TEST] island t-extremes: worst coordinate error " ~ worstExtremeError ~
            ", worst pointwise |f| " ~ worstExtremeResidual);
        if (worstExtremeError > 1e-9 || worstExtremeResidual > 1e-12)
        {
            failures = failures ~ " island t-extreme refinement missed (coordinate error " ~ worstExtremeError ~
                ", pointwise residual " ~ worstExtremeResidual ~ ").";
        }

        reportTestVerdict(context, id, "FUNNEL FACTORED SELF TEST", failures,
            "sliding audit, factored cell isolation, native subdivision, and island " ~
            "t-extremes all match their analytic answers.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Census Self Test" }
export const sweepFunnelCensusSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // The same island fixture as the factored self test (see there for the algebra).
        const islandSurface = islandFixtureSurface();
        const islandFactors = buildEnvelopePatchFactors(islandSurface);
        const islandMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.134, -0.066, 0.134], singleSpanKnots);
        const islandSpans = buildMotionSpanPolynomials(islandMotion);

        // Census: the island fixture has exactly one component, a true grazing island.
        const censusOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 17,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false };
        const islandCensus = censusFunnelComponents(islandFactors, islandSpans, censusOptions);
        println("[FUNNEL SELF TEST] island census: " ~ size(islandCensus.components) ~ " component(s)" ~
            (size(islandCensus.components) == 1 ? (", isIsland " ~ islandCensus.components[0].isIsland) : ""));
        if (size(islandCensus.components) != 1 || !islandCensus.components[0].isIsland)
        {
            failures = failures ~ " island census expected exactly one island component.";
        }

        // Census with a square trim mask [0.35, 0.65]^2: the island's mid-life loop lies
        // entirely outside the valid square, so the shell splits into two trim-touching caps.
        var squareLoop = [[0.35, 0.35], [0.65, 0.35], [0.65, 0.65], [0.35, 0.65]];
        var trimmedOptions = censusOptions;
        trimmedOptions.trimLoops = [squareLoop];
        const trimmedCensus = censusFunnelComponents(islandFactors, islandSpans, trimmedOptions);
        var trimTouchCount = 0;
        var trimmedIslandCount = 0;
        for (var componentRecord in trimmedCensus.components)
        {
            if (componentRecord.touchesTrimBoundary)
            {
                trimTouchCount += 1;
            }
            if (componentRecord.isIsland)
            {
                trimmedIslandCount += 1;
            }
        }
        println("[FUNNEL SELF TEST] trimmed census: " ~ size(trimmedCensus.components) ~
            " components, " ~ trimTouchCount ~ " trim-touching, " ~ trimmedIslandCount ~ " islands");
        if (size(trimmedCensus.components) != 2 || trimTouchCount != 2 || trimmedIslandCount != 0)
        {
            failures = failures ~ " trimmed census expected two trim-touching caps.";
        }

        // Winding-number spot checks on a concave polygon.
        const concaveLoop = [[0, 0], [1, 0], [1, 1], [0.5, 0.3], [0, 1]];
        if (!uvPointInsideLoops([concaveLoop], 0.1, 0.1) || !uvPointInsideLoops([concaveLoop], 0.25, 0.5) ||
            uvPointInsideLoops([concaveLoop], 0.5, 0.9) || uvPointInsideLoops([concaveLoop], 2, 0.5))
        {
            failures = failures ~ " even-odd point classification failed on the concave polygon.";
        }

        // Periodic seam: z_u = 0.8 (u - 0.5)^2 v(1-v) under constant w = (1, 0, 0.02) puts one
        // contact lobe against each u edge (the same physical band on a closed face). Without
        // the wrap the census sees two boundary components; with uPeriodic they are ONE
        // seam-crossing component.
        const seamSurface = seamFixtureSurface();
        const seamFactors = buildEnvelopePatchFactors(seamSurface);
        const seamMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.02, 0.02, 0.02], singleSpanKnots);
        const seamSpans = buildMotionSpanPolynomials(seamMotion);
        // The seam fixture is t-constant, so a coarse t axis suffices.
        var seamOptions = censusOptions;
        seamOptions.tNodesPerSpan = 5;
        const openCensus = censusFunnelComponents(seamFactors, seamSpans, seamOptions);
        var periodicOptions = seamOptions;
        periodicOptions.uPeriodic = true;
        const wrappedCensus = censusFunnelComponents(seamFactors, seamSpans, periodicOptions);
        println("[FUNNEL SELF TEST] seam census: open " ~ size(openCensus.components) ~
            " component(s); wrapped " ~ size(wrappedCensus.components) ~ " component(s)" ~
            (size(wrappedCensus.components) == 1 ? (", crossesUSeam " ~ wrappedCensus.components[0].crossesUSeam) : ""));
        if (size(openCensus.components) != 2 || size(wrappedCensus.components) != 1 ||
            !wrappedCensus.components[0].crossesUSeam)
        {
            failures = failures ~ " periodic seam census expected 2 open / 1 wrapped seam-crossing component.";
        }

        // A contact set lying exactly ON a grid node line (spec 6.7). f = 0.03 (0.5 - u)
        // here, so the whole contact set is the plane u = 0.5 - and on a nine-node grid that
        // plane IS the middle node line. In closed form f is zero along it; out of the
        // coefficient path it is ~1e-18 with a sign that is pure rounding, so with an absolute
        // zero threshold each v node decided independently whether the cell columns either
        // side of the line were sign-mixed and the slab came out a ragged 80 cells of 128.
        // The threshold now comes from the block's own value range, and no caller supplies a
        // number at all.
        const nodeContactFactors = buildEnvelopePatchFactors(nodeContactFixtureSurface());
        const nodeContactSpans = buildMotionSpanPolynomials(
            constantVelocityTranslationMotion(vector(1, 0, 0.5)));
        const nodeContactCensus = censusFunnelComponents(nodeContactFactors, nodeContactSpans,
                { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 9,
                        "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false });
        const nodeContactCells = size(nodeContactCensus.components) == 1 ?
            nodeContactCensus.components[0].cellCount : 0;
        println("[FUNNEL SELF TEST] node-contact census: " ~ size(nodeContactCensus.components) ~
            " component(s), " ~ nodeContactCells ~ " cells (closed form: one 128-cell slab); " ~
            "derived sign tolerance " ~ nodeContactCensus.signTolerance.minimum ~ ", " ~
            nodeContactCensus.signTolerance.zeroSignNodeCount ~ " zero-sign node(s) of 729");
        // The zero-sign nodes are exactly the u = 0.5 plane: 9 v nodes x 9 t nodes.
        if (size(nodeContactCensus.components) != 1 || nodeContactCells != 128 ||
            nodeContactCensus.signTolerance.zeroSignNodeCount != 81)
        {
            failures = failures ~ " the node-landing contact set was expected to census as one " ~
                "128-cell slab with 81 zero-sign nodes, got " ~ size(nodeContactCensus.components) ~
                " component(s), " ~ nodeContactCells ~ " cells, " ~
                nodeContactCensus.signTolerance.zeroSignNodeCount ~ " zero-sign node(s).";
        }
        if (nodeContactCensus.signTolerance.minimum <= 0)
        {
            failures = failures ~ " the census derived no sign tolerance from the block range.";
        }

        reportTestVerdict(context, id, "FUNNEL CENSUS SELF TEST", failures,
            "census components (plain island, trimmed caps, periodic seam wrap), a contact " ~
            "set landing on a node line, and the even-odd trim classification all match " ~
            "their analytic answers.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Certified Census Self Test" }
export const sweepFunnelCertifiedCensusSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();

        // ---- The rotation-dominant station: b' is exactly zero, so there is no translation
        // direction at all. f = 0.6 x (y - t x) with x = u + 1, y = v + 0.3.
        const rotationSurface = rotationFixtureSurface();
        const rotationFactors = buildEnvelopePatchFactors(rotationSurface);
        const rotationMotion = firstOrderRotationMotion(vector(0, 0, 1));
        const rotationSpans = buildMotionSpanPolynomials(rotationMotion);

        var translationDotsZero = true;
        for (var i = 0; i < 3; i += 1)
        {
            for (var coefficient in rotationSpans[0].translationDots[i])
            {
                translationDotsZero = translationDotsZero && coefficient == 0;
            }
        }
        tally = checkThat(tally, translationDotsZero,
            "the rotation fixture's translation dot polynomials are not all exactly zero, so " ~
            "|b'| is not exactly zero and the fixture does not test what it is for.");

        const rotationBlock = materializeEnvelopeBlock(
            buildEnvelopePatchProducts(rotationFactors.patches[0][0]), rotationSpans[0]);
        var worstClosedForm = 0;
        var worstPointwise = 0;
        for (var probe in [[0, 0, 0], [0.3, 0.7, 0.4], [0.5, 0.5, 0.5], [1, 1, 1],
                    [0.25, 0.125, 0.875], [0.9, 0.2, 0.35]])
        {
            const x = probe[0] + 1;
            const y = probe[1] + 0.3;
            const exact = 0.6 * x * (y - probe[2] * x);
            worstClosedForm = max(worstClosedForm,
                abs(evaluateMaterializedBlock(rotationBlock, probe[0], probe[1], probe[2]) - exact));
            worstPointwise = max(worstPointwise,
                abs(evaluateEnvelopePointwise(rotationMotion, rotationSurface, probe[0], probe[1], probe[2]) - exact));
        }
        println("[FUNNEL CERTIFIED CENSUS] rotation f vs 0.6 x (y - t x): coefficient path " ~
            worstClosedForm ~ ", pointwise path " ~ worstPointwise);
        tally = checkWithin(tally, worstClosedForm, 1e-14, "the coefficient path's f against the closed form");
        tally = checkWithin(tally, worstPointwise, 1e-14, "the pointwise path's f against the closed form");

        // Sliding under rotation, both ways round: the fixture grazes, and a plane rotating
        // about its own normal slides - the case with no translation direction to imprint an
        // isocline along even in principle.
        const rotationAudit = auditEnvelopeSliding(rotationFactors, rotationSpans, 1e-12);
        const planeFactors = buildEnvelopePatchFactors(planeFixtureSurface());
        const planeAudit = auditEnvelopeSliding(planeFactors, rotationSpans, 1e-12);
        const planeBlock = materializeEnvelopeBlock(
            buildEnvelopePatchProducts(planeFactors.patches[0][0]), rotationSpans[0]);
        var planeCoefficientsZero = true;
        for (var grid in planeBlock)
        {
            for (var row in grid)
            {
                for (var coefficient in row)
                {
                    planeCoefficientsZero = planeCoefficientsZero && coefficient == 0;
                }
            }
        }
        println("[FUNNEL CERTIFIED CENSUS] rotation sliding audit: fixture slides " ~
            rotationAudit.slides ~ " (live " ~ size(rotationAudit.liveBlocks) ~ "); plane slides " ~
            planeAudit.slides ~ " (" ~ size(planeAudit.slidingBlocks) ~ " block(s)), every plane " ~
            "coefficient exactly zero " ~ planeCoefficientsZero);
        tally = checkThat(tally, !rotationAudit.slides && size(rotationAudit.liveBlocks) == 1,
            "the rotation fixture should be one live non-sliding block.");
        tally = checkThat(tally, planeAudit.slides && size(planeAudit.slidingBlocks) == 1,
            "a plane rotating about its own normal axis should be reported as sliding.");
        tally = checkThat(tally, planeCoefficientsZero,
            "the sliding plane's block coefficients should be exactly zero, not merely small.");

        // ---- The certified census on the rotation fixture. One component: it is born inside
        // the t range (the contact line enters the domain at t = 0.15) and is still alive at
        // t = 1, and it reaches the uv boundary throughout.
        const baseOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 17,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false };
        const rotationCensus = censusFunnelComponentsCertified(rotationFactors, rotationSpans, baseOptions);
        printCensusPasses("rotation", rotationCensus);
        tally = checkThat(tally, rotationCensus.certified && rotationCensus.refinements == 1 &&
            rotationCensus.resolution.tNodesPerSpan == 33,
            "the rotation census should certify after exactly one refinement, at 17/17/33.");
        tally = checkThat(tally, size(rotationCensus.components) == 1,
            "the rotation census should find exactly one component, got " ~
            size(rotationCensus.components) ~ ".");
        if (size(rotationCensus.components) == 1)
        {
            const rotationComponent = rotationCensus.components[0];
            tally = checkThat(tally, !rotationComponent.isIsland && !rotationComponent.touchesTStart &&
                rotationComponent.touchesTEnd && rotationComponent.touchesDomainBoundaryUv &&
                !rotationComponent.crossesUSeam,
                "the rotation component's flags should be born-inside / alive-at-tEnd / uv-touching.");
            tally = checkWithin(tally, rotationComponent.parameterBounds.tMin - 0.125, 1e-12,
                "the rotation component's birth cell (the contact line enters at t = 0.15, so the " ~
                "cell below it starts at 0.125)");
            tally = checkWithin(tally, rotationComponent.parameterBounds.tMax - 1, 1e-12,
                "the rotation component's death t");
        }
        tally = checkThat(tally, rotationCensus.coverage.contradictions == 0,
            "the interval screen and the pointwise block evaluation disagreed on where f can " ~
            "vanish (" ~ rotationCensus.coverage.contradictions ~ " mixed cells outside every live cell).");

        reportCheckTally(context, id, "FUNNEL CERTIFIED CENSUS SELF TEST", tally,
            "a rotation-dominant station with |b'| exactly zero censuses and certifies with no " ~
            "kernel oracle, and sliding is exact under rotation both ways round.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Census Certificate Self Test" }
export const sweepFunnelCensusCertificateSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const baseOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 17,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false };

        // ---- The island fixture: the same loop on a known grazing island, where the interval
        // screen is exactly as tight as the sign grid - every live cell is a mixed cell.
        const islandFactors = buildEnvelopePatchFactors(islandFixtureSurface());
        const islandSpans = buildMotionSpanPolynomials(translationMotionFromQuadraticVelocity(
                [1, 1, 1], [0, 0, 0], [0.134, -0.066, 0.134]));
        const islandCensus = censusFunnelComponentsCertified(islandFactors, islandSpans, baseOptions);
        printCensusPasses("island", islandCensus);
        tally = checkThat(tally, islandCensus.certified && islandCensus.refinements == 1 &&
            size(islandCensus.components) == 1 && islandCensus.components[0].isIsland,
            "the island census should certify after one refinement as exactly one island.");
        var islandScreenTight = true;
        for (var pass in islandCensus.passes)
        {
            islandScreenTight = islandScreenTight && pass.liveCellCount == pass.mixedCellCount;
        }
        tally = checkThat(tally, islandScreenTight,
            "on the island fixture every live cell should also be sign-mixed - the interval " ~
            "screen is exactly as tight as the sign grid there.");

        // ---- Why the stability half is not optional: on the seam fixture's two lobes a 3/3/3
        // grid reads ONE component, and coverage certifies it, because both lobes share a
        // single live region at that cell size. Only refinement separates them.
        const seamFactors = buildEnvelopePatchFactors(seamFixtureSurface());
        const seamSpans = buildMotionSpanPolynomials(translationMotionFromQuadraticVelocity(
                [1, 1, 1], [0, 0, 0], [0.02, 0.02, 0.02]));
        var coarseOptions = baseOptions;
        coarseOptions.uNodesPerPatch = 3;
        coarseOptions.vNodesPerPatch = 3;
        coarseOptions.tNodesPerSpan = 3;
        coarseOptions.minimumNodesPerPatch = 3;
        coarseOptions.minimumNodesPerSpan = 3;
        const seamCensus = censusFunnelComponentsCertified(seamFactors, seamSpans, coarseOptions);
        printCensusPasses("seam from 3/3/3", seamCensus);
        tally = checkThat(tally, size(seamCensus.passes) >= 2 && seamCensus.passes[0].componentCount == 1 &&
            seamCensus.passes[0].unresolvedRegionCount == 0,
            "the seam fixture's first pass should merge both lobes into one COVERAGE-CLEAN " ~
            "component - that is the case the stability half exists to catch.");
        tally = checkThat(tally, seamCensus.certified && size(seamCensus.components) == 2 &&
            seamCensus.refinements == 2,
            "the seam census should separate into two components and certify after two refinements.");

        // ---- And the limit of the stability half, pinned rather than described: two
        // consecutive resolutions that are both too coarse agree with each other and are wrong
        // together. Here the grazing island is reported as reaching the uv boundary, and the
        // loop certifies it. minimumNodesPerPatch is what keeps a caller out of this.
        var coarseIslandOptions = coarseOptions;
        coarseIslandOptions.tNodesPerSpan = 5;
        coarseIslandOptions.maxRefinements = 1;
        const coarseIslandCensus = censusFunnelComponentsCertified(islandFactors, islandSpans, coarseIslandOptions);
        printCensusPasses("island from 3/3/5", coarseIslandCensus);
        tally = checkThat(tally, coarseIslandCensus.certified &&
            size(coarseIslandCensus.components) == 1 && !coarseIslandCensus.components[0].isIsland,
            "the too-coarse island run should certify a WRONG answer (island reported as " ~
            "uv-touching) - if it no longer does, the floor's justification has changed.");

        reportCheckTally(context, id, "FUNNEL CENSUS CERTIFICATE SELF TEST", tally,
            "coverage and stability each catch what the other cannot: the screen is exactly as " ~
            "tight as the sign grid on the island, a coarse grid merges the seam's two lobes " ~
            "past a clean coverage proof, and two coarse passes can agree while both are wrong.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Mask Self Test" }
export const sweepFunnelMaskSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // The two census masks: the cyclic trim mask a closed face needs, and the degeneracy
        // mask that keeps a collapsed net boundary from joining every component through itself.
        // Selection-free; the fixtures' answers are all closed-form. Split from the census self
        // test so each feature's step count stays inside one regeneration budget.
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // Two trim loops that WIND the u seam, bounding the band 0.25 + 0.05 sin(2 pi u) to 0.75.
        // Both are given as { points, winding } records, which is what buildFaceTrimLoops emits.
        const lowerTrim = { "points" : sinusoidalSeamLoop(0.25, 0.05, 32), "winding" : 1 };
        const upperTrim = { "points" : sinusoidalSeamLoop(0.75, 0, 32), "winding" : 1 };
        const band = [lowerTrim, upperTrim];
        var bandFailures = 0;
        for (var u in [0, 0.2, 0.5, 0.95])
        {
            if (!uvPointInsideLoopsCyclic(band, u, 0.5, 1))
            {
                bandFailures += 1;
            }
            if (uvPointInsideLoopsCyclic(band, u, 0.05, 1) || uvPointInsideLoopsCyclic(band, u, 0.95, 1))
            {
                bandFailures += 1;
            }
        }
        // At u = 0.25 the wavy trim sits at exactly 0.30, so 0.28 is out and 0.32 is in - the
        // mask resolves the loop's shape, not just its average height.
        if (uvPointInsideLoopsCyclic(band, 0.25, 0.28, 1) || !uvPointInsideLoopsCyclic(band, 0.25, 0.32, 1))
        {
            bandFailures += 1;
        }
        println("[FUNNEL MASK SELF TEST] cyclic band: " ~ bandFailures ~ " misclassification(s) of 13");
        if (bandFailures != 0)
        {
            failures = failures ~ " the cyclic band mask misclassified " ~ bandFailures ~ " point(s).";
        }

        // A hole centred on the seam, unwrapped to u in [-0.06, 0.06] and handed over as a bare
        // point array - the census reads that shape as a closed loop of winding zero. It must
        // mask from BOTH sides of the seam, which is what the periodic images are for.
        const seamHole = seamStraddlingHole(0.06, 24);
        const bandWithHole = [lowerTrim, upperTrim, seamHole];
        var holeFailures = 0;
        for (var u in [0, 0.03, 0.97, 0.999])
        {
            if (uvPointInsideLoopsCyclic(bandWithHole, u, 0.5, 1))
            {
                holeFailures += 1;
            }
        }
        for (var u in [0.2, 0.5, 0.8])
        {
            if (!uvPointInsideLoopsCyclic(bandWithHole, u, 0.5, 1))
            {
                holeFailures += 1;
            }
        }
        println("[FUNNEL MASK SELF TEST] seam-straddling hole: " ~ holeFailures ~
            " misclassification(s) of 7");
        if (holeFailures != 0)
        {
            failures = failures ~ " the seam-straddling hole was misclassified at " ~ holeFailures ~
                " point(s).";
        }

        // The seam fixture again (see the census self test for its algebra), now masked. First a
        // band that excludes nothing: every node runs through the crossing test and not one
        // verdict may change.
        const seamSurface = seamFixtureSurface();
        const seamFactors = buildEnvelopePatchFactors(seamSurface);
        const seamSpans = buildMotionSpanPolynomials(translationMotionFromQuadraticVelocity(
                    [1, 1, 1], [0, 0, 0], [0.02, 0.02, 0.02], singleSpanKnots));
        var seamOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 5,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : true };
        const unmasked = censusFunnelComponents(seamFactors, seamSpans, seamOptions);
        var wideOptions = seamOptions;
        wideOptions.trimLoops = [{ "points" : sinusoidalSeamLoop(-0.1, 0, 32), "winding" : 1 },
                { "points" : sinusoidalSeamLoop(1.1, 0, 32), "winding" : 1 }];
        const wideMasked = censusFunnelComponents(seamFactors, seamSpans, wideOptions);
        println("[FUNNEL MASK SELF TEST] seam fixture unmasked: " ~ size(unmasked.components) ~
            " component(s), " ~ unmasked.components[0].cellCount ~ " cells; band excluding nothing: " ~
            size(wideMasked.components) ~ " component(s), " ~
            (size(wideMasked.components) == 1 ? (wideMasked.components[0].cellCount ~ " cells, trim " ~
                    wideMasked.components[0].touchesTrimBoundary ~ ", seam " ~
                    wideMasked.components[0].crossesUSeam) : ""));
        if (size(unmasked.components) != 1 || unmasked.components[0].cellCount != 80 ||
            size(wideMasked.components) != 1 || wideMasked.components[0].cellCount != 80 ||
            wideMasked.components[0].touchesTrimBoundary || !wideMasked.components[0].crossesUSeam)
        {
            failures = failures ~ " a trim band that excludes nothing changed the seam census.";
        }

        // Now a band that cuts: v in [0.3, 0.7] keeps only the two mid-v arcs of the contact
        // curve, which no longer meet across the seam - one wrapped component becomes two
        // trim-touching ones.
        var tightOptions = seamOptions;
        tightOptions.trimLoops = [{ "points" : sinusoidalSeamLoop(0.3, 0, 32), "winding" : 1 },
                { "points" : sinusoidalSeamLoop(0.7, 0, 32), "winding" : 1 }];
        const tightMasked = censusFunnelComponents(seamFactors, seamSpans, tightOptions);
        var tightTrimTouching = 0;
        var tightSeamCrossing = 0;
        var tightCells = 0;
        for (var component in tightMasked.components)
        {
            tightTrimTouching += component.touchesTrimBoundary ? 1 : 0;
            tightSeamCrossing += component.crossesUSeam ? 1 : 0;
            tightCells += component.cellCount;
        }
        println("[FUNNEL MASK SELF TEST] band [0.3, 0.7]: " ~ size(tightMasked.components) ~
            " component(s), " ~ tightCells ~ " cells total, " ~ tightTrimTouching ~
            " trim-touching, " ~ tightSeamCrossing ~ " seam-crossing");
        if (size(tightMasked.components) != 2 || tightCells != 16 || tightTrimTouching != 2 ||
            tightSeamCrossing != 0)
        {
            failures = failures ~ " the cutting trim band did not split the seam component in two.";
        }

        // The degeneracy mask. On the pole fixture f = v (1 - a(t) c'(u)) with c' = 0.6 u (1 - u)
        // and a(t) = 8 + 12 t, so the contact set is two sheets at u = 0.5 +/- sqrt(0.25 - 1 /
        // (0.6 a)) - well inside the domain and separate everywhere except v = 0, where the
        // collapsed control row makes f vanish identically. Unmasked, that zero slab joins them.
        const poleSurface = poleFixtureSurface();
        const poleFactors = buildEnvelopePatchFactors(poleSurface);
        const poleSpans = buildMotionSpanPolynomials(translationMotionFromQuadraticVelocity(
                    [8, 14, 20], [0, 0, 0], [1, 1, 1], singleSpanKnots));
        var poleOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 9,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false };
        const poleUnmasked = censusFunnelComponents(poleFactors, poleSpans, poleOptions);
        var poleMaskedOptions = poleOptions;
        poleMaskedOptions.degenerate = { "uStart" : false, "uEnd" : false, "vStart" : true, "vEnd" : false };
        const poleMasked = censusFunnelComponents(poleFactors, poleSpans, poleMaskedOptions);
        var poleCells = "";
        var poleReported = 0;
        var poleIslands = 0;
        for (var component in poleMasked.components)
        {
            poleCells = poleCells ~ " " ~ component.cellCount;
            poleReported += component.touchesDegenerateBoundary ? 1 : 0;
            poleIslands += component.isIsland ? 1 : 0;
        }
        println("[FUNNEL MASK SELF TEST] pole fixture unmasked: " ~ size(poleUnmasked.components) ~
            " component(s), " ~ poleUnmasked.components[0].cellCount ~ " cells; masked: " ~
            size(poleMasked.components) ~ " component(s), cells" ~ poleCells ~ ", " ~ poleReported ~
            " reporting the pole, " ~ poleIslands ~ " island(s)");
        if (size(poleUnmasked.components) != 1 || poleUnmasked.components[0].cellCount != 204)
        {
            failures = failures ~ " the unmasked pole fixture was expected to flood into one " ~
                "204-cell component.";
        }
        if (size(poleMasked.components) != 2 || poleReported != 2 || poleIslands != 0 ||
            poleMasked.components[0].cellCount != 70 || poleMasked.components[1].cellCount != 70)
        {
            failures = failures ~ " the degeneracy mask did not split the pole fixture into two " ~
                "70-cell sheets.";
        }

        reportTestVerdict(context, id, "FUNNEL MASK SELF TEST", failures,
            "the cyclic trim mask (band, wave, seam-straddling hole), a trim band that " ~
            "excludes nothing, a band that cuts, and the degeneracy mask all match their " ~
            "closed-form answers.");
    });

annotation { "Feature Type Name" : "Sweep Emit - Extraction Self Test" }
export const sweepEmitExtractionSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // Three fixtures cover the three extraction paths: a cylinder solid (analytic classes,
        // no spline), a created bicubic patch (exact B-spline class), and an elliptical extrude
        // (no analytic class, storage-typed surface - the approximation path with trim loops).
        var failures = "";

        // Fixture 1: cylinder solid - expect two PLANE records and one CYLINDER record.
        fCylinder(context, id + "cylinder", {
                    "bottomCenter" : vector(0, 0, 0) * meter,
                    "topCenter" : vector(0, 0, 0.06) * meter,
                    "radius" : 0.02 * meter
                });
        const cylinderRecords = extractToolFaceRecords(context, qCreatedBy(id + "cylinder", EntityType.BODY), 1e-6);
        println("[EMIT SELF TEST] cylinder solid: " ~ summarizeFaceRecords(cylinderRecords));
        var planeCount = 0;
        var cylinderCount = 0;
        var cylinderWallIsPeriodic = false;
        for (var record in cylinderRecords)
        {
            if (record.surfaceClass == SweepSurfaceClass.PLANE)
            {
                planeCount += 1;
            }
            if (record.surfaceClass == SweepSurfaceClass.CYLINDER)
            {
                cylinderCount += 1;
                cylinderWallIsPeriodic = record.periodic[0] || record.periodic[1];
            }
        }
        if (planeCount != 2 || cylinderCount != 1)
        {
            failures = failures ~ " cylinder solid classified as " ~ planeCount ~ " PLANE / " ~
                cylinderCount ~ " CYLINDER (expected 2 / 1).";
        }
        if (!cylinderWallIsPeriodic)
        {
            failures = failures ~ " cylinder wall not reported periodic.";
        }

        // Fixture 2: created bicubic patch - expect one exact BSPLINE record whose calibration
        // is affine, and a clean inversion round trip.
        const rows = wavyBicubicPatchNet();
        opCreateBSplineSurface(context, id + "patch", {
                    "bSplineSurface" : bSplineSurface({
                                "uDegree" : 3,
                                "vDegree" : 3,
                                "isUPeriodic" : false,
                                "isVPeriodic" : false,
                                "controlPoints" : controlPointMatrix(rows)
                            })
                });
        const patchRecords = extractToolFaceRecords(context, qCreatedBy(id + "patch", EntityType.BODY), 1e-6);
        println("[EMIT SELF TEST] bicubic patch: " ~ summarizeFaceRecords(patchRecords));
        if (size(patchRecords) != 1 || patchRecords[0].surfaceClass != SweepSurfaceClass.BSPLINE ||
            !patchRecords[0].splineIsExact)
        {
            failures = failures ~ " patch did not extract as one exact BSPLINE record.";
        }
        else
        {
            if (patchRecords[0].calibration.isAffine != true)
            {
                failures = failures ~ " patch calibration not affine (residual " ~
                    patchRecords[0].calibration.maxFitResidual ~ " m).";
            }
            const patchInversion = inversionRoundTripWorstCase(patchRecords[0].spline);
            println("[EMIT SELF TEST] patch inversion round trip: worst 3D residual " ~
                patchInversion.worstResidual ~ " m in " ~ patchInversion.worstIterations ~ " iterations");
            if (patchInversion.worstResidual > 1e-9)
            {
                failures = failures ~ " patch inversion residual " ~ patchInversion.worstResidual ~ " m.";
            }
        }

        // Fixture 3: elliptical extrude - the wall has no analytic class and no spline storage,
        // so it must take the approximation path and carry trim loops; its calibration verdict
        // (affine or not) is recorded by the printout either way, and inversion must hold
        // regardless.
        const sketchId = id + "ellipseSketch";
        const ellipseSketch = newSketchOnPlane(context, sketchId, {
                    "sketchPlane" : plane(vector(0.25, 0, 0) * meter, vector(0, 0, 1))
                });
        skEllipse(ellipseSketch, "ellipse1", {
                    "center" : vector(0, 0) * meter,
                    "majorRadius" : 0.03 * meter,
                    "minorRadius" : 0.015 * meter
                });
        skSolve(ellipseSketch);
        opExtrude(context, id + "ellipseExtrude", {
                    "entities" : qSketchRegion(sketchId),
                    "direction" : vector(0, 0, 1),
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : 0.05 * meter
                });
        opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

        const extrudeRecords = extractToolFaceRecords(context, qCreatedBy(id + "ellipseExtrude", EntityType.BODY), 1e-6);
        println("[EMIT SELF TEST] elliptical extrude: " ~ summarizeFaceRecords(extrudeRecords));
        var wallRecord = undefined;
        for (var record in extrudeRecords)
        {
            if (record.surfaceClass != SweepSurfaceClass.PLANE)
            {
                wallRecord = record;
            }
        }
        if (wallRecord == undefined || wallRecord.surfaceClass != SweepSurfaceClass.OTHER ||
            wallRecord.spline == undefined)
        {
            failures = failures ~ " elliptical wall did not take the approximation path.";
        }
        else
        {
            println("[EMIT SELF TEST] elliptical wall spline: " ~ describeSurfaceShape(wallRecord.spline));
            println("[EMIT SELF TEST] elliptical wall calibration: isAffine " ~
                wallRecord.calibration.isAffine ~ ", max fit residual " ~
                wallRecord.calibration.maxFitResidual ~ " m, usable samples " ~
                wallRecord.calibration.usableSampleCount ~ "/" ~ wallRecord.calibration.sampleCount);
            const wallInversion = inversionRoundTripWorstCase(wallRecord.spline);
            println("[EMIT SELF TEST] elliptical wall inversion round trip: worst 3D residual " ~
                wallInversion.worstResidual ~ " m in " ~ wallInversion.worstIterations ~ " iterations");
            if (wallInversion.worstResidual > 1e-9)
            {
                failures = failures ~ " elliptical wall inversion residual " ~ wallInversion.worstResidual ~ " m.";
            }
            if (wallRecord.calibration.usableSampleCount < 4)
            {
                failures = failures ~ " elliptical wall calibration paired only " ~
                    wallRecord.calibration.usableSampleCount ~ " samples.";
            }
        }

        // Phase B: co-edge and vertex records over the same fixtures, plus a box whose
        // vertices exercise adjacency indexing and cone-normal assembly.
        const cylinderCoEdges = extractCoEdgeRecords(context, qCreatedBy(id + "cylinder", EntityType.BODY), cylinderRecords, 9);
        println("[EMIT SELF TEST] cylinder co-edges: " ~ summarizeCoEdgeRecords(cylinderCoEdges));
        if (size(cylinderCoEdges) != 2)
        {
            failures = failures ~ " cylinder produced " ~ size(cylinderCoEdges) ~ " co-edges (expected 2).";
        }
        for (var record in cylinderCoEdges)
        {
            if (record.curveClass != SweepCurveClass.CIRCLE || record.convexity != EdgeConvexityType.CONVEX ||
                record.faceIndexLeft == undefined || record.faceIndexRight == undefined)
            {
                failures = failures ~ " cylinder edge " ~ record.edgeIndex ~ " not a two-sided convex circle.";
            }
        }

        const patchCoEdges = extractCoEdgeRecords(context, qCreatedBy(id + "patch", EntityType.BODY), patchRecords, 9);
        println("[EMIT SELF TEST] patch co-edges: " ~ summarizeCoEdgeRecords(patchCoEdges));
        if (size(patchCoEdges) != 4)
        {
            failures = failures ~ " patch produced " ~ size(patchCoEdges) ~ " co-edges (expected 4).";
        }
        for (var record in patchCoEdges)
        {
            const sideCount = (record.faceIndexLeft == undefined ? 0 : 1) + (record.faceIndexRight == undefined ? 0 : 1);
            const pcurve = record.uvCurves.left != undefined ? record.uvCurves.left : record.uvCurves.right;
            if (sideCount != 1 || pcurve == undefined)
            {
                failures = failures ~ " patch edge " ~ record.edgeIndex ~ " not one-sided with a pcurve.";
            }
            else if (pcurve.maxResidual > 1e-8)
            {
                failures = failures ~ " patch edge " ~ record.edgeIndex ~ " pcurve residual " ~ pcurve.maxResidual ~ " m.";
            }
        }

        if (wallRecord != undefined)
        {
            const extrudeCoEdges = extractCoEdgeRecords(context, qCreatedBy(id + "ellipseExtrude", EntityType.BODY), extrudeRecords, 9);
            println("[EMIT SELF TEST] extrude co-edges: " ~ summarizeCoEdgeRecords(extrudeCoEdges));
            if (size(extrudeCoEdges) != 2)
            {
                failures = failures ~ " extrude produced " ~ size(extrudeCoEdges) ~ " co-edges (expected 2).";
            }
            for (var record in extrudeCoEdges)
            {
                var wallSidePcurve = undefined;
                if (record.faceIndexLeft == wallRecord.faceIndex)
                {
                    wallSidePcurve = record.uvCurves.left;
                }
                else if (record.faceIndexRight == wallRecord.faceIndex)
                {
                    wallSidePcurve = record.uvCurves.right;
                }
                if (record.convexity != EdgeConvexityType.CONVEX || wallSidePcurve == undefined)
                {
                    failures = failures ~ " extrude edge " ~ record.edgeIndex ~ " missing convexity or wall pcurve.";
                }
                else if (wallSidePcurve.maxResidual > 1e-8)
                {
                    failures = failures ~ " extrude edge " ~ record.edgeIndex ~ " wall pcurve residual " ~
                        wallSidePcurve.maxResidual ~ " m.";
                }
            }
        }

        fCuboid(context, id + "box", {
                    "corner1" : vector(-0.08, -0.04, 0) * meter,
                    "corner2" : vector(-0.03, 0, 0.05) * meter
                });
        const boxBody = qCreatedBy(id + "box", EntityType.BODY);
        const boxRecords = extractToolFaceRecords(context, boxBody, 1e-6);
        const boxCoEdges = extractCoEdgeRecords(context, boxBody, boxRecords, 9);
        const boxVertices = extractVertexRecords(context, boxBody, boxCoEdges);
        println("[EMIT SELF TEST] box: " ~ summarizeFaceRecords(boxRecords));
        println("[EMIT SELF TEST] box co-edges: " ~ summarizeCoEdgeRecords(boxCoEdges));
        println("[EMIT SELF TEST] box vertices: " ~ size(boxVertices));
        if (size(boxRecords) != 6 || size(boxCoEdges) != 12 || size(boxVertices) != 8)
        {
            failures = failures ~ " box counts " ~ size(boxRecords) ~ "/" ~ size(boxCoEdges) ~ "/" ~
                size(boxVertices) ~ " (expected 6/12/8).";
        }
        for (var record in boxCoEdges)
        {
            if (record.curveClass != SweepCurveClass.LINE || record.convexity != EdgeConvexityType.CONVEX ||
                record.faceIndexLeft == undefined || record.faceIndexRight == undefined ||
                record.faceIndexLeft == record.faceIndexRight)
            {
                failures = failures ~ " box edge " ~ record.edgeIndex ~ " not a two-sided convex line between distinct faces.";
            }
        }
        for (var record in boxVertices)
        {
            if (size(record.adjacentEdges) != 3 || size(record.coneNormals) != 3)
            {
                failures = failures ~ " box vertex " ~ record.vertexIndex ~ " has " ~ size(record.adjacentEdges) ~
                    " edges / " ~ size(record.coneNormals) ~ " cone normals (expected 3/3).";
            }
        }

        reportTestVerdict(context, id, "EMIT SELF TEST", failures,
            "faces classify on all fixtures; inversion at machine precision; co-edges carry class, " ~
            "convexity, sides, one-sided normals, and pcurves within tolerance; box vertices assemble " ~
            "3 edges and 3 cone normals each.");
    });

annotation { "Feature Type Name" : "Sweep Emit - Trim Loop Self Test" }
export const sweepEmitTrimLoopSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // Selection-free: every fixture is a hand-built 2D curve or sample array whose answer is
        // known in closed form, so the whole trim converter is checked without any geometry.
        var failures = "";

        // A square from four degree-1 curves, handed over out of order with one reversed. A
        // degree-1 curve is its own polyline, so this isolates the chaining.
        const corners = [vector(0.1, 0.2), vector(0.9, 0.2), vector(0.9, 0.8), vector(0.1, 0.8)];
        var squareCurves = makeArray(4);
        for (var index = 0; index < 4; index += 1)
        {
            squareCurves[index] = uvLineCurve(corners[index], corners[(index + 1) % 4]);
        }
        const scrambledSquare = [squareCurves[2], uvLineCurve(corners[1], corners[0]),
                squareCurves[3], squareCurves[1]];
        var squareSegments = makeArray(4);
        var squareBound = 0;
        for (var index = 0; index < 4; index += 1)
        {
            const sampled = polylineFromUvCurve(scrambledSquare[index], 1e-9);
            squareSegments[index] = sampled.points;
            squareBound = max(squareBound, sampled.certifiedBound);
            if (sampled.samplesPerSpan != 1)
            {
                failures = failures ~ " a degree-1 trim curve needed " ~ sampled.samplesPerSpan ~
                    " samples per span.";
            }
        }
        const squareLoops = chainUvPolylinesIntoLoops(squareSegments, 1e-9);
        println("[TRIM SELF TEST] scrambled square: " ~ size(squareLoops) ~ " loop(s), " ~
            (size(squareLoops) == 1 ? (size(squareLoops[0].points) ~ " points, gap " ~
                    squareLoops[0].closureGap) : "") ~ ", chord bound " ~ squareBound);
        if (size(squareLoops) != 1 || size(squareLoops[0].points) != 4 || !squareLoops[0].closed ||
            squareLoops[0].closureGap > 1e-15 || squareBound > 1e-15)
        {
            failures = failures ~ " scrambled square did not chain into one exact 4-point loop.";
        }

        // An exact rational-quadratic circle: the polyline bound must track the equal-angle
        // sagitta of the segment count it settled on, and must quarter when the count doubles.
        const circleCentre = vector(0.5, 0.5);
        const circleRadius = 0.25;
        const circleCurve = uvCircleCurve(circleCentre, circleRadius);
        var worstRadiusError = 0;
        for (var parameter in [0, 0.13, 0.37, 0.5, 0.9])
        {
            const onCurve = evaluateBSplineCurveDerivatives(circleCurve, parameter, 0)[0];
            worstRadiusError = max(worstRadiusError,
                abs(sqrt(squaredNorm(onCurve - circleCentre)) - circleRadius));
        }
        println("[TRIM SELF TEST] rational circle radius error at five parameters: " ~ worstRadiusError);
        if (worstRadiusError > 1e-14)
        {
            failures = failures ~ " the rational circle fixture is not exact (" ~ worstRadiusError ~ ").";
        }
        const circleTolerances = [1e-3, 1e-4, 1e-5];
        const expectedSegmentCounts = [64, 128, 512];
        var previousBound = 0;
        var previousSegments = 0;
        for (var toleranceIndex = 0; toleranceIndex < 3; toleranceIndex += 1)
        {
            const tolerance = circleTolerances[toleranceIndex];
            const sampled = polylineFromUvCurve(circleCurve, tolerance);
            const segmentCount = size(sampled.points) - 1;
            const equalAngleSagitta = circleRadius * (1 - cos(PI / segmentCount * radian));
            const sagittaRatio = sampled.certifiedBound / equalAngleSagitta;
            println("[TRIM SELF TEST] circle at tolerance " ~ tolerance ~ ": " ~ segmentCount ~
                " segments, bound " ~ sampled.certifiedBound ~ ", equal-angle sagitta " ~
                equalAngleSagitta ~ ", ratio " ~ sagittaRatio);
            if (segmentCount != expectedSegmentCounts[toleranceIndex] ||
                sampled.certifiedBound > tolerance || sagittaRatio < 1 || sagittaRatio > 1.15)
            {
                failures = failures ~ " circle polyline at tolerance " ~ tolerance ~
                    " gave " ~ segmentCount ~ " segments at bound " ~ sampled.certifiedBound ~ ".";
            }
            if (previousSegments > 0)
            {
                // Uniform-in-parameter refinement is second order, so the bound falls with the
                // square of the segment count whatever the parameterization does inside a span.
                const convergenceFactor = previousBound / sampled.certifiedBound /
                    ((segmentCount / previousSegments) * (segmentCount / previousSegments));
                println("[TRIM SELF TEST]   second-order convergence factor: " ~ convergenceFactor);
                if (abs(convergenceFactor - 1) > 0.02)
                {
                    failures = failures ~ " circle polyline refinement is not second order (" ~
                        convergenceFactor ~ ").";
                }
            }
            previousBound = sampled.certifiedBound;
            previousSegments = segmentCount;
        }

        // The whole converter on a face record: a square boundary with a circular hole, on the
        // approximation path, at the default tolerances for a unit uv domain.
        const holeRecord = {
                "faceIndex" : 0,
                "spline" : flatUnitDomainSurface(),
                "trimLoops" : { "boundary" : squareCurves, "inner" : [[circleCurve]] }
            };
        const holeTrim = buildFaceTrimLoops(holeRecord, [], {});
        println("[TRIM SELF TEST] square with a hole: " ~ summarizeTrimLoops(holeTrim));
        var holeLoopSizes = makeArray(size(holeTrim.loops), 0);
        for (var loopIndex = 0; loopIndex < size(holeTrim.loops); loopIndex += 1)
        {
            holeLoopSizes[loopIndex] = size(holeTrim.loops[loopIndex].points);
        }
        println("[TRIM SELF TEST]   loop sizes " ~ toString(holeLoopSizes));
        if (holeTrim.source != "approximation" || holeTrim.loopCount != 2 || !holeTrim.usable ||
            holeTrim.windingLoopCount != 0 || holeLoopSizes[0] != 4 || holeLoopSizes[1] != 64 ||
            holeTrim.worstClosureGap > 1e-15 || abs(holeTrim.worstCertifiedBound - 3.3454290863e-4) > 1e-12 ||
            abs(holeTrim.maxChordLength - 0.8) > 1e-15)
        {
            failures = failures ~ " the square-with-hole record did not convert to a 4-point and a " ~
                "64-point loop.";
        }

        // Seam-aware chaining at period 1: a trim that spans the whole seam has ends a period
        // apart, so a plain distance would call the loop open. Both sample densities matter -
        // eight segments, and the ONE chord the kernel can hand over for a degree-1 uv line,
        // which is the only legitimate two-point loop there is.
        for (var seamSegmentCount in [8, 1])
        {
            const wholeSeamLoops = chainUvPolylinesIntoLoops(
                    [uvArcSamples(0.2, 0, 1, seamSegmentCount), uvArcSamples(0.8, 0, 1, seamSegmentCount)],
                    1e-9, 1);
            var wholeSeamWindings = makeArray(size(wholeSeamLoops), 0);
            var wholeSeamSizes = makeArray(size(wholeSeamLoops), 0);
            var wholeSeamClosed = true;
            for (var loopIndex = 0; loopIndex < size(wholeSeamLoops); loopIndex += 1)
            {
                wholeSeamWindings[loopIndex] = wholeSeamLoops[loopIndex].winding;
                wholeSeamSizes[loopIndex] = size(wholeSeamLoops[loopIndex].points);
                wholeSeamClosed = wholeSeamClosed && wholeSeamLoops[loopIndex].closed;
            }
            println("[TRIM SELF TEST] whole-seam trims at " ~ seamSegmentCount ~ " segment(s): " ~
                size(wholeSeamLoops) ~ " loop(s), windings " ~ toString(wholeSeamWindings) ~
                ", sizes " ~ toString(wholeSeamSizes) ~ ", closed " ~ wholeSeamClosed);
            // A winding loop KEEPS its tail: it is the head one period along, and dropping it
            // would delete the segment that covers the seam.
            if (size(wholeSeamLoops) != 2 || !wholeSeamClosed ||
                wholeSeamWindings[0] != 1 || wholeSeamWindings[1] != 1 ||
                wholeSeamSizes[0] != seamSegmentCount + 1 || wholeSeamSizes[1] != seamSegmentCount + 1)
            {
                failures = failures ~ " whole-seam trim curves at " ~ seamSegmentCount ~
                    " segment(s) did not close as two winding loops keeping every sample.";
            }
        }

        // The same two trims split into arcs, shuffled, one reversed - the joins now land both
        // inside the domain and across the seam.
        const splitSeamLoops = chainUvPolylinesIntoLoops([uvArcSamples(0.2, 0, 0.5, 4),
                    uvArcSamples(0.8, 0.5, 1, 4), uvArcSamples(0.2, 1, 0.5, 4),
                    uvArcSamples(0.8, 0, 0.5, 4)], 1e-9, 1);
        var splitSeamWindingSum = 0;
        for (var splitLoop in splitSeamLoops)
        {
            splitSeamWindingSum += abs(splitLoop.winding);
        }
        println("[TRIM SELF TEST] split whole-seam trims: " ~ size(splitSeamLoops) ~
            " loop(s), total |winding| " ~ splitSeamWindingSum);
        if (size(splitSeamLoops) != 2 || splitSeamWindingSum != 2)
        {
            failures = failures ~ " split whole-seam trim arcs did not chain into two winding loops.";
        }

        // A hole straddling the seam, arriving as two pcurve pieces folded into the domain. The
        // per-segment unwrap plus the periodic joins must give one loop of span 0.1 that does
        // NOT wind - the loop is a hole, not a wrap.
        const foldedUpper = unwrapLoopU(foldedHoleSamples(0.05, 1, 8), 1);
        const foldedLower = unwrapLoopU(foldedHoleSamples(0.05, -1, 8), 1);
        const foldedLoops = chainUvPolylinesIntoLoops([foldedUpper, reverse(foldedLower)], 1e-9, 1);
        var foldedSpan = 0;
        var foldedWinding = 0;
        if (size(foldedLoops) == 1)
        {
            var spanMin = foldedLoops[0].points[0][0];
            var spanMax = foldedLoops[0].points[0][0];
            for (var loopPoint in foldedLoops[0].points)
            {
                spanMin = min(spanMin, loopPoint[0]);
                spanMax = max(spanMax, loopPoint[0]);
            }
            foldedSpan = spanMax - spanMin;
            foldedWinding = foldedLoops[0].winding;
        }
        println("[TRIM SELF TEST] seam-straddling hole: " ~ size(foldedLoops) ~ " loop(s), winding " ~
            foldedWinding ~ ", u span " ~ foldedSpan);
        if (size(foldedLoops) != 1 || foldedWinding != 0 || abs(foldedSpan - 0.1) > 1e-12)
        {
            failures = failures ~ " a seam-straddling hole did not chain into one 0.1-wide loop.";
        }

        // Folded input reads as no winding whichever kind of loop it is, which is why the
        // converter unwraps before it measures.
        var foldedRamp = makeArray(33, vector(0, 0.4));
        for (var index = 0; index < 33; index += 1)
        {
            foldedRamp[index] = vector(positiveModulo(0.3 + index / 32, 1), 0.4);
        }
        const foldedRampWinding = loopUWinding(foldedRamp, 1);
        const unwrappedRampWinding = loopUWinding(unwrapLoopU(foldedRamp, 1), 1);
        println("[TRIM SELF TEST] a folded winding ramp reads winding " ~ foldedRampWinding ~
            " folded and " ~ unwrappedRampWinding ~ " unwrapped");
        if (foldedRampWinding != 0 || unwrappedRampWinding != 1)
        {
            failures = failures ~ " unwrapping did not recover the winding of a folded ramp.";
        }

        reportTestVerdict(context, id, "TRIM LOOP SELF TEST", failures,
            "certified polyline conversion, seam-aware chaining, winding classification, " ~
            "and the record-level trim loop build all match their closed-form answers.");
    });

annotation { "Feature Type Name" : "Sweep Envelope Fit Self Test" }
export const sweepEnvelopeFitSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // ---------- Station builder ----------
        const stations = buildFitStations(0, 1, 5, [0.33, 0.5000001], 0.02);
        var stationsContainEvents = false;
        for (var station in stations)
        {
            if (station == 0.33)
            {
                stationsContainEvents = true;
            }
        }
        var stationsSorted = true;
        for (var index = 1; index < size(stations); index += 1)
        {
            if (stations[index] <= stations[index - 1])
            {
                stationsSorted = false;
            }
        }
        println("[FIT SELF TEST] stations with events: " ~ toString(stations));
        if (size(stations) != 6 || !stationsContainEvents || !stationsSorted ||
            stations[0] != 0 || stations[size(stations) - 1] != 1 ||
            abs(stations[3] - 0.5000001) > 1e-12)
        {
            failures = failures ~ " station builder expected 6 sorted stations with 0.33 inserted and 0.5 snapped to the event.";
        }

        // ---------- Fixture 1: ruled envelope, fixed anchors ----------
        // S = (u, v, 0.15u^2 - 0.18u + 0.05uv) under constant velocity (1, 0, 0.06):
        // f = 0.24 - 0.3u - 0.05v, the section is the fixed line u = 0.8 - v/6, and the
        // envelope is that line's lift translated along (t, 0, 0.06t).
        const slantSurface = slantFixtureSurface();
        const slantMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.06, 0.06], singleSpanKnots);
        const slantFit = fitEnvelopeComponent(slantMotion, slantSurface, {
                    "tStart" : 0, "tEnd" : 1,
                    "startAnchor" : { "anchorKind" : "fixedUv", "uv" : [0.8, 0] },
                    "endAnchor" : { "anchorKind" : "fixedUv", "uv" : [0.8 - 1 / 6, 1] },
                    "tolerance" : 1e-7,
                    "initialQCount" : 6, "initialStationCount" : 6,
                    "maxRefinementRounds" : 3
                });
        if (slantFit.failed)
        {
            failures = failures ~ " ruled fixture fit failed: " ~ slantFit.reason ~ ".";
        }
        else
        {
            println("[FIT SELF TEST] ruled fixture: " ~ slantFit.stationCount ~ "x" ~ slantFit.qCount ~
                " grid, deviation " ~ slantFit.worstDeviation ~ " (q " ~ slantFit.worstQDeviation ~
                ", t " ~ slantFit.worstTDeviation ~ "), removal " ~ slantFit.removalDeviation ~
                ", certified bound " ~ slantFit.certifiedBound ~ ", budgetHit " ~ slantFit.budgetHit);
            println("[FIT SELF TEST] ruled fixture orientation: lambda sign " ~
                slantFit.orientation.lambdaSign ~ " consistent " ~ slantFit.orientation.lambdaSignConsistent ~
                ", faces outward " ~ slantFit.orientation.facesOutward ~ " unanimous " ~
                slantFit.orientation.verdictUnanimous ~ ", q reversed " ~ slantFit.qReversed ~
                ", worst fold margin " ~ slantFit.orientation.worstFoldMargin ~ ", difference " ~
                slantFit.orientation.differenceAgreements ~ "/" ~ slantFit.orientation.differenceChecked ~
                " agree, consistent " ~ slantFit.orientation.consistent ~ ", stationary " ~
                slantFit.orientation.stationarySamples ~ "/" ~ slantFit.orientation.sampleCount);
            if (!slantFit.orientation.consistent)
            {
                failures = failures ~ " ruled fixture orientation certificate came back inconsistent.";
            }
            // This fixture translates at CONSTANT velocity, so A' and b'' vanish and f_t is
            // identically zero: every sample must report a stationary contact set (spec 7.7),
            // and that must not spoil the patch verdict.
            if (!slantFit.orientation.contactStationary)
            {
                failures = failures ~ " a constant-velocity translation did not report a " ~
                    "stationary contact set (" ~ slantFit.orientation.stationarySamples ~ " of " ~
                    slantFit.orientation.sampleCount ~ " samples).";
            }
            if (slantFit.budgetHit || slantFit.certifiedBound > 1e-7)
            {
                failures = failures ~ " ruled fixture missed tolerance (bound " ~ slantFit.certifiedBound ~ ").";
            }
            // Independent analytic membership: a fitted point (x, y, z) must satisfy v = y,
            // u = 0.8 - v/6, t = x - u, z = surface z + 0.06t.
            var worstRuledResidual = 0;
            const ruledDomain = fitSurfaceKnotDomain(slantFit.surface);
            for (var i = 1; i <= 5; i += 1)
            {
                for (var j = 1; j <= 5; j += 1)
                {
                    const uu = ruledDomain.uMin + (ruledDomain.uMax - ruledDomain.uMin) * i / 6;
                    const vv = ruledDomain.vMin + (ruledDomain.vMax - ruledDomain.vMin) * j / 6;
                    const fitted = evaluateBSplineSurfacePoint(slantFit.surface, uu, vv);
                    const toolV = fitted[1];
                    const toolU = 0.8 - toolV / 6;
                    const motionT = fitted[0] - toolU;
                    const exactZ = 0.15 * toolU ^ 2 - 0.18 * toolU + 0.05 * toolU * toolV + 0.06 * motionT;
                    worstRuledResidual = max(worstRuledResidual, abs(fitted[2] - exactZ));
                }
            }
            println("[FIT SELF TEST] ruled fixture analytic membership residual: " ~ worstRuledResidual);
            if (worstRuledResidual > 1e-6)
            {
                failures = failures ~ " ruled fixture analytic residual " ~ worstRuledResidual ~ ".";
            }

            // The same component with its anchors SWAPPED. That reverses the section march, so
            // kappa flips and exactly one of the two runs has to reverse q - which is how the
            // orientation pass and its held-out-row bookkeeping get exercised whichever way the
            // fixture happens to fall. Both must land outward, at the same certified deviation:
            // reversing q moves no geometry.
            const swappedFit = fitEnvelopeComponent(slantMotion, slantSurface, {
                        "tStart" : 0, "tEnd" : 1,
                        "startAnchor" : { "anchorKind" : "fixedUv", "uv" : [0.8 - 1 / 6, 1] },
                        "endAnchor" : { "anchorKind" : "fixedUv", "uv" : [0.8, 0] },
                        "tolerance" : 1e-7,
                        "initialQCount" : 6, "initialStationCount" : 6,
                        "maxRefinementRounds" : 3
                    });
            if (swappedFit.failed)
            {
                failures = failures ~ " anchor-swapped ruled fit failed: " ~ swappedFit.reason ~ ".";
            }
            else
            {
                println("[FIT SELF TEST] anchors swapped: deviation " ~ swappedFit.worstDeviation ~
                    ", certified bound " ~ swappedFit.certifiedBound ~ ", q reversed " ~
                    swappedFit.qReversed ~ " (against " ~ slantFit.qReversed ~ "), faces outward " ~
                    swappedFit.orientation.facesOutward ~ ", consistent " ~
                    swappedFit.orientation.consistent);
                if (!swappedFit.orientation.consistent)
                {
                    failures = failures ~ " anchor-swapped orientation certificate inconsistent.";
                }
                if (swappedFit.qReversed == slantFit.qReversed)
                {
                    failures = failures ~ " swapping the anchors did not flip the q reversal " ~
                        "decision, so the reversal path went untested.";
                }
                if (abs(swappedFit.worstDeviation - slantFit.worstDeviation) > 1e-9)
                {
                    failures = failures ~ " reversing q changed the certified deviation from " ~
                        slantFit.worstDeviation ~ " to " ~ swappedFit.worstDeviation ~
                        " - the held-out rows are mis-aligned against the net's q parameters.";
                }
            }
        }

        // ---------- Fixture 2: curvature in both directions, branch anchors ----------
        // S = (u, v, 0.15u^2 + 0.05uv^2) under velocity (1, 0, w(t)), w = 0.06 + 0.18t - 0.18t^2:
        // f = w(t) - 0.3u - 0.05v^2, sections are moving parabolas u = (w - 0.05v^2)/0.3.
        const curvedSurface = curvedFixtureSurface();
        const curvedMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.15, 0.06], singleSpanKnots);
        const curvedFit = fitEnvelopeComponent(curvedMotion, curvedSurface, {
                    "tStart" : 0, "tEnd" : 1,
                    "startAnchor" : curvedFixtureBranchAnchor(0),
                    "endAnchor" : curvedFixtureBranchAnchor(1),
                    "tolerance" : 1e-6,
                    "maxQCount" : 24, "maxStationCount" : 24, "maxRefinementRounds" : 3
                });
        if (curvedFit.failed)
        {
            failures = failures ~ " curved fixture fit failed: " ~ curvedFit.reason ~ ".";
        }
        else
        {
            println("[FIT SELF TEST] curved fixture: " ~ curvedFit.stationCount ~ "x" ~ curvedFit.qCount ~
                " grid, deviation " ~ curvedFit.worstDeviation ~ " (q " ~ curvedFit.worstQDeviation ~
                ", t " ~ curvedFit.worstTDeviation ~ "), certified bound " ~ curvedFit.certifiedBound ~
                ", budgetHit " ~ curvedFit.budgetHit);
            if (curvedFit.budgetHit || curvedFit.certifiedBound > 1e-6)
            {
                failures = failures ~ " curved fixture missed tolerance (bound " ~ curvedFit.certifiedBound ~ ").";
            }
            const curvedResidual = worstCurvedFixtureResidual(curvedFit.surface, 6);
            println("[FIT SELF TEST] curved fixture analytic membership residual: " ~ curvedResidual);
            if (curvedResidual > 2e-6)
            {
                failures = failures ~ " curved fixture analytic residual " ~ curvedResidual ~ ".";
            }
        }

        reportTestVerdict(context, id, "FIT SELF TEST", failures,
            "station builder, ruled and curved rectangle fits (fixed and branch anchors), " ~
            "direction-resolved certification, and knot cleanup all match their analytic answers.");
    });

// A separate feature (not a section of the main self test) because Onshape's per-regeneration
// interpreter step budget cannot absorb three certified fits in one feature: two fits plus a
// refinement loop trips "Too many steps".
annotation { "Feature Type Name" : "Sweep Envelope Fit Refinement Self Test" }
export const sweepEnvelopeFitRefinementSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // The curved fixture from the main self test, started too coarse for the tolerance so
        // the direction-resolved doubling has to run. 6x6 needs one q doubling and two
        // station doublings to clear 1e-6; the t direction floors at ~2.5e-7
        // under the 24-station cap, so 1e-7 is not reachable inside these caps).
        const curvedSurface = curvedFixtureSurface();
        const curvedMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.15, 0.06], singleSpanKnots);
        const refinedFit = fitEnvelopeComponent(curvedMotion, curvedSurface, {
                    "tStart" : 0, "tEnd" : 1,
                    "startAnchor" : curvedFixtureBranchAnchor(0),
                    "endAnchor" : curvedFixtureBranchAnchor(1),
                    "tolerance" : 1e-6,
                    "initialQCount" : 6, "initialStationCount" : 6,
                    "maxQCount" : 24, "maxStationCount" : 24, "maxRefinementRounds" : 3
                });
        if (refinedFit.failed)
        {
            failures = failures ~ " refinement run failed: " ~ refinedFit.reason ~ ".";
        }
        else
        {
            println("[FIT REFINEMENT SELF TEST] refinement run: " ~ refinedFit.stationCount ~ "x" ~ refinedFit.qCount ~
                " grid after " ~ refinedFit.refinementRounds ~ " round(s), deviation " ~
                refinedFit.worstDeviation ~ " (q " ~ refinedFit.worstQDeviation ~ ", t " ~
                refinedFit.worstTDeviation ~ "), budgetHit " ~ refinedFit.budgetHit);
            if (refinedFit.budgetHit || refinedFit.worstDeviation > 1e-6)
            {
                failures = failures ~ " refinement run missed tolerance (deviation " ~ refinedFit.worstDeviation ~ ").";
            }
            if (refinedFit.qCount <= 6 && refinedFit.stationCount <= 6)
            {
                failures = failures ~ " refinement run never refined from its 6x6 start.";
            }
        }

        reportTestVerdict(context, id, "FIT REFINEMENT SELF TEST", failures,
            "the direction-resolved refinement loop grew a too-coarse start to tolerance.");
    });

// The pole-capable grid assembly against the published interpolator: separate from the island
// test so the island's closed-loop marching gets the whole per-regeneration step budget.
annotation { "Feature Type Name" : "Sweep Envelope Fit Interpolation Self Test" }
export const sweepEnvelopeFitInterpolationSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";

        // ---------- Interpolation cross-check on a nondegenerate grid ----------
        // The pole-capable assembly must agree with the published surface interpolator
        // wherever both are defined.
        var checkGrid = makeArray(5);
        var checkGridWithUnits = makeArray(5);
        for (var i = 0; i < 5; i += 1)
        {
            var row = makeArray(5);
            var rowWithUnits = makeArray(5);
            for (var j = 0; j < 5; j += 1)
            {
                row[j] = vector(0.1 * i, 0.2 * j, 0.03 * i * i - 0.02 * j * j + 0.015 * i * j);
                rowWithUnits[j] = row[j] * meter;
            }
            checkGrid[i] = row;
            checkGridWithUnits[i] = rowWithUnits;
        }
        const customFit = interpolateFitGrid(checkGrid, 3, 3);
        const libraryFit = interpolateBSplineSurfaceThroughGrid(checkGridWithUnits, 3, 3);
        var worstInterpolationDisagreement = 0;
        for (var i = 0; i < 5; i += 1)
        {
            for (var j = 0; j < 5; j += 1)
            {
                worstInterpolationDisagreement = max(worstInterpolationDisagreement,
                    norm(customFit.controlPoints[i][j] * meter - libraryFit.controlPoints[i][j]) / meter);
            }
        }
        for (var index = 0; index < size(customFit.uKnots); index += 1)
        {
            worstInterpolationDisagreement = max(worstInterpolationDisagreement,
                abs(customFit.uKnots[index] - libraryFit.uKnots[index]));
            worstInterpolationDisagreement = max(worstInterpolationDisagreement,
                abs(customFit.vKnots[index] - libraryFit.vKnots[index]));
        }
        println("[FIT INTERPOLATION SELF TEST] pole-capable vs published interpolation disagreement: " ~
            worstInterpolationDisagreement);
        if (worstInterpolationDisagreement > 1e-12)
        {
            failures = failures ~ " pole-capable interpolation disagrees with the published one by " ~
                worstInterpolationDisagreement ~ ".";
        }

        // ---------- Pole collapse through the interpolation itself ----------
        // A fully collapsed first row must come back as a fully collapsed first CONTROL row:
        // this is the property the island patch's exact pole closure rests on, checked here
        // without paying for a fit.
        var poleGrid = makeArray(5);
        const poleApex = vector(0.2, 0.3, 0.9);
        for (var i = 0; i < 5; i += 1)
        {
            var row = makeArray(5);
            for (var j = 0; j < 5; j += 1)
            {
                row[j] = i == 0 ? poleApex :
                    vector(0.2 + 0.1 * i * cos(360 * degree * j / 4), 0.3 + 0.1 * i * sin(360 * degree * j / 4),
                        0.9 - 0.05 * i);
            }
            poleGrid[i] = row;
        }
        const poleSurface = interpolateFitGrid(poleGrid, 3, 3);
        var worstPoleRowError = 0;
        for (var j = 0; j < 5; j += 1)
        {
            worstPoleRowError = max(worstPoleRowError, norm(poleSurface.controlPoints[0][j] - poleApex));
        }
        const poleDomain = fitSurfaceKnotDomain(poleSurface);
        for (var vFraction in [0, 0.37, 1])
        {
            const vv = poleDomain.vMin + (poleDomain.vMax - poleDomain.vMin) * vFraction;
            worstPoleRowError = max(worstPoleRowError,
                norm(evaluateBSplineSurfacePoint(poleSurface, poleDomain.uMin, vv) - poleApex));
        }
        println("[FIT INTERPOLATION SELF TEST] collapsed-row closure error: " ~ worstPoleRowError);
        if (worstPoleRowError > 1e-14)
        {
            failures = failures ~ " a collapsed data row did not interpolate to a collapsed control row (error " ~
                worstPoleRowError ~ ").";
        }

        // ---------- Periodic row interpolation against an analytic circle ----------
        // A unit circle sampled at 12 equal angles: the periodic interpolant must pass through
        // every sample, stay on the circle BETWEEN samples (a clamped fit would not, at the
        // seam), and cross the seam smoothly - one-sided tangents there must agree.
        const circleSampleCount = 12;
        var circlePoints = makeArray(circleSampleCount);
        var circleParameters = makeArray(circleSampleCount, 0);
        for (var index = 0; index < circleSampleCount; index += 1)
        {
            const angle = 360 * degree * index / circleSampleCount;
            circlePoints[index] = vector(cos(angle), sin(angle), 0);
            circleParameters[index] = index / circleSampleCount;
        }
        const periodicCircle = interpolatePeriodicRow(circlePoints, 3, circleParameters, 1);
        const circleSpline = { "degree" : 3, "isPeriodic" : true, "isRational" : false,
                "controlPoints" : periodicCircle.controlPoints, "knots" : periodicCircle.knots };
        var worstCircleInterpolationError = 0;
        var worstCircleRadiusError = 0;
        for (var index = 0; index < circleSampleCount; index += 1)
        {
            const atSample = evaluateBSplineCurveDerivatives(circleSpline, circleParameters[index], 0)[0];
            worstCircleInterpolationError = max(worstCircleInterpolationError, norm(atSample - circlePoints[index]));
            const between = evaluateBSplineCurveDerivatives(circleSpline,
                    circleParameters[index] + 0.5 / circleSampleCount, 0)[0];
            worstCircleRadiusError = max(worstCircleRadiusError, abs(norm(between) - 1));
        }
        // Seam smoothness, stated exactly: for a genuinely periodic curve the domain end IS the
        // domain start, so position, tangent and second derivative there must agree to machine
        // precision. (Sampling two nearby points either side of the seam instead measures the
        // curve's own curvature across the gap: a 2e-6 parameter gap on this circle reads 1.3e-5
        // of tangent change on a curve that is perfectly smooth there.) A clamped fit of the
        // same closed data fails this check outright, which is the point.
        const atSeamStart = evaluateBSplineCurveDerivatives(circleSpline, 0, 2);
        const atSeamEnd = evaluateBSplineCurveDerivatives(circleSpline, 1, 2);
        const seamPositionError = norm(atSeamEnd[0] - atSeamStart[0]);
        const seamTangentError = norm(atSeamEnd[1] - atSeamStart[1]) / norm(atSeamStart[1]);
        const seamCurvatureError = norm(atSeamEnd[2] - atSeamStart[2]) / norm(atSeamStart[2]);
        println("[FIT INTERPOLATION SELF TEST] periodic circle: interpolation " ~ worstCircleInterpolationError ~
            ", between-sample radius error " ~ worstCircleRadiusError ~ " (cubic theory h^4/384 = " ~
            ((2 * PI / circleSampleCount) ^ 4 / 384) ~ "), seam position " ~ seamPositionError ~
            ", relative seam tangent " ~ seamTangentError ~ ", relative seam curvature " ~ seamCurvatureError);
        if (worstCircleInterpolationError > 1e-13)
        {
            failures = failures ~ " periodic interpolation missed its own data points by " ~
                worstCircleInterpolationError ~ ".";
        }
        // 12 cubic segments around a unit circle: h^4/384 is about 2e-4, so this bounds the
        // construction, not the discretization.
        if (worstCircleRadiusError > 5e-4)
        {
            failures = failures ~ " periodic circle radius error " ~ worstCircleRadiusError ~ ".";
        }
        if (seamPositionError > 1e-13 || seamTangentError > 1e-12 || seamCurvatureError > 1e-12)
        {
            failures = failures ~ " the periodic seam is not C2 (position " ~ seamPositionError ~
                ", tangent " ~ seamTangentError ~ ", curvature " ~ seamCurvatureError ~ ").";
        }

        reportTestVerdict(context, id, "FIT INTERPOLATION SELF TEST", failures,
            "the pole-capable grid interpolation matches the published interpolator on a " ~
            "nondegenerate grid, collapses a degenerate row exactly, and the periodic row " ~
            "interpolation reproduces a circle smoothly across its seam.");
    });

annotation { "Feature Type Name" : "Sweep Envelope Fit Island Self Test" }
export const sweepEnvelopeFitIslandSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // ---------- Island fixture: pole-collapsed patch ----------
        // z_u = 0.8u(1-u)v(1-v) peaks at 0.05; wz(t) = 0.134 - 0.4t + 0.4t^2 dips below the
        // peak exactly on t in (0.3, 0.7): birth/death at (0.5, 0.5, 0.3) and (0.5, 0.5, 0.7).
        const islandSurface = islandFixtureSurface();
        const islandMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], ISLAND_BUMP_VELOCITY_Z, singleSpanKnots);
        // One fit at a modest grid: the interpreter's per-regeneration step budget cannot
        // absorb an island refinement loop - closed-loop marching plus its fresh-station
        // certification is the most expensive path in the module. 8x8
        // measures 2.2e-4 with the q and t directions balanced, so 5e-4 asserts the
        // construction rather than the discretization.
        const islandFit = fitIslandComponent(islandMotion, islandSurface, {
                    "birth" : { "u" : 0.5, "v" : 0.5, "t" : 0.3 },
                    "death" : { "u" : 0.5, "v" : 0.5, "t" : 0.7 },
                    "tolerance" : 5e-4,
                    "initialQCount" : 8, "initialStationCount" : 8,
                    "maxQCount" : 15, "maxStationCount" : 15, "maxRefinementRounds" : 1
                });
        if (islandFit.failed)
        {
            failures = failures ~ " island fit failed: " ~ islandFit.reason ~ ".";
        }
        else
        {
            println("[FIT ISLAND SELF TEST] island fit: " ~ islandFit.stationCount ~ "x" ~ islandFit.qCount ~
                " grid after " ~ islandFit.refinementRounds ~ " round(s), deviation " ~
                islandFit.worstDeviation ~ " (q " ~ islandFit.worstQDeviation ~ ", t " ~
                islandFit.worstTDeviation ~ "), budgetHit " ~ islandFit.budgetHit);
            if (islandFit.budgetHit || islandFit.worstDeviation > 5e-4)
            {
                failures = failures ~ " island fit missed tolerance (deviation " ~ islandFit.worstDeviation ~ ").";
            }

            // Orientation (spec 6.6). Two things get checked here that no other fit reaches.
            //
            // (1) The collapsed POLE rows must be skipped by the certificate: an island's birth
            //     and death rows are a single repeated point, so they carry no q direction.
            // (2) This fixture's lambda spans BOTH SIGNS. On the loop max |z_uu| is
            //     0.2 sqrt(1 - 20 w_z), so lambda = w_z' + z_uu keeps one sign only where
            //     |w_z'| exceeds that - true just after birth and just before death, FALSE
            //     across t in about (0.39, 0.61). The bump fixture is therefore a LOCALLY
            //     SELF-INTERSECTING sweep through its middle band, which v1 must reject
            //     (spec 3, spec 10 detector 1), and the fold certificate has to say so. The fit
            //     still certifies to tolerance because a deviation check cannot see a fold -
            //     which is exactly why the orientation pass is a gate and not a formality.
            const islandPoleRows = 2;
            const expectedIslandSamples = (islandFit.stationCount - islandPoleRows) * islandFit.qCount;
            println("[FIT ISLAND SELF TEST] orientation: " ~ islandFit.orientation.sampleCount ~
                " samples (pole rows skipped, expected " ~ expectedIslandSamples ~ "), lambda sign " ~
                islandFit.orientation.lambdaSign ~ " consistent " ~
                islandFit.orientation.lambdaSignConsistent ~ ", worst fold margin " ~
                islandFit.orientation.worstFoldMargin ~ ", faces outward " ~
                islandFit.orientation.facesOutward ~ " unanimous " ~
                islandFit.orientation.verdictUnanimous ~ ", q reversed " ~ islandFit.qReversed ~
                ", difference " ~ islandFit.orientation.differenceAgreements ~ "/" ~
                islandFit.orientation.differenceChecked ~ " agree, consistent " ~
                islandFit.orientation.consistent);
            if (islandFit.orientation.sampleCount != expectedIslandSamples)
            {
                failures = failures ~ " the island's collapsed pole rows were not skipped (" ~
                    islandFit.orientation.sampleCount ~ " samples against " ~
                    expectedIslandSamples ~ ").";
            }
            if (islandFit.orientation.lambdaSignConsistent || islandFit.orientation.consistent)
            {
                failures = failures ~ " the fold certificate did NOT fire on the bump fixture, " ~
                    "whose lambda provably spans both signs across its middle band.";
            }

            // Pole closure: the fitted surface's u-start and u-end edges must BE the poles.
            const islandDomain = fitSurfaceKnotDomain(islandFit.surface);
            var worstPoleError = 0;
            for (var vFraction in [0, 0.31, 0.5, 0.77, 1])
            {
                const vv = islandDomain.vMin + (islandDomain.vMax - islandDomain.vMin) * vFraction;
                worstPoleError = max(worstPoleError,
                    norm(evaluateBSplineSurfacePoint(islandFit.surface, islandDomain.uMin, vv) - islandFit.poleStartPoint));
                worstPoleError = max(worstPoleError,
                    norm(evaluateBSplineSurfacePoint(islandFit.surface, islandDomain.uMax, vv) - islandFit.poleEndPoint));
            }
            println("[FIT ISLAND SELF TEST] pole closure error: " ~ worstPoleError);
            if (worstPoleError > 1e-12)
            {
                failures = failures ~ " pole rows did not collapse exactly (error " ~ worstPoleError ~ ").";
            }

            // Independent analytic membership: an envelope point is where the swept family's
            // height function h(u) = z(u, v) + Wz(x - u) - zFitted has a DOUBLE root in u, so
            // min |h| over u must vanish to fit tolerance.
            var worstIslandResidual = 0;
            for (var i = 1; i <= 3; i += 1)
            {
                for (var j = 0; j <= 2; j += 1)
                {
                    const uu = islandDomain.uMin + (islandDomain.uMax - islandDomain.uMin) * i / 4;
                    const vv = islandDomain.vMin + (islandDomain.vMax - islandDomain.vMin) * j / 2;
                    const fitted = evaluateBSplineSurfacePoint(islandFit.surface, uu, vv);
                    worstIslandResidual = max(worstIslandResidual,
                        islandFixtureMembershipResidual(fitted, ISLAND_BUMP_VELOCITY_Z));
                }
            }
            println("[FIT ISLAND SELF TEST] island analytic membership residual: " ~ worstIslandResidual);
            if (worstIslandResidual > 1e-4)
            {
                failures = failures ~ " island analytic residual " ~ worstIslandResidual ~ ".";
            }
        }

        reportTestVerdict(context, id, "FIT ISLAND SELF TEST", failures,
            "the island pole-collapsed patch certifies with exact pole closure and analytic " ~
            "envelope membership.");
    });

annotation { "Feature Type Name" : "Sweep Island Cap Live Test" }
export const sweepIslandCapLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = islandFixtureSurface();
        const motion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], ISLAND_CAP_VELOCITY_Z, [0, 0, 0, 0, 1, 1, 1, 1]);
        const poleTime = 0;
        const boundaryTime = ISLAND_PEAK_HEIGHT_SLOPE / ISLAND_CAP_SLOPE;

        // Born at t = 0 at the bump's peak, CLIPPED at t = 0.0375 - one pole and one genuine
        // loop boundary. The island's far end is not a pole: at t = 0.125 the level set
        // degenerates onto the patch boundary instead, so nothing past the clip is fitted.
        const fit = fitIslandComponent(motion, surface, {
                    "birth" : { "u" : 0.5, "v" : 0.5, "t" : poleTime },
                    "death" : { "u" : 0.5, "v" : 0.5, "t" : boundaryTime },
                    "tEnd" : ISLAND_CAP_T_END,
                    "tolerance" : ISLAND_CAP_TOLERANCE,
                    "initialQCount" : 8, "initialStationCount" : 8,
                    "maxQCount" : 15, "maxStationCount" : 15, "maxRefinementRounds" : 1
                });
        tally = checkThat(tally, !fit.failed, "the clipped island fit failed: " ~ fit.reason ~ ".");
        if (!fit.failed)
        {
            println("[ISLAND CAP LIVE TEST] fit: " ~ fit.stationCount ~ "x" ~ fit.qCount ~
                " grid in " ~ fit.refinementRounds ~ " round(s), deviation " ~ fit.worstDeviation ~
                " (q " ~ fit.worstQDeviation ~ ", t " ~ fit.worstTDeviation ~ "), poles " ~
                fit.poleAtStart ~ "/" ~ fit.poleAtEnd ~ ", budgetHit " ~ fit.budgetHit);
            tally = checkThat(tally, !fit.budgetHit && fit.worstDeviation <= ISLAND_CAP_TOLERANCE,
                "the cap fit missed " ~ ISLAND_CAP_TOLERANCE ~ " (deviation " ~ fit.worstDeviation ~ ").");
            tally = checkThat(tally, fit.poleAtStart && !fit.poleAtEnd,
                "the clip produced poles " ~ fit.poleAtStart ~ "/" ~ fit.poleAtEnd ~
                " instead of one at the start only.");

            // ONE collapsed row, so the orientation certificate skips one row and no more.
            const expectedSamples = (fit.stationCount - 1) * fit.qCount;
            println("[ISLAND CAP LIVE TEST] orientation: " ~ fit.orientation.sampleCount ~
                " samples (one pole row skipped, expected " ~ expectedSamples ~ "), lambda sign " ~
                fit.orientation.lambdaSign ~ " consistent " ~ fit.orientation.lambdaSignConsistent ~
                ", worst fold margin " ~ fit.orientation.worstFoldMargin ~ ", stationary " ~
                fit.orientation.stationarySamples ~ ", degenerate " ~ fit.orientation.degenerateSamples ~
                ", difference " ~ fit.orientation.differenceAgreements ~ "/" ~
                fit.orientation.differenceChecked ~ " agree, q reversed " ~ fit.qReversed ~
                ", consistent " ~ fit.orientation.consistent);
            tally = checkThat(tally, fit.orientation.sampleCount == expectedSamples,
                "the cap's single collapsed pole row was not skipped exactly once (" ~
                fit.orientation.sampleCount ~ " samples against " ~ expectedSamples ~ ").");

            // The fold certificate, the whole point of clipping: lambda = wz' + z_uu with
            // |wz'| = 0.4 against max |z_uu| = 0.1095 on the clip loop, so lambda holds -1 with
            // a fold margin of 0.570 - and f_t is a genuine 0.4, not the stationary case.
            tally = checkThat(tally, fit.orientation.lambdaSignConsistent && fit.orientation.consistent,
                "the fold certificate did not clear on the clipped cap (lambda sign " ~
                fit.orientation.lambdaSign ~ ", consistent " ~ fit.orientation.consistent ~ ").");
            tally = checkThat(tally, fit.orientation.lambdaSign == -1,
                "lambda came out " ~ fit.orientation.lambdaSign ~ " where wz' = -0.4 dominates z_uu.");
            tally = checkThat(tally, fit.orientation.worstFoldMargin > 0.5,
                "the worst fold margin is " ~ fit.orientation.worstFoldMargin ~ ", under the 0.570 " ~
                "the closed form predicts.");
            tally = checkThat(tally, !fit.orientation.contactStationary,
                "the contact set reported stationary, but this motion accelerates in z.");

            // Pole closure at the collapsed end, and a genuine loop at the clipped end.
            const fitDomain = fitSurfaceKnotDomain(fit.surface);
            var worstPoleError = 0;
            var loopSpan = 0;
            for (var vFraction in [0, 0.31, 0.5, 0.77, 1])
            {
                const vv = fitDomain.vMin + (fitDomain.vMax - fitDomain.vMin) * vFraction;
                worstPoleError = max(worstPoleError,
                    norm(evaluateBSplineSurfacePoint(fit.surface, fitDomain.uMin, vv) - fit.poleStartPoint));
                loopSpan = max(loopSpan,
                    norm(evaluateBSplineSurfacePoint(fit.surface, fitDomain.uMax, vv) -
                            evaluateBSplineSurfacePoint(fit.surface, fitDomain.uMax, fitDomain.vMin)));
            }
            println("[ISLAND CAP LIVE TEST] pole closure error " ~ worstPoleError ~
                ", clipped-end loop span " ~ loopSpan ~ " m");
            tally = checkWithin(tally, worstPoleError, 1e-12, "the cap's pole row closure");
            tally = checkThat(tally, loopSpan > 0.1,
                "the clipped end collapsed too (span " ~ loopSpan ~ " m), so this is not a one-pole cap.");

            // EMISSION - the question this fixture exists to answer. A net with ONE collapsed
            // boundary row and a closed v direction has never been handed to the kernel; spec
            // 7.4 only established that BOTH rows collapsed is refused.
            const emission = emitIslandPatches(context, id, fit, {});
            println("[ISLAND CAP LIVE TEST] emission: refused " ~ emission.refused ~ ", shape " ~
                emission.shape ~ ", poles " ~ emission.poleCount ~ ", patches " ~
                emission.patchCount ~ ", faces " ~ emission.faceCount ~ ", declared periodic " ~
                toString(emission.declaredPeriodic));
            tally = checkThat(tally, !emission.refused,
                "the fold-free cap was refused: " ~ (emission.reason == undefined ? "" : emission.reason));
            tally = checkThat(tally, emission.faceCount == 1,
                "the one-pole cap emitted " ~ emission.faceCount ~ " faces instead of one.");

            if (emission.faceCount == 1)
            {
                // Kernel certification against fresh envelope loops at t values that are
                // neither stations (multiples of 0.0375/7) nor the midpoints the fit already
                // certified against - in BOTH held-out families, which the first live run
                // showed is not a luxury: the loops' own arc-length fractions come back at
                // exactly 0 m, because this fit's t direction is right to 9e-7 and the
                // evaluator does not resolve that. The q MIDPOINTS carry the whole of this
                // fit's error, so they are the number the kernel has to agree with.
                var freshRowPoints = [];
                var freshMidPoints = [];
                for (var freshT in [0.013, 0.031])
                {
                    const freshLoop = islandLoopSamples(motion, surface, [0.5, 0.5], freshT, 8, {});
                    if (freshLoop.failed)
                    {
                        tally = checkThat(tally, false, "fresh loop at t = " ~ freshT ~ " failed.");
                        continue;
                    }
                    for (var point in freshLoop.liftedRow)
                    {
                        freshRowPoints = append(freshRowPoints, point * meter);
                    }
                    for (var point in freshLoop.midLifted)
                    {
                        freshMidPoints = append(freshMidPoints, point * meter);
                    }
                }
                if (size(freshMidPoints) > 0)
                {
                    const patchFaces = qCreatedBy(emission.patchIds[0], EntityType.FACE);
                    const rowDeviation = evPointsDeviation(context, {
                                    "points" : freshRowPoints, "topologies" : patchFaces
                                })[0].deviation / meter;
                    const midDeviation = evPointsDeviation(context, {
                                    "points" : freshMidPoints, "topologies" : patchFaces
                                })[0].deviation / meter;
                    println("[ISLAND CAP LIVE TEST] kernel deviation vs fresh envelope loops: " ~
                        rowDeviation ~ " m at the loops' own q fractions, " ~ midDeviation ~
                        " m at their q midpoints (the fit certifies " ~ fit.worstDeviation ~ ")");
                    tally = checkWithin(tally, midDeviation, 1e-3,
                        "the emitted cap's deviation from fresh envelope points");
                    tally = checkThat(tally, midDeviation <= 2 * fit.certifiedBound + 1e-9,
                        "the kernel measures " ~ midDeviation ~ " m against a certified bound of " ~
                        fit.certifiedBound ~ " m, so the emitted face is not the surface that " ~
                        "was certified.");
                }

                // And an independent analytic check: an envelope point is where the swept
                // family's height function has a double root in u.
                var worstMembership = 0;
                for (var i = 1; i <= 3; i += 1)
                {
                    for (var j = 0; j <= 2; j += 1)
                    {
                        const uu = fitDomain.uMin + (fitDomain.uMax - fitDomain.uMin) * i / 4;
                        const vv = fitDomain.vMin + (fitDomain.vMax - fitDomain.vMin) * j / 2;
                        worstMembership = max(worstMembership, islandFixtureMembershipResidual(
                                evaluateBSplineSurfacePoint(fit.surface, uu, vv), ISLAND_CAP_VELOCITY_Z));
                    }
                }
                println("[ISLAND CAP LIVE TEST] analytic envelope membership: " ~ worstMembership);
                tally = checkWithin(tally, worstMembership, 1e-4, "the cap's analytic membership residual");

                // THE SPLIT SHAPE, exercised here rather than on the two-pole island: that
                // island folds by construction (see emitIslandPatches), and the kernel refuses
                // its halves in both declarations for reasons that have nothing to do with the
                // split. Cutting this FOLD-FREE cap at a mid station produces exactly the two
                // shapes the two-pole route would need - a 4-row one-pole cap and a 5-row
                // pole-free tube - meeting on one shared loop row.
                const split = splitIslandFitGrid(fit);
                tally = checkThat(tally, !split.failed,
                    "the cap split failed: " ~ (split.reason == undefined ? "" : split.reason));
                if (!split.failed)
                {
                    const startPatch = emitFitSurfacePatch(context, id + "splitStart", split.startSurface);
                    const endPatch = emitFitSurfacePatch(context, id + "splitEnd", split.endSurface);
                    println("[ISLAND CAP LIVE TEST] split at station " ~ split.cut ~ " of " ~
                        (fit.stationCount - 1) ~ ": nets " ~ size(split.startSurface.controlPoints) ~
                        "x" ~ size(split.startSurface.controlPoints[0]) ~ " and " ~
                        size(split.endSurface.controlPoints) ~ "x" ~
                        size(split.endSurface.controlPoints[0]) ~ ", faces " ~
                        startPatch.faceCount ~ " + " ~ endPatch.faceCount ~ ", periodic " ~
                        startPatch.declaredPeriodic ~ "/" ~ endPatch.declaredPeriodic ~
                        (startPatch.refused ? (", start refused: " ~ startPatch.reason) : "") ~
                        (endPatch.refused ? (", end refused: " ~ endPatch.reason) : ""));
                    tally = checkThat(tally, startPatch.faceCount == 1 && endPatch.faceCount == 1,
                        "the fold-free split emitted " ~ startPatch.faceCount ~ " and " ~
                        endPatch.faceCount ~ " faces instead of one each.");

                    // The shared row has to be ONE curve, not two through the same points.
                    const naiveStart = interpolateFitGrid(subArray(fit.liftedGrid, 0, split.cut + 1), 3, 3, true);
                    const naiveEnd = interpolateFitGrid(subArray(fit.liftedGrid, split.cut, fit.stationCount), 3, 3, true);
                    const naiveBoundary = naiveStart.controlPoints[size(naiveStart.controlPoints) - 1];
                    var naiveGap = 0;
                    for (var index = 0; index < size(naiveBoundary); index += 1)
                    {
                        naiveGap = max(naiveGap, norm(naiveBoundary[index] - naiveEnd.controlPoints[0][index]));
                    }
                    println("[ISLAND CAP LIVE TEST] shared control row gap: " ~ split.seamError ~
                        " m with the whole grid's v parameters, " ~ naiveGap ~ " m with each half's own");
                    tally = checkWithin(tally, split.seamError, 1e-15, "the two caps' shared control row gap");
                    tally = checkThat(tally, naiveGap > 1e-9,
                        "each half's own v parameters gave a gap of only " ~ naiveGap ~ " m, so this " ~
                        "fixture no longer shows why the parameters must be prescribed.");

                    if (startPatch.faceCount == 1 && endPatch.faceCount == 1)
                    {
                        var sharedPoints = [];
                        for (var point in split.sharedRow)
                        {
                            sharedPoints = append(sharedPoints, point * meter);
                        }
                        for (var patch in [startPatch, endPatch])
                        {
                            const deviation = evPointsDeviation(context, {
                                            "points" : sharedPoints,
                                            "topologies" : qCreatedBy(patch.id, EntityType.FACE)
                                        })[0].deviation / meter;
                            println("[ISLAND CAP LIVE TEST] shared row deviation from the " ~
                                (patch.declaredPeriodic ? "periodic" : "clamped") ~ " patch: " ~
                                deviation ~ " m");
                            tally = checkWithin(tally, deviation, 1e-9,
                                "a split cap's deviation from the shared loop row");
                        }
                    }
                }
            }
        }

        reportCheckTally(context, id, "ISLAND CAP LIVE TEST", tally,
            "a clipped island fits fold-free, closes exactly on its single pole, and emits ONE " ~
            "kernel face that certifies against fresh envelope loops.");
    });

annotation { "Feature Type Name" : "Sweep Island Split Live Test" }
export const sweepIslandSplitLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = islandFixtureSurface();
        const motion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], ISLAND_BUMP_VELOCITY_Z, [0, 0, 0, 0, 1, 1, 1, 1]);
        const birth = { "u" : 0.5, "v" : 0.5, "t" : 0.3 };
        const death = { "u" : 0.5, "v" : 0.5, "t" : 0.7 };

        // WHY a two-pole island always folds, measured rather than argued: at a t-extreme the
        // contact set is a single point where f_u = f_v = 0, so lambda IS f_t there. The loop
        // shrinks to a point at both ends with the same sign of f inside it, which forces the
        // same Hessian definiteness at both - and that forces OPPOSITE f_t signs. Here
        // f_t = wz' = 0.8t - 0.4, so -0.16 at the birth and +0.16 at the death.
        var worstPoleLambdaGap = 0;
        var poleTimeDerivatives = [];
        for (var pole in [birth, death])
        {
            const sample = envelopeOrientationSample(motion, surface, pole.u, pole.v, pole.t);
            worstPoleLambdaGap = max(worstPoleLambdaGap, abs(sample.lambda - sample.tDerivative));
            poleTimeDerivatives = append(poleTimeDerivatives, sample.tDerivative);
        }
        println("[ISLAND SPLIT LIVE TEST] at the poles: f_t = " ~ toString(poleTimeDerivatives) ~
            ", worst |lambda - f_t| = " ~ worstPoleLambdaGap);
        tally = checkWithin(tally, worstPoleLambdaGap, 1e-15, "|lambda - f_t| at the island's poles");
        tally = checkThat(tally, poleTimeDerivatives[0] * poleTimeDerivatives[1] < 0,
            "the two poles' f_t signs agree (" ~ toString(poleTimeDerivatives) ~ "), which would make a " ~
            "fold-free two-pole island possible after all.");

        const fit = fitIslandComponent(motion, surface, {
                    "birth" : birth, "death" : death,
                    "tolerance" : 5e-4,
                    "initialQCount" : 8, "initialStationCount" : 8,
                    "maxQCount" : 15, "maxStationCount" : 15, "maxRefinementRounds" : 1
                });
        tally = checkThat(tally, !fit.failed, "the two-pole island fit failed: " ~ fit.reason ~ ".");
        if (!fit.failed)
        {
            println("[ISLAND SPLIT LIVE TEST] fit: " ~ fit.stationCount ~ "x" ~ fit.qCount ~
                " grid, deviation " ~ fit.worstDeviation ~ ", poles " ~ fit.poleAtStart ~ "/" ~
                fit.poleAtEnd ~ ", lambda consistent " ~ fit.orientation.lambdaSignConsistent);
            tally = checkThat(tally, fit.poleAtStart && fit.poleAtEnd,
                "the unclipped island did not come out with two poles.");
            tally = checkThat(tally, !fit.orientation.lambdaSignConsistent,
                "the fold certificate did not fire on the two-pole island, whose lambda provably " ~
                "spans both signs across t in (0.385, 0.615).");

            // v1 policy: a folded component is reported, never emitted.
            const refusal = emitIslandPatches(context, id + "refused", fit, {});
            println("[ISLAND SPLIT LIVE TEST] default emission: refused " ~ refusal.refused ~
                ", faces " ~ refusal.faceCount);
            tally = checkThat(tally, refusal.refused && refusal.faceCount == 0,
                "a folded island was emitted instead of reported.");

            // The emission SHAPE, exercised with the fold gate lifted: two single-pole caps
            // sharing one row. The shared control row must be identical coordinate for
            // coordinate - not merely close - which is what the prescribed v parameters buy.
            const split = splitIslandFitGrid(fit);
            tally = checkThat(tally, !split.failed,
                "the island split failed: " ~ (split.reason == undefined ? "" : split.reason));
            if (!split.failed)
            {
                println("[ISLAND SPLIT LIVE TEST] split at station " ~ split.cut ~ " of " ~
                    (fit.stationCount - 1) ~ ": nets " ~ size(split.startSurface.controlPoints) ~ "x" ~
                    size(split.startSurface.controlPoints[0]) ~ " and " ~
                    size(split.endSurface.controlPoints) ~ "x" ~
                    size(split.endSurface.controlPoints[0]));
                // The split's own arithmetic still holds on a folded island - it is grid
                // algebra, not geometry - so the shared row is exact here too, against each
                // half averaging its OWN v parameters. Same eight points, two
                // parameterizations, two curves: that gap is what prescribing them removes.
                const naiveStart = interpolateFitGrid(subArray(fit.liftedGrid, 0, split.cut + 1), 3, 3, true);
                const naiveEnd = interpolateFitGrid(subArray(fit.liftedGrid, split.cut, fit.stationCount), 3, 3, true);
                const naiveBoundary = naiveStart.controlPoints[size(naiveStart.controlPoints) - 1];
                var naiveGap = 0;
                for (var index = 0; index < size(naiveBoundary); index += 1)
                {
                    naiveGap = max(naiveGap, norm(naiveBoundary[index] - naiveEnd.controlPoints[0][index]));
                }
                println("[ISLAND SPLIT LIVE TEST] shared control row gap: " ~ split.seamError ~
                    " m with the whole grid's v parameters, " ~ naiveGap ~ " m with each half's own");
                tally = checkWithin(tally, split.seamError, 1e-15,
                    "the two caps' shared control row gap");
                tally = checkThat(tally, naiveGap > 1e-9,
                    "each half's own v parameters gave a gap of only " ~ naiveGap ~ " m, so this " ~
                    "fixture no longer shows why the parameters must be prescribed.");

                // Handing the folded halves to the kernel anyway, with `requireFoldFree`
                // false, answers CANNOT_MAKE_BSPLINESURFACE for BOTH halves in BOTH
                // declarations - four refusals, all caught, nothing thrown. That is the second and independent
                // reason a two-pole island is never emitted, and it is not re-run here on
                // purpose: a caught kernel notice suppresses the console output this test
                // reports through, so the run that proves it cannot also print its verdict.
                // The split SHAPE is exercised where it can be - on the fold-free clipped cap,
                // in the island cap live test, where both halves come back as one face each.
            }
        }

        reportCheckTally(context, id, "ISLAND SPLIT LIVE TEST", tally,
            "a two-pole island's poles carry opposite f_t, so lambda cannot hold one sign across " ~
            "it; the fold certificate fires, v1 reports instead of emitting, and the split's " ~
            "shared row is exact even where the kernel will not take the halves.");
    });

annotation { "Feature Type Name" : "Sweep Envelope Fit Tube Self Test" }
export const sweepEnvelopeFitTubeSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const barrel = tubeFixtureSurface();
        // Velocity mostly along the barrel axis with a growing sideways component, so the
        // contact loop starts as the flat v = 0.5 iso-curve and tilts as t runs.
        const motion = translationMotionFromQuadraticVelocity([0, 0.12, 0.25], [0, 0, 0], [1, 1, 1],
            [0, 0, 0, 0, 1, 1, 1, 1]);
        const uSeed = 5;
        const tubeOptions = { "vMargin" : 0.05, "sectionTolerance" : 1e-13, "meridianSamples" : 24 };
        const uPeriod = 8;

        // ---------- One station: the loop wraps the seam once and sits on the analytic
        // contact curve ----------
        const probeT = 0.7;
        const probeLoop = tubeLoopSamples(motion, barrel, probeT, uSeed, 12, tubeOptions);
        if (probeLoop.failed)
        {
            failures = failures ~ " tube loop at t = " ~ probeT ~ " failed: " ~ probeLoop.reason ~ ".";
        }
        else
        {
            var worstAnalyticV = 0;
            for (var uv in probeLoop.uvRow)
            {
                worstAnalyticV = max(worstAnalyticV,
                    abs(uv[1] - tubeFixtureExactV(motion, uv[0], probeT)));
            }
            println("[TUBE SELF TEST] loop at t = " ~ probeT ~ ": uTravel " ~ probeLoop.uTravel ~
                " (period " ~ uPeriod ~ "), section residual " ~ probeLoop.worstResidual ~
                ", worst v vs the closed form " ~ worstAnalyticV);
            if (probeLoop.uTravel != uPeriod)
            {
                failures = failures ~ " the loop travelled " ~ probeLoop.uTravel ~ " in u, not one period.";
            }
            if (probeLoop.worstResidual > 1e-11)
            {
                failures = failures ~ " marched section residual " ~ probeLoop.worstResidual ~ ".";
            }
            if (worstAnalyticV > 1e-11)
            {
                failures = failures ~ " marched v is " ~ worstAnalyticV ~ " off the closed-form contact curve.";
            }
        }

        // ---------- The fit ----------
        const tubeFit = fitTubeComponent(motion, barrel, mergeMaps(tubeOptions, {
                        "tStart" : 0, "tEnd" : 1, "uSeed" : uSeed, "tolerance" : TUBE_SELF_TEST_TOLERANCE,
                        "initialQCount" : 14, "initialStationCount" : 8,
                        "maxQCount" : 27, "maxStationCount" : 15, "maxRefinementRounds" : 2
                    }));
        if (tubeFit.failed)
        {
            failures = failures ~ " tube fit failed: " ~ tubeFit.reason ~ ".";
        }
        else
        {
            println("[TUBE SELF TEST] tube fit: " ~ tubeFit.stationCount ~ "x" ~ tubeFit.qCount ~
                " grid in " ~ tubeFit.refinementRounds ~ " round(s), deviation " ~ tubeFit.worstDeviation ~
                " (q " ~ tubeFit.worstQDeviation ~ ", t " ~ tubeFit.worstTDeviation ~
                "), section residual " ~ tubeFit.worstSectionResidual ~ ", budgetHit " ~ tubeFit.budgetHit);
            if (tubeFit.budgetHit || tubeFit.worstDeviation > TUBE_SELF_TEST_TOLERANCE)
            {
                failures = failures ~ " tube fit missed tolerance (deviation " ~ tubeFit.worstDeviation ~ ").";
            }
            if (tubeFit.surface.isVPeriodic != true)
            {
                failures = failures ~ " the tube fit is not periodic in q.";
            }

            // Orientation (spec 6.6). This is the ONLY path whose rows carry u UNWRAPPED past
            // the seam, so it is the only live test of the rule that a q-direction difference
            // must never wrap the sample index: a wrapping difference would be a period-sized
            // jump pointing the wrong way, flipping kappa at exactly one column per station and
            // breaking unanimity. This barrel's lambda is one-signed with a fold margin near
            // 0.95, so unanimity here IS that check. Isolated samples where f_t vanishes (the
            // profile's cy' = 0 points, where the contact curve is tangent to the station) are
            // expected and must count as stationary, not as degeneracy.
            println("[TUBE SELF TEST] orientation: " ~ tubeFit.orientation.sampleCount ~
                " samples, lambda sign " ~ tubeFit.orientation.lambdaSign ~ " consistent " ~
                tubeFit.orientation.lambdaSignConsistent ~ ", worst fold margin " ~
                tubeFit.orientation.worstFoldMargin ~ ", faces outward " ~
                tubeFit.orientation.facesOutward ~ " unanimous " ~
                tubeFit.orientation.verdictUnanimous ~ ", q reversed " ~ tubeFit.qReversed ~
                ", difference " ~ tubeFit.orientation.differenceAgreements ~ "/" ~
                tubeFit.orientation.differenceChecked ~ " agree, stationary " ~
                tubeFit.orientation.stationarySamples ~ ", degenerate " ~
                tubeFit.orientation.degenerateSamples ~ ", consistent " ~
                tubeFit.orientation.consistent);
            if (!tubeFit.orientation.verdictUnanimous)
            {
                failures = failures ~ " the tube's outward verdict was not unanimous - an " ~
                    "unwrapped-u row produced a spurious kappa flip at the seam.";
            }
            if (!tubeFit.orientation.consistent || tubeFit.orientation.lambdaSign != -1)
            {
                failures = failures ~ " the tube orientation certificate did not come back " ~
                    "consistent with one negative lambda sign.";
            }

            // Analytic envelope membership at t values no station and no midpoint station used.
            var worstMembership = 0;
            for (var freshT in [0.31, 0.83])
            {
                for (var index = 0; index < 9; index += 1)
                {
                    const u = 3 + uPeriod * index / 9;
                    const analyticPoint = liftContactPoint(motion, barrel, u,
                        tubeFixtureExactV(motion, u, freshT), freshT);
                    worstMembership = max(worstMembership,
                        invertPointOnSurfaceFromGrid(tubeFit.surface, analyticPoint, 8).residual);
                }
            }
            println("[TUBE SELF TEST] closed-form envelope membership: " ~ worstMembership);
            if (worstMembership > TUBE_SELF_TEST_TOLERANCE)
            {
                failures = failures ~ " closed-form envelope membership " ~ worstMembership ~ ".";
            }

            // The q seam: the domain END and the domain START are the SAME point of a periodic
            // direction, so they must agree in position, tangent, and curvature. Sampling two
            // nearby parameters either side instead would measure the patch's own curvature
            // across the gap and read as a false kink (spec 7.4).
            const fitDomain = fitSurfaceKnotDomain(tubeFit.surface);
            const seamU = 0.5 * (fitDomain.uMin + fitDomain.uMax);
            const atStart = evaluateBSplineSurfaceDerivatives(tubeFit.surface, seamU, fitDomain.vMin, 0, 2);
            const atEnd = evaluateBSplineSurfaceDerivatives(tubeFit.surface, seamU, fitDomain.vMax, 0, 2);
            const seamPosition = norm(atEnd[0][0] - atStart[0][0]);
            const seamTangent = norm(atEnd[0][1] - atStart[0][1]) / max(1e-300, norm(atStart[0][1]));
            const seamCurvature = norm(atEnd[0][2] - atStart[0][2]) / max(1e-300, norm(atStart[0][2]));
            println("[TUBE SELF TEST] q seam: position " ~ seamPosition ~ ", relative tangent " ~
                seamTangent ~ ", relative curvature " ~ seamCurvature);
            if (seamPosition > 1e-12 || seamTangent > 1e-10 || seamCurvature > 1e-9)
            {
                failures = failures ~ " the q seam is not C2 (position " ~ seamPosition ~ ").";
            }
        }

        reportTestVerdict(context, id, "TUBE SELF TEST", failures,
            "the wrapping section march closes on the closed-form contact curve after exactly " ~
            "one period of u, and the tube fit certifies with a C2 periodic q seam.");
    });

/**
 * ROTATING TUBE SELF TEST (spec 7.9, section 12.1's rotation-through-a-fitted-patch gap).
 *
 * Every step-7 measurement before this one was a pure translation, which is also the one case
 * where the envelope degenerates to a profile sweep (spec 7.7) and where lambda is constant by
 * construction. This is the first fitted patch whose motion actually rotates, so it is the first
 * test in which the contact set MOVES across the tool and the fold certificate has a varying
 * lambda to certify.
 *
 * Everything it asserts is closed form - see rotatingTubeMotion's comment for the derivation and
 * for why rotation about the barrel's own axis is non-degenerate only because the profile is an
 * ellipse. The one number that is not closed form is the stored rotation's rigidity drift, which
 * no polynomial rotation can avoid; it is measured and bounded rather than assumed away.
 */
annotation { "Feature Type Name" : "Sweep Rotating Tube Self Test" }
export const sweepRotatingTubeSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const barrel = tubeFixtureSurface();
        const motion = rotatingTubeMotion();
        const straight = constantVelocityTranslationMotion(
            vector(ROTATING_TUBE_CROSS_SPEED, 0, ROTATING_TUBE_AXIAL_SPEED));
        const uSeed = 5;
        const uPeriod = TUBE_FIXTURE_PROFILE_COUNT;
        const tubeOptions = { "vMargin" : 0.05, "sectionTolerance" : 1e-13, "meridianSamples" : 24 };

        // ---------- The stored rotation: how rigid, and is M's third row really zero ----------
        var worstDrift = 0;
        var worstThirdRow = 0;
        var worstTurn = 0;
        for (var index = 0; index <= 40; index += 1)
        {
            const t = index / 40;
            const pullback = rotatingTubePullback(motion, t);
            worstDrift = max(worstDrift, orthonormalityDefect(pullback.columns));
            for (var entry in pullback.thirdRow)
            {
                worstThirdRow = max(worstThirdRow, abs(entry));
            }
            worstTurn = max(worstTurn,
                abs(pullback.columns[0][0] - cos(rotatingTubeTurn(t) * radian)));
        }
        println("[ROTATING TUBE SELF TEST] stored rotation: drift " ~ worstDrift ~
            ", worst |cos - stored| " ~ worstTurn ~ ", worst |M third row| " ~ worstThirdRow);
        tally = checkWithin(tally, worstDrift, ROTATING_TUBE_DRIFT_LIMIT,
            "the stored rotation's orthonormality drift");
        // Zero by construction - the z column is constant, so zhat . x' = zhat . y' = 0 - but the
        // column arrives through a derivative spline built from differences, so what survives is
        // rounding (3.6e-15 measured), not structure. A nonzero entry HERE would make f cubic in
        // v and void the closed form, so the bound has to be tight enough to catch that.
        tally = checkWithin(tally, worstThirdRow, 1e-13,
            "M's third row - a nonzero entry there makes f cubic in v and voids the closed form");

        // ---------- The closed form is a root of the shipped f ----------
        var worstClosedForm = 0;
        for (var timeIndex = 0; timeIndex <= 8; timeIndex += 1)
        {
            const t = timeIndex / 8;
            for (var uIndex = 0; uIndex < 24; uIndex += 1)
            {
                const u = TUBE_FIXTURE_DOMAIN_START + uPeriod * uIndex / 24;
                worstClosedForm = max(worstClosedForm,
                    abs(evaluateEnvelopePointwise(motion, barrel, u, rotatingTubeExactV(motion, u, t), t)));
            }
        }
        println("[ROTATING TUBE SELF TEST] |f| at the closed-form contact v: " ~ worstClosedForm);
        tally = checkWithin(tally, worstClosedForm, 1e-15, "|f| at the closed-form contact v");

        // ---------- The contact set MOVES, and it is the rotation that moves it ----------
        var worstTravel = 0;
        var worstRotationGap = 0;
        var worstBand = 0;
        for (var uIndex = 0; uIndex < 24; uIndex += 1)
        {
            const u = TUBE_FIXTURE_DOMAIN_START + uPeriod * uIndex / 24;
            const atStart = rotatingTubeExactV(motion, u, 0);
            for (var timeIndex = 0; timeIndex <= 8; timeIndex += 1)
            {
                const t = timeIndex / 8;
                const moving = rotatingTubeExactV(motion, u, t);
                worstTravel = max(worstTravel, abs(moving - atStart));
                worstBand = max(worstBand, abs(moving - 0.5));
                worstRotationGap = max(worstRotationGap,
                    abs(moving - rotatingTubeExactV(straight, u, t)));
            }
        }
        println("[ROTATING TUBE SELF TEST] contact set: travel " ~ worstTravel ~
            ", worst |v - 0.5| " ~ worstBand ~ ", gap against the same motion with A = I " ~
            worstRotationGap);
        tally = checkThat(tally, worstTravel > 0.1,
            "the contact set travelled only " ~ worstTravel ~ " - a one-parameter subgroup " ~
            "would give a t-independent contact set, which is what this fixture exists to avoid.");
        tally = checkThat(tally, worstRotationGap > 0.1,
            "the rotation moved the contact curve by only " ~ worstRotationGap ~ ", so this is " ~
            "not meaningfully a rotation test.");
        tally = checkThat(tally, worstBand < 0.45,
            "the contact curve reached |v - 0.5| = " ~ worstBand ~ ", off the barrel.");

        // ---------- One marched station lands on the closed-form curve ----------
        const probeT = 0.7;
        const probeLoop = tubeLoopSamples(motion, barrel, probeT, uSeed, 12, tubeOptions);
        tally = checkThat(tally, !probeLoop.failed,
            "the rotating tube loop at t = " ~ probeT ~ " failed: " ~ probeLoop.reason ~ ".");
        if (!probeLoop.failed)
        {
            var worstMarched = 0;
            for (var uv in probeLoop.uvRow)
            {
                worstMarched = max(worstMarched, abs(uv[1] - rotatingTubeExactV(motion, uv[0], probeT)));
            }
            println("[ROTATING TUBE SELF TEST] marched loop at t = " ~ probeT ~ ": uTravel " ~
                probeLoop.uTravel ~ " (period " ~ uPeriod ~ "), section residual " ~
                probeLoop.worstResidual ~ ", worst v vs the closed form " ~ worstMarched);
            tally = checkThat(tally, probeLoop.uTravel == uPeriod,
                "the loop travelled " ~ probeLoop.uTravel ~ " in u, not one period.");
            tally = checkWithin(tally, probeLoop.worstResidual, 1e-11, "the marched section residual");
            // The marcher stops once |f| <= sectionTolerance, and a value residual converts to a
            // v error of sectionTolerance / |f_v|. This fixture's contact function is FLAT in v -
            // the quadratic coefficient K is small - so |f_v| runs about 5e-4 and the floor sits
            // near 2e-10, two orders looser than the straight-translation barrel where a steeper
            // f_v hides the same residual. Tightening sectionTolerance would buy nothing: the fit
            // is q-limited at 3e-5, ten orders above this.
            tally = checkWithin(tally, worstMarched, 1e-9,
                "the marched v against the closed-form contact curve");
        }

        // ---------- The fit, and the fold certificate on a varying lambda ----------
        const tubeFit = fitTubeComponent(motion, barrel, mergeMaps(tubeOptions, {
                        "tStart" : 0, "tEnd" : 1, "uSeed" : uSeed, "tolerance" : ROTATING_TUBE_TOLERANCE,
                        "initialQCount" : 14, "initialStationCount" : 8,
                        "maxQCount" : 55, "maxStationCount" : 29, "maxRefinementRounds" : 3
                    }));
        tally = checkThat(tally, !tubeFit.failed, "the rotating tube fit failed: " ~ tubeFit.reason ~ ".");
        if (!tubeFit.failed)
        {
            println("[ROTATING TUBE SELF TEST] fit: " ~ tubeFit.stationCount ~ "x" ~ tubeFit.qCount ~
                " grid in " ~ tubeFit.refinementRounds ~ " round(s), deviation " ~
                tubeFit.worstDeviation ~ " (q " ~ tubeFit.worstQDeviation ~ ", t " ~
                tubeFit.worstTDeviation ~ "), section residual " ~ tubeFit.worstSectionResidual ~
                ", budgetHit " ~ tubeFit.budgetHit);
            tally = checkThat(tally, !tubeFit.budgetHit && tubeFit.worstDeviation <= ROTATING_TUBE_TOLERANCE,
                "the rotating tube fit missed tolerance (deviation " ~ tubeFit.worstDeviation ~ ").");
            tally = checkThat(tally, tubeFit.surface.isVPeriodic == true,
                "the rotating tube fit is not periodic in q.");

            // This is the point of the whole fixture: unlike every earlier step-7 measurement,
            // lambda here is not constant by construction, so one-signedness is a real result
            // rather than an identity.
            println("[ROTATING TUBE SELF TEST] orientation: " ~ tubeFit.orientation.sampleCount ~
                " samples, lambda sign " ~ tubeFit.orientation.lambdaSign ~ " consistent " ~
                tubeFit.orientation.lambdaSignConsistent ~ ", worst fold margin " ~
                tubeFit.orientation.worstFoldMargin ~ ", faces outward " ~
                tubeFit.orientation.facesOutward ~ " unanimous " ~
                tubeFit.orientation.verdictUnanimous ~ ", difference " ~
                tubeFit.orientation.differenceAgreements ~ "/" ~
                tubeFit.orientation.differenceChecked ~ " agree, stationary " ~
                tubeFit.orientation.stationarySamples ~ ", degenerate " ~
                tubeFit.orientation.degenerateSamples ~ ", consistent " ~
                tubeFit.orientation.consistent);
            tally = checkThat(tally, tubeFit.orientation.verdictUnanimous,
                "the rotating tube's outward verdict was not unanimous.");
            tally = checkThat(tally, tubeFit.orientation.lambdaSignConsistent,
                "lambda changed sign across the rotating component, so the fold certificate " ~
                "fired - the fixture is meant to keep it one-signed at a fold margin near 0.73.");
            tally = checkThat(tally, tubeFit.orientation.consistent,
                "the rotating tube orientation certificate did not come back consistent.");

            // Closed-form membership at t values no station and no midpoint station used.
            var worstMembership = 0;
            for (var freshT in [0.31, 0.83])
            {
                for (var index = 0; index < 9; index += 1)
                {
                    const u = TUBE_FIXTURE_DOMAIN_START + uPeriod * index / 9;
                    const analyticPoint = liftContactPoint(motion, barrel, u,
                        rotatingTubeExactV(motion, u, freshT), freshT);
                    worstMembership = max(worstMembership,
                        invertPointOnSurfaceFromGrid(tubeFit.surface, analyticPoint, 8).residual);
                }
            }
            println("[ROTATING TUBE SELF TEST] closed-form envelope membership: " ~ worstMembership);
            tally = checkWithin(tally, worstMembership, ROTATING_TUBE_TOLERANCE,
                "closed-form envelope membership");
        }

        reportCheckTally(context, id, "ROTATING TUBE SELF TEST", tally,
            "a genuinely rotating motion carries the contact set across the barrel, the quadratic " ~
            "closed form tracks it, and the fitted periodic patch certifies with a one-signed " ~
            "lambda - the first fitted patch in the project whose motion is not a pure translation.");
    });

annotation { "Feature Type Name" : "Sweep Tube Ellipsoid Live Test" }
export const sweepTubeEllipsoidLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        // Half an ellipse revolved about the global X axis: one smooth face, a seam, and a
        // degenerate pole at each end - the first REAL tool the tube path sees, and the shape
        // the whole of step 7 is aimed at.
        const semiAxial = 0.05;
        const semiRadial = 0.03;
        const sketchId = id + "ellipsoidSketch";
        const profileSketch = newSketchOnPlane(context, sketchId, {
                    "sketchPlane" : plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0))
                });
        skEllipse(profileSketch, "profile", {
                    "center" : vector(0, 0) * meter,
                    "majorRadius" : semiAxial * meter,
                    "minorRadius" : semiRadial * meter
                });
        skLineSegment(profileSketch, "axisCut", {
                    "start" : vector(-2 * semiAxial, 0) * meter,
                    "end" : vector(2 * semiAxial, 0) * meter
                });
        skSolve(profileSketch);
        opRevolve(context, id + "ellipsoid", {
                    "entities" : qNthElement(qSketchRegion(sketchId), 0),
                    "axis" : line(vector(0, 0, 0) * meter, vector(1, 0, 0)),
                    "angleForward" : 360 * degree
                });
        opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

        const records = extractToolFaceRecords(context, qCreatedBy(id + "ellipsoid", EntityType.BODY), 1e-7);
        println("[ELLIPSOID LIVE TEST] " ~ summarizeFaceRecords(records));
        if (size(records) != 1 || records[0].spline == undefined)
        {
            failures = failures ~ " the ellipsoid did not extract as one spline-bearing face.";
        }
        else
        {
            const record = records[0];
            println("[ELLIPSOID LIVE TEST] extracted spline: " ~ describeSurfaceShape(record.spline));
            // A surface of revolution is periodic in exactly one direction and collapsed at
            // both ends of the other. Extraction reports both; nothing here assumes which
            // direction the kernel chose.
            const uPeriodic = record.spline.isUPeriodic == true;
            const vPeriodic = record.spline.isVPeriodic == true;
            const poles = record.degenerate;
            const polesAcrossV = poles.vStart && poles.vEnd;
            const polesAcrossU = poles.uStart && poles.uEnd;
            if (uPeriodic == vPeriodic)
            {
                failures = failures ~ " the ellipsoid is periodic in " ~ (uPeriodic ? "both" : "neither") ~
                    " direction, expected exactly one.";
            }
            if (!(uPeriodic && polesAcrossV) && !(vPeriodic && polesAcrossU))
            {
                failures = failures ~ " the poles were not found at both ends of the non-periodic direction.";
            }

            // Everything below wants u circumferential, which is how the tube marcher is
            // written; transposing costs nothing and makes the test independent of the
            // kernel's choice.
            const toolSurface = vPeriodic ? transposeSurface(record.spline) : record.spline;
            const domain = surfaceKnotDomain(toolSurface);
            const uPeriod = domain.uEnd - domain.uStart;
            const vSpan = domain.vEnd - domain.vStart;

            // Velocity mostly along the ellipsoid's own axis, tilting sideways as t runs. The
            // contact set of an ellipsoid under a translation is exactly its intersection with
            // the CENTRAL PLANE <grad F, w> = 0, so the marched loop has a closed-form test
            // that owes nothing to this module.
            const motion = translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0.06, 0.12], [0, 0, 0],
                [0, 0, 0, 0, 1, 1, 1, 1]);
            const probeT = 0.6;
            const loop = tubeLoopSamples(motion, toolSurface, probeT, domain.uStart + 0.5 * uPeriod, 10, {
                        "vMargin" : 0.1 * vSpan, "sectionTolerance" : 1e-13, "meridianSamples" : 32
                    });
            if (loop.failed)
            {
                failures = failures ~ " the ellipsoid tube loop failed: " ~ loop.reason ~ ".";
            }
            else
            {
                const velocity = evaluateMotionSample(motion, probeT).translationDerivative;
                const velocityDirection = velocity / norm(velocity);
                var worstRadial = 0;
                var worstPlaneSine = 0;
                for (var uv in loop.uvRow)
                {
                    const wrapped = domain.uStart + positiveModulo(uv[0] - domain.uStart, uPeriod);
                    const point = evaluateBSplineSurfacePoint(toolSurface, wrapped, uv[1]);
                    const scaled = vector(point[0] / semiAxial ^ 2, point[1] / semiRadial ^ 2,
                        point[2] / semiRadial ^ 2);
                    worstRadial = max(worstRadial, abs(sqrt(point[0] ^ 2 / semiAxial ^ 2 +
                                    (point[1] ^ 2 + point[2] ^ 2) / semiRadial ^ 2) - 1) * semiRadial);
                    worstPlaneSine = max(worstPlaneSine, abs(dot(scaled / norm(scaled), velocityDirection)));
                }
                println("[ELLIPSOID LIVE TEST] tube loop at t = " ~ probeT ~ ": uTravel " ~ loop.uTravel ~
                    " (period " ~ uPeriod ~ "), section residual " ~ loop.worstResidual ~
                    ", off the ellipsoid " ~ worstRadial ~ " m, off the analytic contact plane " ~
                    worstPlaneSine ~ " (sine)");
                if (loop.uTravel != uPeriod)
                {
                    failures = failures ~ " the ellipsoid loop travelled " ~ loop.uTravel ~ ", not one period.";
                }
                if (worstRadial > 1e-5)
                {
                    failures = failures ~ " marched points sit " ~ worstRadial ~ " m off the ellipsoid.";
                }
                if (worstPlaneSine > 1e-3)
                {
                    failures = failures ~ " marched normals are " ~ worstPlaneSine ~ " off perpendicular to the velocity.";
                }
            }
        }

        reportTestVerdict(context, id, "ELLIPSOID LIVE TEST", failures,
            "a revolved ellipsoid extracts non-rational with both poles found, and its " ~
            "wrapping contact loop lands on the analytic central-plane section.");
    });

annotation { "Feature Type Name" : "Sweep Tube Patch Live Test" }
export const sweepTubePatchLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";

        // ---------- Regression: the periodic freeform wall that forceNonRational broke ----------
        // An elliptical extrude's wall is the step-4 fixture whose forced non-rational
        // approximation comes back in a periodic spelling normalizeSurfaceDefinition refuses to
        // guess at. Extraction now asks for the rational form, which converts exactly.
        const wallSketchId = id + "wallSketch";
        const wallSketch = newSketchOnPlane(context, wallSketchId, {
                    "sketchPlane" : plane(vector(0.25, 0, 0) * meter, vector(0, 0, 1))
                });
        skEllipse(wallSketch, "ellipse1", {
                    "center" : vector(0, 0) * meter,
                    "majorRadius" : 0.03 * meter,
                    "minorRadius" : 0.015 * meter
                });
        skSolve(wallSketch);
        opExtrude(context, id + "wallExtrude", {
                    "entities" : qSketchRegion(wallSketchId),
                    "direction" : vector(0, 0, 1),
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : 0.05 * meter
                });
        opDeleteBodies(context, id + "deleteWallSketch", {
                    "entities" : qCreatedBy(wallSketchId, EntityType.BODY)
                });
        const wallRecords = extractToolFaceRecords(context,
            qCreatedBy(id + "wallExtrude", EntityType.BODY), 1e-6);
        println("[TUBE PATCH LIVE TEST] elliptical extrude: " ~ summarizeFaceRecords(wallRecords));
        var wallSpline = undefined;
        for (var record in wallRecords)
        {
            if (record.spline != undefined)
            {
                wallSpline = record.spline;
            }
        }
        if (wallSpline == undefined)
        {
            failures = failures ~ " the elliptical extrude wall produced no spline.";
        }
        else
        {
            println("[TUBE PATCH LIVE TEST] wall spline: " ~ describeSurfaceShape(wallSpline) ~
                ", control net " ~ size(wallSpline.controlPoints) ~ "x" ~ size(wallSpline.controlPoints[0]));
            const wallInversion = invertPointOnSurfaceFromGrid(wallSpline,
                evaluateBSplineSurfacePoint(wallSpline, 0.31, 0.42), 6);
            println("[TUBE PATCH LIVE TEST] wall inversion round trip: " ~ wallInversion.residual ~ " m");
            if (wallInversion.residual > 1e-9)
            {
                failures = failures ~ " wall inversion residual " ~ wallInversion.residual ~ " m.";
            }
        }

        // ---------- The ellipsoid's lateral envelope ----------
        const tool = ellipsoidToolFixture(context, id + "tool", ELLIPSOID_SEMI_AXIAL, ELLIPSOID_SEMI_RADIAL, 1e-6);
        println("[TUBE PATCH LIVE TEST] tool: " ~ describeSurfaceShape(tool.surface) ~
            ", control net " ~ size(tool.surface.controlPoints) ~ "x" ~ size(tool.surface.controlPoints[0]));

        // A STRAIGHT translation, deliberately off the ellipsoid's own axis. Straight is the
        // case whose swept volume is exact - a Minkowski sum with a segment - which is the
        // check the whole assembly gets measured against once the caps land.
        const direction = normalize(vector(1, 0.35, 0));
        const travel = 0.18;
        const displacement = travel * direction;
        const motion = translationMotionFromQuadraticVelocity(
            [displacement[0], displacement[0], displacement[0]],
            [displacement[1], displacement[1], displacement[1]],
            [displacement[2], displacement[2], displacement[2]],
            [0, 0, 0, 0, 1, 1, 1, 1]);

        const fit = fitTubeComponent(motion, tool.surface, {
                    "tStart" : 0, "tEnd" : 1,
                    "uSeed" : tool.domain.uMin + 0.5 * (tool.domain.uMax - tool.domain.uMin),
                    "tolerance" : 1e-5,
                    "vMargin" : 0.08 * (tool.domain.vMax - tool.domain.vMin),
                    "sectionTolerance" : 1e-13, "meridianSamples" : 32,
                    "initialQCount" : 16, "initialStationCount" : 5,
                    "maxQCount" : 31, "maxStationCount" : 9, "maxRefinementRounds" : 2
                });
        if (fit.failed)
        {
            failures = failures ~ " the ellipsoid tube fit failed: " ~ fit.reason ~ ".";
        }
        else
        {
            println("[TUBE PATCH LIVE TEST] fit: " ~ fit.stationCount ~ "x" ~ fit.qCount ~ " grid in " ~
                fit.refinementRounds ~ " round(s), deviation " ~ fit.worstDeviation ~ " (q " ~
                fit.worstQDeviation ~ ", t " ~ fit.worstTDeviation ~ "), budgetHit " ~ fit.budgetHit);
            if (fit.budgetHit || fit.worstDeviation > 1e-5)
            {
                failures = failures ~ " the tube fit missed 1e-5 (deviation " ~ fit.worstDeviation ~ ").";
            }

            // The unproven kernel question: a v-PERIODIC net is cylinder topology, not the
            // whole-island shape section 7.4 found rejected. Emit it and see.
            opCreateBSplineSurface(context, id + "tubePatch", {
                        "bSplineSurface" : kernelFitSurface(attachFitSurfaceUnits(fit.surface), true)
                    });
            const patchFaces = evaluateQuery(context, qCreatedBy(id + "tubePatch", EntityType.FACE));
            println("[TUBE PATCH LIVE TEST] emitted " ~ size(patchFaces) ~ " face(s) from a " ~
                size(fit.surface.controlPoints) ~ "x" ~ size(fit.surface.controlPoints[0]) ~
                " periodic net");
            if (size(patchFaces) != 1)
            {
                failures = failures ~ " the periodic tube net emitted " ~ size(patchFaces) ~ " faces.";
            }

            // Kernel certification against fresh envelope points at stations the fit never used.
            var freshPoints = [];
            for (var freshT in [0.27, 0.63])
            {
                const freshLoop = tubeLoopSamples(motion, tool.surface, freshT,
                    tool.domain.uMin + 0.5 * (tool.domain.uMax - tool.domain.uMin), 8, {
                            "vMargin" : 0.08 * (tool.domain.vMax - tool.domain.vMin),
                            "sectionTolerance" : 1e-13, "meridianSamples" : 32
                        });
                if (freshLoop.failed)
                {
                    failures = failures ~ " fresh loop at t = " ~ freshT ~ " failed.";
                    continue;
                }
                for (var point in freshLoop.liftedRow)
                {
                    freshPoints = append(freshPoints, point * meter);
                }
            }
            if (size(freshPoints) > 0)
            {
                const patchDeviation = evPointsDeviation(context, {
                                "points" : freshPoints,
                                "topologies" : qCreatedBy(id + "tubePatch", EntityType.FACE)
                            })[0].deviation;
                println("[TUBE PATCH LIVE TEST] kernel deviation vs fresh envelope points: " ~ patchDeviation);
                if (patchDeviation > 1e-4 * meter)
                {
                    failures = failures ~ " emitted patch deviation " ~ (patchDeviation / meter) ~ " m.";
                }
            }
        }

        reportTestVerdict(context, id, "TUBE PATCH LIVE TEST", failures,
            "the ellipsoid's lateral envelope fits, emits as ONE periodic B-spline face, and " ~
            "kernel-certifies against fresh envelope points.");
    });

annotation { "Feature Type Name" : "Sweep Solid Assembly Live Test" }
export const sweepSolidAssemblyLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Contact loop samples" }
        isInteger(definition.loopSamples, ASSEMBLY_LOOP_SAMPLE_BOUNDS);

        annotation { "Name" : "Face extraction tolerance" }
        isLength(definition.extractTolerance, ASSEMBLY_EXTRACT_BOUNDS);
    }
    {
        // The two knobs carry their own defaults so the feature runs identically whether it is
        // inserted in the UI or executed with an empty definition by the test harness.
        const loopSamples = definition.loopSamples == undefined ? 61 : definition.loopSamples;
        const extractTolerance = definition.extractTolerance == undefined ?
            1e-7 : definition.extractTolerance / meter;
        var tally = newCheckTally();

        // ---------- The step-7 fixture ----------
        // The ellipsoid of spec 7.7 under the same STRAIGHT translation, deliberately off its
        // own axis. Straight is the case with an exact answer: the swept volume of a convex tool
        // along a segment is a Minkowski sum, V = V_tool + A_silhouette * L, so this run is
        // measured against closed-form truth rather than against itself.
        const semiAxial = ELLIPSOID_SEMI_AXIAL;
        const semiRadial = ELLIPSOID_SEMI_RADIAL;
        const tool = ellipsoidToolFixture(context, id + "tool", semiAxial, semiRadial, extractTolerance);
        println("[SOLID ASSEMBLY LIVE TEST] tool extracted at " ~ extractTolerance ~ " m: " ~
            describeSurfaceShape(tool.surface) ~ ", control net " ~ size(tool.surface.controlPoints) ~
            "x" ~ size(tool.surface.controlPoints[0]));

        const direction = normalize(vector(1, 0.35, 0));
        const travel = 0.18;
        const displacement = travel * direction;
        const motion = translationMotionFromQuadraticVelocity(
            [displacement[0], displacement[0], displacement[0]],
            [displacement[1], displacement[1], displacement[1]],
            [displacement[2], displacement[2], displacement[2]],
            [0, 0, 0, 0, 1, 1, 1, 1]);

        // ---------- The kernel's face-normal convention, measured rather than assumed ----------
        // Cap classification reads evFaceTangentPlane's normal as the OUTWARD one. On the tool
        // itself that is checkable in closed form: the ellipsoid's outward direction at p is
        // (x/a^2, y/b^2, z/b^2), and nothing downstream is meaningful if the two disagree.
        const toolSample = faceInteriorTangentPlane(context,
            qNthElement(qOwnedByBody(tool.body, EntityType.FACE), 0), 2);
        tally = checkThat(tally, toolSample.found, "no interior sample landed on the tool's face.");
        if (toolSample.found)
        {
            const samplePoint = toolSample.plane.origin / meter;
            const outwardReference = vector(samplePoint[0] / semiAxial ^ 2,
                samplePoint[1] / semiRadial ^ 2, samplePoint[2] / semiRadial ^ 2);
            const outwardAgreement = dot(toolSample.plane.normal, normalize(outwardReference));
            println("[SOLID ASSEMBLY LIVE TEST] face normal vs the ellipsoid's outward direction: " ~
                outwardAgreement);
            tally = checkThat(tally, outwardAgreement > 0.99,
                "the kernel's face normal is not the outward one (agreement " ~ outwardAgreement ~
                "), so the cap sign convention is inverted.");
        }

        // ---------- The lateral envelope ----------
        // ONE round at the requested sampling: a straight translation makes the tube exactly
        // linear in t, so five stations are exact there (measured 2.5e-16 in spec 7.7) and every
        // sample bought is spent on q, which is the direction the seam gap comes from.
        // tool.frame, not tool.surface: this selects the ANALYTIC overload of fitTubeComponent by
        // type, and the marched one is unreachable from here.
        const fit = fitTubeComponent(motion, tool.frame, {
                    "tStart" : 0, "tEnd" : 1,
                    "tolerance" : 1e-9,
                    "profileSamples" : 96,
                    "initialQCount" : loopSamples, "initialStationCount" : 5,
                    "maxQCount" : loopSamples, "maxStationCount" : 5, "maxRefinementRounds" : 1
                });
        tally = checkThat(tally, !fit.failed, "the tube fit failed: " ~ (fit.failed ? fit.reason : ""));
        if (fit.failed)
        {
            reportCheckTally(context, id, "SOLID ASSEMBLY LIVE TEST", tally, "");
            return;
        }
        println("[SOLID ASSEMBLY LIVE TEST] fit: " ~ fit.stationCount ~ "x" ~ fit.qCount ~
            " grid, deviation " ~ fit.worstDeviation ~ " (q " ~ fit.worstQDeviation ~ ", t " ~
            fit.worstTDeviation ~ ")");
        tally = checkWithin(tally, fit.worstDeviation, 1e-5, "the tube fit deviation");

        opCreateBSplineSurface(context, id + "patch", {
                    "bSplineSurface" : kernelFitSurface(attachFitSurfaceUnits(fit.surface), true)
                });
        const patchBody = qCreatedBy(id + "patch", EntityType.BODY);
        tally = checkThat(tally, size(evaluateQuery(context, qCreatedBy(id + "patch", EntityType.FACE))) == 1,
            "the lateral patch did not emit as one face.");

        // ---------- Section 9 ----------
        const assembly = assembleSweptSolid(context, id + "assembly", {
                    "toolBody" : tool.body,
                    "shellBodies" : patchBody,
                    "caps" : [
                        {
                            "motionSample" : evaluateMotionSample(motion, 0),
                            "isStart" : true,
                            "contactCurves" : [fitBoundaryContactCurve(fit.surface, true)]
                        },
                        {
                            "motionSample" : evaluateMotionSample(motion, 1),
                            "isStart" : false,
                            "contactCurves" : [fitBoundaryContactCurve(fit.surface, false)]
                        }
                    ]
                });
        println("[SOLID ASSEMBLY LIVE TEST] " ~ summarizeSweptSolid(assembly));
        for (var report in assembly.capReports)
        {
            println("[SOLID ASSEMBLY LIVE TEST] cap " ~ report.capIndex ~ ": stage " ~ report.stage ~
                ", imprint " ~ (report.imprint == undefined ? "-" :
                    ("" ~ report.imprint.faceCountBefore ~ " -> " ~ report.imprint.faceCountAfter ~ " faces, " ~
                        size(report.imprint.splittingEdges) ~ " splitting edge(s)")) ~
                ", seam gap " ~ (report.seamGap == undefined ? "-" : toString(report.seamGap)) ~
                " m, signs " ~ (report.signs == undefined ? "-" : toString(report.signs)) ~
                (report.reason == undefined || report.reason == "" ? "" : (", " ~ report.reason)));
        }

        // The section 2.3 ledger, assembled from the terms this run measured.
        const ledgerSum = assembly.worstRigidityDefect + extractTolerance + fit.worstDeviation +
            assembly.worstSeamGap;
        println("[SOLID ASSEMBLY LIVE TEST] error ledger: motion " ~ assembly.worstRigidityDefect ~
            " + faceExtract " ~ extractTolerance ~ " + envelopeFit " ~ fit.worstDeviation ~
            " + knit slop " ~ assembly.worstSeamGap ~ " = " ~ ledgerSum ~ " m");

        // The ellipsoid is ONE face, so a cap that imprinted and classified correctly keeps
        // exactly one half and deletes the other. A cap that never reached the trim stage is
        // covered by the assembly check below instead.
        var trimmedCaps = 0;
        for (var report in assembly.capReports)
        {
            if (report.stage != "trim")
            {
                continue;
            }
            trimmedCaps += 1;
            tally = checkThat(tally, report.keptFaceCount == 1 && report.deletedFaceCount == 1,
                "cap " ~ report.capIndex ~ " split the ellipsoid into " ~ report.keptFaceCount ~
                " kept and " ~ report.deletedFaceCount ~ " deleted faces, expected one of each.");
        }
        tally = checkThat(tally, trimmedCaps == 2,
            "only " ~ trimmedCaps ~ " of 2 caps reached the trim stage.");
        tally = checkThat(tally, !assembly.failed, "the assembly did not close: " ~ assembly.reason);
        if (assembly.failed)
        {
            reportCheckTally(context, id, "SOLID ASSEMBLY LIVE TEST", tally,
                "the certified sheets are left in the context (spec 9.3).");
            return;
        }

        // ---------- Section 14: one solid, no slivers, volume, deviation ----------
        const solid = assembly.solidBody;
        const quality = assembly.quality;
        tally = checkThat(tally, assembly.knit.solidCount == 1,
            "the knit produced " ~ assembly.knit.solidCount ~ " solid bodies.");
        tally = checkThat(tally, quality.minEdgeLength > 1e-5,
            "the shortest edge is " ~ quality.minEdgeLength ~ " m, under the 1e-5 m sliver floor.");
        tally = checkThat(tally, quality.minFaceArea > 1e-9,
            "the smallest face is " ~ quality.minFaceArea ~ " m^2, a sliver.");

        // Exact volume: Minkowski sum of the ellipsoid with the travel segment.
        const toolVolume = 4 / 3 * PI * semiAxial * semiRadial ^ 2;
        const silhouetteArea = PI * semiAxial * semiRadial ^ 2 *
            sqrt((direction[0] / semiAxial) ^ 2 + (direction[1] / semiRadial) ^ 2 +
                (direction[2] / semiRadial) ^ 2);
        const exactVolume = toolVolume + silhouetteArea * travel;
        const volumeError = abs(quality.volume - exactVolume) / exactVolume;
        println("[SOLID ASSEMBLY LIVE TEST] volume " ~ quality.volume ~ " m^3 against the exact " ~
            exactVolume ~ " m^3 (tool " ~ toolVolume ~ " + silhouette " ~ silhouetteArea ~
            " x " ~ travel ~ "), relative error " ~ volumeError);
        tally = checkWithin(tally, volumeError, 1e-3, "the swept volume's relative error");

        // Deviation against fresh envelope points at stations the fit never used. The fit consumed
        // stations 0, 0.25 .. 1 and their midpoints, so t = 0.27 and 0.63 are points the surface
        // has never seen whichever solver produces them. They come from the ANALYTIC route here:
        // the marched one is the slow path, and the cross-route agreement it would test is what
        // the A/B test exists to measure, so re-deriving it in the assembly run buys nothing.
        var freshPoints = [];
        for (var freshT in [0.27, 0.63])
        {
            const freshLoop = analyticTubeLoopSamples(motion, tool.frame, freshT, 12, {
                        "profileSamples" : 96
                    });
            if (freshLoop.failed)
            {
                tally = checkThat(tally, false, "the fresh envelope loop at t = " ~ freshT ~ " failed.");
                continue;
            }
            for (var point in freshLoop.liftedRow)
            {
                freshPoints = append(freshPoints, meter * point);
            }
        }
        if (size(freshPoints) > 0)
        {
            const solidDeviation = evPointsDeviation(context, {
                            "points" : freshPoints,
                            "topologies" : solid
                        })[0].deviation / meter;
            println("[SOLID ASSEMBLY LIVE TEST] solid deviation vs fresh envelope points: " ~
                solidDeviation ~ " m");
            tally = checkWithin(tally, solidDeviation, 1e-4, "the solid's envelope deviation");
        }

        println("[SOLID ASSEMBLY LIVE TEST] reached the tally with " ~ tally.checks ~ " check(s), " ~
            tally.failures ~ " failed");

        // The tool copy the caps were cut from has served its purpose; the swept solid is the
        // only body this test means to leave behind.
        opDeleteBodies(context, id + "deleteTool", { "entities" : tool.body });

        reportCheckTally(context, id, "SOLID ASSEMBLY LIVE TEST", tally,
            "an ellipsoid swept along a straight translation closes to ONE solid body whose " ~
            "volume matches the Minkowski-sum anchor and whose surface carries fresh envelope " ~
            "points to tolerance.");
    // The defaults map is what lets this feature run with an EMPTY definition - inserted in the
    // UI the bound specs would supply them, but a programmatic invocation hands the precondition
    // undefined parameters and fails before the body is reached.
    }, { "loopSamples" : 61, "extractTolerance" : 1e-7 * meter });

annotation { "Feature Type Name" : "Sweep Trig Root Closed Form Self Test" }
export const sweepTrigRootClosedFormSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // The closed form replaced a scan that cost 82% of the analytic contact solve, so the scan
        // is kept and becomes the ORACLE: every case below is solved both ways and the two must
        // agree. That is the only honest way to trust a new root solver - a closed form validated
        // against itself proves nothing, and a wrong one produces silently wrong contact curves.
        var tally = newCheckTally();

        // Cases chosen to hit every branch of the quartic reduction, not to look thorough:
        //   generic degree 2; a DOUBLE root (tangency); leading coefficient zero, which is exactly
        //   "theta = pi is a root" and the one value the half-angle substitution cannot represent;
        //   the biquadratic case q == 0, where s may legitimately vanish; degree 1; a polynomial
        //   with no roots at all; and cos 2x, whose four roots are known exactly.
        const cases = [
                ["generic", [0.13, -0.44, 0.28], [0, 0.51, -0.33]],
                ["cos 2x", [0, 0, 1], [0, 0, 0]],
                ["double root at 0", [-1, 1, 0], [0, 0, 0]],
                ["leading zero (root at pi)", [0.5, 0.9, 0.4], [0, 0.2, -0.1]],
                ["biquadratic", [0.2, 0, 0.5], [0, 0, 0]],
                ["degree 1", [0.2, 0.7, 0], [0, -0.3, 0]],
                ["no roots", [3.0, 0.4, 0.2], [0, 0.1, -0.05]],
                ["tiny second harmonic", [0.01, -0.3, 1e-9], [0, 0.2, 5e-10]],
                ["first harmonic only", [0, 1, 0], [0, 1, 0]]
            ];

        var worstRootDisagreement = 0;
        var worstClosedFormResidual = 0;
        var countMismatches = "";
        for (var entry in cases)
        {
            const polynomial = trigPolynomial(entry[1], entry[2]);
            const closed = solveTrigPolynomialRoots(polynomial, 0, {});
            const scanned = solveTrigPolynomialRoots(polynomial, 0, { "closedForm" : false });

            var worstForCase = 0;
            for (var root in closed.roots)
            {
                worstClosedFormResidual = max(worstClosedFormResidual,
                    abs(trigPolynomialValue(polynomial, root)));
                // Nearest scanned root: the two paths may order or bracket differently, and what
                // matters is that every closed-form root is one the scan also found.
                var nearest = 1e300;
                for (var other in scanned.roots)
                {
                    nearest = min(nearest, abs(root - other));
                }
                if (size(scanned.roots) > 0)
                {
                    worstForCase = max(worstForCase, nearest);
                }
            }
            worstRootDisagreement = max(worstRootDisagreement, worstForCase);
            println("[TRIG ROOT SELF TEST] " ~ entry[0] ~ ": closed form " ~ size(closed.roots) ~
                " root(s), scan " ~ size(scanned.roots) ~ ", worst separation " ~ worstForCase ~
                ", closed-form tangency " ~ closed.nearTangency ~ ", scan tangency " ~
                scanned.nearTangency);
            if (size(closed.roots) < size(scanned.roots))
            {
                countMismatches = countMismatches ~ " " ~ entry[0] ~ " (closed form found " ~
                    size(closed.roots) ~ ", scan found " ~ size(scanned.roots) ~ ")";
            }
        }

        println("[TRIG ROOT SELF TEST] worst closed-form residual |P(root)| " ~
            worstClosedFormResidual ~ ", worst disagreement with the scan " ~ worstRootDisagreement);
        tally = checkWithin(tally, worstClosedFormResidual, 1e-12,
            "the closed form's own residual |P(root)|");
        tally = checkWithin(tally, worstRootDisagreement, 1e-7,
            "the closed form's disagreement with the scan it replaces");
        tally = checkThat(tally, countMismatches == "",
            "the closed form MISSED roots the scan found:" ~ countMismatches);

        // Tangency, asserted independently of both paths. A double root IS a root where P' vanishes,
        // so that is the oracle - not the scan's flag, which over-reports (it fires on cos 2x and on
        // a pure first harmonic, neither of which has a double root), and not root proximity, which
        // under-reports (a stable quadratic solver returns coincident roots once). Under-reporting
        // here would suppress a spec 6.4 rejection, so it gets its own check.
        var tangencyMismatches = "";
        for (var entry in cases)
        {
            const polynomial = trigPolynomial(entry[1], entry[2]);
            const closed = solveTrigPolynomialRoots(polynomial, 0, {});
            var derivativeAmplitude = 0;
            for (var harmonic = 1; harmonic < size(entry[1]); harmonic += 1)
            {
                derivativeAmplitude += harmonic *
                    sqrt(entry[1][harmonic] ^ 2 + entry[2][harmonic] ^ 2);
            }
            var expected = false;
            for (var root in closed.roots)
            {
                if (abs(trigPolynomialDerivative(polynomial, root)) <=
                    1e-7 * max(1e-300, derivativeAmplitude))
                {
                    expected = true;
                }
            }
            println("[TRIG ROOT SELF TEST] " ~ entry[0] ~ ": tangency expected " ~ expected ~
                ", closed form reports " ~ closed.nearTangency);
            if (expected != closed.nearTangency)
            {
                tangencyMismatches = tangencyMismatches ~ " " ~ entry[0] ~ " (expected " ~
                    expected ~ ", got " ~ closed.nearTangency ~ ")";
            }
        }
        tally = checkThat(tally, tangencyMismatches == "",
            "the closed form's tangency verdict disagrees with the vanishing-derivative test:" ~
            tangencyMismatches);

        // cos 2x has its four roots at the odd multiples of pi/4, exactly - a known answer, not a
        // cross-check.
        const doubled = solveTrigPolynomialRoots(trigPolynomial([0, 0, 1], [0, 0, 0]), 0, {});
        var worstDoubled = 0;
        for (var index = 0; index < size(doubled.roots); index += 1)
        {
            worstDoubled = max(worstDoubled, abs(doubled.roots[index] - (0.25 + 0.5 * index) * PI));
        }
        println("[TRIG ROOT SELF TEST] cos 2x closed form: " ~ size(doubled.roots) ~
            " roots, worst error against the exact odd multiples of pi/4 " ~ worstDoubled);
        tally = checkThat(tally, size(doubled.roots) == 4,
            "cos 2x gave " ~ size(doubled.roots) ~ " closed-form roots, expected 4.");
        tally = checkWithin(tally, worstDoubled, 1e-12, "cos 2x's closed-form root error");

        reportCheckTally(context, id, "TRIG ROOT SELF TEST", tally,
            "the closed-form degree-2 trigonometric root solver agrees with the scan it replaces " ~
            "on every branch of the quartic reduction - double roots, a root at pi the half-angle " ~
            "substitution cannot represent, the biquadratic case, and degree 1 - and reproduces " ~
            "cos 2x's exact roots.");
    });

annotation { "Feature Type Name" : "Sweep Probe - Iso-Curve Generator" }
export const sweepProbeIsoCurveGenerator = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // What does the kernel hand back for an UNTRIMMED iso-curve of a revolve, and which
        // parameter direction is the meridian? Both are measurements, not guesses: the enum names
        // DIR1/DIR2 rather than U/V, and section 7.6 already recorded that this kernel put the
        // circumferential direction in V for this very face. The discriminator is geometric - a
        // meridian holds theta constant around the axis, the circumferential curve does not.
        //
        // This probe exists because the ellipsoid A/B failed on "the recovered generator is an
        // ELLIPSE", and the fix depends on what this returns: a BSplineCurve needs no conversion, an
        // Ellipse needs an exact rational-quadratic conversion.
        const tool = ellipsoidToolFixture(context, id + "tool", ELLIPSOID_SEMI_AXIAL,
                ELLIPSOID_SEMI_RADIAL, 1e-7);
        const face = qNthElement(qOwnedByBody(tool.body, EntityType.FACE), 0);
        const axisLine is Line = evAxis(context, { "axis" : face });
        println("[ISO PROBE] axis through " ~ axisLine.origin ~ " along " ~ axisLine.direction);
        println("[ISO PROBE] periodicity " ~ evFacePeriodicity(context, { "face" : face }));

        const directions = [
                ["DIR1_ISO", FaceCurveCreationType.DIR1_ISO],
                ["DIR2_ISO", FaceCurveCreationType.DIR2_ISO]
            ];
        for (var entry in directions)
        {
            const label = entry[0];
            const curveId = id + ("iso" ~ label);
            var created = true;
            try silent
            {
                opCreateCurvesOnFace(context, curveId, {
                            "curveDefinition" : [curveOnFaceDefinition(face, entry[1],
                                    ["isoCurve"], [0.35])],
                            "skipTrim" : true,
                            "useFaceParameter" : true
                        });
            }
            catch
            {
                created = false;
            }
            if (!created)
            {
                println("[ISO PROBE] " ~ label ~ ": opCreateCurvesOnFace THREW.");
                continue;
            }
            const edges = evaluateQuery(context, qCreatedBy(curveId, EntityType.EDGE));
            println("[ISO PROBE] " ~ label ~ ": " ~ size(edges) ~ " edge(s)");
            for (var edgeIndex = 0; edgeIndex < size(edges); edgeIndex += 1)
            {
                const edge = edges[edgeIndex];
                const curveDefinition = evCurveDefinition(context, { "edge" : edge });
                var description = "keys " ~ keys(curveDefinition);
                if (curveDefinition is Line)
                    description = "Line";
                else if (curveDefinition is Circle)
                    description = "Circle radius " ~ curveDefinition.radius ~ ", axis " ~
                        curveDefinition.coordSystem.zAxis;
                else if (curveDefinition is Ellipse)
                    description = "Ellipse major " ~ curveDefinition.majorRadius ~ " minor " ~
                        curveDefinition.minorRadius ~ ", axis " ~ curveDefinition.coordSystem.zAxis;
                else if (curveDefinition is BSplineCurve)
                    description = "BSplineCurve degree " ~ curveDefinition.degree ~ ", " ~
                        size(curveDefinition.controlPoints) ~ " control points, rational " ~
                        (curveDefinition.isRational == true);

                // Meridian or circumferential? Sample three points and read theta about the axis.
                const samples = evEdgeTangentLines(context, { "edge" : edge, "parameters" : [0.1, 0.5, 0.9] });
                var worstThetaSpread = 0;
                const radial = normalize(perpendicularVector(axisLine.direction));
                const second = cross(axisLine.direction, radial);
                var firstTheta = undefined;
                for (var sample in samples)
                {
                    const offset = sample.origin - axisLine.origin;
                    const inPlane = offset - dot(offset, axisLine.direction) * axisLine.direction;
                    const theta = atan2(dot(inPlane, second) / meter, dot(inPlane, radial) / meter) / radian;
                    if (firstTheta == undefined)
                    {
                        firstTheta = theta;
                    }
                    else
                    {
                        worstThetaSpread = max(worstThetaSpread, abs(theta - firstTheta));
                    }
                }
                const closed = norm(evEdgeTangentLines(context, { "edge" : edge, "parameters" : [0, 1] })[0].origin -
                        evEdgeTangentLines(context, { "edge" : edge, "parameters" : [0, 1] })[1].origin);
                println("[ISO PROBE]   edge " ~ edgeIndex ~ ": " ~ description ~
                    "; theta spread over the curve " ~ worstThetaSpread ~
                    " rad (near 0 = MERIDIAN, the generator); endpoint gap " ~ closed ~
                    " (0 = closed curve, straddles the axis)");
            }
            opDeleteBodies(context, id + ("deleteIso" ~ label),
                { "entities" : qCreatedBy(curveId, EntityType.BODY) });
        }

        opDeleteBodies(context, id + "deleteTool", { "entities" : tool.body });
        reportTestVerdict(context, id, "ISO PROBE", "",
            "both iso-curve directions reported their curve class, whether they hold theta " ~
            "constant, and whether they close - which is what decides the generator route.");
    });

// ============================= The ellipsoid A/B: analytic route against sampled route =============================

/** Stations for the A/B. Odd counts put a station on 0.5; the ends are included. */
const ELLIPSOID_AB_STATION_BOUNDS =
{
    (unitless) : [2, 5, 41]
} as IntegerBoundSpec;

/**
 * The world envelope point of a profile-driven analytic frame at (u, v) and time t:
 * `A(t) * S_tool + b(t)`, where `S_tool` is the tool-frame point the frame's own basis builds.
 * The analytic route's entire output passes through here, and it touches no control net.
 */
function analyticEnvelopePoint(frame is map, motionSample is map, u is number, v is number) returns Vector
{
    const toolPoint = analyticWorldPointAndNormal(frame, u, v).point;
    return motionSample.rotation * toolPoint + motionSample.translation;
}

/** The tool-frame point, before the motion - what an inversion onto the extracted net needs. */
function analyticToolPoint(frame is map, u is number, v is number) returns Vector
{
    return analyticWorldPointAndNormal(frame, u, v).point;
}

/**
 * Squared distance from `point` to the polyline through `polyline`, segment-wise rather than
 * vertex-wise. Vertex-wise overstates by up to half the sample spacing, which on a 61-sample loop
 * of an ellipsoid is ~0.8 mm - four orders above what this test is trying to resolve.
 */
function squaredDistanceToPolyline(point is Vector, polyline is array, closed is boolean) returns number
{
    var best = 1e300;
    const count = size(polyline);
    const last = closed ? count : count - 1;
    for (var index = 0; index < last; index += 1)
    {
        const from = polyline[index];
        const to = polyline[(index + 1) % count];
        const along = to - from;
        const lengthSquared = squaredNorm(along);
        var closest = from;
        if (lengthSquared > 1e-30)
        {
            const fraction = min(1, max(0, dot(point - from, along) / lengthSquared));
            closest = from + fraction * along;
        }
        best = min(best, squaredNorm(point - closest));
    }
    return best;
}

annotation { "Feature Type Name" : "Sweep Ellipsoid A/B - Analytic vs Sampled Route" }
export const sweepEllipsoidRouteAbLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Analytic route (closed form, zero de Boor)", "Default" : true }
        definition.analyticRoute is boolean;

        annotation { "Name" : "Sampled route (march and resample)", "Default" : true }
        definition.sampledRoute is boolean;

        annotation { "Name" : "Stations" }
        isInteger(definition.stations, ELLIPSOID_AB_STATION_BOUNDS);

        annotation { "Name" : "Contact loop samples (sampled route)" }
        isInteger(definition.loopSamples, ASSEMBLY_LOOP_SAMPLE_BOUNDS);

        annotation { "Name" : "Face extraction tolerance (sampled route)" }
        isLength(definition.extractTolerance, ASSEMBLY_EXTRACT_BOUNDS);
    }
    {
        // Spec 12.3 tier 0 item 0g, first half. THE question this answers: was the sampling ever
        // necessary for this face? The section-9.4 fixture is a SurfaceType.REVOLVED ellipsoid whose
        // generator is one untrimmed iso-curve away, and section 11.6 measured that 87% of its build
        // is the spline evaluator serving a solve it does not need. So: same tool, same motion, same
        // stations, two routes to the contact curve, and a cross-check that owes neither route
        // anything.
        //
        // What this does NOT cover: the emitted solid. That needs tier 0 items 0a-0c wired, and the
        // solid-vs-solid half of 0g stays open until they are. The contact curve is the part that
        // settles whether the closed form is the right answer; emission is downstream of it.
        //
        // The ROUTE SWITCHES are the measurement instrument. Run each route alone under
        // profiler-tools/report.mjs and diff the per-function tables: `leanSurfaceDerivatives` is
        // the row that matters, and the analytic route should not appear in it at all.
        var tally = newCheckTally();
        const nextId = getUnstableIncrementingId(id);
        const stationCount = definition.stations == undefined ? 5 : definition.stations;
        const loopSamples = definition.loopSamples == undefined ? 61 : definition.loopSamples;
        const extractTolerance = definition.extractTolerance == undefined ?
            1e-7 : definition.extractTolerance / meter;
        const runAnalytic = definition.analyticRoute != false;
        const runSampled = definition.sampledRoute != false;

        // ---------- One tool, two representations of the same face ----------
        const tool = ellipsoidToolFixture(context, id + "tool", ELLIPSOID_SEMI_AXIAL,
                ELLIPSOID_SEMI_RADIAL, extractTolerance);
        println("[ELLIPSOID A/B] sampled representation: " ~ describeSurfaceShape(tool.surface) ~
            ", net " ~ size(tool.surface.controlPoints) ~ "x" ~ size(tool.surface.controlPoints[0]));

        const analyticRecords = extractToolFaceRecords(context, tool.body, extractTolerance, false, nextId);
        println("[ELLIPSOID A/B] analytic representation: " ~ summarizeFaceRecords(analyticRecords));
        tally = checkThat(tally, size(analyticRecords) == 1,
            "the ellipsoid extracted as " ~ size(analyticRecords) ~ " records, expected 1.");
        var frame = undefined;
        if (size(analyticRecords) == 1)
        {
            tally = checkThat(tally, analyticRecords[0].surfaceClass == SweepSurfaceClass.REVOLVED,
                "the ellipsoid classified as " ~ analyticRecords[0].surfaceClass ~
                ", not REVOLVED - the whole point of this test is that it IS a revolve.");
            frame = analyticFrameForFaceRecord(analyticRecords[0]);
            tally = checkThat(tally, frame != undefined,
                "no analytic frame was recovered for the ellipsoid.");
            tally = checkThat(tally, analyticRecords[0].spline == undefined,
                "the analytic record carries a spline, so it reached evApproximateBSplineSurface.");
        }
        if (frame == undefined)
        {
            reportCheckTally(context, id, "ELLIPSOID A/B", tally, "");
            return;
        }
        println("[ELLIPSOID A/B] generator: degree " ~ frame.profile.degree ~ " x " ~
            size(frame.profile.controlPoints) ~ ", rational " ~ (frame.profile.isRational == true) ~
            ", domain [" ~ frame.profileStart ~ ", " ~ frame.profileEnd ~ "], out of plane " ~
            frame.profileOutOfPlane ~ " m");

        // The section-9.4 motion, unchanged, so both routes are measured on a recorded fixture.
        const direction = normalize(vector(1, 0.35, 0));
        const travel = 0.18;
        const motion = constantVelocityTranslationMotion(travel * direction);

        // ---------- Both routes, station by station ----------
        var worstCrossCheck = 0;
        var worstCurveGap = 0;
        var worstPolylineSagitta = 0;
        var worstInversionResidual = 0;
        var analyticPointTotal = 0;
        var sampledPointTotal = 0;
        var stationsCompared = 0;
        var sampledFailures = 0;

        for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
        {
            const t = stationCount == 1 ? 0.5 : stationIndex / (stationCount - 1);
            const motionSample = evaluateMotionSample(motion, t);

            var analyticWorld = [];
            var analyticParams = [];
            if (runAnalytic)
            {
                // Closed form. No control net is touched anywhere in this block: the frame carries a
                // generator curve, and section 6.5.1's theta polynomial is solved per profile
                // parameter by the trig root solver.
                const pullback = analyticContactPullback(motionSample, frame);
                const contact = solveAnalyticContactCurve(frame, pullback, { "sampleCount" : loopSamples });
                for (var sample in contact.samples)
                {
                    analyticWorld = append(analyticWorld,
                        analyticEnvelopePoint(frame, motionSample, sample[0], sample[1]));
                    analyticParams = append(analyticParams, sample);
                }
                analyticPointTotal += size(contact.samples);
                if (stationIndex == 0)
                {
                    println("[ELLIPSOID A/B] analytic form at t = 0: " ~ contact.form ~ ", " ~
                        size(contact.samples) ~ " contact points, near tangency " ~ contact.nearTangency);
                }
            }

            var sampledWorld = [];
            if (runSampled)
            {
                // The route section 9.4 and every section 7 measurement took: seed a meridian, march
                // the wrapping loop with a predictor-corrector, resample by arc length. Every step
                // is an order-2 leanSurfaceDerivatives call on the extracted net.
                const loop = tubeLoopSamples(motion, tool.surface, t,
                        tool.domain.uMin + 0.5 * (tool.domain.uMax - tool.domain.uMin), loopSamples, {
                                "vMargin" : 0.08 * (tool.domain.vMax - tool.domain.vMin),
                                "sectionTolerance" : 1e-13, "meridianSamples" : 32
                            });
                if (loop.failed)
                {
                    sampledFailures += 1;
                    println("[ELLIPSOID A/B] sampled route FAILED at t = " ~ t ~ ": " ~ loop.reason);
                }
                else
                {
                    sampledWorld = loop.liftedRow;
                    sampledPointTotal += size(loop.liftedRow);
                }
            }

            if (!runAnalytic || !runSampled || size(analyticWorld) == 0 || size(sampledWorld) == 0)
            {
                continue;
            }
            stationsCompared += 1;

            // ---------- Cross-check 1: the analytic answer against the SAMPLED route's own equation ----------
            // For each analytic contact point, invert its TOOL-frame point onto the extracted net and
            // evaluate f there. If the closed form is right, the sampled representation agrees that
            // the point grazes - measured in the sampled route's own parameters, with its own
            // evaluator. This is the load-bearing check, and it deliberately spends evaluations:
            // it is the CHECK, not the route.
            // Eight per station is plenty for a worst case, and the parameters were solved once
            // above rather than re-solved per point.
            const crossCheckCount = min(8, size(analyticParams));
            for (var index = 0; index < crossCheckCount; index += 1)
            {
                const sample = analyticParams[floor(index * size(analyticParams) / crossCheckCount)];
                const toolPoint = analyticToolPoint(frame, sample[0], sample[1]);
                const inverted = invertPointOnSurfaceFromGrid(tool.surface, toolPoint, 6);
                worstInversionResidual = max(worstInversionResidual, inverted.residual);
                worstCrossCheck = max(worstCrossCheck, abs(evaluateEnvelopePointwise(motion,
                                tool.surface, inverted.uv[0], inverted.uv[1], t)));
            }

            // ---------- Cross-check 2: do the two routes trace the SAME curve in space ----------
            // Segment-wise, so the number is the curves' separation rather than the sampled loop's
            // vertex spacing.
            for (var point in analyticWorld)
            {
                worstCurveGap = max(worstCurveGap,
                    sqrt(squaredDistanceToPolyline(point, sampledWorld, true)));
            }

            // The comparison's OWN resolution, measured rather than assumed: a sampled loop stored
            // as a polyline sits inside its own curve by the chord sagitta, so no point-to-polyline
            // distance can resolve better than that. Each vertex's distance from the chord between
            // its neighbours IS that sagitta, so the floor is read off the data.
            const sampledCount = size(sampledWorld);
            for (var index = 0; index < sampledCount; index += 1)
            {
                const previous = sampledWorld[(index + sampledCount - 1) % sampledCount];
                const next = sampledWorld[(index + 1) % sampledCount];
                worstPolylineSagitta = max(worstPolylineSagitta,
                    norm(sampledWorld[index] - 0.5 * (previous + next)));
            }
        }

        println("[ELLIPSOID A/B] " ~ stationCount ~ " station(s): analytic produced " ~
            analyticPointTotal ~ " contact points, sampled produced " ~ sampledPointTotal ~
            " (" ~ sampledFailures ~ " station failure(s))");
        if (runAnalytic)
        {
            tally = checkThat(tally, analyticPointTotal > 0,
                "the analytic route produced no contact points at all.");
        }
        if (runSampled)
        {
            tally = checkThat(tally, sampledFailures == 0,
                sampledFailures ~ " station(s) failed on the sampled route.");
        }

        if (stationsCompared > 0)
        {
            println("[ELLIPSOID A/B] analytic points against the SAMPLED route's own equation: " ~
                "worst |f| " ~ worstCrossCheck ~ ", worst inversion residual " ~
                worstInversionResidual ~ " m");
            // Two floors bound this comparison, and both are measured rather than posited: the
            // extraction tolerance, since the two representations of the FACE are that far apart by
            // construction; and the sampled loop's polyline sagitta, since that is how far inside
            // its own curve the stored polyline sits. The gap can mean nothing below their max.
            const comparisonFloor = max(10 * extractTolerance, 2 * worstPolylineSagitta);
            println("[ELLIPSOID A/B] the two routes' contact curves are " ~ worstCurveGap ~
                " m apart at worst, over " ~ stationsCompared ~ " compared station(s) - against a " ~
                "comparison floor of " ~ comparisonFloor ~ " m (polyline sagitta " ~
                worstPolylineSagitta ~ " m, extraction " ~ extractTolerance ~ " m)");
            tally = checkWithin(tally, worstCurveGap, comparisonFloor,
                "the separation between the analytic and sampled contact curves (m)");
            tally = checkWithin(tally, worstInversionResidual, max(1e-6, 10 * extractTolerance),
                "the inversion residual of analytic points onto the extracted net (m)");
        }
        else if (runAnalytic && runSampled)
        {
            tally = checkThat(tally, false, "no station could be compared between the two routes.");
        }

        opDeleteBodies(context, id + "deleteTool", { "entities" : tool.body });

        reportCheckTally(context, id, "ELLIPSOID A/B",
            tally,
            "the closed-form route and the marched route trace the same contact curve on the same " ~
            "revolved face, and the analytic answer satisfies the sampled route's own envelope " ~
            "equation - so the sampling was never necessary for this face. Run each route alone " ~
            "under profiler-tools/report.mjs and diff the leanSurfaceDerivatives row for the cost.");
    }, { "analyticRoute" : true, "sampledRoute" : true, "stations" : 5, "loopSamples" : 61,
            "extractTolerance" : 1e-7 * meter });

// ============================= The motion matrix: emission beyond straight translation =============================

/** Turn bounds for the motion matrix. Modest by default: enough to break the surrogate, little
 *  enough that a convex tool's contact set stays ONE closed loop per station, which is what the
 *  tube fitter assumes. */
const MOTION_MATRIX_TURN_BOUNDS =
{
    (degree) : [0, 20, 90]
} as AngleBoundSpec;

/**
 * Hermite spans for a given turn, derived rather than picked. Cubic Hermite interpolation of a
 * span of width h carries error h^4 * (fourth derivative) / 384, and here the function is a column
 * entry of a rotation by turn * t, so its fourth derivative is turn^4 and the orthonormality drift
 * lands at (h*turn)^4 / 384 PER COLUMN ENTRY. Orthonormality is a product of two columns, so the
 * defect runs about twice that - which the first run measured: h*turn = 0.0233 predicted 7.6e-10
 * and the stored rotation came back at 1.53e-9, over spec 2.1's 1e-9 bar that this test asserts
 * against. Carrying the factor of two, 2 (h*turn)^4 / 384 <= 1e-9 needs h*turn <= 0.0209, so the
 * constant is 0.020 with a little margin.
 *
 * At 20 degrees that is 18 spans; at 90 degrees, 79. Span count costs nothing per evaluation - the
 * splines stay degree 3, and only the knot vector grows - so there is no reason to be stingy.
 */
function motionMatrixSpans(totalTurn is number) returns number
{
    return max(4, ceil(abs(totalTurn) / 0.020));
}

/**
 * The fit budget for a matrix row, sized against the thing that actually runs out: FeatureScript's
 * interpreter STEP budget, which is counted per FEATURE EVALUATION and which a tube fit spends on
 * interpreted de Boor calls, one per Newton correction per grid point.
 *
 * The first attempt at this test asked for 9 x 41 per row - 369 grid points, MORE than the 5 x 61 =
 * 305 of the spec 9.4 fixture that takes 23 s on its own - and three rows of that answered
 * "Too many steps: possible infinite loop" from the row loop. So: 7 stations pinned (max = initial,
 * which also removes the refinement pass's re-fits) and q from the loop-samples knob, 25 by
 * default. That is 175 points, a little over half of one spec 9.4 fit, so ONE row sits
 * comfortably inside the budget.
 *
 * Accuracy is not what is being bought here and it is worth being explicit about that. This test
 * discriminates an envelope from a silhouette extrude, an effect on the order of 10 mm; spec 9.4
 * remains the precision fixture. At q = 25 the fitted loop is still within ~1e-6 m of the marched
 * one (the error scales as the fourth power of the sample spacing, and 61 samples measured
 * 3.4e-8 m), which is orders under every gate this test applies and far under what the knit needs.
 *
 * The step budget being PER FEATURE is also why the runner inserts one instance per row rather
 * than asking one feature to sweep three times.
 */
const MOTION_MATRIX_FIT_BUDGET =
{
    "initialStationCount" : 7,
    "maxStationCount" : 7,
    "maxRefinementRounds" : 1
};

/** Loop samples for a matrix row. 25 by default - see MOTION_MATRIX_FIT_BUDGET for why not 61. */
const MOTION_MATRIX_LOOP_BOUNDS =
{
    (unitless) : [12, 25, 241]
} as IntegerBoundSpec;

/** Rotate `v` about a unit axis by `angle` plain radians (Rodrigues). */
function rotateAboutAxis(unitAxis is Vector, angle is number, v is Vector) returns Vector
{
    const cosine = cos(angle * radian);
    const sine = sin(angle * radian);
    return cosine * v + sine * cross(unitAxis, v) + (1 - cosine) * dot(unitAxis, v) * unitAxis;
}

/**
 * `A(t)` as three cubic Hermite splines of a rotation about `axis` by `totalTurn * t`, in the
 * column form the motion module stores (columnX is A's FIRST COLUMN - evaluateMotionSample
 * assembles the matrix with matrixFromColumns).
 *
 * Hermite rather than the tester's `firstOrderRotationMotion`, which is A's degree-1 Taylor
 * polynomial and drifts as O(t^2 |w|^2): fine for a station-class fixture, not for a motion that
 * has to stay rigid across a whole emission. Per span the Bezier control points are
 * `[f0, f0 + d0/3, f1 - d1/3, f1]` with `d = width * dA/dt`, and `d(A e_j)/dt = totalTurn * (k x A e_j)`
 * exactly - the same construction rotatingTubeMotion uses, generalized off the z axis.
 *
 * Returns { knots, columnX, columnY, columnZ, controlCount }.
 */
function turningRotationSplines(axis is Vector, totalTurn is number, spans is number) returns map
{
    const unitAxis = normalize(axis);
    const controlCount = 3 * spans + 1;
    var xControls = makeArray(controlCount, vector(0, 0, 0));
    var yControls = makeArray(controlCount, vector(0, 0, 0));
    var zControls = makeArray(controlCount, vector(0, 0, 0));
    for (var span = 0; span < spans; span += 1)
    {
        const tStart = span / spans;
        const tEnd = (span + 1) / spans;
        const width = tEnd - tStart;
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            const basis = vector(columnIndex == 0 ? 1 : 0, columnIndex == 1 ? 1 : 0,
                    columnIndex == 2 ? 1 : 0);
            const startColumn = rotateAboutAxis(unitAxis, totalTurn * tStart, basis);
            const endColumn = rotateAboutAxis(unitAxis, totalTurn * tEnd, basis);
            const startSlope = (width * totalTurn) * cross(unitAxis, startColumn);
            const endSlope = (width * totalTurn) * cross(unitAxis, endColumn);
            const segment = [startColumn, startColumn + startSlope / 3,
                    endColumn - endSlope / 3, endColumn];
            for (var pointIndex = 0; pointIndex < 4; pointIndex += 1)
            {
                if (columnIndex == 0)
                {
                    xControls[3 * span + pointIndex] = segment[pointIndex];
                }
                else if (columnIndex == 1)
                {
                    yControls[3 * span + pointIndex] = segment[pointIndex];
                }
                else
                {
                    zControls[3 * span + pointIndex] = segment[pointIndex];
                }
            }
        }
    }
    var knots = makeArray(controlCount + 4, 1);
    for (var index = 0; index < 4; index += 1)
    {
        knots[index] = 0;
    }
    for (var span = 1; span < spans; span += 1)
    {
        for (var repeat = 0; repeat < 3; repeat += 1)
        {
            knots[1 + 3 * span + repeat] = span / spans;
        }
    }
    return {
            "knots" : knots,
            "columnX" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : xControls },
            "columnY" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : yControls },
            "columnZ" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : zControls },
            "controlCount" : controlCount
        };
}

/** A straight path with a SPINNING frame: b(t) as in spec 9.4, A(t) turning about `axis`. */
function spinningStraightMotion(axis is Vector, totalTurn is number, displacement is Vector,
    spans is number) returns map
{
    const rotation = turningRotationSplines(axis, totalTurn, spans);
    return {
            "columnX" : rotation.columnX,
            "columnY" : rotation.columnY,
            "columnZ" : rotation.columnZ,
            "translation" : constantVelocityTranslationMotion(displacement).translation
        };
}

/**
 * A rigid REVOLUTION about a line through `axisPoint`: curvature and rotation at once, and the
 * cheapest honest way to get both without a kernel path.
 *
 * `p -> R(p - C) + C` means `A = R` and `b = C - R C`. b is LINEAR in A's entries, so it rides
 * A's own control points EXACTLY - `b_j = C - A_j C`, on the same knot vector - and introduces no
 * approximation of its own beyond the one already in A. The tool's origin traces a circular arc
 * of radius |C| while the frame turns through the same angle, which is exactly the motion a
 * silhouette extrude cannot represent.
 */
function revolutionMotion(axisPoint is Vector, axis is Vector, totalTurn is number,
    spans is number) returns map
{
    const rotation = turningRotationSplines(axis, totalTurn, spans);
    var translationControls = makeArray(rotation.controlCount, vector(0, 0, 0));
    for (var index = 0; index < rotation.controlCount; index += 1)
    {
        const mapped = rotation.columnX.controlPoints[index] * axisPoint[0] +
            rotation.columnY.controlPoints[index] * axisPoint[1] +
            rotation.columnZ.controlPoints[index] * axisPoint[2];
        translationControls[index] = axisPoint - mapped;
    }
    return {
            "columnX" : rotation.columnX,
            "columnY" : rotation.columnY,
            "columnZ" : rotation.columnZ,
            "translation" : {
                    "degree" : 3, "knots" : rotation.knots, "isRational" : false,
                    "controlPoints" : translationControls
                }
        };
}

/** The same motion with b shifted by a constant. b' is untouched, so the envelope is only moved -
 *  which is what keeps four swept solids from interpenetrating in one Part Studio. */
function offsetMotionTranslation(motion is map, offset is Vector) returns map
{
    var shifted = motion.translation;
    var controlPoints = makeArray(size(shifted.controlPoints), vector(0, 0, 0));
    for (var index = 0; index < size(controlPoints); index += 1)
    {
        controlPoints[index] = shifted.controlPoints[index] + offset;
    }
    shifted.controlPoints = controlPoints;
    return mergeMaps(motion, { "translation" : shifted });
}

/** The worst orthonormality drift of a stored A(t) over the sweep - spec 2.1's epsilon_motion. */
function motionRotationDrift(strippedMotion is map, sampleCount is number) returns number
{
    var worst = 0;
    for (var index = 0; index <= sampleCount; index += 1)
    {
        const sample = evaluateMotionSample(strippedMotion, index / sampleCount);
        const rotation = sample.rotation;
        worst = max(worst, orthonormalityDefect([
                        vector(rotation[0][0], rotation[1][0], rotation[2][0]),
                        vector(rotation[0][1], rotation[1][1], rotation[2][1]),
                        vector(rotation[0][2], rotation[1][2], rotation[2][2])
                    ]));
    }
    return worst;
}

/** Contact-loop points at stations the fit never used - the anchor that needs no closed form. */
function freshEnvelopePoints(strippedMotion is map, tool is map, times is array,
    perLoop is number) returns array
{
    var points = [];
    for (var freshT in times)
    {
        const loop = tubeLoopSamples(strippedMotion, tool.surface, freshT,
                tool.domain.uMin + 0.5 * (tool.domain.uMax - tool.domain.uMin), perLoop, {
                        "vMargin" : 0.08 * (tool.domain.vMax - tool.domain.vMin),
                        "sectionTolerance" : 1e-13, "meridianSamples" : 32
                    });
        if (loop.failed)
        {
            continue;
        }
        for (var point in loop.liftedRow)
        {
            points = append(points, meter * point);
        }
    }
    return points;
}

/**
 * The cheap trick, built for real so it can be measured: take the contact loop at t = 0 - the
 * self-shadow silhouette for the initial velocity - and extrude it along that velocity.
 *
 * It is handed every advantage on purpose: the EXACT t = 0 contact loop off the certified fit, the
 * exact initial direction, and 1.5x the path chord in length. Under a straight translation this IS
 * the envelope's lateral surface and the gap is the fit error. Under rotation or curvature the
 * contact set MOVES in the tool frame as t advances, and no single extrusion can follow it.
 *
 * Returns { built {boolean}, body {Query}, reason }.
 */
function silhouetteExtrudeSurrogate(context is Context, id is Id, fit is map,
    strippedMotion is map) returns map
{
    const wire = emitContactWire(context, id + "wire", fitBoundaryContactCurve(fit.surface, true));
    if (wire.refused)
    {
        return { "built" : false, "body" : undefined, "reason" : wire.reason };
    }
    const startSample = evaluateMotionSample(strippedMotion, 0);
    const endSample = evaluateMotionSample(strippedMotion, 1);
    const chord = norm(endSample.translation - startSample.translation);
    const velocity = startSample.translationDerivative;
    if (norm(velocity) < 1e-12 || chord < 1e-9)
    {
        return { "built" : false, "body" : undefined,
                "reason" : "the motion has no initial velocity to extrude along." };
    }
    var built = true;
    try silent
    {
        opExtrude(context, id + "surrogate", {
                    "entities" : qCreatedBy(wire.id, EntityType.EDGE),
                    "direction" : normalize(velocity),
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : 1.5 * chord * meter
                });
    }
    catch
    {
        built = false;
    }
    opDeleteBodies(context, id + "deleteWire", { "entities" : wire.wireBody });
    if (!built)
    {
        return { "built" : false, "body" : undefined, "reason" : "opExtrude refused the contact wire." };
    }
    return { "built" : true, "body" : qCreatedBy(id + "surrogate", EntityType.BODY), "reason" : "" };
}

/**
 * One row of the motion matrix: fit the lateral envelope, emit it, close it with spec 9, and
 * measure the result against whatever anchors that motion admits.
 *
 * caseSpec: { label, motion, loopSamples, fitOverrides {map}, exactVolume {number or undefined},
 * surrogateShouldMatch {boolean}, compareSurrogate {boolean} }.
 *
 * Returns the updated tally. Returns early rather than throwing wherever the row is already
 * decided - the caller catches, and a live test that dies takes its console output with it.
 */
function sweepMotionMatrixRow(context is Context, id is Id, tally is map, tool is map,
    caseSpec is map) returns map
{
    var running = tally;
    const label = caseSpec.label;
    const motion = caseSpec.motion;

    const drift = motionRotationDrift(motion, 24);
    println("[MOTION MATRIX] " ~ label ~ ": stored A(t) orthonormality drift " ~ drift);
    running = checkWithin(running, drift, 1e-9,
        label ~ "'s stored rotation drift (spec 2.1's epsilon_motion)");

    const fit = fitTubeComponent(motion, tool.surface, mergeMaps({
                    "tStart" : 0, "tEnd" : 1,
                    "uSeed" : tool.domain.uMin + 0.5 * (tool.domain.uMax - tool.domain.uMin),
                    "tolerance" : 1e-9,
                    "vMargin" : 0.08 * (tool.domain.vMax - tool.domain.vMin),
                    "sectionTolerance" : 1e-13, "meridianSamples" : 32,
                    "initialQCount" : caseSpec.loopSamples, "maxQCount" : caseSpec.loopSamples
                }, caseSpec.fitOverrides));
    running = checkThat(running, !fit.failed,
        label ~ "'s tube fit failed: " ~ (fit.failed ? fit.reason : ""));
    if (fit.failed)
    {
        return running;
    }
    println("[MOTION MATRIX] " ~ label ~ ": fit " ~ fit.stationCount ~ "x" ~ fit.qCount ~
        " in " ~ fit.refinementRounds ~ " round(s), deviation " ~ fit.worstDeviation ~
        " (q " ~ fit.worstQDeviation ~ ", t " ~ fit.worstTDeviation ~ ")");
    running = checkWithin(running, fit.worstDeviation, 1e-5, label ~ "'s tube fit deviation");

    opCreateBSplineSurface(context, id + "patch", {
                "bSplineSurface" : kernelFitSurface(attachFitSurfaceUnits(fit.surface), true)
            });
    const patchBody = qCreatedBy(id + "patch", EntityType.BODY);
    running = checkThat(running,
        size(evaluateQuery(context, qCreatedBy(id + "patch", EntityType.FACE))) == 1,
        label ~ "'s lateral patch did not emit as one face.");

    const assembly = assembleSweptSolid(context, id + "assembly", {
                "toolBody" : tool.body,
                "shellBodies" : patchBody,
                "caps" : [
                    {
                        "motionSample" : evaluateMotionSample(motion, 0),
                        "isStart" : true,
                        "contactCurves" : [fitBoundaryContactCurve(fit.surface, true)]
                    },
                    {
                        "motionSample" : evaluateMotionSample(motion, 1),
                        "isStart" : false,
                        "contactCurves" : [fitBoundaryContactCurve(fit.surface, false)]
                    }
                ]
            });
    println("[MOTION MATRIX] " ~ label ~ ": " ~ summarizeSweptSolid(assembly));
    running = checkThat(running, !assembly.failed,
        label ~ "'s assembly did not close: " ~ (assembly.failed ? assembly.reason : ""));
    if (assembly.failed)
    {
        return running;
    }

    const quality = assembly.quality;
    running = checkThat(running, assembly.knit.solidCount == 1,
        label ~ "'s knit produced " ~ assembly.knit.solidCount ~ " solid bodies.");
    running = checkThat(running, quality.minEdgeLength > 1e-5,
        label ~ "'s shortest edge is " ~ quality.minEdgeLength ~ " m, under the sliver floor.");
    running = checkThat(running, quality.minFaceArea > 1e-9,
        label ~ "'s smallest face is " ~ quality.minFaceArea ~ " m^2, a sliver.");
    println("[MOTION MATRIX] " ~ label ~ ": volume " ~ quality.volume ~ " m^3, " ~
        quality.faceCount ~ " faces, worst seam gap " ~ assembly.worstSeamGap ~ " m");

    if (caseSpec.exactVolume != undefined)
    {
        const volumeError = abs(quality.volume - caseSpec.exactVolume) / caseSpec.exactVolume;
        println("[MOTION MATRIX] " ~ label ~ ": volume against the exact " ~ caseSpec.exactVolume ~
            " m^3, relative error " ~ volumeError);
        running = checkWithin(running, volumeError, 1e-3, label ~ "'s swept volume relative error");
    }

    // The anchor that needs no closed form, and the only one every motion admits.
    const freshPoints = freshEnvelopePoints(motion, tool, [0.27, 0.63], 12);
    running = checkThat(running, size(freshPoints) > 0,
        label ~ " produced no fresh envelope points to measure against.");
    if (size(freshPoints) == 0)
    {
        return running;
    }
    const solidDeviation = evPointsDeviation(context, {
                    "points" : freshPoints,
                    "topologies" : assembly.solidBody
                })[0].deviation / meter;
    println("[MOTION MATRIX] " ~ label ~ ": solid deviation vs " ~ size(freshPoints) ~
        " fresh envelope points " ~ solidDeviation ~ " m");
    running = checkWithin(running, solidDeviation, 1e-4, label ~ "'s envelope deviation");

    if (!caseSpec.compareSurrogate)
    {
        return running;
    }
    const surrogate = silhouetteExtrudeSurrogate(context, id + "trick", fit, motion);
    running = checkThat(running, surrogate.built,
        label ~ "'s silhouette-extrude surrogate could not be built: " ~ surrogate.reason);
    if (!surrogate.built)
    {
        return running;
    }
    const surrogateGap = evPointsDeviation(context, {
                    "points" : freshPoints,
                    "topologies" : surrogate.body
                })[0].deviation / meter;
    println("[MOTION MATRIX] " ~ label ~ ": SILHOUETTE-EXTRUDE surrogate is " ~ surrogateGap ~
        " m from the same fresh envelope points (ours: " ~ solidDeviation ~ " m)");
    if (caseSpec.surrogateShouldMatch)
    {
        // Under a straight translation the trick IS the envelope, and saying so is the point:
        // this row is what makes the other rows' failure mean something.
        running = checkWithin(running, surrogateGap, 1e-4,
            label ~ ": the surrogate should reproduce this envelope, and its gap");
    }
    else
    {
        running = checkThat(running, surrogateGap > 1e-3,
            label ~ ": the silhouette-extrude surrogate came within " ~ surrogateGap ~
            " m of the envelope, so this motion does not distinguish a true envelope from the trick.");
    }
    return running;
}

annotation { "Feature Type Name" : "Sweep Solid Motion Matrix Live Test" }
export const sweepSolidMotionMatrixLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // "Default" belongs on the SPEC, not only in the defineFeature defaults map: a feature
        // inserted over the API carries no parameters at all, and an unspecified boolean arrives
        // as FALSE rather than as undefined, so a body that reads it sees every row switched off.
        annotation { "Name" : "Straight translation (the section 9.4 baseline)", "Default" : true }
        definition.straight is boolean;

        annotation { "Name" : "Straight path, spinning frame", "Default" : true }
        definition.spin is boolean;

        annotation { "Name" : "Circular arc path, frame turning with it", "Default" : true }
        definition.arc is boolean;

        annotation { "Name" : "Selected path edges, roll-free frame" }
        definition.selectedPath is boolean;

        if (definition.selectedPath)
        {
            annotation { "Name" : "Path edges", "Filter" : EntityType.EDGE }
            definition.pathEdges is Query;

            annotation { "Name" : "Frame source" }
            definition.frameSourceMode is MotionFrameSource;
        }

        annotation { "Name" : "Compare each row against a silhouette-extrude surrogate", "Default" : true }
        definition.compareSurrogate is boolean;

        annotation { "Name" : "Turn over the sweep" }
        isAngle(definition.turnAngle, MOTION_MATRIX_TURN_BOUNDS);

        annotation { "Name" : "Contact loop samples" }
        isInteger(definition.loopSamples, MOTION_MATRIX_LOOP_BOUNDS);

        annotation { "Name" : "Face extraction tolerance" }
        isLength(definition.extractTolerance, ASSEMBLY_EXTRACT_BOUNDS);
    }
    {
        // ONE ROW PER INSERT. FeatureScript's interpreter step budget is counted per feature
        // evaluation, and one row is a little over half of the spec 9.4 fit - so two rows are
        // borderline and three answer "Too many steps: possible infinite loop" from the row loop
        // below, which is what the first version of this test did. The switches are here so a row
        // can be CHOSEN, not so three can be run at once; profiler-tools/run-tests.mjs inserts one
        // instance of this feature per row for the same reason.
        //
        // Spec 9.4 emitted its first solid under a STRAIGHT translation, which was the right first
        // fixture - it is the one motion with a closed-form volume - but it is also the one motion
        // where the answer is indistinguishable from a much cheaper trick: for a pure translation
        // the grazing set is exactly the silhouette for b', and the lateral envelope is that curve
        // extruded along b'. So a straight-translation PASS is not evidence of a true envelope.
        //
        // This test runs the same emission chain over a MATRIX of motions, each row switchable, and
        // measures every row against the silhouette-extrude surrogate built for real. The straight
        // row asserts the surrogate MATCHES; every other row asserts it does NOT. That pair is the
        // evidence the straight row alone cannot give.
        var tally = newCheckTally();
        const loopSamples = definition.loopSamples == undefined ? 25 : definition.loopSamples;
        const extractTolerance = definition.extractTolerance == undefined ?
            1e-7 : definition.extractTolerance / meter;
        const turnAngle = (definition.turnAngle == undefined ? 20 * degree : definition.turnAngle) / radian;
        const compareSurrogate = definition.compareSurrogate != false;

        const semiAxial = ELLIPSOID_SEMI_AXIAL;
        const semiRadial = ELLIPSOID_SEMI_RADIAL;
        const tool = ellipsoidToolFixture(context, id + "tool", semiAxial, semiRadial, extractTolerance);
        println("[MOTION MATRIX] tool extracted at " ~ extractTolerance ~ " m: " ~
            describeSurfaceShape(tool.surface));

        // The spec 9.4 motion, unchanged, so the straight row reproduces a recorded run.
        const direction = normalize(vector(1, 0.35, 0));
        const travel = 0.18;
        const displacement = travel * direction;
        const toolVolume = 4 / 3 * PI * semiAxial * semiRadial ^ 2;
        const silhouetteArea = PI * semiAxial * semiRadial ^ 2 *
            sqrt((direction[0] / semiAxial) ^ 2 + (direction[1] / semiRadial) ^ 2 +
                (direction[2] / semiRadial) ^ 2);
        const minkowskiVolume = toolVolume + silhouetteArea * travel;

        // The revolution radius is chosen so its ARC LENGTH matches the straight row's travel, so
        // the two rows differ in the frame and the curvature rather than in how far the tool went.
        const revolutionRadius = travel / max(1e-6, turnAngle);
        const spinAxis = vector(0, 0, 1);

        var rows = [];
        if (definition.straight != false)
        {
            rows = append(rows, {
                        "label" : "STRAIGHT",
                        "motion" : constantVelocityTranslationMotion(displacement),
                        "exactVolume" : minkowskiVolume,
                        "surrogateShouldMatch" : true,
                        // Spec 9.4's own 5 x 61 fit, so this row REPRODUCES a recorded run rather
                        // than merely resembling it. Affordable because rows are separate feature
                        // instances and so do not share a step budget; and the surrogate comparison
                        // never needed the rows to be sampled alike, since each row is measured
                        // against its own surrogate.
                        "fitOverrides" : { "initialStationCount" : 5, "maxStationCount" : 5,
                                "maxRefinementRounds" : 1, "initialQCount" : 61, "maxQCount" : 61 },
                        "offset" : vector(0, 0, 0)
                    });
        }
        if (definition.spin != false)
        {
            rows = append(rows, {
                        "label" : "SPIN",
                        "motion" : spinningStraightMotion(spinAxis, turnAngle, displacement,
                            motionMatrixSpans(turnAngle)),
                        "exactVolume" : undefined,
                        "surrogateShouldMatch" : false,
                        "fitOverrides" : MOTION_MATRIX_FIT_BUDGET,
                        "offset" : vector(0, 0.2, 0)
                    });
        }
        if (definition.arc != false)
        {
            rows = append(rows, {
                        "label" : "ARC",
                        "motion" : revolutionMotion(vector(0, -revolutionRadius, 0), spinAxis,
                            turnAngle, motionMatrixSpans(turnAngle)),
                        "exactVolume" : undefined,
                        "surrogateShouldMatch" : false,
                        "fitOverrides" : MOTION_MATRIX_FIT_BUDGET,
                        "offset" : vector(0, 0.4, 0)
                    });
        }
        if (definition.selectedPath == true)
        {
            var pathMotion = undefined;
            try silent
            {
                pathMotion = buildMotionSpline(context, id + "pathMotion", {
                            "pathEdges" : definition.pathEdges,
                            "keepOrientation" : false,
                            "frameSource" : definition.frameSourceMode == undefined ?
                                MotionFrameSource.AUTOMATIC : definition.frameSourceMode
                        });
            }
            tally = checkThat(tally, pathMotion != undefined,
                "buildMotionSpline refused the selected path edges.");
            if (pathMotion != undefined)
            {
                println("[MOTION MATRIX] SELECTED PATH: " ~ size(pathMotion.stationParameters) ~
                    " stations, drift " ~ pathMotion.orthogonalityDrift ~ ", frame source " ~
                    pathMotion.frameSource ~ ", path length " ~ pathMotion.pathLength);
                rows = append(rows, {
                            "label" : "SELECTED PATH",
                            "motion" : pathMotion,
                            "exactVolume" : undefined,
                            "surrogateShouldMatch" : false,
                            "fitOverrides" : MOTION_MATRIX_FIT_BUDGET,
                            "offset" : vector(0, 0, 0)
                        });
            }
        }
        tally = checkThat(tally, size(rows) > 0, "no motion row was enabled.");
        if (size(rows) > 1)
        {
            println("[MOTION MATRIX] " ~ size(rows) ~ " rows enabled in ONE feature. The step " ~
                "budget is per feature evaluation and one row is roughly half a section 9.4 fit, " ~
                "so this may end in 'Too many steps'. Insert the feature once per row instead.");
        }

        for (var rowIndex = 0; rowIndex < size(rows); rowIndex += 1)
        {
            const row = rows[rowIndex];
            println("[MOTION MATRIX] === " ~ row.label ~ " ===");
            var completed = false;
            try silent
            {
                tally = sweepMotionMatrixRow(context, id + ("row" ~ rowIndex), tally, tool,
                    mergeMaps(row, {
                                "motion" : offsetMotionTranslation(row.motion, row.offset),
                                "loopSamples" : loopSamples,
                                "compareSurrogate" : compareSurrogate
                            }));
                completed = true;
            }
            tally = checkThat(tally, completed,
                row.label ~ " threw partway through; the last console line above names how far it got.");
        }

        opDeleteBodies(context, id + "deleteTool", { "entities" : tool.body });

        reportCheckTally(context, id, "MOTION MATRIX", tally,
            "the emission chain closes a solid under rotation and curvature as well as under a " ~
            "straight translation, every row carries fresh off-station envelope points to " ~
            "tolerance, and the silhouette-extrude surrogate reproduces ONLY the straight row - " ~
            "which is what makes the straight row's PASS mean an envelope rather than a trick.");
    }, { "straight" : true, "spin" : true, "arc" : true, "selectedPath" : false,
            "compareSurrogate" : true, "turnAngle" : 20 * degree, "loopSamples" : 25,
            "extractTolerance" : 1e-7 * meter,
            "frameSourceMode" : MotionFrameSource.AUTOMATIC });

annotation { "Feature Type Name" : "Sweep Envelope Fit Live Test" }
export const sweepEnvelopeFitLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // ---------- Rectangle patch: fit, emit, kernel-certify ----------
        const curvedSurface = curvedFixtureSurface();
        const curvedMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.15, 0.06], singleSpanKnots);
        // Modest grids on both patches: this feature pays for two fits plus kernel work, and
        // the point here is that the emitted geometry matches the envelope, which the
        // self tests already measured at finer grids.
        const curvedFit = fitEnvelopeComponent(curvedMotion, curvedSurface, {
                    "tStart" : 0, "tEnd" : 1,
                    "startAnchor" : curvedFixtureBranchAnchor(0),
                    "endAnchor" : curvedFixtureBranchAnchor(1),
                    "tolerance" : 1e-4,
                    "initialQCount" : 8, "initialStationCount" : 8,
                    "maxQCount" : 15, "maxStationCount" : 15, "maxRefinementRounds" : 1
                });
        if (curvedFit.failed || curvedFit.budgetHit)
        {
            failures = failures ~ " rectangle fit did not certify for emission.";
        }
        else
        {
            opCreateBSplineSurface(context, id + "rectPatch", {
                        "bSplineSurface" : kernelFitSurface(attachFitSurfaceUnits(curvedFit.surface))
                    });
            // Fresh envelope points at t stations the fit never used, kernel-projected.
            var rectPoints = [];
            for (var tFresh in [0.23, 0.61])
            {
                const row = sectionSamplesAtStation(curvedMotion, curvedSurface, tFresh,
                    curvedFixtureBranchAnchor(0), curvedFixtureBranchAnchor(1), 5, {});
                if (row.failed)
                {
                    failures = failures ~ " fresh rectangle section at t = " ~ tFresh ~ " failed.";
                    continue;
                }
                for (var point in row.liftedRow)
                {
                    rectPoints = append(rectPoints, point * meter);
                }
            }
            const rectDeviation = evPointsDeviation(context, {
                            "points" : rectPoints,
                            "topologies" : qCreatedBy(id + "rectPatch", EntityType.FACE)
                        })[0].deviation;
            println("[FIT LIVE TEST] rectangle patch kernel deviation vs fresh envelope points: " ~ rectDeviation);
            if (rectDeviation > 1e-4 * meter)
            {
                failures = failures ~ " rectangle patch kernel deviation " ~ (rectDeviation / meter) ~ ".";
            }
        }

        // ---------- Island patch: fit, emit pole-collapsed, kernel-certify ----------
        // Islands are NOT emitted here: a whole-island net is rejected by the kernel (see
        // kernelFitSurface). Their fit is covered by the island self test, and their emission
        // shape is the spec section 7.1 fallback, settled with the caps work.

        reportTestVerdict(context, id, "FIT LIVE TEST", failures,
            "the rectangle envelope patch emitted through opCreateBSplineSurface and " ~
            "kernel-certified against fresh envelope samples.");
    });

annotation { "Feature Type Name" : "Sweep Strip Decomposition Self Test" }
export const sweepStripDecompositionSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = mergeFixtureSurface();
        const motion = mergeFixtureMotion();
        const branches = [mergeFixtureCapBranch(0), mergeFixtureCapBranch(1), mergeFixtureWallBranch()];

        // The fixture's own branch samples have to be on f = 0 before anything reads them as a
        // boundary: an anchor is only as exact as the array it comes out of.
        var worstBranchResidual = 0;
        for (var branch in branches)
        {
            for (var index = 0; index < size(branch.tSamples); index += 1)
            {
                worstBranchResidual = max(worstBranchResidual, abs(evaluateEnvelopePointwise(motion, surface,
                                branch.uvSamples[index][0], branch.uvSamples[index][1], branch.tSamples[index])));
            }
        }
        println("[STRIP DECOMPOSITION SELF TEST] fixture branch samples worst |f|: " ~ worstBranchResidual);
        tally = checkWithin(tally, worstBranchResidual, 1e-15, "the fixture branch samples' worst |f|");

        const decomposition = decomposeFunnelComponentIntoStrips(motion, surface, {
                    "tStart" : 0, "tEnd" : 1, "branches" : branches, "sectionTolerance" : 1e-13
                });
        if (decomposition.failed)
        {
            tally = checkThat(tally, false, "the merge component's decomposition failed: " ~ decomposition.reason);
        }
        else
        {
            const alternations = decomposition.alternations;
            println("[STRIP DECOMPOSITION SELF TEST] alternations " ~ alternations.alternationCount ~
                " (" ~ alternations.endpointsAtStart ~ " endpoints at t0, " ~ alternations.endpointsAtEnd ~
                " at t1; cap arcs " ~ alternations.capArcsAtStart ~ "/" ~ alternations.capArcsAtEnd ~
                "), sides " ~ size(decomposition.sides) ~ ", split times " ~ toString(decomposition.splitTimes) ~
                ", bands " ~ size(decomposition.bands) ~ ", strips " ~ size(decomposition.strips) ~
                ", seams " ~ size(decomposition.seams));

            tally = checkThat(tally, alternations.alternationCount == 6,
                "the merge component's boundary alternates " ~ alternations.alternationCount ~
                " times, not the 6 its two-arc start and one-arc end make.");
            tally = checkThat(tally, alternations.endpointsAtStart == 4 && alternations.endpointsAtEnd == 2,
                "the cap endpoint counts are " ~ alternations.endpointsAtStart ~ "/" ~
                alternations.endpointsAtEnd ~ ", not 4/2.");
            tally = checkThat(tally, alternations.exceedsRectangle,
                "a 6-alternation component was not reported as exceeding a rectangle.");
            tally = checkThat(tally, size(decomposition.sides) == 4,
                "the three branches cut into " ~ size(decomposition.sides) ~ " sides, not 4.");
            tally = checkThat(tally, size(decomposition.splitTimes) == 1,
                "the decomposition found " ~ size(decomposition.splitTimes) ~ " interior cut times, not 1.");

            // ---------- The refined merge time ----------
            // The wall branch's t is exactly SPLIT_TIME (1 - s^2), so its maximum is the fixture
            // constant. The golden section runs on sigma, where t is quadratic, so the answer is
            // expected at machine precision even though sigma itself converges to only ~1e-9.
            if (size(decomposition.splitTimes) == 1)
            {
                println("[STRIP DECOMPOSITION SELF TEST] refined merge time: " ~ decomposition.splitTimes[0] ~
                    " (exact " ~ MERGE_FIXTURE_SPLIT_TIME ~ ")");
                tally = checkWithin(tally, decomposition.splitTimes[0] - MERGE_FIXTURE_SPLIT_TIME, 1e-12,
                    "the refined merge time's error against the fixture's closed form");
            }

            // ---------- The cut sample is ONE sample, shared ----------
            var wallSides = [];
            var capSides = [];
            for (var side in decomposition.sides)
            {
                if (side.branchIndex == 2)
                {
                    wallSides = append(wallSides, side);
                }
                else if (side.branchIndex == 0)
                {
                    capSides = append(capSides, side);
                }
            }
            tally = checkThat(tally, size(wallSides) == 2,
                "the turning wall branch cut into " ~ size(wallSides) ~ " sides, not 2.");
            tally = checkThat(tally, size(capSides) == 1,
                "the monotone v = 0 branch was cut into " ~ size(capSides) ~ " sides instead of being left alone.");
            if (size(capSides) == 1)
            {
                tally = checkThat(tally, uvSampleArraysIdentical(capSides[0].uvSamples, branches[0].uvSamples),
                    "a monotone branch's uv samples were not carried through the split untouched.");
            }
            if (size(wallSides) == 2)
            {
                const lower = wallSides[0];
                const upper = wallSides[1];
                const cutIndex = size(lower.tSamples) - 1;
                const cutTime = lower.tSamples[cutIndex];
                tally = checkThat(tally,
                    cutTime == upper.tSamples[0] &&
                    lower.uvSamples[cutIndex][0] == upper.uvSamples[0][0] &&
                    lower.uvSamples[cutIndex][1] == upper.uvSamples[0][1],
                    "the two sides of the cut do not carry the SAME cut sample - they only agree numerically.");
                // And the anchors read out of them at the cut time have to be that sample, bit for
                // bit, or the two strips' shared corner is two points.
                const anchorBelow = anchorUvAtStation(lower, motion, surface, cutTime, 1e-13);
                const anchorAbove = anchorUvAtStation(upper, motion, surface, cutTime, 1e-13);
                tally = checkThat(tally,
                    anchorBelow[0] == anchorAbove[0] && anchorBelow[1] == anchorAbove[1] &&
                    anchorBelow[0] == lower.uvSamples[cutIndex][0] &&
                    anchorBelow[1] == lower.uvSamples[cutIndex][1],
                    "the two sides' anchors at the cut time are not the shared cut sample itself " ~
                    "(" ~ toString(anchorBelow) ~ " against " ~ toString(anchorAbove) ~ ").");
                println("[STRIP DECOMPOSITION SELF TEST] shared cut sample: t " ~ cutTime ~ ", uv " ~
                    toString(anchorBelow) ~ ", sides " ~ size(lower.tSamples) ~ " + " ~ size(upper.tSamples) ~
                    " samples of the branch's 20");
            }

            // ---------- Bands, strips and the pairing ----------
            tally = checkThat(tally, size(decomposition.bands) == 2,
                "the merge component made " ~ size(decomposition.bands) ~ " bands, not 2.");
            tally = checkThat(tally, size(decomposition.strips) == 3,
                "the merge component made " ~ size(decomposition.strips) ~ " strips, not the two legs " ~
                "and one trunk its shape calls for.");
            if (size(decomposition.strips) == 3 && size(decomposition.splitTimes) == 1)
            {
                const splitTime = decomposition.splitTimes[0];
                var legCount = 0;
                var trunkCount = 0;
                var pairingCorrect = true;
                var rangesExact = true;
                for (var strip in decomposition.strips)
                {
                    const startBranch = strip.startAnchor.branchIndex;
                    const endBranch = strip.endAnchor.branchIndex;
                    if (strip.bandIndex == 0)
                    {
                        legCount += 1;
                        rangesExact = rangesExact && strip.tStart == 0 && strip.tEnd == splitTime;
                        // A leg runs from one cap wall to the turning wall branch, and its wall
                        // side must be the one on its own side of v = 1/2.
                        const wallIsEnd = endBranch == 2;
                        const exactlyOneWall = wallIsEnd ? startBranch != 2 : startBranch == 2;
                        const capBranch = wallIsEnd ? startBranch : endBranch;
                        const wallSide = wallIsEnd ? strip.endAnchor : strip.startAnchor;
                        const wallV = anchorUvAtStation(wallSide, motion, surface, 0.5 * splitTime, 1e-13)[1];
                        pairingCorrect = pairingCorrect && exactlyOneWall &&
                            ((capBranch == 0) == (wallV < 0.5));
                    }
                    else
                    {
                        trunkCount += 1;
                        rangesExact = rangesExact && strip.tStart == splitTime && strip.tEnd == 1;
                        pairingCorrect = pairingCorrect && startBranch + endBranch == 1;
                    }
                }
                tally = checkThat(tally, legCount == 2 && trunkCount == 1,
                    "the strips split " ~ legCount ~ "/" ~ trunkCount ~ " across the two bands, not 2/1.");
                tally = checkThat(tally, rangesExact,
                    "a strip's t range does not match its band's bounds exactly - a seam station " ~
                    "that does not compare equal cannot share arrays.");
                tally = checkThat(tally, pairingCorrect,
                    "the marched pairing did not join each cap wall to the wall-branch side on its " ~
                    "own side of v = 1/2.");
            }

            // ---------- The seam: one marched arc, resampled by all three strips ----------
            tally = checkThat(tally, size(decomposition.seams) == 1,
                "the decomposition reported " ~ size(decomposition.seams) ~ " seams, not 1.");
            if (size(decomposition.seams) == 1 && size(decomposition.strips) == 3)
            {
                const seam = decomposition.seams[0];
                const coarse = decomposition.strips[seam.coarseStripIndex];
                println("[STRIP DECOMPOSITION SELF TEST] seam at t " ~ seam.t ~ ": coarse strip " ~
                    seam.coarseStripIndex ~ " (band " ~ coarse.bandIndex ~ "), fine strips " ~
                    toString(seam.fineStripIndices) ~ ", shared polyline " ~ seam.polylinePointCount ~ " points");
                tally = checkThat(tally, coarse.bandIndex == 1,
                    "the seam's merged side is band " ~ coarse.bandIndex ~ " - it must be the " ~
                    "single-arc band, which is the one that is smooth across the merge.");
                tally = checkThat(tally, size(seam.fineStripIndices) == 2,
                    "the seam attached " ~ size(seam.fineStripIndices) ~ " fine strips to its shared arc, not 2.");
                tally = checkThat(tally, coarse.startSectionPolyline != undefined &&
                    coarse.endSectionPolyline == undefined &&
                    coarse.mergeAtStart == false && coarse.mergeAtEnd == false,
                    "the merged strip should carry the shared polyline at its START and no merge " ~
                    "mark (it is the smooth side of the merge).");

                if (size(seam.fineStripIndices) == 2 && coarse.startSectionPolyline != undefined)
                {
                    const shared = coarse.startSectionPolyline;
                    var interiorShared = 0;
                    var sharingExact = true;
                    var gradingCorrect = true;
                    for (var fineStripIndex in seam.fineStripIndices)
                    {
                        const fine = decomposition.strips[fineStripIndex];
                        gradingCorrect = gradingCorrect && fine.mergeAtEnd == true &&
                            fine.mergeAtStart == false && fine.gradeEnd == false;
                        sharingExact = sharingExact && fine.endSectionPolyline != undefined &&
                            uvSampleArraysIdentical(fine.endSectionPolyline, shared);
                        const clipped = clipSectionPolylineToAnchors(shared,
                            anchorUvAtStation(fine.startAnchor, motion, surface, seam.t, 1e-13),
                            anchorUvAtStation(fine.endAnchor, motion, surface, seam.t, 1e-13));
                        if (clipped.failed)
                        {
                            sharingExact = false;
                        }
                        else
                        {
                            const run = sharedPolylineRunLength(shared, clipped.uvPoints);
                            sharingExact = sharingExact && run >= 0;
                            interiorShared += max(0, run);
                        }
                    }
                    println("[STRIP DECOMPOSITION SELF TEST] shared interior vertices used by the two " ~
                        "legs: " ~ interiorShared ~ " of the arc's " ~ (size(shared) - 2));
                    tally = checkThat(tally, sharingExact,
                        "a leg strip's seam row is not the merged strip's own polyline, vertex for vertex.");
                    // The two clipped runs partition the shared arc's interior vertices. One short
                    // is also correct: when the junction anchor's closest point on the arc lands
                    // exactly ON a vertex, that vertex retires into the anchor and belongs to
                    // neither run.
                    tally = checkThat(tally, interiorShared >= size(shared) - 3 &&
                        interiorShared <= size(shared) - 2,
                        "the two legs' clipped seam rows cover " ~ interiorShared ~ " of the shared " ~
                        "arc's " ~ (size(shared) - 2) ~ " interior vertices - they must partition it.");
                    tally = checkThat(tally, gradingCorrect,
                        "the merging (fine) strips did not have their merge end MARKED, or picked " ~
                        "up station grading the decomposition does not turn on by default.");
                }
            }
        }

        // ---------- Control: a genuine rectangle must NOT decompose ----------
        // The curved fixture's two boundary branches are monotone in t by construction, so this
        // is the case the >4-alternation test has to stay silent on.
        const curvedDecomposition = decomposeFunnelComponentIntoStrips(
            translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0], [0.06, 0.15, 0.06]),
            curvedFixtureSurface(), {
                    "tStart" : 0, "tEnd" : 1, "sectionTolerance" : 1e-13,
                    "branches" : [curvedFixtureBranchAnchor(0), curvedFixtureBranchAnchor(1)]
                });
        if (curvedDecomposition.failed)
        {
            tally = checkThat(tally, false, "the rectangle control decomposition failed: " ~
                curvedDecomposition.reason);
        }
        else
        {
            println("[STRIP DECOMPOSITION SELF TEST] rectangle control: alternations " ~
                curvedDecomposition.alternations.alternationCount ~ ", sides " ~
                size(curvedDecomposition.sides) ~ ", strips " ~ size(curvedDecomposition.strips) ~
                ", seams " ~ size(curvedDecomposition.seams));
            tally = checkThat(tally, curvedDecomposition.alternations.alternationCount == 4 &&
                !curvedDecomposition.alternations.exceedsRectangle,
                "the rectangle control reported " ~ curvedDecomposition.alternations.alternationCount ~
                " alternations instead of 4.");
            tally = checkThat(tally, size(curvedDecomposition.strips) == 1 &&
                size(curvedDecomposition.seams) == 0 && size(curvedDecomposition.sides) == 2,
                "the rectangle control decomposed into " ~ size(curvedDecomposition.strips) ~
                " strips and " ~ size(curvedDecomposition.seams) ~ " seams instead of one strip and no seam.");
            if (size(curvedDecomposition.strips) == 1)
            {
                tally = checkThat(tally, curvedDecomposition.strips[0].mergeAtStart == false &&
                    curvedDecomposition.strips[0].mergeAtEnd == false &&
                    curvedDecomposition.strips[0].startSectionPolyline == undefined &&
                    curvedDecomposition.strips[0].endSectionPolyline == undefined,
                    "the rectangle control's single strip picked up a merge mark or a prescribed " ~
                    "section.");
            }

        }

        reportCheckTally(context, id, "STRIP DECOMPOSITION SELF TEST", tally,
            "a 6-alternation component splits at its refined merge time into two legs and a " ~
            "trunk, the cut sample and the seam arc are shared arrays rather than numbers that " ~
            "agree, and a genuine rectangle still comes out as one strip.");
    });

// Both strips are fitted at FIXED grids with the refinement loop switched off, and that is a
// finding rather than a convenience. A merging strip's certified deviation does not fall cleanly
// with grid size - its (q, t) chart has a square-root corner at the merge - and an independent
// recomputation of this fixture's own certification gives, for the leg on uniform stations,
// 8.5e-4 at 6x6, 5.5e-4 at 8x8, 3.0e-4 at 12x8, 2.1e-4 at 15x8, with the graded series
// non-monotone (spec 7.10). So a refine-to-tolerance loop pointed at a target the chart cannot
// reach doubles its way straight into the interpreter's step limit, which is exactly what the
// first two live attempts at this test did. Hence fixed grids, with the deviation REPORTED and
// scale-free assertions around it: whatever accuracy the fit reaches, the patch has to be the
// envelope to that accuracy and its seam edge has to be on the shared contact arc to that
// accuracy.
//
// The grid sizes come from the same recomputation. The leg's section is a monotone half of a
// parabola, so 8 q samples are plenty (q deviation 2.9e-5) and the strip is t-limited. The trunk's
// section crosses the parabola's apex, where arc length turns hardest, so it is q-limited and
// needs 16 (1.3e-4 at 6x16 against 5.4e-4 at 6x12); its t direction is free either way, both of
// its boundary curves being polynomial in t.
annotation { "Feature Type Name" : "Sweep Strip Fit Self Test" }
export const sweepStripFitSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = mergeFixtureSurface();
        const motion = mergeFixtureMotion();
        const decomposition = decomposeFunnelComponentIntoStrips(motion, surface, {
                    "tStart" : 0, "tEnd" : 1, "sectionTolerance" : 1e-13,
                    "branches" : [mergeFixtureCapBranch(0), mergeFixtureCapBranch(1), mergeFixtureWallBranch()]
                });
        if (decomposition.failed || size(decomposition.seams) != 1 ||
            size(decomposition.seams[0].fineStripIndices) != 2)
        {
            tally = checkThat(tally, false, "the decomposition this test fits did not come out as " ~
                "one seam with two merging strips (see the strip decomposition self test).");
        }
        else
        {
            const seam = decomposition.seams[0];
            // maxRefinementRounds 1 pins the grid; the tolerance still has to be the REAL target
            // for the strip, because the fit spends whatever slack is left under it on knot
            // removal - hand it a loose tolerance and it will happily displace the net by that
            // much and still report itself certified.
            const fixedGrid = { "sectionTolerance" : 1e-13, "maxRefinementRounds" : 1 };
            const legFit = fitEnvelopeComponent(motion, surface,
                mergeMaps(mergeMaps(fixedGrid, { "initialQCount" : 8, "initialStationCount" : 8,
                            "tolerance" : LEG_STRIP_DEVIATION_LIMIT }),
                    decomposition.strips[seam.fineStripIndices[0]]));
            const trunkFit = fitEnvelopeComponent(motion, surface,
                mergeMaps(mergeMaps(fixedGrid, { "initialQCount" : 16, "initialStationCount" : 6,
                            "tolerance" : TRUNK_STRIP_DEVIATION_LIMIT }),
                    decomposition.strips[seam.coarseStripIndex]));
            // The leg's t range ENDS at the seam and the trunk's starts there, so they read
            // opposite edges of their own patches. The limits are twice the recomputed deviation at
            // these grids (5.5e-4 and 1.3e-4): loose enough not to be a tolerance test, tight
            // enough that a chart or seam regression fails them.
            tally = reportStripFit(tally, "STRIP FIT SELF TEST", "leg", legFit, seam.t, true,
                LEG_STRIP_DEVIATION_LIMIT);
            tally = reportStripFit(tally, "STRIP FIT SELF TEST", "trunk", trunkFit, seam.t, false,
                TRUNK_STRIP_DEVIATION_LIMIT);
        }


        // ---------- The graded station layout, on its own ----------
        // Grading is opt-in because it does not improve a merging strip's certified deviation
        // (spec 7.10), but the layout itself has to be right for the caller who asks for it: ends
        // exact, spacing shrinking toward the graded end, and uniform in sqrt of the distance to
        // it - which for stations at (1 - (1 - k/(n-1))^2) means sqrt(1 - fraction) is linear.
        const gradedStations = buildFitStations(0.25, 0.75, 6, [], 1e-9, { "gradeEnd" : true });
        const uniformStations = buildFitStations(0.25, 0.75, 6, [], 1e-9, {});
        println("[STRIP FIT SELF TEST] graded stations " ~ toString(gradedStations) ~
            ", uniform " ~ toString(uniformStations));
        var gradedShrinks = true;
        var gradedSqrtLinear = 0;
        for (var index = 1; index + 1 < size(gradedStations); index += 1)
        {
            if (gradedStations[index + 1] - gradedStations[index] >=
                gradedStations[index] - gradedStations[index - 1])
            {
                gradedShrinks = false;
            }
            gradedSqrtLinear = max(gradedSqrtLinear,
                abs(sqrt(0.75 - gradedStations[index]) - sqrt(0.75 - gradedStations[index - 1]) -
                    (sqrt(0.75 - gradedStations[index + 1]) - sqrt(0.75 - gradedStations[index]))));
        }
        tally = checkThat(tally, size(gradedStations) == 6 && gradedStations[0] == 0.25 &&
            gradedStations[5] == 0.75 && uniformStations[0] == 0.25 && uniformStations[5] == 0.75,
            "a station set did not land its ends exactly on the strip's own t range - a seam " ~
            "station that does not compare equal cannot share arrays.");
        tally = checkThat(tally, gradedShrinks,
            "graded stations do not close up toward the graded end.");
        tally = checkWithin(tally, gradedSqrtLinear, 1e-14,
            "the graded stations' departure from uniform spacing in sqrt(distance to the merge)");

        reportCheckTally(context, id, "STRIP FIT SELF TEST", tally,
            "both strips of a 6-alternation component fit at their fixed grids, each patch is the " ~
            "envelope to its own measured deviation, and both seam edges lie on the one exact " ~
            "contact arc at the merge time - which is the seam statement, two curves within d of " ~
            "the same arc being within 2d of each other.");
    });

/**
 * BERNSTEIN POLYNOMIAL UTILS TESTER - fixed validation vectors for every exported function of
 * the Bernstein section of solidSweepUtils.fs. No geometry is created; results go to the console and the
 * feature info line. Spec: docs/specs/SOLID_SWEEP_SPEC.md section 6.0.
 *
 * Every check is either an exact coefficient identity (products, derivatives, sums of known
 * polynomials) or an evaluation-parity check (the operated-on polynomial evaluates equal to
 * the operation applied to evaluations) at fixed parameters - the same anchors the spline
 * refinement tester uses.
 */
annotation { "Feature Type Name" : "Bernstein Polynomial Utils Tester" }
export const bernsteinPolynomialUtilsTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Print passing checks too" }
        definition.printPassingChecks is boolean;
        // OFF by default: the caught throw this check provokes surfaces as an INFO evaluation
        // notice, and ANY notice makes the MCP harness return notices INSTEAD of the console.
        // Enable interactively in a real document; harness payloads
        // must stay notice-clean.
        annotation { "Name" : "Provoke the identically-zero throw guard", "Default" : false }
        definition.provokeZeroPolynomialThrow is boolean;
    }
    {
        var failures = [];
        var checkCount = 0;
        const tight = 1e-12;
        const loose = 1e-10;
        const parameters = [0, 0.15, 0.4, 0.5, 0.73, 0.9, 1];

        // ---------- PRODUCT ----------
        // x times (1 - x) has exact degree-2 coefficients [0, 1/2, 0].
        checkCount += 1;
        const xTimesOneMinusX = multiplyBernstein([0, 1], [1, 0]);
        if (!coefficientsNear(xTimesOneMinusX, [0, 0.5, 0], tight))
        {
            failures = append(failures, "PRODUCT exact: expected [0, 0.5, 0], got " ~ xTimesOneMinusX);
        }
        // Evaluation parity on an arbitrary degree-3 times degree-2 pair.
        const productA = [0.3, -1.2, 2.5, 0.7];
        const productB = [1.5, -0.4, 0.9];
        const product = multiplyBernstein(productA, productB);
        for (var t in parameters)
        {
            checkCount += 1;
            const direct = evaluateBernstein(productA, t) * evaluateBernstein(productB, t);
            const viaProduct = evaluateBernstein(product, t);
            if (abs(direct - viaProduct) > loose)
            {
                failures = append(failures, "PRODUCT parity at t=" ~ t ~ ": " ~ viaProduct ~ " vs " ~ direct);
            }
        }

        // ---------- SUM / ELEVATE ----------
        // x plus (1 - x) is the constant 1.
        checkCount += 1;
        const sumToOne = addBernstein([0, 1], [1, 0]);
        if (!coefficientsNear(sumToOne, [1, 1], tight))
        {
            failures = append(failures, "SUM exact: expected [1, 1], got " ~ sumToOne);
        }
        // Elevation preserves values.
        const elevatedLine = elevateBernstein([0, 1], 4);
        for (var t in parameters)
        {
            checkCount += 1;
            if (abs(evaluateBernstein(elevatedLine, t) - t) > tight)
            {
                failures = append(failures, "ELEVATE parity at t=" ~ t);
            }
        }

        // ---------- DERIVATIVE ----------
        // d/dt of t squared (degree-2 coefficients [0, 0, 1]) is 2t (degree-1 [0, 2]).
        checkCount += 1;
        if (!coefficientsNear(differentiateBernstein([0, 0, 1]), [0, 2], tight))
        {
            failures = append(failures, "DERIVATIVE: d/dt t^2 expected [0, 2], got " ~ differentiateBernstein([0, 0, 1]));
        }
        // d/dt of t cubed (degree-3 [0, 0, 0, 1]) is 3 t^2 (degree-2 [0, 0, 3]).
        checkCount += 1;
        if (!coefficientsNear(differentiateBernstein([0, 0, 0, 1]), [0, 0, 3], tight))
        {
            failures = append(failures, "DERIVATIVE: d/dt t^3 expected [0, 0, 3], got " ~ differentiateBernstein([0, 0, 0, 1]));
        }

        // ---------- SUBDIVIDE ----------
        const subdivisionSource = [1, -2, 3, 0.5];
        const splitAt = 0.3;
        const subdivided = subdivideBernstein(subdivisionSource, splitAt);
        for (var s in parameters)
        {
            checkCount += 2;
            const leftExpected = evaluateBernstein(subdivisionSource, splitAt * s);
            const leftActual = evaluateBernstein(subdivided.left, s);
            if (abs(leftExpected - leftActual) > loose)
            {
                failures = append(failures, "SUBDIVIDE left parity at s=" ~ s ~ ": " ~ leftActual ~ " vs " ~ leftExpected);
            }
            const rightExpected = evaluateBernstein(subdivisionSource, splitAt + (1 - splitAt) * s);
            const rightActual = evaluateBernstein(subdivided.right, s);
            if (abs(rightExpected - rightActual) > loose)
            {
                failures = append(failures, "SUBDIVIDE right parity at s=" ~ s ~ ": " ~ rightActual ~ " vs " ~ rightExpected);
            }
        }

        // ---------- RANGE / EXCLUDES ZERO ----------
        checkCount += 3;
        const range = bernsteinRange([1, 2, -3]);
        if (range.minimum != -3 || range.maximum != 2)
        {
            failures = append(failures, "RANGE: expected [-3, 2], got [" ~ range.minimum ~ ", " ~ range.maximum ~ "]");
        }
        if (!bernsteinExcludesZero([1, 2, 3], 1e-9))
        {
            failures = append(failures, "EXCLUDES-ZERO: all-positive coefficients should exclude zero");
        }
        if (bernsteinExcludesZero([-1, 2], 1e-9))
        {
            failures = append(failures, "EXCLUDES-ZERO: sign-changing coefficients must not exclude zero");
        }

        // ---------- ROOT ISOLATION ----------
        // (x - 1/4)(x - 3/4) in Bernstein form is [3/16, -5/16, 3/16]: two roots.
        const rootTolerance = 1e-4;
        const rootIntervals = isolateBernsteinRoots([3 / 16, -5 / 16, 3 / 16], 1e-12, rootTolerance);
        checkCount += 1;
        if (size(rootIntervals) != 2)
        {
            failures = append(failures, "ROOTS: expected 2 intervals, got " ~ size(rootIntervals) ~ ": " ~ rootIntervals);
        }
        else
        {
            checkCount += 4;
            if (!(rootIntervals[0].start <= 0.25 && 0.25 <= rootIntervals[0].end))
            {
                failures = append(failures, "ROOTS: first interval misses 0.25: " ~ rootIntervals[0]);
            }
            if (!(rootIntervals[1].start <= 0.75 && 0.75 <= rootIntervals[1].end))
            {
                failures = append(failures, "ROOTS: second interval misses 0.75: " ~ rootIntervals[1]);
            }
            if (rootIntervals[0].end - rootIntervals[0].start > 8 * rootTolerance)
            {
                failures = append(failures, "ROOTS: first interval too wide: " ~ rootIntervals[0]);
            }
            if (rootIntervals[1].end - rootIntervals[1].start > 8 * rootTolerance)
            {
                failures = append(failures, "ROOTS: second interval too wide: " ~ rootIntervals[1]);
            }
        }
        // No roots when the polynomial stays positive.
        checkCount += 1;
        if (size(isolateBernsteinRoots([1, 1, 2], 1e-12, rootTolerance)) != 0)
        {
            failures = append(failures, "ROOTS: positive polynomial should isolate no roots");
        }
        // The identically-zero polynomial must throw (the sliding case). Gated: see the
        // precondition note - the caught throw's INFO notice hides the harness console.
        if (definition.provokeZeroPolynomialThrow)
        {
            println("[BERNSTEIN TESTER] the throw printed next is EXPECTED (provoking the identically-zero guard):");
            checkCount += 1;
            var sawZeroPolynomialThrow = true;
            try
            {
                isolateBernsteinRoots([0, 0, 0], 1e-12, rootTolerance);
                sawZeroPolynomialThrow = false;
            }
            if (!sawZeroPolynomialThrow)
            {
                failures = append(failures, "ROOTS: identically-zero polynomial must throw, did not");
            }
        }

        // ---------- VECTOR (univariate) ----------
        // a(t) = (t, 1 - t, 2); b(t) = (1, t, t^2), components of differing degree.
        const vectorA = [[0, 1], [1, 0], [2, 2]];
        const vectorB = [[1, 1], [0, 0.5, 1], [0, 0, 1]];
        const dotCoefficients = dotBernsteinVectors(vectorA, vectorB);
        const crossCoefficients = crossBernsteinVectors(vectorA, vectorB);
        for (var t in parameters)
        {
            const aValue = evaluateBernsteinVector(vectorA, t);
            const bValue = evaluateBernsteinVector(vectorB, t);
            checkCount += 1;
            const dotDirect = aValue[0] * bValue[0] + aValue[1] * bValue[1] + aValue[2] * bValue[2];
            if (abs(evaluateBernstein(dotCoefficients, t) - dotDirect) > loose)
            {
                failures = append(failures, "VECTOR dot parity at t=" ~ t);
            }
            checkCount += 1;
            const crossDirect = [
                    aValue[1] * bValue[2] - aValue[2] * bValue[1],
                    aValue[2] * bValue[0] - aValue[0] * bValue[2],
                    aValue[0] * bValue[1] - aValue[1] * bValue[0]
                ];
            const crossViaCoefficients = evaluateBernsteinVector(crossCoefficients, t);
            if (abs(crossViaCoefficients[0] - crossDirect[0]) > loose ||
                abs(crossViaCoefficients[1] - crossDirect[1]) > loose ||
                abs(crossViaCoefficients[2] - crossDirect[2]) > loose)
            {
                failures = append(failures, "VECTOR cross parity at t=" ~ t);
            }
        }

        // ---------- BIVARIATE ----------
        // gridSum(u, v) = u + v (bilinear); gridProductUV(u, v) = u * v (bilinear).
        const gridSum = [[0, 1], [1, 2]];
        const gridProductUV = [[0, 0], [0, 1]];
        const gridProduct = multiplyBernsteinGrids(gridSum, gridProductUV);
        const gridPairs = [[0, 0], [0.3, 0.7], [0.5, 0.5], [1, 0.2], [0.8, 1]];
        for (var pair in gridPairs)
        {
            checkCount += 1;
            const expected = (pair[0] + pair[1]) * (pair[0] * pair[1]);
            const actual = evaluateBernsteinGrid(gridProduct, pair[0], pair[1]);
            if (abs(actual - expected) > loose)
            {
                failures = append(failures, "GRID product parity at (" ~ pair[0] ~ ", " ~ pair[1] ~ "): " ~
                    actual ~ " vs " ~ expected);
            }
        }
        // d/du of (u + v) is the constant 1.
        checkCount += 1;
        const gridDerivativeU = differentiateBernsteinGridU(gridSum);
        if (!coefficientsNear(gridDerivativeU[0], [1, 1], tight) || size(gridDerivativeU) != 1)
        {
            failures = append(failures, "GRID d/du of u+v expected [[1, 1]], got " ~ gridDerivativeU);
        }
        // Subdivision parity in u at 0.4 on the product grid.
        const gridSplit = subdivideBernsteinGridU(gridProduct, 0.4);
        for (var pair in gridPairs)
        {
            checkCount += 2;
            const lowExpected = evaluateBernsteinGrid(gridProduct, 0.4 * pair[0], pair[1]);
            const lowActual = evaluateBernsteinGrid(gridSplit.low, pair[0], pair[1]);
            if (abs(lowExpected - lowActual) > loose)
            {
                failures = append(failures, "GRID subdivide low parity at (" ~ pair[0] ~ ", " ~ pair[1] ~ ")");
            }
            const highExpected = evaluateBernsteinGrid(gridProduct, 0.4 + 0.6 * pair[0], pair[1]);
            const highActual = evaluateBernsteinGrid(gridSplit.high, pair[0], pair[1]);
            if (abs(highExpected - highActual) > loose)
            {
                failures = append(failures, "GRID subdivide high parity at (" ~ pair[0] ~ ", " ~ pair[1] ~ ")");
            }
        }
        // v-direction subdivision parity at 0.35 (the native right-multiplication path).
        const gridSplitV = subdivideBernsteinGridV(gridProduct, 0.35);
        for (var pair in gridPairs)
        {
            checkCount += 2;
            const lowExpected = evaluateBernsteinGrid(gridProduct, pair[0], 0.35 * pair[1]);
            const lowActual = evaluateBernsteinGrid(gridSplitV.low, pair[0], pair[1]);
            if (abs(lowExpected - lowActual) > loose)
            {
                failures = append(failures, "GRID subdivide-V low parity at (" ~ pair[0] ~ ", " ~ pair[1] ~ ")");
            }
            const highExpected = evaluateBernsteinGrid(gridProduct, pair[0], 0.35 + 0.65 * pair[1]);
            const highActual = evaluateBernsteinGrid(gridSplitV.high, pair[0], pair[1]);
            if (abs(highExpected - highActual) > loose)
            {
                failures = append(failures, "GRID subdivide-V high parity at (" ~ pair[0] ~ ", " ~ pair[1] ~ ")");
            }
        }
        // Grid range and zero exclusion.
        checkCount += 2;
        if (!bernsteinGridExcludesZero([[1, 2], [3, 0.5]], 1e-9))
        {
            failures = append(failures, "GRID excludes-zero: all-positive grid should exclude zero");
        }
        if (bernsteinGridExcludesZero(gridProduct, 1e-9))
        {
            failures = append(failures, "GRID excludes-zero: u*v*(u+v) touches zero, must not be excluded");
        }

        // ---------- VECTOR (bivariate) ----------
        // vGridA = (u + v, u * v, 1); vGridB = (u * v, u + v, 0). Dot = 2 (u+v)(uv).
        const vGridA = [gridSum, gridProductUV, constantBernsteinGrid(1, 1, 1)];
        const vGridB = [gridProductUV, gridSum, constantBernsteinGrid(0, 1, 1)];
        const vGridDot = dotBernsteinVectorGrids(vGridA, vGridB);
        for (var pair in gridPairs)
        {
            checkCount += 1;
            const expected = 2 * (pair[0] + pair[1]) * (pair[0] * pair[1]);
            const actual = evaluateBernsteinGrid(vGridDot, pair[0], pair[1]);
            if (abs(actual - expected) > loose)
            {
                failures = append(failures, "VECTOR-GRID dot parity at (" ~ pair[0] ~ ", " ~ pair[1] ~ ")");
            }
        }
        // Cross of the same pair, checked pointwise.
        const vGridCross = crossBernsteinVectorGrids(vGridA, vGridB);
        for (var pair in gridPairs)
        {
            checkCount += 1;
            const aValue = evaluateBernsteinVectorGrid(vGridA, pair[0], pair[1]);
            const bValue = evaluateBernsteinVectorGrid(vGridB, pair[0], pair[1]);
            const crossDirect = [
                    aValue[1] * bValue[2] - aValue[2] * bValue[1],
                    aValue[2] * bValue[0] - aValue[0] * bValue[2],
                    aValue[0] * bValue[1] - aValue[1] * bValue[0]
                ];
            const crossActual = evaluateBernsteinVectorGrid(vGridCross, pair[0], pair[1]);
            if (abs(crossActual[0] - crossDirect[0]) > loose ||
                abs(crossActual[1] - crossDirect[1]) > loose ||
                abs(crossActual[2] - crossDirect[2]) > loose)
            {
                failures = append(failures, "VECTOR-GRID cross parity at (" ~ pair[0] ~ ", " ~ pair[1] ~ ")");
            }
        }

        // ---------- REPORT ----------
        for (var failure in failures)
        {
            println("[BERNSTEIN TESTER] FAIL: " ~ failure);
        }
        if (definition.printPassingChecks && size(failures) == 0)
        {
            println("[BERNSTEIN TESTER] all groups green: product, sum/elevate, derivative, subdivide, " ~
                "range/exclusion, roots, vectors, grids, vector grids.");
        }
        const summary = size(failures) == 0 ?
            ("All " ~ checkCount ~ " checks passed.") :
            (size(failures) ~ " of " ~ checkCount ~ " checks FAILED - see console.");
        println("[BERNSTEIN TESTER] " ~ summary);
        reportFeatureInfo(context, id, summary);
    }, { printPassingChecks : true, provokeZeroPolynomialThrow : false });

/**
 * MOTION SPLINE TESTER - live validation of the motion section of solidSweepUtils.fs against a user-picked path.
 * Spec: docs/specs/SOLID_SWEEP_SPEC.md section 4. Creates no geometry (the module's helper
 * scaffold lives and dies inside its own scratch scope); draws debug frames on request.
 *
 * Checks:
 *   - the motion starts at identity (A(0) = I, b(0) = 0);
 *   - the rotation is orthonormal AT every station (interpolation nodes are exactly rigid);
 *   - the certified orthogonality drift passes its tolerance, re-verified independently at
 *     off-node parameters;
 *   - reported derivatives match central finite differences of the motion itself;
 *   - trajectoryCurveOf is EXACT: its spline (evaluated by std evaluateSpline, faithful on
 *     non-rational curves) matches applyMotion pointwise, and its derivative matches
 *     motionVelocityAt - the control-point-transport exactness claim of spec section 2.1;
 *   - motionSnapshotTransform maps the start frame origin onto each station frame origin;
 *   - keep-orientation mode keeps the rotation exactly identity;
 *   - one event is recorded per interior edge junction.
 */
annotation { "Feature Type Name" : "SW Motion Spline Tester" }
export const swMotionSplineTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Path edges", "Filter" : EntityType.EDGE }
        definition.pathEdges is Query;
        annotation { "Name" : "Keep orientation (pure translation)" }
        definition.keepOrientation is boolean;
        annotation { "Name" : "Frame source" }
        definition.frameSourceMode is MotionFrameSource;
        annotation { "Name" : "Draw station frames" }
        definition.drawStationFrames is boolean;
    }
    {
        var failures = [];
        var checkCount = 0;

        const motion = buildMotionSpline(context, id + "motion", {
                    "pathEdges" : definition.pathEdges,
                    "keepOrientation" : definition.keepOrientation,
                    "frameSource" : definition.frameSourceMode
                });
        println("[MOTION TESTER] built: " ~ size(motion.stationParameters) ~ " stations, drift " ~
            motion.orthogonalityDrift ~ ", frame source " ~ motion.frameSource ~
            ", " ~ size(motion.events) ~ " event(s), path length " ~ motion.pathLength);
        println("[MOTION TESTER] diagnostics: mode " ~ motion.buildDiagnostics.requestedMode ~
            ", " ~ motion.buildDiagnostics.ladderRungs ~ " ladder rung(s), " ~
            motion.buildDiagnostics.finalStationCount ~ " final stations, " ~
            motion.buildDiagnostics.perStationEvaluationCalls ~ " per-station ev-call(s), " ~
            motion.buildDiagnostics.batchedTangentCalls ~ " batched tangent call(s). " ~
            "Read the feature compute time alongside this line for the A/B comparison.");

        // ---------- IDENTITY AT START ----------
        const startSample = motionAt(motion, 0);
        checkCount += 2;
        if (norm(startSample.columns[0] - vector(1, 0, 0)) > 1e-9 ||
            norm(startSample.columns[1] - vector(0, 1, 0)) > 1e-9 ||
            norm(startSample.columns[2] - vector(0, 0, 1)) > 1e-9)
        {
            failures = append(failures, "START: A(0) is not identity: " ~ startSample.columns);
        }
        if (norm(startSample.translation) > 1e-9)
        {
            failures = append(failures, "START: b(0) is not zero: " ~ startSample.translation);
        }

        // ---------- RIGIDITY AT STATIONS ----------
        for (var stationParameter in motion.stationParameters)
        {
            checkCount += 1;
            const sample = motionAt(motion, stationParameter);
            if (orthonormalityDefect(sample.columns) > 1e-9)
            {
                failures = append(failures, "STATION rigidity at t=" ~ stationParameter ~ ": defect " ~
                    orthonormalityDefect(sample.columns));
            }
        }

        // ---------- DRIFT: CERTIFIED AND INDEPENDENTLY RE-CHECKED ----------
        checkCount += 1;
        if (motion.orthogonalityDrift > motion.driftTolerance)
        {
            failures = append(failures, "DRIFT: certified " ~ motion.orthogonalityDrift ~ " exceeds tolerance " ~
                motion.driftTolerance);
        }
        const offNodeParameters = [0.037, 0.111, 0.234, 0.389, 0.456, 0.541, 0.678, 0.723, 0.812, 0.897, 0.961];
        for (var t in offNodeParameters)
        {
            checkCount += 1;
            const defect = orthonormalityDefect(motionAt(motion, t).columns);
            if (defect > 10 * motion.driftTolerance)
            {
                failures = append(failures, "DRIFT re-check at t=" ~ t ~ ": defect " ~ defect);
            }
        }

        // ---------- DERIVATIVE PARITY (central finite differences) ----------
        const finiteDifferenceStep = 1e-5;
        for (var t in [0.15, 0.33, 0.52, 0.71, 0.88])
        {
            const sample = motionAt(motion, t);
            const ahead = motionAt(motion, t + finiteDifferenceStep);
            const behind = motionAt(motion, t - finiteDifferenceStep);
            for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
            {
                checkCount += 2;
                const velocityByDifference = (ahead.columns[columnIndex] - behind.columns[columnIndex]) / (2 * finiteDifferenceStep);
                if (norm(velocityByDifference - sample.columnVelocities[columnIndex]) >
                    1e-5 * (1 + norm(sample.columnVelocities[columnIndex])))
                {
                    failures = append(failures, "DERIVATIVE: column " ~ columnIndex ~ " velocity at t=" ~ t);
                }
                const accelerationByDifference = (ahead.columnVelocities[columnIndex] - behind.columnVelocities[columnIndex]) / (2 * finiteDifferenceStep);
                if (norm(accelerationByDifference - sample.columnAccelerations[columnIndex]) >
                    1e-4 * (1 + norm(sample.columnAccelerations[columnIndex])))
                {
                    failures = append(failures, "DERIVATIVE: column " ~ columnIndex ~ " acceleration at t=" ~ t);
                }
            }
            checkCount += 2;
            const translationVelocityByDifference = (ahead.translation - behind.translation) / (2 * finiteDifferenceStep);
            if (norm(translationVelocityByDifference - sample.translationVelocity) >
                1e-5 * (1 + norm(sample.translationVelocity)))
            {
                failures = append(failures, "DERIVATIVE: translation velocity at t=" ~ t);
            }
            const translationAccelerationByDifference = (ahead.translationVelocity - behind.translationVelocity) / (2 * finiteDifferenceStep);
            if (norm(translationAccelerationByDifference - sample.translationAcceleration) >
                1e-4 * (1 + norm(sample.translationAcceleration)))
            {
                failures = append(failures, "DERIVATIVE: translation acceleration at t=" ~ t);
            }
        }

        // ---------- TRAJECTORY EXACTNESS ----------
        // The transported control net must reproduce applyMotion EXACTLY (spec section 2.1).
        // std evaluateSpline is faithful here because the trajectory is non-rational.
        const toolPoint = vector(0.013, -0.007, 0.021);
        const trajectory = trajectoryCurveOf(motion, toolPoint);
        const trajectoryWithUnits = bSplineCurve({
                    "degree" : trajectory.degree,
                    "isPeriodic" : trajectory.isPeriodic,
                    "controlPoints" : attachMeters(trajectory.controlPoints),
                    "knots" : trajectory.knots
                });
        const trajectoryParameters = [0, 0.18, 0.35, 0.5, 0.64, 0.83, 1];
        const trajectoryEvaluations = evaluateSpline({
                    "spline" : trajectoryWithUnits,
                    "parameters" : trajectoryParameters,
                    "nDerivatives" : 1
                });
        for (var parameterIndex = 0; parameterIndex < size(trajectoryParameters); parameterIndex += 1)
        {
            const t = trajectoryParameters[parameterIndex];
            const sample = motionAt(motion, t);
            checkCount += 2;
            const positionDelta = norm(trajectoryEvaluations[0][parameterIndex] / meter - applyMotion(sample, toolPoint));
            if (positionDelta > 1e-9)
            {
                failures = append(failures, "TRAJECTORY position at t=" ~ t ~ ": delta " ~ positionDelta);
            }
            const velocityDelta = norm(trajectoryEvaluations[1][parameterIndex] / meter - motionVelocityAt(sample, toolPoint));
            if (velocityDelta > 1e-7)
            {
                failures = append(failures, "TRAJECTORY velocity at t=" ~ t ~ ": delta " ~ velocityDelta);
            }
        }

        // ---------- SNAPSHOT TRANSFORMS ----------
        for (var stationIndex in [0, size(motion.stationFrames) - 1])
        {
            checkCount += 1;
            const snapshot = motionSnapshotTransform(motion, stationIndex);
            const mappedStart = snapshot * motion.stationFrames[0].origin;
            if (norm(mappedStart - motion.stationFrames[stationIndex].origin) > 1e-9 * meter)
            {
                failures = append(failures, "SNAPSHOT: station " ~ stationIndex ~ " maps start origin " ~
                    norm(mappedStart - motion.stationFrames[stationIndex].origin) ~ " away");
            }
        }

        // ---------- MODE AND EVENTS ----------
        if (definition.keepOrientation)
        {
            for (var t in [0.1, 0.45, 0.98])
            {
                checkCount += 1;
                const sample = motionAt(motion, t);
                if (norm(sample.columns[0] - vector(1, 0, 0)) > 1e-10 ||
                    norm(sample.columns[1] - vector(0, 1, 0)) > 1e-10 ||
                    norm(sample.columns[2] - vector(0, 0, 1)) > 1e-10)
                {
                    failures = append(failures, "KEEP-ORIENTATION: rotation not identity at t=" ~ t);
                }
            }
        }
        checkCount += 1;
        if (size(motion.events) != size(motion.path.edges) - 1)
        {
            failures = append(failures, "EVENTS: expected " ~ (size(motion.path.edges) - 1) ~
                " junction event(s), got " ~ size(motion.events));
        }

        // ---------- DEBUG DRAWING ----------
        if (definition.drawStationFrames)
        {
            const axisLength = motion.pathLength * 0.03;
            for (var frame in motion.stationFrames)
            {
                addDebugLine(context, frame.origin, frame.origin + axisLength * frame.xAxis, DebugColor.BLUE);
                addDebugLine(context, frame.origin, frame.origin + axisLength * frame.zAxis, DebugColor.RED);
            }
        }

        // ---------- REPORT ----------
        for (var failure in failures)
        {
            println("[MOTION TESTER] FAIL: " ~ failure);
        }
        const summary = size(failures) == 0 ?
            ("All " ~ checkCount ~ " checks passed (" ~ size(motion.stationParameters) ~ " stations, drift " ~
                    motion.orthogonalityDrift ~ ").") :
            (size(failures) ~ " of " ~ checkCount ~ " checks FAILED - see console.");
        println("[MOTION TESTER] " ~ summary);
        reportFeatureInfo(context, id, summary);
    });

/**
 * Self test for the analytic-contact section of solidSweepUtils.fs (spec section 6.0 strategy 1). Selection-free: it builds
 * its own typed analytic surfaces and its own motion stations, so it needs no geometry and no
 * Part Studio state.
 *
 * The load-bearing check is CROSS-PATH: every closed form is compared against
 * evaluateAnalyticContactDirect, which builds the world-space point and normal and evaluates the
 * section 1.1 definition against the motion sample without touching the pullback. Agreement to
 * machine precision on all five classes is what certifies the pullback algebra.
 *
 * The motion station used for that check has BOTH a rotation derivative and a translation
 * derivative and a NON-orthonormal A on purpose: the pullback identity f = <N, W S + c> holds
 * for any A, so exercising it away from SO(3) separates an algebra error from an orthonormality
 * assumption. The rigid-motion consequence is then checked separately and exactly, where A is
 * the identity and A' is skew.
 */

annotation { "Feature Type Name" : "Sweep Analytic Contact Self Test" }
export const sweepAnalyticContactSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        var checks = 0;

        // ---------- Cross-path consistency on all five classes ----------
        const generalSample = constantMotionSample(
                [[0.93, -0.21, 0.11], [0.24, 0.88, -0.17], [-0.08, 0.19, 0.97]],
                [[0.13, -0.44, 0.28], [0.51, 0.07, -0.33], [-0.22, 0.39, 0.05]],
                vector(0.31, -0.17, 0.44));
        const axis = normalize(vector(0.3, -0.5, 0.81));
        const inPlane = normalize(cross(axis, vector(0.11, 0.97, -0.2)));
        const testOrigin = vector(0.07, -0.13, 0.21) * meter;
        const testCoordSystem = coordSystem(testOrigin, inPlane, axis);

        const surfaces = [
                ["PLANE", plane(testOrigin, axis, inPlane), 2.7, 1.2],
                ["CYLINDER", cylinder(testCoordSystem, 0.037 * meter), 3.14159, 0.1],
                ["CONE", cone(testCoordSystem, 0.42 * radian), 3.14159, 0.2],
                ["SPHERE", sphere(testCoordSystem, 0.051 * meter), 3.14159, 1.4],
                ["TORUS", torus(testCoordSystem, 0.018 * meter, 0.06 * meter), 3.14159, 3.14159]
            ];
        var worstConsistency = 0;
        for (var entry in surfaces)
        {
            const frame = stripAnalyticSurface(entry[1]);
            const pullback = analyticContactPullback(generalSample, frame);
            var worstForClass = 0;
            for (var i = 0; i <= 8; i += 1)
            {
                for (var j = 0; j <= 8; j += 1)
                {
                    const u = -entry[2] + 2 * entry[2] * i / 8;
                    const v = -entry[3] + 2 * entry[3] * j / 8;
                    worstForClass = max(worstForClass,
                        abs(evaluateAnalyticContact(frame, pullback, u, v) -
                            evaluateAnalyticContactDirect(frame, generalSample, u, v)));
                }
            }
            println("[ANALYTIC SELF TEST] " ~ entry[0] ~ " closed form vs the section 1.1 definition: " ~
                worstForClass);
            worstConsistency = max(worstConsistency, worstForClass);
            checks += 1;
        }
        if (worstConsistency > 1e-14)
        {
            failures = failures ~ " closed form disagrees with the direct definition by " ~
                worstConsistency ~ ".";
        }

        // ---------- The trig structures reproduce f ----------
        // Cylinder and cone are LINEAR in their ruling parameter; sphere and torus reduce to one
        // degree-2 trigonometric polynomial per meridian. Both claims are checked against the
        // closed form, so a wrong harmonic collection cannot hide.
        const cylinderFrame = stripAnalyticSurface(cylinder(testCoordSystem, 0.037 * meter));
        const cylinderPullback = analyticContactPullback(generalSample, cylinderFrame);
        const cylinderStructure = analyticContactStructure(cylinderFrame, cylinderPullback, {});
        var worstCylinderStructure = 0;
        for (var i = 0; i <= 24; i += 1)
        {
            for (var j = 0; j <= 6; j += 1)
            {
                const theta = 2 * PI * i / 24;
                const z = -0.1 + 0.2 * j / 6;
                const structured = trigPolynomialValue(cylinderStructure.constantTerm, theta) +
                    z * trigPolynomialValue(cylinderStructure.rulingTerm, theta);
                worstCylinderStructure = max(worstCylinderStructure,
                    abs(structured - evaluateAnalyticContact(cylinderFrame, cylinderPullback, theta, z)));
            }
        }
        println("[ANALYTIC SELF TEST] cylinder A(theta) + z B(theta) vs closed form: " ~ worstCylinderStructure);
        checks += 1;
        if (worstCylinderStructure > 1e-15)
        {
            failures = failures ~ " cylinder ruled structure off by " ~ worstCylinderStructure ~ ".";
        }

        const coneFrame = stripAnalyticSurface(cone(testCoordSystem, 0.42 * radian));
        const conePullback = analyticContactPullback(generalSample, coneFrame);
        const coneStructure = analyticContactStructure(coneFrame, conePullback, {});
        var worstConeStructure = 0;
        for (var i = 0; i <= 24; i += 1)
        {
            for (var j = 0; j <= 6; j += 1)
            {
                const theta = 2 * PI * i / 24;
                const ruling = 0.02 + 0.18 * j / 6;
                const structured = trigPolynomialValue(coneStructure.constantTerm, theta) +
                    ruling * trigPolynomialValue(coneStructure.rulingTerm, theta);
                worstConeStructure = max(worstConeStructure,
                    abs(structured - evaluateAnalyticContact(coneFrame, conePullback, theta, ruling)));
            }
        }
        println("[ANALYTIC SELF TEST] cone A(theta) + l B(theta) vs closed form: " ~ worstConeStructure);
        checks += 1;
        if (worstConeStructure > 1e-15)
        {
            failures = failures ~ " cone ruled structure off by " ~ worstConeStructure ~ ".";
        }

        var worstMeridian = 0;
        for (var entry in [["SPHERE", sphere(testCoordSystem, 0.051 * meter)],
                    ["TORUS", torus(testCoordSystem, 0.018 * meter, 0.06 * meter)]])
        {
            const frame = stripAnalyticSurface(entry[1]);
            const pullback = analyticContactPullback(generalSample, frame);
            for (var i = 0; i <= 12; i += 1)
            {
                const theta = 2 * PI * i / 12;
                const meridian = analyticMeridianPolynomial(frame, pullback, theta);
                for (var j = 0; j <= 12; j += 1)
                {
                    const phi = -PI + 2 * PI * j / 12;
                    worstMeridian = max(worstMeridian, abs(trigPolynomialValue(meridian, phi) -
                                evaluateAnalyticContact(frame, pullback, theta, phi)));
                }
            }
        }
        println("[ANALYTIC SELF TEST] sphere and torus meridian polynomials vs closed form: " ~ worstMeridian);
        checks += 1;
        if (worstMeridian > 1e-15)
        {
            failures = failures ~ " meridian polynomial off by " ~ worstMeridian ~ ".";
        }

        // ---------- Sliding, exactly (spec 6.4) ----------
        // A cylinder translating along its own axis and a plane translating parallel to itself
        // are the two cases section 6.4 calls the common ones on real parts. Both must come out
        // EXACTLY zero from the coefficients, with no sampling anywhere.
        const axialSample = constantMotionSample(identityRotationRows(), zeroRows(), 0.25 * axis);
        const axialStructure = analyticContactStructure(cylinderFrame,
            analyticContactPullback(axialSample, cylinderFrame), {});
        println("[ANALYTIC SELF TEST] cylinder translating along its own axis slides: " ~
            axialStructure.slidesEverywhere);
        checks += 1;
        if (!axialStructure.slidesEverywhere)
        {
            failures = failures ~ " a cylinder translating along its own axis was not detected as sliding.";
        }

        const planeFrame = stripAnalyticSurface(plane(testOrigin, axis, inPlane));
        const parallelStructure = analyticContactStructure(planeFrame,
            analyticContactPullback(constantMotionSample(identityRotationRows(), zeroRows(), 0.25 * inPlane),
                planeFrame), {});
        println("[ANALYTIC SELF TEST] plane translating in its own plane slides: " ~
            parallelStructure.slidesEverywhere);
        checks += 1;
        if (!parallelStructure.slidesEverywhere)
        {
            failures = failures ~ " a plane translating parallel to itself was not detected as sliding.";
        }

        // A plane translating along its normal must NOT slide, and its constant is exactly the
        // normal speed - a closed-form value with no discretization in it at all.
        const normalSpeed = 0.31;
        const normalStructure = analyticContactStructure(planeFrame,
            analyticContactPullback(constantMotionSample(identityRotationRows(), zeroRows(), normalSpeed * axis),
                planeFrame), {});
        const constantError = abs(normalStructure.constant - normalSpeed);
        println("[ANALYTIC SELF TEST] plane translating along its normal: slides " ~
            normalStructure.slidesEverywhere ~ ", constant error " ~ constantError);
        checks += 1;
        if (normalStructure.slidesEverywhere || constantError > 1e-16)
        {
            failures = failures ~ " plane normal-translation constant error " ~ constantError ~ ".";
        }

        // ---------- The rigid-motion consequence ----------
        // A exactly orthonormal makes W = A^T A' skew, and a skew W contributes nothing to any
        // quadratic-in-normal term. For a cylinder that means the radius term drops out and the
        // contact curve is first-harmonic only. Checked on the coefficients, so it is exact.
        const skewSample = constantMotionSample(identityRotationRows(),
                [[0, -0.4, 0.25], [0.4, 0, -0.13], [-0.25, 0.13, 0]], vector(0.2, -0.1, 0.05));
        const skewStructure = analyticContactStructure(cylinderFrame,
            analyticContactPullback(skewSample, cylinderFrame), {});
        const secondHarmonic = max(abs(skewStructure.constantTerm.cosine[2]),
                abs(skewStructure.constantTerm.sine[2]));
        println("[ANALYTIC SELF TEST] skew W leaves the cylinder second harmonic at " ~ secondHarmonic ~
            " and its constant at " ~ abs(skewStructure.constantTerm.cosine[0]));
        checks += 1;
        if (secondHarmonic > 1e-17 || abs(skewStructure.constantTerm.cosine[0]) > 1e-17)
        {
            failures = failures ~ " skew W left a quadratic term of " ~ secondHarmonic ~ ".";
        }

        // ---------- Contact curves against closed-form answers ----------
        // Cylinder under a translation PERPENDICULAR to its axis: f = <d(theta), b'>, so contact
        // is the two rulings at theta = atan2(-b'1, b'2) mod pi. Nothing numerical about it.
        const crossSpeed = 0.4;
        const crossDirection = inPlane;
        const crossPullback = analyticContactPullback(
                constantMotionSample(identityRotationRows(), zeroRows(), crossSpeed * crossDirection), cylinderFrame);
        const crossStructure = analyticContactStructure(cylinderFrame, crossPullback, {});
        const crossRoots = solveTrigPolynomialRoots(crossStructure.constantTerm, 0, {});
        const expectedTheta = atan2(-crossStructure.constantTerm.cosine[1],
                crossStructure.constantTerm.sine[1]) / radian;
        var worstRootError = 0;
        for (var root in crossRoots.roots)
        {
            var difference = abs(root - expectedTheta);
            while (difference > PI + 1e-9)
            {
                difference -= PI;
            }
            worstRootError = max(worstRootError, min(difference, abs(difference - PI)));
        }
        println("[ANALYTIC SELF TEST] cylinder crossing translation: " ~ size(crossRoots.roots) ~
            " contact rulings, worst theta error " ~ worstRootError ~
            ", residual " ~ crossRoots.worstResidual);
        checks += 1;
        if (size(crossRoots.roots) != 2 || worstRootError > 1e-12)
        {
            failures = failures ~ " cylinder crossing-translation rulings: " ~
                size(crossRoots.roots) ~ " roots, worst error " ~ worstRootError ~ ".";
        }

        // Sphere under a translation: contact is the great circle perpendicular to b', so along
        // each meridian the latitude solves tan(phi) = -<d, b'> / <axis, b'> in closed form.
        const sphereFrame = stripAnalyticSurface(sphere(testCoordSystem, 0.051 * meter));
        const sphereVelocity = vector(0.22, -0.31, 0.17);
        const spherePullback = analyticContactPullback(
                constantMotionSample(identityRotationRows(), zeroRows(), sphereVelocity), sphereFrame);
        var worstSphereLatitude = 0;
        var sphereRootTotal = 0;
        for (var i = 0; i < 12; i += 1)
        {
            const theta = 2 * PI * i / 12;
            const meridian = analyticMeridianPolynomial(sphereFrame, spherePullback, theta);
            const solved = solveTrigPolynomialRoots(meridian, -PI, {});
            sphereRootTotal += size(solved.roots);
            const localVelocity = vector(dot(sphereFrame.basis[0], sphereVelocity),
                    dot(sphereFrame.basis[1], sphereVelocity), dot(sphereFrame.basis[2], sphereVelocity));
            const expected = atan2(-(localVelocity[0] * cos(theta * radian) +
                            localVelocity[1] * sin(theta * radian)), localVelocity[2]) / radian;
            for (var root in solved.roots)
            {
                var difference = abs(root - expected);
                worstSphereLatitude = max(worstSphereLatitude,
                    min(difference, min(abs(difference - PI), abs(difference - 2 * PI))));
            }
        }
        println("[ANALYTIC SELF TEST] sphere translation great circle: " ~ sphereRootTotal ~
            " roots over 12 meridians, worst latitude error " ~ worstSphereLatitude);
        checks += 1;
        if (worstSphereLatitude > 1e-12)
        {
            failures = failures ~ " sphere great-circle latitude error " ~ worstSphereLatitude ~ ".";
        }

        // ---------- Trig polynomial utilities ----------
        // cos(2x) has its four roots at the odd multiples of pi/4, exactly.
        const doubled = trigPolynomial([0, 0, 1], [0, 0, 0]);
        const doubledRoots = solveTrigPolynomialRoots(doubled, 0, {});
        var worstDoubled = 0;
        for (var index = 0; index < size(doubledRoots.roots); index += 1)
        {
            worstDoubled = max(worstDoubled, abs(doubledRoots.roots[index] - (0.25 + 0.5 * index) * PI));
        }
        println("[ANALYTIC SELF TEST] cos(2x) roots: " ~ size(doubledRoots.roots) ~ ", worst error " ~
            worstDoubled);
        checks += 1;
        if (size(doubledRoots.roots) != 4 || worstDoubled > 1e-14)
        {
            failures = failures ~ " cos(2x) gave " ~ size(doubledRoots.roots) ~ " roots, worst error " ~
                worstDoubled ~ ".";
        }

        // The bound must never be exceeded by an actual sample - that is the whole point of a
        // screen. Checked on the general cylinder's own coefficients.
        const boundClaim = trigPolynomialBound(cylinderStructure.constantTerm);
        var worstSampled = 0;
        for (var index = 0; index <= 720; index += 1)
        {
            worstSampled = max(worstSampled,
                abs(trigPolynomialValue(cylinderStructure.constantTerm, 2 * PI * index / 720)));
        }
        println("[ANALYTIC SELF TEST] trig bound " ~ boundClaim ~ " against sampled maximum " ~ worstSampled);
        checks += 1;
        if (worstSampled > boundClaim * (1 + 1e-12))
        {
            failures = failures ~ " a sample exceeded the trig bound (" ~ worstSampled ~ " > " ~
                boundClaim ~ ").";
        }

        // An identically zero polynomial must be reported as such, not handed back as roots.
        const zeroPolynomial = trigPolynomial([0, 0, 0], [0, 0, 0]);
        const zeroSolved = solveTrigPolynomialRoots(zeroPolynomial, 0, {});
        checks += 1;
        if (!zeroSolved.identicallyZero || size(zeroSolved.roots) != 0)
        {
            failures = failures ~ " an identically zero polynomial was not reported as sliding.";
        }

        // ---------- The free screen ----------
        // A plane translating fast along its normal cannot graze anywhere, and the bound proves
        // it without evaluating f at a single parameter.
        const screenBound = analyticContactBound(planeFrame,
            analyticContactPullback(constantMotionSample(identityRotationRows(), zeroRows(), 0.9 * axis), planeFrame),
            { "uMin" : -0.05, "uMax" : 0.05, "vMin" : -0.05, "vMax" : 0.05 }, {});
        println("[ANALYTIC SELF TEST] non-grazing plane screen: minimum |f| " ~
            screenBound.minimumMagnitude);
        checks += 1;
        if (screenBound.minimumMagnitude <= 0)
        {
            failures = failures ~ " the screen failed to reject a plane that cannot graze.";
        }

        reportTestVerdict(context, id, "ANALYTIC SELF TEST", checks, failures,
            ("all five analytic classes agree with the section 1.1 " ~
            "definition to machine precision, the ruled and meridian structures reproduce f, " ~
            "sliding and the rigid-motion skew consequence are exact, and the closed-form " ~
            "contact curves match their analytic answers."));
    });

/**
 * A generator curve for a profile-driven analytic frame, built from local pairs along two given
 * world directions. `pairs` are (radius, height) for a revolve and (x, y) for an extrusion, plain
 * numbers, meters implied - the same units contract the rest of the stack runs on.
 */
function profileFromLocalPairs(frameOrigin is Vector, firstDirection is Vector,
    secondDirection is Vector, pairs is array, degree is number, knots is array, weights) returns map
{
    var controlPoints = makeArray(size(pairs));
    for (var index = 0; index < size(pairs); index += 1)
    {
        controlPoints[index] = frameOrigin + pairs[index][0] * firstDirection +
            pairs[index][1] * secondDirection;
    }
    return {
            "degree" : degree,
            "knots" : knots,
            "controlPoints" : controlPoints,
            "isRational" : weights != undefined,
            "weights" : weights,
            "isPeriodic" : false
        };
}

/** Control point pairs on a circle of the given radius, spanning `sweepAngle` radians from zero. */
function circularProfilePairs(radius is number, sweepAngle is number, count is number) returns array
{
    var pairs = makeArray(count);
    for (var index = 0; index < count; index += 1)
    {
        const angle = sweepAngle * index / (count - 1);
        pairs[index] = [radius * cos(angle * radian), radius * sin(angle * radian)];
    }
    return pairs;
}

annotation { "Feature Type Name" : "Sweep Analytic Profile Contact Self Test" }
export const sweepAnalyticProfileContactSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // The two PROFILE-driven analytic classes of spec 6.5.1, selection-free: every generator
        // here is a hand-built spline, so the algebra is tested without any kernel recovery in
        // front of it. The station carries a nonzero rotation derivative and a deliberately
        // NON-orthonormal A, for the same reason the five-class test does - the pullback identity
        // holds for any A, and exercising it away from SO(3) separates an algebra error from an
        // orthonormality assumption.
        var tally = newCheckTally();

        const generalSample = constantMotionSample(
                [[0.93, -0.21, 0.11], [0.24, 0.88, -0.17], [-0.08, 0.19, 0.97]],
                [[0.13, -0.44, 0.28], [0.51, 0.07, -0.33], [-0.22, 0.39, 0.05]],
                vector(0.31, -0.17, 0.44));
        const axis = normalize(vector(0.3, -0.5, 0.81));
        const inPlane = normalize(cross(axis, vector(0.11, 0.97, -0.2)));
        const secondInPlane = cross(axis, inPlane);
        const frameOrigin = vector(0.07, -0.13, 0.21);
        const cubicKnots = [0, 0, 0, 0, 0.5, 1, 1, 1, 1];

        // ---------- REVOLVED: a non-rational cubic generator ----------
        const revolvedFrame = analyticProfileFrame(AnalyticSurfaceKind.REVOLVED, frameOrigin, axis, inPlane,
                profileFromLocalPairs(frameOrigin, inPlane, axis,
                    [[0.050, -0.040], [0.062, -0.020], [0.048, 0.000], [0.055, 0.020], [0.050, 0.040]],
                    3, cubicKnots, undefined));
        const revolvedPullback = analyticContactPullback(generalSample, revolvedFrame);
        println("[ANALYTIC PROFILE SELF TEST] revolved frame: generator degree " ~
            revolvedFrame.profile.degree ~ " x " ~ size(revolvedFrame.profile.controlPoints) ~
            " on [" ~ revolvedFrame.profileStart ~ ", " ~ revolvedFrame.profileEnd ~
            "], out of plane " ~ revolvedFrame.profileOutOfPlane);
        tally = checkWithin(tally, revolvedFrame.profileOutOfPlane, 1e-16,
            "the revolved generator's out-of-plane coordinate");

        var worstRevolvedCrossPath = 0;
        var worstRevolvedStructure = 0;
        for (var i = 0; i <= 8; i += 1)
        {
            const u = revolvedFrame.profileStart +
                (revolvedFrame.profileEnd - revolvedFrame.profileStart) * i / 8;
            const meridian = analyticRevolvedThetaPolynomial(revolvedFrame, revolvedPullback, u);
            for (var j = 0; j <= 12; j += 1)
            {
                const theta = 2 * PI * j / 12;
                const closedForm = evaluateAnalyticContact(revolvedFrame, revolvedPullback, u, theta);
                worstRevolvedCrossPath = max(worstRevolvedCrossPath, abs(closedForm -
                            evaluateAnalyticContactDirect(revolvedFrame, generalSample, u, theta)));
                worstRevolvedStructure = max(worstRevolvedStructure,
                    abs(trigPolynomialValue(meridian, theta) - closedForm));
            }
        }
        println("[ANALYTIC PROFILE SELF TEST] REVOLVED closed form vs the section 1.1 definition: " ~
            worstRevolvedCrossPath);
        println("[ANALYTIC PROFILE SELF TEST] REVOLVED theta polynomial vs closed form: " ~
            worstRevolvedStructure);
        tally = checkWithin(tally, worstRevolvedCrossPath, 1e-14,
            "the REVOLVED closed form's disagreement with the direct definition");
        tally = checkWithin(tally, worstRevolvedStructure, 1e-15,
            "the REVOLVED theta polynomial's disagreement with the closed form");

        // ---------- REVOLVED, rational: the same surface as the SPHERE class ----------
        // A revolved exact quarter circle IS a sphere, so the two classes must agree - and they
        // agree through DIFFERENT algebra, one evaluating a rational curve and one a formula.
        // The relation is exact rather than approximate: this module's revolved normal is the
        // unnormalized S_u x S_v, which for a circular generator is -|C'(u)| times the sphere's
        // unit normal, so f_revolved + |C'| f_sphere is identically zero.
        const sphereRadius = 0.051;
        const rationalFrame = analyticProfileFrame(AnalyticSurfaceKind.REVOLVED, frameOrigin, axis, inPlane,
                profileFromLocalPairs(frameOrigin, inPlane, axis,
                    [[sphereRadius, 0], [sphereRadius, sphereRadius], [0, sphereRadius]],
                    2, [0, 0, 0, 1, 1, 1], [1, sqrt(0.5), 1]));
        const sphereFrame = stripAnalyticSurface(sphere(coordSystem(frameOrigin * meter, inPlane, axis),
                sphereRadius * meter));
        const sharedPullback = analyticContactPullback(generalSample, rationalFrame);
        var worstBasisAgreement = 0;
        for (var index = 0; index < 3; index += 1)
        {
            worstBasisAgreement = max(worstBasisAgreement,
                norm(rationalFrame.basis[index] - sphereFrame.basis[index]));
        }
        var worstRadiusError = 0;
        var worstSphereAgreement = 0;
        for (var i = 0; i <= 8; i += 1)
        {
            const u = i / 8;
            const local = analyticProfileDerivatives(rationalFrame, u, 1);
            const generatorSpeed = norm(local[1]);
            worstRadiusError = max(worstRadiusError,
                abs(sqrt(local[0][0] ^ 2 + local[0][2] ^ 2) - sphereRadius));
            const latitude = atan2(local[0][2], local[0][0]) / radian;
            for (var j = 0; j <= 12; j += 1)
            {
                const theta = 2 * PI * j / 12;
                worstSphereAgreement = max(worstSphereAgreement,
                    abs(evaluateAnalyticContact(rationalFrame, sharedPullback, u, theta) +
                        generatorSpeed *
                        evaluateAnalyticContact(sphereFrame, sharedPullback, theta, latitude)));
            }
        }
        println("[ANALYTIC PROFILE SELF TEST] rational quarter circle: basis agreement " ~
            worstBasisAgreement ~ ", radius error " ~ worstRadiusError ~
            ", REVOLVED against SPHERE " ~ worstSphereAgreement);
        tally = checkWithin(tally, worstBasisAgreement, 1e-15,
            "the revolved and sphere frames' basis disagreement");
        tally = checkWithin(tally, worstRadiusError, 1e-16,
            "the rational generator's deviation from an exact circle");
        tally = checkWithin(tally, worstSphereAgreement, 1e-15,
            "the REVOLVED form's disagreement with the SPHERE form on the same surface");

        // ---------- REVOLVED sliding: rotation about its own axis, exactly ----------
        // The whole surface slides along itself, and it does so in the coefficients rather than
        // in a sampled residual: with W skew about e3 and g zero, every one of the five theta
        // coefficients cancels term by term.
        const spinRate = 0.7;
        const spinDerivative = [[0, -spinRate * axis[2], spinRate * axis[1]],
                [spinRate * axis[2], 0, -spinRate * axis[0]],
                [-spinRate * axis[1], spinRate * axis[0], 0]];
        const spinSample = constantMotionSample(identityRotationRows(), spinDerivative,
                -1 * (matrix(spinDerivative) * frameOrigin));
        const spinStructure = analyticContactStructure(revolvedFrame,
            analyticContactPullback(spinSample, revolvedFrame), {});
        println("[ANALYTIC PROFILE SELF TEST] revolve spinning about its own axis slides: " ~
            spinStructure.slidesEverywhere);
        tally = checkThat(tally, spinStructure.slidesEverywhere,
            "a revolved face spinning about its own axis was not detected as sliding.");

        // ---------- REVOLVED contact curve ----------
        const revolvedCurve = solveAnalyticContactCurve(revolvedFrame, revolvedPullback,
                { "sampleCount" : 24 });
        var worstRevolvedRoot = 0;
        for (var sample in revolvedCurve.samples)
        {
            worstRevolvedRoot = max(worstRevolvedRoot,
                abs(evaluateAnalyticContact(revolvedFrame, revolvedPullback, sample[0], sample[1])));
        }
        println("[ANALYTIC PROFILE SELF TEST] REVOLVED contact curve: form " ~ revolvedCurve.form ~
            ", " ~ size(revolvedCurve.samples) ~ " samples, worst |f| " ~ worstRevolvedRoot ~
            ", near tangency " ~ revolvedCurve.nearTangency);
        tally = checkThat(tally, size(revolvedCurve.samples) > 0,
            "the REVOLVED contact curve came back empty.");
        tally = checkWithin(tally, worstRevolvedRoot, 1e-12,
            "the worst |f| on the REVOLVED contact curve");

        // ---------- EXTRUDED: a cubic cross section whose tangent sweeps most of a turn ----------
        // The wide sweep is deliberate: B(u) is a linear form in the tangent direction, so a
        // tangent that turns through more than half a period GUARANTEES B vanishes somewhere and
        // the singular-u machinery is actually exercised rather than trivially empty.
        const extrudedFrame = analyticProfileFrame(AnalyticSurfaceKind.EXTRUDED, frameOrigin, axis, inPlane,
                profileFromLocalPairs(frameOrigin, inPlane, secondInPlane,
                    circularProfilePairs(0.04, 1.75 * PI, 8), 3,
                    [0, 0, 0, 0, 0.2, 0.4, 0.6, 0.8, 1, 1, 1, 1], undefined));
        const extrudedPullback = analyticContactPullback(generalSample, extrudedFrame);
        println("[ANALYTIC PROFILE SELF TEST] extruded frame: cross section degree " ~
            extrudedFrame.profile.degree ~ " x " ~ size(extrudedFrame.profile.controlPoints) ~
            ", out of plane " ~ extrudedFrame.profileOutOfPlane);
        tally = checkWithin(tally, extrudedFrame.profileOutOfPlane, 1e-16,
            "the extruded cross section's out-of-plane coordinate");

        var worstExtrudedCrossPath = 0;
        var worstExtrudedStructure = 0;
        for (var i = 0; i <= 8; i += 1)
        {
            const u = extrudedFrame.profileStart +
                (extrudedFrame.profileEnd - extrudedFrame.profileStart) * i / 8;
            const terms = analyticExtrudedRulingTerms(extrudedFrame, extrudedPullback, u);
            for (var j = 0; j <= 6; j += 1)
            {
                const v = -0.05 + 0.1 * j / 6;
                const closedForm = evaluateAnalyticContact(extrudedFrame, extrudedPullback, u, v);
                worstExtrudedCrossPath = max(worstExtrudedCrossPath, abs(closedForm -
                            evaluateAnalyticContactDirect(extrudedFrame, generalSample, u, v)));
                worstExtrudedStructure = max(worstExtrudedStructure,
                    abs(terms.constant + v * terms.ruling - closedForm));
            }
        }
        println("[ANALYTIC PROFILE SELF TEST] EXTRUDED closed form vs the section 1.1 definition: " ~
            worstExtrudedCrossPath);
        println("[ANALYTIC PROFILE SELF TEST] EXTRUDED A(u) + v B(u) vs closed form: " ~
            worstExtrudedStructure);
        tally = checkWithin(tally, worstExtrudedCrossPath, 1e-14,
            "the EXTRUDED closed form's disagreement with the direct definition");
        tally = checkWithin(tally, worstExtrudedStructure, 1e-15,
            "the EXTRUDED ruled structure's disagreement with the closed form");

        // ---------- EXTRUDED sliding: translation along the extrusion, exactly ----------
        // N = C' x e3 is perpendicular to e3 everywhere, so a velocity along e3 dots to zero at
        // every parameter with nothing to sample.
        const alongStructure = analyticContactStructure(extrudedFrame,
            analyticContactPullback(constantMotionSample(identityRotationRows(), zeroRows(), 0.35 * axis),
                extrudedFrame), {});
        println("[ANALYTIC PROFILE SELF TEST] extrusion translating along its own direction slides: " ~
            alongStructure.slidesEverywhere);
        tally = checkThat(tally, alongStructure.slidesEverywhere,
            "an extruded face translating along its own direction was not detected as sliding.");

        // ---------- EXTRUDED contact curve and its singular rulings ----------
        const extrudedCurve = solveAnalyticContactCurve(extrudedFrame, extrudedPullback,
                { "sampleCount" : 48 });
        var worstGraphResidual = 0;
        for (var sample in extrudedCurve.samples)
        {
            const terms = analyticExtrudedRulingTerms(extrudedFrame, extrudedPullback, sample[0]);
            const scale = max(1e-30, abs(terms.constant) + abs(sample[1] * terms.ruling));
            worstGraphResidual = max(worstGraphResidual,
                abs(evaluateAnalyticContact(extrudedFrame, extrudedPullback, sample[0], sample[1])) / scale);
        }
        var worstSingularRuling = 0;
        for (var singular in extrudedCurve.singularProfileParameters)
        {
            worstSingularRuling = max(worstSingularRuling,
                abs(analyticExtrudedRulingTerms(extrudedFrame, extrudedPullback, singular).ruling));
        }
        println("[ANALYTIC PROFILE SELF TEST] EXTRUDED contact curve: form " ~ extrudedCurve.form ~
            ", " ~ size(extrudedCurve.samples) ~ " graph samples, worst relative residual " ~
            worstGraphResidual);
        println("[ANALYTIC PROFILE SELF TEST] EXTRUDED singular rulings: " ~
            size(extrudedCurve.singularProfileParameters) ~ " at " ~
            extrudedCurve.singularProfileParameters ~ ", worst |B| " ~ worstSingularRuling);
        tally = checkThat(tally, size(extrudedCurve.samples) > 0,
            "the EXTRUDED contact graph came back empty.");
        tally = checkWithin(tally, worstGraphResidual, 1e-12,
            "the worst relative |f| on the EXTRUDED contact graph");
        tally = checkThat(tally, size(extrudedCurve.singularProfileParameters) > 0,
            "the EXTRUDED singular-ruling search found nothing on a cross section whose tangent " ~
            "sweeps 315 degrees, where a linear form in the tangent must vanish.");
        tally = checkWithin(tally, worstSingularRuling, 1e-14,
            "the worst |B| at a reported singular ruling");

        // ---------- The free screen, on both classes ----------
        // Sampled along the generator and rigorous in the second parameter, which is the same
        // guarantee sphere and torus give. A sample must never exceed the claim.
        const revolvedBound = analyticContactBound(revolvedFrame, revolvedPullback,
                { "uMin" : revolvedFrame.profileStart, "uMax" : revolvedFrame.profileEnd,
                    "vMin" : 0, "vMax" : 2 * PI }, { "profileSamples" : 33 });
        var worstRevolvedSample = 0;
        for (var i = 0; i <= 32; i += 1)
        {
            const u = revolvedFrame.profileStart +
                (revolvedFrame.profileEnd - revolvedFrame.profileStart) * i / 32;
            for (var j = 0; j < 36; j += 1)
            {
                worstRevolvedSample = max(worstRevolvedSample,
                    abs(evaluateAnalyticContact(revolvedFrame, revolvedPullback, u, 2 * PI * j / 36)));
            }
        }
        println("[ANALYTIC PROFILE SELF TEST] REVOLVED bound " ~ revolvedBound.maximumMagnitude ~
            " against sampled maximum " ~ worstRevolvedSample);
        tally = checkThat(tally, worstRevolvedSample <= revolvedBound.maximumMagnitude * (1 + 1e-12),
            "a sample exceeded the REVOLVED bound (" ~ worstRevolvedSample ~ " > " ~
            revolvedBound.maximumMagnitude ~ ").");

        const extrudedBound = analyticContactBound(extrudedFrame, extrudedPullback,
                { "uMin" : extrudedFrame.profileStart, "uMax" : extrudedFrame.profileEnd,
                    "vMin" : -0.05, "vMax" : 0.05 }, { "profileSamples" : 33 });
        var worstExtrudedSample = 0;
        for (var i = 0; i <= 32; i += 1)
        {
            const u = extrudedFrame.profileStart +
                (extrudedFrame.profileEnd - extrudedFrame.profileStart) * i / 32;
            for (var j = 0; j <= 8; j += 1)
            {
                worstExtrudedSample = max(worstExtrudedSample,
                    abs(evaluateAnalyticContact(extrudedFrame, extrudedPullback, u, -0.05 + 0.1 * j / 8)));
            }
        }
        println("[ANALYTIC PROFILE SELF TEST] EXTRUDED bound " ~ extrudedBound.maximumMagnitude ~
            " against sampled maximum " ~ worstExtrudedSample);
        tally = checkThat(tally, worstExtrudedSample <= extrudedBound.maximumMagnitude * (1 + 1e-12),
            "a sample exceeded the EXTRUDED bound (" ~ worstExtrudedSample ~ " > " ~
            extrudedBound.maximumMagnitude ~ ").");

        reportCheckTally(context, id, "ANALYTIC PROFILE SELF TEST", tally,
            "REVOLVED and EXTRUDED agree with the section 1.1 definition to machine precision, " ~
            "their theta polynomial and ruled structures reproduce f, a rational quarter-circle " ~
            "generator reproduces the SPHERE class exactly, both sliding cases come out of the " ~
            "coefficients, the contact curves sit on f = 0, the singular rulings are found rather " ~
            "than sampled through, and neither bound is exceeded.");
    });

/**
 * The distance from each of `worldPoints` to `face`, and the kernel's own unit normal at the
 * nearest point of the face - one evDistance per point plus one batched tangent-plane call.
 * Returns { worstDistance {number, meters}, normals {array of Vector} }.
 */
function faceProximityAndNormals(context is Context, face is Query, worldPoints is array) returns map
{
    var parameters = makeArray(size(worldPoints));
    var worstDistance = 0;
    for (var index = 0; index < size(worldPoints); index += 1)
    {
        const measured = evDistance(context, {
                    "side0" : worldPoints[index] * meter,
                    "side1" : face
                });
        worstDistance = max(worstDistance, measured.distance / meter);
        parameters[index] = vector(measured.sides[1].parameter[0], measured.sides[1].parameter[1]);
    }
    const tangentPlanes = evFaceTangentPlanes(context, { "face" : face, "parameters" : parameters });
    var normals = makeArray(size(worldPoints));
    for (var index = 0; index < size(worldPoints); index += 1)
    {
        normals[index] = tangentPlanes[index].normal;
    }
    return { "worstDistance" : worstDistance, "normals" : normals };
}

/** The world point and world normal of a profile-driven frame at one parameter pair. */
function analyticWorldPointAndNormal(frame is map, u is number, v is number) returns map
{
    const local = analyticLocalPointAndNormal(frame, u, v);
    const basis = frame.basis;
    return {
            "point" : frame.origin + local.point[0] * basis[0] + local.point[1] * basis[1] +
                local.point[2] * basis[2],
            "normal" : local.normal[0] * basis[0] + local.normal[1] * basis[1] +
                local.normal[2] * basis[2]
        };
}

/**
 * One profile-driven fixture, from extraction to a contact curve the kernel confirms. Returns the
 * updated tally, and returns EARLY rather than throwing wherever the answer is already decided -
 * the caller wraps this in a catch, and a live test that dies takes its own console output with
 * it (a feature that throws is never added, so its printlns are never readable).
 *
 * fixture: [ label, body query, expected SweepSurfaceClass, expected AnalyticSurfaceKind,
 * second-parameter start, second-parameter end ].
 */
function checkProfileFixture(context is Context, nextId is function, tally is map,
    fixture is array, stage is box) returns map
{
    var running = tally;
    const label = fixture[0];
    const body = fixture[1];
    stage[] = "extraction";
    const records = extractToolFaceRecords(context, body, 1e-6, false, nextId);
    println("[ANALYTIC PROFILE LIVE TEST] " ~ label ~ ": " ~ summarizeFaceRecords(records));
    running = checkThat(running, size(records) == 1,
        label ~ " extracted " ~ size(records) ~ " face records, expected 1.");
    if (size(records) != 1)
    {
        return running;
    }
    const record = records[0];
    running = checkThat(running, record.surfaceClass == fixture[2],
        label ~ " classified as " ~ record.surfaceClass ~ " instead.");
    running = checkThat(running, record.spline == undefined && record.trimLoops == undefined &&
            record.splineIsExact == false,
        label ~ " carries approximation output, so it reached evApproximateBSplineSurface.");
    // The message is an ARGUMENT, so it is built whether the check passes or not - and on a pass
    // the refusal is undefined, which concatenation will not take.
    const refusalText = record.profileRecovery == undefined ? "no recovery was attempted" :
        (record.profileRecovery.refusal == undefined ? "no refusal was recorded" :
            record.profileRecovery.refusal);
    running = checkThat(running, record.analyticFrame != undefined,
        label ~ " produced no analytic frame: " ~ refusalText);
    if (record.analyticFrame == undefined)
    {
        return running;
    }

    stage[] = "reading the recovered frame";
    const frame = analyticFrameForFaceRecord(record);
    const recovery = record.profileRecovery;
    println("[ANALYTIC PROFILE LIVE TEST] " ~ label ~ " recovery: " ~ recovery.profileEdgeCount ~
        " profile edge(s), generator " ~ recovery.profileCurveClass ~ " degree " ~
        frame.profile.degree ~ " x " ~ size(frame.profile.controlPoints) ~ ", rational " ~
        (frame.profile.isRational == true) ~ ", direction residual " ~ recovery.directionResidual ~
        ", out of plane " ~ frame.profileOutOfPlane ~ " m");
    running = checkThat(running, frame.kind == fixture[3],
        label ~ " built a " ~ frame.kind ~ " frame.");
    running = checkThat(running, recovery.profileCurveClass == SweepCurveClass.BSPLINE,
        label ~ " recovered a generator that is not a spline.");
    running = checkWithin(running, frame.profileOutOfPlane, 1e-9,
        label ~ "'s generator out-of-plane coordinate (m)");

    // The load-bearing live check: the frame's own point and normal against the KERNEL's, on a
    // grid over the face. A wrong axis, a wrong radial direction, or a generator read in the wrong
    // plane all fail here and nowhere in the pure algebra.
    stage[] = "the point and normal grid";
    const faces = qOwnedByBody(body, EntityType.FACE);
    const secondStart = fixture[4];
    const secondEnd = fixture[5];
    var gridPoints = makeArray(16);
    var gridNormals = makeArray(16);
    var gridCount = 0;
    for (var i = 0; i < 4; i += 1)
    {
        for (var j = 0; j < 4; j += 1)
        {
            // Corners are avoided: the face's own ends are its trim boundary, and a nearest-point
            // query there can land on an edge rather than on the interior.
            const u = frame.profileStart + (frame.profileEnd - frame.profileStart) * (0.1 + 0.8 * i / 3);
            const v = secondStart + (secondEnd - secondStart) * (0.1 + 0.8 * j / 3);
            const world = analyticWorldPointAndNormal(frame, u, v);
            gridPoints[gridCount] = world.point;
            gridNormals[gridCount] = world.normal;
            gridCount += 1;
        }
    }
    println("[ANALYTIC PROFILE LIVE TEST] " ~ label ~ ": measuring " ~ gridCount ~
        " reconstructed points against the kernel face...");
    const proximity = faceProximityAndNormals(context, faces, gridPoints);
    var worstNormalAngle = 0;
    for (var index = 0; index < gridCount; index += 1)
    {
        worstNormalAngle = max(worstNormalAngle,
            norm(cross(normalize(gridNormals[index]), proximity.normals[index])));
    }
    println("[ANALYTIC PROFILE LIVE TEST] " ~ label ~ " against the kernel face: worst point " ~
        "distance " ~ proximity.worstDistance ~ " m, worst normal cross product " ~ worstNormalAngle);
    running = checkWithin(running, proximity.worstDistance, 1e-9,
        label ~ "'s reconstructed points' distance from the kernel face (m)");
    running = checkWithin(running, worstNormalAngle, 1e-7,
        label ~ "'s reconstructed normals' cross product against the kernel's");

    // And the answer itself. The station is built FROM the recovered frame so that its answer is
    // known in closed form, which makes this a measurement of the recovery rather than another
    // internal residual. Rotation is the self test's job; what is at stake here is whether the
    // frame is the face's own.
    //
    // REVOLVED: translate along the frame's own e1. Then W is zero and g = (speed, 0, 0), so
    // f = -z'(u) speed cos(theta) and the contact set is EXACTLY the two meridians at theta =
    // pi/2 and 3 pi/2, at every u where the generator's height is not stationary.
    //
    // EXTRUDED: translate along the generator's own tangent at the mid parameter. There
    // A = <C' x e3, C'> = 0 exactly, so that whole ruling is in the contact set - and a pure
    // translation is also the case B == 0 identically, which is the whole-ruling branch. Both the
    // answer and the branch taken are known in advance.
    stage[] = "the closed-form contact curve";
    const midParameter = 0.5 * (frame.profileStart + frame.profileEnd);
    const midTangent = analyticProfileDerivatives(frame, midParameter, 1)[1];
    const stationVelocity = frame.kind == AnalyticSurfaceKind.REVOLVED ?
        0.3 * frame.basis[0] :
        0.3 * normalize(midTangent[0] * frame.basis[0] + midTangent[1] * frame.basis[1] +
                midTangent[2] * frame.basis[2]);
    const liveSample = constantMotionSample(identityRotationRows(), zeroRows(), stationVelocity);
    const pullback = analyticContactPullback(liveSample, frame);
    const contact = solveAnalyticContactCurve(frame, pullback, { "sampleCount" : 8 });
    var contactPoints = makeArray(size(contact.samples));
    var worstOwnResidual = 0;
    for (var index = 0; index < size(contact.samples); index += 1)
    {
        const sample = contact.samples[index];
        contactPoints[index] = analyticWorldPointAndNormal(frame, sample[0], sample[1]).point;
        worstOwnResidual = max(worstOwnResidual,
            abs(evaluateAnalyticContact(frame, pullback, sample[0], sample[1])));
    }
    println("[ANALYTIC PROFILE LIVE TEST] " ~ label ~ " contact curve: form " ~ contact.form ~ ", " ~
        size(contact.samples) ~ " samples, worst own |f| " ~ worstOwnResidual ~
        ", singular thetas " ~ size(contact.singularThetas) ~ ", singular u " ~
        size(contact.singularProfileParameters) ~ ", near tangency " ~ contact.nearTangency);
    running = checkThat(running, size(contact.samples) > 0,
        label ~ "'s contact curve came back empty where a closed-form answer says it should not be.");

    if (frame.kind == AnalyticSurfaceKind.REVOLVED)
    {
        var worstMeridianError = 0;
        for (var sample in contact.samples)
        {
            worstMeridianError = max(worstMeridianError,
                min(abs(sample[1] - 0.5 * PI), abs(sample[1] - 1.5 * PI)));
        }
        println("[ANALYTIC PROFILE LIVE TEST] REVOLVED contact meridians: worst theta error " ~
            "against pi/2 and 3 pi/2 is " ~ worstMeridianError);
        running = checkWithin(running, worstMeridianError, 1e-9,
            "the REVOLVED contact curve's departure from its closed-form meridians");
    }
    else
    {
        var bestRulingError = 1e300;
        for (var sample in contact.samples)
        {
            bestRulingError = min(bestRulingError, abs(sample[0] - midParameter));
        }
        println("[ANALYTIC PROFILE LIVE TEST] EXTRUDED whole rulings: nearest to the tangent " ~
            "parameter " ~ midParameter ~ " is off by " ~ bestRulingError);
        running = checkThat(running, contact.form == "profileRulings",
            "a pure translation gave form " ~ contact.form ~
            " instead of whole rulings on the EXTRUDED face.");
        running = checkWithin(running, bestRulingError, 1e-9,
            "the EXTRUDED contact ruling's departure from the parameter whose tangent the " ~
            "station translates along");
    }

    if (size(contact.samples) == 0)
    {
        return running;
    }
    // The strongest statement the analytic layer can make: its contact set is the face's TRUE
    // grazing set, checked with the kernel's own normals rather than with ours.
    stage[] = "the contact curve against the kernel's normals";
    const contactProximity = faceProximityAndNormals(context, faces, contactPoints);
    var worstKernelContact = 0;
    var contactScale = 0;
    for (var index = 0; index < size(contactPoints); index += 1)
    {
        const velocity = liveSample.rotationDerivative * contactPoints[index] +
            liveSample.translationDerivative;
        worstKernelContact = max(worstKernelContact,
            abs(dot(liveSample.rotation * contactProximity.normals[index], velocity)));
        contactScale = max(contactScale, norm(velocity));
    }
    println("[ANALYTIC PROFILE LIVE TEST] " ~ label ~ " contact curve against the kernel's own " ~
        "normals: worst |f| " ~ worstKernelContact ~ " at speeds up to " ~ contactScale ~
        ", worst point distance " ~ contactProximity.worstDistance ~ " m");
    running = checkWithin(running, contactProximity.worstDistance, 1e-9,
        label ~ "'s contact points' distance from the kernel face (m)");
    running = checkWithin(running, worstKernelContact, 1e-7,
        label ~ "'s contact curve residual against the kernel's own normals");
    stage[] = "finished";
    return running;
}

annotation { "Feature Type Name" : "Sweep Analytic Profile Live Test" }
export const sweepAnalyticProfileLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // Tier 1 item 3's done-when, on real kernel faces: a revolved-spline face and an
        // extruded-spline face each reach the analytic layer, and NEITHER touches
        // evApproximateBSplineSurface. What is proved here that the self test cannot prove is that
        // the RECOVERED generator is the face's own - every check measures the closed form against
        // the kernel's geometry rather than against more of our own algebra.
        //
        // Every stage is caught rather than allowed to propagate. That is not defensive habit: a
        // feature that throws is refused by the feature list, so its console output never becomes
        // readable, and the console is where a live test says what it found.
        var tally = newCheckTally();
        const nextId = getUnstableIncrementingId(id);

        var built = false;
        try silent
        {
            // Fixture 1: a spline revolved about an axis it does not touch. The sketch plane's
            // normal is -Y, so sketch (a, b) lands at world (a, 0, b) and the Z axis lies in it.
            const revolveSketchId = id + "revolveSketch";
            const revolveSketch = newSketchOnPlane(context, revolveSketchId, {
                        "sketchPlane" : plane(vector(0, 0, 0) * meter, vector(0, -1, 0), vector(1, 0, 0))
                    });
            skFitSpline(revolveSketch, "generator", {
                        "points" : [vector(0.020, -0.030) * meter, vector(0.031, -0.012) * meter,
                                vector(0.024, 0.008) * meter, vector(0.033, 0.030) * meter]
                    });
            skSolve(revolveSketch);
            opRevolve(context, id + "revolve", {
                        "entities" : qCreatedBy(revolveSketchId, EntityType.EDGE),
                        "axis" : line(vector(0, 0, 0) * meter, vector(0, 0, 1)),
                        "angleForward" : 360 * degree
                    });
            opDeleteBodies(context, id + "deleteRevolveSketch",
                { "entities" : qCreatedBy(revolveSketchId, EntityType.BODY) });

            // Fixture 2: a spline extruded, centred on z = 0 so its box centre - which is the
            // frame's origin - sits at the middle of the ruling rather than at one end.
            const extrudeSketchId = id + "extrudeSketch";
            const extrudeSketch = newSketchOnPlane(context, extrudeSketchId, {
                        "sketchPlane" : plane(vector(0.12, 0, -0.025) * meter, vector(0, 0, 1),
                                vector(1, 0, 0))
                    });
            skFitSpline(extrudeSketch, "crossSection", {
                        "points" : [vector(-0.030, -0.020) * meter, vector(-0.008, 0.014) * meter,
                                vector(0.012, -0.010) * meter, vector(0.030, 0.018) * meter]
                    });
            skSolve(extrudeSketch);
            opExtrude(context, id + "extrude", {
                        "entities" : qCreatedBy(extrudeSketchId, EntityType.EDGE),
                        "direction" : vector(0, 0, 1),
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : 0.05 * meter
                    });
            opDeleteBodies(context, id + "deleteExtrudeSketch",
                { "entities" : qCreatedBy(extrudeSketchId, EntityType.BODY) });
            built = true;
        }
        println("[ANALYTIC PROFILE LIVE TEST] fixtures built: " ~ built);
        tally = checkThat(tally, built, "the two fixtures could not be built.");

        if (built)
        {
            const fixtures = [
                    ["REVOLVED", qCreatedBy(id + "revolve", EntityType.BODY),
                        SweepSurfaceClass.REVOLVED, AnalyticSurfaceKind.REVOLVED, 0, 2 * PI],
                    ["EXTRUDED", qCreatedBy(id + "extrude", EntityType.BODY),
                        SweepSurfaceClass.EXTRUDED, AnalyticSurfaceKind.EXTRUDED, -0.020, 0.020]
                ];
            for (var fixture in fixtures)
            {
                println("[ANALYTIC PROFILE LIVE TEST] === " ~ fixture[0] ~ " ===");
                // A box, because the stage has to survive the throw that ends the try block: it is
                // the one mutable cell FeatureScript has, and without it a catch can only report
                // THAT the fixture died, never where.
                const stage = new box("nothing yet");
                try silent
                {
                    tally = checkProfileFixture(context, nextId, tally, fixture, stage);
                }
                println("[ANALYTIC PROFILE LIVE TEST] " ~ fixture[0] ~ " reached: " ~ stage[]);
                tally = checkThat(tally, stage[] == "finished",
                    fixture[0] ~ " stopped during " ~ stage[] ~ ".");
            }
        }

        reportCheckTally(context, id, "ANALYTIC PROFILE LIVE TEST", tally,
            "a revolved-spline face and an extruded-spline face both classify as their named " ~
            "class, recover their exact generator, reproduce the kernel's own points and normals, " ~
            "and hand back a closed-form contact curve that the kernel's own normals confirm - " ~
            "with no call to evApproximateBSplineSurface on either.");
    });

annotation { "Feature Type Name" : "Sweep Orientation Self Test" }
export const sweepOrientationSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        var checks = 0;

        // The fixture: c = -2 (a solid under a downward parabola, so the surface is convex and
        // its outward normal is the +z-ish one), velocity z linear and negative so the contact
        // ruling u* = w_z / c sits inside [0, 1] for the whole sweep.
        const c = -2;
        const z0 = -0.6;
        const z1 = -0.5;
        const surface = parabolicCylinderSurface(c);
        const motion = translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0],
                [z0, z0 + z1 / 2, z0 + z1]);

        // ---------- The invariant against its closed form ----------
        // f_t = z1 and lambda = z1 + c EVERYWHERE on this funnel, independent of u, v and t.
        const expectedLambda = z1 + c;
        var worstLambda = 0;
        var worstTimeDerivative = 0;
        var worstValue = 0;
        var worstResidual = 0;
        var worstNormal = 0;
        var worstCoordinates = 0;
        for (var stationIndex = 0; stationIndex <= 8; stationIndex += 1)
        {
            const t = stationIndex / 8;
            const u = contactRuling(c, z0, z1, t);
            for (var vIndex = 0; vIndex <= 4; vIndex += 1)
            {
                const v = vIndex / 4;
                const orientation = envelopeOrientationSample(motion, surface, u, v, t);
                worstValue = max(worstValue, abs(orientation.value));
                worstLambda = max(worstLambda, abs(orientation.lambda - expectedLambda));
                worstTimeDerivative = max(worstTimeDerivative, abs(orientation.tDerivative - z1));
                worstResidual = max(worstResidual, orientation.tangentialResidual);
                worstNormal = max(worstNormal, norm(orientation.outwardNormal -
                            normalize(vector(-c * u, 0, 1))));
                // On this fixture the velocity IS S_u exactly, so (alpha, beta) = (1, 0).
                worstCoordinates = max(worstCoordinates,
                    abs(orientation.alpha - 1) + abs(orientation.beta));
            }
        }
        println("[ORIENTATION SELF TEST] on-funnel |f| " ~ worstValue ~ "; velocity out of the " ~
            "tangent plane " ~ worstResidual ~ "; (alpha, beta) vs (1, 0) " ~ worstCoordinates);
        println("[ORIENTATION SELF TEST] lambda vs the closed form " ~ expectedLambda ~ ": " ~ worstLambda ~
            "; f_t vs " ~ z1 ~ ": " ~ worstTimeDerivative);
        println("[ORIENTATION SELF TEST] outward normal vs the transported unit normal: " ~ worstNormal);
        checks += 4;
        if (worstValue > 1e-15)
        {
            failures = failures ~ " the closed-form contact ruling is off the funnel by " ~ worstValue ~ ".";
        }
        if (worstLambda > 1e-14 || worstTimeDerivative > 1e-14)
        {
            failures = failures ~ " lambda / f_t disagree with the closed form by " ~
                max(worstLambda, worstTimeDerivative) ~ ".";
        }
        if (worstResidual > 1e-14 || worstCoordinates > 1e-14)
        {
            failures = failures ~ " the velocity's tangent coordinates are wrong by " ~
                max(worstResidual, worstCoordinates) ~ ".";
        }
        if (worstNormal > 1e-15)
        {
            failures = failures ~ " the outward normal is off by " ~ worstNormal ~ ".";
        }

        // ---------- The chart identity, against finite differences of the chart itself ----------
        // Psi(u, v) = Phi(u, v, t(u)) with t(u) = (c u - z0) / z1 solving f = 0. The identity
        // says Psi_u x Psi_v = (lambda / f_t) A N, and on this fixture that is exactly
        // ((z1 + c) / z1) (-c u, 0, 1) - an independent geometric check of the same two numbers.
        const chartU = 0.42;
        const chartV = 0.55;
        const step = 1e-5;
        const chartNormal = cross(
                (1 / (2 * step)) * (funnelChartPoint(motion, surface, c, z0, z1, chartU + step, chartV) -
                    funnelChartPoint(motion, surface, c, z0, z1, chartU - step, chartV)),
                (1 / (2 * step)) * (funnelChartPoint(motion, surface, c, z0, z1, chartU, chartV + step) -
                    funnelChartPoint(motion, surface, c, z0, z1, chartU, chartV - step)));
        const predictedNormal = (expectedLambda / z1) * vector(-c * chartU, 0, 1);
        const chartError = norm(chartNormal - predictedNormal) / norm(predictedNormal);
        println("[ORIENTATION SELF TEST] chart normal vs (lambda / f_t) A N: relative " ~ chartError ~
            " (chart " ~ chartNormal ~ ", predicted " ~ predictedNormal ~ ")");
        checks += 1;
        if (chartError > 1e-8)
        {
            failures = failures ~ " the (u, v) chart normal disagrees with (lambda / f_t) A N by " ~
                chartError ~ ".";
        }

        // ---------- The patch verdict, and that the q direction is what flips it ----------
        const forwardQ = fitPatchOrientationAt(motion, surface, contactRuling(c, z0, z1, 0.5), 0.5, 0.5, [0, 1]);
        const backwardQ = fitPatchOrientationAt(motion, surface, contactRuling(c, z0, z1, 0.5), 0.5, 0.5, [0, -1]);
        println("[ORIENTATION SELF TEST] lambda sign " ~ forwardQ.lambdaSign ~ ", f_t sign " ~
            forwardQ.timeDerivativeSign ~ ", chart sign " ~ forwardQ.chartSign ~
            "; q along +v: kappa " ~ forwardQ.kappaSign ~ " faces outward " ~ forwardQ.facesOutward ~
            "; q along -v: kappa " ~ backwardQ.kappaSign ~ " faces outward " ~ backwardQ.facesOutward);
        checks += 1;
        if (forwardQ.lambdaSign != -1 || forwardQ.timeDerivativeSign != -1 || forwardQ.chartSign != 1)
        {
            failures = failures ~ " the fixture's signs came out (" ~ forwardQ.lambdaSign ~ ", " ~
                forwardQ.timeDerivativeSign ~ ", " ~ forwardQ.chartSign ~ ") instead of (-1, -1, 1).";
        }
        if (!(forwardQ.kappaSign == 1 && forwardQ.facesOutward) ||
            !(backwardQ.kappaSign == -1 && !backwardQ.facesOutward))
        {
            failures = failures ~ " reversing q did not reverse the patch verdict.";
        }

        // ---------- The grid certificate, and its finite-difference cross-check ----------
        const grid = buildFunnelFitGrid(motion, surface, c, z0, z1, 9, 7, false);
        const certificate = certifyFitGridOrientation(motion, surface, grid.uvRows, grid.stations,
                { "liftedGrid" : grid.liftedGrid });
        println("[ORIENTATION SELF TEST] grid certificate: " ~ certificate.sampleCount ~ " samples, " ~
            "lambda sign " ~ certificate.lambdaSign ~ " consistent " ~ certificate.lambdaSignConsistent ~
            ", faces outward " ~ certificate.facesOutward ~ " unanimous " ~ certificate.verdictUnanimous ~
            ", worst fold margin " ~ certificate.worstFoldMargin ~ ", difference check " ~
            certificate.differenceAgreements ~ "/" ~ certificate.differenceChecked ~
            " agree, worst alignment " ~ certificate.worstDifferenceAlignment);
        checks += 2;
        if (!certificate.consistent || !certificate.facesOutward || certificate.lambdaSign != -1)
        {
            failures = failures ~ " the grid certificate did not come back consistent and outward.";
        }
        if (certificate.differenceChecked == 0 || certificate.differenceDisagreements != 0)
        {
            failures = failures ~ " the finite-difference cross-check disagreed with the analytic " ~
                "verdict at " ~ certificate.differenceDisagreements ~ " of " ~
                certificate.differenceChecked ~ " samples.";
        }

        const reversedGrid = buildFunnelFitGrid(motion, surface, c, z0, z1, 9, 7, true);
        const reversedCertificate = certifyFitGridOrientation(motion, surface, reversedGrid.uvRows,
                reversedGrid.stations, { "liftedGrid" : reversedGrid.liftedGrid });
        println("[ORIENTATION SELF TEST] the same grid with q reversed: faces outward " ~
            reversedCertificate.facesOutward ~ ", unanimous " ~ reversedCertificate.verdictUnanimous ~
            ", difference check " ~ reversedCertificate.differenceAgreements ~ "/" ~
            reversedCertificate.differenceChecked ~ " agree");
        checks += 1;
        if (reversedCertificate.facesOutward || !reversedCertificate.verdictUnanimous ||
            reversedCertificate.differenceDisagreements != 0)
        {
            failures = failures ~ " reversing the grid's q direction did not flip the certificate " ~
                "while keeping the two routes in agreement.";
        }

        // ---------- Cap classification ----------
        // At t = 0 the fixture's f is w_z(0) - c u = -0.6 + 2 u, so the ingress cap keeps
        // u < 0.3 and the egress cap keeps u > 0.3. Sampled either side of the ruling.
        const ingressInside = classifyCapSample(motion, surface, 0.1, 0.5, 0, true, 0);
        const ingressOutside = classifyCapSample(motion, surface, 0.5, 0.5, 0, true, 0);
        const egressInside = classifyCapSample(motion, surface, 0.5, 0.5, 0, false, 0);
        println("[ORIENTATION SELF TEST] cap classification at t = 0: f(0.1) " ~ ingressInside.value ~
            " ingress keeps " ~ ingressInside.keep ~ "; f(0.5) " ~ ingressOutside.value ~
            " ingress keeps " ~ ingressOutside.keep ~ ", egress keeps " ~ egressInside.keep);
        checks += 1;
        if (!ingressInside.keep || ingressOutside.keep || !egressInside.keep ||
            abs(ingressInside.value - (z0 - c * 0.1)) > 1e-15)
        {
            failures = failures ~ " cap classification disagreed with the sign of f.";
        }

        // ---------- Exact net reversal ----------
        const reversalError = checkSurfaceReversal(rationalTestSurface());
        println("[ORIENTATION SELF TEST] rational net reversed: worst point mismatch at mirrored " ~
            "parameters " ~ reversalError.pointError ~ ", worst normal-flip mismatch " ~
            reversalError.normalError);
        checks += 2;
        if (reversalError.pointError > 1e-15 || reversalError.normalError > 1e-14)
        {
            failures = failures ~ " reversing a fit net's q direction was not exact (" ~
                reversalError.pointError ~ " / " ~ reversalError.normalError ~ ").";
        }

        // ---------- The row reversal helpers ----------
        const clampedRow = reverseFitGridRow([10, 11, 12, 13, 14], false);
        const closedRow = reverseFitGridRow([10, 11, 12, 13, 14], true);
        const midRow = reverseFitGridMidRow([20, 21, 22, 23]);
        println("[ORIENTATION SELF TEST] row reversal: clamped " ~ clampedRow ~ ", closed " ~
            closedRow ~ ", midpoints " ~ midRow);
        checks += 1;
        if (clampedRow != [14, 13, 12, 11, 10] || closedRow != [10, 14, 13, 12, 11] ||
            midRow != [23, 22, 21, 20])
        {
            failures = failures ~ " a grid row reversal helper returned the wrong permutation.";
        }

        reportTestVerdict(context, id, "ORIENTATION SELF TEST", checks, failures,
            ("lambda, f_t and the outward normal match the parabolic " ~
            "cylinder's closed forms, the (u, v) chart normal is (lambda / f_t) A N by finite " ~
            "difference, the q direction is what flips the patch verdict, the grid certificate " ~
            "agrees with its own finite differences both ways round, cap classification follows " ~
            "the sign of f, and net reversal is exact."));
    });

annotation { "Feature Type Name" : "Sweep Orientation Sharp Feature Self Test" }
export const sweepOrientationSharpSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        var checks = 0;

        // ---------- Strip alternation on a co-edge with two roots per column ----------
        // Same parabolic cylinder, but the velocity's z component is now the QUADRATIC
        // w_z(t) = -2.1 + 8.8 t (1 - t). Along the co-edge v = 0 the strip function is
        // g(s, t) = 2 s + w_z(t), so each column has exactly the two roots
        // t = 0.5 -/+ 0.5 sqrt((0.4 + 8 s) / 8.8), and g_t = w_z'(t) = 8.8 (1 - 2 t) is positive
        // at the first and negative at the second. That is the alternation rule, in closed form.
        const c = -2;
        const stripMotion = translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0], [-2.1, 2.3, -2.1]);
        const columnCount = 21;
        var sampleParameters = makeArray(columnCount, 0);
        var normals = makeArray(columnCount);
        var points = makeArray(columnCount);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            const s = columnIndex / (columnCount - 1);
            sampleParameters[columnIndex] = s;
            normals[columnIndex] = vector(-c * s, 0, 1);
            points[columnIndex] = vector(s, 0, 0.5 * c * s * s);
        }
        const branches = marchStripZeroCurves(stripMotion, normals, points, 0, 1, 41, 1e-13);
        var worstRootError = 0;
        for (var branch in branches)
        {
            for (var sample in branch.samples)
            {
                const s = sampleParameters[sample.sampleIndex];
                const offset = 0.5 * sqrt((0.4 + 8 * s) / 8.8);
                worstRootError = max(worstRootError, min(abs(sample.t - (0.5 - offset)),
                            abs(sample.t - (0.5 + offset))));
            }
        }
        const oriented = orientStripBranches(stripMotion, normals, points, branches, {});
        var signs = [];
        for (var branchResult in oriented.branches)
        {
            signs = append(signs, branchResult.timeDerivativeSign);
        }
        println("[ORIENTATION SHARP SELF TEST] strip: " ~ size(branches) ~ " branches, worst root " ~
            "error vs the closed form " ~ worstRootError ~ ", branch f_t signs " ~ signs ~
            ", alternation " ~ oriented.alternationChecked ~ " pairs checked, " ~
            size(oriented.alternationViolations) ~ " violations, " ~ oriented.tangencyCount ~
            " tangencies");
        checks += 3;
        if (size(branches) != 2)
        {
            failures = failures ~ " the strip produced " ~ size(branches) ~ " branches, not 2.";
        }
        if (worstRootError > 1e-11)
        {
            failures = failures ~ " strip roots are off the closed form by " ~ worstRootError ~ ".";
        }
        if (!oriented.alternationConsistent || oriented.alternationChecked != columnCount)
        {
            failures = failures ~ " the alternation certificate failed (" ~ oriented.alternationChecked ~
                " pairs, " ~ size(oriented.alternationViolations) ~ " violations).";
        }
        if (size(signs) == 2 && signs[0] * signs[1] != -1)
        {
            failures = failures ~ " the two branches did not come out with opposite f_t signs.";
        }

        // Both branches pass alternation, and their lambda signs are OPPOSITE - the mid-column
        // sample of each. That is the whole reason orientation needs lambda as well as f_t: the
        // alternation rule says only that f_t flips between neighboring roots, and here one
        // sheet is the real envelope while the other is occluded (its contact point runs
        // backwards, lambda > 0 against the other's lambda < 0).
        const stripSurface = parabolicCylinderSurface(c);
        var lambdaSigns = [];
        for (var branch in branches)
        {
            const midSample = branch.samples[floor(size(branch.samples) / 2)];
            const orientation = envelopeOrientationSample(stripMotion, stripSurface,
                sampleParameters[midSample.sampleIndex], 0, midSample.t);
            lambdaSigns = append(lambdaSigns, orientation.lambdaSign);
        }
        println("[ORIENTATION SHARP SELF TEST] the two sheets' lambda signs at mid column: " ~ lambdaSigns);
        checks += 1;
        if (size(lambdaSigns) == 2 && lambdaSigns[0] * lambdaSigns[1] != -1)
        {
            failures = failures ~ " the two sheets did not land on opposite sides of lambda = 0.";
        }

        // ---------- Co-edge sense and direction bookkeeping ----------
        // The left face's co-edge runs +s and the right face's runs -s, and an envelope co-edge
        // follows sign(lambda / f_t) of that. With lambda fixed on a component, the two branches
        // above generate co-edges running OPPOSITE ways - which is what the alternation rule
        // buys, stated as topology rather than as a sign.
        const leftSense = coEdgeSense("left");
        const rightSense = coEdgeSense("right");
        const alongPositive = envelopeCoEdgeDirection(leftSense, -1, -1);
        const alongNegative = envelopeCoEdgeDirection(leftSense, -1, 1);
        println("[ORIENTATION SHARP SELF TEST] senses left " ~ leftSense ~ " right " ~ rightSense ~
            "; envelope directions for the two f_t signs " ~ alongPositive ~ " / " ~ alongNegative ~
            "; partner of the first " ~ partnerCoEdgeDirection(alongPositive));
        checks += 1;
        if (leftSense != 1 || rightSense != -1 || alongPositive != 1 || alongNegative != -1 ||
            partnerCoEdgeDirection(alongPositive) != -1)
        {
            failures = failures ~ " the co-edge direction bookkeeping is wrong.";
        }

        // ---------- Sharp-edge face orientation ----------
        // A convex ridge along +x whose two side normals sit at +/-45 degrees about +z, under a
        // translation in +y. The two side contact functions differ in sign, so the edge grazes;
        // the sheet's parametric normal (A e') x velocity is then +z, squarely inside the cone.
        const ridgeMotion = translationMotionFromQuadraticVelocity([0, 0, 0], [1, 1, 1], [0, 0, 0]);
        const leftNormal = normalize(vector(0, -1, 1));
        const rightNormal = normalize(vector(0, 1, 1));
        const ridge = orientSharpEdgeFace(ridgeMotion, vector(0, 0, 0), vector(1, 0, 0),
                leftNormal, rightNormal, 0.5);
        const flippedRidge = orientSharpEdgeFace(ridgeMotion, vector(0, 0, 0), vector(-1, 0, 0),
                leftNormal, rightNormal, 0.5);
        println("[ORIENTATION SHARP SELF TEST] ridge along +x: inside cone " ~ ridge.insideCone ~
            ", flip " ~ ridge.flipRequired ~ ", outward " ~ ridge.outwardNormal ~ ", margin " ~
            ridge.coneMargin ~ "; the same ridge parameterized backwards: flip " ~
            flippedRidge.flipRequired ~ ", outward " ~ flippedRidge.outwardNormal);
        checks += 2;
        if (!ridge.insideCone || ridge.flipRequired ||
            norm(ridge.outwardNormal - vector(0, 0, 1)) > 1e-15)
        {
            failures = failures ~ " the sharp-edge sheet's outward normal came out wrong.";
        }
        if (!flippedRidge.insideCone || !flippedRidge.flipRequired ||
            norm(flippedRidge.outwardNormal - vector(0, 0, 1)) > 1e-15)
        {
            failures = failures ~ " reversing the edge parameterization did not flip the sheet " ~
                "while keeping the outward normal.";
        }

        // The same ridge under a translation along -z grazes nowhere: both side contact
        // functions are negative, and neither candidate normal lies in the cone.
        const plungeMotion = translationMotionFromQuadraticVelocity([0, 0, 0], [0, 0, 0], [-1, -1, -1]);
        const plunge = orientSharpEdgeFace(plungeMotion, vector(0, 0, 0), vector(1, 0, 0),
                leftNormal, rightNormal, 0.5);
        println("[ORIENTATION SHARP SELF TEST] the same ridge plunging along -z: inside cone " ~
            plunge.insideCone ~ ", degenerate " ~ plunge.degenerate);
        checks += 1;
        if (plunge.insideCone || !plunge.degenerate)
        {
            failures = failures ~ " a non-grazing sharp edge was not reported as outside the cone.";
        }

        // ---------- Sharp-vertex co-edge orientation ----------
        // The ridge's start vertex at the origin, its edge running toward +x, the face's outward
        // normal +z, the vertex trajectory running +y. The face has to be on the co-edge's left,
        // which puts the co-edge on -y: n x w must point toward +x, and (0,0,1) x (0,-1,0) does.
        const vertexCoEdge = orientSharpVertexCoEdge(ridgeMotion, vector(0, 0, 0), vector(1, 0, 0),
                vector(0, 0, 1), 0.5);
        println("[ORIENTATION SHARP SELF TEST] sharp-vertex co-edge: direction " ~
            vertexCoEdge.direction ~ ", tangent " ~ vertexCoEdge.tangent ~ ", test " ~
            vertexCoEdge.testValue);
        checks += 1;
        if (vertexCoEdge.direction != -1 || norm(vertexCoEdge.tangent - vector(0, -1, 0)) > 1e-15 ||
            norm(cross(vector(0, 0, 1), vertexCoEdge.tangent) - vector(1, 0, 0)) > 1e-15)
        {
            failures = failures ~ " the sharp-vertex co-edge was oriented the wrong way.";
        }

        // The far vertex of the same edge takes -e'(s1) as its interior direction, so its
        // co-edge runs the other way - which is exactly what closes the loop around the face.
        const farVertex = orientSharpVertexCoEdge(ridgeMotion, vector(0, 0, 0), vector(-1, 0, 0),
                vector(0, 0, 1), 0.5);
        println("[ORIENTATION SHARP SELF TEST] the edge's far vertex: direction " ~ farVertex.direction);
        checks += 1;
        if (farVertex.direction != 1)
        {
            failures = failures ~ " the far vertex's co-edge did not reverse.";
        }

        reportTestVerdict(context, id, "ORIENTATION SHARP SELF TEST", checks, failures,
            ("the two strip roots per column alternate in f_t exactly " ~
            "as Rolle requires and land on opposite sides of lambda = 0, the co-edge direction " ~
            "bookkeeping follows sign(lambda / f_t) times the input sense, a convex ridge's " ~
            "sheet normal is cone-selected and flips with the edge parameterization, a " ~
            "non-grazing ridge is reported outside the cone, and the two ends of one sharp " ~
            "edge orient their vertex co-edges oppositely."));
    });

/**
 * The two things the rectangle path cannot reach, on a funnel with genuinely CLOSED section
 * loops: the closed-row reversal (which holds q's origin fixed instead of reversing outright),
 * collapsed pole rows, and the lambda-sign fold certificate actually FIRING.
 *
 * Fixture is the funnel solver's island bump, z_u = 0.8 u(1-u) v(1-v) under velocity
 * (1, 0, w_z(t)) with w_z = 0.134 - 0.4t + 0.4t^2. Its level sets are closed loops around
 * (0.5, 0.5), born at t = 0.3 and dying at t = 0.7 where w_z meets the bump's peak of 0.05.
 * Here alpha = 1 and beta = 0 again, so
 *
 *     f = w_z - z_u,   f_u = -z_uu,   f_t = w_z',   lambda = w_z' + z_uu
 *
 * and z_uu = 0.8(1 - 2u) v(1 - v) CHANGES SIGN at u = 0.5 - which the loop encircles. On the
 * loop max |z_uu| = 0.2 sqrt(1 - 20 w_z), attained at the v = 0.5 extremes, so lambda keeps one
 * sign exactly where |w_z'| > 0.2 sqrt(1 - 20 w_z). That splits the fixture in two:
 *
 *   - t in [0.30, 0.36] (just after birth): lambda one sign, worst margin 0.031 - an
 *     orientable component, and the clean closed-row test.
 *   - t in [0.44, 0.60] (the middle band): lambda spans both signs at EVERY station - the
 *     envelope folds, and the certificate has to say so.
 *
 * That second half is the point. It means this bump fixture is a LOCALLY SELF-INTERSECTING
 * sweep through its middle, which v1 must reject (spec 3, spec 10 detector 1) - and it is the
 * only live exercise the fold certificate has.
 */
annotation { "Feature Type Name" : "Sweep Orientation Grid Self Test" }
export const sweepOrientationGridSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        var checks = 0;

        const surface = islandBumpSurface();
        const motion = translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0], [0.134, -0.066, 0.134]);
        const qCount = 8;

        // ---------- The orientable window, with a collapsed birth pole ----------
        const simpleGrid = buildLoopGrid(motion, surface, [0.30, 0.315, 0.33, 0.345, 0.36], qCount);
        const simple = certifyFitGridOrientation(motion, surface, simpleGrid.uvRows,
                simpleGrid.stations, { "liftedGrid" : simpleGrid.liftedGrid });
        const expectedSamples = (size(simpleGrid.stations) - 1) * qCount;
        println("[ORIENTATION GRID SELF TEST] orientable window: " ~ simple.sampleCount ~ " samples " ~
            "(pole row skipped, expected " ~ expectedSamples ~ "), lambda sign " ~ simple.lambdaSign ~
            " consistent " ~ simple.lambdaSignConsistent ~ ", faces outward " ~ simple.facesOutward ~
            " unanimous " ~ simple.verdictUnanimous ~ ", worst fold margin " ~ simple.worstFoldMargin ~
            ", difference " ~ simple.differenceAgreements ~ "/" ~ simple.differenceChecked ~
            " agree, stationary " ~ simple.stationarySamples ~ ", consistent " ~ simple.consistent);
        checks += 3;
        if (simple.sampleCount != expectedSamples)
        {
            failures = failures ~ " the collapsed pole row was not skipped (" ~ simple.sampleCount ~
                " samples against " ~ expectedSamples ~ ").";
        }
        if (!simple.consistent || simple.lambdaSign != -1 || !simple.verdictUnanimous)
        {
            failures = failures ~ " the orientable window did not certify (lambda sign " ~
                simple.lambdaSign ~ ", consistent " ~ simple.consistent ~ ").";
        }
        if (simple.differenceChecked == 0 || simple.differenceDisagreements != 0)
        {
            failures = failures ~ " the finite-difference route disagreed on " ~
                simple.differenceDisagreements ~ " of " ~ simple.differenceChecked ~ " closed-row samples.";
        }

        // ---------- The same window with every closed row reversed ----------
        // A closed row reverses by holding sample 0 and reversing the rest, so q's origin does
        // not move. The verdict must flip and the two routes must still agree.
        // EVERY row, pole included. Reversing a collapsed row is a no-op, and reversing only
        // some of them would leave the grid inconsistently ordered between rows - which is
        // precisely what made this check fail the first time it ran.
        var reversedUv = simpleGrid.uvRows;
        var reversedLifted = simpleGrid.liftedGrid;
        for (var rowIndex = 0; rowIndex < size(simpleGrid.stations); rowIndex += 1)
        {
            reversedUv[rowIndex] = reverseFitGridRow(simpleGrid.uvRows[rowIndex], true);
            reversedLifted[rowIndex] = reverseFitGridRow(simpleGrid.liftedGrid[rowIndex], true);
        }
        const reversed = certifyFitGridOrientation(motion, surface, reversedUv, simpleGrid.stations,
                { "liftedGrid" : reversedLifted });
        const originHeld = squaredNorm(reversedLifted[1][0] - simpleGrid.liftedGrid[1][0]) < 1e-28;
        println("[ORIENTATION GRID SELF TEST] closed rows reversed: faces outward " ~
            reversed.facesOutward ~ " (against " ~ simple.facesOutward ~ "), unanimous " ~
            reversed.verdictUnanimous ~ ", difference " ~ reversed.differenceAgreements ~ "/" ~
            reversed.differenceChecked ~ " agree, consistent " ~ reversed.consistent ~
            ", q origin held " ~ originHeld);
        checks += 3;
        if (reversed.facesOutward == simple.facesOutward)
        {
            failures = failures ~ " reversing the closed rows did not flip the verdict.";
        }
        if (!reversed.consistent || reversed.differenceDisagreements != 0)
        {
            failures = failures ~ " the reversed closed-row grid did not certify (" ~
                reversed.differenceDisagreements ~ " disagreements).";
        }
        if (!originHeld)
        {
            failures = failures ~ " the closed-row reversal moved q's origin.";
        }

        // ---------- The fold window: the certificate must FIRE ----------
        const foldGrid = buildLoopGrid(motion, surface, [0.44, 0.48, 0.52, 0.56, 0.60], qCount);
        const fold = certifyFitGridOrientation(motion, surface, foldGrid.uvRows,
                foldGrid.stations, { "liftedGrid" : foldGrid.liftedGrid });
        println("[ORIENTATION GRID SELF TEST] fold window: " ~ fold.sampleCount ~ " samples, " ~
            "lambda sign consistent " ~ fold.lambdaSignConsistent ~ ", worst fold margin " ~
            fold.worstFoldMargin ~ ", faces outward " ~ fold.facesOutward ~ " unanimous " ~
            fold.verdictUnanimous ~ ", consistent " ~ fold.consistent);
        checks += 2;
        if (fold.lambdaSignConsistent)
        {
            failures = failures ~ " the fold certificate did NOT fire on a component whose " ~
                "lambda provably spans both signs at every station.";
        }
        if (fold.consistent)
        {
            failures = failures ~ " a folded component was reported consistent.";
        }

        reportTestVerdict(context, id, "ORIENTATION GRID SELF TEST", checks, failures,
            ("a collapsed pole row is skipped, a closed-row grid " ~
            "certifies with its finite-difference cross-check agreeing, reversing the closed " ~
            "rows flips the verdict while holding q's origin, and the lambda-sign fold " ~
            "certificate fires on the bump fixture's middle band - which is therefore a " ~
            "locally self-intersecting sweep, not a valid one."));
    });

annotation { "Feature Type Name" : "Sweep Section Tangency Self Test" }
export const sweepSectionTangencySelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = tangencyFixtureSurface();
        const station = 0.5;
        const marchOptions = { "stepSize" : 0.05, "maxSteps" : 400, "tolerance" : 1e-12 };

        // ---------- The fixture's own algebra, before any detector runs ----------
        // If f is not 1/2 - u and f_t is not c - (v - 1/2)^2 / 2 then every verdict below is
        // about a different surface than the one this test reasons about.
        const algebraOffset = 0.03;
        const algebraMotion = tangencyFixtureMotion(algebraOffset);
        var worstValue = 0;
        var worstTimeDerivative = 0;
        for (var uIndex = 0; uIndex <= 4; uIndex += 1)
        {
            for (var vIndex = 0; vIndex <= 4; vIndex += 1)
            {
                const u = uIndex / 4;
                const v = vIndex / 4;
                worstValue = max(worstValue, abs(evaluateEnvelopePointwise(algebraMotion, surface,
                                u, v, station) - (0.5 - u)));
                worstTimeDerivative = max(worstTimeDerivative,
                    abs(evaluateEnvelopeTimeDerivativePointwise(algebraMotion, surface, u, v, station) -
                        (algebraOffset - 0.5 * (v - 0.5) ^ 2)));
            }
        }
        println("[SECTION TANGENCY] fixture algebra at t = 0.5: worst |f - (1/2 - u)| " ~ worstValue ~
            ", worst |f_t - (c - (v-1/2)^2/2)| " ~ worstTimeDerivative);
        tally = checkWithin(tally, worstValue, 1e-15, "f against its closed form");
        tally = checkWithin(tally, worstTimeDerivative, 1e-15, "f_t against its closed form");

        // ---------- Case A: two interior tangencies; the section must split in three ----------
        const marched = marchSectionCurve(algebraMotion, surface, station, [0.5, 0], [0.5, 1], marchOptions);
        tally = checkThat(tally, marched.reachedEnd,
            "the section march did not reach the end anchor, so the audit ran on a partial section.");
        const audit = auditSectionTangency(algebraMotion, surface, station, marched.uvPoints,
                { "nearTangencyTolerance" : 0.05 });
        const expectedV = sqrt(2 * algebraOffset);
        println("[SECTION TANGENCY] c = +0.03: " ~ size(marched.uvPoints) ~ " marched points, " ~
            size(audit.tangencies) ~ " tangencies, " ~ size(audit.nearTangencies) ~
            " near tangencies, stationary " ~ audit.stationarySection ~ ", scale " ~
            audit.scaleReference ~ ", value scale " ~ audit.valueScaleReference ~ ", floor " ~
            audit.stationaryFloor ~ ", worst section |f| " ~ audit.worstSectionResidual);
        tally = checkThat(tally, audit.detected && !audit.stationarySection,
            "the two-tangency case reported detected " ~ audit.detected ~ " stationary " ~
            audit.stationarySection ~ ".");
        tally = checkThat(tally, size(audit.tangencies) == 2,
            "the two-tangency case found " ~ size(audit.tangencies) ~ " tangencies, not 2.");
        // The samples flanking each tangency dip to a |f_t| of 0.00125 - 0.1% of the scale, well
        // inside the near-tangency threshold - so this also checks that one event is not counted
        // twice, once as a tangency and again as the near tangency beside it.
        tally = checkThat(tally, size(audit.nearTangencies) == 0,
            "the two-tangency case also reported " ~ size(audit.nearTangencies) ~
            " near tangencies, double-counting its own tangencies.");
        if (size(audit.tangencies) == 2)
        {
            var worstTangencyU = 0;
            var worstTangencyV = 0;
            var worstTangencyDerivative = 0;
            var worstTangencyResidual = 0;
            for (var index = 0; index < 2; index += 1)
            {
                const tangency = audit.tangencies[index];
                const predictedV = index == 0 ? 0.5 - expectedV : 0.5 + expectedV;
                worstTangencyU = max(worstTangencyU, abs(tangency.uv[0] - 0.5));
                worstTangencyV = max(worstTangencyV, abs(tangency.uv[1] - predictedV));
                worstTangencyDerivative = max(worstTangencyDerivative, abs(tangency.timeDerivative));
                worstTangencyResidual = max(worstTangencyResidual, tangency.sectionResidual);
            }
            println("[SECTION TANGENCY] tangencies at v = " ~ audit.tangencies[0].uv[1] ~ " and " ~
                audit.tangencies[1].uv[1] ~ " against 1/2 -/+ " ~ expectedV ~ ": worst dv " ~
                worstTangencyV ~ ", worst du " ~ worstTangencyU ~ ", worst |f_t| " ~
                worstTangencyDerivative ~ ", worst |f| " ~ worstTangencyResidual);
            tally = checkWithin(tally, worstTangencyV, 1e-11, "the refined tangency v");
            tally = checkWithin(tally, worstTangencyU, 1e-14, "the refined tangency u");
            tally = checkWithin(tally, worstTangencyDerivative, 1e-12, "|f_t| at the refined tangency");
            tally = checkWithin(tally, worstTangencyResidual, 1e-12, "|f| at the refined tangency");
        }

        const split = splitSectionAtTangencies(marched.uvPoints, audit);
        println("[SECTION TANGENCY] split: " ~ size(split.pieces) ~ " pieces (" ~
            pieceSizes(split.pieces) ~ "), splitCount " ~ split.splitCount ~ ", dropped " ~
            split.droppedPieces);
        tally = checkThat(tally, size(split.pieces) == 3 && split.droppedPieces == 0,
            "the split produced " ~ size(split.pieces) ~ " pieces and dropped " ~
            split.droppedPieces ~ ", not 3 and 0.");
        if (size(split.pieces) == 3)
        {
            // Spec 2.3: the seam is ONE value, so exact equality is the right test - a
            // tolerance here would pass a split that merely agreed to tolerance.
            var seamsShared = true;
            for (var pieceIndex = 0; pieceIndex + 1 < 3; pieceIndex += 1)
            {
                const tail = split.pieces[pieceIndex][size(split.pieces[pieceIndex]) - 1];
                const head = split.pieces[pieceIndex + 1][0];
                seamsShared = seamsShared && tail[0] == head[0] && tail[1] == head[1];
            }
            tally = checkThat(tally, seamsShared,
                "adjacent pieces do not share their split point exactly.");

            // The point of splitting: no piece's interior crosses f_t = 0.
            var interiorSignChanges = 0;
            for (var pieceIndex = 0; pieceIndex < 3; pieceIndex += 1)
            {
                const piece = split.pieces[pieceIndex];
                var previousSign = 0;
                for (var pointIndex = 1; pointIndex + 1 < size(piece); pointIndex += 1)
                {
                    const derivative = evaluateEnvelopeTimeDerivativePointwise(algebraMotion, surface,
                            piece[pointIndex][0], piece[pointIndex][1], station);
                    const currentSign = derivative > 0 ? 1 : -1;
                    if (previousSign != 0 && currentSign != previousSign)
                    {
                        interiorSignChanges += 1;
                    }
                    previousSign = currentSign;
                }
            }
            println("[SECTION TANGENCY] f_t sign changes inside the three pieces: " ~ interiorSignChanges);
            tally = checkThat(tally, interiorSignChanges == 0,
                "a piece's interior still crosses f_t = 0 " ~ interiorSignChanges ~ " times.");

            const resampled = resampleSectionPieces(algebraMotion, surface, station, split.pieces, 7, 1e-12);
            println("[SECTION TANGENCY] resampled 3 pieces at 7 samples: worst |f| " ~ resampled.worstResidual);
            tally = checkWithin(tally, resampled.worstResidual, 1e-12,
                "the worst |f| over the resampled pieces");
        }

        // ---------- Case B: a near tangency, reported and NOT split ----------
        const nearMotion = tangencyFixtureMotion(-0.03);
        const nearMarched = marchSectionCurve(nearMotion, surface, station, [0.5, 0], [0.5, 1], marchOptions);
        const nearAudit = auditSectionTangency(nearMotion, surface, station, nearMarched.uvPoints,
                { "nearTangencyTolerance" : 0.05 });
        const nearSplit = splitSectionAtTangencies(nearMarched.uvPoints, nearAudit);
        println("[SECTION TANGENCY] c = -0.03: " ~ size(nearAudit.tangencies) ~ " tangencies, " ~
            size(nearAudit.nearTangencies) ~ " near tangencies, minimum |f_t| " ~
            nearAudit.minimumMagnitude ~ " (relative " ~ nearAudit.minimumRelative ~ "), pieces " ~
            size(nearSplit.pieces));
        tally = checkThat(tally, !nearAudit.detected && !nearAudit.stationarySection &&
                size(nearAudit.tangencies) == 0,
            "the near-tangency case reported " ~ size(nearAudit.tangencies) ~ " tangencies.");
        tally = checkThat(tally, size(nearAudit.nearTangencies) == 1,
            "the near-tangency case reported " ~ size(nearAudit.nearTangencies) ~
            " near tangencies, not 1.");
        tally = checkWithin(tally, nearAudit.minimumMagnitude - 0.03, 1e-14,
            "the near tangency's own |f_t| against the closed-form 0.03");
        if (size(nearAudit.nearTangencies) == 1)
        {
            tally = checkWithin(tally, nearAudit.nearTangencies[0].uv[1] - 0.5, 1e-12,
                "the near tangency's v against the closed-form 1/2");
        }
        tally = checkThat(tally, size(nearSplit.pieces) == 1 && nearSplit.splitCount == 0,
            "a near tangency split the section into " ~ size(nearSplit.pieces) ~ " pieces.");

        // ---------- Case C: silent ----------
        const quietMotion = tangencyFixtureMotion(-0.3);
        const quietMarched = marchSectionCurve(quietMotion, surface, station, [0.5, 0], [0.5, 1], marchOptions);
        const quietAudit = auditSectionTangency(quietMotion, surface, station, quietMarched.uvPoints,
                { "nearTangencyTolerance" : 0.05 });
        println("[SECTION TANGENCY] c = -0.30: " ~ size(quietAudit.tangencies) ~ " tangencies, " ~
            size(quietAudit.nearTangencies) ~ " near tangencies, minimum |f_t| " ~
            quietAudit.minimumMagnitude ~ " (relative " ~ quietAudit.minimumRelative ~ ")");
        tally = checkThat(tally, !quietAudit.detected && !quietAudit.stationarySection &&
                size(quietAudit.tangencies) == 0 && size(quietAudit.nearTangencies) == 0,
            "the silent case reported " ~ size(quietAudit.tangencies) ~ " tangencies and " ~
            size(quietAudit.nearTangencies) ~ " near tangencies.");
        tally = checkWithin(tally, quietAudit.minimumMagnitude - 0.3, 1e-14,
            "the silent case's minimum |f_t| against the closed-form 0.3");

        // ---------- Case D: the funnel solver's own section fixture, constant velocity ----------
        // f_t is identically zero here and so is its own scale, which is the whole reason the
        // audit carries a second reference. The verdict must be stationary, never a tangency.
        const slantSurface = slantFixtureSurface();
        const slantMotion = constantVelocityTranslationMotion(vector(1, 0, 0.06));
        const slantMarched = marchSectionCurve(slantMotion, slantSurface, station, [0.8, 0],
                [(0.24 - 0.05) / 0.3, 1], { "stepSize" : 0.05, "maxSteps" : 400, "tolerance" : 1e-12 });
        const slantAudit = auditSectionTangency(slantMotion, slantSurface, station, slantMarched.uvPoints,
                { "nearTangencyTolerance" : 0.05 });
        const slantSplit = splitSectionAtTangencies(slantMarched.uvPoints, slantAudit);
        println("[SECTION TANGENCY] slant fixture, constant velocity: stationary " ~
            slantAudit.stationarySection ~ ", |f_t| scale " ~ slantAudit.scaleReference ~
            ", |f| scale " ~ slantAudit.valueScaleReference ~ ", floor " ~ slantAudit.stationaryFloor ~
            ", largest |f_t| " ~ largestOf(slantAudit.sampleTimeDerivatives) ~ ", pieces " ~
            size(slantSplit.pieces));
        tally = checkThat(tally, slantAudit.stationarySection && !slantAudit.detected &&
                size(slantAudit.tangencies) == 0,
            "the constant-velocity section came out stationary " ~ slantAudit.stationarySection ~
            " with " ~ size(slantAudit.tangencies) ~ " tangencies.");
        tally = checkThat(tally, slantAudit.valueScaleReference > 0.5,
            "the |f| reference on the slant fixture is " ~ slantAudit.valueScaleReference ~
            ", too small to carry the stationary test.");
        tally = checkThat(tally, slantAudit.scaleReference < 1e-14,
            "the constant-velocity fixture's own f_t scale is " ~ slantAudit.scaleReference ~
            ", so it is not the degenerate reference this case is meant to exercise.");
        tally = checkThat(tally, size(slantSplit.pieces) == 1,
            "a stationary section was split into " ~ size(slantSplit.pieces) ~ " pieces.");

        reportCheckTally(context, id, "SECTION TANGENCY", tally,
            "detector 2 fires on two closed-form tangencies, splits the section into three " ~
            "pieces sharing their seams exactly, reports the near tangency without splitting, " ~
            "and stays silent on both the far case and the constant-velocity section.");
    });

annotation { "Feature Type Name" : "Sweep Edge Singularity Self Test" }
export const sweepEdgeSingularitySelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const curvature = 1.2;
        const epsilon = 0.35;
        const singularT = 0.5;
        const edgeCurve = singularEdgeCurve(curvature);
        const gridCount = 12;

        // The (s, t) grid deliberately misses t* = 1/2: with 12 nodes at i/11 the nearest are
        // 5/11 and 6/11, so nothing here can succeed by landing on the answer.
        var sampleParameters = makeArray(gridCount);
        var edgePoints = makeArray(gridCount);
        var edgeTangents = makeArray(gridCount);
        var tValues = makeArray(gridCount);
        for (var index = 0; index < gridCount; index += 1)
        {
            const s = index / (gridCount - 1);
            const derivatives = evaluateBSplineCurveDerivatives(edgeCurve, s, 1);
            sampleParameters[index] = s;
            edgePoints[index] = derivatives[0];
            edgeTangents[index] = (1 / norm(derivatives[1])) * derivatives[1];
            tValues[index] = s;
        }

        // ---------- The fixture's own algebra ----------
        // e'(s) = (1, s, c s^2 / 2) from the spline against the polynomial it is meant to be.
        var worstTangent = 0;
        for (var index = 0; index < gridCount; index += 1)
        {
            const s = sampleParameters[index];
            const predicted = vector(1, s, 0.5 * curvature * s ^ 2);
            worstTangent = max(worstTangent, norm(edgeTangents[index] -
                        (1 / norm(predicted)) * predicted));
        }
        println("[EDGE SINGULARITY] fixture algebra: worst unit e'(s) against (1, s, c s^2 / 2): " ~
            worstTangent);
        tally = checkWithin(tally, worstTangent, 1e-15, "the spline edge tangent against its polynomial");

        const singularMotion = singularEdgeMotion(curvature, epsilon, singularT);

        // The singularity itself, from the closed forms alone: at (t*, t*) the velocity IS the
        // edge tangent, so the measure must read zero without any search.
        const atSingularity = edgeSweepSingularityMeasure(singularMotion,
                evaluateBSplineCurveDerivatives(edgeCurve, singularT, 0)[0],
                evaluateBSplineCurveDerivatives(edgeCurve, singularT, 1)[1], singularT);
        println("[EDGE SINGULARITY] closed-form check at (s, t) = (0.5, 0.5): sine " ~
            atSingularity.sine ~ ", cosine " ~ atSingularity.cosine ~ ", speed " ~ atSingularity.speed);
        tally = checkWithin(tally, atSingularity.sine, 1e-15, "the sine at the closed-form singularity");
        tally = checkThat(tally, atSingularity.cosine > 0.999999 && !atSingularity.degenerate,
            "the velocity at the singularity is not the transported tangent: cosine " ~
            atSingularity.cosine ~ ".");

        // The measure against the closed form over the whole grid - the load-bearing cross-path
        // check, spline evaluation and motion sampling against hand-written polynomials.
        var worstMeasure = 0;
        for (var sampleIndex = 0; sampleIndex < gridCount; sampleIndex += 1)
        {
            for (var timeIndex = 0; timeIndex < gridCount; timeIndex += 1)
            {
                const s = sampleParameters[sampleIndex];
                const t = tValues[timeIndex];
                const measure = edgeSweepSingularityMeasure(singularMotion, edgePoints[sampleIndex],
                        edgeTangents[sampleIndex], t);
                const tangent = vector(1, s, 0.5 * curvature * s ^ 2);
                const velocity = vector(1, t, 0.5 * curvature * t ^ 2 + epsilon * (t - singularT));
                const predicted = norm(cross(tangent, velocity)) / (norm(tangent) * norm(velocity));
                worstMeasure = max(worstMeasure, abs(measure.sine - predicted));
            }
        }
        println("[EDGE SINGULARITY] the normalized sine over " ~ (gridCount * gridCount) ~
            " nodes against its closed form: worst " ~ worstMeasure);
        tally = checkWithin(tally, worstMeasure, 1e-15, "the grid sine against its closed form");

        // ---------- The grid alone must NOT be enough ----------
        const gridOnly = auditEdgeSweepSingularity(singularMotion, edgePoints, edgeTangents, tValues);
        println("[EDGE SINGULARITY] grid only (no curve): minimum sine " ~ gridOnly.minimumSine ~
            " at sample " ~ gridOnly.minimumSampleIndex ~ ", t " ~ gridOnly.minimumT ~
            "; candidates " ~ size(gridOnly.candidates) ~ ", detected " ~ gridOnly.detected);
        tally = checkThat(tally, gridOnly.minimumSine > 1e-3 && gridOnly.minimumSine < 0.05,
            "the grid's minimum sine is " ~ gridOnly.minimumSine ~
            ", so this grid either lands on the singularity or never screens it.");
        tally = checkThat(tally, !gridOnly.detected,
            "the grid alone claimed to detect the singularity, so the refinement is untested.");
        tally = checkThat(tally, size(gridOnly.candidates) >= 1,
            "the grid screened " ~ size(gridOnly.candidates) ~ " candidates, so nothing would refine.");

        // ---------- With the curve, the refinement must find the isolated point ----------
        const refinedAudit = auditEdgeSweepSingularity(singularMotion, edgePoints, edgeTangents,
                tValues, { "strippedCurve" : edgeCurve });
        println("[EDGE SINGULARITY] refined: " ~ size(refinedAudit.candidates) ~ " candidates, " ~
            size(refinedAudit.singularities) ~ " singularities, " ~ size(refinedAudit.ruledOut) ~
            " ruled out, " ~ refinedAudit.mergedCount ~ " merged, detected " ~ refinedAudit.detected);
        tally = checkThat(tally, refinedAudit.detected,
            "the refinement did not detect the isolated singularity at (0.5, 0.5).");
        tally = checkThat(tally, size(refinedAudit.singularities) == 1,
            "the refinement reported " ~ size(refinedAudit.singularities) ~ " singularities, not 1.");
        if (size(refinedAudit.singularities) >= 1)
        {
            const found = refinedAudit.singularities[0];
            println("[EDGE SINGULARITY] found (s, t) = (" ~ found.curveParameter ~ ", " ~
                found.refinedT ~ ") against (0.5, 0.5): sine " ~ found.refinedSine ~
                " from a grid sine of " ~ found.gridSine ~ ", inversion residual " ~
                found.inversionResidual);
            tally = checkWithin(tally, found.curveParameter - singularT, 1e-8,
                "the refined curve parameter against the closed-form 1/2");
            tally = checkWithin(tally, found.refinedT - singularT, 1e-8,
                "the refined t against the closed-form 1/2");
            tally = checkWithin(tally, found.refinedSine, 1e-12, "the refined sine");
            tally = checkWithin(tally, found.inversionResidual, 1e-12,
                "the arc-sample-to-curve-parameter inversion residual");
        }

        // ---------- The silent half: a velocity that can never match the tangent ----------
        // e' always has first component 1; this velocity's is 0, so no (s, t) is parallel.
        const crossingMotion = constantVelocityTranslationMotion(vector(0, 0.2, 1));
        const quietAudit = auditEdgeSweepSingularity(crossingMotion, edgePoints, edgeTangents,
                tValues, { "strippedCurve" : edgeCurve });
        println("[EDGE SINGULARITY] crossing velocity: minimum sine " ~ quietAudit.minimumSine ~
            ", candidates " ~ size(quietAudit.candidates) ~ ", ruled out " ~
            size(quietAudit.ruledOut) ~ ", detected " ~ quietAudit.detected ~
            ", degenerate nodes " ~ size(quietAudit.degenerateNodes));
        tally = checkThat(tally, !quietAudit.detected && size(quietAudit.singularities) == 0,
            "the crossing velocity reported " ~ size(quietAudit.singularities) ~ " singularities.");
        tally = checkThat(tally, quietAudit.minimumSine > 0.5,
            "the crossing velocity's minimum sine is " ~ quietAudit.minimumSine ~
            ", closer to parallel than this fixture is meant to be.");
        tally = checkThat(tally, size(quietAudit.ruledOut) == size(quietAudit.candidates),
            "the crossing velocity ruled out " ~ size(quietAudit.ruledOut) ~ " of " ~
            size(quietAudit.candidates) ~ " candidates - a screened candidate went unaccounted for.");
        tally = checkThat(tally, size(quietAudit.degenerateNodes) == 0,
            "the crossing velocity reported " ~ size(quietAudit.degenerateNodes) ~ " degenerate nodes.");

        reportCheckTally(context, id, "EDGE SINGULARITY", tally,
            "detector 3 refines a grid miss of 0.0136 onto the closed-form isolated singularity " ~
            "at (1/2, 1/2), reports nothing from the grid alone, and rules out every candidate " ~
            "under a velocity that can never be parallel to the edge.");
    });

/**
 * LIVE test of the trim loop plumbing (spec sections 5, 6.3.3): the one step that cannot be
 * checked from fixtures, because the question is whether real extraction output converts into
 * loops the census can mask with.
 *
 * The fixture is a parabolic sheet SPLIT by a projected circle, which yields two faces on ONE
 * surface: an annulus whose trim set is the domain rectangle plus a hole, and the disc inside
 * it. Both are exactly extracted B-splines, so neither carries kernel trim curves and both
 * take the co-edge pcurve path - the path that has no other source and had never run.
 *
 * Everything about it is closed form. The surface is S(u, v) = (0.2u, 0.15v, 0.1u^2), so
 *
 *     S_u = (0.2, 0, 0.2u)    S_v = (0, 0.15, 0)    N = (-0.03u, 0, 0.03)
 *
 * and under the constant velocity (1, 0, 0.5) the envelope function is exactly
 * f = 0.03 (0.5 - u): the contact set is the plane u = 0.5, a slab spanning every v and t.
 * Inverting the projected circle costs nothing either, since u and v depend only on x and y -
 * the hole is the ELLIPSE u = 0.5 + 0.15 cos(theta), v = 0.5 + 0.2 sin(theta).
 *
 * That fixes every number this test asserts. On a nine-node grid the only nodes inside the
 * ellipse are the five of the plus shape centred on (0.5, 0.5), so masking removes the cells
 * around them and cuts the slab in two: ONE 128-cell component becomes TWO of 32 cells, both
 * reporting the trim boundary.
 *
 * It also exercises the boundary tolerance, which is what the annulus's outer loop needs: that
 * loop lies exactly ON the domain rectangle, where an even-odd ray cast is a coin flip. Without
 * the tolerance the u = 1 and v = 1 node lines come back masked and the boundary cell rows
 * vanish - and the boundary is where co-edge components live.
 */

annotation { "Feature Type Name" : "Sweep Trim Loop Live Test" }
export const sweepTrimLoopLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";

        // First, pure: chainer output straight into the cyclic mask. This round trip lives here
        // because it is the only place both modules are visible, and it is the one thing neither
        // self test can check - the mask had only ever seen hand-built loops, whose stored
        // convention is not the one the chainer produces for a WINDING loop.
        var roundTripFailures = 0;
        for (var seamSegmentCount in [8, 1])
        {
            const seamLoops = chainUvPolylinesIntoLoops(
                    [seamArcSamples(0.2, seamSegmentCount), seamArcSamples(0.8, seamSegmentCount)], 1e-9, 1);
            if (size(seamLoops) != 2)
            {
                roundTripFailures += 1;
                continue;
            }
            var maskLoops = makeArray(2);
            for (var loopIndex = 0; loopIndex < 2; loopIndex += 1)
            {
                maskLoops[loopIndex] = { "points" : seamLoops[loopIndex].points,
                        "winding" : seamLoops[loopIndex].winding };
            }
            for (var probeU in [0, 0.125, 0.5, 0.875, 0.99])
            {
                if (!uvPointInsideLoopsCyclic(maskLoops, probeU, 0.5, 1) ||
                    uvPointInsideLoopsCyclic(maskLoops, probeU, 0.05, 1) ||
                    uvPointInsideLoopsCyclic(maskLoops, probeU, 0.95, 1))
                {
                    roundTripFailures += 1;
                }
            }
        }
        println("[TRIM LIVE TEST] chainer to cyclic mask round trip: " ~ roundTripFailures ~
            " misclassification(s) of 30");
        if (roundTripFailures != 0)
        {
            failures = failures ~ " chained winding loops were misclassified by the cyclic mask at " ~
                roundTripFailures ~ " probe(s).";
        }

        // The sheet: degrees (2, 2) reproduce S(u, v) = (0.2u, 0.15v, 0.1u^2) exactly, since x
        // and y are linear and z is a quadratic in u alone.
        const xCoefficients = [0, 0.1, 0.2];
        const yCoefficients = [0, 0.075, 0.15];
        const zCoefficients = [0, 0, 0.1];
        var netRows = makeArray(3);
        for (var i = 0; i < 3; i += 1)
        {
            var row = makeArray(3);
            for (var j = 0; j < 3; j += 1)
            {
                row[j] = vector(xCoefficients[i], yCoefficients[j], zCoefficients[i]) * meter;
            }
            netRows[i] = row;
        }
        const sheetId = id + "sheet";
        opCreateBSplineSurface(context, sheetId, {
                    "bSplineSurface" : bSplineSurface({
                                "uDegree" : 2,
                                "vDegree" : 2,
                                "isUPeriodic" : false,
                                "isVPeriodic" : false,
                                "controlPoints" : controlPointMatrix(netRows)
                            })
                });

        // Split the sheet with a circle projected straight up. Splitting rather than deleting
        // leaves BOTH faces on the same surface: the annulus carries the hole, the disc does
        // not, and the two must disagree about the domain centre.
        const sketchId = id + "holeSketch";
        var holeSketch = newSketchOnPlane(context, sketchId, {
                    "sketchPlane" : plane(vector(0, 0, 0) * meter, vector(0, 0, 1))
                });
        skCircle(holeSketch, "hole", {
                    "center" : vector(0.1, 0.075) * meter,
                    "radius" : 0.03 * meter
                });
        skSolve(holeSketch);
        opSplitFace(context, id + "split", {
                    "faceTargets" : qOwnedByBody(qCreatedBy(sheetId, EntityType.BODY), EntityType.FACE),
                    "edgeTools" : qCreatedBy(sketchId, EntityType.EDGE),
                    "projectionType" : ProjectionType.DIRECTION,
                    "direction" : vector(0, 0, 1)
                });
        opDeleteBodies(context, id + "dropSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

        // Scoped to the sheet this feature created, never qEverything: the harness Part Studio
        // carries bodies from earlier runs, and a live run that reads "every sheet body" reads
        // those too - measured, five faces instead of two.
        const toolBody = qCreatedBy(sheetId, EntityType.BODY);
        const faceRecords = extractToolFaceRecords(context, toolBody, 1e-6);
        println("[TRIM LIVE TEST] " ~ summarizeFaceRecords(faceRecords));
        if (size(faceRecords) != 2)
        {
            reportFeatureInfo(context, id, "FAIL: the split sheet has " ~ size(faceRecords) ~
                " face(s), expected 2.");
            return;
        }
        const coEdgeRecords = extractCoEdgeRecords(context, toolBody, faceRecords, 33);
        println("[TRIM LIVE TEST] " ~ summarizeCoEdgeRecords(coEdgeRecords));

        // The annulus is the larger face. Both records must be exact B-splines, or the loops
        // would be coming from kernel trim curves instead of the pcurve path this test is for.
        var annulusIndex = 0;
        var discIndex = 1;
        if (evArea(context, { "entities" : faceRecords[0].faceQuery }) <
            evArea(context, { "entities" : faceRecords[1].faceQuery }))
        {
            annulusIndex = 1;
            discIndex = 0;
        }
        for (var record in faceRecords)
        {
            if (!record.splineIsExact)
            {
                failures = failures ~ " face " ~ record.faceIndex ~ " came back as " ~
                    record.surfaceClass ~ ", so it took the approximation path and the co-edge " ~
                    "pcurve path is not what ran.";
            }
        }

        const annulusTrim = buildFaceTrimLoops(faceRecords[annulusIndex], coEdgeRecords, {});
        const discTrim = buildFaceTrimLoops(faceRecords[discIndex], coEdgeRecords, {});
        println("[TRIM LIVE TEST] annulus " ~ summarizeTrimLoops(annulusTrim));
        println("[TRIM LIVE TEST] disc    " ~ summarizeTrimLoops(discTrim));
        if (annulusTrim.source != "coEdges" || annulusTrim.loopCount != 2 || !annulusTrim.usable ||
            annulusTrim.windingLoopCount != 0)
        {
            failures = failures ~ " the annulus did not convert to two closed co-edge loops.";
        }
        if (discTrim.source != "coEdges" || discTrim.loopCount != 1 || !discTrim.usable)
        {
            failures = failures ~ " the disc did not convert to one closed co-edge loop.";
        }

        // The mask geometry, read off the ellipse. Sixteen probes at 0.5 and 1.6 times the hole
        // radius, taken as points ON the surface so their uv is exact: the inner ring must be
        // masked out of the annulus and kept by the disc, and the outer ring the reverse.
        const holeCentre = vector(0.1, 0.075);
        var innerFailures = 0;
        var outerFailures = 0;
        for (var probeIndex = 0; probeIndex < 8; probeIndex += 1)
        {
            const angle = 2 * PI * probeIndex / 8 * radian;
            const direction = vector(cos(angle), sin(angle));
            const innerUv = surfacePointUv(holeCentre + 0.5 * 0.03 * direction);
            const outerUv = surfacePointUv(holeCentre + 1.6 * 0.03 * direction);
            if (uvPointInsideLoops(annulusTrim.loops, innerUv[0], innerUv[1]) ||
                !uvPointInsideLoops(discTrim.loops, innerUv[0], innerUv[1]))
            {
                innerFailures += 1;
            }
            if (!uvPointInsideLoops(annulusTrim.loops, outerUv[0], outerUv[1]) ||
                uvPointInsideLoops(discTrim.loops, outerUv[0], outerUv[1]))
            {
                outerFailures += 1;
            }
        }
        println("[TRIM LIVE TEST] mask probes: " ~ innerFailures ~ " inner and " ~ outerFailures ~
            " outer misclassification(s) of 8 each");
        if (innerFailures != 0 || outerFailures != 0)
        {
            failures = failures ~ " the converted loops misclassified " ~ (innerFailures + outerFailures) ~
                " of 16 probes around the hole.";
        }

        // The domain corners: the annulus's outer loop runs exactly along them, so this is the
        // boundary-tolerance path.
        var cornerFailures = 0;
        for (var cornerU in [0, 1])
        {
            for (var cornerV in [0, 1])
            {
                if (!uvPointInsideLoops(annulusTrim.loops, cornerU, cornerV) &&
                    !uvPointOnLoops(annulusTrim.loops, cornerU, cornerV, 1e-6, 0))
                {
                    cornerFailures += 1;
                }
            }
        }
        println("[TRIM LIVE TEST] domain corners rejected by the mask: " ~ cornerFailures ~ " of 4");
        if (cornerFailures != 0)
        {
            failures = failures ~ " the boundary tolerance did not keep the domain corners.";
        }

        // The census, which is the whole point: the same face, masked with its own trim loops.
        const factors = buildEnvelopePatchFactors(faceRecords[annulusIndex].spline);
        const spans = buildMotionSpanPolynomials(constantVelocityTranslationMotion(vector(1, 0, 0.5)));
        // No sign tolerance is supplied, and none is needed: this fixture's contact set lands
        // exactly ON the u = 0.5 node line, where f is zero in closed form but comes out of the
        // coefficient path at ~1e-18 with a sign that is pure rounding, and the census derives
        // its own threshold from each block's value range. The guessed 1e-12 this test used to
        // pass is what spec 12.1 item 5 removed; with an absolute zero the slab came out a ragged
        // 80 cells instead of 128, per v node.
        var censusOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 9,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false };
        const unmasked = censusFunnelComponents(factors, spans, censusOptions);
        var maskedOptions = censusOptions;
        maskedOptions.trimLoops = annulusTrim.loops;
        maskedOptions.degenerate = faceRecords[annulusIndex].degenerate;
        const masked = censusFunnelComponents(factors, spans, maskedOptions);
        var maskedCells = "";
        var maskedCellTotal = 0;
        var maskedTrimTouching = 0;
        for (var component in masked.components)
        {
            maskedCells = maskedCells ~ " " ~ component.cellCount;
            maskedCellTotal += component.cellCount;
            maskedTrimTouching += component.touchesTrimBoundary ? 1 : 0;
        }
        println("[TRIM LIVE TEST] census sign tolerance derived from the block range: " ~
            unmasked.signTolerance.minimum ~ ", " ~ unmasked.signTolerance.zeroSignNodeCount ~
            " zero-sign node(s) of 729 (closed form: the 81 of the u = 0.5 plane)");
        println("[TRIM LIVE TEST] census unmasked: " ~ size(unmasked.components) ~ " component(s), " ~
            (size(unmasked.components) > 0 ? (unmasked.components[0].cellCount ~ " cells") : "") ~
            "; masked with the face's own loops: " ~ size(masked.components) ~ " component(s), cells" ~
            maskedCells ~ ", " ~ maskedTrimTouching ~ " trim-touching (closed form: 128 cells " ~
            "cut to 32 + 32)");
        if (size(unmasked.components) != 1 || unmasked.components[0].cellCount != 128)
        {
            failures = failures ~ " the unmasked census was expected to be one 128-cell slab at " ~
                "u = 0.5, got " ~ size(unmasked.components) ~ " component(s).";
        }
        // The hole spans the four middle v cell rows of eight, so exactly half the slab survives,
        // in two equal halves.
        if (size(masked.components) != 2 || maskedTrimTouching != 2 ||
            masked.components[0].cellCount != 32 || masked.components[1].cellCount != 32)
        {
            failures = failures ~ " the hole did not cut the slab into two 32-cell halves (" ~
                maskedCellTotal ~ " of " ~ unmasked.components[0].cellCount ~ " cells survived).";
        }

        reportTestVerdict(context, id, "TRIM LOOP LIVE TEST", failures,
            "a real face's co-edge pcurves converted into closed uv loops, the loops " ~
            "classified 20 of 20 probes including the domain corners, and the census masked " ~
            "with them split the contact slab exactly as the closed form predicts.");
    });


// ============================= Lean evaluator agreement (spec 11.2 levers A and D) =============================

/**
 * A clamped rational full circle in u (degree 2, the standard nine-point construction with
 * weights 1 and sqrt(1/2)) crossed with a straight line in v: an exact unit cylinder.
 *
 * Two properties make it the fixture the lean evaluator has to survive. Its knot vector has
 * INTERIOR knots of multiplicity two, so the basis recurrence runs on a vector where several
 * spans are empty; and it is genuinely RATIONAL with non-unit weights, so A4.4's quotient rule
 * carries every mixed partial rather than falling through the non-rational shortcut.
 */
function rationalCylinderFixtureSurface() returns map
{
    const cornerWeight = sqrt(0.5);
    const circleXY = [[1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1], [1, -1], [1, 0]];
    const circleWeights = [1, cornerWeight, 1, cornerWeight, 1, cornerWeight, 1, cornerWeight, 1];
    var net = makeArray(9);
    var weightGrid = makeArray(9);
    for (var i = 0; i < 9; i += 1)
    {
        var row = makeArray(2);
        var weightRow = makeArray(2);
        for (var j = 0; j < 2; j += 1)
        {
            row[j] = vector(circleXY[i][0], circleXY[i][1], j * 1.5);
            weightRow[j] = circleWeights[i];
        }
        net[i] = row;
        weightGrid[i] = weightRow;
    }
    return {
            "uDegree" : 2, "vDegree" : 1,
            "uKnots" : [0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 4], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : net, "isRational" : true, "weights" : weightGrid,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * A non-rational multi-span patch, degree (3, 2) with unequal interior knots in both
 * directions: the case a single-span Bezier fixture cannot reach, where the span search and the
 * knot differences in the basis recurrence both have work to do.
 */
function multiSpanFixtureSurface() returns map
{
    const uParameters = [0, 0.15, 0.4, 0.65, 0.85, 1];
    const vParameters = [0, 0.3, 0.7, 1];
    var net = makeArray(6);
    for (var i = 0; i < 6; i += 1)
    {
        var row = makeArray(4);
        for (var j = 0; j < 4; j += 1)
        {
            const u = uParameters[i];
            const v = vParameters[j];
            row[j] = vector(u, v, 0.3 * u * u - 0.2 * u * v + 0.11 * v * v * v);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 0, 0.4, 0.7, 1, 1, 1, 1], "vKnots" : [0, 0, 0, 0.5, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The worst ABSOLUTE disagreement between the lean derivative triangle and splineRefinementUtils'
 * general rectangle, over a sample grid inside the knot domain.
 *
 * The claim the lean path makes is exact agreement, not agreement to a tolerance: it runs the
 * same recurrence, the same summation order and the same quotient rule, only on scalars. So the
 * number this returns is expected to be 0, and any nonzero value is a real finding about the
 * rewrite rather than rounding to be waved through.
 */
function leanSurfaceDisagreement(surface is map, gridCount is number) returns number
{
    const domain = surfaceKnotDomain(surface);
    const uOrders = [0, 1, 0, 2, 1, 0];
    const vOrders = [0, 0, 1, 0, 1, 2];
    var worst = 0;
    for (var uIndex = 0; uIndex < gridCount; uIndex += 1)
    {
        const u = domain.uStart + (domain.uEnd - domain.uStart) * (uIndex + 0.5) / gridCount;
        for (var vIndex = 0; vIndex < gridCount; vIndex += 1)
        {
            const v = domain.vStart + (domain.vEnd - domain.vStart) * (vIndex + 0.5) / gridCount;
            const lean = leanSurfaceDerivatives(surface, u, v, 2);
            const general = evaluateBSplineSurfaceDerivatives(surface, u, v, 2, 2);
            const leanPoint = leanSurfacePoint(surface, u, v);
            for (var entry = 0; entry < 6; entry += 1)
            {
                const reference = general[uOrders[entry]][vOrders[entry]];
                for (var component = 0; component < 3; component += 1)
                {
                    worst = max(worst, abs(lean[entry][component] - reference[component]));
                }
            }
            for (var component = 0; component < 3; component += 1)
            {
                worst = max(worst, abs(leanPoint[component] - general[0][0][component]));
            }
        }
    }
    return worst;
}

/**
 * The envelope gradient computed the way solidSweepUtils computed it BEFORE the lean rewrite:
 * the general evaluator, std `cross`/`dot`/`norm`, and Matrix multiplication. This is the
 * reference the rewritten `evaluateEnvelopeGradientPointwise` is held against, and it is kept
 * here rather than in the module precisely because it is the slow path the module no longer has.
 */
function referenceEnvelopeGradient(strippedMotion is map, strippedSurface is map,
    u is number, v is number, t is number) returns map
{
    const derivatives = evaluateBSplineSurfaceDerivatives(strippedSurface, u, v, 2, 2);
    const surfacePoint = derivatives[0][0];
    const uTangent = derivatives[1][0];
    const vTangent = derivatives[0][1];
    const normal = cross(uTangent, vTangent);
    const uNormalDerivative = cross(derivatives[2][0], vTangent) + cross(uTangent, derivatives[1][1]);
    const vNormalDerivative = cross(derivatives[1][1], vTangent) + cross(uTangent, derivatives[0][2]);
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * surfacePoint + sample.translationDerivative;
    const acceleration = sample.rotationSecondDerivative * surfacePoint + sample.translationSecondDerivative;
    const transportedNormal = sample.rotation * normal;
    const turnedNormal = sample.rotationDerivative * normal;
    return {
            "value" : dot(transportedNormal, velocity),
            "uDerivative" : dot(sample.rotation * uNormalDerivative, velocity) +
                dot(transportedNormal, sample.rotationDerivative * uTangent),
            "vDerivative" : dot(sample.rotation * vNormalDerivative, velocity) +
                dot(transportedNormal, sample.rotationDerivative * vTangent),
            "tDerivative" : dot(turnedNormal, velocity) + dot(transportedNormal, acceleration),
            "tDerivativeScale" : norm(turnedNormal) * norm(velocity) +
                norm(transportedNormal) * norm(acceleration),
            "valueScale" : norm(transportedNormal) * norm(velocity)
        };
}

/** The worst RELATIVE disagreement between the module's gradient and the reference above. */
function leanGradientDisagreement(strippedMotion is map, strippedSurface is map,
    gridCount is number, times is array) returns number
{
    const domain = surfaceKnotDomain(strippedSurface);
    const fields = ["value", "uDerivative", "vDerivative", "tDerivative", "tDerivativeScale", "valueScale"];
    var worst = 0;
    for (var t in times)
    {
        for (var uIndex = 0; uIndex < gridCount; uIndex += 1)
        {
            const u = domain.uStart + (domain.uEnd - domain.uStart) * (uIndex + 0.5) / gridCount;
            for (var vIndex = 0; vIndex < gridCount; vIndex += 1)
            {
                const v = domain.vStart + (domain.vEnd - domain.vStart) * (vIndex + 0.5) / gridCount;
                const actual = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, u, v, t);
                const reference = referenceEnvelopeGradient(strippedMotion, strippedSurface, u, v, t);
                for (var field in fields)
                {
                    worst = max(worst, abs(actual[field] - reference[field]) / (1 + abs(reference[field])));
                }
                // The section form omits the time terms; the three it does return have to be
                // the SAME numbers, since skipping f_t is meant to skip work and nothing else.
                const section = evaluateSectionGradientPointwise(strippedMotion, strippedSurface, u, v, t);
                for (var field in ["value", "uDerivative", "vDerivative"])
                {
                    worst = max(worst, abs(section[field] - actual[field]) / (1 + abs(actual[field])));
                }
            }
        }
    }
    return worst;
}

/**
 * The worst disagreement between evaluating with the raw motion and evaluating with a motion
 * FROZEN at the same t - the whole of lever A, checked rather than assumed. Zero is the only
 * acceptable answer: freezing is meant to skip recomputation, not to approximate it.
 */
function frozenMotionDisagreement(strippedMotion is map, strippedSurface is map,
    gridCount is number, times is array) returns number
{
    const domain = surfaceKnotDomain(strippedSurface);
    const fields = ["value", "uDerivative", "vDerivative", "tDerivative", "tDerivativeScale", "valueScale"];
    var worst = 0;
    for (var t in times)
    {
        const frozen = evaluateMotionSample(strippedMotion, t);
        for (var uIndex = 0; uIndex < gridCount; uIndex += 1)
        {
            const u = domain.uStart + (domain.uEnd - domain.uStart) * (uIndex + 0.5) / gridCount;
            for (var vIndex = 0; vIndex < gridCount; vIndex += 1)
            {
                const v = domain.vStart + (domain.vEnd - domain.vStart) * (vIndex + 0.5) / gridCount;
                const raw = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, u, v, t);
                const thawed = evaluateEnvelopeGradientPointwise(frozen, strippedSurface, u, v, t);
                for (var field in fields)
                {
                    worst = max(worst, abs(raw[field] - thawed[field]));
                }
                worst = max(worst, norm(liftContactPoint(strippedMotion, strippedSurface, u, v, t) -
                            liftContactPoint(frozen, strippedSurface, u, v, t)));
                worst = max(worst, abs(evaluateEnvelopePointwise(strippedMotion, strippedSurface, u, v, t) -
                            evaluateEnvelopePointwise(frozen, strippedSurface, u, v, t)));
            }
        }
    }
    return worst;
}

/**
 * The optimization pass's regression gate (spec 11.2). Everything the pass changed that could
 * silently change an ANSWER rather than a runtime is checked here against the code it replaced:
 * the lean surface evaluator against splineRefinementUtils' general one, the scalarized envelope
 * gradient against the Vector-and-Matrix formula it was rewritten from, and the frozen motion
 * against the same evaluation done from the splines every time.
 *
 * The live half runs on the extracted ellipsoid, because that is the only net in the project
 * that is rational AND periodic AND multi-span at once, and it is the surface the 66 s assembly
 * run spends its time on.
 */
annotation { "Feature Type Name" : "Sweep Lean Evaluator Live Test" }
export const sweepLeanEvaluatorLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();

        // ---------- Surface derivatives, self-contained fixtures ----------
        const fixtures = [
            ["island Bezier patch (3x2, non-rational)", islandFixtureSurface()],
            ["slant patch (2x1, non-rational)", slantFixtureSurface()],
            ["multi-span patch (3x2, unequal interior knots)", multiSpanFixtureSurface()],
            ["rational cylinder (2x1, doubled interior knots)", rationalCylinderFixtureSurface()]
        ];
        for (var fixture in fixtures)
        {
            const disagreement = leanSurfaceDisagreement(fixture[1], 7);
            println("[LEAN EVALUATOR TEST] " ~ fixture[0] ~ ": lean vs general worst |difference| " ~
                disagreement);
            tally = checkThat(tally, disagreement == 0,
                "the lean evaluator disagrees with the general one on the " ~ fixture[0] ~
                " by " ~ disagreement ~ " - the rewrite claims EXACT agreement, so any " ~
                "difference at all is a defect in it.");
        }

        // ---------- The extracted ellipsoid: rational, periodic, multi-span ----------
        const tool = ellipsoidToolFixture(context, id + "tool", ELLIPSOID_SEMI_AXIAL,
            ELLIPSOID_SEMI_RADIAL, 1e-7);
        println("[LEAN EVALUATOR TEST] tool: " ~ describeSurfaceShape(tool.surface) ~ ", control net " ~
            size(tool.surface.controlPoints) ~ "x" ~ size(tool.surface.controlPoints[0]));
        const ellipsoidDisagreement = leanSurfaceDisagreement(tool.surface, 9);
        println("[LEAN EVALUATOR TEST] extracted ellipsoid: lean vs general worst |difference| " ~
            ellipsoidDisagreement);
        tally = checkThat(tally, ellipsoidDisagreement == 0,
            "the lean evaluator disagrees with the general one on the extracted ellipsoid by " ~
            ellipsoidDisagreement ~ ".");

        // ---------- The envelope gradient against the formula it was rewritten from ----------
        // A twisting motion, so A', A'' and b'' are all nonzero and every term of the gradient
        // is exercised rather than vanishing the way it does under a straight translation.
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];
        const twistingMotion = {
                "columnX" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(1, 0, 0), vector(0.95, 0.15, 0.02),
                            vector(0.88, 0.28, 0.06), vector(0.8, 0.4, 0.1)]
                },
                "columnY" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 1, 0), vector(-0.12, 0.97, 0.05),
                            vector(-0.24, 0.92, 0.09), vector(-0.35, 0.85, 0.14)]
                },
                "columnZ" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 1), vector(0.03, -0.06, 0.99),
                            vector(0.07, -0.11, 0.97), vector(0.12, -0.18, 0.93)]
                },
                "translation" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0.02, 0.01, 0.01),
                            vector(0.05, 0.015, 0.03), vector(0.09, 0.02, 0.04)]
                }
            };
        const times = [0.13, 0.5, 0.86];
        for (var fixture in [["island patch", islandFixtureSurface()],
                    ["rational cylinder", rationalCylinderFixtureSurface()],
                    ["extracted ellipsoid", tool.surface]])
        {
            const gradientDisagreement = leanGradientDisagreement(twistingMotion, fixture[1], 5, times);
            println("[LEAN EVALUATOR TEST] " ~ fixture[0] ~ ": gradient vs the pre-rewrite formula, " ~
                "worst relative difference " ~ gradientDisagreement);
            tally = checkWithin(tally, gradientDisagreement, 1e-13,
                "the gradient's disagreement with the pre-rewrite formula on the " ~ fixture[0]);

            const frozenDisagreement = frozenMotionDisagreement(twistingMotion, fixture[1], 5, times);
            println("[LEAN EVALUATOR TEST] " ~ fixture[0] ~ ": frozen vs re-sampled motion, worst " ~
                "|difference| " ~ frozenDisagreement);
            tally = checkThat(tally, frozenDisagreement == 0,
                "freezing the motion changed the answer on the " ~ fixture[0] ~ " by " ~
                frozenDisagreement ~ " - a frozen sample must be the SAME state, not a nearby one.");
        }

        // The rational cylinder is exact geometry, so the lean evaluator's own correctness is
        // checkable without any reference implementation: every point sits on the unit circle
        // and every u tangent is perpendicular to the radius.
        const cylinder = rationalCylinderFixtureSurface();
        var worstRadial = 0;
        var worstTangency = 0;
        for (var uIndex = 0; uIndex < 24; uIndex += 1)
        {
            const u = 4 * (uIndex + 0.5) / 24;
            const derivatives = leanSurfaceDerivatives(cylinder, u, 0.4, 2);
            const point = derivatives[0];
            const radius = sqrt(point[0] ^ 2 + point[1] ^ 2);
            worstRadial = max(worstRadial, abs(radius - 1));
            const uTangent = derivatives[1];
            worstTangency = max(worstTangency,
                abs(point[0] * uTangent[0] + point[1] * uTangent[1]) /
                    (1 + sqrt(uTangent[0] ^ 2 + uTangent[1] ^ 2)));
        }
        println("[LEAN EVALUATOR TEST] rational cylinder: worst radius error " ~ worstRadial ~
            ", worst radius/tangent non-perpendicularity " ~ worstTangency);
        tally = checkWithin(tally, worstRadial, 1e-14, "the rational cylinder's radius error");
        tally = checkWithin(tally, worstTangency, 1e-14, "the rational cylinder's tangent perpendicularity");

        opDeleteBodies(context, id + "deleteTool", { "entities" : tool.body });
        reportCheckTally(context, id, "LEAN EVALUATOR TEST", tally,
            "the lean evaluator reproduces the general one exactly on rational, periodic and " ~
            "multi-span nets, the scalarized gradient reproduces the formula it replaced, and " ~
            "a frozen motion is the same state the splines give.");
    });


// ============================= The rotating-box fixture: path and rotation combinations (spec 8) =============================

/** Which curve the tool's origin travels along in the sharp-feature fixture. */
export enum SweepTestPathType
{
    annotation { "Name" : "Straight line" }
    LINE,
    annotation { "Name" : "Circular arc" }
    ARC,
    annotation { "Name" : "Helix" }
    HELIX,
    annotation { "Name" : "Free spline S-curve" }
    S_CURVE
}

/**
 * How the tool's frame turns while it travels. The three fixed-axis kinds compose rotations
 * about WORLD axes, innermost first, so TWO_AXIS and THREE_AXIS put the tool in genuine
 * multi-axis tumble rather than in one spin seen from a tilted angle; the two path kinds build
 * the frame from the path's own tangent, which is the motion a real sweep feature transports.
 */
/**
 * How far the closure pipeline is allowed to run before it stops and reports (spec 9).
 *
 * A stage that hangs cannot be found from the output, because a regeneration that never finishes
 * delivers no printlns and a feature killed for running long rolls its whole result back. The run
 * that COMPLETES is the measurement: stopping one stage short of the expensive one is what names
 * it.
 */
export enum SweepClosureStage
{
    annotation { "Name" : "Cap copies" }
    CAPS,
    annotation { "Name" : "Contact wires" }
    WIRES,
    annotation { "Name" : "Imprint" }
    IMPRINT,
    annotation { "Name" : "Trim to the envelope side" }
    TRIM,
    annotation { "Name" : "Match the seams" }
    SEAMS,
    annotation { "Name" : "Knit to a solid" }
    KNIT
}

/** The `stopAfter` string `assembleSweptSolid` takes, for a chosen stage. */
function closureStageName(stage is SweepClosureStage) returns string
{
    if (stage == SweepClosureStage.CAPS)
    {
        return "caps";
    }
    if (stage == SweepClosureStage.WIRES)
    {
        return "wires";
    }
    if (stage == SweepClosureStage.IMPRINT)
    {
        return "imprint";
    }
    if (stage == SweepClosureStage.TRIM)
    {
        return "trim";
    }
    if (stage == SweepClosureStage.SEAMS)
    {
        return "seams";
    }
    return "knit";
}

export enum SweepTestRotationType
{
    annotation { "Name" : "None - pure translation" }
    NONE,
    annotation { "Name" : "Spin about one fixed axis" }
    SINGLE_AXIS,
    annotation { "Name" : "Tumble about two fixed axes" }
    TWO_AXIS,
    annotation { "Name" : "Tumble about three fixed axes" }
    THREE_AXIS,
    annotation { "Name" : "Follow the path tangent" }
    FOLLOW_PATH,
    annotation { "Name" : "Follow the path tangent and roll about it" }
    FOLLOW_PATH_AND_ROLL
}

/**
 * The largest `h * (turn rate)` a cubic Hermite span may carry and still hold the stored rotation
 * inside spec 2.1's 1e-9 orthonormality bar - see turningRotationSplines for the h^4 argument.
 *
 * The bare h^4 arithmetic allows 0.0209. This sits below it because the rate driving the span
 * count is SAMPLED at finitely many nodes, so the true peak lies between two of them and a
 * constant sized to land exactly on 1e-9 lands just over it instead. At 0.016 the predicted
 * defect is 3.4e-10, which leaves the sampling somewhere to be wrong.
 */
const HERMITE_SPAN_TURN_BUDGET = 0.016;

/**
 * Hermite spans are capped here so an extreme turn cannot build a motion spline large enough to
 * spend the whole interpreter step budget before any geometry is emitted.
 *
 * A frame that follows a free spline's tangent needs the most of them, because its turn rate is
 * driven by how near the tangent passes the projected reference rather than by any angle the
 * dialog asks for. `sweepTestMotionSpans` reports when the cap binds, and a bound cap means the
 * stored rotation will not hold spec 2.1's 1e-9 - which the drift check then says out loud rather
 * than leaving as an unexplained failure.
 */
const HERMITE_SPAN_LIMIT = 512;

/** The world direction a straight fixture path travels, deliberately off every tool axis so an
    axis-aligned box never presents a face whose normal is exactly perpendicular to the travel. */
const SWEEP_TEST_LINE_DIRECTION = vector(1, 0.35, 0.15);

/**
 * The fixture path's position, velocity and acceleration at `t` in [0, 1], in closed form.
 *
 * pathSpec: { travel {number} : meters of chord or of axial rise, radius {number} : meters,
 * sweepAngle {number} : plain radians of arc or helix turn }.
 *
 * Returns { position, velocity, acceleration } - unit-stripped Vectors, meters implied.
 */
function sweepTestPathSample(pathType is SweepTestPathType, pathSpec is map, t is number) returns map
{
    if (pathType == SweepTestPathType.LINE)
    {
        const direction = normalize(SWEEP_TEST_LINE_DIRECTION);
        return {
                "position" : (pathSpec.travel * t) * direction,
                "velocity" : pathSpec.travel * direction,
                "acceleration" : vector(0, 0, 0)
            };
    }
    if (pathType == SweepTestPathType.ARC || pathType == SweepTestPathType.HELIX)
    {
        // An arc through the origin at t = 0 with its centre on +y, so the tool starts where the
        // straight fixture starts and the two are directly comparable.
        const angle = pathSpec.sweepAngle * t;
        const radius = pathSpec.radius;
        const rise = pathType == SweepTestPathType.HELIX ? pathSpec.travel : 0;
        return {
                "position" : vector(radius * sin(angle * radian),
                        radius * (1 - cos(angle * radian)), rise * t),
                "velocity" : vector(radius * pathSpec.sweepAngle * cos(angle * radian),
                        radius * pathSpec.sweepAngle * sin(angle * radian), rise),
                "acceleration" : vector(-radius * pathSpec.sweepAngle ^ 2 * sin(angle * radian),
                        radius * pathSpec.sweepAngle ^ 2 * cos(angle * radian), 0)
            };
    }
    // A cubic Bezier that leaves and re-enters the travel direction with an out-of-plane rise,
    // so the frame's reference projection turns in all three axes rather than in a plane.
    const travel = pathSpec.travel;
    const controlPoints = [
            vector(0, 0, 0),
            vector(travel / 3, 0.55 * travel, 0.12 * travel),
            vector(2 * travel / 3, -0.55 * travel, 0.34 * travel),
            vector(travel, 0, 0.5 * travel)
        ];
    const oneMinus = 1 - t;
    return {
            "position" : oneMinus ^ 3 * controlPoints[0] + 3 * oneMinus ^ 2 * t * controlPoints[1] +
                3 * oneMinus * t ^ 2 * controlPoints[2] + t ^ 3 * controlPoints[3],
            "velocity" : 3 * oneMinus ^ 2 * (controlPoints[1] - controlPoints[0]) +
                6 * oneMinus * t * (controlPoints[2] - controlPoints[1]) +
                3 * t ^ 2 * (controlPoints[3] - controlPoints[2]),
            "acceleration" : 6 * oneMinus * (controlPoints[2] - 2 * controlPoints[1] + controlPoints[0]) +
                6 * t * (controlPoints[3] - 2 * controlPoints[2] + controlPoints[1])
        };
}

/**
 * The world axis a tangent-following frame should project against: whichever of the three the
 * path's tangent comes CLOSEST TO ALIGNING WITH LEAST over the sweep.
 *
 * The projected reference is what fixes the frame's roll, and its derivative carries a factor
 * `1 / |reference - (reference . T) T|`. A path whose tangent swings toward the reference
 * therefore spins the frame arbitrarily fast near that moment, which costs Hermite spans in
 * proportion and can put the stored rotation's drift over spec 2.1's bar for reasons that have
 * nothing to do with the sweep. Choosing the axis by measurement rather than by convention costs
 * one pass over the path.
 *
 * Returns { reference {Vector}, worstAlignment {number} : |T . reference| at its largest, which
 * is how close the chosen axis still comes }.
 */
function sweepTestFrameReference(pathType is SweepTestPathType, pathSpec is map) returns map
{
    const axes = [vector(1, 0, 0), vector(0, 1, 0), vector(0, 0, 1)];
    const probeCount = 64;
    var worstAlignment = makeArray(3, 0);
    for (var index = 0; index <= probeCount; index += 1)
    {
        const sample = sweepTestPathSample(pathType, pathSpec, index / probeCount);
        const speed = norm(sample.velocity);
        if (speed < 1e-12)
        {
            continue;
        }
        const tangent = (1 / speed) * sample.velocity;
        for (var axisIndex = 0; axisIndex < 3; axisIndex += 1)
        {
            worstAlignment[axisIndex] = max(worstAlignment[axisIndex],
                abs(dot(tangent, axes[axisIndex])));
        }
    }
    var best = 0;
    for (var axisIndex = 1; axisIndex < 3; axisIndex += 1)
    {
        if (worstAlignment[axisIndex] < worstAlignment[best])
        {
            best = axisIndex;
        }
    }
    return { "reference" : axes[best], "worstAlignment" : worstAlignment[best] };
}

/** The world axes and turn rates of a fixed-axis rotation kind, INNERMOST FIRST: the frame is
    `R_last ... R_first`, so a two-axis tumble is a spin seen from a frame that is itself
    turning, which no single axis reproduces. Returns [{ axis, rate }] with rate in plain
    radians per unit t. */
function sweepTestRotationAxes(rotationType is SweepTestRotationType, turnAngle is number) returns array
{
    if (rotationType == SweepTestRotationType.SINGLE_AXIS)
    {
        return [{ "axis" : vector(0, 0, 1), "rate" : turnAngle }];
    }
    if (rotationType == SweepTestRotationType.TWO_AXIS)
    {
        return [
                { "axis" : vector(1, 0, 0), "rate" : turnAngle },
                { "axis" : vector(0, 0, 1), "rate" : 0.6 * turnAngle }
            ];
    }
    if (rotationType == SweepTestRotationType.THREE_AXIS)
    {
        return [
                { "axis" : vector(1, 0, 0), "rate" : turnAngle },
                { "axis" : vector(0, 1, 0), "rate" : 0.6 * turnAngle },
                { "axis" : vector(0, 0, 1), "rate" : 0.35 * turnAngle }
            ];
    }
    if (rotationType == SweepTestRotationType.FOLLOW_PATH_AND_ROLL)
    {
        return [{ "axis" : vector(1, 0, 0), "rate" : turnAngle }];
    }
    return [];
}

/** `columns[0] * v[0] + columns[1] * v[1] + columns[2] * v[2]` - a matrix stored as its columns
    applied to a vector. */
function applyColumnsToVector(columns is array, value is Vector) returns Vector
{
    return columns[0] * value[0] + columns[1] * value[1] + columns[2] * value[2];
}

/**
 * A composition of fixed-axis rotations and its exact time derivative at `t`.
 *
 * With `A = R_n ... R_1` and each `R_i` turning about its own axis at rate `w_i`, the product
 * rule gives `A' = sum_i w_i S_i K_i T_i` where `T_i = R_i ... R_1`, `S_i = R_n ... R_{i+1}` and
 * `K_i` is the cross-product matrix of axis i. Every factor there is a rotation of a vector, so
 * both `A` and `A'` come out of Rodrigues alone - no numerical differencing anywhere, which is
 * what lets the Hermite interpolation below carry a certified orthonormality bound.
 *
 * Returns { columns, columnDerivatives } - two arrays of three unitless Vectors.
 */
function fixedAxisFrameSample(axes is array, t is number) returns map
{
    const stageCount = size(axes);
    var stageColumns = makeArray(stageCount + 1);
    stageColumns[0] = [vector(1, 0, 0), vector(0, 1, 0), vector(0, 0, 1)];
    for (var stage = 0; stage < stageCount; stage += 1)
    {
        const unitAxis = normalize(axes[stage].axis);
        const angle = axes[stage].rate * t;
        var rotated = makeArray(3, vector(0, 0, 0));
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            rotated[columnIndex] = rotateAboutAxis(unitAxis, angle, stageColumns[stage][columnIndex]);
        }
        stageColumns[stage + 1] = rotated;
    }

    var columnDerivatives = makeArray(3, vector(0, 0, 0));
    for (var stage = 0; stage < stageCount; stage += 1)
    {
        const unitAxis = normalize(axes[stage].axis);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            var carried = axes[stage].rate * cross(unitAxis, stageColumns[stage + 1][columnIndex]);
            for (var outerStage = stage + 1; outerStage < stageCount; outerStage += 1)
            {
                carried = rotateAboutAxis(normalize(axes[outerStage].axis),
                    axes[outerStage].rate * t, carried);
            }
            columnDerivatives[columnIndex] = columnDerivatives[columnIndex] + carried;
        }
    }
    return { "columns" : stageColumns[stageCount], "columnDerivatives" : columnDerivatives };
}

/**
 * The frame that follows the path's tangent, and its exact derivative.
 *
 * The first column is the unit tangent; the second is a fixed world `reference` with its
 * tangent component removed and renormalized; the third closes the right-handed triple. Every
 * derivative is the quotient rule on the path's own velocity and acceleration, so the frame is
 * exactly orthonormal at every sampled node and its derivative is exact there too - which is
 * all the Hermite interpolation needs.
 *
 * Returns { columns, columnDerivatives, degenerate {boolean} } - degenerate when the tangent
 * runs into the reference or the path stalls, where this frame has no definition.
 */
function pathTangentFrameSample(pathSample is map, reference is Vector) returns map
{
    const speed = norm(pathSample.velocity);
    if (speed < 1e-12)
    {
        return { "columns" : [vector(1, 0, 0), vector(0, 1, 0), vector(0, 0, 1)],
                "columnDerivatives" : makeArray(3, vector(0, 0, 0)), "degenerate" : true };
    }
    const tangent = (1 / speed) * pathSample.velocity;
    const tangentDerivative = (1 / speed) *
        (pathSample.acceleration - dot(pathSample.acceleration, tangent) * tangent);

    const projected = reference - dot(reference, tangent) * tangent;
    const projectedNorm = norm(projected);
    if (projectedNorm < 1e-9)
    {
        return { "columns" : [tangent, vector(0, 1, 0), vector(0, 0, 1)],
                "columnDerivatives" : makeArray(3, vector(0, 0, 0)), "degenerate" : true };
    }
    const projectedDerivative = -(dot(reference, tangentDerivative) * tangent +
            dot(reference, tangent) * tangentDerivative);
    const up = (1 / projectedNorm) * projected;
    const upDerivative = (1 / projectedNorm) *
        (projectedDerivative - dot(projectedDerivative, up) * up);

    const side = cross(tangent, up);
    const sideDerivative = cross(tangentDerivative, up) + cross(tangent, upDerivative);
    return {
            "columns" : [tangent, up, side],
            "columnDerivatives" : [tangentDerivative, upDerivative, sideDerivative],
            "degenerate" : false
        };
}

/**
 * The fixture's rigid frame `A(t)` and `A'(t)` for one rotation kind, in closed form.
 *
 * FOLLOW_PATH_AND_ROLL composes the tangent frame with a roll about the tool's OWN first axis -
 * `A = F(t) Rx(a t)` - so the roll stays a roll however the path bends, and its derivative is
 * `F' Rx + a F Kx Rx`, both halves of which the two samplers above already produce exactly.
 *
 * Returns { columns, columnDerivatives, degenerate }.
 */
function sweepTestFrameSample(rotationType is SweepTestRotationType, rotationSpec is map,
    pathSample is map, t is number) returns map
{
    if (rotationType == SweepTestRotationType.NONE)
    {
        return { "columns" : [vector(1, 0, 0), vector(0, 1, 0), vector(0, 0, 1)],
                "columnDerivatives" : makeArray(3, vector(0, 0, 0)), "degenerate" : false };
    }
    if (rotationType == SweepTestRotationType.FOLLOW_PATH)
    {
        return pathTangentFrameSample(pathSample, rotationSpec.reference);
    }
    if (rotationType != SweepTestRotationType.FOLLOW_PATH_AND_ROLL)
    {
        const fixed = fixedAxisFrameSample(sweepTestRotationAxes(rotationType, rotationSpec.turnAngle), t);
        return mergeMaps(fixed, { "degenerate" : false });
    }

    const followFrame = pathTangentFrameSample(pathSample, rotationSpec.reference);
    const rollAxis = vector(1, 0, 0);
    const rollAngle = rotationSpec.turnAngle * t;
    var columns = makeArray(3, vector(0, 0, 0));
    var columnDerivatives = makeArray(3, vector(0, 0, 0));
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        const basis = vector(columnIndex == 0 ? 1 : 0, columnIndex == 1 ? 1 : 0,
                columnIndex == 2 ? 1 : 0);
        const rolled = rotateAboutAxis(rollAxis, rollAngle, basis);
        const rolledDerivative = rotationSpec.turnAngle * cross(rollAxis, rolled);
        columns[columnIndex] = applyColumnsToVector(followFrame.columns, rolled);
        columnDerivatives[columnIndex] =
            applyColumnsToVector(followFrame.columnDerivatives, rolled) +
            applyColumnsToVector(followFrame.columns, rolledDerivative);
    }
    return { "columns" : columns, "columnDerivatives" : columnDerivatives,
            "degenerate" : followFrame.degenerate };
}

/**
 * The clamped cubic B-spline that interpolates `values` with `derivatives` at the uniform nodes
 * `i / spans` of [0, 1].
 *
 * Per span the Bezier control points are `[f0, f0 + h f0' / 3, f1 - h f1' / 3, f1]`, and the
 * interior knots carry multiplicity 3, which is what makes the spline reproduce each span's
 * Hermite cubic exactly while sharing one knot vector with every other spline built the same
 * way. Sharing that knot vector is what spec 2.1 needs: it is the reason a fixed point's
 * trajectory and a transported edge are exact B-splines rather than fits.
 *
 * Returns a unit-stripped spline map { degree, knots, isRational, controlPoints }.
 */
function hermiteSplineFromNodes(values is array, derivatives is array, spans is number) returns map
{
    const controlCount = 3 * spans + 1;
    var controlPoints = makeArray(controlCount, vector(0, 0, 0));
    for (var span = 0; span < spans; span += 1)
    {
        const width = 1 / spans;
        controlPoints[3 * span] = values[span];
        controlPoints[3 * span + 1] = values[span] + (width / 3) * derivatives[span];
        controlPoints[3 * span + 2] = values[span + 1] - (width / 3) * derivatives[span + 1];
        controlPoints[3 * span + 3] = values[span + 1];
    }
    var knots = makeArray(controlCount + 4, 1);
    for (var index = 0; index < 4; index += 1)
    {
        knots[index] = 0;
    }
    for (var span = 1; span < spans; span += 1)
    {
        for (var repeat = 0; repeat < 3; repeat += 1)
        {
            knots[1 + 3 * span + repeat] = span / spans;
        }
    }
    return { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : controlPoints };
}

/**
 * The Hermite span count a path and rotation combination needs, from the frame's own MEASURED
 * angular rate rather than from an estimate of it.
 *
 * Cubic Hermite interpolation of a span of width `h` carries error `h^4 f''''/384`, and the
 * fourth derivative of a rotation column turning at rate `w` is `w^4`; orthonormality is a
 * product of two columns, so the defect runs about twice that. `2 (h w)^4 / 384 <= 1e-9` gives
 * `h w <= 0.021`, which HERMITE_SPAN_TURN_BUDGET rounds down.
 *
 * `w` is read off the analytic sampler: for a rotation `A' = K A` with `K` skew, so the
 * Frobenius norm of `A'` is `sqrt(2) |w|` exactly. A frame that follows the path has no closed
 * form for that rate - it depends on how near the tangent passes the projected reference - so
 * estimating it is what put a FOLLOW build's drift forty times over the bar, and measuring the
 * largest local rate is what sizes the spans for the case that actually binds.
 *
 * Returns { spans, worstRate {number} : the largest |w| seen, plain radians per unit t,
 * capped {boolean} : HERMITE_SPAN_LIMIT bound before the budget was met, so the stored rotation
 * will not hold 1e-9 and the drift check is expected to say so }.
 */
function sweepTestMotionSpans(pathType is SweepTestPathType, pathSpec is map,
    rotationType is SweepTestRotationType, rotationSpec is map) returns map
{
    const probeCount = 96;
    var worstRate = 0;
    for (var index = 0; index <= probeCount; index += 1)
    {
        const t = index / probeCount;
        const pathSample = sweepTestPathSample(pathType, pathSpec, t);
        const frameSample = sweepTestFrameSample(rotationType, rotationSpec, pathSample, t);
        var frobeniusSquared = 0;
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            frobeniusSquared += squaredNorm(frameSample.columnDerivatives[columnIndex]);
        }
        worstRate = max(worstRate, sqrt(0.5 * frobeniusSquared));
    }
    const wanted = max(4, ceil(worstRate / HERMITE_SPAN_TURN_BUDGET));
    return {
            "spans" : min(HERMITE_SPAN_LIMIT, wanted),
            "worstRate" : worstRate,
            "capped" : wanted > HERMITE_SPAN_LIMIT
        };
}

/**
 * The fixture motion for one path and rotation combination: four cubic B-splines on one shared
 * knot vector, in the unit-stripped column form the whole solid-sweep stack consumes.
 *
 * Returns { columnX, columnY, columnZ, translation, spans, degenerateSamples } - the last being
 * how many nodes the frame sampler could not define, which is zero on every combination the
 * dialog offers with a usable path.
 */
function sweepTestMotion(pathType is SweepTestPathType, pathSpec is map,
    rotationType is SweepTestRotationType, rotationSpec is map, spans is number) returns map
{
    var columnValues = [makeArray(spans + 1, vector(0, 0, 0)), makeArray(spans + 1, vector(0, 0, 0)),
            makeArray(spans + 1, vector(0, 0, 0))];
    var columnSlopes = [makeArray(spans + 1, vector(0, 0, 0)), makeArray(spans + 1, vector(0, 0, 0)),
            makeArray(spans + 1, vector(0, 0, 0))];
    var translationValues = makeArray(spans + 1, vector(0, 0, 0));
    var translationSlopes = makeArray(spans + 1, vector(0, 0, 0));
    var degenerateSamples = 0;
    for (var node = 0; node <= spans; node += 1)
    {
        const t = node / spans;
        const pathSample = sweepTestPathSample(pathType, pathSpec, t);
        const frameSample = sweepTestFrameSample(rotationType, rotationSpec, pathSample, t);
        if (frameSample.degenerate)
        {
            degenerateSamples += 1;
        }
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            columnValues[columnIndex][node] = frameSample.columns[columnIndex];
            columnSlopes[columnIndex][node] = frameSample.columnDerivatives[columnIndex];
        }
        translationValues[node] = pathSample.position;
        translationSlopes[node] = pathSample.velocity;
    }
    return {
            "columnX" : hermiteSplineFromNodes(columnValues[0], columnSlopes[0], spans),
            "columnY" : hermiteSplineFromNodes(columnValues[1], columnSlopes[1], spans),
            "columnZ" : hermiteSplineFromNodes(columnValues[2], columnSlopes[2], spans),
            "translation" : hermiteSplineFromNodes(translationValues, translationSlopes, spans),
            "spans" : spans,
            "degenerateSamples" : degenerateSamples
        };
}

/**
 * The cuboid tool: one solid body centred on the origin, optionally tilted off the
 * world axes about the (1, 1, 1) diagonal, read into the extraction records the sharp-feature
 * layer consumes.
 *
 * The tilt exists so a fixture can put every face normal off every path axis at once. An
 * axis-aligned cuboid translating along an axis leaves four faces with `<n, b'>` identically
 * zero, which is spec 6.4's SLIDING case rather than a grazing curve, and the plan reports it
 * as such instead of emitting a patch.
 *
 * Returns { body, faceRecords, coEdgeRecords, vertexRecords, halfSize }.
 */
function cuboidToolFixture(context is Context, id is Id, sizeVector is Vector, tiltAngle is ValueWithUnits,
    samplesPerEdge is number, extractTolerance is number) returns map
{
    const half = 0.5 * sizeVector;
    const sketchId = id + "sketch";
    const profileSketch = newSketchOnPlane(context, sketchId, {
                "sketchPlane" : plane(vector(0, 0, -half[2]) * meter, vector(0, 0, 1), vector(1, 0, 0))
            });
    skRectangle(profileSketch, "profile", {
                "firstCorner" : vector(-half[0], -half[1]) * meter,
                "secondCorner" : vector(half[0], half[1]) * meter
            });
    skSolve(profileSketch);
    opExtrude(context, id + "extrude", {
                "entities" : qNthElement(qSketchRegion(sketchId), 0),
                "direction" : vector(0, 0, 1),
                "startBound" : BoundingType.BLIND,
                "startDepth" : 0 * meter,
                "endBound" : BoundingType.BLIND,
                "endDepth" : sizeVector[2] * meter
            });
    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

    const body = qCreatedBy(id + "extrude", EntityType.BODY);
    if (abs(tiltAngle / degree) > 1e-9)
    {
        opTransform(context, id + "tilt", {
                    "bodies" : body,
                    "transform" : rotationAround(line(vector(0, 0, 0) * meter,
                                normalize(vector(1, 1, 1))), tiltAngle)
                });
    }

    const faceRecords = extractToolFaceRecords(context, body, extractTolerance);
    const coEdgeRecords = extractCoEdgeRecords(context, body, faceRecords, samplesPerEdge);
    const vertexRecords = extractVertexRecords(context, body, coEdgeRecords);
    return {
            "body" : body,
            "faceRecords" : faceRecords,
            "coEdgeRecords" : coEdgeRecords,
            "vertexRecords" : vertexRecords,
            "halfSize" : half
        };
}

/**
 * Exact envelope points at times NO patch station used, one pair per planned patch.
 *
 * The two fractions are deliberately off the uniform station grid this layer builds, so the
 * points measure the only error a ruled patch carries - the interpolation of its directrices in
 * t - rather than reproducing the samples the fit interpolated exactly. Both ruling ends and
 * the ruling midpoint are taken, because a ruled patch can be right at its boundaries and wrong
 * between them.
 *
 * Returns an array of unit-bearing points for evPointsDeviation.
 */
function freshPolyhedralEnvelopePoints(strippedMotion is map, coEdgeRecords is array,
    boundingByFace is array, plan is map, scanSamples is number) returns array
{
    var points = [];
    for (var patchPlan in plan.patches)
    {
        const faceBounding = patchPlan.patchKind == "face" ?
            boundingByFace[patchPlan.ownerIndex] : [];
        for (var fraction in [0.37, 0.71])
        {
            const t = patchPlan.tStart + fraction * (patchPlan.tEnd - patchPlan.tStart);
            const frozen = evaluateMotionSample(strippedMotion, t);
            var startPoint = undefined;
            var endPoint = undefined;
            if (patchPlan.patchKind == "edge")
            {
                const funnel = sharpEdgeFunnelSpansAtTime(frozen, coEdgeRecords[patchPlan.ownerIndex],
                    t, scanSamples);
                if (size(funnel.spans) == 1)
                {
                    const ends = sharpEdgeSpanEndPoints(frozen, coEdgeRecords[patchPlan.ownerIndex],
                        funnel.spans[0], t);
                    startPoint = ends[0];
                    endPoint = ends[1];
                }
            }
            else
            {
                const ruling = planarFaceCrossingsAtTime(frozen, coEdgeRecords, faceBounding, t,
                    scanSamples);
                for (var crossing in ruling.crossings)
                {
                    if (startPoint == undefined &&
                        crossing.edgeIndex == patchPlan.directrixSources[0].edgeIndex &&
                        crossing.side == patchPlan.directrixSources[0].side)
                    {
                        startPoint = crossing.worldPoint;
                    }
                    else if (endPoint == undefined &&
                        crossing.edgeIndex == patchPlan.directrixSources[1].edgeIndex &&
                        crossing.side == patchPlan.directrixSources[1].side)
                    {
                        endPoint = crossing.worldPoint;
                    }
                }
            }
            if (startPoint == undefined || endPoint == undefined)
            {
                continue;
            }
            points = append(points, meter * startPoint);
            points = append(points, meter * (0.5 * (startPoint + endPoint)));
            points = append(points, meter * endPoint);
        }
    }
    return points;
}

const SWEEP_CUBE_SIZE_BOUNDS =
{
    (meter) : [0.005, 0.06, 2],
    (centimeter) : 6,
    (millimeter) : 60,
    (inch) : 2.5
} as LengthBoundSpec;

const SWEEP_CUBE_TRAVEL_BOUNDS =
{
    (meter) : [0.005, 0.20, 5],
    (centimeter) : 20,
    (millimeter) : 200,
    (inch) : 8
} as LengthBoundSpec;

const SWEEP_CUBE_RADIUS_BOUNDS =
{
    (meter) : [0.005, 0.15, 5],
    (centimeter) : 15,
    (millimeter) : 150,
    (inch) : 6
} as LengthBoundSpec;

const SWEEP_CUBE_SWEEP_ANGLE_BOUNDS = { (degree) : [1, 90, 359] } as AngleBoundSpec;

const SWEEP_CUBE_TURN_ANGLE_BOUNDS = { (degree) : [-720, 60, 720] } as AngleBoundSpec;

const SWEEP_CUBE_TILT_BOUNDS = { (degree) : [0, 12, 180] } as AngleBoundSpec;

const SWEEP_CUBE_EDGE_SAMPLE_BOUNDS = { (unitless) : [3, 9, 129] } as IntegerBoundSpec;

const SWEEP_CUBE_STATION_BOUNDS = { (unitless) : [2, 9, 65] } as IntegerBoundSpec;

/**
 * Stations for the contact-function root scan that finds every owner's breakpoints. A funnel that
 * opens and closes again between two stations is invisible to a sign-change scan, and the segment
 * planned across it then has no funnel at some of its own stations - which is what
 * SWEEP_EDGE_FUNNEL_UNSTABLE reports. More stations is the direct answer and it is cheap: the
 * whole scan shares ONE frozen motion sample per station.
 */
const SWEEP_CUBE_BREAKPOINT_BOUNDS = { (unitless) : [33, 385, 3073] } as IntegerBoundSpec;

/**
 * The parameters that define the fixture itself - the path, the rotation, and the cube.
 *
 * Shared, because the sweep and the closure are two features over ONE fixture: the closure
 * rebuilds the motion rather than being handed it, and a motion rebuilt from different numbers
 * is a different motion. One predicate is what keeps the two dialogs from drifting apart.
 */
export predicate sweepCubeFixtureParameters(definition is map)
{
    annotation { "Name" : "Path type" }
    definition.pathType is SweepTestPathType;

    annotation { "Name" : "Rotation" }
    definition.rotationType is SweepTestRotationType;

    annotation { "Name" : "Cube edge length" }
    isLength(definition.cubeSize, SWEEP_CUBE_SIZE_BOUNDS);

    annotation { "Name" : "Cube tilt off the world axes" }
    isAngle(definition.cubeTilt, SWEEP_CUBE_TILT_BOUNDS);

    annotation { "Name" : "Travel" }
    isLength(definition.travel, SWEEP_CUBE_TRAVEL_BOUNDS);

    if (definition.pathType == SweepTestPathType.ARC ||
        definition.pathType == SweepTestPathType.HELIX)
    {
        annotation { "Name" : "Path radius" }
        isLength(definition.pathRadius, SWEEP_CUBE_RADIUS_BOUNDS);

        annotation { "Name" : "Path sweep angle" }
        isAngle(definition.pathSweepAngle, SWEEP_CUBE_SWEEP_ANGLE_BOUNDS);
    }

    if (definition.rotationType != SweepTestRotationType.NONE &&
        definition.rotationType != SweepTestRotationType.FOLLOW_PATH)
    {
        annotation { "Name" : "Total turn" }
        isAngle(definition.turnAngle, SWEEP_CUBE_TURN_ANGLE_BOUNDS);
    }

    annotation { "Name" : "Samples per tool edge" }
    isInteger(definition.samplesPerEdge, SWEEP_CUBE_EDGE_SAMPLE_BOUNDS);
}

/**
 * The fixture both features work from: the motion spline and the extracted cuboid tool.
 *
 * Deterministic in the parameters alone, which is what lets the closure feature rebuild exactly
 * the motion the sweep used instead of being handed it - a Query can cross a feature boundary,
 * a spline map cannot.
 *
 * Returns { pathSpec, frameReference, rotationSpec, spanBudget, spans, motion, cubeSize, tool }.
 */
function sweepTestCubeFixture(context is Context, id is Id, definition is map) returns map
{
    const pathSpec = {
            "travel" : definition.travel / meter,
            "radius" : (definition.pathRadius == undefined ? 0.15 * meter : definition.pathRadius) / meter,
            "sweepAngle" : (definition.pathSweepAngle == undefined ? 90 * degree :
                    definition.pathSweepAngle) / radian
        };
    const frameReference = sweepTestFrameReference(definition.pathType, pathSpec);
    const rotationSpec = {
            "turnAngle" : (definition.turnAngle == undefined ? 60 * degree : definition.turnAngle) / radian,
            "reference" : frameReference.reference
        };
    const spanBudget = sweepTestMotionSpans(definition.pathType, pathSpec,
        definition.rotationType, rotationSpec);
    const cubeSize = definition.cubeSize / meter;
    return {
            "pathSpec" : pathSpec,
            "frameReference" : frameReference,
            "rotationSpec" : rotationSpec,
            "spanBudget" : spanBudget,
            "spans" : spanBudget.spans,
            "motion" : sweepTestMotion(definition.pathType, pathSpec, definition.rotationType,
                rotationSpec, spanBudget.spans),
            "cubeSize" : cubeSize,
            "tool" : cuboidToolFixture(context, id + "tool", vector(cubeSize, cubeSize, cubeSize),
                definition.cubeTilt, definition.samplesPerEdge, 1e-7)
        };
}

annotation { "Feature Type Name" : "Sweep Rotating Cube Live Test" }
export const sweepRotatingCubeLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        sweepCubeFixtureParameters(definition);

        annotation { "Name" : "Stations per contact segment" }
        isInteger(definition.patchStations, SWEEP_CUBE_STATION_BOUNDS);

        annotation { "Name" : "Stations for the contact breakpoint scan" }
        isInteger(definition.breakpointStations, SWEEP_CUBE_BREAKPOINT_BOUNDS);

        annotation { "Name" : "Attempt closure to a solid" }
        definition.attemptClosure is boolean;

        if (definition.attemptClosure)
        {
            annotation { "Name" : "Run the closure up to" }
            definition.closureStage is SweepClosureStage;

            annotation { "Name" : "Measure the neighbour seams first" }
            definition.measureShellSeams is boolean;

            annotation { "Name" : "Ask opEnclose when the union does not close", "Default" : true }
            definition.encloseFallback is boolean;

            annotation { "Name" : "Try the sheet union before the enclose", "Default" : true }
            definition.unionFirst is boolean;
        }

        annotation { "Name" : "Keep the tool body" }
        definition.keepTool is boolean;

        annotation { "Name" : "Colour the sheets by what produced them", "Default" : true }
        definition.colorPatches is boolean;
    }
    {
        var tally = newCheckTally();
        const fixture = sweepTestCubeFixture(context, id, definition);
        const frameReference = fixture.frameReference;
        const spanBudget = fixture.spanBudget;
        const spans = fixture.spans;
        const motion = fixture.motion;
        println("[ROTATING CUBE] " ~ definition.pathType ~ " path, " ~ definition.rotationType ~
            " frame, reference " ~ toString(frameReference.reference) ~ " (worst tangent alignment " ~
            roundToPrecision(frameReference.worstAlignment, 4) ~ "), peak turn rate " ~
            roundToPrecision(spanBudget.worstRate, 4) ~ " rad, " ~ spans ~ " Hermite spans" ~
            (spanBudget.capped ? " (CAPPED - the drift bar will not be met)" : "") ~ " (" ~
            size(motion.columnX.controlPoints) ~ " control points per spline)");
        tally = checkThat(tally, motion.degenerateSamples == 0,
            motion.degenerateSamples ~ " motion node(s) had no frame - the tangent ran into the " ~
            "frame reference, so this path and rotation combination has no roll-free frame.");
        // Twice the node count puts a sample on every span MIDPOINT, which is where a cubic
        // Hermite span's error peaks.
        const drift = motionRotationDrift(motion, 2 * spans);
        println("[ROTATING CUBE] stored A(t) orthonormality drift " ~ drift);
        tally = checkWithin(tally, drift, 1e-9, "the stored rotation drift (spec 2.1 epsilon_motion)");

        // ---------- The tool ----------
        const cubeSize = fixture.cubeSize;
        const tool = fixture.tool;
        println("[ROTATING CUBE] " ~ summarizeFaceRecords(tool.faceRecords));
        println("[ROTATING CUBE] " ~ summarizeCoEdgeRecords(tool.coEdgeRecords));
        tally = checkThat(tally, size(tool.faceRecords) == 6 && size(tool.coEdgeRecords) == 12 &&
                size(tool.vertexRecords) == 8,
            "the cube extracted as " ~ size(tool.faceRecords) ~ " faces, " ~
            size(tool.coEdgeRecords) ~ " edges and " ~ size(tool.vertexRecords) ~
            " vertices, expected 6 / 12 / 8.");
        var planarFaces = 0;
        for (var faceRecord in tool.faceRecords)
        {
            if (faceRecord.surfaceClass == SweepSurfaceClass.PLANE)
            {
                planarFaces += 1;
            }
        }
        tally = checkThat(tally, planarFaces == size(tool.faceRecords),
            planarFaces ~ " of " ~ size(tool.faceRecords) ~ " faces classified PLANE - the ruled " ~
            "route needs every face planar.");

        // ---------- The plan ----------
        const plan = planPolyhedralEnvelope(motion, tool.faceRecords, tool.coEdgeRecords, {
                    "breakpointStations" : definition.breakpointStations == undefined ?
                        385 : definition.breakpointStations
                });
        println("[ROTATING CUBE] " ~ summarizePolyhedralPlan(plan));
        for (var entry in plan.skipped)
        {
            // Only the named refusals earn console space; a face or edge that simply does not
            // graze on a segment is the normal answer for most of a cube's boundary.
            if (startsWith(entry.reason, "SWEEP_"))
            {
                println("[ROTATING CUBE] skipped " ~ entry.patchKind ~ " " ~ entry.ownerIndex ~
                    " segment " ~ entry.segmentIndex ~ ": " ~ entry.reason);
            }
        }
        tally = checkThat(tally, size(plan.patches) > 0,
            "the plan found no envelope patch at all across " ~ plan.segmentCounts["face"] ~
            " face and " ~ plan.segmentCounts["edge"] ~ " edge segment(s).");
        tally = checkThat(tally, size(plan.slidingFaces) == 0,
            "face(s) " ~ toString(plan.slidingFaces) ~ " slide: the contact function is " ~
            "identically zero there, so their envelope is a transported face and not a ruled " ~
            "patch. Tilt the cube or add rotation.");

        // Every sharp edge that carries a funnel must be a CONVEX edge of the cube, and every
        // cube edge is convex, so a plan that emits no edge sheet at all has lost the sharp
        // features this layer exists for.
        var edgePatchCount = 0;
        for (var patchPlan in plan.patches)
        {
            if (patchPlan.patchKind == "edge")
            {
                edgePatchCount += 1;
            }
        }
        tally = checkThat(tally, edgePatchCount > 0,
            "no sharp-edge sheet was planned; a swept cube's envelope is made of them.");

        // ---------- Emission ----------
        var boundingByFace = makeArray(size(tool.faceRecords));
        for (var faceIndex = 0; faceIndex < size(tool.faceRecords); faceIndex += 1)
        {
            boundingByFace[faceIndex] = faceBoundingCoEdges(tool.coEdgeRecords, faceIndex);
        }
        const nextId = getUnstableIncrementingId(id + "patch");
        const emission = emitPolyhedralEnvelope(context, nextId, motion, tool.faceRecords,
            tool.coEdgeRecords, plan, {
                    "stationCount" : definition.patchStations,
                    "colorPatches" : definition.colorPatches,
                    "namePatches" : !definition.attemptClosure
                });
        println("[ROTATING CUBE] " ~ summarizePolyhedralEmission(emission));
        if (emission.refusedCount > 0)
        {
            // A refusal is only diagnosable next to the patches that did NOT refuse, so when one
            // happens every patch gets a line and the first refused one gets its whole sample grid.
            for (var line in describePolyhedralPatches(emission))
            {
                println("[ROTATING CUBE PATCH] " ~ line);
            }
            var dumped = false;
            for (var report in emission.patchReports)
            {
                if (!report.refused || dumped)
                {
                    continue;
                }
                dumped = true;
                const refit = fitRuledEnvelopePatch(motion, tool.coEdgeRecords,
                    report.patchKind == "face" ? boundingByFace[report.ownerIndex] : [],
                    report, { "stationCount" : definition.patchStations });
                println("[ROTATING CUBE GRID] " ~ report.patchKind ~ " " ~ report.ownerIndex ~
                    " segment " ~ report.segmentIndex ~ ", " ~ (refit.failed ? "refit failed" :
                        (size(refit.grid) ~ " station(s)")));
                if (!refit.failed)
                {
                    for (var stationIndex = 0; stationIndex < size(refit.grid); stationIndex += 1)
                    {
                        println("[ROTATING CUBE GRID]   t " ~ roundToPrecision(refit.stations[stationIndex], 6) ~
                            "  a " ~ toString(refit.grid[stationIndex][0]) ~
                            "  b " ~ toString(refit.grid[stationIndex][size(refit.grid[0]) - 1]));
                    }
                    probeRefusedRuledNet(context, id + "probe", refit.grid, tally);
                }
            }
        }
        tally = checkThat(tally, emission.refusedCount == 0,
            emission.refusedCount ~ " of " ~ size(plan.patches) ~ " patch(es) were refused.");
        tally = checkThat(tally, emission.emittedCount == size(plan.patches),
            "only " ~ emission.emittedCount ~ " of " ~ size(plan.patches) ~ " planned patches emitted.");
        if (emission.emittedCount == 0)
        {
            reportCheckTally(context, id, "ROTATING CUBE", tally,
                "nothing was emitted, so nothing could be measured.");
            return;
        }
        tally = checkThat(tally, emission.shortestPatchExtent > 1e-5,
            "the smallest patch extent is " ~ emission.shortestPatchExtent ~
            " m, under the 1e-5 m sliver floor.");
        tally = checkThat(tally, emission.interiorCollapses == 0,
            emission.interiorCollapses ~ " ruling(s) collapsed away from a patch's own ends, " ~
            "which is a pinch in the patch's middle rather than a taper into a corner.");

        // ---------- Certification against fresh envelope points ----------
        const freshPoints = freshPolyhedralEnvelopePoints(motion, tool.coEdgeRecords,
            boundingByFace, plan, 0);
        const sheetBodies = qUnion(emission.bodies);
        var reportedDeviation = -1;
        if (size(freshPoints) > 0)
        {
            const deviation = evPointsDeviation(context, {
                            "points" : freshPoints,
                            "topologies" : sheetBodies
                        })[0].deviation / meter;
            reportedDeviation = deviation;
            println("[ROTATING CUBE] sheet deviation against " ~ size(freshPoints) ~
                " fresh envelope points: " ~ deviation ~ " m");
            tally = checkWithin(tally, deviation, 1e-5, "the emitted sheets' envelope deviation");

            // The control. A deviation of zero is the answer this measurement WANTS, and an
            // instrument that cannot fail returns it for free - so the tool's own centre at
            // mid-sweep, which sits an inradius deep inside the swept volume, is measured against
            // the same sheets by the same call. It has to come back far from zero, or the zero
            // above was the query resolving to nothing rather than the points landing on a surface.
            const interiorPoint = evaluateMotionSample(motion, 0.5).translation;
            const controlDeviation = evPointsDeviation(context, {
                            "points" : [meter * interiorPoint],
                            "topologies" : sheetBodies
                        })[0].deviation / meter;
            println("[ROTATING CUBE] control: the tool centre at t = 0.5 is " ~ controlDeviation ~
                " m from the same sheets");
            tally = checkThat(tally, controlDeviation > 0.1 * cubeSize,
                "the deviation control reads " ~ controlDeviation ~ " m for a point an inradius " ~
                "inside the swept volume, so the measurement above is not measuring.");
        }
        else
        {
            tally = checkThat(tally, false, "no fresh envelope point could be built to measure against.");
        }

        // The headline, written onto the tool body's NAME.
        //
        // printlns reach a reader only through a Part Studio watcher, and a watcher is a limited
        // resource that anyone with the document open is already spending one of - so a run whose
        // numbers are needed after the fact cannot depend on one. A part name is readable from the
        // parts list in a single API call, always.
        const headline = "RUN " ~ definition.pathType ~ "/" ~ definition.rotationType ~
            " | " ~ emission.emittedCount ~ " of " ~ size(plan.patches) ~ " emitted, " ~
            emission.refusedCount ~ " refused, " ~ emission.refittedCount ~ " refit | " ~
            size(plan.skipped) ~ " skipped | deviation " ~
            roundToPrecision(reportedDeviation, 9) ~ " | drift " ~ roundToPrecision(drift, 12);
        if (definition.keepTool)
        {
            setProperty(context, {
                        "entities" : tool.body,
                        "propertyType" : PropertyType.NAME,
                        "value" : headline
                    });
        }

        if (!definition.attemptClosure)
        {
            if (!definition.keepTool)
            {
                opDeleteBodies(context, id + "deleteTool", { "entities" : tool.body });
            }
            reportCheckTally(context, id, "ROTATING CUBE", tally,
                "a " ~ definition.rotationType ~ " cube sweep along a " ~ definition.pathType ~
                " path emitted " ~ emission.emittedCount ~ " certified envelope sheet(s).");
            return;
        }

        // ---------- Closure ----------
        //
        // The attempt is guarded, and that is not defensiveness. Spec 9.3 says a sweep that cannot
        // be closed reports the CERTIFIED OPEN SHEET SET; a feature that dies instead reports
        // nothing at all, because the regeneration rolls back whole and the parts list comes back
        // empty - indistinguishable from a run that never happened. The sheets are the result
        // whether or not the caps sew to them.
        var closureLine = "CLOSE " ~ definition.pathType ~ "/" ~ definition.rotationType ~ " | ";
        var closureFailure = "";
        try
        {
            const startCurves = polyhedralCapContactCurves(motion, tool.faceRecords, tool.coEdgeRecords, 0, 0);
            const endCurves = polyhedralCapContactCurves(motion, tool.faceRecords, tool.coEdgeRecords, 1, 0);
            closureLine = closureLine ~ size(startCurves.curves) ~ "+" ~ size(endCurves.curves) ~
            " cap contact segment(s)";
            for (var note in concatenateArrays([startCurves.skipped, endCurves.skipped]))
            {
                closureLine = closureLine ~ " | skipped " ~ note;
            }

            // Whether the shell CAN be sewn, asked before the union is asked to sew it. It is
            // its own switch because it is not free: every sheet's every edge is sampled against
            // every OTHER sheet's edges, which is quadratic in the sheet count.
            if (definition.measureShellSeams)
            {
                const lateral = matchShellSeamEdges(context, sheetBodies,
                    SHELL_SEAM_MATCH_TOLERANCE);
                closureLine = closureLine ~ " | lateral seams " ~ toString(lateral.pairCount) ~
                " pair(s) of " ~ toString(lateral.edgeCount) ~ " free edge(s), " ~
                toString(lateral.unmatchedCount) ~ " unmatched, worst pair " ~
                toString(roundToPrecision(lateral.worstPairGap, 9));
            }

            const assembly = assembleSweptSolid(context, id + "assembly", {
                        "trace" : true,
                        "fallbackToEnclose" : definition.encloseFallback,
                        "matchSeams" : definition.measureShellSeams,
                        "tryUnion" : definition.unionFirst,
                        "deleteWires" : false,
                        "stopAfter" : closureStageName(definition.closureStage == undefined ?
                            SweepClosureStage.KNIT : definition.closureStage),
                        "toolBody" : tool.body,
                        "shellBodies" : sheetBodies,
                        "caps" : [
                            {
                                "motionSample" : evaluateMotionSample(motion, 0),
                                "isStart" : true,
                                "contactCurves" : startCurves.curves
                            },
                            {
                                "motionSample" : evaluateMotionSample(motion, 1),
                                "isStart" : false,
                                "contactCurves" : endCurves.curves
                            }
                        ]
                    });
            // Appended one piece at a time, and every piece through `toString`. A line built as
            // one concatenation is lost WHOLE when any term in it is undefined - the assignment
            // never happens, so what survives is the line as it stood before the assembly ran,
            // which reads as though the assembly itself never returned.
            closureLine = closureLine ~ (assembly.failed == true ?
                (" | FAILED " ~ toString(assembly.reason)) : "");
            closureLine = closureLine ~ (assembly.stoppedAt != "" ?
                (" | stopped after " ~ toString(assembly.stoppedAt)) : "");
            if (assembly.failed != true && assembly.stoppedAt == "")
            {
                closureLine = closureLine ~ " | closed by " ~ toString(assembly.knit.closedBy) ~
                ", " ~ toString(assembly.knit.bodyCountBefore) ~ " sheets in, " ~
                toString(assembly.knit.solidCount) ~ " solid";
                closureLine = closureLine ~ (assembly.quality == undefined ? ", quality unmeasured" :
                    (", volume " ~ toString(roundToPrecision(assembly.quality.volume, 9)) ~
                        " m^3, " ~ toString(assembly.quality.faceCount) ~ " faces"));
            }
            closureLine = closureLine ~ " | worst cap seam " ~
            toString(roundToPrecision(assembly.worstSeamGap, 9));
            if (assembly.seams != undefined)
            {
                closureLine = closureLine ~ " | " ~ toString(assembly.seams.pairCount) ~
                " seam pair(s) of " ~ toString(assembly.seams.edgeCount) ~ " free edge(s), " ~
                toString(assembly.seams.unmatchedCount) ~ " unmatched, worst pair " ~
                toString(roundToPrecision(assembly.seams.worstPairGap, 9));
            }
            // Which cap failed, and how many faces it kept, is the whole of the diagnosis - and it
            // has to survive on a body NAME, because a run read after the fact had no watcher to
            // carry its printlns.
            for (var report in assembly.capReports)
            {
                closureLine = closureLine ~ " | cap " ~ toString(report.capIndex) ~ " " ~
                toString(report.stage) ~ " " ~
                toString(report.keptFaceCount == undefined ? 0 : report.keptFaceCount) ~ " kept/" ~
                toString(report.deletedFaceCount == undefined ? 0 : report.deletedFaceCount) ~ " cut" ~
                (report.reason == undefined || report.reason == "" ? "" : (" " ~ toString(report.reason)));
            }
            if (!assembly.failed)
            {
                try silent
                {
                    setProperty(context, {
                                "entities" : assembly.solidBody,
                                "propertyType" : PropertyType.NAME,
                                "value" : closureLine
                            });
                }
            }
            closureFailure = assembly.failed ? assembly.reason : "";
            if (!assembly.failed && assembly.stoppedAt == "")
            {
                tally = checkThat(tally, assembly.knit.solidCount == 1,
                    "the knit produced " ~ assembly.knit.solidCount ~ " solid bodies.");
                const toolVolume = cubeSize ^ 3;
                tally = checkThat(tally, assembly.quality != undefined &&
                    assembly.quality.volume > toolVolume,
                    "the swept volume " ~ (assembly.quality == undefined ? "(unmeasured)" :
                        ("" ~ assembly.quality.volume)) ~ " m^3 is not larger than the " ~
                    "tool's own " ~ toolVolume ~ " m^3, which no sweep of a moving tool can be.");
            }
        }
        catch (error)
        {
            closureFailure = "THREW " ~ toString(error);
            closureLine = closureLine ~ " | " ~ closureFailure;
        }
        println("[ROTATING CUBE] " ~ closureLine);
        if (definition.keepTool)
        {
            setProperty(context, {
                        "entities" : tool.body,
                        "propertyType" : PropertyType.NAME,
                        "value" : headline ~ " || " ~ closureLine
                    });
        }
        tally = checkThat(tally, closureFailure == "",
            "the assembly did not close: " ~ closureFailure);

        reportCheckTally(context, id, "ROTATING CUBE", tally,
            "a " ~ definition.rotationType ~ " cube sweep along a " ~ definition.pathType ~
            " path emitted " ~ emission.emittedCount ~ " certified envelope sheet(s) and closed.");
    }, {
            "pathType" : SweepTestPathType.ARC,
            "rotationType" : SweepTestRotationType.TWO_AXIS,
            "cubeSize" : 0.06 * meter,
            "cubeTilt" : 12 * degree,
            "travel" : 0.20 * meter,
            "pathRadius" : 0.15 * meter,
            "pathSweepAngle" : 90 * degree,
            "turnAngle" : 60 * degree,
            "samplesPerEdge" : 9,
            "patchStations" : 9,
            "breakpointStations" : 385,
            "attemptClosure" : false,
            "closureStage" : SweepClosureStage.KNIT,
            "measureShellSeams" : false,
            "encloseFallback" : true,
            "unionFirst" : true,
            "keepTool" : true,
            "colorPatches" : true
        });


/**
 * What a kernel that answers CANNOT_MAKE_BSPLINESURFACE actually objects to, asked by variation
 * rather than by reasoning.
 *
 * The same sample grid is offered four ways - as fitted, as a degree-1 polyline surface, as the
 * four corners alone, and scaled a hundredfold about its own centroid. Which of them the kernel
 * takes separates the three candidate causes that a refusal alone cannot: the interpolation's
 * degree, the patch's shape, and its size against the kernel's own resolution. Every body the
 * probe makes is deleted again; it is asking a question, not emitting geometry.
 */
function probeRefusedRuledNet(context is Context, id is Id, grid is array, tally is map)
{
    const stationCount = size(grid);
    const columnCount = size(grid[0]);
    var centroid = vector(0, 0, 0);
    for (var row in grid)
    {
        for (var point in row)
        {
            centroid = centroid + point;
        }
    }
    centroid = (1 / (stationCount * columnCount)) * centroid;

    var scaledGrid = makeArray(stationCount);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        var row = makeArray(columnCount, vector(0, 0, 0));
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            row[columnIndex] = centroid + 100 * (grid[stationIndex][columnIndex] - centroid);
        }
        scaledGrid[stationIndex] = row;
    }
    const cornerGrid = [grid[0], grid[stationCount - 1]];

    const variants = [
            { "name" : "as fitted", "grid" : grid, "uDegree" : min(3, stationCount - 1) },
            { "name" : "degree 1 in u", "grid" : grid, "uDegree" : 1 },
            { "name" : "corners only", "grid" : cornerGrid, "uDegree" : 1 },
            { "name" : "scaled 100x", "grid" : scaledGrid, "uDegree" : min(3, stationCount - 1) }
        ];
    var probeIndex = 0;
    for (var variant in variants)
    {
        probeIndex += 1;
        const probeId = id + ("variant" ~ probeIndex);
        var surface = undefined;
        try
        {
            surface = interpolateFitGrid(variant.grid, variant.uDegree, min(3, columnCount - 1));
        }
        catch (error)
        {
            println("[ROTATING CUBE PROBE] " ~ variant.name ~ ": interpolation refused - " ~ toString(error));
        }
        if (surface == undefined)
        {
            continue;
        }
        const emission = emitFitSurfacePatch(context, probeId, surface);
        println("[ROTATING CUBE PROBE] " ~ variant.name ~ " (" ~ size(surface.controlPoints) ~ "x" ~
            size(surface.controlPoints[0]) ~ " net, uDegree " ~ surface.uDegree ~ "): " ~
            (emission.refused ? ("REFUSED " ~ emission.reason) : "accepted"));
        if (!emission.refused)
        {
            opDeleteBodies(context, probeId + "delete", {
                        "entities" : qCreatedBy(emission.id, EntityType.BODY)
                    });
        }
    }
}


/**
 * The SECOND STAGE of the sweep: take the certified sheets a previous feature emitted, census
 * their seams, and knit them into one solid.
 *
 * It is a separate feature because the knit does not survive in the first one. A boolean or an
 * enclose over sheets created in the SAME feature evaluation takes the whole regeneration down
 * here - not a refusal, not a catchable throw, but "Error regenerating" with every body the
 * feature made rolled back; the identical call over sheets a PREVIOUS feature emitted closes the
 * shell in seconds. Measured on the line fixture at every size tried, with the union skipped, with
 * the caps excluded, and with the shell named by an independent query: the split is what decides
 * it, not the cost, not the operation, and not which bodies are handed over.
 *
 * With `useExistingSheets` off it runs on two extracted faces of a cuboid instead - the smallest
 * shell there is, with a known answer - which is how the matcher itself gets tested apart from
 * any sweep.
 */
annotation { "Feature Type Name" : "Close Swept Shell" }
export const sweepShellClosureTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Close the sheets already in this Part Studio" }
        definition.useExistingSheets is boolean;

        if (definition.useExistingSheets)
        {
            sweepCubeFixtureParameters(definition);

            annotation { "Name" : "Cap the ends" , "Default" : true }
            definition.buildCaps is boolean;
        }
        else
        {
            annotation { "Name" : "Cube size" }
            isLength(definition.cubeSize, LENGTH_BOUNDS);
        }

        annotation { "Name" : "Census the seams first" , "Default" : true }
        definition.evaluateMidpoints is boolean;

        annotation { "Name" : "Knit into a solid" }
        definition.knitExisting is boolean;
    }
    {
        var tally = newCheckTally();
        // Pointed at the sheets a PREVIOUS feature left behind, the probe runs on its own step
        // budget and its own operation list: a failure here is the matcher's, not the sweep's,
        // and the sweep that produced the shell does not have to be re-run to ask again.
        if (definition.useExistingSheets)
        {
            // Frozen to the bodies that exist NOW. The caps this feature is about to build
            // become sheets themselves the moment they are trimmed, and a live query would then
            // include them in the set it was supposed to be closing.
            const existing = qUnion(evaluateQuery(context,
                    qBodyType(qEverything(EntityType.BODY), BodyType.SHEET)));
            const sheetCount = size(evaluateQuery(context, existing));
            const freeEdges = evaluateQuery(context, qEdgeTopologyFilter(
                        qOwnedByBody(existing, EntityType.EDGE), EdgeTopology.ONE_SIDED));
            var existingLine = "CLOSE: " ~ sheetCount ~ " sheet(s), " ~ size(freeEdges) ~
            " one-sided edge(s)";
            if (definition.evaluateMidpoints)
            {
                var asked = 0;
                var answered = 0;
                for (var freeEdge in freeEdges)
                {
                    asked += 1;
                    try silent
                    {
                        evEdgeTangentLine(context, {
                                    "edge" : freeEdge,
                                    "parameter" : 0.5,
                                    "arcLengthParameterization" : false
                                });
                        answered += 1;
                    }
                }
                existingLine = existingLine ~ " | " ~ answered ~ " of " ~ asked ~ " midpoints answered";
            }
            // The CAPS are built here too, not by the sweep. Every operation the sweep runs
            // after emitting its sheets is one it may not survive - the line fixture reaches the
            // trim and the helix, at twice the patch count, does not reach the contact wires -
            // and the pattern is the same either way: the regeneration dies whole with no stage
            // named. A second feature starts with the ceiling clear. Nothing has to cross the
            // boundary but the sheets themselves, because the motion is deterministic in the
            // dialog's own numbers and this feature rebuilds it from the same predicate.
            var assembly = undefined;
            if (definition.buildCaps)
            {
                const fixture = sweepTestCubeFixture(context, id, definition);
                const startCurves = polyhedralCapContactCurves(fixture.motion,
                    fixture.tool.faceRecords, fixture.tool.coEdgeRecords, 0, 0);
                const endCurves = polyhedralCapContactCurves(fixture.motion,
                    fixture.tool.faceRecords, fixture.tool.coEdgeRecords, 1, 0);
                existingLine = existingLine ~ " | caps " ~ toString(size(startCurves.curves)) ~
                "+" ~ toString(size(endCurves.curves)) ~ " contact segment(s)";
                assembly = assembleSweptSolid(context, id + "assembly", {
                            "toolBody" : fixture.tool.body,
                            "shellBodies" : existing,
                            "shellQueryOverride" : undefined,
                            "makeSolid" : true,
                            "matchSeams" : false,
                            "measureSeams" : false,
                            // Always stopped at the trim. The knit runs afterwards, from here,
                            // because it has to be handed the seam matches and the seams do not
                            // exist to be matched until the caps are trimmed into the shell.
                            "stopAfter" : "trim",
                            "caps" : [
                                {
                                    "motionSample" : evaluateMotionSample(fixture.motion, 0),
                                    "isStart" : true,
                                    "contactCurves" : startCurves.curves
                                },
                                {
                                    "motionSample" : evaluateMotionSample(fixture.motion, 1),
                                    "isStart" : false,
                                    "contactCurves" : endCurves.curves
                                }
                            ]
                        });
                existingLine = existingLine ~ " | assembly " ~
                (assembly.failed == true ? ("FAILED " ~ toString(assembly.reason)) :
                    ("ok, stopped after " ~ toString(assembly.stoppedAt)));
                for (var report in assembly.capReports)
                {
                    existingLine = existingLine ~ " | cap " ~ toString(report.capIndex) ~ " " ~
                    toString(report.stage) ~ " " ~
                    toString(report.keptFaceCount == undefined ? 0 : report.keptFaceCount) ~
                    " kept/" ~
                    toString(report.deletedFaceCount == undefined ? 0 : report.deletedFaceCount) ~
                    " cut";
                }
                opDeleteBodies(context, id + "dropFixtureTool", { "entities" : fixture.tool.body });
            }

            // The whole shell, caps included, and the census taken over it. A union handed no
            // matches has to find them, and finding them means intersecting tangent sheets pair
            // by pair - which is what takes the regeneration down rather than refusing. Handing
            // the union the pairs is the difference between a knit and a dead feature.
            const shell = assembly == undefined ? existing : assembly.shellBodies;
            var seams = undefined;
            if (definition.evaluateMidpoints)
            {
                seams = matchShellSeamEdges(context, shell, SHELL_SEAM_MATCH_TOLERANCE);
                existingLine = existingLine ~ " | shell seams " ~ toString(seams.pairCount) ~
                " pair(s) of " ~ toString(seams.edgeCount) ~ ", " ~
                toString(seams.unmatchedCount) ~ " unmatched, worst " ~
                toString(roundToPrecision(seams.worstPairGap, 9));
            }

            if (definition.knitExisting)
            {
                // The union runs only when it has been handed its matches. Without them it has
                // to find where the sheets meet, and they meet tangentially - so it intersects
                // them pair by pair and the regeneration dies. The enclose asks a different
                // question, what region the sheets bound, and asks it of the whole set at once.
                const knit = knitSweptShell(context, id + "knit", shell, {
                            "makeSolid" : true,
                            "fallbackToEnclose" : true,
                            "tryUnion" : seams != undefined,
                            "matchSeams" : seams != undefined,
                            "seamMatches" : seams == undefined ? undefined : seams.matches
                        });
                if (knit != undefined)
                {
                    existingLine = existingLine ~ " | knit " ~
                    (knit.failed ? ("FAILED " ~ toString(knit.reason)) :
                        ("closed by " ~ toString(knit.closedBy))) ~ ", " ~
                    toString(knit.bodyCountBefore) ~ " in, " ~ toString(knit.bodyCountAfter) ~
                    " out, " ~ toString(knit.solidCount) ~ " solid";
                    if (!knit.failed)
                    {
                        // Counts by query and ONE volume call. `summarizeSolidQuality` walks
                        // every face and every edge for their extremes, and a few hundred more
                        // evaluations is exactly what this feature cannot afford on top of the
                        // knit - the regeneration dies whole and reports nothing.
                        const faceCount = size(evaluateQuery(context,
                                qOwnedByBody(knit.solidBody, EntityType.FACE)));
                        const edgeCount = size(evaluateQuery(context,
                                qOwnedByBody(knit.solidBody, EntityType.EDGE)));
                        existingLine = existingLine ~ " | " ~ toString(faceCount) ~ " faces, " ~
                        toString(edgeCount) ~ " edges, volume " ~
                        toString(roundToPrecision(evVolume(context,
                                    { "entities" : knit.solidBody }) / meter ^ 3, 9)) ~ " m^3";
                        setProperty(context, {
                                    "entities" : knit.solidBody,
                                    "propertyType" : PropertyType.NAME,
                                    "value" : "SWEPT SOLID"
                                });
                        // The sheets have done their job. They also sit exactly on the solid's
                        // own faces, so leaving them in place hides the thing that was built.
                        opDeleteBodies(context, id + "dropSheets", {
                                    "entities" : qSubtraction(qBodyType(qEverything(EntityType.BODY),
                                            BodyType.SHEET), knit.solidBody)
                                });
                    }
                }
            }

            // Onto a body the probe MAKES, not onto one it found. A part's name in the parts
            // list belongs to the feature that created the body; a later feature setting the
            // same property leaves the list showing the original, so a probe that renames
            // someone else's body reports nothing and looks like a probe that never ran.
            opPolyline(context, id + "marker", {
                        "points" : [vector(0, 0, 0) * meter, vector(0, 0, 0.001) * meter]
                    });
            setProperty(context, {
                        "entities" : qCreatedBy(id + "marker", EntityType.BODY),
                        "propertyType" : PropertyType.NAME,
                        "value" : existingLine
                    });
            reportCheckTally(context, id, "SHELL CLOSURE", tally, existingLine);
            return;
        }
        fCuboid(context, id + "box", {
                    "corner1" : vector(0, 0, 0) * meter,
                    "corner2" : vector(1, 1, 1) * definition.cubeSize
                });
        const faces = evaluateQuery(context, qCreatedBy(id + "box", EntityType.FACE));
        opExtractSurface(context, id + "sheetA", {
                    "faces" : faces[0],
                    "tangentPropagation" : false
                });
        opExtractSurface(context, id + "sheetB", {
                    "faces" : faces[1],
                    "tangentPropagation" : false
                });
        opDeleteBodies(context, id + "dropBox", {
                    "entities" : qCreatedBy(id + "box", EntityType.BODY)
                });
        const sheets = qUnion([qCreatedBy(id + "sheetA", EntityType.BODY),
                    qCreatedBy(id + "sheetB", EntityType.BODY)]);

        const bodyCount = size(evaluateQuery(context, sheets));
        const edgeCount = size(evaluateQuery(context, qEdgeTopologyFilter(
                        qOwnedByBody(sheets, EntityType.EDGE), EdgeTopology.ONE_SIDED)));
        var line = "CUBOID PROBE " ~ bodyCount ~ " sheet(s), " ~ edgeCount ~ " one-sided edge(s)";

        var seams = undefined;
        try
        {
            seams = matchShellSeamEdges(context, sheets, SHELL_SEAM_MATCH_TOLERANCE);
        }
        catch (error)
        {
            line = line ~ " | THREW " ~ toString(error);
        }
        if (seams != undefined)
        {
            line = line ~ " | " ~ toString(seams.pairCount) ~ " pair(s), " ~
            toString(seams.unmatchedCount) ~ " unmatched, worst " ~
            toString(roundToPrecision(seams.worstPairGap, 9));
        }
        setProperty(context, {
                    "entities" : qCreatedBy(id + "sheetA", EntityType.BODY),
                    "propertyType" : PropertyType.NAME,
                    "value" : line
                });
        tally = checkThat(tally, seams != undefined && seams.pairCount == 1,
            "the two extracted faces did not match on exactly one seam.");
        reportCheckTally(context, id, "SHELL CLOSURE", tally, line);
    }, { "useExistingSheets" : false, "evaluateMidpoints" : true, "knitExisting" : false,
            "buildCaps" : true, "cubeSize" : 0.05 * meter,
            "pathType" : SweepTestPathType.ARC,
            "rotationType" : SweepTestRotationType.TWO_AXIS,
            "cubeTilt" : 12 * degree, "travel" : 0.20 * meter, "pathRadius" : 0.15 * meter,
            "pathSweepAngle" : 90 * degree, "turnAngle" : 60 * degree, "samplesPerEdge" : 9 });


/**
 * The THIRD stage: census the seams of every sheet in the Part Studio and knit them into one
 * solid. Nothing else.
 *
 * Its own feature because the ceiling is per evaluation and this stage is the one that keeps
 * hitting it. Emitting the sheets, capping the ends and knitting the shell are three pieces of
 * work that each run fine from a clear start and take the regeneration down when stacked - not
 * by refusing, but by dying whole with no stage named. Where the boundary falls moves with the
 * fixture: the line cube reaches the trim in the sweep, the helix at twice the patch count does
 * not reach the contact wires. Three features is what makes the pipeline independent of where it
 * falls.
 */
annotation { "Feature Type Name" : "Knit Swept Shell" }
export const sweepShellKnitTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Census the seams and hand them to the union", "Default" : true }
        definition.censusSeams is boolean;

        annotation { "Name" : "Delete the sheets once the solid exists", "Default" : true }
        definition.dropSheets is boolean;
    }
    {
        var tally = newCheckTally();
        const shell = qUnion(evaluateQuery(context,
                qBodyType(qEverything(EntityType.BODY), BodyType.SHEET)));
        const sheetCount = size(evaluateQuery(context, shell));
        var line = "KNIT: " ~ sheetCount ~ " sheet(s)";
        tally = checkThat(tally, sheetCount > 0, "there are no sheets in this Part Studio to knit.");

        var seams = undefined;
        if (definition.censusSeams && sheetCount > 0)
        {
            seams = matchShellSeamEdges(context, shell, SHELL_SEAM_MATCH_TOLERANCE);
            line = line ~ ", " ~ seams.pairCount ~ " seam pair(s) of " ~ seams.edgeCount ~
            " free edge(s), " ~ seams.unmatchedCount ~ " unmatched, worst " ~
            roundToPrecision(seams.worstPairGap, 9);
        }

        if (sheetCount > 0)
        {
            const knit = knitSweptShell(context, id + "knit", shell, {
                        "makeSolid" : true,
                        "fallbackToEnclose" : true,
                        "tryUnion" : seams != undefined,
                        "matchSeams" : seams != undefined,
                        "seamMatches" : seams == undefined ? undefined : seams.matches
                    });
            line = line ~ " | " ~ (knit.failed ? ("FAILED " ~ toString(knit.reason)) :
                    ("closed by " ~ toString(knit.closedBy))) ~ ", " ~
            toString(knit.bodyCountAfter) ~ " body/bodies out, " ~ toString(knit.solidCount) ~
            " solid";
            tally = checkThat(tally, !knit.failed, "the shell did not close: " ~ toString(knit.reason));
            if (!knit.failed)
            {
                const faceCount = size(evaluateQuery(context,
                        qOwnedByBody(knit.solidBody, EntityType.FACE)));
                const volume = evVolume(context, { "entities" : knit.solidBody }) / meter ^ 3;
                line = line ~ " | " ~ faceCount ~ " faces, volume " ~
                roundToPrecision(volume, 9) ~ " m^3";
                tally = checkThat(tally, volume > 0, "the closed body encloses no volume.");
                setProperty(context, {
                            "entities" : knit.solidBody,
                            "propertyType" : PropertyType.NAME,
                            "value" : "SWEPT SOLID"
                        });
                if (definition.dropSheets)
                {
                    opDeleteBodies(context, id + "dropSheets", {
                                "entities" : qSubtraction(qBodyType(qEverything(EntityType.BODY),
                                        BodyType.SHEET), knit.solidBody)
                            });
                }
            }
        }

        // The report rides on a body this feature makes, because a part's name in the parts list
        // belongs to the feature that created the body and the parts list is the only channel
        // that does not need a Part Studio watcher.
        opPolyline(context, id + "marker", {
                    "points" : [vector(0, 0, 0) * meter, vector(0, 0, 0.001) * meter]
                });
        setProperty(context, {
                    "entities" : qCreatedBy(id + "marker", EntityType.BODY),
                    "propertyType" : PropertyType.NAME,
                    "value" : line
                });
        reportCheckTally(context, id, "SHELL KNIT", tally, line);
    }, { "censusSeams" : true, "dropSheets" : true });
