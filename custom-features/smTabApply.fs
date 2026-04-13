FeatureScript 2909;
// SM Tab Apply — Place a tagged tab tool Part Studio onto a sheet metal model.
//
// This feature is the apply half of the SM Tab workflow.  The tag half lives in smTabTag.fs.
//
// How it works
//   1. The user selects a tool Part Studio that has been prepared with smTabTag.  No
//      ComputedConfigurationInputs are needed because thickness is never authored in the
//      tool Part Studio — it is always read from the target SM model at apply-time.
//   2. At each placement location the instantiator derives the tool Part Studio at the
//      requested coordinate system (driven by a mate connector or sketch vertex).
//   3. Bodies tagged with role "smTabUnionSurface" are merged into the selected SM wall faces
//      using a surface-to-surface opBoolean UNION (allowSheets: true), mirroring the approach
//      used by the standard sheetMetalTab feature.  No thickening is performed for union.
//   4. Bodies tagged with role "smTabLocalSubtractBody" cut the merged SM wall using a
//      surface-to-surface opBoolean SUBTRACTION (allowSheets: true).  No thickening needed.
//   5. Bodies tagged with role "smTabOuterSubtractBody" are thickened using getModelParameters
//      from the target SM wall and then subtracted across the user-defined outer scope.
//      Thickening is required here because outer scope targets may include solid bodies.
//
// Forward-compatibility note
//   Because ALL geometry is kept as pure surface bodies with no embedded thickness, a
//   future opWrap path for non-planar SM walls can be added to this script without any
//   changes to the tag Part Studio contract.

import(path : "onshape/std/attributes.fs", version : "2909.0");
import(path : "onshape/std/boolean.fs", version : "2909.0");
import(path : "onshape/std/containers.fs", version : "2909.0");
import(path : "onshape/std/coordSystem.fs", version : "2909.0");
import(path : "onshape/std/error.fs", version : "2909.0");
import(path : "onshape/std/evaluate.fs", version : "2909.0");
import(path : "onshape/std/feature.fs", version : "2909.0");
import(path : "onshape/std/geomOperations.fs", version : "2909.0");
import(path : "onshape/std/instantiator.fs", version : "2909.0");
import(path : "onshape/std/query.fs", version : "2909.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2909.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2909.0");
import(path : "onshape/std/valueBounds.fs", version : "2909.0");
import(path : "onshape/std/transform.fs", version : "2909.0");
import(path : "onshape/std/units.fs", version : "2909.0");
import(path : "onshape/std/vector.fs", version : "2909.0");
export import(path : "onshape/std/mateconnectoraxistype.gen.fs", version : "2909.0");

// ---------------------------------------------------------------------------
// Attribute name constants — must match smTabTag.fs
// ---------------------------------------------------------------------------

const SM_TAB_BODY_ATTRIBUTE_NAME   = "smTabBodyAttribute";
const SM_TAB_ROLE_UNION_SURFACE    = "smTabUnionSurface";
const SM_TAB_ROLE_LOCAL_SUBTRACT   = "smTabLocalSubtractBody";
const SM_TAB_ROLE_OUTER_SUBTRACT   = "smTabOuterSubtractBody";
const SM_TAB_FEATURE_NAME_VAR      = "smTabFeatureName";

const FEATURE_NAME_SEPARATOR = " - ";

// ---------------------------------------------------------------------------
// Feature definition
// ---------------------------------------------------------------------------

/**
 * SM Tab Apply feature.
 *
 * Places a tab tool Part Studio (prepared with SM Tab Tag) at one or more locations on a
 * sheet metal model.  Thickness is read from the target SM model at regeneration time so
 * the tool Part Studio never needs to encode gauge information.
 *
 * Feature Name Template: displays "SM Tab Apply - <name>" when a feature name is resolved
 * from the tool Part Studio.
 */
annotation { "Feature Type Name" : "SM Tab Apply", "Feature Name Template" : "SM Tab Apply#featureName" }
export const smTabApply = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // ------------------------------------------------------------------
        // Tool Part Studio — no ComputedConfigurationInputs because thickness
        // is not a configuration parameter in the tag Part Studio.
        // ------------------------------------------------------------------
        annotation {
                    "Name" : "Tab tool Part Studio",
                    "Description" : "A Part Studio prepared with the SM Tab Tag feature.",
                    "Filter" : PartStudioItemType.ENTIRE_PART_STUDIO,
                    "MaxNumberOfPicks" : 1,
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
                }
        definition.formPartStudio is PartStudioData;

        // ------------------------------------------------------------------
        // Placement locations — mate connectors or sketch vertices.
        // ------------------------------------------------------------------
        annotation {
                    "Name" : "Location(s)",
                    "Filter" : BodyType.MATE_CONNECTOR || (EntityType.VERTEX && SketchObject.YES && ModifiableEntityOnly.YES)
                }
        definition.locations is Query;

        // ------------------------------------------------------------------
        // Orientation overrides
        // ------------------------------------------------------------------
        annotation { "Name" : "Flip direction", "UIHint" : [UIHint.OPPOSITE_DIRECTION, UIHint.FIRST_IN_ROW] }
        definition.flipDirection is boolean;

        annotation { "Name" : "Reorient secondary axis", "UIHint" : UIHint.MATE_CONNECTOR_AXIS_TYPE }
        definition.secondaryAxisType is MateConnectorAxisType;

        // ------------------------------------------------------------------
        // SM wall union scope — the SM definition faces to merge the tab into.
        // ------------------------------------------------------------------
        annotation {
                    "Name" : "Union scope",
                    "Description" : "Sheet metal wall definition faces to merge the tab surface into.",
                    "Filter" : SheetMetalDefinitionEntityType.FACE && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES
                }
        definition.unionScope is Query;

        // ------------------------------------------------------------------
        // Outer subtraction — offset applied to tagged outer subtraction tools
        // before cutting, and the scope of bodies to cut.
        // ------------------------------------------------------------------
        annotation { "Name" : "Outer subtraction offset" }
        isLength(definition.outerSubtractionOffset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation {
                    "Name" : "Outer subtraction scope",
                    "Filter" : (SheetMetalDefinitionEntityType.FACE && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES) ||
                               (EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES && ActiveSheetMetal.NO)
                }
        definition.outerSubtractionScope is Query;

        // ------------------------------------------------------------------
        // Hidden computed feature name populated from the tool Part Studio variable.
        // ------------------------------------------------------------------
        annotation { "Name" : "Feature name (computed)", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.featureName is string;
    }
    {
        // ------------------------------------------------------------------
        // Phase 1 — Input validation
        // ------------------------------------------------------------------
        if (isQueryEmpty(context, definition.locations))
        {
            throw regenError(ErrorStringEnum.FORMED_SELECT_LOCATION, ["locations"]);
        }

        if (isQueryEmpty(context, definition.unionScope))
        {
            throw regenError("Select at least one sheet metal wall face in Union scope.", ["unionScope"]);
        }

        // ------------------------------------------------------------------
        // Phase 2 — Instantiate the tool Part Studio at every requested location.
        // The csys mate connector tagged by smTabTag drives the fromWorld transform.
        // ------------------------------------------------------------------
        const instantiator = newInstantiator(id + "instantiate");
        var allInstantiatedBodies = qNothing();

        for (var location in evaluateQuery(context, definition.locations))
        {
            var placementCSys = resolveLocationCSys(context, location);
            placementCSys = applyOrientationOverrides(placementCSys, definition.flipDirection, definition.secondaryAxisType);

            const instanceBodies = addInstance(instantiator, definition.formPartStudio, {
                        "transform"    : toWorld(placementCSys),
                        "identity"     : location,
                        "mateConnector" : qHasAttributeWithValueMatching(
                                qEverything(EntityType.BODY)->qBodyType(BodyType.MATE_CONNECTOR),
                                SM_TAB_BODY_ATTRIBUTE_NAME,
                                { "role" : "smTabCsysMateConnector" }
                            )
                    });
            allInstantiatedBodies = qUnion([allInstantiatedBodies, instanceBodies]);
        }

        try
        {
            instantiate(context, instantiator);
        }
        catch
        {
            throw regenError(ErrorStringEnum.FORMED_FAILED_TO_DERIVE, ["formPartStudio"]);
        }

        // ------------------------------------------------------------------
        // Phase 3 — Identify role-tagged bodies from the instantiated set.
        // ------------------------------------------------------------------
        const unionSurfaceBodies   = qHasAttributeWithValueMatching(allInstantiatedBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_UNION_SURFACE });
        const localSubtractBodies  = qHasAttributeWithValueMatching(allInstantiatedBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_LOCAL_SUBTRACT });
        const outerSubtractBodies  = qHasAttributeWithValueMatching(allInstantiatedBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_OUTER_SUBTRACT });
        const csysConnectorBodies  = qHasAttributeWithValueMatching(allInstantiatedBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : "smTabCsysMateConnector" });

        if (isQueryEmpty(context, unionSurfaceBodies))
        {
            throw regenError("The tool Part Studio contains no bodies tagged as union surfaces. Run SM Tab Tag in the tool Part Studio.", ["formPartStudio"]);
        }

        // ------------------------------------------------------------------
        // Phase 4 — Resolve SM definition entities from the union scope wall.
        // These are used for SM state tracking (Phase 6) and the union target
        // body (Phase 7).  Model parameters are resolved later, conditionally,
        // only if outer subtract bodies are present and need thickening.
        // ------------------------------------------------------------------
        const unionWallDefinitionEntities = getSMDefinitionEntities(context, definition.unionScope);
        if (unionWallDefinitionEntities == undefined || unionWallDefinitionEntities == [])
        {
            throw regenError("Could not resolve sheet metal definition entities from union scope.", ["unionScope"]);
        }

        // ------------------------------------------------------------------
        // Phase 5 — Thicken outer subtract surface bodies into solids.
        // Union and local subtract bodies remain as surfaces for surface-to-
        // surface boolean operations (allowSheets: true), matching the standard
        // sheetMetalTab approach.  Outer subtract bodies are thickened because
        // their scope may include plain solid bodies that require solid tools.
        //
        // The full SM wall thickness is applied in BOTH normal directions so
        // the resulting solid always penetrates through the wall regardless of
        // which way the surface normal points in the tool Part Studio.
        // ------------------------------------------------------------------
        var thickenedOuterSubtractSolids = qNothing();
        if (!isQueryEmpty(context, outerSubtractBodies))
        {
            const unionSMModelBody = qOwnerBody(qUnion(unionWallDefinitionEntities));
            const modelParameters  = getModelParameters(context, unionSMModelBody);
            const totalThickness   = modelParameters.frontThickness + modelParameters.backThickness;
            const thickenedOuterId = id + "thickenOuterSubtractSurfaces";
            opThicken(context, thickenedOuterId, {
                        "entities"   : outerSubtractBodies,
                        "thickness1" : totalThickness,
                        "thickness2" : totalThickness
                    });
            thickenedOuterSubtractSolids = qCreatedBy(thickenedOuterId, EntityType.BODY)->qBodyType(BodyType.SOLID);
        }

        // ------------------------------------------------------------------
        // Phase 6 — Track SM model state before boolean operations.
        //
        // persistentUnionDefinitionEntities mirrors the unionEntityPersistantQuery
        // pattern in sheetMetalTab.fs — it is built from the SM definition faces
        // (not the SM body) with startTracking so updateSheetMetalGeometry (Phase
        // 11) correctly identifies the modified region after the boolean operations.
        // ------------------------------------------------------------------
        const smBodiesAffected = qOwnerBody(qUnion(unionWallDefinitionEntities));
        const initialData      = getInitialEntitiesAndAttributes(context, smBodiesAffected);
        const trackedSMBodies  = qUnion([startTracking(context, smBodiesAffected), smBodiesAffected]);
        const associateChanges = startTracking(context, qOwnedByBody(smBodiesAffected, EntityType.FACE));

        const unionDefinitionEntitiesQuery    = qUnion(unionWallDefinitionEntities);
        const persistentUnionDefinitionEntities = qUnion([unionDefinitionEntitiesQuery, startTracking(context, unionDefinitionEntitiesQuery)]);

        // ------------------------------------------------------------------
        // Phase 7 — Union the tab surface bodies into the SM master surface.
        //
        // smBodiesAffected is the SM master surface body (the invisible model body
        // that the user cannot select directly).  It is derived from the user's
        // unionScope selection via getSMDefinitionEntities + qOwnerBody.
        //
        // The standard sheetMetalTab feature passes only "tools" for UNION
        // (no "targets" field) and includes the wall body alongside the tab
        // surface so that all bodies are merged into a single sheet body.
        // ------------------------------------------------------------------
        opBoolean(context, id + "unionTabToWall", {
                    "tools"         : qUnion([smBodiesAffected, unionSurfaceBodies]),
                    "operationType" : BooleanOperationType.UNION,
                    "allowSheets"   : true
                });

        // ------------------------------------------------------------------
        // Phase 8 — Local subtraction (wall-scoped cuts).
        // Surface-to-surface boolean with allowSheets: true.  The surface tool
        // bodies cut directly against the SM master surface (smBodiesAffected).
        // Targeting the SM master surface body is correct here because the user
        // cannot select the invisible master surface — it is resolved automatically
        // from the unionScope selection through getSMDefinitionEntities + qOwnerBody.
        // opBoolean SUBTRACTION consumes the tool bodies.
        // ------------------------------------------------------------------
        if (!isQueryEmpty(context, localSubtractBodies))
        {
            opBoolean(context, id + "localSubtract", {
                        "tools"         : localSubtractBodies,
                        "targets"       : smBodiesAffected,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "allowSheets"   : true
                    });
        }

        // ------------------------------------------------------------------
        // Phase 9 — Outer subtraction (broader scope cuts).
        // Thickened outer subtract solids cut across the user-defined scope.
        // An optional opOffsetFace expands/contracts the thickened solids before
        // cutting (offsetting a solid is the correct operation here).
        // opBoolean SUBTRACTION consumes the tool bodies.
        // ------------------------------------------------------------------
        if (!isQueryEmpty(context, thickenedOuterSubtractSolids) && !isQueryEmpty(context, definition.outerSubtractionScope))
        {
            // Apply offset to the thickened outer subtraction solids when requested.
            if (definition.outerSubtractionOffset > 0 * meter)
            {
                opOffsetFace(context, id + "offsetOuterSubtractTools", {
                            "moveFaces" : qOwnedByBody(thickenedOuterSubtractSolids, EntityType.FACE),
                            "offsetDistance" : definition.outerSubtractionOffset
                        });
            }

            // Resolve outer scope: separate SM definition entities from plain solid bodies.
            // getSMDefinitionEntities may throw for non-SM selections; a non-SM outer scope
            // is valid (plain solids only), so we catch the error and proceed with an empty array.
            var outerScopeDefinitionEntities = [];
            try
            {
                outerScopeDefinitionEntities = getSMDefinitionEntities(context, definition.outerSubtractionScope);
            }
            catch
            {
                // Outer subtraction scope contains no SM definition entities; solid targets will
                // still be resolved below via qActiveSheetMetalFilter.
            }
            var outerScopeTargets = qNothing();
            if (outerScopeDefinitionEntities != undefined && outerScopeDefinitionEntities != [])
            {
                outerScopeTargets = qUnion([outerScopeTargets, qOwnerBody(qUnion(outerScopeDefinitionEntities))]);
            }
            const outerScopeSolids = qActiveSheetMetalFilter(qBodyType(definition.outerSubtractionScope, BodyType.SOLID), ActiveSheetMetal.NO);
            if (!isQueryEmpty(context, outerScopeSolids))
            {
                outerScopeTargets = qUnion([outerScopeTargets, outerScopeSolids]);
            }

            if (!isQueryEmpty(context, outerScopeTargets))
            {
                opBoolean(context, id + "outerSubtract", {
                            "tools"         : thickenedOuterSubtractSolids,
                            "targets"       : outerScopeTargets,
                            "operationType" : BooleanOperationType.SUBTRACTION,
                            "targetsAndToolsNeedGrouping" : true
                        });
            }
        }

        // ------------------------------------------------------------------
        // Phase 10 — Clean up original surface bodies and the csys connector.
        // - unionSurfaceBodies: consumed by the UNION boolean above.
        // - localSubtractBodies: consumed by the local SUBTRACTION boolean above.
        // - outerSubtractBodies: originals were thickened into solids that were
        //   consumed by the outer SUBTRACTION boolean.  The original surfaces
        //   still exist and need explicit deletion.
        // - csysConnectorBodies: placement helper, no longer needed.
        // ------------------------------------------------------------------
        const bodiesToDelete = qUnion([csysConnectorBodies, unionSurfaceBodies, localSubtractBodies, outerSubtractBodies]);
        if (!isQueryEmpty(context, bodiesToDelete))
        {
            opDeleteBodies(context, id + "cleanupBodies", { "entities" : bodiesToDelete });
        }

        // ------------------------------------------------------------------
        // Phase 11 — Update the sheet metal model to recognise the new geometry.
        // persistentUnionDefinitionEntities mirrors the unionEntityPersistantQuery
        // pattern in sheetMetalTab.fs — passing the SM definition entities (faces)
        // with tracking, not the SM body, drives the correct SM attribute update.
        // ------------------------------------------------------------------
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, trackedSMBodies, initialData, id);
        updateSheetMetalGeometry(context, id, {
                    "entities"           : qUnion([toUpdate.modifiedEntities, persistentUnionDefinitionEntities]),
                    "deletedAttributes"  : toUpdate.deletedAttributes,
                    "associatedChanges"  : associateChanges
                });

        // ------------------------------------------------------------------
        // Phase 12 — Resolve feature name from the tool Part Studio variable.
        // ------------------------------------------------------------------
        // Retrieve the feature name from the source Part Studio context.
        // The variable may not exist if the user left the name blank in smTabTag.fs.
        // An explicit catch handles that case without suppressing unexpected error details.
        var resolvedFeatureName = "";
        try
        {
            var sourceConfig = {};
            if (definition.formPartStudio.configuration != undefined)
            {
                sourceConfig = definition.formPartStudio.configuration;
            }
            const sourceContext = definition.formPartStudio.buildFunction(sourceConfig);
            const retrievedName = getVariable(sourceContext, SM_TAB_FEATURE_NAME_VAR);
            if (retrievedName != undefined && retrievedName is string && retrievedName != "")
            {
                resolvedFeatureName = FEATURE_NAME_SEPARATOR ~ retrievedName;
            }
        }
        catch
        {
            // SM_TAB_FEATURE_NAME_VAR was not set in the tool Part Studio (user left name blank).
            // Feature name template will display as "SM Tab Apply" with no suffix.
        }
        setFeatureComputedParameter(context, id, { "name" : "featureName", "value" : resolvedFeatureName });

    }, {
            flipDirection            : false,
            secondaryAxisType        : MateConnectorAxisType.PLUS_X,
            outerSubtractionOffset   : 0 * millimeter,
            outerSubtractionScope    : qNothing(),
            featureName              : ""
        });

// ---------------------------------------------------------------------------
// Helper functions
// ---------------------------------------------------------------------------

/**
 * Resolve a coordinate system from a placement location.
 * Accepts either a mate connector body or a sketch vertex.
 *
 * @param context   {Context}
 * @param location  {Query}   A single mate connector or sketch vertex.
 * @returns {CoordSystem}
 */
function resolveLocationCSys(context is Context, location is Query) returns CoordSystem
{
    if (!isQueryEmpty(context, location->qBodyType(BodyType.MATE_CONNECTOR)))
    {
        return evMateConnector(context, { "mateConnector" : location });
    }

    // Sketch vertex: use the sketch plane normal as Z and sketch X as X.
    const sketchPlane = evOwnerSketchPlane(context, { "entity" : location });
    const vertexPoint = evVertexPoint(context, { "vertex" : location });
    return coordSystem(vertexPoint, sketchPlane.x, sketchPlane.normal);
}

/**
 * Apply flip and secondary-axis orientation overrides to a resolved coordinate system.
 *
 * @param placementCSys      {CoordSystem}           Coordinate system to modify.
 * @param flipDirection      {boolean}               When true, negate the Z axis.
 * @param secondaryAxisType  {MateConnectorAxisType} Rotates the X axis around Z.
 * @returns {CoordSystem}
 */
function applyOrientationOverrides(placementCSys is CoordSystem, flipDirection is boolean, secondaryAxisType is MateConnectorAxisType) returns CoordSystem
{
    var xAxis = placementCSys.xAxis;
    var zAxis = placementCSys.zAxis;

    if (flipDirection)
    {
        zAxis = -zAxis;
    }

    if (secondaryAxisType == MateConnectorAxisType.PLUS_Y)
    {
        xAxis = cross(zAxis, xAxis);
    }
    else if (secondaryAxisType == MateConnectorAxisType.MINUS_X)
    {
        xAxis = -xAxis;
    }
    else if (secondaryAxisType == MateConnectorAxisType.MINUS_Y)
    {
        xAxis = -cross(zAxis, xAxis);
    }
    // MateConnectorAxisType.PLUS_X is the default; no adjustment needed.

    return coordSystem(placementCSys.origin, xAxis, zAxis);
}
