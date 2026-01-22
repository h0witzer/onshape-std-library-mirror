# Largest Inscribed Circle Utility

This utility provides functions for finding and drawing the largest inscribed circle on a planar face in Onshape FeatureScript.

## Overview

The largest inscribed circle (also known as the maximum inscribed circle) is the largest circle that can fit inside a planar polygon without crossing its boundaries. The center of this circle is known as the Chebyshev center - the point that is farthest from all edges of the polygon.

This implementation uses **binary search with inward offset operations** to efficiently find the maximum inscribed circle, leveraging Onshape's native geometric operations for optimal performance.

## Functions

### `evLargestInscribedCircle(context, definition)`

Evaluation function that calculates the center, radius, and plane of the largest inscribed circle.

**Parameters:**
- `context` (Context): The context in which to evaluate
- `definition` (map):
  - `face` (Query): A query that resolves to a single planar face
  - `options` (LargestInscribedCircleOptions, optional): Configuration options

**Returns:** `LargestInscribedCircleResult` containing:
- `center` (Vector): 3D world position of the circle center
- `radius` (ValueWithUnits): Radius of the largest inscribed circle  
- `plane` (Plane): The plane on which the circle lies

**Example:**
```featurescript
var result = evLargestInscribedCircle(context, { 
    "face" : qNthElement(qEverything(EntityType.FACE), 0) 
});
println("Center: " ~ result.center);
println("Radius: " ~ result.radius);
```

### `opLargestInscribedCircle(context, id, definition)`

Operation function that creates a sketch circle representing the largest inscribed circle.

**Parameters:**
- `context` (Context): The context in which to create the circle
- `id` (Id): The id for the operation
- `definition` (map):
  - `face` (Query): A query that resolves to a single planar face
  - `options` (LargestInscribedCircleOptions, optional): Configuration options

**Example:**
```featurescript
opLargestInscribedCircle(context, id + "inscribedCircle", { 
    "face" : myFaceQuery 
});
```

## Configuration Options

The `LargestInscribedCircleOptions` type allows customization of the algorithm:

- `maxIterations` (number): Maximum number of binary search iterations. Default: 20
- `tolerance` (ValueWithUnits): Convergence tolerance for binary search. Default: 1e-6 meter

**Example with custom options:**
```featurescript
var result = evLargestInscribedCircle(context, { 
    "face" : myFaceQuery,
    "options" : {
        "maxIterations" : 25,
        "tolerance" : 1e-7 * meter
    } as LargestInscribedCircleOptions
});
```

## Algorithm

The implementation uses **binary search with inward offset operations** for optimal performance:

1. Extracts the edges of the planar face
2. Estimates maximum possible radius using bounding box diagonal
3. Performs binary search on offset distance:
   - For each test offset, attempts to create inward-offset wire using `opOffsetWire`
   - If offset succeeds, uses centroid of offset region as circle center
   - Adjusts search bounds based on success/failure
4. Converges to maximum offset distance where edges still form valid region
5. Returns the center and radius of the largest inscribed circle

This approach **leverages Onshape's native geometric operations** (`opOffsetWire`, `evApproximateCentroid`) rather than grid sampling, providing significantly better performance - typically **O(log n)** iterations where n is the precision requirement, rather than **O(m²)** for grid-based approaches where m is the grid resolution.

## Performance Considerations

- Default settings (20 iterations, 1e-6m tolerance) work excellently for most faces
- Binary search converges logarithmically, making it extremely fast
- No expensive grid sampling or point-in-polygon tests
- Leverages hardware-accelerated geometric operations in Onshape
- Performance scales with face complexity much better than grid-based methods

## Use Cases

- Optimal placement of circular features within irregular boundaries
- Maximizing drill hole or boss diameter within a planar region
- Finding the visual center of irregular planar shapes
- Automated layout and packing operations
