# Medial Axis Feature

## Overview

This FeatureScript feature computes the Medial Axis Transform (MAT) for planar faces. The medial axis, also known as the skeleton or centerline, is the locus of centers of all maximal inscribed circles within a 2D shape.

## Algorithm

The implementation is based on the tracing algorithm presented in:

> **"A tracing algorithm for constructing medial axis transform of 3D objects bound by free-form surfaces"**  
> by M. Ramanathan and B. Gurumoorthy  
> Indian Institute of Science, 2005

### Key Concepts

1. **Medial Axis**: The set of points equidistant from two or more boundary edges
2. **Distance Criterion**: Points must be equidistant from boundary segments
3. **Curvature Criterion**: The radius of the maximal inscribed circle must be ≤ the minimum radius of curvature of the boundary segments

### Algorithm Steps

1. Extract boundary edges from the selected planar face
2. For each pair of boundary edges:
   - Sample points along one edge parametrically (tracing parameter)
   - Compute the inward normal at each sample point
   - Find intersections with normals from the other edge
   - Verify distance criterion (point is equidistant from both edges)
   - Verify curvature criterion (maximal circle fits within boundaries)
3. Connect valid medial axis points into continuous curves
4. Output curves representing the medial axis

## Usage

1. Select a planar face
2. Adjust tracing step size (smaller = more accurate but slower)
3. Adjust distance tolerance (how close distances must be to be considered "equal")
4. Optionally create a composite curve from all medial axis segments

## Parameters

- **Face**: The planar face to compute the medial axis for (required)
- **Tracing step size**: Distance between sample points along edges (default: 0.005 m)
  - Smaller values give more accurate results but take longer to compute
  - Larger values are faster but may miss details
- **Distance tolerance**: Tolerance for considering points equidistant (default: 0.0001 m)
  - Controls how strictly the distance criterion is enforced
- **Create composite curve**: Whether to merge all medial axis curves into one (default: true)

## Limitations

- **Planar faces only**: Currently only supports 2D planar faces. The 3D extension for curved surfaces is not implemented.
- **Approximate**: The algorithm produces an approximation based on sampling. The accuracy depends on the step size.
- **Performance**: For complex shapes with many edges, computation may be slow. Adjust step size if needed.

## Examples

### Simple Rectangle
- Creates a centerline along the length of the rectangle
- Connects at corners with branches

### Circle
- Creates a single point at the center (if tolerance allows)
- May create small curves near the center due to sampling

### Complex Polygons
- Creates a skeletal structure that captures the shape's topology
- Branch points occur where three or more edges are equidistant

## Technical Details

### Distance Criterion
For a point P to be on the medial axis, it must satisfy:
```
|P - E1| = |P - E2| (within tolerance)
```
where E1 and E2 are the closest points on two boundary edges.

### Curvature Criterion
The radius r of the maximal inscribed circle must satisfy:
```
r ≤ min(R1, R2)
```
where R1 and R2 are the radii of curvature of the boundary edges at the touchpoints.

This ensures the circle is truly maximal and doesn't extend beyond the boundary.

### Normal Intersection Method
Instead of computing bisectors (which can be non-rational for free-form curves), the algorithm:
1. Computes normals to boundary edges (always linear)
2. Finds their intersections
3. Validates using distance and curvature criteria

This is computationally more efficient than bisector-based methods.

## References

- Ramanathan, M., & Gurumoorthy, B. (2005). "A tracing algorithm for constructing medial axis transform of 3D objects bound by free-form surfaces". Proceedings of the International Conference on Shape Modeling and Applications (SMI'05).
- The paper PDF is located in: `whitepaper-references/A tracing algorithm for constructing medial axis transform of 3D objects bound by free-form surfaces.pdf`

## Future Enhancements

Possible improvements for future versions:
- Support for 3D surfaces (full algorithm from the paper)
- Junction point detection (where 4+ edges meet)
- Sheet generation (2D medial surfaces between face pairs)
- Adaptive step sizing based on local curvature
- Optimization for closed loops and symmetry
