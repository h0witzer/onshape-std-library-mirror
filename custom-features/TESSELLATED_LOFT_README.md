# Tessellated Loft Feature

## Overview

This is a standalone tessellated loft feature that provides direct access to the `opTessellatedLoft` operation introduced in version 2878 of the Onshape Standard Library. It was created by stripping down the `sheetMetalLoft.fs` feature to remove all sheet metal specific logic.

## Purpose

The tessellated loft feature allows you to experiment with the underlying tessellated loft operation without the constraints and complexity of sheet metal features. This is useful for:

- Learning how tessellated loft works
- Creating general-purpose lofted surfaces
- Prototyping new features based on tessellated loft
- Understanding the relationship between profiles, connections, and the resulting geometry

## What is Tessellated Loft?

Unlike traditional loft operations that create smooth NURBS surfaces, tessellated loft creates a faceted surface by triangulating between two profiles. The resolution of the tessellation is controlled by the chordal tolerance parameter.

## Features

- **Two Profile Loft**: Create lofted surfaces between two edge or vertex profiles
- **Chordal Tolerance Control**: Adjust the resolution of the tessellation
- **Connection Matching**: Optionally define how profiles should align
- **Automatic Matching**: Uses `evTessellatedLoftMatches` to automatically determine profile alignment
- **Rip Support**: Mark connections as rips to create discontinuities in the loft

## Usage

### Basic Usage

1. Select first profile (edges, vertices, wire bodies, or sheet body edges)
2. Select second profile
3. Adjust chordal tolerance to control tessellation resolution (smaller = more facets)
4. The feature will automatically match the profiles and create the loft

### Advanced Usage with Connections

1. Enable "Match connections" checkbox
2. Add connection items to explicitly define how profiles should align
3. For each connection:
   - Select vertices or edges on both profiles
   - Optionally mark as "Rip" to create a discontinuity
4. The feature will use your connections to control the loft alignment

## Parameters

### Profile 1 & Profile 2
- Accepts: Edges, vertices, faces, wire bodies, sheet bodies (edges), point bodies (vertices)
- Construction geometry is excluded

### Chordal Tolerance
- Controls the maximum deviation of a chord from the actual geometry
- Default: 0.001 meters
- Range: 0.00001 to 0.1 meters (or equivalent in other units)
- Smaller values = finer tessellation = more triangles

### Match Connections
- Boolean flag to enable manual connection definition
- When disabled, automatic matching is used

### Connections (when Match Connections is enabled)
- Array of connection definitions
- Each connection specifies vertices or edges on both profiles
- Rip flag controls whether the connection creates a discontinuity

## Differences from Sheet Metal Loft

The following sheet metal features have been removed:

- ✗ Sheet metal model parameters (thickness, bend radius, k-factor)
- ✗ Sheet metal attributes (wall, bend, joint annotations)
- ✗ Boolean operations with existing sheet metal bodies
- ✗ Automatic rip detection for closed profiles
- ✗ Profile transformation to match sheet metal definition surfaces
- ✗ Flip direction manipulator
- ✗ Sheet metal model creation (NEW vs ADD operations)

What remains:

- ✓ Core tessellated loft operation (`opTessellatedLoft`)
- ✓ Profile selection and validation
- ✓ Connection matching (manual and automatic)
- ✓ Chordal tolerance control
- ✓ Profile edge and vertex extraction
- ✓ Error handling and validation

## Implementation Details

### Key Functions

- **`getProfileEdgesAndVertices()`**: Extracts edges and vertices from various profile types
- **`packDefinition()`**: Prepares connection data for `opTessellatedLoft`
- **`convertMatchesToConnections()`**: Converts automatic match results to connection format
- **`createConnectionFromMatch()`**: Creates individual connection from match data
- **`collectMatchItems()`**: Processes vertex and edge match items

### Workflow

1. Validate profile selections
2. Check profiles don't intersect
3. Extract edges and vertices from profiles
4. Get automatic matches using `evTessellatedLoftMatches`
5. Convert matches to connections
6. Execute `opTessellatedLoft`

## Example Use Cases

### Simple Loft Between Two Sketches
- Sketch two closed or open profiles
- Select all edges from each sketch
- Run tessellated loft

### Loft with Explicit Alignment
- Create profiles with specific features you want to align
- Enable "Match connections"
- Add connections to explicitly pair vertices or edge points
- Observe how connections affect the loft

### Variable Resolution Loft
- Create a base loft with default tolerance
- Duplicate and adjust chordal tolerance
- Compare faceting resolution

## Limitations

- Only works with two profiles (no multi-section lofting)
- Profiles must not intersect
- Creates faceted surfaces, not smooth NURBS
- No end conditions or tangency controls
- No guide curves support

## Tips

- Start with larger chordal tolerance and refine as needed
- Use automatic matching for simple cases
- Add explicit connections when profiles have complex alignment requirements
- Check profile distance before running (profiles too close will fail)
- For closed profiles, consider adding a connection to control the seam location

## Related Functions

- `opTessellatedLoft`: The underlying operation (from `geomOperations.fs`)
- `evTessellatedLoftMatches`: Automatic profile matching (from `evaluate.fs`)
- `sheetMetalLoft`: The full sheet metal version of this feature

## Version

- FeatureScript Version: 2878
- Based on: `sheetMetalLoft.fs` from Onshape Standard Library

## License

This follows the same MIT License as the Onshape Standard Library.
