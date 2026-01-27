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
 * Query for all faces on a 3D sheet metal part that correspond to the edge/profile
 * geometry that would be cut on a laser cutter in the 2D flat pattern.
 * 
 * This function identifies the 3D model faces whose flat pattern counterparts represent
 * the vertical edge faces (cut profiles). These are the faces that correspond to the
 * perimeter and internal cutout edges that would be cut by a laser cutter, waterjet,
 * or other 2D cutting process.
 * 
 * Stock faces (the top and bottom faces of the sheet metal in the flat pattern) are
 * horizontal and are NOT returned by this function. This function specifically returns
 * the 3D faces whose flat counterparts define the cut perimeter.
 * 
 * Implementation: The function uses qFacesParallelToDirection on the flat pattern to
 * identify cut faces (faces parallel to Z direction = vertical edges), then maps those
 * back to the corresponding 3D model faces.
 * 
 * @param context : The context in which to evaluate the query
 * @param sheetMetalPart : Query for the sheet metal part(s) to analyze.
 *                         This should be a query for one or more sheet metal solid bodies.
 * 
 * @returns Query for all 3D model faces whose flat pattern counterparts are cut profile
 *          edges. Returns a query that matches 3D faces when evaluated, or matches nothing
 *          if the input is not a valid sheet metal part.
 * 
 * @example
 * ```
 * // Get all 3D faces corresponding to laser-cut edges from a sheet metal part
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
    
    // In the flat pattern, cut faces are parallel to Z direction (vertical edges)
    // Use qFacesParallelToDirection to get faces parallel to Z direction
    // For planar faces, this means their normals are perpendicular to Z (vertical edges)
    const zDirection = vector(0, 0, 1);
    const cutFacesInFlat = qFacesParallelToDirection(flatFaces, zDirection);
    
    // Now we need to find which 3D model faces correspond to these cut faces in flat
    // We do this by filtering the original model faces
    const cutFaceEntitiesInFlat = evaluateQuery(context, cutFacesInFlat);
    
    var cutFacesIn3D = [];
    
    // For each 3D model face, check if its flat counterpart is a cut face
    const modelFaceEntities = evaluateQuery(context, modelFaces);
    for (var modelFace in modelFaceEntities)
    {
        const correspondingFlatFace = evaluateQuery(context, qCorrespondingInFlat(modelFace));
        
        // Check if this flat face is in our cut faces list
        for (var cutFace in cutFaceEntitiesInFlat)
        {
            if (size(correspondingFlatFace) > 0 && correspondingFlatFace[0] == cutFace)
            {
                cutFacesIn3D = append(cutFacesIn3D, modelFace);
                break;
            }
        }
    }
    
    return qUnion(cutFacesIn3D);
}

/**
 * Query for all faces on a 3D sheet metal part that correspond to the stock/material
 * faces in the 2D flat pattern (the top and bottom surfaces of the sheet metal).
 * 
 * This function identifies the 3D model faces whose flat pattern counterparts represent
 * the horizontal stock faces. These are the flat top and bottom surfaces of the sheet
 * metal material in the flat pattern, as opposed to the vertical edge faces that would
 * be cut by a laser cutter.
 * 
 * Cut faces (the vertical edge profiles) are NOT returned by this function. This function
 * specifically returns the 3D faces whose flat counterparts are the stock material surfaces.
 * 
 * Implementation: The function uses qParallelPlanes on the flat pattern to identify stock
 * faces (faces with normals parallel to Z direction = horizontal surfaces), then maps those
 * back to the corresponding 3D model faces.
 * 
 * @param context : The context in which to evaluate the query
 * @param sheetMetalPart : Query for the sheet metal part(s) to analyze.
 *                         This should be a query for one or more sheet metal solid bodies.
 * 
 * @returns Query for all 3D model faces whose flat pattern counterparts are stock material
 *          surfaces. Returns a query that matches 3D faces when evaluated, or matches nothing
 *          if the input is not a valid sheet metal part.
 * 
 * @example
 * ```
 * // Get all 3D faces corresponding to stock surfaces from a sheet metal part
 * const stockFacesQuery = qSheetMetalStockFaces(context, definition.sheetMetalPart);
 * 
 * // Use in a feature to highlight or operate on stock faces
 * debug(context, stockFacesQuery, DebugColor.BLUE);
 * 
 * // Evaluate to get actual face entities if needed
 * const stockFaceEntities = evaluateQuery(context, stockFacesQuery);
 * ```
 */
export function qSheetMetalStockFaces(context is Context, sheetMetalPart is Query) returns Query
{
    // Get all faces from the sheet metal part (3D folded model)
    const modelFaces = qOwnedByBody(sheetMetalPart, EntityType.FACE);
    
    // Get the corresponding faces in the flat pattern
    const flatFaces = qCorrespondingInFlat(modelFaces);
    
    // In the flat pattern, stock faces have normals parallel to Z direction (horizontal surfaces)
    // Use qParallelPlanes to get faces with normals parallel to Z direction
    // These are the top and bottom faces of the sheet material
    const zDirection = vector(0, 0, 1);
    const stockFacesInFlat = qParallelPlanes(flatFaces, zDirection);
    
    // Now we need to find which 3D model faces correspond to these stock faces in flat
    // We do this by filtering the original model faces
    const stockFaceEntitiesInFlat = evaluateQuery(context, stockFacesInFlat);
    
    var stockFacesIn3D = [];
    
    // For each 3D model face, check if its flat counterpart is a stock face
    const modelFaceEntities = evaluateQuery(context, modelFaces);
    for (var modelFace in modelFaceEntities)
    {
        const correspondingFlatFace = evaluateQuery(context, qCorrespondingInFlat(modelFace));
        
        // Check if this flat face is in our stock faces list
        for (var stockFace in stockFaceEntitiesInFlat)
        {
            if (size(correspondingFlatFace) > 0 && correspondingFlatFace[0] == stockFace)
            {
                stockFacesIn3D = append(stockFacesIn3D, modelFace);
                break;
            }
        }
    }
    
    return qUnion(stockFacesIn3D);
}
