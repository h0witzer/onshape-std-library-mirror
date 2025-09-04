FeatureScript 2752;
import(path : "onshape/std/geometry.fs", version : "2752.0");

annotation { "Feature Type Name" : "Normalized Tube End" }
export const normalizedTubeEnd = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Select the frame part (tube)
        annotation { "Name" : "Frame part", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        definition.framePart is Query;

        // Select the end face (could be planar, coped, mitered, etc.)
        annotation { "Name" : "End face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.endFace is Query;

        // Select the inner cylindrical face
        annotation { "Name" : "Inner face (inside surface)", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.innerFace is Query;

        // Select the outer cylindrical face
        annotation { "Name" : "Outer face (outside surface)", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.outerFace is Query;
    }
    {
        // Get all edges of the end face
        const endEdges = qAdjacent(definition.endFace, AdjacencyType.EDGE);

        // Inner edge = edge(s) adjacent to both endFace and innerFace
        const innerEdge = qIntersection([endEdges, qAdjacent(definition.innerFace, AdjacencyType.EDGE)]);

        // Outer edge = edge(s) adjacent to both endFace and outerFace
        const outerEdge = qIntersection([endEdges, qAdjacent(definition.outerFace, AdjacencyType.EDGE)]);

        // Compute wall thickness using measureDistance
        const distanceResult = measureDistance(context, {
                    entities : qUnion([definition.innerFace, definition.outerFace])
                });
        const thickness = distanceResult.distance;
        const offset = 1.5 * thickness;
        debug(context, thickness);

        // Create ruled surfaces normal to tube faces
        ruledSurface(context, id + "innerRuled", {
                    surfaceOperationType : NewSurfaceOperationType.NEW,
                    edges : innerEdge,
                    ruledType : RuledSurfaceInterfaceType.NORMAL,
                    referenceFaces : definition.innerFace,
                    distance : offset,
                    oppositeDirection : true
                });

        ruledSurface(context, id + "outerRuled", {
                    surfaceOperationType : NewSurfaceOperationType.NEW,
                    edges : outerEdge,
                    ruledType : RuledSurfaceInterfaceType.NORMAL,
                    referenceFaces : definition.outerFace,
                    distance : offset,
                    oppositeDirection : true
                });

        // Debug highlight ruled surfaces
        const innerRuledQuery = qCreatedBy(id + "innerRuled");
        const outerRuledQuery = qCreatedBy(id + "outerRuled");
        debug(context, innerRuledQuery, DebugColor.MAGENTA);
        debug(context, outerRuledQuery, DebugColor.GREEN);

        const innerTool = qCreatedBy(id + "innerRuled");
        const outerTool = qCreatedBy(id + "outerRuled");

        // Split part using ruled surfaces
        opSplitPart(context, id + "splitOuter", {
                    targets : definition.framePart,
                    tool : outerRuledQuery

                });

        opSplitPart(context, id + "splitInner", {
                    targets : definition.framePart,
                    tool : innerRuledQuery
                });
                
        // Delete extra bodies created by split

    }, {});
