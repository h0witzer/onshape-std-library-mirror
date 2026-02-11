FeatureScript 2878;

// Planar Face Label Placement (Alternative)
// Uses FeatureScript's built-in evaluation functions to find optimal placement
// Works with ALL face types: polygonal, splines, circles, arcs, etc.

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/coordSystem.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/mateConnector.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");

annotation {
    "Feature Type Name" : "Face Label Placement Alt",
    "Feature Type Description" : "Places a mate connector at an optimal position on a planar face using the face centroid (alternative implementation)",
    "Feature Name Template" : "Face Label Alt"
}
export const faceLabelPlacementAlt = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
            "Name" : "Planar face",
            "Filter" : EntityType.FACE && GeometryType.PLANE,
            "MaxNumberOfPicks" : 1
        }
        definition.face is Query;
    }
    {
        // Verify face is selected
        verifyNonemptyQuery(context, definition, "face", "Select a planar face");
        
        const face = definition.face;
        
        // Use FeatureScript's built-in function to get the approximate centroid
        // This works for ALL face types: polygonal, circles, splines, etc.
        const centroid = evApproximateCentroid(context, {
            "entities" : face
        });
        
        // Get the tangent plane for orientation
        const tangentPlane = evFaceTangentPlane(context, {
            "face" : face,
            "parameter" : vector(0.5, 0.5)
        });
        
        // Create coordinate system for mate connector
        // Origin at centroid, Z-axis is face normal, X-axis from plane
        const placementCsys = coordSystem(centroid, tangentPlane.x, tangentPlane.normal);
        
        // Create the mate connector
        opMateConnector(context, id + "mateConnector", {
            "coordSystem" : placementCsys,
            "owner" : qNothing()
        });
    });
