FeatureScript 2856;
// Sheet Metal Queries Tester
// This feature allows testing and demonstration of the sheet metal query helper functions.
// Import the custom sheet metal query libraries to trial run the functions.

import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/query.fs", version : "2856.0");
import(path : "onshape/std/evaluate.fs", version : "2856.0");
import(path : "onshape/std/feature.fs", version : "2856.0");
import(path : "onshape/std/valueBounds.fs", version : "2856.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2856.0");
import(path : "onshape/std/smobjecttype.gen.fs", version : "2856.0");
import(path : "onshape/std/smjointtype.gen.fs", version : "2856.0");
import(path : "onshape/std/smbendtype.gen.fs", version : "2856.0");
import(path : "onshape/std/debug.fs", version : "2856.0");
import(path : "onshape/std/debugcolor.gen.fs", version : "2856.0");

// Import the custom sheet metal query libraries
// NOTE: These imports use empty version strings for local development/testing
// When using in Onshape, you should:
// 1. Upload these files to an Onshape document
// 2. Use the full Onshape document path with proper version, e.g.:
//    import(path : "c234567890abcdef12345678/v/abc123/e/def456/sheetMetalQueries.fs", version : "");
// 3. Or reference by document name if using the standard library pattern
import(path : "sheetMetalQueries.fs", version : "");
import(path : "sheetMetalQueriesUtils.fs", version : "");

/**
 * Enumeration of available query operations to test.
 * Provides a selection menu for different sheet metal query functions.
 */
export enum SMQueryTestOperation
{
    annotation { "Name" : "Query bend edges" }
    BEND_EDGES,
    annotation { "Name" : "Query rip edges" }
    RIP_EDGES,
    annotation { "Name" : "Query wall faces" }
    WALL_FACES,
    annotation { "Name" : "Query corner vertices" }
    CORNER_VERTICES,
    annotation { "Name" : "Query planar walls" }
    PLANAR_WALLS,
    annotation { "Name" : "Query cylindrical walls" }
    CYLINDRICAL_WALLS,
    annotation { "Name" : "Query adjacent walls" }
    ADJACENT_WALLS,
    annotation { "Name" : "Query boundary edges" }
    BOUNDARY_EDGES,
    annotation { "Name" : "Count bend edges" }
    COUNT_BENDS,
    annotation { "Name" : "Count wall faces" }
    COUNT_WALLS,
    annotation { "Name" : "Get bend radii" }
    GET_BEND_RADII,
    annotation { "Name" : "Get sheet metal thickness" }
    GET_THICKNESS
}

/**
 * Sheet Metal Query Tester Feature
 * Allows selection of sheet metal entities and testing of various query helper functions.
 * Results are visualized using debug graphics and reported to the console.
 */
annotation { "Feature Type Name" : "Sheet Metal Query Tester" }
export const sheetMetalQueryTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sheet metal entities",
                    "Filter" : ActiveSheetMetal.YES && (EntityType.BODY || EntityType.FACE || EntityType.EDGE),
                    "MaxNumberOfPicks" : 1 }
        definition.sheetMetalInput is Query;
        
        annotation { "Name" : "Query operation" }
        definition.operation is SMQueryTestOperation;
        
        if (definition.operation == SMQueryTestOperation.ADJACENT_WALLS)
        {
            annotation { "Name" : "Wall faces to find adjacent to",
                        "Filter" : ActiveSheetMetal.YES && EntityType.FACE }
            definition.wallFacesInput is Query;
        }
        
        annotation { "Name" : "Show debug graphics", "Default" : true }
        definition.showDebug is boolean;
    }
    {
        // Execute the selected query operation
        var resultQuery = qNothing();
        var resultMessage = "";
        
        if (definition.operation == SMQueryTestOperation.BEND_EDGES)
        {
            resultQuery = qSheetMetalBendEdges(context, definition.sheetMetalInput);
            const bendCount = size(evaluateQuery(context, resultQuery));
            resultMessage = "Found " ~ bendCount ~ " bend edge(s)";
            
            // Display debug graphics for bend edges
            if (definition.showDebug && bendCount > 0)
            {
                debug(context, resultQuery, DebugColor.RED);
            }
        }
        else if (definition.operation == SMQueryTestOperation.RIP_EDGES)
        {
            resultQuery = qSheetMetalRipEdges(context, definition.sheetMetalInput);
            const ripCount = size(evaluateQuery(context, resultQuery));
            resultMessage = "Found " ~ ripCount ~ " rip edge(s)";
            
            if (definition.showDebug && ripCount > 0)
            {
                debug(context, resultQuery, DebugColor.BLUE);
            }
        }
        else if (definition.operation == SMQueryTestOperation.WALL_FACES)
        {
            resultQuery = qSheetMetalWallFaces(context, definition.sheetMetalInput);
            const wallCount = size(evaluateQuery(context, resultQuery));
            resultMessage = "Found " ~ wallCount ~ " wall face(s)";
            
            if (definition.showDebug && wallCount > 0)
            {
                debug(context, resultQuery, DebugColor.GREEN);
            }
        }
        else if (definition.operation == SMQueryTestOperation.CORNER_VERTICES)
        {
            resultQuery = qSheetMetalCornerVertices(context, definition.sheetMetalInput);
            const cornerCount = size(evaluateQuery(context, resultQuery));
            resultMessage = "Found " ~ cornerCount ~ " corner vertex/vertices";
            
            if (definition.showDebug && cornerCount > 0)
            {
                debug(context, resultQuery, DebugColor.MAGENTA);
            }
        }
        else if (definition.operation == SMQueryTestOperation.PLANAR_WALLS)
        {
            resultQuery = qSheetMetalPlanarWalls(context, definition.sheetMetalInput);
            const planarCount = size(evaluateQuery(context, resultQuery));
            resultMessage = "Found " ~ planarCount ~ " planar wall(s)";
            
            if (definition.showDebug && planarCount > 0)
            {
                debug(context, resultQuery, DebugColor.CYAN);
            }
        }
        else if (definition.operation == SMQueryTestOperation.CYLINDRICAL_WALLS)
        {
            resultQuery = qSheetMetalCylindricalWalls(context, definition.sheetMetalInput);
            const cylinderCount = size(evaluateQuery(context, resultQuery));
            resultMessage = "Found " ~ cylinderCount ~ " cylindrical wall(s)";
            
            if (definition.showDebug && cylinderCount > 0)
            {
                debug(context, resultQuery, DebugColor.YELLOW);
            }
        }
        else if (definition.operation == SMQueryTestOperation.ADJACENT_WALLS)
        {
            resultQuery = qSheetMetalAdjacentWalls(context, definition.wallFacesInput);
            const adjacentCount = size(evaluateQuery(context, resultQuery));
            resultMessage = "Found " ~ adjacentCount ~ " adjacent wall(s)";
            
            if (definition.showDebug && adjacentCount > 0)
            {
                debug(context, resultQuery, DebugColor.ORANGE);
            }
        }
        else if (definition.operation == SMQueryTestOperation.BOUNDARY_EDGES)
        {
            resultQuery = qSheetMetalBoundaryEdges(context, definition.sheetMetalInput);
            const boundaryCount = size(evaluateQuery(context, resultQuery));
            resultMessage = "Found " ~ boundaryCount ~ " boundary edge(s)";
            
            if (definition.showDebug && boundaryCount > 0)
            {
                debug(context, resultQuery, DebugColor.PURPLE);
            }
        }
        else if (definition.operation == SMQueryTestOperation.COUNT_BENDS)
        {
            const allEdges = qOwnedByBody(definition.sheetMetalInput, EntityType.EDGE);
            const bendCount = countBendEdges(context, allEdges);
            resultMessage = "Total bend edges: " ~ bendCount;
        }
        else if (definition.operation == SMQueryTestOperation.COUNT_WALLS)
        {
            const allFaces = qOwnedByBody(definition.sheetMetalInput, EntityType.FACE);
            const wallCount = countWallFaces(context, allFaces);
            resultMessage = "Total wall faces: " ~ wallCount;
        }
        else if (definition.operation == SMQueryTestOperation.GET_BEND_RADII)
        {
            const bendEdges = qSheetMetalBendEdges(context, definition.sheetMetalInput);
            const radii = getAllBendRadii(context, bendEdges);
            resultMessage = "Found " ~ size(radii) ~ " bend(s) with radii";
            
            if (size(radii) > 0)
            {
                resultMessage = resultMessage ~ ": ";
                for (var i = 0; i < size(radii); i += 1)
                {
                    if (i > 0)
                    {
                        resultMessage = resultMessage ~ ", ";
                    }
                    resultMessage = resultMessage ~ toString(radii[i]);
                }
            }
            
            if (definition.showDebug && size(radii) > 0)
            {
                debug(context, bendEdges, DebugColor.RED);
            }
        }
        else if (definition.operation == SMQueryTestOperation.GET_THICKNESS)
        {
            const thickness = getSheetMetalThickness(context, definition.sheetMetalInput);
            if (thickness != undefined)
            {
                resultMessage = "Sheet metal thickness: " ~ toString(thickness);
            }
            else
            {
                resultMessage = "Could not determine sheet metal thickness";
            }
        }
        
        // Report results to console
        println("=== Sheet Metal Query Tester Results ===");
        println("Operation: " ~ definition.operation);
        println(resultMessage);
        println("========================================");
    });

