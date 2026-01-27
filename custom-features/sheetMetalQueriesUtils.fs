FeatureScript 2856;
// Sheet Metal Query Utility Functions
// Supporting utility functions for sheet metal query operations.

import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/query.fs", version : "2856.0");
import(path : "onshape/std/evaluate.fs", version : "2856.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2856.0");
import(path : "onshape/std/smobjecttype.gen.fs", version : "2856.0");
import(path : "onshape/std/smjointtype.gen.fs", version : "2856.0");
import(path : "onshape/std/attributes.fs", version : "2856.0");
import(path : "onshape/std/containers.fs", version : "2856.0");
import(path : "onshape/std/units.fs", version : "2856.0");
import(path : "onshape/std/math.fs", version : "2856.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2856.0");

/**
 * Check if a query contains any sheet metal bend edges.
 * Evaluates whether at least one edge in the query has a BEND joint attribute.
 *
 * @param context {Context} : The context of the current part studio
 * @param edgeQuery {Query} : The query containing edges to check
 * 
 * @returns {boolean} : True if the query contains at least one bend edge
 */
export function hasBendEdges(context is Context, edgeQuery is Query) returns boolean
{
    const edges = evaluateQuery(context, edgeQuery);
    
    for (var edge in edges)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.jointType != undefined && attribute.jointType.value == SMJointType.BEND)
            {
                return true;
            }
        }
    }
    
    return false;
}

/**
 * Check if a query contains any sheet metal wall faces.
 * Evaluates whether at least one face in the query has a WALL attribute.
 *
 * @param context {Context} : The context of the current part studio
 * @param faceQuery {Query} : The query containing faces to check
 * 
 * @returns {boolean} : True if the query contains at least one wall face
 */
export function hasWallFaces(context is Context, faceQuery is Query) returns boolean
{
    const faces = evaluateQuery(context, faceQuery);
    
    for (var face in faces)
    {
        if (hasSheetMetalAttribute(context, face, SMObjectType.WALL))
        {
            return true;
        }
    }
    
    return false;
}

/**
 * Count the number of bend edges in a query.
 * Returns the total count of edges with BEND joint attributes.
 *
 * @param context {Context} : The context of the current part studio
 * @param edgeQuery {Query} : The query containing edges to count
 * 
 * @returns {number} : The number of bend edges found
 */
export function countBendEdges(context is Context, edgeQuery is Query) returns number
{
    const edges = evaluateQuery(context, edgeQuery);
    var count = 0;
    
    for (var edge in edges)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.jointType != undefined && attribute.jointType.value == SMJointType.BEND)
            {
                count += 1;
                break;
            }
        }
    }
    
    return count;
}

/**
 * Count the number of wall faces in a query.
 * Returns the total count of faces with WALL attributes.
 *
 * @param context {Context} : The context of the current part studio
 * @param faceQuery {Query} : The query containing faces to count
 * 
 * @returns {number} : The number of wall faces found
 */
export function countWallFaces(context is Context, faceQuery is Query) returns number
{
    const faces = evaluateQuery(context, faceQuery);
    var count = 0;
    
    for (var face in faces)
    {
        if (hasSheetMetalAttribute(context, face, SMObjectType.WALL))
        {
            count += 1;
        }
    }
    
    return count;
}

/**
 * Get the bend radius of a bend edge.
 * Returns the radius value from the bend attribute, or undefined if not a bend edge.
 *
 * @param context {Context} : The context of the current part studio
 * @param bendEdge {Query} : A single bend edge to query
 * 
 * @returns {ValueWithUnits} : The bend radius, or undefined if not found
 */
export function getBendRadius(context is Context, bendEdge is Query)
{
    const edges = evaluateQuery(context, bendEdge);
    
    if (size(edges) != 1)
    {
        return undefined;
    }
    
    const attributes = getSmObjectTypeAttributes(context, edges[0], SMObjectType.JOINT);
    
    for (var attribute in attributes)
    {
        if (attribute.jointType != undefined && 
            attribute.jointType.value == SMJointType.BEND &&
            attribute.radius != undefined)
        {
            return attribute.radius.value;
        }
    }
    
    return undefined;
}

/**
 * Get the bend angle of a bend edge.
 * Returns the angle value from the bend attribute, or undefined if not a bend edge.
 *
 * @param context {Context} : The context of the current part studio
 * @param bendEdge {Query} : A single bend edge to query
 * 
 * @returns {ValueWithUnits} : The bend angle, or undefined if not found
 */
export function getBendAngle(context is Context, bendEdge is Query)
{
    const edges = evaluateQuery(context, bendEdge);
    
    if (size(edges) != 1)
    {
        return undefined;
    }
    
    const attributes = getSmObjectTypeAttributes(context, edges[0], SMObjectType.JOINT);
    
    for (var attribute in attributes)
    {
        if (attribute.jointType != undefined && 
            attribute.jointType.value == SMJointType.BEND &&
            attribute.angle != undefined)
        {
            return attribute.angle.value;
        }
    }
    
    return undefined;
}

/**
 * Check if a face is a planar wall face.
 * Returns true if the face is a wall with a planar surface.
 *
 * @param context {Context} : The context of the current part studio
 * @param face {Query} : A single face to check
 * 
 * @returns {boolean} : True if the face is a planar wall
 */
export function isPlanarWall(context is Context, face is Query) returns boolean
{
    const faces = evaluateQuery(context, face);
    
    if (size(faces) != 1)
    {
        return false;
    }
    
    if (!hasSheetMetalAttribute(context, faces[0], SMObjectType.WALL))
    {
        return false;
    }
    
    const surface = evSurfaceDefinition(context, { "face" : faces[0] });
    return surface is Plane;
}

/**
 * Check if a face is a cylindrical wall face (rolled wall).
 * Returns true if the face is a wall with a cylindrical surface.
 *
 * @param context {Context} : The context of the current part studio
 * @param face {Query} : A single face to check
 * 
 * @returns {boolean} : True if the face is a cylindrical wall
 */
export function isCylindricalWall(context is Context, face is Query) returns boolean
{
    const faces = evaluateQuery(context, face);
    
    if (size(faces) != 1)
    {
        return false;
    }
    
    if (!hasSheetMetalAttribute(context, faces[0], SMObjectType.WALL))
    {
        return false;
    }
    
    const surface = evSurfaceDefinition(context, { "face" : faces[0] });
    return surface is Cylinder;
}

/**
 * Get all bend radii from a set of bend edges as an array.
 * Returns an array of radius values for all bend edges in the query.
 *
 * @param context {Context} : The context of the current part studio
 * @param bendEdges {Query} : The bend edges to get radii from
 * 
 * @returns {array} : Array of ValueWithUnits representing bend radii
 */
export function getAllBendRadii(context is Context, bendEdges is Query) returns array
{
    const edges = evaluateQuery(context, bendEdges);
    var radii = [];
    
    for (var edge in edges)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.jointType != undefined && 
                attribute.jointType.value == SMJointType.BEND &&
                attribute.radius != undefined)
            {
                radii = append(radii, attribute.radius.value);
                break;
            }
        }
    }
    
    return radii;
}

/**
 * Get the sheet metal model thickness from a sheet metal entity.
 * Returns the front thickness value from the model attribute.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntity {Query} : Any entity belonging to a sheet metal model
 * 
 * @returns {ValueWithUnits} : The sheet metal thickness, or undefined if not found
 */
export function getSheetMetalThickness(context is Context, sheetMetalEntity is Query)
{
    const evaluated = evaluateQuery(context, sheetMetalEntity);
    
    if (size(evaluated) == 0)
    {
        return undefined;
    }
    
    const ownerBody = qOwnerBody(evaluated[0]);
    const modelAttributes = getSmObjectTypeAttributes(context, ownerBody, SMObjectType.MODEL);
    
    if (size(modelAttributes) > 0 && modelAttributes[0].frontThickness != undefined)
    {
        return modelAttributes[0].frontThickness.value;
    }
    
    return undefined;
}

/**
 * Filter a query to only include entities that are part of active sheet metal models.
 * Returns a query containing only entities from active sheet metal.
 *
 * @param context {Context} : The context of the current part studio
 * @param entityQuery {Query} : The query to filter
 * 
 * @returns {Query} : Filtered query containing only active sheet metal entities
 */
export function filterActiveSheetMetal(context is Context, entityQuery is Query) returns Query
{
    return qActiveSheetMetalFilter(entityQuery, ActiveSheetMetal.YES);
}

