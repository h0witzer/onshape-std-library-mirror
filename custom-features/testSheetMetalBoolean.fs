FeatureScript 2878;
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2878.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2878.0");

/**
 * Test feature to validate sheet metal boolean subtraction logic
 * 
 * This feature tests the same pattern used in Sheet Metal Tab feature
 * to get SM definition faces and perform boolean subtraction.
 * 
 * Purpose: Debug why master definition faces are not being found
 */
annotation { "Feature Type Name" : "Test Sheet Metal Boolean" }
export const testSheetMetalBoolean = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Tool bodies to subtract", 
                     "Filter" : EntityType.BODY,
                     "MaxNumberOfPicks" : 10 }
        definition.toolBodies is Query;
        
        annotation { "Name" : "Target sheet metal faces",
                     "Filter" : EntityType.FACE,
                     "MaxNumberOfPicks" : 100 }
        definition.targetFaces is Query;
    }
    {
        println("=== TEST SHEET METAL BOOLEAN SUBTRACTION ===");
        println("");
        
        // Step 1: Evaluate tool bodies
        println("Step 1: Evaluating tool bodies query");
        const toolBodiesArray = evaluateQuery(context, definition.toolBodies);
        println("  Tool bodies count: " ~ size(toolBodiesArray));
        for (var i = 0; i < size(toolBodiesArray); i += 1)
        {
            println("  Tool body " ~ i ~ ": " ~ toolBodiesArray[i]);
        }
        println("");
        
        // Step 2: Evaluate target faces
        println("Step 2: Evaluating target faces query");
        const targetFacesArray = evaluateQuery(context, definition.targetFaces);
        println("  Target faces count: " ~ size(targetFacesArray));
        for (var i = 0; i < size(targetFacesArray); i += 1)
        {
            println("  Target face " ~ i ~ ": " ~ targetFacesArray[i]);
        }
        println("");
        
        // Step 3: Get SM definition entities from target faces
        // This follows the Sheet Metal Tab pattern (sheetMetalTab.fs line 504-510)
        println("Step 3: Getting SM definition entities from target faces");
        println("  Calling getSMDefinitionEntities(context, targetFaces, EntityType.FACE)");
        
        var smDefinitionFaces = try silent(getSMDefinitionEntities(context, definition.targetFaces, EntityType.FACE));
        
        if (smDefinitionFaces is undefined)
        {
            println("  ERROR: getSMDefinitionEntities returned undefined");
            println("  This means the target faces are not sheet metal definition faces");
            smDefinitionFaces = [];
        }
        else
        {
            println("  SUCCESS: getSMDefinitionEntities returned an array");
            println("  SM definition faces count: " ~ size(smDefinitionFaces));
            for (var i = 0; i < size(smDefinitionFaces); i += 1)
            {
                println("  SM definition face " ~ i ~ ": " ~ smDefinitionFaces[i]);
            }
        }
        println("");
        
        // Step 4: Check if we have definition faces to work with
        if (size(smDefinitionFaces) == 0)
        {
            println("ERROR: No SM definition faces found. Cannot proceed with boolean subtraction.");
            println("Possible reasons:");
            println("  1. Target faces are not from a sheet metal part");
            println("  2. Target faces are not definition faces");
            println("  3. getSMDefinitionEntities is not finding the definition entities");
            return;
        }
        
        // Step 5: Perform boolean subtraction for each definition face
        println("Step 4: Performing boolean subtraction");
        var successCount = 0;
        var failCount = 0;
        
        for (var i = 0; i < size(smDefinitionFaces); i += 1)
        {
            const face = smDefinitionFaces[i];
            println("  Processing SM definition face " ~ i);
            
            try silent
            {
                opBoolean(context, id + ("bool" ~ i), {
                    "tools" : definition.toolBodies,
                    "targets" : face,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "allowSheets" : true
                });
                println("    SUCCESS: Boolean subtraction completed for face " ~ i);
                successCount += 1;
            }
            catch
            {
                println("    ERROR: Boolean subtraction failed for face " ~ i);
                failCount += 1;
            }
        }
        
        println("");
        println("Step 5: Boolean subtraction summary");
        println("  Successful operations: " ~ successCount);
        println("  Failed operations: " ~ failCount);
        println("");
        
        // Step 6: Update sheet metal geometry
        println("Step 6: Updating sheet metal geometry");
        try
        {
            updateSheetMetalGeometry(context, id, {});
            println("  SUCCESS: Sheet metal geometry updated");
        }
        catch (error)
        {
            println("  ERROR: Sheet metal update failed");
            println("  Error: " ~ error);
        }
        
        println("");
        println("=== TEST COMPLETE ===");
    });
