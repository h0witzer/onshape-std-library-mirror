FeatureScript 2837;

// Under development, not for general use
// This is a tester script that leverages the special sheet metal boolean wiring used by the hole feature
// to allow boolean operations on sheet metal that violate normal building rules.
// The hole feature's countersink and counterbore operations are the only case in the engine allowed
// to violate normal sheet metal generation rules. This feature exposes that functionality for testing.
// I'm not gonna ask you why you need this feature
// And whatever the Onshape developers ask you, you didn't get this feature from me. It fell off a truck. Capisce?

import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2837.0");
import(path : "onshape/std/registerSheetMetalBooleanTools.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");


/**
 * Sheet Metal Boolean Tester Feature
 *
 * This feature exposes the special sheet metal boolean wiring used by the hole feature's
 * countersink and counterbore operations. It allows testing boolean operations on sheet metal
 * that would normally violate sheet metal generation rules.
 *
 * The hole feature is the only case in the Onshape engine allowed to violate normal sheet metal
 * rules, using registerSheetMetalBooleanTools to perform the operations. This feature exposes
 * that functionality for experimentation and testing.
 */
annotation { "Feature Type Name" : "Black Market Boolean",
        "Feature Type Description" : "Performs illegal subtractive boolean operations on sheet metal using the special wiring from the hole feature" }
export const blackMarketSheetMetalBoolean = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Tool body",
                    "Filter" : EntityType.BODY && BodyType.SOLID && AllowMeshGeometry.YES && ActiveSheetMetal.NO }
        definition.toolBody is Query;

        annotation { "Name" : "Target sheet metal part",
                    "Filter" : EntityType.BODY && ModifiableEntityOnly.YES && ActiveSheetMetal.YES,
                    "MaxNumberOfPicks" : 1 }
        definition.targetSheetMetal is Query;

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
            throw regenError("Selected target is not a sheet metal part. This feature only works with sheet metal. How did you even select that by the way?");
        }


        // Use the same function that the hole feature uses for countersink/counterbore operations
        // This is the special wiring that allows violations of normal sheet metal rules
        try
        {
            // Call registerSheetMetalBooleanTools - this is the key function that enables
            // the special sheet metal boolean operations used by hole feature countersinks/counterbores
            const wallToCuttingToolBodyIds = registerSheetMetalBooleanTools(context, id, {
                        "targets" : definition.targetSheetMetal,
                        "subtractiveTools" : definition.toolBody,
                        "doUpdateSMGeometry" : true
                    });

            // The function returns a map of walls to cutting tool body IDs
            // If the map is not empty, tools were successfully registered
            if (wallToCuttingToolBodyIds != undefined && size(wallToCuttingToolBodyIds) > 0)
            {
                // Count the total number of tools across all walls
                var totalToolCount = 0;
                for (var toolIdSet in values(wallToCuttingToolBodyIds))
                {
                    totalToolCount += size(toolIdSet);
                }
                
                const wallCount = size(wallToCuttingToolBodyIds);
                const message = "Successfully registered " ~ totalToolCount ~
                    " cutting tool(s) to " ~ wallCount ~ " sheet metal wall(s), geometry updated.";
                reportFeatureInfo(context, id, message);

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

    });
