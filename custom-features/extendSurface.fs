FeatureScript 2878;
import(path : "onshape/std/geometry.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/valueBounds.fs", version : "2878.0");
import(path : "onshape/std/geomOperations.fs", version : "2878.0");

export enum ExtendTypeEnum
{
    annotation { "Name" : "Extend Full Surface by Distance" }
    EXTEND_BY_DISTANCE_FULL,
    annotation { "Name" : "Extend Edges by Distance" }
    EXTEND_BY_DISTANCE_EDGES
    
    // ,annotation { "Name" : "Extend Edges to Surface" }
    // EXTEND_UP_TO_SURFACE
}


annotation { "Feature Type Name" : "Extend Surface" }
export const extendSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Extend Type" }
        definition.extendType is ExtendTypeEnum;

        if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_FULL)
        {
            annotation { "Name" : "Surface to Extend", "Filter" : (EntityType.BODY && BodyType.SHEET), "MaxNumberOfPicks" : 1 }
            definition.surfaceToExtend is Query;
        }

        if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_EDGES)
        {
            annotation { "Name" : "Edges to Extend", "Filter" : EntityType.EDGE}
            definition.extendEdges is Query;
        }
        
        // if (definition.extendType == ExtendTypeEnum.EXTEND_UP_TO_SURFACE)
        // {
        //     annotation { "Name" : "Edges to Extend", "Filter" : EntityType.EDGE }
        //     definition.extendToSurfaceEdges is Query;
        //     annotation { "Name" : "Limit Surface", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        //     definition.limitSurface is Query;
            
        // }
        
        if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_FULL || definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_EDGES)
        {
             annotation { "Name" : "Extend Distance" }
        isLength(definition.extendDistance, LENGTH_BOUNDS);
        }

    }
    {
        // Determine which entities to work with based on extend type
        var entities is Query;
        if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_FULL)
        {
            entities = definition.surfaceToExtend;
        }
        else if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_EDGES)
        {
            entities = definition.extendEdges;
        }

        // Handle positive distances (extension) and negative distances (retraction) differently
        if (definition.extendDistance >= 0)
        {
            // Positive distance: use opExtendSheetBody for extension
            var extendDefinition = {
                "extendMethod" : "EXTEND_BY_DISTANCE",
                "entities" : entities,
                "distance" : definition.extendDistance
            };
            opExtendSheetBody(context, id, extendDefinition);
        }
        else
        {
            // Negative distance: use opEdgeChange for retraction
            var edgesToRetract = getEdgesForRetraction(context, entities);
            
            if (size(edgesToRetract) == 0)
            {
                throw regenError("No valid edges found for retraction. Ensure the selected entities have boundary edges.");
            }
            
            var edgeChangeOptions = [];
            for (var edge in edgesToRetract)
            {
                edgeChangeOptions = append(edgeChangeOptions, {
                    "edge" : edge,
                    "face" : qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE),
                    "offset" : definition.extendDistance
                });
            }
            
            opEdgeChange(context, id + "edgeChange", { "edgeChangeOptions" : edgeChangeOptions });
        }
    });

/**
 * Helper function to get edges for retraction operation.
 * Extracts edges from the entities query, filtering for one-sided edges.
 * 
 * @param context : The context to evaluate queries in
 * @param entities : Query for either edges or sheet bodies to retract
 * @returns : Array of edge queries suitable for retraction
 */
function getEdgesForRetraction(context is Context, entities is Query) returns array
{
    // Get edges from the query - either directly selected edges or edges owned by selected bodies
    var selectedEdges = qEntityFilter(entities, EntityType.EDGE);
    var bodyEdges = qOwnedByBody(entities, EntityType.EDGE);
    var allEdges = qUnion([selectedEdges, bodyEdges]);
    
    // Filter for one-sided edges (sheet body boundaries)
    var oneSidedEdges = qEdgeTopologyFilter(allEdges, EdgeTopology.ONE_SIDED);
    
    // Evaluate and return as array
    return evaluateQuery(context, oneSidedEdges);
}
