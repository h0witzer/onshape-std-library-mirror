FeatureScript 2837;

// Under development, not for general use
// This is a tester script that uses the sheet metal form pathway to test adding geometry to sheet metal
// The form feature allows adding both positive and negative geometry to sheet metal parts and corresponding
// sketches to the flat pattern, which can violate normal sheet metal rules.

import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/formedUtils.fs", version : "2837.0");
import(path : "965f408b9b722119505011e4", version : "5e409d85d3c8b389c4323b37");//registerSheetMetalFormedTools.fs modified
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");

/**
 * Defines the type of boolean operation to test
 * @value SUBTRACTION : Subtract tool body from sheet metal target (like countersink/counterbore)
 * @value UNION : Union tool body with sheet metal target (experimental)
 */
export enum SheetMetalFormTestType
{
    annotation { "Name" : "Subtraction" }
    SUBTRACTION,
    annotation { "Name" : "Union (Experimental)" }
    UNION
}

/**
 * Sheet Metal Form Tester Feature
 * 
 * This feature uses the sheet metal form pathway to test adding geometry to sheet metal parts.
 * Unlike the boolean tester which uses the hole feature pathway (subtraction only), the form
 * feature pathway allows both additive and subtractive geometry.
 * 
 * The form feature uses registerSheetMetalFormedTools which stores tools in formedToolBodyIds
 * attribute. The native updateSheetMetalGeometry function processes this attribute to apply
 * the geometry changes to both the 3D folded body and the flat pattern.
 */
annotation { "Feature Type Name" : "Sheet Metal Form Tester",
             "Feature Type Description" : "Test adding geometry to sheet metal using the form feature pathway" }
export const sheetMetalFormTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Operation type", "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.operationType is SheetMetalFormTestType;

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
        // Verify the tool body is selected
        if (isQueryEmpty(context, definition.toolBody))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["toolBody"]);
        }

        // Verify the target is selected
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

        // Get all definition faces from the target sheet metal
        const targetFaces = qOwnedByBody(definition.targetSheetMetal, EntityType.FACE);
        const definitionFaces = getSMDefinitionEntities(context, targetFaces);
        
        if (definitionFaces == [])
        {
            throw regenError("Could not get definition entities for target sheet metal.");
        }

        // Mark the tool body with the appropriate form attribute based on operation type
        // For SUBTRACTION, use negative part (depressed geometry)
        // For UNION, use positive part (raised geometry)
        if (definition.operationType == SheetMetalFormTestType.UNION)
        {
            setFormAttribute(context, definition.toolBody, FORM_BODY_POSITIVE_PART);
        }
        else // SUBTRACTION
        {
            setFormAttribute(context, definition.toolBody, FORM_BODY_NEGATIVE_PART);
        }

        // EXPERIMENTAL: Mark the solid tool body with FORM_BODY_SKETCH_FOR_FLAT_VIEW
        // This serves dual purposes:
        // 1. Tests if solids can be imported to the flat pattern view
        // 2. Signals to registerSheetMetalFormedTools to skip the footprint validation
        //    (allowing the tool to be positioned wherever the user wants)
        setFormAttribute(context, definition.toolBody, FORM_BODY_SKETCH_FOR_FLAT_VIEW);

        // Prepare the definition face to formed bodies map
        // We need to map each definition face that the tool intersects
        var definitionFaceToFormedBodies = {};
        for (var definitionFace in definitionFaces)
        {
            definitionFaceToFormedBodies[(definitionFace)] = [definition.toolBody];
        }

        try
        {
            // Call registerSheetMetalFormedTools to register the form tools
            const wallToFormedBodyIds = registerSheetMetalFormedTools(context, id, {
                "definitionFaceToFormedBodies" : definitionFaceToFormedBodies,
                "doUpdateSMGeometry" : definition.updateGeometry
            });

            // Report success
            if (wallToFormedBodyIds != undefined && size(wallToFormedBodyIds) > 0)
            {
                const geometryStatus = definition.updateGeometry ? "Geometry updated." : "Geometry update deferred.";
                const operationType = definition.operationType == SheetMetalFormTestType.UNION ? "union" : "subtraction";
                const message = "Successfully registered " ~ operationType ~ " form tool to sheet metal wall(s). " ~ geometryStatus;
                reportFeatureInfo(context, id, message);

                // If keepTool is false, delete the tool body (standard behavior)
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
                    "No form tools were registered. Tool may not intersect planar walls correctly, or may intersect non-planar features.");
            }
        }
        catch (error)
        {
            throw regenError("Failed to register sheet metal form tools: " ~ error);
        }
    });
