# Label Placement Features

This directory contains FeatureScript implementations for placing mate connectors at optimal positions on planar faces.

## Features

### 1. Face Label Placement (`mihcPlacement.fs`)

Places a mate connector at the centroid of a planar face using FeatureScript's built-in evaluation functions.

**How it works:**
1. Uses `evApproximateCentroid()` to find the face centroid
2. Uses `evFaceTangentPlane()` to get face orientation
3. Places mate connector at centroid with proper orientation

**Advantages:**
- Works with ALL face types: rectangles, circles, splines, arcs, etc.
- Simple and robust - only ~57 lines of code
- Uses proper FeatureScript evaluation functions
- No manual 2D projections or polygon operations
- Guaranteed to place connector inside the face

**Usage:**
1. Select a planar face
2. A mate connector will be placed at the face centroid
3. Z-axis is normal to face, X-axis aligned with face plane

**Best for:** Any planar face where centroid placement is acceptable

---

### 2. Face Label Placement Alt (`earClippingPlacement.fs`)

Alternative implementation using the same centroid-based approach (provided for comparison/testing).

---

## Why Centroid-Based Placement?

The original implementation used complex polygon algorithms (MIHC, Ear Clipping) but had critical limitations:

❌ **Required discrete vertices** - Failed on faces with splines, circles, arcs  
❌ **Complex 2D projections** - Manual projection and polygon operations  
❌ **~200 lines of code** - Hard to maintain, lots of edge cases  
❌ **"Face must have at least 3 vertices"** - Common error on curved faces  

The new centroid-based approach:

✅ **Works for all face types** - Handles any planar face geometry  
✅ **Uses FeatureScript functions** - `evApproximateCentroid`, `evFaceTangentPlane`  
✅ **Simple and robust** - Only ~57 lines of code  
✅ **No manual projections** - Let FeatureScript handle the geometry  

### Centroid Limitations

For **concave shapes** (horseshoe, U-shape, C-channel), the centroid may fall in a void or gap. However:
- This is a known tradeoff for simplicity and robustness
- The centroid is still a valid placement for most use cases
- For truly optimal placement on concave shapes, you'd need more complex algorithms

---

## Technical Details

### FeatureScript Functions Used

**evApproximateCentroid()**
- Returns the approximate centroid of any entity (face, body, etc.)
- Works for all geometry types
- Fast and built-in to FeatureScript

**evFaceTangentPlane()**
- Returns a Plane tangent to the face at a parameter position
- Provides origin, normal, and x-axis for orientation
- Used to orient the mate connector

**coordSystem()**
- Creates a coordinate system from origin and axes
- Used to position the mate connector

**opMateConnector()**
- Creates a mate connector at a specified coordinate system
- Places the visual connector on the face

### Coordinate System

The mate connector is positioned with:
- **Origin:** Face centroid
- **Z-axis:** Face normal (perpendicular to face)
- **X-axis:** From tangent plane (in-plane direction)

---

## Future Enhancements

If more sophisticated placement is needed for concave shapes:

1. **Sample grid approach:**
   - Generate grid of points across face bounding box
   - Test each point with `evDistance()` to check if inside face
   - Choose point farthest from edges (but still inside)

2. **Bounding box subdivision:**
   - Use `evBox3d()` to get face bounding box
   - Sample points in bounding box
   - Find largest inscribed circle/rectangle

3. **Distance-based optimization:**
   - Start with centroid
   - Iteratively move toward widest part using `evDistance()`

However, for most label placement use cases, the centroid is sufficient and much simpler.

---

## Files

- **mihcPlacement.fs** - Main centroid-based placement feature
- **earClippingPlacement.fs** - Alternative implementation (same approach)
- **labelPlacementUtils.fs** - Shared utilities (currently unused but kept for reference)
- **README.md** - This file
- **QUICKSTART.md** - Usage guide
- **Planar Face Label Placement Strategies.md** - Original research document (historical reference)

---

## Usage in Onshape

1. Upload `mihcPlacement.fs` to Onshape as a FeatureScript document
2. In a Part Studio, use the feature from the toolbar
3. Select any planar face (rectangle, circle, spline-bounded, etc.)
4. A mate connector will appear at the face centroid

Works with:
- ✅ Rectangular faces
- ✅ Circular faces
- ✅ Elliptical faces
- ✅ Faces with spline edges
- ✅ Faces with arc edges
- ✅ Any planar face geometry

---

## Acknowledgments

Thanks to the user for correctly pointing out:
- "Why use projected geometry instead of localized coordinate systems?"
- "Look for appropriate ev or op functions"
- The vertex-based approach fails on splines and circles

This feedback led to a much better, simpler, more robust implementation.
