FeatureScript ✨;
// Standalone debug feature stripped down from sheetMetalStart
// Purpose: Test basic sheet metal thicken to understand surface hiding and context naming

import(path : "onshape/std/feature.fs", version : "✨");
import(path : "onshape/std/query.fs", version : "✨");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "✨");
import(path : "onshape/std/sheetMetalUtils.fs", version : "✨");
import(path : "onshape/std/geomOperations.fs", version : "✨");
import(path : "onshape/std/error.fs", version : "✨");
import(path : "onshape/std/evaluate.fs", version : "✨");
import(path : "onshape/std/valueBounds.fs", version : "✨");
import(path : "onshape/std/smreliefstyle.gen.fs", version : "✨");

annotation { "Feature Type Name" : "Debug SM Thicken" }
export const debugSheetMetalThicken = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face to thicken", "Filter" : EntityType.FACE }
        definition.faceToThicken is Query;

        annotation { "Name" : "Thickness" }
        isLength(definition.thickness, SM_THICKNESS_BOUNDS);

        annotation { "Name" : "Bend Radius" }
        isLength(definition.bendRadius, SM_BEND_RADIUS_BOUNDS);

        annotation { "Name" : "K Factor" }
        isReal(definition.kFactor, K_FACTOR_BOUNDS);

        annotation { "Name" : "Minimal Clearance" }
        isLength(definition.minimalClearance, SM_MINIMAL_CLEARANCE_BOUNDS);

        annotation { "Name" : "Delete original body" }
        definition.deleteOriginal is boolean;
    }
    {
        // This is the EXACT pattern from sheetMetalStart's convertFaces → annotateConvertedFaces
        
        // Step 1: Extract surface from face (convertFaces pattern)
        var surfaceId = id + "extractSurface";
        
        try
        {
            opExtractSurface(context, surfaceId, {
                "faces" : definition.faceToThicken,
                "offset" : 0 * meter
            });
        }
        catch
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_THICKEN);
        }
        
        // Step 2: Annotate with sheet metal attributes (annotateConvertedFaces pattern)
        // KEY: Use id, NOT surfaceId for annotation and queries
        try
        {
            var thicknessDirection = SMThicknessDirection.BACK;
            annotateSmSurfaceBodies(context, id, {
                "surfaceBodies" : qCreatedBy(id, EntityType.BODY),  // Uses id, not surfaceId!
                "bendEdgesAndFaces" : qNothing(),
                "specialRadiiBends" : [],
                "defaultRadius" : definition.bendRadius,
                "controlsThickness" : true,
                "thickness" : definition.thickness,
                "thicknessDirection" : thicknessDirection,
                "minimalClearance" : definition.minimalClearance,
                "kFactor" : definition.kFactor,
                "flipDirectionUp" : false,
                "defaultTwoCornerStyle" : SMReliefStyle.SIMPLE,
                "defaultThreeCornerStyle" : SMReliefStyle.SIMPLE,
                "defaultBendReliefStyle" : SMReliefStyle.OBROUND,
                "defaultCornerReliefScale" : 1.5,
                "defaultRoundReliefDiameter" : 0 * meter,
                "defaultSquareReliefWidth" : 0 * meter,
                "defaultBendReliefDepthScale" : 2.0,
                "defaultBendReliefScale" : 1.0625,
                "bendCalculationType" : SMBendCalculationType.K_FACTOR
            }, 0);
            
            if (getFeatureError(context, id) != undefined)
            {
                return;
            }
        }
        catch (thrownError)
        {
            if (thrownError is map && thrownError.message is ErrorStringEnum)
            {
                throw thrownError;
            }
            throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
        }
        
        // Step 3: Delete original body if requested (before finalization, as in sheetMetalStart)
        if (definition.deleteOriginal)
        {
            // Get the owner body of the face
            var faceOwner = qOwnerBody(definition.faceToThicken);
            
            try
            {
                opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : faceOwner
                });
            }
            catch
            {
                throw regenError(ErrorStringEnum.REGEN_ERROR);
            }
        }
        
        // Step 4: Finalize sheet metal geometry (finalizeSheetMetalGeometry pattern)
        // KEY: Use id, NOT surfaceId for queries
        try
        {
            updateSheetMetalGeometry(context, id, {
                "entities" : qUnion([qCreatedBy(id, EntityType.FACE), qCreatedBy(id, EntityType.EDGE)])
            });
        }
        catch (error)
        {
            var messageAsEnum = try silent(error.message as ErrorStringEnum);
            if (messageAsEnum == ErrorStringEnum.BOOLEAN_INVALID)
            {
                throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
            }
            else if (messageAsEnum == ErrorStringEnum.BAD_GEOMETRY ||
                    messageAsEnum == ErrorStringEnum.THICKEN_FAILED)
            {
                throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_THICKEN);
            }
            else
            {
                throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
            }
        }
    });
