FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");   // bSplineCurve, knotArray
import(path : "onshape/std/splineUtils.fs", version : "3044.0");     // evaluateSpline

// Non-standard import: the module under test lives in custom-features/splineRefinementUtils.fs.
// TODO(publish): replace both placeholder ids with the published document/version ids of
// splineRefinementUtils.fs (see the std-library browser-updater flow). For FIRST BRING-UP it
// is simpler to paste splineRefinementUtils.fs into the same Feature Studio ABOVE this file
// and delete the import line entirely.
// import(path : "SPLINE_REFINEMENT_UTILS_DOCUMENT_ID", version : "SPLINE_REFINEMENT_UTILS_VERSION_ID");//splineRefinementUtils.fs
import(path : "9a2b77793cdc37bace6d915a", version : "7e8a1bdb8f400bfbcc839a00");//splineRefinementUtils.fs


/*
    Spline Refinement Tester
    ========================

    Runs the validation vectors of docs/specs/SPLINE_REFINEMENT_UTILITY_SPEC.md section 6
    against splineRefinementUtils.fs and reports pass/fail counts. No geometry is created; the
    feature exists purely to execute in an Onshape context and print results.

    Covered here (the implemented core):
      Vector 1 - the tiling-spec section 5.3 clamped extraction example, structure AND
                 curve-reproduction checked numerically.
      Vector 2 - the spec section 2.1 counterexample: inserting 0.5 into a clamped degree-2
                 spline must COPY control point Q3 = P2, never blend. This is the regression
                 test for the retired tweenSurfaces insertKnotBoehm bug; both the direct path
                 and the refinementOperator path are checked.
      Vector 3 - convex-combination invariant: every refinementOperator row has non-negative weights
                 summing to 1.
      Vector 4 - geometry preservation: a refined curve evaluates identically to its input
                 (std evaluateSpline on BSplineCurve, pure, no Context needed - safe THERE
                 because the fixture is non-rational; the kernel builtin silently ignores
                 weights, so rational comparisons use evaluateRationalCurvePoints instead).
      Vector 5 - refinementOperator path and direct path produce identical control points and knots.
      Vector 6 - (structural half) uniformPeriodExtractionOperator across degrees and span
                 counts: output count degree + spanCount, convex rows, identity interior rows.
                 The term-for-term comparison against displacementMap.fs extractionWeights
                 happens during that feature's migration, not here.
      Evaluator sanity - basis partition of unity; bilinear patch midpoint and corner
                 evaluation including the domain-end span case.

    Curve-level Layer 3 (all implemented, NOT yet run in Onshape as of this addition):
      NORM   - normalizeSplineDefinition: force-rational on a plain input; genuine periodic
               clamping, checked by evaluating the RAW periodic-flagged array literally over
               its own domain (valid because Boehm insertion/evaluation is a purely local array
               operation, independent of the periodic flag) and comparing against the clamped
               output. The ported std kernel-quirk branch (single-overlapping-knot case) is
               NOT independently covered here - ported verbatim from std editCurve.fs, and its
               exact trigger shape could not be verified without a live kernel-returned
               periodic curve to inspect.
      MERGE  - mergeKnotVectors: merge(A, A) == A; a hand-verified two-vector union at max
               multiplicity; refining each input up to the merge reproduces it from both sides.
      REFINE-COUNT - refineSplineToControlPointCount: exact target count, geometry preserved,
               no-op when already met.
      SHARE  - makeSplinesShareKnotVector: two differently-structured curves land on an
               identical knot vector, each still matching its own original geometry.
      DECOMPOSE - decomposeIntoBezierSegments: every returned segment, evaluated as its own
               plain Bezier, matches the parent curve on its sub-domain.
      ELEVATE (vector 7) - elevateSplineDegree: geometry preserved across degree 2->3 (a plain
               Bezier and a 3-segment case), 3->5, and a repeated-interior-knot case; no-op
               when the degree is already met.
      COMPATIBLE - makeSplinesCompatible: a mixed-degree pair lands on a shared degree AND
               knot vector, each still matching its own original geometry.
      PREPARE - prepareSplineForDeformation: elevate-then-refine composition smoke test.

    Surface-level Layer 3 (the four hooks tweenSurfaces.fs needs; also implemented, NOT yet
    run in Onshape as of this addition):
      SURFACE-NORM - normalizeSurfaceDefinition: force-rational; per-direction periodic
               clamping (U-periodic/V-clamped fixture, so it also proves the two directions
               are handled independently, not just "is any direction periodic").
      SURFACE-REFINE-COUNT - refineSurfaceToControlPointCounts: exact U and V target counts,
               geometry preserved.
      SURFACE-ELEVATE - elevateSurfaceDegrees: both directions reach target degree, geometry
               preserved.
      SURFACE-SHARE - makeSurfacesShareKnotVectors: two surfaces with different interior U/V
               structure land on identical U and V knot vectors, each still matching its own
               original geometry.
      SURFACE-COMPATIBLE - makeSurfacesCompatible: a lower-U-degree surface is elevated to
               match, then both share knot vectors, each still matching its own geometry.

    refineSurfaceToSpanDensity, decomposeSurfaceIntoBezierPatches, extractSubSurface, and
    prepareSurfaceForDeformation are still stubbed in the module (not needed by
    tweenSurfaces.fs) and have no vectors here.
*/

annotation { "Feature Type Name" : "Spline Refinement Tester", "UIHint" : "NO_PREVIEW_PROVIDED" }
export const splineRefinementTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Print passing checks" }
        definition.printPassingChecks is boolean;
    }
    {
        var passCount = new box(0);
        var failures = new box([]);

        runSectionFiveThreeVector(passCount, failures, definition.printPassingChecks);
        runCounterexampleVector(passCount, failures, definition.printPassingChecks);
        runConvexCombinationVector(passCount, failures, definition.printPassingChecks);
        runGeometryPreservationVector(passCount, failures, definition.printPassingChecks);
        runOperatorVersusDirectVector(passCount, failures, definition.printPassingChecks);
        runUniformExtractionStructureVector(passCount, failures, definition.printPassingChecks);
        runEvaluatorSanityChecks(passCount, failures, definition.printPassingChecks);

        runNormalizationVector(passCount, failures, definition.printPassingChecks);
        runMergeKnotVectorsVector(passCount, failures, definition.printPassingChecks);
        runRefineToControlPointCountVector(passCount, failures, definition.printPassingChecks);
        runShareKnotVectorVector(passCount, failures, definition.printPassingChecks);
        runDecomposeIntoBezierSegmentsVector(passCount, failures, definition.printPassingChecks);
        runElevateSplineDegreeVector(passCount, failures, definition.printPassingChecks);
        runMakeSplinesCompatibleVector(passCount, failures, definition.printPassingChecks);
        runPrepareSplineForDeformationVector(passCount, failures, definition.printPassingChecks);

        runSurfaceNormalizationVector(passCount, failures, definition.printPassingChecks);
        runSurfaceRefineToControlPointCountsVector(passCount, failures, definition.printPassingChecks);
        runSurfaceElevateDegreesVector(passCount, failures, definition.printPassingChecks);
        runSurfaceShareKnotVectorsVector(passCount, failures, definition.printPassingChecks);
        runSurfaceMakeSurfacesCompatibleVector(passCount, failures, definition.printPassingChecks);
        runSurfacePrepareForDeformationVector(passCount, failures, definition.printPassingChecks);
        runExtractSubSurfaceVector(passCount, failures, definition.printPassingChecks);
        runDecomposeSurfaceIntoBezierPatchesVector(passCount, failures, definition.printPassingChecks);
        runSpanDensityVector(passCount, failures, definition.printPassingChecks);
        runSurfaceDerivativeVector(passCount, failures, definition.printPassingChecks);
        runSimplifySurfaceVector(passCount, failures, definition.printPassingChecks);
        runInterpolationVector(passCount, failures, definition.printPassingChecks);
        runArcLengthPlacementVector(passCount, failures, definition.printPassingChecks);
        runRedundantKnotVector(passCount, failures, definition.printPassingChecks);
        runIsocurveVector(passCount, failures, definition.printPassingChecks);
        runConcatenationVector(passCount, failures, definition.printPassingChecks);
        runLoftVector(passCount, failures, definition.printPassingChecks);
        runPeriodicRefinementVector(passCount, failures, definition.printPassingChecks);
        runPeriodicElevationVector(passCount, failures, definition.printPassingChecks);
        runPeriodicShareKnotVectorVector(passCount, failures, definition.printPassingChecks);
        runReverseSplineVector(passCount, failures, definition.printPassingChecks);
        runRewindowPeriodicSplineVector(passCount, failures, definition.printPassingChecks);
        runTightPeriodicSplineVector(passCount, failures, definition.printPassingChecks);
        runPeriodicOperatorVector(passCount, failures, definition.printPassingChecks);
        runSurfacePeriodicVector(passCount, failures, definition.printPassingChecks);
        runSurfaceRewindowVector(passCount, failures, definition.printPassingChecks);
        runBezierArcSeamVector(passCount, failures, definition.printPassingChecks);
        runClosedClampedVector(passCount, failures, definition.printPassingChecks);
        runClosedClampedSurfaceVector(passCount, failures, definition.printPassingChecks);
        runReverseCanonicalVector(passCount, failures, definition.printPassingChecks);
        runReverseCanonicalSurfaceVector(passCount, failures, definition.printPassingChecks);
        runKernelWeightsProbeVector(passCount, failures, definition.printPassingChecks);

        const summary = passCount[] ~ " checks passed, " ~ size(failures[]) ~ " failed.";
        println("[splineRefinementTester] " ~ summary);
        for (var failureMessage in failures[])
        {
            println("[splineRefinementTester] FAIL: " ~ failureMessage);
        }
        if (size(failures[]) > 0)
        {
            reportFeatureWarning(context, id, "Spline refinement tests FAILED: " ~ summary ~ " See the console for details.");
        }
        else
        {
            reportFeatureInfo(context, id, "Spline refinement tests passed: " ~ summary);
        }
    });

// ============================================================================================
// Check bookkeeping
// ============================================================================================

/**
 * Record one check. Failures accumulate with their description; passes count and optionally
 * print, so a green run can still show what was exercised.
 */
function recordCheck(passCount is box, failures is box, passed is boolean, description is string, printPassing is boolean)
{
    if (passed)
    {
        passCount[] = passCount[] + 1;
        if (printPassing)
        {
            println("[splineRefinementTester] pass: " ~ description);
        }
    }
    else
    {
        failures[] = append(failures[], description);
    }
}

/** Vector equality within a length tolerance. Compares squared distances - equivalent to
    comparing norm() directly (sqrt is monotonic) but skips the sqrt on every call. */
function pointsMatch(pointA is Vector, pointB is Vector) returns boolean
{
    const tolerance = 1e-9 * meter;
    return squaredNorm(pointA - pointB) < tolerance * tolerance;
}

/** Plain-number array equality within KNOT_PARAMETER_TOLERANCE. */
function knotVectorsMatch(knotsA is array, knotsB is array) returns boolean
{
    if (size(knotsA) != size(knotsB))
    {
        return false;
    }
    for (var knotIndex = 0; knotIndex < size(knotsA); knotIndex += 1)
    {
        if (abs(knotsA[knotIndex] - knotsB[knotIndex]) > KNOT_PARAMETER_TOLERANCE)
        {
            return false;
        }
    }
    return true;
}

/** Evaluate a plain (non-rational, non-periodic) curve definition at the given parameters. */
function evaluateCurvePoints(controlPoints is array, knots is array, degree is number, parameters is array) returns array
{
    const curve = bSplineCurve({
                "degree" : degree,
                "isPeriodic" : false,
                "controlPoints" : controlPoints,
                "knots" : knotArray(knots)
            });
    return evaluateSpline({ "spline" : curve, "parameters" : parameters })[0];
}

// ============================================================================================
// Shared fixtures
// ============================================================================================

/** Six distinct 3D control points, centimeter scale. */
function makeSixPointFixture() returns array
{
    var fixturePoints = makeArray(6, vector(0, 0, 0) * centimeter);
    for (var pointIndex = 0; pointIndex < 6; pointIndex += 1)
    {
        fixturePoints[pointIndex] = vector(pointIndex, pointIndex * pointIndex, 3 - pointIndex) * centimeter;
    }
    return fixturePoints;
}

/** Clamped uniform degree-3 knot vector for 6 control points: [0,0,0,0, 1/3, 2/3, 1,1,1,1]. */
function makeClampedCubicKnots() returns array
{
    return [0, 0, 0, 0, 1 / 3, 2 / 3, 1, 1, 1, 1];
}

/** Three distinct 3D control points — a plain degree-2 Bezier. */
function makeThreePointFixture() returns array
{
    return [vector(0, 0, 0) * centimeter, vector(1, 2, 0) * centimeter, vector(2, 0, 1) * centimeter];
}

/** Five distinct 3D control points, centimeter scale. */
function makeFivePointFixture() returns array
{
    var fixturePoints = makeArray(5, vector(0, 0, 0) * centimeter);
    for (var pointIndex = 0; pointIndex < 5; pointIndex += 1)
    {
        fixturePoints[pointIndex] = vector(pointIndex, 5 - pointIndex, pointIndex * 2) * centimeter;
    }
    return fixturePoints;
}

/** Nine evenly spaced sample parameters across [0, 1], reused by every geometry-preservation check. */
function makeUnitSampleParameters() returns array
{
    var sampleParameters = makeArray(9, 0);
    for (var sampleIndex = 0; sampleIndex < 9; sampleIndex += 1)
    {
        sampleParameters[sampleIndex] = sampleIndex / 8;
    }
    return sampleParameters;
}

// ============================================================================================
// Vector 1 — tiling-spec section 5.3 clamped extraction
// ============================================================================================

function runSectionFiveThreeVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 2;
    const uniformKnots = [0, 1, 2, 3, 4, 5, 6, 7, 8]; // 6 control points, domain [2, 6]
    const fixturePoints = makeSixPointFixture();

    const extraction = clampedSegmentOperator(uniformKnots, degree, 3, 5);

    recordCheck(passCount, failures, extraction.outputCount == 4,
        "V1: section 5.3 extraction has 4 control points (got " ~ extraction.outputCount ~ ")", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(extraction.knots, [3, 3, 3, 4, 5, 5, 5]),
        "V1: section 5.3 piece knots are [3,3,3,4,5,5,5] (got " ~ extraction.knots ~ ")", printPassing);

    // The piece must reproduce the parent curve across [3, 5] exactly.
    const sampleParameters = [3, 3.5, 4.2, 5];
    const parentPoints = evaluateCurvePoints(fixturePoints, uniformKnots, degree, sampleParameters);
    const piecePoints = evaluateCurvePoints(applyKnotRefinementOperator(extraction, fixturePoints), extraction.knots, degree, sampleParameters);
    var pieceReproduces = true;
    for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
    {
        if (!pointsMatch(parentPoints[sampleIndex], piecePoints[sampleIndex]))
        {
            pieceReproduces = false;
        }
    }
    recordCheck(passCount, failures, pieceReproduces,
        "V1: extracted piece reproduces the parent curve on [3, 5]", printPassing);
}

// ============================================================================================
// Vector 2 — the spec section 2.1 counterexample (the retired tweenSurfaces bug)
// ============================================================================================

function runCounterexampleVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 2;
    const clampedKnots = [0, 0, 0, 1, 2, 3, 3, 3]; // 5 control points, domain [0, 3]
    var fixturePoints = makeArray(5, vector(0, 0, 0) * centimeter);
    for (var pointIndex = 0; pointIndex < 5; pointIndex += 1)
    {
        fixturePoints[pointIndex] = vector(pointIndex, pointIndex * pointIndex, 0) * centimeter;
    }

    // Direct path: inserting 0.5 (span index 2) must produce
    //   Q1 = 0.5 P0 + 0.5 P1,  Q2 = 0.75 P1 + 0.25 P2,  Q3 = P2 (PLAIN COPY - the bug produced
    //   1.25 P2 - 0.25 P3 here, an extrapolation outside the hull).
    const inserted = insertKnotOnce(fixturePoints, clampedKnots, degree, 0.5);
    recordCheck(passCount, failures, size(inserted.controlPoints) == 6,
        "V2: insertion yields 6 control points", printPassing);
    recordCheck(passCount, failures,
        pointsMatch(inserted.controlPoints[1], 0.5 * fixturePoints[0] + 0.5 * fixturePoints[1]),
        "V2: Q1 is the expected blend of P0 and P1", printPassing);
    recordCheck(passCount, failures,
        pointsMatch(inserted.controlPoints[2], 0.75 * fixturePoints[1] + 0.25 * fixturePoints[2]),
        "V2: Q2 is the expected blend of P1 and P2", printPassing);
    recordCheck(passCount, failures, pointsMatch(inserted.controlPoints[3], fixturePoints[2]),
        "V2: Q3 is a plain copy of P2 (the section 2.1 regression check)", printPassing);
    recordCheck(passCount, failures, pointsMatch(inserted.controlPoints[4], fixturePoints[3]),
        "V2: Q4 is a plain copy of P3", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(inserted.knots, [0, 0, 0, 0.5, 1, 2, 3, 3, 3]),
        "V2: knot vector gains 0.5 in sorted position", printPassing);

    // Operator path: the same insertion's row 3 must be the single identity term {index 2, weight 1}.
    const refinementOperator = knotRefinementOperator(clampedKnots, degree, [0.5]);
    const rowThree = refinementOperator.rows[3];
    recordCheck(passCount, failures,
        size(rowThree) == 1 && rowThree[0].index == 2 && abs(rowThree[0].weight - 1) <= SPARSE_WEIGHT_CUTOFF,
        "V2: refinementOperator row 3 is the single identity term on input 2", printPassing);
}

// ============================================================================================
// Vector 3 — convex-combination invariant on refinementOperator rows
// ============================================================================================

function refinementOperatorRowsAreConvex(refinementOperator is map) returns boolean
{
    for (var row in refinementOperator.rows)
    {
        var weightSum = 0;
        for (var term in row)
        {
            if (term.weight < -1e-12)
            {
                return false;
            }
            weightSum += term.weight;
        }
        if (abs(weightSum - 1) > 1e-9)
        {
            return false;
        }
    }
    return true;
}

function runConvexCombinationVector(passCount is box, failures is box, printPassing is boolean)
{
    const refinement = knotRefinementOperator(makeClampedCubicKnots(), 3, [0.25, 0.5, 0.5, 0.8]);
    recordCheck(passCount, failures, refinementOperatorRowsAreConvex(refinement),
        "V3: refinement refinementOperator rows are convex combinations", printPassing);

    const extraction = clampedSegmentOperator([0, 1, 2, 3, 4, 5, 6, 7, 8], 2, 3, 5);
    recordCheck(passCount, failures, refinementOperatorRowsAreConvex(extraction),
        "V3: clamped extraction refinementOperator rows are convex combinations", printPassing);
}

// ============================================================================================
// Vector 4 — geometry preservation under refinement
// ============================================================================================

function runGeometryPreservationVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 3;
    const clampedKnots = makeClampedCubicKnots();
    const fixturePoints = makeSixPointFixture();
    const refined = refineKnotVector(fixturePoints, clampedKnots, degree, [0.2, 0.45, 0.45, 0.7]);

    var sampleParameters = makeArray(11, 0);
    for (var sampleIndex = 0; sampleIndex < 11; sampleIndex += 1)
    {
        sampleParameters[sampleIndex] = sampleIndex / 10;
    }
    const originalPoints = evaluateCurvePoints(fixturePoints, clampedKnots, degree, sampleParameters);
    const refinedPoints = evaluateCurvePoints(refined.controlPoints, refined.knots, degree, sampleParameters);

    var geometryPreserved = true;
    for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
    {
        if (!pointsMatch(originalPoints[sampleIndex], refinedPoints[sampleIndex]))
        {
            geometryPreserved = false;
        }
    }
    recordCheck(passCount, failures, geometryPreserved,
        "V4: refined curve evaluates identically to the original at 11 parameters", printPassing);
    recordCheck(passCount, failures, size(refined.controlPoints) == 10,
        "V4: four insertions grow 6 control points to 10", printPassing);
}

// ============================================================================================
// Vector 5 — refinementOperator path equals direct path
// ============================================================================================

function runOperatorVersusDirectVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 3;
    const clampedKnots = makeClampedCubicKnots();
    const fixturePoints = makeSixPointFixture();
    const parametersToInsert = [0.2, 0.45, 0.45, 0.7];

    const direct = refineKnotVector(fixturePoints, clampedKnots, degree, parametersToInsert);
    const refinementOperator = knotRefinementOperator(clampedKnots, degree, parametersToInsert);
    const viaOperator = applyKnotRefinementOperator(refinementOperator, fixturePoints);

    var controlPointsAgree = size(viaOperator) == size(direct.controlPoints);
    if (controlPointsAgree)
    {
        const tolerance = 1e-12 * meter;
        for (var pointIndex = 0; pointIndex < size(viaOperator); pointIndex += 1)
        {
            if (squaredNorm(viaOperator[pointIndex] - direct.controlPoints[pointIndex]) > tolerance * tolerance)
            {
                controlPointsAgree = false;
            }
        }
    }
    recordCheck(passCount, failures, controlPointsAgree,
        "V5: refinementOperator and direct insertion produce identical control points", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(refinementOperator.knots, direct.knots),
        "V5: refinementOperator and direct insertion produce identical knot vectors", printPassing);
}

// ============================================================================================
// Vector 6 (structural half) — uniform period extraction across degrees and span counts
// ============================================================================================

function runUniformExtractionStructureVector(passCount is box, failures is box, printPassing is boolean)
{
    for (var degree = 1; degree <= 4; degree += 1)
    {
        for (var spanCount in [5, 8])
        {
            const extraction = uniformPeriodExtractionOperator(degree, spanCount);
            const label = "degree " ~ degree ~ ", spanCount " ~ spanCount;

            recordCheck(passCount, failures, extraction.outputCount == degree + spanCount,
                "V6: " ~ label ~ ": output count is degree + spanCount (got " ~ extraction.outputCount ~ ")", printPassing);
            recordCheck(passCount, failures, refinementOperatorRowsAreConvex(extraction),
                "V6: " ~ label ~ ": rows are convex combinations", printPassing);

            // Away from the clamped ends the extraction is the identity on the window's
            // interior control points: a single term of weight 1.
            var interiorRowsAreIdentity = true;
            for (var outputIndex = degree; outputIndex < extraction.outputCount - degree; outputIndex += 1)
            {
                const row = extraction.rows[outputIndex];
                if (size(row) != 1 || abs(row[0].weight - 1) > SPARSE_WEIGHT_CUTOFF)
                {
                    interiorRowsAreIdentity = false;
                }
            }
            recordCheck(passCount, failures, interiorRowsAreIdentity,
                "V6: " ~ label ~ ": interior rows are identity copies", printPassing);
        }
    }
}

// ============================================================================================
// Evaluator sanity — basis partition of unity, bilinear patch evaluation, domain-end span
// ============================================================================================

function runEvaluatorSanityChecks(passCount is box, failures is box, printPassing is boolean)
{
    // Partition of unity for the cubic basis at assorted parameters, including a knot value.
    const clampedKnots = makeClampedCubicKnots();
    var partitionHolds = true;
    for (var parameter in [0.05, 1 / 3, 0.5, 0.99])
    {
        const spanIndex = findEvaluationSpanIndex(clampedKnots, 3, parameter);
        const basisValues = bSplineBasisValues(clampedKnots, 3, spanIndex, parameter);
        var basisSum = 0;
        for (var basisValue in basisValues)
        {
            if (basisValue < -1e-12)
            {
                partitionHolds = false;
            }
            basisSum += basisValue;
        }
        if (abs(basisSum - 1) > 1e-9)
        {
            partitionHolds = false;
        }
    }
    recordCheck(passCount, failures, partitionHolds,
        "EV: cubic basis is a non-negative partition of unity", printPassing);

    // Bilinear 2x2 patch: center evaluates to the average, corners interpolate, and the
    // (1, 1) corner exercises the domain-end span handling.
    const bilinearPatch = {
            "uDegree" : 1,
            "vDegree" : 1,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : [
                    [vector(0, 0, 0) * centimeter, vector(0, 2, 0) * centimeter],
                    [vector(2, 0, 0) * centimeter, vector(2, 2, 1) * centimeter]
                ],
            "uKnots" : [0, 0, 1, 1],
            "vKnots" : [0, 0, 1, 1]
        };
    recordCheck(passCount, failures,
        pointsMatch(evaluateBSplineSurfacePoint(bilinearPatch, 0.5, 0.5), vector(1, 1, 0.25) * centimeter),
        "EV: bilinear patch center evaluates to the control point average", printPassing);
    recordCheck(passCount, failures,
        pointsMatch(evaluateBSplineSurfacePoint(bilinearPatch, 0, 0), vector(0, 0, 0) * centimeter),
        "EV: bilinear patch (0, 0) corner interpolates", printPassing);
    recordCheck(passCount, failures,
        pointsMatch(evaluateBSplineSurfacePoint(bilinearPatch, 1, 1), vector(2, 2, 1) * centimeter),
        "EV: bilinear patch (1, 1) corner interpolates (domain-end span)", printPassing);
}

// ============================================================================================
// NORM — normalizeSplineDefinition: force-rational, and genuine periodic clamping
// ============================================================================================

function runNormalizationVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 3;
    const knots = makeClampedCubicKnots();
    const fixturePoints = makeSixPointFixture();
    const plainSpline = { "degree" : degree, "isPeriodic" : false, "controlPoints" : fixturePoints, "knots" : knotArray(knots) };
    const normalizedPlain = normalizeSplineDefinition(plainSpline);

    recordCheck(passCount, failures, normalizedPlain.isRational == true,
        "NORM: a non-rational input becomes isRational:true", printPassing);
    var allUnitWeights = size(normalizedPlain.weights) == size(fixturePoints);
    for (var weight in normalizedPlain.weights)
    {
        if (weight != 1)
        {
            allUnitWeights = false;
        }
    }
    recordCheck(passCount, failures, allUnitWeights,
        "NORM: force-rational assigns unit weights to every control point", printPassing);
    // Periodicity is PRESERVED, not clamped - normalizeSplineDefinition's job for a periodic
    // stored-form input is limited to force-rational conversion and rebuilding the outer knot
    // padding from the domain knots (exactly idempotent for this already-canonical fixture).
    // Evaluating the RAW array LITERALLY over its own domain and comparing against the
    // normalized output is valid regardless - Boehm insertion/evaluation is a purely local
    // array operation, independent of the periodic flag.
    const periodicDegree = 2;
    const periodicKnots = [0, 1, 2, 3, 4, 5, 6, 7, 8]; // domain [2, 6] at this degree
    var periodicPoints = makeArray(6, vector(0, 0, 0) * centimeter);
    for (var pointIndex = 0; pointIndex < 6; pointIndex += 1)
    {
        periodicPoints[pointIndex] = vector(2 * pointIndex, pointIndex * pointIndex - pointIndex, pointIndex) * centimeter;
    }
    const periodicSpline = { "degree" : periodicDegree, "isPeriodic" : true, "controlPoints" : periodicPoints, "knots" : knotArray(periodicKnots) };
    const normalizedPeriodic = normalizeSplineDefinition(periodicSpline);

    recordCheck(passCount, failures, normalizedPeriodic.isPeriodic == true,
        "NORM: a periodic input stays isPeriodic:true (preserved, not clamped)", printPassing);
    recordCheck(passCount, failures, normalizedPeriodic.isRational == true,
        "NORM: a periodic input still becomes isRational:true", printPassing);

    const sampleParameters = [2, 3.5, 4.5, 6];
    const literalPoints = evaluateCurvePoints(periodicPoints, periodicKnots, periodicDegree, sampleParameters);
    const normalizedPoints = evaluateCurvePoints(normalizedPeriodic.controlPoints, normalizedPeriodic.knots, periodicDegree, sampleParameters);
    var periodicNormalizePreservesGeometry = true;
    for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
    {
        if (!pointsMatch(literalPoints[sampleIndex], normalizedPoints[sampleIndex]))
        {
            periodicNormalizePreservesGeometry = false;
        }
    }
    recordCheck(passCount, failures, periodicNormalizePreservesGeometry,
        "NORM: normalizing a periodic input reproduces the original array's own geometry on its domain", printPassing);
}

// ============================================================================================
// MERGE — mergeKnotVectors: identity, a hand-verified union, and the refine-to-merge property
// ============================================================================================

function runMergeKnotVectorsVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 3;
    const knotsA = makeClampedCubicKnots(); // interior {1/3 (mult 1), 2/3 (mult 1)}

    const selfMerged = mergeKnotVectors(knotsA, knotsA, degree);
    recordCheck(passCount, failures, knotVectorsMatch(selfMerged, knotsA),
        "MERGE: merge(A, A) equals A", printPassing);

    // knotsB shares degree and domain but has a different interior structure: {1/3 (1), 1/2 (1)}.
    // Union at max multiplicity: {1/3 (1), 1/2 (1), 2/3 (1)}.
    const knotsB = [0, 0, 0, 0, 1 / 3, 1 / 2, 1, 1, 1, 1];
    const merged = mergeKnotVectors(knotsA, knotsB, degree);
    const expectedMerged = [0, 0, 0, 0, 1 / 3, 1 / 2, 2 / 3, 1, 1, 1, 1];
    recordCheck(passCount, failures, knotVectorsMatch(merged, expectedMerged),
        "MERGE: max-multiplicity union of two different interior structures (got " ~ merged ~ ")", printPassing);

    const insertionsA = insertionsToReach(knotsA, merged, degree);
    const insertionsB = insertionsToReach(knotsB, merged, degree);
    const refinedFromA = refineKnotVector(makeSixPointFixture(), knotsA, degree, insertionsA);
    const refinedFromB = refineKnotVector(makeSixPointFixture(), knotsB, degree, insertionsB);
    recordCheck(passCount, failures, knotVectorsMatch(refinedFromA.knots, merged),
        "MERGE: refining A up to the merge reproduces the merged knot vector exactly", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(refinedFromB.knots, merged),
        "MERGE: refining B up to the merge reproduces the merged knot vector exactly", printPassing);
}

// ============================================================================================
// REFINE-COUNT — refineSplineToControlPointCount: exact count, geometry preserved, no-op
// ============================================================================================

function runRefineToControlPointCountVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 3;
    const knots = makeClampedCubicKnots();
    const fixturePoints = makeSixPointFixture();
    const spline = { "degree" : degree, "isPeriodic" : false, "controlPoints" : fixturePoints, "knots" : knotArray(knots) };

    const refined = refineSplineToControlPointCount(spline, 10);
    recordCheck(passCount, failures, size(refined.controlPoints) == 10,
        "REFINE-COUNT: refineSplineToControlPointCount hits the exact target count", printPassing);

    const sampleParameters = makeUnitSampleParameters();
    const originalPoints = evaluateCurvePoints(fixturePoints, knots, degree, sampleParameters);
    const refinedPoints = evaluateCurvePoints(refined.controlPoints, refined.knots, degree, sampleParameters);
    var geometryPreserved = true;
    for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
    {
        if (!pointsMatch(originalPoints[sampleIndex], refinedPoints[sampleIndex]))
        {
            geometryPreserved = false;
        }
    }
    recordCheck(passCount, failures, geometryPreserved,
        "REFINE-COUNT: geometry preserved after refining to a higher control point count", printPassing);

    const noOpResult = refineSplineToControlPointCount(spline, 6);
    recordCheck(passCount, failures, size(noOpResult.controlPoints) == 6,
        "REFINE-COUNT: refining to an already-met target count is a no-op", printPassing);
}

// ============================================================================================
// SHARE — makeSplinesShareKnotVector: identical knots afterward, each curve's own geometry kept
// ============================================================================================

function runShareKnotVectorVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 3;
    const knotsA = makeClampedCubicKnots(); // interior {1/3, 2/3}
    const pointsA = makeSixPointFixture();
    const knotsB = [0, 0, 0, 0, 1 / 2, 1, 1, 1, 1]; // interior {1/2}, 5 control points
    const pointsB = makeFivePointFixture();

    const splineA = { "degree" : degree, "isPeriodic" : false, "controlPoints" : pointsA, "knots" : knotArray(knotsA) };
    const splineB = { "degree" : degree, "isPeriodic" : false, "controlPoints" : pointsB, "knots" : knotArray(knotsB) };
    const shared = makeSplinesShareKnotVector(splineA, splineB);

    recordCheck(passCount, failures, knotVectorsMatch(shared.a.knots, shared.b.knots),
        "SHARE: both outputs land on an identical knot vector", printPassing);

    const sampleParameters = makeUnitSampleParameters();
    const originalAPoints = evaluateCurvePoints(pointsA, knotsA, degree, sampleParameters);
    const sharedAPoints = evaluateCurvePoints(shared.a.controlPoints, shared.a.knots, degree, sampleParameters);
    const originalBPoints = evaluateCurvePoints(pointsB, knotsB, degree, sampleParameters);
    const sharedBPoints = evaluateCurvePoints(shared.b.controlPoints, shared.b.knots, degree, sampleParameters);

    var aPreserved = true;
    var bPreserved = true;
    for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
    {
        if (!pointsMatch(originalAPoints[sampleIndex], sharedAPoints[sampleIndex]))
        {
            aPreserved = false;
        }
        if (!pointsMatch(originalBPoints[sampleIndex], sharedBPoints[sampleIndex]))
        {
            bPreserved = false;
        }
    }
    recordCheck(passCount, failures, aPreserved,
        "SHARE: curve A's geometry is unchanged after refining onto the shared knot vector", printPassing);
    recordCheck(passCount, failures, bPreserved,
        "SHARE: curve B's geometry is unchanged after refining onto the shared knot vector", printPassing);
}

// ============================================================================================
// DECOMPOSE — decomposeIntoBezierSegments: every segment matches the parent on its sub-domain
// ============================================================================================

function runDecomposeIntoBezierSegmentsVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 3;
    const knots = makeClampedCubicKnots(); // interior {1/3, 2/3} -> 3 segments
    const fixturePoints = makeSixPointFixture();
    const spline = { "degree" : degree, "isPeriodic" : false, "controlPoints" : fixturePoints, "knots" : knotArray(knots) };

    const segments = decomposeIntoBezierSegments(spline);
    recordCheck(passCount, failures, size(segments) == 3,
        "DECOMPOSE: two interior knots split a degree-3 curve into 3 Bezier segments (got " ~ size(segments) ~ ")", printPassing);

    const bezierKnots = makeUniformKnotArray(degree, degree + 1, false);
    const localParameters = [0, 0.25, 0.5, 0.75, 1];
    var allSegmentsMatch = true;
    for (var segmentIndex = 0; segmentIndex < size(segments); segmentIndex += 1)
    {
        const segment = segments[segmentIndex];
        if (size(segment.controlPoints) != degree + 1)
        {
            allSegmentsMatch = false;
            continue;
        }

        var absoluteParameters = makeArray(size(localParameters), 0);
        for (var sampleIndex = 0; sampleIndex < size(localParameters); sampleIndex += 1)
        {
            absoluteParameters[sampleIndex] = segment.domainStart + localParameters[sampleIndex] * (segment.domainEnd - segment.domainStart);
        }

        const segmentPoints = evaluateCurvePoints(segment.controlPoints, bezierKnots, degree, localParameters);
        const parentPoints = evaluateCurvePoints(fixturePoints, knots, degree, absoluteParameters);
        for (var sampleIndex = 0; sampleIndex < size(localParameters); sampleIndex += 1)
        {
            if (!pointsMatch(segmentPoints[sampleIndex], parentPoints[sampleIndex]))
            {
                allSegmentsMatch = false;
            }
        }
    }
    recordCheck(passCount, failures, allSegmentsMatch,
        "DECOMPOSE: every Bezier segment (degree+1 control points) matches the parent curve on its own sub-domain", printPassing);
}

// ============================================================================================
// ELEVATE (vector 7) — elevateSplineDegree: geometry preserved across several degree pairs
// ============================================================================================

function runElevationCase(passCount is box, failures is box, printPassing is boolean,
        fromDegree is number, toDegree is number, knots is array, controlPoints is array)
{
    const spline = { "degree" : fromDegree, "isPeriodic" : false, "controlPoints" : controlPoints, "knots" : knotArray(knots) };
    const elevated = elevateSplineDegree(spline, toDegree);

    recordCheck(passCount, failures, elevated.degree == toDegree,
        "ELEVATE: degree " ~ fromDegree ~ " -> " ~ toDegree ~ " reaches the target degree", printPassing);

    const sampleParameters = makeUnitSampleParameters();
    const originalPoints = evaluateCurvePoints(controlPoints, knots, fromDegree, sampleParameters);
    const elevatedPoints = evaluateCurvePoints(elevated.controlPoints, elevated.knots, toDegree, sampleParameters);

    var geometryPreserved = true;
    for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
    {
        if (!pointsMatch(originalPoints[sampleIndex], elevatedPoints[sampleIndex]))
        {
            geometryPreserved = false;
        }
    }
    recordCheck(passCount, failures, geometryPreserved,
        "ELEVATE: degree " ~ fromDegree ~ " -> " ~ toDegree ~ " preserves geometry exactly", printPassing);
}

function runElevateSplineDegreeVector(passCount is box, failures is box, printPassing is boolean)
{
    // Degree 2 -> 3, no interior knots (a plain Bezier) - the single-segment path.
    runElevationCase(passCount, failures, printPassing, 2, 3, [0, 0, 0, 1, 1, 1], makeThreePointFixture());

    // Degree 2 -> 3 with two interior simple knots (three Bezier segments) - exercises the
    // decompose/elevate-each/recombine path, not just the single-segment case.
    runElevationCase(passCount, failures, printPassing, 2, 3, [0, 0, 0, 1 / 3, 2 / 3, 1, 1, 1], makeFivePointFixture());

    // Degree 3 -> 5, a larger degree jump.
    runElevationCase(passCount, failures, printPassing, 3, 5, makeClampedCubicKnots(), makeSixPointFixture());

    // A repeated interior knot (multiplicity 2, degree 3) - exercises interiorKnotRun's
    // multiplicity-aware insertion count (needs degree - multiplicity = 1 more, not 3).
    runElevationCase(passCount, failures, printPassing, 3, 5, [0, 0, 0, 0, 0.5, 0.5, 1, 1, 1, 1], makeSixPointFixture());

    const noOpSpline = { "degree" : 3, "isPeriodic" : false, "controlPoints" : makeSixPointFixture(), "knots" : knotArray(makeClampedCubicKnots()) };
    const noOpResult = elevateSplineDegree(noOpSpline, 3);
    recordCheck(passCount, failures, noOpResult.degree == 3,
        "ELEVATE: elevating to an already-met degree is a no-op", printPassing);
}

// ============================================================================================
// COMPATIBLE — makeSplinesCompatible: shared degree AND knots, each curve's geometry kept
// ============================================================================================

function runMakeSplinesCompatibleVector(passCount is box, failures is box, printPassing is boolean)
{
    const degreeA = 2;
    const knotsA = [0, 0, 0, 1, 1, 1];
    const pointsA = makeThreePointFixture();

    const degreeB = 3;
    const knotsB = makeClampedCubicKnots();
    const pointsB = makeSixPointFixture();

    const splineA = { "degree" : degreeA, "isPeriodic" : false, "controlPoints" : pointsA, "knots" : knotArray(knotsA) };
    const splineB = { "degree" : degreeB, "isPeriodic" : false, "controlPoints" : pointsB, "knots" : knotArray(knotsB) };
    const compatible = makeSplinesCompatible(splineA, splineB);

    recordCheck(passCount, failures, compatible.a.degree == degreeB && compatible.b.degree == degreeB,
        "COMPATIBLE: the lower-degree input is elevated to match the higher one", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(compatible.a.knots, compatible.b.knots),
        "COMPATIBLE: both outputs share an identical knot vector", printPassing);

    const sampleParameters = makeUnitSampleParameters();
    const originalAPoints = evaluateCurvePoints(pointsA, knotsA, degreeA, sampleParameters);
    const compatibleAPoints = evaluateCurvePoints(compatible.a.controlPoints, compatible.a.knots, degreeB, sampleParameters);
    const originalBPoints = evaluateCurvePoints(pointsB, knotsB, degreeB, sampleParameters);
    const compatibleBPoints = evaluateCurvePoints(compatible.b.controlPoints, compatible.b.knots, degreeB, sampleParameters);

    var aPreserved = true;
    var bPreserved = true;
    for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
    {
        if (!pointsMatch(originalAPoints[sampleIndex], compatibleAPoints[sampleIndex]))
        {
            aPreserved = false;
        }
        if (!pointsMatch(originalBPoints[sampleIndex], compatibleBPoints[sampleIndex]))
        {
            bPreserved = false;
        }
    }
    recordCheck(passCount, failures, aPreserved,
        "COMPATIBLE: the elevated-and-shared curve A still matches its original geometry", printPassing);
    recordCheck(passCount, failures, bPreserved,
        "COMPATIBLE: curve B still matches its original geometry after sharing the knot vector", printPassing);
}

// ============================================================================================
// PREPARE — prepareSplineForDeformation: elevate-then-refine composition smoke test
// ============================================================================================

function runPrepareSplineForDeformationVector(passCount is box, failures is box, printPassing is boolean)
{
    const degree = 2;
    const knots = [0, 0, 0, 1, 1, 1];
    const fixturePoints = makeThreePointFixture();
    const spline = { "degree" : degree, "isPeriodic" : false, "controlPoints" : fixturePoints, "knots" : knotArray(knots) };

    const prepared = prepareSplineForDeformation(spline, 3, 8);

    recordCheck(passCount, failures, prepared.degree == 3,
        "PREPARE: elevates to the target degree", printPassing);
    recordCheck(passCount, failures, size(prepared.controlPoints) == 8,
        "PREPARE: refines to exactly the target control point count (got " ~ size(prepared.controlPoints) ~ ")", printPassing);

    const sampleParameters = makeUnitSampleParameters();
    const originalPoints = evaluateCurvePoints(fixturePoints, knots, degree, sampleParameters);
    const preparedPoints = evaluateCurvePoints(prepared.controlPoints, prepared.knots, 3, sampleParameters);
    var geometryPreserved = true;
    for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
    {
        if (!pointsMatch(originalPoints[sampleIndex], preparedPoints[sampleIndex]))
        {
            geometryPreserved = false;
        }
    }
    recordCheck(passCount, failures, geometryPreserved,
        "PREPARE: elevate-then-refine preserves geometry exactly", printPassing);
}

// ============================================================================================
// Surface fixtures and helpers
// ============================================================================================

/** U degree 3 (interior {1/3, 2/3}, 6 rows), V degree 2 (interior {0.5}, 4 columns), clamped. */
function makeClampedSurfaceFixture() returns map
{
    var controlPoints = makeArray(6, 0);
    for (var rowIndex = 0; rowIndex < 6; rowIndex += 1)
    {
        var row = makeArray(4, vector(0, 0, 0) * centimeter);
        for (var columnIndex = 0; columnIndex < 4; columnIndex += 1)
        {
            row[columnIndex] = vector(rowIndex, columnIndex, rowIndex + columnIndex * 0.3) * centimeter;
        }
        controlPoints[rowIndex] = row;
    }
    return {
            "uDegree" : 3,
            "vDegree" : 2,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : [0, 0, 0, 0, 1 / 3, 2 / 3, 1, 1, 1, 1],
            "vKnots" : [0, 0, 0, 0.5, 1, 1, 1]
        };
}

/** U degree 3 (interior {0.5}, 5 rows), V degree 2 (interior {0.25, 0.75}, 5 columns), clamped
    - deliberately a DIFFERENT interior structure from makeClampedSurfaceFixture, same degrees
    and domain, for the knot-sharing vector. */
function makeAlternateClampedSurfaceFixture() returns map
{
    var controlPoints = makeArray(5, 0);
    for (var rowIndex = 0; rowIndex < 5; rowIndex += 1)
    {
        var row = makeArray(5, vector(0, 0, 0) * centimeter);
        for (var columnIndex = 0; columnIndex < 5; columnIndex += 1)
        {
            row[columnIndex] = vector(rowIndex * 1.5, columnIndex * 0.7, rowIndex - columnIndex) * centimeter;
        }
        controlPoints[rowIndex] = row;
    }
    return {
            "uDegree" : 3,
            "vDegree" : 2,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : [0, 0, 0, 0, 0.5, 1, 1, 1, 1],
            "vKnots" : [0, 0, 0, 0.25, 0.75, 1, 1, 1]
        };
}

/** U degree 2 (LOWER than the fixtures above, interior {0.5}, 4 rows), V degree 2 (Bezier, no
    interior, 3 columns), clamped - for the degree-elevation half of the compatibility vector. */
function makeLowerDegreeSurfaceFixture() returns map
{
    var controlPoints = makeArray(4, 0);
    for (var rowIndex = 0; rowIndex < 4; rowIndex += 1)
    {
        var row = makeArray(3, vector(0, 0, 0) * centimeter);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            row[columnIndex] = vector(rowIndex * 2, columnIndex, rowIndex * columnIndex) * centimeter;
        }
        controlPoints[rowIndex] = row;
    }
    return {
            "uDegree" : 2,
            "vDegree" : 2,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : [0, 0, 0, 0.5, 1, 1, 1],
            "vKnots" : [0, 0, 0, 1, 1, 1]
        };
}

/**
 * A GENUINELY U-periodic surface: a square tube. U degree 2 with 4 fundamental rows plus a
 * 2-row overlap tail (6 stored rows, uniform knots [0..8], domain [2, 6], period 4); V degree 2
 * clamped Bezier across 3 columns. isUPeriodic:true, isVPeriodic:false, so it also checks that
 * only the periodic direction gets periodic treatment.
 *
 * The overlap tail is a LITERAL copy of the first two rows, which the whole periodic design
 * rests on (P[i] == P[i + n] as VALUES). An earlier version of this fixture set every row from
 * its index, so it was flagged periodic while failing the overlap condition — harmless while
 * normalizeSurfaceDefinition clamped everything, and quietly meaningless once it started
 * preserving periodicity, since refinement tiles from the fundamental rows and would have
 * disagreed with the fixture's own tail.
 */
function makeUPeriodicSurfaceFixture() returns map
{
    // Four fundamental rows: corners of a square in XY, swept along Z by the V direction.
    const corners = [vector(2, 2, 0), vector(-2, 2, 0), vector(-2, -2, 0), vector(2, -2, 0)];
    var controlPoints = makeArray(6, 0);
    for (var rowIndex = 0; rowIndex < 6; rowIndex += 1)
    {
        const corner = corners[rowIndex % 4]; // rows 4 and 5 ARE rows 0 and 1 - the overlap
        var row = makeArray(3, vector(0, 0, 0) * centimeter);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            row[columnIndex] = (corner + vector(0, 0, 3 * columnIndex)) * centimeter;
        }
        controlPoints[rowIndex] = row;
    }
    return {
            "uDegree" : 2,
            "vDegree" : 2,
            "isRational" : false,
            "isUPeriodic" : true,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : [0, 1, 2, 3, 4, 5, 6, 7, 8],
            "vKnots" : [0, 0, 0, 1, 1, 1]
        };
}

/**
 * An OPEN counterpart to makeUPeriodicSurfaceFixture: same U and V degrees (2 and 2) and same
 * 3-column V structure, but a genuinely clamped U direction. Used for the mixed-periodicity
 * case, where the degrees must match for makeSurfacesShareKnotVectors to get past its
 * equal-degree guard and actually reach the clamping decision under test.
 */
function makeClampedOpenTubeFixture() returns map
{
    var controlPoints = makeArray(4, 0);
    for (var rowIndex = 0; rowIndex < 4; rowIndex += 1)
    {
        var row = makeArray(3, vector(0, 0, 0) * centimeter);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            row[columnIndex] = vector(rowIndex, 2 - columnIndex, rowIndex * 0.5 + columnIndex) * centimeter;
        }
        controlPoints[rowIndex] = row;
    }
    return {
            "uDegree" : 2,
            "vDegree" : 2,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : [0, 0, 0, 0.5, 1, 1, 1],
            "vKnots" : [0, 0, 0, 1, 1, 1]
        };
}

/**
 * A wrap-form periodic surface whose circular direction has BEZIER-ARC knot structure:
 * fundamental knots [0, 0.5, 0.5, 0.5] — multiplicity 3, equal to the degree, at the arc joint.
 * (The dimensions are revolve-inspired, but note this is NOT the kernel's own convention — a
 * real revolve arrives CLOSED CLAMPED, see makeClosedClampedCylinderFixture. This fixture is a
 * legitimate wrap-form object in its own right, and the arc-joint multiplicity pattern is what
 * the seam-alignment machinery under test here has to survive; the same pattern is what a
 * converted closed-clamped revolve carries after normalization.)
 *
 * This structure is the whole point of the fixture, and it breaks assumptions the uniform
 * multiplicity-1 fixtures cannot:
 *   - Fundamental knot indices 1, 2 and 3 ALL have value 0.5, so every nonzero seam shift lands
 *     on the C0 arc joint. The seam is effectively immovable by re-windowing alone.
 *   - A seam at a multiplicity-3 knot is C0, which the kernel rejects outright
 *     (PERIODIC_BSPLINESURFACE_NOT_SMOOTH) — it is fine as an INTERIOR knot but not as a seam.
 *   - The weights are non-uniform, so control point correspondence has to be right for the
 *     rational geometry to survive blending. G1 across a C0 knot needs P4-P3 parallel to P3-P2,
 *     which is a nonlinear condition and therefore only preserved by a linear blend when both
 *     surfaces already agree there.
 */
function makeBezierArcPeriodicSurfaceFixture() returns map
{
    return makeBezierArcPeriodicSurfaceFixture(1);
}

/** Same structure at a scaled radius, for building a SECOND surface that shares the first's knot
    vector by construction — which is what alignPeriodicSurfaceSeams requires of its inputs. */
function makeBezierArcPeriodicSurfaceFixture(radiusScale is number) returns map
{
    // Two U rows (degree 1, clamped): a cone from radius 0.1034 to radius 0.0486.
    const radii = [0.1034 * radiusScale, 0.0486 * radiusScale];
    const heights = [0.3976, 0.4942];
    var controlPoints = makeArray(2, 0);
    var weights = makeArray(2, 0);
    for (var rowIndex = 0; rowIndex < 2; rowIndex += 1)
    {
        const radius = radii[rowIndex];
        const height = heights[rowIndex];
        // Four fundamental points: a square circumscribing the circle, centred at (0, radius).
        const fundamental = [
                vector(-radius, 0, height),
                vector(-radius, 2 * radius, height),
                vector(radius, 2 * radius, height),
                vector(radius, 0, height)
            ];
        const fundamentalWeights = [1, 2 / 3, 2 / 3, 1];
        var row = makeArray(7, vector(0, 0, 0) * meter);
        var weightRow = makeArray(7, 1);
        for (var columnIndex = 0; columnIndex < 7; columnIndex += 1)
        {
            row[columnIndex] = fundamental[columnIndex % 4] * meter; // columns 4-6 ARE columns 0-2
            weightRow[columnIndex] = fundamentalWeights[columnIndex % 4];
        }
        controlPoints[rowIndex] = row;
        weights[rowIndex] = weightRow;
    }
    return {
            "uDegree" : 1,
            "vDegree" : 3,
            "isRational" : true,
            "isUPeriodic" : false,
            "isVPeriodic" : true,
            "controlPoints" : controlPoints,
            "weights" : weights,
            "uKnots" : [0, 0, 1, 1],
            "vKnots" : [-0.5, -0.5, -0.5, 0, 0.5, 0.5, 0.5, 1, 1.5, 1.5, 1.5]
        };
}

/**
 * Multiplicity of the knot AT THE SEAM of a stored periodic direction — the domain start,
 * knots[degree]. This is the number that decides whether the kernel will accept the surface at
 * all: multiplicity equal to the degree means a C0 seam, and a periodic surface with a C0 seam is
 * rejected. Interior knots may reach that multiplicity freely; the seam may not.
 */
function seamMultiplicity(knots is array, degree is number) returns number
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

/** True if the control grid satisfies the overlap condition DOWN COLUMNS (the U direction):
    row i equals row n + i, elementwise, for i = 0..uDegree-1. */
function columnOverlapConditionHolds(controlPoints is array, uDegree is number) returns boolean
{
    const n = size(controlPoints) - uDegree;
    for (var overlapIndex = 0; overlapIndex < uDegree; overlapIndex += 1)
    {
        const firstRow = controlPoints[overlapIndex];
        const wrappedRow = controlPoints[n + overlapIndex];
        if (size(firstRow) != size(wrappedRow))
        {
            return false;
        }
        for (var columnIndex = 0; columnIndex < size(firstRow); columnIndex += 1)
        {
            if (!pointsMatch(firstRow[columnIndex], wrappedRow[columnIndex]))
            {
                return false;
            }
        }
    }
    return true;
}

/** Evaluate a surface's RAW arrays literally, ignoring its own periodic flags - the surface
    analog of evaluateCurvePoints always constructing with isPeriodic:false. Valid because
    Boehm insertion/evaluation is a purely local array operation, independent of the periodic
    flag (see normalizeSplineDefinition's own doc comment for the argument). */
function evaluateSurfaceLiterally(surface is map, uParameter is number, vParameter is number) returns Vector
{
    var literalSurface = surface;
    literalSurface.isUPeriodic = false;
    literalSurface.isVPeriodic = false;
    return evaluateBSplineSurfacePoint(literalSurface, uParameter, vParameter);
}

/** True if surfaceA and surfaceB evaluate to matching points at every (u, v) in the cross
    product of uParameters and vParameters. */
function surfacesMatchOnGrid(surfaceA is map, surfaceB is map, uParameters is array, vParameters is array) returns boolean
{
    for (var uParameter in uParameters)
    {
        for (var vParameter in vParameters)
        {
            if (!pointsMatch(evaluateBSplineSurfacePoint(surfaceA, uParameter, vParameter), evaluateBSplineSurfacePoint(surfaceB, uParameter, vParameter)))
            {
                return false;
            }
        }
    }
    return true;
}

// ============================================================================================
// SURFACE-NORM — normalizeSurfaceDefinition: force-rational, periodicity PRESERVED per direction
// ============================================================================================

function runSurfaceNormalizationVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = makeUPeriodicSurfaceFixture();
    const normalized = normalizeSurfaceDefinition(fixture);

    recordCheck(passCount, failures, normalized.isRational == true,
        "SURFACE-NORM: a non-rational surface becomes isRational:true", printPassing);
    recordCheck(passCount, failures, normalized.isUPeriodic == true,
        "SURFACE-NORM: the U-periodic direction stays isUPeriodic:true (preserved, not clamped)", printPassing);
    recordCheck(passCount, failures, normalized.isVPeriodic == false,
        "SURFACE-NORM: V was already non-periodic and stays that way", printPassing);
    recordCheck(passCount, failures, columnOverlapConditionHolds(normalized.controlPoints, normalized.uDegree),
        "SURFACE-NORM: the U overlap condition survives normalization", printPassing);
    recordCheck(passCount, failures, size(normalized.controlPoints) == 6 && size(normalized.controlPoints[0]) == 3,
        "SURFACE-NORM: an already-stored periodic form is left at its own dimensions", printPassing);

    const uSamples = [2, 3.5, 4.5, 6]; // U domain [2, 6], matching the curve NORM vector
    const vSamples = [0, 0.5, 1];
    var geometryPreserved = true;
    for (var uParameter in uSamples)
    {
        for (var vParameter in vSamples)
        {
            const literalPoint = evaluateSurfaceLiterally(fixture, uParameter, vParameter);
            const normalizedPoint = evaluateBSplineSurfacePoint(normalized, uParameter, vParameter);
            if (!pointsMatch(literalPoint, normalizedPoint))
            {
                geometryPreserved = false;
            }
        }
    }
    recordCheck(passCount, failures, geometryPreserved,
        "SURFACE-NORM: normalizing a periodic surface reproduces its own geometry on its domain", printPassing);
}

// ============================================================================================
// SURFACE-PERIODIC — the periodic direction survives refinement, elevation, and knot sharing.
// Every check pairs an OVERLAP CONDITION assertion (down columns, since U is the periodic
// direction here) with a geometry assertion: a result can preserve one and lose the other, and
// losing the overlap is the failure that would put a seam in a tweened cylinder.
// ============================================================================================

function runSurfacePeriodicVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const fixture = makeUPeriodicSurfaceFixture();
        const uSamples = [2, 2.8, 3.5, 4.4, 5.2, 6];
        const vSamples = [0, 0.5, 1];

        // --- Refinement in the periodic direction ---
        const refined = refineSurfaceToControlPointCounts(fixture, 9, 3);
        recordCheck(passCount, failures, refined.isUPeriodic == true,
            "SURFACE-PERIODIC: refinement leaves the surface U-periodic", printPassing);
        recordCheck(passCount, failures, size(refined.controlPoints) == 9,
            "SURFACE-PERIODIC: refinement hits the exact stored U row count (got " ~ size(refined.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, columnOverlapConditionHolds(refined.controlPoints, refined.uDegree),
            "SURFACE-PERIODIC: the U overlap condition holds after refinement", printPassing);
        recordCheck(passCount, failures, surfacesMatchOnGrid(normalizeSurfaceDefinition(fixture), refined, uSamples, vSamples),
            "SURFACE-PERIODIC: geometry is unchanged by refining the periodic direction", printPassing);

        // --- Elevation in the periodic direction ---
        const elevated = elevateSurfaceDegrees(fixture, 4, 2);
        recordCheck(passCount, failures, elevated.isUPeriodic == true && elevated.uDegree == 4,
            "SURFACE-PERIODIC: elevation reaches U degree 4 and stays U-periodic", printPassing);
        recordCheck(passCount, failures, columnOverlapConditionHolds(elevated.controlPoints, elevated.uDegree),
            "SURFACE-PERIODIC: the U overlap condition holds after elevation", printPassing);
        recordCheck(passCount, failures, surfacesMatchOnGrid(normalizeSurfaceDefinition(fixture), elevated, uSamples, vSamples),
            "SURFACE-PERIODIC: geometry is unchanged by elevating the periodic direction", printPassing);

        // --- Sharing knot vectors between two periodic surfaces ---
        // The second surface is the same tube refined to a different U structure, so the merge
        // has genuinely different fundamental knots to reconcile rather than a no-op.
        const otherFixture = refineSurfaceToControlPointCounts(fixture, 7, 3);
        const shared = makeSurfacesShareKnotVectors(fixture, otherFixture);
        recordCheck(passCount, failures, shared.a.isUPeriodic == true && shared.b.isUPeriodic == true,
            "SURFACE-PERIODIC: both shared surfaces are still U-periodic", printPassing);
        recordCheck(passCount, failures, knotVectorsMatch(shared.a.uKnots, shared.b.uKnots),
            "SURFACE-PERIODIC: both shared surfaces land on an identical U knot vector", printPassing);
        recordCheck(passCount, failures, size(shared.a.controlPoints) == size(shared.b.controlPoints),
            "SURFACE-PERIODIC: both shared surfaces land on the same row count", printPassing);
        recordCheck(passCount, failures, columnOverlapConditionHolds(shared.a.controlPoints, shared.a.uDegree),
            "SURFACE-PERIODIC: surface A's U overlap condition holds after sharing", printPassing);
        recordCheck(passCount, failures, columnOverlapConditionHolds(shared.b.controlPoints, shared.b.uDegree),
            "SURFACE-PERIODIC: surface B's U overlap condition holds after sharing", printPassing);

        // Sharing remaps onto a canonical domain, so compare at the same PROPORTIONAL position:
        // the shared pair lives on U in [0, 1] while the fixture lives on [2, 6].
        const sharedUSamples = [0, 0.2, 0.375, 0.6, 0.8, 1];
        recordCheck(passCount, failures, surfacesMatchOnGrid(shared.a, shared.b, sharedUSamples, vSamples),
            "SURFACE-PERIODIC: the two shared surfaces agree pointwise (they are the same tube)", printPassing);

        // --- Mixed periodicity falls back to clamping, honestly and only then ---
        // A genuinely clamped surface, not the periodic fixture with its flag flipped: flipping
        // the flag would leave unclamped knots behind, which the clamped merge path correctly
        // refuses. Matching degrees and V structure, so U periodicity is the only difference.
        const mixed = makeSurfacesShareKnotVectors(fixture, makeClampedOpenTubeFixture());
        recordCheck(passCount, failures, mixed.a.isUPeriodic == false && mixed.b.isUPeriodic == false,
            "SURFACE-PERIODIC: a periodic/open U pair is clamped on BOTH sides, not silently mismatched", printPassing);
        recordCheck(passCount, failures, knotVectorsMatch(mixed.a.uKnots, mixed.b.uKnots),
            "SURFACE-PERIODIC: the clamped mixed pair still lands on an identical U knot vector", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "SURFACE-PERIODIC: threw an error: " ~ error, printPassing);
    }
}

/**
 * rewindowPeriodicSurfaceDirection re-cuts which period-length window a periodic direction stores.
 * It is a pure reindex of grid and knot intervals, so the three things that must hold are: the
 * grid does not grow, the overlap condition survives, and the surface evaluates identically at
 * ABSOLUTE parameters (re-windowing relabels which window is stored, it does not move geometry or
 * shift parameter values). Both directions are covered - U reindexes rows, V reindexes within
 * every row, and those are separate code paths.
 */
function runSurfaceRewindowVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        // --- U direction (rows) ---
        const tube = normalizeSurfaceDefinition(makeUPeriodicSurfaceFixture()); // n = 4, domain [2, 6], period 4
        const rewoundU = rewindowPeriodicSurfaceDirection(tube, true, 1);

        recordCheck(passCount, failures, size(rewoundU.controlPoints) == size(tube.controlPoints),
            "SURFACE-REWINDOW: re-windowing U adds no rows (got " ~ size(rewoundU.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, rewoundU.isUPeriodic == true,
            "SURFACE-REWINDOW: the U direction is still periodic afterwards", printPassing);
        recordCheck(passCount, failures, columnOverlapConditionHolds(rewoundU.controlPoints, rewoundU.uDegree),
            "SURFACE-REWINDOW: the U overlap condition holds after re-windowing", printPassing);
        recordCheck(passCount, failures, abs(rewoundU.uKnots[rewoundU.uDegree] - 3) <= KNOT_PARAMETER_TOLERANCE,
            "SURFACE-REWINDOW: the U domain now starts at fundamental knot 1 (parameter 3)", printPassing);

        // Overlap of the two domains: original [2, 6], re-wound [3, 7].
        const sharedUSamples = [3, 3.7, 4.5, 5.2, 6];
        const vSamples = [0, 0.5, 1];
        recordCheck(passCount, failures, surfacesMatchOnGrid(tube, rewoundU, sharedUSamples, vSamples),
            "SURFACE-REWINDOW: U geometry is identical at shared absolute parameters", printPassing);

        // Re-windowing by a full period is the identity; by n + 1 it matches by 1 (indices wrap).
        const rewoundFullPeriod = rewindowPeriodicSurfaceDirection(tube, true, 4);
        recordCheck(passCount, failures, surfacesMatchOnGrid(tube, rewoundFullPeriod, [2.5, 3.5, 4.5, 5.5], vSamples),
            "SURFACE-REWINDOW: re-windowing U by a whole period changes nothing", printPassing);
        const rewoundWrapped = rewindowPeriodicSurfaceDirection(tube, true, 5);
        recordCheck(passCount, failures, knotVectorsMatch(rewoundWrapped.uKnots, rewoundU.uKnots),
            "SURFACE-REWINDOW: a start index past one period wraps (index 5 matches index 1)", printPassing);

        // --- V direction (columns) ---
        // Same tube transposed, so V is the periodic direction and the column code path runs.
        var transposedGrid = makeArray(3, 0);
        for (var rowIndex = 0; rowIndex < 3; rowIndex += 1)
        {
            var row = makeArray(6, vector(0, 0, 0) * centimeter);
            for (var columnIndex = 0; columnIndex < 6; columnIndex += 1)
            {
                row[columnIndex] = tube.controlPoints[columnIndex][rowIndex];
            }
            transposedGrid[rowIndex] = row;
        }
        const vTube = normalizeSurfaceDefinition({
                    "uDegree" : 2,
                    "vDegree" : 2,
                    "isRational" : false,
                    "isUPeriodic" : false,
                    "isVPeriodic" : true,
                    "controlPoints" : transposedGrid,
                    "uKnots" : [0, 0, 0, 1, 1, 1],
                    "vKnots" : [0, 1, 2, 3, 4, 5, 6, 7, 8]
                });
        const rewoundV = rewindowPeriodicSurfaceDirection(vTube, false, 1);

        recordCheck(passCount, failures, size(rewoundV.controlPoints[0]) == size(vTube.controlPoints[0]),
            "SURFACE-REWINDOW: re-windowing V adds no columns (got " ~ size(rewoundV.controlPoints[0]) ~ ")", printPassing);
        recordCheck(passCount, failures, rewoundV.isVPeriodic == true,
            "SURFACE-REWINDOW: the V direction is still periodic afterwards", printPassing);
        recordCheck(passCount, failures, abs(rewoundV.vKnots[rewoundV.vDegree] - 3) <= KNOT_PARAMETER_TOLERANCE,
            "SURFACE-REWINDOW: the V domain now starts at fundamental knot 1 (parameter 3)", printPassing);
        recordCheck(passCount, failures, surfacesMatchOnGrid(vTube, rewoundV, [0, 0.5, 1], sharedUSamples),
            "SURFACE-REWINDOW: V geometry is identical at shared absolute parameters", printPassing);

        // The V overlap condition, checked across rows rather than down columns.
        var vOverlapHolds = true;
        const vFundamentalCount = size(rewoundV.controlPoints[0]) - rewoundV.vDegree;
        for (var rowIndex = 0; rowIndex < size(rewoundV.controlPoints); rowIndex += 1)
        {
            for (var overlapIndex = 0; overlapIndex < rewoundV.vDegree; overlapIndex += 1)
            {
                if (!pointsMatch(rewoundV.controlPoints[rowIndex][overlapIndex],
                        rewoundV.controlPoints[rowIndex][vFundamentalCount + overlapIndex]))
                {
                    vOverlapHolds = false;
                }
            }
        }
        recordCheck(passCount, failures, vOverlapHolds,
            "SURFACE-REWINDOW: the V overlap condition holds after re-windowing", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "SURFACE-REWINDOW: threw an error: " ~ error, printPassing);
    }
}

/**
 * The Bezier-arc seam problem, written as a test BEFORE the fix exists — so it is expected to
 * report failures until two-sided seam alignment lands. Every other periodic fixture in this file
 * uses uniform multiplicity-1 knots, which is exactly why they all passed while real revolves
 * failed in Onshape; this vector exists so that class of bug cannot hide again.
 *
 * The invariant under test: whatever alignment is performed, BOTH surfaces must come out with a
 * multiplicity-1 seam. Re-windowing one surface alone cannot satisfy that here, because every
 * nonzero seam index of this structure lands on the multiplicity-3 arc joint. The fix has to move
 * BOTH seams — inserting mid-span knots so that a relative offset can be split between them, with
 * each landing somewhere smooth.
 */
function runBezierArcSeamVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const cone = normalizeSurfaceDefinition(makeBezierArcPeriodicSurfaceFixture());

        // Sanity: the fixture really does have the structure this vector is about.
        recordCheck(passCount, failures, seamMultiplicity(cone.vKnots, cone.vDegree) == 1,
            "BEZIER-SEAM: the fixture starts with a multiplicity-1 seam (got " ~
            seamMultiplicity(cone.vKnots, cone.vDegree) ~ ")", printPassing);
        const fundamentalVKnots = subArray(cone.vKnots, cone.vDegree, cone.vDegree + size(cone.controlPoints[0]) - cone.vDegree);
        recordCheck(passCount, failures, size(distinctValueRunsForTest(fundamentalVKnots)) == 2,
            "BEZIER-SEAM: the fixture's period has exactly two distinct knot values (arc-joint structure)", printPassing);

        // Every nonzero re-window index lands on the multiplicity-3 arc joint. This documents WHY
        // one-sided re-windowing cannot solve the alignment, rather than leaving it to be
        // rediscovered from a kernel error message.
        var everyNonzeroShiftBreaksTheSeam = true;
        for (var startIndex = 1; startIndex < 4; startIndex += 1)
        {
            const rewound = rewindowPeriodicSurfaceDirection(cone, false, startIndex);
            if (seamMultiplicity(rewound.vKnots, rewound.vDegree) == 1)
            {
                everyNonzeroShiftBreaksTheSeam = false;
            }
        }
        recordCheck(passCount, failures, everyNonzeroShiftBreaksTheSeam,
            "BEZIER-SEAM: as expected, EVERY nonzero one-sided seam shift lands on the C0 arc joint", printPassing);

        // The alignment case itself. Surface B is a second cone at a different radius built on the
        // SAME knot structure, which is what alignPeriodicSurfaceSeams requires - it aligns two
        // surfaces that already share a knot vector, which is the state makeSurfacesCompatible
        // leaves them in.
        const otherCone = normalizeSurfaceDefinition(makeBezierArcPeriodicSurfaceFixture(0.35));
        const aligned = alignPeriodicSurfaceSeams(cone, otherCone, false, 3);

        recordCheck(passCount, failures, seamMultiplicity(aligned.a.vKnots, aligned.a.vDegree) == 1,
            "BEZIER-SEAM: surface A comes out with a multiplicity-1 seam (got " ~
            seamMultiplicity(aligned.a.vKnots, aligned.a.vDegree) ~ ")", printPassing);
        recordCheck(passCount, failures, seamMultiplicity(aligned.b.vKnots, aligned.b.vDegree) == 1,
            "BEZIER-SEAM: surface B comes out with a multiplicity-1 seam (got " ~
            seamMultiplicity(aligned.b.vKnots, aligned.b.vDegree) ~ ")", printPassing);
        recordCheck(passCount, failures, knotVectorsMatch(aligned.a.vKnots, aligned.b.vKnots),
            "BEZIER-SEAM: both outputs still share an identical V knot vector", printPassing);
        recordCheck(passCount, failures, aligned.a.isVPeriodic == true && aligned.b.isVPeriodic == true,
            "BEZIER-SEAM: both outputs are still V-periodic", printPassing);

        // The relative offset actually asked for must be realized. Fundamental knots are
        // [0, 0.5, 0.5, 0.5], so a shift of 3 means an offset of 0.5 - and the two seams have to
        // differ by exactly that, which is what makes the correspondence right.
        recordCheck(passCount, failures, abs((aligned.seamB - aligned.seamA) - 0.5) <= KNOT_PARAMETER_TOLERANCE,
            "BEZIER-SEAM: the two seams differ by exactly the requested offset (got " ~
            (aligned.seamB - aligned.seamA) ~ ")", printPassing);
        recordCheck(passCount, failures, seamMultiplicity(cone.vKnots, cone.vDegree) == 1,
            "BEZIER-SEAM: neither seam landed on the multiplicity-3 arc joint at 0.5", printPassing);

        // GEOMETRY: re-windowing and re-sharing must not move either surface. Both results live on
        // a remapped [0, 1] V domain, so surface A at v corresponds to the original at
        // seamA + v * period.
        //
        // That sum has to be WRAPPED back into the original's domain before evaluating.
        // evaluateBSplineSurfacePoint deliberately does not wrap (see its own doc comment), and a
        // seam near the end of the period pushes seam + fraction straight past the domain end -
        // seamB is 0.75 here, so a fraction of 0.45 asks for 1.20. Wrapping is exact for a periodic
        // direction, and unlike shrinking the sample range it keeps coverage across the whole
        // period, which is where a seam bug would actually show up.
        const uSamples = [0, 0.5, 1];
        const vFractions = [0, 0.15, 0.3, 0.45, 0.6, 0.85];
        var aPreserved = true;
        var bPreserved = true;
        for (var uParameter in uSamples)
        {
            for (var vFraction in vFractions)
            {
                const originalA = wrapIntoDomain(aligned.seamA + vFraction, 0, 1);
                const originalB = wrapIntoDomain(aligned.seamB + vFraction, 0, 1);
                if (!pointsMatch(evaluateBSplineSurfacePoint(aligned.a, uParameter, vFraction),
                        evaluateBSplineSurfacePoint(cone, uParameter, originalA)))
                {
                    aPreserved = false;
                }
                if (!pointsMatch(evaluateBSplineSurfacePoint(aligned.b, uParameter, vFraction),
                        evaluateBSplineSurfacePoint(otherCone, uParameter, originalB)))
                {
                    bPreserved = false;
                }
            }
        }
        recordCheck(passCount, failures, aPreserved,
            "BEZIER-SEAM: surface A's geometry is unmoved by the seam alignment", printPassing);
        recordCheck(passCount, failures, bPreserved,
            "BEZIER-SEAM: surface B's geometry is unmoved by the seam alignment", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "BEZIER-SEAM: threw an error: " ~ error, printPassing);
    }
}

/** Fold a parameter back into [domainStart, domainStart + period). Uses floor division rather
    than `%`, which returns a NEGATIVE remainder in FeatureScript for a negative left operand. */
function wrapIntoDomain(value is number, domainStart is number, period is number) returns number
{
    const offset = value - domainStart;
    return domainStart + (offset - floor(offset / period) * period);
}

/**
 * The REAL kernel periodic convention, CONFIRMED by a full raw control-grid dump from a live
 * revolve (2026-08-08) — the only acceptable source for a fixture like this, after an earlier
 * fixture built from post-processing output enshrined a convention that does not exist (a
 * "degree-wide overlap with clamped knots", disproven by convex hull: those points could never
 * have traced a full circle). A revolve's circular direction arrives CLOSED CLAMPED: the full
 * circle as two rational cubic Bezier arcs, ordinary clamped knots, LAST control point
 * coinciding with the FIRST (one coincident point), weights [1, 1/3, 1/3, 1, 1/3, 1/3, 1], and
 * isPeriodic as metadata for the C1 closure. Numbers below are the logged cylinder verbatim:
 * radius 0.0352842599367892 m.
 */
function makeClosedClampedCircleFixture() returns map
{
    const r = 0.0352842599367892;
    return {
            "degree" : 3,
            "isPeriodic" : true,
            "isRational" : true,
            "controlPoints" : [
                    vector(r, 0, 0) * meter,
                    vector(r, 2 * r, 0) * meter,
                    vector(-r, 2 * r, 0) * meter,
                    vector(-r, 0, 0) * meter,
                    vector(-r, -2 * r, 0) * meter,
                    vector(r, -2 * r, 0) * meter,
                    vector(r, 0, 0) * meter
                ],
            "weights" : [1, 1 / 3, 1 / 3, 1, 1 / 3, 1 / 3, 1],
            "knots" : [0, 0, 0, 0, 0.5, 0.5, 0.5, 1, 1, 1, 1],
            "radius" : r // the on-circle evaluator anchor in runClosedClampedVector needs it
        };
}

/** Rational curve evaluation at in-domain parameters, through the MODULE's basis machinery.

    Deliberately NOT std evaluateSpline. The kernel builtin behind it IGNORES WEIGHTS -
    measured live 2026-08-08: on the rational circle fixture it returned the UNWEIGHTED
    control polygon's curve, matching an unweighted de Boor replication to the last printed
    digit ((0.6875r, 1.125r) at u = 0.125 where the true circle point is (0.8r, 0.6r)). The
    KERNEL-WEIGHTS probe vector re-measures this every run against all three candidate
    readings (rational / unweighted / premultiplied-homogeneous), on both isPeriodic flags -
    added when the claim was challenged, since the original evidence was two call shapes from
    one diagnostic dump. The gap is invisible for the non-rational fixtures elsewhere in this
    tester and fatal for rational comparisons across a value-changing operation: a rational
    curve and its knot-inserted refinement are the same TRUE curve but different unweighted
    polygons, so kernel comparison reports a false mismatch - which burned two live runs before
    the three-way diagnostic dump isolated it. The evaluator here is anchored to ground truth
    by the on-circle checks (runClosedClampedVector's circle, KERNEL-WEIGHTS' quarter circle)
    before any form comparison is trusted. */
function evaluateRationalCurvePoints(controlPoints is array, weights is array, knots is array, degree is number, parameters is array) returns array
{
    var points = makeArray(size(parameters));
    for (var parameterIndex = 0; parameterIndex < size(parameters); parameterIndex += 1)
    {
        points[parameterIndex] = evaluateModuleBasisCurvePoint(controlPoints, weights, knots, degree, parameters[parameterIndex]);
    }
    return points;
}

/** One rational evaluation through the module's exported bSplineBasisValues /
    findEvaluationSpanIndex - kernel-free, so clamped and wrap forms evaluate identically.
    Homogeneous accumulation; parameter must be inside the knot domain. */
function evaluateModuleBasisCurvePoint(controlPoints is array, weights is array, knots is array, degree is number, parameter is number) returns Vector
{
    const spanIndex = findEvaluationSpanIndex(knots, degree, parameter);
    const basisValues = bSplineBasisValues(knots, degree, spanIndex, parameter);
    var weightedSum = undefined;
    var weightSum = 0;
    for (var basisIndex = 0; basisIndex <= degree; basisIndex += 1)
    {
        const pointIndex = spanIndex - degree + basisIndex;
        const termWeight = basisValues[basisIndex] * weights[pointIndex];
        weightSum = weightSum + termWeight;
        weightedSum = weightedSum == undefined ? termWeight * controlPoints[pointIndex]
            : weightedSum + termWeight * controlPoints[pointIndex];
    }
    return weightedSum / weightSum;
}

function runClosedClampedVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const fixture = makeClosedClampedCircleFixture();
        const normalized = normalizeSplineDefinition(fixture);

        // n = N - 1 = 6 distinct points; stored wrap form = n + degree = 9.
        recordCheck(passCount, failures, size(normalized.controlPoints) == 9,
            "CLOSED-CLAMPED: conversion yields n + degree = 9 stored points (got " ~ size(normalized.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, normalized.isPeriodic == true,
            "CLOSED-CLAMPED: the curve stays periodic through conversion", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(normalized.controlPoints, 3),
            "CLOSED-CLAMPED: the overlap condition holds after conversion", printPassing);
        recordCheck(passCount, failures, seamMultiplicity(normalized.knots, 3) == 3,
            "CLOSED-CLAMPED: the converted seam knot carries multiplicity degree (got " ~
            seamMultiplicity(normalized.knots, 3) ~ ")", printPassing);

        var wrapPadded = true;
        const n = size(normalized.controlPoints) - 3;
        const period = normalized.knots[3 + n] - normalized.knots[3];
        for (var knotIndex = 0; knotIndex + n < size(normalized.knots); knotIndex += 1)
        {
            if (abs(normalized.knots[knotIndex + n] - (normalized.knots[knotIndex] + period)) > KNOT_PARAMETER_TOLERANCE)
            {
                wrapPadded = false;
            }
        }
        recordCheck(passCount, failures, wrapPadded,
            "CLOSED-CLAMPED: converted knots are genuinely wrap-padded", printPassing);

        // The modular gather stored[j] = P[(j + 1 - degree) mod n] puts the seam point P0 at
        // stored index degree - 1 = 2.
        recordCheck(passCount, failures, pointsMatch(normalized.controlPoints[2], fixture.controlPoints[0]),
            "CLOSED-CLAMPED: the seam control point lands at stored index degree - 1", printPassing);

        // GEOMETRY IDENTITY: the raw arrays evaluated literally as a clamped curve ARE the true
        // circle; the converted wrap form must agree at the same absolute parameters.
        const parameters = [0, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875];
        const literalPoints = evaluateRationalCurvePoints(fixture.controlPoints, fixture.weights, fixture.knots, 3, parameters);

        // EVALUATOR ANCHOR, before any form comparison is trusted: the fixture arrays are the
        // kernel's own raw output for an exact circle of radius r about the origin, so every
        // literal evaluation must land on that circle. This is the analytic ground truth that
        // replaces kernel evaluateSpline as the oracle (see evaluateRationalCurvePoints for why
        // the kernel is disqualified: it silently drops the weights). Tolerance is the
        // first-order band |d(r^2)| = 2 r dr for a 1e-9 m radial deviation.
        var literalOnCircle = true;
        for (var sampleIndex = 0; sampleIndex < size(parameters); sampleIndex += 1)
        {
            if (abs(squaredNorm(literalPoints[sampleIndex]) - fixture.radius * fixture.radius * meter * meter) >
                2 * fixture.radius * 1e-9 * meter * meter)
            {
                literalOnCircle = false;
            }
        }
        recordCheck(passCount, failures, literalOnCircle,
            "CLOSED-CLAMPED: evaluator anchor - literal evaluation lands on the exact circle", printPassing);

        const convertedPoints = evaluateRationalCurvePoints(normalized.controlPoints, normalized.weights, normalized.knots, 3, parameters);
        var conversionPreservesGeometry = true;
        for (var sampleIndex = 0; sampleIndex < size(parameters); sampleIndex += 1)
        {
            if (!pointsMatch(literalPoints[sampleIndex], convertedPoints[sampleIndex]))
            {
                conversionPreservesGeometry = false;
            }
        }
        recordCheck(passCount, failures, conversionPreservesGeometry,
            "CLOSED-CLAMPED: conversion preserves the circle exactly at absolute parameters", printPassing);

        // A REAL insertion - the operation this convention could previously only refuse - now
        // succeeds and stays exact.
        const homogeneousPoints = combinePointsAndWeights(normalized.controlPoints, normalized.weights);
        const refined = refinePeriodicPoints(homogeneousPoints, normalized.knots, 3, [0.25]);
        const separated = separatePointsAndWeights(refined.controlPoints);
        recordCheck(passCount, failures, overlapConditionHolds(separated.points, 3),
            "CLOSED-CLAMPED: the overlap condition holds after a real insertion", printPassing);

        // The refined WRAP form must still be the circle. Compared through the anchored
        // module-basis evaluator - clamped and wrap forms evaluate identically there, so this
        // isolates the refinement itself. (An earlier version of this check compared through
        // kernel evaluateSpline and failed two live runs in a row on arrays that were exact to
        // machine epsilon - the kernel had silently dropped the weights, and a rational curve's
        // knot-inserted refinement has a DIFFERENT unweighted polygon than the original.)
        const refinedWrapPoints = evaluateRationalCurvePoints(separated.points, separated.weights, refined.knots, 3, parameters);
        var insertionPreservesGeometry = true;
        for (var sampleIndex = 0; sampleIndex < size(parameters); sampleIndex += 1)
        {
            if (!pointsMatch(literalPoints[sampleIndex], refinedWrapPoints[sampleIndex]))
            {
                insertionPreservesGeometry = false;
            }
        }
        recordCheck(passCount, failures, insertionPreservesGeometry,
            "CLOSED-CLAMPED: a real insertion on the converted circle preserves the geometry exactly", printPassing);

        // And the refined curve must EMIT back to a valid closed clamped form carrying the same
        // circle - refinement and emission composed, the exact chain every live feature output
        // takes.
        const emittedRefined = toClosedClampedPeriodicForm({
                    "degree" : 3,
                    "isPeriodic" : true,
                    "isRational" : true,
                    "controlPoints" : separated.points,
                    "weights" : separated.weights,
                    "knots" : refined.knots
                });
        const refinedFundamentalCount = size(separated.points) - 3;
        recordCheck(passCount, failures,
            size(emittedRefined.controlPoints) == refinedFundamentalCount + 1 &&
            pointsMatch(emittedRefined.controlPoints[0], emittedRefined.controlPoints[refinedFundamentalCount]),
            "CLOSED-CLAMPED: the refined curve emits as closed clamped (n + 1 points, last == first)", printPassing);
        const emittedRefinedPoints = evaluateRationalCurvePoints(emittedRefined.controlPoints, emittedRefined.weights, emittedRefined.knots, 3, parameters);
        var emissionPreservesGeometry = true;
        for (var sampleIndex = 0; sampleIndex < size(parameters); sampleIndex += 1)
        {
            if (!pointsMatch(literalPoints[sampleIndex], emittedRefinedPoints[sampleIndex]))
            {
                emissionPreservesGeometry = false;
            }
        }
        if (!insertionPreservesGeometry || !emissionPreservesGeometry)
        {
            // Failure-only diagnostics: dump every intermediate array verbatim plus the
            // three-way evaluation so one run pins which stage diverges.
            println("[CLOSED-CLAMPED DIAG] converted knots: " ~ normalized.knots);
            for (var pointIndex = 0; pointIndex < size(normalized.controlPoints); pointIndex += 1)
            {
                println("[CLOSED-CLAMPED DIAG] converted[" ~ pointIndex ~ "] " ~ normalized.controlPoints[pointIndex] ~ " w=" ~ normalized.weights[pointIndex]);
            }
            println("[CLOSED-CLAMPED DIAG] refined knots: " ~ refined.knots);
            for (var pointIndex = 0; pointIndex < size(separated.points); pointIndex += 1)
            {
                println("[CLOSED-CLAMPED DIAG] refined[" ~ pointIndex ~ "] " ~ separated.points[pointIndex] ~ " w=" ~ separated.weights[pointIndex]);
            }
            println("[CLOSED-CLAMPED DIAG] emitted knots: " ~ emittedRefined.knots);
            for (var pointIndex = 0; pointIndex < size(emittedRefined.controlPoints); pointIndex += 1)
            {
                println("[CLOSED-CLAMPED DIAG] emitted[" ~ pointIndex ~ "] " ~ emittedRefined.controlPoints[pointIndex] ~ " w=" ~ emittedRefined.weights[pointIndex]);
            }
            for (var sampleIndex = 0; sampleIndex < size(parameters); sampleIndex += 1)
            {
                println("[CLOSED-CLAMPED DIAG] u=" ~ parameters[sampleIndex] ~
                    " literal=" ~ literalPoints[sampleIndex] ~
                    " refinedWrap=" ~ refinedWrapPoints[sampleIndex] ~
                    " emitted=" ~ emittedRefinedPoints[sampleIndex]);
            }
        }
        recordCheck(passCount, failures, emissionPreservesGeometry,
            "CLOSED-CLAMPED: the emitted refined curve carries the same circle exactly", printPassing);

        // ROUND TRIP: the emission form must reproduce the kernel's own input arrays exactly.
        const emitted = toClosedClampedPeriodicForm(normalized);
        recordCheck(passCount, failures, knotVectorsMatch(emitted.knots, fixture.knots),
            "CLOSED-CLAMPED: emission reproduces the raw clamped knots", printPassing);
        var roundTripMatches = size(emitted.controlPoints) == size(fixture.controlPoints);
        if (roundTripMatches)
        {
            for (var pointIndex = 0; pointIndex < size(fixture.controlPoints); pointIndex += 1)
            {
                if (!pointsMatch(emitted.controlPoints[pointIndex], fixture.controlPoints[pointIndex]) ||
                    abs(emitted.weights[pointIndex] - fixture.weights[pointIndex]) > 1e-9)
                {
                    roundTripMatches = false;
                }
            }
        }
        recordCheck(passCount, failures, roundTripMatches,
            "CLOSED-CLAMPED: emission reproduces the raw control points and weights exactly", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "CLOSED-CLAMPED: threw an error: " ~ error, printPassing);
    }

    // Honesty preserved: a periodic curve whose knots are NEITHER wrap-padded NOR clamped must
    // still refuse rather than guess.
    try
    {
        normalizeSplineDefinition({
                    "degree" : 2,
                    "isPeriodic" : true,
                    "controlPoints" : [vector(0, 0, 0) * meter, vector(1, 0, 0) * meter, vector(1, 1, 0) * meter, vector(0, 1, 0) * meter],
                    "knots" : [0, 1, 2, 3.5, 4, 5, 7]
                });
        recordCheck(passCount, failures, false,
            "CLOSED-CLAMPED: unrecognized periodic knots correctly refuse (they did not - this is now unguarded)", printPassing);
    }
    catch
    {
        recordCheck(passCount, failures, true,
            "CLOSED-CLAMPED: unrecognized periodic knots still refuse rather than guess", printPassing);
    }
}

/** The logged CYLINDER as a surface, verbatim: the closed-clamped circle swept between the two
    logged heights. uDegree 3 U-periodic (closed clamped), vDegree 1 clamped, 2 columns. */
function makeClosedClampedCylinderFixture() returns map
{
    const circle = makeClosedClampedCircleFixture();
    const heights = [0.4039749626640555, 0.4606121628299273];
    var controlPoints = makeArray(7, 0);
    var weights = makeArray(7, 0);
    for (var rowIndex = 0; rowIndex < 7; rowIndex += 1)
    {
        controlPoints[rowIndex] = [
                circle.controlPoints[rowIndex] + vector(0, 0, heights[0]) * meter,
                circle.controlPoints[rowIndex] + vector(0, 0, heights[1]) * meter
            ];
        weights[rowIndex] = [circle.weights[rowIndex], circle.weights[rowIndex]];
    }
    return {
            "uDegree" : 3,
            "vDegree" : 1,
            "isRational" : true,
            "isUPeriodic" : true,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "weights" : weights,
            "uKnots" : circle.knots,
            "vKnots" : [0, 0, 1, 1]
        };
}

function runClosedClampedSurfaceVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const fixture = makeClosedClampedCylinderFixture();
        const normalized = normalizeSurfaceDefinition(fixture);

        recordCheck(passCount, failures, size(normalized.controlPoints) == 9,
            "CLOSED-CLAMPED-SURFACE: conversion yields 9 stored rows (got " ~ size(normalized.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, normalized.isUPeriodic == true,
            "CLOSED-CLAMPED-SURFACE: the surface stays U-periodic through conversion", printPassing);
        recordCheck(passCount, failures, columnOverlapConditionHolds(normalized.controlPoints, 3),
            "CLOSED-CLAMPED-SURFACE: the U overlap condition holds after conversion", printPassing);

        // Geometry: the raw arrays evaluated literally ARE the cylinder; the converted form
        // must agree pointwise on a grid.
        const uSamples = [0, 0.2, 0.45, 0.7, 0.9];
        const vSamples = [0, 0.5, 1];
        recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, normalized, uSamples, vSamples),
            "CLOSED-CLAMPED-SURFACE: conversion preserves the cylinder exactly on a grid", printPassing);

        // The self-pair through full compatibility - the first live failure case - must
        // reproduce the same geometry.
        const compatible = makeSurfacesCompatible(fixture, fixture);
        recordCheck(passCount, failures, knotVectorsMatch(compatible.a.uKnots, compatible.b.uKnots),
            "CLOSED-CLAMPED-SURFACE: the self-pair lands on identical U knots", printPassing);
        recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, compatible.a, uSamples, vSamples),
            "CLOSED-CLAMPED-SURFACE: compatibility on a self-pair preserves the geometry exactly", printPassing);

        // A REAL U insertion - previously the refusal case - now succeeds through the operator
        // path and stays exact.
        const refined = refineSurfaceToControlPointCounts(fixture, 10, 2);
        recordCheck(passCount, failures, size(refined.controlPoints) == 10,
            "CLOSED-CLAMPED-SURFACE: a real U insertion hits the target row count (got " ~ size(refined.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, refined.isUPeriodic == true,
            "CLOSED-CLAMPED-SURFACE: the refined surface is still U-periodic", printPassing);
        recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, refined, uSamples, vSamples),
            "CLOSED-CLAMPED-SURFACE: a real U insertion preserves the geometry exactly", printPassing);

        // Emission round trip: back to the kernel's own arrays.
        const emitted = toClosedClampedSurfaceDirection(normalized, true);
        recordCheck(passCount, failures, knotVectorsMatch(emitted.uKnots, fixture.uKnots),
            "CLOSED-CLAMPED-SURFACE: emission reproduces the raw clamped U knots", printPassing);
        var roundTripMatches = size(emitted.controlPoints) == size(fixture.controlPoints);
        if (roundTripMatches)
        {
            for (var rowIndex = 0; rowIndex < size(fixture.controlPoints); rowIndex += 1)
            {
                for (var columnIndex = 0; columnIndex < size(fixture.controlPoints[0]); columnIndex += 1)
                {
                    if (!pointsMatch(emitted.controlPoints[rowIndex][columnIndex], fixture.controlPoints[rowIndex][columnIndex]) ||
                        abs(emitted.weights[rowIndex][columnIndex] - fixture.weights[rowIndex][columnIndex]) > 1e-9)
                    {
                        roundTripMatches = false;
                    }
                }
            }
        }
        recordCheck(passCount, failures, roundTripMatches,
            "CLOSED-CLAMPED-SURFACE: emission reproduces the raw control grid and weights exactly", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "CLOSED-CLAMPED-SURFACE: threw an error: " ~ error, printPassing);
    }
}

// ============================================================================================
// REVERSE-CANONICAL — reversing a multiplicity-degree seam structure stays canonical and
// SHAREABLE. Regression for the live cone-to-cylinder failure (2026-08-08): reversing one
// revolve direction left its seam multiplicity run SPLIT across the domain boundary (reflection
// mirrors the run to the far end), and the knot merge — which compares runs by literal value —
// then demanded phantom seam-image insertions above the multiplicity cap. The pre-existing
// REVERSE vectors could never catch this: their smooth uniform fixtures have multiplicity-1
// seams, which cannot split.
// ============================================================================================

function runReverseCanonicalVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const original = normalizeSplineDefinition(makeClosedClampedCircleFixture());
        const reversed = reverseSpline(original);

        recordCheck(passCount, failures, seamKnotMultiplicity(reversed.knots, 3) == 3,
            "REVERSE-CANONICAL: the reversed circle's seam run stays contiguous at multiplicity degree (got " ~
            seamKnotMultiplicity(reversed.knots, 3) ~ ")", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(reversed.controlPoints, 3),
            "REVERSE-CANONICAL: the overlap condition holds after reversal", printPassing);

        // reverseSpline's contract is C_rev(t) == C(-t); canonicalization may shift the stored
        // window by a whole period, which changes nothing about that identity mod period.
        const originalDomain = knotDomain(original.knots, 3);
        const originalPeriod = originalDomain.end - originalDomain.start;
        const reversedDomain = knotDomain(reversed.knots, 3);
        const reversedPeriod = reversedDomain.end - reversedDomain.start;
        var reversalPreservesGeometry = true;
        for (var sampleIndex = 0; sampleIndex < 8; sampleIndex += 1)
        {
            const reversedParameter = reversedDomain.start + reversedPeriod * sampleIndex / 8;
            const reversedPoint = evaluateModuleBasisCurvePoint(reversed.controlPoints, reversed.weights, reversed.knots, 3, reversedParameter);
            const originalPoint = evaluateModuleBasisCurvePoint(original.controlPoints, original.weights, original.knots, 3,
                    wrapIntoDomain(-reversedParameter, originalDomain.start, originalPeriod));
            if (!pointsMatch(reversedPoint, originalPoint))
            {
                reversalPreservesGeometry = false;
            }
        }
        recordCheck(passCount, failures, reversalPreservesGeometry,
            "REVERSE-CANONICAL: the reversed circle is the same circle traversed backwards, exactly", printPassing);

        // THE REGRESSION: sharing the reversed and forward forms threw the multiplicity-cap
        // error before canonicalization. It must succeed and land both on identical knots.
        const shared = makeSplinesShareKnotVector(original, reversed);
        recordCheck(passCount, failures, knotVectorsMatch(shared.a.knots, shared.b.knots),
            "REVERSE-CANONICAL: forward and reversed forms share a knot vector after the merge", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "REVERSE-CANONICAL: threw an error: " ~ error, printPassing);
    }
}

function runReverseCanonicalSurfaceVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const original = normalizeSurfaceDefinition(makeClosedClampedCylinderFixture());
        const reversed = reverseSurfaceDirection(original, true);

        recordCheck(passCount, failures, seamKnotMultiplicity(reversed.uKnots, 3) == 3,
            "REVERSE-CANONICAL-SURFACE: the reversed U seam run stays contiguous at multiplicity degree (got " ~
            seamKnotMultiplicity(reversed.uKnots, 3) ~ ")", printPassing);
        recordCheck(passCount, failures, reversed.isUPeriodic == true && size(reversed.controlPoints) == 9,
            "REVERSE-CANONICAL-SURFACE: the reversed surface keeps its stored periodic U structure", printPassing);

        // reverseSurfaceDirection's contract is S_rev(u, v) == S(uStart + uEnd - u, v), the
        // reflection about the original's own U domain (mod period after canonicalization).
        const originalUDomain = knotDomain(original.uKnots, 3);
        const originalUPeriod = originalUDomain.end - originalUDomain.start;
        const reversedUDomain = knotDomain(reversed.uKnots, 3);
        const reversedUPeriod = reversedUDomain.end - reversedUDomain.start;
        var reversalPreservesGeometry = true;
        for (var sampleIndex = 0; sampleIndex < 6; sampleIndex += 1)
        {
            const reversedU = reversedUDomain.start + reversedUPeriod * sampleIndex / 6;
            const originalU = wrapIntoDomain(originalUDomain.start + originalUDomain.end - reversedU,
                    originalUDomain.start, originalUPeriod);
            for (var vParameter in [0, 0.5, 1])
            {
                if (!pointsMatch(evaluateBSplineSurfacePoint(reversed, reversedU, vParameter),
                        evaluateBSplineSurfacePoint(original, originalU, vParameter)))
                {
                    reversalPreservesGeometry = false;
                }
            }
        }
        recordCheck(passCount, failures, reversalPreservesGeometry,
            "REVERSE-CANONICAL-SURFACE: reversal preserves the cylinder exactly", printPassing);

        // THE REGRESSION, surface level - the exact composite that failed live on the
        // cone-to-cylinder pair.
        const shared = makeSurfacesShareKnotVectors(original, reversed);
        recordCheck(passCount, failures, knotVectorsMatch(shared.a.uKnots, shared.b.uKnots),
            "REVERSE-CANONICAL-SURFACE: forward and reversed surfaces share U knots after the merge", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "REVERSE-CANONICAL-SURFACE: threw an error: " ~ error, printPassing);
    }
}

// ============================================================================================
// KERNEL-WEIGHTS — direct probe of what std evaluateSpline actually computes for a RATIONAL
// curve. The weights-ignored claim rests on the CLOSED-CLAMPED diagnostic dump (16-digit match
// to the unweighted polygon value at u = 0.125/0.25), which measured exactly two call shapes;
// when challenged ("a periodic curve with one weighted point tweened fine" - true but
// nondiscriminating: weights only ever fed the alignment choice, and geometry CREATION
// respects weights), this vector was added to settle it in isolation. It discriminates THREE
// readings of the same arrays: standard rational (sum NwP / sum Nw), weights-ignored
// (sum NP), and premultiplied-homogeneous (the kernel expecting P to already be w-scaled:
// sum NP / sum Nw). Nothing in the std library calls evaluateSpline AT ALL (verified by grep
// of the full mirror), so no std consumer exists that would ever have caught a rational gap.
// ============================================================================================

/** All three candidate readings of (controlPoints, weights) at `parameter`, via the module's
    basis machinery. Whichever one the kernel's own result lands on is what the kernel
    computes. */
function threeWayEvaluation(controlPoints is array, weights is array, knots is array, degree is number, parameter is number) returns map
{
    const spanIndex = findEvaluationSpanIndex(knots, degree, parameter);
    const basisValues = bSplineBasisValues(knots, degree, spanIndex, parameter);
    var rationalNumerator = undefined;
    var unweightedSum = undefined;
    var weightSum = 0;
    for (var basisIndex = 0; basisIndex <= degree; basisIndex += 1)
    {
        const pointIndex = spanIndex - degree + basisIndex;
        const rationalTerm = (basisValues[basisIndex] * weights[pointIndex]) * controlPoints[pointIndex];
        const unweightedTerm = basisValues[basisIndex] * controlPoints[pointIndex];
        weightSum = weightSum + basisValues[basisIndex] * weights[pointIndex];
        rationalNumerator = rationalNumerator == undefined ? rationalTerm : rationalNumerator + rationalTerm;
        unweightedSum = unweightedSum == undefined ? unweightedTerm : unweightedSum + unweightedTerm;
    }
    return {
            "rational" : rationalNumerator / weightSum,
            "unweighted" : unweightedSum,
            "premultiplied" : unweightedSum / weightSum
        };
}

function evaluateCurvePointViaKernel(controlPoints is array, weights is array, knots is array, degree is number, isPeriodic is boolean, parameter is number) returns Vector
{
    const curve = bSplineCurve({
                "degree" : degree,
                "isPeriodic" : isPeriodic,
                "controlPoints" : controlPoints,
                "weights" : weights,
                "knots" : knotArray(knots)
            });
    return evaluateSpline({ "spline" : curve, "parameters" : [parameter] })[0][0];
}

function printKernelProbeLine(controlPoints is array, weights is array, knots is array, degree is number, isPeriodic is boolean, parameter is number)
{
    const kernelPoint = evaluateCurvePointViaKernel(controlPoints, weights, knots, degree, isPeriodic, parameter);
    const interpretations = threeWayEvaluation(controlPoints, weights, knots, degree, parameter);
    println("[KERNEL-WEIGHTS PROBE]   u=" ~ parameter ~
        " |kernel-rational|=" ~ sqrt(squaredNorm(kernelPoint - interpretations.rational)) ~
        " |kernel-unweighted|=" ~ sqrt(squaredNorm(kernelPoint - interpretations.unweighted)) ~
        " |kernel-premultiplied|=" ~ sqrt(squaredNorm(kernelPoint - interpretations.premultiplied)));
}

function runKernelWeightsProbeVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        // Analytic fixture where the weights matter heavily and the truth needs no oracle at
        // all: the exact quarter circle as a degree-2 rational Bezier, weight sqrt(2)/2 on the
        // corner. Every point of the TRUE curve satisfies |P| == R; the weights-ignored curve
        // is the parabola through the same control points (~6% off-circle at midspan).
        const quarterR = 0.05;
        const quarterPoints = [vector(quarterR, 0, 0) * meter, vector(quarterR, quarterR, 0) * meter, vector(0, quarterR, 0) * meter];
        const quarterWeights = [1, sqrt(2) / 2, 1];
        const quarterKnots = [0, 0, 0, 1, 1, 1];

        // Pass/fail HALF - module coverage only: the module's rational evaluation must trace
        // the analytic circle. A second, weights-heavy analytic anchor for the evaluator.
        var moduleOnCircle = true;
        for (var parameter in [0.25, 0.5, 0.75])
        {
            const interpretations = threeWayEvaluation(quarterPoints, quarterWeights, quarterKnots, 2, parameter);
            if (abs(squaredNorm(interpretations.rational) - quarterR * quarterR * meter * meter) >
                2 * quarterR * 1e-9 * meter * meter)
            {
                moduleOnCircle = false;
            }
        }
        recordCheck(passCount, failures, moduleOnCircle,
            "KERNEL-WEIGHTS: the module's rational evaluation traces the analytic quarter circle exactly", printPassing);

        // Probe HALF - printed every run, never pass/fail, because it measures KERNEL
        // behavior rather than module correctness. Whichever delta column is ~0 is what the
        // kernel computes. Covers isPeriodic false AND true, including the kernel's own
        // closed-clamped convention, which the original diagnostic never measured with the
        // true flag.
        println("[KERNEL-WEIGHTS PROBE] quarter circle (degree 2, w=[1, 0.7071, 1]), isPeriodic false:");
        for (var parameter in [0.25, 0.5, 0.75])
        {
            printKernelProbeLine(quarterPoints, quarterWeights, quarterKnots, 2, false, parameter);
        }
        const circle = makeClosedClampedCircleFixture();
        println("[KERNEL-WEIGHTS PROBE] closed-clamped circle (degree 3, w thirds), isPeriodic false:");
        for (var parameter in [0.125, 0.25])
        {
            printKernelProbeLine(circle.controlPoints, circle.weights, circle.knots, 3, false, parameter);
        }
        println("[KERNEL-WEIGHTS PROBE] closed-clamped circle, isPeriodic true (the kernel's own periodic convention):");
        for (var parameter in [0.125, 0.25])
        {
            printKernelProbeLine(circle.controlPoints, circle.weights, circle.knots, 3, true, parameter);
        }
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "KERNEL-WEIGHTS: threw an error: " ~ error, printPassing);
    }
}

/** Local copy of the module's run-counting used only to describe a fixture's structure. */
function distinctValueRunsForTest(values is array) returns array
{
    var distinctValues = [];
    for (var valueIndex = 0; valueIndex < size(values); valueIndex += 1)
    {
        var alreadySeen = false;
        for (var seenIndex = 0; seenIndex < size(distinctValues); seenIndex += 1)
        {
            if (abs(distinctValues[seenIndex] - values[valueIndex]) <= KNOT_PARAMETER_TOLERANCE)
            {
                alreadySeen = true;
            }
        }
        if (!alreadySeen)
        {
            distinctValues = append(distinctValues, values[valueIndex]);
        }
    }
    return distinctValues;
}

// ============================================================================================
// SURFACE-REFINE-COUNT — refineSurfaceToControlPointCounts: exact counts, geometry preserved
// ============================================================================================

function runSurfaceRefineToControlPointCountsVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = makeClampedSurfaceFixture();
    const refined = refineSurfaceToControlPointCounts(fixture, 10, 7);

    recordCheck(passCount, failures, size(refined.controlPoints) == 10,
        "SURFACE-REFINE-COUNT: hits the exact target U control point count (got " ~ size(refined.controlPoints) ~ ")", printPassing);
    recordCheck(passCount, failures, size(refined.controlPoints[0]) == 7,
        "SURFACE-REFINE-COUNT: hits the exact target V control point count (got " ~ size(refined.controlPoints[0]) ~ ")", printPassing);

    const uSamples = [0, 0.25, 0.5, 0.75, 1];
    const vSamples = [0, 0.5, 1];
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, refined, uSamples, vSamples),
        "SURFACE-REFINE-COUNT: geometry preserved after refining both directions", printPassing);
}

// ============================================================================================
// SURFACE-ELEVATE — elevateSurfaceDegrees: both directions reach target degree, geometry kept
// ============================================================================================

function runSurfaceElevateDegreesVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = makeClampedSurfaceFixture();
    const elevated = elevateSurfaceDegrees(fixture, 4, 3);

    recordCheck(passCount, failures, elevated.uDegree == 4 && elevated.vDegree == 3,
        "SURFACE-ELEVATE: both directions reach their target degree", printPassing);

    const uSamples = [0, 0.25, 0.5, 0.75, 1];
    const vSamples = [0, 0.5, 1];
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, elevated, uSamples, vSamples),
        "SURFACE-ELEVATE: geometry preserved after elevating both directions", printPassing);
}

// ============================================================================================
// SURFACE-SHARE — makeSurfacesShareKnotVectors: identical knots afterward, geometry kept
// ============================================================================================

function runSurfaceShareKnotVectorsVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixtureA = makeClampedSurfaceFixture();
    const fixtureB = makeAlternateClampedSurfaceFixture();
    const shared = makeSurfacesShareKnotVectors(fixtureA, fixtureB);

    recordCheck(passCount, failures, knotVectorsMatch(shared.a.uKnots, shared.b.uKnots),
        "SURFACE-SHARE: both outputs share an identical U knot vector", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(shared.a.vKnots, shared.b.vKnots),
        "SURFACE-SHARE: both outputs share an identical V knot vector", printPassing);

    const uSamples = [0, 0.25, 0.5, 0.75, 1];
    const vSamples = [0, 0.5, 1];
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixtureA, shared.a, uSamples, vSamples),
        "SURFACE-SHARE: surface A's geometry is unchanged after sharing the knot vector", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixtureB, shared.b, uSamples, vSamples),
        "SURFACE-SHARE: surface B's geometry is unchanged after sharing the knot vector", printPassing);
}

// ============================================================================================
// SURFACE-COMPATIBLE — makeSurfacesCompatible: shared degree AND knots, geometry kept
// ============================================================================================

function runSurfaceMakeSurfacesCompatibleVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixtureA = makeClampedSurfaceFixture(); // uDegree 3
    const fixtureB = makeLowerDegreeSurfaceFixture(); // uDegree 2 - the lower one
    const compatible = makeSurfacesCompatible(fixtureA, fixtureB);

    recordCheck(passCount, failures, compatible.a.uDegree == 3 && compatible.b.uDegree == 3,
        "SURFACE-COMPATIBLE: the lower U degree input is elevated to match the higher one", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(compatible.a.uKnots, compatible.b.uKnots),
        "SURFACE-COMPATIBLE: both outputs share an identical U knot vector", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(compatible.a.vKnots, compatible.b.vKnots),
        "SURFACE-COMPATIBLE: both outputs share an identical V knot vector", printPassing);

    const uSamples = [0, 0.25, 0.5, 0.75, 1];
    const vSamples = [0, 0.5, 1];
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixtureA, compatible.a, uSamples, vSamples),
        "SURFACE-COMPATIBLE: surface A's geometry is unchanged after becoming compatible", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixtureB, compatible.b, uSamples, vSamples),
        "SURFACE-COMPATIBLE: surface B's geometry is unchanged after elevating and sharing", printPassing);
}

// ============================================================================================
// SURFACE-PREPARE / SUB-SURFACE / SURFACE-BEZIER / SPAN-DENSITY — the last four entry points,
// closed together because they are exactly what the deformation pipeline's step 2 calls
// (spec section 9.1). Shared helpers first.
// ============================================================================================

/** Wrap one patch from decomposeSurfaceIntoBezierPatches as a standalone surface definition:
    clamped knots of multiplicity degree + 1 at each end of its own rectangle. */
function bezierPatchAsSurface(patch is map) returns map
{
    var uKnots = makeArray(2 * (patch.uDegree + 1), patch.uDomainStart);
    for (var index = patch.uDegree + 1; index < size(uKnots); index += 1)
    {
        uKnots[index] = patch.uDomainEnd;
    }
    var vKnots = makeArray(2 * (patch.vDegree + 1), patch.vDomainStart);
    for (var index = patch.vDegree + 1; index < size(vKnots); index += 1)
    {
        vKnots[index] = patch.vDomainEnd;
    }
    return {
            "uDegree" : patch.uDegree,
            "vDegree" : patch.vDegree,
            "isRational" : true,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : patch.controlPoints,
            "weights" : patch.weights,
            "uKnots" : uKnots,
            "vKnots" : vKnots
        };
}

/**
 * Count distinct knot VALUES strictly inside (cellStart, cellEnd), computed INDEPENDENTLY of the
 * module rather than by calling its own counter — this is the number the density contract is
 * stated in, so a shared helper would let one bug satisfy both sides.
 *
 * Periodic directions are counted over the infinite tiling {fundamental + k * period}, which is
 * the true knot set of a closed direction and a strict superset of the two-period window the
 * module happens to build. A cell straddling the seam therefore gets counted the same way whether
 * or not the module folded it correctly.
 */
function distinctKnotsStrictlyInside(knots is array, degree is number, isPeriodic is boolean, cellStart is number, cellEnd is number) returns number
{
    const domain = knotDomain(knots, degree);
    const period = domain.end - domain.start;
    const cycleLimit = isPeriodic ? 2 : 0;
    var seen = [];
    for (var knotIndex = 0; knotIndex < size(knots); knotIndex += 1)
    {
        for (var cycle = -cycleLimit; cycle <= cycleLimit; cycle += 1)
        {
            const value = knots[knotIndex] + cycle * period;
            if (value > cellStart + KNOT_PARAMETER_TOLERANCE && value < cellEnd - KNOT_PARAMETER_TOLERANCE)
            {
                var alreadySeen = false;
                for (var seenValue in seen)
                {
                    if (abs(seenValue - value) <= KNOT_PARAMETER_TOLERANCE)
                    {
                        alreadySeen = true;
                    }
                }
                if (!alreadySeen)
                {
                    seen = append(seen, value);
                }
            }
        }
    }
    return size(seen);
}

// ============================================================================================
// SURFACE-PREPARE — prepareSurfaceForDeformation: elevate THEN refine, and neither step shrinks
// anything. The order is the point: elevating a 3-segment U direction to degree 4 already lands
// on 13 control points, so a target below that must be left alone rather than "achieved" by
// refining first and elevating a bigger net afterwards.
// ============================================================================================

function runSurfacePrepareForDeformationVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = makeClampedSurfaceFixture(); // uDegree 3 / 6 rows / interior {1/3, 2/3}; vDegree 2 / 4 columns / interior {0.5}
    const uSamples = [0, 0.25, 0.5, 0.75, 1];
    const vSamples = [0, 0.5, 1];

    // Elevation alone: U 3 segments -> 3 * 4 + 1 = 13 rows, V 2 segments -> 2 * 3 + 1 = 7 columns.
    const elevatedOnly = prepareSurfaceForDeformation(fixture, 4, 3, 5, 5);
    recordCheck(passCount, failures, elevatedOnly.uDegree == 4 && elevatedOnly.vDegree == 3,
        "SURFACE-PREPARE: both directions reach the target degree", printPassing);
    recordCheck(passCount, failures, size(elevatedOnly.controlPoints) == 13 && size(elevatedOnly.controlPoints[0]) == 7,
        "SURFACE-PREPARE: a control point target already met by elevation refines no further (got " ~
        size(elevatedOnly.controlPoints) ~ " x " ~ size(elevatedOnly.controlPoints[0]) ~ ", expected 13 x 7)", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, elevatedOnly, uSamples, vSamples),
        "SURFACE-PREPARE: geometry preserved through elevation", printPassing);

    const prepared = prepareSurfaceForDeformation(fixture, 4, 3, 16, 10);
    recordCheck(passCount, failures, size(prepared.controlPoints) == 16 && size(prepared.controlPoints[0]) == 10,
        "SURFACE-PREPARE: hits the exact target counts when they exceed the elevated ones (got " ~
        size(prepared.controlPoints) ~ " x " ~ size(prepared.controlPoints[0]) ~ ")", printPassing);
    recordCheck(passCount, failures, prepared.uDegree == 4 && prepared.vDegree == 3,
        "SURFACE-PREPARE: refinement does not disturb the elevated degrees", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, prepared, uSamples, vSamples),
        "SURFACE-PREPARE: geometry preserved through elevation AND refinement", printPassing);

    const untouched = prepareSurfaceForDeformation(fixture, 3, 2, 6, 4);
    recordCheck(passCount, failures, untouched.uDegree == 3 && untouched.vDegree == 2 &&
        size(untouched.controlPoints) == 6 && size(untouched.controlPoints[0]) == 4,
        "SURFACE-PREPARE: a request already satisfied changes nothing", printPassing);

    // Periodic input must come back periodic — the deformation pipeline hands whole revolved
    // faces to this function, and a silently clamped cylinder is the failure mode section 2.3
    // exists to prevent.
    const periodicFixture = makeUPeriodicSurfaceFixture();
    const preparedPeriodic = prepareSurfaceForDeformation(periodicFixture, 3, 2, 14, 6);
    recordCheck(passCount, failures, preparedPeriodic.isUPeriodic == true,
        "SURFACE-PREPARE: a U-periodic input stays U-periodic through elevate + refine", printPassing);
    recordCheck(passCount, failures, columnOverlapConditionHolds(preparedPeriodic.controlPoints, preparedPeriodic.uDegree),
        "SURFACE-PREPARE: the U overlap condition survives elevate + refine", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(periodicFixture, preparedPeriodic, [2, 3.5, 4.5, 6], [0, 0.5, 1]),
        "SURFACE-PREPARE: periodic geometry preserved through elevate + refine", printPassing);
}

// ============================================================================================
// SUB-SURFACE — extractSubSurface: the piece reproduces the parent across its own rectangle,
// exactly, with clamped knot vectors on the requested domain.
// ============================================================================================

function runExtractSubSurfaceVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = makeClampedSurfaceFixture();

    // A rectangle whose bounds are existing interior knots and which contains none: one Bezier
    // patch per direction, so the counts are hand-checkable (uDegree + 1 by vDegree + 1).
    const bezierPiece = extractSubSurface(fixture, 1 / 3, 2 / 3, 0, 0.5);
    const bezierUDomain = knotDomain(bezierPiece.uKnots, bezierPiece.uDegree);
    const bezierVDomain = knotDomain(bezierPiece.vKnots, bezierPiece.vDegree);
    recordCheck(passCount, failures, abs(bezierUDomain.start - 1 / 3) <= KNOT_PARAMETER_TOLERANCE &&
        abs(bezierUDomain.end - 2 / 3) <= KNOT_PARAMETER_TOLERANCE &&
        abs(bezierVDomain.start) <= KNOT_PARAMETER_TOLERANCE && abs(bezierVDomain.end - 0.5) <= KNOT_PARAMETER_TOLERANCE,
        "SUB-SURFACE: the piece's domain is exactly the requested rectangle", printPassing);
    recordCheck(passCount, failures, isClampedKnotArray(bezierPiece.uKnots, bezierPiece.uDegree) &&
        isClampedKnotArray(bezierPiece.vKnots, bezierPiece.vDegree),
        "SUB-SURFACE: both of the piece's knot vectors are clamped", printPassing);
    recordCheck(passCount, failures, size(bezierPiece.controlPoints) == 4 && size(bezierPiece.controlPoints[0]) == 3,
        "SUB-SURFACE: a knot-to-knot rectangle with no interior knots is one Bezier patch (got " ~
        size(bezierPiece.controlPoints) ~ " x " ~ size(bezierPiece.controlPoints[0]) ~ ", expected 4 x 3)", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, bezierPiece, [1 / 3, 0.5, 2 / 3], [0, 0.25, 0.5]),
        "SUB-SURFACE: the piece reproduces the parent across its rectangle", printPassing);

    // A rectangle whose bounds are NOT knots and which straddles interior knots — the case that
    // actually exercises boundary insertion on both sides.
    const straddlingPiece = extractSubSurface(fixture, 0.25, 1, 0.25, 1);
    recordCheck(passCount, failures, size(straddlingPiece.controlPoints) == 6 && size(straddlingPiece.controlPoints[0]) == 4,
        "SUB-SURFACE: a range straddling interior knots keeps them (got " ~ size(straddlingPiece.controlPoints) ~
        " x " ~ size(straddlingPiece.controlPoints[0]) ~ ", expected 6 x 4)", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, straddlingPiece, [0.25, 0.5, 0.75, 1], [0.25, 0.6, 1]),
        "SUB-SURFACE: geometry preserved for a range whose bounds are not knots", printPassing);

    // Periodic parent. A narrowed closed direction genuinely becomes an open patch; a request for
    // the WHOLE domain must not spend the closed representation to say so.
    const periodicFixture = makeUPeriodicSurfaceFixture(); // U periodic, domain [2, 6]
    const narrowed = extractSubSurface(periodicFixture, 3, 5, 0, 1);
    recordCheck(passCount, failures, narrowed.isUPeriodic == false,
        "SUB-SURFACE: narrowing a periodic direction returns an open patch, flagged honestly", printPassing);
    recordCheck(passCount, failures, isClampedKnotArray(narrowed.uKnots, narrowed.uDegree),
        "SUB-SURFACE: the narrowed periodic direction comes back clamped", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(periodicFixture, narrowed, [3, 3.5, 4, 4.5, 5], [0, 0.5, 1]),
        "SUB-SURFACE: the piece of a periodic parent reproduces it across the extracted range", printPassing);

    const wholeDomain = extractSubSurface(periodicFixture, 2, 6, 0, 1);
    recordCheck(passCount, failures, wholeDomain.isUPeriodic == true,
        "SUB-SURFACE: requesting the whole domain leaves a periodic direction periodic", printPassing);
    recordCheck(passCount, failures, columnOverlapConditionHolds(wholeDomain.controlPoints, wholeDomain.uDegree),
        "SUB-SURFACE: the untouched periodic direction keeps its overlap condition", printPassing);
}

// ============================================================================================
// SURFACE-BEZIER — decomposeSurfaceIntoBezierPatches: the patch GRID tiles the parent, and every
// patch reproduces it across its own rectangle. Includes a periodic parent, whose closed
// direction is deliberately spent (a Bezier patch is open by definition) — the check is that the
// union still reproduces the closed surface, seam included.
// ============================================================================================

function runDecomposeSurfaceIntoBezierPatchesVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = makeClampedSurfaceFixture(); // U interior {1/3, 2/3} -> 3 segments; V interior {0.5} -> 2 segments
    const patches = decomposeSurfaceIntoBezierPatches(fixture);

    recordCheck(passCount, failures, size(patches) == 3 && size(patches[0]) == 2,
        "SURFACE-BEZIER: the patch grid is (distinct U interior knots + 1) x (distinct V interior knots + 1) (got " ~
        size(patches) ~ " x " ~ size(patches[0]) ~ ", expected 3 x 2)", printPassing);

    var everyPatchIsBezier = true;
    var domainsTile = true;
    var patchGeometryMatches = true;
    for (var uSegment = 0; uSegment < size(patches); uSegment += 1)
    {
        for (var vSegment = 0; vSegment < size(patches[uSegment]); vSegment += 1)
        {
            const patch = patches[uSegment][vSegment];
            if (size(patch.controlPoints) != patch.uDegree + 1 || size(patch.controlPoints[0]) != patch.vDegree + 1)
            {
                everyPatchIsBezier = false;
            }
            // Adjacent patches must share a boundary exactly, and the outermost bounds must be
            // the parent's own domain — otherwise the patches are individually fine and
            // collectively not a decomposition.
            if (uSegment > 0 && abs(patch.uDomainStart - patches[uSegment - 1][vSegment].uDomainEnd) > KNOT_PARAMETER_TOLERANCE)
            {
                domainsTile = false;
            }
            if (vSegment > 0 && abs(patch.vDomainStart - patches[uSegment][vSegment - 1].vDomainEnd) > KNOT_PARAMETER_TOLERANCE)
            {
                domainsTile = false;
            }

            const patchSurface = bezierPatchAsSurface(patch);
            for (var uFraction in [0, 0.3, 0.7, 1])
            {
                for (var vFraction in [0, 0.5, 1])
                {
                    const uParameter = patch.uDomainStart + uFraction * (patch.uDomainEnd - patch.uDomainStart);
                    const vParameter = patch.vDomainStart + vFraction * (patch.vDomainEnd - patch.vDomainStart);
                    if (!pointsMatch(evaluateBSplineSurfacePoint(patchSurface, uParameter, vParameter),
                            evaluateBSplineSurfacePoint(fixture, uParameter, vParameter)))
                    {
                        patchGeometryMatches = false;
                    }
                }
            }
        }
    }
    recordCheck(passCount, failures, everyPatchIsBezier,
        "SURFACE-BEZIER: every patch is exactly (uDegree + 1) x (vDegree + 1) control points", printPassing);
    recordCheck(passCount, failures, domainsTile,
        "SURFACE-BEZIER: adjacent patch domains meet exactly, so the grid tiles the parent", printPassing);
    recordCheck(passCount, failures, patchGeometryMatches,
        "SURFACE-BEZIER: every patch reproduces the parent across its own rectangle, corners included", printPassing);
    recordCheck(passCount, failures, abs(patches[0][0].uDomainStart) <= KNOT_PARAMETER_TOLERANCE &&
        abs(patches[2][1].uDomainEnd - 1) <= KNOT_PARAMETER_TOLERANCE &&
        abs(patches[0][0].vDomainStart) <= KNOT_PARAMETER_TOLERANCE &&
        abs(patches[2][1].vDomainEnd - 1) <= KNOT_PARAMETER_TOLERANCE,
        "SURFACE-BEZIER: the outer patch bounds are the parent's own domain", printPassing);

    // Periodic parent: U uniform over domain [2, 6] with interior {3, 4, 5} once clamped, so four
    // U segments and one V segment. The seam patch [5, 6] is the one that could not exist if the
    // full-period clamp were wrong.
    const periodicFixture = makeUPeriodicSurfaceFixture();
    const periodicPatches = decomposeSurfaceIntoBezierPatches(periodicFixture);
    recordCheck(passCount, failures, size(periodicPatches) == 4 && size(periodicPatches[0]) == 1,
        "SURFACE-BEZIER: a closed U direction decomposes into one patch per period span (got " ~
        size(periodicPatches) ~ " x " ~ size(periodicPatches[0]) ~ ", expected 4 x 1)", printPassing);

    var periodicGeometryMatches = true;
    for (var uSegment = 0; uSegment < size(periodicPatches); uSegment += 1)
    {
        const patch = periodicPatches[uSegment][0];
        const patchSurface = bezierPatchAsSurface(patch);
        for (var uFraction in [0, 0.25, 0.5, 0.75, 1])
        {
            for (var vFraction in [0, 0.5, 1])
            {
                const uParameter = patch.uDomainStart + uFraction * (patch.uDomainEnd - patch.uDomainStart);
                const vParameter = patch.vDomainStart + vFraction * (patch.vDomainEnd - patch.vDomainStart);
                if (!pointsMatch(evaluateBSplineSurfacePoint(patchSurface, uParameter, vParameter),
                        evaluateSurfaceLiterally(periodicFixture, uParameter, vParameter)))
                {
                    periodicGeometryMatches = false;
                }
            }
        }
    }
    recordCheck(passCount, failures, periodicGeometryMatches,
        "SURFACE-BEZIER: the patches of a closed direction reproduce it across every span, seam span included", printPassing);
}

// ============================================================================================
// SPAN-DENSITY — refineSurfaceToSpanDensity. The count checks alone would pass an implementation
// that inserted the right NUMBER of knots in the WRONG cells, so the assertion that matters is
// the per-cell RECOUNT afterwards, computed independently of the module (see
// distinctKnotsStrictlyInside). The periodic case deliberately forces the wrap cell to choose a
// parameter past the domain end, exercising the fold back into one period.
// ============================================================================================

function runSpanDensityVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = makeClampedSurfaceFixture(); // U interior {1/3, 2/3}, 6 rows; V interior {0.5}, 4 columns
    const uBoundaries = [0, 0.5, 1];
    const vBoundaries = [0, 1];
    const refined = refineSurfaceToSpanDensity(fixture, uBoundaries, vBoundaries, 4);

    // U: each of [0, 0.5] and [0.5, 1] holds one interior knot, so 2 control points -> 2 more
    // each. V: [0, 1] holds one, so 2 more. 6 + 4 = 10 rows, 4 + 2 = 6 columns.
    recordCheck(passCount, failures, size(refined.controlPoints) == 10 && size(refined.controlPoints[0]) == 6,
        "SPAN-DENSITY: inserts exactly the deficit, no more (got " ~ size(refined.controlPoints) ~ " x " ~
        size(refined.controlPoints[0]) ~ ", expected 10 x 6)", printPassing);

    var everyCellSatisfied = true;
    for (var cellIndex = 0; cellIndex < size(uBoundaries) - 1; cellIndex += 1)
    {
        if (distinctKnotsStrictlyInside(refined.uKnots, refined.uDegree, false, uBoundaries[cellIndex], uBoundaries[cellIndex + 1]) + 1 < 4)
        {
            everyCellSatisfied = false;
        }
    }
    if (distinctKnotsStrictlyInside(refined.vKnots, refined.vDegree, false, 0, 1) + 1 < 4)
    {
        everyCellSatisfied = false;
    }
    recordCheck(passCount, failures, everyCellSatisfied,
        "SPAN-DENSITY: every cell actually reaches the requested density (recounted independently)", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, refined, [0, 0.25, 0.5, 0.75, 1], [0, 0.5, 1]),
        "SPAN-DENSITY: geometry preserved", printPassing);

    const alreadyDense = refineSurfaceToSpanDensity(fixture, uBoundaries, vBoundaries, 2);
    recordCheck(passCount, failures, size(alreadyDense.controlPoints) == 6 && size(alreadyDense.controlPoints[0]) == 4,
        "SPAN-DENSITY: a requirement already met inserts nothing", printPassing);

    const untouchedDirection = refineSurfaceToSpanDensity(fixture, [], vBoundaries, 4);
    recordCheck(passCount, failures, size(untouchedDirection.controlPoints) == 6 && size(untouchedDirection.controlPoints[0]) == 6,
        "SPAN-DENSITY: an empty boundary list leaves that direction alone", printPassing);

    // Periodic U over domain [2, 6], fundamental knots {2, 3, 4, 5}. Boundaries [3, 5] make the
    // cells [3, 5] and — cyclically — [5, 7]. At a requirement of 4, the wrap cell's second pick
    // is the midpoint of [6, 7] = 6.5, which is PAST the domain end and only lands correctly if
    // it is folded back to 2.5.
    const periodicFixture = makeUPeriodicSurfaceFixture();
    const periodicRefined = refineSurfaceToSpanDensity(periodicFixture, [3, 5], [], 4);

    recordCheck(passCount, failures, periodicRefined.isUPeriodic == true,
        "SPAN-DENSITY: the periodic direction stays periodic", printPassing);
    recordCheck(passCount, failures, columnOverlapConditionHolds(periodicRefined.controlPoints, periodicRefined.uDegree),
        "SPAN-DENSITY: the U overlap condition survives targeted refinement", printPassing);
    recordCheck(passCount, failures, size(periodicRefined.controlPoints) == 10,
        "SPAN-DENSITY: 4 periodic insertions grow the stored count by 4 (got " ~
        size(periodicRefined.controlPoints) ~ ", expected 10)", printPassing);
    recordCheck(passCount, failures,
        distinctKnotsStrictlyInside(periodicRefined.uKnots, periodicRefined.uDegree, true, 3, 5) + 1 >= 4,
        "SPAN-DENSITY: the ordinary periodic cell [3, 5] reaches the requested density", printPassing);
    recordCheck(passCount, failures,
        distinctKnotsStrictlyInside(periodicRefined.uKnots, periodicRefined.uDegree, true, 5, 7) + 1 >= 4,
        "SPAN-DENSITY: the WRAP cell [5, 7] reaches it too — the fold past the domain end landed correctly", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(periodicFixture, periodicRefined, [2, 2.6, 3.5, 4.5, 5.5, 6], [0, 0.5, 1]),
        "SPAN-DENSITY: periodic geometry preserved across the whole period", printPassing);

    // Curve sibling, same contract and same helpers, different application (direct insertion
    // rather than an operator). Degree 3 over [0, 1] with interior {1/3, 2/3}: one interior knot
    // per half, so a requirement of 4 needs two more in each.
    const curveDegree = 3;
    const curveKnots = makeClampedCubicKnots();
    const curvePoints = makeSixPointFixture();
    const curve = { "degree" : curveDegree, "isPeriodic" : false, "controlPoints" : curvePoints, "knots" : knotArray(curveKnots) };
    const denseCurve = refineSplineToSpanDensity(curve, [0, 0.5, 1], 4);

    recordCheck(passCount, failures, size(denseCurve.controlPoints) == 10,
        "SPAN-DENSITY: the curve form inserts exactly the deficit (got " ~ size(denseCurve.controlPoints) ~ ", expected 10)", printPassing);
    recordCheck(passCount, failures,
        distinctKnotsStrictlyInside(denseCurve.knots, curveDegree, false, 0, 0.5) + 1 >= 4 &&
        distinctKnotsStrictlyInside(denseCurve.knots, curveDegree, false, 0.5, 1) + 1 >= 4,
        "SPAN-DENSITY: both curve cells reach the requested density (recounted independently)", printPassing);

    const curveSamples = makeUnitSampleParameters();
    const curveBefore = evaluateCurvePoints(curvePoints, curveKnots, curveDegree, curveSamples);
    const curveAfter = evaluateCurvePoints(denseCurve.controlPoints, denseCurve.knots, curveDegree, curveSamples);
    var curveGeometryPreserved = true;
    for (var sampleIndex = 0; sampleIndex < size(curveSamples); sampleIndex += 1)
    {
        if (!pointsMatch(curveBefore[sampleIndex], curveAfter[sampleIndex]))
        {
            curveGeometryPreserved = false;
        }
    }
    recordCheck(passCount, failures, curveGeometryPreserved,
        "SPAN-DENSITY: curve geometry preserved", printPassing);
}

// ============================================================================================
// INTERPOLATE — the constructive entry points. Unlike everything else here there is no input
// spline to compare against, so the anchor is the DEFINING PROPERTY: the result passes exactly
// through the data. That is checkable to full tolerance, which is what makes this testable at all.
//
// A second, stronger anchor covers the space between the data points, where "passes through" says
// nothing: interpolation builds each control point as an affine combination of the data points, so
// data lying in a PLANE must produce a surface lying entirely in that plane — everywhere, not just
// at the samples. Any error in the operator or the solve breaks that immediately.
// ============================================================================================

function runInterpolationVector(passCount is box, failures is box, printPassing is boolean)
{
    // ---- Curve: through every point ----
    const curvePoints = makeSixPointFixture();
    const curve = interpolateBSplineCurveThroughPoints(curvePoints, 3);

    recordCheck(passCount, failures, size(curve.controlPoints) == size(curvePoints),
        "INTERPOLATE: the curve has one control point per data point", printPassing);

    var curvePassesThrough = true;
    for (var index = 0; index < size(curvePoints); index += 1)
    {
        const evaluated = evaluateBSplineCurveDerivatives(curve, curve.parameters[index], 0)[0];
        if (!pointsMatch(evaluated, curvePoints[index]))
        {
            curvePassesThrough = false;
        }
    }
    recordCheck(passCount, failures, curvePassesThrough,
        "INTERPOLATE: the curve passes exactly through every data point at its own parameter", printPassing);

    // ---- Surface: sample a known surface, interpolate, pass through every sample ----
    const source = normalizeSurfaceDefinition(makeClampedSurfaceFixture());
    const uSampleParameters = [0, 0.2, 0.5, 0.8, 1];
    const vSampleParameters = [0, 0.4, 0.7, 1];
    var grid = makeArray(size(uSampleParameters), 0);
    for (var i = 0; i < size(uSampleParameters); i += 1)
    {
        var row = makeArray(size(vSampleParameters), vector(0, 0, 0) * meter);
        for (var j = 0; j < size(vSampleParameters); j += 1)
        {
            row[j] = evaluateBSplineSurfacePoint(source, uSampleParameters[i], vSampleParameters[j]);
        }
        grid[i] = row;
    }

    const interpolated = interpolateBSplineSurfaceThroughGrid(grid, 3, 2);
    recordCheck(passCount, failures, size(interpolated.controlPoints) == size(uSampleParameters) &&
        size(interpolated.controlPoints[0]) == size(vSampleParameters),
        "INTERPOLATE: the surface has one control point per grid node (got " ~ size(interpolated.controlPoints) ~
        " x " ~ size(interpolated.controlPoints[0]) ~ ", expected 5 x 4)", printPassing);
    recordCheck(passCount, failures, interpolated.uDegree == 3 && interpolated.vDegree == 2,
        "INTERPOLATE: the surface carries the requested degrees", printPassing);

    var surfacePassesThrough = true;
    for (var i = 0; i < size(uSampleParameters); i += 1)
    {
        for (var j = 0; j < size(vSampleParameters); j += 1)
        {
            const evaluated = evaluateBSplineSurfacePoint(interpolated, interpolated.uParameters[i], interpolated.vParameters[j]);
            if (!pointsMatch(evaluated, grid[i][j]))
            {
                surfacePassesThrough = false;
            }
        }
    }
    recordCheck(passCount, failures, surfacePassesThrough,
        "INTERPOLATE: the surface passes exactly through every grid node", printPassing);

    // ---- Planar anchor: covers the space BETWEEN the data, which "passes through" does not ----
    // Deliberately non-uniform spacing and a tilted plane, so chord-length parameterization is
    // doing real work rather than degenerating to uniform.
    const planeOrigin = vector(1, 2, 3) * centimeter;
    const planeU = normalize(vector(1, 1, 0));
    const planeV = normalize(vector(-1, 1, 1));
    const planeNormal = normalize(cross(planeU, planeV));
    const uOffsets = [0, 0.7, 1.9, 3.6, 5.0];
    const vOffsets = [0, 1.3, 2.1, 4.4];
    var planarGrid = makeArray(size(uOffsets), 0);
    for (var i = 0; i < size(uOffsets); i += 1)
    {
        var row = makeArray(size(vOffsets), planeOrigin);
        for (var j = 0; j < size(vOffsets); j += 1)
        {
            row[j] = planeOrigin + uOffsets[i] * centimeter * planeU + vOffsets[j] * centimeter * planeV;
        }
        planarGrid[i] = row;
    }

    const planarSurface = interpolateBSplineSurfaceThroughGrid(planarGrid, 3, 2);
    var staysInPlane = true;
    for (var uParameter in [0, 0.13, 0.37, 0.62, 0.85, 1])
    {
        for (var vParameter in [0, 0.29, 0.55, 0.91, 1])
        {
            const evaluated = evaluateBSplineSurfacePoint(planarSurface, uParameter, vParameter);
            if (abs(dot(evaluated - planeOrigin, planeNormal)) > 1e-9 * meter)
            {
                staysInPlane = false;
            }
        }
    }
    recordCheck(passCount, failures, staysInPlane,
        "INTERPOLATE: planar data gives a surface lying in that plane EVERYWHERE, not only at the nodes", printPassing);

    // Interpolating too few points for the requested degree is impossible, not merely awkward, and
    // must say so rather than producing a singular solve full of infinities.
    const tooFew = try silent(interpolateBSplineCurveThroughPoints(makeThreePointFixture(), 5));
    recordCheck(passCount, failures, tooFew == undefined,
        "INTERPOLATE: refuses to interpolate fewer points than the degree needs", printPassing);
}

// ============================================================================================
// ARCLENGTH — arc-length knot placement and affine domain rescaling.
//
// The measured quantity is ARC LENGTH BETWEEN CONSECUTIVE KNOTS, which is exactly what the
// placement controls. Allocating pieces per span and then cutting each span into that many equal
// pieces at once bounds the result far more tightly than the greedy halving it replaced: halving
// can only ever divide a span in powers of two, so any piece count that is not a power of two comes
// out at a flat 2:1 no matter how carefully each individual split was located. The two cylinder
// checks below are the regression test for that — it is why their thresholds are near one rather
// than near two.
//
// AN EARLIER VERSION OF THIS VECTOR MEASURED THE WRONG THING and failed on correct code — worth
// recording so it is not reintroduced. It compared ANGULAR GAPS BETWEEN CONTROL POINTS around the
// cylinder, which cannot be even for this fixture at any knot spacing: refinement only adds knots,
// so the arc joints keep their multiplicity-3 knots, and at multiplicity == degree the curve
// interpolates its control point while the neighbours crowd in against it. The lopsidedness it
// detected was a property of the fixture's C0 joints, not of the placement.
//
// Placement can never affect geometry (insertion is exact wherever it lands), so the geometry
// checks here guard the plumbing, not the heuristic.
// ============================================================================================

/** Arc length of each span between consecutive DISTINCT knots of one direction, measured by
    sub-sampled chords along a representative isocurve. */
function arcLengthSpansOfDirection(surface is map, isUDirection is boolean, station is number) returns array
{
    const degree = isUDirection ? surface.uDegree : surface.vDegree;
    const knots = isUDirection ? surface.uKnots : surface.vKnots;
    const domain = knotDomain(knots, degree);

    var breaks = [domain.start];
    for (var knotIndex = 0; knotIndex < size(knots); knotIndex += 1)
    {
        const value = knots[knotIndex];
        if (value > breaks[size(breaks) - 1] + KNOT_PARAMETER_TOLERANCE && value < domain.end - KNOT_PARAMETER_TOLERANCE)
        {
            breaks = append(breaks, value);
        }
    }
    breaks = append(breaks, domain.end);

    const subSamples = 8;
    var spans = makeArray(size(breaks) - 1, 0 * meter);
    for (var spanIndex = 0; spanIndex < size(breaks) - 1; spanIndex += 1)
    {
        var spanLength = 0 * meter;
        var previous = isUDirection ? evaluateBSplineSurfacePoint(surface, breaks[spanIndex], station)
            : evaluateBSplineSurfacePoint(surface, station, breaks[spanIndex]);
        for (var step = 1; step <= subSamples; step += 1)
        {
            const parameter = breaks[spanIndex] + (breaks[spanIndex + 1] - breaks[spanIndex]) * step / subSamples;
            const point = isUDirection ? evaluateBSplineSurfacePoint(surface, parameter, station)
                : evaluateBSplineSurfacePoint(surface, station, parameter);
            spanLength += norm(point - previous);
            previous = point;
        }
        spans[spanIndex] = spanLength;
    }
    return spans;
}

/**
 * Whether a knot vector reads the same forwards as backwards about its own domain — the exact
 * statement of "this refinement treated both sides alike", and the only property that catches an
 * allocation bias without asserting anything about WHERE the knots went.
 */
function knotVectorIsPalindromic(knots is array) returns boolean
{
    const count = size(knots);
    const endsSum = knots[0] + knots[count - 1];
    for (var index = 0; index < count; index += 1)
    {
        if (abs(knots[index] + knots[count - 1 - index] - endsSum) > KNOT_PARAMETER_TOLERANCE)
        {
            return false;
        }
    }
    return true;
}

/**
 * A mirror-symmetric surface: a parabola in u (even in x, so every span has a mirror twin of
 * identical arc length) extruded straight along v, on a palindromic knot vector with a
 * multiplicity-2 interior knot at the middle.
 *
 * This is the configuration a lowest-index tie-break biases, and it is the shape of the case that
 * found the bug live — two equal faces of a cube merged into one patch, whose elevated knot vector
 * is exactly this: two equal spans meeting at a raised-multiplicity knot in the middle.
 */
function makeMirrorSymmetricSurfaceFixture() returns map
{
    const xValues = [-1, -0.6, -0.2, 0.2, 0.6, 1];
    var controlPoints = makeArray(6, 0);
    var weights = makeArray(6, 0);
    for (var index = 0; index < 6; index += 1)
    {
        const x = xValues[index];
        controlPoints[index] = [vector(x, 0, x * x) * meter, vector(x, 1, x * x) * meter];
        weights[index] = [1, 1];
    }
    return {
            "uDegree" : 3,
            "vDegree" : 1,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "weights" : weights,
            "uKnots" : [0, 0, 0, 0, 1, 1, 2, 2, 2, 2],
            "vKnots" : [0, 0, 1, 1]
        };
}

/** Ratio of the longest span to the shortest — the evenness measure. */
function spanLengthRatio(spans is array) returns number
{
    var shortest = spans[0];
    var longest = spans[0];
    for (var span in spans)
    {
        shortest = min(shortest, span);
        longest = max(longest, span);
    }
    return shortest <= 0 * meter ? 1e9 : longest / shortest;
}

function runArcLengthPlacementVector(passCount is box, failures is box, printPassing is boolean)
{
    const cylinder = normalizeSurfaceDefinition(makeClosedClampedCylinderFixture());
    const fundamentalBefore = size(cylinder.controlPoints) - cylinder.uDegree;
    const targetFundamental = 24;
    const refined = refineSurfaceToControlPointCounts(cylinder, targetFundamental + cylinder.uDegree,
            size(cylinder.controlPoints[0]));

    recordCheck(passCount, failures, size(refined.controlPoints) == targetFundamental + cylinder.uDegree,
        "ARCLENGTH: the periodic direction reaches its target stored count (got " ~ size(refined.controlPoints) ~
        ", expected " ~ (targetFundamental + cylinder.uDegree) ~ ", from " ~ fundamentalBefore ~
        " editable points by " ~ (targetFundamental - fundamentalBefore) ~ " insertions)", printPassing);
    recordCheck(passCount, failures, columnOverlapConditionHolds(refined.controlPoints, refined.uDegree),
        "ARCLENGTH: the overlap condition survives arc-length refinement", printPassing);

    // The knots must divide the PERIMETER evenly, which is what arc-length placement promises.
    // This fixture is the two-arc rational cubic circle, so its wrap form has TWO equal half spans
    // (the multiplicity-3 knot at the half-way point is the only interior one). Six editable points
    // to twenty-four is eighteen insertions, hence twenty pieces, ten per half — dead even, and the
    // only spread left is quadrature error in the profile.
    //
    // Note ten pieces is not a power of two, so bisection cannot reach it evenly either: it lands on
    // six quarter-spans and four eighths and reports 2:1. This threshold is what catches that.
    const cylinderRatio = spanLengthRatio(arcLengthSpansOfDirection(refined, true, 0.5));
    recordCheck(passCount, failures, cylinderRatio < 1.15,
        "ARCLENGTH: knots divide the cylinder's perimeter evenly (longest/shortest span " ~ cylinderRatio ~
        ", an even allocation puts this at 1)", printPassing);

    // The same check where the piece count cannot split evenly BETWEEN the spans either. Fifteen
    // editable points is eleven pieces over two spans — 6/5, a 6:5 ratio, the best any insertion-only
    // placement can do, since existing knots are never removed.
    const unevenTarget = 15;
    const unevenlyRefined = refineSurfaceToControlPointCounts(cylinder, unevenTarget + cylinder.uDegree,
            size(cylinder.controlPoints[0]));
    const unevenRatio = spanLengthRatio(arcLengthSpansOfDirection(unevenlyRefined, true, 0.5));
    recordCheck(passCount, failures, unevenRatio < 1.35,
        "ARCLENGTH: an indivisible target still spreads evenly (longest/shortest span " ~ unevenRatio ~
        ", 6/5 pieces puts this at 1.2 and quantized halving at 2)", printPassing);

    var geometryHeld = true;
    const cylinderDomain = knotDomain(cylinder.uKnots, cylinder.uDegree);
    for (var fraction in [0, 0.17, 0.4, 0.63, 0.91])
    {
        const uParameter = cylinderDomain.start + fraction * (cylinderDomain.end - cylinderDomain.start);
        for (var vParameter in [0, 0.5, 1])
        {
            if (!pointsMatch(evaluateBSplineSurfacePoint(refined, uParameter, vParameter),
                    evaluateBSplineSurfacePoint(cylinder, uParameter, vParameter)))
            {
                geometryHeld = false;
            }
        }
    }
    recordCheck(passCount, failures, geometryHeld,
        "ARCLENGTH: refinement is still exact - placement changes distribution, never geometry", printPassing);

    // VARIABLE EXISTING DENSITY — the case reported as globally questionable. A direction whose
    // knots are already clustered at one end must still come out evenly divided, and this is what
    // per-span profile sampling exists for: sampling the profile uniformly across the domain gives
    // a span occupying one percent of the range one or two samples, so both its measured length
    // and any parameter located inside it are guesswork.
    // Same control net, but with both interior knots crowded into the first twentieth of the
    // domain — a legal knot vector describing a wildly non-uniform parameterization.
    var clustered = makeClampedSurfaceFixture();
    clustered.uKnots = [0, 0, 0, 0, 0.03, 0.06, 1, 1, 1, 1];
    clustered = normalizeSurfaceDefinition(clustered);
    const denselyRefined = refineSurfaceToControlPointCounts(clustered, 20, size(clustered.controlPoints[0]));
    const variableRatio = spanLengthRatio(arcLengthSpansOfDirection(denselyRefined, true, 0.5));
    recordCheck(passCount, failures, variableRatio < 2.5,
        "ARCLENGTH: a direction refined heavily still divides evenly by arc length (longest/shortest span " ~
        variableRatio ~ ")", printPassing);

    var denseGeometryHeld = true;
    for (var uParameter in [0, 0.2, 0.45, 0.7, 1])
    {
        for (var vParameter in [0, 0.5, 1])
        {
            if (!pointsMatch(evaluateBSplineSurfacePoint(denselyRefined, uParameter, vParameter),
                    evaluateBSplineSurfacePoint(clustered, uParameter, vParameter)))
            {
                denseGeometryHeld = false;
            }
        }
    }
    recordCheck(passCount, failures, denseGeometryHeld,
        "ARCLENGTH: heavy refinement of a variable-density direction is still exact", printPassing);

    // BALANCE — the allocation must not prefer one side of a mirror-symmetric shape.
    //
    // The failure this catches is not a parity accident. "Give the insertion to the longest piece",
    // with ties broken by array order, picks the LOWER INDEX every single time; on a symmetric shape
    // every span ties with its mirror, so every allocation that cannot divide evenly lands on the
    // same side, at every count. That systematic lean is what put knots preferentially on one side
    // of a merged fillet strip, and — because the merge then removes a knot at the seam, reading the
    // spacing on BOTH sides as it goes — it came out as a geometric artifact rather than merely an
    // uneven net.
    const symmetric = normalizeSurfaceDefinition(makeMirrorSymmetricSurfaceFixture());
    const symmetricCount = size(symmetric.controlPoints);
    const symmetricColumns = size(symmetric.controlPoints[0]);
    recordCheck(passCount, failures, knotVectorIsPalindromic(symmetric.uKnots),
        "ARCLENGTH: the mirror-symmetric fixture starts palindromic", printPassing);

    // One insertion cannot be split between two equal spans, so the balanced allocation declines it
    // rather than picking a side. Coming in UNDER the target is the contract, not a shortfall.
    const balancedOdd = refineSurfaceToControlPointCounts(symmetric, symmetricCount + 1, symmetricColumns, true);
    recordCheck(passCount, failures, knotVectorIsPalindromic(balancedOdd.uKnots),
        "ARCLENGTH: balanced refinement stays palindromic when the target cannot divide evenly", printPassing);
    recordCheck(passCount, failures, size(balancedOdd.controlPoints) <= symmetricCount + 1,
        "ARCLENGTH: balanced refinement never overshoots its target (got " ~ size(balancedOdd.controlPoints) ~
        ", asked at most " ~ (symmetricCount + 1) ~ ")", printPassing);

    // Two CAN be split, so the whole tie group is served and the target is reached exactly.
    const balancedEven = refineSurfaceToControlPointCounts(symmetric, symmetricCount + 2, symmetricColumns, true);
    recordCheck(passCount, failures, knotVectorIsPalindromic(balancedEven.uKnots) &&
        size(balancedEven.controlPoints) == symmetricCount + 2,
        "ARCLENGTH: balanced refinement serves a whole tie group and reaches the target", printPassing);

    var balancedExact = true;
    for (var uParameter in [0, 0.4, 1, 1.6, 2])
    {
        for (var vParameter in [0, 0.5, 1])
        {
            if (!pointsMatch(evaluateBSplineSurfacePoint(balancedEven, uParameter, vParameter),
                    evaluateBSplineSurfacePoint(symmetric, uParameter, vParameter)))
            {
                balancedExact = false;
            }
        }
    }
    recordCheck(passCount, failures, balancedExact,
        "ARCLENGTH: balanced refinement is still exact - it changes the count, never the surface", printPassing);

    // The plain overload keeps its exact-count contract and pays for it by picking a side. BOTH
    // halves matter: making balance the default would silently break every caller that asked for a
    // number, and a balanced flag that changed nothing would be worse than no flag.
    const exactOdd = refineSurfaceToControlPointCounts(symmetric, symmetricCount + 1, symmetricColumns);
    recordCheck(passCount, failures, size(exactOdd.controlPoints) == symmetricCount + 1,
        "ARCLENGTH: the plain overload still hits its exact target", printPassing);
    recordCheck(passCount, failures, !knotVectorIsPalindromic(exactOdd.uKnots),
        "ARCLENGTH: the plain overload does pick a side - the two overloads genuinely differ", printPassing);

    // Affine domain rescale: geometry identical at proportional parameters, knots on the new span.
    const fixture = normalizeSurfaceDefinition(makeClampedSurfaceFixture());
    const rescaled = rescaleSurfaceDirectionDomain(fixture, true, 5, 9);
    const rescaledDomain = knotDomain(rescaled.uKnots, rescaled.uDegree);
    recordCheck(passCount, failures, abs(rescaledDomain.start - 5) < KNOT_PARAMETER_TOLERANCE &&
        abs(rescaledDomain.end - 9) < KNOT_PARAMETER_TOLERANCE,
        "ARCLENGTH: rescaleSurfaceDirectionDomain lands the domain exactly where asked", printPassing);

    var rescaleExact = true;
    for (var fraction in [0, 0.25, 0.5, 0.75, 1])
    {
        for (var vParameter in [0, 0.5, 1])
        {
            if (!pointsMatch(evaluateBSplineSurfacePoint(rescaled, 5 + 4 * fraction, vParameter),
                    evaluateBSplineSurfacePoint(fixture, fraction, vParameter)))
            {
                rescaleExact = false;
            }
        }
    }
    recordCheck(passCount, failures, rescaleExact,
        "ARCLENGTH: rescaling the domain moves no geometry - it is an affine reparameterization", printPassing);

    const arcLength = approximateDirectionArcLength(rescaled, true);
    recordCheck(passCount, failures, abs(arcLength / approximateDirectionArcLength(fixture, true) - 1) < 1e-6,
        "ARCLENGTH: measured arc length is a property of the shape, unchanged by reparameterization", printPassing);
}

// ============================================================================================
// REDUNDANT — removeRedundantSurfaceKnots, including the PERIODIC removal it needed.
//
// The anchor is the exact round trip: refine a CLAMPED direction (insertion only, so every added
// knot is by construction exactly removable), then clean — the added knots must come back out at
// zero cost and the surface must not move.
//
// PERIODIC DIRECTIONS MUST COME BACK UNTOUCHED, and that is a real checked contract rather than an
// omission. A first implementation did clean them, through the tile / operate / slice window that
// refinement and elevation use, and it MOVED THE GEOMETRY — a cylinder came back a bean while the
// deviation it reported stayed small. Both of these checks caught it. The window construction is
// sound for insertion and elevation, which are local and forward; removal is the SOLVE that inverts
// them, and running that inside a clamped window does not reproduce the infinite periodic answer.
// It was removed rather than flagged off: a lumpy exact net beats a smooth wrong one.
// ============================================================================================

function runRedundantKnotVector(passCount is box, failures is box, printPassing is boolean)
{
    const cylinder = normalizeSurfaceDefinition(makeClosedClampedCylinderFixture());
    const radius = makeClosedClampedCircleFixture().radius * meter;

    const refinedCylinder = refineSurfaceToControlPointCounts(cylinder, size(cylinder.controlPoints) + 8,
            size(cylinder.controlPoints[0]));
    const cleanedCylinder = removeRedundantSurfaceKnots(refinedCylinder, 1e-9 * meter);

    recordCheck(passCount, failures, size(cleanedCylinder.controlPoints) == size(refinedCylinder.controlPoints) &&
        knotVectorsMatch(cleanedCylinder.uKnots, refinedCylinder.uKnots),
        "REDUNDANT: a periodic direction is returned untouched, not best-effort cleaned", printPassing);
    recordCheck(passCount, failures, cleanedCylinder.isUPeriodic == true &&
        columnOverlapConditionHolds(cleanedCylinder.controlPoints, cleanedCylinder.uDegree),
        "REDUNDANT: closure and the overlap condition survive", printPassing);

    // The check that caught the bean: whatever cleaning does or declines to do, the closed
    // direction must still BE the exact circle afterwards.
    const axis = vector(0, 0, 1);
    var stillACircle = true;
    const cleanedDomain = knotDomain(cleanedCylinder.uKnots, cleanedCylinder.uDegree);
    for (var fraction in [0, 0.13, 0.37, 0.62, 0.88])
    {
        const uParameter = cleanedDomain.start + fraction * (cleanedDomain.end - cleanedDomain.start);
        const point = evaluateBSplineSurfacePoint(cleanedCylinder, uParameter, 0.5);
        const radial = point - dot(point, axis) * axis;
        if (abs(norm(radial) / radius - 1) > 1e-9)
        {
            stillACircle = false;
        }
    }
    recordCheck(passCount, failures, stillACircle,
        "REDUNDANT: the closed direction is still the exact circle", printPassing);

    // A clamped direction with deliberately doubled interior knots: the duplicates are redundant
    // on a smooth surface and must go, without moving it.
    var doubled = makeClampedSurfaceFixture();
    doubled.uKnots = [0, 0, 0, 0, 1 / 3, 1 / 3, 2 / 3, 1, 1, 1, 1];
    var doubledPoints = makeArray(7, 0);
    for (var rowIndex = 0; rowIndex < 7; rowIndex += 1)
    {
        doubledPoints[rowIndex] = doubled.controlPoints[min(rowIndex, 5)];
    }
    doubled.controlPoints = doubledPoints;
    const refinedDoubled = refineSurfaceToControlPointCounts(normalizeSurfaceDefinition(doubled), 11, 4);
    const cleanedClamped = removeRedundantSurfaceKnots(refinedDoubled, 1e-9 * meter);
    recordCheck(passCount, failures, size(cleanedClamped.controlPoints) < size(refinedDoubled.controlPoints),
        "REDUNDANT: a clamped direction sheds its removable knots too (" ~ size(refinedDoubled.controlPoints) ~
        " -> " ~ size(cleanedClamped.controlPoints) ~ ")", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(cleanedClamped, refinedDoubled,
            [0, 0.25, 0.5, 0.75, 1], [0, 0.5, 1]),
        "REDUNDANT: cleaning a clamped direction moves no geometry", printPassing);
}

// ============================================================================================
// ISOCURVE — extractIsoparametricCurve. The anchor is agreement with the surface evaluator at
// matched parameters, which is checkable to full tolerance, plus the standing rational anchor:
// an isocurve of the real cylinder around its closed direction IS the circle, radius exact —
// a weights-dropping extraction cannot fake that.
// ============================================================================================

function runIsocurveVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = normalizeSurfaceDefinition(makeClampedSurfaceFixture());

    const vCurve = extractIsoparametricCurve(fixture, true, 0.4); // fix u -> curve in v
    var vCurveMatches = true;
    for (var vParameter in [0, 0.3, 0.5, 0.8, 1])
    {
        if (!pointsMatch(evaluateBSplineCurveDerivatives(vCurve, vParameter, 0)[0],
                evaluateBSplineSurfacePoint(fixture, 0.4, vParameter)))
        {
            vCurveMatches = false;
        }
    }
    recordCheck(passCount, failures, vCurveMatches,
        "ISOCURVE: fixing u gives the curve the surface traces in v, exactly", printPassing);
    recordCheck(passCount, failures, vCurve.degree == fixture.vDegree && knotVectorsMatch(vCurve.knots, fixture.vKnots),
        "ISOCURVE: the v-curve inherits the v degree and knot vector verbatim", printPassing);

    const uCurve = extractIsoparametricCurve(fixture, false, 0.3); // fix v -> curve in u
    var uCurveMatches = true;
    for (var uParameter in [0, 0.25, 0.5, 0.75, 1])
    {
        if (!pointsMatch(evaluateBSplineCurveDerivatives(uCurve, uParameter, 0)[0],
                evaluateBSplineSurfacePoint(fixture, uParameter, 0.3)))
        {
            uCurveMatches = false;
        }
    }
    recordCheck(passCount, failures, uCurveMatches,
        "ISOCURVE: fixing v gives the curve the surface traces in u, exactly", printPassing);

    // Rational + periodic: around the real cylinder, the isocurve IS the circle.
    const cylinder = normalizeSurfaceDefinition(makeClosedClampedCylinderFixture());
    const radius = makeClosedClampedCircleFixture().radius * meter;
    const circle = extractIsoparametricCurve(cylinder, false, 0.5); // fix v -> closed curve in u
    recordCheck(passCount, failures, circle.isPeriodic == true,
        "ISOCURVE: an isocurve around a closed direction is itself closed", printPassing);

    const axis = vector(0, 0, 1);
    var staysOnCircle = true;
    var matchesSurface = true;
    const cylinderDomain = knotDomain(cylinder.uKnots, cylinder.uDegree);
    for (var fraction in [0, 0.125, 0.3, 0.6, 0.9])
    {
        const uParameter = cylinderDomain.start + fraction * (cylinderDomain.end - cylinderDomain.start);
        const point = evaluateBSplineCurveDerivatives(circle, uParameter, 0)[0];
        const radial = point - dot(point, axis) * axis;
        if (abs(norm(radial) / radius - 1) > 1e-9)
        {
            staysOnCircle = false;
        }
        if (!pointsMatch(point, evaluateBSplineSurfacePoint(cylinder, uParameter, 0.5)))
        {
            matchesSurface = false;
        }
    }
    recordCheck(passCount, failures, staysOnCircle,
        "ISOCURVE: the cylinder's isocurve lies on the true circle - weights were NOT dropped", printPassing);
    recordCheck(passCount, failures, matchesSurface,
        "ISOCURVE: the closed isocurve agrees with the surface evaluator everywhere sampled", printPassing);
}

// ============================================================================================
// CONCAT — concatenateBSplineCurves / concatenateBSplineSurfaces, plus the targeted removal that
// heals the seams. The anchor is the full split -> concatenate -> heal round trip: pieces of ONE
// underlying spline must reassemble into geometry identical to the original, and healing the
// seams must cost EXACTLY zero and recover the original knot vectors — the exact-merge case is
// this pipeline reporting 0, not a separate code path.
// ============================================================================================

/** A Bezier segment from decomposeIntoBezierSegments as a standalone clamped curve. */
function bezierSegmentAsCurve(segment is map) returns map
{
    var segmentKnots = makeArray(2 * (segment.degree + 1), segment.domainStart);
    for (var index = segment.degree + 1; index < size(segmentKnots); index += 1)
    {
        segmentKnots[index] = segment.domainEnd;
    }
    return {
            "degree" : segment.degree,
            "isPeriodic" : false,
            "isRational" : true,
            "controlPoints" : segment.controlPoints,
            "weights" : segment.weights,
            "knots" : knotArray(segmentKnots)
        };
}

function runConcatenationVector(passCount is box, failures is box, printPassing is boolean)
{
    // ---- Curves: decompose into Beziers, chain them back, heal the seams ----
    const degree = 3;
    const originalKnots = makeClampedCubicKnots();
    const originalPoints = makeSixPointFixture();
    const original = { "degree" : degree, "isPeriodic" : false, "controlPoints" : originalPoints, "knots" : knotArray(originalKnots) };

    const segments = decomposeIntoBezierSegments(original);
    var segmentCurves = makeArray(size(segments), 0);
    for (var index = 0; index < size(segments); index += 1)
    {
        segmentCurves[index] = bezierSegmentAsCurve(segments[index]);
    }

    const chained = concatenateBSplineCurves(segmentCurves, 1e-7 * meter);
    recordCheck(passCount, failures, size(chained.seamParameters) == 2 &&
        abs(chained.seamParameters[0] - 1 / 3) < KNOT_PARAMETER_TOLERANCE &&
        abs(chained.seamParameters[1] - 2 / 3) < KNOT_PARAMETER_TOLERANCE,
        "CONCAT: the chain reports its seam parameters, at the original breakpoints", printPassing);

    var chainMatches = true;
    for (var parameter in makeUnitSampleParameters())
    {
        if (!pointsMatch(evaluateBSplineCurveDerivatives(chained, parameter, 0)[0],
                evaluateCurvePoints(originalPoints, originalKnots, degree, [parameter])[0]))
        {
            chainMatches = false;
        }
    }
    recordCheck(passCount, failures, chainMatches,
        "CONCAT: chaining a curve's own Bezier pieces reproduces it exactly, parameterization included", printPassing);

    // Heal: each seam sits at multiplicity == degree; the original had multiplicity 1, so remove
    // degree - 1 instances at each. The pieces come from one curve, so this must be free.
    var healed = removeSplineKnot(chained, 1 / 3, degree - 1);
    const firstHealDeviation = healed.deviation;
    healed = removeSplineKnot(healed, 2 / 3, degree - 1);
    recordCheck(passCount, failures, firstHealDeviation < 1e-9 * meter && healed.deviation < 1e-9 * meter,
        "CONCAT: healing seams between pieces of ONE curve costs exactly zero", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(healed.knots, originalKnots),
        "CONCAT: healing recovers the original knot vector, not merely the original shape", printPassing);

    // ---- Surfaces: split with extractSubSurface, chain, heal ----
    const fixture = normalizeSurfaceDefinition(makeClampedSurfaceFixture());
    const uSamples = [0, 0.25, 0.4, 0.7, 1];
    const vSamples = [0, 0.5, 1];

    const pieceA = extractSubSurface(fixture, 0, 0.4, 0, 1);
    const pieceB = extractSubSurface(fixture, 0.4, 1, 0, 1);
    const joined = concatenateBSplineSurfaces([pieceA, pieceB], true, 1e-7 * meter);
    recordCheck(passCount, failures, size(joined.seamParameters) == 1 &&
        abs(joined.seamParameters[0] - 0.4) < KNOT_PARAMETER_TOLERANCE,
        "CONCAT: the surface chain reports its seam at the split parameter", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(joined, fixture, uSamples, vSamples),
        "CONCAT: two pieces of one surface reassemble into it exactly", printPassing);

    const healedSurface = removeSurfaceKnotLine(joined, true, 0.4, fixture.uDegree);
    recordCheck(passCount, failures, healedSurface.deviation < 1e-9 * meter,
        "CONCAT: healing the surface seam costs exactly zero (got " ~ toString(healedSurface.deviation) ~ ")", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(healedSurface.uKnots, fixture.uKnots),
        "CONCAT: the healed surface recovers the original U knot vector", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(healedSurface, fixture, uSamples, vSamples),
        "CONCAT: split -> concatenate -> heal is a perfect round trip", printPassing);

    // V-direction chaining exercises the transpose conjugation.
    const pieceC = extractSubSurface(fixture, 0, 1, 0, 0.6);
    const pieceD = extractSubSurface(fixture, 0, 1, 0.6, 1);
    const joinedV = concatenateBSplineSurfaces([pieceC, pieceD], false, 1e-7 * meter);
    recordCheck(passCount, failures, surfacesMatchOnGrid(joinedV, fixture, [0, 0.5, 1], [0, 0.3, 0.6, 0.8, 1]),
        "CONCAT: chaining in V (via transposition) reassembles exactly too", printPassing);

    var transposeMatches = true;
    const flipped = transposeSurface(fixture);
    for (var uParameter in [0, 0.4, 1])
    {
        for (var vParameter in [0, 0.7, 1])
        {
            if (!pointsMatch(evaluateBSplineSurfacePoint(flipped, vParameter, uParameter),
                    evaluateBSplineSurfacePoint(fixture, uParameter, vParameter)))
            {
                transposeMatches = false;
            }
        }
    }
    recordCheck(passCount, failures, transposeMatches,
        "CONCAT: transposeSurface swaps the directions exactly, S'(v, u) == S(u, v)", printPassing);
}

// ============================================================================================
// LOFT — loftBSplineSurfaceThroughCurves. The decisive anchor is UNISOLVENCE: lofting a surface's
// own isocurves, taken at the Greville abscissae of its own knot vector, back onto that knot
// vector must reproduce the surface EXACTLY — not approximately — because the interpolation
// system has exactly one solution and the original control net is it (Schoenberg–Whitney).
// ============================================================================================

function runLoftVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = normalizeSurfaceDefinition(makeClampedSurfaceFixture());
    const sectionCount = size(fixture.controlPoints);

    var stations = makeArray(sectionCount, 0);
    for (var index = 0; index < sectionCount; index += 1)
    {
        var total = 0;
        for (var j = 1; j <= fixture.uDegree; j += 1)
        {
            total += fixture.uKnots[index + j];
        }
        stations[index] = total / fixture.uDegree;
    }

    var sections = makeArray(sectionCount, 0);
    for (var index = 0; index < sectionCount; index += 1)
    {
        sections[index] = extractIsoparametricCurve(fixture, true, stations[index]);
    }

    const rebuilt = loftBSplineSurfaceThroughCurves(sections, fixture.uDegree, stations, fixture.uKnots);
    recordCheck(passCount, failures, knotVectorsMatch(rebuilt.uKnots, fixture.uKnots),
        "LOFT: the prescribed-knot loft carries the surface's own U knot vector", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(rebuilt, fixture, [0, 0.2, 0.5, 0.8, 1], [0, 0.4, 1]),
        "LOFT: lofting a surface's own isocurves at its Greville abscissae reproduces it EXACTLY (unisolvence)", printPassing);

    // Default-station overload: whatever stations it picks, it must pass through every section there.
    const lofted = loftBSplineSurfaceThroughCurves(sections, fixture.uDegree);
    var passesThroughSections = true;
    for (var index = 0; index < sectionCount; index += 1)
    {
        for (var vParameter in [0, 0.5, 1])
        {
            if (!pointsMatch(evaluateBSplineSurfacePoint(lofted, lofted.uParameters[index], vParameter),
                    evaluateBSplineCurveDerivatives(sections[index], vParameter, 0)[0]))
            {
                passesThroughSections = false;
            }
        }
    }
    recordCheck(passCount, failures, passesThroughSections,
        "LOFT: the default-station loft passes exactly through every section at its own station", printPassing);

    // Closed rational sections: lofting circles of the real cylinder. The overlap condition and
    // the weights must both survive columnwise interpolation.
    const cylinder = normalizeSurfaceDefinition(makeClosedClampedCylinderFixture());
    const radius = makeClosedClampedCircleFixture().radius * meter;
    var circles = makeArray(3, 0);
    for (var index = 0; index < 3; index += 1)
    {
        circles[index] = extractIsoparametricCurve(cylinder, false, index * 0.5);
    }
    const ring = loftBSplineSurfaceThroughCurves(circles, 2);
    recordCheck(passCount, failures, ring.isVPeriodic == true,
        "LOFT: closed sections give a surface closed in the section direction", printPassing);

    const axis = vector(0, 0, 1);
    var ringOnCylinder = true;
    const ringVDomain = knotDomain(ring.vKnots, ring.vDegree);
    for (var uParameter in [0, 0.5, 1])
    {
        for (var fraction in [0, 0.2, 0.55, 0.85])
        {
            const vParameter = ringVDomain.start + fraction * (ringVDomain.end - ringVDomain.start);
            const point = evaluateBSplineSurfacePoint(ring, uParameter, vParameter);
            const radial = point - dot(point, axis) * axis;
            if (abs(norm(radial) / radius - 1) > 1e-9)
            {
                ringOnCylinder = false;
            }
        }
    }
    recordCheck(passCount, failures, ringOnCylinder,
        "LOFT: lofted circles stay on the true cylinder - rational sections survive lofting", printPassing);

    // Identical sections have no loft direction; that must be said, not solved with infinities.
    const degenerate = try silent(loftBSplineSurfaceThroughCurves([sections[0], sections[0], sections[0], sections[0]], 3));
    recordCheck(passCount, failures, degenerate == undefined,
        "LOFT: refuses a family of identical sections rather than dividing by zero spacing", printPassing);
}

// ============================================================================================
// SIMPLIFY — simplifySurfaceToControlPointCounts, the module's one lossy operation.
//
// The anchor is a ROUND TRIP that must be EXACT, not approximate: knots this module's own
// refinement inserted are by construction exactly removable, so refining a surface and simplifying
// straight back to its original count must return the original geometry with a reported deviation
// of exactly zero. That turns a lossy algorithm into one with an exactly-checkable case, which is
// worth far more than only testing it where the answer is fuzzy.
//
// The genuinely lossy case is checked separately, and on the only terms that mean anything for a
// lossy operation: the count target is met, the reported deviation is honest (the net really did
// move by no more than it claims), and the broad shape survives.
// ============================================================================================

function runSimplifySurfaceVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = normalizeSurfaceDefinition(makeClampedSurfaceFixture()); // 6 x 4, degrees 3 and 2
    const uSamples = [0, 0.25, 0.5, 0.75, 1];
    const vSamples = [0, 0.5, 1];

    // ---- Exact round trip: refine, then simplify back ----
    const refined = refineSurfaceToControlPointCounts(fixture, 12, 8);
    recordCheck(passCount, failures, size(refined.controlPoints) == 12 && size(refined.controlPoints[0]) == 8,
        "SIMPLIFY: setup — refinement reached 12 x 8", printPassing);

    const roundTripped = simplifySurfaceToControlPointCounts(refined, 6, 4);
    recordCheck(passCount, failures, size(roundTripped.controlPoints) == 6 && size(roundTripped.controlPoints[0]) == 4,
        "SIMPLIFY: round trip returns to the original counts (got " ~ size(roundTripped.controlPoints) ~ " x " ~
        size(roundTripped.controlPoints[0]) ~ ", expected 6 x 4)", printPassing);
    recordCheck(passCount, failures, roundTripped.deviation < 1e-9 * meter,
        "SIMPLIFY: round trip reports ZERO deviation — every inserted knot was exactly removable (got " ~
        roundTripped.deviation ~ ")", printPassing);
    recordCheck(passCount, failures, surfacesMatchOnGrid(fixture, roundTripped, uSamples, vSamples),
        "SIMPLIFY: round trip reproduces the original surface exactly", printPassing);
    recordCheck(passCount, failures, knotVectorsMatch(roundTripped.uKnots, fixture.uKnots) &&
        knotVectorsMatch(roundTripped.vKnots, fixture.vKnots),
        "SIMPLIFY: round trip recovers the original knot vectors, not merely the original counts", printPassing);

    // ---- Genuinely lossy: below what the surface can represent exactly ----
    const reduced = simplifySurfaceToControlPointCounts(fixture, 4, 3);
    recordCheck(passCount, failures, size(reduced.controlPoints) == 4 && size(reduced.controlPoints[0]) == 3,
        "SIMPLIFY: reaches a target BELOW the input count (got " ~ size(reduced.controlPoints) ~ " x " ~
        size(reduced.controlPoints[0]) ~ ", expected 4 x 3)", printPassing);
    recordCheck(passCount, failures, reduced.deviation >= 0 * meter,
        "SIMPLIFY: a lossy reduction reports a deviation rather than claiming exactness", printPassing);

    // The reported deviation must be HONEST: it claims to bound how far the control net moved, and
    // by partition of unity that bounds how far the surface moved. Checking the surface against
    // that claim is what stops the number from being decorative.
    var withinClaimedDeviation = true;
    const claimed = reduced.deviation + 1e-9 * meter;
    for (var uParameter in uSamples)
    {
        for (var vParameter in vSamples)
        {
            const before = evaluateBSplineSurfacePoint(fixture, uParameter, vParameter);
            const after = evaluateBSplineSurfacePoint(reduced, uParameter, vParameter);
            if (norm(before - after) > claimed)
            {
                withinClaimedDeviation = false;
            }
        }
    }
    recordCheck(passCount, failures, withinClaimedDeviation,
        "SIMPLIFY: the surface really does stay within the deviation the reduction reported", printPassing);

    // A direction already at or below its target must be untouched, and a target of 0 must mean
    // "no request" rather than "reduce to nothing".
    const untouched = simplifySurfaceToControlPointCounts(fixture, 6, 0);
    recordCheck(passCount, failures, size(untouched.controlPoints) == 6 && size(untouched.controlPoints[0]) == 4 &&
        untouched.deviation == 0 * meter,
        "SIMPLIFY: targets already met, and a 0 target, leave the surface alone", printPassing);

    // Degree must be untouched — simplification removes knots, never lowers the continuity ceiling.
    recordCheck(passCount, failures, reduced.uDegree == fixture.uDegree && reduced.vDegree == fixture.vDegree,
        "SIMPLIFY: degrees are unchanged — this reduces control points, not smoothness", printPassing);

    // ---- HANDEDNESS: removal must not care which end of the knot vector it was handed ----
    //
    // The discriminating property for the one-sided lean, and deliberately checked on an ASYMMETRIC
    // fixture so it tests the OPERATOR rather than the data. Reducing a direction, and reducing that
    // direction reversed and then un-reversing, must give the same surface: reversal is exact, so any
    // difference between the two is handedness inside the removal itself.
    //
    // This failed before removeKnotFromPointArrays centred its ambiguous point. Where the removal
    // window is even — degree - multiplicity odd, which is every simple-knot removal at degree 2 and
    // 4, and the last removal of every degree-3 seam heal — A5.8's forward and backward recurrences
    // produce two different answers for the surviving control point, and the shift kept whichever one
    // index parity left standing. That put the entire removal error on one side, so the same geometry
    // fed in mirrored came back measurably different: 1.48 against 0.74 worst deviation on a
    // two-span crease, from inputs that were reflections of each other.
    //
    // V is the direction under test because this fixture is degree 2 there, which is the even-window
    // case; U is degree 3 with simple knots, an ODD window, where the recurrences meet on a shared
    // slot and there was never an ambiguity to resolve.
    const reducedV = simplifySurfaceToControlPointCounts(fixture, 0, 3);
    const throughReversal = reverseSurfaceDirection(
            simplifySurfaceToControlPointCounts(reverseSurfaceDirection(fixture, false), 0, 3), false);
    recordCheck(passCount, failures, surfacesMatchOnGrid(reducedV, throughReversal, uSamples, vSamples),
        "SIMPLIFY: reduction commutes with reversing the direction — the removal has no handedness", printPassing);
    recordCheck(passCount, failures, abs(reducedV.deviation - throughReversal.deviation) < 1e-9 * meter,
        "SIMPLIFY: and it reports the same deviation either way round (got " ~ toString(reducedV.deviation) ~
        " and " ~ toString(throughReversal.deviation) ~ ")", printPassing);

    // ---- The same property stated on symmetric data, where it is checkable by eye ----
    //
    // makeMirrorSymmetricSurfaceFixture is a parabola on [0,0,0,0,1,1,2,2,2,2]: degree 3 with the
    // interior knot at multiplicity 2, so removing it is exactly the even-window case, and it is
    // structurally the elevated two-cube-face merge whose seam heal reported the hooked surface.
    //
    // The forward recurrence answers x = +0.2 for the surviving control point and the backward one
    // answers x = -0.2. Either alone puts a symmetric surface's control point off the symmetry plane
    // by 0.2 m; their midpoint is x = 0, which is where the geometry says it belongs. The reported
    // deviation halves with it — 0.4 m to 0.2 m — because that number is the displacement actually
    // incurred, and centring genuinely halves it rather than relocating it.
    const symmetricInput = normalizeSurfaceDefinition(makeMirrorSymmetricSurfaceFixture());
    const symmetricReduced = simplifySurfaceToControlPointCounts(symmetricInput, 5, 0);
    var symmetricNet = size(symmetricReduced.controlPoints) == 5;
    for (var rowIndex = 0; rowIndex < size(symmetricReduced.controlPoints); rowIndex += 1)
    {
        const mirrorIndex = size(symmetricReduced.controlPoints) - 1 - rowIndex;
        for (var columnIndex = 0; columnIndex < size(symmetricReduced.controlPoints[0]); columnIndex += 1)
        {
            const point = symmetricReduced.controlPoints[rowIndex][columnIndex];
            const mirrored = symmetricReduced.controlPoints[mirrorIndex][columnIndex];
            if (!pointsMatch(point, vector(-mirrored[0], mirrored[1], mirrored[2])))
            {
                symmetricNet = false;
            }
        }
    }
    recordCheck(passCount, failures, symmetricNet,
        "SIMPLIFY: an even-window removal on a mirror-symmetric net returns a mirror-symmetric net", printPassing);
    recordCheck(passCount, failures, knotVectorIsPalindromic(symmetricReduced.uKnots),
        "SIMPLIFY: and a palindromic knot vector", printPassing);
    recordCheck(passCount, failures, abs(symmetricReduced.deviation - 0.2 * meter) < 1e-9 * meter,
        "SIMPLIFY: the centred removal reports HALF the gap between the two recurrences, because that " ~
        "is what it actually cost (got " ~ toString(symmetricReduced.deviation) ~ ", expected 0.2 m)", printPassing);
}

// ============================================================================================
// SURFACE-DERIV — exact derivative evaluation. Structured as three independent tiers, because
// no one of them is sufficient:
//
//   (1) ANALYTIC POLYNOMIAL. A patch built to be S(u,v) = (u, v, u^2 + v^2) exactly, so every
//       derivative is known in closed form and checked to full tolerance. Catches any error in
//       the basis-derivative recurrence (A2.3), which is where the dense index arithmetic is.
//   (2) ANALYTIC RATIONAL. A circle's tangent is perpendicular to its radius — a property no
//       weights-dropping implementation can fake, since the unweighted control polygon's curve
//       is a different curve with a different tangent. This is the same class of anchor that
//       finally settled the evaluateSpline weights bug, and it is checked to zero, not to a
//       tolerance on a magnitude. Curvature on the same cylinder is equally analytic: Gaussian
//       exactly 0, minimum radius exactly r.
//   (3) FINITE DIFFERENCE, on a fixture whose weights vary in BOTH directions. Tiers 1 and 2
//       between them never exercise the mixed rational terms of A4.4 — the paraboloid is not
//       rational, and the cylinder's weight function is independent of v, so every w^(0,j) and
//       w^(i,j) term vanishes on it. A dropped or mis-weighted binomial in that recursion would
//       survive both. Tolerances here are deliberately loose (1e-4 / 1e-3 relative): the target
//       is a missing TERM, which is an error of order 1, not the last digit of a difference
//       quotient.
// ============================================================================================

/**
 * A degree-(2,2) Bezier patch that IS S(u, v) = (u, v, u^2 + v^2) on [0,1]^2, exactly.
 *
 * Construction, so it can be re-derived rather than trusted: on the degree-2 Bernstein basis the
 * coefficients of `u` are [0, 0.5, 1] and of `u^2` are [0, 0, 1]. Setting the x component of
 * every control point from its ROW index makes the x sum telescope to `u` by partition of unity
 * over the v basis, and symmetrically for y from the column index; the z component is the sum of
 * the two square-coefficient vectors, giving u^2 + v^2.
 */
function makeAnalyticParaboloidPatchFixture() returns map
{
    const linearCoefficients = [0, 0.5, 1];
    const squareCoefficients = [0, 0, 1];
    var controlPoints = makeArray(3, 0);
    for (var rowIndex = 0; rowIndex < 3; rowIndex += 1)
    {
        var row = makeArray(3, vector(0, 0, 0) * centimeter);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            row[columnIndex] = vector(linearCoefficients[rowIndex], linearCoefficients[columnIndex],
                    squareCoefficients[rowIndex] + squareCoefficients[columnIndex]) * centimeter;
        }
        controlPoints[rowIndex] = row;
    }
    return {
            "uDegree" : 2,
            "vDegree" : 2,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : [0, 0, 0, 1, 1, 1],
            "vKnots" : [0, 0, 0, 1, 1, 1]
        };
}

/** makeClampedSurfaceFixture given weights that vary in BOTH directions, so the rational
    quotient rule's mixed terms are actually loaded. No analytic form — it exists purely as a
    finite-difference subject, which needs none. All weights are positive by inspection
    (minimum 0.4 at row 0, column 3). */
function makeBidirectionalRationalSurfaceFixture() returns map
{
    var fixture = makeClampedSurfaceFixture();
    var weights = makeArray(6, 0);
    for (var rowIndex = 0; rowIndex < 6; rowIndex += 1)
    {
        var weightRow = makeArray(4, 1);
        for (var columnIndex = 0; columnIndex < 4; columnIndex += 1)
        {
            weightRow[columnIndex] = 1 + 0.3 * rowIndex - 0.2 * columnIndex + 0.1 * rowIndex * columnIndex;
        }
        weights[rowIndex] = weightRow;
    }
    fixture.weights = weights;
    fixture.isRational = true;
    return fixture;
}

/** Relative agreement between two length Vectors, scaled by the larger magnitude. For
    finite-difference comparisons only — exact equality is meaningless against a difference
    quotient, and an absolute tolerance would be wrong at both ends of the scale range. */
function vectorsAgreeRelatively(actual is Vector, expected is Vector, relativeTolerance is number) returns boolean
{
    const scale = max(norm(actual), norm(expected));
    if (scale / meter == 0)
    {
        return true; // both exactly zero, which agrees at any tolerance
    }
    return norm(actual - expected) <= relativeTolerance * scale;
}

function runSurfaceDerivativeVector(passCount is box, failures is box, printPassing is boolean)
{
    // ---- Tier 1: the analytic polynomial patch ----
    const paraboloid = makeAnalyticParaboloidPatchFixture();
    var analyticMatches = true;
    var thirdOrderVanishes = true;
    for (var uParameter in [0, 0.3, 0.5, 1])
    {
        for (var vParameter in [0, 0.25, 0.7, 1])
        {
            const derivatives = evaluateBSplineSurfaceDerivatives(paraboloid, uParameter, vParameter, 3, 3);
            if (!pointsMatch(derivatives[0][0], vector(uParameter, vParameter, uParameter * uParameter + vParameter * vParameter) * centimeter) ||
                !pointsMatch(derivatives[1][0], vector(1, 0, 2 * uParameter) * centimeter) ||
                !pointsMatch(derivatives[0][1], vector(0, 1, 2 * vParameter) * centimeter) ||
                !pointsMatch(derivatives[2][0], vector(0, 0, 2) * centimeter) ||
                !pointsMatch(derivatives[0][2], vector(0, 0, 2) * centimeter) ||
                !pointsMatch(derivatives[1][1], vector(0, 0, 0) * centimeter))
            {
                analyticMatches = false;
            }
            // Order 3 exceeds degree 2, so it is identically zero — not approximately.
            if (!pointsMatch(derivatives[3][0], vector(0, 0, 0) * centimeter) ||
                !pointsMatch(derivatives[0][3], vector(0, 0, 0) * centimeter))
            {
                thirdOrderVanishes = false;
            }
        }
    }
    recordCheck(passCount, failures, analyticMatches,
        "SURFACE-DERIV: every derivative of S(u,v) = (u, v, u^2 + v^2) matches its closed form", printPassing);
    recordCheck(passCount, failures, thirdOrderVanishes,
        "SURFACE-DERIV: derivatives above the degree are exactly zero", printPassing);
    recordCheck(passCount, failures,
        pointsMatch(evaluateBSplineSurfaceDerivatives(paraboloid, 0.4, 0.6, 0, 0)[0][0],
            evaluateBSplineSurfacePoint(paraboloid, 0.4, 0.6)),
        "SURFACE-DERIV: order (0, 0) reproduces evaluateBSplineSurfacePoint", printPassing);

    // ---- Tier 2: the analytic rational cylinder ----
    // Circle in XY centered on the origin, swept along Z; the axis is the Z axis, so the radial
    // vector at any point is simply (x, y, 0).
    const cylinder = makeClosedClampedCylinderFixture();
    const radius = makeClosedClampedCircleFixture().radius * meter;
    const axialSpan = (0.4606121628299273 - 0.4039749626640555) * meter;

    var tangentIsPerpendicularToRadius = true;
    var axialTangentIsExact = true;
    var normalIsRadial = true;
    for (var uParameter in [0, 0.125, 0.25, 0.5, 0.75, 1])
    {
        for (var vParameter in [0, 0.5, 1])
        {
            const derivatives = evaluateBSplineSurfaceDerivatives(cylinder, uParameter, vParameter, 1, 1);
            const point = derivatives[0][0];
            // The component of the position perpendicular to the Z axis — the radius vector.
            // Written as a projection rather than by rebuilding a Vector component-wise, which
            // would mix a raw length into vector().
            const axis = vector(0, 0, 1);
            const radial = point - dot(point, axis) * axis;

            // |P|^2 = r^2 along the circle, so differentiating gives 2 P . P_u = 0 EXACTLY. An
            // implementation that dropped the weights would return the unweighted polygon's
            // tangent here, which is not perpendicular to anything in particular.
            if (abs(dot(derivatives[1][0], radial)) > 1e-9 * norm(derivatives[1][0]) * norm(radial))
            {
                tangentIsPerpendicularToRadius = false;
            }
            // V is a degree-1 sweep with column-independent weights, so S_v is the exact
            // difference of the two column heights.
            if (!pointsMatch(derivatives[0][1], vector(0 * meter, 0 * meter, axialSpan)))
            {
                axialTangentIsExact = false;
            }
            const normal = evaluateBSplineSurfaceNormal(cylinder, uParameter, vParameter);
            if (abs(abs(dot(normal, radial)) / norm(radial) - 1) > 1e-9)
            {
                normalIsRadial = false;
            }
        }
    }
    recordCheck(passCount, failures, tangentIsPerpendicularToRadius,
        "SURFACE-DERIV: the rational circle's tangent is perpendicular to its radius (weights are NOT dropped)", printPassing);
    recordCheck(passCount, failures, axialTangentIsExact,
        "SURFACE-DERIV: the cylinder's axial tangent is exactly the column height difference", printPassing);
    recordCheck(passCount, failures, normalIsRadial,
        "SURFACE-DERIV: the cylinder's normal is exactly radial", printPassing);

    var curvatureIsCylindrical = true;
    for (var uParameter in [0, 0.125, 0.375, 0.75])
    {
        const curvature = evaluateBSplineSurfaceCurvature(cylinder, uParameter, 0.5);
        const flattest = min(abs(curvature.principalCurvatures[0]), abs(curvature.principalCurvatures[1]));
        const sharpest = max(abs(curvature.principalCurvatures[0]), abs(curvature.principalCurvatures[1]));
        if (curvature.minimumRadius == undefined ||
            abs(curvature.gaussianCurvature) * radius * radius > 1e-9 ||
            flattest * radius > 1e-9 ||
            abs(sharpest * radius - 1) > 1e-9 ||
            abs(curvature.minimumRadius / radius - 1) > 1e-9)
        {
            curvatureIsCylindrical = false;
        }
    }
    recordCheck(passCount, failures, curvatureIsCylindrical,
        "SURFACE-DERIV: cylinder curvature is analytic — Gaussian 0, one principal 0, minimum radius r", printPassing);

    // ---- Tier 3: finite differences against weights that vary in both directions ----
    const generalRational = makeBidirectionalRationalSurfaceFixture();
    const firstStep = 1e-4;
    const secondStep = 1e-3;
    var firstDerivativesAgree = true;
    var secondDerivativesAgree = true;
    var mixedDerivativeAgrees = true;
    for (var uParameter in [0.2, 0.45, 0.8])
    {
        for (var vParameter in [0.25, 0.6])
        {
            const derivatives = evaluateBSplineSurfaceDerivatives(generalRational, uParameter, vParameter, 2, 2);

            const uForward = evaluateBSplineSurfacePoint(generalRational, uParameter + firstStep, vParameter);
            const uBackward = evaluateBSplineSurfacePoint(generalRational, uParameter - firstStep, vParameter);
            const vForward = evaluateBSplineSurfacePoint(generalRational, uParameter, vParameter + firstStep);
            const vBackward = evaluateBSplineSurfacePoint(generalRational, uParameter, vParameter - firstStep);
            if (!vectorsAgreeRelatively((uForward - uBackward) / (2 * firstStep), derivatives[1][0], 1e-4) ||
                !vectorsAgreeRelatively((vForward - vBackward) / (2 * firstStep), derivatives[0][1], 1e-4))
            {
                firstDerivativesAgree = false;
            }

            const centre = evaluateBSplineSurfacePoint(generalRational, uParameter, vParameter);
            const uForwardWide = evaluateBSplineSurfacePoint(generalRational, uParameter + secondStep, vParameter);
            const uBackwardWide = evaluateBSplineSurfacePoint(generalRational, uParameter - secondStep, vParameter);
            const vForwardWide = evaluateBSplineSurfacePoint(generalRational, uParameter, vParameter + secondStep);
            const vBackwardWide = evaluateBSplineSurfacePoint(generalRational, uParameter, vParameter - secondStep);
            if (!vectorsAgreeRelatively((uForwardWide - 2 * centre + uBackwardWide) / (secondStep * secondStep), derivatives[2][0], 1e-3) ||
                !vectorsAgreeRelatively((vForwardWide - 2 * centre + vBackwardWide) / (secondStep * secondStep), derivatives[0][2], 1e-3))
            {
                secondDerivativesAgree = false;
            }

            const plusPlus = evaluateBSplineSurfacePoint(generalRational, uParameter + secondStep, vParameter + secondStep);
            const plusMinus = evaluateBSplineSurfacePoint(generalRational, uParameter + secondStep, vParameter - secondStep);
            const minusPlus = evaluateBSplineSurfacePoint(generalRational, uParameter - secondStep, vParameter + secondStep);
            const minusMinus = evaluateBSplineSurfacePoint(generalRational, uParameter - secondStep, vParameter - secondStep);
            if (!vectorsAgreeRelatively((plusPlus - plusMinus - minusPlus + minusMinus) / (4 * secondStep * secondStep),
                    derivatives[1][1], 1e-3))
            {
                mixedDerivativeAgrees = false;
            }
        }
    }
    recordCheck(passCount, failures, firstDerivativesAgree,
        "SURFACE-DERIV: S_u and S_v match central differences on bidirectionally-weighted rational input", printPassing);
    recordCheck(passCount, failures, secondDerivativesAgree,
        "SURFACE-DERIV: S_uu and S_vv match second differences on the same", printPassing);
    recordCheck(passCount, failures, mixedDerivativeAgrees,
        "SURFACE-DERIV: S_uv matches its mixed difference — the A4.4 cross terms tiers 1 and 2 cannot reach", printPassing);

    // ---- Curve sibling: the same rational anchor, one dimension down ----
    const circle = makeClosedClampedCircleFixture();
    var curveTangentIsPerpendicular = true;
    var curveStaysOnCircle = true;
    for (var parameter in [0, 0.125, 0.25, 0.5, 0.875])
    {
        const derivatives = evaluateBSplineCurveDerivatives(circle, parameter, 2);
        if (abs(norm(derivatives[0]) / (circle.radius * meter) - 1) > 1e-9)
        {
            curveStaysOnCircle = false;
        }
        if (abs(dot(derivatives[1], derivatives[0])) > 1e-9 * norm(derivatives[1]) * norm(derivatives[0]))
        {
            curveTangentIsPerpendicular = false;
        }
    }
    recordCheck(passCount, failures, curveStaysOnCircle,
        "SURFACE-DERIV: evaluateBSplineCurveDerivatives order 0 lands on the circle, radius exact", printPassing);
    recordCheck(passCount, failures, curveTangentIsPerpendicular,
        "SURFACE-DERIV: the rational circle CURVE's tangent is perpendicular to its radius", printPassing);
}

// ============================================================================================
// Periodic-preserving refinement — the highest-risk new code this pass. Two checks matter
// per vector: (1) the OVERLAP CONDITION (controlPoints[i] == controlPoints[n+i] for
// i = 0..degree-1) holds EXPLICITLY, not just assumed as a consequence of the design — this is
// the crux the whole three-period-window technique rests on; (2) geometry is preserved, using
// evaluatePeriodicCurvePoints (NOT evaluateCurvePoints, which always forces isPeriodic:false -
// here we WANT the periodic evaluation path, both because that is what we are testing and
// because bSplineCurve's own constructor throws if the overlap condition is broken but only
// partially so - controlPointsNeedsOverlap rejects "0 or degree matches, nothing between" - a
// free, kernel-level sanity check on top of the explicit one). Every vector is wrapped in
// try/catch so a bug in this new code reports as a clear failure rather than silently
// preventing every vector after it from running at all.
//
// EVERY periodic vector must include a degree >= 2 case. Degree 1 is degenerate for exactly
// the property under test: degree-1 control points lie ON the curve, so a broken clamp-based
// extraction is value-neutral there and passes anyway — a clamp-vs-slice extraction bug in the
// module survived green degree-1 REFINE and SHARE runs and was only exposed by the degree-2
// ELEVATE vector. Degree-1 cases are kept as simple hand-checkable baselines, never as the
// sole coverage.
// ============================================================================================

/**
 * Stored-form periodic degree-1 triangle: 3 fundamental points + 1 overlap, uniform knots.
 * Degree 1 is the degenerate baseline (see the block header) - every number is hand-checkable,
 * but it is never sufficient coverage on its own.
 */
function makePeriodicTriangleFixture() returns map
{
    const p0 = vector(0, 0, 0) * centimeter;
    const p1 = vector(4, 0, 0) * centimeter;
    const p2 = vector(2, 3, 0) * centimeter;
    return { "controlPoints" : [p0, p1, p2, p0], "knots" : [0, 1, 2, 3, 4, 5], "degree" : 1 };
}

/** Stored-form periodic degree-2 pentagon: 5 fundamental points + 2 overlap, uniform knots. */
function makePeriodicPentagonFixture() returns map
{
    var fundamentalPoints = makeArray(5, vector(0, 0, 0) * centimeter);
    for (var pointIndex = 0; pointIndex < 5; pointIndex += 1)
    {
        const angle = pointIndex * 2 * PI / 5 * radian;
        fundamentalPoints[pointIndex] = vector(3 * cos(angle), 3 * sin(angle), 0) * centimeter;
    }
    var controlPoints = makeArray(7, fundamentalPoints[0]);
    for (var pointIndex = 0; pointIndex < 7; pointIndex += 1)
    {
        controlPoints[pointIndex] = fundamentalPoints[pointIndex % 5];
    }
    return { "controlPoints" : controlPoints, "knots" : [-2, -1, 0, 1, 2, 3, 4, 5, 6, 7], "degree" : 2 };
}

/** Stored-form periodic degree-2 square: 4 fundamental points + 2 overlap, uniform knots,
    domain [2, 6] (period 4) - deliberately a DIFFERENT domain and period from the pentagon
    fixtures, so sharing knot vectors across them exercises the canonical-domain remap. */
function makePeriodicSquareFixture() returns map
{
    const f0 = vector(2, 2, 0) * centimeter;
    const f1 = vector(-2, 2, 0) * centimeter;
    const f2 = vector(-2, -2, 0) * centimeter;
    const f3 = vector(2, -2, 0) * centimeter;
    return { "controlPoints" : [f0, f1, f2, f3, f0, f1], "knots" : [0, 1, 2, 3, 4, 5, 6, 7, 8], "degree" : 2 };
}

/** True if controlPoints[i] == controlPoints[n + i] for i = 0..degree-1 (the overlap
    condition), where n = size(controlPoints) - degree. */
function overlapConditionHolds(controlPoints is array, degree is number) returns boolean
{
    const n = size(controlPoints) - degree;
    for (var overlapIndex = 0; overlapIndex < degree; overlapIndex += 1)
    {
        if (!pointsMatch(controlPoints[overlapIndex], controlPoints[n + overlapIndex]))
        {
            return false;
        }
    }
    return true;
}

/** Evaluate a STORED periodic curve at the given ABSOLUTE parameters, preserving
    isPeriodic:true (unlike evaluateCurvePoints, which always forces isPeriodic:false). Relies
    on std's own evaluateSpline/bSplineCurve to do the periodic evaluation - not something this
    module implements itself.

    NON-RATIONAL FIXTURES ONLY. The kernel builtin behind evaluateSpline silently IGNORES
    WEIGHTS (measured live 2026-08-08 - see evaluateRationalCurvePoints for the full account,
    including the false trail it laid first: the resulting mismatch was initially misread as
    the kernel mis-evaluating C0-seam wrap forms, a claim now retracted). Every caller of this
    function uses unit-weight fixtures, where the dropped weights change nothing. For rational
    content use evaluateRationalCurvePoints, which never touches the kernel. */
function evaluatePeriodicCurvePoints(controlPoints is array, knots is array, degree is number, parameters is array) returns array
{
    const curve = bSplineCurve({
                "degree" : degree,
                "isPeriodic" : true,
                "controlPoints" : controlPoints,
                "knots" : knotArray(knots)
            });
    return evaluateSpline({ "spline" : curve, "parameters" : parameters })[0];
}

function runPeriodicRefinementVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const fixture = makePeriodicTriangleFixture();
        // One insertion inside the first segment [P0, P1], at its exact midpoint (parameter
        // 1.5, since fundamental knots are [1, 2, 3] and the first segment spans [1, 2]).
        const refined = refinePeriodicPoints(fixture.controlPoints, fixture.knots, fixture.degree, [1.5]);

        recordCheck(passCount, failures, size(refined.controlPoints) == 5,
            "PERIODIC-REFINE: one insertion grows the stored triangle from 4 to 5 control points (got " ~ size(refined.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(refined.controlPoints, fixture.degree),
            "PERIODIC-REFINE: the overlap condition holds after refinement (controlPoints[0] == controlPoints[n])", printPassing);
        recordCheck(passCount, failures,
            pointsMatch(refined.controlPoints[1], 0.5 * fixture.controlPoints[0] + 0.5 * fixture.controlPoints[1]),
            "PERIODIC-REFINE: the inserted point is exactly the midpoint of the segment it splits", printPassing);

        const sampleParameters = [1, 1.5, 2, 2.5, 3, 3.5];
        const originalPoints = evaluatePeriodicCurvePoints(fixture.controlPoints, fixture.knots, fixture.degree, sampleParameters);
        const refinedPoints = evaluatePeriodicCurvePoints(refined.controlPoints, refined.knots, fixture.degree, sampleParameters);
        var geometryPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPoints[sampleIndex], refinedPoints[sampleIndex]))
            {
                geometryPreserved = false;
            }
        }
        recordCheck(passCount, failures, geometryPreserved,
            "PERIODIC-REFINE: geometry preserved (periodic evaluation) after refinement", printPassing);

        // Degree-2 pentagon - the non-degenerate case (see the block header: degree 1 cannot
        // catch extraction bugs). One insertion at 2.5, the midpoint of fundamental span [2, 3]
        // (fundamental knots [0..4], domain [0, 5]).
        const pentagon = makePeriodicPentagonFixture();
        const refinedPentagon = refinePeriodicPoints(pentagon.controlPoints, pentagon.knots, pentagon.degree, [2.5]);

        recordCheck(passCount, failures, size(refinedPentagon.controlPoints) == 8,
            "PERIODIC-REFINE: degree-2 insertion grows the stored pentagon from 7 to 8 control points (got " ~ size(refinedPentagon.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(refinedPentagon.controlPoints, pentagon.degree),
            "PERIODIC-REFINE: degree-2 overlap condition holds after refinement", printPassing);

        const pentagonParameters = [0, 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5.5];
        const originalPentagonPoints = evaluatePeriodicCurvePoints(pentagon.controlPoints, pentagon.knots, pentagon.degree, pentagonParameters);
        const refinedPentagonPoints = evaluatePeriodicCurvePoints(refinedPentagon.controlPoints, refinedPentagon.knots, pentagon.degree, pentagonParameters);
        var pentagonGeometryPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(pentagonParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPentagonPoints[sampleIndex], refinedPentagonPoints[sampleIndex]))
            {
                pentagonGeometryPreserved = false;
            }
        }
        recordCheck(passCount, failures, pentagonGeometryPreserved,
            "PERIODIC-REFINE: degree-2 geometry preserved (periodic evaluation) after refinement", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "PERIODIC-REFINE: threw an error: " ~ error, printPassing);
    }
}

function runPeriodicElevationVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const fixture = makePeriodicPentagonFixture();
        // elevatePeriodicPointsRaw wraps elevateHomogeneousPointsRaw and (as of the removeKnots
        // fix) removeKnots, both of which require true 4D homogeneous points - unlike
        // refinePeriodicPoints's pure Boehm-insertion math, removeKnots's equality check
        // (weightedPointsTolerantEquals -> separatePointsAndWeights) hard-codes index 3 as the
        // weight component, so plain 3D vectors are not enough here.
        const unitWeights = makeArray(size(fixture.controlPoints), 1);
        const homogeneousPoints = combinePointsAndWeights(fixture.controlPoints, unitWeights);
        const elevated = elevatePeriodicPointsRaw(homogeneousPoints, fixture.knots, fixture.degree, 4);
        const separatedElevated = separatePointsAndWeights(elevated.controlPoints);

        recordCheck(passCount, failures, overlapConditionHolds(separatedElevated.points, 4),
            "PERIODIC-ELEVATE: the overlap condition holds after elevation to degree 4", printPassing);

        const sampleParameters = [0, 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5];
        const originalPoints = evaluatePeriodicCurvePoints(fixture.controlPoints, fixture.knots, fixture.degree, sampleParameters);
        const elevatedPoints = evaluatePeriodicCurvePoints(separatedElevated.points, elevated.knots, 4, sampleParameters);
        var geometryPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPoints[sampleIndex], elevatedPoints[sampleIndex]))
            {
                geometryPreserved = false;
            }
        }
        recordCheck(passCount, failures, geometryPreserved,
            "PERIODIC-ELEVATE: geometry preserved (periodic evaluation) after elevating degree 2 -> 4", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "PERIODIC-ELEVATE: threw an error: " ~ error, printPassing);
    }
}

function runPeriodicShareKnotVectorVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        // Two periodic degree-1 curves with DIFFERENT fundamental counts (triangle vs.
        // pentagon-shaped, but degree 1 to keep this vector independent of the elevation one) -
        // share a knot vector requires both isPeriodic and matching degree; makeSplinesCompatible
        // elevates first when degrees differ, so use two already-equal-degree periodic splines
        // here to isolate the share-only path.
        const triangle = makePeriodicTriangleFixture();
        // Degree-1 pentagon fixture: stored count = n + degree = 5 + 1 = 6, knot count =
        // stored + degree + 1 = 8, built directly rather than via makePeriodicPentagonFixture
        // (which is degree 2) since this vector isolates the share-only path at matching degree.
        var fundamentalPentagon = makeArray(5, vector(0, 0, 0) * centimeter);
        for (var pointIndex = 0; pointIndex < 5; pointIndex += 1)
        {
            const angle = pointIndex * 2 * PI / 5 * radian;
            fundamentalPentagon[pointIndex] = vector(3 * cos(angle), 3 * sin(angle), 0) * centimeter;
        }
        var pentagonControlPoints = makeArray(6, fundamentalPentagon[0]);
        for (var pointIndex = 0; pointIndex < 6; pointIndex += 1)
        {
            pentagonControlPoints[pointIndex] = fundamentalPentagon[pointIndex % 5];
        }
        const pentagon = { "controlPoints" : pentagonControlPoints, "knots" : [0, 1, 2, 3, 4, 5, 6, 7], "degree" : 1 };

        const splineA = { "degree" : 1, "isPeriodic" : true, "controlPoints" : triangle.controlPoints, "knots" : knotArray(triangle.knots) };
        const splineB = { "degree" : 1, "isPeriodic" : true, "controlPoints" : pentagon.controlPoints, "knots" : knotArray(pentagon.knots) };

        const shared = makeSplinesShareKnotVector(splineA, splineB);

        recordCheck(passCount, failures, knotVectorsMatch(shared.a.knots, shared.b.knots),
            "PERIODIC-SHARE: both outputs land on an identical knot vector", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(shared.a.controlPoints, 1),
            "PERIODIC-SHARE: curve A's overlap condition holds after sharing", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(shared.b.controlPoints, 1),
            "PERIODIC-SHARE: curve B's overlap condition holds after sharing", printPassing);
        recordCheck(passCount, failures, shared.a.isPeriodic == true && shared.b.isPeriodic == true,
            "PERIODIC-SHARE: both outputs are still flagged periodic (not silently clamped)", printPassing);

        // makeSplinesShareKnotVector rescales each curve onto a canonical [0, 1)-period domain
        // (see makePeriodicSplinesShareKnotVector's own comment) - a pure reparameterization,
        // so "geometry unchanged" means matching at the same PROPORTIONAL position along each
        // curve's own period, not at the same literal absolute parameter. fractions span
        // slightly past one full period (up to 7/6) to also exercise wraparound.
        const fractions = [0, 1 / 6, 1 / 3, 0.5, 2 / 3, 5 / 6, 1, 7 / 6];
        var originalParametersA = makeArray(size(fractions), 0);
        var originalParametersB = makeArray(size(fractions), 0);
        for (var fractionIndex = 0; fractionIndex < size(fractions); fractionIndex += 1)
        {
            originalParametersA[fractionIndex] = 1 + fractions[fractionIndex] * 3; // triangle: domainStart 1, period 3
            originalParametersB[fractionIndex] = 1 + fractions[fractionIndex] * 5; // pentagon: domainStart 1, period 5
        }
        const originalAPoints = evaluatePeriodicCurvePoints(triangle.controlPoints, triangle.knots, 1, originalParametersA);
        const sharedAPoints = evaluatePeriodicCurvePoints(shared.a.controlPoints, shared.a.knots, 1, fractions);
        const originalBPoints = evaluatePeriodicCurvePoints(pentagon.controlPoints, pentagon.knots, 1, originalParametersB);
        const sharedBPoints = evaluatePeriodicCurvePoints(shared.b.controlPoints, shared.b.knots, 1, fractions);

        var aPreserved = true;
        var bPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(fractions); sampleIndex += 1)
        {
            if (!pointsMatch(originalAPoints[sampleIndex], sharedAPoints[sampleIndex]))
            {
                aPreserved = false;
            }
            if (!pointsMatch(originalBPoints[sampleIndex], sharedBPoints[sampleIndex]))
            {
                bPreserved = false;
            }
        }
        recordCheck(passCount, failures, aPreserved,
            "PERIODIC-SHARE: curve A's geometry is unchanged after sharing the knot vector", printPassing);
        recordCheck(passCount, failures, bPreserved,
            "PERIODIC-SHARE: curve B's geometry is unchanged after sharing the knot vector", printPassing);

        // Degree-2 case - non-degenerate extraction (see block header), plus genuinely
        // different domains AND periods (pentagon [0, 5], square [2, 6]), exercising the
        // canonical-domain remap for real.
        const pentagonD2 = makePeriodicPentagonFixture();
        const square = makePeriodicSquareFixture();
        const splineC = { "degree" : 2, "isPeriodic" : true, "controlPoints" : pentagonD2.controlPoints, "knots" : knotArray(pentagonD2.knots) };
        const splineD = { "degree" : 2, "isPeriodic" : true, "controlPoints" : square.controlPoints, "knots" : knotArray(square.knots) };
        const sharedD2 = makeSplinesShareKnotVector(splineC, splineD);

        recordCheck(passCount, failures, knotVectorsMatch(sharedD2.a.knots, sharedD2.b.knots),
            "PERIODIC-SHARE: degree-2 outputs land on an identical knot vector", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(sharedD2.a.controlPoints, 2),
            "PERIODIC-SHARE: degree-2 curve A's overlap condition holds after sharing", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(sharedD2.b.controlPoints, 2),
            "PERIODIC-SHARE: degree-2 curve B's overlap condition holds after sharing", printPassing);

        var originalParametersC = makeArray(size(fractions), 0);
        var originalParametersD = makeArray(size(fractions), 0);
        for (var fractionIndex = 0; fractionIndex < size(fractions); fractionIndex += 1)
        {
            originalParametersC[fractionIndex] = 0 + fractions[fractionIndex] * 5; // pentagon: domainStart 0, period 5
            originalParametersD[fractionIndex] = 2 + fractions[fractionIndex] * 4; // square: domainStart 2, period 4
        }
        const originalCPoints = evaluatePeriodicCurvePoints(pentagonD2.controlPoints, pentagonD2.knots, 2, originalParametersC);
        const sharedCPoints = evaluatePeriodicCurvePoints(sharedD2.a.controlPoints, sharedD2.a.knots, 2, fractions);
        const originalDPoints = evaluatePeriodicCurvePoints(square.controlPoints, square.knots, 2, originalParametersD);
        const sharedDPoints = evaluatePeriodicCurvePoints(sharedD2.b.controlPoints, sharedD2.b.knots, 2, fractions);

        var cPreserved = true;
        var dPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(fractions); sampleIndex += 1)
        {
            if (!pointsMatch(originalCPoints[sampleIndex], sharedCPoints[sampleIndex]))
            {
                cPreserved = false;
            }
            if (!pointsMatch(originalDPoints[sampleIndex], sharedDPoints[sampleIndex]))
            {
                dPreserved = false;
            }
        }
        recordCheck(passCount, failures, cPreserved,
            "PERIODIC-SHARE: degree-2 curve A's geometry is unchanged after sharing the knot vector", printPassing);
        recordCheck(passCount, failures, dPreserved,
            "PERIODIC-SHARE: degree-2 curve B's geometry is unchanged after sharing the knot vector", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "PERIODIC-SHARE: threw an error: " ~ error, printPassing);
    }
}

// ============================================================================================
// REVERSE / REWINDOW — the two exact reparameterizations. Both change only WHICH parameter
// labels which point, never the geometry, which is what lets tweenCurves choose a
// correspondence between two closed curves without approximating anything.
// ============================================================================================

function runReverseSplineVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        // Clamped case: reversing maps parameter t to -t, so the reversed curve at -t must be
        // the original at t.
        const degree = 3;
        const knots = makeClampedCubicKnots();
        const fixturePoints = makeSixPointFixture();
        const spline = { "degree" : degree, "isPeriodic" : false, "controlPoints" : fixturePoints, "knots" : knotArray(knots) };
        const reversed = reverseSpline(spline);

        recordCheck(passCount, failures, size(reversed.controlPoints) == size(fixturePoints),
            "REVERSE: control point count is unchanged", printPassing);
        recordCheck(passCount, failures, pointsMatch(reversed.controlPoints[0], fixturePoints[size(fixturePoints) - 1]),
            "REVERSE: the reversed curve's first control point is the original's last", printPassing);

        const sampleParameters = makeUnitSampleParameters();
        var mirroredParameters = makeArray(size(sampleParameters), 0);
        for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
        {
            mirroredParameters[sampleIndex] = -sampleParameters[sampleIndex];
        }
        const originalPoints = evaluateCurvePoints(fixturePoints, knots, degree, sampleParameters);
        const reversedPoints = evaluateCurvePoints(reversed.controlPoints, reversed.knots, degree, mirroredParameters);
        var clampedGeometryPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPoints[sampleIndex], reversedPoints[sampleIndex]))
            {
                clampedGeometryPreserved = false;
            }
        }
        recordCheck(passCount, failures, clampedGeometryPreserved,
            "REVERSE: clamped geometry is preserved (reversed curve at -t equals original at t)", printPassing);

        // Periodic case: the overlap condition must survive, since control points are only
        // permuted. Degree 2, per the block header's degree >= 2 rule.
        const pentagon = makePeriodicPentagonFixture();
        const periodicSpline = { "degree" : 2, "isPeriodic" : true, "controlPoints" : pentagon.controlPoints, "knots" : knotArray(pentagon.knots) };
        const reversedPeriodic = reverseSpline(periodicSpline);

        recordCheck(passCount, failures, reversedPeriodic.isPeriodic == true,
            "REVERSE: a periodic spline stays periodic", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(reversedPeriodic.controlPoints, 2),
            "REVERSE: the overlap condition holds after reversing a periodic spline", printPassing);

        const periodicParameters = [0, 0.7, 1.4, 2.1, 2.8, 3.5, 4.2, 4.9];
        var mirroredPeriodicParameters = makeArray(size(periodicParameters), 0);
        for (var sampleIndex = 0; sampleIndex < size(periodicParameters); sampleIndex += 1)
        {
            mirroredPeriodicParameters[sampleIndex] = -periodicParameters[sampleIndex];
        }
        const originalPeriodicPoints = evaluatePeriodicCurvePoints(pentagon.controlPoints, pentagon.knots, 2, periodicParameters);
        const reversedPeriodicPoints = evaluatePeriodicCurvePoints(reversedPeriodic.controlPoints, reversedPeriodic.knots, 2, mirroredPeriodicParameters);
        var periodicGeometryPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(periodicParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPeriodicPoints[sampleIndex], reversedPeriodicPoints[sampleIndex]))
            {
                periodicGeometryPreserved = false;
            }
        }
        recordCheck(passCount, failures, periodicGeometryPreserved,
            "REVERSE: periodic geometry is preserved (reversed curve at -t equals original at t)", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "REVERSE: threw an error: " ~ error, printPassing);
    }
}

function runRewindowPeriodicSplineVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const pentagon = makePeriodicPentagonFixture(); // degree 2, fundamental knots [0..4], period 5
        const spline = { "degree" : 2, "isPeriodic" : true, "controlPoints" : pentagon.controlPoints, "knots" : knotArray(pentagon.knots) };

        // Case 1: seam moved ONTO an existing knot (3) - no insertion needed, so the control
        // point count must not grow.
        const onKnot = rewindowPeriodicSpline(spline, 3);
        recordCheck(passCount, failures, size(onKnot.controlPoints) == size(pentagon.controlPoints),
            "REWINDOW: moving the seam onto an existing knot does not add control points (got " ~ size(onKnot.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, abs(onKnot.knots[2] - 3) <= KNOT_PARAMETER_TOLERANCE,
            "REWINDOW: the new domain start is the requested seam parameter", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(onKnot.controlPoints, 2),
            "REWINDOW: the overlap condition holds after re-windowing onto a knot", printPassing);

        // Re-windowing relabels which window is stored; it does NOT shift parameter values, so
        // the curve evaluates identically at the same ABSOLUTE parameters.
        const sampleParameters = [3, 3.6, 4.2, 4.8, 5.4, 6, 6.6, 7.2];
        const originalPoints = evaluatePeriodicCurvePoints(pentagon.controlPoints, pentagon.knots, 2, sampleParameters);
        const onKnotPoints = evaluatePeriodicCurvePoints(onKnot.controlPoints, onKnot.knots, 2, sampleParameters);
        var onKnotGeometryPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPoints[sampleIndex], onKnotPoints[sampleIndex]))
            {
                onKnotGeometryPreserved = false;
            }
        }
        recordCheck(passCount, failures, onKnotGeometryPreserved,
            "REWINDOW: geometry is preserved at absolute parameters after re-windowing onto a knot", printPassing);

        // Case 2: seam moved BETWEEN knots (2.5) - one exact insertion, so exactly one more
        // control point, and the geometry still must not move.
        const offKnot = rewindowPeriodicSpline(spline, 2.5);
        recordCheck(passCount, failures, size(offKnot.controlPoints) == size(pentagon.controlPoints) + 1,
            "REWINDOW: moving the seam between knots adds exactly one control point (got " ~ size(offKnot.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, abs(offKnot.knots[2] - 2.5) <= KNOT_PARAMETER_TOLERANCE,
            "REWINDOW: the new domain start is the requested off-knot seam parameter", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(offKnot.controlPoints, 2),
            "REWINDOW: the overlap condition holds after re-windowing between knots", printPassing);

        const offKnotPoints = evaluatePeriodicCurvePoints(offKnot.controlPoints, offKnot.knots, 2, sampleParameters);
        var offKnotGeometryPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPoints[sampleIndex], offKnotPoints[sampleIndex]))
            {
                offKnotGeometryPreserved = false;
            }
        }
        recordCheck(passCount, failures, offKnotGeometryPreserved,
            "REWINDOW: geometry is preserved at absolute parameters after re-windowing between knots", printPassing);

        // A seam parameter outside the domain must wrap, landing on the same window as its
        // in-domain equivalent one period earlier.
        const wrapped = rewindowPeriodicSpline(spline, 2.5 + 5);
        recordCheck(passCount, failures, abs(wrapped.knots[2] - 2.5) <= KNOT_PARAMETER_TOLERANCE,
            "REWINDOW: a seam parameter beyond one period wraps into the domain", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "REWINDOW: threw an error: " ~ error, printPassing);
    }
}

/**
 * A "tight" periodic spline has FEWER control points per period than its degree (n <= degree).
 * These used to be rejected outright as degenerate, because the wide window hardcoded a
 * one-period margin, which is not enough for the clamped ends to stay clear of the core when a
 * period is that short. The margin is now computed as ceil((degree + 1) / n) periods, so these
 * take the identical exact path. Degree 3 with n = 3 needs a 2-period margin; degree 4 with
 * n = 2 needs 3.
 */
function runTightPeriodicSplineVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        // Degree 3, n = 3 (stored: 3 + 3 = 6 control points, 3 + 2*3 + 1 = 10 knots).
        const f0 = vector(3, 0, 0) * centimeter;
        const f1 = vector(-1.5, 2.6, 0) * centimeter;
        const f2 = vector(-1.5, -2.6, 0) * centimeter;
        const fundamentalPoints = [f0, f1, f2];
        var controlPoints = makeArray(6, f0);
        for (var pointIndex = 0; pointIndex < 6; pointIndex += 1)
        {
            controlPoints[pointIndex] = fundamentalPoints[pointIndex % 3];
        }
        const knots = [-3, -2, -1, 0, 1, 2, 3, 4, 5, 6];
        const degree = 3;

        const refined = refinePeriodicPoints(controlPoints, knots, degree, [1.5]);
        recordCheck(passCount, failures, size(refined.controlPoints) == 7,
            "TIGHT-PERIODIC: refining a degree-3, 3-point-per-period spline yields 7 control points (got " ~ size(refined.controlPoints) ~ ")", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(refined.controlPoints, degree),
            "TIGHT-PERIODIC: the overlap condition holds after refining a tight periodic spline", printPassing);

        const sampleParameters = [0, 0.4, 0.8, 1.2, 1.6, 2, 2.4, 2.8, 3.4];
        const originalPoints = evaluatePeriodicCurvePoints(controlPoints, knots, degree, sampleParameters);
        const refinedPoints = evaluatePeriodicCurvePoints(refined.controlPoints, refined.knots, degree, sampleParameters);
        var geometryPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPoints[sampleIndex], refinedPoints[sampleIndex]))
            {
                geometryPreserved = false;
            }
        }
        recordCheck(passCount, failures, geometryPreserved,
            "TIGHT-PERIODIC: geometry preserved after refining a tight periodic spline", printPassing);

        // Same fixture through degree elevation, which routes through the same wide window.
        const unitWeights = makeArray(size(controlPoints), 1);
        const homogeneousPoints = combinePointsAndWeights(controlPoints, unitWeights);
        const elevated = elevatePeriodicPointsRaw(homogeneousPoints, knots, degree, 5);
        const separatedElevated = separatePointsAndWeights(elevated.controlPoints);

        recordCheck(passCount, failures, overlapConditionHolds(separatedElevated.points, 5),
            "TIGHT-PERIODIC: the overlap condition holds after elevating a tight periodic spline to degree 5", printPassing);

        const elevatedPoints = evaluatePeriodicCurvePoints(separatedElevated.points, elevated.knots, 5, sampleParameters);
        var elevationPreserved = true;
        for (var sampleIndex = 0; sampleIndex < size(sampleParameters); sampleIndex += 1)
        {
            if (!pointsMatch(originalPoints[sampleIndex], elevatedPoints[sampleIndex]))
            {
                elevationPreserved = false;
            }
        }
        recordCheck(passCount, failures, elevationPreserved,
            "TIGHT-PERIODIC: geometry preserved after elevating a tight periodic spline degree 3 -> 5", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "TIGHT-PERIODIC: threw an error: " ~ error, printPassing);
    }
}

/**
 * periodicRefinementOperator is a deliberate second implementation of refinePeriodicPoints'
 * math - operator form for surface grids (one build amortized over every row), direct insertion
 * for single arrays. Two implementations of one result is a standing correctness risk, so the
 * first thing checked here is that they agree EXACTLY, on every fixture, not merely that each
 * looks plausible on its own.
 */
function runPeriodicOperatorVector(passCount is box, failures is box, printPassing is boolean)
{
    try
    {
        const fixtures = [
                { "fixture" : makePeriodicTriangleFixture(), "insertions" : [1.5], "label" : "degree-1 triangle" },
                { "fixture" : makePeriodicPentagonFixture(), "insertions" : [2.5], "label" : "degree-2 pentagon" },
                { "fixture" : makePeriodicPentagonFixture(), "insertions" : [0.5, 2.5, 3.5], "label" : "degree-2 pentagon, three insertions" },
                { "fixture" : makePeriodicSquareFixture(), "insertions" : [3.5], "label" : "degree-2 square" }
            ];

        for (var caseIndex = 0; caseIndex < size(fixtures); caseIndex += 1)
        {
            const fixture = fixtures[caseIndex].fixture;
            const insertions = fixtures[caseIndex].insertions;
            const label = fixtures[caseIndex].label;
            const degree = fixture.degree;

            const direct = refinePeriodicPoints(fixture.controlPoints, fixture.knots, degree, insertions);
            const refinementOperator = periodicRefinementOperator(fixture.knots, degree, insertions);
            const viaOperator = applyKnotRefinementOperator(refinementOperator, fixture.controlPoints);

            recordCheck(passCount, failures, refinementOperator.inputCount == size(fixture.controlPoints),
                "PERIODIC-OPERATOR: " ~ label ~ ": operator consumes the stored control point count", printPassing);
            recordCheck(passCount, failures, size(viaOperator) == size(direct.controlPoints),
                "PERIODIC-OPERATOR: " ~ label ~ ": operator and direct insertion agree on control point count", printPassing);
            recordCheck(passCount, failures, knotVectorsMatch(refinementOperator.knots, direct.knots),
                "PERIODIC-OPERATOR: " ~ label ~ ": operator and direct insertion agree on the knot vector", printPassing);

            var pointsAgree = size(viaOperator) == size(direct.controlPoints);
            if (pointsAgree)
            {
                for (var pointIndex = 0; pointIndex < size(viaOperator); pointIndex += 1)
                {
                    if (!pointsMatch(viaOperator[pointIndex], direct.controlPoints[pointIndex]))
                    {
                        pointsAgree = false;
                    }
                }
            }
            recordCheck(passCount, failures, pointsAgree,
                "PERIODIC-OPERATOR: " ~ label ~ ": operator and direct insertion agree on every control point", printPassing);
            recordCheck(passCount, failures, overlapConditionHolds(viaOperator, degree),
                "PERIODIC-OPERATOR: " ~ label ~ ": the overlap condition holds in the operator's output", printPassing);

            // Rows must still be convex combinations - the fold onto stored indices accumulates
            // several window weights into one column, so this is a real check, not a formality.
            var rowsAreConvex = true;
            for (var rowIndex = 0; rowIndex < refinementOperator.outputCount; rowIndex += 1)
            {
                var weightSum = 0;
                for (var term in refinementOperator.rows[rowIndex])
                {
                    if (term.weight < -1e-10)
                    {
                        rowsAreConvex = false;
                    }
                    weightSum += term.weight;
                }
                if (abs(weightSum - 1) > 1e-9)
                {
                    rowsAreConvex = false;
                }
            }
            recordCheck(passCount, failures, rowsAreConvex,
                "PERIODIC-OPERATOR: " ~ label ~ ": every folded operator row is a convex combination", printPassing);
        }

        // The whole point of the operator form: one build, applied to many rows. Applying it
        // down the columns of a two-row grid must match applying it to each row separately.
        const pentagon = makePeriodicPentagonFixture();
        var secondRow = makeArray(size(pentagon.controlPoints), vector(0, 0, 0) * centimeter);
        for (var pointIndex = 0; pointIndex < size(pentagon.controlPoints); pointIndex += 1)
        {
            secondRow[pointIndex] = pentagon.controlPoints[pointIndex] + vector(0, 0, 4) * centimeter;
        }
        const gridOperator = periodicRefinementOperator(pentagon.knots, pentagon.degree, [2.5]);
        const refinedGrid = applyKnotRefinementOperatorAcrossRows(gridOperator, [pentagon.controlPoints, secondRow]);
        const refinedFirst = applyKnotRefinementOperator(gridOperator, pentagon.controlPoints);
        const refinedSecond = applyKnotRefinementOperator(gridOperator, secondRow);

        var gridAgrees = size(refinedGrid) == 2 && size(refinedGrid[0]) == size(refinedFirst);
        if (gridAgrees)
        {
            for (var pointIndex = 0; pointIndex < size(refinedFirst); pointIndex += 1)
            {
                if (!pointsMatch(refinedGrid[0][pointIndex], refinedFirst[pointIndex]) ||
                    !pointsMatch(refinedGrid[1][pointIndex], refinedSecond[pointIndex]))
                {
                    gridAgrees = false;
                }
            }
        }
        recordCheck(passCount, failures, gridAgrees,
            "PERIODIC-OPERATOR: one operator applied across a grid's rows matches per-row application", printPassing);
        recordCheck(passCount, failures, overlapConditionHolds(refinedGrid[1], pentagon.degree),
            "PERIODIC-OPERATOR: the overlap condition holds in every refined grid row", printPassing);
    }
    catch (error)
    {
        recordCheck(passCount, failures, false, "PERIODIC-OPERATOR: threw an error: " ~ error, printPassing);
    }
}
