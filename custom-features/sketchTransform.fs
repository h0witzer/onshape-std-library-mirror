FeatureScript 2878;

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/box.fs", version : "2878.0");
import(path : "onshape/std/coordSystem.fs", version : "2878.0");
import(path : "onshape/std/error.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/featureList.fs", version : "2878.0");
import(path : "onshape/std/manipulator.fs", version : "2878.0");
import(path : "onshape/std/matrix.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/transform.fs", version : "2878.0");
import(path : "onshape/std/units.fs", version : "2878.0");
import(path : "onshape/std/valueBounds.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");

/**
 * Shear parameter bounds for skewing transformations.
 * Values typically range from -5 to 5 for reasonable skewing effects.
 */
const SHEAR_PARAMETER_BOUNDS =
{ 
    (unitless) : [-5, 0, 5]
} as RealBoundSpec;

/**
 * Manipulator IDs for interactive control handles.
 */
const SCALE_X_MANIPULATOR = "scaleXManip";
const SCALE_Y_MANIPULATOR = "scaleYManip";
const SKEW_MANIPULATOR = "skewManip";

/**
 * Planar transform feature for scaling and skewing sketch-derived bodies.
 * 
 * This feature allows non-uniform scaling and skewing transformations on sketch entities
 * that have been converted to bodies. Since sketches are planar, this feature focuses on
 * 2D transformations in the sketch plane with interactive manipulator handles.
 * 
 * Supports two input modes:
 * 1. Select sketch feature directly - transforms all bodies created by the sketch
 * 2. Select individual sketch bodies - transforms only selected entities
 */
annotation { "Feature Type Name" : "Sketch Transform",
            "Manipulator Change Function" : "sketchTransformManipulatorChange" }
export const sketchTransform = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Input type", "UIHint" : UIHint.SHOW_LABEL }
        definition.inputType is SketchInputType;

        if (definition.inputType == SketchInputType.SKETCH_FEATURE)
        {
            annotation { "Name" : "Sketch feature", "MaxNumberOfPicks" : 1, "Filter" : SketchObject.YES }
            definition.sketchFeature is FeatureList;
        }
        else
        {
            annotation { "Name" : "Sketch bodies to transform", "Filter" : EntityType.BODY && SketchObject.YES }
            definition.sketchBodies is Query;
        }

        annotation { "Name" : "Transform anchor", "Filter" : BodyType.MATE_CONNECTOR || (EntityType.VERTEX && SketchObject.NO), "MaxNumberOfPicks" : 1 }
        definition.anchor is Query;

        annotation { "Group Name" : "Scaling", "Name" : "Scale X" }
        isReal(definition.scaleX, SCALE_BOUNDS);

        annotation { "Group Name" : "Scaling", "Name" : "Scale Y" }
        isReal(definition.scaleY, SCALE_BOUNDS);

        annotation { "Group Name" : "Skewing", "Name" : "Skew X by Y" }
        isReal(definition.skewXY, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Skewing", "Name" : "Skew Y by X" }
        isReal(definition.skewYX, SHEAR_PARAMETER_BOUNDS);
    }
    {
        // Get bodies to transform based on input type
        var bodiesToTransform = qNothing();
        if (definition.inputType == SketchInputType.SKETCH_FEATURE)
        {
            // Get all bodies created by the selected sketch feature
            if (size(definition.sketchFeature) == 0)
            {
                throw regenError("Select a sketch feature", ["sketchFeature"]);
            }
            bodiesToTransform = qCreatedBy(definition.sketchFeature, EntityType.BODY);
        }
        else
        {
            // Use directly selected sketch bodies
            bodiesToTransform = qOwnerBody(definition.sketchBodies);
        }

        if (isQueryEmpty(context, bodiesToTransform))
        {
            throw regenError("Select at least one sketch body to transform", 
                definition.inputType == SketchInputType.SKETCH_FEATURE ? ["sketchFeature"] : ["sketchBodies"]);
        }

        // Resolve the anchor coordinate system for the transformation
        const anchorSystem = resolveAnchorCoordinateSystem(context, definition, bodiesToTransform);

        // Get the bounding box for manipulator placement
        const boundingBox = evBox3d(context, { "topology" : bodiesToTransform, "cSys" : anchorSystem, "tight" : false });
        const transformExtents = boundingBox.maxCorner - boundingBox.minCorner;

        // Build the planar transformation matrix (focusing on XY plane, sketch plane)
        // For planar sketches, we use a 2D transformation embedded in 3D space
        const localLinear = matrix([
                [definition.scaleX, definition.skewXY, 0],
                [definition.skewYX, definition.scaleY, 0],
                [0, 0, 1]
            ]);

        // Create the local transformation and convert to world space
        const localTransform = transform(localLinear, vector(0, 0, 0) * meter);
        const worldTransform = toWorld(anchorSystem) * localTransform * fromWorld(anchorSystem);

        // Add manipulators for interactive control
        addPlanarManipulators(context, id, definition, anchorSystem, boundingBox, transformExtents);

        // Apply the transformation to the selected bodies
        opTransform(context, id + "transform", {
                    "bodies" : bodiesToTransform,
                    "transform" : worldTransform
                });
    }, {
            "inputType" : SketchInputType.SKETCH_BODIES,
            "sketchBodies" : qNothing(),
            "sketchFeature" : {},
            "anchor" : qNothing(),
            "scaleX" : 1,
            "scaleY" : 1,
            "skewXY" : 0,
            "skewYX" : 0
        });

/**
 * Enum to specify the input type for the sketch transform feature.
 */
export enum SketchInputType
{
    annotation { "Name" : "Sketch feature" }
    SKETCH_FEATURE,
    annotation { "Name" : "Sketch bodies" }
    SKETCH_BODIES
}

/**
 * Manipulator change handler that updates scale and skew parameters based on manipulator movement.
 * 
 * @param context - The current context
 * @param definition - The current feature definition
 * @param newManipulators - Map of manipulator IDs to their new states
 * @returns Updated definition map with new parameter values
 */
export function sketchTransformManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    // Get bodies to transform for extent calculations
    var bodiesToTransform = qNothing();
    if (definition.inputType == SketchInputType.SKETCH_FEATURE && size(definition.sketchFeature) > 0)
    {
        bodiesToTransform = qCreatedBy(definition.sketchFeature, EntityType.BODY);
    }
    else
    {
        bodiesToTransform = qOwnerBody(definition.sketchBodies);
    }

    if (isQueryEmpty(context, bodiesToTransform))
    {
        return definition;
    }

    const anchorSystem = resolveAnchorCoordinateSystem(context, definition, bodiesToTransform);
    const boundingBox = evBox3d(context, { "topology" : bodiesToTransform, "cSys" : anchorSystem, "tight" : false });
    const transformExtents = boundingBox.maxCorner - boundingBox.minCorner;

    // Update scale X from manipulator
    if (newManipulators[SCALE_X_MANIPULATOR] is map)
    {
        const xExtent = transformExtents[0];
        if (xExtent > TOLERANCE.zeroLength * meter)
        {
            const manipOffset = newManipulators[SCALE_X_MANIPULATOR].offset;
            const baseExtent = xExtent / definition.scaleX;
            definition.scaleX = max(0.01, (baseExtent + manipOffset) / baseExtent);
        }
    }

    // Update scale Y from manipulator
    if (newManipulators[SCALE_Y_MANIPULATOR] is map)
    {
        const yExtent = transformExtents[1];
        if (yExtent > TOLERANCE.zeroLength * meter)
        {
            const manipOffset = newManipulators[SCALE_Y_MANIPULATOR].offset;
            const baseExtent = yExtent / definition.scaleY;
            definition.scaleY = max(0.01, (baseExtent + manipOffset) / baseExtent);
        }
    }

    // Update skew from manipulator
    if (newManipulators[SKEW_MANIPULATOR] is map)
    {
        const yExtent = transformExtents[1];
        if (yExtent > TOLERANCE.zeroLength * meter)
        {
            definition.skewXY = newManipulators[SKEW_MANIPULATOR].offset / yExtent;
        }
    }

    return definition;
}

/**
 * Add planar manipulators for interactive scale and skew control.
 * Places manipulator handles at appropriate locations on the transformed bounding box.
 * 
 * @param context - The current context
 * @param id - The feature ID
 * @param definition - The feature definition
 * @param anchorSystem - The coordinate system for the transformation
 * @param boundingBox - The bounding box of bodies being transformed
 * @param transformExtents - The size of the bounding box
 */
function addPlanarManipulators(context is Context, id is Id, definition is map, anchorSystem is CoordSystem, 
                               boundingBox is Box3d, transformExtents is Vector)
{
    var manipulators = {};

    // Calculate the current transformation for manipulator placement
    const localLinear = matrix([
            [definition.scaleX, definition.skewXY, 0],
            [definition.skewYX, definition.scaleY, 0],
            [0, 0, 1]
        ]);
    const localTransform = transform(localLinear, vector(0, 0, 0) * meter);
    const shearToWorld = toWorld(anchorSystem) * localTransform;

    const xExtent = transformExtents[0];
    const yExtent = transformExtents[1];

    // Scale X manipulator - placed on the +X edge
    if (xExtent > TOLERANCE.zeroLength * meter)
    {
        var xEdgePoint = vector(boundingBox.maxCorner[0], 
                               (boundingBox.minCorner[1] + boundingBox.maxCorner[1]) / 2,
                               (boundingBox.minCorner[2] + boundingBox.maxCorner[2]) / 2);
        
        const basePointWorld = shearToWorld * xEdgePoint;
        const directionLocal = vector(1, 0, 0);
        const directionWorld = shearToWorld.linear * directionLocal;
        const directionMagnitude = norm(directionWorld);
        
        if (directionMagnitude > TOLERANCE.zeroLength)
        {
            const currentScaledExtent = xExtent * definition.scaleX;
            const baseExtent = xExtent / definition.scaleX;
            const offset = currentScaledExtent - baseExtent;
            
            manipulators[SCALE_X_MANIPULATOR] = linearManipulator({
                        "base" : basePointWorld,
                        "direction" : directionWorld / directionMagnitude,
                        "offset" : offset,
                        "primaryParameterId" : "scaleX"
                    });
        }
    }

    // Scale Y manipulator - placed on the +Y edge
    if (yExtent > TOLERANCE.zeroLength * meter)
    {
        var yEdgePoint = vector((boundingBox.minCorner[0] + boundingBox.maxCorner[0]) / 2,
                               boundingBox.maxCorner[1],
                               (boundingBox.minCorner[2] + boundingBox.maxCorner[2]) / 2);
        
        const basePointWorld = shearToWorld * yEdgePoint;
        const directionLocal = vector(0, 1, 0);
        const directionWorld = shearToWorld.linear * directionLocal;
        const directionMagnitude = norm(directionWorld);
        
        if (directionMagnitude > TOLERANCE.zeroLength)
        {
            const currentScaledExtent = yExtent * definition.scaleY;
            const baseExtent = yExtent / definition.scaleY;
            const offset = currentScaledExtent - baseExtent;
            
            manipulators[SCALE_Y_MANIPULATOR] = linearManipulator({
                        "base" : basePointWorld,
                        "direction" : directionWorld / directionMagnitude,
                        "offset" : offset,
                        "primaryParameterId" : "scaleY"
                    });
        }
    }

    // Skew XY manipulator - placed on the top edge showing skew direction
    if (yExtent > TOLERANCE.zeroLength * meter)
    {
        var skewPoint = vector(boundingBox.maxCorner[0],
                              boundingBox.maxCorner[1],
                              (boundingBox.minCorner[2] + boundingBox.maxCorner[2]) / 2);
        
        const basePointWorld = shearToWorld * skewPoint;
        const directionLocal = vector(1, 0, 0);
        const directionWorld = shearToWorld.linear * directionLocal;
        const directionMagnitude = norm(directionWorld);
        
        if (directionMagnitude > TOLERANCE.zeroLength)
        {
            manipulators[SKEW_MANIPULATOR] = linearManipulator({
                        "base" : basePointWorld,
                        "direction" : directionWorld / directionMagnitude,
                        "offset" : definition.skewXY * yExtent,
                        "primaryParameterId" : "skewXY",
                        "style" : ManipulatorStyleEnum.SECONDARY
                    });
        }
    }

    if (size(manipulators) > 0)
    {
        addManipulators(context, id, manipulators);
    }
}

/**
 * Determine the anchor coordinate system used to evaluate transformations in local space.
 * 
 * @param context - The current context
 * @param definition - The feature definition containing the anchor selection
 * @param bodiesToTransform - Query for the bodies being transformed
 * @returns CoordSystem - The coordinate system to use as the transformation anchor
 * 
 * Defaults to the approximate centroid of the selected bodies when no anchor is provided.
 * If a mate connector is selected, uses its coordinate system.
 * If a vertex is selected, creates a world-aligned coordinate system at that point.
 */
function resolveAnchorCoordinateSystem(context is Context, definition is map, bodiesToTransform is Query) returns CoordSystem
{
    const anchorSelection = evaluateQuery(context, definition.anchor);
    
    if (size(anchorSelection) > 1)
    {
        throw regenError(ErrorStringEnum.TOO_MANY_ENTITIES_SELECTED, ["anchor"]);
    }

    if (size(anchorSelection) == 1)
    {
        const selectedAnchor = anchorSelection[0];
        
        // Try to evaluate as a mate connector first
        const mateSystem = try silent(evMateConnector(context, { "mateConnector" : selectedAnchor }));
        if (mateSystem != undefined)
        {
            return mateSystem;
        }

        // If not a mate connector, treat as a vertex and create a world-aligned coordinate system
        const anchorPoint = evVertexPoint(context, { "vertex" : selectedAnchor });
        return coordSystem(anchorPoint, vector(1, 0, 0), vector(0, 0, 1));
    }

    // No anchor provided - use the approximate centroid of the bodies as the origin
    const fallbackOrigin = evApproximateCentroid(context, { "entities" : bodiesToTransform });
    return coordSystem(fallbackOrigin, vector(1, 0, 0), vector(0, 0, 1));
}
