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

- **Bend Edges**: Edges with BEND joint attributes (on definition body)
- **Rip Edges**: Edges with RIP joint attributes (on definition body)
- **Wall Faces**: Faces with WALL attributes (on definition body)
- **Corner Vertices**: Vertices with CORNER attributes (on definition body)
- **Planar Walls**: Wall faces that are planar surfaces (on definition body)
- **Cylindrical Walls**: Wall faces that are cylindrical surfaces (on definition body)
- **Adjacent Walls**: Wall faces connected by bend edges
- **Boundary Edges**: Non-bend edges on walls
- **Edges by Joint Type**: Filter edges by specific joint types
- **Bends by Bend Type**: Filter bend edges by bend type
- **Joint Faces**: Cylindrical bend faces on solid sheet metal bodies *(NEW)*
- **Joint Faces by Type**: Filter joint faces by joint type (BEND, RIP, TANGENT) *(NEW)*
- **Joint Faces by Style**: Filter joint faces by joint style *(NEW)*

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

### Basic Query Usage (Definition Body Queries)

```featurescript
// Import the libraries (adjust paths as needed for your Onshape document)
import(path : "943642034066bc27de5d166f", version : "69b99f9a9a085a7e6f9298ec"); // sheetMetalQueries.fs
import(path : "fba4b55b04a2fe9dc396f4c4", version : "4833906c31694f269cdc2f75"); // sheetMetalQueriesUtils.fs

// Query all bend edges in a sheet metal part (definition body)
const bendEdges = qSheetMetalBendEdges(context, definition.sheetMetalBody);

// Query all planar wall faces (definition body)
const planarWalls = qSheetMetalPlanarWalls(context, definition.sheetMetalBody);

// Query adjacent walls to a selected wall (definition body)
const adjacentWalls = qSheetMetalAdjacentWalls(context, definition.selectedWall);

// Query boundary edges (non-bend edges)
const boundaryEdges = qSheetMetalBoundaryEdges(context, definition.wallFaces);
```

### Solid Body Queries (NEW - For Joint Faces)

```featurescript
// These queries work when selecting a SOLID sheet metal body
// They return the cylindrical faces that represent bends on the solid

// Query all joint faces (bend cylindrical faces) on a solid sheet metal body
const jointFaces = qSheetMetalJointFaces(context, definition.solidSheetMetalBody);

// Query only BEND type joint faces
const bendJointFaces = qSheetMetalJointFacesByType(context, definition.solidSheetMetalBody, SMJointType.BEND);

// Query only RIP type joint faces
const ripJointFaces = qSheetMetalJointFacesByType(context, definition.solidSheetMetalBody, SMJointType.RIP);

// Query joint faces by style (e.g., EDGE style rips)
const edgeRipFaces = qSheetMetalJointFacesByStyle(context, definition.solidSheetMetalBody, SMJointStyle.EDGE);
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
| `qSheetMetalBendEdges` | context, sheetMetalEntities | Query | All bend edges (definition) |
| `qSheetMetalRipEdges` | context, sheetMetalEntities | Query | All rip edges (definition) |
| `qSheetMetalWallFaces` | context, sheetMetalEntities | Query | All wall faces (definition) |
| `qSheetMetalCornerVertices` | context, sheetMetalEntities | Query | All corner vertices (definition) |
| `qSheetMetalPlanarWalls` | context, sheetMetalEntities | Query | Planar wall faces (definition) |
| `qSheetMetalCylindricalWalls` | context, sheetMetalEntities | Query | Cylindrical wall faces (definition) |
| `qSheetMetalAdjacentWalls` | context, wallFaces | Query | Walls adjacent to input (definition) |
| `qSheetMetalBendsBetweenWalls` | context, wallFaces1, wallFaces2 | Query | Bends connecting two walls |
| `qSheetMetalEdgesByJointType` | context, entities, jointType | Query | Edges with specific joint type |
| `qSheetMetalBendsByType` | context, entities, bendType | Query | Bends with specific bend type |
| `qSheetMetalBoundaryEdges` | context, wallFaces | Query | Non-bend edges on walls |
| `qSheetMetalJointFaces` | context, sheetMetalBody | Query | All joint faces on solid body *(NEW)* |
| `qSheetMetalJointFacesByType` | context, body, jointType | Query | Joint faces by type on solid *(NEW)* |
| `qSheetMetalJointFacesByStyle` | context, body, jointStyle | Query | Joint faces by style on solid *(NEW)* |

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

**Definition vs Solid Bodies**: 
Sheet metal has both a definition body (surface/master body) and solid bodies (3D folded, flat pattern):
- **Definition Body Queries** (most original functions): Query entities on the hidden master surface body that defines the sheet metal. These queries work with edges, faces, and vertices that have sheet metal attributes attached.
- **Solid Body Queries** (NEW functions): Query entities on the visible solid 3D body. These are useful when:
  - You want to select a visible solid sheet metal part
  - You need the cylindrical faces that represent bends on the solid
  - You're working with the actual 3D geometry users can see and select

**NEW: Joint Faces on Solid Bodies**:
- When you select a solid active sheet metal body, the cylindrical faces represent the bends
- `qSheetMetalJointFaces()` returns all these cylindrical bend faces
- `qSheetMetalJointFacesByType()` filters by BEND, RIP, or TANGENT joint types
- `qSheetMetalJointFacesByStyle()` filters by joint style (e.g., EDGE, OVERLAP for rips)

These functions use association attributes to map between solid body faces and definition body edges.

### Common Use Cases

1. **Automated Flange Operations**: Select all boundary edges to add flanges
2. **Bend Analysis**: Query and analyze all bends in a part
3. **Wall Modifications**: Find and modify specific walls based on criteria
4. **Pattern Operations**: Create patterns based on bend locations
5. **Quality Checks**: Verify bend radii, wall counts, etc.
6. **Joint Face Selection** *(NEW)*: Select cylindrical bend faces on solid bodies for operations like:
   - Adding reinforcement ribs at bends
   - Applying different materials or colors to bend regions
   - Measuring bend face areas for manufacturing analysis
   - Filtering specific types of joints (BEND vs RIP) for different operations

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

