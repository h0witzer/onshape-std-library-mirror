FeatureScript 2909;
// SM Tab Apply — places a tagged tab tool Part Studio onto a sheet metal model.
// Thickness is resolved from the target SM model at apply-time; the tool Part Studio never encodes gauge.
// The tag half of this workflow lives in smTabTag.fs.

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

// Attribute name constants — must match smTabTag.fs
const SM_TAB_BODY_ATTRIBUTE_NAME   = "smTabBodyAttribute";
const SM_TAB_ROLE_UNION_SURFACE    = "smTabUnionSurface";
const SM_TAB_ROLE_LOCAL_SUBTRACT   = "smTabLocalSubtractBody";
const SM_TAB_ROLE_OUTER_SUBTRACT   = "smTabOuterSubtractBody";
const SM_TAB_FEATURE_NAME_VAR      = "smTabFeatureName";

const FEATURE_NAME_SEPARATOR = " - ";

// Distance used by retryUnionWithMicroShift to nudge a tab surface body into the SM wall face.
// 1 µm is far below any sheet metal manufacturing tolerance (~0.5 mm) but well above the
// boolean kernel's numerical precision floor, giving the UNION enough overlapping area to succeed.
const EDGE_ADJACENT_MICRO_SHIFT_DISTANCE = 1e-6 * meter;

// ---------------------------------------------------------------------------
// Feature definition
// ---------------------------------------------------------------------------

/** Places a tab tool Part Studio (prepared with SM Tab Tag) at one or more locations on a sheet metal model. */
annotation { "Feature Type Name" : "SM Tab Apply", "Feature Name Template" : "SM Tab Apply#featureName" }
export const smTabApply = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
                    "Name" : "Tab tool Part Studio",
                    "Description" : "A Part Studio prepared with the SM Tab Tag feature.",
                    "Filter" : PartStudioItemType.ENTIRE_PART_STUDIO,
                    "MaxNumberOfPicks" : 1,
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
                }
        definition.formPartStudio is PartStudioData;

        annotation {
                    "Name" : "Location(s)",
                    "Filter" : BodyType.MATE_CONNECTOR || (EntityType.VERTEX && SketchObject.YES && ModifiableEntityOnly.YES)
                }
        definition.locations is Query;

        annotation { "Name" : "Flip direction", "UIHint" : [UIHint.OPPOSITE_DIRECTION, UIHint.FIRST_IN_ROW] }
        definition.flipDirection is boolean;

        annotation { "Name" : "Reorient secondary axis", "UIHint" : UIHint.MATE_CONNECTOR_AXIS_TYPE }
        definition.secondaryAxisType is MateConnectorAxisType;

        annotation {
                    "Name" : "Union scope",
                    "Description" : "Sheet metal wall definition faces to merge the tab surface into.",
                    "Filter" : SheetMetalDefinitionEntityType.FACE && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES
                }
        definition.unionScope is Query;

        annotation { "Name" : "Outer subtraction offset" }
        isLength(definition.outerSubtractionOffset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation {
                    "Name" : "Outer subtraction scope",
                    "Filter" : (SheetMetalDefinitionEntityType.FACE && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES) ||
                               (EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES && ActiveSheetMetal.NO)
                }
        definition.outerSubtractionScope is Query;

        annotation { "Name" : "Feature name (computed)", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.featureName is string;
    }
    {
        if (isQueryEmpty(context, definition.locations))
            throw regenError(ErrorStringEnum.FORMED_SELECT_LOCATION, ["locations"]);

        if (isQueryEmpty(context, definition.unionScope))
            throw regenError("Select at least one sheet metal wall face in Union scope.", ["unionScope"]);

        // Instantiate the tool Part Studio at each placement location.
        const instantiated = instantiateToolBodies(context, id, definition);

        // Classify instantiated bodies by role tag.
        const unionSurfaceBodies  = qHasAttributeWithValueMatching(instantiated.allBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_UNION_SURFACE });
        const localSubtractBodies = qHasAttributeWithValueMatching(instantiated.allBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_LOCAL_SUBTRACT });
        const outerSubtractBodies = qHasAttributeWithValueMatching(instantiated.allBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_OUTER_SUBTRACT });

        if (isQueryEmpty(context, unionSurfaceBodies))
            throw regenError("The tool Part Studio contains no bodies tagged as union surfaces. Run SM Tab Tag in the tool Part Studio.", ["formPartStudio"]);

        // Resolve SM definition entities from the union scope wall.
        const unionWallDefinitionEntities = getSMDefinitionEntities(context, definition.unionScope);
        if (unionWallDefinitionEntities == undefined || unionWallDefinitionEntities == [])
            throw regenError("Could not resolve sheet metal definition entities from union scope.", ["unionScope"]);

        // Snap tab surfaces onto the SM definition face plane.
        const smDefinitionFacePlanes = collectDefinitionFacePlanes(context, unionWallDefinitionEntities);
        if (size(smDefinitionFacePlanes) > 0)
        {
            snapBodiesToNearestDefinitionPlane(context, id + "snapUnionBodies", unionSurfaceBodies, smDefinitionFacePlanes);
            if (!isQueryEmpty(context, localSubtractBodies))
                snapBodiesToNearestDefinitionPlane(context, id + "snapLocalSubtractBodies", localSubtractBodies, smDefinitionFacePlanes);
        }

        // Build implied outer subtract copies when no explicit outer subtract bodies are tagged.
        const impliedOuterSubtractBodies = buildImpliedOuterSubtractBodies(context, id, outerSubtractBodies, unionSurfaceBodies, definition.outerSubtractionScope);

        // Thicken outer subtract surfaces into solids for the subtraction pass.
        const unionSMBody = qOwnerBody(qUnion(unionWallDefinitionEntities));
        const thickenedOuterSubtractSolids = thickenOuterSubtractSurfaces(context, id, outerSubtractBodies, impliedOuterSubtractBodies, definition.outerSubtractionScope, unionSMBody);

        // Capture SM model state before boolean operations.
        const smBodiesAffected = qUnion(evaluateQuery(context, unionSMBody));
        const initialData      = getInitialEntitiesAndAttributes(context, smBodiesAffected);
        const trackedSMBodies  = qUnion([startTracking(context, smBodiesAffected), smBodiesAffected]);
        const associateChanges = startTracking(context, qOwnedByBody(smBodiesAffected, EntityType.FACE));

        const unionDefinitionEntitiesQuery      = qUnion(unionWallDefinitionEntities);
        const persistentUnionDefinitionEntities = qUnion([unionDefinitionEntitiesQuery, startTracking(context, unionDefinitionEntitiesQuery)]);

        // Per-location deRip, union, and local subtract.
        const deripPartEntityQueries = buildDeripDataForUnionScope(context, unionDefinitionEntitiesQuery);
        var smBodyPostUnion = qOwnerBody(persistentUnionDefinitionEntities);
        for (var locationIndex = 0; locationIndex < size(instantiated.locationBodySets); locationIndex += 1)
        {
            const locationBodies = instantiated.locationBodySets[locationIndex];
            if (isQueryEmpty(context, qHasAttributeWithValueMatching(locationBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_UNION_SURFACE })))
                continue;
            smBodyPostUnion = processTabAtLocation(context, id + unstableIdComponent(locationIndex),
                    locationBodies, persistentUnionDefinitionEntities, deripPartEntityQueries);
        }

        // Outer subtraction pass across the user-defined scope.
        applyOuterSubtraction(context, id, thickenedOuterSubtractSolids, definition.outerSubtractionScope, definition.outerSubtractionOffset);

        // Delete all instantiated bodies that were not consumed by boolean operations.
        // qCreatedBy is a persistent query that still resolves consumed bodies; opDeleteBodies
        // fails if any consumed entity is in the list, so filter them out first.
        const survivingInstantiatedBodies = qConsumed(instantiated.allBodies, Consumed.NO);
        if (!isQueryEmpty(context, survivingInstantiatedBodies))
            opDeleteBodies(context, id + "cleanup", { "entities" : survivingInstantiatedBodies });
        // Delete locally-derived bodies (implied outer subtract copies and thickened solids) using
        // the same consumed-body guard.
        const survivingDerivedBodies = qConsumed(qUnion([impliedOuterSubtractBodies, thickenedOuterSubtractSolids]), Consumed.NO);
        if (!isQueryEmpty(context, survivingDerivedBodies))
            opDeleteBodies(context, id + "cleanupDerived", { "entities" : survivingDerivedBodies });

        // Update SM model geometry.
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, smBodyPostUnion, initialData, id);
        updateSheetMetalGeometry(context, id, {
                    "entities"          : qUnion([toUpdate.modifiedEntities, persistentUnionDefinitionEntities]),
                    "deletedAttributes" : toUpdate.deletedAttributes,
                    "associatedChanges" : associateChanges
                });

        setFeatureComputedParameter(context, id, { "name" : "featureName", "value" : resolveTabFeatureName(context, definition) });

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
 * Instantiates the tool Part Studio at each requested location.
 * Returns a map with:
 *   allBodies        {Query} — all instantiated bodies across all locations
 *   locationBodySets {array} — per-location body queries for per-location boolean processing
 */
function instantiateToolBodies(context is Context, id is Id, definition is map) returns map
{
    const instantiator = newInstantiator(id + "instantiate");
    var allBodies = qNothing();
    var locationBodySets = [];

    for (var location in evaluateQuery(context, definition.locations))
    {
        var placementCSys = resolveLocationCSys(context, location);
        placementCSys = applyOrientationOverrides(placementCSys, definition.flipDirection, definition.secondaryAxisType);

        // partQuery scoped to tagged bodies so that: (a) sketch-object-flagged surfaces
        // created directly from sketch regions are imported (the default SketchObject.NO
        // filter was dropping them silently), and (b) any untagged helper geometry in the
        // tool Part Studio is not imported.
        const instanceBodies = addInstance(instantiator, definition.formPartStudio, {
                    "transform" : toWorld(placementCSys),
                    "identity"  : location,
                    "partQuery" : qHasAttribute(qEverything(EntityType.BODY), SM_TAB_BODY_ATTRIBUTE_NAME)
                });
        allBodies = qUnion([allBodies, instanceBodies]);
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

    return { "allBodies" : allBodies, "locationBodySets" : locationBodySets };
}

/**
 * Resolves a coordinate system from a placement location.
 * Accepts either a mate connector body or a sketch vertex.
 */
function resolveLocationCSys(context is Context, location is Query) returns CoordSystem
{
    if (!isQueryEmpty(context, location->qBodyType(BodyType.MATE_CONNECTOR)))
        return evMateConnector(context, { "mateConnector" : location });

    // Sketch vertex: use the sketch plane normal as Z and sketch X as X.
    const sketchPlane = evOwnerSketchPlane(context, { "entity" : location });
    const vertexPoint = evVertexPoint(context, { "vertex" : location });
    return coordSystem(vertexPoint, sketchPlane.x, sketchPlane.normal);
}

/**
 * Applies flip and secondary-axis orientation overrides to a resolved coordinate system.
 * flipDirection negates the Z axis; secondaryAxisType rotates the X axis around Z.
 */
function applyOrientationOverrides(placementCSys is CoordSystem, flipDirection is boolean, secondaryAxisType is MateConnectorAxisType) returns CoordSystem
{
    var xAxis = placementCSys.xAxis;
    var zAxis = placementCSys.zAxis;

    if (flipDirection)
        zAxis = -zAxis;

    if (secondaryAxisType == MateConnectorAxisType.PLUS_Y)
        xAxis = cross(zAxis, xAxis);
    else if (secondaryAxisType == MateConnectorAxisType.MINUS_X)
        xAxis = -xAxis;
    else if (secondaryAxisType == MateConnectorAxisType.MINUS_Y)
        xAxis = -cross(zAxis, xAxis);
    // MateConnectorAxisType.PLUS_X is the default; no adjustment needed.

    return coordSystem(placementCSys.origin, xAxis, zAxis);
}

/**
 * Evaluates planes for each SM definition face in the union scope.
 * Non-planar faces are silently skipped.
 * Returns an array of Plane values.
 */
function collectDefinitionFacePlanes(context is Context, unionWallDefinitionEntities is array) returns array
{
    var planes = [];
    for (var smFace in unionWallDefinitionEntities)
    {
        var facePlane = try(evPlane(context, { "face" : smFace }));
        if (facePlane != undefined)
            planes = append(planes, facePlane);
    }
    return planes;
}

/**
 * Snaps each surface body in the given query to be exactly coplanar with its nearest SM
 * definition face.  Corrects orientation via opFlipOrientation when normals are antiparallel,
 * then translates along the wall normal to achieve exact geometric coincidence.
 *
 * @param bodies                 {Query}  Surface bodies to snap; each must be planar.
 * @param smDefinitionFacePlanes {array}  Plane values from SM definition faces.
 */
function snapBodiesToNearestDefinitionPlane(context is Context, id is Id, bodies is Query, smDefinitionFacePlanes is array)
{
    if (size(smDefinitionFacePlanes) == 0)
        return;

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

        const bodySubId = id + unstableIdComponent(snapBodyIndex);

        if (dot(bodyFacePlane.normal, nearestDefinitionPlane.normal) < 0)
        {
            opFlipOrientation(context, bodySubId + "flip", {
                        "bodies" : currentBody
                    });
        }

        const snapTranslationVector = dot(nearestDefinitionPlane.origin - bodyFacePlane.origin,
                nearestDefinitionPlane.normal) * nearestDefinitionPlane.normal;
        opTransform(context, bodySubId + "snap", {
                    "bodies"    : currentBody,
                    "transform" : transform(snapTranslationVector)
                });
    }
}

/**
 * Creates geometry-exact copies of the union surface bodies to use as implied outer
 * subtraction tools when no explicit outer subtract bodies are tagged.
 * Returns qNothing() when tagged outer subtract bodies exist or no outer scope is defined.
 */
function buildImpliedOuterSubtractBodies(context is Context, id is Id, outerSubtractBodies is Query, unionSurfaceBodies is Query, outerSubtractionScope is Query) returns Query
{
    if (!isQueryEmpty(context, outerSubtractBodies) || isQueryEmpty(context, outerSubtractionScope))
        return qNothing();

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

/**
 * Evaluates SM wall face origins, normals, and model parameters for the outer subtraction scope.
 * Used to match each outer subtract surface body to its nearest SM wall for orientation and gauge.
 * Returns an array of maps with fields: origin, normal, modelParams.
 * modelParams may be undefined when getModelParameters fails for a given face.
 */
function collectOuterScopeFaceData(context is Context, outerSubtractionScope is Query) returns array
{
    var faceData = [];
    if (isQueryEmpty(context, outerSubtractionScope))
        return faceData;

    var outerScopeDefinitionFaces = try(getSMDefinitionEntities(context, outerSubtractionScope));
    if (outerScopeDefinitionFaces == undefined)
        return faceData;

    for (var smFace in outerScopeDefinitionFaces)
    {
        var wallTangent = try silent(evFaceTangentPlane(context, { "face" : smFace, "parameter" : vector(0.5, 0.5) }));
        if (wallTangent != undefined)
        {
            faceData = append(faceData, {
                        "origin"      : wallTangent.origin,
                        "normal"      : wallTangent.normal,
                        "modelParams" : try silent(getModelParameters(context, qOwnerBody(smFace)))
                    });
        }
    }
    return faceData;
}

/**
 * Orients and thickens a single outer subtract surface body into a solid.
 * Finds the nearest outer scope SM wall face by face-center proximity, flips the surface
 * when antiparallel to the matched wall, then thickens using that wall's gauge.
 * Falls back to fallbackParams when no outer scope face data is available.
 * Returns a Query of the resulting solid body.
 *
 * @param bodyId            {Id}    Operation namespace for this body.
 * @param surfaceBody       {Query} The surface body to orient and thicken.
 * @param outerScopeFaceData {array} From collectOuterScopeFaceData.
 * @param fallbackParams    {map}   SM model parameters used when no outer scope face matches.
 */
function thickenSingleOuterSubtractBody(context is Context, bodyId is Id, surfaceBody is Query, outerScopeFaceData is array, fallbackParams is map) returns Query
{
    var closestDistance   = undefined;
    var closestWallNormal = undefined;
    var closestBodyNormal = undefined;
    var targetParams      = fallbackParams;

    for (var subtractFace in evaluateQuery(context, qOwnedByBody(surfaceBody, EntityType.FACE)))
    {
        var subtractTangent = try silent(evFaceTangentPlane(context, { "face" : subtractFace, "parameter" : vector(0.5, 0.5) }));
        if (subtractTangent == undefined)
            continue;

        for (var wallFaceData in outerScopeFaceData)
        {
            const distToWall = norm(subtractTangent.origin - wallFaceData.origin);
            if (closestDistance is undefined || distToWall < closestDistance)
            {
                closestDistance   = distToWall;
                closestWallNormal = wallFaceData.normal;
                closestBodyNormal = subtractTangent.normal;
                if (wallFaceData.modelParams != undefined)
                    targetParams = wallFaceData.modelParams;
            }
        }
    }

    if (closestWallNormal != undefined && closestBodyNormal != undefined && dot(closestBodyNormal, closestWallNormal) < 0)
        opFlipOrientation(context, bodyId + "flip", { "bodies" : surfaceBody });

    opThicken(context, bodyId + "thicken", {
                "entities"   : surfaceBody,
                "thickness1" : targetParams.frontThickness,
                "thickness2" : targetParams.backThickness
            });
    return qCreatedBy(bodyId + "thicken", EntityType.BODY)->qBodyType(BodyType.SOLID);
}

/**
 * Thickens all outer subtract surface bodies (tagged or implied) into solids.
 * When no explicit outer subtract bodies are tagged, uses implied copies instead.
 * Implied bodies use the union scope wall gauge; tagged bodies use the nearest outer scope wall gauge.
 * Returns a Query of all thickened solid bodies, or qNothing() if no outer subtract tools exist.
 *
 * @param outerSubtractBodies        {Query} Tagged outer subtract surfaces.
 * @param impliedOuterSubtractBodies {Query} Implied copies from buildImpliedOuterSubtractBodies.
 * @param outerSubtractionScope      {Query} Used to collect outer scope face data for gauge matching.
 * @param unionSMBody                {Query} Union scope SM body; provides fallback gauge parameters.
 */
function thickenOuterSubtractSurfaces(context is Context, id is Id, outerSubtractBodies is Query, impliedOuterSubtractBodies is Query, outerSubtractionScope is Query, unionSMBody is Query) returns Query
{
    var thickenedSolids = qNothing();

    if (!isQueryEmpty(context, outerSubtractBodies))
    {
        const outerScopeFaceData = collectOuterScopeFaceData(context, outerSubtractionScope);
        const fallbackParams     = getModelParameters(context, unionSMBody);
        const bodyArray          = evaluateQuery(context, outerSubtractBodies);
        for (var bodyIndex = 0; bodyIndex < size(bodyArray); bodyIndex += 1)
        {
            const thickened = thickenSingleOuterSubtractBody(context, id + "outerSubtractBody" + unstableIdComponent(bodyIndex),
                    bodyArray[bodyIndex], outerScopeFaceData, fallbackParams);
            thickenedSolids = qUnion([thickenedSolids, thickened]);
        }
    }
    else if (!isQueryEmpty(context, impliedOuterSubtractBodies))
    {
        const unionModelParams = getModelParameters(context, unionSMBody);
        const impliedBodyArray = evaluateQuery(context, impliedOuterSubtractBodies);
        for (var impliedIndex = 0; impliedIndex < size(impliedBodyArray); impliedIndex += 1)
        {
            const thickenId = id + "thickenImpliedOuterSubtract" + unstableIdComponent(impliedIndex);
            opThicken(context, thickenId, {
                        "entities"   : impliedBodyArray[impliedIndex],
                        "thickness1" : unionModelParams.frontThickness,
                        "thickness2" : unionModelParams.backThickness
                    });
            thickenedSolids = qUnion([thickenedSolids, qCreatedBy(thickenId, EntityType.BODY)->qBodyType(BodyType.SOLID)]);
        }
    }

    return thickenedSolids;
}

/**
 * Performs deRip, union, and local subtract for a single placement location.
 * DeRip is attempted first; failures are non-fatal and processing continues without it.
 * Returns the live SM body query after the union operation (qOwnerBody of the persistent definition entities).
 *
 * @param locationId                        {Id}    Unique operation namespace for this location.
 * @param locationBodies                    {Query} All instantiated bodies for this location.
 * @param persistentUnionDefinitionEntities {Query} Tracked union definition entities; resolves the live SM body.
 * @param deripPartEntityQueries            {array} Pre-computed deRip collision targets from buildDeripDataForUnionScope.
 */
function processTabAtLocation(context is Context, locationId is Id, locationBodies is Query, persistentUnionDefinitionEntities is Query, deripPartEntityQueries is array) returns Query
{
    const locationUnionBodies        = qHasAttributeWithValueMatching(locationBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_UNION_SURFACE });
    const locationLocalSubtractBodies = qHasAttributeWithValueMatching(locationBodies, SM_TAB_BODY_ATTRIBUTE_NAME, { "role" : SM_TAB_ROLE_LOCAL_SUBTRACT });

    // DeRip any rip joints that would block the union; non-fatal on failure.
    var deripEdgeCandidates = [];
    try
    {
        const smModelParams = getModelParameters(context, qOwnerBody(persistentUnionDefinitionEntities));
        opThicken(context, locationId + "thickenForDeRip", {
                    "entities"   : qOwnedByBody(locationUnionBodies, EntityType.FACE),
                    "thickness1" : smModelParams.frontThickness,
                    "thickness2" : smModelParams.backThickness
                });
        const thickenedTabBody = qCreatedBy(locationId + "thickenForDeRip", EntityType.BODY);

        if (size(deripPartEntityQueries) > 0)
        {
            const deripCollisions = try silent(evCollision(context, {
                        "tools"   : qOwnedByBody(thickenedTabBody, EntityType.FACE),
                        "targets" : qUnion(deripPartEntityQueries)
                    }));
            if (deripCollisions != undefined)
            {
                for (var collision in deripCollisions)
                {
                    if (collision["type"] != ClashType.ABUT_NO_CLASS)
                    {
                        const definitionEdges = try silent(getSMDefinitionEntities(context, collision.target, EntityType.EDGE));
                        if (definitionEdges != undefined && definitionEdges != [])
                            deripEdgeCandidates = concatenateArrays([deripEdgeCandidates, definitionEdges]);
                    }
                }
            }
        }

        opDeleteBodies(context, locationId + "deleteThickenedDeRip", { "entities" : thickenedTabBody });
    }
    catch
    {
        // Temporary thickening or collision detection failed; proceed without deRip.
    }

    if (size(deripEdgeCandidates) > 0)
        deripEdges(context, locationId + "deripRipJoints", qUnion(deripEdgeCandidates));

    // Union the tab surface into the live SM body.
    // Use try silent so that a failed first attempt does not surface error status before the
    // micro-shift retry path has had a chance to recover.
    const unionOpId = locationId + "unionTabToWall";
    var unionRetried = false;
    try silent
    {
        opBoolean(context, unionOpId, {
                    "tools"         : qUnion([qOwnerBody(persistentUnionDefinitionEntities), locationUnionBodies]),
                    "operationType" : BooleanOperationType.UNION,
                    "allowSheets"   : true
                });
    }
    catch
    {
        // Union threw. The boolean kernel requires at least a small overlapping area to merge
        // two coplanar surface bodies; a shared boundary edge alone is sometimes insufficient.
        // This commonly occurs when the tab surface's boundary edge is exactly coincident with
        // the SM wall's boundary edge (edge-adjacent placement). Apply a sub-tolerance
        // micro-shift toward the wall face interior to create a minimal overlap and retry.
        const retryOpId = locationId + "unionTabToWallRetry";
        if (!retryUnionWithMicroShift(context, retryOpId, locationUnionBodies, persistentUnionDefinitionEntities))
            throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE, ["unionScope"]);
        if (getFeatureStatus(context, retryOpId).statusEnum == ErrorStringEnum.BOOLEAN_UNION_NO_OP)
            throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE, ["unionScope"]);
        unionRetried = true;
    }

    if (!unionRetried && getFeatureStatus(context, unionOpId).statusEnum == ErrorStringEnum.BOOLEAN_UNION_NO_OP)
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE, ["unionScope"]);

    const smBodyPostUnion = qOwnerBody(persistentUnionDefinitionEntities);

    // Local subtract: cut the SM wall immediately after union.
    if (!isQueryEmpty(context, locationLocalSubtractBodies))
    {
        opBoolean(context, locationId + "localSubtract", {
                    "tools"         : locationLocalSubtractBodies,
                    "targets"       : smBodyPostUnion,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "allowSheets"   : true
                });
    }

    return smBodyPostUnion;
}

/**
 * Recovers from a failed UNION by applying a sub-tolerance micro-shift to each union surface body
 * in the direction toward the SM definition face centroid, projected into the wall plane. This
 * creates a minimal overlapping area between the tab surface and the SM wall face that the boolean
 * kernel needs to merge two coplanar surface bodies. Returns true if the retry union succeeded and
 * was not a no-op.
 *
 * @param retryOpId                         {Id}    Unique operation namespace for the retry boolean.
 * @param locationUnionBodies               {Query} Tab surface bodies to shift and union.
 * @param persistentUnionDefinitionEntities {Query} SM definition entities tracking the wall face.
 */
function retryUnionWithMicroShift(context is Context, retryOpId is Id, locationUnionBodies is Query, persistentUnionDefinitionEntities is Query) returns boolean
{
    // Compute the SM definition face centroid as an approximate "inward" target direction.
    const wallCentroid = try silent(evApproximateCentroid(context, { "entities" : persistentUnionDefinitionEntities }));
    if (wallCentroid == undefined)
        return false;

    // Shift each union surface body by 1 µm toward the wall face centroid in the wall plane.
    // This is far below any manufacturing tolerance but above the boolean kernel's precision floor.
    const bodyArray = evaluateQuery(context, locationUnionBodies);
    for (var bodyIndex = 0; bodyIndex < size(bodyArray); bodyIndex += 1)
    {
        const tabBody = bodyArray[bodyIndex];
        const tabFacePlane = try silent(evPlane(context, { "face" : qOwnedByBody(tabBody, EntityType.FACE) }));
        if (tabFacePlane == undefined)
            continue;

        // Project the wall-centroid direction into the wall plane to stay coplanar after shift.
        const toWall = wallCentroid - tabFacePlane.origin;
        const toWallInPlane = toWall - dot(toWall, tabFacePlane.normal) * tabFacePlane.normal;
        // Treat in-plane vectors shorter than the kernel's zero-length tolerance as degenerate
        // (the tab centroid is essentially at the wall centroid) and skip the shift.
        if (norm(toWallInPlane) < TOLERANCE.zeroLength * meter)
            continue;

        const shiftVectorTowardWall = normalize(toWallInPlane) * EDGE_ADJACENT_MICRO_SHIFT_DISTANCE;
        try
        {
            opTransform(context, retryOpId + "microShift" + unstableIdComponent(bodyIndex), {
                        "bodies"    : tabBody,
                        "transform" : transform(shiftVectorTowardWall)
                    });
        }
        catch
        {
            // Shift failed for this body; skip it and allow the retry to proceed with
            // whatever bodies were successfully shifted.
        }
    }

    try
    {
        opBoolean(context, retryOpId, {
                    "tools"         : qUnion([qOwnerBody(persistentUnionDefinitionEntities), locationUnionBodies]),
                    "operationType" : BooleanOperationType.UNION,
                    "allowSheets"   : true
                });
    }
    catch
    {
        return false;
    }
    return getFeatureStatus(context, retryOpId).statusEnum != ErrorStringEnum.BOOLEAN_UNION_NO_OP;
}

/**
 * Applies the outer subtraction pass to SM scope faces and any non-SM solid scope.
 * Mirrors the smSubtractTab + solidSubtractTab pattern from sheetMetalTab.fs.
 * Calls getSMDefinitionEntities fresh to avoid stale entity references from prior topology mutations.
 *
 * @param thickenedOuterSubtractSolids {Query}          Thickened solid subtraction tools.
 * @param outerSubtractionScope        {Query}          Bodies or faces to subtract from.
 * @param outerSubtractionOffset       {ValueWithUnits} Expansion offset applied before cutting.
 */
function applyOuterSubtraction(context is Context, id is Id, thickenedOuterSubtractSolids is Query, outerSubtractionScope is Query, outerSubtractionOffset is ValueWithUnits)
{
    if (isQueryEmpty(context, thickenedOuterSubtractSolids) || isQueryEmpty(context, outerSubtractionScope))
        return;

    if (outerSubtractionOffset > 0 * meter)
    {
        opOffsetFace(context, id + "offsetOuterSubtractTools", {
                    "moveFaces"      : qOwnedByBody(thickenedOuterSubtractSolids, EntityType.FACE),
                    "offsetDistance" : outerSubtractionOffset
                });
    }

    const separatedOuterScope = separateSheetMetalQueries(context, outerSubtractionScope);

    // SM definition face targets — fresh call avoids stale entity IDs from prior mutations.
    const outerScopeSMFacesQuery = qUnion([
                qOwnedByBody(qEntityFilter(separatedOuterScope.sheetMetalQueries, EntityType.BODY), EntityType.FACE),
                qEntityFilter(separatedOuterScope.sheetMetalQueries, EntityType.FACE)
            ]);
    var freshOuterScopeDefinitionFaces = try(getSMDefinitionEntities(context, outerScopeSMFacesQuery, EntityType.FACE));
    if (freshOuterScopeDefinitionFaces is undefined)
        freshOuterScopeDefinitionFaces = [];

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

/**
 * Collects rendered-part entity queries for deRip collision detection against the union scope
 * wall's adjacent definition edges.  Called once before the per-location loop since the union
 * scope does not change between locations.
 *
 * @param unionDefinitionEntitiesQuery {Query} SM definition entities from the union scope.
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

/**
 * Reads SM_TAB_FEATURE_NAME_VAR from the tool Part Studio context.
 * Returns a " - <name>" suffix string, or empty string if the variable is unset.
 */
function resolveTabFeatureName(context is Context, definition is map) returns string
{
    try
    {
        var sourceConfig = {};
        if (definition.formPartStudio.configuration != undefined)
            sourceConfig = definition.formPartStudio.configuration;
        const sourceContext = definition.formPartStudio.buildFunction(sourceConfig);
        const retrievedName = getVariable(sourceContext, SM_TAB_FEATURE_NAME_VAR);
        if (retrievedName != undefined && retrievedName is string && retrievedName != "")
            return FEATURE_NAME_SEPARATOR ~ retrievedName;
    }
    catch { }
    return "";
}

