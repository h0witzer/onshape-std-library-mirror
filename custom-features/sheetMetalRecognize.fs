FeatureScript 2599;
/* Manually generated version */
// This module is edited from part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab in the STD document for the license text.
// Copyright (c) 2013-Present Onshape Inc.

export import(path : "onshape/std/extrudeCommon.fs", version : "2599.0");
export import(path : "onshape/std/query.fs", version : "2599.0");

import(path : "onshape/std/attributes.fs", version : "2599.0");
import(path : "onshape/std/box.fs", version : "2599.0");
import(path : "onshape/std/containers.fs", version : "2599.0");
import(path : "onshape/std/coordSystem.fs", version : "2599.0");
import(path : "onshape/std/curveGeometry.fs", version : "2599.0");
import(path : "onshape/std/error.fs", version : "2599.0");
import(path : "onshape/std/evaluate.fs", version : "2599.0");
import(path : "onshape/std/feature.fs", version : "2599.0");
import(path : "onshape/std/geomOperations.fs", version : "2599.0");
import(path : "onshape/std/manipulator.fs", version : "2599.0");
import(path : "onshape/std/math.fs", version : "2599.0");
import(path : "onshape/std/modifyFillet.fs", version : "2599.0");
import(path : "onshape/std/properties.fs", version : "2599.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2599.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2599.0");
import(path : "onshape/std/sketch.fs", version : "2599.0");
import(path : "onshape/std/smreliefstyle.gen.fs", version : "2599.0");
import(path : "onshape/std/string.fs", version : "2599.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2599.0");
import(path : "onshape/std/tool.fs", version : "2599.0");
import(path : "onshape/std/topologyUtils.fs", version : "2599.0");
import(path : "onshape/std/valueBounds.fs", version : "2599.0");
import(path : "onshape/std/vector.fs", version : "2599.0");


/**
 * Default corner relief style setting
 */
export enum SMCornerStrategyType
{
    annotation { "Name" : "Sized rectangle" }
    SIZED_RECTANGLE,
    annotation { "Name" : "Rectangle" }
    RECTANGLE,
    annotation { "Name" : "Sized round" }
    SIZED_ROUND,
    annotation { "Name" : "Round" }
    ROUND,
    annotation { "Name" : "Closed" }
    CLOSED,
    annotation { "Name" : "Simple" }
    SIMPLE
}

/**
 * Default bend relief style setting
 */
export enum SMBendStrategyType
{
    annotation { "Name" : "Rectangle" }
    RECTANGLE,
    annotation { "Name" : "Obround" }
    OBROUND,
    annotation { "Name" : "Tear" }
    TEAR
}

/**
 * Corner relief scale bounds
 */
export const CORNER_RELIEF_SCALE_BOUNDS =
{
            (unitless) : [1.0, 1.5, 2.0]
        } as RealBoundSpec;

/**
 * Bend relief depth scale bounds
 */
export const BEND_RELIEF_DEPTH_SCALE_BOUNDS =
{
            (unitless) : [1.0, 2.0, 5.0]
        } as RealBoundSpec;

/**
 * Bend relief width scale bounds
 */
export const BEND_RELIEF_WIDTH_SCALE_BOUNDS =
{
            (unitless) : [0.0625, 1.0625, 2.0]
        } as RealBoundSpec;

/**
 * Default bend allowance setting
 */
export enum SMBendAllowanceType
{
    annotation { "Name" : "K Factor" }
    K_FACTOR,
    annotation { "Name" : "Allowance" }
    BEND_ALLOWANCE,
    annotation { "Name" : "Deduction" }
    BEND_DEDUCTION
}

/**
 * Create and activate a sheet metal model by converting existing parts, extruding sketch curves or thickening.
 * All operations on an active sheet metal model will automatically be represented in the flat pattern and the table.
 * Sheet metal models may consist of multiple parts. Multiple sheet metal models can be active.
 */
annotation { "Feature Type Name" : "Recognize Sheet Metal",
        "Manipulator Change Function" : "sheetMetalStartManipulatorChange",
        "Filter Selector" : "allparts",
        "Editing Logic Function" : "sheetMetalStartEditLogic" }
export const sheetMetalStart = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Entities", "UIHint" : UIHint.ALWAYS_HIDDEN, "Filter" : (EntityType.BODY && (BodyType.SOLID || BodyType.SHEET)) && ActiveSheetMetal.YES ||
                    ((EntityType.FACE || EntityType.EDGE) && SketchObject.YES && ConstructionObject.NO) }
        definition.initEntities is Query;

        // First the entities
        annotation { "Group Name" : "Selections", "Collapsed By Default" : false }
        {

            annotation { "Name" : "Parts to recognise",
                        "Filter" : EntityType.BODY && BodyType.SOLID }
            definition.bodies is Query;
            annotation { "Name" : "Edges or cylinders to bend",
                        "Filter" : ((EntityType.EDGE && EdgeTopology.TWO_SIDED && GeometryType.LINE) ||
                                (EntityType.FACE && GeometryType.CYLINDER)) && SketchObject.NO }
            definition.bends is Query;
            annotation { "Name" : "Specify thickness" }
            definition.changeThickness is boolean;

        }

        // Then some common parameters
        annotation { "Group Name" : "General", "Collapsed By Default" : false }
        {
            if (definition.changeThickness)
            {
                annotation { "Name" : "Thickness", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.thickness, SM_THICKNESS_BOUNDS);

                annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.oppositeDirection is boolean;
            }

            annotation { "Name" : "Bend radius", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.radius, SM_BEND_RADIUS_BOUNDS);
        }

        annotation { "Group Name" : "Material", "Collapsed By Default" : true }
        {
            if (definition.changeThickness)
            {
                annotation { "Name" : "Bend allowance type", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.HORIZONTAL_ENUM] }
                definition.bendAllowanceType is SMBendAllowanceType;
            }

            if ((!definition.changeThickness) || definition.bendAllowanceType == SMBendAllowanceType.K_FACTOR)
            {
                annotation { "Name" : "Bend K Factor", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isReal(definition.kFactor, K_FACTOR_BOUNDS);

                annotation { "Name" : "Rolled K Factor", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isReal(definition.kFactorRolled, ROLLED_K_FACTOR_BOUNDS);
            }
            else if (definition.bendAllowanceType == SMBendAllowanceType.BEND_ALLOWANCE)
            {
                annotation { "Name" : "Allowance for right angle", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.bendAllowance, ZERO_DEFAULT_LENGTH_BOUNDS);
            }
            else if (definition.bendAllowanceType == SMBendAllowanceType.BEND_DEDUCTION)
            {
                annotation { "Name" : "Deduction for right angle", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.bendDeduction, ZERO_DEFAULT_LENGTH_BOUNDS);
            }
        }

        annotation { "Group Name" : "Relief", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Minimal gap", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.minimalClearance, SM_MINIMAL_CLEARANCE_BOUNDS);

            annotation { "Name" : "Corner relief type",
                        "Default" : SMCornerStrategyType.SIMPLE,
                        "UIHint" : [UIHint.SHOW_LABEL, UIHint.REMEMBER_PREVIOUS_VALUE] }
            definition.defaultCornerStyle is SMCornerStrategyType;

            if (definition.defaultCornerStyle == SMCornerStrategyType.RECTANGLE ||
                definition.defaultCornerStyle == SMCornerStrategyType.ROUND)
            {
                annotation { "Name" : "Corner relief scale", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isReal(definition.defaultCornerReliefScale, CORNER_RELIEF_SCALE_BOUNDS);
            }

            else if (definition.defaultCornerStyle == SMCornerStrategyType.SIZED_ROUND)
            {
                annotation { "Name" : "Corner relief diameter", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.defaultRoundReliefDiameter, SM_RELIEF_SIZE_BOUNDS);
            }

            else if (definition.defaultCornerStyle == SMCornerStrategyType.SIZED_RECTANGLE)
            {
                annotation { "Name" : "Corner relief width", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.defaultSquareReliefWidth, SM_RELIEF_SIZE_BOUNDS);
            }

            annotation { "Name" : "Bend relief type",
                        "Default" : SMBendStrategyType.OBROUND,
                        "UIHint" : [UIHint.SHOW_LABEL, UIHint.REMEMBER_PREVIOUS_VALUE] }
            definition.defaultBendReliefStyle is SMBendStrategyType;


            if (definition.defaultBendReliefStyle == SMBendStrategyType.OBROUND ||
                definition.defaultBendReliefStyle == SMBendStrategyType.RECTANGLE)
            {
                annotation { "Name" : "Bend relief depth scale", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isReal(definition.defaultBendReliefDepthScale, BEND_RELIEF_DEPTH_SCALE_BOUNDS);
                annotation { "Name" : "Bend relief width scale", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isReal(definition.defaultBendReliefScale, BEND_RELIEF_WIDTH_SCALE_BOUNDS);
            }
        }
    }
    {
            removeAttributes(context, {
                "attributePattern" : asSMAttribute({})
            });
        if (definition.bendAllowanceType != SMBendAllowanceType.K_FACTOR && (definition.changeThickness))
        {
            var t = definition.thickness;
            var r = definition.radius;
            var BALow = ceil(definition.radius * (PI / 2) / millimeter, 0.01);
            var BAHigh = floor((PI / 2) * (r + t) / millimeter, 0.01);
            var BDLow = ceil(1 / 2 * (-PI * r + 4 * r + 4 * t) / millimeter, 0.01);
            var BDHigh = floor(1 / 2 * (-PI * t - PI * r + 4 * r + 4 * t) / millimeter, 0.01);
            var b = definition.bendAllowance;
            if (definition.bendAllowanceType == SMBendAllowanceType.BEND_DEDUCTION)
                b = 2 * (definition.radius + definition.thickness) - definition.bendDeduction;

            definition.kFactor = (2 * b - PI * r) / (PI * t);
            if (definition.kFactor < 0 || definition.kFactor > 1)
            {
                reportFeatureWarning(context, id, {
                                SMBendAllowanceType.BEND_DEDUCTION : "The bend deduction needs to be between " ~ BDHigh ~ "mm and " ~ BDLow ~ "mm",
                                SMBendAllowanceType.BEND_ALLOWANCE : "The bend allowance needs to be between " ~ BALow ~ "mm and " ~ BAHigh ~ "mm"
                            }[definition.bendAllowanceType]);
            }
            definition.kFactorRolled = definition.kFactor;
        }
        definition.supportRolled = isAtVersionOrLater(context, FeatureScriptVersionNumber.V727_SM_SUPPORT_ROLLED);
        checkNotInFeaturePattern(context, definition.bodies, ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);
        sheetMetalRecognize(context, id, definition);

    }, {
            "kFactor" : 0.45,
            "kFactorRolled" : 0.5,
            "minimalClearance" : 2e-5 * meter,
            "oppositeDirection" : false,
            "initEntities" : qNothing(),
            "bendArcs" : qNothing(),
            "defaultCornerStyle" : SMCornerStrategyType.SIMPLE,
            "defaultCornerReliefScale" : 1.5,
            "defaultRoundReliefDiameter" : 0 * meter,
            "defaultSquareReliefWidth" : 0 * meter,
            "defaultBendReliefStyle" : SMBendStrategyType.OBROUND,
            "defaultBendReliefDepthScale" : 1.5,
            "defaultBendReliefScale" : 1.0625,
            "bendsIncluded" : false,
            "clearance" : 0 * meter,
            "keepInputParts" : false,
            "tangentPropagation" : false,
            "hasSecondDirection" : false, // option for extrude second direction
            "oppositeExtrudeDirection" : false,
            "secondDirectionOppositeExtrudeDirection" : false,
            "hasOffset" : false,
            "hasSecondDirectionOffset" : false,
            "offsetOppositeDirection" : false,
            "secondDirectionOffsetOppositeDirection" : false
        });

function finalizeSheetMetalGeometry(context is Context, id is Id, entities is Query)
{
    try
    {
        updateSheetMetalGeometry(context, id, { "entities" : entities });
    }
    catch (e)
    {
        var messageAsEnum = try silent(e.message as ErrorStringEnum);
        if (messageAsEnum == ErrorStringEnum.BOOLEAN_INVALID)
        {
            // I can't think of anything more useful to tell the user right now. Analyzing such cases
            // may make it clearer when it can happen
            throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
        }
        else if (messageAsEnum == ErrorStringEnum.BAD_GEOMETRY ||
            messageAsEnum == ErrorStringEnum.THICKEN_FAILED)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_THICKEN);
        }
        else if (messageAsEnum == ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);
        }
        else
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
        }
    }
}


function getSketchCurvesToExtrude(definition is map)
{
    var sketchCurves = (definition.supportRolled == true) ?
    qUnion([definition.bendArcs, definition.sketchCurves]) :
    qGeometry(definition.sketchCurves, GeometryType.LINE);
    return qConstructionFilter(sketchCurves, ConstructionObject.NO);
}


function getExtrudeDirection(context is Context, entity is Query)
{
    const tangentAtEdge = evEdgeTangentLine(context, { "edge" : entity, "parameter" : 0.5 });
    const entityPlane = evOwnerSketchPlane(context, { "entity" : entity });
    var direction = entityPlane.normal;
    return line(tangentAtEdge.origin, direction);
}

function extrudeUpToBoundaryFlip(context is Context, definition is map) returns map
{
    const sketchCurves = getSketchCurvesToExtrude(definition);
    const resolvedEntities = evaluateQuery(context, sketchCurves);
    if (size(resolvedEntities) == 0)
    {
        return definition;
    }
    const extrudeAxis = try(getExtrudeDirection(context, resolvedEntities[0]));
    if (extrudeAxis == undefined)
    {
        return definition;
    }
    return extrudeUpToBoundaryFlipCommon(context, extrudeAxis, definition);
}


function getDefaultTwoCornerStyle(definition is map) returns SMReliefStyle
{
    if (definition.defaultCornerStyle == SMCornerStrategyType.RECTANGLE)
    {
        return SMReliefStyle.RECTANGLE;
    }
    else if (definition.defaultCornerStyle == SMCornerStrategyType.ROUND)
    {
        return SMReliefStyle.ROUND;
    }
    else if (definition.defaultCornerStyle == SMCornerStrategyType.SIZED_RECTANGLE)
    {
        return SMReliefStyle.SIZED_RECTANGLE;
    }
    else if (definition.defaultCornerStyle == SMCornerStrategyType.SIZED_ROUND)
    {
        return SMReliefStyle.SIZED_ROUND;
    }
    else if (definition.defaultCornerStyle == SMCornerStrategyType.CLOSED)
    {
        return SMReliefStyle.CLOSED;
    }
    else if (definition.defaultCornerStyle == SMCornerStrategyType.SIMPLE)
    {
        return SMReliefStyle.SIMPLE;
    }
    else
    {
        return SMReliefStyle.RECTANGLE;
    }
}

function getDefaultThreeCornerStyle(context is Context, definition is map) returns SMReliefStyle
{
    const includeSized = isAtVersionOrLater(context, FeatureScriptVersionNumber.V781_THREE_BEND_SIZED);

    if (definition.defaultCornerStyle == SMCornerStrategyType.RECTANGLE)
    {
        return SMReliefStyle.RECTANGLE;
    }
    else if (definition.defaultCornerStyle == SMCornerStrategyType.ROUND)
    {
        return SMReliefStyle.ROUND;
    }
    else if (includeSized && definition.defaultCornerStyle == SMCornerStrategyType.SIZED_RECTANGLE)
    {
        return SMReliefStyle.SIZED_RECTANGLE;
    }
    else if (includeSized && definition.defaultCornerStyle == SMCornerStrategyType.SIZED_ROUND)
    {
        return SMReliefStyle.SIZED_ROUND;
    }
    else if (definition.defaultCornerStyle == SMCornerStrategyType.SIMPLE)
    {
        return SMReliefStyle.SIMPLE;
    }
    else
    {
        return SMReliefStyle.RECTANGLE;
    }
}

function getDefaultBendReliefStyle(definition is map) returns SMReliefStyle
{
    if (definition.defaultBendReliefStyle == SMBendStrategyType.RECTANGLE)
    {
        return SMReliefStyle.RECTANGLE;
    }
    else if (definition.defaultBendReliefStyle == SMBendStrategyType.OBROUND)
    {
        return SMReliefStyle.OBROUND;
    }
    else if (definition.defaultBendReliefStyle == SMBendStrategyType.TEAR)
    {
        return SMReliefStyle.TEAR;
    }
    else
    {
        return SMReliefStyle.OBROUND;
    }
}

/**
 *  @internal
 *  This function uses evOffsetDetection functionality to recognise sheet metal body,
 *  extracts definition sheet surface, replaces cylinders with sharp edges, when possible,
 *  otherwise replacing them with rolled bends.
 *  Sheet body is annotated as Model, planar faces are annotated as Walls,
 *  cylinders or sharp edges replacing them are annotated as Bends preserving original radius,
 *  Original sharp edges are annotated as Bends of input radius. TODO : recognize Rips.
 */
// annotation { "Feature Type Name" : "Recognise" }
function sheetMetalRecognize(context is Context, id is Id, definition is map)
{
    definition.bends = qUnion(evaluateQuery(context, qGeometry(definition.bends, GeometryType.CYLINDER)));
    var associationAttributes = getAttributes(context, {
            "entities" : definition.bodies,
            "attributePattern" : {} as SMAssociationAttribute
        });
    if (size(associationAttributes) != 0)
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_INPUT_BODY_SHOULD_NOT_BE_SHEET_METAL, ["bodies"]);
    }
    var offsetGroups = evOffsetDetection(context, definition);

    if (size(offsetGroups) != size(evaluateQuery(context, definition.bodies)))
    {
        var offsets = offsetGroups;
        var evaluatedBodies = evaluateQuery(context, definition.bodies);
        var badParts = [];
        for (var body in evaluatedBodies)
        {
            var numberOfParts = 0;
            for (var i = 0; i < size(offsets); i += 1)
            {
                if (size(evaluateQuery(context, qSubtraction(qOwnerBody(offsets[i].side0[0]), body))) == 0)
                {
                    numberOfParts += 1;
                }
            }
            if (numberOfParts == 0)
            {
                badParts = append(badParts, body);
            }
        }
        if (badParts != [])
            throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_RECOGNIZE_PARTS, ["bodies"], qUnion(badParts));
    }

    // Map each input body to its corresponding offset group for name preservation
    var evaluatedInputBodies = evaluateQuery(context, definition.bodies);
    var bodyToGroupIndex = {};
    for (var i = 0; i < size(evaluatedInputBodies); i += 1)
    {
        for (var j = 0; j < size(offsetGroups); j += 1)
        {
            if (size(evaluateQuery(context, qSubtraction(qOwnerBody(offsetGroups[j].side0[0]), evaluatedInputBodies[i]))) == 0)
            {
                bodyToGroupIndex[i] = j;
                break;
            }
        }
    }

    var objectCount = 0;
    var groupCount = 0;
    var smFacesAndEdgesQ = qNothing();
    var surfaceIdToBodyIndex = {};
    
    for (var group in offsetGroups)
    {
        var surfaceId = id + ("surface_" ~ groupCount);
        var surfaceData = definition.changeThickness ?
        makeSurfaceBody(context, surfaceId, group, definition.oppositeDirection, definition.bends) :
        makeSurfaceBody(context, surfaceId, group, definition.bends);
        surfaceData.defaultRadius = definition.radius;
        surfaceData.controlsThickness = definition.changeThickness;
        if (definition.changeThickness)
        {
            surfaceData.thickness = definition.thickness;
        }

        surfaceData = mergeMaps(surfaceData, {
                    "minimalClearance" : definition.minimalClearance,
                    "kFactor" : definition.kFactor,
                    "kFactorRolled" : definition.kFactorRolled,
                    "defaultTwoCornerStyle" : getDefaultTwoCornerStyle(definition),
                    "defaultThreeCornerStyle" : getDefaultThreeCornerStyle(context, definition),
                    "defaultBendReliefStyle" : getDefaultBendReliefStyle(definition),
                    "defaultCornerReliefScale" : definition.defaultCornerReliefScale,
                    "defaultRoundReliefDiameter" : definition.defaultRoundReliefDiameter,
                    "defaultSquareReliefWidth" : definition.defaultSquareReliefWidth,
                    "defaultBendReliefDepthScale" : definition.defaultBendReliefDepthScale,
                    "defaultBendReliefScale" : definition.defaultBendReliefScale
                });

        // Store mapping from surface ID to original body index for name preservation
        for (var bodyIdx = 0; bodyIdx < size(evaluatedInputBodies); bodyIdx += 1)
        {
            if (bodyToGroupIndex[bodyIdx] != undefined && bodyToGroupIndex[bodyIdx] == groupCount)
            {
                surfaceIdToBodyIndex[surfaceId] = bodyIdx;
                break;
            }
        }

        groupCount += 1;
        smFacesAndEdgesQ = qUnion([smFacesAndEdgesQ, qCreatedBy(surfaceId, EntityType.FACE), qCreatedBy(surfaceId, EntityType.EDGE)]);
        objectCount = annotateSmSurfaceBodies(context, id, surfaceData, objectCount);
        if (getFeatureError(context, id) != undefined)
        {
            continue;
        }
    }
    if (!definition.keepInputParts)
    {
        try
        {
            opDeleteBodies(context, id + "deleteInput", {
                        "entities" : definition.bodies
                    });
        }
        catch
        {
            throw regenError(ErrorStringEnum.REGEN_ERROR);
        }
    }

    finalizeSheetMetalGeometry(context, id, smFacesAndEdgesQ);

    // Apply names from input bodies to generated sheet metal bodies
    if (definition.inputBodyNames != undefined && size(definition.inputBodyNames) > 0)
    {
        var finalBodies = evaluateQuery(context, qCreatedBy(id, EntityType.BODY));
        for (var finalBody in finalBodies)
        {
            // Find which surface ID created this body by checking face ownership
            var bodyFaces = evaluateQuery(context, qOwnedByBody(finalBody, EntityType.FACE));
            if (size(bodyFaces) > 0)
            {
                // Check each surface ID to see if it created faces in this body
                for (var surfaceIdKeyPair in surfaceIdToBodyIndex)
                {
                    var surfaceIdKey = surfaceIdKeyPair.key;
                    var facesFromSurface = evaluateQuery(context, qIntersection([
                        qCreatedBy(surfaceIdKey, EntityType.FACE),
                        qOwnedByBody(finalBody, EntityType.FACE)
                    ]));
                    if (size(facesFromSurface) > 0)
                    {
                        var bodyIndex = surfaceIdKeyPair.value;
                        if (bodyIndex < size(definition.inputBodyNames) && definition.inputBodyNames[bodyIndex] != undefined)
                        {
                            setProperty(context, {
                                "entities" : finalBody,
                                "propertyType" : PropertyType.NAME,
                                "value" : definition.inputBodyNames[bodyIndex]
                            });
                            break;
                        }
                    }
                }
            }
        }
    }
}


function makeSurfaceBody(context is Context, id is Id, group is map, inputBends is Query)
{
    var out = { "thickness" : 0.5 * (group.offsetLow + group.offsetHigh),
        "thicknessDirection" : SMThicknessDirection.BACK };
    var bends = [];
    for (var i = 0; i < size(group.side0); i += 1)
    {
        if (evaluateQuery(context, qIntersection([inputBends, qUnion([group.side0[i], group.side1[i]])])) != [])
        {
            bends = append(bends, evaluateQuery(context, group.side0[i])[0]);
        }
    }
    bends = startTracking(context, qUnion(bends));
    try
    {
        opExtractSurface(context, id, {
                    "faces" : qUnion(group.side0),
                    "offset" : 0.0,
                    "useFacesAroundToTrimOffset" : true
                });
        bends = qUnion(evaluateQuery(context, bends));
        var srfBodies = evaluateQuery(context, qCreatedBy(id, EntityType.BODY));
        if (size(srfBodies) != 1)
        {
            throw regenError("Unexpected number of surfaces extracted");
        }
        out.surfaceBodies = srfBodies[0];
    }
    catch
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_THICKEN, ["bodies"]);
    }

    //Collect sharp edges to mark them as default radius bends
    var sharpEdges = [];
    for (var edge in evaluateQuery(context, qOwnedByBody(out.surfaceBodies, EntityType.EDGE)))
    {
        if (!edgeIsTwoSided(context, edge))
        {
            continue;
        }
        var convexity = evEdgeConvexity(context, { "edge" : edge });
        if (convexity == EdgeConvexityType.CONVEX || convexity == EdgeConvexityType.CONCAVE)
        {
            sharpEdges = append(sharpEdges, edge);
        }
    }
    out.bendEdgesAndFaces = qUnion([qUnion(sharpEdges), bends]);

    // remove cylindrical faces where possible and collect replacement edges with radius data
    // TODO: when moveEdge functionality is available try extract planar faces,
    // extend to other side of bend or rip and merge
    out.specialRadiiBends = [];
    return out;
}

function makeSurfaceBody(context is Context, id is Id, group is map, opp is boolean, inputBends is Query)
{
    var out = { "thickness" : 0.5 * (group.offsetLow + group.offsetHigh),
        "thicknessDirection" : SMThicknessDirection.BACK };
    var bends = [];
    for (var i = 0; i < size(group.side0); i += 1)
    {
        if (evaluateQuery(context, qIntersection([inputBends, qUnion([group.side0[i], group.side1[i]])])) != [])
            try silent
            {
                var query = group[opp ? "side1" : "side0"][i];
                bends = append(bends, query);
            }
    }
    bends = startTracking(context, qUnion(bends));
    try
    {
        opExtractSurface(context, id, {
                    "faces" : qUnion(group[opp ? "side1" : "side0"]),
                    "offset" : 0.0,
                    "useFacesAroundToTrimOffset" : true
                });
        bends = qUnion(evaluateQuery(context, bends));
        var srfBodies = evaluateQuery(context, qCreatedBy(id, EntityType.BODY));
        if (size(srfBodies) != 1)
        {
            throw regenError("Unexpected number of surfaces extracted");
        }
        out.surfaceBodies = srfBodies[0];
    }
    catch
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_THICKEN, ["bodies"]);
    }

    //Collect sharp edges to mark them as default radius bends
    var sharpEdges = [];
    for (var edge in evaluateQuery(context, qOwnedByBody(out.surfaceBodies, EntityType.EDGE)))
    {
        if (!edgeIsTwoSided(context, edge))
        {
            continue;
        }
        var convexity = evEdgeConvexity(context, { "edge" : edge });
        if (convexity == EdgeConvexityType.CONVEX || convexity == EdgeConvexityType.CONCAVE)
        {
            sharpEdges = append(sharpEdges, edge);
        }
    }
    out.bendEdgesAndFaces = qUnion([qUnion(sharpEdges), bends]);

    // remove cylindrical faces where possible and collect replacement edges with radius data
    // TODO: when moveEdge functionality is available try extract planar faces,
    // extend to other side of bend or rip and merge
    out.specialRadiiBends = [];

    return out;
}

/**
 * @internal
 */
export function sheetMetalStartManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    return extrudeManipulatorChange(context, definition, newManipulators);
}

/**
 * @internal
 */
export function sheetMetalStartEditLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    specifiedParameters is map, hiddenBodies is Query) returns map
{
    // Preselection processing
    if (oldDefinition == {})
    {
        const bodies = qEntityFilter(definition.initEntities, EntityType.BODY);
        const faces = qEntityFilter(definition.initEntities, EntityType.FACE);
        const edges = qModifiableEntityFilter(qEntityFilter(definition.initEntities, EntityType.EDGE));
        if (size(evaluateQuery(context, bodies)) > 0)
        {
            definition.partToConvert = bodies;
        }
        else if (size(evaluateQuery(context, faces)) > 0)
        {
            definition.regions = faces;
        }
        else if (size(evaluateQuery(context, edges)) > 0)
        {
            definition.sketchCurves = edges;
        }
        // Clear out the pre-selection data: this is especially important if the query is to imported data
        definition.initEntities = qNothing();
    }

    // Capture names from input bodies to preserve them after recognition
    if (definition.bodies != undefined)
    {
        var inputBodyNames = [];
        for (var body in evaluateQuery(context, definition.bodies))
        {
            var bodyName = getProperty(context, { "entity" : body, "propertyType" : PropertyType.NAME });
            inputBodyNames = append(inputBodyNames, bodyName);
        }
        definition.inputBodyNames = inputBodyNames;
    }

    // Extrude flips
    // If this is changed, make sure to reflect the change in extrude::extrudeEditLogic.
    if (canSetExtrudeFlips(definition, specifiedParameters))
    {
        if (canSetExtrudeUpToFlip(definition, specifiedParameters))
        {
            definition = extrudeUpToBoundaryFlip(context, definition);
        }
    }
    definition = setExtrudeSecondDirectionFlip(definition, specifiedParameters);

    return definition;
}
