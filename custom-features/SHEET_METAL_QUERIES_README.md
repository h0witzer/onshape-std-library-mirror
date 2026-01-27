# Sheet Metal Query Helper Functions

This directory contains custom helper functions for working with sheet metal queries in Onshape FeatureScript. These functions make it easier to select and work with sheet metal entities that are not trivial to access through standard query operations.

## Overview

The sheet metal query helpers consist of three main files:

1. **sheetMetalQueries.fs** - Main query functions library
2. **sheetMetalQueriesUtils.fs** - Supporting utility functions
3. **sheetMetalQueriesTester.fs** - Interactive tester for trying out the functions

## Files Description

### sheetMetalQueries.fs

This is the primary library containing robust query functions for sheet metal entities. It provides functions to query:

- **Bend Edges**: Edges with BEND joint attributes
- **Rip Edges**: Edges with RIP joint attributes  
- **Wall Faces**: Faces with WALL attributes
- **Corner Vertices**: Vertices with CORNER attributes
- **Planar Walls**: Wall faces that are planar surfaces
- **Cylindrical Walls**: Wall faces that are cylindrical surfaces (rolled walls)
- **Adjacent Walls**: Wall faces connected by bend edges
- **Boundary Edges**: Non-bend edges on walls
- **Edges by Joint Type**: Filter edges by specific joint types
- **Bends by Bend Type**: Filter bend edges by bend type

### sheetMetalQueriesUtils.fs

Supporting utility functions for sheet metal operations:

- **hasBendEdges()**: Check if a query contains bend edges
- **hasWallFaces()**: Check if a query contains wall faces
- **countBendEdges()**: Count the number of bend edges
- **countWallFaces()**: Count the number of wall faces
- **getBendRadius()**: Get the radius of a bend edge
- **getBendAngle()**: Get the angle of a bend edge
- **isPlanarWall()**: Check if a face is a planar wall
- **isCylindricalWall()**: Check if a face is a cylindrical wall
- **getAllBendRadii()**: Get all bend radii as an array
- **getSheetMetalThickness()**: Get the thickness of a sheet metal model
- **filterActiveSheetMetal()**: Filter to only active sheet metal entities

### sheetMetalQueriesTester.fs

An interactive feature that lets you test and visualize the query functions:

- Select sheet metal entities to query
- Choose from various query operations
- View results with debug graphics in different colors
- See results printed to the console

## Usage Examples

### Basic Query Usage

```featurescript
// Import the libraries (adjust paths as needed for your Onshape document)
import(path : "sheetMetalQueries.fs", version : "");
import(path : "sheetMetalQueriesUtils.fs", version : "");

// Query all bend edges in a sheet metal part
const bendEdges = qSheetMetalBendEdges(context, definition.sheetMetalBody);

// Query all planar wall faces
const planarWalls = qSheetMetalPlanarWalls(context, definition.sheetMetalBody);

// Query adjacent walls to a selected wall
const adjacentWalls = qSheetMetalAdjacentWalls(context, definition.selectedWall);

// Query boundary edges (non-bend edges)
const boundaryEdges = qSheetMetalBoundaryEdges(context, definition.wallFaces);
```

### Utility Function Usage

```featurescript
// Count bend edges
const bendCount = countBendEdges(context, qOwnerBody(definition.input));

// Get bend radius of a specific bend
const radius = getBendRadius(context, definition.bendEdge);

// Get sheet metal thickness
const thickness = getSheetMetalThickness(context, definition.sheetMetalBody);

// Check if a face is a planar wall
if (isPlanarWall(context, definition.face))
{
    // Do something with the planar wall
}

// Get all bend radii in a part
const allRadii = getAllBendRadii(context, bendEdges);
```

### Using the Tester

1. Insert the "Sheet Metal Query Tester" feature into your part studio
2. Select a sheet metal body, face, or edge
3. Choose the query operation you want to test
4. Enable "Show debug graphics" to visualize results
5. Check the console output for detailed results

## Function Reference

### Query Functions (sheetMetalQueries.fs)

| Function | Parameters | Returns | Description |
|----------|------------|---------|-------------|
| `qSheetMetalBendEdges` | context, sheetMetalEntities | Query | All bend edges |
| `qSheetMetalRipEdges` | context, sheetMetalEntities | Query | All rip edges |
| `qSheetMetalWallFaces` | context, sheetMetalEntities | Query | All wall faces |
| `qSheetMetalCornerVertices` | context, sheetMetalEntities | Query | All corner vertices |
| `qSheetMetalPlanarWalls` | context, sheetMetalEntities | Query | Planar wall faces |
| `qSheetMetalCylindricalWalls` | context, sheetMetalEntities | Query | Cylindrical wall faces |
| `qSheetMetalAdjacentWalls` | context, wallFaces | Query | Walls adjacent to input |
| `qSheetMetalBendsBetweenWalls` | context, wallFaces1, wallFaces2 | Query | Bends connecting two walls |
| `qSheetMetalEdgesByJointType` | context, entities, jointType | Query | Edges with specific joint type |
| `qSheetMetalBendsByType` | context, entities, bendType | Query | Bends with specific bend type |
| `qSheetMetalBoundaryEdges` | context, wallFaces | Query | Non-bend edges on walls |

### Utility Functions (sheetMetalQueriesUtils.fs)

| Function | Parameters | Returns | Description |
|----------|------------|---------|-------------|
| `hasBendEdges` | context, edgeQuery | boolean | Check for bend edges |
| `hasWallFaces` | context, faceQuery | boolean | Check for wall faces |
| `countBendEdges` | context, edgeQuery | number | Count bend edges |
| `countWallFaces` | context, faceQuery | number | Count wall faces |
| `getBendRadius` | context, bendEdge | ValueWithUnits | Get bend radius |
| `getBendAngle` | context, bendEdge | ValueWithUnits | Get bend angle |
| `isPlanarWall` | context, face | boolean | Check if planar wall |
| `isCylindricalWall` | context, face | boolean | Check if cylindrical wall |
| `getAllBendRadii` | context, bendEdges | array | Get all bend radii |
| `getSheetMetalThickness` | context, entity | ValueWithUnits | Get sheet thickness |
| `filterActiveSheetMetal` | context, entityQuery | Query | Filter to active SM only |

## Implementation Notes

### Design Principles

1. **Robust Selection**: Functions handle both definition entities and solid entities automatically
2. **Attribute-Based**: Queries work by examining sheet metal attributes (SMAttribute)
3. **Non-Destructive**: All functions are read-only queries that don't modify geometry
4. **Clear Documentation**: Each function has detailed JSDoc-style comments
5. **Descriptive Naming**: Function names clearly indicate what they query

### Key Concepts

**Sheet Metal Attributes**: Onshape sheet metal uses attributes to track different types of entities:
- `SMObjectType.MODEL` - The sheet metal model itself
- `SMObjectType.WALL` - Flat or rolled wall faces
- `SMObjectType.JOINT` - Edges connecting walls (bends, rips, tangents)
- `SMObjectType.CORNER` - Corner vertices where walls meet

**Definition vs Solid**: Sheet metal has both a definition body (surface) and solid bodies (3D folded, flat pattern). These functions work with either, automatically resolving to definition entities when needed.

### Common Use Cases

1. **Automated Flange Operations**: Select all boundary edges to add flanges
2. **Bend Analysis**: Query and analyze all bends in a part
3. **Wall Modifications**: Find and modify specific walls based on criteria
4. **Pattern Operations**: Create patterns based on bend locations
5. **Quality Checks**: Verify bend radii, wall counts, etc.

## Testing and Validation

The tester feature provides a way to validate the functions work correctly:

1. Create a simple sheet metal part (e.g., a box with bends)
2. Insert the Sheet Metal Query Tester feature
3. Test each operation type to verify correct results
4. Use debug graphics to visually confirm selections
5. Check console output for numeric results

## Limitations and Considerations

- Functions require active sheet metal models
- Some functions evaluate queries internally (performance consideration for large models)
- The tester is designed for demonstration, not production use
- Import paths may need adjustment when used in Onshape documents

## Contributing

When extending these libraries:

1. Follow existing naming conventions (qSheetMetal* prefix for queries)
2. Add clear documentation with parameter descriptions
3. Test with both simple and complex sheet metal parts
4. Consider performance for large sheet metal models
5. Add corresponding test cases to the tester feature

## Version History

- **v1.0** (2026-01-27): Initial release with core query functions

## References

- [Onshape FeatureScript Documentation](https://cad.onshape.com/FsDoc/)
- [Sheet Metal Attributes](https://cad.onshape.com/FsDoc/library.html#module-sheetMetalAttribute.fs)
- [Query Functions](https://cad.onshape.com/FsDoc/library.html#module-query.fs)

