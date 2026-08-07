# Sheet Metal Surface Hiding Experiment

## Overview
This experiment explores the concept of using sheet metal annotations and `defineSheetMetalFeature` to create hidden surfaces that remain accessible through broader queries. The goal is to investigate whether this approach could be adapted for Frame-like attributes that need a hidden attribute-holding body.

## Background
- Sheet metal models in Onshape use a hidden "master body" (a surface body) that stores the definition of the sheet metal
- Features defined with `defineSheetMetalFeature` can modify this master body without exposing it to regular queries
- The master body and its entities are annotated with `SMAttribute` objects that store metadata
- These hidden surfaces can still be queried using specialized functions like `getSMDefinitionEntities` or by querying for specific attributes

## Experiment Components

### Feature 1: hideEdgeSurface.fs
Creates a surface along an edge of a solid and hides it using sheet metal annotations.

**Process:**
1. User selects an edge on a solid body
2. Feature creates a ruled surface (using `opLoft`) offset from the edge
3. Surface is annotated with a custom `SMAttribute` (specifically a WALL attribute)
4. Custom properties are stored in the attribute (custom text, offset distance, experiment type)
5. `updateSheetMetalGeometry` is called to finalize the sheet metal feature
6. The surface should become hidden from standard queries but remain in the context

**Expected Result:**
- Surface is created and annotated
- Surface becomes hidden from typical `qEverything()` queries that might not include sheet metal definition bodies
- Surface can still be queried through sheet metal-specific functions

### Feature 2: queryHiddenSurface.fs
Queries for surfaces hidden with sheet metal annotations and retrieves their stored properties.

**Query Methods Tested:**
1. **Method 1**: Query by `SMObjectType.WALL` using `getAttributes` with `attributePattern`
2. **Method 2**: Query using `qAttributeQuery` to find entities with WALL attributes
3. **Method 3**: Query all SHEET bodies directly with `qBodyType(qEverything(), BodyType.SHEET)`
4. **Method 4**: Use `getSMDefinitionEntities` with a search scope
5. **Method 5**: Broader query patterns to count total entities and faces
6. **Method 6**: Query using `qAttributeFilter` for unnamed attributes matching experiment type

**Expected Result:**
- Hidden surfaces should be found by some or all of these query methods
- Custom properties should be retrievable from the attributes
- Console log output should show which query methods successfully find the hidden surfaces

## Key Technical Details

### Sheet Metal Attributes
- Sheet metal uses **unnamed attributes** (legacy pattern) rather than named attributes
- Attributes are set using `setAttribute` without a `"name"` field
- These can be queried using `getAttributes` with `attributePattern` or `qAttributeFilter`
- Attributes are of type `SMAttribute` which includes fields like `objectType`, `attributeId`, and custom fields

### defineSheetMetalFeature
- Wraps `defineFeature` and sets `isSheetMetal = true` in the defaults
- This changes how the feature interacts with the context and query system
- Bodies created/modified in these features may be hidden from standard queries

### updateSheetMetalGeometry
- Called to finalize sheet metal changes
- Updates both the folded and flat representations
- May affect visibility of entities in queries

## Potential Applications

If this experiment is successful, the approach could be used for:

1. **Frame Features**: Store hidden metadata about frame definitions similar to how sheet metal stores bend/wall data
2. **Hidden Sketch Geometry**: Maintain construction geometry that shouldn't appear in normal queries but needs to persist
3. **Feature Metadata Storage**: Store feature-specific data on hidden entities for later retrieval
4. **Design Intent Tracking**: Maintain invisible reference geometry that captures original design decisions

## Testing Instructions

1. Create a simple solid body (e.g., a box or cylinder)
2. Run the **Hide Edge Surface** feature:
   - Select an edge on the solid
   - Set an offset distance (e.g., 10mm)
   - Enter a custom property value (e.g., "TestSurface1")
3. Run the **Query Hidden Surface** feature:
   - Optionally select the solid body as search scope
   - Set experiment type filter to "hiddenEdgeSurface"
   - Review console output to see which query methods find the surface

## Expected Console Output

The query feature should log:
- Number of WALL attributes found
- Details of each hidden surface found (custom property, offset distance, etc.)
- Results from each query method
- Total entity counts for comparison

## Known Limitations

1. This is an experimental approach and may not work as intended
2. Sheet metal system is designed for specific use cases and may not cooperate with non-sheet-metal applications
3. Hidden surfaces may still affect regeneration time or context size
4. The approach relies on undocumented behavior of `defineSheetMetalFeature`

## Future Explorations

If successful, further experiments could explore:
- Creating multiple hidden surfaces per solid
- Storing more complex data structures in attributes
- Querying hidden surfaces from other features or Part Studios
- Performance implications of many hidden surfaces
- Whether hidden surfaces survive operations like boolean unions/subtracts
- Integration with Frame features or other parametric systems

## References

- `sheetMetalAttribute.fs` - Attribute definitions and helper functions
- `sheetMetalUtils.fs` - `defineSheetMetalFeature` and `updateSheetMetalGeometry`
- `sheetMetalStart.fs` - Example of creating sheet metal from surfaces
- `attributes.fs` - General attribute system documentation
- `SHEET_METAL_GOTCHAS.md` - Known issues with sheet metal system
