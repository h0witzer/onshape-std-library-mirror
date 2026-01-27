FeatureScript 2856;
import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2856.0");

/**
 * Sheet Metal Query Helper Functions
 * 
 * This module provides a collection of specialized query functions for working with
 * sheet metal entities in Onshape. These functions enable more robust and powerful
 * selections of sheet metal geometry that aren't currently trivial to access using
 * standard query methods.
 * 
 * The functions in this library abstract away the complexity of working with sheet
 * metal attributes and provide intuitive, purpose-driven queries for common sheet
 * metal operations.
 */

/**
 * Query for all faces on the flat pattern of a sheet metal part that correspond to 
 * the cut profile that would be made on a laser cutter, waterjet, or other 2D cutting process.
 * 
 * This function works by analyzing the flat pattern representation of the sheet metal part.
 * Cut faces are identified as faces that are perpendicular to the sheet (parallel to Z-axis)
 * in the flat pattern, representing the edges that would be cut by a 2D cutting process.
 * 
 * Stock faces (the top and bottom faces of the sheet metal) are parallel to the XY plane
 * and are NOT returned by this function. This function specifically returns the edge/profile
 * faces that define the perimeter and internal cutouts of the flat pattern.
 * 
 * Implementation: The function gets the flat pattern entities corresponding to the 3D part,
 * then filters for faces that are parallel to the Z-axis (perpendicular to the sheet plane).
 * 
 * @param context : The context in which to evaluate the query
 * @param sheetMetalPart : Query for the sheet metal part(s) to analyze.
 *                         This should be a query for one or more sheet metal solid bodies.
 * 
 * @returns Query for all cut profile faces in the flat pattern. These are the faces
 *          perpendicular to the sheet that represent the laser-cut edges. Returns a query
 *          that matches faces when evaluated, or matches nothing if the input is not a
 *          valid sheet metal part.
 * 
 * @example
 * ```
 * // Get all laser-cut edge faces from a sheet metal part's flat pattern
 * const cutFacesQuery = qSheetMetalCutFaces(context, definition.sheetMetalPart);
 * 
 * // Use in a feature to highlight or operate on cut profile faces
 * debug(context, cutFacesQuery, DebugColor.GREEN);
 * 
 * // Evaluate to get actual face entities if needed
 * const cutFaceEntities = evaluateQuery(context, cutFacesQuery);
 * ```
 */
export function qSheetMetalCutFaces(context is Context, sheetMetalPart is Query) returns Query
{
    // Get all faces from the sheet metal part (3D folded model)
    const modelFaces = qOwnedByBody(sheetMetalPart, EntityType.FACE);
    
    // Get the corresponding faces in the flat pattern
    const flatFaces = qCorrespondingInFlat(modelFaces);
    
    // Evaluate the flat faces to filter them by orientation
    const flatFaceEntities = evaluateQuery(context, flatFaces);
    
    var cutFaceQueries = [];
    
    // Filter faces based on their normal direction in the flat pattern
    // Cut faces are perpendicular to the sheet (parallel to Z-axis)
    for (var face in flatFaceEntities)
    {
        try
        {
            // Get the plane of the face
            const facePlane = evPlane(context, {
                "face" : face
            });
            
            // Check if the face normal is parallel to Z-axis (perpendicular to sheet)
            // The Z-axis vector is (0, 0, 1)
            const zAxis = vector(0, 0, 1);
            const normalDotZ = abs(dot(facePlane.normal, zAxis));
            
            // If the face normal is parallel to Z (normalDotZ ≈ 1), it's a cut face
            // If the face normal is perpendicular to Z (normalDotZ ≈ 0), it's a stock face (top/bottom)
            // We want faces that are parallel to Z (the vertical edges of the flat pattern)
            if (normalDotZ > 0.99) // Close to 1, meaning parallel to Z
            {
                cutFaceQueries = append(cutFaceQueries, face);
            }
        }
        catch
        {
            // If face is not planar or evaluation fails, skip it
        }
    }
    
    return qUnion(cutFaceQueries);
}
