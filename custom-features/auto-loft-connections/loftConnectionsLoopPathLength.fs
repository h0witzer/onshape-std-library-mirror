FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

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

        var endPoints1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);

        println("Number of edges 1: " ~ evaluateQueryCount(context, edgeGroup1));
        println("Number of edges 2: " ~ evaluateQueryCount(context, edgeGroup2));
        
        // Calculate total path length for both edge groups
        var totalLength1 = evLength(context, {
                    "entities" : edgeGroup1
                });
        var totalLength2 = evLength(context, {
                    "entities" : edgeGroup2
                });
        
        println("Total length 1: " ~ totalLength1);
        println("Total length 2: " ~ totalLength2);
        
        // Get the internal points for the profile. we don't want the edgeGroup endpoints
        var adjFaces1 = qAdjacent(edgeGroup1, AdjacencyType.EDGE, EntityType.FACE);
        var smoothEdges1 = qAdjacent(adjFaces1, AdjacencyType.EDGE, EntityType.EDGE)->qEdgeConvexityTypeFilter(EdgeConvexityType.SMOOTH);
        var internalEndPoints1 = qAdjacent(smoothEdges1, AdjacencyType.VERTEX, EntityType.VERTEX);
        
        var edgeInternalPoints1 = qIntersection([endPoints1, internalEndPoints1]);
        
        debug(context, edgeInternalPoints1, DebugColor.RED);

        // Step 1: Calculate path length ratios for all vertices in both edge groups
        var pathRatios1 = [];
        for (var i = 0; i < evaluateQueryCount(context, edgeInternalPoints1); i += 1)
        {
            var currentPoint = qNthElement(edgeInternalPoints1, i);
            var pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, edgeGroup1, totalLength1);
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
            var pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, edgeGroup2, totalLength2);
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
            // Convert ratio to edge+parameter on both groups
            var edge1Info = convertPathLengthRatioToEdgeParameter(context, ratio, edgeGroup1, totalLength1);
            var edge2Info = convertPathLengthRatioToEdgeParameter(context, ratio, edgeGroup2, totalLength2);
            
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
 * Calculate the path length ratio (0 to 1) of a vertex position along an edge group.
 * 
 * @param context {Context}: The context
 * @param vertex {Query}: The vertex to find the position of
 * @param edgeGroup {Query}: The edge group to measure along
 * @param totalLength {ValueWithUnits}: The total length of the edge group
 * 
 * @returns {number}: The ratio (0 to 1) of the vertex position along the edge group
 */
function calculatePathLengthRatioForVertex(context is Context, vertex is Query, edgeGroup is Query, totalLength is ValueWithUnits) returns number
{
    // Get the 3D position of the vertex
    var vertexPosition = evVertexPoint(context, {
                "vertex" : vertex
            });
    
    // Find which edge in the group contains this vertex or is closest to it
    var edgeDist = evDistance(context, {
                "side0" : vertex,
                "side1" : edgeGroup
            });
    
    var closestEdge = qNthElement(edgeGroup, edgeDist.sides[1].index);
    
    // Now we need to find the arc-length parameter on this edge
    // We'll project the vertex position onto the edge with arc-length parameterization
    var closestEdgeLength = evLength(context, {
                "entities" : closestEdge
            });
    
    // Use evDistance to get the closest point, then use a binary search or approximation
    // to find the arc-length parameter. For simplicity, we'll use evDistance result
    // but recognize it's not perfect. A better approach would be to evaluate multiple
    // points along the edge and find the closest one.
    var arcLengthParameter = findArcLengthParameter(context, closestEdge, vertexPosition);
    
    // Calculate the path length up to the start of this edge
    var edgeGroupArray = evaluateQuery(context, edgeGroup);
    var pathLengthBeforeEdge = 0 * meter;
    
    for (var edge in edgeGroupArray)
    {
        var isClosestEdge = !isQueryEmpty(context, qIntersection([edge, closestEdge]));
        if (isClosestEdge)
        {
            break;
        }
        else
        {
            var edgeLength = evLength(context, {
                        "entities" : edge
                    });
            pathLengthBeforeEdge = pathLengthBeforeEdge + edgeLength;
        }
    }
    
    // Calculate the length along the closest edge to the vertex using arc-length parameter
    var lengthAlongEdge = closestEdgeLength * arcLengthParameter;
    
    // Total path length to the vertex
    var totalPathLengthToVertex = pathLengthBeforeEdge + lengthAlongEdge;
    
    // Return as a ratio
    var ratio = totalPathLengthToVertex / totalLength;
    
    return ratio;
}

/**
 * Find the arc-length parameter for a point on an edge.
 * Uses binary search to find the parameter that gives the closest point.
 * 
 * @param context {Context}: The context
 * @param edge {Query}: The edge to search on
 * @param targetPoint {Vector}: The 3D point to find parameter for
 * 
 * @returns {number}: The arc-length parameter (0-1)
 */
function findArcLengthParameter(context is Context, edge is Query, targetPoint is Vector) returns number
{
    // Binary search for the arc-length parameter
    var minParam = 0.0;
    var maxParam = 1.0;
    var tolerance = 0.0001;
    
    for (var iter = 0; iter < 20; iter += 1)
    {
        var midParam = (minParam + maxParam) / 2.0;
        
        var pointAtMid = evEdgeTangentLine(context, {
                    "edge" : edge,
                    "parameter" : midParam,
                    "arcLengthParameterization" : true
                }).origin;
        
        var distToTarget = norm(pointAtMid - targetPoint);
        
        // Check if we're close enough
        if (distToTarget < TOLERANCE.zeroLength * meter)
        {
            return midParam;
        }
        
        // Determine which half to search
        // Evaluate at slightly before and after midParam to determine gradient
        var paramBefore = max(0.0, midParam - 0.01);
        var paramAfter = min(1.0, midParam + 0.01);
        
        var pointBefore = evEdgeTangentLine(context, {
                    "edge" : edge,
                    "parameter" : paramBefore,
                    "arcLengthParameterization" : true
                }).origin;
        
        var pointAfter = evEdgeTangentLine(context, {
                    "edge" : edge,
                    "parameter" : paramAfter,
                    "arcLengthParameterization" : true
                }).origin;
        
        var distBefore = norm(pointBefore - targetPoint);
        var distAfter = norm(pointAfter - targetPoint);
        
        if (distBefore < distToTarget)
        {
            maxParam = midParam;
        }
        else if (distAfter < distToTarget)
        {
            minParam = midParam;
        }
        else
        {
            // We're at a local minimum
            return midParam;
        }
    }
    
    return (minParam + maxParam) / 2.0;
}

/**
 * Convert a path length ratio (0 to 1) to a specific edge and parameter in an edge group.
 * 
 * @param context {Context}: The context
 * @param pathLengthRatio {number}: The ratio (0 to 1) of position along the edge group
 * @param edgeGroup {Query}: The edge group to find the edge in
 * @param totalLength {ValueWithUnits}: The total length of the edge group
 * 
 * @returns {map}: A map with "edge" (Query) and "parameter" (number) fields
 */
function convertPathLengthRatioToEdgeParameter(context is Context, pathLengthRatio is number, edgeGroup is Query, totalLength is ValueWithUnits) returns map
{
    // Clamp the ratio to [0, 1] to handle numerical errors
    pathLengthRatio = max(0.0, min(1.0, pathLengthRatio));
    
    var targetPathLength = totalLength * pathLengthRatio;
    var edgeGroupArray = evaluateQuery(context, edgeGroup);
    var accumulatedLength = 0 * meter;
    
    for (var edge in edgeGroupArray)
    {
        var edgeLength = evLength(context, {
                    "entities" : edge
                });
        
        if (accumulatedLength + edgeLength >= targetPathLength)
        {
            // This is the edge containing the target point
            var lengthIntoEdge = targetPathLength - accumulatedLength;
            var parameterOnEdge = lengthIntoEdge / edgeLength;
            
            // Clamp parameter to [0, 1] to avoid numerical errors
            parameterOnEdge = max(0.0, min(1.0, parameterOnEdge));
            
            return {
                "edge" : edge,
                "parameter" : parameterOnEdge
            };
        }
        
        accumulatedLength = accumulatedLength + edgeLength;
    }
    
    // If we get here, we're at or past the end - return the last edge at parameter 1.0
    var lastEdge = edgeGroupArray[size(edgeGroupArray) - 1];
    return {
        "edge" : lastEdge,
        "parameter" : 1.0
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
