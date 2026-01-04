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
 * 1. Extruding the face profile normally in the direction of the face normal
 * 2. Applying a 45-degree draft to each side face individually, from largest to smallest
 * 3. Querying all edges from the drafted body
 * 4. Filtering out edges that are vertex-adjacent to the start cap face
 * 5. Also filtering out edges from faces that failed to draft
 * 6. Projecting the remaining peak edges back onto the input face
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
        
        // Step 2: Apply 45-degree inward draft to side faces, one at a time
        // Get all faces of the extruded body except the start and end caps
        const allExtrudedFaces = qOwnedByBody(extrudedBody, EntityType.FACE);
        const startCapFace = qCapEntity(id + "extrude", CapType.START, EntityType.FACE);
        const endCapFace = qCapEntity(id + "extrude", CapType.END, EntityType.FACE);
        const sideFaces = qSubtraction(allExtrudedFaces, qUnion([startCapFace, endCapFace]));
        
        // Get array of individual side faces sorted by area (largest first)
        const sideFacesArray = evaluateQuery(context, sideFaces);
        const sortedSideFaces = sortFacesByArea(context, sideFacesArray);
        
        // Track faces that failed to draft - their edges should be excluded
        var failedDraftFaces is Query = qNothing();
        
        // Draft each face individually, starting with largest
        var faceIndex = 0;
        for (var face in sortedSideFaces)
        {
            try silent
            {
                opDraft(context, id + ("draft_" ~ faceIndex), {
                    "draftType" : DraftType.REFERENCE_SURFACE,
                    "draftFaces" : face,
                    "referenceSurface" : facePlane,
                    "pullVec" : facePlane.normal,
                    "angle" : 45 * degree
                });
            }
            catch
            {
                // If draft fails, add this face to the exclusion list
                failedDraftFaces = qUnion([failedDraftFaces, face]);
            }
            faceIndex += 1;
        }
        
        // Step 3: Query all edges from the drafted body
        const allEdgesAfterDraft = qOwnedByBody(extrudedBody, EntityType.EDGE);
        
        // Step 4: Filter out edges that are vertex-adjacent to the start cap face
        // Also exclude edges from faces that failed to draft
        const edgesAdjacentToStartCap = qAdjacent(startCapFace, AdjacencyType.VERTEX, EntityType.EDGE);
        const edgesFromFailedFaces = qAdjacent(failedDraftFaces, AdjacencyType.VERTEX, EntityType.EDGE);
        const edgesToExclude = qUnion([edgesAdjacentToStartCap, edgesFromFailedFaces]);
        
        // Get the peak edges by subtracting excluded edges from all edges in the drafted body
        const peakEdges = qSubtraction(allEdgesAfterDraft, edgesToExclude);
        
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

/**
 * Sort an array of face queries by their surface area in descending order (largest first).
 * This is used to draft faces from largest to smallest, which improves draft success rate.
 * 
 * @param context {Context}: The context
 * @param faces {array}: Array of face queries to sort
 * @returns {array}: Sorted array of face queries (largest area first)
 */
function sortFacesByArea(context is Context, faces is array) returns array
{
    // Create array of maps containing face and its area
    var facesWithAreas = [];
    for (var face in faces)
    {
        const area = evArea(context, { "faces" : face });
        facesWithAreas = append(facesWithAreas, { "face" : face, "area" : area });
    }
    
    // Sort by area in descending order (largest first)
    // For descending order: if a > b, return negative (a comes first)
    facesWithAreas = sort(facesWithAreas, function(a, b) {
        if (a.area > b.area)
            return -1;
        else if (a.area < b.area)
            return 1;
        else
            return 0;
    });
    
    // Extract just the face queries
    var sortedFaces = [];
    for (var item in facesWithAreas)
    {
        sortedFaces = append(sortedFaces, item.face);
    }
    
    return sortedFaces;
}
