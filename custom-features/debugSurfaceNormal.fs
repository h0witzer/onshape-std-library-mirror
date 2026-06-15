FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

/**
 * Debug Surface Normal Feature
 * 
 * This feature visualizes the surface normal at the center of a selected surface or surfaces.
 * It displays a debug arrow pointing in the direction of the surface normal at the midpoint
 * (parameter [0.5, 0.5]) of each selected face.
 * 
 * Use this feature to validate surface orientation and verify that normals point in the
 * expected direction, particularly after operations that may flip surface normals.
 */

annotation { 
    "Feature Type Name" : "Debug Surface Normal",
    "Feature Type Description" : "Display surface normal at the middle of selected surfaces for validation"
}
export const debugSurfaceNormal = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { 
            "Name" : "Surfaces to debug", 
            "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO,
            "Description" : "Select one or more surfaces to visualize their normals"
        }
        definition.surfacesToDebug is Query;

        annotation { 
            "Name" : "Normal length",
            "Description" : "Length of the debug normal arrow"
        }
        isLength(definition.normalLength, NONNEGATIVE_LENGTH_BOUNDS);

        annotation { 
            "Name" : "Debug color",
            "Description" : "Color to display the normal arrow",
            "Default" : DebugColor.RED
        }
        definition.debugColor is DebugColor;
    }
    {
        // Verify that surfaces are selected
        verifyNonemptyQuery(context, definition, "surfacesToDebug", "Select at least one surface to debug");

        // Get all the faces to debug
        const facesToDebug = evaluateQuery(context, definition.surfacesToDebug);

        // For each face, evaluate the tangent plane at the center and display the normal
        for (var face in facesToDebug)
        {
            try
            {
                // Evaluate the tangent plane at the center of the face (parameter [0.5, 0.5])
                const tangentPlane = evFaceTangentPlane(context, {
                    "face" : face,
                    "parameter" : vector(0.5, 0.5)
                });

                // Calculate the end point of the normal arrow
                const normalEndPoint = tangentPlane.origin + tangentPlane.normal * definition.normalLength;

                // Calculate arrow radius based on normal length
                const arrowRadius = definition.normalLength * 0.05;

                // Display the normal as a debug arrow
                addDebugArrow(context, tangentPlane.origin, normalEndPoint, arrowRadius, definition.debugColor);

                // Also display a point at the origin for reference
                addDebugPoint(context, tangentPlane.origin, definition.debugColor);
            }
            catch
            {
                // If we can't evaluate the tangent plane for this face, report a warning
                reportFeatureWarning(context, id, "Could not evaluate normal for one or more selected surfaces");
            }
        }

        // Report success
        const faceCount = size(facesToDebug);
        const pluralSurface = faceCount == 1 ? "surface" : "surfaces";
        reportFeatureInfo(context, id, "Displayed normals for " ~ faceCount ~ " " ~ pluralSurface);
    });

