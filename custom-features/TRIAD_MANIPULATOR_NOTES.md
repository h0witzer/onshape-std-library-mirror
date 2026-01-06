# Triad Manipulator Implementation Notes

This document describes the differences between the two types of triad manipulators in FeatureScript and how to properly use them.

## Manipulator Types

### 1. `triadManipulator` (Simple Triad)
**Definition**: `manipulator.fs` - Line 133
**Capabilities**: Translation only (3-DOF)

```featurescript
const triadManip = triadManipulator({
    "base" : basePoint,      // Vector: center position
    "offset" : offsetVector   // Vector: translation from base
});
```

**Output**: Returns offset as a 3D vector
- `newManipulators[MANIPULATOR_ID].offset` → Vector with X, Y, Z translation

**Use Case**: When you only need translation control (e.g., moving control points, positioning features)

### 2. `fullTriadManipulator` (Full 6-DOF Triad)
**Definition**: `manipulator.fs` - Line 156
**Capabilities**: Translation + Rotation (6-DOF)

```featurescript
const triadManip = fullTriadManipulator({
    "base" : coordinateSystem,  // CoordSystem: defines local axes
    "transform" : currentTransform,  // Transform: current state relative to base
    "displayEditView" : true     // Optional: show edit text box
});
```

**Output**: Returns a Transform object
- `newManipulators[MANIPULATOR_ID].transform` → Transform with rotation matrix + translation

**Use Case**: When you need full 6-DOF control (e.g., positioning planes, orienting features)

## Key Differences

### Data Storage

**Simple Triad**:
- Store: Vector (3 length values)
- Precondition: `isLength(x/y/z, bounds)`

**Full Triad**:
- Store: Decomposed Transform
  - Rotation: `isAnything(rotationMatrix)` - flat array of 9 values
  - Translation: `isLength(x/y/z, bounds)` - 3 separate length values
- Cannot store Transform directly in preconditions
- Cannot use nested arrays (`rotationMatrix is array` inside array loop)

### Transform Application

**Simple Triad** (translation):
```featurescript
const newPosition = basePoint + offset;
```

**Full Triad** (rotation + translation):
```featurescript
// Transform is relative to base coordinate system
const worldTransform = toWorld(baseCoordSys) * transform;
const newPosition = worldTransform * originalPoint;

// DO NOT use sandwich pattern: toWorld * transform * fromWorld
// This applies rotation in the wrong direction!
```

### Rotation Direction

**Critical**: The fullTriadManipulator returns transforms that are **relative to the base coordinate system**.

- ✅ **Correct**: `toWorld(baseCS) * transform`
- ❌ **Wrong**: `toWorld(baseCS) * transform * fromWorld(baseCS)`

The sandwich pattern (`toWorld * T * fromWorld`) is used for transforming coordinate systems themselves, not for applying user manipulations. Using it with fullTriadManipulator will result in rotations happening in the opposite direction from user input.

## Storage Pattern

### routingCurve Pattern
Stores rotation matrix directly in points array:
```featurescript
definition.points is array;
for (var point in definition.points)
{
    isAnything(point.rotationMatrix);  // Flat array of 9 values
    isLength(point.dx, bounds);
    isLength(point.dy, bounds);
    isLength(point.dz, bounds);
}
```

### triadTransform Pattern
Stores rotation as Euler angles:
```featurescript
isAngle(definition.rx, bounds);  // X rotation
isAngle(definition.ry, bounds);  // Y rotation
isAngle(definition.rz, bounds);  // Z rotation
isLength(definition.dx, bounds); // X translation
isLength(definition.dy, bounds); // Y translation
isLength(definition.dz, bounds); // Z translation
```

Then composes:
```featurescript
const rotation = composeRotation(baseCSys, rx, ry, rz);
const triadTransform = transform(rotation, vector(dx, dy, dz));
```

## Decomposing and Reconstructing Transforms

### Storing from Manipulator
```featurescript
const manipulator = newManipulators[MANIPULATOR_ID];
const transform = manipulator.transform;

// Flatten rotation matrix to array of 9 values
const linearMatrix = transform.linear;
const rotationMatrix = [
    linearMatrix[0][0], linearMatrix[0][1], linearMatrix[0][2],
    linearMatrix[1][0], linearMatrix[1][1], linearMatrix[1][2],
    linearMatrix[2][0], linearMatrix[2][1], linearMatrix[2][2]
];

const translation = transform.translation;
// Store: rotationMatrix, translation[0], translation[1], translation[2]
```

### Reconstructing for Use
```featurescript
if (rotationMatrix != undefined && size(rotationMatrix) == 9)
{
    // Convert flat array back to Matrix
    const matrix3x3 = matrix([
        [rotationMatrix[0], rotationMatrix[1], rotationMatrix[2]],
        [rotationMatrix[3], rotationMatrix[4], rotationMatrix[5]],
        [rotationMatrix[6], rotationMatrix[7], rotationMatrix[8]]
    ]);
    
    const transform = transform(matrix3x3, vector(tx, ty, tz));
}
```

## Best Practices

1. **Use Simple Triad** when you only need translation
2. **Use Full Triad** when you need rotation or 6-DOF control
3. **Always decompose** Transform objects for storage in preconditions
4. **Use isAnything()** for rotation matrix arrays to avoid nested array errors
5. **Apply transforms** with `toWorld(baseCS) * transform`, not the sandwich pattern
6. **Validate transform** existence by checking `rotationMatrix != undefined`
7. **Provide defaults** when no transform exists (identity transform)

## Common Pitfalls

1. ❌ Storing Transform object directly in precondition → "Unrecognized type" error
2. ❌ Using `rotationMatrix is array` in nested loop → "Cannot define array within array" error
3. ❌ Returning `undefined` from function declared `returns map` → "Function returned undefined" error
4. ❌ Using sandwich pattern for manipulator transforms → Opposite rotation direction
5. ❌ Not checking for `undefined` before accessing array size → Runtime error

## Examples in Standard Library

- **routingCurve.fs**: Uses fullTriadManipulator with rotationMatrix storage pattern
- **triadTransform.fs**: Uses fullTriadManipulator with Euler angle storage pattern
- **freeFormDeformationPlanes.fs**: Uses fullTriadManipulator with rotationMatrix storage for plane manipulation

## References

- Onshape FeatureScript Documentation: https://cad.onshape.com/FsDoc/
- manipulator.fs: Manipulator type definitions and preconditions
- transform.fs: Transform type and operations
- coordSystem.fs: Coordinate system utilities
