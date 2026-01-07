# Sheet Metal Boolean Tester

## Overview

The Sheet Metal Boolean Tester is a custom FeatureScript feature that exposes the special sheet metal boolean wiring used by Onshape's hole feature for countersink and counterbore operations. This is the only workflow in the Onshape engine that is allowed to violate normal sheet metal generation rules.

## Purpose

This tester feature allows you to:
- Perform boolean subtraction operations on sheet metal parts that would normally be prohibited
- Test the limits of the special sheet metal boolean wiring
- Experiment with complex cutting operations on sheet metal without following standard sheet metal building rules

## Background

In normal Onshape sheet metal workflows, boolean operations must follow strict rules about what geometry can interact with sheet metal parts. However, the hole feature's countersink and counterbore operations use a special internal function (`registerSheetMetalBooleanTools`) that bypasses these restrictions to enable cuts that violate normal sheet metal rules.

This tester exposes that same functionality for experimentation and testing purposes.

## How It Works

The feature uses the `registerSheetMetalBooleanTools` function from the Onshape Standard Library, which:

1. Performs collision detection between tool bodies and sheet metal targets
2. Associates tool bodies with the underlying sheet metal master body's walls using SMAttributes
3. Copies the tool bodies and registers them for boolean operations
4. Optionally calls `updateSheetMetalGeometry` to perform the actual boolean operation

## Usage

### Parameters

- **Operation type**: Currently only "Subtraction" is supported (like countersink/counterbore)
  - Union operations are listed as experimental but not yet implemented
  
- **Tool body**: The solid body to use as the cutting tool
  - Must be a solid body (not surface or sheet metal)
  - Must intersect with planar walls of the target sheet metal
  
- **Target sheet metal part**: The sheet metal part to cut from
  - Must be an active sheet metal part
  - Must be modifiable
  
- **Update geometry immediately** (default: true): 
  - When true: The boolean operation is performed immediately
  - When false: The tool is registered but the geometry update is deferred
  
- **Keep tool body** (default: false):
  - When false: The tool body is deleted after the operation (standard boolean behavior)
  - When true: The tool body is preserved after the operation

### Limitations

1. **Planar walls only**: The tool can only cut planar walls of the sheet metal part. If the tool intersects with:
   - Curved walls
   - Rolled walls
   - Rips
   - Joints
   - Corners
   - Side walls (non-planar features)
   
   Then the tool will not be registered and a warning will be displayed.

2. **Subtraction only**: Currently only subtraction operations are supported. The hole feature only uses subtraction for countersinks and counterbores, so union operations remain experimental and unsupported.

3. **Sheet metal targets only**: The target must be an active sheet metal part. Regular solid bodies are not supported.

## Technical Details

### Key Function: `registerSheetMetalBooleanTools`

This internal function is defined in `registerSheetMetalBooleanTools.fs` and performs the following operations:

```featurescript
registerSheetMetalBooleanTools(context, id, {
    "targets" : <sheet metal query>,
    "subtractiveTools" : <tool body query>,
    "doUpdateSMGeometry" : <boolean>
});
```

The function returns a map from sheet metal walls (as robust queries) to cutting tool body ID sets. If the map is empty, no tools were successfully registered.

### Behind the Scenes

1. **Collision Detection**: Uses `evCollision` to find intersections between tools and targets
2. **Planar Check**: Validates that tools only intersect with planar walls
3. **Tool Copying**: Uses `opPattern` with `HolePropagationType.PROPAGATE_SAME_HOLE` to copy tools
4. **Attribute Assignment**: Assigns tool body IDs to wall attributes via SMAttribute
5. **Geometry Update**: Calls `updateSheetMetalGeometry` to perform the actual boolean on patch bodies

### Comparison to Standard Boolean

Unlike the standard `booleanBodies` feature which validates sheet metal operations, this tester uses the special hole feature pathway that:
- Allows cuts that violate normal sheet metal build order
- Works on the sheet metal's internal patch body representation
- Registers tools via attributes rather than direct boolean operations
- Enables complex cutting geometry like countersinks on folded sheet metal

## Examples

### Basic Countersink Test
1. Create a sheet metal part with a planar face
2. Create a cone or other cutting solid that intersects the planar face
3. Use the Sheet Metal Boolean Tester with:
   - Operation type: Subtraction
   - Tool body: Your cutting solid
   - Target: Your sheet metal part
   - Update geometry: true
   - Keep tool: false

### Testing Tool Registration Without Cutting
1. Follow the same setup as above
2. Set "Update geometry immediately" to false
3. Set "Keep tool body" to true
4. The tool will be registered but not immediately applied
5. Check the feature info message to see how many tools were registered

## Troubleshooting

### "No tools were registered" Warning
This means the tool either:
- Does not intersect the sheet metal at all
- Only intersects non-planar features (bends, corners, etc.)
- Is not properly configured as a solid body

**Solution**: Ensure your tool intersects planar faces of the sheet metal part.

### "Selected target is not a sheet metal part" Error
The selected target is not recognized as an active sheet metal part.

**Solution**: Select a body that has been created using sheet metal features (Sheet Metal Start, Flange, etc.).

### Tool Disappears Unexpectedly
If "Keep tool body" is set to false (default), the tool is deleted after the operation.

**Solution**: Set "Keep tool body" to true if you want to preserve the tool.

## Development Notes

- This feature is marked as "Under development, not for general use"
- It is intended for testing and experimentation
- Future work may include:
  - Union operation support (requires investigation of additive sheet metal operations)
  - Multiple tool body support
  - More detailed reporting of which walls were affected
  - Support for non-planar wall operations (if technically feasible)

## Version Information

- **FeatureScript Version**: 2837
- **Library Version**: 2837.0
- **Key Dependencies**:
  - `registerSheetMetalBooleanTools.fs`
  - `sheetMetalUtils.fs`
  - `geomOperations.fs`

## References

- See `hole.fs` in the standard library for the canonical usage in countersink/counterbore operations
- See `registerSheetMetalBooleanTools.fs` for the implementation details
- See `SHEET_METAL_GOTCHAS.md` for general sheet metal development guidelines
