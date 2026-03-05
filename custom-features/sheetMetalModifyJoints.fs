FeatureScript 2892;
// Sheet Metal Modify Joints (Multi-Group) Custom Feature
//
// This feature replaces the standard single-pick "Modify joint" (sheetMetalJoint) feature with
// an array-based approach that supports multiple independent joint groups in a single feature.
// Each group has its own joint selection and type settings, so the user can simultaneously:
//   • Turn one set of joints into RIP / Butt Direction 1
//   • Turn another set into RIP / Butt Direction 2
//   • Adjust bend radii on a third set
//   … all within one feature, with a single sheet metal rebuild at the end.
//
// By batching every attribute change across every group before calling updateSheetMetalGeometry
// exactly once, the feature avoids the O(n) rebuild overhead that accumulates when many
// individual sheetMetalJoint features appear in the feature tree.
//
// Joints that are incompatible with the chosen type (e.g. face bends selected with RIP,
// or face bends with a non-default radius) are silently skipped so that a mixed selection
// of edge and face bends still succeeds for the compatible subset.

// Enums exported so they are accessible from the precondition annotation
export import(path : "onshape/std/smjointstyle.gen.fs", version : "2892.0");

// SMJointType is used internally by the helper functions but is not part of the
// public precondition API; ModifyJointType below is used instead to limit the
// choices presented to the user to only the applicable joint types.
import(path : "onshape/std/smjointtype.gen.fs", version : "2892.0");

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
 * The joint types available when modifying joints with this feature.
 * TANGENT is intentionally excluded: it is set automatically by the geometry engine
 * when two faces meet tangentially and cannot be meaningfully forced onto arbitrary geometry.
 */
export enum ModifyJointType
{
    annotation { "Name" : "Bend" }
    BEND,
    annotation { "Name" : "Rip" }
    RIP
}

/**
 * Array-based multi-group version of "Modify joint". Define any number of joint groups; each
 * group independently selects joints and specifies a joint type with its type-specific options.
 * For example:
 *   Group 1 — RIP / Butt Direction 1 on a set of joints along one edge
 *   Group 2 — RIP / Butt Direction 2 on a different set of joints
 *   Group 3 — BEND with a custom radius on yet another set
 *
 * All attribute changes from all groups are batched before the single updateSheetMetalGeometry
 * call, so the rebuild cost is always one pass regardless of the number of groups or joints.
 */
annotation { "Feature Type Name" : "Modify joints",
        "Filter Selector" : "allparts" }
export const sheetMetalModifyJoints = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
                    "Name" : "Joint groups",
                    "Item name" : "Group",
                    "Driven query" : "joints",
                    "Item label template" : "[#jointType] #joints",
                    "UIHint" : UIHint.COLLAPSE_ARRAY_ITEMS
                }
        definition.jointGroups is array;

        for (var group in definition.jointGroups)
        {
            annotation { "Name" : "Joints",
                        "Filter" : (SheetMetalDefinitionEntityType.FACE || SheetMetalDefinitionEntityType.EDGE) && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES,
                        "MaxNumberOfPicks" : 500 }
            group.joints is Query;

            annotation { "Name" : "Joint type", "Default" : ModifyJointType.BEND }
            group.jointType is ModifyJointType;

            if (group.jointType == ModifyJointType.BEND)
            {
                annotation { "Name" : "Use model bend radius", "Default" : true }
                group.useDefaultRadius is boolean;
                if (!group.useDefaultRadius)
                {
                    annotation { "Name" : "Bend radius" }
                    isLength(group.radius, SM_BEND_RADIUS_BOUNDS);
                }

                annotation { "Name" : "Use model K Factor", "Default" : true }
                group.useDefaultKFactor is boolean;
                if (!group.useDefaultKFactor)
                {
                    annotation { "Name" : "K Factor" }
                    isReal(group.kFactor, K_FACTOR_BOUNDS);
                }
            }

            if (group.jointType == ModifyJointType.RIP)
            {
                annotation { "Name" : "Joint style" }
                group.jointStyle is SMJointStyle;
            }
        }
    }
    {
        // Collect all joints from every group into a single union for global validation.
        // This ensures checkNotInFeaturePattern and the single-model check cover all groups.
        var allGroupJointQueries = [];
        for (var group in definition.jointGroups)
        {
            allGroupJointQueries = append(allGroupJointQueries, group.joints);
        }
        const allJointsQuery = qUnion(allGroupJointQueries);

        // Prevents accidental use inside a feature pattern where rebuild semantics differ
        checkNotInFeaturePattern(context, allJointsQuery, ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);

        // All selected joints across every group must belong to a single active sheet metal model
        if (!areEntitiesFromSingleActiveSheetMetalModel(context, allJointsQuery))
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["jointGroups"]);
        }

        var allModifiedEdges = [];
        var processedJointCount = 0;

        // The controlling feature id used in attributes must always be the top-level feature
        // string so that the sheet metal table can locate this feature for editing.
        const controllingFeatureId = toAttributeId(id);

        // Outer loop: iterate over each joint group
        for (var groupIndex = 0; groupIndex < size(definition.jointGroups); groupIndex += 1)
        {
            var group = definition.jointGroups[groupIndex];
            var groupSelectedEntities = evaluateQuery(context, group.joints);

            // Inner loop: iterate over each joint within the current group
            for (var jointIndex = 0; jointIndex < size(groupSelectedEntities); jointIndex += 1)
            {
                // Access the current joint's query from the evaluated array
                var currentJointQuery = groupSelectedEntities[jointIndex] as Query;

                // Each joint gets a unique sub-id ONLY for the bendAngle temporary operation
                // so that temporary fillet sub-operations do not collide across groups or joints.
                var jointSubId = id + ("group" ~ groupIndex ~ "joint" ~ jointIndex);

                // Determine whether this entity resolves to an edge joint or a face bend
                var jointDefinitionEntity = resolveJointDefinitionEntity(context, currentJointQuery, EntityType.EDGE);
                var isFaceBend = false;

                if (jointDefinitionEntity == undefined)
                {
                    jointDefinitionEntity = resolveJointDefinitionEntity(context, currentJointQuery, EntityType.FACE);
                    isFaceBend = true;
                }

                // Skip if the entity does not resolve to a recognizable joint
                if (jointDefinitionEntity == undefined)
                {
                    continue;
                }

                var existingAttribute = getJointAttribute(context, jointDefinitionEntity);
                if (existingAttribute == undefined)
                {
                    continue;
                }

                // Build the replacement attribute according to the group's joint type,
                // skipping combinations that are geometrically invalid for this entity.
                var newAttribute;
                if (group.jointType == ModifyJointType.BEND)
                {
                    // Resolve the bend radius: use the model default or the user-supplied value
                    var bendRadius;
                    if (group.useDefaultRadius)
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
                        bendRadius = group.radius;
                    }

                    // Resolve the K-factor: use the model default or the user-supplied value
                    var kFactor;
                    if (group.useDefaultKFactor)
                    {
                        kFactor = getDefaultKFactor(context, currentJointQuery);
                    }
                    else
                    {
                        kFactor = group.kFactor;
                    }

                    if (!isFaceBend)
                    {
                        newAttribute = buildEdgeBendAttribute(context, jointSubId, controllingFeatureId,
                            jointDefinitionEntity, existingAttribute, bendRadius, group.useDefaultRadius,
                            kFactor, group.useDefaultKFactor);
                    }
                    else
                    {
                        newAttribute = buildFaceBendAttribute(controllingFeatureId,
                            existingAttribute, kFactor, group.useDefaultKFactor);
                    }
                }
                else if (group.jointType == ModifyJointType.RIP)
                {
                    if (isFaceBend)
                    {
                        // RIP is not valid for face bends — skip silently
                        continue;
                    }
                    newAttribute = buildRipAttribute(controllingFeatureId, existingAttribute, group.jointStyle);
                }
                else
                {
                    // Unrecognized joint type — skip rather than hard-fail so the loop continues
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
        }

        // If no joints across any group were successfully processed, raise an error
        if (processedJointCount == 0)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["jointGroups"]);
        }

        // Single sheet metal rebuild covering all modified joints from all groups
        var allModifiedEdgesQuery = qUnion(allModifiedEdges);
        updateSheetMetalGeometry(context, id, {
                    "entities" : allModifiedEdgesQuery,
                    "associatedChanges" : allModifiedEdgesQuery
                });
    }, {
        jointGroups : [
            {
                jointType : ModifyJointType.BEND,
                jointStyle : SMJointStyle.EDGE,
                useDefaultRadius : true,
                useDefaultKFactor : true
            }
        ]
    });


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
