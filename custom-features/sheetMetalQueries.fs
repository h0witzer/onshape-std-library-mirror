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
 * This function returns all model faces (on the 3D folded body) that correspond
 * to definition entities with the SMObjectType.WALL attribute. Wall faces represent
 * the flat wall faces of the sheet metal part - the actual sheet material profile
 * that would be cut on a laser cutter, waterjet, or other 2D cutting process,
 * regardless of attributes or generation source.
 * 
 * Implementation Note: Sheet metal attributes (like WALL) are stored on the master
 * definition body (BodyType.SHEET), not the 3D model. This function queries the
 * definition entities with WALL attributes and maps them to their corresponding
 * model faces using association attributes.
 * 
 * @param context : The context in which to evaluate the query
 * @param sheetMetalPart : Query for the sheet metal part(s) to analyze.
 *                         This should be a query for one or more sheet metal solid bodies.
 * 
 * @returns Query for all wall faces (laser-cuttable faces) on the 3D model of the
 *          specified sheet metal part(s). Returns a query that matches model faces
 *          when evaluated, or matches nothing if the input is not a valid sheet
 *          metal part or has no wall faces.
 * 
 * @example
 * ```
 * // Get all laser-cuttable faces from a sheet metal part
 * const cutFacesQuery = qSheetMetalCutFaces(context, definition.sheetMetalPart);
 * 
 * // Use in a feature to highlight or operate on cut faces
 * debug(context, cutFacesQuery, DebugColor.GREEN);
 * 
 * // Evaluate to get actual face entities if needed
 * const cutFaceEntities = evaluateQuery(context, cutFacesQuery);
 * ```
 */
export function qSheetMetalCutFaces(context is Context, sheetMetalPart is Query) returns Query
{
    // Get the sheet metal definition body (master body) for the part
    const definitionBody = getSheetMetalModelForPart(context, sheetMetalPart);
    
    // Create attribute pattern to match WALL type sheet metal faces on the definition
    const wallAttributePattern = asSMAttribute({ "objectType" : SMObjectType.WALL });
    
    // Get all definition faces (on the master body) with WALL attributes
    const definitionWallFaces = qAttributeFilter(qOwnedByBody(definitionBody, EntityType.FACE), wallAttributePattern);
    
    // Map the definition faces to their corresponding model faces
    // getSMCorrespondingInPart takes definition entities and returns corresponding model entities
    const modelWallFaces = getSMCorrespondingInPart(context, definitionWallFaces, EntityType.FACE);
    
    return modelWallFaces;
}
