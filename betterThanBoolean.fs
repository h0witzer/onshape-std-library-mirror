FeatureScript 2679;

// Vibe coded by Derek Van Allen with help from Codex and Gemini
// This feature is inspired by the Intersect tool from Solidworks and Boundary fill feature in Fusion
// Manipulator usage was inspired by Caden Armstrong and Michael Pascoe from various sources

import(path : "onshape/std/common.fs", version : "2679.0");
import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/manipulator.fs", version : "2679.0");
import(path : "onshape/std/containers.fs", version : "2679.0");
import(path : "onshape/std/debug.fs", version : "2679.0");
import(path : "onshape/std/valueBounds.fs", version : "2679.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2679.0");
import(path : "onshape/std/error.fs", version : "2679.0");
import(path : "261d99c1a339a5b7d6ca9096", version : "8b403bd9faf83c5a4e65834f");
icon::import(path : "32e129618be41ad8355ae1fe", version : "645e53d0896e5a01563e0e1e");

// Bounds for part index parameters
const PART_INDEX_BOUNDS = { (unitless) : [-10000, 0, 10000] } as IntegerBoundSpec;

const HANDLE_MANIPULATOR = "groupHandles";

const CELLS_VARIABLE_NAME = "-betterThanBooleanCells";

function getCellsQuery(context is Context, id is Id, bodies is Query) returns Query
{
    const varName = toAttributeId(id) ~ CELLS_VARIABLE_NAME;
    var cached = try(getVariable(context, varName));
    if (cached != undefined)
    {
        return cached as Query;
    }
    var cells = decomposeIntoCells(context, id + "cells", bodies);
    setVariable(context, varName, cells);
    return cells;
}


// Returns true if the given part index is currently in the keep group list.
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

/**
 * Feature "Better Than Boolean" divides all modifiable bodies into keep and
 * delete groups for intersecting operations.
 * Point manipulators at each body centroid toggle group membership when clicked.
 * Parts are visualized only with debug colors rather than appearance changes.
 * If merging keep parts fails because the boolean results in non-manifold
 * geometry, the user is notified with an informational message.
 * An additional message warns when the union creates more than one region.
 */

annotation { "Feature Type Name" : "Better Than Boolean",
        "Icon" : icon::BLOB_DATA,
        "Feature Type Description" : "<b> Summary </b> <br> Decompose inputs into regions of overlap and recombine the result. <br>",
        "Manipulator Change Function" : "betterThanBooleanManipulatorChange",
        "Editing Logic Function" : "betterThanBooleanEditLogic",
        // Disallow picking geometry other than the manipulators
        "Filter Selector" : "none" }
export const betterThanBoolean = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {

        annotation { "Name" : "Bodies to modify",
                    "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES }
        definition.bodies is Query;

        // Indices of bodies that belong to the keep group.
        annotation { "Name" : "Keep indices", "Item name" : "entry", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.keepIndices is array;
        for (var entry in definition.keepIndices)
        {
            annotation { "Name" : "Index" }
            isInteger(entry.keepIndex, PART_INDEX_BOUNDS);
        }

        // Current selection index for point manipulator.
        annotation { "Name" : "Handle index", "UIHint" : UIHint.ALWAYS_HIDDEN }
        isInteger(definition.index, PART_INDEX_BOUNDS);

        // Button to select all cell regions.
        annotation { "Name" : "Select All" }
        isButton(definition.selectAll);

        // Button to invert membership of all parts.
        annotation { "Name" : "Invert selection" }
        isButton(definition.invertSelection);
    }
    {
        verifyNonemptyQuery(context, definition, "bodies", "Select bodies to modify.");
        const cellsQuery = getCellsQuery(context, id, definition.bodies);
        const bodies = evaluateQuery(context, cellsQuery);
        // Create manipulator points at each body's centroid and draw debug points.
        var handlePoints = [];
        var partIndex = 0;
        for (var body in bodies)
        {
            const centroidPoint = evApproximateCentroid(context, { "entities" : body });
            handlePoints = append(handlePoints, centroidPoint);

            // Show point location in the group's color.
            const debugColor = isInKeepGroup(definition, partIndex) ? DebugColor.YELLOW : DebugColor.BLACK;
            addDebugPoint(context, centroidPoint, debugColor);
            partIndex += 1;
        }

        // Display manipulators for all bodies.
        addManipulators(context, id, { (HANDLE_MANIPULATOR) : pointsManipulator({ "points" : handlePoints, "index" : definition.index }) });

        // Determine which bodies belong to each group so they can be combined
        // or removed after boolean operation
        var keepGroup = [];
        var deleteGroup = [];
        partIndex = 0;
        for (var body in bodies)
        {
            const inKeep = isInKeepGroup(definition, partIndex);
            if (inKeep)
            {
                keepGroup = append(keepGroup, body);
            }
            else
            {
                deleteGroup = append(deleteGroup, body);
            }
            partIndex += 1;
        }

        // Merge all keep bodies together and remove the delete ones.
        var keepResult;
        if (size(keepGroup) > 1)
        {
            // Using the same query for the tools lets us inspect the resulting
            // bodies after the operation.
            const mergeQuery = qUnion(keepGroup);
            try
            {
                opBoolean(context, id + "mergeKeep", {
                            "operationType" : BooleanOperationType.UNION,
                            "tools" : mergeQuery
                        });
            }
            catch (error)
            {
                const message = try(error.message as ErrorStringEnum);
                if (message == ErrorStringEnum.BOOLEAN_NON_MANIFOLD_RESULT)
                {
                    reportFeatureInfo(context, id, ErrorStringEnum.BOOLEAN_NON_MANIFOLD_RESULT);
                }
                else
                {
                    throw error;
                }
            }

            keepResult = mergeQuery;
            if (size(evaluateQuery(context, keepResult)) > 1)
            {
                reportFeatureInfo(context, id, "Boolean produced multiple regions");
            }
        }
        else if (size(keepGroup) == 1)
        {
            keepResult = keepGroup[0];
        }

        if (size(deleteGroup) > 0)
        {
            opDeleteBodies(context, id + "removeDelete", { "entities" : qUnion(deleteGroup) });
        }

        // keepResult references the final body after union, if any.
        // Additional processing could occur here if needed.
    },
    {
            keepIndices : [],
            index : -1
        });

// Manipulator change function toggles group membership when a centroid handle is
// clicked and deselects the handle afterwards.
export function betterThanBooleanManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    if (newManipulators[HANDLE_MANIPULATOR] is map)
    {
        const clickedIndex = newManipulators[HANDLE_MANIPULATOR].index;
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
            // Deselect the manipulator handle after toggling.
            definition.index = -1;
        }
    }
    return definition;
}

// Editing logic function handles clicks on the invert selection button.
export function betterThanBooleanEditLogic(context is Context, id is Id,
    oldDefinition is map, definition is map, isCreating is boolean, clickedButton is string) returns map
{
    if (clickedButton == "selectAll")
    {
        const cellsQuery = getCellsQuery(context, id, definition.bodies);
        const bodies = evaluateQuery(context, cellsQuery);
        var newKeep = [];
        var index = 0;
        for (var body in bodies)
        {

            newKeep = append(newKeep, { "keepIndex" : index });
            index += 1;
        }
        definition.keepIndices = newKeep;
    }
    if (clickedButton == "invertSelection")
    {
        const cellsQuery = getCellsQuery(context, id, definition.bodies);
        const bodies = evaluateQuery(context, cellsQuery);
        var newKeep = [];
        var index = 0;
        for (var body in bodies)
        {
            if (!isInKeepGroup(definition, index))
            {
                newKeep = append(newKeep, { "keepIndex" : index });
            }
            index += 1;
        }
        definition.keepIndices = newKeep;
    }
    return definition;
}
