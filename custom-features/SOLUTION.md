# Sheet Metal Stitch Cut Bend - Complete Solution

## Problem Summary
The stitch cut bend feature splits a single sheet metal joint edge into alternating bend and rip segments. The original implementation wasn't generating proper geometry because split edges inherited shared attributes from the parent edge.

## Root Causes Discovered

### Issue 1: Shared Association Attributes
When `opSplitEdges` splits an edge, all resulting segments inherit the **same** association attribute (SMAssociationAttribute) from the parent. This causes:
- "Failed to completely disambiguate created topology" error
- Sheet metal system cannot distinguish between multiple entities with identical association IDs

### Issue 2: Shared Definition Attributes  
Split segments also inherit the **same** definition attribute (SMAttribute with joint properties) from the parent. This causes:
- Segments behave as a single logical joint
- Modify Joint affects all segments simultaneously
- Spurious geometry (bend lines at origin in flat pattern)

### Issue 3: Incomplete Property Copying
When creating new attributes, **all** properties must be copied:
- **BEND**: radius, angle, kFactor
- **RIP**: jointStyle, angle, minimalClearance

Missing properties (like RIP's `jointStyle`) cause "N/A" values and incorrect behavior.

## Solution Pattern (Validated by Tester)

### Step 1: Split Edges
```featurescript
// Track edges BEFORE splitting
const trackedEdges = qUnion([orderedEdgeQuery, startTracking(context, orderedEdgeQuery)]);

// Perform split operations
opSplitEdges(context, id + "split", {
    "edges" : edgeQuery,
    "parameters" : [splitParams]  // Array of arrays
});

// Get split edges via tracking
const allEdgesAfterSplit = qEntityFilter(qUnion([orderedEdgeQuery, trackedEdges]), EntityType.EDGE);
const allEdgesAfterSplitEval = evaluateQuery(context, allEdgesAfterSplit);
const allEdgesAfterSplitQuery = qUnion(allEdgesAfterSplitEval);
```

### Step 2: Remove Shared Association Attributes
```featurescript
removeAttributes(context, {
    "entities" : allEdgesAfterSplitQuery,
    "attributePattern" : {} as SMAssociationAttribute
});
```

### Step 3: Remove Shared Definition Attributes
```featurescript
removeAttributes(context, {
    "entities" : allEdgesAfterSplitQuery,
    "attributePattern" : {} as SMAttribute
});
```

### Step 4: Assign Unique Association Attributes
```featurescript
assignSMAssociationAttributes(context, allEdgesAfterSplitQuery);
```

### Step 5: Create Unique Definition Attributes
For each segment, create a new attribute with a unique ID:

**For BEND segments:**
```featurescript
var newBendAttr = makeSMJointAttribute(toAttributeId(id + ("bend" ~ i)));
newBendAttr.jointType = {value: SMJointType.BEND};
newBendAttr.radius = radius;
newBendAttr.angle = existingAttribute.angle;  // Copy from original
newBendAttr.kFactor = kFactor;

setAttribute(context, {
    "entities" : segmentEdge,
    "attribute" : newBendAttr
});
```

**For RIP segments:**
```featurescript
var newRipAttr = makeSMJointAttribute(toAttributeId(id + ("rip" ~ i)));
newRipAttr.jointType = {value: SMJointType.RIP};
newRipAttr.jointStyle = {value: SMJointStyle.EDGE};
newRipAttr.angle = existingAttribute.angle;  // Copy from original
newRipAttr.minimalClearance = existingAttribute.minimalClearance;  // Copy if exists

setAttribute(context, {
    "entities" : segmentEdge,
    "attribute" : newRipAttr
});
```

### Step 6: Update Sheet Metal Geometry
```featurescript
updateSheetMetalGeometry(context, id, { 
    "entities" : allEdgesAfterSplitQuery,
    "associatedChanges" : allEdgesAfterSplitQuery
});
```

## Key Insights

### 1. Why assignSMAttributesToNewOrSplitEntities Doesn't Work
This function is designed for **entity creation** scenarios where:
- NEW entities are created (via opSplitFace, opExtrude, etc.)
- Those new entities are then attributed
- Function identifies them as "new" and handles appropriately

In our case:
- Split edges **inherit** all attributes from parent
- Function sees them as "existing" (via tracking resolution)
- Returns empty `modifiedEntities` query
- Not designed for **attribute modification** after splitting

### 2. Both Attribute Types Must Be Unique

| Attribute Type | Purpose | If Shared → Problem |
|----------------|---------|---------------------|
| **Association** (SMAssociationAttribute) | Entity tracking, links to model | Disambiguation error |
| **Definition** (SMAttribute) | Joint properties (type, radius, angle, etc.) | Segments act as one joint |

### 3. All Properties Must Be Copied

| Joint Type | Required Properties |
|------------|-------------------|
| **BEND** | `radius`, `angle`, `kFactor` |
| **RIP** | `jointStyle`, `angle`, `minimalClearance` |

Note: Both have `angle` property!

### 4. Use setAttribute, Not replaceSMAttribute
- `replaceSMAttribute` requires an existing attribute to replace
- After we remove all attributes, segments have none
- Use `setAttribute` to assign new attributes directly

## Testing Validation

### smSplitEdgeTester.fs
Minimal reproduction that validates the complete pattern:
- ✅ Splits 1 BEND → 2 independent BENDs
- ✅ Splits 1 RIP → 2 independent RIPs
- ✅ Each segment has unique association attribute
- ✅ Each segment has unique definition attribute with all properties
- ✅ Modify Joint works independently on each segment
- ✅ No disambiguation errors
- ✅ No spurious geometry

### sheetMetalStitchCutBend.fs
Production feature applying the validated pattern:
- Splits edge into multiple segments
- Classifies segments as bridges (BEND) or stitches (RIP)
- Assigns unique attributes to each with alternating types
- Generates proper sheet metal geometry

## Files Changed

1. **smSplitEdgeTester.fs** - Minimal test validating the attribution pattern
2. **sheetMetalStitchCutBend.fs** - Production feature with validated pattern applied
3. **CRITICAL_INSIGHT.md** - Documentation of why both attributes must be unique
4. **ANALYSIS_assignSMAttributesToNewOrSplitEntities.md** - Deep dive into standard library function
5. **FINDINGS.md** - Standard library usage patterns
6. **SOLUTION.md** (this file) - Complete solution summary

## Conclusion

The sheet metal edge split attribution problem required understanding three key issues:
1. Split edges inherit shared association attributes
2. Split edges inherit shared definition attributes  
3. All attribute properties must be copied

The solution is straightforward once understood: remove both shared attributes, assign unique versions, copy all properties. This pattern is now validated and documented for future reference.
