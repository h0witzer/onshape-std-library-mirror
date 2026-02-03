FeatureScript 2878;

/**
 * Minimal tester to understand assignSMAttributesToNewOrSplitEntities
 * 
 * Goal: Take a sheet metal joint edge and split it into 2 segments,
 * maintaining the same attribute type on both segments.
 */

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/context.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/geomOperations.fs", version : "2878.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2878.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2878.0");
import(path : "onshape/std/curveGeometry.fs", version : "2878.0");
import(path : "onshape/std/debug.fs", version : "2878.0");

annotation { "Feature Type Name" : "SM Split Edge Tester" }
export const smSplitEdgeTester = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edge to split",
                     "Filter" : (SheetMetalDefinitionEntityType.EDGE) && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES,
                     "MaxNumberOfPicks" : 1 }
        definition.edge is Query;
    }
    {
        println("=== SM SPLIT EDGE TESTER ===");
        
        // Validate input
        if (!areEntitiesFromSingleActiveSheetMetalModel(context, definition.edge))
        {
            throw regenError("Edge must be from an active sheet metal model");
        }
        
        // Get the joint definition entity
        var jointEntity = findJointDefinitionEntity(context, definition.edge, EntityType.EDGE);
        if (jointEntity == undefined)
        {
            throw regenError("Not a valid sheet metal joint edge");
        }
        
        // Get existing attribute
        var existingAttribute = getJointAttribute(context, jointEntity);
        if (existingAttribute == undefined)
        {
            throw regenError("Edge has no joint attribute");
        }
        
        println("=== INITIAL STATE ===");
        println("Joint type: " ~ existingAttribute.jointType.value);
        
        // Get the edge and its geometry
        const selectedEdges = evaluateQuery(context, qEntityFilter(jointEntity, EntityType.EDGE));
        if (size(selectedEdges) != 1)
        {
            throw regenError("Expected exactly one edge");
        }
        
        const edgeQuery = qUnion([selectedEdges[0]]);
        const edgeLength = evLength(context, { "entities" : edgeQuery });
        
        println("Edge length: " ~ edgeLength);
        
        // STEP 1: Capture initial state BEFORE splitting
        const modelBodyQuery = qOwnerBody(jointEntity);
        const initialData = getInitialEntitiesAndAttributes(context, modelBodyQuery);
        
        println("Initial entities count: " ~ size(initialData.originalEntities));
        println("Initial association attributes count: " ~ size(initialData.initialAssociationAttributes));
        
        // STEP 2: Split the edge at midpoint
        const splitParam = 0.5;  // Split at middle
        
        println("=== SPLITTING EDGE AT MIDPOINT ===");
        opSplitEdges(context, id + "split", {
            "edges" : edgeQuery,
            "parameters" : [splitParam]
        });
        
        // Track the edges through the split
        const trackedEdges = startTracking(context, edgeQuery);
        const edgesAfterSplit = qEntityFilter(qUnion([edgeQuery, trackedEdges]), EntityType.EDGE);
        const splitEdgesEval = evaluateQuery(context, edgesAfterSplit);
        
        println("Edges after split: " ~ size(splitEdgesEval));
        
        // Check attributes on split edges BEFORE calling assignSMAttributesToNewOrSplitEntities
        println("=== ATTRIBUTES BEFORE assignSMAttributesToNewOrSplitEntities ===");
        for (var i = 0; i < size(splitEdgesEval); i += 1)
        {
            const segEdgeQ = qUnion([splitEdgesEval[i]]);
            const assocAttrs = try silent(getSMAssociationAttributes(context, segEdgeQ));
            const defAttr = try silent(getJointAttribute(context, segEdgeQ));
            println("Segment " ~ i ~ " transient ID: " ~ splitEdgesEval[i]);
            println("  Association attrs: " ~ (assocAttrs == undefined ? "NONE" : size(assocAttrs)));
            println("  Definition attr type: " ~ (defAttr == undefined ? "NONE" : defAttr.jointType.value));
        }
        
        // STEP 3: Call assignSMAttributesToNewOrSplitEntities
        println("=== CALLING assignSMAttributesToNewOrSplitEntities ===");
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, modelBodyQuery, initialData, id + "attribution");
        
        println("toUpdate.modifiedEntities: " ~ toUpdate.modifiedEntities);
        const modifiedEntitiesEval = evaluateQuery(context, toUpdate.modifiedEntities);
        println("Number of modified entities: " ~ size(modifiedEntitiesEval));
        println("Deleted attributes count: " ~ size(toUpdate.deletedAttributes));
        
        // Check attributes AFTER calling assignSMAttributesToNewOrSplitEntities
        println("=== ATTRIBUTES AFTER assignSMAttributesToNewOrSplitEntities ===");
        for (var i = 0; i < size(splitEdgesEval); i += 1)
        {
            const segEdgeQ = qUnion([splitEdgesEval[i]]);
            const assocAttrs = try silent(getSMAssociationAttributes(context, segEdgeQ));
            const defAttr = try silent(getJointAttribute(context, segEdgeQ));
            println("Segment " ~ i ~ " transient ID: " ~ splitEdgesEval[i]);
            println("  Association attrs: " ~ (assocAttrs == undefined ? "NONE" : size(assocAttrs)));
            println("  Definition attr type: " ~ (defAttr == undefined ? "NONE" : defAttr.jointType.value));
        }
        
        // Visual debug - color segments
        if (size(splitEdgesEval) >= 2)
        {
            debug(context, qUnion([splitEdgesEval[0]]), DebugColor.GREEN);
            debug(context, qUnion([splitEdgesEval[1]]), DebugColor.BLUE);
        }
        
        // STEP 4: Update sheet metal geometry
        println("=== CALLING updateSheetMetalGeometry ===");
        
        // Create fresh query with body ownership
        const splitEdgesFromModelBody = qIntersection([
            qOwnedByBody(modelBodyQuery),
            qUnion(splitEdgesEval)
        ]);
        
        updateSheetMetalGeometry(context, id, {
            "entities" : splitEdgesFromModelBody,
            "deletedAttributes" : toUpdate.deletedAttributes,
            "associatedChanges" : splitEdgesFromModelBody
        });
        
        println("=== COMPLETE ===");
    }, {});  // Empty defaults map for defineSheetMetalFeature

/**
 * Find the joint definition entity for a given selection
 */
function findJointDefinitionEntity(context is Context, entity is Query, entityType is EntityType)
{
    const entityQ = qUnion(getSMDefinitionEntities(context, entity));
    var sheetEntities = qEntityFilter(entityQ, entityType);
    if (size(evaluateQuery(context, sheetEntities)) != 1)
    {
        return undefined;
    }
    return sheetEntities;
}
