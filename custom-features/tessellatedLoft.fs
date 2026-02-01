FeatureScript 2878;
// This is a standalone tessellated loft feature based on sheetMetalLoft.fs
// Stripped of all sheet metal specific logic to allow experimentation with the underlying tessellated loft operation.

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/containers.fs", version : "2878.0");
import(path : "onshape/std/curveGeometry.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/geomOperations.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/string.fs", version : "2878.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2878.0");
import(path : "onshape/std/topologyUtils.fs", version : "2878.0");
import(path : "onshape/std/valueBounds.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");
import(path : "onshape/std/debug.fs", version : "2878.0");

const CHORDAL_BOUNDS = {
            (meter) : [0.00001, 0.001, 0.1],
            (centimeter) : 0.1,
            (millimeter) : 1.0,
            (inch) : 0.05,
            (foot) : 0.005,
            (yard) : 0.001
        } as LengthBoundSpec;

/**
 * Create a tessellated loft between two profiles without sheet metal specific logic.
 * This feature is intended for experimentation with the underlying opTessellatedLoft operation.
 * Adjust chordal tolerance to change loft resolution.
 */
annotation { "Feature Type Name" : "Tessellated Loft" }
export const tessellatedLoft = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Profile 1",
                    "Filter" : ((EntityType.EDGE || EntityType.FACE || (EntityType.BODY && (BodyType.WIRE || BodyType.SHEET) && SketchObject.NO)) && ConstructionObject.NO)
                    || EntityType.VERTEX,
                    "AdditionalBoxSelectFilter" : (EntityType.EDGE && !EntityType.BODY) }
        definition.profile1 is Query;
        
        annotation { "Name" : "Profile 2",
                    "Filter" : ((EntityType.EDGE || EntityType.FACE || (EntityType.BODY && (BodyType.WIRE || BodyType.SHEET) && SketchObject.NO)) && ConstructionObject.NO)
                    || EntityType.VERTEX,
                    "AdditionalBoxSelectFilter" : (EntityType.EDGE && !EntityType.BODY) }
        definition.profile2 is Query;
        
        annotation { "Name" : "Chordal tolerance" }
        isLength(definition.chordalTolerance, CHORDAL_BOUNDS);
        
        annotation { "Name" : "Match connections" }
        definition.matchConnections is boolean;
        
        if (definition.matchConnections)
        {
            annotation { "Group Name" : "Connections", "Driving Parameter" : "matchConnections", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Match connections", "Item name" : "connection", "UIHint" : UIHint.FOCUS_INNER_QUERY,
                            "Driven query" : "connectionEntities", "Item label template" : "#connectionEntities" }
                definition.connections is array;
                for (var connection in definition.connections)
                {
                    annotation { "Name" : "Vertices or edges",
                                "Filter" : (EntityType.EDGE && ConstructionObject.NO) || (EntityType.VERTEX) }
                    connection.connectionEntities is Query;
                    
                    annotation { "Name" : "Rip", "Default" : false }
                    connection.isRip is boolean;
                    
                    annotation { "Name" : "Edge queries", "UIHint" : UIHint.ALWAYS_HIDDEN }
                    connection.connectionEdgeQueries is Query;
                    
                    annotation { "Name" : "Edge parameters", "UIHint" : UIHint.ALWAYS_HIDDEN }
                    isAnything(connection.connectionEdgeParameters);
                }
            }
        }
    }
    {
        // Validate profiles are selected
        if (isQueryEmpty(context, definition.profile1))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["profile1"]);
        }
        if (isQueryEmpty(context, definition.profile2))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["profile2"]);
        }
        
        // Check that profiles don't intersect
        const distanceBetweenProfiles = evDistance(context, { 
            "side0" : definition.profile1, 
            "side1" : definition.profile2,
            "arcLengthParameterization" : false 
        });
        
        if (distanceBetweenProfiles.distance < TOLERANCE.zeroLength * meter)
        {
            throw regenError(ErrorStringEnum.LOFT_SELECT_PROFILES);
        }
        
        // Prepare profile subqueries by extracting edges and vertices
        definition.profileSubqueries = [
            getProfileEdgesAndVertices(definition.profile1), 
            getProfileEdgesAndVertices(definition.profile2)
        ];
        
        // Pack definition with connections if needed
        definition = packDefinition(context, definition);
        
        // Get automatic matches for the profiles
        const matchOutput = evTessellatedLoftMatches(context, definition);
        
        // Convert matches to connections format for opTessellatedLoft
        definition.connections = convertMatchesToConnections(matchOutput.matches);
        
        // Execute the tessellated loft operation
        try
        {
            opTessellatedLoft(context, id + "loft", definition);
        }
        catch (error)
        {
            // Highlight created geometry in case of error for debugging
            setErrorEntities(context, id, {
                "entities" : qCreatedBy(id + "loft", EntityType.FACE),
                "color" : DebugColor.YELLOW
            });
            throw error;
        }
    },
    {
        "matchConnections" : false,
        "connections" : [],
        "chordalTolerance" : 0.001 * meter
    });

/**
 * Extract edges and vertices from a profile query for use with opTessellatedLoft.
 * Handles edges, faces, wire bodies, sheet bodies, and point bodies.
 *
 * @param profile : The profile query to extract edges and vertices from
 * @returns Query containing all edges and vertices that define the profile
 */
function getProfileEdgesAndVertices(profile is Query) returns Query
{
    return qUnion([
        qEntityFilter(profile, EntityType.EDGE),
        qEntityFilter(profile, EntityType.VERTEX),
        qEntityFilter(profile, EntityType.FACE)->qAdjacent(AdjacencyType.EDGE, EntityType.EDGE),
        qEntityFilter(profile, EntityType.BODY)->qBodyType(BodyType.WIRE)->qOwnedByBody(EntityType.EDGE),
        qEntityFilter(profile, EntityType.BODY)->qBodyType(BodyType.SHEET)->qOwnedByBody(EntityType.EDGE)->qEdgeTopologyFilter(EdgeTopology.ONE_SIDED),
        qEntityFilter(profile, EntityType.BODY)->qBodyType(BodyType.POINT)->qOwnedByBody(EntityType.VERTEX)
    ]);
}

/**
 * Pack the definition with processed connection data for opTessellatedLoft.
 * Converts connection entities to individual edge queries and validates parameters.
 *
 * @param context : The context of the operation
 * @param definition : The feature definition map
 * @returns The updated definition map with processed connections
 */
function packDefinition(context is Context, definition is map) returns map
{
    if (!definition.matchConnections)
    {
        definition.connections = [];
        return definition;
    }
    
    for (var connectionIndex = 0; connectionIndex < size(definition.connections); connectionIndex += 1)
    {
        // opTessellatedLoft expects an array of individual connection edge queries
        definition.connections[connectionIndex].connectionEdges =
            evaluateQuery(context, qEntityFilter(definition.connections[connectionIndex].connectionEdgeQueries, EntityType.EDGE));

        // Validate that edge parameters match the number of edges
        const hasEdgeParameterMismatch = size(definition.connections[connectionIndex].connectionEdges)
            != size(definition.connections[connectionIndex].connectionEdgeParameters);
        if (hasEdgeParameterMismatch)
        {
            throw regenError(ErrorStringEnum.LOFT_CONNECTION_MATCHING, ["connections[" ~ connectionIndex ~ "].connectionEntities"]);
        }
        
        definition.connections[connectionIndex].removeRedundantEdge = !definition.connections[connectionIndex].isRip;
    }
    
    return definition;
}

/**
 * Convert automatic match output from evTessellatedLoftMatches to connection format.
 * Each match becomes a connection that defines how the two profiles should be aligned.
 *
 * @param matches : Array of match maps from evTessellatedLoftMatches
 * @returns Array of connection maps suitable for opTessellatedLoft
 */
function convertMatchesToConnections(matches is array) returns array
{
    var connections = [];
    
    for (var match in matches)
    {
        connections = append(connections, createConnectionFromMatch(match));
    }
    
    return connections;
}

/**
 * Create a connection map from a single match result.
 * Processes both vertices and edges with their parameters.
 *
 * @param match : A single match map from evTessellatedLoftMatches
 * @returns A connection map for opTessellatedLoft
 */
function createConnectionFromMatch(match is map) returns map
{
    var collectedMatchItems = { 
        "connectionEntities" : [], 
        "connectionEdgeQueries" : [], 
        "connectionEdgeParameters" : [] 
    };
    
    collectedMatchItems = collectMatchItems(collectedMatchItems, match.match[0]);
    collectedMatchItems = collectMatchItems(collectedMatchItems, match.match[1]);
    
    return {
        "isRip" : false,
        "connectionEntities" : qUnion(collectedMatchItems.connectionEntities),
        "connectionEdgeQueries" : qUnion(collectedMatchItems.connectionEdgeQueries),
        "connectionEdgeParameters" : collectedMatchItems.connectionEdgeParameters,
        "connectionEdges" : collectedMatchItems.connectionEdgeQueries,
        "removeRedundantEdge" : match.removeRedundantEdge
    };
}

/**
 * Collect match item data (vertex or edge with parameter) into the connection data structure.
 *
 * @param connection : The connection data being built
 * @param matchItem : A match item containing either a vertex or edge with parameter
 * @returns Updated connection data with the match item added
 */
function collectMatchItems(connection is map, matchItem is map) returns map
{
    if (matchItem.vertex != undefined)
    {
        connection.connectionEntities = append(connection.connectionEntities, matchItem.vertex);
    }
    else
    {
        connection.connectionEntities = append(connection.connectionEntities, matchItem.edge);
        connection.connectionEdgeQueries = append(connection.connectionEdgeQueries, matchItem.edge);
        connection.connectionEdgeParameters = append(connection.connectionEdgeParameters, clamp(matchItem.parameter, 0, 1));
    }
    
    return connection;
}
