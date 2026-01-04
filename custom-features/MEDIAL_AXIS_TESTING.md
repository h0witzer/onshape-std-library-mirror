# Medial Axis Transform - Testing Guide

## Quick Start

### Basic Test (Rectangle)
1. Create a new Onshape document
2. Create a sketch with a simple rectangle (e.g., 10mm x 20mm)
3. Extrude the sketch to create a face
4. Add the Medial Axis Transform feature
5. Select the rectangular face
6. Set sample density to 50
7. Enable "Show debug info"
8. Run the feature

**Expected Result**: A single line down the center of the rectangle (the medial axis of a rectangle is a line segment)

### Intermediate Test (Circle)
1. Create a sketch with a circle (e.g., 20mm diameter)
2. Extrude to create a face
3. Add the Medial Axis Transform feature
4. Select the circular face
5. Set sample density to 50
6. Run the feature

**Expected Result**: A single point at the center (the medial axis of a circle is its center point)

### Advanced Test (L-Shape)
1. Create a sketch with an L-shaped polygon
2. Extrude to create a face
3. Add the Medial Axis Transform feature
4. Select the L-shaped face
5. Set sample density to 100 (higher for better accuracy)
6. Run the feature

**Expected Result**: Three line segments meeting at a branch point (the medial axis has branches at convex vertices)

## Troubleshooting

### No Medial Axis Generated
**Symptoms**: Feature runs but no curves appear
**Possible Causes**:
- Sample density too low
- Face is not planar
- Collision detection failed

**Solutions**:
1. Increase sample density to 100 or higher
2. Verify face is truly planar using evPlane
3. Check console output for warnings
4. Enable debug info to see if any points are being computed

### Too Few Segments
**Symptoms**: Medial axis looks incomplete or jagged
**Possible Causes**:
- Sample density too low
- Boundary has high curvature regions that need more samples

**Solutions**:
1. Increase sample density significantly (try 200+)
2. Enable adaptive sampling (future feature)
3. Check edge lengths in console output

### Incorrect Normals
**Symptoms**: Medial axis appears outside the face or on the wrong side
**Possible Causes**:
- Face orientation is unexpected
- Normal computation is inverted

**Solutions**:
1. Check debug visualization to see if normals point inward
2. Try flipping face orientation
3. Verify computeInwardNormal logic for your face type

### Collision Detection Issues
**Symptoms**: Warning "No collisions detected"
**Possible Causes**:
- Numerical collision solver failing
- Segments are parallel or diverging
- Tolerances too strict

**Solutions**:
1. Increase sample density to create more segment pairs
2. Check that segments are not perfectly parallel
3. Adjust tolerance in testEndpointSegmentCollision (currently 0.01)
4. Consider implementing analytical quadratic solver

## Debugging Tips

### Console Output Analysis
Look for these key indicators:
```
=== Medial Axis Transform ===
Computing medial axis for planar face...
Step 1: Sampling face boundary...
  - Found N boundary loops         [Should be 1 for simple shapes, 2+ for shapes with holes]
  - Loop 0 has N edges            [Number of edges in boundary]
  - Boundary segments: N          [Total segments created]
Step 2: Computing RLFS function...
  - RLFS pieces computed: N       [Should be > 0; if 0, no collisions detected]
Step 3: Extracting medial axis graph...
  - MA nodes: N                   [Number of medial axis points]
  - MA edges: N                   [Number of medial axis line segments]
Step 4: Drawing medial axis curves...
Drew N medial axis curves
=== Medial Axis Transform Complete ===
```

### Debug Visualization
When "Show debug info" is enabled:
- **Blue points**: MA nodes (centers of maximal discs)
- **Red lines**: MA edges (medial axis segments)
- **Yellow lines**: Not currently used

### Common Patterns
- **Rectangle**: Single horizontal or vertical line
- **Circle**: Single point at center
- **Ellipse**: Single line segment along major axis
- **L-shape**: Three segments meeting at vertex
- **Square with hole**: Outer medial axis (square) plus branch to hole
- **Star shape**: Multiple branches from center to each arm

## Performance Benchmarking

### Expected Performance
- **Simple shapes** (< 100 segments): < 1 second
- **Medium shapes** (100-500 segments): 1-5 seconds
- **Complex shapes** (500-1000 segments): 5-30 seconds
- **Very complex** (> 1000 segments): May be slow due to O(n²) collision testing

### Optimization Opportunities
1. Implement spatial indexing for collision tests (currently O(n²))
2. Implement adaptive sampling based on curvature
3. Use analytical quadratic solver instead of numerical
4. Implement full Find MA/Follow MA algorithm (avoids redundant work)
5. Use parallel processing for independent collision tests

## Known Limitations

### Current Implementation
1. **Simplified Collision Detection**: Uses numerical approximation instead of analytical solution
2. **All-Pairs Testing**: Tests all segment pairs instead of intelligent boundary marching
3. **No Reflex Vertex Handling**: Doesn't split reflex vertices into dummy segments
4. **No Hole Support**: Fix MA routine not implemented for regions with holes
5. **No Adaptive Sampling**: Samples uniformly instead of adapting to curvature
6. **Node Duplication**: Creates separate nodes for each edge instead of sharing

### Future Enhancements
1. Implement analytical collision equation solver
2. Implement Find MA Extreme Points with LMPC detection
3. Implement Follow MA with branch tracking
4. Implement Fix MA for handling holes
5. Add reflex vertex splitting
6. Add curvature-aware adaptive sampling
7. Optimize with spatial data structures
8. Implement proper node sharing in graph extraction

## Test Cases to Implement

### Unit Tests (if test framework available)
1. **Boundary Sampling**
   - Test single edge loop extraction
   - Test multiple loops (with holes)
   - Test edge flipping detection
   - Test normal computation

2. **Collision Detection**
   - Test parallel segments (should not collide)
   - Test converging segments (should collide)
   - Test adjacent segments at convex vertex (should collide)
   - Test adjacent segments at reflex vertex (should not collide immediately)

3. **RLFS Computation**
   - Test simple rectangle (2 adjacent segment pairs)
   - Test triangle (3 adjacent segment pairs)
   - Test collision distances are positive

4. **Graph Extraction**
   - Test peer piece finding
   - Test node creation
   - Test edge connectivity

### Integration Tests
1. Rectangle: Verify single line axis
2. Square: Verify cross pattern (4 branches)
3. Circle: Verify single center point
4. L-shape: Verify 3-segment branching structure
5. Star: Verify radial branch pattern

## Reporting Issues

When reporting issues, please include:
1. Shape description or screenshot
2. Sample density used
3. Console output
4. Screenshot of debug visualization (if enabled)
5. Expected vs actual result

## Contributing Improvements

Priority improvements:
1. **High Priority**: Analytical collision solver (accuracy)
2. **High Priority**: Adaptive sampling (accuracy for curved boundaries)
3. **Medium Priority**: Find MA/Follow MA implementation (performance)
4. **Medium Priority**: Reflex vertex handling (accuracy at concave corners)
5. **Low Priority**: Fix MA for holes (handling complex topologies)

See MEDIAL_AXIS_TRANSFORM.md for detailed algorithm descriptions and implementation notes.
