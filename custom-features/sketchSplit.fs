FeatureScript 2770;
import(path : "onshape/std/common.fs", version : "2770.0");
import(path : "9f4c9835d8018ff7dbdb5683/77c856824c783f8f1c5a3312/f937aebd788e8d724a4de67b", version : "1e843593890adcd0071cee82"); // Reese Number Utils for pseudo rng function

/**
 * Split with Sketch Feature
 * 
 * Splits target bodies using sketch edges by extruding each edge through all
 * and using the resulting surfaces as split tools. Supports multiple disconnected
 * sketch lines with interactive selection of which resulting bodies to keep.
 */
annotation { "Feature Type Name" : "Split with Sketch", 
            "Feature Type Description" : "Use sketches to split entities",
            "Manipulator Change Function" : "splitSketchManipulatorChange",
            "Editing Logic Function" : "splitSketchEditLogic",
            "Filter Selector" : "none" }
export const splitSketch= defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Parts to Split", "Filter" : EntityType.BODY, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
        definition.partsToSplit is Query;

        annotation { "Name" : "Sketch Edges to Split with", "Filter" : EntityType.EDGE && ConstructionObject.NO, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
        definition.sketchEdges is Query;
        
        annotation { "Name" : "Extend Lines", "Default": false, "UIHint": UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.extendLines is boolean;
        
        // Indices of bodies that belong to the keep group
        annotation { "Name" : "Keep indices", "Item name" : "entry", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.keepIndices is array;
        for (var entry in definition.keepIndices)
        {
            annotation { "Name" : "Index" }
            isInteger(entry.keepIndex, { (unitless) : [-10000, 0, 10000] } as IntegerBoundSpec);
        }

        // Current selection index for point manipulator
        annotation { "Name" : "Handle index", "UIHint" : UIHint.ALWAYS_HIDDEN }
        isInteger(definition.index, { (unitless) : [-10000, 0, 10000] } as IntegerBoundSpec);

        // Button to keep all resulting bodies
        annotation { "Name" : "Keep all regions" }
        isButton(definition.selectAll);

        // Button to invert which regions are kept
        annotation { "Name" : "Invert kept regions" }
        isButton(definition.invertSelection);

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
        // All resulting bodies are kept initially (KEEP_ALL).
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
                    // Attempt to perform the split operation, keeping all resulting bodies
                    try silent
                    {
                        opSplitPart(context, splitId, {
                            "targets" : definition.partsToSplit,
                            "tool" : extrudedTool,
                            "keepType" : SplitOperationKeepType.KEEP_ALL,
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
        
        // Report status to the user if splits failed
        if (successfulSplitCount == 0)
        {
            throw regenError("All split operations failed. Check that sketch edges intersect target bodies.");
        }
        
        if (failedSplitCount > 0)
        {
            reportFeatureWarning(context, id, "Completed " ~ successfulSplitCount ~ " split(s). " ~ failedSplitCount ~ " split(s) failed.");
        }
        
        // Now get all resulting bodies after splits for interactive selection
        const allBodies = evaluateQuery(context, definition.partsToSplit);
        
        // Cache the body count for use in edit logic
        const bodiesVariableName = toAttributeId(id) ~ "-splitBodiesCount";
        setVariable(context, bodiesVariableName, size(allBodies));
        
        // Create manipulator points at each body's centroid and draw debug points
        var handlePoints = [];
        var partIndex = 0;
        const centroidOverlapTolerance = 1e-5 * meter;
        const centroidOffsetDistance = 0.005 * meter;
        var centroidsAdjusted = false;
        
        for (var body in allBodies)
        {
            var centroidPoint = evApproximateCentroid(context, { "entities" : body });
            
            // Check for coincident centroids and offset if necessary
            var overlapCount = 0;
            for (var existingPoint in handlePoints)
            {
                if (norm(centroidPoint - existingPoint) < centroidOverlapTolerance)
                {
                    overlapCount += 1;
                }
            }
            if (overlapCount > 0)
            {
                centroidsAdjusted = true;
                const randDir = randomUnitVector(partIndex + overlapCount);
                centroidPoint += overlapCount * centroidOffsetDistance * randDir;
            }
            
            handlePoints = append(handlePoints, centroidPoint);
            
            // Show point location in the group's color
            var debugColor = DebugColor.BLACK;
            if (isInKeepGroup(definition, partIndex))
            {
                debugColor = DebugColor.YELLOW;
            }
            else if (overlapCount > 0)
            {
                debugColor = DebugColor.BLUE;
            }
            addDebugPoint(context, centroidPoint, debugColor);
            partIndex += 1;
        }
        
        if (centroidsAdjusted)
        {
            reportFeatureInfo(context, id, "Overlapping centroids adjusted; shifted points are shown in blue");
        }
        
        // Display manipulators for all bodies
        addManipulators(context, id, { 
            "groupHandles" : pointsManipulator({ 
                "points" : handlePoints, 
                "index" : definition.index 
            }) 
        });
        
        // Determine which bodies to delete (those not in keep group)
        var deleteGroup = [];
        partIndex = 0;
        for (var body in allBodies)
        {
            if (!isInKeepGroup(definition, partIndex))
            {
                deleteGroup = append(deleteGroup, body);
            }
            partIndex += 1;
        }
        
        // Delete bodies that are not in the keep group
        if (size(deleteGroup) > 0)
        {
            opDeleteBodies(context, id + "removeUnselected", { "entities" : qUnion(deleteGroup) });
        }

    },
    {
        keepIndices : [],
        index : -1
    });

// Helper function: generates a random unit vector using pseudoRandomNumber for a given seed
function randomUnitVector(seed is number) returns Vector
{
    const azimuth = pseudoRandomNumber(seed, 0, 2 * PI);
    const inclination = pseudoRandomNumber(seed + 1, 0, PI);
    return vector(
            cos(azimuth * radian) * sin(inclination * radian),
            sin(azimuth * radian) * sin(inclination * radian),
            cos(inclination * radian));
}

// Helper function: returns true if the given part index is currently in the keep group list
function isInKeepGroup(definition is map, partIndex is number) returns boolean
{
    for (var entry in definition.keepIndices)
    {
        if (entry.keepIndex == partIndex)
        {
            return true;
        }
    }
    return false;
}

// Manipulator change function toggles group membership when a centroid handle is clicked
export function splitSketchManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    if (newManipulators["groupHandles"] is map)
    {
        const clickedIndex = newManipulators["groupHandles"].index;
        if (clickedIndex >= 0)
        {
            var removePosition = 0;
            var found = false;
            for (var entry in definition.keepIndices)
            {
                if (entry.keepIndex == clickedIndex)
                {
                    found = true;
                    break;
                }
                removePosition += 1;
            }

            if (found)
            {
                definition.keepIndices = removeElementAt(definition.keepIndices, removePosition);
            }
            else
            {
                definition.keepIndices = append(definition.keepIndices, { "keepIndex" : clickedIndex });
            }
            // Deselect the manipulator handle after toggling
            definition.index = -1;
        }
    }
    return definition;
}

// Editing logic function handles clicks on the keep all and invert buttons
export function splitSketchEditLogic(context is Context, id is Id,
    oldDefinition is map, definition is map, isCreating is boolean, clickedButton is string) returns map
{
    if (clickedButton == "selectAll")
    {
        // Get the cached body count from the last successful regeneration
        const bodiesVariableName = toAttributeId(id) ~ "-splitBodiesCount";
        const bodyCount = try silent(getVariable(context, bodiesVariableName));
        
        if (bodyCount != undefined)
        {
            var newKeep = [];
            for (var index = 0; index < bodyCount; index += 1)
            {
                newKeep = append(newKeep, { "keepIndex" : index });
            }
            definition.keepIndices = newKeep;
        }
    }
    if (clickedButton == "invertSelection")
    {
        // Get the cached body count from the last successful regeneration
        const bodiesVariableName = toAttributeId(id) ~ "-splitBodiesCount";
        const bodyCount = try silent(getVariable(context, bodiesVariableName));
        
        if (bodyCount != undefined)
        {
            var newKeep = [];
            for (var index = 0; index < bodyCount; index += 1)
            {
                if (!isInKeepGroup(definition, index))
                {
                    newKeep = append(newKeep, { "keepIndex" : index });
                }
            }
            definition.keepIndices = newKeep;
        }
    }
    return definition;
}
