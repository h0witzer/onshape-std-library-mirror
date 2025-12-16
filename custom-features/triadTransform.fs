FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

//This tool should be illegal. If you need to move objects around in this manner you should be doing it at the assembly level
//Or you're dealing with some of my coworkers and need to prove a concept as fast and sloppy as possible
//Break Glass In Case Of Evan

// Imports used in interface
export import(path : "onshape/std/query.fs", version : "2837.0");
export import(path : "onshape/std/manipulator.fs", version : "2837.0");
export import(path : "onshape/std/tool.fs", version : "2837.0");

// Imports used internally
import(path : "onshape/std/box.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/topologyUtils.fs", version : "2837.0");
import(path : "onshape/std/transform.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/matrix.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");

const TRIAD_MANIPULATOR = "triadManipulator";
const INSTANCE_MANIPULATOR = "instanceManipulator";

/**
 * Creates a new empty instance with default transform values.
 * Returns a fresh object to avoid reference sharing issues.
 * 
 * @returns {map} : New instance object with default values
 */
function createEmptyInstance() returns map
{
    return {
        "index" : 0,
        "instanceDx" : 0 * millimeter,
        "instanceDy" : 0 * millimeter,
        "instanceDz" : 0 * millimeter,
        "instanceRx" : 0 * degree,
        "instanceRy" : 0 * degree,
        "instanceRz" : 0 * degree,
        "rotationMatrix" : identityMatrix(3),
        "arrayAdded" : false
    };
}

predicate triadTransformPredicate(definition is map)
{
    annotation { "Name" : "Entities to transform", "Filter" : EntityType.BODY && ModifiableEntityOnly.YES && AllowMeshGeometry.YES && SketchObject.NO }
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
    
    annotation { "Name" : "Multi-copy mode", "Default" : false }
    definition.multiCopyMode is boolean;
    
    if (definition.multiCopyMode)
    {
        annotation { "Name" : "Selected instance", "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.UNCONFIGURABLE] }
        isInteger(definition.instanceIndex, { (unitless) : [-1, -1, 10000] } as IntegerBoundSpec);
        
        annotation { "Name" : "Instances", "Item name" : "instance", "Item label template" : "Instance #index", "UIHint" : UIHint.PREVENT_ARRAY_REORDER }
        definition.instances is array;
        for (var instance in definition.instances)
        {
            annotation { "Name" : "Instance index", "UIHint" : [UIHint.ALWAYS_HIDDEN] }
            isInteger(instance.index, { (unitless) : [0, 0, 10000] } as IntegerBoundSpec);
            
            annotation { "Name" : "X translation" }
            isLength(instance.instanceDx, ZERO_DEFAULT_LENGTH_BOUNDS);
            
            annotation { "Name" : "Y translation" }
            isLength(instance.instanceDy, ZERO_DEFAULT_LENGTH_BOUNDS);
            
            annotation { "Name" : "Z translation" }
            isLength(instance.instanceDz, ZERO_DEFAULT_LENGTH_BOUNDS);
            
            annotation { "Name" : "X rotation" }
            isAngle(instance.instanceRx, ANGLE_360_ZERO_DEFAULT_BOUNDS);
            
            annotation { "Name" : "Y rotation" }
            isAngle(instance.instanceRy, ANGLE_360_ZERO_DEFAULT_BOUNDS);
            
            annotation { "Name" : "Z rotation" }
            isAngle(instance.instanceRz, ANGLE_360_ZERO_DEFAULT_BOUNDS);
            
            annotation { "Name" : "Rotation matrix", "UIHint" : UIHint.ALWAYS_HIDDEN }
            isAnything(instance.rotationMatrix);
            
            annotation { "Name" : "Added by the array", "Default" : true, "UIHint" : UIHint.ALWAYS_HIDDEN }
            instance.arrayAdded is boolean;
        }
    }

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
 * In multi-copy mode, also adds point manipulators for each placed instance.
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
    
    // In multi-copy mode, add point manipulators for the current position and all placed instances
    if (definition.multiCopyMode)
    {
        addInstanceManipulators(context, id, baseCSys, definition);
    }
}

/**
 * Adds point manipulators for all placed instances in multi-copy mode.
 * Each point represents a placed copy that can be clicked to select/modify.
 * The first point (index 0) represents the current manipulator position (primary).
 * Points are added in order by their index field to ensure correct mapping.
 * 
 * @param context {Context} : The context for the feature
 * @param id {Id} : The feature identifier
 * @param baseCSys {CoordSystem} : The base coordinate system for the manipulator
 * @param definition {map} : The feature definition containing instances array
 */
function addInstanceManipulators(context is Context, id is Id, 
    baseCSys is CoordSystem, definition is map)
{
    var instancePositions = [];
    
    // First, add the current manipulator position (primary instance at point index 0)
    const currentRotation = composeRotation(baseCSys, definition.rx, definition.ry, definition.rz);
    const currentTransform = transform(currentRotation, vector(definition.dx, definition.dy, definition.dz));
    const currentWorldTransform = toWorld(baseCSys) * currentTransform;
    instancePositions = append(instancePositions, currentWorldTransform.translation);
    
    // Then add all saved instances, ordered by their index field
    // Point manipulator index = instance.index + 1 (since 0 is reserved for primary)
    const numInstances = @size(definition.instances);
    if (numInstances > 0)
    {
        // Find the maximum index to determine how many positions we need
        var maxIndex = -1;
        for (var instance in definition.instances)
        {
            if (instance.index > maxIndex)
            {
                maxIndex = instance.index;
            }
        }
        
        // Create array with positions for each index (may have gaps if instances were deleted)
        for (var targetIndex = 0; targetIndex <= maxIndex; targetIndex += 1)
        {
            // Find the instance with this index
            var foundInstance = undefined;
            for (var instance in definition.instances)
            {
                if (instance.index == targetIndex)
                {
                    foundInstance = instance;
                    break;
                }
            }
            
            if (foundInstance != undefined)
            {
                const rotation = composeRotation(baseCSys, foundInstance.instanceRx, foundInstance.instanceRy, foundInstance.instanceRz);
                const instanceTransform = transform(rotation, vector(foundInstance.instanceDx, foundInstance.instanceDy, foundInstance.instanceDz));
                const worldTransform = toWorld(baseCSys) * instanceTransform;
                instancePositions = append(instancePositions, worldTransform.translation);
            }
        }
    }
    
    const pointManip = pointsManipulator({
                "points" : instancePositions,
                "index" : -1
            });
    addManipulators(context, id, { (INSTANCE_MANIPULATOR) : pointManip });
}

/**
 * Simple transform tool using a full triad manipulator. Allows translation
 * and rotation in one feature. By default transformation is performed about
 * the centroid of the selected bodies.
 */
annotation { "Feature Type Name" : "Triad transform",
        "Manipulator Change Function" : "triadTransformManipulatorChange",
        "Editing Logic Function" : "triadTransformEditLogic",
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

        if (definition.multiCopyMode)
        {
            // In multi-copy mode, apply current manipulator position plus all saved instance transforms
            var transforms = [];
            var instanceNames = [];
            
            // First, apply the current manipulator position (index 0 / "primary")
            transforms = append(transforms, worldTransform);
            instanceNames = append(instanceNames, "primary");
            
            // Then apply all saved instances
            for (var instanceIndex = 0; instanceIndex < @size(definition.instances); instanceIndex += 1)
            {
                const instance = definition.instances[instanceIndex];
                const instanceRotation = composeRotation(baseCSys, instance.instanceRx, instance.instanceRy, instance.instanceRz);
                const instanceLocalTransform = transform(instanceRotation, vector(instance.instanceDx, instance.instanceDy, instance.instanceDz));
                const instanceWorldTransform = toWorld(baseCSys) * instanceLocalTransform * fromWorld(baseCSys);
                transforms = append(transforms, instanceWorldTransform);
                instanceNames = append(instanceNames, "copy" ~ toString(instanceIndex + 1));
            }
            
            opPattern(context, id, {
                        "entities" : qOwnerBody(definition.entities),
                        "transforms" : transforms,
                        "instanceNames" : instanceNames
                    });
        }
        else if (definition.copyParts)
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
            "multiCopyMode" : false,
            "instances" : [],
            "instanceIndex" : -1,  // -1 means primary (current manipulator position)
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
 * In multi-copy mode, also handles instance selection via point manipulator clicks.
 * 
 * @param context {Context} : The context for the feature
 * @param definition {map} : The current feature definition
 * @param newManipulators {map} : The new manipulator state after user interaction
 * 
 * @returns {map} : Updated definition with new transform values
 */
export function triadTransformManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    // Handle instance selection in multi-copy mode
    if (newManipulators[INSTANCE_MANIPULATOR] is map)
    {
        const clickedIndex = newManipulators[INSTANCE_MANIPULATOR].index;
        // Point manipulator index 0 is the primary position
        // Point manipulator index 1+ corresponds to instance.index (clickedIndex - 1)
        if (clickedIndex == 0)
        {
            // Clicking the primary position - no need to load anything, already in manipulator
            // Just ensure instanceIndex reflects this (use -1 to indicate primary)
            definition.instanceIndex = -1;
        }
        else if (clickedIndex > 0)
        {
            // Clicking a saved instance - instanceIndex should be (clickedIndex - 1)
            // This maps point manipulator index directly to the instance's index field
            definition.instanceIndex = clickedIndex - 1;
        }
        return definition;
    }
    
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
        
        // If in multi-copy mode and a saved instance is selected, also update that instance
        // (instanceIndex >= 0 means a saved instance is selected, -1 means primary position)
        if (definition.multiCopyMode && @size(definition.instances) > 0 && definition.instanceIndex >= 0)
        {
            for (var instanceArrayIndex = 0; instanceArrayIndex < @size(definition.instances); instanceArrayIndex += 1)
            {
                if (definition.instances[instanceArrayIndex].index == definition.instanceIndex)
                {
                    definition.instances[instanceArrayIndex].instanceDx = definition.dx;
                    definition.instances[instanceArrayIndex].instanceDy = definition.dy;
                    definition.instances[instanceArrayIndex].instanceDz = definition.dz;
                    definition.instances[instanceArrayIndex].instanceRx = definition.rx;
                    definition.instances[instanceArrayIndex].instanceRy = definition.ry;
                    definition.instances[instanceArrayIndex].instanceRz = definition.rz;
                    definition.instances[instanceArrayIndex].rotationMatrix = rotation;
                    break;
                }
            }
        }
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
    const origin = findCenter(context, definition.entities);
    return coordSystem(origin, vector(1, 0, 0), vector(0, 0, 1));
}

/**
 * Calculates the center point (centroid) of the bounding box for the given entities.
 * 
 * @param context {Context} : The context for the feature
 * @param entities {Query} : The entities to find the center of
 * 
 * @returns {Vector} : The center point as a 3D vector with units
 */
function findCenter(context is Context, entities is Query) returns Vector
{
    const boxResult = evBox3d(context, { 'topology' : entities, 'tight' : false });
    return box3dCenter(boxResult);
}

/**
 * Edit logic function for triad transform feature.
 * Handles button clicks for placing copies in multi-copy mode and manages the instances array.
 * 
 * @param context {Context} : The context for the feature
 * @param id {Id} : The feature identifier
 * @param oldDefinition {map} : The previous feature definition
 * @param definition {map} : The current feature definition
 * @param isCreating {boolean} : Whether this is the initial creation of the feature
 * @param specifiedParameters {map} : Parameters that were explicitly set by the user
 * @param hiddenQueries {Query} : Hidden queries in the context
 * @param clickedButton {string} : Name of the button that was clicked, if any
 * 
 * @returns {map} : Updated definition after processing edit logic
 */
export function triadTransformEditLogic(context is Context, id is Id, oldDefinition is map, definition is map, 
    isCreating is boolean, specifiedParameters is map, hiddenQueries is Query, clickedButton is string) returns map
{
    // Handle switching to multi-copy mode for the first time
    if (definition.multiCopyMode && !oldDefinition.multiCopyMode)
    {
        // Initialize empty instances array if needed
        if (definition.instances == undefined)
        {
            definition.instances = [];
        }
        definition.instanceIndex = -1;  // Start with primary position selected
    }
    
    // Step 1: Process instances added via the array UI button
    // When the user clicks "Add" in the array, initialize new instances with current manipulator data
    if (definition.multiCopyMode && @size(definition.instances) > 0)
    {
        const baseCSys = getBaseCoordinateSystem(context, definition);
        const rotation = composeRotation(baseCSys, definition.rx, definition.ry, definition.rz);
        
        for (var instanceArrayIndex = 0; instanceArrayIndex < @size(definition.instances); instanceArrayIndex += 1)
        {
            if (definition.instances[instanceArrayIndex].arrayAdded)
            {
                // Initialize this array-added instance with current manipulator transform
                definition.instances[instanceArrayIndex].instanceDx = definition.dx;
                definition.instances[instanceArrayIndex].instanceDy = definition.dy;
                definition.instances[instanceArrayIndex].instanceDz = definition.dz;
                definition.instances[instanceArrayIndex].instanceRx = definition.rx;
                definition.instances[instanceArrayIndex].instanceRy = definition.ry;
                definition.instances[instanceArrayIndex].instanceRz = definition.rz;
                definition.instances[instanceArrayIndex].rotationMatrix = transpose(rotation);
                definition.instances[instanceArrayIndex].arrayAdded = false;
            }
        }
    }
    
    // Step 2: Handle instance array management BEFORE selection loading
    // This ensures indices are correct before we try to load by index
    if (definition.multiCopyMode && @size(definition.instances) > 0)
    {
        const numInstances = @size(definition.instances);
        var currentIndicesOrder = makeArray(numInstances);
        for (var instanceArrayIndex = 0; instanceArrayIndex < numInstances; instanceArrayIndex += 1)
        {
            currentIndicesOrder[instanceArrayIndex] = definition.instances[instanceArrayIndex].index;
        }
        
        // Check if indices need reordering (due to deletion or other array changes)
        // First deduplicate any duplicate indices, then shift to remove gaps
        const deduplicatedIndices = deduplicateIndicesForInstances(currentIndicesOrder);
        const newIndicesOrder = shiftIndicesForInstances(deduplicatedIndices);
        if (currentIndicesOrder != newIndicesOrder)
        {
            // Update indices to maintain consistency
            for (var instanceArrayIndex = 0; instanceArrayIndex < numInstances; instanceArrayIndex += 1)
            {
                definition.instances[instanceArrayIndex].index = newIndicesOrder[instanceArrayIndex];
                // Update the array item label to show the correct index
                setFeatureComputedParameter(context, id, {
                            "name" : "instances[" ~ toString(instanceArrayIndex) ~ "].index",
                            "value" : newIndicesOrder[instanceArrayIndex]
                        });
            }
        }
        
        // Ensure instanceIndex is valid
        // -1 is valid (means primary position), 0 to numInstances-1 are valid saved instances
        if (definition.instanceIndex >= numInstances)
        {
            definition.instanceIndex = numInstances - 1;
        }
        if (definition.instanceIndex < -1)
        {
            definition.instanceIndex = -1;  // Reset to primary if invalid
        }
        
        // Hide all instances in the array except the currently selected one
        // If instanceIndex is -1 (primary), hide all instances
        var hiddenIds = [];
        for (var instanceArrayIndex = 0; instanceArrayIndex < numInstances; instanceArrayIndex += 1)
        {
            if (definition.instanceIndex == -1 || definition.instances[instanceArrayIndex].index != definition.instanceIndex)
            {
                hiddenIds = append(hiddenIds, "instances[" ~ toString(instanceArrayIndex) ~ "]");
            }
        }
        setFeatureHiddenParameters(context, id, hiddenIds);
    }
    
    // Step 3: Handle instance selection change - load the selected instance's transform to the manipulator
    // This happens AFTER index management to ensure we're working with correct indices
    if (definition.multiCopyMode && 
        oldDefinition.multiCopyMode && 
        oldDefinition.instanceIndex != definition.instanceIndex &&
        @size(definition.instances) > 0 &&
        definition.instanceIndex >= 0)  // Only load if not selecting primary (which is -1)
    {
        // Find the array index for the selected instance
        for (var instanceArrayIndex = 0; instanceArrayIndex < @size(definition.instances); instanceArrayIndex += 1)
        {
            if (definition.instances[instanceArrayIndex].index == definition.instanceIndex)
            {
                // Load this instance's transform into the main manipulator
                definition.dx = definition.instances[instanceArrayIndex].instanceDx;
                definition.dy = definition.instances[instanceArrayIndex].instanceDy;
                definition.dz = definition.instances[instanceArrayIndex].instanceDz;
                definition.rx = definition.instances[instanceArrayIndex].instanceRx;
                definition.ry = definition.instances[instanceArrayIndex].instanceRy;
                definition.rz = definition.instances[instanceArrayIndex].instanceRz;
                break;
            }
        }
    }
    
    return definition;
}

/**
 * Shifts indices to remove gaps, similar to routingCurve's shiftIndices.
 * Ensures indices are sequential starting from 0.
 * 
 * @param indices {array} : Array of unique index values
 * 
 * @returns {array} : Array with indices shifted to remove gaps
 */
function shiftIndicesForInstances(indices is array) returns array
{
    const numIndices = @size(indices);
    const sortedIndices = sort(indices, function(a, b)
        {
            return a - b;
        });
    var missingIndices = [];
    var expectedIndex = 0;
    for (var index in sortedIndices)
    {
        while (index > expectedIndex)
        {
            missingIndices = append(missingIndices, expectedIndex);
            expectedIndex += 1;
        }
        expectedIndex += 1;
    }
    const numMissingIndices = @size(missingIndices);
    for (var indexArrayPosition = 0; indexArrayPosition < numIndices; indexArrayPosition += 1)
    {
        var diff = 0;
        while (diff < numMissingIndices && missingIndices[diff] < indices[indexArrayPosition])
        {
            diff += 1;
        }
        indices[indexArrayPosition] -= diff;
    }
    return indices;
}

/**
 * Deduplicates indices by incrementing duplicates, similar to routingCurve's deduplicateIndices.
 * First occurrence of an index has priority.
 * 
 * @param indices {array} : Array of index values that may contain duplicates
 * 
 * @returns {array} : Array with unique index values
 */
function deduplicateIndicesForInstances(indices is array) returns array
{
    var seenIndices = {};
    const numIndices = @size(indices);
    for (var currentIndexPosition = 0; currentIndexPosition < numIndices; currentIndexPosition += 1)
    {
        const index = indices[currentIndexPosition];
        if (seenIndices[index] == undefined)
        {
            seenIndices[index] = true;
            continue;
        }
        for (var otherIndexPosition = 0; otherIndexPosition < numIndices; otherIndexPosition += 1)
        {
            // Before currentIndexPosition, there is a single instance of indices[currentIndexPosition], we don't want to change it.
            // If there are more instances of indices[currentIndexPosition] after currentIndexPosition, we do want to change them.
            if (indices[otherIndexPosition] > index || (indices[otherIndexPosition] == index && otherIndexPosition >= currentIndexPosition))
            {
                indices[otherIndexPosition] += 1;
            }
        }
        seenIndices[indices[currentIndexPosition]] = true;
    }
    return indices;
}
