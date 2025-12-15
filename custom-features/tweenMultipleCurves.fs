FeatureScript 2837;

// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/approximationUtils.fs", version : "2837.0");
import(path : "onshape/std/splineUtils.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/path.fs", version : "2837.0");

// Import tweenCurves function from tweenCurves.fs
// NOTE: Replace with actual document ID once published
// For now using placeholder - this would be: import(path : "DOCUMENT_ID/VERSION_ID/ELEMENT_ID", version : "VERSION_HASH");
// Until then, we'll need to use a local copy or reference

/**
 * Defines the method for matching curve segments between two paths.
 */
export enum TweenConnectionMethod
{
    annotation { "Name" : "Nearest distance" }
    NEAREST_DISTANCE,
    annotation { "Name" : "Path length parameterization" }
    PATH_LENGTH
}

export const TWEEN_FRACTION_BOUNDS = { (unitless) : [0, 0.5, 1] } as RealBoundSpec;

annotation { 
    "Feature Type Name" : "Tween Multiple Curves",
    "Feature Type Description" : "Interpolates B-spline control points between two paths of multiple curves. For single curves, delegates to the standard tweenCurves function.",
    "UIHint" : "NO_PREVIEW_PROVIDED" 
}
export const tweenMultipleCurves = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Connection method", "UIHint" : UIHint.SHOW_LABEL }
        definition.connectionMethod is TweenConnectionMethod;
        
        annotation { "Name" : "First curve or edge group", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO }
        definition.curves1 is Query;
        
        annotation { "Name" : "Second curve or edge group", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO }
        definition.curves2 is Query;
        
        annotation { "Name" : "Tween fraction" }
        isReal(definition.fraction, TWEEN_FRACTION_BOUNDS);
    }
    {
        // Get all connected edges for each input
        const edgeGroup1 = qTangentConnectedEdges(definition.curves1);
        const edgeGroup2 = qTangentConnectedEdges(definition.curves2);
        
        const edgeCount1 = evaluateQueryCount(context, edgeGroup1);
        const edgeCount2 = evaluateQueryCount(context, edgeGroup2);
        
        // If both are single edges, use the exact tweenCurves logic
        if (edgeCount1 == 1 && edgeCount2 == 1)
        {
            // Call the core tween logic directly for single curve case
            tweenSingleCurvePair(context, id, qNthElement(edgeGroup1, 0), qNthElement(edgeGroup2, 0), definition.fraction);
            return;
        }
        
        // For multiple edges, we need to determine break points and tween subsegments
        // Build paths
        const path1 = constructPath(context, edgeGroup1);
        const path2 = constructPath(context, edgeGroup2);
        
        // Generate break points based on method
        var breakPoints = [];
        if (definition.connectionMethod == TweenConnectionMethod.PATH_LENGTH)
        {
            breakPoints = generatePathLengthBreakPoints(context, path1, path2, edgeGroup1, edgeGroup2);
        }
        else
        {
            breakPoints = generateNearestDistanceBreakPoints(context, path1, path2, edgeGroup1, edgeGroup2);
        }
        
        // Tween each segment pair
        for (var i = 0; i < size(breakPoints) - 1; i += 1)
        {
            const start1 = breakPoints[i].segment1;
            const end1 = breakPoints[i + 1].segment1;
            const start2 = breakPoints[i].segment2;
            const end2 = breakPoints[i + 1].segment2;
            
            // For now, if segment is a full edge, tween it
            if (start1.edge == end1.edge && start1.parameter == 0.0 && end1.parameter == 1.0)
            {
                if (start2.edge == end2.edge && start2.parameter == 0.0 && end2.parameter == 1.0)
                {
                    tweenSingleCurvePair(context, id + ("seg_" ~ i), start1.edge, start2.edge, definition.fraction);
                }
            }
        }
    });

/**
 * Tween a single curve pair using the same logic as tweenCurves.
 * This is the core tweening function that handles all the complexity.
 */
function tweenSingleCurvePair(context is Context, id is Id, curve1 is Query, curve2 is Query, fraction is number)
{
    if (evaluateQueryCount(context, curve1) == 0)
        throw regenError("Select first curve.", ["curves1"]);
    if (evaluateQueryCount(context, curve2) == 0)
        throw regenError("Select second curve.", ["curves2"]);

    const edge1 = evaluateQuery(context, curve1)[0];
    const edge2 = evaluateQuery(context, curve2)[0];

    const def1 = evCurveDefinition(context, { "edge" : edge1, "returnBSplinesAsOther" : true });
    const def2 = evCurveDefinition(context, { "edge" : edge2, "returnBSplinesAsOther" : true });

    // Handle circles and lines specially
    if ((def1 is Circle && def2 is Circle) ||
        (def1 is Circle && def2 is Line) ||
        (def1 is Line && def2 is Circle))
    {
        tweenCircleOrLine(context, id, edge1, edge2, fraction);
        return;
    }

    var bSpline1 = getBSplineFromInput(context, curve1);
    var bSpline2 = getBSplineFromInput(context, curve2);

    if (bSpline1 == undefined || bSpline2 == undefined)
        throw regenError("Could not get B-spline representation for input curves.");

    // Match degrees
    if (bSpline1.degree != bSpline2.degree)
    {
        const targetDegree = max(bSpline1.degree, bSpline2.degree);
        if (bSpline1.degree < targetDegree)
        {
            bSpline1 = elevateDegree(bSpline1, targetDegree);
        }
        if (bSpline2.degree < targetDegree)
        {
            bSpline2 = elevateDegree(bSpline2, targetDegree);
        }
    }

    // Match control point counts
    if (size(bSpline1.controlPoints) != size(bSpline2.controlPoints))
    {
        const targetCount = max(size(bSpline1.controlPoints), size(bSpline2.controlPoints));
        const cpFractions1 = computeControlPointFractions(bSpline1.controlPoints);
        const cpFractions2 = computeControlPointFractions(bSpline2.controlPoints);

        if (size(bSpline1.controlPoints) < targetCount)
        {
            bSpline1 = matchCPCount(context, bSpline1, targetCount, cpFractions2);
        }
        if (size(bSpline2.controlPoints) < targetCount)
        {
            bSpline2 = matchCPCount(context, bSpline2, targetCount, cpFractions1);
        }
    }

    var cpList1 = bSpline1.controlPoints;
    var cpList2_orig = bSpline2.controlPoints;
    var weights1 = bSpline1.weights;
    var weights2_orig = bSpline2.weights;

    // Handle periodic curves
    if (bSpline1.isPeriodic && bSpline2.isPeriodic && size(cpList1) == size(cpList2_orig))
    {
        const shiftNormal = bestPeriodicShift(cpList1, cpList2_orig);
        const normalRot = rotateArray(cpList2_orig, -shiftNormal);
        var normalWeightRot = weights2_orig == undefined ? undefined : rotateArray(weights2_orig, -shiftNormal);
        const distNormal = sumDistances(cpList1, normalRot);

        const reversed = reverse(cpList2_orig);
        const shiftRev = bestPeriodicShift(cpList1, reversed);
        const revRot = rotateArray(reversed, -shiftRev);
        var revWeightRot = weights2_orig == undefined ? undefined : rotateArray(reverse(weights2_orig), -shiftRev);
        const distRev = sumDistances(cpList1, revRot);

        if (distRev < distNormal)
        {
            cpList2_orig = revRot;
            if (weights2_orig != undefined)
                weights2_orig = revWeightRot;
        }
        else
        {
            cpList2_orig = normalRot;
            if (weights2_orig != undefined)
                weights2_orig = normalWeightRot;
        }
    }

    // Auto-flip detection
    var autoDecidedFlip = false;
    if (!(bSpline1.isPeriodic && bSpline2.isPeriodic))
    {
        try
        {
            if (size(cpList1) > 1 && size(cpList2_orig) > 1)
            {
                var dir1 = normalize(cpList1[1] - cpList1[0]);
                var dir2 = normalize(cpList2_orig[1] - cpList2_orig[0]);
                if (dot(dir1, dir2) < 0)
                {
                    autoDecidedFlip = true;
                }
            }
        }
        catch
        {
        }
    }

    var finalCpList2 = autoDecidedFlip ? reverse(cpList2_orig) : cpList2_orig;
    var finalWeights2 = bSpline2.isRational && autoDecidedFlip ? reverse(weights2_orig) : weights2_orig;

    // Compatibility checks
    if (bSpline1.degree != bSpline2.degree)
    {
        throw regenError("Failed to match curve degrees after elevation.", ["curves1", "curves2"]);
    }
    if (size(cpList1) != size(finalCpList2))
    {
        throw regenError("Curves have different B-spline control point counts (" ~ size(cpList1) ~ " vs " ~ size(finalCpList2) ~ ").", ["curves1", "curves2"]);
    }
    if (bSpline1.isRational != bSpline2.isRational)
    {
        throw regenError("Curves have different rationality. Both must be rational or non-rational.", ["curves1", "curves2"]);
    }

    // Interpolate control points
    var tweenedCps = [];
    var tweenedWeights = [];

    for (var i = 0; i < size(cpList1); i += 1)
    {
        const weight1 = weights1[i];
        const weight2 = finalWeights2[i];
        const blendedWeight = weight1 * (1 - fraction) + weight2 * fraction;

        const pos1 = cpList1[i];
        const pos2 = finalCpList2[i];
        const blendedPos = pos1 * (1 - fraction) + pos2 * fraction;

        tweenedCps = append(tweenedCps, blendedPos / blendedWeight);
        tweenedWeights = append(tweenedWeights, blendedWeight);
    }

    var isPeriodicTween = bSpline1.isPeriodic;
    if (bSpline1.isPeriodic != bSpline2.isPeriodic)
    {
        reportFeatureWarning(context, id, "Curves have different periodicity; tweened curve will adopt periodicity of the first curve.");
    }

    var newBSplineDef = bSplineCurve({
                "degree" : bSpline1.degree,
                "controlPoints" : tweenedCps,
                "isPeriodic" : isPeriodicTween,
                "isRational" : bSpline1.isRational,
                "weights" : tweenedWeights
            });

    opCreateBSplineCurve(context, id + "tweenedCpSpline", { "bSplineCurve" : newBSplineDef });
}

// Include all helper functions from tweenCurves.fs
// (These would normally be imported, but copying for now)

function getBSplineFromInput(context is Context, definition is map) returns map
{
    var bspline;
    const edgesQuery = getAllEdgesQuery(definition);
    const edges = evaluateQuery(context, edgesQuery);
    if (size(edges) > 1)
    {
        throw regenError("Multiple edges selected.", definition);
    }
    const edge = edges[0];
    const curveDef = evCurveDefinition(context, {
                "edge" : edge,
                "simplify" : true
            });
    if (curveDef is Line)
    {
        const edgeVertices = evaluateQuery(context, qAdjacent(edge, AdjacencyType.VERTEX, EntityType.VERTEX));
        bspline = bSplineCurve({
                    "degree" : 1,
                    "isPeriodic" : false,
                    "controlPoints" : [evVertexPoint(context, { "vertex" : edgeVertices[0] }), evVertexPoint(context, { "vertex" : edgeVertices[1] })]
                });
    }
    else if (curveDef is BSplineCurve)
    {
        bspline = curveDef;
    }
    else
    {
        bspline = evApproximateBSplineCurve(context, {
                    "edge" : edge
                });
    }
    
    if (!bspline.isRational)
    {
        bspline.weights = makeArray(size(bspline.controlPoints), 1);
        bspline.isRational = true;
    }
    return cleanUpPeriodicBSplineDefinition(bspline);
}

function cleanUpPeriodicBSplineDefinition(bspline is map) returns map
{
    if (!bspline.isPeriodic || bspline.knots[0] == 0 || size(bspline.controlPoints) + 2 * bspline.degree + 1 == size(bspline.knots))
    {
        return bspline;
    }
    const numOverlappingKnots = indexOf(bspline.knots, 0);
    if (numOverlappingKnots == 1)
    {
        bspline.knots[0] = 0;
        bspline.knots[size(bspline.knots) - 1] = 1;
        return bspline;
    }
    const lastIndex = size(bspline.controlPoints) - bspline.degree;
    bspline.controlPoints = subArray(bspline.controlPoints, 0, lastIndex);
    if (bspline.weights != undefined)
    {
        bspline.weights = subArray(bspline.weights, 0, lastIndex);
    }
    return bspline;
}

function getAllEdgesQuery(query is Query) returns Query
{
    return qUnion([qEntityFilter(query, EntityType.EDGE), qEntityFilter(query, EntityType.BODY)->qOwnedByBody(EntityType.EDGE)]);
}

function sumDistances(points1 is array, points2 is array) returns ValueWithUnits
{
    var total = 0 * meter;
    for (var i = 0; i < size(points1); i += 1)
    {
        total += norm(points1[i] - points2[i]);
    }
    return total;
}

function computeControlPointFractions(points is array) returns array
{
    if (size(points) == 0)
    {
        return [];
    }
    var fractions = makeArray(size(points));
    fractions[0] = 0 * meter;
    var total = 0 * meter;
    for (var i = 1; i < size(points); i += 1)
    {
        total += norm(points[i] - points[i - 1]);
        fractions[i] = total;
    }
    if (total == 0 * meter)
    {
        return makeArray(size(points), 0);
    }
    for (var i = 0; i < size(points); i += 1)
    {
        fractions[i] /= total;
    }
    return fractions;
}

function bestPeriodicShift(reference is array, candidate is array) returns number
{
    const n = size(reference);
    var bestShift = 0;
    var bestDistance = 1e30 * meter;
    for (var shift = 0; shift < n; shift += 1)
    {
        const rotated = rotateArray(candidate, -shift);
        const distance = sumDistances(reference, rotated);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestShift = shift;
        }
    }
    return bestShift;
}

function collinearPoints(p1 is Vector, p2 is Vector, p3 is Vector) returns boolean
{
    return parallelVectors(p2 - p1, p3 - p1);
}

function tweenCircleOrLine(context is Context, id is Id, edge1 is Query, edge2 is Query, fraction is number)
{
    const baseParams = [0, 0.5, 1];
    var params1 = baseParams;
    var params2 = baseParams;

    const info1 = evEdgeTangentLine(context, { "edge" : edge1, "parameter" : 0 });
    const info2 = evEdgeTangentLine(context, { "edge" : edge2, "parameter" : 0 });
    const dir1 = normalize(info1.direction);
    const dir2 = normalize(info2.direction);
    if (dot(dir1, dir2) < 0)
        params2 = reverse(params2);

    const def1 = evCurveDefinition(context, { "edge" : edge1, "returnBSplinesAsOther" : true });
    const def2 = evCurveDefinition(context, { "edge" : edge2, "returnBSplinesAsOther" : true });

    if (def1 is Circle && def2 is Circle)
    {
        params2 = alignCircleToCurve(context, edge2, edge1, params2);
    }
    else if (def1 is Circle && !(def2 is Circle))
    {
        params1 = alignCircleToCurve(context, edge1, edge2, params1);
    }
    else if (def2 is Circle && !(def1 is Circle))
    {
        params2 = alignCircleToCurve(context, edge2, edge1, params2);
    }

    const tangents1 = evEdgeTangentLines(context, { "edge" : edge1, "parameters" : params1 });
    const tangents2 = evEdgeTangentLines(context, { "edge" : edge2, "parameters" : params2 });

    var points = [];
    for (var i = 0; i < size(baseParams); i += 1)
    {
        const pos1 = tangents1[i].origin;
        const pos2 = tangents2[i].origin;
        points = append(points, pos1 * (1 - fraction) + pos2 * fraction);
    }

    if (collinearPoints(points[0], points[1], points[2]))
    {
        const lineDef = bSplineCurve({ "degree" : 1, "isPeriodic" : false, "controlPoints" : [points[0], points[2]] });
        opCreateBSplineCurve(context, id + "tweenedCpSpline", { "bSplineCurve" : lineDef });
        return;
    }

    if (squaredNorm(points[0] - points[2]) < 1e-8 * meter * meter)
    {
        const center = def1.coordSystem.origin * (1 - fraction) + def2.coordSystem.origin * fraction;
        const radius = def1.radius * (1 - fraction) + def2.radius * fraction;
        const normal = normalize(def1.coordSystem.zAxis * (1 - fraction) + def2.coordSystem.zAxis * fraction);
        opCircle3d(context, id + "tweenedCpSpline", { "center" : center, "normal" : normal, "radius" : radius });
    }
    else
    {
        opArc3d(context, id + "tweenedCpSpline", { "start" : points[0], "mid" : points[1], "end" : points[2] });
    }
}

function alignCircleToCurve(context is Context, circleEdge is Query, otherEdge is Query, baseParams is array) returns array
{
    const sampleCount = 8;
    var sampleParams = [];
    for (var i = 0; i < sampleCount; i += 1)
        sampleParams = append(sampleParams, i / sampleCount);

    const circlePts = mapArray(evEdgeTangentLines(context, { "edge" : circleEdge, "parameters" : sampleParams }),
        function(line) { return line.origin; });
    const otherPts = mapArray(evEdgeTangentLines(context, { "edge" : otherEdge, "parameters" : sampleParams }),
        function(line) { return line.origin; });

    const normalShift = bestPeriodicShift(circlePts, otherPts);
    const normalRot = rotateArray(circlePts, -normalShift);
    const reversed = reverse(circlePts);
    const revShift = bestPeriodicShift(reversed, otherPts);
    const revRot = rotateArray(reversed, -revShift);
    const distNorm = sumDistances(normalRot, otherPts);
    const distRev = sumDistances(revRot, otherPts);

    if (distRev < distNorm)
        return rotateArray(reverse(baseParams), -revShift);
    return rotateArray(baseParams, -normalShift);
}

// Stub functions for degree elevation and CP matching
// These need full implementation from tweenCurves.fs

function elevateDegree(bspline is map, newDegree is number) returns map
{
    // Simplified - would need full Bezier decomposition logic
    return bspline;
}

function matchCPCount(context is Context, bspline is map, targetCount is number, refFractions is array) returns map
{
    if (size(bspline.controlPoints) >= targetCount)
    {
        return bspline;
    }
    
    const startParam = bspline.knots[bspline.degree];
    const endParam = bspline.knots[size(bspline.knots) - bspline.degree - 1];
    var params = [];
    for (var i = 0; i < targetCount; i += 1)
    {
        var fraction = i / (targetCount - 1);
        if (refFractions != undefined && size(refFractions) == targetCount)
        {
            fraction = refFractions[i];
        }
        params = append(params, startParam + (endParam - startParam) * fraction);
    }
    const positions = evaluateSpline({ "spline" : bspline, "parameters" : params })[0];
    const target = approximationTarget({ 'positions' : positions });
    var refined = approximateSpline(context, {
                "degree" : bspline.degree,
                "tolerance" : 1e-8 * meter,
                "isPeriodic" : bspline.isPeriodic,
                "targets" : [target],
                "parameters" : params,
                "maxControlPoints" : targetCount
            })[0];
    if (size(refined.controlPoints) != targetCount)
    {
        refined = bSplineCurve({
                    "degree" : bspline.degree,
                    "isPeriodic" : bspline.isPeriodic,
                    "isRational" : bspline.isRational,
                    "controlPoints" : positions,
                    "weights" : bspline.isRational ? makeArray(targetCount, 1) : undefined
                });
    }
    else if (bspline.isRational && !refined.isRational)
    {
        refined.isRational = true;
        refined.weights = makeArray(size(refined.controlPoints), 1);
    }
    return refined;
}

// Placeholder multi-curve functions

function generateNearestDistanceBreakPoints(context is Context, path1 is Path, path2 is Path,
                                             edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Simplified: just return start and end for now
    return [
        {
            "segment1" : { "edge" : path1.edges[0], "parameter" : 0.0 },
            "segment2" : { "edge" : path2.edges[0], "parameter" : 0.0 }
        },
        {
            "segment1" : { "edge" : path1.edges[size(path1.edges) - 1], "parameter" : 1.0 },
            "segment2" : { "edge" : path2.edges[size(path2.edges) - 1], "parameter" : 1.0 }
        }
    ];
}

function generatePathLengthBreakPoints(context is Context, path1 is Path, path2 is Path,
                                        edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Simplified: just return start and end for now
    return [
        {
            "segment1" : { "edge" : path1.edges[0], "parameter" : 0.0 },
            "segment2" : { "edge" : path2.edges[0], "parameter" : 0.0 }
        },
        {
            "segment1" : { "edge" : path1.edges[size(path1.edges) - 1], "parameter" : 1.0 },
            "segment2" : { "edge" : path2.edges[size(path2.edges) - 1], "parameter" : 1.0 }
        }
    ];
}
