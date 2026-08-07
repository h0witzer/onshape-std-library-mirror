# opCreateOutline Test Feature Documentation

## Overview

The **Test opCreateOutline** feature (`testOpCreateOutline.fs`) is a comprehensive testing tool for the `opCreateOutline` operation. This feature allows you to test all parameters of `opCreateOutline` with different configurations and settings.

## What is opCreateOutline?

`opCreateOutline` is an Onshape FeatureScript operation that creates a 2D outline by projecting 3D tool bodies or surfaces onto a target face. The operation is useful for creating projected profiles, silhouettes, or outlines of 3D geometry on a plane, cylinder, or extruded surface.

## Feature Parameters

### Required Parameters

#### 1. Tool Bodies and Faces
- **Purpose**: The 3D geometry to project onto the target face
- **Accepts**: 
  - Solid bodies
  - Sheet bodies
  - Individual faces
- **Max Selection**: 100 entities
- **Notes**: If you select faces, they will be automatically extracted into surface bodies

#### 2. Target Face
- **Purpose**: The face whose surface will be used to create the outline
- **Accepts**: 
  - Planar faces (planes)
  - Cylindrical faces
  - Extruded surfaces
- **Max Selection**: 1 face
- **Important**: Only these three surface types are supported by opCreateOutline

### Optional Parameters

#### 3. Use Offset Faces Parameter
- **Purpose**: Toggle to enable/disable the offsetFaces parameter
- **Type**: Boolean checkbox
- **Default**: False (unchecked)

#### 4. Offset Faces (Conditional)
- **Purpose**: Faces in tools which are offsets of the target face
- **Accepts**: Faces from the tool bodies
- **Max Selection**: 100 faces
- **Visibility**: Only appears when "Use offset faces parameter" is checked
- **Notes**: 
  - For non-planar targets, exactly two offset faces per tool are required
  - This parameter helps opCreateOutline optimize the projection for offset geometries

#### 5. Show Debug Information
- **Purpose**: Enable detailed console logging for debugging
- **Type**: Boolean checkbox
- **Default**: False
- **Output**: Prints detailed information including:
  - Tool face/body counts
  - Target face geometry type
  - Whether offsetFaces parameter is being used
  - Created entity counts (bodies, faces, edges)
  - Error messages if the operation fails

#### 6. Keep Intermediate Bodies
- **Purpose**: Preserve temporary bodies created during face extraction
- **Type**: Boolean checkbox
- **Default**: False
- **Notes**: Useful for debugging - shows the intermediate surface bodies created from face selections

## Usage Examples

### Example 1: Basic Planar Projection

**Goal**: Project a 3D solid body onto a plane to create a 2D outline

1. Select your 3D body/bodies in "Tool bodies and faces"
2. Select a planar face as "Target face"
3. Leave "Use offset faces parameter" unchecked
4. Optionally enable "Show debug information" to see console output
5. Run the feature

**Result**: A 2D outline sketch/face on the target plane showing the projection of your 3D geometry

### Example 2: Cylindrical Projection

**Goal**: Project geometry onto a cylinder surface

1. Select tool bodies to project
2. Select a cylindrical face as "Target face"
3. Leave "Use offset faces parameter" unchecked
4. Run the feature

**Result**: A curved outline on the cylindrical surface

### Example 3: Using Offset Faces

**Goal**: Optimize projection for bodies that have faces parallel/offset from target

1. Select your tool bodies
2. Select the target face (plane, cylinder, or extruded surface)
3. Check "Use offset faces parameter"
4. Select the faces from your tool bodies that are offsets of the target
   - For planar targets: Select parallel faces
   - For non-planar targets: You must select exactly 2 offset faces per tool body
5. Run the feature

**Result**: An optimized outline that takes advantage of the offset face information

### Example 4: Projecting Multiple Faces

**Goal**: Project individual faces instead of entire bodies

1. Select individual faces in "Tool bodies and faces"
2. Select your target face
3. Enable "Keep intermediate bodies" to see the extracted surfaces
4. Enable "Show debug information" to track the conversion process
5. Run the feature

**Result**: Projected outline with visible intermediate surface bodies (if keeping them)

## Debug Output Examples

When "Show debug information" is enabled, you'll see output like:

```
=== opCreateOutline Test Feature START ===
Processing 2 tool faces
Extracting surfaces from selected faces...
Extracted 2 surface bodies from faces
Target face count: 1
Target surface type: PLANE
Tool bodies count: 2
offsetFaces parameter NOT used
Executing opCreateOutline...
opCreateOutline executed successfully
Created entities:
  Bodies: 1
  Faces: 3
  Edges: 5
Cleaning up 2 intermediate bodies
=== opCreateOutline Test Feature END ===
```

## Testing Different Scenarios

This test feature is designed to help you explore the behavior of opCreateOutline in various contexts:

### Test Case 1: Different Target Surface Types
- Test with planar faces
- Test with cylindrical faces
- Test with extruded surfaces
- Observe differences in behavior and output

### Test Case 2: With and Without Offset Faces
- Run the same projection twice
- First without offsetFaces parameter
- Then with offsetFaces parameter
- Compare performance and results

### Test Case 3: Multiple vs. Single Tools
- Project a single tool body
- Project multiple tool bodies
- Observe how the operation handles multiple inputs

### Test Case 4: Face Selection vs. Body Selection
- Select entire bodies as tools
- Select individual faces as tools
- Enable intermediate body visualization
- See how face extraction works

## Common Issues and Troubleshooting

### Error: "No target face selected"
- **Cause**: Target face parameter is empty
- **Solution**: Select a face for the target parameter

### Error: "No tool bodies selected"
- **Cause**: Tool bodies and faces parameter is empty
- **Solution**: Select at least one body or face to project

### Error: Operation fails with offsetFaces
- **Cause**: For non-planar targets, you need exactly 2 offset faces per tool
- **Solution**: Either disable offsetFaces parameter or select the correct number of offset faces

### Error: "Surface type not supported"
- **Cause**: Target face is not a plane, cylinder, or extruded surface
- **Solution**: Select a different target face with a supported geometry type

## Advanced Tips

1. **Visual Debugging**: When testing, enable "Show debug information" and "Keep intermediate bodies" together to see both console output and visual results

2. **Green Highlighting**: With debug enabled, successfully created faces are highlighted in green

3. **Incremental Testing**: Start simple (single body, planar target, no offset faces) and gradually add complexity

4. **Performance Testing**: Use the debug output to compare operation times and entity counts with different parameter combinations

## File Location

This feature is located at:
```
/custom-features/testOpCreateOutline.fs
```

## Version Information

- **FeatureScript Version**: 2837
- **Feature Type Name**: "Test opCreateOutline"
- **Standard Library Imports**:
  - common.fs
  - query.fs
  - evaluate.fs
  - geomOperations.fs
  - topologyUtils.fs
  - debug.fs
  - feature.fs

## Related Operations

- `opExtractSurface`: Used internally to convert face selections to surface bodies
- `opDeleteBodies`: Used for cleanup of intermediate bodies
- `qCreatedBy`: Used to query entities created by the outline operation

## Support and Feedback

This is a test/development feature designed for exploring opCreateOutline functionality. Use it to understand the operation's capabilities and limitations before implementing it in production features.
