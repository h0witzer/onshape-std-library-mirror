FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/splineUtils.fs", version : "3044.0");    // evaluateSpline
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");  // BSplineCurve, bSplineCurve

/**
 * STANDALONE probe of std evaluateSpline's weight handling, against a curve YOU drew.
 *
 * Deliberately independent of splineRefinementUtils and its tester: this file imports ONLY the
 * Onshape standard library, contains no fixtures, and does no spline mathematics of its own.
 * Every number it reports comes from the kernel:
 *
 *   1. evCurveDefinition reads YOUR edge's B-spline definition (weights printed verbatim, so
 *      you can confirm your non-uniform weights actually arrived).
 *   2. evaluateSpline - the function under suspicion - evaluates that definition at a spread
 *      of parameters across its own knot domain.
 *   3. evDistance - a separate kernel subsystem, the same machinery behind measurements -
 *      reports how far each returned point sits from your ACTUAL drawn edge. If
 *      evaluateSpline is faithful, every point lies on the curve and this column is ~0.
 *   4. The same control points and knots are fed back through evaluateSpline with the weights
 *      OMITTED (a non-rational twin). If the suspect's points coincide with the twin's, the
 *      mechanism is specifically that the weights were dropped.
 *
 * Each evaluated point is also drawn as a debug point while the feature dialog is open:
 * GREEN if it lies on your curve (within 1e-7 m), RED if it does not. The only arithmetic in
 * this file is a linear spread of parameters and one vector subtraction for column 4.
 *
 * To make a curve where weights matter: draw an arc or circle and run Onshape's native
 * Edit Curve on it (exact conversion to a rational B-spline), or edit a spline's control
 * point weights directly in Edit Curve. A plain sketched spline is NON-rational (weights all
 * 1), and on such a curve this probe cannot discriminate - it will say so.
 */
annotation { "Feature Type Name" : "Evaluate Spline Weights Probe" }
export const evaluateSplineWeightsProbe = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Spline edge", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.splineEdge is Query;
    }
    {
        const curveDefinition = evCurveDefinition(context, { "edge" : definition.splineEdge });
        if (!(curveDefinition is BSplineCurve))
        {
            println("[WEIGHTS PROBE] evCurveDefinition did not return a B-spline. It returned: " ~ curveDefinition);
            throw regenError("The picked edge is not a B-spline curve. Run Onshape's native Edit Curve on it first " ~
                "(which converts arcs/circles to rational B-splines exactly, and lets you edit spline weights directly).");
        }
        if (curveDefinition.dimension != 3)
        {
            throw regenError("The picked edge reports dimension " ~ curveDefinition.dimension ~ "; this probe expects a 3D world-space edge.");
        }

        println("[WEIGHTS PROBE] degree: " ~ curveDefinition.degree ~
            ", isRational: " ~ curveDefinition.isRational ~
            ", isPeriodic: " ~ curveDefinition.isPeriodic ~
            ", control points: " ~ size(curveDefinition.controlPoints));
        println("[WEIGHTS PROBE] weights as read from YOUR curve: " ~ curveDefinition.weights);
        println("[WEIGHTS PROBE] knots: " ~ curveDefinition.knots);

        var weightsMatter = false;
        if (curveDefinition.isRational == true && curveDefinition.weights != undefined)
        {
            for (var weight in curveDefinition.weights)
            {
                if (abs(weight - 1) > 1e-9)
                {
                    weightsMatter = true;
                }
            }
        }
        if (!weightsMatter)
        {
            println("[WEIGHTS PROBE] WARNING: every weight on this curve is 1 (or absent) - the rational and " ~
                "weightless evaluations of this curve are IDENTICAL by definition, so this run cannot tell " ~
                "whether weights are respected. Draw a curve with genuinely non-uniform weights.");
        }

        // Parameters spread across the definition's own knot domain.
        const degree = curveDefinition.degree;
        const knots = curveDefinition.knots;
        const domainStart = knots[degree];
        const domainEnd = knots[size(knots) - degree - 1];
        const sampleCount = 15;
        var parameters = makeArray(sampleCount, 0);
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
        {
            parameters[sampleIndex] = domainStart + (domainEnd - domainStart) * sampleIndex / (sampleCount - 1);
        }

        // The suspect, and its weightless twin (same points, same knots, weights omitted).
        const suspectPoints = evaluateSpline({ "spline" : curveDefinition, "parameters" : parameters })[0];
        const weightlessTwin = bSplineCurve({
                    "degree" : curveDefinition.degree,
                    "isPeriodic" : curveDefinition.isPeriodic,
                    "controlPoints" : curveDefinition.controlPoints,
                    "knots" : curveDefinition.knots
                });
        const weightlessPoints = evaluateSpline({ "spline" : weightlessTwin, "parameters" : parameters })[0];

        const onCurveTolerance = 1e-7 * meter;
        var maxOffCurve = 0 * meter;
        var maxWeightlessDelta = 0 * meter;
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
        {
            const offCurve = evDistance(context, {
                        "side0" : suspectPoints[sampleIndex],
                        "side1" : definition.splineEdge
                    }).distance;
            const weightlessDelta = norm(suspectPoints[sampleIndex] - weightlessPoints[sampleIndex]);
            maxOffCurve = max(maxOffCurve, offCurve);
            maxWeightlessDelta = max(maxWeightlessDelta, weightlessDelta);
            println("[WEIGHTS PROBE] u=" ~ parameters[sampleIndex] ~
                "  distance from YOUR curve: " ~ offCurve ~
                "  distance from the weightless twin: " ~ weightlessDelta);
            addDebugPoint(context, suspectPoints[sampleIndex],
                offCurve > onCurveTolerance ? DebugColor.RED : DebugColor.GREEN);
        }

        var verdict;
        if (!weightsMatter)
        {
            verdict = "Inconclusive: this curve's weights are all 1, so nothing here can discriminate. " ~
                "Pick a curve with non-uniform weights.";
        }
        else if (maxOffCurve <= onCurveTolerance)
        {
            verdict = "evaluateSpline's points all lie ON your curve (max off-curve " ~ maxOffCurve ~
                ") - it respected the weights for this curve.";
        }
        else if (maxWeightlessDelta <= onCurveTolerance)
        {
            verdict = "evaluateSpline's points are OFF your curve by up to " ~ maxOffCurve ~
                ", and coincide with the weights-OMITTED evaluation to within " ~ maxWeightlessDelta ~
                " - it ignored your weights.";
        }
        else
        {
            verdict = "evaluateSpline's points are off your curve by up to " ~ maxOffCurve ~
                " but do NOT match the weightless twin (delta up to " ~ maxWeightlessDelta ~
                ") - some third behavior; report these numbers.";
        }
        println("[WEIGHTS PROBE] VERDICT: " ~ verdict);
        reportFeatureInfo(context, id, verdict);
    });
