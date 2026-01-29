# Stitch Cut Bend - Attribute Assignment Fix

## Problem

User reported that edges were being split correctly at bridge domain boundaries, but:
- ✅ Stitch segments (gaps) correctly became RIPs
- ❌ Bridge segments also stayed as RIPs instead of becoming BENDs

The geometry was splitting correctly, but attributes weren't being assigned properly.

## Root Cause

The bug was in the `applyJointAttributesToSegments()` function, specifically at the end of the loop where attributes were being replaced:

```featurescript
// OLD CODE (BUGGY)
replaceSMAttribute(context, edgeAttribute, newAttribute);
```

### Why This Failed

1. **Edge Splitting Inheritance**: When `opSplitEdges` splits an edge, the child segments inherit the parent edge's attribute
2. **Shared Attributes**: Multiple split segments share the SAME attribute instance
3. **replaceSMAttribute Behavior**: This function finds ALL entities with the given attribute and replaces them all at once
4. **The Bug**: When processing bridge segments, calling `replaceSMAttribute` would find and replace the attribute on ALL segments that shared that attribute - including stitch segments that should stay as RIPs

### Example of the Bug

```
Original edge: BEND attribute (instance #123)
After split:   |segment1|segment2|segment3|
               All inherit BEND attribute #123

Loop iteration 1 (segment1 = bridge, should be BEND):
  - Create new BEND attribute
  - replaceSMAttribute finds all entities with attribute #123
  - Replaces #123 with BEND on ALL three segments ✓

Loop iteration 2 (segment2 = stitch, should be RIP):
  - Create new RIP attribute
  - replaceSMAttribute finds all entities with BEND attribute
  - Replaces BEND with RIP on ALL three segments ✗ (overwrites segment1)

Loop iteration 3 (segment3 = bridge, should be BEND):
  - Create new BEND attribute  
  - replaceSMAttribute finds all entities with RIP attribute
  - Replaces RIP with BEND on ALL three segments ✗ (overwrites segment2)

Result: Last attribute applied wins, all segments get the same type
```

## Solution

Changed to use `removeAttributes` + `setAttribute` directly on each specific edge:

```featurescript
// NEW CODE (FIXED)
// Remove old attribute and set new one on this specific edge only
// We can't use replaceSMAttribute because it would affect all edges with the same attribute
if (edgeAttribute != undefined)
{
    removeAttributes(context, { "entities" : edgeQuery, "attributePattern" : edgeAttribute });
}
setAttribute(context, { "entities" : edgeQuery, "attribute" : newAttribute });
```

### Why This Works

1. **Target Specific Edge**: `removeAttributes` and `setAttribute` operate only on `edgeQuery` (the specific edge)
2. **Independent Assignment**: Each segment gets its own attribute independently
3. **No Cross-Talk**: Changing one segment's attribute doesn't affect others

### After the Fix

```
Original edge: BEND attribute (instance #123)
After split:   |segment1|segment2|segment3|
               All inherit BEND attribute #123

Loop iteration 1 (segment1 = bridge, should be BEND):
  - Remove attribute from segment1 only
  - Set new BEND attribute on segment1 only
  - Segments 2,3 still have original attribute

Loop iteration 2 (segment2 = stitch, should be RIP):
  - Remove attribute from segment2 only
  - Set new RIP attribute on segment2 only
  - Segments 1,3 keep their attributes

Loop iteration 3 (segment3 = bridge, should be BEND):
  - Remove attribute from segment3 only
  - Set new BEND attribute on segment3 only
  - Segments 1,2 keep their attributes

Result: ✓ segment1=BEND, segment2=RIP, segment3=BEND (correct!)
```

## Code Location

File: `custom-features/sheetMetalStitchCutBend.fs`
Function: `applyJointAttributesToSegments()`
Lines: ~403-409

## Expected Behavior After Fix

- ✅ Bridge segments receive BEND attributes
- ✅ Stitch segments receive RIP attributes  
- ✅ Each segment type is independent and correct
- ✅ Pattern generates correctly: `|BEND|RIP|BEND|RIP|BEND|`

## Testing

To verify the fix works:
1. Select a sheet metal edge joint
2. Apply stitch cut bend feature
3. Verify bridges (connections) are BEND joints
4. Verify stitches (cuts) are RIP joints
5. Check flat pattern unfolds correctly
