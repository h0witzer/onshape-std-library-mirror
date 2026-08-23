FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

/**
 * BERNSTEIN POLYNOMIAL UTILITIES - pure coefficient arithmetic for polynomials held in
 * Bernstein (Bezier) form. Spec: docs/specs/SOLID_SWEEP_SPEC.md section 6.0 (the
 * coefficient-first envelope solver). Companion tester: bernsteinPolynomialUtilsTester.fs.
 *
 * STATUS (2026-08-22): the 2D grid layer is formulated over native matrix builtins - grid
 * multiplication runs as binomial cwise masks + per-kernel-row Toeplitz matrix products with
 * native shifted sums, elevation as closed-form E*G*E^T operator products, differentiation as
 * banded matrix products, add/subtract/scale as native matrix operations, and grid subdivision
 * as split-operator matrix products (the de Casteljau split is linear; operators assembled
 * row-locally). Validated live through swEnvelopeMath's factored-vs-pointwise consistency test
 * (1e-19 relative, owner profile 1.88 s on the stress workload) and the tester's full run
 * (93 checks green 2026-08-22, including u- and v-direction native subdivision parity).
 *
 * This module is deliberately STANDALONE and dependency-free (standard library only). It is
 * NOT part of splineRefinementUtils.fs, so refining the solid sweep never forces a republish
 * and version bump across that module's consumers.
 *
 * REPRESENTATION CONVENTIONS (used by every function here):
 *   - A univariate polynomial is a plain array of unitless numbers - its Bernstein
 *     coefficients on the domain [0, 1]. The degree is implicit: size - 1.
 *   - A bivariate polynomial is an array of rows of unitless numbers (a coefficient grid).
 *     The ROW index is the FIRST parameter (u); the COLUMN index is the SECOND parameter (v).
 *     Degrees are implicit: rows - 1 in u, columns - 1 in v.
 *   - A vector-valued polynomial is an array of exactly three coefficient arrays (or three
 *     grids), one per world component: [xCoefficients, yCoefficients, zCoefficients].
 *     Components may have DIFFERENT degrees; sums and products elevate as needed.
 *   - Numbers only, no ValueWithUnits anywhere. Strip units upstream, reattach downstream.
 *   - Grid-valued results may carry the std Matrix type tag: a Matrix is an array of plain
 *     rows, so every consumer indexes and sizes it exactly like an untagged grid. Bulk grid
 *     arithmetic routes through the native matrix builtins; interpreted loops are reserved
 *     for small operator-matrix assembly, read-only scans, and short univariate arrays.
 *
 * WHY BERNSTEIN FORM: the envelope function f = <A(t)*(S_u x S_v), A'(t)*S + b'(t)> is a
 * polynomial per Bezier patch of the tool face. Held as coefficients, the convex-hull
 * property answers "can f vanish here at all?" from coefficient signs alone (see
 * bernsteinExcludesZero), and root isolation is deterministic subdivision - no sampling.
 */

// ===================== Univariate: evaluation, arithmetic =====================

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
        throw "bernsteinPolynomialUtils: multiplyBernstein requires non-empty coefficient arrays.";
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
        throw "bernsteinPolynomialUtils: cannot elevate degree " ~ (size(coefficients) - 1) ~
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

// ===================== Univariate: sign analysis, root isolation =====================

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
        throw "bernsteinPolynomialUtils: isolateBernsteinRoots got an identically-zero polynomial " ~
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

// ===================== Univariate: vector-valued =====================

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

// ===================== Bivariate: evaluation, arithmetic =====================

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
        throw "bernsteinPolynomialUtils: accumulateScaledBernsteinGrids needs equally many grids and weights (nonzero count).";
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
                throw "bernsteinPolynomialUtils: accumulateScaledBernsteinGrids requires one shared degree pair " ~
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

// ===================== Bivariate: vector-valued =====================

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

// ===================== Private helpers =====================

/**
 * Binomial coefficient n-choose-k as a number (exact in doubles for the degrees this
 * module sees; envelope-function degrees stay well under 30).
 */
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
        throw "bernsteinPolynomialUtils: empty coefficient grid.";
    }
    const columnCount = size(grid[0]);
    if (columnCount == 0)
    {
        throw "bernsteinPolynomialUtils: coefficient grid has an empty row.";
    }
    for (var rowIndex = 1; rowIndex < size(grid); rowIndex += 1)
    {
        if (size(grid[rowIndex]) != columnCount)
        {
            throw "bernsteinPolynomialUtils: coefficient grid is not rectangular (row 0 has " ~
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
