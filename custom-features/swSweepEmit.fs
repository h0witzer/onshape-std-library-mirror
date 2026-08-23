FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0"); // BSplineSurface, Cylinder, Cone, Sphere, Torus types
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");   // Line, Circle, Ellipse, BSplineCurve types

// Non-standard import: splineRefinementUtils.fs (custom-features/splineRefinementUtils.fs in the
// repository), imported as the PUBLISHED cross-document pin. Supplies normalizeSurfaceDefinition,
// evaluateBSplineSurfacePoint, and evaluateBSplineSurfaceDerivatives (the rational-aware
// derivative rectangle the point inversion consumes). Bump the version id on republish.
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs

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
        var rows = makeArray(4);
        for (var rowIndex = 0; rowIndex < 4; rowIndex += 1)
        {
            var row = makeArray(4);
            for (var columnIndex = 0; columnIndex < 4; columnIndex += 1)
            {
                row[columnIndex] = vector(0.1 + columnIndex * 0.01, rowIndex * 0.01,
                            0.005 * sin(90 * degree * columnIndex) + 0.004 * cos(60 * degree * rowIndex)) * meter;
            }
            rows[rowIndex] = row;
        }
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

        const verdict = (failures == "") ?
            "PASS: faces classify on all fixtures; inversion at machine precision; co-edges carry class, " ~
                "convexity, sides, one-sided normals, and pcurves within tolerance; box vertices assemble " ~
                "3 edges and 3 cone normals each." :
            ("FAIL:" ~ failures);
        println("[EMIT SELF TEST] VERDICT: " ~ verdict);
        reportFeatureInfo(context, id, verdict);
    });

// ============================= Face extraction =============================

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
 *         classes (exact for BSPLINE, approximated at faceExtractTolerance for OTHER),
 *         else undefined,
 *     splineIsExact {boolean},
 *     periodic {array} : [uPeriodic, vPeriodic] from evFacePeriodicity,
 *     trimLoops {map} : { boundary, inner } 2D UV BSplineCurves in the approximated spline's
 *         domain - present only on the approximation path (exact and analytic faces get their
 *         loops from co-edge pcurves in the edge extraction pass),
 *     calibration {map} : UvCalibration for spline-bearing records, else undefined
 * }
 *
 * faceExtractTolerance is a plain number, meters implied.
 */
export function extractToolFaceRecords(context is Context, toolBody is Query, faceExtractTolerance is number) returns array
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
            "calibration" : undefined
        };
        if (surfaceClass == SweepSurfaceClass.BSPLINE)
        {
            record.spline = stripSurfaceUnits(normalizeSurfaceDefinition(surfaceDefinition));
            record.splineIsExact = true;
        }
        else if (surfaceClass == SweepSurfaceClass.OTHER)
        {
            // Non-rational is required by swEnvelopeMath's coefficient path; the kernel
            // absorbs the conversion cost here, at the same tolerance.
            const approximated = evApproximateBSplineSurface(context, {
                        "face" : face,
                        "tolerance" : faceExtractTolerance,
                        "forceNonRational" : true
                    });
            record.spline = stripSurfaceUnits(normalizeSurfaceDefinition(approximated.bSplineSurface));
            record.trimLoops = {
                "boundary" : approximated.boundaryBSplineCurves,
                "inner" : approximated.innerLoopBSplineCurves
            };
        }
        else
        {
            record.analytic = surfaceDefinition;
        }
        if (record.spline != undefined)
        {
            record.calibration = buildUvCalibration(context, face, record.spline);
        }
        records[faceIndex] = record;
    }
    return records;
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
