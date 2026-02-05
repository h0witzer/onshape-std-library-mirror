# Sheet Metal Stitch Cut Bend - Complete Documentation

## Problem Statement

The sheet metal stitch cut bend custom feature wasn't correctly applying attributes to split edge domains. When splitting a joint edge into alternating bend/rip segments, attributes along the chain were being destroyed, resulting in open edges without proper bend or rip geometry.

## Root Cause

When edges are split using `opSplitEdges`, the resulting segments inherit **both** the association attribute and definition attribute from the parent edge. This causes two critical issues:

1. **Shared Association Attributes** → All split segments share the same association attribute ID, causing the sheet metal system's disambiguation to fail with "Failed to completely disambiguate created topology"

2. **Shared Definition Attributes** → All split segments reference the same SMAttribute object (same attribute ID), making them behave as a single logical joint instead of independent segments

## The Solution

### Core Pattern (6 Steps)

```featurescript
// 1. Split edges with proper tracking
const trackedEdges = qUnion([orderedEdgeQuery, startTracking(context, orderedEdgeQuery)]);
opSplitEdges(context, id + "split", { "edges" : trackedEdges, ... });
const allEdgesAfterSplit = qEntityFilter(trackedEdges, EntityType.EDGE);

// 2. Remove shared association attributes
removeAttributes(context, {
    "entities" : allEdgesAfterSplit,
    "attributePattern" : {} as SMAssociationAttribute
});

// 3. Remove shared definition attributes
removeAttributes(context, {
    "entities" : allEdgesAfterSplit,
    "attributePattern" : {} as SMAttribute
});

// 4. Assign unique association attributes
assignSMAssociationAttributes(context, allEdgesAfterSplit);

// 5. Create unique definition attributes for each segment
for (var i = 0; i < size(segments); i += 1)
{
    const segment = segments[i];
    const isB end = (i % 2 == 0);  // Alternating pattern
    
    // Create new attribute with unique ID
    var newAttr = makeSMJointAttribute(toAttributeId(id + ("bend" ~ i)));
    newAttr.jointType = isBend ? SMJointType.BEND : SMJointType.RIP;
    
    // For BEND: copy radius, kFactor, COMPUTE angle
    if (isBend) {
        newAttr.radius = {...};
        newAttr.kFactor = {...};
        const angle = bendAngle(context, id + ("angle" ~ i), segment, radius);
        newAttr.angle = { "value" : angle, "canBeEdited" : false };
    }
    
    // For RIP: copy jointStyle, angle, minimalClearance
    if (isRip) {
        newAttr.jointStyle = {...};
        newAttr.angle = {...};
        newAttr.minimalClearance = {...};
    }
    
    setAttribute(context, { "entities" : segment, "attribute" : newAttr });
}

// 6. Update geometry
updateSheetMetalGeometry(context, id, {
    "entities" : allEdgesAfterSplit,
    "associatedChanges" : allEdgesAfterSplit
});
```

### Critical Details

**1. Both Attribute Types Must Be Unique**

| Attribute Type | Purpose | If Shared → Problem |
|----------------|---------|---------------------|
| **Association** (SMAssociationAttribute) | Links entity to sheet metal model for tracking | Multiple entities with same ID → disambiguation error |
| **Definition** (SMAttribute) | Defines joint properties (type, radius, angle, etc.) | Multiple entities with same ID → behave as single joint |

**2. All Properties Must Be Copied**

**BEND Properties:**
- `jointType` = SMJointType.BEND
- `radius` - Bend radius (with metadata: value, canBeEdited, isDefault, controllingFeatureId, parameterIdInFeature)
- `angle` - **MUST BE COMPUTED** per segment using `bendAngle(context, id, segmentEdge, radius)`, not copied
- `kFactor` - K-factor for bend calculation

**RIP Properties:**
- `jointType` = SMJointType.RIP
- `jointStyle` - SMJointStyle.EDGE, FACE, BUTT, etc.
- `angle` - Rip angle (can be copied from original)
- `minimalClearance` - Gap clearance

**3. Get Model Defaults BEFORE Splitting**

```featurescript
// CRITICAL: Get model parameters BEFORE any operations
const modelParams = getModelParameters(context, definition.entity);

var defaultRadius, defaultKFactor;
if (definition.useDefaultRadius)
    defaultRadius = modelParams.defaultBendRadius;
if (definition.useDefaultKFactor)
    defaultKFactor = modelParams.kFactor;

// Then do splitting and attribute modification...
```

Once edges are split and attributes removed, you can't query the model anymore.

**4. Why `assignSMAttributesToNewOrSplitEntities` Doesn't Work**

This function is designed for **propagating EXISTING attributes** to split entities when you want to KEEP the same attribute type. 

In our case:
- We're **CHANGING** attribute types (from original to alternating BEND/RIP)
- Split edges inherit attributes, so function sees "nothing new"
- Returns empty `modifiedEntities` query
- Designed for creation scenarios, not modification scenarios

## Key Insights

### The Tester Validation

Created `smSplitEdgeTester.fs` to validate the pattern in isolation:
- Input: 1 BEND or RIP edge
- Action: Split into 2 segments at midpoint
- Result: 2 independent segments, each with unique attributes
- Works for both BEND and RIP joint types

This proved the core attribution pattern before applying to the complex alternating stitch cut bend.

### Common Mistakes to Avoid

❌ **Don't** rely on `assignSMAttributesToNewOrSplitEntities` for attribute modification
❌ **Don't** copy bend angles - they must be computed per segment
❌ **Don't** forget to copy all properties (missing jointStyle → "N/A" in UI)
❌ **Don't** try to get model parameters after removing attributes
❌ **Don't** use `qIntersection` or complex queries - keep it simple

✅ **Do** remove both association and definition attributes
✅ **Do** assign unique IDs to each segment
✅ **Do** compute bend angles using `bendAngle()`
✅ **Do** get model parameters early, before any operations
✅ **Do** use helper functions with proper metadata structure

## Result

The feature now:
- ✅ Splits edges correctly with visible domain separation
- ✅ Each segment has unique association attribute (independent tracking)
- ✅ Each segment has unique definition attribute (independent properties)
- ✅ Alternating BEND/RIP pattern works correctly
- ✅ Bend tables show accurate geometry-specific angles
- ✅ Modify Joint can independently change each segment
- ✅ All properties correctly populated with full metadata
- ✅ **NEW: Bend relief support with subsegment creation**

## Bend Relief Support

### Problem

Bend relief attributes cannot be applied to geometry that has rip association. This prevented proper bend relief from being created at the ends of bend segments in stitch cut bends.

### Solution

When the sheet metal model has bend relief style set to RECTANGLE or OBROUND (not TEAR), the feature automatically creates additional subsegments at the start and end of each bend region. These subsegments:

1. **Have NO attributes** - Relief segments are left unattributed, acting as free edges at sheet ends
2. **Are sized based on material thickness** - Uses `thickness × depthScale × safetyMargin`, not bend radius
3. **Allow bend relief geometry** - The sheet metal update handler creates bend relief at junction vertices
4. **Have corner attributes at junctions** - Corner attributes applied to vertices between relief and bend segments

### Implementation Details

**Functions:**
- `getBendReliefParameters()` - Extracts bend relief settings from model definition
- `shouldCreateBendReliefSubsegments()` - Determines if subsegments are needed
- `calculateBendReliefSubsegmentSize()` - Calculates proper subsegment size based on thickness
- `calculateSplitParametersFromDomains()` - Tracks which splits create bend-to-relief junctions
- `applyCornerAttributesToBendReliefVertices()` - Uses `qCreatedBy` to apply corner attributes to junction vertices

**Algorithm:**
1. Extract bend relief parameters and thickness from sheet metal model
2. For RECTANGLE or OBROUND styles, create subsegments at each bend end
3. Size subsegments = `thickness × depthScale` (directly from model, no safety margin)
4. Track which domain boundaries are bend-to-relief junctions during domain processing
5. During edge splitting, store operation IDs for bend-to-relief splits
6. Leave relief segments **unattributed** (no bend or rip attributes)
7. Use `qCreatedBy` to get vertices from bend-to-relief split operations
8. Apply corner attributes only to those specific vertices

**Result:**
- Relief segments act as free edges
- Subsegments sized exactly to accommodate bend relief geometry without excess
- Corner attributes at bend-to-relief junctions trigger bend relief geometry creation
- No adjacency checks needed - vertices identified by split operation
- Correctly handles edge pattern: `bend-relief-bend-relief-rip-relief-bend...`
- Skips relief-to-rip junctions and end vertices without relief
- No self-intersecting geometry from oversized relief segments

**Design Pattern:**
```
[Relief-Free][Bend-Attributed][Relief-Free][Rip-Attributed][Relief-Free]
      ↑             ↑              ↑              ↑              ↑
      |       Corner attr          |           No attr          |
   (from split)    here       (from split)                (from split)
   
Only bend→relief splits create attributed vertices
```

## Usage

1. Select a joint edge (bend or rip) in an active sheet metal model
2. Set bridge width (width of each bend segment/connection)
3. Configure spacing (equal, linear, custom)
4. Optionally override bend radius and k-factor (defaults to model settings)
5. Feature splits edge into alternating bend/rip segments
6. **Bend relief is automatically created based on model settings**

## Files

- `sheetMetalStitchCutBend.fs` - Production feature implementation
- `smSplitEdgeTester.fs` - Minimal validation tester (split 1 edge → 2 segments)
- `SHEET_METAL_STITCH_CUT_BEND_README.md` - This complete documentation

## Technical Reference

**Key Functions:**
- `getModelParameters()` - Get model configuration
- `getBendReliefParameters()` - Get bend relief settings from model
- `opSplitEdges()` - Split edges at parameters
- `removeAttributes()` - Clear shared attributes
- `assignSMAssociationAttributes()` - Assign unique associations
- `makeSMJointAttribute()` - Create definition attributes
- `makeSMCornerAttribute()` - Create corner attributes for bend relief
- `setAttribute()` - Apply attributes to entities
- `bendAngle()` - Compute geometry-accurate bend angle
- `updateSheetMetalGeometry()` - Process attributed entities

**Standard Library Modules:**
- `sheetMetalAttribute.fs` - Attribute structures
- `sheetMetalUtils.fs` - Model parameters, geometry updates
- `smreliefstyle.gen.fs` - Relief style enumerations
- `geomOperations.fs` - Edge splitting operations
- `attributes.fs` - Attribute management

