# Critical Insight: Split Edges Need BOTH Unique Association AND Definition Attributes

## The Problem

When splitting a sheet metal edge, BOTH resulting segments inherit ALL attributes from the parent:

### What Gets Inherited:
1. **Association Attribute (SMAssociationAttribute)** - Tracks which body/model the entity belongs to
2. **Definition Attribute (SMAttribute)** - Defines the joint properties (type, radius, angle, k-factor)

### Why This Breaks:
If both segments share the **same definition attribute object** (same attribute ID), the sheet metal system treats them as a **single logical joint** even if they have different association attributes.

## Symptoms of Shared Definition Attributes

1. **Modify Joint affects both segments** - Changing one changes both
2. **Act as single bend/rip region** - Can't be independently modified
3. **Spurious geometry** - Bend lines appear at origin in flat pattern view
4. **Failed disambiguation** - Sheet metal system can't properly track separate segments

## Complete Solution

After splitting, you MUST:

### Step 1: Remove Shared Association Attribute
```featurescript
removeAttributes(context, {
    "entities" : splitEdgesQuery,
    "attributePattern" : {} as SMAssociationAttribute
});
```

### Step 2: Remove Shared Definition Attribute
```featurescript
removeAttributes(context, {
    "entities" : splitEdgesQuery,
    "attributePattern" : {} as SMAttribute
});
```

### Step 3: Assign Unique Association Attributes
```featurescript
assignSMAssociationAttributes(context, splitEdgesQuery);
```

### Step 4: Create Unique Definition Attributes (Copy ALL Properties!)
```featurescript
// Store original properties first
const originalJointType = existingAttribute.jointType;

// Create new attribute for each segment with unique ID
for (var i = 0; i < size(splitEdgesEval); i += 1)
{
    const segmentEdge = qUnion([splitEdgesEval[i]]);
    
    var newJointAttr = makeSMJointAttribute(toAttributeId(id + ("joint" ~ i)));
    newJointAttr.jointType = originalJointType;
    
    if (originalJointType.value == SMJointType.BEND)
    {
        // Copy ALL BEND properties
        newJointAttr.radius = existingAttribute.radius;
        newJointAttr.angle = existingAttribute.angle;
        newJointAttr.kFactor = existingAttribute.kFactor;
    }
    else if (originalJointType.value == SMJointType.RIP)
    {
        // Copy ALL RIP properties
        newJointAttr.jointStyle = existingAttribute.jointStyle;
        newJointAttr.angle = existingAttribute.angle;
        newJointAttr.minimalClearance = existingAttribute.minimalClearance;
    }
    
    setAttribute(context, {
        "entities" : segmentEdge,
        "attribute" : newJointAttr
    });
}
```

## Joint Type Properties Reference

### BEND Properties:
- `jointType` = SMJointType.BEND
- `radius` - Bend radius
- `angle` - Bend angle
- `kFactor` - K-factor for bend calculation

### RIP Properties:
- `jointType` = SMJointType.RIP
- `jointStyle` - SMJointStyle.EDGE, SMJointStyle.FACE, etc.
- `angle` - Rip angle
- `minimalClearance` - Gap clearance

**Important:** Both have an `angle` property! Make sure to copy ALL properties for each type.

## Why Both Are Needed

| Attribute Type | Purpose | What Happens If Shared |
|----------------|---------|------------------------|
| **Association** | Entity tracking | "Failed to disambiguate" error |
| **Definition** | Joint properties | Segments act as single joint |

**You need BOTH to be unique for independent segments!**

## Application to Stitch Cut Bend

For the stitch cut bend feature:

1. **Split edges** into segments
2. **Remove shared association attributes**
3. **Remove shared definition attributes**
4. **Assign unique association attributes**
5. **Create definition attributes** (alternating BEND/RIP with unique IDs)
6. **Update geometry**

The key is that EACH segment must have:
- Its own unique association attribute (for tracking)
- Its own unique definition attribute (for independent behavior)

## Common Mistake

❌ **Wrong**: Only fix association attributes
```featurescript
removeAttributes(context, {...} as SMAssociationAttribute);
assignSMAssociationAttributes(context, splitEdgesQuery);
// Missing: definition attributes still shared!
```

✅ **Correct**: Fix both attribute types
```featurescript
// Remove both shared attributes
removeAttributes(context, {...} as SMAssociationAttribute);
removeAttributes(context, {...} as SMAttribute);

// Assign unique versions of both
assignSMAssociationAttributes(context, splitEdgesQuery);
for (each segment) { setAttribute(...new definition attribute...); }
```

## Verification

After fixing, verify each segment has:
```
Segment 0:
  Association ID: <unique-id-0> (e.g., JiB)
  Definition Attribute ID: <unique-id-0> (e.g., bend0)
  
Segment 1:
  Association ID: <unique-id-1> (e.g., JiF)
  Definition Attribute ID: <unique-id-1> (e.g., bend1)
```

Now they're truly independent!
