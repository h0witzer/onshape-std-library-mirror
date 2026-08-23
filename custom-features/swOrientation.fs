FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

// Non-standard imports. swEnvelopeMath and swFunnelSolver are same-document element imports
// (unpublished on purpose - see swEnvelopeMath.fs); their ids churn on every re-paste, so bump
// these when the owner re-pastes either module. splineRefinementUtils is the published
// cross-document pin. NOTE for MCP harness runs: same-document imports cannot resolve from the
// harness document; the test payload inlines the needed function bodies in place of these lines.
import(path : "eede4083ca591e1a7adb8440", version : "3968d1ef5b507302198a917b"); //owner combined tab: swEnvelopeMath.fs, swFunnelSolver.fs (fix id/version on paste; split into one line per tab if they live separately)
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs

/**
 * SOLID SWEEP - orientation (spec: docs/specs/SOLID_SWEEP_SPEC.md section 6.6, the solver
 * pipeline's step 5). Pure module: no Context in any function. Answers, for every class of
 * envelope entity, which way it faces:
 *
 *   - grazing patches from smooth faces: does the fitted (station, q) net's own parametric
 *     normal point out of the swept volume, or must the q direction be reversed;
 *   - the co-edges those patches generate along an input co-edge: which way each is traversed
 *     so its patch stays on its left;
 *   - sharp-edge envelope faces: which of the two normals of the transported edge sheet is
 *     outward;
 *   - sharp-vertex trajectory edges: which way each is traversed for the face it bounds;
 *   - cap faces: which side of the ingress or egress cut survives.
 *
 * ONE INVARIANT CARRIES EVERY SMOOTH-FACE CASE. Write the contact point's velocity in the
 * transported tangent basis - on the funnel f = 0 it lies in that plane by definition, which is
 * what makes the two coordinates well defined:
 *
 *     A' S + b' = alpha (A S_u) + beta (A S_v),      lambda = f_t - alpha f_u - beta f_v
 *
 * Then for ANY chart of the funnel, the chart's parametric normal is a known multiple of the
 * transported normal A N (N = S_u x S_v, unnormalized - the same normal f is built from):
 *
 *     the project-to-(u, v) chart:   Psi_u x Psi_v  =  (lambda / f_t) * A N
 *     the fit's (q, t) chart:        Phi_q x Phi_t  =  kappa * lambda  * A N
 *
 * where kappa is the signed scale of the section direction, (u_q, v_q) = kappa (-f_v, f_u).
 * Both identities were checked against finite differences of the charts themselves, on a
 * generic freeform patch under a genuinely rotating motion, before this module was written.
 *
 * Three consequences, and they are the whole module:
 *
 * 1. A fit patch is parameterized u = station (t), v = q, so its normal is
 *    Phi_t x Phi_q = -kappa lambda A N: the net faces OUTWARD exactly when kappa lambda < 0.
 *    Nothing about the fit's accuracy enters - only which way the section march ran.
 * 2. An envelope co-edge's traversal direction is sign(lambda / f_t) times its input co-edge's,
 *    because the projection from funnel to domain is orientation preserving or reversing with
 *    that sign (framework paper Theorem 12, Proposition 14). lambda keeps one sign on a funnel
 *    component, so the f_t signs are what vary - and at a fixed co-edge parameter the roots of
 *    the strip function in t have ALTERNATING f_t signs by Rolle, which is the papers'
 *    alternation rule (framework paper 5.3).
 * 3. lambda = 0 is exactly where the contact point stops moving - the chart folds and the
 *    envelope has a cusp there. So lambda holding one sign across a component is a
 *    local-self-intersection certificate (section 10 detector 1), delivered free by the
 *    orientation pass. A component whose lambda changes sign is reported, never emitted.
 *
 * A NOTE ON PURE TRANSLATION, learned live. Under a constant-velocity translation A' and b''
 * both vanish, so f_t is identically zero - and its raw floating-point sign is then rounding
 * noise (measured on the ruled fit fixture: half the grid came back +1, half exactly 0). f_t is
 * therefore tested against a RELATIVE floor, and a vanishing f_t is reported as
 * `contactStationary` rather than as degeneracy. It is spec 7.7's profile-sweep case: the
 * contact set does not move in the tool frame. Crucially it costs nothing for the PATCH verdict,
 * which is sign(kappa * lambda) and never reads f_t - only the CO-EDGE rule goes quiet, and it
 * should, because there is no f_t sign to alternate.
 *
 * On the alternation rule: this module evaluates the time derivative at EVERY branch sample
 * instead of propagating one sign by alternation. It costs one motion sample apiece against
 * marching that already found the roots, and it turns the rule into a certificate - two
 * neighboring roots sharing a sign mean a root was missed or two merged, which is the
 * degeneracy section 6.4 names SWEEP_FUNNEL_TANGENT_TO_SLICE.
 *
 * Sharp features follow the sharp-features paper directly (its 7.1, 7.2, 7.4): the transported
 * edge sheet's normal is (A e') x velocity and the outward choice is the candidate lying inside
 * the transported cone of normals; a sharp-vertex co-edge is oriented by testing the edge's
 * interior direction against n x w; and a lateral co-edge shared by a sharp-edge face and a
 * grazing patch is traversed oppositely by the two of them.
 *
 * All geometry here is plain numbers (meters implied), matching the rest of the solver.
 */

// ============================= The orientation invariant =============================

/**
 * Everything orientation needs at one point of the funnel, from one surface evaluation and one
 * motion evaluation (spec 6.6). The point need not lie exactly on f = 0 - `value` and
 * `tangentialResidual` report how far off it is, and both scale the trust in `lambda`.
 *
 * Returns {
 *     value, uDerivative, vDerivative, tDerivative {number} : f and its gradient, built on the
 *         UNNORMALIZED normal S_u x S_v as everywhere in this solver,
 *     surfacePoint, uTangent, vTangent, parametricNormal {Vector} : tool frame,
 *     transportedNormal {Vector} : A N, unnormalized,
 *     outwardNormal {Vector} : the unit outward normal of the envelope here - the transported
 *         normal, which IS the answer at every grazing point (framework paper 5.4.1),
 *     normalMagnitude {number} : |A N|, zero at a pole,
 *     velocity {Vector} : A' S + b',
 *     alpha, beta {number} : the velocity's coordinates in (A S_u, A S_v),
 *     tangentialResidual {number} : how far the velocity is from that plane - on the funnel it
 *         equals |f| / |A N|, so it doubles as a residual check,
 *     lambda {number}, lambdaSign, timeDerivativeSign {number} : -1, 0 or 1,
 *     timeDerivativeMargin {number} : |f_t| against the size of lambda's three terms, scale
 *         free - f_t is compared against a RELATIVE floor because under a constant-velocity
 *         translation A' and b'' both vanish, making f_t identically zero and its raw
 *         floating-point sign pure rounding noise,
 *     contactStationary {boolean} : that margin is at or under `stationaryTolerance`, so the
 *         contact set does not move in the tool frame. This is the profile-sweep degeneracy of
 *         spec 7.7. It silences the CO-EDGE rule (there is no f_t sign to alternate) and leaves
 *         the PATCH verdict untouched, which never reads f_t,
 *     chartSign {number} : sign(lambda / f_t), the (u, v) chart's agreement with A N; 0 when
 *         the contact set is stationary,
 *     foldMargin {number} : |lambda| against the size of its own three terms - how far this
 *         point sits from the fold lambda = 0, scale free,
 *     degenerate {boolean} : PATCH degeneracy - a vanishing normal, a rank-deficient tangent
 *         basis, or a lambda / fold margin inside the given tolerances. A vanishing f_t is
 *         deliberately not included; see contactStationary
 * }
 */
export function envelopeOrientationSample(strippedMotion is map, strippedSurface is map,
    u is number, v is number, t is number) returns map
{
    return envelopeOrientationSample(strippedMotion, strippedSurface, u, v, t, {});
}

/**
 * Same, with tolerances: { valueTolerance (default 0) : a derivative or lambda inside this
 * counts as zero, foldTolerance (default 0) : a relative fold margin at or under this counts
 * as degenerate, stationaryTolerance (default 1e-12) : a RELATIVE f_t margin at or under this
 * counts as a stationary contact set }.
 */
export function envelopeOrientationSample(strippedMotion is map, strippedSurface is map,
    u is number, v is number, t is number, options is map) returns map
{
    const valueTolerance = options.valueTolerance == undefined ? 0 : options.valueTolerance;
    const foldTolerance = options.foldTolerance == undefined ? 0 : options.foldTolerance;
    const stationaryTolerance = options.stationaryTolerance == undefined ? 1e-12 :
        options.stationaryTolerance;

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
    const value = dot(transportedNormal, velocity);
    const uDerivative = dot(sample.rotation * uNormalDerivative, velocity) +
        dot(transportedNormal, sample.rotationDerivative * uTangent);
    const vDerivative = dot(sample.rotation * vNormalDerivative, velocity) +
        dot(transportedNormal, sample.rotationDerivative * vTangent);
    const tDerivative = dot(sample.rotationDerivative * normal, velocity) +
        dot(transportedNormal, acceleration);

    // The velocity's coordinates in the TRANSPORTED tangent basis. World space on purpose:
    // pulling back into the tool frame would need A's inverse, and the motion module's A is
    // orthonormal only to its certified drift - the Gram solve needs no such assumption.
    const worldUTangent = sample.rotation * uTangent;
    const worldVTangent = sample.rotation * vTangent;
    const coordinates = solveTangentCoordinates(worldUTangent, worldVTangent, velocity);
    const lambda = coordinates.degenerate ? 0 :
        tDerivative - coordinates.alpha * uDerivative - coordinates.beta * vDerivative;

    const normalMagnitude = norm(transportedNormal);
    const termScale = abs(tDerivative) + abs(coordinates.alpha * uDerivative) +
        abs(coordinates.beta * vDerivative);
    const foldMargin = termScale < 1e-300 ? 0 : abs(lambda) / termScale;
    const lambdaSign = signOf(lambda, valueTolerance);
    // f_t against a RELATIVE floor, not against zero. Under a constant-velocity translation
    // A' and b'' both vanish, so f_t is identically zero and its floating-point sign is pure
    // rounding noise - measured live on the ruled fixture, where half the grid came back +1
    // and half exactly 0. That is the profile-sweep degeneracy of spec 7.7, and it has to
    // resolve deterministically to "stationary" rather than to a coin flip.
    const timeDerivativeMargin = termScale < 1e-300 ? 0 : abs(tDerivative) / termScale;
    const timeDerivativeSign = timeDerivativeMargin <= stationaryTolerance ? 0 :
        signOf(tDerivative, valueTolerance);
    const contactStationary = timeDerivativeSign == 0;
    return {
            "value" : value,
            "uDerivative" : uDerivative,
            "vDerivative" : vDerivative,
            "tDerivative" : tDerivative,
            "surfacePoint" : surfacePoint,
            "uTangent" : uTangent,
            "vTangent" : vTangent,
            "parametricNormal" : normal,
            "transportedNormal" : transportedNormal,
            "outwardNormal" : normalMagnitude < 1e-300 ? vector(0, 0, 0) :
                (1 / normalMagnitude) * transportedNormal,
            "normalMagnitude" : normalMagnitude,
            "velocity" : velocity,
            "alpha" : coordinates.alpha,
            "beta" : coordinates.beta,
            "tangentialResidual" : coordinates.residual,
            "lambda" : lambda,
            "lambdaSign" : lambdaSign,
            "timeDerivativeSign" : timeDerivativeSign,
            "timeDerivativeMargin" : timeDerivativeMargin,
            "contactStationary" : contactStationary,
            "chartSign" : lambdaSign * timeDerivativeSign,
            "foldMargin" : foldMargin,
            // PATCH degeneracy only. A vanishing f_t is deliberately NOT in here: the patch
            // verdict is sign(kappa * lambda) and never reads f_t, so a stationary contact set
            // leaves the patch fully determined - it only silences the CO-EDGE rule, which is
            // what contactStationary is for.
            "degenerate" : coordinates.degenerate || normalMagnitude < 1e-300 ||
                lambdaSign == 0 || foldMargin <= foldTolerance
        };
}

/**
 * The velocity's coordinates in the (generally non-orthogonal) tangent basis, through the 2x2
 * Gram system - a least-squares solve, so an off-funnel point still returns usable coordinates
 * plus the residual that says how far off it was.
 * Returns { alpha, beta, residual, degenerate }.
 */
function solveTangentCoordinates(uTangent is Vector, vTangent is Vector, velocity is Vector) returns map
{
    const g11 = dot(uTangent, uTangent);
    const g12 = dot(uTangent, vTangent);
    const g22 = dot(vTangent, vTangent);
    const determinant = g11 * g22 - g12 * g12;
    if (determinant <= 1e-300 * (1 + g11 * g22))
    {
        return { "alpha" : 0, "beta" : 0, "residual" : norm(velocity), "degenerate" : true };
    }
    const r1 = dot(uTangent, velocity);
    const r2 = dot(vTangent, velocity);
    const alpha = (g22 * r1 - g12 * r2) / determinant;
    const beta = (g11 * r2 - g12 * r1) / determinant;
    return {
            "alpha" : alpha,
            "beta" : beta,
            "residual" : norm(velocity - alpha * uTangent - beta * vTangent),
            "degenerate" : false
        };
}

/** -1, 0 or 1; anything within `tolerance` of zero is 0. */
export function signOf(value is number, tolerance is number) returns number
{
    if (value > tolerance)
    {
        return 1;
    }
    return value < -tolerance ? -1 : 0;
}

// ============================= Grazing patch orientation =============================

/**
 * Whether a fitted grazing patch's own parametric normal points out of the swept volume, from
 * ONE funnel sample plus the direction q runs in there (spec 6.6).
 *
 * A fit is parameterized u = station (t), v = q, so its normal is
 * Phi_t x Phi_q = -kappa lambda A N, and it faces outward exactly when kappa lambda < 0.
 * `qDirectionUv` is any uv-space vector pointing the way q increases - the difference of two
 * neighboring grid samples does, since only its sign against (-f_v, f_u) is read.
 *
 * Returns the `envelopeOrientationSample` map extended with {
 *     kappaSign {number} : the q direction's sign against (-f_v, f_u),
 *     facesOutward {boolean} : the fit net's own normal already points out of the swept volume,
 *     flipRequired {boolean} : the q direction must be reversed - the negation of facesOutward,
 *         named for the caller that acts on it,
 *     qDirectionDegenerate {boolean} : q ran perpendicular to the section here, so its sign
 *         says nothing and the caller must supply a longer baseline
 * }
 */
export function fitPatchOrientationAt(strippedMotion is map, strippedSurface is map,
    u is number, v is number, t is number, qDirectionUv is array) returns map
{
    return fitPatchOrientationAt(strippedMotion, strippedSurface, u, v, t, qDirectionUv, {});
}

/** Same, with `envelopeOrientationSample`'s tolerances plus { qDirectionTolerance (default 0) :
    a relative floor under which the q direction's sign counts as undecided }. */
export function fitPatchOrientationAt(strippedMotion is map, strippedSurface is map,
    u is number, v is number, t is number, qDirectionUv is array, options is map) returns map
{
    var result = envelopeOrientationSample(strippedMotion, strippedSurface, u, v, t, options);
    const sectionProjection = -result.vDerivative * qDirectionUv[0] + result.uDerivative * qDirectionUv[1];
    const gradientNorm = sqrt(result.uDerivative ^ 2 + result.vDerivative ^ 2);
    const baselineNorm = sqrt(qDirectionUv[0] ^ 2 + qDirectionUv[1] ^ 2);
    const relativeTolerance = options.qDirectionTolerance == undefined ? 0 : options.qDirectionTolerance;
    const kappaSign = signOf(sectionProjection, relativeTolerance * gradientNorm * baselineNorm);
    result.kappaSign = kappaSign;
    result.qDirectionDegenerate = kappaSign == 0;
    result.facesOutward = kappaSign * result.lambdaSign < 0;
    result.flipRequired = !result.facesOutward;
    return result;
}

/**
 * Certify a whole fit grid's orientation and its freedom from folds (spec 6.6): one
 * `envelopeOrientationSample` per grid sample, asserting that lambda keeps ONE sign over the
 * component, and - when the lifted grid is supplied - cross-checking the analytic verdict
 * against the finite-difference normal of the grid itself. The two routes are independent: one
 * reads lambda off exact derivatives, the other differences the actual fitted points.
 *
 * uvRows[i][j] is the uv of grid row i (station i), column j (q sample j); `stations` are those
 * rows' t values. options: {
 *     liftedGrid {array, optional} : the same-shaped grid of lifted 3D points, which switches
 *         the finite-difference cross-check on,
 *     valueTolerance, foldTolerance, qDirectionTolerance : passed through
 * }
 *
 * Rows whose uv samples are all one point (an island fit's collapsed pole row) contribute no
 * sample and no difference - they are skipped, not failed.
 *
 * Returns {
 *     sampleCount, lambdaSign, lambdaSignConsistent {boolean}, worstFoldMargin,
 *     facesOutward, flipRequired {boolean} : the majority verdict over the grid,
 *     verdictUnanimous {boolean},
 *     differenceChecked, differenceAgreements, differenceDisagreements {number},
 *     worstDifferenceAlignment {number} : the smallest |cos| between a finite-difference grid
 *         normal and the outward normal there - near zero means the grid is nearly folded,
 *     worstTangentialResidual, degenerateSamples {number},
 *     stationarySamples {number}, contactStationary {boolean} : samples where f_t vanishes
 *         relative to lambda's terms - a pure translation makes EVERY sample stationary (spec
 *         7.7). It does not spoil the patch verdict and is not counted against `consistent`;
 *         it does mean this component's co-edges cannot be oriented by the alternation rule,
 *         consistent {boolean}
 * }
 */
export function certifyFitGridOrientation(strippedMotion is map, strippedSurface is map,
    uvRows is array, stations is array, options is map) returns map
{
    const liftedGrid = options.liftedGrid;
    var sampleCount = 0;
    var lambdaSign = 0;
    var lambdaSignConsistent = true;
    var worstFoldMargin = undefined;
    var outwardVotes = 0;
    var inwardVotes = 0;
    var degenerateSamples = 0;
    var stationarySamples = 0;
    var worstTangentialResidual = 0;
    var differenceChecked = 0;
    var differenceAgreements = 0;
    var differenceDisagreements = 0;
    var worstDifferenceAlignment = undefined;

    for (var rowIndex = 0; rowIndex < size(uvRows); rowIndex += 1)
    {
        const row = uvRows[rowIndex];
        if (isCollapsedUvRow(row))
        {
            continue;
        }
        for (var columnIndex = 0; columnIndex < size(row); columnIndex += 1)
        {
            const qDirection = gridQDirection(row, columnIndex);
            if (qDirection == undefined)
            {
                continue;
            }
            const orientation = fitPatchOrientationAt(strippedMotion, strippedSurface,
                row[columnIndex][0], row[columnIndex][1], stations[rowIndex], qDirection, options);
            sampleCount += 1;
            worstTangentialResidual = max(worstTangentialResidual, orientation.tangentialResidual);
            if (orientation.contactStationary)
            {
                stationarySamples += 1;
            }
            if (orientation.degenerate || orientation.qDirectionDegenerate)
            {
                degenerateSamples += 1;
                continue;
            }
            if (lambdaSign == 0)
            {
                lambdaSign = orientation.lambdaSign;
            }
            else if (orientation.lambdaSign != lambdaSign)
            {
                lambdaSignConsistent = false;
            }
            if (worstFoldMargin == undefined || orientation.foldMargin < worstFoldMargin)
            {
                worstFoldMargin = orientation.foldMargin;
            }
            if (orientation.facesOutward)
            {
                outwardVotes += 1;
            }
            else
            {
                inwardVotes += 1;
            }
            if (liftedGrid == undefined)
            {
                continue;
            }
            const difference = differenceNormalAt(liftedGrid, rowIndex, columnIndex);
            if (difference == undefined)
            {
                continue;
            }
            const differenceMagnitude = norm(difference);
            if (differenceMagnitude < 1e-300)
            {
                continue;
            }
            const alignment = dot((1 / differenceMagnitude) * difference, orientation.outwardNormal);
            differenceChecked += 1;
            if ((alignment > 0) == orientation.facesOutward)
            {
                differenceAgreements += 1;
            }
            else
            {
                differenceDisagreements += 1;
            }
            if (worstDifferenceAlignment == undefined || abs(alignment) < worstDifferenceAlignment)
            {
                worstDifferenceAlignment = abs(alignment);
            }
        }
    }
    const facesOutward = outwardVotes >= inwardVotes;
    return {
            "sampleCount" : sampleCount,
            "lambdaSign" : lambdaSign,
            "lambdaSignConsistent" : lambdaSignConsistent,
            "worstFoldMargin" : worstFoldMargin == undefined ? 0 : worstFoldMargin,
            "facesOutward" : facesOutward,
            "flipRequired" : !facesOutward,
            "verdictUnanimous" : outwardVotes == 0 || inwardVotes == 0,
            "differenceChecked" : differenceChecked,
            "differenceAgreements" : differenceAgreements,
            "differenceDisagreements" : differenceDisagreements,
            "worstDifferenceAlignment" : worstDifferenceAlignment == undefined ? 0 : worstDifferenceAlignment,
            "worstTangentialResidual" : worstTangentialResidual,
            "degenerateSamples" : degenerateSamples,
            "stationarySamples" : stationarySamples,
            "contactStationary" : stationarySamples == sampleCount && sampleCount > 0,
            "consistent" : lambdaSignConsistent && differenceDisagreements == 0 &&
                (outwardVotes == 0 || inwardVotes == 0) && degenerateSamples == 0
        };
}

/** Whether every uv sample of a row is the same point - an island fit's collapsed pole row. */
function isCollapsedUvRow(row is array) returns boolean
{
    for (var index = 1; index < size(row); index += 1)
    {
        if (abs(row[index][0] - row[0][0]) > 1e-14 || abs(row[index][1] - row[0][1]) > 1e-14)
        {
            return false;
        }
    }
    return true;
}

/**
 * The uv direction q increases in at one column: centered where both neighbors exist, one sided
 * at either end. undefined when the row is too short, so the return type is deliberately
 * undeclared.
 *
 * Deliberately NEVER wraps the index, even on a closed row. A tube fit's loop carries u
 * UNWRAPPED past the seam (spec 7.5), so the difference between its last and first samples is a
 * period-sized jump pointing the wrong way - it would flip kappa's sign at exactly one column
 * and report a spurious inconsistency. A non-wrapping adjacent difference is always available
 * and always right for a direction test, which is all kappa's sign needs.
 */
function gridQDirection(row is array, columnIndex is number)
{
    const columnCount = size(row);
    if (columnCount < 2)
    {
        return undefined;
    }
    const lowIndex = max(0, columnIndex - 1);
    const highIndex = min(columnCount - 1, columnIndex + 1);
    return [row[highIndex][0] - row[lowIndex][0], row[highIndex][1] - row[lowIndex][1]];
}

/** The finite-difference normal of a lifted fit grid at one sample: (d/d station) x (d/d q),
    which is the fitted net's own parametric normal since the fit runs u = station, v = q. Uses
    the same non-wrapping neighbors gridQDirection does, so the two routes are differencing the
    same pair. undefined where the grid is too small to difference, so the return type is
    undeclared. */
function differenceNormalAt(liftedGrid is array, rowIndex is number, columnIndex is number)
{
    const rowCount = size(liftedGrid);
    const lowRow = max(0, rowIndex - 1);
    const highRow = min(rowCount - 1, rowIndex + 1);
    const row = liftedGrid[rowIndex];
    const columnCount = size(row);
    if (lowRow == highRow || columnCount < 2)
    {
        return undefined;
    }
    const lowColumn = max(0, columnIndex - 1);
    const highColumn = min(columnCount - 1, columnIndex + 1);
    return cross(liftedGrid[highRow][columnIndex] - liftedGrid[lowRow][columnIndex],
        row[highColumn] - row[lowColumn]);
}

// ============================= Envelope co-edge orientation =============================

/**
 * Orient every envelope co-edge that one input co-edge generates, and certify the alternation
 * that makes it cheap (spec 6.6; framework paper 5.3, Proposition 14).
 *
 * `normals` and `points` are swSweepEmit's sideNormals / edgePoints arrays for ONE side of the
 * co-edge - the same shared arrays the strip marching ran on, so no geometry is re-derived
 * here. `branches` is `marchStripZeroCurves`' output for that side.
 *
 * The strip function's time derivative is evaluated at EVERY branch sample rather than
 * propagated from one sign by alternation, which turns the rule into a certificate: at a fixed
 * co-edge parameter the roots in t must alternate in sign (Rolle), so two neighbors sharing a
 * sign mean a root was missed or two merged - SWEEP_FUNNEL_TANGENT_TO_SLICE.
 *
 * options: { valueTolerance (default 0) : |g_t| inside this counts as a tangency, not a sign }.
 *
 * Returns {
 *     branches {array}, one per input branch: {
 *         timeDerivativeSign {number} : the branch's sign, taken at its widest-margin sample,
 *         signChanges {number} : how many times the sign flips ALONG the branch - a nonzero
 *             count is a fold in the co-edge parameter, and the branch must be split there,
 *         foldColumns {array} : the sample indices those flips straddle,
 *         minimumMagnitude {number} : the smallest |g_t| on the branch,
 *         sampleSigns {array}
 *     },
 *     alternationChecked {number} : how many neighboring root pairs were compared,
 *     alternationViolations {array} : { sampleIndex, lowerT, upperT, sign },
 *     alternationConsistent {boolean},
 *     tangencyCount {number} : branch samples whose |g_t| fell inside tolerance
 * }
 */
export function orientStripBranches(strippedMotion is map, normals is array, points is array,
    branches is array, options is map) returns map
{
    const valueTolerance = options.valueTolerance == undefined ? 0 : options.valueTolerance;
    var branchResults = makeArray(size(branches));
    var tangencyCount = 0;

    // Every root gathered by the column it lands in, so the alternation reads off afterward.
    var columnRoots = makeArray(size(normals));
    for (var columnIndex = 0; columnIndex < size(normals); columnIndex += 1)
    {
        columnRoots[columnIndex] = [];
    }
    for (var branchIndex = 0; branchIndex < size(branches); branchIndex += 1)
    {
        const samples = branches[branchIndex].samples;
        var signs = makeArray(size(samples), 0);
        var minimumMagnitude = undefined;
        var bestMagnitude = -1;
        var branchSign = 0;
        for (var sampleIndex = 0; sampleIndex < size(samples); sampleIndex += 1)
        {
            const sample = samples[sampleIndex];
            const derivative = evaluateContactFunctionTimeDerivative(strippedMotion,
                normals[sample.sampleIndex], points[sample.sampleIndex], sample.t);
            const magnitude = abs(derivative);
            signs[sampleIndex] = signOf(derivative, valueTolerance);
            if (signs[sampleIndex] == 0)
            {
                tangencyCount += 1;
            }
            if (minimumMagnitude == undefined || magnitude < minimumMagnitude)
            {
                minimumMagnitude = magnitude;
            }
            // The branch's sign comes from its most confident sample, not its first: a branch
            // that starts at a near tangency would otherwise be labelled by its worst point.
            if (magnitude > bestMagnitude)
            {
                bestMagnitude = magnitude;
                branchSign = signs[sampleIndex];
            }
            columnRoots[sample.sampleIndex] = append(columnRoots[sample.sampleIndex],
                { "t" : sample.t, "sign" : signs[sampleIndex] });
        }
        var signChanges = 0;
        var foldColumns = [];
        var previousSign = 0;
        for (var sampleIndex = 0; sampleIndex < size(samples); sampleIndex += 1)
        {
            if (signs[sampleIndex] == 0)
            {
                continue;
            }
            if (previousSign != 0 && signs[sampleIndex] != previousSign)
            {
                signChanges += 1;
                foldColumns = append(foldColumns, samples[sampleIndex].sampleIndex);
            }
            previousSign = signs[sampleIndex];
        }
        branchResults[branchIndex] = {
                "timeDerivativeSign" : branchSign,
                "signChanges" : signChanges,
                "foldColumns" : foldColumns,
                "minimumMagnitude" : minimumMagnitude == undefined ? 0 : minimumMagnitude,
                "sampleSigns" : signs
            };
    }

    var alternationChecked = 0;
    var alternationViolations = [];
    for (var columnIndex = 0; columnIndex < size(columnRoots); columnIndex += 1)
    {
        if (size(columnRoots[columnIndex]) < 2)
        {
            continue;
        }
        const sorted = sort(columnRoots[columnIndex], function(a, b)
            {
                return a.t - b.t;
            });
        for (var index = 0; index + 1 < size(sorted); index += 1)
        {
            if (sorted[index].sign == 0 || sorted[index + 1].sign == 0)
            {
                continue;
            }
            alternationChecked += 1;
            if (sorted[index].sign == sorted[index + 1].sign)
            {
                alternationViolations = append(alternationViolations, {
                            "sampleIndex" : columnIndex,
                            "lowerT" : sorted[index].t,
                            "upperT" : sorted[index + 1].t,
                            "sign" : sorted[index].sign
                        });
            }
        }
    }
    return {
            "branches" : branchResults,
            "alternationChecked" : alternationChecked,
            "alternationViolations" : alternationViolations,
            "alternationConsistent" : size(alternationViolations) == 0,
            "tangencyCount" : tangencyCount
        };
}

/**
 * Which way an input co-edge runs along its edge's own parameter, for the face on the given
 * side. swSweepEmit decides "left" by the kernel's own `usingFaceOrientation` tangent: walking
 * the edge's default (arc-length increasing) direction keeps the left face on the left. So the
 * left face's co-edge traverses +s and the right face's traverses -s.
 */
export function coEdgeSense(side is string) returns number
{
    if (side == "left")
    {
        return 1;
    }
    if (side == "right")
    {
        return -1;
    }
    throw "swOrientation: co-edge side must be \"left\" or \"right\", got \"" ~ side ~ "\".";
}

/**
 * The direction an envelope co-edge is traversed, in units of the input edge's own parameter s
 * (framework paper Proposition 14): the input co-edge's sense times sign(lambda / f_t).
 *
 * `lambdaSign` is the FUNNEL COMPONENT's sign - one number for the whole grazing patch, since
 * lambda cannot change sign on a component without folding it. `timeDerivativeSign` is the
 * per-branch sign from `orientStripBranches`, and it is the factor that alternates.
 * Returns +1 (increasing s) or -1, and 0 when either input is undecided.
 */
export function envelopeCoEdgeDirection(inputSense is number, lambdaSign is number,
    timeDerivativeSign is number) returns number
{
    return inputSense * lambdaSign * timeDerivativeSign;
}

/**
 * The partner co-edge's direction on the other face sharing this lateral boundary
 * (sharp-features paper 7.2): a sharp-edge envelope face traverses the shared co-edge opposite
 * to the grazing patch meeting it there. The same statement holds for any two faces sharing a
 * co-edge in a coherently oriented shell, which is what makes it the knit's consistency check
 * rather than a special case.
 */
export function partnerCoEdgeDirection(direction is number) returns number
{
    return -direction;
}

// ============================= Sharp-edge face orientation =============================

/**
 * Which normal of a sharp edge's envelope face points out of the swept volume (sharp-features
 * paper 7.4). That face is the transported edge, Phi(s, t) = A e(s) + b, so its parametric
 * normal is (A e') x velocity; the outward one is whichever of the two candidates lies inside
 * the TRANSPORTED cone of normals. For a convex edge that cone is the wedge between the two
 * adjacent faces' one-sided normals, and every vector here is perpendicular to the transported
 * edge tangent, so the test is a signed-angle comparison inside that one plane.
 *
 * `edgeTangent` is e'(s) (any positive multiple); `leftNormal` and `rightNormal` are the
 * co-edge record's one-sided unit normals at the same s. All tool frame.
 *
 * Returns {
 *     parametricNormal {Vector} : (A e') x velocity, unnormalized,
 *     outwardNormal {Vector} : unit, the cone-selected one,
 *     flipRequired {boolean} : the (s, t) sheet's own normal points inward,
 *     insideCone {boolean} : one of the two candidates was in the cone,
 *     wedgeSine {number} : the signed sine between the two side normals about the tangent -
 *         near zero means the edge is not sharp here, or the two normals are opposed,
 *     coneMargin {number} : how far inside the wedge the chosen normal sits, scale free,
 *     degenerate {boolean} : a vanishing velocity or tangent, a collapsed wedge, or NEITHER
 *         candidate inside the cone - in which case the edge does not graze at this (s, t)
 * }
 */
export function orientSharpEdgeFace(strippedMotion is map, edgePoint is Vector, edgeTangent is Vector,
    leftNormal is Vector, rightNormal is Vector, t is number) returns map
{
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * edgePoint + sample.translationDerivative;
    const transportedTangent = sample.rotation * edgeTangent;
    const tangentNorm = norm(transportedTangent);
    const parametricNormal = cross(transportedTangent, velocity);
    const parametricNorm = norm(parametricNormal);
    var failure = {
            "parametricNormal" : parametricNormal,
            "outwardNormal" : vector(0, 0, 0),
            "flipRequired" : false,
            "insideCone" : false,
            "wedgeSine" : 0,
            "coneMargin" : 0,
            "degenerate" : true
        };
    if (tangentNorm < 1e-300 || parametricNorm < 1e-300)
    {
        return failure;
    }
    const axis = (1 / tangentNorm) * transportedTangent;
    const candidate = (1 / parametricNorm) * parametricNormal;
    const wedgeStart = projectOffAxis(sample.rotation * leftNormal, axis);
    const wedgeEnd = projectOffAxis(sample.rotation * rightNormal, axis);
    if (norm(wedgeStart) < 1e-300 || norm(wedgeEnd) < 1e-300)
    {
        return failure;
    }
    const start = (1 / norm(wedgeStart)) * wedgeStart;
    const end = (1 / norm(wedgeEnd)) * wedgeEnd;
    const wedgeSine = dot(axis, cross(start, end));
    failure.wedgeSine = wedgeSine;
    if (abs(wedgeSine) < 1e-300)
    {
        return failure;
    }
    const forward = wedgeMembership(axis, start, end, candidate, wedgeSine);
    const backward = wedgeMembership(axis, start, end, -candidate, wedgeSine);
    if (!forward.inside && !backward.inside)
    {
        return failure;
    }
    return {
            "parametricNormal" : parametricNormal,
            "outwardNormal" : forward.inside ? candidate : -candidate,
            "flipRequired" : !forward.inside,
            "insideCone" : true,
            "wedgeSine" : wedgeSine,
            "coneMargin" : forward.inside ? forward.margin : backward.margin,
            "degenerate" : false
        };
}

/** A vector with its component along `axis` (unit) removed. */
function projectOffAxis(value is Vector, axis is Vector) returns Vector
{
    return value - dot(value, axis) * axis;
}

/**
 * Whether `candidate` lies in the wedge swept from `start` to `end` the way `wedgeSine` turns,
 * with all four vectors in the plane normal to `axis`. Both partial turns must go the same way
 * as the wedge itself.
 * Returns { inside, margin } - the margin being the smaller partial turn, relative to the wedge.
 */
function wedgeMembership(axis is Vector, start is Vector, end is Vector, candidate is Vector,
    wedgeSine is number) returns map
{
    const toCandidate = dot(axis, cross(start, candidate));
    const fromCandidate = dot(axis, cross(candidate, end));
    const wedgeDirection = wedgeSine > 0 ? 1 : -1;
    return {
            "inside" : toCandidate * wedgeDirection >= 0 && fromCandidate * wedgeDirection >= 0,
            "margin" : min(abs(toCandidate), abs(fromCandidate)) / abs(wedgeSine)
        };
}

// ============================= Sharp-vertex co-edge orientation =============================

/**
 * Which way a sharp vertex's trajectory edge is traversed for the sharp-edge face it bounds
 * (sharp-features paper 7.1). That edge IS the vertex's trajectory, so its tangent is the
 * vertex velocity w; the face lies to its left with respect to the face's outward normal n
 * exactly when n x w points into the face - and at the vertex, "into the face" is the
 * transported direction the edge's own parameter runs from that end.
 *
 * `edgeDirectionIntoEdge` is +e'(s0) at the start vertex and -e'(s1) at the end vertex - the
 * caller picks, since only it knows which end this vertex is. `faceOutwardNormal` is the
 * world-space normal `orientSharpEdgeFace` returned for the same (s, t).
 *
 * Returns {
 *     tangent {Vector} : the envelope edge's oriented unit tangent,
 *     direction {number} : +1 when that is the vertex velocity, -1 when it is its negative,
 *     testValue {number} : the signed test, scale free,
 *     degenerate {boolean} : a vanishing velocity, or a test too close to zero to decide
 * }
 */
export function orientSharpVertexCoEdge(strippedMotion is map, vertexPoint is Vector,
    edgeDirectionIntoEdge is Vector, faceOutwardNormal is Vector, t is number) returns map
{
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * vertexPoint + sample.translationDerivative;
    const speed = norm(velocity);
    const interiorDirection = sample.rotation * edgeDirectionIntoEdge;
    const interiorNorm = norm(interiorDirection);
    if (speed < 1e-300 || interiorNorm < 1e-300)
    {
        return { "tangent" : vector(0, 0, 0), "direction" : 0, "testValue" : 0, "degenerate" : true };
    }
    const tangent = (1 / speed) * velocity;
    const testValue = dot((1 / interiorNorm) * interiorDirection, cross(faceOutwardNormal, tangent));
    const direction = testValue > 0 ? 1 : -1;
    return {
            "tangent" : direction * tangent,
            "direction" : direction,
            "testValue" : testValue,
            "degenerate" : testValue == 0
        };
}

// ============================= Cap face classification =============================

/**
 * Which side of an ingress or egress cap survives (spec 1.1 and 9): the ingress cap keeps the
 * tool boundary where f <= 0, the egress cap where f >= 0. A cap face is a piece of the
 * transported tool's own boundary, so its outward normal is already the tool's - `opPattern`
 * of the tool body carries it, and there is nothing to flip.
 *
 * Returns { value, keep {boolean}, marginal {boolean} } - `marginal` flags a sample within
 * tolerance of the contact curve, where the classification is not decidable and the caller
 * should sample further from the trim.
 */
export function classifyCapSample(strippedMotion is map, strippedSurface is map, u is number,
    v is number, t is number, isIngress is boolean, valueTolerance is number) returns map
{
    const value = evaluateEnvelopePointwise(strippedMotion, strippedSurface, u, v, t);
    const valueSign = signOf(value, valueTolerance);
    return {
            "value" : value,
            "keep" : isIngress ? valueSign <= 0 : valueSign >= 0,
            "marginal" : valueSign == 0
        };
}

// ============================= Applying a flip =============================

/**
 * Reverse a fit grid row's q direction. A CLAMPED row (the rectangle fit) reverses outright; a
 * CLOSED row (island and tube fits, stored as n distinct samples with no repeated closer) has
 * to hold its first sample fixed and reverse the rest, so the q origin - which those fits go to
 * some trouble to keep consistent from station to station - does not move.
 */
export function reverseFitGridRow(row is array, closed is boolean) returns array
{
    const count = size(row);
    var reversed = makeArray(count);
    for (var index = 0; index < count; index += 1)
    {
        reversed[index] = closed ? row[(count - index) % count] : row[count - 1 - index];
    }
    return reversed;
}

/**
 * Reverse the row of q MIDPOINTS that goes with a reversed row. In both the clamped and the
 * closed convention this is a plain reversal: midpoint j sits between samples j and j + 1, and
 * reversing the samples reverses the intervals between them in step. Kept as its own function
 * because getting it wrong silently mis-attributes certification samples rather than failing.
 */
export function reverseFitGridMidRow(midRow is array) returns array
{
    const count = size(midRow);
    var reversed = makeArray(count);
    for (var index = 0; index < count; index += 1)
    {
        reversed[index] = midRow[count - 1 - index];
    }
    return reversed;
}

/**
 * Reverse a fitted surface's v (q) direction outright: control columns and weights reversed, v
 * knots and v parameters mirrored about their own span. Exact - no geometry moves, the surface's
 * normal simply flips - so this is the post-hoc route for a patch that is already fitted.
 *
 * Refuses a v-periodic net: reversing the wrap padding of the module's periodic convention is
 * not the same operation, and the island and tube fits have a better route anyway - reverse
 * their grid rows with `reverseFitGridRow` before interpolating.
 */
export function reverseFitSurfaceQDirection(surface is map) returns map
{
    if (surface.isVPeriodic)
    {
        throw "swOrientation: cannot reverse a v-periodic fit net in place - reverse the fit " ~
            "grid rows with reverseFitGridRow before interpolating instead.";
    }
    const rowCount = size(surface.controlPoints);
    const columnCount = size(surface.controlPoints[0]);
    var controlPoints = makeArray(rowCount);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        var row = makeArray(columnCount);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            row[columnIndex] = surface.controlPoints[rowIndex][columnCount - 1 - columnIndex];
        }
        controlPoints[rowIndex] = row;
    }
    var reversed = surface;
    reversed.controlPoints = controlPoints;
    reversed.vKnots = mirrorParameters(surface.vKnots);
    if (surface.vParameters != undefined)
    {
        reversed.vParameters = mirrorParameters(surface.vParameters);
    }
    if (surface.isRational && surface.weights != undefined)
    {
        var weights = makeArray(rowCount);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            var row = makeArray(columnCount);
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
            {
                row[columnIndex] = surface.weights[rowIndex][columnCount - 1 - columnIndex];
            }
            weights[rowIndex] = row;
        }
        reversed.weights = weights;
    }
    return reversed;
}

/** An ascending parameter array mirrored about its own span: k -> first + last - k, reversed. */
function mirrorParameters(parameters is array) returns array
{
    const count = size(parameters);
    const total = parameters[0] + parameters[count - 1];
    var mirrored = makeArray(count);
    for (var index = 0; index < count; index += 1)
    {
        mirrored[index] = total - parameters[count - 1 - index];
    }
    return mirrored;
}
