FeatureScript 2837;

import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "fd0be504205eef1f9385b57b", version : "30f65e1d900f2b1c5cc3abb6");


annotation { "Feature Type Name" : "Tween Two Curves",
        "Feature Type Description" : "Interpolates between two curves or multi-segment paths. Handles domain matching for paths with different numbers of segments.",
        "UIHint" : "NO_PREVIEW_PROVIDED" }
export const tweenTwoCurves = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "First curve", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.curve1 is Query;
        annotation { "Name" : "Second curve", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.curve2 is Query;
        annotation { "Name" : "Tween fraction" }
        isReal(definition.fraction, TWEEN_FRACTION_BOUNDS);
    }
    {
        tweenCurves(context, id, definition.curve1, definition.curve2, definition.fraction);
    });

