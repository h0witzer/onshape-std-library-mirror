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
// For manipulating control points around a center, use the sandwich pattern with INVERSE
const inverseTransform = inverse(transform);
const worldTransform = toWorld(baseCoordSys) * inverseTransform * fromWorld(baseCoordSys);
const newPosition = worldTransform * originalPoint;

// For transforming entire bodies, use the sandwich pattern WITHOUT inverse
const worldTransform = toWorld(baseCoordSys) * transform * fromWorld(baseCoordSys);
opTransform(context, id, { "bodies" : bodies, "transform" : worldTransform });
```

### Rotation Direction

**Critical**: The fullTriadManipulator returns transforms that represent how the coordinate system moves.

- For **point manipulation**: Use `inverse(transform)` to get the correct rotation direction
  - When the user rotates the handle clockwise, the coordinate system rotates clockwise
  - But we want to rotate points clockwise around the center
  - These are opposite operations, hence the inverse
- For **body transformation**: Use `transform` directly (matches triadTransform)
  - Bodies are transformed as rigid objects
  - The transform is applied directly without inversion

The difference is subtle but crucial:
- **Manipulating points**: We're moving points relative to a fixed center → use inverse
- **Transforming bodies**: We're moving the entire object → use direct transform

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
5. **Apply transforms with inverse** for point manipulation: `toWorld(baseCS) * inverse(transform) * fromWorld(baseCS)`
6. **Apply transforms directly** for body transformation: `toWorld(baseCS) * transform * fromWorld(baseCS)`
7. **Validate transform** existence by checking `rotationMatrix != undefined`
8. **Provide defaults** when no transform exists (identity transform)

## Common Pitfalls

1. ❌ Storing Transform object directly in precondition → "Unrecognized type" error
2. ❌ Using `rotationMatrix is array` in nested loop → "Cannot define array within array" error
3. ❌ Returning `undefined` from function declared `returns map` → "Function returned undefined" error
4. ❌ Forgetting to invert transform for point manipulation → Opposite rotation direction
5. ❌ Using direct transform instead of inverse for points → Unwanted positional offset
6. ❌ Not checking for `undefined` before accessing array size → Runtime error

## Examples in Standard Library

- **routingCurve.fs**: Uses fullTriadManipulator with rotationMatrix storage pattern
- **triadTransform.fs**: Uses fullTriadManipulator with Euler angle storage pattern
- **freeFormDeformationPlanes.fs**: Uses fullTriadManipulator with rotationMatrix storage for plane manipulation

## References

- Onshape FeatureScript Documentation: https://cad.onshape.com/FsDoc/
- manipulator.fs: Manipulator type definitions and preconditions
- transform.fs: Transform type and operations
- coordSystem.fs: Coordinate system utilities
