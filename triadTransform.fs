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
const GROUP_INDEX_BOUNDS = { (unitless) : [0, 0, 50] } as IntegerBoundSpec;

predicate transformGroupPredicate(group is map)
{
    annotation { "Name" : "Group index", "UIHint" : UIHint.ALWAYS_HIDDEN }
    isInteger(group.index, GROUP_INDEX_BOUNDS);

    annotation { "Name" : "Entities to transform", "Filter" : EntityType.BODY && ModifiableEntityOnly.YES && AllowMeshGeometry.YES && SketchObject.NO }
    group.entities is Query;

    annotation { "Name" : "X translation", "Group Name" : "Transform", "Collapsed By Default" : true }
    isLength(group.dx, ZERO_DEFAULT_LENGTH_BOUNDS);

    annotation { "Name" : "Y translation", "Group Name" : "Transform" }
    isLength(group.dy, ZERO_DEFAULT_LENGTH_BOUNDS);

    annotation { "Name" : "Z translation", "Group Name" : "Transform" }
    isLength(group.dz, ZERO_DEFAULT_LENGTH_BOUNDS);

    annotation { "Name" : "X rotation", "Group Name" : "Transform" }
    isAngle(group.rx, ANGLE_360_ZERO_DEFAULT_BOUNDS);

    annotation { "Name" : "Y rotation", "Group Name" : "Transform" }
    isAngle(group.ry, ANGLE_360_ZERO_DEFAULT_BOUNDS);

    annotation { "Name" : "Z rotation", "Group Name" : "Transform" }
    isAngle(group.rz, ANGLE_360_ZERO_DEFAULT_BOUNDS);
}

predicate transformGroupsPredicate(definition is map)
{
    annotation { "Name" : "Transforms", "Item name" : "group", "Item label template" : "Group #index", "UIHint" : UIHint.COLLAPSE_ARRAY_ITEMS }
    definition.groups is array;
    for (var group in definition.groups)
    {
        transformGroupPredicate(group);
    }
}

/** Add a triad manipulator centered on the given coordinate system. */
function addTriadManipulator(context is Context, id is Id,
        baseCSys is CoordSystem, definition is map, index is number)
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
    const manipName = TRIAD_MANIPULATOR ~ toString(index);
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
        transformGroupsPredicate(definition);
    }
    {
        for (var i = 0; i < size(definition.groups); i += 1)
        {
            const group = definition.groups[i];
            const subId = id + unstableIdComponent(i);
            const baseCSys = getBaseCoordinateSystem(context, subId, group);

            addTriadManipulator(context, id, baseCSys, group, i);

            const rotationMatrix = composeRotation(baseCSys,
                    group.rx, group.ry, group.rz);
            const localTransform = transform(rotationMatrix,
                    vector(group.dx, group.dy, group.dz));

            const worldTransform = toWorld(baseCSys) * localTransform * fromWorld(baseCSys);
            opTransform(context, subId, {
                        "bodies" : qOwnerBody(group.entities),
                        "transform" : worldTransform
                    });
        }
    }, {
            "groups" : [{
                    "index" : 0,
                    "entities" : qNothing(),
                    "dx" : 0 * millimeter,
                    "dy" : 0 * millimeter,
                    "dz" : 0 * millimeter,
                    "rx" : 0 * degree,
                    "ry" : 0 * degree,
                    "rz" : 0 * degree
                }]
        });

/**
 * Manipulator handler for triad transform feature.
 */
export function triadTransformManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{

    var groups = definition.groups;
    for (var key, manipulator in newManipulators)
    {

        if (startsWith(key, TRIAD_MANIPULATOR))
        {
            const index = stringToNumber(replace(key, TRIAD_MANIPULATOR, ""));
            const triadTransform = manipulator.transform;
            const rotation = transpose(triadTransform.linear);
            const angles = matrixToXYZAngles(rotation);
            groups[index].dx = triadTransform.translation[0];
            groups[index].dy = triadTransform.translation[1];
            groups[index].dz = triadTransform.translation[2];
            groups[index].rx = angles[0];
            groups[index].ry = angles[1];
            groups[index].rz = angles[2];
        }
    }
    definition.groups = groups;
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
