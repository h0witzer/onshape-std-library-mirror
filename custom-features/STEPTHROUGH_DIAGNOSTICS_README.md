# Step-Through Diagnostics Utility

## Overview

The Step-Through Diagnostics utility (`stepThrough.fs`) provides an interactive debugging mechanism for FeatureScript development. Instead of commenting out blocks of code or scattering println statements throughout your feature, you can insert diagnostic checkpoints that pause execution and display current state information in a structured, easy-to-read format.

## The Problem This Solves

FeatureScript does not have a built-in step-through debugger, which makes diagnosing issues in complex features challenging. Developers typically resort to:

- Commenting out large blocks of code to isolate problems
- Adding multiple println statements to trace execution
- Manually inserting debug() calls throughout the code
- Rebuilding the feature repeatedly to test different sections

This utility provides a more elegant solution by allowing you to insert checkpoints that:
- Display variable states at specific execution points
- Inspect and highlight query results visually
- Pause execution at strategic locations
- Provide a clear, formatted output of diagnostic information

## Features

### 1. Interactive Feature Checkpoints
Add a "Step-Through Diagnostics" feature to your Part Studio that acts as a visual checkpoint. The feature allows you to:
- Name your checkpoint for easy identification
- Display up to 5 state variables with their current values
- Inspect up to 3 queries with automatic entity highlighting
- Add developer notes for context
- Enable/disable checkpoints without removing them
- Choose output display mode (Feature info, Console notices, or Both)

### 2. Programmatic Checkpoints
Import the utility functions into your own features for inline debugging:

```featurescript
import(path : "path/to/stepThrough.fs", version : "2837.0");

// Simple checkpoint with state variables
stepThrough(context, id + "loopCheck" ~ index, {
    "iteration" : index,
    "itemsProcessed" : processedCount,
    "currentQuery" : size(evaluateQuery(context, myQuery))
});

// Checkpoint with query inspection
stepThroughWithQuery(context, id + "faceCheck", {
    "faceCount" : size(faces),
    "areaProcessed" : totalArea
}, myFaceQuery);
```

## Usage Guide

### Method 1: Using the Feature UI

1. **Add the Feature**: Insert a "Step-Through Diagnostics" feature in your Part Studio where you want to inspect state

2. **Configure the Checkpoint**:
   - **Checkpoint name**: Give it a descriptive name (e.g., "Loop iteration check", "After boolean operation")
   - **Display mode**: Choose where to see the output
     - "Feature info (recommended)": Shows in the feature's info panel
     - "Notices": Shows in the FeatureScript console
     - "Both": Shows in both locations
   - **Enable this checkpoint**: Toggle to enable/disable without deleting

3. **Add State Variables**:
   - Variable 1-5 name: Enter the variable name as you want it displayed
   - Variable 1-5 value: Enter any FeatureScript expression (variables, function calls, etc.)
   
   Example:
   ```
   Variable 1 name: Loop Index
   Variable 1 value: loopIndex
   
   Variable 2 name: Query Size
   Variable 2 value: size(evaluateQuery(context, qCreatedBy(id)))
   ```

4. **Inspect Queries** (optional):
   - Select up to 3 queries to inspect
   - Queries will be highlighted in the viewport
   - Entity counts (bodies, faces, edges, vertices) will be displayed

5. **Add Notes** (optional):
   - Add developer notes explaining what you're checking at this point

6. **Regenerate**: The feature will pause at this checkpoint and display all diagnostic information

7. **Continue**: Edit the feature to continue, or disable it to skip this checkpoint in future regenerations

### Method 2: Programmatic Usage in Your Features

1. **Import the module**:
```featurescript
import(path : "path/to/stepThrough.fs", version : "2837.0");
```

2. **Add checkpoints in your code**:

#### Simple State Checkpoint
```featurescript
for (var index = 0; index < size(items); index += 1)
{
    stepThrough(context, id + "itemCheck" ~ index, {
        "iteration" : index,
        "currentItem" : items[index],
        "processedSoFar" : processedCount
    });
    
    // Your processing code here
    processItem(items[index]);
}
```

#### Checkpoint with Query Inspection
```featurescript
const selectedFaces = qEntityFilter(qCreatedBy(id, "extrude"), EntityType.FACE);

stepThroughWithQuery(context, id + "faceInspection", {
    "totalFaces" : size(evaluateQuery(context, selectedFaces)),
    "processingStep" : "After extrude"
}, selectedFaces);
```

## Examples

### Example 1: Debugging a Loop

```featurescript
for (var edgeIndex = 0; edgeIndex < size(edges); edgeIndex += 1)
{
    const currentEdge = edges[edgeIndex];
    const edgeLength = evLength(context, { "entities" : currentEdge });
    
    stepThrough(context, id + "edgeLoop" ~ edgeIndex, {
        "checkpoint" : "Processing edge " ~ edgeIndex,
        "edgeIndex" : edgeIndex,
        "totalEdges" : size(edges),
        "currentLength" : edgeLength,
        "isLongEnough" : edgeLength > minimumLength
    });
    
    if (edgeLength > minimumLength)
    {
        // Process this edge
        processEdge(context, id, currentEdge);
    }
}
```

**Output in Feature Info:**
```
═══ STEP-THROUGH CHECKPOINT ═══
Checkpoint: Processing edge 3
────────────────────────────────

▼ State Variables:
  • checkpoint: Processing edge 3
  • edgeIndex: 3
  • totalEdges: 12
  • currentLength: 45.2 mm
  • isLongEnough: true

────────────────────────────────
Edit this feature to continue or
disable it to skip this checkpoint.
═══════════════════════════════
```

### Example 2: Debugging Query Operations

```featurescript
const allFaces = qCreatedBy(id, "extrude");
const planeFaces = qGeometry(allFaces, GeometryType.PLANE);
const largeFaces = qLargest(planeFaces);

stepThroughWithQuery(context, id + "queryDebug", {
    "operation" : "Face selection",
    "allFaceCount" : size(evaluateQuery(context, allFaces)),
    "planeCount" : size(evaluateQuery(context, planeFaces)),
    "largestCount" : size(evaluateQuery(context, largeFaces))
}, largeFaces);

// The selected faces will be highlighted in red in the viewport
```

### Example 3: Debugging Conditional Logic

```featurescript
if (definition.useAdvancedMode)
{
    stepThrough(context, id + "advancedMode", {
        "mode" : "Advanced",
        "tolerance" : definition.tolerance,
        "iterations" : definition.maxIterations,
        "useApproximation" : definition.approximate
    });
    
    performAdvancedOperation(context, id, definition);
}
else
{
    stepThrough(context, id + "simpleMode", {
        "mode" : "Simple",
        "standardTolerance" : TOLERANCE.zeroLength
    });
    
    performSimpleOperation(context, id, definition);
}
```

## Best Practices

1. **Use Descriptive Checkpoint Names**: Make it easy to identify where execution paused
   - Good: "After face selection", "Loop iteration 5 of pattern"
   - Avoid: "Checkpoint 1", "Debug here"

2. **Include Context in State Variables**: Show not just values, but what they mean
   - Include iteration counters in loops
   - Show total counts alongside current indices
   - Display boolean flags that affect logic flow

3. **Strategic Checkpoint Placement**:
   - At the start and end of complex loops
   - Before and after critical operations
   - At decision points in conditional logic
   - After query operations to verify results

4. **Disable Instead of Delete**: When you've fixed an issue, disable checkpoints instead of removing them. They might be useful again later.

5. **Use Query Inspection**: When debugging geometry operations, always use query inspection to visualize what entities you're working with

6. **Combine with Standard Debug Tools**: Use stepThrough alongside debug() and println() for comprehensive debugging

## Technical Details

### Function Signatures

```featurescript
/**
 * Create a diagnostic checkpoint with state inspection
 * @param context : Current context
 * @param checkpointId : Unique ID for this checkpoint
 * @param stateMap : Map of variable names to values
 */
export function stepThrough(context is Context, checkpointId is Id, stateMap is map)

/**
 * Create a diagnostic checkpoint with query inspection
 * @param context : Current context
 * @param checkpointId : Unique ID for this checkpoint
 * @param stateMap : Map of variable names to values
 * @param query : Query to inspect and highlight
 */
export function stepThroughWithQuery(context is Context, checkpointId is Id, stateMap is map, query is Query)
```

### Output Formats

The utility formats output for maximum readability:
- Sections are clearly delimited with visual separators
- Variable lists use bullet points
- Query results show entity type counts
- Multiple display modes allow flexibility in how you view diagnostics

### Performance Considerations

- Checkpoints only execute when enabled
- Minimal overhead when disabled
- Query evaluation happens only for non-empty queries
- No persistent state or context modifications

## Troubleshooting

**Checkpoint doesn't appear**: 
- Ensure the checkpoint is enabled
- Check that the display mode is set correctly
- Verify the feature is being regenerated

**Variables show "undefined"**:
- Check variable names are spelled correctly
- Ensure variables are in scope at the checkpoint location
- Use FeatureScript expressions that are valid at that point

**Query inspection shows nothing**:
- Verify the query evaluates to entities at that point in execution
- Check that entities haven't been consumed by a previous operation
- Use debug(context, query) separately to verify query validity

## Integration with Existing Code

The utility is designed to be non-invasive:
- No modifications to your feature's signature required
- No context state changes
- Can be added/removed without affecting feature behavior
- Compatible with all FeatureScript versions 2837+

## Future Enhancements

Potential improvements for future versions:
- Variable history tracking across checkpoint activations
- Conditional checkpoint activation based on state
- Export diagnostic data for analysis
- Integration with external logging systems
- Automatic checkpoint generation from code analysis

## Support and Contributions

This utility is part of the custom features collection. For issues, improvements, or questions, please refer to the repository's contribution guidelines.

## License

This utility follows the same MIT license as the Onshape Standard Library mirror. See LICENSE.txt for details.
