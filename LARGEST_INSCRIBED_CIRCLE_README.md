# Largest Inscribed Circle Utility

This utility provides functions for finding and drawing the largest inscribed circle on a planar face in Onshape FeatureScript.

## Overview

The largest inscribed circle (also known as the maximum inscribed circle) is the largest circle that can fit inside a planar polygon without crossing its boundaries. The center of this circle is known as the Chebyshev center - the point that is farthest from all edges of the polygon.

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

- `gridResolution` (number): Number of grid cells per dimension. Higher values give better accuracy but take longer. Default: 20
- `refinementIterations` (number): Number of refinement passes. Default: 3
- `tolerance` (ValueWithUnits): Distance tolerance for calculations. Default: 1e-7 meter

**Example with custom options:**
```featurescript
var result = evLargestInscribedCircle(context, { 
    "face" : myFaceQuery,
    "options" : {
        "gridResolution" : 30,
        "refinementIterations" : 4,
        "tolerance" : 1e-8 * meter
    } as LargestInscribedCircleOptions
});
```

## Algorithm

The implementation uses a grid-based search algorithm with successive refinement:

1. Computes the bounding box of the planar face
2. Creates a grid of sample points within the bounding box
3. For each point:
   - Verifies the point lies on the face
   - Calculates minimum distance to all edges
   - Tracks the point with maximum minimum-distance
4. Refines the search area around the best point found
5. Repeats refinement iterations for higher accuracy

This approach provides an excellent balance between accuracy and performance, making it suitable for use as a utility function.

## Performance Considerations

- Default settings (20x20 grid, 3 refinements) work well for most faces
- Increase `gridResolution` for complex or highly irregular polygons
- Increase `refinementIterations` for higher precision requirements
- The algorithm scales with the number of edges and grid resolution

## Use Cases

- Optimal placement of circular features within irregular boundaries
- Maximizing drill hole or boss diameter within a planar region
- Finding the visual center of irregular planar shapes
- Automated layout and packing operations
