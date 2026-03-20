FeatureScript 2909;
// Sheet Metal Split Feature
// Splits an active sheet metal part by operating on its master surface definition body
// and then triggering a sheet metal rebuild to update all part representations.
//
// The standard split feature cannot operate on active sheet metal parts because of the
// special tracking, associations, and attribute system used by the sheet metal engine.
// This feature works around that limitation by:
//   1. Extracting the master surface definition body for each selected sheet metal part
//   2. Performing opSplitPart on those surface bodies directly
//   3. Propagating sheet metal attributes to any new entities created by the split
//   4. Calling updateSheetMetalGeometry to rebuild the 3D solid parts from the updated surfaces

// Imports used in interface
export import(path : "onshape/std/splitoperationkeeptype.gen.fs", version : "2909.0");
export import(path : "onshape/std/query.fs", version : "2909.0");

// Imports used internally
import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/attributes.fs", version : "2909.0");
import(path : "onshape/std/containers.fs", version : "2909.0");
import(path : "onshape/std/evaluate.fs", version : "2909.0");
import(path : "onshape/std/feature.fs", version : "2909.0");
import(path : "onshape/std/manipulator.fs", version : "2909.0");
import(path : "onshape/std/math.fs", version : "2909.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2909.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2909.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2909.0");
import(path : "onshape/std/vector.fs", version : "2909.0");
import(path : "onshape/std/geomOperations.fs", version : "2909.0");
import(path : "onshape/std/uihint.gen.fs", version : "2909.0");

/**
 * Splits one or more active sheet metal parts by targeting the underlying master surface
 * definition bodies, then rebuilds the sheet metal geometry from the modified surfaces.
 *
 * Unlike the standard Split feature, this feature correctly handles the sheet metal
 * association and attribute tracking system, so the resulting halves are both valid
 * active sheet metal parts.
 */
annotation { "Feature Type Name" : "Sheet metal split",
             "Filter Selector" : "allparts",
             "Manipulator Change Function" : "sheetMetalSplitManipulatorChange" }
export const sheetMetalSplit = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Target selection: only active sheet metal solid bodies may be split
        annotation { "Name" : "Sheet metal parts to split",
                     "Filter" : EntityType.BODY && BodyType.SOLID && ActiveSheetMetal.YES && ModifiableEntityOnly.YES }
        definition.targets is Query;

        // Splitting tool: same geometry types supported by the standard split feature
        annotation { "Name" : "Entity to split with",
                     "Filter" : ((EntityType.BODY && BodyType.SHEET && SketchObject.NO) || EntityType.FACE || BodyType.MATE_CONNECTOR),
                     "MaxNumberOfPicks" : 1 }
        definition.tool is Query;

        annotation { "Name" : "Keep tools" }
        definition.keepTools is boolean;

        annotation { "Name" : "Keep both sides", "Default" : true }
        definition.keepBothSides is boolean;

        if (!definition.keepBothSides)
        {
            annotation { "Name" : "Opposite direction", "Default" : true, "UIHint" : UIHint.OPPOSITE_DIRECTION }
            definition.keepFront is boolean;
        }
    }
    {
        // Validate that at least one target has been selected
        if (isQueryEmpty(context, definition.targets))
        {
            throw regenError("Select at least one active sheet metal part to split", ["targets"]);
        }

        // Warn when a single face tool would be kept regardless of the keepTools checkbox,
        // mirroring the behavior and messaging of the standard split feature
        const toolFaceCount = size(evaluateQuery(context, qEntityFilter(definition.tool, EntityType.FACE)));
        if (toolFaceCount == 1 && !definition.keepTools)
        {
            const toolIsConstructionPlane = !isQueryEmpty(context, qConstructionFilter(definition.tool, ConstructionObject.YES));
            if (toolIsConstructionPlane)
            {
                reportFeatureInfo(context, id, ErrorStringEnum.SPLIT_KEEP_PLANES_AND_MATE_CONNECTORS);
            }
            else
            {
                reportFeatureInfo(context, id, ErrorStringEnum.SPLIT_KEEP_TOOLS_WITH_FACE);
            }
        }

        // Convert any mate connector tools to temporary planar bodies so opSplitPart can use them
        var temporaryPlaneQueries = buildTemporaryPlanesForMateConnectors(context, id, definition.tool);
        const temporaryPlaneCount = size(temporaryPlaneQueries);
        if (temporaryPlaneCount == 1)
        {
            definition.tool = temporaryPlaneQueries[0];
        }
        else if (temporaryPlaneCount > 1)
        {
            throw regenError("Only one splitting tool may be selected", ["tool"], definition.tool);
        }

        // Gather the master surface definition body for each selected sheet metal part.
        // The definition body is the internal sheet surface that the SM engine uses to
        // describe the flat geometry — splitting it is what actually divides the part.
        var allDefinitionBodyQueries = [];
        for (var solidPart in evaluateQuery(context, definition.targets))
        {
            const definitionEntities = qUnion(getSMDefinitionEntities(context, solidPart));
            if (!isQueryEmpty(context, definitionEntities))
            {
                allDefinitionBodyQueries = append(allDefinitionBodyQueries, qOwnerBody(definitionEntities));
            }
        }

        if (size(allDefinitionBodyQueries) == 0)
        {
            throw regenError("Could not locate sheet metal definition bodies for the selected parts", ["targets"]);
        }

        const allDefinitionBodiesQuery = qUnion(allDefinitionBodyQueries);

        // Snapshot the current entities and attribute state before any geometry changes
        const initialData = getInitialEntitiesAndAttributes(context, allDefinitionBodiesQuery);

        // Track the definition bodies so we can find newly created bodies after the split
        const trackedDefinitionBodies = startTracking(context, allDefinitionBodiesQuery);

        // Map the user's keepBothSides/keepFront choices to the opSplitPart keepType enum
        const keepType = definition.keepBothSides
                         ? SplitOperationKeepType.KEEP_ALL
                         : (definition.keepFront ? SplitOperationKeepType.KEEP_FRONT : SplitOperationKeepType.KEEP_BACK);

        // Attach a flip manipulator when the user wants to keep only one side of the split
        if (!definition.keepBothSides)
        {
            addSplitSideManipulator(context, id, definition, allDefinitionBodiesQuery);
        }

        // Split the master surface definition bodies (not the solid SM parts)
        opSplitPart(context, id + "splitDefinitionBodies", {
            "targets"   : allDefinitionBodiesQuery,
            "tool"      : definition.tool,
            "keepTools" : definition.keepTools,
            "keepType"  : keepType
        });

        // Build a query covering all definition bodies that exist after the split,
        // including any newly created ones produced by splitting
        const postSplitBodiesQuery = qUnion([
            allDefinitionBodiesQuery,
            qEntityFilter(trackedDefinitionBodies, EntityType.BODY)
        ]);

        // Propagate sheet metal attributes to entities that were created by the split
        const attributeUpdateData = assignSMAttributesToNewOrSplitEntities(context, postSplitBodiesQuery, initialData, id);

        // Rebuild the sheet metal solid parts from the updated surface definition bodies
        updateSheetMetalGeometry(context, id + "smUpdate", {
            "entities"          : attributeUpdateData.modifiedEntities,
            "deletedAttributes" : attributeUpdateData.deletedAttributes
        });

        // Remove any temporary planes that were created for mate connector tools.
        // opSplitPart does not delete construction planes regardless of keepTools, so
        // we always clean up our own temporary planes here.
        if (temporaryPlaneQueries != [])
        {
            opDeleteBodies(context, id + "deleteTempPlanes", { "entities" : qUnion(temporaryPlaneQueries) });
        }
    },
    { keepTools : false, keepBothSides : true, keepFront : true });


// ─── Manipulator ─────────────────────────────────────────────────────────────

/**
 * Adds a flip manipulator positioned on the splitting tool face nearest to the sheet metal
 * definition bodies, allowing the user to interactively choose which side of the split to keep.
 *
 * @param id {Id}
 * @param definition {map} : feature definition (reads tool, keepFront)
 * @param definitionBodies {Query} : sheet metal master surface definition bodies
 */
function addSplitSideManipulator(context is Context, id is Id, definition is map, definitionBodies is Query)
{
    // Obtain the faces of the splitting tool body
    var toolFaces;
    if (!isQueryEmpty(context, qOwnedByBody(definition.tool, EntityType.FACE)))
    {
        toolFaces = qOwnedByBody(definition.tool, EntityType.FACE);
    }
    else if (!isQueryEmpty(context, qEntityFilter(definition.tool, EntityType.FACE)))
    {
        toolFaces = qEntityFilter(definition.tool, EntityType.FACE);
    }
    else
    {
        return;
    }

    if (isQueryEmpty(context, definitionBodies))
    {
        return;
    }

    // Find the point on the tool face closest to the definition bodies
    const distanceResult = try(evDistance(context, {
        "side0" : toolFaces,
        "side1" : definitionBodies
    }));

    if (distanceResult == undefined)
    {
        return;
    }

    // Place the manipulator at the closest point on the tool face
    const tangentPlane = evFaceTangentPlane(context, {
        "face"      : qNthElement(toolFaces, distanceResult.sides[0].index),
        "parameter" : distanceResult.sides[0].parameter
    });

    addManipulators(context, id, {
        "splitSideManipulator" : flipManipulator({
            "base"      : tangentPlane.origin,
            "direction" : tangentPlane.normal,
            "flipped"   : !definition.keepFront
        })
    });
}

/**
 * Manipulator change handler for the sheet metal split feature.
 * Responds to the flip manipulator by updating the keepFront field.
 */
export function sheetMetalSplitManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    for (var manipulatorEntry in newManipulators)
    {
        if (manipulatorEntry.key == "splitSideManipulator")
        {
            definition.keepFront = !manipulatorEntry.value.flipped;
            return definition;
        }
    }
    return definition;
}


// ─── Helpers ─────────────────────────────────────────────────────────────────

/**
 * For each mate connector found in the tools query, creates a temporary planar body whose
 * plane matches the mate connector's XY plane, so opSplitPart can use it as a cutting tool.
 * Returns an array of face queries, one per created plane.
 *
 * @param id {Id}
 * @param tools {Query} : the tool selection to scan for mate connectors
 * @returns {array} : array of Query values, each pointing to one temporary planar face
 */
function buildTemporaryPlanesForMateConnectors(context is Context, id is Id, tools is Query) returns array
{
    var temporaryPlanes = [];
    var mateConnectorIndex = 0;
    for (var tool in evaluateQuery(context, tools))
    {
        const coordinateSystem = try(evMateConnector(context, { "mateConnector" : tool }));
        if (coordinateSystem != undefined)
        {
            const planeId = id + "mateConnectorPlane" + unstableIdComponent(mateConnectorIndex);
            setExternalDisambiguation(context, planeId, tool);
            opPlane(context, planeId, { "plane" : plane(coordinateSystem) });
            temporaryPlanes = append(temporaryPlanes, qEntityFilter(qCreatedBy(planeId), EntityType.FACE));
            mateConnectorIndex += 1;
        }
    }
    return temporaryPlanes;
}
