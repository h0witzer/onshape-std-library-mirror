FeatureScript 2559;
import(path : "onshape/std/common.fs", version : "2559.0");

export import(path : "292286148f0044bbd7ef4042", version : "51203dd1425955172d13b65b");//ctPointsBackEndAlt.fs

annotation { "Feature Type Name" : "CT POINT GEN",
        "Feature Type Description" : "Creates curve points from CT points on selected edges",
        "Manipulator Change Function" : "createPointsManipulatorChange",
        "Editing Logic Function" : "createPointsEditingLogic", }
export const createPoints = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // (Other CT point parameters are defined and set up via internalCTpointsPredicate.)
        internalCTpointsPredicate(definition);
    }
    {
        // Get the CT points from your function.
        // Ensure that doCreatePoints returns the list of points.
        var ctPoints = doCreatePoints(context, id, definition);
        // --- Step 4. Convert CT Points from World to Sketch Coordinates ---
        // Convert each 3D CT point to the sketch's coordinate system.
        var pCount = 0;
        for (var i = 0; i < size(ctPoints); i += 1)
        {
            opPoint(context, id + ("point" ~ pCount), {
                        "point" : ctPoints[i]
                    });
            pCount += 1;
        }
    });
