FeatureScript 2345;
import(path : "onshape/std/common.fs", version : "2345.0");

descriptionImage::import(path : "7dbf72c51198694b8409256d", version : "b360feb440133437b5b10f35");
featureIcon::import(path : "77dc7885c854be8b5a7f98df", version : "a6a6357b44c24d8b6ee852aa");



annotation { "Feature Type Name" : "Curve Eval",
        "Feature Type Description" : "Evaluate a curve/edge.<br>" ~
        "This feature displays the control point grids, knots, and other details.<br>" ~
        "If the selected entity is not a spline, then a spline approximation is used.",
        "Icon":featureIcon::BLOB_DATA,
        "Description Image" : descriptionImage::BLOB_DATA }
export const evaluator = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edge/Curve to analyze", "Filter" : EntityType.EDGE && ConstructionObject.NO && AllowMeshGeometry.NO, "MaxNumberOfPicks" : 1 }
        definition.myEdge is Query;

        annotation { "Name" : "Show control point grids" }
        definition.isCP is boolean;

        annotation { "Name" : "Show knots" }
        definition.isKnots is boolean;

        annotation { "Name" : "Show report", "Description" : "Display additional diagnostic info in the FeatureScript notices panel" }
        definition.isReport is boolean;
    }

    {
        verifyNonemptyQuery(context, definition, "myEdge", "Select edge to evaluate");

        // Get the edge/curve definition...
        var edgeEval = evCurveDefinition(context, {
                "edge" : definition.myEdge
            });

        // If the curve is not a SPLINE, then evaluate an approximation instead
        // For example, when the curve is a projection, intersection curve, sp curve, etc
        if (edgeEval.curveType != CurveType.SPLINE)
        {
            edgeEval = evApproximateBSplineCurve(context, {
                        "edge" : definition.myEdge,
                        "forceCubic" : false,
                        "forceNonRational" : false
                    });
        }

        const numIntKNots = size(deduplicate(edgeEval.knots)) - 2;        
        

        reportFeatureInfo(context, id, "Degree = " ~ edgeEval.degree ~ ", Num spans = " ~ numIntKNots + 1 ~ ", with " ~ size(edgeEval.controlPoints) ~ " control points, and is " ~ (edgeEval.isRational == true ? "rational" : "non-rational"));

        if (definition.isCP)
        {
            displayControlPolygon(context, edgeEval.controlPoints);
        }

        if (definition.isKnots)
        {
            displayKnots(context, edgeEval.knots, definition.myEdge);
        }

        if (definition.isReport)
        {
            debug(context, "Degree = " ~ edgeEval.degree);
            debug(context, "Rational = " ~ edgeEval.isRational);
            debug(context, "Periodic = " ~ edgeEval.isPeriodic);
            debug(context, "Control Points = " ~ size(edgeEval.controlPoints));
            debug(context, edgeEval.knots);
            debug(context, "Knots = " ~ size(edgeEval.knots));
            debug(context, "There are " ~ numIntKNots ~ " internal knots");
            debug(context, numIntKNots == 0 ? "Single Span Bezier" : "Multi-span bspline (" ~ numIntKNots + 1 ~ " spans)");
        }
    });

// Displays the knots for the bspline
function displayKnots(context is Context, knots is array, edge is map)
{
    if (knots == [])
        return;

    // Rescale the knots
    const start = knots[0];
    const domainLength = knots[size(knots) - 1] - start;
    const paramScale = 1 / domainLength;

    for (var i = 0; i < size(knots) - 1; i += 1)
    {
        try silent
        {
            var edgeCrv = evEdgeCurvature(context, {
                    "edge" : edge,
                    "parameter" : (knots[i] - start) * paramScale,
                    "arcLengthParameterization" : false
                });
            addDebugPoint(context, edgeCrv.frame.origin, DebugColor.BLACK);
        }
    }
}

// Displays the control polygon for the bspline
function displayControlPolygon(context is Context, points is array)
{
    for (var i = 0; i < size(points) - 1; i += 1)
    {
        addDebugLine(context, points[i], points[i + 1], DebugColor.CYAN);
        addDebugPoint(context, points[i + 1], DebugColor.MAGENTA);
    }
    addDebugPoint(context, points[0], DebugColor.MAGENTA);
}

