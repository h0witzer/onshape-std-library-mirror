FeatureScript 2856;
// Sheet Metal Query Helper Functions
// This module provides robust helper functions for querying sheet metal entities
// that are not trivial to select in standard features.

import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/query.fs", version : "2856.0");
import(path : "onshape/std/evaluate.fs", version : "2856.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2856.0");
import(path : "onshape/std/smobjecttype.gen.fs", version : "2856.0");
import(path : "onshape/std/smjointtype.gen.fs", version : "2856.0");
import(path : "onshape/std/smbendtype.gen.fs", version : "2856.0");
import(path : "onshape/std/attributes.fs", version : "2856.0");
import(path : "onshape/std/containers.fs", version : "2856.0");
import(path : "onshape/std/vector.fs", version : "2856.0");
import(path : "onshape/std/math.fs", version : "2856.0");
import(path : "onshape/std/units.fs", version : "2856.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2856.0");

/**
 * Query all bend edges in the specified sheet metal entities.
 * Returns edges that have a BEND joint attribute attached to them.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntities {Query} : The sheet metal entities to search within
 * 
 * @returns {Query} : A query for all bend edges
 */
export function qSheetMetalBendEdges(context is Context, sheetMetalEntities is Query) returns Query
{
    const definitionEntities = getSMDefinitionEntities(context, sheetMetalEntities, EntityType.EDGE);
    var bendEdges = [];
    
    for (var edge in definitionEntities)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.jointType != undefined && attribute.jointType.value == SMJointType.BEND)
            {
                bendEdges = append(bendEdges, edge);
                break;
            }
        }
    }
    
    return qUnion(bendEdges);
}

/**
 * Query all rip edges in the specified sheet metal entities.
 * Returns edges that have a RIP joint attribute attached to them.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntities {Query} : The sheet metal entities to search within
 * 
 * @returns {Query} : A query for all rip edges
 */
export function qSheetMetalRipEdges(context is Context, sheetMetalEntities is Query) returns Query
{
    const definitionEntities = getSMDefinitionEntities(context, sheetMetalEntities, EntityType.EDGE);
    var ripEdges = [];
    
    for (var edge in definitionEntities)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.jointType != undefined && attribute.jointType.value == SMJointType.RIP)
            {
                ripEdges = append(ripEdges, edge);
                break;
            }
        }
    }
    
    return qUnion(ripEdges);
}

/**
 * Query all wall faces in the specified sheet metal entities.
 * Returns faces that have a WALL attribute attached to them.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntities {Query} : The sheet metal entities to search within
 * 
 * @returns {Query} : A query for all wall faces
 */
export function qSheetMetalWallFaces(context is Context, sheetMetalEntities is Query) returns Query
{
    const definitionEntities = getSMDefinitionEntities(context, sheetMetalEntities, EntityType.FACE);
    var wallFaces = [];
    
    for (var face in definitionEntities)
    {
        if (hasSheetMetalAttribute(context, face, SMObjectType.WALL))
        {
            wallFaces = append(wallFaces, face);
        }
    }
    
    return qUnion(wallFaces);
}

/**
 * Query all corner vertices in the specified sheet metal entities.
 * Returns vertices that have a CORNER attribute attached to them.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntities {Query} : The sheet metal entities to search within
 * 
 * @returns {Query} : A query for all corner vertices
 */
export function qSheetMetalCornerVertices(context is Context, sheetMetalEntities is Query) returns Query
{
    const definitionEntities = getSMDefinitionEntities(context, sheetMetalEntities, EntityType.VERTEX);
    var cornerVertices = [];
    
    for (var vertex in definitionEntities)
    {
        if (hasSheetMetalAttribute(context, vertex, SMObjectType.CORNER))
        {
            cornerVertices = append(cornerVertices, vertex);
        }
    }
    
    return qUnion(cornerVertices);
}

/**
 * Query all planar wall faces in the specified sheet metal entities.
 * Returns wall faces that are planar surfaces.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntities {Query} : The sheet metal entities to search within
 * 
 * @returns {Query} : A query for all planar wall faces
 */
export function qSheetMetalPlanarWalls(context is Context, sheetMetalEntities is Query) returns Query
{
    const wallFaces = qSheetMetalWallFaces(context, sheetMetalEntities);
    const evaluatedWalls = evaluateQuery(context, wallFaces);
    var planarWalls = [];
    
    for (var face in evaluatedWalls)
    {
        const surface = evSurfaceDefinition(context, { "face" : face });
        if (surface is Plane)
        {
            planarWalls = append(planarWalls, face);
        }
    }
    
    return qUnion(planarWalls);
}

/**
 * Query all cylindrical wall faces (rolled walls) in the specified sheet metal entities.
 * Returns wall faces that are cylindrical surfaces.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntities {Query} : The sheet metal entities to search within
 * 
 * @returns {Query} : A query for all cylindrical wall faces
 */
export function qSheetMetalCylindricalWalls(context is Context, sheetMetalEntities is Query) returns Query
{
    const wallFaces = qSheetMetalWallFaces(context, sheetMetalEntities);
    const evaluatedWalls = evaluateQuery(context, wallFaces);
    var cylindricalWalls = [];
    
    for (var face in evaluatedWalls)
    {
        const surface = evSurfaceDefinition(context, { "face" : face });
        if (surface is Cylinder)
        {
            cylindricalWalls = append(cylindricalWalls, face);
        }
    }
    
    return qUnion(cylindricalWalls);
}

/**
 * Query adjacent wall faces to a given wall or set of walls.
 * Returns wall faces that share a bend edge with the input walls.
 *
 * @param context {Context} : The context of the current part studio
 * @param wallFaces {Query} : The wall faces to find adjacent walls for
 * 
 * @returns {Query} : A query for all adjacent wall faces
 */
export function qSheetMetalAdjacentWalls(context is Context, wallFaces is Query) returns Query
{
    const definitionEntities = getSMDefinitionEntities(context, wallFaces, EntityType.FACE);
    const definitionQuery = qUnion(definitionEntities);
    
    // Get edges adjacent to the walls
    const adjacentEdges = qAdjacent(definitionQuery, AdjacencyType.EDGE, EntityType.EDGE);
    
    // Get faces adjacent to those edges
    const adjacentFaces = qAdjacent(adjacentEdges, AdjacencyType.EDGE, EntityType.FACE);
    
    // Filter to only wall faces and exclude the original input
    const adjacentWalls = qSubtraction(
        qIntersection([adjacentFaces, qSheetMetalWallFaces(context, qOwnerBody(definitionQuery))]),
        definitionQuery
    );
    
    return adjacentWalls;
}

/**
 * Query bend edges connecting two specific walls.
 * Returns bend edges that are shared between the two wall queries.
 *
 * @param context {Context} : The context of the current part studio
 * @param wallFaces1 {Query} : The first set of wall faces
 * @param wallFaces2 {Query} : The second set of wall faces
 * 
 * @returns {Query} : A query for bend edges connecting the two walls
 */
export function qSheetMetalBendsBetweenWalls(context is Context, wallFaces1 is Query, wallFaces2 is Query) returns Query
{
    const definitionEntities1 = getSMDefinitionEntities(context, wallFaces1, EntityType.FACE);
    const definitionEntities2 = getSMDefinitionEntities(context, wallFaces2, EntityType.FACE);
    
    if (size(definitionEntities1) == 0 || size(definitionEntities2) == 0)
    {
        return qNothing();
    }
    
    const definitionQuery1 = qUnion(definitionEntities1);
    const definitionQuery2 = qUnion(definitionEntities2);
    
    // Get edges adjacent to first wall set
    const edges1 = qAdjacent(definitionQuery1, AdjacencyType.EDGE, EntityType.EDGE);
    
    // Get edges adjacent to second wall set
    const edges2 = qAdjacent(definitionQuery2, AdjacencyType.EDGE, EntityType.EDGE);
    
    // Find the intersection of these edge sets
    const sharedEdges = qIntersection([edges1, edges2]);
    
    // Filter to only bend edges
    const bendEdges = qSheetMetalBendEdges(context, qOwnerBody(definitionQuery1));
    
    return qIntersection([sharedEdges, bendEdges]);
}

/**
 * Query all edges of a specific joint type (BEND, RIP, or TANGENT).
 * Returns edges that have the specified joint type attribute.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntities {Query} : The sheet metal entities to search within
 * @param jointType {SMJointType} : The type of joint to query for
 * 
 * @returns {Query} : A query for all edges with the specified joint type
 */
export function qSheetMetalEdgesByJointType(context is Context, sheetMetalEntities is Query, jointType is SMJointType) returns Query
{
    const definitionEntities = getSMDefinitionEntities(context, sheetMetalEntities, EntityType.EDGE);
    var matchingEdges = [];
    
    for (var edge in definitionEntities)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.jointType != undefined && attribute.jointType.value == jointType)
            {
                matchingEdges = append(matchingEdges, edge);
                break;
            }
        }
    }
    
    return qUnion(matchingEdges);
}

/**
 * Query all bend edges with a specific bend type (STANDARD, etc.).
 * Returns bend edges that have the specified bend type attribute.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalEntities {Query} : The sheet metal entities to search within
 * @param bendType {SMBendType} : The type of bend to query for
 * 
 * @returns {Query} : A query for all bend edges with the specified bend type
 */
export function qSheetMetalBendsByType(context is Context, sheetMetalEntities is Query, bendType is SMBendType) returns Query
{
    const bendEdges = qSheetMetalBendEdges(context, sheetMetalEntities);
    const evaluatedBends = evaluateQuery(context, bendEdges);
    var matchingBends = [];
    
    for (var edge in evaluatedBends)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.bendType != undefined && attribute.bendType.value == bendType)
            {
                matchingBends = append(matchingBends, edge);
                break;
            }
        }
    }
    
    return qUnion(matchingBends);
}

/**
 * Query boundary edges of sheet metal walls (edges not shared with bends).
 * Returns edges that belong to walls but are not bend edges.
 *
 * @param context {Context} : The context of the current part studio
 * @param wallFaces {Query} : The wall faces to find boundary edges for
 * 
 * @returns {Query} : A query for all boundary edges
 */
export function qSheetMetalBoundaryEdges(context is Context, wallFaces is Query) returns Query
{
    const definitionEntities = getSMDefinitionEntities(context, wallFaces, EntityType.FACE);
    const definitionQuery = qUnion(definitionEntities);
    
    // Get all edges adjacent to the walls
    const allWallEdges = qAdjacent(definitionQuery, AdjacencyType.EDGE, EntityType.EDGE);
    
    // Get all bend edges
    const bendEdges = qSheetMetalBendEdges(context, qOwnerBody(definitionQuery));
    
    // Subtract bend edges from all wall edges to get boundary edges
    return qSubtraction(allWallEdges, bendEdges);
}

