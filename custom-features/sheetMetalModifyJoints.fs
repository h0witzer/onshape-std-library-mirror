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
// Joints that are incompatible with the chosen type (e.g. face bends selected with RIP or
// TANGENT, or face bends with a non-default radius) are silently skipped so that a mixed
// selection of edge and face bends still succeeds for the compatible subset.
//
// Joint type and style options adapt to the selected geometry:
//   • TANGENT appears in the dropdown only when the selected joints have zero angle
//     (tangent-capable geometry), because TANGENT is only geometrically meaningful there.
//   • Butt joint styles (Direction 1 / Direction 2) appear only when selected joints have
//     a non-zero angle, because direction only matters for angled rip joints.
// The hidden booleans canBeTangent and hasStyle are set per-group by the editing logic
// function sheetMetalModifyJointsEditLogic and gate these conditional UI sections.

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
 * The joint types available when the selected joints are NOT tangent-capable.
 * Shown when the editing logic determines that no selected joint has a zero angle.
 */
export enum ModifyJointType
{
    annotation { "Name" : "Bend" }
    BEND,
    annotation { "Name" : "Rip" }
    RIP
}

/**
 * The joint types available when at least one selected joint IS tangent-capable
 * (i.e. has a zero or undefined angle).  TANGENT is added to the list so the user
 * can restore a joint that was previously changed away from its natural tangent state.
 */
export enum ModifyJointTypeWithTangent
{
    annotation { "Name" : "Bend" }
    BEND,
    annotation { "Name" : "Rip" }
    RIP,
    annotation { "Name" : "Tangent" }
    TANGENT
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
        "Filter Selector" : "allparts",
        "Editing Logic Function" : "sheetMetalModifyJointsEditLogic" }
export const sheetMetalModifyJoints = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
                    "Name" : "Joint groups",
                    "Item name" : "Group",
                    "Driven query" : "joints",
                    "Item label template" : "#joints",
                    // Note: the label template uses only #joints (not #jointType) because the
                    // joint type is split across two parameters — group.jointType when
                    // canBeTangent is false, and group.jointTypeWithTangent when true — so a
                    // single #jointType reference would show a stale value in tangent mode.
                    "UIHint" : UIHint.COLLAPSE_ARRAY_ITEMS
                }
        definition.jointGroups is array;

        for (var group in definition.jointGroups)
        {
            annotation { "Name" : "Joints",
                        "Filter" : (SheetMetalDefinitionEntityType.FACE || SheetMetalDefinitionEntityType.EDGE) && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES,
                        "MaxNumberOfPicks" : 500 }
            group.joints is Query;

            // Hidden flag set by sheetMetalModifyJointsEditLogic.
            // True when any selected joint has zero/undefined angle (tangent-capable geometry).
            annotation { "Name" : "Can be tangent", "Default" : false, "UIHint" : UIHint.ALWAYS_HIDDEN }
            group.canBeTangent is boolean;

            // Hidden flag set by sheetMetalModifyJointsEditLogic.
            // True when any selected joint has non-zero angle (butt joint directions are meaningful).
            annotation { "Name" : "Has style", "Default" : false, "UIHint" : UIHint.ALWAYS_HIDDEN }
            group.hasStyle is boolean;

            // When no selected joint is tangent-capable, show Bend and Rip only
            if (!group.canBeTangent)
            {
                annotation { "Name" : "Joint type", "Default" : ModifyJointType.BEND }
                group.jointType is ModifyJointType;
            }

            // When at least one selected joint is tangent-capable, add Tangent to the list
            if (group.canBeTangent)
            {
                annotation { "Name" : "Joint type", "Default" : ModifyJointTypeWithTangent.BEND }
                group.jointTypeWithTangent is ModifyJointTypeWithTangent;
            }

            // BEND sub-options — shown when Bend is selected in whichever dropdown is active
            if ((!group.canBeTangent && group.jointType == ModifyJointType.BEND) ||
                (group.canBeTangent && group.jointTypeWithTangent == ModifyJointTypeWithTangent.BEND))
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

            // RIP sub-options — joint style (butt direction) only shown when the joint has a
            // non-zero angle, because direction is meaningless for a flat zero-angle rip joint
            if (group.hasStyle &&
                ((!group.canBeTangent && group.jointType == ModifyJointType.RIP) ||
                 (group.canBeTangent && group.jointTypeWithTangent == ModifyJointTypeWithTangent.RIP)))
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

                // Build the replacement attribute according to the group's effective joint type,
                // skipping combinations that are geometrically invalid for this entity.
                //
                // jointTypeParameterId names the precondition parameter that the user set the
                // joint type through. The sheet metal table uses this to re-open the right
                // dropdown when the user edits the feature from the table.
                var newAttribute;
                const effectiveJointType = getGroupJointType(group);
                const jointTypeParameterId = group.canBeTangent ? "jointTypeWithTangent" : "jointType";
                if (effectiveJointType == SMJointType.BEND)
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
                        newAttribute = buildEdgeBendAttribute(context, jointSubId, controllingFeatureId, jointTypeParameterId,
                            jointDefinitionEntity, existingAttribute, bendRadius, group.useDefaultRadius,
                            kFactor, group.useDefaultKFactor);
                    }
                    else
                    {
                        newAttribute = buildFaceBendAttribute(controllingFeatureId,
                            existingAttribute, kFactor, group.useDefaultKFactor);
                    }
                }
                else if (effectiveJointType == SMJointType.RIP)
                {
                    if (isFaceBend)
                    {
                        // RIP is not valid for face bends — skip silently
                        continue;
                    }
                    newAttribute = buildRipAttribute(controllingFeatureId, jointTypeParameterId, existingAttribute, group.jointStyle);
                }
                else if (effectiveJointType == SMJointType.TANGENT)
                {
                    if (isFaceBend)
                    {
                        // TANGENT is not valid for face bends — skip silently
                        continue;
                    }
                    newAttribute = buildTangentAttribute(controllingFeatureId, jointTypeParameterId, existingAttribute);
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
                // jointType is the active dropdown when canBeTangent is false (the default).
                // jointTypeWithTangent is pre-initialized here so that the first time the editing
                // logic sets canBeTangent = true it has a defined starting value to show.
                jointType : ModifyJointType.BEND,
                jointTypeWithTangent : ModifyJointTypeWithTangent.BEND,
                jointStyle : SMJointStyle.EDGE,
                useDefaultRadius : true,
                useDefaultKFactor : true,
                canBeTangent : false,
                hasStyle : false
            }
        ]
    });


// ─── Editing logic ────────────────────────────────────────────────────────────

/**
 * Editing logic for sheetMetalModifyJoints. Called by Onshape whenever the user
 * changes any input in the feature dialog, and once when the dialog first opens.
 *
 * For each joint group, this function inspects the attribute angle of the selected joints:
 *   • A joint with zero/undefined angle is tangent-capable  → sets group.canBeTangent = true
 *     (causes the "Tangent" option to appear in the Joint type dropdown for that group)
 *   • A joint with non-zero angle supports butt styles      → sets group.hasStyle = true
 *     (causes the Joint style dropdown to appear for RIP groups)
 *
 * When canBeTangent transitions between false and true, the selected joint type value is
 * copied between group.jointType and group.jointTypeWithTangent so the user's choice is
 * preserved when the dropdown switches between its two-option and three-option variants.
 *
 * The isCreating parameter is required by the Onshape editing logic function protocol:
 * Onshape only calls an editing logic function during re-editing (not just creation) when
 * the function signature includes isCreating. The parameter is unused in the body because
 * the logic is identical at creation time and at edit time — but its presence in the
 * signature is what triggers the call during edit.
 */
export function sheetMetalModifyJointsEditLogic(context is Context, id is Id,
    oldDefinition is map, definition is map, isCreating is boolean,
    specifiedParameters is map, hiddenBodies is Query) returns map
{
    var updatedGroups = [];
    for (var group in definition.jointGroups)
    {
        var canBeTangent = false;
        var hasStyle = false;

        // Resolve selected entities to definition edges so we can read their attributes.
        // try (without silent) is used throughout this function because editing logic must
        // never throw — a throw here would break the feature dialog. Errors are still reported
        // to the console by the FeatureScript runtime; only the return value is suppressed to
        // undefined so the loop can continue gracefully. Validate output in the feature body
        // before shipping to production (test by checking the console for unexpected errors
        // when joints are partially selected or in unusual states).
        const definitionEdges = try(getSMDefinitionEntities(context, group.joints, EntityType.EDGE));
        if (definitionEdges != undefined)
        {
            for (var edgeEntity in definitionEdges)
            {
                // Read the current attribute to learn the joint angle.
                // Returns undefined when the edge has no joint attribute yet (partial selection).
                const existingAttribute = try(getJointAttribute(context, edgeEntity as Query));
                if (existingAttribute != undefined)
                {
                    // The angle is read directly from the stored SMAttribute map rather than
                    // from geometry because:
                    //   1. Reading an attribute is lightweight, appropriate for editing logic.
                    //   2. isEntityAppropriateForAttribute (which would call edgeAngle on the
                    //      geometry) is heavier and could be slow with many joints selected.
                    //   3. This mirrors sheetMetalJointEditLogic in the standard library, which
                    //      uses the same attribute.angle comparison for the hasStyle flag.
                    // Divide angle.value by the radian unit to get the dimensionless numeric
                    // magnitude in radians, then compare against the TOLERANCE.zeroAngle threshold
                    // (a small dimensionless number). This is the standard library pattern for
                    // "is this angle effectively zero?" used throughout sheetMetalUtils.fs.
                    if (existingAttribute.angle != undefined &&
                        existingAttribute.angle.value != undefined &&
                        abs(existingAttribute.angle.value / radian) > TOLERANCE.zeroAngle)
                    {
                        // Non-zero angle: butt joint direction styles are applicable
                        hasStyle = true;
                    }
                    else
                    {
                        // Zero or undefined angle: geometry is inherently tangent-capable
                        canBeTangent = true;
                    }
                }
            }
        }

        // Sync the joint type value between the two dropdown parameters when canBeTangent
        // changes, so the user's prior selection survives the switch.
        const previousCanBeTangent = group.canBeTangent;
        if (canBeTangent && !previousCanBeTangent)
        {
            // Transitioning to tangent-capable view: copy jointType → jointTypeWithTangent
            if (group.jointType == ModifyJointType.RIP)
                group.jointTypeWithTangent = ModifyJointTypeWithTangent.RIP;
            else
                group.jointTypeWithTangent = ModifyJointTypeWithTangent.BEND;
        }
        else if (!canBeTangent && previousCanBeTangent)
        {
            // Transitioning away from tangent-capable view: copy jointTypeWithTangent → jointType
            // TANGENT maps to BEND since it is no longer a valid option
            if (group.jointTypeWithTangent == ModifyJointTypeWithTangent.RIP)
                group.jointType = ModifyJointType.RIP;
            else
                group.jointType = ModifyJointType.BEND;
        }

        group.canBeTangent = canBeTangent;
        group.hasStyle = hasStyle;
        updatedGroups = append(updatedGroups, group);
    }
    definition.jointGroups = updatedGroups;
    return definition;
}


// ─── Private helper functions ────────────────────────────────────────────────

/**
 * Translate the effective joint type from a group definition map into the corresponding
 * SMJointType used internally by the feature body and attribute builders.
 *
 * When group.canBeTangent is true, the active dropdown is group.jointTypeWithTangent
 * (ModifyJointTypeWithTangent); otherwise it is group.jointType (ModifyJointType).
 * Both are mapped to their SMJointType equivalents so the body dispatch is uniform.
 *
 * Required group fields (always present via the precondition default values and editing logic):
 *   canBeTangent         - boolean gate; selects which dropdown parameter is active
 *   jointType            - ModifyJointType; active when canBeTangent is false
 *   jointTypeWithTangent - ModifyJointTypeWithTangent; active when canBeTangent is true
 *
 * The caller (feature body) guarantees these fields exist: they are declared in the
 * precondition and initialized in the default values map, so the function never
 * needs to guard against missing fields.
 * Inputs:
 *   group - A single entry from definition.jointGroups
 * Output: SMJointType (BEND, RIP, or TANGENT)
 */
function getGroupJointType(group is map) returns SMJointType
{
    if (group.canBeTangent)
    {
        if (group.jointTypeWithTangent == ModifyJointTypeWithTangent.RIP)
            return SMJointType.RIP;
        if (group.jointTypeWithTangent == ModifyJointTypeWithTangent.TANGENT)
            return SMJointType.TANGENT;
        return SMJointType.BEND;
    }
    if (group.jointType == ModifyJointType.RIP)
        return SMJointType.RIP;
    return SMJointType.BEND;
}

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
 *   jointTypeParameterId - Name of the precondition parameter that holds the joint type ("jointType"
 *                          or "jointTypeWithTangent"). Used as parameterIdInFeature so the sheet
 *                          metal table opens the correct dropdown when editing.
 *   jointEdge            - Query for the definition edge being modified
 *   existingAttribute    - Current SMAttribute on this edge
 *   radius               - Bend radius to apply (ValueWithUnits)
 *   useDefaultRadius     - True when radius comes from the model default
 *   kFactor              - K-factor to apply (number)
 *   useDefaultKFactor    - True when kFactor comes from the model default
 * Output: SMAttribute with BEND joint type
 */
function buildEdgeBendAttribute(context is Context, id is Id, controllingFeatureId is string,
    jointTypeParameterId is string, jointEdge is Query,
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
            "parameterIdInFeature" : jointTypeParameterId,
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
 *   jointTypeParameterId - Name of the active joint type precondition parameter ("jointType" or
 *                          "jointTypeWithTangent"); used as parameterIdInFeature for table editing.
 *   existingAttribute    - Current SMAttribute on this edge
 *   jointStyle           - SMJointStyle value (EDGE, BUTT, BUTT2)
 * Output: SMAttribute with RIP joint type
 */
function buildRipAttribute(controllingFeatureId is string, jointTypeParameterId is string,
    existingAttribute is SMAttribute, jointStyle) returns SMAttribute
{
    var ripAttribute = makeSMJointAttribute(existingAttribute.attributeId);
    ripAttribute.jointType = {
            "value" : SMJointType.RIP,
            "controllingFeatureId" : controllingFeatureId,
            "parameterIdInFeature" : jointTypeParameterId,
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
 * Only called when the joint's geometry is inherently tangent-capable (zero angle),
 * which is validated by isEntityAppropriateForAttribute before the attribute is committed.
 * Mirrors the logic in sheetMetalJoint.fs createNewTangentAttribute.
 *
 * Inputs:
 *   controllingFeatureId - String attribute id of the top-level feature (for sheet metal table linkage)
 *   jointTypeParameterId - Name of the active joint type precondition parameter; always
 *                          "jointTypeWithTangent" since TANGENT is only selectable from that dropdown.
 *   existingAttribute    - Current SMAttribute on this edge
 * Output: SMAttribute with TANGENT joint type
 */
function buildTangentAttribute(controllingFeatureId is string, jointTypeParameterId is string,
    existingAttribute is SMAttribute) returns SMAttribute
{
    var tangentAttribute = makeSMJointAttribute(existingAttribute.attributeId);
    tangentAttribute.jointType = {
            "value" : SMJointType.TANGENT,
            "controllingFeatureId" : controllingFeatureId,
            "parameterIdInFeature" : jointTypeParameterId,
            "canBeEdited" : true
        };
    return tangentAttribute;
}
