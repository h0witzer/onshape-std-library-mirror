# Key Findings: assignSMAttributesToNewOrSplitEntities Usage Pattern

## Summary

The `assignSMAttributesToNewOrSplitEntities` function is **NOT** designed for the stitch cut bend use case. It's designed for a different scenario.

## What the Tester Revealed

Running `smSplitEdgeTester.fs` showed:

```
=== BEFORE assignSMAttributesToNewOrSplitEntities ===
Segment 0: Association attrs: 1, Definition attr type: BEND
Segment 1: Association attrs: 1, Definition attr type: BEND

=== AFTER assignSMAttributesToNewOrSplitEntities ===
Segment 0: Association attrs: 1, Definition attr type: BEND (UNCHANGED)
Segment 1: Association attrs: 1, Definition attr type: BEND (UNCHANGED)

toUpdate.modifiedEntities: EMPTY (0 entities)
Deleted attributes count: 33
```

**Key insight:** When edges are split, they INHERIT all attributes from the parent edge. Both split segments have the same association and definition attributes. The function sees "nothing new" and returns an empty query.

## How The Function Is Actually Used in Standard Library

### Example 1: sheetMetalRip.fs (lines 145-158)

```featurescript
// 1. Create NEW entities
opSplitFace(context, id + "split", ...);
var newEdges = evaluateQuery(context, qCreatedBy(id + "split", EntityType.EDGE));

// 2. Manually attribute the NEW entities
for (var e in newEdges) {
    setAttribute(context, {
        "entities" : e,
        "attribute" : createRipAttribute(...)  // NEW attribute type
    });
}

// 3. Call function to handle split/association tracking
const toUpdate = assignSMAttributesToNewOrSplitEntities(context, modelQ, initialData, id);

// 4. Use toUpdate.modifiedEntities
updateSheetMetalGeometry(context, id, { 
    "entities" : toUpdate.modifiedEntities,
    "deletedAttributes" : toUpdate.deletedAttributes 
});
```

### Example 2: sheetMetalBend.fs (lines 122-136)

```featurescript
// 1. Capture initial state
const initialData = getInitialEntitiesAndAttributes(context, modelBodyQ);

// 2. Create NEW geometry with attributes
annotateBendSurface(context, id, wrappedSheetQ, ...);  // Adds attributes to NEW surface

// 3. Call function
var toUpdate = assignSMAttributesToNewOrSplitEntities(context, surfacePieces.fixedSurface, initialData, id);

// 4. Use toUpdate.modifiedEntities
updateSheetMetalGeometry(context, id, { 
    "entities" : toUpdate.modifiedEntities,
    "deletedAttributes" : toUpdate.deletedAttributes 
});
```

## The Pattern

The function is designed for scenarios where:

1. **New entities are CREATED** (not just split)
2. **New attributes are APPLIED** to those new entities
3. Function identifies which entities are "new" (weren't in initialData)
4. Function handles association attribute tracking for those new entities
5. Returns `modifiedEntities` containing the new/attributed entities

## Why It Doesn't Work for Stitch Cut Bend

In stitch cut bend:

1. We **split existing edges** (they inherit parent attributes)
2. Split edges already have association + definition attributes
3. Function sees "nothing new" → returns EMPTY
4. We then try to **CHANGE** attributes AFTER calling the function
5. But `updateSheetMetalGeometry` already received empty entity list

## The Solution for Stitch Cut Bend

**Don't rely on `toUpdate.modifiedEntities`** - it will be empty. Instead:

```featurescript
// 1. Capture initial state
const initialData = getInitialEntitiesAndAttributes(context, modelBodyQuery);

// 2. Split edges
opSplitEdges(context, id + "split", ...);

// 3. Identify and classify split edges
const bridgeSegments = ...;
const stitchSegments = ...;

// 4. Apply new attributes to segments
applyJointAttributesToSegments(..., bridgeSegments, ..., SMJointType.BEND);
applyJointAttributesToSegments(..., stitchSegments, ..., SMJointType.RIP);

// 5. Call function for deleted attributes tracking (not for entity list)
const toUpdate = assignSMAttributesToNewOrSplitEntities(context, modelBodyQuery, initialData, id);

// 6. Pass ACTUAL modified edges, not toUpdate.modifiedEntities
const allModifiedEdges = qUnion([bridgeSegments, stitchSegments]);
const splitEdgesEvaluated = evaluateQuery(context, allModifiedEdges);
const splitEdgesFromModelBody = qIntersection([
    qOwnedByBody(modelBodyQuery),
    qUnion(splitEdgesEvaluated)
]);

updateSheetMetalGeometry(context, id, { 
    "entities" : splitEdgesFromModelBody,  // Actual edges, not toUpdate.modifiedEntities
    "deletedAttributes" : toUpdate.deletedAttributes,
    "associatedChanges" : splitEdgesFromModelBody
});
```

## Conclusion

The function `assignSMAttributesToNewOrSplitEntities` is useful for tracking deleted attributes, but for stitch cut bend, we must pass the actual modified edges directly to `updateSheetMetalGeometry`, not rely on the function's `modifiedEntities` output.
