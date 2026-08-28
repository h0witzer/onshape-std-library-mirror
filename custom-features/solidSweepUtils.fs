FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
// geometry.fs, not just common.fs, and the imprint is why: `ProjectionType` is re-exported by
// projectCurves.fs and splitpart.fs alone, so `ProjectionType.NORMAL_TO_TARGET` in
// imprintContactWires does not resolve under common.fs. `import(path : "onshape/std/geometry.fs")`
// is what a Feature Studio opens with, and it is the cheapest way to stop guessing which std
// element re-exports a given enum.
import(path : "onshape/std/geometry.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0");
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");

import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs

/**
 * SOLID SWEEP - the whole library, one element (spec: docs/specs/SOLID_SWEEP_SPEC.md).
 *
 * Motion, the envelope function, the funnel solver, orientation, the degeneracy detectors,
 * extraction, fitting, and emission. No test code and no fixtures live here: those are in
 * solidSweepTester.fs, which imports this element.
 *
 * Units contract, uniform across the file: every spline stored or passed between these
 * functions is unit-stripped - control points are plain numbers with meters implied - and every
 * residual and tolerance is a plain number in meters. Units are attached only where a kernel
 * call demands them, at emission and at the ev-call boundary.
 *
 * Assembled by tools/consolidateSweepStack.py from the modules the stack was developed in; edit
 * this file directly from here on.
 */


// ============================= Enumerations =============================

/**
 * Selectable frame source for roll-free motion builds. AUTOMATIC (double reflection) is the
 * default; KERNEL_SWEEP samples the kernel sweeper's ribbon frames instead and exists as an
 * A/B diagnostic - both produce the same A(t), since a constant frame twist conjugates out
 * of S(t) * S(0)^T.
 */
export enum MotionFrameSource
{
    annotation { "Name" : "Automatic (double reflection)" }
    AUTOMATIC,
    annotation { "Name" : "Kernel sweep frames (diagnostic)" }
    KERNEL_SWEEP
}

/**
 * The kinds this module solves in closed form. Deliberately its own enum rather than
 * extraction's SweepSurfaceClass: the five parameter-driven kinds are typechecked off the
 * evSurfaceDefinition value itself.
 *
 * REVOLVED and EXTRUDED are the two PROFILE-driven kinds (spec 6.5.1). They carry no shape in
 * evSurfaceDefinition, so their frames are not built by stripAnalyticSurface but by
 * analyticProfileFrame from a generator recovered at extraction, and they are the only two kinds
 * whose closed forms evaluate a curve.
 */
export enum AnalyticSurfaceKind
{
    PLANE,
    CYLINDER,
    CONE,
    SPHERE,
    TORUS,
    REVOLVED,
    EXTRUDED
}

/** Classification of a tool face's underlying surface, driving which solver path it takes. */
export enum SweepSurfaceClass
{
    PLANE,
    CYLINDER,
    CONE,
    SPHERE,
    TORUS,
    REVOLVED,
    EXTRUDED,
    BSPLINE,
    OTHER
}

/** Classification of a tool edge's underlying curve, driving which solver path it takes. */
export enum SweepCurveClass
{
    LINE,
    CIRCLE,
    ELLIPSE,
    BSPLINE,
    OTHER
}


// ============================= Constants and tolerances =============================

// The angle above which two consecutive path edges are declared tangent-DIScontinuous.
const PATH_TANGENT_BREAK_THRESHOLD = 0.1 * degree;

// Hard ceiling on station densification (build cost guard).
const MAXIMUM_STATION_COUNT = 260;

// Default certified drift ceiling. Budget-derived: drift contributes about drift * toolRadius
// of position error, so 1e-6 keeps the motion's share around 1e-7 m for a 100 mm tool - an
// order under the fit tolerance. The floor achievable on a given path is set by the
// evPathTangentLines samples, which every frame source shares.
const DEFAULT_DRIFT_TOLERANCE = 1e-6;

// A rung whose drift improvement falls below the h^4-predicted improvement divided by this
// factor means the build is at the shared sampling floor - stop densifying and report
// honestly.
const PLATEAU_IMPROVEMENT_FACTOR = 4;

/**
 * The relative floor below which a value coming out of the coefficient path carries no sign:
 * one block's f is a twelve-term sum of degree-elevated grid products, so cancellation there
 * costs a few thousand machine epsilons of the terms' own magnitude. Four orders above that,
 * and - measured on the spec 6.7 fixture - ten orders below a real block's |f| range.
 */
export const ENVELOPE_RELATIVE_SIGN_TOLERANCE = 1e-12;

/**
 * The relative floor under which f_t on a section carries no sign. f_t is a four-vector
 * inner-product sum through the rational surface evaluators, so cancellation there costs a few
 * thousand machine epsilons of its own terms' magnitude; this sits three orders above that.
 */
export const SECTION_TIME_DERIVATIVE_RELATIVE_FLOOR = 1e-12;

/**
 * Relative spread below which a weight grid counts as uniform, and absolute spread (meters
 * implied) below which a boundary control row counts as collapsed to a point. Both are read
 * off exact structure - a revolve's pole row is byte-identical, a non-rational net's weights
 * are exactly one - so the thresholds only have to survive arithmetic noise.
 */
export const UNIFORM_WEIGHT_TOLERANCE = 1e-12;

export const DEGENERATE_ROW_TOLERANCE = 1e-12;

/**
 * Default polyline and join tolerances, as fractions of the smaller uv domain span. The
 * polyline fraction sits two orders of magnitude below one cell of a nine-node census grid, so
 * a mask decision never turns on the chord approximation. The join fraction is the ceiling on
 * how far two independently inverted pcurve ends of the same vertex may land apart before the
 * chain is reported open.
 */
export const TRIM_POLYLINE_TOLERANCE_FRACTION = 1e-3;

export const TRIM_JOIN_TOLERANCE_FRACTION = 1e-5;

/** Upper bound on samples per knot span, so a pathological trim curve stops rather than spins. */
export const TRIM_POLYLINE_SAMPLE_CAP = 256;

/**
 * The relative floor that separates a decisive cap-face classification from a grazing one:
 * |<n, v>| under this times the sampled point's speed is not a sign, it is noise.
 */
export const CAP_FACE_SIGN_RELATIVE_FLOOR = 1e-6;

/** Parameter rings tried, centre outward, when looking for a sample inside a trimmed face. */
export const CAP_INTERIOR_SAMPLE_RINGS = 2;

/** Points sampled along a seam when measuring the knit slop. */
export const SEAM_GAP_SAMPLE_COUNT = 32;

/**
 * Point-inversion settings for FIT CERTIFICATION, which measures a distance and never reads the
 * parameter back.
 *
 * The tolerance is 1e-9 and the reason is worth stating, because the first attempt at this used
 * 1e-6 and MEASURABLY MOVED THE ANSWER - the reported fit deviation went from 4.638e-8 to
 * 1.622e-7, caught by reading the build's own notices rather than its clock.
 *
 * The bad argument was: at a closest point the distance is stationary in the parameter, so a
 * parameter error e costs only (e |S_u|)^2 / 2R with R the surface's radius. The right one uses
 * the DISTANCE, not the radius. For a target lying essentially on the surface at distance d, the
 * distance function along the surface is sqrt(d^2 + s^2) ~ d + s^2 / 2d, so the error is
 * s^2 / 2d - and d here is 5e-8, not 3e-2. Six orders of magnitude of denominator, which is
 * exactly the gap between the prediction and what the run reported.
 *
 * So the requirement is s << d, not s << R: with |S_u| ~ 0.18 the parameter step must stay well
 * under 4e-7, and 1e-9 leaves an error near 3.5e-13 - three orders under the deviation being
 * measured and eight under the tolerance it is checked against.
 */
export const CERTIFICATION_INVERSION = { "parameterTolerance" : 1e-9 };


// ============================= Bernstein coefficient arithmetic (spec 6.0) =============================

/**
 * Evaluates a Bernstein polynomial by de Casteljau's algorithm.
 * Input: `coefficients` (array of numbers, size >= 1), `parameter` (number, domain [0, 1]).
 * Output: the polynomial's value (number).
 */
export function evaluateBernstein(coefficients is array, parameter is number) returns number
{
    const lastIndex = size(coefficients) - 1;
    var working = coefficients;
    for (var step = 1; step <= lastIndex; step += 1)
    {
        for (var index = 0; index <= lastIndex - step; index += 1)
        {
            working[index] = (1 - parameter) * working[index] + parameter * working[index + 1];
        }
    }
    return working[0];
}

/**
 * Multiplies two Bernstein polynomials. The product of degree-m and degree-n polynomials is
 * the degree-(m+n) polynomial with coefficients given by the binomial convolution.
 * Input: two coefficient arrays. Output: the product's coefficient array (size m + n + 1).
 */
export function multiplyBernstein(coefficientsA is array, coefficientsB is array) returns array
{
    const degreeA = size(coefficientsA) - 1;
    const degreeB = size(coefficientsB) - 1;
    if (degreeA < 0 || degreeB < 0)
    {
        throw "solidSweepUtils bernstein: multiplyBernstein requires non-empty coefficient arrays.";
    }
    const productDegree = degreeA + degreeB;
    // Binomial weights are folded into the inputs once, so the convolution's inner loop is a
    // bare multiply-add; the product row divides them back out.
    const rowProduct = binomialRow(productDegree);
    const weightedA = weightByBinomialRow(coefficientsA, binomialRow(degreeA));
    const weightedB = weightByBinomialRow(coefficientsB, binomialRow(degreeB));
    var product = makeArray(productDegree + 1, 0);
    for (var k = 0; k <= productDegree; k += 1)
    {
        const iStart = max(0, k - degreeB);
        const iEnd = min(degreeA, k);
        var accumulated = 0;
        for (var i = iStart; i <= iEnd; i += 1)
        {
            accumulated += weightedA[i] * weightedB[k - i];
        }
        product[k] = accumulated / rowProduct[k];
    }
    return product;
}

/**
 * Raises a Bernstein polynomial's degree by one without changing its values.
 * Input: coefficient array of degree n. Output: coefficient array of degree n + 1.
 */
export function elevateBernsteinOnce(coefficients is array) returns array
{
    const oldDegree = size(coefficients) - 1;
    const newDegree = oldDegree + 1;
    var elevated = makeArray(newDegree + 1, 0);
    elevated[0] = coefficients[0];
    elevated[newDegree] = coefficients[oldDegree];
    for (var index = 1; index <= oldDegree; index += 1)
    {
        const blend = index / newDegree;
        elevated[index] = blend * coefficients[index - 1] + (1 - blend) * coefficients[index];
    }
    return elevated;
}

/**
 * Raises a Bernstein polynomial to the given target degree (values unchanged).
 * Input: coefficient array, `targetDegree` >= current degree. Output: elevated array.
 */
export function elevateBernstein(coefficients is array, targetDegree is number) returns array
{
    if (targetDegree < size(coefficients) - 1)
    {
        throw "solidSweepUtils bernstein: cannot elevate degree " ~ (size(coefficients) - 1) ~
            " down to " ~ targetDegree ~ ".";
    }
    var elevated = coefficients;
    while (size(elevated) - 1 < targetDegree)
    {
        elevated = elevateBernsteinOnce(elevated);
    }
    return elevated;
}

/**
 * Adds two Bernstein polynomials, elevating the lower-degree one as needed.
 * Input: two coefficient arrays. Output: the sum's coefficient array.
 */
export function addBernstein(coefficientsA is array, coefficientsB is array) returns array
{
    const commonDegree = max(size(coefficientsA), size(coefficientsB)) - 1;
    var elevatedA = elevateBernstein(coefficientsA, commonDegree);
    const elevatedB = elevateBernstein(coefficientsB, commonDegree);
    for (var index = 0; index <= commonDegree; index += 1)
    {
        elevatedA[index] += elevatedB[index];
    }
    return elevatedA;
}

/**
 * Multiplies every coefficient by a scalar factor.
 */
export function scaleBernstein(coefficients is array, factor is number) returns array
{
    var scaled = coefficients;
    for (var index = 0; index < size(scaled); index += 1)
    {
        scaled[index] *= factor;
    }
    return scaled;
}

/**
 * Subtracts the second Bernstein polynomial from the first (with elevation as needed).
 */
export function subtractBernstein(coefficientsA is array, coefficientsB is array) returns array
{
    return addBernstein(coefficientsA, scaleBernstein(coefficientsB, -1));
}

/**
 * The constant polynomial `value` expressed at the given degree.
 */
export function constantBernstein(value is number, degree is number) returns array
{
    return makeArray(degree + 1, value);
}

/**
 * Differentiates a Bernstein polynomial: degree n in, degree n - 1 out
 * (coefficients n * (c[i+1] - c[i])). A constant differentiates to the degree-0 zero.
 */
export function differentiateBernstein(coefficients is array) returns array
{
    const degree = size(coefficients) - 1;
    if (degree == 0)
    {
        return [0];
    }
    var derivative = makeArray(degree, 0);
    for (var index = 0; index < degree; index += 1)
    {
        derivative[index] = degree * (coefficients[index + 1] - coefficients[index]);
    }
    return derivative;
}

/**
 * Splits a Bernstein polynomial at `parameter` by de Casteljau subdivision.
 * Input: coefficient array, split parameter in (0, 1).
 * Output: map { left, right } - `left` reproduces the original on [0, parameter] and `right`
 * on [parameter, 1], each re-parameterized to its own [0, 1].
 */
export function subdivideBernstein(coefficients is array, parameter is number) returns map
{
    const lastIndex = size(coefficients) - 1;
    var working = coefficients;
    var left = makeArray(lastIndex + 1, 0);
    var right = makeArray(lastIndex + 1, 0);
    left[0] = working[0];
    right[lastIndex] = working[lastIndex];
    for (var step = 1; step <= lastIndex; step += 1)
    {
        for (var index = 0; index <= lastIndex - step; index += 1)
        {
            working[index] = (1 - parameter) * working[index] + parameter * working[index + 1];
        }
        left[step] = working[0];
        right[lastIndex - step] = working[lastIndex - step];
    }
    return { "left" : left, "right" : right };
}

/**
 * The coefficient range. By the convex-hull property the polynomial's values on [0, 1] lie
 * inside [minimum, maximum].
 * Output: map { minimum, maximum }.
 */
export function bernsteinRange(coefficients is array) returns map
{
    var minimum = coefficients[0];
    var maximum = coefficients[0];
    for (var index = 1; index < size(coefficients); index += 1)
    {
        minimum = min(minimum, coefficients[index]);
        maximum = max(maximum, coefficients[index]);
    }
    return { "minimum" : minimum, "maximum" : maximum };
}

/**
 * True when the convex-hull property proves the polynomial cannot come within
 * `valueTolerance` of zero anywhere on [0, 1] - the instant patch-rejection test.
 */
export function bernsteinExcludesZero(coefficients is array, valueTolerance is number) returns boolean
{
    const range = bernsteinRange(coefficients);
    return range.minimum > valueTolerance || range.maximum < -valueTolerance;
}

/**
 * Isolates the roots of a Bernstein polynomial on [0, 1] by deterministic bisection with
 * convex-hull rejection - no sampling, no derivatives required.
 * Input: coefficient array; `valueTolerance` (coefficient magnitude treated as zero);
 * `intervalTolerance` (stop refining below this interval width).
 * Output: array of maps { start, end } - disjoint intervals, each containing at least one
 * point where the convex hull cannot exclude a root, refined to at most roughly
 * intervalTolerance width (adjacent hits are merged, so a root sitting exactly on a
 * subdivision point reports one interval up to twice that width).
 * Throws when every coefficient is within `valueTolerance` of zero (the identically-zero /
 * sliding case - callers must audit for it first).
 */
export function isolateBernsteinRoots(coefficients is array, valueTolerance is number, intervalTolerance is number) returns array
{
    var allNearZero = true;
    for (var index = 0; index < size(coefficients); index += 1)
    {
        if (abs(coefficients[index]) > valueTolerance)
        {
            allNearZero = false;
        }
    }
    if (allNearZero)
    {
        throw "solidSweepUtils bernstein: isolateBernsteinRoots got an identically-zero polynomial " ~
            "(all coefficients within " ~ valueTolerance ~ " of zero) - audit for the sliding case first.";
    }

    const rawIntervals = collectBernsteinRootIntervals(coefficients, 0, 1, valueTolerance, intervalTolerance, 60);

    // Merge intervals that touch (a root on a subdivision point flags both halves).
    if (size(rawIntervals) == 0)
    {
        return [];
    }
    var merged = [];
    var current = rawIntervals[0];
    for (var index = 1; index < size(rawIntervals); index += 1)
    {
        const candidate = rawIntervals[index];
        if (candidate.start <= current.end + intervalTolerance * 0.01)
        {
            current.end = candidate.end;
        }
        else
        {
            merged = append(merged, current);
            current = candidate;
        }
    }
    merged = append(merged, current);
    return merged;
}

/**
 * Evaluates a vector-valued Bernstein polynomial ([x, y, z] coefficient arrays).
 * Output: array of three numbers.
 */
export function evaluateBernsteinVector(vectorCoefficients is array, parameter is number) returns array
{
    return [
            evaluateBernstein(vectorCoefficients[0], parameter),
            evaluateBernstein(vectorCoefficients[1], parameter),
            evaluateBernstein(vectorCoefficients[2], parameter)
        ];
}

/**
 * Dot product of two vector-valued Bernstein polynomials.
 * Output: one scalar coefficient array.
 */
export function dotBernsteinVectors(vectorA is array, vectorB is array) returns array
{
    return addBernstein(
        addBernstein(
            multiplyBernstein(vectorA[0], vectorB[0]),
            multiplyBernstein(vectorA[1], vectorB[1])),
        multiplyBernstein(vectorA[2], vectorB[2]));
}

/**
 * Cross product of two vector-valued Bernstein polynomials.
 * Output: a vector-valued polynomial (array of three coefficient arrays).
 */
export function crossBernsteinVectors(vectorA is array, vectorB is array) returns array
{
    return [
            subtractBernstein(multiplyBernstein(vectorA[1], vectorB[2]), multiplyBernstein(vectorA[2], vectorB[1])),
            subtractBernstein(multiplyBernstein(vectorA[2], vectorB[0]), multiplyBernstein(vectorA[0], vectorB[2])),
            subtractBernstein(multiplyBernstein(vectorA[0], vectorB[1]), multiplyBernstein(vectorA[1], vectorB[0]))
        ];
}

/**
 * Evaluates a bivariate Bernstein grid (rows = u, columns = v) by de Casteljau in each
 * direction. Output: the polynomial's value (number).
 */
export function evaluateBernsteinGrid(grid is array, uParameter is number, vParameter is number) returns number
{
    const rowCount = size(grid);
    var columnValues = makeArray(rowCount, 0);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        columnValues[rowIndex] = evaluateBernstein(grid[rowIndex], vParameter);
    }
    return evaluateBernstein(columnValues, uParameter);
}

/**
 * Multiplies two bivariate Bernstein grids (tensor binomial convolution).
 * Output: grid of degree (uA + uB, vA + vB).
 */
export function multiplyBernsteinGrids(gridA is array, gridB is array) returns array
{
    checkBernsteinGrid(gridA);
    checkBernsteinGrid(gridB);
    const uDegreeA = size(gridA) - 1;
    const vDegreeA = size(gridA[0]) - 1;
    const uDegreeB = size(gridB) - 1;
    const vDegreeB = size(gridB[0]) - 1;
    const uDegree = uDegreeA + uDegreeB;
    const vDegree = vDegreeA + vDegreeB;

    // Native formulation: binomial weighting on and off is elementwise multiplication by
    // outer-product masks; the v-direction convolution against each weighted row of A is a
    // matrix product with that row's Toeplitz matrix; the u-direction shift-and-sum is a
    // native matrix sum of row-offset embeddings. Interpreted work is confined to assembling
    // the small operator matrices.
    const weightedA = cwiseProduct(gridA as Matrix,
        outerProductMatrix(binomialRow(uDegreeA), binomialRow(vDegreeA)));
    const weightedB = cwiseProduct(gridB as Matrix,
        outerProductMatrix(binomialRow(uDegreeB), binomialRow(vDegreeB)));

    const outputColumnCount = vDegree + 1;
    const zeroRow = makeArray(outputColumnCount, 0);
    var accumulated = undefined;
    for (var i = 0; i <= uDegreeA; i += 1)
    {
        const weightedRowA = weightedA[i];
        var toeplitzRows = makeArray(vDegreeB + 1);
        for (var j = 0; j <= vDegreeB; j += 1)
        {
            var toeplitzRow = makeArray(outputColumnCount, 0);
            for (var jj = 0; jj <= vDegreeA; jj += 1)
            {
                toeplitzRow[j + jj] = weightedRowA[jj];
            }
            toeplitzRows[j] = toeplitzRow;
        }
        const convolvedRows = weightedB * (toeplitzRows as Matrix);
        var shiftedRows = makeArray(uDegree + 1);
        for (var rowIndex = 0; rowIndex <= uDegree; rowIndex += 1)
        {
            shiftedRows[rowIndex] = (rowIndex >= i && rowIndex <= i + uDegreeB) ?
                convolvedRows[rowIndex - i] : zeroRow;
        }
        accumulated = accumulated == undefined ? (shiftedRows as Matrix) :
            accumulated + (shiftedRows as Matrix);
    }
    return cwiseProduct(accumulated,
        outerProductMatrix(reciprocalRow(binomialRow(uDegree)), reciprocalRow(binomialRow(vDegree))));
}

/**
 * Elevates a grid to the given target degrees (values unchanged).
 */
export function elevateBernsteinGrid(grid is array, targetUDegree is number, targetVDegree is number) returns array
{
    checkBernsteinGrid(grid);
    // Elevation is a linear operator per direction, applied as native matrix products:
    // rows from the left, columns via the transposed operator from the right.
    const uDegree = size(grid) - 1;
    const vDegree = size(grid[0]) - 1;
    if (uDegree >= targetUDegree && vDegree >= targetVDegree)
    {
        return grid;
    }
    var elevated = grid as Matrix;
    if (uDegree < targetUDegree)
    {
        elevated = (bernsteinElevationMatrix(uDegree, targetUDegree) as Matrix) * elevated;
    }
    if (vDegree < targetVDegree)
    {
        elevated = elevated * (bernsteinElevationMatrixTransposed(vDegree, targetVDegree) as Matrix);
    }
    return elevated;
}

/**
 * Adds two grids, elevating each to the common degrees as needed.
 */
export function addBernsteinGrids(gridA is array, gridB is array) returns array
{
    checkBernsteinGrid(gridA);
    checkBernsteinGrid(gridB);
    const uDegree = max(size(gridA), size(gridB)) - 1;
    const vDegree = max(size(gridA[0]), size(gridB[0])) - 1;
    return (elevateBernsteinGrid(gridA, uDegree, vDegree) as Matrix) +
        (elevateBernsteinGrid(gridB, uDegree, vDegree) as Matrix);
}

/**
 * The weighted sum of same-degree grids in ONE pass:
 * result[r][c] = sum_k weights[k] * grids[k][r][c]. Zero-weight terms are skipped; an
 * all-zero weight vector yields the zero grid at the shared degrees. This is the
 * accumulation kernel for materializing factored envelope blocks - a scale-then-add chain
 * allocates an intermediate grid and re-walks every cell per term, where this walks each
 * output row once with single-level reads and writes.
 */
export function accumulateScaledBernsteinGrids(grids is array, weights is array) returns array
{
    if (size(grids) == 0 || size(grids) != size(weights))
    {
        throw "solidSweepUtils bernstein: accumulateScaledBernsteinGrids needs equally many grids and weights (nonzero count).";
    }
    const rowCount = size(grids[0]);
    const columnCount = size(grids[0][0]);
    var liveGrids = [];
    var liveWeights = [];
    for (var termIndex = 0; termIndex < size(grids); termIndex += 1)
    {
        if (weights[termIndex] != 0)
        {
            if (size(grids[termIndex]) != rowCount || size(grids[termIndex][0]) != columnCount)
            {
                throw "solidSweepUtils bernstein: accumulateScaledBernsteinGrids requires one shared degree pair " ~
                    "(grid 0 is " ~ (rowCount - 1) ~ "x" ~ (columnCount - 1) ~ ", grid " ~ termIndex ~ " is " ~
                    (size(grids[termIndex]) - 1) ~ "x" ~ (size(grids[termIndex][0]) - 1) ~ ").";
            }
            liveGrids = append(liveGrids, grids[termIndex]);
            liveWeights = append(liveWeights, weights[termIndex]);
        }
    }
    const liveCount = size(liveGrids);
    var accumulated = makeArray(rowCount);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        var row = makeArray(columnCount, 0);
        for (var termIndex = 0; termIndex < liveCount; termIndex += 1)
        {
            const weight = liveWeights[termIndex];
            const sourceRow = liveGrids[termIndex][rowIndex];
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
            {
                row[columnIndex] += weight * sourceRow[columnIndex];
            }
        }
        accumulated[rowIndex] = row;
    }
    return accumulated;
}

/**
 * Multiplies every grid coefficient by a scalar factor.
 */
export function scaleBernsteinGrid(grid is array, factor is number) returns array
{
    return (grid as Matrix) * factor;
}

/**
 * Subtracts the second grid from the first (with elevation as needed).
 */
export function subtractBernsteinGrids(gridA is array, gridB is array) returns array
{
    checkBernsteinGrid(gridA);
    checkBernsteinGrid(gridB);
    const uDegree = max(size(gridA), size(gridB)) - 1;
    const vDegree = max(size(gridA[0]), size(gridB[0])) - 1;
    return (elevateBernsteinGrid(gridA, uDegree, vDegree) as Matrix) -
        (elevateBernsteinGrid(gridB, uDegree, vDegree) as Matrix);
}

/**
 * The constant bivariate polynomial `value` at the given degrees.
 */
export function constantBernsteinGrid(value is number, uDegree is number, vDegree is number) returns array
{
    var grid = makeArray(uDegree + 1);
    for (var rowIndex = 0; rowIndex <= uDegree; rowIndex += 1)
    {
        grid[rowIndex] = makeArray(vDegree + 1, value);
    }
    return grid;
}

/**
 * Partial derivative in u (across rows): degree (m, n) in, degree (m - 1, n) out.
 */
export function differentiateBernsteinGridU(grid is array) returns array
{
    checkBernsteinGrid(grid);
    const uDegree = size(grid) - 1;
    const columnCount = size(grid[0]);
    if (uDegree == 0)
    {
        return [makeArray(columnCount, 0)];
    }
    var differenceRows = makeArray(uDegree);
    for (var rowIndex = 0; rowIndex < uDegree; rowIndex += 1)
    {
        var row = makeArray(uDegree + 1, 0);
        row[rowIndex] = -uDegree;
        row[rowIndex + 1] = uDegree;
        differenceRows[rowIndex] = row;
    }
    return (differenceRows as Matrix) * (grid as Matrix);
}

/**
 * Partial derivative in v (along each row): degree (m, n) in, degree (m, n - 1) out.
 */
export function differentiateBernsteinGridV(grid is array) returns array
{
    checkBernsteinGrid(grid);
    const vDegree = size(grid[0]) - 1;
    if (vDegree == 0)
    {
        var zeroColumn = makeArray(size(grid));
        for (var rowIndex = 0; rowIndex < size(grid); rowIndex += 1)
        {
            zeroColumn[rowIndex] = [0];
        }
        return zeroColumn;
    }
    var differenceColumns = makeArray(vDegree + 1);
    for (var rowIndex = 0; rowIndex <= vDegree; rowIndex += 1)
    {
        var row = makeArray(vDegree, 0);
        if (rowIndex < vDegree)
        {
            row[rowIndex] = -vDegree;
        }
        if (rowIndex > 0)
        {
            row[rowIndex - 1] = vDegree;
        }
        differenceColumns[rowIndex] = row;
    }
    return (grid as Matrix) * (differenceColumns as Matrix);
}

/**
 * Splits a grid at `parameter` in the u direction.
 * Output: map { low, high } - `low` covers u in [0, parameter], `high` covers [parameter, 1].
 */
export function subdivideBernsteinGridU(grid is array, parameter is number) returns map
{
    checkBernsteinGrid(grid);
    // The de Casteljau split is a linear operator per direction: one native matrix product
    // against each of the left/right operator matrices, assembled row-locally.
    const operators = bernsteinSubdivisionOperators(size(grid) - 1, parameter);
    return {
            "low" : (operators.left as Matrix) * (grid as Matrix),
            "high" : (operators.right as Matrix) * (grid as Matrix)
        };
}

/**
 * Splits a grid at `parameter` in the v direction.
 * Output: map { low, high }.
 */
export function subdivideBernsteinGridV(grid is array, parameter is number) returns map
{
    checkBernsteinGrid(grid);
    const operators = bernsteinSubdivisionOperators(size(grid[0]) - 1, parameter);
    return {
            "low" : (grid as Matrix) * (transposeOperatorRows(operators.left) as Matrix),
            "high" : (grid as Matrix) * (transposeOperatorRows(operators.right) as Matrix)
        };
}

/**
 * The coefficient range of a grid (convex-hull bound for values on the unit square).
 * Output: map { minimum, maximum }.
 */
export function bernsteinGridRange(grid is array) returns map
{
    var minimum = grid[0][0];
    var maximum = grid[0][0];
    for (var rowIndex = 0; rowIndex < size(grid); rowIndex += 1)
    {
        for (var columnIndex = 0; columnIndex < size(grid[rowIndex]); columnIndex += 1)
        {
            minimum = min(minimum, grid[rowIndex][columnIndex]);
            maximum = max(maximum, grid[rowIndex][columnIndex]);
        }
    }
    return { "minimum" : minimum, "maximum" : maximum };
}

/**
 * True when the convex hull proves the bivariate polynomial cannot come within
 * `valueTolerance` of zero anywhere on the unit square.
 */
export function bernsteinGridExcludesZero(grid is array, valueTolerance is number) returns boolean
{
    const range = bernsteinGridRange(grid);
    return range.minimum > valueTolerance || range.maximum < -valueTolerance;
}

/**
 * Evaluates a vector-valued grid ([x, y, z] grids). Output: array of three numbers.
 */
export function evaluateBernsteinVectorGrid(vectorGrid is array, uParameter is number, vParameter is number) returns array
{
    return [
            evaluateBernsteinGrid(vectorGrid[0], uParameter, vParameter),
            evaluateBernsteinGrid(vectorGrid[1], uParameter, vParameter),
            evaluateBernsteinGrid(vectorGrid[2], uParameter, vParameter)
        ];
}

/**
 * Dot product of two vector-valued grids. Output: one scalar grid.
 */
export function dotBernsteinVectorGrids(vectorGridA is array, vectorGridB is array) returns array
{
    return addBernsteinGrids(
        addBernsteinGrids(
            multiplyBernsteinGrids(vectorGridA[0], vectorGridB[0]),
            multiplyBernsteinGrids(vectorGridA[1], vectorGridB[1])),
        multiplyBernsteinGrids(vectorGridA[2], vectorGridB[2]));
}

/**
 * Cross product of two vector-valued grids. Output: a vector-valued grid.
 */
export function crossBernsteinVectorGrids(vectorGridA is array, vectorGridB is array) returns array
{
    return [
            subtractBernsteinGrids(multiplyBernsteinGrids(vectorGridA[1], vectorGridB[2]),
                multiplyBernsteinGrids(vectorGridA[2], vectorGridB[1])),
            subtractBernsteinGrids(multiplyBernsteinGrids(vectorGridA[2], vectorGridB[0]),
                multiplyBernsteinGrids(vectorGridA[0], vectorGridB[2])),
            subtractBernsteinGrids(multiplyBernsteinGrids(vectorGridA[0], vectorGridB[1]),
                multiplyBernsteinGrids(vectorGridA[1], vectorGridB[0]))
        ];
}

/** The full Pascal row [C(n,0) .. C(n,n)] in one pass. */
function binomialRow(n is number) returns array
{
    var row = makeArray(n + 1, 1);
    for (var k = 1; k <= n; k += 1)
    {
        row[k] = row[k - 1] * (n - k + 1) / k;
    }
    return row;
}

/** Each coefficient multiplied by its row weight: result[i] = coefficients[i] * row[i]. */
function weightByBinomialRow(coefficients is array, row is array) returns array
{
    var weighted = makeArray(size(coefficients), 0);
    for (var index = 0; index < size(coefficients); index += 1)
    {
        weighted[index] = coefficients[index] * row[index];
    }
    return weighted;
}

/** The outer product uWeights * vWeightsᵀ as a Matrix, via one native product. */
function outerProductMatrix(uWeights is array, vWeights is array) returns Matrix
{
    var columnRows = makeArray(size(uWeights));
    for (var index = 0; index < size(uWeights); index += 1)
    {
        columnRows[index] = [uWeights[index]];
    }
    return (columnRows as Matrix) * ([vWeights] as Matrix);
}

/** Elementwise reciprocals of a row of nonzero numbers. */
function reciprocalRow(row is array) returns array
{
    var reciprocals = makeArray(size(row), 0);
    for (var index = 0; index < size(row); index += 1)
    {
        reciprocals[index] = 1 / row[index];
    }
    return reciprocals;
}

/**
 * The degree-elevation operator from `fromDegree` to `toDegree` as a
 * (toDegree + 1) x (fromDegree + 1) matrix of rows:
 * E[k][i] = C(from, i) * C(to - from, k - i) / C(to, k).
 */
function bernsteinElevationMatrix(fromDegree is number, toDegree is number) returns array
{
    const rowFrom = binomialRow(fromDegree);
    const rowGap = binomialRow(toDegree - fromDegree);
    const rowTo = binomialRow(toDegree);
    var operatorRows = makeArray(toDegree + 1);
    for (var k = 0; k <= toDegree; k += 1)
    {
        var row = makeArray(fromDegree + 1, 0);
        const iStart = max(0, k - (toDegree - fromDegree));
        const iEnd = min(fromDegree, k);
        for (var i = iStart; i <= iEnd; i += 1)
        {
            row[i] = rowFrom[i] * rowGap[k - i] / rowTo[k];
        }
        operatorRows[k] = row;
    }
    return operatorRows;
}

/** The transpose of bernsteinElevationMatrix, built directly for right-multiplication. */
function bernsteinElevationMatrixTransposed(fromDegree is number, toDegree is number) returns array
{
    const rowFrom = binomialRow(fromDegree);
    const rowGap = binomialRow(toDegree - fromDegree);
    const rowTo = binomialRow(toDegree);
    var operatorRows = makeArray(fromDegree + 1);
    for (var i = 0; i <= fromDegree; i += 1)
    {
        var row = makeArray(toDegree + 1, 0);
        for (var k = i; k <= i + toDegree - fromDegree; k += 1)
        {
            row[k] = rowFrom[i] * rowGap[k - i] / rowTo[k];
        }
        operatorRows[i] = row;
    }
    return operatorRows;
}

/**
 * The de Casteljau subdivision operators at `parameter` for one degree n, as { left, right }:
 * (n + 1)-square lower/upper triangular matrices of rows with
 * left[k][j] = C(k, j) * p^j * (1-p)^(k-j) for j <= k and
 * right[k][j] = C(n-k, j-k) * p^(j-k) * (1-p)^(n-j) for j >= k,
 * so that splitLeft = left * coefficients and splitRight = right * coefficients.
 */
function bernsteinSubdivisionOperators(degree is number, parameter is number) returns map
{
    var parameterPowers = makeArray(degree + 1, 1);
    var complementPowers = makeArray(degree + 1, 1);
    for (var index = 1; index <= degree; index += 1)
    {
        parameterPowers[index] = parameterPowers[index - 1] * parameter;
        complementPowers[index] = complementPowers[index - 1] * (1 - parameter);
    }
    var leftRows = makeArray(degree + 1);
    var rightRows = makeArray(degree + 1);
    for (var k = 0; k <= degree; k += 1)
    {
        const leftBinomials = binomialRow(k);
        const rightBinomials = binomialRow(degree - k);
        var leftRow = makeArray(degree + 1, 0);
        for (var j = 0; j <= k; j += 1)
        {
            leftRow[j] = leftBinomials[j] * parameterPowers[j] * complementPowers[k - j];
        }
        var rightRow = makeArray(degree + 1, 0);
        for (var j = k; j <= degree; j += 1)
        {
            rightRow[j] = rightBinomials[j - k] * parameterPowers[j - k] * complementPowers[degree - j];
        }
        leftRows[k] = leftRow;
        rightRows[k] = rightRow;
    }
    return { "left" : leftRows, "right" : rightRows };
}

/** The transpose of a square operator (array of rows), for right-multiplication. */
function transposeOperatorRows(operatorRows is array) returns array
{
    const count = size(operatorRows);
    var transposed = makeArray(count);
    for (var rowIndex = 0; rowIndex < count; rowIndex += 1)
    {
        var row = makeArray(count, 0);
        for (var columnIndex = 0; columnIndex < count; columnIndex += 1)
        {
            row[columnIndex] = operatorRows[columnIndex][rowIndex];
        }
        transposed[rowIndex] = row;
    }
    return transposed;
}

/**
 * Throws with the violation named unless `grid` is a non-empty rectangular array of rows.
 */
function checkBernsteinGrid(grid is array)
{
    if (size(grid) == 0)
    {
        throw "solidSweepUtils bernstein: empty coefficient grid.";
    }
    const columnCount = size(grid[0]);
    if (columnCount == 0)
    {
        throw "solidSweepUtils bernstein: coefficient grid has an empty row.";
    }
    for (var rowIndex = 1; rowIndex < size(grid); rowIndex += 1)
    {
        if (size(grid[rowIndex]) != columnCount)
        {
            throw "solidSweepUtils bernstein: coefficient grid is not rectangular (row 0 has " ~
                columnCount ~ " columns, row " ~ rowIndex ~ " has " ~ size(grid[rowIndex]) ~ ").";
        }
    }
}

/**
 * Recursive bisection for root isolation: rejects subtrees whose convex hull excludes zero,
 * emits the interval once it is narrower than `intervalTolerance` (or depth runs out).
 */
function collectBernsteinRootIntervals(coefficients is array, globalStart is number, globalEnd is number,
    valueTolerance is number, intervalTolerance is number, depthRemaining is number) returns array
{
    if (bernsteinExcludesZero(coefficients, valueTolerance))
    {
        return [];
    }
    if (globalEnd - globalStart <= intervalTolerance || depthRemaining <= 0)
    {
        return [{ "start" : globalStart, "end" : globalEnd }];
    }
    const split = subdivideBernstein(coefficients, 0.5);
    const midpoint = 0.5 * (globalStart + globalEnd);
    const leftIntervals = collectBernsteinRootIntervals(split.left, globalStart, midpoint,
        valueTolerance, intervalTolerance, depthRemaining - 1);
    const rightIntervals = collectBernsteinRootIntervals(split.right, midpoint, globalEnd,
        valueTolerance, intervalTolerance, depthRemaining - 1);
    return concatenateArrays([leftIntervals, rightIntervals]);
}


// ============================= Motion (spec 4) =============================

/**
 * Builds the motion spline for a path. The only context-using entry point of this module.
 * Input map fields:
 *   pathEdges {Query} : edges forming one open, tangent-continuous path.
 *   keepOrientation {boolean} : true = pure translation (A = identity); false = roll-free
 *       frame transport.
 *   frameSource {MotionFrameSource} : optional, default AUTOMATIC (double reflection);
 *       KERNEL_SWEEP is an A/B diagnostic mode.
 *   initialStationCount {number} : optional, default 33 (grown predictively until drift passes).
 *   driftTolerance {number} : optional, default 1e-6 (unitless, sup |A^T A - I|; position
 *       cost is roughly drift times tool radius).
 * Output: the MotionSpline map described in the header.
 * Throws with the violation named on closed paths, tangent breaks, and scaffold failures.
 */
export function buildMotionSpline(context is Context, id is Id, definition is map) returns map
{
    const constructed = constructPath(context, definition.pathEdges, {});
    const path = constructed.path;
    if (path.closed)
    {
        throw "solidSweepUtils motion: closed paths are not supported yet - pick an open edge chain.";
    }
    checkPathTangentContinuity(context, path);

    const pathLength = evPathLength(context, path);
    const junctionParameters = interiorJunctionParameters(context, path, pathLength);
    const driftTolerance = definition.driftTolerance == undefined ? DEFAULT_DRIFT_TOLERANCE : definition.driftTolerance;
    const initialStationCount = max(5, definition.initialStationCount == undefined ? 33 : definition.initialStationCount);
    var stationCount = initialStationCount;

    var rungCount = 0;
    var perStationEvaluationCalls = 0;
    var batchedTangentCalls = 0;

    if (definition.keepOrientation == true)
    {
        // Pure translation: identity rotation, origins straight off the path. Drift is
        // exactly zero; one pass, no scaffold.
        const stationParameters = mergedStationParameters(stationCount, junctionParameters);
        const tangentResult = evPathTangentLines(context, path, stationParameters);
        const stationSamples = keepOrientationSamples(tangentResult.tangentLines);
        var motion = assembleMotionSpline(stationSamples, stationParameters, junctionParameters,
            path, pathLength, true, driftTolerance);
        motion.frameSource = "IDENTITY";
        motion.buildDiagnostics = {
                "requestedMode" : "IDENTITY",
                "ladderRungs" : 1,
                "finalStationCount" : size(stationParameters),
                "perStationEvaluationCalls" : 0,
                "batchedTangentCalls" : 1
            };
        return motion;
    }

    // Roll-free mode. Double-reflection frames drive densification (one batched tangent call
    // plus pure math per rung). MotionFrameSource.KERNEL_SWEEP instead samples the kernel
    // sweeper's ribbon frames per station (2 ev-calls each) as an A/B diagnostic - both
    // sources produce the same A(t), since a constant frame twist conjugates out of
    // S(t) * S(0)^T.
    const mode = definition.frameSource == undefined ? MotionFrameSource.AUTOMATIC : definition.frameSource;
    var motion;

    if (mode == MotionFrameSource.KERNEL_SWEEP)
    {
        const scratchId = id + "frameScaffold";
        var built = false;
        startFeature(context, scratchId);
        try
        {
            const sweepFaces = buildFrameScaffold(context, scratchId, path);
            var previousDrift = -1;
            while (true)
            {
                const stationParameters = mergedStationParameters(stationCount, junctionParameters);
                const tangentResult = evPathTangentLines(context, path, stationParameters);
                batchedTangentCalls += 1;
                const stationSamples = rollFreeSamples(context, sweepFaces,
                    tangentResult.tangentLines, tangentResult.edgeIndices);
                perStationEvaluationCalls += 2 * size(stationParameters);
                rungCount += 1;
                motion = assembleMotionSpline(stationSamples, stationParameters, junctionParameters,
                    path, pathLength, false, driftTolerance);
                if (motion.orthogonalityDrift <= driftTolerance)
                {
                    break;
                }
                const plateaued = previousDrift > 0 &&
                    motion.orthogonalityDrift * PLATEAU_IMPROVEMENT_FACTOR > previousDrift;
                if (plateaued || stationCount * 2 - 1 > MAXIMUM_STATION_COUNT)
                {
                    break;
                }
                previousDrift = motion.orthogonalityDrift;
                stationCount = stationCount * 2 - 1;
            }
            built = true;
        }
        abortFeature(context, scratchId);
        if (!built)
        {
            throw "solidSweepUtils motion: the frame scaffold failed (helper sweep or frame sampling threw) - " ~
                "see the console for the underlying error.";
        }
        motion.frameSource = "KERNEL_SWEEP";
    }
    else
    {
        var previousDrift = -1;
        var previousStationCount = -1;
        while (true)
        {
            const stationParameters = mergedStationParameters(stationCount, junctionParameters);
            const tangentResult = evPathTangentLines(context, path, stationParameters);
            batchedTangentCalls += 1;
            const stationSamples = doubleReflectionSamples(tangentResult.tangentLines, undefined);
            rungCount += 1;
            motion = assembleMotionSpline(stationSamples, stationParameters, junctionParameters,
                path, pathLength, false, driftTolerance);
            if (motion.orthogonalityDrift <= driftTolerance)
            {
                break;
            }
            // Plateau: the h^4 model predicts (growth ratio)^4 improvement per rung; falling
            // short of a quarter of that means the build is at the shared sampling floor.
            const expectedImprovement = previousStationCount > 0 ?
                ((stationCount / previousStationCount) ^ 4) : 1;
            const plateaued = previousDrift > 0 &&
                PLATEAU_IMPROVEMENT_FACTOR * previousDrift < expectedImprovement * motion.orthogonalityDrift;
            if (plateaued)
            {
                break;
            }
            // Drift scales as h^4, so one rung predicts the station count that meets the
            // tolerance: jump straight there with 20% overshoot, growing at least 30% so
            // progress is guaranteed.
            var nextCount = ceil(1.2 * stationCount * ((motion.orthogonalityDrift / driftTolerance) ^ 0.25));
            nextCount = max(nextCount, ceil(1.3 * stationCount));
            nextCount = min(nextCount, MAXIMUM_STATION_COUNT);
            if (nextCount <= stationCount)
            {
                break;
            }
            previousDrift = motion.orthogonalityDrift;
            previousStationCount = stationCount;
            stationCount = nextCount;
        }
        motion.frameSource = "DOUBLE_REFLECTION";
    }

    if (motion.orthogonalityDrift > driftTolerance)
    {
        throw "solidSweepUtils motion: orthogonality drift " ~ motion.orthogonalityDrift ~ " on " ~
            motion.frameSource ~ " frames still exceeds " ~ driftTolerance ~ " at " ~
            size(motion.stationParameters) ~ " stations. Raise driftTolerance (position cost is " ~
            "roughly drift times tool radius), simplify the path, or try another frame source.";
    }
    motion.buildDiagnostics = {
            "requestedMode" : mode,
            "ladderRungs" : rungCount,
            "finalStationCount" : size(motion.stationParameters),
            "perStationEvaluationCalls" : perStationEvaluationCalls,
            "batchedTangentCalls" : batchedTangentCalls
        };
    return motion;
}

/**
 * Evaluates the motion and its first two derivatives at `t` in [0, 1]. Pure math, no context.
 * Three lean basis+combine evaluations on the packed fit and its difference-net derivative
 * splines (built once at assembly).
 * Output map: columns / columnVelocities / columnAccelerations (arrays of three unitless
 * Vectors - the columns of A, A', A'') and translation / translationVelocity /
 * translationAcceleration (unitless Vectors, meters implied).
 */
export function motionAt(motion is map, t is number) returns map
{
    const position = evaluatePackedPoint(motion.packedSpline, t);
    const velocity = evaluatePackedPoint(motion.packedVelocity, t);
    const acceleration = evaluatePackedPoint(motion.packedAcceleration, t);
    return {
            "columns" : [sliceVector3(position, 0), sliceVector3(position, 3), sliceVector3(position, 6)],
            "columnVelocities" : [sliceVector3(velocity, 0), sliceVector3(velocity, 3), sliceVector3(velocity, 6)],
            "columnAccelerations" : [sliceVector3(acceleration, 0), sliceVector3(acceleration, 3), sliceVector3(acceleration, 6)],
            "translation" : sliceVector3(position, 9),
            "translationVelocity" : sliceVector3(velocity, 9),
            "translationAcceleration" : sliceVector3(acceleration, 9)
        };
}

/**
 * Applies a motion sample to a unitless tool-frame point: A(t) * point + b(t).
 */
export function applyMotion(motionSample is map, toolPoint is Vector) returns Vector
{
    return motionSample.columns[0] * toolPoint[0] +
        motionSample.columns[1] * toolPoint[1] +
        motionSample.columns[2] * toolPoint[2] +
        motionSample.translation;
}

/**
 * The velocity of a tool-frame point under a motion sample: A'(t) * point + b'(t).
 */
export function motionVelocityAt(motionSample is map, toolPoint is Vector) returns Vector
{
    return motionSample.columnVelocities[0] * toolPoint[0] +
        motionSample.columnVelocities[1] * toolPoint[1] +
        motionSample.columnVelocities[2] * toolPoint[2] +
        motionSample.translationVelocity;
}

/**
 * The EXACT trajectory of a fixed tool-frame point as a B-spline curve: control points
 * Q_j = A_j * point + b_j on the motion's own knot vector (valid because all four motion
 * splines share that knot vector and B-spline bases are a partition of unity).
 * Input: the MotionSpline and a unitless point. Output: a non-rational spline map (unitless).
 */
export function trajectoryCurveOf(motion is map, toolPoint is Vector) returns map
{
    const count = size(motion.columnX.controlPoints);
    var trajectoryControlPoints = makeArray(count, vector(0, 0, 0));
    for (var index = 0; index < count; index += 1)
    {
        trajectoryControlPoints[index] = motion.columnX.controlPoints[index] * toolPoint[0] +
            motion.columnY.controlPoints[index] * toolPoint[1] +
            motion.columnZ.controlPoints[index] * toolPoint[2] +
            motion.translation.controlPoints[index];
    }
    return {
            "degree" : motion.degree,
            "isPeriodic" : false,
            "isRational" : false,
            "controlPoints" : trajectoryControlPoints,
            "knots" : knotArray(motion.columnX.knots)
        };
}

/**
 * The exactly-rigid placement of the tool at a station, from the RAW kernel frames (never the
 * spline fit): toWorld(frame_i) * inverse(toWorld(frame_0)). Kernel-facing (with units).
 */
export function motionSnapshotTransform(motion is map, stationIndex is number) returns Transform
{
    return toWorld(motion.stationFrames[stationIndex]) * inverse(toWorld(motion.stationFrames[0]));
}

/**
 * Throws (with the edge pair and angle named) if consecutive path edges meet with a tangent
 * break larger than PATH_TANGENT_BREAK_THRESHOLD.
 */
function checkPathTangentContinuity(context is Context, path is Path)
{
    var previousEndDirection;
    for (var edgeIndex = 0; edgeIndex < size(path.edges); edgeIndex += 1)
    {
        var endpointTangents = evEdgeTangentLines(context, {
                    "edge" : path.edges[edgeIndex],
                    "parameters" : [0, 1]
                });
        if (path.flipped[edgeIndex])
        {
            endpointTangents = [
                    line(endpointTangents[1].origin, -endpointTangents[1].direction),
                    line(endpointTangents[0].origin, -endpointTangents[0].direction)
                ];
        }
        if (edgeIndex > 0)
        {
            const breakAngle = angleBetween(previousEndDirection, endpointTangents[0].direction);
            if (breakAngle > PATH_TANGENT_BREAK_THRESHOLD)
            {
                throw "solidSweepUtils motion: the path has a " ~ (breakAngle / degree) ~
                    " degree tangent break between edges " ~ edgeIndex ~ " and " ~ (edgeIndex + 1) ~
                    " - the motion needs a tangent-continuous path.";
            }
        }
        previousEndDirection = endpointTangents[1].direction;
    }
}

/**
 * Arc-length fractions of the interior edge junctions - potential curvature breaks that
 * downstream fitting must treat as mandatory patch boundaries (the motion's `events`).
 */
function interiorJunctionParameters(context is Context, path is Path, pathLength is ValueWithUnits) returns array
{
    if (size(path.edges) < 2)
    {
        return [];
    }
    var junctions = makeArray(size(path.edges) - 1, 0);
    var accumulated = 0;
    for (var edgeIndex = 0; edgeIndex < size(path.edges) - 1; edgeIndex += 1)
    {
        accumulated += evLength(context, { "entities" : path.edges[edgeIndex] }) / pathLength;
        junctions[edgeIndex] = accumulated;
    }
    return junctions;
}

/**
 * Uniform stations 0..1 at the given count, merged (sorted, deduplicated) with the junction
 * parameters so every junction is an interpolation node.
 */
function mergedStationParameters(stationCount is number, junctionParameters is array) returns array
{
    var stations = makeArray(stationCount + size(junctionParameters), 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        stations[index] = index / (stationCount - 1);
    }
    for (var index = 0; index < size(junctionParameters); index += 1)
    {
        stations[stationCount + index] = junctionParameters[index];
    }
    stations = sort(stations, function(a, b)
        {
            return a - b;
        });
    var deduplicated = [stations[0]];
    for (var index = 1; index < size(stations); index += 1)
    {
        if (stations[index] - deduplicated[size(deduplicated) - 1] > 1e-9)
        {
            deduplicated = append(deduplicated, stations[index]);
        }
    }
    return deduplicated;
}

/**
 * A station sample: { frame (CoordSystem, with units), xAxis, yAxis, zAxis, origin (unitless
 * Vector) }. Keep-orientation mode: world axes at each path point.
 */
function keepOrientationSamples(tangentLines is array) returns array
{
    var samples = makeArray(size(tangentLines), 0);
    for (var index = 0; index < size(tangentLines); index += 1)
    {
        samples[index] = {
                "frame" : coordSystem(tangentLines[index].origin, vector(1, 0, 0), vector(0, 0, 1)),
                "xAxis" : vector(1, 0, 0),
                "yAxis" : vector(0, 1, 0),
                "zAxis" : vector(0, 0, 1),
                "origin" : tangentLines[index].origin / meter
            };
    }
    return samples;
}

/**
 * Builds the helper-sweep scaffold along the path (the curvePattern.fs technique): per edge,
 * sketch a tiny line perpendicular to the edge start (chained across edges by tracking the
 * previous sweep's end-cap vertex) and opSweep it along that edge. Must run inside an active
 * scratch scope; returns one swept-face Query per path edge.
 */
function buildFrameScaffold(context is Context, id is Id, path is Path) returns array
{
    var sweepFaces = makeArray(size(path.edges), 0);
    var previousEndTangent;
    var trackingQuery;
    for (var edgeIndex = 0; edgeIndex < size(path.edges); edgeIndex += 1)
    {
        var endpointTangents = evEdgeTangentLines(context, {
                    "edge" : path.edges[edgeIndex],
                    "parameters" : [0, 1]
                });
        if (path.flipped[edgeIndex])
        {
            endpointTangents = [
                    line(endpointTangents[1].origin, -endpointTangents[1].direction),
                    line(endpointTangents[0].origin, -endpointTangents[0].direction)
                ];
        }

        const sketchPlane = plane(endpointTangents[0].origin, endpointTangents[0].direction);
        var sketchPoint;
        if (edgeIndex == 0)
        {
            const profileHalfWidth = (10000 * TOLERANCE.zeroLength) * meter;
            sketchPoint = vector(0 * meter, profileHalfWidth);
        }
        else
        {
            // Transform the previous sweep's tracked end point into this edge's start plane,
            // so the profile is continuous across the junction.
            const junctionTransform = transform(previousEndTangent, endpointTangents[0]);
            const previousEndCapVertices = qCapEntity(id + ("sweep" ~ (edgeIndex - 1)), CapType.END, EntityType.VERTEX);
            const previousProfileEnd = evVertexPoint(context, {
                        "vertex" : qIntersection([previousEndCapVertices, trackingQuery])
                    });
            sketchPoint = worldToPlane(sketchPlane, junctionTransform * previousProfileEnd);
        }

        const sketchId = id + ("sketch" ~ edgeIndex);
        var sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
        skLineSegment(sketch, "line1", {
                    "start" : vector(0 * meter, 0 * meter),
                    "end" : sketchPoint
                });
        skSolve(sketch);

        trackingQuery = startTracking(context, {
                    "subquery" : sketchEntityQuery(sketchId, undefined, "line1.end"),
                    "secondarySubquery" : path.edges[edgeIndex]
                });

        opSweep(context, id + ("sweep" ~ edgeIndex), {
                    "profiles" : qCreatedBy(sketchId, EntityType.EDGE),
                    "path" : path.edges[edgeIndex]
                });
        sweepFaces[edgeIndex] = qCreatedBy(id + ("sweep" ~ edgeIndex), EntityType.FACE);

        previousEndTangent = endpointTangents[1];
    }
    return sweepFaces;
}

/**
 * Samples the kernel sweeper's roll-free frame at each station off the scaffold faces:
 * closest point on the swept ribbon -> tangent plane -> x forced to the projected path
 * tangent (the curvePattern anti-roll step). z = ribbon normal, y = z cross x.
 */
function rollFreeSamples(context is Context, sweepFaces is array, tangentLines is array, edgeIndices is array) returns array
{
    var samples = makeArray(size(tangentLines), 0);
    for (var index = 0; index < size(tangentLines); index += 1)
    {
        const ribbonFace = sweepFaces[edgeIndices[index]];
        const distanceResult = evDistance(context, {
                    "side0" : ribbonFace,
                    "side1" : tangentLines[index].origin,
                    "arcLengthParameterization" : false
                });
        var tangentPlane = evFaceTangentPlane(context, {
                    "face" : ribbonFace,
                    "parameter" : distanceResult.sides[0].parameter
                });
        tangentPlane.x = project(tangentPlane, tangentLines[index]).direction;

        const xAxis = tangentPlane.x;
        const zAxis = tangentPlane.normal;
        const yAxis = cross(zAxis, xAxis);
        samples[index] = {
                "frame" : coordSystem(tangentLines[index].origin, xAxis, zAxis),
                "xAxis" : xAxis,
                "yAxis" : yAxis,
                "zAxis" : zAxis,
                "origin" : tangentLines[index].origin / meter
            };
    }
    return samples;
}

/**
 * Rotation-minimizing frames by the double-reflection method (Wang, Juettler, Zheng, Liu
 * 2008), built purely from the station tangent lines. The roll-free frame source.
 * `initialNormalCandidate` (a unitless direction or undefined) seeds the frame roll; the
 * seed orients only the frames themselves - the relative motion A(t) is seed-invariant.
 */
function doubleReflectionSamples(tangentLines is array, initialNormalCandidate) returns array
{
    const stationCount = size(tangentLines);
    var tangents = makeArray(stationCount, 0);
    var positions = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        tangents[index] = tangentLines[index].direction;
        positions[index] = tangentLines[index].origin / meter;
    }

    var normal = initialNormalCandidate == undefined ? perpendicularVector(tangents[0]) : initialNormalCandidate;
    normal = normalize(normal - dot(normal, tangents[0]) * tangents[0]);

    var samples = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        const xAxis = tangents[index];
        const zAxis = normal;
        const yAxis = cross(zAxis, xAxis);
        samples[index] = {
                "frame" : coordSystem(tangentLines[index].origin, xAxis, zAxis),
                "xAxis" : xAxis,
                "yAxis" : yAxis,
                "zAxis" : zAxis,
                "origin" : positions[index]
            };

        if (index < stationCount - 1)
        {
            // First reflection: across the bisecting plane of the chord to the next station.
            const chord = positions[index + 1] - positions[index];
            const chordSquared = dot(chord, chord);
            var reflectedNormal = normal;
            var reflectedTangent = tangents[index];
            if (chordSquared > 0)
            {
                reflectedNormal = normal - (2 * dot(chord, normal) / chordSquared) * chord;
                reflectedTangent = tangents[index] - (2 * dot(chord, tangents[index]) / chordSquared) * chord;
            }
            // Second reflection: across the plane bisecting the reflected and true tangents.
            const tangentDifference = tangents[index + 1] - reflectedTangent;
            const differenceSquared = dot(tangentDifference, tangentDifference);
            if (differenceSquared > 0)
            {
                reflectedNormal = reflectedNormal -
                    (2 * dot(tangentDifference, reflectedNormal) / differenceSquared) * tangentDifference;
            }
            // Re-orthonormalize against the next tangent to shed roundoff.
            normal = normalize(reflectedNormal - dot(reflectedNormal, tangents[index + 1]) * tangents[index + 1]);
        }
    }
    return samples;
}

/**
 * Turns station samples into the MotionSpline map: rotation columns A_k = S_k * S_0^T and
 * translations b_k = origin_k - A_k * origin_0 per station, interpolated by cubic B-splines
 * at the station parameters, normalized, and drift-certified at every knot-span midpoint.
 */
function assembleMotionSpline(stationSamples is array, stationParameters is array, junctionParameters is array,
    path is Path, pathLength is ValueWithUnits, keepOrientation is boolean, driftTolerance is number) returns map
{
    const stationCount = size(stationSamples);
    const startSample = stationSamples[0];

    // All twelve motion scalars are fitted in ONE interpolation call on 12-dimensional
    // vectors, then unpacked - the interpolation operator build is the dominant O(n^3) cost
    // and this shares it across the four splines instead of paying it four times.
    var packedSamples = makeArray(stationCount, 0);
    var stationFrames = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        const sample = stationSamples[index];
        // Column i of A_k = S_k * (row i of S_0), with S = [x, y, z] as columns.
        const columnX = sample.xAxis * startSample.xAxis[0] + sample.yAxis * startSample.yAxis[0] + sample.zAxis * startSample.zAxis[0];
        const columnY = sample.xAxis * startSample.xAxis[1] + sample.yAxis * startSample.yAxis[1] + sample.zAxis * startSample.zAxis[1];
        const columnZ = sample.xAxis * startSample.xAxis[2] + sample.yAxis * startSample.yAxis[2] + sample.zAxis * startSample.zAxis[2];
        const translation = sample.origin -
            (columnX * startSample.origin[0] + columnY * startSample.origin[1] + columnZ * startSample.origin[2]);
        packedSamples[index] = vector([
                    columnX[0], columnX[1], columnX[2],
                    columnY[0], columnY[1], columnY[2],
                    columnZ[0], columnZ[1], columnZ[2],
                    translation[0], translation[1], translation[2]
                ]);
        stationFrames[index] = sample.frame;
    }

    const packedSpline = interpolateBSplineCurveThroughPoints(packedSamples, 3, stationParameters);
    const packedVelocity = differentiatedPackedSpline(packedSpline);
    const packedAcceleration = differentiatedPackedSpline(packedVelocity);
    const columnXSpline = normalizeSplineDefinition(unpackedComponentSpline(packedSpline, 0));
    const columnYSpline = normalizeSplineDefinition(unpackedComponentSpline(packedSpline, 3));
    const columnZSpline = normalizeSplineDefinition(unpackedComponentSpline(packedSpline, 6));
    const translationSpline = normalizeSplineDefinition(unpackedComponentSpline(packedSpline, 9));

    const drift = keepOrientation ? 0 : orthogonalityDriftAtSpanMidpoints(packedSpline);

    return {
            "columnX" : columnXSpline,
            "columnY" : columnYSpline,
            "columnZ" : columnZSpline,
            "translation" : translationSpline,
            "packedSpline" : packedSpline,
            "packedVelocity" : packedVelocity,
            "packedAcceleration" : packedAcceleration,
            "degree" : 3,
            "keepOrientation" : keepOrientation,
            "stationParameters" : stationParameters,
            "stationFrames" : stationFrames,
            "orthogonalityDrift" : drift,
            "driftTolerance" : driftTolerance,
            "events" : junctionParameters,
            "pathLength" : pathLength,
            "path" : path
        };
}

/**
 * Slices one 3-dimensional component spline out of the packed 12-dimensional fit
 * (components componentOffset .. componentOffset + 2 of each control point).
 */
function unpackedComponentSpline(packedSpline is map, componentOffset is number) returns map
{
    const count = size(packedSpline.controlPoints);
    var controlPoints = makeArray(count, 0);
    for (var index = 0; index < count; index += 1)
    {
        const packedPoint = packedSpline.controlPoints[index];
        controlPoints[index] = vector(packedPoint[componentOffset],
            packedPoint[componentOffset + 1],
            packedPoint[componentOffset + 2]);
    }
    return {
            "degree" : packedSpline.degree,
            "isPeriodic" : false,
            "isRational" : false,
            "controlPoints" : controlPoints,
            "knots" : knotArray(packedSpline.knots)
        };
}

/**
 * sup |A(t)^T A(t) - I| over every knot-span midpoint - the certification that the fitted
 * rotation stays rigid BETWEEN the exactly-rigid interpolation nodes. Evaluated directly on
 * the packed non-rational fit with one basis computation per midpoint.
 */
function orthogonalityDriftAtSpanMidpoints(packedSpline is map) returns number
{
    const knots = packedSpline.knots;
    const degree = packedSpline.degree;
    var worstDrift = 0;
    for (var knotIndex = degree; knotIndex < size(knots) - degree - 1; knotIndex += 1)
    {
        if (knots[knotIndex + 1] - knots[knotIndex] <= 1e-12)
        {
            continue;
        }
        const midpoint = 0.5 * (knots[knotIndex] + knots[knotIndex + 1]);
        const packedPoint = evaluatePackedPoint(packedSpline, midpoint);
        const columnX = vector(packedPoint[0], packedPoint[1], packedPoint[2]);
        const columnY = vector(packedPoint[3], packedPoint[4], packedPoint[5]);
        const columnZ = vector(packedPoint[6], packedPoint[7], packedPoint[8]);
        worstDrift = max(worstDrift, abs(dot(columnX, columnX) - 1));
        worstDrift = max(worstDrift, abs(dot(columnY, columnY) - 1));
        worstDrift = max(worstDrift, abs(dot(columnZ, columnZ) - 1));
        worstDrift = max(worstDrift, abs(dot(columnX, columnY)));
        worstDrift = max(worstDrift, abs(dot(columnX, columnZ)));
        worstDrift = max(worstDrift, abs(dot(columnY, columnZ)));
    }
    return worstDrift;
}

/**
 * The derivative of a packed non-rational spline as its own spline: standard B-spline
 * difference net (degree drops by one, one knot trimmed from each end).
 */
function differentiatedPackedSpline(spline is map) returns map
{
    const degree = spline.degree;
    const knots = spline.knots;
    const count = size(spline.controlPoints);
    var derivativeControlPoints = makeArray(count - 1, 0);
    for (var index = 0; index < count - 1; index += 1)
    {
        const spanWidth = knots[index + degree + 1] - knots[index + 1];
        derivativeControlPoints[index] = spanWidth <= 1e-15 ? 0 * spline.controlPoints[index] :
            (degree / spanWidth) * (spline.controlPoints[index + 1] - spline.controlPoints[index]);
    }
    var derivativeKnots = makeArray(size(knots) - 2, 0);
    for (var index = 0; index < size(knots) - 2; index += 1)
    {
        derivativeKnots[index] = knots[index + 1];
    }
    return {
            "degree" : degree - 1,
            "isPeriodic" : false,
            "isRational" : false,
            "controlPoints" : derivativeControlPoints,
            "knots" : knotArray(derivativeKnots)
        };
}

/**
 * Components offset .. offset + 2 of a packed value as a 3-vector.
 */
function sliceVector3(packedValue is Vector, offset is number) returns Vector
{
    return vector(packedValue[offset], packedValue[offset + 1], packedValue[offset + 2]);
}

/**
 * Point of the packed non-rational fit at `parameter`: one span lookup, one basis
 * computation, degree + 1 control-point combinations.
 */
function evaluatePackedPoint(packedSpline is map, parameter is number) returns Vector
{
    const degree = packedSpline.degree;
    const spanIndex = leanEvaluationSpanIndex(packedSpline.knots, degree, parameter);
    const basisValues = bSplineBasisValues(packedSpline.knots, degree, spanIndex, parameter);
    var point = basisValues[0] * packedSpline.controlPoints[spanIndex - degree];
    for (var basisIndex = 1; basisIndex <= degree; basisIndex += 1)
    {
        point = point + basisValues[basisIndex] * packedSpline.controlPoints[spanIndex - degree + basisIndex];
    }
    return point;
}


// ============================= Envelope function layer (spec 6.1-6.2) =============================

/**
 * Decompose a stripped motion into per-span Bernstein data. All four motion splines share one
 * knot vector by the motion module's contract; this throws if their Bezier breakpoints
 * disagree.
 *
 * Each span: {
 *     tStart, tEnd {number},
 *     columns {array} : [C1, C2, C3] - A(t)'s columns as Bernstein vectors on local [0, 1],
 *     columnDerivatives {array} : [C1', C2', C3'] in GLOBAL t units,
 *     translationDerivative {array} : b' as a Bernstein vector in global t units,
 *     velocityDots {array} : M[i][j] = <C_i, C_j'> coefficient arrays, all one shared degree,
 *     translationDots {array} : T[i] = <C_i, b'> coefficient arrays, same degree as M,
 *     velocityDotRanges, translationDotRanges {arrays} : {minimum, maximum} per polynomial,
 *         precomputed for the block screen
 * }
 */
export function buildMotionSpanPolynomials(strippedMotion is map) returns array
{
    const columnSegments = [
            decomposeIntoBezierSegments(strippedMotion.columnX),
            decomposeIntoBezierSegments(strippedMotion.columnY),
            decomposeIntoBezierSegments(strippedMotion.columnZ)
        ];
    const translationSegments = decomposeIntoBezierSegments(strippedMotion.translation);
    const spanCount = size(translationSegments);
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        if (size(columnSegments[columnIndex]) != spanCount)
        {
            throw "solidSweepUtils envelope: motion splines decompose into different span counts (" ~
                size(columnSegments[columnIndex]) ~ " vs " ~ spanCount ~ ") - they must share one knot vector.";
        }
    }

    var spans = makeArray(spanCount);
    for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
    {
        const tStart = translationSegments[spanIndex].domainStart;
        const tEnd = translationSegments[spanIndex].domainEnd;
        const spanWidth = tEnd - tStart;

        var columns = makeArray(3);
        var columnDerivatives = makeArray(3);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            const segment = columnSegments[columnIndex][spanIndex];
            if (abs(segment.domainStart - tStart) > 1e-12 || abs(segment.domainEnd - tEnd) > 1e-12)
            {
                throw "solidSweepUtils envelope: motion spline span domains disagree at span " ~ spanIndex ~ ".";
            }
            columns[columnIndex] = componentCoefficientArrays(segment.controlPoints);
            columnDerivatives[columnIndex] = differentiateBernsteinVector(columns[columnIndex], spanWidth);
        }
        const translationVector = componentCoefficientArrays(translationSegments[spanIndex].controlPoints);
        const translationDerivative = differentiateBernsteinVector(translationVector, spanWidth);

        var velocityDots = makeArray(3);
        var translationDots = makeArray(3);
        var sharedDegree = 0;
        for (var i = 0; i < 3; i += 1)
        {
            var dotRow = makeArray(3);
            for (var j = 0; j < 3; j += 1)
            {
                dotRow[j] = dotBernsteinVectors(columns[i], columnDerivatives[j]);
                sharedDegree = max(sharedDegree, size(dotRow[j]) - 1);
            }
            velocityDots[i] = dotRow;
            translationDots[i] = dotBernsteinVectors(columns[i], translationDerivative);
            sharedDegree = max(sharedDegree, size(translationDots[i]) - 1);
        }
        var velocityDotRanges = makeArray(3);
        var translationDotRanges = makeArray(3);
        for (var i = 0; i < 3; i += 1)
        {
            var rangeRow = makeArray(3);
            for (var j = 0; j < 3; j += 1)
            {
                velocityDots[i][j] = elevateBernstein(velocityDots[i][j], sharedDegree);
                rangeRow[j] = bernsteinRange(velocityDots[i][j]);
            }
            velocityDotRanges[i] = rangeRow;
            translationDots[i] = elevateBernstein(translationDots[i], sharedDegree);
            translationDotRanges[i] = bernsteinRange(translationDots[i]);
        }

        spans[spanIndex] = {
                "tStart" : tStart,
                "tEnd" : tEnd,
                "columns" : columns,
                "columnDerivatives" : columnDerivatives,
                "translationDerivative" : translationDerivative,
                "velocityDots" : velocityDots,
                "translationDots" : translationDots,
                "velocityDotRanges" : velocityDotRanges,
                "translationDotRanges" : translationDotRanges
            };
    }
    return spans;
}

/**
 * Build the motion-independent envelope factors of one non-rational stripped surface:
 * per Bezier patch, the S and N coefficient grids plus their value ranges - everything the
 * loose block screen needs and nothing it does not. The expensive N_i * S_j product grids are
 * NOT built here; buildEnvelopePatchProducts supplies them lazily for patches that survive
 * screening. Throws on rational input - the coefficient path requires the non-rational form
 * extraction supplies for freeform faces.
 *
 * Returns { uSegments, vSegments, patches : 2D array of {
 *     uStart, uEnd, vStart, vEnd,
 *     surfaceGrids {array} : S as [xGrid, yGrid, zGrid],
 *     normalGrids {array} : N = S_u x S_v (global-parameter derivatives),
 *     surfaceRanges, normalRanges {arrays} : {minimum, maximum} per component grid
 * } }
 */
export function buildEnvelopePatchFactors(strippedSurface is map) returns map
{
    if (strippedSurface.isRational == true)
    {
        throw "solidSweepUtils envelope: buildEnvelopePatchFactors requires a non-rational surface. " ~
            "Freeform faces are extracted non-rationally for the coefficient path; analytic " ~
            "faces take closed forms and never reach this assembly.";
    }
    const patchRows = decomposeSurfaceIntoBezierPatches(strippedSurface);
    const uSegments = size(patchRows);
    const vSegments = size(patchRows[0]);
    var factorRows = makeArray(uSegments);
    for (var uSegment = 0; uSegment < uSegments; uSegment += 1)
    {
        var factorRow = makeArray(vSegments);
        for (var vSegment = 0; vSegment < vSegments; vSegment += 1)
        {
            const patch = patchRows[uSegment][vSegment];
            const uWidth = patch.uDomainEnd - patch.uDomainStart;
            const vWidth = patch.vDomainEnd - patch.vDomainStart;
            const surfaceGrids = componentCoefficientGrids(patch.controlPoints);
            var uTangentGrids = makeArray(3);
            var vTangentGrids = makeArray(3);
            for (var component = 0; component < 3; component += 1)
            {
                uTangentGrids[component] = scaleBernsteinGrid(differentiateBernsteinGridU(surfaceGrids[component]), 1 / uWidth);
                vTangentGrids[component] = scaleBernsteinGrid(differentiateBernsteinGridV(surfaceGrids[component]), 1 / vWidth);
            }
            const normalGrids = crossBernsteinVectorGrids(uTangentGrids, vTangentGrids);

            var surfaceRanges = makeArray(3);
            var normalRanges = makeArray(3);
            for (var component = 0; component < 3; component += 1)
            {
                surfaceRanges[component] = bernsteinGridRange(surfaceGrids[component]);
                normalRanges[component] = bernsteinGridRange(normalGrids[component]);
            }

            factorRow[vSegment] = {
                    "uStart" : patch.uDomainStart, "uEnd" : patch.uDomainEnd,
                    "vStart" : patch.vDomainStart, "vEnd" : patch.vDomainEnd,
                    "surfaceGrids" : surfaceGrids,
                    "normalGrids" : normalGrids,
                    "surfaceRanges" : surfaceRanges,
                    "normalRanges" : normalRanges
                };
        }
        factorRows[uSegment] = factorRow;
    }
    return { "uSegments" : uSegments, "vSegments" : vSegments, "patches" : factorRows };
}

/**
 * The lazily built product data of one patch - only patches that survive screening pay for
 * this. Returns {
 *     productGrids {array} : G[i][j] = N_i * S_j at one shared uv degree,
 *     elevatedNormalGrids {array} : H[i] = N_i elevated to that degree,
 *     termMatrix {Matrix} : the twelve term grids flattened row-major into a 12 x (rows*cols)
 *         matrix, ordered [G00..G02, G10..G12, G20..G22, H0, H1, H2] - materialization is then
 *         one native matrix product per block,
 *     gridRowCount, gridColumnCount {number} : the shared grid dimensions for unflattening
 * }.
 */
export function buildEnvelopePatchProducts(patchFactor is map) returns map
{
    var productGrids = makeArray(3);
    var targetUDegree = 0;
    var targetVDegree = 0;
    for (var i = 0; i < 3; i += 1)
    {
        var productRow = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            productRow[j] = multiplyBernsteinGrids(patchFactor.normalGrids[i], patchFactor.surfaceGrids[j]);
            targetUDegree = max(targetUDegree, size(productRow[j]) - 1);
            targetVDegree = max(targetVDegree, size(productRow[j][0]) - 1);
        }
        productGrids[i] = productRow;
    }
    var elevatedNormalGrids = makeArray(3);
    var termRows = makeArray(12);
    for (var i = 0; i < 3; i += 1)
    {
        for (var j = 0; j < 3; j += 1)
        {
            productGrids[i][j] = elevateBernsteinGrid(productGrids[i][j], targetUDegree, targetVDegree);
            termRows[i * 3 + j] = concatenateArrays(productGrids[i][j]);
        }
        elevatedNormalGrids[i] = elevateBernsteinGrid(patchFactor.normalGrids[i], targetUDegree, targetVDegree);
        termRows[9 + i] = concatenateArrays(elevatedNormalGrids[i]);
    }
    return {
            "productGrids" : productGrids,
            "elevatedNormalGrids" : elevatedNormalGrids,
            "termMatrix" : matrix(termRows),
            "gridRowCount" : targetUDegree + 1,
            "gridColumnCount" : targetVDegree + 1
        };
}

/**
 * Loose interval screen of one (patch x t-span) block, from precomputed ranges alone - a few
 * dozen interval multiplies, no grid arithmetic. Every factor's value on the block lies in
 * its Bernstein hull range, so the summed interval product contains the true range of f and
 * canVanish == false is a certificate.
 * Returns { canVanish {boolean}, looseMin, looseMax {number} }.
 */
export function screenEnvelopeBlock(patchFactor is map, spanPolynomials is map, valueTolerance is number) returns map
{
    var lowerBound = 0;
    var upperBound = 0;
    for (var i = 0; i < 3; i += 1)
    {
        for (var j = 0; j < 3; j += 1)
        {
            const term = multiplyIntervals(
                multiplyIntervals(patchFactor.normalRanges[i], patchFactor.surfaceRanges[j]),
                spanPolynomials.velocityDotRanges[i][j]);
            lowerBound += term.minimum;
            upperBound += term.maximum;
        }
        const translationTerm = multiplyIntervals(patchFactor.normalRanges[i], spanPolynomials.translationDotRanges[i]);
        lowerBound += translationTerm.minimum;
        upperBound += translationTerm.maximum;
    }
    return {
            "canVanish" : lowerBound <= valueTolerance && upperBound >= -valueTolerance,
            "looseMin" : lowerBound,
            "looseMax" : upperBound
        };
}

/** Two-argument convenience: screen with zero tolerance. */
export function screenEnvelopeBlock(patchFactor is map, spanPolynomials is map) returns map
{
    return screenEnvelopeBlock(patchFactor, spanPolynomials, 0);
}

/**
 * screenEnvelopeBlock with the zero threshold taken from the BLOCK'S OWN value range rather
 * than from an absolute number a caller guessed: `relativeTolerance` times the larger end of
 * the loose range, never below `absoluteFloor`. Returns screenEnvelopeBlock's record plus
 * `signTolerance` - the threshold, which the caller uses for every sign test it makes on this
 * block, so that screening and the signs it later reads agree on what zero means.
 *
 * An absolute threshold cannot work here: the same number is a certificate on a block whose
 * |f| runs to 1e-2 and pure noise on one that runs to 1e-14, and nothing upstream of a block
 * knows which it is.
 */
export function screenEnvelopeBlockScaled(patchFactor is map, spanPolynomials is map,
    relativeTolerance is number, absoluteFloor is number) returns map
{
    const loose = screenEnvelopeBlock(patchFactor, spanPolynomials, 0);
    const signTolerance = max(absoluteFloor,
        relativeTolerance * max(abs(loose.looseMin), abs(loose.looseMax)));
    return {
            "canVanish" : loose.looseMin <= signTolerance && loose.looseMax >= -signTolerance,
            "looseMin" : loose.looseMin,
            "looseMax" : loose.looseMax,
            "signTolerance" : signTolerance
        };
}

/**
 * Materialize one block's full coefficient tensor: f on the block as a Bernstein polynomial
 * in local t whose coefficients are uv grids - result[m] is the grid multiplying the m-th
 * Bernstein basis function in t. The whole accumulation is ONE native matrix product per
 * block: the (t-coefficients x 12) weight matrix times the patch's flattened 12-row term
 * matrix, with each result row sliced back into a grid. Consumes the lazily built patch
 * products; call it only on blocks the screen left alive (or in testers).
 */
export function materializeEnvelopeBlock(patchProducts is map, spanPolynomials is map) returns array
{
    const timeCoefficientCount = size(spanPolynomials.translationDots[0]);
    var weightRows = makeArray(timeCoefficientCount);
    for (var m = 0; m < timeCoefficientCount; m += 1)
    {
        var weights = makeArray(12, 0);
        var termCursor = 0;
        for (var i = 0; i < 3; i += 1)
        {
            for (var j = 0; j < 3; j += 1)
            {
                weights[termCursor] = spanPolynomials.velocityDots[i][j][m];
                termCursor += 1;
            }
        }
        for (var i = 0; i < 3; i += 1)
        {
            weights[9 + i] = spanPolynomials.translationDots[i][m];
        }
        weightRows[m] = weights;
    }
    const flattenedBlocks = matrix(weightRows) * patchProducts.termMatrix;

    const columnCount = patchProducts.gridColumnCount;
    var blockGrids = makeArray(timeCoefficientCount);
    for (var m = 0; m < timeCoefficientCount; m += 1)
    {
        const flatRow = flattenedBlocks[m];
        var grid = makeArray(patchProducts.gridRowCount);
        for (var rowIndex = 0; rowIndex < patchProducts.gridRowCount; rowIndex += 1)
        {
            grid[rowIndex] = subArray(flatRow, rowIndex * columnCount, (rowIndex + 1) * columnCount);
        }
        blockGrids[m] = grid;
    }
    return blockGrids;
}

/**
 * Exact convex-hull screen of a materialized block: true when every coefficient across all
 * t-coefficient grids has one strict sign (beyond valueTolerance), so f cannot vanish there.
 */
export function envelopeBlockExcludesZero(blockGrids is array, valueTolerance is number) returns boolean
{
    var minimum = undefined;
    var maximum = undefined;
    for (var grid in blockGrids)
    {
        const range = bernsteinGridRange(grid);
        minimum = minimum == undefined ? range.minimum : min(minimum, range.minimum);
        maximum = maximum == undefined ? range.maximum : max(maximum, range.maximum);
    }
    return minimum > valueTolerance || maximum < -valueTolerance;
}

/** Evaluate a materialized block at local block coordinates (all in [0, 1]). */
export function evaluateMaterializedBlock(blockGrids is array, localU is number, localV is number, localT is number) returns number
{
    var timeCoefficients = makeArray(size(blockGrids));
    for (var m = 0; m < size(blockGrids); m += 1)
    {
        timeCoefficients[m] = evaluateBernsteinGrid(blockGrids[m], localU, localV);
    }
    return evaluateBernstein(timeCoefficients, localT);
}

// ============================= Lean pointwise evaluation (spec 11.2 levers A and D) =============================

// The module's own B-spline evaluation path, written for the one question its inner loops ask
// and nothing else: a 3D surface, partial derivatives to TOTAL order two, plain numbers in and
// plain numbers out.
//
// It sits next to splineRefinementUtils' general evaluator, which is exact, rational aware and
// already tested, because of what that one is written IN. Its inner statement is
// `pointSum = pointSum + blendValue * controlPoint`, which is two std operator calls, each with
// a precondition and its own three-iteration interpreted loop, for three multiply-adds of real
// work; `dot` allocates a `@subArray` per call. A single order-(2, 2) evaluation on a 9x4
// rational net runs about a hundred of those, and the section march runs the evaluation tens of
// thousands of times per build.
//
// These functions do the IDENTICAL arithmetic - same A2.3 basis recurrence, same summation
// order, same A4.4 quotient rule - on scalar accumulators. Agreement is therefore to the last
// bit, not to a tolerance, and `sweepLeanEvaluatorLiveTest` in the tester is what holds it
// there.
//
// Two economies beyond scalarization, both invisible to callers:
//
// Only the TRIANGLE uOrder + vOrder <= 2 is computed. The general evaluator returns the full
// rectangle on purpose (its own docs explain why), but the envelope gradient, the orientation
// sample and point inversion read exactly S, S_u, S_v, S_uu, S_uv, S_vv and never the three
// rectangle corners above them - and A4.4 for a triangle entry reads only triangle entries, so
// dropping those three costs nothing downstream.
//
// The U collapse hoists the control-point ROW out of its inner loop. The general evaluator
// cannot: its loops run vBasisIndex outer, uBasisIndex inner, so the row index moves innermost.
// Swapping them here accumulates into a row of scalar sums instead, and for a fixed column the
// terms still arrive in ascending uBasisIndex - which is why the swap changes no floating-point
// result.
//
// Derivatives come back in the fixed flat order [S, S_u, S_v, S_uu, S_uv, S_vv], truncated to
// whatever total order was asked for, each entry a plain [x, y, z] array rather than a Vector:
// every caller below does its own component arithmetic, and the cast back to Vector happens
// only where a value leaves the module.

/** How many flat triangle entries each maximum total order returns. */
const LEAN_TRIANGLE_SIZE = [1, 3, 6];

/** The u order of each flat triangle entry, [S, S_u, S_v, S_uu, S_uv, S_vv]. */
const LEAN_TRIANGLE_U_ORDER = [0, 1, 0, 2, 1, 0];

/** The v order of each flat triangle entry. */
const LEAN_TRIANGLE_V_ORDER = [0, 0, 1, 0, 1, 2];

/**
 * Derivatives of the (degree + 1) nonvanishing basis functions at `parameter`, orders 0 through
 * maxOrder, as ONE FLAT array: result[order * (degree + 1) + functionIndex].
 *
 * NURBS Book Algorithm A2.3, arithmetic for arithmetic as splineRefinementUtils'
 * `bSplineBasisDerivatives` runs it - including its choice to start each basis function from
 * freshly zeroed coefficient rows rather than ping-pong the book's two dirty ones. Duplicated
 * here rather than imported because that one is private to its module, and because the agreement
 * this module needs is bit for bit.
 *
 * FLAT, and with its buffers hoisted, for a measured reason (spec 11.4). The row-of-rows version
 * of this function was 14.8 s of a 31.1 s profiled build - 48% - and 92% of the build's 850,000
 * `makeArray` calls were inside it: nineteen allocations per invocation, at 8.5 us each. Worse
 * than the allocations, `ndu[level][functionIndex] = ...` is a DEEP write, and FeatureScript
 * arrays are values, so every one of those rebuilt a row. A single flat array indexed
 * `row * stride + column` has neither problem, and the two coefficient buffers are now allocated
 * once and re-zeroed per basis function, which is the same state the fresh allocation gave them.
 *
 * None of that changes a number: same recurrence, same operands, same order.
 */
/**
 * The evaluation span index of `parameter`, by binary search.
 *
 * Answers exactly what `findEvaluationSpanIndex` answers - the largest non-degenerate span whose
 * knot does not exceed the parameter, falling back to `degree` when the parameter sits at or
 * below the domain start - and reaches it in a number of steps that grows with the LOGARITHM of
 * the span count rather than with the count. The published module's form walks the knot vector
 * downward one span at a time, which is why a motion fitted at hundreds of stations pays for
 * every one of them on every evaluation.
 *
 * The step back over degenerate spans runs at most `degree` times: that is the largest
 * multiplicity an interior knot may carry.
 */
function leanEvaluationSpanIndex(knots is array, degree is number, parameter is number) returns number
{
    const highestSpan = size(knots) - degree - 2;
    if (highestSpan < degree || knots[degree] > parameter)
    {
        return degree;
    }
    var low = degree;
    var high = highestSpan;
    while (low < high)
    {
        const middle = low + floor((high - low + 1) / 2);
        if (knots[middle] <= parameter)
        {
            low = middle;
        }
        else
        {
            high = middle - 1;
        }
    }
    var spanIndex = low;
    while (spanIndex > degree && knots[spanIndex] >= knots[spanIndex + 1])
    {
        spanIndex -= 1;
    }
    return knots[spanIndex] < knots[spanIndex + 1] ? spanIndex : degree;
}

function leanBasisDerivatives(knots is array, degree is number, spanIndex is number,
    parameter is number, maxOrder is number) returns array
{
    const stride = degree + 1;
    var leftDistances = makeArray(stride, 0);
    var rightDistances = makeArray(stride, 0);

    if (maxOrder == 0)
    {
        // Values only - the point-evaluation path, which is a third of all basis calls. It runs
        // the identical recurrence without storing the knot differences only the derivative pass
        // reads, so it writes half as much and needs no `ndu` table at all. `shared` here is the
        // same quotient the general path forms, over the same sum.
        var values = makeArray(stride, 0);
        values[0] = 1;
        for (var level = 1; level <= degree; level += 1)
        {
            leftDistances[level] = parameter - knots[spanIndex + 1 - level];
            rightDistances[level] = knots[spanIndex + level] - parameter;
            var saved = 0;
            for (var functionIndex = 0; functionIndex < level; functionIndex += 1)
            {
                const shared = values[functionIndex] /
                    (rightDistances[functionIndex + 1] + leftDistances[level - functionIndex]);
                values[functionIndex] = saved + rightDistances[functionIndex + 1] * shared;
                saved = leftDistances[level - functionIndex] * shared;
            }
            values[level] = saved;
        }
        return values;
    }

    // A2.3's table: basis values of every degree in the upper triangle, the knot differences the
    // recurrence divided by in the lower one, both in one flat array.
    var ndu = makeArray(stride * stride, 0);
    ndu[0] = 1;
    for (var level = 1; level <= degree; level += 1)
    {
        leftDistances[level] = parameter - knots[spanIndex + 1 - level];
        rightDistances[level] = knots[spanIndex + level] - parameter;
        const levelRow = level * stride;
        var saved = 0;
        for (var functionIndex = 0; functionIndex < level; functionIndex += 1)
        {
            const difference = rightDistances[functionIndex + 1] + leftDistances[level - functionIndex];
            ndu[levelRow + functionIndex] = difference;
            const shared = ndu[functionIndex * stride + level - 1] / difference;
            ndu[functionIndex * stride + level] = saved + rightDistances[functionIndex + 1] * shared;
            saved = leftDistances[level - functionIndex] * shared;
        }
        ndu[levelRow + level] = saved;
    }

    var derivatives = makeArray((maxOrder + 1) * stride, 0);
    for (var functionIndex = 0; functionIndex <= degree; functionIndex += 1)
    {
        derivatives[functionIndex] = ndu[functionIndex * stride + degree];
    }

    const effectiveMaxOrder = min(maxOrder, degree);
    // Allocated once. Zeroing both at the top of each basis function reproduces exactly what
    // fresh allocation gave the book's recurrence, including the state the ping-pong leaves the
    // spare buffer in between orders.
    var previousCoefficients = makeArray(degree + 2, 0);
    var currentCoefficients = makeArray(degree + 2, 0);
    for (var functionIndex = 0; functionIndex <= degree; functionIndex += 1)
    {
        for (var slot = 0; slot <= degree + 1; slot += 1)
        {
            previousCoefficients[slot] = 0;
            currentCoefficients[slot] = 0;
        }
        previousCoefficients[0] = 1;
        for (var order = 1; order <= effectiveMaxOrder; order += 1)
        {
            var accumulated = 0;
            const shiftedIndex = functionIndex - order;
            const reducedDegree = degree - order;
            const lowerRow = (reducedDegree + 1) * stride;
            if (functionIndex >= order)
            {
                currentCoefficients[0] = previousCoefficients[0] / ndu[lowerRow + shiftedIndex];
                accumulated = currentCoefficients[0] * ndu[shiftedIndex * stride + reducedDegree];
            }
            const firstTerm = shiftedIndex >= -1 ? 1 : -shiftedIndex;
            const lastTerm = (functionIndex - 1 <= reducedDegree) ? order - 1 : degree - functionIndex;
            for (var termIndex = firstTerm; termIndex <= lastTerm; termIndex += 1)
            {
                currentCoefficients[termIndex] = (previousCoefficients[termIndex] - previousCoefficients[termIndex - 1]) /
                    ndu[lowerRow + shiftedIndex + termIndex];
                accumulated += currentCoefficients[termIndex] * ndu[(shiftedIndex + termIndex) * stride + reducedDegree];
            }
            if (functionIndex <= reducedDegree)
            {
                currentCoefficients[order] = -previousCoefficients[order - 1] / ndu[lowerRow + functionIndex];
                accumulated += currentCoefficients[order] * ndu[functionIndex * stride + reducedDegree];
            }
            derivatives[order * stride + functionIndex] = accumulated;

            const swapRow = previousCoefficients;
            previousCoefficients = currentCoefficients;
            currentCoefficients = swapRow;
        }
    }

    var factor = degree;
    for (var order = 1; order <= effectiveMaxOrder; order += 1)
    {
        const base = order * stride;
        for (var functionIndex = 0; functionIndex <= degree; functionIndex += 1)
        {
            derivatives[base + functionIndex] = factor * derivatives[base + functionIndex];
        }
        factor = factor * (degree - order);
    }
    return derivatives;
}

/**
 * The flat derivative triangle of a stripped surface at (u, v) up to `maxTotalOrder` (0, 1 or
 * 2): [S], [S, S_u, S_v] or [S, S_u, S_v, S_uu, S_uv, S_vv], each entry a plain [x, y, z].
 *
 * Rational input goes through A4.4's quotient rule exactly as the general evaluator does -
 * skipping it is the shape of the evaluateSpline weights-ignoring bug, and every revolved tool
 * face extracts rational.
 */
export function leanSurfaceDerivatives(surface is map, uParameter is number, vParameter is number,
    maxTotalOrder is number) returns array
{
    const uDegree = surface.uDegree;
    const vDegree = surface.vDegree;
    const uSpanIndex = leanEvaluationSpanIndex(surface.uKnots, uDegree, uParameter);
    const vSpanIndex = leanEvaluationSpanIndex(surface.vKnots, vDegree, vParameter);
    // Flat: basis[order * (degree + 1) + functionIndex].
    const uBasis = leanBasisDerivatives(surface.uKnots, uDegree, uSpanIndex, uParameter, maxTotalOrder);
    const vBasis = leanBasisDerivatives(surface.vKnots, vDegree, vSpanIndex, vParameter, maxTotalOrder);
    const uStride = uDegree + 1;
    const firstURowIndex = uSpanIndex - uDegree;
    const firstVColumnIndex = vSpanIndex - vDegree;
    const isRational = surface.isRational == true && surface.weights != undefined;
    const controlPoints = surface.controlPoints;
    const weights = surface.weights;
    const entryCount = LEAN_TRIANGLE_SIZE[maxTotalOrder];

    // Collapse U once per u order into a row of scalar sums per v basis index. Laid out flat as
    // [uOrder * columnCount + vBasisIndex] so the whole collapse is four arrays, not twelve, and
    // the per-order loop is unrolled into three scalars: at three iterations it cost more in
    // loop overhead and in staging the blend values through an array than the work it carried.
    const columnCount = vDegree + 1;
    const secondBase = columnCount;
    const thirdBase = 2 * columnCount;
    const slotCount = (maxTotalOrder + 1) * columnCount;
    var collapsedX = makeArray(slotCount, 0);
    var collapsedY = makeArray(slotCount, 0);
    var collapsedZ = makeArray(slotCount, 0);
    var collapsedW = makeArray(slotCount, 0);
    const wantFirst = maxTotalOrder >= 1;
    const wantSecond = maxTotalOrder >= 2;
    for (var uBasisIndex = 0; uBasisIndex <= uDegree; uBasisIndex += 1)
    {
        const pointRow = controlPoints[firstURowIndex + uBasisIndex];
        const weightRow = isRational ? weights[firstURowIndex + uBasisIndex] : undefined;
        const uValue = uBasis[uBasisIndex];
        const uFirst = wantFirst ? uBasis[uStride + uBasisIndex] : 0;
        const uSecond = wantSecond ? uBasis[2 * uStride + uBasisIndex] : 0;
        for (var vBasisIndex = 0; vBasisIndex < columnCount; vBasisIndex += 1)
        {
            const columnIndex = firstVColumnIndex + vBasisIndex;
            const controlPoint = pointRow[columnIndex];
            const controlX = controlPoint[0];
            const controlY = controlPoint[1];
            const controlZ = controlPoint[2];
            const controlWeight = isRational ? weightRow[columnIndex] : 1;
            const blend0 = isRational ? uValue * controlWeight : uValue;
            collapsedX[vBasisIndex] += blend0 * controlX;
            collapsedY[vBasisIndex] += blend0 * controlY;
            collapsedZ[vBasisIndex] += blend0 * controlZ;
            if (isRational)
            {
                collapsedW[vBasisIndex] += blend0;
            }
            if (wantFirst)
            {
                const slot = secondBase + vBasisIndex;
                const blend1 = isRational ? uFirst * controlWeight : uFirst;
                collapsedX[slot] += blend1 * controlX;
                collapsedY[slot] += blend1 * controlY;
                collapsedZ[slot] += blend1 * controlZ;
                if (isRational)
                {
                    collapsedW[slot] += blend1;
                }
            }
            if (wantSecond)
            {
                const slot = thirdBase + vBasisIndex;
                const blend2 = isRational ? uSecond * controlWeight : uSecond;
                collapsedX[slot] += blend2 * controlX;
                collapsedY[slot] += blend2 * controlY;
                collapsedZ[slot] += blend2 * controlZ;
                if (isRational)
                {
                    collapsedW[slot] += blend2;
                }
            }
        }
    }

    // Blend the collapsed rows in V and apply A4.4 in the SAME pass, one flat triangle entry at
    // a time. The quotient rule for an entry reads only entries the flat order has already
    // produced, so the four numerator arrays this used to build - allocated per evaluation, on
    // the hottest function in the build - are just six scalars carried down the loop.
    var result = makeArray(entryCount);
    var weight = 0;
    var uWeight = 0;
    var vWeight = 0;
    var pointX = 0;
    var pointY = 0;
    var pointZ = 0;
    var uX = 0;
    var uY = 0;
    var uZ = 0;
    var vX = 0;
    var vY = 0;
    var vZ = 0;
    for (var entry = 0; entry < entryCount; entry += 1)
    {
        const base = LEAN_TRIANGLE_U_ORDER[entry] * columnCount;
        const vRow = LEAN_TRIANGLE_V_ORDER[entry] * columnCount;
        var sumX = 0;
        var sumY = 0;
        var sumZ = 0;
        var sumW = 0;
        for (var vBasisIndex = 0; vBasisIndex < columnCount; vBasisIndex += 1)
        {
            const blendValue = vBasis[vRow + vBasisIndex];
            const slot = base + vBasisIndex;
            sumX += blendValue * collapsedX[slot];
            sumY += blendValue * collapsedY[slot];
            sumZ += blendValue * collapsedZ[slot];
        }
        if (isRational)
        {
            for (var vBasisIndex = 0; vBasisIndex < columnCount; vBasisIndex += 1)
            {
                sumW += vBasis[vRow + vBasisIndex] * collapsedW[base + vBasisIndex];
            }
        }
        else
        {
            result[entry] = [sumX, sumY, sumZ];
            continue;
        }

        if (entry == 0)
        {
            weight = sumW;
            pointX = sumX / weight;
            pointY = sumY / weight;
            pointZ = sumZ / weight;
            result[0] = [pointX, pointY, pointZ];
        }
        else if (entry == 1)
        {
            uWeight = sumW;
            uX = (sumX - uWeight * pointX) / weight;
            uY = (sumY - uWeight * pointY) / weight;
            uZ = (sumZ - uWeight * pointZ) / weight;
            result[1] = [uX, uY, uZ];
        }
        else if (entry == 2)
        {
            vWeight = sumW;
            vX = (sumX - vWeight * pointX) / weight;
            vY = (sumY - vWeight * pointY) / weight;
            vZ = (sumZ - vWeight * pointZ) / weight;
            result[2] = [vX, vY, vZ];
        }
        else if (entry == 3)
        {
            result[3] = [(sumX - 2 * uWeight * uX - sumW * pointX) / weight,
                    (sumY - 2 * uWeight * uY - sumW * pointY) / weight,
                    (sumZ - 2 * uWeight * uZ - sumW * pointZ) / weight];
        }
        else if (entry == 4)
        {
            result[4] = [(sumX - vWeight * uX - uWeight * vX - sumW * pointX) / weight,
                    (sumY - vWeight * uY - uWeight * vY - sumW * pointY) / weight,
                    (sumZ - vWeight * uZ - uWeight * vZ - sumW * pointZ) / weight];
        }
        else
        {
            result[5] = [(sumX - 2 * vWeight * vX - sumW * pointX) / weight,
                    (sumY - 2 * vWeight * vY - sumW * pointY) / weight,
                    (sumZ - 2 * vWeight * vZ - sumW * pointZ) / weight];
        }
    }
    return result;
}

/** The surface point alone, as a plain [x, y, z]. */
/**
 * Every derivative of a B-spline CURVE up to `maxOrder`, as flat scalar triples
 * `[x, y, z, x', y', z', ...]` - the curve twin of `leanSurfaceDerivatives`, and for the same
 * reason (spec 6.0.3, spec 11.2 lever D).
 *
 * The analytic layer's generators are tiny - a revolved ellipsoid's is degree 2 with five control
 * points - and every closed-form contact evaluation reads one. Routed through
 * `splineRefinementUtils`' general `evaluateBSplineCurveDerivatives` that measured **~470
 * microseconds a call**, which is what a fully general rational NURBS evaluator costs when it
 * allocates Vectors and a binomial table for a five-point quadratic. At a few hundred calls per
 * station and a few thousand per self test, that evaluator WAS the analytic route's runtime.
 *
 * Same arithmetic, same NURBS Book A2.3 recurrence and A4.2 quotient rule, same summation order -
 * on plain numbers, with the control-point row hoisted and no intermediate Vector allocated. It is
 * the identical trade `leanSurfaceDerivatives` already makes one dimension up, and the agreement
 * against the general evaluator is asserted rather than assumed (see the self test).
 *
 * Returns a flat array of `3 * (maxOrder + 1)` plain numbers, meters implied.
 */
export function leanCurveDerivatives(curve is map, parameter is number, maxOrder is number) returns array
{
    const degree = curve.degree;
    const knots = curve.knots;
    const spanIndex = leanEvaluationSpanIndex(knots, degree, parameter);
    // Flat: basis[order * (degree + 1) + functionIndex].
    const basis = leanBasisDerivatives(knots, degree, spanIndex, parameter, maxOrder);
    const stride = degree + 1;
    const firstIndex = spanIndex - degree;
    const isRational = curve.isRational == true && curve.weights != undefined;
    const controlPoints = curve.controlPoints;
    const weights = curve.weights;

    var numerator = makeArray(3 * (maxOrder + 1), 0);
    var weightDerivatives = makeArray(maxOrder + 1, 0);
    for (var order = 0; order <= maxOrder; order += 1)
    {
        var sumX = 0;
        var sumY = 0;
        var sumZ = 0;
        var sumW = 0;
        const orderBase = order * stride;
        for (var basisIndex = 0; basisIndex <= degree; basisIndex += 1)
        {
            const controlIndex = firstIndex + basisIndex;
            const point = controlPoints[controlIndex];
            var blend = basis[orderBase + basisIndex];
            if (isRational)
            {
                blend = blend * weights[controlIndex];
                sumW += blend;
            }
            sumX += blend * point[0];
            sumY += blend * point[1];
            sumZ += blend * point[2];
        }
        numerator[3 * order] = sumX;
        numerator[3 * order + 1] = sumY;
        numerator[3 * order + 2] = sumZ;
        weightDerivatives[order] = sumW;
    }
    if (!isRational)
    {
        return numerator;
    }

    // A4.2's quotient rule, unrolled onto the flat triples. maxOrder is 0, 1 or 2 here, so the
    // binomial coefficients are literals rather than a table.
    var result = makeArray(3 * (maxOrder + 1), 0);
    const inverseWeight = 1 / weightDerivatives[0];
    result[0] = numerator[0] * inverseWeight;
    result[1] = numerator[1] * inverseWeight;
    result[2] = numerator[2] * inverseWeight;
    if (maxOrder >= 1)
    {
        result[3] = (numerator[3] - weightDerivatives[1] * result[0]) * inverseWeight;
        result[4] = (numerator[4] - weightDerivatives[1] * result[1]) * inverseWeight;
        result[5] = (numerator[5] - weightDerivatives[1] * result[2]) * inverseWeight;
    }
    if (maxOrder >= 2)
    {
        result[6] = (numerator[6] - 2 * weightDerivatives[1] * result[3] -
                weightDerivatives[2] * result[0]) * inverseWeight;
        result[7] = (numerator[7] - 2 * weightDerivatives[1] * result[4] -
                weightDerivatives[2] * result[1]) * inverseWeight;
        result[8] = (numerator[8] - 2 * weightDerivatives[1] * result[5] -
                weightDerivatives[2] * result[2]) * inverseWeight;
    }
    return result;
}

export function leanSurfacePoint(surface is map, uParameter is number, vParameter is number) returns array
{
    return leanSurfaceDerivatives(surface, uParameter, vParameter, 0)[0];
}

/** rows * (x, y, z) for a 3x3 Matrix or array of rows, as a plain [x, y, z]. */
function applyRowsToTriple(rows, x is number, y is number, z is number) returns array
{
    const rowX = rows[0];
    const rowY = rows[1];
    const rowZ = rows[2];
    return [rowX[0] * x + rowX[1] * y + rowX[2] * z,
            rowY[0] * x + rowY[1] * y + rowY[2] * z,
            rowZ[0] * x + rowZ[1] * y + rowZ[2] * z];
}

/** The dot product of two plain triples, associated left to right as std `dot` associates it. */
function dotTriples(first is array, second is array) returns number
{
    return first[0] * second[0] + first[1] * second[1] + first[2] * second[2];
}

/** The length of a plain triple. */
function normTriple(triple is array) returns number
{
    return sqrt(triple[0] * triple[0] + triple[1] * triple[1] + triple[2] * triple[2]);
}

/** The cross product of two plain triples, component for component as std `cross` writes it. */
function crossTriples(first is array, second is array) returns array
{
    return [first[1] * second[2] - second[1] * first[2],
            first[2] * second[0] - second[2] * first[0],
            first[0] * second[1] - second[0] * first[1]];
}

/** Sum of two plain triples. */
function addTriples(first is array, second is array) returns array
{
    return [first[0] + second[0], first[1] + second[1], first[2] + second[2]];
}

/**
 * The distance between two plain triples. The sqrt is the point here - this is arc length, the
 * one place in the module where a squared measure would be the wrong answer rather than a
 * cheaper one - but the subtraction and the sum of squares do not need std `operator-` and
 * `dot`, which between them run two interpreted three-iteration loops and allocate a
 * `@subArray`, once per marched point.
 */
function distanceBetweenTriples(first is array, second is array) returns number
{
    const deltaX = first[0] - second[0];
    const deltaY = first[1] - second[1];
    const deltaZ = first[2] - second[2];
    return sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
}

/** Difference of two plain triples, first - second. */
function leanResidual(first is array, second is array) returns array
{
    return [first[0] - second[0], first[1] - second[1], first[2] - second[2]];
}

/**
 * The parametric normal N = S_u x S_v and its two parametric derivatives, from a flat triangle:
 * [N, N_u, N_v].
 */
function leanNormalAndDerivatives(derivatives is array) returns array
{
    const uTangent = derivatives[1];
    const vTangent = derivatives[2];
    const uvDerivative = derivatives[4];
    return [crossTriples(uTangent, vTangent),
            addTriples(crossTriples(derivatives[3], vTangent), crossTriples(uTangent, uvDerivative)),
            addTriples(crossTriples(uvDerivative, vTangent), crossTriples(uTangent, derivatives[5]))];
}

/** A(t) * toolTriple + b(t), as a plain [x, y, z]. */
function leanLift(motionSample is map, toolTriple is array) returns array
{
    return addTriples(applyRowsToTriple(motionSample.rotation,
                toolTriple[0], toolTriple[1], toolTriple[2]), motionSample.translation);
}

/** A'(t) * toolTriple + b'(t) - the world velocity of the tool point at toolTriple. */
function leanVelocity(motionSample is map, toolTriple is array) returns array
{
    return addTriples(applyRowsToTriple(motionSample.rotationDerivative,
                toolTriple[0], toolTriple[1], toolTriple[2]), motionSample.translationDerivative);
}

/** A''(t) * toolTriple + b''(t). */
function leanAcceleration(motionSample is map, toolTriple is array) returns array
{
    return addTriples(applyRowsToTriple(motionSample.rotationSecondDerivative,
                toolTriple[0], toolTriple[1], toolTriple[2]), motionSample.translationSecondDerivative);
}

/**
 * The motion state at global parameter t, straight from the stripped motion splines:
 * { rotation, rotationDerivative, rotationSecondDerivative {matrices},
 *   translation, translationDerivative, translationSecondDerivative {Vectors} }.
 * This is the module's own evaluation path, independent of the coefficient assembly.
 *
 * A sample carries the t it was taken at, and handing a SAMPLE back to this function returns it
 * unchanged instead of re-evaluating four spline curves. That is the whole of spec 11.2's lever
 * A: fourteen entry points in this module take a tGlobal and loop under it - the section march,
 * its corrector, the arc-length resample, the lift, the meridian seed - and every pointwise
 * evaluation inside those loops was recomputing the same four curve derivatives. Freezing at
 * the top of a fixed-t function and passing the sample down in the motion's place leaves every
 * call site below reading exactly as it did, and costs one map lookup where it used to cost
 * four spline evaluations.
 *
 * The t is checked rather than ignored. A frozen sample that leaks into a loop which genuinely
 * varies t - a contact-root bisection, a branch time extremum - would otherwise return one
 * station's state for every t it was asked about, and the answer would look plausible.
 */
export function evaluateMotionSample(strippedMotion is map, t is number, maxOrder is number) returns map
{
    if (strippedMotion.sampledAt != undefined)
    {
        if (strippedMotion.sampledAt != t)
        {
            throw "solidSweepUtils: a motion sample frozen at t = " ~ strippedMotion.sampledAt ~
                " was evaluated at t = " ~ t ~ ". Freeze a motion only where t is held fixed for " ~
                "the whole subtree that reads it.";
        }
        if (strippedMotion.sampledOrder != undefined && strippedMotion.sampledOrder < maxOrder)
        {
            throw "solidSweepUtils: a motion sample frozen to order " ~ strippedMotion.sampledOrder ~
                " was read at order " ~ maxOrder ~ ". Freeze at the highest order the subtree reads.";
        }
        return strippedMotion;
    }
    // The lean curve evaluator, not the general one. A motion column is a non-rational cubic on
    // plain-number control points, and the general NURBS reader allocates Vectors and a binomial
    // table to serve it. Both run the same A2.3 recurrence in the same summation order; this one
    // returns its orders flat, indexed `3 * order + component`.
    const xDerivatives = leanCurveDerivatives(strippedMotion.columnX, t, maxOrder);
    const yDerivatives = leanCurveDerivatives(strippedMotion.columnY, t, maxOrder);
    const zDerivatives = leanCurveDerivatives(strippedMotion.columnZ, t, maxOrder);
    const translationDerivatives = leanCurveDerivatives(strippedMotion.translation, t, maxOrder);
    var sample = {
            "rotation" : matrix([[xDerivatives[0], yDerivatives[0], zDerivatives[0]],
                        [xDerivatives[1], yDerivatives[1], zDerivatives[1]],
                        [xDerivatives[2], yDerivatives[2], zDerivatives[2]]]),
            "rotationDerivative" : matrix([[xDerivatives[3], yDerivatives[3], zDerivatives[3]],
                        [xDerivatives[4], yDerivatives[4], zDerivatives[4]],
                        [xDerivatives[5], yDerivatives[5], zDerivatives[5]]]),
            "translation" : vector(translationDerivatives[0], translationDerivatives[1],
                translationDerivatives[2]),
            "translationDerivative" : vector(translationDerivatives[3], translationDerivatives[4],
                translationDerivatives[5]),
            "sampledAt" : t,
            "sampledOrder" : maxOrder
        };
    if (maxOrder >= 2)
    {
        sample.rotationSecondDerivative =
            matrix([[xDerivatives[6], yDerivatives[6], zDerivatives[6]],
                    [xDerivatives[7], yDerivatives[7], zDerivatives[7]],
                    [xDerivatives[8], yDerivatives[8], zDerivatives[8]]]);
        sample.translationSecondDerivative = vector(translationDerivatives[6],
            translationDerivatives[7], translationDerivatives[8]);
    }
    return sample;
}

/**
 * The motion state at t through second order - what a caller reading acceleration needs, and the
 * order every consumer got before the order became a choice.
 */
export function evaluateMotionSample(strippedMotion is map, t is number) returns map
{
    return evaluateMotionSample(strippedMotion, t, 2);
}

/**
 * Pointwise envelope function f(u, v, t) evaluated end to end from the original stripped
 * splines - the polish/certification path, and the independent check of the coefficient
 * assembly. u, v are in the surface's knot domain; t in the motion's.
 */
export function evaluateEnvelopePointwise(strippedMotion is map, strippedSurface is map, u is number, v is number, t is number) returns number
{
    const derivatives = leanSurfaceDerivatives(strippedSurface, u, v, 1);
    const normal = crossTriples(derivatives[1], derivatives[2]);
    const sample = evaluateMotionSample(strippedMotion, t);
    return dotTriples(applyRowsToTriple(sample.rotation, normal[0], normal[1], normal[2]),
        leanVelocity(sample, derivatives[0]));
}

/**
 * Pointwise time derivative f_t(u, v, t): with N fixed in t,
 * f_t = <A' N, A' S + b'> + <A N, A'' S + b''>.
 */
export function evaluateEnvelopeTimeDerivativePointwise(strippedMotion is map, strippedSurface is map, u is number, v is number, t is number) returns number
{
    const derivatives = leanSurfaceDerivatives(strippedSurface, u, v, 1);
    const normal = crossTriples(derivatives[1], derivatives[2]);
    const surfacePoint = derivatives[0];
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = leanVelocity(sample, surfacePoint);
    const acceleration = leanAcceleration(sample, surfacePoint);
    return dotTriples(applyRowsToTriple(sample.rotationDerivative, normal[0], normal[1], normal[2]), velocity) +
        dotTriples(applyRowsToTriple(sample.rotation, normal[0], normal[1], normal[2]), acceleration);
}

/**
 * The contact (strip) function at one sample: g = <A(t) n, A'(t) p + b'(t)> for a one-sided
 * normal n at a point p. This is the co-edge strip function of spec section 6.2 evaluated at
 * one (sample, t), and equally the sharp-vertex function when p is the vertex and n a cone
 * normal.
 */
export function evaluateContactFunctionAtPoint(strippedMotion is map, normal is Vector, point is Vector, t is number) returns number
{
    // Order 1: the contact function reads A, A' and b' and no acceleration term.
    const sample = evaluateMotionSample(strippedMotion, t, 1);
    return dotTriples(applyRowsToTriple(sample.rotation, normal[0], normal[1], normal[2]),
        leanVelocity(sample, point));
}

/**
 * The strip function on a co-edge side's shared sample arrays: g[sampleIndex][tIndex] over
 * the given global t values. normals and points are extraction's sideNormals / edgePoints
 * arrays - the SAME arrays every adjacent consumer reads, which is what keeps seams exact.
 */
export function buildStripFunctionGrid(strippedMotion is map, normals is array, points is array, tValues is array) returns array
{
    // One motion sample per COLUMN of the grid rather than one per cell: the strip function
    // reads the motion only through A(t), and this grid asks about the same t once per sample.
    var frozenMotions = makeArray(size(tValues));
    for (var tIndex = 0; tIndex < size(tValues); tIndex += 1)
    {
        frozenMotions[tIndex] = evaluateMotionSample(strippedMotion, tValues[tIndex]);
    }
    var grid = makeArray(size(normals));
    for (var sampleIndex = 0; sampleIndex < size(normals); sampleIndex += 1)
    {
        var row = makeArray(size(tValues));
        for (var tIndex = 0; tIndex < size(tValues); tIndex += 1)
        {
            row[tIndex] = evaluateContactFunctionAtPoint(frozenMotions[tIndex], normals[sampleIndex],
                points[sampleIndex], tValues[tIndex]);
        }
        grid[sampleIndex] = row;
    }
    return grid;
}

/** Bezier-segment control points (array of 3-vectors) to a Bernstein vector [x, y, z] arrays. */
function componentCoefficientArrays(segmentControlPoints is array) returns array
{
    var components = makeArray(3);
    for (var component = 0; component < 3; component += 1)
    {
        var coefficients = makeArray(size(segmentControlPoints));
        for (var pointIndex = 0; pointIndex < size(segmentControlPoints); pointIndex += 1)
        {
            coefficients[pointIndex] = segmentControlPoints[pointIndex][component];
        }
        components[component] = coefficients;
    }
    return components;
}

/** Patch control net (grid of 3-vectors) to a Bernstein vector grid [xGrid, yGrid, zGrid]. */
function componentCoefficientGrids(patchControlPoints is array) returns array
{
    var components = makeArray(3);
    for (var component = 0; component < 3; component += 1)
    {
        var grid = makeArray(size(patchControlPoints));
        for (var rowIndex = 0; rowIndex < size(patchControlPoints); rowIndex += 1)
        {
            var row = makeArray(size(patchControlPoints[rowIndex]));
            for (var columnIndex = 0; columnIndex < size(patchControlPoints[rowIndex]); columnIndex += 1)
            {
                row[columnIndex] = patchControlPoints[rowIndex][columnIndex][component];
            }
            grid[rowIndex] = row;
        }
        components[component] = grid;
    }
    return components;
}

/** Differentiate a Bernstein vector on local [0, 1], rescaled to global units by spanWidth. */
function differentiateBernsteinVector(vectorCoefficients is array, spanWidth is number) returns array
{
    var derivative = makeArray(3);
    for (var component = 0; component < 3; component += 1)
    {
        derivative[component] = scaleBernstein(differentiateBernstein(vectorCoefficients[component]), 1 / spanWidth);
    }
    return derivative;
}

/** Interval product of two {minimum, maximum} ranges (bernsteinRange / bernsteinGridRange form). */
function multiplyIntervals(rangeA is map, rangeB is map) returns map
{
    const products = [rangeA.minimum * rangeB.minimum, rangeA.minimum * rangeB.maximum,
                rangeA.maximum * rangeB.minimum, rangeA.maximum * rangeB.maximum];
    return {
            "minimum" : min(min(products[0], products[1]), min(products[2], products[3])),
            "maximum" : max(max(products[0], products[1]), max(products[2], products[3]))
        };
}

/** A 3x3 matrix from three column Vectors. */
function matrixFromColumns(columnX is Vector, columnY is Vector, columnZ is Vector) returns Matrix
{
    return matrix([[columnX[0], columnY[0], columnZ[0]],
                [columnX[1], columnY[1], columnZ[1]],
                [columnX[2], columnY[2], columnZ[2]]]);
}


// ============================= Closed-form contact for analytic faces (spec 6.5) =============================

// ---- Typed contact frames: one type per class, dispatched by overload resolution ----
//
// The alternative this replaces was a `kind` field read by an if-ladder in every consumer, which a
// REVOLVED frame walked past four times on every one of a few hundred evaluations per station. The
// cost is the smaller half of the argument; the structural half is that a ladder makes a new class
// an EDIT to four existing functions, while an overload set makes it an addition. Plug a class in by
// declaring its type and writing its overloads - nothing already working is touched.
//
// The predicates are mutually exclusive on `kind`, which is what makes resolution unambiguous, and
// they check the fields their overloads actually read so that a malformed frame fails at the call
// boundary rather than deep inside an evaluator.

/**
 * The fields every analytic contact frame carries. NOT a type - a shared predicate the seven
 * specific ones call.
 *
 * There is no umbrella TYPE here on purpose: a FeatureScript value carries ONE type tag and there is
 * no subtyping, so a frame tagged `PlaneContactFrame` stops satisfying an `AnalyticContactFrame`
 * parameter and the call fails to resolve. Frames therefore stay untagged maps; the specific types
 * below exist to be written in PARAMETER positions, where their predicates run against the map and
 * pick the overload. Functions that do not branch on class simply take a map.
 */
export predicate canBeAnalyticContactFrame(value)
{
    value is map;
    value.kind is AnalyticSurfaceKind;
    value.origin is Vector;
    value.basis is array;
}

/** A plane's contact frame: e3 is the plane normal, (u, v) are signed distances along e1, e2. */
export type PlaneContactFrame typecheck canBePlaneContactFrame;

/** @internal */
export predicate canBePlaneContactFrame(value)
{
    canBeAnalyticContactFrame(value);
    value.kind == AnalyticSurfaceKind.PLANE;
}

/** A cylinder's: (theta, z) around and along the axis. */
export type CylinderContactFrame typecheck canBeCylinderContactFrame;

/** @internal */
export predicate canBeCylinderContactFrame(value)
{
    canBeAnalyticContactFrame(value);
    value.kind == AnalyticSurfaceKind.CYLINDER;
    value.radius is number;
}

/** A cone's: (theta, l), l measured from the APEX along the ruling. */
export type ConeContactFrame typecheck canBeConeContactFrame;

/** @internal */
export predicate canBeConeContactFrame(value)
{
    canBeAnalyticContactFrame(value);
    value.kind == AnalyticSurfaceKind.CONE;
    value.halfAngle is number;
}

/** A sphere's: (longitude, latitude) from e1 and the equator. */
export type SphereContactFrame typecheck canBeSphereContactFrame;

/** @internal */
export predicate canBeSphereContactFrame(value)
{
    canBeAnalyticContactFrame(value);
    value.kind == AnalyticSurfaceKind.SPHERE;
    value.radius is number;
}

/** A torus's: (theta around the axis, phi around the tube from the outer equator). */
export type TorusContactFrame typecheck canBeTorusContactFrame;

/** @internal */
export predicate canBeTorusContactFrame(value)
{
    canBeAnalyticContactFrame(value);
    value.kind == AnalyticSurfaceKind.TORUS;
    value.radius is number;
    value.minorRadius is number;
}

/** A surface of revolution's: (generator parameter, theta about the axis). Carries the generator. */
export type RevolvedContactFrame typecheck canBeRevolvedContactFrame;

/** @internal */
export predicate canBeRevolvedContactFrame(value)
{
    canBeAnalyticContactFrame(value);
    value.kind == AnalyticSurfaceKind.REVOLVED;
    value.profile is map;
}

/** An extrusion's: (cross-section parameter, distance along e3). Carries the cross section. */
export type ExtrudedContactFrame typecheck canBeExtrudedContactFrame;

/** @internal */
export predicate canBeExtrudedContactFrame(value)
{
    canBeAnalyticContactFrame(value);
    value.kind == AnalyticSurfaceKind.EXTRUDED;
    value.profile is map;
}

/**
 * Whether an evSurfaceDefinition value is one this module handles FROM THE DEFINITION ALONE -
 * the five parameter-driven classes. The two profile-driven ones (spec 6.5.1) are also handled
 * by this module, but their definitions carry no shape, so they are recognized at extraction by
 * classifyProfileDrivenSurface and built by analyticProfileFrame instead.
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
export function stripAnalyticSurface(surfaceDefinition is Plane) returns map
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
        } as PlaneContactFrame;
}

export function stripAnalyticSurface(surfaceDefinition is Cylinder) returns map
{
    return mergeMaps(coordSystemFrame(surfaceDefinition.coordSystem), {
                "kind" : AnalyticSurfaceKind.CYLINDER,
                "radius" : surfaceDefinition.radius / meter
            }) as CylinderContactFrame;
}

export function stripAnalyticSurface(surfaceDefinition is Cone) returns map
{
    return mergeMaps(coordSystemFrame(surfaceDefinition.coordSystem), {
                "kind" : AnalyticSurfaceKind.CONE,
                "halfAngle" : surfaceDefinition.halfAngle / radian
            }) as ConeContactFrame;
}

export function stripAnalyticSurface(surfaceDefinition is Sphere) returns map
{
    return mergeMaps(coordSystemFrame(surfaceDefinition.coordSystem), {
                "kind" : AnalyticSurfaceKind.SPHERE,
                "radius" : surfaceDefinition.radius / meter
            }) as SphereContactFrame;
}

export function stripAnalyticSurface(surfaceDefinition is Torus) returns map
{
    return mergeMaps(coordSystemFrame(surfaceDefinition.coordSystem), {
                "kind" : AnalyticSurfaceKind.TORUS,
                "radius" : surfaceDefinition.radius / meter,
                "minorRadius" : surfaceDefinition.minorRadius / meter
            }) as TorusContactFrame;
}

// NO untyped catch-all overload here, deliberately. An untyped parameter matches every value,
// including a Plane, so adding one makes every call ambiguous against the typed overloads rather
// than giving unsupported input a friendlier error - which is what broke this element the first time
// these overloads went in. A caller that needs to ask first has `isAnalyticContactSurface`; one that
// does not gets FeatureScript's own no-matching-function error, which names the type it was handed.

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

/**
 * Build the frame for one of the two PROFILE-driven analytic classes (spec 6.5.1). Neither
 * carries its shape in evSurfaceDefinition, so neither can be built by stripAnalyticSurface: the
 * generator is recovered at extraction by cutting the face with a plane and reading the resulting
 * edge, and this function is what turns that generator into the frame the closed forms consume.
 *
 * The profile's control points are mapped into the frame's own basis ONCE, here. An affine map
 * carries a NURBS exactly through its control points with weights untouched, so the local profile
 * is the same curve, not a refit of it - which is what keeps "exact generating profile" true all
 * the way to the contact solve.
 *
 * kind {AnalyticSurfaceKind} : REVOLVED or EXTRUDED.
 * origin {Vector} : plain numbers, meters implied - a point on the axis (REVOLVED) or any point
 *     of the cutting plane (EXTRUDED). Only its offset from the profile matters.
 * axis {Vector} : the revolve axis, or the extrusion direction. Normalized here.
 * firstInPlane {Vector} : the direction that becomes e1. For REVOLVED it MUST point to the side
 *     of the axis the profile lies on, so that r(u) >= 0; its axial part is projected out.
 * worldProfile {map} : the unit-stripped generator curve in world coordinates.
 *
 * Returns the frame shape stripAnalyticSurface returns, plus:
 *     profile {map} : the same curve with control points in local coordinates,
 *     profileStart, profileEnd {number} : its parameter domain,
 *     profileOutOfPlane {number} : the largest local out-of-plane control point coordinate -
 *         local y for REVOLVED (the profile must lie in the e1-e3 half plane), local z for
 *         EXTRUDED (the cross section must lie in the e1-e2 plane). A diagnostic, not a gate:
 *         a nonzero value means the recovered generator does not match the recovered axis.
 */
export function analyticProfileFrame(kind is AnalyticSurfaceKind, origin is Vector, axis is Vector,
    firstInPlane is Vector, worldProfile is map) returns map
{
    const axisDirection = normalize(axis);
    const radialPart = firstInPlane - dot(firstInPlane, axisDirection) * axisDirection;
    if (squaredNorm(radialPart) < 1e-20)
    {
        throw "solidSweepUtils analytic contact: the in-plane direction for a " ~ kind ~
            " frame is parallel to its axis, so no radial direction can be built from it.";
    }
    const firstAxis = normalize(radialPart);
    const basis = [firstAxis, cross(axisDirection, firstAxis), axisDirection];

    var localPoints = makeArray(size(worldProfile.controlPoints));
    var outOfPlane = 0;
    for (var pointIndex = 0; pointIndex < size(localPoints); pointIndex += 1)
    {
        const offset = worldProfile.controlPoints[pointIndex] - origin;
        localPoints[pointIndex] = vector(dot(basis[0], offset), dot(basis[1], offset), dot(basis[2], offset));
        outOfPlane = max(outOfPlane,
            abs(localPoints[pointIndex][kind == AnalyticSurfaceKind.REVOLVED ? 1 : 2]));
    }
    const profile = {
            "degree" : worldProfile.degree,
            "knots" : worldProfile.knots,
            "controlPoints" : localPoints,
            "isRational" : worldProfile.isRational,
            "weights" : worldProfile.weights,
            "isPeriodic" : worldProfile.isPeriodic
        };
    const frame = {
            "kind" : kind,
            "origin" : origin,
            "basis" : basis,
            "radius" : undefined,
            "minorRadius" : undefined,
            "halfAngle" : undefined,
            "profile" : profile,
            "profileStart" : profile.knots[profile.degree],
            "profileEnd" : profile.knots[size(profile.knots) - 1 - profile.degree],
            "profileOutOfPlane" : outOfPlane
        };
    // One tag or the other: `as` needs a literal type name, so this is the one place a class is
    // chosen by a branch - a constructor, run once per face, not an evaluator run per sample.
    return kind == AnalyticSurfaceKind.REVOLVED ?
        frame as RevolvedContactFrame : frame as ExtrudedContactFrame;
}

/**
 * The local generator point and its first two derivatives at one profile parameter, as
 * result[order]. Rational-correct (splineRefinementUtils applies the NURBS Book quotient rule),
 * which is the whole point of spec 6.0.2: a one-parameter denominator clears, so a rational
 * profile is an ordinary curve here rather than a two-parameter ratio the coefficient path
 * cannot screen.
 */
export function analyticProfileDerivatives(frame is map, u is number, maxOrder is number) returns array
{
    // Lean core, Vector-shaped result: callers index this as [order][component], so the API stays
    // while the ~470 microsecond general evaluator does not.
    const flat = leanCurveDerivatives(frame.profile, u, maxOrder);
    var derivatives = makeArray(maxOrder + 1, vector(0, 0, 0));
    for (var order = 0; order <= maxOrder; order += 1)
    {
        derivatives[order] = vector(flat[3 * order], flat[3 * order + 1], flat[3 * order + 2]);
    }
    return derivatives;
}

/** `sampleCount` parameters spanning a profile-driven frame's own domain, ends included. */
export function analyticProfileParameters(frame is map, sampleCount is number) returns array
{
    const total = max(2, sampleCount);
    var parameters = makeArray(total);
    for (var index = 0; index < total; index += 1)
    {
        parameters[index] = frame.profileStart +
            (frame.profileEnd - frame.profileStart) * index / (total - 1);
    }
    return parameters;
}

/**
 * The generator's position and first derivative at every parameter `analyticProfileParameters`
 * returns, as one row per parameter in the flat form `leanCurveDerivatives` produces.
 *
 * The grid is a property of the FRAME, so this is the whole table a multi-station fit needs: the
 * same parameters are solved at t0, at t1 and at every station and midpoint between them, and
 * without this each one repeats the identical de Boor evaluation. Ninety-six parameters over nine
 * stations is eight hundred and sixty-four evaluations of a curve that never moved.
 */
export function analyticGeneratorDerivativeTable(frame is map, sampleCount is number) returns array
{
    const parameters = analyticProfileParameters(frame, sampleCount);
    var table = makeArray(size(parameters));
    for (var index = 0; index < size(parameters); index += 1)
    {
        table[index] = leanCurveDerivatives(frame.profile, parameters[index], 1);
    }
    return table;
}

/**
 * Pull one motion station back into a face's own frame: the twelve numbers the closed forms
 * below consume, and the ONLY place the motion appears.
 *
 * motionSample is the envelope layer's evaluateMotionSample shape - { rotation, rotationDerivative,
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
 *   REVOLVED (u, theta) : u the generator's own parameter, theta around the axis from e1.
 *   EXTRUDED (u, v) : u the cross section's own parameter, v the distance along e3.
 *
 * Returns { point {Vector}, normal {Vector} } in local coordinates. Normals point away from the
 * material for the standard outward orientation of each class; the zero set of f does not depend
 * on that sign, and orientation is decided separately (spec 6.3 step 5).
 *
 * The five parameter-driven classes return a UNIT normal. REVOLVED and EXTRUDED return the
 * unnormalized S_u x S_v instead: its length depends on u alone, so it is a positive u-only
 * rescaling of f that moves no root and keeps the closed forms polynomial in the profile's
 * derivatives - a normalization would put a square root of them under every coefficient and
 * would divide by zero at a stationary point of the generator. Their |f| bounds are therefore
 * bounds on the rescaled f, which is stated where they are computed.
 */
export function analyticLocalPointAndNormal(frame is PlaneContactFrame, u is number, v is number) returns map
{
    return { "point" : vector(u, v, 0), "normal" : vector(0, 0, 1) };
}

export function analyticLocalPointAndNormal(frame is CylinderContactFrame, u is number, v is number) returns map
{
    const radial = vector(cos(u * radian), sin(u * radian), 0);
    return { "point" : frame.radius * radial + vector(0, 0, v), "normal" : radial };
}

export function analyticLocalPointAndNormal(frame is ConeContactFrame, u is number, v is number) returns map
{
    const cosHalf = cos(frame.halfAngle * radian);
    const sinHalf = sin(frame.halfAngle * radian);
    const radial = vector(cos(u * radian), sin(u * radian), 0);
    return {
            "point" : v * (sinHalf * radial + vector(0, 0, cosHalf)),
            "normal" : cosHalf * radial - vector(0, 0, sinHalf)
        };
}

export function analyticLocalPointAndNormal(frame is SphereContactFrame, u is number, v is number) returns map
{
    const radial = vector(cos(v * radian) * cos(u * radian), cos(v * radian) * sin(u * radian),
            sin(v * radian));
    return { "point" : frame.radius * radial, "normal" : radial };
}

export function analyticLocalPointAndNormal(frame is TorusContactFrame, u is number, v is number) returns map
{
    const axial = vector(cos(u * radian), sin(u * radian), 0);
    return {
            "point" : (frame.radius + frame.minorRadius * cos(v * radian)) * axial +
                vector(0, 0, frame.minorRadius * sin(v * radian)),
            "normal" : cos(v * radian) * axial + vector(0, 0, sin(v * radian))
        };
}

/**
 * S = (r(u) cos v, r(u) sin v, z(u)), and S_u x S_v = r (-z' cos v, -z' sin v, r'). The positive
 * factor r is dropped: it scales f without moving its zero set, and dropping it keeps the normal
 * finite on a generator that reaches the axis.
 */
export function analyticLocalPointAndNormal(frame is RevolvedContactFrame, u is number, v is number) returns map
{
    return analyticLocalPointAndNormal(frame, leanCurveDerivatives(frame.profile, u, 1), v);
}

/**
 * Same, on generator derivatives already in hand.
 *
 * A contact point is nearly always produced at a parameter whose r, z, r' and z' the caller just
 * used to solve for theta there, so taking `u` obliges it to throw that away and de Boor the same
 * curve a second time - which measured as 2543 evaluations, a ninth of the build.
 */
export function analyticLocalPointAndNormal(frame is RevolvedContactFrame, derivatives is array,
    v is number) returns map
{
    const radius = derivatives[0];
    const height = derivatives[2];
    const radiusSlope = derivatives[3];
    const heightSlope = derivatives[5];
    const cosine = cos(v * radian);
    const sine = sin(v * radian);
    return {
            "point" : vector(radius * cosine, radius * sine, height),
            "normal" : vector(-heightSlope * cosine, -heightSlope * sine, radiusSlope)
        };
}

/**
 * S = C(u) + v e3, so S_v = e3 and N = C' x e3 does not depend on v at all - which is what makes f
 * linear in v, the same ruling form the cylinder and cone already solve.
 */
export function analyticLocalPointAndNormal(frame is ExtrudedContactFrame, u is number, v is number) returns map
{
    const derivatives = leanCurveDerivatives(frame.profile, u, 1);
    return {
            "point" : vector(derivatives[0], derivatives[1], derivatives[2] + v),
            "normal" : vector(derivatives[4], -derivatives[3], 0)
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
    const cosine = polynomial.cosine;
    const sine = polynomial.sine;
    const count = size(cosine);
    if (count == 0)
    {
        return 0;
    }
    // Harmonic 0 is the constant: cos 0 is 1 and sin 0 is 0, so the general term below would buy
    // two transcendental calls to multiply by one and by zero. Harmonics 1 and 2 are the whole
    // degree-2 form this module solves, and 2x comes from x by the double angle rather than from
    // a second pair of calls - cos 2x as (c - s)(c + s), which unlike 2c^2 - 1 cannot cancel.
    var total = cosine[0];
    if (count > 1)
    {
        const c = cos(x * radian);
        const s = sin(x * radian);
        total += cosine[1] * c + sine[1] * s;
        if (count > 2)
        {
            total += cosine[2] * ((c - s) * (c + s)) + sine[2] * (2 * s * c);
            for (var harmonic = 3; harmonic < count; harmonic += 1)
            {
                total += cosine[harmonic] * cos(harmonic * x * radian) +
                    sine[harmonic] * sin(harmonic * x * radian);
            }
        }
    }
    return total;
}

/** P'(x). Exact - term by term, no differencing. */
export function trigPolynomialDerivative(polynomial is map, x is number) returns number
{
    const cosine = polynomial.cosine;
    const sine = polynomial.sine;
    const count = size(cosine);
    if (count < 2)
    {
        return 0;
    }
    // Same two harmonics, same double angle - the constant differentiates away, so this one starts
    // at harmonic 1 rather than treating it separately.
    const c = cos(x * radian);
    const s = sin(x * radian);
    var total = sine[1] * c - cosine[1] * s;
    if (count > 2)
    {
        total += 2 * (sine[2] * ((c - s) * (c + s)) - cosine[2] * (2 * s * c));
        for (var harmonic = 3; harmonic < count; harmonic += 1)
        {
            total += harmonic * (sine[harmonic] * cos(harmonic * x * radian) -
                        cosine[harmonic] * sin(harmonic * x * radian));
        }
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
/**
 * Two guarded Newton steps on a trigonometric polynomial, to finish a root the closed form has
 * already isolated. Guarded means a step is kept only if it reduces |P|, so a vanishing derivative
 * at a double root leaves the input untouched rather than throwing it somewhere else.
 */
function polishTrigRoot(polynomial is map, root is number) returns number
{
    var best = root;
    var bestValue = abs(trigPolynomialValue(polynomial, root));
    for (var step = 0; step < 2; step += 1)
    {
        const slope = trigPolynomialDerivative(polynomial, best);
        if (abs(slope) < 1e-300)
        {
            break;
        }
        const candidate = best - trigPolynomialValue(polynomial, best) / slope;
        const candidateValue = abs(trigPolynomialValue(polynomial, candidate));
        if (candidateValue >= bestValue)
        {
            break;
        }
        best = candidate;
        bestValue = candidateValue;
    }
    return best;
}

/**
 * Real roots of a quadratic, in the numerically stable form: compute the root whose formula does
 * not cancel, then get the other from the product of roots. The naive quadratic formula loses most
 * of its digits when `4 a c` is small against `b^2`, which is the common case here.
 */
function realQuadraticRoots(quadratic is number, linear is number, constant is number) returns array
{
    const scale = max(abs(quadratic), max(abs(linear), abs(constant)));
    if (scale == 0)
    {
        return [];
    }
    if (abs(quadratic) <= 1e-15 * scale)
    {
        return abs(linear) <= 1e-15 * scale ? [] : [-constant / linear];
    }
    const discriminant = linear * linear - 4 * quadratic * constant;
    if (discriminant < 0)
    {
        return [];
    }
    const rootOfDiscriminant = sqrt(discriminant);
    const stableNumerator = -0.5 * (linear + (linear >= 0 ? rootOfDiscriminant : -rootOfDiscriminant));
    if (stableNumerator == 0)
    {
        return [0];
    }
    const firstRoot = stableNumerator / quadratic;
    const secondRoot = constant / stableNumerator;
    return abs(firstRoot - secondRoot) <= 1e-14 * max(1, abs(firstRoot)) ?
        [firstRoot] : [min(firstRoot, secondRoot), max(firstRoot, secondRoot)];
}

/** The real cube root, which `^ (1/3)` will not give for a negative argument. */
function realCubeRoot(value is number) returns number
{
    return value < 0 ? -((-value) ^ (1 / 3)) : (value ^ (1 / 3));
}

/**
 * Every real root of a real cubic. Depress to `y^3 + p y + q`, then split on the discriminant:
 * one real root by Cardano when it is positive, three by VIETE'S TRIGONOMETRIC form when it is not.
 * The trigonometric branch matters - Cardano's formula for three real roots runs through complex
 * intermediates (the casus irreducibilis) and loses accuracy that the cosine form does not.
 */
function realCubicRoots(cubic is number, quadratic is number, linear is number,
    constant is number) returns array
{
    const scale = max(abs(cubic), max(abs(quadratic), max(abs(linear), abs(constant))));
    if (scale == 0)
    {
        return [];
    }
    if (abs(cubic) <= 1e-15 * scale)
    {
        return realQuadraticRoots(quadratic, linear, constant);
    }
    const shift = quadratic / (3 * cubic);
    const p = (3 * cubic * linear - quadratic * quadratic) / (3 * cubic * cubic);
    const q = (2 * quadratic * quadratic * quadratic - 9 * cubic * quadratic * linear +
            27 * cubic * cubic * constant) / (27 * cubic * cubic * cubic);
    const discriminant = 0.25 * q * q + p * p * p / 27;
    if (discriminant > 0)
    {
        const rootOfDiscriminant = sqrt(discriminant);
        return [realCubeRoot(-0.5 * q + rootOfDiscriminant) +
                    realCubeRoot(-0.5 * q - rootOfDiscriminant) - shift];
    }
    if (p == 0)
    {
        return [realCubeRoot(-q) - shift];
    }
    const radius = 2 * sqrt(-p / 3);
    const cosineArgument = min(1, max(-1, 3 * q / (p * radius)));
    const angle = acos(cosineArgument) / radian;
    var roots = makeArray(3, 0);
    for (var index = 0; index < 3; index += 1)
    {
        roots[index] = radius * cos((angle - 2 * PI * index) / 3 * radian) - shift;
    }
    return roots;
}

/**
 * Every real root of a real quartic, by resolvent cubic. Depress to `y^4 + p y^2 + q y + r`, factor
 * it as `(y^2 + s y + u)(y^2 - s y + v)` where `s^2` is a root of
 * `z^3 + 2p z^2 + (p^2 - 4r) z - q^2 = 0`, and solve the two quadratics. The biquadratic case
 * (`q == 0`) is split off because there `s` may legitimately be zero and the `q / s` below would not
 * exist.
 */
function realQuarticRoots(quartic is number, cubic is number, quadratic is number, linear is number,
    constant is number) returns array
{
    const scale = max(abs(quartic), max(abs(cubic), max(abs(quadratic),
                    max(abs(linear), abs(constant)))));
    if (scale == 0)
    {
        return [];
    }
    if (abs(quartic) <= 1e-15 * scale)
    {
        return realCubicRoots(cubic, quadratic, linear, constant);
    }
    // Normalize first: every tolerance below is then relative to 1.
    const c3 = cubic / quartic;
    const c2 = quadratic / quartic;
    const c1 = linear / quartic;
    const c0 = constant / quartic;
    const shift = 0.25 * c3;
    const p = c2 - 6 * shift * shift;
    const q = c1 - 2 * c2 * shift + 8 * shift * shift * shift;
    const r = c0 - c1 * shift + c2 * shift * shift - 3 * shift * shift * shift * shift;

    var roots = [];
    if (abs(q) <= 1e-14 * max(1, max(abs(p), abs(r))))
    {
        // Biquadratic: y^4 + p y^2 + r, so y^2 solves a quadratic.
        for (var square in realQuadraticRoots(1, p, r))
        {
            if (square >= 0)
            {
                const y = sqrt(square);
                roots = append(roots, y - shift);
                if (y > 0)
                {
                    roots = append(roots, -y - shift);
                }
            }
        }
        return roots;
    }
    var bestSquare = undefined;
    for (var candidate in realCubicRoots(1, 2 * p, p * p - 4 * r, -q * q))
    {
        if (candidate > 0 && (bestSquare == undefined || candidate > bestSquare))
        {
            bestSquare = candidate;
        }
    }
    if (bestSquare == undefined)
    {
        return [];
    }
    const s = sqrt(bestSquare);
    const u = 0.5 * (p + bestSquare - q / s);
    const v = 0.5 * (p + bestSquare + q / s);
    for (var y in realQuadraticRoots(1, s, u))
    {
        roots = append(roots, y - shift);
    }
    for (var y in realQuadraticRoots(1, -s, v))
    {
        roots = append(roots, y - shift);
    }
    return roots;
}

/**
 * Every root of a degree-2 real trigonometric polynomial, IN CLOSED FORM (2026-08-24).
 *
 * Why this exists: the scan-and-polish route below cost 82% of the analytic contact solve —
 * ~8,000 interpreted `trigPolynomialValue` calls on the section-9.4 fixture, ~26 per profile
 * parameter — searching for roots that are available exactly. Every analytic class in this module
 * produces degree at most 2, so this is the path they all take.
 *
 * The tangent half-angle `t = tan(theta / 2)` turns `a0 + a1 cos + b1 sin + a2 cos 2 + b2 sin 2`
 * into a quartic in `t` after clearing `(1 + t^2)^2`:
 *
 *   t^4 : a0 - a1 + a2      t^3 : 2 b1 - 4 b2      t^2 : 2 a0 - 6 a2
 *   t^1 : 2 b1 + 4 b2       t^0 : a0 + a1 + a2
 *
 * Two of those coefficients are worth reading as identities rather than algebra: the constant term
 * is `P(0)` and the leading term is `P(pi)`. So a vanishing leading coefficient is not a
 * degeneracy to guard against but exactly the statement that `theta = pi` is a root — the value the
 * substitution sends to infinity — and it is added back explicitly.
 *
 * `nearTangency` is decided by the DERIVATIVE at each root, which is what a double root actually
 * is - `P(theta) = P'(theta) = 0` - and it replaces two heuristics that were both wrong. The scan
 * below reports a tangency when a sample grazes zero without a sign change, which fires on simple
 * roots it happens to sample near (measured: `cos 2x` and a pure first harmonic, both of which have
 * only simple roots). And root-proximity detection, which this function used first, MISSES the case
 * that matters: `-1 + cos theta` reduces to `-2 t^2 (t^2 + 1)`, whose coincident roots a stable
 * quadratic solver returns once, so there is no second root to be close to. Under-reporting a
 * degeneracy that spec 6.4 turns into a rejection is the dangerous direction, so the test is on
 * `P'` and not on the root list.
 */
export function solveDegreeTwoTrigRootsClosedForm(polynomial is map, start is number,
    options is map) returns map
{
    const cosine = polynomial.cosine;
    const sine = polynomial.sine;
    const a0 = cosine[0];
    const a1 = size(cosine) > 1 ? cosine[1] : 0;
    const a2 = size(cosine) > 2 ? cosine[2] : 0;
    const b1 = size(sine) > 1 ? sine[1] : 0;
    const b2 = size(sine) > 2 ? sine[2] : 0;

    const leading = a0 - a1 + a2;
    const bound = max(1e-300, trigPolynomialBound(polynomial));
    const tangencyTolerance = options.tangencyTolerance == undefined ? 1e-9 * bound :
        options.tangencyTolerance;
    // The derivative's amplitude bound: sum of k * harmonic amplitude, the same shape of bound
    // trigPolynomialBound gives for the value.
    var derivativeAmplitude = 0;
    for (var harmonic = 1; harmonic < size(cosine); harmonic += 1)
    {
        derivativeAmplitude += harmonic *
            sqrt(cosine[harmonic] ^ 2 + (harmonic < size(sine) ? sine[harmonic] ^ 2 : 0));
    }
    const derivativeBound = max(1e-300, derivativeAmplitude);

    var angles = [];
    for (var t in realQuarticRoots(leading, 2 * b1 - 4 * b2, 2 * a0 - 6 * a2, 2 * b1 + 4 * b2,
                a0 + a1 + a2))
    {
        angles = append(angles, 2 * atan(t) / radian);
    }
    // The substitution cannot represent theta = pi; a vanishing leading coefficient IS that root.
    if (abs(leading) <= 1e-13 * bound)
    {
        angles = append(angles, PI);
    }

    // Shift into [start, start + 2 pi), sort, and drop duplicates - a double root arrives twice and
    // is reported once, with nearTangency set.
    var shifted = makeArray(size(angles), 0);
    for (var index = 0; index < size(angles); index += 1)
    {
        shifted[index] = start + positiveModulo(angles[index] - start, 2 * PI);
    }
    var roots = makeArray(size(shifted), 0);
    var rootCount = 0;
    var nearTangency = false;
    var worstResidual = 0;
    for (var pass = 0; pass < size(shifted); pass += 1)
    {
        var smallest = undefined;
        for (var candidate in shifted)
        {
            if (candidate > -1e300 && (smallest == undefined || candidate < smallest))
            {
                smallest = candidate;
            }
        }
        if (smallest == undefined)
        {
            break;
        }
        for (var index = 0; index < size(shifted); index += 1)
        {
            if (shifted[index] == smallest)
            {
                shifted[index] = -1e301;
            }
        }
        if (rootCount > 0 && abs(smallest - roots[rootCount - 1]) <= 1e-9)
        {
            nearTangency = true;
            continue;
        }
        // POLISH. The quartic isolates the roots exactly; it does not deliver them to machine
        // precision, because its coefficients are sums and differences of the trig ones and the
        // resolvent cubic and its square roots each shed digits. Measured: root residuals ~2e-15,
        // which showed up as a sphere great-circle latitude error of 1.7e-12 against the 4.4e-16
        // this module used to report. Two guarded Newton steps on the ORIGINAL polynomial put it
        // back - isolation and refinement are different jobs, and the closed form only replaces the
        // first. This is two evaluations per root, not the sixteen-sample scan it replaced.
        const polished = polishTrigRoot(polynomial, smallest);
        roots[rootCount] = polished;
        rootCount += 1;
        worstResidual = max(worstResidual, abs(trigPolynomialValue(polynomial, polished)));
        // A double root is a root where P' also vanishes. Scaled by the derivative's own amplitude
        // bound so the test means the same thing on a millimetre polynomial and a metre one.
        if (abs(trigPolynomialDerivative(polynomial, smallest)) <= 1e-7 * derivativeBound)
        {
            nearTangency = true;
        }
    }
    if (worstResidual > tangencyTolerance && rootCount > 0)
    {
        // A root the quartic reports but the polynomial does not confirm means the coefficients are
        // ill-conditioned; say so rather than hand back a number the caller cannot check.
        nearTangency = true;
    }
    return {
            "identicallyZero" : false,
            "roots" : subArray(roots, 0, rootCount),
            "nearTangency" : nearTangency,
            "worstResidual" : worstResidual
        };
}

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
    // Degree 2 and below is EVERY analytic class in this module, and its roots are available
    // exactly (solveDegreeTwoTrigRootsClosedForm). The scan below stays for higher degree and as
    // the oracle the closed form is validated against - pass `closedForm : false` to force it.
    if (degree <= 2 && options.closedForm != false)
    {
        return solveDegreeTwoTrigRootsClosedForm(polynomial, start, options);
    }
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
 *   REVOLVED: no ruling - one degree-2 trigonometric polynomial in theta per profile parameter
 *   EXTRUDED: f = A(u) + v B(u), both built from the cross section's own derivatives
 *
 * Returns a map carrying `kind`, `slidesEverywhere` (spec 6.4's exact verdict), `bound` (the
 * free |f| screen over the whole face), and the class-specific coefficient bundle named above.
 * The two profile-driven classes have no station-wide bundle - their coefficients depend on the
 * profile parameter, so they are rebuilt per u by analyticRevolvedThetaPolynomial and
 * analyticExtrudedRulingTerms - and their sliding verdict is sampled along the profile.
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
    if (frame.kind == AnalyticSurfaceKind.REVOLVED)
    {
        // The theta polynomial is rebuilt per profile parameter by
        // analyticRevolvedThetaPolynomial, so there is no station-wide coefficient bundle to
        // report - only the sliding verdict, and that one is sampled along the profile the same
        // way sphere and torus sample along theta.
        const profileSamples = options.profileSamples == undefined ? 8 : options.profileSamples;
        const parameters = analyticProfileParameters(frame, profileSamples);
        var slides = true;
        for (var u in parameters)
        {
            if (!trigPolynomialIsZero(analyticRevolvedThetaPolynomial(frame, pullback, u),
                    coefficientTolerance))
            {
                slides = false;
                break;
            }
        }
        return {
                "kind" : frame.kind,
                "slidesEverywhere" : slides,
                "profileSamples" : size(parameters),
                "bound" : undefined
            };
    }
    if (frame.kind == AnalyticSurfaceKind.EXTRUDED)
    {
        // f = A(u) + v B(u); the face slides exactly when both coefficients vanish identically.
        const profileSamples = options.profileSamples == undefined ? 8 : options.profileSamples;
        const parameters = analyticProfileParameters(frame, profileSamples);
        var slides = true;
        for (var u in parameters)
        {
            const terms = analyticExtrudedRulingTerms(frame, pullback, u);
            if (abs(terms.constant) > coefficientTolerance || abs(terms.ruling) > coefficientTolerance)
            {
                slides = false;
                break;
            }
        }
        return {
                "kind" : frame.kind,
                "slidesEverywhere" : slides,
                "profileSamples" : size(parameters),
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

/**
 * The degree-2 trigonometric polynomial in theta that f becomes along one parallel of a REVOLVED
 * face, at profile parameter u (spec 6.5.1). This is the sphere/torus form generalized: the
 * coefficients come from r, z, r', z' at one u instead of from a formula, and everything
 * downstream - roots, bound, identically-zero test, the nearTangency report - is the existing
 * trig machinery unchanged.
 *
 * With N = (-z' c, -z' s, r') and S = (r c, r s, z) the collection is
 *
 *   constant : r' (w33 z + g3) - (r z' / 2) (w11 + w22)
 *   cos      : -z' (w13 z + g1) + r' r w31
 *   sin      : -z' (w23 z + g2) + r' r w32
 *   cos 2    : -(r z' / 2) (w11 - w22)
 *   sin 2    : -(r z' / 2) (w12 + w21)
 *
 * in one-based w indices; the code below reads pullback.wLocal zero-based.
 */
export function analyticRevolvedThetaPolynomial(frame is map, pullback is map, u is number) returns map
{
    return analyticRevolvedThetaPolynomial(frame, pullback, leanCurveDerivatives(frame.profile, u, 1));
}

/**
 * Same collection, on generator derivatives the caller already holds.
 *
 * r, z, r' and z' depend on the FRAME alone - not on the station, not on the pullback - so a fit
 * that solves the same generator grid at every station is evaluating one identical de Boor per
 * station per parameter. `analyticGeneratorDerivativeTable` computes that grid once and this
 * overload consumes it; the `u` form above stays the one to call for a parameter that is not on
 * the grid, which is every point solved between two samples.
 */
export function analyticRevolvedThetaPolynomial(frame is map, pullback is map,
    derivatives is array) returns map
{
    const w = pullback.wLocal;
    const g = pullback.gLocal;
    const radius = derivatives[0];
    const height = derivatives[2];
    const radiusSlope = derivatives[3];
    const heightSlope = derivatives[5];
    const quadratic = 0.5 * radius * heightSlope;
    return trigPolynomial(
            [radiusSlope * (w[2][2] * height + g[2]) - quadratic * (w[0][0] + w[1][1]),
                -heightSlope * (w[0][2] * height + g[0]) + radiusSlope * radius * w[2][0],
                -quadratic * (w[0][0] - w[1][1])],
            [0,
                -heightSlope * (w[1][2] * height + g[1]) + radiusSlope * radius * w[2][1],
                -quadratic * (w[0][1] + w[1][0])]);
}

/**
 * The two coefficients of f = A(u) + v B(u) for an EXTRUDED face at profile parameter u, plus
 * the derivative of B (spec 6.5.1). f is LINEAR in the ruling parameter because the normal
 * C' x e3 does not depend on v, so the contact curve is the explicit graph v = -A(u) / B(u) -
 * the cylinder and cone form with a profile curve's own coefficients in place of cos u and sin u.
 *
 * Returns { constant : A(u), ruling : B(u), rulingSlope : dB/du }. The slope is exact (it needs
 * only C''), which is what lets the singular-u search polish with Newton rather than bisect alone.
 */
export function analyticExtrudedRulingTerms(frame is map, pullback is map, u is number) returns map
{
    const w = pullback.wLocal;
    const g = pullback.gLocal;
    const derivatives = leanCurveDerivatives(frame.profile, u, 2);
    // N = C' x e3 = (C'_2, -C'_1, 0), and W e3 is the third column of W.
    const transported = applyLocalMatrix(w,
            vector(derivatives[0], derivatives[1], derivatives[2])) + g;
    return {
            "constant" : derivatives[4] * transported[0] - derivatives[3] * transported[1],
            "ruling" : derivatives[4] * w[0][2] - derivatives[3] * w[1][2],
            "rulingSlope" : derivatives[7] * w[0][2] - derivatives[6] * w[1][2]
        };
}

/** The number of distinct knot spans inside a profile-driven frame's own parameter domain. */
function profileSpanCount(frame is map) returns number
{
    const knots = frame.profile.knots;
    var spans = 0;
    for (var index = 1; index < size(knots); index += 1)
    {
        if (knots[index] > knots[index - 1] + 1e-12 && knots[index] > frame.profileStart - 1e-12 &&
            knots[index - 1] < frame.profileEnd + 1e-12)
        {
            spans += 1;
        }
    }
    return max(1, spans);
}

/**
 * Every profile parameter where an EXTRUDED face's ruling coefficient B vanishes. There the
 * whole ruling either lies in the contact set (A also zero) or misses it entirely, so it must be
 * REPORTED rather than sampled through - the same treatment the cylinder and cone give their
 * singular thetas, and the same reason: the graph v = -A/B has a vertical asymptote and no value.
 *
 * Root isolation is polynomial rather than trigonometric here, so the grid is per knot SPAN
 * rather than per harmonic: B restricted to one span is a polynomial of degree at most
 * degree - 1 (rational profiles included, since a strictly positive denominator cannot add a
 * root), and 8 samples per span per degree leaves several intervals per root. A merged pair is a
 * tangency, reported through `nearTangency` exactly as the trig solver reports one.
 *
 * Returns { identicallyZero, roots {array, ascending}, nearTangency, worstResidual }.
 */
export function solveExtrudedRulingSingularities(frame is map, pullback is map, options is map) returns map
{
    return solveExtrudedTermRoots(frame, pullback, options, false);
}

/**
 * Every profile parameter where an EXTRUDED face's OFFSET coefficient A vanishes. When B is
 * identically zero - which is exactly what a pure translation does to an extruded face, since
 * then W is zero and B is `N, W e3` - f does not depend on the ruling parameter at all, and the
 * contact set is the set of WHOLE rulings at these parameters. It is the extruded twin of the
 * cylinder's two contact rulings under a crossing translation, and it is the common case on real
 * parts: an extruded profile swept along a line.
 */
export function solveExtrudedOffsetRoots(frame is map, pullback is map, options is map) returns map
{
    return solveExtrudedTermRoots(frame, pullback, options, true);
}

/** The shared scan-bracket-Newton over one of the two ruling coefficients. */
function solveExtrudedTermRoots(frame is map, pullback is map, options is map,
    useOffset is boolean) returns map
{
    const tolerance = options.tolerance == undefined ? 1e-14 : options.tolerance;
    const coefficientTolerance = options.coefficientTolerance == undefined ? 1e-15 :
        options.coefficientTolerance;
    const sampleCount = max(24, 8 * max(1, frame.profile.degree) * profileSpanCount(frame));
    const step = (frame.profileEnd - frame.profileStart) / sampleCount;

    var values = makeArray(sampleCount + 1, 0);
    var scale = 0;
    for (var index = 0; index <= sampleCount; index += 1)
    {
        const terms = analyticExtrudedRulingTerms(frame, pullback, frame.profileStart + step * index);
        values[index] = useOffset ? terms.constant : terms.ruling;
        scale = max(scale, abs(values[index]));
    }
    if (scale <= coefficientTolerance)
    {
        return { "identicallyZero" : true, "roots" : [], "nearTangency" : false, "worstResidual" : 0 };
    }
    const tangencyTolerance = options.tangencyTolerance == undefined ? 1e-9 * scale :
        options.tangencyTolerance;

    var roots = makeArray(sampleCount + 2, 0);
    var rootCount = 0;
    var nearTangency = false;
    var worstResidual = 0;
    for (var index = 1; index <= sampleCount; index += 1)
    {
        const low = frame.profileStart + step * (index - 1);
        const high = frame.profileStart + step * index;
        if (values[index - 1] == 0)
        {
            roots[rootCount] = low;
            rootCount += 1;
        }
        else if (values[index - 1] * values[index] < 0)
        {
            const refined = refineExtrudedRulingRoot(frame, pullback, low, high, values[index - 1],
                    tolerance * scale, useOffset);
            roots[rootCount] = refined.x;
            rootCount += 1;
            worstResidual = max(worstResidual, abs(refined.value));
        }
        else if (abs(values[index]) <= tangencyTolerance || abs(values[index - 1]) <= tangencyTolerance)
        {
            nearTangency = true;
        }
    }
    if (values[sampleCount] == 0)
    {
        roots[rootCount] = frame.profileEnd;
        rootCount += 1;
    }
    return {
            "identicallyZero" : false,
            "roots" : subArray(roots, 0, rootCount),
            "nearTangency" : nearTangency,
            "worstResidual" : worstResidual
        };
}

/**
 * Bisection-safeguarded Newton on one ruling coefficient inside a bracket, mirroring
 * refineTrigRoot exactly. B carries an exact slope; A does not - its derivative needs the
 * generator's third derivative, which nothing else in this module wants - so A is bisected. The
 * bracket makes that a correctness question of iteration count only, and 60 halvings of one knot
 * span reach the double-precision floor with room to spare.
 */
function refineExtrudedRulingRoot(frame is map, pullback is map, low is number, high is number,
    lowValue is number, tolerance is number, useOffset is boolean) returns map
{
    var bracketLow = low;
    var bracketHigh = high;
    var bracketLowValue = lowValue;
    var x = 0.5 * (low + high);
    var terms = analyticExtrudedRulingTerms(frame, pullback, x);
    var value = useOffset ? terms.constant : terms.ruling;
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
        const slope = useOffset ? 0 : terms.rulingSlope;
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
        terms = analyticExtrudedRulingTerms(frame, pullback, x);
        value = useOffset ? terms.constant : terms.ruling;
    }
    return { "x" : x, "value" : value };
}

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
 * REVOLVED: per-profile-parameter theta roots. EXTRUDED: the same explicit graph as the cylinder
 * and cone, over the profile parameter instead of theta - or, when B vanishes identically as it
 * does under any pure translation, WHOLE RULINGS at the roots of A, reported as form
 * "profileRulings" with the ruling parameter set to 0. The singular u are reported in
 * `singularProfileParameters`.
 *
 * options: { sampleCount (default 64), thetaStart (default 0), thetaSpan (default 2 pi),
 * tolerance, coefficientTolerance, profileSamples }. The two profile-driven classes ignore
 * thetaSpan: they sample their generator's own domain, and their theta roots come from the full
 * period the trig solver always covers.
 *
 * Returns { form, slidesEverywhere, samples {array of [u, v]}, singularThetas {array},
 * singularProfileParameters {array}, nearTangency {boolean} }.
 */
export function solveAnalyticContactCurve(frame is map, pullback is map, options is map) returns map
{
    const structure = analyticContactStructure(frame, pullback, options);
    if (structure.slidesEverywhere)
    {
        return { "form" : "sliding", "slidesEverywhere" : true, "samples" : [],
                "singularThetas" : [], "singularProfileParameters" : [], "nearTangency" : false };
    }
    if (frame.kind == AnalyticSurfaceKind.PLANE)
    {
        return { "form" : "line", "slidesEverywhere" : false,
                "constant" : structure.constant,
                "uCoefficient" : structure.uCoefficient,
                "vCoefficient" : structure.vCoefficient,
                "samples" : [], "singularThetas" : [], "singularProfileParameters" : [],
                "nearTangency" : false };
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
                "singularThetas" : singular.roots, "singularProfileParameters" : [],
                "nearTangency" : singular.nearTangency };
    }

    if (frame.kind == AnalyticSurfaceKind.REVOLVED)
    {
        // One degree-2 theta polynomial per profile parameter, up to four roots each - the
        // sphere/torus branch with the profile in place of the formula.
        const parameters = analyticProfileParameters(frame, sampleCount + 1);
        var samples = makeArray(4 * size(parameters), [0, 0]);
        var sampleTotal = 0;
        var nearTangency = false;
        for (var u in parameters)
        {
            const solved = solveTrigPolynomialRoots(
                    analyticRevolvedThetaPolynomial(frame, pullback, u), thetaStart, options);
            nearTangency = nearTangency || solved.nearTangency;
            for (var root in solved.roots)
            {
                if (sampleTotal < size(samples))
                {
                    samples[sampleTotal] = [u, root];
                    sampleTotal += 1;
                }
            }
        }
        return { "form" : "profileThetaRoots", "slidesEverywhere" : false,
                "samples" : subArray(samples, 0, sampleTotal),
                "singularThetas" : [], "singularProfileParameters" : [],
                "nearTangency" : nearTangency };
    }
    if (frame.kind == AnalyticSurfaceKind.EXTRUDED)
    {
        const singular = solveExtrudedRulingSingularities(frame, pullback, options);
        if (singular.identicallyZero)
        {
            // B vanishes identically, so f does not depend on the ruling parameter and the graph
            // has no values anywhere. The contact set is WHOLE RULINGS at the roots of A - the
            // answer a pure translation always produces, and the one an extruded part swept along
            // a line needs. Each sample carries ruling parameter 0; the whole ruling is in.
            const rulings = solveExtrudedOffsetRoots(frame, pullback, options);
            var rulingSamples = makeArray(size(rulings.roots), [0, 0]);
            for (var index = 0; index < size(rulings.roots); index += 1)
            {
                rulingSamples[index] = [rulings.roots[index], 0];
            }
            return { "form" : "profileRulings", "slidesEverywhere" : false,
                    "samples" : rulingSamples, "singularThetas" : [],
                    "singularProfileParameters" : [], "nearTangency" : rulings.nearTangency };
        }
        // The explicit graph v = -A(u) / B(u), with the u where B vanishes reported rather than
        // sampled through. The cut is scaled by B's own sampled magnitude, so it means the same
        // thing on a millimetre profile and a metre one.
        const parameters = analyticProfileParameters(frame, sampleCount + 1);
        var terms = makeArray(size(parameters));
        var rulingScale = 0;
        for (var index = 0; index < size(parameters); index += 1)
        {
            terms[index] = analyticExtrudedRulingTerms(frame, pullback, parameters[index]);
            rulingScale = max(rulingScale, abs(terms[index].ruling));
        }
        const singularCut = max(coefficientTolerance, 1e-12 * rulingScale);
        var samples = makeArray(size(parameters), [0, 0]);
        var sampleTotal = 0;
        for (var index = 0; index < size(parameters); index += 1)
        {
            if (abs(terms[index].ruling) <= singularCut)
            {
                continue;
            }
            samples[sampleTotal] = [parameters[index], -terms[index].constant / terms[index].ruling];
            sampleTotal += 1;
        }
        return { "form" : "profileRuledGraph", "slidesEverywhere" : false,
                "samples" : subArray(samples, 0, sampleTotal),
                "singularThetas" : [], "singularProfileParameters" : singular.roots,
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
            "singularThetas" : [], "singularProfileParameters" : [], "nearTangency" : nearTangency };
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
    if (frame.kind == AnalyticSurfaceKind.REVOLVED || frame.kind == AnalyticSurfaceKind.EXTRUDED)
    {
        // Rigorous in the second parameter, sampled in the profile parameter - the same shape of
        // guarantee sphere and torus already give, and for the same reason: the coefficients are
        // a curve's values rather than a formula's. Remember these bound the UNNORMALIZED f of
        // spec 6.5.1, which is |S_u x S_v| times the unit-normal one.
        const profileSamples = options.profileSamples == undefined ? 16 : options.profileSamples;
        const parameters = analyticProfileParameters(frame, profileSamples);
        const rulingReach = max(abs(domain.vMin), abs(domain.vMax));
        var worstProfile = 0;
        for (var u in parameters)
        {
            if (frame.kind == AnalyticSurfaceKind.REVOLVED)
            {
                worstProfile = max(worstProfile,
                    trigPolynomialBound(analyticRevolvedThetaPolynomial(frame, pullback, u)));
            }
            else
            {
                const terms = analyticExtrudedRulingTerms(frame, pullback, u);
                worstProfile = max(worstProfile, abs(terms.constant) + abs(terms.ruling) * rulingReach);
            }
        }
        return { "minimumMagnitude" : 0, "maximumMagnitude" : worstProfile };
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


// ============================= Funnel solver, masks and certified census (spec 6.3-6.4, 6.7-6.8) =============================

/**
 * Audit every (patch x t-span) block of one face for the sliding degeneracy: |f| within
 * `valueTolerance` over the WHOLE block, meaning the face slides along itself there (plane
 * parallel to translation, cylinder along its own axis). Blocks that survive screening are
 * materialized once; their coefficient tensors are kept on the live records so downstream
 * stages (census, island refinement) never pay for materialization twice.
 *
 * Returns {
 *     slides {boolean} : true when any block slid,
 *     slidingBlocks {array} : { uSegment, vSegment, spanIndex, tStart, tEnd },
 *     liveBlocks {array} : { uSegment, vSegment, spanIndex, blockGrids } - can vanish, does
 *         not slide,
 *     deadBlockCount {number} : blocks certified sign-definite
 * }
 */
export function auditEnvelopeSliding(patchFactors is map, spans is array, valueTolerance is number) returns map
{
    var slidingBlocks = [];
    var liveBlocks = [];
    var deadBlockCount = 0;
    for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
    {
        for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
        {
            const patch = patchFactors.patches[uSegment][vSegment];
            var products = undefined;
            for (var spanIndex = 0; spanIndex < size(spans); spanIndex += 1)
            {
                const screen = screenEnvelopeBlock(patch, spans[spanIndex], valueTolerance);
                if (!screen.canVanish)
                {
                    deadBlockCount += 1;
                    continue;
                }
                if (products == undefined)
                {
                    products = buildEnvelopePatchProducts(patch);
                }
                const blockGrids = materializeEnvelopeBlock(products, spans[spanIndex]);
                var blockMinimum = undefined;
                var blockMaximum = undefined;
                for (var grid in blockGrids)
                {
                    const range = bernsteinGridRange(grid);
                    blockMinimum = blockMinimum == undefined ? range.minimum : min(blockMinimum, range.minimum);
                    blockMaximum = blockMaximum == undefined ? range.maximum : max(blockMaximum, range.maximum);
                }
                if (blockMinimum >= -valueTolerance && blockMaximum <= valueTolerance)
                {
                    slidingBlocks = append(slidingBlocks, {
                                "uSegment" : uSegment, "vSegment" : vSegment, "spanIndex" : spanIndex,
                                "tStart" : spans[spanIndex].tStart, "tEnd" : spans[spanIndex].tEnd
                            });
                }
                else if (blockMinimum > valueTolerance || blockMaximum < -valueTolerance)
                {
                    deadBlockCount += 1;
                }
                else
                {
                    liveBlocks = append(liveBlocks, {
                                "uSegment" : uSegment, "vSegment" : vSegment, "spanIndex" : spanIndex,
                                "blockGrids" : blockGrids
                            });
                }
            }
        }
    }
    return {
            "slides" : size(slidingBlocks) > 0,
            "slidingBlocks" : slidingBlocks,
            "liveBlocks" : liveBlocks,
            "deadBlockCount" : deadBlockCount
        };
}

/**
 * Isolate the possible grazing set of one (patch x t-span) block by FACTORED subdivision:
 * each split halves the cell in u, v, or t by subdividing the S and N coefficient grids
 * (native split-matrix products) or the twelve t-polynomials, then re-screens the child with
 * range products alone. A dead verdict is a certificate; f is never materialized during the
 * descent, matching the spec 6.0 doctrine that most live-block work never materializes f.
 *
 * options: {
 *     minCellWidthU, minCellWidthV, minCellWidthT {number} : leaf sizes as LOCAL fractions of
 *         the block (defaults 1/8),
 *     maxSplitDepth {number} : safety cap (default 24),
 *     valueTolerance {number} : screening tolerance (default 0)
 * }
 *
 * Returns {
 *     liveCells {array} : { uStart..tEnd local fractions, globalUStart..globalTEnd },
 *     screenedCount, deadCount {number}
 * }
 */
export function isolateEnvelopeCells(patchFactor is map, spanPolynomials is map, options is map) returns map
{
    const filledOptions = mergeMaps({
                "minCellWidthU" : 1 / 8, "minCellWidthV" : 1 / 8, "minCellWidthT" : 1 / 8,
                "maxSplitDepth" : 24, "valueTolerance" : 0
            }, options);
    const bounds = { "u0" : 0, "u1" : 1, "v0" : 0, "v1" : 1, "t0" : 0, "t1" : 1 };
    const collected = collectLiveEnvelopeCells(patchFactor, spanPolynomials, bounds, filledOptions, filledOptions.maxSplitDepth);
    var liveCells = makeArray(size(collected.cells));
    for (var cellIndex = 0; cellIndex < size(collected.cells); cellIndex += 1)
    {
        var cell = collected.cells[cellIndex];
        cell.globalUStart = patchFactor.uStart + (patchFactor.uEnd - patchFactor.uStart) * cell.uStart;
        cell.globalUEnd = patchFactor.uStart + (patchFactor.uEnd - patchFactor.uStart) * cell.uEnd;
        cell.globalVStart = patchFactor.vStart + (patchFactor.vEnd - patchFactor.vStart) * cell.vStart;
        cell.globalVEnd = patchFactor.vStart + (patchFactor.vEnd - patchFactor.vStart) * cell.vEnd;
        cell.globalTStart = spanPolynomials.tStart + (spanPolynomials.tEnd - spanPolynomials.tStart) * cell.tStart;
        cell.globalTEnd = spanPolynomials.tStart + (spanPolynomials.tEnd - spanPolynomials.tStart) * cell.tEnd;
        liveCells[cellIndex] = cell;
    }
    return { "liveCells" : liveCells, "screenedCount" : collected.screened, "deadCount" : collected.dead };
}

/** Recursive descent for isolateEnvelopeCells; bounds are local fractions of the root block. */
function collectLiveEnvelopeCells(cellFactor is map, cellSpan is map, bounds is map, options is map,
    depthRemaining is number) returns map
{
    const screen = screenEnvelopeBlock(cellFactor, cellSpan, options.valueTolerance);
    if (!screen.canVanish)
    {
        return { "cells" : [], "screened" : 1, "dead" : 1 };
    }
    const uWidth = bounds.u1 - bounds.u0;
    const vWidth = bounds.v1 - bounds.v0;
    const tWidth = bounds.t1 - bounds.t0;
    const uPressure = uWidth / options.minCellWidthU;
    const vPressure = vWidth / options.minCellWidthV;
    const tPressure = tWidth / options.minCellWidthT;
    if (depthRemaining <= 0 || (uPressure <= 1.0000001 && vPressure <= 1.0000001 && tPressure <= 1.0000001))
    {
        return {
                "cells" : [{
                            "uStart" : bounds.u0, "uEnd" : bounds.u1,
                            "vStart" : bounds.v0, "vEnd" : bounds.v1,
                            "tStart" : bounds.t0, "tEnd" : bounds.t1,
                            "looseMin" : screen.looseMin, "looseMax" : screen.looseMax
                        }],
                "screened" : 1, "dead" : 0
            };
    }

    var lowFactor = cellFactor;
    var highFactor = cellFactor;
    var lowSpan = cellSpan;
    var highSpan = cellSpan;
    var lowBounds = bounds;
    var highBounds = bounds;
    if (uPressure >= vPressure && uPressure >= tPressure)
    {
        const split = splitCellFactor(cellFactor, true);
        lowFactor = split.low;
        highFactor = split.high;
        const midpoint = 0.5 * (bounds.u0 + bounds.u1);
        lowBounds.u1 = midpoint;
        highBounds.u0 = midpoint;
    }
    else if (vPressure >= tPressure)
    {
        const split = splitCellFactor(cellFactor, false);
        lowFactor = split.low;
        highFactor = split.high;
        const midpoint = 0.5 * (bounds.v0 + bounds.v1);
        lowBounds.v1 = midpoint;
        highBounds.v0 = midpoint;
    }
    else
    {
        const split = splitCellSpan(cellSpan);
        lowSpan = split.low;
        highSpan = split.high;
        const midpoint = 0.5 * (bounds.t0 + bounds.t1);
        lowBounds.t1 = midpoint;
        highBounds.t0 = midpoint;
    }
    const lowResult = collectLiveEnvelopeCells(lowFactor, lowSpan, lowBounds, options, depthRemaining - 1);
    const highResult = collectLiveEnvelopeCells(highFactor, highSpan, highBounds, options, depthRemaining - 1);
    return {
            "cells" : concatenateArrays([lowResult.cells, highResult.cells]),
            "screened" : 1 + lowResult.screened + highResult.screened,
            "dead" : lowResult.dead + highResult.dead
        };
}

/** Split a cell's S and N grids at the local midpoint of u (splitU true) or v. */
function splitCellFactor(cellFactor is map, splitU is boolean) returns map
{
    var lowSurfaceGrids = makeArray(3);
    var highSurfaceGrids = makeArray(3);
    var lowNormalGrids = makeArray(3);
    var highNormalGrids = makeArray(3);
    var lowSurfaceRanges = makeArray(3);
    var highSurfaceRanges = makeArray(3);
    var lowNormalRanges = makeArray(3);
    var highNormalRanges = makeArray(3);
    for (var component = 0; component < 3; component += 1)
    {
        const surfaceSplit = splitU ? subdivideBernsteinGridU(cellFactor.surfaceGrids[component], 0.5) :
            subdivideBernsteinGridV(cellFactor.surfaceGrids[component], 0.5);
        const normalSplit = splitU ? subdivideBernsteinGridU(cellFactor.normalGrids[component], 0.5) :
            subdivideBernsteinGridV(cellFactor.normalGrids[component], 0.5);
        lowSurfaceGrids[component] = surfaceSplit.low;
        highSurfaceGrids[component] = surfaceSplit.high;
        lowNormalGrids[component] = normalSplit.low;
        highNormalGrids[component] = normalSplit.high;
        lowSurfaceRanges[component] = bernsteinGridRange(surfaceSplit.low);
        highSurfaceRanges[component] = bernsteinGridRange(surfaceSplit.high);
        lowNormalRanges[component] = bernsteinGridRange(normalSplit.low);
        highNormalRanges[component] = bernsteinGridRange(normalSplit.high);
    }
    var low = cellFactor;
    low.surfaceGrids = lowSurfaceGrids;
    low.normalGrids = lowNormalGrids;
    low.surfaceRanges = lowSurfaceRanges;
    low.normalRanges = lowNormalRanges;
    var high = cellFactor;
    high.surfaceGrids = highSurfaceGrids;
    high.normalGrids = highNormalGrids;
    high.surfaceRanges = highSurfaceRanges;
    high.normalRanges = highNormalRanges;
    return { "low" : low, "high" : high };
}

/** Split a cell's twelve t-polynomials at the local midpoint of t. */
function splitCellSpan(cellSpan is map) returns map
{
    var lowVelocityDots = makeArray(3);
    var highVelocityDots = makeArray(3);
    var lowVelocityRanges = makeArray(3);
    var highVelocityRanges = makeArray(3);
    var lowTranslationDots = makeArray(3);
    var highTranslationDots = makeArray(3);
    var lowTranslationRanges = makeArray(3);
    var highTranslationRanges = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var lowRow = makeArray(3);
        var highRow = makeArray(3);
        var lowRangeRow = makeArray(3);
        var highRangeRow = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            const split = subdivideBernstein(cellSpan.velocityDots[i][j], 0.5);
            lowRow[j] = split.left;
            highRow[j] = split.right;
            lowRangeRow[j] = bernsteinRange(split.left);
            highRangeRow[j] = bernsteinRange(split.right);
        }
        lowVelocityDots[i] = lowRow;
        highVelocityDots[i] = highRow;
        lowVelocityRanges[i] = lowRangeRow;
        highVelocityRanges[i] = highRangeRow;
        const translationSplit = subdivideBernstein(cellSpan.translationDots[i], 0.5);
        lowTranslationDots[i] = translationSplit.left;
        highTranslationDots[i] = translationSplit.right;
        lowTranslationRanges[i] = bernsteinRange(translationSplit.left);
        highTranslationRanges[i] = bernsteinRange(translationSplit.right);
    }
    var low = cellSpan;
    low.velocityDots = lowVelocityDots;
    low.velocityDotRanges = lowVelocityRanges;
    low.translationDots = lowTranslationDots;
    low.translationDotRanges = lowTranslationRanges;
    var high = cellSpan;
    high.velocityDots = highVelocityDots;
    high.velocityDotRanges = highVelocityRanges;
    high.translationDots = highTranslationDots;
    high.translationDotRanges = highTranslationRanges;
    return { "low" : low, "high" : high };
}

/**
 * Census of the funnel components of one face over D x I (spec 6.3 step 3): a coarse value
 * grid evaluated from screened/materialized blocks (never pointwise splines), trim masking by
 * even-odd ray crossings, and sign-change flood fill with an explicit u-seam wrap for
 * periodic faces. Kernel-oracle seeds (spec 2.2) merge in at a higher layer - this census is
 * pure and self-contained.
 *
 * options: {
 *     uNodesPerPatch, vNodesPerPatch, tNodesPerSpan {number} : grid nodes per patch/span
 *         (>= 3 recommended),
 *     valueTolerance {number} : absolute FLOOR on the threshold below which a value counts
 *         as a zero sign. The threshold itself is derived per block from that block's own
 *         loose value range (screenEnvelopeBlockScaled), because no absolute number can be
 *         right for every block of a face: the same number is a certificate on a block whose
 *         |f| runs to 1e-2 and pure noise on one that runs to 1e-14. Leave it 0 unless a
 *         caller knows a physical noise floor the coefficients do not show,
 *     relativeValueTolerance {number} : the fraction of a block's own value range that
 *         derives its threshold (default ENVELOPE_RELATIVE_SIGN_TOLERANCE),
 *     trimLoops {array} : uv trim loops in the face's knot domain; empty means the whole
 *         rectangle is valid; a node is valid when an even-odd crossing count over all loops
 *         is odd. Each entry is either a bare point array ([ [u, v], ... ], implicitly closed)
 *         or the { points, winding } record extraction's buildFaceTrimLoops produces - the
 *         winding is what tells a loop that WRAPS a periodic seam (stored open, its ends one
 *         period apart) from one that closes on itself,
 *     uPeriodic {boolean} : link the first and last u cell columns during flood fill, and mask
 *         with the cyclic +v ray instead of the +u one,
 *     trimBoundaryTolerance {number} : a node within this distance of a trim loop counts as
 *         VALID whatever the crossing test says. The trim boundary belongs to the face, and a
 *         face that fills its whole surface has a trim loop lying exactly ON the domain
 *         rectangle - where an even-odd ray cast is a coin flip that would silently delete the
 *         boundary cell rows, which is precisely where co-edge components live. Defaults to
 *         1e-6 of the smaller domain span,
 *     degenerate {map} : { uStart, uEnd, vStart, vEnd } booleans, straight from the face
 *         record's `degenerate` - the collapsed control-net boundaries where the surface normal
 *         vanishes. f is identically zero along such a boundary, so without masking it every
 *         real component that reaches the pole floods through it into every other one
 * }
 *
 * Returns { components {array}, uNodes, vNodes, tNodes {arrays of global parameters},
 * cellClass {3D array} : 0 masked / 1 uniform sign / 2 sign-mixed per cell, and
 * mixedCellCount {number} - what certifyCensusCoverage checks the isolation against }.
 * Component: {
 *     cellCount {number},
 *     parameterBounds {map} : uMin..tMax over member cell corners (seam-crossing components
 *         smear across the seam - read crossesUSeam first),
 *     touchesTStart, touchesTEnd, touchesDomainBoundaryUv, touchesTrimBoundary,
 *     touchesDegenerateBoundary, crossesUSeam, isIsland {booleans},
 *     minTCellCenter, maxTCellCenter {maps} : { u, v, t } cell centers at the component's
 *         t-extremes - Newton seeds for island refinement
 * }
 */
export function censusFunnelComponents(patchFactors is map, spans is array, censusOptions is map) returns map
{
    const options = mergeMaps({ "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false,
                "relativeValueTolerance" : ENVELOPE_RELATIVE_SIGN_TOLERANCE,
                "degenerate" : { "uStart" : false, "uEnd" : false, "vStart" : false, "vEnd" : false } },
            censusOptions);
    const uNodesPerPatch = options.uNodesPerPatch;
    const vNodesPerPatch = options.vNodesPerPatch;
    const tNodesPerSpan = options.tNodesPerSpan;
    const uNodeCount = patchFactors.uSegments * (uNodesPerPatch - 1) + 1;
    const vNodeCount = patchFactors.vSegments * (vNodesPerPatch - 1) + 1;
    const tNodeCount = size(spans) * (tNodesPerSpan - 1) + 1;

    // Global node parameter arrays.
    var uNodes = makeArray(uNodeCount, 0);
    var vNodes = makeArray(vNodeCount, 0);
    var tNodes = makeArray(tNodeCount, 0);
    for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
    {
        const patch = patchFactors.patches[uSegment][0];
        for (var offset = 0; offset < uNodesPerPatch; offset += 1)
        {
            uNodes[uSegment * (uNodesPerPatch - 1) + offset] =
                patch.uStart + (patch.uEnd - patch.uStart) * offset / (uNodesPerPatch - 1);
        }
    }
    for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
    {
        const patch = patchFactors.patches[0][vSegment];
        for (var offset = 0; offset < vNodesPerPatch; offset += 1)
        {
            vNodes[vSegment * (vNodesPerPatch - 1) + offset] =
                patch.vStart + (patch.vEnd - patch.vStart) * offset / (vNodesPerPatch - 1);
        }
    }
    for (var spanIndex = 0; spanIndex < size(spans); spanIndex += 1)
    {
        for (var offset = 0; offset < tNodesPerSpan; offset += 1)
        {
            tNodes[spanIndex * (tNodesPerSpan - 1) + offset] =
                spans[spanIndex].tStart + (spans[spanIndex].tEnd - spans[spanIndex].tStart) * offset / (tNodesPerSpan - 1);
        }
    }

    // Block-wise SIGN fill: dead blocks get their certified constant sign, live blocks are
    // materialized once and evaluated on their local node grid. What is stored is the sign
    // rather than the value, because the threshold a value must clear to HAVE a sign is a
    // property of the block that produced it - derived from that block's own loose range - and
    // the block is known here and not in the cell loop below. A node shared by two blocks is
    // written by each of them; the values agree to rounding, and so, away from the threshold,
    // do the signs.
    var signs = makeArray(uNodeCount);
    for (var uIndex = 0; uIndex < uNodeCount; uIndex += 1)
    {
        var plane = makeArray(vNodeCount);
        for (var vIndex = 0; vIndex < vNodeCount; vIndex += 1)
        {
            plane[vIndex] = makeArray(tNodeCount, 0);
        }
        signs[uIndex] = plane;
    }
    var minSignTolerance = undefined;
    var maxSignTolerance = 0;
    var zeroSignNodeCount = 0;
    for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
    {
        for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
        {
            const patch = patchFactors.patches[uSegment][vSegment];
            var products = undefined;
            for (var spanIndex = 0; spanIndex < size(spans); spanIndex += 1)
            {
                const screen = screenEnvelopeBlockScaled(patch, spans[spanIndex],
                        options.relativeValueTolerance, options.valueTolerance);
                const blockTolerance = screen.signTolerance;
                minSignTolerance = minSignTolerance == undefined ? blockTolerance :
                    min(minSignTolerance, blockTolerance);
                maxSignTolerance = max(maxSignTolerance, blockTolerance);
                var blockGrids = undefined;
                var fillSign = 0;
                if (!screen.canVanish)
                {
                    fillSign = screen.looseMin > 0 ? 1 : -1;
                }
                else
                {
                    if (products == undefined)
                    {
                        products = buildEnvelopePatchProducts(patch);
                    }
                    blockGrids = materializeEnvelopeBlock(products, spans[spanIndex]);
                }
                for (var uOffset = 0; uOffset < uNodesPerPatch; uOffset += 1)
                {
                    const uIndex = uSegment * (uNodesPerPatch - 1) + uOffset;
                    const localU = uOffset / (uNodesPerPatch - 1);
                    for (var vOffset = 0; vOffset < vNodesPerPatch; vOffset += 1)
                    {
                        const vIndex = vSegment * (vNodesPerPatch - 1) + vOffset;
                        const localV = vOffset / (vNodesPerPatch - 1);
                        for (var tOffset = 0; tOffset < tNodesPerSpan; tOffset += 1)
                        {
                            const tIndex = spanIndex * (tNodesPerSpan - 1) + tOffset;
                            var nodeSign = fillSign;
                            if (blockGrids != undefined)
                            {
                                const nodeValue = evaluateMaterializedBlock(blockGrids, localU, localV,
                                    tOffset / (tNodesPerSpan - 1));
                                nodeSign = nodeValue > blockTolerance ? 1 :
                                    (nodeValue < -blockTolerance ? -1 : 0);
                            }
                            if (nodeSign == 0)
                            {
                                zeroSignNodeCount += 1;
                            }
                            signs[uIndex][vIndex][tIndex] = nodeSign;
                        }
                    }
                }
            }
        }
    }

    // Two uv node masks, both independent of t. The trim mask is even-odd against the loops -
    // cast in +v when u is cyclic, since a ray along a cyclic direction has no outside to start
    // from. The degeneracy mask is the collapsed control-net boundaries: f vanishes identically
    // there, so the pole line reads as one connected zero set joining everything that reaches
    // it.
    const uPeriodForMask = uNodes[uNodeCount - 1] - uNodes[0];
    const resolvedBoundaryTolerance = options.trimBoundaryTolerance != undefined ?
        options.trimBoundaryTolerance :
        1e-6 * min(uNodes[uNodeCount - 1] - uNodes[0], vNodes[vNodeCount - 1] - vNodes[0]);
    const degenerate = options.degenerate;
    const hasTrimLoops = size(options.trimLoops) > 0;
    const hasDegenerateBoundary = degenerate.uStart == true || degenerate.uEnd == true ||
        degenerate.vStart == true || degenerate.vEnd == true;
    var nodeValid = makeArray(uNodeCount);
    var nodeDegenerate = makeArray(uNodeCount);
    for (var uIndex = 0; uIndex < uNodeCount; uIndex += 1)
    {
        var validRow = makeArray(vNodeCount, true);
        var degenerateRow = makeArray(vNodeCount, false);
        if (hasTrimLoops || hasDegenerateBoundary)
        {
            const uOnDegenerateBoundary = (degenerate.uStart == true && uIndex == 0) ||
                (degenerate.uEnd == true && uIndex == uNodeCount - 1);
            for (var vIndex = 0; vIndex < vNodeCount; vIndex += 1)
            {
                if (hasTrimLoops)
                {
                    validRow[vIndex] = options.uPeriodic ?
                        uvPointInsideLoopsCyclic(options.trimLoops, uNodes[uIndex], vNodes[vIndex], uPeriodForMask) :
                        uvPointInsideLoops(options.trimLoops, uNodes[uIndex], vNodes[vIndex]);
                    if (!validRow[vIndex])
                    {
                        // Only a rejected node can be rescued by the boundary tolerance, so the
                        // distance sweep runs on the minority of nodes rather than all of them.
                        validRow[vIndex] = uvPointOnLoops(options.trimLoops, uNodes[uIndex], vNodes[vIndex],
                            resolvedBoundaryTolerance, options.uPeriodic ? uPeriodForMask : 0);
                    }
                }
                degenerateRow[vIndex] = uOnDegenerateBoundary ||
                    (degenerate.vStart == true && vIndex == 0) ||
                    (degenerate.vEnd == true && vIndex == vNodeCount - 1);
            }
        }
        nodeValid[uIndex] = validRow;
        nodeDegenerate[uIndex] = degenerateRow;
    }

    // Cell classification: 0 invalid (a trim-masked or pole corner), 1 uniform sign, 2 mixed.
    const cellCountU = uNodeCount - 1;
    const cellCountV = vNodeCount - 1;
    const cellCountT = tNodeCount - 1;
    var mixedCellCount = 0;
    var cellClass = makeArray(cellCountU);
    for (var i = 0; i < cellCountU; i += 1)
    {
        var classPlane = makeArray(cellCountV);
        for (var j = 0; j < cellCountV; j += 1)
        {
            var classColumn = makeArray(cellCountT, 1);
            if (cellHasMaskedCorner(nodeValid, i, j) || cellHasDegenerateCorner(nodeDegenerate, i, j))
            {
                for (var k = 0; k < cellCountT; k += 1)
                {
                    classColumn[k] = 0;
                }
            }
            else
            {
                for (var k = 0; k < cellCountT; k += 1)
                {
                    var minimumSign = 1;
                    var maximumSign = -1;
                    for (var corner = 0; corner < 8; corner += 1)
                    {
                        const cornerSign = signs[i + (corner % 2)][j + (floor(corner / 2) % 2)][k + floor(corner / 4)];
                        minimumSign = min(minimumSign, cornerSign);
                        maximumSign = max(maximumSign, cornerSign);
                    }
                    classColumn[k] = (minimumSign < 1 && maximumSign > -1) ? 2 : 1;
                    if (classColumn[k] == 2)
                    {
                        mixedCellCount += 1;
                    }
                }
            }
            classPlane[j] = classColumn;
        }
        cellClass[i] = classPlane;
    }

    // Flood fill over mixed cells, 6-connectivity, u wrap when periodic.
    var visited = makeArray(cellCountU);
    for (var i = 0; i < cellCountU; i += 1)
    {
        var visitedPlane = makeArray(cellCountV);
        for (var j = 0; j < cellCountV; j += 1)
        {
            visitedPlane[j] = makeArray(cellCountT, false);
        }
        visited[i] = visitedPlane;
    }
    // One queue for every component: append() copies, so growing one per component makes the
    // flood fill quadratic in the component size. Every mixed cell is visited exactly once
    // across all components, so a single buffer of that length is enough for all of them.
    var queue = makeArray(max(mixedCellCount, 1), [0, 0, 0]);
    var components = [];
    for (var i = 0; i < cellCountU; i += 1)
    {
        for (var j = 0; j < cellCountV; j += 1)
        {
            for (var k = 0; k < cellCountT; k += 1)
            {
                if (visited[i][j][k] || cellClass[i][j][k] != 2)
                {
                    continue;
                }
                var cellCount = 0;
                var crossesUSeam = false;
                var touchesTStart = false;
                var touchesTEnd = false;
                var touchesDomainBoundaryUv = false;
                var touchesTrimBoundary = false;
                var touchesDegenerateBoundary = false;
                var uMin = uNodes[uNodeCount - 1];
                var uMax = uNodes[0];
                var vMin = vNodes[vNodeCount - 1];
                var vMax = vNodes[0];
                var tMin = tNodes[tNodeCount - 1];
                var tMax = tNodes[0];
                var minTCell = undefined;
                var maxTCell = undefined;
                queue[0] = [i, j, k];
                var queueLength = 1;
                var queueCursor = 0;
                visited[i][j][k] = true;
                while (queueCursor < queueLength)
                {
                    const currentCell = queue[queueCursor];
                    queueCursor += 1;
                    const ci = currentCell[0];
                    const cj = currentCell[1];
                    const ck = currentCell[2];
                    cellCount += 1;
                    uMin = min(uMin, uNodes[ci]);
                    uMax = max(uMax, uNodes[ci + 1]);
                    vMin = min(vMin, vNodes[cj]);
                    vMax = max(vMax, vNodes[cj + 1]);
                    tMin = min(tMin, tNodes[ck]);
                    tMax = max(tMax, tNodes[ck + 1]);
                    if (minTCell == undefined || tNodes[ck] < tNodes[minTCell[2]])
                    {
                        minTCell = currentCell;
                    }
                    if (maxTCell == undefined || tNodes[ck + 1] > tNodes[maxTCell[2] + 1])
                    {
                        maxTCell = currentCell;
                    }
                    if (ck == 0)
                    {
                        touchesTStart = true;
                    }
                    if (ck == cellCountT - 1)
                    {
                        touchesTEnd = true;
                    }
                    if (cj == 0 || cj == cellCountV - 1 || (!options.uPeriodic && (ci == 0 || ci == cellCountU - 1)))
                    {
                        touchesDomainBoundaryUv = true;
                    }
                    for (var direction = 0; direction < 6; direction += 1)
                    {
                        var ni = ci + (direction == 0 ? 1 : (direction == 1 ? -1 : 0));
                        const nj = cj + (direction == 2 ? 1 : (direction == 3 ? -1 : 0));
                        const nk = ck + (direction == 4 ? 1 : (direction == 5 ? -1 : 0));
                        var wrapped = false;
                        if (options.uPeriodic && ni < 0)
                        {
                            ni = cellCountU - 1;
                            wrapped = true;
                        }
                        if (options.uPeriodic && ni > cellCountU - 1)
                        {
                            ni = 0;
                            wrapped = true;
                        }
                        if (ni < 0 || ni > cellCountU - 1 || nj < 0 || nj > cellCountV - 1 || nk < 0 || nk > cellCountT - 1)
                        {
                            continue;
                        }
                        if (cellClass[ni][nj][nk] == 0)
                        {
                            // Which mask blocked it is a uv question, so the node masks answer
                            // it directly - a cell can be blocked by both.
                            if (cellHasMaskedCorner(nodeValid, ni, nj))
                            {
                                touchesTrimBoundary = true;
                            }
                            if (cellHasDegenerateCorner(nodeDegenerate, ni, nj))
                            {
                                touchesDegenerateBoundary = true;
                            }
                            continue;
                        }
                        if (cellClass[ni][nj][nk] != 2 || visited[ni][nj][nk])
                        {
                            continue;
                        }
                        if (wrapped)
                        {
                            crossesUSeam = true;
                        }
                        visited[ni][nj][nk] = true;
                        queue[queueLength] = [ni, nj, nk];
                        queueLength += 1;
                    }
                }
                components = append(components, {
                            "cellCount" : cellCount,
                            "parameterBounds" : { "uMin" : uMin, "uMax" : uMax, "vMin" : vMin, "vMax" : vMax,
                                "tMin" : tMin, "tMax" : tMax },
                            "touchesTStart" : touchesTStart,
                            "touchesTEnd" : touchesTEnd,
                            "touchesDomainBoundaryUv" : touchesDomainBoundaryUv,
                            "touchesTrimBoundary" : touchesTrimBoundary,
                            "touchesDegenerateBoundary" : touchesDegenerateBoundary,
                            "crossesUSeam" : crossesUSeam,
                            "isIsland" : !touchesDomainBoundaryUv && !touchesTrimBoundary &&
                                !touchesDegenerateBoundary && !touchesTStart && !touchesTEnd,
                            "minTCellCenter" : cellCenter(uNodes, vNodes, tNodes, minTCell),
                            "maxTCellCenter" : cellCenter(uNodes, vNodes, tNodes, maxTCell)
                        });
            }
        }
    }
    return { "components" : components, "uNodes" : uNodes, "vNodes" : vNodes, "tNodes" : tNodes,
            "cellClass" : cellClass, "mixedCellCount" : mixedCellCount,
            "signTolerance" : { "minimum" : minSignTolerance == undefined ? 0 : minSignTolerance,
                "maximum" : maxSignTolerance, "zeroSignNodeCount" : zeroSignNodeCount } };
}

/**
 * Certify that one census SAW every component there is (spec 2.2). The census reads signs on a
 * grid, so on its own it cannot tell "no component here" from "a component too small for this
 * grid". The factored isolation can: a screen-dead cell is a proof that f has no zero in it, so
 * the live cells are a certified COVER of the whole zero set.
 *
 * The two are compared on the census's own cell lattice. Every census cell overlapping an
 * isolation live cell is marked live; the marked cells are then flood-filled into regions, and a
 * region carrying no sign-mixed cell is somewhere the zero set may live and the census reported
 * nothing - the only shape a missed component can take. Cells the census masked out (trim,
 * poles) are excluded: those are not part of the face.
 *
 * The reverse direction is a cross-path check rather than a certificate. A sign-mixed census cell
 * outside every live cell would mean the pointwise block evaluation and the interval screen
 * disagree about whether f can vanish there, which is a bug in one of them, not a topology
 * finding - so `contradictions` is expected to be 0 always.
 *
 * The isolation runs at the census's OWN cell size as its leaf floor, so a live leaf lands in one
 * census cell wherever the two lattices align and in at most a few where they do not. Its zero
 * threshold is the block's derived sign tolerance, the same number the census gave the signs it
 * is being checked against.
 *
 * options: {
 *     valueTolerance, relativeValueTolerance {number} : as censusFunnelComponents,
 *     uPeriodic {boolean} : wrap the first and last u cell columns when filling regions,
 *     maxSplitDepth {number} : isolation safety cap (default 24),
 *     unresolvedReportLimit {number} : how many unresolved regions to return in full (default 4)
 * }
 *
 * Returns {
 *     certified {boolean} : no unresolved region and no contradiction,
 *     unresolvedRegionCount {number}, unresolvedRegions {array} : the first few in full,
 *     contradictions {number},
 *     liveCellCount {number} : census cells marked live, regionCount {number},
 *     liveLeafCount, screenedCount, deadCount {number} : the isolation's own tally,
 *     isolationFloor {map} : the leaf sizes used, as local fractions of a patch/span
 * }
 */
export function certifyCensusCoverage(patchFactors is map, spans is array, censusResult is map,
    options is map) returns map
{
    const filled = mergeMaps({ "valueTolerance" : 0,
                "relativeValueTolerance" : ENVELOPE_RELATIVE_SIGN_TOLERANCE, "uPeriodic" : false,
                "maxSplitDepth" : 24, "unresolvedReportLimit" : 4 }, options);
    const uNodes = censusResult.uNodes;
    const vNodes = censusResult.vNodes;
    const tNodes = censusResult.tNodes;
    const cellClass = censusResult.cellClass;
    const cellCountU = size(uNodes) - 1;
    const cellCountV = size(vNodes) - 1;
    const cellCountT = size(tNodes) - 1;
    const uNodesPerPatch = cellCountU / patchFactors.uSegments + 1;
    const vNodesPerPatch = cellCountV / patchFactors.vSegments + 1;
    const tNodesPerSpan = cellCountT / size(spans) + 1;
    const isolationOptions = {
            "minCellWidthU" : 1 / (uNodesPerPatch - 1),
            "minCellWidthV" : 1 / (vNodesPerPatch - 1),
            "minCellWidthT" : 1 / (tNodesPerSpan - 1),
            "maxSplitDepth" : filled.maxSplitDepth
        };

    var live = makeArray(cellCountU);
    for (var i = 0; i < cellCountU; i += 1)
    {
        var plane = makeArray(cellCountV);
        for (var j = 0; j < cellCountV; j += 1)
        {
            plane[j] = makeArray(cellCountT, false);
        }
        live[i] = plane;
    }

    var liveLeafCount = 0;
    var screenedCount = 0;
    var deadCount = 0;
    var liveCellCount = 0;
    for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
    {
        for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
        {
            const patch = patchFactors.patches[uSegment][vSegment];
            for (var spanIndex = 0; spanIndex < size(spans); spanIndex += 1)
            {
                const screen = screenEnvelopeBlockScaled(patch, spans[spanIndex],
                        filled.relativeValueTolerance, filled.valueTolerance);
                screenedCount += 1;
                if (!screen.canVanish)
                {
                    deadCount += 1;
                    continue;
                }
                const isolation = isolateEnvelopeCells(patch, spans[spanIndex],
                        mergeMaps(isolationOptions, { "valueTolerance" : screen.signTolerance }));
                liveLeafCount += size(isolation.liveCells);
                screenedCount += isolation.screenedCount;
                deadCount += isolation.deadCount;
                for (var cell in isolation.liveCells)
                {
                    const uRange = overlappingCellRange(uNodes, cell.globalUStart, cell.globalUEnd);
                    const vRange = overlappingCellRange(vNodes, cell.globalVStart, cell.globalVEnd);
                    const tRange = overlappingCellRange(tNodes, cell.globalTStart, cell.globalTEnd);
                    for (var i = uRange.start; i <= uRange.end; i += 1)
                    {
                        for (var j = vRange.start; j <= vRange.end; j += 1)
                        {
                            for (var k = tRange.start; k <= tRange.end; k += 1)
                            {
                                if (!live[i][j][k])
                                {
                                    live[i][j][k] = true;
                                    liveCellCount += 1;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    var contradictions = 0;
    for (var i = 0; i < cellCountU; i += 1)
    {
        for (var j = 0; j < cellCountV; j += 1)
        {
            for (var k = 0; k < cellCountT; k += 1)
            {
                if (cellClass[i][j][k] == 2 && !live[i][j][k])
                {
                    contradictions += 1;
                }
            }
        }
    }

    const regions = fillLiveRegions(live, cellClass, uNodes, vNodes, tNodes, filled.uPeriodic,
            liveCellCount);
    var unresolvedRegionCount = 0;
    var unresolvedRegions = [];
    for (var region in regions)
    {
        if (region.mixedCellCount > 0)
        {
            continue;
        }
        unresolvedRegionCount += 1;
        if (size(unresolvedRegions) < filled.unresolvedReportLimit)
        {
            unresolvedRegions = append(unresolvedRegions, region);
        }
    }
    return {
            "certified" : unresolvedRegionCount == 0 && contradictions == 0,
            "unresolvedRegionCount" : unresolvedRegionCount,
            "unresolvedRegions" : unresolvedRegions,
            "contradictions" : contradictions,
            "liveCellCount" : liveCellCount,
            "regionCount" : size(regions),
            "liveLeafCount" : liveLeafCount,
            "screenedCount" : screenedCount,
            "deadCount" : deadCount,
            "isolationFloor" : { "u" : isolationOptions.minCellWidthU,
                "v" : isolationOptions.minCellWidthV, "t" : isolationOptions.minCellWidthT }
        };
}

/**
 * The inclusive index range of the cells of `nodes` that overlap [low, high]. A leaf sitting
 * exactly on cell boundaries claims the one cell it fills rather than its two neighbours, which
 * is what keeps an aligned lattice one-to-one; a leaf narrower than a cell still claims that
 * cell, since containment is what the certificate needs.
 */
function overlappingCellRange(nodes is array, low is number, high is number) returns map
{
    const cellCount = size(nodes) - 1;
    const tolerance = 1e-9 * (nodes[cellCount] - nodes[0]);
    var start = -1;
    var end = -1;
    for (var index = 0; index < cellCount; index += 1)
    {
        if (nodes[index + 1] > low + tolerance && nodes[index] < high - tolerance)
        {
            start = start < 0 ? index : start;
            end = index;
        }
    }
    if (start >= 0)
    {
        return { "start" : start, "end" : end };
    }
    // A leaf thinner than the overlap tolerance: give it the cell that contains it.
    for (var index = 0; index < cellCount; index += 1)
    {
        if (nodes[index] - tolerance <= low && high <= nodes[index + 1] + tolerance)
        {
            return { "start" : index, "end" : index };
        }
    }
    return { "start" : 0, "end" : -1 };
}

/**
 * Flood fill the live cells into 6-connected regions (u wrap when periodic), skipping cells the
 * census masked out. Each region reports how many of its cells the census found sign-mixed.
 * One preallocated queue serves every region: every live cell is visited exactly once across all
 * of them, and append() copies.
 */
function fillLiveRegions(live is array, cellClass is array, uNodes is array, vNodes is array,
    tNodes is array, uPeriodic is boolean, liveCellCount is number) returns array
{
    const cellCountU = size(uNodes) - 1;
    const cellCountV = size(vNodes) - 1;
    const cellCountT = size(tNodes) - 1;
    var visited = makeArray(cellCountU);
    for (var i = 0; i < cellCountU; i += 1)
    {
        var plane = makeArray(cellCountV);
        for (var j = 0; j < cellCountV; j += 1)
        {
            plane[j] = makeArray(cellCountT, false);
        }
        visited[i] = plane;
    }
    var queue = makeArray(max(liveCellCount, 1), [0, 0, 0]);
    var regions = [];
    for (var i = 0; i < cellCountU; i += 1)
    {
        for (var j = 0; j < cellCountV; j += 1)
        {
            for (var k = 0; k < cellCountT; k += 1)
            {
                if (visited[i][j][k] || !live[i][j][k] || cellClass[i][j][k] == 0)
                {
                    continue;
                }
                var cellCount = 0;
                var mixedCellCount = 0;
                var crossesUSeam = false;
                var uMin = uNodes[cellCountU];
                var uMax = uNodes[0];
                var vMin = vNodes[cellCountV];
                var vMax = vNodes[0];
                var tMin = tNodes[cellCountT];
                var tMax = tNodes[0];
                queue[0] = [i, j, k];
                var queueLength = 1;
                var queueCursor = 0;
                visited[i][j][k] = true;
                while (queueCursor < queueLength)
                {
                    const currentCell = queue[queueCursor];
                    queueCursor += 1;
                    const ci = currentCell[0];
                    const cj = currentCell[1];
                    const ck = currentCell[2];
                    cellCount += 1;
                    if (cellClass[ci][cj][ck] == 2)
                    {
                        mixedCellCount += 1;
                    }
                    uMin = min(uMin, uNodes[ci]);
                    uMax = max(uMax, uNodes[ci + 1]);
                    vMin = min(vMin, vNodes[cj]);
                    vMax = max(vMax, vNodes[cj + 1]);
                    tMin = min(tMin, tNodes[ck]);
                    tMax = max(tMax, tNodes[ck + 1]);
                    for (var direction = 0; direction < 6; direction += 1)
                    {
                        var ni = ci + (direction == 0 ? 1 : (direction == 1 ? -1 : 0));
                        const nj = cj + (direction == 2 ? 1 : (direction == 3 ? -1 : 0));
                        const nk = ck + (direction == 4 ? 1 : (direction == 5 ? -1 : 0));
                        var wrapped = false;
                        if (uPeriodic && ni < 0)
                        {
                            ni = cellCountU - 1;
                            wrapped = true;
                        }
                        if (uPeriodic && ni > cellCountU - 1)
                        {
                            ni = 0;
                            wrapped = true;
                        }
                        if (ni < 0 || ni > cellCountU - 1 || nj < 0 || nj > cellCountV - 1 ||
                            nk < 0 || nk > cellCountT - 1)
                        {
                            continue;
                        }
                        if (visited[ni][nj][nk] || !live[ni][nj][nk] || cellClass[ni][nj][nk] == 0)
                        {
                            continue;
                        }
                        if (wrapped)
                        {
                            crossesUSeam = true;
                        }
                        visited[ni][nj][nk] = true;
                        queue[queueLength] = [ni, nj, nk];
                        queueLength += 1;
                    }
                }
                regions = append(regions, {
                            "cellCount" : cellCount,
                            "mixedCellCount" : mixedCellCount,
                            "crossesUSeam" : crossesUSeam,
                            "parameterBounds" : { "uMin" : uMin, "uMax" : uMax, "vMin" : vMin,
                                "vMax" : vMax, "tMin" : tMin, "tMax" : tMax }
                        });
            }
        }
    }
    return regions;
}

/**
 * The census of spec 6.3 step 3 with its topology certified rather than assumed: run the census,
 * certify its coverage, and refine by halving the cell size until the certificate holds - or
 * report that it does not.
 *
 * Two independent things have to be true before a component set is the answer.
 *
 * 1. *Nothing hides.* certifyCensusCoverage proves it from the interval screen's dead
 *    certificates. This half is a proof.
 * 2. *Nothing merged.* Coverage cannot see two zero-set components sharing one live region: at a
 *    coarse enough cell size two separate contact loops read as one. So the component set must
 *    also AGREE with the set found at half the cell size - same count, same flags. This half is
 *    a convergence check, not a proof: two consecutive resolutions that are both too coarse can
 *    agree with each other and be wrong together, which is what `minimumNodesPerPatch` exists to
 *    keep a caller away from.
 *
 * A refinement doubles the cells per patch in every direction (n nodes become 2n - 1), which
 * keeps a power-of-two node count aligned with the isolation's halving lattice, so live leaves and
 * census cells stay one-to-one.
 *
 * options: censusFunnelComponents's options, plus {
 *     minimumNodesPerPatch, minimumNodesPerSpan {number} : the floor the first pass starts from
 *         whatever the caller asked for (defaults 9),
 *     maxRefinements {number} : how many doublings to spend before giving up (default 2),
 *     requireStability {boolean} : demand the stability half (default true; false accepts the
 *         coverage proof alone, for a caller that already knows the topology)
 * }
 *
 * Returns censusFunnelComponents's record for the final pass, plus {
 *     certified {boolean}, stable {boolean}, coverage {map} : the final certificate,
 *     refinements {number}, resolution {map} : the node counts that produced the answer,
 *     passes {array} : one summary per pass, so a caller can report what refinement cost
 * }
 */
export function censusFunnelComponentsCertified(patchFactors is map, spans is array,
    censusOptions is map) returns map
{
    const options = mergeMaps({ "minimumNodesPerPatch" : 9, "minimumNodesPerSpan" : 9,
                "maxRefinements" : 2, "requireStability" : true }, censusOptions);
    var uNodesPerPatch = max(options.uNodesPerPatch, options.minimumNodesPerPatch);
    var vNodesPerPatch = max(options.vNodesPerPatch, options.minimumNodesPerPatch);
    var tNodesPerSpan = max(options.tNodesPerSpan, options.minimumNodesPerSpan);
    var result = undefined;
    var coverage = undefined;
    var previousSignatures = undefined;
    var stable = false;
    var refinements = 0;
    var passes = [];
    for (var attempt = 0; attempt <= options.maxRefinements; attempt += 1)
    {
        var passOptions = options;
        passOptions.uNodesPerPatch = uNodesPerPatch;
        passOptions.vNodesPerPatch = vNodesPerPatch;
        passOptions.tNodesPerSpan = tNodesPerSpan;
        result = censusFunnelComponents(patchFactors, spans, passOptions);
        coverage = certifyCensusCoverage(patchFactors, spans, result, passOptions);
        const signatures = componentFlagSignatures(result.components);
        stable = previousSignatures != undefined &&
            signatureMultisetsAgree(previousSignatures, signatures);
        refinements = attempt;
        passes = append(passes, {
                    "uNodesPerPatch" : uNodesPerPatch, "vNodesPerPatch" : vNodesPerPatch,
                    "tNodesPerSpan" : tNodesPerSpan,
                    "componentCount" : size(result.components),
                    "mixedCellCount" : result.mixedCellCount,
                    "liveCellCount" : coverage.liveCellCount,
                    "unresolvedRegionCount" : coverage.unresolvedRegionCount,
                    "contradictions" : coverage.contradictions,
                    "stable" : stable
                });
        if (coverage.certified && (stable || !options.requireStability))
        {
            break;
        }
        previousSignatures = signatures;
        uNodesPerPatch = 2 * uNodesPerPatch - 1;
        vNodesPerPatch = 2 * vNodesPerPatch - 1;
        tNodesPerSpan = 2 * tNodesPerSpan - 1;
    }
    return mergeMaps(result, {
                "certified" : coverage.certified && (stable || !options.requireStability),
                "stable" : stable,
                "coverage" : coverage,
                "refinements" : refinements,
                "resolution" : { "uNodesPerPatch" : passes[size(passes) - 1].uNodesPerPatch,
                    "vNodesPerPatch" : passes[size(passes) - 1].vNodesPerPatch,
                    "tNodesPerSpan" : passes[size(passes) - 1].tNodesPerSpan },
                "passes" : passes
            });
}

/** One string per component, encoding the seven boolean flags the stability check compares. */
function componentFlagSignatures(components is array) returns array
{
    var signatures = makeArray(size(components), "");
    for (var index = 0; index < size(components); index += 1)
    {
        const component = components[index];
        signatures[index] =
            (component.isIsland ? "1" : "0") ~
            (component.touchesTStart ? "1" : "0") ~
            (component.touchesTEnd ? "1" : "0") ~
            (component.touchesDomainBoundaryUv ? "1" : "0") ~
            (component.touchesTrimBoundary ? "1" : "0") ~
            (component.touchesDegenerateBoundary ? "1" : "0") ~
            (component.crossesUSeam ? "1" : "0");
    }
    return signatures;
}

/** True when two signature arrays hold the same strings with the same multiplicities. */
function signatureMultisetsAgree(first is array, second is array) returns boolean
{
    if (size(first) != size(second))
    {
        return false;
    }
    for (var signature in first)
    {
        if (countSignature(first, signature) != countSignature(second, signature))
        {
            return false;
        }
    }
    return true;
}

function countSignature(signatures is array, signature is string) returns number
{
    var count = 0;
    for (var candidate in signatures)
    {
        if (candidate == signature)
        {
            count += 1;
        }
    }
    return count;
}

/**
 * Even-odd point-in-loops classification for a face that is not cyclic in u: casts a ray in +u
 * and counts crossings over every loop, each implicitly closed. Odd count = inside. Orientation
 * of the loops does not matter, so boundary-plus-holes trim sets work unmodified.
 *
 * Loop entries take either shape the census accepts: a bare point array or a { points, winding }
 * record. Winding is meaningless here - a face with a winding trim loop is cyclic in u by
 * construction, and that is uvPointInsideLoopsCyclic's job.
 */
export function uvPointInsideLoops(trimLoops is array, u is number, v is number) returns boolean
{
    var crossings = 0;
    for (var trimLoopEntry in trimLoops)
    {
        const trimLoop = trimLoopPoints(trimLoopEntry);
        const pointCount = size(trimLoop);
        for (var index = 0; index < pointCount; index += 1)
        {
            const start = trimLoop[index];
            const end = trimLoop[(index + 1) % pointCount];
            if ((start[1] > v) != (end[1] > v))
            {
                const crossingU = start[0] + (v - start[1]) / (end[1] - start[1]) * (end[0] - start[0]);
                if (crossingU > u)
                {
                    crossings += 1;
                }
            }
        }
    }
    return crossings % 2 == 1;
}

/**
 * Even-odd point-in-loops classification for a face that is CYCLIC in u: casts the ray in +v
 * and counts crossings against every periodic image of the test point.
 *
 * The +u ray of uvPointInsideLoops has no outside to start from on a closed face, because a ray
 * along a cyclic direction never leaves the domain. The v direction always does, and casting it
 * handles both kinds of loop a closed face produces with one test:
 *   - a loop that WINDS the seam is stored as an open chain whose two ends are the same point
 *     one period apart, so its segments are walked with no implicit closing segment. The ray
 *     crosses such a loop once from below, which is what makes the band between two winding
 *     trims come out odd and everything outside it even.
 *   - a loop that does not wind closes on itself, and if it straddles the seam its u values run
 *     a little past the domain edge. Testing every periodic image of the point is what finds it
 *     from both sides of the seam.
 *
 * Loop entries take either shape the census accepts: a bare point array (winding 0) or a
 * { points, winding } record.
 */
export function uvPointInsideLoopsCyclic(trimLoops is array, u is number, v is number, uPeriod is number) returns boolean
{
    var crossings = 0;
    for (var trimLoop in trimLoops)
    {
        const loopPoints = trimLoopPoints(trimLoop);
        const pointCount = size(loopPoints);
        const segmentCount = trimLoopWinding(trimLoop) != 0 ? pointCount - 1 : pointCount;
        var uMin = loopPoints[0][0];
        var uMax = loopPoints[0][0];
        for (var loopPoint in loopPoints)
        {
            uMin = min(uMin, loopPoint[0]);
            uMax = max(uMax, loopPoint[0]);
        }
        const firstImage = floor((uMin - u) / uPeriod);
        const lastImage = ceil((uMax - u) / uPeriod);
        for (var index = 0; index < segmentCount; index += 1)
        {
            const start = loopPoints[index];
            const end = loopPoints[(index + 1) % pointCount];
            for (var image = firstImage; image <= lastImage; image += 1)
            {
                const imageU = u + image * uPeriod;
                if ((start[0] > imageU) != (end[0] > imageU))
                {
                    const crossingV = start[1] + (imageU - start[0]) / (end[0] - start[0]) * (end[1] - start[1]);
                    if (crossingV > v)
                    {
                        crossings += 1;
                    }
                }
            }
        }
    }
    return crossings % 2 == 1;
}

/**
 * Whether (u, v) lies within `tolerance` of any trim loop segment - the on-the-boundary case
 * that no even-odd ray cast can decide. `uPeriod` nonzero also tests the point's periodic
 * images, matching the cyclic mask; a winding loop is walked without its implicit closure, the
 * same way.
 */
export function uvPointOnLoops(trimLoops is array, u is number, v is number, tolerance is number,
    uPeriod is number) returns boolean
{
    const toleranceSquared = tolerance * tolerance;
    for (var trimLoop in trimLoops)
    {
        const loopPoints = trimLoopPoints(trimLoop);
        const pointCount = size(loopPoints);
        const segmentCount = trimLoopWinding(trimLoop) != 0 ? pointCount - 1 : pointCount;
        for (var index = 0; index < segmentCount; index += 1)
        {
            const start = loopPoints[index];
            const end = loopPoints[(index + 1) % pointCount];
            const imageU = uPeriod == 0 ? u : u + round((0.5 * (start[0] + end[0]) - u) / uPeriod) * uPeriod;
            if (pointToSegmentSquaredDistance(imageU, v, start, end) <= toleranceSquared)
            {
                return true;
            }
        }
    }
    return false;
}

/**
 * Squared uv distance from (pointU, pointV) to the segment start..end, component-wise on plain
 * numbers. The endpoints are left untyped and indexed rather than taken as Vectors, because the
 * census documents a trim loop as EITHER an array of 2D Vectors (what buildFaceTrimLoops emits)
 * or bare [u, v] pairs (what every hand-built fixture uses), and Vector arithmetic accepts only
 * the first. Indexing accepts both, and allocates nothing in what is the mask's inner loop.
 */
function pointToSegmentSquaredDistance(pointU is number, pointV is number, start, end) returns number
{
    const alongU = end[0] - start[0];
    const alongV = end[1] - start[1];
    const alongLengthSquared = alongU * alongU + alongV * alongV;
    var offsetU = pointU - start[0];
    var offsetV = pointV - start[1];
    if (alongLengthSquared > 0)
    {
        const projection = (offsetU * alongU + offsetV * alongV) / alongLengthSquared;
        const clamped = max(0, min(1, projection));
        offsetU -= clamped * alongU;
        offsetV -= clamped * alongV;
    }
    return offsetU * offsetU + offsetV * offsetV;
}

/** The points of a trim loop given in either accepted shape. */
function trimLoopPoints(trimLoop) returns array
{
    return trimLoop is array ? trimLoop : trimLoop.points;
}

/** The u winding of a trim loop; a bare point array is a closed polygon, so zero. */
function trimLoopWinding(trimLoop) returns number
{
    return trimLoop is array ? 0 : trimLoop.winding;
}

/** Whether any of the four uv corners of cell (i, j) is trimmed away. */
function cellHasMaskedCorner(nodeValid is array, i is number, j is number) returns boolean
{
    return !nodeValid[i][j] || !nodeValid[i + 1][j] || !nodeValid[i][j + 1] || !nodeValid[i + 1][j + 1];
}

/** Whether any of the four uv corners of cell (i, j) sits on a collapsed net boundary. */
function cellHasDegenerateCorner(nodeDegenerate is array, i is number, j is number) returns boolean
{
    return nodeDegenerate[i][j] || nodeDegenerate[i + 1][j] || nodeDegenerate[i][j + 1] ||
        nodeDegenerate[i + 1][j + 1];
}

/** The center of one census cell in global parameters. */
function cellCenter(uNodes is array, vNodes is array, tNodes is array, cellIndices is array) returns map
{
    return {
            "u" : 0.5 * (uNodes[cellIndices[0]] + uNodes[cellIndices[0] + 1]),
            "v" : 0.5 * (vNodes[cellIndices[1]] + vNodes[cellIndices[1] + 1]),
            "t" : 0.5 * (tNodes[cellIndices[2]] + tNodes[cellIndices[2] + 1])
        };
}

/**
 * 3-variable Newton on (f, f_u, f_v) = 0 over one materialized block, with EXACT partials
 * read off differentiated coefficient nets - no finite differences, no sampling. This is the
 * grazing-island t-extreme refinement of spec 6.3 step 3; the same stationary condition finds
 * where an island is born or dies.
 *
 * All coordinates are block-local ([0, 1]^3). options: { iterationLimit (default 30),
 * stepTolerance (default 1e-13) }.
 * Returns { converged {boolean}, localU, localV, localT, functionValue, gradientU, gradientV }.
 */
export function refineBlockStationaryPoint(blockGrids is array, seedU is number, seedV is number, seedT is number,
    options is map) returns map
{
    const iterationLimit = options.iterationLimit == undefined ? 30 : options.iterationLimit;
    const stepTolerance = options.stepTolerance == undefined ? 1e-13 : options.stepTolerance;
    const timeCoefficientCount = size(blockGrids);

    var uDerivativeGrids = makeArray(timeCoefficientCount);
    var vDerivativeGrids = makeArray(timeCoefficientCount);
    var uuGrids = makeArray(timeCoefficientCount);
    var uvGrids = makeArray(timeCoefficientCount);
    var vvGrids = makeArray(timeCoefficientCount);
    for (var m = 0; m < timeCoefficientCount; m += 1)
    {
        uDerivativeGrids[m] = differentiateBernsteinGridU(blockGrids[m]);
        vDerivativeGrids[m] = differentiateBernsteinGridV(blockGrids[m]);
        uuGrids[m] = differentiateBernsteinGridU(uDerivativeGrids[m]);
        uvGrids[m] = differentiateBernsteinGridV(uDerivativeGrids[m]);
        vvGrids[m] = differentiateBernsteinGridV(vDerivativeGrids[m]);
    }

    var u = seedU;
    var v = seedV;
    var t = seedT;
    var converged = false;
    var functionValue = 0;
    var gradientU = 0;
    var gradientV = 0;
    for (var iteration = 0; iteration < iterationLimit; iteration += 1)
    {
        var valueCoefficients = makeArray(timeCoefficientCount, 0);
        var uCoefficients = makeArray(timeCoefficientCount, 0);
        var vCoefficients = makeArray(timeCoefficientCount, 0);
        var uuCoefficients = makeArray(timeCoefficientCount, 0);
        var uvCoefficients = makeArray(timeCoefficientCount, 0);
        var vvCoefficients = makeArray(timeCoefficientCount, 0);
        for (var m = 0; m < timeCoefficientCount; m += 1)
        {
            valueCoefficients[m] = evaluateBernsteinGrid(blockGrids[m], u, v);
            uCoefficients[m] = evaluateBernsteinGrid(uDerivativeGrids[m], u, v);
            vCoefficients[m] = evaluateBernsteinGrid(vDerivativeGrids[m], u, v);
            uuCoefficients[m] = evaluateBernsteinGrid(uuGrids[m], u, v);
            uvCoefficients[m] = evaluateBernsteinGrid(uvGrids[m], u, v);
            vvCoefficients[m] = evaluateBernsteinGrid(vvGrids[m], u, v);
        }
        functionValue = evaluateBernstein(valueCoefficients, t);
        gradientU = evaluateBernstein(uCoefficients, t);
        gradientV = evaluateBernstein(vCoefficients, t);
        const timeDerivative = evaluateBernstein(differentiateBernstein(valueCoefficients), t);
        const uu = evaluateBernstein(uuCoefficients, t);
        const uv = evaluateBernstein(uvCoefficients, t);
        const vv = evaluateBernstein(vvCoefficients, t);
        const ut = evaluateBernstein(differentiateBernstein(uCoefficients), t);
        const vt = evaluateBernstein(differentiateBernstein(vCoefficients), t);

        const solved = solveThreeByThree(
            [[gradientU, gradientV, timeDerivative],
                [uu, uv, ut],
                [uv, vv, vt]],
            [-functionValue, -gradientU, -gradientV]);
        if (solved == undefined)
        {
            break;
        }
        var stepU = clampMagnitude(solved[0], 0.25);
        var stepV = clampMagnitude(solved[1], 0.25);
        var stepT = clampMagnitude(solved[2], 0.25);
        u = clampToUnit(u + stepU);
        v = clampToUnit(v + stepV);
        t = clampToUnit(t + stepT);
        if (max(max(abs(stepU), abs(stepV)), abs(stepT)) < stepTolerance)
        {
            converged = true;
            break;
        }
    }
    return {
            "converged" : converged,
            "localU" : u, "localV" : v, "localT" : t,
            "functionValue" : functionValue, "gradientU" : gradientU, "gradientV" : gradientV
        };
}

/**
 * The time derivative of the contact function at one sample:
 * g_t = <A' n, A' p + b'> + <A n, A'' p + b''>.
 */
export function evaluateContactFunctionTimeDerivative(strippedMotion is map, normal is Vector, point is Vector,
    t is number) returns number
{
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * point + sample.translationDerivative;
    const acceleration = sample.rotationSecondDerivative * point + sample.translationSecondDerivative;
    return dot(sample.rotationDerivative * normal, velocity) + dot(sample.rotation * normal, acceleration);
}

/**
 * All roots of the contact function g(t) = <A n, A' p + b'> on [tStart, tEnd]: sign-change
 * brackets on a station grid, each refined by bisection-safeguarded Newton (spec 6.3 step 1).
 * A tangential (non-crossing) zero that stays one-signed between stations is not detected -
 * the sliding audit owns the identically-zero case, and grazing tangencies belong to the
 * island machinery.
 * Returns an array of { t, value }, ascending in t.
 */
export function findContactFunctionRoots(strippedMotion is map, normal is Vector, point is Vector,
    tStart is number, tEnd is number, stationCount is number, tTolerance is number) returns array
{
    var stationValues = makeArray(stationCount, 0);
    var stationParameters = makeArray(stationCount, 0);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        stationParameters[stationIndex] = tStart + (tEnd - tStart) * stationIndex / (stationCount - 1);
        stationValues[stationIndex] = evaluateContactFunctionAtPoint(strippedMotion, normal, point,
            stationParameters[stationIndex]);
    }
    var roots = [];
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        if (stationValues[stationIndex] == 0)
        {
            roots = append(roots, { "t" : stationParameters[stationIndex], "value" : 0 });
            continue;
        }
        if (stationIndex == stationCount - 1 || stationValues[stationIndex] * stationValues[stationIndex + 1] >= 0)
        {
            continue;
        }
        roots = append(roots, refineContactRoot(strippedMotion, normal, point,
                stationParameters[stationIndex], stationParameters[stationIndex + 1],
                stationValues[stationIndex], stationValues[stationIndex + 1], tTolerance));
    }
    // Deduplicate roots that landed within tolerance of each other (a zero on a station).
    var deduplicated = [];
    for (var root in roots)
    {
        if (size(deduplicated) == 0 || root.t - deduplicated[size(deduplicated) - 1].t > 10 * tTolerance)
        {
            deduplicated = append(deduplicated, root);
        }
    }
    return deduplicated;
}

/**
 * Sharp-vertex contact intervals (spec section 8): the sub-intervals of [tStart, tEnd] where
 * the vertex's cone-normal contact functions s_i(t) do not all share one sign. Breakpoints
 * are the union of every s_i's roots; each gap is classified by its midpoint sign pattern and
 * adjacent qualifying gaps merge.
 * Returns an array of { tStart, tEnd }.
 */
export function solveVertexContactIntervals(strippedMotion is map, coneNormals is array, point is Vector,
    tStart is number, tEnd is number, stationCount is number, tTolerance is number) returns array
{
    var breakpoints = [tStart, tEnd];
    for (var normal in coneNormals)
    {
        for (var root in findContactFunctionRoots(strippedMotion, normal, point, tStart, tEnd, stationCount, tTolerance))
        {
            breakpoints = append(breakpoints, root.t);
        }
    }
    breakpoints = sort(breakpoints, function(a, b)
        {
            return a - b;
        });
    var intervals = [];
    for (var index = 0; index < size(breakpoints) - 1; index += 1)
    {
        if (breakpoints[index + 1] - breakpoints[index] < 10 * tTolerance)
        {
            continue;
        }
        const midpoint = 0.5 * (breakpoints[index] + breakpoints[index + 1]);
        var hasPositive = false;
        var hasNegative = false;
        for (var normal in coneNormals)
        {
            const value = evaluateContactFunctionAtPoint(strippedMotion, normal, point, midpoint);
            if (value > 0)
            {
                hasPositive = true;
            }
            if (value < 0)
            {
                hasNegative = true;
            }
        }
        if (!(hasPositive && hasNegative))
        {
            continue;
        }
        if (size(intervals) > 0 && abs(intervals[size(intervals) - 1].tEnd - breakpoints[index]) < 10 * tTolerance)
        {
            var merged = intervals[size(intervals) - 1];
            merged.tEnd = breakpoints[index + 1];
            intervals[size(intervals) - 1] = merged;
        }
        else
        {
            intervals = append(intervals, { "tStart" : breakpoints[index], "tEnd" : breakpoints[index + 1] });
        }
    }
    return intervals;
}

/**
 * March the zero set of the strip function g(s, t) across a co-edge side's SHARED sample
 * arrays (spec 6.2/6.3 step 2): per sample column, every t root is found by brackets plus
 * safeguarded Newton; roots in neighboring columns chain into branches by linear prediction.
 * The result lives at exactly the shared s samples, which is what makes seam stitching exact
 * downstream.
 *
 * normals and points are extraction's sideNormals / edgePoints arrays for one side.
 * Returns an array of branches { startColumn, endColumn, samples : [{ sampleIndex, t, value }] }.
 */
export function marchStripZeroCurves(strippedMotion is map, normals is array, points is array,
    tStart is number, tEnd is number, stationCount is number, tTolerance is number) returns array
{
    const columnCount = size(normals);
    const stationSpacing = (tEnd - tStart) / (stationCount - 1);
    var rootsPerColumn = makeArray(columnCount);
    var claimedPerColumn = makeArray(columnCount);
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        rootsPerColumn[columnIndex] = findContactFunctionRoots(strippedMotion, normals[columnIndex],
            points[columnIndex], tStart, tEnd, stationCount, tTolerance);
        claimedPerColumn[columnIndex] = makeArray(size(rootsPerColumn[columnIndex]), false);
    }

    var branches = [];
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        for (var rootIndex = 0; rootIndex < size(rootsPerColumn[columnIndex]); rootIndex += 1)
        {
            if (claimedPerColumn[columnIndex][rootIndex])
            {
                continue;
            }
            claimedPerColumn[columnIndex][rootIndex] = true;
            var samples = [{
                        "sampleIndex" : columnIndex,
                        "t" : rootsPerColumn[columnIndex][rootIndex].t,
                        "value" : rootsPerColumn[columnIndex][rootIndex].value
                    }];
            var previousT = undefined;
            var currentT = rootsPerColumn[columnIndex][rootIndex].t;
            for (var nextColumn = columnIndex + 1; nextColumn < columnCount; nextColumn += 1)
            {
                // First link: nearest root within a generous window (the local slope is still
                // unknown). Later links: linear prediction with a slope-aware window.
                const predicted = previousT == undefined ? currentT : 2 * currentT - previousT;
                const window = previousT == undefined ? 4 * stationSpacing :
                    2 * stationSpacing + 2 * abs(currentT - previousT);
                var bestIndex = undefined;
                var bestDistance = window;
                for (var candidateIndex = 0; candidateIndex < size(rootsPerColumn[nextColumn]); candidateIndex += 1)
                {
                    if (claimedPerColumn[nextColumn][candidateIndex])
                    {
                        continue;
                    }
                    const distance = abs(rootsPerColumn[nextColumn][candidateIndex].t - predicted);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = candidateIndex;
                    }
                }
                if (bestIndex == undefined)
                {
                    break;
                }
                claimedPerColumn[nextColumn][bestIndex] = true;
                previousT = currentT;
                currentT = rootsPerColumn[nextColumn][bestIndex].t;
                samples = append(samples, {
                            "sampleIndex" : nextColumn,
                            "t" : currentT,
                            "value" : rootsPerColumn[nextColumn][bestIndex].value
                        });
            }
            branches = append(branches, {
                        "startColumn" : samples[0].sampleIndex,
                        "endColumn" : samples[size(samples) - 1].sampleIndex,
                        "samples" : samples
                    });
        }
    }
    return branches;
}

/**
 * The pointwise envelope function and its full gradient at one (u, v, t) - the polish and
 * marching evaluator (order-2 surface derivatives; rational-correct through the
 * splineRefinementUtils evaluators).
 *
 * `tDerivativeScale` and `valueScale` are the Cauchy-Schwarz bounds on |f_t| and |f| built from
 * the same four vectors, |A'N||v| + |AN||a| and |AN||v|. They are what a caller needs in order
 * to tell a genuinely small f_t from one that is small only because every term feeding it is: a
 * constant-velocity translation has f_t identically zero AND its scale zero, so the ratio of
 * the two says nothing and only `valueScale` - the size of f itself - is left to measure
 * against. That is a STATIONARY section rather than a tangency (spec 6.4, detector 2). Both are
 * free here; the vectors already exist.
 *
 * Returns { value, uDerivative, vDerivative, tDerivative, tDerivativeScale, valueScale }.
 */
export function evaluateEnvelopeGradientPointwise(strippedMotion is map, strippedSurface is map,
    u is number, v is number, t is number) returns map
{
    return envelopeGradientAt(strippedMotion, strippedSurface, u, v, t, true);
}

/**
 * The SECTION gradient at one (u, v, t): { value, uDerivative, vDerivative }, and nothing else.
 *
 * This is what marching a section, correcting onto one, resampling one and polishing an anchor
 * all read - checked, not assumed: not one of them touches `tDerivative`, `tDerivativeScale` or
 * `valueScale`, and between them they are essentially every gradient evaluation a build makes.
 * The three fields they skip cost two matrix-vector products, two dot products and FOUR square
 * roots per call, which on the tube path is work whose result is discarded every time.
 *
 * The two time terms are not cheaper here, they are absent. A caller that needs f_t or the
 * Cauchy-Schwarz scales - the tangency audit, its refinement, the branch-time search - calls
 * `evaluateEnvelopeGradientPointwise` and gets all six fields, computed exactly as before.
 */
export function evaluateSectionGradientPointwise(strippedMotion is map, strippedSurface is map,
    u is number, v is number, t is number) returns map
{
    return envelopeGradientAt(strippedMotion, strippedSurface, u, v, t, false);
}

/** Both of the above; `wantTimeTerms` decides whether the f_t half is computed at all. */
function envelopeGradientAt(strippedMotion is map, strippedSurface is map, u is number, v is number,
    t is number, wantTimeTerms is boolean) returns map
{
    const derivatives = leanSurfaceDerivatives(strippedSurface, u, v, 2);
    const surfacePoint = derivatives[0];
    const uTangent = derivatives[1];
    const vTangent = derivatives[2];
    const normals = leanNormalAndDerivatives(derivatives);
    const normal = normals[0];
    const uNormalDerivative = normals[1];
    const vNormalDerivative = normals[2];
    const sample = evaluateMotionSample(strippedMotion, t);
    const rotation = sample.rotation;
    const rotationDerivative = sample.rotationDerivative;
    const velocity = leanVelocity(sample, surfacePoint);
    const transportedNormal = applyRowsToTriple(rotation, normal[0], normal[1], normal[2]);
    const value = dotTriples(transportedNormal, velocity);
    const uDerivative = dotTriples(applyRowsToTriple(rotation, uNormalDerivative[0],
                uNormalDerivative[1], uNormalDerivative[2]), velocity) +
        dotTriples(transportedNormal, applyRowsToTriple(rotationDerivative,
                uTangent[0], uTangent[1], uTangent[2]));
    const vDerivative = dotTriples(applyRowsToTriple(rotation, vNormalDerivative[0],
                vNormalDerivative[1], vNormalDerivative[2]), velocity) +
        dotTriples(transportedNormal, applyRowsToTriple(rotationDerivative,
                vTangent[0], vTangent[1], vTangent[2]));
    if (!wantTimeTerms)
    {
        return { "value" : value, "uDerivative" : uDerivative, "vDerivative" : vDerivative };
    }
    const acceleration = leanAcceleration(sample, surfacePoint);
    const turnedNormal = applyRowsToTriple(rotationDerivative, normal[0], normal[1], normal[2]);
    const normalMagnitude = normTriple(transportedNormal);
    const velocityMagnitude = normTriple(velocity);
    return {
            "value" : value,
            "uDerivative" : uDerivative,
            "vDerivative" : vDerivative,
            "tDerivative" : dotTriples(turnedNormal, velocity) + dotTriples(transportedNormal, acceleration),
            "tDerivativeScale" : normTriple(turnedNormal) * velocityMagnitude +
                normalMagnitude * normTriple(acceleration),
            "valueScale" : normalMagnitude * velocityMagnitude
        };
}

/**
 * March the section p-curve f(., ., tGlobal) = 0 in the uv domain from startUv toward endUv
 * (spec 6.3 step 4): predictor perpendicular to the gradient, corrector along it (1-3 Newton
 * steps per station). Marching stops when the end anchor is within one step, when the
 * gradient degenerates, or when the step budget runs out.
 *
 * startUv / endUv are [u, v] arrays in the surface's knot domain. options: { stepSize (uv
 * units), maxSteps (default 400), tolerance (residual, default 1e-10) }.
 * Returns { uvPoints {array of [u, v]}, reachedEnd {boolean} }.
 */
export function marchSectionCurve(strippedMotion is map, strippedSurface is map, tGlobal is number,
    startUv is array, endUv is array, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    const stepSize = options.stepSize;
    const maxSteps = options.maxSteps == undefined ? 400 : options.maxSteps;
    const tolerance = options.tolerance == undefined ? 1e-10 : options.tolerance;
    const domain = knotDomainOfStrippedSurface(strippedSurface);

    const seeded = correctedSectionUvAndGradient(stationMotion, strippedSurface, tGlobal, startUv,
        domain, tolerance);
    var uv = seeded.uv;
    var carriedGradient = seeded.gradient;
    // Preallocated for the same reason the wrapping march is: `append` in a loop copies the
    // whole polyline every step, which turns a few hundred marched points into tens of
    // thousands of element copies.
    var uvPoints = makeArray(maxSteps + 2, uv);
    var pointCount = 1;
    var previousTangent = undefined;
    var reachedEnd = false;
    for (var step = 0; step < maxSteps; step += 1)
    {
        const gradient = carriedGradient != undefined ? carriedGradient :
            evaluateSectionGradientPointwise(stationMotion, strippedSurface, uv[0], uv[1], tGlobal);
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            break;
        }
        const gradientNorm = sqrt(gradientNormSquared);
        var tangent = [-gradient.vDerivative / gradientNorm, gradient.uDerivative / gradientNorm];
        if (previousTangent == undefined)
        {
            if (tangent[0] * (endUv[0] - uv[0]) + tangent[1] * (endUv[1] - uv[1]) < 0)
            {
                tangent = [-tangent[0], -tangent[1]];
            }
        }
        else if (tangent[0] * previousTangent[0] + tangent[1] * previousTangent[1] < 0)
        {
            tangent = [-tangent[0], -tangent[1]];
        }
        var predicted = [uv[0] + stepSize * tangent[0], uv[1] + stepSize * tangent[1]];
        predicted = [clampToRange(predicted[0], domain.uMin, domain.uMax),
            clampToRange(predicted[1], domain.vMin, domain.vMax)];
        const correction = correctedSectionUvAndGradient(stationMotion, strippedSurface, tGlobal,
            predicted, domain, tolerance);
        const corrected = correction.uv;
        carriedGradient = correction.gradient;
        uvPoints[pointCount] = corrected;
        pointCount += 1;
        previousTangent = tangent;
        const remainingSquared = (corrected[0] - endUv[0]) ^ 2 + (corrected[1] - endUv[1]) ^ 2;
        if (remainingSquared <= stepSize ^ 2)
        {
            uvPoints[pointCount] = correctOntoSection(stationMotion, strippedSurface, tGlobal, endUv, domain, tolerance);
            pointCount += 1;
            reachedEnd = true;
            break;
        }
        uv = corrected;
    }
    return { "uvPoints" : subArray(uvPoints, 0, pointCount), "reachedEnd" : reachedEnd };
}

/**
 * Resample a marched section polyline at fixed fractions of its LIFTED (3D) arc length,
 * re-Newton every resampled point onto f = 0, and lift it rigidly (Phi = A S + b). The fixed
 * fractions q are what make sections from different stations line up into the (q, t) fit grid.
 * Returns { uvSamples, liftedSamples, worstResidual }.
 */
export function resampleAndPolishSection(strippedMotion is map, strippedSurface is map, tGlobal is number,
    uvPoints is array, sampleCount is number, tolerance is number) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    const pointCount = size(uvPoints);
    var liftedPolyline = makeArray(pointCount);
    for (var index = 0; index < pointCount; index += 1)
    {
        liftedPolyline[index] = liftContactPoint(stationMotion, strippedSurface,
            uvPoints[index][0], uvPoints[index][1], tGlobal);
    }
    var cumulativeLengths = makeArray(pointCount, 0);
    for (var index = 1; index < pointCount; index += 1)
    {
        cumulativeLengths[index] = cumulativeLengths[index - 1] +
            distanceBetweenTriples(liftedPolyline[index], liftedPolyline[index - 1]);
    }
    const totalLength = cumulativeLengths[pointCount - 1];
    const domain = knotDomainOfStrippedSurface(strippedSurface);

    var uvSamples = makeArray(sampleCount);
    var liftedSamples = makeArray(sampleCount);
    var worstResidual = 0;
    var cursor = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        const targetLength = totalLength * sampleIndex / (sampleCount - 1);
        while (cursor < pointCount - 2 && cumulativeLengths[cursor + 1] < targetLength)
        {
            cursor += 1;
        }
        const segmentLength = cumulativeLengths[cursor + 1] - cumulativeLengths[cursor];
        const fraction = segmentLength < 1e-300 ? 0 : (targetLength - cumulativeLengths[cursor]) / segmentLength;
        var uv = [uvPoints[cursor][0] + fraction * (uvPoints[cursor + 1][0] - uvPoints[cursor][0]),
            uvPoints[cursor][1] + fraction * (uvPoints[cursor + 1][1] - uvPoints[cursor][1])];
        const correction = correctedSectionUvAndGradient(stationMotion, strippedSurface, tGlobal,
            uv, domain, tolerance);
        uv = correction.uv;
        const residual = abs(correction.gradient != undefined ? correction.gradient.value :
                evaluateEnvelopePointwise(stationMotion, strippedSurface, uv[0], uv[1], tGlobal));
        worstResidual = max(worstResidual, residual);
        uvSamples[sampleIndex] = uv;
        liftedSamples[sampleIndex] = liftContactPoint(stationMotion, strippedSurface, uv[0], uv[1], tGlobal);
    }
    return { "uvSamples" : uvSamples, "liftedSamples" : liftedSamples, "worstResidual" : worstResidual };
}

/** The rigid lift of one contact point: Phi(u, v, t) = A(t) S(u, v) + b(t). */
export function liftContactPoint(strippedMotion is map, strippedSurface is map, u is number, v is number,
    t is number) returns Vector
{
    const sample = evaluateMotionSample(strippedMotion, t);
    return leanLift(sample, leanSurfacePoint(strippedSurface, u, v)) as Vector;
}

/** Bisection-safeguarded Newton on the contact function inside a sign-change bracket. */
function refineContactRoot(strippedMotion is map, normal is Vector, point is Vector,
    bracketLow is number, bracketHigh is number, valueLow is number, valueHigh is number,
    tTolerance is number) returns map
{
    var low = bracketLow;
    var high = bracketHigh;
    var lowValue = valueLow;
    var t = 0.5 * (low + high);
    var value = 0;
    for (var iteration = 0; iteration < 80; iteration += 1)
    {
        value = evaluateContactFunctionAtPoint(strippedMotion, normal, point, t);
        if (value == 0)
        {
            break;
        }
        if (value * lowValue > 0)
        {
            low = t;
            lowValue = value;
        }
        else
        {
            high = t;
        }
        const derivative = evaluateContactFunctionTimeDerivative(strippedMotion, normal, point, t);
        var next = derivative == 0 ? undefined : t - value / derivative;
        if (next == undefined || next <= low || next >= high)
        {
            next = 0.5 * (low + high);
        }
        if (abs(next - t) < tTolerance)
        {
            t = next;
            value = evaluateContactFunctionAtPoint(strippedMotion, normal, point, t);
            break;
        }
        t = next;
    }
    return { "t" : t, "value" : value };
}

/**
 * Newton corrector onto f(., ., tGlobal) = 0 along the uv gradient, clamped to the domain.
 * This overload reads the domain off the surface, for callers outside this module that hold a
 * seed and need it on the section - spec 6.4's tangency refinement iterates through it.
 */
export function correctPointOntoSection(strippedMotion is map, strippedSurface is map,
    tGlobal is number, seedUv is array, tolerance is number) returns array
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    return correctOntoSection(stationMotion, strippedSurface, tGlobal, seedUv,
        knotDomainOfStrippedSurface(strippedSurface), tolerance);
}

/** As above, for callers inside this module that already hold the domain. */
function correctOntoSection(stationMotion is map, strippedSurface is map, tGlobal is number,
    seedUv is array, domain is map, tolerance is number) returns array
{
    return correctedSectionUvAndGradient(stationMotion, strippedSurface, tGlobal, seedUv, domain,
        tolerance).uv;
}

/**
 * The same corrector, handing back the gradient it finished on: { uv, gradient }.
 *
 * The gradient belongs to the RETURNED uv, so a caller about to evaluate there anyway - the
 * section march, which wants the tangent at every corrected point, and the resample, which wants
 * the residual at every sample - gets it without a second evaluation. `gradient` is undefined
 * only when the loop ran out of iterations instead of converging, because then the last gradient
 * belongs to the point before the final step.
 *
 * Both correctors this module used to carry were this function written out twice, once against
 * each of the two knot-domain helpers - which produce the same four fields. There is now one.
 */
function correctedSectionUvAndGradient(stationMotion is map, strippedSurface is map, tGlobal is number,
    seedUv is array, domain is map, tolerance is number) returns map
{
    var uv = seedUv;
    for (var iteration = 0; iteration < 8; iteration += 1)
    {
        const gradient = evaluateSectionGradientPointwise(stationMotion, strippedSurface, uv[0], uv[1], tGlobal);
        if (abs(gradient.value) <= tolerance)
        {
            return { "uv" : uv, "gradient" : gradient };
        }
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            return { "uv" : uv, "gradient" : gradient };
        }
        uv = [clampToRange(uv[0] - gradient.value * gradient.uDerivative / gradientNormSquared, domain.uMin, domain.uMax),
            clampToRange(uv[1] - gradient.value * gradient.vDerivative / gradientNormSquared, domain.vMin, domain.vMax)];
    }
    return { "uv" : uv, "gradient" : undefined };
}

/** The knot-domain rectangle of a stripped surface. */
function knotDomainOfStrippedSurface(strippedSurface is map) returns map
{
    return {
            "uMin" : strippedSurface.uKnots[strippedSurface.uDegree],
            "uMax" : strippedSurface.uKnots[size(strippedSurface.uKnots) - strippedSurface.uDegree - 1],
            "vMin" : strippedSurface.vKnots[strippedSurface.vDegree],
            "vMax" : strippedSurface.vKnots[size(strippedSurface.vKnots) - strippedSurface.vDegree - 1]
        };
}

/** Clamp a value to [low, high]. */
export function clampToRange(value is number, low is number, high is number) returns number
{
    return value < low ? low : (value > high ? high : value);
}

/** Solve a 3x3 linear system by Cramer's rule; undefined when the determinant degenerates. */
function solveThreeByThree(rows is array, rightHandSide is array)
{
    const determinant =
        rows[0][0] * (rows[1][1] * rows[2][2] - rows[1][2] * rows[2][1]) -
        rows[0][1] * (rows[1][0] * rows[2][2] - rows[1][2] * rows[2][0]) +
        rows[0][2] * (rows[1][0] * rows[2][1] - rows[1][1] * rows[2][0]);
    if (abs(determinant) < 1e-30)
    {
        return undefined;
    }
    var solution = makeArray(3, 0);
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        var modified = [
                [rows[0][0], rows[0][1], rows[0][2]],
                [rows[1][0], rows[1][1], rows[1][2]],
                [rows[2][0], rows[2][1], rows[2][2]]
            ];
        for (var rowIndex = 0; rowIndex < 3; rowIndex += 1)
        {
            modified[rowIndex][columnIndex] = rightHandSide[rowIndex];
        }
        solution[columnIndex] =
            (modified[0][0] * (modified[1][1] * modified[2][2] - modified[1][2] * modified[2][1]) -
                    modified[0][1] * (modified[1][0] * modified[2][2] - modified[1][2] * modified[2][0]) +
                    modified[0][2] * (modified[1][0] * modified[2][1] - modified[1][1] * modified[2][0])) / determinant;
    }
    return solution;
}

/** Clamp a value to [0, 1]. */
function clampToUnit(value is number) returns number
{
    return value < 0 ? 0 : (value > 1 ? 1 : value);
}

/** Clamp a value's magnitude. */
function clampMagnitude(value is number, limit is number) returns number
{
    return value > limit ? limit : (value < -limit ? -limit : value);
}


// ============================= Orientation and the fold certificate (spec 6.6) =============================

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

    const derivatives = leanSurfaceDerivatives(strippedSurface, u, v, 2);
    const surfacePoint = derivatives[0];
    const uTangent = derivatives[1];
    const vTangent = derivatives[2];
    const normals = leanNormalAndDerivatives(derivatives);
    const normal = normals[0];
    const uNormalDerivative = normals[1];
    const vNormalDerivative = normals[2];

    const sample = evaluateMotionSample(strippedMotion, t);
    const rotation = sample.rotation;
    const rotationDerivative = sample.rotationDerivative;
    const velocity = leanVelocity(sample, surfacePoint);
    const acceleration = leanAcceleration(sample, surfacePoint);
    const transportedNormal = applyRowsToTriple(rotation, normal[0], normal[1], normal[2]);
    const value = dotTriples(transportedNormal, velocity);
    const uDerivative = dotTriples(applyRowsToTriple(rotation, uNormalDerivative[0],
                uNormalDerivative[1], uNormalDerivative[2]), velocity) +
        dotTriples(transportedNormal, applyRowsToTriple(rotationDerivative,
                uTangent[0], uTangent[1], uTangent[2]));
    const vDerivative = dotTriples(applyRowsToTriple(rotation, vNormalDerivative[0],
                vNormalDerivative[1], vNormalDerivative[2]), velocity) +
        dotTriples(transportedNormal, applyRowsToTriple(rotationDerivative,
                vTangent[0], vTangent[1], vTangent[2]));
    const tDerivative = dotTriples(applyRowsToTriple(rotationDerivative, normal[0], normal[1], normal[2]), velocity) +
        dotTriples(transportedNormal, acceleration);

    // The velocity's coordinates in the TRANSPORTED tangent basis. World space on purpose:
    // pulling back into the tool frame would need A's inverse, and the motion module's A is
    // orthonormal only to its certified drift - the Gram solve needs no such assumption.
    const worldUTangent = applyRowsToTriple(rotation, uTangent[0], uTangent[1], uTangent[2]) as Vector;
    const worldVTangent = applyRowsToTriple(rotation, vTangent[0], vTangent[1], vTangent[2]) as Vector;
    const coordinates = solveTangentCoordinates(worldUTangent, worldVTangent, velocity as Vector);
    const lambda = coordinates.degenerate ? 0 :
        tDerivative - coordinates.alpha * uDerivative - coordinates.beta * vDerivative;

    const normalMagnitude = normTriple(transportedNormal);
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
            // Everything the module publishes leaves as a Vector; the triples above are the
            // lean evaluator's internal currency and stop here.
            "surfacePoint" : surfacePoint as Vector,
            "uTangent" : uTangent as Vector,
            "vTangent" : vTangent as Vector,
            "parametricNormal" : normal as Vector,
            "transportedNormal" : transportedNormal as Vector,
            "outwardNormal" : normalMagnitude < 1e-300 ? vector(0, 0, 0) :
                ((1 / normalMagnitude) * (transportedNormal as Vector)),
            "normalMagnitude" : normalMagnitude,
            "velocity" : velocity as Vector,
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
        // A grid row IS a station, so the motion is sampled per row rather than per q sample.
        const stationMotion = evaluateMotionSample(strippedMotion, stations[rowIndex]);
        for (var columnIndex = 0; columnIndex < size(row); columnIndex += 1)
        {
            const qDirection = gridQDirection(row, columnIndex);
            if (qDirection == undefined)
            {
                continue;
            }
            const orientation = fitPatchOrientationAt(stationMotion, strippedSurface,
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

/**
 * Orient every envelope co-edge that one input co-edge generates, and certify the alternation
 * that makes it cheap (spec 6.6; framework paper 5.3, Proposition 14).
 *
 * `normals` and `points` are extraction's sideNormals / edgePoints arrays for ONE side of the
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
 * side. Extraction decides "left" by the kernel's own `usingFaceOrientation` tangent: walking
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
    throw "solidSweepUtils orientation: co-edge side must be \"left\" or \"right\", got \"" ~ side ~ "\".";
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
        throw "solidSweepUtils orientation: cannot reverse a v-periodic fit net in place - reverse the fit " ~
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


// ============================= Degeneracy detectors (spec 6.9) =============================

/**
 * Audit one marched section p-curve for spec 6.4's SWEEP_FUNNEL_TANGENT_TO_SLICE.
 *
 * `uvPoints` is marchSectionCurve's output for station `tGlobal` - [u, v] arrays in the
 * surface's knot domain, in march order. Nothing is re-marched here: the audit evaluates f_t at
 * the points the march already produced, then refines only inside the brackets it finds.
 *
 * options: {
 *     relativeTolerance : the stationary-section test - |f_t| below this fraction of EITHER
 *         reference everywhere (default SECTION_TIME_DERIVATIVE_RELATIVE_FLOOR),
 *     timeSpan : the motion parameter's own span, for the |f| / span reference (default 1,
 *         which is what the module's normalization gives),
 *     tangencyTolerance : absolute |f_t| counting as zero AT A SAMPLE (default 0, so a tangency
 *         has to show as a sign change between samples),
 *     nearTangencyTolerance : relative |f_t| / scale under which an interior minimum with no
 *         sign change is reported as a near tangency (default 1e-3),
 *     sectionTolerance : the corrector's |f| residual target while refining (default 1e-10),
 *     maxRefineIterations : default 60
 * }
 *
 * Returns {
 *     detected {boolean} : one or more isolated tangencies - the section MUST be split,
 *     stationarySection {boolean} : f_t is at its own noise floor along the whole section,
 *     tangencies {array} : { segmentIndex, fraction, uv, timeDerivative, timeDerivativeScale,
 *         sectionResidual, atSample } in march order,
 *     nearTangencies {array} : { sampleIndex, uv, timeDerivative, relativeMagnitude },
 *     sampleTimeDerivatives {array}, sampleSigns {array}, sampleScales {array},
 *     minimumMagnitude {number}, minimumRelative {number}, minimumSampleIndex {number},
 *     scaleReference {number} : the largest tDerivativeScale on the section,
 *     valueScaleReference {number} : the largest valueScale on the section,
 *     stationaryFloor {number} : the |f_t| the two references together put the noise floor at,
 *     worstSectionResidual {number} : the largest |f| at the marched points - a read on the
 *         input polyline rather than on this audit
 * }
 */
export function auditSectionTangency(strippedMotion is map, strippedSurface is map,
    tGlobal is number, uvPoints is array, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    const relativeTolerance = options.relativeTolerance == undefined ?
        SECTION_TIME_DERIVATIVE_RELATIVE_FLOOR : options.relativeTolerance;
    const tangencyTolerance = options.tangencyTolerance == undefined ? 0 : options.tangencyTolerance;
    const nearTangencyTolerance = options.nearTangencyTolerance == undefined ? 1e-3 :
        options.nearTangencyTolerance;
    const sectionTolerance = options.sectionTolerance == undefined ? 1e-10 : options.sectionTolerance;
    const maxRefineIterations = options.maxRefineIterations == undefined ? 60 : options.maxRefineIterations;
    const timeSpan = options.timeSpan == undefined ? 1 : options.timeSpan;

    const pointCount = size(uvPoints);
    var derivatives = makeArray(pointCount, 0);
    var scales = makeArray(pointCount, 0);
    var signs = makeArray(pointCount, 0);
    var scaleReference = 0;
    var valueScaleReference = 0;
    var worstSectionResidual = 0;
    var largestMagnitude = 0;
    var minimumMagnitude = undefined;
    var minimumSampleIndex = 0;
    for (var index = 0; index < pointCount; index += 1)
    {
        const gradient = evaluateEnvelopeGradientPointwise(stationMotion, strippedSurface,
            uvPoints[index][0], uvPoints[index][1], tGlobal);
        derivatives[index] = gradient.tDerivative;
        scales[index] = gradient.tDerivativeScale;
        scaleReference = max(scaleReference, gradient.tDerivativeScale);
        valueScaleReference = max(valueScaleReference, gradient.valueScale);
        worstSectionResidual = max(worstSectionResidual, abs(gradient.value));
        const magnitude = abs(gradient.tDerivative);
        largestMagnitude = max(largestMagnitude, magnitude);
        if (minimumMagnitude == undefined || magnitude < minimumMagnitude)
        {
            minimumMagnitude = magnitude;
            minimumSampleIndex = index;
        }
    }
    if (minimumMagnitude == undefined)
    {
        minimumMagnitude = 0;
    }
    const minimumRelative = scaleReference <= 0 ? 0 : minimumMagnitude / scaleReference;

    // The stationary test comes before any sign is read: a section whose f_t never rises above
    // its own terms' noise floor has no tangency to find, and every sign on it would be noise.
    // Whichever reference is larger decides, so the constant-velocity case - where f_t's own
    // bound collapses to zero along with f_t - is carried by the size of f instead.
    const stationaryFloor = max(tangencyTolerance,
        relativeTolerance * max(scaleReference,
            timeSpan <= 0 ? 0 : valueScaleReference / timeSpan));
    const stationarySection = largestMagnitude <= stationaryFloor;
    if (stationarySection)
    {
        return {
                "detected" : false,
                "stationarySection" : true,
                "tangencies" : [],
                "nearTangencies" : [],
                "sampleTimeDerivatives" : derivatives,
                "sampleSigns" : signs,
                "sampleScales" : scales,
                "minimumMagnitude" : minimumMagnitude,
                "minimumRelative" : minimumRelative,
                "minimumSampleIndex" : minimumSampleIndex,
                "scaleReference" : scaleReference,
                "valueScaleReference" : valueScaleReference,
                "stationaryFloor" : stationaryFloor,
                "worstSectionResidual" : worstSectionResidual
            };
    }

    for (var index = 0; index < pointCount; index += 1)
    {
        signs[index] = signWithDeadband(derivatives[index], tangencyTolerance);
    }

    var tangencies = [];
    for (var index = 0; index < pointCount; index += 1)
    {
        // A zero that landed on a sample is recorded at the sample itself rather than
        // bracketed, and the brackets on either side of it are then not sign changes at all.
        if (signs[index] == 0)
        {
            tangencies = append(tangencies, {
                        "segmentIndex" : index == 0 ? 0 : index - 1,
                        "fraction" : index == 0 ? 0 : 1,
                        "uv" : uvPoints[index],
                        "timeDerivative" : derivatives[index],
                        "timeDerivativeScale" : scales[index],
                        "sectionResidual" : abs(evaluateEnvelopePointwise(stationMotion,
                                strippedSurface, uvPoints[index][0], uvPoints[index][1], tGlobal)),
                        "atSample" : index
                    });
            continue;
        }
        if (index + 1 >= pointCount || signs[index + 1] == 0 || signs[index + 1] == signs[index])
        {
            continue;
        }
        tangencies = append(tangencies, refineSectionTangency(stationMotion, strippedSurface,
                tGlobal, uvPoints[index], uvPoints[index + 1], derivatives[index],
                derivatives[index + 1], index, sectionTolerance, maxRefineIterations));
    }

    // Near tangencies: interior dips of |f_t| that never cross. Reported only where no tangency
    // was already located in the same neighbourhood, so one event is never counted twice.
    var nearTangencies = [];
    for (var index = 1; index + 1 < pointCount; index += 1)
    {
        const magnitude = abs(derivatives[index]);
        const previousMagnitude = abs(derivatives[index - 1]);
        const nextMagnitude = abs(derivatives[index + 1]);
        // A dip, so at least one side has to be STRICTLY larger: a march that ends by appending
        // its end anchor twice leaves two equal samples, and a non-strict test would read that
        // repeated pair as an interior minimum of a curve that has none there.
        if (magnitude > previousMagnitude || magnitude > nextMagnitude ||
            (magnitude == previousMagnitude && magnitude == nextMagnitude))
        {
            continue;
        }
        const relativeMagnitude = scaleReference <= 0 ? 0 : magnitude / scaleReference;
        if (relativeMagnitude > nearTangencyTolerance)
        {
            continue;
        }
        if (tangencyOnSegment(tangencies, index - 1) || tangencyOnSegment(tangencies, index))
        {
            continue;
        }
        nearTangencies = append(nearTangencies, {
                    "sampleIndex" : index,
                    "uv" : uvPoints[index],
                    "timeDerivative" : derivatives[index],
                    "relativeMagnitude" : relativeMagnitude
                });
    }

    return {
            "detected" : size(tangencies) > 0,
            "stationarySection" : false,
            "tangencies" : tangencies,
            "nearTangencies" : nearTangencies,
            "sampleTimeDerivatives" : derivatives,
            "sampleSigns" : signs,
            "sampleScales" : scales,
            "minimumMagnitude" : minimumMagnitude,
            "minimumRelative" : minimumRelative,
            "minimumSampleIndex" : minimumSampleIndex,
            "scaleReference" : scaleReference,
            "valueScaleReference" : valueScaleReference,
            "stationaryFloor" : stationaryFloor,
            "worstSectionResidual" : worstSectionResidual
        };
}

export function auditSectionTangency(strippedMotion is map, strippedSurface is map,
    tGlobal is number, uvPoints is array) returns map
{
    return auditSectionTangency(strippedMotion, strippedSurface, tGlobal, uvPoints, {});
}

/**
 * Split a marched section at its tangencies (spec 6.4: "section extraction splits at the
 * tangency"), so that no piece handed to arc-length resampling spans a point where the funnel
 * is tangent to the slice.
 *
 * Adjacent pieces SHARE the split point as the same value - the tangency's own uv, appended as
 * the last element of the piece before it and the first of the piece after it - which is what
 * spec 2.3 asks for: the seam is one number, not two that agree to tolerance.
 *
 * A piece that comes out with fewer than two distinct points (a tangency at the very start or
 * end of the march, or two tangencies inside one step) is dropped and counted, never emitted as
 * a degenerate section.
 *
 * Returns { pieces {array of uv polylines}, splitCount, droppedPieces }.
 */
export function splitSectionAtTangencies(uvPoints is array, tangencyAudit is map) returns map
{
    const tangencies = tangencyAudit.tangencies;
    if (size(tangencies) == 0)
    {
        return { "pieces" : [uvPoints], "splitCount" : 0, "droppedPieces" : 0 };
    }
    const ordered = sort(tangencies, function(first, second)
        {
            return first.segmentIndex == second.segmentIndex ?
                first.fraction - second.fraction : first.segmentIndex - second.segmentIndex;
        });

    var pieces = [];
    var droppedPieces = 0;
    var current = [uvPoints[0]];
    var pointIndex = 1;
    for (var tangencyIndex = 0; tangencyIndex < size(ordered); tangencyIndex += 1)
    {
        const tangency = ordered[tangencyIndex];
        while (pointIndex <= tangency.segmentIndex && pointIndex < size(uvPoints))
        {
            current = appendUnlessDuplicate(current, uvPoints[pointIndex]);
            pointIndex += 1;
        }
        current = appendUnlessDuplicate(current, tangency.uv);
        if (size(current) >= 2)
        {
            pieces = append(pieces, current);
        }
        else
        {
            droppedPieces += 1;
        }
        current = [tangency.uv];
    }
    while (pointIndex < size(uvPoints))
    {
        current = appendUnlessDuplicate(current, uvPoints[pointIndex]);
        pointIndex += 1;
    }
    if (size(current) >= 2)
    {
        pieces = append(pieces, current);
    }
    else
    {
        droppedPieces += 1;
    }
    return { "pieces" : pieces, "splitCount" : size(ordered), "droppedPieces" : droppedPieces };
}

/**
 * Arc-length resample each piece of a split section independently, through the funnel solver's
 * own resampleAndPolishSection - so the q fractions run 0..1 WITHIN a piece. That is the point
 * of splitting: q fractions measured across a tangency would slide along the section from
 * station to station, which is what the fixed-fraction convention exists to prevent.
 *
 * Returns { sections {array of resampleAndPolishSection results}, worstResidual }.
 */
export function resampleSectionPieces(strippedMotion is map, strippedSurface is map,
    tGlobal is number, pieces is array, sampleCount is number, tolerance is number) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    var sections = makeArray(size(pieces));
    var worstResidual = 0;
    for (var pieceIndex = 0; pieceIndex < size(pieces); pieceIndex += 1)
    {
        const resampled = resampleAndPolishSection(stationMotion, strippedSurface, tGlobal,
                pieces[pieceIndex], sampleCount, tolerance);
        sections[pieceIndex] = resampled;
        worstResidual = max(worstResidual, resampled.worstResidual);
    }
    return { "sections" : sections, "worstResidual" : worstResidual };
}

/**
 * How far one point of a sharp edge is from spec 6.4's SWEEP_EDGE_SWEEP_SINGULARITY at one
 * station: the NORMALIZED sine between the transported edge tangent A e' and the point's
 * velocity A' e + b'. The sharp edge's envelope sheet is Phi(s, t) = A e(s) + b, whose
 * parametric normal is (A e') x velocity, so this sine vanishing is precisely that sheet losing
 * its normal - the same quantity the orientation pass's orientSharpEdgeFace reports degenerate on,
 * measured here instead of only refused.
 *
 * `edgeTangent` is e'(s) in the tool frame, any positive multiple (extraction's edgeTangents
 * are unit). Returns { sine, cosine, speed, tangentNorm, degenerate } - degenerate meaning a
 * vanishing velocity or tangent, where there is no angle to measure.
 */
export function edgeSweepSingularityMeasure(strippedMotion is map, edgePoint is Vector,
    edgeTangent is Vector, t is number) returns map
{
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * edgePoint + sample.translationDerivative;
    const transportedTangent = sample.rotation * edgeTangent;
    const speed = norm(velocity);
    const tangentNorm = norm(transportedTangent);
    if (speed < 1e-300 || tangentNorm < 1e-300)
    {
        return { "sine" : 1, "cosine" : 0, "speed" : speed, "tangentNorm" : tangentNorm,
                "degenerate" : true };
    }
    const scale = 1 / (speed * tangentNorm);
    return {
            "sine" : scale * norm(cross(transportedTangent, velocity)),
            "cosine" : scale * dot(transportedTangent, velocity),
            "speed" : speed,
            "tangentNorm" : tangentNorm,
            "degenerate" : false
        };
}

/**
 * Audit one co-edge for spec 6.4's SWEEP_EDGE_SWEEP_SINGULARITY over the (s, t) grid of the
 * SHARED extraction arrays - the same edgePoints / edgeTangents the strip function ran on, so
 * no geometry is re-derived and a reported sample index means the same thing here as it does in
 * every other consumer of that co-edge.
 *
 * The grid alone cannot decide: the singular set is generically made of ISOLATED POINTS in
 * (s, t), and a grid node lands on one only by accident. So the grid SCREENS - local minima of
 * the sine, plus the global minimum unconditionally - and, when the caller supplies the edge's
 * stripped B-spline through `strippedCurve`, Levenberg-damped Gauss-Newton decides each
 * candidate. Every refined sine is reported, so a candidate the refinement rules out is
 * recorded as ruled out rather than dropped.
 *
 * options: {
 *     strippedCurve {map} : the co-edge record's spline3d, in the curve's OWN parameter. That
 *         parameter and the arc-length sampleParameters are different parameterizations of the
 *         same curve, so each candidate's seed is obtained by inverting its edgePoint onto the
 *         curve rather than by reusing the sample parameter,
 *     sineTolerance : a refined sine (or, with no curve, a grid sine) at or under this is a
 *         singularity (default 1e-7),
 *     candidateTolerance : the SCREEN - a grid sine under this makes a local minimum a
 *         candidate (default 0.05). Not a verdict: raising it costs refinements, never
 *         correctness,
 *     mergeTolerance : two refined singularities closer than this in (s, t) are ONE (default
 *         1e-6, relative to each domain's span). Neighbouring grid minima straddling the same
 *         isolated point both converge onto it, and a detector that reports two where there is
 *         one would put the wrong count in the caller's rejection message,
 *     maxRefineIterations : default 40
 * }
 *
 * Returns {
 *     detected {boolean},
 *     singularities {array} : { sampleIndex, timeIndex, t, gridSine, refined, converged,
 *         curveParameter, refinedT, refinedSine, inversionResidual },
 *     candidates {array} : { sampleIndex, timeIndex, t, sine } - every screened node, whatever
 *         the refinement then said about it,
 *     ruledOut {array} : candidates whose refinement finished above sineTolerance,
 *     degenerateNodes {array} : { sampleIndex, timeIndex, t, speed, tangentNorm },
 *     minimumSine, minimumSampleIndex, minimumTimeIndex, minimumT,
 *     sineGrid {array} : [sampleIndex][timeIndex],
 *     nodeCount, refinedCount, mergedCount {number} : candidates that refined onto a
 *         singularity another candidate had already found
 * }
 */
export function auditEdgeSweepSingularity(strippedMotion is map, edgePoints is array,
    edgeTangents is array, tValues is array, options is map) returns map
{
    const strippedCurve = options.strippedCurve;
    const sineTolerance = options.sineTolerance == undefined ? 1e-7 : options.sineTolerance;
    const candidateTolerance = options.candidateTolerance == undefined ? 0.05 : options.candidateTolerance;
    const mergeTolerance = options.mergeTolerance == undefined ? 1e-6 : options.mergeTolerance;
    const maxRefineIterations = options.maxRefineIterations == undefined ? 40 : options.maxRefineIterations;
    const sampleCount = size(edgePoints);
    const stationCount = size(tValues);

    var sineGrid = makeArray(sampleCount);
    var degenerateNodes = [];
    var minimumSine = undefined;
    var minimumSampleIndex = 0;
    var minimumTimeIndex = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        var row = makeArray(stationCount, 1);
        for (var timeIndex = 0; timeIndex < stationCount; timeIndex += 1)
        {
            const measure = edgeSweepSingularityMeasure(strippedMotion, edgePoints[sampleIndex],
                    edgeTangents[sampleIndex], tValues[timeIndex]);
            row[timeIndex] = measure.sine;
            if (measure.degenerate)
            {
                degenerateNodes = append(degenerateNodes, {
                            "sampleIndex" : sampleIndex,
                            "timeIndex" : timeIndex,
                            "t" : tValues[timeIndex],
                            "speed" : measure.speed,
                            "tangentNorm" : measure.tangentNorm
                        });
                continue;
            }
            if (minimumSine == undefined || measure.sine < minimumSine)
            {
                minimumSine = measure.sine;
                minimumSampleIndex = sampleIndex;
                minimumTimeIndex = timeIndex;
            }
        }
        sineGrid[sampleIndex] = row;
    }
    if (minimumSine == undefined)
    {
        minimumSine = 1;
    }

    // Screen: axis-neighbour local minima under the screening threshold, and the global minimum
    // whatever its value - so the refinement always gets one shot at the grid's best point.
    var candidates = [];
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        for (var timeIndex = 0; timeIndex < stationCount; timeIndex += 1)
        {
            const sine = sineGrid[sampleIndex][timeIndex];
            const isGlobalMinimum = sampleIndex == minimumSampleIndex && timeIndex == minimumTimeIndex;
            if (!isGlobalMinimum &&
                (sine > candidateTolerance || !isAxisNeighbourMinimum(sineGrid, sampleIndex, timeIndex)))
            {
                continue;
            }
            candidates = append(candidates, {
                        "sampleIndex" : sampleIndex,
                        "timeIndex" : timeIndex,
                        "t" : tValues[timeIndex],
                        "sine" : sine
                    });
        }
    }

    const firstT = tValues[0];
    const lastT = tValues[stationCount - 1];
    const tLow = min(firstT, lastT);
    const tHigh = max(firstT, lastT);
    const curveSpan = strippedCurve == undefined ? 1 :
        curveParameterDomain(strippedCurve).high - curveParameterDomain(strippedCurve).low;
    var singularities = [];
    var ruledOut = [];
    var refinedCount = 0;
    var mergedCount = 0;
    for (var candidateIndex = 0; candidateIndex < size(candidates); candidateIndex += 1)
    {
        const candidate = candidates[candidateIndex];
        if (strippedCurve == undefined)
        {
            if (candidate.sine <= sineTolerance)
            {
                singularities = append(singularities, {
                            "sampleIndex" : candidate.sampleIndex,
                            "timeIndex" : candidate.timeIndex,
                            "t" : candidate.t,
                            "gridSine" : candidate.sine,
                            "refined" : false,
                            "converged" : false,
                            "curveParameter" : undefined,
                            "refinedT" : candidate.t,
                            "refinedSine" : candidate.sine,
                            "inversionResidual" : undefined
                        });
            }
            continue;
        }
        const inversion = curveParameterNearestPoint(strippedCurve, edgePoints[candidate.sampleIndex]);
        const refined = refineEdgeSweepSingularity(strippedMotion, strippedCurve,
                inversion.parameter, candidate.t,
                { "tLow" : tLow, "tHigh" : tHigh, "maxIterations" : maxRefineIterations });
        refinedCount += 1;
        const record = {
                "sampleIndex" : candidate.sampleIndex,
                "timeIndex" : candidate.timeIndex,
                "t" : candidate.t,
                "gridSine" : candidate.sine,
                "refined" : true,
                "converged" : refined.converged,
                "curveParameter" : refined.curveParameter,
                "refinedT" : refined.t,
                "refinedSine" : refined.sine,
                "inversionResidual" : inversion.residual
            };
        if (refined.sine > sineTolerance)
        {
            ruledOut = append(ruledOut, record);
            continue;
        }
        if (!singularityAlreadyFound(singularities, refined.curveParameter, refined.t,
                mergeTolerance * max(curveSpan, 1e-30), mergeTolerance * max(tHigh - tLow, 1e-30)))
        {
            singularities = append(singularities, record);
        }
        else
        {
            mergedCount += 1;
        }
    }

    return {
            "detected" : size(singularities) > 0,
            "singularities" : singularities,
            "candidates" : candidates,
            "ruledOut" : ruledOut,
            "degenerateNodes" : degenerateNodes,
            "minimumSine" : minimumSine,
            "minimumSampleIndex" : minimumSampleIndex,
            "minimumTimeIndex" : minimumTimeIndex,
            "minimumT" : tValues[minimumTimeIndex],
            "sineGrid" : sineGrid,
            "nodeCount" : sampleCount * stationCount,
            "refinedCount" : refinedCount,
            "mergedCount" : mergedCount
        };
}

export function auditEdgeSweepSingularity(strippedMotion is map, edgePoints is array,
    edgeTangents is array, tValues is array) returns map
{
    return auditEdgeSweepSingularity(strippedMotion, edgePoints, edgeTangents, tValues, {});
}

/**
 * Refine one edge-sweep-singularity candidate to the point where the transported edge tangent
 * and the velocity are actually parallel, in the edge curve's own parameter and in t.
 *
 * Least squares rather than a square solve, on purpose: the residual is the 3-vector
 * cross(unit A e', unit velocity) against two unknowns, and its norm IS the sine being driven
 * to zero - so the iteration that locates the point also measures how singular the point it
 * found is. Levenberg damping with a halving line search keeps a candidate that is only a near
 * miss from wandering off the domain in search of a zero that does not exist.
 *
 * options: { tLow, tHigh (the t clamp; default is the seed itself, i.e. s only),
 *     maxIterations (default 40), sineTarget (default 1e-15) }.
 *
 * Returns { curveParameter, t, sine, converged, iterations }.
 */
export function refineEdgeSweepSingularity(strippedMotion is map, strippedCurve is map,
    seedParameter is number, seedT is number, options is map) returns map
{
    const domain = curveParameterDomain(strippedCurve);
    const tLow = options.tLow == undefined ? seedT : options.tLow;
    const tHigh = options.tHigh == undefined ? seedT : options.tHigh;
    const maxIterations = options.maxIterations == undefined ? 40 : options.maxIterations;
    const sineTarget = options.sineTarget == undefined ? 1e-15 : options.sineTarget;
    const parameterStep = 1e-6 * max(domain.high - domain.low, 1e-6);
    const timeStep = 1e-6 * max(tHigh - tLow, 1e-6);

    var s = clampToRange(seedParameter, domain.low, domain.high);
    var t = clampToRange(seedT, tLow, tHigh);
    var residual = edgeSingularityResidual(strippedMotion, strippedCurve, s, t);
    var sine = norm(residual);
    var damping = 1e-10;
    var iterations = 0;
    for (var iteration = 0; iteration < maxIterations; iteration += 1)
    {
        iterations = iteration + 1;
        if (sine <= sineTarget)
        {
            break;
        }
        const sPlus = edgeSingularityResidual(strippedMotion, strippedCurve,
                clampToRange(s + parameterStep, domain.low, domain.high), t);
        const sMinus = edgeSingularityResidual(strippedMotion, strippedCurve,
                clampToRange(s - parameterStep, domain.low, domain.high), t);
        const tPlus = edgeSingularityResidual(strippedMotion, strippedCurve, s,
                clampToRange(t + timeStep, tLow, tHigh));
        const tMinus = edgeSingularityResidual(strippedMotion, strippedCurve, s,
                clampToRange(t - timeStep, tLow, tHigh));
        const sColumn = (1 / (2 * parameterStep)) * (sPlus - sMinus);
        const tColumn = (1 / (2 * timeStep)) * (tPlus - tMinus);
        const a11 = squaredNorm(sColumn);
        const a12 = dot(sColumn, tColumn);
        const a22 = squaredNorm(tColumn);
        const g1 = dot(sColumn, residual);
        const g2 = dot(tColumn, residual);
        if (a11 + a22 < 1e-300)
        {
            break;
        }
        var trialDamping = max(damping * (a11 + a22), 1e-300);
        var accepted = false;
        for (var attempt = 0; attempt < 12; attempt += 1)
        {
            const determinant = (a11 + trialDamping) * (a22 + trialDamping) - a12 * a12;
            if (abs(determinant) < 1e-300)
            {
                trialDamping = trialDamping * 10;
                continue;
            }
            const stepS = (-g1 * (a22 + trialDamping) + g2 * a12) / determinant;
            const stepT = (-g2 * (a11 + trialDamping) + g1 * a12) / determinant;
            const trialS = clampToRange(s + stepS, domain.low, domain.high);
            const trialT = clampToRange(t + stepT, tLow, tHigh);
            const trialResidual = edgeSingularityResidual(strippedMotion, strippedCurve, trialS, trialT);
            const trialSine = norm(trialResidual);
            if (trialSine < sine)
            {
                s = trialS;
                t = trialT;
                residual = trialResidual;
                sine = trialSine;
                damping = max(damping * 0.3, 1e-16);
                accepted = true;
                break;
            }
            trialDamping = trialDamping * 10;
        }
        if (!accepted)
        {
            break;
        }
    }
    return {
            "curveParameter" : s,
            "t" : t,
            "sine" : sine,
            "converged" : sine <= sineTarget * 1e6,
            "iterations" : iterations
        };
}

/**
 * The parameter on a stripped B-spline curve whose point is nearest `target`: a coarse scan of
 * the knot span for a bracket, then Newton on d/ds |e(s) - target|^2 / 2 = <e - target, e'>.
 * This is the bridge between a co-edge's arc-length sample parameters and its spline's own
 * parameter - two different parameterizations of the same curve, which is why a sample index
 * cannot be handed to the refinement directly.
 *
 * Returns { parameter, residual } - the residual being |e(s) - target|.
 */
export function curveParameterNearestPoint(strippedCurve is map, target is Vector) returns map
{
    const domain = curveParameterDomain(strippedCurve);
    const scanCount = 32;
    var bestParameter = domain.low;
    var bestDistanceSquared = undefined;
    for (var scanIndex = 0; scanIndex <= scanCount; scanIndex += 1)
    {
        const parameter = domain.low + (domain.high - domain.low) * scanIndex / scanCount;
        const distanceSquared = squaredNorm(
                evaluateBSplineCurveDerivatives(strippedCurve, parameter, 0)[0] - target);
        if (bestDistanceSquared == undefined || distanceSquared < bestDistanceSquared)
        {
            bestDistanceSquared = distanceSquared;
            bestParameter = parameter;
        }
    }
    var parameter = bestParameter;
    for (var iteration = 0; iteration < 20; iteration += 1)
    {
        const derivatives = evaluateBSplineCurveDerivatives(strippedCurve, parameter, 2);
        const offset = derivatives[0] - target;
        const value = dot(offset, derivatives[1]);
        const slope = squaredNorm(derivatives[1]) + dot(offset, derivatives[2]);
        if (abs(slope) < 1e-300)
        {
            break;
        }
        const next = clampToRange(parameter - value / slope, domain.low, domain.high);
        const converged = abs(next - parameter) < 1e-14 * max(abs(domain.high - domain.low), 1);
        parameter = next;
        if (converged)
        {
            break;
        }
    }
    return {
            "parameter" : parameter,
            "residual" : norm(evaluateBSplineCurveDerivatives(strippedCurve, parameter, 0)[0] - target)
        };
}

/** The knot-parameter span of a stripped B-spline curve. */
export function curveParameterDomain(strippedCurve is map) returns map
{
    return {
            "low" : strippedCurve.knots[strippedCurve.degree],
            "high" : strippedCurve.knots[size(strippedCurve.knots) - strippedCurve.degree - 1]
        };
}

/** cross(unit transported edge tangent, unit velocity) at (s, t); its norm is the sine. */
function edgeSingularityResidual(strippedMotion is map, strippedCurve is map, s is number,
    t is number) returns Vector
{
    const derivatives = evaluateBSplineCurveDerivatives(strippedCurve, s, 1);
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * derivatives[0] + sample.translationDerivative;
    const transportedTangent = sample.rotation * derivatives[1];
    const speed = norm(velocity);
    const tangentNorm = norm(transportedTangent);
    if (speed < 1e-300 || tangentNorm < 1e-300)
    {
        return vector(1, 0, 0);
    }
    return cross((1 / tangentNorm) * transportedTangent, (1 / speed) * velocity);
}

/** True when some already-recorded singularity sits at the same refined (s, t). */
function singularityAlreadyFound(singularities is array, curveParameter is number, t is number,
    parameterTolerance is number, timeTolerance is number) returns boolean
{
    for (var index = 0; index < size(singularities); index += 1)
    {
        if (abs(singularities[index].curveParameter - curveParameter) <= parameterTolerance &&
            abs(singularities[index].refinedT - t) <= timeTolerance)
        {
            return true;
        }
    }
    return false;
}

/** True when a node is no larger than each of its existing axis neighbours. */
function isAxisNeighbourMinimum(sineGrid is array, sampleIndex is number, timeIndex is number) returns boolean
{
    const sine = sineGrid[sampleIndex][timeIndex];
    if (sampleIndex > 0 && sineGrid[sampleIndex - 1][timeIndex] < sine)
    {
        return false;
    }
    if (sampleIndex + 1 < size(sineGrid) && sineGrid[sampleIndex + 1][timeIndex] < sine)
    {
        return false;
    }
    if (timeIndex > 0 && sineGrid[sampleIndex][timeIndex - 1] < sine)
    {
        return false;
    }
    if (timeIndex + 1 < size(sineGrid[sampleIndex]) && sineGrid[sampleIndex][timeIndex + 1] < sine)
    {
        return false;
    }
    return true;
}

/**
 * Bisection-safeguarded false position on f_t inside a section bracket, with every iterate
 * corrected back onto f(., ., tGlobal) = 0 first - so the root found is a point OF THE SECTION
 * at which f_t vanishes, not a point of the chord between two section samples.
 */
function refineSectionTangency(strippedMotion is map, strippedSurface is map, tGlobal is number,
    lowUv is array, highUv is array, lowDerivative is number, highDerivative is number,
    segmentIndex is number, sectionTolerance is number, maxIterations is number) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    var lowFraction = 0;
    var highFraction = 1;
    var lowValue = lowDerivative;
    var highValue = highDerivative;
    var fraction = 0.5;
    var uv = lowUv;
    var gradient = evaluateEnvelopeGradientPointwise(stationMotion, strippedSurface,
        lowUv[0], lowUv[1], tGlobal);
    for (var iteration = 0; iteration < maxIterations; iteration += 1)
    {
        // False position while the two ends still bracket, bisection whenever it would step
        // outside - the usual safeguard, and here it also keeps the corrector's seed close.
        var candidate = 0.5 * (lowFraction + highFraction);
        if (highValue != lowValue)
        {
            const secant = lowFraction - lowValue * (highFraction - lowFraction) / (highValue - lowValue);
            if (secant > lowFraction && secant < highFraction)
            {
                candidate = secant;
            }
        }
        const seed = [lowUv[0] + candidate * (highUv[0] - lowUv[0]),
            lowUv[1] + candidate * (highUv[1] - lowUv[1])];
        uv = correctPointOntoSection(stationMotion, strippedSurface, tGlobal, seed, sectionTolerance);
        gradient = evaluateEnvelopeGradientPointwise(stationMotion, strippedSurface, uv[0], uv[1], tGlobal);
        fraction = candidate;
        const value = gradient.tDerivative;
        if (value == 0 || highFraction - lowFraction < 1e-14)
        {
            break;
        }
        if (value * lowValue > 0)
        {
            lowFraction = candidate;
            lowValue = value;
        }
        else
        {
            highFraction = candidate;
            highValue = value;
        }
    }
    return {
            "segmentIndex" : segmentIndex,
            "fraction" : fraction,
            "uv" : uv,
            "timeDerivative" : gradient.tDerivative,
            "timeDerivativeScale" : gradient.tDerivativeScale,
            "sectionResidual" : abs(gradient.value),
            "atSample" : undefined
        };
}

/** Sign with a dead band: zero inside the tolerance rather than an arbitrary side of it. */
function signWithDeadband(value is number, tolerance is number) returns number
{
    if (abs(value) <= tolerance)
    {
        return 0;
    }
    return value > 0 ? 1 : -1;
}

/** True when some tangency was located on the given march segment. */
function tangencyOnSegment(tangencies is array, segmentIndex is number) returns boolean
{
    for (var index = 0; index < size(tangencies); index += 1)
    {
        if (tangencies[index].segmentIndex == segmentIndex)
        {
            return true;
        }
    }
    return false;
}

/** Append unless the point repeats the last one - split points are shared, not duplicated. */
function appendUnlessDuplicate(points is array, point is array) returns array
{
    const last = points[size(points) - 1];
    if ((point[0] - last[0]) ^ 2 + (point[1] - last[1]) ^ 2 <= 1e-28)
    {
        return points;
    }
    return append(points, point);
}


// ============================= Extraction, caps, knit, assembly (spec 5 and 9) =============================

/**
 * A stripped surface with a uniform weight grid re-declared NON-RATIONAL, weights dropped.
 * `normalizeSurfaceDefinition` gives every surface a weight grid and flags it rational, so a
 * genuinely non-rational net arrives here flagged rational with weights all equal; a constant
 * weight scale cancels in the projective divide, so dropping it is exact. Nets whose weights
 * actually vary pass through untouched, still flagged rational.
 */
export function dropUniformWeights(strippedSurface is map) returns map
{
    var surface = strippedSurface;
    if (surface.isRational != true || surface.weights == undefined)
    {
        surface.isRational = false;
        surface.weights = undefined;
        return surface;
    }
    const reference = surface.weights[0][0];
    if (abs(reference) < UNIFORM_WEIGHT_TOLERANCE)
    {
        return surface;
    }
    for (var weightRow in surface.weights)
    {
        for (var weight in weightRow)
        {
            if (abs(weight - reference) > UNIFORM_WEIGHT_TOLERANCE * abs(reference))
            {
                return surface;
            }
        }
    }
    surface.isRational = false;
    surface.weights = undefined;
    return surface;
}

/**
 * Which of a stripped surface's four control-net boundaries collapse to a single point - the
 * poles of a surface of revolution. A collapsed row is a PARAMETERIZATION artifact that the
 * envelope solver has to know about: the surface normal S_u x S_v vanishes identically there,
 * so the contact function f = <A.n, velocity> is identically zero along the whole row whatever
 * the motion. Those zeros are not contact, and a funnel census that believes them connects
 * every real component through the pole (spec section 7.5).
 *
 * Returns { uStart, uEnd, vStart, vEnd } booleans - uStart/uEnd name collapsed control ROWS
 * (constant u), vStart/vEnd collapsed control COLUMNS (constant v).
 */
export function degenerateSplineBoundaries(strippedSurface is map, tolerance is number) returns map
{
    const controlPoints = strippedSurface.controlPoints;
    const rowCount = size(controlPoints);
    const columnCount = size(controlPoints[0]);
    return {
            "uStart" : rowIsCollapsed(controlPoints[0], tolerance),
            "uEnd" : rowIsCollapsed(controlPoints[rowCount - 1], tolerance),
            "vStart" : columnIsCollapsed(controlPoints, 0, tolerance),
            "vEnd" : columnIsCollapsed(controlPoints, columnCount - 1, tolerance)
        };
}

/** Whether every point of a control row equals the first within tolerance. */
function rowIsCollapsed(row is array, tolerance is number) returns boolean
{
    for (var index = 1; index < size(row); index += 1)
    {
        if (squaredNorm(row[index] - row[0]) > tolerance * tolerance)
        {
            return false;
        }
    }
    return true;
}

/** Whether every point of a control column equals the first within tolerance. */
function columnIsCollapsed(controlPoints is array, columnIndex is number, tolerance is number) returns boolean
{
    for (var rowIndex = 1; rowIndex < size(controlPoints); rowIndex += 1)
    {
        if (squaredNorm(controlPoints[rowIndex][columnIndex] - controlPoints[0][columnIndex]) > tolerance * tolerance)
        {
            return false;
        }
    }
    return true;
}

/**
 * Whether a surface definition that answers no `is` check is one of the two PROFILE-driven
 * classes, and which (spec 6.5.1). Returns undefined for anything else.
 *
 * This is the question extraction had never asked: a REVOLVED or EXTRUDED face reports neither a
 * named struct nor a BSplineSurface, so it fell through to approximation on class alone even
 * though the kernel names the shape. `SurfaceType` (query.fs) carries both as first-class cases
 * and `evSurfaceDefinition` returns `{ surfaceType }` and nothing else for them - the class name
 * is the entire payload, which is why the generator has to be recovered geometrically.
 */
export function classifyProfileDrivenSurface(surfaceDefinition)
{
    if (!(surfaceDefinition is map) || surfaceDefinition.surfaceType == undefined)
    {
        return undefined;
    }
    if (surfaceDefinition.surfaceType == SurfaceType.REVOLVED)
    {
        return SweepSurfaceClass.REVOLVED;
    }
    if (surfaceDefinition.surfaceType == SurfaceType.EXTRUDED)
    {
        return SweepSurfaceClass.EXTRUDED;
    }
    return undefined;
}

/**
 * A conic arc as an EXACT rational quadratic B-spline (spec 6.5.1, 2026-08-24).
 *
 * This is the piece whose absence forced the section-9.4 ellipsoid onto the sampled path: its
 * generator is an `Ellipse`, `readExactProfileCurve` refused conics, and the face fell back to
 * `evApproximateBSplineSurface`. The conversion is exact and standard, so the refusal was never
 * necessary.
 *
 * Two facts do all the work. A circular arc of half-angle alpha is exactly the rational quadratic
 * Bezier with control points `[P0, T, P2]` — T the intersection of the end tangents, at
 * `1 / cos alpha` along the mid-angle direction — and weights `[1, cos alpha, 1]`. And an ellipse
 * is an AFFINE image of the unit circle, while a NURBS is affine-invariant with its weights
 * untouched: mapping the control points through `x -> major * x * e1 + minor * y * e2` carries the
 * exact circle to the exact ellipse. Arcs are split at 90 degrees so no segment approaches the
 * `cos alpha -> 0` degeneracy at a half turn.
 *
 * Angles are plain radians in the conic's own frame; `endAngle` may be less than `startAngle` to
 * traverse the other way. Returns the unit-stripped spline shape the rest of this module consumes.
 */
export function exactConicArcSpline(conicFrame is CoordSystem, majorRadius is number,
    minorRadius is number, startAngle is number, endAngle is number) returns map
{
    const span = endAngle - startAngle;
    const segmentCount = max(1, ceil(abs(span) / (0.5 * PI) - 1e-9));
    const step = span / segmentCount;
    const midWeight = cos(0.5 * abs(step) * radian);
    const origin = conicFrame.origin / meter;
    const firstAxis = conicFrame.xAxis;
    const secondAxis = cross(conicFrame.zAxis, conicFrame.xAxis);

    var controlPoints = makeArray(2 * segmentCount + 1, vector(0, 0, 0));
    var weights = makeArray(2 * segmentCount + 1, 1);
    for (var segment = 0; segment < segmentCount; segment += 1)
    {
        const angleStart = startAngle + step * segment;
        const angleEnd = angleStart + step;
        const angleMid = angleStart + 0.5 * step;
        const unitPoints = [
                vector(cos(angleStart * radian), sin(angleStart * radian)),
                vector(cos(angleMid * radian), sin(angleMid * radian)) / midWeight,
                vector(cos(angleEnd * radian), sin(angleEnd * radian))
            ];
        for (var corner = 0; corner < 3; corner += 1)
        {
            controlPoints[2 * segment + corner] = origin +
                majorRadius * unitPoints[corner][0] * firstAxis +
                minorRadius * unitPoints[corner][1] * secondAxis;
        }
        weights[2 * segment] = 1;
        weights[2 * segment + 1] = midWeight;
        weights[2 * segment + 2] = 1;
    }

    // Degree 2, clamped, every interior knot doubled - one Bezier segment per 90 degrees.
    var knots = makeArray(2 * segmentCount + 4, 1);
    for (var index = 0; index < 3; index += 1)
    {
        knots[index] = 0;
    }
    for (var segment = 1; segment < segmentCount; segment += 1)
    {
        knots[1 + 2 * segment] = segment / segmentCount;
        knots[2 + 2 * segment] = segment / segmentCount;
    }
    return {
            "degree" : 2,
            "knots" : knots,
            "controlPoints" : controlPoints,
            "isRational" : true,
            "weights" : weights,
            "isPeriodic" : false
        };
}

/** The parametric angle of a point on a conic, in the conic's own frame. Plain radians. */
function conicAngleOfPoint(conicFrame is CoordSystem, majorRadius is number, minorRadius is number,
    point is Vector) returns number
{
    const offset = point - conicFrame.origin / meter;
    const secondAxis = cross(conicFrame.zAxis, conicFrame.xAxis);
    return atan2(dot(offset, secondAxis) / minorRadius, dot(offset, conicFrame.xAxis) / majorRadius) / radian;
}

/**
 * The angular span of a conic EDGE, chosen so the arc passes through the edge's own midpoint.
 * Endpoints alone are ambiguous - every pair of points on a conic bounds two arcs - and on a
 * revolve's meridian the two arcs are the two halves of the ellipse, one with r >= 0 and one with
 * r <= 0. Picking by the midpoint is what makes the generator the half that actually exists.
 *
 * Returns { startAngle, endAngle } in plain radians, with endAngle possibly below startAngle.
 */
function conicEdgeAngularSpan(conicFrame is CoordSystem, majorRadius is number, minorRadius is number,
    startPoint is Vector, midPoint is Vector, endPoint is Vector, closed is boolean) returns map
{
    const startAngle = conicAngleOfPoint(conicFrame, majorRadius, minorRadius, startPoint);
    if (closed)
    {
        return { "startAngle" : startAngle, "endAngle" : startAngle + 2 * PI };
    }
    const endAngle = conicAngleOfPoint(conicFrame, majorRadius, minorRadius, endPoint);
    const midAngle = conicAngleOfPoint(conicFrame, majorRadius, minorRadius, midPoint);
    const forwardSpan = positiveModulo(endAngle - startAngle, 2 * PI);
    const forwardMid = positiveModulo(midAngle - startAngle, 2 * PI);
    return forwardMid <= forwardSpan ?
        { "startAngle" : startAngle, "endAngle" : startAngle + forwardSpan } :
        { "startAngle" : startAngle, "endAngle" : startAngle - (2 * PI - forwardSpan) };
}

/**
 * The untrimmed iso-curve edges of a face in one parameter direction (spec 6.5.1). `skipTrim`
 * is what makes this the UNDERLYING surface's curve rather than the piece this face happens to
 * span, which for a revolve is the difference between the generator and a fragment of it.
 *
 * `FaceCurveCreationType.DIRn_ISO` means CONSTANT in direction n - measured 2026-08-24 on a
 * revolved ellipsoid, where DIR1 returned the circumferential circle (theta spread 2.51 rad,
 * closed) and DIR2 the meridian (theta spread exactly 0, open, pole to pole). So the generator of
 * a surface of revolution is the iso-curve constant in the PERIODIC direction.
 *
 * Returns { created {boolean}, id {Id}, edges {array} }; the caller deletes the bodies.
 */
export function faceIsoCurveEdges(context is Context, id is Id, face is Query,
    creationType is FaceCurveCreationType, parameter is number) returns map
{
    var created = true;
    try silent
    {
        opCreateCurvesOnFace(context, id, {
                    "curveDefinition" : [curveOnFaceDefinition(face, creationType, ["generator"], [parameter])],
                    "skipTrim" : true,
                    "useFaceParameter" : true
                });
    }
    catch
    {
        created = false;
    }
    return {
            "created" : created,
            "id" : id,
            "edges" : created ? evaluateQuery(context, qCreatedBy(id, EntityType.EDGE)) : []
        };
}

/**
 * Recover the exact generator of one REVOLVED or EXTRUDED face and return the analytic frame built
 * on it (spec 6.5.1). ONE kernel op per direction needed, plus one delete.
 *
 * A revolved face IS its generating curve — the surface carries no information the generator does
 * not — so the generator is READ OFF rather than reconstructed. `opCreateCurvesOnFace` at
 * `skipTrim : true` returns the untrimmed iso-curve of the underlying surface, and
 * `FaceCurveCreationType.DIRn_ISO` means CONSTANT in direction n. Measured 2026-08-24 on the
 * section-9.4 ellipsoid: DIR1 gave the circumferential circle (theta spread 2.51 rad, closed) and
 * DIR2 the meridian (theta spread exactly 0, open, pole to pole, `Ellipse` major 0.05 minor 0.03).
 * So a surface of revolution's generator is the iso-curve constant in the PERIODIC direction, and it
 * arrives on ONE side of the axis, which is what an axial plane cut could not deliver — that cut
 * returns the closed conic straddling the axis.
 *
 * This replaced an `opPlane` + `opIntersectFaces` recovery and deleted every heuristic it needed:
 * the cutting-plane-through-the-box-centre rule, the `perpendicularVector` fallback for a full
 * revolve, the pick-the-plus-side-edge rule, and a four-tangent-plane normal-invariance test for the
 * extrusion direction. None of them described geometry; they all existed to place a plane well.
 *
 * EXTRUDED takes both directions and identifies them by class: for `S(u,v) = C(u) + v d` the
 * iso-curve constant in the ruling parameter is a straight LINE and the other is the exact cross
 * section, so the `Line` names the direction with no residual to interpret.
 *
 * Returns { frame, profileEdgeCount, profileCurveClass, directionResidual, refusal } — `frame`
 * undefined and `refusal` a sentence when the generator cannot be recovered exactly, which is the
 * caller's signal to fall back to approximation rather than to fail.
 */
export function recoverProfileDrivenFrame(context is Context, nextId is function, face is Query,
    surfaceClass is SweepSurfaceClass) returns map
{
    const periodicity = evFacePeriodicity(context, { "face" : face });
    var scratchIds = [];
    var result = { "frame" : undefined, "profileEdgeCount" : 0, "profileCurveClass" : undefined,
            "directionResidual" : 0, "refusal" : undefined };

    if (surfaceClass == SweepSurfaceClass.REVOLVED)
    {
        // The kernel names the class but is not obliged to hand back an axis for it, and a refusal
        // has to stay a refusal: this runs inside extraction, so a throw takes the whole build down
        // over one face that could have gone to approximation instead.
        var axisOrNothing = undefined;
        try silent
        {
            axisOrNothing = evAxis(context, { "axis" : face });
        }
        if (axisOrNothing == undefined)
        {
            result.refusal = "evAxis would not give an axis for a face the kernel calls REVOLVED.";
            return result;
        }
        const axisLine is Line = axisOrNothing;
        const creationType = periodicity[1] == true ?
            FaceCurveCreationType.DIR2_ISO : FaceCurveCreationType.DIR1_ISO;
        const iso = faceIsoCurveEdges(context, nextId(), face, creationType, 0.5);
        scratchIds = append(scratchIds, iso.id);
        result.profileEdgeCount = size(iso.edges);
        if (size(iso.edges) == 0)
        {
            result.refusal = iso.created ?
                "the meridian iso-curve came back with no edge." :
                "opCreateCurvesOnFace refused the meridian direction.";
            deleteScratchBodies(context, nextId(), scratchIds);
            return result;
        }
        const generatorEdge = iso.edges[0];
        const samples = evEdgeTangentLines(context, { "edge" : generatorEdge, "parameters" : [0, 0.5, 1] });
        const midPoint = samples[1].origin;
        const axisPoint = axisLine.origin +
            dot(midPoint - axisLine.origin, axisLine.direction) * axisLine.direction;
        const radial = midPoint - axisPoint;
        if (norm(radial) < 1e-9 * meter)
        {
            result.refusal = "the meridian iso-curve's midpoint lies on the axis, so no radial " ~
                "direction could be built from it.";
            deleteScratchBodies(context, nextId(), scratchIds);
            return result;
        }
        // The meridian holds theta constant, so its own midpoint names the half plane it lives in -
        // and that is what makes the frame's r(u) non-negative with nothing to choose.
        const firstInPlane = normalize(radial);
        const generator = readExactProfileCurve(context, generatorEdge);
        result.profileCurveClass = generator.curveClass;
        if (generator.spline == undefined)
        {
            result.refusal = "the recovered generator is a " ~ generator.curveClass ~
                ", which this module does not convert to an exact spline.";
        }
        else
        {
            result.frame = analyticProfileFrame(AnalyticSurfaceKind.REVOLVED, axisPoint / meter,
                    axisLine.direction, firstInPlane, generator.spline);
        }
        deleteScratchBodies(context, nextId(), scratchIds);
        return result;
    }

    // EXTRUDED. Both iso-curve directions, identified by class rather than by a residual: the one
    // that is a LINE is the ruling, the other is the exact cross section.
    var rulingDirection = undefined;
    var sectionEdge = undefined;
    var sectionEdgeCount = 0;
    for (var creationType in [FaceCurveCreationType.DIR1_ISO, FaceCurveCreationType.DIR2_ISO])
    {
        const iso = faceIsoCurveEdges(context, nextId(), face, creationType, 0.5);
        scratchIds = append(scratchIds, iso.id);
        if (size(iso.edges) == 0)
        {
            continue;
        }
        const edge = iso.edges[0];
        if (classifyCurveDefinition(evCurveDefinition(context, { "edge" : edge })) == SweepCurveClass.LINE)
        {
            const ends = evEdgeTangentLines(context, { "edge" : edge, "parameters" : [0, 1] });
            const along = ends[1].origin - ends[0].origin;
            if (norm(along) > 1e-9 * meter)
            {
                rulingDirection = normalize(along);
            }
        }
        else
        {
            sectionEdge = edge;
            sectionEdgeCount = size(iso.edges);
        }
    }
    result.profileEdgeCount = sectionEdgeCount;
    if (rulingDirection == undefined || sectionEdge == undefined)
    {
        result.refusal = "the two iso-curve directions did not resolve into one straight ruling " ~
            "and one cross section on a face the kernel calls EXTRUDED.";
        deleteScratchBodies(context, nextId(), scratchIds);
        return result;
    }
    const generator = readExactProfileCurve(context, sectionEdge);
    result.profileCurveClass = generator.curveClass;
    if (generator.spline == undefined)
    {
        result.refusal = "the recovered cross section is a " ~ generator.curveClass ~
            ", which this module does not convert to an exact spline.";
    }
    else
    {
        const sectionMid = evEdgeTangentLines(context, { "edge" : sectionEdge, "parameters" : [0.5] })[0].origin;
        result.frame = analyticProfileFrame(AnalyticSurfaceKind.EXTRUDED, sectionMid / meter,
                rulingDirection, perpendicularVector(rulingDirection), generator.spline);
    }
    deleteScratchBodies(context, nextId(), scratchIds);
    return result;
}

/** Delete the wire bodies an iso-curve pass left behind, tolerating ids that produced nothing. */
function deleteScratchBodies(context is Context, id is Id, scratchIds is array)
{
    var bodies = [];
    for (var scratchId in scratchIds)
    {
        const created = qCreatedBy(scratchId, EntityType.BODY);
        if (size(evaluateQuery(context, created)) > 0)
        {
            bodies = append(bodies, created);
        }
    }
    if (size(bodies) > 0)
    {
        opDeleteBodies(context, id, { "entities" : qUnion(bodies) });
    }
}

/**
 * One profile edge as an EXACT unit-stripped spline, or undefined when it cannot be one.
 *
 * A B-spline edge is already exact. A line is converted exactly, degree 1 on two endpoints. A
 * circle or ellipse edge is REFUSED rather than approximated: its exact rational spelling needs
 * the edge's own angular trim, and an approximation here would be an approximation the record
 * then reports as exact. On the two classes' real fixtures the cut returns a spline (probe 8
 * measured the rational quarter-ellipse coming back as a degree-2 rational B-spline, not as an
 * Ellipse), so this is the one remaining hole in these classes rather than their common case.
 */
function readExactProfileCurve(context is Context, edge is Query) returns map
{
    const curveDefinition = evCurveDefinition(context, { "edge" : edge });
    const curveClass = classifyCurveDefinition(curveDefinition);
    if (curveClass == SweepCurveClass.BSPLINE)
    {
        return { "curveClass" : curveClass,
                "spline" : stripCurveUnits(normalizeSplineDefinition(curveDefinition)) };
    }
    if (curveClass == SweepCurveClass.LINE)
    {
        const ends = evEdgeTangentLines(context, { "edge" : edge, "parameters" : [0, 1] });
        return {
                "curveClass" : curveClass,
                "spline" : {
                        "degree" : 1,
                        "knots" : [0, 0, 1, 1],
                        "controlPoints" : [ends[0].origin / meter, ends[1].origin / meter],
                        "isRational" : false,
                        "weights" : undefined,
                        "isPeriodic" : false
                    }
            };
    }
    if (curveClass == SweepCurveClass.CIRCLE || curveClass == SweepCurveClass.ELLIPSE)
    {
        // Exact, not approximated: a conic arc IS a rational quadratic. Refusing this is what sent
        // the section-9.4 ellipsoid - whose generator is an Ellipse - down the sampled path.
        const isCircle = curveClass == SweepCurveClass.CIRCLE;
        const majorRadius = (isCircle ? curveDefinition.radius : curveDefinition.majorRadius) / meter;
        const minorRadius = (isCircle ? curveDefinition.radius : curveDefinition.minorRadius) / meter;
        const samples = evEdgeTangentLines(context, { "edge" : edge, "parameters" : [0, 0.5, 1] });
        const closed = norm(samples[2].origin - samples[0].origin) < 1e-12 * meter;
        const span = conicEdgeAngularSpan(curveDefinition.coordSystem, majorRadius, minorRadius,
                samples[0].origin / meter, samples[1].origin / meter, samples[2].origin / meter, closed);
        return {
                "curveClass" : curveClass,
                "spline" : exactConicArcSpline(curveDefinition.coordSystem, majorRadius, minorRadius,
                        span.startAngle, span.endAngle)
            };
    }
    return { "curveClass" : curveClass, "spline" : undefined };
}

/**
 * The analytic frame for one face record, whichever of the two routes built it: the five
 * parameter-driven classes carry their whole shape in `analytic` and are stripped on demand, the
 * two profile-driven ones carry a frame already built at extraction. Returns undefined for a
 * record that took the spline or approximation path.
 */
export function analyticFrameForFaceRecord(record is map)
{
    if (record.analyticFrame != undefined)
    {
        return record.analyticFrame;
    }
    if (record.analytic != undefined && isAnalyticContactSurface(record.analytic))
    {
        return stripAnalyticSurface(record.analytic);
    }
    return undefined;
}

/**
 * Read every face of `toolBody` into a ToolFaceRecord. This is the once-per-build extraction
 * pass; downstream solver stages consume the records without touching the context.
 *
 * Each record: {
 *     faceIndex {number} : position in the returned array,
 *     faceQuery {Query} : transient query for the face (valid during this regeneration only),
 *     surfaceClass {SweepSurfaceClass},
 *     analytic {map} : the typed evSurfaceDefinition value for analytic classes, else undefined.
 *         For the two profile-driven classes it is the { surfaceType } map, which carries no
 *         shape - analyticFrame is the shape. Use analyticFrameForFaceRecord rather than
 *         branching on this,
 *     analyticFrame {map} : the analyticProfileFrame of a REVOLVED or EXTRUDED face, generator
 *         included, else undefined. Only the five-argument overload builds these,
 *     profileRecovery {map} : what recoverProfileDrivenFrame measured on the way - edge count,
 *         generator class, extrusion-direction residual, and the refusal sentence when the
 *         generator was not recoverable exactly, else undefined,
 *     spline {map} : normalized, unit-stripped BSplineSurface data for BSPLINE and OTHER
 *         classes - exact for BSPLINE, approximated at faceExtractTolerance for OTHER. A net
 *         whose weights are UNIFORM is re-declared non-rational and the weights dropped (exact:
 *         a constant weight scale cancels in the projective divide); one whose weights genuinely
 *         vary stays rational, which every pointwise consumer handles. Only the coefficient path
 *         (spec 6.0) needs non-rational input - see the four-argument overload,
 *     splineIsExact {boolean} : false whenever the approximation route was taken,
 *     periodic {array} : [uPeriodic, vPeriodic] from evFacePeriodicity,
 *     trimLoops {map} : { boundary, inner } 2D UV BSplineCurves in the approximated spline's
 *         domain - present only on the approximation path (exact and analytic faces get their
 *         loops from co-edge pcurves in the edge extraction pass),
 *     calibration {map} : UvCalibration for spline-bearing records, else undefined,
 *     degenerate {map} : { uStart, uEnd, vStart, vEnd } collapsed control-net boundaries -
 *         the revolve poles the funnel census must mask (degenerateSplineBoundaries)
 * }
 *
 * faceExtractTolerance is a plain number, meters implied.
 *
 * This overload does NOT recognize the two profile-driven classes: recovering a generator costs
 * two kernel ops, which needs an id, so it is the five-argument overload's job. A REVOLVED or
 * EXTRUDED face reaching this one classifies as OTHER and takes the approximation path exactly as
 * it did before spec 6.5.1 - which is what a caller wanting pcurves on that face still needs,
 * since an analytic record carries no spline for the co-edge inversion to invert onto.
 */
export function extractToolFaceRecords(context is Context, toolBody is Query, faceExtractTolerance is number) returns array
{
    return extractToolFaceRecords(context, toolBody, faceExtractTolerance, false, undefined);
}

/**
 * Same, with control over whether freeform approximations are forced NON-RATIONAL.
 *
 * `forceNonRational` is not free, and on a periodic face it is not even usable. Asked for the wall of an elliptical extrude at 1e-6, the kernel answers
 * rationally with a 7 x 2 net whose U knots are clean closed-clamped ([0,0,0,0, .5,.5,.5,
 * 1,1,1,1]) and whose end control rows coincide to 0 m - the form normalizeSurfaceDefinition
 * converts to wrap form exactly. Forced non-rational, the SAME face comes back as a 58 x 2 net
 * with every knot doubled, a knot range overrunning the domain by a span at each end, end rows
 * 2 mm apart, and a wrap relation off by exactly one knot step: a third periodic spelling, which
 * normalizeSurfaceDefinition refuses to guess at rather than silently mis-read. The revolved
 * ellipsoid shows the same appetite - 99 x 49 forced against a handful of control points
 * rational.
 *
 * Nothing in the pointwise path needs the conversion: evaluateBSplineSurfaceDerivatives is
 * NURBS Book A4.4, so marching, seeding, lifting, inversion, fitting, and certification are all
 * rational-correct. Only the envelope layer's coefficient path (spec 6.0) requires non-rational
 * input, and per spec 7.6 that path wants its own coarse extraction anyway. So the default is
 * FALSE, and a caller that turns it on owns the spelling problem.
 */
export function extractToolFaceRecords(context is Context, toolBody is Query, faceExtractTolerance is number,
    forceNonRational is boolean) returns array
{
    return extractToolFaceRecords(context, toolBody, faceExtractTolerance, forceNonRational, undefined);
}

/**
 * Same, with the two PROFILE-driven classes recognized (spec 6.5.1). nextId is an id source -
 * getUnstableIncrementingId(id) - spent on the two kernel ops that recover each REVOLVED or
 * EXTRUDED face's exact generator, plus one delete that cleans the scratch bodies up. Pass
 * undefined and those faces route to approximation as before.
 *
 * What a recognized face gains: closed-form contact curves, an exact sliding verdict, and a free
 * |f| screen, with NO call to evApproximateBSplineSurface anywhere. What it gives up: the
 * approximated net, and with it the co-edge pcurves that inversion builds onto a net - the same
 * trade the five parameter-driven classes have always made.
 */
export function extractToolFaceRecords(context is Context, toolBody is Query, faceExtractTolerance is number,
    forceNonRational is boolean, nextId) returns array
{
    const faces = evaluateQuery(context, qOwnedByBody(toolBody, EntityType.FACE));
    var records = makeArray(size(faces));
    for (var faceIndex = 0; faceIndex < size(faces); faceIndex += 1)
    {
        const face = faces[faceIndex];
        const surfaceDefinition = evSurfaceDefinition(context, { "face" : face });
        const profileDrivenClass = nextId == undefined ? undefined :
            classifyProfileDrivenSurface(surfaceDefinition);
        var record = {
            "faceIndex" : faceIndex,
            "faceQuery" : face,
            "surfaceClass" : profileDrivenClass != undefined ? profileDrivenClass :
                classifySurfaceDefinition(surfaceDefinition),
            "analytic" : undefined,
            "analyticFrame" : undefined,
            "profileRecovery" : undefined,
            "spline" : undefined,
            "splineIsExact" : false,
            "periodic" : evFacePeriodicity(context, { "face" : face }),
            "trimLoops" : undefined,
            "calibration" : undefined,
            "degenerate" : undefined
        };
        if (profileDrivenClass != undefined)
        {
            const recovery = recoverProfileDrivenFrame(context, nextId, face, profileDrivenClass);
            record.profileRecovery = recovery;
            record.analyticFrame = recovery.frame;
            record.analytic = surfaceDefinition;
            if (recovery.frame == undefined)
            {
                // The kernel named the class but its generator did not come back exactly. Routing
                // the face to approximation keeps it solvable, and the refusal sentence in
                // profileRecovery says which way it failed - a silent exactness claim would be
                // the one unacceptable outcome here.
                record.surfaceClass = SweepSurfaceClass.OTHER;
                record.analytic = undefined;
                record = readApproximatedFace(context, record, face, faceExtractTolerance, forceNonRational);
            }
        }
        else if (record.surfaceClass == SweepSurfaceClass.BSPLINE)
        {
            record.spline = dropUniformWeights(stripSurfaceUnits(normalizeSurfaceDefinition(surfaceDefinition)));
            record.splineIsExact = true;
        }
        else if (record.surfaceClass == SweepSurfaceClass.OTHER)
        {
            record = readApproximatedFace(context, record, face, faceExtractTolerance, forceNonRational);
        }
        else
        {
            record.analytic = surfaceDefinition;
        }
        if (record.spline != undefined)
        {
            record.calibration = buildUvCalibration(context, face, record.spline);
            record.degenerate = degenerateSplineBoundaries(record.spline, DEGENERATE_ROW_TOLERANCE);
        }
        records[faceIndex] = record;
    }
    return records;
}

/**
 * Read one face as a B-spline approximation and fill the record's spline, trim
 * loops, and exactness. `forceNonRational` is passed straight through - see the four-argument
 * extractToolFaceRecords for why it defaults to false and what it costs when it is true.
 */
function readApproximatedFace(context is Context, record is map, face is Query, faceExtractTolerance is number,
    forceNonRational is boolean) returns map
{
    var updated = record;
    const approximated = evApproximateBSplineSurface(context, {
                "face" : face,
                "tolerance" : faceExtractTolerance,
                "forceNonRational" : forceNonRational
            });
    updated.spline = dropUniformWeights(stripSurfaceUnits(normalizeSurfaceDefinition(approximated.bSplineSurface)));
    updated.splineIsExact = false;
    updated.trimLoops = {
        "boundary" : approximated.boundaryBSplineCurves,
        "inner" : approximated.innerLoopBSplineCurves
    };
    return updated;
}

/** One line of per-class counts and calibration flags for an array of ToolFaceRecords. */
export function summarizeFaceRecords(records is array) returns string
{
    var summary = size(records) ~ " face(s):";
    for (var record in records)
    {
        summary = summary ~ " [" ~ record.faceIndex ~ "] " ~ record.surfaceClass ~
            (record.splineIsExact ? " exact" : "") ~
            (record.trimLoops != undefined ? (" loops " ~ size(record.trimLoops.boundary) ~ "+" ~
                        size(record.trimLoops.inner)) : "") ~
            (record.calibration != undefined ? (" affine " ~ record.calibration.isAffine) : "") ~
            (record.spline != undefined && record.spline.isRational == true ? " RATIONAL" : "") ~
            (record.degenerate != undefined ? (" poles " ~ record.degenerate.uStart ~ "/" ~ record.degenerate.uEnd ~
                        "/" ~ record.degenerate.vStart ~ "/" ~ record.degenerate.vEnd) : "") ~
            (record.analyticFrame != undefined ? (" generator " ~ record.profileRecovery.profileCurveClass ~
                        " degree " ~ record.analyticFrame.profile.degree ~ " x " ~
                        size(record.analyticFrame.profile.controlPoints) ~
                        (record.analyticFrame.profile.isRational == true ? " RATIONAL" : "") ~
                        " offPlane " ~ record.analyticFrame.profileOutOfPlane) : "") ~
            (record.profileRecovery != undefined && record.profileRecovery.refusal != undefined ?
                (" REFUSED: " ~ record.profileRecovery.refusal) : "") ~
            " periodic " ~ record.periodic[0] ~ "/" ~ record.periodic[1] ~ ";";
    }
    return summary;
}

/**
 * Read every edge of `toolBody` into a CoEdgeRecord tied to the face records from the same
 * extraction pass. Sample arrays are the shared currency of the pipeline: adjacent consumers
 * of an edge read the SAME parameter, point, and normal arrays, which is what makes seam
 * stitching exact downstream. Spline fitting over these samples belongs to the strip-function
 * assembly, not to extraction.
 *
 * Each record: {
 *     edgeIndex {number}, edgeQuery {Query} (transient, this regeneration only),
 *     curveClass {SweepCurveClass},
 *     analyticCurve {map} : the typed evCurveDefinition value for LINE / CIRCLE / ELLIPSE,
 *         else undefined,
 *     spline3d {map} : unit-stripped normalized B-spline of the edge (exact for BSPLINE,
 *         else approximated at 1e-6; undefined when approximation fails),
 *     splineIsExact {boolean},
 *     convexity {EdgeConvexityType} : undefined when the kernel refuses the query
 *         (sheet-boundary edges may),
 *     faceIndexLeft, faceIndexRight {number} : indices into faceRecords; "left" is the face
 *         kept on the left when walking the edge's default (arc-length) direction with that
 *         face's normal up, decided by the kernel's own usingFaceOrientation tangent; a
 *         sheet-boundary edge has one side and the other index is undefined,
 *     sampleParameters {array} : arc-length parameters 0..1 including both ends,
 *     edgePoints {array} : unit-stripped 3D points on the edge at sampleParameters,
 *     edgeTangents {array} : UNIT tangents of the edge at the same parameters, in the edge's
 *         default (arc-length increasing) direction - the tangent plane's own x axis, which
 *         with usingFaceOrientation left off is the edge tangent rather than a walking
 *         direction, so both sides agree. Spec 6.4's SWEEP_EDGE_SWEEP_SINGULARITY needs e'
 *         on exactly these samples, and 8.x's sharp-edge sheets need it again; taking it from
 *         the tangent-plane call that already ran costs no ev-call,
 *     sideNormals {map} : { left, right } arrays of one-sided unit normals at the same
 *         parameters (undefined side omitted),
 *     uvCurves {map} : { left, right } pcurve data { uvSamples, maxResidual } in that face's
 *         knot domain, present only where the face record carries a spline - built by seeded
 *         inversion marching, falling back to the multi-seed grid when a step exceeds 1e-8 m
 * }
 */
export function extractCoEdgeRecords(context is Context, toolBody is Query, faceRecords is array, samplesPerEdge is number) returns array
{
    const edges = evaluateQuery(context, qOwnedByBody(toolBody, EntityType.EDGE));
    var sampleParameters = makeArray(samplesPerEdge);
    for (var sampleIndex = 0; sampleIndex < samplesPerEdge; sampleIndex += 1)
    {
        sampleParameters[sampleIndex] = sampleIndex / (samplesPerEdge - 1);
    }
    var records = makeArray(size(edges));
    for (var edgeIndex = 0; edgeIndex < size(edges); edgeIndex += 1)
    {
        const edge = edges[edgeIndex];
        const curveDefinition = evCurveDefinition(context, { "edge" : edge });
        const curveClass = classifyCurveDefinition(curveDefinition);
        var spline3d = undefined;
        var splineIsExact = false;
        if (curveClass == SweepCurveClass.BSPLINE)
        {
            spline3d = stripCurveUnits(normalizeSplineDefinition(curveDefinition));
            splineIsExact = true;
        }
        else
        {
            try
            {
                spline3d = stripCurveUnits(normalizeSplineDefinition(evApproximateBSplineCurve(context, {
                                    "edge" : edge,
                                    "tolerance" : 1e-6
                                })));
            }
        }
        // Convexity is defined only between two faces; a sheet-boundary edge is not asked
        // (the kernel reports BAD_GEOMETRY for it, and any notice suppresses the MCP test
        // harness's console output).
        const adjacentFaces = evaluateQuery(context, qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE));
        var convexity = undefined;
        if (size(adjacentFaces) == 2)
        {
            convexity = evEdgeConvexity(context, { "edge" : edge });
        }

        // Side assignment by the kernel's own convention: with usingFaceOrientation true the
        // returned tangent plane's x axis is the walking direction that keeps that face on the
        // left, so its sign against the edge's default tangent decides the side.
        const defaultTangentDirection = evEdgeTangentLine(context, { "edge" : edge, "parameter" : 0.5 }).direction;
        var leftFaceQuery = undefined;
        var rightFaceQuery = undefined;
        for (var adjacentFace in adjacentFaces)
        {
            const orientedTangentX = evFaceTangentPlanesAtEdge(context, {
                            "edge" : edge,
                            "face" : adjacentFace,
                            "parameters" : [0.5],
                            "usingFaceOrientation" : true
                        })[0].x;
            if (dot(orientedTangentX, defaultTangentDirection) > 0)
            {
                if (leftFaceQuery == undefined)
                {
                    leftFaceQuery = adjacentFace;
                }
                else
                {
                    rightFaceQuery = adjacentFace;
                    println("[SWEEP EXTRACTION] edge " ~ edgeIndex ~ ": both adjacent faces " ~
                        "classified left; second assigned right by order.");
                }
            }
            else
            {
                if (rightFaceQuery == undefined)
                {
                    rightFaceQuery = adjacentFace;
                }
                else
                {
                    leftFaceQuery = adjacentFace;
                    println("[SWEEP EXTRACTION] edge " ~ edgeIndex ~ ": both adjacent faces " ~
                        "classified right; second assigned left by order.");
                }
            }
        }
        const leftSide = extractCoEdgeSide(context, edge, leftFaceQuery, faceRecords, sampleParameters);
        const rightSide = extractCoEdgeSide(context, edge, rightFaceQuery, faceRecords, sampleParameters);
        const pointsSource = leftSide != undefined ? leftSide : rightSide;

        records[edgeIndex] = {
            "edgeIndex" : edgeIndex,
            "edgeQuery" : edge,
            "curveClass" : curveClass,
            "analyticCurve" : (curveClass == SweepCurveClass.LINE || curveClass == SweepCurveClass.CIRCLE ||
                        curveClass == SweepCurveClass.ELLIPSE) ? curveDefinition : undefined,
            "spline3d" : spline3d,
            "splineIsExact" : splineIsExact,
            "convexity" : convexity,
            "faceIndexLeft" : leftSide == undefined ? undefined : leftSide.faceIndex,
            "faceIndexRight" : rightSide == undefined ? undefined : rightSide.faceIndex,
            "sampleParameters" : sampleParameters,
            "edgePoints" : pointsSource == undefined ? undefined : pointsSource.edgePoints,
            "edgeTangents" : pointsSource == undefined ? undefined : pointsSource.edgeTangents,
            "sideNormals" : {
                "left" : leftSide == undefined ? undefined : leftSide.normals,
                "right" : rightSide == undefined ? undefined : rightSide.normals
            },
            "uvCurves" : {
                "left" : leftSide == undefined ? undefined : leftSide.uvCurve,
                "right" : rightSide == undefined ? undefined : rightSide.uvCurve
            }
        };
    }
    return records;
}

/**
 * Read every vertex of `toolBody` into a VertexRecord assembled purely from the co-edge
 * records' end samples - the cone-of-normals data costs no additional kernel evaluator calls.
 *
 * Each record: {
 *     vertexIndex {number}, vertexQuery {Query},
 *     point {Vector} : unit-stripped position,
 *     adjacentEdges {array} : indices into coEdgeRecords,
 *     coneNormals {array} : one-sided unit normals of the incident faces at this vertex,
 *         deduplicated by direction
 * }
 */
export function extractVertexRecords(context is Context, toolBody is Query, coEdgeRecords is array) returns array
{
    const vertices = evaluateQuery(context, qOwnedByBody(toolBody, EntityType.VERTEX));
    var records = makeArray(size(vertices));
    for (var vertexIndex = 0; vertexIndex < size(vertices); vertexIndex += 1)
    {
        const vertex = vertices[vertexIndex];
        const point = (1 / meter) * evVertexPoint(context, { "vertex" : vertex });
        const adjacentEdgeQueries = evaluateQuery(context, qAdjacent(vertex, AdjacencyType.VERTEX, EntityType.EDGE));
        var adjacentEdges = [];
        var coneNormals = [];
        for (var adjacentEdgeQuery in adjacentEdgeQueries)
        {
            const edgeIndex = edgeIndexForQuery(coEdgeRecords, adjacentEdgeQuery);
            if (edgeIndex == undefined)
            {
                continue;
            }
            adjacentEdges = append(adjacentEdges, edgeIndex);
            const coEdgeRecord = coEdgeRecords[edgeIndex];
            if (coEdgeRecord.edgePoints == undefined)
            {
                continue;
            }
            const lastSampleIndex = size(coEdgeRecord.edgePoints) - 1;
            const endIndex = squaredNorm(point - coEdgeRecord.edgePoints[0]) <=
                squaredNorm(point - coEdgeRecord.edgePoints[lastSampleIndex]) ? 0 : lastSampleIndex;
            if (coEdgeRecord.sideNormals.left != undefined)
            {
                coneNormals = appendUniqueDirection(coneNormals, coEdgeRecord.sideNormals.left[endIndex]);
            }
            if (coEdgeRecord.sideNormals.right != undefined)
            {
                coneNormals = appendUniqueDirection(coneNormals, coEdgeRecord.sideNormals.right[endIndex]);
            }
        }
        records[vertexIndex] = {
            "vertexIndex" : vertexIndex,
            "vertexQuery" : vertex,
            "point" : point,
            "adjacentEdges" : adjacentEdges,
            "coneNormals" : coneNormals
        };
    }
    return records;
}

/** One line of per-edge class, convexity, sides, and pcurve residuals for the printouts. */
export function summarizeCoEdgeRecords(records is array) returns string
{
    var summary = size(records) ~ " edge(s):";
    for (var record in records)
    {
        var pcurveNote = "";
        if (record.uvCurves.left != undefined)
        {
            pcurveNote = pcurveNote ~ " pcL " ~ record.uvCurves.left.maxResidual;
        }
        if (record.uvCurves.right != undefined)
        {
            pcurveNote = pcurveNote ~ " pcR " ~ record.uvCurves.right.maxResidual;
        }
        summary = summary ~ " [" ~ record.edgeIndex ~ "] " ~ record.curveClass ~ " " ~ record.convexity ~
            " L" ~ record.faceIndexLeft ~ "/R" ~ record.faceIndexRight ~ pcurveNote ~ ";";
    }
    return summary;
}

/**
 * Build the UV crossing data for one spline-bearing face record. Probes sample points of the
 * stripped spline, pairs each with the bbox-normalized face UV that evDistance reports for the
 * same location, fits a least-squares kernel-to-knot-domain affine map with the last usable
 * sample held out, and certifies it by 3D residual.
 *
 * Returns {
 *     isAffine {boolean} : true when the affine map's fit AND held-out residuals are inside
 *         residualCap - only then may kernelUvToKnotUv be used; otherwise cross by 3D point
 *         through invertPointOnSurface,
 *     kernelToKnot {map} : { matrix, offset } when the fit produced a map, else undefined,
 *     maxFitResidual {number}, validationResidual {number} : 3D residuals in meters
 *         (validationResidual is undefined without a spare sample),
 *     usableSampleCount {number}, sampleCount {number}
 * }
 */
export function buildUvCalibration(context is Context, faceQuery is Query, strippedSurface is map) returns map
{
    const domain = surfaceKnotDomain(strippedSurface);
    // Asymmetric interior spots so an axis swap or flip cannot masquerade as the identity map.
    const sampleFractions = [
            vector(0.15, 0.30), vector(0.35, 0.75), vector(0.55, 0.20),
            vector(0.80, 0.60), vector(0.70, 0.85), vector(0.45, 0.50)
        ];
    const sampleCount = size(sampleFractions);
    // Samples whose nearest face point lies farther than this are off the face (in a
    // trimmed-away region of the underlying surface) and are excluded from the fit; the same
    // cap certifies the affine residuals.
    const residualCap = 1e-5;

    var knotParameters = makeArray(sampleCount);
    var kernelParameters = makeArray(sampleCount);
    var witnessPoints = makeArray(sampleCount); // plain-number 3D points, meters implied
    var sampleIsUsable = makeArray(sampleCount);
    var usableCount = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        const uParameter = domain.uStart + (domain.uEnd - domain.uStart) * sampleFractions[sampleIndex][0];
        const vParameter = domain.vStart + (domain.vEnd - domain.vStart) * sampleFractions[sampleIndex][1];
        knotParameters[sampleIndex] = vector(uParameter, vParameter);
        const surfacePoint = evaluateBSplineSurfacePoint(strippedSurface, uParameter, vParameter);

        const distanceResult = evDistance(context, {
                    "side0" : faceQuery,
                    "side1" : meter * surfacePoint
                });
        const faceSide = distanceResult.sides[0];
        const parameterIsTwoVector = faceSide.parameter is Vector && size(faceSide.parameter) == 2;
        sampleIsUsable[sampleIndex] = parameterIsTwoVector &&
            (distanceResult.distance / meter) < residualCap;
        if (parameterIsTwoVector)
        {
            kernelParameters[sampleIndex] = vector(faceSide.parameter[0], faceSide.parameter[1]);
        }
        witnessPoints[sampleIndex] = (1 / meter) * faceSide.point;
        if (sampleIsUsable[sampleIndex])
        {
            usableCount += 1;
        }
    }

    var calibration = {
        "isAffine" : false,
        "kernelToKnot" : undefined,
        "maxFitResidual" : undefined,
        "validationResidual" : undefined,
        "usableSampleCount" : usableCount,
        "sampleCount" : sampleCount
    };
    if (usableCount < 4)
    {
        return calibration;
    }

    var usableKernelParameters = makeArray(usableCount);
    var usableKnotParameters = makeArray(usableCount);
    var usableWitnessPoints = makeArray(usableCount);
    var usableCursor = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        if (sampleIsUsable[sampleIndex])
        {
            usableKernelParameters[usableCursor] = kernelParameters[sampleIndex];
            usableKnotParameters[usableCursor] = knotParameters[sampleIndex];
            usableWitnessPoints[usableCursor] = witnessPoints[sampleIndex];
            usableCursor += 1;
        }
    }

    const holdOutValidation = usableCount >= 5;
    const fitCount = holdOutValidation ? usableCount - 1 : usableCount;
    var fitSourcePoints = makeArray(fitCount);
    var fitTargetPoints = makeArray(fitCount);
    for (var fitIndex = 0; fitIndex < fitCount; fitIndex += 1)
    {
        fitSourcePoints[fitIndex] = usableKernelParameters[fitIndex];
        fitTargetPoints[fitIndex] = usableKnotParameters[fitIndex];
    }
    calibration.kernelToKnot = fitTwoDimensionalAffineMap(fitSourcePoints, fitTargetPoints);
    if (calibration.kernelToKnot == undefined)
    {
        return calibration;
    }

    var maxFitResidual = 0;
    var validationResidual = 0;
    for (var usableIndex = 0; usableIndex < usableCount; usableIndex += 1)
    {
        const mapped = applyTwoDimensionalAffineMap(calibration.kernelToKnot, usableKernelParameters[usableIndex]);
        const clampedU = min(max(mapped[0], domain.uStart), domain.uEnd);
        const clampedV = min(max(mapped[1], domain.vStart), domain.vEnd);
        const residual = norm(evaluateBSplineSurfacePoint(strippedSurface, clampedU, clampedV) -
            usableWitnessPoints[usableIndex]);
        if (holdOutValidation && usableIndex == usableCount - 1)
        {
            validationResidual = residual;
        }
        else if (residual > maxFitResidual)
        {
            maxFitResidual = residual;
        }
    }
    calibration.maxFitResidual = maxFitResidual;
    calibration.validationResidual = holdOutValidation ? validationResidual : undefined;
    calibration.isAffine = maxFitResidual < residualCap &&
        (!holdOutValidation || validationResidual < residualCap);
    return calibration;
}

/**
 * Map one bbox-normalized kernel UV into the record's knot domain through a certified affine
 * calibration. Throws when the calibration is not affine - the caller must cross by 3D point
 * (invertPointOnSurface) on such faces instead.
 */
export function kernelUvToKnotUv(calibration is map, kernelUv is Vector) returns Vector
{
    if (calibration.isAffine != true)
    {
        throw "solidSweepUtils emit: kernelUvToKnotUv called on a face whose kernel-to-knot map is not " ~
            "affine (fit residual " ~ calibration.maxFitResidual ~ " m). Cross by 3D point with " ~
            "invertPointOnSurface on this face.";
    }
    return applyTwoDimensionalAffineMap(calibration.kernelToKnot, kernelUv);
}

/**
 * Newton point inversion on a stripped surface: find the knot-domain (u, v) whose surface point
 * is nearest `targetPoint` (a plain-number 3D point, meters implied), starting from `seedUv`.
 * Full second-order Newton on (S - P) . Su = 0, (S - P) . Sv = 0; periodic directions wrap,
 * clamped directions clamp.
 *
 * Returns { uv {Vector}, residual {number} : |S(uv) - target| in meters, converged {boolean},
 * iterations {number} }. `converged` means the iteration settled - the step shrank below 1e-12
 * of the domain span, the residual hit machine zero, or no fraction of the Newton step improved
 * the residual (a stationary point of the distance function, which need not be the global
 * nearest point on a closed surface). Callers gate on `residual`, not `converged` alone; use
 * invertPointOnSurfaceFromGrid when no trustworthy seed is available.
 */
export function invertPointOnSurface(strippedSurface is map, targetPoint is Vector, seedUv is Vector) returns map
{
    return invertPointOnSurface(strippedSurface, targetPoint, seedUv, {});
}

/**
 * Same, with the convergence knob a caller who only wants the DISTANCE should turn.
 *
 * `parameterTolerance` (default 1e-12, relative to the larger knot period) is the step size the
 * Newton iteration stops below. That default is right for a caller that wants the PARAMETER -
 * the edge-sample inversion does, because a pcurve is built out of it. It is two wasted
 * iterations for a caller that wants the residual, and the fit's certification is nine of this
 * function's eleven call sites and reads nothing but `.residual`.
 *
 * Loosening it is safe only up to a point, and the point is set by the DISTANCE rather than by
 * the surface's curvature: for a target essentially on the surface at distance d, moving s along
 * the surface gives sqrt(d^2 + s^2) ~ d + s^2 / 2d, so the requirement is s << d. See
 * `CERTIFICATION_INVERSION`, which carries the measurement that settled it - 1e-6 moved the
 * reported deviation by 3.5x, 1e-9 does not move it at all. Demanding 1e-12 buys nothing beyond
 * that and costs a full order-2 surface evaluation per extra iteration (spec 11.4: this function
 * was 19% of the profiled build).
 */
export function invertPointOnSurface(strippedSurface is map, targetPoint is Vector, seedUv is Vector,
    options is map) returns map
{
    const domain = surfaceKnotDomain(strippedSurface);
    const uPeriod = domain.uEnd - domain.uStart;
    const vPeriod = domain.vEnd - domain.vStart;
    const uIsPeriodic = strippedSurface.isUPeriodic == true;
    const vIsPeriodic = strippedSurface.isVPeriodic == true;
    const stepTolerance = (options.parameterTolerance == undefined ? 1e-12 : options.parameterTolerance) *
        max(uPeriod, vPeriod);
    const maximumIterations = 20;

    const target = [targetPoint[0], targetPoint[1], targetPoint[2]];
    var u = seedUv[0];
    var v = seedUv[1];
    var residualVector = leanResidual(leanSurfacePoint(strippedSurface, u, v), target);
    var converged = false;
    var iterationCount = 0;
    for (var iteration = 0; iteration < maximumIterations; iteration += 1)
    {
        iterationCount = iteration + 1;
        const derivatives = leanSurfaceDerivatives(strippedSurface, u, v, 2);
        residualVector = leanResidual(derivatives[0], target);
        const currentSquaredResidual = dotTriples(residualVector, residualVector);
        if (currentSquaredResidual < 1e-28)
        {
            converged = true;
            break;
        }
        const uTangent = derivatives[1];
        const vTangent = derivatives[2];
        const fValue = dotTriples(residualVector, uTangent);
        const gValue = dotTriples(residualVector, vTangent);
        const j11 = dotTriples(uTangent, uTangent) + dotTriples(residualVector, derivatives[3]);
        const j12 = dotTriples(uTangent, vTangent) + dotTriples(residualVector, derivatives[4]);
        const j22 = dotTriples(vTangent, vTangent) + dotTriples(residualVector, derivatives[5]);
        const determinant = j11 * j22 - j12 * j12;
        if (abs(determinant) < 1e-300)
        {
            break;
        }
        const fullUStep = (j12 * gValue - j22 * fValue) / determinant;
        const fullVStep = (j12 * fValue - j11 * gValue) / determinant;
        if (abs(fullUStep) < stepTolerance && abs(fullVStep) < stepTolerance)
        {
            converged = true;
            break;
        }

        // Damped step: halve until the 3D residual does not increase, so the iteration can
        // neither diverge nor hop into a farther stationary point's basin.
        var stepScale = 1;
        var stepAccepted = false;
        var candidateU = u;
        var candidateV = v;
        for (var damping = 0; damping < 5; damping += 1)
        {
            candidateU = u + stepScale * fullUStep;
            candidateV = v + stepScale * fullVStep;
            if (uIsPeriodic)
            {
                candidateU = domain.uStart + positiveModulo(candidateU - domain.uStart, uPeriod);
            }
            else
            {
                candidateU = min(max(candidateU, domain.uStart), domain.uEnd);
            }
            if (vIsPeriodic)
            {
                candidateV = domain.vStart + positiveModulo(candidateV - domain.vStart, vPeriod);
            }
            else
            {
                candidateV = min(max(candidateV, domain.vStart), domain.vEnd);
            }
            const candidateResidual = leanResidual(leanSurfacePoint(strippedSurface, candidateU, candidateV), target);
            if (dotTriples(candidateResidual, candidateResidual) <= currentSquaredResidual)
            {
                stepAccepted = true;
                break;
            }
            stepScale /= 2;
        }
        if (!stepAccepted)
        {
            // No fraction of the Newton step improves the residual: a stationary point.
            converged = true;
            break;
        }
        u = candidateU;
        v = candidateV;
    }
    residualVector = leanResidual(leanSurfacePoint(strippedSurface, u, v), target);
    return {
            "uv" : vector(u, v),
            "residual" : normTriple(residualVector),
            "converged" : converged,
            "iterations" : iterationCount
        };
}

/**
 * Robust point inversion with no caller-supplied seed: evaluate a gridCount x gridCount lattice
 * over the knot domain, run invertPointOnSurface from the best few distinct lattice cells, and
 * return the best result. Newton alone converges to whichever stationary point of the distance
 * function owns its seed's basin (a closed surface has several); the multi-seed retry is what
 * makes the answer the global nearest point. Callers marching along a curve should seed
 * invertPointOnSurface directly from the previous solution instead.
 */
export function invertPointOnSurfaceFromGrid(strippedSurface is map, targetPoint is Vector, gridCount is number) returns map
{
    const domain = surfaceKnotDomain(strippedSurface);
    const seedCount = 3;
    var seedUvs = makeArray(seedCount, undefined);
    var seedSquaredDistances = makeArray(seedCount, undefined);
    for (var uIndex = 0; uIndex < gridCount; uIndex += 1)
    {
        const u = domain.uStart + (domain.uEnd - domain.uStart) * (uIndex + 0.5) / gridCount;
        for (var vIndex = 0; vIndex < gridCount; vIndex += 1)
        {
            const v = domain.vStart + (domain.vEnd - domain.vStart) * (vIndex + 0.5) / gridCount;
            const offset = leanResidual(leanSurfacePoint(strippedSurface, u, v), targetPoint);
            const squaredDistance = dotTriples(offset, offset);
            for (var rank = 0; rank < seedCount; rank += 1)
            {
                if (seedSquaredDistances[rank] == undefined || squaredDistance < seedSquaredDistances[rank])
                {
                    for (var shift = seedCount - 1; shift > rank; shift -= 1)
                    {
                        seedSquaredDistances[shift] = seedSquaredDistances[shift - 1];
                        seedUvs[shift] = seedUvs[shift - 1];
                    }
                    seedSquaredDistances[rank] = squaredDistance;
                    seedUvs[rank] = vector(u, v);
                    break;
                }
            }
        }
    }
    var best = undefined;
    for (var rank = 0; rank < seedCount; rank += 1)
    {
        if (seedUvs[rank] == undefined)
        {
            continue;
        }
        const attempt = invertPointOnSurface(strippedSurface, targetPoint, seedUvs[rank]);
        if (best == undefined || attempt.residual < best.residual)
        {
            best = attempt;
        }
        if (best.residual < 1e-12)
        {
            break;
        }
    }
    return best;
}

/** The clamped evaluation domain of a stripped surface: { uStart, uEnd, vStart, vEnd }. */
export function surfaceKnotDomain(surface is map) returns map
{
    return {
            "uStart" : surface.uKnots[surface.uDegree],
            "uEnd" : surface.uKnots[size(surface.uKnots) - surface.uDegree - 1],
            "vStart" : surface.vKnots[surface.vDegree],
            "vEnd" : surface.vKnots[size(surface.vKnots) - surface.vDegree - 1]
        };
}

/**
 * The census-ready trim loops of one face record: what spec section 6.3 step 3 masks its
 * coarse sign grid with. Extraction records trim data in two different shapes and neither is a
 * polyline, which is the gap this closes.
 *
 * Source selection follows what the record actually carries:
 *   - "approximation": the 2D B-spline trim curves evApproximateBSplineSurface returned
 *     alongside the surface. They live in the parameter space of the surface from that same
 *     call, which is the record's spline up to the periodic re-spelling normalizeSurfaceDefinition
 *     applies - a conversion that preserves parameter VALUES and can only shift a periodic
 *     domain by whole periods, which the fold below absorbs.
 *   - "coEdges": the pcurve sample arrays of every co-edge side that names this face. This is
 *     the only source for an exactly extracted face, whose surface came from
 *     evSurfaceDefinition and has no loops attached.
 *   - "untrimmed": neither is present, so the whole knot rectangle is valid and the census
 *     needs no mask at all. "noSpline" is the analytic-face answer, which the coarse grid
 *     never sees.
 *
 * Curves from every loop are pooled and chained together rather than trusted in the groups the
 * kernel returned them in: `evApproximateBSplineSurface` documents outer and inner loops as
 * not clearly defined on a periodic face, and the even-odd mask does not need to know which
 * loop is which anyway.
 *
 * options: { polylineTolerance, joinTolerance {number} } - both optional, defaulting to the
 * fractions above times the smaller uv domain span.
 *
 * Returns {
 *     loops {array} : one { points {array of 2D Vector}, winding {number} } per loop, exactly
 *         the shape censusFunnelComponents accepts,
 *     source {string},
 *     loopCount, openLoopCount, windingLoopCount {number},
 *     worstClosureGap {number} : the largest distance a chain's tail landed from its head,
 *     worstCertifiedBound {number} : the largest held-out chord deviation over all curves
 *         (zero on the co-edge path, where the samples ARE the data),
 *     maxChordLength {number} : the longest polyline segment, for comparison against the
 *         census cell size,
 *     usable {boolean} : no open chains, so every loop bounds something,
 *     vPeriodicUnhandled {boolean} : the face is periodic in V, which the masks do not model.
 *         Only u is cyclic downstream - the census flags a seam in u, and section 7.8's
 *         transposeSurface normalizes an extracted revolve's circumferential direction INTO u
 *         for exactly that reason - so a v-periodic face here means the transpose was skipped.
 *         Loops are still folded into the v domain, but a loop WRAPPING the v seam would be
 *         read as a self-closing one, which is why this is reported rather than guessed at
 * }
 */
export function buildFaceTrimLoops(faceRecord is map, coEdgeRecords is array, options is map) returns map
{
    if (faceRecord.spline == undefined)
    {
        // An analytic face never reaches the coarse sign grid: spec section 6.5 solves it in
        // closed form and trims it with its own boundary machinery.
        return emptyTrimLoopResult("noSpline");
    }
    const domain = surfaceKnotDomain(faceRecord.spline);
    const smallerSpan = min(domain.uEnd - domain.uStart, domain.vEnd - domain.vStart);
    const resolved = mergeMaps({
                "polylineTolerance" : TRIM_POLYLINE_TOLERANCE_FRACTION * smallerSpan,
                "joinTolerance" : TRIM_JOIN_TOLERANCE_FRACTION * smallerSpan
            }, options);

    var segments = [];
    var worstCertifiedBound = 0;
    var source = "untrimmed";
    if (faceRecord.trimLoops != undefined && trimCurveCount(faceRecord.trimLoops) > 0)
    {
        source = "approximation";
        const sampled = sampleTrimCurveLoops(faceRecord.trimLoops, resolved.polylineTolerance);
        segments = sampled.segments;
        worstCertifiedBound = sampled.worstCertifiedBound;
    }
    else
    {
        segments = coEdgePcurveSegments(faceRecord.faceIndex, coEdgeRecords);
        if (size(segments) > 0)
        {
            source = "coEdges";
        }
    }
    if (size(segments) == 0)
    {
        return emptyTrimLoopResult("untrimmed");
    }

    const uPeriodic = faceRecord.spline.isUPeriodic == true;
    const vPeriodic = faceRecord.spline.isVPeriodic == true;
    const uPeriod = domain.uEnd - domain.uStart;
    if (uPeriodic && source == "coEdges")
    {
        // Interior folding first, then the chainer handles the joins BETWEEN segments: a pcurve
        // whose edge crosses the seam comes back folded mid-array, because a step whose seeded
        // inversion is refused re-seeds from the in-domain grid, and no join comparison can see
        // a fold that sits inside a segment.
        //
        // ONLY the co-edge path. Folding is an artifact of point inversion; a kernel trim curve
        // is continuous in the surface's own parameter space by construction, and it may cross
        // the whole seam in ONE chord - a degree-1 uv line from (uStart, v) to (uEnd, v) samples
        // to exactly two points. Unwrapping reads that chord as a fold and collapses it.
        segments = unwrapSegmentsU(segments, uPeriod);
    }
    const chained = chainUvPolylinesIntoLoops(segments, resolved.joinTolerance, uPeriodic ? uPeriod : 0);

    var loops = makeArray(size(chained));
    var openLoopCount = 0;
    var windingLoopCount = 0;
    var worstClosureGap = 0;
    var maxChordLength = 0;
    for (var loopIndex = 0; loopIndex < size(chained); loopIndex += 1)
    {
        const chain = chained[loopIndex];
        var points = chain.points;
        if (uPeriodic)
        {
            points = shiftLoopIntoDomain(points, domain.uStart, uPeriod);
        }
        if (vPeriodic)
        {
            points = shiftLoopIntoDomainV(points, domain.vStart, domain.vEnd - domain.vStart);
        }
        const winding = uPeriodic ? chain.winding : 0;
        loops[loopIndex] = { "points" : points, "winding" : winding };
        if (!chain.closed)
        {
            openLoopCount += 1;
        }
        if (winding != 0)
        {
            windingLoopCount += 1;
        }
        worstClosureGap = max(worstClosureGap, chain.closureGap);
        maxChordLength = max(maxChordLength, longestChord(points, winding == 0));
    }
    return {
            "loops" : loops,
            "source" : source,
            "loopCount" : size(loops),
            "openLoopCount" : openLoopCount,
            "windingLoopCount" : windingLoopCount,
            "worstClosureGap" : worstClosureGap,
            "worstCertifiedBound" : worstCertifiedBound,
            "maxChordLength" : maxChordLength,
            "usable" : openLoopCount == 0,
            "vPeriodicUnhandled" : vPeriodic
        };
}

/** One line of trim loop counts, tolerances achieved, and usability for the printouts. */
export function summarizeTrimLoops(trimResult is map) returns string
{
    return trimResult.source ~ ": " ~ trimResult.loopCount ~ " loop(s), " ~
        trimResult.windingLoopCount ~ " winding, " ~ trimResult.openLoopCount ~ " open, gap " ~
        trimResult.worstClosureGap ~ ", chord bound " ~ trimResult.worstCertifiedBound ~
        ", longest chord " ~ trimResult.maxChordLength ~ ", usable " ~ trimResult.usable ~
        (trimResult.vPeriodicUnhandled ? " V-PERIODIC (untransposed)" : "");
}

/**
 * Sample a 2D uv trim curve into a polyline whose chord deviation is CERTIFIED: the curve is
 * sampled uniformly inside every distinct knot span and the count per span is doubled until the
 * held-out mid-parameter sample of every chord sits within `tolerance` of that chord.
 *
 * The held-out samples are the certification, the same way the fit certifies its rows (spec
 * 7.2): the points that decide the answer are never points the answer was built from. A
 * degree-1 trim curve - the kernel's usual answer for a straight boundary - certifies at one
 * segment per span with a zero bound, so a box's loops cost one evaluation each.
 *
 * Returns { points {array of 2D Vector}, certifiedBound {number}, samplesPerSpan {number} }.
 */
export function polylineFromUvCurve(uvCurve is map, tolerance is number) returns map
{
    const breaks = distinctSpanBreaks(uvCurve);
    var samplesPerSpan = 1;
    var sampling = sampleUvCurveUniformly(uvCurve, breaks, samplesPerSpan);
    while (sampling.certifiedBound > tolerance && samplesPerSpan < TRIM_POLYLINE_SAMPLE_CAP)
    {
        samplesPerSpan *= 2;
        sampling = sampleUvCurveUniformly(uvCurve, breaks, samplesPerSpan);
    }
    return sampling;
}

/** Chain with no periodic direction: every join is an ordinary uv distance. */
export function chainUvPolylinesIntoLoops(segments is array, joinTolerance is number) returns array
{
    return chainUvPolylinesIntoLoops(segments, joinTolerance, 0);
}

/**
 * Chain uv polyline segments into closed loops by nearest endpoint, reversing a segment when
 * its far end is the nearer one. Segments may arrive in any order and either direction, and
 * more than one loop may be present - a chain that returns to its own head ends that loop and
 * the next unused segment seeds the next, which separates a boundary from its holes without
 * anyone having to say which is which.
 *
 * Growing forward only is enough for closed input: a cycle traversed forward from any of its
 * segments comes back to that segment's head. A chain that stalls instead is therefore genuine
 * evidence of a gap upstream, and it is returned open, with the gap it stalled at, rather than
 * closed across it.
 *
 * `uPeriod` nonzero makes every join comparison use the NEAREST PERIODIC IMAGE in u, and shifts
 * each attached segment onto that image. Two things fall out of that. A trim curve running the
 * full seam joins its neighbour whose u values sit a period away - without this it would look
 * like a period-wide gap and the loop would be reported open. And the chain that results is
 * already unwrapped: it accumulated its shifts from the joins themselves, rather than from a
 * jump heuristic that cannot tell a fold from a chord spanning the seam.
 *
 * Each returned loop drops the repeated closing point, matching the census convention that
 * closure is implicit, and reports the WINDING it closed with: the number of periods between
 * its head and the image of its head that its tail landed on. Reading the winding off the join
 * is exact at any sample density, where re-deriving it from the truncated point list would need
 * the loop to be sampled finely enough that a lost closing segment is obviously short.
 *
 * Returns an array of { points, closureGap, closed, winding }.
 */
export function chainUvPolylinesIntoLoops(segments is array, joinTolerance is number, uPeriod is number) returns array
{
    const segmentCount = size(segments);
    const joinToleranceSquared = joinTolerance * joinTolerance;
    var totalPointCount = 0;
    for (var segment in segments)
    {
        totalPointCount += size(segment);
    }
    var used = makeArray(segmentCount, false);
    var loops = makeArray(segmentCount);
    var loopCount = 0;
    var usedCount = 0;
    var nextSeed = 0;
    while (usedCount < segmentCount)
    {
        while (used[nextSeed])
        {
            nextSeed += 1;
        }
        var chain = makeArray(totalPointCount, segments[nextSeed][0]);
        var chainLength = 0;
        for (var point in segments[nextSeed])
        {
            chain[chainLength] = point;
            chainLength += 1;
        }
        used[nextSeed] = true;
        usedCount += 1;

        var growing = true;
        while (growing)
        {
            growing = false;
            if (chainClosesHere(chain, chainLength, uPeriod, joinTolerance))
            {
                break;
            }
            var bestIndex = -1;
            var bestDistanceSquared = 0;
            var bestReversed = false;
            for (var candidate = 0; candidate < segmentCount; candidate += 1)
            {
                if (used[candidate])
                {
                    continue;
                }
                const candidatePoints = segments[candidate];
                const headDistanceSquared = nearestImageSquaredDistance(chain[chainLength - 1],
                        candidatePoints[0], uPeriod);
                const tailDistanceSquared = nearestImageSquaredDistance(chain[chainLength - 1],
                        candidatePoints[size(candidatePoints) - 1], uPeriod);
                const reversed = tailDistanceSquared < headDistanceSquared;
                const distanceSquared = reversed ? tailDistanceSquared : headDistanceSquared;
                if (bestIndex < 0 || distanceSquared < bestDistanceSquared)
                {
                    bestIndex = candidate;
                    bestDistanceSquared = distanceSquared;
                    bestReversed = reversed;
                }
            }
            if (bestIndex < 0 || bestDistanceSquared > joinToleranceSquared)
            {
                break;
            }
            const attached = segments[bestIndex];
            const attachedCount = size(attached);
            const joinEnd = bestReversed ? attached[attachedCount - 1] : attached[0];
            const uShift = nearestImageShift(chain[chainLength - 1], joinEnd, uPeriod);
            for (var offset = 1; offset < attachedCount; offset += 1)
            {
                const attachedPoint = bestReversed ? attached[attachedCount - 1 - offset] : attached[offset];
                chain[chainLength] = uShift == 0 ? attachedPoint :
                    vector(attachedPoint[0] + uShift, attachedPoint[1]);
                chainLength += 1;
            }
            used[bestIndex] = true;
            usedCount += 1;
            growing = true;
        }

        const closureShift = nearestImageShift(chain[chainLength - 1], chain[0], uPeriod);
        const closureGap = sqrt(nearestImageSquaredDistance(chain[chainLength - 1], chain[0], uPeriod));
        const winding = uPeriod == 0 ? 0 : round(closureShift / uPeriod);
        const closed = chainClosesHere(chain, chainLength, uPeriod, joinTolerance);
        // A loop that closes on itself repeats its head as its tail, and the census convention
        // is implicit closure, so that repeat comes off. A WINDING loop's tail is a different
        // point - its head one period along - and dropping it would delete the segment that
        // covers the seam, leaving a stretch of u where the mask counts no crossings at all.
        loops[loopCount] = {
                "points" : subArray(chain, 0, (closed && winding == 0) ? chainLength - 1 : chainLength),
                "closureGap" : closureGap,
                "closed" : closed,
                "winding" : winding
            };
        loopCount += 1;
    }
    return subArray(loops, 0, loopCount);
}

/**
 * Unwrap a loop's u values into one continuous run: whenever consecutive samples jump by more
 * than half the period, every later sample is shifted by a whole period.
 *
 * A trim loop that crosses the extraction seam arrives with its u values folded into the
 * domain, and folded coordinates turn its seam segment into a period-long jump that no
 * crossing test can read. Unwrapped, a loop that merely straddles the seam runs a little past
 * the domain edge (the cyclic mask tests every periodic image, so that is fine) and a loop that
 * wraps the seam ends one whole period from where it started, which is what makes its winding
 * measurable.
 */
export function unwrapLoopU(loopPoints is array, uPeriod is number) returns array
{
    const pointCount = size(loopPoints);
    var unwrapped = makeArray(pointCount, loopPoints[0]);
    var shift = 0;
    for (var index = 1; index < pointCount; index += 1)
    {
        const rawDelta = loopPoints[index][0] - loopPoints[index - 1][0];
        if (rawDelta > 0.5 * uPeriod)
        {
            shift -= uPeriod;
        }
        else if (rawDelta < -0.5 * uPeriod)
        {
            shift += uPeriod;
        }
        unwrapped[index] = vector(loopPoints[index][0] + shift, loopPoints[index][1]);
    }
    return unwrapped;
}

/**
 * How many times a loop winds the periodic u direction: how far its stored u travelled from
 * first sample to last, read against the period.
 *
 * Zero means the loop closes on itself - a hole, or the boundary of a face that is not closed
 * in u - so its implicit closing segment is part of the polygon. Plus or minus one means the
 * loop wraps the seam: its stored samples already span a full period, its two ends are the same
 * point one period apart, and there is no closing segment to add. That distinction is the whole
 * difference between the two kinds of loop a periodic face produces, and it is why the loops
 * carry it rather than leaving the census to guess.
 *
 * Only meaningful on UNWRAPPED input. Folded u telescopes back to nearly zero however the loop
 * runs, so a winding loop read straight out of the kernel reports zero - unwrapLoopU first.
 */
export function loopUWinding(loopPoints is array, uPeriod is number) returns number
{
    const travel = loopPoints[size(loopPoints) - 1][0] - loopPoints[0][0];
    if (abs(abs(travel) - uPeriod) < 0.5 * uPeriod)
    {
        return travel > 0 ? 1 : -1;
    }
    return 0;
}

/** The result for a face with nothing to mask: an empty loop set the census reads as all-valid. */
function emptyTrimLoopResult(source is string) returns map
{
    return {
            "loops" : [],
            "source" : source,
            "loopCount" : 0,
            "openLoopCount" : 0,
            "windingLoopCount" : 0,
            "worstClosureGap" : 0,
            "worstCertifiedBound" : 0,
            "maxChordLength" : 0,
            "usable" : true,
            "vPeriodicUnhandled" : false
        };
}

/** Total 2D curves across a record's boundary loop and its inner loops. */
function trimCurveCount(trimLoops is map) returns number
{
    var total = trimLoops.boundary == undefined ? 0 : size(trimLoops.boundary);
    if (trimLoops.inner != undefined)
    {
        for (var innerLoop in trimLoops.inner)
        {
            total += size(innerLoop);
        }
    }
    return total;
}

/** Every trim curve of a record, boundary and inner pooled, sampled to certified polylines. */
function sampleTrimCurveLoops(trimLoops is map, tolerance is number) returns map
{
    var curves = makeArray(trimCurveCount(trimLoops));
    var curveCount = 0;
    if (trimLoops.boundary != undefined)
    {
        for (var curve in trimLoops.boundary)
        {
            curves[curveCount] = curve;
            curveCount += 1;
        }
    }
    if (trimLoops.inner != undefined)
    {
        for (var innerLoop in trimLoops.inner)
        {
            for (var curve in innerLoop)
            {
                curves[curveCount] = curve;
                curveCount += 1;
            }
        }
    }
    var segments = makeArray(curveCount);
    var worstCertifiedBound = 0;
    for (var curveIndex = 0; curveIndex < curveCount; curveIndex += 1)
    {
        const sampled = polylineFromUvCurve(curves[curveIndex], tolerance);
        segments[curveIndex] = sampled.points;
        worstCertifiedBound = max(worstCertifiedBound, sampled.certifiedBound);
    }
    return { "segments" : segments, "worstCertifiedBound" : worstCertifiedBound };
}

/**
 * The pcurve sample arrays of every co-edge side naming this face. A seam edge names the same
 * face on both sides and contributes both, which is correct: on a face whose domain carries the
 * seam as two opposite edges, both are part of the boundary.
 */
function coEdgePcurveSegments(faceIndex is number, coEdgeRecords is array) returns array
{
    var segments = makeArray(2 * size(coEdgeRecords));
    var segmentCount = 0;
    for (var record in coEdgeRecords)
    {
        if (record.faceIndexLeft == faceIndex && record.uvCurves.left != undefined)
        {
            segments[segmentCount] = record.uvCurves.left.uvSamples;
            segmentCount += 1;
        }
        if (record.faceIndexRight == faceIndex && record.uvCurves.right != undefined)
        {
            segments[segmentCount] = record.uvCurves.right.uvSamples;
            segmentCount += 1;
        }
    }
    return subArray(segments, 0, segmentCount);
}

/** The distinct knot values bounding a curve's spans, both domain ends included. */
function distinctSpanBreaks(uvCurve is map) returns array
{
    const domain = knotDomain(uvCurve.knots, uvCurve.degree);
    var breaks = makeArray(size(uvCurve.knots), domain.start);
    var breakCount = 1;
    for (var knot in uvCurve.knots)
    {
        if (knot > domain.start + KNOT_PARAMETER_TOLERANCE && knot < domain.end - KNOT_PARAMETER_TOLERANCE &&
            knot > breaks[breakCount - 1] + KNOT_PARAMETER_TOLERANCE)
        {
            breaks[breakCount] = knot;
            breakCount += 1;
        }
    }
    breaks[breakCount] = domain.end;
    return subArray(breaks, 0, breakCount + 1);
}

/**
 * One pass of the certified sampler: `samplesPerSpan` chords inside every span, plus the
 * held-out mid-parameter evaluation of each chord that measures the bound.
 */
function sampleUvCurveUniformly(uvCurve is map, breaks is array, samplesPerSpan is number) returns map
{
    const spanCount = size(breaks) - 1;
    const pointCount = spanCount * samplesPerSpan + 1;
    var parameters = makeArray(pointCount, breaks[spanCount]);
    for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
    {
        for (var offset = 0; offset < samplesPerSpan; offset += 1)
        {
            parameters[spanIndex * samplesPerSpan + offset] = breaks[spanIndex] +
                (breaks[spanIndex + 1] - breaks[spanIndex]) * offset / samplesPerSpan;
        }
    }
    var points = makeArray(pointCount, vector(0, 0));
    for (var index = 0; index < pointCount; index += 1)
    {
        points[index] = evaluateBSplineCurveDerivatives(uvCurve, parameters[index], 0)[0];
    }
    var worstDeviationSquared = 0;
    for (var index = 0; index < pointCount - 1; index += 1)
    {
        const heldOut = evaluateBSplineCurveDerivatives(uvCurve,
                0.5 * (parameters[index] + parameters[index + 1]), 0)[0];
        worstDeviationSquared = max(worstDeviationSquared,
            squaredNorm(heldOut - 0.5 * (points[index] + points[index + 1])));
    }
    return {
            "points" : points,
            "certifiedBound" : sqrt(worstDeviationSquared),
            "samplesPerSpan" : samplesPerSpan
        };
}

/**
 * Shift a whole unwrapped loop by an integer number of periods so its first sample lands in
 * [domainStart, domainStart + period). Shifting the loop as a unit is the point: folding each
 * sample on its own would undo the unwrapping.
 */
function shiftLoopIntoDomain(loopPoints is array, domainStart is number, period is number) returns array
{
    const first = loopPoints[0][0];
    const shift = domainStart + positiveModulo(first - domainStart, period) - first;
    if (abs(shift) < KNOT_PARAMETER_TOLERANCE)
    {
        return loopPoints;
    }
    var shifted = makeArray(size(loopPoints), loopPoints[0]);
    for (var index = 0; index < size(loopPoints); index += 1)
    {
        shifted[index] = vector(loopPoints[index][0] + shift, loopPoints[index][1]);
    }
    return shifted;
}

/** The same whole-loop shift in the v direction, for the rare v-periodic face. */
function shiftLoopIntoDomainV(loopPoints is array, domainStart is number, period is number) returns array
{
    const first = loopPoints[0][1];
    const shift = domainStart + positiveModulo(first - domainStart, period) - first;
    if (abs(shift) < KNOT_PARAMETER_TOLERANCE)
    {
        return loopPoints;
    }
    var shifted = makeArray(size(loopPoints), loopPoints[0]);
    for (var index = 0; index < size(loopPoints); index += 1)
    {
        shifted[index] = vector(loopPoints[index][0], loopPoints[index][1] + shift);
    }
    return shifted;
}

/**
 * Whether a chain has come back to its own head. Three or more points close on proximity alone;
 * TWO points close only when they sit a whole period apart in u, which is the one legitimate
 * two-point loop - a trim running straight across the seam, which the kernel can hand over as a
 * single degree-1 chord. Without that case such a face reports an open loop and its whole mask
 * is refused.
 */
function chainClosesHere(chain is array, chainLength is number, uPeriod is number,
    joinTolerance is number) returns boolean
{
    if (chainLength < 2)
    {
        return false;
    }
    if (nearestImageSquaredDistance(chain[chainLength - 1], chain[0], uPeriod) > joinTolerance * joinTolerance)
    {
        return false;
    }
    return chainLength > 2 ||
        (uPeriod != 0 && nearestImageShift(chain[chainLength - 1], chain[0], uPeriod) != 0);
}

/** unwrapLoopU applied to every segment, fixing folding INSIDE a segment before any joining. */
function unwrapSegmentsU(segments is array, uPeriod is number) returns array
{
    var unwrapped = makeArray(size(segments));
    for (var index = 0; index < size(segments); index += 1)
    {
        unwrapped[index] = unwrapLoopU(segments[index], uPeriod);
    }
    return unwrapped;
}

/**
 * How far `candidate` is from `reference` once it is slid to its nearest periodic image in u.
 * `uPeriod` zero is the ordinary uv distance.
 */
function nearestImageSquaredDistance(reference is Vector, candidate is Vector, uPeriod is number) returns number
{
    const shift = nearestImageShift(reference, candidate, uPeriod);
    const deltaU = reference[0] - candidate[0] - shift;
    const deltaV = reference[1] - candidate[1];
    return deltaU * deltaU + deltaV * deltaV;
}

/** The whole number of periods to add to `candidate`'s u to bring it nearest `reference`. */
function nearestImageShift(reference is Vector, candidate is Vector, uPeriod is number) returns number
{
    if (uPeriod == 0)
    {
        return 0;
    }
    return round((reference[0] - candidate[0]) / uPeriod) * uPeriod;
}

/** The longest polyline segment, counting the implicit closing segment only when it exists. */
function longestChord(loopPoints is array, includeClosure is boolean) returns number
{
    const pointCount = size(loopPoints);
    var longestSquared = 0;
    for (var index = 1; index < pointCount; index += 1)
    {
        longestSquared = max(longestSquared, squaredNorm(loopPoints[index] - loopPoints[index - 1]));
    }
    if (includeClosure)
    {
        longestSquared = max(longestSquared, squaredNorm(loopPoints[0] - loopPoints[pointCount - 1]));
    }
    return sqrt(longestSquared);
}

/**
 * The exactly-rigid placement of the tool at one motion sample (spec 9.1).
 *
 * Input: an `evaluateMotionSample` map - `rotation` a unitless Matrix whose COLUMNS are the
 * moving frame's axes, `translation` a plain-number vector with meters implied.
 *
 * Returns { transform {Transform}, rigidityDefect {number} } where `rigidityDefect` is the
 * worst distance a unit tool-frame axis moves under the re-orthonormalization, in meters per
 * meter of tool radius - the epsilon_motion term of the section 2.3 error ledger.
 */
export function sweepCapPlacement(motionSample is map) returns map
{
    const rotation = motionSample.rotation;
    const sampledColumns = [
            vector(rotation[0][0], rotation[1][0], rotation[2][0]),
            vector(rotation[0][1], rotation[1][1], rotation[2][1]),
            vector(rotation[0][2], rotation[1][2], rotation[2][2])
        ];
    const rigidColumns = orthonormalizedFrame(sampledColumns);
    var rigidityDefect = 0;
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        rigidityDefect = max(rigidityDefect, norm(rigidColumns[columnIndex] - sampledColumns[columnIndex]));
    }
    const linear = matrix([
                [rigidColumns[0][0], rigidColumns[1][0], rigidColumns[2][0]],
                [rigidColumns[0][1], rigidColumns[1][1], rigidColumns[2][1]],
                [rigidColumns[0][2], rigidColumns[1][2], rigidColumns[2][2]]
            ]);
    return {
            "transform" : transform(linear, meter * motionSample.translation),
            "rigidityDefect" : rigidityDefect
        };
}

/**
 * One rigid copy of the tool body at a cap placement (spec 9.1). One opPattern call per cap,
 * rather than one call carrying every transform, so that each copy answers to its own
 * `qCreatedBy` and no downstream step has to pick instances apart.
 *
 * Returns the Query for the copied body.
 */
export function emitToolCapCopy(context is Context, id is Id, toolBody is Query, placement is map,
    instanceName is string) returns Query
{
    opPattern(context, id, {
                "entities" : toolBody,
                "transforms" : [placement.transform],
                "instanceNames" : [instanceName]
            });
    return qCreatedBy(id, EntityType.BODY);
}

/**
 * The contact curve where a fitted lateral patch meets one of its end caps: the fit surface's
 * own boundary CONTROL row (spec 9.1).
 *
 * The fit's rows are stations and its columns are the q direction, and the station direction
 * is clamped, so control row 0 IS the patch's t-start boundary curve and the last row IS its
 * t-end boundary curve - exactly, not to a tolerance. The curve inherits the q direction's
 * degree, knots, and periodicity.
 *
 * `atStart` picks the t-start row. Output is a stripped curve map (plain numbers, meters
 * implied) in this module's units contract.
 */
export function fitBoundaryContactCurve(fitSurface is map, atStart is boolean) returns map
{
    const rows = fitSurface.controlPoints;
    return {
            "degree" : fitSurface.vDegree,
            "isPeriodic" : fitSurface.isVPeriodic == true,
            "isRational" : false,
            "controlPoints" : atStart ? rows[0] : rows[size(rows) - 1],
            "knots" : fitSurface.vKnots
        };
}

/**
 * The kernel-facing BSplineCurve of a stripped contact curve: meters attached, and a periodic
 * curve converted to the CLOSED CLAMPED form the kernel returns and accepts (the surface
 * emission path's convention, exactly - handing the kernel this module's wrap-padded form is
 * how you earn a non-smooth periodic seam). The conversion runs on homogeneous points, so it
 * needs a unit weight column, which is dropped again on the way out so the curve stays
 * non-rational.
 *
 * `declarePeriodic` false emits the same geometry with the flag cleared: clamping is knot
 * insertion to full multiplicity at the seam, so the curve is unchanged and still closes.
 */
export function kernelContactCurve(strippedCurve is map, declarePeriodic is boolean) returns BSplineCurve
{
    const pointCount = size(strippedCurve.controlPoints);
    var withUnits = makeArray(pointCount);
    for (var pointIndex = 0; pointIndex < pointCount; pointIndex += 1)
    {
        withUnits[pointIndex] = meter * strippedCurve.controlPoints[pointIndex];
    }
    var emitted = strippedCurve;
    emitted.controlPoints = withUnits;
    if (emitted.isPeriodic == true)
    {
        emitted.weights = makeArray(pointCount, 1);
        emitted = toClosedClampedPeriodicForm(emitted);
    }
    return bSplineCurve({
                "degree" : emitted.degree,
                "isPeriodic" : declarePeriodic && emitted.isPeriodic == true,
                "controlPoints" : emitted.controlPoints,
                "knots" : emitted.knots is KnotArray ? emitted.knots : knotArray(emitted.knots)
            });
}

/**
 * One contact wire body from a stripped contact curve: the periodic declaration first, then
 * the closed-clamped non-periodic declaration if the kernel refuses it - the same two-attempt
 * discipline the patch emission uses, for the same reason (a closed curve is a shape the
 * kernel has never been asked for in this form, so it gets asked both ways rather than
 * assumed).
 *
 * Both attempts are guarded and each takes its own id. A refusal is REPORTED, never thrown:
 * spec 9.3 degrades to a named seam rather than taking the feature down.
 *
 * Returns { refused, reason, id, wireBody {Query}, edgeCount, declaredPeriodic }.
 */
export function emitContactWire(context is Context, id is Id, strippedCurve is map) returns map
{
    var reason = "";
    for (var declarePeriodic in [true, false])
    {
        const attemptName = declarePeriodic ? "periodic" : "clamped";
        const attemptId = id + attemptName;
        try
        {
            opCreateBSplineCurve(context, attemptId, {
                        "bSplineCurve" : kernelContactCurve(strippedCurve, declarePeriodic)
                    });
        }
        catch (error)
        {
            reason = reason ~ " " ~ attemptName ~ ": " ~ toString(error);
        }
        const edges = evaluateQuery(context, qCreatedBy(attemptId, EntityType.EDGE));
        if (size(edges) > 0)
        {
            return {
                    "refused" : false, "reason" : "", "id" : attemptId,
                    "wireBody" : qCreatedBy(attemptId, EntityType.BODY),
                    "edgeCount" : size(edges), "declaredPeriodic" : declarePeriodic
                };
        }
    }
    return {
            "refused" : true,
            "reason" : "SWEEP_CONTACT_WIRE_REFUSED: the kernel refused a degree " ~
            strippedCurve.degree ~ " contact curve of " ~ size(strippedCurve.controlPoints) ~
            " control points in both declarations -" ~ reason,
            "id" : undefined, "wireBody" : qNothing(), "edgeCount" : 0, "declaredPeriodic" : false
        };
}

/**
 * Imprint contact wires onto a cap's faces by PROJECTION (spec 9.1): `opSplitFace` with the
 * wires as `edgeTools` and `ProjectionType.NORMAL_TO_TARGET`.
 *
 * The grazing sheets are deliberately absent from this call. Envelope and cap are tangent
 * along the contact curve, and asking the kernel for a tangent surface-surface intersection
 * (which is what passing them as `bodyTools` would do) is its worst case; a projected wire
 * asks nothing of the kernel but a closest-point map.
 *
 * Returns { failed, reason, faceCountBefore, faceCountAfter, splittingEdges {array},
 * splitEdgeQuery {Query} }.
 */
export function imprintContactWires(context is Context, id is Id, capFaces is Query,
    wireEdges is Query) returns map
{
    const before = size(evaluateQuery(context, capFaces));
    var reason = "";
    var splittingEdges = [];
    try
    {
        splittingEdges = opSplitFace(context, id, {
                        "faceTargets" : capFaces,
                        "edgeTools" : wireEdges,
                        "projectionType" : ProjectionType.NORMAL_TO_TARGET
                    }).splittingEdges;
    }
    catch (error)
    {
        reason = "SWEEP_CAP_IMPRINT_FAILED: " ~ toString(error);
    }
    const after = size(evaluateQuery(context, capFaces));
    if (reason == "" && after <= before)
    {
        reason = "SWEEP_CAP_IMPRINT_FAILED: the contact wires left the cap's " ~ before ~
            " face(s) undivided.";
    }
    return {
            "failed" : reason != "", "reason" : reason,
            "faceCountBefore" : before, "faceCountAfter" : after,
            "splittingEdges" : splittingEdges,
            "splitEdgeQuery" : size(splittingEdges) > 0 ? qUnion(splittingEdges) : qNothing()
        };
}

/**
 * A tangent plane at a point KNOWN to lie inside a trimmed face: `evFaceTangentPlanes` with
 * `returnUndefinedOutsideFace`, over parameters walked centre outward in rings, taking the
 * first that lands. One kernel call per face, and no assumption that the middle of a face's
 * parameter box is on the face - after an imprint, half of them are not.
 *
 * Returns { found {boolean}, plane {Plane}, parameter {Vector} }.
 */
export function faceInteriorTangentPlane(context is Context, face is Query, ringCount is number) returns map
{
    const parameters = centreOutwardFaceParameters(ringCount);
    const planes = evFaceTangentPlanes(context, {
                "face" : face,
                "parameters" : parameters,
                "returnUndefinedOutsideFace" : true
            });
    for (var index = 0; index < size(planes); index += 1)
    {
        if (planes[index] != undefined)
        {
            return { "found" : true, "plane" : planes[index], "parameter" : parameters[index] };
        }
    }
    return { "found" : false, "plane" : undefined, "parameter" : undefined };
}

/**
 * The contact function's sign at a point of a moving rigid body: f = <n, v>, with
 * v = A'(t) A(t)^-1 (p - b(t)) + b'(t) the velocity of the material point currently at p.
 *
 * `worldPoint` is a plain-number position in meters (this module's units contract) and
 * `outwardNormal` a unit vector; `motionSample` is an `evaluateMotionSample` map. The returned
 * sign is in meters per unit t, so a caller comparing it against a floor should scale by the
 * local speed, which `capFaceClassification` does.
 */
export function envelopeContactSign(motionSample is map, worldPoint is Vector, outwardNormal is Vector) returns number
{
    return dot(outwardNormal, materialVelocityAt(motionSample, worldPoint));
}

/**
 * Classify every face of a cap by the sign of f at an interior sample (spec 9.1).
 *
 * `keepAdvancing` true keeps the faces the motion is carrying outward (f > 0) - the END cap's
 * leading half; false keeps the retreating ones (f < 0) - the START cap's trailing half. A
 * face whose |f| falls under `CAP_FACE_SIGN_RELATIVE_FLOOR` times the local speed is GRAZING:
 * the tool slides along it and it belongs to neither half, so it is counted and named rather
 * than guessed at.
 *
 * Returns { keep {array of Query}, discard {array of Query}, grazing {array of Query},
 * signs {array}, worstGrazingRatio, unsampled }.
 */
export function capFaceClassification(context is Context, capBody is Query, motionSample is map,
    keepAdvancing is boolean, ringCount is number) returns map
{
    const faces = evaluateQuery(context, qOwnedByBody(capBody, EntityType.FACE));
    var keep = [];
    var discard = [];
    var grazing = [];
    var signs = makeArray(size(faces), 0);
    var worstGrazingRatio = 0;
    var unsampled = 0;
    for (var faceIndex = 0; faceIndex < size(faces); faceIndex += 1)
    {
        const sample = faceInteriorTangentPlane(context, faces[faceIndex], ringCount);
        if (!sample.found)
        {
            unsampled += 1;
            grazing = append(grazing, faces[faceIndex]);
            continue;
        }
        const worldPoint = sample.plane.origin / meter;
        const speed = norm(materialVelocityAt(motionSample, worldPoint));
        const signedValue = envelopeContactSign(motionSample, worldPoint, sample.plane.normal);
        signs[faceIndex] = signedValue;
        const ratio = speed > 0 ? abs(signedValue) / speed : 0;
        if (ratio <= CAP_FACE_SIGN_RELATIVE_FLOOR)
        {
            worstGrazingRatio = max(worstGrazingRatio, ratio);
            grazing = append(grazing, faces[faceIndex]);
        }
        else if ((signedValue > 0) == keepAdvancing)
        {
            keep = append(keep, faces[faceIndex]);
        }
        else
        {
            discard = append(discard, faces[faceIndex]);
        }
    }
    return {
            "keep" : keep, "discard" : discard, "grazing" : grazing, "signs" : signs,
            "worstGrazingRatio" : worstGrazingRatio, "unsampled" : unsampled,
            "faceCount" : size(faces)
        };
}

/**
 * Classify a cap's faces and delete the ones on the wrong side, leaving the cap as an open
 * sheet (spec 9.1): `opDeleteFace` with `leaveOpen`, which is what turns the solid tool copy
 * into the half-shell the knit sews.
 *
 * options: requireDecisiveSigns {boolean, default true} - a grazing or unsampled face fails
 * the cap rather than being assigned a side; interiorSampleRings {number};
 * fallbackToExtract {boolean, default true} - when the direct edit refuses the cap, extract the
 * kept faces into their own sheet and drop the copy instead of failing.
 *
 * Returns the classification's counts plus { failed, reason, trimmedBy ("deleteFace" |
 * "extractSurface" | ""), capSheet {Query} - the body to hand the knit, which is NOT the cap
 * copy when the extraction route was taken }.
 */
export function trimCapToEnvelopeSide(context is Context, id is Id, capBody is Query,
    motionSample is map, keepAdvancing is boolean, options is map) returns map
{
    const settings = mergeMaps({
                "requireDecisiveSigns" : true, "fallbackToExtract" : true,
                "interiorSampleRings" : CAP_INTERIOR_SAMPLE_RINGS
            }, options);
    const classified = capFaceClassification(context, capBody, motionSample, keepAdvancing,
        settings.interiorSampleRings);
    var reason = "";
    if (settings.requireDecisiveSigns && size(classified.grazing) > 0)
    {
        reason = "SWEEP_CAP_FACE_GRAZING: " ~ size(classified.grazing) ~ " of " ~
            classified.faceCount ~ " cap face(s) have no decisive contact sign (" ~
            classified.unsampled ~ " could not be sampled inside, worst ratio " ~
            classified.worstGrazingRatio ~ "); a sliding face belongs to neither cap half.";
    }
    else if (size(classified.keep) == 0)
    {
        reason = "SWEEP_CAP_EMPTY: the cap kept none of its " ~ classified.faceCount ~ " face(s).";
    }
    else if (size(classified.discard) == 0)
    {
        reason = "SWEEP_CAP_UNDIVIDED: the cap discarded none of its " ~ classified.faceCount ~
            " face(s), so it is still a closed tool copy rather than a half shell.";
    }
    if (reason != "")
    {
        return mergeMaps(classified, { "failed" : true, "reason" : reason,
                    "trimmedBy" : "", "capSheet" : capBody,
                    "keptFaceCount" : size(classified.keep), "deletedFaceCount" : 0 });
    }
    var deleteReason = "";
    try
    {
        opDeleteFace(context, id + "deleteFaces", {
                    "deleteFaces" : qUnion(classified.discard),
                    "includeFillet" : false,
                    "capVoid" : false,
                    "leaveOpen" : true
                });
    }
    catch (error)
    {
        deleteReason = "SWEEP_CAP_TRIM_FAILED: " ~ toString(error);
    }
    if (deleteReason == "")
    {
        return mergeMaps(classified, {
                    "failed" : false, "reason" : "", "trimmedBy" : "deleteFace",
                    "capSheet" : capBody,
                    "keptFaceCount" : size(classified.keep),
                    "deletedFaceCount" : size(classified.discard)
                });
    }
    if (!settings.fallbackToExtract)
    {
        return mergeMaps(classified, {
                    "failed" : true, "reason" : deleteReason, "trimmedBy" : "",
                    "capSheet" : capBody,
                    "keptFaceCount" : size(classified.keep), "deletedFaceCount" : 0
                });
    }

    // Second route, taken only when the direct edit THREW - which leaves the copy as it was, so
    // the classified face queries still resolve. Extracting the kept faces asks the kernel for a
    // copy of geometry it already has instead of for a repair of a body it has just holed, and
    // the copy the caps were cut from goes away with the faces that were not kept.
    var extractReason = "";
    const extractId = id + "extractKept";
    try
    {
        opExtractSurface(context, extractId, {
                    "faces" : qUnion(classified.keep),
                    "tangentPropagation" : false
                });
    }
    catch (error)
    {
        extractReason = " opExtractSurface: " ~ toString(error);
    }
    const extracted = evaluateQuery(context, qCreatedBy(extractId, EntityType.BODY));
    if (size(extracted) != 1)
    {
        return mergeMaps(classified, {
                    "failed" : true, "trimmedBy" : "",
                    "reason" : deleteReason ~ " Extraction produced " ~ size(extracted) ~
                    " body(s)." ~ extractReason,
                    "capSheet" : capBody,
                    "keptFaceCount" : size(classified.keep), "deletedFaceCount" : 0
                });
    }
    opDeleteBodies(context, id + "dropCopy", { "entities" : capBody });
    return mergeMaps(classified, {
                "failed" : false,
                "reason" : "SWEEP_CAP_TRIMMED_BY_EXTRACTION: opDeleteFace refused this cap (" ~
                deleteReason ~ ") and the kept faces were extracted instead.",
                "trimmedBy" : "extractSurface",
                "capSheet" : qCreatedBy(extractId, EntityType.BODY),
                "keptFaceCount" : size(classified.keep),
                "deletedFaceCount" : size(classified.discard)
            });
}

/**
 * The shortest cap contact segment worth handing the kernel, in meters. Below it the two
 * boundary crossings are the same point: the face grazes the cap at a point rather than along a
 * line, and there is no curve to imprint.
 */
export const CAP_CONTACT_CURVE_MINIMUM_LENGTH = 1e-7;

/** How far two one-sided edge midpoints may be apart and still be called the same seam. */
export const SHELL_SEAM_MATCH_TOLERANCE = 1e-5;

/**
 * `candidates` with construction objects and sketch objects removed - the form in which a set of
 * bodies may be handed to a boolean, an enclose, a seam census or a delete.
 *
 * `BodyType.SHEET` admits construction planes and sketch regions, so a shell gathered by body
 * type alone contains the Part Studio's default planes. Each consumer misreads them differently
 * and none of them refuses: `opEnclose` treats a plane as a BOUNDING WALL and returns the cells
 * that plane cuts the swept region into rather than the region itself; `opBoolean` takes it as a
 * tool; the seam census counts its boundary edges as unmatched free edges; and a cleanup that
 * subtracts the result from "every sheet" names it for deletion. The standard library applies
 * this same pair at every one of those entry points - `boolean.fs` spells its selection filter
 * `BodyType.SHEET && ConstructionObject.NO && SketchObject.NO`, and `enclose.fs` re-applies both
 * to its own entities before deleting them.
 */
export function qWithoutConstructionOrSketchObjects(candidates is Query) returns Query
{
    return qSketchFilter(qConstructionFilter(candidates, ConstructionObject.NO), SketchObject.NO);
}

/**
 * Every sheet body in `candidates` that is real geometry: `BodyType.SHEET` narrowed by
 * [qWithoutConstructionOrSketchObjects]. This is the form a swept shell is gathered in.
 */
export function qSweptShellSheets(candidates is Query) returns Query
{
    return qWithoutConstructionOrSketchObjects(qBodyType(candidates, BodyType.SHEET));
}

/**
 * The coincident one-sided edge pairs of a sheet shell - the `matches` an `opBoolean` UNION needs
 * in order to SEW a shell instead of intersecting it.
 *
 * A union handed nothing but bodies has to discover for itself where they meet, and neighbouring
 * envelope patches meet TANGENTIALLY: every adjacent pair is a tangent surface-surface
 * intersection, the kernel's worst case, repeated across every pair in the shell. Named matches
 * replace that search with a sew of stated pairs. This is the standard library's own recipe for
 * joining surfaces - `createTopologyMatchesForSurfaceJoin` in boolean.fs builds the same array
 * and `joinSurfaceBodies` hands it to `opBoolean` with `recomputeMatches` and
 * `eraseImprintedEdges` - and it matches edges by the same criterion, a midpoint within the
 * boolean's default tolerance.
 *
 * The matching itself is arithmetic, not queries: each one-sided edge's midpoint is asked for
 * once and the pairing is decided on distances between those points. That makes the pass one
 * kernel call per edge rather than one query per candidate pair, and it produces the diagnostic
 * for free - an edge with no partner within tolerance is a seam that will not sew, and it is the
 * only thing standing between a certified shell and a solid.
 *
 * Returns { matches {array}, pairCount, edgeCount, unmatchedCount, worstPairGap,
 * unmatchedPoints {array} } with distances in meters.
 */
export function matchShellSeamEdges(context is Context, shellBodies is Query, tolerance is number) returns map
{
    const bodies = evaluateQuery(context, qWithoutConstructionOrSketchObjects(shellBodies));
    var edges = [];
    var owners = [];
    var midPoints = [];
    for (var bodyIndex = 0; bodyIndex < size(bodies); bodyIndex += 1)
    {
        const oneSided = evaluateQuery(context, qEdgeTopologyFilter(
                    qOwnedByBody(bodies[bodyIndex], EntityType.EDGE), EdgeTopology.ONE_SIDED));
        for (var edge in oneSided)
        {
            var midPoint = undefined;
            // A ruling that tapers to a point leaves a degenerate edge with no tangent line to
            // ask for. It bounds nothing, so it seams to nothing.
            //
            // `arcLengthParameterization` off, which is what boolean.fs's own edge matcher asks
            // for. Arc length is an inversion the kernel has to iterate for, and on an envelope
            // patch that tapers to a point at one end there is nothing for it to converge to.
            // The midpoint only has to be a repeatable interior point, not a metric one - both
            // sides of a seam are the same curve, so the same parameterization lands both on it.
            try silent
            {
                midPoint = evEdgeTangentLine(context, {
                                "edge" : edge,
                                "parameter" : 0.5,
                                "arcLengthParameterization" : false
                            }).origin / meter;
            }
            if (midPoint == undefined)
            {
                continue;
            }
            edges = append(edges, edge);
            owners = append(owners, bodyIndex);
            midPoints = append(midPoints, midPoint);
        }
    }

    const edgeCount = size(edges);
    var partner = makeArray(edgeCount, -1);
    var matches = [];
    var worstPairGap = 0;
    for (var first = 0; first < edgeCount; first += 1)
    {
        if (partner[first] != -1)
        {
            continue;
        }
        var best = -1;
        var bestSquared = tolerance * tolerance;
        for (var second = first + 1; second < edgeCount; second += 1)
        {
            if (partner[second] != -1 || owners[second] == owners[first])
            {
                continue;
            }
            const separation = squaredNorm(midPoints[second] - midPoints[first]);
            if (separation <= bestSquared)
            {
                best = second;
                bestSquared = separation;
            }
        }
        if (best == -1)
        {
            continue;
        }
        partner[first] = best;
        partner[best] = first;
        worstPairGap = max(worstPairGap, sqrt(bestSquared));
        matches = append(matches, {
                    "topology1" : edges[first],
                    "topology2" : edges[best],
                    "matchType" : TopologyMatchType.COINCIDENT
                });
    }

    var unmatchedCount = 0;
    var unmatchedPoints = [];
    var unmatchedOwners = [];
    var unmatchedEdges = [];
    for (var index = 0; index < edgeCount; index += 1)
    {
        if (partner[index] == -1)
        {
            unmatchedCount += 1;
            unmatchedPoints = append(unmatchedPoints, midPoints[index]);
            unmatchedOwners = append(unmatchedOwners, owners[index]);
            unmatchedEdges = append(unmatchedEdges, edges[index]);
        }
    }
    return {
            "matches" : matches, "pairCount" : size(matches), "edgeCount" : edgeCount,
            "unmatchedCount" : unmatchedCount, "worstPairGap" : worstPairGap,
            "unmatchedPoints" : unmatchedPoints,
            // Which body each unmatched edge belongs to, and the edge itself. A midpoint says a
            // seam did not close; the owner says which sheet is missing a neighbour, which is the
            // half of the report that names something.
            "unmatchedOwners" : unmatchedOwners,
            "unmatchedEdges" : unmatchedEdges
        };
}

/**
 * Knit a certified shell into one solid (spec 9.2): `opBoolean` UNION across the lateral
 * patches and the trimmed caps with `allowSheets` and `makeSolid`.
 *
 * The union consumes its tools into one surviving body, so the result is read back through the
 * caller's own shell query rather than through `qCreatedBy` of this operation.
 *
 * `fallbackToEnclose` (default true) asks `opEnclose` for the region the sheets bound when the
 * union does not close, and says which route succeeded. The two ask different questions of the
 * kernel, and a body that encloses but does not sew is a measurement about the seam gap, not a
 * nuisance.
 *
 * The union is handed NAMED MATCHES (see `matchShellSeamEdges`) rather than being left to
 * discover where the sheets meet, because the sheets meet tangentially and a tangent
 * surface-surface intersection across every neighbouring pair is the kernel's worst case.
 *
 * options: makeSolid, fallbackToEnclose, seamTolerance {number, default
 * SHELL_SEAM_MATCH_TOLERANCE}, recomputeMatches {boolean, default true}.
 *
 * Returns { failed, reason, closedBy ("union" | "enclose" | ""), seams {map}, bodyCountBefore,
 * bodyCountAfter, solidCount, solidBody {Query} }.
 */
export function knitSweptShell(context is Context, id is Id, shellBodies is Query, options is map) returns map
{
    const settings = mergeMaps({ "makeSolid" : true, "fallbackToEnclose" : true,
                "seamTolerance" : SHELL_SEAM_MATCH_TOLERANCE, "recomputeMatches" : true,
                "matchSeams" : false, "tryUnion" : true,
                // Neighbouring envelope patches meet TANGENTIALLY, so every seam in this shell is
                // a mergeable edge and erasing them asks the kernel to merge the whole shell into
                // as few faces as it can while it is still sewing it. That is the standard
                // library's default because a normal surface join meets at creases, where there is
                // nothing to merge.
                "eraseImprintedEdges" : true }, options);
    // What the kernel is handed. `shellBodies` itself stays unfiltered below, because after the
    // union it is also how the surviving body is read back, and by then that body is a SOLID.
    const knittableBodies = qWithoutConstructionOrSketchObjects(shellBodies);
    const before = size(evaluateQuery(context, knittableBodies));
    // Explicit branches, never a chained conditional: FeatureScript's `?:` needs parentheses
    // around a nested conditional, and a map-literal branch in the wrong slot is evaluated AS a
    // condition, which fails at run time with no location attached (tools/fsLint.py TERNARY).
    var seams = undefined;
    if (!settings.matchSeams)
    {
        seams = { "matches" : [], "pairCount" : 0, "edgeCount" : -1, "unmatchedCount" : -1,
            "worstPairGap" : 0, "unmatchedPoints" : [] };
    }
    else if (settings.seamMatches != undefined)
    {
        seams = { "matches" : settings.seamMatches, "pairCount" : size(settings.seamMatches),
            "edgeCount" : -1, "unmatchedCount" : -1, "worstPairGap" : 0, "unmatchedPoints" : [] };
    }
    else
    {
        seams = matchShellSeamEdges(context, shellBodies, settings.seamTolerance);
    }
    var reason = "";
    const unionId = id + "union";
    // `tryUnion` false goes straight to the enclose. The two ask different questions and only
    // one of them is cheap: a union has to SEW, and where it cannot it falls back to
    // intersecting tangent sheets pair by pair, which is the kernel's worst case and takes the
    // whole regeneration down rather than refusing. An enclose asks only what region the sheets
    // bound.
    if (!settings.tryUnion)
    {
        reason = "SWEEP_KNIT_UNION_SKIPPED: the union was not attempted.";
    }
    if (settings.tryUnion)
    {
        // `try(...)`, the EXPRESSION form, not a try/catch block around the call. A statement
        // block does not stop an operation's failure from taking the whole regeneration down -
        // what comes back then is "Error regenerating" with no stage attached and every body the
        // feature made rolled back. Every boolean in boolean.fs is guarded this way, and the
        // status on the operation's own id is how the outcome is read afterwards.
        try(opBoolean(context, unionId, {
                        "tools" : knittableBodies,
                        "operationType" : BooleanOperationType.UNION,
                        "allowSheets" : true,
                        "makeSolid" : settings.makeSolid,
                        "matches" : seams.matches,
                        "recomputeMatches" : size(seams.matches) == 0 ? true : settings.recomputeMatches,
                        "eraseImprintedEdges" : settings.eraseImprintedEdges
                    }));
    }
    // An operation can fail by SETTING A STATUS on its own id rather than by throwing, which a
    // catch never sees.
    if (reason == "" && settings.tryUnion && featureHasNonTrivialStatus(context, unionId))
    {
        // The kernel's OWN message, not the fact that there was one. "A non-OK status" names the
        // operation that refused and nothing about what it objected to, which is the difference
        // between a diagnosis and another round of guessing at the geometry.
        const unionError = getFeatureError(context, unionId);
        reason = "SWEEP_KNIT_REFUSED: the union reported " ~
            (unionError == undefined ? "a non-OK status with no message." : toString(unionError));
    }
    const remaining = evaluateQuery(context, knittableBodies);
    const solids = evaluateQuery(context, qBodyType(knittableBodies, BodyType.SOLID));
    if (reason == "" && size(remaining) != 1)
    {
        reason = "SWEEP_KNIT_OPEN: the union left " ~ size(remaining) ~ " bodies where one was " ~
            "expected, so at least one seam did not close.";
    }
    if (reason == "" && settings.makeSolid && size(solids) != 1)
    {
        reason = "SWEEP_KNIT_NOT_SOLID: the shell closed to " ~ size(solids) ~
            " solid bodies; the certified sheets are left in place (spec 9.3).";
    }
    if (reason == "" && size(solids) == 1)
    {
        return {
                "failed" : false, "reason" : "", "closedBy" : "union", "seams" : seams,
                "bodyCountBefore" : before, "bodyCountAfter" : size(remaining),
                "solidCount" : size(solids),
                "solidBody" : qBodyType(knittableBodies, BodyType.SOLID)
            };
    }
    // SEW-ONLY succeeds on one body, solid or not. Spec 9.3's certified open sheet set is a result
    // in its own right, and it is also the diagnostic that separates the two things `opBoolean`
    // reports with one error code: bodies it will not accept, and a solid it cannot make from
    // bodies that were fine. Asking for the sew alone is how the shell says which.
    if (reason == "" && !settings.makeSolid && size(remaining) == 1)
    {
        return {
                "failed" : false, "reason" : "SWEEP_KNIT_SEWN: the shell sewed into one open sheet " ~
                "body; no solid was requested.",
                "closedBy" : "sew", "seams" : seams,
                "bodyCountBefore" : before, "bodyCountAfter" : size(remaining),
                "solidCount" : size(solids), "solidBody" : qNothing()
            };
    }
    if (!settings.fallbackToEnclose)
    {
        return {
                "failed" : true, "reason" : reason, "closedBy" : "", "seams" : seams,
                "bodyCountBefore" : before, "bodyCountAfter" : size(remaining),
                "solidCount" : size(solids), "solidBody" : qNothing()
            };
    }

    // Second attempt: opEnclose, which asks the kernel to find the region the sheets BOUND
    // rather than to sew edge to edge. It is the route spec 10 already names for closing an
    // interval sub-sweep, and it answers a different question than the union does - which is
    // the whole reason it is worth one more operation to ask it. Reported separately so a
    // run always says WHICH route closed the body.
    //
    // First, though, the one input condition the enclose shares with the union: sheets that
    // CROSS each other. An envelope's local pieces are each exact where they graze the tool and
    // still pass through one another where responsibility changes hands, and a transversal
    // crossing is unbounded input to both operations. Splitting every crossing pair along its
    // mutual intersection turns the crossings into shared edges: the enclose can then cell the
    // complex, and the union of the cells erases every piece that lay inside the sweep. This is
    // the kernel performing spec 10's trim.
    var splitPairs = 0;
    var splitsAccepted = 0;
    if (settings.splitCrossings == true)
    {
        println("[KNIT split] scanning for crossings");
        const shellList = evaluateQuery(context, knittableBodies);
        const collisions = evCollision(context, {
                    "tools" : knittableBodies, "targets" : knittableBodies });
        println("[KNIT split] " ~ size(collisions) ~ " collision record(s)");
        var seen = {};
        for (var collision in collisions)
        {
            if (collision["type"] != ClashType.INTERFERE)
            {
                continue;
            }
            var toolIndex = -1;
            var targetIndex = -1;
            for (var bodyIndex = 0; bodyIndex < size(shellList); bodyIndex += 1)
            {
                if (toolIndex < 0 && size(evaluateQuery(context,
                            qIntersection([collision.toolBody, shellList[bodyIndex]]))) > 0)
                {
                    toolIndex = bodyIndex;
                }
                if (targetIndex < 0 && size(evaluateQuery(context,
                            qIntersection([collision.targetBody, shellList[bodyIndex]]))) > 0)
                {
                    targetIndex = bodyIndex;
                }
                if (toolIndex >= 0 && targetIndex >= 0)
                {
                    break;
                }
            }
            const pairKey = min(toolIndex, targetIndex) ~ "-" ~ max(toolIndex, targetIndex);
            if (toolIndex < 0 || targetIndex < 0 || toolIndex == targetIndex ||
                seen[pairKey] == true)
            {
                continue;
            }
            seen[pairKey] = true;
            // Both directions, body-level: each sheet is divided into separate bodies along the
            // intersection, so the crossing becomes a set of pieces that only share edges. The
            // pieces belong to the split operations, which is why a caller's shell query has to
            // be LIVE (the tester's is) for the enclose below to see them.
            println("[KNIT split] pair " ~ pairKey ~ " forward split");
            const splitAId = id + ("splitA" ~ splitPairs);
            const forward = try(opSplitPart(context, splitAId, {
                            "targets" : shellList[toolIndex],
                            "tool" : shellList[targetIndex],
                            "keepTools" : true
                        }));
            if (forward == undefined)
            {
                println("[KNIT split] pair " ~ pairKey ~ " forward REFUSED: " ~
                    toString(getFeatureError(context, splitAId)));
            }
            // The partner is split by every PIECE the first split left, because the first body
            // no longer exists whole to serve as the tool.
            const pieces = evaluateQuery(context, qUnion([shellList[toolIndex],
                            qCreatedBy(splitAId, EntityType.BODY)]));
            println("[KNIT split] pair " ~ pairKey ~ " backward split over " ~ size(pieces) ~ " piece(s)");
            var backward = undefined;
            for (var pieceIndex = 0; pieceIndex < size(pieces); pieceIndex += 1)
            {
                const piece = try(opSplitPart(context,
                        id + ("splitB" ~ splitPairs ~ "p" ~ pieceIndex), {
                            "targets" : shellList[targetIndex],
                            "tool" : pieces[pieceIndex],
                            "keepTools" : true
                        }));
                if (piece != undefined)
                {
                    backward = piece;
                }
            }
            if (forward != undefined || backward != undefined)
            {
                splitsAccepted += 1;
            }
            splitPairs += 1;
        }
    }
    const splitSummary = splitPairs == 0 ? "" :
        (" " ~ splitsAccepted ~ " of " ~ splitPairs ~ " crossing pair(s) split.");
    println("[KNIT split] splits done (" ~ splitsAccepted ~ " of " ~ splitPairs ~ ")");
    if (splitPairs > 0 && splitsAccepted == 0)
    {
        return {
                "failed" : true,
                "reason" : reason ~ " Every crossing split was refused, so the enclose was not " ~
                    "asked; the sheets still cross.",
                "closedBy" : "", "seams" : seams,
                "bodyCountBefore" : before, "bodyCountAfter" : before,
                "solidCount" : 0, "solidBody" : qNothing()
            };
    }
    try(opEnclose(context, id + "enclose", { "entities" : knittableBodies }));
    const encloseError = getFeatureError(context, id + "enclose");
    const encloseReason = featureHasNonTrivialStatus(context, id + "enclose") ?
        (" opEnclose reported " ~ (encloseError == undefined ?
                "a non-OK status with no message." : toString(encloseError))) : "";
    const enclosed = evaluateQuery(context, qBodyType(qCreatedBy(id + "enclose", EntityType.BODY),
            BodyType.SOLID));
    // A self-crossing shell bounds MORE than one cell: envelope pieces that dip inside the swept
    // volume wall it into compartments, and every compartment is inside the sweep. The swept
    // solid is their union - a solid-solid union, the kernel's ordinary case, nothing like
    // sewing tangent sheets. This is also why the union above refuses the same shell: sheets
    // that cross each other over an area are not a sewable input, but they bound cells fine.
    var cells = enclosed;
    if (size(enclosed) > 1)
    {
        try(opBoolean(context, id + "cellUnion", {
                        "tools" : qCreatedBy(id + "enclose", EntityType.BODY),
                        "operationType" : BooleanOperationType.UNION,
                        "eraseImprintedEdges" : true
                    }));
        cells = evaluateQuery(context, qBodyType(qCreatedBy(id + "enclose", EntityType.BODY),
                BodyType.SOLID));
    }
    if (size(cells) == 1)
    {
        return {
                "failed" : false,
                "reason" : "SWEEP_KNIT_ENCLOSED: the sheet union did not close (" ~ reason ~
                ") but opEnclose did" ~
                (splitPairs > 0 ? (" after" ~ splitSummary) : "") ~
                (size(enclosed) > 1 ? (", bounding " ~ size(enclosed) ~
                        " cells unioned into one solid") : "") ~
                "; the shell bounds a region even where it does not sew.",
                "closedBy" : "enclose", "seams" : seams,
                "bodyCountBefore" : before, "bodyCountAfter" : size(remaining),
                "solidCount" : size(cells),
                "solidBody" : qBodyType(qCreatedBy(id + "enclose", EntityType.BODY), BodyType.SOLID)
            };
    }
    return {
            "failed" : true,
            "reason" : reason ~ " Enclose produced " ~ size(enclosed) ~ " solid cell(s)" ~
                (size(enclosed) > 1 ? (" that would not union into one (" ~ size(cells) ~
                        " left)") : "") ~ "." ~ splitSummary ~ encloseReason,
            "closedBy" : "", "seams" : seams,
            "bodyCountBefore" : before, "bodyCountAfter" : size(remaining),
            "solidCount" : size(solids), "solidBody" : qNothing()
        };
}

/**
 * The worst distance from `edge` to `target`, sampled along the edge - the knit slop term of
 * the section 2.3 ledger, and the one number that says whether a seam is sewable before the
 * union is asked to sew it. Measured wire-to-imprint it is exactly how far the projection had
 * to move the patch's own boundary to land it on the cap.
 *
 * Returns the deviation in meters.
 */
export function measureSeamGap(context is Context, edge is Query, target is Query, sampleCount is number) returns number
{
    var parameters = makeArray(sampleCount);
    for (var index = 0; index < sampleCount; index += 1)
    {
        parameters[index] = index / sampleCount;
    }
    const tangentLines = evEdgeTangentLines(context, { "edge" : edge, "parameters" : parameters });
    var points = makeArray(sampleCount);
    for (var index = 0; index < sampleCount; index += 1)
    {
        points[index] = tangentLines[index].origin;
    }
    return evPointsDeviation(context, { "points" : points, "topologies" : target })[0].deviation / meter;
}

/**
 * The worst distance from any sheet's boundary to the boundary of every OTHER sheet - the one
 * number that says whether a shell can be sewn at all.
 *
 * A knit sews edge to edge. Neighbouring envelope patches are TANGENT along the curve they share,
 * so a kernel that cannot sew them falls back to intersecting them, and a tangent surface-surface
 * intersection repeated across every neighbouring pair is its worst case - the union stops
 * answering rather than refusing. That failure mode reports nothing, so the mismatch has to be
 * measured before the union is asked for.
 *
 * Measured edge-to-edge, not edge-to-surface: two patches that continue each other tangentially
 * put a boundary point within nothing of the NEIGHBOUR'S SURFACE whether or not their boundaries
 * coincide, so a surface-distance reads zero on exactly the shells that will not sew.
 *
 * Returns { worst, worstBodyIndex, worstEdgeIndex, edgeCount, unmeasured, perBody {array} }
 * in meters.
 */
export function measureShellSeamMismatch(context is Context, bodies is array, sampleCount is number) returns map
{
    var perBody = makeArray(size(bodies), 0);
    var worst = 0;
    var worstBodyIndex = -1;
    var worstEdgeIndex = -1;
    var edgeCount = 0;
    var unmeasured = 0;
    for (var bodyIndex = 0; bodyIndex < size(bodies); bodyIndex += 1)
    {
        var otherEdges = [];
        for (var otherIndex = 0; otherIndex < size(bodies); otherIndex += 1)
        {
            if (otherIndex != bodyIndex)
            {
                otherEdges = append(otherEdges, qOwnedByBody(bodies[otherIndex], EntityType.EDGE));
            }
        }
        if (size(otherEdges) == 0)
        {
            continue;
        }
        const target = qUnion(otherEdges);
        const edges = evaluateQuery(context, qOwnedByBody(bodies[bodyIndex], EntityType.EDGE));
        for (var edgeIndex = 0; edgeIndex < size(edges); edgeIndex += 1)
        {
            edgeCount += 1;
            // A ruling that tapers to a point leaves a degenerate edge, which has no tangent line
            // to sample. That is a shape fact about the patch, not a seam, so it is skipped rather
            // than allowed to take the measurement down.
            var gap = undefined;
            try silent
            {
                gap = measureSeamGap(context, edges[edgeIndex], target, sampleCount);
            }
            if (gap == undefined)
            {
                unmeasured += 1;
                continue;
            }
            perBody[bodyIndex] = max(perBody[bodyIndex], gap);
            if (gap > worst)
            {
                worst = gap;
                worstBodyIndex = bodyIndex;
                worstEdgeIndex = edgeIndex;
            }
        }
    }
    return {
            "worst" : worst, "worstBodyIndex" : worstBodyIndex, "worstEdgeIndex" : worstEdgeIndex,
            "edgeCount" : edgeCount, "unmeasured" : unmeasured, "perBody" : perBody
        };
}

/**
 * Counts and volume of a finished body.
 * Returns { faceCount, edgeCount, minFaceArea (m^2), minEdgeLength (m), volume (m^3) }, with the
 * two minima zero unless the `withExtremes` overload asks for them.
 */
export function summarizeSolidQuality(context is Context, body is Query) returns map
{
    return summarizeSolidQuality(context, body, false);
}

/**
 * Same, with `withExtremes` asking for the per-face and per-edge minima as well.
 *
 * They are off by default because they are one evaluation per face and one per edge on a body
 * the caller has usually just built at the end of a long feature, and the extremes are a
 * REPORT rather than a decision - nothing in the assembly branches on them. The counts and the
 * volume are three calls and answer the question the assembly actually asks, which is whether
 * a closed body exists and encloses more than the tool does.
 */
export function summarizeSolidQuality(context is Context, body is Query, withExtremes is boolean) returns map
{
    const faceCount = size(evaluateQuery(context, qOwnedByBody(body, EntityType.FACE)));
    const edgeCount = size(evaluateQuery(context, qOwnedByBody(body, EntityType.EDGE)));
    var minFaceArea = 0;
    var minEdgeLength = 0;
    if (withExtremes)
    {
        for (var face in evaluateQuery(context, qOwnedByBody(body, EntityType.FACE)))
        {
            const area = evArea(context, { "entities" : face }) / meter ^ 2;
            minFaceArea = minFaceArea == 0 ? area : min(minFaceArea, area);
        }
        for (var edge in evaluateQuery(context, qOwnedByBody(body, EntityType.EDGE)))
        {
            const edgeLength = evLength(context, { "entities" : edge }) / meter;
            minEdgeLength = minEdgeLength == 0 ? edgeLength : min(minEdgeLength, edgeLength);
        }
    }
    return {
            "faceCount" : faceCount, "edgeCount" : edgeCount,
            "minFaceArea" : minFaceArea, "minEdgeLength" : minEdgeLength,
            "volume" : evVolume(context, { "entities" : body }) / meter ^ 3
        };
}

/**
 * The whole of spec section 9 for a smooth-only sweep: cap copies, contact wires, imprint,
 * classification, trim, knit.
 *
 * definition {{
 *      @field toolBody {Query} : the tool body, positioned in the motion's TOOL FRAME - the
 *              frame A(t) and b(t) act on, so that cap i lands at A(t_i) p + b(t_i).
 *      @field shellBodies {Query} : the already-emitted, already-certified lateral patches.
 *      @field caps {array} : one map per end cap, each
 *              { motionSample {map} : an evaluateMotionSample map at that cap's time,
 *                isStart {boolean} : true for the t-start cap, which keeps its RETREATING half,
 *                contactCurves {array} : stripped contact curves where the lateral patches meet
 *                        this cap - fitBoundaryContactCurve of each adjacent fit }.
 *      @field makeSolid {boolean} : @optional default true.
 *      @field requireDecisiveSigns {boolean} : @optional default true (see trimCapToEnvelopeSide).
 *      @field measureSeams {boolean} : @optional default true; costs two kernel calls per seam.
 *      @field seamSampleCount {number} : @optional default SEAM_GAP_SAMPLE_COUNT.
 *      @field interiorSampleRings {number} : @optional default CAP_INTERIOR_SAMPLE_RINGS.
 *      @field deleteWires {boolean} : @optional default true; the wires have no place in the
 *              output once they have been imprinted.
 *      @field trace {boolean} : @optional default false; println each stage as it is reached.
 *      @field stopAfter {string} : @optional default "knit"; one of "caps", "wires", "imprint",
 *              "trim", "knit". Stopping short leaves the stages already run in the context and
 *              returns `failed` false with `stoppedAt` naming where it stopped - which is how a
 *              stage that HANGS is located, since a regeneration that never finishes delivers
 *              none of its printlns and a feature killed for running long rolls back whole.
 * }}
 *
 * Returns { failed, reason, stoppedAt, solidBody {Query}, shellBodies {Query}, capReports {array},
 * worstSeamGap, worstRigidityDefect, knit {map}, quality {map} }. A failure at any stage stops
 * the assembly and leaves everything built so far in the context - spec 9.3's certified open
 * sheet set, with the stage that failed named.
 */
export function assembleSweptSolid(context is Context, id is Id, definition is map) returns map
{
    const settings = mergeMaps({
                "makeSolid" : true, "requireDecisiveSigns" : true, "measureSeams" : true,
                "seamSampleCount" : SEAM_GAP_SAMPLE_COUNT,
                "interiorSampleRings" : CAP_INTERIOR_SAMPLE_RINGS,
                "deleteWires" : true, "trace" : false, "stopAfter" : "knit",
                "seamTolerance" : SHELL_SEAM_MATCH_TOLERANCE, "matchSeams" : false
            }, definition);
    // How far the assembly is allowed to run: "caps", "wires", "imprint", "trim" or "knit".
    //
    // A stage that HANGS reports nothing at all. Printlns are delivered when the evaluation
    // finishes, so a regeneration that never finishes carries none of them, and a feature killed
    // for running long rolls back whole - the parts list comes back empty and the trace is lost
    // with it. Stopping short of a stage and finishing normally is therefore the only measurement
    // available: the run that COMPLETES names the last stage that is not the expensive one.
    const stageRank = { "caps" : 0, "wires" : 1, "imprint" : 2, "trim" : 3, "seams" : 4,
            "knit" : 5 };
    const stopRank = stageRank[settings.stopAfter];
    if (stopRank == undefined)
    {
        throw "solidSweepUtils assemble: stopAfter must be caps, wires, imprint, trim, seams or knit.";
    }
    // A feature that dies rolls its whole regeneration back, so every name and every body it was
    // going to be diagnosed by goes with it - the parts list comes back empty and says nothing
    // about how far the assembly got. A println is emitted as it happens and survives that, which
    // makes it the only channel that reports the stage an uncatchable failure stopped at.
    const trace = function(line is string)
        {
            if (settings.trace)
            {
                println("[SWEEP ASSEMBLY] " ~ line);
            }
        };
    var capReports = [];
    // One entry per cap: the copy while it is still a solid, replaced by whatever body the trim
    // leaves behind - which is a different body when the trim had to fall back to extraction.
    var capOutputs = [];
    var wireBodies = [];
    var worstSeamGap = 0;
    var worstRigidityDefect = 0;
    var failure = "";

    for (var capIndex = 0; capIndex < size(settings.caps); capIndex += 1)
    {
        const cap = settings.caps[capIndex];
        const capId = id + ("cap" ~ capIndex);
        const placement = sweepCapPlacement(cap.motionSample);
        worstRigidityDefect = max(worstRigidityDefect, placement.rigidityDefect);
        trace("cap " ~ capIndex ~ ": placing the copy, rigidity defect " ~ placement.rigidityDefect);
        var capBody = qNothing();
        // Every stage is caught and NAMED. An escaping throw takes the whole regeneration with it,
        // and what comes back then is the word "Execution error" with no stage attached - which is
        // the one thing a caller cannot act on. A stage that throws is a stage that failed, and
        // spec 9.3 wants the certified sheets reported either way.
        try
        {
            capBody = emitToolCapCopy(context, capId + "copy", settings.toolBody, placement,
                "sweepCap" ~ capIndex);
        }
        catch (error)
        {
            capReports = append(capReports, { "capIndex" : capIndex, "stage" : "copy",
                        "failed" : true, "reason" : toString(error), "capBody" : capBody });
            failure = "cap " ~ capIndex ~ " copy threw: " ~ toString(error);
            break;
        }
        capOutputs = append(capOutputs, capBody);
        trace("cap " ~ capIndex ~ ": copy placed; " ~ size(cap.contactCurves) ~ " contact curve(s) to wire");
        if (stopRank < stageRank["wires"])
        {
            capReports = append(capReports, { "capIndex" : capIndex, "stage" : "caps",
                        "failed" : false, "reason" : "", "capBody" : capBody,
                        "rigidityDefect" : placement.rigidityDefect });
            continue;
        }

        var wireEdges = [];
        var wireReports = [];
        var wireRefusal = "";
        for (var curveIndex = 0; curveIndex < size(cap.contactCurves); curveIndex += 1)
        {
            const wire = emitContactWire(context, capId + ("wire" ~ curveIndex),
                cap.contactCurves[curveIndex]);
            wireReports = append(wireReports, wire);
            if (wire.refused)
            {
                wireRefusal = wireRefusal ~ " " ~ wire.reason;
            }
            else
            {
                wireBodies = append(wireBodies, wire.wireBody);
                wireEdges = append(wireEdges, qCreatedBy(wire.id, EntityType.EDGE));
            }
        }
        if (wireRefusal != "")
        {
            capReports = append(capReports, { "capIndex" : capIndex, "stage" : "wire",
                        "failed" : true, "reason" : wireRefusal, "wires" : wireReports,
                        "capBody" : capBody });
            failure = "cap " ~ capIndex ~ ":" ~ wireRefusal;
            break;
        }

        // A cap handed NO contact curve needs no split: its contact set lies entirely on edges
        // the copy already carries, so every one of its faces is wholly advancing or wholly
        // retreating and the classification below decides them all on their own interior sign.
        // A polyhedral tool under pure translation is exactly that case - a plane face's
        // contact function is constant there, so no face carries a contact line at all.
        trace("cap " ~ capIndex ~ ": " ~ size(wireEdges) ~ " wire(s) built; imprinting");
        if (stopRank < stageRank["imprint"])
        {
            capReports = append(capReports, { "capIndex" : capIndex, "stage" : "wires",
                        "failed" : false, "reason" : "", "wires" : wireReports,
                        "capBody" : capBody, "rigidityDefect" : placement.rigidityDefect });
            continue;
        }
        var imprint = { "failed" : false, "reason" : "", "faceCountBefore" : 0,
                "faceCountAfter" : 0, "splittingEdges" : [], "splitEdgeQuery" : qNothing() };
        if (size(wireEdges) > 0)
        {
            try
            {
                imprint = imprintContactWires(context, capId + "imprint",
                    qOwnedByBody(capBody, EntityType.FACE), qUnion(wireEdges));
            }
            catch (error)
            {
                imprint = mergeMaps(imprint, { "failed" : true,
                            "reason" : "SWEEP_CAP_IMPRINT_THREW: " ~ toString(error) });
            }
        }
        if (imprint.failed)
        {
            capReports = append(capReports, { "capIndex" : capIndex, "stage" : "imprint",
                        "failed" : true, "reason" : imprint.reason, "wires" : wireReports,
                        "imprint" : imprint, "capBody" : capBody });
            failure = "cap " ~ capIndex ~ ": " ~ imprint.reason;
            break;
        }

        trace("cap " ~ capIndex ~ ": imprint " ~ imprint.faceCountBefore ~ " -> " ~
            imprint.faceCountAfter ~ " faces");
        if (stopRank < stageRank["trim"])
        {
            capReports = append(capReports, { "capIndex" : capIndex, "stage" : "imprint",
                        "failed" : false, "reason" : "", "wires" : wireReports,
                        "imprint" : imprint, "capBody" : capBody,
                        "rigidityDefect" : placement.rigidityDefect });
            continue;
        }
        var seamGap = 0;
        if (settings.measureSeams)
        {
            for (var wireEdge in wireEdges)
            {
                try silent
                {
                    seamGap = max(seamGap, measureSeamGap(context, wireEdge,
                                imprint.splitEdgeQuery, settings.seamSampleCount));
                }
            }
            worstSeamGap = max(worstSeamGap, seamGap);
        }

        trace("cap " ~ capIndex ~ ": seam gap " ~ seamGap ~ " m; classifying and trimming");
        var trim = undefined;
        try
        {
            trim = trimCapToEnvelopeSide(context, capId + "trim", capBody, cap.motionSample,
                !cap.isStart, settings);
        }
        catch (error)
        {
            capReports = append(capReports, { "capIndex" : capIndex, "stage" : "trim",
                        "failed" : true, "reason" : "SWEEP_CAP_TRIM_THREW: " ~ toString(error),
                        "wires" : wireReports, "imprint" : imprint, "capBody" : capBody });
            failure = "cap " ~ capIndex ~ " trim threw: " ~ toString(error);
            break;
        }
        trace("cap " ~ capIndex ~ ": trim " ~ (trim.failed ? ("FAILED " ~ trim.reason) :
                ("kept " ~ size(trim.keep) ~ ", deleted " ~ size(trim.discard) ~ ", by " ~ trim.trimmedBy)));
        if (trim.capSheet != undefined)
        {
            capOutputs[capIndex] = trim.capSheet;
        }
        capReports = append(capReports, mergeMaps(trim, {
                        "capIndex" : capIndex, "stage" : "trim", "wires" : wireReports,
                        "imprint" : imprint, "seamGap" : seamGap, "capBody" : capBody,
                        "rigidityDefect" : placement.rigidityDefect
                    }));
        if (trim.failed)
        {
            failure = "cap " ~ capIndex ~ ": " ~ trim.reason;
            break;
        }
    }

    // The wires are consumed knowledge once imprinted - but on a failure they are the first
    // thing to look at, so they only go away when there is nothing left to diagnose.
    if (settings.deleteWires && failure == "" && size(wireBodies) > 0)
    {
        try silent
        {
            opDeleteBodies(context, id + "deleteWires", { "entities" : qUnion(wireBodies) });
        }
    }
    // The caller may name the shell itself. The assembled query is a qUnion of two cap queries
    // and a qUnion of every lateral patch, all of them `qCreatedBy` this same evaluation; a
    // caller that can identify the same bodies another way gets to say so.
    const shell = settings.shellQueryOverride != undefined ?
        settings.shellQueryOverride : qUnion(append(capOutputs, settings.shellBodies));
    if (failure != "")
    {
        return {
                "failed" : true, "reason" : failure, "stoppedAt" : "",
                "solidBody" : qNothing(),
                "shellBodies" : shell, "capReports" : capReports,
                "worstSeamGap" : worstSeamGap, "worstRigidityDefect" : worstRigidityDefect,
                "seams" : undefined, "knit" : undefined, "quality" : undefined
            };
    }

    if (stopRank < stageRank["seams"])
    {
        return {
                "failed" : false, "reason" : "", "stoppedAt" : settings.stopAfter,
                "solidBody" : qNothing(), "shellBodies" : shell, "capReports" : capReports,
                "worstSeamGap" : worstSeamGap, "worstRigidityDefect" : worstRigidityDefect,
                "seams" : undefined, "knit" : undefined, "quality" : undefined
            };
    }

    // The seam census is its own stage, and it is the one worth reading before the union runs:
    // it says whether the shell CAN be sewn. Every free edge that found no partner is a hole, and
    // a union asked to close a shell with holes cannot succeed however long it is given.
    var seams = undefined;
    var seamReason = "";
    try
    {
        // Only when asked for. The census is one kernel call per free edge and this whole
        // assembly runs inside ONE feature evaluation, whose budget the sweep that produced the
        // shell has already spent most of; a shell whose seams are exact by construction does
        // not need the union told where they are.
        if (settings.matchSeams)
        {
            seams = matchShellSeamEdges(context, shell, settings.seamTolerance);
        }
    }
    catch (error)
    {
        seamReason = "SWEEP_SEAM_MATCH_THREW: " ~ toString(error);
    }
    if (stopRank < stageRank["knit"] || seamReason != "")
    {
        return {
                "failed" : seamReason != "", "reason" : seamReason,
                "stoppedAt" : seamReason != "" ? "" : settings.stopAfter,
                "solidBody" : qNothing(), "shellBodies" : shell, "capReports" : capReports,
                "worstSeamGap" : worstSeamGap, "worstRigidityDefect" : worstRigidityDefect,
                "seams" : seams, "knit" : undefined, "quality" : undefined
            };
    }
    var knit = undefined;
    try
    {
        // Inside the guard, because a trace argument is evaluated whether or not it is printed
        // and this one asks the kernel to resolve the whole shell.
        trace("knitting " ~ size(evaluateQuery(context, shell)) ~ " sheet(s)");
        knit = knitSweptShell(context, id + "knit", shell,
            seams == undefined ? settings : mergeMaps(settings, { "seamMatches" : seams.matches }));
    }
    catch (error)
    {
        return {
                "failed" : true, "reason" : "SWEEP_KNIT_THREW: " ~ toString(error),
                "stoppedAt" : "", "solidBody" : qNothing(), "shellBodies" : shell,
                "capReports" : capReports, "worstSeamGap" : worstSeamGap,
                "worstRigidityDefect" : worstRigidityDefect, "seams" : seams,
                "knit" : undefined, "quality" : undefined
            };
    }
    trace("knit " ~ (knit.failed ? ("FAILED " ~ knit.reason) : ("closed by " ~ knit.closedBy)));
    var quality = undefined;
    if (!knit.failed)
    {
        try silent
        {
            quality = summarizeSolidQuality(context, knit.solidBody);
        }
    }
    return {
            "failed" : knit.failed, "reason" : knit.reason, "stoppedAt" : "",
            "solidBody" : knit.solidBody, "shellBodies" : shell, "capReports" : capReports,
            "worstSeamGap" : worstSeamGap, "worstRigidityDefect" : worstRigidityDefect,
            "seams" : seams, "knit" : knit, "quality" : quality
        };
}

/** A one-line report of an assembly, for the console. */
export function summarizeSweptSolid(assembly is map) returns string
{
    var summary = assembly.failed ? ("FAILED - " ~ assembly.reason) : "one solid";
    summary = summary ~ "; " ~ size(assembly.capReports) ~ " cap(s)";
    for (var report in assembly.capReports)
    {
        summary = summary ~ ", cap " ~ report.capIndex ~ " " ~
            (report.keptFaceCount == undefined ? "0" : report.keptFaceCount) ~ " kept / " ~
            (report.deletedFaceCount == undefined ? "0" : report.deletedFaceCount) ~ " deleted";
    }
    summary = summary ~ "; worst seam gap " ~ assembly.worstSeamGap ~ " m, rigidity defect " ~
        assembly.worstRigidityDefect;
    if (assembly.knit != undefined)
    {
        summary = summary ~ "; closed by " ~ (assembly.knit.closedBy == "" ? "nothing" : assembly.knit.closedBy) ~
            " (" ~ assembly.knit.bodyCountBefore ~ " sheets in, " ~ assembly.knit.bodyCountAfter ~
            " bodies out, " ~ assembly.knit.solidCount ~ " solid)";
    }
    if (assembly.quality != undefined)
    {
        summary = summary ~ "; " ~ assembly.quality.faceCount ~ " faces, " ~
            assembly.quality.edgeCount ~ " edges, min face area " ~ assembly.quality.minFaceArea ~
            " m^2, min edge " ~ assembly.quality.minEdgeLength ~ " m, volume " ~
            assembly.quality.volume ~ " m^3";
    }
    return summary;
}

/**
 * The velocity of the material point currently at `worldPoint` (plain numbers, meters) under a
 * motion sample: v = A' A^-1 (p - b) + b'.
 */
function materialVelocityAt(motionSample is map, worldPoint is Vector) returns Vector
{
    const toolPoint = inverse(motionSample.rotation) * (worldPoint - motionSample.translation);
    return motionSample.rotationDerivative * toolPoint + motionSample.translationDerivative;
}

/**
 * Three orthonormal columns from three sampled ones by modified Gram-Schmidt. A fitted motion's
 * A(t) is orthonormal only to its drift, and a cap placed by the raw matrix would be sheared by
 * that drift - which is larger than the seam gaps this module works to.
 */
function orthonormalizedFrame(columns is array) returns array
{
    var frame = makeArray(3);
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        var candidate = columns[columnIndex];
        for (var previous = 0; previous < columnIndex; previous += 1)
        {
            candidate = candidate - dot(candidate, frame[previous]) * frame[previous];
        }
        if (squaredNorm(candidate) < 1e-24)
        {
            throw "solidSweepUtils emit: the motion sample's rotation columns are degenerate, so the tool " ~
                "has no rigid placement at this time.";
        }
        frame[columnIndex] = normalize(candidate);
    }
    return frame;
}

/**
 * Face parameters walked outward from the centre of the parameter box in square rings, kept
 * inside [0.02, 0.98] so a sample never lands on a boundary the kernel might call outside.
 */
function centreOutwardFaceParameters(ringCount is number) returns array
{
    const step = 0.48 / max(ringCount, 1);
    var parameters = [];
    for (var ring = 0; ring <= ringCount; ring += 1)
    {
        for (var uStep = -ring; uStep <= ring; uStep += 1)
        {
            for (var vStep = -ring; vStep <= ring; vStep += 1)
            {
                if (max(abs(uStep), abs(vStep)) == ring)
                {
                    parameters = append(parameters, vector(0.5 + uStep * step, 0.5 + vStep * step));
                }
            }
        }
    }
    return parameters;
}

/** Map an evSurfaceDefinition result onto SweepSurfaceClass. */
function classifySurfaceDefinition(surfaceDefinition is map) returns SweepSurfaceClass
{
    if (surfaceDefinition is Plane)
    {
        return SweepSurfaceClass.PLANE;
    }
    if (surfaceDefinition is Cylinder)
    {
        return SweepSurfaceClass.CYLINDER;
    }
    if (surfaceDefinition is Cone)
    {
        return SweepSurfaceClass.CONE;
    }
    if (surfaceDefinition is Sphere)
    {
        return SweepSurfaceClass.SPHERE;
    }
    if (surfaceDefinition is Torus)
    {
        return SweepSurfaceClass.TORUS;
    }
    if (surfaceDefinition is BSplineSurface)
    {
        return SweepSurfaceClass.BSPLINE;
    }
    return SweepSurfaceClass.OTHER;
}

/**
 * Strip length units from a normalized surface definition into a plain (untyped) map holding
 * exactly the fields the module evaluators consume: control points become plain-number vectors,
 * meters implied; weights, knots, degrees, and periodicity flags pass through.
 */
function stripSurfaceUnits(surface is map) returns map
{
    var strippedControlPoints = makeArray(size(surface.controlPoints));
    for (var rowIndex = 0; rowIndex < size(surface.controlPoints); rowIndex += 1)
    {
        var strippedRow = makeArray(size(surface.controlPoints[rowIndex]));
        for (var columnIndex = 0; columnIndex < size(surface.controlPoints[rowIndex]); columnIndex += 1)
        {
            strippedRow[columnIndex] = (1 / meter) * surface.controlPoints[rowIndex][columnIndex];
        }
        strippedControlPoints[rowIndex] = strippedRow;
    }
    return {
            "uDegree" : surface.uDegree,
            "vDegree" : surface.vDegree,
            "uKnots" : surface.uKnots,
            "vKnots" : surface.vKnots,
            "controlPoints" : strippedControlPoints,
            "isRational" : surface.isRational,
            "weights" : surface.weights,
            "isUPeriodic" : surface.isUPeriodic,
            "isVPeriodic" : surface.isVPeriodic
        };
}

/** Modulo that lands in [0, divisor) for any sign of value. */
export function positiveModulo(value is number, divisor is number) returns number
{
    return ((value % divisor) + divisor) % divisor;
}

/**
 * One side of a co-edge: the adjacent face's index, one-sided normals and edge points at the
 * given arc-length parameters (one batched tangent-plane call), and the pcurve samples when
 * the face record carries a spline. Returns undefined when faceQuery is undefined (a
 * sheet-boundary edge's missing side).
 */
function extractCoEdgeSide(context is Context, edge is Query, faceQuery, faceRecords is array, sampleParameters is array)
{
    if (faceQuery == undefined)
    {
        return undefined;
    }
    const faceIndex = faceIndexForQuery(faceRecords, faceQuery);
    const tangentPlanes = evFaceTangentPlanesAtEdge(context, {
                "edge" : edge,
                "face" : faceQuery,
                "parameters" : sampleParameters
            });
    var normals = makeArray(size(tangentPlanes));
    var edgePoints = makeArray(size(tangentPlanes));
    var edgeTangents = makeArray(size(tangentPlanes));
    for (var planeIndex = 0; planeIndex < size(tangentPlanes); planeIndex += 1)
    {
        normals[planeIndex] = tangentPlanes[planeIndex].normal;
        edgePoints[planeIndex] = (1 / meter) * tangentPlanes[planeIndex].origin;
        edgeTangents[planeIndex] = tangentPlanes[planeIndex].x;
    }
    var uvCurve = undefined;
    if (faceIndex != undefined && faceRecords[faceIndex].spline != undefined)
    {
        uvCurve = invertEdgeSamplesOntoFace(faceRecords[faceIndex].spline, edgePoints);
    }
    return {
            "faceIndex" : faceIndex,
            "normals" : normals,
            "edgePoints" : edgePoints,
            "edgeTangents" : edgeTangents,
            "uvCurve" : uvCurve
        };
}

/**
 * Pcurve samples of an edge on one face: invert each stripped edge point onto the face's
 * spline, seeding the first from the multi-seed grid and each subsequent one from its
 * predecessor, with a grid retry whenever a seeded step lands above 1e-8 m. Returns
 * { uvSamples {array of Vector}, maxResidual {number} }.
 */
function invertEdgeSamplesOntoFace(strippedSurface is map, edgePoints is array) returns map
{
    var uvSamples = makeArray(size(edgePoints));
    var maxResidual = 0;
    var previousUv = undefined;
    for (var pointIndex = 0; pointIndex < size(edgePoints); pointIndex += 1)
    {
        var inversion;
        if (previousUv == undefined)
        {
            inversion = invertPointOnSurfaceFromGrid(strippedSurface, edgePoints[pointIndex], 7);
        }
        else
        {
            inversion = invertPointOnSurface(strippedSurface, edgePoints[pointIndex], previousUv);
            if (inversion.residual > 1e-8)
            {
                inversion = invertPointOnSurfaceFromGrid(strippedSurface, edgePoints[pointIndex], 7);
            }
        }
        uvSamples[pointIndex] = inversion.uv;
        previousUv = inversion.uv;
        if (inversion.residual > maxResidual)
        {
            maxResidual = inversion.residual;
        }
    }
    return { "uvSamples" : uvSamples, "maxResidual" : maxResidual };
}

/** Map an evCurveDefinition result onto SweepCurveClass. */
function classifyCurveDefinition(curveDefinition is map) returns SweepCurveClass
{
    if (curveDefinition is Line)
    {
        return SweepCurveClass.LINE;
    }
    if (curveDefinition is Circle)
    {
        return SweepCurveClass.CIRCLE;
    }
    if (curveDefinition is Ellipse)
    {
        return SweepCurveClass.ELLIPSE;
    }
    if (curveDefinition is BSplineCurve)
    {
        return SweepCurveClass.BSPLINE;
    }
    return SweepCurveClass.OTHER;
}

/**
 * Strip length units from a normalized curve definition into a plain (untyped) map holding
 * exactly the fields the module evaluators consume: control points become plain-number
 * vectors, meters implied; weights, knots, degree, and periodicity pass through.
 */
function stripCurveUnits(spline is map) returns map
{
    var strippedControlPoints = makeArray(size(spline.controlPoints));
    for (var pointIndex = 0; pointIndex < size(spline.controlPoints); pointIndex += 1)
    {
        strippedControlPoints[pointIndex] = (1 / meter) * spline.controlPoints[pointIndex];
    }
    return {
            "degree" : spline.degree,
            "knots" : spline.knots,
            "controlPoints" : strippedControlPoints,
            "isRational" : spline.isRational,
            "weights" : spline.weights,
            "isPeriodic" : spline.isPeriodic
        };
}

/** Index of the face record whose query resolves to the same transient entity, else undefined. */
function faceIndexForQuery(faceRecords is array, faceQuery is Query)
{
    for (var record in faceRecords)
    {
        if (record.faceQuery.transientId == faceQuery.transientId)
        {
            return record.faceIndex;
        }
    }
    return undefined;
}

/** Index of the co-edge record whose query resolves to the same transient entity, else undefined. */
function edgeIndexForQuery(coEdgeRecords is array, edgeQuery is Query)
{
    for (var record in coEdgeRecords)
    {
        if (record.edgeQuery.transientId == edgeQuery.transientId)
        {
            return record.edgeIndex;
        }
    }
    return undefined;
}

/** Append a unit direction unless an equal one (within 1e-9 per component scale) is present. */
function appendUniqueDirection(directions is array, candidate is Vector) returns array
{
    for (var existing in directions)
    {
        if (squaredNorm(candidate - existing) < 1e-18)
        {
            return directions;
        }
    }
    return append(directions, candidate);
}

/** One line describing a stripped surface's stored shape, for the self-test printouts.
    Exported because the fit module's live tests print extraction results too, and a helper
    reached across a tab boundary has to be exported to resolve there. */
export function describeSurfaceShape(strippedSurface is map) returns string
{
    return "degree " ~ strippedSurface.uDegree ~ "x" ~ strippedSurface.vDegree ~
        ", knots " ~ size(strippedSurface.uKnots) ~ "/" ~ size(strippedSurface.vKnots) ~
        ", periodic " ~ strippedSurface.isUPeriodic ~ "/" ~ strippedSurface.isVPeriodic ~
        ", rational " ~ (strippedSurface.isRational == true);
}

/**
 * Solve a 3x3 linear system by Gaussian elimination with partial pivoting.
 * matrixRows: array of 3 rows, each an array of 3 plain numbers. rightHandSide: array of 3 plain
 * numbers. Returns the solution as an array of 3 numbers, or undefined when the system is
 * singular (best available pivot below 1e-10 of the largest matrix entry).
 */
function solveThreeByThreeSystem(matrixRows is array, rightHandSide is array)
{
    var augmented = makeArray(3);
    var largestEntry = 0;
    for (var rowIndex = 0; rowIndex < 3; rowIndex += 1)
    {
        augmented[rowIndex] = [matrixRows[rowIndex][0], matrixRows[rowIndex][1], matrixRows[rowIndex][2],
                    rightHandSide[rowIndex]];
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            largestEntry = max(largestEntry, abs(matrixRows[rowIndex][columnIndex]));
        }
    }
    if (largestEntry == 0)
    {
        return undefined;
    }
    for (var pivotColumn = 0; pivotColumn < 3; pivotColumn += 1)
    {
        var pivotRow = pivotColumn;
        for (var rowIndex = pivotColumn + 1; rowIndex < 3; rowIndex += 1)
        {
            if (abs(augmented[rowIndex][pivotColumn]) > abs(augmented[pivotRow][pivotColumn]))
            {
                pivotRow = rowIndex;
            }
        }
        if (abs(augmented[pivotRow][pivotColumn]) < 1e-10 * largestEntry)
        {
            return undefined;
        }
        if (pivotRow != pivotColumn)
        {
            const swappedRow = augmented[pivotColumn];
            augmented[pivotColumn] = augmented[pivotRow];
            augmented[pivotRow] = swappedRow;
        }
        for (var rowIndex = pivotColumn + 1; rowIndex < 3; rowIndex += 1)
        {
            const eliminationFactor = augmented[rowIndex][pivotColumn] / augmented[pivotColumn][pivotColumn];
            for (var columnIndex = pivotColumn; columnIndex < 4; columnIndex += 1)
            {
                augmented[rowIndex][columnIndex] = augmented[rowIndex][columnIndex] -
                    eliminationFactor * augmented[pivotColumn][columnIndex];
            }
        }
    }
    var solution = makeArray(3);
    for (var rowIndex = 2; rowIndex >= 0; rowIndex -= 1)
    {
        var accumulated = augmented[rowIndex][3];
        for (var columnIndex = rowIndex + 1; columnIndex < 3; columnIndex += 1)
        {
            accumulated -= augmented[rowIndex][columnIndex] * solution[columnIndex];
        }
        solution[rowIndex] = accumulated / augmented[rowIndex][rowIndex];
    }
    return solution;
}

/**
 * Least-squares affine map between two 2D parameter spaces: target ~= matrix * source + offset.
 * sourcePoints / targetPoints: equal-length arrays (at least 3 points, not collinear) of 2D
 * unitless Vectors. Returns { "matrix" : [[m00, m01], [m10, m11]], "offset" : [b0, b1] } (plain
 * numbers), or undefined when the source points do not span a 2D patch. Solved per target
 * coordinate as a 3-unknown normal-equations system; both coordinates share one matrix.
 */
function fitTwoDimensionalAffineMap(sourcePoints is array, targetPoints is array)
{
    var sumPP = 0;
    var sumPQ = 0;
    var sumQQ = 0;
    var sumP = 0;
    var sumQ = 0;
    var sumPU = 0;
    var sumQU = 0;
    var sumU = 0;
    var sumPV = 0;
    var sumQV = 0;
    var sumV = 0;
    const pointCount = size(sourcePoints);
    for (var pointIndex = 0; pointIndex < pointCount; pointIndex += 1)
    {
        const p = sourcePoints[pointIndex][0];
        const q = sourcePoints[pointIndex][1];
        const u = targetPoints[pointIndex][0];
        const v = targetPoints[pointIndex][1];
        sumPP += p * p;
        sumPQ += p * q;
        sumQQ += q * q;
        sumP += p;
        sumQ += q;
        sumPU += p * u;
        sumQU += q * u;
        sumU += u;
        sumPV += p * v;
        sumQV += q * v;
        sumV += v;
    }
    const normalMatrix = [[sumPP, sumPQ, sumP], [sumPQ, sumQQ, sumQ], [sumP, sumQ, pointCount]];
    const uCoefficients = solveThreeByThreeSystem(normalMatrix, [sumPU, sumQU, sumU]);
    const vCoefficients = solveThreeByThreeSystem(normalMatrix, [sumPV, sumQV, sumV]);
    if (uCoefficients == undefined || vCoefficients == undefined)
    {
        return undefined;
    }
    return {
            "matrix" : [[uCoefficients[0], uCoefficients[1]], [vCoefficients[0], vCoefficients[1]]],
            "offset" : [uCoefficients[2], vCoefficients[2]]
        };
}

/** Apply an affine map from fitTwoDimensionalAffineMap to one 2D unitless Vector. */
function applyTwoDimensionalAffineMap(affineMap is map, sourcePoint is Vector) returns Vector
{
    return vector(
        affineMap.matrix[0][0] * sourcePoint[0] + affineMap.matrix[0][1] * sourcePoint[1] + affineMap.offset[0],
        affineMap.matrix[1][0] * sourcePoint[0] + affineMap.matrix[1][1] * sourcePoint[1] + affineMap.offset[1]);
}


// ============================= Fitting and certification (spec 7) =============================

/**
 * Uniform stations over [tStart, tEnd] with event times merged in EXACTLY: an event inside
 * mergeTolerance of an interior station replaces it; anywhere else in the open interval it is
 * inserted. Endpoints are never displaced. Returns the sorted station array (size >= the
 * requested count).
 */
export function buildFitStations(tStart is number, tEnd is number, stationCount is number,
    eventTimes is array, mergeTolerance is number) returns array
{
    return buildFitStations(tStart, tEnd, stationCount, eventTimes, mergeTolerance, {});
}

/**
 * As above, with optional end grading: { gradeStart, gradeEnd } booleans. A graded end clusters
 * stations QUADRATICALLY toward it, which is uniform in sqrt(distance to that end) - the right
 * spacing for a strip whose t range ends at a section merge, where the section's boundary point
 * runs like a square root of the distance to the merge (spec 7.1). Ungraded, the spacing is
 * uniform, which is what every non-merging component wants.
 *
 * The two endpoints are ASSIGNED rather than computed: a strip's seam station has to compare
 * equal to its neighbour's for the shared arrays to be shared exactly, and
 * tStart + (tEnd - tStart) is not tEnd for every pair of doubles.
 */
export function buildFitStations(tStart is number, tEnd is number, stationCount is number,
    eventTimes is array, mergeTolerance is number, grading is map) returns array
{
    const gradeStart = grading.gradeStart == true;
    const gradeEnd = grading.gradeEnd == true;
    var stations = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        const uniform = index / (stationCount - 1);
        var fraction = uniform;
        if (gradeStart && gradeEnd)
        {
            fraction = 0.5 * (1 - cos(uniform * 180 * degree));
        }
        else if (gradeStart)
        {
            fraction = uniform ^ 2;
        }
        else if (gradeEnd)
        {
            fraction = 1 - (1 - uniform) ^ 2;
        }
        stations[index] = tStart + (tEnd - tStart) * fraction;
    }
    stations[0] = tStart;
    stations[stationCount - 1] = tEnd;
    for (var eventTime in eventTimes)
    {
        if (eventTime <= tStart + mergeTolerance || eventTime >= tEnd - mergeTolerance)
        {
            continue;
        }
        var nearestIndex = 0;
        for (var index = 1; index < size(stations); index += 1)
        {
            if (abs(stations[index] - eventTime) < abs(stations[nearestIndex] - eventTime))
            {
                nearestIndex = index;
            }
        }
        if (abs(stations[nearestIndex] - eventTime) <= mergeTolerance &&
            nearestIndex != 0 && nearestIndex != size(stations) - 1)
        {
            stations[nearestIndex] = eventTime;
        }
        else
        {
            stations = append(stations, eventTime);
        }
    }
    return sort(stations, function(a, b)
        {
            return a - b;
        });
}

/**
 * The uv where a station's section march starts or ends.
 *
 * anchor kinds:
 *   { anchorKind : "fixedUv", uv : [u, v] } - a constant anchor (a vertex contact).
 *   { anchorKind : "branch", tSamples : [t...], uvSamples : [[u, v]...] } - a co-edge branch:
 *     the bracketing samples in t are interpolated linearly, then the anchor is polished onto
 *     f = 0 ALONG the local sample segment, so it never leaves the shared boundary polyline.
 *     tSamples must be monotone along the branch - splitBranchAtTimeExtrema cuts a branch at
 *     its t extrema for exactly that reason, and each side it returns is monotone.
 *
 * A station landing exactly on one of the branch's own end times returns that end SAMPLE, with
 * no interpolation and no polish. That is the shared-array rule (spec 2.3) at its sharpest: two
 * sides cut from one branch carry the same cut sample, and the strips on either side of the cut
 * have to read the same numbers out of it, which interpolating to fraction 1 and then polishing
 * along a different segment would not give.
 */
export function anchorUvAtStation(anchor is map, strippedMotion is map, strippedSurface is map,
    t is number, sectionTolerance is number) returns array
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    if (anchor.anchorKind == "fixedUv")
    {
        return anchor.uv;
    }
    const sampleCount = size(anchor.tSamples);
    if (t == anchor.tSamples[0])
    {
        return anchor.uvSamples[0];
    }
    if (t == anchor.tSamples[sampleCount - 1])
    {
        return anchor.uvSamples[sampleCount - 1];
    }
    var segmentIndex = undefined;
    for (var index = 0; index + 1 < sampleCount; index += 1)
    {
        if ((anchor.tSamples[index] - t) * (anchor.tSamples[index + 1] - t) <= 0)
        {
            segmentIndex = index;
            break;
        }
    }
    if (segmentIndex == undefined)
    {
        segmentIndex = abs(anchor.tSamples[0] - t) < abs(anchor.tSamples[sampleCount - 1] - t) ?
            0 : sampleCount - 2;
    }
    const tLow = anchor.tSamples[segmentIndex];
    const tHigh = anchor.tSamples[segmentIndex + 1];
    const fraction = abs(tHigh - tLow) < 1e-300 ? 0 : clampToRange((t - tLow) / (tHigh - tLow), 0, 1);
    const uvLow = anchor.uvSamples[segmentIndex];
    const uvHigh = anchor.uvSamples[segmentIndex + 1];
    var uv = [uvLow[0] + fraction * (uvHigh[0] - uvLow[0]), uvLow[1] + fraction * (uvHigh[1] - uvLow[1])];
    const segment = [uvHigh[0] - uvLow[0], uvHigh[1] - uvLow[1]];
    return polishAnchorAlongDirection(stationMotion, strippedSurface, t, uv, segment, sectionTolerance);
}

/**
 * 1D Newton on f restricted to a line through uv along the (unnormalized) direction - the
 * stay-on-boundary polish. The step is capped at twice the direction's own length so a nearly
 * tangential section cannot fling the anchor off its segment.
 */
function polishAnchorAlongDirection(strippedMotion is map, strippedSurface is map, t is number,
    seedUv is array, direction is array, sectionTolerance is number) returns array
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    const directionLength = sqrt(direction[0] ^ 2 + direction[1] ^ 2);
    if (directionLength < 1e-300)
    {
        return seedUv;
    }
    const unit = [direction[0] / directionLength, direction[1] / directionLength];
    var uv = seedUv;
    var traveled = 0;
    for (var iteration = 0; iteration < 12; iteration += 1)
    {
        const gradient = evaluateSectionGradientPointwise(stationMotion, strippedSurface, uv[0], uv[1], t);
        if (abs(gradient.value) <= sectionTolerance)
        {
            break;
        }
        const directionalDerivative = gradient.uDerivative * unit[0] + gradient.vDerivative * unit[1];
        if (abs(directionalDerivative) < 1e-30)
        {
            break;
        }
        var step = -gradient.value / directionalDerivative;
        step = clampToRange(step, -2 * directionLength - traveled, 2 * directionLength - traveled);
        traveled += step;
        uv = [uv[0] + step * unit[0], uv[1] + step * unit[1]];
    }
    return uv;
}

/**
 * One station's section, marched anchor to anchor and resampled at 2q - 1 arc-length
 * fractions: the even fractions are the station's fit grid row, the odd fractions are fresh
 * q-midpoint samples held out for certification.
 *
 * options (all optional): { sectionTolerance (residual, default 1e-12), marchStepSize
 * (uv units; default anchor distance / (2q)), marchMaxSteps (default max(400, 40q)),
 * sectionPolyline (a SHARED marched polyline for this station - clipped to these anchors and
 * resampled instead of marching, which is how two strips meeting at a seam resample one arc
 * rather than two; spec 2.3 and 7.1) }.
 * Returns { failed, reason?, uvRow, liftedRow, midLifted, worstResidual }.
 */
export function sectionSamplesAtStation(strippedMotion is map, strippedSurface is map, t is number,
    startAnchor is map, endAnchor is map, qCount is number, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    const sectionTolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const startUv = anchorUvAtStation(startAnchor, stationMotion, strippedSurface, t, sectionTolerance);
    const endUv = anchorUvAtStation(endAnchor, stationMotion, strippedSurface, t, sectionTolerance);
    const anchorDistance = sqrt((endUv[0] - startUv[0]) ^ 2 + (endUv[1] - startUv[1]) ^ 2);
    if (anchorDistance < 1e-12)
    {
        return { "failed" : true, "reason" : "coincident section anchors at t = " ~ t };
    }
    if (options.sectionPolyline != undefined)
    {
        const clipped = clipSectionPolylineToAnchors(options.sectionPolyline, startUv, endUv);
        if (clipped.failed)
        {
            return { "failed" : true, "reason" : clipped.reason ~ " (t = " ~ t ~ ")" };
        }
        return splitResampledRow(resampleAndPolishSection(stationMotion, strippedSurface, t,
                clipped.uvPoints, 2 * qCount - 1, sectionTolerance), qCount);
    }
    const stepSize = options.marchStepSize == undefined ? anchorDistance / (2 * qCount) : options.marchStepSize;
    const maxSteps = options.marchMaxSteps == undefined ? max(400, 40 * qCount) : options.marchMaxSteps;
    const march = marchSectionCurve(stationMotion, strippedSurface, t, startUv, endUv,
        { "stepSize" : stepSize, "maxSteps" : maxSteps, "tolerance" : sectionTolerance });
    if (!march.reachedEnd)
    {
        return { "failed" : true, "reason" : "section march did not reach the end anchor at t = " ~ t };
    }
    const resampled = resampleAndPolishSection(stationMotion, strippedSurface, t, march.uvPoints,
        2 * qCount - 1, sectionTolerance);
    return splitResampledRow(resampled, qCount);
}

/** Split a 2q - 1 resample into the q grid samples (even fractions) and q - 1 fresh midpoints. */
function splitResampledRow(resampled is map, qCount is number) returns map
{
    var uvRow = makeArray(qCount);
    var liftedRow = makeArray(qCount);
    var midLifted = makeArray(qCount - 1);
    for (var index = 0; index < qCount; index += 1)
    {
        uvRow[index] = resampled.uvSamples[2 * index];
        liftedRow[index] = resampled.liftedSamples[2 * index];
        if (index < qCount - 1)
        {
            midLifted[index] = resampled.liftedSamples[2 * index + 1];
        }
    }
    return { "failed" : false, "uvRow" : uvRow, "liftedRow" : liftedRow, "midLifted" : midLifted,
            "worstResidual" : resampled.worstResidual };
}

/**
 * March the closed section loop f(., ., tGlobal) = 0 from a seed already near it: predictor
 * perpendicular to the gradient, corrector along it, terminating when the march returns to
 * within one step of its (corrected) starting point after a minimum step count. The exact
 * start point is appended so the polyline closes identically.
 *
 * options: { stepSize (required), maxSteps (default 400), tolerance (default 1e-12),
 * minimumSteps (default 6) }. Returns { uvPoints, closed }.
 */
export function marchClosedSectionLoop(strippedMotion is map, strippedSurface is map, tGlobal is number,
    seedUv is array, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    const stepSize = options.stepSize;
    const maxSteps = options.maxSteps == undefined ? 400 : options.maxSteps;
    const tolerance = options.tolerance == undefined ? 1e-12 : options.tolerance;
    const minimumSteps = options.minimumSteps == undefined ? 6 : options.minimumSteps;
    const domain = fitSurfaceKnotDomain(strippedSurface);

    const seeded = correctedSectionUvAndGradient(stationMotion, strippedSurface, tGlobal, seedUv,
        domain, tolerance);
    var uv = seeded.uv;
    var carriedGradient = seeded.gradient;
    const startUv = uv;
    var uvPoints = makeArray(maxSteps + 2, uv);
    var pointCount = 1;
    var previousTangent = undefined;
    var closed = false;
    for (var step = 0; step < maxSteps; step += 1)
    {
        const gradient = carriedGradient != undefined ? carriedGradient :
            evaluateSectionGradientPointwise(stationMotion, strippedSurface, uv[0], uv[1], tGlobal);
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            break;
        }
        const gradientNorm = sqrt(gradientNormSquared);
        var tangent = [-gradient.vDerivative / gradientNorm, gradient.uDerivative / gradientNorm];
        if (previousTangent != undefined && tangent[0] * previousTangent[0] + tangent[1] * previousTangent[1] < 0)
        {
            tangent = [-tangent[0], -tangent[1]];
        }
        var predicted = [uv[0] + stepSize * tangent[0], uv[1] + stepSize * tangent[1]];
        predicted = [clampToRange(predicted[0], domain.uMin, domain.uMax),
            clampToRange(predicted[1], domain.vMin, domain.vMax)];
        const correction = correctedSectionUvAndGradient(stationMotion, strippedSurface, tGlobal,
            predicted, domain, tolerance);
        const corrected = correction.uv;
        carriedGradient = correction.gradient;
        uvPoints[pointCount] = corrected;
        pointCount += 1;
        previousTangent = tangent;
        if (step + 1 >= minimumSteps &&
            (corrected[0] - startUv[0]) ^ 2 + (corrected[1] - startUv[1]) ^ 2 <= stepSize ^ 2)
        {
            uvPoints[pointCount] = startUv;
            pointCount += 1;
            closed = true;
            break;
        }
        uv = corrected;
    }
    return { "uvPoints" : subArray(uvPoints, 0, pointCount), "closed" : closed };
}

/**
 * The first section crossing on a uv ray: sample outward from fromUv until f changes sign
 * against its value at fromUv, then bisect the bracketing segment. Returns { found, uv }.
 */
export function findSectionCrossingOnRay(strippedMotion is map, strippedSurface is map, t is number,
    fromUv is array, direction is array, maxDistance is number, sampleCount is number) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    const startValue = evaluateEnvelopePointwise(stationMotion, strippedSurface,
        fromUv[0], fromUv[1], t);
    var lowFraction = 0;
    var lowValue = startValue;
    var highFraction = undefined;
    for (var index = 1; index <= sampleCount; index += 1)
    {
        const fraction = index / sampleCount;
        const uv = [fromUv[0] + direction[0] * fraction * maxDistance,
            fromUv[1] + direction[1] * fraction * maxDistance];
        const value = evaluateEnvelopePointwise(stationMotion, strippedSurface, uv[0], uv[1], t);
        if (value * startValue <= 0)
        {
            highFraction = fraction;
            break;
        }
        lowFraction = fraction;
        lowValue = value;
    }
    if (highFraction == undefined)
    {
        return { "found" : false };
    }
    for (var iteration = 0; iteration < 60; iteration += 1)
    {
        const midFraction = 0.5 * (lowFraction + highFraction);
        const uv = [fromUv[0] + direction[0] * midFraction * maxDistance,
            fromUv[1] + direction[1] * midFraction * maxDistance];
        const value = evaluateEnvelopePointwise(stationMotion, strippedSurface, uv[0], uv[1], t);
        if (value * lowValue > 0)
        {
            lowFraction = midFraction;
            lowValue = value;
        }
        else
        {
            highFraction = midFraction;
        }
    }
    const fraction = 0.5 * (lowFraction + highFraction);
    return { "found" : true, "uv" : [fromUv[0] + direction[0] * fraction * maxDistance,
                fromUv[1] + direction[1] * fraction * maxDistance] };
}

/**
 * One island station's closed loop, sampled for the fit grid: seed on the fixed +u ray out of
 * centerUv (the SAME ray every station, which is what keeps the q origin consistent), marched
 * closed, oriented counterclockwise, and resampled at 2q - 1 fractions of its arc length
 * (the last fraction returns to the seed, so the row closes on itself).
 *
 * options: { sectionTolerance (default 1e-12) }.
 * Returns { failed, reason?, uvRow, liftedRow, midLifted, radius, worstResidual }.
 */
export function islandLoopSamples(strippedMotion is map, strippedSurface is map, centerUv is array,
    t is number, qCount is number, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    const sectionTolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const crossing = findSectionCrossingOnRay(stationMotion, strippedSurface, t, centerUv,
        [1, 0], domain.uMax - centerUv[0], 64);
    if (!crossing.found)
    {
        return { "failed" : true, "reason" : "no island loop crossing on the +u ray at t = " ~ t };
    }
    // Marched stations per resample interval, two by default. Denser marching buys nothing:
    // every resampled point is re-Newtoned onto f = 0 regardless, so polyline density affects
    // only how evenly q lands along the loop, never how far the samples sit from the envelope.
    // Same knob, and the same caveat, as the tube's - see tubeLoopSamples.
    const stepsPerSample = options.marchStepsPerSample == undefined ? 2 : options.marchStepsPerSample;
    const radius = sqrt((crossing.uv[0] - centerUv[0]) ^ 2 + (crossing.uv[1] - centerUv[1]) ^ 2);
    const stepSize = max(1e-9, 2 * PI * radius / (stepsPerSample * (2 * qCount - 2)));
    const loop = marchClosedSectionLoop(stationMotion, strippedSurface, t, crossing.uv,
        { "stepSize" : stepSize, "maxSteps" : max(400, 40 * qCount), "tolerance" : sectionTolerance });
    if (!loop.closed)
    {
        return { "failed" : true, "reason" : "island loop march did not close at t = " ~ t };
    }
    const oriented = orientLoopCounterClockwise(loop.uvPoints);
    const resampled = resampleClosedLoopSection(stationMotion, strippedSurface, t, oriented,
        2 * qCount, sectionTolerance);
    var result = splitClosedRow(resampled, qCount);
    result.radius = radius;
    return result;
}

/**
 * Resample a marched CLOSED loop at fixed fractions of its lifted arc length, re-Newton every
 * sample onto f = 0, and lift it. Unlike the open-section resampler the fractions are
 * sampleIndex / sampleCount, so the last sample stops one step short of the start instead of
 * duplicating it - a periodic fit wants n distinct points, and a duplicated closing point would
 * make its collocation system singular.
 * Returns { uvSamples, liftedSamples, worstResidual }.
 */
export function resampleClosedLoopSection(strippedMotion is map, strippedSurface is map, tGlobal is number,
    uvPoints is array, sampleCount is number, tolerance is number) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    const pointCount = size(uvPoints);
    var liftedPolyline = makeArray(pointCount);
    for (var index = 0; index < pointCount; index += 1)
    {
        liftedPolyline[index] = liftContactPoint(stationMotion, strippedSurface,
            uvPoints[index][0], uvPoints[index][1], tGlobal);
    }
    var cumulativeLengths = makeArray(pointCount, 0);
    for (var index = 1; index < pointCount; index += 1)
    {
        cumulativeLengths[index] = cumulativeLengths[index - 1] +
            distanceBetweenTriples(liftedPolyline[index], liftedPolyline[index - 1]);
    }
    const totalLength = cumulativeLengths[pointCount - 1];
    const domain = fitSurfaceKnotDomain(strippedSurface);

    var uvSamples = makeArray(sampleCount);
    var liftedSamples = makeArray(sampleCount);
    var worstResidual = 0;
    var cursor = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        const targetLength = totalLength * sampleIndex / sampleCount;
        while (cursor < pointCount - 2 && cumulativeLengths[cursor + 1] < targetLength)
        {
            cursor += 1;
        }
        const segmentLength = cumulativeLengths[cursor + 1] - cumulativeLengths[cursor];
        const fraction = segmentLength < 1e-300 ? 0 : (targetLength - cumulativeLengths[cursor]) / segmentLength;
        var uv = [uvPoints[cursor][0] + fraction * (uvPoints[cursor + 1][0] - uvPoints[cursor][0]),
            uvPoints[cursor][1] + fraction * (uvPoints[cursor + 1][1] - uvPoints[cursor][1])];
        const correction = correctedSectionUvAndGradient(stationMotion, strippedSurface, tGlobal,
            uv, domain, tolerance);
        uv = correction.uv;
        worstResidual = max(worstResidual,
            abs(correction.gradient != undefined ? correction.gradient.value :
                    evaluateEnvelopePointwise(stationMotion, strippedSurface, uv[0], uv[1], tGlobal)));
        uvSamples[sampleIndex] = uv;
        liftedSamples[sampleIndex] = liftContactPoint(stationMotion, strippedSurface, uv[0], uv[1], tGlobal);
    }
    return { "uvSamples" : uvSamples, "liftedSamples" : liftedSamples, "worstResidual" : worstResidual };
}

/** Split a 2q closed-loop resample into q grid samples and the q midpoints between them - the
    last midpoint spans the wrap, which is exactly where a clamped fit would kink. */
function splitClosedRow(resampled is map, qCount is number) returns map
{
    var uvRow = makeArray(qCount);
    var liftedRow = makeArray(qCount);
    var midLifted = makeArray(qCount);
    for (var index = 0; index < qCount; index += 1)
    {
        uvRow[index] = resampled.uvSamples[2 * index];
        liftedRow[index] = resampled.liftedSamples[2 * index];
        midLifted[index] = resampled.liftedSamples[2 * index + 1];
    }
    return { "failed" : false, "uvRow" : uvRow, "liftedRow" : liftedRow, "midLifted" : midLifted,
            "worstResidual" : resampled.worstResidual };
}

/** Reverse a closed uv polyline (first == last) when its signed area is clockwise. */
function orientLoopCounterClockwise(uvPoints is array) returns array
{
    var signedArea = 0;
    for (var index = 0; index + 1 < size(uvPoints); index += 1)
    {
        signedArea += uvPoints[index][0] * uvPoints[index + 1][1] - uvPoints[index + 1][0] * uvPoints[index][1];
    }
    if (signedArea >= 0)
    {
        return uvPoints;
    }
    const count = size(uvPoints);
    var reversed = makeArray(count);
    for (var index = 0; index < count; index += 1)
    {
        reversed[index] = uvPoints[count - 1 - index];
    }
    return reversed;
}

/**
 * The tensor-product B-spline surface through a rectangular grid of PLAIN-number points
 * (grid[i][j]: i down the station/u direction, j across the q/v direction) - NURBS Book A9.4
 * composed from the prescribed-parameter curve interpolator, with one extension: a fully
 * collapsed row (an island pole, every point identical) has no chord parameterization, so
 * degenerate rows are skipped when the v parameters are averaged. Clamped interpolation then
 * carries the collapse through exactly: every column's end control point IS its end data
 * point, so a collapsed data row becomes a collapsed control row and the surface closes to
 * the pole with zero error.
 *
 * Returns { uDegree, vDegree, isRational, isUPeriodic, isVPeriodic, controlPoints, uKnots,
 * vKnots, uParameters, vParameters }.
 */
export function interpolateFitGrid(grid is array, uDegree is number, vDegree is number) returns map
{
    return interpolateFitGrid(grid, uDegree, vDegree, false);
}

/**
 * Same, with the v (q) direction optionally PERIODIC - the island case. A closed section loop
 * fitted with a clamped v direction carries a tangent kink where the loop closes; on the island
 * fixture that kink dominates the whole patch's certified deviation (6.4e-3 in q against 2.7e-5
 * of true envelope error). A periodic v direction removes it, and
 * pole rows plus periodic longitude is exactly how a NURBS sphere is represented.
 *
 * Periodic input rows carry n DISTINCT points with no repeated closing point; the result is
 * stored in the module's wrap-padded convention (n + vDegree control columns against
 * n + 2*vDegree + 1 knots) that splineRefinementUtils' evaluators read directly.
 */
export function interpolateFitGrid(grid is array, uDegree is number, vDegree is number, vPeriodic is boolean) returns map
{
    return interpolateFitGrid(grid, uDegree, vDegree, vPeriodic, undefined);
}

/**
 * Same, with the v parameters PRESCRIBED rather than averaged off this grid's own rows.
 *
 * That is what lets two patches share a boundary row EXACTLY (spec 7.4's island split). A
 * clamped u interpolation puts its end CONTROL row exactly at its end DATA row, so two halves
 * cut at a shared station already carry the same boundary data - but each half then interpolates
 * that row in v at its own averaged parameters, and two different parameterizations of the same
 * eight points are two different curves. Measured on the island fixture: 7.2e-7 m apart with
 * each half's own parameters, bit-identical with the whole grid's.
 */
export function interpolateFitGrid(grid is array, uDegree is number, vDegree is number,
    vPeriodic is boolean, vParametersOverride) returns map
{
    return interpolateFitGrid(grid, uDegree, vDegree, vPeriodic, vParametersOverride, undefined);
}

/**
 * Same, with the u parameters PRESCRIBED as well.
 *
 * This is what lets two patches that share a whole BOUNDARY COLUMN emit the same curve for it.
 * The v = 0 boundary of a clamped tensor fit has control points equal to the stage-one u
 * interpolation of column 0, so it depends on that column's data and on the u parameters and on
 * nothing else - and averaged u parameters are taken over the grid's OWN columns, which two
 * neighbouring patches do not share. Prescribing them by station time instead makes the shared
 * boundary the same curve on both sides, exactly as prescribing v parameters does for the
 * island split's shared row.
 */
export function interpolateFitGrid(grid is array, uDegree is number, vDegree is number,
    vPeriodic is boolean, vParametersOverride, uParametersOverride) returns map
{
    const rowCount = size(grid);
    const columnCount = size(grid[0]);

    var uParameters = uParametersOverride;
    if (uParameters == undefined)
    {
        var uParameterSets = [];
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            var column = makeArray(rowCount);
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
            {
                column[rowIndex] = grid[rowIndex][columnIndex];
            }
            const chord = chordParametersPlain(column);
            if (chord.degenerate)
            {
                throw "solidSweepUtils fit: fit grid column " ~ columnIndex ~ " is a single repeated point - " ~
                    "the motion holds this q sample stationary, so there is no surface to fit.";
            }
            uParameterSets = append(uParameterSets, chord.parameters);
        }
        uParameters = averageParameterSets(uParameterSets, rowCount);
    }
    const vParameters = vParametersOverride == undefined ?
        fitGridVParameters(grid, vPeriodic, columnCount) : vParametersOverride;

    // Stage 1 - interpolate every column in u at the shared parameters.
    var stageOneControls = makeArray(columnCount);
    var uKnots = undefined;
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        var column = makeArray(rowCount);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            column[rowIndex] = grid[rowIndex][columnIndex];
        }
        const curve = interpolateBSplineCurveThroughPoints(column, uDegree, uParameters);
        stageOneControls[columnIndex] = curve.controlPoints;
        uKnots = curve.knots;
    }

    // Stage 2 - interpolate every stage-1 coefficient row in v.
    var controlPoints = makeArray(rowCount);
    var vKnots = undefined;
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        var coefficientRow = makeArray(columnCount);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            coefficientRow[columnIndex] = stageOneControls[columnIndex][rowIndex];
        }
        const curve = vPeriodic ? interpolatePeriodicRow(coefficientRow, vDegree, vParameters, 1) :
            interpolateBSplineCurveThroughPoints(coefficientRow, vDegree, vParameters);
        controlPoints[rowIndex] = curve.controlPoints;
        vKnots = curve.knots;
    }

    return {
            "uDegree" : uDegree,
            "vDegree" : vDegree,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : vPeriodic,
            "controlPoints" : controlPoints,
            "uKnots" : uKnots,
            "vKnots" : vKnots,
            "uParameters" : uParameters,
            "vParameters" : vParameters,
            "vPeriod" : 1
        };
}

/** A fit grid's averaged v parameters, over the rows that HAVE a chord parameterization - a
    collapsed pole row has none. */
function fitGridVParameters(grid is array, vPeriodic is boolean, columnCount is number) returns array
{
    var vParameterSets = [];
    for (var rowIndex = 0; rowIndex < size(grid); rowIndex += 1)
    {
        const chord = vPeriodic ? closedChordParametersPlain(grid[rowIndex]) : chordParametersPlain(grid[rowIndex]);
        if (!chord.degenerate)
        {
            vParameterSets = append(vParameterSets, chord.parameters);
        }
    }
    if (size(vParameterSets) == 0)
    {
        throw "solidSweepUtils fit: every fit grid row is collapsed - nothing to fit.";
    }
    return averageParameterSets(vParameterSets, columnCount);
}

/**
 * Interpolate n points at n prescribed parameters with a PERIODIC B-spline of the given degree
 * over one period: a knot at every data parameter makes the collocation system square, and the
 * n unknown control points wrap. Returns the wrap-padded pair the evaluators expect -
 * n + degree control points, n + 2*degree + 1 knots, with knots[j + n] = knots[j] + period so
 * the padding supplies every knot de Boor needs across [knots[degree], knots[degree + n]].
 *
 * parameters must be strictly increasing inside [parameters[0], parameters[0] + period).
 */
export function interpolatePeriodicRow(points is array, degree is number, parameters is array,
    period is number) returns map
{
    const count = size(points);
    if (count < degree + 1)
    {
        throw "solidSweepUtils fit: a periodic degree-" ~ degree ~ " interpolation needs at least " ~
            (degree + 1) ~ " points around the loop, got " ~ count ~ ".";
    }
    const knots = periodicInterpolationKnots(parameters, degree, period);

    var collocationRows = makeArray(count);
    for (var dataIndex = 0; dataIndex < count; dataIndex += 1)
    {
        var row = makeArray(count, 0);
        const spanIndex = leanEvaluationSpanIndex(knots, degree, parameters[dataIndex]);
        const basisValues = bSplineBasisValues(knots, degree, spanIndex, parameters[dataIndex]);
        for (var offset = 0; offset <= degree; offset += 1)
        {
            const wrappedIndex = (spanIndex - degree + offset) % count;
            row[wrappedIndex] = row[wrappedIndex] + basisValues[offset];
        }
        collocationRows[dataIndex] = row;
    }
    const inverseRows = inverse(matrix(collocationRows));

    var corePoints = makeArray(count, 0 * points[0]);
    for (var controlIndex = 0; controlIndex < count; controlIndex += 1)
    {
        var accumulated = 0 * points[0];
        for (var dataIndex = 0; dataIndex < count; dataIndex += 1)
        {
            accumulated = accumulated + inverseRows[controlIndex][dataIndex] * points[dataIndex];
        }
        corePoints[controlIndex] = accumulated;
    }

    var controlPoints = makeArray(count + degree, 0 * points[0]);
    for (var controlIndex = 0; controlIndex < count + degree; controlIndex += 1)
    {
        controlPoints[controlIndex] = corePoints[controlIndex % count];
    }
    return { "controlPoints" : controlPoints, "knots" : knots, "parameters" : parameters };
}

/** The wrap-padded knot array with a knot at every data parameter: knots[j] = t(j - degree). */
function periodicInterpolationKnots(parameters is array, degree is number, period is number) returns array
{
    const count = size(parameters);
    var knots = makeArray(count + 2 * degree + 1, 0);
    for (var knotIndex = 0; knotIndex < count + 2 * degree + 1; knotIndex += 1)
    {
        const baseIndex = knotIndex - degree;
        const wraps = floor(baseIndex / count);
        knots[knotIndex] = parameters[baseIndex - wraps * count] + wraps * period;
    }
    return knots;
}

/** Chord-length parameters of a plain-number polyline; degenerate when its total length vanishes. */
function chordParametersPlain(points is array) returns map
{
    const count = size(points);
    var distances = makeArray(count, 0);
    var totalLength = 0;
    for (var index = 1; index < count; index += 1)
    {
        distances[index] = norm(points[index] - points[index - 1]);
        totalLength += distances[index];
    }
    if (totalLength <= 1e-14)
    {
        return { "degenerate" : true };
    }
    var parameters = makeArray(count, 0);
    for (var index = 1; index < count - 1; index += 1)
    {
        parameters[index] = parameters[index - 1] + distances[index] / totalLength;
    }
    parameters[count - 1] = 1;
    return { "degenerate" : false, "parameters" : parameters };
}

/**
 * Chord-length parameters of a CLOSED polyline given by its n distinct points: the return
 * segment from the last point back to the first counts toward the period, so the parameters
 * land in [0, 1) and the period is exactly 1.
 */
function closedChordParametersPlain(points is array) returns map
{
    const count = size(points);
    var distances = makeArray(count, 0);
    var totalLength = 0;
    for (var index = 0; index < count; index += 1)
    {
        distances[index] = norm(points[(index + 1) % count] - points[index]);
        totalLength += distances[index];
    }
    if (totalLength <= 1e-14)
    {
        return { "degenerate" : true };
    }
    var parameters = makeArray(count, 0);
    for (var index = 1; index < count; index += 1)
    {
        parameters[index] = parameters[index - 1] + distances[index - 1] / totalLength;
    }
    return { "degenerate" : false, "parameters" : parameters };
}

/** Elementwise average of equal-length parameter arrays. */
function averageParameterSets(parameterSets is array, expectedCount is number) returns array
{
    var averaged = makeArray(expectedCount, 0);
    for (var index = 0; index < expectedCount; index += 1)
    {
        var total = 0;
        for (var parameterSet in parameterSets)
        {
            total += parameterSet[index];
        }
        averaged[index] = total / size(parameterSets);
    }
    return averaged;
}

/**
 * Fit one boundary-touching funnel component on the (q, t) rectangle (spec 7.2): assemble the
 * grid from anchored section marches, interpolate at bicubic degree, certify against fresh
 * Newton-converged envelope samples the fit never saw, double the deficient direction until
 * tolerance or the 60x60 budget, then remove redundant knots with the remaining slack.
 *
 * fitInput: {
 *     tStart, tEnd {number} : the component's t range,
 *     startAnchor, endAnchor {map} : see anchorUvAtStation,
 *     tolerance {number} : certified 3D deviation target (meters implied),
 *     eventTimes {array, default []} : t values every station set must contain,
 *     initialQCount {default 12}, initialStationCount {default 16},
 *     maxQCount {default 60}, maxStationCount {default 60},
 *     maxRefinementRounds {default 8},
 *     orientOutward {default true} : reverse the q direction when the orientation pass says the net
 *         would otherwise face into the swept volume (spec 6.6). Set false only to A/B the
 *         orientation pass - a knit needs the outward net.
 *     sectionTolerance, marchStepSize, marchMaxSteps : see sectionSamplesAtStation
 * }
 *
 * Returns { failed, reason? } or {
 *     failed : false, surface (plain numbers, emission via attachFitSurfaceUnits),
 *     worstDeviation, worstQDeviation, worstTDeviation, removalDeviation, certifiedBound,
 *     budgetHit ("SWEEP_FIT_BUDGET_HIT" semantics - best surface returned, over tolerance),
 *     refinementRounds, qCount, stationCount, stations, worstSectionResidual,
 *     orientation : certifyFitGridOrientation's map for the grid AS MARCHED - the lambda sign,
 *         its consistency (a component whose lambda flips FOLDS and must not be emitted), the
 *         outward verdict and its finite-difference cross-check,
 *     qReversed {boolean} : whether the q direction was then flipped. With orientOutward true
 *         and a decidable certificate, the returned surface faces outward either way; read
 *         `orientation.consistent` before trusting it, and `qReversed` only to line up other
 *         data against the emitted net's q parameters
 * }
 */
export function fitEnvelopeComponent(strippedMotion is map, strippedSurface is map, fitInput is map) returns map
{
    const input = mergeMaps({
                "eventTimes" : [],
                "initialQCount" : 12, "initialStationCount" : 16,
                "maxQCount" : 60, "maxStationCount" : 60,
                "maxRefinementRounds" : 8, "orientOutward" : true,
                "gradeStart" : false, "gradeEnd" : false
            }, fitInput);
    var qCount = max(4, input.initialQCount);
    var stationCount = max(4, input.initialStationCount);
    var budgetHit = false;
    var rounds = 0;
    var result = undefined;

    for (var round = 0; round < input.maxRefinementRounds; round += 1)
    {
        rounds = round + 1;
        const spacing = (input.tEnd - input.tStart) / (stationCount - 1);
        const stations = buildFitStations(input.tStart, input.tEnd, stationCount, input.eventTimes,
            0.25 * spacing, { "gradeStart" : input.gradeStart, "gradeEnd" : input.gradeEnd });

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var uvRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            // A seam station resamples the polyline the neighbouring strip marched, so both
            // sides of the seam read one array of numbers (spec 2.3). Interior stations and every
            // held-out certification row below march their own.
            var stationOptions = input;
            if (stationIndex == 0 && input.startSectionPolyline != undefined)
            {
                stationOptions = mergeMaps(input, { "sectionPolyline" : input.startSectionPolyline });
            }
            else if (stationIndex == size(stations) - 1 && input.endSectionPolyline != undefined)
            {
                stationOptions = mergeMaps(input, { "sectionPolyline" : input.endSectionPolyline });
            }
            const row = sectionSamplesAtStation(strippedMotion, strippedSurface, stations[stationIndex],
                input.startAnchor, input.endAnchor, qCount, stationOptions);
            if (row.failed)
            {
                return { "failed" : true, "reason" : row.reason };
            }
            liftedGrid[stationIndex] = row.liftedRow;
            qMidRows[stationIndex] = row.midLifted;
            uvRows[stationIndex] = row.uvRow;
            worstSectionResidual = max(worstSectionResidual, row.worstResidual);
        }

        // Orientation (spec 6.6). The net's normal is Phi_t x Phi_q, so which way the section
        // march ran decides whether it faces out of the swept volume; reversing q is exact and
        // costs nothing here, before interpolation. The certificate also carries the lambda
        // sign check, which is what says this component does not fold.
        const orientation = certifyFitGridOrientation(strippedMotion, strippedSurface, uvRows,
                stations, { "liftedGrid" : liftedGrid });
        const flipQ = input.orientOutward && orientation.flipRequired;
        if (flipQ)
        {
            for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
            {
                liftedGrid[stationIndex] = reverseFitGridRow(liftedGrid[stationIndex], false);
                qMidRows[stationIndex] = reverseFitGridMidRow(qMidRows[stationIndex]);
            }
        }
        const fitSurface = interpolateFitGrid(liftedGrid, 3, 3);

        // Certification: q midpoints at the stations attribute the q direction; fresh
        // sections at station midpoints attribute t (grid fractions) and the rest (odd
        // fractions).
        var worstQ = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            for (var midIndex = 0; midIndex < qCount - 1; midIndex += 1)
            {
                const seed = vector(fitSurface.uParameters[stationIndex],
                    0.5 * (fitSurface.vParameters[midIndex] + fitSurface.vParameters[midIndex + 1]));
                worstQ = max(worstQ, invertPointOnSurface(fitSurface, qMidRows[stationIndex][midIndex], seed,
                            CERTIFICATION_INVERSION).residual);
            }
        }
        var worstT = 0;
        var worstBoth = 0;
        for (var stationIndex = 0; stationIndex + 1 < size(stations); stationIndex += 1)
        {
            const tMid = 0.5 * (stations[stationIndex] + stations[stationIndex + 1]);
            const uSeed = 0.5 * (fitSurface.uParameters[stationIndex] + fitSurface.uParameters[stationIndex + 1]);
            const freshRow = sectionSamplesAtStation(strippedMotion, strippedSurface, tMid,
                input.startAnchor, input.endAnchor, qCount, input);
            if (freshRow.failed)
            {
                return { "failed" : true, "reason" : freshRow.reason };
            }
            // A held-out row is marched anchor to anchor like the grid rows were, so a flipped
            // q direction has to be applied to it too or every q parameter it is checked
            // against is mirrored.
            const freshLifted = flipQ ? reverseFitGridRow(freshRow.liftedRow, false) : freshRow.liftedRow;
            const freshMid = flipQ ? reverseFitGridMidRow(freshRow.midLifted) : freshRow.midLifted;
            for (var qIndex = 0; qIndex < qCount; qIndex += 1)
            {
                worstT = max(worstT, invertPointOnSurface(fitSurface, freshLifted[qIndex],
                            vector(uSeed, fitSurface.vParameters[qIndex]), CERTIFICATION_INVERSION).residual);
                if (qIndex < qCount - 1)
                {
                    worstBoth = max(worstBoth, invertPointOnSurface(fitSurface, freshMid[qIndex],
                                vector(uSeed, 0.5 * (fitSurface.vParameters[qIndex] + fitSurface.vParameters[qIndex + 1])),
                                CERTIFICATION_INVERSION).residual);
                }
            }
        }
        const worstDeviation = max(worstQ, max(worstT, worstBoth));

        result = {
                "failed" : false,
                "surface" : fitSurface,
                "worstDeviation" : worstDeviation,
                "worstQDeviation" : worstQ,
                "worstTDeviation" : worstT,
                "removalDeviation" : 0,
                "certifiedBound" : worstDeviation,
                "budgetHit" : false,
                "refinementRounds" : rounds,
                "qCount" : qCount,
                "stationCount" : size(stations),
                "stations" : stations,
                "worstSectionResidual" : worstSectionResidual,
                "orientation" : orientation,
                "qReversed" : flipQ
            };
        if (worstDeviation <= input.tolerance)
        {
            break;
        }
        var needQ = worstQ > input.tolerance;
        var needT = worstT > input.tolerance;
        if (!needQ && !needT)
        {
            needQ = true;
            needT = true;
        }
        const grownQ = min(2 * qCount - 1, input.maxQCount);
        const grownStations = min(2 * stationCount - 1, input.maxStationCount);
        const canGrow = (needQ && grownQ > qCount) || (needT && grownStations > stationCount);
        if (!canGrow || round == input.maxRefinementRounds - 1)
        {
            budgetHit = true;
            break;
        }
        if (needQ)
        {
            qCount = grownQ;
        }
        if (needT)
        {
            stationCount = grownStations;
        }
    }

    result.budgetHit = budgetHit;
    if (!budgetHit)
    {
        const slack = input.tolerance - result.worstDeviation;
        if (slack > 0)
        {
            const cleaned = removeRedundantSurfaceKnots(attachFitSurfaceUnits(result.surface), slack * meter);
            result.removalDeviation = cleaned.deviation / meter;
            var cleanedPlain = stripFitSurfaceUnits(cleaned);
            cleanedPlain.uParameters = result.surface.uParameters;
            cleanedPlain.vParameters = result.surface.vParameters;
            result.surface = cleanedPlain;
            result.certifiedBound = result.worstDeviation + result.removalDeviation;
        }
    }
    return result;
}

/**
 * Fit one grazing island as a pole-collapsed patch (spec 7.1, probe 4): stations span the
 * fitted t range, a pole row is the lifted stationary point repeated, and every other station
 * is a closed loop seeded on the fixed +u ray out of the interpolated island center.
 * Certification and refinement follow fitEnvelopeComponent; knot cleanup is skipped so a pole
 * row stays exactly collapsed.
 *
 * The fitted range need not be the whole island. tStart / tEnd CLIP it, and a clipped end
 * carries a full loop row instead of a pole - which is what makes the three island patch
 * shapes (spec 7.4): two poles, one pole, or none. Only the last two are emittable, and only
 * the last two can be fold-free: lambda at a t-extreme is exactly f_t there (f_u = f_v = 0 at
 * an extreme), and the two extremes of one island have opposite f_t signs, so a TWO-pole island
 * always folds somewhere between them.
 *
 * islandInput: {
 *     birth, death {map} : { u, v, t } from island refinement (refineBlockStationaryPoint),
 *     tStart {number, default birth.t}, tEnd {number, default death.t} : the clipped range,
 *     tolerance {number},
 *     initialQCount {default 12}, initialStationCount {default 16},
 *     maxQCount {default 60}, maxStationCount {default 60},
 *     maxRefinementRounds {default 8}, sectionTolerance {default 1e-12}
 * }
 *
 * Returns the fitEnvelopeComponent result shape plus poleStartPoint / poleEndPoint (the
 * lifted 3D poles, plain numbers - meaningful only where the matching poleAtStart /
 * poleAtEnd is true), poleAtStart / poleAtEnd {boolean}, and liftedGrid, the (station, q) grid
 * of lifted points as fitted, which is what the emission splits.
 */
export function fitIslandComponent(strippedMotion is map, strippedSurface is map, islandInput is map) returns map
{
    const input = mergeMaps({
                "initialQCount" : 12, "initialStationCount" : 16,
                "maxQCount" : 60, "maxStationCount" : 60,
                "maxRefinementRounds" : 8, "orientOutward" : true
            }, islandInput);
    const birth = input.birth;
    const death = input.death;
    const tStart = input.tStart == undefined ? birth.t : input.tStart;
    const tEnd = input.tEnd == undefined ? death.t : input.tEnd;
    // A clip lands on a pole only when it lands ON the extreme; anywhere else the row is a
    // full loop. The window is relative to the island's own t extent, not absolute.
    const poleWindow = 1e-9 * max(1e-30, death.t - birth.t);
    const poleAtStart = abs(tStart - birth.t) <= poleWindow;
    const poleAtEnd = abs(tEnd - death.t) <= poleWindow;
    const poleStartPoint = liftContactPoint(strippedMotion, strippedSurface, birth.u, birth.v, birth.t);
    const poleEndPoint = liftContactPoint(strippedMotion, strippedSurface, death.u, death.v, death.t);
    var qCount = max(4, input.initialQCount);
    var stationCount = max(4, input.initialStationCount);
    var budgetHit = false;
    var rounds = 0;
    var result = undefined;

    for (var round = 0; round < input.maxRefinementRounds; round += 1)
    {
        rounds = round + 1;
        const stations = buildFitStations(tStart, tEnd, stationCount, [], 0);

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var uvRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            const atStart = stationIndex == 0 && poleAtStart;
            const atEnd = stationIndex == size(stations) - 1 && poleAtEnd;
            if (atStart || atEnd)
            {
                const pole = atStart ? birth : death;
                liftedGrid[stationIndex] = makeArray(qCount, atStart ? poleStartPoint : poleEndPoint);
                qMidRows[stationIndex] = undefined;
                // A collapsed uv row, which the orientation certificate recognizes and skips -
                // there is no q direction at a pole.
                uvRows[stationIndex] = makeArray(qCount, [pole.u, pole.v]);
                continue;
            }
            const loop = islandLoopSamples(strippedMotion, strippedSurface,
                islandCenterAt(birth, death, stations[stationIndex]), stations[stationIndex], qCount, input);
            if (loop.failed)
            {
                return { "failed" : true, "reason" : loop.reason };
            }
            liftedGrid[stationIndex] = loop.liftedRow;
            qMidRows[stationIndex] = loop.midLifted;
            uvRows[stationIndex] = loop.uvRow;
            worstSectionResidual = max(worstSectionResidual, loop.worstResidual);
        }

        // Orientation (spec 6.6). The island's loops are marched counterclockwise in uv, which
        // is a consistent q origin and direction but says nothing about which way the patch
        // faces - lambda does. Reversing a CLOSED row holds its first sample fixed, so the q
        // origin the loop marching established survives the flip.
        const orientation = certifyFitGridOrientation(strippedMotion, strippedSurface, uvRows,
                stations, { "liftedGrid" : liftedGrid });
        const flipQ = input.orientOutward && orientation.flipRequired;
        if (flipQ)
        {
            for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
            {
                if (qMidRows[stationIndex] == undefined)
                {
                    continue;                                   // a pole row: reversing it is a no-op
                }
                liftedGrid[stationIndex] = reverseFitGridRow(liftedGrid[stationIndex], true);
                qMidRows[stationIndex] = reverseFitGridMidRow(qMidRows[stationIndex]);
            }
        }
        const fitSurface = interpolateFitGrid(liftedGrid, 3, 3, true);

        var worstQ = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            if (qMidRows[stationIndex] == undefined)
            {
                continue;
            }
            for (var midIndex = 0; midIndex < qCount; midIndex += 1)
            {
                const seed = vector(fitSurface.uParameters[stationIndex],
                    periodicMidParameter(fitSurface.vParameters, midIndex, 1));
                worstQ = max(worstQ, invertPointOnSurface(fitSurface, qMidRows[stationIndex][midIndex], seed,
                            CERTIFICATION_INVERSION).residual);
            }
        }
        var worstT = 0;
        var worstBoth = 0;
        for (var stationIndex = 0; stationIndex + 1 < size(stations); stationIndex += 1)
        {
            const tMid = 0.5 * (stations[stationIndex] + stations[stationIndex + 1]);
            const uSeed = 0.5 * (fitSurface.uParameters[stationIndex] + fitSurface.uParameters[stationIndex + 1]);
            const freshLoop = islandLoopSamples(strippedMotion, strippedSurface,
                islandCenterAt(birth, death, tMid), tMid, qCount, input);
            if (freshLoop.failed)
            {
                return { "failed" : true, "reason" : freshLoop.reason };
            }
            // Held-out loops are marched the same way the grid rows were, so a flipped q has
            // to reach them too.
            const freshLifted = flipQ ? reverseFitGridRow(freshLoop.liftedRow, true) : freshLoop.liftedRow;
            const freshMid = flipQ ? reverseFitGridMidRow(freshLoop.midLifted) : freshLoop.midLifted;
            for (var qIndex = 0; qIndex < qCount; qIndex += 1)
            {
                worstT = max(worstT, invertPointOnSurface(fitSurface, freshLifted[qIndex],
                            vector(uSeed, fitSurface.vParameters[qIndex]), CERTIFICATION_INVERSION).residual);
                worstBoth = max(worstBoth, invertPointOnSurface(fitSurface, freshMid[qIndex],
                            vector(uSeed, periodicMidParameter(fitSurface.vParameters, qIndex, 1)),
                            CERTIFICATION_INVERSION).residual);
            }
        }
        const worstDeviation = max(worstQ, max(worstT, worstBoth));

        result = {
                "failed" : false,
                "surface" : fitSurface,
                "worstDeviation" : worstDeviation,
                "worstQDeviation" : worstQ,
                "worstTDeviation" : worstT,
                "removalDeviation" : 0,
                "certifiedBound" : worstDeviation,
                "budgetHit" : false,
                "refinementRounds" : rounds,
                "qCount" : qCount,
                "stationCount" : size(stations),
                "stations" : stations,
                "worstSectionResidual" : worstSectionResidual,
                "orientation" : orientation,
                "qReversed" : flipQ,
                "poleStartPoint" : poleStartPoint,
                "poleEndPoint" : poleEndPoint,
                "poleAtStart" : poleAtStart,
                "poleAtEnd" : poleAtEnd,
                "tStart" : tStart,
                "tEnd" : tEnd,
                "liftedGrid" : liftedGrid
            };
        if (worstDeviation <= input.tolerance)
        {
            break;
        }
        var needQ = worstQ > input.tolerance;
        var needT = worstT > input.tolerance;
        if (!needQ && !needT)
        {
            needQ = true;
            needT = true;
        }
        const grownQ = min(2 * qCount - 1, input.maxQCount);
        const grownStations = min(2 * stationCount - 1, input.maxStationCount);
        const canGrow = (needQ && grownQ > qCount) || (needT && grownStations > stationCount);
        if (!canGrow || round == input.maxRefinementRounds - 1)
        {
            budgetHit = true;
            break;
        }
        if (needQ)
        {
            qCount = grownQ;
        }
        if (needT)
        {
            stationCount = grownStations;
        }
    }

    result.budgetHit = budgetHit;
    return result;
}

/**
 * Emit an island component's fitted patch as kernel B-spline face(s) (spec 7.4). The three
 * shapes and why only two of them ship:
 *
 *   - NO pole (both ends clipped by the sweep's own t range): cylinder topology, two genuine
 *     boundary loops, one closed direction. Emitted as ONE v-periodic face, which is the shape
 *     spec 7.7 proved the kernel accepts.
 *   - ONE pole (the other end clipped): a cap, one collapsed boundary row against one genuine
 *     loop. Probe 4's accepted shape.
 *   - TWO poles: rejected by opCreateBSplineSurface outright (spec 7.4 - a net with both
 *     u-boundary rows collapsed and v closed has no non-degenerate boundary to form a face
 *     from), so it splits at a mid-t station into two single-pole caps that share that station's
 *     loop row EXACTLY. Both halves interpolate the shared row against the WHOLE grid's v
 *     parameters, without which the two boundary curves pass through the same points along
 *     different parameterizations and miss each other by 7.2e-7 m on this module's fixture.
 *
 * A two-pole island cannot be fold-free, so v1 never actually emits one: lambda at a t-extreme
 * IS f_t there, and an island's two extremes carry opposite f_t signs (whichever way the loop
 * shrinks to a point, the inside sign of f is the same at both ends, so the two Hessians have
 * the same definiteness and the f_t signs must differ). `requireFoldFree` is therefore the
 * default, and the split path exists for the emission SHAPE - which the trimming work of spec
 * 10 will need once a folded component can be cut down to its fold-free pieces.
 *
 * options: { requireFoldFree {boolean, default true} : refuse a component whose lambda changes
 * sign, reporting SWEEP_ISLAND_UNSUPPORTED rather than emitting a folded patch }.
 *
 * Returns { refused {boolean}, reason {string, when refused}, poleCount, shape {string},
 * patchCount, faceCount, declaredPeriodic {array of boolean}, patchIds {array of Id},
 * seamError {number, 0 unless split}, cut {number, the shared station index when split} }.
 */
export function emitIslandPatches(context is Context, id is Id, islandFit is map, options is map) returns map
{
    const settings = mergeMaps({ "requireFoldFree" : true }, options);
    const poleCount = (islandFit.poleAtStart ? 1 : 0) + (islandFit.poleAtEnd ? 1 : 0);
    if (settings.requireFoldFree && !islandFit.orientation.lambdaSignConsistent)
    {
        return {
                "refused" : true,
                "reason" : "SWEEP_ISLAND_UNSUPPORTED: lambda changes sign across this island, so " ~
                "the envelope folds inside it and the patch would be locally self-intersecting.",
                "poleCount" : poleCount,
                "shape" : "folded",
                "patchCount" : 0, "faceCount" : 0,
                "declaredPeriodic" : [], "patchIds" : [], "seamError" : 0
            };
    }
    if (poleCount < 2)
    {
        const patch = emitFitSurfacePatch(context, id + "islandPatch", islandFit.surface);
        return {
                "refused" : patch.refused,
                "reason" : patch.refused ? ("SWEEP_ISLAND_UNSUPPORTED: " ~ patch.reason) : "",
                "poleCount" : poleCount,
                "shape" : poleCount == 0 ? "loop ends" : "one pole",
                "patchCount" : patch.refused ? 0 : 1, "faceCount" : patch.faceCount,
                "declaredPeriodic" : patch.refused ? [] : [patch.declaredPeriodic],
                "patchIds" : patch.refused ? [] : [patch.id], "seamError" : 0
            };
    }
    const split = splitIslandFitGrid(islandFit);
    if (split.failed)
    {
        return {
                "refused" : true, "reason" : split.reason, "poleCount" : poleCount,
                "shape" : "two poles", "patchCount" : 0, "faceCount" : 0,
                "declaredPeriodic" : [], "patchIds" : [], "seamError" : 0
            };
    }
    const startCap = emitFitSurfacePatch(context, id + "islandCapStart", split.startSurface);
    const endCap = emitFitSurfacePatch(context, id + "islandCapEnd", split.endSurface);
    var patchIds = [];
    var declaredPeriodic = [];
    for (var cap in [startCap, endCap])
    {
        if (!cap.refused)
        {
            patchIds = append(patchIds, cap.id);
            declaredPeriodic = append(declaredPeriodic, cap.declaredPeriodic);
        }
    }
    const refused = startCap.refused || endCap.refused;
    return {
            "refused" : refused,
            "reason" : refused ? ("SWEEP_ISLAND_UNSUPPORTED: start cap: " ~ startCap.reason ~
                    " end cap: " ~ endCap.reason) : "",
            "poleCount" : poleCount,
            "shape" : "two poles split at a shared row",
            "patchCount" : size(patchIds), "faceCount" : startCap.faceCount + endCap.faceCount,
            "declaredPeriodic" : declaredPeriodic,
            "patchIds" : patchIds,
            "seamError" : split.seamError, "cut" : split.cut,
            "startSurface" : split.startSurface, "endSurface" : split.endSurface
        };
}

/**
 * Split a two-pole island's fitted grid at a mid-t station into two single-pole caps (spec
 * 7.4), each interpolated against the WHOLE grid's v parameters so the shared row's boundary
 * curve is one curve and not two.
 *
 * The cut is the middle station, held so that each half keeps the four rows a cubic
 * interpolation needs - which is why an island fitted on fewer than seven stations reports
 * rather than splits.
 *
 * Returns { failed, reason? , cut, startSurface, endSurface, sharedRow, seamError } where
 * seamError is the worst coordinate gap between the two halves' shared CONTROL row - zero by
 * construction, and reported so that a change which breaks it cannot pass unnoticed.
 */
export function splitIslandFitGrid(islandFit is map) returns map
{
    const grid = islandFit.liftedGrid;
    const rowCount = size(grid);
    if (rowCount < 7)
    {
        return { "failed" : true, "reason" : "an island split needs at least 7 stations so that " ~
                    "both halves keep 4 rows for a cubic interpolation, got " ~ rowCount ~ "." };
    }
    const cut = min(max(floor((rowCount - 1) / 2), 3), rowCount - 4);
    const vParameters = islandFit.surface.vParameters;
    const startSurface = interpolateFitGrid(subArray(grid, 0, cut + 1), 3, 3, true, vParameters);
    const endSurface = interpolateFitGrid(subArray(grid, cut, rowCount), 3, 3, true, vParameters);

    const startBoundary = startSurface.controlPoints[size(startSurface.controlPoints) - 1];
    const endBoundary = endSurface.controlPoints[0];
    var seamError = 0;
    for (var index = 0; index < size(startBoundary); index += 1)
    {
        seamError = max(seamError, norm(startBoundary[index] - endBoundary[index]));
    }
    return {
            "failed" : false, "cut" : cut,
            "startSurface" : startSurface, "endSurface" : endSurface,
            "sharedRow" : grid[cut], "seamError" : seamError
        };
}

/**
 * One kernel B-spline face from a fit surface: the periodic declaration first, then the
 * closed-clamped non-periodic declaration if the kernel refuses the first (spec 7.4 found the
 * flag is not what a whole island fails on, but a shape the kernel has never been asked for
 * gets asked both ways rather than assumed).
 *
 * BOTH attempts are guarded, and each takes its own operation id because a thrown operation
 * still registers the id it was given. A refusal is REPORTED, never thrown: emission has to
 * degrade to a named seam rather than take the whole feature down with it (spec 9), and the
 * live run that found this was the two-pole island split, where the kernel refuses both
 * declarations and the unguarded second attempt aborted the test before it printed anything.
 *
 * Returns { faceCount, declaredPeriodic {boolean}, id, refused {boolean}, reason {string} }.
 */
export function emitFitSurfacePatch(context is Context, id is Id, surface is map) returns map
{
    const withUnits = attachFitSurfaceUnits(surface);
    var reason = "";
    for (var declarePeriodic in [true, false])
    {
        const attemptName = declarePeriodic ? "periodic" : "clamped";
        const attemptId = id + attemptName;
        try
        {
            opCreateBSplineSurface(context, attemptId, {
                        "bSplineSurface" : kernelFitSurface(withUnits, declarePeriodic)
                    });
        }
        catch (error)
        {
            reason = reason ~ " " ~ attemptName ~ ": " ~ toString(error);
        }
        const faces = evaluateQuery(context, qCreatedBy(attemptId, EntityType.FACE));
        if (size(faces) > 0)
        {
            return { "faceCount" : size(faces), "declaredPeriodic" : declarePeriodic,
                    "id" : attemptId, "refused" : false, "reason" : "" };
        }
    }
    return {
            "faceCount" : 0, "declaredPeriodic" : false, "id" : undefined, "refused" : true,
            "reason" : "the kernel refused a " ~ size(surface.controlPoints) ~ "x" ~
            size(surface.controlPoints[0]) ~ " net in both declarations -" ~ reason
        };
}

/** The parameter halfway between sample index and its successor around a period - the last one
    spans the wrap, which is the seam a clamped fit would kink at. */
function periodicMidParameter(parameters is array, index is number, period is number) returns number
{
    const count = size(parameters);
    const nextParameter = index + 1 < count ? parameters[index + 1] : parameters[0] + period;
    return 0.5 * (parameters[index] + nextParameter);
}

/** The island center's uv at a station, interpolated linearly between birth and death. */
function islandCenterAt(birth is map, death is map, t is number) returns array
{
    const fraction = (t - birth.t) / (death.t - birth.t);
    return [birth.u + fraction * (death.u - birth.u), birth.v + fraction * (death.v - birth.v)];
}

/**
 * The contact loop of a REVOLVED tool face at one station, as a closed 3-D polyline, with no
 * marching and no surface evaluation (spec 6.10, the exact-emission rung).
 *
 * This is the seam the fitting layer was missing. `fitTubeComponent` consumed `tubeLoopSamples`,
 * which takes a control net and marches it; the analytic layer could produce contact curves but had
 * nowhere to hand them.
 *
 * The loop is built in the generator's own parameterization because that is the direction the
 * closed form solves: at each generator parameter `u`, `f` is a degree-2 trigonometric polynomial in
 * theta whose roots are exact. The other way round - theta given, solve for u - would be a root find
 * on a spline, which is what we are removing.
 *
 * A convex tool gives TWO theta roots at each `u` the loop reaches and NONE outside it, so the loop
 * is two branches meeting at turning points. Two things about that shape decide the accuracy, and a
 * first version got both wrong:
 *
 * 1. *The branches are separated by theta CONTINUITY, not by root index.* Sorted roots swap places
 *    when a branch crosses theta = 0, and indexing them braids the two halves together.
 * 2. *Uniform `u` is the wrong sampling.* At a turning point the two roots merge and `dtheta/du`
 *    runs away, so equal steps in `u` produce enormous chords exactly where the curve turns hardest.
 *    Measured: a uniform 96-sample loop fitted to 2.8e-4 where the marched route reached 4.6e-8.
 *    So the sampling is CHORD-DRIVEN - subdivide any interval whose 3-D chord exceeds the target,
 *    and bisect toward each turning point until the loop actually closes on it. `u` is a parameter
 *    to solve at, not a place to put a sample.
 *
 * options: { profileSamples (default 64), targetChord (default: no refinement),
 * maxLoopRefinements (default 8) }. Returns { failed, reason, points, worstResidual }.
 */
export function analyticContactLoopPolyline(strippedMotion is map, frame is RevolvedContactFrame,
    t is number, options is map) returns map
{
    const sampleCount = options.profileSamples == undefined ? 64 : options.profileSamples;
    const maxRefinements = options.maxLoopRefinements == undefined ? 8 : options.maxLoopRefinements;
    const motionSample = evaluateMotionSample(strippedMotion, t);
    const pullback = analyticContactPullback(motionSample, frame);

    // Pass one: a uniform scan, only to find WHERE the loop lives and to seed the branches. The
    // generator derivatives on this grid are the same at every station, so a multi-station caller
    // passes the table in rather than paying for it once per station.
    const seedParameters = analyticProfileParameters(frame, sampleCount);
    const seedDerivatives = options.generatorTable != undefined &&
        size(options.generatorTable) == size(seedParameters) ?
        options.generatorTable : analyticGeneratorDerivativeTable(frame, sampleCount);
    var samples = [];
    var worstResidual = 0;
    var previousLower = undefined;
    var previousUpper = undefined;
    for (var index = 0; index < size(seedParameters); index += 1)
    {
        const pair = analyticLoopRootsAt(frame, pullback, seedParameters[index], previousLower,
                previousUpper, options, seedDerivatives[index]);
        if (!pair.found)
        {
            continue;
        }
        worstResidual = max(worstResidual, pair.residual);
        samples = append(samples, pair);
        previousLower = pair.lower;
        previousUpper = pair.upper;
    }
    if (size(samples) < 3)
    {
        return { "failed" : true, "reason" : "the analytic contact loop reached only " ~ size(samples) ~
                " generator parameter(s) with two roots - this face does not make a tube at t = " ~ t,
                "entries" : [], "worstResidual" : worstResidual };
    }

    // Pass two: close on the turning points. Between the outermost `u` that has two roots and the
    // first that has none lies the merge, and without it the loop is left open by however coarse the
    // seed scan was.
    const seedStep = (frame.profileEnd - frame.profileStart) / max(1, sampleCount - 1);
    samples = closeLoopEnd(frame, pullback, samples, seedStep, true, options);
    samples = closeLoopEnd(frame, pullback, samples, seedStep, false, options);

    // Pass three: subdivide by CHORD. The target comes from the caller because only it knows how
    // finely the loop will be resampled; with none, the seed scan stands.
    if (options.targetChord != undefined && options.targetChord > 0)
    {
        for (var round = 0; round < maxRefinements; round += 1)
        {
            var refined = [];
            var inserted = 0;
            for (var index = 0; index + 1 < size(samples); index += 1)
            {
                refined = append(refined, samples[index]);
                const here = samples[index];
                const next = samples[index + 1];
                const lowerChord = norm(analyticWorldContactPoint(frame, motionSample, next.derivatives, next.lower) -
                        analyticWorldContactPoint(frame, motionSample, here.derivatives, here.lower));
                const upperChord = norm(analyticWorldContactPoint(frame, motionSample, next.derivatives, next.upper) -
                        analyticWorldContactPoint(frame, motionSample, here.derivatives, here.upper));
                if (max(lowerChord, upperChord) <= options.targetChord)
                {
                    continue;
                }
                const middle = analyticLoopRootsAt(frame, pullback, 0.5 * (here.u + next.u),
                        here.lower, here.upper, options);
                if (middle.found)
                {
                    refined = append(refined, middle);
                    inserted += 1;
                }
            }
            refined = append(refined, samples[size(samples) - 1]);
            samples = refined;
            if (inserted == 0)
            {
                break;
            }
        }
    }

    // Up one branch and back down the other closes the loop. Each entry carries the parameter and
    // the branch it came from, because the resampler solves EXACTLY at an interpolated parameter
    // rather than interpolating the point - so it has to know which branch it is continuing.
    const count = size(samples);
    var entries = makeArray(2 * count, undefined);
    var entryCount = 0;
    for (var index = 0; index < count; index += 1)
    {
        entries[entryCount] = { "u" : samples[index].u, "theta" : samples[index].lower, "branch" : 0,
                "point" : analyticWorldContactPoint(frame, motionSample, samples[index].derivatives,
                        samples[index].lower) };
        entryCount += 1;
    }
    for (var index = count - 1; index >= 0; index -= 1)
    {
        entries[entryCount] = { "u" : samples[index].u, "theta" : samples[index].upper, "branch" : 1,
                "point" : analyticWorldContactPoint(frame, motionSample, samples[index].derivatives,
                        samples[index].upper) };
        entryCount += 1;
    }
    return { "failed" : false, "reason" : "", "entries" : subArray(entries, 0, entryCount),
            "worstResidual" : worstResidual };
}

/**
 * The two theta roots at one generator parameter, assigned to branches by CONTINUITY with the
 * previous station rather than by sorted order. Returns { found, u, lower, upper, residual }.
 */
function analyticLoopRootsAt(frame is RevolvedContactFrame, pullback is map, u is number,
    previousLower, previousUpper, options is map) returns map
{
    return analyticLoopRootsAt(frame, pullback, u, previousLower, previousUpper, options,
            leanCurveDerivatives(frame.profile, u, 1));
}

/** Same, on generator derivatives already in hand - see `analyticGeneratorDerivativeTable`. */
function analyticLoopRootsAt(frame is RevolvedContactFrame, pullback is map, u is number,
    previousLower, previousUpper, options is map, derivatives is array) returns map
{
    const solved = solveTrigPolynomialRoots(analyticRevolvedThetaPolynomial(frame, pullback, derivatives),
            0, options);
    if (solved.identicallyZero || size(solved.roots) < 2)
    {
        return { "found" : false, "u" : u, "lower" : 0, "upper" : 0,
                "derivatives" : derivatives, "residual" : 0 };
    }
    var lower = solved.roots[0];
    var upper = solved.roots[size(solved.roots) - 1];
    if (previousLower != undefined)
    {
        const straight = angularSeparation(lower, previousLower) + angularSeparation(upper, previousUpper);
        const swapped = angularSeparation(upper, previousLower) + angularSeparation(lower, previousUpper);
        if (swapped < straight)
        {
            const held = lower;
            lower = upper;
            upper = held;
        }
    }
    return { "found" : true, "u" : u, "lower" : lower, "upper" : upper,
            "derivatives" : derivatives, "residual" : solved.worstResidual };
}

/**
 * Walk toward a turning point by bisection and append the samples found. The merge is where the two
 * roots coincide, so the loop's own end; stopping at whatever the seed scan happened to reach leaves
 * a chord across the sharpest part of the curve.
 */
function closeLoopEnd(frame is RevolvedContactFrame, pullback is map, samples is array,
    seedStep is number, atStart is boolean, options is map) returns array
{
    const endSample = atStart ? samples[0] : samples[size(samples) - 1];
    const direction = atStart ? -1 : 1;
    var inside = endSample.u;
    var outside = endSample.u + direction * seedStep;
    if (outside < frame.profileStart)
    {
        outside = frame.profileStart;
    }
    if (outside > frame.profileEnd)
    {
        outside = frame.profileEnd;
    }
    var found = [];
    for (var step = 0; step < 24; step += 1)
    {
        const middle = 0.5 * (inside + outside);
        const pair = analyticLoopRootsAt(frame, pullback, middle, endSample.lower, endSample.upper, options);
        if (pair.found)
        {
            inside = middle;
            found = append(found, pair);
        }
        else
        {
            outside = middle;
        }
        if (abs(outside - inside) <= 1e-12)
        {
            break;
        }
    }
    if (size(found) == 0)
    {
        return samples;
    }
    // The bisection visits parameters out of order; sorting by u puts them back on the branch.
    var ordered = sortLoopSamplesByParameter(concatenateArrays([samples, found]));
    return atStart ? ordered : ordered;
}

/** Loop samples in ascending generator parameter. */
function sortLoopSamplesByParameter(samples is array) returns array
{
    var ordered = samples;
    for (var pass = 1; pass < size(ordered); pass += 1)
    {
        const held = ordered[pass];
        var slot = pass;
        while (slot > 0 && ordered[slot - 1].u > held.u)
        {
            ordered[slot] = ordered[slot - 1];
            slot -= 1;
        }
        ordered[slot] = held;
    }
    return ordered;
}
/** Separation of two angles, taking the shorter way round. */
function angularSeparation(first is number, second is number) returns number
{
    const raw = abs(positiveModulo(first - second, 2 * PI));
    return min(raw, 2 * PI - raw);
}

/** A contact point in WORLD space: the frame's local point, into the tool frame, through the motion. */
function analyticWorldContactPoint(frame is map, motionSample is map, u is number,
    v is number) returns Vector
{
    const local = analyticLocalPointAndNormal(frame, u, v);
    return analyticWorldPointOfLocal(frame, motionSample, local);
}

/** Same, on generator derivatives already in hand. */
function analyticWorldContactPoint(frame is map, motionSample is map, derivatives is array,
    v is number) returns Vector
{
    return analyticWorldPointOfLocal(frame, motionSample,
            analyticLocalPointAndNormal(frame, derivatives, v));
}

/** The frame-to-world half both of the above share. */
function analyticWorldPointOfLocal(frame is map, motionSample is map, local is map) returns Vector
{
    const basis = frame.basis;
    const toolPoint = frame.origin + local.point[0] * basis[0] + local.point[1] * basis[1] +
        local.point[2] * basis[2];
    return motionSample.rotation * toolPoint + motionSample.translation;
}

/**
 * One tube station's loop, resampled the way `tubeLoopSamples` resamples a marched one: `qCount`
 * points at equal arc-length fractions for the fit row, and `qCount` midpoints held out for
 * certification. Same return shape, so the fitting layer cannot tell which produced it.
 *
 * The q origin is the loop's highest point along the frame's own axis, which is a property of the
 * geometry rather than of a marching seed - so it is the same point at every station without any
 * seeding to carry forward, and the seam it fixes is stable by construction.
 */
export function analyticTubeLoopSamples(strippedMotion is map, frame is RevolvedContactFrame,
    t is number, qCount is number, options is map) returns map
{
    const motionSample = evaluateMotionSample(strippedMotion, t);
    const pullback = analyticContactPullback(motionSample, frame);
    const loop = analyticContactLoopPolyline(strippedMotion, frame, t, options);
    if (loop.failed)
    {
        return { "failed" : true, "reason" : loop.reason };
    }
    const entries = loop.entries;
    const count = size(entries);

    // Arc length around the closed loop, from the coarse polyline. The polyline's ONLY job is to
    // map arc length to a generator parameter; the output points are then solved exactly at those
    // parameters, so its resolution moves where the samples land and never how far off the curve
    // they are. Refining it until linear interpolation was accurate enough - the first version -
    // generated thousands of points to interpolate a hundred and cost 31 s, because bisecting `u`
    // near a turning point shrinks the chord like a square root and never converges.
    var cumulative = makeArray(count + 1, 0);
    for (var step = 0; step < count; step += 1)
    {
        cumulative[step + 1] = cumulative[step] +
            norm(entries[(step + 1) % count].point - entries[step].point);
    }
    const total = cumulative[count];
    if (total <= 0)
    {
        return { "failed" : true, "reason" : "the analytic contact loop has zero length at t = " ~ t };
    }

    // q origin: the loop's highest point along the frame's own axis. A property of the geometry, so
    // it is the same point at every station with no seed to carry forward.
    var originOffset = 0;
    var highest = -1e300;
    for (var index = 0; index < count; index += 1)
    {
        const height = dot(entries[index].point, frame.basis[2]);
        if (height > highest)
        {
            highest = height;
            originOffset = cumulative[index];
        }
    }

    var liftedRow = makeArray(qCount, vector(0, 0, 0));
    var midLifted = makeArray(qCount, vector(0, 0, 0));
    var worstResidual = loop.worstResidual;
    for (var q = 0; q < qCount; q += 1)
    {
        liftedRow[q] = exactLoopPointAtLength(frame, pullback, motionSample, entries, cumulative,
                total, originOffset + total * q / qCount);
        midLifted[q] = exactLoopPointAtLength(frame, pullback, motionSample, entries, cumulative,
                total, originOffset + total * (q + 0.5) / qCount);
    }
    return {
            "failed" : false, "reason" : "",
            "liftedRow" : liftedRow, "midLifted" : midLifted,
            "uvRow" : undefined, "seedV" : undefined, "uTravel" : 2 * PI,
            "worstResidual" : worstResidual
        };
}

/**
 * The contact point at a given arc length around the loop, solved EXACTLY rather than interpolated.
 *
 * The polyline brackets the target and gives a generator parameter to aim at; the theta root is then
 * solved in closed form there, seeded on the bracketing sample's branch so it stays on the right one.
 * Across a turning point the two neighbours belong to different branches and there is no shared
 * parameter to solve at - that one segment falls back to the chord, which is both short and the
 * flattest part of the merge.
 */
function exactLoopPointAtLength(frame is RevolvedContactFrame, pullback is map, motionSample is map,
    entries is array, cumulative is array, total is number, targetLength is number) returns Vector
{
    const count = size(entries);
    const target = positiveModulo(targetLength, total);
    var step = count - 1;
    for (var candidate = 0; candidate < count; candidate += 1)
    {
        if (cumulative[candidate + 1] >= target)
        {
            step = candidate;
            break;
        }
    }
    const from = entries[step];
    const to = entries[(step + 1) % count];
    const spanLength = cumulative[step + 1] - cumulative[step];
    const alpha = spanLength <= 0 ? 0 : (target - cumulative[step]) / spanLength;
    if (from.branch != to.branch)
    {
        return from.point + alpha * (to.point - from.point);
    }
    const u = from.u + alpha * (to.u - from.u);
    // One de Boor serves both the theta solve and the point it produces.
    const derivatives = leanCurveDerivatives(frame.profile, u, 1);
    const solved = solveTrigPolynomialRoots(
            analyticRevolvedThetaPolynomial(frame, pullback, derivatives), 0, {});
    if (solved.identicallyZero || size(solved.roots) == 0)
    {
        return from.point + alpha * (to.point - from.point);
    }
    // Whichever root continues the bracketing sample's branch.
    var best = solved.roots[0];
    var bestSeparation = angularSeparation(best, from.theta);
    for (var root in solved.roots)
    {
        const separation = angularSeparation(root, from.theta);
        if (separation < bestSeparation)
        {
            best = root;
            bestSeparation = separation;
        }
    }
    return analyticWorldContactPoint(frame, motionSample, derivatives, best);
}
/**
 * A unit-stripped B-spline NET, as a type rather than as `map`.
 *
 * This exists so the marched path and the analytic path cannot be confused for one another. A
 * tagged contact frame IS still a map, so a `strippedSurface is map` parameter accepts one happily
 * and then fails deep inside on a missing knot vector - or worse, an added analytic overload becomes
 * ambiguous against it and nothing resolves. Naming the net makes the two overload sets disjoint:
 * a net has knot vectors and no `kind`, a frame has a tag and no knots.
 */
export type StrippedSurface typecheck canBeStrippedSurface;

/** @internal */
export predicate canBeStrippedSurface(value)
{
    value is map;
    value.uKnots is array;
    value.vKnots is array;
    value.uDegree is number;
    value.vDegree is number;
    value.controlPoints is array;
}

/**
 * Which way the analytic tube's fitted patch faces, without a control net.
 *
 * `certifyFitGridOrientation` decides this from lambda on the tool's own (u, v) chart, which an
 * analytic face does not have. The same question answered geometrically: the patch's own normal at
 * an interior grid node, against the outward direction at that contact point. For a closed convex
 * tool the outward direction is the contact point measured from the transported frame origin, and
 * the decision is a sign with a large margin rather than a tolerance.
 *
 * Returns { flipRequired, margin }.
 */
export function analyticFitGridOrientation(strippedMotion is map, frame is RevolvedContactFrame,
    liftedGrid is array, stations is array) returns map
{
    const stationIndex = floor(0.5 * size(stations));
    const row = liftedGrid[stationIndex];
    const qCount = size(row);
    const previousStation = liftedGrid[max(0, stationIndex - 1)];
    const nextStation = liftedGrid[min(size(stations) - 1, stationIndex + 1)];
    const qIndex = floor(0.25 * qCount);

    const alongStation = nextStation[qIndex] - previousStation[qIndex];
    const alongQ = row[(qIndex + 1) % qCount] - row[(qIndex + qCount - 1) % qCount];
    const patchNormal = cross(alongStation, alongQ);

    const motionSample = evaluateMotionSample(strippedMotion, stations[stationIndex]);
    const centre = motionSample.rotation * frame.origin + motionSample.translation;
    const outward = row[qIndex] - centre;
    const margin = dot(patchNormal, outward);
    return { "flipRequired" : margin < 0, "margin" : margin };
}

/**
 * The tube fit for an ANALYTIC surface of revolution: same grid, same interpolation, same
 * certification, with the loops produced in closed form instead of marched (spec 6.10, tier 0).
 *
 * This is the overload that keeps the expensive path from being chosen by accident. The marched
 * twin takes a `StrippedSurface`; this one takes a `RevolvedContactFrame`; a caller holding a frame
 * cannot reach the marcher and a caller holding a net cannot reach this. That is the whole point -
 * the section-9.4 assembly fixture spent 18.4 s marching a face whose contact curve is closed form,
 * not because anything chose that, but because `is map` accepted both and the net-based overload was
 * the only one there.
 *
 * What is NOT duplicated: the fit grid interpolation, the held-out certification, and the refinement
 * loop all work on lifted 3-D rows and on the FIT surface, so they are the same code operating on
 * rows from a different producer.
 */
export function fitTubeComponent(strippedMotion is map, frame is RevolvedContactFrame, tubeInput is map) returns map
{
    const input = mergeMaps({
                "eventTimes" : [],
                "initialQCount" : 12, "initialStationCount" : 16,
                "maxQCount" : 60, "maxStationCount" : 60,
                "maxRefinementRounds" : 8, "orientOutward" : true
            }, tubeInput);
    var qCount = max(4, input.initialQCount);
    var stationCount = max(4, input.initialStationCount);
    var budgetHit = false;
    var rounds = 0;
    var result = undefined;

    // The generator grid every station scans, evaluated once. r, z, r' and z' depend on the frame
    // alone, so recomputing them per station is the one piece of pure repetition left on the
    // analytic route: this fit solves the same parameters at five stations and four midpoints.
    const generatorTable = analyticGeneratorDerivativeTable(frame,
        input.profileSamples == undefined ? 64 : input.profileSamples);
    const loopOptions = mergeMaps(input, { "generatorTable" : generatorTable });

    for (var round = 0; round < input.maxRefinementRounds; round += 1)
    {
        rounds = round + 1;
        const spacing = (input.tEnd - input.tStart) / (stationCount - 1);
        const stations = buildFitStations(input.tStart, input.tEnd, stationCount, input.eventTimes, 0.25 * spacing);

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            const loop = analyticTubeLoopSamples(strippedMotion, frame, stations[stationIndex], qCount, loopOptions);
            if (loop.failed)
            {
                return { "failed" : true, "reason" : loop.reason };
            }
            liftedGrid[stationIndex] = loop.liftedRow;
            qMidRows[stationIndex] = loop.midLifted;
            worstSectionResidual = max(worstSectionResidual, loop.worstResidual);
        }

        const orientation = analyticFitGridOrientation(strippedMotion, frame, liftedGrid, stations);
        const flipQ = input.orientOutward && orientation.flipRequired;
        if (flipQ)
        {
            for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
            {
                liftedGrid[stationIndex] = reverseFitGridRow(liftedGrid[stationIndex], true);
                qMidRows[stationIndex] = reverseFitGridMidRow(qMidRows[stationIndex]);
            }
        }
        const fitSurface = interpolateFitGrid(liftedGrid, 3, 3, true);

        var worstQ = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            for (var midIndex = 0; midIndex < qCount; midIndex += 1)
            {
                const seed = vector(fitSurface.uParameters[stationIndex],
                    periodicMidParameter(fitSurface.vParameters, midIndex, 1));
                worstQ = max(worstQ, invertPointOnSurface(fitSurface, qMidRows[stationIndex][midIndex], seed,
                            CERTIFICATION_INVERSION).residual);
            }
        }
        var worstT = 0;
        var worstBoth = 0;
        for (var stationIndex = 0; stationIndex + 1 < size(stations); stationIndex += 1)
        {
            const tMid = 0.5 * (stations[stationIndex] + stations[stationIndex + 1]);
            const uSeed = 0.5 * (fitSurface.uParameters[stationIndex] + fitSurface.uParameters[stationIndex + 1]);
            const freshLoop = analyticTubeLoopSamples(strippedMotion, frame, tMid, qCount, loopOptions);
            if (freshLoop.failed)
            {
                return { "failed" : true, "reason" : freshLoop.reason };
            }
            const freshLifted = flipQ ? reverseFitGridRow(freshLoop.liftedRow, true) : freshLoop.liftedRow;
            const freshMid = flipQ ? reverseFitGridMidRow(freshLoop.midLifted) : freshLoop.midLifted;
            for (var qIndex = 0; qIndex < qCount; qIndex += 1)
            {
                worstT = max(worstT, invertPointOnSurface(fitSurface, freshLifted[qIndex],
                            vector(uSeed, fitSurface.vParameters[qIndex]), CERTIFICATION_INVERSION).residual);
                worstBoth = max(worstBoth, invertPointOnSurface(fitSurface, freshMid[qIndex],
                            vector(uSeed, periodicMidParameter(fitSurface.vParameters, qIndex, 1)),
                            CERTIFICATION_INVERSION).residual);
            }
        }
        const worstDeviation = max(worstQ, max(worstT, worstBoth));

        result = {
                "failed" : false,
                "surface" : fitSurface,
                "worstDeviation" : worstDeviation,
                "worstQDeviation" : worstQ,
                "worstTDeviation" : worstT,
                "removalDeviation" : 0,
                "certifiedBound" : worstDeviation,
                "budgetHit" : false,
                "refinementRounds" : rounds,
                "qCount" : qCount,
                "stationCount" : size(stations),
                "stations" : stations,
                "worstSectionResidual" : worstSectionResidual,
                "orientation" : orientation,
                "qReversed" : flipQ,
                "startLoop" : liftedGrid[0],
                "endLoop" : liftedGrid[size(stations) - 1]
            };
        if (worstDeviation <= input.tolerance)
        {
            break;
        }
        var needQ = worstQ > input.tolerance;
        var needT = worstT > input.tolerance;
        if (!needQ && !needT)
        {
            needQ = true;
            needT = true;
        }
        const grownQ = min(2 * qCount - 1, input.maxQCount);
        const grownStations = min(2 * stationCount - 1, input.maxStationCount);
        const canGrow = (needQ && grownQ > qCount) || (needT && grownStations > stationCount);
        if (!canGrow || round == input.maxRefinementRounds - 1)
        {
            budgetHit = true;
            break;
        }
        if (needQ)
        {
            qCount = grownQ;
        }
        if (needT)
        {
            stationCount = grownStations;
        }
    }

    result.budgetHit = budgetHit;
    return result;
}

/**
 * Fit one funnel component whose sections are CLOSED LOOPS THAT WRAP THE PERIODIC u SEAM of the
 * tool face (spec 7.5) - the shape a closed smooth tool makes when it travels roughly along its
 * own parameterization axis, and the lateral surface of the first watertight swept solid.
 *
 * It is the third component shape, between the two the module already fits. A rectangle
 * component is anchored at two co-edge branches and open in q; an island is a closed loop that
 * shrinks to a pole at each end of its t range; a tube is a closed loop at EVERY station, all
 * the way to t0 and t1, so the patch is a cylinder: q periodic, t clamped, no poles, and its
 * two t-edges are the contact loops the start and end caps are trimmed to.
 *
 * The periodic seam needs no rewindow and no pre-split, which settles the question deferred out
 * of steps 4 and 5. Nothing about a section march cares where the face's seam is: the marcher
 * carries u UNWRAPPED and wraps it only on the way into an evaluator, so one loop is one
 * monotone strip of arc length whatever the seam does, and the (q, t) reparameterization hands
 * the kernel a rectangle that never mentions u. What the seam costs is one flag: the q direction
 * of the fit must be declared periodic, exactly as the island's q must be (spec 7.4).
 *
 * tubeInput: {
 *     tStart, tEnd {number} : the component's t range,
 *     tolerance {number} : certified 3D deviation target (meters implied),
 *     uSeed {number, default the u domain midpoint} : the FIXED meridian every station is
 *         seeded on, which is what pins the q origin from row to row,
 *     vMargin {number, default 0} : v excluded at both ends of the domain - set it past the
 *         degenerate pole rows of a surface of revolution, where the normal vanishes and f is
 *         identically zero for reasons that have nothing to do with contact,
 *     eventTimes {array, default []}, initialQCount {default 12}, initialStationCount
 *         {default 16}, maxQCount {default 60}, maxStationCount {default 60},
 *     maxRefinementRounds {default 8}, sectionTolerance {default 1e-12},
 *     meridianSamples {default 32}
 * }
 *
 * Returns the fitEnvelopeComponent result shape (failed / surface / worstDeviation /
 * worstQDeviation / worstTDeviation / certifiedBound / budgetHit / refinementRounds / qCount /
 * stationCount / stations / worstSectionResidual) plus startLoop and endLoop, the lifted t0 and
 * t1 section rows the caps are cut with. Knot cleanup is skipped for the same reason the island
 * skips it: removal perturbs control rows, and here it would also have to preserve the wrap.
 */
export function fitTubeComponent(strippedMotion is map, strippedSurface is StrippedSurface, tubeInput is map) returns map
{
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const input = mergeMaps({
                "eventTimes" : [],
                "initialQCount" : 12, "initialStationCount" : 16,
                "maxQCount" : 60, "maxStationCount" : 60,
                "maxRefinementRounds" : 8, "orientOutward" : true,
                "uSeed" : 0.5 * (domain.uMin + domain.uMax)
            }, tubeInput);
    var qCount = max(4, input.initialQCount);
    var stationCount = max(4, input.initialStationCount);
    var budgetHit = false;
    var rounds = 0;
    var result = undefined;

    for (var round = 0; round < input.maxRefinementRounds; round += 1)
    {
        rounds = round + 1;
        const spacing = (input.tEnd - input.tStart) / (stationCount - 1);
        const stations = buildFitStations(input.tStart, input.tEnd, stationCount, input.eventTimes, 0.25 * spacing);

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var uvRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        // Each station seeds its meridian search from its predecessor's crossing, so a face
        // whose meridian is cut more than once stays on one branch instead of hopping.
        var previousSeedV = undefined;
        var firstSeedV = undefined;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            const loop = tubeLoopSamples(strippedMotion, strippedSurface, stations[stationIndex],
                input.uSeed, qCount, mergeMaps(input, { "preferV" : previousSeedV }));
            if (loop.failed)
            {
                return { "failed" : true, "reason" : loop.reason };
            }
            liftedGrid[stationIndex] = loop.liftedRow;
            qMidRows[stationIndex] = loop.midLifted;
            uvRows[stationIndex] = loop.uvRow;
            worstSectionResidual = max(worstSectionResidual, loop.worstResidual);
            previousSeedV = loop.seedV;
            if (stationIndex == 0)
            {
                firstSeedV = loop.seedV;
            }
        }

        // Orientation (spec 6.6). The tube's loops carry u unwrapped, so their q direction is
        // whichever way the wrapping march happened to run - lambda decides whether that faces
        // out. Reversing a CLOSED row holds the seed meridian's sample fixed.
        const orientation = certifyFitGridOrientation(strippedMotion, strippedSurface, uvRows,
                stations, { "liftedGrid" : liftedGrid });
        const flipQ = input.orientOutward && orientation.flipRequired;
        if (flipQ)
        {
            for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
            {
                liftedGrid[stationIndex] = reverseFitGridRow(liftedGrid[stationIndex], true);
                qMidRows[stationIndex] = reverseFitGridMidRow(qMidRows[stationIndex]);
            }
        }
        const fitSurface = interpolateFitGrid(liftedGrid, 3, 3, true);

        // Certification, direction-resolved exactly as the rectangle and island fits do it:
        // q midpoints at the stations attribute q, fresh whole loops at midpoint stations
        // attribute t, and the midpoints of those bound the rest. Every q midpoint counts here,
        // the wrap one included - it is the sample a clamped q would have kinked at.
        var worstQ = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            for (var midIndex = 0; midIndex < qCount; midIndex += 1)
            {
                const seed = vector(fitSurface.uParameters[stationIndex],
                    periodicMidParameter(fitSurface.vParameters, midIndex, 1));
                worstQ = max(worstQ, invertPointOnSurface(fitSurface, qMidRows[stationIndex][midIndex], seed,
                            CERTIFICATION_INVERSION).residual);
            }
        }
        var worstT = 0;
        var worstBoth = 0;
        var freshSeedV = undefined;
        for (var stationIndex = 0; stationIndex + 1 < size(stations); stationIndex += 1)
        {
            const tMid = 0.5 * (stations[stationIndex] + stations[stationIndex + 1]);
            const uSeed = 0.5 * (fitSurface.uParameters[stationIndex] + fitSurface.uParameters[stationIndex + 1]);
            const freshLoop = tubeLoopSamples(strippedMotion, strippedSurface, tMid, input.uSeed, qCount,
                mergeMaps(input, { "preferV" : freshSeedV == undefined ? firstSeedV : freshSeedV }));
            if (freshLoop.failed)
            {
                return { "failed" : true, "reason" : freshLoop.reason };
            }
            freshSeedV = freshLoop.seedV;
            // Held-out loops are marched the same way the grid rows were, so a flipped q has
            // to reach them too.
            const freshLifted = flipQ ? reverseFitGridRow(freshLoop.liftedRow, true) : freshLoop.liftedRow;
            const freshMid = flipQ ? reverseFitGridMidRow(freshLoop.midLifted) : freshLoop.midLifted;
            for (var qIndex = 0; qIndex < qCount; qIndex += 1)
            {
                worstT = max(worstT, invertPointOnSurface(fitSurface, freshLifted[qIndex],
                            vector(uSeed, fitSurface.vParameters[qIndex]), CERTIFICATION_INVERSION).residual);
                worstBoth = max(worstBoth, invertPointOnSurface(fitSurface, freshMid[qIndex],
                            vector(uSeed, periodicMidParameter(fitSurface.vParameters, qIndex, 1)),
                            CERTIFICATION_INVERSION).residual);
            }
        }
        const worstDeviation = max(worstQ, max(worstT, worstBoth));

        result = {
                "failed" : false,
                "surface" : fitSurface,
                "worstDeviation" : worstDeviation,
                "worstQDeviation" : worstQ,
                "worstTDeviation" : worstT,
                "removalDeviation" : 0,
                "certifiedBound" : worstDeviation,
                "budgetHit" : false,
                "refinementRounds" : rounds,
                "qCount" : qCount,
                "stationCount" : size(stations),
                "stations" : stations,
                "worstSectionResidual" : worstSectionResidual,
                "orientation" : orientation,
                "qReversed" : flipQ,
                "startLoop" : liftedGrid[0],
                "endLoop" : liftedGrid[size(stations) - 1]
            };
        if (worstDeviation <= input.tolerance)
        {
            break;
        }
        var needQ = worstQ > input.tolerance;
        var needT = worstT > input.tolerance;
        if (!needQ && !needT)
        {
            needQ = true;
            needT = true;
        }
        const grownQ = min(2 * qCount - 1, input.maxQCount);
        const grownStations = min(2 * stationCount - 1, input.maxStationCount);
        const canGrow = (needQ && grownQ > qCount) || (needT && grownStations > stationCount);
        if (!canGrow || round == input.maxRefinementRounds - 1)
        {
            budgetHit = true;
            break;
        }
        if (needQ)
        {
            qCount = grownQ;
        }
        if (needT)
        {
            stationCount = grownStations;
        }
    }

    result.budgetHit = budgetHit;
    return result;
}

/**
 * One tube station's wrapping loop, sampled for the fit grid: seeded on the fixed meridian,
 * marched with u wrapping, and resampled at 2q arc-length fractions in one call - the even
 * fractions are the fit row, the odd ones are held-out midpoints for certification, and the
 * last midpoint spans the wrap.
 *
 * options: everything fitTubeComponent's input carries; preferV picks the meridian crossing.
 * Returns { failed, reason?, uvRow, liftedRow, midLifted, worstResidual, seedV, uTravel }.
 */
export function tubeLoopSamples(strippedMotion is map, strippedSurface is StrippedSurface, t is number,
    uSeed is number, qCount is number, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    const domain = fitSurfaceKnotDomain(strippedSurface);
    const sectionTolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const seed = tubeSeedOnMeridian(stationMotion, strippedSurface, t, uSeed, options);
    if (!seed.found)
    {
        return { "failed" : true, "reason" : "no tube section crossing on the u = " ~ uSeed ~
                    " meridian at t = " ~ t };
    }
    // Marched points per resample interval, two by default, as the island loop uses: every
    // resampled point is re-Newtoned onto f = 0 regardless, so density only evens out where q
    // lands. It is a knob because it is also the single biggest term in this function's cost -
    // the march and its corrector are about three quarters of a station's surface evaluations,
    // and they scale straight off it. Dropping it to 1 is spec 11.2's lever C and has to be
    // MEASURED, not assumed: a coarser polyline seeds the resample's corrector worse, which
    // buys some of the saving back, and it widens the closure test's radius.
    const stepsPerSample = options.marchStepsPerSample == undefined ? 2 : options.marchStepsPerSample;
    const period = domain.uMax - domain.uMin;
    const stepSize = max(1e-9, period / (2 * stepsPerSample * qCount));
    const loop = marchWrappingSectionLoop(stationMotion, strippedSurface, t, seed.uv,
        mergeMaps(options, { "stepSize" : stepSize, "maxSteps" : max(400, 40 * qCount),
                    "tolerance" : sectionTolerance }));
    if (!loop.closed)
    {
        return { "failed" : true, "reason" : "the tube loop march did not close at t = " ~ t ~
                    (loop.hitVBoundary ? " - it ran into the v boundary, so this component reaches a " ~
                        "pole or a face edge and is not a tube" : "") };
    }
    if (loop.uTravel == 0)
    {
        return { "failed" : true, "reason" : "the section loop at t = " ~ t ~ " closed without " ~
                    "wrapping the u seam - it is an island, not a tube" };
    }
    const resampled = resampleWrappingLoopSection(stationMotion, strippedSurface, t, loop.uvPoints,
        2 * qCount, sectionTolerance);
    var result = splitClosedRow(resampled, qCount);
    result.seedV = seed.uv[1];
    result.uTravel = loop.uTravel;
    return result;
}

/**
 * Where f(., ., t) = 0 crosses the FIXED meridian u = uSeed: a Newton continuation from the
 * previous station's crossing when there is one, else a v scan between the margins for a sign
 * change, bisected. Seeding every station on the same meridian is what keeps the q origin from
 * drifting around the loop - the island fit's fixed +u ray, one dimension over.
 *
 * options: { vMargin (default 0), meridianSamples (default 32), preferV (default undefined) }.
 * Returns { found, uv, crossingCount } - crossingCount 0 on the continuation path, which never
 * scans.
 */
export function tubeSeedOnMeridian(strippedMotion is map, strippedSurface is map, t is number,
    uSeed is number, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    const domain = fitSurfaceKnotDomain(strippedSurface);
    const uPeriodic = strippedSurface.isUPeriodic == true;
    const margin = options.vMargin == undefined ? 0 : options.vMargin;
    const sampleCount = options.meridianSamples == undefined ? 32 : options.meridianSamples;
    const vLow = domain.vMin + margin;
    const vHigh = domain.vMax - margin;

    if (options.preferV != undefined)
    {
        const continued = polishMeridianCrossing(stationMotion, strippedSurface, t, uSeed,
            options.preferV, vLow, vHigh, domain, uPeriodic);
        if (continued.converged)
        {
            return { "found" : true, "uv" : [uSeed, continued.v], "crossingCount" : 0 };
        }
    }

    var previousV = vLow;
    var previousValue = wrappedEnvelopeValue(stationMotion, strippedSurface, [uSeed, vLow], t,
            domain, uPeriodic);
    var bracketLow = undefined;
    var bracketHigh = undefined;
    var bracketLowValue = 0;
    var crossingCount = 0;
    for (var index = 1; index <= sampleCount; index += 1)
    {
        const v = vLow + (vHigh - vLow) * index / sampleCount;
        const value = wrappedEnvelopeValue(stationMotion, strippedSurface, [uSeed, v], t,
                domain, uPeriodic);
        if (previousValue * value <= 0)
        {
            crossingCount += 1;
            const midpoint = 0.5 * (previousV + v);
            if (bracketLow == undefined || (options.preferV != undefined &&
                        abs(midpoint - options.preferV) < abs(0.5 * (bracketLow + bracketHigh) - options.preferV)))
            {
                bracketLow = previousV;
                bracketHigh = v;
                bracketLowValue = previousValue;
            }
        }
        previousV = v;
        previousValue = value;
    }
    if (bracketLow == undefined)
    {
        return { "found" : false, "crossingCount" : 0 };
    }
    var low = bracketLow;
    var high = bracketHigh;
    var lowValue = bracketLowValue;
    for (var iteration = 0; iteration < 48; iteration += 1)
    {
        const mid = 0.5 * (low + high);
        const value = wrappedEnvelopeValue(stationMotion, strippedSurface, [uSeed, mid], t,
                domain, uPeriodic);
        if (value * lowValue > 0)
        {
            low = mid;
            lowValue = value;
        }
        else
        {
            high = mid;
        }
    }
    const polished = polishMeridianCrossing(stationMotion, strippedSurface, t, uSeed,
        0.5 * (low + high), vLow, vHigh, domain, uPeriodic);
    return { "found" : true, "uv" : [uSeed, polished.v], "crossingCount" : crossingCount };
}

/**
 * March the closed section loop of a face at one station, letting it WRAP the periodic u seam
 * rather than stalling against it. The polyline is carried in UNWRAPPED u and wrapped only on
 * the way into an evaluator, so arc length, closure, and the resample all see one monotone
 * strip instead of a curve that teleports at the seam.
 *
 * Closure is tested against the seed's images u0 + k * period for k in {1, 0, -1}, wrap first.
 * k = 0 means the loop closed WITHOUT wrapping - an island, not a tube - and is reported as
 * uTravel 0 rather than quietly fitted as a tube. The march also stops if it reaches vMargin of
 * the v domain, because a component that runs into a pole or a face edge is not a tube either.
 *
 * options: { stepSize, maxSteps (default 400), tolerance (default 1e-12), minimumSteps
 * (default 6), vMargin (default 0) }.
 * Returns { closed, uvPoints (unwrapped u, the seed's closing image appended), uTravel,
 * hitVBoundary }.
 */
export function marchWrappingSectionLoop(strippedMotion is map, strippedSurface is map, tGlobal is number,
    seedUv is array, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    const stepSize = options.stepSize;
    const maxSteps = options.maxSteps == undefined ? 400 : options.maxSteps;
    const tolerance = options.tolerance == undefined ? 1e-12 : options.tolerance;
    const minimumSteps = options.minimumSteps == undefined ? 6 : options.minimumSteps;
    const margin = options.vMargin == undefined ? 0 : options.vMargin;
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const uPeriodic = strippedSurface.isUPeriodic == true;
    const period = uPeriodic ? domain.uMax - domain.uMin : 0;

    const seeded = correctedUvAndGradient(stationMotion, strippedSurface, tGlobal, seedUv, domain,
        uPeriodic, tolerance);
    var uv = seeded.uv;
    // The corrector's own last evaluation is at the point it returns, which is exactly where
    // this loop wants the tangent. Carrying it across saves one order-2 evaluation per step.
    var carriedGradient = seeded.gradient;
    const startUv = uv;
    var uvPoints = makeArray(maxSteps + 2, uv);
    var pointCount = 1;
    var previousTangent = undefined;
    var closed = false;
    var uTravel = 0;
    var hitVBoundary = false;
    for (var step = 0; step < maxSteps; step += 1)
    {
        const gradient = carriedGradient != undefined ? carriedGradient :
            wrappedEnvelopeGradient(stationMotion, strippedSurface, uv, tGlobal, domain, uPeriodic);
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            break;
        }
        const gradientNorm = sqrt(gradientNormSquared);
        var tangent = [-gradient.vDerivative / gradientNorm, gradient.uDerivative / gradientNorm];
        if (previousTangent == undefined)
        {
            // The first step fixes the direction of travel as +u, so every station traverses q
            // the same way around the tool and a closing uTravel comes out positive.
            if (tangent[0] < 0)
            {
                tangent = [-tangent[0], -tangent[1]];
            }
        }
        else if (tangent[0] * previousTangent[0] + tangent[1] * previousTangent[1] < 0)
        {
            tangent = [-tangent[0], -tangent[1]];
        }
        const predicted = [uv[0] + stepSize * tangent[0], uv[1] + stepSize * tangent[1]];
        const correction = correctedUvAndGradient(stationMotion, strippedSurface, tGlobal, predicted,
            domain, uPeriodic, tolerance);
        const corrected = correction.uv;
        carriedGradient = correction.gradient;
        if (corrected[1] <= domain.vMin + margin || corrected[1] >= domain.vMax - margin)
        {
            hitVBoundary = true;
            break;
        }
        uvPoints[pointCount] = corrected;
        pointCount += 1;
        previousTangent = tangent;
        if (step + 1 >= minimumSteps)
        {
            const closure = closureImageShift(corrected, startUv, period, stepSize);
            if (closure.closed)
            {
                uvPoints[pointCount] = [startUv[0] + closure.uTravel, startUv[1]];
                pointCount += 1;
                closed = true;
                uTravel = closure.uTravel;
                break;
            }
        }
        uv = corrected;
    }
    return { "closed" : closed, "uvPoints" : subArray(uvPoints, 0, pointCount), "uTravel" : uTravel,
            "hitVBoundary" : hitVBoundary };
}

/**
 * Resample a marched WRAPPING loop at fixed fractions of its lifted arc length, re-Newton every
 * sample onto f = 0, and lift it. Same contract as resampleClosedLoopSection - fractions are
 * sampleIndex / sampleCount, so the last sample stops one step short of the start and the
 * periodic collocation system stays nonsingular - but every evaluation wraps u, so a sample may
 * legitimately carry an unwrapped u outside the face's domain.
 * Returns { uvSamples, liftedSamples, worstResidual }.
 */
export function resampleWrappingLoopSection(strippedMotion is map, strippedSurface is map, tGlobal is number,
    uvPoints is array, sampleCount is number, tolerance is number) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, tGlobal);

    const domain = fitSurfaceKnotDomain(strippedSurface);
    const uPeriodic = strippedSurface.isUPeriodic == true;
    const pointCount = size(uvPoints);
    var liftedPolyline = makeArray(pointCount);
    for (var index = 0; index < pointCount; index += 1)
    {
        liftedPolyline[index] = wrappedLiftContactPoint(stationMotion, strippedSurface, uvPoints[index],
            tGlobal, domain, uPeriodic);
    }
    var cumulativeLengths = makeArray(pointCount, 0);
    for (var index = 1; index < pointCount; index += 1)
    {
        cumulativeLengths[index] = cumulativeLengths[index - 1] +
            distanceBetweenTriples(liftedPolyline[index], liftedPolyline[index - 1]);
    }
    const totalLength = cumulativeLengths[pointCount - 1];

    var uvSamples = makeArray(sampleCount);
    var liftedSamples = makeArray(sampleCount);
    var worstResidual = 0;
    var cursor = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        const targetLength = totalLength * sampleIndex / sampleCount;
        while (cursor < pointCount - 2 && cumulativeLengths[cursor + 1] < targetLength)
        {
            cursor += 1;
        }
        const segmentLength = cumulativeLengths[cursor + 1] - cumulativeLengths[cursor];
        const fraction = segmentLength < 1e-300 ? 0 : (targetLength - cumulativeLengths[cursor]) / segmentLength;
        var uv = [uvPoints[cursor][0] + fraction * (uvPoints[cursor + 1][0] - uvPoints[cursor][0]),
            uvPoints[cursor][1] + fraction * (uvPoints[cursor + 1][1] - uvPoints[cursor][1])];
        // The residual read is the corrector's own converged value wherever it converged, so
        // the sample costs one evaluation less than it looks like it should.
        const correction = correctedUvAndGradient(stationMotion, strippedSurface, tGlobal, uv,
            domain, uPeriodic, tolerance);
        uv = correction.uv;
        worstResidual = max(worstResidual, abs(correction.gradient != undefined ? correction.gradient.value :
                        wrappedEnvelopeValue(stationMotion, strippedSurface, uv, tGlobal, domain, uPeriodic)));
        uvSamples[sampleIndex] = uv;
        liftedSamples[sampleIndex] = wrappedLiftContactPoint(stationMotion, strippedSurface, uv,
            tGlobal, domain, uPeriodic);
    }
    return { "uvSamples" : uvSamples, "liftedSamples" : liftedSamples, "worstResidual" : worstResidual };
}

/** Whether a marched point has returned to one of the seed's images u0 + k * period, wrap first
    so a genuine tube is never mistaken for a loop that merely passed near its own start. */
function closureImageShift(uv is array, startUv is array, period is number, stepSize is number) returns map
{
    for (var seamShift in (period == 0 ? [0] : [period, 0, -period]))
    {
        if ((uv[0] - startUv[0] - seamShift) ^ 2 + (uv[1] - startUv[1]) ^ 2 <= stepSize ^ 2)
        {
            return { "closed" : true, "uTravel" : seamShift };
        }
    }
    return { "closed" : false, "uTravel" : 0 };
}

/** Newton in v alone at a fixed meridian, held inside [vLow, vHigh]. Returns { converged, v }. */
function polishMeridianCrossing(strippedMotion is map, strippedSurface is map, t is number, uSeed is number,
    seedV is number, vLow is number, vHigh is number, domain is map, uPeriodic is boolean) returns map
{
    var v = clampToRange(seedV, vLow, vHigh);
    var converged = false;
    for (var iteration = 0; iteration < 12; iteration += 1)
    {
        const gradient = wrappedEnvelopeGradient(strippedMotion, strippedSurface, [uSeed, v], t, domain, uPeriodic);
        if (abs(gradient.value) <= 1e-14)
        {
            converged = true;
            break;
        }
        if (abs(gradient.vDerivative) < 1e-30)
        {
            break;
        }
        const stepped = v - gradient.value / gradient.vDerivative;
        if (stepped < vLow || stepped > vHigh)
        {
            break;
        }
        v = stepped;
    }
    return { "converged" : converged, "v" : v };
}

/**
 * The same corrector, handing back the gradient it finished on: { uv, gradient }.
 *
 * The gradient is the one evaluated AT the returned uv, so a caller that was about to evaluate
 * there anyway - the march, which needs the tangent at every corrected point, and the resample,
 * which reads the residual at every sample - gets it for nothing. It is `undefined` in the one
 * case where the loop ran out of iterations rather than converging, because then the last
 * gradient belongs to the point before the final step and not to the point being returned.
 *
 * This removes one order-2 surface evaluation per marched step, which on the tube path is
 * roughly a fifth of all of them - and it removes it by not repeating work, so the numbers do
 * not move at all.
 */
function correctedUvAndGradient(stationMotion is map, strippedSurface is map, tGlobal is number,
    seedUv is array, domain is map, uPeriodic is boolean, tolerance is number) returns map
{
    var uv = seedUv;
    for (var iteration = 0; iteration < 8; iteration += 1)
    {
        const gradient = wrappedEnvelopeGradient(stationMotion, strippedSurface, uv, tGlobal, domain, uPeriodic);
        if (abs(gradient.value) <= tolerance)
        {
            return { "uv" : uv, "gradient" : gradient };
        }
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            return { "uv" : uv, "gradient" : gradient };
        }
        uv = [uv[0] - gradient.value * gradient.uDerivative / gradientNormSquared,
            clampToRange(uv[1] - gradient.value * gradient.vDerivative / gradientNormSquared, domain.vMin, domain.vMax)];
    }
    return { "uv" : uv, "gradient" : undefined };
}

/** The envelope gradient at a uv whose u may sit outside a periodic domain. */
function wrappedEnvelopeGradient(strippedMotion is map, strippedSurface is map, uv is array, t is number,
    domain is map, uPeriodic is boolean) returns map
{
    return evaluateSectionGradientPointwise(strippedMotion, strippedSurface,
        wrapOrClampU(uv[0], domain, uPeriodic), clampToRange(uv[1], domain.vMin, domain.vMax), t);
}

/**
 * The envelope function ALONE at a uv whose u may sit outside a periodic domain. f reads no
 * second derivative of the surface, so a caller that wants only the value - a sign scan, a
 * bisection, a residual read - pays for the order-1 triangle instead of the order-2 one. The
 * number is the `value` field of `wrappedEnvelopeGradient` to the last bit: the lean evaluator
 * computes S, S_u and S_v the same way at either order.
 */
function wrappedEnvelopeValue(stationMotion is map, strippedSurface is map, uv is array, t is number,
    domain is map, uPeriodic is boolean) returns number
{
    return evaluateEnvelopePointwise(stationMotion, strippedSurface,
        wrapOrClampU(uv[0], domain, uPeriodic), clampToRange(uv[1], domain.vMin, domain.vMax), t);
}

/** The lifted envelope point at a uv whose u may sit outside a periodic domain. */
function wrappedLiftContactPoint(strippedMotion is map, strippedSurface is map, uv is array, t is number,
    domain is map, uPeriodic is boolean) returns Vector
{
    return liftContactPoint(strippedMotion, strippedSurface,
        wrapOrClampU(uv[0], domain, uPeriodic), clampToRange(uv[1], domain.vMin, domain.vMax), t);
}

/** u brought back into a periodic u domain; a non-periodic u is clamped to it instead. */
function wrapOrClampU(u is number, domain is map, uPeriodic is boolean) returns number
{
    if (!uPeriodic)
    {
        return clampToRange(u, domain.uMin, domain.uMax);
    }
    const period = domain.uMax - domain.uMin;
    const offset = u - domain.uMin;
    const remainder = offset - floor(offset / period) * period;
    return domain.uMin + (remainder >= period || remainder < 0 ? 0 : remainder);
}

/**
 * uv on a branch's sample polyline at the continuous sample parameter sigma (a fractional sample
 * index). The polyline IS the boundary as far as the fit is concerned - anchorUvAtStation
 * interpolates it and polishes ALONG it, never off it - so the extremum search runs on the same
 * polyline the anchors will, and the split time it reports is that polyline's own extremum rather
 * than the underlying curve's.
 */
function branchUvAtParameter(branch is map, sigma is number) returns array
{
    const lastIndex = size(branch.uvSamples) - 1;
    const clamped = clampToRange(sigma, 0, lastIndex);
    const low = min(floor(clamped), lastIndex - 1);
    const fraction = clamped - low;
    const uvLow = branch.uvSamples[low];
    const uvHigh = branch.uvSamples[low + 1];
    return [uvLow[0] + fraction * (uvHigh[0] - uvLow[0]), uvLow[1] + fraction * (uvHigh[1] - uvLow[1])];
}

/**
 * The branch's own t at sigma: the interpolated sample t is a seed, and 1D Newton on
 * f(uv(sigma), .) = 0 is the answer. f_t does not vanish at a boundary tangency - the tangency is
 * in uv, not in t - so this Newton stays well conditioned exactly where the extremum search needs
 * it most.
 *
 * Returns { failed, t, residual, uv }.
 */
function branchTimeAtParameter(strippedMotion is map, strippedSurface is map, branch is map,
    sigma is number, tStart is number, tEnd is number, tolerance is number) returns map
{
    const uv = branchUvAtParameter(branch, sigma);
    const lastIndex = size(branch.tSamples) - 1;
    const clamped = clampToRange(sigma, 0, lastIndex);
    const low = min(floor(clamped), lastIndex - 1);
    const fraction = clamped - low;
    const stepLimit = 0.25 * abs(tEnd - tStart);
    var t = branch.tSamples[low] + fraction * (branch.tSamples[low + 1] - branch.tSamples[low]);
    var residual = 1e300;
    for (var iteration = 0; iteration < 20; iteration += 1)
    {
        const gradient = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, uv[0], uv[1], t);
        residual = abs(gradient.value);
        if (residual <= tolerance)
        {
            break;
        }
        if (abs(gradient.tDerivative) < 1e-30)
        {
            return { "failed" : true, "t" : t, "residual" : residual, "uv" : uv };
        }
        t = t - clampToRange(gradient.value / gradient.tDerivative, -stepLimit, stepLimit);
    }
    return { "failed" : false, "t" : t, "residual" : residual, "uv" : uv };
}

/**
 * Refine one bracketed interior t extremum of a branch: golden section on sigma, and the Newton
 * solve at the converged sigma is the reported t. Searching sigma rather than d t / d sigma is
 * deliberate - t is quadratic in sigma near a smooth extremum, so a sigma good to 1e-9 pins t to
 * machine precision, and no second derivative of the polyline is needed (a piecewise-linear
 * polyline has none).
 *
 * The reported t sits at or below a maximum (at or above a minimum) of the polyline's own t,
 * which is the safe side: the band that ends there stays valid.
 *
 * Returns { failed, reason?, sigma, t, uv, residual, isMaximum }.
 */
function refineBranchTimeExtremum(strippedMotion is map, strippedSurface is map, branch is map,
    lowSigma is number, highSigma is number, isMaximum is boolean, tStart is number,
    tEnd is number, tolerance is number) returns map
{
    const golden = (sqrt(5) - 1) / 2;
    const stalled = { "failed" : true,
            "reason" : "the branch time solve stalled inside a t extremum bracket" };
    var low = lowSigma;
    var high = highSigma;
    var probeA = high - golden * (high - low);
    var probeB = low + golden * (high - low);
    var valueA = branchTimeAtParameter(strippedMotion, strippedSurface, branch, probeA, tStart, tEnd, tolerance);
    var valueB = branchTimeAtParameter(strippedMotion, strippedSurface, branch, probeB, tStart, tEnd, tolerance);
    if (valueA.failed || valueB.failed)
    {
        return stalled;
    }
    for (var iteration = 0; iteration < 40; iteration += 1)
    {
        if (high - low <= 1e-9)
        {
            break;
        }
        if (isMaximum ? valueA.t > valueB.t : valueA.t < valueB.t)
        {
            high = probeB;
            probeB = probeA;
            valueB = valueA;
            probeA = high - golden * (high - low);
            valueA = branchTimeAtParameter(strippedMotion, strippedSurface, branch, probeA, tStart, tEnd, tolerance);
            if (valueA.failed)
            {
                return stalled;
            }
        }
        else
        {
            low = probeA;
            probeA = probeB;
            valueA = valueB;
            probeB = low + golden * (high - low);
            valueB = branchTimeAtParameter(strippedMotion, strippedSurface, branch, probeB, tStart, tEnd, tolerance);
            if (valueB.failed)
            {
                return stalled;
            }
        }
    }
    const sigma = 0.5 * (low + high);
    const settled = branchTimeAtParameter(strippedMotion, strippedSurface, branch, sigma, tStart, tEnd, tolerance);
    if (settled.failed)
    {
        return stalled;
    }
    return { "failed" : false, "sigma" : sigma, "t" : settled.t, "uv" : settled.uv,
            "residual" : settled.residual, "isMaximum" : isMaximum };
}

/**
 * Cut one lateral branch into t-monotone sides at its refined interior t extrema (spec 7.1). The
 * cut sample is APPENDED to the side below and PREPENDED to the side above as the same value, so
 * the two sides share it exactly; every other sample is carried over from the input arrays
 * untouched.
 *
 * options: { tStart, tEnd (the component's t range, for the Newton step limit), sectionTolerance
 * (residual, default 1e-12), plateauTolerance (a sample-to-sample t difference at or below this
 * counts as flat and is not read as a turn, default 0) }.
 *
 * Returns { failed, reason?, sides {array}, extrema {array} }. Each side is a branch anchor
 * (anchorKind "branch", tSamples, uvSamples) carrying tMin, tMax, rising, and cutAtStart /
 * cutAtEnd - which of its ends is a shared cut rather than an original branch end.
 */
export function splitBranchAtTimeExtrema(strippedMotion is map, strippedSurface is map,
    branch is map, options is map) returns map
{
    const tolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const plateauTolerance = options.plateauTolerance == undefined ? 0 : options.plateauTolerance;
    const tSamples = branch.tSamples;
    const sampleCount = size(tSamples);
    if (sampleCount < 2 || size(branch.uvSamples) != sampleCount)
    {
        return { "failed" : true, "reason" : "a lateral branch needs at least two samples and " ~
                    "matching t and uv arrays" };
    }

    // Turns are read off the SIGN RUNS of the sample differences rather than off single
    // differences, and the reason is the symmetric case: a branch sampled symmetrically about its
    // own extremum has two samples at exactly the same t, so the difference between them is zero
    // and the two differences flanking any single sample never have opposite signs. Walking the
    // runs and remembering the last nonzero sign brackets that turn between the last rising
    // sample and the first falling one, plateau or no plateau. A 20-sample even layout on the
    // merge fixture is exactly this case.
    var extrema = [];
    var runStartIndex = undefined;
    var runSign = 0;
    for (var index = 0; index + 1 < sampleCount; index += 1)
    {
        const difference = tSamples[index + 1] - tSamples[index];
        if (abs(difference) <= plateauTolerance)
        {
            continue;
        }
        const sign = difference > 0 ? 1 : -1;
        if (runSign != 0 && sign != runSign)
        {
            const refined = refineBranchTimeExtremum(strippedMotion, strippedSurface, branch,
                runStartIndex, index + 1, runSign > 0, options.tStart, options.tEnd, tolerance);
            if (refined.failed)
            {
                return { "failed" : true, "reason" : refined.reason };
            }
            extrema = append(extrema, refined);
        }
        runStartIndex = index;
        runSign = sign;
    }

    const cutCount = size(extrema);
    var sides = makeArray(cutCount + 1);
    var lowerIndex = 0;
    for (var sideIndex = 0; sideIndex <= cutCount; sideIndex += 1)
    {
        var upperIndex = sampleCount;
        if (sideIndex < cutCount)
        {
            upperIndex = lowerIndex;
            while (upperIndex < sampleCount && upperIndex < extrema[sideIndex].sigma)
            {
                upperIndex += 1;
            }
        }
        var sideT = subArray(tSamples, lowerIndex, upperIndex);
        var sideUv = subArray(branch.uvSamples, lowerIndex, upperIndex);
        if (sideIndex > 0)
        {
            sideT = concatenateArrays([[extrema[sideIndex - 1].t], sideT]);
            sideUv = concatenateArrays([[extrema[sideIndex - 1].uv], sideUv]);
        }
        if (sideIndex < cutCount)
        {
            sideT = concatenateArrays([sideT, [extrema[sideIndex].t]]);
            sideUv = concatenateArrays([sideUv, [extrema[sideIndex].uv]]);
        }
        if (size(sideT) < 2)
        {
            return { "failed" : true, "reason" : "two t extrema fell inside one branch sample " ~
                        "interval - the branch is sampled too coarsely to split" };
        }
        // Monotonicity is "never both directions", not "never against the overall rise": a side
        // running from one t back to the same t has no overall rise to compare against, and
        // testing against zero would call it monotone whatever it does in between.
        var tMin = sideT[0];
        var tMax = sideT[0];
        var sawRise = false;
        var sawFall = false;
        for (var index = 1; index < size(sideT); index += 1)
        {
            tMin = min(tMin, sideT[index]);
            tMax = max(tMax, sideT[index]);
            const step = sideT[index] - sideT[index - 1];
            if (step > plateauTolerance)
            {
                sawRise = true;
            }
            else if (step < -plateauTolerance)
            {
                sawFall = true;
            }
        }
        if (sawRise && sawFall)
        {
            return { "failed" : true, "reason" : "a branch side still turns in t after splitting " ~
                        "at its bracketed t extrema (t from " ~ tMin ~ " to " ~ tMax ~ ")" };
        }
        sides[sideIndex] = {
                "anchorKind" : "branch", "tSamples" : sideT, "uvSamples" : sideUv,
                "tMin" : tMin, "tMax" : tMax, "rising" : sawRise,
                "cutAtStart" : sideIndex > 0, "cutAtEnd" : sideIndex < cutCount
            };
        lowerIndex = upperIndex;
    }
    return { "failed" : false, "sides" : sides, "extrema" : extrema };
}

/**
 * Spec 7.1's alternation count, read off the sides. Cap arcs live only at the two ends of the t
 * range and no two of them can be adjacent around the boundary loop (a lateral arc separates
 * them), so each cap arc contributes exactly two alternations and the count is just the number of
 * side endpoints landing on the two caps. A rectangle is 4 - one arc at each end; the shape this
 * decomposition exists for is anything above that.
 *
 * Returns { endpointsAtStart, endpointsAtEnd, capArcsAtStart, capArcsAtEnd, alternationCount,
 * exceedsRectangle }.
 */
export function componentBoundaryAlternations(sides is array, tStart is number, tEnd is number,
    capTolerance is number) returns map
{
    var atStart = 0;
    var atEnd = 0;
    for (var side in sides)
    {
        if (abs(side.tMin - tStart) <= capTolerance)
        {
            atStart += 1;
        }
        if (abs(side.tMax - tEnd) <= capTolerance)
        {
            atEnd += 1;
        }
    }
    return {
            "endpointsAtStart" : atStart, "endpointsAtEnd" : atEnd,
            "capArcsAtStart" : atStart / 2, "capArcsAtEnd" : atEnd / 2,
            "alternationCount" : atStart + atEnd, "exceedsRectangle" : atStart + atEnd > 4
        };
}

/**
 * March the section arc between two lateral sides at one station. Returns the marched polyline
 * plus the two tests a pairing decision rests on: whether the march arrived, and whether its
 * INTERIOR stayed off the domain boundary. A march aimed at a boundary point its own arc does not
 * reach can still arrive - by crawling along the boundary through the stretch where the section
 * leaves the domain - so arriving is not enough.
 *
 * Returns { failed, reason?, uvPoints, length, reachedEnd, crawled, startUv, endUv }.
 */
function marchSectionArcBetweenSides(strippedMotion is map, strippedSurface is map, t is number,
    startSide is map, endSide is map, domain is map, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    const tolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const divisions = options.marchDivisions == undefined ? 24 : options.marchDivisions;
    const startUv = anchorUvAtStation(startSide, stationMotion, strippedSurface, t, tolerance);
    const endUv = anchorUvAtStation(endSide, stationMotion, strippedSurface, t, tolerance);
    const distance = sqrt((endUv[0] - startUv[0]) ^ 2 + (endUv[1] - startUv[1]) ^ 2);
    if (distance < 1e-12)
    {
        return { "failed" : true, "reason" : "a strip's section collapses to a point at t = " ~ t };
    }
    const stepSize = distance / divisions;
    const march = marchSectionCurve(stationMotion, strippedSurface, t, startUv, endUv,
        { "stepSize" : stepSize, "maxSteps" : max(400, 20 * divisions), "tolerance" : tolerance });
    var length = 0;
    for (var index = 1; index < size(march.uvPoints); index += 1)
    {
        length += sqrt((march.uvPoints[index][0] - march.uvPoints[index - 1][0]) ^ 2 +
                (march.uvPoints[index][1] - march.uvPoints[index - 1][1]) ^ 2);
    }
    return {
            "failed" : false, "uvPoints" : march.uvPoints, "length" : length,
            "reachedEnd" : march.reachedEnd, "startUv" : startUv, "endUv" : endUv,
            "crawled" : sectionInteriorTouchesDomain(march.uvPoints, domain, 0.4 * stepSize)
        };
}

/**
 * True when a marched section's interior runs along the domain boundary. The three points at each
 * end are exempt: an arc's own endpoints ARE on the boundary, and the samples next to them are
 * within a step of it.
 */
function sectionInteriorTouchesDomain(uvPoints is array, domain is map, tolerance is number) returns boolean
{
    for (var index = 3; index + 3 < size(uvPoints); index += 1)
    {
        const uv = uvPoints[index];
        if (uv[0] - domain.uMin <= tolerance || domain.uMax - uv[0] <= tolerance ||
            uv[1] - domain.vMin <= tolerance || domain.vMax - uv[1] <= tolerance)
        {
            return true;
        }
    }
    return false;
}

/**
 * Pair one band's lateral sides into section arcs at the band's midpoint station, by marching
 * (see marchSectionArcBetweenSides). Among the candidates that arrive without crawling the
 * shortest march wins - a defensive tie-break, since a genuine arc is the shortest way between
 * its own two ends.
 *
 * Returns { failed, reason?, pairs {array of [i, j] index pairs into sideIndices} }.
 */
function pairBandSides(strippedMotion is map, strippedSurface is map, t is number, sides is array,
    sideIndices is array, domain is map, options is map) returns map
{
    const stationMotion = evaluateMotionSample(strippedMotion, t);

    const count = size(sideIndices);
    var paired = makeArray(count, false);
    var pairs = makeArray(count / 2);
    var pairCount = 0;
    for (var first = 0; first < count; first += 1)
    {
        if (paired[first])
        {
            continue;
        }
        var bestSecond = undefined;
        var bestLength = 1e300;
        for (var second = first + 1; second < count; second += 1)
        {
            if (paired[second])
            {
                continue;
            }
            const arc = marchSectionArcBetweenSides(stationMotion, strippedSurface, t,
                sides[sideIndices[first]], sides[sideIndices[second]], domain, options);
            if (arc.failed || !arc.reachedEnd || arc.crawled || arc.length >= bestLength)
            {
                continue;
            }
            bestLength = arc.length;
            bestSecond = second;
        }
        if (bestSecond == undefined)
        {
            return { "failed" : true, "reason" : "no section arc joins one of the lateral " ~
                        "boundary points at t = " ~ t ~ " to another - the component's boundary " ~
                        "curves are incomplete or the band is degenerate" };
        }
        paired[first] = true;
        paired[bestSecond] = true;
        pairs[pairCount] = [first, bestSecond];
        pairCount += 1;
    }
    return { "failed" : false, "pairs" : pairs };
}

/** The closest point on a uv polyline: { position (a continuous vertex index), squaredDistance }. */
function closestPositionOnPolyline(uvPoints is array, uv is array) returns map
{
    var bestPosition = 0;
    var bestSquaredDistance = 1e300;
    for (var index = 0; index + 1 < size(uvPoints); index += 1)
    {
        const start = uvPoints[index];
        const delta = [uvPoints[index + 1][0] - start[0], uvPoints[index + 1][1] - start[1]];
        const lengthSquared = delta[0] ^ 2 + delta[1] ^ 2;
        const fraction = lengthSquared < 1e-300 ? 0 :
            clampToRange(((uv[0] - start[0]) * delta[0] + (uv[1] - start[1]) * delta[1]) / lengthSquared, 0, 1);
        const offset = [start[0] + fraction * delta[0] - uv[0], start[1] + fraction * delta[1] - uv[1]];
        const squaredDistance = offset[0] ^ 2 + offset[1] ^ 2;
        if (squaredDistance < bestSquaredDistance)
        {
            bestSquaredDistance = squaredDistance;
            bestPosition = index + fraction;
        }
    }
    return { "position" : bestPosition, "squaredDistance" : bestSquaredDistance };
}

/**
 * The sub-polyline of a SHARED section polyline between two anchors: the interior vertices are
 * the shared polyline's own numbers and the two ends are the anchors themselves, so strips on
 * either side of a seam resample one marched arc instead of marching their own (spec 2.3). The
 * order is reversed when the anchors run against the polyline.
 *
 * Returns { failed, reason?, uvPoints, startPosition, endPosition }.
 */
export function clipSectionPolylineToAnchors(uvPoints is array, startUv is array, endUv is array) returns map
{
    const pointCount = size(uvPoints);
    if (pointCount < 2)
    {
        return { "failed" : true, "reason" : "a shared section polyline needs at least two points" };
    }
    const startPosition = closestPositionOnPolyline(uvPoints, startUv).position;
    const endPosition = closestPositionOnPolyline(uvPoints, endUv).position;
    if (abs(endPosition - startPosition) < 1e-9)
    {
        return { "failed" : true, "reason" : "both anchors land on one point of the shared " ~
                    "section polyline" };
    }
    const reversed = endPosition < startPosition;
    const low = reversed ? endPosition : startPosition;
    const high = reversed ? startPosition : endPosition;
    var firstInterior = 0;
    while (firstInterior < pointCount && firstInterior <= low + 1e-12)
    {
        firstInterior += 1;
    }
    var lastInterior = pointCount - 1;
    while (lastInterior >= 0 && lastInterior >= high - 1e-12)
    {
        lastInterior -= 1;
    }
    const interior = firstInterior <= lastInterior ? subArray(uvPoints, firstInterior, lastInterior + 1) : [];
    return {
            "failed" : false,
            "uvPoints" : concatenateArrays([[startUv], reversed ? reverse(interior) : interior, [endUv]]),
            "startPosition" : startPosition, "endPosition" : endPosition
        };
}

/**
 * Decompose one funnel component's boundary into rectangle-able strips (spec 7.1).
 *
 * input: {
 *     tStart, tEnd {number} : the component's t range,
 *     branches {array} : one branch anchor per lateral boundary curve of the component, in the
 *         shape anchorUvAtStation consumes ({ anchorKind : "branch", tSamples, uvSamples }) -
 *         marchStripZeroCurves' output carried to uv, which is the same array the adjacent sharp
 *         edge's lateral trim reads (spec 2.3),
 *     sectionTolerance {number, default 1e-12},
 *     capTolerance {number, default 1e-9} : how close a side's t extreme must be to the
 *         component's own t range end to count as landing on a cap,
 *     marchDivisions {number, default 24} : march steps per anchor distance, for the pairing and
 *         seam marches,
 *     gradeMergeEnds {boolean, default false} : also set gradeStart / gradeEnd on the strips whose
 *         t range ends at a merge. The merge is marked either way (mergeAtStart / mergeAtEnd);
 *         this only says whether to cluster stations there, which is measured NOT to help - see
 *         the section comment above
 * }
 *
 * Returns {
 *     failed, reason?,
 *     alternations {map} : componentBoundaryAlternations' record,
 *     sides {array} : every branch's t-monotone pieces, each carrying its branchIndex,
 *     splitTimes {array} : the interior cut times, ascending,
 *     bands {array} : { tStart, tEnd, sideIndices, pairs },
 *     strips {array} : each a map that merges straight into a fitEnvelopeComponent input -
 *         tStart, tEnd, startAnchor, endAnchor, startSectionPolyline / endSectionPolyline where a
 *         seam prescribes one, mergeAtStart / mergeAtEnd where that end is a merge (and
 *         gradeStart / gradeEnd to match, when gradeMergeEnds is on) - plus bandIndex and the two
 *         side indices,
 *     seams {array} : { t, coarseStripIndex, fineStripIndices, polylinePointCount }
 * }
 */
export function decomposeFunnelComponentIntoStrips(strippedMotion is map, strippedSurface is map,
    input is map) returns map
{
    const options = mergeMaps({ "sectionTolerance" : 1e-12, "capTolerance" : 1e-9,
                "marchDivisions" : 24, "gradeMergeEnds" : false }, input);
    const capTolerance = options.capTolerance;
    const domain = fitSurfaceKnotDomain(strippedSurface);

    // 1. Lateral branches to t-monotone sides, and their interior cut times.
    var sides = [];
    var splitTimes = [];
    for (var branchIndex = 0; branchIndex < size(input.branches); branchIndex += 1)
    {
        const split = splitBranchAtTimeExtrema(strippedMotion, strippedSurface,
            input.branches[branchIndex], options);
        if (split.failed)
        {
            return { "failed" : true, "reason" : split.reason };
        }
        for (var side in split.sides)
        {
            sides = append(sides, mergeMaps(side, { "branchIndex" : branchIndex }));
        }
        for (var extremum in split.extrema)
        {
            // A merge landing on a cap splits the branch but adds no band: there is no t range on
            // the far side of it.
            if (extremum.t > input.tStart + capTolerance && extremum.t < input.tEnd - capTolerance)
            {
                splitTimes = append(splitTimes, extremum.t);
            }
        }
    }
    const alternations = componentBoundaryAlternations(sides, input.tStart, input.tEnd, capTolerance);

    // 2. Bands. Cut times within capTolerance of one another are one event.
    const sortedTimes = sort(splitTimes, function(a, b)
        {
            return a - b;
        });
    var bandBounds = [input.tStart];
    for (var splitTime in sortedTimes)
    {
        if (abs(splitTime - bandBounds[size(bandBounds) - 1]) > capTolerance)
        {
            bandBounds = append(bandBounds, splitTime);
        }
    }
    bandBounds = append(bandBounds, input.tEnd);

    var bands = makeArray(size(bandBounds) - 1);
    var strips = [];
    for (var bandIndex = 0; bandIndex + 1 < size(bandBounds); bandIndex += 1)
    {
        const tLow = bandBounds[bandIndex];
        const tHigh = bandBounds[bandIndex + 1];
        var sideIndices = [];
        for (var sideIndex = 0; sideIndex < size(sides); sideIndex += 1)
        {
            if (sides[sideIndex].tMin <= tLow + capTolerance &&
                sides[sideIndex].tMax >= tHigh - capTolerance)
            {
                sideIndices = append(sideIndices, sideIndex);
            }
        }
        if (size(sideIndices) == 0 || size(sideIndices) % 2 != 0)
        {
            return { "failed" : true, "reason" : "the band t in [" ~ tLow ~ ", " ~ tHigh ~ "] is " ~
                        "bounded by " ~ size(sideIndices) ~ " lateral sides - a section's " ~
                        "endpoints come in pairs, so the component's boundary curves are " ~
                        "incomplete" };
        }
        const pairing = pairBandSides(strippedMotion, strippedSurface, 0.5 * (tLow + tHigh), sides,
            sideIndices, domain, options);
        if (pairing.failed)
        {
            return { "failed" : true, "reason" : pairing.reason };
        }
        bands[bandIndex] = { "tStart" : tLow, "tEnd" : tHigh, "sideIndices" : sideIndices,
                "pairs" : pairing.pairs };
        for (var pair in pairing.pairs)
        {
            strips = append(strips, {
                        "bandIndex" : bandIndex, "tStart" : tLow, "tEnd" : tHigh,
                        "startSideIndex" : sideIndices[pair[0]], "endSideIndex" : sideIndices[pair[1]],
                        "startAnchor" : sides[sideIndices[pair[0]]],
                        "endAnchor" : sides[sideIndices[pair[1]]],
                        "mergeAtStart" : false, "mergeAtEnd" : false,
                        "gradeStart" : false, "gradeEnd" : false
                    });
        }
    }

    // 3. Seams. At each interior cut time the merged side - whichever band carries fewer arcs -
    // marches the arc once, and the strips of the other band resample that same polyline.
    var seams = [];
    for (var boundIndex = 1; boundIndex + 1 < size(bandBounds); boundIndex += 1)
    {
        const seamTime = bandBounds[boundIndex];
        const lowerBand = boundIndex - 1;
        const upperBand = boundIndex;
        const coarseBand = size(bands[upperBand].pairs) < size(bands[lowerBand].pairs) ?
            upperBand : lowerBand;
        const fineBand = coarseBand == lowerBand ? upperBand : lowerBand;
        for (var coarseStripIndex = 0; coarseStripIndex < size(strips); coarseStripIndex += 1)
        {
            if (strips[coarseStripIndex].bandIndex != coarseBand)
            {
                continue;
            }
            const arc = marchSectionArcBetweenSides(strippedMotion, strippedSurface, seamTime,
                strips[coarseStripIndex].startAnchor, strips[coarseStripIndex].endAnchor, domain, options);
            if (arc.failed || !arc.reachedEnd)
            {
                return { "failed" : true, "reason" : arc.failed ? arc.reason :
                            ("the shared section march at the seam t = " ~ seamTime ~ " did not " ~
                            "reach its end anchor") };
            }
            var coarseStrip = strips[coarseStripIndex];
            if (coarseBand == lowerBand)
            {
                coarseStrip.endSectionPolyline = arc.uvPoints;
            }
            else
            {
                coarseStrip.startSectionPolyline = arc.uvPoints;
            }
            strips[coarseStripIndex] = coarseStrip;
            const onPolylineTolerance = 0.25 * arc.length / options.marchDivisions;
            var fineStripIndices = [];
            for (var fineStripIndex = 0; fineStripIndex < size(strips); fineStripIndex += 1)
            {
                if (strips[fineStripIndex].bandIndex != fineBand)
                {
                    continue;
                }
                var fineStrip = strips[fineStripIndex];
                const startHit = closestPositionOnPolyline(arc.uvPoints,
                    anchorUvAtStation(fineStrip.startAnchor, strippedMotion, strippedSurface,
                        seamTime, options.sectionTolerance));
                const endHit = closestPositionOnPolyline(arc.uvPoints,
                    anchorUvAtStation(fineStrip.endAnchor, strippedMotion, strippedSurface,
                        seamTime, options.sectionTolerance));
                if (max(startHit.squaredDistance, endHit.squaredDistance) > onPolylineTolerance ^ 2)
                {
                    continue;
                }
                if (fineBand == lowerBand)
                {
                    fineStrip.endSectionPolyline = arc.uvPoints;
                    fineStrip.mergeAtEnd = true;
                    fineStrip.gradeEnd = options.gradeMergeEnds;
                }
                else
                {
                    fineStrip.startSectionPolyline = arc.uvPoints;
                    fineStrip.mergeAtStart = true;
                    fineStrip.gradeStart = options.gradeMergeEnds;
                }
                strips[fineStripIndex] = fineStrip;
                fineStripIndices = append(fineStripIndices, fineStripIndex);
            }
            seams = append(seams, { "t" : seamTime, "coarseStripIndex" : coarseStripIndex,
                        "fineStripIndices" : fineStripIndices,
                        "polylinePointCount" : size(arc.uvPoints) });
        }
    }

    return { "failed" : false, "alternations" : alternations, "sides" : sides,
            "splitTimes" : subArray(bandBounds, 1, size(bandBounds) - 1), "bands" : bands,
            "strips" : strips, "seams" : seams };
}

/**
 * Fit every strip of a decomposed component (spec 7.1). Each strip record already carries the fit
 * input keys the decomposition owns - t range, anchors, the shared seam polylines, the grading
 * flags - so the caller supplies only what is common to all of them (tolerance, grid counts,
 * refinement budget).
 *
 * One caution on that shared tolerance: a strip with a merge end (`mergeAtStart` / `mergeAtEnd`)
 * has a square-root corner in its chart and its deviation does not fall cleanly with grid size, so
 * a target below what that chart reaches makes the refinement loop double to the budget cap
 * instead of converging (spec 7.10). Size those strips, or give them their own tolerance.
 *
 * Returns { failed, reason?, fits {array, index-aligned with the strips}, worstCertifiedBound,
 * budgetHit }.
 */
export function fitComponentStrips(strippedMotion is map, strippedSurface is map,
    decomposition is map, baseInput is map) returns map
{
    var fits = makeArray(size(decomposition.strips));
    var worstCertifiedBound = 0;
    var budgetHit = false;
    for (var stripIndex = 0; stripIndex < size(decomposition.strips); stripIndex += 1)
    {
        const fit = fitEnvelopeComponent(strippedMotion, strippedSurface,
            mergeMaps(baseInput, decomposition.strips[stripIndex]));
        if (fit.failed)
        {
            return { "failed" : true, "reason" : "strip " ~ stripIndex ~ ": " ~ fit.reason };
        }
        fits[stripIndex] = fit;
        worstCertifiedBound = max(worstCertifiedBound, fit.certifiedBound);
        budgetHit = budgetHit || fit.budgetHit;
    }
    return { "failed" : false, "fits" : fits, "worstCertifiedBound" : worstCertifiedBound,
            "budgetHit" : budgetHit };
}

/** Attach meters to a plain-number fit surface's control points (emission and knot cleanup). */
export function attachFitSurfaceUnits(surface is map) returns map
{
    var result = surface;
    var controlPoints = makeArray(size(surface.controlPoints));
    for (var rowIndex = 0; rowIndex < size(surface.controlPoints); rowIndex += 1)
    {
        var row = makeArray(size(surface.controlPoints[rowIndex]));
        for (var columnIndex = 0; columnIndex < size(surface.controlPoints[rowIndex]); columnIndex += 1)
        {
            row[columnIndex] = surface.controlPoints[rowIndex][columnIndex] * meter;
        }
        controlPoints[rowIndex] = row;
    }
    result.controlPoints = controlPoints;
    return result;
}

/** Strip meters back off a unit-bearing fit surface's control points. */
export function stripFitSurfaceUnits(surface is map) returns map
{
    var result = surface;
    var controlPoints = makeArray(size(surface.controlPoints));
    for (var rowIndex = 0; rowIndex < size(surface.controlPoints); rowIndex += 1)
    {
        var row = makeArray(size(surface.controlPoints[rowIndex]));
        for (var columnIndex = 0; columnIndex < size(surface.controlPoints[rowIndex]); columnIndex += 1)
        {
            row[columnIndex] = surface.controlPoints[rowIndex][columnIndex] / meter;
        }
        controlPoints[rowIndex] = row;
    }
    result.controlPoints = controlPoints;
    return result;
}

/**
 * The kernel-facing BSplineSurface of a unit-bearing, non-rational fit surface (the
 * editSurface.fs emission pattern): a periodic direction is converted to the CLOSED CLAMPED
 * form the kernel returns and provably accepts - handing it this module's wrap-padded form is
 * how you earn a PERIODIC_BSPLINESURFACE_NOT_SMOOTH - the periodic flag stays true through the
 * conversion, and the knot arrays get explicit KnotArray casts because typecheck types do not
 * propagate through array operations.
 */
export function kernelFitSurface(surface is map) returns BSplineSurface
{
    return kernelFitSurface(surface, true);
}

/**
 * Same, with control over whether a periodic direction is DECLARED periodic to the kernel.
 *
 * `opCreateBSplineSurface` answers CANNOT_MAKE_BSPLINESURFACE for a whole-island net - BOTH u-boundary rows collapsed to poles
 * and the v direction closed - whether v is declared periodic or converted to closed-clamped
 * and declared non-periodic. The flag is not the problem; such a net has no non-degenerate
 * boundary left to form a face from. Probe 4's accepted case collapsed ONE boundary row of an
 * open patch, which is a different shape. Emitting whole islands is therefore reopened as the
 * spec section 7.1 fallback (split at the t-extremes into two single-pole caps that share their
 * mid-t loop exactly, or SWEEP_ISLAND_UNSUPPORTED); the fit itself is unaffected and certifies.
 *
 * Declaring a periodic direction non-periodic costs nothing geometrically: clamping is knot
 * insertion to full multiplicity at the seam, which reproduces the same curve, so a patch stays
 * C2 across its seam and its two v-edges stay coincident for the knit to sew.
 */
export function kernelFitSurface(surface is map, declarePeriodic is boolean) returns BSplineSurface
{
    var emitted = surface;
    if (emitted.isUPeriodic == true || emitted.isVPeriodic == true)
    {
        // The conversion runs on homogeneous points, so it needs a weights grid even for a
        // non-rational net. Unit weights survive it exactly - knot insertion rows sum to one -
        // so the weights are dropped again below and the emitted surface stays non-rational.
        emitted.weights = unitWeightGrid(size(emitted.controlPoints), size(emitted.controlPoints[0]));
        if (emitted.isUPeriodic == true)
        {
            emitted = toClosedClampedSurfaceDirection(emitted, true);
        }
        if (emitted.isVPeriodic == true)
        {
            emitted = toClosedClampedSurfaceDirection(emitted, false);
        }
    }
    return bSplineSurface({
                "uDegree" : emitted.uDegree,
                "vDegree" : emitted.vDegree,
                "isUPeriodic" : declarePeriodic && emitted.isUPeriodic == true,
                "isVPeriodic" : declarePeriodic && emitted.isVPeriodic == true,
                "controlPoints" : controlPointMatrix(emitted.controlPoints),
                "uKnots" : emitted.uKnots is KnotArray ? emitted.uKnots : knotArray(emitted.uKnots),
                "vKnots" : emitted.vKnots is KnotArray ? emitted.vKnots : knotArray(emitted.vKnots)
            });
}

/** A rowCount x columnCount grid of weights, all one. */
function unitWeightGrid(rowCount is number, columnCount is number) returns array
{
    var weights = makeArray(rowCount);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        weights[rowIndex] = makeArray(columnCount, 1);
    }
    return weights;
}

/** The knot-domain rectangle of a stripped surface. */
export function fitSurfaceKnotDomain(surface is map) returns map
{
    return {
            "uMin" : surface.uKnots[surface.uDegree],
            "uMax" : surface.uKnots[size(surface.uKnots) - surface.uDegree - 1],
            "vMin" : surface.vKnots[surface.vDegree],
            "vMax" : surface.vKnots[size(surface.vKnots) - surface.vDegree - 1]
        };
}


// ============================= Sharp features and polyhedral emission (spec 8) =============================

/**
 * How small |g| may be at a shared co-edge sample, relative to the largest |g| that co-edge
 * carries at the same station, before the sample counts as a strip-function root in its own
 * right. A sign-change scan needs two strict signs to bracket a root, and every contact
 * breakpoint puts a root exactly ON a sample end - a breakpoint IS the time a face's contact
 * line reaches a vertex - so the relative floor is what reports those at all.
 */
const STRIP_ROOT_RELATIVE_FLOOR = 1e-11;

/**
 * How close to zero a strip function may be AT AN EDGE END, measured against its own change
 * across the last sample interval, before that end counts as the root itself.
 *
 * A segment boundary IS the time a crossing reaches a vertex, and the boundary is refined in t,
 * not in s: at the boundary station the end sample's |g| is the time residual times dg/dt, which
 * is a vanishing fraction of a sample spacing but need not be a vanishing fraction of the whole
 * edge's |g|. Judged against the global scale such a root is missed, the crossing is reported
 * nowhere, and the patch loses a directrix at its own last station. Judged against the local
 * slope it is what it is - a root one ten-millionth of a sample spacing away.
 */
const STRIP_ROOT_ENDPOINT_SLOPE_FLOOR = 1e-7;

/**
 * The per-sample zero floor for one strip-function scan: the global relative floor everywhere,
 * widened at the two END samples to the slope-relative one above.
 */
function stripRootFloors(values is array, valueScale is number) returns array
{
    const sampleCount = size(values);
    var floors = makeArray(sampleCount, STRIP_ROOT_RELATIVE_FLOOR * valueScale);
    if (sampleCount >= 2)
    {
        floors[0] = max(floors[0], STRIP_ROOT_ENDPOINT_SLOPE_FLOOR * abs(values[1] - values[0]));
        floors[sampleCount - 1] = max(floors[sampleCount - 1], STRIP_ROOT_ENDPOINT_SLOPE_FLOOR *
                abs(values[sampleCount - 1] - values[sampleCount - 2]));
    }
    return floors;
}

/** Bracket width at which a strip-function root in the edge's arc-length parameter is settled. */
const STRIP_ROOT_PARAMETER_TOLERANCE = 1e-13;

/**
 * How far outside [0, 1] a strip-function root may land and still be read as sitting ON the
 * edge's vertex, in the edge's own parameter.
 *
 * This is a statement about the PARTITION, not about the root solve, which is why it is not the
 * bracket tolerance above. A face's contact line reaches a vertex at that face's own breakpoint,
 * and a shared time partition asks every owner for a station at every OTHER owner's breakpoint -
 * so a face is routinely asked for its contact line a hair outside the interval where it has one,
 * and the honest answer is the vertex it just left. The margin has to cover the partition's
 * residue mapped into edge parameter: on a 60 mm edge whose contact line sweeps its length in
 * 0.035 of the sweep, this is sixty nanometres of edge, and the residue it absorbs is ten.
 *
 * Rejecting those roots instead reports the face as having NO contact line at that station, which
 * fails the patch outright rather than degrading it.
 */
const STRIP_ROOT_VERTEX_MARGIN = 1e-6;

/** Iteration cap for one strip-function root solve. */
const STRIP_ROOT_ITERATION_LIMIT = 60;

/**
 * How small the largest |g| along a whole co-edge may be, relative to the tool's local speed,
 * before the adjacent face is declared SLIDING rather than grazing along a curve: the contact
 * function is then identically zero across the face, so it carries no contact line to rule
 * between and spec 6.4's detector owns the case.
 */
const STRIP_SLIDING_RELATIVE_FLOOR = 1e-9;

/** Contact intervals shorter than this fraction of the sweep are reported and not emitted. */
const MINIMUM_CONTACT_INTERVAL_FRACTION = 1e-6;

/** Default t stations per contact interval for a ruled envelope patch's u direction. */
const DEFAULT_PATCH_STATION_COUNT = 9;

/** Default columns across a ruled envelope patch's v direction. Two is EXACT whenever the
    ruling is a straight segment, which it always is on a plane face and on a straight edge. */
const DEFAULT_PATCH_RULING_COLUMNS = 2;

/**
 * A ruling shorter than this collapses the patch to a point at that station, and is SNAPPED to
 * one rather than merely reported.
 *
 * Ten times the kernel's own `TOLERANCE.zeroLength`, and above it rather than below it, because
 * the band between the two is where an unusable edge lives: long enough that the kernel builds it,
 * short enough that it bounds nothing and no neighbour can sew to it. A shared time partition
 * puts rulings in that band routinely - every owner is cut at every OTHER owner's breakpoints, so
 * an owner whose contact line collapses at its own root is asked for that root's neighbour
 * instead, and the residue is the root gap times the rate the contact line grows. The tolerance
 * has to bound that residue, not the arithmetic that produced it.
 */
const PATCH_RULING_COLLAPSE_TOLERANCE = 1e-7;

/**
 * How many times a refused patch is refitted at half the stations before it is given up on.
 *
 * A ruled patch whose directrices move ALONG the ruling rather than across it is a sliver: on the
 * arc fixture one runs 75 mm along its ruling and 23 mm in time while the strip itself is 1.006 mm
 * wide. Interpolating such a patch overshoots - the control points leave the sliver - and the
 * kernel answers CANNOT_MAKE_BSPLINESURFACE, naming neither the net nor the reason. Measured on
 * that patch: a 0.33 mm overshoot across a 1.006 mm width, refused; the same points as a control
 * net, with no overshoot, accepted; and the bilinear form of the same four corners, accepted.
 *
 * The refit halves the stations, which halves nothing about the geometry but shrinks the
 * overshoot until the net lies inside the patch again; two stations is bilinear, whose control
 * points ARE the data, so the sequence always terminates. **The KERNEL decides when to stop, not
 * a tolerance.** An overshoot threshold looked like the cleaner test and is not: a healthy patch
 * on the same fixture overshoots by 4.6 - 4.9% of its transverse travel and a refused one by 33%,
 * so any constant between them is fitted to two numbers and quietly knocks sound patches down to
 * bilinear when the geometry changes.
 */
const PATCH_REFIT_ATTEMPTS = 6;

/**
 * The exact rigid transport of a B-spline curve into a tensor-product B-spline surface
 * (spec 2.1(c)). `Phi(s, t) = A(t) c(s) + b(t)` has control net `Q[i][j] = A_j P_i + b_j` on the
 * curve's own knot vector in u and the motion's in v: an affine map commutes with the convex
 * combinations de Boor takes, and the four motion splines share one knot vector, so the
 * transported surface is exact relative to the fitted motion with no sampling anywhere.
 *
 * A RATIONAL curve transports exactly too, with `W[i][j] = w_i`: the motion's basis is a
 * partition of unity, so the projective denominator is the curve's own and rides through
 * untouched.
 *
 * Input is a unit-stripped spline curve map { degree, knots, isRational, isPeriodic,
 * controlPoints, weights }. Output is the module's fit-surface shape { uDegree, vDegree,
 * isRational, isUPeriodic, isVPeriodic, controlPoints, uKnots, vKnots, weights }, u running
 * along the curve and v along t.
 */
export function transportCurve(strippedMotion is map, strippedCurve is map) returns map
{
    const curvePointCount = size(strippedCurve.controlPoints);
    const motionPointCount = size(strippedMotion.columnX.controlPoints);
    const isRational = strippedCurve.isRational == true && strippedCurve.weights != undefined;
    var net = makeArray(curvePointCount);
    var weightGrid = makeArray(curvePointCount);
    for (var curveIndex = 0; curveIndex < curvePointCount; curveIndex += 1)
    {
        const curvePoint = strippedCurve.controlPoints[curveIndex];
        var row = makeArray(motionPointCount, vector(0, 0, 0));
        for (var motionIndex = 0; motionIndex < motionPointCount; motionIndex += 1)
        {
            row[motionIndex] = strippedMotion.columnX.controlPoints[motionIndex] * curvePoint[0] +
                strippedMotion.columnY.controlPoints[motionIndex] * curvePoint[1] +
                strippedMotion.columnZ.controlPoints[motionIndex] * curvePoint[2] +
                strippedMotion.translation.controlPoints[motionIndex];
        }
        net[curveIndex] = row;
        weightGrid[curveIndex] = makeArray(motionPointCount,
            isRational ? strippedCurve.weights[curveIndex] : 1);
    }
    return {
            "uDegree" : strippedCurve.degree,
            "vDegree" : strippedMotion.columnX.degree,
            "isRational" : isRational,
            "isUPeriodic" : strippedCurve.isPeriodic == true,
            "isVPeriodic" : false,
            "controlPoints" : net,
            "uKnots" : strippedCurve.knots,
            "vKnots" : strippedMotion.columnX.knots,
            "weights" : isRational ? weightGrid : undefined
        };
}

/**
 * The EXACT trajectory edge of a sharp vertex over one contact interval (spec 8): the vertex's
 * trajectory curve `A(t) p + b(t)` - itself an exact B-spline on the motion's knot vector -
 * restricted to [tStart, tEnd] by clamped segment extraction, which is knot insertion to full
 * multiplicity at both ends and so reproduces the same curve exactly.
 *
 * Returns a unit-stripped spline curve map { degree, isPeriodic, isRational, controlPoints,
 * knots }.
 */
export function sharpVertexTrajectoryEdge(strippedMotion is map, vertexPoint is Vector,
    tStart is number, tEnd is number) returns map
{
    const degree = strippedMotion.columnX.degree;
    const pointCount = size(strippedMotion.columnX.controlPoints);
    var trajectoryControls = makeArray(pointCount, vector(0, 0, 0));
    for (var index = 0; index < pointCount; index += 1)
    {
        trajectoryControls[index] = strippedMotion.columnX.controlPoints[index] * vertexPoint[0] +
            strippedMotion.columnY.controlPoints[index] * vertexPoint[1] +
            strippedMotion.columnZ.controlPoints[index] * vertexPoint[2] +
            strippedMotion.translation.controlPoints[index];
    }
    const segmentOperator = clampedSegmentOperator(strippedMotion.columnX.knots, degree, tStart, tEnd);
    return {
            "degree" : degree,
            "isPeriodic" : false,
            "isRational" : false,
            "controlPoints" : applyKnotRefinementOperator(segmentOperator, trajectoryControls),
            "knots" : segmentOperator.knots
        };
}

/**
 * The co-edge's point and unit tangent at an arbitrary arc-length parameter, linearly
 * interpolated between the shared sample arrays extraction already built.
 *
 * Exact wherever the edge is a straight line - every co-edge of a polyhedral tool, which is
 * what this layer solves; on a curved edge it carries the sample spacing's linear interpolation
 * error, which a larger `samplesPerEdge` at extraction buys down.
 *
 * Returns { point, tangent, lowIndex, blend } - the last two so a caller reading a per-side
 * array does not repeat the bracket search.
 */
export function interpolateCoEdgePoint(coEdgeRecord is map, s is number) returns map
{
    const parameters = coEdgeRecord.sampleParameters;
    const points = coEdgeRecord.edgePoints;
    const tangents = coEdgeRecord.edgeTangents;
    const lastIndex = size(parameters) - 1;
    if (s <= parameters[0])
    {
        return { "point" : points[0], "tangent" : tangents[0], "lowIndex" : 0, "blend" : 0 };
    }
    if (s >= parameters[lastIndex])
    {
        return { "point" : points[lastIndex], "tangent" : tangents[lastIndex],
                "lowIndex" : lastIndex - 1, "blend" : 1 };
    }
    var lowIndex = 0;
    for (var index = 1; index <= lastIndex; index += 1)
    {
        if (parameters[index] > s)
        {
            lowIndex = index - 1;
            break;
        }
    }
    const span = parameters[lowIndex + 1] - parameters[lowIndex];
    const blend = span > 0 ? (s - parameters[lowIndex]) / span : 0;
    const tangent = (1 - blend) * tangents[lowIndex] + blend * tangents[lowIndex + 1];
    const tangentNorm = norm(tangent);
    return {
            "point" : (1 - blend) * points[lowIndex] + blend * points[lowIndex + 1],
            "tangent" : tangentNorm > 0 ? (1 / tangentNorm) * tangent : tangents[lowIndex],
            "lowIndex" : lowIndex,
            "blend" : blend
        };
}

/**
 * The co-edge's point, unit tangent and ONE-SIDED unit normal at an arbitrary arc-length
 * parameter, on the same interpolation as `interpolateCoEdgePoint` with the side's normal
 * blended and renormalized. `side` is "left" or "right".
 *
 * Returns { point, tangent, normal } - unit-stripped tool-frame Vectors.
 */
export function interpolateCoEdgeSample(coEdgeRecord is map, side is string, s is number) returns map
{
    const normals = coEdgeRecord.sideNormals[side];
    if (normals == undefined)
    {
        throw "solidSweepUtils sharp features: edge " ~ coEdgeRecord.edgeIndex ~ " has no " ~ side ~
            " side - a sheet boundary edge carries only one.";
    }
    const located = interpolateCoEdgePoint(coEdgeRecord, s);
    const normal = (1 - located.blend) * normals[located.lowIndex] +
        located.blend * normals[located.lowIndex + 1];
    const normalNorm = norm(normal);
    return {
            "point" : located.point,
            "tangent" : located.tangent,
            "normal" : normalNorm > 0 ? (1 / normalNorm) * normal : normals[located.lowIndex]
        };
}

/**
 * The strip function `g(s, t) = <A n, A' p + b'>` on one side of a co-edge at an arbitrary
 * arc-length parameter. `strippedMotion` may be a motion or a sample frozen at this same t.
 */
export function coEdgeStripValueAt(strippedMotion is map, coEdgeRecord is map, side is string,
    s is number, t is number) returns number
{
    const sample = interpolateCoEdgeSample(coEdgeRecord, side, s);
    return evaluateContactFunctionAtPoint(strippedMotion, sample.normal, sample.point, t);
}

/**
 * The zero set of one co-edge side's strip function in the EDGE parameter at a fixed station:
 * where the adjacent face's contact line crosses this edge at time t.
 *
 * The scan runs on the co-edge's own shared sample parameters (or a uniform refinement of them
 * when `scanSamples` asks for more), so every root reported is a root of exactly the array the
 * adjacent grazing patch and the sharp-edge sheet both read - which is what makes the two agree
 * at the seam rather than merely coincide numerically. Strict sign changes are solved by
 * false position; a sample whose |g| is under `STRIP_ROOT_RELATIVE_FLOOR` of the station's
 * largest |g| is reported as a root where it stands, which is how a crossing sitting exactly on
 * a vertex at a contact breakpoint is seen.
 *
 * Returns {
 *     roots {array} : [{ s, value }] in increasing s, deduplicated,
 *     valueScale {number} : the largest |g| the scan saw, the scale every relative test uses,
 *     sliding {boolean} : valueScale is under STRIP_SLIDING_RELATIVE_FLOOR times the local
 *         speed, so the face slides along this edge and carries no contact line here
 * }
 */
export function findCoEdgeStripRootsAtTime(strippedMotion is map, coEdgeRecord is map, side is string,
    t is number, scanSamples is number, descriptor is map) returns map
{
    // Order 1: every read below is the contact function's value, which carries no acceleration.
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    if (descriptor.isAffine)
    {
        return coEdgeStripRootsFromDescriptor(frozen, descriptor, side);
    }
    const sampleCount = max(scanSamples, size(coEdgeRecord.sampleParameters));
    var parameters = makeArray(sampleCount, 0);
    var values = makeArray(sampleCount, 0);
    var valueScale = 0;
    var speedScale = 0;
    for (var index = 0; index < sampleCount; index += 1)
    {
        const s = index / (sampleCount - 1);
        const sample = interpolateCoEdgeSample(coEdgeRecord, side, s);
        parameters[index] = s;
        values[index] = evaluateContactFunctionAtPoint(frozen, sample.normal, sample.point, t);
        valueScale = max(valueScale, abs(values[index]));
        speedScale = max(speedScale,
            norm(frozen.rotationDerivative * sample.point + frozen.translationDerivative));
    }
    if (valueScale <= STRIP_SLIDING_RELATIVE_FLOOR * max(speedScale, 1e-300))
    {
        return { "roots" : [], "valueScale" : valueScale, "sliding" : true };
    }

    const floors = stripRootFloors(values, valueScale);
    var roots = [];
    for (var index = 0; index < sampleCount; index += 1)
    {
        if (abs(values[index]) <= floors[index])
        {
            roots = append(roots, { "s" : parameters[index], "value" : values[index] });
            continue;
        }
        if (index == sampleCount - 1 || abs(values[index + 1]) <= floors[index + 1])
        {
            continue;
        }
        if (values[index] * values[index + 1] > 0)
        {
            continue;
        }
        roots = append(roots, solveStripRootInBracket(frozen, coEdgeRecord, side, t,
                parameters[index], parameters[index + 1], values[index], values[index + 1],
                STRIP_ROOT_RELATIVE_FLOOR * valueScale));
    }
    var deduplicated = [];
    for (var root in roots)
    {
        if (size(deduplicated) == 0 ||
            root.s - deduplicated[size(deduplicated) - 1].s > 10 * STRIP_ROOT_PARAMETER_TOLERANCE)
        {
            deduplicated = append(deduplicated, root);
        }
    }
    return { "roots" : deduplicated, "valueScale" : valueScale, "sliding" : false };
}

/**
 * Where one side's contact line crosses a co-edge, describing the edge on the spot. A caller
 * asking about one edge repeatedly should build the descriptor once and use the six-argument
 * overload.
 */
export function findCoEdgeStripRootsAtTime(strippedMotion is map, coEdgeRecord is map,
    side is string, t is number, scanSamples is number) returns map
{
    return findCoEdgeStripRootsAtTime(strippedMotion, coEdgeRecord, side, t, scanSamples,
        affineCoEdgeContactDescriptor(coEdgeRecord));
}

/**
 * One strip-function root inside a bracket the caller found, by Illinois false position.
 *
 * False position rather than plain bisection because `g` is AFFINE in s wherever the edge is a
 * straight line and its side face is planar - the polyhedral case - so the first secant lands
 * on the root and the solve costs one evaluation instead of forty. The Illinois halving of the
 * stale endpoint keeps the same iteration linearly convergent when `g` is genuinely curved, and
 * a secant that steps outside the bracket falls back to the midpoint.
 *
 * Returns { s, value }.
 */
function solveStripRootInBracket(frozenSample is map, coEdgeRecord is map, side is string, t is number,
    lowParameter is number, highParameter is number, lowStartValue is number, highStartValue is number,
    valueFloor is number) returns map
{
    var low = lowParameter;
    var high = highParameter;
    var lowValue = lowStartValue;
    var highValue = highStartValue;
    var middle = 0.5 * (low + high);
    var value = 0;
    for (var iteration = 0; iteration < STRIP_ROOT_ITERATION_LIMIT; iteration += 1)
    {
        const denominator = highValue - lowValue;
        middle = denominator == 0 ? 0.5 * (low + high) :
            low - lowValue * (high - low) / denominator;
        if (!(middle > low && middle < high))
        {
            middle = 0.5 * (low + high);
        }
        value = coEdgeStripValueAt(frozenSample, coEdgeRecord, side, middle, t);
        if (abs(value) <= valueFloor || high - low <= STRIP_ROOT_PARAMETER_TOLERANCE)
        {
            return { "s" : middle, "value" : value };
        }
        if (value * lowValue > 0)
        {
            low = middle;
            lowValue = value;
            highValue = 0.5 * highValue;
        }
        else
        {
            high = middle;
            highValue = value;
            lowValue = 0.5 * lowValue;
        }
    }
    return { "s" : middle, "value" : value };
}

/**
 * How far a sampled co-edge point may sit off the chord, and a side normal off the first
 * sample's, and still be called straight and planar. Relative to the edge's own length and to a
 * unit normal.
 */
const AFFINE_COEDGE_RELATIVE_TOLERANCE = 1e-9;

/**
 * The closed-form description of a co-edge whose strip function is exact in one division: a
 * STRAIGHT edge bounded by two PLANAR faces, so each side's normal is constant along it.
 *
 * Both conditions are read off the shared sample arrays rather than assumed. Where they hold,
 * `p(s) = origin + s * direction` and each side's normal is a constant `n`, so
 *
 *     g(s, t) = <A n, A' origin + b'> + s * <A n, A' direction>
 *
 * is AFFINE in the edge parameter. Its root is `-constant / slope`, its extreme values are its
 * two endpoint values, and the funnel between two such functions is an interval bounded by their
 * roots - none of which needs a sample.
 *
 * Returns { isAffine {boolean}, origin, direction, leftNormal, rightNormal } - Vectors,
 * unit-stripped, in the tool frame. `isAffine` false routes the caller to the sampled scan,
 * which is what a curved edge or a non-planar adjacent face takes.
 */
export function affineCoEdgeContactDescriptor(coEdgeRecord is map) returns map
{
    const refusal = { "isAffine" : false, "origin" : vector(0, 0, 0), "direction" : vector(0, 0, 0),
            "leftNormal" : vector(0, 0, 0), "rightNormal" : vector(0, 0, 0) };
    const leftNormals = coEdgeRecord.sideNormals.left;
    const rightNormals = coEdgeRecord.sideNormals.right;
    const points = coEdgeRecord.edgePoints;
    const parameters = coEdgeRecord.sampleParameters;
    if (leftNormals == undefined || rightNormals == undefined || points == undefined ||
        parameters == undefined || size(points) < 2)
    {
        return refusal;
    }

    const lastIndex = size(points) - 1;
    const origin = points[0];
    const direction = points[lastIndex] - origin;
    const edgeLength = norm(direction);
    if (edgeLength <= 0)
    {
        return refusal;
    }
    const pointTolerance = AFFINE_COEDGE_RELATIVE_TOLERANCE * edgeLength;
    const spanParameter = parameters[lastIndex] - parameters[0];
    if (spanParameter <= 0)
    {
        return refusal;
    }

    for (var index = 1; index < lastIndex; index += 1)
    {
        const fraction = (parameters[index] - parameters[0]) / spanParameter;
        if (norm(points[index] - (origin + fraction * direction)) > pointTolerance)
        {
            return refusal;
        }
    }
    for (var index = 1; index <= lastIndex; index += 1)
    {
        if (norm(leftNormals[index] - leftNormals[0]) > AFFINE_COEDGE_RELATIVE_TOLERANCE ||
            norm(rightNormals[index] - rightNormals[0]) > AFFINE_COEDGE_RELATIVE_TOLERANCE)
        {
            return refusal;
        }
    }
    return { "isAffine" : true, "origin" : origin, "direction" : direction,
            "leftNormal" : leftNormals[0], "rightNormal" : rightNormals[0] };
}

/**
 * The sharp edge's funnel at one station, solved in closed form from an affine descriptor.
 *
 * Both strip functions are affine in s, so each contributes at most one root and the two roots
 * partition [0, 1] into at most three sub-intervals. Membership is decided once per sub-interval
 * on the sign product at its midpoint, which is the same criterion the sampled scan applies at
 * every sample and reaches the same answer because an affine function changes sign only at its
 * root. A sub-interval count of three is what admits the two-span case, where the funnel occupies
 * both ends of the edge and not its middle.
 *
 * Returns the shape `sharpEdgeFunnelSpansAtTime` returns.
 */
export function sharpEdgeFunnelSpansFromDescriptor(frozenSample is map, descriptor is map) returns map
{
    const leftRotated = applyRowsToTriple(frozenSample.rotation, descriptor.leftNormal[0],
        descriptor.leftNormal[1], descriptor.leftNormal[2]);
    const rightRotated = applyRowsToTriple(frozenSample.rotation, descriptor.rightNormal[0],
        descriptor.rightNormal[1], descriptor.rightNormal[2]);
    const originVelocity = leanVelocity(frozenSample, descriptor.origin);
    const directionRate = applyRowsToTriple(frozenSample.rotationDerivative, descriptor.direction[0],
        descriptor.direction[1], descriptor.direction[2]);

    const leftConstant = dotTriples(leftRotated, originVelocity);
    const leftSlope = dotTriples(leftRotated, directionRate);
    const rightConstant = dotTriples(rightRotated, originVelocity);
    const rightSlope = dotTriples(rightRotated, directionRate);

    const leftScale = max(abs(leftConstant), abs(leftConstant + leftSlope));
    const rightScale = max(abs(rightConstant), abs(rightConstant + rightSlope));
    const endVelocity = addTriples(originVelocity, directionRate);
    const speedScale = max(sqrt(dotTriples(originVelocity, originVelocity)),
        sqrt(dotTriples(endVelocity, endVelocity)));
    const slidingFloor = STRIP_SLIDING_RELATIVE_FLOOR * max(speedScale, 1e-300);
    if (leftScale <= slidingFloor || rightScale <= slidingFloor)
    {
        return { "found" : false, "spans" : [], "sliding" : true,
                "leftScale" : leftScale, "rightScale" : rightScale };
    }

    const leftFloor = STRIP_ROOT_RELATIVE_FLOOR * leftScale;
    const rightFloor = STRIP_ROOT_RELATIVE_FLOOR * rightScale;

    // The breakpoints of the sign pattern: the ends, plus whichever side's root falls strictly
    // inside. A slope under its own floor is a constant function, which has no root to add.
    var breakParameters = [0];
    var breakSources = ["startVertex"];
    const leftRoot = abs(leftSlope) > leftFloor ? -leftConstant / leftSlope : undefined;
    const rightRoot = abs(rightSlope) > rightFloor ? -rightConstant / rightSlope : undefined;
    const leftInside = leftRoot != undefined && leftRoot > 0 && leftRoot < 1;
    const rightInside = rightRoot != undefined && rightRoot > 0 && rightRoot < 1;
    if (leftInside && rightInside)
    {
        breakParameters = append(breakParameters, min(leftRoot, rightRoot));
        breakSources = append(breakSources, leftRoot <= rightRoot ? "left" : "right");
        breakParameters = append(breakParameters, max(leftRoot, rightRoot));
        breakSources = append(breakSources, leftRoot <= rightRoot ? "right" : "left");
    }
    else if (leftInside)
    {
        breakParameters = append(breakParameters, leftRoot);
        breakSources = append(breakSources, "left");
    }
    else if (rightInside)
    {
        breakParameters = append(breakParameters, rightRoot);
        breakSources = append(breakSources, "right");
    }
    breakParameters = append(breakParameters, 1);
    breakSources = append(breakSources, "endVertex");

    var spans = [];
    var runStart = -1;
    for (var index = 0; index < size(breakParameters) - 1; index += 1)
    {
        const middle = 0.5 * (breakParameters[index] + breakParameters[index + 1]);
        const leftValue = leftConstant + middle * leftSlope;
        const rightValue = rightConstant + middle * rightSlope;
        const leftSign = abs(leftValue) <= leftFloor ? 0 : (leftValue > 0 ? 1 : -1);
        const rightSign = abs(rightValue) <= rightFloor ? 0 : (rightValue > 0 ? 1 : -1);
        const inside = leftSign * rightSign <= 0;
        if (inside && runStart < 0)
        {
            runStart = index;
        }
        if (!inside || index == size(breakParameters) - 2)
        {
            if (runStart >= 0)
            {
                const runEnd = inside ? index + 1 : index;
                spans = append(spans, {
                            "sLow" : breakParameters[runStart], "sHigh" : breakParameters[runEnd],
                            "lowSource" : breakSources[runStart], "highSource" : breakSources[runEnd]
                        });
                runStart = -1;
            }
        }
    }
    return { "found" : size(spans) > 0, "spans" : spans, "sliding" : false,
            "leftScale" : leftScale, "rightScale" : rightScale };
}

/**
 * Where ONE side's contact line crosses an affine co-edge, in closed form.
 *
 * The strip function is `g(s) = constant + s * slope`, so it has at most one root and that root
 * is a division. A slope under the value floor is a constant function, which crosses nowhere; a
 * root outside [0, 1] is a crossing the edge does not reach. This answers the same question as
 * the sampled scan in [findCoEdgeStripRootsAtTime] and reaches the same root without bracketing
 * for it.
 *
 * Returns { roots {array} : [{ s, value }], valueScale {number}, sliding {boolean} }.
 */
export function coEdgeStripRootsFromDescriptor(frozenSample is map, descriptor is map,
    side is string) returns map
{
    const normal = side == "left" ? descriptor.leftNormal : descriptor.rightNormal;
    const rotated = applyRowsToTriple(frozenSample.rotation, normal[0], normal[1], normal[2]);
    const originVelocity = leanVelocity(frozenSample, descriptor.origin);
    const directionRate = applyRowsToTriple(frozenSample.rotationDerivative, descriptor.direction[0],
        descriptor.direction[1], descriptor.direction[2]);
    const constantTerm = dotTriples(rotated, originVelocity);
    const slope = dotTriples(rotated, directionRate);

    const valueScale = max(abs(constantTerm), abs(constantTerm + slope));
    const endVelocity = addTriples(originVelocity, directionRate);
    const speedScale = max(sqrt(dotTriples(originVelocity, originVelocity)),
        sqrt(dotTriples(endVelocity, endVelocity)));
    if (valueScale <= STRIP_SLIDING_RELATIVE_FLOOR * max(speedScale, 1e-300))
    {
        return { "roots" : [], "valueScale" : valueScale, "sliding" : true };
    }

    const floor = STRIP_ROOT_RELATIVE_FLOOR * valueScale;
    if (abs(slope) <= floor)
    {
        return { "roots" : [], "valueScale" : valueScale, "sliding" : false };
    }
    const root = -constantTerm / slope;
    // A root a hair outside the edge is one that landed on a vertex, which the sampled form
    // reports as the endpoint sample sitting on zero.
    if (root < -STRIP_ROOT_VERTEX_MARGIN || root > 1 + STRIP_ROOT_VERTEX_MARGIN)
    {
        return { "roots" : [], "valueScale" : valueScale, "sliding" : false };
    }
    const clamped = min(1, max(0, root));
    return {
            "roots" : [{ "s" : clamped, "value" : constantTerm + clamped * slope }],
            "valueScale" : valueScale, "sliding" : false
        };
}

/**
 * The sharp edge's FUNNEL at one station (spec 8): the s intervals where the two adjacent
 * faces' strip functions differ in sign, `g_left * g_right <= 0` - the papers' 4.1 criterion,
 * two dot products on arrays that are already built.
 *
 * This overload takes a descriptor from [affineCoEdgeContactDescriptor]. An affine one is
 * answered in closed form; anything else falls through to the sampled scan below. Callers that
 * ask about the same edge at many times build the descriptor once and pass it here.
 *
 * The interval ends are exactly where one side's contact line crosses the edge, so they are the
 * SAME roots `findCoEdgeStripRootsAtTime` hands the adjacent grazing patch; an end that reaches
 * s = 0 or s = 1 is the edge's own vertex, the point at which a contact breakpoint changes the
 * sweep's combinatorial type.
 *
 * Returns {
 *     found {boolean}, spans {array} : [{ sLow, sHigh, lowSource, highSource }] with each
 *         source "left", "right", "startVertex" or "endVertex",
 *     sliding {boolean} : either side reports its face sliding along this edge,
 *     leftScale, rightScale {number} : the two |g| scales, for the console
 * }
 */
export function sharpEdgeFunnelSpansAtTime(strippedMotion is map, coEdgeRecord is map, t is number,
    scanSamples is number, descriptor is map) returns map
{
    if (coEdgeRecord.sideNormals.left == undefined || coEdgeRecord.sideNormals.right == undefined)
    {
        return { "found" : false, "spans" : [], "sliding" : false,
                "leftScale" : 0, "rightScale" : 0 };
    }
    // Order 1: every read below is the contact function's value, which carries no acceleration.
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    if (descriptor.isAffine)
    {
        return sharpEdgeFunnelSpansFromDescriptor(frozen, descriptor);
    }
    const sampleCount = max(scanSamples, size(coEdgeRecord.sampleParameters));
    var parameters = makeArray(sampleCount, 0);
    var leftValues = makeArray(sampleCount, 0);
    var rightValues = makeArray(sampleCount, 0);
    var leftScale = 0;
    var rightScale = 0;
    var speedScale = 0;
    for (var index = 0; index < sampleCount; index += 1)
    {
        const s = index / (sampleCount - 1);
        const leftSample = interpolateCoEdgeSample(coEdgeRecord, "left", s);
        const rightSample = interpolateCoEdgeSample(coEdgeRecord, "right", s);
        parameters[index] = s;
        leftValues[index] = evaluateContactFunctionAtPoint(frozen, leftSample.normal, leftSample.point, t);
        rightValues[index] = evaluateContactFunctionAtPoint(frozen, rightSample.normal, rightSample.point, t);
        leftScale = max(leftScale, abs(leftValues[index]));
        rightScale = max(rightScale, abs(rightValues[index]));
        speedScale = max(speedScale,
            norm(frozen.rotationDerivative * leftSample.point + frozen.translationDerivative));
    }
    const slidingFloor = STRIP_SLIDING_RELATIVE_FLOOR * max(speedScale, 1e-300);
    if (leftScale <= slidingFloor || rightScale <= slidingFloor)
    {
        return { "found" : false, "spans" : [], "sliding" : true,
                "leftScale" : leftScale, "rightScale" : rightScale };
    }

    // Membership is decided on SIGNS, never on the product: two genuinely small values of
    // opposite sign multiply to a number far under any absolute floor, and the criterion is
    // about their signs alone.
    const leftFloors = stripRootFloors(leftValues, leftScale);
    const rightFloors = stripRootFloors(rightValues, rightScale);
    var inside = makeArray(sampleCount, false);
    for (var index = 0; index < sampleCount; index += 1)
    {
        const leftSign = abs(leftValues[index]) <= leftFloors[index] ? 0 : (leftValues[index] > 0 ? 1 : -1);
        const rightSign = abs(rightValues[index]) <= rightFloors[index] ? 0 : (rightValues[index] > 0 ? 1 : -1);
        inside[index] = leftSign * rightSign <= 0;
    }

    var spans = [];
    var runStart = -1;
    for (var index = 0; index < sampleCount; index += 1)
    {
        if (inside[index] && runStart < 0)
        {
            runStart = index;
        }
        if (runStart < 0)
        {
            continue;
        }
        if (inside[index] && index < sampleCount - 1)
        {
            continue;
        }
        const runEnd = inside[index] ? index : index - 1;
        const low = funnelSpanEnd(frozen, coEdgeRecord, t, parameters, leftValues, rightValues,
            leftFloors, rightFloors, runStart, false);
        const high = funnelSpanEnd(frozen, coEdgeRecord, t, parameters, leftValues, rightValues,
            leftFloors, rightFloors, runEnd, true);
        spans = append(spans, {
                    "sLow" : low.s, "sHigh" : high.s,
                    "lowSource" : low.source, "highSource" : high.source
                });
        runStart = -1;
    }
    return { "found" : size(spans) > 0, "spans" : spans, "sliding" : false,
            "leftScale" : leftScale, "rightScale" : rightScale };
}

/**
 * The sharp edge's funnel at one station, describing the edge on the spot. A caller asking about
 * one edge at many times should build the descriptor once and use the five-argument overload.
 */
export function sharpEdgeFunnelSpansAtTime(strippedMotion is map, coEdgeRecord is map, t is number,
    scanSamples is number) returns map
{
    return sharpEdgeFunnelSpansAtTime(strippedMotion, coEdgeRecord, t, scanSamples,
        affineCoEdgeContactDescriptor(coEdgeRecord));
}

/**
 * One end of a funnel run, refined and named. A run reaching the sample array's own end sits at
 * the edge's VERTEX and needs no refinement; an interior end is bracketed against the neighbour
 * outside the run, and the side that changes sign across that bracket is the one whose contact
 * line crosses here, so the root is solved on that side alone.
 *
 * Returns { s, source } with source "left", "right", "startVertex" or "endVertex".
 */
function funnelSpanEnd(frozenSample is map, coEdgeRecord is map, t is number, parameters is array,
    leftValues is array, rightValues is array, leftFloors is array, rightFloors is array,
    runIndex is number, isHighEnd is boolean) returns map
{
    const outsideIndex = isHighEnd ? runIndex + 1 : runIndex - 1;
    if (outsideIndex < 0)
    {
        return { "s" : parameters[0], "source" : "startVertex" };
    }
    if (outsideIndex > size(parameters) - 1)
    {
        return { "s" : parameters[size(parameters) - 1], "source" : "endVertex" };
    }
    const leftCrosses = leftValues[runIndex] * leftValues[outsideIndex] < 0 &&
        abs(leftValues[runIndex]) > leftFloors[runIndex] &&
        abs(leftValues[outsideIndex]) > leftFloors[outsideIndex];
    const rightCrosses = rightValues[runIndex] * rightValues[outsideIndex] < 0 &&
        abs(rightValues[runIndex]) > rightFloors[runIndex] &&
        abs(rightValues[outsideIndex]) > rightFloors[outsideIndex];
    if (!leftCrosses && !rightCrosses)
    {
        // The run ends on a sample that already sits on a zero, so the sample IS the boundary.
        return { "s" : parameters[runIndex],
                "source" : abs(leftValues[runIndex]) <= leftFloors[runIndex] ? "left" : "right" };
    }
    const side = leftCrosses ? "left" : "right";
    const sideValues = leftCrosses ? leftValues : rightValues;
    const sideFloor = leftCrosses ? leftFloors[runIndex] : rightFloors[runIndex];
    const lowIndex = isHighEnd ? runIndex : outsideIndex;
    const highIndex = isHighEnd ? outsideIndex : runIndex;
    const root = solveStripRootInBracket(frozenSample, coEdgeRecord, side, t, parameters[lowIndex],
        parameters[highIndex], sideValues[lowIndex], sideValues[highIndex], sideFloor);
    return { "s" : root.s, "source" : side };
}

/**
 * The sweep's CONTACT BREAKPOINTS (spec 8, the papers' 6.2 sign-pattern combinatorics): every
 * time at which a contact line reaches a vertex of the tool, which is exactly a root of that
 * vertex's own contact function against one of its cone normals.
 *
 * Between two consecutive breakpoints the entire combinatorial type of the contact set is
 * constant - which face grazes, which edge is inside its funnel, and which edge each end of a
 * face's ruling rides - so every patch this layer emits is planned on one such interval and
 * needs no further splitting. On a polyhedral tool this is the complete list: the contact set
 * can change type only by passing through a vertex.
 *
 * Returns the sorted, deduplicated times, opening at `tStart` and closing at `tEnd`.
 */
export function polyhedralContactBreakpoints(strippedMotion is map, vertexRecords is array,
    tStart is number, tEnd is number, stationCount is number, tTolerance is number) returns array
{
    var times = [tStart, tEnd];
    for (var vertexRecord in vertexRecords)
    {
        for (var coneNormal in vertexRecord.coneNormals)
        {
            const roots = findContactFunctionRoots(strippedMotion, coneNormal, vertexRecord.point,
                tStart, tEnd, stationCount, tTolerance);
            for (var root in roots)
            {
                times = append(times, root.t);
            }
        }
    }
    times = sort(times, function(first, second)
        {
            return first - second;
        });
    var deduplicated = [times[0]];
    for (var index = 1; index < size(times); index += 1)
    {
        if (times[index] - deduplicated[size(deduplicated) - 1] > 10 * tTolerance)
        {
            deduplicated = append(deduplicated, times[index]);
        }
    }
    // The last breakpoint is the sweep's own end, never a root that landed a tolerance short of
    // it: otherwise the final interval is dropped and the end cap has nothing to close against.
    deduplicated[size(deduplicated) - 1] = tEnd;
    return deduplicated;
}

/** Every co-edge that bounds `faceIndex`, with the side that face sits on.
    Returns [{ edgeIndex, side }]. */
export function faceBoundingCoEdges(coEdgeRecords is array, faceIndex is number) returns array
{
    var bounding = [];
    for (var coEdgeRecord in coEdgeRecords)
    {
        if (coEdgeRecord.faceIndexLeft == faceIndex)
        {
            bounding = append(bounding, { "edgeIndex" : coEdgeRecord.edgeIndex, "side" : "left" });
        }
        else if (coEdgeRecord.faceIndexRight == faceIndex)
        {
            bounding = append(bounding, { "edgeIndex" : coEdgeRecord.edgeIndex, "side" : "right" });
        }
    }
    return bounding;
}

/**
 * Where a PLANE face's contact line crosses its own boundary at one station.
 *
 * On a plane the envelope function is affine in both surface parameters - the normal does not
 * turn and the point is affine - so the contact set is a straight LINE at every station and the
 * envelope patch it sweeps is exactly ruled between the two points where that line leaves the
 * face. Those two points are strip-function roots on the face's bounding co-edges, so the whole
 * answer costs one 1-D root solve per bounding edge and no surface evaluation at all.
 *
 * A convex face yields exactly two crossings or none, EXCEPT at a contact breakpoint, where the
 * crossing sits on a vertex and both edges meeting there report it. Callers that need the pair
 * match by their planned source edge rather than by the count.
 *
 * Returns {
 *     crossings {array} : [{ edgeIndex, side, s, worldPoint }] - worldPoint is
 *         `A(t) e(s) + b(t)`, unit-stripped meters,
 *     sliding {boolean} : some bounding co-edge reports the face sliding,
 *     valueScale {number} : the largest |g| seen around the boundary
 * }
 */
export function planarFaceCrossingsAtTime(strippedMotion is map, coEdgeRecords is array,
    boundingCoEdges is array, t is number, scanSamples is number) returns map
{
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    var crossings = [];
    var sliding = false;
    var valueScale = 0;
    for (var bounding in boundingCoEdges)
    {
        const coEdgeRecord = coEdgeRecords[bounding.edgeIndex];
        const found = findCoEdgeStripRootsAtTime(frozen, coEdgeRecord, bounding.side, t, scanSamples);
        valueScale = max(valueScale, found.valueScale);
        if (found.sliding)
        {
            sliding = true;
            continue;
        }
        for (var root in found.roots)
        {
            const located = interpolateCoEdgePoint(coEdgeRecord, root.s);
            crossings = append(crossings, {
                        "edgeIndex" : bounding.edgeIndex,
                        "side" : bounding.side,
                        "s" : root.s,
                        "worldPoint" : frozen.rotation * located.point + frozen.translation
                    });
        }
    }
    return { "crossings" : crossings, "sliding" : sliding, "valueScale" : valueScale };
}

/**
 * The world-space ends of a sharp edge's funnel span at one station: `A(t) e(s) + b(t)` at the
 * span's two edge parameters. Returns [Vector, Vector], unit-stripped meters.
 */
export function sharpEdgeSpanEndPoints(strippedMotion is map, coEdgeRecord is map, span is map,
    t is number) returns array
{
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    const lowPoint = interpolateCoEdgePoint(coEdgeRecord, span.sLow).point;
    const highPoint = interpolateCoEdgePoint(coEdgeRecord, span.sHigh).point;
    return [
            frozen.rotation * lowPoint + frozen.translation,
            frozen.rotation * highPoint + frozen.translation
        ];
}

/**
 * A reusable station grid: uniform t values across [tStart, tEnd] and the motion samples frozen
 * at them.
 *
 * Every contact function a polyhedral plan asks about - one per (point, normal) pair, and a cube
 * has 24 of them for its faces alone - is scanned over the SAME stations, and the motion enters
 * each of those scans only through `A(t)`, `A'(t)` and `b'(t)`. Freezing the grid once turns the
 * whole breakpoint pass from one motion evaluation per pair per station into one per station,
 * which is the same lever the section march takes (spec 11.2 lever A).
 *
 * Returns { parameters {array}, samples {array} } - a sample's `sampledAt` guards it against
 * being read at any other t.
 */
/** Knots nearer than this are one knot, so the span between them is not a span. */
const MOTION_SPAN_MINIMUM_WIDTH = 1e-12;

/**
 * The station parameters a contact-root scan needs over [tStart, tEnd], derived from the MOTION
 * rather than from a constant.
 *
 * The function being bracketed is `g(t) = <A(t) n, A'(t) p + b'(t)>`. A(t) is a spline of degree
 * `d`, so `A'` and `b'` are degree `d - 1` and g is piecewise polynomial of degree `2d - 1` -
 * degree 5 for the cubic motion this module fits. A polynomial of that degree has at most
 * `2d - 1` roots per knot span, so `2d` sub-intervals per span cannot miss a sign change, and
 * that is a BOUND rather than a heuristic.
 *
 * This also makes the grid density-aware for free. The motion's knot vector already records where
 * the motion is busy: spec section 4.1 densifies stations until orthogonality drift certifies, so
 * a fast-turning path arrives carrying more spans. A uniform grid discards that and is wrong in
 * both directions - wasteful on a simple motion, and capable of MISSING ROOTS on a dense one,
 * where a fixed count leaves under one sample per span.
 *
 * `minimumStations` is a floor for motions with very few spans; the count only ever grows from
 * the span bound.
 */
export function motionContactStationParameters(strippedMotion is map, tStart is number,
    tEnd is number, minimumStations is number) returns array
{
    const degree = strippedMotion.columnX.degree;
    const knots = strippedMotion.columnX.knots;
    // Span boundaries: the sweep's own ends plus every distinct interior knot.
    var boundaries = [tStart];
    for (var index = 0; index < size(knots); index += 1)
    {
        const knot = knots[index];
        if (knot > boundaries[size(boundaries) - 1] + MOTION_SPAN_MINIMUM_WIDTH && knot < tEnd)
        {
            boundaries = append(boundaries, knot);
        }
    }
    boundaries = append(boundaries, tEnd);

    const spanCount = size(boundaries) - 1;
    var subIntervalsPerSpan = 2 * degree;
    if (spanCount * subIntervalsPerSpan + 1 < minimumStations)
    {
        subIntervalsPerSpan = ceil((minimumStations - 1) / spanCount);
    }

    var parameters = makeArray(spanCount * subIntervalsPerSpan + 1, 0);
    var writeIndex = 0;
    for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
    {
        const spanStart = boundaries[spanIndex];
        const spanWidth = boundaries[spanIndex + 1] - spanStart;
        for (var step = 0; step < subIntervalsPerSpan; step += 1)
        {
            parameters[writeIndex] = spanStart + spanWidth * step / subIntervalsPerSpan;
            writeIndex += 1;
        }
    }
    parameters[writeIndex] = tEnd;
    return parameters;
}

/**
 * The motion sampled at parameters derived from its own knot vector - see
 * [motionContactStationParameters]. `minimumStations` floors the count.
 */
export function buildMotionStationGrid(strippedMotion is map, tStart is number, tEnd is number,
    minimumStations is number, densityAware is boolean) returns map
{
    const parameters = densityAware ?
        motionContactStationParameters(strippedMotion, tStart, tEnd, minimumStations) :
        uniformStationParameters(tStart, tEnd, minimumStations);
    var samples = makeArray(size(parameters));
    for (var index = 0; index < size(parameters); index += 1)
    {
        // Order 1. Every consumer of this grid reads the contact function's VALUE, which is
        // `<A n, A' p + b'>` - no acceleration term appears in it.
        samples[index] = evaluateMotionSample(strippedMotion, parameters[index], 1);
    }
    return { "parameters" : parameters, "samples" : samples };
}

/** `stationCount` parameters spread evenly across [tStart, tEnd]. */
function uniformStationParameters(tStart is number, tEnd is number, stationCount is number) returns array
{
    var parameters = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        parameters[index] = tStart + (tEnd - tStart) * index / (stationCount - 1);
    }
    return parameters;
}

export function buildMotionStationGrid(strippedMotion is map, tStart is number, tEnd is number,
    minimumStations is number) returns map
{
    // Uniform, pending the sliver merge of spec section 9.2. The density-aware grid above is
    // correct and finds contact transitions this one misses; on the rotating-cube fixture one of
    // those transitions cuts a segment the kernel then refuses as a transverse sliver, and a
    // refused patch costs more deviation than the missed transition does. Turn this to `true` in
    // the same change that lands segment merging.
    return buildMotionStationGrid(strippedMotion, tStart, tEnd, minimumStations, false);
}

/**
 * The roots of one contact function `<A n, A' p + b'>` over a PRE-FROZEN station grid: the scan
 * reads the grid, and only the bracket refinement - which needs values between stations - goes
 * back to the motion itself.
 *
 * Returns [{ t, value }] in increasing t, deduplicated.
 */
export function findContactFunctionRootsOnGrid(strippedMotion is map, stationGrid is map,
    normal is Vector, point is Vector, tTolerance is number) returns array
{
    const stationCount = size(stationGrid.parameters);
    var stationValues = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        stationValues[index] = evaluateContactFunctionAtPoint(stationGrid.samples[index], normal,
            point, stationGrid.parameters[index]);
    }
    var roots = [];
    for (var index = 0; index < stationCount; index += 1)
    {
        if (stationValues[index] == 0)
        {
            roots = append(roots, { "t" : stationGrid.parameters[index], "value" : 0 });
            continue;
        }
        if (index == stationCount - 1 || stationValues[index] * stationValues[index + 1] >= 0)
        {
            continue;
        }
        roots = append(roots, refineContactRoot(strippedMotion, normal, point,
                stationGrid.parameters[index], stationGrid.parameters[index + 1],
                stationValues[index], stationValues[index + 1], tTolerance));
    }
    var deduplicated = [];
    for (var root in roots)
    {
        if (size(deduplicated) == 0 || root.t - deduplicated[size(deduplicated) - 1].t > 10 * tTolerance)
        {
            deduplicated = append(deduplicated, root);
        }
    }
    return deduplicated;
}

/**
 * The contact breakpoints of one set of (point, normal) pairs: every time the contact function
 * `<A n, A' p + b'>` vanishes at one of them, with the sweep's own ends bracketing them.
 */
/**
 * How small a Bernstein coefficient counts as zero, relative to the largest coefficient of the
 * polynomial it belongs to. The threshold belongs to the polynomial that produced the value, not
 * to the caller, which is the same rule the census applies to its block screen.
 */
const CONTACT_COEFFICIENT_RELATIVE_TOLERANCE = 1e-12;

/** How narrowly root isolation brackets before Newton takes over, in a span's local parameter. */
const CONTACT_ROOT_ISOLATION_WIDTH = 1e-4;

/**
 * The contact function `g(t) = <A(t) n, A'(t) p + b'(t)>` on one motion span, as Bernstein
 * coefficients in that span's local [0, 1] parameter.
 *
 * No spline is evaluated. Expanding the inner product over the columns of A,
 *
 *     g = SUM_i SUM_j n_i p_j <C_i, C_j'>  +  SUM_i n_i <C_i, b'>
 *
 * and `buildMotionSpanPolynomials` has already built those twelve coefficient arrays per span and
 * elevated them to one shared degree, so the whole contact function of a constant normal at a
 * constant point is a WEIGHTED SUM of polynomials the motion computed once. Twelve numbers per
 * span, and the motion never appears again - the same reduction section 6.5 makes for the analytic
 * face classes, one dimension down.
 *
 * `includeTranslation` false drops the `b'` term, which is what turns this into the RULING
 * coefficient of a straight edge: with `p` replaced by the edge's direction, `<A n, A' d>` is the
 * slope of the affine strip function along that edge.
 */
export function contactFunctionSpanCoefficients(motionSpan is map, normal is Vector, point is Vector,
    includeTranslation is boolean) returns array
{
    const length = size(motionSpan.translationDots[0]);
    var coefficients = makeArray(length, 0);
    for (var i = 0; i < 3; i += 1)
    {
        const normalWeight = normal[i];
        if (normalWeight == 0)
        {
            continue;
        }
        if (includeTranslation)
        {
            const translationRow = motionSpan.translationDots[i];
            for (var index = 0; index < length; index += 1)
            {
                coefficients[index] = coefficients[index] + normalWeight * translationRow[index];
            }
        }
        for (var j = 0; j < 3; j += 1)
        {
            const weight = normalWeight * point[j];
            if (weight == 0)
            {
                continue;
            }
            const velocityRow = motionSpan.velocityDots[i][j];
            for (var index = 0; index < length; index += 1)
            {
                coefficients[index] = coefficients[index] + weight * velocityRow[index];
            }
        }
    }
    return coefficients;
}

/**
 * A root of a Bernstein polynomial inside an isolating interval, by Newton safeguarded with
 * bisection, entirely on the coefficients. `derivativeCoefficients` is the polynomial's own
 * derivative, differentiated once by the caller rather than per iteration.
 */
function polishBernsteinRoot(coefficients is array, derivativeCoefficients is array,
    bracketLow is number, bracketHigh is number, parameterTolerance is number) returns number
{
    var low = bracketLow;
    var high = bracketHigh;
    const lowValue = evaluateBernstein(coefficients, low);
    var parameter = 0.5 * (low + high);
    for (var iteration = 0; iteration < 24; iteration += 1)
    {
        const value = evaluateBernstein(coefficients, parameter);
        if (value == 0)
        {
            return parameter;
        }
        if (value * lowValue > 0)
        {
            low = parameter;
        }
        else
        {
            high = parameter;
        }
        const slope = evaluateBernstein(derivativeCoefficients, parameter);
        var next = slope == 0 ? undefined : parameter - value / slope;
        if (next == undefined || next <= low || next >= high)
        {
            next = 0.5 * (low + high);
        }
        if (abs(next - parameter) < parameterTolerance)
        {
            return next;
        }
        parameter = next;
    }
    return parameter;
}

/**
 * Every root of the contact function on [tStart, tEnd], in closed form.
 *
 * The function is piecewise polynomial in t, so each motion span is screened by the convex hull of
 * its own coefficients - a span whose coefficients share a sign is PROVEN root-free and costs one
 * pass over `2d` numbers - and only the survivors are subdivided into isolating intervals and
 * polished. Nothing here evaluates a spline, brackets against a station grid, or bisects on a
 * sampled sign, so no root can hide between stations.
 *
 * A span whose coefficients all vanish is the sliding case and is skipped; the degeneracy audit of
 * section 6.4 owns it.
 *
 * Returns [{ t, value }] sorted ascending, with roots closer than `tTolerance` merged.
 */
export function contactFunctionRootsExact(motionSpans is array, normal is Vector, point is Vector,
    tStart is number, tEnd is number, tTolerance is number) returns array
{
    var roots = [];
    for (var motionSpan in motionSpans)
    {
        if (motionSpan.tEnd <= tStart || motionSpan.tStart >= tEnd)
        {
            continue;
        }
        const spanWidth = motionSpan.tEnd - motionSpan.tStart;
        if (spanWidth <= 0)
        {
            continue;
        }
        const coefficients = contactFunctionSpanCoefficients(motionSpan, normal, point, true);
        const range = bernsteinRange(coefficients);
        const scale = max(abs(range.minimum), abs(range.maximum));
        if (scale <= 0)
        {
            continue;
        }
        const valueTolerance = CONTACT_COEFFICIENT_RELATIVE_TOLERANCE * scale;
        if (bernsteinExcludesZero(coefficients, valueTolerance))
        {
            continue;
        }
        const derivativeCoefficients = differentiateBernstein(coefficients);
        const localTolerance = max(tTolerance / spanWidth, 1e-15);
        for (var interval in isolateBernsteinRoots(coefficients, valueTolerance,
                CONTACT_ROOT_ISOLATION_WIDTH))
        {
            const local = polishBernsteinRoot(coefficients, derivativeCoefficients,
                interval.start, interval.end, localTolerance);
            const t = motionSpan.tStart + local * spanWidth;
            if (t < tStart - tTolerance || t > tEnd + tTolerance)
            {
                continue;
            }
            // CROSSINGS only. A breakpoint marks a change of contact type, and an
            // even-multiplicity root is a touch rather than a change - `g` returns to the side it
            // came from, so nothing about the sweep's combinatorics differs across it and cutting
            // there manufactures a patch of no extent. Coefficient root isolation finds these
            // where a sampled sign grid structurally cannot, so the test has to be made explicitly
            // rather than inherited from the sampling. Tangencies belong to the grazing and
            // degeneracy machinery of sections 6.4 and 7.4, which reads them from `f_t`.
            const probe = max(100 * localTolerance, 1e-7);
            const beforeValue = evaluateBernstein(coefficients, max(0, local - probe));
            const afterValue = evaluateBernstein(coefficients, min(1, local + probe));
            if (beforeValue * afterValue > 0)
            {
                continue;
            }
            roots = append(roots, {
                        "t" : min(tEnd, max(tStart, t)),
                        "value" : evaluateBernstein(coefficients, local)
                    });
        }
    }
    if (size(roots) == 0)
    {
        return roots;
    }
    roots = sort(roots, function(first, second)
        {
            return first.t - second.t;
        });
    var deduplicated = [roots[0]];
    for (var index = 1; index < size(roots); index += 1)
    {
        if (roots[index].t - deduplicated[size(deduplicated) - 1].t > 10 * tTolerance)
        {
            deduplicated = append(deduplicated, roots[index]);
        }
    }
    return deduplicated;
}

/**
 * The sweep-wide breakpoint times contributed by a set of contact pairs, solved on coefficients.
 * Returns the sorted, deduplicated times including both sweep ends.
 */
function contactBreakpointsExact(motionSpans is array, contactPairs is array, tStart is number,
    tEnd is number, tTolerance is number) returns array
{
    var times = [tStart, tEnd];
    for (var contactPair in contactPairs)
    {
        for (var root in contactFunctionRootsExact(motionSpans, contactPair.normal,
                contactPair.point, tStart, tEnd, tTolerance))
        {
            times = append(times, root.t);
        }
    }
    times = sort(times, function(first, second)
        {
            return first - second;
        });
    var deduplicated = [times[0]];
    for (var index = 1; index < size(times); index += 1)
    {
        if (times[index] - deduplicated[size(deduplicated) - 1] > 10 * tTolerance)
        {
            deduplicated = append(deduplicated, times[index]);
        }
    }
    deduplicated[size(deduplicated) - 1] = tEnd;
    return deduplicated;
}

function contactBreakpointsForPairs(strippedMotion is map, stationGrid is map, contactPairs is array,
    tTolerance is number) returns array
{
    const stationCount = size(stationGrid.parameters);
    const tStart = stationGrid.parameters[0];
    const tEnd = stationGrid.parameters[stationCount - 1];
    var times = [tStart, tEnd];
    for (var contactPair in contactPairs)
    {
        for (var root in findContactFunctionRootsOnGrid(strippedMotion, stationGrid,
                contactPair.normal, contactPair.point, tTolerance))
        {
            times = append(times, root.t);
        }
    }
    times = sort(times, function(first, second)
        {
            return first - second;
        });
    var deduplicated = [times[0]];
    for (var index = 1; index < size(times); index += 1)
    {
        if (times[index] - deduplicated[size(deduplicated) - 1] > 10 * tTolerance)
        {
            deduplicated = append(deduplicated, times[index]);
        }
    }
    // The last entry is the sweep's own end, never a root that landed a tolerance short of it:
    // otherwise the final segment is dropped and the end cap has nothing to close against.
    deduplicated[size(deduplicated) - 1] = tEnd;
    return deduplicated;
}

/** Two sorted time arrays merged into one, dropping entries within `tolerance` of a kept one. */
function mergeSortedTimes(existing is array, incoming is array, tolerance is number) returns array
{
    var merged = concatenateArrays([existing, incoming]);
    merged = sort(merged, function(first, second)
        {
            return first - second;
        });
    var deduplicated = [merged[0]];
    for (var index = 1; index < size(merged); index += 1)
    {
        if (merged[index] - deduplicated[size(deduplicated) - 1] > 10 * tolerance)
        {
            deduplicated = append(deduplicated, merged[index]);
        }
    }
    return deduplicated;
}

/**
 * The (point, normal) pairs at the CORNERS of one plane face: each bounding co-edge's two end
 * samples carrying that face's own one-sided normal.
 *
 * The roots of the contact function at these pairs are the times the face's contact line
 * reaches one of its corners, which are exactly the times the line's two boundary crossings
 * change which edge they ride - so they are this face's OWN breakpoints and nothing else's.
 */
/**
 * How near two contact pairs' points and normals must be to be the same pair. Points are
 * unit-stripped meters and normals are unit vectors, so one absolute floor serves both: a tool
 * whose distinct vertices sit this close has already lost to the kernel's own resolution.
 */
const CONTACT_PAIR_MATCH_TOLERANCE = 1e-10;

/**
 * `existing` with every pair of `incoming` that it does not already hold appended.
 *
 * A contact pair is a constant normal at a constant point, so it defines one scalar function of
 * time and its roots are the same whichever owner asked for them. Owners overlap heavily - a
 * face corner belongs to two of that face's bounding co-edges and to every sharp edge meeting
 * there - so pooling before the root pass is what stops the same function being solved several
 * times over.
 */
export function mergeContactPairs(existing is array, incoming is array) returns array
{
    var merged = existing;
    for (var candidate in incoming)
    {
        var isNew = true;
        for (var held in merged)
        {
            if (squaredNorm(held.point - candidate.point) <=
                CONTACT_PAIR_MATCH_TOLERANCE * CONTACT_PAIR_MATCH_TOLERANCE &&
                squaredNorm(held.normal - candidate.normal) <=
                CONTACT_PAIR_MATCH_TOLERANCE * CONTACT_PAIR_MATCH_TOLERANCE)
            {
                isNew = false;
                break;
            }
        }
        if (isNew)
        {
            merged = append(merged, candidate);
        }
    }
    return merged;
}

export function planarFaceCornerPairs(coEdgeRecords is array, boundingCoEdges is array) returns array
{
    var contactPairs = [];
    for (var bounding in boundingCoEdges)
    {
        const coEdgeRecord = coEdgeRecords[bounding.edgeIndex];
        const normals = coEdgeRecord.sideNormals[bounding.side];
        if (normals == undefined || coEdgeRecord.edgePoints == undefined)
        {
            continue;
        }
        const lastIndex = size(coEdgeRecord.edgePoints) - 1;
        contactPairs = append(contactPairs,
            { "point" : coEdgeRecord.edgePoints[0], "normal" : normals[0] });
        contactPairs = append(contactPairs,
            { "point" : coEdgeRecord.edgePoints[lastIndex], "normal" : normals[lastIndex] });
    }
    return contactPairs;
}

/**
 * The (point, normal) pairs at the two ENDS of one sharp edge, on both of its sides.
 *
 * Both of this edge's strip functions are solved in the edge parameter, so the funnel span's
 * ends move continuously until a root reaches s = 0 or s = 1 - which is a root of the contact
 * function at that endpoint against that side's normal, and is the only way the span's source
 * can change or the funnel open and close. Four pairs, and they are this edge's own breakpoints.
 */
export function sharpEdgeEndPairs(coEdgeRecord is map) returns array
{
    var contactPairs = [];
    const lastIndex = size(coEdgeRecord.edgePoints) - 1;
    for (var side in ["left", "right"])
    {
        const normals = coEdgeRecord.sideNormals[side];
        if (normals == undefined)
        {
            continue;
        }
        contactPairs = append(contactPairs,
            { "point" : coEdgeRecord.edgePoints[0], "normal" : normals[0] });
        contactPairs = append(contactPairs,
            { "point" : coEdgeRecord.edgePoints[lastIndex], "normal" : normals[lastIndex] });
    }
    return contactPairs;
}

/**
 * The times a sharp edge's funnel OPENS or CLOSES, found by watching the funnel itself across the
 * shared station grid and bisecting every transition.
 *
 * `sharpEdgeEndPairs` catches the times the funnel's boundary reaches one of the edge's own ends,
 * and those are not all of them. The funnel is the set where the two adjacent faces' strip
 * functions differ in sign, so it also closes when its two boundary roots MEET IN THE EDGE'S
 * INTERIOR - the moment the edge grazes at a single interior point rather than along a span. No
 * endpoint is involved, so no endpoint root marks it, and a segment planned across one has no
 * funnel at its own middle stations, which is what SWEEP_EDGE_FUNNEL_UNSTABLE reports.
 *
 * The scan reads the frozen station grid, so the whole pass costs no motion evaluations of its own.
 */
function sharpEdgeFunnelTransitions(strippedMotion is map, stationGrid is map, coEdgeRecord is map,
    tTolerance is number, scanSamples is number) returns array
{
    const stationCount = size(stationGrid.parameters);
    // Described once for the whole pass. On a polyhedral tool this makes every funnel question
    // below four dot products and two divisions instead of a scan across the edge's samples.
    const descriptor = affineCoEdgeContactDescriptor(coEdgeRecord);
    var occupied = makeArray(stationCount, false);
    for (var index = 0; index < stationCount; index += 1)
    {
        occupied[index] = sharpEdgeFunnelSpansAtTime(stationGrid.samples[index], coEdgeRecord,
            stationGrid.parameters[index], scanSamples, descriptor).found;
    }
    var transitions = [];
    for (var index = 1; index < stationCount; index += 1)
    {
        if (occupied[index] == occupied[index - 1])
        {
            continue;
        }
        // Bisect on OCCUPANCY. The span width is not a continuous function to solve on - it does
        // not exist on the empty side - so the bracket is closed on the boolean itself.
        var low = stationGrid.parameters[index - 1];
        var high = stationGrid.parameters[index];
        const lowOccupied = occupied[index - 1];
        while (high - low > tTolerance)
        {
            const middle = 0.5 * (low + high);
            if (sharpEdgeFunnelSpansAtTime(strippedMotion, coEdgeRecord, middle, scanSamples,
                    descriptor).found == lowOccupied)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        transitions = append(transitions, 0.5 * (low + high));
    }
    return transitions;
}

/**
 * Plan the whole envelope of a POLYHEDRAL tool: which patches exist, over which time segment,
 * and which boundary curve each of their two directrices rides.
 *
 * The plan is pure math - no context, no operations - so a caller can print it, check it, and
 * decide what to emit. Every entry is a RULED patch, because on a polyhedron every envelope
 * face is one: a plane face's contact set is a straight segment at each station, and a straight
 * sharp edge's funnel span is a straight sub-segment of that edge, so both sweep exactly-ruled
 * surfaces between two directrices this layer solves for pointwise. Neither ever evaluates a
 * surface: the whole answer is the scalar contact function at points of the tool's own edges.
 *
 * **Each face and each edge is split at its OWN breakpoints, not at the sweep's.** A face's
 * combinatorial type changes only when ITS contact line reaches ONE OF ITS corners, and an
 * edge's only when its funnel span reaches one of its ends; splitting every owner at every
 * other owner's breakpoints would cut the same patch into as many pieces as the tool has
 * corners and hand the knit hundreds of sheets with nothing to show for it. The sweep-wide
 * timeline is returned as well, and it costs nothing extra: every face-corner pair is a
 * (vertex, incident face normal) pair, so merging the per-face lists IS that timeline.
 *
 * options: {
 *     tStart, tEnd {number} : the sweep interval, default 0 and 1,
 *     breakpointStations {number} : stations for the contact root scan, default 97,
 *     tTolerance {number} : breakpoint refinement tolerance, default 1e-12,
 *     scanSamples {number} : samples for each edge-parameter root scan, default the co-edge's own
 * }
 *
 * Returns {
 *     breakpoints {array} : the sweep-wide timeline, merged from every owner's own,
 *     patches {array} : [{ patchKind ("face" or "edge"), ownerIndex, segmentIndex, tStart,
 *         tEnd, directrixSources }] - two { edgeIndex, side } for a face patch, two
 *         { source } names for an edge sheet,
 *     segmentCounts {map} : { face, edge } - how many own-breakpoint segments were examined,
 *     skipped {array} : [{ patchKind, ownerIndex, segmentIndex, reason }] - every face or edge
 *         looked at on a segment and not planned, with why,
 *     slidingFaces {array} : face indices whose contact function is identically zero somewhere
 * }
 */
export function planPolyhedralEnvelope(strippedMotion is map, faceRecords is array,
    coEdgeRecords is array, options is map) returns map
{
    const settings = mergeMaps({
                "tStart" : 0, "tEnd" : 1,
                "breakpointStations" : 97,
                "tTolerance" : 1e-12,
                "scanSamples" : 0,
                "exactBreakpoints" : false
            }, options);
    const sweepSpan = settings.tEnd - settings.tStart;
    // The motion's own per-span Bernstein polynomials, built once and shared by every contact
    // question this pass asks. The station grid below still serves the funnel occupancy scan.
    const motionSpans = settings.exactBreakpoints == true ?
        buildMotionSpanPolynomials(strippedMotion) : [];
    const stationGrid = buildMotionStationGrid(strippedMotion, settings.tStart, settings.tEnd,
        settings.breakpointStations);

    var breakpoints = [settings.tStart, settings.tEnd];
    var patches = [];
    var skipped = [];
    var slidingFaces = [];
    var faceSegments = 0;
    var edgeSegments = 0;

    // ---- Pass one: every owner's own breakpoints, merged into ONE partition of the sweep ----
    //
    // Patches are cut on the shared partition, not on their own breakpoints, and that is what
    // makes the shell sewable. Two patches meet along a whole boundary curve; cut on different
    // partitions they meet along PARTS of each other's boundaries instead, so neither edge has a
    // partner and the knit has nothing to sew even where the surfaces coincide. Measured on the
    // line fixture with per-owner partitions: 40 of 108 free edges had no partner at all.
    //
    // The cost is more patches - every owner is cut wherever ANY owner changes - and each extra
    // cut is a sub-interval of one the owner already had, so no patch spans a change in its own
    // contact type. Sub-intervals where an owner has no contact are dropped by the same midpoint
    // tests that always decided that.
    // Every owner's contact pairs are POOLED and deduplicated before a single root pass runs over
    // them. A (normal, point) pair is a scalar function of t alone, and the same one belongs to
    // every owner incident to it: a square face generates each of its corners twice, once per
    // bounding co-edge, and every sharp edge's end pair repeats one of its own faces' corners.
    // A cube offers 96 pairs and holds 24 distinct ones. Pooling is exact here because the
    // partition is sweep-wide - each owner's roots are merged into ONE timeline above, so the
    // owner a root arrived through never mattered.
    var contactPairs = [];
    var funnelTransitions = [];
    var eligibleFaces = [];
    for (var faceIndex = 0; faceIndex < size(faceRecords); faceIndex += 1)
    {
        if (faceRecords[faceIndex].surfaceClass != SweepSurfaceClass.PLANE)
        {
            skipped = append(skipped, { "patchKind" : "face", "ownerIndex" : faceIndex,
                        "segmentIndex" : -1,
                        "reason" : "SWEEP_FACE_NOT_PLANAR: class " ~ faceRecords[faceIndex].surfaceClass ~
                        " needs the grazing-fit route, not the ruled one." });
            continue;
        }
        const boundingCoEdges = faceBoundingCoEdges(coEdgeRecords, faceIndex);
        contactPairs = mergeContactPairs(contactPairs,
            planarFaceCornerPairs(coEdgeRecords, boundingCoEdges));
        eligibleFaces = append(eligibleFaces, faceIndex);
    }

    var eligibleEdges = [];
    for (var edgeIndex = 0; edgeIndex < size(coEdgeRecords); edgeIndex += 1)
    {
        const coEdgeRecord = coEdgeRecords[edgeIndex];
        if (coEdgeRecord.sideNormals.left == undefined || coEdgeRecord.sideNormals.right == undefined)
        {
            skipped = append(skipped, { "patchKind" : "edge", "ownerIndex" : edgeIndex,
                        "segmentIndex" : -1,
                        "reason" : "SWEEP_EDGE_ONE_SIDED: a sheet boundary edge has no funnel." });
            continue;
        }
        if (coEdgeRecord.convexity != EdgeConvexityType.CONVEX)
        {
            skipped = append(skipped, { "patchKind" : "edge", "ownerIndex" : edgeIndex,
                        "segmentIndex" : -1,
                        "reason" : "SWEEP_EDGE_NOT_CONVEX: convexity " ~ coEdgeRecord.convexity ~
                        " is outside v1's scope." });
            continue;
        }
        contactPairs = mergeContactPairs(contactPairs, sharpEdgeEndPairs(coEdgeRecord));
        funnelTransitions = concatenateArrays([funnelTransitions,
                    sharpEdgeFunnelTransitions(strippedMotion, stationGrid, coEdgeRecord,
                        settings.tTolerance, settings.scanSamples)]);
        eligibleEdges = append(eligibleEdges, edgeIndex);
    }

    // `exactBreakpoints` solves the contact roots on coefficients instead of bracketing them on
    // the station grid: `g` is piecewise polynomial in t, so every pair's roots come out of the
    // motion's own span polynomials behind a convex-hull screen, and no root can sit between two
    // samples. It is measured correct and measured to find crossings the grid misses - which is
    // why it is not yet the default. Those extra crossings cut segments whose patches the kernel
    // refuses as transverse slivers, and a refused patch leaves a larger hole than the missed
    // crossing does. Turn this on in the same change that lands the segment merge of section 9.2.
    const pairRoots = settings.exactBreakpoints == true ?
        contactBreakpointsExact(motionSpans, contactPairs, settings.tStart, settings.tEnd,
            settings.tTolerance) :
        contactBreakpointsForPairs(strippedMotion, stationGrid, contactPairs, settings.tTolerance);

    // ONE time per event. A funnel opens or closes exactly when a contact root crosses an edge
    // end, so the occupancy bisection above and the root refinement find the SAME transitions -
    // but the bisection detects a span only once it is wide enough to catch a scan sample, so
    // its answer lands late by a few nanoseconds of t. Kept as two partition entries they cut a
    // sub-resolution sliver segment: every owner's patches then stop and restart across a gap
    // smaller than the kernel's own resolution, which one seam sews across and three sheets
    // around the same ring stack into BOOLEAN_INVALID. The refined root is the true event, so a
    // transition within the emission's own minimum-interval floor of a root is that root said
    // worse and is dropped in its favour; a transition with no root nearby - a funnel closing in
    // the edge's interior - stands on its own. Merging at the same floor is provably harmless:
    // any segment narrower than it is dropped unplanned below.
    const duplicateEventWindow = MINIMUM_CONTACT_INTERVAL_FRACTION * sweepSpan;
    var interiorTransitions = [];
    for (var transition in funnelTransitions)
    {
        var matchesRoot = false;
        for (var root in pairRoots)
        {
            if (abs(transition - root) < duplicateEventWindow)
            {
                matchesRoot = true;
                break;
            }
        }
        if (!matchesRoot)
        {
            interiorTransitions = append(interiorTransitions, transition);
        }
    }
    breakpoints = mergeSortedTimes(breakpoints, pairRoots, 0.1 * duplicateEventWindow);
    breakpoints = mergeSortedTimes(breakpoints, interiorTransitions, 0.1 * duplicateEventWindow);
    // The partition's ends are the sweep's own, never a root that landed a window short of them:
    // a cluster keeps its smallest member, and losing tEnd would leave the last patches ending
    // before the cap they must close against.
    breakpoints[0] = settings.tStart;
    breakpoints[size(breakpoints) - 1] = settings.tEnd;

    // ---- Pass two: cut every owner on the shared partition ----
    const partition = breakpoints;
    for (var faceIndex in eligibleFaces)
    {
        const boundingCoEdges = faceBoundingCoEdges(coEdgeRecords, faceIndex);
        for (var breakIndex = 0; breakIndex < size(partition) - 1; breakIndex += 1)
        {
            const segmentStart = partition[breakIndex];
            const segmentEnd = partition[breakIndex + 1];
            if (segmentEnd - segmentStart < MINIMUM_CONTACT_INTERVAL_FRACTION * sweepSpan)
            {
                continue;
            }
            faceSegments += 1;
            const ruling = planarFaceCrossingsAtTime(strippedMotion, coEdgeRecords, boundingCoEdges,
                0.5 * (segmentStart + segmentEnd), settings.scanSamples);
            if (ruling.sliding)
            {
                slidingFaces = appendUniqueIndex(slidingFaces, faceIndex);
                skipped = append(skipped, { "patchKind" : "face", "ownerIndex" : faceIndex,
                            "segmentIndex" : breakIndex,
                            "reason" : "SWEEP_FACE_SLIDING: the contact function is identically " ~
                            "zero along a bounding edge, so the face carries no contact line." });
                continue;
            }
            if (size(ruling.crossings) == 0)
            {
                continue;
            }
            if (size(ruling.crossings) != 2)
            {
                skipped = append(skipped, { "patchKind" : "face", "ownerIndex" : faceIndex,
                            "segmentIndex" : breakIndex,
                            "reason" : "SWEEP_FACE_CROSSING_COUNT: " ~ size(ruling.crossings) ~
                            " boundary crossings at the segment midpoint, expected two on a " ~
                            "convex face." });
                continue;
            }
            patches = append(patches, {
                        "patchKind" : "face",
                        "ownerIndex" : faceIndex,
                        "segmentIndex" : breakIndex,
                        "tStart" : segmentStart,
                        "tEnd" : segmentEnd,
                        "directrixSources" : [
                                { "edgeIndex" : ruling.crossings[0].edgeIndex,
                                    "side" : ruling.crossings[0].side },
                                { "edgeIndex" : ruling.crossings[1].edgeIndex,
                                    "side" : ruling.crossings[1].side }
                            ]
                    });
        }
    }

    for (var edgeIndex in eligibleEdges)
    {
        const coEdgeRecord = coEdgeRecords[edgeIndex];
        for (var breakIndex = 0; breakIndex < size(partition) - 1; breakIndex += 1)
        {
            const segmentStart = partition[breakIndex];
            const segmentEnd = partition[breakIndex + 1];
            if (segmentEnd - segmentStart < MINIMUM_CONTACT_INTERVAL_FRACTION * sweepSpan)
            {
                continue;
            }
            edgeSegments += 1;
            const funnel = sharpEdgeFunnelSpansAtTime(strippedMotion, coEdgeRecord,
                0.5 * (segmentStart + segmentEnd), settings.scanSamples);
            if (funnel.sliding)
            {
                skipped = append(skipped, { "patchKind" : "edge", "ownerIndex" : edgeIndex,
                            "segmentIndex" : breakIndex,
                            "reason" : "SWEEP_EDGE_SLIDING: an adjacent face slides along this edge." });
                continue;
            }
            if (!funnel.found)
            {
                continue;
            }
            if (size(funnel.spans) != 1)
            {
                skipped = append(skipped, { "patchKind" : "edge", "ownerIndex" : edgeIndex,
                            "segmentIndex" : breakIndex,
                            "reason" : "SWEEP_EDGE_FUNNEL_SPLIT: " ~ size(funnel.spans) ~
                            " disjoint funnel spans at the segment midpoint; v1 rules between one." });
                continue;
            }
            patches = append(patches, {
                        "patchKind" : "edge",
                        "ownerIndex" : edgeIndex,
                        "segmentIndex" : breakIndex,
                        "tStart" : segmentStart,
                        "tEnd" : segmentEnd,
                        "directrixSources" : [
                                { "source" : funnel.spans[0].lowSource },
                                { "source" : funnel.spans[0].highSource }
                            ]
                    });
        }
    }

    return {
            "breakpoints" : partition,
            "patches" : patches,
            "segmentCounts" : { "face" : faceSegments, "edge" : edgeSegments },
            "skipped" : skipped,
            "slidingFaces" : slidingFaces
        };
}

/** `indices` with `candidate` appended if it is not already present. */
function appendUniqueIndex(indices is array, candidate is number) returns array
{
    for (var existing in indices)
    {
        if (existing == candidate)
        {
            return indices;
        }
    }
    return append(indices, candidate);
}

/**
 * The ruled patch of one planned entry: sample its two directrices at t stations across the
 * segment and interpolate a tensor-product surface, degree 1 across the ruling.
 *
 * A face patch's directrices are the two boundary crossings, matched to the segment's planned
 * source edges so the same directrix stays on the same edge across the whole patch - which the
 * segment's own definition guarantees, since the type can change only at that face's own
 * breakpoint. An edge sheet's are the two ends of its funnel span. Both are EXACT points; the
 * only error a patch carries is the interpolation of those directrices in t, which more
 * stations buy down.
 *
 * options: { stationCount {number}, rulingColumns {number}, scanSamples {number} }.
 *
 * A ruling that collapses to a point at the FIRST or LAST station is correct geometry, not a
 * defect: a segment boundary is the moment the contact line enters or leaves the face through a
 * corner, so the patch tapers to that corner and `interpolateFitGrid` carries the collapse
 * exactly (a collapsed data row becomes a collapsed control row). A collapse at an INTERIOR
 * station is different - the patch pinches in its own middle - so the two are counted apart.
 *
 * Returns { failed, reason, surface {map}, grid {array}, stations {array},
 * collapsedStart {boolean}, collapsedEnd {boolean}, collapsedInterior {number},
 * worstDirectrixTurn {number} : the largest angle in radians between consecutive chords of
 * either directrix. A directrix that doubles back turns through nearly pi there, and a ruled
 * net built over one is self-overlapping - which the kernel answers with
 * CANNOT_MAKE_BSPLINESURFACE and no indication of which net it disliked, so the fold is
 * measured here instead of being inferred from the refusal }.
 */
export function fitRuledEnvelopePatch(strippedMotion is map, coEdgeRecords is array,
    faceBounding is array, patchPlan is map, options is map) returns map
{
    const settings = mergeMaps({
                "stationCount" : DEFAULT_PATCH_STATION_COUNT,
                "rulingColumns" : DEFAULT_PATCH_RULING_COLUMNS,
                "scanSamples" : 0
            }, options);
    return fitRuledEnvelopePatchAtStations(strippedMotion, coEdgeRecords, faceBounding,
        patchPlan, settings, max(2, settings.stationCount));
}

/**
 * One attempt at the ruled patch, at a station count the caller has chosen. The emission loop
 * calls this directly when the kernel has refused a patch and it is being refitted coarser.
 * Returns the same map `fitRuledEnvelopePatch` does.
 */
export function fitRuledEnvelopePatchAtStations(strippedMotion is map, coEdgeRecords is array,
    faceBounding is array, patchPlan is map, options is map, requestedStations is number) returns map
{
    const settings = mergeMaps({
                "stationCount" : DEFAULT_PATCH_STATION_COUNT,
                "rulingColumns" : DEFAULT_PATCH_RULING_COLUMNS,
                "scanSamples" : 0
            }, options);
    const stationCount = max(2, requestedStations);
    const columnCount = max(2, settings.rulingColumns);
    var stations = makeArray(stationCount, 0);
    var grid = makeArray(stationCount);
    var collapsedStart = false;
    var collapsedEnd = false;
    var collapsedInterior = 0;
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        const t = patchPlan.tStart +
            (patchPlan.tEnd - patchPlan.tStart) * stationIndex / (stationCount - 1);
        stations[stationIndex] = t;
        const ends = directrixEndsAtStation(strippedMotion, coEdgeRecords, faceBounding,
            patchPlan, t, settings.scanSamples);
        if (ends.failed)
        {
            return { "failed" : true, "reason" : "station " ~ stationIndex ~ " at t = " ~ t ~
                    ": " ~ ends.reason, "surface" : undefined, "grid" : undefined,
                    "stations" : stations, "collapsedStart" : collapsedStart,
                    "collapsedEnd" : collapsedEnd, "collapsedInterior" : collapsedInterior,
                    "worstDirectrixTurn" : 0, "leastDirectrixTravel" : 0,
                    "netExtent" : 0, "minParameterGap" : 0,
                    "transverseTravel" : 0, "netOvershoot" : 0 };
        }
        const collapsed = squaredNorm(ends.endPoint - ends.startPoint) <
            PATCH_RULING_COLLAPSE_TOLERANCE * PATCH_RULING_COLLAPSE_TOLERANCE;
        // A collapsed ruling is SNAPPED to its own midpoint, so every column of the row is the
        // same point and the row is exactly degenerate. Reporting the collapse and then handing
        // the kernel the residue is what leaves a sliver edge: the apex a taper is supposed to
        // produce becomes an edge a few nanometres long, which is real enough to be built and too
        // short to bound anything, so the patch is refused and whatever survives has a free edge
        // no neighbour can match. An exact apex is a shape the kernel accepts.
        const rulingMidpoint = 0.5 * (ends.startPoint + ends.endPoint);
        var row = makeArray(columnCount, vector(0, 0, 0));
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            const blend = columnIndex / (columnCount - 1);
            row[columnIndex] = collapsed ? rulingMidpoint :
                (1 - blend) * ends.startPoint + blend * ends.endPoint;
        }
        grid[stationIndex] = row;
        if (collapsed)
        {
            if (stationIndex == 0)
            {
                collapsedStart = true;
            }
            else if (stationIndex == stationCount - 1)
            {
                collapsedEnd = true;
            }
            else
            {
                collapsedInterior += 1;
            }
        }
    }

    // Every sheet of one shell has to face the same way before any of them is built. Nothing
    // upstream chooses the ruling's direction, and the direction is what decides which side the
    // sheet calls outside - see `orientRuledGridOutward`.
    const oriented = orientRuledGridOutward(grid,
        patchOutwardReferenceInTool(coEdgeRecords, patchPlan),
        evaluateMotionSample(strippedMotion, stations[floor(0.5 * stationCount)], 1));
    grid = oriented.grid;

    // Two measures of the patch's own shape, both taken before the kernel is asked for anything,
    // because CANNOT_MAKE_BSPLINESURFACE names no net and no reason.
    //
    // TRAVEL is how far each directrix moves along the segment. A patch's ruling can be a healthy
    // length while the whole strip goes essentially nowhere in t - a contact line that holds
    // still - and that patch is a sliver in the direction the sliver check was not looking. Its
    // rows then coincide, its chord parameters repeat, and the interpolation that has to invert
    // them is singular.
    //
    // TURN is the fold measure: a ruled patch is emitted over the motion of its two directrices,
    // and if either reverses inside the segment the surface laps back over itself. The reversal
    // shows as consecutive chords pointing nearly opposite ways, which no amount of station
    // refinement removes because it is the geometry rather than the sampling.
    // TRANSVERSE TRAVEL is how far the patch advances ACROSS its own ruling. A ruled patch whose
    // directrices move along the ruling direction rather than across it is a sliver however
    // healthy its ruling length and its travel look separately, and this is the width any
    // interpolation overshoot has to be judged against.
    var transverseTravel = 0;
    for (var stationIndex = 1; stationIndex < stationCount; stationIndex += 1)
    {
        const ruling = grid[stationIndex - 1][columnCount - 1] - grid[stationIndex - 1][0];
        const rulingLength = norm(ruling);
        if (rulingLength < 1e-12)
        {
            continue;
        }
        const step = grid[stationIndex][0] - grid[stationIndex - 1][0];
        transverseTravel += norm(cross(step, (1 / rulingLength) * ruling));
    }

    var worstDirectrixTurn = 0;
    var leastDirectrixTravel = 1e300;
    for (var columnIndex in [0, columnCount - 1])
    {
        var travel = 0;
        for (var stationIndex = 1; stationIndex < stationCount; stationIndex += 1)
        {
            travel += norm(grid[stationIndex][columnIndex] - grid[stationIndex - 1][columnIndex]);
        }
        leastDirectrixTravel = min(leastDirectrixTravel, travel);
        for (var stationIndex = 1; stationIndex < stationCount - 1; stationIndex += 1)
        {
            const before = grid[stationIndex][columnIndex] - grid[stationIndex - 1][columnIndex];
            const after = grid[stationIndex + 1][columnIndex] - grid[stationIndex][columnIndex];
            if (squaredNorm(before) < 1e-24 || squaredNorm(after) < 1e-24)
            {
                continue;
            }
            worstDirectrixTurn = max(worstDirectrixTurn, angleBetween(before, after) / radian);
        }
    }

    const uDegree = min(3, stationCount - 1);
    const vDegree = min(3, columnCount - 1);
    // The u parameters are the stations' own normalized TIMES, not this grid's chord lengths.
    // A patch shares each of its two boundary columns with a neighbouring patch, and a chord
    // parameterization averaged over a grid's own columns is different on the two sides of that
    // shared column - so the same curve gets fitted twice, differently, and the seam between the
    // two sheets is a gap rather than a shared edge. Time is the one parameterization both sides
    // agree on without having to know about each other.
    var uParameters = makeArray(stationCount, 0);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        uParameters[stationIndex] = stationIndex / (stationCount - 1);
    }
    var surface = undefined;
    var reason = "";
    try
    {
        surface = interpolateFitGrid(grid, uDegree, vDegree, false, undefined, uParameters);
    }
    catch (error)
    {
        reason = "SWEEP_PATCH_INTERPOLATION_REFUSED: " ~ toString(error);
    }
    if (surface == undefined)
    {
        return { "failed" : true, "reason" : reason, "surface" : undefined, "grid" : grid,
                "stations" : stations, "collapsedStart" : collapsedStart,
                "collapsedEnd" : collapsedEnd, "collapsedInterior" : collapsedInterior,
                "worstDirectrixTurn" : worstDirectrixTurn,
                "leastDirectrixTravel" : leastDirectrixTravel,
                "netExtent" : 0, "minParameterGap" : 0,
                "transverseTravel" : transverseTravel, "netOvershoot" : 0 };
    }
    // The net's own conditioning, which is what an ill-posed interpolation shows up as: a
    // repeated u parameter makes the collocation matrix singular and the control points that
    // come back out of it are enormous rather than merely wrong.
    var netExtent = 0;
    for (var row in surface.controlPoints)
    {
        for (var point in row)
        {
            netExtent = max(netExtent, norm(point));
        }
    }
    var minParameterGap = 1e300;
    for (var index = 1; index < size(surface.uParameters); index += 1)
    {
        minParameterGap = min(minParameterGap, surface.uParameters[index] - surface.uParameters[index - 1]);
    }
    // OVERSHOOT is how far the interpolation's control points stray from the data they pass
    // through. A clamped interpolation returns one control point per data point, so the two grids
    // line up index for index. Judged against the transverse travel above it says whether the net
    // still lies inside the patch: a control point pushed out of a sliver is what a kernel
    // answers CANNOT_MAKE_BSPLINESURFACE to.
    var netOvershoot = 0;
    for (var rowIndex = 0; rowIndex < size(surface.controlPoints) && rowIndex < stationCount; rowIndex += 1)
    {
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            netOvershoot = max(netOvershoot,
                norm(surface.controlPoints[rowIndex][columnIndex] - grid[rowIndex][columnIndex]));
        }
    }
    return { "failed" : false, "reason" : "", "surface" : surface, "grid" : grid,
            "stations" : stations, "collapsedStart" : collapsedStart,
            "collapsedEnd" : collapsedEnd, "collapsedInterior" : collapsedInterior,
            "worstDirectrixTurn" : worstDirectrixTurn,
            "leastDirectrixTravel" : leastDirectrixTravel,
            "netExtent" : netExtent, "minParameterGap" : minParameterGap,
            "transverseTravel" : transverseTravel, "netOvershoot" : netOvershoot,
            "reversedForOutward" : oriented.reversed,
            "orientationDecided" : oriented.decided };
}

/**
 * The two world-space ends of a planned patch's ruling at one station, solved the way that
 * patch's kind defines them.
 * Returns { failed, reason, startPoint, endPoint }.
 */
/**
 * The direction the envelope faces AWAY from the swept material, at this patch's contact set, in
 * TOOL coordinates - the reference a patch's own surface normal has to agree with.
 *
 * At a grazing contact the envelope is tangent to the moving tool, so the two share a normal, and
 * the side the material is on is the side the TOOL's material is on. A plane face's contact line
 * therefore faces the way that face faces; a convex sharp edge's funnel faces somewhere in the cone
 * between its two adjacent faces, and any interior direction of that cone serves as a SIGN
 * reference because a convex cone spans less than half a turn. Both are constant in tool
 * coordinates on a polyhedron, so this is read once per patch rather than per station.
 *
 * Returns undefined where the records carry no normal for the side asked about.
 */
function patchOutwardReferenceInTool(coEdgeRecords is array, patchPlan is map)
{
    if (patchPlan.patchKind == "edge")
    {
        const coEdgeRecord = coEdgeRecords[patchPlan.ownerIndex];
        const leftNormals = coEdgeRecord.sideNormals.left;
        const rightNormals = coEdgeRecord.sideNormals.right;
        if (leftNormals == undefined || rightNormals == undefined)
        {
            return undefined;
        }
        const middle = floor(0.5 * size(leftNormals));
        const summed = leftNormals[middle] + rightNormals[middle];
        if (squaredNorm(summed) < 1e-20)
        {
            return undefined;
        }
        return normalize(summed);
    }
    // A face patch names the co-edge and the SIDE its own directrix rides, and that side's normal
    // array is this face's own normal - which is what makes the reference readable without the
    // face records.
    const source = patchPlan.directrixSources[0];
    const normals = coEdgeRecords[source.edgeIndex].sideNormals[source.side];
    if (normals == undefined || size(normals) == 0)
    {
        return undefined;
    }
    return normals[floor(0.5 * size(normals))];
}

/**
 * `grid` with every row's columns reversed where the net's own surface normal opposes the envelope's
 * outward direction, so that every emitted sheet of one shell faces the same way.
 *
 * A tensor-product sheet's normal is the cross product of its u and v partials, so the ruling's
 * DIRECTION decides which way the sheet faces - and nothing upstream chooses that direction. A
 * plane face's two directrices arrive in the order its bounding co-edges happen to sit in the
 * co-edge array, and a sharp edge's arrive in the order of that edge's own parameterization: both
 * are arbitrary with respect to the material. A shell whose sheets disagree about which side is
 * outside has no coherent inside, which is what `opBoolean` refuses when it is asked to make a
 * solid of it, and reversing the ruling changes the parameterization and not one point of the
 * geometry.
 *
 * The comparison is taken at the station whose ruling is LONGEST, because a patch that tapers has
 * no ruling direction at all at its apex and a near-degenerate one carries no reliable sign.
 *
 * Returns { grid {array}, reversed {boolean}, decided {boolean} } - `decided` false when no
 * reference or no usable ruling was available, in which case the grid is returned untouched.
 */
function orientRuledGridOutward(grid is array, outwardInTool, frozenSample is map) returns map
{
    if (outwardInTool == undefined || size(grid) < 2)
    {
        return { "grid" : grid, "reversed" : false, "decided" : false };
    }
    const columnCount = size(grid[0]);
    var bestStation = -1;
    var bestSquared = 0;
    for (var stationIndex = 0; stationIndex < size(grid); stationIndex += 1)
    {
        const spread = squaredNorm(grid[stationIndex][columnCount - 1] - grid[stationIndex][0]);
        if (spread > bestSquared)
        {
            bestStation = stationIndex;
            bestSquared = spread;
        }
    }
    if (bestStation < 0)
    {
        return { "grid" : grid, "reversed" : false, "decided" : false };
    }
    const ruling = grid[bestStation][columnCount - 1] - grid[bestStation][0];
    // The u step is taken across the longest-ruling station, from whichever neighbours exist.
    const after = min(size(grid) - 1, bestStation + 1);
    const before = max(0, bestStation - 1);
    const step = grid[after][0] - grid[before][0];
    const natural = cross(step, ruling);
    if (squaredNorm(natural) < 1e-24)
    {
        return { "grid" : grid, "reversed" : false, "decided" : false };
    }
    const outward = frozenSample.rotation * outwardInTool;
    if (dot(natural, outward) >= 0)
    {
        return { "grid" : grid, "reversed" : false, "decided" : true };
    }
    var flipped = makeArray(size(grid));
    for (var stationIndex = 0; stationIndex < size(grid); stationIndex += 1)
    {
        var row = makeArray(columnCount, vector(0, 0, 0));
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            row[columnIndex] = grid[stationIndex][columnCount - 1 - columnIndex];
        }
        flipped[stationIndex] = row;
    }
    return { "grid" : flipped, "reversed" : true, "decided" : true };
}

function directrixEndsAtStation(strippedMotion is map, coEdgeRecords is array, faceBounding is array,
    patchPlan is map, t is number, scanSamples is number) returns map
{
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    if (patchPlan.patchKind == "edge")
    {
        const coEdgeRecord = coEdgeRecords[patchPlan.ownerIndex];
        const funnel = sharpEdgeFunnelSpansAtTime(frozen, coEdgeRecord, t, scanSamples);
        if (size(funnel.spans) == 1)
        {
            const ends = sharpEdgeSpanEndPoints(frozen, coEdgeRecord, funnel.spans[0], t);
            return { "failed" : false, "reason" : "", "startPoint" : ends[0], "endPoint" : ends[1] };
        }
        // A segment boundary can sit EXACTLY on the funnel's own opening or closing - the
        // partition holds one time per event, and that event is this funnel's. The span there is
        // a point, not missing, but a zero-width span has no sign change to find. A probe a hair
        // inside the segment says WHERE on the edge the span lives; the apex is that parameter
        // evaluated at the boundary's own time, snapped to the vertex when the span opens from
        // one - so both patches sharing the event compute the identical point. A missing span at
        // an INTERIOR station stays a refusal: the segment was planned around a funnel it does
        // not have.
        const segmentWidth = patchPlan.tEnd - patchPlan.tStart;
        const atStart = abs(t - patchPlan.tStart) <= abs(patchPlan.tEnd - t);
        const boundaryGap = atStart ? abs(t - patchPlan.tStart) : abs(patchPlan.tEnd - t);
        if (size(funnel.spans) == 0 && segmentWidth > 0 && boundaryGap < 1e-3 * segmentWidth)
        {
            const probeT = atStart ? t + 1e-6 * segmentWidth : t - 1e-6 * segmentWidth;
            const probe = sharpEdgeFunnelSpansAtTime(strippedMotion, coEdgeRecord, probeT,
                scanSamples);
            if (size(probe.spans) == 1)
            {
                const span = probe.spans[0];
                var apexS = 0.5 * (span.sLow + span.sHigh);
                if (span.lowSource == "startVertex" || span.sLow < STRIP_ROOT_VERTEX_MARGIN)
                {
                    apexS = 0;
                }
                else if (span.highSource == "endVertex" || span.sHigh > 1 - STRIP_ROOT_VERTEX_MARGIN)
                {
                    apexS = 1;
                }
                const apex = sharpEdgeSpanEndPoints(frozen, coEdgeRecord,
                    { "sLow" : apexS, "sHigh" : apexS,
                        "lowSource" : span.lowSource, "highSource" : span.highSource }, t);
                return { "failed" : false, "reason" : "",
                        "startPoint" : apex[0], "endPoint" : apex[1] };
            }
        }
        return { "failed" : true,
                "reason" : "SWEEP_EDGE_FUNNEL_UNSTABLE: " ~ size(funnel.spans) ~
                " funnel span(s) inside a segment planned with one.",
                "startPoint" : undefined, "endPoint" : undefined };
    }

    const ruling = planarFaceCrossingsAtTime(frozen, coEdgeRecords, faceBounding, t, scanSamples);
    var startPoint = undefined;
    var endPoint = undefined;
    for (var crossing in ruling.crossings)
    {
        if (startPoint == undefined && crossing.edgeIndex == patchPlan.directrixSources[0].edgeIndex &&
            crossing.side == patchPlan.directrixSources[0].side)
        {
            startPoint = crossing.worldPoint;
        }
        else if (endPoint == undefined && crossing.edgeIndex == patchPlan.directrixSources[1].edgeIndex &&
            crossing.side == patchPlan.directrixSources[1].side)
        {
            endPoint = crossing.worldPoint;
        }
    }
    if (startPoint == undefined || endPoint == undefined)
    {
        return { "failed" : true, "reason" : "SWEEP_PATCH_DIRECTRIX_LOST: the ruling left edges " ~
                patchPlan.directrixSources[0].edgeIndex ~ " and " ~
                patchPlan.directrixSources[1].edgeIndex ~ " inside their own contact segment (" ~
                size(ruling.crossings) ~ " crossing(s) here).",
                "startPoint" : undefined, "endPoint" : undefined };
    }
    return { "failed" : false, "reason" : "", "startPoint" : startPoint, "endPoint" : endPoint };
}

/**
 * Emit every planned patch as its own B-spline sheet body.
 *
 * `nextId` is an id source - `getUnstableIncrementingId(id)` - because a thrown operation still
 * registers the id it was given, so hand-naming ids in this loop would mask the first real
 * failure behind a duplicate-id error.
 *
 * Returns {
 *     bodies {array of Query}, patchReports {array} : one per planned patch, carrying the plan
 *         plus stage, refusal, reason, net size and ruling extent,
 *     emittedCount, refusedCount {number},
 *     shortestPatchExtent {number} : the smallest LONGEST ruling any patch reaches, in meters -
 *         the sliver test, because a patch's extent is how long it ever gets, not how short it
 *         gets at the corner it tapers into,
 *     longestRulingLength {number},
 *     interiorCollapses {number} : rulings that collapsed away from a patch's own ends, which is
 *         a pinch rather than a taper and is never expected,
 *     worstDirectrixTurn {number} : radians, the sharpest corner any directrix turns through -
 *         near pi means a directrix doubled back and the patch laps over itself
 * }
 *
 * `options.colorPatches` paints each emitted sheet by what produced it - warm for a plane face's
 * grazing patch, cool for a sharp edge's, shaded by which face or edge it came from. A shell of
 * three dozen anonymous sheets is unreadable in the viewport, and a patch in the wrong place, a
 * hole where a segment was skipped, and a sliver that reports a healthy number all look alike
 * until the sheets are told apart by eye.
 */
/**
 * Fit one planned patch and offer it to the kernel, halving the station count and offering again
 * on each refusal, down to the bilinear net whose control points are the data.
 *
 * The kernel is the acceptance test. It is the only party that knows which nets it will build, so
 * asking it costs one refused operation where guessing a threshold costs every sound patch its
 * accuracy. A patch that survives at full stations pays nothing.
 *
 * Returns { fit, emission, stationCount, refits, accepted } - `emission` undefined when the fit
 * itself failed, and `accepted` true only when a body exists.
 */
function emitRuledPatchWithRefit(context is Context, nextId is function, strippedMotion is map,
    coEdgeRecords is array, faceBounding is array, patchPlan is map, options is map,
    requestedStations is number) returns map
{
    var fit = fitRuledEnvelopePatchAtStations(strippedMotion, coEdgeRecords, faceBounding,
        patchPlan, options, requestedStations);
    var stationCount = requestedStations;
    var emission = fit.failed ? undefined : emitFitSurfacePatch(context, nextId(), fit.surface);
    var refits = 0;
    while (!fit.failed && emission.refused && stationCount > 2 && refits < PATCH_REFIT_ATTEMPTS)
    {
        refits += 1;
        stationCount = max(2, floor(stationCount / 2));
        fit = fitRuledEnvelopePatchAtStations(strippedMotion, coEdgeRecords, faceBounding,
            patchPlan, options, stationCount);
        if (fit.failed)
        {
            break;
        }
        emission = emitFitSurfacePatch(context, nextId(), fit.surface);
    }
    return {
            "fit" : fit, "emission" : emission, "stationCount" : stationCount, "refits" : refits,
            "accepted" : !fit.failed && !emission.refused
        };
}

export function emitPolyhedralEnvelope(context is Context, nextId is function, strippedMotion is map,
    faceRecords is array, coEdgeRecords is array, plan is map, options is map) returns map
{
    const settings = mergeMaps({ "colorPatches" : false, "namePatches" : true }, options);
    var boundingByFace = makeArray(size(faceRecords));
    for (var faceIndex = 0; faceIndex < size(faceRecords); faceIndex += 1)
    {
        boundingByFace[faceIndex] = faceBoundingCoEdges(coEdgeRecords, faceIndex);
    }
    var bodies = [];
    var patchReports = [];
    var emittedCount = 0;
    var refusedCount = 0;
    var shortestPatchExtent = 1e300;
    var longestRulingLength = 0;
    var interiorCollapses = 0;
    var worstDirectrixTurn = 0;
    var refittedCount = 0;
    var reversedCount = 0;
    var undecidedCount = 0;
    const requestedStations = max(2, mergeMaps({ "stationCount" : DEFAULT_PATCH_STATION_COUNT },
                options).stationCount);
    // A patch the kernel refuses at every station count is NOT dropped. Its interval is carried
    // into a neighbour on the same owner and the pair is emitted as one wider patch. Dropping it
    // leaves a hole in the shell, and a hole costs more than a slightly wider patch: measured on
    // the rotating cube, four refused slivers put the envelope 3.669938e-3 m out against a fit
    // error of zero.
    //
    // Forward is the cheap direction and needs no deletion, because a refused operation makes no
    // body - the next segment of the same owner simply starts earlier. A refusal in an owner's
    // LAST segment has no successor, so it is absorbed backwards instead, which does mean
    // deleting that neighbour's sheet and re-emitting it over the union.
    //
    // A merged patch spans a contact breakpoint, which is the one thing the shared partition
    // exists to prevent. That trade is made deliberately and only where the kernel has already
    // refused every faithful form of the segment; the merge reports its own span so the ledger
    // can see what was given up.
    var pendingStart = undefined;
    var pendingEnd = 0;
    var pendingOwner = "";
    var lastAccepted = undefined;
    var mergedForward = 0;
    var mergedBackward = 0;
    var unmergeableRefusals = 0;
    var widestMergeSpan = 0;

    for (var patchIndex = 0; patchIndex < size(plan.patches); patchIndex += 1)
    {
        const patchPlan = plan.patches[patchIndex];
        const ownerKey = patchPlan.patchKind ~ ":" ~ patchPlan.ownerIndex;

        // Leaving an owner with an unemitted interval still pending: absorb it backwards.
        if (ownerKey != pendingOwner && pendingStart != undefined)
        {
            if (lastAccepted == undefined)
            {
                unmergeableRefusals += 1;
            }
            else
            {
                const widened = mergeMaps(lastAccepted.plan, { "tEnd" : pendingEnd });
                // A widened interval can reach into a sub-interval where this owner has no
                // contact at all - that is often WHY the partition cut there - and the fitter
                // is not obliged to survive being asked for a directrix that does not exist.
                // A merge that cannot be fitted is simply not merged.
                var retry = { "accepted" : false, "emission" : undefined };
                try
                {
                    retry = emitRuledPatchWithRefit(context, nextId, strippedMotion,
                        coEdgeRecords, lastAccepted.faceBounding, widened, options,
                        requestedStations);
                }
                if (retry.accepted)
                {
                    opDeleteBodies(context, nextId(), {
                                "entities" : qCreatedBy(lastAccepted.id, EntityType.BODY)
                            });
                    bodies[size(bodies) - 1] = qCreatedBy(retry.emission.id, EntityType.BODY);
                    mergedBackward += 1;
                    widestMergeSpan = max(widestMergeSpan, pendingEnd - pendingStart);
                }
                else
                {
                    unmergeableRefusals += 1;
                }
            }
            pendingStart = undefined;
        }
        if (ownerKey != pendingOwner)
        {
            pendingOwner = ownerKey;
            lastAccepted = undefined;
        }

        const faceBounding = patchPlan.patchKind == "face" ?
            boundingByFace[patchPlan.ownerIndex] : [];
        const effectivePlan = pendingStart == undefined ? patchPlan :
            mergeMaps(patchPlan, { "tStart" : pendingStart });
        // Guarded for the same reason as the backward merge: a forward-merged plan starts earlier
        // than its own segment and may reach across a sub-interval this owner does not contact.
        // A patch that cannot be fitted is REPORTED, never allowed to take the regeneration down.
        var attempt = { "accepted" : false, "fit" : undefined, "emission" : undefined,
                "stationCount" : requestedStations, "refits" : 0 };
        try
        {
            attempt = emitRuledPatchWithRefit(context, nextId, strippedMotion, coEdgeRecords,
                faceBounding, effectivePlan, options, requestedStations);
        }
        // A MERGED form that will not build must not poison the rest of the owner's chain. The
        // widened interval reaches across a segment boundary the partition put there for a
        // reason, so when it fails the right answer is to give up on the merge, emit THIS segment
        // on its own interval, and report the sliver as unmerged - not to widen further. Widening
        // on failure cascades: one refused patch takes every later segment of the same owner with
        // it, which is measurably worse than the hole it was trying to avoid.
        if (!attempt.accepted && pendingStart != undefined)
        {
            unmergeableRefusals += 1;
            pendingStart = undefined;
            attempt = { "accepted" : false, "fit" : undefined, "emission" : undefined,
                    "stationCount" : requestedStations, "refits" : 0 };
            try
            {
                attempt = emitRuledPatchWithRefit(context, nextId, strippedMotion, coEdgeRecords,
                    faceBounding, patchPlan, options, requestedStations);
            }
        }
        if (attempt.fit == undefined)
        {
            if (pendingStart == undefined)
            {
                pendingStart = patchPlan.tStart;
            }
            pendingEnd = patchPlan.tEnd;
            patchReports = append(patchReports, mergeMaps(patchPlan, {
                            "stage" : "fit", "refused" : true, "mergePending" : true,
                            "reason" : "SWEEP_PATCH_FIT_UNAVAILABLE: no fit could be built over " ~
                            "this interval.",
                            "stationCount" : requestedStations, "refits" : 0, "faceCount" : 0,
                            "shortestRuling" : 0, "longestRuling" : 0,
                            "collapsedStart" : false, "collapsedEnd" : false,
                            "collapsedInterior" : 0, "worstDirectrixTurn" : 0,
                            "leastDirectrixTravel" : 0, "netExtent" : 0, "minParameterGap" : 0,
                            "transverseTravel" : 0, "netOvershoot" : 0,
                            "netRows" : 0, "netColumns" : 0
                        }));
            continue;
        }
        const fit = attempt.fit;
        const stationCount = attempt.stationCount;
        const refits = attempt.refits;

        if (!attempt.accepted)
        {
            // Carry the whole unemitted interval - this segment plus anything already pending -
            // into the next segment of this owner.
            if (pendingStart == undefined)
            {
                pendingStart = effectivePlan.tStart;
            }
            pendingEnd = effectivePlan.tEnd;
            patchReports = append(patchReports, mergeMaps(effectivePlan, {
                            "stage" : fit.failed ? "fit" : "emit",
                            "refused" : true, "mergePending" : true,
                            "reason" : fit.failed ? fit.reason : attempt.emission.reason,
                            "stationCount" : stationCount, "refits" : refits,
                            "faceCount" : 0,
                            "shortestRuling" : 0, "longestRuling" : 0,
                            "collapsedStart" : fit.collapsedStart, "collapsedEnd" : fit.collapsedEnd,
                            "collapsedInterior" : fit.collapsedInterior,
                            "worstDirectrixTurn" : fit.worstDirectrixTurn,
                            "leastDirectrixTravel" : fit.leastDirectrixTravel,
                            "netExtent" : fit.netExtent, "minParameterGap" : fit.minParameterGap,
                            "transverseTravel" : fit.transverseTravel,
                            "netOvershoot" : fit.netOvershoot,
                            "netRows" : 0, "netColumns" : 0
                        }));
            continue;
        }
        if (pendingStart != undefined)
        {
            mergedForward += 1;
            widestMergeSpan = max(widestMergeSpan, effectivePlan.tEnd - pendingStart);
            pendingStart = undefined;
        }

        const emission = attempt.emission;
        var patchShortest = 1e300;
        var patchLongest = 0;
        for (var row in fit.grid)
        {
            const rulingLength = norm(row[size(row) - 1] - row[0]);
            patchShortest = min(patchShortest, rulingLength);
            patchLongest = max(patchLongest, rulingLength);
        }
        shortestPatchExtent = min(shortestPatchExtent, patchLongest);
        longestRulingLength = max(longestRulingLength, patchLongest);
        interiorCollapses += fit.collapsedInterior;
        worstDirectrixTurn = max(worstDirectrixTurn, fit.worstDirectrixTurn);
        if (refits > 0)
        {
            refittedCount += 1;
        }
        if (fit.reversedForOutward == true)
        {
            reversedCount += 1;
        }
        if (fit.orientationDecided != true)
        {
            undecidedCount += 1;
        }
        emittedCount += 1;
        const patchBody = qCreatedBy(emission.id, EntityType.BODY);
        bodies = append(bodies, patchBody);
        lastAccepted = { "id" : emission.id, "plan" : effectivePlan,
                "faceBounding" : faceBounding };
        if (settings.colorPatches)
        {
            setProperty(context, {
                        "entities" : patchBody,
                        "propertyType" : PropertyType.APPEARANCE,
                        "value" : polyhedralPatchColor(patchPlan.patchKind, patchPlan.ownerIndex)
                    });
        }
        // The sheet carries its own identity and the two numbers that decide whether its net is
        // sound. A part name is readable straight off the parts list, which is one API call and no
        // Part Studio watcher - and watchers are a limited resource a diagnostic should not spend.
        //
        // Off when the sheets are about to be knitted: naming is an operation per sheet, and the
        // closure needs every operation this evaluation has left.
        if (settings.namePatches)
        {
            setProperty(context, {
                        "entities" : patchBody,
                        "propertyType" : PropertyType.NAME,
                        "value" : patchPlan.patchKind ~ " " ~ patchPlan.ownerIndex ~ " seg " ~
                        patchPlan.segmentIndex ~ " | " ~ stationCount ~ " stations" ~
                        (refits > 0 ? " (refit " ~ refits ~ "x)" : "") ~ " | transverse " ~
                        roundToPrecision(fit.transverseTravel, 7) ~ " | overshoot " ~
                        roundToPrecision(fit.netOvershoot, 7)
                    });
        }
        patchReports = append(patchReports, mergeMaps(effectivePlan, {
                        "stage" : "emit", "refused" : false, "reason" : emission.reason,
                        "stationCount" : stationCount, "refits" : refits,
                        "faceCount" : emission.faceCount, "shortestRuling" : patchShortest,
                        "longestRuling" : patchLongest, "collapsedInterior" : fit.collapsedInterior,
                        "collapsedStart" : fit.collapsedStart, "collapsedEnd" : fit.collapsedEnd,
                        "worstDirectrixTurn" : fit.worstDirectrixTurn,
                        "leastDirectrixTravel" : fit.leastDirectrixTravel,
                        "netExtent" : fit.netExtent, "minParameterGap" : fit.minParameterGap,
                        "transverseTravel" : fit.transverseTravel,
                        "netOvershoot" : fit.netOvershoot,
                        "reversedForOutward" : fit.reversedForOutward,
                        "orientationDecided" : fit.orientationDecided,
                        "netRows" : size(fit.surface.controlPoints),
                        "netColumns" : size(fit.surface.controlPoints[0])
                    }));
    }

    // The last owner's trailing refusal, if any, has no successor either.
    if (pendingStart != undefined)
    {
        if (lastAccepted == undefined)
        {
            unmergeableRefusals += 1;
        }
        else
        {
            const widened = mergeMaps(lastAccepted.plan, { "tEnd" : pendingEnd });
            // A widened interval can reach into a sub-interval where this owner has no
            // contact at all - that is often WHY the partition cut there - and the fitter
            // is not obliged to survive being asked for a directrix that does not exist.
            // A merge that cannot be fitted is simply not merged.
            var retry = { "accepted" : false, "emission" : undefined };
            try
            {
                retry = emitRuledPatchWithRefit(context, nextId, strippedMotion,
                    coEdgeRecords, lastAccepted.faceBounding, widened, options,
                    requestedStations);
            }
            if (retry.accepted)
            {
                opDeleteBodies(context, nextId(), {
                            "entities" : qCreatedBy(lastAccepted.id, EntityType.BODY)
                        });
                bodies[size(bodies) - 1] = qCreatedBy(retry.emission.id, EntityType.BODY);
                mergedBackward += 1;
                widestMergeSpan = max(widestMergeSpan, pendingEnd - pendingStart);
            }
            else
            {
                unmergeableRefusals += 1;
            }
        }
    }
    refusedCount = unmergeableRefusals;

    return {
            "bodies" : bodies, "patchReports" : patchReports,
            "emittedCount" : emittedCount, "refusedCount" : refusedCount,
            "shortestPatchExtent" : emittedCount > 0 ? shortestPatchExtent : 0,
            "longestRulingLength" : longestRulingLength,
            "interiorCollapses" : interiorCollapses,
            "worstDirectrixTurn" : worstDirectrixTurn,
            "refittedCount" : refittedCount,
            "reversedCount" : reversedCount,
            "undecidedOrientationCount" : undecidedCount,
            "mergedForward" : mergedForward, "mergedBackward" : mergedBackward,
            "widestMergeSpan" : widestMergeSpan
        };
}

/**
 * The cap trim curves at one end of the sweep: one straight contact segment per PLANE face that
 * grazes there, as a degree-1 stripped curve through the face's two boundary crossings.
 *
 * Only the face rulings are handed to the imprint. A sharp edge's contact span at the same
 * station lies exactly along an edge the cap copy already carries, so it splits nothing and
 * asking the kernel to project it would ask for a degenerate split; the face rulings alone cut
 * every cap face the contact set crosses, which is what the classification needs.
 *
 * Returns { curves {array}, faceIndices {array}, skipped {array of string} }.
 */
export function polyhedralCapContactCurves(strippedMotion is map, faceRecords is array,
    coEdgeRecords is array, t is number, scanSamples is number) returns map
{
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    var curves = [];
    var faceIndices = [];
    var skipped = [];
    for (var faceIndex = 0; faceIndex < size(faceRecords); faceIndex += 1)
    {
        if (faceRecords[faceIndex].surfaceClass != SweepSurfaceClass.PLANE)
        {
            continue;
        }
        const ruling = planarFaceCrossingsAtTime(frozen, coEdgeRecords,
            faceBoundingCoEdges(coEdgeRecords, faceIndex), t, scanSamples);
        if (size(ruling.crossings) != 2)
        {
            // Every refusal is named, the empty one included. A face with no crossings is the
            // commonest way a cap ends up with no trim curve at all, and a count of zero reported
            // as silence is indistinguishable from a face that was never examined.
            skipped = append(skipped, "face " ~ faceIndex ~ ": " ~ size(ruling.crossings) ~
                " crossings at t = " ~ t ~ (ruling.sliding ? ", sliding" : ""));
            continue;
        }
        // A face that grazes at a single POINT has its two crossings on top of each other, and
        // the segment between them is a curve of zero length. It splits nothing, and the kernel
        // asked to build it does not refuse - it takes the regeneration down. A face touching at
        // a point is correct geometry at a cap; it just carries no trim curve.
        const span = ruling.crossings[1].worldPoint - ruling.crossings[0].worldPoint;
        if (squaredNorm(span) < CAP_CONTACT_CURVE_MINIMUM_LENGTH * CAP_CONTACT_CURVE_MINIMUM_LENGTH)
        {
            skipped = append(skipped, "face " ~ faceIndex ~ ": its two crossings at t = " ~ t ~
                " are " ~ norm(span) ~ " m apart, so it grazes at a point and carries no trim curve.");
            continue;
        }
        // Degree THREE, as a single Bezier span with its control points spaced evenly along the
        // segment - which reproduces the straight line exactly. The kernel already refuses a
        // degree-1 multi-span net when it is asked for a surface; a degree-1 curve is the same
        // question in one dimension fewer, and here it does not refuse it, it takes the
        // regeneration down. A cubic is a shape it is asked for constantly.
        const p0 = ruling.crossings[0].worldPoint;
        curves = append(curves, {
                    "degree" : 3,
                    "isPeriodic" : false,
                    "isRational" : false,
                    "controlPoints" : [p0, p0 + (1 / 3) * span, p0 + (2 / 3) * span, p0 + span],
                    "knots" : [0, 0, 0, 0, 1, 1, 1, 1]
                });
        faceIndices = append(faceIndices, faceIndex);
    }
    return { "curves" : curves, "faceIndices" : faceIndices, "skipped" : skipped };
}

/**
 * The correspondence ledger at ONE cap station: what the cap's own boundary is made of, and what
 * the lateral patches arriving at that station expect to meet.
 *
 * A cap is a copy of the tool, and the shell that has to sew to it was cut on funnel SPANS. Where
 * a span covers a whole tool edge the two boundaries are the same curve and the union sews; where
 * it covers part of one, the cap offers a whole edge against the shell's sub-segment and the pair
 * has no partner. This function reports which of the two every owner is in, so the unmatched edge
 * a census counts as a number can be named by the entity that owns it.
 *
 * Diagnostic only: nothing downstream reads it, and it makes no operations. Every number in it
 * comes from the same closed-form calls the emission itself uses, so a line here is the plan's own
 * arithmetic rather than a second opinion about it.
 *
 * Returns an array of report lines.
 */
export function describeCapContactCorrespondence(strippedMotion is map, faceRecords is array,
    coEdgeRecords is array, t is number, scanSamples is number) returns array
{
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    var lines = [];
    for (var faceIndex = 0; faceIndex < size(faceRecords); faceIndex += 1)
    {
        const record = faceRecords[faceIndex];
        if (record.surfaceClass != SweepSurfaceClass.PLANE)
        {
            lines = append(lines, "face " ~ faceIndex ~ ": class " ~
                toString(record.surfaceClass) ~ ", off the polyhedral route");
            continue;
        }
        const ruling = planarFaceCrossingsAtTime(frozen, coEdgeRecords,
            faceBoundingCoEdges(coEdgeRecords, faceIndex), t, scanSamples);
        var line = "face " ~ faceIndex ~ ": " ~ size(ruling.crossings) ~ " crossing(s)" ~
            (ruling.sliding ? " SLIDING" : "") ~ " scale " ~
            toString(roundToPrecision(ruling.valueScale, 12));
        for (var crossing in ruling.crossings)
        {
            line = line ~ " | edge " ~ crossing.edgeIndex ~ " " ~ crossing.side ~ " s " ~
            toString(roundToPrecision(crossing.s, 9));
        }
        if (size(ruling.crossings) == 2)
        {
            line = line ~ " | chord " ~ toString(roundToPrecision(
                        norm(ruling.crossings[1].worldPoint - ruling.crossings[0].worldPoint), 12)) ~
            " m";
        }
        lines = append(lines, line);
    }

    // The edge half. A cap edge is whole; a lateral edge-sheet's t-boundary is the funnel span.
    // Classifying every edge as covered, partial or absent is what says whether the shell can be
    // matched to the cap COINCIDENT or needs the span expressed as a containment.
    for (var edgeIndex = 0; edgeIndex < size(coEdgeRecords); edgeIndex += 1)
    {
        const coEdgeRecord = coEdgeRecords[edgeIndex];
        const descriptor = affineCoEdgeContactDescriptor(coEdgeRecord);
        const funnel = sharpEdgeFunnelSpansAtTime(frozen, coEdgeRecord, t, scanSamples, descriptor);
        var line = "edge " ~ edgeIndex ~ ": " ~
        (descriptor.isAffine ? "affine" : "sampled") ~ ", " ~
        (funnel.sliding ? "SLIDING" : (funnel.found ? size(funnel.spans) ~ " span(s)" : "no funnel"));
        for (var span in funnel.spans)
        {
            // Against the strip solver's own parameter tolerance, which is what decides whether
            // the span's end sits ON the vertex or inside the edge.
            const wholeLow = span.sLow <= 10 * STRIP_ROOT_PARAMETER_TOLERANCE;
            const wholeHigh = span.sHigh >= 1 - 10 * STRIP_ROOT_PARAMETER_TOLERANCE;
            line = line ~ " | [" ~ toString(roundToPrecision(span.sLow, 9)) ~ ", " ~
            toString(roundToPrecision(span.sHigh, 9)) ~ "] " ~
            (wholeLow && wholeHigh ? "WHOLE" : "PARTIAL") ~
            " low " ~ toString(span.lowSource) ~ " high " ~ toString(span.highSource);
        }
        lines = append(lines, line);
    }
    return lines;
}

/**
 * The tool edge nearest `worldPoint` at station `t`, with the edge parameter it lands at - the
 * name for a point a census could only report as coordinates.
 *
 * A geometric search, and deliberately confined to diagnostics: it says which entity a measured
 * free edge belongs to so a failure can be described, and nothing that decides topology may call
 * it. Identity on the production path comes from tracking queries, never from a nearest match.
 *
 * Returns { edgeIndex, s, distance } in unit-stripped meters, or undefined for no records.
 */
export function locateWorldPointOnToolEdges(strippedMotion is map, coEdgeRecords is array,
    worldPoint is Vector, t is number, samplesPerEdge is number) returns map
{
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    const sampleCount = max(samplesPerEdge, 2);
    var bestEdge = -1;
    var bestParameter = 0;
    var bestSquared = 0;
    for (var edgeIndex = 0; edgeIndex < size(coEdgeRecords); edgeIndex += 1)
    {
        const coEdgeRecord = coEdgeRecords[edgeIndex];
        if (coEdgeRecord.edgePoints == undefined)
        {
            continue;
        }
        for (var index = 0; index < sampleCount; index += 1)
        {
            const s = index / (sampleCount - 1);
            const located = interpolateCoEdgePoint(coEdgeRecord, s);
            const separation = squaredNorm(worldPoint -
                    (frozen.rotation * located.point + frozen.translation));
            if (bestEdge == -1 || separation < bestSquared)
            {
                bestEdge = edgeIndex;
                bestParameter = s;
                bestSquared = separation;
            }
        }
    }
    if (bestEdge == -1)
    {
        return undefined;
    }
    return { "edgeIndex" : bestEdge, "s" : bestParameter, "distance" : sqrt(bestSquared) };
}

/** The plan's breakpoints, interval count, and per-kind patch counts, for the console. */
/**
 * Whether the planned patches close the envelope's CROSS-SECTION at one station (spec 6.8's
 * certified census, on the polyhedral route).
 *
 * At any instant the contact set of a convex tool is a single closed curve on its boundary, made
 * of plane-face contact lines and sharp-edge funnel spans laid end to end. Each arc contributes
 * two endpoints, and the curve closes exactly when every endpoint is shared by exactly TWO arcs.
 * An endpoint owned by one arc is a GAP - the shell has a slit running down it in t - and one
 * owned by three is a branch, which is a shell that crosses itself.
 *
 * This is the invariant a seam census cannot see. Pairing one-sided edges by midpoint asks whether
 * each emitted sheet found a neighbour, which is a question about the sheets that were emitted; it
 * cannot report an arc that was never planned at all, because a missing arc leaves no edge to go
 * unpaired - it leaves two OTHER edges paired with each other.
 *
 * Returns { closed {boolean}, arcCount, clusters {array} : [{ point, owners {array}, count }],
 * worstCount, orphans, branches }.
 */
export function certifyCrossSectionLoop(strippedMotion is map, coEdgeRecords is array,
    faceBoundingByFace is array, plan is map, t is number, tolerance is number) returns map
{
    const frozen = evaluateMotionSample(strippedMotion, t, 1);
    var endPoints = [];
    var owners = [];
    var arcCount = 0;
    for (var patchPlan in plan.patches)
    {
        if (t < patchPlan.tStart || t > patchPlan.tEnd)
        {
            continue;
        }
        const faceBounding = patchPlan.patchKind == "face" ?
            faceBoundingByFace[patchPlan.ownerIndex] : [];
        const ends = directrixEndsAtStation(frozen, coEdgeRecords, faceBounding, patchPlan, t, 0);
        if (ends.failed)
        {
            continue;
        }
        arcCount += 1;
        const label = patchPlan.patchKind ~ " " ~ patchPlan.ownerIndex ~ "/" ~
            patchPlan.segmentIndex;
        endPoints = append(endPoints, ends.startPoint);
        owners = append(owners, label);
        endPoints = append(endPoints, ends.endPoint);
        owners = append(owners, label);
    }

    // Cluster the endpoints. A convex tool has few arcs, so this is a small quadratic pass on
    // numbers that are already in hand.
    var assigned = makeArray(size(endPoints), -1);
    var clusters = [];
    for (var index = 0; index < size(endPoints); index += 1)
    {
        if (assigned[index] != -1)
        {
            continue;
        }
        var members = [owners[index]];
        assigned[index] = size(clusters);
        for (var other = index + 1; other < size(endPoints); other += 1)
        {
            if (assigned[other] != -1)
            {
                continue;
            }
            if (squaredNorm(endPoints[other] - endPoints[index]) <= tolerance * tolerance)
            {
                assigned[other] = size(clusters);
                members = append(members, owners[other]);
            }
        }
        clusters = append(clusters, { "point" : endPoints[index], "owners" : members,
                    "count" : size(members) });
    }

    var orphans = 0;
    var branches = 0;
    var worstCount = 0;
    for (var cluster in clusters)
    {
        worstCount = max(worstCount, cluster.count);
        if (cluster.count < 2)
        {
            orphans += 1;
        }
        else if (cluster.count > 2)
        {
            branches += 1;
        }
    }
    return { "closed" : orphans == 0 && branches == 0, "arcCount" : arcCount,
            "clusters" : clusters, "worstCount" : worstCount, "orphans" : orphans,
            "branches" : branches };
}

export function summarizePolyhedralPlan(plan is map) returns string
{
    var facePatches = 0;
    var edgePatches = 0;
    for (var patchPlan in plan.patches)
    {
        if (patchPlan.patchKind == "face")
        {
            facePatches += 1;
        }
        else
        {
            edgePatches += 1;
        }
    }
    var summary = size(plan.breakpoints) ~ " sweep-wide breakpoint(s); " ~
        plan.segmentCounts["face"] ~ " face and " ~ plan.segmentCounts["edge"] ~
        " edge own-breakpoint segment(s) examined; " ~ size(plan.patches) ~
        " patch(es) planned (" ~ facePatches ~ " face, " ~ edgePatches ~ " sharp edge)";
    if (size(plan.slidingFaces) > 0)
    {
        summary = summary ~ "; sliding face(s) " ~ toString(plan.slidingFaces);
    }
    summary = summary ~ "; breakpoints";
    for (var breakTime in plan.breakpoints)
    {
        summary = summary ~ " " ~ roundToPrecision(breakTime, 6);
    }
    return summary;
}

/**
 * The emission's aggregate counts, plus a line only for the patches worth reading about.
 *
 * The per-patch detail is deliberately NOT all of it: a console line carrying every patch of a
 * cube runs to thousands of characters and the notices pane drops it whole, taking the counts
 * with it. `describePolyhedralPatches` is the full listing for a caller that wants it.
 */
export function summarizePolyhedralEmission(emission is map) returns string
{
    var summary = emission.emittedCount ~ " sheet(s) emitted, " ~ emission.refusedCount ~
        " refused; smallest patch extent " ~ emission.shortestPatchExtent ~ " m, longest ruling " ~
        emission.longestRulingLength ~ " m, " ~ emission.interiorCollapses ~
        " interior collapse(s), " ~ emission.refittedCount ~
        " refitted coarser after a kernel refusal, worst directrix turn " ~
        roundToPrecision(emission.worstDirectrixTurn * 180 / PI, 1) ~ " deg, " ~
        emission.reversedCount ~ " ruling(s) reversed to face outward" ~
        (emission.undecidedOrientationCount > 0 ?
            (", " ~ emission.undecidedOrientationCount ~ " UNDECIDED") : "");
    for (var report in emission.patchReports)
    {
        if (!report.refused && report.collapsedInterior == 0 && report.faceCount == 1)
        {
            continue;
        }
        summary = summary ~ "; " ~ describeOnePolyhedralPatch(report);
    }
    return summary;
}

/** Every emitted patch, one entry each - kind, owner, segment, net size and ruling extent. */
export function describePolyhedralPatches(emission is map) returns array
{
    var lines = makeArray(size(emission.patchReports), "");
    for (var index = 0; index < size(emission.patchReports); index += 1)
    {
        lines[index] = describeOnePolyhedralPatch(emission.patchReports[index]);
    }
    return lines;
}

/**
 * The viewport colour of one emitted patch: warm for a plane face's grazing patch, cool for a
 * sharp edge's sheet, with the owner index shading it so neighbouring patches are told apart.
 */
export function polyhedralPatchColor(patchKind is string, ownerIndex is number) returns Color
{
    const shade = 0.25 + 0.12 * (ownerIndex % 6);
    if (patchKind == "face")
    {
        return color(0.90, 0.25 + shade * 0.5, 0.15);
    }
    return color(0.15, 0.35 + shade * 0.4, 0.90);
}

/**
 * One emitted patch, named and measured.
 *
 * The SHAPE is reported before the outcome, on refusals as well as successes: a kernel that
 * answers CANNOT_MAKE_BSPLINESURFACE says nothing about which net it disliked, and the two
 * things that make a ruled net unacceptable - a patch that tapers to a point at BOTH ends, and
 * one whose ruling collapses in its own middle - are both visible in these flags.
 */
function describeOnePolyhedralPatch(report is map) returns string
{
    const shape = "ruling " ~ roundToPrecision(report.shortestRuling, 6) ~ " .. " ~
        roundToPrecision(report.longestRuling, 6) ~ " m, travel " ~
        roundToPrecision(report.leastDirectrixTravel, 8) ~ " m, turn " ~
        roundToPrecision(report.worstDirectrixTurn * 180 / PI, 1) ~ " deg, net extent " ~
        roundToPrecision(report.netExtent, 4) ~ " m, transverse " ~
        roundToPrecision(report.transverseTravel, 8) ~ " m, overshoot " ~
        roundToPrecision(report.netOvershoot, 8) ~ " m" ~
        (report.collapsedStart == true ? ", tapers at start" : "") ~
        (report.collapsedEnd == true ? ", tapers at end" : "") ~
        (report.collapsedInterior > 0 ?
            (", " ~ report.collapsedInterior ~ " INTERIOR collapse(s)") : "");
    return "[" ~ report.patchKind ~ " " ~ report.ownerIndex ~ " segment " ~ report.segmentIndex ~
        " t " ~ roundToPrecision(report.tStart, 4) ~ ".." ~ roundToPrecision(report.tEnd, 4) ~ "] " ~
        (report.refused ? (shape ~ " - REFUSED " ~ report.reason) :
            ("" ~ report.netRows ~ "x" ~ report.netColumns ~ " net, " ~ report.faceCount ~
                " face, " ~ shape));
}
