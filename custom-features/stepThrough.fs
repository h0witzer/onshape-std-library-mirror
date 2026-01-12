FeatureScript 2837;
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/string.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");

/**
 * Step-Through Diagnostics Utility
 * 
 * This utility provides an interactive step-through debugging mechanism for FeatureScript.
 * Instead of commenting out code or adding numerous println statements, you can insert
 * stepthrough checkpoints that pause execution and display current state information.
 * 
 * Usage:
 * 1. Import this module into your feature
 * 2. Call stepThrough() at any point where you want to inspect state
 * 3. The feature will pause at that point, display state information in the UI
 * 4. Use the "Continue" button to proceed to the next stepthrough point
 * 
 * Example:
 * ```
 * for (var loopIndex = 0; loopIndex < size(myArray); loopIndex += 1)
 * {
 *     stepThrough(context, id + "checkpoint" ~ loopIndex, {
 *         "checkpoint" : "Loop iteration " ~ loopIndex,
 *         "loopIndex" : loopIndex,
 *         "arrayElement" : myArray[loopIndex],
 *         "queriesResolved" : size(evaluateQuery(context, myQuery))
 *     });
 *     
 *     // Your loop code here
 * }
 * ```
 */

// Constants for maintainability
const MAX_VARIABLE_COUNT = 5;
const MAX_QUERY_COUNT = 3;
const ENTITY_TYPE_ORDER = [EntityType.BODY, EntityType.FACE, EntityType.EDGE, EntityType.VERTEX];

/**
 * Helper function to get the display name for an entity type
 * 
 * @param entityType : The entity type to get the name for
 * @param count : The count of entities (for singular/plural handling)
 * @returns : The display name string (e.g., "face" or "faces")
 */
function getEntityTypeName(entityType is EntityType, count is number) returns string
{
    if (entityType == EntityType.BODY)
    {
        return count == 1 ? "body" : "bodies";
    }
    else if (entityType == EntityType.FACE)
    {
        return count == 1 ? "face" : "faces";
    }
    else if (entityType == EntityType.EDGE)
    {
        return count == 1 ? "edge" : "edges";
    }
    else if (entityType == EntityType.VERTEX)
    {
        return count == 1 ? "vertex" : "vertices";
    }
    return "";
}

export enum StepThroughDisplayMode
{
    annotation { "Name" : "Feature info (recommended)" }
    FEATURE_INFO,
    annotation { "Name" : "Notices (FeatureScript console)" }
    NOTICES,
    annotation { "Name" : "Both" }
    BOTH
}

annotation { 
    "Feature Type Name" : "Step-Through Diagnostics",
    "UIHint" : UIHint.NO_PREVIEW_PROVIDED,
    "Feature Name Template" : "Diagnostic Checkpoint: #checkpointName",
    "Tooltip Template" : "Step-through diagnostic checkpoint: #checkpointName",
    "Feature Type Description" : "Interactive step-through diagnostic utility for debugging FeatureScript. Place checkpoints in your code to pause execution and inspect state."
}
export const stepThroughFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Checkpoint name", "UIHint" : UIHint.VARIABLE_NAME, "MaxLength" : 256, "Default" : "Checkpoint" }
        definition.checkpointName is string;

        annotation { "Name" : "Display mode", "UIHint" : UIHint.SHOW_LABEL, "Default" : StepThroughDisplayMode.FEATURE_INFO }
        definition.displayMode is StepThroughDisplayMode;

        annotation { "Name" : "Enable this checkpoint", "Default" : true }
        definition.enabled is boolean;

        annotation { "Group Name" : "State Variables", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Variable 1 name", "MaxLength" : 100, "Default" : "" }
            definition.var1Name is string;

            if (definition.var1Name != "")
            {
                annotation { "Name" : "Variable 1 value (FS expression)" }
                isAnything(definition.var1Value);
            }

            annotation { "Name" : "Variable 2 name", "MaxLength" : 100, "Default" : "" }
            definition.var2Name is string;

            if (definition.var2Name != "")
            {
                annotation { "Name" : "Variable 2 value (FS expression)" }
                isAnything(definition.var2Value);
            }

            annotation { "Name" : "Variable 3 name", "MaxLength" : 100, "Default" : "" }
            definition.var3Name is string;

            if (definition.var3Name != "")
            {
                annotation { "Name" : "Variable 3 value (FS expression)" }
                isAnything(definition.var3Value);
            }

            annotation { "Name" : "Variable 4 name", "MaxLength" : 100, "Default" : "" }
            definition.var4Name is string;

            if (definition.var4Name != "")
            {
                annotation { "Name" : "Variable 4 value (FS expression)" }
                isAnything(definition.var4Value);
            }

            annotation { "Name" : "Variable 5 name", "MaxLength" : 100, "Default" : "" }
            definition.var5Name is string;

            if (definition.var5Name != "")
            {
                annotation { "Name" : "Variable 5 value (FS expression)" }
                isAnything(definition.var5Value);
            }
        }

        annotation { "Group Name" : "Query Inspection", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Inspect query 1", "Filter" : (EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY) && AllowFlattenedGeometry.YES }
            definition.query1 is Query;

            annotation { "Name" : "Inspect query 2", "Filter" : (EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY) && AllowFlattenedGeometry.YES }
            definition.query2 is Query;

            annotation { "Name" : "Inspect query 3", "Filter" : (EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY) && AllowFlattenedGeometry.YES }
            definition.query3 is Query;
        }

        annotation { "Group Name" : "Additional Notes", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Developer notes", "MaxLength" : 1000, "Default" : "" }
            definition.notes is string;
        }
    }
    {
        if (!definition.enabled)
        {
            reportFeatureInfo(context, id, "Checkpoint '" ~ definition.checkpointName ~ "' is disabled. Enable to see diagnostic information.");
            return;
        }

        var diagnosticOutput = "═══ STEP-THROUGH CHECKPOINT ═══\n";
        diagnosticOutput ~= "Checkpoint: " ~ definition.checkpointName ~ "\n";
        diagnosticOutput ~= "────────────────────────────────\n";

        // Display state variables
        var hasVariables = false;
        for (var varNum = 1; varNum <= MAX_VARIABLE_COUNT; varNum += 1)
        {
            const varName = definition["var" ~ varNum ~ "Name"];
            if (varName != undefined && varName != "")
            {
                if (!hasVariables)
                {
                    diagnosticOutput ~= "\n▼ State Variables:\n";
                    hasVariables = true;
                }
                const varValue = definition["var" ~ varNum ~ "Value"];
                diagnosticOutput ~= "  • " ~ varName ~ ": " ~ varValue ~ "\n";
            }
        }

        // Inspect queries
        var hasQueries = false;
        for (var queryNum = 1; queryNum <= MAX_QUERY_COUNT; queryNum += 1)
        {
            const query = definition["query" ~ queryNum];
            if (query != undefined && !isQueryEmpty(context, query))
            {
                if (!hasQueries)
                {
                    diagnosticOutput ~= "\n▼ Query Inspection:\n";
                    hasQueries = true;
                }
                
                const entities = evaluateQuery(context, query);
                diagnosticOutput ~= "  • Query " ~ queryNum ~ ": ";
                
                if (size(entities) == 0)
                {
                    diagnosticOutput ~= "No entities\n";
                }
                else
                {
                    var queryDescription = "";
                    var isFirstType = true;
                    
                    for (var entityType in ENTITY_TYPE_ORDER)
                    {
                        const filteredEntities = evaluateQuery(context, qEntityFilter(qUnion(entities), entityType));
                        const count = size(filteredEntities);
                        
                        if (count > 0)
                        {
                            if (!isFirstType)
                            {
                                queryDescription ~= ", ";
                            }
                            isFirstType = false;
                            
                            queryDescription ~= count ~ " " ~ getEntityTypeName(entityType, count);
                        }
                    }
                    
                    diagnosticOutput ~= queryDescription ~ "\n";
                    
                    // Highlight the query entities in the UI
                    debug(context, query);
                }
            }
        }

        // Display notes if provided
        if (definition.notes != undefined && definition.notes != "")
        {
            diagnosticOutput ~= "\n▼ Developer Notes:\n";
            diagnosticOutput ~= "  " ~ definition.notes ~ "\n";
        }

        diagnosticOutput ~= "────────────────────────────────\n";
        diagnosticOutput ~= "Edit this feature to continue or\n";
        diagnosticOutput ~= "disable it to skip this checkpoint.\n";
        diagnosticOutput ~= "═══════════════════════════════";

        // Output based on display mode
        if (definition.displayMode == StepThroughDisplayMode.FEATURE_INFO || 
            definition.displayMode == StepThroughDisplayMode.BOTH)
        {
            reportFeatureInfo(context, id, diagnosticOutput);
        }

        if (definition.displayMode == StepThroughDisplayMode.NOTICES || 
            definition.displayMode == StepThroughDisplayMode.BOTH)
        {
            println("\n" ~ diagnosticOutput);
        }
    });

/**
 * Programmatic step-through function for use in your own features.
 * 
 * Call this function to create a checkpoint that displays state information.
 * The execution pauses at this point when the feature is regenerated, allowing
 * you to inspect the current state of your feature.
 * 
 * @param context : The context in which the feature is executing
 * @param id : A unique ID for this checkpoint (use your feature's id + a suffix)
 * @param stateMap : A map containing variable names (as keys) and their values to display
 *                   Example: { "loopIndex" : loopIndex, "currentFace" : currentFace }
 * 
 * Example usage in a loop:
 * ```
 * for (var index = 0; index < 10; index += 1)
 * {
 *     stepThrough(context, id + "loopCheck" ~ index, {
 *         "iteration" : index,
 *         "processedItems" : processedCount
 *     });
 * }
 * ```
 */
export function stepThrough(context is Context, checkpointId is Id, stateMap is map)
{
    var output = "\n▼▼▼ STEP-THROUGH CHECKPOINT ▼▼▼\n";
    output ~= "ID: " ~ checkpointId ~ "\n";
    
    if (size(stateMap) > 0)
    {
        output ~= "State:\n";
        const mapKeys = keys(stateMap);
        for (var key in mapKeys)
        {
            output ~= "  • " ~ key ~ ": " ~ stateMap[key] ~ "\n";
        }
    }
    
    output ~= "▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲\n";
    
    println(output);
}

/**
 * Programmatic step-through function with query inspection.
 * 
 * Similar to stepThrough, but also inspects and highlights a query.
 * 
 * @param context : The context in which the feature is executing
 * @param checkpointId : A unique ID for this checkpoint
 * @param stateMap : A map of variable names and values to display
 * @param query : A query to inspect and highlight in the UI
 */
export function stepThroughWithQuery(context is Context, checkpointId is Id, stateMap is map, query is Query)
{
    var output = "\n▼▼▼ STEP-THROUGH CHECKPOINT ▼▼▼\n";
    output ~= "ID: " ~ checkpointId ~ "\n";
    
    if (size(stateMap) > 0)
    {
        output ~= "State:\n";
        const mapKeys = keys(stateMap);
        for (var key in mapKeys)
        {
            output ~= "  • " ~ key ~ ": " ~ stateMap[key] ~ "\n";
        }
    }
    
    // Inspect and display query
    const entities = evaluateQuery(context, query);
    output ~= "Query: ";
    
    if (size(entities) == 0)
    {
        output ~= "No entities\n";
    }
    else
    {
        var queryDescription = "";
        var isFirstType = true;
        
        for (var entityType in ENTITY_TYPE_ORDER)
        {
            const count = size(evaluateQuery(context, qEntityFilter(qUnion(entities), entityType)));
            
            if (count > 0)
            {
                if (!isFirstType)
                {
                    queryDescription ~= ", ";
                }
                isFirstType = false;
                
                queryDescription ~= count ~ " " ~ getEntityTypeName(entityType, count);
            }
        }
        
        output ~= queryDescription ~ "\n";
        debug(context, query);
    }
    
    output ~= "▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲\n";
    
    println(output);
}

/**
 * Helper function to check if a query is empty (undefined or evaluates to nothing)
 * 
 * @param context : The context in which to evaluate the query
 * @param query : The query to check
 * @returns : true if the query is empty or undefined, false otherwise
 */
function isQueryEmpty(context is Context, query is Query) returns boolean
{
    if (query == undefined)
    {
        return true;
    }
    
    try
    {
        const entities = evaluateQuery(context, query);
        return size(entities) == 0;
    }
    catch
    {
        return true;
    }
}
