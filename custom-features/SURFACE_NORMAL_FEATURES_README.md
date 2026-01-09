# Surface Normal Flip and Debug Features

This directory contains two tester scripts for working with surface normals in Onshape FeatureScript:

## 1. Flip Surface Normals (`flipSurfaceNormals.fs`)

This feature reverses the orientation of selected surfaces so that their normals point in the opposite direction.

### How it works:
1. Extracts the B-spline surface definition (or creates an approximation for non-B-spline surfaces)
2. Reverses the V direction control points to flip the normal
3. Reverses the V knot vector to maintain proper parametrization
4. Creates a new surface with the modified control points
5. Replaces the original surface with the flipped surface

### Usage:
1. Select one or more surfaces/faces you want to flip
2. Run the "Flip Surface Normals" feature
3. The surfaces will be flipped in place

### Notes:
- Works with any surface type (planar, cylindrical, B-spline, etc.)
- Non-B-spline surfaces are automatically approximated as B-splines
- Maintains the same underlying geometry, only changes orientation
- Multiple surfaces can be flipped at once

## 2. Debug Surface Normal (`debugSurfaceNormal.fs`)

This feature visualizes the surface normal at the center of selected surfaces for validation purposes.

### How it works:
1. Evaluates the tangent plane at the center point (parameter [0.5, 0.5]) of each surface
2. Displays a debug arrow pointing in the direction of the surface normal
3. Displays a point at the evaluation location for reference

### Usage:
1. Select one or more surfaces/faces to visualize
2. Set the "Normal length" parameter to control the size of the debug arrow
3. Choose a "Debug color" for the visualization
4. Run the "Debug Surface Normal" feature
5. The normal arrows will be visible while editing the feature

### Parameters:
- **Surfaces to debug**: Select surfaces to visualize their normals
- **Normal length**: Length of the debug arrow (default units: meters, inches, etc.)
- **Debug color**: Color of the debug arrow (RED, GREEN, BLUE, CYAN, MAGENTA, YELLOW, etc.)

### Notes:
- Debug visualizations are only visible while the feature is being edited
- Multiple surfaces can be debugged simultaneously
- Useful for verifying surface orientation before and after flip operations

## Workflow Example

1. Create or select surfaces in your part studio
2. Run "Debug Surface Normal" to see the current normal directions
3. Run "Flip Surface Normals" to reverse the orientation
4. Run "Debug Surface Normal" again to verify the normals have been flipped

## Technical Details

### Surface Normal Flipping
The flip operation works by reversing the parametric V direction of the surface. For a B-spline surface defined by control points `P[i][j]` where `i` is the U direction and `j` is the V direction, flipping reverses the `j` indices:

```
Original: P[0][0], P[0][1], ..., P[0][n]
          P[1][0], P[1][1], ..., P[1][n]
          ...

Flipped:  P[0][n], P[0][n-1], ..., P[0][0]
          P[1][n], P[1][n-1], ..., P[1][0]
          ...
```

The V knot vector is also reversed using the transformation: `k_new = (k_max + k_min) - k_old`

### Surface Normal Evaluation
The normal is evaluated at the surface center using `evFaceTangentPlane`, which returns a plane containing:
- `origin`: The 3D point at the surface center
- `normal`: The unit normal vector perpendicular to the surface
- `x` and `y`: Tangent vectors in the plane

## Limitations

- The flip operation may not preserve certain surface properties like UV mapping or texture coordinates
- Approximation of complex surfaces may introduce minor geometric variations
- Debug visualizations only appear during feature editing, not in the final model

