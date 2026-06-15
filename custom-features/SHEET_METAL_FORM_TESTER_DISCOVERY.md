# Sheet Metal Form Tester Discovery: Bodies in Flat Pattern Context

## Summary

The sheet metal form tester has successfully spawned non-sheet-metal bodies inside the 2D flat pattern context window - a significant breakthrough in understanding sheet metal geometry manipulation.

## Observed Behavior

### Test Configuration
- **Operation**: Subtraction (FORM_BODY_NEGATIVE_PART)
- **Keep Tools**: Enabled
- **Update Geometry**: Enabled
- **Tool Body**: Marked with both `FORM_BODY_NEGATIVE_PART` and `FORM_BODY_SKETCH_FOR_FLAT_VIEW` attributes

### Results
1. **3D Viewport**: 
   - Target sheet metal body: No visible changes
   - Tool body: Disappeared from view

2. **Feature Tree**:
   - Reports 4 bodies total (original 2 + 2 new)
   - Bodies 2, 3, and 4 exist but are not visible in 3D viewport
   - Cannot be selected in viewport
   - Cannot be used in standard features (e.g., Shell)

3. **2D Flat Pattern Context**:
   - Bodies 2, 3, and 4 are **physically present** in the 2D context
   - Oriented unusually
   - Invisible to user without debug tools
   - **Can be visualized** using Query Explorer and Query Variable with debugging enabled

## Technical Analysis

### What Happened
The modified `registerSheetMetalFormedTools` function successfully:
1. Accepted solid bodies marked with `FORM_BODY_SKETCH_FOR_FLAT_VIEW` attribute
2. Registered them in the `sketchBodyIds` array within `formedToolBodyIds` attribute
3. Passed them to native `@updateSheetMetalGeometry` function
4. The native function **created copies** of the tool bodies in the flat pattern context

### Why This Is Significant
This is the **first successful attempt** at spawning non-sheet-metal geometry inside a sheet metal flat pattern context window. The standard sheet metal workflow strictly prohibits this, but by leveraging the form feature pathway with modified guards, we've bypassed the restrictions.

### The Bodies Created
- **Body 1**: Original sheet metal target (unchanged)
- **Body 2**: Original tool body (now invisible, possibly moved to flat context)
- **Body 3**: First generated body in flat context
- **Body 4**: Second generated body in flat context

The fact that there are **multiple bodies** generated suggests the native engine may be:
- Creating separate representations for different aspects of the form
- Duplicating the tool body for different processing stages
- Creating temporary intermediate geometry

## Potential Applications

### What Works
1. ✅ Solid bodies can be marked with `FORM_BODY_SKETCH_FOR_FLAT_VIEW` attribute
2. ✅ Modified FeatureScript guards allow registration of these bodies
3. ✅ Native `@updateSheetMetalGeometry` accepts and processes them
4. ✅ Bodies are created in the flat pattern 2D context
5. ✅ Bodies persist in the feature tree (queryable via debug tools)

### Current Limitations
1. ❌ Bodies are invisible in both 3D and 2D viewports
2. ❌ Bodies cannot be selected by user
3. ❌ Bodies cannot be used in standard features
4. ❌ Orientation appears arbitrary/incorrect
5. ❌ No visible effect on sheet metal geometry

## Next Steps for Investigation

### 1. Understanding Body Visibility
- Why are the bodies invisible?
- Is there an attribute controlling visibility in flat context?
- Can we query and modify visibility settings?

### 2. Body Orientation
- How is the native engine positioning/orienting the bodies?
- Can we control the transform applied?
- Is there a coordinate system mismatch?

### 3. Making Bodies Usable
- What attributes are needed to make bodies selectable?
- Can we apply sketch entities to make them visible?
- Can we convert the bodies to a usable form?

### 4. Leveraging the Mechanism
- Can we control which bodies are generated?
- Can we position the bodies predictably?
- Can we make the bodies interact with the flat pattern geometry?
- Can we extract/export the bodies for use elsewhere?

## Code Modifications Enabling This

### `registerSheetMetalFormedTools.fs`
1. **Bypassed footprint validation** (line ~135-145):
   - Bodies with `FORM_BODY_SKETCH_FOR_FLAT_VIEW` skip `isFormFootPrintOnFace` check
   - Allows tools at any position without intersection requirements

2. **Modified `isFormFootPrintOnFace`** (line ~20-45):
   - Try-catch around `evCollision` call
   - Returns `true` on failure instead of throwing error
   - Allows bodies with multiple attributes to pass validation

### `sheetMetalFormTester.fs`
- Marks solid tool bodies with both form attribute and sketch attribute
- Uses standard `registerSheetMetalFormedTools` call
- Tool bodies are passed through without conversion to wire/sketch

## Debugging Commands

### Query Explorer
```featurescript
// View all bodies in context
qEverything(EntityType.BODY)

// View bodies in flat pattern context
qInContextOf(qEverything(EntityType.BODY), qFlatContext())

// View bodies with form attributes
qHasAttribute(FORM_BODY_SKETCH_FOR_FLAT_VIEW)
```

### Query Variable
Set variable to any of the above queries and enable "Show results" to visualize invisible bodies.

## Hypothesis

The native `@updateSheetMetalGeometry` function:
1. Sees bodies in `sketchBodyIds` with `FORM_BODY_SKETCH_FOR_FLAT_VIEW` attribute
2. Attempts to process them as if they were wire bodies (standard form behavior)
3. Creates copies/transforms of the solid bodies in the flat context
4. Lacks visibility/selection logic for solid bodies (expects wires)
5. Leaves bodies in flat context but without proper display attributes

**This suggests**: The native engine has infrastructure for importing geometry into flat patterns, but only has complete logic for wire/sketch bodies. Solid bodies are being processed but not fully integrated.

## Success Metric

We have successfully proven that:
1. FeatureScript guards can be modified to allow non-standard body types
2. Native functions will accept and process these bodies
3. Geometry can be created inside the flat pattern context
4. The barrier is not at the `@` level for body creation, but for visibility/usability

**The pathway exists. Now we need to find the visibility/selection mechanism.**
