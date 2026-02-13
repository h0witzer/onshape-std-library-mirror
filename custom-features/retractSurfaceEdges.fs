FeatureScript 2878;
import(path : "onshape/std/geometry.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/valueBounds.fs", version : "2878.0");
import(path : "onshape/std/geomOperations.fs", version : "2878.0");

/**
 * Feature to retract surface edges by a specified distance.
 * Works with both one-sided edges (sheet boundaries) and two-sided edges (internal edges).
 * For two-sided edges, the retraction is applied to both adjacent faces, creating an opening.
 */
annotation { "Feature Type Name" : "Retract Surface Edges" }
export const retractSurfaceEdges = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edges to retract", "Filter" : EntityType.EDGE }
        definition.edgesToRetract is Query;
        
        annotation { "Name" : "Retraction distance" }
        isLength(definition.retractionDistance, NONNEGATIVE_LENGTH_BOUNDS);
    }
    {
        // Get edges for retraction operation
        var edgesToRetract = getEdgesForRetraction(context, definition.edgesToRetract);
        
        if (size(edgesToRetract) == 0)
        {
            throw regenError("No valid edges found for retraction. Ensure the selected entities have edges.");
        }
        
        // Build edge change options for all edges and their adjacent faces
        var edgeChangeOptions = [];
        for (var edgeInfo in edgesToRetract)
        {
            // For each edge, get all adjacent faces
            var adjacentFaces = evaluateQuery(context, qAdjacent(edgeInfo.edge, AdjacencyType.EDGE, EntityType.FACE));
            
            // Apply edge change to each adjacent face
            // For two-sided edges, this creates an opening between both surfaces
            for (var face in adjacentFaces)
            {
                edgeChangeOptions = append(edgeChangeOptions, {
                    "edge" : edgeInfo.edge,
                    "face" : face,
                    "offset" : -definition.retractionDistance
                });
            }
        }
        
        opEdgeChange(context, id + "edgeChange", { "edgeChangeOptions" : edgeChangeOptions });
    });

/**
 * Helper function to get edges for retraction operation.
 * Extracts edges from the query, supporting both one-sided and two-sided edges.
 * 
 * @param context : The context to evaluate queries in
 * @param entities : Query for edges or sheet bodies to retract
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
