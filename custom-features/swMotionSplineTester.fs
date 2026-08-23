FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");   // bSplineCurve
import(path : "onshape/std/splineUtils.fs", version : "3044.0");     // evaluateSpline (faithful on NON-rational splines)

// export import: MotionFrameSource is used as a dialog parameter type below.
export import(path : "e32b4de68532811bf7e189be", version : "b50d8aa6085738408921225f");//swMotionSpline.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * MOTION SPLINE TESTER - live validation of swMotionSpline.fs against a user-picked path.
 * Spec: docs/specs/SOLID_SWEEP_SPEC.md section 4. Creates no geometry (the module's helper
 * scaffold lives and dies inside its own scratch scope); draws debug frames on request.
 *
 * Checks:
 *   - the motion starts at identity (A(0) = I, b(0) = 0);
 *   - the rotation is orthonormal AT every station (interpolation nodes are exactly rigid);
 *   - the certified orthogonality drift passes its tolerance, re-verified independently at
 *     off-node parameters;
 *   - reported derivatives match central finite differences of the motion itself;
 *   - trajectoryCurveOf is EXACT: its spline (evaluated by std evaluateSpline, faithful on
 *     non-rational curves) matches applyMotion pointwise, and its derivative matches
 *     motionVelocityAt - the control-point-transport exactness claim of spec section 2.1;
 *   - motionSnapshotTransform maps the start frame origin onto each station frame origin;
 *   - keep-orientation mode keeps the rotation exactly identity;
 *   - one event is recorded per interior edge junction.
 */
annotation { "Feature Type Name" : "SW Motion Spline Tester" }
export const swMotionSplineTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Path edges", "Filter" : EntityType.EDGE }
        definition.pathEdges is Query;
        annotation { "Name" : "Keep orientation (pure translation)" }
        definition.keepOrientation is boolean;
        annotation { "Name" : "Frame source" }
        definition.frameSourceMode is MotionFrameSource;
        annotation { "Name" : "Draw station frames" }
        definition.drawStationFrames is boolean;
    }
    {
        var failures = [];
        var checkCount = 0;

        const motion = buildMotionSpline(context, id + "motion", {
                    "pathEdges" : definition.pathEdges,
                    "keepOrientation" : definition.keepOrientation,
                    "frameSource" : definition.frameSourceMode
                });
        println("[MOTION TESTER] built: " ~ size(motion.stationParameters) ~ " stations, drift " ~
            motion.orthogonalityDrift ~ ", frame source " ~ motion.frameSource ~
            ", " ~ size(motion.events) ~ " event(s), path length " ~ motion.pathLength);
        println("[MOTION TESTER] diagnostics: mode " ~ motion.buildDiagnostics.requestedMode ~
            ", " ~ motion.buildDiagnostics.ladderRungs ~ " ladder rung(s), " ~
            motion.buildDiagnostics.finalStationCount ~ " final stations, " ~
            motion.buildDiagnostics.perStationEvaluationCalls ~ " per-station ev-call(s), " ~
            motion.buildDiagnostics.batchedTangentCalls ~ " batched tangent call(s). " ~
            "Read the feature compute time alongside this line for the A/B comparison.");

        // ---------- IDENTITY AT START ----------
        const startSample = motionAt(motion, 0);
        checkCount += 2;
        if (norm(startSample.columns[0] - vector(1, 0, 0)) > 1e-9 ||
            norm(startSample.columns[1] - vector(0, 1, 0)) > 1e-9 ||
            norm(startSample.columns[2] - vector(0, 0, 1)) > 1e-9)
        {
            failures = append(failures, "START: A(0) is not identity: " ~ startSample.columns);
        }
        if (norm(startSample.translation) > 1e-9)
        {
            failures = append(failures, "START: b(0) is not zero: " ~ startSample.translation);
        }

        // ---------- RIGIDITY AT STATIONS ----------
        for (var stationParameter in motion.stationParameters)
        {
            checkCount += 1;
            const sample = motionAt(motion, stationParameter);
            if (orthonormalityDefect(sample.columns) > 1e-9)
            {
                failures = append(failures, "STATION rigidity at t=" ~ stationParameter ~ ": defect " ~
                    orthonormalityDefect(sample.columns));
            }
        }

        // ---------- DRIFT: CERTIFIED AND INDEPENDENTLY RE-CHECKED ----------
        checkCount += 1;
        if (motion.orthogonalityDrift > motion.driftTolerance)
        {
            failures = append(failures, "DRIFT: certified " ~ motion.orthogonalityDrift ~ " exceeds tolerance " ~
                motion.driftTolerance);
        }
        const offNodeParameters = [0.037, 0.111, 0.234, 0.389, 0.456, 0.541, 0.678, 0.723, 0.812, 0.897, 0.961];
        for (var t in offNodeParameters)
        {
            checkCount += 1;
            const defect = orthonormalityDefect(motionAt(motion, t).columns);
            if (defect > 10 * motion.driftTolerance)
            {
                failures = append(failures, "DRIFT re-check at t=" ~ t ~ ": defect " ~ defect);
            }
        }

        // ---------- DERIVATIVE PARITY (central finite differences) ----------
        const finiteDifferenceStep = 1e-5;
        for (var t in [0.15, 0.33, 0.52, 0.71, 0.88])
        {
            const sample = motionAt(motion, t);
            const ahead = motionAt(motion, t + finiteDifferenceStep);
            const behind = motionAt(motion, t - finiteDifferenceStep);
            for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
            {
                checkCount += 2;
                const velocityByDifference = (ahead.columns[columnIndex] - behind.columns[columnIndex]) / (2 * finiteDifferenceStep);
                if (norm(velocityByDifference - sample.columnVelocities[columnIndex]) >
                    1e-5 * (1 + norm(sample.columnVelocities[columnIndex])))
                {
                    failures = append(failures, "DERIVATIVE: column " ~ columnIndex ~ " velocity at t=" ~ t);
                }
                const accelerationByDifference = (ahead.columnVelocities[columnIndex] - behind.columnVelocities[columnIndex]) / (2 * finiteDifferenceStep);
                if (norm(accelerationByDifference - sample.columnAccelerations[columnIndex]) >
                    1e-4 * (1 + norm(sample.columnAccelerations[columnIndex])))
                {
                    failures = append(failures, "DERIVATIVE: column " ~ columnIndex ~ " acceleration at t=" ~ t);
                }
            }
            checkCount += 2;
            const translationVelocityByDifference = (ahead.translation - behind.translation) / (2 * finiteDifferenceStep);
            if (norm(translationVelocityByDifference - sample.translationVelocity) >
                1e-5 * (1 + norm(sample.translationVelocity)))
            {
                failures = append(failures, "DERIVATIVE: translation velocity at t=" ~ t);
            }
            const translationAccelerationByDifference = (ahead.translationVelocity - behind.translationVelocity) / (2 * finiteDifferenceStep);
            if (norm(translationAccelerationByDifference - sample.translationAcceleration) >
                1e-4 * (1 + norm(sample.translationAcceleration)))
            {
                failures = append(failures, "DERIVATIVE: translation acceleration at t=" ~ t);
            }
        }

        // ---------- TRAJECTORY EXACTNESS ----------
        // The transported control net must reproduce applyMotion EXACTLY (spec section 2.1).
        // std evaluateSpline is faithful here because the trajectory is non-rational.
        const toolPoint = vector(0.013, -0.007, 0.021);
        const trajectory = trajectoryCurveOf(motion, toolPoint);
        const trajectoryWithUnits = bSplineCurve({
                    "degree" : trajectory.degree,
                    "isPeriodic" : trajectory.isPeriodic,
                    "controlPoints" : attachMeters(trajectory.controlPoints),
                    "knots" : trajectory.knots
                });
        const trajectoryParameters = [0, 0.18, 0.35, 0.5, 0.64, 0.83, 1];
        const trajectoryEvaluations = evaluateSpline({
                    "spline" : trajectoryWithUnits,
                    "parameters" : trajectoryParameters,
                    "nDerivatives" : 1
                });
        for (var parameterIndex = 0; parameterIndex < size(trajectoryParameters); parameterIndex += 1)
        {
            const t = trajectoryParameters[parameterIndex];
            const sample = motionAt(motion, t);
            checkCount += 2;
            const positionDelta = norm(trajectoryEvaluations[0][parameterIndex] / meter - applyMotion(sample, toolPoint));
            if (positionDelta > 1e-9)
            {
                failures = append(failures, "TRAJECTORY position at t=" ~ t ~ ": delta " ~ positionDelta);
            }
            const velocityDelta = norm(trajectoryEvaluations[1][parameterIndex] / meter - motionVelocityAt(sample, toolPoint));
            if (velocityDelta > 1e-7)
            {
                failures = append(failures, "TRAJECTORY velocity at t=" ~ t ~ ": delta " ~ velocityDelta);
            }
        }

        // ---------- SNAPSHOT TRANSFORMS ----------
        for (var stationIndex in [0, size(motion.stationFrames) - 1])
        {
            checkCount += 1;
            const snapshot = motionSnapshotTransform(motion, stationIndex);
            const mappedStart = snapshot * motion.stationFrames[0].origin;
            if (norm(mappedStart - motion.stationFrames[stationIndex].origin) > 1e-9 * meter)
            {
                failures = append(failures, "SNAPSHOT: station " ~ stationIndex ~ " maps start origin " ~
                    norm(mappedStart - motion.stationFrames[stationIndex].origin) ~ " away");
            }
        }

        // ---------- MODE AND EVENTS ----------
        if (definition.keepOrientation)
        {
            for (var t in [0.1, 0.45, 0.98])
            {
                checkCount += 1;
                const sample = motionAt(motion, t);
                if (norm(sample.columns[0] - vector(1, 0, 0)) > 1e-10 ||
                    norm(sample.columns[1] - vector(0, 1, 0)) > 1e-10 ||
                    norm(sample.columns[2] - vector(0, 0, 1)) > 1e-10)
                {
                    failures = append(failures, "KEEP-ORIENTATION: rotation not identity at t=" ~ t);
                }
            }
        }
        checkCount += 1;
        if (size(motion.events) != size(motion.path.edges) - 1)
        {
            failures = append(failures, "EVENTS: expected " ~ (size(motion.path.edges) - 1) ~
                " junction event(s), got " ~ size(motion.events));
        }

        // ---------- DEBUG DRAWING ----------
        if (definition.drawStationFrames)
        {
            const axisLength = motion.pathLength * 0.03;
            for (var frame in motion.stationFrames)
            {
                addDebugLine(context, frame.origin, frame.origin + axisLength * frame.xAxis, DebugColor.BLUE);
                addDebugLine(context, frame.origin, frame.origin + axisLength * frame.zAxis, DebugColor.RED);
            }
        }

        // ---------- REPORT ----------
        for (var failure in failures)
        {
            println("[MOTION TESTER] FAIL: " ~ failure);
        }
        const summary = size(failures) == 0 ?
            ("All " ~ checkCount ~ " checks passed (" ~ size(motion.stationParameters) ~ " stations, drift " ~
                    motion.orthogonalityDrift ~ ").") :
            (size(failures) ~ " of " ~ checkCount ~ " checks FAILED - see console.");
        println("[MOTION TESTER] " ~ summary);
        reportFeatureInfo(context, id, summary);
    });

// ===================== Tester helpers =====================

