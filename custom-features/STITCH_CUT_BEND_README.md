# Sheet Metal Stitch Cut Bend Feature

## Overview

The **Stitch Cut Bend** feature is a specialized sheet metal tool that combines the attribute modification logic from the **Modify Joint** feature with the spacing calculations from the **Tab and Slot** feature. It allows you to convert a continuous sheet metal edge joint into a series of alternating bend and rip segments, creating a "stitched" connection pattern commonly used in sheet metal fabrication.

## Concept

In sheet metal manufacturing, a **stitch cut bend** (also known as a **stitch weld pattern** or **intermittent bend**) creates:
- **Stitches**: Short bend segments that maintain the connection between two sheet metal walls
- **Gaps**: Cut sections (rips) between the stitches that provide flexibility, reduce stress, or allow for manufacturing processes

This pattern is useful for:
- Creating flexible connections in sheet metal assemblies
- Reducing material stress at bend lines
- Allowing for thermal expansion
- Facilitating manufacturing processes that require partial separation

## How It Works

### 1. Edge Selection
- Select a single sheet metal joint edge (typically an existing bend or rip)
- The feature works on sheet metal definition entities in the master body

### 2. Edge Splitting
The feature splits the selected edge into multiple segments at calculated positions:
```
Original edge: |-----------------------------------|

After splitting: |--|  |--|  |--|  |--|  |--|  |--|
                  ^    ^    ^    ^    ^    ^
                Stitches (bends) and gaps (rips)
```

### 3. Attribute Assignment
Each segment receives an appropriate sheet metal joint attribute:
- **Stitch segments**: Assigned bend attributes (or custom joint type)
- **Gap segments**: Assigned rip attributes (or custom joint type)

### 4. Geometry Update
The sheet metal model is rebuilt with the new attributes, creating the physical geometry in both folded and flat states.

## Parameters

### Joint Selection
- **Joint edge**: The sheet metal edge to convert into a stitch cut bend

### Joint Types
- **Stitch type**: The joint type for stitch segments (default: BEND)
  - `BEND`: Creates bending segments at stitch locations
  - `RIP`: Creates rips (useful for inverse patterns)
  - `TANGENT`: Creates tangent joints at stitch locations

- **Gap type**: The joint type for gap segments (default: RIP)
  - `RIP`: Creates cuts between stitches
  - `BEND`: Creates bends between stitches (inverse pattern)
  - `TANGENT`: Creates tangent joints between stitches

### Bend Parameters (when Stitch type = BEND)
- **Use model bend radius**: Use the default bend radius from the sheet metal model
- **Bend radius**: Custom bend radius for stitch segments (if not using default)
- **Use model K Factor**: Use the default K-factor from the sheet metal model
- **K Factor**: Custom K-factor for bend calculation (if not using default)

### Rip Parameters (when Gap type = RIP)
- **Gap style**: The style of rip to create in gaps
  - `EDGE`: Standard edge rip
  - `BUTT`: Butt joint (direction 1)
  - `BUTT2`: Butt joint (direction 2)

### Stitch Sizing and Spacing
- **Stitch width**: The width of each stitch (bend segment)

### Spacing Type
The feature supports three spacing modes:

#### 1. Equal Spacing
Distributes stitches evenly along the edge with equal gaps between them.

**Parameters:**
- **Instance count**: Number of stitches to create
- **Pattern mode**:
  - `Gap at ends`: Places gaps at both ends of the edge
  - `Instance at ends`: Places stitches at both ends of the edge
- **Actual pitch** (read-only): Computed center-to-center distance between stitches

**Optional offsets:**
- **Use offsets**: Enable offset control from edge ends
- **Two offsets**: Use different offsets for each end
- **Offset / Offset 1 / Offset 2**: Distance to offset from edge ends
- **Opposite direction**: Swap which offset applies to which end

#### 2. Distance Spacing
Places stitches at fixed pitch (center-to-center) intervals.

**Parameters:**
- **Instance count**: Number of stitches to create
- **Distance**: Center-to-center spacing between consecutive stitches

#### 3. Best Fit Spacing
Automatically calculates the number of stitches to fit along the edge with a target pitch.

**Parameters:**
- **Target pitch**: Desired center-to-center distance between stitches
- **Actual pitch** (read-only): Computed actual pitch used
- **Instance count** (read-only): Computed number of stitches that fit
- **Pitch ceiling**: Round up (ceiling) instead of rounding to nearest integer
- **Pattern mode**: `Gap at ends` or `Instance at ends`

**Optional offsets** (same as Equal Spacing)

## Examples

### Example 1: Standard Stitch Cut Bend
```
Configuration:
- Stitch type: BEND
- Gap type: RIP
- Stitch width: 0.25 inch
- Spacing type: Equal spacing
- Instance count: 5
- Pattern mode: Gap at ends

Result:
|_gap_|bend|_gap_|bend|_gap_|bend|_gap_|bend|_gap_|bend|_gap_|
```

### Example 2: Flexible Connection with Offsets
```
Configuration:
- Stitch type: BEND
- Gap type: RIP
- Stitch width: 0.5 inch
- Spacing type: Equal spacing
- Instance count: 4
- Use offsets: true
- Offset: 1 inch (from both ends)

Result:
|___offset___|bend|_gap_|bend|_gap_|bend|_gap_|bend|___offset___|
```

### Example 3: Fixed Pitch Pattern
```
Configuration:
- Stitch type: BEND
- Gap type: RIP
- Stitch width: 0.25 inch
- Spacing type: Distance
- Instance count: 6
- Distance: 2 inches

Result:
|bend|_____2"_____|bend|_____2"_____|bend|_____2"_____|...
```

## Technical Implementation

### Architecture
The feature is implemented in `/custom-features/sheetMetalStitchCutBend.fs` and follows these key patterns:

1. **Sheet Metal Feature Wrapper**: Uses `defineSheetMetalFeature()` to ensure proper integration with the sheet metal workflow

2. **Spacing Utilities**: Imports and uses the centralized `spacingUtils.fs` module for consistent pattern spacing calculations

3. **Attribute Management**: 
   - Gets existing joint attributes
   - Creates new attributes for each segment
   - Uses `replaceSMAttribute()` for atomic attribute updates
   - Calls `updateSheetMetalGeometry()` to rebuild the model

4. **Edge Splitting**:
   - Constructs an ordered path from selected edges
   - Calculates normalized split parameters (0 to 1 along path)
   - Converts to per-edge parameters accounting for edge orientation
   - Uses `opSplitEdges()` for actual geometry splitting

5. **Segment Identification**:
   - Tracks split edges using `startTracking()`
   - Identifies which segments fall within stitch domains using midpoint analysis
   - Separates stitch segments from gap segments

### Key Functions

- `findJointDefinitionEntity()`: Locates the sheet metal definition entity from user selection
- `applyJointAttributesToSegments()`: Creates and assigns attributes to edge segments
- `calculateSplitParametersFromDomains()`: Converts stitch domains to edge split parameters
- `calculateEdgeSplitInstructionsFromParameters()`: Maps split parameters to specific edges
- `identifySegmentsByEdgeMidpoints()`: Determines which edges fall within stitch domains

### Dependencies

Required imports from Onshape Standard Library:
- `sheetMetalAttribute.fs`: Attribute creation and manipulation
- `sheetMetalUtils.fs`: Geometry update and model parameters
- `path.fs`: Edge path construction
- `geomOperations.fs`: Edge splitting operations
- `smjointtype.gen.fs` and `smjointstyle.gen.fs`: Joint type enumerations

External dependency:
- `spacingUtils.fs`: Centralized pattern spacing calculations (from custom-features/spacing_utilities)

## Usage Guidelines

### When to Use
- Creating flexible sheet metal connections that need controlled separation points
- Reducing stress concentrations at continuous bend lines
- Manufacturing scenarios requiring partial edge separation
- Creating perforated or ventilated bend patterns

### Best Practices
1. **Start with simple patterns**: Begin with equal spacing and gap-at-ends mode
2. **Consider stitch width**: Ensure stitches are wide enough to maintain structural integrity
3. **Check overlaps**: The feature validates that stitches don't overlap, but verify the pattern makes sense for your design
4. **Test flat patterns**: Always check that the flat pattern develops correctly
5. **Use appropriate gap types**: RIP is standard for cuts; consider TANGENT for smoother transitions

### Limitations
- Cannot be applied to face bends (only edge joints)
- Cannot be used in feature patterns
- Must be applied to active sheet metal models
- Requires edges that can be ordered into a continuous chain
- Stitch count and width must allow non-overlapping segments

## Troubleshooting

### "Cannot create stitch cut bend on a face bend"
**Cause**: Selected entity is a face bend, not an edge bend
**Solution**: Select an edge joint instead

### "No stitches can fit with the specified parameters"
**Cause**: Stitch width or count is too large for the edge length
**Solution**: Reduce stitch width or instance count, or increase offset distances

### "Resultant stitches would overlap"
**Cause**: The calculated stitch positions would create overlapping segments
**Solution**: 
- Increase spacing distance (for distance mode)
- Decrease instance count (for equal/best-fit modes)
- Reduce stitch width
- Adjust target pitch (for best-fit mode)

### "Unable to order the selected edges into a continuous chain"
**Cause**: Selected edges don't form a continuous path
**Solution**: Ensure edges are connected end-to-end in sequence

### "Failed to split the sheet metal edge"
**Cause**: Edge split operation encountered geometric issues
**Solution**:
- Check that the edge is valid sheet metal geometry
- Verify the edge isn't at a complex junction
- Try adjusting spacing parameters to avoid splitting at problematic locations

## Related Features

- **Modify Joint** (`sheetMetalJoint.fs`): Base feature for modifying individual joint attributes
- **Tab and Slot** (`sheetMetalTabAndSlot`): Uses similar spacing logic for adding tabs to sheet metal
- **Sheet Metal Bend** (`sheetMetalBend.fs`): Creates new bends in sheet metal parts
- **Sheet Metal Rip** (`sheetMetalRip.fs`): Creates rips to separate sheet metal walls

## Version History

- **Initial Release**: Complete implementation with spacing utilities integration
  - Full spacing mode support (Equal, Distance, Best Fit)
  - Bend and rip attribute assignment
  - Edge splitting and segment identification
  - Sheet metal geometry update integration
