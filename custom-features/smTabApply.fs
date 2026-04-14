FeatureScript 2909;
// SM Tab Apply — Place a tagged tab tool Part Studio onto a sheet metal model.
// The tag half of this workflow lives in smTabTag.fs.  Thickness is resolved from the
// target SM model at apply-time; the tool Part Studio never encodes gauge.
//
// Union surface bodies merge into the SM wall via opBoolean UNION.  Local subtract bodies
// cut the merged wall immediately after union.  Outer subtract bodies (or implied copies
// of the union surfaces) are thickened using SM model parameters and subtracted across
// the user-defined outer scope following the sheetMetalTab.fs pattern.

import(path : "onshape/std/attributes.fs", version : "2909.0");
import(path : "onshape/std/boolean.fs", version : "2909.0");
import(path : "onshape/std/containers.fs", version : "2909.0");
import(path : "onshape/std/coordSystem.fs", version : "2909.0");
import(path : "onshape/std/error.fs", version : "2909.0");
import(path : "onshape/std/evaluate.fs", version : "2909.0");
import(path : "onshape/std/feature.fs", version : "2909.0");
import(path : "onshape/std/geomOperations.fs", version : "2909.0");
import(path : "onshape/std/instantiator.fs", version : "2909.0");
import(path : "onshape/std/moveFace.fs", version : "2909.0");
import(path : "onshape/std/query.fs", version : "2909.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2909.0");
import(path : "onshape/std/sheetMetalTab.fs", version : "2909.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2909.0");
import(path : "onshape/std/string.fs", version : "2909.0");
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

        // Per-location body set queries — resolved after instantiate().
        // Each entry is the query returned by addInstance for one placement location.
        // Phases 7 and 8 process union and local subtract bodies per location so that
        // a boolean failure at one location does not prevent other locations from merging.
        var locationBodySets = [];

        for (var location in evaluateQuery(context, definition.locations))
        {
            var placementCSys = resolveLocationCSys(context, location);
            placementCSys = applyOrientationOverrides(placementCSys, definition.flipDirection, definition.secondaryAxisType);

            const instanceBodies = addInstance(instantiator, definition.formPartStudio, {
                        "transform" : toWorld(placementCSys),
                        "identity"  : location
                    });
            allInstantiatedBodies = qUnion([allInstantiatedBodies, instanceBodies]);
            locationBodySets = append(locationBodySets, instanceBodies);
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
        // Used for SM state tracking (Phase 8) and as union targets in Phase 9b.
        // Model parameters are resolved conditionally in Phase 7 when outer
        // subtract bodies need thickening.
        // ------------------------------------------------------------------
        const unionWallDefinitionEntities = getSMDefinitionEntities(context, definition.unionScope);
        if (unionWallDefinitionEntities == undefined || unionWallDefinitionEntities == [])
        {
            throw regenError("Could not resolve sheet metal definition entities from union scope.", ["unionScope"]);
        }

        // ------------------------------------------------------------------
        // Phase 5 — Snap union and local subtract surface bodies onto the SM definition face.
        //
        // Aligns each derived surface body to be exactly coplanar with its nearest SM
        // definition face.  Corrects orientation via opFlipOrientation when normals are
        // antiparallel, then translates along the wall normal to achieve geometric coincidence.
        // ------------------------------------------------------------------
        var smDefinitionFacePlanes = [];
        for (var smFace in unionWallDefinitionEntities)
        {
            var definitionFacePlane = undefined;
            try
            {
                definitionFacePlane = evPlane(context, { "face" : smFace });
            }
            catch
            {
                // SM wall face not planar — should not occur; skip.
            }
            if (definitionFacePlane != undefined)
            {
                smDefinitionFacePlanes = append(smDefinitionFacePlanes, definitionFacePlane);
            }
        }
        if (size(smDefinitionFacePlanes) > 0)
        {
            snapBodiesToNearestDefinitionPlane(context, id + "snapUnionBodies", unionSurfaceBodies, smDefinitionFacePlanes);
            if (!isQueryEmpty(context, localSubtractBodies))
            {
                snapBodiesToNearestDefinitionPlane(context, id + "snapLocalSubtractBodies", localSubtractBodies, smDefinitionFacePlanes);
            }
        }

        // ------------------------------------------------------------------
        // Phase 6 — Implied outer subtraction bodies.
        // When no outer subtraction bodies were tagged and an outer scope is defined,
        // copy the snapped union surface bodies to act as implied clearance cutters.
        // ------------------------------------------------------------------
        const impliedOuterSubtractBodies = buildImpliedOuterSubtractBodies(context, id, outerSubtractBodies, unionSurfaceBodies, definition.outerSubtractionScope);

        // ------------------------------------------------------------------
        // Phase 7 — Thicken outer subtract surface bodies into solids.
        //
        // Finds the nearest outer scope SM definition face per subtract body using
        // evFaceTangentPlane, corrects surface orientation when needed, then
        // thickens using the matched SM wall's frontThickness / backThickness.
        // ------------------------------------------------------------------

        // Resolve outer scope SM definition faces for Phase 7 thickening orientation detection.
        // Phase 10 makes a fresh getSMDefinitionEntities call after Phase 9a/9b mutations.
        var outerScopeDefinitionFaces = [];
        if (!isQueryEmpty(context, definition.outerSubtractionScope))
        {
            try
            {
                outerScopeDefinitionFaces = getSMDefinitionEntities(context, definition.outerSubtractionScope);
            }
            catch
            {
                // Outer scope contains no SM definition entities (solid-only scope).
            }
        }

        var thickenedOuterSubtractSolids = qNothing();
        if (!isQueryEmpty(context, outerSubtractBodies))
        {
            // Pre-evaluate tangent planes for all outer scope SM definition faces.
            var outerScopeFaceData = [];
            for (var smFace in outerScopeDefinitionFaces)
            {
                var wallTangent = undefined;
                try
                {
                    wallTangent = evFaceTangentPlane(context, {
                                "face"      : smFace,
                                "parameter" : vector(0.5, 0.5)
                            });
                }
                catch
                {
                    // Skip faces that cannot be evaluated.
                }

                if (wallTangent != undefined)
                {
                    var wallModelParams = undefined;
                    try
                    {
                        wallModelParams = getModelParameters(context, qOwnerBody(smFace));
                    }
                    catch
                    {
                        // Face may not belong to an active SM model.
                    }
                    outerScopeFaceData = append(outerScopeFaceData, {
                                "origin"      : wallTangent.origin,
                                "normal"      : wallTangent.normal,
                                "modelParams" : wallModelParams
                            });
                }
            }

            // Fallback model parameters from the union scope wall when no outer
            // scope SM face data is available (e.g. solid-only outer scope).
            const unionSMBody    = qOwnerBody(qUnion(unionWallDefinitionEntities));
            const fallbackParams = getModelParameters(context, unionSMBody);

            const outerSubtractBodyArray = evaluateQuery(context, outerSubtractBodies);
            for (var outerSubtractBodyIndex = 0; outerSubtractBodyIndex < size(outerSubtractBodyArray); outerSubtractBodyIndex += 1)
            {
                const currentBody = outerSubtractBodyArray[outerSubtractBodyIndex];

                // For each face of this outer subtract surface body, find the
                // closest outer scope SM definition face by Euclidean distance
                // between face center points.  Track the overall closest pair
                // (subtract face center, wall face center) to make a single flip
                // decision for the whole body.
                var closestDistOverall    = undefined;
                var closestWallNormal     = undefined;
                var closestSubtractNormal = undefined;
                var bodyTargetParams      = fallbackParams;

                for (var subtractFace in evaluateQuery(context, qOwnedByBody(currentBody, EntityType.FACE)))
                {
                    var subtractTangent = undefined;
                    try
                    {
                        subtractTangent = evFaceTangentPlane(context, {
                                    "face"      : subtractFace,
                                    "parameter" : vector(0.5, 0.5)
                                });
                    }
                    catch
                    {
                        // Skip non-evaluable faces.
                    }

                    if (subtractTangent != undefined)
                    {
                        for (var wallFaceData in outerScopeFaceData)
                        {
                            const distToWall = norm(subtractTangent.origin - wallFaceData.origin);
                            if (closestDistOverall is undefined || distToWall < closestDistOverall)
                            {
                                closestDistOverall    = distToWall;
                                closestWallNormal     = wallFaceData.normal;
                                closestSubtractNormal = subtractTangent.normal;
                                if (wallFaceData.modelParams != undefined)
                                {
                                    bodyTargetParams = wallFaceData.modelParams;
                                }
                            }
                        }
                    }
                }

                // Each body gets its own parent sub-ID to keep flip and thicken operations
                // contiguous and avoid namespace collision with the per-location loop.
                const outerBodySubId = id + "outerSubtractBody" + unstableIdComponent(outerSubtractBodyIndex);

                // Flip the surface body's orientation before thickening when its
                // face normal points away from the matched SM wall's inward normal.
                // opFlipOrientation reverses the surface direction so opThicken
                // places material on the correct side.
                if (closestWallNormal != undefined && closestSubtractNormal != undefined &&
                    dot(closestSubtractNormal, closestWallNormal) < 0)
                {
                    opFlipOrientation(context, outerBodySubId + "flip", {
                                "bodies" : currentBody
                            });
                }

                // Thicken with the matched SM wall's front/back gauge thickness.
                const thickenId = outerBodySubId + "thicken";
                opThicken(context, thickenId, {
                            "entities"   : currentBody,
                            "thickness1" : bodyTargetParams.frontThickness,
                            "thickness2" : bodyTargetParams.backThickness
                        });
                const currentThickened = qCreatedBy(thickenId, EntityType.BODY)->qBodyType(BodyType.SOLID);

                thickenedOuterSubtractSolids = qUnion([thickenedOuterSubtractSolids, currentThickened]);
            }
        }
        else if (!isQueryEmpty(context, impliedOuterSubtractBodies))
        {
            // Use implied copies created in Phase 6.  Phase 5 already aligned their normals,
            // so no orientation correction is needed.  Thicken using the union scope wall's gauge parameters.
            const unionSMBodyForImplied    = qOwnerBody(qUnion(unionWallDefinitionEntities));
            const unionModelParamsImplied  = getModelParameters(context, unionSMBodyForImplied);

            const impliedBodyArray = evaluateQuery(context, impliedOuterSubtractBodies);
            for (var impliedOuterSubtractBodyIndex = 0; impliedOuterSubtractBodyIndex < size(impliedBodyArray); impliedOuterSubtractBodyIndex += 1)
            {
                const currentImplied = impliedBodyArray[impliedOuterSubtractBodyIndex];
                const impliedThickenId = id + "thickenImpliedOuterSubtract" + unstableIdComponent(impliedOuterSubtractBodyIndex);
                opThicken(context, impliedThickenId, {
                            "entities"   : currentImplied,
                            "thickness1" : unionModelParamsImplied.frontThickness,
                            "thickness2" : unionModelParamsImplied.backThickness
                        });
                const impliedThickened = qCreatedBy(impliedThickenId, EntityType.BODY)->qBodyType(BodyType.SOLID);
                thickenedOuterSubtractSolids = qUnion([thickenedOuterSubtractSolids, impliedThickened]);
            }
        }

        // ------------------------------------------------------------------
        // Phase 8 — Track SM model state before boolean operations.
        //
        // Body-level tracking anchors on concrete evaluated entity references.
        // Face-level tracking (persistentUnionDefinitionEntities) follows the
        // unionEntityPersistantQuery pattern from sheetMetalTab.fs to maintain
        // a live SM body reference through topology changes.
        // ------------------------------------------------------------------
        const smBodiesAffected = qUnion(evaluateQuery(context, qOwnerBody(qUnion(unionWallDefinitionEntities))));
        const initialData      = getInitialEntitiesAndAttributes(context, smBodiesAffected);
        const trackedSMBodies  = qUnion([startTracking(context, smBodiesAffected), smBodiesAffected]);
        const associateChanges = startTracking(context, qOwnedByBody(smBodiesAffected, EntityType.FACE));

        const unionDefinitionEntitiesQuery      = qUnion(unionWallDefinitionEntities);
        const persistentUnionDefinitionEntities = qUnion([unionDefinitionEntitiesQuery, startTracking(context, unionDefinitionEntitiesQuery)]);

        // ------------------------------------------------------------------
        // Phase 9 — Per-location deRip (9a), UNION (9b), and local subtract (9c).
        //
        // Each location is processed independently so a geometry failure at one
        // location does not block others.  Phase 9a uses per-location union bodies
        // for deRip collision detection.  Phase 9b re-evaluates the live SM body
        // after each deRip.  Phase 9c cuts the wall immediately after its location's UNION.
        // smBodyPostUnion is updated after each Phase 9b UNION and remains valid
        // for Phases 10-12.
        // ------------------------------------------------------------------

        // Adjacency and deRip data is the same for all locations; compute once before the loop.
        const deripCorrespondingPartEntityQueries = buildDeripDataForUnionScope(context, unionDefinitionEntitiesQuery);

        var smBodyPostUnion = qOwnerBody(persistentUnionDefinitionEntities);

        for (var placementLocationIndex = 0; placementLocationIndex < size(locationBodySets); placementLocationIndex += 1)
        {
            const locationBodies = locationBodySets[placementLocationIndex];
            const locationUnionBodies        = qHasAttributeWithValueMatching(locationBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_UNION_SURFACE });
            const locationLocalSubtractBodies = qHasAttributeWithValueMatching(locationBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_LOCAL_SUBTRACT });

            if (isQueryEmpty(context, locationUnionBodies))
            {
                continue;
            }

            // Each location gets its own parent sub-ID so all operations within this
            // iteration are contiguous in the operation history — required by opThicken.
            const locationId = id + unstableIdComponent(placementLocationIndex);

            // ------------------------------------------------------------------
            // Phase 9a — Per-location rip joint resolution before UNION.
            //
            // Thickens this location's union surface bodies for deRip collision
            // detection.  Using per-location bodies avoids spurious candidates
            // from overlapping instances at multiple locations.
            // ------------------------------------------------------------------
            var locationDeripEdgeCandidates = [];
            try
            {
                const smModelParams = getModelParameters(context, qOwnerBody(persistentUnionDefinitionEntities));

                opThicken(context, locationId + "thickenForDeRip", {
                            "entities"   : qOwnedByBody(locationUnionBodies, EntityType.FACE),
                            "thickness1" : smModelParams.frontThickness,
                            "thickness2" : smModelParams.backThickness
                        });
                const thickenedTabBody = qCreatedBy(locationId + "thickenForDeRip", EntityType.BODY);

                if (size(deripCorrespondingPartEntityQueries) > 0)
                {
                    const deripCollisions = try silent(evCollision(context, {
                                "tools"   : qOwnedByBody(thickenedTabBody, EntityType.FACE),
                                "targets" : qUnion(deripCorrespondingPartEntityQueries)
                            }));
                    if (deripCollisions != undefined)
                    {
                        for (var collision in deripCollisions)
                        {
                            if (collision["type"] != ClashType.ABUT_NO_CLASS)
                            {
                                const definitionEdges = try silent(
                                        getSMDefinitionEntities(context, collision.target, EntityType.EDGE));
                                if (definitionEdges != undefined && definitionEdges != [])
                                    locationDeripEdgeCandidates = concatenateArrays([locationDeripEdgeCandidates, definitionEdges]);
                            }
                        }
                    }
                }

                opDeleteBodies(context, locationId + "deleteThickenedDeRip", { "entities" : thickenedTabBody });
            }
            catch
            {
                // Temporary thickening or collision detection failed — proceed without deRip for this location.
            }

            if (size(locationDeripEdgeCandidates) > 0)
            {
                deripEdges(context, locationId + "deripRipJoints", qUnion(locationDeripEdgeCandidates));
            }

            // ------------------------------------------------------------------
            // Phase 9b — Union this location's tab surface bodies into the SM master.
            //
            // qOwnerBody(persistentUnionDefinitionEntities) is re-evaluated after
            // any Phase 9a deRip restructuring to target the live SM body.
            // ------------------------------------------------------------------
            const unionOpId = locationId + "unionTabToWall";

            try
            {
                opBoolean(context, unionOpId, {
                            "tools"         : qUnion([qOwnerBody(persistentUnionDefinitionEntities), locationUnionBodies]),
                            "operationType" : BooleanOperationType.UNION,
                            "allowSheets"   : true
                        });
            }
            catch
            {
                throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE, ["unionScope"]);
            }

            const unionBooleanStatus = getFeatureStatus(context, unionOpId);
            if (unionBooleanStatus.statusEnum == ErrorStringEnum.BOOLEAN_UNION_NO_OP)
            {
                throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE, ["unionScope"]);
            }

            // Update smBodyPostUnion so Phase 9c (local subtract) and Phase 12
            // (updateSheetMetalGeometry) target the live post-UNION SM body.
            smBodyPostUnion = qOwnerBody(persistentUnionDefinitionEntities);

            // ------------------------------------------------------------------
            // Phase 9c — Per-location local subtraction (wall-scoped cuts).
            // Runs immediately after this location's UNION.
            // ------------------------------------------------------------------
            if (!isQueryEmpty(context, locationLocalSubtractBodies))
            {
                opBoolean(context, locationId + "localSubtract", {
                            "tools"         : locationLocalSubtractBodies,
                            "targets"       : smBodyPostUnion,
                            "operationType" : BooleanOperationType.SUBTRACTION,
                            "allowSheets"   : true
                        });
            }
        }

        // ------------------------------------------------------------------
        // Phase 10 — Outer subtraction (broader scope cuts).
        // ------------------------------------------------------------------
        applyOuterSubtraction(context, id, thickenedOuterSubtractSolids, definition.outerSubtractionScope, definition.outerSubtractionOffset);

        // ------------------------------------------------------------------
        // Phase 11 — Clean up source bodies.
        // - unionSurfaceBodies: consumed by Phase 9b UNION booleans.
        // - localSubtractBodies: consumed by Phase 9c SUBTRACTION booleans.
        // - outerSubtractBodies: originals thickened in Phase 7; surfaces need deletion.
        // - impliedOuterSubtractBodies: copies from Phase 6; thickened in Phase 7.
        // - thickenedOuterSubtractSolids: may be partially consumed by solid path; try silent.
        // - csysConnectorBodies: placement helper, no longer needed.
        // ------------------------------------------------------------------
        const bodiesToDelete = qUnion([csysConnectorBodies, unionSurfaceBodies, localSubtractBodies, outerSubtractBodies, impliedOuterSubtractBodies]);
        if (!isQueryEmpty(context, bodiesToDelete))
        {
            opDeleteBodies(context, id + "cleanupBodies", { "entities" : bodiesToDelete });
        }
        if (!isQueryEmpty(context, thickenedOuterSubtractSolids))
        {
            try silent(opDeleteBodies(context, id + "cleanupThickenedSolids", { "entities" : thickenedOuterSubtractSolids }));
        }

        // ------------------------------------------------------------------
        // Phase 12 — Update SM model geometry.
        // Follows the assignSMAttributesToNewOrSplitEntities + updateSheetMetalGeometry
        // pattern from sheetMetalTab.fs using smBodyPostUnion as the live body reference.
        // ------------------------------------------------------------------
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, smBodyPostUnion, initialData, id);
        updateSheetMetalGeometry(context, id, {
                    "entities"           : qUnion([toUpdate.modifiedEntities, persistentUnionDefinitionEntities]),
                    "deletedAttributes"  : toUpdate.deletedAttributes,
                    "associatedChanges"  : associateChanges
                });

        // ------------------------------------------------------------------
        // Phase 13 — Resolve feature name from the tool Part Studio variable.
        // ------------------------------------------------------------------
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
            // SM_TAB_FEATURE_NAME_VAR not set; feature name template displays without suffix.
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

/**
 * Snap each surface body in the given query to be exactly coplanar with its nearest SM
 * definition face.
 *
 * The SM definition (master surface) is coincident with either the inner or the outer
 * face of the rendered SM wall — never a midplane.  If derived tab bodies are placed on
 * the opposite face from where the SM definition lives, they will be offset by the full
 * wall thickness and the opBoolean UNION/SUBTRACTION will silently no-op because the
 * surfaces never touch the master surface body.
 *
 * Algorithm per body:
 *   1. Evaluate evPlane on the body's face and find the nearest SM definition face plane
 *      by Euclidean distance between the two face origins.
 *   2. If normals are antiparallel (dot < 0), call opFlipOrientation to reverse the
 *      surface direction before translating.
 *   3. Translate along the SM wall's normal direction by the perpendicular distance
 *      between the two planes (dot(wallOrigin - bodyOrigin, wallNormal) * wallNormal)
 *      to achieve exact geometric coincidence with the SM definition face.
 *
 * @param context                {Context}
 * @param id                     {Id}
 * @param bodies                 {Query}  Surface bodies to snap; each must be planar.
 * @param smDefinitionFacePlanes {array}  Array of Plane values from SM definition faces.
 */
function snapBodiesToNearestDefinitionPlane(context is Context, id is Id, bodies is Query, smDefinitionFacePlanes is array)
{
    if (size(smDefinitionFacePlanes) == 0)
    {
        return;
    }
    const bodyArray = evaluateQuery(context, bodies);
    for (var snapBodyIndex = 0; snapBodyIndex < size(bodyArray); snapBodyIndex += 1)
    {
        const currentBody = bodyArray[snapBodyIndex];

        var bodyFacePlane = undefined;
        try
        {
            bodyFacePlane = evPlane(context, { "face" : qOwnedByBody(currentBody, EntityType.FACE) });
        }
        catch
        {
            continue;
        }

        // Find the SM definition face plane whose origin is nearest to this body's face origin.
        var nearestDefinitionPlane = smDefinitionFacePlanes[0];
        var nearestDistance = norm(smDefinitionFacePlanes[0].origin - bodyFacePlane.origin);
        for (var candidatePlane in smDefinitionFacePlanes)
        {
            const distanceToCandidate = norm(candidatePlane.origin - bodyFacePlane.origin);
            if (distanceToCandidate < nearestDistance)
            {
                nearestDistance = distanceToCandidate;
                nearestDefinitionPlane = candidatePlane;
            }
        }

        // Each body gets its own parent sub-ID so flip and snap are contiguous siblings
        // under a unique parent — required to avoid non-contiguous parent-ID errors.
        const bodySubId = id + unstableIdComponent(snapBodyIndex);

        // Correct surface normal direction before computing the snap translation so
        // opBoolean UNION sees parallel normals between the union body and the SM wall.
        if (dot(bodyFacePlane.normal, nearestDefinitionPlane.normal) < 0)
        {
            opFlipOrientation(context, bodySubId + "flip", {
                        "bodies" : currentBody
                    });
        }

        // Compute the perpendicular distance from the body's plane origin to the SM
        // definition plane along the SM wall normal, then translate to achieve coincidence.
        const snapTranslationVector = dot(nearestDefinitionPlane.origin - bodyFacePlane.origin,
                nearestDefinitionPlane.normal) * nearestDefinitionPlane.normal;
        opTransform(context, bodySubId + "snap", {
                    "bodies"    : currentBody,
                    "transform" : transform(snapTranslationVector)
                });
    }
}

// ---------------------------------------------------------------------------
// buildImpliedOuterSubtractBodies
// ---------------------------------------------------------------------------

/**
 * Creates geometry-exact copies of the union surface bodies to use as implied outer
 * subtraction tools when no explicit outer subtract bodies are tagged in the tool Part Studio.
 * Returns qNothing() when tagged outer subtract bodies exist or no outer scope is defined.
 *
 * @param context                {Context}
 * @param id                     {Id}
 * @param outerSubtractBodies    {Query}  Tagged outer subtract bodies; if non-empty, returns qNothing().
 * @param unionSurfaceBodies     {Query}  Already-snapped union surface bodies to copy from.
 * @param outerSubtractionScope  {Query}  Outer scope; if empty, returns qNothing().
 * @returns {Query}
 */
function buildImpliedOuterSubtractBodies(context is Context, id is Id, outerSubtractBodies is Query, unionSurfaceBodies is Query, outerSubtractionScope is Query) returns Query
{
    if (!isQueryEmpty(context, outerSubtractBodies) || isQueryEmpty(context, outerSubtractionScope))
    {
        return qNothing();
    }

    var impliedOuterSubtractBodies = qNothing();
    const unionBodyArrayForCopy = evaluateQuery(context, unionSurfaceBodies);
    for (var unionBodyCopyIndex = 0; unionBodyCopyIndex < size(unionBodyArrayForCopy); unionBodyCopyIndex += 1)
    {
        const copyId = id + "copyUnionForOuterSubtract" + unstableIdComponent(unionBodyCopyIndex);
        opPattern(context, copyId, {
                    "entities"                    : unionBodyArrayForCopy[unionBodyCopyIndex],
                    "transforms"                  : [identityTransform()],
                    "instanceNames"               : ["implied"],
                    "copyPropertiesAndAttributes" : false
                });
        impliedOuterSubtractBodies = qUnion([impliedOuterSubtractBodies, qCreatedBy(copyId, EntityType.BODY)]);
    }
    return impliedOuterSubtractBodies;
}

// ---------------------------------------------------------------------------
// applyOuterSubtraction
// ---------------------------------------------------------------------------

/**
 * Applies the outer subtraction pass to active SM scope faces and any non-SM solid scope.
 * Mirrors the smSubtractTab + solidSubtractTab pattern from sheetMetalTab.fs.
 * Calls getSMDefinitionEntities fresh so post-Phase-9a/9b topology mutations do not
 * produce stale entity references.
 *
 * @param context                      {Context}
 * @param id                           {Id}
 * @param thickenedOuterSubtractSolids {Query}           Thickened solid subtraction tools.
 * @param outerSubtractionScope        {Query}           Bodies or faces to subtract from.
 * @param outerSubtractionOffset       {ValueWithUnits}  Expansion offset applied before cutting.
 */
function applyOuterSubtraction(context is Context, id is Id, thickenedOuterSubtractSolids is Query, outerSubtractionScope is Query, outerSubtractionOffset is ValueWithUnits)
{
    if (isQueryEmpty(context, thickenedOuterSubtractSolids) || isQueryEmpty(context, outerSubtractionScope))
    {
        return;
    }

    if (outerSubtractionOffset > 0 * meter)
    {
        opOffsetFace(context, id + "offsetOuterSubtractTools", {
                    "moveFaces"      : qOwnedByBody(thickenedOuterSubtractSolids, EntityType.FACE),
                    "offsetDistance" : outerSubtractionOffset
                });
    }

    const separatedOuterScope = separateSheetMetalQueries(context, outerSubtractionScope);

    // SM definition face targets — fresh call avoids stale entity IDs from Phase 9a/9b mutations.
    const outerScopeSMFacesQuery = qUnion([
                qOwnedByBody(qEntityFilter(separatedOuterScope.sheetMetalQueries, EntityType.BODY), EntityType.FACE),
                qEntityFilter(separatedOuterScope.sheetMetalQueries, EntityType.FACE)
            ]);
    var freshOuterScopeDefinitionFaces = try(getSMDefinitionEntities(context, outerScopeSMFacesQuery, EntityType.FACE));
    if (freshOuterScopeDefinitionFaces is undefined)
    {
        freshOuterScopeDefinitionFaces = [];
    }

    if (size(freshOuterScopeDefinitionFaces) > 0)
    {
        var outerScopeSMFaceIndex = 0;
        for (var smFace in freshOuterScopeDefinitionFaces)
        {
            const faceSubId = id + "outerSubtractSM" + unstableIdComponent(outerScopeSMFaceIndex);
            const targetModelParameters = try silent(getModelParameters(context, qOwnerBody(smFace)));
            if (targetModelParameters != undefined)
            {
                const tool = createBooleanToolsForFace(context, faceSubId + "tool", smFace, thickenedOuterSubtractSolids, targetModelParameters);
                if (tool != undefined)
                {
                    opBoolean(context, faceSubId + "subtract", {
                                "tools"            : qCreatedBy(faceSubId + "tool", EntityType.FACE),
                                "targets"          : smFace,
                                "operationType"    : BooleanOperationType.SUBTRACTION,
                                "localizedInFaces" : true,
                                "allowSheets"      : true
                            });
                }
            }
            outerScopeSMFaceIndex += 1;
        }
    }

    // Non-SM solid targets.
    if (!isQueryEmpty(context, separatedOuterScope.nonSheetMetalQueries))
    {
        try silent(opBoolean(context, id + "outerSubtractSolid", {
                        "tools"         : thickenedOuterSubtractSolids,
                        "targets"       : separatedOuterScope.nonSheetMetalQueries,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "allowSheets"   : true
                    }));
    }
}

// ---------------------------------------------------------------------------
// buildDeripDataForUnionScope
// ---------------------------------------------------------------------------

/**
 * Collects the rendered-part entity queries needed for deRip collision detection
 * against the union scope wall's adjacent definition edges.  Called once before
 * the per-location loop because the union scope does not change between locations.
 *
 * @param context                      {Context}
 * @param unionDefinitionEntitiesQuery {Query}  SM definition entities from the union scope.
 * @returns {array}  Array of Query values (part faces or edges) for evCollision targets.
 */
function buildDeripDataForUnionScope(context is Context, unionDefinitionEntitiesQuery is Query) returns array
{
    const adjacentDefinitionEdges = evaluateQuery(context,
            qEdgeTopologyFilter(
                qAdjacent(unionDefinitionEntitiesQuery, AdjacencyType.EDGE, EntityType.EDGE),
                EdgeTopology.TWO_SIDED));
    var deripCorrespondingPartEntityQueries = [];
    for (var adjEdge in adjacentDefinitionEdges)
    {
        const jointAttributes = getSmObjectTypeAttributes(context, adjEdge, SMObjectType.JOINT);
        if (size(jointAttributes) == 0 ||
            jointAttributes[0].jointType == undefined ||
            jointAttributes[0].jointType.value != SMJointType.TANGENT)
        {
            const partFace = try silent(getSMCorrespondingInPart(context, adjEdge, EntityType.FACE));
            if (!isQueryEmpty(context, partFace))
                deripCorrespondingPartEntityQueries = append(deripCorrespondingPartEntityQueries, partFace);
        }
        else
        {
            const partEdge = try silent(getSMCorrespondingInPart(context, adjEdge, EntityType.EDGE));
            if (!isQueryEmpty(context, partEdge))
                deripCorrespondingPartEntityQueries = append(deripCorrespondingPartEntityQueries, partEdge);
        }
    }
    return deripCorrespondingPartEntityQueries;
}
