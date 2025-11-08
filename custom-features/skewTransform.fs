FeatureScript 2796;

import(path : "onshape/std/common.fs", version : "2796.0");
import(path : "onshape/std/coordSystem.fs", version : "2796.0");
import(path : "onshape/std/debug.fs", version : "2796.0");
import(path : "onshape/std/error.fs", version : "2796.0");
import(path : "onshape/std/evaluate.fs", version : "2796.0");
import(path : "onshape/std/feature.fs", version : "2796.0");
import(path : "onshape/std/manipulator.fs", version : "2796.0");
import(path : "onshape/std/matrix.fs", version : "2796.0");
import(path : "onshape/std/math.fs", version : "2796.0");
import(path : "onshape/std/query.fs", version : "2796.0");
import(path : "onshape/std/transform.fs", version : "2796.0");
import(path : "onshape/std/units.fs", version : "2796.0");
import(path : "onshape/std/valueBounds.fs", version : "2796.0");
import(path : "onshape/std/vector.fs", version : "2796.0");

const SHEAR_PARAMETER_BOUNDS =
{ 
    (unitless) : [-5, 0, 5]
} as RealBoundSpec;

const SHEAR_MANIPULATOR_DATA = [
        { "parameter" : "xyShear", "id" : "shearXByY", "directionIndex" : 0, "normalIndex" : 1 },
        { "parameter" : "xzShear", "id" : "shearXByZ", "directionIndex" : 0, "normalIndex" : 2 },
        { "parameter" : "yxShear", "id" : "shearYByX", "directionIndex" : 1, "normalIndex" : 0 },
        { "parameter" : "yzShear", "id" : "shearYByZ", "directionIndex" : 1, "normalIndex" : 2 },
        { "parameter" : "zxShear", "id" : "shearZByX", "directionIndex" : 2, "normalIndex" : 0 },
        { "parameter" : "zyShear", "id" : "shearZByY", "directionIndex" : 2, "normalIndex" : 1 }
    ];

const BOUNDING_BOX_EDGE_PAIRS = [
        [0, 1], [0, 2], [0, 4],
        [1, 3], [1, 5],
        [2, 3], [2, 6],
        [3, 7],
        [4, 5], [4, 6],
        [5, 7],
        [6, 7]
    ];

annotation { "Feature Type Name" : "Skew transform",
        "Manipulator Change Function" : "skewTransformManipulatorChange",
        "Editing Logic Function" : "skewTransformEditLogic",
        "Filter Selector" : "allparts" }
export const skewTransform = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Entities", "Filter" : EntityType.BODY && ModifiableEntityOnly.YES && AllowMeshGeometry.YES && SketchObject.NO }
        definition.targets is Query;

        annotation { "Name" : "Anchor", "Filter" : BodyType.MATE_CONNECTOR || (EntityType.VERTEX && SketchObject.NO), "MaxNumberOfPicks" : 1 }
        definition.anchor is Query;

        annotation { "Group Name" : "Stretch", "Name" : "Scale X" }
        isReal(definition.xScale, SCALE_BOUNDS);

        annotation { "Group Name" : "Stretch", "Name" : "Scale Y" }
        isReal(definition.yScale, SCALE_BOUNDS);

        annotation { "Group Name" : "Stretch", "Name" : "Scale Z" }
        isReal(definition.zScale, SCALE_BOUNDS);

        annotation { "Group Name" : "Shear", "Name" : "Shear X by Y" }
        isReal(definition.xyShear, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Shear", "Name" : "Shear X by Z" }
        isReal(definition.xzShear, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Shear", "Name" : "Shear Y by X" }
        isReal(definition.yxShear, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Shear", "Name" : "Shear Y by Z" }
        isReal(definition.yzShear, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Shear", "Name" : "Shear Z by X" }
        isReal(definition.zxShear, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Shear", "Name" : "Shear Z by Y" }
        isReal(definition.zyShear, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Shear", "Name" : "Reset all skews" }
        isButton(definition.resetSkews);
    }
    {
        const bodiesToTransform = qOwnerBody(definition.targets);
        if (isQueryEmpty(context, bodiesToTransform))
        {
            throw regenError("Select at least one body to transform", ["targets"]);
        }

        const anchorSystem = resolveAnchorCoordinateSystem(context, definition, bodiesToTransform);
        const boundingBox = evBox3d(context, { "topology" : bodiesToTransform, "cSys" : anchorSystem, "tight" : false });
        const transformExtents = boundingBox.maxCorner - boundingBox.minCorner;

        const localLinear = matrix([
                [definition.xScale, definition.xyShear, definition.xzShear],
                [definition.yxShear, definition.yScale, definition.yzShear],
                [definition.zxShear, definition.zyShear, definition.zScale]
            ]);
        const localTransform = transform(localLinear, vector(0, 0, 0) * meter);
        const shearToWorld = toWorld(anchorSystem) * localTransform;

        addShearManipulators(context, id, definition, boundingBox, transformExtents, shearToWorld);

        displayShearedBoundingBox(context, boundingBox, shearToWorld);

        const worldTransform = toWorld(anchorSystem) * localTransform * fromWorld(anchorSystem);

        opTransform(context, id + "transform", {
                    "bodies" : bodiesToTransform,
                    "transform" : worldTransform
                });
    }, {
            "targets" : qNothing(),
            "anchor" : qNothing(),
            "xScale" : 1,
            "yScale" : 1,
            "zScale" : 1,
            "xyShear" : 0,
            "xzShear" : 0,
            "yxShear" : 0,
            "yzShear" : 0,
            "zxShear" : 0,
            "zyShear" : 0
        });

/**
 * Manipulator change handler that maps planar shear manipulators back to parameter values.
 */
export function skewTransformManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    const bodiesToTransform = qOwnerBody(definition.targets);
    if (isQueryEmpty(context, bodiesToTransform))
    {
        return definition;
    }

    const anchorSystem = resolveAnchorCoordinateSystem(context, definition, bodiesToTransform);
    const boundingBox = evBox3d(context, { "topology" : bodiesToTransform, "cSys" : anchorSystem, "tight" : false });
    const transformExtents = boundingBox.maxCorner - boundingBox.minCorner;

    for (var descriptor in SHEAR_MANIPULATOR_DATA)
    {
        const manipulator = newManipulators[descriptor.id];
        if (!(manipulator is map))
        {
            continue;
        }

        const relevantExtent = transformExtents[descriptor.normalIndex];
        if (relevantExtent.value <= TOLERANCE.zeroLength)
        {
            continue;
        }

        definition[descriptor.parameter] = manipulator.offset / relevantExtent;
    }

    return definition;
}

/**
 * Handle clicks from UI buttons including the reset control for shear parameters.
 */
export function skewTransformEditLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, clickedButton is string) returns map
{
    if (clickedButton == "resetSkews")
    {
        definition.xyShear = 0;
        definition.xzShear = 0;
        definition.yxShear = 0;
        definition.yzShear = 0;
        definition.zxShear = 0;
        definition.zyShear = 0;
    }

    return definition;
}

/**
 * Determine the anchor coordinate system used to evaluate shear in local space. Defaults to the centroid of the selected bodies when no anchor is provided.
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
        const mateSystem = try silent(evMateConnector(context, { "mateConnector" : selectedAnchor }));
        if (mateSystem != undefined)
        {
            return mateSystem;
        }

        const anchorPoint = evVertexPoint(context, { "vertex" : selectedAnchor });
        return coordSystem(anchorPoint, vector(1, 0, 0), vector(0, 1, 0));
    }

    const fallbackOrigin = evApproximateCentroid(context, { "entities" : bodiesToTransform });
    return coordSystem(fallbackOrigin, vector(1, 0, 0), vector(0, 1, 0));
}

/**
 * Place shear manipulators on the transformed bounding box faces and align them with the skewed axes.
 */
function addShearManipulators(context is Context, id is Id, definition is map, boundingBox is Box3d, transformExtents is Vector, shearToWorld is Transform)
{
    var manipulators = {} as map;
    var addedManipulator = false;

    for (var descriptor in SHEAR_MANIPULATOR_DATA)
    {
        const relevantExtent = transformExtents[descriptor.normalIndex];
        if (relevantExtent.value <= TOLERANCE.zeroLength)
        {
            continue;
        }

        var facePoint = vector(0, 0, 0);
        for (var axis = 0; axis < 3; axis += 1)
        {
            if (axis == descriptor.normalIndex)
            {
                facePoint[axis] = boundingBox.maxCorner[axis];
            }
            else
            {
                facePoint[axis] = 0.5 * (boundingBox.minCorner[axis] + boundingBox.maxCorner[axis]);
            }
        }

        var directionLocal = vector(0, 0, 0);
        directionLocal[descriptor.directionIndex] = 1;

        const basePointWorld = shearToWorld * facePoint;
        const directionWorld = shearToWorld.linear * directionLocal;
        const directionMagnitude = norm(directionWorld);
        if (directionMagnitude <= TOLERANCE.zeroLength)
        {
            continue;
        }

        const manipulatorId = descriptor.id;
        manipulators[manipulatorId] = linearManipulator({
                    "base" : basePointWorld,
                    "direction" : directionWorld / directionMagnitude,
                    "offset" : definition[descriptor.parameter] * relevantExtent,
                    "primaryParameterId" : descriptor.parameter,
                    "style" : ManipulatorStyleEnum.SECONDARY
                });
        addedManipulator = true;
    }

    if (addedManipulator)
    {
        addManipulators(context, id, manipulators);
    }
}

/**
 * Draw the transformed bounding volume after applying the local shear and stretch transform.
 */
function displayShearedBoundingBox(context is Context, boundingBox is Box3d, shearToWorld is Transform)
{
    const minCorner = boundingBox.minCorner;
    const maxCorner = boundingBox.maxCorner;

    const localCorners = [
            vector(minCorner[0], minCorner[1], minCorner[2]),
            vector(minCorner[0], minCorner[1], maxCorner[2]),
            vector(minCorner[0], maxCorner[1], minCorner[2]),
            vector(minCorner[0], maxCorner[1], maxCorner[2]),
            vector(maxCorner[0], minCorner[1], minCorner[2]),
            vector(maxCorner[0], minCorner[1], maxCorner[2]),
            vector(maxCorner[0], maxCorner[1], minCorner[2]),
            vector(maxCorner[0], maxCorner[1], maxCorner[2])
        ];

    var worldCorners = [] as array;
    for (var corner in localCorners)
    {
        worldCorners = append(worldCorners, shearToWorld * corner);
    }

    for (var edgePair in BOUNDING_BOX_EDGE_PAIRS)
    {
        const startCorner = worldCorners[edgePair[0]];
        const endCorner = worldCorners[edgePair[1]];
        addDebugLine(context, startCorner, endCorner, DebugColor.CYAN);
    }
}
