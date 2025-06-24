FeatureScript 2656;
// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2656.0");
import(path : "onshape/std/evaluate.fs", version : "2656.0");
import(path : "onshape/std/geomOperations.fs", version : "2656.0");
import(path : "onshape/std/query.fs", version : "2656.0");
import(path : "onshape/std/vector.fs", version : "2656.0");
import(path : "onshape/std/units.fs", version : "2656.0");
import(path : "onshape/std/valueBounds.fs", version : "2656.0");
import(path : "onshape/std/curveGeometry.fs", version : "2656.0"); // For bSplineCurve() constructor and BSplineCurve type
import(path : "onshape/std/error.fs", version : "2656.0"); // For reportFeatureWarning
import(path : "onshape/std/approximationUtils.fs", version : "2656.0");
import(path : "onshape/std/splineUtils.fs", version : "2656.0");
import(path : "onshape/std/containers.fs", version : "2656.0");


const TWEEN_FRACTION_BOUNDS = { (unitless) : [0, 0.5, 1] } as RealBoundSpec;

annotation { "Feature Type Name" : "Tween Two Curves (CP Interpolation)",
        "Feature Type Description" : "Interpolates B-spline control points. Curves must be compatible or convertible to compatible B-splines (same degree & CP count).",
        "UIHint" : "NO_PREVIEW_PROVIDED" }
export const tweenTwoCurves = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "First curve", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.curve1 is Query;
        annotation { "Name" : "Second curve", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.curve2 is Query;
        annotation { "Name" : "Tween fraction" }
        isReal(definition.fraction, TWEEN_FRACTION_BOUNDS);
        // definition.numSamples can be removed if no fallback
    }
    {
        if (evaluateQueryCount(context, definition.curve1) == 0)
            throw regenError("Select first curve.", ["curve1"]);
        if (evaluateQueryCount(context, definition.curve2) == 0)
            throw regenError("Select second curve.", ["curve2"]);


        var bSpline1 = getBSplineFromInput(context, definition.curve1);
        var bSpline2 = getBSplineFromInput(context, definition.curve2);

        if (bSpline1 == undefined || bSpline2 == undefined)
            throw regenError("Could not get B-spline representation for input curves.");

        // === DEGREE MATCHING ===
        // Elevate the lower degree curve so both have the same degree
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
            if (size(bSpline1.controlPoints) < targetCount)
            {
                bSpline1 = matchCPCount(context, bSpline1, targetCount);
            }
            if (size(bSpline2.controlPoints) < targetCount)
            {
                bSpline2 = matchCPCount(context, bSpline2, targetCount);
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
        // Sanity check: degrees should match after elevation step
        if (bSpline1.degree != bSpline2.degree)
        {
            throw regenError("Failed to match curve degrees after elevation.", ["curve1", "curve2"]);
        }
        // CP Count Check (Cannot "elevate" CP count with current stdlib utilities)
        if (size(cpList1) != size(finalCpList2))
        {
            throw regenError("Curves have different B-spline control point counts (" ~ size(cpList1) ~ " vs " ~ size(finalCpList2) ~ "). CP count matching is not auto-implemented. Please use curves with same CP count or enable point sampling fallback.", ["curve1", "curve2"]);
        }
        // Rationality Check
        if (bSpline1.isRational != bSpline2.isRational)
        {
            // Ideally, make both rational if one is. For now, error if different.
            throw regenError("Curves have different rationality. Both must be rational or non-rational.", ["curve1", "curve2"]);
        }

        // --- If compatible, proceed with Control Point Tweening ---
        var tweenedCps = [];
        var tweenedWeights = [];
        const fraction = definition.fraction;

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

        // Assume periodicity matches if degrees and CP counts do; otherwise, could be complex.
        // Simplification: take periodicity from first curve, or ensure both match.
        var isPeriodicTween = bSpline1.isPeriodic;
        if (bSpline1.isPeriodic != bSpline2.isPeriodic)
        {
            reportFeatureWarning(context, id, "Curves have different periodicity; tweened curve will adopt periodicity of the first curve.");
            // Or could throw an error if strict matching is required for periodicity too.
        }


        var newBSplineDef = bSplineCurve({
                "degree" : bSpline1.degree,
                "controlPoints" : tweenedCps,
                "isPeriodic" : isPeriodicTween,
                "isRational" : bSpline1.isRational,
                "weights" : tweenedWeights
                // Knots will be defaulted by bSplineCurve()
            });
        opCreateBSplineCurve(context, id + "tweenedCpSpline", { "bSplineCurve" : newBSplineDef });
    });


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
function matchCPCount(context is Context, bspline is map, targetCount is number) returns map
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
        params = append(params, startParam + (endParam - startParam) * i / (targetCount - 1));
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

    if (bspline.isRational && !refined.isRational)
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
    var total = 0*meter;
    for (var i = 0; i < size(points1); i += 1)
    {
        total += norm(points1[i] - points2[i]);
    }
    return total;
}

// Find the rotation of `candidate` that best matches `reference`
function bestPeriodicShift(reference is array, candidate is array) returns number
{
    const n = size(reference);
    var bestShift = 0;
    var bestDistance = 1e30*meter;
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
