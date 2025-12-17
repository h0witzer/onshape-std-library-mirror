FeatureScript 2679;
// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/geomOperations.fs", version : "2679.0");
import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/units.fs", version : "2679.0");
import(path : "onshape/std/valueBounds.fs", version : "2679.0");
import(path : "onshape/std/curveGeometry.fs", version : "2679.0"); // For bSplineCurve() constructor and BSplineCurve type
import(path : "onshape/std/error.fs", version : "2679.0"); // For reportFeatureWarning
import(path : "onshape/std/approximationUtils.fs", version : "2679.0");
import(path : "onshape/std/splineUtils.fs", version : "2679.0");
import(path : "onshape/std/containers.fs", version : "2679.0");
import(path : "f42f46716945f2a9bda5a481/eabbc18661ba5776e0ba962d/97730412fb61f53dcd526c08", version : "a24da502290d2ae4706c631f"); // 3d Arc Utilities


export const TWEEN_FRACTION_BOUNDS = { (unitless) : [0, 0.5, 1] } as RealBoundSpec;


/**
 * Create a curve that is the tween between two input curves.
 *
 * All compatibility checks and conversions are handled internally.
 */
export function tweenCurves(context is Context, id is Id,
        curve1 is Query, curve2 is Query, fraction is number)
{
    if (evaluateQueryCount(context, curve1) == 0)
        throw regenError("Select first curve.", ["curve1"]);
    if (evaluateQueryCount(context, curve2) == 0)
        throw regenError("Select second curve.", ["curve2"]);

    const edge1 = evaluateQuery(context, curve1)[0];
    const edge2 = evaluateQuery(context, curve2)[0];

    const def1 = evCurveDefinition(context, { "edge" : edge1, "returnBSplinesAsOther" : true });
    const def2 = evCurveDefinition(context, { "edge" : edge2, "returnBSplinesAsOther" : true });

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

    // === DEGREE MATCHING ===
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

    // === CONTROL POINT COUNT MATCHING ===
    if (size(bSpline1.controlPoints) != size(bSpline2.controlPoints))
    {
        const targetCount = max(size(bSpline1.controlPoints), size(bSpline2.controlPoints));
        const cpFractions1 = computeControlPointFractions(bSpline1.controlPoints);
        const cpFractions2 = computeControlPointFractions(bSpline2.controlPoints);

        if (size(bSpline1.controlPoints) < targetCount)
        {
            bSpline1 = matchCPCount(context, bSpline1, targetCount,
                    cpFractions2);
        }
        if (size(bSpline2.controlPoints) < targetCount)
        {
            bSpline2 = matchCPCount(context, bSpline2, targetCount,
                    cpFractions1);
        }
    }

    var cpList1 = bSpline1.controlPoints;
    var cpList2_orig = bSpline2.controlPoints;
    var weights1 = bSpline1.weights; // undefined if not rational
    var weights2_orig = bSpline2.weights; // undefined if not rational

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
        { /* autoDecidedFlip remains false */
        }
    }

    var finalCpList2 = autoDecidedFlip ? reverse(cpList2_orig) : cpList2_orig;
    var finalWeights2 = bSpline2.isRational && autoDecidedFlip ? reverse(weights2_orig) : weights2_orig;

    // === COMPATIBILITY CHECK ===
    if (bSpline1.degree != bSpline2.degree)
    {
        throw regenError("Failed to match curve degrees after elevation.", ["curve1", "curve2"]);
    }
    if (size(cpList1) != size(finalCpList2))
    {
        throw regenError("Curves have different B-spline control point counts (" ~ size(cpList1) ~ " vs " ~ size(finalCpList2) ~ "). CP count matching is not auto-implemented. Please use curves with same CP count or enable point sampling fallback.", ["curve1", "curve2"]);
    }
    if (bSpline1.isRational != bSpline2.isRational)
    {
        throw regenError("Curves have different rationality. Both must be rational or non-rational.", ["curve1", "curve2"]);
    }

    var tweenedCps = [];
    var tweenedWeights = [];

    for (var i = 0; i < size(cpList1); i += 1)
    {
        const weight1 = weights1[i];
        const weight2 = finalWeights2[i];
        const blendedWeight = weight1 * (1 - fraction) + weight2 * fraction;

        const pos1 = cpList1[i];
        const pos2 = finalCpList2[i];
        
        // For rational B-splines (NURBS), interpolate in homogeneous coordinates
        // Weighted CP = CP * weight, then interpolate, then divide by interpolated weight
        const weightedPos1 = pos1 * weight1;
        const weightedPos2 = pos2 * weight2;
        const blendedWeightedPos = weightedPos1 * (1 - fraction) + weightedPos2 * fraction;

        tweenedCps = append(tweenedCps, blendedWeightedPos / blendedWeight);
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

//==================================================================
//=================== Stealing from editCurve ======================
//==================================================================

//==================================================================
//======================== Input Processing ========================
//==================================================================

function checkBSpline(bspline is BSplineCurve, wire is Query)
{
    if (bspline.degree > MAX_DEGREE)
    {
        throw regenError(ErrorStringEnum.EDIT_CURVE_DEGREE_TOO_HIGH, ["wire"], wire);
    }
    if (size(bspline.controlPoints) > MAX_CONTROL_POINTS)
    {
        throw regenError(ErrorStringEnum.EDIT_CURVE_TOO_MANY_CONTROL_POINTS, ["wire"], wire);
    }
}

function getBSplineFromInput(context is Context, definition is map) returns map
{
    var bspline;
    const edgesQuery = getAllEdgesQuery(definition);
    {
        const edges = evaluateQuery(context, edgesQuery);
        if (size(edges) > 1)
        {
            throw regenError(ErrorStringEnum.EDIT_CURVE_MULTIPLE_EDGES, ["wire"], definition);
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
            checkBSpline(bspline, definition);
        }
        else
        {
            bspline = evApproximateBSplineCurve(context, {
                        "edge" : edge
                    });
            if (isAtVersionOrLater(context, FeatureScriptVersionNumber.V2554_EDIT_CURVE_CHECK_BSPLINE_APPROXIMATION))
            {
                checkBSpline(bspline, definition);
            }
        }
    }
    // Since weights can be modified, it's either to make every curve rational and default the weights to all 1s.
    if (!bspline.isRational)
    {
        bspline.weights = makeArray(size(bspline.controlPoints), 1);
        bspline.isRational = true;
    }
    return cleanUpPeriodicBSplineDefinition(bspline);
}

// There are a few ways that periodic NURBS are handled, the can have no overlaps, overlapping knots or onverlapping knots and control points.
// For our purposes, if a curve has overlapping knots and overlapping control points, we remove the overlapping control points.
function cleanUpPeriodicBSplineDefinition(bspline is map) returns map
{
    if (!bspline.isPeriodic || bspline.knots[0] == 0 || size(bspline.controlPoints) + 2 * bspline.degree + 1 == size(bspline.knots))
    {
        return bspline;
    }
    const numOverlappingKnots = indexOf(bspline.knots, 0);
    // In certain cases, we get periodic curves with only the first control point overlapping and a single knot overlap.
    // For our bspline creation code this is the same as having no overlapping knots so we clamp the knot vector to [0;1] to avoid issues with elevation.
    if (numOverlappingKnots == 1)
    {
        bspline.knots[0] = 0;
        bspline.knots[size(bspline.knots) - 1] = 1;
        return bspline;
    }
    // If we're here, we have repeated knots AND repeated control points. We only want the knots.
    const lastIndex = size(bspline.controlPoints) - bspline.degree;
    bspline.controlPoints = subArray(bspline.controlPoints, 0, lastIndex);
    if (bspline.weights != undefined)
    {
        bspline.weights = subArray(bspline.weights, 0, lastIndex);
    }
    return bspline;
}

//==================================================================
//=========================== Elevation ============================
//==================================================================

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

// Refine a B-spline so that it has exactly `targetCount` control points.
// Uses sampling and spline approximation to preserve the original shape.
function matchCPCount(context is Context, bspline is map, targetCount is number, refFractions is array) returns map
{
    if (size(bspline.controlPoints) >= targetCount)
    {
        // If the curve already has the desired number of points or more,
        // leave it unchanged.
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


//==================================================================
//=========================== Utilities ============================
//==================================================================

function isBezier(points is array, curveDegree is number, knots is array) returns boolean
{
    return size(points) == curveDegree + 1 && knots[0] == 0;
}


function getAllEdgesQuery(query is Query) returns Query
{
    return qUnion([qEntityFilter(query, EntityType.EDGE), qEntityFilter(query, EntityType.BODY)->qOwnedByBody(EntityType.EDGE)]);
}

// Compute the sum of point distances between two control point arrays
function sumDistances(points1 is array, points2 is array) returns ValueWithUnits
{
    var total = 0 * meter;
    for (var i = 0; i < size(points1); i += 1)
    {
        total += norm(points1[i] - points2[i]);
    }
    return total;
}

// Compute cumulative fractions along a control point list
function computeControlPointFractions(points is array) returns array
{
    if (size(points) == 0)
    {
        return [];
    }
    var fractions = makeArray(size(points));
    // Use a distance value so that divisions by the total length
    // later yield dimensionless fractions.
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


// Find the rotation of `candidate` that best matches `reference`
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

function alignCircleToCurve(context is Context, circleEdge is Query, otherEdge is Query, baseParams is array) returns array
{
    const sampleCount = 8;
    var sampleParams = [];
    for (var i = 0; i < sampleCount; i += 1)
        sampleParams = append(sampleParams, i / sampleCount);

    const circlePts = mapArray(evEdgeTangentLines(context, { "edge" : circleEdge, "parameters" : sampleParams }),
        line
        =>line.origin);
    const otherPts = mapArray(evEdgeTangentLines(context, { "edge" : otherEdge, "parameters" : sampleParams }),
        line
        =>line.origin);

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
