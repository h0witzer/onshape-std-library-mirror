FeatureScript 2752;
import(path : "onshape/std/geometry.fs", version : "2752.0");
import(path : "onshape/std/splitoperationkeeptype.gen.fs", version : "2752.0");
import(path : "onshape/std/evaluate.fs", version : "2752.0");

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

@@ -37,48 +39,58 @@ export const normalizedTubeEnd = defineFeature(function(context is Context, id i
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
        const innerRuledQuery = qCreatedBy(id + "innerRuled", EntityType.BODY);
        const outerRuledQuery = qCreatedBy(id + "outerRuled", EntityType.BODY);
        debug(context, innerRuledQuery, DebugColor.MAGENTA);
        debug(context, outerRuledQuery, DebugColor.GREEN);

        // Determine which side of the split to keep by comparing normals
        const endFacePlane = evFaceTangentPlane(context, { "face" : definition.endFace, "parameter" : vector(0.5, 0.5) });
        const endFaceNormal = endFacePlane.normal;
        const outerKeepType = determineKeepType(context, outerRuledQuery, endFaceNormal);
        const innerKeepType = determineKeepType(context, innerRuledQuery, endFaceNormal);

        // Split part using ruled surfaces and discard pieces outside the keep region
        opSplitPart(context, id + "splitOuter", {
                    targets : definition.framePart,
                    tool : outerRuledQuery,
                    keepType : outerKeepType
                });

        opSplitPart(context, id + "splitInner", {
                    targets : definition.framePart,
                    tool : innerRuledQuery,
                    keepType : innerKeepType
                });

    }, {});

function determineKeepType(context is Context, toolBody is Query, endFaceNormal is Vector) returns SplitOperationKeepType
{
    const toolFace = qOwnedByBody(toolBody, EntityType.FACE);
    const tangentPlane = evFaceTangentPlane(context, { "face" : toolFace, "parameter" : vector(0.5, 0.5) });
    return dot(tangentPlane.normal, endFaceNormal) > 0 ?
            SplitOperationKeepType.KEEP_BACK : SplitOperationKeepType.KEEP_FRONT;
}
