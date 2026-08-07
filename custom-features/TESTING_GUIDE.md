# Testing Guide: Sheet Metal Surface Hiding Experiment

## Quick Start

This experiment consists of two FeatureScript features that work together to test whether surfaces can be hidden using sheet metal annotations while remaining queryable.

## Files Created

1. **hideEdgeSurface.fs** - Creates and hides a surface
2. **queryHiddenSurface.fs** - Queries for hidden surfaces
3. **SHEET_METAL_SURFACE_HIDING_EXPERIMENT.md** - Detailed documentation
4. **TESTING_GUIDE.md** - This file

## Step-by-Step Testing

### Step 1: Set Up Your Test Part Studio

1. Create a new Part Studio in Onshape
2. Create a simple solid body:
   - Easiest: Create a box (e.g., 100mm x 100mm x 100mm)
   - Or: Create any solid with clear edges

### Step 2: Add the Custom Features

1. Copy the contents of `hideEdgeSurface.fs` to a new Custom Feature in your Part Studio
2. Copy the contents of `queryHiddenSurface.fs` to another new Custom Feature in your Part Studio

### Step 3: Create a Hidden Surface

1. Insert the "Hide Edge Surface (Experiment)" feature
2. Configure:
   - **Edge to create surface along**: Select any edge on your solid
   - **Offset distance**: Enter 10mm (or any value)
   - **Custom property value**: Enter "TestSurface1" (or any text)
3. Click the green checkmark to confirm
4. Check the console output - you should see: "Hidden edge surface created with custom property: TestSurface1"

### Step 4: Query for the Hidden Surface

1. Insert the "Query Hidden Surface (Experiment)" feature
2. Configure:
   - **Search scope**: Optionally select your solid body
   - **Experiment type filter**: Enter "hiddenEdgeSurface"
3. Click the green checkmark to confirm
4. Review the console output to see which query methods found the surface

## Expected Results

### What Should Happen

The console output from Step 4 should show:
- Multiple query methods attempting to find the hidden surface
- Some methods should successfully find the surface and report:
  - Attribute ID
  - Custom Property ("TestSurface1")
  - Edge ID
  - Offset Distance (10mm)
  - Experiment Type ("hiddenEdgeSurface")

### Query Methods That Should Work

Based on the sheet metal architecture, these methods are most likely to succeed:
- **Method 1**: Query by SMObjectType.WALL (using getAttributes)
- **Method 2**: Query using qAttributeQuery
- **Method 3**: Query all SHEET bodies
- **Method 6**: Query using qAttributeFilter

### Query Methods That May Not Work

Some methods may not find the surface if sheet metal hiding truly isolates it:
- **Method 4**: getSMDefinitionEntities (requires proper sheet metal association)
- **Method 5**: Broader patterns may or may not include hidden entities

## Interpreting Results

### Success Indicators

The experiment is **successful** if:
1. The surface is created without errors
2. At least one query method finds the hidden surface
3. Custom properties can be retrieved from the surface's attributes
4. The surface is NOT visible in the graphics area (hidden)
5. The surface does NOT appear in standard body/face queries

### What This Means

If successful, this proves that:
- Surfaces can be hidden using defineSheetMetalFeature
- Hidden surfaces maintain their attributes
- Hidden surfaces can be queried using specialized methods
- This approach could be adapted for Frame-like hidden attribute bodies

### Failure Indicators

The experiment **fails** if:
1. The surface creation throws an error
2. No query methods can find the surface
3. Attributes cannot be retrieved
4. The surface remains visible in the graphics area

## Advanced Testing

### Test Multiple Surfaces

1. Create multiple edges on your solid
2. Run "Hide Edge Surface" on each edge with different custom properties
3. Query to see if all surfaces are found

### Test Surface Persistence

1. Create a hidden surface
2. Add other features after it (e.g., fillet, extrude)
3. Query again to see if the surface still exists

### Test Attribute Modification

1. Create a hidden surface
2. Try to modify its attributes in a subsequent feature
3. Query to verify the modifications

### Test Query Scope

1. Create multiple solid bodies
2. Hide surfaces on different bodies
3. Query with and without search scope to see filtering behavior

## Troubleshooting

### Error: "Cannot resolve entities"
- Make sure you selected a valid edge in Step 3
- Check that your solid body has proper topology

### Error: "Failed to create loft surface"
- The edge may be too complex or degenerate
- Try a different, simpler edge (straight edge works best)

### No Output in Console
- Make sure you have the console panel open in Onshape
- Check that println statements are not filtered out

### Surface Is Visible
- This may indicate that defineSheetMetalFeature doesn't hide the surface as expected
- Check if updateSheetMetalGeometry completed without errors
- This could be a significant finding about how sheet metal hiding works

## Reporting Results

When reporting your findings, please include:
1. Which query methods successfully found the surface
2. Whether the surface was visible or hidden
3. Console output from both features
4. Any errors encountered
5. Your Onshape version and environment

## Next Steps

Based on the results:
- **If successful**: Consider adapting this approach for Frame attributes or other use cases
- **If partially successful**: Investigate which specific aspects work and which don't
- **If unsuccessful**: Document the limitations and consider alternative approaches

## Safety Notes

This is an **experimental** feature:
- Not intended for production use
- May have unexpected side effects
- Should only be used in test Part Studios
- May behave differently in future Onshape versions

## Questions or Issues

If you encounter unexpected behavior or have questions:
1. Check the detailed documentation in SHEET_METAL_SURFACE_HIDING_EXPERIMENT.md
2. Review the code comments for implementation details
3. Consult Onshape's FeatureScript documentation for underlying functions
4. Test with simpler geometry if complex geometry causes issues
