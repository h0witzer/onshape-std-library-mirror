FeatureScript 2909;
// Sheet Metal Split Feature
// Splits an active sheet metal part by operating on only the master surface definition
// body faces that are actually associated with the selected solid output parts, then
// triggering a sheet metal rebuild to update all part representations.
//
// The standard split feature cannot operate on active sheet metal parts because of the
// special tracking, associations, and attribute system used by the sheet metal engine.
// This feature works around that limitation by:
//   1. Resolving the exact definition body wall faces associated with each selected solid part
//      (via face-level SMAssociationAttribute links, not the whole definition body)
//   2. Performing opSplitFace on only those faces — faces that belong to other parts
//      sharing the same definition body are never touched
//   3. For keepBothSides = true: assigning RIP joint attributes to the new boundary edges
//      so the SM engine treats the split line as a physical separation between two distinct
//      solid output parts (same pattern used by sheetMetalRip.fs)
//   4. For keepBothSides = false: deleting the unwanted face fragments from the definition
//      body using opDeleteFace (leaveOpen = true), leaving only the wanted side
//   5. Calling assignSMAttributesToNewOrSplitEntities and updateSheetMetalGeometry to
//      propagate attributes and rebuild the solid parts

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
 * Splits one or more active sheet metal parts by targeting only the underlying master
 * surface definition body faces associated with the selected solid output parts, then
 * rebuilds the sheet metal geometry from the modified surfaces.
 *
 * Unlike the standard Split feature, this feature correctly handles the sheet metal
 * association and attribute tracking system, so the resulting halves are both valid
 * active sheet metal parts.
 *
 * Selective scoping: when a single sheet metal model produces multiple solid output parts
 * from one shared definition body (for example, walls connected by bends vs. walls
 * separated only by rip joints), only the definition body faces that are explicitly
 * associated with the selected parts are split.  Faces belonging to unselected parts
 * in the same definition body remain completely untouched.
 *
 * This is achieved by using opSplitFace with a scoped faceTargets query (resolved via
 * face-level SMAssociationAttribute links from the solid part faces to the definition
 * body faces) rather than opSplitPart on the entire shared definition body.
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

        // ── Resolve the SPECIFIC definition body faces for the selected parts ─────
        // Walking the face-level SMAssociationAttribute links from the solid part faces
        // to the definition body faces gives us a scoped faceTargets query.  opSplitFace
        // will only modify these faces; all other faces in the shared definition body
        // (belonging to unselected parts) are left completely untouched.
        const selectedDefinitionFaces = collectDefinitionFacesForSelectedParts(context, definition.targets, definitionBodyQuery);
        if (isQueryEmpty(context, selectedDefinitionFaces))
        {
            throw regenError("Could not resolve the sheet metal definition faces for the selected parts", ["targets"]);
        }

        // ── Extract the tool plane before any geometry changes ────────────────────
        // Needed for single-side mode to classify which face fragments to discard.
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

        // ── Track each selected definition face individually for fragment collection ─
        // Needed only in single-side mode: we collect both the tracked (surviving) and
        // newly-created face fragments so we can classify each one against the tool plane.
        var selectedFaceTrackers = [];
        if (!definition.keepBothSides)
        {
            for (var faceToTrack in evaluateQuery(context, selectedDefinitionFaces))
            {
                selectedFaceTrackers = append(selectedFaceTrackers, startTracking(context, faceToTrack));
            }
        }

        // ── Split ONLY the selected definition faces ───────────────────────────────
        // opSplitFace confines the geometry change to exactly the wall faces that belong
        // to the selected solid parts.  All other faces in the same definition body remain
        // untouched.  This is the key difference from opSplitPart, which would always
        // split the entire shared definition body.
        const splitId = id + "splitFaces";
        opSplitFace(context, splitId, buildOpSplitFaceDefinition(context, selectedDefinitionFaces, effectiveTool));

        // ── Post-split: apply RIP joints or delete unwanted fragments ─────────────
        if (definition.keepBothSides)
        {
            // Assign a RIP joint attribute to every new edge created at the split boundary.
            // Without this the SM engine does not know to separate the two halves of the
            // selected part into distinct solid output parts.  This is the same pattern
            // used by the sheet metal Rip feature (sheetMetalRip.fs).
            const newSplitEdges = evaluateQuery(context, qCreatedBy(splitId, EntityType.EDGE));
            for (var splitEdgeIndex = 0; splitEdgeIndex < size(newSplitEdges); splitEdgeIndex += 1)
            {
                setAttribute(context, {
                    "entities"  : newSplitEdges[splitEdgeIndex],
                    "attribute" : createRipAttribute(context, newSplitEdges[splitEdgeIndex],
                                                     toAttributeId(id + "rip" + splitEdgeIndex),
                                                     SMJointStyle.EDGE, {})
                });
            }
        }
        else
        {
            // Collect all face fragments produced by the split: the "surviving" tracked
            // fragment for each originally-targeted face plus any new fragments created
            // by the split operation (which carry brand-new transient IDs).
            var allSplitFragments = [];
            for (var tracker in selectedFaceTrackers)
            {
                allSplitFragments = concatenateArrays([allSplitFragments, evaluateQuery(context, tracker)]);
            }
            allSplitFragments = concatenateArrays([allSplitFragments,
                                                   evaluateQuery(context, qCreatedBy(splitId, EntityType.FACE))]);

            // Classify each fragment against the tool plane and collect the ones to discard.
            // A fragment whose centroid lies on the positive-normal side of the tool plane
            // is considered "front"; the other side is "back".
            var facesToDiscard = [];
            for (var fragment in allSplitFragments)
            {
                const centroid = evApproximateCentroid(context, { "entities" : fragment });
                const signedDistance = dot(centroid - toolPlane.origin, toolPlane.normal);
                const isOnFront = signedDistance > 0 * meter;
                const shouldDiscard = definition.keepFront ? !isOnFront : isOnFront;
                if (shouldDiscard)
                {
                    facesToDiscard = append(facesToDiscard, fragment);
                }
            }

            // Remove the discarded face fragments from the definition body.
            // leaveOpen = true is required for surface bodies (confirmed by sheetMetalBend.fs).
            // Adjacent edges become open boundary edges of the remaining fragments, which
            // the SM engine correctly treats as cut edges of the trimmed sheet.
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

        // ── Propagate SM attributes and rebuild ────────────────────────────────────
        // opSplitFace does not create new bodies, so postSplitBodiesQuery is the same
        // definition body (augmented by the tracking query in case of body-level changes).
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
        // opSplitFace preserves the tool (keepToolSurfaces: true), so we delete it
        // manually here when the user has not checked "Keep tools".  Face tools and
        // construction planes are non-deletable and are handled by the info message above.
        if (!definition.keepTools)
        {
            const toolBodyQuery = qEntityFilter(effectiveTool, EntityType.BODY);
            if (!isQueryEmpty(context, toolBodyQuery))
            {
                opDeleteBodies(context, id + "deleteTool", { "entities" : toolBodyQuery });
            }
        }

        // Always remove any temporary planar bodies created from mate connectors.
        // opSplitFace does not delete construction planes regardless of keepToolSurfaces.
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
