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
 * 3. Trying to delete the end cap face; if that fails, moving it by a specified distance
 * 4. Querying peak edges from the resulting geometry
 * 5. Filtering edges based on adjacency to the start cap (vertex or edge adjacency)
 * 6. Combining filtered edges with edges created by the delete/move operation
 * 7. Projecting all medial edges back onto the input face
 * 
 * @param context {Context}: The context of the feature
 * @param id {Id}: The identifier for this feature
 * @param definition {map}: Feature parameters containing:
 *   - face {Query}: The planar face to compute the medial axis for
 *   - includeBoundaryEdges {boolean}: When false (default), excludes edges that share vertices with the input face.
 *                                      When true, excludes only edges that share edges with the input face (less strict).
 *   - overrideMoveDistance {boolean}: When true, allows manual specification of the move face distance.
 *   - manualMoveDistance {ValueWithUnits}: Manual override for the move face distance (only used when overrideMoveDistance is true).
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
        
        annotation { "Name" : "Override move distance", "Default" : false }
        definition.overrideMoveDistance is boolean;
        
        if (definition.overrideMoveDistance)
        {
            annotation { "Name" : "Move distance" }
            isLength(definition.manualMoveDistance, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);
        }
        
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
        
        // Use manual move distance if override is enabled, otherwise use calculated distance
        const moveDistance = definition.overrideMoveDistance ? 
            definition.manualMoveDistance : 
            distances.moveDistance;
        
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
        
        // Step 5: Filter edges based on adjacency to the start cap face
        // When includeBoundaryEdges is false: exclude all vertex-adjacent edges (default, strictest filtering)
        // When includeBoundaryEdges is true: exclude only edge-adjacent edges (keeps vertex-adjacent edges)
        var peakEdges;
        if (definition.includeBoundaryEdges)
        {
            // Include vertex-adjacent edges, but exclude edge-adjacent edges
            const edgesEdgeAdjacentToStartCap = qAdjacent(startCapFace, AdjacencyType.EDGE, EntityType.EDGE);
            peakEdges = qSubtraction(allEdgesAfterOperation, edgesEdgeAdjacentToStartCap);
        }
        else
        {
            // Exclude all boundary edges that share vertices with start cap (default behavior)
            const edgesVertexAdjacentToStartCap = qAdjacent(startCapFace, AdjacencyType.VERTEX, EntityType.EDGE);
            peakEdges = qSubtraction(allEdgesAfterOperation, edgesVertexAdjacentToStartCap);
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
