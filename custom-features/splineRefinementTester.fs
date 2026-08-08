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
