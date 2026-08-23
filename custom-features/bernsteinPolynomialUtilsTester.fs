FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

import(path : "8b495c3bb1037b467ca1d02e", version : "3968d1ef5b507302198a917b"); //bernsteinPolynomialUtils.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * BERNSTEIN POLYNOMIAL UTILS TESTER - fixed validation vectors for every exported function of
 * bernsteinPolynomialUtils.fs. No geometry is created; results go to the console and the
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
        // notice, and ANY notice makes the MCP harness return notices INSTEAD of the console
        // (measured 2026-08-22). Enable interactively in a real document; harness payloads
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

// ===================== Tester helpers =====================

