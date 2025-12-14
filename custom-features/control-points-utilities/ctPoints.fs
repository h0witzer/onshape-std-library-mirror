FeatureScript 2559;
import(path : "onshape/std/common.fs", version : "2559.0");
export import (path : "292286148f0044bbd7ef4042", version : "51203dd1425955172d13b65b");//ctPointsBackEndAlt.fs


annotation { "Feature Type Name" : "CT points",
             "Feature Type Description" : "Creates 3d points in space, on curve and on surface",
             "Manipulator Change Function" : "createPointsManipulatorChange",
             "Editing Logic Function" : "createPointsEditingLogic",}
export const createPoints = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        internalCTpointsPredicate(definition);
    }
    {
        doCreatePoints(context, id, definition);
        return;
    });
