FeatureScript 1364;
import(path : "onshape/std/geometry.fs", version : "1364.0");

annotation { "Feature Type Name" : "Evolvent" }
export const myFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Curve edge", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.curveEdge is Query;

        annotation { "Name" : "Flip side" }
        definition.flipSide is boolean;

        annotation { "Name" : "Number of points" }
        isInteger(definition.pointNumber, POSITIVE_COUNT_BOUNDS);
    }
    {
        var path = constructPath(context, definition.curveEdge);
        if (definition.flipSide)
            path = reverse(path);

        var tangentsArr = evPathTangentLines(context, path, range(0, 1, definition.pointNumber)).tangentLines;
        /*
           evEdgeTangentLines(context, {
           "edge" : definition.curveEdge,
           "parameters" : range(0, 1, definition.pointNumber)
           });
         */
        var points = [];
        var length = 0 * meter;
        var lengthStep = evLength(context, { "entities" : definition.curveEdge }) / definition.pointNumber;

        for (var tangent in tangentsArr)
        {
            var point = tangent.origin - tangent.direction * length;
            points = append(points, point);
            length += lengthStep;
        }
        try
        {
            opFitSpline(context, id + "fitSplineSpiral", {
                        "points" : points
                    });
        }

    });
