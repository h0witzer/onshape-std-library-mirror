# Final Summary: Sheet Metal Stitch Cut Bend Fix

## Problem Solved
The sheet metal stitch cut bend custom feature now correctly applies attributes to split edge domains, creating alternating bend/rip segments that behave as independent joints.

## Key Discoveries

### 1. Why `assignSMAttributesToNewOrSplitEntities` Doesn't Work
**Designed for:** Propagating EXISTING attributes to NEW entities
**Our use case:** MODIFYING attributes on split entities
**Result:** Returns empty `modifiedEntities` because split edges already have inherited attributes

### 2. Both Attribute Types Must Be Unique
Split edges inherit BOTH attributes from parent:
- **Association attribute** (tracking/disambiguation)
- **Definition attribute** (joint properties)

If either is shared:
- Shared association → "Failed to disambiguate" error
- Shared definition → Segments behave as single joint

### 3. All Properties Must Be Copied
**BEND requires:** radius, angle, kFactor
**RIP requires:** jointStyle, angle, minimalClearance

Missing properties cause "N/A" values and incorrect behavior.

### 4. Angle Must Be Computed Per Segment
**Wrong:** Copy original angle (covers entire original edge)
**Right:** Compute angle using `bendAngle()` for each segment's geometry

## Complete Solution Pattern

```featurescript
// 1. Get defaults BEFORE operations (while original edge valid)
var defaultRadius, defaultKFactor;
if (definition.useDefaultRadius) {
    defaultRadius = getDefaultSheetMetalRadius(context, definition.entity);
}
if (definition.useDefaultKFactor) {
    defaultKFactor = getDefaultSheetMetalKFactor(context, definition.entity);
}

// 2. Split edges with tracking
const trackedEdges = qUnion([edgeQuery, startTracking(context, edgeQuery)]);
opSplitEdges(context, id + "split", {...});
const splitEdgesQuery = qEntityFilter(trackedEdges, EntityType.EDGE);

// 3. Remove shared association attributes
removeAttributes(context, {
    "entities" : splitEdgesQuery,
    "attributePattern" : {} as SMAssociationAttribute
});

// 4. Remove shared definition attributes
removeAttributes(context, {
    "entities" : splitEdgesQuery,
    "attributePattern" : {} as SMAttribute
});

// 5. Assign unique association attributes
assignSMAssociationAttributes(context, splitEdgesQuery);

// 6. Create unique definition attributes
for (var i = 0; i < size(splitEdges); i += 1) {
    const segmentEdge = splitEdges[i];
    
    if (isBend) {
        var attr = makeSMJointAttribute(toAttributeId(id + ("bend" ~ i)));
        attr.jointType = {value: SMJointType.BEND};
        attr.radius = radius;
        
        // CRITICAL: Compute angle per segment
        const computedAngle = try silent(bendAngle(context, id + ("angle" ~ i), segmentEdge, radius));
        if (computedAngle != undefined && abs(computedAngle) >= TOLERANCE.zeroAngle * radian) {
            attr.angle = { "value" : computedAngle, "canBeEdited" : false };
        }
        
        attr.kFactor = kFactor;
        setAttribute(context, {"entities": segmentEdge, "attribute": attr});
    }
    else if (isRip) {
        var attr = makeSMJointAttribute(toAttributeId(id + ("rip" ~ i)));
        attr.jointType = {value: SMJointType.RIP};
        attr.jointStyle = {value: SMJointStyle.EDGE};
        attr.angle = existingAttribute.angle;  // Can copy for rips
        attr.minimalClearance = minimalClearance;
        setAttribute(context, {"entities": segmentEdge, "attribute": attr});
    }
}

// 7. Update geometry
updateSheetMetalGeometry(context, id, { 
    "entities" : splitEdgesQuery,
    "associatedChanges" : splitEdgesQuery
});
```

## Validation

### Tester (`smSplitEdgeTester.fs`)
- ✅ Splits BEND into 2 BENDs with unique attributes
- ✅ Splits RIP into 2 RIPs with unique attributes
- ✅ Each segment independently modifiable
- ✅ All properties correctly set

### Production Feature (`sheetMetalStitchCutBend.fs`)
- ✅ Splits edge into multiple segments
- ✅ Alternating BEND/RIP pattern
- ✅ Each segment has unique association
- ✅ Each segment has unique definition
- ✅ Bend tables show correct angles
- ✅ Modify Joint works on individual segments
- ✅ No spurious geometry

## Common Pitfalls Avoided

1. **Don't use `replaceSMAttribute`** after removing attributes - use `setAttribute`
2. **Don't call `getDefault...()` functions** after removing attributes
3. **Don't copy angle** for BEND segments - compute it
4. **Don't forget jointStyle** for RIP segments
5. **Don't pass empty queries** to `updateSheetMetalGeometry`

## Files

**Production:**
- `custom-features/sheetMetalStitchCutBend.fs` - Complete working feature

**Validation:**
- `custom-features/smSplitEdgeTester.fs` - Minimal test case

**Documentation:**
- `SOLUTION.md` - Complete solution walkthrough
- `CRITICAL_INSIGHT.md` - Why both attributes must be unique
- `ANALYSIS_assignSMAttributesToNewOrSplitEntities.md` - Function deep dive
- `FINDINGS.md` - Investigation notes
- `FINAL_SUMMARY.md` - This document

## Status: COMPLETE ✅

The feature is now fully functional with proper attribute tracking for split sheet metal edges!
