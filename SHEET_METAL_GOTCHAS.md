# Sheet Metal Feature Development Gotchas

This document catalogs non-obvious requirements and behaviors when developing sheet metal features in FeatureScript for Onshape.

## Critical Requirements

### 1. Context Naming - Function Name Must Be "sheetMetalStart"

**Issue**: Custom sheet metal features show "Unknown" as the context name in the flat pattern context window.

**Root Cause**: The flat pattern context window in Onshape only inherits the feature name if the main feature function is explicitly named `sheetMetalStart`.

**Solution**: Name your main feature function exactly `sheetMetalStart` (not `sheetMetalStartCustom`, `mySheetMetalStart`, etc.)

```featurescript
// ✅ CORRECT - Context will be named properly
export function sheetMetalStart(context is Context, id is Id, definition is map)
{
    // your implementation
}

// ❌ WRONG - Context name will show as "Unknown"
export function customSheetMetal(context is Context, id is Id, definition is map)
{
    // your implementation
}
```

**Impact**: This is a hard-coded requirement in Onshape's system and cannot be worked around through ID naming, attributes, or other approaches.

---

### 2. Surface Body Visibility - Must Use defineSheetMetalFeature

**Issue**: Surface bodies (the sheet metal definition bodies) remain visible in the viewport even when properly annotated with sheet metal attributes.

**Root Cause**: Surface bodies will not be automatically hidden unless:
1. The feature is defined using `defineSheetMetalFeature` instead of `defineFeature`, OR
2. Another sheet metal feature interacts with the bodies, forcing a sheet metal update

**Solution**: Use `defineSheetMetalFeature` to define your feature:

```featurescript
// ✅ CORRECT - Surface bodies will be hidden automatically
defineSheetMetalFeature(sheetMetalStart, {
    // feature definition
});

// ❌ WRONG - Surface bodies remain visible
defineFeature(sheetMetalStart, {
    // feature definition
});
```

**Workaround**: If you cannot use `defineSheetMetalFeature`, surface bodies will become hidden after any subsequent sheet metal operation (like move face) interacts with them, as this forces a sheet metal geometry update.

**Impact**: Visible surface bodies are confusing to users and make the model appear broken, even though the sheet metal functionality works correctly.

---

### 3. ID Pattern for Operations vs Queries

**Issue**: Using the wrong ID for queries causes double surfaces/parts and tracking issues.

**Pattern**: SheetMetalStart uses `id + "extractSurface"` for the operation but references bodies/faces/edges with just `id`:

```featurescript
// Operation uses sub-ID
var surfaceId = id + "extractSurface";
opExtractSurface(context, surfaceId, {
    "faces" : facesToExtract
});

// Annotation and queries use BASE ID (not surfaceId!)
annotateSmSurfaceBodies(context, id, {
    "surfaceBodies" : qCreatedBy(id, EntityType.BODY),  // NOT surfaceId!
    // ...
});

updateSheetMetalGeometry(context, id, {
    "entities" : qUnion([
        qCreatedBy(id, EntityType.FACE),   // NOT surfaceId!
        qCreatedBy(id, EntityType.EDGE)
    ])
});
```

**Why**: The operation ID is used internally for the geometry operation, but Onshape's sheet metal system tracks entities based on the base feature ID for context management.

---

## Best Practices

### Follow the Canonical Pattern

When implementing sheet metal features, follow the exact pattern from `sheetMetalStart.fs`:

1. Use `defineSheetMetalFeature` for feature definition
2. Name main function exactly `sheetMetalStart` for proper context naming
3. Extract surfaces with `id + "extractSurface"` (or similar sub-ID)
4. Annotate and finalize using base `id` with `qCreatedBy(id, ...)`
5. Use `annotateSmSurfaceBodies` for setting MODEL, WALL attributes
6. Use `updateSheetMetalGeometry` to finalize and create 3D representations

### Don't Take Shortcuts

- Attempting to simplify or optimize away from the canonical pattern often leads to broken behavior
- The pattern exists because of specific requirements in Onshape's sheet metal engine
- What looks like unnecessary complexity is often required for correct operation

### Test Thoroughly

When developing sheet metal features, verify:
- [ ] Context name appears correctly (not "Unknown")
- [ ] Surface bodies are hidden from viewport
- [ ] Sheet metal attributes are properly set
- [ ] Flat pattern generates correctly
- [ ] Multiple instances don't interfere with each other
- [ ] Subsequent operations work correctly on the sheet metal bodies

---

## Common Pitfalls

### Pitfall: Assuming `defineFeature` is sufficient
**Problem**: Surface bodies remain visible  
**Solution**: Use `defineSheetMetalFeature`

### Pitfall: Using creative function names
**Problem**: Context shows as "Unknown"  
**Solution**: Name function exactly `sheetMetalStart`

### Pitfall: Using operation sub-IDs in queries
**Problem**: Double surfaces, tracking issues  
**Solution**: Use base `id` for `qCreatedBy` queries

### Pitfall: Deleting surface bodies after creation
**Problem**: Sheet metal context is destroyed  
**Solution**: Surface bodies must remain (they are the sheet metal definition)

---

## Version Information

This document is based on FeatureScript 2815 and Onshape Standard Library version 2815.0.

## Contributing

If you discover additional sheet metal development gotchas, please document them here following the same format:
- Clear description of the issue
- Root cause explanation
- Concrete solution with code examples
- Impact assessment
