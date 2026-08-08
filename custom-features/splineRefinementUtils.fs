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
    this module adds representational degrees of freedom while leaving the geometry bit-for-bit
    unchanged. The lossy inverses (knot removal as a simplification service, degree reduction,
    approximation) are deliberately NOT here — the standard library's nurbsUtils.fs and
    splineUtils.fs already provide removeKnots and approximateSpline for those jobs.

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
      Layers 1-2, direct insertion, and the surface evaluator: implemented AND tester-verified
        passing in Onshape.
      Curve-level Layer 3 (normalizeSplineDefinition through prepareSplineForDeformation, plus
        their shared private helpers): implemented, NOT yet run in Onshape — needs a tester
        pass (add vectors per each function's doc comment) before anything downstream (Phase 3
        tweenCurves, Phase 4's curve half) treats it as trustworthy.
      Surface-level Layer 3 (refineSurfaceToControlPointCounts through
        prepareSurfaceForDeformation): still HOOK stubs. Each one's doc comment now describes
        exactly which curve-level helper it generalizes and how — the hard math already exists,
        this is "tensor-apply the same operators via applyKnotRefinementOperatorDownColumns /
        AcrossRows instead of the flat applyKnotRefinementOperator."

    HOOKS — instructions for implementing a remaining stubbed function:
      Stubs are marked "HOOK(functionName)" in their doc comment and throw a descriptive error.
      To implement one:
        1. Read its doc comment contract and the spec section it cites. Do not change any
           function signature, and do not modify already-implemented functions to make a hook
           pass — if an implemented function seems wrong, stop and flag it instead.
        2. Follow the conventions block above, especially preallocation, scalar-on-left, and
           the KnotArray-casting discipline on every return.
        3. A hook is DONE when the tester vector named in its comment passes in Onshape, not
           before. Add the vector to splineRefinementTester.fs if it is not already there.
      Remaining recommended order (all curve-level dependencies now exist):
        refineSurfaceToControlPointCounts -> makeSurfacesShareKnotVectors ->
        elevateSurfaceDegrees -> makeSurfacesCompatible -> refineSurfaceToSpanDensity ->
        decomposeSurfaceIntoBezierPatches -> extractSubSurface -> prepareSurfaceForDeformation.
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

const NOT_IMPLEMENTED_MESSAGE = "splineRefinementUtils: not yet implemented (see HOOK notes in the source): ";

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

/**
 * Elementwise firstScale * firstRow + secondScale * secondRow over plain-number coefficient
 * rows. Used only while building refinementOperators on the unit basis.
 */
function scaledRowSum(firstScale is number, firstRow is array, secondScale is number, secondRow is array) returns array
{
    var combined = makeArray(size(firstRow), 0);
    for (var entryIndex = 0; entryIndex < size(firstRow); entryIndex += 1)
    {
        combined[entryIndex] = firstScale * firstRow[entryIndex] + secondScale * secondRow[entryIndex];
    }
    return combined;
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
 * @returns {map} : { "rows" : dense coefficient rows, "knots" : refined knot vector }
 */
function buildRefinementCoefficients(knots is array, degree is number, parametersToInsert is array, maximumFinalMultiplicity is number) returns map
{
    const inputCount = size(knots) - degree - 1;
    if (inputCount < degree + 1)
    {
        throw "splineRefinementUtils: " ~ inputCount ~ " control points is too few for degree " ~ degree ~
            " (need at least " ~ (degree + 1) ~ ").";
    }

    // Identity to start: coefficientRows[i] is the length-inputCount unit row for input i.
    var coefficientRows = makeArray(inputCount, 0);
    for (var inputIndex = 0; inputIndex < inputCount; inputIndex += 1)
    {
        var unitRow = makeArray(inputCount, 0);
        unitRow[inputIndex] = 1;
        coefficientRows[inputIndex] = unitRow;
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
                refinedRows[outputIndex] = scaledRowSum(alpha, coefficientRows[outputIndex], 1 - alpha, coefficientRows[outputIndex - 1]);
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
            "rows" : sparsifyCoefficientRows(built.rows)
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
            "rows" : sparsifyCoefficientRows(pieceRows)
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
// they exist per the AGENTS.md manual-math rule because no std function does the job. Curves
// need no analog here: cast to BSplineCurve and use std evaluateSpline.

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
    // Direct sequential insertion, NOT knotRefinementOperator. The operator earns its keep only
    // when the SAME refinement is applied to many point arrays (every row and column of a
    // surface grid), because building it costs O(M^2) unconditionally — buildRefinementCoefficients
    // starts from a dense M x M identity basis and each insertion runs scaledRowSum across
    // full-length rows. Here it would be built and thrown away after a single application, so
    // that entire M^2 term is pure overhead; refineKnotVector produces the identical result in
    // O(insertions x M).
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
// knotRefinementOperator. The operator's dense-basis build is O(M^2) before a single point is
// touched, which only pays off when the same refinement is reused across many point arrays —
// true for a surface grid's rows and columns, false for one curve. At a 300-point period that is
// the difference between a 316-point window refined in O(insertions x M) and a 903-point window
// costing millions of entry operations.
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
function buildPeriodicWindow(controlPoints is array, knots is array, degree is number) returns map
{
    const fundamental = extractFundamentalPeriodicCurveData(controlPoints, knots, degree);
    const n = fundamental.n;
    if (n < 1)
    {
        throw "splineRefinementUtils: periodic operations need at least one control point per period, got " ~ n ~ ".";
    }

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

    // Direct sequential insertion rather than knotRefinementOperator — the operator's O(M^2)
    // dense-basis build would be thrown away after one application here. See
    // decomposeIntoSegmentsCore for the same reasoning.
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
    const fundamentalKnots = subArray(knots, degree, degree + n);
    const period = knots[degree + n] - knots[degree];

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

    var foldedRows = makeArray(outputCount, 0);
    for (var outputIndex = 0; outputIndex < outputCount; outputIndex += 1)
    {
        const windowRow = refinement.rows[sliceStart + outputIndex];
        var storedRow = makeArray(n + degree, 0);
        for (var windowIndex = 0; windowIndex < windowPointCount; windowIndex += 1)
        {
            if (abs(windowRow[windowIndex]) > SPARSE_WEIGHT_CUTOFF)
            {
                const shifted = windowIndex - marginPoints;
                const cycle = floor(shifted / n);
                const storedIndex = shifted - cycle * n;
                storedRow[storedIndex] = storedRow[storedIndex] + windowRow[windowIndex];
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
 * Parameters to insert to grow a direction by `numToInsert` control points, choosing widest
 * spans. The periodic chooser additionally considers the WRAP span, which a plain array of
 * fundamental knots does not represent. Growing a STORED periodic direction by k control points
 * grows its period by k too (stored count is n + degree), so callers pass the same
 * target-minus-current either way.
 */
function directionWidestSpanInsertions(knots is array, degree is number, isPeriodic is boolean, numToInsert is number) returns array
{
    if (!isPeriodic)
    {
        return widestSpanMidpointInsertions(knots, numToInsert);
    }
    const n = size(knots) - 2 * degree - 1;
    return widestPeriodicSpanMidpointInsertions(subArray(knots, degree, degree + n), knots[degree + n] - knots[degree], numToInsert);
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

    // Periodic input: convert to the canonical STORED periodic form — n + degree control
    // points (the degree-sized overlap tail included) with n + 2*degree + 1 periodic-padded
    // knots — preserving periodicity. Exactly two input forms have defined semantics (they are
    // the two overlap conventions bSplineCurve itself accepts, "degree or 0 overlapping
    // control points"); each is recognized by exact counting and converted exactly. Anything
    // else throws with the observed shape rather than guessing. (An earlier version ported
    // std editCurve.fs's cleanUpPeriodicBSplineDefinition heuristics here, keyed partly on
    // knots[0] != 0 — but every canonical periodic curve on a [0, 1] domain has knots[0] < 0
    // by construction of the padding, so that heuristic fired on perfectly canonical input and
    // destroyed it. Kernel-quirk forms, if they ever reach this module, should surface loudly
    // through the throw below so they can be handled exactly, not silently reinterpreted.)
    if (normalized.isPeriodic == true)
    {
        const degree = normalized.degree;
        const pointCount = size(normalized.controlPoints);
        const knotCount = size(normalized.knots);
        if (knotCount == pointCount + degree + 1 && pointCount > degree)
        {
            // Already the stored form. Rebuild the outer padding from the domain knots (the
            // only slots every producer agrees on) rather than trusting it: kernel-returned
            // periodic curves can carry patched or arbitrary values in the outer pad slots,
            // and everything downstream in this module derives structure from the fundamental
            // knots anyway. Exactly idempotent for already-canonical input.
            const n = pointCount - degree;
            const fundamentalKnots = subArray(normalized.knots, degree, degree + n);
            const period = normalized.knots[degree + n] - normalized.knots[degree];
            normalized.knots = buildPeriodicKnotArray(fundamentalKnots, period, degree, n + 2 * degree + 1);
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
    // Direct insertion, not the operator — one point array, so the operator's O(M^2) dense-basis
    // build would be discarded after a single application (see decomposeIntoSegmentsCore).
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
 * The resulting domain is [-domainEnd, -domainStart]. That is fine for every consumer here:
 * makeSplinesShareKnotVector remaps to a canonical domain before merging on both its clamped
 * and periodic branches, so absolute knot values never need to be comparable across splines.
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
    return result;
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
    var seamIndex = -1;
    for (var knotIndex = 0; knotIndex < n; knotIndex += 1)
    {
        if (abs(fundamental.fundamentalKnots[knotIndex] - wrappedSeam) <= KNOT_PARAMETER_TOLERANCE)
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
// Layer 3 — SURFACE HOOKS. Still stubbed: they need the tensor (grid) generalization of the
// curve-level helpers just above, which now all exist and are ready to reuse. See each note.
// A hook is done when its tester vector passes in Onshape.
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

    if (normalized.isUPeriodic == true)
    {
        // U is the ROW direction: the overlap tail is `uDegree` extra ROWS.
        const rowCount = size(normalized.controlPoints);
        const uKnotCount = size(normalized.uKnots);
        if (uKnotCount == rowCount + normalized.uDegree + 1)
        {
            normalized.uKnots = rebuildPeriodicKnotPadding(normalized.uKnots, normalized.uDegree, rowCount - normalized.uDegree);
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
            normalized.vKnots = rebuildPeriodicKnotPadding(normalized.vKnots, normalized.vDegree, columnCount - normalized.vDegree);
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
    var normalized = normalizeSurfaceDefinition(surface);
    var homogeneousGrid = combineSurfaceControlPointsAndWeights(normalized.controlPoints, normalized.weights);

    if (size(normalized.controlPoints) < targetUCount)
    {
        const isPeriodic = normalized.isUPeriodic == true;
        const insertions = directionWidestSpanInsertions(normalized.uKnots, normalized.uDegree, isPeriodic,
                targetUCount - size(normalized.controlPoints));
        const uOperator = directionRefinementOperator(normalized.uKnots, normalized.uDegree, isPeriodic, insertions);
        homogeneousGrid = applyKnotRefinementOperatorDownColumns(uOperator, homogeneousGrid);
        normalized.uKnots = knotArray(uOperator.knots);
    }
    if (size(normalized.controlPoints[0]) < targetVCount)
    {
        const isPeriodic = normalized.isVPeriodic == true;
        const insertions = directionWidestSpanInsertions(normalized.vKnots, normalized.vDegree, isPeriodic,
                targetVCount - size(normalized.controlPoints[0]));
        const vOperator = directionRefinementOperator(normalized.vKnots, normalized.vDegree, isPeriodic, insertions);
        homogeneousGrid = applyKnotRefinementOperatorAcrossRows(vOperator, homogeneousGrid);
        normalized.vKnots = knotArray(vOperator.knots);
    }

    const separated = separateSurfaceControlPointsAndWeights(homogeneousGrid);
    normalized.controlPoints = separated.points;
    normalized.weights = separated.weights;
    return normalized;
}

/**
 * Guarantee at least minimumControlPointsPerSpan control points across every span of the given
 * parameter partitions, refining only where the requirement is not met — the targeted
 * refinement a lattice-driven FFD wants (spec section 9.2).
 *
 * HOOK(refineSurfaceToSpanDensity) — normalizeSurfaceDefinition + the row-wise combine/separate
 * helpers above now exist, so this is: for each direction, for each partition cell count
 * existing DISTINCT interior knots strictly inside the cell (interiorKnotRun's counting
 * pattern, scoped to a sub-range); if below the requirement, insert evenly-spaced NEW
 * midpoints inside just that cell (safe: the cell was just confirmed low-density). One
 * refinementOperator per direction, applied via the tensor appliers as above.
 */
export function refineSurfaceToSpanDensity(surface is map, uSpanBoundaries is array, vSpanBoundaries is array, minimumControlPointsPerSpan is number) returns map
{
    throw NOT_IMPLEMENTED_MESSAGE ~ "refineSurfaceToSpanDensity";
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
 * HOOK(decomposeSurfaceIntoBezierPatches) — normalizeSurfaceDefinition now exists to start
 * from. Build a grid-aware sibling of decomposeIntoSegmentsCore that takes an isDownColumns
 * flag and calls applyKnotRefinementOperatorDownColumns/AcrossRows instead of
 * applyKnotRefinementOperator; slicing along a row direction is a direct subArray of the outer
 * grid (same as the flat case), slicing along a column direction needs a per-row subArray. Run
 * once per direction, nesting the second pass inside each first-pass region. Returns a 2D array
 * of patches, each { "controlPoints", "weights", "uDegree", "vDegree", "uDomainStart/End",
 * "vDomainStart/End" }.
 */
export function decomposeSurfaceIntoBezierPatches(surface is map) returns array
{
    throw NOT_IMPLEMENTED_MESSAGE ~ "decomposeSurfaceIntoBezierPatches";
}

/**
 * Exact sub-surface over a parameter rectangle — clampedSegmentOperator per direction,
 * tensor-applied. The general form of the displacement map's tile extraction; the entry point
 * flattening and local-patch work call (spec section 9.3).
 *
 * HOOK(extractSubSurface) — normalizeSurfaceDefinition + combine/separateSurfaceControlPointsAndWeights
 * now exist. Build clampedSegmentOperator for [uStart, uEnd] on uKnots and [vStart, vEnd] on
 * vKnots; applyKnotRefinementOperatorDownColumns then AcrossRows; return a full clamped
 * surface map (piece knot vectors come from the refinementOperators, cast via knotArray(...)).
 * Tester: piece evaluates identically to the parent across its rectangle
 * (evaluateBSplineSurfacePoint both sides).
 */
export function extractSubSurface(surface is map, uStart is number, uEnd is number, vStart is number, vEnd is number) returns map
{
    throw NOT_IMPLEMENTED_MESSAGE ~ "extractSubSurface";
}

/**
 * Surface form of prepareSplineForDeformation: elevate both directions, then refine both
 * directions. This is step 2 of the deformation pipeline (spec section 9.1); the adaptive
 * tolerance loop of section 9.1.1 drives the counts and lives in the calling feature.
 *
 * HOOK(prepareSurfaceForDeformation) — composition of elevateSurfaceDegrees and
 * refineSurfaceToControlPointCounts, in that order; both now exist.
 */
export function prepareSurfaceForDeformation(surface is map, targetUDegree is number, targetVDegree is number, targetUCount is number, targetVCount is number) returns map
{
    throw NOT_IMPLEMENTED_MESSAGE ~ "prepareSurfaceForDeformation";
}
