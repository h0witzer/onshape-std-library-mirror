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

/**
 * Feature that creates a medial axis curve from a planar face.
 * 
 * The medial axis is computed by:
 * 1. Extruding the face profile with a 45-degree draft taper
 * 2. Querying the peak edges from the extruded body
 * 3. Filtering out edges that are vertex-adjacent to the original face
 * 4. Projecting the remaining edges back onto the input face
 * 
 * @param context {Context}: The context of the feature
 * @param id {Id}: The identifier for this feature
 * @param definition {map}: Feature parameters containing:
 *   - face {Query}: The planar face to compute the medial axis for
 */
annotation { "Feature Type Name" : "Medial Axis" }
export const medialAxis = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Planar face", 
                    "Filter" : EntityType.FACE && GeometryType.PLANE,
                    "MaxNumberOfPicks" : 1 }
        definition.face is Query;
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
        
        // Calculate extrusion distance based on bounding box
        const extrusionDistance = calculateExtrusionDistance(context, inputFace);
        
        // Step 1: Extrude the face normally
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
        
        // Create a neutral plane at the base for drafting
        opPlane(context, id + "neutralPlane", {
            "plane" : facePlane,
            "width" : 1 * meter,
            "height" : 1 * meter
        });
        const neutralPlaneFace = qCreatedBy(id + "neutralPlane", EntityType.FACE);
        
        // Apply draft to the side faces
        opDraft(context, id + "draft", {
            "draftType" : DraftType.NEUTRAL_PLANE,
            "draftFaces" : sideFaces,
            "neutralPlane" : neutralPlaneFace,
            "pullVec" : facePlane.normal,
            "angle" : 45 * degree
        });
        
        // Clean up the neutral plane
        opDeleteBodies(context, id + "deleteNeutralPlane", {
            "entities" : qOwnerBody(neutralPlaneFace)
        });
        
        // Step 3: Query all edges from the drafted body
        const allEdgesAfterDraft = qOwnedByBody(extrudedBody, EntityType.EDGE);
        
        // Step 4: Filter out edges that are vertex-adjacent to the original face
        // These are the edges at the base of the extrusion
        const edgesAdjacentToOriginalFace = qAdjacent(inputFace, AdjacencyType.VERTEX, EntityType.EDGE);
        
        // Get the peak edges (edges not adjacent to the original face)
        const peakEdges = qSubtraction(allEdgesAfterDraft, edgesAdjacentToOriginalFace);
        
        if (isQueryEmpty(context, peakEdges))
        {
            // Clean up the extruded body and report error
            opDeleteBodies(context, id + "cleanup", { "entities" : extrudedBody });
            throw regenError("No peak edges found. The extrusion may not have created a proper medial axis.");
        }
        
        // Step 5: Project the peak edges back onto the original face
        opDropCurve(context, id + "project", {
            "tools" : peakEdges,
            "targets" : inputFace,
            "projectionType" : ProjectionType.NORMAL_TO_TARGET
        });
        
        // Step 6: Delete the temporary extruded body
        opDeleteBodies(context, id + "cleanup", { "entities" : extrudedBody });
    });

/**
 * Calculate a safe extrusion distance for the medial axis computation.
 * Uses the bounding box diagonal as a conservative upper bound.
 * For a 45-degree draft, the ideal distance would be the radius of the
 * largest inscribed circle, but since we don't have that function available,
 * we use the bounding box diagonal which ensures the extrusion is sufficient.
 * 
 * @param context {Context}: The context
 * @param face {Query}: The face to compute the distance for
 * @returns {ValueWithUnits}: The computed extrusion distance
 */
function calculateExtrusionDistance(context is Context, face is Query) returns ValueWithUnits
{
    const boundingBox = evBox3d(context, { "topology" : face });
    const diagonal = norm(boundingBox.maxCorner - boundingBox.minCorner);
    
    // Use the diagonal as a safe upper bound
    // For a 45-degree draft, this ensures we extrude far enough
    return diagonal;
}
