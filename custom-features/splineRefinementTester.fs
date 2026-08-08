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
                 (std evaluateSpline on BSplineCurve, pure, no Context needed).
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

/** U degree 2, uniform periodic-style knots [0..8] (domain [2, 6], with genuine wrap-padding
    structure beyond it - the same shape that exposed the findKnotSpanIndex bug for curves), 6
    rows; V degree 2, clamped Bezier, 3 columns. isUPeriodic:true, isVPeriodic:false - tests
    that normalizeSurfaceDefinition clamps ONLY the periodic direction. */
function makeUPeriodicSurfaceFixture() returns map
{
    var controlPoints = makeArray(6, 0);
    for (var rowIndex = 0; rowIndex < 6; rowIndex += 1)
    {
        var row = makeArray(3, vector(0, 0, 0) * centimeter);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            row[columnIndex] = vector(rowIndex, columnIndex, rowIndex - columnIndex) * centimeter;
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
// SURFACE-NORM — normalizeSurfaceDefinition: force-rational, per-direction periodic clamping
// ============================================================================================

function runSurfaceNormalizationVector(passCount is box, failures is box, printPassing is boolean)
{
    const fixture = makeUPeriodicSurfaceFixture();
    const normalized = normalizeSurfaceDefinition(fixture);

    recordCheck(passCount, failures, normalized.isRational == true,
        "SURFACE-NORM: a non-rational surface becomes isRational:true", printPassing);
    recordCheck(passCount, failures, normalized.isUPeriodic == false,
        "SURFACE-NORM: the U-periodic direction is clamped to isUPeriodic:false", printPassing);
    recordCheck(passCount, failures, normalized.isVPeriodic == false,
        "SURFACE-NORM: V was already non-periodic and stays that way", printPassing);
    recordCheck(passCount, failures, normalized.wasClampedFromPeriodic == true,
        "SURFACE-NORM: reports wasClampedFromPeriodic when U needed clamping", printPassing);

    const uSamples = [2, 3.5, 4.5, 6]; // U domain [2, 6], matching the curve NORM vector
    const vSamples = [0, 0.5, 1];
    var geometryPreserved = true;
    for (var uParameter in uSamples)
    {
        for (var vParameter in vSamples)
        {
            const literalPoint = evaluateSurfaceLiterally(fixture, uParameter, vParameter);
            const clampedPoint = evaluateBSplineSurfacePoint(normalized, uParameter, vParameter);
            if (!pointsMatch(literalPoint, clampedPoint))
            {
                geometryPreserved = false;
            }
        }
    }
    recordCheck(passCount, failures, geometryPreserved,
        "SURFACE-NORM: periodic clamp reproduces the original array's own geometry on its domain", printPassing);
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
    module implements itself. */
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
