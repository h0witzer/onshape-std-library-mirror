# Stitch Cut Bend - Edge Selection and evLength Fixes

## Issues Resolved

### Issue 1: evLength Precondition Failure
**Error Message:**
```
Precondition of evLength failed (arg.entities is Query)
Line 142: computeCurvePatternSpacing
```

**Root Cause:**
- `computeCurvePatternSpacing()` expects `definition.edges` parameter
- Feature used `definition.entity` (sheet metal naming convention)
- Spacing utilities couldn't find the edges parameter

**Fix:**
Added mapping before calling spacing calculation:
```featurescript
// Set edges for spacing calculation (spacing utilities expect definition.edges)
definition.edges = jointEntity;
```

### Issue 2: Edge Selection Not Working
**Error Message:**
```
"no sheet metal edge selected" regardless of what is selected
```

**Root Cause:**
After finding `jointEntity` (the sheet metal definition entity) at line 91-102, the code incorrectly continued using `definition.entity` (the user's raw selection) for edge operations:

```featurescript
// WRONG: Using user selection instead of definition entity
const selectedEdgesQuery = qEntityFilter(definition.entity, EntityType.EDGE);
const totalLength = evLength(context, { "entities" : definition.entity });
```

In sheet metal features, the user selection could be:
- 3D folded geometry edge
- Flat pattern edge  
- Part geometry edge

But operations must use the **definition entity** which is the master sheet metal body representation.

**Fix:**
Changed all edge operations to use `jointEntity`:

```featurescript
// Line 118: Filter the definition entity, not user selection
const selectedEdgesQuery = qEntityFilter(jointEntity, EntityType.EDGE);

// Line 129: Use jointEntity in error reporting
throw regenError("Unable to order...", ["entity"], jointEntity);

// Line 132: Calculate length on definition entity
const totalLength = evLength(context, { "entities" : jointEntity });

// Line 142: Pass definition entity to spacing utilities
definition.edges = jointEntity;
```

## Code Flow Comparison

### Before (Broken):
```
User selects edge → definition.entity
↓
Find jointEntity from definition.entity (getSMDefinitionEntities)
↓
✗ Continue using definition.entity for edge operations (WRONG)
```

### After (Fixed):
```
User selects edge → definition.entity
↓
Find jointEntity from definition.entity (getSMDefinitionEntities)
↓
✓ Use jointEntity for all subsequent edge operations (CORRECT)
```

## Why This Matters

Sheet metal features work with three representations:
1. **User Selection**: Could be 3D, flat, or part geometry
2. **Definition Entity**: Master sheet metal body representation
3. **Part Geometry**: Actual 3D/flat result

The `getSMDefinitionEntities()` function maps from user selection (#1) to definition entity (#2). All sheet metal operations (attributes, measurements, splitting) must use the definition entity.

## Comparison with Modify Joint

Modify Joint doesn't do edge splitting or spacing calculations, so it:
1. Finds `jointEntity`
2. Gets/replaces attributes on `jointEntity`
3. Calls `updateSheetMetalGeometry`

Our feature does MORE:
1. Finds `jointEntity`
2. Measures edge length on `jointEntity`
3. Calculates spacing on `jointEntity`
4. Splits edges of `jointEntity`
5. Assigns attributes to split segments
6. Calls `updateSheetMetalGeometry`

All steps 2-5 MUST use `jointEntity`, not the original user selection.

## Testing Checklist

- [ ] Select a sheet metal edge joint in 3D view
- [ ] Feature should recognize the selection (no "no sheet metal edge" error)
- [ ] evLength should execute successfully (no precondition failure)
- [ ] Spacing calculation should work
- [ ] Edge splitting should occur
- [ ] Bridge and stitch attributes should be assigned
- [ ] Flat pattern should generate correctly

## Files Changed

- `custom-features/sheetMetalStitchCutBend.fs`
  - Line 118: Filter `jointEntity` instead of `definition.entity`
  - Line 129: Use `jointEntity` in error message
  - Line 132: Calculate length on `jointEntity`
  - Line 142: Set `definition.edges = jointEntity` for spacing utilities

## Summary

The feature now correctly:
1. ✅ Maps user selection to definition entity
2. ✅ Uses definition entity for all edge operations
3. ✅ Provides `definition.edges` for spacing utilities
4. ✅ Should recognize sheet metal edges properly
5. ✅ Should execute evLength successfully
