FeatureScript 2559;
import(path : "onshape/std/common.fs", version : "2559.0");
import(path : "onshape/std/geometry.fs", version : "2559.0");

export enum PointLocation
{
    annotation { "Name" : "On curve" }
    LOCATION_ON_CRV,
}

export enum SpacingOnCurve
{
    annotation { "Name" : "Equispaced" }
    CURVE_EQUI,
    annotation { "Name" : "Equispaced by distance" }
    CURVE_EQUI_PITCH,
    annotation { "Name" : "Length" }
    CURVE_CUSTOM_LENGTH,
    annotation { "Name" : "Length %" }
    CURVE_CUSTOM_PERCENT,
    annotation { "Name" : "Random" }
    CURVE_RANDOM,
    annotation { "Name" : "Reference point" }
    CURVE_REFERENCE,
}

export enum SpacingOnSurf
{
    annotation { "Name" : "UV points" }
    SURF_UV_POINTS,
    annotation { "Name" : "Equispaced" }
    SURF_EQUI,
    annotation { "Name" : "Choose random" }
    SURF_RANDOM,
    annotation { "Name" : "Reference point" }
    SURF_REFERENCE,
}

const CP_POINTS_MANIPULATOR = "pointManipulator";
const CP_TOGGLE_POINTS_MANIPULATOR = "togglePointsManipulator";
const CP_DIRECTION_MANIPULATOR = "directionManipulator";

export predicate internalCTpointsPredicate(definition is map)
{
    annotation { "Name" : "Point Location", "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.NO_PREVIEW_PROVIDED] }
    definition.pointLocation is PointLocation;

    if (definition.pointLocation == PointLocation.LOCATION_ON_CRV)
    {
        annotation { "Name" : "Edges", "Filter" : EntityType.EDGE && AllowFlattenedGeometry.YES, "Description" : "Tangent continuous edges" , "UIHint" : UIHint.SHOW_CREATE_SELECTION}
        definition.edge is Query;

        annotation { "Name" : "Point distribution", "UIHint" : [UIHint.SHOW_LABEL] }
        definition.curveSpacing is SpacingOnCurve;

        // *** New: Define closed-curve options for all spacing types ***
        annotation { "Name" : "Closed curve", "Default" : false, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.closedCurve is boolean;

        annotation { "Name" : "Flip curve direction", "Default" : false, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.flipCurveDirection is boolean;

        annotation { "Name" : "Pick start point", "Default" : false, "Description": "If selected, choose a start point for closed loops" }
        definition.pickStartPoint is boolean;

        if (definition.pickStartPoint)
        {
            annotation { "Name" : "Select start", "Filter" : EntityType.VERTEX || BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
            definition.closedEdgeStartPoint is Query;
        }
        // ******************************************************

        if (definition.curveSpacing == SpacingOnCurve.CURVE_REFERENCE)
        {
            annotation { "Name" : "Reference point", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
            definition.referencePoint is Query;
        }
        else if (definition.curveSpacing == SpacingOnCurve.CURVE_EQUI)
        {
            annotation { "Name" : "Number of points" }
            isInteger(definition.numEquiCrv, { (unitless) : [2, 5, 1000] } as IntegerBoundSpec);
        }
        else if (definition.curveSpacing == SpacingOnCurve.CURVE_EQUI_PITCH)
        {
            annotation {
                        "Name" : "Points spacing",
                        "Description" : "Average (approximate) distance between adjacent points"
                    }
            isLength(definition.curvePitch, { (millimeter) : [0, 5, 1000] } as LengthBoundSpec);
        }
        else if (definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_LENGTH ||
                 definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_PERCENT)
        {
            // (Note: closed-curve options already defined above.)
            if (definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_LENGTH)
            {
                annotation { "Name" : "Curve lengths",
                            "Item name" : "point",
                            "Item label template" : "Curve length" }
                definition.curveLengths is array;

                for (var curveLength in definition.curveLengths)
                {
                    annotation { "Name" : "Length", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                    isLength(curveLength.length, LENGTH_BOUNDS);
                }
            }
            else
            {
                annotation { "Name" : "Curve lengths in %",
                            "Item name" : "point",
                            "Item label template" : "Length %" }
                definition.curveLengthsInPercent is array;

                for (var curveLengthInPercent in definition.curveLengthsInPercent)
                {
                    annotation { "Name" : "Length %", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                    isReal(curveLengthInPercent.percent, { (unitless) : [0, 10, 100] } as RealBoundSpec);
                }
            }
        }
        else if (definition.curveSpacing == SpacingOnCurve.CURVE_RANDOM)
        {
            /* handling it down below */
        }

        annotation {
                    "Name" : "Start offset",
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE,
                    "Default" : "0 mm"
                }
        isLength(definition.startOffset, { (meter) : [0, 0, 1] } as LengthBoundSpec);

        annotation {
                    "Name" : "End offset",
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE,
                    "Default" : "0 mm"
                }
        isLength(definition.endOffset, { (meter) : [0, 0, 1] } as LengthBoundSpec);
    }

    if (definition.pointLocation == PointLocation.LOCATION_ON_CRV &&
        definition.curveSpacing == SpacingOnCurve.CURVE_RANDOM)
    {
        annotation { "Name" : "Multiple points", "Default" : false, "Description" : "Allow for selection of multiple points", "UIHint" : UIHint.DISPLAY_SHORT }
        definition.multipleRndPoints is boolean;

        if (definition.multipleRndPoints)
        {
            annotation { "Name" : "Random point indices",
                        "Item name" : "index",
                        "UIHint" : UIHint.ALWAYS_HIDDEN }
            definition.randomPointIndices is array;

            for (var randomPointIndex in definition.randomPointIndices)
            {
                annotation { "Name" : "index", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isInteger(randomPointIndex.index, { (unitless) : [0, 1, 10000000] } as IntegerBoundSpec);
            }
        }
        else
        {
            annotation { "Name" : "Random curve point index", "UIHint" : UIHint.ALWAYS_HIDDEN }
            isInteger(definition.randomSinglePointIdx, { (unitless) : [-1, -1, 10000000] } as IntegerBoundSpec);
        }

        annotation { "Name" : "Show preview", "Default" : false, "Description" : "Displays preview but may take time", "UIHint" : UIHint.DISPLAY_SHORT }
        definition.showPreview is boolean;

        { // for curve
            annotation { "Name" : "Selection pool", "Default" : false }
            definition.selectionPool is boolean;

            if (definition.selectionPool)
            {
                annotation { "Name" : "Num points", "Description" : "Number of points in selection pool" }
                isInteger(definition.numCrvPoolSize, { (unitless) : [1, 200, 10000] } as IntegerBoundSpec);
            }
        }
    }
}

//
// Helper function to adjust the parameter array if a start point is picked
//
function adjustParameterArrayForStartPoint(context is Context, definition is map, parameterArray is array) returns array
{
    if (definition.closedCurve && definition.pickStartPoint && !isQueryEmpty(context, definition.closedEdgeStartPoint))
    {
         var distResult = evDistance(context, {
             "side0" : definition.edge,
             "side1" : definition.closedEdgeStartPoint,
             "arcLengthParameterization" : true
         });
         // Use sides[1].parameter if that’s the correct one for the edge.
         var startPointParam = distResult.sides[1].parameter;
         return mapArray(parameterArray, function(param) { 
             return mapCurveParameter(param, startPointParam, definition.flipCurveDirection); 
         });
    }
    return parameterArray;
}


export function doCreatePoints(context is Context, id is Id, definition is map) returns array
{
    var outPoints = [];

    if (definition.pointLocation == PointLocation.LOCATION_ON_CRV)
    {
        if (isQueryEmpty(context, definition.edge))
        {
            return; // nothing to do, wait for input
        }
        var massProperties = evApproximateMassProperties(context, { "entities" : definition.edge, "density" : 1 * kilogram / meter });

        if (size(evaluateQuery(context, definition.edge)) > 1)
        {
            /* handle multiple edges */
            outPoints = pointsOnPath(context, id, definition);
        }
        else
        {
            var parameterArray = [];

            if (definition.curveSpacing == SpacingOnCurve.CURVE_REFERENCE)
            {
                if (!isQueryEmpty(context, definition.referencePoint))
                { // if a reference is selected
                    var referenceParam = evDistance(context, {
                                "side0" : definition.referencePoint,
                                "side1" : definition.edge
                            }).sides[1].parameter;
                    parameterArray = append(parameterArray, referenceParam);
                }
            }
            else if (definition.curveSpacing == SpacingOnCurve.CURVE_EQUI)
            {
                var totalLength = massProperties.length;
                var effectiveLength = totalLength - definition.startOffset - definition.endOffset;

                if (effectiveLength <= 0 * meter)
                {
                    parameterArray = [];
                    reportFeatureWarning(context, id, "Effective length ≤ 0");
                }
                else
                {
                    var numPoints = definition.numEquiCrv;
                    var startParam = definition.startOffset / totalLength;
                    var endParam = (totalLength - definition.endOffset) / totalLength;

                    if (definition.closedCurve)
                    {
                        var paramStep = (endParam - startParam) / numPoints;
                        for (var i = 0; i < numPoints; i += 1)
                        {
                            parameterArray = append(parameterArray, startParam + i * paramStep);
                        }
                    }
                    else
                    {
                        var numIntervals = numPoints - 1;
                        var paramStep = (endParam - startParam) / numIntervals;
                        for (var i = 0; i < numPoints; i += 1)
                        {
                            parameterArray = append(parameterArray, startParam + i * paramStep);
                        }
                    }
                }
                // *** Adjust the computed parameters using the picked start point (if any) ***
                parameterArray = adjustParameterArrayForStartPoint(context, definition, parameterArray);
            }
            else if (definition.curveSpacing == SpacingOnCurve.CURVE_EQUI_PITCH)
            {
                var curveLength = massProperties.length;
                var pitch = definition.curvePitch;
                if (pitch <= 0.0)
                {
                    pitch = 5 * millimeter;
                }
                var nSegments = floor(curveLength / pitch);
                if (nSegments < 1)
                    nSegments = 1;
                var paramStep = 1 / nSegments;
                parameterArray = [];
                for (var i = 0; i <= nSegments; i += 1)
                {
                    parameterArray = append(parameterArray, i * paramStep);
                }
                parameterArray = adjustParameterArrayForStartPoint(context, definition, parameterArray);

                var crvPoints = evEdgeTangentLines(context, {
                        "edge" : definition.edge,
                        "parameters" : parameterArray,
                        "arcLengthParameterization" : true
                    });
                outPoints = mapArray(crvPoints, function(x)
                    {
                        return x.origin;
                    });
            }
            else if (definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_LENGTH ||
                     definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_PERCENT)
            {
                var startParam = undefined;
                var currentParam;
                var midTangentParam = 0.5;

                if (definition.closedCurve && definition.pickStartPoint && !isQueryEmpty(context, definition.closedEdgeStartPoint))
                {
                    var distResult = evDistance(context, {
                            "side0" : definition.edge,
                            "side1" : definition.closedEdgeStartPoint,
                            "arcLengthParameterization" : true });
                    startParam = distResult.sides[0].parameter;
                    midTangentParam += startParam;
                    if (midTangentParam > 1)
                        midTangentParam -= 1;
                }
                else
                {
                    var begTangentLine = evEdgeTangentLine(context, { "edge" : definition.edge,
                            "parameter" : definition.flipCurveDirection ? 1 : 0,
                            "arcLengthParameterization" : true });
                    addDebugPoint(context, begTangentLine.origin, DebugColor.YELLOW);
                }

                var midTangentLine = evEdgeTangentLine(context, { "edge" : definition.edge,
                        "parameter" : midTangentParam,
                        "arcLengthParameterization" : true });
                var cpDirectionManipulator is Manipulator = flipManipulator({
                        "base" : midTangentLine.origin,
                        "direction" : midTangentLine.direction,
                        "flipped" : definition.flipCurveDirection
                    });
                addManipulators(context, id, { (CP_DIRECTION_MANIPULATOR) : cpDirectionManipulator });

                if (definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_LENGTH)
                {
                    var massProperties = evApproximateMassProperties(context, { "entities" : definition.edge, "density" : 1 * kilogram / meter });
                    println("curve length is " ~ roundToPrecision(massProperties.length / millimeter, 2) ~ "mm");
                    for (var curveLength in definition.curveLengths)
                    {
                        currentParam = curveLength.length / massProperties.length;
                        currentParam = mapCurveParameter(currentParam, startParam, definition.flipCurveDirection);
                        parameterArray = append(parameterArray, currentParam);
                    }
                }
                else
                {
                    for (var curveLengthInPercent in definition.curveLengthsInPercent)
                    {
                        currentParam = curveLengthInPercent.percent / 100;
                        currentParam = mapCurveParameter(currentParam, startParam, definition.flipCurveDirection);
                        parameterArray = append(parameterArray, currentParam);
                    }
                }
            }
            else if (definition.curveSpacing == SpacingOnCurve.CURVE_RANDOM)
            {
                var currParam = 0;
                var randomNumIncrCrv = 1 / definition.numCrvPoolSize;
                while (currParam < 1)
                {
                    parameterArray = append(parameterArray, currParam);
                    currParam += randomNumIncrCrv;
                }
                parameterArray = append(parameterArray, 1);
                parameterArray = adjustParameterArrayForStartPoint(context, definition, parameterArray);
            }

            if (parameterArray != [])
            {
                var crvPoints = evEdgeTangentLines(context, { "edge" : definition.edge, "parameters" : parameterArray, "arcLengthParameterization" : true });
                outPoints = mapArray(crvPoints, function(x)
                    {
                        return x.origin;
                    });
            }
        }
    }

    if (definition.pointLocation == PointLocation.LOCATION_ON_CRV &&
        definition.curveSpacing == SpacingOnCurve.CURVE_RANDOM)
    {
        if (definition.showPreview)
            for (var inRandomPoint in outPoints)
                addDebugPoint(context, inRandomPoint, DebugColor.YELLOW);

        if (definition.multipleRndPoints)
        {
            var cpTogglePointsManipulator is Manipulator = togglePointsManipulator({
                    "points" : outPoints,
                    "selectedIndices" : [],
                    "suppressedIndices" : []
                });
            addManipulators(context, id, { (CP_TOGGLE_POINTS_MANIPULATOR) : cpTogglePointsManipulator });

            var ii = 0;
            for (var randomPointIndex in definition.randomPointIndices)
            {
                addDebugPoint(context, outPoints[randomPointIndex.index], DebugColor.RED);
                ii += 1;
            }
        }
        else
        {
            var cpPointsManipulator is Manipulator = pointsManipulator({
                    "points" : outPoints,
                    "index" : 0,
                    "primaryParameterId" : "randomSinglePointIdx"
                });
            addManipulators(context, id, { (CP_POINTS_MANIPULATOR) : cpPointsManipulator });

            if (definition.randomSinglePointIdx != -1)
            {
                addDebugPoint(context, outPoints[definition.randomSinglePointIdx], DebugColor.RED);
            }
        }
    }
    else
    {
        var pCount = 0;
        for (var outPoint in outPoints)
        {
            addDebugPoint(context, outPoint, DebugColor.RED);
            pCount += 1;
        }
    }
    return outPoints;
}

function pointsOnPath(context is Context, id is Id, definition is map) returns array
{
    var edgePath;

    try
    {
        edgePath = constructPath(context, definition.edge);
    }
    catch
    {
        throw regenError(ErrorStringEnum.PATH_EDGES_NOT_CONTINUOUS, ["edges"]);
    }

    var parameterArray = [];
    if (definition.curveSpacing == SpacingOnCurve.CURVE_REFERENCE)
    {
        if (!isQueryEmpty(context, definition.referencePoint) && !isQueryEmpty(context, definition.edge))
        {
            var minDist = 10000 * meter;
            var minParam = -1;
            var minIndex = -1;
            var ii = 0;

            for (var edgeP in evaluateQuery(context, definition.edge))
            {
                var distResult = evDistance(context, {
                        "side0" : definition.referencePoint,
                        "side1" : edgeP,
                        "arcLengthParameterization" : true
                    });
                if (distResult.distance < minDist)
                {
                    minDist = distResult.distance;
                    minParam = distResult.sides[1].parameter;
                    minIndex = ii;
                }
                ii += 1;
            }
            var refLine = evEdgeTangentLine(context, {
                    "edge" : qNthElement(definition.edge, minIndex),
                    "parameter" : minParam,
                    "arcLengthParameterization" : true
                });
            return [refLine.origin];
        }
    }
    if (definition.curveSpacing == SpacingOnCurve.CURVE_EQUI)
    {
        var pathLength = evPathLength(context, edgePath);
        var startOffsetVal = definition.startOffset;
        var endOffsetVal = definition.endOffset;

        var startParam = startOffsetVal / pathLength;
        var endParam = (pathLength - endOffsetVal) / pathLength;
        var effectiveParamRange = endParam - startParam;

        if (effectiveParamRange <= 0)
        {
            return [];
        }

        var numPoints = definition.numEquiCrv;
        if (definition.closedCurve)
        {
            var paramStep = effectiveParamRange / numPoints;
            for (var i = 0; i < numPoints; i += 1)
            {
                parameterArray = append(parameterArray, startParam + i * paramStep);
            }
        }
        else
        {
            var numIntervals = numPoints - 1;
            var paramStep = effectiveParamRange / numIntervals;
            for (var i = 0; i < numPoints; i += 1)
            {
                parameterArray = append(parameterArray, startParam + i * paramStep);
            }
        }
        parameterArray = adjustParameterArrayForStartPoint(context, definition, parameterArray);
    }
    else if (definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_LENGTH ||
             definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_PERCENT)
    {
        var evPathTangentLinesResult;
        var midTangentLine;

        if (definition.closedCurve && definition.pickStartPoint && !isQueryEmpty(context, definition.closedEdgeStartPoint))
        {
            evPathTangentLinesResult = evPathTangentLines(context, edgePath, [0.5], definition.closedEdgeStartPoint);
            midTangentLine = evPathTangentLinesResult.tangentLines[0];
        }
        else
        {
            evPathTangentLinesResult = evPathTangentLines(context, edgePath, [0, 0.5]);
            var begTangentLine = evPathTangentLinesResult.tangentLines[0];
            addDebugPoint(context, begTangentLine.origin, DebugColor.YELLOW);
            midTangentLine = evPathTangentLinesResult.tangentLines[1];
        }

        var cpDirectionManipulator is Manipulator = flipManipulator({
                "base" : midTangentLine.origin,
                "direction" : midTangentLine.direction,
                "flipped" : definition.flipCurveDirection
            });
        addManipulators(context, id, { (CP_DIRECTION_MANIPULATOR) : cpDirectionManipulator });

        if (definition.curveSpacing == SpacingOnCurve.CURVE_CUSTOM_LENGTH)
        {
            var pathLength = evPathLength(context, edgePath);
            println("path length is " ~ roundToPrecision(pathLength / millimeter, 2) ~ "mm");

            for (var curveLength in definition.curveLengths)
                parameterArray = append(parameterArray, curveLength.length / pathLength);
        }
        else
        {
            for (var curveLengthInPercent in definition.curveLengthsInPercent)
                parameterArray = append(parameterArray, curveLengthInPercent.percent / 100);
        }
    }
    else if (definition.curveSpacing == SpacingOnCurve.CURVE_RANDOM)
    {
        var currParam = 0;
        var randomNumIncrCrv = 1 / definition.numCrvPoolSize;
        while (currParam < 1)
        {
            parameterArray = append(parameterArray, currParam);
            currParam += randomNumIncrCrv;
        }
        parameterArray = append(parameterArray, 1);
        parameterArray = adjustParameterArrayForStartPoint(context, definition, parameterArray);
    }
    else if (definition.curveSpacing == SpacingOnCurve.CURVE_EQUI_PITCH)
    {
        var pathLength = evPathLength(context, edgePath);
        var pitch = definition.curvePitch;
        if (pitch <= 0.0)
            pitch = 5 * millimeter;
        var nSegments = floor(pathLength / pitch);
        if (nSegments < 1)
            nSegments = 1;
        var paramStep = 1 / nSegments;
        for (var i = 0; i <= nSegments; i += 1)
        {
            parameterArray = append(parameterArray, i * paramStep);
        }
        parameterArray = adjustParameterArrayForStartPoint(context, definition, parameterArray);
    }

    if (parameterArray != [])
    {
        var pathPoints;

        if (definition.flipCurveDirection)
            parameterArray = mapArray(parameterArray, function(x)
                {
                    return (1 - x);
                });

        if (definition.closedCurve && definition.pickStartPoint && !isQueryEmpty(context, definition.closedEdgeStartPoint))
            pathPoints = evPathTangentLines(context, edgePath, parameterArray, definition.closedEdgeStartPoint);
        else
            pathPoints = evPathTangentLines(context, edgePath, parameterArray);

        return mapArray(pathPoints.tangentLines, function(x)
            {
                return x.origin;
            });
    }

    return [];
}

function mapCurveParameter(inParam, startParam, flipDirection)
{
    var outParam;
    if (isUndefinedOrEmptyString(startParam))
    {
        outParam = (flipDirection ? 1 - inParam : inParam);
    }
    else
    {
        if (flipDirection)
        {
            outParam = startParam - inParam;
            if (outParam < 0)
                outParam += 1;
        }
        else
        {
            // Instead of adding, subtract to make the selected point become zero.
            outParam = inParam - startParam;
            if (outParam < 0)
                outParam += 1;
        }
    }
    return outParam;
}


export function createPointsManipulatorChange(context is Context, definition is map, manipulators is map) returns map
{
    if (manipulators[CP_POINTS_MANIPULATOR] is map)
    {
        if (!definition.multipleRndPoints)
        {
            var newIndex = manipulators[CP_POINTS_MANIPULATOR].index;

            if (newIndex == definition.randomSinglePointIdx)
                definition.randomSinglePointIdx = -1;
            else
                definition.randomSinglePointIdx = newIndex;
        }
    }

    if (manipulators[CP_TOGGLE_POINTS_MANIPULATOR] is map)
    {
        if (definition.multipleRndPoints)
        {
            var newIndices = manipulators[CP_TOGGLE_POINTS_MANIPULATOR].selectedIndices;

            for (var newIndex in newIndices)
            {
                var bToggle = false;
                for (var ii = 0; ii < size(definition.randomPointIndices); ii += 1)
                {
                    if (definition.randomPointIndices[ii].index == newIndex)
                    {
                        definition.randomPointIndices = removeElementAt(definition.randomPointIndices, ii);
                        bToggle = true;
                        break;
                    }
                }

                if (!bToggle)
                {
                    var randomPointIndex = {};
                    randomPointIndex.index = newIndex;
                    definition.randomPointIndices = append(definition.randomPointIndices, randomPointIndex);
                }
            }
        }
    }

    if (manipulators[CP_DIRECTION_MANIPULATOR] is map)
        definition.flipCurveDirection = manipulators[CP_DIRECTION_MANIPULATOR].flipped;

    return definition;
}

export function createPointsEditingLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean) returns map
{
    var nEdges = size(evaluateQuery(context, definition.edge));
    if (isUndefinedOrEmptyString(nEdges))
    {
        definition.closedCurve = false;
    }
    else if (nEdges > 1)
    {
        var edgePath = try silent(constructPath(context, definition.edge));
        if (!isUndefinedOrEmptyString(edgePath) && isQueryEmpty(context, getPathEndVertices(context, edgePath)))
            definition.closedCurve = true;
        else
            definition.closedCurve = false;
    }
    else if (nEdges == 1)
    {
        definition.closedCurve = isClosed(context, definition.edge);
    }
    else
    {
        definition.closedCurve = false;
    }

    var clearSelection = true;
    if (oldDefinition.pointLocation == PointLocation.LOCATION_ON_CRV && definition.pointLocation == PointLocation.LOCATION_ON_CRV)
    {
        if (oldDefinition.curveSpacing == SpacingOnCurve.CURVE_RANDOM && definition.curveSpacing == SpacingOnCurve.CURVE_RANDOM)
            if (areQueriesEquivalent(context, oldDefinition.edge, definition.edge))
            if (oldDefinition.selectionPool && definition.selectionPool)
            if (oldDefinition.numCrvPoolSize == definition.numCrvPoolSize)
                clearSelection = false;
    }

    if (clearSelection)
        definition.randomPointIndices = resize(definition.randomPointIndices, 0);

    return definition;
}
