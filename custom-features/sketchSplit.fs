FeatureScript 2770;
import(path : "onshape/std/common.fs", version : "2770.0");

annotation { "Feature Type Name" : "Split with Sketch", "Feature Type Description" : "Use sketches to split entities" }
export const splitSketch= defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Parts to Split", "Filter" : EntityType.BODY}
        definition.partsToSplit is Query;

        annotation { "Name" : "Sketch Edges to Split with", "Filter" : EntityType.EDGE && ConstructionObject.NO}
        definition.sketchEdges is Query;

        annotation { "Name" : "Keep Both Sides", "Default" : false, "UIHint": UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.keepBothSides is boolean;

        if (!definition.keepBothSides)
        {
            annotation { "Name" : "Opposite Direction", "Default" : true, "UIHint" : UIHint.OPPOSITE_DIRECTION }
            definition.oppositeDir is boolean;

        }
        
        annotation { "Name" : "Extend Lines", "Default": false }
        definition.extendLines is boolean;
        

    }
    {
        opExtrude(context, id + "extrude1", {
                    "entities" : definition.sketchEdges,
                    "direction" : evOwnerSketchPlane(context, { "entity" : definition.sketchEdges}).normal,
                    "endBound" : BoundingType.THROUGH_ALL,
                    "startBound" : BoundingType.THROUGH_ALL
                });


        opSplitPart(context, id + "splitPart1", {
                    "targets" : definition.partsToSplit,
                    "tool" : qCreatedBy(id + "extrude1", EntityType.BODY),
                    "keepType" : definition.keepBothSides ? SplitOperationKeepType.KEEP_ALL : (definition.oppositeDir ? SplitOperationKeepType.KEEP_FRONT : SplitOperationKeepType.KEEP_BACK),
                    "useTrimmed": !definition.extendLines
                });

    });
