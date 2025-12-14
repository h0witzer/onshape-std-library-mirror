FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

import(path : "12312312345abcabcabcdeff/a6fb0cf8f4a5191f6485f2f7/b2077a52c9d520f2bda0b236", version : "2c109a09f2a3e5d28a9f523f");

/**
 * Auto Connection Loop: Creates bidirectional loft connections between two edge groups.
 * 
 * This feature performs a two-pass connection algorithm to ensure all internal vertices 
 * find partners for the loft operation:
 * 
 * 1. First pass: Creates connections from Edge Group 1 vertices to Edge Group 2
 * 2. Second pass: Creates connections from unmatched Edge Group 2 vertices to Edge Group 1
 * 
 * This bidirectional approach guarantees consistent results regardless of input order 
 * (A to B vs B to A) and ensures all vertices are properly connected.
 */

export const INDEX_BOUNDS =
{
    (unitless) : [0, 0, 1e5]
} as IntegerBoundSpec;

annotation { "Feature Type Name" : "Auto Connection Loop", "Feature Type Description" : "" }
export const loftAutoConnection = defineFeature(function(context is Context, id is Id, definition is map)
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

        var edgeInfo = {};
        var edgeDist;
        var endPoints1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);

        println("Number of edges 1: " ~ evaluateQueryCount(context, edgeGroup1));
        println("Number of edges 2: " ~ evaluateQueryCount(context, edgeGroup2));
        
        // get the internal points for the profile. we don't want the edgeGroup endpoints
        
        var adjFaces1 = qAdjacent(edgeGroup1, AdjacencyType.EDGE, EntityType.FACE);
        var smoothEdges1 = qAdjacent(adjFaces1, AdjacencyType.EDGE, EntityType.EDGE)->qEdgeConvexityTypeFilter(EdgeConvexityType.SMOOTH);
        var internalEndPoints1 = qAdjacent(smoothEdges1, AdjacencyType.VERTEX, EntityType.VERTEX);
        
        var edgeInternalPoints1 = qIntersection([endPoints1, internalEndPoints1]);
        
        debug(context, edgeInternalPoints1, DebugColor.RED);

        var loftConnections = [];

        // First pass: Create connections from edge group 1 to edge group 2
        for (var i = 0; i < evaluateQueryCount(context, edgeInternalPoints1); i += 1)
        {
            var currentPoint = qNthElement(edgeInternalPoints1, i); // obtain each point from edge1

            edgeDist = evDistance(context, { // evaluate the distance to edgeGroup2 and obtain closest edge and parameter
                        "side0" : currentPoint,
                        "side1" : edgeGroup2
                    });

            var corresponding = { //creates a map that contains the corresponding edge and parameter info
                    "edge" : qNthElement(edgeGroup2, edgeDist.sides[1].index),
                    "parameter" : edgeDist.sides[1].parameter,
                };

            corresponding.point = evEdgeTangentLine(context, { // grabs the point to plot with a spline as a sanity check
                            "edge" : corresponding.edge,
                            "parameter" : corresponding.parameter
                        }).origin; 


            var connectionMap = {
                "connectionEntities" : qUnion([currentPoint, corresponding.edge]),
                "connectionEdges" : [corresponding.edge],
                "connectionEdgeParameters" : [corresponding.parameter]};
            
            loftConnections = append(loftConnections, connectionMap);

            println(connectionMap.connectionEdgeParameters); 
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
                        "parameter" : parameter2
                    }).origin;
            connectionPointsOnEdge2 = append(connectionPointsOnEdge2, connectionPointOnEdge2);
        }
        
        // Second pass: Create connections from edge group 2 to edge group 1 for unmatched vertices
        var allVertices2 = qAdjacent(edgeGroup2, AdjacencyType.VERTEX, EntityType.VERTEX);
        var adjFaces2 = qAdjacent(edgeGroup2, AdjacencyType.EDGE, EntityType.FACE);
        var smoothEdges2 = qAdjacent(adjFaces2, AdjacencyType.EDGE, EntityType.EDGE)->qEdgeConvexityTypeFilter(EdgeConvexityType.SMOOTH);
        var internalEndPoints2 = qAdjacent(smoothEdges2, AdjacencyType.VERTEX, EntityType.VERTEX);
        var edgeInternalPoints2 = qIntersection([allVertices2, internalEndPoints2]);
        
        debug(context, edgeInternalPoints2, DebugColor.BLUE);
        
        for (var j = 0; j < evaluateQueryCount(context, edgeInternalPoints2); j += 1)
        {
            var currentPoint = qNthElement(edgeInternalPoints2, j); // obtain each point from edge2
            
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
                // Find closest point on edge group 1
                edgeDist = evDistance(context, {
                            "side0" : currentPoint,
                            "side1" : edgeGroup1
                        });
                
                var correspondingOnEdge1 = {
                        "edge" : qNthElement(edgeGroup1, edgeDist.sides[1].index),
                        "parameter" : edgeDist.sides[1].parameter,
                    };
                
                correspondingOnEdge1.point = evEdgeTangentLine(context, {
                                "edge" : correspondingOnEdge1.edge,
                                "parameter" : correspondingOnEdge1.parameter
                            }).origin;
                
                var connectionMap = {
                    "connectionEntities" : qUnion([currentPoint, correspondingOnEdge1.edge]),
                    "connectionEdges" : [correspondingOnEdge1.edge],
                    "connectionEdgeParameters" : [correspondingOnEdge1.parameter]};
                
                loftConnections = append(loftConnections, connectionMap);
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
