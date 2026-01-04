# Medial Axis Transform Feature

## Overview

The Medial Axis Transform (MAT) feature computes the skeleton or medial axis of a planar face. The medial axis is defined as the locus of centers of maximal discs that fit inside the face, and it serves as a compact representation of the shape.

## Algorithm

This implementation is based on the paper:
**"Performant Medial Axis Transform for Planar Domains"**

Located in: `whitepaper-references/Performant Medial Axis Transform for Planar Domains.pdf`

### Key Concepts

1. **Grass-Fire Model**: The algorithm simulates a fire spreading inward from the boundary at uniform speed. The medial axis forms where fire fronts meet.

2. **RLFS Function**: The Reduced Length Function to Skeleton (RLFS) represents the distance each boundary point travels before reaching the medial axis.

3. **Segment Collision Detection**: The algorithm tests when boundary segments, displaced along their normals, collide with each other.

### Algorithm Steps

#### 1. Boundary Sampling
- Extract boundary loops from the planar face using `qLoopEdges`
- Order edges in each loop using `constructPaths`
- Sample each edge into straight-line segments
- Compute inward-pointing normals for each segment endpoint

#### 2. RLFS Computation via Collision Detection
- Test pairs of boundary segments for collision during displacement
- Solve the collision equation: `P + d*N = (1-t)*(P1 + d*N1) + t*(P2 + d*N2)`
  - `P` = endpoint position, `N` = endpoint normal
  - `P1, P2` = segment endpoints, `N1, N2` = segment normals
  - `d` = displacement distance, `t` = parameter on segment
- Record RLFS pieces for segments that collide
- Each RLFS piece stores:
  - Segment index and parameter interval
  - Displacement distances at endpoints
  - Peer segment index (the segment it collided with)

#### 3. Find MA Extreme Points
- March along boundary segments
- Test adjacent segments for collision
- Identify MA extreme points at:
  - Convex vertices
  - Regions of locally maximal positive curvature (LMPC)

#### 4. Follow MA
- Track MA branches from extreme points
- Walk along boundary in opposite directions simultaneously
- Continue until:
  - No more collisions occur
  - A branching point is reached
  - Previously computed RLFS with smaller values is encountered

#### 5. Fix MA (for regions with holes)
- Find segments with undefined RLFS on inner boundaries
- Test against all other segments to find peer segments
- Track new MA branches from these collision points

#### 6. Extract Explicit Medial Axis
- Iterate over all RLFS pieces
- For each unprocessed piece:
  - Find its peer piece on the peer segment
  - Compute MA edge endpoints by:
    - Displacing boundary samples along normals
    - Averaging positions from both peer pieces
  - Create or share MA nodes for edge endpoints
  - Connect adjacent edges through shared nodes

#### 7. Visualization
- Create a sketch on the face plane
- Draw MA edges as line segments connecting nodes
- Each node has:
  - Position (center of maximal disc)
  - Radius (size of maximal disc)

## Usage

### Inputs
- **Planar face**: The face to compute the medial axis for (must be planar)
- **Sample density**: Number of samples per unit length (controls accuracy)
- **Use adaptive sampling**: Whether to adjust sampling based on curvature
- **Show debug info**: Display debug visualization and console output

### Outputs
- Sketch curves representing the medial axis on the face
- Debug visualization showing:
  - MA nodes (blue points)
  - MA edges (red lines)
  - Console output with statistics

## Implementation Details

### Data Structures

#### BoundarySegment
```
{
    startPoint: Vector,        // 3D start position
    endPoint: Vector,          // 3D end position
    startNormal: Vector,       // Unit normal at start (inward)
    endNormal: Vector,         // Unit normal at end (inward)
    index: number,             // Segment index
    nextIndex: number,         // Next segment in loop
    previousIndex: number      // Previous segment in loop
}
```

#### RLFSPiece
```
{
    segmentIndex: number,      // Segment this piece belongs to
    startParameter: number,    // Start parameter (0 to 1)
    endParameter: number,      // End parameter (0 to 1)
    startDistance: Length,     // RLFS value at start
    endDistance: Length,       // RLFS value at end
    peerSegmentIndex: number,  // Peer segment index
    edgeIndex: number          // Corresponding MA edge (-1 if undefined)
}
```

#### MANode
```
{
    position: Vector,          // 3D position
    radius: Length,            // Radius of maximal disc
    contributionCount: number  // Number of edges sharing this node
}
```

#### MAEdge
```
{
    startNodeIndex: number,    // Index of start node
    endNodeIndex: number       // Index of end node
}
```

### Key Functions

#### `sampleFaceBoundary`
Samples the face boundary into segments with normals. Handles multiple loops (outer boundary and holes).

#### `computeRLFSFunction`
Computes the RLFS function through segment collision detection. Tests all segment pairs for collision.

#### `testSegmentCollision`
Tests if two segments collide during displacement. Returns collision parameters if they collide.

#### `extractMedialAxisGraph`
Extracts the explicit medial axis as a graph from the RLFS function. Creates nodes and edges representing the MA.

#### `drawMedialAxisCurves`
Draws the MA curves on the face using sketch functions. Creates a sketch on the face plane and adds line segments.

## Limitations and Future Work

### Current Limitations
1. Collision detection uses simplified numerical approach (should be analytical quadratic solution)
2. Find MA Extreme Points and Follow MA are simplified (should implement full algorithm)
3. Fix MA for holes not yet implemented
4. Reflex vertex handling (dummy segments) not yet implemented
5. No adaptive sampling based on curvature yet

### Future Improvements
1. Implement analytical collision detection solver
2. Implement full Find MA Extreme Points routine with LMPC detection
3. Implement full Follow MA routine with branch tracking and overwriting
4. Implement Fix MA routine for regions with holes
5. Add reflex vertex splitting into dummy segments
6. Implement curvature-aware adaptive sampling
7. Optimize collision testing with spatial data structures
8. Add offset curve generation capability

## Performance

The algorithm has time complexity O((1 + genus) * n), where:
- `genus` = number of holes in the region
- `n` = number of boundary segments

The performance is linear in the number of samples, making it suitable for interactive use with moderate sample densities.

## References

1. **Paper**: "Performant Medial Axis Transform for Planar Domains"
   - Location: `whitepaper-references/Performant Medial Axis Transform for Planar Domains.pdf`
   - Describes the grass-fire simulation algorithm with RLFS representation

2. **Kerf Bending Feature**: `kerf-bending/kerfBendingAnalytical.fs`
   - Example of curvature sampling using `evEdgeCurvature`
   - Shows adaptive sampling based on curvature magnitude

3. **Onshape Standard Library**: https://cad.onshape.com/FsDoc/library.html
   - Reference for all standard functions used
   - Documentation for evaluation, query, and geometry functions

## Testing

To test the feature:
1. Create a planar face in Onshape (e.g., a sketch on a plane)
2. Select the "Medial Axis Transform" feature
3. Pick the planar face
4. Adjust sample density as needed
5. Enable "Show debug info" to see visualization
6. Run the feature and observe the medial axis curves

Start with simple shapes (rectangles, circles) and gradually test more complex shapes with curves and holes.
