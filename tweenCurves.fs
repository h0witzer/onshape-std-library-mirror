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
//import(path : "onshape/std/editCurve.fs", version : "2656.0");


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
        if (evaluateQueryCount(context, definition.curve1) == 0) throw regenError("Select first curve.", ["curve1"]);
        if (evaluateQueryCount(context, definition.curve2) == 0) throw regenError("Select second curve.", ["curve2"]);


        var bSpline1 = getBSplineFromInput(context, definition.curve1);
        var bSpline2 = getBSplineFromInput(context, definition.curve2);

        if (bSpline1 == undefined || bSpline2 == undefined)
            throw regenError("Could not get B-spline representation for input curves.");

        var cpList1 = bSpline1.controlPoints;
        var cpList2_orig = bSpline2.controlPoints;
        var weights1 = bSpline1.weights; // undefined if not rational
        var weights2_orig = bSpline2.weights; // undefined if not rational

        var autoDecidedFlip = false;
        try 
        {
            if (size(cpList1) > 1 && size(cpList2_orig) > 1) {
                var vecDir1 = cpList1[size(cpList1) - 1] - cpList1[0];
                var vecDir2 = cpList2_orig[size(cpList2_orig) - 1] - cpList2_orig[0];
                var dir1 = normalize(vecDir1);
                var dir2 = normalize(vecDir2);
                if (dot(dir1, dir2) < 0) autoDecidedFlip = true;
            }
        } catch { /* autoDecidedFlip remains false */ }

        var finalCpList2 = autoDecidedFlip ? reverse(cpList2_orig) : cpList2_orig;
        var finalWeights2 = bSpline2.isRational && autoDecidedFlip ? reverse(weights2_orig) : weights2_orig;

        // === COMPATIBILITY CHECK ===
        // Degree Check (Cannot elevate with current stdlib utilities for general B-splines)
        if (bSpline1.degree != bSpline2.degree) {
            throw regenError("Curves have different B-spline degrees (" ~ bSpline1.degree ~ " vs " ~ bSpline2.degree ~ "). Degree matching is not auto-implemented. Please use curves of same degree or enable point sampling fallback.", ["curve1", "curve2"]);
        }
        // CP Count Check (Cannot "elevate" CP count with current stdlib utilities)
        if (size(cpList1) != size(finalCpList2)) {
            throw regenError("Curves have different B-spline control point counts (" ~ size(cpList1) ~ " vs " ~ size(finalCpList2) ~ "). CP count matching is not auto-implemented. Please use curves with same CP count or enable point sampling fallback.", ["curve1", "curve2"]);
        }
        // Rationality Check
        if (bSpline1.isRational != bSpline2.isRational) {
            // Ideally, make both rational if one is. For now, error if different.
            throw regenError("Curves have different rationality. Both must be rational or non-rational.", ["curve1", "curve2"]);
        }

        // --- If compatible, proceed with Control Point Tweening ---
        var tweenedCps = [];
        var tweenedWeights = bSpline1.isRational ? [] : undefined;
        const fraction = definition.fraction;

        for (var i = 0; i < size(cpList1); i += 1) {
            tweenedCps = append(tweenedCps, cpList1[i] * (1 - fraction) + finalCpList2[i] * fraction);
            if (bSpline1.isRational) {
                tweenedWeights = append(tweenedWeights, weights1[i] * (1 - fraction) + finalWeights2[i] * fraction);
            }
        }
        
        // Assume periodicity matches if degrees and CP counts do; otherwise, could be complex.
        // Simplification: take periodicity from first curve, or ensure both match.
        var isPeriodicTween = bSpline1.isPeriodic;
        if (bSpline1.isPeriodic != bSpline2.isPeriodic) {
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

function inputCanBeModified(context is Context, wire is Query) returns boolean
{
    const allBodiesQuery = qOwnerBody(wire);
    const allBodiesEdgesQuery = qOwnedByBody(allBodiesQuery, EntityType.EDGE);
    const edgesQuery = qEntityFilter(wire, EntityType.EDGE);
    const bodiesQuery = qEntityFilter(wire, EntityType.BODY);
    const bodiesEdgesQuery = qOwnedByBody(bodiesQuery, EntityType.EDGE);
    const allEdgesQuery = qUnion([edgesQuery, bodiesEdgesQuery]);
    const nonModifiableSelections = qSubtraction(wire, qModifiableEntityFilter(wire));

    // We can only use the raw input if:
    // - all the inputs come from a single body,
    // - all the edges of said body have been selected,
    // - the body is a wire,
    // - the body is not a sketch body.
    // - all the selections are not from in-context entities

    const singleBody = size(evaluateQuery(context, allBodiesQuery)) == 1;
    const allEdgesSelected = size(evaluateQuery(context, allBodiesEdgesQuery)) == size(evaluateQuery(context, allEdgesQuery));
    const isWireBody = !isQueryEmpty(context, qBodyType(allBodiesQuery, BodyType.WIRE));
    const isNotSketchBody = isQueryEmpty(context, qSketchFilter(allBodiesQuery, SketchObject.YES));
    const allEdgesNotInContext = isQueryEmpty(context, nonModifiableSelections);

    return singleBody && allEdgesSelected && isWireBody && isNotSketchBody && allEdgesNotInContext;
}

function getQueryToReplace(context is Context, id is Id, definition is map) returns Query
{
    if (inputCanBeModified(context, definition))
    {
        return definition;
    }

    opExtractWires(context, id + "opExtractWires", {
                "edges" : getAllEdgesQuery(definition)
            });

    return qCreatedBy(id + "opExtractWires", EntityType.BODY);
}

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
    // if (definition.approximate)
    // {
    //     var path;
    //     try silent
    //     {
    //         path = constructPath(context, edgesQuery, { "tolerance" : 1e-5 * meter }).path;
    //     }
    //     catch (error)
    //     {
    //         throw regenError(error, ["wire"], definition);
    //     }

    //     checkApproximationParameters(definition, path);

    //     const approximationTarget = makeApproximationTarget(context, path, definition.keepStartDerivative, definition.keepEndDerivative);

    //     bspline = approximateSpline(context, {
    //                     "degree" : definition.approximationDegree,
    //                     "tolerance" : definition.approximationTolerance,
    //                     "isPeriodic" : path.closed,
    //                     "targets" : [approximationTarget],
    //                     "maxControlPoints" : definition.approximationMaxCPs
    //                 })[0];
    // }
    // else
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

function elevate(context is Context, id is Id, bspline is map, targetDegree is number) returns map
{
    if (bspline.degree >= targetDegree)
    {
        reportFeatureInfo(context, id, "Curve degree is already equal or above elevation target degree.");
    }
    else
    {
        bspline = elevateDegree(bspline, targetDegree);
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





//==================================================================
//=========================== Utilities ============================
//==================================================================

// Returns {} if there's an issue, otherwise the bspline
function computeBSplineBeforeEdit(context is Context, definition is map) returns map
{
    if (isQueryEmpty(context, definition))
    {
        return {};
    }
    // If getBSplineFromInput throws, we it means that either we need to reapproximate the curve,
    // or the approximation parameters are wrong.
    // In both cases, it's fine to just set the new weight to 1.
    var bspline;
    try
    {
        bspline = getBSplineFromInput(context, definition);
    }
    catch
    {
        return {};
    }
    if (definition.elevate && bspline.degree < definition.elevationDegree)
    {
        bspline = elevateDegree(bspline, definition.elevationDegree);
    }
    return bspline;
}

function isBezier(points is array, curveDegree is number, knots is array) returns boolean
{
    return size(points) == curveDegree + 1 && knots[0] == 0;
}

function computeSpans(bspline is map) returns number
{
    var spans = 1;
    for (var i = bspline.degree + 1; i < size(bspline.knots) - (bspline.degree + 1); i += 1)
    {
        if (bspline.knots[i] != bspline.knots[i - 1])
        {
            spans += 1;
        }
    }
    return spans;
}

function showPolyline(context is Context, bspline is map)
{
    for (var i = 0; i < size(bspline.controlPoints) - 1; i += 1)
    {
        if (tolerantEquals(bspline.controlPoints[i], bspline.controlPoints[i + 1]))
        {
            continue;
        }
        addDebugLine(context, bspline.controlPoints[i], bspline.controlPoints[i + 1], DebugColor.MAGENTA);
    }
    if (bspline.isPeriodic && !firstAndLastCPShouldOverlap(bspline))
    {
        addDebugLine(context, bspline.controlPoints[size(bspline.controlPoints) - 1], bspline.controlPoints[0], DebugColor.MAGENTA);
    }
}

function updateCurveData(context is Context, id is Id, bspline is map)
{
    const spans = computeSpans(bspline);
    const numCP = size(bspline.controlPoints);

    setFeatureComputedParameter(context, id, { "name" : "curveDegree", "value" : bspline.degree });
    setFeatureComputedParameter(context, id, { "name" : "curveNumCPs", "value" : numCP });
    setFeatureComputedParameter(context, id, { "name" : "curveNumSpans", "value" : spans });
}

function getAllEdgesQuery(query is Query) returns Query
{
    return qUnion([qEntityFilter(query, EntityType.EDGE), qEntityFilter(query, EntityType.BODY)->qOwnedByBody(EntityType.EDGE)]);
}

function firstAndLastCPShouldOverlap(bspline is map) returns boolean
{
    return bspline.isPeriodic && bspline.knots[0] == 0;
}
