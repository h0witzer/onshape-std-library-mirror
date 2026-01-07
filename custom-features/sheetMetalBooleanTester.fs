FeatureScript 2837;

// Under development, not for general use
// This is a tester script that leverages the special sheet metal boolean wiring used by the hole feature
// to allow boolean operations on sheet metal that violate normal building rules.
// The hole feature's countersink and counterbore operations are the only case in the engine allowed
// to violate normal sheet metal generation rules. This feature exposes that functionality for testing.

import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2837.0");
import(path : "registerSheetMetalBooleanToolsModified.fs", version : "");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");

/**
 * Defines the type of boolean operation to test
 * @value SUBTRACTION : Subtract tool body from sheet metal target (like countersink/counterbore)
 * @value UNION : Union tool body with sheet metal target (experimental)
 */
export enum SheetMetalBooleanTestType
{
    annotation { "Name" : "Subtraction" }
    SUBTRACTION,
    annotation { "Name" : "Union (Experimental)" }
    UNION
}

/**
 * Sheet Metal Boolean Tester Feature
 * 
 * This feature exposes the special sheet metal boolean wiring used by the hole feature's
 * countersink and counterbore operations. It allows testing boolean operations on sheet metal
 * that would normally violate sheet metal generation rules.
 * 
 * The hole feature is the only case in the Onshape engine allowed to violate normal sheet metal
 * rules, using registerSheetMetalBooleanTools to perform the operations. This tester exposes
 * that functionality for experimentation and testing.
 */
annotation { "Feature Type Name" : "Sheet Metal Boolean Tester",
             "Feature Type Description" : "Test boolean operations on sheet metal using the special wiring from the hole feature" }
export const sheetMetalBooleanTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Operation type", "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.operationType is SheetMetalBooleanTestType;

        annotation { "Name" : "Tool body",
                     "Filter" : EntityType.BODY && BodyType.SOLID && AllowMeshGeometry.YES,
                     "MaxNumberOfPicks" : 1 }
        definition.toolBody is Query;

        annotation { "Name" : "Target sheet metal part",
                     "Filter" : EntityType.BODY && ModifiableEntityOnly.YES,
                     "MaxNumberOfPicks" : 1 }
        definition.targetSheetMetal is Query;

        annotation { "Name" : "Update geometry immediately",
                     "Default" : true }
        definition.updateGeometry is boolean;

        annotation { "Name" : "Keep tool body",
                     "Default" : false }
        definition.keepTool is boolean;
    }
    {
        // Verify the tool body is a solid
        if (isQueryEmpty(context, definition.toolBody))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["toolBody"]);
        }

        // Verify the target is sheet metal
        if (isQueryEmpty(context, definition.targetSheetMetal))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["targetSheetMetal"]);
        }

        // Check if the target is actually sheet metal
        const isSheetMetalTarget = isActiveSheetMetalPart(context, definition.targetSheetMetal);
        if (!isSheetMetalTarget)
        {
            throw regenError("Selected target is not a sheet metal part. This feature only works with sheet metal.");
        }

        if (definition.operationType == SheetMetalBooleanTestType.SUBTRACTION)
        {
            // Use the same function that the hole feature uses for countersink/counterbore operations
            // This is the special wiring that allows violations of normal sheet metal rules
            try
            {
                // Call registerSheetMetalBooleanTools - this is the key function that enables
                // the special sheet metal boolean operations used by hole feature countersinks/counterbores
                const wallToCuttingToolBodyIds = registerSheetMetalBooleanTools(context, id, {
                    "targets" : definition.targetSheetMetal,
                    "subtractiveTools" : definition.toolBody,
                    "doUpdateSMGeometry" : definition.updateGeometry
                });

                // The function returns a map of walls to cutting tool body IDs
                // If the map is not empty, tools were successfully registered
                if (wallToCuttingToolBodyIds != undefined && size(wallToCuttingToolBodyIds) > 0)
                {
                    const geometryStatus = definition.updateGeometry ? "Geometry updated." : "Geometry update deferred.";
                    const message = "Successfully registered " ~ size(wallToCuttingToolBodyIds) ~ 
                                    " cutting tool(s) to sheet metal wall(s). " ~ geometryStatus;
                    reportFeatureInfo(context, id, message);
                    
                    // If keepTool is false, delete the tool body (standard boolean behavior)
                    // Note: registerSheetMetalBooleanTools already copies the tool, so we're deleting the original
                    if (!definition.keepTool)
                    {
                        opDeleteBodies(context, id + "deleteTool", {
                            "entities" : definition.toolBody
                        });
                    }
                }
                else
                {
                    reportFeatureWarning(context, id, 
                        "No tools were registered. Tool may not intersect planar walls, or may intersect non-planar features.");
                }
            }
            catch (error)
            {
                throw regenError("Failed to register sheet metal boolean tools: " ~ error);
            }
        }
        else if (definition.operationType == SheetMetalBooleanTestType.UNION)
        {
            // Union operations using modified registerSheetMetalBooleanTools
            // This is experimental - testing additive operations on sheet metal
            try
            {
                // Call registerSheetMetalBooleanTools with additiveTools parameter
                const wallToAddingToolBodyIds = registerSheetMetalBooleanTools(context, id, {
                    "targets" : definition.targetSheetMetal,
                    "additiveTools" : definition.toolBody,
                    "doUpdateSMGeometry" : definition.updateGeometry
                });

                // The function returns a map of walls to adding tool body IDs
                // If the map is not empty, tools were successfully registered
                if (wallToAddingToolBodyIds != undefined && size(wallToAddingToolBodyIds) > 0)
                {
                    const geometryStatus = definition.updateGeometry ? "Geometry updated." : "Geometry update deferred.";
                    const message = "Successfully registered " ~ size(wallToAddingToolBodyIds) ~ 
                                    " additive tool(s) to sheet metal wall(s). " ~ geometryStatus;
                    reportFeatureInfo(context, id, message);
                    
                    // If keepTool is false, delete the tool body (standard boolean behavior)
                    // Note: registerSheetMetalBooleanTools already copies the tool, so we're deleting the original
                    if (!definition.keepTool)
                    {
                        opDeleteBodies(context, id + "deleteTool", {
                            "entities" : definition.toolBody
                        });
                    }
                }
                else
                {
                    reportFeatureWarning(context, id, 
                        "No tools were registered. Tool may not intersect planar walls, or may intersect non-planar features.");
                }
            }
            catch (error)
            {
                throw regenError("Failed to register sheet metal boolean tools for union: " ~ error);
            }
        }
    });
