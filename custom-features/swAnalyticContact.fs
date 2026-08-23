FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0"); // Plane, Cylinder, Cone, Sphere, Torus

/**
 * SOLID SWEEP - closed-form contact for ANALYTIC faces (spec: docs/specs/SOLID_SWEEP_SPEC.md
 * section 6.0 strategy 1, "analytic faces solve in closed form - zero de Boor"). Pure module:
 * no Context, no spline evaluation, no Bezier decomposition, no de Boor anywhere.
 *
 * WHY THIS IS THE FIRST STRATEGY, NOT AN OPTIMIZATION. Real tool bodies are mostly analytic:
 * a box is six planes, a cylinder is two planes and a wall, an extruded profile is planes plus
 * cylinders plus the occasional freeform wall. The coefficient path of section 6.0 strategy 2
 * handles freeform faces only, and it additionally requires NON-RATIONAL nets, which extraction
 * cannot supply for conic geometry (section 7.8). Without this layer the solver has no route at
 * all for the faces most parts are actually made of.
 *
 * THE ONE IDENTITY EVERYTHING RESTS ON. The envelope function of section 1.1 is
 *
 *     f(u,v,t) = < A(t) N(u,v) , A'(t) S(u,v) + b'(t) >
 *
 * and because A(t) is orthonormal (to the motion module's certified drift), the world-space
 * inner product can be pulled back into the TOOL frame:
 *
 *     f = < N , W(t) S + c(t) >,      W = A^T A',   c = A^T b'
 *
 * Twelve numbers per station, and the motion never appears again. Push that once more into the
 * face's OWN orthonormal frame E = [e1 e2 e3] (e3 the axis or normal), with the shape's origin
 * folded into the offset, and every analytic class collapses to a single expression:
 *
 *     f = < N_local , W_local S_local + g_local >,
 *     W_local = E^T W E,    g_local = E^T (W origin + c)
 *
 * The classes then differ ONLY in how S_local and N_local are built from their own parameters,
 * which is what makes this layer small enough to verify by hand. Note W is exactly skew when A
 * is exactly orthonormal (A^T A = I differentiates to A'^T A + A^T A' = 0), which the tester
 * uses as an anchor: a skew W kills every quadratic-in-normal term.
 *
 * WHAT FALLS OUT FOR FREE. Three things the freeform path has to work for:
 *   - The SLIDING AUDIT (spec 6.4, SWEEP_NOT_GENERAL_POSITION_SLIDING, "the common case on real
 *     parts") becomes exact and free: f is identically zero over the face iff a handful of
 *     coefficients vanish. No sampling, no tolerance on the geometry - only on the coefficients.
 *   - A conservative |f| BOUND over the whole face, from the same coefficients, is the analytic
 *     analogue of the convex-hull screen: a face that cannot graze at this station is rejected
 *     before any root work.
 *   - The per-station CONTACT CURVE is closed form. Plane: a straight line in (u,v). Cylinder
 *     and cone: an explicit graph z(theta) or l(theta), because f is LINEAR in the ruling
 *     parameter and a degree-2 trigonometric polynomial in theta (spec 6.0). Sphere and torus:
 *     one degree-2 trigonometric polynomial per meridian, so at most four roots, each bracketed
 *     and Newton-polished.
 *
 * Units: plain numbers, meters implied, matching every other pure module in the sweep. Angles
 * are plain radians here, NOT ValueWithUnits - these are parameters of a parameterization, not
 * measured quantities, and they get differentiated.
 */

// ============================= Analytic surface frames =============================

/**
 * The kinds this module solves in closed form. Deliberately its own enum rather than
 * swSweepEmit's SweepSurfaceClass: this module typechecks the evSurfaceDefinition value itself
 * and depends on nothing but std, so it can be tested and reasoned about alone.
 */
export enum AnalyticSurfaceKind
{
    PLANE,
    CYLINDER,
    CONE,
    SPHERE,
    TORUS
}

/**
 * Whether an evSurfaceDefinition value is one this module handles.
 */
export function isAnalyticContactSurface(surfaceDefinition) returns boolean
{
    return surfaceDefinition is Plane || surfaceDefinition is Cylinder || surfaceDefinition is Cone ||
        surfaceDefinition is Sphere || surfaceDefinition is Torus;
}

/**
 * Unit-strip a typed evSurfaceDefinition value into the frame this module computes on: an
 * orthonormal basis with the AXIS (or the plane's normal) as the third vector, the shape's
 * origin, and the shape's scalar parameters as plain numbers.
 *
 * Returns {
 *     kind {AnalyticSurfaceKind},
 *     origin {Vector} : plain numbers, meters implied - the plane's origin, the cylinder /
 *         sphere / torus centre, the CONE'S APEX,
 *     basis {array} : [e1, e2, e3] orthonormal, e3 the axis or the plane normal,
 *     radius {number} : cylinder / sphere radius, torus MAJOR radius, else undefined,
 *     minorRadius {number} : torus tube radius, else undefined,
 *     halfAngle {number} : cone half angle in plain radians, else undefined
 * }
 */
export function stripAnalyticSurface(surfaceDefinition) returns map
{
    if (surfaceDefinition is Plane)
    {
        const normal = surfaceDefinition.normal;
        const xAxis = surfaceDefinition.x;
        return {
                "kind" : AnalyticSurfaceKind.PLANE,
                "origin" : surfaceDefinition.origin / meter,
                "basis" : [xAxis, cross(normal, xAxis), normal],
                "radius" : undefined,
                "minorRadius" : undefined,
                "halfAngle" : undefined
            };
    }
    if (surfaceDefinition is Cylinder)
    {
        return mergeMaps(coordSystemFrame(surfaceDefinition.coordSystem), {
                    "kind" : AnalyticSurfaceKind.CYLINDER,
                    "radius" : surfaceDefinition.radius / meter
                });
    }
    if (surfaceDefinition is Cone)
    {
        return mergeMaps(coordSystemFrame(surfaceDefinition.coordSystem), {
                    "kind" : AnalyticSurfaceKind.CONE,
                    "halfAngle" : surfaceDefinition.halfAngle / radian
                });
    }
    if (surfaceDefinition is Sphere)
    {
        return mergeMaps(coordSystemFrame(surfaceDefinition.coordSystem), {
                    "kind" : AnalyticSurfaceKind.SPHERE,
                    "radius" : surfaceDefinition.radius / meter
                });
    }
    if (surfaceDefinition is Torus)
    {
        return mergeMaps(coordSystemFrame(surfaceDefinition.coordSystem), {
                    "kind" : AnalyticSurfaceKind.TORUS,
                    "radius" : surfaceDefinition.radius / meter,
                    "minorRadius" : surfaceDefinition.minorRadius / meter
                });
    }
    throw "swAnalyticContact: " ~ (surfaceDefinition is map ? "surface" : "value") ~
        " is not one of the five analytic classes this module solves.";
}

/** The stripped frame common to every coordSystem-based analytic class. */
function coordSystemFrame(coordSystem is CoordSystem) returns map
{
    return {
            "origin" : coordSystem.origin / meter,
            "basis" : [coordSystem.xAxis, cross(coordSystem.zAxis, coordSystem.xAxis), coordSystem.zAxis],
            "radius" : undefined,
            "minorRadius" : undefined,
            "halfAngle" : undefined
        };
}

// ============================= The motion pullback =============================

/**
 * Pull one motion station back into a face's own frame: the twelve numbers the closed forms
 * below consume, and the ONLY place the motion appears.
 *
 * motionSample is swEnvelopeMath's evaluateMotionSample shape - { rotation, rotationDerivative,
 * translationDerivative } as plain-number matrices and vectors. Taking the SAMPLE rather than
 * the motion is what keeps this module free of any spline dependency.
 *
 * Returns { wLocal {array} : 3x3 rows of E^T A^T A' E, gLocal {Vector} : E^T (A^T A' origin +
 * A^T b') }. Read wLocal as w[row][column] = < e_row , W e_column >.
 */
export function analyticContactPullback(motionSample is map, frame is map) returns map
{
    const rotation = motionSample.rotation;
    const worldW = transpose(rotation) * motionSample.rotationDerivative;
    const worldC = transpose(rotation) * motionSample.translationDerivative;
    const offset = worldW * frame.origin + worldC;

    const basis = frame.basis;
    var wLocal = makeArray(3);
    for (var row = 0; row < 3; row += 1)
    {
        const projected = worldW * basis[row];
        wLocal[row] = [dot(basis[0], projected), dot(basis[1], projected), dot(basis[2], projected)];
    }
    // w[row][column] must be < e_row , W e_column >, and the loop above built
    // < e_column , W e_row >, so transpose the little matrix back.
    var oriented = makeArray(3);
    for (var row = 0; row < 3; row += 1)
    {
        oriented[row] = [wLocal[0][row], wLocal[1][row], wLocal[2][row]];
    }
    return {
            "wLocal" : oriented,
            "gLocal" : vector(dot(basis[0], offset), dot(basis[1], offset), dot(basis[2], offset))
        };
}

// ============================= The closed forms =============================

/**
 * A face's local surface point and unit normal at (u, v), in the frame's own basis. This is the
 * only per-class geometry in the module; everything else reads it.
 *
 * Parameter conventions, all plain radians where angular:
 *   PLANE    (u, v) : signed distances along e1, e2 from the origin.
 *   CYLINDER (theta, z) : theta around from e1, z along the axis.
 *   CONE     (theta, l) : theta around from e1, l the distance from the APEX along the ruling.
 *   SPHERE   (theta, phi) : longitude from e1, latitude from the equator (phi in [-pi/2, pi/2]).
 *   TORUS    (theta, phi) : theta around the axis, phi around the tube from the outer equator.
 *
 * Returns { point {Vector}, normal {Vector} } in local coordinates. Normals point away from the
 * material for the standard outward orientation of each class; the zero set of f does not depend
 * on that sign, and orientation is decided separately (spec 6.3 step 5).
 */
export function analyticLocalPointAndNormal(frame is map, u is number, v is number) returns map
{
    if (frame.kind == AnalyticSurfaceKind.PLANE)
    {
        return { "point" : vector(u, v, 0), "normal" : vector(0, 0, 1) };
    }
    if (frame.kind == AnalyticSurfaceKind.CYLINDER)
    {
        const radial = vector(cos(u * radian), sin(u * radian), 0);
        return { "point" : frame.radius * radial + vector(0, 0, v), "normal" : radial };
    }
    if (frame.kind == AnalyticSurfaceKind.CONE)
    {
        const cosHalf = cos(frame.halfAngle * radian);
        const sinHalf = sin(frame.halfAngle * radian);
        const radial = vector(cos(u * radian), sin(u * radian), 0);
        return {
                "point" : v * (sinHalf * radial + vector(0, 0, cosHalf)),
                "normal" : cosHalf * radial - vector(0, 0, sinHalf)
            };
    }
    if (frame.kind == AnalyticSurfaceKind.SPHERE)
    {
        const radial = vector(cos(v * radian) * cos(u * radian), cos(v * radian) * sin(u * radian),
                sin(v * radian));
        return { "point" : frame.radius * radial, "normal" : radial };
    }
    // TORUS
    const axial = vector(cos(u * radian), sin(u * radian), 0);
    const tubeNormal = cos(v * radian) * axial + vector(0, 0, sin(v * radian));
    return {
            "point" : (frame.radius + frame.minorRadius * cos(v * radian)) * axial +
                vector(0, 0, frame.minorRadius * sin(v * radian)),
            "normal" : tubeNormal
        };
}

/**
 * The envelope function at one parameter, in closed form: f = < N_local, W_local S_local +
 * g_local >. Zero de Boor, zero spline evaluation, one 3x3 product.
 */
export function evaluateAnalyticContact(frame is map, pullback is map, u is number, v is number) returns number
{
    const local = analyticLocalPointAndNormal(frame, u, v);
    return dot(local.normal, applyLocalMatrix(pullback.wLocal, local.point) + pullback.gLocal);
}

/**
 * The same value computed the long way round for verification: build the WORLD point and normal
 * from the frame, then evaluate the section 1.1 definition directly against the motion sample.
 * Shares no algebra with evaluateAnalyticContact beyond the frame itself, so agreement between
 * the two is a real check on the pullback.
 */
export function evaluateAnalyticContactDirect(frame is map, motionSample is map, u is number,
    v is number) returns number
{
    const local = analyticLocalPointAndNormal(frame, u, v);
    const basis = frame.basis;
    const worldPoint = frame.origin + local.point[0] * basis[0] + local.point[1] * basis[1] +
        local.point[2] * basis[2];
    const worldNormal = local.normal[0] * basis[0] + local.normal[1] * basis[1] +
        local.normal[2] * basis[2];
    const velocity = motionSample.rotationDerivative * worldPoint + motionSample.translationDerivative;
    return dot(motionSample.rotation * worldNormal, velocity);
}

/** A 3x3 stored as rows of plain numbers, applied to a plain-number Vector. */
function applyLocalMatrix(rows is array, columnVector is Vector) returns Vector
{
    return vector(
        rows[0][0] * columnVector[0] + rows[0][1] * columnVector[1] + rows[0][2] * columnVector[2],
        rows[1][0] * columnVector[0] + rows[1][1] * columnVector[1] + rows[1][2] * columnVector[2],
        rows[2][0] * columnVector[0] + rows[2][1] * columnVector[1] + rows[2][2] * columnVector[2]);
}

// ============================= Trigonometric polynomials =============================

/**
 * A real trigonometric polynomial P(x) = sum_k cosine[k] cos(k x) + sine[k] sin(k x), with
 * cosine[0] the constant and sine[0] unused. Degree n has at most 2n roots per period, which is
 * what bounds the root search below. Every analytic class here produces degree at most 2.
 */
export function trigPolynomial(cosine is array, sine is array) returns map
{
    return { "cosine" : cosine, "sine" : sine };
}

/** P(x), x in plain radians. */
export function trigPolynomialValue(polynomial is map, x is number) returns number
{
    var total = 0;
    for (var harmonic = 0; harmonic < size(polynomial.cosine); harmonic += 1)
    {
        total += polynomial.cosine[harmonic] * cos(harmonic * x * radian) +
            polynomial.sine[harmonic] * sin(harmonic * x * radian);
    }
    return total;
}

/** P'(x). Exact - term by term, no differencing. */
export function trigPolynomialDerivative(polynomial is map, x is number) returns number
{
    var total = 0;
    for (var harmonic = 1; harmonic < size(polynomial.cosine); harmonic += 1)
    {
        total += harmonic * (polynomial.sine[harmonic] * cos(harmonic * x * radian) -
                    polynomial.cosine[harmonic] * sin(harmonic * x * radian));
    }
    return total;
}

/**
 * A conservative bound on |P| over the whole period: |constant| + sum of harmonic amplitudes.
 * Tight when the harmonics align somewhere, and always valid - which is what a screen needs.
 * This is the analytic analogue of the coefficient path's convex-hull rejection (spec 6.0).
 */
export function trigPolynomialBound(polynomial is map) returns number
{
    var bound = abs(polynomial.cosine[0]);
    for (var harmonic = 1; harmonic < size(polynomial.cosine); harmonic += 1)
    {
        bound += sqrt(polynomial.cosine[harmonic] ^ 2 + polynomial.sine[harmonic] ^ 2);
    }
    return bound;
}

/** Whether every coefficient is within tolerance of zero, so P is identically zero. */
export function trigPolynomialIsZero(polynomial is map, tolerance is number) returns boolean
{
    for (var harmonic = 0; harmonic < size(polynomial.cosine); harmonic += 1)
    {
        if (abs(polynomial.cosine[harmonic]) > tolerance ||
            (harmonic > 0 && abs(polynomial.sine[harmonic]) > tolerance))
        {
            return false;
        }
    }
    return true;
}

/**
 * Every root of P in [start, start + 2 pi): scan a grid fine enough for the degree, bracket
 * every sign change, and polish with bisection-safeguarded Newton on the exact derivative.
 *
 * The grid is 8 samples per harmonic, so a degree-n polynomial with its at most 2n roots gets at
 * least four intervals per root - ample except where two roots have merged, which is a TANGENCY
 * and is reported rather than resolved: `nearTangency` is set when a scanned extremum comes
 * within `tangencyTolerance` of zero without the neighbouring samples changing sign. That is the
 * honest answer, because a double root of f along a p-curve is exactly the degeneracy spec 6.4
 * names SWEEP_FUNNEL_TANGENT_TO_SLICE, and silently returning one root or three would hide it.
 *
 * A polynomial that is identically zero returns { identicallyZero : true } and no roots: the
 * caller is looking at a sliding face, not a contact curve.
 *
 * Returns { identicallyZero, roots {array, ascending}, nearTangency {boolean},
 * worstResidual {number} }.
 */
export function solveTrigPolynomialRoots(polynomial is map, start is number, options is map) returns map
{
    const tolerance = options.tolerance == undefined ? 1e-14 : options.tolerance;
    const coefficientTolerance = options.coefficientTolerance == undefined ? 1e-15 :
        options.coefficientTolerance;
    const tangencyTolerance = options.tangencyTolerance == undefined ?
        1e-9 * max(1e-30, trigPolynomialBound(polynomial)) : options.tangencyTolerance;
    if (trigPolynomialIsZero(polynomial, coefficientTolerance))
    {
        return { "identicallyZero" : true, "roots" : [], "nearTangency" : false, "worstResidual" : 0 };
    }
    const degree = size(polynomial.cosine) - 1;
    const sampleCount = max(16, 8 * max(1, degree));
    const period = 2 * PI;
    const step = period / sampleCount;

    var roots = makeArray(2 * max(1, degree) + 2);
    var rootCount = 0;
    var nearTangency = false;
    var worstResidual = 0;
    var previousX = start;
    var previousValue = trigPolynomialValue(polynomial, start);
    for (var index = 1; index <= sampleCount; index += 1)
    {
        const x = start + step * index;
        const value = trigPolynomialValue(polynomial, x);
        if (previousValue == 0)
        {
            // An exact sample hit; take it and let the next interval start clean.
            if (rootCount < size(roots))
            {
                roots[rootCount] = previousX;
                rootCount += 1;
            }
        }
        else if (previousValue * value < 0)
        {
            const refined = refineTrigRoot(polynomial, previousX, x, previousValue, tolerance);
            if (rootCount < size(roots))
            {
                roots[rootCount] = refined.x;
                rootCount += 1;
            }
            worstResidual = max(worstResidual, abs(refined.value));
        }
        else if (abs(value) <= tangencyTolerance || abs(previousValue) <= tangencyTolerance)
        {
            // Same sign at both ends but grazing zero in between: a merged pair, not a crossing.
            nearTangency = true;
        }
        previousX = x;
        previousValue = value;
    }
    return {
            "identicallyZero" : false,
            "roots" : subArray(roots, 0, rootCount),
            "nearTangency" : nearTangency,
            "worstResidual" : worstResidual
        };
}

/** Bisection-safeguarded Newton inside a sign-change bracket. Returns { x, value }. */
function refineTrigRoot(polynomial is map, low is number, high is number, lowValue is number,
    tolerance is number) returns map
{
    var bracketLow = low;
    var bracketHigh = high;
    var bracketLowValue = lowValue;
    var x = 0.5 * (low + high);
    var value = trigPolynomialValue(polynomial, x);
    for (var iteration = 0; iteration < 60; iteration += 1)
    {
        if (abs(value) <= tolerance)
        {
            break;
        }
        if (value * bracketLowValue > 0)
        {
            bracketLow = x;
            bracketLowValue = value;
        }
        else
        {
            bracketHigh = x;
        }
        const slope = trigPolynomialDerivative(polynomial, x);
        var next = abs(slope) < 1e-300 ? 0.5 * (bracketLow + bracketHigh) : x - value / slope;
        if (next <= bracketLow || next >= bracketHigh)
        {
            next = 0.5 * (bracketLow + bracketHigh);
        }
        if (next == x)
        {
            break;
        }
        x = next;
        value = trigPolynomialValue(polynomial, x);
    }
    return { "x" : x, "value" : value };
}

// ============================= Per-class contact structure =============================

/**
 * The structure of f over one analytic face at one station, in the form each class's contact
 * solve wants. This is where the paper algebra of the module header is actually spent.
 *
 * Every class reports `ruledCoefficients` when f is LINEAR in its second parameter, which is
 * what makes the contact curve an explicit graph:
 *
 *   PLANE:    f = constant + uCoefficient u + vCoefficient v   (linear in BOTH)
 *   CYLINDER: f = A(theta) + z B(theta),  A degree 2, B degree 1
 *   CONE:     f = A(theta) + l B(theta),  A degree 1, B degree 2
 *   SPHERE:   no ruling - one degree-2 trigonometric polynomial in phi per meridian
 *   TORUS:    no ruling - one degree-2 trigonometric polynomial in phi per meridian
 *
 * Returns a map carrying `kind`, `slidesEverywhere` (spec 6.4's exact verdict), `bound` (the
 * free |f| screen over the whole face), and the class-specific coefficient bundle named above.
 */
export function analyticContactStructure(frame is map, pullback is map, options is map) returns map
{
    const coefficientTolerance = options.coefficientTolerance == undefined ? 1e-15 :
        options.coefficientTolerance;
    const w = pullback.wLocal;
    const g = pullback.gLocal;

    if (frame.kind == AnalyticSurfaceKind.PLANE)
    {
        // f = g3 + u <n, W e1> + v <n, W e2>, exactly linear. A plane either grazes on a single
        // straight line in its own parameters, slides entirely, or never touches.
        const constant = g[2];
        const uCoefficient = w[2][0];
        const vCoefficient = w[2][1];
        return {
                "kind" : frame.kind,
                "slidesEverywhere" : abs(constant) <= coefficientTolerance &&
                    abs(uCoefficient) <= coefficientTolerance && abs(vCoefficient) <= coefficientTolerance,
                "constant" : constant,
                "uCoefficient" : uCoefficient,
                "vCoefficient" : vCoefficient,
                "bound" : undefined
            };
    }
    if (frame.kind == AnalyticSurfaceKind.CYLINDER)
    {
        // f = r <d, W d> + <d, g> + z <d, W e3>. The quadratic form goes to second harmonics by
        // the double-angle identities; note it vanishes identically when W is skew, which is why
        // an exactly rigid motion leaves a cylinder's contact curve first-harmonic only.
        const r = frame.radius;
        const axialTerm = trigPolynomial(
                [0.5 * r * (w[0][0] + w[1][1]), g[0], 0.5 * r * (w[0][0] - w[1][1])],
                [0, g[1], 0.5 * r * (w[0][1] + w[1][0])]);
        const rulingTerm = trigPolynomial([0, w[0][2]], [0, w[1][2]]);
        return {
                "kind" : frame.kind,
                "slidesEverywhere" : trigPolynomialIsZero(axialTerm, coefficientTolerance) &&
                    trigPolynomialIsZero(rulingTerm, coefficientTolerance),
                "constantTerm" : axialTerm,
                "rulingTerm" : rulingTerm,
                "bound" : undefined
            };
    }
    if (frame.kind == AnalyticSurfaceKind.CONE)
    {
        // N = cosA d - sinA e3, S = l (sinA d + cosA e3) from the apex, so
        // f = <N, g> + l <N, W (sinA d + cosA e3)>: degree 1 in theta for the offset part,
        // degree 2 for the ruling part.
        const cosHalf = cos(frame.halfAngle * radian);
        const sinHalf = sin(frame.halfAngle * radian);
        const offsetTerm = trigPolynomial([-sinHalf * g[2], cosHalf * g[0]], [0, cosHalf * g[1]]);
        // <N, W (sinA d + cosA e3)>
        //   = sinA cosA <d, W d> - sinA^2 <e3, W d> + cosA^2 <d, W e3> - sinA cosA w33
        const quadratic = sinHalf * cosHalf;
        const rulingTerm = trigPolynomial(
                [quadratic * (0.5 * (w[0][0] + w[1][1]) - w[2][2]),
                    cosHalf * cosHalf * w[0][2] - sinHalf * sinHalf * w[2][0],
                    0.5 * quadratic * (w[0][0] - w[1][1])],
                [0,
                    cosHalf * cosHalf * w[1][2] - sinHalf * sinHalf * w[2][1],
                    0.5 * quadratic * (w[0][1] + w[1][0])]);
        return {
                "kind" : frame.kind,
                "slidesEverywhere" : trigPolynomialIsZero(offsetTerm, coefficientTolerance) &&
                    trigPolynomialIsZero(rulingTerm, coefficientTolerance),
                "constantTerm" : offsetTerm,
                "rulingTerm" : rulingTerm,
                "bound" : undefined
            };
    }
    // SPHERE and TORUS have no ruling; their meridian polynomials are built on demand by
    // analyticMeridianPolynomial, and sliding is decided from the same coefficients.
    const meridianSamples = options.meridianSamples == undefined ? 8 : options.meridianSamples;
    var slides = true;
    for (var index = 0; index < meridianSamples; index += 1)
    {
        const meridian = analyticMeridianPolynomial(frame, pullback, 2 * PI * index / meridianSamples);
        if (!trigPolynomialIsZero(meridian, coefficientTolerance))
        {
            slides = false;
            break;
        }
    }
    return {
            "kind" : frame.kind,
            "slidesEverywhere" : slides,
            "bound" : undefined
        };
}

/**
 * The degree-2 trigonometric polynomial in phi that f becomes along one meridian theta of a
 * sphere or torus. Sphere: f = <n, g> + r n^T W n with n = (cosPhi cosTheta, cosPhi sinTheta,
 * sinPhi). Torus: the same normal against S = ((R + r cosPhi) d, r sinPhi). Both are quadratic
 * in (cosPhi, sinPhi), so both reduce to second harmonics.
 */
export function analyticMeridianPolynomial(frame is map, pullback is map, theta is number) returns map
{
    const w = pullback.wLocal;
    const g = pullback.gLocal;
    const cosTheta = cos(theta * radian);
    const sinTheta = sin(theta * radian);
    // The normal is cosPhi * d + sinPhi * e3, with d = (cosTheta, sinTheta, 0).
    // Write every inner product as p * cosPhi + q * sinPhi and its quadratic partners.
    const dDotG = g[0] * cosTheta + g[1] * sinTheta;              // <d, g>
    const axisDotG = g[2];                                         // <e3, g>
    const dWd = w[0][0] * cosTheta * cosTheta + (w[0][1] + w[1][0]) * cosTheta * sinTheta +
        w[1][1] * sinTheta * sinTheta;                             // <d, W d>
    const dWAxis = w[0][2] * cosTheta + w[1][2] * sinTheta;        // <d, W e3>
    const axisWd = w[2][0] * cosTheta + w[2][1] * sinTheta;        // <e3, W d>
    const axisWAxis = w[2][2];                                     // <e3, W e3>

    if (frame.kind == AnalyticSurfaceKind.SPHERE)
    {
        const r = frame.radius;
        // f = cosPhi dDotG + sinPhi axisDotG
        //   + r [ cosPhi^2 dWd + cosPhi sinPhi (dWAxis + axisWd) + sinPhi^2 axisWAxis ]
        return trigPolynomial(
                [0.5 * r * (dWd + axisWAxis), dDotG, 0.5 * r * (dWd - axisWAxis)],
                [0, axisDotG, 0.5 * r * (dWAxis + axisWd)]);
    }
    // TORUS: S = (R + r cosPhi) d + r sinPhi e3.
    // f = <n, g> + <n, W S>
    //   = cosPhi dDotG + sinPhi axisDotG
    //     + (R + r cosPhi) [ cosPhi dWd + sinPhi axisWd ]
    //     + r sinPhi      [ cosPhi dWAxis + sinPhi axisWAxis ]
    const majorRadius = frame.radius;
    const minorRadius = frame.minorRadius;
    // Collect by harmonic: constant, first, second.
    const constantTerm = 0.5 * minorRadius * (dWd + axisWAxis);
    const firstCosine = dDotG + majorRadius * dWd;
    const firstSine = axisDotG + majorRadius * axisWd;
    const secondCosine = 0.5 * minorRadius * (dWd - axisWAxis);
    const secondSine = 0.5 * minorRadius * (axisWd + dWAxis);
    return trigPolynomial([constantTerm, firstCosine, secondCosine], [0, firstSine, secondSine]);
}

// ============================= Contact curves in closed form =============================

/**
 * The contact curve of one analytic face at one station, in closed form (spec 6.3 step 4, the
 * analytic counterpart of section marching).
 *
 * PLANE: f is linear, so { form : "line", constant, uCoefficient, vCoefficient } - the caller
 * intersects that line with the face's trim domain. CYLINDER and CONE: f is linear in the ruling
 * parameter, so the curve is the explicit graph `ruling = -constantTerm(theta) /
 * rulingTerm(theta)`, returned as samples over the requested theta range together with the
 * thetas where the ruling coefficient VANISHES - at those the whole ruling either lies in the
 * contact set (numerator also zero) or misses it entirely, which is the closed-form statement of
 * a vertical asymptote and must not be sampled through. SPHERE and TORUS: per-meridian roots.
 *
 * options: { sampleCount (default 64), thetaStart (default 0), thetaSpan (default 2 pi),
 * tolerance, coefficientTolerance }.
 *
 * Returns { form, slidesEverywhere, samples {array of [u, v]}, singularThetas {array},
 * nearTangency {boolean} }.
 */
export function solveAnalyticContactCurve(frame is map, pullback is map, options is map) returns map
{
    const structure = analyticContactStructure(frame, pullback, options);
    if (structure.slidesEverywhere)
    {
        return { "form" : "sliding", "slidesEverywhere" : true, "samples" : [],
                "singularThetas" : [], "nearTangency" : false };
    }
    if (frame.kind == AnalyticSurfaceKind.PLANE)
    {
        return { "form" : "line", "slidesEverywhere" : false,
                "constant" : structure.constant,
                "uCoefficient" : structure.uCoefficient,
                "vCoefficient" : structure.vCoefficient,
                "samples" : [], "singularThetas" : [], "nearTangency" : false };
    }

    const sampleCount = options.sampleCount == undefined ? 64 : options.sampleCount;
    const thetaStart = options.thetaStart == undefined ? 0 : options.thetaStart;
    const thetaSpan = options.thetaSpan == undefined ? 2 * PI : options.thetaSpan;
    const coefficientTolerance = options.coefficientTolerance == undefined ? 1e-15 :
        options.coefficientTolerance;

    if (frame.kind == AnalyticSurfaceKind.CYLINDER || frame.kind == AnalyticSurfaceKind.CONE)
    {
        const rulingBound = trigPolynomialBound(structure.rulingTerm);
        const singularCut = max(coefficientTolerance, 1e-12 * rulingBound);
        var samples = makeArray(sampleCount + 1, [0, 0]);
        var sampleTotal = 0;
        for (var index = 0; index <= sampleCount; index += 1)
        {
            const theta = thetaStart + thetaSpan * index / sampleCount;
            const rulingCoefficient = trigPolynomialValue(structure.rulingTerm, theta);
            if (abs(rulingCoefficient) <= singularCut)
            {
                continue;
            }
            samples[sampleTotal] = [theta,
                    -trigPolynomialValue(structure.constantTerm, theta) / rulingCoefficient];
            sampleTotal += 1;
        }
        const singular = solveTrigPolynomialRoots(structure.rulingTerm, thetaStart, options);
        return { "form" : "ruledGraph", "slidesEverywhere" : false,
                "samples" : subArray(samples, 0, sampleTotal),
                "singularThetas" : singular.roots,
                "nearTangency" : singular.nearTangency };
    }

    // SPHERE and TORUS: one meridian polynomial per theta, up to four roots each.
    var samples = makeArray(4 * (sampleCount + 1), [0, 0]);
    var sampleTotal = 0;
    var nearTangency = false;
    for (var index = 0; index <= sampleCount; index += 1)
    {
        const theta = thetaStart + thetaSpan * index / sampleCount;
        const meridian = analyticMeridianPolynomial(frame, pullback, theta);
        const solved = solveTrigPolynomialRoots(meridian, -PI, options);
        nearTangency = nearTangency || solved.nearTangency;
        for (var root in solved.roots)
        {
            if (sampleTotal < size(samples))
            {
                samples[sampleTotal] = [theta, root];
                sampleTotal += 1;
            }
        }
    }
    return { "form" : "meridianRoots", "slidesEverywhere" : false,
            "samples" : subArray(samples, 0, sampleTotal),
            "singularThetas" : [], "nearTangency" : nearTangency };
}

/**
 * A conservative bound on |f| over a rectangle of the face's parameters - the free screen that
 * rejects a face which cannot graze at this station before any root work happens (spec 6.0's
 * convex-hull rejection, in the analytic setting). Bounds the ruling parameter by the caller's
 * own domain because f grows linearly in it.
 *
 * Returns { minimumMagnitude, maximumMagnitude } : when minimumMagnitude > 0 the face provably
 * does not graze anywhere in the rectangle.
 */
export function analyticContactBound(frame is map, pullback is map, domain is map, options is map) returns map
{
    const structure = analyticContactStructure(frame, pullback, options);
    if (frame.kind == AnalyticSurfaceKind.PLANE)
    {
        const extreme = abs(structure.uCoefficient) * max(abs(domain.uMin), abs(domain.uMax)) +
            abs(structure.vCoefficient) * max(abs(domain.vMin), abs(domain.vMax));
        return {
                "minimumMagnitude" : max(0, abs(structure.constant) - extreme),
                "maximumMagnitude" : abs(structure.constant) + extreme
            };
    }
    if (frame.kind == AnalyticSurfaceKind.CYLINDER || frame.kind == AnalyticSurfaceKind.CONE)
    {
        const constantBound = trigPolynomialBound(structure.constantTerm);
        const rulingReach = trigPolynomialBound(structure.rulingTerm) *
            max(abs(domain.vMin), abs(domain.vMax));
        return {
                "minimumMagnitude" : 0,
                "maximumMagnitude" : constantBound + rulingReach
            };
    }
    var worst = 0;
    const meridianSamples = options.meridianSamples == undefined ? 8 : options.meridianSamples;
    for (var index = 0; index < meridianSamples; index += 1)
    {
        worst = max(worst, trigPolynomialBound(
                    analyticMeridianPolynomial(frame, pullback, 2 * PI * index / meridianSamples)));
    }
    return { "minimumMagnitude" : 0, "maximumMagnitude" : worst };
}
