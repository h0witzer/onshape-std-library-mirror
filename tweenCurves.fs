FeatureScript 2656;
import(path : "onshape/std/common.fs", version : "2656.0");
import(path : "onshape/std/geometry.fs", version : "2656.0");

/**
 * Defines bounds for the tween fraction parameter, ensuring it's between 0 and 1.
 * Default is 0.5 (halfway).
 */
const TWEEN_FRACTION_BOUNDS = { (unitless) : [0, 0.5, 1] } as RealBoundSpec;

/**
 * Defines bounds for the number of sample points used for interpolation.
 * Minimum 2 points are required to define a curve.
 */
const SAMPLE_POINTS_BOUNDS = { (unitless) : [2, 20, 200] } as IntegerBoundSpec; // Max increased for potentially complex curves

/**
* @FeatureTypeName "Tween Curve"
 * @UIHint NO_PREVIEW_PROVIDED // opFitSpline doesn't offer a direct preview for the feature itself
 *
 * Creates a curve that is a "tween" or interpolation between two input curves.
 * The tweening is controlled by a fraction, where 0 results in a curve identical
 * to the first input curve, 1 results in a curve identical to the second,
 * and 0.5 results in a curve halfway between them.
 */


annotation { "Feature Type Name" : "Tween Two Curves", "Feature Type Description" : "Takes two curves and does a tween. Approximately." }
export const tweenTwoCurves = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "First curve",
                    "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO,
                    "MaxNumberOfPicks" : 1 }
        definition.curve1 is Query;

        annotation { "Name" : "Second curve",
                    "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO,
                    "MaxNumberOfPicks" : 1 }
        definition.curve2 is Query;

        annotation { "Name" : "Tween fraction",
                    "Description" : "0 = like first curve, 0.5 = halfway, 1 = like second curve." }
        isReal(definition.fraction, TWEEN_FRACTION_BOUNDS);

        annotation { "Name" : "Number of sample points",
                    "Description" : "More points for better accuracy. Minimum 2." }
        isInteger(definition.numSamples, SAMPLE_POINTS_BOUNDS);

    }
    {
        if (evaluateQueryCount(context, definition.curve1) == 0)
            throw regenError("Please select the first curve.", ["curve1"]);
        if (evaluateQueryCount(context, definition.curve2) == 0)
            throw regenError("Please select the second curve.", ["curve2"]);

        var autoDecidedFlip = false;
        // --- Automatic Flip Detection Logic ---
        var startPoint1 = evEdgeTangentLine(context, { "edge" : definition.curve1, "parameter" : 0.0, "arcLengthParameterization" : false }).origin;
        var endPoint1 = evEdgeTangentLine(context, { "edge" : definition.curve1, "parameter" : 1.0, "arcLengthParameterization" : false }).origin;
        var vecDir1 = endPoint1 - startPoint1;

        var startPoint2 = evEdgeTangentLine(context, { "edge" : definition.curve2, "parameter" : 0.0, "arcLengthParameterization" : false }).origin;
        var endPoint2 = evEdgeTangentLine(context, { "edge" : definition.curve2, "parameter" : 1.0, "arcLengthParameterization" : false }).origin;
        var vecDir2 = endPoint2 - startPoint2;

        // Only attempt to normalize and compare if vectors are non-zero (handles closed curves or points)
        // if (!tolerantEqualsZero(norm(vecDir1), VECTOR_LENGTH_TOLERANCE) && !tolerantEqualsZero(norm(vecDir2), VECTOR_LENGTH_TOLERANCE))
        // {
        var dir1 = normalize(vecDir1);
        var dir2 = normalize(vecDir2);
        var alignment = dot(dir1, dir2);

        if (alignment < 0) // If dot product is negative, general directions are opposed
        {
            autoDecidedFlip = true;
        }
        // If one or both curves are effectively points (zero length start-to-end vector),
        // autoDecidedFlip remains false, which is a safe default.
        // --- End of Automatic Flip Detection Logic ---

        var shouldFlipSecondCurve = autoDecidedFlip;

        var points1 = [];
        var points2 = [];
        const numSamples = definition.numSamples;

        for (var i = 0; i < numSamples; i += 1)
        {
            var t_param_curve1 = (numSamples == 1) ? 0.5 : i / (numSamples - 1);
            var t_param_curve2 = t_param_curve1;

            if (shouldFlipSecondCurve)
            {
                t_param_curve2 = 1.0 - t_param_curve1;
            }

            var lineOnCurve1 = evEdgeTangentLine(context, {
                    "edge" : definition.curve1, "parameter" : t_param_curve1, "arcLengthParameterization" : true
                });
            points1 = append(points1, lineOnCurve1.origin);

            var lineOnCurve2 = evEdgeTangentLine(context, {
                    "edge" : definition.curve2, "parameter" : t_param_curve2, "arcLengthParameterization" : true
                });
            points2 = append(points2, lineOnCurve2.origin);
        }

        var tweenedPoints = [];
        const fraction = definition.fraction;
        for (var i = 0; i < numSamples; i += 1)
        {
            var p1 = points1[i];
            var p2 = points2[i];
            var interpolatedPoint = p1 * (1 - fraction) + p2 * fraction;
            tweenedPoints = append(tweenedPoints, interpolatedPoint);
        }

        if (size(tweenedPoints) >= 2)
        {
            opFitSpline(context, id + "tweenedSpline", { "points" : tweenedPoints });
        }
        else
        {
            throw regenError("Not enough sample points to create a spline (minimum 2 required).", ["numSamples"]);
        }
    });
