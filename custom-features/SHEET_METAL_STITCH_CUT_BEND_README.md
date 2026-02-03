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
- ✅ **Bend relief attributes applied to vertexes at bend/rip boundaries**

## Recent Enhancement: Automatic Bend Relief Application

The feature now automatically applies bend relief attributes to vertexes at the boundaries between bend and rip segments. This enhancement:

- **Reads model parameters:** Extracts bend relief settings (style, scale, depth) from the sheet metal model
- **Identifies boundary vertexes:** Uses `qCreatedBy()` to find vertexes created by split operations, then filters to those adjacent to both bend and rip edges
- **Applies corner attributes:** Creates or updates corner attributes with the model's bend relief settings
- **Maintains consistency:** Uses the same relief parameters defined in the sheet metal model

**Implementation Note:** The feature uses `qCreatedBy(splitOperationId, EntityType.VERTEX)` to directly query vertexes created by the `opSplitEdges` operations, rather than using the deprecated `qVertexAdjacent` function. This is more efficient and accurate since these vertexes are precisely what we need to attribute.

This ensures that the stitch cut bend feature properly integrates with the sheet metal model's relief settings without requiring user input for conflicting parameters.

## Usage

1. Select a joint edge (bend or rip) in an active sheet metal model
2. Set bridge width (width of each bend segment/connection)
3. Configure spacing (equal, linear, custom)
4. Optionally override bend radius and k-factor (defaults to model settings)
5. Feature splits edge into alternating bend/rip segments
6. **Bend reliefs are automatically applied using model settings**

## Files

- `sheetMetalStitchCutBend.fs` - Production feature implementation
- `smSplitEdgeTester.fs` - Minimal validation tester (split 1 edge → 2 segments)
- `SHEET_METAL_STITCH_CUT_BEND_README.md` - This complete documentation

## Technical Reference

**Key Functions:**
- `getModelParameters()` - Get model configuration
- `getModelAttribute()` - Get full model attribute including relief parameters
- `opSplitEdges()` - Split edges at parameters
- `removeAttributes()` - Clear shared attributes
- `assignSMAssociationAttributes()` - Assign unique associations
- `makeSMJointAttribute()` - Create definition attributes
- `makeSMCornerAttribute()` - Create corner attributes for bend reliefs
- `getCornerAttribute()` - Get existing corner attributes
- `applyBendReliefAttributesToVertexes()` - Apply bend relief to boundary vertexes
- `qCreatedBy()` - Query entities created by split operations
- `qAdjacent()` - Query adjacent entities (replaces deprecated qVertexAdjacent)
- `setAttribute()` - Apply attributes to entities
- `replaceSMAttribute()` - Update existing attributes
- `bendAngle()` - Compute geometry-accurate bend angle
- `updateSheetMetalGeometry()` - Process attributed entities

**Standard Library Modules:**
- `sheetMetalAttribute.fs` - Attribute structures
- `sheetMetalUtils.fs` - Model parameters, geometry updates
- `geomOperations.fs` - Edge splitting operations
- `attributes.fs` - Attribute management
- `query.fs` - Query operations for finding entities
- `evaluate.fs` - Corner type evaluation


