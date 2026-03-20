FeatureScript 2909;
// Butcher Sheet Metal Feature
// Splits one or more active sheet metal parts using one of two operating modes:
//
// CHAINSAW mode — calls opSplitPart on the shared master surface definition body.
//   The entire definition body (which may serve multiple solid output parts) is split
//   into new sheet body pieces.  Fast and blunt: every part in the shared definition
//   body is affected, regardless of which parts were individually selected.
//   Single-side keep uses opSplitPart's built-in keepType parameter
//   (SplitOperationKeepType.KEEP_FRONT/KEEP_BACK), the same API as the standard Split
//   part feature (splitpart.fs:167).  No geometry math is needed for side selection.
//
// SCALPEL mode — calls opSplitFace scoped exclusively to the definition body wall
//   faces that are associated with the selected solid output parts.  Parts in the same
//   definition body that were NOT selected are completely untouched.  New split
//   boundary edges are stamped with editable RIP joint attributes (all fields set to
//   canBeEdited: true) so the Modify Joints panel remains live after the feature.
//   This mode is analogous to the "Face" mode of the standard Split feature, which
//   also does not expose a keep-side option.  Projection options (ProjectionType)
//   are provided for parity with the standard face split UI.
//
// Both modes share the same parameters and post-split SM rebuild pipeline
// (assignSMAttributesToNewOrSplitEntities + updateSheetMetalGeometry).

// Imports used in interface
export import(path : "onshape/std/query.fs", version : "2909.0");
export import(path : "onshape/std/projectiontype.gen.fs", version : "2909.0");
export import(path : "onshape/std/splitoperationkeeptype.gen.fs", version : "2909.0");

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
import(path : "onshape/std/topologyUtils.fs", version : "2909.0");
import(path : "onshape/std/vector.fs", version : "2909.0");
import(path : "onshape/std/geomOperations.fs", version : "2909.0");
import(path : "onshape/std/uihint.gen.fs", version : "2909.0");

/**
 * Operating mode for the Butcher Sheet Metal feature.
 *
 * CHAINSAW - Direct opSplitPart on the entire SM definition body.  Every part that
 *            shares the definition body is split (or the body becomes two new bodies).
 *            Fast and blunt: no per-part face-level scoping.  Supports keep-side via
 *            opSplitPart's built-in keepType parameter.
 *
 * SCALPEL  - opSplitFace scoped exclusively to the definition body faces that belong
 *            to the selected solid output parts.  Parts sharing the same definition body
 *            that are NOT selected are never touched.  New split boundary edges receive
 *            editable RIP joint attributes so the Modify Joints panel stays live.
 *            Analogous to the "Face" mode of the standard Split feature; no keep-side
 *            option is exposed (matching standard face split behaviour).
 */
export enum ButcherMode
{
    annotation { "Name" : "Chainsaw" }
    CHAINSAW,
    annotation { "Name" : "Scalpel" }
    SCALPEL
}

/**
 * Splits one or more active sheet metal parts by operating on the underlying master
 * surface definition body, then rebuilds the sheet metal geometry.
 *
 * Unlike the standard Split feature, this feature works directly with the sheet metal
 * association and attribute tracking system, operating on the definition body surfaces
 * rather than the 3D solid output parts.
 *
 * See the ButcherMode enum for a description of the two operating modes.
 */
// The exported constant is intentionally named `sheetMetalSplit` for backward compatibility
// with existing Part Studio scripts that already reference this feature by that name.
// All other naming (Feature Type Name, file, manipulator handler) uses "Butcher Sheet Metal".
annotation { "Feature Type Name" : "Butcher sheet metal",
             "Filter Selector" : "allparts",
             "Manipulator Change Function" : "butcherSheetMetalManipulatorChange" }
export const sheetMetalSplit = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // CHAINSAW / SCALPEL displayed as a horizontal toggle, matching how the standard
        // Split feature presents its Part / Face mode selector (splitpart.fs:49).
        annotation { "Name" : "Split mode", "Default" : ButcherMode.CHAINSAW, "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.splitMode is ButcherMode;

        // Target selection: only active sheet metal solid bodies may be split
        annotation { "Name" : "Sheet metal parts to split",
                     "Filter" : EntityType.BODY && BodyType.SOLID && ActiveSheetMetal.YES && ModifiableEntityOnly.YES }
        definition.targets is Query;

        // Chainsaw splitting tool: sheet bodies, faces, and mate connectors (part-split parity)
        // Scalpel splitting tool: additionally accepts sketch edges and wire body curves for
        // use with the edge projection options (face-split parity, splitpart.fs:82-88)
        if (definition.splitMode == ButcherMode.CHAINSAW)
        {
            annotation { "Name" : "Entity to split with",
                         "Filter" : ((EntityType.BODY && BodyType.SHEET && SketchObject.NO) || EntityType.FACE || BodyType.MATE_CONNECTOR),
                         "MaxNumberOfPicks" : 1 }
            definition.tool is Query;
        }

        if (definition.splitMode == ButcherMode.SCALPEL)
        {
            annotation { "Name" : "Entity to split with",
                         "Filter" : (EntityType.EDGE && SketchObject.YES && ConstructionObject.NO) ||
                             (EntityType.BODY && (BodyType.SHEET || BodyType.WIRE) && ModifiableEntityOnly.NO && SketchObject.NO) ||
                             (EntityType.BODY && (BodyType.SHEET || BodyType.WIRE) && ModifiableEntityOnly.NO && SketchObject.YES) ||
                             EntityType.FACE ||
                             BodyType.MATE_CONNECTOR,
                         "MaxNumberOfPicks" : 1 }
            definition.scalpelTool is Query;
        }

        annotation { "Name" : "Keep tools" }
        definition.keepTools is boolean;

        // ── Chainsaw-only: keep-side selection ───────────────────────────────────
        // opSplitPart (chainsaw) naturally supports a keep-side concept via keepType.
        // Scalpel mode (opSplitFace) is analogous to the standard "Face" split, which
        // does not expose a keep-side option in its UI — so neither do we.
        if (definition.splitMode == ButcherMode.CHAINSAW)
        {
            annotation { "Name" : "Keep both sides", "Default" : true }
            definition.keepBothSides is boolean;

            if (!definition.keepBothSides)
            {
                annotation { "Name" : "Opposite direction", "Default" : true, "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.keepFront is boolean;
            }
        }

        // ── Scalpel-only: edge projection options ────────────────────────────────
        // Parity with the standard face split feature (splitpart.fs:91-107).
        // Controls how edge and wire body tools are projected onto the target faces.
        if (definition.splitMode == ButcherMode.SCALPEL)
        {
            annotation { "Group Name" : "Edge projection options", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Projection direction type" }
                definition.projectionType is ProjectionType;

                if (definition.projectionType == ProjectionType.DIRECTION)
                {
                    annotation { "Name" : "Use sketch plane direction", "Default" : true }
                    definition.useSketchPlaneDirection is boolean;

                    if (!definition.useSketchPlaneDirection)
                    {
                        annotation { "Name" : "Direction", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
                        definition.directionQuery is Query;
                    }
                }
            }
        }
    }
    {
        // ── Validate targets ──────────────────────────────────────────────────────
        if (isQueryEmpty(context, definition.targets))
        {
            throw regenError("Select at least one active sheet metal part to split", ["targets"]);
        }

        // ── Resolve the active tool query for this mode ───────────────────────────
        // Chainsaw uses definition.tool (part-split filter); scalpel uses definition.scalpelTool
        // (face-split filter, includes sketch edges and wire bodies).
        var rawToolQuery = (definition.splitMode == ButcherMode.CHAINSAW)
            ? definition.tool
            : definition.scalpelTool;

        // ── Warn when a single face tool is always kept, mirroring standard split ─
        const toolFaceCount = size(evaluateQuery(context, qEntityFilter(rawToolQuery, EntityType.FACE)));
        if (toolFaceCount == 1 && !definition.keepTools)
        {
            const toolIsConstructionPlane = !isQueryEmpty(context, qConstructionFilter(rawToolQuery, ConstructionObject.YES));
            if (toolIsConstructionPlane)
            {
                reportFeatureInfo(context, id, ErrorStringEnum.SPLIT_KEEP_PLANES_AND_MATE_CONNECTORS);
            }
            else
            {
                reportFeatureInfo(context, id, ErrorStringEnum.SPLIT_KEEP_TOOLS_WITH_FACE);
            }
        }

        // ── Convert mate connectors to temporary planar bodies ────────────────────
        // This is done once so the same plane can be used for every face target group.
        const temporaryPlaneQueries = buildTemporaryPlanesForMateConnectors(context, id, rawToolQuery);
        const temporaryPlaneCount = size(temporaryPlaneQueries);
        if (temporaryPlaneCount == 1)
        {
            rawToolQuery = temporaryPlaneQueries[0];
        }
        else if (temporaryPlaneCount > 1)
        {
            throw regenError("Only one splitting tool may be selected",
                             definition.splitMode == ButcherMode.CHAINSAW ? ["tool"] : ["scalpelTool"],
                             rawToolQuery);
        }

        // effectiveTool is rawToolQuery after mate-connector conversion.
        const effectiveTool = rawToolQuery;

        // ── Locate the shared master surface definition body ──────────────────────
        // For a single SM model, this is the one SHEET body that the engine uses to
        // derive all solid output parts — it may contain wall faces for many parts.
        const definitionBodyQuery = qUnion(getSMDefinitionEntities(context, definition.targets));
        if (isQueryEmpty(context, definitionBodyQuery))
        {
            throw regenError("Could not locate the sheet metal definition body for the selected parts", ["targets"]);
        }

        // ── SCALPEL only: resolve definition faces scoped to selected parts ────────
        // Walking the face-level SMAssociationAttribute links from the solid part faces
        // to the definition body faces gives us a scoped faceTargets query that leaves
        // all other parts in the same definition body completely untouched.
        var selectedDefinitionFaces = undefined;
        if (definition.splitMode == ButcherMode.SCALPEL)
        {
            selectedDefinitionFaces = collectDefinitionFacesForSelectedParts(context, definition.targets, definitionBodyQuery);
            if (isQueryEmpty(context, selectedDefinitionFaces))
            {
                throw regenError("Could not resolve the sheet metal definition faces for the selected parts", ["targets"]);
            }
        }

        // ── Snapshot the definition body state before any geometry changes ─────────
        const initialData = getInitialEntitiesAndAttributes(context, definitionBodyQuery);
        const trackedDefinitionBody = startTracking(context, definitionBodyQuery);

        // ── Attach the flip manipulator for chainsaw single-side mode ─────────────
        // Scalpel mode has no keep-side option (analogous to standard face split),
        // so the manipulator is only relevant when using chainsaw mode.
        if (definition.splitMode == ButcherMode.CHAINSAW && !definition.keepBothSides)
        {
            addSplitSideManipulator(context, id, definition, definitionBodyQuery);
        }

        // ── Execute the split ─────────────────────────────────────────────────────
        const splitId = id + "split";
        if (definition.splitMode == ButcherMode.CHAINSAW)
        {
            // Chainsaw: opSplitPart on the entire definition body.  keepType is the
            // engine-native API for single-side selection (identical to splitpart.fs:167).
            // Using keepType here means the engine handles side selection internally,
            // with the same "front = tool normal direction" semantics as qSplitBy and
            // as the standard Split feature.  No geometry math or post-split deletion
            // is needed for chainsaw single-side mode.
            opSplitPart(context, splitId, {
                "targets"   : definitionBodyQuery,
                "tool"      : effectiveTool,
                "keepTools" : true,
                "keepType"  : definition.keepBothSides
                    ? SplitOperationKeepType.KEEP_ALL
                    : (definition.keepFront ? SplitOperationKeepType.KEEP_FRONT
                                           : SplitOperationKeepType.KEEP_BACK)
            });
        }
        else
        {
            // Scalpel: opSplitFace confined to definition body faces belonging to the
            // selected solid parts.  All other faces in the shared definition body remain
            // untouched.  keepToolSurfaces: true (set inside buildOpSplitFaceDefinition
            // for body tools) so we manage deletion ourselves.
            const splitResult = opSplitFace(context, splitId,
                                            buildOpSplitFaceDefinition(context, selectedDefinitionFaces, effectiveTool, definition));

            // Stamp every new split boundary edge with an editable RIP joint attribute.
            // All fields (including angle) are marked canBeEdited: true so the Modify
            // Joints panel remains live and fully interactive after the feature is applied.
            var ripIndex = 0;
            for (var splitEdge in splitResult.splittingEdges)
            {
                setAttribute(context, {
                    "entities"  : splitEdge,
                    "attribute" : createEditableRipAttribute(context, splitEdge,
                                                             toAttributeId(id + "rip" + ripIndex))
                });
                ripIndex += 1;
            }
        }

        // ── Propagate SM attributes and rebuild ────────────────────────────────────
        // The tracked definition body query captures all resulting bodies (new pieces
        // from opSplitPart, or the modified original body from opSplitFace).
        const postSplitBodiesQuery = qUnion([
            definitionBodyQuery,
            qEntityFilter(trackedDefinitionBody, EntityType.BODY)
        ]);

        const attributeUpdateData = assignSMAttributesToNewOrSplitEntities(context, postSplitBodiesQuery, initialData, id);
        updateSheetMetalGeometry(context, id + "smUpdate", {
            "entities"          : attributeUpdateData.modifiedEntities,
            "deletedAttributes" : attributeUpdateData.deletedAttributes
        });

        // ── Tool cleanup ─────────────────────────────────────────────────────────
        // Both opSplitFace (keepToolSurfaces: true) and opSplitPart (keepTools: true)
        // preserve the tool body so we can manage its lifetime here.
        //
        // Two cases:
        //   • Tool was selected as a whole sheet body: delete it directly.
        //   • Tool was selected as a face: resolve the owning non-construction sheet body
        //     and delete that.  Solid bodies and SM definition bodies are never touched.
        // Construction planes and mate-connector temp planes are handled separately.
        if (!definition.keepTools)
        {
            var toolBodiesToDelete = qEntityFilter(effectiveTool, EntityType.BODY);
            if (isQueryEmpty(context, toolBodiesToDelete))
            {
                const faceToolQuery = qEntityFilter(effectiveTool, EntityType.FACE);
                if (!isQueryEmpty(context, faceToolQuery))
                {
                    const ownerBodies = qOwnerBody(faceToolQuery);
                    const nonConstructionOwnerBodies = qConstructionFilter(ownerBodies, ConstructionObject.NO);
                    toolBodiesToDelete = qBodyType(nonConstructionOwnerBodies, BodyType.SHEET);
                }
            }
            if (!isQueryEmpty(context, toolBodiesToDelete))
            {
                opDeleteBodies(context, id + "deleteTool", { "entities" : toolBodiesToDelete });
            }
        }

        // Always remove any temporary planar bodies created from mate connectors.
        // Neither opSplitFace nor opSplitPart cleans these up automatically.
        if (temporaryPlaneQueries != [])
        {
            opDeleteBodies(context, id + "deleteTempPlanes", { "entities" : qUnion(temporaryPlaneQueries) });
        }
    },
    { keepTools : false, keepBothSides : true, keepFront : true, splitMode : ButcherMode.CHAINSAW,
      projectionType : ProjectionType.DIRECTION, useSketchPlaneDirection : true });


// ─── Manipulator ─────────────────────────────────────────────────────────────

/**
 * Adds a flip manipulator positioned on the splitting tool face nearest to the sheet metal
 * definition bodies, allowing the user to interactively choose which side of the split to keep.
 * Only called for chainsaw mode; scalpel mode has no keep-side option.
 *
 * @param id {Id}
 * @param definition {map} : feature definition (reads tool, keepFront)
 * @param definitionBodies {Query} : sheet metal master surface definition bodies used to
 *                                   find the closest point for manipulator placement
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
 * Manipulator change handler for the Butcher Sheet Metal feature.
 * Responds to the flip manipulator by updating the keepFront field.
 */
export function butcherSheetMetalManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
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
 * Returns the wall face entities of the sheet metal definition body that correspond to
 * the selected solid output parts.
 *
 * Each face of a solid SM output part carries an SMAssociationAttribute whose value is
 * shared with the corresponding face in the master surface definition body.  Walking
 * this link gives exactly the subset of definition body faces that belong to the
 * selected parts, without touching faces that belong to other parts in the same body.
 *
 * @param solidParts {Query} : the solid SM output parts the user selected
 * @param definitionBody {Query} : the shared master surface definition body
 * @returns {Query} : face entities owned by definitionBody that correspond to solidParts
 */
function collectDefinitionFacesForSelectedParts(context is Context, solidParts is Query, definitionBody is Query) returns Query
{
    // Get all faces of the selected solid output parts.
    const solidPartFaces = qOwnedByBody(solidParts, EntityType.FACE);

    // Get the face-level SMAssociationAttributes.  Each solid face carries one, and
    // its attribute value is shared with the matching definition body face.
    const faceAssociations = getSMAssociationAttributes(context, solidPartFaces);

    var definitionFaceQueries = [];
    for (var assoc in faceAssociations)
    {
        // qAttributeQuery returns every entity that carries this attribute value, which
        // includes both the solid face and the corresponding definition body face.
        // Intersecting with faces owned by the definition body isolates the definition face.
        const definitionFace = qIntersection([
            qEntityFilter(qAttributeQuery(assoc), EntityType.FACE),
            qOwnedByBody(definitionBody, EntityType.FACE)
        ]);
        if (!isQueryEmpty(context, definitionFace))
        {
            definitionFaceQueries = append(definitionFaceQueries, definitionFace);
        }
    }

    return qUnion(definitionFaceQueries);
}

/**
 * Builds the parameter map for opSplitFace, classifying the cutting tool into the correct
 * field (bodyTools, planeTools, or faceTools) that opSplitFace expects, and threading
 * projection options through for parity with the standard face split (splitpart.fs).
 *
 * Construction plane faces (including temporary planes created from mate connectors) are
 * placed in planeTools so they are treated as infinite extents.  Sheet body tools are
 * placed in bodyTools with keepToolSurfaces = true so we can manage their lifetime
 * ourselves (respecting the keepTools checkbox).  All other faces go to faceTools.
 *
 * When projectionType is DIRECTION and edge or wire body tools are present, an explicit
 * direction is resolved from useSketchPlaneDirection / directionQuery, matching the
 * setDirectionForEdgeTools logic in splitpart.fs.
 *
 * @param faceTargets {Query} : the definition body wall faces to split
 * @param tool {Query} : the cutting tool query (after mate-connector conversion)
 * @param definition {map} : feature definition (reads projectionType, useSketchPlaneDirection, directionQuery)
 * @returns {map} : parameter map suitable for passing directly to opSplitFace
 */
function buildOpSplitFaceDefinition(context is Context, faceTargets is Query, tool is Query, definition is map) returns map
{
    var splitDefinition = { "faceTargets" : faceTargets };

    // Separate the tool query into its constituent entity types.
    // Each type maps to a distinct opSplitFace parameter name (parity with splitpart.fs).
    const toolBodies = qEntityFilter(tool, EntityType.BODY);
    const toolFaces  = qEntityFilter(tool, EntityType.FACE);
    // Non-construction edges (e.g. sketch edges) feed the "edgeTools" parameter of opSplitFace.
    const toolEdges  = qConstructionFilter(qEntityFilter(tool, EntityType.EDGE), ConstructionObject.NO);

    // Sketch bodies in Onshape are BodyType.WIRE or BodyType.SHEET bodies with SketchObject.YES.
    // They cannot be passed to opSplitFace as bodyTools — the engine only accepts non-sketch
    // sheet or wire bodies there.  Use qSketchFilter to separate them and expand each sketch body
    // to its owned non-construction edges so opSplitFace receives the same edge geometry the
    // user would get by selecting sketch edges one-by-one.
    const sketchWireSheetBodies    = qSketchFilter(toolBodies, SketchObject.YES);
    const nonSketchWireSheetBodies = qSketchFilter(toolBodies, SketchObject.NO);
    const expandedSketchBodyEdges = qConstructionFilter(
        qOwnedByBody(sketchWireSheetBodies, EntityType.EDGE),
        ConstructionObject.NO
    );
    // Merge directly-selected edges with edges expanded from any selected sketch body.
    const allEdgeTools = qUnion([toolEdges, expandedSketchBodyEdges]);

    if (!isQueryEmpty(context, nonSketchWireSheetBodies))
    {
        // Sheet or wire body tool: preserve it so we can delete it ourselves per keepTools.
        splitDefinition["bodyTools"]        = nonSketchWireSheetBodies;
        splitDefinition["keepToolSurfaces"] = true;
    }

    if (!isQueryEmpty(context, toolFaces))
    {
        // Construction plane faces (and mate-connector temp planes from opPlane) are
        // treated as infinite so the split extends fully across each targeted face.
        const constructionFaces = qConstructionFilter(toolFaces, ConstructionObject.YES);
        const regularFaces      = qConstructionFilter(toolFaces, ConstructionObject.NO);

        if (!isQueryEmpty(context, constructionFaces))
        {
            splitDefinition["planeTools"] = constructionFaces;
        }
        if (!isQueryEmpty(context, regularFaces))
        {
            splitDefinition["faceTools"] = regularFaces;
        }
    }

    if (!isQueryEmpty(context, allEdgeTools))
    {
        // Sketch edge (and other non-construction edge) tools are projected onto the target
        // faces using the configured projection type and direction.  Without this assignment
        // opSplitFace never receives the edge geometry and reports nothing selected.
        // When the user selects an entire sketch body, its owned non-construction edges are
        // expanded above (expandedSketchBodyEdges) and merged here.
        splitDefinition["edgeTools"] = allEdgeTools;
    }

    // Thread projectionType through to opSplitFace for parity with the standard face split.
    splitDefinition["projectionType"] = definition.projectionType;

    // Resolve an explicit split direction when using direction projection with edge or
    // wire body tools.  Mirrors setDirectionForEdgeTools in splitpart.fs.
    if (definition.projectionType == ProjectionType.DIRECTION)
    {
        const wireBodyTools = qBodyType(nonSketchWireSheetBodies, BodyType.WIRE);

        if (!isQueryEmpty(context, allEdgeTools) || !isQueryEmpty(context, wireBodyTools))
        {
            var splitDirection = undefined;
            if (definition.useSketchPlaneDirection)
            {
                // Use the normal of the sketch plane that owns the sketch edges.
                // Build a combined query:
                //   - qSketchFilter on directly-selected toolEdges for individual edge picks
                //   - expandedSketchBodyEdges passed directly (owned by a sketch body, so
                //     evOwnerSketchPlane can resolve their plane even without SketchObject.YES)
                // allEdgeTools is NOT filtered by SketchObject.YES here to avoid the case
                // where edges expanded via qOwnedByBody do not carry that attribute.
                const sketchEdgesForPlaneDetection = qUnion([
                    qSketchFilter(toolEdges, SketchObject.YES),
                    expandedSketchBodyEdges
                ]);
                if (!isQueryEmpty(context, sketchEdgesForPlaneDetection))
                {
                    const sketchPlane = try(evOwnerSketchPlane(context, {
                        "entity" : sketchEdgesForPlaneDetection
                    }));
                    if (sketchPlane != undefined)
                    {
                        splitDirection = sketchPlane.normal;
                    }
                }
            }
            else
            {
                // extractDirection handles axes and planar faces; returns undefined if
                // the query yields no usable direction.
                splitDirection = extractDirection(context, definition.directionQuery);
            }

            if (splitDirection != undefined)
            {
                splitDefinition["direction"] = splitDirection;
            }
            else if (!definition.useSketchPlaneDirection)
            {
                throw regenError(ErrorStringEnum.SPLIT_SELECT_FACE_DIRECTION, ["directionQuery"]);
            }
        }
    }

    return splitDefinition;
}

/**
 * For each mate connector found in the tools query, creates a temporary planar body whose
 * plane matches the mate connector's XY plane, so opSplitFace can use it as a cutting tool.
 * Returns an array of face queries, one per created plane.
 *
 * @param id {Id}
 * @param tools {Query} : the tool selection to scan for mate connectors
 * @returns {array} : array of Query values, each pointing to one temporary planar face
 */
function buildTemporaryPlanesForMateConnectors(context is Context, id is Id, tools is Query) returns array
{
    var temporaryPlanes = [];
    var planeIndex = 0;
    for (var tool in evaluateQuery(context, tools))
    {
        const coordinateSystem = try(evMateConnector(context, { "mateConnector" : tool }));
        if (coordinateSystem != undefined)
        {
            const planeId = id + "mateConnectorPlane" + unstableIdComponent(planeIndex);
            setExternalDisambiguation(context, planeId, tool);
            opPlane(context, planeId, { "plane" : plane(coordinateSystem) });
            temporaryPlanes = append(temporaryPlanes, qEntityFilter(qCreatedBy(planeId), EntityType.FACE));
            planeIndex += 1;
        }
    }
    return temporaryPlanes;
}

/**
 * Creates a RIP joint SMAttribute for the given split edge with ALL fields marked as
 * canBeEdited: true, including the rip angle.
 *
 * This differs from the standard library's createRipAttribute which marks the angle
 * field as canBeEdited: false.  Keeping every field editable ensures the Modify Joints
 * panel remains fully interactive after the Butcher Sheet Metal feature is applied.
 *
 * @param entity {Query} : the split edge to attribute
 * @param ripId {string} : unique attribute identifier (produced by toAttributeId)
 * @returns {SMAttribute} : a JOINT / RIP attribute with all fields editable
 */
function createEditableRipAttribute(context is Context, entity is Query, ripId is string) returns SMAttribute
{
    var ripAttribute = makeSMJointAttribute(ripId);
    ripAttribute.jointType = { "value" : SMJointType.RIP, "canBeEdited" : true };

    // try silent mirrors the pattern in the standard library's createRipAttribute
    // (sheetMetalUtils.fs).  An undefined angle is a valid state for a flat-sheet rip
    // edge (zero dihedral angle) — the attribute is still valid without the angle field.
    const angle = try silent(edgeAngle(context, entity));
    if (angle != undefined)
    {
        // Set canBeEdited: true on the angle so the Modify Joints panel is not locked.
        // The standard createRipAttribute sets this to false (angle is read-only there).
        ripAttribute.angle = { "value" : angle, "canBeEdited" : true };
    }

    // Only set a joint style when the rip edge has a non-zero dihedral angle,
    // consistent with the gate used by createRipAttribute in sheetMetalUtils.fs.
    if (angle != undefined && abs(angle) >= TOLERANCE.zeroAngle * radian)
    {
        ripAttribute.jointStyle = { "value" : SMJointStyle.EDGE, "canBeEdited" : true };
    }

    return ripAttribute;
}
