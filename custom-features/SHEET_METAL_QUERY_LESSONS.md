# Sheet Metal Query Development - Lessons Learned

This document captures the key lessons learned during the development of sheet metal query helper functions for Onshape FeatureScript. It serves as a reference for understanding sheet metal architecture, query function selection, and common pitfalls to avoid.

## Table of Contents
1. [Sheet Metal Architecture](#sheet-metal-architecture)
2. [Association Attributes](#association-attributes)
3. [Query Function Selection](#query-function-selection)
4. [Mapping Between Representations](#mapping-between-representations)
5. [Common Pitfalls](#common-pitfalls)
6. [Best Practices](#best-practices)

---

## Sheet Metal Architecture

### Two Parallel Representations

Sheet metal parts in Onshape maintain two distinct but linked representations:

1. **Definition Body (Master Body)**
   - Type: `BodyType.SHEET` (surface body)
   - Contains the master definition of the sheet metal geometry
   - Stores all SMObjectType attributes (WALL, BEND, CORNER, etc.)
   - Hidden from normal view
   - Source of truth for sheet metal semantics

2. **Model Body (3D Folded Body)**
   - Type: `BodyType.SOLID` (solid body)
   - The visible 3D representation users interact with
   - Does NOT contain SMObjectType attributes directly
   - Linked to definition body via association attributes

### Why This Architecture Matters

Operations on sheet metal must understand this dual representation:
- **Attributes exist only on the definition body** - querying model entities directly for SMObjectType attributes returns nothing
- **Users work with model bodies** - all visible entities and operations target the model
- **Mapping is required** - to use attribute information, you must map between representations

---

## Association Attributes

### Purpose

Association attributes (`SMAssociationAttribute`) establish the correspondence between definition and model entities. Each entity on the master body has a distinct association attribute, and the corresponding entity on the model body has the same attribute.

### How They Work

```featurescript
// Same attributeId links corresponding entities
definitionEntity: { attributeId: "abc123" }  // On master body
modelEntity:      { attributeId: "abc123" }  // On 3D model
```

### Key Functions

1. **`getSMAssociationAttributes(context, entities)`**
   - Gets association attributes from entities
   - Returns array of SMAssociationAttribute

2. **`getSMDefinitionEntities(context, selection)`**
   - Maps from model entities to definition entities
   - Uses association attributes to find corresponding master entities

3. **`getSMCorrespondingInPart(context, selection, entityType)`**
   - Maps from definition entities to model entities
   - Returns 3D model entities corresponding to definition entities

### Critical Insight

**You cannot get model entities directly from SMObjectType attributes.** You must:
1. Query definition entities with SMObjectType attributes
2. Get their association attributes
3. Find model entities with matching association attributes

---

## Query Function Selection

### The Critical Decision: Direction vs Normal

This was the primary source of confusion during development. Understanding the difference between these query functions is essential:

#### `qFacesParallelToDirection(query, direction)`

**Purpose:** Returns faces parallel to a given direction vector

**For planar faces:** 
- Face normal is **perpendicular** to the direction
- If direction is Z (0, 0, 1), returns faces with normals perpendicular to Z
- These are **vertical faces** when Z is up

**Use for:** Cut faces in flat pattern (vertical edge profiles)

```featurescript
// Get vertical edge faces in flat pattern
const zDirection = vector(0, 0, 1);
const cutFaces = qFacesParallelToDirection(flatFaces, zDirection);
// Returns faces parallel to Z = vertical edges
```

#### `qParallelPlanes(query, normal)`

**Purpose:** Returns planar faces with normals parallel to a given normal vector

**For planar faces:**
- Face normal is **parallel** to the given normal
- If normal is Z (0, 0, 1), returns faces with normals parallel to Z
- These are **horizontal faces** when Z is up

**Use for:** Stock faces in flat pattern (top/bottom surfaces)

```featurescript
// Get horizontal stock faces in flat pattern
const zDirection = vector(0, 0, 1);
const stockFaces = qParallelPlanes(flatFaces, zDirection);
// Returns faces with normals parallel to Z = horizontal surfaces
```

### Key Distinction

| Function | Direction/Normal Relationship | In Flat Pattern (Z up) |
|----------|------------------------------|------------------------|
| `qFacesParallelToDirection` | Normal ⊥ Direction | Vertical edges (cut profiles) |
| `qParallelPlanes` | Normal ∥ Direction | Horizontal surfaces (stock) |

### Why Not Dot Products?

**Avoid manual vector math when robust query functions exist.**

❌ **Wrong approach:**
```featurescript
// Don't do this
const normalDotZ = abs(dot(facePlane.normal, zAxis));
if (normalDotZ > 0.99) { ... }
```

✅ **Correct approach:**
```featurescript
// Use built-in query functions
const cutFaces = qFacesParallelToDirection(flatFaces, zDirection);
```

**Reasons:**
1. Query functions are optimized and tested
2. Avoid floating-point comparison errors
3. More maintainable and readable
4. Less error-prone

---

## Mapping Between Representations

### Flat Pattern to 3D Model

The most common operation is identifying 3D model faces based on flat pattern geometry:

```featurescript
export function qSheetMetalCutFaces(context is Context, sheetMetalPart is Query) returns Query
{
    // Step 1: Get all 3D model faces
    const modelFaces = qOwnedByBody(sheetMetalPart, EntityType.FACE);
    
    // Step 2: Map to flat pattern
    const flatFaces = qCorrespondingInFlat(modelFaces);
    
    // Step 3: Filter in flat pattern using geometric queries
    const cutFacesInFlat = qFacesParallelToDirection(flatFaces, vector(0, 0, 1));
    
    // Step 4: Map back to 3D model faces
    const cutFaceEntitiesInFlat = evaluateQuery(context, cutFacesInFlat);
    var cutFacesIn3D = [];
    
    const modelFaceEntities = evaluateQuery(context, modelFaces);
    for (var modelFace in modelFaceEntities)
    {
        const correspondingFlatFace = evaluateQuery(context, qCorrespondingInFlat(modelFace));
        
        for (var cutFace in cutFaceEntitiesInFlat)
        {
            if (size(correspondingFlatFace) > 0 && correspondingFlatFace[0] == cutFace)
            {
                cutFacesIn3D = append(cutFacesIn3D, modelFace);
                break;
            }
        }
    }
    
    // Step 5: Return 3D model faces
    return qUnion(cutFacesIn3D);
}
```

### Key Mapping Functions

1. **`qCorrespondingInFlat(entitiesInFolded)`**
   - Maps from 3D model to flat pattern
   - One-way mapping (3D → Flat)
   - Query-based, doesn't require context for construction

2. **Reverse mapping requires iteration**
   - No direct `qCorrespondingIn3D` function exists
   - Must iterate through 3D entities and check their flat counterparts

---

## Common Pitfalls

### 1. Querying Model Entities for SMObjectType Attributes

❌ **Wrong:**
```featurescript
const wallPattern = asSMAttribute({ "objectType" : SMObjectType.WALL });
const wallFaces = qAttributeFilter(modelFaces, wallPattern);
// Returns nothing! Attributes are on definition, not model
```

✅ **Correct:**
```featurescript
const definitionBody = getSheetMetalModelForPart(context, part);
const wallFaces = qAttributeFilter(qOwnedByBody(definitionBody, EntityType.FACE), wallPattern);
const modelWallFaces = getSMCorrespondingInPart(context, wallFaces, EntityType.FACE);
```

### 2. Returning Flat Pattern Faces Instead of 3D Model Faces

❌ **Wrong:**
```featurescript
export function qSheetMetalCutFaces(...) returns Query
{
    const flatFaces = qCorrespondingInFlat(modelFaces);
    const cutFaces = qFacesParallelToDirection(flatFaces, zDirection);
    return cutFaces; // Returns flat pattern faces!
}
```

✅ **Correct:**
```featurescript
export function qSheetMetalCutFaces(...) returns Query
{
    const flatFaces = qCorrespondingInFlat(modelFaces);
    const cutFaces = qFacesParallelToDirection(flatFaces, zDirection);
    // Map back to 3D model faces before returning
    return mapToModelFaces(context, modelFaces, cutFaces);
}
```

**Why this matters:** Operations like move face, extrude, etc. require 3D model entities, not flat pattern entities.

### 3. Confusing Direction with Normal

❌ **Wrong:**
```featurescript
// Want vertical edges, so use parallel planes?
const cutFaces = qParallelPlanes(flatFaces, zDirection); // Returns horizontal faces!
```

✅ **Correct:**
```featurescript
// Vertical edges = faces parallel to Z direction
const cutFaces = qFacesParallelToDirection(flatFaces, zDirection);
```

### 4. Using Subtraction Instead of Direct Query

❌ **Inefficient:**
```featurescript
const stockFaces = qParallelPlanes(flatFaces, zDirection);
const cutFaces = qSubtraction(flatFaces, stockFaces);
```

✅ **Better:**
```featurescript
const cutFaces = qFacesParallelToDirection(flatFaces, zDirection);
```

### 5. Forgetting Context Parameter for Query Builders

Some query functions that need to evaluate/map require context:

```featurescript
// Pure query constructors - no context needed
export function qSomeQuery(query is Query) returns Query { ... }

// Query builders that evaluate - context needed
export function qSheetMetalCutFaces(context is Context, query is Query) returns Query { ... }
```

---

## Best Practices

### 1. Always Return 3D Model Entities

Users expect to work with visible 3D model entities. Always map back from flat pattern or definition to 3D model before returning.

### 2. Use Robust Query Functions

Prefer built-in query functions over manual calculations:
- Use `qParallelPlanes` instead of dot product comparisons
- Use `qFacesParallelToDirection` for directional filtering
- Use `qGeometry` for geometry type filtering

### 3. Document Query Behavior Clearly

Specify in documentation:
- What representation (3D model vs flat pattern vs definition)
- What geometric criteria are used
- What is returned

### 4. Handle Edge Cases

- Check for empty queries with `isQueryEmpty`
- Use `try`/`catch` when evaluating geometry that might not exist
- Validate that parts are actually sheet metal

### 5. Consistent Naming Conventions

Follow established patterns:
- Query constructors: `qSomething`
- Context required: Include `context is Context` parameter
- Clear function names describing what is returned

### 6. Comments Should Explain "Why" Not "What"

❌ **Bad:**
```featurescript
// Get faces
const faces = qOwnedByBody(part, EntityType.FACE);
```

✅ **Good:**
```featurescript
// Get all 3D model faces to map to flat pattern
const modelFaces = qOwnedByBody(sheetMetalPart, EntityType.FACE);
```

---

## Summary of Key Concepts

1. **Sheet metal has three representations**: definition body (master), 3D model body, and flat pattern
2. **Attributes exist on definition body only**, not on model entities
3. **Association attributes link definition to model** via matching attributeId
4. **qFacesParallelToDirection returns faces perpendicular to direction** (for planar faces)
5. **qParallelPlanes returns faces with normals parallel to the given normal**
6. **Always return 3D model entities** from query functions users will call
7. **Avoid manual vector math** when robust query functions exist
8. **Map from flat to 3D requires iteration**, no direct query exists

---

## Example: Complete Implementation Pattern

```featurescript
/**
 * Query for sheet metal entities with specific geometric properties.
 * Always returns 3D model entities suitable for geometry operations.
 */
export function qSheetMetalCustomQuery(context is Context, sheetMetalPart is Query) returns Query
{
    // 1. Start with 3D model
    const modelFaces = qOwnedByBody(sheetMetalPart, EntityType.FACE);
    
    // 2. Map to flat pattern for geometric analysis
    const flatFaces = qCorrespondingInFlat(modelFaces);
    
    // 3. Apply geometric filter using robust query functions
    const filteredFlatFaces = qSomeGeometricQuery(flatFaces, ...);
    
    // 4. Map back to 3D model (requires iteration)
    const filteredFlatEntities = evaluateQuery(context, filteredFlatFaces);
    var resultIn3D = [];
    
    const modelFaceEntities = evaluateQuery(context, modelFaces);
    for (var modelFace in modelFaceEntities)
    {
        const correspondingFlat = evaluateQuery(context, qCorrespondingInFlat(modelFace));
        
        for (var filteredFace in filteredFlatEntities)
        {
            if (size(correspondingFlat) > 0 && correspondingFlat[0] == filteredFace)
            {
                resultIn3D = append(resultIn3D, modelFace);
                break;
            }
        }
    }
    
    // 5. Return 3D model entities
    return qUnion(resultIn3D);
}
```

---

## References

- Onshape Standard Library: `sheetMetalAttribute.fs` - Association attribute definitions
- Onshape Standard Library: `sheetMetalUtils.fs` - Mapping functions
- Onshape Standard Library: `query.fs` - Query function documentation
- Onshape FeatureScript Documentation: https://cad.onshape.com/FsDoc/

---

*This document was created during the development of sheet metal query helpers and should be updated as new patterns and lessons emerge.*
