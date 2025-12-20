# Kerf Bending Utilities

## Overview

The `kerfBendingUtils.fs` module provides FeatureScript utilities for calculating kerf bend patterns along curves. These utilities are converted from the Python implementation found in `non-featurescript-functions-reference/kerf bending/`.

## What is Kerf Bending?

Kerf bending is a technique where cuts (kerfs) are made partway through a material, typically plywood, to create flexible hinge points. This allows flat panels to be bent into curved surfaces. The key insight is that by calculating the exact positions of these cuts, you can control the final curve shape of the bent material.

## Key Concepts

### Kerf Angle
The **kerf angle** is the angle by which a single cut bends the material. This depends on:
- **Cut Width**: The thickness of the blade or cutting tool
- **Cut Depth**: How deep the cut goes (typically 10-15% less than material thickness)

The formula is: `kerfAngle = 2 * atan2(cutWidth, 2 * cutDepth)`

### Curve Discretization
The algorithm works by:
1. Sampling points along the desired curve (Bezier curve)
2. Calculating the tangent angle at each point
3. Finding cut positions where the tangent angle changes by the kerf angle

## Main Functions

### `calculateKerfAngle(cutWidth, cutDepth)`
Calculate the kerf angle for given tool and cut parameters.

**Parameters:**
- `cutWidth` (ValueWithUnits): Thickness of the cutting blade with length units
- `cutDepth` (ValueWithUnits): Depth of the cut with length units

**Returns:** The kerf angle in radians (ValueWithUnits with angle units)

**Example:**
```featurescript
const kerfAngle = calculateKerfAngle(2.7 * millimeter, 35 * millimeter);
```

### `generateKerfBendingSolution(controlPoints, cutWidth, cutDepth, [curveSamples], [searchWindow])`
Generate a complete kerf bending solution for a quadratic Bezier curve.

**Parameters:**
- `controlPoints` (array): Three 3D control points defining the Bezier curve
- `cutWidth` (ValueWithUnits): Thickness of the cutting tool
- `cutDepth` (ValueWithUnits): Depth of the cut
- `curveSamples` (number, optional): Number of samples (default: 600)
- `searchWindow` (number, optional): Search window size (default: 80)

**Returns:** A `KerfBendingSolution` containing:
- `cutPositions`: Array of 3D positions where cuts should be made
- `cutDistances`: Array of distances between consecutive cuts
- `totalLength`: Total length of the flattened workpiece
- `numberOfCuts`: Total number of cuts required
- `kerfAngle`: The kerf angle used
- `curvatureSigns`: Array indicating which side of the material to cut on

**Example:**
```featurescript
// Define a parabolic curve with three control points
const controlPoints = [
    vector(-400, 0, 0) * millimeter,
    vector(0, -820, 0) * millimeter,
    vector(400, 0, 0) * millimeter
];

// Define cut parameters
const cutWidth = 2.7 * millimeter;  // Blade thickness
const cutDepth = 35 * millimeter;   // Cut depth (85-90% of material thickness)

// Generate the solution
const solution = generateKerfBendingSolution(controlPoints, cutWidth, cutDepth);

// Access results
println("Total length: " ~ solution.totalLength);
println("Number of cuts: " ~ solution.numberOfCuts);
```

### `calculateFlattenedCutPositions(cutDistances, centerOrigin)`
Transform 3D cut positions into 1D positions along a straight line for CAM software.

**Parameters:**
- `cutDistances` (array): Array of distances between consecutive cuts
- `centerOrigin` (boolean): If true, center around zero; if false, start from zero

**Returns:** Array of 1D positions where cuts should be made

**Example:**
```featurescript
const flatPositions = calculateFlattenedCutPositions(solution.cutDistances, true);
// flatPositions now contains linear positions suitable for CNC programming
```

### `createKerfBendingSummary(solution)`
Create a summary map with key statistics about a kerf bending solution.

**Returns:** Map with:
- `totalLength`: Total workpiece length
- `numberOfCuts`: Number of cuts
- `kerfAngleDegrees`: Kerf angle in degrees
- `averageCutSpacing`: Mean distance between cuts
- `minimumCutSpacing`: Smallest gap between cuts
- `maximumCutSpacing`: Largest gap between cuts

## Types

### `CurvePoint`
Represents a point along a curve with associated geometric information.

**Fields:**
- `position` (Vector): 3D position with length units
- `tangentAngle` (ValueWithUnits): Tangent angle with angle units
- `curvatureSign` (number): -1, 0, or 1 indicating curve direction

### `KerfBendingSolution`
Complete solution for a kerf bending problem.

**Fields:**
- `cutPositions` (array): 3D positions for cuts
- `cutDistances` (array): Distances between cuts
- `totalLength` (ValueWithUnits): Total workpiece length
- `numberOfCuts` (number): Number of cuts
- `kerfAngle` (ValueWithUnits): Kerf angle used
- `curvatureSigns` (array): Curvature signs at each cut

## Utility Functions

### `calculatePointDistance(point1, point2)`
Calculate distance between two 2D or 3D points.

### `calculateAngleDifference(angle1, angle2)`
Calculate the smallest difference between two angles, accounting for wraparound.

### `createQuadraticBezierCurvePoints(controlPoints, numberOfSamples)`
Generate discretized curve points with tangent and curvature information.

### `calculateCutDistances(cutPositions)`
Calculate distances between consecutive cut positions.

### `calculateTotalLength(cutDistances)`
Sum all cut distances to get total workpiece length.

## Complete Usage Example

```featurescript
import(path : "kerfBendingUtils.fs", version : "");

// Define the desired curve as a quadratic Bezier
// These three points control the parabolic shape
const p0 = vector(-400, 0, 0) * millimeter;    // Start point
const p1 = vector(0, -820, 0) * millimeter;    // Control point (creates curve)
const p2 = vector(400, 0, 0) * millimeter;     // End point

const controlPoints = [p0, p1, p2];

// Material and tool parameters
const bladeThickness = 2.7 * millimeter;
const cutDepth = 35 * millimeter;  // For ~40mm thick plywood

// Generate the complete solution
const solution = generateKerfBendingSolution(
    controlPoints,
    bladeThickness,
    cutDepth
);

// Get summary statistics
const summary = createKerfBendingSummary(solution);

// Print results
println("=== Kerf Bending Solution ===");
println("Total workpiece length: " ~ solution.totalLength);
println("Number of cuts required: " ~ solution.numberOfCuts);
println("Kerf angle: " ~ summary.kerfAngleDegrees ~ " degrees");
println("Average cut spacing: " ~ summary.averageCutSpacing);

// Get flattened positions for CAM software
const flatPositions = calculateFlattenedCutPositions(
    solution.cutDistances,
    true  // Center around origin
);

// Use flatPositions to create cut lines in your CAD model
// or export to DXF for CNC machining
```

## Theory and Algorithm

The algorithm works by:

1. **Sampling the curve**: The desired curve (Bezier) is discretized into many points
2. **Calculating tangents**: At each point, the tangent angle is computed
3. **Finding cut positions**: Starting from the leftmost point, the algorithm searches for points where the tangent angle has changed by exactly the kerf angle
4. **Bidirectional search**: The algorithm works both left and right from the starting point to cover the entire curve
5. **Curvature tracking**: The sign of curvature determines which side of the material to cut on

## Implementation Notes

### Conversion from Python

This FeatureScript implementation is based on the Python code in:
- `non-featurescript-functions-reference/kerf bending/kerf bending offline.py`
- `non-featurescript-functions-reference/kerf bending/kerfBackend.py`

Key differences:
- FeatureScript uses `Vector` types with proper unit handling
- No dependency on external libraries (numpy, scipy, ezdxf)
- Focused on mathematical utilities rather than DXF generation
- Type-safe with preconditions and type checking

### Limitations

1. **Bezier curves only**: Currently only supports quadratic Bezier curves (3 control points)
2. **Planar curves**: Designed for 2D curves embedded in 3D space (z=0)
3. **No spline support yet**: Spline curves would require additional implementation

### Future Enhancements

Potential additions:
- Support for cubic Bezier curves (4 control points)
- Spline curve support using FeatureScript's spline utilities
- Direct integration with sketch entities
- Automatic generation of cut geometry in the CAD model
- DXF export functionality (if FeatureScript supports it)

## References

- Original Python implementation: `non-featurescript-functions-reference/kerf bending/`
- Onshape FeatureScript Documentation: https://cad.onshape.com/FsDoc/
- Kerf bending technique: A woodworking/fabrication method for creating curves in flat materials

## License

This implementation follows the same MIT License as the Onshape Standard Library and the original Python implementation.
