# Testing Guide for Surface Normal Features

This guide explains how to test the flip surface normals and debug surface normal features in Onshape.

## Prerequisites

Since these are FeatureScript custom features, they need to be imported into an Onshape Part Studio to be tested. These scripts cannot be executed in this local environment as they require the Onshape CAD platform.

## Testing Procedure

### 1. Import the Features into Onshape

1. Copy the contents of `flipSurfaceNormals.fs` and `debugSurfaceNormal.fs`
2. In Onshape, create a new FeatureScript document for each feature
3. Paste the code into the FeatureScript editor
4. Click the checkmark to compile the code

### 2. Basic Test: Planar Surface

**Create a test surface:**
1. Create a new Part Studio
2. Create a sketch on the Top plane
3. Draw a rectangle (e.g., 10 x 10 inches)
4. Exit the sketch
5. Use "Extrude" to create a surface (select "New" and "Surface" in extrude options)
6. Set the extrude depth to 0 inches to create a flat surface

**Test the debug feature:**
1. Add the "Debug Surface Normal" custom feature
2. Select the created surface
3. Set the normal length to 2 inches
4. Choose a debug color (e.g., RED)
5. Observe the arrow pointing perpendicular to the surface at its center
6. Note the direction - it should point upward (in +Z direction for Top plane)

**Test the flip feature:**
1. Add the "Flip Surface Normals" custom feature
2. Select the same surface
3. Execute the feature
4. Verify success message appears

**Validate the flip:**
1. Edit the "Debug Surface Normal" feature (or add a new one)
2. Select the flipped surface
3. Observe the arrow now points in the opposite direction (downward, -Z direction)

### 3. Advanced Test: Curved Surface

**Create a curved test surface:**
1. Create a new sketch on the Top plane
2. Draw a circle (e.g., 5 inch radius)
3. Exit the sketch
4. Use "Revolve" to create a hemisphere surface
   - Select "New" and "Surface" in revolve options
   - Revolve 90 degrees to create a dome
   - Select the vertical axis as the revolve axis

**Test with curved surface:**
1. Add "Debug Surface Normal" feature
2. Select the curved surface
3. Set normal length to 2 inches
4. The arrow should point outward from the dome at the center

**Flip the curved surface:**
1. Add "Flip Surface Normals" feature
2. Select the curved surface
3. Execute the feature

**Validate curved flip:**
1. Add another "Debug Surface Normal" feature
2. Select the flipped curved surface
3. The arrow should now point inward (into the dome)

### 4. Multiple Surface Test

**Create multiple surfaces:**
1. Create several different surfaces:
   - A planar rectangular surface
   - A cylindrical surface
   - A conical surface

**Test batch operations:**
1. Use "Debug Surface Normal" to see all normals at once
2. Select all surfaces in one feature call
3. Observe multiple arrows displayed

**Flip multiple surfaces:**
1. Add "Flip Surface Normals" feature
2. Select all surfaces at once
3. Verify all surfaces are flipped successfully

**Validate batch flip:**
1. Use "Debug Surface Normal" again
2. All arrows should now point in opposite directions

## Expected Results

### Debug Surface Normal Feature

**Success indicators:**
- Arrow displays at the center of the surface
- Arrow points perpendicular to the surface
- Point displays at the arrow origin
- Feature info reports correct number of surfaces
- Debug visualizations only visible during feature edit

**Failure cases:**
- Warning message for invalid surfaces (meshes, etc.)
- Graceful handling of surfaces where normal cannot be evaluated

### Flip Surface Normals Feature

**Success indicators:**
- Success message reports number of flipped surfaces
- Surface appears unchanged geometrically
- Normal direction reversed (validated by debug feature)
- Works with planar, cylindrical, conical, and freeform surfaces

**Failure cases:**
- Warning messages for surfaces that cannot be flipped
- Feature continues with other surfaces if one fails
- Error only if ALL surfaces fail

## Validation Workflow

The recommended workflow to validate the flip operation:

1. **Before flip:**
   ```
   Surface → Debug Surface Normal (note direction) → [Arrow points direction A]
   ```

2. **Perform flip:**
   ```
   Surface → Flip Surface Normals → [Success message]
   ```

3. **After flip:**
   ```
   Flipped Surface → Debug Surface Normal → [Arrow points direction -A]
   ```

## Known Limitations

1. Debug arrows are only visible when editing the debug feature
2. Approximation may be used for complex surfaces
3. Periodic surfaces may have special handling requirements
4. Surface UV parameterization is reversed in the V direction

## Troubleshooting

**"Could not evaluate normal" warning:**
- Surface may be invalid or degenerate
- Try with a simpler surface
- Check that surface is not a mesh

**"Failed to flip surface" warning:**
- Surface may not be compatible with B-spline approximation
- Try with a different surface type
- Check that surface is a valid face on a sheet body

**No debug arrow visible:**
- Ensure you are editing the debug feature (dialog is open)
- Check that the normal length is appropriate for your part scale
- Verify the debug color is visible against your background

## Code Verification

Both features follow FeatureScript best practices:

✓ Clear feature names and descriptions
✓ Appropriate parameter bounds (NONNEGATIVE_LENGTH_BOUNDS)
✓ Proper error handling with try-catch blocks
✓ Informative success and error messages
✓ Support for multiple surface selection
✓ Comments explaining the logic
✓ Proper cleanup of temporary geometry

