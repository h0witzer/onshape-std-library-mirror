FeatureScript 2837;
// This module is part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab for the license text.
// Copyright (c) 2013-Present PTC Inc.

// Imports used in interface
export import(path : "onshape/std/query.fs", version : "2837.0");

// Imports used internally
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/formedUtils.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/registerSheetMetalBooleanTools.fs", version : "2837.0");
import(path : "onshape/std/registerSheetMetalFormedTools.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");

/**
 * Performs a boolean union operation between solid tool bodies and sheet metal target bodies.
 * This feature uses the same mechanism as the sheet metal formed feature to merge non-sheet-metal
 * geometry with sheet metal parts without triggering sheet metal validation errors.
 * 
 * The union tools are treated as "positive bodies" in the formed tool mechanism, which adds
 * material to the sheet metal parts, effectively performing a union operation.
 * 
 * Unlike standard booleans, this feature:
 * - Uses the formed tool registration mechanism to bypass sheet metal validation
 * - Marks the union tools as positive-only formed bodies (additive)
 * - Updates sheet metal geometry to apply the union operations
 * - Maintains the sheet metal model validity
 */
annotation { "Feature Type Name" : "Sheet Metal Boolean Union" }
export const sheetMetalBooleanUnion = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Union tools",
                     "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES,
                     "UIHint" : UIHint.ALLOW_QUERY_ORDER }
        definition.unionTools is Query;

        annotation { "Name" : "Sheet metal targets",
                     "Filter" : EntityType.BODY && ActiveSheetMetal.YES && ModifiableEntityOnly.YES }
        definition.sheetMetalTargets is Query;

        annotation { "Name" : "Keep tools", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.keepTools is boolean;
    }
    {
        // Validate inputs
        if (isQueryEmpty(context, definition.unionTools))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["unionTools"]);
        }

        if (isQueryEmpty(context, definition.sheetMetalTargets))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["sheetMetalTargets"]);
        }

        // Verify tools are solid bodies
        if (!isQueryEmpty(context, qBodyType(definition.unionTools, BodyType.SHEET)))
        {
            throw regenError(ErrorStringEnum.BOOLEAN_TOOL_INPUTS_NOT_SOLID, ["unionTools"]);
        }

        // Verify targets are sheet metal
        const parts = partitionSheetMetalParts(context, definition.sheetMetalTargets);
        if (size(parts.sheetMetalPartsMap) == 0)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_NO_ACTIVE_PARTS_FOUND, ["sheetMetalTargets"]);
        }

        // Mark all union tools as positive-only form bodies (additive operation)
        setFormAttribute(context, definition.unionTools, FORM_BODY_POSITIVE_PART);

        // Build definition face to formed bodies map for registration
        var definitionFaceToFormedBodies = buildDefinitionFaceMapping(context, definition.unionTools, definition.sheetMetalTargets);

        if (definitionFaceToFormedBodies == {})
        {
            reportFeatureInfo(context, id, ErrorStringEnum.BOOLEAN_UNION_NO_OP);
            if (!definition.keepTools)
            {
                opDeleteBodies(context, id + "deleteTools", { "entities" : definition.unionTools });
            }
            return;
        }

        // Register the union tools as formed tools with sheet metal walls
        const wallToFormedToolBodyIds = callSubfeatureAndProcessStatus(id, registerSheetMetalFormedTools, context, id + "registerUnion", {
            "definitionFaceToFormedBodies" : definitionFaceToFormedBodies,
            "doUpdateSMGeometry" : true
        });

        // Clean up: delete tools if not keeping them
        if (!definition.keepTools)
        {
            opDeleteBodies(context, id + "deleteTools", {
                "entities" : definition.unionTools
            });
        }
    }, { keepTools : false });

/**
 * @internal
 * Build mapping from sheet metal definition faces to union tool bodies.
 * This identifies which walls of the sheet metal part each union tool should be applied to.
 */
function buildDefinitionFaceMapping(context is Context, unionTools is Query, sheetMetalTargets is Query) returns map
{
    var definitionFaceToFormedBodies = {};
    
    // Perform collision detection between union tools and sheet metal parts (using 3D folded bodies)
    const collisions = evCollision(context, {
        "tools" : unionTools,
        "targets" : qSheetMetalFlatFilter(sheetMetalTargets, SMFlatType.NO)
    });
    
    if (size(collisions) == 0)
    {
        return {};
    }
    
    // Build map of definition faces to tools that collide with them
    const targetToDefinitionEntity = makeDefinitionEntityCache(context);
    const definitionEntityToWallIsPlanarCache = makeIsEntityPlanarCache(context);
    
    for (var collision in collisions)
    {
        if (!isIntersectingClashType(collision['type']))
        {
            continue;
        }
        
        const definitionEntity = targetToDefinitionEntity(collision.target);
        if (definitionEntity == qNothing())
        {
            // No definition entity found (e.g., hole walls), skip
            continue;
        }
        
        const wallIsPlanar = definitionEntityToWallIsPlanarCache(definitionEntity);
        if (!wallIsPlanar)
        {
            // Do not allow union with non-planar walls (side walls, rolled walls, etc.)
            continue;
        }
        
        // Add this tool to the list of tools for this definition face
        definitionFaceToFormedBodies = insertIntoMapOfArrays(definitionFaceToFormedBodies, definitionEntity, collision.toolBody);
    }
    
    return definitionFaceToFormedBodies;
}


