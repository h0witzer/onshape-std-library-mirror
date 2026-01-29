# Stitch Cut Bend - updateSheetMetalGeometry Fix

## Problem

After fixing the attribute assignment to use individual `setAttribute` calls instead of `replaceSMAttribute`, the feature failed with:

```
@updateSheetMetalGeometry: SHEET_METAL_REBUILD_ERROR
```

The geometry wasn't building at all.

## Root Cause

We were passing a union of individual split edge segment queries to `updateSheetMetalGeometry`:

```featurescript
// WRONG: Passing split segment queries
const allModifiedEdges = qUnion([bridgeSegmentEdges, stitchSegmentEdges]);
updateSheetMetalGeometry(context, id, { 
    "entities" : allModifiedEdges,
    "associatedChanges" : allModifiedEdges
});
```

### Why This Failed

After edge splitting:
1. `bridgeSegmentEdges` and `stitchSegmentEdges` are queries for individual split segments
2. These segments were created by `opSplitEdges` and tracked via `startTracking`
3. We then set attributes on each segment individually
4. But the sheet metal system expects to receive the **definition entity** that was modified

The queries of split segments don't properly represent the master definition entity structure. The sheet metal rebuild system needs to know which definition entity to rebuild, not individual segment queries.

## Solution

Pass the original `jointEntity` (the definition entity) instead:

```featurescript
// CORRECT: Passing definition entity
updateSheetMetalGeometry(context, id, { 
    "entities" : jointEntity,
    "associatedChanges" : jointEntity
});
```

### Why This Works

1. **Definition Entity Hierarchy**: `jointEntity` is the master definition entity
2. **Segment Ownership**: After splitting, all segments remain part of the original definition entity structure
3. **Attribute Tracking**: When we set attributes on segments, those changes are tracked within the definition entity
4. **Rebuild Scope**: Passing `jointEntity` tells the sheet metal system to rebuild everything associated with that joint
5. **Pattern Match**: This matches Modify Joint's pattern of passing the definition entity query

## Comparison with Modify Joint

### Modify Joint Pattern
```featurescript
// Find definition entity
var jointEntity = findJointDefinitionEntity(context, definition.entity, EntityType.EDGE);

// Get existing attribute
var existingAttribute = getJointAttribute(context, jointEntity);

// Create new attribute and replace (returns definition entity query)
var jointEdgesQ = replaceSMAttribute(context, existingAttribute, newAttribute);

// Update with definition entity
updateSheetMetalGeometry(context, id, { 
    "entities" : jointEdgesQ,
    "associatedChanges" : jointEdgesQ 
});
```

### Our Feature Pattern
```featurescript
// Find definition entity
var jointEntity = findJointDefinitionEntity(context, definition.entity, EntityType.EDGE);

// Split edges within definition entity
opSplitEdges(context, id, { "edges" : instruction.edge, ... });

// Identify and categorize segments
const bridgeSegmentEdges = identifySegmentsByEdgeMidpoints(...);
const stitchSegmentEdges = qSubtraction(allEdgesAfterSplit, bridgeSegmentEdges);

// Set attributes on individual segments (all part of jointEntity)
applyJointAttributesToSegments(context, id, bridgeSegmentEdges, ...);
applyJointAttributesToSegments(context, id, stitchSegmentEdges, ...);

// Update with parent definition entity (contains all modified segments)
updateSheetMetalGeometry(context, id, { 
    "entities" : jointEntity,
    "associatedChanges" : jointEntity 
});
```

## Key Insight

The sheet metal system works with **definition entities**, not individual part geometry queries. Even after splitting and modifying individual segments:

- The segments are part of the definition entity structure
- Attribute changes on segments are tracked within that structure
- The rebuild system needs the definition entity to know what to rebuild
- Passing segment queries bypasses this structure and causes rebuild failures

## User's Hint

The user mentioned: "Sheet Metal Tab and slot has a fix for this in just re-evaluating the path and figuring the domains after the splitting operation."

While Tab and Slot does re-evaluate paths, it doesn't directly call `updateSheetMetalGeometry` - it does boolean operations instead. The real insight was that we need to work with the proper entity hierarchy, not just the split segment queries.

## Testing

To verify the fix:
1. Select a sheet metal edge joint
2. Apply stitch cut bend feature with any spacing configuration
3. Feature should successfully build geometry
4. Edges should be split into alternating BEND and RIP segments
5. Folded and flat patterns should both be valid
6. No `SHEET_METAL_REBUILD_ERROR` should occur

## Result

The feature now successfully:
- ✅ Splits edges at calculated positions
- ✅ Assigns BEND attributes to bridges
- ✅ Assigns RIP attributes to stitches
- ✅ Rebuilds sheet metal geometry without errors
- ✅ Generates valid folded and flat patterns
