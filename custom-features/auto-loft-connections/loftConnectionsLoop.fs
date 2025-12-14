FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/path.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/bridgingCurve.fs", version : "2837.0");

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
 * Two connection methods are available:
 * - Nearest Distance: Uses evDistance to find closest points (default)
 * - Path Length: Uses path length parameterization for uniform distribution
 * 
 * This bidirectional approach guarantees consistent results regardless of input order 
 * (A to B vs B to A) and ensures all vertices are properly connected.
 */

export enum ConnectionMethod
{
    annotation { "Name" : "Nearest distance" }
    NEAREST_DISTANCE,
    annotation { "Name" : "Path length" }
    PATH_LENGTH
}

export const INDEX_BOUNDS =
{
    (unitless) : [0, 0, 1e5]
} as IntegerBoundSpec;

annotation { "Feature Type Name" : "Auto Connection Loop", "Feature Type Description" : "" }
export const loftAutoConnection = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Connection method", "UIHint" : UIHint.SHOW_LABEL }
        definition.connectionMethod is ConnectionMethod;
        
        annotation { "Name" : "Edge 1", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.edge1 is Query;

        annotation { "Name" : "Edge 2", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.edge2 is Query;
        
        annotation { "Name" : "Guide Curve 1", "Filter" : EntityType.EDGE && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.guide0 is Query;
        
        annotation { "Name" : "Guide Curve 2", "Filter" : EntityType.EDGE && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.guide1 is Query;
        
        annotation { "Name" : "Match curvature at Edge 1", "Default" : true }
        definition.matchCurvature1 is boolean;
        
        annotation { "Name" : "Match curvature at Edge 2", "Default" : true }
        definition.matchCurvature2 is boolean;
        
        annotation { "Name" : "Derivative magnitude", "Default" : 1.0 }
        isReal(definition.derivativeMagnitude, POSITIVE_REAL_BOUNDS);
        
        annotation { "Name" : "Use G3 bridging curve guides", "Default" : false }
        definition.useG3Guides is boolean;
  
    }
    {
        var edgeGroup1 = qTangentConnectedEdges(definition.edge1);
        var edgeGroup2 = qTangentConnectedEdges(definition.edge2);
        var guide0 = definition.guide0;
        var guide1 = definition.guide1;

        println("Number of edges 1: " ~ evaluateQueryCount(context, edgeGroup1));
        println("Number of edges 2: " ~ evaluateQueryCount(context, edgeGroup2));
        
        // Generate connections based on selected method
        var loftConnections = [];
        if (definition.connectionMethod == ConnectionMethod.PATH_LENGTH)
        {
            loftConnections = generatePathLengthConnections(context, edgeGroup1, edgeGroup2);
        }
        else
        {
            loftConnections = generateNearestDistanceConnections(context, edgeGroup1, edgeGroup2);
        }

        var loftDerivativeInfo1 = {
            "profileIndex" : 0,
            "magnitude" : definition.derivativeMagnitude,
            "matchCurvature" : definition.matchCurvature1,
            "adjacentFaces" : qAdjacent(edgeGroup1, AdjacencyType.EDGE, EntityType.FACE),
            "userDefinedAdjacentFaces" : false
        };

        var loftDerivativeInfo2 = {
            "profileIndex" : 1,
            "magnitude" : definition.derivativeMagnitude,
            "matchCurvature" : definition.matchCurvature2,
            "adjacentFaces" : qAdjacent(edgeGroup2, AdjacencyType.EDGE, EntityType.FACE),
            "userDefinedAdjacentFaces" : false
        };

        // Generate G3 bridging curve guides if requested
        var guidesArray = [guide0, guide1];
        if (definition.useG3Guides)
        {
            for (var i = 0; i < size(loftConnections); i += 1)
            {
                try silent
                {
                    var connection = loftConnections[i];
                    var bridgeId = id + ("bridge" ~ i);
                    
                    // Get the vertex from connection entities
                    var connectionVertex = qEntityFilter(connection.connectionEntities, EntityType.VERTEX);
                    
                    // Get the edge and parameter for the other side
                    var connectionEdge = connection.connectionEdges[0];
                    var connectionParam = connection.connectionEdgeParameters[0];
                    
                    // Create point at the parameter location on the edge
                    var paramPointId = id + ("paramPoint" ~ i);
                    var paramPoint = evEdgeTangentLine(context, {
                        "edge" : connectionEdge,
                        "parameter" : connectionParam
                    }).origin;
                    opPoint(context, paramPointId, {"point" : paramPoint});
                    var paramVertex = qCreatedBy(paramPointId, EntityType.VERTEX);
                    
                    // Get adjacent faces for each vertex (select first face from each side)
                    var adjacentFaces1 = qNthElement(qAdjacent(connectionVertex, AdjacencyType.VERTEX, EntityType.FACE), 0);
                    var adjacentFaces2 = qNthElement(qAdjacent(paramVertex, AdjacencyType.VERTEX, EntityType.FACE), 0);
                    
                    // Create G3 bridging curve between the vertices
                    bridgingCurve(context, bridgeId, {
                        "side1" : qUnion([connectionVertex, adjacentFaces1]),
                        "match1" : BridgingCurveMatchType.G3,
                        "flip1" : false,
                        "side2" : qUnion([paramVertex, adjacentFaces2]),
                        "match2" : BridgingCurveMatchType.G3,
                        "flip2" : false,
                        "editControlPoints" : false
                    });
                    
                    var bridgeCurve = qCreatedBy(bridgeId, EntityType.EDGE);
                    guidesArray = append(guidesArray, bridgeCurve);
                }
            }
        }

        opLoft(context, id + "loft1", {
                    "profileSubqueries" : [edgeGroup1, edgeGroup2],
                    "guideSubqueries": guidesArray,
                    "connections" : loftConnections,
                    "bodyType" : ToolBodyType.SURFACE,
                    // "trimProfiles" : true,
                    // "trimGuidesByProfiles" : true,
                    "derivativeInfo" : [loftDerivativeInfo1, loftDerivativeInfo2],
                    "loftTopology" : LoftTopology.COLUMNS
                });

    });

/**
 * Generate connections using nearest distance method (evDistance).
 * Two-pass bidirectional approach.
 */
function generateNearestDistanceConnections(context is Context, edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    var endPoints1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
    var adjFaces1 = qAdjacent(edgeGroup1, AdjacencyType.EDGE, EntityType.FACE);
    var smoothEdges1 = qAdjacent(adjFaces1, AdjacencyType.EDGE, EntityType.EDGE)->qEdgeConvexityTypeFilter(EdgeConvexityType.SMOOTH);
    var internalEndPoints1 = qAdjacent(smoothEdges1, AdjacencyType.VERTEX, EntityType.VERTEX);
    var edgeInternalPoints1 = qIntersection([endPoints1, internalEndPoints1]);
    
    debug(context, edgeInternalPoints1, DebugColor.RED);
    
    var loftConnections = [];
    
    // First pass: Create connections from edge group 1 to edge group 2
    for (var i = 0; i < evaluateQueryCount(context, edgeInternalPoints1); i += 1)
    {
        var currentPoint = qNthElement(edgeInternalPoints1, i);
        
        var edgeDist = evDistance(context, {
                    "side0" : currentPoint,
                    "side1" : edgeGroup2
                });
        
        var corresponding = {
                "edge" : qNthElement(edgeGroup2, edgeDist.sides[1].index),
                "parameter" : edgeDist.sides[1].parameter,
            };
        
        corresponding.point = evEdgeTangentLine(context, {
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
    
    // Collect all connection points from the first pass
    var connectionPointsOnEdge2 = [];
    for (var k = 0; k < size(loftConnections); k += 1)
    {
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
        var currentPoint = qNthElement(edgeInternalPoints2, j);
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
            var edgeDist = evDistance(context, {
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
    
    return loftConnections;
}

/**
 * Generate connections using path length parameterization method.
 * Creates connections at unified set of path length ratios.
 */
function generatePathLengthConnections(context is Context, edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Construct ordered paths from the edge groups
    var path1 = constructPath(context, edgeGroup1);
    var path2 = constructPath(context, edgeGroup2);
    
    var totalLength1 = evPathLength(context, path1);
    var totalLength2 = evPathLength(context, path2);
    
    // Get the internal vertices for each path
    var endPoints1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
    var adjFaces1 = qAdjacent(edgeGroup1, AdjacencyType.EDGE, EntityType.FACE);
    var smoothEdges1 = qAdjacent(adjFaces1, AdjacencyType.EDGE, EntityType.EDGE)->qEdgeConvexityTypeFilter(EdgeConvexityType.SMOOTH);
    var internalEndPoints1 = qAdjacent(smoothEdges1, AdjacencyType.VERTEX, EntityType.VERTEX);
    var edgeInternalPoints1 = qIntersection([endPoints1, internalEndPoints1]);
    
    debug(context, edgeInternalPoints1, DebugColor.RED);
    
    // Calculate path length ratios for all vertices in both paths
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
    
    // Merge and sort all unique path length ratios
    var allRatios = concatenateArrays(pathRatios1, pathRatios2);
    allRatios = removeDuplicateRatios(allRatios);
    allRatios = sortRatios(allRatios);
    
    println("Total unique path ratios: " ~ size(allRatios));
    
    // Create connections at all path length ratios
    var loftConnections = [];
    for (var ratio in allRatios)
    {
        // Convert ratio to edge+parameter on both paths using evPathTangentLines
        var result1 = evPathTangentLines(context, path1, [ratio]);
        var result2 = evPathTangentLines(context, path2, [ratio]);
        
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
    
    return loftConnections;
}

/**
 * Remove duplicate ratios from an array (within tolerance).
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
 * Sort an array of numbers using bubble sort.
 */
function sortRatios(arr is array) returns array
{
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
 */
function calculatePathLengthRatioForVertex(context is Context, vertex is Query, path is Path, totalLength is ValueWithUnits) returns number
{
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
