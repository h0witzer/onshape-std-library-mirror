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

        for (var location in evaluateQuery(context, definition.locations))
        {
            var placementCSys = resolveLocationCSys(context, location);
            placementCSys = applyOrientationOverrides(placementCSys, definition.flipDirection, definition.secondaryAxisType);

            const instanceBodies = addInstance(instantiator, definition.formPartStudio, {
                        "transform" : toWorld(placementCSys),
                        "identity"  : location
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

        // Diagnostic: report role-tagged body counts so the console shows what
        // was found after instantiation.  Zero counts here means the instantiator
        // did not carry the attribute over from the tool Part Studio.
        println("SM Tab Apply — Phase 3 role query results:");
        println("  union surface bodies:  " ~ toString(size(evaluateQuery(context, unionSurfaceBodies))));
        println("  local subtract bodies: " ~ toString(size(evaluateQuery(context, localSubtractBodies))));
        println("  outer subtract bodies: " ~ toString(size(evaluateQuery(context, outerSubtractBodies))));
        println("  csys connector bodies: " ~ toString(size(evaluateQuery(context, csysConnectorBodies))));

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

        println("SM Tab Apply — Phase 4 SM definition entity count: " ~ toString(size(unionWallDefinitionEntities)));

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
        //   3. If normals antiparallel, call opFlipOrientation (same heuristic as
        //      onlyTabs.fs booleanOneTabGroup — works for any surface geometry).
        //   4. Translate along the SM wall normal by the perpendicular distance
        //      between the two planes so the body is exactly coplanar with the
        //      SM definition face before any boolean operation.
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
        println("SM Tab Apply — Phase 4.5 SM definition face planes found: " ~ toString(size(smDefinitionFacePlanes)));

        if (size(smDefinitionFacePlanes) > 0)
        {
            snapBodiesToNearestDefinitionPlane(context, id + "snapUnionBodies", unionSurfaceBodies, smDefinitionFacePlanes);
            if (!isQueryEmpty(context, localSubtractBodies))
            {
                snapBodiesToNearestDefinitionPlane(context, id + "snapLocalSubtractBodies", localSubtractBodies, smDefinitionFacePlanes);
            }
        }
        else
        {
            println("SM Tab Apply — Phase 4.5 WARNING: no planar SM definition faces found; bodies NOT snapped to SM definition face.");
        }

        // ------------------------------------------------------------------
        // Phase 5 — Thicken outer subtract surface bodies into solids.
        //
        // Orientation is corrected with opFlipOrientation before thickening,
        // using the same face-tangent-plane heuristic as booleanOneTabGroup in
        // onlyTabs.fs.  evFaceTangentPlane at (0.5, 0.5) is used instead of
        // evPlane so the heuristic works for cylindrical, conical, and other
        // non-planar SM wall geometries — not just planar faces.
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

        // Resolve outer scope SM definition faces once here so Phase 9 can
        // reuse the result without a second getSMDefinitionEntities call.
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
            for (var bodyIndex = 0; bodyIndex < size(outerSubtractBodyArray); bodyIndex += 1)
            {
                const currentBody = outerSubtractBodyArray[bodyIndex];

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

                // Flip the surface body's orientation before thickening when its
                // face normal points away from the matched SM wall's inward normal.
                // opFlipOrientation reverses the surface direction so opThicken
                // places material on the correct side — the non-planar-safe
                // alternative to mirrorAcross used in the standard sheetMetalTab.
                if (closestWallNormal != undefined && closestSubtractNormal != undefined &&
                    dot(closestSubtractNormal, closestWallNormal) < 0)
                {
                    opFlipOrientation(context, id + "flipOuterSubtract" + unstableIdComponent(bodyIndex), {
                                "bodies" : currentBody
                            });
                }

                // Thicken with the matched SM wall's front/back gauge thickness.
                const thickenId = id + "thickenOuterSubtract" + unstableIdComponent(bodyIndex);
                opThicken(context, thickenId, {
                            "entities"   : currentBody,
                            "thickness1" : bodyTargetParams.frontThickness,
                            "thickness2" : bodyTargetParams.backThickness
                        });
                const currentThickened = qCreatedBy(thickenId, EntityType.BODY)->qBodyType(BodyType.SOLID);

                thickenedOuterSubtractSolids = qUnion([thickenedOuterSubtractSolids, currentThickened]);
            }
        }

        // ------------------------------------------------------------------
        // Phase 6 — Track SM model state before boolean operations.
        //
        // Two complementary tracking mechanisms:
        //
        // (a) smBodiesAffected / trackedSMBodies — body-level tracking.
        //     The SM definition body is evaluated to a CONCRETE entity reference
        //     before startTracking is called.  This mirrors the standard library
        //     pattern in sheetMetalTab.fs and onlyTabs.fs exactly:
        //       unionBodies = evaluateQuery(context, qOwnerBody(unionEntityQuery))
        //       sheetMetalBodiesQuery = qUnion([startTracking(...), qUnion(unionBodies)])
        //     Using a lazy derived query (qOwnerBody wrapped in qUnion) instead of
        //     evaluated concrete entities causes startTracking to produce a
        //     zero-result query after opBoolean UNION restructures the SM definition
        //     body's face topology, because the body entity is recreated internally
        //     even though qCreatedBy returns 0.
        //
        // (b) persistentUnionDefinitionEntities — face-level tracking.
        //     Tracks the SM definition FACES (not the body) through all boolean
        //     operations.  This is the unionEntityPersistantQuery pattern from
        //     sheetMetalTab.fs and is used by updateSheetMetalGeometry (Phase 11).
        //     Because it tracks faces rather than the body container, it survives
        //     body-level restructuring reliably.
        //
        // After the UNION (Phase 7), smBodyPostUnion is derived from (b) via
        // qOwnerBody(persistentUnionDefinitionEntities).  This is used as the
        // Phase 8 subtraction target and the Phase 11 attribute-assignment scope,
        // giving a guaranteed-live body reference even if body-level tracking (a)
        // returned 0.
        // ------------------------------------------------------------------
        const smBodiesAffected = qUnion(evaluateQuery(context, qOwnerBody(qUnion(unionWallDefinitionEntities))));
        const initialData      = getInitialEntitiesAndAttributes(context, smBodiesAffected);
        const trackedSMBodies  = qUnion([startTracking(context, smBodiesAffected), smBodiesAffected]);
        const associateChanges = startTracking(context, qOwnedByBody(smBodiesAffected, EntityType.FACE));

        const unionDefinitionEntitiesQuery      = qUnion(unionWallDefinitionEntities);
        const persistentUnionDefinitionEntities = qUnion([unionDefinitionEntitiesQuery, startTracking(context, unionDefinitionEntitiesQuery)]);

        // ------------------------------------------------------------------
        // Phase 4.6 — ABUT nudge: correct tab surfaces that only touch the
        // SM definition face boundary with no interior area overlap.
        //
        // Root cause: when the tab surface's attachment edge is exactly
        // coincident with the SM definition face's outer boundary edge AND
        // that edge is also shared by an adjacent SM face (e.g. the front
        // wall of a box-channel), the UNION kernel classifies the contact as
        // ABUT_NO_CLASS — a T-junction it cannot resolve — and throws
        // BOOLEAN_INVALID.
        //
        // Detection: evCollision between unionSurfaceBodies and smBodiesAffected
        // before the UNION.  When every result is ABUT_NO_CLASS or NONE the tab
        // has zero interior area overlap with the SM definition face.
        //
        // Fix: translate the union (and local subtract) surface bodies by 1 μm
        // toward the SM definition face centroid, projected strictly in-plane
        // (the normal component is stripped out so the snap-to-plane result is
        // preserved).  The 1 μm overlap strip is geometrically imperceptible
        // but sufficient to give the UNION kernel a classifiable intersection
        // region and eliminate the T-junction ambiguity.
        // ------------------------------------------------------------------
        const abutCheckCollisions = try silent(evCollision(context, {
                    "tools"   : unionSurfaceBodies,
                    "targets" : smBodiesAffected
                }));
        if (abutCheckCollisions is array && size(abutCheckCollisions) > 0)
        {
            var abutOnlyContacts = true;
            for (var abutCollision in abutCheckCollisions)
            {
                if (abutCollision['type'] != ClashType.ABUT_NO_CLASS &&
                    abutCollision['type'] != ClashType.NONE)
                {
                    abutOnlyContacts = false;
                    break;
                }
            }

            if (abutOnlyContacts)
            {
                // All contacts are boundary-only (ABUT_NO_CLASS / NONE).
                // Compute the in-plane direction from the tab face centroid to the
                // SM definition face centroid and nudge by 1 μm in that direction.
                var tabBodyFaceOrigin = undefined;
                const abutCheckUnionBodyArray = evaluateQuery(context, unionSurfaceBodies);
                if (size(abutCheckUnionBodyArray) > 0)
                {
                    const abutCheckTabFaceArray = evaluateQuery(context, qOwnedByBody(abutCheckUnionBodyArray[0], EntityType.FACE));
                    if (size(abutCheckTabFaceArray) > 0)
                    {
                        try
                        {
                            const abutCheckTangent = evFaceTangentPlane(context, {
                                        "face"      : abutCheckTabFaceArray[0],
                                        "parameter" : vector(0.5, 0.5)
                                    });
                            tabBodyFaceOrigin = abutCheckTangent.origin;
                        }
                        catch { }
                    }
                }

                if (tabBodyFaceOrigin != undefined && size(smDefinitionFacePlanes) > 0)
                {
                    const abutSMDefinitionPlane = smDefinitionFacePlanes[0];
                    const towardSMFaceCentroid  = abutSMDefinitionPlane.origin - tabBodyFaceOrigin;
                    // Strip the normal component so the nudge stays in-plane and
                    // does not undo the snap-to-SM-definition-face translation.
                    const inPlaneDirection = towardSMFaceCentroid -
                            dot(towardSMFaceCentroid, abutSMDefinitionPlane.normal) * abutSMDefinitionPlane.normal;
                    const inPlaneMagnitude = norm(inPlaneDirection);

                    if (inPlaneMagnitude > 1e-9 * meter)
                    {
                        const nudgeAmount = 1e-6 * meter;  // 1 μm — imperceptible to user
                        const nudgeVector = (nudgeAmount / inPlaneMagnitude) * inPlaneDirection;
                        println("SM Tab Apply — Phase 4.6: all contacts are ABUT_NO_CLASS; applying " ~
                                toString(nudgeAmount) ~ " in-plane nudge toward SM face interior.");
                        opTransform(context, id + "nudgeUnionBodies", {
                                    "bodies"    : unionSurfaceBodies,
                                    "transform" : transform(nudgeVector)
                                });
                        if (!isQueryEmpty(context, localSubtractBodies))
                        {
                            opTransform(context, id + "nudgeLocalSubtractBodies", {
                                        "bodies"    : localSubtractBodies,
                                        "transform" : transform(nudgeVector)
                                    });
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Phase 7 — Union the tab surface bodies into the SM master surface.
        //
        // Both smBodiesAffected and unionSurfaceBodies are passed as "tools"
        // with no "targets", mirroring booleanOneTabGroup in onlyTabs.fs.
        //
        // recomputeMatches: true is required — without it the boolean kernel
        // uses stale match data from the SM master body and throws
        // BOOLEAN_INVALID when the tab surface overlaps (rather than being
        // pre-trimmed to edge-adjacency) with the SM wall.  onlyTabs.fs
        // booleanOneTabGroup uses this flag for exactly the same reason.
        //
        // try (not silent) lets opBoolean log its native error to the console
        // while we still catch and add geometry diagnostics before re-throwing
        // the SHEET_METAL_TAB_FAILS_MERGE error.
        // After the try-catch, getFeatureStatus detects the BOOLEAN_UNION_NO_OP
        // case (tab body entirely within SM wall boundary) so the feature never
        // silently claims success without producing a geometry change.
        // ------------------------------------------------------------------

        // Pre-union diagnostics: report body counts so the log always confirms
        // both sides have bodies before the attempt is made.
        const unionBodiesForDiag = evaluateQuery(context, unionSurfaceBodies);
        const smBodiesForDiag    = evaluateQuery(context, smBodiesAffected);
        println("SM Tab Apply — Phase 7: attempting UNION of " ~
                toString(size(unionBodiesForDiag)) ~
                " union bodies with SM master surface body count " ~
                toString(size(smBodiesForDiag)));

        try
        {
            opBoolean(context, id + "unionTabToWall", {
                        "tools"            : qUnion([smBodiesAffected, unionSurfaceBodies]),
                        "operationType"    : BooleanOperationType.UNION,
                        "allowSheets"      : true,
                        "recomputeMatches" : true
                    });
        }
        catch
        {
            // ------------------------------------------------------------------
            // UNION failed — run geometry diagnostics before re-throwing so the
            // console shows exactly what went wrong.
            //
            // Checks performed:
            //   A. Per-face tangent planes (normal + origin) for every union body
            //      face — identifies orientation issues.
            //   B. Per-face tangent planes for every SM master body face —
            //      confirms which definition faces are present and their normals.
            //   C. Signed coplanarity distance — for each union face, the
            //      perpendicular distance from the face center to each SM
            //      definition plane.  Should be ~0 if Phase 4.5 snapping worked.
            //   D. evCollision contact check — reports whether the union body
            //      and SM master body have any geometric contact at all (NONE
            //      means they are completely separate, TOUCHING means edge-
            //      adjacent, INTERFERING means overlapping volume which should
            //      not occur for surfaces).
            // ------------------------------------------------------------------

            // A — union body face tangent planes
            for (var unionBodyIndex = 0; unionBodyIndex < size(unionBodiesForDiag); unionBodyIndex += 1)
            {
                const unionFaceArray = evaluateQuery(context, qOwnedByBody(unionBodiesForDiag[unionBodyIndex], EntityType.FACE));
                println("SM Tab Apply — Phase 7 diag A: union body " ~ toString(unionBodyIndex) ~
                        " has " ~ toString(size(unionFaceArray)) ~ " face(s)");
                for (var faceIndex = 0; faceIndex < size(unionFaceArray); faceIndex += 1)
                {
                    var faceTangentPlane = undefined;
                    try
                    {
                        faceTangentPlane = evFaceTangentPlane(context, {
                                    "face"      : unionFaceArray[faceIndex],
                                    "parameter" : vector(0.5, 0.5)
                                });
                    }
                    catch
                    {
                        println("SM Tab Apply — Phase 7 diag A: union body " ~ toString(unionBodyIndex) ~
                                " face " ~ toString(faceIndex) ~ " evFaceTangentPlane failed (non-planar or degenerate)");
                    }
                    if (faceTangentPlane != undefined)
                    {
                        println("SM Tab Apply — Phase 7 diag A: union body " ~ toString(unionBodyIndex) ~
                                " face " ~ toString(faceIndex) ~
                                " normal = " ~ toString(faceTangentPlane.normal) ~
                                " origin = " ~ toString(faceTangentPlane.origin));
                    }
                }
            }

            // B — SM master body face tangent planes
            for (var smBodyIndex = 0; smBodyIndex < size(smBodiesForDiag); smBodyIndex += 1)
            {
                const smFaceArray = evaluateQuery(context, qOwnedByBody(smBodiesForDiag[smBodyIndex], EntityType.FACE));
                println("SM Tab Apply — Phase 7 diag B: SM master body " ~ toString(smBodyIndex) ~
                        " has " ~ toString(size(smFaceArray)) ~ " face(s)");
                for (var faceIndex = 0; faceIndex < size(smFaceArray); faceIndex += 1)
                {
                    var faceTangentPlane = undefined;
                    try
                    {
                        faceTangentPlane = evFaceTangentPlane(context, {
                                    "face"      : smFaceArray[faceIndex],
                                    "parameter" : vector(0.5, 0.5)
                                });
                    }
                    catch
                    {
                        println("SM Tab Apply — Phase 7 diag B: SM master body " ~ toString(smBodyIndex) ~
                                " face " ~ toString(faceIndex) ~ " evFaceTangentPlane failed");
                    }
                    if (faceTangentPlane != undefined)
                    {
                        println("SM Tab Apply — Phase 7 diag B: SM master body " ~ toString(smBodyIndex) ~
                                " face " ~ toString(faceIndex) ~
                                " normal = " ~ toString(faceTangentPlane.normal) ~
                                " origin = " ~ toString(faceTangentPlane.origin));
                    }
                }
            }

            // C — coplanarity: signed distance from each union face center to each SM definition plane
            for (var unionBodyIndex = 0; unionBodyIndex < size(unionBodiesForDiag); unionBodyIndex += 1)
            {
                const unionFaceArray = evaluateQuery(context, qOwnedByBody(unionBodiesForDiag[unionBodyIndex], EntityType.FACE));
                for (var unionFaceIndex = 0; unionFaceIndex < size(unionFaceArray); unionFaceIndex += 1)
                {
                    var unionCenter = undefined;
                    try
                    {
                        unionCenter = evFaceTangentPlane(context, {
                                    "face"      : unionFaceArray[unionFaceIndex],
                                    "parameter" : vector(0.5, 0.5)
                                });
                    }
                    catch { }
                    if (unionCenter != undefined)
                    {
                        for (var planeIndex = 0; planeIndex < size(smDefinitionFacePlanes); planeIndex += 1)
                        {
                            const smPlane = smDefinitionFacePlanes[planeIndex];
                            const offsetVector = unionCenter.origin - smPlane.origin;
                            const signedDistance = dot(offsetVector, smPlane.normal);
                            println("SM Tab Apply — Phase 7 diag C: union body " ~ toString(unionBodyIndex) ~
                                    " face " ~ toString(unionFaceIndex) ~
                                    " signed distance to SM definition plane " ~ toString(planeIndex) ~
                                    " = " ~ toString(signedDistance) ~ " m" ~
                                    " (normal dot = " ~ toString(dot(unionCenter.normal, smPlane.normal)) ~ ")");
                        }
                    }
                }
            }

            // D — evCollision contact check between union bodies and SM master faces
            try
            {
                const contactResults = evCollision(context, {
                            "tools"   : unionSurfaceBodies,
                            "targets" : smBodiesAffected
                        });
                println("SM Tab Apply — Phase 7 diag D: evCollision returned " ~
                        toString(size(contactResults)) ~ " result(s)");
                for (var contactIndex = 0; contactIndex < size(contactResults); contactIndex += 1)
                {
                    println("SM Tab Apply — Phase 7 diag D: contact " ~ toString(contactIndex) ~
                            " type = " ~ toString(contactResults[contactIndex]['type']));
                }
            }
            catch
            {
                println("SM Tab Apply — Phase 7 diag D: evCollision failed (bodies may have no geometric relationship)");
            }

            // E — body type (SHEET vs SOLID) for every union body and every SM master body.
            // BOOLEAN_INVALID is expected when smBodiesAffected resolves to a 3D solid rather
            // than the SM definition surface body; a SOLID here confirms that root cause.
            for (var unionBodyIndex = 0; unionBodyIndex < size(unionBodiesForDiag); unionBodyIndex += 1)
            {
                const unionBodyIsSheet = !isQueryEmpty(context, qBodyType(unionBodiesForDiag[unionBodyIndex], BodyType.SHEET));
                const unionEdgeCount   = size(evaluateQuery(context, qOwnedByBody(unionBodiesForDiag[unionBodyIndex], EntityType.EDGE)));
                println("SM Tab Apply — Phase 7 diag E: union body " ~ toString(unionBodyIndex) ~
                        " BodyType = " ~ (unionBodyIsSheet ? "SHEET" : "SOLID_OR_WIRE") ~
                        "  edge count = " ~ toString(unionEdgeCount));
            }
            for (var smBodyIndex = 0; smBodyIndex < size(smBodiesForDiag); smBodyIndex += 1)
            {
                const smBodyIsSheet = !isQueryEmpty(context, qBodyType(smBodiesForDiag[smBodyIndex], BodyType.SHEET));
                println("SM Tab Apply — Phase 7 diag E: SM master body " ~ toString(smBodyIndex) ~
                        " BodyType = " ~ (smBodyIsSheet ? "SHEET" : "SOLID_OR_WIRE"));
            }

            throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE, ["unionScope"]);
        }
        const unionBooleanStatus = getFeatureStatus(context, id + "unionTabToWall");
        if (unionBooleanStatus.statusEnum == ErrorStringEnum.BOOLEAN_UNION_NO_OP)
        {
            // The UNION completed without error but produced no geometry change.
            // The tab body is entirely within the SM wall boundary (no shared
            // boundary edge exists).  Verify that the tab overlaps or is
            // edge-adjacent to the SM wall in the definition surface plane.
            println("SM Tab Apply — Phase 7: UNION was a no-op — tab body has no shared boundary with the SM definition face.");
            throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE, ["unionScope"]);
        }
        println("SM Tab Apply — Phase 7: UNION completed successfully.");

        // Post-UNION: derive the SM definition body from the persistent face-tracking
        // query.  persistentUnionDefinitionEntities tracks the SM definition FACES
        // (not the body) through all operations, so qOwnerBody of the tracked faces
        // gives a live, guaranteed-correct SM body reference even when body-level
        // tracking (trackedSMBodies) returns 0 after the UNION restructures the
        // SM definition body's entity topology.
        const smBodyPostUnion = qOwnerBody(persistentUnionDefinitionEntities);

        // Post-UNION entity-count diagnostics.
        // trackedSMBodies = 0 means body-level startTracking lost the entity after UNION.
        // smBodyPostUnion must be non-zero for Phase 8 and Phase 11 to succeed.
        println("SM Tab Apply — Phase 7 post-UNION: trackedSMBodies count = " ~
                toString(size(evaluateQuery(context, trackedSMBodies))));
        println("SM Tab Apply — Phase 7 post-UNION: smBodyPostUnion count = " ~
                toString(size(evaluateQuery(context, smBodyPostUnion))));
        println("SM Tab Apply — Phase 7 post-UNION: localSubtractBodies count = " ~
                toString(size(evaluateQuery(context, localSubtractBodies))));
        println("SM Tab Apply — Phase 7 post-UNION: qCreatedBy(unionTabToWall) body count = " ~
                toString(size(evaluateQuery(context, qCreatedBy(id + "unionTabToWall", EntityType.BODY)))));

        // ------------------------------------------------------------------
        // Phase 8 — Local subtraction (wall-scoped cuts).
        // Surface-to-surface boolean with allowSheets: true.  The surface tool
        // bodies cut directly against the SM master surface.
        //
        // smBodyPostUnion is used as the target — it is derived from the
        // persistentUnionDefinitionEntities face tracking and is guaranteed to
        // resolve to the live SM definition body after the Phase 7 UNION.
        // ------------------------------------------------------------------
        if (!isQueryEmpty(context, localSubtractBodies))
        {
            println("SM Tab Apply — Phase 8: attempting local SUBTRACTION with " ~
                    toString(size(evaluateQuery(context, localSubtractBodies))) ~ " tool bodies.");
            opBoolean(context, id + "localSubtract", {
                        "tools"         : localSubtractBodies,
                        "targets"       : smBodyPostUnion,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "allowSheets"   : true
                    });
            println("SM Tab Apply — Phase 8: local SUBTRACTION completed.");
        }
        else
        {
            println("SM Tab Apply — Phase 8: no local subtract bodies; skipping.");
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

            // Build outer scope target query from the SM definition faces already resolved
            // in Phase 5 (outerScopeDefinitionFaces) and any plain solid bodies.
            var outerScopeTargets = qNothing();
            if (outerScopeDefinitionFaces != undefined && outerScopeDefinitionFaces != [])
            {
                outerScopeTargets = qUnion([outerScopeTargets, qOwnerBody(qUnion(outerScopeDefinitionFaces))]);
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
                            "operationType" : BooleanOperationType.SUBTRACTION
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
        //
        // assignSMAttributesToNewOrSplitEntities uses smBodyPostUnion (derived from
        // persistentUnionDefinitionEntities face tracking) rather than trackedSMBodies
        // (body-level tracking).  Body-level startTracking resolves to 0 after the
        // Phase 7 opBoolean UNION restructures the SM definition body's face topology;
        // smBodyPostUnion is guaranteed to resolve correctly because it is derived via
        // qOwnerBody from the working face-level tracking query.
        //
        // This mirrors the sheetMetalTab.fs pattern:
        //   assignSMAttributesToNewOrSplitEntities(context, sheetMetalBodiesQuery, ...)
        //   updateSheetMetalGeometry(..., entities: qUnion([toUpdate, unionEntityPersistantQuery]))
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
 *      surface direction before translating.  This is the same heuristic used in
 *      booleanOneTabGroup in onlyTabs.fs and avoids the mirrorAcross planar assumption
 *      used by the standard sheetMetalTab applyPlaneToPlaneTransform.
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
    for (var bodyIndex = 0; bodyIndex < size(bodyArray); bodyIndex += 1)
    {
        const currentBody = bodyArray[bodyIndex];

        var bodyFacePlane = undefined;
        try
        {
            bodyFacePlane = evPlane(context, { "face" : qOwnedByBody(currentBody, EntityType.FACE) });
        }
        catch
        {
            println("SM Tab Apply — snapBodiesToNearestDefinitionPlane: body index " ~
                    toString(bodyIndex) ~ " is non-planar; skipping snap.");
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

        // Correct surface normal direction before computing the snap translation so
        // opBoolean UNION sees parallel normals between the union body and the SM wall.
        if (dot(bodyFacePlane.normal, nearestDefinitionPlane.normal) < 0)
        {
            opFlipOrientation(context, id + "flip" + unstableIdComponent(bodyIndex), {
                        "bodies" : currentBody
                    });
        }

        // Compute the perpendicular distance from the body's plane origin to the SM
        // definition plane along the SM wall normal, then translate to achieve coincidence.
        const snapTranslationVector = dot(nearestDefinitionPlane.origin - bodyFacePlane.origin,
                nearestDefinitionPlane.normal) * nearestDefinitionPlane.normal;
        println("SM Tab Apply — snapBodiesToNearestDefinitionPlane: body " ~
                toString(bodyIndex) ~ " snap translation = " ~ toString(snapTranslationVector));
        opTransform(context, id + "snap" + unstableIdComponent(bodyIndex), {
                    "bodies"    : currentBody,
                    "transform" : transform(snapTranslationVector)
                });
    }
}
