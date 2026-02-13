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


annotation { "Feature Type Name" : "Modify Surface Edges" }
export const modifyEdges = defineFeature(function(context is Context, id is Id, definition is map)
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
        var entities;
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
                throw regenError("No valid edges found for retraction. Ensure the selected entities have edges.");
            }
            
            var edgeChangeOptions = [];
            for (var edgeInfo in edgesToRetract)
            {
                // For each edge, get all adjacent faces
                var adjacentFaces = evaluateQuery(context, qAdjacent(edgeInfo.edge, AdjacencyType.EDGE, EntityType.FACE));
                
                // Apply edge change to each adjacent face
                for (var face in adjacentFaces)
                {
                    edgeChangeOptions = append(edgeChangeOptions, {
                        "edge" : edgeInfo.edge,
                        "face" : face,
                        "offset" : definition.extendDistance
                    });
                }
            }
            
            opEdgeChange(context, id + "edgeChange", { "edgeChangeOptions" : edgeChangeOptions });
        }
    });

/**
 * Helper function to get edges for retraction operation.
 * Extracts edges from the entities query, now supporting both one-sided and two-sided edges.
 * 
 * @param context : The context to evaluate queries in
 * @param entities : Query for either edges or sheet bodies to modify
 * @returns : Array of maps with edge information, each containing an "edge" field
 */
function getEdgesForRetraction(context is Context, entities is Query) returns array
{
    // Get edges from the query - either directly selected edges or edges owned by selected bodies
    var selectedEdges = qEntityFilter(entities, EntityType.EDGE);
    var bodyEdges = qOwnedByBody(entities, EntityType.EDGE);
    var allEdges = qUnion([selectedEdges, bodyEdges]);
    
    // Get both one-sided edges (sheet body boundaries) and two-sided edges (internal edges)
    var oneSidedEdges = qEdgeTopologyFilter(allEdges, EdgeTopology.ONE_SIDED);
    var twoSidedEdges = qEdgeTopologyFilter(allEdges, EdgeTopology.TWO_SIDED);
    var relevantEdges = qUnion([oneSidedEdges, twoSidedEdges]);
    
    // Evaluate and return as array of edge info maps
    var edgeArray = evaluateQuery(context, relevantEdges);
    var result = [];
    for (var edge in edgeArray)
    {
        result = append(result, { "edge" : edge });
    }
    return result;
}
