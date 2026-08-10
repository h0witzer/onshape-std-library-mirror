FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/containers.fs", version : "3044.0");
import(path : "onshape/std/math.fs", version : "3044.0");
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");   // knotArray, bSplineCurve, makeUniformKnotArray (used by the hooks)
import(path : "onshape/std/splineUtils.fs", version : "3044.0");     // evaluateSpline (curves), elevateBezierDegree (used by the elevation hooks)
import(path : "onshape/std/nurbsUtils.fs", version : "3044.0");      // removeKnots, combinePointsAndWeights, separatePointsAndWeights (used by the hooks)

/*
    Spline Refinement Utilities
    ===========================

    Exact B-spline refinement as a shared, importable module: knot insertion, knot refinement,
    clamped segment extraction, Bezier decomposition, and degree elevation. Every operation in
    that core adds representational degrees of freedom while leaving the geometry bit-for-bit
    unchanged.

    The LOSSY operations that did land here are the ones std cannot do at all, and each carries a
    measured `deviation` rather than a boolean: simplification to a control point count, redundant
    knot removal across a whole knot line at once (spec section 2.2 — deciding removability for a
    line rather than a curve is what makes A5.8 a surface algorithm), and periodic simplification by
    cyclic projection (spec section 2.2.0 — the one operation with no exact alternative, since a
    NURBS circle is genuinely only C0 at its arc joins in homogeneous space). Plain single-curve
    removal and approximation are still std's job, via nurbsUtils.fs and splineUtils.fs.

    Design, migration plan, validation vectors, and the mathematical references live in
    docs/specs/SPLINE_REFINEMENT_UTILITY_SPEC.md. The insertion arithmetic is validated against
    docs/specs/DISPLACEMENT_MAP_TILING_SPEC.md sections 5.1-5.4 and The NURBS Book chapter 5.
    The companion feature splineRefinementTester.fs runs the validation vectors and reports
    pass/fail; no change to this module should ship without a clean tester run in Onshape.

    LAYERING (see spec section 3):
      Layer 1 - knot vector arithmetic. Pure functions on plain number arrays.
      Layer 2 - the refinement operator. Knot refinement is a FIXED LINEAR MAP on control
                points: it depends only on (knots, degree, parametersToInsert), never on the
                control point values. We build that map once, symbolically, by running Boehm
                insertion on unit-basis coefficient rows, and store it sparsely. Applying it is
                cheap, reusable across every row/column of a surface grid, and type-agnostic
                (works on length Vectors, homogeneous 4-vectors, unitless parameter points).
                This layer is also the algorithm seam: a future Oslo-style builder changes
                nothing above it.
      Layer 3 - task-level entry points composed from Layers 1-2. Several are HOOK stubs, see
                below.

    CONVENTIONS (spec section 5 — every implementer must follow these):
      - A knot array always has size(controlPoints) + degree + 1 entries. The overlapping-
        control-point periodic form returned by some evaluators is normalized away on entry
        (normalizeSplineDefinition).
      - Span rule: the span of parameter u is the LARGEST index k with
        knots[k] <= u < knots[k+1]; the strict right inequality automatically skips
        zero-width (repeated-knot) spans.
      - Refinement entry points reject an insertion once the resulting multiplicity would
        exceed `degree` (a degree-multiplicity knot is already a kink; degree+1 splits the
        spline). Clamped segment extraction deliberately goes to degree+1 — that is slicing,
        not refining in place.
      - Periodic policy: refining a periodic spline clamps it first and reports that in the
        returned map. Never hand a periodic flag alongside knots that no longer satisfy the
        periodic padding relation.
      - Multiply scalars on the LEFT of unit-carrying Vectors: `weight * point`, never
        `point * weight` (Onshape's isLength checks reject the latter in some contexts).
      - Preallocate with makeArray + index assignment in every loop that grows with control
        point count. append() in a loop is O(n^2) and has been a measured multi-second cost.
      - No op* or ev* calls anywhere in this module: it is pure arithmetic, testable without a
        Context. Anything needing a Context belongs in the calling feature.
      - Every exported function that RETURNS a spline/surface definition map must cast its
        knots/uKnots/vKnots field with `knotArray(...)` before returning, even though the
        Layer 1/2 operators underneath all produce plain arrays. FeatureScript's typecheck
        types do not propagate through array operations, so a plain array does not implicitly
        satisfy `is KnotArray` even when its values are valid — bSplineCurve and
        opCreateBSplineSurface will reject it. Every curve-level function below already does
        this; see the KNOTARRAY DISCIPLINE note above the surface hooks for the precedent
        (tweenSurfaces.fs has its own defensive cast for the same reason).

    STATUS (see docs/specs/SPLINE_REFINEMENT_UTILITY_SPEC.md section 7 for the phased plan):
      NO STUBS REMAIN — every exported function is implemented. Layers 1-2, direct insertion,
        the surface evaluator, all of curve-level Layer 3, and the periodic machinery (refine,
        elevate, share, reverse, re-window, two-sided seam alignment, closed-clamped conversion)
        are implemented AND tester-verified passing in Onshape across many live runs.
      Confirmed live through 2026-08-09, each batch on its first run: the derivative layer
        (SURFACE-DERIV), simplification (SIMPLIFY), interpolation (INTERPOLATE), and the ASSEMBLY
        layer (ISOCURVE, CONCAT, LOFT) — isocurve extraction, transposition, curve and surface
        concatenation, targeted seam-knot removal, and lofting, including the unisolvence
        reconstruction anchor and the split -> concatenate -> heal exact round trip. Every entry
        point in this module is now live-verified.

    ADDING A NEW ENTRY POINT — the standing rules, learned the hard way:
      1. Read the spec section it cites first. Do not change an existing function's signature,
         and do not modify an already-verified function to make a new one pass — if a verified
         function looks wrong, stop and flag it instead.
      2. Follow the conventions block above, especially preallocation, scalar-on-left, and the
         KnotArray-casting discipline on every return.
      3. knotRefinementOperator / periodicRefinementOperator ONLY where the same refinement runs
         across many point arrays (a surface's rows or columns). For a single array use
         refineKnotVector — an operator built for one array is reuse that never happens.
      4. It is DONE when its tester vector passes in Onshape, not when it compiles. Every
         periodic vector needs a degree >= 2 case; degree 1 hides an entire class of bug.
*/

// ============================================================================================
// Constants and types
// ============================================================================================

/**
 * Tolerance for deciding that two knot parameter values are the same knot. Used for
 * multiplicity counting and for locating inserted knots after refinement.
 */
export const KNOT_PARAMETER_TOLERANCE = 1e-10;

/**
 * Operator weights with absolute value at or below this cutoff are dropped when a dense
 * coefficient row is compacted to sparse form. Matches the displacement-map implementation
 * this refinementOperator formulation was extracted from.
 */
export const SPARSE_WEIGHT_CUTOFF = 1e-12;

/**
 * The largest target control point count per period periodic simplification will attempt before
 * giving up and returning the best result it reached, WITH that result's true deviation.
 *
 * A ceiling on n rather than on doublings because the projection's cost is governed by n: the
 * normal matrix is n x n and its factorization is O(n^3), so an unbounded doubling schedule reaches
 * an unusable solve long before it reaches an interesting tolerance. 128 is far past where accuracy
 * stops being the binding constraint — a uniform periodic cubic on a circle carries radial error
 * about r*(2*pi/n)^4/384, so n = 24 already lands near a micron on a 100 mm part. This is a guard
 * against a tolerance no representation could meet, not a working limit.
 */
export const PERIODIC_SIMPLIFICATION_MAX_CONTROL_POINTS = 128;

/**
 * The algorithm used to build a refinement refinementOperator. BOEHM inserts the requested parameters
 * one at a time (Boehm single knot insertion). A future OSLO entry (Oslo / discrete B-spline
 * algorithm, whole knot set in one pass) can be added here without changing any caller —
 * the refinementOperator representation is algorithm-independent.
 */
export enum KnotInsertionAlgorithm
{
    BOEHM
}

// ============================================================================================
// Layer 1 — knot vector arithmetic (pure, no geometry)
// ============================================================================================

/**
 * Find the knot span containing `parameter`: the LARGEST index `spanIndex` such that
 * `knots[spanIndex] <= parameter < knots[spanIndex + 1]`, bounded to the valid insertion range
 * `[degree, controlPointCount - 1]`.
 *
 * The strict right inequality means zero-width spans (repeated knots) can never be returned,
 * and a parameter equal to an existing knot value lands in the span that STARTS at that value.
 * Both properties are what make repeated insertion at one parameter (needed for clamping)
 * terminate correctly.
 *
 * Special case at the domain end (NURBS Book Algorithm A2.1's FindSpan: `u == U[n+1] -> n`):
 * without it, a parameter exactly equal to the array's OWN reported domain end
 * (`knots[controlPointCount]`) has its naive backward scan walk PAST the last valid span into
 * whatever knot values happen to exist beyond it. For an array that carries extra trailing
 * structure past its own domain — a periodic wrap window, or any array this domain is only a
 * sub-range of — that "beyond" is a real, different, larger value, not a degenerate repeat of
 * the end, so the scan finds a genuinely different (and out-of-range) span. This is exactly
 * what normalizeSplineDefinition's periodic clamp does (clampedSegmentOperator over the
 * array's own knotDomain), which is why it went uncaught until that path exercised it.
 *
 * @param knots {array} : non-decreasing plain numbers
 * @param degree {number} : spline degree (documentation of intent; the search itself is
 *      degree-independent)
 * @param parameter {number} : must satisfy knots[0] <= parameter <= knots[size - 1]
 * @returns {number} : the span index, always within [degree, size(knots) - degree - 2]
 * @throws if no span contains the parameter (out of range)
 */
export function findKnotSpanIndex(knots is array, degree is number, parameter is number) returns number
{
    const controlPointCount = size(knots) - degree - 1;
    if (abs(parameter - knots[controlPointCount]) <= KNOT_PARAMETER_TOLERANCE)
    {
        return controlPointCount - 1;
    }

    for (var spanIndex = size(knots) - 2; spanIndex >= 0; spanIndex -= 1)
    {
        if (knots[spanIndex] <= parameter && parameter < knots[spanIndex + 1])
        {
            return spanIndex;
        }
    }
    throw "splineRefinementUtils: parameter " ~ parameter ~ " is not inside any span of the knot vector (domain is [" ~
        knots[degree] ~ ", " ~ knots[controlPointCount] ~ "]).";
}

/**
 * Count how many knots equal `parameter`, within KNOT_PARAMETER_TOLERANCE.
 * Returns 0 when the parameter is not an existing knot.
 */
export function knotMultiplicity(knots is array, parameter is number) returns number
{
    var multiplicity = 0;
    for (var knotIndex = 0; knotIndex < size(knots); knotIndex += 1)
    {
        if (abs(knots[knotIndex] - parameter) <= KNOT_PARAMETER_TOLERANCE)
        {
            multiplicity += 1;
        }
    }
    return multiplicity;
}

/**
 * The parameter interval on which a spline with these knots and this degree is defined.
 * @returns {map} : { "start" : knots[degree], "end" : knots[size - degree - 1] }
 */
export function knotDomain(knots is array, degree is number) returns map
{
    return {
            "start" : knots[degree],
            "end" : knots[size(knots) - degree - 1]
        };
}

/**
 * True when the first `degree + 1` knots are equal and the last `degree + 1` knots are equal
 * (within KNOT_PARAMETER_TOLERANCE) — the clamped form, where the spline interpolates its end
 * control points.
 */
export function isClampedKnotArray(knots is array, degree is number) returns boolean
{
    const knotCount = size(knots);
    for (var offset = 1; offset <= degree; offset += 1)
    {
        if (abs(knots[offset] - knots[0]) > KNOT_PARAMETER_TOLERANCE)
        {
            return false;
        }
        if (abs(knots[knotCount - 1 - offset] - knots[knotCount - 1]) > KNOT_PARAMETER_TOLERANCE)
        {
            return false;
        }
    }
    return true;
}

// ============================================================================================
// Layer 2 — the refinement refinementOperator
// ============================================================================================
//
// Operator representation (spec section 3, Layer 2):
// {
//     "degree"      : number,   // spline degree the refinementOperator was built for
//     "inputCount"  : number,   // control point count the refinementOperator consumes
//     "outputCount" : number,   // control point count the refinementOperator produces
//     "knots"       : array,    // the knot vector of the OUTPUT spline
//     "rows"        : array     // rows[outputIndex] = array of { "index", "weight" } terms;
// }                             // output CP = sum over terms of weight * input CP [index]
//
// Every row of a legal refinementOperator is a convex combination: weights are non-negative and sum
// to 1. The tester asserts this invariant (vector 3); it is what guarantees refined control
// points never leave the convex hull of the originals.

// --------------------------------------------------------------------------------------------
// BANDED COEFFICIENT ROWS — the representation buildRefinementCoefficients accumulates in.
//
// A row is `{ "start" : number, "weights" : array }`, meaning input index `start + k` carries
// weight `weights[k]` and every index outside that window carries zero. It is the sparse form the
// operator ends in, minus the per-term map, which is what makes it usable as an ACCUMULATOR.
//
// This replaced a dense `inputCount`-wide array per row, and the reason is a complexity one rather
// than a tidiness one. A discrete B-spline coefficient row has at most `degree + 1` nonzero terms
// NO MATTER HOW MANY KNOTS ARE INSERTED — local support survives refinement — so the dense form
// spent O(inputCount) on every blend, every identity row, and every sparsify scan to carry
// `degree + 1` real numbers. The whole builder was O(insertions * degree * inputCount + inputCount^2)
// to produce O(insertions * degree) worth of coefficients. Banded rows make it
// O(insertions * degree^2), which is independent of the control point count entirely.
//
// THE ARITHMETIC IS UNCHANGED, deliberately and checkably. Every blend below zero-fills the union
// window and evaluates the identical two-term expression the dense version evaluated at that index,
// in the same order — a position present in only one row contributes `scale * 0`, which is exactly
// zero, and adding it changes no bits. This function is upstream of every refinement in the module,
// so it is the last place a "small" numerical drift would be acceptable.
// --------------------------------------------------------------------------------------------

/**
 * Drop leading and trailing EXACT zeros from a banded row, keeping at least one entry.
 *
 * This is the width control. A blend's union window can come out one wider than either input, since
 * the two rows are offset by one; discrete B-spline locality says the operator's rows never exceed
 * `degree + 1` nonzeros however many knots are inserted, so the surplus end position is a
 * structural zero, and dropping it keeps the width at the bound instead of letting it ratchet up
 * one per insertion.
 *
 * WHAT IS AND IS NOT GUARANTEED, because the difference decides how much to trust the bound. Trimming
 * an exact zero is unconditionally safe: every later reader multiplies the entry by something and
 * adds it, and `scale * 0` contributes zero whether the entry is stored or implied. What is NOT
 * asserted here is that the surplus position always lands on exactly 0.0 in floating point rather
 * than on something tiny. If it ever does not, this row keeps one extra entry and the next blend
 * starts from a slightly wider window — the result stays exactly right and the function stays
 * correct, it just costs more. So the `degree + 1` width is the expected case and the performance
 * argument above rests on it; nothing here rests on it for correctness.
 *
 * @param start {number} : the window's first input index
 * @param weights {array} : plain numbers
 * @returns {map} : a banded row
 */
function trimBandedRow(start is number, weights is array) returns map
{
    var firstOffset = 0;
    while (firstOffset < size(weights) - 1 && weights[firstOffset] == 0)
    {
        firstOffset += 1;
    }
    var endOffset = size(weights);
    while (endOffset > firstOffset + 1 && weights[endOffset - 1] == 0)
    {
        endOffset -= 1;
    }
    if (firstOffset == 0 && endOffset == size(weights))
    {
        return { "start" : start, "weights" : weights };
    }
    return { "start" : start + firstOffset, "weights" : subArray(weights, firstOffset, endOffset) };
}

/**
 * `firstScale * firstRow + secondScale * secondRow` over banded coefficient rows — the Boehm blend,
 * and the innermost statement of the whole refinement layer.
 *
 * The union window is zero-filled rather than special-cased so that every position evaluates the
 * same two-term expression, which is what makes this bit-for-bit identical to the dense elementwise
 * version it replaces.
 */
function blendBandedRows(firstScale is number, firstRow is map, secondScale is number, secondRow is map) returns map
{
    const firstStart = firstRow.start;
    const secondStart = secondRow.start;
    const firstWeights = firstRow.weights;
    const secondWeights = secondRow.weights;
    const firstCount = size(firstWeights);
    const secondCount = size(secondWeights);

    const start = min(firstStart, secondStart);
    const width = max(firstStart + firstCount, secondStart + secondCount) - start;

    var blended = makeArray(width, 0);
    for (var offset = 0; offset < width; offset += 1)
    {
        const absoluteIndex = start + offset;
        const firstOffset = absoluteIndex - firstStart;
        const secondOffset = absoluteIndex - secondStart;
        const firstValue = (firstOffset >= 0 && firstOffset < firstCount) ? firstWeights[firstOffset] : 0;
        const secondValue = (secondOffset >= 0 && secondOffset < secondCount) ? secondWeights[secondOffset] : 0;
        blended[offset] = firstScale * firstValue + secondScale * secondValue;
    }
    return trimBandedRow(start, blended);
}

/**
 * Compact banded coefficient rows to the sparse { "index", "weight" } term lists the operator
 * representation uses, applying the same SPARSE_WEIGHT_CUTOFF the dense compactor applies — so a
 * row's surviving terms, and their order, are exactly what the dense path produced.
 */
function sparsifyBandedRows(bandedRows is array) returns array
{
    var sparseRows = makeArray(size(bandedRows), 0);
    for (var rowIndex = 0; rowIndex < size(bandedRows); rowIndex += 1)
    {
        const bandedRow = bandedRows[rowIndex];
        const weights = bandedRow.weights;
        var termCount = 0;
        for (var offset = 0; offset < size(weights); offset += 1)
        {
            if (abs(weights[offset]) > SPARSE_WEIGHT_CUTOFF)
            {
                termCount += 1;
            }
        }
        var terms = makeArray(termCount, 0);
        var termIndex = 0;
        for (var offset = 0; offset < size(weights); offset += 1)
        {
            if (abs(weights[offset]) > SPARSE_WEIGHT_CUTOFF)
            {
                terms[termIndex] = { "index" : bandedRow.start + offset, "weight" : weights[offset] };
                termIndex += 1;
            }
        }
        sparseRows[rowIndex] = terms;
    }
    return sparseRows;
}

/**
 * Core Boehm builder shared by the public refinementOperator constructors. Runs single knot insertion,
 * one parameter at a time, on unit-basis coefficient rows, so the accumulated coefficients ARE
 * the linear map from input control points to refined control points.
 *
 * The insertion formula (spec section 5.1 of the tiling spec, NURBS Book 5.2), with k the span
 * of ubar and D the degree:
 *     Q[i] = P[i]                                        for i <= k - D
 *     Q[i] = alpha * P[i] + (1 - alpha) * P[i - 1]       for k - D + 1 <= i <= k,
 *              alpha = (ubar - U[i]) / (U[i + D] - U[i])
 *     Q[i] = P[i - 1]                                    for i >= k + 1
 * The i >= k + 1 branch is a PLAIN COPY. Computing a second blend there is the exact bug this
 * module exists to retire (see spec section 2.1); the tester's counterexample vector guards it.
 *
 * @param maximumFinalMultiplicity : `degree` for refinement (shape-safe),
 *      `degree + 1` for clamped extraction (deliberate slicing).
 * @returns {map} : { "rows" : BANDED coefficient rows (see the banded-row block above),
 *      "knots" : refined knot vector }
 */
function buildRefinementCoefficients(knots is array, degree is number, parametersToInsert is array, maximumFinalMultiplicity is number) returns map
{
    const inputCount = size(knots) - degree - 1;
    if (inputCount < degree + 1)
    {
        throw "splineRefinementUtils: " ~ inputCount ~ " control points is too few for degree " ~ degree ~
            " (need at least " ~ (degree + 1) ~ ").";
    }

    // Identity to start: row i is the single unit weight at input i. One entry, not a row of
    // inputCount entries of which one is 1 — the identity alone used to be an inputCount^2 build,
    // paid in full even when nothing was inserted.
    var coefficientRows = makeArray(inputCount, 0);
    for (var inputIndex = 0; inputIndex < inputCount; inputIndex += 1)
    {
        coefficientRows[inputIndex] = { "start" : inputIndex, "weights" : [1] };
    }

    // Ascending insertion order keeps span searches and multiplicity counts simple.
    const sortedParameters = sort(parametersToInsert, function(a, b) { return a - b; });

    var currentKnots = knots;
    for (var parameterToInsert in sortedParameters)
    {
        const existingMultiplicity = knotMultiplicity(currentKnots, parameterToInsert);
        if (existingMultiplicity + 1 > maximumFinalMultiplicity)
        {
            throw "splineRefinementUtils: inserting " ~ parameterToInsert ~ " would raise its multiplicity to " ~
                (existingMultiplicity + 1) ~ ", above the allowed " ~ maximumFinalMultiplicity ~
                " for degree " ~ degree ~ ".";
        }

        const spanIndex = findKnotSpanIndex(currentKnots, degree, parameterToInsert);
        const currentCount = size(coefficientRows);

        var refinedRows = makeArray(currentCount + 1, 0);
        for (var outputIndex = 0; outputIndex <= currentCount; outputIndex += 1)
        {
            if (outputIndex <= spanIndex - degree)
            {
                refinedRows[outputIndex] = coefficientRows[outputIndex];
            }
            else if (outputIndex >= spanIndex + 1)
            {
                refinedRows[outputIndex] = coefficientRows[outputIndex - 1]; // plain copy — never a blend
            }
            else
            {
                const denominator = currentKnots[outputIndex + degree] - currentKnots[outputIndex];
                const alpha = denominator == 0 ? 0 : (parameterToInsert - currentKnots[outputIndex]) / denominator;
                refinedRows[outputIndex] = blendBandedRows(alpha, coefficientRows[outputIndex], 1 - alpha, coefficientRows[outputIndex - 1]);
            }
        }
        coefficientRows = refinedRows;

        // Insert the knot after its span start, preallocated.
        const oldKnotCount = size(currentKnots);
        var refinedKnots = makeArray(oldKnotCount + 1, 0);
        for (var knotIndex = 0; knotIndex <= spanIndex; knotIndex += 1)
        {
            refinedKnots[knotIndex] = currentKnots[knotIndex];
        }
        refinedKnots[spanIndex + 1] = parameterToInsert;
        for (var knotIndex = spanIndex + 1; knotIndex < oldKnotCount; knotIndex += 1)
        {
            refinedKnots[knotIndex + 1] = currentKnots[knotIndex];
        }
        currentKnots = refinedKnots;
    }

    return { "rows" : coefficientRows, "knots" : currentKnots };
}

/**
 * Compact dense coefficient rows to the sparse { "index", "weight" } term lists used in the
 * refinementOperator representation. Most rows of a clamped extraction are a single identity term, so
 * applying the refinementOperator is mostly pure copying.
 */
function sparsifyCoefficientRows(denseRows is array) returns array
{
    var sparseRows = makeArray(size(denseRows), 0);
    for (var rowIndex = 0; rowIndex < size(denseRows); rowIndex += 1)
    {
        const denseRow = denseRows[rowIndex];
        var termCount = 0;
        for (var entryIndex = 0; entryIndex < size(denseRow); entryIndex += 1)
        {
            if (abs(denseRow[entryIndex]) > SPARSE_WEIGHT_CUTOFF)
            {
                termCount += 1;
            }
        }
        var terms = makeArray(termCount, 0);
        var termIndex = 0;
        for (var entryIndex = 0; entryIndex < size(denseRow); entryIndex += 1)
        {
            if (abs(denseRow[entryIndex]) > SPARSE_WEIGHT_CUTOFF)
            {
                terms[termIndex] = { "index" : entryIndex, "weight" : denseRow[entryIndex] };
                termIndex += 1;
            }
        }
        sparseRows[rowIndex] = terms;
    }
    return sparseRows;
}

/**
 * Build the refinement refinementOperator that inserts `parametersToInsert` into `knots` for a spline of
 * the given degree. The refinementOperator is a fixed sparse linear map (see the Layer 2 block comment);
 * apply it with applyKnotRefinementOperator and its tensor variants.
 *
 * Refinement is shape-safe: any insertion that would raise a knot's multiplicity above
 * `degree` throws. Use clampedSegmentOperator when you actually want to slice.
 *
 * @param knots {array} : knot vector, size = inputCount + degree + 1
 * @param degree {number}
 * @param parametersToInsert {array} : plain numbers inside the knot domain; any order;
 *      repeats allowed up to the multiplicity limit
 * @returns {map} : the refinementOperator (see Layer 2 block comment)
 */
export function knotRefinementOperator(knots is array, degree is number, parametersToInsert is array) returns map
{
    return knotRefinementOperator(knots, degree, parametersToInsert, KnotInsertionAlgorithm.BOEHM);
}

/**
 * Algorithm-selecting overload of knotRefinementOperator. Only BOEHM exists today; the
 * parameter is the seam a future Oslo builder plugs into without touching callers.
 */
export function knotRefinementOperator(knots is array, degree is number, parametersToInsert is array, algorithm is KnotInsertionAlgorithm) returns map
{
    if (algorithm != KnotInsertionAlgorithm.BOEHM)
    {
        throw "splineRefinementUtils: unsupported insertion algorithm " ~ algorithm;
    }
    const built = buildRefinementCoefficients(knots, degree, parametersToInsert, degree);
    return {
            "degree" : degree,
            "inputCount" : size(knots) - degree - 1,
            "outputCount" : size(built.rows),
            "knots" : built.knots,
            "rows" : sparsifyBandedRows(built.rows)
        };
}

/**
 * Apply a refinement refinementOperator to one array of control points. Works on anything that supports
 * scalar multiplication and addition: 3D length Vectors, homogeneous weighted points, unitless
 * parameter-space points, or plain numbers (e.g. a weight array — apply the SAME refinementOperator to
 * points and weights of a rational spline, in homogeneous form).
 *
 * Identity rows (single term, weight exactly 1) copy straight through with no arithmetic.
 *
 * @param refinementOperator {map} : from knotRefinementOperator / clampedSegmentOperator / uniformPeriodExtractionOperator
 * @param controlPoints {array} : size must equal refinementOperator.inputCount
 * @returns {array} : refined control points, size refinementOperator.outputCount
 */
export function applyKnotRefinementOperator(refinementOperator is map, controlPoints is array) returns array
{
    if (size(controlPoints) != refinementOperator.inputCount)
    {
        throw "splineRefinementUtils: refinementOperator expects " ~ refinementOperator.inputCount ~ " control points, got " ~ size(controlPoints) ~ ".";
    }
    var refined = makeArray(refinementOperator.outputCount, controlPoints[0]);
    for (var outputIndex = 0; outputIndex < refinementOperator.outputCount; outputIndex += 1)
    {
        const terms = refinementOperator.rows[outputIndex];
        if (size(terms) == 1 && terms[0].weight == 1)
        {
            refined[outputIndex] = controlPoints[terms[0].index]; // identity — pure copy
        }
        else
        {
            var accumulated = terms[0].weight * controlPoints[terms[0].index];
            for (var termIndex = 1; termIndex < size(terms); termIndex += 1)
            {
                accumulated = accumulated + terms[termIndex].weight * controlPoints[terms[termIndex].index];
            }
            refined[outputIndex] = accumulated;
        }
    }
    return refined;
}

/**
 * Apply an refinementOperator WITHIN each row of a grid (refines the second index / V direction).
 * grid[rowIndex][columnIndex]; refinementOperator.inputCount must equal the row length.
 */
export function applyKnotRefinementOperatorAcrossRows(refinementOperator is map, grid is array) returns array
{
    var refinedGrid = makeArray(size(grid), 0);
    for (var rowIndex = 0; rowIndex < size(grid); rowIndex += 1)
    {
        refinedGrid[rowIndex] = applyKnotRefinementOperator(refinementOperator, grid[rowIndex]);
    }
    return refinedGrid;
}

/**
 * Apply an refinementOperator WITHIN each column of a grid (refines the first index / U direction).
 * grid[rowIndex][columnIndex]; refinementOperator.inputCount must equal the row COUNT. Preallocates the
 * output grid and scatters each refined column back, matching the displacement-map tensor
 * pattern (refineTileSeed) this generalizes.
 */
export function applyKnotRefinementOperatorDownColumns(refinementOperator is map, grid is array) returns array
{
    const rowCount = size(grid);
    const columnCount = size(grid[0]);
    var refinedGrid = makeArray(refinementOperator.outputCount, 0);
    for (var outputRowIndex = 0; outputRowIndex < refinementOperator.outputCount; outputRowIndex += 1)
    {
        refinedGrid[outputRowIndex] = makeArray(columnCount, grid[0][0]); // placeholder values, overwritten below
    }
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        var columnVector = makeArray(rowCount, grid[0][0]);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            columnVector[rowIndex] = grid[rowIndex][columnIndex];
        }
        const refinedColumn = applyKnotRefinementOperator(refinementOperator, columnVector);
        for (var outputRowIndex = 0; outputRowIndex < refinementOperator.outputCount; outputRowIndex += 1)
        {
            refinedGrid[outputRowIndex][columnIndex] = refinedColumn[outputRowIndex];
        }
    }
    return refinedGrid;
}

/**
 * Operator that extracts the clamped sub-spline reproducing the input EXACTLY on
 * [startParameter, endParameter] (spec section 5.2 of the tiling spec): both parameters are
 * raised to multiplicity degree + 1 by insertion, then the piece between them is sliced out.
 * The returned refinementOperator's `knots` is the piece's own clamped knot vector and `outputCount` is
 * the piece's control point count.
 *
 * Validate any change against the section 5.3 test vector: degree 2, knots [0..8], extract
 * [3, 5] -> 4 control points, piece knots [3,3,3,4,5,5,5].
 *
 * @param startParameter {number} : an existing knot value or any interior parameter,
 *      startParameter < endParameter, both inside the knot domain
 */
export function clampedSegmentOperator(knots is array, degree is number, startParameter is number, endParameter is number) returns map
{
    // Insert each boundary up to multiplicity degree + 1.
    const startInsertions = degree + 1 - knotMultiplicity(knots, startParameter);
    const endInsertions = degree + 1 - knotMultiplicity(knots, endParameter);
    var parametersToInsert = makeArray(max(0, startInsertions) + max(0, endInsertions), startParameter);
    for (var insertionIndex = max(0, startInsertions); insertionIndex < size(parametersToInsert); insertionIndex += 1)
    {
        parametersToInsert[insertionIndex] = endParameter;
    }

    const built = buildRefinementCoefficients(knots, degree, parametersToInsert, degree + 1);
    const refinedKnots = built.knots;

    // First knot equal to startParameter, last knot equal to endParameter.
    var firstStartIndex = -1;
    var lastEndIndex = -1;
    for (var knotIndex = 0; knotIndex < size(refinedKnots); knotIndex += 1)
    {
        if (firstStartIndex == -1 && abs(refinedKnots[knotIndex] - startParameter) <= KNOT_PARAMETER_TOLERANCE)
        {
            firstStartIndex = knotIndex;
        }
        if (abs(refinedKnots[knotIndex] - endParameter) <= KNOT_PARAMETER_TOLERANCE)
        {
            lastEndIndex = knotIndex;
        }
    }
    if (firstStartIndex == -1 || lastEndIndex == -1)
    {
        throw "splineRefinementUtils: clampedSegmentOperator could not locate the boundary knots after insertion.";
    }

    const pieceControlPointCount = (lastEndIndex - firstStartIndex + 1) - (degree + 1);
    var pieceRows = makeArray(pieceControlPointCount, 0);
    for (var pieceIndex = 0; pieceIndex < pieceControlPointCount; pieceIndex += 1)
    {
        pieceRows[pieceIndex] = built.rows[firstStartIndex + pieceIndex];
    }

    return {
            "degree" : degree,
            "inputCount" : size(knots) - degree - 1,
            "outputCount" : pieceControlPointCount,
            "knots" : subArray(refinedKnots, firstStartIndex, lastEndIndex + 1),
            "rows" : sparsifyBandedRows(pieceRows)
        };
}

/**
 * The integer-uniform special case displacementMap.fs uses to pull one tile of a conceptually
 * infinite uniform surface: `spanCount + 2 * degree + 1` input control points on uniform
 * integer knots 0, 1, 2, ..., extract the clamped piece covering one period of `spanCount`
 * spans starting at parameter `degree`.
 *
 * Must match displacementMap.fs extractionWeights(degree, n) term for term (spec vector 6)
 * before that feature migrates onto this module.
 */
export function uniformPeriodExtractionOperator(degree is number, spanCount is number) returns map
{
    const inputCount = spanCount + 2 * degree + 1;
    var uniformKnots = makeArray(inputCount + degree + 1, 0);
    for (var knotIndex = 0; knotIndex < size(uniformKnots); knotIndex += 1)
    {
        uniformKnots[knotIndex] = knotIndex;
    }
    return clampedSegmentOperator(uniformKnots, degree, degree, spanCount + degree);
}

// ============================================================================================
// Layer 3 — direct insertion (implemented)
// ============================================================================================

/**
 * Single Boehm knot insertion applied directly to control points — the one-off path when
 * building an refinementOperator is not worth it. Same formula as the refinementOperator core; kept as an
 * INDEPENDENT implementation on purpose, so the tester can check the two against each other
 * (vector 5).
 *
 * Rational splines: convert to homogeneous form first (combinePointsAndWeights from
 * nurbsUtils.fs), insert, then separatePointsAndWeights — weights ride along as the fourth
 * homogeneous coordinate, exactly like any other control point component.
 *
 * @param controlPoints {array} : anything supporting scalar multiply and add
 * @param knots {array} : size must be size(controlPoints) + degree + 1
 * @param parameter {number} : inside the knot domain; resulting multiplicity must not
 *      exceed `degree`
 * @returns {map} : { "controlPoints" : array (one longer), "knots" : array (one longer) }
 */
export function insertKnotOnce(controlPoints is array, knots is array, degree is number, parameter is number) returns map
{
    const controlPointCount = size(controlPoints);
    if (size(knots) != controlPointCount + degree + 1)
    {
        throw "splineRefinementUtils: knot vector has " ~ size(knots) ~ " entries; expected " ~
            (controlPointCount + degree + 1) ~ " for " ~ controlPointCount ~ " control points of degree " ~ degree ~ ".";
    }
    // Resulting multiplicity may reach `degree` (Bezier decomposition depends on that) but
    // never exceed it — `degree + 1` splits the spline. Use clampedSegmentOperator to slice.
    if (knotMultiplicity(knots, parameter) + 1 > degree)
    {
        throw "splineRefinementUtils: inserting " ~ parameter ~ " would raise its multiplicity above degree " ~ degree ~
            " - refinement rejects that; use clampedSegmentOperator to slice instead.";
    }

    const spanIndex = findKnotSpanIndex(knots, degree, parameter);

    var refinedPoints = makeArray(controlPointCount + 1, controlPoints[0]);
    for (var outputIndex = 0; outputIndex <= controlPointCount; outputIndex += 1)
    {
        if (outputIndex <= spanIndex - degree)
        {
            refinedPoints[outputIndex] = controlPoints[outputIndex];
        }
        else if (outputIndex >= spanIndex + 1)
        {
            // Plain copy of the shifted predecessor. This branch being a blend instead of a
            // copy is the tweenSurfaces bug (spec section 2.1) — leave it a copy.
            refinedPoints[outputIndex] = controlPoints[outputIndex - 1];
        }
        else
        {
            const denominator = knots[outputIndex + degree] - knots[outputIndex];
            const alpha = denominator == 0 ? 0 : (parameter - knots[outputIndex]) / denominator;
            refinedPoints[outputIndex] = alpha * controlPoints[outputIndex] + (1 - alpha) * controlPoints[outputIndex - 1];
        }
    }

    var refinedKnots = makeArray(size(knots) + 1, 0);
    for (var knotIndex = 0; knotIndex <= spanIndex; knotIndex += 1)
    {
        refinedKnots[knotIndex] = knots[knotIndex];
    }
    refinedKnots[spanIndex + 1] = parameter;
    for (var knotIndex = spanIndex + 1; knotIndex < size(knots); knotIndex += 1)
    {
        refinedKnots[knotIndex + 1] = knots[knotIndex];
    }

    return { "controlPoints" : refinedPoints, "knots" : refinedKnots };
}

/**
 * Insert a batch of parameters by repeated insertKnotOnce, ascending. Exact, but O(batch size)
 * passes over the control points — for large batches, or when the same refinement applies to
 * many rows/columns, build the refinementOperator once instead (knotRefinementOperator).
 *
 * @returns {map} : { "controlPoints", "knots" }
 */
export function refineKnotVector(controlPoints is array, knots is array, degree is number, parametersToInsert is array) returns map
{
    const sortedParameters = sort(parametersToInsert, function(a, b) { return a - b; });
    var current = { "controlPoints" : controlPoints, "knots" : knots };
    for (var parameterToInsert in sortedParameters)
    {
        current = insertKnotOnce(current.controlPoints, current.knots, degree, parameterToInsert);
    }
    return current;
}

// ============================================================================================
// Evaluation (pure de Boor — no Context, no geometry creation)
// ============================================================================================
//
// The standard library has NO evaluator for a B-spline DEFINITION held in memory:
// evaluateSpline (splineUtils.fs) is BSplineCurve-only, and the face evaluators
// (evFaceTangentPlanes / evFaceCurvatures) require created geometry and re-normalize their
// parameters to the face's parameter-space bounding box. These functions fill that one gap —
// they exist per the AGENTS.md manual-math rule because no std function does the job.
//
// CURVES: do NOT reach for std evaluateSpline when the curve is (or may be) RATIONAL. The
// kernel builtin behind it SILENTLY IGNORES WEIGHTS — proven live 2026-08-08, when a rational
// circle evaluated to its unweighted control polygon's curve exactly (off-circle by ~30% of
// the radius) and cost two tester runs to a false mismatch. For rational curves, evaluate
// through bSplineBasisValues / findEvaluationSpanIndex with homogeneous accumulation (weights
// times basis, divide at the end) — tweenCurves' evaluateNormalizedSplinePoint and the
// tester's evaluateModuleBasisCurvePoint are the reference implementations. std evaluateSpline
// remains fine for genuinely non-rational curves.

/**
 * The `degree + 1` non-vanishing B-spline basis function values at `parameter`, for the span
 * returned by findEvaluationSpanIndex. Standard Cox-de Boor triangular scheme (The NURBS Book,
 * algorithm A2.2).
 *
 * @returns {array} : values[r] = N_(spanIndex - degree + r), r = 0..degree; non-negative and
 *      summing to 1 (the tester asserts partition of unity)
 */
export function bSplineBasisValues(knots is array, degree is number, spanIndex is number, parameter is number) returns array
{
    var basisValues = makeArray(degree + 1, 0);
    var leftDistances = makeArray(degree + 1, 0);
    var rightDistances = makeArray(degree + 1, 0);
    basisValues[0] = 1;
    for (var level = 1; level <= degree; level += 1)
    {
        leftDistances[level] = parameter - knots[spanIndex + 1 - level];
        rightDistances[level] = knots[spanIndex + level] - parameter;
        var saved = 0;
        for (var functionIndex = 0; functionIndex < level; functionIndex += 1)
        {
            const shared = basisValues[functionIndex] / (rightDistances[functionIndex + 1] + leftDistances[level - functionIndex]);
            basisValues[functionIndex] = saved + rightDistances[functionIndex + 1] * shared;
            saved = leftDistances[level - functionIndex] * shared;
        }
        basisValues[level] = saved;
    }
    return basisValues;
}

/**
 * Span index for EVALUATION: like findKnotSpanIndex, but a parameter at (or within tolerance
 * above) the domain end returns the last non-degenerate span instead of throwing, so clamped
 * splines can be evaluated at their endpoints.
 */
export function findEvaluationSpanIndex(knots is array, degree is number, parameter is number) returns number
{
    for (var spanIndex = size(knots) - degree - 2; spanIndex >= degree; spanIndex -= 1)
    {
        if (knots[spanIndex] <= parameter && knots[spanIndex] < knots[spanIndex + 1])
        {
            return spanIndex;
        }
    }
    return degree; // parameter at or below the domain start
}

/**
 * Evaluate a B-spline surface DEFINITION at raw knot-domain parameters (u, v). Pure
 * arithmetic; rational-aware (homogeneous accumulation when `isRational` with `weights`).
 *
 * Expects the definition shape produced by evSurfaceDefinition / evApproximateBSplineSurface /
 * bSplineSurface: controlPoints[uIndex][vIndex] with uKnots sized to the row count.
 *
 * PERIODIC directions are fine, WITHIN THEIR OWN DOMAIN. A stored periodic direction
 * (n + degree control points against n + 2*degree + 1 knots) is already a complete, ordinary
 * B-spline representation across [knots[degree], knots[n + degree]] — the padding supplies every
 * knot de Boor needs there — so no periodic-aware arithmetic is required and none is done. What
 * this function does NOT do is wrap: a parameter outside that domain is not folded back into it,
 * so keep parameters in-domain for periodic input. (This used to throw on periodic surfaces
 * outright, which made sense only while normalizeSurfaceDefinition clamped every periodic
 * direction; now that periodicity is preserved, that guard would reject exactly the surfaces the
 * module is built to handle.)
 *
 * @param surface {map} : { uDegree, vDegree, controlPoints, uKnots, vKnots,
 *      isRational (optional), weights (required when rational) }
 * @param uParameter {number} : raw value in the uKnots domain
 * @param vParameter {number} : raw value in the vKnots domain
 * @returns {Vector} : the 3D point (with the control points' length units)
 */
export function evaluateBSplineSurfacePoint(surface is map, uParameter is number, vParameter is number) returns Vector
{

    const uSpanIndex = findEvaluationSpanIndex(surface.uKnots, surface.uDegree, uParameter);
    const vSpanIndex = findEvaluationSpanIndex(surface.vKnots, surface.vDegree, vParameter);
    const uBasisValues = bSplineBasisValues(surface.uKnots, surface.uDegree, uSpanIndex, uParameter);
    const vBasisValues = bSplineBasisValues(surface.vKnots, surface.vDegree, vSpanIndex, vParameter);
    const firstURowIndex = uSpanIndex - surface.uDegree;
    const firstVColumnIndex = vSpanIndex - surface.vDegree;
    const isRational = surface.isRational == true && surface.weights != undefined;

    var weightedPointSum = undefined; // accumulate from the first term so length units are preserved
    var weightSum = 0;
    for (var uBasisIndex = 0; uBasisIndex <= surface.uDegree; uBasisIndex += 1)
    {
        for (var vBasisIndex = 0; vBasisIndex <= surface.vDegree; vBasisIndex += 1)
        {
            const rowIndex = firstURowIndex + uBasisIndex;
            const columnIndex = firstVColumnIndex + vBasisIndex;
            var blendValue = uBasisValues[uBasisIndex] * vBasisValues[vBasisIndex];
            if (isRational)
            {
                blendValue = blendValue * surface.weights[rowIndex][columnIndex];
                weightSum += blendValue;
            }
            const contribution = blendValue * surface.controlPoints[rowIndex][columnIndex];
            weightedPointSum = weightedPointSum == undefined ? contribution : weightedPointSum + contribution;
        }
    }
    return isRational ? weightedPointSum / weightSum : weightedPointSum;
}

// ============================================================================================
// Layer 3 — EXACT DERIVATIVE EVALUATION (The NURBS Book ch. 3-4).
//
// Why this exists, since it is the module's first block that is not about refinement: the
// deformation feature (spec section 9.1) needs three things no amount of point evaluation
// provides — point-to-parameter INVERSION (Newton on the distance function, section 6.1 of the
// book, which needs first AND second derivatives), exact surface NORMALS for the offset step of
// flow-along-surface, and CURVATURE for section 9.1.1's refinement seeding, which sizes the
// first refinement level from the target's minimum curvature radius. Every one of those is a
// derivative question.
//
// The alternative was the kernel: evDistance(point, face) does closest-point projection and
// returns a face parameter. Two problems, both disqualifying for a per-control-point inner loop.
// It is one kernel call per point, so a refined 40x40 net costs 1600 calls per refinement level.
// And its parameter is in evFaceTangentPlane's form, NORMALIZED TO THE FACE'S PARAMETER-SPACE
// BOUNDING BOX rather than expressed in knot values — feeding a definition-side knot parameter
// to it, or reading its result as one, is a silent mismatch (the same trap recorded against
// evFaceTangentPlanes elsewhere). Definition-side derivatives have neither problem: pure
// arithmetic, no Context, exact, and in the definition's own parameterization by construction.
//
// RATIONAL SURFACES ARE NOT THE HOMOGENEOUS NUMERATOR'S DERIVATIVE. S = A/w, so every order
// needs the quotient rule (Algorithm A4.4), and the mixed partials bring in every lower-order
// derivative of both A and w. Skipping that is the exact shape of the evaluateSpline
// weights-ignoring bug (section 2.3.3): it agrees perfectly on non-rational input and is
// silently wrong on every revolve-derived surface in existence. The tester anchors this on a
// property no weights-dropping implementation can fake — a circle's tangent is perpendicular to
// its radius, checked to zero.
// ============================================================================================

/**
 * Derivatives of the (degree + 1) nonvanishing basis functions at `parameter`, orders 0 through
 * maxOrder: `result[order][functionIndex]`, where functionIndex 0..degree corresponds to control
 * point (spanIndex - degree + functionIndex). Row 0 is exactly bSplineBasisValues' output.
 *
 * NURBS Book Algorithm A2.3 (DersBasisFuns). The insight worth stating, because the code reads
 * as dense index arithmetic otherwise: the triangular recurrence bSplineBasisValues already runs
 * computes every knot difference it needs along the way and then throws them away. A2.3 keeps
 * them — basis functions in `ndu`'s upper triangle, knot differences in its lower triangle — and
 * the derivative loop is then a second recurrence over that saved table, with no re-evaluation
 * of anything.
 *
 * Orders above `degree` are returned as zeros rather than computed, which is not a shortcut: a
 * degree-p piecewise polynomial's (p+1)-th derivative IS identically zero.
 */
function bSplineBasisDerivatives(knots is array, degree is number, spanIndex is number, parameter is number, maxOrder is number) returns array
{
    var ndu = makeArray(degree + 1, 0);
    for (var rowIndex = 0; rowIndex <= degree; rowIndex += 1)
    {
        ndu[rowIndex] = makeArray(degree + 1, 0);
    }
    var leftDistances = makeArray(degree + 1, 0);
    var rightDistances = makeArray(degree + 1, 0);
    ndu[0][0] = 1;
    for (var level = 1; level <= degree; level += 1)
    {
        leftDistances[level] = parameter - knots[spanIndex + 1 - level];
        rightDistances[level] = knots[spanIndex + level] - parameter;
        var saved = 0;
        for (var functionIndex = 0; functionIndex < level; functionIndex += 1)
        {
            // Lower triangle: the knot difference this step divides by. Upper triangle: the
            // basis function itself. bSplineBasisValues computes the same two quantities but
            // keeps only the second.
            ndu[level][functionIndex] = rightDistances[functionIndex + 1] + leftDistances[level - functionIndex];
            const shared = ndu[functionIndex][level - 1] / ndu[level][functionIndex];
            ndu[functionIndex][level] = saved + rightDistances[functionIndex + 1] * shared;
            saved = leftDistances[level - functionIndex] * shared;
        }
        ndu[level][level] = saved;
    }

    var derivatives = makeArray(maxOrder + 1, 0);
    for (var order = 0; order <= maxOrder; order += 1)
    {
        derivatives[order] = makeArray(degree + 1, 0);
    }
    for (var functionIndex = 0; functionIndex <= degree; functionIndex += 1)
    {
        derivatives[0][functionIndex] = ndu[functionIndex][degree];
    }

    const effectiveMaxOrder = min(maxOrder, degree);
    for (var functionIndex = 0; functionIndex <= degree; functionIndex += 1)
    {
        // Two alternating rows of coefficients, rebuilt per functionIndex. The book ping-pongs
        // two rows of one array without clearing them between outer iterations; allocating fresh
        // zeroed rows here is equivalent wherever the book is correct (each order's reads are
        // covered by the previous order's writes) and cannot carry a stale value across.
        var previousCoefficients = makeArray(degree + 2, 0);
        var currentCoefficients = makeArray(degree + 2, 0);
        previousCoefficients[0] = 1;
        for (var order = 1; order <= effectiveMaxOrder; order += 1)
        {
            var accumulated = 0;
            const shiftedIndex = functionIndex - order;
            const reducedDegree = degree - order;
            if (functionIndex >= order)
            {
                currentCoefficients[0] = previousCoefficients[0] / ndu[reducedDegree + 1][shiftedIndex];
                accumulated = currentCoefficients[0] * ndu[shiftedIndex][reducedDegree];
            }
            const firstTerm = shiftedIndex >= -1 ? 1 : -shiftedIndex;
            const lastTerm = (functionIndex - 1 <= reducedDegree) ? order - 1 : degree - functionIndex;
            for (var termIndex = firstTerm; termIndex <= lastTerm; termIndex += 1)
            {
                currentCoefficients[termIndex] = (previousCoefficients[termIndex] - previousCoefficients[termIndex - 1]) /
                    ndu[reducedDegree + 1][shiftedIndex + termIndex];
                accumulated += currentCoefficients[termIndex] * ndu[shiftedIndex + termIndex][reducedDegree];
            }
            if (functionIndex <= reducedDegree)
            {
                currentCoefficients[order] = -previousCoefficients[order - 1] / ndu[reducedDegree + 1][functionIndex];
                accumulated += currentCoefficients[order] * ndu[functionIndex][reducedDegree];
            }
            derivatives[order][functionIndex] = accumulated;

            const swapRow = previousCoefficients;
            previousCoefficients = currentCoefficients;
            currentCoefficients = swapRow;
        }
    }

    // The recurrence above produces the derivatives up to a factorial-like factor
    // degree * (degree - 1) * ... * (degree - order + 1), applied here in one pass.
    var factor = degree;
    for (var order = 1; order <= effectiveMaxOrder; order += 1)
    {
        for (var functionIndex = 0; functionIndex <= degree; functionIndex += 1)
        {
            derivatives[order][functionIndex] = factor * derivatives[order][functionIndex];
        }
        factor = factor * (degree - order);
    }
    return derivatives;
}

/** Binomial coefficients C(n, k) for n, k <= maxN, by Pascal's triangle. Needed by the rational
    quotient rule, where the order-(k, l) derivative mixes every lower order weighted by C(k, i)
    and C(l, j). */
function binomialCoefficientTable(maxN is number) returns array
{
    var table = makeArray(maxN + 1, 0);
    for (var n = 0; n <= maxN; n += 1)
    {
        var row = makeArray(maxN + 2, 0);
        row[0] = 1;
        for (var k = 1; k <= n; k += 1)
        {
            row[k] = table[n - 1][k - 1] + table[n - 1][k];
        }
        table[n] = row;
    }
    return table;
}

/**
 * Every partial derivative of a B-spline surface up to `maxUOrder` in U and `maxVOrder` in V,
 * as `result[uOrder][vOrder]` — a Vector in the control points' own length units. Exact, rational
 * aware, no Context, no created geometry.
 *
 * result[0][0] equals evaluateBSplineSurfacePoint. result[1][0] and result[0][1] are the
 * isoparametric tangents; their cross product is the (unnormalized) normal.
 *
 * Two deliberate choices:
 *
 * The full RECTANGLE of orders is computed, not the book's total-order triangle (k + l <= d).
 * The rectangle is what the rational recursion reads from anyway, and asking for exactly the
 * five derivatives Newton point-inversion needs — S_u, S_v, S_uu, S_uv, S_vv — is then one call
 * with maxUOrder = maxVOrder = 2 rather than an order budget the caller has to reason about.
 *
 * The U sum is FACTORED OUT of the V sum (Algorithm A3.6's `temp` array) rather than written as
 * one direct double sum over the control net. At degree 3 and order 2 that is 84 multiply-adds
 * against 144, and this is the deformation feature's innermost loop — once per control point per
 * Newton iteration per refinement level. The same amortization argument as the operator layer,
 * one level down.
 */
export function evaluateBSplineSurfaceDerivatives(surface is map, uParameter is number, vParameter is number,
    maxUOrder is number, maxVOrder is number) returns array
{
    const uSpanIndex = findEvaluationSpanIndex(surface.uKnots, surface.uDegree, uParameter);
    const vSpanIndex = findEvaluationSpanIndex(surface.vKnots, surface.vDegree, vParameter);
    const uBasisDerivatives = bSplineBasisDerivatives(surface.uKnots, surface.uDegree, uSpanIndex, uParameter, maxUOrder);
    const vBasisDerivatives = bSplineBasisDerivatives(surface.vKnots, surface.vDegree, vSpanIndex, vParameter, maxVOrder);
    const firstURowIndex = uSpanIndex - surface.uDegree;
    const firstVColumnIndex = vSpanIndex - surface.vDegree;
    const isRational = surface.isRational == true && surface.weights != undefined;

    // A zero in the control points' own units — the module's alternative to accumulating from
    // the first term, which does not generalize to a grid of accumulators.
    const zeroVector = 0 * surface.controlPoints[0][0];

    var numeratorDerivatives = makeArray(maxUOrder + 1, 0);
    var weightDerivatives = makeArray(maxUOrder + 1, 0);
    for (var uOrder = 0; uOrder <= maxUOrder; uOrder += 1)
    {
        // Collapse the U direction once per uOrder: rowSums[s] is the weighted control point of
        // column s blended by this order's U basis derivatives. Independent of vOrder, which is
        // exactly why it hoists.
        var rowSums = makeArray(surface.vDegree + 1, zeroVector);
        var rowWeightSums = makeArray(surface.vDegree + 1, 0);
        for (var vBasisIndex = 0; vBasisIndex <= surface.vDegree; vBasisIndex += 1)
        {
            var pointSum = zeroVector;
            var weightSum = 0;
            for (var uBasisIndex = 0; uBasisIndex <= surface.uDegree; uBasisIndex += 1)
            {
                const rowIndex = firstURowIndex + uBasisIndex;
                const columnIndex = firstVColumnIndex + vBasisIndex;
                var blendValue = uBasisDerivatives[uOrder][uBasisIndex];
                if (isRational)
                {
                    blendValue = blendValue * surface.weights[rowIndex][columnIndex];
                    weightSum += blendValue;
                }
                pointSum = pointSum + blendValue * surface.controlPoints[rowIndex][columnIndex];
            }
            rowSums[vBasisIndex] = pointSum;
            rowWeightSums[vBasisIndex] = weightSum;
        }

        var numeratorRow = makeArray(maxVOrder + 1, zeroVector);
        var weightRow = makeArray(maxVOrder + 1, 0);
        for (var vOrder = 0; vOrder <= maxVOrder; vOrder += 1)
        {
            var combinedPointSum = zeroVector;
            var combinedWeightSum = 0;
            for (var vBasisIndex = 0; vBasisIndex <= surface.vDegree; vBasisIndex += 1)
            {
                combinedPointSum = combinedPointSum + vBasisDerivatives[vOrder][vBasisIndex] * rowSums[vBasisIndex];
                combinedWeightSum += vBasisDerivatives[vOrder][vBasisIndex] * rowWeightSums[vBasisIndex];
            }
            numeratorRow[vOrder] = combinedPointSum;
            weightRow[vOrder] = combinedWeightSum;
        }
        numeratorDerivatives[uOrder] = numeratorRow;
        weightDerivatives[uOrder] = weightRow;
    }

    if (!isRational)
    {
        return numeratorDerivatives;
    }

    // Algorithm A4.4 (RatSurfaceDerivs), rectangle form. Every term reads a strictly lower order
    // in U or in V, so the ascending double loop has each one already computed.
    const binomials = binomialCoefficientTable(max(maxUOrder, maxVOrder));
    var surfaceDerivatives = makeArray(maxUOrder + 1, 0);
    for (var uOrder = 0; uOrder <= maxUOrder; uOrder += 1)
    {
        surfaceDerivatives[uOrder] = makeArray(maxVOrder + 1, zeroVector);
    }
    for (var uOrder = 0; uOrder <= maxUOrder; uOrder += 1)
    {
        for (var vOrder = 0; vOrder <= maxVOrder; vOrder += 1)
        {
            var accumulated = numeratorDerivatives[uOrder][vOrder];
            for (var vTerm = 1; vTerm <= vOrder; vTerm += 1)
            {
                accumulated = accumulated -
                    binomials[vOrder][vTerm] * weightDerivatives[0][vTerm] * surfaceDerivatives[uOrder][vOrder - vTerm];
            }
            for (var uTerm = 1; uTerm <= uOrder; uTerm += 1)
            {
                accumulated = accumulated -
                    binomials[uOrder][uTerm] * weightDerivatives[uTerm][0] * surfaceDerivatives[uOrder - uTerm][vOrder];
                var mixedSum = zeroVector;
                for (var vTerm = 1; vTerm <= vOrder; vTerm += 1)
                {
                    mixedSum = mixedSum +
                        binomials[vOrder][vTerm] * weightDerivatives[uTerm][vTerm] * surfaceDerivatives[uOrder - uTerm][vOrder - vTerm];
                }
                accumulated = accumulated - binomials[uOrder][uTerm] * mixedSum;
            }
            surfaceDerivatives[uOrder][vOrder] = accumulated / weightDerivatives[0][0];
        }
    }
    return surfaceDerivatives;
}

/** One partial derivative of a surface. Convenience over evaluateBSplineSurfaceDerivatives; when
    more than one order is wanted, call that directly — it computes the whole rectangle for very
    little more than one corner of it. */
export function evaluateBSplineSurfaceDerivative(surface is map, uParameter is number, vParameter is number,
    uOrder is number, vOrder is number) returns Vector
{
    return evaluateBSplineSurfaceDerivatives(surface, uParameter, vParameter, uOrder, vOrder)[uOrder][vOrder];
}

/**
 * Throw when the two isoparametric tangents do not span a plane, i.e. the surface has no normal
 * at this parameter — a degenerate point such as a cone apex or a sphere pole, or a fully
 * degenerate isoparametric line.
 *
 * The test is on the SINE of the angle between the tangents (|Su x Sv| against |Su||Sv|), not on
 * the cross product's own magnitude, so it is scale free: a millimetre-scale patch and a
 * metre-scale one degenerate at the same geometric configuration rather than at the same number.
 */
function verifyNonDegenerateTangents(uTangent is Vector, vTangent is Vector, crossProduct is Vector,
    uParameter is number, vParameter is number)
{
    if (squaredNorm(crossProduct) <= 1e-20 * squaredNorm(uTangent) * squaredNorm(vTangent))
    {
        throw "splineRefinementUtils: the surface is degenerate at (" ~ uParameter ~ ", " ~ vParameter ~
            ") - its two isoparametric tangents are parallel or vanishing, so no normal or curvature exists " ~
            "there. This is a real property of the surface (a cone apex or sphere pole behaves this way), not " ~
            "a numerical failure; a caller sampling a whole surface should avoid its degenerate parameters " ~
            "rather than expect a value here.";
    }
}

/** Unit surface normal, normalize(Su x Sv). Throws at a degenerate point rather than returning a
    direction that is arbitrary — see verifyNonDegenerateTangents. */
export function evaluateBSplineSurfaceNormal(surface is map, uParameter is number, vParameter is number) returns Vector
{
    const derivatives = evaluateBSplineSurfaceDerivatives(surface, uParameter, vParameter, 1, 1);
    const uTangent = derivatives[1][0];
    const vTangent = derivatives[0][1];
    const crossProduct = cross(uTangent, vTangent);
    verifyNonDegenerateTangents(uTangent, vTangent, crossProduct, uParameter, vParameter);
    return normalize(crossProduct);
}

/**
 * Principal, Gaussian and mean curvature at a parameter, from the first and second fundamental
 * forms. What spec section 9.1.1's SEEDING consumes: it sizes the first refinement level from
 * the target's minimum curvature radius, since a span of arc length s deviates from the surface
 * by about s^2 / (8R).
 *
 * Returns { normal, principalCurvatures (two, ascending), gaussianCurvature, meanCurvature,
 * minimumRadius }. `minimumRadius` is `undefined` where both principal curvatures vanish — a
 * genuinely flat point has no finite radius, and reporting some large number instead would make
 * a plane look like a tight curve's opposite rather than like the special case it is. Callers
 * seeding a refinement level should read undefined as "curvature imposes no requirement here".
 */
export function evaluateBSplineSurfaceCurvature(surface is map, uParameter is number, vParameter is number) returns map
{
    const derivatives = evaluateBSplineSurfaceDerivatives(surface, uParameter, vParameter, 2, 2);
    const uTangent = derivatives[1][0];
    const vTangent = derivatives[0][1];
    const crossProduct = cross(uTangent, vTangent);
    verifyNonDegenerateTangents(uTangent, vTangent, crossProduct, uParameter, vParameter);
    const normal = normalize(crossProduct);

    // First fundamental form (lengths squared) and second (lengths, the normal being unitless).
    const formE = dot(uTangent, uTangent);
    const formF = dot(uTangent, vTangent);
    const formG = dot(vTangent, vTangent);
    const formL = dot(derivatives[2][0], normal);
    const formM = dot(derivatives[1][1], normal);
    const formN = dot(derivatives[0][2], normal);

    const discriminant = formE * formG - formF * formF; // == squaredNorm(crossProduct), by Lagrange
    const gaussianCurvature = (formL * formN - formM * formM) / discriminant;
    const meanCurvature = (formE * formN - 2 * formF * formM + formG * formL) / (2 * discriminant);

    // k = H +/- sqrt(H^2 - K). The radicand is >= 0 exactly (it is the squared half-difference of
    // the principal curvatures) and reaches 0 at an umbilic, where rounding can carry it just
    // below; the floor handles that arithmetic artifact and nothing else.
    const radicand = max(0 / meter / meter, meanCurvature * meanCurvature - gaussianCurvature);
    const spread = sqrt(radicand);
    const firstCurvature = meanCurvature - spread;
    const secondCurvature = meanCurvature + spread;

    // Stripped to a plain number purely so the flat case can be tested against a literal 0; a
    // truly planar surface gives it EXACTLY 0, since its second derivatives all lie in the
    // tangent plane and dot to zero against the normal.
    const largestMagnitude = max(abs(firstCurvature), abs(secondCurvature));
    const largestMagnitudePerMeter = largestMagnitude * meter;
    return {
            "normal" : normal,
            "principalCurvatures" : [firstCurvature, secondCurvature],
            "gaussianCurvature" : gaussianCurvature,
            "meanCurvature" : meanCurvature,
            "minimumRadius" : largestMagnitudePerMeter == 0 ? undefined : 1 / largestMagnitude
        };
}

/**
 * Curve form: every derivative of a B-spline curve up to `maxOrder`, as `result[order]`.
 * result[0] is the point. Same rational quotient rule as the surface (Algorithm A4.2), same
 * reason it cannot be skipped.
 *
 * Here for the same reason every other curve/surface sibling in this module is: spec section
 * 9.1's bend-along-curve map needs a moving frame, which is derivatives. Nothing calls it yet.
 */
export function evaluateBSplineCurveDerivatives(spline is map, parameter is number, maxOrder is number) returns array
{
    const spanIndex = findEvaluationSpanIndex(spline.knots, spline.degree, parameter);
    const basisDerivatives = bSplineBasisDerivatives(spline.knots, spline.degree, spanIndex, parameter, maxOrder);
    const firstControlPointIndex = spanIndex - spline.degree;
    const isRational = spline.isRational == true && spline.weights != undefined;
    const zeroVector = 0 * spline.controlPoints[0];

    var numeratorDerivatives = makeArray(maxOrder + 1, zeroVector);
    var weightDerivatives = makeArray(maxOrder + 1, 0);
    for (var order = 0; order <= maxOrder; order += 1)
    {
        var pointSum = zeroVector;
        var weightSum = 0;
        for (var basisIndex = 0; basisIndex <= spline.degree; basisIndex += 1)
        {
            const controlPointIndex = firstControlPointIndex + basisIndex;
            var blendValue = basisDerivatives[order][basisIndex];
            if (isRational)
            {
                blendValue = blendValue * spline.weights[controlPointIndex];
                weightSum += blendValue;
            }
            pointSum = pointSum + blendValue * spline.controlPoints[controlPointIndex];
        }
        numeratorDerivatives[order] = pointSum;
        weightDerivatives[order] = weightSum;
    }

    if (!isRational)
    {
        return numeratorDerivatives;
    }

    const binomials = binomialCoefficientTable(maxOrder);
    var curveDerivatives = makeArray(maxOrder + 1, zeroVector);
    for (var order = 0; order <= maxOrder; order += 1)
    {
        var accumulated = numeratorDerivatives[order];
        for (var term = 1; term <= order; term += 1)
        {
            accumulated = accumulated - binomials[order][term] * weightDerivatives[term] * curveDerivatives[order - term];
        }
        curveDerivatives[order] = accumulated / weightDerivatives[0];
    }
    return curveDerivatives;
}

// ============================================================================================
// Layer 3 — shared curve-level private helpers. Every one of these is pure arithmetic on
// knot values, indices, or homogeneous points; none of them touch a Context.
// ============================================================================================

/**
 * Distinct knot values strictly between the clamped ends of `knots`, each with its CURRENT
 * multiplicity, in ascending order. `knots` must be clamped (isClampedKnotArray) — every
 * caller of this module builds toward that via normalizeSplineDefinition, and kernel-returned
 * non-periodic curves/surfaces are always clamped in practice; this assertion exists so a
 * violation fails loudly here instead of producing silently wrong breakpoints downstream.
 */
function interiorKnotRun(knots is array, degree is number) returns array
{
    if (!isClampedKnotArray(knots, degree))
    {
        throw "splineRefinementUtils: interior knot analysis requires a clamped knot vector - got an unclamped, " ~
            "non-periodic knot vector. Kernel-returned non-periodic geometry should always be clamped; if this " ~
            "fires, the input needs its own clamping step before reaching this module.";
    }

    const firstInteriorIndex = degree + 1;
    const lastInteriorIndex = size(knots) - degree - 2; // inclusive; < firstInteriorIndex for a Bezier (no interior knots)

    var distinctCount = 0;
    var previousValue = knots[degree]; // domain start, sentinel
    for (var knotIndex = firstInteriorIndex; knotIndex <= lastInteriorIndex; knotIndex += 1)
    {
        if (abs(knots[knotIndex] - previousValue) > KNOT_PARAMETER_TOLERANCE)
        {
            distinctCount += 1;
            previousValue = knots[knotIndex];
        }
    }

    var runs = makeArray(distinctCount, 0);
    var runIndex = -1;
    for (var knotIndex = firstInteriorIndex; knotIndex <= lastInteriorIndex; knotIndex += 1)
    {
        if (runIndex == -1 || abs(knots[knotIndex] - runs[runIndex].value) > KNOT_PARAMETER_TOLERANCE)
        {
            runIndex += 1;
            runs[runIndex] = { "value" : knots[knotIndex], "multiplicity" : 1 };
        }
        else
        {
            runs[runIndex] = { "value" : runs[runIndex].value, "multiplicity" : runs[runIndex].multiplicity + 1 };
        }
    }
    return runs;
}

/**
 * The domain breakpoints (Bezier segment boundaries: domain start, each distinct interior
 * knot value, domain end) and the flat list of parameters that must be inserted to raise
 * every interior knot to multiplicity == degree, for a clamped spline. Shared by every
 * function that needs to decompose a spline into independent Bezier segments.
 */
function interiorRunsAndInsertionPlan(knots is array, degree is number) returns map
{
    const runs = interiorKnotRun(knots, degree);
    const domain = knotDomain(knots, degree);
    const numSegments = size(runs) + 1;

    var breakpoints = makeArray(numSegments + 1, domain.start);
    for (var runIndex = 0; runIndex < size(runs); runIndex += 1)
    {
        breakpoints[runIndex + 1] = runs[runIndex].value;
    }
    breakpoints[numSegments] = domain.end;

    var insertionCount = 0;
    for (var runIndex = 0; runIndex < size(runs); runIndex += 1)
    {
        insertionCount += degree - runs[runIndex].multiplicity;
    }
    var insertions = makeArray(insertionCount, 0);
    var writeIndex = 0;
    for (var runIndex = 0; runIndex < size(runs); runIndex += 1)
    {
        const neededInsertions = degree - runs[runIndex].multiplicity;
        for (var repeatIndex = 0; repeatIndex < neededInsertions; repeatIndex += 1)
        {
            insertions[writeIndex] = runs[runIndex].value;
            writeIndex += 1;
        }
    }
    return { "breakpoints" : breakpoints, "insertions" : insertions, "numSegments" : numSegments };
}

/**
 * Decompose `points` (any addable/scalable representation — homogeneous 4D points for
 * rational callers, plain length Vectors otherwise) into its independent Bezier segments via
 * knot refinement (spec section 4's note on decomposeIntoBezierSegments): one
 * knotRefinementOperator raises every interior knot to multiplicity == degree, then the result
 * slices cleanly into (numSegments) groups of (degree + 1) points, segment s occupying
 * [s * degree, s * degree + degree] of the refined array.
 */
function decomposeIntoSegmentsCore(points is array, knots is array, degree is number) returns map
{
    const plan = interiorRunsAndInsertionPlan(knots, degree);
    // Direct sequential insertion, NOT knotRefinementOperator. The operator earns its keep when the
    // SAME refinement is applied to many point arrays (every row and column of a surface grid);
    // built for one array it is work done and thrown away, and refineKnotVector produces the
    // identical result in O(insertions x M).
    //
    // The MARGIN here used to be much larger and is worth restating, since the old note is what a
    // reader may remember: buildRefinementCoefficients once carried a dense M-wide row per output
    // and cost an unconditional O(M^2) before a single point was touched. Banded rows removed that
    // term, so the operator is now within a constant factor of direct insertion even for one array.
    // The choice below stands on "no reuse, so do the simple thing", not on a complexity gap.
    const refinedPoints = refineKnotVector(points, knots, degree, plan.insertions).controlPoints;

    var segmentPointArrays = makeArray(plan.numSegments, 0);
    for (var segmentIndex = 0; segmentIndex < plan.numSegments; segmentIndex += 1)
    {
        segmentPointArrays[segmentIndex] = subArray(refinedPoints, segmentIndex * degree, segmentIndex * degree + degree + 1);
    }
    return { "segmentPointArrays" : segmentPointArrays, "breakpoints" : plan.breakpoints, "degree" : degree };
}

/**
 * Elevate homogeneous points from `degree` to `targetDegree` WITHOUT the final removeKnots
 * simplification step std's own elevateBSpline applies. Deliberately: removeKnots decides
 * removability by checking whether the ACTUAL point values happen to allow it
 * (weightedPointsTolerantEquals in nurbsUtils.fs), so two curves with the same knots but
 * different points can simplify to DIFFERENT final knot vectors. That is fine for a single
 * curve (elevateSplineDegree does simplify) but unacceptable across the columns/rows of one
 * surface, which must all land on the SAME shared knot vector - so elevateSurfaceDegrees calls
 * this raw form on every column/row and skips simplification, at the cost of an unminimized
 * (but always mathematically valid) shared knot vector.
 */
function elevateHomogeneousPointsRaw(points is array, knots is array, degree is number, targetDegree is number) returns map
{
    const core = decomposeIntoSegmentsCore(points, knots, degree);
    const numSegments = size(core.segmentPointArrays);

    var elevatedSegments = makeArray(numSegments, 0);
    for (var segmentIndex = 0; segmentIndex < numSegments; segmentIndex += 1)
    {
        elevatedSegments[segmentIndex] = elevateBezierDegree(core.segmentPointArrays[segmentIndex], targetDegree);
    }

    const elevatedPointCount = numSegments * targetDegree + 1;
    var elevatedPoints = makeArray(elevatedPointCount, elevatedSegments[0][0]);
    for (var segmentIndex = 0; segmentIndex < numSegments; segmentIndex += 1)
    {
        for (var pointIndex = 0; pointIndex <= targetDegree; pointIndex += 1)
        {
            elevatedPoints[segmentIndex * targetDegree + pointIndex] = elevatedSegments[segmentIndex][pointIndex];
        }
    }

    const elevatedKnotCount = (numSegments + 1) * targetDegree + 2;
    var elevatedKnots = makeArray(elevatedKnotCount, core.breakpoints[0]);
    var writeIndex = targetDegree + 1;
    for (var segmentIndex = 1; segmentIndex < numSegments; segmentIndex += 1)
    {
        for (var repeatIndex = 0; repeatIndex < targetDegree; repeatIndex += 1)
        {
            elevatedKnots[writeIndex] = core.breakpoints[segmentIndex];
            writeIndex += 1;
        }
    }
    for (var knotIndex = writeIndex; knotIndex < elevatedKnotCount; knotIndex += 1)
    {
        elevatedKnots[knotIndex] = core.breakpoints[numSegments];
    }

    return { "points" : elevatedPoints, "knots" : elevatedKnots };
}

/**
 * Choose `numToInsert` new parameters via repeated widest-span-midpoint selection (ties ->
 * smallest index), never a blind uniform distribution across the domain — a uniform
 * distribution collides with existing knots whenever the input is itself uniformly
 * parameterized, silently over-raising multiplicity (spec section 2.2, the bug this retires).
 * A midpoint of a nonzero-width span can never equal an existing knot.
 */
function widestSpanMidpointInsertions(knots is array, numToInsert is number) returns array
{
    var insertions = makeArray(numToInsert, 0);
    var workingKnots = knots;
    for (var insertIndex = 0; insertIndex < numToInsert; insertIndex += 1)
    {
        var widestIndex = 0;
        var widestWidth = workingKnots[1] - workingKnots[0];
        for (var spanIndex = 1; spanIndex < size(workingKnots) - 1; spanIndex += 1)
        {
            const width = workingKnots[spanIndex + 1] - workingKnots[spanIndex];
            if (width > widestWidth)
            {
                widestWidth = width;
                widestIndex = spanIndex;
            }
        }
        const midpoint = (workingKnots[widestIndex] + workingKnots[widestIndex + 1]) / 2;
        insertions[insertIndex] = midpoint;

        var updatedKnots = makeArray(size(workingKnots) + 1, 0);
        for (var knotIndex = 0; knotIndex <= widestIndex; knotIndex += 1)
        {
            updatedKnots[knotIndex] = workingKnots[knotIndex];
        }
        updatedKnots[widestIndex + 1] = midpoint;
        for (var knotIndex = widestIndex + 1; knotIndex < size(workingKnots); knotIndex += 1)
        {
            updatedKnots[knotIndex + 1] = workingKnots[knotIndex];
        }
        workingKnots = updatedKnots;
    }
    return insertions;
}

/**
 * Affine rescale of knot VALUES ONLY (control points untouched) so the domain becomes exactly
 * [0, 1]. Used before mergeKnotVectors, which requires both inputs to already share a domain.
 */
function remapKnotsToUnitDomain(knots is array, degree is number) returns array
{
    const domain = knotDomain(knots, degree);
    const domainSpan = domain.end - domain.start;
    var remapped = makeArray(size(knots), 0);
    for (var knotIndex = 0; knotIndex < size(knots); knotIndex += 1)
    {
        remapped[knotIndex] = (knots[knotIndex] - domain.start) / domainSpan;
    }
    return remapped;
}

/**
 * Given `knots` and a `mergedKnots` built as mergeKnotVectors(knots, somethingElse, degree)
 * (so every distinct value of `knots` also appears in `mergedKnots` with >= multiplicity),
 * return the flat list of parameters that must be inserted into `knots` to reach
 * `mergedKnots` exactly. Single lockstep walk over both (both sorted ascending by
 * construction), since every value in `knots` appears in `mergedKnots` in the same order.
 */
export function insertionsToReach(knots is array, mergedKnots is array, degree is number) returns array
{
    const runs = interiorKnotRun(knots, degree);
    const mergedRuns = interiorKnotRun(mergedKnots, degree);

    var insertionCount = 0;
    var runIndex = 0;
    for (var mergedIndex = 0; mergedIndex < size(mergedRuns); mergedIndex += 1)
    {
        var existingMultiplicity = 0;
        if (runIndex < size(runs) && abs(runs[runIndex].value - mergedRuns[mergedIndex].value) <= KNOT_PARAMETER_TOLERANCE)
        {
            existingMultiplicity = runs[runIndex].multiplicity;
            runIndex += 1;
        }
        insertionCount += mergedRuns[mergedIndex].multiplicity - existingMultiplicity;
    }

    var insertions = makeArray(insertionCount, 0);
    var writeIndex = 0;
    runIndex = 0;
    for (var mergedIndex = 0; mergedIndex < size(mergedRuns); mergedIndex += 1)
    {
        var existingMultiplicity = 0;
        if (runIndex < size(runs) && abs(runs[runIndex].value - mergedRuns[mergedIndex].value) <= KNOT_PARAMETER_TOLERANCE)
        {
            existingMultiplicity = runs[runIndex].multiplicity;
            runIndex += 1;
        }
        const neededInsertions = mergedRuns[mergedIndex].multiplicity - existingMultiplicity;
        for (var repeatIndex = 0; repeatIndex < neededInsertions; repeatIndex += 1)
        {
            insertions[writeIndex] = mergedRuns[mergedIndex].value;
            writeIndex += 1;
        }
    }
    return insertions;
}

// ============================================================================================
// Layer 3 — genuine periodic-preserving refinement.
//
// A periodic direction is STORED as `n` unique control points plus `degree` literal copies of
// the first `degree` (the OVERLAP CONDITION: P[i] = P[i mod n] for all valid i), paired with
// knots satisfying knots[i + n] = knots[i] + PERIOD. That overlap is a constraint on control
// point VALUES, not just knot count — it is what tells the evaluator "wrap here." Clamped
// extraction (what normalizeSplineDefinition used to do for periodic input) computes new
// control points via Boehm insertion at a boundary; nothing about that computation has any
// reason to satisfy the overlap condition, so re-flagging the result as periodic afterward
// produces a curve with a seam — position may hold, tangent/curvature will not.
//
// The fix: a periodic B-spline is the finite window onto an INFINITE periodically-extended
// structure (knots and control points both repeating every PERIOD). Boehm insertion and degree
// elevation are LOCAL — they only touch `degree` neighboring control points around wherever
// they operate. So: tile that structure into a finite window, apply the same operation at every
// periodic image that reaches into the core, then slice the core period back out. The result is
// automatically overlap-consistent with its (identically treated) neighbours, because the
// infinite periodic structure was never actually broken — only sliced from a window wide enough
// that the slicing itself introduces no error.
//
// TWO windows, because the two operations have genuinely different requirements — do not merge
// them back together:
//   * buildPeriodicWindow (refinement): UNCLAMPED, margin measured in CONTROL POINTS
//     (2*degree + 2). Insertion needs nothing but locality, so the margin only has to cover the
//     reach of a blend plus the seam-straddling points the extraction slices.
//   * buildPeriodicWideClampedWindow (elevation): CLAMPED, margin of whole PERIODS. Bezier
//     decomposition rejects an unclamped knot array, and its trailing removeKnots pass reasons
//     globally, so every period must be an identical tile for decisions to match across the wrap.
//     The period count is computed as ceil((degree + 1) / n) rather than fixed at 1, so tight
//     periodic splines (fewer control points per period than the degree) still work.
//
// COST: refinement uses refineKnotVector (direct sequential insertion), NOT
// knotRefinementOperator, because an operator built for a single point array is reuse that never
// happens. The WINDOW WIDTH is the cost that actually matters here and is why two windows exist at
// all: at a 300-point period, the refinement window is 316 points against the clamped elevation
// window's 903, and everything downstream is linear in that. (The operator itself was once an
// unconditional O(M^2) build on top of this, which made the choice lopsided; banded coefficient
// rows removed that term, so it is now a mild preference rather than a large one.)
// ============================================================================================

/**
 * Strip a STORED periodic (controlPoints, knots) pair down to its fundamental (one-period)
 * form: the n unique control points (dropping the degree-sized overlap tail) and the n knot
 * values spanning exactly one period (knots[degree .. degree + n - 1]), plus the period width.
 */
function extractFundamentalPeriodicCurveData(controlPoints is array, knots is array, degree is number) returns map
{
    const n = size(controlPoints) - degree;
    const fundamentalControlPoints = subArray(controlPoints, 0, n);
    const fundamentalKnots = subArray(knots, degree, degree + n);
    const period = knots[degree + n] - knots[degree];
    return { "fundamentalControlPoints" : fundamentalControlPoints, "fundamentalKnots" : fundamentalKnots, "period" : period, "n" : n };
}

/**
 * Build a periodic-padded knot array of any requested size from a fundamental (one-period)
 * knot list: knots[i] = fundamentalKnots[(i - originOffset) mod n] + floor((i - originOffset) / n) * period.
 * `originOffset` is the array index at which fundamentalKnots[0] (the period's own domain
 * start) should land — `degree` for the standard STORED form (n + 2*degree + 1 total knots),
 * `degree + n` for a window with one full margin period placed before that domain start.
 */
function buildPeriodicKnotArray(fundamentalKnots is array, period is number, originOffset is number, totalKnotCount is number) returns array
{
    const n = size(fundamentalKnots);
    var knots = makeArray(totalKnotCount, fundamentalKnots[0]);
    for (var knotIndex = 0; knotIndex < totalKnotCount; knotIndex += 1)
    {
        const shifted = knotIndex - originOffset;
        const cycle = floor(shifted / n);
        const index = shifted - cycle * n; // always in [0, n) by the definition of floor division, negative shifted included
        knots[knotIndex] = fundamentalKnots[index] + cycle * period;
    }
    return knots;
}

/**
 * The wrap-form STORED knot array equivalent to a CLOSED CLAMPED periodic curve's clamped
 * knots: fundamental list = [seam at multiplicity `degree`, then the interior knots verbatim],
 * wrap-padded. `pointCount` is the CLOSED-CLAMPED point count N (n_fundamental = N - 1).
 *
 * Why the seam carries multiplicity exactly `degree`: the closed clamped curve repeated
 * end-to-end IS its own periodic extension (C0 by closure), and the concatenated clamped
 * representation joins copies at multiplicity `degree` — so the periodic structure's per-period
 * knot list is [seam^degree, interior], count degree + (N - degree - 1) = N - 1 = n, matching
 * the fundamental point count identically. The closure's actual smoothness stays carried by the
 * control point geometry, exactly as in the input. This is NURBS Book §12.1 (curve unclamping,
 * Algorithm A12.1) in its periodic-identification form.
 */
function closedClampedPeriodicKnots(knots is array, degree is number, pointCount is number) returns array
{
    const n = pointCount - 1;
    const domainStart = knots[degree];
    const period = knots[pointCount] - domainStart;
    var fundamentalKnots = makeArray(n, domainStart);
    for (var interiorIndex = 0; interiorIndex < pointCount - degree - 1; interiorIndex += 1)
    {
        fundamentalKnots[degree + interiorIndex] = knots[degree + 1 + interiorIndex];
    }
    return buildPeriodicKnotArray(fundamentalKnots, period, degree, n + 2 * degree + 1);
}

/**
 * Multiplicity of the knot AT THE SEAM of a stored periodic direction — the domain start,
 * knots[degree]. This decides the legal EMISSION form: a wrap-form periodic surface/curve with
 * seam multiplicity >= degree is rejected by the kernel as a non-smooth periodic seam, while
 * the SAME curve in closed clamped form (the kernel's own output convention) is accepted, with
 * the closure's smoothness judged from the control point geometry instead.
 */
export function seamKnotMultiplicity(knots is array, degree is number) returns number
{
    const seamValue = knots[degree];
    var multiplicity = 0;
    for (var knotIndex = 0; knotIndex < size(knots); knotIndex += 1)
    {
        if (abs(knots[knotIndex] - seamValue) <= KNOT_PARAMETER_TOLERANCE)
        {
            multiplicity += 1;
        }
    }
    return multiplicity;
}

/**
 * Tile the infinite periodic structure into a finite window: the core period plus
 * `marginPoints` control points of margin on EACH side. Pure array construction — no operator,
 * no clamping — so it costs O(window size) and nothing more.
 *
 * Margin is measured in CONTROL POINTS, not whole periods, because that is what the locality
 * argument actually needs. Boehm insertion at a parameter only rewrites control points whose
 * support contains it (at most `degree` of them, each a blend of two neighbours), so the core's
 * refined points are exact as soon as every input within `degree` of the core is present, plus
 * `degree` more knots of reach on each side for the seam-straddling points the extraction
 * slices. `2 * degree + 2` covers both with slack. For a 300-point period at degree 3 that is a
 * 316-point window instead of the 903-point one a whole-period margin would build — and it is
 * the window size that multiplies through everything downstream.
 */
/**
 * True when `knots` is genuinely wrap-padded: knots[i + n] == knots[i] + period for every valid
 * i. This module's tile/operate/slice periodic construction is exact ONLY on this form; the
 * other valid periodic convention (CLOSED CLAMPED — see normalizeSplineDefinition) is converted
 * to this form at normalization, never operated on directly.
 */
export function isWrapPaddedPeriodicKnots(knots is array, degree is number, n is number) returns boolean
{
    const period = knots[degree + n] - knots[degree];
    if (period <= KNOT_PARAMETER_TOLERANCE)
    {
        return false;
    }
    for (var knotIndex = 0; knotIndex < size(knots) - n; knotIndex += 1)
    {
        if (abs(knots[knotIndex + n] - (knots[knotIndex] + period)) > KNOT_PARAMETER_TOLERANCE)
        {
            return false;
        }
    }
    return true;
}

/**
 * Internal assertion: every real (non-identity) periodic operation runs on wrap-padded knots.
 * After normalizeSplineDefinition/normalizeSurfaceDefinition — which convert the closed-clamped
 * kernel convention to wrap form exactly — this should be unreachable; reaching it means a
 * caller bypassed normalization with unconverted data.
 */
function verifyGenuinePeriodicPadding(knots is array, degree is number, n is number, period is number)
{
    if (!isWrapPaddedPeriodicKnots(knots, degree, n))
    {
        throw "splineRefinementUtils: internal error - a periodic operation received knots that are not " ~
            "wrap-padded. All recognized periodic conventions are converted to wrap form by " ~
            "normalizeSplineDefinition/normalizeSurfaceDefinition; reaching this means a caller bypassed " ~
            "normalization. knots: " ~ knots;
    }
}

/**
 * How many knots at the TAIL of a wrap-form fundamental knot list are periodic images of the
 * seam (values equal to fundamentalKnots[0] + period). A nonzero count means the seam's modular
 * multiplicity run is SPLIT across the domain boundary — a VALID but NONCANONICAL stored form:
 * the wrap relation still holds and evaluation is unaffected, but every consumer that compares
 * knot STRUCTURE (directionSharingPlan's run merge, seamKnotMultiplicity, clamped emission)
 * assumes the whole run sits contiguously at the domain start. Two spellings of one structure
 * is exactly how a reversed revolve direction became unshareable with its unreversed partner:
 * the run merge saw phantom extra knots at the seam's far image and demanded insertions above
 * the multiplicity cap (live cone-to-cylinder failure, 2026-08-08). REVERSAL is the operation
 * that manufactures the split — reflecting [seam x m, interior...] puts m - 1 seam images at
 * the far end whenever m > 1 — while conversion from closed-clamped never does.
 */
function seamImageTailCount(fundamentalKnots is array, period is number) returns number
{
    const n = size(fundamentalKnots);
    const seamImage = fundamentalKnots[0] + period;
    var tailCount = 0;
    while (tailCount < n - 1 && abs(fundamentalKnots[n - 1 - tailCount] - seamImage) <= KNOT_PARAMETER_TOLERANCE)
    {
        tailCount += 1;
    }
    return tailCount;
}

/**
 * Re-cut one periodic point/weight cycle so it starts at fundamental index `shift`: the shared
 * gather of rewindowPeriodicSpline / rewindowPeriodicSurfaceDirection, factored RAW (no
 * normalization) so the normalize-time seam canonicalization can use it without recursing.
 * `elements` may be the full stored array (n + degree entries) or just the fundamental — only
 * the first n entries are read. Pure re-index; no arithmetic on values.
 */
function recutPeriodicCycle(elements is array, n is number, degree is number, shift is number) returns array
{
    var recut = makeArray(n + degree, elements[0]);
    for (var index = 0; index < n + degree; index += 1)
    {
        const sourceIndex = shift + index;
        const cycle = floor(sourceIndex / n);
        recut[index] = elements[sourceIndex - cycle * n];
    }
    return recut;
}

/** The knot half of the same re-cut: gather the fundamental from index `shift`, wrapping with
    + period per cycle, and re-pad. The re-cut window's domain starts at the gathered first
    knot's value — for the canonicalization re-cut that is the seam's next periodic image, so
    the domain shifts by up to one period, which is meaningless for a periodic direction. */
function recutPeriodicKnots(knots is array, degree is number, shift is number) returns array
{
    const n = size(knots) - 2 * degree - 1;
    const fundamentalKnots = subArray(knots, degree, degree + n);
    const period = knots[degree + n] - knots[degree];
    var newFundamentalKnots = makeArray(n, 0);
    for (var knotIndex = 0; knotIndex < n; knotIndex += 1)
    {
        const sourceIndex = shift + knotIndex;
        const cycle = floor(sourceIndex / n);
        newFundamentalKnots[knotIndex] = fundamentalKnots[sourceIndex - cycle * n] + cycle * period;
    }
    return buildPeriodicKnotArray(newFundamentalKnots, period, degree, n + 2 * degree + 1);
}

function buildPeriodicWindow(controlPoints is array, knots is array, degree is number) returns map
{
    const fundamental = extractFundamentalPeriodicCurveData(controlPoints, knots, degree);
    const n = fundamental.n;
    if (n < 1)
    {
        throw "splineRefinementUtils: periodic operations need at least one control point per period, got " ~ n ~ ".";
    }
    verifyGenuinePeriodicPadding(knots, degree, n, fundamental.period);

    const marginPoints = 2 * degree + 2;
    const windowPointCount = n + 2 * marginPoints;
    var windowPoints = makeArray(windowPointCount, controlPoints[0]);
    for (var pointIndex = 0; pointIndex < windowPointCount; pointIndex += 1)
    {
        const sourceIndex = pointIndex - marginPoints;
        const cycle = floor(sourceIndex / n);
        windowPoints[pointIndex] = fundamental.fundamentalControlPoints[sourceIndex - cycle * n];
    }
    // Control point p pairs with knot degree + p, and windowPoints[marginPoints] is fundamental
    // point 0, so fundamental knot 0 must land at knot index degree + marginPoints.
    const windowKnots = buildPeriodicKnotArray(fundamental.fundamentalKnots, fundamental.period,
            degree + marginPoints, windowPointCount + degree + 1);

    return {
            "controlPoints" : windowPoints,
            "knots" : windowKnots,
            "period" : fundamental.period,
            "coreStart" : fundamental.fundamentalKnots[0],
            "coreEnd" : fundamental.fundamentalKnots[0] + fundamental.period
        };
}

/**
 * Build a CLAMPED representation spanning the core period plus `marginPeriods` whole margin
 * periods on EACH side, from a stored periodic (controlPoints, knots) pair.
 *
 * Only DEGREE ELEVATION needs this. Elevation goes through Bezier decomposition, whose
 * interiorKnotRun step rejects an unclamped knot array outright, and its per-segment
 * computation plus the trailing removeKnots pass both reason about the whole window — which is
 * why the margin here stays a whole period (so every period is an identical tile and removeKnots
 * makes identical decisions across the wrap) rather than the few control points
 * buildPeriodicWindow gets away with. Plain refinement has neither constraint and uses that
 * cheaper window instead.
 *
 * The margin must be wide enough that the clamped ends' contamination (the outermost
 * degree + 1 control points, per extractPeriodicCoreAndRepad's local-linear-independence
 * argument) cannot reach the core. One margin period contributes n control points, so
 * ceil((degree + 1) / n) periods always suffice. That is 1 for every ordinary periodic curve,
 * where n > degree; computing it rather than hardcoding 1 is what lets tight periodic splines
 * (n <= degree — a degree-3 closed curve with only 3 distinct control points, say) go through
 * the same exact path instead of being rejected as degenerate.
 */
/**
 * The knots-only half of buildPeriodicWideClampedWindow: everything that depends on
 * (knots, degree) alone and NOT on the control points, so a surface can build it once and reuse
 * it down every column. The clamp operator is the expensive part (a full operator build over the
 * wide window), and it is identical for every column of a periodic direction — rebuilding it per
 * column is exactly the amortization mistake the operator layer exists to avoid.
 */
function periodicWideClampedWindowPlan(knots is array, degree is number) returns map
{
    const n = size(knots) - 2 * degree - 1;
    if (n < 1)
    {
        throw "splineRefinementUtils: periodic operations need a STORED periodic knot array with at least " ~
            "one control point per period; got " ~ size(knots) ~ " knots at degree " ~ degree ~ ", implying n = " ~ n ~ ".";
    }
    const fundamentalKnots = subArray(knots, degree, degree + n);
    const period = knots[degree + n] - knots[degree];
    verifyGenuinePeriodicPadding(knots, degree, n, period);
    const marginPeriods = max(1, ceil((degree + 1) / n));

    const wideControlPointCount = (2 * marginPeriods + 1) * n + degree;
    // Control point `marginPeriods * n` is fundamental point 0, so its knot — fundamental knot
    // 0, the core's domain start — must land at knot index degree + marginPeriods * n.
    const rawWideKnots = buildPeriodicKnotArray(fundamentalKnots, period,
            degree + marginPeriods * n, wideControlPointCount + degree + 1);

    const domainStart = fundamentalKnots[0];
    const domainEnd = domainStart + period;
    const marginWidth = marginPeriods * period;

    return {
            "n" : n,
            "wideControlPointCount" : wideControlPointCount,
            "clampOperator" : clampedSegmentOperator(rawWideKnots, degree, domainStart - marginWidth, domainEnd + marginWidth),
            "period" : period,
            "coreStart" : domainStart,
            "coreEnd" : domainEnd
        };
}

/** Tile one point array through a plan's window and clamp it, giving the wide clamped points. */
function applyPeriodicWideClampedWindow(plan is map, controlPoints is array) returns array
{
    const n = plan.n;
    var wideControlPoints = makeArray(plan.wideControlPointCount, controlPoints[0]);
    for (var pointIndex = 0; pointIndex < plan.wideControlPointCount; pointIndex += 1)
    {
        const cycle = floor(pointIndex / n);
        wideControlPoints[pointIndex] = controlPoints[pointIndex - cycle * n];
    }
    return applyKnotRefinementOperator(plan.clampOperator, wideControlPoints);
}

function buildPeriodicWideClampedWindow(controlPoints is array, knots is array, degree is number) returns map
{
    const plan = periodicWideClampedWindowPlan(knots, degree);
    return {
            "controlPoints" : applyPeriodicWideClampedWindow(plan, controlPoints),
            "knots" : plan.clampOperator.knots,
            "period" : plan.period,
            "coreStart" : plan.coreStart,
            "coreEnd" : plan.coreEnd
        };
}

/**
 * Extract the core period back out of a (just-refined-or-elevated) wide window and rebuild the
 * genuine STORED periodic form.
 *
 * This is a raw index SLICE of the wide window's control points, NOT a clampedSegmentOperator
 * extraction — deliberately, and this distinction is the crux of the whole periodic design. A
 * clamped extraction computes NEW control points near the segment ends so the extracted piece
 * interpolates them; those are exactly the wrong values here, because the stored periodic form
 * needs the seam-straddling de Boor points of the underlying periodic structure, not
 * interpolating ones (the module section's block comment makes this same argument against
 * clamping at normalization time — it applies equally at extraction time). At degree 1 the two
 * happen to coincide, since degree-1 control points lie ON the curve; a clamp-based version of
 * this function passed every degree-1 test while being wrong for all degrees >= 2.
 *
 * Why the slice is exact: the wide window represents the same FUNCTION as the periodic curve
 * across all three periods, and after any synchronized operation its interior knots still tile
 * identically period-to-period. By local linear independence of the B-spline basis, a control
 * point whose basis support lies strictly inside the window's domain is uniquely determined by
 * that function — so it MUST equal the corresponding control point of the infinite periodic
 * structure. Only the first and last (degree + 1) window points, whose support touches a
 * clamped end, can deviate. Slicing interior points therefore reproduces the periodic
 * structure's own points verbatim, and the overlap condition P[i] == P[i + n] holds because
 * both slots are tiled images of the same fundamental point. The guard below verifies the
 * slice stays clear of both contaminated ends, and throws — rather than returning something
 * subtly wrong — for periods too coarse relative to the degree for a one-period margin.
 */
function extractPeriodicCoreAndRepad(widePoints is array, wideKnots is array, degree is number, coreStart is number, coreEnd is number, period is number) returns map
{
    // Locate the core period's knots in the wide (post-operation) knot vector: coreStartIndex
    // is the first knot at coreStart, newN counts knots (with multiplicity) in [coreStart, coreEnd).
    var coreStartIndex = -1;
    var newN = 0;
    for (var knotIndex = 0; knotIndex < size(wideKnots); knotIndex += 1)
    {
        if (wideKnots[knotIndex] > coreStart - KNOT_PARAMETER_TOLERANCE &&
            wideKnots[knotIndex] < coreEnd - KNOT_PARAMETER_TOLERANCE)
        {
            if (coreStartIndex == -1)
            {
                coreStartIndex = knotIndex;
            }
            newN += 1;
        }
    }

    // Control point P[i] pairs with knot support [K[i], K[i + degree + 1]]; the stored form
    // puts its domain start at knot index `degree`, so the slice begins degree points before
    // the core's first knot.
    const sliceStart = coreStartIndex - degree;
    if (sliceStart < degree + 1 || sliceStart + newN + degree > size(widePoints) - degree - 1)
    {
        throw "splineRefinementUtils: periodic core extraction would reach into the window's boundary " ~
            "region, where control points are either clamp-recomputed or short of the inputs that would " ~
            "have refined them (slice [" ~ sliceStart ~ ", " ~ (sliceStart + newN + degree) ~ ") of " ~
            size(widePoints) ~ " points at degree " ~ degree ~ "). The window's margin is too narrow for " ~
            "this period and degree; widen it rather than accepting an inexact result.";
    }

    const newControlPoints = subArray(widePoints, sliceStart, sliceStart + newN + degree);
    const newFundamentalKnots = subArray(wideKnots, coreStartIndex, coreStartIndex + newN);
    const newKnots = buildPeriodicKnotArray(newFundamentalKnots, period, degree, newN + 2 * degree + 1);

    return { "controlPoints" : newControlPoints, "knots" : newKnots };
}

/**
 * Refine a periodic spline (STORED form) by inserting `parametersToInsert` (each an ABSOLUTE
 * parameter within one period, i.e. within [domainStart, domainStart + period) — matching
 * every other insertion function in this module), preserving periodicity EXACTLY: the overlap
 * condition holds for the result, not just the knot count. See the module section's block
 * comment for the windowing argument this relies on.
 */
export function refinePeriodicPoints(controlPoints is array, knots is array, degree is number, parametersToInsert is array) returns map
{
    if (size(parametersToInsert) == 0)
    {
        return { "controlPoints" : controlPoints, "knots" : knots };
    }

    const window = buildPeriodicWindow(controlPoints, knots, degree);
    const windowDomainStart = window.knots[degree];
    const windowDomainEnd = window.knots[size(window.knots) - degree - 1];

    // Each parameter is inserted at every periodic image that lands inside this window's own
    // domain. That filter is exactly the right rule, not an approximation of "insert all three
    // images": an image only changes a control point whose support contains it, and the window
    // reaches just far enough past the core for those to be the only ones that matter. So an
    // image near the period boundary IS included (it does reach a point the extraction slices),
    // while one from the middle of an adjacent period falls outside the domain and is correctly
    // skipped — which is what makes the margin shrinkable in the first place.
    var candidateImages = makeArray(3 * size(parametersToInsert), 0);
    var imageCount = 0;
    for (var parameterIndex = 0; parameterIndex < size(parametersToInsert); parameterIndex += 1)
    {
        for (var cycle = -1; cycle <= 1; cycle += 1)
        {
            const image = parametersToInsert[parameterIndex] + cycle * window.period;
            if (image > windowDomainStart + KNOT_PARAMETER_TOLERANCE &&
                image < windowDomainEnd - KNOT_PARAMETER_TOLERANCE)
            {
                candidateImages[imageCount] = image;
                imageCount += 1;
            }
        }
    }

    // Direct sequential insertion rather than knotRefinementOperator — the operator would be built
    // and thrown away after one application here. See decomposeIntoSegmentsCore for the full
    // reasoning, including how much smaller this margin became once coefficient rows went banded.
    const refined = refineKnotVector(window.controlPoints, window.knots, degree, subArray(candidateImages, 0, imageCount));

    return extractPeriodicCoreAndRepad(refined.controlPoints, refined.knots, degree, window.coreStart, window.coreEnd, window.period);
}

/**
 * Periodic analog of knotRefinementOperator: a reusable linear map from a periodic direction's
 * STORED control points (n + degree of them) to the refined stored control points
 * (n' + degree), carrying the refined stored knot vector. Same map shape as
 * knotRefinementOperator, so it feeds applyKnotRefinementOperator and BOTH tensor appliers with
 * no special casing — a periodic surface direction refines exactly like a clamped one.
 *
 * This is the operator form of refinePeriodicPoints, and it exists for the reason the operator
 * layer exists at all: a surface applies ONE refinement to every row (or column), so the
 * O(window^2) build amortizes and per-row cost drops to a handful of terms. For a single point
 * array, refinePeriodicPoints' direct insertion is cheaper — use that. The two must agree
 * exactly; the tester asserts it.
 *
 * Construction is the same tile / operate / slice as refinePeriodicPoints, composed into one
 * map instead of run on values: the tiling makes window input p read stored input
 * (p - margin) mod n, so each sliced output row is rebuilt by folding its window-index weights
 * back onto stored indices, ACCUMULATING where several window images of the same stored point
 * contribute. The overlap condition comes out of that fold for free — output rows j and j + n'
 * fold to identical stored-index weight sets, because shifting an output by one refined period
 * shifts its window inputs by exactly one input period, and those are the same stored points.
 */
export function periodicRefinementOperator(knots is array, degree is number, parametersToInsert is array) returns map
{
    const n = size(knots) - 2 * degree - 1;
    if (n < 1)
    {
        throw "splineRefinementUtils: periodicRefinementOperator expects a STORED periodic knot array (" ~
            "n + 2*degree + 1 entries for n + degree control points); got " ~ size(knots) ~ " knots at degree " ~
            degree ~ ", implying n = " ~ n ~ ".";
    }

    // Identity fast path, matching refinePeriodicPoints' own early return: with nothing to
    // insert, the correct operator is identity on the INPUT exactly as given, knots included.
    // Without this, the general path below still computes an outputCount == inputCount
    // identity-valued operator, but reaches it via buildPeriodicKnotArray - which always
    // reconstructs the outer padding by tiling the fundamental knots, discarding whatever the
    // input's own padding was. That is wrong for the same reason normalizeSplineDefinition's
    // rebuild was wrong (see its own doc comment): a periodic direction can validly arrive with
    // CLAMPED padding (multiplicity degree + 1 at the literal ends) whose wraparound is carried
    // by the overlap control points, not by knot values, and retiling it produces a different
    // array from the one the caller is entitled to get back unchanged when no work was done.
    if (size(parametersToInsert) == 0)
    {
        const identityCount = n + degree;
        var identityRows = makeArray(identityCount, 0);
        for (var rowIndex = 0; rowIndex < identityCount; rowIndex += 1)
        {
            identityRows[rowIndex] = [{ "index" : rowIndex, "weight" : 1 }];
        }
        return { "degree" : degree, "inputCount" : identityCount, "outputCount" : identityCount, "knots" : knots, "rows" : identityRows };
    }

    const fundamentalKnots = subArray(knots, degree, degree + n);
    const period = knots[degree + n] - knots[degree];
    verifyGenuinePeriodicPadding(knots, degree, n, period);

    const marginPoints = 2 * degree + 2;
    const windowPointCount = n + 2 * marginPoints;
    const windowKnots = buildPeriodicKnotArray(fundamentalKnots, period, degree + marginPoints,
            windowPointCount + degree + 1);

    const coreStart = fundamentalKnots[0];
    const coreEnd = coreStart + period;
    const windowDomainStart = windowKnots[degree];
    const windowDomainEnd = windowKnots[windowPointCount];

    // Same image filter as refinePeriodicPoints — see there for why domain membership is the
    // exact rule rather than an approximation of "insert all images".
    var candidateImages = makeArray(3 * size(parametersToInsert), 0);
    var imageCount = 0;
    for (var parameterIndex = 0; parameterIndex < size(parametersToInsert); parameterIndex += 1)
    {
        for (var cycle = -1; cycle <= 1; cycle += 1)
        {
            const image = parametersToInsert[parameterIndex] + cycle * period;
            if (image > windowDomainStart + KNOT_PARAMETER_TOLERANCE &&
                image < windowDomainEnd - KNOT_PARAMETER_TOLERANCE)
            {
                candidateImages[imageCount] = image;
                imageCount += 1;
            }
        }
    }

    const refinement = buildRefinementCoefficients(windowKnots, degree, subArray(candidateImages, 0, imageCount), degree);

    var coreStartIndex = -1;
    var newN = 0;
    for (var knotIndex = 0; knotIndex < size(refinement.knots); knotIndex += 1)
    {
        if (refinement.knots[knotIndex] > coreStart - KNOT_PARAMETER_TOLERANCE &&
            refinement.knots[knotIndex] < coreEnd - KNOT_PARAMETER_TOLERANCE)
        {
            if (coreStartIndex == -1)
            {
                coreStartIndex = knotIndex;
            }
            newN += 1;
        }
    }
    const sliceStart = coreStartIndex - degree;
    const outputCount = newN + degree;
    if (sliceStart < degree + 1 || sliceStart + outputCount > size(refinement.rows) - degree - 1)
    {
        throw "splineRefinementUtils: periodic operator extraction would reach into the window's boundary " ~
            "region (slice [" ~ sliceStart ~ ", " ~ (sliceStart + outputCount) ~ ") of " ~ size(refinement.rows) ~
            " rows at degree " ~ degree ~ "). The window's margin is too narrow for this period and degree.";
    }

    // The fold walks each row's OWN band rather than the whole window. It used to test every one of
    // the window's control point slots for every output row — an outputCount x windowPointCount
    // scan to find the handful of terms a banded row actually carries. Ascending order is preserved
    // (a band's offsets ascend, and so did the window indices), so the accumulation rounds
    // identically.
    var foldedRows = makeArray(outputCount, 0);
    for (var outputIndex = 0; outputIndex < outputCount; outputIndex += 1)
    {
        const windowRow = refinement.rows[sliceStart + outputIndex];
        var storedRow = makeArray(n + degree, 0);
        for (var offset = 0; offset < size(windowRow.weights); offset += 1)
        {
            const weight = windowRow.weights[offset];
            if (abs(weight) > SPARSE_WEIGHT_CUTOFF)
            {
                const shifted = windowRow.start + offset - marginPoints;
                const cycle = floor(shifted / n);
                const storedIndex = shifted - cycle * n;
                storedRow[storedIndex] = storedRow[storedIndex] + weight;
            }
        }
        foldedRows[outputIndex] = storedRow;
    }

    const newFundamentalKnots = subArray(refinement.knots, coreStartIndex, coreStartIndex + newN);

    return {
            "degree" : degree,
            "inputCount" : n + degree,
            "outputCount" : outputCount,
            "knots" : buildPeriodicKnotArray(newFundamentalKnots, period, degree, newN + 2 * degree + 1),
            "rows" : sparsifyCoefficientRows(foldedRows)
        };
}

// ============================================================================================
// Per-direction dispatchers.
//
// A surface direction is periodic or it is not, and every Layer 3 surface entry point has to
// branch on that. These four helpers absorb the branch ONCE so the surface functions read the
// same whether a direction wraps or not — which matters because the alternative is the same
// two-way branch written out four times, where a periodic case silently missing from one of them
// is exactly the kind of gap that ships. They also work unchanged for curves, since a curve is
// just a single point array in one direction.
//
// The payoff of periodicRefinementOperator matching knotRefinementOperator's map shape lands
// here: directionRefinementOperator returns one or the other and NOTHING downstream cares.
// ============================================================================================

/** Refinement operator for a direction, periodic or clamped. Same map shape either way. */
function directionRefinementOperator(knots is array, degree is number, isPeriodic is boolean, parametersToInsert is array) returns map
{
    return isPeriodic ? periodicRefinementOperator(knots, degree, parametersToInsert)
        : knotRefinementOperator(knots, degree, parametersToInsert);
}

/**
 * Given two knot arrays for the same direction and degree, produce each one's insertions onto a
 * common refined structure, along with the domain-remapped knots those insertions apply to.
 * Both branches remap to a canonical domain FIRST so the two sides land on a literally identical
 * knot vector rather than a merely proportional one.
 *
 * The periodic branch merges FUNDAMENTAL knot runs (one period's worth) rather than whole knot
 * arrays: the padding is derived, so merging it would double-count the wrap.
 */
function directionSharingPlan(knotsA is array, knotsB is array, degree is number, isPeriodic is boolean) returns map
{
    const remappedA = remapKnotsToUnitDomain(knotsA, degree);
    const remappedB = remapKnotsToUnitDomain(knotsB, degree);

    if (!isPeriodic)
    {
        const merged = mergeKnotVectors(remappedA, remappedB, degree);
        return {
                "remappedA" : remappedA,
                "remappedB" : remappedB,
                "insertionsA" : insertionsToReach(remappedA, merged, degree),
                "insertionsB" : insertionsToReach(remappedB, merged, degree)
            };
    }

    const nA = size(remappedA) - 2 * degree - 1;
    const nB = size(remappedB) - 2 * degree - 1;
    const runsA = distinctValueRuns(subArray(remappedA, degree, degree + nA));
    const runsB = distinctValueRuns(subArray(remappedB, degree, degree + nB));
    const merged = mergeValueRuns(runsA, runsB);
    return {
            "remappedA" : remappedA,
            "remappedB" : remappedB,
            "insertionsA" : insertionsFromMergedRuns(runsA, merged),
            "insertionsB" : insertionsFromMergedRuns(runsB, merged)
        };
}

/**
 * Elevate every point array in `pointArrays` — all sharing one knot vector, i.e. all the columns
 * or all the rows of one surface direction — from `degree` to `targetDegree`, returning the
 * single knot vector they all land on.
 *
 * NO removeKnots simplification on either branch. That is load-bearing, not an oversight:
 * removeKnots decides removability from the actual point VALUES, so two columns with identical
 * knots but different points can simplify to DIFFERENT knot vectors, and a surface whose columns
 * disagree about their knot vector is not a surface. The cost is an unminimized (always exactly
 * correct) result. elevateSplineDegree does simplify, because a lone curve has nothing to stay
 * consistent with.
 */
function elevatePointArraysSharingKnots(pointArrays is array, knots is array, degree is number, targetDegree is number, isPeriodic is boolean) returns map
{
    var elevated = makeArray(size(pointArrays), 0);
    var sharedKnots = undefined;

    if (isPeriodic)
    {
        // One plan for the whole direction — see periodicWideClampedWindowPlan.
        const plan = periodicWideClampedWindowPlan(knots, degree);
        for (var arrayIndex = 0; arrayIndex < size(pointArrays); arrayIndex += 1)
        {
            const wide = applyPeriodicWideClampedWindow(plan, pointArrays[arrayIndex]);
            const raw = elevateHomogeneousPointsRaw(wide, plan.clampOperator.knots, degree, targetDegree);
            const core = extractPeriodicCoreAndRepad(raw.points, raw.knots, targetDegree, plan.coreStart, plan.coreEnd, plan.period);
            elevated[arrayIndex] = core.controlPoints;
            sharedKnots = core.knots;
        }
        return { "pointArrays" : elevated, "knots" : sharedKnots };
    }

    for (var arrayIndex = 0; arrayIndex < size(pointArrays); arrayIndex += 1)
    {
        const raw = elevateHomogeneousPointsRaw(pointArrays[arrayIndex], knots, degree, targetDegree);
        elevated[arrayIndex] = raw.points;
        sharedKnots = raw.knots;
    }
    return { "pointArrays" : elevated, "knots" : sharedKnots };
}

/**
 * Elevate a periodic spline (STORED form) from `degree` to `targetDegree`, preserving
 * periodicity exactly, via the same tile-operate-extract technique as refinePeriodicPoints —
 * but over the CLAMPED whole-period window, which elevation genuinely needs and plain
 * refinement does not (see buildPeriodicWideClampedWindow).
 *
 * Unlike elevateSurfaceDegrees's raw-and-unsimplified use of elevateHomogeneousPointsRaw, this
 * runs a removeKnots pass on the wide window before extracting, so the result carries minimal
 * knot multiplicities (matching what non-periodic elevateSplineDegree produces) instead of the
 * Bezier-decomposed maximal-multiplicity form — which would compound into serious knot bloat
 * across repeated operations. Extraction itself works on either form (it slices points and
 * counts knots directly), so this is a quality-of-result choice, not load-bearing arithmetic.
 * Running removeKnots asymmetrically across the wrap is not a real risk: all three period
 * copies in the wide window are exact tiled duplicates of the same fundamental control points,
 * elevated through the same deterministic per-segment Bezier computation, so removeKnots's
 * per-knot tolerance check makes the identical removal decision at every corresponding knot in
 * all three copies. Requires true 4D homogeneous points (removeKnots's equality test hard-codes
 * index 3 as the weight component); refinePeriodicPoints, by contrast, is dimension-agnostic.
 */
export function elevatePeriodicPointsRaw(controlPoints is array, knots is array, degree is number, targetDegree is number) returns map
{
    if (degree >= targetDegree)
    {
        return { "controlPoints" : controlPoints, "knots" : knots };
    }

    const wide = buildPeriodicWideClampedWindow(controlPoints, knots, degree);
    const raw = elevateHomogeneousPointsRaw(wide.controlPoints, wide.knots, degree, targetDegree);
    const simplified = removeKnots(raw.points, raw.knots, targetDegree);

    return extractPeriodicCoreAndRepad(simplified.points, simplified.knots, targetDegree, wide.coreStart, wide.coreEnd, wide.period);
}

// ============================================================================================
// Layer 3 — curve-level entry points (implemented and tester-covered; see splineRefinementTester.fs)
// ============================================================================================

/**
 * Normalize any spline map (BSplineCurve, raw evCurveDefinition output, or hand-built) to the
 * module's canonical form: size(knots) == size(controlPoints) + degree + 1, and always
 * rational with a weights array (unit weights when the input was not rational) — mirroring
 * std's own getBSplineFromInput pattern in editCurve.fs, which uses exactly this
 * "always rational" convention for the same reason (one code path downstream, regardless of
 * whether the input happened to be rational).
 *
 * Periodicity is PRESERVED, not clamped — clamping a periodic input was this module's earlier
 * policy and it was wrong: it computes new control points via Boehm insertion at a boundary,
 * which has no reason to satisfy the overlap condition (P[i] = P[i+n]) a genuinely periodic
 * representation requires, so re-flagging the result periodic afterward produces a curve with
 * a seam. Every downstream entry point in this module (refineSplineToControlPointCount,
 * elevateSplineDegree, makeSplinesShareKnotVector) branches internally to use the genuine
 * periodic-preserving primitives (refinePeriodicPoints, elevatePeriodicPointsRaw) instead. The
 * specific kernel quirk where a periodic definition has exactly one overlapping knot/control
 * point (ported from std editCurve.fs cleanUpPeriodicBSplineDefinition, also duplicated
 * verbatim in tweenCurves.fs) is reconciled first, since it is a representation defect rather
 * than genuine periodicity, and left exactly as std's own version handles it.
 */
export function normalizeSplineDefinition(spline is map) returns map
{
    var normalized = spline;

    // Periodic input: normalize to the canonical wrap STORED periodic form — n + degree
    // control points (overlap tail included) with n + 2*degree + 1 wrap-padded knots
    // (knots[i + n] = knots[i] + period). THREE input forms have defined semantics, and they
    // are discriminated by their DATA, never by array counts alone — the counts collide
    // exactly, and count-based recognition shipped two real bugs here before this was learned:
    //
    //  1. Wrap-padded stored form: kept verbatim. Do NOT rebuild the padding — every downstream
    //     periodic primitive derives fundamental knots and period from the core indices
    //     [degree, degree + n) alone, so rebuilding buys nothing when the padding is genuine
    //     and destroys the curve when the input is form 3.
    //  2. Fundamental-only form (n points, n + 2*degree + 1 knots): overlap tail appended,
    //     padding rebuilt — unambiguous, since there are no overlap points to disagree with.
    //  3. CLOSED CLAMPED form — what evApproximateBSplineSurface actually returns for a
    //     revolve's periodic direction, CONFIRMED by a full raw control-grid dump (2026-08-08):
    //     ordinary clamped knots, and the LAST control point coincides with the FIRST. One
    //     coincident point — NOT a degree-wide overlap; an earlier comment here claimed
    //     otherwise from partial data (only row 0 of the raw grid was ever dumped), and the
    //     fixture built on that claim was fiction. The full circle arrives as two rational
    //     cubic Bezier arcs [P0, (r,2r), (-r,2r), (-r,0), (-r,-2r), (r,-2r), P0], weights
    //     [1, 1/3, 1/3, 1, 1/3, 1/3, 1], knots [0 x4, .5 x3, 1 x4], with isPeriodic as
    //     metadata for "this closure is smooth" (here C1: seam tangents (0,2r) on both sides
    //     with symmetric spans). Converted exactly to form 1 below.
    //
    // Anything else throws with the observed shape rather than guessing.
    if (normalized.isPeriodic == true)
    {
        const degree = normalized.degree;
        const pointCount = size(normalized.controlPoints);
        const knotCount = size(normalized.knots);

        // The form-3 conversion permutes points and weights together, so weights must exist
        // before it runs (the general force-rational block sits below this branch).
        if (normalized.isRational != true || normalized.weights == undefined)
        {
            normalized.weights = makeArray(pointCount, 1);
            normalized.isRational = true;
        }

        if (knotCount == pointCount + degree + 1 && pointCount > degree)
        {
            if (isWrapPaddedPeriodicKnots(normalized.knots, degree, pointCount - degree))
            {
                // Form 1 — keep the knots as given, EXCEPT canonicalizing a seam multiplicity
                // run split across the domain boundary (reversal manufactures those; see
                // seamImageTailCount). The re-cut is a pure re-index of the window — no
                // arithmetic on point values — and shifts the domain by up to one period,
                // which is meaningless for a periodic curve.
                const n = pointCount - degree;
                const tailCount = seamImageTailCount(subArray(normalized.knots, degree, degree + n),
                        normalized.knots[degree + n] - normalized.knots[degree]);
                if (tailCount > 0)
                {
                    normalized.controlPoints = recutPeriodicCycle(normalized.controlPoints, n, degree, n - tailCount);
                    normalized.weights = recutPeriodicCycle(normalized.weights, n, degree, n - tailCount);
                    normalized.knots = knotArray(recutPeriodicKnots(normalized.knots, degree, n - tailCount));
                }
            }
            else if (isClampedKnotArray(normalized.knots, degree) &&
                tolerantEquals(normalized.controlPoints[pointCount - 1], normalized.controlPoints[0]) &&
                abs(normalized.weights[pointCount - 1] - normalized.weights[0]) < 1e-9)
            {
                // Form 3: closed clamped, N points of which n = N - 1 are distinct. The wrap
                // stored form is a pure MODULAR GATHER — no arithmetic on point values:
                //     stored[j] = P[(j + 1 - degree) mod n]
                // with fundamental knots [seam x degree, interior verbatim] (see
                // closedClampedPeriodicKnots for why the seam carries multiplicity exactly
                // `degree`, and for the NURBS Book A12.1 grounding). Exactness: the closed
                // curve repeated end-to-end IS its own periodic extension, the concatenated
                // clamped representation carries exactly that fundamental list's tiled knot
                // pattern, and slicing one period of de Boor points out of the middle of the
                // tiling — extractPeriodicCoreAndRepad's local-linear-independence argument —
                // collapses to this closed-form gather.
                const fundamentalCount = pointCount - 1;
                var gatheredPoints = makeArray(fundamentalCount + degree, normalized.controlPoints[0]);
                var gatheredWeights = makeArray(fundamentalCount + degree, 1);
                for (var storedIndex = 0; storedIndex < fundamentalCount + degree; storedIndex += 1)
                {
                    const shifted = storedIndex + 1 - degree;
                    const sourceIndex = shifted - floor(shifted / fundamentalCount) * fundamentalCount;
                    gatheredPoints[storedIndex] = normalized.controlPoints[sourceIndex];
                    gatheredWeights[storedIndex] = normalized.weights[sourceIndex];
                }
                normalized.controlPoints = gatheredPoints;
                normalized.weights = gatheredWeights;
                normalized.knots = closedClampedPeriodicKnots(normalized.knots, degree, pointCount);
            }
            else
            {
                throw "splineRefinementUtils: periodic input matches the stored-form count but is neither " ~
                    "wrap-padded nor closed-clamped-with-coincident-endpoints. Refusing to guess - report " ~
                    "this shape so it can be handled exactly. knots: " ~ normalized.knots;
            }
        }
        else if (knotCount == pointCount + 2 * degree + 1 && pointCount > degree)
        {
            // Fundamental-only form ("0 overlapping control points"): same padded knots, but
            // the overlap tail is absent. Append exact copies of the first `degree` points
            // (and weights, when present), then rebuild the padding as above.
            const n = pointCount;
            var extendedPoints = makeArray(n + degree, normalized.controlPoints[0]);
            for (var pointIndex = 0; pointIndex < n + degree; pointIndex += 1)
            {
                extendedPoints[pointIndex] = normalized.controlPoints[pointIndex % n];
            }
            normalized.controlPoints = extendedPoints;
            if (normalized.weights != undefined)
            {
                var extendedWeights = makeArray(n + degree, 1);
                for (var weightIndex = 0; weightIndex < n + degree; weightIndex += 1)
                {
                    extendedWeights[weightIndex] = normalized.weights[weightIndex % n];
                }
                normalized.weights = extendedWeights;
            }
            const fundamentalKnots = subArray(normalized.knots, degree, degree + n);
            const period = normalized.knots[degree + n] - normalized.knots[degree];
            normalized.knots = buildPeriodicKnotArray(fundamentalKnots, period, degree, n + 2 * degree + 1);
        }
        else
        {
            throw "splineRefinementUtils: unrecognized periodic spline form - " ~ pointCount ~
                " control points with " ~ knotCount ~ " knots at degree " ~ degree ~
                ". Expected the stored form (n + degree points, n + 2*degree + 1 knots) or the " ~
                "fundamental-only form (n points, n + 2*degree + 1 knots), each with n > degree. " ~
                "Refusing to guess at a kernel quirk form - report this shape so it can be handled exactly.";
        }
    }

    if (normalized.isRational != true || normalized.weights == undefined)
    {
        normalized.weights = makeArray(size(normalized.controlPoints), 1);
        normalized.isRational = true;
    }

    // Guarantee KnotArray-typed output regardless of path, so a caller can hand the result
    // straight to bSplineCurve/opCreateBSplineSurface without its own defensive cast (a plain
    // array does NOT implicitly satisfy an `is KnotArray` precondition in FeatureScript, even
    // if its values would pass canBeKnotArray - tweenSurfaces.fs has the same defensive
    // is-KnotArray-else-cast idiom for exactly this reason).
    normalized.knots = normalized.knots is KnotArray ? normalized.knots : knotArray(normalized.knots);

    if (size(normalized.knots) != size(normalized.controlPoints) + normalized.degree + 1)
    {
        throw "splineRefinementUtils: normalizeSplineDefinition produced an inconsistent knot vector - " ~
            size(normalized.knots) ~ " knots for " ~ size(normalized.controlPoints) ~ " control points at degree " ~
            normalized.degree ~ ".";
    }
    return normalized;
}

/**
 * Clamp a periodic spline to a single non-wrapping period. Used ONLY for the mixed
 * periodic/non-periodic case in makeSplinesShareKnotVector, where the spline is about to be
 * blended against a genuinely open curve — "closed" has no shared meaning to preserve there
 * regardless of what this module can do. NOT used by the genuinely periodic-preserving paths
 * (refinePeriodicPoints, elevatePeriodicPointsRaw, makePeriodicSplinesShareKnotVector) — see
 * this module section's block comment for why clamping alone is wrong whenever periodicity
 * CAN be preserved instead. Result gains "wasClampedFromPeriodic" : true.
 */
function clampPeriodicSplineForMixedUse(normalized is map) returns map
{
    const domain = knotDomain(normalized.knots, normalized.degree);
    const clamp = clampedSegmentOperator(normalized.knots, normalized.degree, domain.start, domain.end);
    const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
    const clampedHomogeneous = applyKnotRefinementOperator(clamp, homogeneousPoints);
    const separated = separatePointsAndWeights(clampedHomogeneous);

    var result = normalized;
    result.controlPoints = separated.points;
    result.weights = separated.weights;
    result.knots = knotArray(clamp.knots);
    result.isPeriodic = false;
    result.wasClampedFromPeriodic = true;
    return result;
}

/**
 * Choose `numToInsert` new parameters for a PERIODIC direction via repeated widest-span
 * selection, operating on fundamental (one-period) knots and additionally considering the
 * WRAP span (from the last fundamental knot back to the first, one period later) that a plain
 * array of fundamental knots does not explicitly represent. Otherwise identical in spirit to
 * widestSpanMidpointInsertions. Returns ABSOLUTE parameters (within [fundamentalKnots[0],
 * fundamentalKnots[0] + period)), matching every insertion function in this module.
 */
function widestPeriodicSpanMidpointInsertions(fundamentalKnots is array, period is number, numToInsert is number) returns array
{
    var insertions = makeArray(numToInsert, 0);
    var workingKnots = fundamentalKnots;
    for (var insertIndex = 0; insertIndex < numToInsert; insertIndex += 1)
    {
        const workingN = size(workingKnots);
        var widestIndex = 0;
        var widestWidth = (workingN > 1 ? workingKnots[1] : workingKnots[0] + period) - workingKnots[0];
        for (var spanIndex = 1; spanIndex < workingN; spanIndex += 1)
        {
            const nextValue = spanIndex + 1 < workingN ? workingKnots[spanIndex + 1] : workingKnots[0] + period;
            const width = nextValue - workingKnots[spanIndex];
            if (width > widestWidth)
            {
                widestWidth = width;
                widestIndex = spanIndex;
            }
        }

        const nextValueForWidest = widestIndex + 1 < workingN ? workingKnots[widestIndex + 1] : workingKnots[0] + period;
        const midpoint = (workingKnots[widestIndex] + nextValueForWidest) / 2;
        insertions[insertIndex] = midpoint;

        var updatedKnots = makeArray(workingN + 1, 0);
        for (var knotIndex = 0; knotIndex <= widestIndex; knotIndex += 1)
        {
            updatedKnots[knotIndex] = workingKnots[knotIndex];
        }
        updatedKnots[widestIndex + 1] = midpoint;
        for (var knotIndex = widestIndex + 1; knotIndex < workingN; knotIndex += 1)
        {
            updatedKnots[knotIndex + 1] = workingKnots[knotIndex];
        }
        workingKnots = updatedKnots;
    }
    return insertions;
}

/**
 * Distinct values and multiplicities of a plain SORTED array — the periodic analog of
 * interiorKnotRun, which requires a clamped array with degree-known ends. Used for fundamental
 * (one-period) knot lists, where every entry is "interior" (there are no clamped ends).
 */
function distinctValueRuns(values is array) returns array
{
    var distinctCount = 0;
    for (var valueIndex = 0; valueIndex < size(values); valueIndex += 1)
    {
        if (valueIndex == 0 || abs(values[valueIndex] - values[valueIndex - 1]) > KNOT_PARAMETER_TOLERANCE)
        {
            distinctCount += 1;
        }
    }

    var runs = makeArray(distinctCount, 0);
    var runIndex = -1;
    for (var valueIndex = 0; valueIndex < size(values); valueIndex += 1)
    {
        if (runIndex == -1 || abs(values[valueIndex] - runs[runIndex].value) > KNOT_PARAMETER_TOLERANCE)
        {
            runIndex += 1;
            runs[runIndex] = { "value" : values[valueIndex], "multiplicity" : 1 };
        }
        else
        {
            runs[runIndex] = { "value" : runs[runIndex].value, "multiplicity" : runs[runIndex].multiplicity + 1 };
        }
    }
    return runs;
}

/**
 * Merge two sorted lists of { "value", "multiplicity" } runs, taking the max multiplicity of
 * each distinct value present in either. The shared core of mergeKnotVectors (clamped case)
 * and mergeFundamentalPeriodicKnotsAndInsertions (periodic case) — extracted so this
 * merge-sort-style walk exists once, not duplicated.
 */
function mergeValueRuns(runsA is array, runsB is array) returns map
{
    var mergedCount = 0;
    var indexA = 0;
    var indexB = 0;
    while (indexA < size(runsA) || indexB < size(runsB))
    {
        mergedCount += 1;
        const hasA = indexA < size(runsA);
        const hasB = indexB < size(runsB);
        if (hasA && hasB && abs(runsA[indexA].value - runsB[indexB].value) <= KNOT_PARAMETER_TOLERANCE)
        {
            indexA += 1;
            indexB += 1;
        }
        else if (!hasB || (hasA && runsA[indexA].value < runsB[indexB].value))
        {
            indexA += 1;
        }
        else
        {
            indexB += 1;
        }
    }

    var mergedValues = makeArray(mergedCount, 0);
    var mergedMultiplicities = makeArray(mergedCount, 0);
    indexA = 0;
    indexB = 0;
    var writeIndex = 0;
    while (indexA < size(runsA) || indexB < size(runsB))
    {
        const hasA = indexA < size(runsA);
        const hasB = indexB < size(runsB);
        if (hasA && hasB && abs(runsA[indexA].value - runsB[indexB].value) <= KNOT_PARAMETER_TOLERANCE)
        {
            mergedValues[writeIndex] = runsA[indexA].value;
            mergedMultiplicities[writeIndex] = max(runsA[indexA].multiplicity, runsB[indexB].multiplicity);
            indexA += 1;
            indexB += 1;
        }
        else if (!hasB || (hasA && runsA[indexA].value < runsB[indexB].value))
        {
            mergedValues[writeIndex] = runsA[indexA].value;
            mergedMultiplicities[writeIndex] = runsA[indexA].multiplicity;
            indexA += 1;
        }
        else
        {
            mergedValues[writeIndex] = runsB[indexB].value;
            mergedMultiplicities[writeIndex] = runsB[indexB].multiplicity;
            indexB += 1;
        }
        writeIndex += 1;
    }
    return { "values" : mergedValues, "multiplicities" : mergedMultiplicities };
}

/**
 * Given `runs` and a superset `merged` (from mergeValueRuns, where every runs[*].value also
 * appears in merged with >= multiplicity), the flat list of parameters to insert to reach
 * `merged` exactly. Same lockstep-walk idea as insertionsToReach, generalized off
 * interiorKnotRun's specific run shape to the plain {values, multiplicities} shape
 * mergeValueRuns produces (kept separate from insertionsToReach to avoid touching that
 * already-verified function).
 */
function insertionsFromMergedRuns(runs is array, merged is map) returns array
{
    var insertionCount = 0;
    var runIndex = 0;
    for (var mergedIndex = 0; mergedIndex < size(merged.values); mergedIndex += 1)
    {
        var existingMultiplicity = 0;
        if (runIndex < size(runs) && abs(runs[runIndex].value - merged.values[mergedIndex]) <= KNOT_PARAMETER_TOLERANCE)
        {
            existingMultiplicity = runs[runIndex].multiplicity;
            runIndex += 1;
        }
        insertionCount += merged.multiplicities[mergedIndex] - existingMultiplicity;
    }

    var insertions = makeArray(insertionCount, 0);
    var writeIndex = 0;
    runIndex = 0;
    for (var mergedIndex = 0; mergedIndex < size(merged.values); mergedIndex += 1)
    {
        var existingMultiplicity = 0;
        if (runIndex < size(runs) && abs(runs[runIndex].value - merged.values[mergedIndex]) <= KNOT_PARAMETER_TOLERANCE)
        {
            existingMultiplicity = runs[runIndex].multiplicity;
            runIndex += 1;
        }
        const neededInsertions = merged.multiplicities[mergedIndex] - existingMultiplicity;
        for (var repeatIndex = 0; repeatIndex < neededInsertions; repeatIndex += 1)
        {
            insertions[writeIndex] = merged.values[mergedIndex];
            writeIndex += 1;
        }
    }
    return insertions;
}

/**
 * Periodic analog of makeSplinesShareKnotVector's remap+merge+insertionsToReach pipeline:
 * refines both periodic splines (equal degree, checked by the caller) onto a common
 * fundamental knot structure, using refinePeriodicPoints so periodicity is preserved exactly
 * rather than lost to clamping.
 *
 * Both curves' OWN knot arrays are rescaled to a canonical [0, 1)-period domain FIRST, via
 * remapKnotsToUnitDomain - a pure affine reparameterization, degree/periodicity-agnostic, that
 * changes nothing geometrically. Only then are the fundamental knot positions merged and
 * inserted, in that same shared normalized domain, so the two outputs land on a LITERALLY
 * identical knot vector - matching the non-periodic sibling branch's postcondition exactly,
 * not just a proportional one. (An earlier version merged in normalized space but then mapped
 * insertions back to each curve's own original absolute domain/period, which never produced
 * matching arrays for two curves with different periods.)
 */
function makePeriodicSplinesShareKnotVector(splineA is map, splineB is map) returns map
{
    const degree = splineA.degree;
    const plan = directionSharingPlan(splineA.knots, splineB.knots, degree, true);

    const homogeneousA = combinePointsAndWeights(splineA.controlPoints, splineA.weights);
    const homogeneousB = combinePointsAndWeights(splineB.controlPoints, splineB.weights);
    const refinedA = refinePeriodicPoints(homogeneousA, plan.remappedA, degree, plan.insertionsA);
    const refinedB = refinePeriodicPoints(homogeneousB, plan.remappedB, degree, plan.insertionsB);

    const separatedA = separatePointsAndWeights(refinedA.controlPoints);
    const separatedB = separatePointsAndWeights(refinedB.controlPoints);

    var resultA = splineA;
    resultA.controlPoints = separatedA.points;
    resultA.weights = separatedA.weights;
    resultA.knots = knotArray(refinedA.knots);

    var resultB = splineB;
    resultB.controlPoints = separatedB.points;
    resultB.weights = separatedB.weights;
    resultB.knots = knotArray(refinedB.knots);

    return { "a" : resultA, "b" : resultB };
}

/**
 * Union of two knot vectors over a SHARED domain, taking the maximum multiplicity of each
 * distinct value — the vector both splines must be refined to before their control points are
 * index-aligned (spec section 2.4). Both inputs must already span the same [start, end]
 * domain — remap with remapKnotsToUnitDomain (or any consistent affine rescale) first.
 */
export function mergeKnotVectors(knotsA is array, knotsB is array, degree is number) returns array
{
    const domainA = knotDomain(knotsA, degree);
    const domainB = knotDomain(knotsB, degree);
    if (abs(domainA.start - domainB.start) > KNOT_PARAMETER_TOLERANCE || abs(domainA.end - domainB.end) > KNOT_PARAMETER_TOLERANCE)
    {
        throw "splineRefinementUtils: mergeKnotVectors requires both knot vectors to share a domain - got [" ~
            domainA.start ~ ", " ~ domainA.end ~ "] and [" ~ domainB.start ~ ", " ~ domainB.end ~ "]. Remap to a common domain first.";
    }

    const runsA = interiorKnotRun(knotsA, degree);
    const runsB = interiorKnotRun(knotsB, degree);
    var merged = mergeValueRuns(runsA, runsB);

    var totalKnotCount = degree + 1;
    for (var mergedIndex = 0; mergedIndex < size(merged.values); mergedIndex += 1)
    {
        merged.multiplicities[mergedIndex] = min(merged.multiplicities[mergedIndex], degree);
        totalKnotCount += merged.multiplicities[mergedIndex];
    }
    totalKnotCount += degree + 1;

    var mergedKnots = makeArray(totalKnotCount, domainA.start);
    var writeIndex = degree + 1;
    for (var mergedIndex = 0; mergedIndex < size(merged.values); mergedIndex += 1)
    {
        for (var repeatIndex = 0; repeatIndex < merged.multiplicities[mergedIndex]; repeatIndex += 1)
        {
            mergedKnots[writeIndex] = merged.values[mergedIndex];
            writeIndex += 1;
        }
    }
    for (var knotIndex = writeIndex; knotIndex < totalKnotCount; knotIndex += 1)
    {
        mergedKnots[knotIndex] = domainA.end;
    }
    return mergedKnots;
}

/**
 * Refine a spline to EXACTLY targetCount control points, geometry unchanged. Replaces
 * tweenSurfaces refineCurveControlPointCount and tweenCurves matchCPCount (both retired for
 * the reasons in spec sections 2.1-2.2 — neither preserved geometry exactly).
 */
export function refineSplineToControlPointCount(spline is map, targetCount is number) returns map
{
    const normalized = normalizeSplineDefinition(spline);
    if (size(normalized.controlPoints) >= targetCount)
    {
        return normalized;
    }

    if (normalized.isPeriodic == true)
    {
        // Stored count is n + degree, so a target STORED count implies a target FUNDAMENTAL
        // (one-period) count of targetCount - degree.
        const fundamental = extractFundamentalPeriodicCurveData(normalized.controlPoints, normalized.knots, normalized.degree);
        const targetFundamentalCount = targetCount - normalized.degree;
        const insertions = widestPeriodicSpanMidpointInsertions(fundamental.fundamentalKnots, fundamental.period, targetFundamentalCount - fundamental.n);
        const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
        const refined = refinePeriodicPoints(homogeneousPoints, normalized.knots, normalized.degree, insertions);
        const separated = separatePointsAndWeights(refined.controlPoints);

        var periodicResult = normalized;
        periodicResult.controlPoints = separated.points;
        periodicResult.weights = separated.weights;
        periodicResult.knots = knotArray(refined.knots);
        return periodicResult;
    }

    const insertions = widestSpanMidpointInsertions(normalized.knots, targetCount - size(normalized.controlPoints));
    const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
    // Direct insertion, not the operator — one point array, so the operator build would be
    // discarded after a single application (see decomposeIntoSegmentsCore).
    const refined = refineKnotVector(homogeneousPoints, normalized.knots, normalized.degree, insertions);
    const separated = separatePointsAndWeights(refined.controlPoints);

    var result = normalized;
    result.controlPoints = separated.points;
    result.weights = separated.weights;
    result.knots = knotArray(refined.knots);
    return result;
}

/**
 * Refine both splines onto their merged knot vector so control points are index-aligned and
 * interpolation between them is exact (the affine case of spec section 3.2). Degrees must
 * already match — use makeSplinesCompatible when they may not.
 */
export function makeSplinesShareKnotVector(splineA is map, splineB is map) returns map
{
    var normalizedA = normalizeSplineDefinition(splineA);
    var normalizedB = normalizeSplineDefinition(splineB);
    if (normalizedA.degree != normalizedB.degree)
    {
        throw "splineRefinementUtils: makeSplinesShareKnotVector requires equal degrees (got " ~
            normalizedA.degree ~ " and " ~ normalizedB.degree ~ ") - elevate first, or use makeSplinesCompatible.";
    }

    if (normalizedA.isPeriodic == true && normalizedB.isPeriodic == true)
    {
        return makePeriodicSplinesShareKnotVector(normalizedA, normalizedB);
    }

    // Mixed periodicity: a periodic (closed) curve and a genuinely open one cannot both keep
    // their own notion of "closed" when blended against something that has none - clamp
    // whichever side is periodic to a single non-wrapping period, then fall through to the
    // ordinary clamped path below for both.
    if (normalizedA.isPeriodic == true)
    {
        normalizedA = clampPeriodicSplineForMixedUse(normalizedA);
    }
    if (normalizedB.isPeriodic == true)
    {
        normalizedB = clampPeriodicSplineForMixedUse(normalizedB);
    }

    const degree = normalizedA.degree;

    const remappedKnotsA = remapKnotsToUnitDomain(normalizedA.knots, degree);
    const remappedKnotsB = remapKnotsToUnitDomain(normalizedB.knots, degree);
    const mergedKnots = mergeKnotVectors(remappedKnotsA, remappedKnotsB, degree);

    const insertionsA = insertionsToReach(remappedKnotsA, mergedKnots, degree);
    const insertionsB = insertionsToReach(remappedKnotsB, mergedKnots, degree);

    const refinedA = refineKnotVector(combinePointsAndWeights(normalizedA.controlPoints, normalizedA.weights), remappedKnotsA, degree, insertionsA);
    const refinedB = refineKnotVector(combinePointsAndWeights(normalizedB.controlPoints, normalizedB.weights), remappedKnotsB, degree, insertionsB);

    const separatedA = separatePointsAndWeights(refinedA.controlPoints);
    const separatedB = separatePointsAndWeights(refinedB.controlPoints);

    var resultA = normalizedA;
    resultA.controlPoints = separatedA.points;
    resultA.weights = separatedA.weights;
    resultA.knots = knotArray(refinedA.knots);

    var resultB = normalizedB;
    resultB.controlPoints = separatedB.points;
    resultB.weights = separatedB.weights;
    resultB.knots = knotArray(refinedB.knots);

    return { "a" : resultA, "b" : resultB };
}

/**
 * Full curve compatibility: common degree AND common knot vector — elevate the lower-degree
 * input first, then makeSplinesShareKnotVector. The complete form of what both tween features
 * need (spec sections 2.4 and 3.1); this is what Phase 4 calls.
 */
export function makeSplinesCompatible(splineA is map, splineB is map) returns map
{
    const normalizedA = normalizeSplineDefinition(splineA);
    const normalizedB = normalizeSplineDefinition(splineB);
    const targetDegree = max(normalizedA.degree, normalizedB.degree);
    const elevatedA = normalizedA.degree < targetDegree ? elevateSplineDegree(normalizedA, targetDegree) : normalizedA;
    const elevatedB = normalizedB.degree < targetDegree ? elevateSplineDegree(normalizedB, targetDegree) : normalizedB;
    return makeSplinesShareKnotVector(elevatedA, elevatedB);
}

/**
 * Raise every interior knot to multiplicity == degree, yielding the spline's independent
 * Bezier segments — the knot-insertion route to what the triplicated de Boor
 * subdivideIntoBeziers/splitAtFirstKnot block (std editCurve.fs, tweenCurves.fs,
 * tweenSurfaces.fs) computes; supersedes all three copies.
 */
export function decomposeIntoBezierSegments(spline is map) returns array
{
    const normalized = normalizeSplineDefinition(spline);
    const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
    const core = decomposeIntoSegmentsCore(homogeneousPoints, normalized.knots, normalized.degree);

    var segments = makeArray(size(core.segmentPointArrays), 0);
    for (var segmentIndex = 0; segmentIndex < size(segments); segmentIndex += 1)
    {
        const separated = separatePointsAndWeights(core.segmentPointArrays[segmentIndex]);
        segments[segmentIndex] = {
                "controlPoints" : separated.points,
                "weights" : separated.weights,
                "degree" : core.degree,
                "domainStart" : core.breakpoints[segmentIndex],
                "domainEnd" : core.breakpoints[segmentIndex + 1]
            };
    }
    return segments;
}

/**
 * Exact degree elevation of a curve: Bezier-decompose (via decomposeIntoSegmentsCore, in
 * homogeneous coordinates so rational input is handled uniformly), elevate each segment with
 * std elevateBezierDegree, recombine, then std removeKnots to drop the multiplicity the
 * independent per-segment elevation introduced. Geometry unchanged; this is what raises the
 * continuity CEILING that no amount of knot refinement can (spec section 3.1) — refining a
 * degree-p spline can never make it smoother than C^(p-1).
 */
export function elevateSplineDegree(spline is map, targetDegree is number) returns map
{
    const normalized = normalizeSplineDefinition(spline);
    if (normalized.degree >= targetDegree)
    {
        return normalized;
    }

    if (normalized.isPeriodic == true)
    {
        // elevatePeriodicPointsRaw already runs its own removeKnots pass internally (needed
        // for correctness there, not just space) - see its doc comment.
        const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
        const elevated = elevatePeriodicPointsRaw(homogeneousPoints, normalized.knots, normalized.degree, targetDegree);
        const separated = separatePointsAndWeights(elevated.controlPoints);

        var periodicResult = normalized;
        periodicResult.controlPoints = separated.points;
        periodicResult.weights = separated.weights;
        periodicResult.knots = knotArray(elevated.knots);
        periodicResult.degree = targetDegree;
        return periodicResult;
    }

    const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
    const raw = elevateHomogeneousPointsRaw(homogeneousPoints, normalized.knots, normalized.degree, targetDegree);
    const simplified = removeKnots(raw.points, raw.knots, targetDegree);
    const separated = separatePointsAndWeights(simplified.points);

    var result = normalized;
    result.controlPoints = separated.points;
    result.weights = separated.weights;
    result.knots = knotArray(simplified.knots);
    result.degree = targetDegree;
    return result;
}

/**
 * Reverse a spline's direction exactly: C'(t) = C(-t). Control points and weights are
 * reversed; knots are reversed AND negated (newKnots[k] = -knots[M - 1 - k]), which is what
 * keeps every control point paired with its own support interval — control point P[j], whose
 * support is [knots[j], knots[j + degree + 1]], becomes P'[m - 1 - j] with support
 * [-knots[j + degree + 1], -knots[j]].
 *
 * Exact for clamped and periodic alike, because control points are only PERMUTED, never
 * recomputed: clamped end multiplicities mirror onto the opposite end, and periodic padding is
 * preserved (knots[i + n] = knots[i] + period implies the same for the reversed array), so the
 * overlap condition survives — the reversed stored array satisfies R[j] = R[j + n] wherever
 * the original did.
 *
 * Periodic results are additionally re-CANONICALIZED: reflection splits a seam multiplicity
 * run > 1 across the domain boundary (the run sat at the domain start; its mirror sits at the
 * domain end), which is the same function in a noncanonical window — and noncanonical windows
 * are two spellings of one knot structure, which breaks structure-comparing consumers (see
 * seamImageTailCount). Re-normalizing performs that re-cut. The mult-1 seams of every earlier
 * REVERSE fixture could never split, which is why this stayed invisible until Bezier-arc
 * (multiplicity-degree) revolve structures arrived.
 *
 * The resulting domain is [-domainEnd, -domainStart], possibly shifted by one period by the
 * canonicalization re-cut. That is fine for every consumer here: makeSplinesShareKnotVector
 * remaps to a canonical domain before merging on both its clamped and periodic branches, and a
 * periodic curve's window placement is pure labelling — so read the domain off the result
 * rather than assuming it.
 */
export function reverseSpline(spline is map) returns map
{
    const normalized = normalizeSplineDefinition(spline);
    const knotCount = size(normalized.knots);

    var reversedKnots = makeArray(knotCount, 0);
    for (var knotIndex = 0; knotIndex < knotCount; knotIndex += 1)
    {
        reversedKnots[knotIndex] = -normalized.knots[knotCount - 1 - knotIndex];
    }

    var result = normalized;
    result.controlPoints = reverse(normalized.controlPoints);
    result.weights = reverse(normalized.weights);
    result.knots = knotArray(reversedKnots);
    return normalizeSplineDefinition(result);
}

/**
 * Move a periodic spline's seam (its domain start) to `seamParameter`, exactly.
 *
 * A periodic B-spline is a finite window onto an infinite periodic structure, so WHICH
 * period-length window is stored is a pure labelling choice — re-cutting it changes nothing
 * geometric, and absolute parameter values keep meaning exactly what they meant before
 * (C'(t) == C(t) everywhere both are defined). `seamParameter` is wrapped into the spline's own
 * domain; if it does not already land on a knot, one is inserted there first via
 * refinePeriodicPoints (exact), so the window has somewhere to be cut.
 *
 * This is what makes seam alignment between two closed curves an exact operation. Rotating a
 * periodic spline's control point array in place — the obvious shortcut, and what tweenCurves
 * did historically — only preserves geometry when the knot vector is uniform, because it moves
 * control points relative to knots that did not move with them.
 */
export function rewindowPeriodicSpline(spline is map, seamParameter is number) returns map
{
    var normalized = normalizeSplineDefinition(spline);
    if (normalized.isPeriodic != true)
    {
        throw "splineRefinementUtils: rewindowPeriodicSpline requires a periodic spline - got a clamped one.";
    }
    const degree = normalized.degree;

    const initial = extractFundamentalPeriodicCurveData(normalized.controlPoints, normalized.knots, degree);
    const period = initial.period;
    const domainStart = initial.fundamentalKnots[0];
    const offsetIntoPeriod = seamParameter - domainStart;
    const wrappedSeam = domainStart + (offsetIntoPeriod - floor(offsetIntoPeriod / period) * period);

    var seamIsAlreadyAKnot = false;
    for (var knotIndex = 0; knotIndex < initial.n; knotIndex += 1)
    {
        if (abs(initial.fundamentalKnots[knotIndex] - wrappedSeam) <= KNOT_PARAMETER_TOLERANCE)
        {
            seamIsAlreadyAKnot = true;
        }
    }
    if (!seamIsAlreadyAKnot)
    {
        const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
        const refined = refinePeriodicPoints(homogeneousPoints, normalized.knots, degree, [wrappedSeam]);
        const separated = separatePointsAndWeights(refined.controlPoints);
        normalized.controlPoints = separated.points;
        normalized.weights = separated.weights;
        normalized.knots = knotArray(refined.knots);
    }

    const fundamental = extractFundamentalPeriodicCurveData(normalized.controlPoints, normalized.knots, degree);
    const n = fundamental.n;
    // FIRST index of the matching multiplicity run, deliberately: cutting at a later index of
    // the run would strand the earlier copies at the window's far end — the split-seam
    // noncanonical form seamImageTailCount describes. (This loop used to keep the LAST match,
    // which did exactly that whenever the seam landed on a multiplicity > 1 knot.)
    var seamIndex = -1;
    for (var knotIndex = 0; knotIndex < n; knotIndex += 1)
    {
        if (seamIndex == -1 && abs(fundamental.fundamentalKnots[knotIndex] - wrappedSeam) <= KNOT_PARAMETER_TOLERANCE)
        {
            seamIndex = knotIndex;
        }
    }
    if (seamIndex == -1)
    {
        throw "splineRefinementUtils: rewindowPeriodicSpline could not locate seam parameter " ~ wrappedSeam ~
            " among the fundamental knots after insertion.";
    }
    if (seamIndex == 0)
    {
        return normalized;
    }

    // Re-cut the window starting at seamIndex, reading the infinite structure through its own
    // tiling rules: control points wrap plainly, knots wrap with + period per cycle.
    var newFundamentalKnots = makeArray(n, 0);
    var newControlPoints = makeArray(n + degree, normalized.controlPoints[0]);
    var newWeights = makeArray(n + degree, 1);
    for (var pointIndex = 0; pointIndex < n + degree; pointIndex += 1)
    {
        const sourceIndex = seamIndex + pointIndex;
        const cycle = floor(sourceIndex / n);
        const wrappedIndex = sourceIndex - cycle * n;
        newControlPoints[pointIndex] = fundamental.fundamentalControlPoints[wrappedIndex];
        newWeights[pointIndex] = normalized.weights[wrappedIndex];
        if (pointIndex < n)
        {
            newFundamentalKnots[pointIndex] = fundamental.fundamentalKnots[wrappedIndex] + cycle * period;
        }
    }

    var result = normalized;
    result.controlPoints = newControlPoints;
    result.weights = newWeights;
    result.knots = knotArray(buildPeriodicKnotArray(newFundamentalKnots, period, degree, n + 2 * degree + 1));
    return result;
}

/**
 * Elevate to targetDegree, THEN refine to targetControlPointCount — the correct order for
 * preparing a spline to be deformed (spec section 3.1: elevating after refining multiplies the
 * control point count for nothing). Either step is skipped when already satisfied.
 */
export function prepareSplineForDeformation(spline is map, targetDegree is number, targetControlPointCount is number) returns map
{
    const normalized = normalizeSplineDefinition(spline);
    const elevated = normalized.degree < targetDegree ? elevateSplineDegree(normalized, targetDegree) : normalized;
    return size(elevated.controlPoints) < targetControlPointCount ? refineSplineToControlPointCount(elevated, targetControlPointCount) : elevated;
}

// ============================================================================================
// Layer 3 — SURFACE ENTRY POINTS. Each is the tensor (grid) generalization of a curve-level
// helper just above: same operators, applied via applyKnotRefinementOperatorDownColumns /
// AcrossRows instead of the flat applyKnotRefinementOperator.
//
// KNOTARRAY DISCIPLINE — every curve-level function above casts its returned uKnots/vKnots
// analog with `knotArray(...)` (or the `is KnotArray ? ... : knotArray(...)` idiom in
// normalizeSplineDefinition) before returning, even though the operators underneath
// (knotRefinementOperator, refineKnotVector, clampedSegmentOperator) all produce PLAIN arrays.
// This is NOT optional: FeatureScript's typecheck types do not propagate through array
// operations, so a plain array does not implicitly satisfy `is KnotArray` even when its values
// are valid — bSplineCurve/opCreateBSplineSurface will reject it. tweenSurfaces.fs already has
// its own defensive `surface.uKnots is KnotArray ? surface.uKnots : knotArray(surface.uKnots)`
// for exactly this reason (lines 668-669, 917-918). Every surface hook below MUST cast its
// returned uKnots/vKnots the same way, so a caller can hand the result straight to
// opCreateBSplineSurface without repeating that defensive cast itself.
// ============================================================================================

/**
 * Apply combinePointsAndWeights (nurbsUtils.fs) to every row of a 2D control point grid.
 */
function combineSurfaceControlPointsAndWeights(controlPoints is array, weights is array) returns array
{
    var homogeneousGrid = makeArray(size(controlPoints), 0);
    for (var rowIndex = 0; rowIndex < size(controlPoints); rowIndex += 1)
    {
        homogeneousGrid[rowIndex] = combinePointsAndWeights(controlPoints[rowIndex], weights[rowIndex]);
    }
    return homogeneousGrid;
}

/**
 * Apply separatePointsAndWeights (nurbsUtils.fs) to every row of a homogeneous 2D grid.
 */
function separateSurfaceControlPointsAndWeights(homogeneousGrid is array) returns map
{
    var points = makeArray(size(homogeneousGrid), 0);
    var weights = makeArray(size(homogeneousGrid), 0);
    for (var rowIndex = 0; rowIndex < size(homogeneousGrid); rowIndex += 1)
    {
        const separatedRow = separatePointsAndWeights(homogeneousGrid[rowIndex]);
        points[rowIndex] = separatedRow.points;
        weights[rowIndex] = separatedRow.weights;
    }
    return { "points" : points, "weights" : weights };
}

/**
 * Rebuild a STORED periodic direction's outer knot padding from its own domain knots — the
 * surface counterpart of what normalizeSplineDefinition does per curve. Exactly idempotent for
 * already-canonical input; the point is not to trust padding slots that different producers
 * fill differently, since everything downstream derives structure from the fundamental knots.
 */
function rebuildPeriodicKnotPadding(knots is array, degree is number, n is number) returns array
{
    const fundamentalKnots = subArray(knots, degree, degree + n);
    return buildPeriodicKnotArray(fundamentalKnots, knots[degree + n] - knots[degree], degree, n + 2 * degree + 1);
}

/** True when two ROWS of a control grid coincide elementwise, points and weights both — the
    closed-clamped closure test for a U-periodic direction. */
function surfaceRowsCoincide(controlPoints is array, weights is array, rowA is number, rowB is number) returns boolean
{
    for (var columnIndex = 0; columnIndex < size(controlPoints[0]); columnIndex += 1)
    {
        if (!tolerantEquals(controlPoints[rowA][columnIndex], controlPoints[rowB][columnIndex]) ||
            abs(weights[rowA][columnIndex] - weights[rowB][columnIndex]) > 1e-9)
        {
            return false;
        }
    }
    return true;
}

/** True when two COLUMNS of a control grid coincide in every row, points and weights both — the
    closed-clamped closure test for a V-periodic direction. */
function surfaceColumnsCoincide(controlPoints is array, weights is array, columnA is number, columnB is number) returns boolean
{
    for (var rowIndex = 0; rowIndex < size(controlPoints); rowIndex += 1)
    {
        if (!tolerantEquals(controlPoints[rowIndex][columnA], controlPoints[rowIndex][columnB]) ||
            abs(weights[rowIndex][columnA] - weights[rowIndex][columnB]) > 1e-9)
        {
            return false;
        }
    }
    return true;
}

/**
 * Surface analog of normalizeSplineDefinition: force rational (row-wise unit weights when not
 * already rational) and canonicalize each periodic direction, PRESERVING periodicity.
 *
 * This used to clamp periodic directions instead, which was wrong for the same reason it was
 * wrong for curves: clamping recomputes control points at a boundary, and nothing about that
 * computation satisfies the overlap condition a genuinely periodic representation needs, so a
 * clamped-then-reflagged cylinder carries a seam. Directions are independent — a cylinder is
 * U-periodic and V-clamped, and only U gets the periodic treatment.
 *
 * The two recognized periodic forms per direction, both converted exactly by counting: the
 * STORED form (n + degree control points, n + 2*degree + 1 knots) and the fundamental-only form
 * (n control points, n + 2*degree + 1 knots), the latter gaining its overlap rows/columns back.
 * Anything else throws with the observed shape rather than being guessed at.
 */
export function normalizeSurfaceDefinition(surface is map) returns map
{
    var normalized = surface;

    if (normalized.isRational != true || normalized.weights == undefined)
    {
        var weights = makeArray(size(normalized.controlPoints), 0);
        for (var rowIndex = 0; rowIndex < size(normalized.controlPoints); rowIndex += 1)
        {
            weights[rowIndex] = makeArray(size(normalized.controlPoints[rowIndex]), 1);
        }
        normalized.weights = weights;
        normalized.isRational = true;
    }

    // Periodic directions are discriminated by DATA, never by array counts alone — see
    // normalizeSplineDefinition's block comment for the three recognized forms and the raw-dump
    // evidence behind form 3 (CLOSED CLAMPED: clamped knots, LAST row/column coinciding with the
    // FIRST — one coincident row/column, not a degree-wide overlap; this is what
    // evApproximateBSplineSurface returns for a revolve). Form 3 converts to wrap form by the
    // same modular gather the curve version uses, applied to whole rows (U) or within every row
    // (V), so the entire downstream periodic machinery sees one canonical form.
    if (normalized.isUPeriodic == true)
    {
        // U is the ROW direction: the overlap tail is `uDegree` extra ROWS.
        const rowCount = size(normalized.controlPoints);
        const uKnotCount = size(normalized.uKnots);
        if (uKnotCount == rowCount + normalized.uDegree + 1)
        {
            if (isWrapPaddedPeriodicKnots(normalized.uKnots, normalized.uDegree, rowCount - normalized.uDegree))
            {
                // Wrap form — keep uKnots as given, except canonicalizing a seam run split
                // across the domain boundary (see seamImageTailCount): re-cut whole ROWS.
                const n = rowCount - normalized.uDegree;
                const tailCount = seamImageTailCount(subArray(normalized.uKnots, normalized.uDegree, normalized.uDegree + n),
                        normalized.uKnots[normalized.uDegree + n] - normalized.uKnots[normalized.uDegree]);
                if (tailCount > 0)
                {
                    normalized.controlPoints = recutPeriodicCycle(normalized.controlPoints, n, normalized.uDegree, n - tailCount);
                    normalized.weights = recutPeriodicCycle(normalized.weights, n, normalized.uDegree, n - tailCount);
                    normalized.uKnots = knotArray(recutPeriodicKnots(normalized.uKnots, normalized.uDegree, n - tailCount));
                }
            }
            else if (isClampedKnotArray(normalized.uKnots, normalized.uDegree) &&
                surfaceRowsCoincide(normalized.controlPoints, normalized.weights, rowCount - 1, 0))
            {
                const fundamentalCount = rowCount - 1;
                var gatheredRows = makeArray(fundamentalCount + normalized.uDegree, normalized.controlPoints[0]);
                var gatheredWeightRows = makeArray(fundamentalCount + normalized.uDegree, normalized.weights[0]);
                for (var storedIndex = 0; storedIndex < fundamentalCount + normalized.uDegree; storedIndex += 1)
                {
                    const shifted = storedIndex + 1 - normalized.uDegree;
                    const sourceRow = shifted - floor(shifted / fundamentalCount) * fundamentalCount;
                    gatheredRows[storedIndex] = normalized.controlPoints[sourceRow];
                    gatheredWeightRows[storedIndex] = normalized.weights[sourceRow];
                }
                normalized.controlPoints = gatheredRows;
                normalized.weights = gatheredWeightRows;
                normalized.uKnots = closedClampedPeriodicKnots(normalized.uKnots, normalized.uDegree, rowCount);
            }
            else
            {
                throw "splineRefinementUtils: U-periodic input matches the stored-form count but is neither " ~
                    "wrap-padded nor closed-clamped-with-coincident-end-rows. Refusing to guess. uKnots: " ~ normalized.uKnots;
            }
        }
        else if (uKnotCount == rowCount + 2 * normalized.uDegree + 1)
        {
            var extendedPoints = makeArray(rowCount + normalized.uDegree, normalized.controlPoints[0]);
            var extendedWeights = makeArray(rowCount + normalized.uDegree, normalized.weights[0]);
            for (var rowIndex = 0; rowIndex < rowCount + normalized.uDegree; rowIndex += 1)
            {
                extendedPoints[rowIndex] = normalized.controlPoints[rowIndex % rowCount];
                extendedWeights[rowIndex] = normalized.weights[rowIndex % rowCount];
            }
            normalized.controlPoints = extendedPoints;
            normalized.weights = extendedWeights;
            normalized.uKnots = rebuildPeriodicKnotPadding(normalized.uKnots, normalized.uDegree, rowCount);
        }
        else
        {
            throw "splineRefinementUtils: unrecognized U-periodic surface form - " ~ rowCount ~ " rows with " ~
                uKnotCount ~ " U knots at U degree " ~ normalized.uDegree ~
                ". Expected the stored form (n + degree rows, n + 2*degree + 1 knots) or the fundamental-only " ~
                "form (n rows, n + 2*degree + 1 knots).";
        }
    }

    if (normalized.isVPeriodic == true)
    {
        // V is the COLUMN direction: the overlap tail is `vDegree` extra entries in EVERY row.
        const columnCount = size(normalized.controlPoints[0]);
        const vKnotCount = size(normalized.vKnots);
        if (vKnotCount == columnCount + normalized.vDegree + 1)
        {
            if (isWrapPaddedPeriodicKnots(normalized.vKnots, normalized.vDegree, columnCount - normalized.vDegree))
            {
                // Wrap form — keep vKnots as given, except canonicalizing a seam run split
                // across the domain boundary (see seamImageTailCount): re-cut within EVERY row.
                const n = columnCount - normalized.vDegree;
                const tailCount = seamImageTailCount(subArray(normalized.vKnots, normalized.vDegree, normalized.vDegree + n),
                        normalized.vKnots[normalized.vDegree + n] - normalized.vKnots[normalized.vDegree]);
                if (tailCount > 0)
                {
                    var recutGrid = makeArray(size(normalized.controlPoints), 0);
                    var recutWeightGrid = makeArray(size(normalized.weights), 0);
                    for (var rowIndex = 0; rowIndex < size(normalized.controlPoints); rowIndex += 1)
                    {
                        recutGrid[rowIndex] = recutPeriodicCycle(normalized.controlPoints[rowIndex], n, normalized.vDegree, n - tailCount);
                        recutWeightGrid[rowIndex] = recutPeriodicCycle(normalized.weights[rowIndex], n, normalized.vDegree, n - tailCount);
                    }
                    normalized.controlPoints = recutGrid;
                    normalized.weights = recutWeightGrid;
                    normalized.vKnots = knotArray(recutPeriodicKnots(normalized.vKnots, normalized.vDegree, n - tailCount));
                }
            }
            else if (isClampedKnotArray(normalized.vKnots, normalized.vDegree) &&
                surfaceColumnsCoincide(normalized.controlPoints, normalized.weights, columnCount - 1, 0))
            {
                const fundamentalCount = columnCount - 1;
                var gatheredGrid = makeArray(size(normalized.controlPoints), 0);
                var gatheredWeightGrid = makeArray(size(normalized.weights), 0);
                for (var rowIndex = 0; rowIndex < size(normalized.controlPoints); rowIndex += 1)
                {
                    var row = makeArray(fundamentalCount + normalized.vDegree, normalized.controlPoints[rowIndex][0]);
                    var weightRow = makeArray(fundamentalCount + normalized.vDegree, 1);
                    for (var storedIndex = 0; storedIndex < fundamentalCount + normalized.vDegree; storedIndex += 1)
                    {
                        const shifted = storedIndex + 1 - normalized.vDegree;
                        const sourceColumn = shifted - floor(shifted / fundamentalCount) * fundamentalCount;
                        row[storedIndex] = normalized.controlPoints[rowIndex][sourceColumn];
                        weightRow[storedIndex] = normalized.weights[rowIndex][sourceColumn];
                    }
                    gatheredGrid[rowIndex] = row;
                    gatheredWeightGrid[rowIndex] = weightRow;
                }
                normalized.controlPoints = gatheredGrid;
                normalized.weights = gatheredWeightGrid;
                normalized.vKnots = closedClampedPeriodicKnots(normalized.vKnots, normalized.vDegree, columnCount);
            }
            else
            {
                throw "splineRefinementUtils: V-periodic input matches the stored-form count but is neither " ~
                    "wrap-padded nor closed-clamped-with-coincident-end-columns. Refusing to guess. vKnots: " ~ normalized.vKnots;
            }
        }
        else if (vKnotCount == columnCount + 2 * normalized.vDegree + 1)
        {
            var extendedGrid = makeArray(size(normalized.controlPoints), 0);
            var extendedWeightGrid = makeArray(size(normalized.weights), 0);
            for (var rowIndex = 0; rowIndex < size(normalized.controlPoints); rowIndex += 1)
            {
                var row = makeArray(columnCount + normalized.vDegree, normalized.controlPoints[rowIndex][0]);
                var weightRow = makeArray(columnCount + normalized.vDegree, normalized.weights[rowIndex][0]);
                for (var columnIndex = 0; columnIndex < columnCount + normalized.vDegree; columnIndex += 1)
                {
                    row[columnIndex] = normalized.controlPoints[rowIndex][columnIndex % columnCount];
                    weightRow[columnIndex] = normalized.weights[rowIndex][columnIndex % columnCount];
                }
                extendedGrid[rowIndex] = row;
                extendedWeightGrid[rowIndex] = weightRow;
            }
            normalized.controlPoints = extendedGrid;
            normalized.weights = extendedWeightGrid;
            normalized.vKnots = rebuildPeriodicKnotPadding(normalized.vKnots, normalized.vDegree, columnCount);
        }
        else
        {
            throw "splineRefinementUtils: unrecognized V-periodic surface form - " ~ columnCount ~ " columns with " ~
                vKnotCount ~ " V knots at V degree " ~ normalized.vDegree ~
                ". Expected the stored form (n + degree columns, n + 2*degree + 1 knots) or the fundamental-only " ~
                "form (n columns, n + 2*degree + 1 knots).";
        }
    }

    // KnotArray discipline (see the block comment above) applies on every path - a caller may
    // hand in a plain array for uKnots/vKnots.
    normalized.uKnots = normalized.uKnots is KnotArray ? normalized.uKnots : knotArray(normalized.uKnots);
    normalized.vKnots = normalized.vKnots is KnotArray ? normalized.vKnots : knotArray(normalized.vKnots);

    if (size(normalized.uKnots) != size(normalized.controlPoints) + normalized.uDegree + 1)
    {
        throw "splineRefinementUtils: normalizeSurfaceDefinition produced an inconsistent U knot vector - " ~
            size(normalized.uKnots) ~ " knots for " ~ size(normalized.controlPoints) ~ " rows at U degree " ~ normalized.uDegree ~ ".";
    }
    if (size(normalized.vKnots) != size(normalized.controlPoints[0]) + normalized.vDegree + 1)
    {
        throw "splineRefinementUtils: normalizeSurfaceDefinition produced an inconsistent V knot vector - " ~
            size(normalized.vKnots) ~ " knots for " ~ size(normalized.controlPoints[0]) ~ " columns at V degree " ~ normalized.vDegree ~ ".";
    }
    return normalized;
}

/**
 * Refine a surface to at least the target control point counts per direction, geometry
 * unchanged. Replaces tweenSurfaces refineControlPointCount. ONE refinementOperator per
 * direction, applied to the WHOLE grid via the tensor appliers — never per isoparametric curve
 * (that was tweenSurfaces' original mistake; the operator is identical for every row/column,
 * which is the entire point of Layer 2).
 */
export function refineSurfaceToControlPointCounts(surface is map, targetUCount is number, targetVCount is number) returns map
{
    return refineSurfaceToControlPointCounts(surface, targetUCount, targetVCount, false);
}

/**
 * Balance-aware overload. With `balancedOnly` true the refinement stops short of the target rather
 * than SPLITTING A TIE — see arcLengthSpanInsertions for what that means and why it matters — so the
 * result may come back with fewer control points than asked for, never more.
 *
 * WHEN TO USE WHICH. The plain overload is right whenever the count itself is the contract: a user
 * typing "40 control points" wants forty, and the last one landing on one side of a symmetric shape
 * costs nothing, because INSERTION IS EXACT and cannot mark the surface.
 *
 * `balancedOnly` is for refinement that FEEDS A LOSSY STEP. Knot removal reads the spacing on both
 * sides of the knot it removes, so a net refined with one extra knot on one side heals lopsidedly,
 * and that asymmetry IS in the geometry — it survives as a visible artifact. Refine balanced first,
 * do the lossy work on an even net, then refine exactly to the count afterwards where the parity
 * cannot hurt anything. The multi-face merge does exactly this.
 */
export function refineSurfaceToControlPointCounts(surface is map, targetUCount is number, targetVCount is number, balancedOnly is boolean) returns map
{
    return refineSurfaceToControlPointCountsWithOperators(surface, targetUCount, targetVCount, balancedOnly).surface;
}

/**
 * The same refinement, with the two direction operators HANDED BACK so a caller can carry a SECOND
 * grid onto the identical knot vectors for free.
 *
 * This exists for one caller shape, and it is worth stating because the operators are otherwise an
 * implementation detail nobody should need: a loop that refines a surface, transforms the refined
 * net somehow, and then wants to compare the transformed result against the PREVIOUS level's
 * transformed result. The convex-hull bound that makes such a comparison meaningful requires both
 * nets to sit on one knot vector, and the obvious way to get there — makeSurfacesShareKnotVectors —
 * re-derives from scratch what this function has just finished computing: it re-normalizes both
 * surfaces, re-merges knot vectors that are already nested, rebuilds both operators, and then runs a
 * full tensor pass over the FINE grid to refine it by nothing at all. Handing the operators back
 * replaces all of that with one application to the coarse grid, which is the only work there ever
 * was. The free-refinement loop in freeFormDeformation.fs is the caller this was extracted for.
 *
 * Either operator is `undefined` when its direction already met the target and was left alone;
 * applySurfaceDirectionOperators takes that as "this direction does not move" and skips it, which is
 * the whole saving in the common case where only one direction still has room to grow.
 *
 * @returns {map} : `surface` {map} — exactly what the plain overload returns — plus `uOperator` and
 *                  `vOperator`, each an operator map or `undefined`
 */
export function refineSurfaceToControlPointCountsWithOperators(surface is map, targetUCount is number,
    targetVCount is number, balancedOnly is boolean) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    var uOperator = undefined;
    var vOperator = undefined;

    // Insertion parameters are chosen by ARC LENGTH, not parameter width. Parameter width is a
    // poor proxy on anything whose parameterization is not uniform-speed — a rational circle
    // being the standard example, where it piles control points onto one side of the cylinder.
    // Placement is a heuristic and insertion is exact wherever it lands, so this changes only the
    // distribution, never the geometry.
    if (size(normalized.controlPoints) < targetUCount)
    {
        const isPeriodic = normalized.isUPeriodic == true;
        const insertions = arcLengthSpanInsertions(normalized, true, targetUCount - size(normalized.controlPoints), balancedOnly);
        uOperator = directionRefinementOperator(normalized.uKnots, normalized.uDegree, isPeriodic, insertions);
        homogeneousGrid = applyKnotRefinementOperatorDownColumns(uOperator, homogeneousGrid);
        normalized.uKnots = knotArray(uOperator.knots);
    }
    if (size(normalized.controlPoints[0]) < targetVCount)
    {
        const isPeriodic = normalized.isVPeriodic == true;
        // Re-normalize so the profile is measured on the grid as it stands after any U pass.
        var afterU = normalized;
        const separatedForProfile = separateSurfaceControlPointsAndWeights(homogeneousGrid);
        afterU.controlPoints = separatedForProfile.points;
        afterU.weights = separatedForProfile.weights;
        const insertions = arcLengthSpanInsertions(afterU, false, targetVCount - size(normalized.controlPoints[0]), balancedOnly);
        vOperator = directionRefinementOperator(normalized.vKnots, normalized.vDegree, isPeriodic, insertions);
        homogeneousGrid = applyKnotRefinementOperatorAcrossRows(vOperator, homogeneousGrid);
        normalized.vKnots = knotArray(vOperator.knots);
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    return { "surface" : normalized, "uOperator" : uOperator, "vOperator" : vOperator };
}

/**
 * Carry a surface through a pair of direction operators produced by
 * refineSurfaceToControlPointCountsWithOperators — U down the columns first, then V across the rows,
 * which is the order that produced them and therefore the order that reproduces their knot vectors.
 *
 * The surface must be on the knot vectors those operators were BUILT from; the operators check their
 * own input counts, so a mismatched grid is caught rather than silently mis-refined. Geometry is
 * unchanged, as everywhere in this module — this is knot insertion and nothing else.
 *
 * `undefined` for either operator means that direction is left exactly as it is, which is the case a
 * caller hits whenever one direction has reached its ceiling while the other keeps refining.
 *
 * @param surface {map}
 * @param uOperator : an operator map, or `undefined` to leave U alone
 * @param vOperator : an operator map, or `undefined` to leave V alone
 * @returns {map} : the refined surface definition
 */
export function applySurfaceDirectionOperators(surface is map, uOperator, vOperator) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    if (uOperator == undefined && vOperator == undefined)
    {
        return normalized;
    }

    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    if (uOperator != undefined)
    {
        homogeneousGrid = applyKnotRefinementOperatorDownColumns(uOperator, homogeneousGrid);
        normalized.uKnots = knotArray(uOperator.knots);
    }
    if (vOperator != undefined)
    {
        homogeneousGrid = applyKnotRefinementOperatorAcrossRows(vOperator, homogeneousGrid);
        normalized.vKnots = knotArray(vOperator.knots);
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    return normalized;
}

/**
 * The sorted, distinct knot VALUES that count toward density in one direction.
 *
 * For a clamped direction that is just its distinct knot values. For a periodic direction the
 * stored array's outer padding is derived rather than independent, so the meaningful set is ONE
 * period's fundamental values together with their next-period images — which puts both the seam
 * (domain start) and its image (domain end) in the list. That inclusion is load-bearing, not
 * incidental: it is what stops a wrap-straddling cell from ever proposing an insertion on top of
 * the seam, since every parameter this file chooses is the midpoint of a gap between two
 * consecutive listed values.
 */
function densityBreakValues(knots is array, degree is number, isPeriodic is boolean) returns array
{
    // For the stored periodic form (n + 2*degree + 1 knots) this slice is exactly the
    // fundamental knots [domain.start, domain.end), the domain end excluded as a duplicate image
    // of the domain start — it is re-added below with the rest of the second period.
    const source = isPeriodic ? subArray(knots, degree, size(knots) - degree - 1) : knots;

    var distinctCount = 0;
    for (var index = 0; index < size(source); index += 1)
    {
        if (index == 0 || abs(source[index] - source[index - 1]) > KNOT_PARAMETER_TOLERANCE)
        {
            distinctCount += 1;
        }
    }
    var distinct = makeArray(distinctCount, 0);
    var writeIndex = 0;
    for (var index = 0; index < size(source); index += 1)
    {
        if (index == 0 || abs(source[index] - source[index - 1]) > KNOT_PARAMETER_TOLERANCE)
        {
            distinct[writeIndex] = source[index];
            writeIndex += 1;
        }
    }
    if (!isPeriodic)
    {
        return distinct;
    }

    // Ascending by construction: every fundamental value is < domain.end, and every image is
    // >= domain.end.
    const domain = knotDomain(knots, degree);
    const period = domain.end - domain.start;
    var withImages = makeArray(2 * distinctCount, 0);
    for (var index = 0; index < distinctCount; index += 1)
    {
        withImages[index] = distinct[index];
        withImages[index + distinctCount] = distinct[index] + period;
    }
    return withImages;
}

/** The entries of a sorted array strictly inside (rangeStart, rangeEnd), tolerance-guarded. */
function valuesStrictlyInside(sortedValues is array, rangeStart is number, rangeEnd is number) returns array
{
    var count = 0;
    for (var value in sortedValues)
    {
        if (value > rangeStart + KNOT_PARAMETER_TOLERANCE && value < rangeEnd - KNOT_PARAMETER_TOLERANCE)
        {
            count += 1;
        }
    }
    var inside = makeArray(count, 0);
    var writeIndex = 0;
    for (var value in sortedValues)
    {
        if (value > rangeStart + KNOT_PARAMETER_TOLERANCE && value < rangeEnd - KNOT_PARAMETER_TOLERANCE)
        {
            inside[writeIndex] = value;
            writeIndex += 1;
        }
    }
    return inside;
}

/**
 * The cells a density requirement applies to, as { start, end } pairs, with the boundary list
 * validated first.
 *
 * A periodic direction's boundary list is CYCLIC, so the wrap cell (last boundary -> first
 * boundary + period) is a cell like any other. Omitting it would leave a silently under-refined
 * band straddling the seam — invisible to a lattice-driven caller, which is exactly why it is
 * built here rather than left to each caller to remember.
 */
function spanDensityCells(boundaries is array, domain is map, isPeriodic is boolean, contextName is string) returns array
{
    for (var index = 0; index < size(boundaries); index += 1)
    {
        if (boundaries[index] < domain.start - KNOT_PARAMETER_TOLERANCE ||
            boundaries[index] > domain.end + KNOT_PARAMETER_TOLERANCE)
        {
            throw "splineRefinementUtils: " ~ contextName ~ " boundary " ~ boundaries[index] ~
                " lies outside the domain [" ~ domain.start ~ ", " ~ domain.end ~ "].";
        }
        if (index > 0 && boundaries[index] - boundaries[index - 1] <= KNOT_PARAMETER_TOLERANCE)
        {
            throw "splineRefinementUtils: " ~ contextName ~ " needs strictly increasing boundaries; got " ~
                boundaries[index - 1] ~ " then " ~ boundaries[index] ~ ".";
        }
    }

    if (!isPeriodic)
    {
        if (size(boundaries) < 2)
        {
            throw "splineRefinementUtils: " ~ contextName ~ " needs at least two boundaries to bound a cell on a " ~
                "clamped (non-closed) input; got " ~ size(boundaries) ~ ".";
        }
        var clampedCells = makeArray(size(boundaries) - 1, 0);
        for (var index = 0; index < size(boundaries) - 1; index += 1)
        {
            clampedCells[index] = { "start" : boundaries[index], "end" : boundaries[index + 1] };
        }
        return clampedCells;
    }

    // On a closed direction the domain end IS the domain start, so a boundary given there would
    // make the wrap cell degenerate. Say so rather than producing an empty-width cell.
    const lastBoundary = boundaries[size(boundaries) - 1];
    if (lastBoundary > domain.end - KNOT_PARAMETER_TOLERANCE)
    {
        throw "splineRefinementUtils: " ~ contextName ~ " is periodic, so its domain end (" ~ domain.end ~
            ") is the same location as its domain start (" ~ domain.start ~ "). Give that boundary once, as the " ~
            "domain start; the wrap cell back to it is added automatically.";
    }
    var cells = makeArray(size(boundaries), 0);
    for (var index = 0; index < size(boundaries) - 1; index += 1)
    {
        cells[index] = { "start" : boundaries[index], "end" : boundaries[index + 1] };
    }
    cells[size(boundaries) - 1] = { "start" : lastBoundary, "end" : boundaries[0] + (domain.end - domain.start) };
    return cells;
}

/**
 * Choose `numToInsert` new parameters strictly inside (cellStart, cellEnd) by repeated
 * widest-gap-midpoint splitting of the cell's EXISTING structure (its bounds plus whatever
 * distinct knot values already sit inside it).
 *
 * Same construction as widestSpanMidpointInsertions, scoped to one cell, and for the same reason
 * (spec section 2.2): a midpoint of a nonzero-width gap can never equal one of the gap's own
 * endpoints, so no chosen parameter ever collides with an existing knot and silently raises its
 * multiplicity. Blind even spacing across the cell has exactly that failure mode whenever the
 * input is itself uniformly parameterized.
 */
function cellMidpointInsertions(cellStart is number, cellEnd is number, interiorValues is array, numToInsert is number) returns array
{
    var breaks = makeArray(size(interiorValues) + 2, cellStart);
    for (var index = 0; index < size(interiorValues); index += 1)
    {
        breaks[index + 1] = interiorValues[index];
    }
    breaks[size(interiorValues) + 1] = cellEnd;

    var insertions = makeArray(numToInsert, 0);
    for (var insertIndex = 0; insertIndex < numToInsert; insertIndex += 1)
    {
        var widestIndex = 0;
        var widestWidth = breaks[1] - breaks[0];
        for (var gapIndex = 1; gapIndex < size(breaks) - 1; gapIndex += 1)
        {
            const width = breaks[gapIndex + 1] - breaks[gapIndex];
            if (width > widestWidth)
            {
                widestWidth = width;
                widestIndex = gapIndex;
            }
        }
        const midpoint = (breaks[widestIndex] + breaks[widestIndex + 1]) / 2;
        insertions[insertIndex] = midpoint;

        var updatedBreaks = makeArray(size(breaks) + 1, 0);
        for (var index = 0; index <= widestIndex; index += 1)
        {
            updatedBreaks[index] = breaks[index];
        }
        updatedBreaks[widestIndex + 1] = midpoint;
        for (var index = widestIndex + 1; index < size(breaks); index += 1)
        {
            updatedBreaks[index + 1] = breaks[index];
        }
        breaks = updatedBreaks;
    }
    return insertions;
}

/**
 * The parameters one direction must gain to satisfy the density requirement over `boundaries`.
 *
 * THE DENSITY DEFINITION, stated once because everything else follows from it: a cell containing
 * `d` distinct interior knot values carries `d + 1` polynomial pieces across that cell, and each
 * piece is one independent degree of freedom the deformation map can express there — so
 * "control points in the cell" means `d + 1`, and reaching `m` of them needs `m - 1 - d` new
 * knots. That count is exact, monotone under insertion, and cell-local, which is what lets each
 * cell be handled independently without any cross-cell bookkeeping.
 *
 * Insertions land STRICTLY INSIDE their own cell, so two adjacent cells can never collide on the
 * boundary they share, and the boundaries themselves are never inserted (see the entry point's
 * own comment for why that is the caller's separate decision).
 */
function spanDensityInsertions(knots is array, degree is number, isPeriodic is boolean, boundaries is array,
    minimumControlPointsPerSpan is number, contextName is string) returns array
{
    if (size(boundaries) == 0)
    {
        return [];
    }

    const domain = knotDomain(knots, degree);
    const cells = spanDensityCells(boundaries, domain, isPeriodic, contextName);
    const breaks = densityBreakValues(knots, degree, isPeriodic);

    var perCellInsertions = makeArray(size(cells), []);
    var totalCount = 0;
    for (var cellIndex = 0; cellIndex < size(cells); cellIndex += 1)
    {
        const interior = valuesStrictlyInside(breaks, cells[cellIndex].start, cells[cellIndex].end);
        const needed = minimumControlPointsPerSpan - 1 - size(interior);
        if (needed > 0)
        {
            perCellInsertions[cellIndex] = cellMidpointInsertions(cells[cellIndex].start, cells[cellIndex].end, interior, needed);
            totalCount += needed;
        }
    }

    // The wrap cell runs past the domain end, but refinePeriodicPoints (and therefore
    // periodicRefinementOperator) documents its parameters as absolute values within ONE period.
    // Fold those images back. Order does not matter to either — each parameter is expanded to
    // its own periodic images and inserted independently — so no sort is needed here.
    const period = domain.end - domain.start;
    var insertions = makeArray(totalCount, 0);
    var writeIndex = 0;
    for (var cellIndex = 0; cellIndex < size(cells); cellIndex += 1)
    {
        for (var parameter in perCellInsertions[cellIndex])
        {
            insertions[writeIndex] = (isPeriodic && parameter > domain.end - KNOT_PARAMETER_TOLERANCE) ? parameter - period : parameter;
            writeIndex += 1;
        }
    }
    return insertions;
}

/**
 * Curve form of the same contract: at least `minimumControlPointsPerSpan` control points across
 * every cell of `spanBoundaries`, geometry unchanged, periodicity preserved.
 *
 * Placed here beside its surface sibling rather than up in the curve section, because it shares
 * every helper above and the density DEFINITION (see spanDensityInsertions) is the part worth
 * reading once. The only real difference is the application: one point array, so direct
 * insertion, never an operator — the operator build would be discarded after a single use.
 */
export function refineSplineToSpanDensity(spline is map, spanBoundaries is array, minimumControlPointsPerSpan is number) returns map
{
    if (minimumControlPointsPerSpan < 1)
    {
        throw "splineRefinementUtils: refineSplineToSpanDensity needs minimumControlPointsPerSpan >= 1 (got " ~
            minimumControlPointsPerSpan ~ "); every cell already carries at least one.";
    }

    var normalized = normalizeSplineDefinition(spline);
    const isPeriodic = normalized.isPeriodic == true;
    const insertions = spanDensityInsertions(normalized.knots, normalized.degree, isPeriodic, spanBoundaries,
            minimumControlPointsPerSpan, "refineSplineToSpanDensity");
    if (size(insertions) == 0)
    {
        return normalized;
    }

    const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
    const refined = isPeriodic
        ? refinePeriodicPoints(homogeneousPoints, normalized.knots, normalized.degree, insertions)
        : refineKnotVector(homogeneousPoints, normalized.knots, normalized.degree, insertions);
    const separated = separatePointsAndWeights(refined.controlPoints);

    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    normalized.knots = knotArray(refined.knots);
    return normalized;
}

// ============================================================================================
// Layer 3 — INTERPOLATION (NURBS Book ch. 9). Construct the B-spline that passes exactly THROUGH
// a given set of points, rather than one derived from an existing spline.
//
// This is the module's first constructive entry point — everything else transforms a spline that
// already exists. It is here because merging several faces into one patch (EDIT_SURFACE_SPEC
// section 5A) needs it and std has nothing equivalent: there is no surface fitter anywhere in the
// library, as established when simplification was added.
//
// THE OPERATOR SHAPE REPEATS. The interpolation matrix depends only on (parameters, knots, degree)
// — never on the points — so its inverse is built ONCE per direction and applied to every row or
// column of a grid. That is the same amortization argument as the refinement operator layer, one
// chapter later in the same book, and it is what makes the surface case n one-dimensional solves
// per direction instead of one enormous two-dimensional one.
// ============================================================================================

/**
 * Chord-length parameters in [0, 1] for a point sequence: each point gets the fraction of total
 * polyline length at which it sits.
 *
 * Chord length rather than uniform spacing because uniform parameters over unevenly spaced data
 * produce the classic interpolation overshoot — the curve loops out past widely separated points
 * trying to keep a constant speed it was never given.
 */
function chordLengthParameters(points is array) returns array
{
    const count = size(points);
    var distances = makeArray(count, 0 * meter);
    var totalLength = 0 * meter;
    for (var index = 1; index < count; index += 1)
    {
        distances[index] = norm(points[index] - points[index - 1]);
        totalLength += distances[index];
    }
    if (totalLength <= 0 * meter)
    {
        throw "splineRefinementUtils: cannot parameterize a point sequence of zero total length - every point is " ~
            "coincident, so there is no curve to interpolate.";
    }

    var parameters = makeArray(count, 0);
    for (var index = 1; index < count - 1; index += 1)
    {
        parameters[index] = parameters[index - 1] + distances[index] / totalLength;
    }
    parameters[count - 1] = 1;
    return parameters;
}

/** Elementwise average of several parameter arrays of equal length — how a surface reconciles the
    differing chord-length parameterizations of its own rows into one shared direction parameter
    (NURBS Book Algorithm A9.4). */
function averagedParameters(parameterSets is array) returns array
{
    const count = size(parameterSets[0]);
    var averaged = makeArray(count, 0);
    for (var index = 0; index < count; index += 1)
    {
        var total = 0;
        for (var set in parameterSets)
        {
            total += set[index];
        }
        averaged[index] = total / size(parameterSets);
    }
    return averaged;
}

/**
 * The knot vector for interpolation at the given parameters, by averaging (NURBS Book eq. 9.8).
 *
 * Averaging rather than any other placement because it is what makes the interpolation matrix
 * banded and, in Piegl & Tiller's phrasing, totally positive — i.e. guaranteed nonsingular. A knot
 * vector chosen any other way can produce a system with no solution at all.
 */
function averagedInterpolationKnots(parameters is array, degree is number) returns array
{
    const count = size(parameters);
    if (count < degree + 1)
    {
        throw "splineRefinementUtils: interpolating " ~ count ~ " points at degree " ~ degree ~ " is impossible - a " ~
            "degree-" ~ degree ~ " B-spline needs at least " ~ (degree + 1) ~ " control points, so it cannot be made " ~
            "to pass through fewer points than that. Lower the degree or supply more points.";
    }

    // Clamps at the parameters' OWN ends, not literal 0 and 1 — chord-length parameters happen to
    // span [0, 1], but prescribed parameters (the lofting and unisolvence cases) span whatever
    // domain the caller's data lives in, and the knot vector must match it.
    const knotCount = count + degree + 1;
    var knots = makeArray(knotCount, parameters[0]);
    for (var index = knotCount - degree - 1; index < knotCount; index += 1)
    {
        knots[index] = parameters[count - 1];
    }
    for (var j = 1; j <= count - degree - 1; j += 1)
    {
        var total = 0;
        for (var i = j; i <= j + degree - 1; i += 1)
        {
            total += parameters[i];
        }
        knots[j + degree] = total / degree;
    }
    return knots;
}

/**
 * The INVERSE of the interpolation matrix, as plain rows, plus the knots it belongs to.
 *
 * Built once and applied to many point arrays — the whole reason the surface case is affordable.
 * Row k of the forward matrix holds the degree+1 nonzero basis values at parameter k; inverting it
 * turns "these control points give those data points" into "those data points require these control
 * points", which is the direction we actually need.
 */
function interpolationOperator(parameters is array, knots is array, degree is number) returns map
{
    const count = size(parameters);
    for (var index = 1; index < count; index += 1)
    {
        if (parameters[index] - parameters[index - 1] <= KNOT_PARAMETER_TOLERANCE)
        {
            throw "splineRefinementUtils: interpolation parameters must strictly increase; entries " ~ (index - 1) ~
                " and " ~ index ~ " are equal to within tolerance, which makes the interpolation system singular. " ~
                "Two data points are coincident or nearly so.";
        }
    }

    var forwardRows = makeArray(count, 0);
    for (var k = 0; k < count; k += 1)
    {
        var row = makeArray(count, 0);
        const spanIndex = findEvaluationSpanIndex(knots, degree, parameters[k]);
        const basisValues = bSplineBasisValues(knots, degree, spanIndex, parameters[k]);
        for (var i = 0; i <= degree; i += 1)
        {
            row[spanIndex - degree + i] = basisValues[i];
        }
        forwardRows[k] = row;
    }

    return { "inverseRows" : inverse(matrix(forwardRows)), "knots" : knots, "degree" : degree, "count" : count };
}

/** Apply an interpolation operator: control points = inverse(N) * data points. Works on any
    addable/scalable point type, so length vectors of any dimension ride through unchanged. */
function applyInterpolationOperator(interpolationOp is map, points is array) returns array
{
    const count = interpolationOp.count;
    var controlPoints = makeArray(count, 0 * points[0]);
    for (var i = 0; i < count; i += 1)
    {
        var accumulated = 0 * points[0];
        for (var k = 0; k < count; k += 1)
        {
            accumulated = accumulated + interpolationOp.inverseRows[i][k] * points[k];
        }
        controlPoints[i] = accumulated;
    }
    return controlPoints;
}

/**
 * The B-spline curve of the given degree passing exactly through `points`, in order.
 * NURBS Book Algorithm A9.1. Returns the spline plus the `parameters` at which each input point
 * sits on it — the caller needs those to verify or to sample where the data was.
 */
export function interpolateBSplineCurveThroughPoints(points is array, degree is number) returns map
{
    return interpolateCurveCore(points, degree, chordLengthParameters(points));
}

/**
 * Same, at PRESCRIBED parameters — the caller says where along the curve each point must sit
 * rather than accepting the chord-length default. The lofting and exact-reconstruction cases need
 * this: reproducing an existing spline requires interpolating at ITS parameters, not at ones
 * invented from the data's spacing.
 */
export function interpolateBSplineCurveThroughPoints(points is array, degree is number, parameters is array) returns map
{
    if (size(parameters) != size(points))
    {
        throw "splineRefinementUtils: " ~ size(points) ~ " points but " ~ size(parameters) ~
            " prescribed parameters - each point needs exactly one.";
    }
    return interpolateCurveCore(points, degree, parameters);
}

function interpolateCurveCore(points is array, degree is number, parameters is array) returns map
{
    const knots = averagedInterpolationKnots(parameters, degree);
    const interpolationOp = interpolationOperator(parameters, knots, degree);
    return {
            "degree" : degree,
            "isPeriodic" : false,
            "isRational" : false,
            "controlPoints" : applyInterpolationOperator(interpolationOp, points),
            "knots" : knotArray(knots),
            "parameters" : parameters
        };
}

/**
 * The tensor-product B-spline surface passing exactly through a rectangular grid of points.
 * NURBS Book Algorithm A9.4.
 *
 * SEPARABLE, which is the point: interpolate every COLUMN in u, then interpolate the resulting
 * coefficient rows in v. Two operators, each inverted once, applied across the grid — rather than
 * one (rows*columns) square system, which at a 40x40 grid would be a 1600x1600 inverse instead of
 * two 40x40 ones.
 *
 * `grid[i][j]` is indexed u by i and v by j. Returns the surface plus the `uParameters` and
 * `vParameters` the data landed on. The result is non-rational: interpolation through points is a
 * polynomial construction, and there is nothing to make rational.
 */
export function interpolateBSplineSurfaceThroughGrid(grid is array, uDegree is number, vDegree is number) returns map
{
    const rowCount = size(grid);
    const columnCount = size(grid[0]);

    // Each column has its own chord-length parameterization; the direction gets their average, so
    // one shared parameter list serves every column (A9.4). Same across rows for v.
    var uParameterSets = makeArray(columnCount, 0);
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        var column = makeArray(rowCount, grid[0][0]);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            column[rowIndex] = grid[rowIndex][columnIndex];
        }
        uParameterSets[columnIndex] = chordLengthParameters(column);
    }
    var vParameterSets = makeArray(rowCount, 0);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        vParameterSets[rowIndex] = chordLengthParameters(grid[rowIndex]);
    }

    const uParameters = averagedParameters(uParameterSets);
    const vParameters = averagedParameters(vParameterSets);
    const uKnots = averagedInterpolationKnots(uParameters, uDegree);
    const vKnots = averagedInterpolationKnots(vParameters, vDegree);
    const uOperator = interpolationOperator(uParameters, uKnots, uDegree);
    const vOperator = interpolationOperator(vParameters, vKnots, vDegree);

    // Stage 1 — down every column, in u.
    var intermediate = makeArray(rowCount, 0);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        intermediate[rowIndex] = makeArray(columnCount, grid[0][0]);
    }
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        var column = makeArray(rowCount, grid[0][0]);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            column[rowIndex] = grid[rowIndex][columnIndex];
        }
        const interpolatedColumn = applyInterpolationOperator(uOperator, column);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            intermediate[rowIndex][columnIndex] = interpolatedColumn[rowIndex];
        }
    }

    // Stage 2 — across every row of stage 1's coefficients, in v.
    var controlPoints = makeArray(rowCount, 0);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        controlPoints[rowIndex] = applyInterpolationOperator(vOperator, intermediate[rowIndex]);
    }

    return {
            "uDegree" : uDegree,
            "vDegree" : vDegree,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : knotArray(uKnots),
            "vKnots" : knotArray(vKnots),
            "uParameters" : uParameters,
            "vParameters" : vParameters
        };
}

// ============================================================================================
// Layer 3 — ARC-LENGTH REPARAMETERIZATION. Where to PUT a knot, and how long a piece's parameter
// domain should be.
//
// WHY MEASURING ARC LENGTH NUMERICALLY IS LEGITIMATE HERE, given how much of this module exists
// to avoid sampling: knot placement is a pure HEURISTIC. Insertion is exact wherever you insert,
// so a badly chosen parameter costs distribution, never accuracy. That is categorically different
// from sampling which determines geometry, and it is why quadrature is allowed in this section
// and nowhere else.
//
// WHY IT IS NEEDED (both found live 2026-08-09): parameter width is not arc length.
//   - A rational circle's Bezier-arc parameterization has strongly varying speed, so splitting
//     the widest span BY PARAMETER piles control points onto one side of a cylinder instead of
//     spreading them around it.
//   - Concatenation translates each piece's domain without rescaling, so a long wall and a small
//     fillet keep parameter lengths unrelated to their physical size, and every later
//     parameter-driven decision inherits that distortion.
// ============================================================================================

/**
 * A monotone parameter -> arc length table for one direction, averaged over representative
 * isocurves of the other direction so a single unusual station cannot skew it.
 *
 * Chord-sum quadrature on a dense uniform sample. It underestimates true arc length slightly, and
 * that is irrelevant: every use compares or bisects these numbers, so a consistent scale factor
 * cancels out.
 */
/** The distinct knot values bounding a direction's spans: domain start, every distinct interior
    knot, domain end. For a periodic direction the domain end is the wrap image of the start, so
    the wrap span appears here like any other. */
function directionBreakValues(knots is array, degree is number) returns array
{
    const domain = knotDomain(knots, degree);
    var breaks = [domain.start];
    for (var knotIndex = 0; knotIndex < size(knots); knotIndex += 1)
    {
        const value = knots[knotIndex];
        if (value > breaks[size(breaks) - 1] + KNOT_PARAMETER_TOLERANCE &&
            value < domain.end - KNOT_PARAMETER_TOLERANCE)
        {
            breaks = append(breaks, value);
        }
    }
    return append(breaks, domain.end);
}

/**
 * The stations' ISOPARAMETRIC CURVES, collapsed once, as weighted control points plus their weight
 * sums along the varying direction.
 *
 * The profile below walks hundreds of parameters along ONE direction while holding three parameters
 * fixed in the other, and the fixed direction's basis values do not depend on where along the
 * varying direction it is. Collapsing the net against those fixed basis values once turns every
 * later sample from a (p+1)(q+1) double sum over the control grid into a (p+1) sum over a curve —
 * which is what an isocurve IS. It also lets the varying direction's span search and basis values be
 * computed once per sample and shared by all three stations, instead of recomputed per station.
 *
 * Exactly extractIsoparametricCurve's construction, kept private and inlined here because that
 * function normalizes and rebuilds a homogeneous grid on every call, and this needs three curves off
 * one already-normalized surface.
 *
 * @param surface {map} : normalized, so `weights` is present whenever `isRational`
 * @param isUDirection {boolean} : which direction VARIES; the stations are in the other one
 * @param stations {array} : fixed-direction parameters
 * @returns {array} : one `{ weightedPoints, weights }` per station, indexed along the varying direction
 */
function directionStationIsocurves(surface is map, isUDirection is boolean, stations is array) returns array
{
    const fixedDegree = isUDirection ? surface.vDegree : surface.uDegree;
    const fixedKnots = isUDirection ? surface.vKnots : surface.uKnots;
    const varyingCount = isUDirection ? size(surface.controlPoints) : size(surface.controlPoints[0]);
    const isRational = surface.isRational == true && surface.weights != undefined;
    const zeroVector = 0 * surface.controlPoints[0][0];

    var isocurves = makeArray(size(stations), 0);
    for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
    {
        const station = stations[stationIndex];
        const spanIndex = findEvaluationSpanIndex(fixedKnots, fixedDegree, station);
        const basisValues = bSplineBasisValues(fixedKnots, fixedDegree, spanIndex, station);
        const firstFixedIndex = spanIndex - fixedDegree;

        var weightedPoints = makeArray(varyingCount, zeroVector);
        var weightSums = makeArray(varyingCount, 0);
        for (var varyingIndex = 0; varyingIndex < varyingCount; varyingIndex += 1)
        {
            var pointSum = zeroVector;
            var weightSum = 0;
            for (var basisIndex = 0; basisIndex <= fixedDegree; basisIndex += 1)
            {
                const rowIndex = isUDirection ? varyingIndex : firstFixedIndex + basisIndex;
                const columnIndex = isUDirection ? firstFixedIndex + basisIndex : varyingIndex;
                var blendValue = basisValues[basisIndex];
                if (isRational)
                {
                    blendValue = blendValue * surface.weights[rowIndex][columnIndex];
                    weightSum += blendValue;
                }
                pointSum = pointSum + blendValue * surface.controlPoints[rowIndex][columnIndex];
            }
            weightedPoints[varyingIndex] = pointSum;
            weightSums[varyingIndex] = weightSum;
        }
        isocurves[stationIndex] = { "weightedPoints" : weightedPoints, "weights" : weightSums };
    }
    return isocurves;
}

/** One point on a collapsed station isocurve, given the varying direction's already-computed span
    start and basis values. Rational handling mirrors evaluateBSplineSurfacePoint exactly: the weight
    sum is accumulated and divided out only when the surface is rational. */
function stationIsocurvePoint(isocurve is map, firstIndex is number, basisValues is array, degree is number,
    isRational is boolean) returns Vector
{
    var pointSum = 0 * isocurve.weightedPoints[0];
    var weightSum = 0;
    for (var basisIndex = 0; basisIndex <= degree; basisIndex += 1)
    {
        const index = firstIndex + basisIndex;
        pointSum = pointSum + basisValues[basisIndex] * isocurve.weightedPoints[index];
        if (isRational)
        {
            weightSum += basisValues[basisIndex] * isocurve.weights[index];
        }
    }
    return isRational ? pointSum / weightSum : pointSum;
}

function directionArcLengthProfile(surface is map, isUDirection is boolean) returns map
{
    const degree = isUDirection ? surface.uDegree : surface.vDegree;
    const knots = isUDirection ? surface.uKnots : surface.vKnots;
    const domain = knotDomain(knots, degree);

    const otherDegree = isUDirection ? surface.vDegree : surface.uDegree;
    const otherDomain = knotDomain(isUDirection ? surface.vKnots : surface.uKnots, otherDegree);
    const otherSpan = otherDomain.end - otherDomain.start;
    const stations = [otherDomain.start + 0.25 * otherSpan, otherDomain.start + 0.5 * otherSpan,
            otherDomain.start + 0.75 * otherSpan];

    // Sample PER EXISTING SPAN, not uniformly across the domain. Uniform sampling gives a span
    // occupying one percent of the parameter range one or two samples, so both its arc length and
    // any parameter located inside it become guesswork — and variable knot density is precisely
    // where that misleads. Per-span sampling resolves every span equally regardless of width, at
    // the cost of a non-uniform grid the lookups below have to search rather than index.
    const breaks = directionBreakValues(knots, degree);
    const samplesPerSpan = 24;
    var parameters = makeArray((size(breaks) - 1) * samplesPerSpan + 1, domain.start);
    var writeIndex = 0;
    for (var spanIndex = 0; spanIndex < size(breaks) - 1; spanIndex += 1)
    {
        for (var step = 0; step < samplesPerSpan; step += 1)
        {
            parameters[writeIndex] = breaks[spanIndex] +
                (breaks[spanIndex + 1] - breaks[spanIndex]) * step / samplesPerSpan;
            writeIndex += 1;
        }
    }
    parameters[writeIndex] = domain.end;

    // THE ONE PLACE IN THIS PASS WHERE THE ARITHMETIC IS REGROUPED, so it is called out rather than
    // buried. Collapsing the fixed direction first computes sum_i uB[i] * (sum_j vB[j] w_ij P_ij)
    // where the direct evaluator computed sum_i sum_j (uB[i] vB[j] w_ij) P_ij. Mathematically the
    // same sum; in floating point the groupings round differently, in the last bit or two.
    //
    // That is acceptable HERE and would not be elsewhere in this module, for the reason the section
    // header gives: knot placement is a HEURISTIC. Insertion is exact wherever it lands, so a
    // parameter that moves by 1e-15 changes the distribution by 1e-15 and the geometry by nothing.
    // Nothing downstream is a knife edge either — the allocator's tie test carries
    // ALLOCATION_TIE_TOLERANCE at 1e-9, nine orders above the perturbation, and every cut is
    // re-checked against its own span bounds before use.
    const isRational = surface.isRational == true && surface.weights != undefined;
    const isocurves = directionStationIsocurves(surface, isUDirection, stations);

    const sampleCount = size(parameters);
    var cumulative = makeArray(sampleCount, 0 * meter);
    var previousPoints = makeArray(size(stations), WORLD_ORIGIN);

    const firstSpanIndex = findEvaluationSpanIndex(knots, degree, parameters[0]);
    const firstBasisValues = bSplineBasisValues(knots, degree, firstSpanIndex, parameters[0]);
    for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
    {
        previousPoints[stationIndex] = stationIsocurvePoint(isocurves[stationIndex],
            firstSpanIndex - degree, firstBasisValues, degree, isRational);
    }

    for (var index = 1; index < sampleCount; index += 1)
    {
        // Computed once and shared by all three stations — they differ only in which isocurve they
        // read, never in where along it they sit.
        const spanIndex = findEvaluationSpanIndex(knots, degree, parameters[index]);
        const basisValues = bSplineBasisValues(knots, degree, spanIndex, parameters[index]);
        const firstIndex = spanIndex - degree;

        var stepLength = 0 * meter;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            const point = stationIsocurvePoint(isocurves[stationIndex], firstIndex, basisValues, degree, isRational);
            stepLength += norm(point - previousPoints[stationIndex]);
            previousPoints[stationIndex] = point;
        }
        cumulative[index] = cumulative[index - 1] + stepLength / size(stations);
    }
    return { "parameters" : parameters, "cumulative" : cumulative, "domain" : domain };
}

/** Arc length at an arbitrary parameter, by binary search and linear interpolation. Binary search
    rather than indexing because the profile grid is per-span and therefore non-uniform. */
function arcLengthAt(profile is map, parameter is number) returns ValueWithUnits
{
    const sampleCount = size(profile.parameters);
    if (parameter <= profile.parameters[0])
    {
        return profile.cumulative[0];
    }
    if (parameter >= profile.parameters[sampleCount - 1])
    {
        return profile.cumulative[sampleCount - 1];
    }
    var low = 0;
    var high = sampleCount - 1;
    while (high - low > 1)
    {
        const middle = floor((low + high) / 2);
        if (profile.parameters[middle] <= parameter)
        {
            low = middle;
        }
        else
        {
            high = middle;
        }
    }
    const spanWidth = profile.parameters[high] - profile.parameters[low];
    const fraction = spanWidth <= 0 ? 0 : (parameter - profile.parameters[low]) / spanWidth;
    return profile.cumulative[low] + fraction * (profile.cumulative[high] - profile.cumulative[low]);
}

/** The parameter at a given arc length, by the same interpolation run backwards — the step that
    turns "cut this span into equal pieces" from a parameter statement into a geometric one. */
function parameterAtArcLength(profile is map, targetLength is ValueWithUnits) returns number
{
    const sampleCount = size(profile.parameters);
    for (var index = 1; index < sampleCount; index += 1)
    {
        if (profile.cumulative[index] >= targetLength)
        {
            const spanLength = profile.cumulative[index] - profile.cumulative[index - 1];
            const fraction = spanLength <= 0 * meter ? 0.5
                : (targetLength - profile.cumulative[index - 1]) / spanLength;
            return profile.parameters[index - 1] +
                fraction * (profile.parameters[index] - profile.parameters[index - 1]);
        }
    }
    return profile.parameters[sampleCount - 1];
}

/**
 * Relative width within which two spans count as THE SAME LENGTH for allocation. Exact equality is
 * the wrong test: mirror-image spans of a symmetric shape are equal in exact arithmetic and differ
 * in the last bit or two once their lengths have been through chord-sum quadrature, and a tie
 * detector that misses by one ulp reintroduces exactly the bias it exists to remove.
 */
const ALLOCATION_TIE_TOLERANCE = 1e-9;

/**
 * `count` members of a tie group of `groupSize`, as indices into that group, SPREAD across it
 * rather than clustered at its front.
 *
 * Used where a tie group cannot be served whole and something has to give. There is by definition no
 * geometric basis for choosing between tied members — that is what makes them tied — so the only
 * honest goal left is to stop the leftover reading as a DIRECTION. Serving `tiedSpans` front-to-back,
 * which is what the allocator used to do, hands the remainder to a contiguous run of the lowest
 * indices: a twelve-span net with eleven leftover insertions put every one of them in the first
 * eleven spans, which is a solid block down one side and is exactly the "it fills the left first"
 * report. The same remainder spread through the group is invisible.
 *
 * Half-open midpoint sampling, so a single leftover lands in the MIDDLE of the group rather than at
 * either end, and `count == groupSize` returns every member in order.
 */
function spreadTieGroupSelection(groupSize is number, count is number) returns array
{
    var selected = makeArray(count, 0);
    for (var index = 0; index < count; index += 1)
    {
        selected[index] = floor((index + 0.5) * groupSize / count);
    }
    return selected;
}

/**
 * Choose up to `numToInsert` parameters for a surface direction so the resulting spans come out as
 * EQUAL IN ARC LENGTH as insertion allows.
 *
 * ALLOCATE, THEN SUBDIVIDE — not repeated bisection. The predecessor split whichever span was
 * longest at its arc-length midpoint, once per insertion. That is arc-length aware and still
 * structurally coarse, because a span can only ever be halved, quartered, eighthed: spreading 21
 * insertions over four equal spans yields sixths and quarters, and the perimeter carries a
 * TWO-TO-ONE spacing variation whatever the target count is. Greedy halving bounds the ratio at
 * two and, for most targets, achieves it. That two-to-one is what a cylinder refined to a round
 * number of control points looks like, and it is why "the knots go kind of wherever" survived the
 * first arc-length pass — the placement was measuring the right quantity and then quantizing it.
 *
 * So: first decide how many PIECES each existing span should end up cut into — greedily, giving
 * each insertion to whichever span currently has the longest piece, which is the allocation that
 * minimizes the longest piece overall — then cut each span into that many EQUAL-ARC-LENGTH pieces
 * in one go. Four equal spans and 21 insertions become 6/6/6/7 pieces, a spacing ratio of 7:6.
 *
 * WHAT THIS CANNOT FIX, because it is not placement: existing knots are never removed, so a
 * direction arriving with one span far shorter than the ideal piece keeps it, and the ratio that
 * forces is a property of the input. Nor does even knot spacing imply evenly spaced CONTROL POINTS
 * — a control point sits at its Greville abscissa, the average of `degree` consecutive knots, so at
 * a knot of multiplicity == degree one control point lands exactly on the knot and its neighbours
 * crowd in at a fraction of the surrounding spacing. An exact rational circle REQUIRES those
 * multiple knots (its homogeneous curve genuinely corners there), so a cylinder's net keeps a tight
 * pair or triple at each arc joint no matter how the knots between them are placed.
 *
 * BALANCE, and the bias `balancedOnly` removes. "Give the insertion to the longest current piece"
 * with ties broken by array order picks the LOWER INDEX every single time. On a symmetric shape
 * every span ties with its mirror, so every allocation that cannot divide evenly lands on the same
 * side, for the same reason, at every count — not a parity accident but a systematic directional
 * lean, and what put knots preferentially on one side of a merged fillet strip. So the allocation
 * serves a WHOLE TIE GROUP at a time, which makes it invariant under any relabelling of equally-long
 * spans, the mirror relabelling included. `balancedOnly` then says what to do when the remaining
 * budget cannot cover a whole group: stop, returning FEWER than `numToInsert`, rather than pick a
 * side. Callers that need the count leave it false; callers feeding a LOSSY step set it, because
 * knot removal reads the spacing on both sides of the knot it takes out and an uneven net heals
 * lopsidedly — which lands in the geometry, where an uneven set of handles does not.
 *
 * When the PLAIN overload splits a group — which it must, since its contract is the exact count —
 * the spans served are SPREAD across the group rather than taken off its front, per
 * spreadTieGroupSelection. Front-loading is what turned an unavoidable remainder into a visible block
 * of extra knots down one side, and it is the placement half of the "it fills the left first"
 * report. It changes only WHICH tied spans take the remainder, never how many pieces exist, so every
 * spacing-ratio property is untouched; and insertion is exact, so it cannot move the surface either
 * way. This is cosmetic in the strict sense and is worth doing for exactly that reason — a defect
 * that is only in the handles should be fixed where it lives, not by making the geometry pay.
 *
 * Works uniformly for clamped and periodic directions: the break list is the distinct knot values
 * inside the domain plus the domain end, which for a periodic direction is the wrap image of the
 * start, so the wrap span participates like any other. Every returned parameter is strictly
 * interior to a span, so it can never collide with an existing knot and raise a multiplicity.
 */
function arcLengthSpanInsertions(surface is map, isUDirection is boolean, numToInsert is number, balancedOnly is boolean) returns array
{
    if (numToInsert <= 0)
    {
        return [];
    }

    const degree = isUDirection ? surface.uDegree : surface.vDegree;
    const knots = isUDirection ? surface.uKnots : surface.vKnots;
    const profile = directionArcLengthProfile(surface, isUDirection);

    const breaks = directionBreakValues(knots, degree);
    const spanCount = size(breaks) - 1;
    var breakLengths = makeArray(size(breaks), 0 * meter);
    for (var index = 0; index < size(breaks); index += 1)
    {
        breakLengths[index] = arcLengthAt(profile, breaks[index]);
    }

    // A direction the profile measures as having no length at all — a fully degenerate patch —
    // carries no arc-length information to allocate by, so it falls back to parameter width. That
    // is the one case where the two measures cannot disagree, since there is nothing to disagree
    // about, and it keeps the greedy below from handing every insertion to span zero.
    const measuredTotal = breakLengths[spanCount] - breakLengths[0];
    var spanLengths = makeArray(spanCount, 0 * meter);
    for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
    {
        spanLengths[spanIndex] = measuredTotal > 0 * meter
            ? breakLengths[spanIndex + 1] - breakLengths[spanIndex]
            : (breaks[spanIndex + 1] - breaks[spanIndex]) * meter;
    }

    // Allocate a piece count per span, a WHOLE TIE GROUP at a time. Serving the group together is
    // what makes the allocation blind to array order among equally-long spans; serving it one span
    // at a time gives identical results whenever the group is served completely, and silently
    // favours the lowest index whenever it is not.
    var pieces = makeArray(spanCount, 1);
    var allocated = 0;
    while (allocated < numToInsert)
    {
        var longestPiece = -1 * meter;
        for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
        {
            longestPiece = max(longestPiece, spanLengths[spanIndex] / pieces[spanIndex]);
        }

        var tiedSpans = [];
        for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
        {
            if (spanLengths[spanIndex] / pieces[spanIndex] >= longestPiece * (1 - ALLOCATION_TIE_TOLERANCE))
            {
                tiedSpans = append(tiedSpans, spanIndex);
            }
        }

        const remainingBudget = numToInsert - allocated;
        if (size(tiedSpans) <= remainingBudget)
        {
            for (var tiedSpan in tiedSpans)
            {
                pieces[tiedSpan] += 1;
                allocated += 1;
            }
            continue;
        }

        // The group cannot be served whole, so `balancedOnly` stops rather than pick a side — even
        // when that means returning NOTHING, which happens whenever the budget is smaller than the
        // very first tie group.
        //
        // Returning nothing looks like it should be wrong, and a floor that served the first group
        // anyway (split, or rounded up past the target) was written and then reverted on measurement.
        // On the merged cube corner the three policies differ in exactly one configuration — a budget
        // one control point above the concatenated count — and there serving nothing is the only one
        // that stays mirror-symmetric: 0.000 mirror error against 0.033 for a split group and 0.066
        // for a rounded-up one. The extra handles do buy accuracy (0.33 -> 0.24 -> 0.15 deviation
        // from the true corner), but they buy it by overshooting the post-heal target and handing the
        // surplus to the lossy reduce, which then takes one knot off a tied pair. Trading the
        // symmetry this flag exists to protect for a sharper corner is not this flag's call to make;
        // a caller who wants the sharper corner has a budget control and can raise it.
        if (balancedOnly)
        {
            break;
        }

        // Split the group by spreading rather than by taking a run off its front — see
        // spreadTieGroupSelection. This is the last decision in the allocator that array order used
        // to make, and the one the "it fills the left side first" report was actually about.
        const spread = spreadTieGroupSelection(size(tiedSpans), remainingBudget);
        for (var selected in spread)
        {
            pieces[tiedSpans[selected]] += 1;
            allocated += 1;
        }
    }

    var insertions = makeArray(allocated, 0);
    var writeIndex = 0;
    for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
    {
        const cutCount = pieces[spanIndex] - 1;
        if (cutCount < 1)
        {
            continue;
        }

        // Locate every cut by arc length, then accept the set only if it is strictly increasing
        // and strictly inside the span. Checking the SET rather than each cut on its own matters:
        // a profile too coarse to separate two nearby cuts would otherwise hand back a duplicate
        // parameter, and a duplicate raises a multiplicity instead of adding a span — fatal at
        // degree 1, where any multiplicity above one is illegal.
        var spanCuts = makeArray(cutCount, 0);
        var usable = true;
        var previous = breaks[spanIndex];
        for (var cutIndex = 1; cutIndex <= cutCount; cutIndex += 1)
        {
            const targetLength = breakLengths[spanIndex] + spanLengths[spanIndex] * cutIndex / pieces[spanIndex];
            const parameter = parameterAtArcLength(profile, targetLength);
            if (parameter <= previous + KNOT_PARAMETER_TOLERANCE ||
                parameter >= breaks[spanIndex + 1] - KNOT_PARAMETER_TOLERANCE)
            {
                usable = false;
            }
            spanCuts[cutIndex - 1] = parameter;
            previous = parameter;
        }
        if (!usable)
        {
            for (var cutIndex = 1; cutIndex <= cutCount; cutIndex += 1)
            {
                spanCuts[cutIndex - 1] = breaks[spanIndex] +
                    (breaks[spanIndex + 1] - breaks[spanIndex]) * cutIndex / pieces[spanIndex];
            }
        }

        for (var cut in spanCuts)
        {
            insertions[writeIndex] = cut;
            writeIndex += 1;
        }
    }
    return insertions;
}

/**
 * Affinely rescale one direction's parameter domain to [newStart, newEnd]. EXACT — an affine
 * parameter change moves no geometry; only knot VALUES change, never control points or weights.
 *
 * The assembly counterpart of arc-length placement: before concatenating a strip, give each piece
 * a domain length proportional to its physical extent, so the merged surface's parameterization
 * reflects the shape rather than whatever domains its pieces happened to arrive with.
 */
export function rescaleSurfaceDirectionDomain(surface is map, isUDirection is boolean, newStart is number, newEnd is number) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    const degree = isUDirection ? normalized.uDegree : normalized.vDegree;
    const knots = isUDirection ? normalized.uKnots : normalized.vKnots;
    const domain = knotDomain(knots, degree);
    const currentSpan = domain.end - domain.start;
    if (currentSpan <= 0 || newEnd - newStart <= 0)
    {
        throw "splineRefinementUtils: rescaleSurfaceDirectionDomain needs positive domains - got [" ~ domain.start ~
            ", " ~ domain.end ~ "] mapping to [" ~ newStart ~ ", " ~ newEnd ~ "].";
    }

    const scale = (newEnd - newStart) / currentSpan;
    var rescaled = makeArray(size(knots), 0);
    for (var knotIndex = 0; knotIndex < size(knots); knotIndex += 1)
    {
        rescaled[knotIndex] = newStart + (knots[knotIndex] - domain.start) * scale;
    }
    if (isUDirection)
    {
        normalized.uKnots = knotArray(rescaled);
    }
    else
    {
        normalized.vKnots = knotArray(rescaled);
    }
    return normalized;
}

/**
 * The approximate arc length of one direction, averaged over representative isocurves — what a
 * caller needs to size that direction's domain proportionally before assembly.
 */
export function approximateDirectionArcLength(surface is map, isUDirection is boolean) returns ValueWithUnits
{
    const profile = directionArcLengthProfile(normalizeSurfaceDefinition(surface), isUDirection);
    return profile.cumulative[size(profile.cumulative) - 1];
}

// ============================================================================================
// Layer 3 — ASSEMBLY: exact isocurve extraction, transposition, concatenation, targeted knot
// removal, and lofting. Built 2026-08-09 as the foundation of the multi-face merge
// (EDIT_SURFACE_SPEC section 5A) after the projection-and-raycast approach failed structurally —
// its coverage precondition (the selection's projected outline must BE a rectangle) is
// unsatisfiable for almost any real selection, a single circular face included.
//
// The principle these share: the faces being merged already ARE B-splines with exactly known
// seams, so a merge should ASSEMBLE their definitions and then remove the seam knots with
// measured deviation — not rediscover everything from sampled points. Concatenation produces the
// C0 composite exactly (seam knots at multiplicity == degree, kinks preserved); the knot-removal
// machinery from the simplification layer then IS the smoothing, and its deviation IS the price
// of the smoothing. One consequence worth stating because it collapses a spec section: pieces of
// the SAME underlying surface concatenate and seam-remove at zero deviation, recovering the
// original surface — so "exact merge of a split face" needs no special path, it is just this
// pipeline reporting 0.
// ============================================================================================

/**
 * The isoparametric curve of a surface at a fixed parameter: fix u, get the B-spline curve the
 * surface traces in v (or the reverse). EXACT — an isocurve's control points are the one-direction
 * de Boor combination of the net, Q_j = sum_i N_i(u0) * P_ij, computed in homogeneous coordinates
 * so rational surfaces ride through with their weights intact.
 *
 * The curve inherits the free direction's knots, degree and periodicity verbatim, so an isocurve
 * of a closed direction is a genuinely closed curve in the module's own canonical form.
 */
export function extractIsoparametricCurve(surface is map, isUFixed is boolean, parameter is number) returns map
{
    const normalized = normalizeSurfaceDefinition(surface);
    const grid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    const fixedDegree = isUFixed ? normalized.uDegree : normalized.vDegree;
    const fixedKnots = isUFixed ? normalized.uKnots : normalized.vKnots;
    const spanIndex = findEvaluationSpanIndex(fixedKnots, fixedDegree, parameter);
    const basisValues = bSplineBasisValues(fixedKnots, fixedDegree, spanIndex, parameter);
    const firstIndex = spanIndex - fixedDegree;

    const resultCount = isUFixed ? size(grid[0]) : size(grid);
    var homogeneousPoints = makeArray(resultCount, 0 * grid[0][0]);
    for (var resultIndex = 0; resultIndex < resultCount; resultIndex += 1)
    {
        var accumulated = 0 * grid[0][0];
        for (var basisIndex = 0; basisIndex <= fixedDegree; basisIndex += 1)
        {
            const source = isUFixed ? grid[firstIndex + basisIndex][resultIndex]
                : grid[resultIndex][firstIndex + basisIndex];
            accumulated = accumulated + basisValues[basisIndex] * source;
        }
        homogeneousPoints[resultIndex] = accumulated;
    }

    const separated = separatePointsAndWeights(homogeneousPoints);
    return {
            "degree" : isUFixed ? normalized.vDegree : normalized.uDegree,
            "isPeriodic" : isUFixed ? normalized.isVPeriodic == true : normalized.isUPeriodic == true,
            "isRational" : true,
            "controlPoints" : separated.points,
            "weights" : separated.weights,
            "knots" : knotArray(isUFixed ? normalized.vKnots : normalized.uKnots)
        };
}

/**
 * Swap a surface's two directions: S'(v, u) = S(u, v). Exact bookkeeping — the control net is
 * transposed and every u field trades places with its v counterpart. Exists so direction-specific
 * algorithms can be written once for one direction and applied to the other by conjugation, and so
 * a merge caller can align pieces whose chain direction is v.
 */
export function transposeSurface(surface is map) returns map
{
    var result = normalizeSurfaceDefinition(surface);
    const rowCount = size(result.controlPoints);
    const columnCount = size(result.controlPoints[0]);

    var transposedPoints = makeArray(columnCount, 0);
    var transposedWeights = makeArray(columnCount, 0);
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        var pointRow = makeArray(rowCount, result.controlPoints[0][0]);
        var weightRow = makeArray(rowCount, 1);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            pointRow[rowIndex] = result.controlPoints[rowIndex][columnIndex];
            weightRow[rowIndex] = result.weights[rowIndex][columnIndex];
        }
        transposedPoints[columnIndex] = pointRow;
        transposedWeights[columnIndex] = weightRow;
    }

    const swappedDegree = result.uDegree;
    const swappedKnots = result.uKnots;
    const swappedPeriodic = result.isUPeriodic == true;
    result.uDegree = result.vDegree;
    result.uKnots = result.vKnots;
    result.isUPeriodic = result.isVPeriodic == true;
    result.vDegree = swappedDegree;
    result.vKnots = swappedKnots;
    result.isVPeriodic = swappedPeriodic;
    result.controlPoints = transposedPoints;
    result.weights = transposedWeights;
    return result;
}

/**
 * Join a sequence of curves end-to-start into ONE B-spline, exactly: each piece keeps its own
 * parameterization (domains are translated to abut, never rescaled), and each junction becomes a
 * knot of multiplicity == degree — a C0 joint that PRESERVES the kink. This is deliberate: the
 * composite is the honest representation of the chain, and smoothing a junction is a separate,
 * measured act (removeSplineKnot at the seam), not something assembly does behind the caller's
 * back.
 *
 * Pieces must be ordered and oriented so each one ends where the next begins, within
 * `joinTolerance`; the two coincident endpoint control points collapse to their average, and the
 * worst gap so absorbed is returned as `joinDeviation`. Rational pieces are reconciled by a global
 * projective rescale of each piece's weights (multiplying every homogeneous point of a piece by
 * one constant changes nothing about its geometry or parameterization), which can always match the
 * single shared endpoint weight.
 *
 * Returns the curve plus `seamParameters` — the junction parameters, exactly what targeted knot
 * removal needs next.
 */
export function concatenateBSplineCurves(curves is array, joinTolerance is ValueWithUnits) returns map
{
    if (size(curves) == 0)
    {
        throw "splineRefinementUtils: concatenateBSplineCurves needs at least one curve.";
    }

    var pieces = makeArray(size(curves), 0);
    var targetDegree = 0;
    for (var index = 0; index < size(curves); index += 1)
    {
        const normalized = normalizeSplineDefinition(curves[index]);
        if (normalized.isPeriodic == true)
        {
            throw "splineRefinementUtils: piece " ~ index ~ " is a closed curve - a closed piece has no free " ~
                "endpoints to chain through, so it cannot participate in a concatenation.";
        }
        pieces[index] = normalized;
        targetDegree = max(targetDegree, normalized.degree);
    }
    for (var index = 0; index < size(pieces); index += 1)
    {
        if (pieces[index].degree < targetDegree)
        {
            pieces[index] = elevateSplineDegree(pieces[index], targetDegree);
        }
    }

    if (size(pieces) == 1)
    {
        var single = pieces[0];
        single.seamParameters = [];
        single.joinDeviation = 0 * meter;
        return single;
    }

    const degree = targetDegree;
    var totalPointCount = 0;
    for (var piece in pieces)
    {
        totalPointCount += size(piece.controlPoints);
    }
    totalPointCount -= size(pieces) - 1;

    const firstHomogeneous = combinePointsAndWeights(pieces[0].controlPoints, pieces[0].weights);
    var assembledPoints = makeArray(totalPointCount, 0 * firstHomogeneous[0]);
    var assembledKnots = makeArray(totalPointCount + degree + 1, 0);
    var seamParameters = makeArray(size(pieces) - 1, 0);
    var joinDeviation = 0 * meter;

    for (var pointIndex = 0; pointIndex < size(firstHomogeneous); pointIndex += 1)
    {
        assembledPoints[pointIndex] = firstHomogeneous[pointIndex];
    }
    var pointWriteIndex = size(firstHomogeneous);

    // Piece 0's knots minus ONE trailing end-clamp value: the junction value ends at multiplicity
    // degree instead of degree + 1, which is exactly the C0 interior knot the composite needs there.
    var knotWriteIndex = 0;
    for (var knotIndex = 0; knotIndex < size(pieces[0].knots) - 1; knotIndex += 1)
    {
        assembledKnots[knotWriteIndex] = pieces[0].knots[knotIndex];
        knotWriteIndex += 1;
    }
    var domainEnd = knotDomain(pieces[0].knots, degree).end;

    for (var pieceIndex = 1; pieceIndex < size(pieces); pieceIndex += 1)
    {
        var pieceHomogeneous = combinePointsAndWeights(pieces[pieceIndex].controlPoints, pieces[pieceIndex].weights);

        // Weight continuity by global projective rescale — the only weight change that alters nothing.
        const previousEndWeight = assembledPoints[pointWriteIndex - 1][3];
        const rescale = previousEndWeight / pieceHomogeneous[0][3];
        for (var pointIndex = 0; pointIndex < size(pieceHomogeneous); pointIndex += 1)
        {
            pieceHomogeneous[pointIndex] = rescale * pieceHomogeneous[pointIndex];
        }

        const seamPair = separatePointsAndWeights([assembledPoints[pointWriteIndex - 1], pieceHomogeneous[0]]);
        const gap = norm(seamPair.points[0] - seamPair.points[1]);
        if (gap > joinTolerance)
        {
            throw "splineRefinementUtils: pieces " ~ (pieceIndex - 1) ~ " and " ~ pieceIndex ~ " do not meet - the " ~
                "gap between them is " ~ toString(gap) ~ ", over the join tolerance. Pieces must be ordered and " ~
                "oriented so each one ends where the next begins.";
        }
        joinDeviation = max(joinDeviation, gap);

        assembledPoints[pointWriteIndex - 1] = (assembledPoints[pointWriteIndex - 1] + pieceHomogeneous[0]) / 2;
        for (var pointIndex = 1; pointIndex < size(pieceHomogeneous); pointIndex += 1)
        {
            assembledPoints[pointWriteIndex] = pieceHomogeneous[pointIndex];
            pointWriteIndex += 1;
        }

        // Translate this piece's domain to start at the running end, keeping its parameterization.
        const pieceDomain = knotDomain(pieces[pieceIndex].knots, degree);
        const shift = domainEnd - pieceDomain.start;
        seamParameters[pieceIndex - 1] = domainEnd;

        const pieceKnots = pieces[pieceIndex].knots;
        for (var knotIndex = degree + 1; knotIndex < size(pieceKnots) - degree - 1; knotIndex += 1)
        {
            assembledKnots[knotWriteIndex] = pieceKnots[knotIndex] + shift;
            knotWriteIndex += 1;
        }
        domainEnd = pieceDomain.end + shift;
        const endMultiplicity = pieceIndex == size(pieces) - 1 ? degree + 1 : degree;
        for (var repeatIndex = 0; repeatIndex < endMultiplicity; repeatIndex += 1)
        {
            assembledKnots[knotWriteIndex] = domainEnd;
            knotWriteIndex += 1;
        }
    }

    if (pointWriteIndex != totalPointCount || knotWriteIndex != size(assembledKnots))
    {
        throw "splineRefinementUtils: internal error - concatenation produced " ~ pointWriteIndex ~ " points and " ~
            knotWriteIndex ~ " knots where " ~ totalPointCount ~ " and " ~ size(assembledKnots) ~ " were expected.";
    }

    const separated = separatePointsAndWeights(assembledPoints);
    return {
            "degree" : degree,
            "isPeriodic" : false,
            "isRational" : true,
            "controlPoints" : separated.points,
            "weights" : separated.weights,
            "knots" : knotArray(assembledKnots),
            "seamParameters" : seamParameters,
            "joinDeviation" : joinDeviation
        };
}

/**
 * The surface form: join a strip of surfaces along their shared edges into ONE B-spline surface,
 * exactly, with each seam at multiplicity == uDegree (C0, kink preserved — smoothing is
 * removeSurfaceKnotLine's separate, measured job). `isUDirection` names the CHAIN direction;
 * pieces must be ordered and oriented so each one's final row of control points coincides with the
 * next one's first row, within `joinTolerance`.
 *
 * The transverse direction is reconciled exactly before assembly: every piece is elevated to the
 * common degrees, each piece's transverse knots are affinely remapped to [0, 1], and all pieces
 * are refined onto the n-way merged transverse knot vector — insertion only, geometry unchanged.
 *
 * TWO NAMED LIMITS, both thrown rather than fudged:
 * - No direction may be periodic: a closed chain direction has no ends to chain, and a periodic
 *   transverse direction needs the periodic knot-sharing path this function does not yet drive.
 * - After the per-piece projective rescale, the two sides of each seam must agree in WEIGHTS
 *   column-for-column, not just in 3D position. If they do not, the faces parameterize their
 *   shared edge differently (a rational reparameterization gap), and joining them needs seam
 *   reparameterization machinery that does not exist yet. Silently averaging mismatched weights
 *   would build a surface whose seam column means two different things to its two sides.
 */
export function concatenateBSplineSurfaces(surfaces is array, isUDirection is boolean, joinTolerance is ValueWithUnits) returns map
{
    if (size(surfaces) == 0)
    {
        throw "splineRefinementUtils: concatenateBSplineSurfaces needs at least one surface.";
    }
    if (!isUDirection)
    {
        var transposed = makeArray(size(surfaces), 0);
        for (var index = 0; index < size(surfaces); index += 1)
        {
            transposed[index] = transposeSurface(surfaces[index]);
        }
        return transposeSurface(concatenateBSplineSurfaces(transposed, true, joinTolerance));
    }

    var pieces = makeArray(size(surfaces), 0);
    var targetUDegree = 0;
    var targetVDegree = 0;
    for (var index = 0; index < size(surfaces); index += 1)
    {
        const normalized = normalizeSurfaceDefinition(surfaces[index]);
        if (normalized.isUPeriodic == true)
        {
            throw "splineRefinementUtils: piece " ~ index ~ " is closed in the chain direction - it has no ends to " ~
                "chain through.";
        }
        if (normalized.isVPeriodic == true)
        {
            throw "splineRefinementUtils: piece " ~ index ~ " is periodic transverse to the chain. Concatenating " ~
                "closed-section strips needs the periodic knot-sharing path, which this function does not drive yet.";
        }
        pieces[index] = normalized;
        targetUDegree = max(targetUDegree, normalized.uDegree);
        targetVDegree = max(targetVDegree, normalized.vDegree);
    }
    for (var index = 0; index < size(pieces); index += 1)
    {
        if (pieces[index].uDegree < targetUDegree || pieces[index].vDegree < targetVDegree)
        {
            pieces[index] = elevateSurfaceDegrees(pieces[index], targetUDegree, targetVDegree);
        }
    }

    if (size(pieces) == 1)
    {
        var single = pieces[0];
        single.seamParameters = [];
        single.joinDeviation = 0 * meter;
        return single;
    }

    // Transverse compatibility: remap every piece's v-knots to [0, 1], merge them all, refine each
    // piece onto the merged vector. Exact — insertion only.
    var remappedVKnots = makeArray(size(pieces), 0);
    var mergedVKnots = undefined;
    for (var index = 0; index < size(pieces); index += 1)
    {
        remappedVKnots[index] = remapKnotsToUnitDomain(pieces[index].vKnots, targetVDegree);
        mergedVKnots = mergedVKnots == undefined ? remappedVKnots[index]
            : mergeKnotVectors(mergedVKnots, remappedVKnots[index], targetVDegree);
    }
    var pieceGrids = makeArray(size(pieces), 0);
    for (var index = 0; index < size(pieces); index += 1)
    {
        var homogeneousGrid = combineSurfaceControlPointsAndWeights(pieces[index].controlPoints, pieces[index].weights);
        const insertions = insertionsToReach(remappedVKnots[index], mergedVKnots, targetVDegree);
        if (size(insertions) > 0)
        {
            const vOperator = knotRefinementOperator(remappedVKnots[index], targetVDegree, insertions);
            homogeneousGrid = applyKnotRefinementOperatorAcrossRows(vOperator, homogeneousGrid);
        }
        pieceGrids[index] = homogeneousGrid;
    }
    const sharedColumnCount = size(pieceGrids[0][0]);

    var totalRowCount = 0;
    for (var grid in pieceGrids)
    {
        totalRowCount += size(grid);
    }
    totalRowCount -= size(pieces) - 1;

    var assembledGrid = makeArray(totalRowCount, 0);
    var assembledUKnots = makeArray(totalRowCount + targetUDegree + 1, 0);
    var seamParameters = makeArray(size(pieces) - 1, 0);
    var joinDeviation = 0 * meter;

    for (var rowIndex = 0; rowIndex < size(pieceGrids[0]); rowIndex += 1)
    {
        assembledGrid[rowIndex] = pieceGrids[0][rowIndex];
    }
    var rowWriteIndex = size(pieceGrids[0]);

    var knotWriteIndex = 0;
    for (var knotIndex = 0; knotIndex < size(pieces[0].uKnots) - 1; knotIndex += 1)
    {
        assembledUKnots[knotWriteIndex] = pieces[0].uKnots[knotIndex];
        knotWriteIndex += 1;
    }
    var domainEnd = knotDomain(pieces[0].uKnots, targetUDegree).end;

    for (var pieceIndex = 1; pieceIndex < size(pieces); pieceIndex += 1)
    {
        var grid = pieceGrids[pieceIndex];

        // One projective rescale per piece, anchored at the seam's first column.
        const previousSeamRow = assembledGrid[rowWriteIndex - 1];
        const rescale = previousSeamRow[0][3] / grid[0][0][3];
        for (var rowIndex = 0; rowIndex < size(grid); rowIndex += 1)
        {
            for (var columnIndex = 0; columnIndex < sharedColumnCount; columnIndex += 1)
            {
                grid[rowIndex][columnIndex] = rescale * grid[rowIndex][columnIndex];
            }
        }

        var averagedSeamRow = makeArray(sharedColumnCount, previousSeamRow[0]);
        for (var columnIndex = 0; columnIndex < sharedColumnCount; columnIndex += 1)
        {
            const seamPair = separatePointsAndWeights([previousSeamRow[columnIndex], grid[0][columnIndex]]);
            const gap = norm(seamPair.points[0] - seamPair.points[1]);
            if (gap > joinTolerance)
            {
                throw "splineRefinementUtils: pieces " ~ (pieceIndex - 1) ~ " and " ~ pieceIndex ~ " do not meet at " ~
                    "transverse column " ~ columnIndex ~ " - the gap is " ~ toString(gap) ~ ", over the join " ~
                    "tolerance. Pieces must be ordered and oriented so each ends where the next begins, with their " ~
                    "transverse directions aligned.";
            }
            const weightGap = abs(seamPair.weights[0] - seamPair.weights[1]);
            if (weightGap > 1e-6 * max(seamPair.weights[0], seamPair.weights[1]))
            {
                throw "splineRefinementUtils: pieces " ~ (pieceIndex - 1) ~ " and " ~ pieceIndex ~ " agree in position " ~
                    "but not in WEIGHT at transverse column " ~ columnIndex ~ " - their two parameterizations of the " ~
                    "shared edge differ rationally, and joining them exactly needs seam reparameterization that is " ~
                    "not built yet.";
            }
            joinDeviation = max(joinDeviation, gap);
            averagedSeamRow[columnIndex] = (previousSeamRow[columnIndex] + grid[0][columnIndex]) / 2;
        }
        assembledGrid[rowWriteIndex - 1] = averagedSeamRow;
        for (var rowIndex = 1; rowIndex < size(grid); rowIndex += 1)
        {
            assembledGrid[rowWriteIndex] = grid[rowIndex];
            rowWriteIndex += 1;
        }

        const pieceDomain = knotDomain(pieces[pieceIndex].uKnots, targetUDegree);
        const shift = domainEnd - pieceDomain.start;
        seamParameters[pieceIndex - 1] = domainEnd;

        const pieceUKnots = pieces[pieceIndex].uKnots;
        for (var knotIndex = targetUDegree + 1; knotIndex < size(pieceUKnots) - targetUDegree - 1; knotIndex += 1)
        {
            assembledUKnots[knotWriteIndex] = pieceUKnots[knotIndex] + shift;
            knotWriteIndex += 1;
        }
        domainEnd = pieceDomain.end + shift;
        const endMultiplicity = pieceIndex == size(pieces) - 1 ? targetUDegree + 1 : targetUDegree;
        for (var repeatIndex = 0; repeatIndex < endMultiplicity; repeatIndex += 1)
        {
            assembledUKnots[knotWriteIndex] = domainEnd;
            knotWriteIndex += 1;
        }
    }

    if (rowWriteIndex != totalRowCount || knotWriteIndex != size(assembledUKnots))
    {
        throw "splineRefinementUtils: internal error - surface concatenation produced " ~ rowWriteIndex ~ " rows and " ~
            knotWriteIndex ~ " chain knots where " ~ totalRowCount ~ " and " ~ size(assembledUKnots) ~ " were expected.";
    }

    const separated = separateSurfaceControlPointsAndWeights(assembledGrid);
    return {
            "uDegree" : targetUDegree,
            "vDegree" : targetVDegree,
            "isRational" : true,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : separated.points,
            "weights" : separated.weights,
            "uKnots" : knotArray(assembledUKnots),
            "vKnots" : knotArray(mergedVKnots),
            "seamParameters" : seamParameters,
            "joinDeviation" : joinDeviation
        };
}

/**
 * Remove `timesToRemove` instances of the knot at `parameter` from a curve, measuring the price.
 * This is the SMOOTHING primitive for concatenated chains: a seam sits at multiplicity == degree
 * (C0); each removal raises the continuity there by one order and moves the curve by an amount
 * this function measures and returns as `deviation`. Removing a knot that is exactly removable —
 * a seam between pieces of the same underlying curve — costs exactly zero.
 */
export function removeSplineKnot(spline is map, parameter is number, timesToRemove is number) returns map
{
    var normalized = normalizeSplineDefinition(spline);
    if (normalized.isPeriodic == true)
    {
        throw "splineRefinementUtils: removeSplineKnot does not yet handle periodic curves - removal there must " ~
            "preserve the overlap condition, which needs the periodic wide-window construction.";
    }

    var points = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
    var workingKnots = normalized.knots;
    var worstDeviation = 0 * meter;
    for (var removalIndex = 0; removalIndex < timesToRemove; removalIndex += 1)
    {
        const candidate = findRemovalCandidateAt(workingKnots, normalized.degree, parameter,
                timesToRemove - removalIndex);
        const removed = removeKnotFromPointArrays([points], workingKnots, normalized.degree,
                candidate.index, candidate.multiplicity);
        points = removed.pointArrays[0];
        workingKnots = removed.knots;
        worstDeviation = max(worstDeviation, removed.deviation);
    }

    const separated = separatePointsAndWeights(points);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    normalized.knots = knotArray(workingKnots);
    normalized.deviation = worstDeviation;
    return normalized;
}

/**
 * The surface form: remove `timesToRemove` instances of the knot LINE at `parameter` in the given
 * direction, deciding each removal across the whole line at once (the same whole-line rule as
 * simplification, for the same reason — rows that disagree about their knots are not a surface).
 * The seam-smoothing step of the multi-face merge.
 */
export function removeSurfaceKnotLine(surface is map, isUDirection is boolean, parameter is number, timesToRemove is number) returns map
{
    if (isUDirection)
    {
        return transposeSurface(removeSurfaceKnotLine(transposeSurface(surface), false, parameter, timesToRemove));
    }

    var normalized = normalizeSurfaceDefinition(surface);
    if (normalized.isVPeriodic == true)
    {
        throw "splineRefinementUtils: removeSurfaceKnotLine does not yet handle a periodic direction - removal " ~
            "there must preserve the overlap condition, which needs the periodic wide-window construction.";
    }

    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    var workingKnots = normalized.vKnots;
    var worstDeviation = 0 * meter;
    for (var removalIndex = 0; removalIndex < timesToRemove; removalIndex += 1)
    {
        const candidate = findRemovalCandidateAt(workingKnots, normalized.vDegree, parameter,
                timesToRemove - removalIndex);
        const removed = removeKnotFromPointArrays(homogeneousGrid, workingKnots, normalized.vDegree,
                candidate.index, candidate.multiplicity);
        homogeneousGrid = removed.pointArrays;
        workingKnots = removed.knots;
        worstDeviation = max(worstDeviation, removed.deviation);
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    normalized.vKnots = knotArray(workingKnots);
    normalized.deviation = worstDeviation;
    return normalized;
}

/** Locate the removal candidate for one specific knot value, or throw naming what was found — a
    targeted removal must never quietly remove some OTHER knot. */
function findRemovalCandidateAt(knots is array, degree is number, parameter is number, remainingToRemove is number) returns map
{
    const candidates = interiorKnotRemovalCandidates(knots, degree);
    for (var candidate in candidates)
    {
        if (abs(knots[candidate.index] - parameter) <= KNOT_PARAMETER_TOLERANCE)
        {
            return candidate;
        }
    }
    throw "splineRefinementUtils: no interior knot at parameter " ~ parameter ~ " with " ~ remainingToRemove ~
        " removal(s) still requested - either the value is not a knot of this spline, or its multiplicity is " ~
        "already exhausted.";
}

/**
 * LOFT (skin) through a family of section curves: the B-spline surface that passes exactly through
 * every section, built by interpolating the sections' control points column-by-column in the loft
 * direction. The interpolation operator is built ONCE and applied to every column — chapter 9's
 * version of the operator-amortization rule the whole module runs on.
 *
 * Sections must already share degree, knots and periodicity (they do by construction when they are
 * isocurves of one chain, or the columns of one assembly); this function checks and throws rather
 * than repairing, because repair would move geometry a caller believes is exact. CLOSED sections
 * are welcome: interpolation is columnwise, wrap columns are equal per section, and interpolating
 * equal values reproduces them exactly — so the overlap condition survives lofting verbatim.
 *
 * The loft direction becomes u; sections keep their own parameterization as v. Returns
 * `uParameters`, the station of each section on the result.
 */
export function loftBSplineSurfaceThroughCurves(sectionCurves is array, loftDegree is number) returns map
{
    return loftCore(sectionCurves, loftDegree, undefined, undefined);
}

/**
 * Loft at PRESCRIBED stations, optionally onto a PRESCRIBED loft-direction knot vector. The exact
 * reconstruction case: lofting a surface's own isocurves, taken at the Greville abscissae of its
 * own knots, onto those same knots, reproduces the surface EXACTLY (Schoenberg–Whitney
 * unisolvence) — the tester's anchor for this whole layer.
 */
export function loftBSplineSurfaceThroughCurves(sectionCurves is array, loftDegree is number,
    stationParameters is array, loftKnots is array) returns map
{
    return loftCore(sectionCurves, loftDegree, stationParameters, loftKnots);
}

function loftCore(sectionCurves is array, loftDegree is number, stationParameters, loftKnots) returns map
{
    if (size(sectionCurves) < loftDegree + 1)
    {
        throw "splineRefinementUtils: lofting " ~ size(sectionCurves) ~ " sections at degree " ~ loftDegree ~
            " is impossible - the loft direction needs at least " ~ (loftDegree + 1) ~ " sections.";
    }

    var sections = makeArray(size(sectionCurves), 0);
    for (var index = 0; index < size(sectionCurves); index += 1)
    {
        sections[index] = normalizeSplineDefinition(sectionCurves[index]);
        if (index > 0)
        {
            if (sections[index].degree != sections[0].degree ||
                (sections[index].isPeriodic == true) != (sections[0].isPeriodic == true) ||
                size(sections[index].knots) != size(sections[0].knots))
            {
                throw "splineRefinementUtils: section " ~ index ~ " does not match section 0 in degree, periodicity " ~
                    "or knot count - sections must be made compatible before lofting (makeSplinesCompatible, or " ~
                    "build them from one source).";
            }
            for (var knotIndex = 0; knotIndex < size(sections[0].knots); knotIndex += 1)
            {
                if (abs(sections[index].knots[knotIndex] - sections[0].knots[knotIndex]) > KNOT_PARAMETER_TOLERANCE)
                {
                    throw "splineRefinementUtils: section " ~ index ~ "'s knot vector differs from section 0's at " ~
                        "index " ~ knotIndex ~ " - sections must share one knot vector exactly before lofting.";
                }
            }
        }
    }

    const columnCount = size(sections[0].controlPoints);
    var homogeneousSections = makeArray(size(sections), 0);
    for (var index = 0; index < size(sections); index += 1)
    {
        homogeneousSections[index] = combinePointsAndWeights(sections[index].controlPoints, sections[index].weights);
    }

    var stations = stationParameters;
    if (stations == undefined)
    {
        // Default stations: chord-length parameters of each control column's 3D polyline, averaged
        // over the columns (A9.4's rule) — skipping columns the sections share verbatim, whose
        // zero-length polylines carry no spacing information.
        var parameterSets = [];
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            var column = makeArray(size(sections), sections[0].controlPoints[columnIndex]);
            var columnLength = 0 * meter;
            for (var index = 0; index < size(sections); index += 1)
            {
                column[index] = sections[index].controlPoints[columnIndex];
                if (index > 0)
                {
                    columnLength += norm(column[index] - column[index - 1]);
                }
            }
            if (columnLength > 0 * meter)
            {
                parameterSets = append(parameterSets, chordLengthParameters(column));
            }
        }
        if (size(parameterSets) == 0)
        {
            throw "splineRefinementUtils: every section is identical - there is no loft direction to build.";
        }
        stations = averagedParameters(parameterSets);
    }
    else if (size(stations) != size(sections))
    {
        throw "splineRefinementUtils: " ~ size(sections) ~ " sections but " ~ size(stations) ~
            " station parameters - each section needs exactly one.";
    }

    const knots = loftKnots == undefined ? averagedInterpolationKnots(stations, loftDegree) : loftKnots;
    if (size(knots) != size(sections) + loftDegree + 1)
    {
        throw "splineRefinementUtils: the prescribed loft knot vector has " ~ size(knots) ~ " entries; " ~
            (size(sections) + loftDegree + 1) ~ " are required for " ~ size(sections) ~ " sections at degree " ~
            loftDegree ~ ".";
    }
    const loftOperator = interpolationOperator(stations, knots, loftDegree);

    var lofted = makeArray(size(sections), 0);
    for (var index = 0; index < size(sections); index += 1)
    {
        lofted[index] = makeArray(columnCount, homogeneousSections[0][0]);
    }
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        var column = makeArray(size(sections), homogeneousSections[0][columnIndex]);
        for (var index = 0; index < size(sections); index += 1)
        {
            column[index] = homogeneousSections[index][columnIndex];
        }
        const loftedColumn = applyInterpolationOperator(loftOperator, column);
        for (var index = 0; index < size(sections); index += 1)
        {
            lofted[index][columnIndex] = loftedColumn[index];
        }
    }

    const separated = separateSurfaceControlPointsAndWeights(lofted);
    for (var row in separated.weights)
    {
        for (var weight in row)
        {
            if (weight <= 0)
            {
                throw "splineRefinementUtils: lofting produced a non-positive weight - the sections' weights vary " ~
                    "too sharply for these stations. Add sections where the weight variation is fastest.";
            }
        }
    }

    return {
            "uDegree" : loftDegree,
            "vDegree" : sections[0].degree,
            "isRational" : true,
            "isUPeriodic" : false,
            "isVPeriodic" : sections[0].isPeriodic == true,
            "controlPoints" : separated.points,
            "weights" : separated.weights,
            "uKnots" : knotArray(knots),
            "vKnots" : knotArray(sections[0].knots),
            "uParameters" : stations
        };
}

// ============================================================================================
// Layer 3 — SIMPLIFICATION (knot removal). The module's first deliberately LOSSY operation, and
// the scope note at the top of this file is amended accordingly: it said lossy inverses are out
// because "std already exports removeKnots and approximateSpline for those". For SURFACES that is
// simply false, and both halves of it are false:
//
//   - There is NO surface approximation or fitting routine anywhere in std. evApproximateBSplineSurface
//     takes a tolerance and gives no control over control point count; approximationUtils' routines
//     are Path/curve based (they are what editCurve's "Maximum control points" drives); opFitSpline
//     is curves. Grepping the whole library for a surface fitter returns nothing.
//   - nurbsUtils' removeKnots cannot substitute, for TWO independent reasons. First, its candidate
//     list (knotsLastIndicesAndMultiplicities) only ever offers knots with multiplicity >= 2, so it
//     structurally cannot simplify an ordinary surface whose interior knots are all simple — it
//     exists to clean up after Bezier decomposition, not to reduce a net. Second, and this is the
//     trap already documented against elevateSurfaceDegrees: it decides removability from the actual
//     point VALUES, so run per row it would remove DIFFERENT knots in different rows, and rows that
//     disagree about their knot vector are not a surface.
//
// So removability here is decided FOR A WHOLE KNOT LINE AT ONCE — the deviation is the worst across
// every row (or column), and the knot goes only if the whole line can afford it. That single change
// is what makes A5.8 a surface algorithm instead of a curve one.
// ============================================================================================

/**
 * Geometric distance between two homogeneous control points, in length units.
 *
 * Not the 4D norm: a homogeneous point is (w*x, w*y, w*z, w), mixing lengths with a unitless
 * weight, so its norm is not a length and not a meaningful error. separatePointsAndWeights divides
 * the weight back out, which gives the actual displacement of the control point being compared.
 */
function homogeneousPointDeviation(pointA is Vector, pointB is Vector) returns ValueWithUnits
{
    const separated = separatePointsAndWeights([pointA, pointB]);
    return norm(separated.points[0] - separated.points[1]);
}

/**
 * Try removing ONE instance of the knot at `knotIndex` from every point array at once, all of them
 * sharing `knots`. NURBS Book Algorithm A5.8, transcribed from std's own removeKnot (proven code)
 * with TWO structural changes, both forced by the same fact — this module removes knots that are
 * not removable, where the book only ever removes knots it has already proved removable:
 *
 *   1. Instead of a per-array boolean removability test it returns the WORST deviation across all
 *      arrays, leaving the accept/reject decision to the caller.
 *   2. Where the recurrence produces two competing answers for the surviving control point, it
 *      takes their MIDPOINT rather than whichever one the shift happens to leave standing. See the
 *      even-window branch in the body; this is the fix for the one-sided lean, and it improves
 *      accuracy rather than only symmetry.
 *
 * Returns { deviation, pointArrays, knots }. The returned arrays are the post-removal candidate;
 * the caller decides whether the deviation is affordable before adopting them.
 */
function removeKnotFromPointArrays(pointArrays is array, knots is array, degree is number, knotIndex is number, multiplicity is number) returns map
{
    const knotValue = knots[knotIndex];
    const knotCount = size(knots);
    const pointCount = size(pointArrays[0]);
    const first = knotIndex - degree;
    const last = knotIndex - multiplicity;
    const off = first - 1;

    var worstDeviation = 0 * meter;
    var candidateArrays = makeArray(size(pointArrays), 0);

    for (var arrayIndex = 0; arrayIndex < size(pointArrays); arrayIndex += 1)
    {
        const points = pointArrays[arrayIndex];
        var temp = makeArray(last - off + 2, points[0]);
        temp[0] = points[off];
        temp[last + 1 - off] = points[last + 1];

        var i = first;
        var j = last;
        var ii = 1;
        var jj = last - off;
        while (j - i > 0)
        {
            const alphaI = (knotValue - knots[i]) / (knots[i + degree + 1] - knots[i]);
            const alphaJ = (knotValue - knots[j]) / (knots[j + degree + 1] - knots[j]);
            temp[ii] = (points[i] - (1 - alphaI) * temp[ii - 1]) / alphaI;
            temp[jj] = (points[j] - alphaJ * temp[jj + 1]) / (1 - alphaJ);
            i += 1;
            ii += 1;
            j -= 1;
            jj -= 1;
        }

        // The removal is exact when the two ends of the recurrence meet; the gap between them (or,
        // in the odd case, the distance from the original point to its reconstruction) IS the error
        // removing this knot would introduce.
        var deviation = 0 * meter;
        if (j - i < 0)
        {
            // EVEN WINDOW (degree - multiplicity odd): the forward and backward recurrences pass
            // each other instead of landing on a shared slot, so they produce TWO answers for the
            // one control point that survives. They agree exactly when the knot is removable, which
            // is the only case A5.8 was ever written to handle — the NURBS Book tests `dist <= TOL`
            // and only then proceeds, so which answer it keeps is invisible there and unspecified.
            //
            // THIS MODULE REMOVES KNOTS THAT ARE NOT REMOVABLE, deliberately: seam healing and
            // simplification both hand the caller a deviation instead of a boolean. That turns an
            // unspecified choice into a load-bearing one, and taking either answer verbatim dumps
            // the WHOLE disagreement onto the side whose reconstruction was discarded. The shift
            // below keeps whichever slot index parity leaves standing, so the error always landed
            // the same way round: measured on a mirror-symmetric net, a symmetric input came back
            // lopsided by half the reported deviation, and the same geometry fed in mirrored gave a
            // materially different answer. That is the one-sided lean, and its home is here rather
            // than in any placement heuristic.
            //
            // The two answers bracket the truth, so an endpoint is the WORST available choice. The
            // midpoint is exactly minimax on the control-point displacement this function reports:
            // it halves the error rather than merely relocating it (measured 2.9x closer to the
            // original curve on the symmetric two-face crease). Symmetry then falls out of
            // minimizing the error, which is why there is no symmetry rule anywhere in this file.
            //
            // Centring in HOMOGENEOUS coordinates, not Cartesian, because the whole recurrence is
            // linear there — the midpoint of the two homogeneous answers is the point that a
            // subsequent re-insertion treats consistently. For a non-rational direction the two are
            // identical anyway.
            //
            // Both slots take the centred value so the parity of the shift below stops mattering:
            // whichever one survives carries the same point, and no caller has to reason about it.
            const forwardAnswer = temp[ii - 1];
            const backwardAnswer = temp[jj + 1];
            const centeredAnswer = 0.5 * (forwardAnswer + backwardAnswer);
            temp[ii - 1] = centeredAnswer;
            temp[jj + 1] = centeredAnswer;

            // The displacement ACTUALLY incurred, which is half the gap and is what a caller should
            // be told it paid. Reported as the worse of the two sides rather than assumed even: the
            // homogeneous midpoint of two points with different weights need not project to the
            // Cartesian midpoint, so on a rational direction the two halves differ slightly. This
            // keeps the number commensurable with the odd-window branch below, which likewise
            // measures how far the net moved, so the greedy in simplifyDirection still compares
            // like with like.
            deviation = max(homogeneousPointDeviation(forwardAnswer, centeredAnswer),
                    homogeneousPointDeviation(backwardAnswer, centeredAnswer));
        }
        else
        {
            const alphaI = (knotValue - knots[i]) / (knots[i + degree + 1] - knots[i]);
            deviation = homogeneousPointDeviation(points[i], alphaI * temp[ii + 1] + (1 - alphaI) * temp[ii - 1]);
        }
        worstDeviation = max(worstDeviation, deviation);

        // Build this array's post-removal points: the recurrence's interior values written back,
        // then everything past the removed point shifted down one.
        var updated = points;
        var writeI = first;
        var writeJ = last;
        while (writeJ - writeI > 0)
        {
            updated[writeI] = temp[writeI - off];
            updated[writeJ] = temp[writeJ - off];
            writeI += 1;
            writeJ -= 1;
        }
        const firstOut = floor((2 * knotIndex - multiplicity - degree) / 2);
        for (var k = firstOut + 1; k < pointCount; k += 1)
        {
            updated[k - 1] = updated[k];
        }
        candidateArrays[arrayIndex] = subArray(updated, 0, pointCount - 1);
    }

    var updatedKnots = knots;
    for (var k = knotIndex + 1; k < knotCount; k += 1)
    {
        updatedKnots[k - 1] = updatedKnots[k];
    }

    return {
            "deviation" : worstDeviation,
            "pointArrays" : candidateArrays,
            "knots" : subArray(updatedKnots, 0, knotCount - 1)
        };
}

/**
 * Every distinct interior knot of a clamped vector, as { index, multiplicity } where `index` is the
 * LAST position holding that value — the index A5.8 wants.
 *
 * Unlike std's knotsLastIndicesAndMultiplicities this returns SIMPLE knots too. That omission is
 * exactly why std's removeKnots cannot reduce an ordinary net: a surface read from a face has
 * multiplicity-1 interior knots almost everywhere, and skipping them leaves nothing to remove.
 */
function interiorKnotRemovalCandidates(knots is array, degree is number) returns array
{
    const runs = interiorKnotRun(knots, degree);
    var candidates = makeArray(size(runs), 0);
    var searchFrom = degree + 1;
    for (var runIndex = 0; runIndex < size(runs); runIndex += 1)
    {
        var lastIndex = searchFrom;
        for (var knotIndex = searchFrom; knotIndex < size(knots); knotIndex += 1)
        {
            if (abs(knots[knotIndex] - runs[runIndex].value) <= KNOT_PARAMETER_TOLERANCE)
            {
                lastIndex = knotIndex;
            }
        }
        candidates[runIndex] = { "index" : lastIndex, "multiplicity" : runs[runIndex].multiplicity };
        searchFrom = lastIndex + 1;
    }
    return candidates;
}

/**
 * Reduce one direction to `targetCount` control points by repeatedly removing the CHEAPEST knot —
 * the one whose removal displaces the control net least, measured across the whole knot line.
 *
 * Greedy least-error selection, re-evaluated every round because removing one knot changes what the
 * others cost. Returns the arrays, the knots, and the WORST deviation incurred, which the caller is
 * expected to report rather than swallow: this is the module's one lossy operation and the whole
 * basis for allowing it is that the price is measured and stated.
 *
 * Stops early — short of the target — if no candidate remains, rather than mangling the surface to
 * hit a number.
 *
 * THE `<` TIE-BREAK BELOW IS DELIBERATE AND WAS MEASURED. It gives the first — lowest-index, i.e.
 * leftmost — candidate every tie, which reads exactly like the one-sided lean removeKnotFromPointArrays
 * really did have, and an earlier pass here replaced it with whole-tie-group removal on that
 * suspicion. THAT CHANGE WAS WRONG AND IS REVERTED. Do not re-attempt it without new evidence:
 *
 *   - The greedy is ALREADY self-correcting across rounds. Removing the left twin of a mirror pair
 *     makes the right twin the cheapest candidate in the very next round, so pairs come out together
 *     without anyone arranging it. On a mirror-symmetric fixture every EVEN removal count lands
 *     exactly symmetric under the existing code.
 *   - What is left at ODD removal counts is not a bias but arithmetic: one knot of a tied pair has to
 *     go, and no rule makes that symmetric. Its magnitude is the removal cost the caller already
 *     accepted and had reported.
 *   - Group commitment measured strictly worse. Across 348 reduction cases it changed the outcome in
 *     12 and was worse in all 12, never better; mean deviation on symmetric fixtures rose from 0.081
 *     to 0.124, and the results were LESS mirror-symmetric, not more, because overriding the greedy's
 *     next choice sends the trajectory somewhere else and a greedy is chaotic downstream of a tie.
 *
 * The lean this function was accused of lives in removeKnotFromPointArrays' even-window branch, where
 * it was real, avoidable and is now fixed. Fixing it there is what makes the tie-break here harmless.
 */
function simplifyDirection(pointArrays is array, knots is array, degree is number, targetCount is number) returns map
{
    var currentArrays = pointArrays;
    var currentKnots = knots;
    var worstDeviation = 0 * meter;

    while (size(currentArrays[0]) > targetCount)
    {
        const candidates = interiorKnotRemovalCandidates(currentKnots, degree);
        if (size(candidates) == 0)
        {
            break;
        }

        var bestResult = undefined;
        for (var candidate in candidates)
        {
            const attempt = removeKnotFromPointArrays(currentArrays, currentKnots, degree, candidate.index, candidate.multiplicity);
            if (bestResult == undefined || attempt.deviation < bestResult.deviation)
            {
                bestResult = attempt;
            }
        }

        currentArrays = bestResult.pointArrays;
        currentKnots = bestResult.knots;
        worstDeviation = max(worstDeviation, bestResult.deviation);
    }

    return { "pointArrays" : currentArrays, "knots" : currentKnots, "deviation" : worstDeviation };
}

/**
 * Remove every knot whose removal moves the surface by less than `tolerance` — CLEANUP of redundant
 * representation, not reduction toward a budget. CLAMPED DIRECTIONS ONLY; a periodic direction is
 * returned untouched (see the body for why, and why untouched is the right answer rather than a
 * best effort).
 *
 * A knot is removed only if it is genuinely free at the given tolerance; anything that would move
 * the surface further is kept. The worst deviation actually incurred comes back in `deviation`.
 *
 * The motivating case — a kernel cylinder whose Bezier arc joints carry multiplicity == degree,
 * dragging Greville abscissae together so a refined cylinder's control points bunch at the joints —
 * is exactly the case this CANNOT yet serve, because that cylinder is periodic. Fixing periodic
 * removal is what would unlock it; nothing else here does.
 */
export function removeRedundantSurfaceKnots(surface is map, tolerance is ValueWithUnits) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    var worstDeviation = 0 * meter;

    for (var isUDirection in [true, false])
    {
        const degree = isUDirection ? normalized.uDegree : normalized.vDegree;
        const isPeriodic = isUDirection ? normalized.isUPeriodic == true : normalized.isVPeriodic == true;
        var workingKnots = isUDirection ? normalized.uKnots : normalized.vKnots;

        // A PERIODIC direction is left exactly alone. A first attempt did clean it, through the
        // tile / operate / slice window that refinement and elevation use, and it MOVED THE
        // GEOMETRY — a cylinder came back as a bean while the deviation it reported stayed small.
        // The window construction is sound for insertion and elevation, which are local and
        // forward; knot removal is a SOLVE that inverts them, and something about running it inside
        // a clamped window does not reproduce the infinite periodic answer. That was removed rather
        // than left behind a flag: silently wrong geometry is the worst failure this module can
        // have, and a lumpy but exact net beats a smooth wrong one every time.
        if (isPeriodic)
        {
            continue;
        }

        // U runs down columns, so work on the transposed view and put it back.
        var arrays = homogeneousGrid;
        if (isUDirection)
        {
            arrays = makeArray(size(homogeneousGrid[0]), 0);
            for (var columnIndex = 0; columnIndex < size(homogeneousGrid[0]); columnIndex += 1)
            {
                var column = makeArray(size(homogeneousGrid), homogeneousGrid[0][0]);
                for (var rowIndex = 0; rowIndex < size(homogeneousGrid); rowIndex += 1)
                {
                    column[rowIndex] = homogeneousGrid[rowIndex][columnIndex];
                }
                arrays[columnIndex] = column;
            }
        }

        var madeProgress = true;
        while (madeProgress)
        {
            madeProgress = false;
            // A periodic direction must keep at least one control point per period; a clamped one
            // at least degree + 1. Below that there is no spline left to remove from.
            if (size(arrays[0]) <= degree + 1)
            {
                break;
            }
            // Candidates come from a DIFFERENT list per form, and conflating them is what made an
            // earlier version throw: interiorKnotRemovalCandidates rests on interiorKnotRun, which
            // requires a CLAMPED array with degree-known ends. A periodic direction's stored array
            // is wrap-padded and unclamped, so its candidates are the distinct FUNDAMENTAL knot
            // values instead — where every entry is interior and there are no clamped ends at all.
            for (var candidate in interiorKnotRemovalCandidates(workingKnots, degree))
            {
                const attempt = try silent(removeKnotFromPointArrays(arrays, workingKnots, degree,
                            candidate.index, candidate.multiplicity));
                if (attempt != undefined && attempt.deviation <= tolerance)
                {
                    arrays = attempt.pointArrays;
                    workingKnots = attempt.knots;
                    worstDeviation = max(worstDeviation, attempt.deviation);
                    madeProgress = true;
                    break;
                }
            }
        }

        if (isUDirection)
        {
            const newRowCount = size(arrays[0]);
            var rebuilt = makeArray(newRowCount, 0);
            for (var rowIndex = 0; rowIndex < newRowCount; rowIndex += 1)
            {
                var row = makeArray(size(arrays), arrays[0][0]);
                for (var columnIndex = 0; columnIndex < size(arrays); columnIndex += 1)
                {
                    row[columnIndex] = arrays[columnIndex][rowIndex];
                }
                rebuilt[rowIndex] = row;
            }
            homogeneousGrid = rebuilt;
            normalized.uKnots = knotArray(workingKnots);
        }
        else
        {
            homogeneousGrid = arrays;
            normalized.vKnots = knotArray(workingKnots);
        }
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    normalized.deviation = worstDeviation;
    return normalized;
}

/**
 * Reduce a surface to at most the target control point counts per direction, keeping its broad
 * shape — the inverse of refineSurfaceToControlPointCounts, and the operation that lets a user
 * simplify an over-dense net rather than only add to it.
 *
 * LOSSY BY CONSTRUCTION, and that is the point: removing a knot that is not exactly removable moves
 * the surface. The returned map carries `deviation`, the worst control-point displacement incurred,
 * and callers are expected to surface it. Where the knots ARE exactly removable — every knot this
 * module's own refinement inserted, for instance — the deviation comes back at zero and the
 * round trip is exact.
 *
 * A direction already at or below its target is left completely alone. A target of 0 means "no
 * request", matching refineSurfaceToControlPointCounts' convention.
 *
 * PERIODIC DIRECTIONS THROW rather than being silently clamped or skipped. Knot removal on a
 * wrap-padded array has to preserve the overlap condition, which needs the same tile / operate /
 * slice window this module already uses for periodic refinement (buildPeriodicWindow →
 * extractPeriodicCoreAndRepad). That construction is understood and not yet built here; it is the
 * next piece, not an impossibility.
 */
export function simplifySurfaceToControlPointCounts(surface is map, targetUCount is number, targetVCount is number) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    var worstDeviation = 0 * meter;

    const wantsU = targetUCount > 0 && size(normalized.controlPoints) > targetUCount;
    const wantsV = targetVCount > 0 && size(normalized.controlPoints[0]) > targetVCount;
    if (!wantsU && !wantsV)
    {
        normalized.deviation = worstDeviation;
        return normalized;
    }
    if ((wantsU && normalized.isUPeriodic == true) || (wantsV && normalized.isVPeriodic == true))
    {
        throw "splineRefinementUtils: simplifySurfaceToControlPointCounts cannot yet reduce a PERIODIC direction. " ~
            "Knot removal there must preserve the overlap condition P[i] == P[i+n], which needs the same wide-window " ~
            "construction periodic refinement uses (buildPeriodicWindow / extractPeriodicCoreAndRepad). Refinement of " ~
            "a periodic direction is unaffected.";
    }

    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);

    if (wantsU)
    {
        // U runs down columns, so transpose into per-column arrays, simplify, and transpose back —
        // the same extract/scatter shape elevateSurfaceDegrees uses for its own U pass.
        const rowCount = size(homogeneousGrid);
        const columnCount = size(homogeneousGrid[0]);
        var columns = makeArray(columnCount, 0);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            var column = makeArray(rowCount, homogeneousGrid[0][0]);
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
            {
                column[rowIndex] = homogeneousGrid[rowIndex][columnIndex];
            }
            columns[columnIndex] = column;
        }

        const simplified = simplifyDirection(columns, normalized.uKnots, normalized.uDegree, targetUCount);
        worstDeviation = max(worstDeviation, simplified.deviation);

        const newRowCount = size(simplified.pointArrays[0]);
        var rebuiltGrid = makeArray(newRowCount, 0);
        for (var rowIndex = 0; rowIndex < newRowCount; rowIndex += 1)
        {
            var row = makeArray(columnCount, simplified.pointArrays[0][0]);
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
            {
                row[columnIndex] = simplified.pointArrays[columnIndex][rowIndex];
            }
            rebuiltGrid[rowIndex] = row;
        }
        homogeneousGrid = rebuiltGrid;
        normalized.uKnots = knotArray(simplified.knots);
    }

    if (wantsV)
    {
        // V runs across rows, which the grid already stores directly — no transpose needed.
        const simplified = simplifyDirection(homogeneousGrid, normalized.vKnots, normalized.vDegree, targetVCount);
        worstDeviation = max(worstDeviation, simplified.deviation);
        homogeneousGrid = simplified.pointArrays;
        normalized.vKnots = knotArray(simplified.knots);
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    normalized.deviation = worstDeviation;
    return normalized;
}

// ============================================================================================
// PERIODIC SIMPLIFICATION BY CYCLIC PROJECTION.
//
// The one lossy operation in this module that a PERIODIC direction can actually use, and the
// reason it exists: a kernel-native closed surface is C0 at knots the deformation then creases.
//
// A revolve arrives as a circle stored in rational Bezier arcs — degree 3 with every join at
// multiplicity 3. A knot of multiplicity m in a degree-d direction leaves the surface C^(d-m), so
// m == d leaves it C0: FREE TO CREASE. Such a surface is G1 across those joins only because its
// control points happen to be arranged for it, collinear with matching weight ratios. Nothing in
// the STRUCTURE requires it, and any nonlinear map on control points — an FFD lattice, say —
// destroys the arrangement. opCreateBSplineSurface then refuses the body outright:
// PERIODIC_BSPLINESURFACE_NOT_SMOOTH when the offending join is the seam, BSPLINESURFACE_NOT_G1
// when it is interior. Measured 2026-08-10, with refinement and elevation both disabled, so
// neither is implicated.
//
// Knot INSERTION cannot fix this — it only ever raises multiplicity. Knot REMOVAL cannot either:
// removeRedundantSurfaceKnots' own comment records a periodic attempt that returned a cylinder
// "as a bean" while reporting a small deviation, and the diagnosis there is right. A5.8 is a
// LOCAL BIDIRECTIONAL RECURRENCE anchored by control points it assumes are unchanged; on a
// periodic spline those anchors are themselves images of points the true answer does change, so a
// clamped window solves the wrong system and reports only a local residual. Exact removal is
// impossible anyway: a NURBS circle genuinely is C0 at its arc joins in HOMOGENEOUS space, the
// projected curve being smooth only through weight cancellation.
//
// So this takes the one remaining route, and takes it as linear algebra rather than as a fit.
// Given a target knot vector U_tgt with every multiplicity 1:
//
//   1. Form the common refinement U_com = U_cur union U_tgt (per-value max multiplicity). Both
//      S(U_cur) and S(U_tgt) are subspaces of S(U_com), because a spline space is contained in
//      another exactly when its knot vector is a sub-multiset of the other's.
//   2. Lift the current points into S(U_com) EXACTLY, by periodic knot insertion.
//   3. Solve min ||A*Q - P_com|| where A is the refinement operator S(U_tgt) -> S(U_com).
//
// THE SURFACE IS NEVER EVALUATED. No sample points, no parameterization choice, no interpolation
// conditions — which is what separates this from approximating the face and lofting it back. Two
// properties follow that a fit does not have:
//
//   - EXACT WHEN EXACTNESS IS POSSIBLE. If the spline really does lie in S(U_tgt), the residual is
//     identically zero and Q is recovered exactly, because A has full column rank. This is a
//     strict generalization of knot removal, not a substitute for it.
//   - THE ERROR BOUND IS STRUCTURAL, not sampled. Both splines end up expressed in the SAME basis
//     S(U_com), and B-splines are non-negative and partition unity, so for every t
//         ||S_tgt(t) - S_cur(t)|| = ||sum_i N_i(t) * R_i|| <= max_i ||R_i||,  R = A*Q - P_com.
//     A sup bound over the whole domain, computed from control points alone.
//
// And the periodic case is native rather than patched. periodicRefinementOperator ALREADY folds
// its window rows back onto stored indices (see its own comment: "the overlap condition comes out
// of that fold for free"), so A arrives cyclically banded with the wrap already consistent. The
// clamped window is used only to harvest FORWARD, LOCAL, EXACT refinement coefficients — the
// operation extractPeriodicCoreAndRepad's local-linear-independence argument covers — and the
// SOLVE is global over the true cyclic unknowns. That is the whole difference from the attempt
// that produced the bean.
// ============================================================================================

/** One period's knots as `{value, multiplicity}` runs, ascending. The fundamental array is
    non-decreasing, and normalizeSplineDefinition has already canonicalized any run straddling the
    stored window's ends, so consecutive scanning is the whole job.

    @param fundamentalKnots {array} : one period's knots
    @returns {array} : maps with `value` and `multiplicity` */
function fundamentalKnotRuns(fundamentalKnots is array) returns array
{
    var runs = [];
    var index = 0;
    while (index < size(fundamentalKnots))
    {
        var runLength = 1;
        while (index + runLength < size(fundamentalKnots) &&
            abs(fundamentalKnots[index + runLength] - fundamentalKnots[index]) <= KNOT_PARAMETER_TOLERANCE)
        {
            runLength += 1;
        }
        runs = append(runs, { "value" : fundamentalKnots[index], "multiplicity" : runLength });
        index += runLength;
    }
    return runs;
}

/** The highest multiplicity anywhere in one period. On a periodic direction EVERY knot is
    interior — there are no clamped ends — so this alone decides whether the direction can crease.

    @param knots {array} : STORED periodic knot array
    @param degree {number}
    @returns {number} */
export function periodicMaximumKnotMultiplicity(knots is array, degree is number) returns number
{
    const n = size(knots) - 2 * degree - 1;
    if (n < 1)
    {
        return 0;
    }
    var worst = 0;
    for (var run in fundamentalKnotRuns(subArray(knots, degree, degree + n)))
    {
        worst = max(worst, run.multiplicity);
    }
    return worst;
}

/** True when a deformation applied to this direction's control points could crease it — i.e. some
    knot sits at multiplicity `degree` or above, leaving the direction only C0 there. At
    multiplicity degree - 1 and below the direction is C1 or better FOR ANY CONTROL POINTS, which
    is precisely the property a deformer needs and cannot otherwise obtain.

    @param knots {array} : STORED periodic knot array
    @param degree {number}
    @returns {boolean} */
export function periodicDirectionCanCrease(knots is array, degree is number) returns boolean
{
    return periodicMaximumKnotMultiplicity(knots, degree) >= degree;
}

/** Every span of one period bisected, keeping the existing breakpoints. Balanced by construction —
    the module's own warning on refineSurfaceToControlPointCounts(..., balancedOnly) is that
    lopsided refinement feeding a LOSSY step leaves an asymmetry that survives in the geometry, and
    this is a lossy step. The wrap span from the last breakpoint back to the first plus a period is
    bisected like any other, which is what keeps the result symmetric across the seam.

    @param breakpoints {array} : distinct, ascending, within one period
    @param period {number}
    @returns {array} : twice as many, still ascending and within one period */
function bisectPeriodicBreakpoints(breakpoints is array, period is number) returns array
{
    var bisected = makeArray(2 * size(breakpoints), 0);
    for (var index = 0; index < size(breakpoints); index += 1)
    {
        const nextValue = index + 1 < size(breakpoints) ? breakpoints[index + 1] : breakpoints[0] + period;
        bisected[2 * index] = breakpoints[index];
        bisected[2 * index + 1] = 0.5 * (breakpoints[index] + nextValue);
    }
    return bisected;
}

/** The insertions that take one period's knots up to another's: for each value, the shortfall in
    multiplicity. Both arrays must be ascending and over the same period, and `target` must dominate
    `source` value by value — which it does by construction, `target` being a union.

    @param sourceRuns {array} : from fundamentalKnotRuns
    @param targetRuns {array} : from fundamentalKnotRuns
    @returns {array} : plain parameters, repeats included */
function fundamentalInsertionList(sourceRuns is array, targetRuns is array) returns array
{
    var insertions = [];
    for (var targetRun in targetRuns)
    {
        var sourceMultiplicity = 0;
        for (var sourceRun in sourceRuns)
        {
            if (abs(sourceRun.value - targetRun.value) <= KNOT_PARAMETER_TOLERANCE)
            {
                sourceMultiplicity = sourceRun.multiplicity;
            }
        }
        for (var extra = sourceMultiplicity; extra < targetRun.multiplicity; extra += 1)
        {
            insertions = append(insertions, targetRun.value);
        }
    }
    return insertions;
}

/** The per-value maximum of two periods' knot runs — the common refinement U_cur union U_tgt.

    @returns {array} : runs, ascending */
function mergeFundamentalRuns(runsA is array, runsB is array) returns array
{
    var merged = runsA;
    for (var runB in runsB)
    {
        var found = false;
        for (var index = 0; index < size(merged); index += 1)
        {
            if (abs(merged[index].value - runB.value) <= KNOT_PARAMETER_TOLERANCE)
            {
                found = true;
                if (runB.multiplicity > merged[index].multiplicity)
                {
                    merged[index] = { "value" : merged[index].value, "multiplicity" : runB.multiplicity };
                }
            }
        }
        if (!found)
        {
            merged = append(merged, runB);
        }
    }

    // Insertion sort: `merged` is at most a few dozen entries and already nearly ordered.
    for (var outer = 1; outer < size(merged); outer += 1)
    {
        var moving = merged[outer];
        var inner = outer - 1;
        while (inner >= 0 && merged[inner].value > moving.value)
        {
            merged[inner + 1] = merged[inner];
            inner -= 1;
        }
        merged[inner + 1] = moving;
    }
    return merged;
}

/** Runs expanded back into a flat ascending knot list.

    @returns {array} */
function expandFundamentalRuns(runs is array) returns array
{
    var total = 0;
    for (var run in runs)
    {
        total += run.multiplicity;
    }
    var expanded = makeArray(total, 0);
    var writeIndex = 0;
    for (var run in runs)
    {
        for (var copy = 0; copy < run.multiplicity; copy += 1)
        {
            expanded[writeIndex] = run.value;
            writeIndex += 1;
        }
    }
    return expanded;
}

/**
 * Cholesky factorization of a symmetric positive definite matrix of plain numbers, returning the
 * lower triangle L with A == L * transpose(L).
 *
 * Rolled here rather than pulled from matrix.fs so this module keeps its standing property of
 * being pure arithmetic over plain arrays, and so a non-positive pivot can throw with a diagnosis
 * instead of returning a quietly wrong inverse. A non-positive pivot means the normal matrix is
 * singular, which for a refinement operator means the target space was not genuinely coarser —
 * a caller error worth naming rather than absorbing.
 */
function choleskyFactor(normalMatrix is array) returns array
{
    const count = size(normalMatrix);
    var lower = makeArray(count, 0);
    for (var row = 0; row < count; row += 1)
    {
        lower[row] = makeArray(count, 0);
    }
    for (var row = 0; row < count; row += 1)
    {
        for (var column = 0; column <= row; column += 1)
        {
            var sum = normalMatrix[row][column];
            for (var back = 0; back < column; back += 1)
            {
                sum = sum - lower[row][back] * lower[column][back];
            }
            if (row == column)
            {
                if (sum <= 0)
                {
                    throw "splineRefinementUtils: periodic simplification's normal matrix is not positive definite " ~
                        "(pivot " ~ sum ~ " at index " ~ row ~ "). The target knot vector does not span a genuinely " ~
                        "coarser subspace of the common refinement.";
                }
                lower[row][column] = sqrt(sum);
            }
            else
            {
                lower[row][column] = sum / lower[column][column];
            }
        }
    }
    return lower;
}

/** Solve `L * transpose(L) * X = rightHandSides` for X, one column per right-hand side, by forward
    then back substitution.

    @param lower {array} : from choleskyFactor
    @param rightHandSides {array} : rows x columns of plain numbers
    @returns {array} : same shape */
function choleskySolve(lower is array, rightHandSides is array) returns array
{
    const count = size(lower);
    const columnCount = size(rightHandSides[0]);
    var solution = makeArray(count, 0);
    for (var row = 0; row < count; row += 1)
    {
        solution[row] = makeArray(columnCount, 0);
    }

    for (var column = 0; column < columnCount; column += 1)
    {
        var intermediate = makeArray(count, 0);
        for (var row = 0; row < count; row += 1)
        {
            var sum = rightHandSides[row][column];
            for (var back = 0; back < row; back += 1)
            {
                sum = sum - lower[row][back] * intermediate[back];
            }
            intermediate[row] = sum / lower[row][row];
        }
        for (var row = count - 1; row >= 0; row -= 1)
        {
            var sum = intermediate[row];
            for (var forward = row + 1; forward < count; forward += 1)
            {
                sum = sum - lower[forward][row] * solution[forward][column];
            }
            solution[row][column] = sum / lower[row][row];
        }
    }
    return solution;
}

/**
 * The three operators that carry one periodic direction from its current knot vector onto
 * `targetFundamentalKnots` (which must be all-simple, one period, ascending, same domain start).
 *
 * Returns:
 *   `toCommon`   — current stored points -> common-refinement stored points. EXACT.
 *   `simplify`   — common-refinement stored points -> target stored points. The projection.
 *   `backToCommon` — target stored points -> common-refinement stored points. EXACT, and only for
 *                    measuring the residual: applying it after `simplify` lands back in the space
 *                    `toCommon`'s output lives in, which is what makes the two directly comparable.
 *
 * All three carry the module's standard operator shape, so every existing applier — including both
 * tensor appliers — drives them with no special casing.
 *
 * THE CYCLIC FOLD is the step worth understanding. periodicRefinementOperator's map is stated over
 * STORED points (n + degree of them, the last `degree` being wrap images of the first `degree`), so
 * its columns carry the same unknown more than once. The projection's unknowns are the n
 * FUNDAMENTAL points, so columns are folded modulo n before the normal equations are formed —
 * accumulating, exactly as periodicRefinementOperator folds its own window columns. Rows are folded
 * the other way, by TRUNCATION to the first n_common: rows r and r + n_common are identical
 * equations against identical right-hand sides, so keeping both would silently weight the first
 * `degree` equations double and tilt the fit.
 */
function periodicSimplificationOperators(knots is array, degree is number, targetFundamentalKnots is array) returns map
{
    const currentN = size(knots) - 2 * degree - 1;
    const currentFundamental = subArray(knots, degree, degree + currentN);
    const period = knots[degree + currentN] - knots[degree];

    const currentRuns = fundamentalKnotRuns(currentFundamental);
    const targetRuns = fundamentalKnotRuns(targetFundamentalKnots);
    const commonRuns = mergeFundamentalRuns(currentRuns, targetRuns);
    const commonFundamental = expandFundamentalRuns(commonRuns);

    const targetN = size(targetFundamentalKnots);
    const commonN = size(commonFundamental);

    const targetKnots = buildPeriodicKnotArray(targetFundamentalKnots, period, degree, targetN + 2 * degree + 1);
    const toCommon = periodicRefinementOperator(knots, degree, fundamentalInsertionList(currentRuns, commonRuns));
    const backToCommon = periodicRefinementOperator(targetKnots, degree, fundamentalInsertionList(targetRuns, commonRuns));

    // Both operators must land in the SAME space, or the residual below compares points that do not
    // correspond and the reported deviation is meaningless — the exact failure mode that made the
    // previous periodic knot removal report a small number for a cylinder shaped like a bean. The
    // two insertion lists are built independently from the same merge, so this is a genuine
    // cross-check of that merge and not a restatement of it.
    if (toCommon.outputCount != commonN + degree || backToCommon.outputCount != commonN + degree)
    {
        throw "splineRefinementUtils: periodic simplification's two refinements disagree on the common space (" ~
            toCommon.outputCount ~ " and " ~ backToCommon.outputCount ~ ", expected " ~ (commonN + degree) ~ ").";
    }

    // Dense cyclic A: commonN equations over targetN unknowns.
    var cyclicA = makeArray(commonN, 0);
    for (var row = 0; row < commonN; row += 1)
    {
        var denseRow = makeArray(targetN, 0);
        for (var term in backToCommon.rows[row])
        {
            const folded = term.index - floor(term.index / targetN) * targetN;
            denseRow[folded] = denseRow[folded] + term.weight;
        }
        cyclicA[row] = denseRow;
    }

    // Which rows actually touch each unknown. A refinement operator's rows have at most `degree + 1`
    // nonzero terms, so each COLUMN of A is nonzero on only a handful of rows — the band the comment
    // below names. Collecting them costs one scan of a matrix that has just been built anyway, and
    // turns the normal-equation loop from O(targetN^2 * commonN) into O(targetN^2 * bandwidth).
    //
    // THIS IS EXACT, not an approximation of the sum. A skipped row contributes cyclicA[row][i] == 0
    // times something, which is exactly zero, and adding zero to a finite running sum returns it
    // unchanged — so every entry below is bit-for-bit what the dense triple loop produced. The
    // surviving rows are still visited in ascending order, so even the rounding of the accumulation
    // is untouched. That matters more here than the speed does: this matrix decides the projection,
    // and the tester's PERIODIC-SIMPLIFY exactness check is what would catch it drifting.
    var rowsTouchingUnknown = makeArray(targetN, []);
    for (var row = 0; row < commonN; row += 1)
    {
        for (var i = 0; i < targetN; i += 1)
        {
            if (cyclicA[row][i] != 0)
            {
                rowsTouchingUnknown[i] = append(rowsTouchingUnknown[i], row);
            }
        }
    }

    // Normal equations. transpose(A)*A is targetN x targetN, symmetric, cyclically banded, and
    // positive definite because a refinement operator between nested spline spaces is injective.
    var normalMatrix = makeArray(targetN, 0);
    for (var i = 0; i < targetN; i += 1)
    {
        const contributingRows = rowsTouchingUnknown[i];
        const contributingCount = size(contributingRows);
        var normalRow = makeArray(targetN, 0);
        for (var j = 0; j < targetN; j += 1)
        {
            var sum = 0;
            for (var index = 0; index < contributingCount; index += 1)
            {
                const row = contributingRows[index];
                sum = sum + cyclicA[row][i] * cyclicA[row][j];
            }
            normalRow[j] = sum;
        }
        normalMatrix[i] = normalRow;
    }

    var transposedA = makeArray(targetN, 0);
    for (var i = 0; i < targetN; i += 1)
    {
        var transposedRow = makeArray(commonN, 0);
        for (var row = 0; row < commonN; row += 1)
        {
            transposedRow[row] = cyclicA[row][i];
        }
        transposedA[i] = transposedRow;
    }

    // pseudoInverse = inverse(transpose(A)*A) * transpose(A), built once and reused down every
    // row or column of the grid — the amortization the whole operator layer exists for.
    const pseudoInverse = choleskySolve(choleskyFactor(normalMatrix), transposedA);

    var simplifyRows = makeArray(targetN + degree, 0);
    for (var row = 0; row < targetN + degree; row += 1)
    {
        // The trailing `degree` rows are the wrap: literal copies, so the overlap condition
        // Q[i] == Q[i + n] holds by construction rather than by arithmetic coincidence.
        const sourceRow = row < targetN ? row : row - targetN;
        var denseRow = makeArray(commonN + degree, 0);
        for (var column = 0; column < commonN; column += 1)
        {
            denseRow[column] = pseudoInverse[sourceRow][column];
        }
        simplifyRows[row] = denseRow;
    }

    return {
            "toCommon" : toCommon,
            "backToCommon" : backToCommon,
            "commonN" : commonN,
            "simplify" : {
                    "degree" : degree,
                    "inputCount" : commonN + degree,
                    "outputCount" : targetN + degree,
                    "knots" : knotArray(targetKnots),
                    "rows" : sparsifyCoefficientRows(simplifyRows)
                }
        };
}

/**
 * Project one periodic direction's point arrays onto an all-simple knot vector, refining the target
 * until the certified deviation fits `tolerance`.
 *
 * `pointArrays` is a list of same-length HOMOGENEOUS arrays sharing this direction's knot vector —
 * one per row (or column) of a surface grid, or a single array for a curve. They are simplified
 * JOINTLY through one operator, which is what keeps every row landing on an identical knot vector.
 *
 * Returns `{ pointArrays, knots, deviation, changed }`. A direction that cannot crease is returned
 * untouched with zero deviation, so this is safe to call unconditionally.
 *
 * The target starts at the direction's own distinct breakpoints with every multiplicity collapsed
 * to 1 and DOUBLES until it fits. Starting there rather than at a uniform vector keeps the
 * surface's own structure — a revolve's arc joins stay knot lines — and doubling keeps every pass
 * balanced. If the cap is reached the best result so far is returned WITH its true deviation, for
 * the caller to warn about; silently returning something further out than requested is the one
 * outcome this must not have.
 */
function simplifyPeriodicDirectionToTolerance(pointArrays is array, knots is array, degree is number,
    tolerance is ValueWithUnits) returns map
{
    if (!periodicDirectionCanCrease(knots, degree))
    {
        return { "pointArrays" : pointArrays, "knots" : knots, "deviation" : 0 * meter, "changed" : false };
    }

    const currentN = size(knots) - 2 * degree - 1;
    const period = knots[degree + currentN] - knots[degree];
    const currentRuns = fundamentalKnotRuns(subArray(knots, degree, degree + currentN));

    var breakpoints = makeArray(size(currentRuns), 0);
    for (var index = 0; index < size(currentRuns); index += 1)
    {
        breakpoints[index] = currentRuns[index].value;
    }

    var best = undefined;
    while (size(breakpoints) <= PERIODIC_SIMPLIFICATION_MAX_CONTROL_POINTS)
    {
        // A degree-d periodic direction needs more than d control points per period before the
        // basis is even locally independent, so a target below that is not worth solving.
        if (size(breakpoints) > degree)
        {
            const operators = periodicSimplificationOperators(knots, degree, breakpoints);

            var simplifiedArrays = makeArray(size(pointArrays), 0);
            var worstDeviation = 0 * meter;
            for (var arrayIndex = 0; arrayIndex < size(pointArrays); arrayIndex += 1)
            {
                const common = applyKnotRefinementOperator(operators.toCommon, pointArrays[arrayIndex]);
                const simplified = applyKnotRefinementOperator(operators.simplify, common);
                simplifiedArrays[arrayIndex] = simplified;

                // The residual, measured in the shared basis S(U_common) — the partition-of-unity
                // bound. Only the FUNDAMENTAL rows are measured; the wrap rows are their images and
                // would report the same numbers twice.
                const rebuilt = applyKnotRefinementOperator(operators.backToCommon, simplified);
                for (var row = 0; row < operators.commonN; row += 1)
                {
                    worstDeviation = max(worstDeviation, homogeneousPointDeviation(rebuilt[row], common[row]));
                }
            }

            best = {
                    "pointArrays" : simplifiedArrays,
                    "knots" : operators.simplify.knots,
                    "deviation" : worstDeviation,
                    "changed" : true
                };
            if (worstDeviation <= tolerance)
            {
                return best;
            }
        }
        breakpoints = bisectPeriodicBreakpoints(breakpoints, period);
    }

    if (best == undefined)
    {
        return { "pointArrays" : pointArrays, "knots" : knots, "deviation" : 0 * meter, "changed" : false };
    }
    return best;
}

/**
 * Make every PERIODIC direction of a surface safe to deform: no knot left at multiplicity `degree`,
 * so the direction is C1 or better everywhere FOR ANY CONTROL POINTS and no deformation can crease
 * it. See this section's block comment for why that is the requirement and why insertion and
 * removal both cannot meet it.
 *
 * A direction that is not periodic, or that already cannot crease, is left completely alone — so
 * this is safe to call on any surface and costs one knot scan when there is nothing to do. The
 * returned map carries `deviation` (worst certified control-point displacement, zero when the
 * simplification was exact) and `simplified` (whether anything changed), matching the convention on
 * simplifySurfaceToControlPointCounts and removeRedundantSurfaceKnots.
 *
 * NOT applied to clamped directions, deliberately. The same C0 hazard exists there — an elevated or
 * Bezier-decomposed clamped direction creases under deformation too — but a clamped direction's
 * ends are legitimately at multiplicity degree + 1 and its interior candidates need the clamped
 * removal machinery that already exists. Periodic is where the hazard is unavoidable, because a
 * revolve is always delivered this way.
 */
export function simplifySurfacePeriodicDirections(surface is map, tolerance is ValueWithUnits) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    var worstDeviation = 0 * meter;
    var anySimplified = false;

    if (normalized.isVPeriodic == true)
    {
        // V runs across rows, which the grid already stores directly — no transpose needed.
        const simplified = simplifyPeriodicDirectionToTolerance(homogeneousGrid, normalized.vKnots,
                normalized.vDegree, tolerance);
        if (simplified.changed)
        {
            homogeneousGrid = simplified.pointArrays;
            normalized.vKnots = knotArray(simplified.knots);
            worstDeviation = max(worstDeviation, simplified.deviation);
            anySimplified = true;
        }
    }

    if (normalized.isUPeriodic == true)
    {
        // U runs down columns, so work on the transposed view and put it back — the same
        // extract/scatter shape simplifySurfaceToControlPointCounts uses for its own U pass.
        const rowCount = size(homogeneousGrid);
        const columnCount = size(homogeneousGrid[0]);
        var columns = makeArray(columnCount, 0);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            var column = makeArray(rowCount, homogeneousGrid[0][0]);
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
            {
                column[rowIndex] = homogeneousGrid[rowIndex][columnIndex];
            }
            columns[columnIndex] = column;
        }

        const simplified = simplifyPeriodicDirectionToTolerance(columns, normalized.uKnots,
                normalized.uDegree, tolerance);
        if (simplified.changed)
        {
            const newRowCount = size(simplified.pointArrays[0]);
            var rebuilt = makeArray(newRowCount, 0);
            for (var rowIndex = 0; rowIndex < newRowCount; rowIndex += 1)
            {
                var row = makeArray(columnCount, simplified.pointArrays[0][0]);
                for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
                {
                    row[columnIndex] = simplified.pointArrays[columnIndex][rowIndex];
                }
                rebuilt[rowIndex] = row;
            }
            homogeneousGrid = rebuilt;
            normalized.uKnots = knotArray(simplified.knots);
            worstDeviation = max(worstDeviation, simplified.deviation);
            anySimplified = true;
        }
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    normalized.deviation = worstDeviation;
    normalized.simplified = anySimplified;
    return normalized;
}

/**
 * Guarantee at least `minimumControlPointsPerSpan` control points across every cell of the given
 * parameter partitions, refining only where the requirement is not met — the targeted refinement
 * a lattice-driven FFD wants (spec section 9.2), as opposed to refineSurfaceToControlPointCounts'
 * blanket growth. Geometry unchanged, as everywhere in this module. Periodicity is PRESERVED:
 * both directions go through directionRefinementOperator, so a closed direction refines closed.
 *
 * Cells come from consecutive boundaries, plus — on a periodic direction — the wrap cell back to
 * the first boundary. Boundaries need not be knots and need not cover the whole domain; a
 * direction whose boundary list is empty is left alone.
 *
 * NOT done here, deliberately: the boundaries are never themselves inserted as knots. Density and
 * CONTINUITY are separate questions, the same way degree and tolerance are separate in spec
 * section 9.1.1 — a caller whose deformation map is only C0 across its lattice cell boundaries
 * needs a knot of multiplicity `degree` at each boundary to represent the crease, and no amount
 * of density substitutes for it. That is one directionRefinementOperator call with each boundary
 * repeated `degree` times, and it belongs to the caller that knows its map's continuity, not to a
 * function whose contract is density.
 */
export function refineSurfaceToSpanDensity(surface is map, uSpanBoundaries is array, vSpanBoundaries is array, minimumControlPointsPerSpan is number) returns map
{
    if (minimumControlPointsPerSpan < 1)
    {
        throw "splineRefinementUtils: refineSurfaceToSpanDensity needs minimumControlPointsPerSpan >= 1 (got " ~
            minimumControlPointsPerSpan ~ "); every cell already carries at least one.";
    }

    var normalized = normalizeSurfaceDefinition(surface);
    const uInsertions = spanDensityInsertions(normalized.uKnots, normalized.uDegree, normalized.isUPeriodic == true,
            uSpanBoundaries, minimumControlPointsPerSpan, "refineSurfaceToSpanDensity's U direction");
    const vInsertions = spanDensityInsertions(normalized.vKnots, normalized.vDegree, normalized.isVPeriodic == true,
            vSpanBoundaries, minimumControlPointsPerSpan, "refineSurfaceToSpanDensity's V direction");
    if (size(uInsertions) == 0 && size(vInsertions) == 0)
    {
        return normalized;
    }

    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    if (size(uInsertions) > 0)
    {
        const uOperator = directionRefinementOperator(normalized.uKnots, normalized.uDegree, normalized.isUPeriodic == true, uInsertions);
        homogeneousGrid = applyKnotRefinementOperatorDownColumns(uOperator, homogeneousGrid);
        normalized.uKnots = knotArray(uOperator.knots);
    }
    if (size(vInsertions) > 0)
    {
        const vOperator = directionRefinementOperator(normalized.vKnots, normalized.vDegree, normalized.isVPeriodic == true, vInsertions);
        homogeneousGrid = applyKnotRefinementOperatorAcrossRows(vOperator, homogeneousGrid);
        normalized.vKnots = knotArray(vOperator.knots);
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    return normalized;
}

/**
 * Refine `normalized` (whose CURRENT knots are currentUKnots/currentVKnots, already
 * domain-remapped to match a target) onto targetUKnots/targetVKnots via insertionsToReach per
 * direction, tensor-applied. Shared by makeSurfacesShareKnotVectors for both input surfaces.
 */
function refineSurfaceOntoKnotVectors(normalized is map, currentUKnots is array, currentVKnots is array,
    insertionsU is array, insertionsV is array) returns map
{
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);

    const uOperator = directionRefinementOperator(currentUKnots, normalized.uDegree, normalized.isUPeriodic == true, insertionsU);
    homogeneousGrid = applyKnotRefinementOperatorDownColumns(uOperator, homogeneousGrid);

    const vOperator = directionRefinementOperator(currentVKnots, normalized.vDegree, normalized.isVPeriodic == true, insertionsV);
    homogeneousGrid = applyKnotRefinementOperatorAcrossRows(vOperator, homogeneousGrid);

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    var result = normalized;
    result.controlPoints = separated.points;
    result.weights = separated.weights;
    result.uKnots = knotArray(uOperator.knots);
    result.vKnots = knotArray(vOperator.knots);
    return result;
}

/**
 * Surface analog of makeSplinesShareKnotVector: refine both surfaces onto merged U and V knot
 * vectors. Degrees must already match per direction — use makeSurfacesCompatible when they
 * may not. The curve-level helpers (remapKnotsToUnitDomain, mergeKnotVectors,
 * insertionsToReach) work as-is on a knot array regardless of which surface direction it
 * belongs to; only the application step needs the tensor appliers instead of the direct
 * per-curve path.
 */
export function makeSurfacesShareKnotVectors(surfaceA is map, surfaceB is map) returns map
{
    var normalizedA = normalizeSurfaceDefinition(surfaceA);
    var normalizedB = normalizeSurfaceDefinition(surfaceB);
    if (normalizedA.uDegree != normalizedB.uDegree || normalizedA.vDegree != normalizedB.vDegree)
    {
        throw "splineRefinementUtils: makeSurfacesShareKnotVectors requires equal U and V degrees - elevate first, or use makeSurfacesCompatible.";
    }

    // Mixed periodicity IN A DIRECTION: a closed direction blended against an open one has no
    // shared notion of "closed" to preserve, so the periodic side is clamped down to a single
    // non-wrapping period — per direction, independently, exactly as the curve path does. When
    // both sides agree, periodicity is preserved end to end.
    if ((normalizedA.isUPeriodic == true) != (normalizedB.isUPeriodic == true))
    {
        normalizedA = clampSurfaceDirectionForMixedUse(normalizedA, true);
        normalizedB = clampSurfaceDirectionForMixedUse(normalizedB, true);
    }
    if ((normalizedA.isVPeriodic == true) != (normalizedB.isVPeriodic == true))
    {
        normalizedA = clampSurfaceDirectionForMixedUse(normalizedA, false);
        normalizedB = clampSurfaceDirectionForMixedUse(normalizedB, false);
    }

    const uPlan = directionSharingPlan(normalizedA.uKnots, normalizedB.uKnots, normalizedA.uDegree, normalizedA.isUPeriodic == true);
    const vPlan = directionSharingPlan(normalizedA.vKnots, normalizedB.vKnots, normalizedA.vDegree, normalizedA.isVPeriodic == true);

    const resultA = refineSurfaceOntoKnotVectors(normalizedA, uPlan.remappedA, vPlan.remappedA, uPlan.insertionsA, vPlan.insertionsA);
    const resultB = refineSurfaceOntoKnotVectors(normalizedB, uPlan.remappedB, vPlan.remappedB, uPlan.insertionsB, vPlan.insertionsB);

    return { "a" : resultA, "b" : resultB };
}

/**
 * Re-cut a periodic surface direction's stored window so it starts at fundamental index
 * `startIndex` instead of 0 — the surface analog of rewindowPeriodicSpline, and the operation
 * that makes SEAM alignment between two closed surfaces exact.
 *
 * Restricted to integer fundamental indices, which makes it a pure RE-INDEX of both the control
 * grid and the knot intervals: no knot insertion, no new control points, no arithmetic on point
 * values at all. That restriction is deliberate. rewindowPeriodicSpline accepts an arbitrary
 * seam parameter and inserts a knot when it has to, which is right for a curve where the caller
 * has a continuous optimum in hand; a surface's seam only ever needs to line up with the OTHER
 * surface's control structure, and the n integer positions are exactly the candidates worth
 * considering. Snapping to them costs nothing and keeps the whole operation free.
 *
 * Geometry is untouched — a periodic surface is a finite window onto an infinite periodic
 * structure, so which period-length window gets stored is pure labelling. Absolute parameters
 * keep meaning what they meant: the direction's domain simply moves to start at the new seam.
 *
 * @param startIndex : any integer; wrapped into [0, n). 0 returns the surface unchanged.
 */
export function rewindowPeriodicSurfaceDirection(surface is map, isUDirection is boolean, startIndex is number) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    const degree = isUDirection ? normalized.uDegree : normalized.vDegree;
    const knots = isUDirection ? normalized.uKnots : normalized.vKnots;
    if (isUDirection ? normalized.isUPeriodic != true : normalized.isVPeriodic != true)
    {
        throw "splineRefinementUtils: rewindowPeriodicSurfaceDirection requires that direction to be periodic.";
    }

    const n = size(knots) - 2 * degree - 1;
    const shift = startIndex - floor(startIndex / n) * n;
    if (shift == 0)
    {
        return normalized;
    }

    const fundamentalKnots = subArray(knots, degree, degree + n);
    const period = knots[degree + n] - knots[degree];
    var newFundamentalKnots = makeArray(n, 0);
    for (var knotIndex = 0; knotIndex < n; knotIndex += 1)
    {
        const sourceIndex = shift + knotIndex;
        const cycle = floor(sourceIndex / n);
        newFundamentalKnots[knotIndex] = fundamentalKnots[sourceIndex - cycle * n] + cycle * period;
    }
    const newKnots = knotArray(buildPeriodicKnotArray(newFundamentalKnots, period, degree, n + 2 * degree + 1));

    var result = normalized;
    if (isUDirection)
    {
        // U is the ROW direction: re-index rows, rebuilding the overlap tail by wrapping.
        var newRows = makeArray(n + degree, normalized.controlPoints[0]);
        var newWeightRows = makeArray(n + degree, normalized.weights[0]);
        for (var rowIndex = 0; rowIndex < n + degree; rowIndex += 1)
        {
            const sourceIndex = shift + rowIndex;
            const cycle = floor(sourceIndex / n);
            newRows[rowIndex] = normalized.controlPoints[sourceIndex - cycle * n];
            newWeightRows[rowIndex] = normalized.weights[sourceIndex - cycle * n];
        }
        result.controlPoints = newRows;
        result.weights = newWeightRows;
        result.uKnots = newKnots;
        return result;
    }

    // V is the COLUMN direction: re-index within every row.
    var newGrid = makeArray(size(normalized.controlPoints), 0);
    var newWeightGrid = makeArray(size(normalized.weights), 0);
    for (var rowIndex = 0; rowIndex < size(normalized.controlPoints); rowIndex += 1)
    {
        var row = makeArray(n + degree, normalized.controlPoints[rowIndex][0]);
        var weightRow = makeArray(n + degree, normalized.weights[rowIndex][0]);
        for (var columnIndex = 0; columnIndex < n + degree; columnIndex += 1)
        {
            const sourceIndex = shift + columnIndex;
            const cycle = floor(sourceIndex / n);
            row[columnIndex] = normalized.controlPoints[rowIndex][sourceIndex - cycle * n];
            weightRow[columnIndex] = normalized.weights[rowIndex][sourceIndex - cycle * n];
        }
        newGrid[rowIndex] = row;
        newWeightGrid[rowIndex] = weightRow;
    }
    result.controlPoints = newGrid;
    result.weights = newWeightGrid;
    result.vKnots = newKnots;
    return result;
}

/**
 * Reverse ONE direction of a surface exactly: rows (U) or every row's columns (V) reversed,
 * with that direction's knots reflected about its own domain
 * (newKnots[k] = domainStart + domainEnd - knots[M - 1 - k], keeping the domain in place).
 * Pure permutation plus reflection — control points are never recomputed — so it is exact for
 * clamped and periodic directions alike, by reverseSpline's own argument; like reverseSpline,
 * the result is re-normalized, which canonicalizes the seam multiplicity run that reflection
 * splits across the domain boundary on a periodic direction (see seamImageTailCount).
 *
 * This is the reversal to use on NORMALIZED (wrap-form) surfaces. tweenSurfaces' legacy
 * applyAlignmentTransform flip — a hand-rolled `1 - knot` reflection — is exact only for the
 * RAW kernel forms it predates (clamped and closed-clamped on a [0, 1] domain); on a wrap form
 * it leaves the split seam run in place, which made a reversed revolve direction unshareable
 * with its unreversed partner (the live cone-to-cylinder multiplicity-cap throw, 2026-08-08).
 */
export function reverseSurfaceDirection(surface is map, isUDirection is boolean) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    const degree = isUDirection ? normalized.uDegree : normalized.vDegree;
    const knots = isUDirection ? normalized.uKnots : normalized.vKnots;
    const knotCount = size(knots);
    const domain = knotDomain(knots, degree);

    var reflectedKnots = makeArray(knotCount, 0);
    for (var knotIndex = 0; knotIndex < knotCount; knotIndex += 1)
    {
        reflectedKnots[knotIndex] = domain.start + domain.end - knots[knotCount - 1 - knotIndex];
    }

    var result = normalized;
    if (isUDirection)
    {
        result.controlPoints = reverse(normalized.controlPoints);
        result.weights = reverse(normalized.weights);
        result.uKnots = knotArray(reflectedKnots);
    }
    else
    {
        var reversedGrid = makeArray(size(normalized.controlPoints), 0);
        var reversedWeightGrid = makeArray(size(normalized.weights), 0);
        for (var rowIndex = 0; rowIndex < size(normalized.controlPoints); rowIndex += 1)
        {
            reversedGrid[rowIndex] = reverse(normalized.controlPoints[rowIndex]);
            reversedWeightGrid[rowIndex] = reverse(normalized.weights[rowIndex]);
        }
        result.controlPoints = reversedGrid;
        result.weights = reversedWeightGrid;
        result.vKnots = knotArray(reflectedKnots);
    }
    return normalizeSurfaceDefinition(result);
}

/**
 * Align the seams of two surfaces that ALREADY share a knot vector in `isUDirection`, so that
 * fundamental index i of A corresponds to index (i + relativeShift) of B — leaving BOTH surfaces
 * with a multiplicity-1 (smooth) seam. (Implemented and covered by the BEZIER-SEAM tester
 * vector, which fixed this signature and algorithm while the function was still a stub.)
 *
 * WHY ONE-SIDED RE-WINDOWING IS NOT ENOUGH — the thing that makes this hard, and the reason the
 * naive version shipped broken: the kernel stores a revolve's circular direction as BEZIER ARCS,
 * giving fundamental knots like [0, 0.5, 0.5, 0.5]. Indices 1, 2 and 3 all carry the SAME value,
 * so every nonzero seam shift lands on the multiplicity-3 arc joint. A seam there is C0, which the
 * kernel rejects (PERIODIC_BSPLINESURFACE_NOT_SMOOTH), while the same multiplicity is perfectly
 * legal at an INTERIOR knot. So the required offset is reachable only by moving one seam onto a
 * joint — unless both seams move.
 *
 * THE ALGORITHM:
 *   1. Read the shared direction's fundamental knots, period, and n. The offset to realize is
 *      offsetParameter = fundamentalKnots[relativeShift] - fundamentalKnots[0].
 *   2. Find a seam parameter s such that BOTH s and (s + offsetParameter) mod period are "safe":
 *      each is either an existing multiplicity-1 knot, or not an existing knot at all (so
 *      inserting it yields multiplicity 1). Candidates worth trying, in order: the existing
 *      multiplicity-1 knots, then the midpoints of the non-degenerate spans.
 *   3. Insert both s and s + offsetParameter into BOTH surfaces, so they keep sharing a knot
 *      vector. Insertion is exact (periodicRefinementOperator).
 *   4. Re-window A to s and B to s + offsetParameter, via rewindowPeriodicSurfaceDirection at the
 *      fundamental indices those parameters now occupy.
 *   5. Re-share. Both contribute multiplicity 1 at the merged seam, so the seam stays smooth,
 *      and the correspondence set up in step 4 is preserved because both were remapped from the
 *      same shared domain.
 *
 * Worked example, from the revolve pair that exposed this: fundamental [0, 0.5, 0.5, 0.5],
 * relativeShift 3, so offsetParameter = 0.5. s = 0 fails (0 + 0.5 = 0.5 is the multiplicity-3
 * joint). s = 0.25 succeeds: 0.25 and 0.75 are both new knots, both land at multiplicity 1, and
 * their difference is the required 0.5.
 */
export function alignPeriodicSurfaceSeams(surfaceA is map, surfaceB is map, isUDirection is boolean, relativeShift is number) returns map
{
    var normalizedA = normalizeSurfaceDefinition(surfaceA);
    var normalizedB = normalizeSurfaceDefinition(surfaceB);
    const degree = isUDirection ? normalizedA.uDegree : normalizedA.vDegree;
    const knots = isUDirection ? normalizedA.uKnots : normalizedA.vKnots;
    const n = size(knots) - 2 * degree - 1;
    const fundamentalKnots = subArray(knots, degree, degree + n);
    const period = knots[degree + n] - knots[degree];

    const wrappedShift = relativeShift - floor(relativeShift / n) * n;
    if (wrappedShift == 0)
    {
        return { "a" : normalizedA, "b" : normalizedB, "seamA" : fundamentalKnots[0], "seamB" : fundamentalKnots[0] };
    }
    const offsetParameter = fundamentalKnots[wrappedShift] - fundamentalKnots[0];

    const seamPair = findSafeSeamPair(fundamentalKnots, period, offsetParameter);

    // Insert only the seams that are not already knots. Inserting an existing multiplicity-1 knot
    // would raise it to 2, defeating the entire point of choosing it.
    var insertions = makeArray(2, 0);
    var insertionCount = 0;
    if (fundamentalKnotMultiplicity(fundamentalKnots, seamPair.seamA) == 0)
    {
        insertions[insertionCount] = seamPair.seamA;
        insertionCount += 1;
    }
    if (fundamentalKnotMultiplicity(fundamentalKnots, seamPair.seamB) == 0)
    {
        insertions[insertionCount] = seamPair.seamB;
        insertionCount += 1;
    }
    const actualInsertions = subArray(insertions, 0, insertionCount);
    normalizedA = insertIntoPeriodicSurfaceDirection(normalizedA, isUDirection, actualInsertions);
    normalizedB = insertIntoPeriodicSurfaceDirection(normalizedB, isUDirection, actualInsertions);

    normalizedA = rewindowPeriodicSurfaceDirection(normalizedA, isUDirection,
            periodicSurfaceFundamentalIndex(normalizedA, isUDirection, seamPair.seamA));
    normalizedB = rewindowPeriodicSurfaceDirection(normalizedB, isUDirection,
            periodicSurfaceFundamentalIndex(normalizedB, isUDirection, seamPair.seamB));

    // Re-share. Both sides now contribute multiplicity 1 at their own seam, and the merge takes
    // the maximum per value, so the merged seam stays multiplicity 1 — which is the whole
    // objective. The correspondence set up by the two re-windows survives because both are
    // remapped from the same shared domain by the same period.
    const shared = makeSurfacesShareKnotVectors(normalizedA, normalizedB);
    return { "a" : shared.a, "b" : shared.b, "seamA" : seamPair.seamA, "seamB" : seamPair.seamB };
}

/** How many times `value` occurs among one period's fundamental knots — 0 when it is not a knot
    at all, which is the SAFEST case, since inserting there yields multiplicity exactly 1. */
function fundamentalKnotMultiplicity(fundamentalKnots is array, value is number) returns number
{
    var multiplicity = 0;
    for (var knotIndex = 0; knotIndex < size(fundamentalKnots); knotIndex += 1)
    {
        if (abs(fundamentalKnots[knotIndex] - value) <= KNOT_PARAMETER_TOLERANCE)
        {
            multiplicity += 1;
        }
    }
    return multiplicity;
}

/**
 * Find a pair of seam parameters (s, s + offset) that are BOTH safe to seat a seam on — each
 * either absent from the knot vector or present exactly once, so that after insertion each
 * carries multiplicity 1 and leaves its surface smooth across the seam.
 *
 * Candidates are tried cheapest-first: existing multiplicity-1 knots (no insertion needed), then
 * span midpoints, then span quarter points. Only finitely many parameters are unsafe — the
 * high-multiplicity knots — so a safe pair exists for any offset; the widening candidate ladder
 * is about finding one without inserting more than necessary, not about whether one exists.
 */
function findSafeSeamPair(fundamentalKnots is array, period is number, offsetParameter is number) returns map
{
    const domainStart = fundamentalKnots[0];
    const fractions = [0.5, 0.25, 0.75];

    var candidates = makeArray(size(fundamentalKnots) * (1 + size(fractions)), 0);
    var candidateCount = 0;
    for (var knotIndex = 0; knotIndex < size(fundamentalKnots); knotIndex += 1)
    {
        if (fundamentalKnotMultiplicity(fundamentalKnots, fundamentalKnots[knotIndex]) == 1)
        {
            candidates[candidateCount] = fundamentalKnots[knotIndex];
            candidateCount += 1;
        }
    }
    for (var fraction in fractions)
    {
        for (var knotIndex = 0; knotIndex < size(fundamentalKnots); knotIndex += 1)
        {
            const spanEnd = knotIndex + 1 < size(fundamentalKnots) ? fundamentalKnots[knotIndex + 1] : domainStart + period;
            if (spanEnd - fundamentalKnots[knotIndex] > KNOT_PARAMETER_TOLERANCE)
            {
                candidates[candidateCount] = fundamentalKnots[knotIndex] + (spanEnd - fundamentalKnots[knotIndex]) * fraction;
                candidateCount += 1;
            }
        }
    }

    for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex += 1)
    {
        const seamA = candidates[candidateIndex];
        const rawSeamB = seamA + offsetParameter - domainStart;
        const seamB = domainStart + (rawSeamB - floor(rawSeamB / period) * period);
        if (fundamentalKnotMultiplicity(fundamentalKnots, seamA) <= 1 &&
            fundamentalKnotMultiplicity(fundamentalKnots, seamB) <= 1 &&
            abs(seamA - seamB) > KNOT_PARAMETER_TOLERANCE)
        {
            return { "seamA" : seamA, "seamB" : seamB };
        }
    }

    throw "splineRefinementUtils: could not place two smooth seams an offset of " ~ offsetParameter ~
        " apart in a period of " ~ period ~ ". Every candidate landed on a knot whose multiplicity " ~
        "exceeds 1, which would make the seam C0 and the periodic surface invalid.";
}

/** Insert parameters into ONE periodic direction of a surface, exactly, via the periodic
    refinement operator applied across the whole grid. */
function insertIntoPeriodicSurfaceDirection(surface is map, isUDirection is boolean, parametersToInsert is array) returns map
{
    if (size(parametersToInsert) == 0)
    {
        return surface;
    }
    const degree = isUDirection ? surface.uDegree : surface.vDegree;
    const knots = isUDirection ? surface.uKnots : surface.vKnots;
    const refinementOperator = periodicRefinementOperator(knots, degree, parametersToInsert);

    var homogeneousGrid = combineSurfaceControlPointsAndWeights(surface.controlPoints, surface.weights);
    homogeneousGrid = isUDirection ? applyKnotRefinementOperatorDownColumns(refinementOperator, homogeneousGrid)
        : applyKnotRefinementOperatorAcrossRows(refinementOperator, homogeneousGrid);
    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);

    var result = surface;
    result.controlPoints = separated.points;
    result.weights = separated.weights;
    if (isUDirection)
    {
        result.uKnots = knotArray(refinementOperator.knots);
    }
    else
    {
        result.vKnots = knotArray(refinementOperator.knots);
    }
    return result;
}

/** Fundamental index of `parameter` in a periodic surface direction; throws if it is not a knot,
    since every caller here has just ensured that it is. */
function periodicSurfaceFundamentalIndex(surface is map, isUDirection is boolean, parameter is number) returns number
{
    const degree = isUDirection ? surface.uDegree : surface.vDegree;
    const knots = isUDirection ? surface.uKnots : surface.vKnots;
    const n = size(knots) - 2 * degree - 1;
    for (var knotIndex = 0; knotIndex < n; knotIndex += 1)
    {
        if (abs(knots[degree + knotIndex] - parameter) <= KNOT_PARAMETER_TOLERANCE)
        {
            return knotIndex;
        }
    }
    throw "splineRefinementUtils: seam parameter " ~ parameter ~ " is not a knot of this periodic direction.";
}

/**
 * Convert a wrap-form STORED periodic spline to the CLOSED CLAMPED representation for kernel
 * emission: clamped extraction over one period, first and last control points coinciding at the
 * seam point by construction, isPeriodic KEPT true. This is the kernel's own output convention
 * for periodic geometry (confirmed by raw dump), and the form it accepts when the seam knot's
 * multiplicity has reached `degree` — where wrap-form creation is rejected as a non-smooth
 * periodic seam even though the geometry closes smoothly. Exact (clamped extraction), and the
 * exact inverse of the closed-clamped-to-wrap conversion in normalizeSplineDefinition, so a
 * no-op pipeline round-trips to the kernel's original arrays.
 */
export function toClosedClampedPeriodicForm(spline is map) returns map
{
    if (spline.isPeriodic != true)
    {
        return spline;
    }
    const degree = spline.degree;
    const domain = knotDomain(spline.knots, degree);
    const clamp = clampedSegmentOperator(spline.knots, degree, domain.start, domain.end);
    const homogeneousPoints = combinePointsAndWeights(spline.controlPoints, spline.weights);
    const clampedHomogeneous = applyKnotRefinementOperator(clamp, homogeneousPoints);
    const separated = separatePointsAndWeights(clampedHomogeneous);

    var result = spline;
    result.controlPoints = separated.points;
    result.weights = separated.weights;
    result.knots = knotArray(clamp.knots);
    return result;
}

/** Surface counterpart of toClosedClampedPeriodicForm for ONE direction: clamped extraction
    tensor-applied over the grid, periodic flag KEPT true. */
export function toClosedClampedSurfaceDirection(surface is map, isUDirection is boolean) returns map
{
    if (isUDirection ? surface.isUPeriodic != true : surface.isVPeriodic != true)
    {
        return surface;
    }
    const degree = isUDirection ? surface.uDegree : surface.vDegree;
    const knots = isUDirection ? surface.uKnots : surface.vKnots;
    const domain = knotDomain(knots, degree);
    const clamp = clampedSegmentOperator(knots, degree, domain.start, domain.end);

    var homogeneousGrid = combineSurfaceControlPointsAndWeights(surface.controlPoints, surface.weights);
    homogeneousGrid = isUDirection ? applyKnotRefinementOperatorDownColumns(clamp, homogeneousGrid)
        : applyKnotRefinementOperatorAcrossRows(clamp, homogeneousGrid);
    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);

    var result = surface;
    result.controlPoints = separated.points;
    result.weights = separated.weights;
    if (isUDirection)
    {
        result.uKnots = knotArray(clamp.knots);
    }
    else
    {
        result.vKnots = knotArray(clamp.knots);
    }
    return result;
}

/**
 * Clamp ONE direction of a surface to a single non-wrapping period, leaving the other alone.
 * A no-op when that direction is already clamped, so callers can apply it unconditionally to
 * both sides of a mixed pair. Surface counterpart of clampPeriodicSplineForMixedUse.
 */
function clampSurfaceDirectionForMixedUse(normalized is map, isUDirection is boolean) returns map
{
    const degree = isUDirection ? normalized.uDegree : normalized.vDegree;
    const knots = isUDirection ? normalized.uKnots : normalized.vKnots;
    if (isUDirection ? normalized.isUPeriodic != true : normalized.isVPeriodic != true)
    {
        return normalized;
    }

    const domain = knotDomain(knots, degree);
    const clamp = clampedSegmentOperator(knots, degree, domain.start, domain.end);
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    homogeneousGrid = isUDirection ? applyKnotRefinementOperatorDownColumns(clamp, homogeneousGrid)
        : applyKnotRefinementOperatorAcrossRows(clamp, homogeneousGrid);

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    var result = normalized;
    result.controlPoints = separated.points;
    result.weights = separated.weights;
    result.wasClampedFromPeriodic = true;
    if (isUDirection)
    {
        result.uKnots = knotArray(clamp.knots);
        result.isUPeriodic = false;
    }
    else
    {
        result.vKnots = knotArray(clamp.knots);
        result.isVPeriodic = false;
    }
    return result;
}

/**
 * Degree elevation of a surface in either/both directions. Elevates U by extracting each
 * COLUMN as a homogeneous point array and calling elevateHomogeneousPointsRaw (NOT
 * elevateSplineDegree — see that function's own comment: skipping removeKnots is what keeps
 * every column landing on the identical elevated knot vector, since removeKnots' decisions are
 * point-value-dependent and would otherwise diverge column to column), then transposing back
 * (same extract/scatter pattern as applyKnotRefinementOperatorDownColumns). Then V by rows,
 * same idea without the transpose. Replaces tweenSurfaces elevateSurfaceDegree.
 */
export function elevateSurfaceDegrees(surface is map, targetUDegree is number, targetVDegree is number) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);

    if (normalized.uDegree < targetUDegree)
    {
        const rowCount = size(homogeneousGrid);
        const columnCount = size(homogeneousGrid[0]);
        var columns = makeArray(columnCount, 0);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            var column = makeArray(rowCount, homogeneousGrid[0][0]);
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
            {
                column[rowIndex] = homogeneousGrid[rowIndex][columnIndex];
            }
            columns[columnIndex] = column;
        }

        const elevated = elevatePointArraysSharingKnots(columns, normalized.uKnots, normalized.uDegree,
                targetUDegree, normalized.isUPeriodic == true);

        const newRowCount = size(elevated.pointArrays[0]);
        var reassembledGrid = makeArray(newRowCount, 0);
        for (var rowIndex = 0; rowIndex < newRowCount; rowIndex += 1)
        {
            var row = makeArray(columnCount, elevated.pointArrays[0][0]);
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
            {
                row[columnIndex] = elevated.pointArrays[columnIndex][rowIndex];
            }
            reassembledGrid[rowIndex] = row;
        }
        homogeneousGrid = reassembledGrid;
        normalized.uKnots = knotArray(elevated.knots);
        normalized.uDegree = targetUDegree;
    }

    if (normalized.vDegree < targetVDegree)
    {
        const elevated = elevatePointArraysSharingKnots(homogeneousGrid, normalized.vKnots, normalized.vDegree,
                targetVDegree, normalized.isVPeriodic == true);
        homogeneousGrid = elevated.pointArrays;
        normalized.vKnots = knotArray(elevated.knots);
        normalized.vDegree = targetVDegree;
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    return normalized;
}

/**
 * Full surface compatibility: elevate to common degrees per direction, then share knot
 * vectors. What tweenSurfaces Phase 4 calls (spec section 7).
 */
export function makeSurfacesCompatible(surfaceA is map, surfaceB is map) returns map
{
    const normalizedA = normalizeSurfaceDefinition(surfaceA);
    const normalizedB = normalizeSurfaceDefinition(surfaceB);
    const targetUDegree = max(normalizedA.uDegree, normalizedB.uDegree);
    const targetVDegree = max(normalizedA.vDegree, normalizedB.vDegree);
    const elevatedA = (normalizedA.uDegree < targetUDegree || normalizedA.vDegree < targetVDegree)
        ? elevateSurfaceDegrees(normalizedA, targetUDegree, targetVDegree) : normalizedA;
    const elevatedB = (normalizedB.uDegree < targetUDegree || normalizedB.vDegree < targetVDegree)
        ? elevateSurfaceDegrees(normalizedB, targetUDegree, targetVDegree) : normalizedB;
    return makeSurfacesShareKnotVectors(elevatedA, elevatedB);
}

/**
 * Tensor-product Bezier patch decomposition of a surface: decomposeIntoSegmentsCore's plan
 * (interiorRunsAndInsertionPlan) generalized to apply via the tensor operators instead of the
 * flat one, in both directions, then sliced into a 2D array of patches. The per-patch entry
 * point for flattening work (spec section 9.3).
 *
 * Returns a 2D array indexed [uSegment][vSegment], each patch
 * { "controlPoints", "weights", "uDegree", "vDegree", "uDomainStart/End", "vDomainStart/End" }.
 *
 * A CLOSED direction is clamped over one full period first, and that is not the clamping this
 * module rejects elsewhere — the distinction matters enough to state outright. What section 2.3
 * rejects is clamping and then re-flagging the result as periodic, which manufactures a seam
 * where the representation claims smoothness. Here the periodicity is being deliberately spent:
 * a Bezier patch is an open object by definition, and the union of the patches reproduces the
 * closed surface exactly, seam included, because the clamped extraction over the full period is
 * itself exact. Nothing downstream is told the pieces are still closed.
 */
export function decomposeSurfaceIntoBezierPatches(surface is map) returns array
{
    var normalized = normalizeSurfaceDefinition(surface);
    normalized = clampSurfaceDirectionForMixedUse(normalized, true);
    normalized = clampSurfaceDirectionForMixedUse(normalized, false);

    const uPlan = interiorRunsAndInsertionPlan(normalized.uKnots, normalized.uDegree);
    const vPlan = interiorRunsAndInsertionPlan(normalized.vKnots, normalized.vDegree);

    // knotRefinementOperator, NOT refineKnotVector, on both directions — the opposite of
    // decomposeIntoSegmentsCore's choice, and for the reason stated there: the same refinement
    // runs down every column (and then across every row), so the operator build amortizes instead
    // of being discarded after one application.
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    if (size(uPlan.insertions) > 0)
    {
        homogeneousGrid = applyKnotRefinementOperatorDownColumns(
                knotRefinementOperator(normalized.uKnots, normalized.uDegree, uPlan.insertions), homogeneousGrid);
    }
    if (size(vPlan.insertions) > 0)
    {
        homogeneousGrid = applyKnotRefinementOperatorAcrossRows(
                knotRefinementOperator(normalized.vKnots, normalized.vDegree, vPlan.insertions), homogeneousGrid);
    }

    // Every interior knot now sits at multiplicity == degree in both directions, so the grid is
    // numSegments * degree + 1 per direction and patch (i, j) is the (degree + 1) x (degree + 1)
    // block starting at (i * uDegree, j * vDegree) — adjacent patches sharing their boundary row
    // or column, exactly as the curve-level slice does.
    var patches = makeArray(uPlan.numSegments, 0);
    for (var uSegment = 0; uSegment < uPlan.numSegments; uSegment += 1)
    {
        var patchRow = makeArray(vPlan.numSegments, 0);
        for (var vSegment = 0; vSegment < vPlan.numSegments; vSegment += 1)
        {
            var patchGrid = makeArray(normalized.uDegree + 1, 0);
            for (var rowOffset = 0; rowOffset <= normalized.uDegree; rowOffset += 1)
            {
                patchGrid[rowOffset] = subArray(homogeneousGrid[uSegment * normalized.uDegree + rowOffset],
                        vSegment * normalized.vDegree, vSegment * normalized.vDegree + normalized.vDegree + 1);
            }
            const separated = separateSurfaceControlPointsAndWeights(patchGrid);
            patchRow[vSegment] = {
                    "controlPoints" : separated.points,
                    "weights" : separated.weights,
                    "uDegree" : normalized.uDegree,
                    "vDegree" : normalized.vDegree,
                    "uDomainStart" : uPlan.breakpoints[uSegment],
                    "uDomainEnd" : uPlan.breakpoints[uSegment + 1],
                    "vDomainStart" : vPlan.breakpoints[vSegment],
                    "vDomainEnd" : vPlan.breakpoints[vSegment + 1]
                };
        }
        patches[uSegment] = patchRow;
    }
    return patches;
}

/** True when [rangeStart, rangeEnd] is the direction's entire domain, to knot tolerance. */
function coversWholeDomain(domain is map, rangeStart is number, rangeEnd is number) returns boolean
{
    return abs(rangeStart - domain.start) <= KNOT_PARAMETER_TOLERANCE &&
        abs(rangeEnd - domain.end) <= KNOT_PARAMETER_TOLERANCE;
}

/**
 * Reject an extraction range that is not a positive-width sub-range of the direction's own domain.
 *
 * The periodic case gets its own sentence because the obvious reading of "extract [0.8, 0.2] from
 * a closed direction" is a range that WRAPS the seam, and that is not expressible as a slice of
 * the stored window at all. The exact route exists — move the seam first — so name it instead of
 * silently reinterpreting the arguments.
 */
function validateExtractionRange(domain is map, rangeStart is number, rangeEnd is number, isPeriodic is boolean, directionName is string)
{
    if (rangeStart < domain.start - KNOT_PARAMETER_TOLERANCE || rangeEnd > domain.end + KNOT_PARAMETER_TOLERANCE)
    {
        throw "splineRefinementUtils: extractSubSurface's " ~ directionName ~ " range [" ~ rangeStart ~ ", " ~ rangeEnd ~
            "] is not inside that direction's domain [" ~ domain.start ~ ", " ~ domain.end ~ "].";
    }
    if (rangeEnd - rangeStart <= KNOT_PARAMETER_TOLERANCE)
    {
        throw "splineRefinementUtils: extractSubSurface needs a positive-width " ~ directionName ~ " range (got [" ~
            rangeStart ~ ", " ~ rangeEnd ~ "])." ~ (isPeriodic ?
            " A range that WRAPS past a periodic direction's seam is not a sub-rectangle of the stored window: move " ~
            "the seam with rewindowPeriodicSurfaceDirection until the range is contiguous, then extract." : "");
    }
}

/**
 * Exact sub-surface over a parameter rectangle — clampedSegmentOperator per direction,
 * tensor-applied. The general form of the displacement map's tile extraction; the entry point
 * flattening and local-patch work call (spec section 9.3).
 *
 * A direction whose requested range is its WHOLE domain is left untouched rather than run through
 * an identity extraction — which matters for a periodic direction, where clamping over the full
 * period would throw away a closed representation the caller never asked to give up. Any direction
 * that IS narrowed comes back clamped and flagged non-periodic, because a sub-rectangle of a
 * closed surface genuinely is an open patch.
 */
export function extractSubSurface(surface is map, uStart is number, uEnd is number, vStart is number, vEnd is number) returns map
{
    var normalized = normalizeSurfaceDefinition(surface);
    const uDomain = knotDomain(normalized.uKnots, normalized.uDegree);
    const vDomain = knotDomain(normalized.vKnots, normalized.vDegree);
    validateExtractionRange(uDomain, uStart, uEnd, normalized.isUPeriodic == true, "U");
    validateExtractionRange(vDomain, vStart, vEnd, normalized.isVPeriodic == true, "V");

    const narrowsU = !coversWholeDomain(uDomain, uStart, uEnd);
    const narrowsV = !coversWholeDomain(vDomain, vStart, vEnd);
    if (!narrowsU && !narrowsV)
    {
        return normalized;
    }

    // Both operators are built from the ORIGINAL knot vectors: refining down columns does not
    // touch vKnots and vice versa, so the two directions are independent and the order is free.
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);
    if (narrowsU)
    {
        const uClamp = clampedSegmentOperator(normalized.uKnots, normalized.uDegree, uStart, uEnd);
        homogeneousGrid = applyKnotRefinementOperatorDownColumns(uClamp, homogeneousGrid);
        normalized.uKnots = knotArray(uClamp.knots);
        if (normalized.isUPeriodic == true)
        {
            normalized.isUPeriodic = false;
            normalized.wasClampedFromPeriodic = true;
        }
    }
    if (narrowsV)
    {
        const vClamp = clampedSegmentOperator(normalized.vKnots, normalized.vDegree, vStart, vEnd);
        homogeneousGrid = applyKnotRefinementOperatorAcrossRows(vClamp, homogeneousGrid);
        normalized.vKnots = knotArray(vClamp.knots);
        if (normalized.isVPeriodic == true)
        {
            normalized.isVPeriodic = false;
            normalized.wasClampedFromPeriodic = true;
        }
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    return normalized;
}

/**
 * Surface form of prepareSplineForDeformation: elevate both directions, then refine both
 * directions. This is step 2 of the deformation pipeline (spec section 9.1); the adaptive
 * tolerance loop of section 9.1.1 drives the counts and lives in the calling feature.
 *
 * The order is load-bearing and is the surface statement of prepareSplineForDeformation's own:
 * elevating AFTER refining multiplies the control point count for nothing, since elevation adds
 * points in proportion to the segment count it is handed. Either step is skipped when already
 * satisfied, per direction — refineSurfaceToControlPointCounts makes that decision itself.
 */
export function prepareSurfaceForDeformation(surface is map, targetUDegree is number, targetVDegree is number, targetUCount is number, targetVCount is number) returns map
{
    const normalized = normalizeSurfaceDefinition(surface);
    const elevated = (normalized.uDegree < targetUDegree || normalized.vDegree < targetVDegree)
        ? elevateSurfaceDegrees(normalized, targetUDegree, targetVDegree) : normalized;
    return (size(elevated.controlPoints) < targetUCount || size(elevated.controlPoints[0]) < targetVCount)
        ? refineSurfaceToControlPointCounts(elevated, targetUCount, targetVCount) : elevated;
}
