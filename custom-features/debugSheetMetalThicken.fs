FeatureScript ✨; /* Automatically generated version */
// This module is STRIPPED DOWN from sheetMetalStart.fs
// Only THICKEN functionality remains for debugging purposes
// Original file: sheet MetalStart.fs (1319 lines) → debugSheetMetalThicken.fs (~150 lines)

export import(path : "onshape/std/extrudeCommon.fs", version : "✨");
export import(path : "onshape/std/query.fs", version : "✨");

import(path : "onshape/std/attributes.fs", version : "✨");
import(path : "onshape/std/containers.fs", version : "✨");
import(path : "onshape/std/error.fs", version : "✨");
import(path : "onshape/std/evaluate.fs", version : "✨");
import(path : "onshape/std/feature.fs", version : "✨");
import(path : "onshape/std/geomOperations.fs", version : "✨");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "✨");
import(path : "onshape/std/sheetMetalUtils.fs", version : "✨");
import(path : "onshape/std/smreliefstyle.gen.fs", version : "✨");
import(path : "onshape/std/surfaceGeometry.fs", version : "✨");
import(path : "onshape/std/topologyUtils.fs", version : "✨");
import(path : "onshape/std/valueBounds.fs", version : "✨");

// STRIPPED: Removed SMProcessType, SMCornerStrategyType, SMBendStrategyType enums
// STRIPPED: Removed CORNER_RELIEF_SCALE_BOUNDS, BEND_RELIEF_DEPTH_SCALE_BOUNDS, BEND_RELIEF_WIDTH_SCALE_BOUNDS
// STRIPPED: Removed FLIP_DIRECTION_UP_MANIPULATOR_NAME
// STRIPPED: Removed sheetMetalModelParameters predicate (using simplified version)

/**
 * STRIPPED DOWN DEBUG VERSION - Thicken faces to sheet metal
 * Original: sheetMetalStart with CONVERT, EXTRUDE, and THICKEN options
 * Stripped: Only THICKEN remains
 */
annotation { "Feature Type Name" : "Debug SM Thicken (Stripped)" }
export const debugSheetMetalThicken = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // STRIPPED: Removed initEntities, process selection, CONVERT and EXTRUDE options
        // KEPT: Only THICKEN preconditions from original

        annotation { "Name" : "Faces or sketch regions to thicken",
                    "Filter" : ConstructionObject.NO && (GeometryType.PLANE || GeometryType.CYLINDER || GeometryType.EXTRUDED || GeometryType.CONE) }
        definition.regions is Query;

        annotation { "Name" : "Tangent propagation", "Default" : false }
        definition.tangentPropagation is boolean;

        annotation { "Name" : "Edges or cylinders to bend",
                     "Filter" : ((EntityType.EDGE && EdgeTopology.TWO_SIDED && GeometryType.LINE) ||
                                 (EntityType.FACE && GeometryType.CYLINDER)) && SketchObject.NO }
        definition.bends is Query;

        annotation { "Name" : "Clearance from input" }
        isLength(definition.clearance, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation { "Name" : "Include bends", "Description" : "Check to include the clearance for bends" }
        definition.bendsIncluded is boolean;

        // SIMPLIFIED: Minimal sheet metal parameters (from original sheetMetalModelParameters)
        annotation { "Name" : "Thickness" }
        isLength(definition.thickness, SM_THICKNESS_BOUNDS);

        annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.oppositeDirection is boolean;

        annotation { "Name" : "Bend radius" }
        isLength(definition.radius, SM_BEND_RADIUS_BOUNDS);

        annotation { "Name" : "K Factor" }
        isReal(definition.kFactor, K_FACTOR_BOUNDS);

        annotation { "Name" : "Minimal gap" }
        isLength(definition.minimalClearance, SM_MINIMAL_CLEARANCE_BOUNDS);
        
        annotation { "Name" : "Delete original body" }
        definition.deleteOriginal is boolean;
    }
    {
        // STRIPPED: Removed verifyNoMeshSheetMetalStart
        // STRIPPED: Removed convertExistingPart and extrudeSheetMetal branches
        // KEPT: Only thickenToSheetMetal path from original

        definition.supportRolled = true; // Simplified from version check
        
        // Call the EXACT thickenToSheetMetal function from original (copied below)
        thickenToSheetMetal(context, id, definition);
        
        // STRIPPED: Removed addFlipDirectionUpManipulator
    });

// ===== COPIED FROM ORIGINAL sheetMetalStart.fs =====
// This is thickenToSheetMetal and its helper functions, UNCHANGED

function thickenToSheetMetal(context is Context, id is Id, definition is map)
{
    const evaluatedFaceQueries = evaluateQuery(context, definition.regions);
    const faceQueryCount = size(evaluatedFaceQueries);
    if (faceQueryCount == 0)
    {
        throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["regions"]);
    }

    // STRIPPED: Removed version check for INFORM_IN_CONTEXT_SM_THICKEN

    var sketchPlaneToFacesMap = {};
    var facesToConvert = [];
    var index = 0;
    for (var evaluatedFace in evaluatedFaceQueries)
    {
        var key = try silent(evOwnerSketchPlane(context, { "entity" : evaluatedFace }));
        if (key == undefined)
        {
            facesToConvert = append(facesToConvert, evaluatedFace);
        }
        else
        {
            if (sketchPlaneToFacesMap[key] == undefined)
            {
                sketchPlaneToFacesMap[key] = [evaluatedFace];
            }
            else
            {
                sketchPlaneToFacesMap[key] = append(sketchPlaneToFacesMap[key], evaluatedFace);
            }
        }
    }

    index = 0;
    for (var entry in sketchPlaneToFacesMap)
    {
        var faceQueryArray = entry.value;

        definition.regions = qUnion(faceQueryArray);
        convertRegion(context, id + unstableIdComponent(index), definition);
        index += 1;
    }

    // STRIPPED: Removed throwOnUnsupportedFaces check
    
    var bendsQ = qNothing();
    var nFaces = size(facesToConvert);
    var nBends = size(evaluateQuery(context, definition.bends));
    if (nFaces != 0)
    {
        var useFacesAround = false; // Simplified from version check
        facesToConvert = append(facesToConvert, qEntityFilter(definition.bends, EntityType.FACE));
        bendsQ = convertFaces(context, id, definition, qUnion(facesToConvert), useFacesAround);
    }
    definition.keepInputParts = !definition.deleteOriginal; // Modified: use our parameter
    definition.remindToSelectBends = (nFaces > 1 && nBends == 0);

    // STRIPPED: Removed transformResultIfNecessary

    annotateConvertedFaces(context, id, definition, bendsQ);

    return qCreatedBy(id, EntityType.BODY);
}

function convertRegion(context is Context, id is Id, definition is map)
{
    // STRIPPED: Entire convertRegion function removed - only needed for sketch regions
    // For face-based thickening, this function is skipped
}

function convertFaces(context is Context, id is Id, definition, faces is Query, trimWithFacesAround is boolean) returns Query
{
    // STRIPPED: Removed checkConeApexInModel

    var surfaceId = id + "extractSurface"; // KEY: sub-ID for operation
    var bendsQ = startTracking(context, { "subquery" : definition.bends });
    var offset = computeSurfaceOffset(context, definition);

    try
    {
        opExtractSurface(context, surfaceId, {
                    "faces" : faces,
                    "offset" : offset,
                    "useFacesAroundToTrimOffset" : trimWithFacesAround,
                    "tangentPropagation" : definition.tangentPropagation });
    }
    catch
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_THICKEN, ["partToConvert", "facesToExclude", "regions"]);
    }

    return bendsQ;
}

function computeSurfaceOffset(context is Context, definition is map) returns ValueWithUnits
{
    var wallClearance = definition.clearance;
    if (definition.bendsIncluded)
    {
        var edges = evaluateQuery(context, qEntityFilter(definition.bends, EntityType.EDGE));
        if (size(edges) > 0)
        {
            for (var edge in edges)
            {
                var adjacentWalls = qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE);
                if (isQueryEmpty(context, adjacentWalls))
                {
                    continue;
                }
                var convexity = evEdgeConvexity(context, { "edge" : edge });
                if (definition.oppositeDirection)
                {
                    if (convexity != EdgeConvexityType.CONCAVE)
                    {
                        continue;
                    }
                }
                else
                {
                    if (convexity != EdgeConvexityType.CONVEX)
                    {
                        continue;
                    }
                }
                var eAngle = edgeAngle(context, edge);
                var cHalfAngle = cos(eAngle * 0.5);
                var clearance = definition.radius * (1 - cHalfAngle) + definition.clearance * cHalfAngle;
                if (clearance > wallClearance)
                {
                    wallClearance = clearance;
                }
            }
        }
    }
    if (definition.oppositeDirection)
    {
        return -wallClearance;
    }
    else
    {
        return wallClearance;
    }
}

function annotateConvertedFaces(context is Context, id is Id, definition, bendsQuery is Query)
{
    try
    {
        var thicknessDirection = getThicknessDirection(context, definition);
        annotateSmSurfaceBodies(context, id, {  // KEY: uses base id, not surfaceId
                    "surfaceBodies" : qCreatedBy(id, EntityType.BODY),  // KEY: queries with base id
                    "bendEdgesAndFaces" : bendsQuery,
                    "specialRadiiBends" : [],
                    "defaultRadius" : definition.radius,
                    "controlsThickness" : true,
                    "thickness" : definition.thickness,
                    "thicknessDirection" : thicknessDirection,
                    "minimalClearance" : definition.minimalClearance,
                    "kFactor" : definition.kFactor,
                    "kFactorRolled" : definition.kFactor, // Simplified
                    "flipDirectionUp" : false,
                    "defaultTwoCornerStyle" : SMReliefStyle.SIMPLE,
                    "defaultThreeCornerStyle" : SMReliefStyle.SIMPLE,
                    "defaultBendReliefStyle" : SMReliefStyle.OBROUND,
                    "defaultCornerReliefScale" : 1.5,
                    "defaultRoundReliefDiameter" : 0 * meter,
                    "defaultSquareReliefWidth" : 0 * meter,
                    "defaultBendReliefDepthScale" : 2.0,
                    "defaultBendReliefScale" : 1.0625,
                    "bendCalculationType" : SMBendCalculationType.K_FACTOR}, 0);
        if (getFeatureError(context, id) != undefined)
        {
            return;
        }
    }
    catch (thrownError)
    {
        if (thrownError is map && thrownError.message is ErrorStringEnum)
            throw thrownError;

        throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
    }

    if (!definition.keepInputParts)
    {
        // Delete original body before finalization
        var faceOwner = qOwnerBody(definition.regions);
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

    // KEY: Finalization with base id, not surfaceId
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
    
    // STRIPPED: Removed remindToSelectBends check
}

function getThicknessDirection(context is Context, definition is map)
{
    if (definition.oppositeDirection)
    {
        return SMThicknessDirection.BACK;
    }
    else
    {
        return SMThicknessDirection.FRONT;
    }
}

// STRIPPED: Removed all other helper functions (300+ lines)
// STRIPPED: Removed all manipulator functions (200+ lines)
// STRIPPED: Removed all editing logic functions (300+ lines)
