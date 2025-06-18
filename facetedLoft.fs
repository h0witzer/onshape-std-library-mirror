FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");

import(path : "onshape/std/geometry.fs", version : "2679.0");
import(path : "9301eaa235137f59d035d714", version : "9b77626eec64e1cb8953e247"); //facetSketch.fs


annotation { "Feature Type Name" : "Faceted Loft" }
export const facetedLoft = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sketch 1", "Filter" : SketchObject.YES, "MaxNumberOfPicks" : 1 }
        definition.sketch1 is FeatureList;

        annotation { "Name" : "Sketch 2", "Filter" : SketchObject.YES, "MaxNumberOfPicks" : 1 }
        definition.sketch2 is FeatureList;

        annotation { "Name" : "Amount of facets per curve" }
        isInteger(definition.facetNumber, POSITIVE_COUNT_BOUNDS);

        annotation { "Name" : "Replace non-planar faces with triangles" }
        definition.fixNonPlanar is boolean;
    }
    {
        if (size(keys(definition.sketch1)) == 0)
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["sketch1"]);
        if (size(keys(definition.sketch2)) == 0)
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["sketch2"]);
        var sketch1 = qBodyType(qConstructionFilter(qCreatedBy(keys(definition.sketch1)[0], EntityType.EDGE), ConstructionObject.NO), BodyType.WIRE);
        var sketch2 = qBodyType(qConstructionFilter(qCreatedBy(keys(definition.sketch2)[0], EntityType.EDGE), ConstructionObject.NO), BodyType.WIRE);

        var faceter = newFaceter(id, definition.facetNumber);
        var sk1 = qBodyType(qEntityFilter(addEntities(faceter, sketch1), EntityType.EDGE), BodyType.WIRE);
        var sk2 = qBodyType(qEntityFilter(addEntities(faceter, sketch2), EntityType.EDGE), BodyType.WIRE);
        facet(context, faceter);
        opLoft(context, id + "loft", {
                    "profileSubqueries" : [sk1, sk2],
                    "bodyType" : ToolBodyType.SURFACE
                });
        opDeleteBodies(context, id + "delete", {
                    "entities" : qSubtraction(qCreatedBy(id), qCreatedBy(id + "loft"))
                });
        var allFacesSize = size(evaluateQuery(context, qCreatedBy(id, EntityType.FACE)));
        var nonPlanarFaces = qSubtraction(qCreatedBy(id, EntityType.FACE), qGeometry(qEverything(EntityType.FACE), GeometryType.PLANE));
        var nonPlanarFacesSize = size(evaluateQuery(context, nonPlanarFaces));
        if (nonPlanarFacesSize > 0 && nonPlanarFacesSize == allFacesSize)
        {
            if (definition.fixNonPlanar)
                fixNonPlanar(context, id + "fix", nonPlanarFaces);
            else
            {
                addDebugEntities(context, qUnion([nonPlanarFaces, qAdjacent(nonPlanarFaces, AdjacencyType.EDGE)]));

                //addDebugEntities(context, qUnion([nonPlanarFaces, qEdgeAdjacent(nonPlanarFaces, EntityType.EDGE)]));
                opDeleteBodies(context, id + "deleteFace", {
                            "entities" : nonPlanarFaces
                        });
                reportFeatureError(context, id, "There were no planar faces generated.");
            }
        }
        else if (nonPlanarFacesSize > 0)
        {
            if (definition.fixNonPlanar)
                fixNonPlanar(context, id + "fix", nonPlanarFaces);
            else
            {
                addDebugEntities(context, qUnion([nonPlanarFaces, qAdjacent(nonPlanarFaces, AdjacencyType.EDGE)]));
                opDeleteFace(context, id + "deleteFace", {
                            "deleteFaces" : nonPlanarFaces,
                            "includeFillet" : false,
                            "capVoid" : false,
                            "leaveOpen" : true
                        });
                reportFeatureWarning(context, id, "There are " ~ nonPlanarFacesSize ~ " non-planar faces. They will be deleted.");
            }
        }
    });

function fixNonPlanar(context is Context, id is Id, faces is Query)
{
    var evFaces = evaluateQuery(context, faces);

    var partsToBoolean = [qUnion(evaluateQuery(context, qOwnerBody(faces)))];
    var i = 0;
    for (var face in evFaces)
    {
        var edges = face->qAdjacent(AdjacencyType.EDGE);
        var conn_edges = edges->qEdgeTopologyFilter(EdgeTopology.TWO_SIDED);

        // undefined or 0 or 1
        var conn_edge_1;
        for (var x = 0; x < 2; x += 1)
            if (context->evaluateQuery(qIntersection([edges->qNthElement(x), conn_edges])) != [])
            {
                conn_edge_1 = x;
                break;
            }

        if (conn_edge_1 != undefined)
        {

        }

        createTriangle(context, id + i + "loft1", qNthElement(qAdjacent(face, AdjacencyType.EDGE), 0), qNthElement(qAdjacent(face, AdjacencyType.EDGE), 1));
        createTriangle(context, id + i + "loft2", qNthElement(qAdjacent(face, AdjacencyType.EDGE), 2), qNthElement(qAdjacent(face, AdjacencyType.EDGE), 3));
        partsToBoolean = append(partsToBoolean, qUnion([qCreatedBy(id + i + "loft1", EntityType.BODY), qCreatedBy(id + i + "loft2", EntityType.BODY)]));
        i += 1;
    }
    i = 0;
    for (var face in evFaces)
    {
        try
        {
            opDeleteFace(context, id + "delete" + i + "face", {
                        "deleteFaces" : face,
                        "includeFillet" : false,
                        "capVoid" : false,
                        "leaveOpen" : true
                    });
        }
        catch
        {
            // opDeleteBodies(context, id + "delete" + i + "body", {
            //             "entities" : face
            //         });
        }
        i += 1;
    }

    opBoolean(context, id + "boolean", {
                "localizedInFaces" : false,
                "tools" : qUnion(partsToBoolean),
                "operationType" : BooleanOperationType.UNION
            });
    // }



}


function createTriangle(context is Context, id is Id, edge1 is Query, edge2 is Query)
{
    var points = [];
    for (var point in evaluateQuery(context, qEntityFilter(qAdjacent(qUnion([edge1, edge2]), AdjacencyType.VERTEX), EntityType.VERTEX)))
    {
        println(point);
        debug(context, point, DebugColor.CYAN);
        points = append(points, evVertexPoint(context, {
                        "vertex" : point
                    }));
    }
    const normal = cross(points[2] - points[0], points[1] - points[0]);
    const triPlane = plane(points[0], normalize(normal), normalize(points[1] - points[0]));
    for (var i = 0; i < size(points); i += 1)
    {
        points[i] = worldToPlane(triPlane, points[i]);
    }
    points = append(points, points[0]);
    var sketch = newSketchOnPlane(context, id + "sketch", {
            "sketchPlane" : triPlane
        });
    skPolyline(sketch, "polyline", {
                "points" : points
            });
    skSolve(sketch);
    opExtractSurface(context, id + "extract", {
                "faces" : qCreatedBy(id + "sketch", EntityType.FACE)
            });
    opDeleteBodies(context, id + "delete", {
                "entities" : qCreatedBy(id + "sketch")
            });
}


















