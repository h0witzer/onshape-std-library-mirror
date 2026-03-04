FeatureScript 2892;
// Sheet Metal Modify Joints (Multi-Joint) Custom Feature
//
// This feature is a multi-selection version of the standard "Modify joint" (sheetMetalJoint)
// feature. Selecting many joints and applying settings one at a time triggers a full sheet metal
// rebuild for every joint, causing significant performance overhead. By collecting all attribute
// changes first and calling updateSheetMetalGeometry only once at the end, this feature reduces
// rebuild cost to a single pass regardless of how many joints are selected.
//
// Joints that are incompatible with the chosen type (e.g. face bends selected with RIP or
// TANGENT, or face bends with a non-default radius) are silently skipped so that a mixed
// selection of edge and face bends still succeeds for the compatible subset.

// Enums exported so they are accessible from the precondition annotation
export import(path : "onshape/std/smjointtype.gen.fs", version : "2892.0");
export import(path : "onshape/std/smjointstyle.gen.fs", version : "2892.0");

import(path : "onshape/std/common.fs", version : "2892.0");
import(path : "onshape/std/attributes.fs", version : "2892.0");
import(path : "onshape/std/containers.fs", version : "2892.0");
import(path : "onshape/std/evaluate.fs", version : "2892.0");
import(path : "onshape/std/feature.fs", version : "2892.0");
import(path : "onshape/std/math.fs", version : "2892.0");
import(path : "onshape/std/modifyFillet.fs", version : "2892.0");
import(path : "onshape/std/query.fs", version : "2892.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2892.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2892.0");
import(path : "onshape/std/string.fs", version : "2892.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2892.0");
import(path : "onshape/std/units.fs", version : "2892.0");
import(path : "onshape/std/valueBounds.fs", version : "2892.0");

/**
 * Multi-joint version of "Modify joint". Select any number of sheet metal joints and apply
 * a uniform joint-type change (BEND / RIP / TANGENT) with a single sheet metal rebuild.
 *
 * Compared with using the standard single-joint Modify Joint feature repeatedly, this
 * feature avoids the O(n) rebuild overhead that accumulates when many individual
 * sheetMetalJoint features appear in the feature tree.
 */
annotation { "Feature Type Name" : "Modify joints",
        "Filter Selector" : "allparts",
        "Editing Logic Function" : "sheetMetalModifyJointsEditLogic" }
export const sheetMetalModifyJoints = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Joints",
                    "Filter" : (SheetMetalDefinitionEntityType.FACE || SheetMetalDefinitionEntityType.EDGE) && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES,
                    "MaxNumberOfPicks" : 500 }
        definition.joints is Query;

        annotation { "Name" : "Joint type", "Default" : SMJointType.BEND }
        definition.jointType is SMJointType;

        if (definition.jointType == SMJointType.BEND)
        {
            annotation { "Name" : "Use model bend radius", "Default" : true }
            definition.useDefaultRadius is boolean;
            if (!definition.useDefaultRadius)
            {
                annotation { "Name" : "Bend radius" }
                isLength(definition.radius, SM_BEND_RADIUS_BOUNDS);
            }

            annotation { "Name" : "Use model K Factor", "Default" : true }
            definition.useDefaultKFactor is boolean;
            if (!definition.useDefaultKFactor)
            {
                annotation { "Name" : "K Factor" }
                isReal(definition.kFactor, K_FACTOR_BOUNDS);
            }
        }

        if (definition.jointType == SMJointType.RIP)
        {
            // hasStyle is driven by editing logic; always hidden from user
            annotation { "Name" : "Has style", "Default" : true, "UIHint" : UIHint.ALWAYS_HIDDEN }
            definition.hasStyle is boolean;
            if (definition.hasStyle)
            {
                annotation { "Name" : "Joint style" }
                definition.jointStyle is SMJointStyle;
            }
        }
    }
    {
        // Prevents accidental use inside a feature pattern where rebuild semantics differ
        checkNotInFeaturePattern(context, definition.joints, ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);

        // All selected joints must belong to a single active sheet metal model
        if (!areEntitiesFromSingleActiveSheetMetalModel(context, definition.joints))
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["joints"]);
        }

        // Iterate over each selected entity individually so that per-joint attribute changes
        // can be batched before the single updateSheetMetalGeometry call at the end.
        var selectedEntities = evaluateQuery(context, definition.joints);
        var allModifiedEdges = [];
        var processedJointCount = 0;

        // The controlling feature id used in attributes must always be the top-level feature
        // string so that the sheet metal table can locate this feature for editing.
        const controllingFeatureId = toAttributeId(id);

        for (var jointIndex = 0; jointIndex < size(selectedEntities); jointIndex += 1)
        {
            // Access the current joint's query from the evaluated array
            var currentJointQuery = selectedEntities[jointIndex] as Query;

            // Each joint gets a unique sub-id ONLY for the bendAngle temporary operation
            // so that temporary fillet sub-operations do not collide across iterations.
            var jointSubId = id + ("joint" ~ jointIndex);

            // Determine whether this entity resolves to an edge joint or a face bend
            var jointDefinitionEntity = resolveJointDefinitionEntity(context, currentJointQuery, EntityType.EDGE);
            var isFaceBend = false;

            if (jointDefinitionEntity == undefined)
            {
                jointDefinitionEntity = resolveJointDefinitionEntity(context, currentJointQuery, EntityType.FACE);
                isFaceBend = true;
            }

            // Skip if the entity does not resolve to a recognisable joint
            if (jointDefinitionEntity == undefined)
            {
                continue;
            }

            var existingAttribute = getJointAttribute(context, jointDefinitionEntity);
            if (existingAttribute == undefined)
            {
                continue;
            }

            // Build the replacement attribute according to the chosen joint type,
            // skipping combinations that are geometrically invalid for this entity.
            var newAttribute;
            if (definition.jointType == SMJointType.BEND)
            {
                // Resolve the bend radius: use the model default or the user-supplied value
                var bendRadius;
                if (definition.useDefaultRadius)
                {
                    bendRadius = getDefaultBendRadius(context, currentJointQuery);
                }
                else if (isFaceBend)
                {
                    // Custom radius cannot be applied to a face bend — skip silently
                    continue;
                }
                else
                {
                    bendRadius = definition.radius;
                }

                // Resolve the K-factor: use the model default or the user-supplied value
                var kFactor;
                if (definition.useDefaultKFactor)
                {
                    kFactor = getDefaultKFactor(context, currentJointQuery);
                }
                else
                {
                    kFactor = definition.kFactor;
                }

                if (!isFaceBend)
                {
                    newAttribute = buildEdgeBendAttribute(context, jointSubId, controllingFeatureId,
                        jointDefinitionEntity, existingAttribute, bendRadius, definition.useDefaultRadius,
                        kFactor, definition.useDefaultKFactor);
                }
                else
                {
                    newAttribute = buildFaceBendAttribute(controllingFeatureId,
                        existingAttribute, kFactor, definition.useDefaultKFactor);
                }
            }
            else if (definition.jointType == SMJointType.RIP)
            {
                if (isFaceBend)
                {
                    // RIP is not valid for face bends — skip silently
                    continue;
                }
                newAttribute = buildRipAttribute(controllingFeatureId, existingAttribute, definition.jointStyle);
            }
            else if (definition.jointType == SMJointType.TANGENT)
            {
                if (isFaceBend)
                {
                    // TANGENT is not valid for face bends — skip silently
                    continue;
                }
                newAttribute = buildTangentAttribute(controllingFeatureId, existingAttribute);
            }
            else
            {
                // Unrecognised joint type — skip rather than hard-fail so the loop continues
                continue;
            }

            // Verify the attribute is geometrically compatible before committing
            if (!isEntityAppropriateForAttribute(context, jointDefinitionEntity, newAttribute))
            {
                continue;
            }

            // Apply the attribute change and record the affected edges for the batch update
            var modifiedEdgeQuery = replaceSMAttribute(context, existingAttribute, newAttribute);
            allModifiedEdges = append(allModifiedEdges, modifiedEdgeQuery);
            processedJointCount += 1;
        }

        // If no joints were successfully processed, raise an error
        if (processedJointCount == 0)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["joints"]);
        }

        // Single sheet metal rebuild covering all modified joints
        var allModifiedEdgesQuery = qUnion(allModifiedEdges);
        updateSheetMetalGeometry(context, id, {
                    "entities" : allModifiedEdgesQuery,
                    "associatedChanges" : allModifiedEdgesQuery
                });
    }, { jointStyle : SMJointStyle.EDGE, useDefaultRadius : true, hasStyle : true, useDefaultKFactor : true });


// ─── Editing logic ──────────────────────────────────────────────────────────

/**
 * @internal
 * Editing logic for sheetMetalModifyJoints.
 * Keeps the hidden `hasStyle` flag in sync with whether the selected joints have a non-zero
 * angle (which determines whether a RIP joint style option is meaningful).
 * Parameter isCreating is required so that this function is also called during editing.
 */
export function sheetMetalModifyJointsEditLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, specifiedParameters is map, hiddenBodies is Query) returns map
{
    // Editing logic must not throw — use try silent so that partial or empty selections
    // return the current definition unchanged rather than surfacing an error to the user.
    const definitionEntities = try silent(getSMDefinitionEntities(context, definition.joints, EntityType.EDGE));
    if (definitionEntities == undefined || size(definitionEntities) == 0)
    {
        return definition;
    }

    const jointEdgesQuery = qUnion(definitionEntities);

    // Check the first resolved joint edge: if it has a non-trivial angle the RIP style
    // option should be shown. We conservatively check only the first edge to avoid
    // iterating the full selection on every edit-logic invocation.
    // try silent is appropriate here as the edit logic must not throw.
    var existingAttribute = try silent(getJointAttribute(context, jointEdgesQuery));
    if (existingAttribute != undefined &&
        existingAttribute.angle != undefined &&
        existingAttribute.angle.value != undefined &&
        abs(existingAttribute.angle.value / radian) > TOLERANCE.zeroAngle)
    {
        definition.hasStyle = true;
    }
    else
    {
        definition.hasStyle = false;
    }

    return definition;
}


// ─── Private helper functions ────────────────────────────────────────────────

/**
 * Resolve a single selected entity to its sheet metal definition entity of the requested type.
 * Returns undefined when no unique match is found (the caller skips the entity in that case).
 *
 * Inputs:
 *   context    - Evaluation context
 *   entity     - Query for a single user-selected entity
 *   entityType - EntityType.EDGE or EntityType.FACE
 * Output: Query for the resolved definition entity, or undefined
 */
function resolveJointDefinitionEntity(context is Context, entity is Query, entityType is EntityType)
{
    const definitionEntitiesArray = getSMDefinitionEntities(context, entity);
    const definitionEntityQuery = qUnion(definitionEntitiesArray);
    var filteredQuery = qEntityFilter(definitionEntityQuery, entityType);

    if (size(evaluateQuery(context, filteredQuery)) != 1)
    {
        return undefined;
    }
    return filteredQuery;
}

/**
 * Return the default bend radius from the sheet metal model that owns the given entity.
 *
 * Inputs:
 *   context - Evaluation context
 *   entity  - Query for any entity belonging to the sheet metal model
 * Output: ValueWithUnits — the default bend radius length
 */
function getDefaultBendRadius(context is Context, entity is Query) returns ValueWithUnits
{
    var sheetMetalEntity = qUnion(getSMDefinitionEntities(context, entity));
    var modelParameters = getModelParameters(context, qOwnerBody(sheetMetalEntity));
    return modelParameters.defaultBendRadius;
}

/**
 * Return the default K-factor from the sheet metal model that owns the given entity.
 *
 * Inputs:
 *   context - Evaluation context
 *   entity  - Query for any entity belonging to the sheet metal model
 * Output: number — the dimensionless K-factor
 */
function getDefaultKFactor(context is Context, entity is Query) returns number
{
    var sheetMetalEntity = qUnion(getSMDefinitionEntities(context, entity));
    var modelParameters = getModelParameters(context, qOwnerBody(sheetMetalEntity));
    return modelParameters["k-factor"];
}

/**
 * Build a replacement SMAttribute for an edge bend joint.
 * Mirrors the logic in sheetMetalJoint.fs createNewEdgeBendAttribute.
 *
 * Inputs:
 *   context              - Evaluation context
 *   id                   - Unique sub-id for this joint (used only for bendAngle sub-operations)
 *   controllingFeatureId - String attribute id of the top-level feature (for sheet metal table linkage)
 *   jointEdge            - Query for the definition edge being modified
 *   existingAttribute    - Current SMAttribute on this edge
 *   radius               - Bend radius to apply (ValueWithUnits)
 *   useDefaultRadius     - True when radius comes from the model default
 *   kFactor              - K-factor to apply (number)
 *   useDefaultKFactor    - True when kFactor comes from the model default
 * Output: SMAttribute with BEND joint type
 */
function buildEdgeBendAttribute(context is Context, id is Id, controllingFeatureId is string, jointEdge is Query,
    existingAttribute is SMAttribute,
    radius, useDefaultRadius is boolean,
    kFactor, useDefaultKFactor is boolean) returns SMAttribute
precondition
{
    isLength(radius);
}
{
    var bendAttribute;
    if (existingAttribute.jointType.value != SMJointType.BEND)
    {
        bendAttribute = makeSMJointAttribute(existingAttribute.attributeId);
        bendAttribute.angle = existingAttribute.angle;
    }
    else
    {
        bendAttribute = existingAttribute;
    }

    // For non-planar walls the bend angle depends on the radius and must be recomputed
    const planarAdjacentFaces = qGeometry(qAdjacent(jointEdge, AdjacencyType.EDGE, EntityType.FACE), GeometryType.PLANE);
    if (size(evaluateQuery(context, planarAdjacentFaces)) != 2)
    {
        const recomputedAngle = try(bendAngle(context, id, jointEdge, radius));
        if (recomputedAngle == undefined || abs(recomputedAngle) < TOLERANCE.zeroAngle * radian)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_NO_0_ANGLE_BEND, ["joints"]);
        }
        bendAttribute.angle = { "value" : recomputedAngle, "canBeEdited" : false };
    }

    bendAttribute.jointType = {
            "value" : SMJointType.BEND,
            "controllingFeatureId" : controllingFeatureId,
            "parameterIdInFeature" : "jointType",
            "canBeEdited" : true
        };
    bendAttribute.bendType = {
            "value" : SMBendType.STANDARD,
            "canBeEdited" : false
        };
    bendAttribute.radius = {
            "value" : radius,
            "canBeEdited" : true,
            "isDefault" : useDefaultRadius
        };
    bendAttribute['k-factor'] = {
            "value" : kFactor,
            "canBeEdited" : true,
            "isDefault" : useDefaultKFactor
        };

    // When either radius or K-factor is overridden both must be linked to this feature so
    // that subsequent table-driven edits update this feature rather than creating new ones.
    if (!useDefaultRadius || !useDefaultKFactor)
    {
        bendAttribute.radius.controllingFeatureId = controllingFeatureId;
        bendAttribute.radius.parameterIdInFeature = "radius";
        bendAttribute.radius.defaultIdInFeature = "useDefaultRadius";
        bendAttribute['k-factor'].controllingFeatureId = controllingFeatureId;
        bendAttribute['k-factor'].parameterIdInFeature = "kFactor";
        bendAttribute['k-factor'].defaultIdInFeature = "useDefaultKFactor";
    }
    return bendAttribute;
}

/**
 * Build a replacement SMAttribute for a face bend joint, updating only the K-factor.
 * The bend radius of a face bend is defined by the geometry and must not be overridden.
 * Mirrors the logic in sheetMetalJoint.fs createNewFaceBendAttribute.
 *
 * Inputs:
 *   controllingFeatureId - String attribute id of the top-level feature (for sheet metal table linkage)
 *   existingAttribute    - Current SMAttribute on this face
 *   kFactor              - K-factor to apply (number)
 *   useDefaultKFactor    - True when kFactor comes from the model default
 * Output: SMAttribute with updated K-factor
 */
function buildFaceBendAttribute(controllingFeatureId is string,
    existingAttribute is SMAttribute,
    kFactor, useDefaultKFactor is boolean) returns SMAttribute
{
    var bendAttribute = existingAttribute;

    bendAttribute['k-factor'] = {
            "value" : kFactor,
            "canBeEdited" : true,
            "isDefault" : useDefaultKFactor
        };
    if (!useDefaultKFactor)
    {
        bendAttribute['k-factor'].controllingFeatureId = controllingFeatureId;
        bendAttribute['k-factor'].parameterIdInFeature = "kFactor";
        bendAttribute['k-factor'].defaultIdInFeature = "useDefaultKFactor";
    }
    return bendAttribute;
}

/**
 * Build a replacement SMAttribute for a RIP joint.
 * Mirrors the logic in sheetMetalJoint.fs createNewRipAttribute.
 *
 * Inputs:
 *   controllingFeatureId - String attribute id of the top-level feature (for sheet metal table linkage)
 *   existingAttribute    - Current SMAttribute on this edge
 *   jointStyle           - SMJointStyle value (EDGE, INSIDE, OUTSIDE, etc.)
 * Output: SMAttribute with RIP joint type
 */
function buildRipAttribute(controllingFeatureId is string, existingAttribute is SMAttribute, jointStyle) returns SMAttribute
{
    var ripAttribute = makeSMJointAttribute(existingAttribute.attributeId);
    ripAttribute.jointType = {
            "value" : SMJointType.RIP,
            "controllingFeatureId" : controllingFeatureId,
            "parameterIdInFeature" : "jointType",
            "canBeEdited" : true
        };
    ripAttribute.angle = existingAttribute.angle;

    // Joint style is only meaningful for non-zero-angle joints
    if (ripAttribute.angle != undefined &&
        ripAttribute.angle.value != undefined &&
        abs(ripAttribute.angle.value / radian) > TOLERANCE.zeroAngle)
    {
        ripAttribute.jointStyle = {
                "value" : jointStyle,
                "controllingFeatureId" : controllingFeatureId,
                "parameterIdInFeature" : "jointStyle",
                "canBeEdited" : true
            };
    }
    return ripAttribute;
}

/**
 * Build a replacement SMAttribute for a TANGENT joint.
 * Mirrors the logic in sheetMetalJoint.fs createNewTangentAttribute.
 *
 * Inputs:
 *   controllingFeatureId - String attribute id of the top-level feature (for sheet metal table linkage)
 *   existingAttribute    - Current SMAttribute on this edge
 * Output: SMAttribute with TANGENT joint type
 */
function buildTangentAttribute(controllingFeatureId is string, existingAttribute is SMAttribute) returns SMAttribute
{
    var tangentAttribute = makeSMJointAttribute(existingAttribute.attributeId);
    tangentAttribute.jointType = {
            "value" : SMJointType.TANGENT,
            "controllingFeatureId" : controllingFeatureId,
            "parameterIdInFeature" : "jointType",
            "canBeEdited" : true
        };
    return tangentAttribute;
}
