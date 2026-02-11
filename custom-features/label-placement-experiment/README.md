# Label Placement Exploration Features

This directory contains FeatureScript implementations of the label placement strategies described in `Planar Face Label Placement Strategies.md`.

## Features

### 1. MIHC Label Placement (`mihcPlacement.fs`)

Implements the **Maximum Internal Horizontal Chord (MIHC)** algorithm for finding optimal label placement points on planar faces.

**How it works:**
1. Projects the planar face boundary into a 2D coordinate system
2. Generates horizontal scanlines at strategic heights (25%, 50%, 75% or custom)
3. Finds all intersections of each scanline with the polygon edges
4. Identifies the longest internal horizontal segment across all scanlines
5. Places a mate connector at the midpoint of the longest chord

**Advantages:**
- Guaranteed to find a point in the "widest" part of the face
- Handles concave shapes, horseshoes, U-shapes, slotted plates
- Naturally handles holes - picks the largest continuous region
- Non-iterative O(n) algorithm

**Usage:**
1. Select a planar face
2. Choose number of scanlines (1 for center only, 3 for robustness)
3. A mate connector will be placed at the optimal point
4. The X-axis of the mate connector aligns with the chord direction

**Best for:** Labels that should be centered in the "bulk" or "widest" part of a face

---

### 2. Ear Clipping Placement (`earClippingPlacement.fs`)

Implements the **Ear Clipping** algorithm as a fast alternative for finding guaranteed interior points.

**How it works:**
1. Projects the planar face boundary into a 2D coordinate system
2. Finds the first "ear" triangle - three consecutive vertices where:
   - The middle vertex forms a convex angle
   - No other vertices lie inside the triangle
3. Calculates the incenter of the ear triangle (center of inscribed circle)
4. Places a mate connector at the incenter

**Advantages:**
- Very fast O(n) algorithm
- Always finds a guaranteed interior point
- Simpler logic than MIHC
- Good for small icons or markers

**Usage:**
1. Select a planar face
2. A mate connector will be placed at the ear incenter

**Best for:** Quick placement where exact positioning in the "widest" area isn't critical

---

## Comparison

| Feature | Algorithm | Speed | Optimality | Best Use Case |
|---------|-----------|-------|------------|---------------|
| **MIHC** | Scanline intersection | O(n·s) where s = scanlines | Finds widest part | Labels that should span the bulk of the face |
| **Ear Clipping** | Triangle decomposition | O(n²) worst case, O(n) typical | First valid ear | Quick placement, icons, markers |

Both features:
- Only work on planar faces (filtered in precondition)
- Handle unordered vertex collections from the geometry kernel
- Create mate connectors with proper orientation for visualization
- Are non-iterative and deterministic

---

## Technical Details

### 2D Projection

Both features project the 3D face into a local 2D coordinate system using:
- `evFaceTangentPlane()` to get the plane
- Dot product projection: `x = dot(point - origin, plane.x)`, `y = dot(point - origin, plane.y)`

### Edge Loop Reconstruction

FeatureScript's `qAdjacent()` returns unordered collections. Both features:
1. Build a vertex-to-vertex adjacency map from edges
2. Walk the perimeter starting from the first vertex
3. Construct an ordered polygon for 2D analysis

### Coordinate System Creation

Mate connectors are created with:
- **Origin:** The computed 2D point unprojected back to 3D
- **Z-axis:** Face normal (from tangent plane)
- **X-axis:** 
  - MIHC: Direction of the longest chord
  - Ear Clipping: Direction of first ear edge

---

## Future Enhancements

Potential improvements mentioned in the markdown document:
- **Winding number** point-in-polygon validation for robustness
- **Label size awareness** - scale labels to fit within the detected chord length
- **Multi-loop handling** - explicitly handle faces with holes/inner boundaries
- **Performance caching** - store computed positions using editing logic attributes
- **Visual debugging** - sketch the detected chords/ears for verification

---

## References

See `Planar Face Label Placement Strategies.md` for:
- Detailed algorithm descriptions
- Mathematical formulations
- Performance analysis
- Comparison with other methods (centroid, grid sampling, medial axis)
- FeatureScript-specific optimization techniques
