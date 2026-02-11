# Quick Start Guide

## Files in this Directory

- **mihcPlacement.fs** - MIHC algorithm (optimal for finding widest area)
- **earClippingPlacement.fs** - Ear clipping algorithm (fast guaranteed interior point)  
- **labelPlacementUtils.fs** - Shared utility functions
- **README.md** - Comprehensive documentation
- **Planar Face Label Placement Strategies.md** - Research document with algorithm descriptions

## How to Use in Onshape

Since these are FeatureScript files, they need to be imported into Onshape:

1. **Upload labelPlacementUtils.fs** to Onshape as a FeatureScript document
   - This creates a document with an ID like `1470642f04a4ab4b999322bb`
   - Note the document ID and version for imports
2. **Create the feature documents** (mihcPlacement.fs or earClippingPlacement.fs)
3. **Update the import path** in each feature to point to your uploaded utils document:
   ```featurescript
   import(path : "YOUR_UTILS_DOC_ID", version : "YOUR_VERSION");
   ```
4. **Save and test** on a part with planar faces

**Note:** The import paths in the repository files reference a specific Onshape document ID. You'll need to update these to match your own uploaded utils document.

## Testing Scenarios

To compare the two strategies, try these test cases:

### Good Test Faces
- **Horseshoe/U-shape** - Classic concave case where centroid fails
- **Slotted plate** - Face with rectangular cutout
- **C-channel cross-section** - Another concave shape
- **Rectangle with circular hole** - Tests hole handling
- **Irregular polygon** - Tests general robustness

### Expected Behaviors

**MIHC** (mihcPlacement.fs):
- Should place connector in the widest part of the face
- With 3 scanlines: more likely to find optimal position
- X-axis of mate connector aligns with the detected chord

**Ear Clipping** (earClippingPlacement.fs):
- Should place connector at first valid ear triangle's incenter
- Position may not be "optimal" but is guaranteed inside
- Faster execution, less concerned with "widest" area

## Comparing Results

1. Create a test part with various planar faces
2. Run **MIHC with 1 scanline** on a face → note connector position
3. Run **MIHC with 3 scanlines** on same face → compare position
4. Run **Ear Clipping** on same face → compare with MIHC
5. Visually assess which placement looks better for label positioning

## Notes

- Both features **only work on planar faces** (filtered in precondition)
- The mate connectors are placed for **visualization only** - no actual labels are created
- For production label placement, you'd extend these to:
  - Add text/sketch elements
  - Scale labels to fit the detected chord length
  - Handle label conflicts/overlaps

## Troubleshooting

**"Select a planar face" error**: Make sure you're selecting a flat face, not a curved surface

**No connector appears**: Check if the face is truly planar and has at least 3 vertices

**Connector in odd location**: 
- For MIHC: Try increasing scanline count to 3
- For Ear Clipping: This finds the first ear, which may not be optimal - use MIHC instead

## Further Development

See README.md section "Future Enhancements" for ideas like:
- Winding number validation
- Label size awareness  
- Multi-loop handling
- Performance caching
- Visual debugging
