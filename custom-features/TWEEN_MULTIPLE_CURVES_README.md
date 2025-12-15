# Tween Multiple Curves Feature

## Overview

The `tweenMultipleCurves` feature extends the basic tween curve functionality to handle arbitrary numbers of connected curves (paths) on each side. It creates an interpolated curve between two paths by matching their subsegments using intelligent sampling methods.

## Feature Definition

**File**: `custom-features/tweenMultipleCurves.fs`  
**Feature Type Name**: "Tween Multiple Curves"

## Parameters

### Connection Method
Choose how subsegments should be matched between the two paths:

- **Nearest Distance**: Uses `evDistance` to find break points where vertices from one path map to the closest point on the other path. This ensures that natural connection points (vertices) are matched and creates break points at logical positions.

- **Path Length Parameterization**: Uses path length ratios from vertices on both paths to determine unified sampling points. This creates more uniform distribution along the paths regardless of individual edge lengths or geometry.

### First Curve or Edge Group
Select one or more tangent-connected edges that form the first path.

### Second Curve or Edge Group  
Select one or more tangent-connected edges that form the second path.

### Tween Fraction
A value from 0 to 1 that determines the position of the interpolated curve:
- `0.0` = Result is at the first curve/path
- `0.5` = Result is halfway between the two curves/paths
- `1.0` = Result is at the second curve/path

## How It Works

1. **Path Construction**: The feature uses `constructPath` to convert the selected edge groups into ordered paths with direction information.

2. **Sample Parameter Generation**: Based on the selected connection method:
   - **Nearest Distance**: Calculates path ratios for all vertices, then maps vertices from path 2 to path 1 using `evDistance`
   - **Path Length**: Calculates path length ratios for vertices on both paths and merges them into a unified set

3. **Intermediate Sampling**: Automatically adds intermediate sample points between vertices for smoother results (2-3 samples per segment depending on gap size).

4. **Path Sampling**: Uses `evPathTangentLines` to sample both paths at the determined parameters, getting 3D point positions.

5. **Interpolation**: Linearly interpolates between corresponding points on each path based on the tween fraction.

6. **Spline Creation**: Uses `opFitSpline` to create a smooth B-spline curve through all the interpolated points.

## Implementation Details

### Key Functions

#### `generateNearestDistanceSampleParameters`
Generates sample parameters using the nearest distance method by:
- Finding all vertices on both paths
- Computing path length ratios for path 1 vertices
- Mapping path 2 vertices to path 1 using `evDistance`
- Merging, deduplicating, and sorting all ratios

#### `generatePathLengthSampleParameters`
Generates sample parameters using path length parameterization by:
- Finding all vertices on both paths
- Computing path length ratios for vertices on each path independently
- Merging, deduplicating, and sorting all ratios

#### `calculatePathLengthRatioForVertex`
Converts a vertex position to a global path ratio (0 to 1) by:
- Finding which edge contains the vertex using `evDistance`
- Calculating accumulated length up to that point
- Accounting for edge flipping in the path

#### `convertEdgeParameterToPathRatio`
Converts an edge index and local parameter to a global path ratio by:
- Accumulating lengths of all edges before the target edge
- Adding the length within the target edge
- Accounting for edge flipping

#### `addIntermediateSamples`
Enhances the sampling density by:
- Adding 2 intermediate samples for large gaps (> 0.05)
- Adding 1 intermediate sample for medium gaps (> 0.02)
- Leaving small gaps unchanged

#### `createSplineThroughPoints`
Creates the final interpolated curve by calling `opFitSpline` with the tweened points.

### Helper Functions from Auto-Loft-Connections

The following functions were adapted from `loftConnectionsLoop.fs`:
- `calculatePathLengthRatioForVertex`
- `removeDuplicateRatios`
- `sortRatios`

## Comparison with Auto-Loft-Connections

| Aspect | Auto-Loft-Connections | Tween Multiple Curves |
|--------|----------------------|----------------------|
| Purpose | Create loft connection lines | Create single interpolated curve |
| Output | Multiple connection lines | Single fitted spline |
| Sampling | At vertex positions only | At vertices + intermediate points |
| Method 1 | Nearest distance for connections | Nearest distance for sampling |
| Method 2 | Path length for connections | Path length for sampling |
| Result | Used as loft guides | Final curve geometry |

## Use Cases

1. **Multi-segment Profile Interpolation**: When you have two profiles made of multiple connected edges and want to create an intermediate profile.

2. **Complex Shape Morphing**: Interpolating between two complex curved paths with different numbers of edges.

3. **Variable Cross-sections**: Creating intermediate cross-sections for swept or lofted features.

4. **Animation Paths**: Generating intermediate curves for path-based animations or simulations.

## Limitations and Considerations

1. **Single Curve vs Multiple Curves**: When both inputs are single edges, the feature uses the simpler sampling-based approach. For better B-spline control point interpolation with single curves, consider using the original `tweenTwoCurves` feature.

2. **Open Paths Only**: This feature is designed for open paths. Closed paths may work but haven't been specifically tested.

3. **Sampling Density**: The intermediate sampling is heuristic-based. Very complex curves may need manual adjustment of sampling density.

4. **Path Orientation**: The feature attempts to handle path direction automatically, but results depend on the consistency of edge ordering in the paths.

5. **Performance**: Sampling and spline fitting can be computationally expensive for very long paths with many vertices.

## Future Enhancements

Possible improvements for future versions:
- Add option to control sampling density
- Support for closed paths with proper periodic handling
- Integration with original `tweenCurves` for better B-spline interpolation when available
- Visualization of sampling points for debugging
- Support for matching tangent directions at endpoints

## Related Features

- **tweenTwoCurves**: Simple tween between two single curves with B-spline control point interpolation
- **tweenCurves**: Core tween functionality with degree matching and control point matching
- **loftConnectionsLoop**: Auto-loft connections using similar path sampling methods
- **fitSpline**: Creates splines through points (used internally)

## Example Usage

```featurescript
// In Onshape, after adding this feature to your document:

// 1. Select first edge group (multiple connected edges)
// 2. Select second edge group (multiple connected edges)
// 3. Choose connection method (Nearest Distance or Path Length)
// 4. Set tween fraction (e.g., 0.5 for halfway)
// 5. Execute to create the interpolated curve
```

## Technical Notes

- Uses FeatureScript 2837 standard library
- Requires `path.fs` for path construction and evaluation
- Uses `geomOperations.fs` for `opFitSpline`
- Uses `evaluate.fs` for distance calculations and path tangent evaluation
- All helper functions use proper arc length parameterization where appropriate
