FeatureScript 2856;
import(path : "onshape/std/common.fs", version : "2856.0");

// Import the sheet metal queries library
import(path : "943642034066bc27de5d166f", version : "d693581e2a8766ea1378a36d");// sheetMetalQueries.fs

/**
 * Sheet Metal Queries Tester Feature
 * 
 * This feature provides a testing environment for the sheet metal query helper
 * functions defined in sheetMetalQueries.fs. Use this feature to trial run
 * and validate the various query functions available in the queries library.
 * 
 * The tester allows you to select a sheet metal part and visualize the results
 * of different query operations, helping you understand how the query functions
 * work and verify their behavior.
 * 
 * Features:
 * - Visual highlighting of query results in the 3D view
 * - Entity count reporting
 * - Query variable export for use in other features (via setVariable)
 * - Support for multiple query types (cut faces, stock faces)
 * 
 * To use the exported query variable in another feature:
 * 1. Enable "Export Query Variable" and set a variable name
 * 2. In another feature, use getVariable(context, variableName) to access the query
 * 3. The query can be used as input to operations or further filtering
 */

annotation { "Feature Type Name" : "Sheet Metal Queries Tester" }
export const sheetMetalQueriesTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sheet Metal Part",
                     "Filter" : EntityType.BODY && BodyType.SOLID,
                     "MaxNumberOfPicks" : 1 }
        definition.sheetMetalPart is Query;
        
        annotation { "Name" : "Query Function to Test",
                     "UIHint" : UIHint.SHOW_LABEL }
        definition.queryFunction is QueryFunctionType;
        
        annotation { "Name" : "Highlight Results",
                     "Default" : true }
        definition.highlightResults is boolean;
        
        annotation { "Name" : "Export Query Variable",
                     "Default" : false }
        definition.exportQueryVariable is boolean;
        
        if (definition.exportQueryVariable)
        {
            annotation { "Name" : "Variable Name",
                         "Default" : "sheetMetalQuery" }
            definition.variableName is string;
        }
    }
    {
        // Execute the selected query function
        var resultQuery = qNothing();
        
        if (definition.queryFunction == QueryFunctionType.SHEET_METAL_CUT_FACES)
        {
            resultQuery = qSheetMetalCutFaces(context, definition.sheetMetalPart);
        }
        else if (definition.queryFunction == QueryFunctionType.SHEET_METAL_STOCK_FACES)
        {
            resultQuery = qSheetMetalStockFaces(context, definition.sheetMetalPart);
        }
        
        // Evaluate the query to get actual entities
        const resultEntities = evaluateQuery(context, resultQuery);
        const resultCount = size(resultEntities);
        
        // Report results to user
        if (resultCount == 0)
        {
            reportFeatureInfo(context, id, "No entities found matching the query criteria. " ~
                                          "This may be because the selected part is not an active sheet metal part, " ~
                                          "or because it has no entities matching the query type.");
        }
        else
        {
            const entityWord = (resultCount == 1) ? "entity" : "entities";
            reportFeatureInfo(context, id, "Query returned " ~ resultCount ~ " " ~ entityWord);
        }
        
        // Highlight the results if requested
        if (definition.highlightResults && resultCount > 0)
        {
            debug(context, resultQuery, DebugColor.GREEN);
            setFeatureComputedParameter(context, id, {
                "name" : "resultCount",
                "value" : resultCount
            });
        }
        
        // Export query variable if requested
        if (definition.exportQueryVariable)
        {
            const varName = definition.variableName;
            setVariable(context, varName, resultQuery);
            reportFeatureInfo(context, id, "Query exported as variable '" ~ varName ~ "' for use in other features");
        }
    });

/**
 * Enumeration of available query functions for testing
 */
export enum QueryFunctionType
{
    annotation { "Name" : "qSheetMetalCutFaces - Laser-cut edge profiles" }
    SHEET_METAL_CUT_FACES,
    annotation { "Name" : "qSheetMetalStockFaces - Top/bottom stock surfaces" }
    SHEET_METAL_STOCK_FACES
}
