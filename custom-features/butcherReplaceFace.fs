FeatureScript 2960;
// Butcher Replace Face Feature
// A sheet metal analogous version of the standard Replace Face feature (replaceFace.fs).
//
// The standard Replace Face directly edits a 3D body with opReplaceFace.  On an active
// sheet metal part the 3D solid is only a derived representation of an invisible master
// surface definition body, which cannot be edited by the user directly.  This feature
// resolves the selected resultant 3D model faces back to their corresponding master
// definition faces (via getSMDefinitionEntities), runs opReplaceFace on the master
// surfaces, and then rebuilds the sheet metal definition with updateSheetMetalGeometry.
//
// The pipeline mirrors the surface path of Move Face's offsetSheetMetalFaces
// (moveFace.fs:807) — map model faces to master faces, snapshot/track, operate on the
// master surfaces, then assignSMAttributesToNewOrSplitEntities + updateSheetMetalGeometry.
// This does not handle sheet metal edge selections; only wall surface faces are rebased.

// Imports used in interface
export import(path : "onshape/std/query.fs", version : "2960.0");
export import(path : "onshape/std/tool.fs", version : "2960.0");
export import(path : "onshape/std/manipulator.fs", version : "2960.0");

// Imports used internally
import(path : "onshape/std/attributes.fs", version : "2960.0");
import(path : "onshape/std/evaluate.fs", version : "2960.0");
import(path : "onshape/std/feature.fs", version : "2960.0");
import(path : "onshape/std/geomOperations.fs", version : "2960.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2960.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2960.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2960.0");
import(path : "onshape/std/valueBounds.fs", version : "2960.0");
import(path : "onshape/std/vector.fs", version : "2960.0");
import(path : "onshape/std/containers.fs", version : "2960.0");
import(path : "onshape/std/uihint.gen.fs", version : "2960.0");
import(path : "onshape/std/debug.fs", version : "2960.0");

/**
 * Rebases one or more sheet metal wall faces onto the geometry of another face by
 * operating on the underlying master surface definition body, then rebuilding the
 * sheet metal geometry.
 *
 * The faces to replace are resultant 3D model faces (the only faces a user can pick);
 * they are mapped back to their master definition faces before opReplaceFace runs,
 * matching how Move Face operates on master surfaces.  The template face is used directly
 * as reference geometry and may be any face, not just a sheet metal wall.
 */
annotation { "Feature Type Name" : "Butcher replace face",
             "Filter Selector" : "allparts",
             "Manipulator Change Function" : "butcherReplaceFaceManipulatorChange" }
export const butcherReplaceFace = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Faces to replace: resultant 3D model faces on an active sheet metal part.
        // ActiveSheetMetal.YES keeps this scoped to live SM geometry; the master surface
        // definition is resolved internally — users cannot select master faces directly.
        annotation { "Name" : "Faces to replace",
                     "UIHint" : UIHint.SHOW_CREATE_SELECTION,
                     "Filter" : EntityType.FACE && ActiveSheetMetal.YES && ConstructionObject.NO && SketchObject.NO && ModifiableEntityOnly.YES }
        definition.replaceFaces is Query;

        // Template face: a 3D model face whose geometry the replaced faces will lie on.
        annotation { "Name" : "Face to replace with", "Filter" : EntityType.FACE }
        definition.templateFace is Query;

        annotation { "Name" : "Flip alignment", "Default" : false }
        definition.oppositeSense is boolean;

        annotation { "Name" : "Offset distance" }
        isLength(definition.offset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.oppositeDirection is boolean;
    }
    {
        // ── Validate selections ────────────────────────────────────────────────────
        if (isQueryEmpty(context, definition.replaceFaces))
        {
            throw regenError("Select at least one sheet metal face to replace", ["replaceFaces"]);
        }
        if (isQueryEmpty(context, definition.templateFace))
        {
            throw regenError("Select a face to replace with", ["templateFace"]);
        }

        var offset = definition.offset;
        if (definition.oppositeDirection)
            offset = -offset;

        // ── Map the faces being edited from 3D model faces to master definition faces ──
        // Users can only pick resultant 3D model faces; opReplaceFace must edit the
        // invisible master surface definition that those faces are derived from.  The
        // template face only supplies reference geometry, so it is passed through
        // unchanged and may be any face (including non-sheet-metal faces).
        const masterReplaceFaces = qEntityFilter(qUnion(getSMDefinitionEntities(context, definition.replaceFaces, EntityType.FACE)), EntityType.FACE);
        const masterTemplateFace = definition.templateFace;
        if (isQueryEmpty(context, masterReplaceFaces))
        {
            throw regenError("Could not resolve the master definition faces for the faces to replace", ["replaceFaces"]);
        }

        // ── DEBUG: report selection -> master-face mapping ─────────────────────────
        // Selected resultant faces highlight CYAN, resolved master faces MAGENTA.
        println("[butcherReplaceFace] selected replaceFaces count = " ~ size(evaluateQuery(context, definition.replaceFaces)));
        println("[butcherReplaceFace] resolved masterReplaceFaces count = " ~ size(evaluateQuery(context, masterReplaceFaces)));
        println("[butcherReplaceFace] offset (signed) = " ~ toString(offset));
        debug(context, definition.replaceFaces, DebugColor.CYAN);
        debug(context, masterReplaceFaces, DebugColor.MAGENTA);

        // ── Locate the SM definition bodies and snapshot their state ───────────────
        // Track every face of the SM definition body (not just the selected faces), started
        // BEFORE opReplaceFace. opReplaceFace deletes and regenerates the targeted faces, so a
        // tracking query anchored only to masterReplaceFaces resolves empty afterward; the
        // SM rebuild then sees no associated change and defers — the 3D/flat only refresh once a
        // later tool dirties the master surfaces. Body-wide face tracking survives the regenerate.
        const sheetMetalModels = qOwnerBody(masterReplaceFaces);
        const initialData = getInitialEntitiesAndAttributes(context, sheetMetalModels);
        const trackingSMModel = startTracking(context, sheetMetalModels);
        const associatedChanges = startTracking(context, qOwnedByBody(sheetMetalModels, EntityType.FACE));

        // ── DEBUG: report SM definition body and pre-op body-wide face count ───────
        println("[butcherReplaceFace] SM definition body count = " ~ size(evaluateQuery(context, sheetMetalModels)));
        println("[butcherReplaceFace] body-wide faces tracked BEFORE op = " ~ size(evaluateQuery(context, qOwnedByBody(sheetMetalModels, EntityType.FACE))));

        // ── Manipulator on the template face so the offset can be dragged ───────────
        // try mirrors replaceFace.fs: skip the manipulator if the template face has no
        // tangent plane yet (e.g. selection still being defined) without failing the feature.
        const templateFacePlane = try(computeFacePlane(context, definition.templateFace, definition.oppositeSense));
        if (templateFacePlane != undefined)
        {
            addOffsetManipulator(context, id, definition, templateFacePlane);
        }

        // ── Replace the master surfaces with the template geometry ─────────────────
        println("[butcherReplaceFace] calling opReplaceFace...");
        opReplaceFace(context, id + "replaceFace", {
                    "replaceFaces"  : masterReplaceFaces,
                    "templateFace"  : masterTemplateFace,
                    "offset"        : offset,
                    "oppositeSense" : definition.oppositeSense
                });
        println("[butcherReplaceFace] opReplaceFace returned");
        // After the op the body-wide tracking is the only anchor that survives the face
        // regenerate; if this resolves empty the rebuild will defer and the 3D/flat stay stale.
        println("[butcherReplaceFace] associatedChanges resolved AFTER op = " ~ size(evaluateQuery(context, associatedChanges)));
        debug(context, associatedChanges, DebugColor.YELLOW);

        // ── Stamp rips on any new boundary edges and rebuild the sheet metal ───────
        // Confine every downstream query to the sheet metal definition bodies. opReplaceFace
        // edits the master surface in place, but the template face lives on another body; if
        // any of its entities leaked into the update set, updateSheetMetalGeometry would check
        // the SM version flag on a non-sheet-metal body and fail.
        const definitionBodies = qUnion([trackingSMModel, sheetMetalModels]);
        const robustReplaceFaces = qUnion([masterReplaceFaces, associatedChanges]);
        const modifiedFaces = qOwnedByBody(qAdjacent(robustReplaceFaces, AdjacencyType.EDGE, EntityType.FACE), definitionBodies);
        addRipsForReplacedFaceEdges(context, id, qAdjacent(robustReplaceFaces, AdjacencyType.EDGE, EntityType.EDGE)->qOwnedByBody(definitionBodies));

        // ── DEBUG: report rebuild inputs; modified faces GREEN ─────────────────────
        println("[butcherReplaceFace] modifiedFaces count = " ~ size(evaluateQuery(context, modifiedFaces)));
        debug(context, modifiedFaces, DebugColor.GREEN);

        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, definitionBodies, initialData, id);
        println("[butcherReplaceFace] toUpdate.modifiedEntities count = " ~ size(evaluateQuery(context, toUpdate.modifiedEntities)));
        println("[butcherReplaceFace] calling updateSheetMetalGeometry...");
        callSubfeatureAndProcessStatus(id, updateSheetMetalGeometry, context, id + "smUpdate", {
                    "entities" : qOwnedByBody(qUnion([toUpdate.modifiedEntities, modifiedFaces, associatedChanges]), definitionBodies),
                    "deletedAttributes" : toUpdate.deletedAttributes,
                    "associatedChanges" : associatedChanges
                });
        println("[butcherReplaceFace] updateSheetMetalGeometry returned");
    }, { oppositeSense : false, oppositeDirection : false });

//======================= Manipulators ==========================

const OFFSET_MANIPULATOR = "offsetManipulator";

/**
 * Adds a linear offset manipulator on the template face, allowing the offset distance to
 * be dragged interactively. Mirrors addOffsetManipulator in replaceFace.fs.
 *
 * @param id {Id}
 * @param definition {map} : feature definition (reads offset, oppositeSense)
 * @param replaceFacePlane {Plane} : tangent plane of the template face, normal pre-flipped
 */
function addOffsetManipulator(context is Context, id is Id, definition is map, replaceFacePlane is Plane)
{
    addManipulators(context, id, {
                (OFFSET_MANIPULATOR) : linearManipulator({
                            "base" : replaceFacePlane.origin,
                            "direction" : replaceFacePlane.normal,
                            "offset" : definition.offset,
                            "primaryParameterId" : "offset"
                        })
            });
}

/**
 * Evaluates the tangent plane at the center of the template face, flipping the normal
 * when oppositeSense is set so the manipulator points the same way as the offset.
 *
 * @param templateFace {Query} : the template face selection
 * @param oppositeSense {boolean} : whether the resulting normal should be reversed
 * @returns {Plane}
 */
function computeFacePlane(context is Context, templateFace is Query, oppositeSense is boolean) returns Plane
{
    var facePlane = evFaceTangentPlane(context, { "face" : templateFace, "parameter" : vector(0.5, 0.5) });
    if (oppositeSense)
        facePlane.normal = -facePlane.normal;
    return facePlane;
}

/**
 * @internal
 * Manipulator change function for `butcherReplaceFace`. Responds to the linear offset
 * manipulator by updating the offset and direction fields.
 */
export function butcherReplaceFaceManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    if (newManipulators[OFFSET_MANIPULATOR] != undefined)
    {
        definition.oppositeDirection = newManipulators[OFFSET_MANIPULATOR].offset < 0 * meter;
        definition.offset = abs(newManipulators[OFFSET_MANIPULATOR].offset);
    }
    return definition;
}

//======================= Helpers ==========================

/**
 * Stamps a RIP joint attribute on each newly created two-sided wall boundary edge that
 * does not already carry a joint attribute. Mirrors the private addRipsForNewEdges in
 * moveFace.fs, using the standard library's createRipAttribute (sheetMetalUtils.fs).
 *
 * @param id {Id}
 * @param edges {Query} : candidate edges adjacent to the replaced master faces
 */
function addRipsForReplacedFaceEdges(context is Context, id is Id, edges is Query)
{
    var jointIndex = 0;
    for (var edge in evaluateQuery(context, edges))
    {
        const jointAttribute = try(getJointAttribute(context, edge));
        if (jointAttribute != undefined)
        {
            continue;
        }
        const adjacentFaces = evaluateQuery(context, qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE));
        // Only rip edges between two wall faces, matching the standard move face gate.
        if (size(adjacentFaces) == 2 && size(getSmObjectTypeAttributes(context, qUnion(adjacentFaces), SMObjectType.WALL)) == 2)
        {
            const ripAttribute = createRipAttribute(context, edge, toAttributeId(id + "joint" + jointIndex), SMJointStyle.EDGE, {});
            if (ripAttribute != undefined)
            {
                setAttribute(context, { "entities" : edge, "attribute" : ripAttribute });
            }
        }
        jointIndex += 1;
    }
}

