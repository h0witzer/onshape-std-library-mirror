# Deep Analysis: assignSMAttributesToNewOrSplitEntities

## Function Signature
```featurescript
export function assignSMAttributesToNewOrSplitEntities(
    context is Context, 
    sheetMetalModels is Query,
    initialData is map, 
    operationId is Id
) returns map
```

## Purpose
This function handles attribution when sheet metal entities are created or split during operations. It ensures proper association and definition attribute propagation/assignment.

## Input Parameters

1. **context**: The FeatureScript context
2. **sheetMetalModels**: Query for the sheet metal model body/bodies
3. **initialData**: Map from `getInitialEntitiesAndAttributes()` containing:
   - `originalEntities`: Array of entities that existed before the operation
   - `originalEntitiesTracking`: Tracking queries for those entities
   - `initialAssociationAttributes`: Association attributes before the operation
4. **operationId**: Feature ID for generating unique attribute IDs

## Return Value
```featurescript
{
    "modifiedEntities" : Query,      // Entities that were new or split
    "deletedAttributes" : Array      // Association attributes that were deleted
}
```

## Algorithm Flow

### Phase 1: Identify "New" Entities (Lines 969-989)

**Purpose:** Find entities that didn't exist before the operation.

```featurescript
// Build map of original entity IDs
originalOrModifiedEntitiesMap = {
    transientId -> true for all original entities
    + transientId -> true for tracked entities that resolved to 1 entity
}

// Find entities not in the map
entitiesToAddAssociations = entities owned by model 
                            WHERE transientId NOT IN originalOrModifiedEntitiesMap
```

**Key insight:** If a tracked entity resolves to exactly 1 entity (line 977), it's considered "unchanged" and added to the map. Only truly new entities (transients not in the map) are identified for processing.

### Phase 2: Handle Split Entities with Shared Association Attributes (Lines 991-1097)

**Purpose:** When an entity splits, multiple entities may share the same association attribute. Handle this by designating one "master" entity to keep the original attribute, and give others new attributes.

**For each association attribute found on new entities:**

#### Step 2a: Categorize entities (Lines 1000-1008)
```featurescript
For entities with this association attribute:
    if transientId in originalOrModifiedEntitiesMap:
        existingEntitiesWithAttribute.append(entity)  // Original entity
    else:
        newEntitiesWithAttribute.append(entity)       // New entity from split
```

#### Step 2b: Choose master entity (Lines 1010-1036)
```featurescript
if existingEntitiesWithAttribute is not empty:
    masterEntities = first existing entity  // Original keeps attribute
    entitiesToModify = all new entities
else:
    // All entities are new (e.g., original was deleted)
    masterEntities = first new entity (prefer two-sided edge)
    entitiesToModify = remaining new entities
```

**Logic:** Prefer keeping the attribute on an entity that existed before. If none exist, pick one new entity as master (preferring two-sided edges for robustness).

#### Step 2c: Propagate definition attributes (Lines 1038-1093)
```featurescript
definitionAttributes = get SMAttribute from masterEntities

if masterEntity has definition attribute:
    for each entity in [masterEntity, ...entitiesToModify]:
        if entity is NOT appropriate for this attribute:
            remove definition attribute from entity
            
            // Special case: BEND on laminar edge -> convert to RIP
            if attribute was BEND and entity is two-sided edge:
                create RIP attribute instead
            // Special case: BEND on single face -> convert to WALL
            else if attribute was BEND and entity is single face:
                create WALL attribute
        else:
            // Entity can keep the attribute
            if first appropriate entity found:
                make it the new masterEntity  // Promotes it to keep original attribute
```

**Key behaviors:**
1. Definition attributes are propagated if the entity is "appropriate" for them
2. BEND attributes get special handling:
   - Convert to RIP for two-sided edges that can't support bend
   - Convert to WALL for single faces
3. The first entity that can keep the attribute becomes the new master

#### Step 2d: Clean up association attributes (Lines 1095-1096)
```featurescript
removeAttributes(entitiesToModify, association attribute)
```

Remove the shared association attribute from non-master entities. They'll get new ones in Phase 3.

### Phase 3: Assign New Association Attributes (Lines 1099-1104)

**Purpose:** Give unique association attributes to entities that don't have any.

```featurescript
entitiesWithExistingAttributes = entities that have any association attribute
entitiesThatNeedAssociation = entitiesToAddAssociations - entitiesWithExistingAttributes

assignSMAssociationAttributes(entitiesThatNeedAssociation)
```

**Key:** Only entities with NO association attribute get new ones. Entities that got to keep their shared association (the "master") don't need new ones.

### Phase 4: Track Deleted Attributes (Lines 1106-1115)

**Purpose:** Identify which association attributes were deleted during the operation.

```featurescript
finalAssociationAttributes = all association attributes now on model
finalAssociationAttributesMap = { attributeId -> true }

deletedAttributes = initialAssociationAttributes 
                    WHERE attributeId NOT IN finalAssociationAttributesMap
```

### Phase 5: Handle Wall Duplication (Lines 1117-1119)

```featurescript
if version >= V2248:
    splitWallAttributes(context, operationId, planar entities from new entities)
```

Ensures wall attributes don't get duplicated on split planar faces.

## Key Insights

### 1. "New" vs "Split" Entities

- **New entities**: Truly created, not in originalOrModifiedEntitiesMap
- **Split entities**: New entities that share an association attribute with others (indicating a split occurred)
- **Modified entities**: Tracked entities that resolved to 1 entity (considered "unchanged")

### 2. The "Master" Entity Pattern

When entities split:
1. One entity keeps the original association attribute (the "master")
2. Others get new unique association attributes
3. Definition attributes are propagated if appropriate

Preference order for master:
1. An entity that existed before (from existingEntitiesWithAttribute)
2. First entity that can appropriately keep the definition attribute
3. First new entity (preferring two-sided edges)

### 3. Why modifiedEntities Can Be Empty

```featurescript
return { "modifiedEntities" : entitiesToAddAssociationsQ, ... }
```

`entitiesToAddAssociationsQ` contains entities that are "new" (not in original map). If:
- Edges are split
- Both inherit all attributes from parent
- Tracking resolves both as "existing" (added to originalOrModifiedEntitiesMap at line 979)
- Result: Neither is "new" → empty query

**This happens in our stitch cut bend case!**

### 4. Attribute Propagation Intelligence

The function intelligently converts attributes:
- **BEND** on laminar (one-sided) edge → **RIP**
- **BEND** on single face → **WALL**
- Keeps attribute on entities that are "appropriate" for it

## Application to Stitch Cut Bend

### Why it returns empty modifiedEntities:

1. Original edge has association + definition attributes
2. After split, both segments inherit these attributes
3. Function sees both segments already have attributes
4. Tracking may resolve them as "existing" 
5. Neither appears in `entitiesToAddAssociations`
6. Result: `modifiedEntities` is empty

### Why we still need it:

1. ✅ **deletedAttributes tracking**: Essential for cleanup
2. ✅ **Handles edge cases**: If some segments become inappropriate for their attributes
3. ✅ **Association management**: Ensures proper attribute isolation

### Why we can't rely on modifiedEntities:

The function is designed for scenarios where:
- NEW entities are created and attributed
- Or split entities need attribute propagation

NOT for:
- Modifying existing split entities' attributes after the fact

### Correct usage for stitch cut bend:

```featurescript
// 1. Capture initial state
const initialData = getInitialEntitiesAndAttributes(context, modelBodyQuery);

// 2. Split edges (they inherit attributes)

// 3. Call function for attribution handling
const toUpdate = assignSMAttributesToNewOrSplitEntities(context, modelBodyQuery, initialData, id);

// 4. Modify attributes on split segments
applyJointAttributesToSegments(..., SMJointType.BEND);
applyJointAttributesToSegments(..., SMJointType.RIP);

// 5. Use actual modified edges, not toUpdate.modifiedEntities
updateSheetMetalGeometry(context, id, {
    "entities" : actualModifiedEdges,  // NOT toUpdate.modifiedEntities
    "deletedAttributes" : toUpdate.deletedAttributes,  // DO use this
    ...
});
```

## Conclusion

`assignSMAttributesToNewOrSplitEntities` is a sophisticated function for handling entity creation and splitting. It:
- Identifies new entities
- Handles split entities by designating masters
- Propagates and converts attributes intelligently
- Tracks deleted attributes
- Assigns new association attributes where needed

For stitch cut bend, we use it for `deletedAttributes` tracking but pass our own entity list to `updateSheetMetalGeometry` because split edges inherit attributes and don't appear as "new" to this function.
