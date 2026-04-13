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
//   3. ALL tagged surface bodies are thickened using frontThickness + backThickness from
//      the target SM model's getModelParameters before any boolean operation is performed.
//   4. Bodies tagged with role "smTabUnionSurface" are merged into the selected SM wall faces
//      via the standard sheetMetalTab approach.
//   5. Bodies tagged with role "smTabLocalSubtractBody" (thickened) are subtracted from the
//      merged SM wall only.
//   6. Bodies tagged with role "smTabOuterSubtractBody" (thickened) are subtracted across
//      the user-defined outer subtraction scope (other SM faces or solid bodies).
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
                    "Description" : "Sheet metal wall definition faces to merge the thickened tab surface into.",
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
        // Phase 4 — Resolve SM model parameters from the union scope wall.
        // getModelParameters reads the SM attribute on the owning model body.
        // ------------------------------------------------------------------
        const unionWallDefinitionEntities = getSMDefinitionEntities(context, definition.unionScope);
        if (unionWallDefinitionEntities == undefined || unionWallDefinitionEntities == [])
        {
            throw regenError("Could not resolve sheet metal definition entities from union scope.", ["unionScope"]);
        }
        const unionSMModelBody = qOwnerBody(qUnion(unionWallDefinitionEntities));
        const modelParameters  = getModelParameters(context, unionSMModelBody);

        // Total tab thickness equals front + back thickness of the SM model.
        const totalThickness = modelParameters.frontThickness + modelParameters.backThickness;

        // ------------------------------------------------------------------
        // Phase 5 — Thicken ALL tagged surface bodies into solids.
        // All three roles (union, local subtract, outer subtract) are surface bodies.
        // Each set is thickened separately so the resulting solid queries stay distinct.
        // frontThickness drives the positive-normal direction; backThickness drives the
        // negative-normal direction, matching the SM wall's neutral surface position.
        // ------------------------------------------------------------------
        const thickenedUnionId = id + "thickenUnionSurfaces";
        opThicken(context, thickenedUnionId, {
                    "entities"   : unionSurfaceBodies,
                    "thickness1" : modelParameters.frontThickness,
                    "thickness2" : modelParameters.backThickness
                });
        const thickenedUnionSolids = qCreatedBy(thickenedUnionId, EntityType.BODY)->qBodyType(BodyType.SOLID);

        var thickenedLocalSubtractSolids = qNothing();
        if (!isQueryEmpty(context, localSubtractBodies))
        {
            const thickenedLocalId = id + "thickenLocalSubtractSurfaces";
            opThicken(context, thickenedLocalId, {
                        "entities"   : localSubtractBodies,
                        "thickness1" : modelParameters.frontThickness,
                        "thickness2" : modelParameters.backThickness
                    });
            thickenedLocalSubtractSolids = qCreatedBy(thickenedLocalId, EntityType.BODY)->qBodyType(BodyType.SOLID);
        }

        var thickenedOuterSubtractSolids = qNothing();
        if (!isQueryEmpty(context, outerSubtractBodies))
        {
            const thickenedOuterId = id + "thickenOuterSubtractSurfaces";
            opThicken(context, thickenedOuterId, {
                        "entities"   : outerSubtractBodies,
                        "thickness1" : modelParameters.frontThickness,
                        "thickness2" : modelParameters.backThickness
                    });
            thickenedOuterSubtractSolids = qCreatedBy(thickenedOuterId, EntityType.BODY)->qBodyType(BodyType.SOLID);
        }

        // ------------------------------------------------------------------
        // Phase 6 — Track SM model state before boolean operations.
        // ------------------------------------------------------------------
        const smBodiesAffected = qOwnerBody(qUnion(unionWallDefinitionEntities));
        const initialData      = getInitialEntitiesAndAttributes(context, smBodiesAffected);
        const trackedSMBodies  = qUnion([startTracking(context, smBodiesAffected), smBodiesAffected]);
        const associateChanges = startTracking(context, qOwnedByBody(smBodiesAffected, EntityType.FACE));

        // ------------------------------------------------------------------
        // Phase 7 — Union the thickened union solids into the SM wall.
        // Mirrors the approach used by the standard sheetMetalTab feature.
        // ------------------------------------------------------------------
        const tabUnionDefinitionEntities = qUnion(getSMDefinitionEntities(context, definition.unionScope));
        const tabUnionBodies             = evaluateQuery(context, qOwnerBody(tabUnionDefinitionEntities));

        opBoolean(context, id + "unionTabToWall", {
                    "tools"         : qUnion([qUnion(tabUnionBodies), thickenedUnionSolids]),
                    "targets"       : qUnion(tabUnionBodies),
                    "operationType" : BooleanOperationType.UNION
                });

        // ------------------------------------------------------------------
        // Phase 8 — Local subtraction (wall-scoped cuts).
        // Thickened local subtract solids cut the merged SM wall.
        // opBoolean SUBTRACTION consumes the tool bodies.
        // ------------------------------------------------------------------
        if (!isQueryEmpty(context, thickenedLocalSubtractSolids))
        {
            opBoolean(context, id + "localSubtract", {
                        "tools"         : thickenedLocalSubtractSolids,
                        "targets"       : qUnion(tabUnionBodies),
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "targetsAndToolsNeedGrouping" : true
                    });
        }

        // ------------------------------------------------------------------
        // Phase 9 — Outer subtraction (broader scope cuts).
        // Thickened outer subtract solids cut across the user-defined scope.
        // An optional opOffsetFace expands/contracts the thickened solids before
        // cutting, which is the correct place for offset on a solid tool.
        // opBoolean SUBTRACTION consumes the tool bodies.
        // ------------------------------------------------------------------
        if (!isQueryEmpty(context, thickenedOuterSubtractSolids) && !isQueryEmpty(context, definition.outerSubtractionScope))
        {
            // Apply offset to the thickened outer subtraction solids when requested.
            // Offsetting after thickening keeps the operation on solid geometry, which is
            // the correct target for opOffsetFace when resizing a cut envelope.
            if (definition.outerSubtractionOffset > 0 * meter)
            {
                opOffsetFace(context, id + "offsetOuterSubtractTools", {
                            "moveFaces" : qOwnedByBody(thickenedOuterSubtractSolids, EntityType.FACE),
                            "offsetDistance" : definition.outerSubtractionOffset
                        });
            }

            // Resolve outer scope: separate SM definition entities from plain solid bodies.
            const outerScopeDefinitionEntities = try silent(getSMDefinitionEntities(context, definition.outerSubtractionScope));
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
        // All thickened solids were consumed by the boolean operations above, so
        // only the original surface bodies and the placement connector remain.
        // ------------------------------------------------------------------
        const bodiesToDelete = qUnion([csysConnectorBodies, unionSurfaceBodies, localSubtractBodies, outerSubtractBodies]);
        if (!isQueryEmpty(context, bodiesToDelete))
        {
            opDeleteBodies(context, id + "cleanupBodies", { "entities" : bodiesToDelete });
        }

        // ------------------------------------------------------------------
        // Phase 11 — Update the sheet metal model to recognise the new geometry.
        // ------------------------------------------------------------------
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, trackedSMBodies, initialData, id);
        updateSheetMetalGeometry(context, id, {
                    "entities"           : qUnion([toUpdate.modifiedEntities, qUnion(tabUnionBodies)]),
                    "deletedAttributes"  : toUpdate.deletedAttributes,
                    "associatedChanges"  : associateChanges
                });

        // ------------------------------------------------------------------
        // Phase 12 — Resolve feature name from the tool Part Studio variable.
        // ------------------------------------------------------------------
        var resolvedFeatureName = "";
        try silent
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
