FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0"); // BSplineSurface, Cylinder, Cone, Sphere, Torus types
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");   // Line, Circle, Ellipse, BSplineCurve types

// Non-standard import: splineRefinementUtils.fs (custom-features/splineRefinementUtils.fs in the
// repository), imported as the PUBLISHED cross-document pin. Supplies normalizeSurfaceDefinition,
// evaluateBSplineSurfacePoint, and evaluateBSplineSurfaceDerivatives (the rational-aware
// derivative rectangle the point inversion consumes). Bump the version id on republish.
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * SOLID SWEEP - extraction layer (spec: docs/specs/SOLID_SWEEP_SPEC.md section 5). Reads the
 * tool body's faces, edges, and vertices ONCE into context-free records so that everything
 * downstream of extraction runs in pure math with zero ev-calls in any hot loop.
 *
 * Units contract: every spline stored in a record is unit-stripped - control points are plain
 * numbers with meters implied - and all residuals and tolerances in this module are plain
 * numbers in meters. Analytic surface definitions are stored as the typed values the evaluator
 * returns (units intact), because the closed-form solver consumes them symbolically.
 *
 * UV contract: two conventions exist. Solver UV lives in each extracted spline's own knot
 * domain. The kernel reports face UV in ONE shared convention - normalized to the face's
 * parameter-space bounding box - used identically by evFaceTangentPlane(s) and evDistance
 * (planes and meshes report zero vectors from evDistance). Crossings go through the
 * UvCalibration of each record: an affine map where the held-out residual proves one exists,
 * and 3D point inversion (invertPointOnSurface) where it does not.
 *
 * The self-test feature at the top is this module's live tester. It is selection-free - it
 * builds its own fixtures - so the MCP test harness can execute it in one call; per-module
 * *Tester.fs files cannot be used here because the harness compiles a single file.
 */

// ============================= Extraction Self Test =============================

annotation { "Feature Type Name" : "Sweep Emit - Extraction Self Test" }
export const sweepEmitExtractionSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // Three fixtures cover the three extraction paths: a cylinder solid (analytic classes,
        // no spline), a created bicubic patch (exact B-spline class), and an elliptical extrude
        // (no analytic class, storage-typed surface - the approximation path with trim loops).
        var failures = "";

        // Fixture 1: cylinder solid - expect two PLANE records and one CYLINDER record.
        fCylinder(context, id + "cylinder", {
                    "bottomCenter" : vector(0, 0, 0) * meter,
                    "topCenter" : vector(0, 0, 0.06) * meter,
                    "radius" : 0.02 * meter
                });
        const cylinderRecords = extractToolFaceRecords(context, qCreatedBy(id + "cylinder", EntityType.BODY), 1e-6);
        println("[EMIT SELF TEST] cylinder solid: " ~ summarizeFaceRecords(cylinderRecords));
        var planeCount = 0;
        var cylinderCount = 0;
        var cylinderWallIsPeriodic = false;
        for (var record in cylinderRecords)
        {
            if (record.surfaceClass == SweepSurfaceClass.PLANE)
            {
                planeCount += 1;
            }
            if (record.surfaceClass == SweepSurfaceClass.CYLINDER)
            {
                cylinderCount += 1;
                cylinderWallIsPeriodic = record.periodic[0] || record.periodic[1];
            }
        }
        if (planeCount != 2 || cylinderCount != 1)
        {
            failures = failures ~ " cylinder solid classified as " ~ planeCount ~ " PLANE / " ~
                cylinderCount ~ " CYLINDER (expected 2 / 1).";
        }
        if (!cylinderWallIsPeriodic)
        {
            failures = failures ~ " cylinder wall not reported periodic.";
        }

        // Fixture 2: created bicubic patch - expect one exact BSPLINE record whose calibration
        // is affine, and a clean inversion round trip.
        const rows = wavyBicubicPatchNet();
        opCreateBSplineSurface(context, id + "patch", {
                    "bSplineSurface" : bSplineSurface({
                                "uDegree" : 3,
                                "vDegree" : 3,
                                "isUPeriodic" : false,
                                "isVPeriodic" : false,
                                "controlPoints" : controlPointMatrix(rows)
                            })
                });
        const patchRecords = extractToolFaceRecords(context, qCreatedBy(id + "patch", EntityType.BODY), 1e-6);
        println("[EMIT SELF TEST] bicubic patch: " ~ summarizeFaceRecords(patchRecords));
        if (size(patchRecords) != 1 || patchRecords[0].surfaceClass != SweepSurfaceClass.BSPLINE ||
            !patchRecords[0].splineIsExact)
        {
            failures = failures ~ " patch did not extract as one exact BSPLINE record.";
        }
        else
        {
            if (patchRecords[0].calibration.isAffine != true)
            {
                failures = failures ~ " patch calibration not affine (residual " ~
                    patchRecords[0].calibration.maxFitResidual ~ " m).";
            }
            const patchInversion = inversionRoundTripWorstCase(patchRecords[0].spline);
            println("[EMIT SELF TEST] patch inversion round trip: worst 3D residual " ~
                patchInversion.worstResidual ~ " m in " ~ patchInversion.worstIterations ~ " iterations");
            if (patchInversion.worstResidual > 1e-9)
            {
                failures = failures ~ " patch inversion residual " ~ patchInversion.worstResidual ~ " m.";
            }
        }

        // Fixture 3: elliptical extrude - the wall has no analytic class and no spline storage,
        // so it must take the approximation path and carry trim loops; its calibration verdict
        // (affine or not) is recorded by the printout either way, and inversion must hold
        // regardless.
        const sketchId = id + "ellipseSketch";
        const ellipseSketch = newSketchOnPlane(context, sketchId, {
                    "sketchPlane" : plane(vector(0.25, 0, 0) * meter, vector(0, 0, 1))
                });
        skEllipse(ellipseSketch, "ellipse1", {
                    "center" : vector(0, 0) * meter,
                    "majorRadius" : 0.03 * meter,
                    "minorRadius" : 0.015 * meter
                });
        skSolve(ellipseSketch);
        opExtrude(context, id + "ellipseExtrude", {
                    "entities" : qSketchRegion(sketchId),
                    "direction" : vector(0, 0, 1),
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : 0.05 * meter
                });
        opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

        const extrudeRecords = extractToolFaceRecords(context, qCreatedBy(id + "ellipseExtrude", EntityType.BODY), 1e-6);
        println("[EMIT SELF TEST] elliptical extrude: " ~ summarizeFaceRecords(extrudeRecords));
        var wallRecord = undefined;
        for (var record in extrudeRecords)
        {
            if (record.surfaceClass != SweepSurfaceClass.PLANE)
            {
                wallRecord = record;
            }
        }
        if (wallRecord == undefined || wallRecord.surfaceClass != SweepSurfaceClass.OTHER ||
            wallRecord.spline == undefined)
        {
            failures = failures ~ " elliptical wall did not take the approximation path.";
        }
        else
        {
            println("[EMIT SELF TEST] elliptical wall spline: " ~ describeSurfaceShape(wallRecord.spline));
            println("[EMIT SELF TEST] elliptical wall calibration: isAffine " ~
                wallRecord.calibration.isAffine ~ ", max fit residual " ~
                wallRecord.calibration.maxFitResidual ~ " m, usable samples " ~
                wallRecord.calibration.usableSampleCount ~ "/" ~ wallRecord.calibration.sampleCount);
            const wallInversion = inversionRoundTripWorstCase(wallRecord.spline);
            println("[EMIT SELF TEST] elliptical wall inversion round trip: worst 3D residual " ~
                wallInversion.worstResidual ~ " m in " ~ wallInversion.worstIterations ~ " iterations");
            if (wallInversion.worstResidual > 1e-9)
            {
                failures = failures ~ " elliptical wall inversion residual " ~ wallInversion.worstResidual ~ " m.";
            }
            if (wallRecord.calibration.usableSampleCount < 4)
            {
                failures = failures ~ " elliptical wall calibration paired only " ~
                    wallRecord.calibration.usableSampleCount ~ " samples.";
            }
        }

        // Phase B: co-edge and vertex records over the same fixtures, plus a box whose
        // vertices exercise adjacency indexing and cone-normal assembly.
        const cylinderCoEdges = extractCoEdgeRecords(context, qCreatedBy(id + "cylinder", EntityType.BODY), cylinderRecords, 9);
        println("[EMIT SELF TEST] cylinder co-edges: " ~ summarizeCoEdgeRecords(cylinderCoEdges));
        if (size(cylinderCoEdges) != 2)
        {
            failures = failures ~ " cylinder produced " ~ size(cylinderCoEdges) ~ " co-edges (expected 2).";
        }
        for (var record in cylinderCoEdges)
        {
            if (record.curveClass != SweepCurveClass.CIRCLE || record.convexity != EdgeConvexityType.CONVEX ||
                record.faceIndexLeft == undefined || record.faceIndexRight == undefined)
            {
                failures = failures ~ " cylinder edge " ~ record.edgeIndex ~ " not a two-sided convex circle.";
            }
        }

        const patchCoEdges = extractCoEdgeRecords(context, qCreatedBy(id + "patch", EntityType.BODY), patchRecords, 9);
        println("[EMIT SELF TEST] patch co-edges: " ~ summarizeCoEdgeRecords(patchCoEdges));
        if (size(patchCoEdges) != 4)
        {
            failures = failures ~ " patch produced " ~ size(patchCoEdges) ~ " co-edges (expected 4).";
        }
        for (var record in patchCoEdges)
        {
            const sideCount = (record.faceIndexLeft == undefined ? 0 : 1) + (record.faceIndexRight == undefined ? 0 : 1);
            const pcurve = record.uvCurves.left != undefined ? record.uvCurves.left : record.uvCurves.right;
            if (sideCount != 1 || pcurve == undefined)
            {
                failures = failures ~ " patch edge " ~ record.edgeIndex ~ " not one-sided with a pcurve.";
            }
            else if (pcurve.maxResidual > 1e-8)
            {
                failures = failures ~ " patch edge " ~ record.edgeIndex ~ " pcurve residual " ~ pcurve.maxResidual ~ " m.";
            }
        }

        if (wallRecord != undefined)
        {
            const extrudeCoEdges = extractCoEdgeRecords(context, qCreatedBy(id + "ellipseExtrude", EntityType.BODY), extrudeRecords, 9);
            println("[EMIT SELF TEST] extrude co-edges: " ~ summarizeCoEdgeRecords(extrudeCoEdges));
            if (size(extrudeCoEdges) != 2)
            {
                failures = failures ~ " extrude produced " ~ size(extrudeCoEdges) ~ " co-edges (expected 2).";
            }
            for (var record in extrudeCoEdges)
            {
                var wallSidePcurve = undefined;
                if (record.faceIndexLeft == wallRecord.faceIndex)
                {
                    wallSidePcurve = record.uvCurves.left;
                }
                else if (record.faceIndexRight == wallRecord.faceIndex)
                {
                    wallSidePcurve = record.uvCurves.right;
                }
                if (record.convexity != EdgeConvexityType.CONVEX || wallSidePcurve == undefined)
                {
                    failures = failures ~ " extrude edge " ~ record.edgeIndex ~ " missing convexity or wall pcurve.";
                }
                else if (wallSidePcurve.maxResidual > 1e-8)
                {
                    failures = failures ~ " extrude edge " ~ record.edgeIndex ~ " wall pcurve residual " ~
                        wallSidePcurve.maxResidual ~ " m.";
                }
            }
        }

        fCuboid(context, id + "box", {
                    "corner1" : vector(-0.08, -0.04, 0) * meter,
                    "corner2" : vector(-0.03, 0, 0.05) * meter
                });
        const boxBody = qCreatedBy(id + "box", EntityType.BODY);
        const boxRecords = extractToolFaceRecords(context, boxBody, 1e-6);
        const boxCoEdges = extractCoEdgeRecords(context, boxBody, boxRecords, 9);
        const boxVertices = extractVertexRecords(context, boxBody, boxCoEdges);
        println("[EMIT SELF TEST] box: " ~ summarizeFaceRecords(boxRecords));
        println("[EMIT SELF TEST] box co-edges: " ~ summarizeCoEdgeRecords(boxCoEdges));
        println("[EMIT SELF TEST] box vertices: " ~ size(boxVertices));
        if (size(boxRecords) != 6 || size(boxCoEdges) != 12 || size(boxVertices) != 8)
        {
            failures = failures ~ " box counts " ~ size(boxRecords) ~ "/" ~ size(boxCoEdges) ~ "/" ~
                size(boxVertices) ~ " (expected 6/12/8).";
        }
        for (var record in boxCoEdges)
        {
            if (record.curveClass != SweepCurveClass.LINE || record.convexity != EdgeConvexityType.CONVEX ||
                record.faceIndexLeft == undefined || record.faceIndexRight == undefined ||
                record.faceIndexLeft == record.faceIndexRight)
            {
                failures = failures ~ " box edge " ~ record.edgeIndex ~ " not a two-sided convex line between distinct faces.";
            }
        }
        for (var record in boxVertices)
        {
            if (size(record.adjacentEdges) != 3 || size(record.coneNormals) != 3)
            {
                failures = failures ~ " box vertex " ~ record.vertexIndex ~ " has " ~ size(record.adjacentEdges) ~
                    " edges / " ~ size(record.coneNormals) ~ " cone normals (expected 3/3).";
            }
        }

        reportTestVerdict(context, id, "EMIT SELF TEST", failures,
            "faces classify on all fixtures; inversion at machine precision; co-edges carry class, " ~
            "convexity, sides, one-sided normals, and pcurves within tolerance; box vertices assemble " ~
            "3 edges and 3 cone normals each.");
    });

annotation { "Feature Type Name" : "Sweep Emit - Trim Loop Self Test" }
export const sweepEmitTrimLoopSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // Selection-free: every fixture is a hand-built 2D curve or sample array whose answer is
        // known in closed form, so the whole trim converter is checked without any geometry.
        var failures = "";

        // A square from four degree-1 curves, handed over out of order with one reversed. A
        // degree-1 curve is its own polyline, so this isolates the chaining.
        const corners = [vector(0.1, 0.2), vector(0.9, 0.2), vector(0.9, 0.8), vector(0.1, 0.8)];
        var squareCurves = makeArray(4);
        for (var index = 0; index < 4; index += 1)
        {
            squareCurves[index] = uvLineCurve(corners[index], corners[(index + 1) % 4]);
        }
        const scrambledSquare = [squareCurves[2], uvLineCurve(corners[1], corners[0]),
                squareCurves[3], squareCurves[1]];
        var squareSegments = makeArray(4);
        var squareBound = 0;
        for (var index = 0; index < 4; index += 1)
        {
            const sampled = polylineFromUvCurve(scrambledSquare[index], 1e-9);
            squareSegments[index] = sampled.points;
            squareBound = max(squareBound, sampled.certifiedBound);
            if (sampled.samplesPerSpan != 1)
            {
                failures = failures ~ " a degree-1 trim curve needed " ~ sampled.samplesPerSpan ~
                    " samples per span.";
            }
        }
        const squareLoops = chainUvPolylinesIntoLoops(squareSegments, 1e-9);
        println("[TRIM SELF TEST] scrambled square: " ~ size(squareLoops) ~ " loop(s), " ~
            (size(squareLoops) == 1 ? (size(squareLoops[0].points) ~ " points, gap " ~
                    squareLoops[0].closureGap) : "") ~ ", chord bound " ~ squareBound);
        if (size(squareLoops) != 1 || size(squareLoops[0].points) != 4 || !squareLoops[0].closed ||
            squareLoops[0].closureGap > 1e-15 || squareBound > 1e-15)
        {
            failures = failures ~ " scrambled square did not chain into one exact 4-point loop.";
        }

        // An exact rational-quadratic circle: the polyline bound must track the equal-angle
        // sagitta of the segment count it settled on, and must quarter when the count doubles.
        const circleCentre = vector(0.5, 0.5);
        const circleRadius = 0.25;
        const circleCurve = uvCircleCurve(circleCentre, circleRadius);
        var worstRadiusError = 0;
        for (var parameter in [0, 0.13, 0.37, 0.5, 0.9])
        {
            const onCurve = evaluateBSplineCurveDerivatives(circleCurve, parameter, 0)[0];
            worstRadiusError = max(worstRadiusError,
                abs(sqrt(squaredNorm(onCurve - circleCentre)) - circleRadius));
        }
        println("[TRIM SELF TEST] rational circle radius error at five parameters: " ~ worstRadiusError);
        if (worstRadiusError > 1e-14)
        {
            failures = failures ~ " the rational circle fixture is not exact (" ~ worstRadiusError ~ ").";
        }
        const circleTolerances = [1e-3, 1e-4, 1e-5];
        const expectedSegmentCounts = [64, 128, 512];
        var previousBound = 0;
        var previousSegments = 0;
        for (var toleranceIndex = 0; toleranceIndex < 3; toleranceIndex += 1)
        {
            const tolerance = circleTolerances[toleranceIndex];
            const sampled = polylineFromUvCurve(circleCurve, tolerance);
            const segmentCount = size(sampled.points) - 1;
            const equalAngleSagitta = circleRadius * (1 - cos(PI / segmentCount * radian));
            const sagittaRatio = sampled.certifiedBound / equalAngleSagitta;
            println("[TRIM SELF TEST] circle at tolerance " ~ tolerance ~ ": " ~ segmentCount ~
                " segments, bound " ~ sampled.certifiedBound ~ ", equal-angle sagitta " ~
                equalAngleSagitta ~ ", ratio " ~ sagittaRatio);
            if (segmentCount != expectedSegmentCounts[toleranceIndex] ||
                sampled.certifiedBound > tolerance || sagittaRatio < 1 || sagittaRatio > 1.15)
            {
                failures = failures ~ " circle polyline at tolerance " ~ tolerance ~
                    " gave " ~ segmentCount ~ " segments at bound " ~ sampled.certifiedBound ~ ".";
            }
            if (previousSegments > 0)
            {
                // Uniform-in-parameter refinement is second order, so the bound falls with the
                // square of the segment count whatever the parameterization does inside a span.
                const convergenceFactor = previousBound / sampled.certifiedBound /
                    ((segmentCount / previousSegments) * (segmentCount / previousSegments));
                println("[TRIM SELF TEST]   second-order convergence factor: " ~ convergenceFactor);
                if (abs(convergenceFactor - 1) > 0.02)
                {
                    failures = failures ~ " circle polyline refinement is not second order (" ~
                        convergenceFactor ~ ").";
                }
            }
            previousBound = sampled.certifiedBound;
            previousSegments = segmentCount;
        }

        // The whole converter on a face record: a square boundary with a circular hole, on the
        // approximation path, at the default tolerances for a unit uv domain.
        const holeRecord = {
                "faceIndex" : 0,
                "spline" : flatUnitDomainSurface(),
                "trimLoops" : { "boundary" : squareCurves, "inner" : [[circleCurve]] }
            };
        const holeTrim = buildFaceTrimLoops(holeRecord, [], {});
        println("[TRIM SELF TEST] square with a hole: " ~ summarizeTrimLoops(holeTrim));
        var holeLoopSizes = makeArray(size(holeTrim.loops), 0);
        for (var loopIndex = 0; loopIndex < size(holeTrim.loops); loopIndex += 1)
        {
            holeLoopSizes[loopIndex] = size(holeTrim.loops[loopIndex].points);
        }
        println("[TRIM SELF TEST]   loop sizes " ~ toString(holeLoopSizes));
        if (holeTrim.source != "approximation" || holeTrim.loopCount != 2 || !holeTrim.usable ||
            holeTrim.windingLoopCount != 0 || holeLoopSizes[0] != 4 || holeLoopSizes[1] != 64 ||
            holeTrim.worstClosureGap > 1e-15 || abs(holeTrim.worstCertifiedBound - 3.3454290863e-4) > 1e-12 ||
            abs(holeTrim.maxChordLength - 0.8) > 1e-15)
        {
            failures = failures ~ " the square-with-hole record did not convert to a 4-point and a " ~
                "64-point loop.";
        }

        // Seam-aware chaining at period 1: a trim that spans the whole seam has ends a period
        // apart, so a plain distance would call the loop open. Both sample densities matter -
        // eight segments, and the ONE chord the kernel can hand over for a degree-1 uv line,
        // which is the only legitimate two-point loop there is.
        for (var seamSegmentCount in [8, 1])
        {
            const wholeSeamLoops = chainUvPolylinesIntoLoops(
                    [uvArcSamples(0.2, 0, 1, seamSegmentCount), uvArcSamples(0.8, 0, 1, seamSegmentCount)],
                    1e-9, 1);
            var wholeSeamWindings = makeArray(size(wholeSeamLoops), 0);
            var wholeSeamSizes = makeArray(size(wholeSeamLoops), 0);
            var wholeSeamClosed = true;
            for (var loopIndex = 0; loopIndex < size(wholeSeamLoops); loopIndex += 1)
            {
                wholeSeamWindings[loopIndex] = wholeSeamLoops[loopIndex].winding;
                wholeSeamSizes[loopIndex] = size(wholeSeamLoops[loopIndex].points);
                wholeSeamClosed = wholeSeamClosed && wholeSeamLoops[loopIndex].closed;
            }
            println("[TRIM SELF TEST] whole-seam trims at " ~ seamSegmentCount ~ " segment(s): " ~
                size(wholeSeamLoops) ~ " loop(s), windings " ~ toString(wholeSeamWindings) ~
                ", sizes " ~ toString(wholeSeamSizes) ~ ", closed " ~ wholeSeamClosed);
            // A winding loop KEEPS its tail: it is the head one period along, and dropping it
            // would delete the segment that covers the seam.
            if (size(wholeSeamLoops) != 2 || !wholeSeamClosed ||
                wholeSeamWindings[0] != 1 || wholeSeamWindings[1] != 1 ||
                wholeSeamSizes[0] != seamSegmentCount + 1 || wholeSeamSizes[1] != seamSegmentCount + 1)
            {
                failures = failures ~ " whole-seam trim curves at " ~ seamSegmentCount ~
                    " segment(s) did not close as two winding loops keeping every sample.";
            }
        }

        // The same two trims split into arcs, shuffled, one reversed - the joins now land both
        // inside the domain and across the seam.
        const splitSeamLoops = chainUvPolylinesIntoLoops([uvArcSamples(0.2, 0, 0.5, 4),
                    uvArcSamples(0.8, 0.5, 1, 4), uvArcSamples(0.2, 1, 0.5, 4),
                    uvArcSamples(0.8, 0, 0.5, 4)], 1e-9, 1);
        var splitSeamWindingSum = 0;
        for (var splitLoop in splitSeamLoops)
        {
            splitSeamWindingSum += abs(splitLoop.winding);
        }
        println("[TRIM SELF TEST] split whole-seam trims: " ~ size(splitSeamLoops) ~
            " loop(s), total |winding| " ~ splitSeamWindingSum);
        if (size(splitSeamLoops) != 2 || splitSeamWindingSum != 2)
        {
            failures = failures ~ " split whole-seam trim arcs did not chain into two winding loops.";
        }

        // A hole straddling the seam, arriving as two pcurve pieces folded into the domain. The
        // per-segment unwrap plus the periodic joins must give one loop of span 0.1 that does
        // NOT wind - the loop is a hole, not a wrap.
        const foldedUpper = unwrapLoopU(foldedHoleSamples(0.05, 1, 8), 1);
        const foldedLower = unwrapLoopU(foldedHoleSamples(0.05, -1, 8), 1);
        const foldedLoops = chainUvPolylinesIntoLoops([foldedUpper, reverse(foldedLower)], 1e-9, 1);
        var foldedSpan = 0;
        var foldedWinding = 0;
        if (size(foldedLoops) == 1)
        {
            var spanMin = foldedLoops[0].points[0][0];
            var spanMax = foldedLoops[0].points[0][0];
            for (var loopPoint in foldedLoops[0].points)
            {
                spanMin = min(spanMin, loopPoint[0]);
                spanMax = max(spanMax, loopPoint[0]);
            }
            foldedSpan = spanMax - spanMin;
            foldedWinding = foldedLoops[0].winding;
        }
        println("[TRIM SELF TEST] seam-straddling hole: " ~ size(foldedLoops) ~ " loop(s), winding " ~
            foldedWinding ~ ", u span " ~ foldedSpan);
        if (size(foldedLoops) != 1 || foldedWinding != 0 || abs(foldedSpan - 0.1) > 1e-12)
        {
            failures = failures ~ " a seam-straddling hole did not chain into one 0.1-wide loop.";
        }

        // Folded input reads as no winding whichever kind of loop it is, which is why the
        // converter unwraps before it measures.
        var foldedRamp = makeArray(33, vector(0, 0.4));
        for (var index = 0; index < 33; index += 1)
        {
            foldedRamp[index] = vector(positiveModulo(0.3 + index / 32, 1), 0.4);
        }
        const foldedRampWinding = loopUWinding(foldedRamp, 1);
        const unwrappedRampWinding = loopUWinding(unwrapLoopU(foldedRamp, 1), 1);
        println("[TRIM SELF TEST] a folded winding ramp reads winding " ~ foldedRampWinding ~
            " folded and " ~ unwrappedRampWinding ~ " unwrapped");
        if (foldedRampWinding != 0 || unwrappedRampWinding != 1)
        {
            failures = failures ~ " unwrapping did not recover the winding of a folded ramp.";
        }

        reportTestVerdict(context, id, "TRIM LOOP SELF TEST", failures,
            "certified polyline conversion, seam-aware chaining, winding classification, " ~
            "and the record-level trim loop build all match their closed-form answers.");
    });

// ============================= Face extraction =============================

/**
 * Relative spread below which a weight grid counts as uniform, and absolute spread (meters
 * implied) below which a boundary control row counts as collapsed to a point. Both are read
 * off exact structure - a revolve's pole row is byte-identical, a non-rational net's weights
 * are exactly one - so the thresholds only have to survive arithmetic noise.
 */
export const UNIFORM_WEIGHT_TOLERANCE = 1e-12;
export const DEGENERATE_ROW_TOLERANCE = 1e-12;

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

/** Classification of a tool face's underlying surface, driving which solver path it takes. */
export enum SweepSurfaceClass
{
    PLANE,
    CYLINDER,
    CONE,
    SPHERE,
    TORUS,
    BSPLINE,
    OTHER
}

/**
 * Read every face of `toolBody` into a ToolFaceRecord. This is the once-per-build extraction
 * pass; downstream solver stages consume the records without touching the context.
 *
 * Each record: {
 *     faceIndex {number} : position in the returned array,
 *     faceQuery {Query} : transient query for the face (valid during this regeneration only),
 *     surfaceClass {SweepSurfaceClass},
 *     analytic {map} : the typed evSurfaceDefinition value for analytic classes, else undefined,
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
 */
export function extractToolFaceRecords(context is Context, toolBody is Query, faceExtractTolerance is number) returns array
{
    return extractToolFaceRecords(context, toolBody, faceExtractTolerance, false);
}

/**
 * Same, with control over whether freeform approximations are forced NON-RATIONAL.
 *
 * LIVE FINDING (2026-08-23): `forceNonRational` is not free, and on a periodic face it is not
 * even usable. Asked for the wall of an elliptical extrude at 1e-6, the kernel answers
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
 * rational-correct. Only swEnvelopeMath's coefficient path (spec 6.0) requires non-rational
 * input, and per spec 7.6 that path wants its own coarse extraction anyway. So the default is
 * FALSE, and a caller that turns it on owns the spelling problem.
 */
export function extractToolFaceRecords(context is Context, toolBody is Query, faceExtractTolerance is number,
    forceNonRational is boolean) returns array
{
    const faces = evaluateQuery(context, qOwnedByBody(toolBody, EntityType.FACE));
    var records = makeArray(size(faces));
    for (var faceIndex = 0; faceIndex < size(faces); faceIndex += 1)
    {
        const face = faces[faceIndex];
        const surfaceDefinition = evSurfaceDefinition(context, { "face" : face });
        const surfaceClass = classifySurfaceDefinition(surfaceDefinition);
        var record = {
            "faceIndex" : faceIndex,
            "faceQuery" : face,
            "surfaceClass" : surfaceClass,
            "analytic" : undefined,
            "spline" : undefined,
            "splineIsExact" : false,
            "periodic" : evFacePeriodicity(context, { "face" : face }),
            "trimLoops" : undefined,
            "calibration" : undefined,
            "degenerate" : undefined
        };
        if (surfaceClass == SweepSurfaceClass.BSPLINE)
        {
            record.spline = dropUniformWeights(stripSurfaceUnits(normalizeSurfaceDefinition(surfaceDefinition)));
            record.splineIsExact = true;
        }
        else if (surfaceClass == SweepSurfaceClass.OTHER)
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
            " periodic " ~ record.periodic[0] ~ "/" ~ record.periodic[1] ~ ";";
    }
    return summary;
}

// ============================= Edge and vertex extraction =============================

/** Classification of a tool edge's underlying curve, driving which solver path it takes. */
export enum SweepCurveClass
{
    LINE,
    CIRCLE,
    ELLIPSE,
    BSPLINE,
    OTHER
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

// ============================= UV calibration =============================

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
        throw "swSweepEmit: kernelUvToKnotUv called on a face whose kernel-to-knot map is not " ~
            "affine (fit residual " ~ calibration.maxFitResidual ~ " m). Cross by 3D point with " ~
            "invertPointOnSurface on this face.";
    }
    return applyTwoDimensionalAffineMap(calibration.kernelToKnot, kernelUv);
}

// ============================= Point inversion =============================

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
    const domain = surfaceKnotDomain(strippedSurface);
    const uPeriod = domain.uEnd - domain.uStart;
    const vPeriod = domain.vEnd - domain.vStart;
    const uIsPeriodic = strippedSurface.isUPeriodic == true;
    const vIsPeriodic = strippedSurface.isVPeriodic == true;
    const stepTolerance = 1e-12 * max(uPeriod, vPeriod);
    const maximumIterations = 20;

    var u = seedUv[0];
    var v = seedUv[1];
    var residualVector = evaluateBSplineSurfacePoint(strippedSurface, u, v) - targetPoint;
    var converged = false;
    var iterationCount = 0;
    for (var iteration = 0; iteration < maximumIterations; iteration += 1)
    {
        iterationCount = iteration + 1;
        const derivatives = evaluateBSplineSurfaceDerivatives(strippedSurface, u, v, 2, 2);
        residualVector = derivatives[0][0] - targetPoint;
        const currentSquaredResidual = squaredNorm(residualVector);
        if (currentSquaredResidual < 1e-28)
        {
            converged = true;
            break;
        }
        const uTangent = derivatives[1][0];
        const vTangent = derivatives[0][1];
        const fValue = dot(residualVector, uTangent);
        const gValue = dot(residualVector, vTangent);
        const j11 = dot(uTangent, uTangent) + dot(residualVector, derivatives[2][0]);
        const j12 = dot(uTangent, vTangent) + dot(residualVector, derivatives[1][1]);
        const j22 = dot(vTangent, vTangent) + dot(residualVector, derivatives[0][2]);
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
            const candidateResidual = evaluateBSplineSurfacePoint(strippedSurface, candidateU, candidateV) - targetPoint;
            if (squaredNorm(candidateResidual) <= currentSquaredResidual)
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
    residualVector = evaluateBSplineSurfacePoint(strippedSurface, u, v) - targetPoint;
    return {
            "uv" : vector(u, v),
            "residual" : norm(residualVector),
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
            const squaredDistance = squaredNorm(evaluateBSplineSurfacePoint(strippedSurface, u, v) - targetPoint);
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

// ============================= Trim loop plumbing =============================

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

// ----------------------------- trim loop internals -----------------------------

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

// ============================= Internal helpers =============================

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
function positiveModulo(value is number, divisor is number) returns number
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
    for (var planeIndex = 0; planeIndex < size(tangentPlanes); planeIndex += 1)
    {
        normals[planeIndex] = tangentPlanes[planeIndex].normal;
        edgePoints[planeIndex] = (1 / meter) * tangentPlanes[planeIndex].origin;
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

/**
 * Round-trip check of the point inversion on one stripped surface: evaluate three interior
 * knot-domain UVs, invert each with the seedless multi-seed entry, and return the worst 3D
 * residual and iteration count: { worstResidual {number}, worstIterations {number} }.
 */
function inversionRoundTripWorstCase(strippedSurface is map) returns map
{
    const testFractions = [vector(0.20, 0.40), vector(0.60, 0.70), vector(0.85, 0.15)];
    const domain = surfaceKnotDomain(strippedSurface);
    var worstResidual = 0;
    var worstIterations = 0;
    for (var fraction in testFractions)
    {
        const u = domain.uStart + (domain.uEnd - domain.uStart) * fraction[0];
        const v = domain.vStart + (domain.vEnd - domain.vStart) * fraction[1];
        const targetPoint = evaluateBSplineSurfacePoint(strippedSurface, u, v);
        const inversion = invertPointOnSurfaceFromGrid(strippedSurface, targetPoint, 7);
        if (inversion.residual > worstResidual)
        {
            worstResidual = inversion.residual;
        }
        if (inversion.iterations > worstIterations)
        {
            worstIterations = inversion.iterations;
        }
    }
    return { "worstResidual" : worstResidual, "worstIterations" : worstIterations };
}

/** One line describing a stripped surface's stored shape, for the self-test printouts. */
function describeSurfaceShape(strippedSurface is map) returns string
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

/** A degree-1 2D trim curve between two uv points. */
function uvLineCurve(startPoint is Vector, endPoint is Vector) returns map
{
    return {
            "degree" : 1, "dimension" : 2, "isRational" : false, "isPeriodic" : false,
            "controlPoints" : [startPoint, endPoint], "knots" : [0, 0, 1, 1]
        };
}

/**
 * An exact circle in uv as a rational quadratic in four quarter spans - the standard NURBS
 * circle, with corner weights of sqrt(1/2).
 */
function uvCircleCurve(centre is Vector, radius is number) returns map
{
    const cornerWeight = sqrt(0.5);
    const ring = [vector(1, 0), vector(1, 1), vector(0, 1), vector(-1, 1), vector(-1, 0),
            vector(-1, -1), vector(0, -1), vector(1, -1), vector(1, 0)];
    var controlPoints = makeArray(9, centre);
    for (var index = 0; index < 9; index += 1)
    {
        controlPoints[index] = centre + radius * ring[index];
    }
    return {
            "degree" : 2, "dimension" : 2, "isRational" : true, "isPeriodic" : false,
            "controlPoints" : controlPoints,
            "weights" : [1, cornerWeight, 1, cornerWeight, 1, cornerWeight, 1, cornerWeight, 1],
            "knots" : [0, 0, 0, 0.25, 0.25, 0.5, 0.5, 0.75, 0.75, 1, 1, 1]
        };
}

/** A bilinear unit patch - just enough surface for a record to carry a uv domain of [0, 1]^2. */
function flatUnitDomainSurface() returns map
{
    return {
            "uDegree" : 1, "vDegree" : 1,
            "uKnots" : [0, 0, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : [[vector(0, 0, 0), vector(0, 1, 0)], [vector(1, 0, 0), vector(1, 1, 0)]],
            "isRational" : false, "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/** A v-constant uv sample run from one u to another - a trim arc on a closed face. */
function uvArcSamples(v is number, uFrom is number, uTo is number, segmentCount is number) returns array
{
    var samples = makeArray(segmentCount + 1, vector(uFrom, v));
    for (var index = 0; index <= segmentCount; index += 1)
    {
        samples[index] = vector(uFrom + (uTo - uFrom) * index / segmentCount, v);
    }
    return samples;
}

/**
 * Half of a small circular hole centred exactly on the seam, with its u values FOLDED into
 * [0, 1) the way an inverted pcurve arrives. `side` picks the upper or lower half.
 */
function foldedHoleSamples(radius is number, side is number, segmentCount is number) returns array
{
    var samples = makeArray(segmentCount + 1, vector(0, 0.5));
    for (var index = 0; index <= segmentCount; index += 1)
    {
        const centred = -radius + 2 * radius * index / segmentCount;
        const height = sqrt(max(radius * radius - centred * centred, 0));
        samples[index] = vector(positiveModulo(centred, 1), 0.5 + side * height);
    }
    return samples;
}
