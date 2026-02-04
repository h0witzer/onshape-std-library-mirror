# K Factor Override Fix

## Problem

User reported that manually overridden K factors were not being applied to bends in the sheet metal stitch cut bend feature. When unchecking "Use model K Factor" and entering a custom value, the bends still used the model default instead of the custom value.

## Root Cause

In the `processJointEntity` function, the code flow was:

1. Create `localDefinition = mergeMaps(definition, { "edges" : jointEntity })` (line 195)
2. Call `localDefinition = computeCurvePatternSpacing(context, id, localDefinition)` (line 199)
3. Pass `localDefinition` to `applyJointAttributesToSegments` (lines 344, 350)

The issue was that `computeCurvePatternSpacing` returns a new map containing only spacing-related fields:
- `instanceCount`
- `useOffsets`
- `offset`, `offset1`, `offset2`
- `twoOffsets`
- `oppositeDirection`
- `spacingType`
- `distance`
- `endMode`
- `bridgeWidth`

This meant that bend-related parameters were lost:
- `kFactor` ❌
- `radius` ❌
- `useDefaultKFactor` ❌
- `useDefaultRadius` ❌

When `applyJointAttributesToSegments` tried to access these fields (lines 523, 529, 532, 537), they were `undefined`, causing the feature to fail to apply manual overrides.

## Solution

Changed lines 344 and 350 in `processJointEntity` to pass the original `definition` parameter instead of `localDefinition`:

```featurescript
// Before (incorrect):
applyJointAttributesToSegments(context, id + "bridges", bridgeSegmentEdges, existingAttribute, 
    SMJointType.BEND, localDefinition, false, true, defaultRadius, defaultKFactor);

// After (correct):
applyJointAttributesToSegments(context, id + "bridges", bridgeSegmentEdges, existingAttribute, 
    SMJointType.BEND, definition, false, true, defaultRadius, defaultKFactor);
```

This ensures that:
- `localDefinition` is still used for spacing calculations (lines 211-250) ✓
- Original `definition` is used for bend attributes, preserving all parameters ✓

## Code Flow After Fix

1. Main function creates `definition` with all precondition fields (lines 50-79)
2. `processJointEntity` receives original `definition` (line 127)
3. Creates `localDefinition` for spacing calculations only (lines 195-199)
4. Uses `localDefinition` for calculating bridge domains (lines 211-250)
5. Passes original `definition` to `applyJointAttributesToSegments` (lines 344, 350)
6. `applyJointAttributesToSegments` correctly accesses:
   - `definition.useDefaultRadius` → boolean (user choice)
   - `definition.radius` → length (user override value)
   - `definition.useDefaultKFactor` → boolean (user choice)
   - `definition.kFactor` → real (user override value)

## Verification

Manual overrides now work correctly:

**Test Case 1: Manual Radius Override**
1. Uncheck "Use model bend radius"
2. Enter custom radius (e.g., 0.1 inch)
3. ✓ Bends use custom radius

**Test Case 2: Manual K Factor Override**
1. Uncheck "Use model K Factor"
2. Enter custom K factor (e.g., 0.5)
3. ✓ Bends use custom K factor

**Test Case 3: Both Overrides**
1. Uncheck both "Use model" options
2. Enter custom radius and K factor
3. ✓ Bends use both custom values

**Test Case 4: Model Defaults**
1. Keep both "Use model" options checked
2. ✓ Bends use model defaults (unchanged behavior)

## Impact

- **Minimal change**: Only 2 lines modified (344, 350)
- **Backward compatible**: Model defaults still work as before
- **Complete fix**: Both radius and K factor overrides now work
- **No side effects**: Spacing calculations still use `localDefinition` correctly
