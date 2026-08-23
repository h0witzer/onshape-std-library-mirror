FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/boolean.fs", version : "3044.0");         // joinSurfaceBodiesWithAutoMatching
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");   // line, knotArray, rotationAround
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0"); // bSplineSurface, controlPointMatrix
import(path : "onshape/std/topologyUtils.fs", version : "3044.0");   // extractDirection

// Non-standard import: splineRefinementUtils.fs (custom-features/splineRefinementUtils.fs in the
// repository), imported as the PUBLISHED cross-document pin (document/element/version triple).
// Used ONLY by the "Sweep Probe - Evaluator Throughput" feature below; the other probes are
// pure std. If the module is republished, bump the version id here like every other consumer.
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs

/**
 * SOLID SWEEP M0 PROBES - live experiments gating design defaults of the generalized solid
 * sweep feature. Spec: docs/specs/SOLID_SWEEP_SPEC.md section 13. Each probe is a tiny
 * standalone feature that answers exactly one question about kernel behavior we cannot learn
 * from the mirror's documentation. Every probe prints its findings to the console and reports
 * a one-line verdict on the feature; none of them is a modeling tool.
 *
 * Probe 1 - Edge Of Solid Sweep: does opSweep accept an edge that belongs to a solid body as
 *           its profile, or must the edge be extracted to a wire body first? Gates whether the
 *           kernel-sweep route for sharp-edge envelope surfaces (spec section 8) needs an
 *           opExtractWires step.
 * Probe 2 - Evaluator Throughput: how many splineRefinementUtils surface-normal evaluations per
 *           second does interpreted FeatureScript actually deliver? Run at N and 2N and read
 *           Onshape's feature compute time; the difference divided by N is the per-eval cost.
 *           Calibrates every number in spec section 11.
 * Probe 3 - Imprint Tolerance: does opSplitFace's edgeTools imprint accept a wire that lies on
 *           the target face only approximately? Gates cap trimming (spec section 9).
 * Probe 4 - Degenerate Surface: how far can one boundary row of a bicubic patch collapse toward
 *           a single point before opCreateBSplineSurface refuses it? Gates the grazing-island
 *           strategy (spec section 7.1).
 * Probe 5 - Knit Closer: on the same sheet complex, does opBoolean UNION makeSolid or
 *           joinSurfaceBodiesWithAutoMatching close the shell more reliably? Gates the knit
 *           step (spec section 9).
 * Probe 6 - Isocline Oracle: can we pattern a transformed scratch instance, imprint angle-zero
 *           isoclines on it (the instantaneous contact curve for its velocity direction),
 *           harvest the curves as sample points, and abort the scratch scope so nothing
 *           survives? Gates the kernel topology oracle (spec section 2.2).
 * Probe 7 - UV Convention Calibration: what UV convention does evDistance report for a face,
 *           does it coincide with evFaceTangentPlane's bounding-box-normalized convention, and
 *           is the map from the kernel's convention to the extracted spline's knot domain
 *           affine? Gates the calibration helper of spec section 5. Two entry features share
 *           one core: the interactive probe takes a selected face; the Self Test variant is
 *           selection-free (builds a cylinder and a freeform patch itself) so the MCP test
 *           harness can execute it.
 */

// ============================= Probe 1 - Edge Of Solid Sweep =============================

annotation { "Feature Type Name" : "Sweep Probe - Edge Of Solid" }
export const sweepProbeEdgeOfSolid = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edge on a solid body", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.toolEdge is Query;
        annotation { "Name" : "Path edges", "Filter" : EntityType.EDGE }
        definition.pathEdges is Query;
    }
    {
        // Attempt 1: hand opSweep the solid's edge directly.
        var directSucceeded = false;
        try
        {
            opSweep(context, id + "directSweep", {
                        "profiles" : definition.toolEdge,
                        "path" : definition.pathEdges
                    });
            directSucceeded = true;
        }
        const directBodies = evaluateQuery(context, qCreatedBy(id + "directSweep", EntityType.BODY));
        println("[EDGE SWEEP PROBE] direct sweep of the solid's edge: threw=" ~ !directSucceeded ~
            ", bodies created: " ~ size(directBodies));

        // Attempt 2: extract the edge to a wire body first, then sweep the wire's edge.
        var extractSucceeded = false;
        var wireSweepSucceeded = false;
        try
        {
            opExtractWires(context, id + "extractWire", { "edges" : definition.toolEdge });
            extractSucceeded = true;
        }
        if (extractSucceeded)
        {
            try
            {
                opSweep(context, id + "wireSweep", {
                            "profiles" : qOwnedByBody(qCreatedBy(id + "extractWire", EntityType.BODY), EntityType.EDGE),
                            "path" : definition.pathEdges
                        });
                wireSweepSucceeded = true;
            }
        }
        const wireBodies = evaluateQuery(context, qCreatedBy(id + "wireSweep", EntityType.BODY));
        println("[EDGE SWEEP PROBE] extract-to-wire path: extract threw=" ~ !extractSucceeded ~
            ", wire sweep threw=" ~ !wireSweepSucceeded ~ ", bodies created: " ~ size(wireBodies));

        var verdict;
        if (size(directBodies) > 0)
        {
            verdict = "opSweep accepted the solid's edge directly (" ~ size(directBodies) ~
                " sheet body). The kernel-sweep route needs no opExtractWires step.";
        }
        else if (size(wireBodies) > 0)
        {
            verdict = "Direct edge-of-solid sweep failed but extract-to-wire then sweep worked. " ~
                "The kernel-sweep route must insert an opExtractWires step.";
        }
        else
        {
            verdict = "Both the direct sweep and the extract-to-wire sweep failed - check the console " ~
                "for the errors; the path may be unsuitable (must be a connected G1 chain).";
        }
        println("[EDGE SWEEP PROBE] VERDICT: " ~ verdict);
        reportFeatureInfo(context, id, verdict);
    });

// ============================= Probe 2 - Evaluator Throughput =============================

export const SWEEP_PROBE_EVALUATION_COUNT_BOUNDS =
{
            (unitless) : [1000, 20000, 500000]
        } as IntegerBoundSpec;

annotation { "Feature Type Name" : "Sweep Probe - Evaluator Throughput" }
export const sweepProbeEvaluatorThroughput = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face to evaluate on", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.sampleFace is Query;
        annotation { "Name" : "Evaluation count" }
        isInteger(definition.evaluationCount, SWEEP_PROBE_EVALUATION_COUNT_BOUNDS);
    }
    {
        // Read the face once (the pipeline's extraction step), then evaluate in a pure loop.
        // Run this feature at N and at 2N and read Onshape's feature compute time for each:
        // (time(2N) - time(N)) / N is the marginal cost of ONE surface-normal evaluation
        // (order-1 derivatives). The solver hot loop uses order-2 derivatives, so budget
        // roughly 1.5x to 2x the number this probe measures.
        // NOTE (probe finding 2026-08-21): tolerance is a plain unitless number (meters
        // implied) - passing a ValueWithUnits fails evApproximateBSplineSurface's precondition.
        const extracted = evApproximateBSplineSurface(context, {
                    "face" : definition.sampleFace,
                    "tolerance" : 1e-6
                });
        const surface = normalizeSurfaceDefinition(extracted.bSplineSurface);

        const uDegree = surface.uDegree;
        const vDegree = surface.vDegree;
        const uKnots = surface.uKnots;
        const vKnots = surface.vKnots;
        const uStart = uKnots[uDegree];
        const uEnd = uKnots[size(uKnots) - uDegree - 1];
        const vStart = vKnots[vDegree];
        const vEnd = vKnots[size(vKnots) - vDegree - 1];

        // Deterministic parameter spread over the interior of the knot domain (edges avoided so
        // periodic wrap handling does not dominate the measurement).
        const evaluationCount = definition.evaluationCount;
        const samplesAcross = 97; // coprime with typical counts so the grid does not stripe
        var checksum = 0;
        for (var evaluationIndex = 0; evaluationIndex < evaluationCount; evaluationIndex += 1)
        {
            const uFraction = 0.02 + 0.96 * ((evaluationIndex % samplesAcross) / (samplesAcross - 1));
            const vFraction = 0.02 + 0.96 * (((floor(evaluationIndex / samplesAcross)) % samplesAcross) / (samplesAcross - 1));
            const normal = evaluateBSplineSurfaceNormal(surface,
                uStart + (uEnd - uStart) * uFraction,
                vStart + (vEnd - vStart) * vFraction);
            checksum += normal[0]; // unit normal component: plain number, keeps the loop honest
        }

        const summary = "Ran " ~ evaluationCount ~ " surface-normal evaluations (degree " ~
            uDegree ~ "x" ~ vDegree ~ ", " ~ size(uKnots) ~ "/" ~ size(vKnots) ~
            " knots). Checksum " ~ checksum ~ ". Read the feature compute time, rerun at 2x the count, " ~
            "and difference the two.";
        println("[THROUGHPUT PROBE] " ~ summary);
        reportFeatureInfo(context, id, summary);
    });

// ============================= Probe 3 - Imprint Tolerance =============================

annotation { "Feature Type Name" : "Sweep Probe - Imprint Tolerance" }
export const sweepProbeImprintTolerance = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face to imprint", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.targetFace is Query;
        annotation { "Name" : "Edges to imprint (lying on or near the face)", "Filter" : EntityType.EDGE }
        definition.imprintEdges is Query;
    }
    {
        // Build test edges deliberately offset from the face (a sketch on an offset plane, a
        // 3D fit spline through displaced face points, etc.) and record at which offset the
        // imprint stops landing. NORMAL_TO_TARGET projection is the mode cap trimming uses.
        const facesBefore = size(evaluateQuery(context, qOwnedByBody(qOwnerBody(definition.targetFace), EntityType.FACE)));

        var splitSucceeded = false;
        var splittingEdgeCount = 0;
        try
        {
            const result = opSplitFace(context, id + "splitFace", {
                        "faceTargets" : definition.targetFace,
                        "edgeTools" : definition.imprintEdges
                    });
            splittingEdgeCount = size(result.splittingEdges);
            splitSucceeded = true;
        }

        const facesAfter = size(evaluateQuery(context, qOwnedByBody(qOwnerBody(definition.targetFace), EntityType.FACE)));
        const verdict = splitSucceeded ?
            ("opSplitFace imprinted " ~ splittingEdgeCount ~ " edge(s); owner body went from " ~
                    facesBefore ~ " to " ~ facesAfter ~ " faces.") :
            "opSplitFace threw - the imprint edges did not land on the face (see console).";
        println("[IMPRINT PROBE] VERDICT: " ~ verdict);
        reportFeatureInfo(context, id, verdict);
    });

// ============================= Probe 4 - Degenerate Surface =============================

annotation { "Feature Type Name" : "Sweep Probe - Degenerate Surface" }
export const sweepProbeDegenerateSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Print control net details" }
        definition.printControlNetDetails is boolean;
    }
    {
        // A bicubic 4x4 patch whose LAST control point row is collapsed toward its centroid by
        // an increasing fraction; 1.0 is an exact pole (all four points coincident). Each
        // attempt is emitted side by side along +X so survivors are visible in the graphics
        // area. The first fraction that fails is the island-patch policy boundary.
        const collapseFractions = [0.0, 0.9, 0.99, 0.999, 1.0];
        var verdictParts = "";
        for (var attemptIndex = 0; attemptIndex < size(collapseFractions); attemptIndex += 1)
        {
            const collapseFraction = collapseFractions[attemptIndex];
            const xOffset = attemptIndex * 0.06 * meter;

            // Rows run in u; the last row collapses toward its own centroid.
            var rows = makeArray(4);
            for (var rowIndex = 0; rowIndex < 4; rowIndex += 1)
            {
                var row = makeArray(4);
                for (var columnIndex = 0; columnIndex < 4; columnIndex += 1)
                {
                    row[columnIndex] = vector(columnIndex * 0.01, rowIndex * 0.01, 0.005 * sin(90 * degree * columnIndex)) * meter
                        + vector(xOffset, 0 * meter, 0 * meter);
                }
                rows[rowIndex] = row;
            }
            const lastRowCentroid = (rows[3][0] + rows[3][1] + rows[3][2] + rows[3][3]) / 4;
            for (var columnIndex = 0; columnIndex < 4; columnIndex += 1)
            {
                rows[3][columnIndex] = rows[3][columnIndex] + collapseFraction * (lastRowCentroid - rows[3][columnIndex]);
            }
            if (definition.printControlNetDetails)
            {
                println("[DEGENERATE PROBE] fraction " ~ collapseFraction ~ " collapsed last row: " ~ rows[3]);
            }

            var emitted = false;
            try
            {
                opCreateBSplineSurface(context, id + unstableIdComponent(attemptIndex) + "patch", {
                            "bSplineSurface" : bSplineSurface({
                                        "uDegree" : 3,
                                        "vDegree" : 3,
                                        "isUPeriodic" : false,
                                        "isVPeriodic" : false,
                                        "controlPoints" : controlPointMatrix(rows)
                                    })
                        });
                emitted = true;
            }
            println("[DEGENERATE PROBE] collapse fraction " ~ collapseFraction ~ ": " ~
                (emitted ? "ACCEPTED" : "REFUSED"));
            verdictParts = verdictParts ~ collapseFraction ~ (emitted ? " ok; " : " REFUSED; ");
        }
        const verdict = "opCreateBSplineSurface pole collapse: " ~ verdictParts;
        println("[DEGENERATE PROBE] VERDICT: " ~ verdict);
        reportFeatureInfo(context, id, verdict);
    });

// ============================= Probe 5 - Knit Closer =============================

annotation { "Feature Type Name" : "Sweep Probe - Knit Closer" }
export const sweepProbeKnitCloser = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sheet bodies forming a (near-)closed complex", "Filter" : EntityType.BODY && BodyType.SHEET }
        definition.sheetBodies is Query;
    }
    {
        const selectedSheetCount = size(evaluateQuery(context, definition.sheetBodies));
        if (selectedSheetCount < 2)
        {
            throw regenError("Select at least two sheet bodies whose edges are coincident - a one-tool " ~
                "union is invalid by definition. A good fixture: six separate planar surface patches " ~
                "forming a cube (each its own surface body).");
        }

        // Two identical copies of the complex; closer A on one, closer B on the other, so the
        // comparison runs on the same input in the same regeneration.
        opPattern(context, id + "copyA", {
                    "entities" : definition.sheetBodies,
                    "transforms" : [identityTransform()],
                    "instanceNames" : ["closerA"]
                });
        opPattern(context, id + "copyB", {
                    "entities" : definition.sheetBodies,
                    "transforms" : [identityTransform()],
                    "instanceNames" : ["closerB"]
                });
        const copiesA = qCreatedBy(id + "copyA", EntityType.BODY);
        const copiesB = qCreatedBy(id + "copyB", EntityType.BODY);

        // Closer A: one n-ary surface UNION with makeSolid. allowSheets is undocumented on the
        // op but is how the std library itself runs surface booleans (boolean.fs usage).
        var closerASucceeded = false;
        try
        {
            opBoolean(context, id + "unionA", {
                        "tools" : copiesA,
                        "operationType" : BooleanOperationType.UNION,
                        "makeSolid" : true,
                        "allowSheets" : true
                    });
            closerASucceeded = true;
        }
        const solidsA = size(evaluateQuery(context, qBodyType(copiesA, BodyType.SOLID)));
        const remainingA = size(evaluateQuery(context, copiesA));

        // Closer B: the std surface-feature post-processing closer, scoped to the second copy.
        var closerBSucceeded = false;
        try
        {
            joinSurfaceBodiesWithAutoMatching(context, id + "joinB", {
                        "defaultSurfaceScope" : false,
                        "booleanSurfaceScope" : copiesB,
                        "seed" : copiesB
                    }, true, function(reconstructId)
                {
                });
            closerBSucceeded = true;
        }
        const solidsB = size(evaluateQuery(context, qBodyType(copiesB, BodyType.SOLID)));
        const remainingB = size(evaluateQuery(context, copiesB));

        const verdict = "Closer A (opBoolean UNION makeSolid): threw=" ~ !closerASucceeded ~
            ", " ~ remainingA ~ " body(ies) remain, " ~ solidsA ~ " solid. " ~
            "Closer B (joinSurfaceBodiesWithAutoMatching): threw=" ~ !closerBSucceeded ~
            ", " ~ remainingB ~ " body(ies) remain, " ~ solidsB ~ " solid.";
        println("[KNIT PROBE] VERDICT: " ~ verdict);
        reportFeatureInfo(context, id, verdict);
    });

// ============================= Probe 6 - Isocline Oracle =============================

annotation { "Feature Type Name" : "Sweep Probe - Isocline Oracle" }
export const sweepProbeIsoclineOracle = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Tool body", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 1 }
        definition.toolBody is Query;
        annotation { "Name" : "View direction (face/edge/mate connector)", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
        definition.directionEntity is Query;
    }
    {
        const direction = extractDirection(context, definition.directionEntity);
        if (direction == undefined)
        {
            throw regenError("Could not extract a direction from the selected entity.");
        }

        // Everything below runs in a scratch scope: pattern a TRANSFORMED instance of the tool
        // (rotation + translation, to prove the oracle works on instances, not just originals),
        // imprint its angle-zero isoclines, harvest curve samples, then abort so no scratch
        // geometry survives. Only the harvested numbers and debug points outlive the abort.
        const scratchId = id + "scratch";
        var samplePoints = [];
        var wireCount = 0;
        var edgeCount = 0;
        var oracleSucceeded = false;

        // Rotate about an axis through the body's own center so the instance stays nearby.
        const toolBoxCenter = box3dCenter(evBox3d(context, { "topology" : definition.toolBody, "tight" : false }));
        const probeTransform = rotationAround(line(toolBoxCenter, vector(0, 0, 1)), 15 * degree) *
            transform(vector(0.02, 0, 0) * meter);

        startFeature(context, scratchId);
        try
        {
            opPattern(context, scratchId + "instance", {
                        "entities" : definition.toolBody,
                        "transforms" : [probeTransform],
                        "instanceNames" : ["oracleInstance"]
                    });
            opCreateIsocline(context, scratchId + "isocline", {
                        "faces" : qOwnedByBody(qCreatedBy(scratchId + "instance", EntityType.BODY), EntityType.FACE),
                        "direction" : direction,
                        "angle" : 0 * degree
                    });

            const wires = evaluateQuery(context, qCreatedBy(scratchId + "isocline", EntityType.BODY));
            wireCount = size(wires);
            const isoclineEdges = evaluateQuery(context, qOwnedByBody(qCreatedBy(scratchId + "isocline", EntityType.BODY), EntityType.EDGE));
            edgeCount = size(isoclineEdges);

            const samplesPerEdge = 9;
            samplePoints = makeArray(edgeCount * samplesPerEdge);
            var sampleParameters = makeArray(samplesPerEdge);
            for (var sampleIndex = 0; sampleIndex < samplesPerEdge; sampleIndex += 1)
            {
                sampleParameters[sampleIndex] = sampleIndex / (samplesPerEdge - 1);
            }
            for (var edgeIndex = 0; edgeIndex < edgeCount; edgeIndex += 1)
            {
                const tangentLines = evEdgeTangentLines(context, {
                            "edge" : isoclineEdges[edgeIndex],
                            "parameters" : sampleParameters
                        });
                for (var sampleIndex = 0; sampleIndex < samplesPerEdge; sampleIndex += 1)
                {
                    samplePoints[edgeIndex * samplesPerEdge + sampleIndex] = tangentLines[sampleIndex].origin;
                }
            }
            oracleSucceeded = true;
        }
        abortFeature(context, scratchId);

        // The scratch geometry is gone; the harvested samples remain usable. Map each sample
        // back through the inverse of the instance transform - the same h(t) inverse mapping
        // the real pipeline uses - so the points draw ON the original tool body.
        const backToTool = inverse(probeTransform);
        for (var pointIndex = 0; pointIndex < size(samplePoints); pointIndex += 1)
        {
            if (samplePoints[pointIndex] != undefined)
            {
                addDebugPoint(context, backToTool * samplePoints[pointIndex], DebugColor.BLUE);
            }
        }

        const verdict = oracleSucceeded ?
            ("Oracle viable: isocline imprint on a transformed scratch instance produced " ~ wireCount ~
                    " wire body(ies) / " ~ edgeCount ~ " edge(s); " ~ size(samplePoints) ~
                    " samples harvested before abort, back-mapped through the inverse transform " ~
                    "(drawn blue ON the tool body), and no scratch geometry survived. NOTE: a face " ~
                    "sitting at isocline angle 0 EVERYWHERE (cylinder wall viewed along its axis, flat " ~
                    "cap viewed across it) has no discrete isocline - the real pipeline must run the " ~
                    "oracle per face, after the sliding-face audit.") :
            "Oracle FAILED inside the scratch scope - likely a face at isocline angle 0 everywhere " ~
                "(direction along/across an analytic face). This is the degenerate sliding case: the " ~
                "pipeline's per-face audit must skip such faces before the isocline call. See console.";
        println("[ORACLE PROBE] VERDICT: " ~ verdict);
        reportFeatureInfo(context, id, verdict);
    });

// ============================= Probe 7 - UV Convention Calibration =============================

annotation { "Feature Type Name" : "Sweep Probe - UV Convention Calibration" }
export const sweepProbeUvConventionCalibration = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face to calibrate (non-planar; an untrimmed face is cleanest)",
                    "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.calibrationFace is Query;
    }
    {
        reportFeatureInfo(context, id, runUvConventionCalibration(context, definition.calibrationFace));
    });

annotation { "Feature Type Name" : "Sweep Probe - UV Calibration Self Test" }
export const sweepProbeUvCalibrationSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // Fixture-driven probe 7 for selection-free (MCP / headless) execution: builds a
        // cylinder wall (analytic, u-periodic) and a freeform bicubic patch, runs the
        // calibration on each, and reports both verdicts.
        fCylinder(context, id + "cylinder", {
                    "bottomCenter" : vector(0, 0, 0) * meter,
                    "topCenter" : vector(0, 0, 0.08) * meter,
                    "radius" : 0.03 * meter
                });
        println("[UV CALIBRATION SELF TEST] cylinder wall:");
        const cylinderVerdict = runUvConventionCalibration(context,
            qGeometry(qCreatedBy(id + "cylinder", EntityType.FACE), GeometryType.CYLINDER));

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
        println("[UV CALIBRATION SELF TEST] freeform bicubic patch:");
        const patchVerdict = runUvConventionCalibration(context, qCreatedBy(id + "patch", EntityType.FACE));

        reportFeatureInfo(context, id, "CYLINDER: " ~ cylinderVerdict ~ "  ||  PATCH: " ~ patchVerdict);
    });

/**
 * Probe 7 core: calibrate one face's UV conventions. Pairs knot-domain samples of the extracted
 * surface (evaluated to 3D with the module evaluator) with the face UV evDistance reports for
 * the same locations, checks those parameters against evFaceTangentPlanes (returned origin vs
 * witness point), and fits a least-squares kernel-to-knot-domain affine map with the last
 * usable sample held out as a validation point. Prints per-sample detail to the console and
 * returns the one-line verdict string. ev calls only - makes no model changes. Input: one
 * non-mesh face; planar and mesh faces report as degenerate. Findings ledger:
 * docs/specs/SOLID_SWEEP_SPEC.md sections 5, 13 (probe 7), and 15.
 */
function runUvConventionCalibration(context is Context, calibrationFace is Query) returns string
{
    const extracted = evApproximateBSplineSurface(context, {
                "face" : calibrationFace,
                "tolerance" : 1e-6
            });
    const surface = normalizeSurfaceDefinition(extracted.bSplineSurface);
    const uKnots = surface.uKnots;
    const vKnots = surface.vKnots;
    const uStart = uKnots[surface.uDegree];
    const uEnd = uKnots[size(uKnots) - surface.uDegree - 1];
    const vStart = vKnots[surface.vDegree];
    const vEnd = vKnots[size(vKnots) - surface.vDegree - 1];
    println("[UV CALIBRATION PROBE] extracted knot domain u [" ~ uStart ~ ", " ~ uEnd ~ "], v [" ~
        vStart ~ ", " ~ vEnd ~ "], degree " ~ surface.uDegree ~ "x" ~ surface.vDegree ~
        ", periodic u/v " ~ surface.isUPeriodic ~ "/" ~ surface.isVPeriodic);

    // Deliberately asymmetric interior spots so an axis swap or flip in the kernel's
    // parameterization cannot masquerade as the identity map.
    const sampleFractions = [
            vector(0.15, 0.30), vector(0.35, 0.75), vector(0.55, 0.20),
            vector(0.80, 0.60), vector(0.70, 0.85), vector(0.45, 0.50)
        ];
    const sampleCount = size(sampleFractions);
    // Samples whose nearest face point lies farther than this are off the face (in a
    // trimmed-away region of the underlying surface) and are excluded from the fit.
    const witnessDistanceCap = 1e-5 * meter;

    var knotParameters = makeArray(sampleCount);   // 2D unitless, the extracted knot domain
    var kernelParameters = makeArray(sampleCount); // 2D unitless, whatever evDistance reports
    var witnessPoints = makeArray(sampleCount);    // the kernel's own 3D point for those parameters
    var sampleIsUsable = makeArray(sampleCount);
    var usableCount = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        const uParameter = uStart + (uEnd - uStart) * sampleFractions[sampleIndex][0];
        const vParameter = vStart + (vEnd - vStart) * sampleFractions[sampleIndex][1];
        knotParameters[sampleIndex] = vector(uParameter, vParameter);
        const surfacePoint = evaluateBSplineSurfacePoint(surface, uParameter, vParameter);

        const distanceResult = evDistance(context, {
                    "side0" : calibrationFace,
                    "side1" : surfacePoint
                });
        const faceSide = distanceResult.sides[0];
        const parameterIsTwoVector = faceSide.parameter is Vector && size(faceSide.parameter) == 2;
        sampleIsUsable[sampleIndex] = parameterIsTwoVector && distanceResult.distance < witnessDistanceCap;
        if (parameterIsTwoVector)
        {
            kernelParameters[sampleIndex] = vector(faceSide.parameter[0], faceSide.parameter[1]);
        }
        witnessPoints[sampleIndex] = faceSide.point;
        if (sampleIsUsable[sampleIndex])
        {
            usableCount += 1;
        }
        println("[UV CALIBRATION PROBE] sample " ~ sampleIndex ~ ": knot (" ~ uParameter ~ ", " ~
            vParameter ~ ") -> kernel " ~ faceSide.parameter ~ ", witness distance " ~
            (distanceResult.distance / meter) ~ " m" ~
            (sampleIsUsable[sampleIndex] ? "" : "  EXCLUDED (parameter not a 2-vector, or nearest point is not the sample)"));
    }

    if (usableCount < 4)
    {
        const verdict = "Only " ~ usableCount ~ " of " ~ sampleCount ~ " samples paired up - too few " ~
            "for the affine solve. If witness distances are large the samples landed in trimmed-away " ~
            "regions: pick an untrimmed face. If the kernel parameters are all zero this is the " ~
            "documented plane/mesh degenerate case: pick a curved face.";
        println("[UV CALIBRATION PROBE] VERDICT: " ~ verdict);
        return verdict;
    }

    // Convention check: evFaceTangentPlanes at the raw evDistance parameters lands on the
    // witness points exactly when the two kernel conventions coincide.
    var usableKernelParameters = makeArray(usableCount);
    var usableIndices = makeArray(usableCount);
    var usableCursor = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        if (sampleIsUsable[sampleIndex])
        {
            usableKernelParameters[usableCursor] = kernelParameters[sampleIndex];
            usableIndices[usableCursor] = sampleIndex;
            usableCursor += 1;
        }
    }
    var tangentClaimFinding;
    var tangentPlanesSucceeded = false;
    try
    {
        const tangentPlanes = evFaceTangentPlanes(context, {
                    "face" : calibrationFace,
                    "parameters" : usableKernelParameters
                });
        var maxOriginMismatch = 0 * meter;
        for (var usableIndex = 0; usableIndex < usableCount; usableIndex += 1)
        {
            const originMismatch = norm(tangentPlanes[usableIndex].origin - witnessPoints[usableIndices[usableIndex]]);
            if (originMismatch > maxOriginMismatch)
            {
                maxOriginMismatch = originMismatch;
            }
        }
        tangentClaimFinding = (maxOriginMismatch < witnessDistanceCap) ?
            ("Doc claim CONFIRMED: evDistance parameters ARE evFaceTangentPlane parameters (max origin mismatch " ~
                    (maxOriginMismatch / meter) ~ " m).") :
            ("Doc claim REFUTED: evFaceTangentPlanes at the raw evDistance parameters lands elsewhere (max origin mismatch " ~
                    (maxOriginMismatch / meter) ~ " m).");
        tangentPlanesSucceeded = true;
    }
    if (!tangentPlanesSucceeded)
    {
        tangentClaimFinding = "Doc claim REFUTED: evFaceTangentPlanes threw on the raw evDistance parameters " ~
            "(outside its normalized domain) - the two conventions differ.";
    }
    println("[UV CALIBRATION PROBE] " ~ tangentClaimFinding);

    // Kernel-to-knot-domain affine map, least squares, last usable sample held out.
    const holdOutValidation = usableCount >= 5;
    const fitCount = holdOutValidation ? usableCount - 1 : usableCount;
    var fitSourcePoints = makeArray(fitCount);
    var fitTargetPoints = makeArray(fitCount);
    for (var fitIndex = 0; fitIndex < fitCount; fitIndex += 1)
    {
        fitSourcePoints[fitIndex] = usableKernelParameters[fitIndex];
        fitTargetPoints[fitIndex] = knotParameters[usableIndices[fitIndex]];
    }
    const affineMap = fitTwoDimensionalAffineMap(fitSourcePoints, fitTargetPoints);
    if (affineMap == undefined)
    {
        const verdict = tangentClaimFinding ~ " Affine solve DEGENERATE: the kernel parameters do not " ~
            "span a 2D patch (all zero is the documented plane/mesh case; collinear happens on a sliver). " ~
            "Pick a curved, well-proportioned face.";
        println("[UV CALIBRATION PROBE] VERDICT: " ~ verdict);
        return verdict;
    }

    // Residuals in 3D: map each kernel parameter to the knot domain, evaluate OUR surface
    // there, and compare against the kernel's own witness point for that parameter.
    var maxFitResidual = 0 * meter;
    var validationResidual = 0 * meter;
    for (var usableIndex = 0; usableIndex < usableCount; usableIndex += 1)
    {
        const mapped = applyTwoDimensionalAffineMap(affineMap, usableKernelParameters[usableIndex]);
        const clampedU = min(max(mapped[0], uStart), uEnd);
        const clampedV = min(max(mapped[1], vStart), vEnd);
        const residual = norm(evaluateBSplineSurfacePoint(surface, clampedU, clampedV) -
            witnessPoints[usableIndices[usableIndex]]);
        if (holdOutValidation && usableIndex == usableCount - 1)
        {
            validationResidual = residual;
        }
        else if (residual > maxFitResidual)
        {
            maxFitResidual = residual;
        }
    }
    println("[UV CALIBRATION PROBE] affine map kernel->knot: matrix " ~ affineMap.matrix ~
        ", offset " ~ affineMap.offset);

    const affineIsAdequate = maxFitResidual < witnessDistanceCap &&
        (!holdOutValidation || validationResidual < witnessDistanceCap);
    const verdict = tangentClaimFinding ~ " Affine kernel->knot calibration " ~
        (affineIsAdequate ? "ADEQUATE" : "INADEQUATE (the relation is not affine on this face class)") ~
        ": max fit residual " ~ (maxFitResidual / meter) ~ " m" ~
        (holdOutValidation ? (", held-out validation residual " ~ (validationResidual / meter) ~ " m") :
                ", no sample spare for held-out validation") ~
        ", from " ~ usableCount ~ " of " ~ sampleCount ~ " samples.";
    println("[UV CALIBRATION PROBE] VERDICT: " ~ verdict);
    return verdict;
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
