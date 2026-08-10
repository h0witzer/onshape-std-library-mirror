# Surface Refinement Feature - User Guide

## Overview

The **Refine Surface** feature allows you to insert additional knots and control points into B-spline surfaces without changing the underlying geometry. This is particularly useful when working with Free-Form Deformation (FFD) on simple surfaces that have few control points.

## Purpose

### The Problem

When using FFD algorithms on simple surfaces (e.g., planes, cylinders, cones) with few control points, the lattice control points don't provide sufficient local influence over the middle of the surface. This is because there aren't enough control points on the surface itself for the lattice to manipulate.

### The Solution

The Refine Surface feature solves this by:
- **Inserting knots** uniformly in the parameter space
- **Adding control points** without changing the surface shape
- **Preserving geometry exactly** using mathematically correct knot insertion (Boehm algorithm)
- **Preparing surfaces** for more effective FFD manipulation

## How It Works

The feature uses the **Boehm algorithm** for knot insertion, which is the mathematically correct method for adding control points to B-splines and NURBS while maintaining the exact same geometry. The algorithm:

1. Obtains a B-spline representation of the input surface
2. Processes each isoparametric curve (rows and columns of control points) independently
3. Inserts new knots uniformly distributed across the parameter domain
4. Computes new control points using the Boehm knot insertion formula
5. Reconstructs the surface with the increased control point count

The result is a surface that looks identical to the original but has more control points, providing finer control for subsequent operations.

## Usage

### Basic Steps

1. **Select the surface** you want to refine
2. **Choose refinement mode**:
   - **Target count**: Specify exact number of control points desired
   - **Multiply by factor**: Multiply current control point count by a factor
3. **Set control point targets** for U and V directions
4. **Enable visualization** (optional) to see control points before/after
5. Execute the feature

### Refinement Modes

#### Target Count Mode (Default)

Specify the exact number of control points you want in each direction:
- **Target control points in U direction**: e.g., 10
- **Target control points in V direction**: e.g., 10

This mode is useful when you know exactly how many control points you need for your FFD operations.

#### Multiply by Factor Mode

Multiply the current control point count by a factor:
- **Multiply U control points by**: e.g., 2 (doubles the count)
- **Multiply V control points by**: e.g., 2 (doubles the count)

This mode is convenient when you want to systematically increase surface resolution without counting existing control points.

### Visualization Options

- **Show control points**: Displays control points as colored debug spheres
  - Blue: Original control points
  - Green: Refined control points (if refinement occurred)

### Diagnostics Options

For debugging and learning:
- **Print surface information**: Displays surface properties (degree, control point count, type)
- **Print refinement details**: Shows refinement plan and results

## Typical Workflow with FFD

### Example: Refining a Planar Surface for FFD

**Problem**: You have a simple rectangular plane with only 4 control points (2×2). When you apply FFD with a 4×4×4 lattice, the lattice points can't deform the middle of the surface effectively.

**Solution**:
1. Use **Refine Surface** to increase control points to 10×10
2. Now the surface has 100 control points distributed across its area
3. Apply **Free-Form Deformation** with your desired lattice
4. The FFD lattice can now exert local influence across the entire surface

### Example: Refining a Cylindrical Surface

**Problem**: A cylindrical surface has few control points in the circumferential direction, limiting localized FFD effects.

**Solution**:
1. Select the cylindrical face
2. Use **Refine Surface** with target counts of 15 (U) × 20 (V)
3. The refined surface maintains its cylindrical shape exactly
4. Apply FFD for localized deformations that now work smoothly

## Technical Details

### Supported Surface Types

- **B-spline surfaces** (non-rational): Processed directly
- **NURBS surfaces** (rational): Handled in homogeneous coordinates for correctness
- **Other surface types**: Automatically approximated as B-splines first

### Mathematical Guarantees

- **Exact geometry preservation**: The refined surface is geometrically identical to the original
- **Continuity preservation**: Surface smoothness (C0, C1, C2, etc.) is maintained
- **Knot uniformity**: New knots are uniformly distributed in parameter space

### Performance Characteristics

- **Memory**: Increases linearly with control point count
- **Time**: O(n × m) where n and m are the numbers of knots to insert in U and V directions
- **Stability**: Numerically stable for typical refinement factors (2-5×)

### Limitations

- **Cannot reduce** control point count (only increase)
- **Target counts** must be at least the current control point count
- **Large refinements** (e.g., 100×) may be slow and create very large surfaces

## Tips and Best Practices

### For FFD Preparation

1. **Start modest**: Refine to 2-3× the original control point count first
2. **Match lattice size**: If using FFD with N spans, consider refining to have at least N+1 control points per direction
3. **Test iteratively**: Refine → Test FFD → Refine more if needed

### For Performance

1. **Refine only what you need**: Don't over-refine if you don't need extreme local control
2. **Use multiply mode**: It's faster to think in terms of "2× the points" than counting exact numbers
3. **Visualize first**: Enable control point visualization to understand the current state

### For Learning

1. **Enable diagnostics**: See what's happening under the hood
2. **Try simple shapes**: Start with a plane or cylinder to see the effect clearly
3. **Compare with FFD**: Apply FFD before and after refinement to see the difference

## Common Questions

**Q: Will this change the visual appearance of my surface?**
A: No. The refined surface is geometrically identical to the original. You cannot tell them apart visually.

**Q: Can I undo or reduce control points after refinement?**
A: This feature only adds control points. To simplify a surface, you would need different operations (not currently available in this feature).

**Q: How many control points should I target for FFD?**
A: A good rule of thumb: For an FFD lattice with S×T×U spans, aim for at least (S+1) × (T+1) control points on your surface. Start with 10×10 for most cases.

**Q: Does this work with sheet metal or other specialized surface types?**
A: Yes, but attributes may not transfer. The feature works on the underlying geometry and creates a new B-spline surface.

**Q: Can I refine multiple surfaces at once?**
A: Currently, the feature processes one surface at a time. You can apply it to multiple surfaces sequentially.

**Q: What's the difference between this and the Tween Surfaces feature?**
A: Tween Surfaces interpolates between two surfaces. Refine Surface uses the same knot insertion algorithm but applies it to a single surface to add control points without changing shape.

## Relationship to Other Features

### Works Well With

- **Free-Form Deformation** (`freeFormDeformation.fs`): Primary use case - refine first, then deform
- **Free-Form Deformation Planes** (`freeFormDeformationPlanes.fs`): Plane-based FFD variant
- **Edit Curve**: Similar knot insertion concepts for curves

### Based On

- **Tween Surfaces** (`tweenSurfaces.fs`): Shares knot insertion algorithms
- **nurbsUtils.fs**: Uses standard library functions for NURBS operations

## Mathematical Background

The feature implements the **Boehm algorithm** for knot insertion, which is described in:
- Boehm, W. (1980). "Inserting new knots into B-spline curves." Computer-Aided Design, 12(4), 199-201.
- Piegl, L., & Tiller, W. (1997). The NURBS Book (2nd ed.). Springer-Verlag.

For B-spline surfaces, knot insertion is performed independently on each isoparametric curve (U-curves and V-curves), which is equivalent to inserting knots into the surface's knot vectors.

### The Boehm Formula

For inserting a knot u into a B-spline curve at span k, new control points Q_i are computed as:

```
Q_i = α_i × P_i + (1 - α_i) × P_(i-1)
```

where:
```
α_i = (u - u_i) / (u_(i+p) - u_i)
```

and:
- P_i are original control points
- u_i are original knot values
- p is the curve degree
- u is the knot value being inserted

This formula ensures the curve shape remains unchanged while adding a new control point.

## Troubleshooting

### "Target count must be at least the current count"

**Cause**: You specified a target count smaller than the current control point count.
**Solution**: Check the current control point count (enable diagnostics) and increase your target.

### "Invalid B-spline: knot vector size doesn't match"

**Cause**: Internal error in knot insertion algorithm (rare).
**Solution**: Check if your surface is degenerate or has unusual properties. Try approximating it first with a standard operation.

### Refinement is very slow

**Cause**: Attempting to refine to a very large control point count (e.g., 100×100).
**Solution**: Refine in smaller increments. Start with 2-3× factors and increase if needed.

### FFD still doesn't provide enough local control

**Cause**: Control points may still be insufficient for the FFD lattice size.
**Solution**: Refine further, or use a smaller FFD lattice (fewer spans).

## Examples

### Example 1: Simple Plane Refinement

```
Input:
  - Surface: Rectangular plane (2×2 control points)
  - Mode: Target count
  - Target U: 8
  - Target V: 8

Output:
  - Refined plane (8×8 control points = 64 total)
  - Geometry: Identical flat plane
  - Ready for: FFD with 4×4 or smaller lattice
```

### Example 2: Cylinder Multiplication

```
Input:
  - Surface: Cylinder (3×8 control points)
  - Mode: Multiply by factor
  - Multiply U: 2
  - Multiply V: 3

Output:
  - Refined cylinder (6×24 control points = 144 total)
  - Geometry: Identical cylindrical shape
  - Ready for: Detailed circumferential FFD
```

### Example 3: Complex Surface Preparation

```
Input:
  - Surface: Curved automotive panel (5×7 control points)
  - Mode: Target count
  - Target U: 12
  - Target V: 15

Output:
  - Refined panel (12×15 control points = 180 total)
  - Geometry: Identical complex curve
  - Ready for: Localized styling adjustments via FFD
```

## Version History

### Version 1.0
- Initial implementation
- Two refinement modes (target count, multiply factor)
- Support for rational and non-rational B-splines
- Control point visualization
- Diagnostic output

## See Also

- `freeFormDeformation.fs` - FFD feature that benefits from refined surfaces
- `tweenSurfaces.fs` - Surface interpolation using similar algorithms
- `editCurve.fs` - Curve editing with knot insertion
- Onshape Standard Library Documentation: https://cad.onshape.com/FsDoc/

## Credits

Based on algorithms from:
- Wolfgang Boehm's knot insertion papers (1980)
- "The NURBS Book" by Piegl & Tiller (1997)
- Onshape Standard Library nurbsUtils.fs implementation

Developed as part of the Onshape Standard Library Mirror project to address FFD usability on simple surfaces.
