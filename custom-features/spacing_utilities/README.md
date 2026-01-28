# Pattern Spacing Utilities

This directory contains reusable spacing logic utilities for pattern features in FeatureScript.

## Files

### curvePatternSpacingUtils.fs
Provides spacing calculation utilities for curve patterns, including:
- `CurvePatternSpacingType` enum (EQUAL, DISTANCE, BESTFIT)
- `curvePatternSpacingPredicate()` - Predicate for UI configuration of curve pattern spacing
- `computeCurvePatternSpacing()` - Function to calculate instance count and spacing for curve patterns

### circularPatternSpacingUtils.fs
Provides spacing calculation utilities for circular patterns, including:
- `CircularPatternSpacingType` enum (EQUAL, BESTFIT)
- `circularPatternSpacingPredicate()` - Predicate for UI configuration of circular pattern spacing
- `computeCircularPatternSpacing()` - Function to calculate instance count and spacing for circular patterns

## Usage

These utilities extract the spacing logic from `curvePatternBestFit.fs` and `circularPatternBestFit.fs` to make it reusable in other features.

### Example: Using Curve Pattern Spacing in a Custom Feature

```featurescript
FeatureScript 2856;
import(path : "onshape/std/common.fs", version : "2856.0");

// Import the spacing utilities
// Replace with actual document ID when published to Onshape
export import(path : "CURVE_PATTERN_SPACING_UTILS_DOC_ID", version : "VERSION");

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

## Deployment to Onshape

When deploying these utilities to Onshape:

1. Publish `curvePatternSpacingUtils.fs` as an Onshape FeatureScript document
2. Publish `circularPatternSpacingUtils.fs` as an Onshape FeatureScript document
3. Update the import statements in `curvePatternBestFit.fs` and `circularPatternBestFit.fs` with the actual document IDs and versions
4. Update any other custom features that want to use these utilities with the proper document IDs

## Benefits

- **Reusability**: Spacing logic can be imported and used in any pattern feature
- **Maintainability**: Changes to spacing logic only need to be made in one place
- **Consistency**: All features using these utilities will have consistent spacing behavior
- **Modularity**: Follows the same pattern as ctPoints utilities in this repository

## Related Features

- `curvePatternBestFit.fs` - Uses curve pattern spacing utilities
- `circularPatternBestFit.fs` - Uses circular pattern spacing utilities
- `ctPointsBackend.fs` - Similar pattern for control points utilities
