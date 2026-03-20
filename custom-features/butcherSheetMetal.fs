FeatureScript 2909;
// Butcher Sheet Metal Feature
// Splits one or more active sheet metal parts using one of two operating modes:
//
// CHAINSAW mode — calls opSplitPart on the shared master surface definition body.
//   The entire definition body (which may serve multiple solid output parts) is split
//   into new sheet body pieces.  Fast and blunt: every part in the shared definition
//   body is affected, regardless of which parts were individually selected.
//
// SCALPEL mode — calls opSplitFace scoped exclusively to the definition body wall
//   faces that are associated with the selected solid output parts.  Parts in the same
//   definition body that were NOT selected are completely untouched.  New split
//   boundary edges are stamped with editable RIP joint attributes (all fields set to
//   canBeEdited: true) so the Modify Joints panel remains live after the feature.
//
// Both modes share the same parameters and post-split SM rebuild pipeline
// (assignSMAttributesToNewOrSplitEntities + updateSheetMetalGeometry).

// Imports used in interface
export import(path : "onshape/std/query.fs", version : "2909.0");
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
import(path : "onshape/std/vector.fs", version : "2909.0");
import(path : "onshape/std/geomOperations.fs", version : "2909.0");
import(path : "onshape/std/uihint.gen.fs", version : "2909.0");

/**
 * Operating mode for the Butcher Sheet Metal feature.
 *
 * CHAINSAW - Direct opSplitPart on the entire SM definition body.  Every part that
 *            shares the definition body is split (or the body becomes two new bodies).
 *            Fast and blunt: no per-part face-level scoping.
 *
 * SCALPEL  - opSplitFace scoped exclusively to the definition body faces that belong
 *            to the selected solid output parts.  Parts sharing the same definition body
 *            that are NOT selected are never touched.  New split boundary edges receive
 *            editable RIP joint attributes so the Modify Joints panel stays live.
 */
enum ButcherMode
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
        annotation { "Name" : "Split mode", "Default" : ButcherMode.SCALPEL }
        definition.splitMode is ButcherMode;

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
        // ── Validate targets ──────────────────────────────────────────────────────
        if (isQueryEmpty(context, definition.targets))
        {
            throw regenError("Select at least one active sheet metal part to split", ["targets"]);
        }

        // ── Warn when a single face tool is always kept, mirroring standard split ─
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

        // ── Convert mate connectors to temporary planar bodies ────────────────────
        // This is done once so the same plane can be used for every face target group.
        const temporaryPlaneQueries = buildTemporaryPlanesForMateConnectors(context, id, definition.tool);
        const temporaryPlaneCount = size(temporaryPlaneQueries);
        if (temporaryPlaneCount == 1)
        {
            definition.tool = temporaryPlaneQueries[0];
        }
        else if (temporaryPlaneCount > 1)
        {
            throw regenError("Only one splitting tool may be selected", ["tool"], definition.tool);
        }

        const effectiveTool = definition.tool;

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

        // ── Extract the tool plane before any geometry changes ────────────────────
        // Needed for single-side mode to classify which fragments/bodies to discard.
        var toolPlane = undefined;
        if (!definition.keepBothSides)
        {
            toolPlane = try(extractToolPlaneForSplit(context, effectiveTool));
            if (toolPlane == undefined)
            {
                throw regenError("Cannot determine the splitting plane for single-side mode", ["tool"]);
            }
        }

        // ── Snapshot the definition body state before any geometry changes ─────────
        const initialData = getInitialEntitiesAndAttributes(context, definitionBodyQuery);
        const trackedDefinitionBody = startTracking(context, definitionBodyQuery);

        // ── Attach the flip manipulator for single-side mode ──────────────────────
        if (!definition.keepBothSides)
        {
            addSplitSideManipulator(context, id, definition, definitionBodyQuery);
        }

        // ── SCALPEL only: track individual faces for single-side fragment collection ─
        var selectedFaceTrackers = [];
        if (definition.splitMode == ButcherMode.SCALPEL && !definition.keepBothSides)
        {
            for (var faceToTrack in evaluateQuery(context, selectedDefinitionFaces))
            {
                selectedFaceTrackers = append(selectedFaceTrackers, startTracking(context, faceToTrack));
            }
        }

        // ── Execute the split ─────────────────────────────────────────────────────
        const splitId = id + "split";
        if (definition.splitMode == ButcherMode.CHAINSAW)
        {
            // Chainsaw: opSplitPart on the entire definition body.  Creates new sheet
            // body pieces — one per side of the cut.  keepTools: true so we manage
            // tool deletion ourselves in the cleanup section.
            opSplitPart(context, splitId, {
                "targets"   : definitionBodyQuery,
                "tool"      : effectiveTool,
                "keepTools" : true
            });
        }
        else
        {
            // Scalpel: opSplitFace confined to definition body faces belonging to the
            // selected solid parts.  All other faces in the shared definition body remain
            // untouched.  keepToolSurfaces: true (set inside buildOpSplitFaceDefinition
            // for body tools) so we manage deletion ourselves.
            const splitResult = opSplitFace(context, splitId,
                                            buildOpSplitFaceDefinition(context, selectedDefinitionFaces, effectiveTool));

            // In keep-both-sides mode: stamp every new split boundary edge with an
            // editable RIP joint attribute.  All fields (including angle) are marked
            // canBeEdited: true so the Modify Joints panel remains live and fully
            // interactive after the feature is applied.
            if (definition.keepBothSides)
            {
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
        }

        // ── Post-split: discard unwanted fragments when keeping only one side ──────
        if (!definition.keepBothSides)
        {
            const zeroLength = 0 * meter;
            if (definition.splitMode == ButcherMode.CHAINSAW)
            {
                // Chainsaw single-side: opSplitPart produced new sheet bodies.
                // Classify each body by its centroid relative to the tool plane and
                // delete the bodies on the unwanted side.
                const resultingBodies = evaluateQuery(context,
                    qEntityFilter(trackedDefinitionBody, EntityType.BODY));
                var bodiesToDiscard = [];
                for (var bodyFragment in resultingBodies)
                {
                // Signed distance from the tool plane: positive means "front" (on the normal side).
                // FeatureScript's evDistance returns only unsigned magnitude, so the dot product
                // of (centroid − planeOrigin) with the unit plane normal is the correct and
                // only way to obtain a signed scalar for side classification.
                const centroid = evApproximateCentroid(context, { "entities" : bodyFragment });
                const signedDistance = dot(centroid - toolPlane.origin, toolPlane.normal);
                    const isOnFront = signedDistance > zeroLength;
                    const shouldDiscard = definition.keepFront ? !isOnFront : isOnFront;
                    if (shouldDiscard)
                    {
                        bodiesToDiscard = append(bodiesToDiscard, bodyFragment);
                    }
                }
                if (bodiesToDiscard != [])
                {
                    opDeleteBodies(context, id + "deleteDiscardedBodies", {
                        "entities" : qUnion(bodiesToDiscard)
                    });
                }
            }
            else
            {
                // Scalpel single-side: opSplitFace produced new face fragments.
                // Collect the tracked surviving fragments plus any new face IDs,
                // then classify by centroid and remove the unwanted faces.
                var allSplitFragments = [];
                for (var tracker in selectedFaceTrackers)
                {
                    allSplitFragments = concatenateArrays([allSplitFragments, evaluateQuery(context, tracker)]);
                }
                allSplitFragments = concatenateArrays([allSplitFragments,
                                                       evaluateQuery(context, qCreatedBy(splitId, EntityType.FACE))]);

                var facesToDiscard = [];
                for (var fragment in allSplitFragments)
                {
                    // Signed distance from the tool plane: positive means "front" (on the normal side).
                    // FeatureScript's evDistance returns only unsigned magnitude, so the dot product
                    // of (centroid − planeOrigin) with the unit plane normal is the correct and
                    // only way to obtain a signed scalar for side classification.
                    const centroid = evApproximateCentroid(context, { "entities" : fragment });
                    const signedDistance = dot(centroid - toolPlane.origin, toolPlane.normal);
                    const isOnFront = signedDistance > zeroLength;
                    const shouldDiscard = definition.keepFront ? !isOnFront : isOnFront;
                    if (shouldDiscard)
                    {
                        facesToDiscard = append(facesToDiscard, fragment);
                    }
                }

                // leaveOpen = true is required for surface bodies.
                if (facesToDiscard != [])
                {
                    opDeleteFace(context, id + "deleteDiscardedFragments", {
                        "deleteFaces"   : qUnion(facesToDiscard),
                        "includeFillet" : false,
                        "capVoid"       : false,
                        "leaveOpen"     : true
                    });
                }
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
    { keepTools : false, keepBothSides : true, keepFront : true, splitMode : ButcherMode.SCALPEL });


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
 * field (bodyTools, planeTools, or faceTools) that opSplitFace expects.
 *
 * Construction plane faces (including temporary planes created from mate connectors) are
 * placed in planeTools so they are treated as infinite extents.  Sheet body tools are
 * placed in bodyTools with keepToolSurfaces = true so we can manage their lifetime
 * ourselves (respecting the keepTools checkbox).  All other faces go to faceTools.
 *
 * @param faceTargets {Query} : the definition body wall faces to split
 * @param tool {Query} : the cutting tool query (after mate-connector conversion)
 * @returns {map} : parameter map suitable for passing directly to opSplitFace
 */
function buildOpSplitFaceDefinition(context is Context, faceTargets is Query, tool is Query) returns map
{
    var splitDefinition = { "faceTargets" : faceTargets };

    const toolBodies = qEntityFilter(tool, EntityType.BODY);
    const toolFaces  = qEntityFilter(tool, EntityType.FACE);

    if (!isQueryEmpty(context, toolBodies))
    {
        // Sheet body tool: preserve it so we can delete it ourselves per keepTools.
        splitDefinition["bodyTools"]        = toolBodies;
        splitDefinition["keepToolSurfaces"] = true;
    }
    else if (!isQueryEmpty(context, toolFaces))
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

    return splitDefinition;
}

/**
 * Returns a Plane extracted from the cutting tool, used to classify face fragments as
 * "front" (positive normal side) or "back" for the single-side keep mode.
 *
 * For face or construction-plane tools the tangent plane at the face's parametric centre
 * is sampled.  For sheet body tools the first face of the body is sampled.
 *
 * @param tool {Query} : the cutting tool query (after mate-connector conversion)
 * @returns {Plane} : a representative plane describing the tool's orientation
 */
function extractToolPlaneForSplit(context is Context, tool is Query) returns Plane
{
    // Try as a face (covers face tools, construction planes, and mate-connector temp planes)
    const faceQuery = qEntityFilter(tool, EntityType.FACE);
    if (!isQueryEmpty(context, faceQuery))
    {
        return evFaceTangentPlane(context, {
            "face"      : qNthElement(faceQuery, 0),
            "parameter" : vector(0.5, 0.5)
        });
    }

    // Try as a sheet body: sample the first face of the body
    const bodyQuery = qEntityFilter(tool, EntityType.BODY);
    if (!isQueryEmpty(context, bodyQuery))
    {
        const bodyFace = qNthElement(qOwnedByBody(bodyQuery, EntityType.FACE), 0);
        return evFaceTangentPlane(context, {
            "face"      : bodyFace,
            "parameter" : vector(0.5, 0.5)
        });
    }

    throw "Unable to extract a representative plane from the cutting tool";
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
