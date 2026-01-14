# Developable Strips Feature

## Overview

Implementation of the novel method from **"All you need is rotation: Construction of developable strips"** by Takashi Maekawa and Felix Scholz (ACM Transactions on Graphics, Volume 43, Issue 6, December 2024).

This feature generates developable strips along a space curve using rotation angles between the Frenet frame and Darboux frame as a free design parameter. The key innovation is that the rotation angle can be any differentiable function, creating a large design space of developable strips sharing a common directrix curve.

## Key Features

- **Constant Rotation Angle Mode**: Simple fixed angle between frames
- **Linear Variation Mode**: Linearly varying rotation angle along the curve
- **Sinusoidal Pattern Mode**: Sinusoidal variation for wave-like patterns
- **Constant Tangent-Ruling Angle Mode** (ODE-based): Maintains constant angle between tangent and ruling direction
- **Rotation Minimizing Frame (RMF)**: Minimal twist developable surfaces
- **Edge of Regression Control**: Visualize and control singular curves
- **Strip Width Control**: Symmetric or asymmetric width control
- **B-spline Surface Representation**: High-quality surface output

## Applications

- **Architectural Design**: Curved facades, shell structures, pavilions
- **Industrial Design**: Sheet metal forming, product design
- **Papercraft Modeling**: Foldable structures, origami-inspired designs
- **Fabrication**: CNC cutting, laser cutting preparation

## Mathematical Background

### Frenet Frame

For a space curve c(t), the Frenet frame consists of:
- **Tangent** (t): Direction of curve at each point
- **Normal** (n): Points toward center of curvature
- **Binormal** (b): Perpendicular to both tangent and normal

### Darboux Frame

The Darboux frame is obtained by rotating the Frenet frame around the tangent axis by angle φ:
- **Tangent** (t): Remains unchanged
- **Binormal Star** (B*): B* = cos(φ) · n + sin(φ) · b
- **Normal Star** (N*): N* = -sin(φ) · n + cos(φ) · b

### Ruling Direction

The ruling direction of the developable surface is computed using equation (16) from the paper:

```
d = (τ_g · t - κ_n · B*) / sqrt(τ_g² + κ_n²)
```

where (equation 15):
- κ_n = -κ · sin(φ) (normal curvature)
- κ_g = κ · cos(φ) (geodesic curvature)
- τ_g = τ + dφ/ds (geodesic torsion)

### Edge of Regression

The edge of regression (cuspidal edge) is computed using equation (21):

```
e(t) = c(t) - [Λ · κ_n · τ_g · t - κ_n · B*] / 
               [Λ · κ_g · (κ_n² + τ_g²) + d(τ_g)/dt · κ_n - d(κ_n)/dt · τ_g]
```

## Usage

### Basic Usage

1. Create a space curve (edge) in your Onshape document
2. Add the Developable Strips feature
3. Select the input curve
4. Choose a rotation mode
5. Adjust parameters as needed
6. The feature will generate a ruled developable surface

### Rotation Modes

#### Constant Angle
- **Parameters**: Rotation angle φ
- **Effect**: Simple developable strip with fixed frame rotation
- **Use Case**: Basic developable surfaces, tangent/normal/rectifying developables

#### Linear Variation
- **Parameters**: Base angle φ₀, angle rate
- **Effect**: Rotation angle increases linearly along curve
- **Use Case**: Gradually twisting strips, spiral patterns

#### Sinusoidal Pattern
- **Parameters**: Base angle φ₀, amplitude, frequency
- **Effect**: Sinusoidal variation creates wave patterns
- **Use Case**: Decorative patterns, architectural features

#### Constant Tangent-Ruling Angle (ODE)
- **Parameters**: Tangent-ruling angle θ
- **Effect**: Maintains constant angle between tangent and ruling
- **Use Case**: Uniform strip behavior, predictable ruling distribution

#### Rotation Minimizing Frame (RMF)
- **Parameters**: None (automatically computed)
- **Effect**: Minimal twist, rotation angle φ = -∫τ(s)ds
- **Use Case**: Natural-looking twists, minimal distortion

### Width Control

- **Symmetric Width**: Same width on both sides of the directrix
- **Asymmetric Width**: Independent control of positive and negative sides
- **Width Adjustment**: Automatically compensates for non-orthogonal rulings

### Diagnostics and Visualization

Enable diagnostics to visualize:
- **Frenet Frame**: Red (tangent), Green (normal), Blue (binormal)
- **Darboux Frame**: Magenta (tangent), Cyan (B*), Yellow (N*)
- **Ruling Directions**: White lines showing ruling directions
- **Control Points**: Red/Blue points showing strip edges
- **Edge of Regression**: Yellow points and curve (if enabled)

### Advanced Options

- **Number of Sample Points**: Higher values = smoother surface, more computation
- **Print Diagnostics**: Output curve properties and angles to console

## Examples

### Example 1: Helix with Constant Angle

```
Input: Helix curve
Rotation Mode: Constant Angle
Rotation Angle: 90° (rectifying developable)
Strip Width: 10 mm
Result: Classic helical developable strip
```

### Example 2: Architectural Design

```
Input: Complex 3D curve
Rotation Mode: Linear Variation
Base Angle: 30°
Angle Rate: -0.06 degrees/unit
Strip Width: Asymmetric (varies along curve)
Result: Architectural facade element
```

### Example 3: Möbius Strip

```
Input: Special algebraic curve
Rotation Mode: Rotation Minimizing Frame
Result: Möbius strip geometry
```

## Implementation Details

### Torsion Computation

Since torsion requires the third derivative of the curve, which is not directly available in Onshape's evaluation functions, we compute it using finite differences on the Frenet frames:

```
τ(i) ≈ -(b(i+1) - b(i-1)) · n(i) / (2 · ds)
```

### Surface Generation

The developable strip is generated as a ruled surface between two B-spline curves:
1. Sample points along the input curve
2. Compute Frenet frames and torsion
3. Compute rotation angles based on mode
4. Compute Darboux frames
5. Compute ruling directions
6. Generate strip edge points
7. Fit B-spline curves to edges
8. Create ruled surface

### Numerical Methods

- **Finite Differences**: Used for torsion and angle derivatives
- **Central Differences**: More accurate than forward/backward when possible
- **Euler Integration**: Used for ODE-based modes (can be improved with RK4)

## Limitations and Future Enhancements

### Current Limitations

1. **Torsion Accuracy**: Finite difference approximation, not as accurate as analytical computation
2. **ODE Integration**: Simple Euler method, could use Runge-Kutta for better accuracy
3. **Edge of Regression**: Visualization only, not used for strip trimming
4. **Parameterization**: Assumes normalized (0-1) parameterization

### Planned Enhancements

1. **Strip Unfolding**: Flatten developable strips to 2D patterns
2. **Improved ODE Solver**: Implement RK4 for constant tangent-ruling angle mode
3. **Multiple Strips**: Generate multiple parallel strips
4. **Offset Surfaces**: Create triply orthogonal structures
5. **Fabrication Export**: Export flattened patterns for manufacturing

## References

- Maekawa, T., & Scholz, F. (2024). All you need is rotation: Construction of developable strips. ACM Transactions on Graphics, 43(6).
- Kreyszig, E. (1968). Introduction to differential geometry and Riemannian geometry.
- Scholz, F., & Maekawa, T. (2021). Computing Developable Surfaces with Arbitrary Boundaries.

## Citation

If you use this implementation in your work, please cite both the original paper and acknowledge this implementation:

```bibtex
@article{maekawa2024rotation,
  title={All you need is rotation: Construction of developable strips},
  author={Maekawa, Takashi and Scholz, Felix},
  journal={ACM Transactions on Graphics},
  volume={43},
  number={6},
  year={2024},
  publisher={ACM}
}
```

## License

This implementation follows the MIT License of the Onshape Standard Library mirror.

## Author

Implementation by the Onshape Community based on the paper by Maekawa & Scholz (2024).

## Version History

- **v1.0** (2026-01): Initial implementation
  - Core functionality: Frenet/Darboux frames, rotation angle modes
  - Torsion computation via finite differences
  - Edge of regression visualization
  - B-spline surface generation
