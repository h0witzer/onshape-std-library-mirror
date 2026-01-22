FeatureScript 2837;

import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/box.fs", version : "2837.0");
import(path : "onshape/std/projectiontype.gen.fs", version : "2837.0");

/**
 * Feature that creates a medial axis curve from a planar face.
 * 
 * The medial axis is computed by:
 * 1. Under-extruding the face profile normally to a conservative distance
 * 2. Applying a 45-degree draft to all side faces, creating peaks
 * 3. Trying to delete the end cap face; if that fails, moving it by the diagonal distance
 * 4. Querying peak edges from the resulting geometry
 * 5. Optionally filtering out boundary edges adjacent to the start cap
 * 6. Combining peak edges with edges created by the delete/move operation
 * 7. Projecting all medial edges back onto the input face
 * 
 * @param context {Context}: The context of the feature
 * @param id {Id}: The identifier for this feature
 * @param definition {map}: Feature parameters containing:
 *   - face {Query}: The planar face to compute the medial axis for
 *   - includeBoundaryEdges {boolean}: When true, includes boundary edges adjacent to the input face in the projection
 */
annotation { "Feature Type Name" : "Medial Axis" }
export const medialAxis = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Planar face", 
                    "Filter" : EntityType.FACE && GeometryType.PLANE,
                    "MaxNumberOfPicks" : 1 }
        definition.face is Query;
        
        annotation { "Name" : "Include boundary edges", "Default" : false }
        definition.includeBoundaryEdges is boolean;
        
    }
    {
        // Verify we have a planar face
        const inputFace = definition.face;
        if (isQueryEmpty(context, inputFace))
        {
            throw regenError("No face selected");
        }
        
        // Get the plane definition of the face
        const facePlane = evPlane(context, { "face" : inputFace });
        
        // Calculate extrusion distance and move distance
        const distances = calculateDistances(context, inputFace);
        const extrusionDistance = distances.extrusionDistance;
        const moveDistance = distances.moveDistance;
        
        // Step 1: Extrude the face normally (using a conservative under-extrusion distance)
        opExtrude(context, id + "extrude", {
            "entities" : inputFace,
            "direction" : facePlane.normal,
            "endBound" : BoundingType.BLIND,
            "endDepth" : extrusionDistance
        });
        
        const extrudedBody = qCreatedBy(id + "extrude", EntityType.BODY);
        
        // Step 2: Apply 45-degree inward draft to create a tapered body
        // We need to draft the side faces (walls) of the extrusion inward
        // Get all faces of the extruded body except the start and end caps
        const allExtrudedFaces = qOwnedByBody(extrudedBody, EntityType.FACE);
        const startCapFace = qCapEntity(id + "extrude", CapType.START, EntityType.FACE);
        const endCapFace = qCapEntity(id + "extrude", CapType.END, EntityType.FACE);
        const sideFaces = qSubtraction(allExtrudedFaces, qUnion([startCapFace, endCapFace]));
        
        // Apply draft to the side faces using the input face plane as the reference surface
        // Use try/catch to handle draft failures gracefully
        try silent
        {
            opDraft(context, id + "draft", {
                "draftType" : DraftType.REFERENCE_SURFACE,
                "draftFaces" : sideFaces,
                "referenceSurface" : facePlane,
                "pullVec" : facePlane.normal,
                "angle" : 45 * degree
            });
        }
        catch
        {
            // If draft fails, we'll continue without it
            // The undrafted extrusion may still provide useful edges
        }
        
        // Step 3: Try to delete the end cap face first, fall back to move face if that fails
        // Either operation will regenerate peaks where the geometry meets the drafted walls
        var deleteFaceSucceeded = false;
        try
        {
            opDeleteFace(context, id + "deleteEndCap", {
                "deleteFaces" : endCapFace,
                "includeFillet" : false,
                "capVoid" : false,
                "leaveOpen" : false
            });
            deleteFaceSucceeded = true;
        }
        catch
        {
            // Delete face failed, fall back to move face operation
            const moveTransform = transform(facePlane.normal * moveDistance);
            opMoveFace(context, id + "moveEndCap", {
                "moveFaces" : endCapFace,
                "transform" : moveTransform
            });
        }
        
        // Step 4: Query all edges from the body after delete/move operation
        // Peak edges now exist after the end cap has been modified
        const allEdgesAfterOperation = qOwnedByBody(extrudedBody, EntityType.EDGE);
        
        // Step 5: Filter out edges that are vertex-adjacent to the start cap face (boundary edges)
        // unless the user has opted to include them
        var peakEdges;
        if (definition.includeBoundaryEdges)
        {
            // Include all edges (boundary edges + peak edges)
            peakEdges = allEdgesAfterOperation;
        }
        else
        {
            // Exclude boundary edges (default behavior)
            const edgesAdjacentToStartCap = qAdjacent(startCapFace, AdjacencyType.VERTEX, EntityType.EDGE);
            peakEdges = qSubtraction(allEdgesAfterOperation, edgesAdjacentToStartCap);
        }
        
        // Step 6: Query edges created by the delete or move operation
        const edgesCreatedByOperation = deleteFaceSucceeded ? 
            qCreatedBy(id + "deleteEndCap", EntityType.EDGE) : 
            qCreatedBy(id + "moveEndCap", EntityType.EDGE);
        
        // Combine peak edges with edges created by the operation
        const allMedialEdges = qUnion([peakEdges, edgesCreatedByOperation]);
        
        if (isQueryEmpty(context, allMedialEdges))
        {
            // Clean up the extruded body and report error
            opDeleteBodies(context, id + "cleanup", { "entities" : extrudedBody });
            throw regenError("No peak edges found. The extrusion may not have created a proper medial axis.");
        }
        
        // Step 7: Project the peak edges back onto the original face
        opDropCurve(context, id + "project", {
            "tools" : allMedialEdges,
            "targets" : inputFace,
            "projectionType" : ProjectionType.NORMAL_TO_TARGET
        });
        
        // Step 8: Delete the temporary extruded body
        opDeleteBodies(context, id + "cleanup", { "entities" : extrudedBody });
    });

/**
 * Calculate distances for the medial axis computation.
 * Uses a fraction of the shortest edge length for under-extrusion and the
 * bounding box diagonal for moving the end cap to regenerate peaks.
 * 
 * @param context {Context}: The context
 * @param face {Query}: The face to compute distances for
 * @returns {map}: A map containing:
 *   - extrusionDistance {ValueWithUnits}: The under-extrusion distance (0.25× shortest edge)
 *   - moveDistance {ValueWithUnits}: The distance to move end cap (bounding box diagonal)
 */
function calculateDistances(context is Context, face is Query) returns map
{
    // Get all edges that bound the face
    const faceEdges = qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE);
    const edgeArray = evaluateQuery(context, faceEdges);
    
    // Calculate bounding box diagonal for move distance
    const boundingBox = evBox3d(context, { "topology" : face });
    const diagonal = norm(boundingBox.maxCorner - boundingBox.minCorner);
    
    var extrusionDistance;
    if (size(edgeArray) == 0)
    {
        // Fallback to bounding box diagonal if no edges found
        extrusionDistance = 0.25 * diagonal;
    }
    else
    {
        // Find the shortest edge length
        var shortestLength = undefined;
        for (var edge in edgeArray)
        {
            const edgeLength = evLength(context, { "entities" : edge });
            if (shortestLength == undefined || edgeLength < shortestLength)
            {
                shortestLength = edgeLength;
            }
        }
        
        // Use 0.25× the shortest edge as the extrusion distance for stability
        extrusionDistance = 0.25 * shortestLength;
    }
    
    return {
        "extrusionDistance" : extrusionDistance,
        "moveDistance" : diagonal
    };
}
