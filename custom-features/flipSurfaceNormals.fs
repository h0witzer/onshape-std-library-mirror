FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

/**
 * Flip Surface Normals Feature
 * 
 * This feature reverses the orientation of selected surfaces so that their normals point in
 * the opposite direction. This is accomplished by extracting the B-spline surface definition,
 * reversing the V direction control point order to flip the normal direction, and replacing the
 * original surface with the flipped version.
 * 
 * The feature works by:
 * 1. Extracting or approximating the surface as a B-spline
 * 2. Reversing the order of V direction control points to flip the normal
 * 3. Reversing the V knot vector to maintain proper parametrization
 * 4. Creating a new surface with the modified control points
 * 5. Replacing the original surface with the flipped surface
 * 
 * Note: This operation modifies the surface orientation but maintains the same underlying geometry.
 */

annotation { 
    "Feature Type Name" : "Flip Surface Normals",
    "Feature Type Description" : "Reverse the orientation of selected surfaces so normals point the opposite direction"
}
export const flipSurfaceNormals = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { 
            "Name" : "Surfaces to flip", 
            "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO,
            "Description" : "Select one or more surfaces to flip their normals"
        }
        definition.surfacesToFlip is Query;
    }
    {
        // Verify that surfaces are selected
        verifyNonemptyQuery(context, definition, "surfacesToFlip", "Select at least one surface to flip");

        // Get all the faces to flip
        const facesToFlip = evaluateQuery(context, definition.surfacesToFlip);

        var successCount = 0;
        var failureCount = 0;

        // Process each face individually
        for (var faceIndex = 0; faceIndex < size(facesToFlip); faceIndex += 1)
        {
            const currentFace = facesToFlip[faceIndex];
            const faceId = id + ("face_" ~ faceIndex);

            try
            {
                // Get the surface definition
                var surfaceDefinition = evSurfaceDefinition(context, {
                    "face" : currentFace
                });

                var bSplineSurfaceData;

                // If the surface is not a B-spline, approximate it as one
                if (surfaceDefinition.surfaceType != SurfaceType.SPLINE)
                {
                    bSplineSurfaceData = evApproximateBSplineSurface(context, {
                        "face" : currentFace
                    }).bSplineSurface;
                }
                else
                {
                    // Extract the bSplineSurface field from the surface definition
                    bSplineSurfaceData = surfaceDefinition.bSplineSurface;
                }

                // Flip the surface normal by reversing the V direction control points
                // This is done by reversing the order of control point rows
                var flippedControlPoints = [];
                for (var vIndex = size(bSplineSurfaceData.controlPoints) - 1; vIndex >= 0; vIndex -= 1)
                {
                    flippedControlPoints = append(flippedControlPoints, bSplineSurfaceData.controlPoints[vIndex]);
                }

                // Also need to reverse the V knot vector to maintain parametrization
                // For a knot vector [k0, k1, ..., kn], we need to create [K-kn, K-k(n-1), ..., K-k0]
                // where K is a constant chosen to maintain the domain
                var flippedVKnots = [];
                if (size(bSplineSurfaceData.vKnots) > 0)
                {
                    const vKnotMax = bSplineSurfaceData.vKnots[size(bSplineSurfaceData.vKnots) - 1];
                    const vKnotMin = bSplineSurfaceData.vKnots[0];
                    const vKnotSum = vKnotMax + vKnotMin;
                    
                    for (var knotIndex = size(bSplineSurfaceData.vKnots) - 1; knotIndex >= 0; knotIndex -= 1)
                    {
                        flippedVKnots = append(flippedVKnots, vKnotSum - bSplineSurfaceData.vKnots[knotIndex]);
                    }
                }

                // Create the flipped B-spline surface definition
                var flippedSurface = {
                    "uDegree" : bSplineSurfaceData.uDegree,
                    "vDegree" : bSplineSurfaceData.vDegree,
                    "isRational" : bSplineSurfaceData.isRational,
                    "isUPeriodic" : bSplineSurfaceData.isUPeriodic,
                    "isVPeriodic" : bSplineSurfaceData.isVPeriodic,
                    "uKnots" : bSplineSurfaceData.uKnots,
                    "vKnots" : flippedVKnots,
                    "controlPoints" : flippedControlPoints
                };

                // Create the new flipped surface
                opCreateBSplineSurface(context, faceId + "flipped", {
                    "bSplineSurface" : flippedSurface
                });

                // Get the newly created flipped surface
                const flippedFaceQuery = qCreatedBy(faceId + "flipped", EntityType.FACE);

                // Replace the original face with the flipped surface
                opReplaceFace(context, faceId + "replace", {
                    "replaceFaces" : currentFace,
                    "templateFace" : flippedFaceQuery,
                    "offset" : 0 * meter
                });

                // Clean up the temporary flipped surface body
                opDeleteBodies(context, faceId + "cleanup", {
                    "entities" : qOwnerBody(flippedFaceQuery)
                });

                successCount += 1;
            }
            catch (error)
            {
                // If flipping this face failed, report it but continue with others
                reportFeatureWarning(context, id, "Failed to flip surface " ~ (faceIndex + 1) ~ ": " ~ toString(error));
                failureCount += 1;
            }
        }

        // Report results
        if (successCount > 0)
        {
            const pluralSurface = successCount == 1 ? "surface" : "surfaces";
            reportFeatureInfo(context, id, "Successfully flipped " ~ successCount ~ " " ~ pluralSurface);
        }
        
        if (failureCount > 0)
        {
            const pluralFailed = failureCount == 1 ? "surface" : "surfaces";
            reportFeatureWarning(context, id, "Failed to flip " ~ failureCount ~ " " ~ pluralFailed);
        }

        // Throw an error if all surfaces failed
        if (successCount == 0 && failureCount > 0)
        {
            throw regenError("Failed to flip any surfaces. Ensure selected surfaces are valid sheet bodies or surface faces.");
        }
    });

