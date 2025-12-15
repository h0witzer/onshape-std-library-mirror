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
import(path : "onshape/std/path.fs", version : "2837.0");

// Import tweenCurves function and utilities
import(path : "eba90c822a38b2ab9d2b67c5", version : "028e645c08deafca1e158865"); // tweenTwoCurves.fs (includes tweenCurves)
import(path : "f42f46716945f2a9bda5a481/eabbc18661ba5776e0ba962d/97730412fb61f53dcd526c08", version : "a24da502290d2ae4706c631f"); // 3d Arc Utilities

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
    "Feature Type Description" : "Interpolates B-spline control points between two paths of multiple curves. Uses the standard tweenCurves function.",
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
        // Check if we have single edges first, before any query manipulation
        const initialCount1 = evaluateQueryCount(context, definition.curves1);
        const initialCount2 = evaluateQueryCount(context, definition.curves2);
        
        // If both are single edges, pass the original queries directly to tweenCurves
        // This ensures identical behavior to tweenTwoCurves
        if (initialCount1 == 1 && initialCount2 == 1)
        {
            tweenCurves(context, id, definition.curves1, definition.curves2, definition.fraction);
            return;
        }
        
        // Get all connected edges for each input (only for multi-curve case)
        const edgeGroup1 = qTangentConnectedEdges(definition.curves1);
        const edgeGroup2 = qTangentConnectedEdges(definition.curves2);
        
        const edgeCount1 = evaluateQueryCount(context, edgeGroup1);
        const edgeCount2 = evaluateQueryCount(context, edgeGroup2);
        
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
            
            // For now, if segment is a full edge, tween it using the imported function
            if (start1.edge == end1.edge && start1.parameter == 0.0 && end1.parameter == 1.0)
            {
                if (start2.edge == end2.edge && start2.parameter == 0.0 && end2.parameter == 1.0)
                {
                    tweenCurves(context, id + ("seg_" ~ i), start1.edge, start2.edge, definition.fraction);
                }
            }
        }
    });

// Multi-curve helper functions

function generateNearestDistanceBreakPoints(context is Context, path1 is Path, path2 is Path,
                                             edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    const totalLength1 = evPathLength(context, path1);
    const totalLength2 = evPathLength(context, path2);
    
    // Get all vertices from both edge groups
    const allVertices1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
    const allVertices2 = qAdjacent(edgeGroup2, AdjacencyType.VERTEX, EntityType.VERTEX);
    
    var breakPointMaps = [];
    
    // First pass: Map vertices from path1 to path2 using evDistance
    for (var i = 0; i < evaluateQueryCount(context, allVertices1); i += 1)
    {
        const vertex = qNthElement(allVertices1, i);
        const vertexPos = evVertexPoint(context, { "vertex" : vertex });
        
        // Find position on path1
        const pos1 = getVertexPositionOnPath(context, vertex, path1, totalLength1);
        
        // Find closest point on path2 using evDistance
        const edgeDist = evDistance(context, {
                    "side0" : vertex,
                    "side1" : edgeGroup2,
                    "arcLengthParameterization" : true
                });
        
        const pos2 = convertEdgeInfoToPathPosition(context, path2, totalLength2,
                                                    edgeDist.sides[1].index, edgeDist.sides[1].parameter);
        
        breakPointMaps = append(breakPointMaps, {
                    "sortKey" : pos1.ratio,
                    "segment1" : pos1,
                    "segment2" : pos2,
                    "point1" : vertexPos,
                    "point2" : evEdgeTangentLine(context, {
                                "edge" : pos2.edge,
                                "parameter" : pos2.parameter
                            }).origin
                });
        
        // Debug point on path1 (RED)
        debug(context, vertex, DebugColor.RED);
    }
    
    // Collect all mapped points on path2 from first pass
    var mappedPointsOnPath2 = [];
    for (var bp in breakPointMaps)
    {
        mappedPointsOnPath2 = append(mappedPointsOnPath2, bp.point2);
    }
    
    // Second pass: Map vertices from path2 to path1 for unmatched vertices
    for (var j = 0; j < evaluateQueryCount(context, allVertices2); j += 1)
    {
        const vertex = qNthElement(allVertices2, j);
        const vertexPos = evVertexPoint(context, { "vertex" : vertex });
        
        // Check if this vertex is already covered (close to any mapped point)
        var alreadyMapped = false;
        for (var mappedPt in mappedPointsOnPath2)
        {
            if (norm(vertexPos - mappedPt) < TOLERANCE.zeroLength * meter)
            {
                alreadyMapped = true;
                break;
            }
        }
        
        if (!alreadyMapped)
        {
            // Find position on path2
            const pos2 = getVertexPositionOnPath(context, vertex, path2, totalLength2);
            
            // Find closest point on path1 using evDistance
            const edgeDist = evDistance(context, {
                        "side0" : vertex,
                        "side1" : edgeGroup1,
                        "arcLengthParameterization" : true
                    });
            
            const pos1 = convertEdgeInfoToPathPosition(context, path1, totalLength1,
                                                        edgeDist.sides[1].index, edgeDist.sides[1].parameter);
            
            breakPointMaps = append(breakPointMaps, {
                        "sortKey" : pos1.ratio,
                        "segment1" : pos1,
                        "segment2" : pos2,
                        "point1" : evEdgeTangentLine(context, {
                                    "edge" : pos1.edge,
                                    "parameter" : pos1.parameter
                                }).origin,
                        "point2" : vertexPos
                    });
            
            // Debug point on path2 (BLUE)
            debug(context, vertex, DebugColor.BLUE);
        }
    }
    
    // Sort by position on path1
    breakPointMaps = sortBreakPointsByKey(breakPointMaps);
    
    // Add start and end if not present
    if (size(breakPointMaps) == 0 || breakPointMaps[0].sortKey > TOLERANCE.zeroLength)
    {
        breakPointMaps = concatenateArrays([[{
                    "sortKey" : 0.0,
                    "segment1" : { "edge" : path1.edges[0], "parameter" : 0.0, "ratio" : 0.0 },
                    "segment2" : { "edge" : path2.edges[0], "parameter" : 0.0, "ratio" : 0.0 }
                }], breakPointMaps]);
    }
    
    if (size(breakPointMaps) == 0 || breakPointMaps[size(breakPointMaps) - 1].sortKey < 1.0 - TOLERANCE.zeroLength)
    {
        breakPointMaps = append(breakPointMaps, {
                    "sortKey" : 1.0,
                    "segment1" : { "edge" : path1.edges[size(path1.edges) - 1], "parameter" : 1.0, "ratio" : 1.0 },
                    "segment2" : { "edge" : path2.edges[size(path2.edges) - 1], "parameter" : 1.0, "ratio" : 1.0 }
                });
    }
    
    return breakPointMaps;
}

function generatePathLengthBreakPoints(context is Context, path1 is Path, path2 is Path,
                                        edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    const totalLength1 = evPathLength(context, path1);
    const totalLength2 = evPathLength(context, path2);
    
    // Get all vertices from both edge groups
    const allVertices1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
    const allVertices2 = qAdjacent(edgeGroup2, AdjacencyType.VERTEX, EntityType.VERTEX);
    
    // Collect all path ratios from both paths
    var pathRatios = [];
    
    // Get ratios from path1 vertices
    for (var i = 0; i < evaluateQueryCount(context, allVertices1); i += 1)
    {
        const vertex = qNthElement(allVertices1, i);
        const pos = getVertexPositionOnPath(context, vertex, path1, totalLength1);
        pathRatios = append(pathRatios, pos.ratio);
        
        // Debug point on path1 (RED)
        debug(context, vertex, DebugColor.RED);
    }
    
    // Get ratios from path2 vertices
    for (var j = 0; j < evaluateQueryCount(context, allVertices2); j += 1)
    {
        const vertex = qNthElement(allVertices2, j);
        const pos = getVertexPositionOnPath(context, vertex, path2, totalLength2);
        pathRatios = append(pathRatios, pos.ratio);
        
        // Debug point on path2 (BLUE)
        debug(context, vertex, DebugColor.BLUE);
    }
    
    // Remove duplicates and sort
    pathRatios = removeDuplicateRatios(pathRatios);
    pathRatios = sortRatios(pathRatios);
    
    // Ensure start and end are present
    if (size(pathRatios) == 0 || pathRatios[0] > TOLERANCE.zeroLength)
    {
        pathRatios = concatenateArrays([[0.0], pathRatios]);
    }
    if (size(pathRatios) == 0 || pathRatios[size(pathRatios) - 1] < 1.0 - TOLERANCE.zeroLength)
    {
        pathRatios = append(pathRatios, 1.0);
    }
    
    // Convert ratios to break point maps
    var breakPointMaps = [];
    for (var ratio in pathRatios)
    {
        const pos1 = getPositionAtRatio(context, path1, totalLength1, ratio);
        const pos2 = getPositionAtRatio(context, path2, totalLength2, ratio);
        
        breakPointMaps = append(breakPointMaps, {
                    "sortKey" : ratio,
                    "segment1" : pos1,
                    "segment2" : pos2
                });
    }
    
    return breakPointMaps;
}

// Helper functions for path operations

function getVertexPositionOnPath(context is Context, vertex is Query, path is Path, totalLength is ValueWithUnits) returns map
{
    var accumulatedLength = 0 * meter;
    
    for (var i = 0; i < size(path.edges); i += 1)
    {
        const edge = path.edges[i];
        const edgeLength = evLength(context, { "entities" : edge });
        
        const dist = evDistance(context, {
                    "side0" : vertex,
                    "side1" : edge,
                    "arcLengthParameterization" : true
                });
        
        if (dist.distance < TOLERANCE.zeroLength * meter)
        {
            var param = dist.sides[1].parameter;
            var lengthIntoEdge = edgeLength * param;
            
            if (path.flipped[i])
            {
                lengthIntoEdge = edgeLength - lengthIntoEdge;
            }
            
            const totalToVertex = accumulatedLength + lengthIntoEdge;
            
            return {
                "edge" : edge,
                "parameter" : param,
                "ratio" : totalToVertex / totalLength
            };
        }
        
        accumulatedLength += edgeLength;
    }
    
    // Fallback to start
    return { "edge" : path.edges[0], "parameter" : 0.0, "ratio" : 0.0 };
}

function convertEdgeInfoToPathPosition(context is Context, path is Path, totalLength is ValueWithUnits,
                                        edgeIndex is number, edgeParameter is number) returns map
{
    var accumulatedLength = 0 * meter;
    
    for (var i = 0; i < edgeIndex; i += 1)
    {
        accumulatedLength += evLength(context, { "entities" : path.edges[i] });
    }
    
    const edgeLength = evLength(context, { "entities" : path.edges[edgeIndex] });
    var lengthIntoEdge = edgeLength * edgeParameter;
    
    if (path.flipped[edgeIndex])
    {
        lengthIntoEdge = edgeLength - lengthIntoEdge;
    }
    
    const totalToPoint = accumulatedLength + lengthIntoEdge;
    
    return {
        "edge" : path.edges[edgeIndex],
        "parameter" : edgeParameter,
        "ratio" : totalToPoint / totalLength
    };
}

function getPositionAtRatio(context is Context, path is Path, totalLength is ValueWithUnits, ratio is number) returns map
{
    ratio = max(0.0, min(1.0, ratio));
    const targetLength = totalLength * ratio;
    var accumulatedLength = 0 * meter;
    
    for (var i = 0; i < size(path.edges); i += 1)
    {
        const edgeLength = evLength(context, { "entities" : path.edges[i] });
        
        if (accumulatedLength + edgeLength >= targetLength || i == size(path.edges) - 1)
        {
            const lengthIntoEdge = targetLength - accumulatedLength;
            var param = lengthIntoEdge / edgeLength;
            
            if (path.flipped[i])
            {
                param = 1.0 - param;
            }
            
            param = max(0.0, min(1.0, param));
            
            return {
                "edge" : path.edges[i],
                "parameter" : param,
                "ratio" : ratio
            };
        }
        
        accumulatedLength += edgeLength;
    }
    
    // Fallback to end
    return {
        "edge" : path.edges[size(path.edges) - 1],
        "parameter" : path.flipped[size(path.edges) - 1] ? 0.0 : 1.0,
        "ratio" : 1.0
    };
}

function sortBreakPointsByKey(arr is array) returns array
{
    var sorted = arr;
    const n = size(sorted);
    for (var i = 0; i < n - 1; i += 1)
    {
        for (var j = 0; j < n - i - 1; j += 1)
        {
            if (sorted[j].sortKey > sorted[j + 1].sortKey)
            {
                const temp = sorted[j];
                sorted[j] = sorted[j + 1];
                sorted[j + 1] = temp;
            }
        }
    }
    return sorted;
}

function removeDuplicateRatios(ratios is array) returns array
{
    var unique = [];
    for (var ratio in ratios)
    {
        var isDuplicate = false;
        for (var existing in unique)
        {
            if (abs(ratio - existing) < TOLERANCE.zeroLength)
            {
                isDuplicate = true;
                break;
            }
        }
        if (!isDuplicate)
        {
            unique = append(unique, ratio);
        }
    }
    return unique;
}

function sortRatios(arr is array) returns array
{
    var sorted = arr;
    const n = size(sorted);
    for (var i = 0; i < n - 1; i += 1)
    {
        for (var j = 0; j < n - i - 1; j += 1)
        {
            if (sorted[j] > sorted[j + 1])
            {
                const temp = sorted[j];
                sorted[j] = sorted[j + 1];
                sorted[j + 1] = temp;
            }
        }
    }
    return sorted;
}
