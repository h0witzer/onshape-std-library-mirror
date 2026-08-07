# Pattern Spacing Utilities

This directory contains reusable spacing logic utilities for pattern features in FeatureScript.

## Files

### spacingUtils.fs
**Consolidated pattern spacing utilities module** providing spacing calculation utilities for both curve and circular patterns.

#### Curve Pattern Utilities
- `CurvePatternSpacingType` enum (EQUAL, DISTANCE, BESTFIT)
- `curvePatternSpacingPredicate()` - Predicate for UI configuration of curve pattern spacing
- `computeCurvePatternSpacing()` - Function to calculate instance count and spacing for curve patterns

#### Circular Pattern Utilities
- `CircularPatternSpacingType` enum (EQUAL, BESTFIT)
- `circularPatternSpacingPredicate()` - Predicate for UI configuration of circular pattern spacing
- `computeCircularPatternSpacing()` - Function to calculate instance count and spacing for circular patterns

**Note**: The module uses `isFeaturePattern()` internally, but this is not exported as it's already available from the standard library's `patternUtils.fs`.

## Usage

These utilities extract the spacing logic from `curvePatternBestFit.fs` and `circularPatternBestFit.fs` to make it reusable in other features.

### Example: Using Curve Pattern Spacing in a Custom Feature

```featurescript
FeatureScript 2856;
import(path : "onshape/std/common.fs", version : "2856.0");

// Import the consolidated spacing utilities
export import(path : "8ce820287d75ed2e92412d90", version : "a414d4542f7ae1196125cfbe");//spacingUtils.fs

annotation { "Feature Type Name" : "My Custom Pattern Feature" }
export const myCustomPattern = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // ... other preconditions ...
        
        // Use the spacing predicate
        curvePatternSpacingPredicate(definition);
    }
    {
        // ... feature logic ...
        
        // Compute spacing parameters
        definition = computeCurvePatternSpacing(context, id, definition);
        
        // Now definition.instanceCount is set and ready to use
        // ... rest of pattern logic ...
    });
```

### Example: Using Circular Pattern Spacing

```featurescript
// Import the consolidated spacing utilities
export import(path : "8ce820287d75ed2e92412d90", version : "a414d4542f7ae1196125cfbe");//spacingUtils.fs

annotation { "Feature Type Name" : "My Custom Circular Pattern" }
export const myCustomCircularPattern = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Use the spacing predicate
        circularPatternSpacingPredicate(definition);
    }
    {
        // Compute axis line first
        const axis = computePatternAxis(context, definition.axis, ...);
        
        // Compute spacing parameters
        definition = computeCircularPatternSpacing(context, id, definition, axis);
        
        // Pattern logic continues...
    });
```

## Import Path

**Single import for all spacing utilities:**
```featurescript
import(path : "8ce820287d75ed2e92412d90", version : "a414d4542f7ae1196125cfbe");//spacingUtils.fs
```

## Benefits

- **Single import**: One import path for all pattern spacing utilities
- **Reusability**: Spacing logic can be imported and used in any pattern feature
- **Maintainability**: Changes to spacing logic only need to be made in one place
- **Consistency**: All features using these utilities will have consistent spacing behavior
- **Modularity**: Follows the same pattern as ctPoints utilities in this repository

## Related Features

- `curvePatternBestFit.fs` - Uses curve pattern spacing utilities
- `circularPatternBestFit.fs` - Uses circular pattern spacing utilities
- `ctPointsBackend.fs` - Similar pattern for control points utilities

