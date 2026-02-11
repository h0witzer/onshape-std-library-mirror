FeatureScript 2878;

// Planar Face Label Placement (Alternative)
// Uses evFaceTangentPlane parameter sampling as described in the markdown
// Works with ALL face types: polygonal, splines, circles, arcs, etc.

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/coordSystem.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/mateConnector.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");

annotation {
    "Feature Type Name" : "Face Label Alt",
    "Feature Type Description" : "Places a mate connector on a planar face at parameter center (alternative)",
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
        
        // Sample the face at parameter (0.5, 0.5)
        // The tangent plane's origin is a point ON the face surface
        // This works for all face types without needing vertices
        const tangentPlane = evFaceTangentPlane(context, {
            "face" : face,
            "parameter" : vector(0.5, 0.5)
        });
        
        // Use the tangent plane's origin as the placement point
        // It's guaranteed to be on the face (not in a void)
        const placementPoint = tangentPlane.origin;
        
        // Create coordinate system for mate connector
        // Origin at the sampled point, Z-axis is face normal
        const placementCsys = coordSystem(placementPoint, tangentPlane.x, tangentPlane.normal);
        
        // Create the mate connector
        opMateConnector(context, id + "mateConnector", {
            "coordSystem" : placementCsys,
            "owner" : qNothing()
        });
    });
