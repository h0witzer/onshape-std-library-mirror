FeatureScript 2837;

// Under development, not for general use
// This is a tester script that uses the sheet metal form pathway to test adding geometry to sheet metal
// The form feature allows adding both positive and negative geometry to sheet metal parts and corresponding
// sketches to the flat pattern, which can violate normal sheet metal rules.

import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/formedUtils.fs", version : "2837.0");
import(path : "onshape/std/registerSheetMetalFormedTools.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2837.0");

/**
 * Defines the type of form operation to test
 * @value POSITIVE : Add positive (raised) geometry to sheet metal
 * @value NEGATIVE : Add negative (depressed) geometry to sheet metal
 * @value BOTH : Add both positive and negative geometry
 */
export enum SheetMetalFormTestType
{
    annotation { "Name" : "Positive (Raised)" }
    POSITIVE,
    annotation { "Name" : "Negative (Depressed)" }
    NEGATIVE,
    annotation { "Name" : "Both" }
    BOTH
}

/**
 * Sheet Metal Form Tester Feature
 * 
 * This feature uses the sheet metal form pathway to test adding geometry to sheet metal parts.
 * Unlike the boolean tester which uses the hole feature pathway (subtraction only), the form
 * feature pathway allows both additive and subtractive geometry, plus sketch representations
 * in the flat pattern.
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
        annotation { "Name" : "Form type", "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.formType is SheetMetalFormTestType;

        annotation { "Name" : "Tool body (positive part)",
                     "Filter" : EntityType.BODY && BodyType.SOLID && AllowMeshGeometry.YES,
                     "MaxNumberOfPicks" : 1 }
        definition.positiveBody is Query;

        annotation { "Name" : "Tool body (negative part)",
                     "Filter" : EntityType.BODY && BodyType.SOLID && AllowMeshGeometry.YES,
                     "MaxNumberOfPicks" : 1 }
        definition.negativeBody is Query;

        annotation { "Name" : "Sketch for flat view (optional)",
                     "Filter" : EntityType.BODY && BodyType.WIRE,
                     "MaxNumberOfPicks" : 1 }
        definition.sketchBody is Query;

        annotation { "Name" : "Target sheet metal face",
                     "Filter" : GeometryType.PLANE && ActiveSheetMetal.YES && SheetMetalDefinitionEntityType.FACE && ModifiableEntityOnly.YES,
                     "MaxNumberOfPicks" : 1 }
        definition.targetFace is Query;

        annotation { "Name" : "Update geometry immediately",
                     "Default" : true }
        definition.updateGeometry is boolean;

        annotation { "Name" : "Keep tool bodies",
                     "Default" : false }
        definition.keepTools is boolean;
    }
    {
        // Verify the target face is selected
        if (isQueryEmpty(context, definition.targetFace))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["targetFace"]);
        }

        // Check if the target is actually sheet metal
        const isSheetMetalTarget = isActiveSheetMetalPart(context, definition.targetFace);
        if (!isSheetMetalTarget)
        {
            throw regenError("Selected target is not a sheet metal part. This feature only works with sheet metal.");
        }

        // Get the definition entity for the target face
        const definitionEntities = getSMDefinitionEntities(context, definition.targetFace);
        if (size(definitionEntities) != 1)
        {
            throw regenError("Could not get definition entity for target face.");
        }
        const definitionFace = definitionEntities[0];

        // Mark bodies with form attributes based on form type
        const formsToRegister = [];
        
        if (definition.formType == SheetMetalFormTestType.POSITIVE || definition.formType == SheetMetalFormTestType.BOTH)
        {
            if (!isQueryEmpty(context, definition.positiveBody))
            {
                setFormAttribute(context, definition.positiveBody, FORM_BODY_POSITIVE_PART);
                formsToRegister = append(formsToRegister, definition.positiveBody);
            }
        }

        if (definition.formType == SheetMetalFormTestType.NEGATIVE || definition.formType == SheetMetalFormTestType.BOTH)
        {
            if (!isQueryEmpty(context, definition.negativeBody))
            {
                setFormAttribute(context, definition.negativeBody, FORM_BODY_NEGATIVE_PART);
                formsToRegister = append(formsToRegister, definition.negativeBody);
            }
        }

        // Mark sketch body if provided
        if (!isQueryEmpty(context, definition.sketchBody))
        {
            setFormAttribute(context, definition.sketchBody, FORM_BODY_SKETCH_FOR_FLAT_VIEW);
            formsToRegister = append(formsToRegister, definition.sketchBody);
        }

        if (formsToRegister == [])
        {
            throw regenError("At least one tool body must be provided based on the selected form type.");
        }

        // Prepare the definition face to formed bodies map
        const formQuery = qUnion(formsToRegister);
        const definitionFaceToFormedBodies = { definitionFace : [formQuery] };

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
                const message = "Successfully registered form tool(s) to sheet metal wall(s). " ~ geometryStatus;
                reportFeatureInfo(context, id, message);

                // If keepTools is false, delete the tool bodies (standard behavior)
                if (!definition.keepTools)
                {
                    const bodiesToDelete = [];
                    if (!isQueryEmpty(context, definition.positiveBody))
                    {
                        bodiesToDelete = append(bodiesToDelete, definition.positiveBody);
                    }
                    if (!isQueryEmpty(context, definition.negativeBody))
                    {
                        bodiesToDelete = append(bodiesToDelete, definition.negativeBody);
                    }
                    if (!isQueryEmpty(context, definition.sketchBody))
                    {
                        bodiesToDelete = append(bodiesToDelete, definition.sketchBody);
                    }

                    if (bodiesToDelete != [])
                    {
                        opDeleteBodies(context, id + "deleteTools", {
                            "entities" : qUnion(bodiesToDelete)
                        });
                    }
                }
            }
            else
            {
                reportFeatureWarning(context, id,
                    "No form tools were registered. Tools may not intersect planar walls correctly.");
            }
        }
        catch (error)
        {
            throw regenError("Failed to register sheet metal form tools: " ~ error);
        }
    });
