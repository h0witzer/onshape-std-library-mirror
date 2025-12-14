FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

import(path : "12312312345abcabcabcdeff/a6fb0cf8f4a5191f6485f2f7/b2077a52c9d520f2bda0b236", version : "2c109a09f2a3e5d28a9f523f");


export const INDEX_BOUNDS =
{
            (unitless) : [0, 0, 1e5]
        } as IntegerBoundSpec;

annotation { "Feature Type Name" : "Auto Connection", "Feature Type Description" : "" }
export const loftAutoConnection = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edge 1", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.edge0 is Query;

        annotation { "Name" : "Edge 2", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.edge1 is Query;
    }
    {
        var edgeGroup0 = qTangentConnectedEdges(definition.edge0);
        var edgeGroup1 = qTangentConnectedEdges(definition.edge1);

        var edgeInfo = {};
        var edgeDist;
        var endPoints1 = qAdjacent(edgeGroup0, AdjacencyType.VERTEX, EntityType.VERTEX);

        println("Number of edges 1: " ~ evaluateQueryCount(context, edgeGroup0));
        println("Number of edges 2: " ~ evaluateQueryCount(context, edgeGroup1));

        debug(context, endPoints1, DebugColor.RED);


        var point2 = qNthElement(endPoints1, 2);
        var point3 = qNthElement(endPoints1, 3);
        var point4 = qNthElement(endPoints1, 4);


        var corresponding2 = getCorresponding(context, id + "corresponding2", point2, edgeGroup1);
        var corresponding3 = getCorresponding(context, id + "corresponding3", point3, edgeGroup1);
        var corresponding4 = getCorresponding(context, id + "corresponding4", point4, edgeGroup1);

        var connectionMap2 = { "connectionEntities" : qUnion([point2, corresponding2.edge]), "connectionEdges" : [corresponding2.edge], "connectionEdgeParameters" : [corresponding2.parameter] };
        var connectionMap3 = { "connectionEntities" : qUnion([point2, corresponding3.edge]), "connectionEdges" : [corresponding3.edge], "connectionEdgeParameters" : [corresponding3.parameter] };
        var connectionMap4 = { "connectionEntities" : qUnion([point2, corresponding4.edge]), "connectionEdges" : [corresponding4.edge], "connectionEdgeParameters" : [corresponding4.parameter] };

        // adding points 0 and 1
        // var point0 = qNthElement(endPoints1, 0);
        // var point1 = qNthElement(endPoints1, 1);
        // var corresponding0 = getCorresponding(context, id + "corresponding0", point0, edgeGroup1);
        // var corresponding1 = getCorresponding(context, id + "corresponding1", point1, edgeGroup1);
        // var connectionMap0 = { "connectionEntities" : qUnion([point0, corresponding0.edge]), "connectionEdges" : [corresponding0.edge], "connectionEdgeParameters" : [corresponding0.parameter] };
        // var connectionMap1 = { "connectionEntities" : qUnion([point1, corresponding1.edge]), "connectionEdges" : [corresponding1.edge], "connectionEdgeParameters" : [corresponding1.parameter] };


        var loftConnections = [
            // connectionMap0,
            // connectionMap1,
            connectionMap2,
            connectionMap3, 
            connectionMap4
        ];

        var loftDerivativeInfo1 = {
            "profileIndex" : 0,
            "magnitude" : 1,
            "matchCurvature" : false,
            "adjacentFaces" : qAdjacent(edgeGroup0, AdjacencyType.EDGE, EntityType.FACE)
        };

        var loftDerivativeInfo2 = {
            "profileIndex" : 1,
            "magnitude" : 1,
            "matchCurvature" : false,
            "adjacentFaces" : qAdjacent(edgeGroup1, AdjacencyType.EDGE, EntityType.FACE)
        };


        opLoft(context, id + "loft1", {
                    "profileSubqueries" : [edgeGroup0, edgeGroup1],
                    "connections" : loftConnections,
                    "bodyType" : ToolBodyType.SURFACE,
                    "trimProfiles" : true,
                    "trimGuidesByProfiles" : true,
                    "derivativeInfo" : [loftDerivativeInfo1, loftDerivativeInfo2],
                    "loftTopology" : LoftTopology.COLUMNS
                });

    });

function getCorresponding(context, id is Id, currentPoint is Query, edgeGroup1 is Query) returns map
{
    var edgeDist = evDistance(context, {
            "side0" : currentPoint,
            "side1" : edgeGroup1
        });

    var corresponding = {
        "edge" : qNthElement(edgeGroup1, edgeDist.sides[1].index),
        "parameter" : edgeDist.sides[1].parameter,
    };

    corresponding.point = evEdgeTangentLine(context, {
                    "edge" : corresponding.edge,
                    "parameter" : corresponding.parameter
                }).origin;

    opPoint(context, id + "point1", {
                "point" : corresponding.point
            });

    corresponding.queryPoint = qCreatedBy(id + "point1", EntityType.BODY);

    addDebugEntities(context, currentPoint, DebugColor.GREEN);
    addDebugEntities(context, corresponding.queryPoint, DebugColor.MAGENTA);

    // opDeleteBodies(context, id + "deleteBodies1", {
    //         "entities" : showPoint
    // });

    return corresponding;
}


// sample outputs from loft feature

// "connections" : [{ "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]],


// "connections" : [{ "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]]

/* "matchConnections" : true,
   "connections" : [
   { "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]},
   { "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]},
   { "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]},
   { "connectionEntities" : qUnion([query, query]), "connectionEdgeQueries" : qUnion([query]), "connectionEdgeParameters" : [0.5]}
   ]


 */
