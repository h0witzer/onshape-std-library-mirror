# FFD Plane-Based Manipulation Feature

## Overview

This feature provides an alternative, more intuitive method for Free-Form Deformation (FFD) that manipulates entire planes of control points instead of individual points. This approach addresses the UI complexity and instability issues present in the standard point-based FFD implementation.

## Problem Statement

The original FFD script (`freeFormDeformation.fs`) has solid core logic but suffers from:
- Clunky UI when manipulating many individual control points
- Unstable indexing when adding or removing spans
- Difficult mental model for users to understand point-by-point manipulation

## Solution

The plane-based FFD feature (`freeFormDeformationPlanes.fs`) solves these issues by:
- **Simplifying the lattice**: Uses 1 span in two directions, multiple spans in one chosen direction
- **Plane-based manipulation**: All control points on a plane move together as a unit
- **Stable indexing**: Planes are clearly numbered and easy to reference
- **Intuitive controls**: Think in terms of cross-sectional planes rather than individual points

## Key Concepts

### Lattice Structure
- Choose a primary manipulation direction: S (X-axis), T (Y-axis), or U (Z-axis)
- The lattice has 1 span in the other two directions, creating a 2×2 grid per plane
- Multiple spans exist in the selected direction, creating multiple planes
- Each plane contains 4 control points (corners of the 2×2 grid)

### Plane Manipulation
- **Translation**: Move entire planes along X, Y, or Z axes
- **Rotation**: Rotate planes around the manipulation direction axis
- **Unified Movement**: All 4 control points on a plane move together

### Workflow
1. Select surface(s) to deform
2. Choose manipulation direction (S, T, or U)
3. Set number of planes (2-12)
4. Enable "Edit planes" 
5. Click on a plane center to select it
6. Use the triad manipulator to translate the plane
7. Optionally adjust rotation in the parameter panel

## Comparison with Standard FFD

| Feature | Standard FFD | Plane-Based FFD |
|---------|--------------|-----------------|
| Manipulation Unit | Individual points | Entire planes |
| Spans per direction | 1-8 in each direction | 1 in two directions, 2-11 spans in one |
| Total control points | Up to 729 (9×9×9) | Up to 48 (4 points × 12 planes) |
| UI Complexity | High (many individual points) | Low (few planes) |
| Index Stability | Can be confusing | Simple plane numbering |
| Best Use Case | Localized, detailed deformations | Smooth, controlled cross-sectional deformations |

## Usage Examples

### Example 1: Tapering a Surface
1. Select a surface
2. Choose U_DIRECTION (Z-axis)
3. Set 3 planes
4. Select the top plane (plane 2)
5. Translate inward to create a taper

### Example 2: Creating a Twist
1. Select a surface
2. Choose U_DIRECTION (Z-axis)
3. Set 5 planes
4. Select middle and top planes
5. Apply rotation to create a twist effect

### Example 3: Bending
1. Select a surface
2. Choose S_DIRECTION (X-axis)
3. Set 4 planes
4. Translate planes progressively to create a smooth bend

## Technical Details

### Implementation
- Based on the FFD algorithm from Sederberg & Parry's 1986 paper
- Uses trivariate Bernstein polynomials for deformation
- Applies Rodrigues' rotation formula for plane rotation
- Maintains compatibility with NURBS surface representations

### Files
- **Main Feature**: `custom-features/freeFormDeformationPlanes.fs`
- **Reference Implementation**: `custom-features/freeFormDeformation.fs`
- **Documentation**: This file

### Key Functions
- `buildPlaneBasedFFDLattice()`: Creates lattice with plane organization
- `applyPlaneTransformationsToLattice()`: Applies transformations to plane control points
- `rotationAround3D()`: Rotates vectors using Rodrigues' formula
- `addPlaneManipulators()`: Creates UI manipulators for plane selection and movement

## Limitations and Considerations

1. **Fixed Grid**: Always uses 2×2 control point grid per plane (due to 1 span constraint)
2. **Direction Choice**: Must choose manipulation direction before editing
3. **Rotation Axis**: Rotation is always around the manipulation direction axis
4. **Best for Extrusion-like Surfaces**: Works best on surfaces that have clear cross-sections

## Future Enhancements

Potential improvements that could be added:
- Variable grid density per plane (more than 2×2)
- Multi-axis rotation options
- Plane insertion/deletion at runtime
- Copy plane transformations
- Symmetry constraints
- Non-uniform scaling of planes

## Integration with Existing Workflows

This feature complements rather than replaces the standard FFD:
- Use **plane-based FFD** for: sweeps, extrusions, global shape changes
- Use **standard FFD** for: localized detailed control, complex multi-directional deformations

Both features can be used sequentially on the same geometry for maximum flexibility.

## Troubleshooting

### Issue: Planes don't show up for selection
**Solution**: Enable "Edit planes" checkbox and ensure diagnostics are enabled to see plane centers

### Issue: Rotation has no effect
**Solution**: Ensure rotation angle is set in the plane transformations array for the selected plane

### Issue: Deformation looks faceted or rough
**Solution**: The output depends on the input surface control point density. Start with higher-quality input surfaces.

### Issue: Cannot manipulate end planes effectively
**Solution**: Add more planes to give more control over the ends. With 3 planes, middle plane controls the bend.

## References

- Original FFD Paper: Sederberg, T.W. and Parry, S.R., "Free-Form Deformation of Solid Geometric Models", SIGGRAPH 1986
- Whitepaper: `whitepaper-references/"Free-Form Deformation of Parametric CAD Geometry.pdf"`
- Standard FFD Implementation: `custom-features/freeFormDeformation.fs`
