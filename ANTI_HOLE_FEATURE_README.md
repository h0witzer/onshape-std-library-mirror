# Anti-Hole Feature

## Overview

The Anti-Hole feature (`antiHole.fs`) creates additive geometry in the shape of holes instead of cutting subtractive holes. It provides identical hole-specification functionality to the standard Hole feature, but generates solid bodies that can either be created as new separate bodies or added to existing bodies via boolean union.

## Key Differences from Standard Hole Feature

### Generation Types

The Anti-Hole feature offers two generation options:

1. **NEW_BODY**: Creates anti-hole geometry as new separate solid bodies
   - Each anti-hole location generates an independent solid body
   - Bodies are not merged with any existing geometry
   - Useful for creating dowel pins, studs, or other cylindrical features

2. **ADD**: Adds anti-hole geometry to existing bodies via boolean union
   - Requires selection of target bodies to merge with
   - Anti-hole geometry is united with selected bodies
   - Useful for adding material in hole-shaped patterns

### What Was Removed

- **No Subtractive Operations**: All boolean subtraction operations have been removed
- **No Sheet Metal Support**: The feature uses `defineFeature` instead of `defineSheetMetalFeature`
- **Simplified Scope Handling**: Scope is only required when using ADD generation type

## Feature Definition

### Preconditions

- **Generation Type** (AntiHoleGenerationType): NEW_BODY or ADD
- **Locations**: Sketch points or mate connectors defining anti-hole positions
- **Bodies to Merge With** (scope): Required only for ADD generation type
- All standard hole parameters (diameter, depth, style, etc.)

### Supported Hole Styles

- Simple holes
- Counterbore (C_BORE)
- Countersink (C_SINK)

### Supported End Styles

- Blind
- Through all
- Up to next
- Up to entity
- Blind in last (legacy)

## Implementation Details

### Main Functions

- `antiHole`: Main feature function exported for use
- `produceAntiHoles`: Orchestrates anti-hole creation
- `produceAntiHolesUsingOpHole`: Creates geometry using opHole operation
- `antiHoleEditLogic`: Handles feature parameter editing and validation

### How It Works

1. **Profile Generation**: Uses the same profile generation as standard holes
   - Calls `buildOpHoleDefinitionAndCallOpHole` with `subtractFromTargets: false`
   - Creates solid bodies in the shape of hole profiles

2. **Geometry Handling**:
   - NEW_BODY mode: Leaves opHole-created solids as independent bodies
   - ADD mode: Performs boolean union with selected target bodies

3. **Attribution**: Maintains hole attributes for drawing annotations and feature recognition

## Usage Examples

### Creating Dowel Pin Geometry

```featurescript
antiHole(context, id, {
    "generationType" : AntiHoleGenerationType.NEW_BODY,
    "locations" : sketchPoints,
    "holeDiameter" : 0.25 * inch,
    "holeDepth" : 1.0 * inch,
    "endStyle" : HoleEndStyle.BLIND,
    "style" : HoleStyle.SIMPLE
});
```

### Adding Boss Features to Existing Bodies

```featurescript
antiHole(context, id, {
    "generationType" : AntiHoleGenerationType.ADD,
    "locations" : sketchPoints,
    "scope" : existingBodies,
    "holeDiameter" : 0.5 * inch,
    "holeDepth" : 0.75 * inch,
    "endStyle" : HoleEndStyle.BLIND,
    "style" : HoleStyle.SIMPLE
});
```

## Version Information

- **FeatureScript Version**: 2837
- **Based On**: Onshape Standard Library hole.fs
- **Modified**: Anti-hole implementation with additive operations only

## Notes

- The feature maintains compatibility with standard hole sizing tables (ANSI, ISO)
- Thread specifications are retained for annotation purposes
- Tolerances and dimensions follow the same rules as standard holes
- Feature name templates automatically generated based on hole specifications
