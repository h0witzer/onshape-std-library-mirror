FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");

//This tool should be illegal. If you need to move objects around in this manner you should be doing it at the assembly level
//Or you're dealing with some of my coworkers and need to prove a concept as fast and sloppy as possible
//Break Glass In Case Of Evan

// Imports used in interface
export import(path : "onshape/std/query.fs", version : "2679.0");
export import(path : "onshape/std/manipulator.fs", version : "2679.0");
export import(path : "onshape/std/tool.fs", version : "2679.0");

// Imports used internally
import(path : "onshape/std/box.fs", version : "2679.0");
import(path : "onshape/std/coordSystem.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/feature.fs", version : "2679.0");
import(path : "onshape/std/topologyUtils.fs", version : "2679.0");
import(path : "onshape/std/transform.fs", version : "2679.0");
import(path : "onshape/std/valueBounds.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/matrix.fs", version : "2679.0");
import(path : "onshape/std/math.fs", version : "2679.0");
import(path : "onshape/std/units.fs", version : "2679.0");

const TRIAD_MANIPULATOR = "triadManipulator";

predicate triadTransformPredicate(definition is map)
{
    annotation { "Name" : "Entities to transform", "Filter" : EntityType.BODY && ModifiableEntityOnly.YES && AllowMeshGeometry.YES && SketchObject.NO }
    definition.entities is Query;

    annotation { "Name" : "Copy parts", "Default" : false }
    definition.copyParts is boolean;

    annotation { "Name" : "X translation", "Group Name" : "Transform", "Collapsed By Default" : true }
    isLength(definition.dx, ZERO_DEFAULT_LENGTH_BOUNDS);

    annotation { "Name" : "Y translation", "Group Name" : "Transform" }
    isLength(definition.dy, ZERO_DEFAULT_LENGTH_BOUNDS);

    annotation { "Name" : "Z translation", "Group Name" : "Transform" }
    isLength(definition.dz, ZERO_DEFAULT_LENGTH_BOUNDS);

    annotation { "Name" : "X rotation", "Group Name" : "Transform" }
    isAngle(definition.rx, ANGLE_360_ZERO_DEFAULT_BOUNDS);

    annotation { "Name" : "Y rotation", "Group Name" : "Transform" }
    isAngle(definition.ry, ANGLE_360_ZERO_DEFAULT_BOUNDS);

    annotation { "Name" : "Z rotation", "Group Name" : "Transform" }
    isAngle(definition.rz, ANGLE_360_ZERO_DEFAULT_BOUNDS);
}

/** Add a triad manipulator centered on the given coordinate system. */
function addTriadManipulator(context is Context, id is Id,
        baseCSys is CoordSystem, definition is map)
{
    const rotation = composeRotation(baseCSys,
            definition.rx, definition.ry, definition.rz);
    const triadTransform = transform(rotation,
            vector(definition.dx, definition.dy, definition.dz));

    const triadManip = fullTriadManipulator({
                "base" : baseCSys,
                "transform" : triadTransform,
                "displayEditView" : true
            });
    addManipulators(context, id, { (TRIAD_MANIPULATOR) : triadManip });
    // Also add with indexed name for compatibility with sphere drag
    const manipName = TRIAD_MANIPULATOR ~ "0";
    addManipulators(context, id, { (manipName) : triadManip });
}

/**
 * Simple transform tool using a full triad manipulator. Allows translation
 * and rotation in one feature. By default transformation is performed about
 * the centroid of the selected bodies.
 */
annotation { "Feature Type Name" : "Triad transform",
        "Manipulator Change Function" : "triadTransformManipulatorChange",
        "Filter Selector" : "allparts" }
export const triadTransform = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        triadTransformPredicate(definition);
    }
    {
        const baseCSys = getBaseCoordinateSystem(context, id, definition);
        
        addTriadManipulator(context, id, baseCSys, definition);

        const rotationMatrix = composeRotation(baseCSys,
                definition.rx, definition.ry, definition.rz);
        const localTransform = transform(rotationMatrix,
                vector(definition.dx, definition.dy, definition.dz));

        const worldTransform = toWorld(baseCSys) * localTransform * fromWorld(baseCSys);
        
        if (definition.copyParts)
        {
            opPattern(context, id, {
                        "entities" : qOwnerBody(definition.entities),
                        "transforms" : [worldTransform],
                        "instanceNames" : ["copy"]
                    });
        }
        else
        {
            opTransform(context, id, {
                        "bodies" : qOwnerBody(definition.entities),
                        "transform" : worldTransform
                    });
        }
    }, {
            "entities" : qNothing(),
            "copyParts" : false,
            "dx" : 0 * millimeter,
            "dy" : 0 * millimeter,
            "dz" : 0 * millimeter,
            "rx" : 0 * degree,
            "ry" : 0 * degree,
            "rz" : 0 * degree
        });

/**
 * Manipulator handler for triad transform feature.
 */
export function triadTransformManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    // Iterate through all manipulators to find the triad manipulator
    // (this handles potential key variations for different drag types)
    for (var key, manipulator in newManipulators)
    {
        if (key == TRIAD_MANIPULATOR || startsWith(key, TRIAD_MANIPULATOR))
        {
            const triadTransform = manipulator.transform;
            
            // Update translation - handles all drag types including REPOSITION
            const dx = triadTransform.translation[0];
            const dy = triadTransform.translation[1];
            const dz = triadTransform.translation[2];
            
            // Update rotation - extract angles from the rotation matrix
            const rotation = transpose(triadTransform.linear);
            const angles = matrixToXYZAngles(rotation);
            const rx = angles[0];
            const ry = angles[1];
            const rz = angles[2];
            
            // Assign all values to definition
            definition.dx = dx;
            definition.dy = dy;
            definition.dz = dz;
            definition.rx = rx;
            definition.ry = ry;
            definition.rz = rz;
        }
    }
    return definition;
}

function composeRotation(baseCSys is CoordSystem, rx is ValueWithUnits, ry is ValueWithUnits, rz is ValueWithUnits) returns Matrix
{
    const rotX = rotationMatrix3d(baseCSys.xAxis, rx);
    const rotY = rotationMatrix3d(yAxis(baseCSys), ry);
    const rotZ = rotationMatrix3d(baseCSys.zAxis, rz);
    return rotZ * rotY * rotX;
}

function matrixToXYZAngles(linear is Matrix) returns Vector
{
    const sy = sqrt(linear[0][0] * linear[0][0] + linear[1][0] * linear[1][0]);
    var x;
    var y;
    var z;
    if (sy > 1e-6)
    {
        x = atan2(linear[2][1], linear[2][2]);
        y = atan2(-linear[2][0], sy);
        z = atan2(linear[1][0], linear[0][0]);
    }
    else
    {
        x = atan2(-linear[1][2], linear[1][1]);
        y = atan2(-linear[2][0], sy);
        z = 0 * radian;
    }
    return vector(x, y, z);
}

function getBaseCoordinateSystem(context is Context, id is Id, definition is map) returns CoordSystem
{
    const bodies = evaluateQuery(context, definition.entities);
    if (@size(bodies) == 0)
    {
        throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["entities"]);
    }
    const origin = findCenter(context, id, definition.entities);
    return coordSystem(origin, vector(1, 0, 0), vector(0, 0, 1));
}

function findCenter(context is Context, id is Id, entities is Query) returns Vector
{
    const boxResult = evBox3d(context, { 'topology' : entities, 'tight' : false });
    return box3dCenter(boxResult);
}
