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
//      Multiple locations are supported; Phases 6.5/7/8 execute per location so a
//      geometry failure at one location does not block others.
//   3. Bodies tagged with role "smTabUnionSurface" are merged into the selected SM wall faces
//      using a surface-to-surface opBoolean UNION (allowSheets: true), mirroring the approach
//      used by the standard sheetMetalTab feature.  No thickening is performed for union.
//   4. Bodies tagged with role "smTabLocalSubtractBody" cut the merged SM wall using a
//      surface-to-surface opBoolean SUBTRACTION (allowSheets: true).  No thickening needed.
//   5. Bodies tagged with role "smTabOuterSubtractBody" are thickened using getModelParameters
//      from the target SM wall and then subtracted across the user-defined outer scope using
//      the smSubtractTab + solidSubtractTab pattern from sheetMetalTab.fs.
//      If no outer subtraction bodies are tagged but an outer subtraction scope is defined,
//      copies of the union surface bodies are used as implied outer subtraction geometry so
//      the tab footprint itself generates clearances without requiring a dedicated outer
//      subtraction tool in the tag Part Studio.
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
        // Phase 4.5 — Snap union and local subtract surface bodies onto the SM
        // definition face.
        //
        // The SM definition (master surface) is coincident with either the inner
        // or the outer face of the rendered SM wall — never a midplane.
        // getSMDefinitionEntities returns faces that live on exactly one of those
        // two physical surfaces.
        //
        // Derived bodies are positioned by the placement transform (toWorld of the
        // user's mate connector).  If the mate connector was placed on the OPPOSITE
        // face from the SM definition face, the derived bodies will be offset by
        // the full wall thickness and the UNION/SUBTRACTION booleans will silently
        // no-op because the bodies never touch the master surface.
        //
        // For each union/local-subtract body:
        //   1. Evaluate evPlane on the body face and on each SM definition face.
        //   2. Find the nearest SM definition face by Euclidean origin distance.
        //   3. If normals are antiparallel, call opFlipOrientation to align surface
        //      direction with the SM wall before the boolean.
        //   4. Translate along the SM wall normal by the perpendicular distance
        //      between the two planes so the body is exactly coplanar with the
        //      SM definition face.
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
        // Phase 4.6 — Implied outer subtraction bodies.
        //
        // When no outer subtraction surface bodies were tagged in the tool Part
        // Studio (outerSubtractBodies is empty) but an outer subtraction scope is
        // defined, copy the already-snapped union surface bodies and use the copies
        // as implied outer subtraction geometry.  This ensures clearances are cut
        // from the outer scope even when the user has not explicitly tagged a
        // separate outer subtraction tool — the tab footprint itself acts as the
        // clearance cutter.
        //
        // opPattern with an identity transform creates geometry-exact copies.
        // copyPropertiesAndAttributes : false is intentional — the copies must not
        // carry the SM_TAB_ROLE_UNION_SURFACE attribute, as that would include them
        // in unionSurfaceBodies (which is scoped to allInstantiatedBodies and would
        // not resolve them) and could cause confusion in Phase 7 UNION.
        //
        // The copies are oriented correctly because Phase 4.5 has already snapped
        // and flipped the source union surface bodies; no additional orientation
        // correction is needed before Phase 5 thickening.
        // ------------------------------------------------------------------
        var impliedOuterSubtractBodies = qNothing();
        if (isQueryEmpty(context, outerSubtractBodies) && !isQueryEmpty(context, definition.outerSubtractionScope))
        {
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
        }

        // ------------------------------------------------------------------
        // Phase 5 — Thicken outer subtract surface bodies into solids.
        //
        // Uses evFaceTangentPlane at (0.5, 0.5) rather than evPlane so orientation
        // detection works for cylindrical, conical, and other non-planar SM walls,
        // not just planar faces.
        //
        // Algorithm per outer subtract body:
        //   1. Pre-evaluate evFaceTangentPlane on every outer scope SM definition
        //      face to collect (center origin, normal, model params) tuples.
        //   2. For each face of the outer subtract body, find the closest outer
        //      scope SM face by Euclidean distance between face center points.
        //   3. If the dot product of the subtract face's normal and the closest
        //      wall face's normal is negative, call opFlipOrientation on the
        //      surface body to reverse its outward direction.
        //   4. Thicken with that SM wall's frontThickness / backThickness.
        //      Because orientation was corrected in step 3, thickness1 (positive
        //      normal direction) reliably corresponds to the SM wall's front face.
        //
        // Union and local subtract bodies remain as raw surfaces (allowSheets: true).
        // ------------------------------------------------------------------

        // Resolve outer scope SM definition faces now for Phase 5 thickening
        // orientation detection (tangent planes and model parameters).
        // Phase 9 makes its own fresh getSMDefinitionEntities call after the
        // Phase 7 union and Phase 6.5 deripping have mutated the SM body.
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

                // Each body gets its own parent sub-ID containing "outerSubtractBody" +
                // an unstable index.  This keeps flip and thicken contiguous under a single
                // parent and avoids namespace collision with the per-location loop below,
                // which also uses id + unstableIdComponent(N).
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
            // No tagged outer subtraction bodies — use the implied copies of union surface bodies.
            // These copies were made from already-snapped bodies in Phase 4.6, so no orientation
            // correction is needed: Phase 4.5 already aligned their normals with the SM wall.
            // Thicken using the union scope wall's gauge parameters.
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
        // Phase 6 — Track SM model state before boolean operations.
        //
        // Body-level tracking (smBodiesAffected/trackedSMBodies) anchors on
        // concrete evaluated entity references so startTracking survives opBoolean
        // UNION restructuring the SM definition body.
        //
        // Face-level tracking (persistentUnionDefinitionEntities) uses the
        // unionEntityPersistantQuery pattern from sheetMetalTab.fs.  Because it
        // tracks definition faces rather than the body container, it survives body-
        // level restructuring reliably and is used to derive smBodyPostUnion (the
        // live SM body reference) after every UNION in the per-location loop.
        // ------------------------------------------------------------------
        const smBodiesAffected = qUnion(evaluateQuery(context, qOwnerBody(qUnion(unionWallDefinitionEntities))));
        const initialData      = getInitialEntitiesAndAttributes(context, smBodiesAffected);
        const trackedSMBodies  = qUnion([startTracking(context, smBodiesAffected), smBodiesAffected]);
        const associateChanges = startTracking(context, qOwnedByBody(smBodiesAffected, EntityType.FACE));

        const unionDefinitionEntitiesQuery      = qUnion(unionWallDefinitionEntities);
        const persistentUnionDefinitionEntities = qUnion([unionDefinitionEntitiesQuery, startTracking(context, unionDefinitionEntitiesQuery)]);

        // ------------------------------------------------------------------
        // Phases 6.5 / 7 / 8 — Per-location deRip, UNION, and local subtract.
        //
        // Processing each placement location independently prevents a geometry
        // failure at one location from blocking all other locations.  With a
        // single batch opBoolean for all locations, a single bad instance causes
        // the entire feature to fail.
        //
        // Phase 6.5 (deRip): scoped to each location's union surface bodies so
        //   the thickened solid used for collision detection matches exactly the
        //   tab geometry at that location.  The SM definition faces adjacent to
        //   the union scope wall are computed once (they do not change per location).
        //
        // Phase 7 (UNION): each location's union surface bodies are merged into
        //   the SM master surface in a separate opBoolean UNION call.
        //   qOwnerBody(persistentUnionDefinitionEntities) re-evaluates after every
        //   deRip and UNION, so subsequent iterations always target the live SM body.
        //
        // Phase 8 (local subtract): each location's local subtract bodies cut the
        //   SM master surface immediately after that location's UNION, ensuring the
        //   cuts are applied to already-merged geometry.
        //
        // smBodyPostUnion is initialised before the loop to a valid SM body query
        // and updated after every successful per-location UNION.  If no UNIONs are
        // performed (locationBodySets is empty) the feature would have already
        // thrown above; smBodyPostUnion is guaranteed to be valid for Phases 9-11.
        // ------------------------------------------------------------------

        // Pre-compute adjacency data for deRip collision detection once — this is
        // based on the union scope wall edges, which are the same for all locations.
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
            // Phase 6.5 — Per-location pre-union rip joint resolution.
            //
            // Thicken only this location's union surface bodies for the collision
            // solid. Using location-scoped bodies avoids spurious deRip candidates
            // generated when multiple location instances overlap different rip joints
            // simultaneously, which can cause deripEdges to fail or process stale
            // duplicate edge references.
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
            // Phase 7 — Union this location's tab surface bodies into the SM master.
            //
            // qOwnerBody(persistentUnionDefinitionEntities) is re-evaluated here
            // after any deRip that may have restructured the SM definition body
            // topology, giving a guaranteed-live target.
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

            // Update smBodyPostUnion after this location's UNION so Phase 8 (local subtract)
            // and Phase 11 (updateSheetMetalGeometry) target the live post-UNION SM body.
            smBodyPostUnion = qOwnerBody(persistentUnionDefinitionEntities);

            // ------------------------------------------------------------------
            // Phase 8 — Per-location local subtraction (wall-scoped cuts).
            //
            // Runs immediately after this location's UNION so the cuts operate on
            // the already-merged SM wall geometry at this location.
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
        // Phase 9 — Outer subtraction (broader scope cuts).
        // Mirrors sheetMetalTab.fs subtractTab exactly:
        //   separateSheetMetalQueries  →  getSMDefinitionEntities (fresh, called HERE
        //   after Phase 7 union/derip, not the early outerScopeDefinitionFaces which
        //   holds stale transient entity IDs)  →  createBooleanToolsForFace per face
        //   + opBoolean localizedInFaces (smSubtractTab pattern).
        //   separateSheetMetalQueries.nonSheetMetalQueries  →  opBoolean SUBTRACTION
        //   (solidSubtractTab pattern).
        //
        // thickenedOuterSubtractSolids are NOT consumed by the SM face path
        // (createBooleanToolsForFace creates outline bodies as the actual tools)
        // so they must be deleted explicitly in Phase 10.
        // ------------------------------------------------------------------
        if (!isQueryEmpty(context, thickenedOuterSubtractSolids) && !isQueryEmpty(context, definition.outerSubtractionScope))
        {
            // Apply offset to expand/contract the thickened solids before cutting.
            if (definition.outerSubtractionOffset > 0 * meter)
            {
                opOffsetFace(context, id + "offsetOuterSubtractTools", {
                            "moveFaces" : qOwnedByBody(thickenedOuterSubtractSolids, EntityType.FACE),
                            "offsetDistance" : definition.outerSubtractionOffset
                        });
            }

            // Split the outer scope into active-SM and non-SM parts — same split
            // that sheetMetalTab.fs performs at the top of subtractTab via
            // separateSheetMetalQueries.
            const separatedOuterScope = separateSheetMetalQueries(context, definition.outerSubtractionScope);

            // SM definition face targets.
            // Call getSMDefinitionEntities fresh after Phase 7 union and Phase 6.5
            // deripping have mutated the SM body topology — pre-mutation entity IDs
            // are stale and will return empty or incorrect results.
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

            // Non-SM solid targets — mirrors solidSubtractTab in sheetMetalTab.fs.
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

        // ------------------------------------------------------------------
        // Phase 10 — Clean up original surface bodies and the csys connector.
        // - unionSurfaceBodies: consumed by the per-location UNION booleans.
        // - localSubtractBodies: consumed by the per-location SUBTRACTION booleans.
        // - outerSubtractBodies: originals were thickened into solids; the
        //   original surfaces still exist and need explicit deletion.
        // - impliedOuterSubtractBodies: copies of union surfaces created in Phase 4.6
        //   when no outer subtract bodies were tagged; thickened into solids in Phase 5.
        //   The surface copies still exist and need explicit deletion.
        // - thickenedOuterSubtractSolids: NOT consumed by the SM face path
        //   (createBooleanToolsForFace builds outline tools, not consuming the
        //   thickened body).  They may be consumed by the solid path when solid
        //   outer scope targets exist; try silent handles both cases.
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
        // Phase 11 — Update the sheet metal model to recognise the new geometry.
        //
        // Uses smBodyPostUnion (derived from persistentUnionDefinitionEntities face
        // tracking) as the live SM body reference, following the
        // assignSMAttributesToNewOrSplitEntities + updateSheetMetalGeometry pattern
        // from sheetMetalTab.fs.
        // ------------------------------------------------------------------
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, smBodyPostUnion, initialData, id);
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
