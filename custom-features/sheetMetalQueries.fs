FeatureScript 2856;
import(path : "onshape/std/common.fs", version : "2856.0");

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
 * Query for all faces on a 3D sheet metal part that correspond to geometry
 * that would be cut profiles on a laser cutter in the 2D flat pattern.
 * 
 * This function returns all faces with the SMObjectType.WALL attribute, which
 * represent the flat wall faces of the sheet metal part. These are the faces
 * that correspond to the actual sheet material profile that would be cut on
 * a laser cutter, waterjet, or other 2D cutting process, regardless of their
 * attributes or generation source.
 * 
 * Note: This query works on the 3D folded representation of the sheet metal part,
 * not the flat pattern itself. It identifies faces that represent the sheet
 * material walls, which correspond to the cut profile in the flat state.
 * 
 * @param sheetMetalPart : Query for the sheet metal part(s) to analyze.
 *                         This should be a query for one or more sheet metal bodies.
 * 
 * @returns Query for all wall faces (laser-cuttable faces) on the specified
 *          sheet metal part(s). Returns a query that will match wall faces
 *          when evaluated, or match nothing if the input is not a valid sheet
 *          metal part or has no wall faces.
 * 
 * @example
 * ```
 * // Get all laser-cuttable faces from a sheet metal part
 * const cutFacesQuery = qSheetMetalCutFaces(definition.sheetMetalPart);
 * 
 * // Use in a feature to highlight or operate on cut faces
 * debug(context, cutFacesQuery, DebugColor.GREEN);
 * 
 * // Evaluate to get actual face entities if needed
 * const cutFaceEntities = evaluateQuery(context, cutFacesQuery);
 * ```
 */
export function qSheetMetalCutFaces(sheetMetalPart is Query) returns Query
{
    // Create attribute pattern to match WALL type sheet metal faces
    const wallAttributePattern = asSMAttribute({ "objectType" : SMObjectType.WALL });
    
    // Get all faces from the sheet metal part
    const allFaces = qOwnedByBody(sheetMetalPart, EntityType.FACE);
    
    // Filter to only faces with WALL attributes (using legacy unnamed attribute query)
    const cutFaces = qAttributeFilter(allFaces, wallAttributePattern);
    
    return cutFaces;
}
