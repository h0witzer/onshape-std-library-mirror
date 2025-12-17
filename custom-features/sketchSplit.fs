FeatureScript 2770;
import(path : "onshape/std/common.fs", version : "2770.0");

/**
 * Split with Sketch Feature
 * 
 * Splits target bodies using sketch edges by extruding each edge through all
 * and using the resulting surfaces as split tools. Supports multiple disconnected
 * sketch lines with automatic cleanup of failed split operations.
 */
annotation { "Feature Type Name" : "Split with Sketch", "Feature Type Description" : "Use sketches to split entities" }
export const splitSketch= defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Parts to Split", "Filter" : EntityType.BODY, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
        definition.partsToSplit is Query;

        annotation { "Name" : "Sketch Edges to Split with", "Filter" : EntityType.EDGE && ConstructionObject.NO, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
        definition.sketchEdges is Query;

        annotation { "Name" : "Keep Both Sides", "Default" : false, "UIHint": UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.keepBothSides is boolean;

        if (!definition.keepBothSides)
        {
            annotation { "Name" : "Opposite Direction", "Default" : true, "UIHint" : UIHint.OPPOSITE_DIRECTION }
            definition.oppositeDir is boolean;

        }
        
        annotation { "Name" : "Extend Lines", "Default": false, "UIHint": UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.extendLines is boolean;
        

    }
    {
        // Evaluate the sketch edges to get individual edges for iteration
        const sketchEdgesToSplit = evaluateQuery(context, definition.sketchEdges);
        
        if (size(sketchEdgesToSplit) == 0)
        {
            throw regenError("No sketch edges selected to split with");
        }
        
        // Get the sketch plane normal once for all edges
        const sketchPlaneNormal = evOwnerSketchPlane(context, { "entity" : definition.sketchEdges}).normal;
        
        // Track successful and failed splits
        var successfulSplitCount = 0;
        var failedSplitCount = 0;
        
        // Iterate over each sketch edge and perform individual split operations.
        // Each edge is extruded separately and used as a split tool, allowing
        // disconnected lines to split the target bodies independently.
        for (var edgeIndex = 0; edgeIndex < size(sketchEdgesToSplit); edgeIndex += 1)
        {
            const currentEdge = sketchEdgesToSplit[edgeIndex];
            const extrudeId = id + ("extrude" ~ edgeIndex);
            const splitId = id + ("split" ~ edgeIndex);
            const cleanupId = id + ("cleanup" ~ edgeIndex);
            
            // Attempt to extrude the current edge
            try silent
            {
                opExtrude(context, extrudeId, {
                    "entities" : currentEdge,
                    "direction" : sketchPlaneNormal,
                    "endBound" : BoundingType.THROUGH_ALL,
                    "startBound" : BoundingType.THROUGH_ALL
                });
                
                // Query for the extruded body created by this extrude operation
                const extrudedTool = qCreatedBy(extrudeId, EntityType.BODY);
                
                // Check if the extrude created a valid tool body
                if (!isQueryEmpty(context, extrudedTool))
                {
                    // Attempt to perform the split operation
                    try silent
                    {
                        opSplitPart(context, splitId, {
                            "targets" : definition.partsToSplit,
                            "tool" : extrudedTool,
                            "keepType" : definition.keepBothSides ? SplitOperationKeepType.KEEP_ALL : (definition.oppositeDir ? SplitOperationKeepType.KEEP_FRONT : SplitOperationKeepType.KEEP_BACK),
                            "useTrimmed": !definition.extendLines
                        });
                        
                        successfulSplitCount += 1;
                    }
                    catch
                    {
                        // Split failed - perform surface cleanup by deleting the extruded
                        // tool body to avoid leaving orphaned geometry in the part studio
                        try silent
                        {
                            opDeleteBodies(context, cleanupId, {
                                "entities" : extrudedTool
                            });
                        }
                        
                        failedSplitCount += 1;
                    }
                }
                else
                {
                    failedSplitCount += 1;
                }
            }
            catch
            {
                // Extrude failed - this edge could not be processed
                failedSplitCount += 1;
            }
        }
        
        // Report status to the user
        if (successfulSplitCount == 0)
        {
            throw regenError("All split operations failed. Check that sketch edges intersect target bodies.");
        }
        
        if (failedSplitCount > 0)
        {
            reportFeatureWarning(context, id, "Completed " ~ successfulSplitCount ~ " split(s). " ~ failedSplitCount ~ " split(s) failed.");
        }

    });
