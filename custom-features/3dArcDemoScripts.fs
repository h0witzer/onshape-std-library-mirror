FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");

export import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/feature.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "97730412fb61f53dcd526c08", version : "e7466f17a5e8f9cda49e262e");

/**
 * Simple feature creating a 3D arc through three selected vertices.
 */
annotation { "Feature Type Name" : "3D arc" }
export const arc3dFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Start vertex", "Filter" : EntityType.VERTEX }
        definition.startVertex is Query;
        annotation { "Name" : "Mid vertex", "Filter" : EntityType.VERTEX }
        definition.midVertex is Query;
        annotation { "Name" : "End vertex", "Filter" : EntityType.VERTEX }
        definition.endVertex is Query;
    }
    {
        const startP = evVertexPoint(context, { "vertex" : definition.startVertex });
        const midP = evVertexPoint(context, { "vertex" : definition.midVertex });
        const endP = evVertexPoint(context, { "vertex" : definition.endVertex });
        opArc3d(context, id, { "start" : startP, "mid" : midP, "end" : endP });
    },
    {});
    

/**
 * Simple feature creating a tangent 3D arc.
 */
annotation { "Feature Type Name" : "Tangent 3D arc" }
export const tangentArc3dFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Start vertex", "Filter" : EntityType.VERTEX }
        definition.startVertex is Query;
        annotation { "Name" : "Tangent edge", "Filter" : EntityType.EDGE }
        definition.tangentEdge is Query;
        annotation { "Name" : "End vertex", "Filter" : EntityType.VERTEX }
        definition.endVertex is Query;
    }
    {
        const startP = evVertexPoint(context, { "vertex" : definition.startVertex });
        const endP = evVertexPoint(context, { "vertex" : definition.endVertex });
        opTangentArc3d(context, id,
            { "start" : startP, "tangentEdge" : definition.tangentEdge, "end" : endP });
    },
    {});
    
/**
 * Demo feature: create a chain of tangent arcs through a list of points.
 * Points can be reordered in the array to change the path.
 */
annotation { "Feature Type Name" : "Tangent arc chain" }
export const tangentArcChainFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Points", "Item name" : "point", "Item label template" : "Point #index" }
        definition.points is array;
        for (var p in definition.points)
        {
            annotation { "Name" : "Vertex", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
            p.vertex is Query;
        }
    }
    {
        // Convert selected vertices to world-space points
        var vertexPoints = [];
        for (var pointDefinition in definition.points)
            vertexPoints = append(vertexPoints, evVertexPoint(context, { "vertex" : pointDefinition.vertex }));
        const count = size(vertexPoints);
        if (count < 3)
            return; // Need at least three points for the initial arc

        // First arc defined by three points
        var previousWire = opArc3d(context, id + "arc0",
                { "start" : vertexPoints[0], "mid" : vertexPoints[1], "end" : vertexPoints[2] });
        debug(context, previousWire, DebugColor.YELLOW);

        // Create tangent arcs for each additional point
        for (var index = 3; index < count; index += 1)
        {
            const startPoint = vertexPoints[index - 1];
            const endPoint = vertexPoints[index];

            const edge = qOwnedByBody(previousWire, EntityType.EDGE);

            previousWire = opTangentArc3d(context, id +(index - 2),
                    { "start" : startPoint, "tangentEdge" : edge, "end" : endPoint });
        }
    },
    {});
