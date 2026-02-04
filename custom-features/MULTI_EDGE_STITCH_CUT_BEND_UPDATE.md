# Multi-Edge Stitch Cut Bend Update

## Problem Statement

The sheet metal stitch cut bend custom feature worked well for single edges but required multiple feature calls to process multiple edges. This slowed down documents with many edges requiring stitch cut bends. The goal was to enable selecting and processing multiple edges at once with the same settings.

## Solution Overview

Updated the feature to support multiple edge selections while maintaining minimal changes to the existing codebase. Each selected edge is processed independently with unique IDs, and a single sheet metal update call is made at the end for improved performance.

## Key Changes

### 1. Removed Single-Edge Restriction

**Before:**
```featurescript
annotation { "Name" : "Joint edge",
            "Filter" : ... ,
            "MaxNumberOfPicks" : 1 }
definition.entity is Query;
```

**After:**
```featurescript
annotation { "Name" : "Joint edges",
            "Filter" : ... }
definition.entity is Query;
```

Removing `MaxNumberOfPicks : 1` allows users to select multiple edges in the UI.

### 2. Added Helper Function for Finding All Joint Entities

Created `findAllJointDefinitionEntities()` to extract all individual joint entities from the user selection:

```featurescript
function findAllJointDefinitionEntities(context is Context, entity is Query, entityType is EntityType) returns array
{
    const entityQ = qUnion(getSMDefinitionEntities(context, entity));
    const sheetEntities = qEntityFilter(entityQ, entityType);
    const evaluatedEntities = evaluateQuery(context, sheetEntities);
    
    var result = [];
    for (var singleEntity in evaluatedEntities)
    {
        result = append(result, qUnion([singleEntity]));
    }
    return result;
}
```

This function returns an array of Query objects, one for each selected joint entity.

### 3. Extracted Single-Edge Processing Logic

Created `processJointEntity()` function that encapsulates all the logic for processing a single joint entity:

```featurescript
function processJointEntity(context is Context, id is Id, jointEntity is Query, 
    definition is map, defaultRadius, defaultKFactor) returns Query
{
    // Get existing attribute
    // Validate and construct path
    // Calculate spacing and domains
    // Split edges
    // Remove and reassign attributes
    // Identify bridge and stitch segments
    // Apply joint attributes
    
    return allEdgesAfterSplitQuery;
}
```

This function:
- Takes a single joint entity as input
- Uses a unique sub-ID (`id + "entity" ~ entityIndex`)
- Returns the query of all processed edges
- Maintains the exact same processing logic as before

### 4. Refactored Main Feature Body

The main feature body now:
1. Validates the sheet metal context
2. Finds all joint entities (edges and faces)
3. Gets default radius and K-factor once (before processing)
4. Loops over each edge entity
5. Calls `processJointEntity()` for each edge with a unique sub-ID
6. Collects all processed edges
7. Makes a single `updateSheetMetalGeometry()` call at the end

```featurescript
// Get default values BEFORE any operations
var defaultRadius;
var defaultKFactor;
if (definition.useDefaultRadius)
    defaultRadius = getDefaultSheetMetalRadius(context, definition.entity);
if (definition.useDefaultKFactor)
    defaultKFactor = getDefaultSheetMetalKFactor(context, definition.entity);

// Process each joint entity independently
var allProcessedEdges = [];
var entityIndex = 0;
for (var jointEntity in jointEdgeEntities)
{
    const processedEdges = processJointEntity(context, id + ("entity" ~ entityIndex), 
        jointEntity, definition, defaultRadius, defaultKFactor);
    allProcessedEdges = append(allProcessedEdges, processedEdges);
    entityIndex += 1;
}

// Single update call at the end
if (size(allProcessedEdges) > 0)
{
    const allProcessedEdgesQuery = qUnion(allProcessedEdges);
    updateSheetMetalGeometry(context, id, { 
        "entities" : allProcessedEdgesQuery,
        "associatedChanges" : allProcessedEdgesQuery
    });
}
```

## Benefits

1. **Improved Performance**: Single sheet metal update call instead of multiple feature calls
2. **Better User Experience**: Select all edges once instead of one at a time
3. **Minimal Code Changes**: Extracted existing logic into a helper function
4. **Maintained Independence**: Each edge is processed with unique IDs, ensuring proper attribution
5. **Backward Compatible**: Works exactly the same for single edge selections

## Example Use Case

**Before (Cube with 6 Edges):**
- Create 6 separate stitch cut bend features
- Select one edge each time
- Configure parameters 6 times
- 6 sheet metal update calls

**After (Cube with 6 Edges):**
- Create 1 stitch cut bend feature
- Select all 6 edges at once
- Configure parameters once
- 1 sheet metal update call

## Technical Details

### Unique ID Strategy

Each joint entity gets a unique sub-ID:
- Main feature ID: `id`
- Entity 0: `id + "entity0"`
- Entity 1: `id + "entity1"`
- etc.

This ensures:
- Split operations have unique IDs
- Attributes have unique IDs
- No conflicts between processing different edges

### Error Handling

The feature maintains the same error handling as before:
- Validates sheet metal context
- Checks for face bends (not supported)
- Validates edge lengths
- Checks for sufficient space for bridges
- Validates domain overlap

### Attribution Pattern

The same attribution pattern is used for each edge:
1. Split edges at calculated domains
2. Remove shared association attributes
3. Remove shared definition attributes
4. Assign unique association attributes
5. Apply unique definition attributes (BEND vs RIP)

## Files Modified

1. **sheetMetalStitchCutBend.fs**
   - Removed `MaxNumberOfPicks : 1`
   - Added `findAllJointDefinitionEntities()` function
   - Added `processJointEntity()` function
   - Refactored main feature body to loop over entities

2. **SHEET_METAL_STITCH_CUT_BEND_README.md**
   - Updated usage documentation
   - Added multi-edge support section
   - Updated technical reference

## Testing Recommendations

1. **Single Edge**: Verify existing functionality still works
2. **Multiple Edges**: Test with 2, 3, 6+ edges
3. **Mixed Geometries**: Test with different edge lengths and angles
4. **Error Cases**: Test with invalid selections (face bends, non-joint edges)
5. **Performance**: Compare document regeneration time with multiple edges

## Conclusion

The update successfully enables multi-edge selection for the stitch cut bend feature while maintaining minimal code changes and preserving the existing attribution pattern. The refactoring improves both performance and user experience without breaking backward compatibility.
