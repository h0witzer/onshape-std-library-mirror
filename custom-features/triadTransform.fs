FeatureScript 2815;
import(path : "onshape/std/common.fs", version : "2815.0");

//This tool should be illegal. If you need to move objects around in this manner you should be doing it at the assembly level
//Or you're dealing with some of my coworkers and need to prove a concept as fast and sloppy as possible
//Break Glass In Case Of Evan

// Imports used in interface
export import(path : "onshape/std/query.fs", version : "2815.0");
export import(path : "onshape/std/manipulator.fs", version : "2815.0");
export import(path : "onshape/std/tool.fs", version : "2815.0");

// Imports used internally
import(path : "onshape/std/box.fs", version : "2815.0");
import(path : "onshape/std/coordSystem.fs", version : "2815.0");
import(path : "onshape/std/evaluate.fs", version : "2815.0");
import(path : "onshape/std/feature.fs", version : "2815.0");
import(path : "onshape/std/topologyUtils.fs", version : "2815.0");
import(path : "onshape/std/transform.fs", version : "2815.0");
import(path : "onshape/std/valueBounds.fs", version : "2815.0");
import(path : "onshape/std/vector.fs", version : "2815.0");
import(path : "onshape/std/matrix.fs", version : "2815.0");
import(path : "onshape/std/math.fs", version : "2815.0");
import(path : "onshape/std/units.fs", version : "2815.0");

const TRIAD_MANIPULATOR = "triadManipulator";

predicate triadTransformPredicate(definition is map)
{
    annotation { "Name" : "Entities to transform",
                 "Filter" : (EntityType.BODY && ModifiableEntityOnly.YES && AllowMeshGeometry.YES && SketchObject.NO) || BodyType.MATE_CONNECTOR,
                 "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS }
    definition.entities is Query;

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

    annotation { "Name" : "Copy parts", "Default" : false }
    definition.copyParts is boolean;

    annotation { "Name" : "Advanced placement", "Default" : false }
    definition.useAdvancedPlacement is boolean;

    annotation { "Group Name" : "Advanced placement options", "Driving Parameter" : "useAdvancedPlacement", "Collapsed By Default" : false }
    {
        if (definition.useAdvancedPlacement)
        {
            annotation { "Name" : "Reference coordinate system", "Filter" : BodyType.MATE_CONNECTOR || (EntityType.VERTEX && SketchObject.NO), "MaxNumberOfPicks" : 1 }
            definition.referenceCoordSystem is Query;

            annotation { "Name" : "Enable geometry snapping", "Default" : false }
            definition.enableGeometrySnapping is boolean;

            if (definition.enableGeometrySnapping)
            {
                annotation { "Name" : "Reference entities", "Filter" : EntityType.BODY || EntityType.FACE || EntityType.EDGE }
                definition.referenceEntities is Query;
            }
        }
    }
}

/**
 * Adds a triad manipulator centered on the given coordinate system.
 * The manipulator displays rotation and translation controls that the user can interact with.
 * 
 * @param context {Context} : The context for the feature
 * @param id {Id} : The feature identifier
 * @param baseCSys {CoordSystem} : The base coordinate system for the manipulator
 * @param definition {map} : The feature definition containing current transform values
 */
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
        const baseCSys = getBaseCoordinateSystem(context, definition);

        addTriadManipulator(context, id, baseCSys, definition);

        const rotationMatrix = composeRotation(baseCSys,
            definition.rx, definition.ry, definition.rz);
        const localTransform = transform(rotationMatrix,
            vector(definition.dx, definition.dy, definition.dz));

        const worldTransform = toWorld(baseCSys) * localTransform * fromWorld(baseCSys);

        if (definition.copyParts)
        {
            try
            {
                opPattern(context, id, {
                            "entities" : qOwnerBody(definition.entities),
                            "transforms" : [worldTransform],
                            "instanceNames" : ["copy"]
                        });
            }
            catch (error)
            {
                if (error.message == (ErrorStringEnum.CANNOT_USE_MATECONNECTORS_IN_PATTERN as string))
                    throw regenError(ErrorStringEnum.CANNOT_COPY_MATECONNECTORS);
                throw error;
            }
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
            "rz" : 0 * degree,
            "useAdvancedPlacement" : false,
            "referenceCoordSystem" : qNothing(),
            "enableGeometrySnapping" : false,
            "referenceEntities" : qNothing()
        });

/**
 * Manipulator handler for triad transform feature.
 * Updates the definition based on manipulator movement.
 * When geometry snapping is enabled, snaps the manipulator origin to the closest point
 * on reference entities.
 * 
 * @param context {Context} : The context for the feature
 * @param definition {map} : The current feature definition
 * @param newManipulators {map} : The new manipulator state after user interaction
 * 
 * @returns {map} : Updated definition with new transform values
 */
export function triadTransformManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    if (newManipulators[TRIAD_MANIPULATOR] is map)
    {
        const manipulator = newManipulators[TRIAD_MANIPULATOR];
        var triadTransform = manipulator.transform;
        
        // If geometry snapping is enabled, snap the transform origin to reference entities
        if (definition.useAdvancedPlacement && 
            definition.enableGeometrySnapping)
        {
            const referenceEntitiesResolved = evaluateQuery(context, definition.referenceEntities);
            if (@size(referenceEntitiesResolved) > 0)
            {
                // Get the base coordinate system
                const baseCSys = getBaseCoordinateSystem(context, definition);
                
                // Calculate the world position of the manipulator
                const worldTransform = toWorld(baseCSys) * triadTransform;
                const manipulatorOrigin = worldTransform.translation;
                
                // When bodies are selected, snap to their surfaces (faces) rather than interior points
                // For faces and edges, use them directly
                var snapTargets = qUnion([
                    qEntityFilter(definition.referenceEntities, EntityType.FACE),
                    qEntityFilter(definition.referenceEntities, EntityType.EDGE),
                    qOwnedByBody(definition.referenceEntities, EntityType.FACE)
                ]);
                
                // Find the closest point on reference entity surfaces
                const distanceResult = evDistance(context, {
                    "side0" : manipulatorOrigin,
                    "side1" : snapTargets
                });
                
                // Snap to the closest point
                const snappedWorldPoint = distanceResult.sides[1].point;
                
                // Convert back to local coordinates
                const localSnappedPoint = fromWorld(baseCSys) * snappedWorldPoint;
                
                // Update the translation to snap to reference, preserving rotation
                triadTransform = transform(triadTransform.linear, localSnappedPoint);
            }
        }
        
        // Extract rotation and translation from the transform
        const rotation = transpose(triadTransform.linear);
        const angles = matrixToXYZAngles(rotation);
        definition.dx = triadTransform.translation[0];
        definition.dy = triadTransform.translation[1];
        definition.dz = triadTransform.translation[2];
        definition.rx = angles[0];
        definition.ry = angles[1];
        definition.rz = angles[2];
    }
    return definition;
}

/**
 * Composes a 3D rotation matrix from individual X, Y, and Z rotations.
 * Rotations are applied in the order: X, then Y, then Z.
 * 
 * @param baseCSys {CoordSystem} : The base coordinate system defining rotation axes
 * @param rx {ValueWithUnits} : Rotation angle around the X-axis
 * @param ry {ValueWithUnits} : Rotation angle around the Y-axis
 * @param rz {ValueWithUnits} : Rotation angle around the Z-axis
 * 
 * @returns {Matrix} : The composed 3x3 rotation matrix
 */
function composeRotation(baseCSys is CoordSystem, rx is ValueWithUnits, ry is ValueWithUnits, rz is ValueWithUnits) returns Matrix
{
    const rotX = rotationMatrix3d(baseCSys.xAxis, rx);
    const rotY = rotationMatrix3d(yAxis(baseCSys), ry);
    const rotZ = rotationMatrix3d(baseCSys.zAxis, rz);
    return rotZ * rotY * rotX;
}

/**
 * Converts a 3D rotation matrix to Euler angles (X-Y-Z convention).
 * Extracts the individual rotation angles from a composed rotation matrix.
 * 
 * @param linear {Matrix} : The 3x3 rotation matrix to decompose
 * 
 * @returns {Vector} : A 3D vector containing the rotation angles [rx, ry, rz] in radians
 */
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

/**
 * Determines the base coordinate system for the transform.
 * If a custom reference coordinate system is specified in definition.referenceCoordSystem,
 * uses that. Otherwise, defaults to the centroid of the selected entities with standard axes.
 * 
 * @param context {Context} : The context for the feature
 * @param definition {map} : The feature definition map containing entity selection and optional reference coordinate system
 * 
 * @returns {CoordSystem} : The coordinate system to use as the base for transformation
 */
function getBaseCoordinateSystem(context is Context, definition is map) returns CoordSystem
{
    const bodies = evaluateQuery(context, definition.entities);
    if (@size(bodies) == 0)
    {
        throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["entities"]);
    }
    
    // If advanced placement with custom reference coordinate system is specified, use that
    if (definition.useAdvancedPlacement && definition.referenceCoordSystem != undefined)
    {
        const referenceEntities = evaluateQuery(context, definition.referenceCoordSystem);
        if (@size(referenceEntities) > 0)
        {
            // Check if it's a mate connector
            const mateConnectorQuery = qBodyType(definition.referenceCoordSystem, BodyType.MATE_CONNECTOR);
            if (!isQueryEmpty(context, mateConnectorQuery))
            {
                return evMateConnector(context, {
                    "mateConnector" : definition.referenceCoordSystem
                });
            }
            else
            {
                // It's a vertex - create coordinate system at vertex location with standard axes
                const vertexPoint = evVertexPoint(context, {
                    "vertex" : definition.referenceCoordSystem
                });
                return coordSystem(vertexPoint, vector(1, 0, 0), vector(0, 0, 1));
            }
        }
    }
    
    // Default behavior: use centroid of selected entities
    const mateConnectors = qBodyType(definition.entities, BodyType.MATE_CONNECTOR);
    const regularBodies = qSubtraction(definition.entities, mateConnectors);
    const mateConnectorList = evaluateQuery(context, mateConnectors);
    const regularBodyList = evaluateQuery(context, regularBodies);

    // When exactly one mate connector is selected and no other bodies, use its full coordinate system
    if (@size(mateConnectorList) == 1 && @size(regularBodyList) == 0)
    {
        return evMateConnector(context, { "mateConnector" : mateConnectors });
    }

    const origin = findCenter(context, definition.entities);
    return coordSystem(origin, vector(1, 0, 0), vector(0, 0, 1));
}

/**
 * Calculates the center point (centroid) of the given entities.
 * For regular bodies, uses the bounding box center. For mate connectors,
 * uses their origin position. Returns the average of all collected positions.
 * 
 * @param context {Context} : The context for the feature
 * @param entities {Query} : The entities to find the center of
 * 
 * @returns {Vector} : The center point as a 3D vector with units
 */
function findCenter(context is Context, entities is Query) returns Vector
{
    const mateConnectors = qBodyType(entities, BodyType.MATE_CONNECTOR);
    const regularBodies = qSubtraction(entities, mateConnectors);
    const mateConnectorList = evaluateQuery(context, mateConnectors);
    const regularBodyList = evaluateQuery(context, regularBodies);

    var entityPositions = [];

    // Get origins from mate connectors via evMateConnector
    for (var mateConnector in mateConnectorList)
    {
        const coordSys = evMateConnector(context, { "mateConnector" : mateConnector });
        entityPositions = append(entityPositions, coordSys.origin);
    }

    // Get bounding box center from regular bodies
    if (@size(regularBodyList) > 0)
    {
        const boxResult = evBox3d(context, { 'topology' : regularBodies, 'tight' : false });
        entityPositions = append(entityPositions, box3dCenter(boxResult));
    }

    if (@size(entityPositions) == 0)
    {
        throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["entities"]);
    }

    var positionSum = entityPositions[0];
    for (var index = 1; index < @size(entityPositions); index += 1)
    {
        positionSum = positionSum + entityPositions[index];
    }
    return positionSum / @size(entityPositions);
}
