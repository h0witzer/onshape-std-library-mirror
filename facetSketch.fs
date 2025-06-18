FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");
import(path : "onshape/std/geometry.fs", version : "2679.0");

annotation { "Feature Type Name" : "Facet Sketch" }
export const facetSketch = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sketches", "Filter" : SketchObject.YES }
        definition.sketch is FeatureList;

        annotation { "Name" : "Amount of facets per curve" }
        isInteger(definition.facetNumber, POSITIVE_COUNT_BOUNDS);
    }
    {
        if (size(keys(definition.sketch)) == 0)
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["sketch"]);
        var faceter = newFaceter(id, definition.facetNumber);
        for (var key in keys(definition.sketch))
        {
            addEntities(faceter, qBodyType(qConstructionFilter(qCreatedBy(key, EntityType.EDGE), ConstructionObject.NO), BodyType.WIRE));
        }
        facet(context, faceter);
    });

export type Faceter typecheck canBeFaceter;

export predicate canBeFaceter(value)
{
    value is box;
    value[].toConvert is array;
    value[].id is Id;
    value[].facetNumber is number;
    for (var toConvert in value[].toConvert)
    {
        toConvert is map;
        toConvert.entities is Query;
        toConvert.id is Id;
    }
}

export function newFaceter(id is Id, facetNumber is number) returns Faceter
{
    var out is Faceter = new box({
                "toConvert" : [],
                "id" : id,
                "facetNumber" : facetNumber,
                "no" : 0
            }) as Faceter;
    return out;
}

export function addEntities(faceter is Faceter, entities is Query) returns Query
{
    var id = faceter[].id;
    faceter[].no += 1;
    faceter[].toConvert = append(faceter[].toConvert, {
                "entities" : entities,
                "id" : id + faceter[].no
            });
    return qCreatedBy(id + faceter[].no);
}

export function facet(context is Context, faceter is Faceter)
{
    var facetNum = faceter[].facetNumber;
    for (var toConvert in faceter[].toConvert)
    {
        var topId = toConvert.id;
        var entities = evaluateQuery(context, toConvert.entities);
        var skPlane = evOwnerSketchPlane(context, {
                "entity" : entities[0]
            });
        var sketch = newSketchOnPlane(context, topId, {
                "sketchPlane" : skPlane
            });
        for (var i = 0; i < size(entities); i += 1)
        {
            var edge = entities[i];
            try silent
            {
                evLine(context, {
                            "edge" : edge
                        });
                var start = worldToPlane(skPlane, evVertexPoint(context, {
                            "vertex" : qNthElement(qAdjacent(edge, AdjacencyType.VERTEX), 0)
                        }));
                var end = worldToPlane(skPlane, evVertexPoint(context, {
                            "vertex" : qNthElement(qAdjacent(edge, AdjacencyType.VERTEX), 1)
                        }));
                skLineSegment(sketch, "line" ~ i, {
                            "start" : start,
                            "end" : end
                        });
            }
            catch
            {
                // Not a line
                var arr = [];
                for (var j = 0; j <= facetNum; j += 1)
                {
                    arr = append(arr, j / facetNum);
                }
                var positions = evEdgeTangentLines(context, {
                        "edge" : edge,
                        "parameters" : arr
                    });
                for (var j = 0; j < size(positions); j += 1)
                {
                    positions[j] = worldToPlane(skPlane, positions[j].origin);
                }
                skPolyline(sketch, "line" ~ i, {
                            "points" : positions
                        });
            }
        }
        skSolve(sketch);
    }
}
