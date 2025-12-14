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

        var loftConnections = [];

        // First pass: Create connections from edge group 1 to edge group 2 using path length
        for (var i = 0; i < evaluateQueryCount(context, edgeInternalPoints1); i += 1)
        {
            var currentPoint = qNthElement(edgeInternalPoints1, i);
            
            // Calculate the path length position of this vertex along edge group 1
            var pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, edgeGroup1, totalLength1);
            
            println("Vertex " ~ i ~ " from edge1 at path length ratio: " ~ pathLengthRatio);
            
            // Convert the path length ratio to a specific edge and parameter on edge group 2
            var corresponding = convertPathLengthRatioToEdgeParameter(context, pathLengthRatio, edgeGroup2, totalLength2);

            corresponding.point = evEdgeTangentLine(context, {
                            "edge" : corresponding.edge,
                            "parameter" : corresponding.parameter,
                            "arcLengthParameterization" : true
                        }).origin; 

            var connectionMap = {
                "connectionEntities" : qUnion([currentPoint, corresponding.edge]),
                "connectionEdges" : [corresponding.edge],
                "connectionEdgeParameters" : [corresponding.parameter]};
            
            loftConnections = append(loftConnections, connectionMap);

            println("Connection parameter: " ~ connectionMap.connectionEdgeParameters); 
        }
        
        // Collect all connection points from the first pass to check against in the second pass
        var connectionPointsOnEdge2 = [];
        for (var k = 0; k < size(loftConnections); k += 1)
        {
            // Get the point on edge2 from this connection
            var edge2InConnection = loftConnections[k].connectionEdges[0];
            var parameter2 = loftConnections[k].connectionEdgeParameters[0];
            var connectionPointOnEdge2 = evEdgeTangentLine(context, {
                        "edge" : edge2InConnection,
                        "parameter" : parameter2,
                        "arcLengthParameterization" : true
                    }).origin;
            connectionPointsOnEdge2 = append(connectionPointsOnEdge2, connectionPointOnEdge2);
        }
        
        // Second pass: Create connections from edge group 2 to edge group 1 for unmatched vertices using path length
        var allVertices2 = qAdjacent(edgeGroup2, AdjacencyType.VERTEX, EntityType.VERTEX);
        var adjFaces2 = qAdjacent(edgeGroup2, AdjacencyType.EDGE, EntityType.FACE);
        var smoothEdges2 = qAdjacent(adjFaces2, AdjacencyType.EDGE, EntityType.EDGE)->qEdgeConvexityTypeFilter(EdgeConvexityType.SMOOTH);
        var internalEndPoints2 = qAdjacent(smoothEdges2, AdjacencyType.VERTEX, EntityType.VERTEX);
        var edgeInternalPoints2 = qIntersection([allVertices2, internalEndPoints2]);
        
        debug(context, edgeInternalPoints2, DebugColor.BLUE);
        
        for (var j = 0; j < evaluateQueryCount(context, edgeInternalPoints2); j += 1)
        {
            var currentPoint = qNthElement(edgeInternalPoints2, j);
            
            // Get the position of this vertex
            var currentPointPosition = evVertexPoint(context, {
                        "vertex" : currentPoint
                    });
            
            // Check if this vertex is close to any connection point from the first pass
            var isAlreadyConnected = false;
            for (var connectionPoint in connectionPointsOnEdge2)
            {
                if (norm(currentPointPosition - connectionPoint) < TOLERANCE.zeroLength * meter)
                {
                    isAlreadyConnected = true;
                    break;
                }
            }
            
            // Only add this connection if the vertex wasn't already connected
            if (!isAlreadyConnected)
            {
                // Calculate the path length position of this vertex along edge group 2
                var pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, edgeGroup2, totalLength2);
                
                println("Vertex " ~ j ~ " from edge2 at path length ratio: " ~ pathLengthRatio);
                
                // Convert the path length ratio to a specific edge and parameter on edge group 1
                var correspondingOnEdge1 = convertPathLengthRatioToEdgeParameter(context, pathLengthRatio, edgeGroup1, totalLength1);
                
                correspondingOnEdge1.point = evEdgeTangentLine(context, {
                                "edge" : correspondingOnEdge1.edge,
                                "parameter" : correspondingOnEdge1.parameter,
                                "arcLengthParameterization" : true
                            }).origin;
                
                var connectionMap = {
                    "connectionEntities" : qUnion([currentPoint, correspondingOnEdge1.edge]),
                    "connectionEdges" : [correspondingOnEdge1.edge],
                    "connectionEdgeParameters" : [correspondingOnEdge1.parameter]};
                
                loftConnections = append(loftConnections, connectionMap);
                
                println("Added reverse connection at parameter: " ~ correspondingOnEdge1.parameter);
            }
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
    // Find which edge in the group contains this vertex or is closest to it
    var edgeDist = evDistance(context, {
                "side0" : vertex,
                "side1" : edgeGroup
            });
    
    var closestEdge = qNthElement(edgeGroup, edgeDist.sides[1].index);
    var edgeParameter = edgeDist.sides[1].parameter;
    
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
    
    // Calculate the length along the closest edge to the vertex
    var closestEdgeLength = evLength(context, {
                "entities" : closestEdge
            });
    var lengthAlongEdge = closestEdgeLength * edgeParameter;
    
    // Total path length to the vertex
    var totalPathLengthToVertex = pathLengthBeforeEdge + lengthAlongEdge;
    
    // Return as a ratio
    var ratio = totalPathLengthToVertex / totalLength;
    
    return ratio;
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
