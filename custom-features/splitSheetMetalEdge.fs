FeatureScript 2780;
import(path : "onshape/std/common.fs", version : "2780.0");
import(path : "onshape/std/query.fs", version : "2780.0");
import(path : "onshape/std/evaluate.fs", version : "2780.0");
import(path : "onshape/std/valueBounds.fs", version : "2780.0");
import(path : "onshape/std/geomOperations.fs", version : "2780.0");
import(path : "onshape/std/path.fs", version : "2780.0");
import(path : "onshape/std/debug.fs", version : "2780.0");
import(path : "onshape/std/debugcolor.gen.fs", version : "2780.0");

const EDGE_SEGMENT_COUNT_BOUNDS =
{
    (unitless) : [2, 2, 200]
} as IntegerBoundSpec;

const EDGE_LENGTH_TOLERANCE = 1e-9 * meter;
const EDGE_PARAMETER_TOLERANCE = 1e-6;
const FRACTION_TOLERANCE = 1e-9;

const CYAN_MAGENTA_DEBUG_COLORS = [
        DebugColor.CYAN,
        DebugColor.MAGENTA
    ];

// Splits a selected chain of folded sheet metal edges into evenly sized segments without rebuilding the sheet metal definition.
// Inputs: edgeChain (query containing contiguous folded sheet metal edges), segmentCount (desired number of segments for the chain).
// Outputs: The selected folded edges are split into the requested number of segments and alternating cyan/magenta debug lines highlight the result.
annotation { "Feature Type Name" : "SM folded edge chain split" }
export const smFoldedEdgeChainSplit = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edge chain", "Filter" : EntityType.EDGE && SheetMetalDefinitionEntityType.EDGE && ActiveSheetMetal.YES && ModifiableEntityOnly.YES }
        definition.edgeChain is Query;

        annotation { "Name" : "Segment count" }
        isInteger(definition.segmentCount, EDGE_SEGMENT_COUNT_BOUNDS);
    }
    {
        const selectedEdgesQuery = qEntityFilter(definition.edgeChain, EntityType.EDGE);
        const selectedEdges = evaluateQuery(context, selectedEdgesQuery);
        if (size(selectedEdges) == 0)
        {
            throw regenError("Select at least one sheet metal edge", ["edgeChain"]);
        }

        const activeBodiesQuery = qActiveSheetMetalFilter(qOwnerBody(selectedEdgesQuery), ActiveSheetMetal.YES);
        const activeBodies = evaluateQuery(context, activeBodiesQuery);
        if (size(activeBodies) == 0)
        {
            throw regenError("Selected edges must belong to an active sheet metal part", ["edgeChain"], definition.edgeChain);
        }
        if (size(activeBodies) > 1)
        {
            throw regenError("Select edges that belong to a single sheet metal part", ["edgeChain"], definition.edgeChain);
        }

        const orderedEdgeQuery = qUnion(selectedEdges);
        const path = try silent(constructPath(context, orderedEdgeQuery));
        if (path == undefined)
        {
            throw regenError("Unable to order the selected edges into a continuous chain", ["edgeChain"], definition.edgeChain);
        }

        const trackedChain = startTracking(context, qUnion(path.edges));
        const splitInstructions = calculateEdgeSplitInstructions(context, path, definition.segmentCount);
        if (size(splitInstructions) == 0)
        {
            throw regenError("Segment count must create at least one split along the selected edges", ["segmentCount"]);
        }

        var splitOperationIndex = 0;
        for (var instruction in splitInstructions)
        {
            try
            {
                @opSplitEdges(context, id + ("split" ~ toString(splitOperationIndex)), {
                            "edges" : instruction.edge,
                            "parameters" : [instruction.parameters]
                        });
            }
            catch
            {
                throw regenError("Failed to split the sheet metal edge chain at the requested locations", ["edgeChain"], instruction.edge);
            }
            splitOperationIndex += 1;
        }

        showAlternatingCyanMagentaSegments(context, trackedChain, definition.segmentCount);
    });

// Calculates split parameters for each edge within a path so a folded edge chain can be split into evenly sized segments.
// Inputs: path (ordered path representing the selected edge chain), segmentCount (desired number of segments for the chain).
// Outputs: Array of maps containing an edge query and the sorted parameters to split on that edge.
function calculateEdgeSplitInstructions(context is Context, path is Path, segmentCount is number) returns array
{
    const totalLength = evLength(context, {
                "entities" : qUnion(path.edges)
            });
    if (totalLength <= EDGE_LENGTH_TOLERANCE)
    {
        throw regenError("Selected edge chain must have a measurable length", ["edgeChain"]);
    }

    var cumulativeFractions = [0];
    var accumulatedFraction = 0;
    for (var edgeIndex = 0; edgeIndex < size(path.edges); edgeIndex += 1)
    {
        const edge = path.edges[edgeIndex];
        const edgeLength = evLength(context, { "entities" : edge });
        if (edgeLength <= EDGE_LENGTH_TOLERANCE)
        {
            throw regenError("Edge chain contains a zero length edge", ["edgeChain"], edge);
        }
        accumulatedFraction += edgeLength / totalLength;
        if (edgeIndex == size(path.edges) - 1)
        {
            accumulatedFraction = 1;
        }
        cumulativeFractions = append(cumulativeFractions, accumulatedFraction);
    }

    var fractionsToSplit = [];
    for (var index = 1; index < segmentCount; index += 1)
    {
        fractionsToSplit = append(fractionsToSplit, index / segmentCount);
    }

    var edgeParameterMap = {};
    var orderedEdgeIndices = [];
    for (var fraction in fractionsToSplit)
    {
        var assigned = false;
        for (var boundaryIndex = 1; boundaryIndex < size(cumulativeFractions); boundaryIndex += 1)
        {
            const startFraction = cumulativeFractions[boundaryIndex - 1];
            const endFraction = cumulativeFractions[boundaryIndex];
            if (fraction < startFraction - FRACTION_TOLERANCE)
            {
                continue;
            }
            if (fraction > endFraction + FRACTION_TOLERANCE)
            {
                continue;
            }

            const normalizedRange = endFraction - startFraction;
            if (normalizedRange <= EDGE_PARAMETER_TOLERANCE)
            {
                throw regenError("Edge chain contains a segment that cannot be subdivided", ["edgeChain"], path.edges[boundaryIndex - 1]);
            }

            var edgeParameter = (fraction - startFraction) / normalizedRange;
            edgeParameter = max(0, min(1, edgeParameter));
            if (path.flipped[boundaryIndex - 1])
            {
                edgeParameter = 1 - edgeParameter;
            }

            if (edgeParameterMap[boundaryIndex - 1] == undefined)
            {
                edgeParameterMap[boundaryIndex - 1] = [];
                orderedEdgeIndices = append(orderedEdgeIndices, boundaryIndex - 1);
            }
            edgeParameterMap[boundaryIndex - 1] = append(edgeParameterMap[boundaryIndex - 1], edgeParameter);
            assigned = true;
            break;
        }

        if (!assigned)
        {
            const lastEdgeIndex = size(path.edges) - 1;
            var edgeParameter = path.flipped[lastEdgeIndex] ? EDGE_PARAMETER_TOLERANCE : 1 - EDGE_PARAMETER_TOLERANCE;
            if (edgeParameterMap[lastEdgeIndex] == undefined)
            {
                edgeParameterMap[lastEdgeIndex] = [];
                orderedEdgeIndices = append(orderedEdgeIndices, lastEdgeIndex);
            }
            edgeParameterMap[lastEdgeIndex] = append(edgeParameterMap[lastEdgeIndex], edgeParameter);
        }
    }

    var splitInstructions = [];
    for (var edgeIndex in orderedEdgeIndices)
    {
        var parameters = edgeParameterMap[edgeIndex];
        parameters = sort(parameters, function(a, b) { return a - b; });

        var filteredParameters = [];
        for (var parameter in parameters)
        {
            if (parameter <= EDGE_PARAMETER_TOLERANCE || parameter >= 1 - EDGE_PARAMETER_TOLERANCE)
            {
                continue;
            }
            if (size(filteredParameters) != 0 && abs(parameter - filteredParameters[size(filteredParameters) - 1]) < EDGE_PARAMETER_TOLERANCE)
            {
                continue;
            }
            filteredParameters = append(filteredParameters, parameter);
        }

        if (size(filteredParameters) == 0)
        {
            continue;
        }

        splitInstructions = append(splitInstructions, {
                    "edge" : path.edges[edgeIndex],
                    "parameters" : filteredParameters
                });
    }

    return splitInstructions;
}

// Displays the resulting split edge segments with alternating cyan and magenta debug colors for visual verification.
// Inputs: trackedEdges (tracking query that captures the edge chain before splitting), segmentCount (number of desired segments).
// Outputs: Alternating debug highlights per segment domain across the resulting edge chain.
function showAlternatingCyanMagentaSegments(context is Context, trackedEdges is Query, segmentCount is number)
{
    const edgeQuery = qEntityFilter(trackedEdges, EntityType.EDGE);
    const splitEdges = evaluateQuery(context, edgeQuery);
    if (size(splitEdges) == 0)
    {
        return;
    }

    var orderedEdges = splitEdges;
    const orderedPath = try silent(constructPath(context, qUnion(splitEdges)));
    if (orderedPath != undefined)
    {
        orderedEdges = orderedPath.edges;
    }

    if (segmentCount <= 0)
    {
        return;
    }

    var totalLength = 0 * meter;
    var edgeLengths = [];
    for (var edge in orderedEdges)
    {
        const edgeLength = evLength(context, { "entities" : edge });
        edgeLengths = append(edgeLengths, edgeLength);
        totalLength += edgeLength;
    }

    if (totalLength <= EDGE_LENGTH_TOLERANCE)
    {
        return;
    }

    const segmentLength = totalLength / segmentCount;
    const colorCount = size(CYAN_MAGENTA_DEBUG_COLORS);
    var accumulatedLength = 0 * meter;
    var currentSegmentIndex = 0;

    for (var index = 0; index < size(orderedEdges); index += 1)
    {
        const color = CYAN_MAGENTA_DEBUG_COLORS[currentSegmentIndex % colorCount];
        debug(context, orderedEdges[index], color);

        accumulatedLength += edgeLengths[index];
        while (currentSegmentIndex < segmentCount - 1
                && accumulatedLength >= segmentLength * (currentSegmentIndex + 1) - EDGE_LENGTH_TOLERANCE)
        {
            currentSegmentIndex += 1;
        }
    }
}
