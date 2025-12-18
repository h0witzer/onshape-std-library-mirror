FeatureScript 2837;

// Imports from sheetMetalTabAndSlot
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/path.fs", version : "2837.0");
import(path : "onshape/std/transform.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");
import(path : "onshape/std/extendendtype.gen.fs", version : "2837.0");
import(path : "onshape/std/extendsheetshapetype.gen.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/debugcolor.gen.fs", version : "2837.0");

// Constants for tab and slot feature from sheetMetalTabAndSlot
export const SPACING_BOUNDS =
{
    (inch) : [1e-6, 6, 1e6]
} as LengthBoundSpec;

export const TAB_WIDTH_BOUNDS =
{
    (inch) : [1e-6, 1, 1e6]
} as LengthBoundSpec;

export const TAB_DEPTH_BOUNDS =
{
    (inch) : [1e-6, .05, 1e6]
} as LengthBoundSpec;

export enum SpacingType
{
    annotation { "Name" : "Equal" }
    EQUAL,
    annotation { "Name" : "Length" }
    LENGTH
}

// Constants from splitSmEdgeChainToSegments.fs
const EDGE_LENGTH_TOLERANCE = 1e-9 * meter;
const EDGE_PARAMETER_TOLERANCE = 1e-6;
const FRACTION_TOLERANCE = 1e-9;

const CYAN_MAGENTA_DEBUG_COLORS = [
    DebugColor.CYAN,
    DebugColor.MAGENTA
];

/**
 * Refactored sheet metal tab feature using edge-based input.
 * Takes precondition from sheetMetalTabAndSlot and implements:
 * 1. Tab segment location logic from splitSmEdgeChainToSegments.fs
 * 2. Copy sheet metal wall surfaces
 * 3. Extend surfaces with opExtendSheetBody for tab depth
 */
annotation { "Feature Type Name" : "Tab and Slot Refactored" }
export const tabAndSlotRefactored = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Precondition from sheetMetalTabAndSlot
        annotation { "Name" : "Edges", "Filter" : EntityType.EDGE, "UIHint" : UIHint.SHOW_CREATE_SELECTION }
        definition.edges is Query;

        annotation { "Name" : "Spacing Type" }
        definition.spacingType is SpacingType;

        if (definition.spacingType == SpacingType.LENGTH)
        {
            annotation { "Name" : "Spacing" }
            isLength(definition.spacing, SPACING_BOUNDS);
        }

        if (definition.spacingType == SpacingType.EQUAL)
        {
            annotation { "Name" : "Tab Count" }
            isInteger(definition.tabCount, POSITIVE_COUNT_BOUNDS);
        }

        annotation { "Name" : "Tab Width" }
        isLength(definition.tabWidth, TAB_WIDTH_BOUNDS);

        annotation { "Name" : "Tab Depth" }
        isLength(definition.tabDepth, TAB_DEPTH_BOUNDS);
    }
    {
        // Step 1: Calculate tab parameters and determine spacing
        calculateTabSpacing(context, definition);
        
        // Step 2: Generate tab parameters (locations along edge chain)
        const tabParameters = generateTabParameters(context, id, definition);
        
        // Step 3: Split edges to create tab segments
        splitEdgesForTabSegments(context, id, definition, tabParameters);
        
        // Step 4: Copy sheet metal wall surfaces and extend tab segments
        copyAndExtendTabSurfaces(context, id, definition, tabParameters);
        
        // Step 5: (Future) Sheet metal recombination using smTabModified.fs logic
        // TODO: Integrate sheet metal tab merging logic
    });

// Calculates tab spacing based on spacing type (from sheetMetalTabAndSlot)
// Inputs: context, definition (contains edges, spacingType, and spacing/tabCount)
// Outputs: Updates definition.spacing or definition.tabCount based on edge length
function calculateTabSpacing(context is Context, definition is map)
{
    const edgeLength = evLength(context, {
        "entities" : definition.edges
    });
    
    if (definition.spacingType == SpacingType.EQUAL)
    {
        // Calculate spacing from tab count
        definition.spacing = definition.tabWidth + ((edgeLength - (definition.tabCount * definition.tabWidth)) / (definition.tabCount + 1));
    }
    else if (definition.spacingType == SpacingType.LENGTH)
    {
        // Calculate tab count from spacing
        definition.tabCount = floor((edgeLength - definition.tabWidth) / definition.spacing);
    }
}

// Generates tab parameters (locations) along the edge chain (from sheetMetalTabAndSlot)
// Inputs: context, id, definition (contains edges, spacing, tabCount, tabWidth)
// Outputs: Array of parameter values (0-1) indicating tab center locations along the edge chain
function generateTabParameters(context is Context, id is Id, definition is map) returns array
{
    const edgesAsPath = constructPath(context, definition.edges);
    const pathLength = evLength(context, {
        "entities" : definition.edges
    });
    
    const parameterSpacing = definition.spacing / pathLength;
    var placementParams = [];
    
    for (var i = 0; i < definition.tabCount; i += 1)
    {
        var oddEvenParam = i % 2 == 0 ? 1 : -1;
        var newParam = 1.0;
        
        if (definition.tabCount % 2 == 0)
        {
            // Even spacing offset from centre
            newParam = 0.5 + (oddEvenParam * parameterSpacing / 2) + (floor(i / 2) * parameterSpacing * oddEvenParam);
        }
        else if (i > 0)
        {
            // Odd spacing starts at center
            newParam = 0.5 + (ceil(i / 2) * parameterSpacing * (i % 2 == 0 ? 1 : -1));
        }
        else
        {
            newParam = 0.5;
        }
        
        placementParams = append(placementParams, newParam);
    }
    
    // Filter out tabs that overlap with edge vertices
    const tabWidthInParam = definition.tabWidth / pathLength;
    const pathVertices = calculatePathVertexParameters(context, definition.edges);
    
    var finalParams = [];
    for (var i = 0; i < size(placementParams); i += 1)
    {
        var clear = true;
        for (var j = 0; j < size(pathVertices); j += 1)
        {
            if (placementParams[i] + tabWidthInParam / 2 > pathVertices[j] && 
                placementParams[i] - tabWidthInParam / 2 < pathVertices[j])
            {
                clear = false;
            }
        }
        if (clear)
        {
            finalParams = append(finalParams, placementParams[i]);
        }
    }
    
    return finalParams;
}

// Calculates parameter values (0-1) for vertices along the edge path
// Inputs: context, edges (query containing edge chain)
// Outputs: Array of parameter values indicating vertex locations
function calculatePathVertexParameters(context is Context, edges is Query) returns array
{
    const path = constructPath(context, edges);
    const pathLength = evLength(context, {
        "entities" : edges
    });
    
    var params = [0];
    var currentParamLength = 0;
    
    for (var i = 0; i < size(path.edges); i += 1)
    {
        const edgeLength = evLength(context, {
            "entities" : path.edges[i]
        });
        
        const portionOfPath = edgeLength / pathLength;
        currentParamLength += portionOfPath;
        params = append(params, currentParamLength);
    }
    
    return params;
}

// Splits edges to create tab segments (adapted from sheetMetalTabAndSlot)
// Inputs: context, id, definition (contains edges, tabWidth), tabParameters (array of tab center locations)
// Outputs: Splits the edges at tab boundaries using opSplitEdges
function splitEdgesForTabSegments(context is Context, id is Id, definition is map, tabParameters is array)
{
    if (size(tabParameters) == 0)
    {
        return;
    }
    
    const path = constructPath(context, definition.edges);
    const pathLength = evLength(context, {
        "entities" : definition.edges
    });
    
    if (pathLength <= 0 * meter)
    {
        return;
    }
    
    const halfSpan = (definition.tabWidth / pathLength) / 2;
    const parameterTolerance = 1e-9;
    
    // Convert tab parameters to split parameters (start and end of each tab)
    var globalParameters = [];
    for (var parameter in tabParameters)
    {
        var startGlobal = max(parameterTolerance, parameter - halfSpan);
        var endGlobal = min(1 - parameterTolerance, parameter + halfSpan);
        
        if (startGlobal >= endGlobal)
        {
            continue;
        }
        
        globalParameters = append(globalParameters, startGlobal);
        globalParameters = append(globalParameters, endGlobal);
    }
    
    // Convert global parameters to per-edge parameters
    const perEdgeParameters = convertPathParametersToEdgeParameters(context, path, globalParameters);
    
    // Organize split data by edge
    var edgeSplitData = {};
    var orderedEdgeIndices = [];
    
    for (var i = 0; i < size(perEdgeParameters); i += 2)
    {
        const startInfo = perEdgeParameters[i];
        const endInfo = perEdgeParameters[i + 1];
        
        if (startInfo.edgeIndex != endInfo.edgeIndex)
        {
            continue;  // Skip tabs that cross edge vertices
        }
        
        const edgeIndex = startInfo.edgeIndex;
        var data = edgeSplitData[edgeIndex];
        if (data == undefined)
        {
            data = { "edge" : path.edges[edgeIndex], "parameters" : [] };
            edgeSplitData[edgeIndex] = data;
            orderedEdgeIndices = append(orderedEdgeIndices, edgeIndex);
        }
        
        const startParameter = min(startInfo.parameter, endInfo.parameter);
        const endParameter = max(startInfo.parameter, endInfo.parameter);
        
        data.parameters = append(data.parameters, startParameter);
        data.parameters = append(data.parameters, endParameter);
        edgeSplitData[edgeIndex] = data;
    }
    
    // Perform edge splits
    var edgeCounter = 0;
    for (var edgeIndex in orderedEdgeIndices)
    {
        var data = edgeSplitData[edgeIndex];
        var orderedParameters = sort(data.parameters, function(a, b) { return a - b; });
        
        var filteredParameters = [];
        for (var parameter in orderedParameters)
        {
            if (parameter <= 0 || parameter >= 1)
            {
                continue;
            }
            if (size(filteredParameters) > 0 && abs(parameter - filteredParameters[size(filteredParameters) - 1]) < 1e-6)
            {
                continue;
            }
            filteredParameters = append(filteredParameters, parameter);
        }
        
        if (size(filteredParameters) == 0)
        {
            continue;
        }
        
        @opSplitEdges(context, id + ("tabSplit" ~ toString(edgeCounter)), {
            "edges" : data.edge,
            "parameters" : [filteredParameters]
        });
        edgeCounter += 1;
    }
}

// Converts path parameters (0-1 along entire path) to edge-specific parameters
// Inputs: context, path (Path object), parameters (array of path parameters)
// Outputs: Array of maps with "parameter" (0-1 on specific edge) and "edgeIndex"
function convertPathParametersToEdgeParameters(context is Context, path is Path, parameters is array) returns array
{
    var result = [];
    
    const pathLength = evLength(context, {
        "entities" : qUnion(path.edges)
    });
    
    var edgeLengths = [];
    var edgeParameterBoundaries = [0];
    var currentBoundary = 0;
    
    for (var edge in path.edges)
    {
        const edgeLength = evLength(context, {
            "entities" : edge
        });
        edgeLengths = append(edgeLengths, edgeLength);
        currentBoundary += edgeLength / pathLength;
        edgeParameterBoundaries = append(edgeParameterBoundaries, currentBoundary);
    }
    
    for (var i = 0; i < size(parameters); i += 1)
    {
        for (var j = 1; j < size(edgeParameterBoundaries); j += 1)
        {
            if (parameters[i] > edgeParameterBoundaries[j - 1] && parameters[i] < edgeParameterBoundaries[j])
            {
                // Parameter is in this edge's range
                const edgeParameterSize = edgeLengths[j - 1] / pathLength;
                const remaining = parameters[i] - edgeParameterBoundaries[j - 1];
                var edgeParam = remaining / edgeParameterSize;
                
                if (path.flipped[j - 1])
                {
                    edgeParam = 1.0 - edgeParam;
                }
                
                result = append(result, { "parameter" : edgeParam, "edgeIndex" : j - 1 });
                break;
            }
        }
    }
    
    return result;
}

// Copies sheet metal wall surfaces and extends tab segments
// Inputs: context, id, definition (contains edges, tabDepth), tabParameters (array of tab locations)
// Outputs: Creates copied and extended surfaces for tab generation
function copyAndExtendTabSurfaces(context is Context, id is Id, definition is map, tabParameters is array)
{
    if (size(tabParameters) == 0)
    {
        return;
    }
    
    // Get adjacent sheet metal wall faces
    const adjacentFacesQuery = qAdjacent(definition.edges, AdjacencyType.EDGE, EntityType.FACE);
    const wallFaces = evaluateQuery(context, adjacentFacesQuery);
    
    if (size(wallFaces) == 0)
    {
        throw regenError("No sheet metal wall faces found adjacent to selected edges", ["edges"]);
    }
    
    // Copy the wall faces to create surface bodies for tab generation
    opPattern(context, id + "copyWalls", {
        "entities" : qUnion(wallFaces),
        "transforms" : [identityTransform()],
        "instanceNames" : ["tabSurfaces"]
    });
    
    const copiedSurfacesQuery = qCreatedBy(id + "copyWalls", EntityType.BODY);
    const copiedSurfaces = evaluateQuery(context, copiedSurfacesQuery);
    
    if (size(copiedSurfaces) == 0)
    {
        throw regenError("Failed to copy sheet metal wall faces", ["edges"]);
    }
    
    // Extract surfaces to ensure they are proper sheet bodies
    opExtractSurface(context, id + "extractCopied", {
        "faces" : qOwnedByBody(copiedSurfacesQuery, EntityType.FACE),
        "redundancyType" : ExtractSurfaceRedundancyType.REMOVE_ALL_REDUNDANCY
    });
    
    // Get edges that correspond to the selected edges in the copied surfaces
    // These are the edges we want to extend for tab depth
    const copiedEdgesQuery = identifyTabEdges(context, id, definition);
    
    // Extend the tab edges by the specified tab depth
    try
    {
        opExtendSheetBody(context, id + "extend", {
            "entities" : copiedEdgesQuery,
            "endCondition" : ExtendEndType.EXTEND_BLIND,
            "extendDistance" : definition.tabDepth,
            "extensionShape" : ExtendSheetShapeType.LINEAR
        });
    }
    catch (error)
    {
        throw regenError("Failed to extend sheet body edges by tab depth: " ~ error, ["tabDepth", "edges"]);
    }
    
    // Debug: Show the extended surfaces
    const extendedSurfaces = qCreatedBy(id + "extend", EntityType.FACE);
    debug(context, extendedSurfaces, DebugColor.GREEN);
}

// Identifies which edges in the copied surfaces should be extended for tabs
// Inputs: context, id, definition (contains edges after splitting)
// Outputs: Query for edges that should be extended
function identifyTabEdges(context is Context, id is Id, definition is map) returns Query
{
    // After splitting, the edges have been divided into segments
    // We need to identify which segments correspond to tabs (not gaps between tabs)
    // For now, we'll extend all edges on the copied surfaces
    // TODO: Refine to only extend alternating segments (tab segments, not gap segments)
    
    const copiedEdgesQuery = qCreatedBy(id + "extractCopied", EntityType.EDGE);
    return copiedEdgesQuery;
}
