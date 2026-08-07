# Edge Change Relief Implementation

## Overview

This document describes the implementation of an alternative method for creating relief geometry in the Sheet Metal Stitch Cut Bend feature using `opEdgeChange` instead of cylinder subtraction.

## Problem Statement

The original implementation of relief geometry in `sheetMetalStitchCutBend.fs` uses the following approach:
1. Create cylinders by sweeping circles along relief edges
2. Use collision detection to find which faces each cylinder intersects
3. Perform boolean subtraction operations for each face

This approach can be slow on larger models, primarily due to:
- Geometry creation overhead (sketches, sweeps, fillets)
- Collision detection computation
- Multiple boolean operations

## Alternative Solution

The new implementation uses `opEdgeChange` to directly retract edges by a negative dimension:

### Key Components

#### 1. New Function: `retractReliefEdgesWithEdgeChange`

Located after `subtractReliefCylindersFromDefinition` in the file, this function:
- Takes the same parameters as the original function for compatibility
- Calculates the same offset distance (OSSB/ISSB base + bend relief adjustment)
- Uses `qAdjacent` to find faces adjacent to each relief edge
- Applies negative offset to retract edges using `opEdgeChange`

#### 2. User Interface Addition

A new boolean parameter has been added to the feature precondition:
```featurescript
annotation { "Name" : "Use edge change for relief (experimental)", "Default" : false }
definition.useEdgeChangeForRelief is boolean;
```

This allows users to toggle between the two methods for performance comparison.

#### 3. Method Selection Logic

The code now branches based on the parameter value:
```featurescript
if (definition.useEdgeChangeForRelief == true)
{
    // Alternative method: Use opEdgeChange for rectangular reliefs
    retractReliefEdgesWithEdgeChange(...);
}
else
{
    // Original method: Subtract cylinders from sheet metal definition
    subtractReliefCylindersFromDefinition(...);
}
```

## Implementation Details

### Edge Offset Calculation

The offset distance is calculated identically to the cylinder radius in the original method:

1. **Base Setback (OSSB/ISSB)**:
   - Inside-dimensioned: ISSB = R × tan(α/2)
   - Outside-dimensioned: OSSB = (R + T) × tan(α/2)

2. **Relief Depth Adjustment**:
   - Formula: ((depthScale - 1) × T) + (0.5 × widthScale × T)
   - Accounts for depth scale and width effects

3. **Total Offset**: Base + Adjustment

### Edge Change Application

For each relief edge:
1. Find all adjacent faces using `qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE)`
2. Create an edge change option for each adjacent face
3. Apply negative offset to retract the edge inward
4. Execute all edge changes in a single `opEdgeChange` operation

### Error Handling

- Graceful handling of edges that cannot be processed
- Try-catch blocks prevent individual edge failures from breaking the entire operation
- Outer try-catch allows the operation to fail without breaking the feature

## Limitations

### Current Implementation

- **Rectangular reliefs only**: The obround case is not supported in this alternative method
- The obround fillet logic (lines 1110-1123 in original) is intentionally omitted
- This limitation is acceptable as the goal is performance comparison for the rectangular case

### Why Only Rectangular?

The `opEdgeChange` operation creates straight edge offsets, which naturally produces rectangular reliefs. Obround reliefs require additional filleting operations that would negate the performance benefits of this approach.

## Testing Instructions

To compare the two methods:

1. Create a sheet metal model with stitch cut bends
2. Apply the feature with default settings (cylinder method)
3. Note the performance and regeneration time
4. Enable "Use edge change for relief (experimental)" parameter
5. Compare performance and verify geometric results

Expected outcomes:
- Edge change method should be faster, especially on models with many relief edges
- Geometry should be equivalent for rectangular reliefs
- Obround reliefs will only work with the cylinder method

## Reference Implementation

The `retractSurfaceEdges.fs` file in the custom-features directory provides a reference implementation of the `opEdgeChange` pattern used here.

## Code Locations

- Main feature: `custom-features/sheetMetalStitchCutBend.fs`
- Original method: `subtractReliefCylindersFromDefinition` (lines 943-1253)
- Alternative method: `retractReliefEdgesWithEdgeChange` (lines 1255-1426)
- Method selection: `processJointEntity` function (lines 549-563)
- UI parameter: Feature precondition (lines 84-86)

## Future Enhancements

Potential improvements to consider:
1. Support for obround reliefs using edge change + fillet operations
2. Automatic method selection based on model complexity
3. Performance metrics reporting
4. Batch processing optimizations for large model sets
