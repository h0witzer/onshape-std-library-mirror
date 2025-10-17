FeatureScript 2780;
import(path : "onshape/std/common.fs", version : "2780.0");
import(path : "onshape/std/query.fs", version : "2780.0");
import(path : "onshape/std/evaluate.fs", version : "2780.0");
import(path : "onshape/std/valueBounds.fs", version : "2780.0");
import(path : "onshape/std/geomOperations.fs", version : "2780.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2780.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2780.0");
import(path : "onshape/std/debug.fs", version : "2780.0");
import(path : "onshape/std/debugcolor.gen.fs", version : "2780.0");

const EDGE_PARTITION_COUNT_BOUNDS =
{
    (unitless) : [2, 2, 200]
} as IntegerBoundSpec;

const CYAN_MAGENTA_DEBUG_COLORS = [
        DebugColor.CYAN,
        DebugColor.MAGENTA
    ];

// Splits a selected sheet metal edge face into evenly sized partitions on the master definition and updates 3D/2D sheet metal representations.
// Inputs: edgeFace (sheet metal definition face that represents the edge to partition), partitionCount (number of desired segments).
// Outputs: Updated sheet metal definition with the edge divided into the requested number of segments and debug visuals for each segment.
annotation { "Feature Type Name" : "SM edge partition" }
export const smEdgePartition = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edge face", "Filter" : SheetMetalDefinitionEntityType.EDGE && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES, "MaxNumberOfPicks" : 1 }
        definition.edgeFace is Query;

        annotation { "Name" : "Partition count" }
        isInteger(definition.partitionCount, EDGE_PARTITION_COUNT_BOUNDS);
    }
    {
        if (isQueryEmpty(context, definition.edgeFace))
        {
            throw regenError("Select a sheet metal edge face", ["edgeFace"]);
        }

        const definitionEdges = try silent(getSMDefinitionEntities(context, definition.edgeFace, EntityType.EDGE));
        if (definitionEdges == undefined)
        {
            throw regenError("Selected face is not associated with an active sheet metal edge", ["edgeFace"], definition.edgeFace);
        }

        const definitionEdgeQuery = qUnion(definitionEdges);
        const definitionEdgeCount = size(evaluateQuery(context, definitionEdgeQuery));
        if (definitionEdgeCount != 1)
        {
            if (definitionEdgeCount == 0)
            {
                throw regenError("Unable to find a sheet metal master edge for the selected face", ["edgeFace"], definition.edgeFace);
            }
            throw regenError("Select a face that corresponds to a single sheet metal edge", ["edgeFace"], definition.edgeFace);
        }

        const sheetMetalBodies = qOwnerBody(definitionEdgeQuery);
        if (isQueryEmpty(context, sheetMetalBodies))
        {
            throw regenError("Failed to resolve the owning sheet metal definition", ["edgeFace"], definition.edgeFace);
        }

        const initialData = getInitialEntitiesAndAttributes(context, sheetMetalBodies);
        const trackedBodies = startTracking(context, sheetMetalBodies);
        const trackedEdge = startTracking(context, definitionEdgeQuery);

        var splitParameters = [];
        for (var index = 1; index < definition.partitionCount; index += 1)
        {
            splitParameters = append(splitParameters, index / definition.partitionCount);
        }

        if (size(splitParameters) == 0)
        {
            throw regenError("Partition count must create at least one split", ["partitionCount"]);
        }

        try
        {
            @opSplitEdges(context, id + "split", {
                        "edges" : definitionEdgeQuery,
                        "parameters" : [splitParameters]
                    });
        }
        catch
        {
            throw regenError("Failed to split the sheet metal edge at the requested partitions", ["edgeFace"], definition.edgeFace);
        }

        showAlternatingCyanMagentaSegments(context, trackedEdge);

        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, qUnion([trackedBodies, sheetMetalBodies]), initialData, id);
        println(toUpdate);

        updateSheetMetalGeometry(context, id + "smUpdate", {
                    "entities" : qUnion([toUpdate.modifiedEntities, trackedBodies]),
                    "deletedAttributes" : toUpdate.deletedAttributes
                });
    });

// Displays each resulting edge segment with alternating cyan and magenta debug colors so the split can be visually confirmed during feature edit.
// Inputs: splitEdgeTracking (tracking query for the split edge in the definition).
// Outputs: Debug highlights for each resulting edge segment alternating between cyan and magenta.
function showAlternatingCyanMagentaSegments(context is Context, splitEdgeTracking is Query)
{
    const splitEdgesQuery = qEntityFilter(splitEdgeTracking, EntityType.EDGE);
    const edgeCount = size(evaluateQuery(context, splitEdgesQuery));
    if (edgeCount == 0)
    {
        return;
    }

    const colorCount = size(CYAN_MAGENTA_DEBUG_COLORS);
    for (var index = 0; index < edgeCount; index += 1)
    {
        const color = CYAN_MAGENTA_DEBUG_COLORS[index % colorCount];
        debug(context, qNthElement(splitEdgesQuery, index), color);
    }
}
