# Free-Form Deformation (FFD) Feature for Onshape

## Overview

This FeatureScript implementation brings the classic Free-Form Deformation (FFD) algorithm to Onshape, allowing users to smoothly deform B-spline surfaces by manipulating a 3D lattice of control points.

## Algorithm

Based on the seminal 1986 paper by Sederberg and Parry: "Free-Form Deformation of Solid Geometric Models", this implementation uses a trivariate Bernstein polynomial tensor product to smoothly deform geometry.

### Mathematical Foundation

The FFD algorithm works by:

1. **Creating a Lattice**: A 3D grid of control points is created around the target surface, defined by the bounding box and user-specified resolution (S × T × U control points)

2. **Parameter Space Mapping**: Each point on the surface is mapped from world space (x, y, z) to parameter space (s, t, u) where each parameter ranges from 0 to 1 within the lattice

3. **Trivariate Evaluation**: The deformed position is computed using the trivariate tensor product:
   ```
   X(s,t,u) = Σᵢ Σⱼ Σₖ Bᵢ(s) · Bⱼ(t) · Bₖ(u) · Pᵢⱼₖ
   ```
   where:
   - `Bᵢ(s)` is the Bernstein polynomial
   - `Pᵢⱼₖ` is the control point at lattice position (i,j,k)

4. **Surface Deformation**: The control points of the B-spline surface are transformed using the FFD, preserving the surface's parametric structure

## Usage

### Basic Workflow

1. **Select Target Surface**: Choose the face or surface you want to deform
2. **Configure Lattice Resolution**: 
   - S direction spans: Controls resolution along the length
   - T direction spans: Controls resolution along the width  
   - U direction spans: Controls resolution along the height
3. **Enable Lattice Visualization**: Toggle "Show control lattice" to see the control structure
4. **Manipulate Control Points**: Use the triad manipulators to adjust control point positions
5. **Apply Deformation**: The surface updates in real-time to show the deformation

### Parameters

- **Face or surface to deform**: Single face selection
- **S/T/U direction spans**: Integer values (typically 2-8)
  - Lower values = fewer control points, coarser deformation
  - Higher values = more control points, finer local control
- **Show control lattice**: Boolean toggle to visualize the lattice wireframe

### Tips

- Start with a 2×2×2 or 3×3×3 lattice for simple deformations
- Increase resolution only where needed for local detail
- The lattice visualization helps understand the control structure
- Corner and edge control points are automatically shown as manipulators
- For larger lattices (>27 control points), only strategic points show manipulators

## Implementation Details

### File Structure

**Location**: `custom-features/freeFormDeformation.fs`

### Key Functions

- `createFFDLattice()`: Initializes the control point grid
- `bernsteinPolynomial()`: Evaluates Bernstein basis functions
- `worldToParameterSpace()`: Converts world coordinates to STU parameters
- `evaluateFFDTrivariate()`: Computes the trivariate tensor product
- `performFFDDeformation()`: Applies FFD to B-spline surface control points
- `addControlPointManipulators()`: Creates interactive manipulators
- `visualizeLattice()`: Renders the control lattice wireframe

### Data Structures

**FFDLattice**: Custom type storing:
- Bounding box
- Span counts (resolution)
- Control point counts
- Control point positions
- Lattice axes (S, T, U directions)

## Limitations

### Current Implementation

- **Surface Only**: Currently operates on individual faces/surfaces (not yet solid bodies)
- **B-spline Surfaces**: Best results with B-spline surfaces; other surface types are approximated
- **Performance**: Very high-resolution lattices (>8×8×8) may impact performance
- **Manipulator Limit**: Large lattices show subset of control points to keep UI manageable

### Future Enhancements

Potential improvements for future versions:
- Solid body deformation (not just surfaces)
- Multiple surface selection
- Preset deformation patterns (bend, twist, taper)
- Custom manipulator filtering
- Animation/keyframe support
- Constraint-based control point relationships

## Technical Notes

### FeatureScript Version

- Requires FeatureScript 2837 or later
- Uses standard library B-spline surface operations

### Dependencies

Standard Onshape libraries:
- `evaluate.fs`: Surface evaluation and approximation
- `geomOperations.fs`: B-spline surface creation
- `manipulator.fs`: Interactive manipulators
- `surfaceGeometry.fs`: B-spline type definitions

### Mathematical Accuracy

- Factorial calculations for binomial coefficients
- Double-precision floating-point for Bernstein polynomials
- Parameter space conversion uses cross products for generality

## Examples

### Example 1: Simple Bend
```
Surface: Planar face (100mm × 100mm)
Lattice: 3 × 2 × 2
Manipulation: Move corner control points upward
Result: Gentle bend in the surface
```

### Example 2: Local Bulge
```
Surface: Cylindrical face
Lattice: 4 × 4 × 3
Manipulation: Move central control points outward
Result: Localized bulge in the cylinder
```

### Example 3: Twist
```
Surface: Rectangular surface
Lattice: 2 × 2 × 5
Manipulation: Rotate top corner points
Result: Twisting deformation along height
```

## References

1. **Sederberg, T. W., & Parry, S. R. (1986)**. "Free-form deformation of solid geometric models." *ACM SIGGRAPH Computer Graphics*, 20(4), 151-160.

2. **Reference Implementation**: The JavaScript implementation in `non-featurescript-functions-reference/free-form-deformation-master/` served as algorithmic reference.

## Author Notes

This implementation prioritizes:
- **Mathematical Correctness**: Faithful to the original algorithm
- **User Experience**: Interactive manipulators and visual feedback
- **Code Clarity**: Well-documented, readable FeatureScript code
- **FeatureScript Best Practices**: Follows Onshape Standard Library conventions

The feature is designed to be a learning resource as well as a practical tool, with extensive inline documentation explaining each step of the FFD algorithm.
