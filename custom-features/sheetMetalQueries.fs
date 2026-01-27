FeatureScript 2856;
// Sheet Metal Query Helper Functions
// This module provides robust helper functions for querying sheet metal entities
// that are not trivial to select in standard features.

import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/query.fs", version : "2856.0");
import(path : "onshape/std/evaluate.fs", version : "2856.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2856.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2856.0");
import(path : "onshape/std/smobjecttype.gen.fs", version : "2856.0");
import(path : "onshape/std/smjointtype.gen.fs", version : "2856.0");
import(path : "onshape/std/smjointstyle.gen.fs", version : "2856.0");
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
    const definitionEntities = getSMDefinitionEntities(context, sheetMetalEntities, EntityType.FACE);
    var planarWalls = [];
    
    for (var face in definitionEntities)
    {
        if (hasSheetMetalAttribute(context, face, SMObjectType.WALL))
        {
            const surface = evSurfaceDefinition(context, { "face" : face });
            if (surface is Plane)
            {
                planarWalls = append(planarWalls, face);
            }
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
    const definitionEntities = getSMDefinitionEntities(context, sheetMetalEntities, EntityType.FACE);
    var cylindricalWalls = [];
    
    for (var face in definitionEntities)
    {
        if (hasSheetMetalAttribute(context, face, SMObjectType.WALL))
        {
            const surface = evSurfaceDefinition(context, { "face" : face });
            if (surface is Cylinder)
            {
                cylindricalWalls = append(cylindricalWalls, face);
            }
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
    const definitionEntities = getSMDefinitionEntities(context, sheetMetalEntities, EntityType.EDGE);
    var matchingBends = [];
    
    for (var edge in definitionEntities)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.jointType != undefined && 
                attribute.jointType.value == SMJointType.BEND &&
                attribute.bendType != undefined && 
                attribute.bendType.value == bendType)
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

/**
 * Query all joint faces (bend cylindrical faces) on solid sheet metal bodies.
 * When a solid active sheet metal body is selected, this returns the cylindrical
 * faces that correspond to bends in the sheet metal model.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalBody {Query} : The solid sheet metal body to search within
 * 
 * @returns {Query} : A query for all joint faces on the solid body
 */
export function qSheetMetalJointFaces(context is Context, sheetMetalBody is Query) returns Query
{
    // Get all faces owned by the solid body
    const allFaces = qOwnedByBody(sheetMetalBody, EntityType.FACE);
    
    // Get the definition entities (edges) that correspond to the solid body
    const definitionEdges = getSMDefinitionEntities(context, sheetMetalBody, EntityType.EDGE);
    
    // Find which definition edges are bend edges
    var bendEdges = [];
    for (var edge in definitionEdges)
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
    
    // Now find the cylindrical faces on the solid body that correspond to these bend edges
    // Cylindrical faces on sheet metal solids represent the bends
    const cylindricalFaces = qGeometry(allFaces, GeometryType.CYLINDER);
    
    // Filter to only those that have association attributes matching bend edges
    const evaluatedCylinderFaces = evaluateQuery(context, cylindricalFaces);
    var jointFaces = [];
    
    for (var face in evaluatedCylinderFaces)
    {
        // Get the association attribute for this face
        const associations = getSMAssociationAttributes(context, face);
        
        // Check if any association links to a bend edge
        for (var assoc in associations)
        {
            const linkedDefinitionEntities = evaluateQuery(context, qAttributeQuery(assoc));
            for (var defEntity in linkedDefinitionEntities)
            {
                // Check if this definition entity is in our bend edges list
                for (var bendEdge in bendEdges)
                {
                    if (defEntity == bendEdge)
                    {
                        jointFaces = append(jointFaces, face);
                        break;
                    }
                }
            }
        }
    }
    
    return qUnion(jointFaces);
}

/**
 * Query joint faces on solid sheet metal bodies filtered by joint type.
 * Returns cylindrical faces corresponding to bends, rips, or tangent joints.
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalBody {Query} : The solid sheet metal body to search within
 * @param jointType {SMJointType} : The type of joint to filter by (BEND, RIP, TANGENT)
 * 
 * @returns {Query} : A query for joint faces of the specified type
 */
export function qSheetMetalJointFacesByType(context is Context, sheetMetalBody is Query, jointType is SMJointType) returns Query
{
    // Get all faces owned by the solid body
    const allFaces = qOwnedByBody(sheetMetalBody, EntityType.FACE);
    
    // Get the definition entities (edges) that correspond to the solid body
    const definitionEdges = getSMDefinitionEntities(context, sheetMetalBody, EntityType.EDGE);
    
    // Find which definition edges match the specified joint type
    var matchingEdges = [];
    for (var edge in definitionEdges)
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
    
    if (size(matchingEdges) == 0)
    {
        return qNothing();
    }
    
    // For BEND joints, find cylindrical faces on the solid body
    if (jointType == SMJointType.BEND)
    {
        const cylindricalFaces = qGeometry(allFaces, GeometryType.CYLINDER);
        const evaluatedCylinderFaces = evaluateQuery(context, cylindricalFaces);
        var jointFaces = [];
        
        for (var face in evaluatedCylinderFaces)
        {
            const associations = getSMAssociationAttributes(context, face);
            
            for (var assoc in associations)
            {
                const linkedDefinitionEntities = evaluateQuery(context, qAttributeQuery(assoc));
                for (var defEntity in linkedDefinitionEntities)
                {
                    for (var matchingEdge in matchingEdges)
                    {
                        if (defEntity == matchingEdge)
                        {
                            jointFaces = append(jointFaces, face);
                            break;
                        }
                    }
                }
            }
        }
        
        return qUnion(jointFaces);
    }
    
    // For RIP and TANGENT joints, the joint is represented by the planar faces meeting at an edge
    // Return the faces adjacent to the matching edges
    var adjacentFaces = [];
    for (var matchingEdge in matchingEdges)
    {
        // Get solid faces that correspond to this definition edge
        const associations = getSMAssociationAttributes(context, matchingEdge);
        for (var assoc in associations)
        {
            const linkedSolidEntities = evaluateQuery(context, qIntersection([
                qBodyType(qAttributeQuery(assoc), BodyType.SOLID),
                qEntityFilter(qAttributeQuery(assoc), EntityType.FACE)
            ]));
            for (var solidFace in linkedSolidEntities)
            {
                adjacentFaces = append(adjacentFaces, solidFace);
            }
        }
    }
    
    return qUnion(adjacentFaces);
}

/**
 * Query joint faces on solid sheet metal bodies filtered by joint style.
 * This applies primarily to RIP joints which can have different styles (EDGE, OVERLAP, etc.).
 *
 * @param context {Context} : The context of the current part studio
 * @param sheetMetalBody {Query} : The solid sheet metal body to search within
 * @param jointStyle {SMJointStyle} : The style of joint to filter by
 * 
 * @returns {Query} : A query for joint faces of the specified style
 */
export function qSheetMetalJointFacesByStyle(context is Context, sheetMetalBody is Query, jointStyle is SMJointStyle) returns Query
{
    // Get all faces owned by the solid body
    const allFaces = qOwnedByBody(sheetMetalBody, EntityType.FACE);
    
    // Get the definition entities (edges) that correspond to the solid body
    const definitionEdges = getSMDefinitionEntities(context, sheetMetalBody, EntityType.EDGE);
    
    // Find which definition edges match the specified joint style
    var matchingEdges = [];
    for (var edge in definitionEdges)
    {
        const attributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
        for (var attribute in attributes)
        {
            if (attribute.jointStyle != undefined && attribute.jointStyle.value == jointStyle)
            {
                matchingEdges = append(matchingEdges, edge);
                break;
            }
        }
    }
    
    if (size(matchingEdges) == 0)
    {
        return qNothing();
    }
    
    // Get solid faces that correspond to these definition edges
    var adjacentFaces = [];
    for (var matchingEdge in matchingEdges)
    {
        const associations = getSMAssociationAttributes(context, matchingEdge);
        for (var assoc in associations)
        {
            const linkedSolidEntities = evaluateQuery(context, qIntersection([
                qBodyType(qAttributeQuery(assoc), BodyType.SOLID),
                qEntityFilter(qAttributeQuery(assoc), EntityType.FACE)
            ]));
            for (var solidFace in linkedSolidEntities)
            {
                adjacentFaces = append(adjacentFaces, solidFace);
            }
        }
    }
    
    return qUnion(adjacentFaces);
}

