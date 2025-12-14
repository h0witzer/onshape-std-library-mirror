FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/path.fs", version : "2837.0");

import(path : "12312312345abcabcabcdeff/a6fb0cf8f4a5191f6485f2f7/b2077a52c9d520f2bda0b236", version : "2c109a09f2a3e5d28a9f523f");

/**
 * Auto Connection Loop (Path Length): Creates bidirectional loft connections between two edge groups.
 * 
 * This variant uses path length parameterization instead of evDistance checks for connections.
 * 
 * This feature performs a two-pass connection algorithm to ensure all internal vertices 
 * find partners for the loft operation:
 * 
 * 1. First pass: Creates connections from Edge Group 1 vertices to Edge Group 2 using path length ratios
 * 2. Second pass: Creates connections from unmatched Edge Group 2 vertices to Edge Group 1 using path length ratios
 * 
 * Path length approach: For each vertex, calculate its position along the edge group as a ratio
 * of total path length, then use that same ratio to find the corresponding parameter on the 
 * other edge group.
 * 
 * This bidirectional approach guarantees consistent results regardless of input order 
 * (A to B vs B to A) and ensures all vertices are properly connected.
 */

export const INDEX_BOUNDS =
{
    (unitless) : [0, 0, 1e5]
} as IntegerBoundSpec;

annotation { "Feature Type Name" : "Auto Connection Loop Path Length", "Feature Type Description" : "" }
export const loftAutoConnectionPathLength = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edge 1", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.edge1 is Query;

        annotation { "Name" : "Edge 2", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.edge2 is Query;
        
        annotation { "Name" : "Guide Curve 1", "Filter" : EntityType.EDGE && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.guide0 is Query;
        
        annotation { "Name" : "Guide Curve 2", "Filter" : EntityType.EDGE && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.guide1 is Query;
        
  
    }
    {
        var edgeGroup1 = qTangentConnectedEdges(definition.edge1);
        var edgeGroup2 = qTangentConnectedEdges(definition.edge2);
        var guide0 = definition.guide0;
        var guide1 = definition.guide1;

        println("Number of edges 1: " ~ evaluateQueryCount(context, edgeGroup1));
        println("Number of edges 2: " ~ evaluateQueryCount(context, edgeGroup2));
        
        // Construct ordered paths from the edge groups
        var path1 = constructPath(context, edgeGroup1);
        var path2 = constructPath(context, edgeGroup2);
        
        var totalLength1 = evPathLength(context, path1);
        var totalLength2 = evPathLength(context, path2);
        
        println("Total length 1: " ~ totalLength1);
        println("Total length 2: " ~ totalLength2);
        
        // Get the internal vertices for each path (vertices that are between edges)
        var endPoints1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
        var adjFaces1 = qAdjacent(edgeGroup1, AdjacencyType.EDGE, EntityType.FACE);
        var smoothEdges1 = qAdjacent(adjFaces1, AdjacencyType.EDGE, EntityType.EDGE)->qEdgeConvexityTypeFilter(EdgeConvexityType.SMOOTH);
        var internalEndPoints1 = qAdjacent(smoothEdges1, AdjacencyType.VERTEX, EntityType.VERTEX);
        var edgeInternalPoints1 = qIntersection([endPoints1, internalEndPoints1]);
        
        debug(context, edgeInternalPoints1, DebugColor.RED);

        // Step 1: Calculate path length ratios for all vertices in both paths
        var pathRatios1 = [];
        for (var i = 0; i < evaluateQueryCount(context, edgeInternalPoints1); i += 1)
        {
            var currentPoint = qNthElement(edgeInternalPoints1, i);
            var pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, path1, totalLength1);
            pathRatios1 = append(pathRatios1, pathLengthRatio);
            println("Vertex " ~ i ~ " from edge1 at path length ratio: " ~ pathLengthRatio);
        }
        
        var allVertices2 = qAdjacent(edgeGroup2, AdjacencyType.VERTEX, EntityType.VERTEX);
        var adjFaces2 = qAdjacent(edgeGroup2, AdjacencyType.EDGE, EntityType.FACE);
        var smoothEdges2 = qAdjacent(adjFaces2, AdjacencyType.EDGE, EntityType.EDGE)->qEdgeConvexityTypeFilter(EdgeConvexityType.SMOOTH);
        var internalEndPoints2 = qAdjacent(smoothEdges2, AdjacencyType.VERTEX, EntityType.VERTEX);
        var edgeInternalPoints2 = qIntersection([allVertices2, internalEndPoints2]);
        
        debug(context, edgeInternalPoints2, DebugColor.BLUE);
        
        var pathRatios2 = [];
        for (var j = 0; j < evaluateQueryCount(context, edgeInternalPoints2); j += 1)
        {
            var currentPoint = qNthElement(edgeInternalPoints2, j);
            var pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, path2, totalLength2);
            pathRatios2 = append(pathRatios2, pathLengthRatio);
            println("Vertex " ~ j ~ " from edge2 at path length ratio: " ~ pathLengthRatio);
        }
        
        // Step 2: Merge and sort all unique path length ratios
        var allRatios = concatenateArrays(pathRatios1, pathRatios2);
        allRatios = removeDuplicateRatios(allRatios);
        allRatios = sortRatios(allRatios);
        
        println("Total unique path ratios: " ~ size(allRatios));
        
        // Step 3: Create connections at all path length ratios
        var loftConnections = [];
        for (var ratio in allRatios)
        {
            // Convert ratio to edge+parameter on both paths using evPathTangentLines
            var result1 = evPathTangentLines(context, path1, [ratio]);
            var result2 = evPathTangentLines(context, path2, [ratio]);
            
            var edge1 = path1.edges[result1.edgeIndices[0]];
            var edge2 = path2.edges[result2.edgeIndices[0]];
            
            // Calculate local parameters on the specific edges
            var edge1Info = calculateLocalEdgeParameter(context, path1, result1.edgeIndices[0], ratio, totalLength1);
            var edge2Info = calculateLocalEdgeParameter(context, path2, result2.edgeIndices[0], ratio, totalLength2);
            
            // Get the 3D points for debugging
            edge1Info.point = evEdgeTangentLine(context, {
                            "edge" : edge1Info.edge,
                            "parameter" : edge1Info.parameter,
                            "arcLengthParameterization" : true
                        }).origin;
            
            edge2Info.point = evEdgeTangentLine(context, {
                            "edge" : edge2Info.edge,
                            "parameter" : edge2Info.parameter,
                            "arcLengthParameterization" : true
                        }).origin;
            
            // Debug visualization
            debug(context, edge1Info.point, DebugColor.GREEN);
            debug(context, edge2Info.point, DebugColor.MAGENTA);
            
            // Create connection from edge1 point to edge2
            var connectionMap = {
                "connectionEntities" : qUnion([edge1Info.edge, edge2Info.edge]),
                "connectionEdges" : [edge1Info.edge, edge2Info.edge],
                "connectionEdgeParameters" : [edge1Info.parameter, edge2Info.parameter]
            };
            
            loftConnections = append(loftConnections, connectionMap);
            
            println("Connection at ratio " ~ ratio ~ ": edge1 param=" ~ edge1Info.parameter ~ ", edge2 param=" ~ edge2Info.parameter);
        }        

        var loftDerivativeInfo1 = {
            "profileIndex" : 0,
            "magnitude" : 1,
            "matchCurvature" : false,
            "adjacentFaces" : qAdjacent(edgeGroup1, AdjacencyType.EDGE, EntityType.FACE)
        };

        var loftDerivativeInfo2 = {
            "profileIndex" : 1,
            "magnitude" : 1,
            "matchCurvature" : false,
            "adjacentFaces" : qAdjacent(edgeGroup2, AdjacencyType.EDGE, EntityType.FACE)
        };


        opLoft(context, id + "loft1", {
                    "profileSubqueries" : [edgeGroup1, edgeGroup2],
                    "guideSubqueries": [guide0, guide1],
                    "connections" : loftConnections,
                    "bodyType" : ToolBodyType.SURFACE,
                    // "trimProfiles" : true,
                    // "trimGuidesByProfiles" : true,
                    "derivativeInfo" : [loftDerivativeInfo1, loftDerivativeInfo2],
                    "loftTopology" : LoftTopology.COLUMNS
                });

    });

/**
 * Remove duplicate ratios from an array (within tolerance).
 * 
 * @param ratios {array}: Array of numbers
 * @returns {array}: Array with duplicates removed
 */
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

/**
 * Sort an array of numbers.
 * 
 * @param arr {array}: Array of numbers
 * @returns {array}: Sorted array
 */
function sortRatios(arr is array) returns array
{
    // Simple bubble sort for small arrays
    var sorted = arr;
    var n = size(sorted);
    for (var i = 0; i < n - 1; i += 1)
    {
        for (var j = 0; j < n - i - 1; j += 1)
        {
            if (sorted[j] > sorted[j + 1])
            {
                var temp = sorted[j];
                sorted[j] = sorted[j + 1];
                sorted[j + 1] = temp;
            }
        }
    }
    return sorted;
}

/**
 * Calculate the path length ratio (0 to 1) of a vertex position along a path.
 * 
 * @param context {Context}: The context
 * @param vertex {Query}: The vertex to find the position of
 * @param path {Path}: The ordered path to measure along
 * @param totalLength {ValueWithUnits}: The total length of the path
 * 
 * @returns {number}: The ratio (0 to 1) of the vertex position along the path
 */
function calculatePathLengthRatioForVertex(context is Context, vertex is Query, path is Path, totalLength is ValueWithUnits) returns number
{
    var vertexPosition = evVertexPoint(context, {
                "vertex" : vertex
            });
    
    // Find which edge in the ordered path contains this vertex
    var pathLengthBeforeEdge = 0 * meter;
    
    for (var i = 0; i < size(path.edges); i += 1)
    {
        var edge = path.edges[i];
        var edgeLength = evLength(context, {
                    "entities" : edge
                });
        
        // Check if vertex is on this edge by using evDistance
        var dist = evDistance(context, {
                    "side0" : vertex,
                    "side1" : edge,
                    "arcLengthParameterization" : true
                });
        
        // If the vertex is very close to this edge, it's on this edge
        if (dist.distance < TOLERANCE.zeroLength * meter)
        {
            var arcLengthParameter = dist.sides[1].parameter;
            var lengthAlongEdge = edgeLength * arcLengthParameter;
            
            // Account for edge flipping in the path
            if (path.flipped[i])
            {
                lengthAlongEdge = edgeLength - lengthAlongEdge;
            }
            
            var totalPathLengthToVertex = pathLengthBeforeEdge + lengthAlongEdge;
            return totalPathLengthToVertex / totalLength;
        }
        
        pathLengthBeforeEdge = pathLengthBeforeEdge + edgeLength;
    }
    
    // If we didn't find the vertex on any edge, return 0 (shouldn't happen)
    return 0;
}

/**
 * Calculate the local edge parameter for a given edge index and global path ratio.
 * 
 * @param context {Context}: The context
 * @param path {Path}: The path
 * @param edgeIndex {number}: The index of the edge in the path
 * @param globalRatio {number}: The global ratio (0-1) along the entire path
 * @param totalLength {ValueWithUnits}: The total length of the path
 * 
 * @returns {map}: A map with "edge" (Query) and "parameter" (number) fields
 */
function calculateLocalEdgeParameter(context is Context, path is Path, edgeIndex is number, globalRatio is number, totalLength is ValueWithUnits) returns map
{
    // Clamp the ratio to [0, 1] to handle numerical errors
    globalRatio = max(0.0, min(1.0, globalRatio));
    
    var targetPathLength = totalLength * globalRatio;
    var accumulatedLength = 0 * meter;
    
    // Calculate accumulated length up to this edge
    for (var i = 0; i < edgeIndex; i += 1)
    {
        accumulatedLength += evLength(context, {
                    "entities" : path.edges[i]
                });
    }
    
    // Calculate the local parameter on this edge
    var edgeLength = evLength(context, {
                "entities" : path.edges[edgeIndex]
            });
    
    var lengthIntoEdge = targetPathLength - accumulatedLength;
    var parameterOnEdge = lengthIntoEdge / edgeLength;
    
    // Account for edge flipping
    if (path.flipped[edgeIndex])
    {
        parameterOnEdge = 1.0 - parameterOnEdge;
    }
    
    // Clamp parameter to [0, 1] to avoid numerical errors
    parameterOnEdge = max(0.0, min(1.0, parameterOnEdge));
    
    return {
        "edge" : path.edges[edgeIndex],
        "parameter" : parameterOnEdge
    };
}

// sample outputs from loft feature

// "connections" : [{ "connectionEntities" : qUnion([QyYwTUxANZfshu_query, rFlvPOBBnYbHoF_query]), "connectionEdgeQueries" : qUnion([UNawptIesSVOFj_query]), "connectionEdgeParameters" : [0.5]],


// "connections" : [{ "connectionEntities" : qUnion([cWmRAlGarUkTAe_query, cWsdiGpRXWSUvc_query]), "connectionEdgeQueries" : qUnion([ibICFFiMdolvEk_query]), "connectionEdgeParameters" : [0.5]]

/* "matchConnections" : true, 
"connections" : [
{ "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]}, 
{ "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]}, 
{ "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]}, 
{ "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]}
]


*/
