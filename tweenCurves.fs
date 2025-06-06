FeatureScript 2656; /* Automatically generated version */
// This file defines a feature that blends between two curves.

import(path : "onshape/std/common.fs", version : "2656.0");
import(path : "onshape/std/feature.fs", version : "2656.0");
import(path : "onshape/std/curveGeometry.fs", version : "2656.0");
import(path : "onshape/std/evaluate.fs", version :"2656.0");
import(path : "onshape/std/valueBounds.fs", version :"2656.0");
import(path : "onshape/std/path.fs", version : "2656.0");
import(path : "onshape/std/splineUtils.fs", version : "2656.0");
import(path : "onshape/std/nurbsUtils.fs", version : "2656.0");
import(path : "onshape/std/approximationUtils.fs", version :"2656.0");

/**
 * Create a curve that interpolates between two input curves.
 */
 
 annotation { "Feature Type Name" : "Tween gpt Curves", "Feature Type Description" : "Takes two curves and does a tween. Approximately." }
export const tweenCurves = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "First curve", "Filter" : EntityType.EDGE || (EntityType.BODY && BodyType.WIRE && SketchObject.NO) }
        definition.curve1 is Query;
        annotation { "Name" : "Second curve", "Filter" : EntityType.EDGE || (EntityType.BODY && BodyType.WIRE && SketchObject.NO) }
        definition.curve2 is Query;
        annotation { "Name" : "Factor", "Default" : 0.5 }
        isReal(definition.factor, EDGE_PARAMETER_BOUNDS);
    }
    {
        var bs1 = getBSpline(context, definition.curve1);
        var bs2 = getBSpline(context, definition.curve2);

        var targetDegree = max(bs1.degree, bs2.degree);
        if (bs1.degree < targetDegree)
            bs1 = elevateDegree(bs1, targetDegree);
        if (bs2.degree < targetDegree)
            bs2 = elevateDegree(bs2, targetDegree);

        const SAMPLES = 50;
        var params = makeArray(SAMPLES, 0);
        for (var i = 1; i < SAMPLES; i += 1)
            params[i] = i / (SAMPLES - 1);

        var pts = makeArray(SAMPLES);
        for (var i = 0; i < SAMPLES; i += 1)
        {
            const p1 = evaluateSpline({ "spline" : bs1, "parameters" : [params[i]] })[0][0];
            const p2 = evaluateSpline({ "spline" : bs2, "parameters" : [params[i]] })[0][0];
            pts[i] = (1 - definition.factor) * p1 + definition.factor * p2;
        }

        opFitSpline(context, id + "tween", { "points" : pts });
    }, {factor : 0.5});

//================================================================================
// Helper functions
//================================================================================

function getBSpline(context is Context, q is Query) returns BSplineCurve
{
    const edges = evaluateQuery(context, qEntityFilter(q, EntityType.EDGE));
    if (size(edges) != 1)
    {
        var path = constructPath(context, q, { "tolerance" : 1e-5 * meter }).path;
        const target = makeApproximationTarget(context, path, false, false);
        return approximateSpline(context, { "degree" : 3, "tolerance" : 1e-6 * meter,
                            "isPeriodic" : path.closed, "targets" : [target],
                            "maxControlPoints" : MAX_CONTROL_POINTS })[0];
    }
    const edge = edges[0];
    const def = evCurveDefinition(context, { "edge" : edge, "simplify" : true });
    var bspline;
    if (def is Line)
    {
        const verts = evaluateQuery(context, qAdjacent(edge, AdjacencyType.VERTEX, EntityType.VERTEX));
        bspline = bSplineCurve({ "degree" : 1, "isPeriodic" : false,
                    "controlPoints" : [evVertexPoint(context, { "vertex" : verts[0] }), evVertexPoint(context, { "vertex" : verts[1] })] });
    }
    else if (def is BSplineCurve)
    {
        bspline = def;
    }
    else
    {
        bspline = evApproximateBSplineCurve(context, { "edge" : edge });
    }
    if (!bspline.isRational)
    {
        bspline.weights = makeArray(size(bspline.controlPoints), 1);
        bspline.isRational = true;
    }
    return bspline;
}

function subdivideIntoBeziers(points is array, knots is array, curveDegree is number) returns array
{
    var numSplits = 0;
    for (var i = curveDegree + 1; i < size(knots) - curveDegree - 1; i += 1)
    {
        if (knots[i] != knots[i + 1])
        {
            numSplits += 1;
        }
    }
    const overlappingKnots = knots[0] < 0;
    if (overlappingKnots)
    {
        numSplits += 2;
        for (var i = 0; i < curveDegree + 2; i += 1)
        {
            points = append(points, points[i]);
        }
    }
    var currentKnots = knots;
    var currentPoints = points;
    var beziers = makeArray(numSplits + 1);

    for (var i = 0; i < numSplits; i += 1)
    {
        const bezierAndSpline = splitAtFirstKnot(currentPoints, currentKnots, curveDegree);
        beziers[i] = bezierAndSpline.bezier;
        currentPoints = bezierAndSpline.bspline;
        currentKnots = bezierAndSpline.knots;
    }
    beziers[numSplits] = currentPoints;
    if (overlappingKnots)
    {
        beziers = subArray(beziers, 1, size(beziers) - 1);
    }
    return beziers;
}

// Returns the first bezier subdivision and the rest of the curve.
// We apply DeBoor's algorithm, which gives the segment subdivision of the bspline.
// The Bezier points are the first points of each level of segmentation.
// The last points of each level of segmentation are prepended to the bspline.
// See https://doi.org/10.1007/978-3-642-59223-2
function splitAtFirstKnot(points is array, knots is array, curveDegree is number) returns map
{
    if (size(points) == curveDegree + 1)
    {
        // This is a Bezier curve, no need to do anything.
        return {};
    }
    // first knot's index (k) is d + 1
    var k = curveDegree + 1;
    // Value of first knot
    const u = knots[k];
    // multiplicity of the knot
    var s = 1;
    for (var i = k + 1; i < size(knots); i += 1)
    {
        if (knots[i] != knots[k])
        {
            break;
        }
        s += 1;
        k += 1;
    }
    // De boor
    const h = curveDegree - s;
    var result = makeArray(h + 1);
    result[0] = subArray(points, k - curveDegree, k - s + 1);
    for (var r = 1; r <= h; r += 1)
    {
        result[r] = makeArray(curveDegree - s - r + 1);
        for (var i = 0; i <= curveDegree - s - r; i += 1)
        {
            const knotIndex = i + k - curveDegree + r;
            const alpha = (u - knots[knotIndex]) / (knots[knotIndex + curveDegree - r + 1] - knots[knotIndex]);
            result[r][i] = (1 - alpha) * result[r - 1][i] + alpha * result[r - 1][i + 1];
        }
    }
    // Extracting the bezier points from the segmentation
    const bezierPointsBeforeDeBoor = subArray(points, 0, k - curveDegree);
    const bsplinePointsAfterDeBoor = subArray(points, k - s + 1, size(points));
    var bezierPointsInDeBoor = makeArray(h + 1);
    var bsplinePontsInDeBoor = makeArray(h + 1);
    for (var r = 0; r <= h; r += 1)
    {
        bezierPointsInDeBoor[r] = result[r][0];
        bsplinePontsInDeBoor[r] = result[h - r][size(result[h - r]) - 1];
    }
    const bezier = concatenateArrays([bezierPointsBeforeDeBoor, bezierPointsInDeBoor]);
    const bspline = concatenateArrays([bsplinePontsInDeBoor, bsplinePointsAfterDeBoor]);
    const newKnots = concatenateArrays([makeArray(curveDegree + 1, u), subArray(knots, k + 1, size(knots))]);
    return { "bezier" : bezier, "bspline" : bspline, "knots" : newKnots };
}

function elevateDegree(bspline is map, newDegree is number) returns map
{
    const weightedPoints = combinePointsAndWeights(bspline.controlPoints, bspline.weights);

    var newPoints;
    if (isBezier(bspline.controlPoints, bspline.degree, bspline.knots))
    {
        newPoints = elevateBezierDegree(weightedPoints, newDegree);
        bspline.knots = makeUniformKnotArray(newDegree, size(newPoints), false);
    }
    else
    {
        const pointsAndKnots = elevateBSpline(weightedPoints, bspline.knots, bspline.degree, newDegree);
        newPoints = pointsAndKnots.points;
        bspline.knots = pointsAndKnots.knots as KnotArray;
    }

    const pointsAndWeights = separatePointsAndWeights(newPoints);
    bspline.controlPoints = pointsAndWeights.points;
    bspline.weights = pointsAndWeights.weights;
    bspline.degree = newDegree;

    return bspline;
}

function elevateBSpline(originalPoints is array, originalKnots is array, originalDegree is number, newDegree is number) returns map
{
    // First we subdivide the bspline into bezier curves
    var beziers = subdivideIntoBeziers(originalPoints, originalKnots, originalDegree);

    // Then we elevate each bezier curve separately
    for (var i = 0; i < size(beziers); i += 1)
    {
        beziers[i] = elevateBezierDegree(beziers[i], newDegree);
    }
    // Then we combine the beziers into one bspline
    var points = [beziers[0][0]];
    for (var i = 0; i < size(beziers); i += 1)
    {
        for (var j = 1; j < size(beziers[i]); j += 1)
        {
            points = append(points, beziers[i][j]);
        }
    }
    // We make the corresponding knot vector, which is the same knot vector but with added multiplicity
    const lastKnot = originalKnots[size(originalKnots) - originalDegree - 1];
    var i = originalDegree + 1;
    var currentKnot = originalKnots[i];
    var newKnots = makeArray(newDegree + 1, originalKnots[i - 1]);
    while (currentKnot != lastKnot)
    {
        newKnots = concatenateArrays([newKnots, makeArray(newDegree, currentKnot)]);
        // We skip identical knots
        while (originalKnots[i] == currentKnot)
        {
            i += 1;
        }
        currentKnot = originalKnots[i];
    }
    newKnots = concatenateArrays([newKnots, makeArray(newDegree + 1, lastKnot)]);
    // Then we simplify
    return removeKnots(points, newKnots, newDegree);
}

function isBezier(points is array, curveDegree is number, knots is array) returns boolean
{
    return size(points) == curveDegree + 1 && knots[0] == 0;
}
