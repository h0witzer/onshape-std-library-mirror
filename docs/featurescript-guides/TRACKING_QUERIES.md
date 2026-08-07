# Tracking Queries in FeatureScript

## Overview

Tracking queries are a critical pattern in FeatureScript for maintaining references to geometry through operations that modify, split, or transform entities. This document explains when and how to use tracking queries, particularly in cases where `qCreatedBy` is insufficient.

## The Problem

When operations like `opSplitEdges`, `opBoolean`, or `opTransform` modify geometry:
- Original entities may be replaced with new ones
- Some entities may be affected while others remain unchanged
- Standard queries like `qCreatedBy` may not capture all relevant geometry
- Simple queries can "lose" edges that weren't directly created by an operation

## The Solution: mixInTracking Pattern

### Basic Pattern

```featurescript
// BEFORE the operation
const trackedQuery = qUnion([originalQuery, startTracking(context, originalQuery)]);

// Perform the operation (e.g., opSplitEdges, opBoolean)
opSomeOperation(context, id, {...});

// AFTER the operation - combine original seed with tracked results
const allEntitiesAfterOp = qUnion([originalQuery, trackedQuery]);
```

### Why This Works

The `mixInTracking` pattern ensures complete coverage because:

1. **startTracking** monitors the input query and tracks all descendants (split edges, transformed entities, etc.)
2. **Original query** captures any entities that were NOT affected by the operation
3. **Union of both** ensures nothing is missed, whether affected or unaffected

## Common Use Cases

### 1. Edge Splitting Operations

**Problem:** After splitting edges at specific points, you need to identify ALL resulting edges (both split and unsplit).

**Solution:**
```featurescript
// Before splitting
const trackedEdges = qUnion([orderedEdgeQuery, startTracking(context, orderedEdgeQuery)]);

// Perform split operations
for (var instruction in splitInstructions)
{
    @opSplitEdges(context, id + ("split" ~ toString(i)), {
        "edges" : instruction.edge,
        "parameters" : [instruction.parameters]
    });
}

// After splitting - get ALL edges
const allEdgesAfterSplit = qEntityFilter(qUnion([orderedEdgeQuery, trackedEdges]), EntityType.EDGE);
```

**Why it's needed:** 
- Some edges in the chain may be split, others may not
- `qSplitBy` alone misses unsplit edges
- Without the original query union, you lose unsplit edges
- Without tracking, you lose split edges

### 2. Boolean Operations

**Problem:** Boolean operations affect some entities and leave others unchanged.

**Solution:**
```featurescript
const trackedBodies = qUnion([targetBodies, startTracking(context, targetBodies)]);

opBoolean(context, id, {
    "tools" : tools,
    "targets" : targetBodies,
    "operationType" : BooleanOperationType.SUBTRACTION
});

// Get all resulting bodies
const allBodiesAfter = qUnion([targetBodies, trackedBodies]);
```

### 3. Transform Operations

**Problem:** Need to track sheet metal entities through transform operations.

**Solution:**
```featurescript
const tracking = startTracking(context, smEntitiesToTrack);

opTransform(context, id + "transform", {
    "bodies" : bodiesToTransform,
    "transform" : transformMatrix
});

// Tracked entities now resolve to transformed versions
```

## Helper Function

Consider creating a helper function for consistency:

```featurescript
/**
 * Creates a tracking query that captures both original and modified entities.
 * Use this before operations that may affect only some entities in the query.
 */
function mixInTracking(context is Context, query is Query) returns Query
{
    return qUnion([query, startTracking(context, query)]);
}
```

## Debugging Tracking Queries

When debugging tracking issues:

### 1. Visualize Original vs Tracked

```featurescript
// Before operation - show what we're tracking
debug(context, originalQuery, DebugColor.CYAN);

// After operation - show what we captured
debug(context, trackedQuery, DebugColor.RED);

// Final result - show what we're using
debug(context, qUnion([originalQuery, trackedQuery]), DebugColor.YELLOW);
```

### 2. Count Entities

```featurescript
println("Original entities: " ~ toString(size(evaluateQuery(context, originalQuery))));
println("After operation: " ~ toString(size(evaluateQuery(context, trackedQuery))));
println("Combined total: " ~ toString(size(evaluateQuery(context, qUnion([originalQuery, trackedQuery])))));
```

### 3. Verify Completeness

For operations that shouldn't change total length/count:

```featurescript
const originalLength = evLength(context, {"entities" : originalQuery});
const reconstructedLength = evLength(context, {"entities" : finalQuery});
println("Original length: " ~ toString(originalLength));
println("Reconstructed length: " ~ toString(reconstructedLength));
// These should match for split operations
```

## When NOT to Use Tracking

You don't need tracking when:
- Using `qCreatedBy` to get entities created by a specific operation
- Querying entities that definitely weren't modified (e.g., selecting from a different part)
- The operation creates entirely new geometry rather than modifying existing
- **You already have direct access to the geometry you need** - tracking is only for finding geometry *resultant from* an operation, not for accessing geometry you already have a query for

### Important: Direct Access vs. Tracking

**❌ Wrong: Unnecessary tracking**
```featurescript
const extractedEdgesToExtend = qEdgeTopologyFilter(edges, EdgeTopology.ONE_SIDED);

// UNNECESSARY: We already have direct access to these edges
const tracked = startTracking(context, extractedEdgesToExtend);

opExtendSheetBody(context, id, {
    "entities" : extractedEdgesToExtend,
    ...
});

// WRONG: Using tracked query when we can use the original directly
const movedEdges = qEntityFilter(tracked, EntityType.EDGE);
```

**✅ Correct: Use direct access when available**
```featurescript
const extractedEdgesToExtend = qEdgeTopologyFilter(edges, EdgeTopology.ONE_SIDED);

opExtendSheetBody(context, id, {
    "entities" : extractedEdgesToExtend,
    ...
});

// CORRECT: The original query still references these edges after extension
const extendedEdges = extractedEdgesToExtend;
```

**Key Insight:** Operations like `opExtendSheetBody` modify edges *in place*. The original query `extractedEdgesToExtend` still references those same edges after they've been extended. Tracking is only needed when you need to find *new* geometry created by the operation (e.g., with `qCreatedBy`) or when some entities might be split/modified while others remain unchanged.

### Real-World Example: Corner Vertex Identification

From `sheetMetalTabAndSlot`, identifying vertices at tab tips where extended edges meet created edges:

```featurescript
// Edges that will be extended (we have direct access to these)
const extractedEdgesToExtend = qEdgeTopologyFilter(edges, EdgeTopology.ONE_SIDED);

// Perform extension
opExtendSheetBody(context, id + "extendTabs", {
    "entities" : extractedEdgesToExtend,
    "extendDistance" : tabDepth,
    ...
});

// Get edges created by the extension operation (tracking not needed - use qCreatedBy)
const extensionCreatedEdges = qCreatedBy(id + "extendTabs", EntityType.EDGE);

// Find vertices common to both edge sets (no tracking needed for either)
const verticesFromExtendedEdges = qAdjacent(extractedEdgesToExtend, AdjacencyType.VERTEX, EntityType.VERTEX);
const verticesFromCreatedEdges = qAdjacent(extensionCreatedEdges, AdjacencyType.VERTEX, EntityType.VERTEX);
const cornerVertices = qIntersection([verticesFromExtendedEdges, verticesFromCreatedEdges]);
```

**Why no tracking is needed:**
- `extractedEdgesToExtend` still references the extended edges directly (they moved in place)
- `extensionCreatedEdges` uses `qCreatedBy` to get new edges (standard query, not tracking)
- Both queries give us exactly what we need without additional tracking overhead

## Common Pitfalls

### ❌ Wrong: Using tracking result alone
```featurescript
const tracked = startTracking(context, edges);
opSplitEdges(context, id, {...});
const result = tracked; // MISSING: unsplit edges!
```

### ✅ Correct: Union original with tracked
```featurescript
const tracked = startTracking(context, edges);
opSplitEdges(context, id, {...});
const result = qUnion([edges, tracked]); // Gets both split and unsplit
```

### ❌ Wrong: Tracking after operation
```featurescript
opSplitEdges(context, id, {...});
const tracked = startTracking(context, edges); // TOO LATE!
```

### ✅ Correct: Track before operation
```featurescript
const tracked = startTracking(context, edges); // BEFORE operation
opSplitEdges(context, id, {...});
```

## Real-World Example: Tab Segment Identification

This example from `sheetMetalTabAndSlot` demonstrates the complete pattern:

```featurescript
// 1. Setup tracking BEFORE splitting
const trackedEdges = qUnion([orderedEdgeQuery, startTracking(context, orderedEdgeQuery)]);

// 2. Perform split operations
for (var instruction in splitInstructions)
{
    @opSplitEdges(context, splitOperationId + ("split" ~ toString(i)), {
        "edges" : instruction.edge,
        "parameters" : [instruction.parameters]
    });
}

// 3. Get ALL edges (split and unsplit) AFTER operations
const allEdgesAfterSplit = qEntityFilter(
    qUnion([orderedEdgeQuery, trackedEdges]), 
    EntityType.EDGE
);

// 4. Use the complete edge set for further processing
const orderedPath = constructPath(context, allEdgesAfterSplit);
```

**Result:** Successfully captures all edges regardless of whether they were split, enabling accurate path reconstruction and domain checking.

## References

- Forum post explaining mixInTracking pattern
- Examples in: `leftyFlip.fs`, `skewTransform.fs`, `fillPattern.fs`, `onlyTabs-refactor/smTabModified.fs`
- FeatureScript documentation: [startTracking](https://cad.onshape.com/FsDoc/)

## Summary

**Key Takeaway:** When performing operations that modify geometry (split, boolean, transform), always use the mixInTracking pattern:

1. Create tracking query BEFORE operation: `qUnion([original, startTracking(context, original)])`
2. Perform the operation
3. Combine original with tracked AFTER operation: `qUnion([original, tracked])`
4. Filter to desired entity type if needed

This ensures you capture both affected (modified) and unaffected (original) entities, providing complete coverage for downstream operations.
