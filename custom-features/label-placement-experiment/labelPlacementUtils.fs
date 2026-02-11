FeatureScript 2878;

// Shared utility functions for label placement features
// Provides common 2D polygon operations and projection helpers

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");

/**
 * Project a 3D point onto a 2D plane coordinate system
 * @param plane : Plane with origin and basis vectors (x, y)
 * @param point3D : 3D point to project
 * @returns 2D vector in the plane's coordinate system (dimensionless)
 */
export function project2DPoint(plane is Plane, point3D is Vector) returns Vector
{
    const relativePoint = point3D - plane.origin;
    const planeY = cross(plane.normal, plane.x);
    const xCoord = dot(relativePoint, plane.x);
    const yCoord = dot(relativePoint, planeY);
    return vector(xCoord, yCoord);
}

/**
 * Unproject a 2D point back to 3D space
 * @param plane : Plane with origin and basis vectors (x, y)
 * @param point2D : 2D point in the plane's coordinate system
 * @returns 3D point in world coordinates
 */
export function unproject2DPoint(plane is Plane, point2D is Vector) returns Vector
{
    const planeY = cross(plane.normal, plane.x);
    return plane.origin + plane.x * point2D[0] + planeY * point2D[1];
}

/**
 * Compute the 2D bounding box of a polygon
 * @param polygon : Array of 2D points (Vector)
 * @returns Map with minX, maxX, minY, maxY
 */
export function computeBoundingBox2D(polygon is array) returns map
{
    if (size(polygon) == 0)
    {
        return {
            "minX" : 0,
            "maxX" : 0,
            "minY" : 0,
            "maxY" : 0
        };
    }
    
    var minX = polygon[0][0];
    var maxX = polygon[0][0];
    var minY = polygon[0][1];
    var maxY = polygon[0][1];
    
    for (var point in polygon)
    {
        minX = min(minX, point[0]);
        maxX = max(maxX, point[0]);
        minY = min(minY, point[1]);
        maxY = max(maxY, point[1]);
    }
    
    return {
        "minX" : minX,
        "maxX" : maxX,
        "minY" : minY,
        "maxY" : maxY
    };
}

/**
 * Sort polygon vertices into a contiguous loop by walking edges
 * Handles unordered vertex collections from qAdjacent
 * @param context : The context
 * @param face : The face query
 * @param vertices : Array of unordered vertices from evaluateQuery
 * @param plane : Plane for 2D projection
 * @returns Array of 2D points in loop order
 */
export function sortPolygonVertices(context is Context, face is Query, vertices is array, plane is Plane) returns array
{
    if (size(vertices) == 0)
    {
        return [];
    }
    
    // Get all edges of the face
    const edges = evaluateQuery(context, qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE));
    
    if (size(edges) == 0)
    {
        // Fallback: return vertices as-is (may not be ordered)
        var result = [];
        for (var vertex in vertices)
        {
            const point3D = evVertexPoint(context, { "vertex" : vertex });
            result = append(result, project2DPoint(plane, point3D));
        }
        return result;
    }
    
    // Build adjacency map: vertex -> adjacent vertices
    var adjacencyMap = {};
    
    for (var edge in edges)
    {
        const edgeVertices = evaluateQuery(context, qAdjacent(edge, AdjacencyType.VERTEX, EntityType.VERTEX));
        if (size(edgeVertices) == 2)
        {
            const v1 = edgeVertices[0];
            const v2 = edgeVertices[1];
            
            if (adjacencyMap[v1] == undefined)
            {
                adjacencyMap[v1] = [];
            }
            if (adjacencyMap[v2] == undefined)
            {
                adjacencyMap[v2] = [];
            }
            
            adjacencyMap[v1] = append(adjacencyMap[v1], v2);
            adjacencyMap[v2] = append(adjacencyMap[v2], v1);
        }
    }
    
    // Walk around the perimeter starting from the first vertex
    var orderedVertices = [];
    var visited = {};
    var current = vertices[0];
    
    while (size(orderedVertices) < size(vertices))
    {
        orderedVertices = append(orderedVertices, current);
        visited[current] = true;
        
        // Find next unvisited adjacent vertex
        const adjacentVertices = adjacencyMap[current];
        var nextVertex = undefined;
        
        if (adjacentVertices != undefined)
        {
            for (var adj in adjacentVertices)
            {
                if (visited[adj] == undefined)
                {
                    nextVertex = adj;
                    break;
                }
            }
        }
        
        if (nextVertex == undefined)
        {
            // No unvisited neighbors, completed the loop or hit a problem
            break;
        }
        
        current = nextVertex;
    }
    
    // Convert ordered vertices to 2D points
    var result = [];
    for (var vertex in orderedVertices)
    {
        const point3D = evVertexPoint(context, { "vertex" : vertex });
        result = append(result, project2DPoint(plane, point3D));
    }
    
    return result;
}

/**
 * Get all edges of a polygon as vertex pairs
 * @param polygon : Array of 2D points forming a closed polygon
 * @returns Array of maps with "p1" and "p2" keys containing edge endpoints
 */
export function getPolygonEdges(polygon is array) returns array
{
    const numVertices = size(polygon);
    var edges = [];
    
    for (var i = 0; i < numVertices; i += 1)
    {
        edges = append(edges, {
            "p1" : polygon[i],
            "p2" : polygon[(i + 1) % numVertices],
            "index" : i
        });
    }
    
    return edges;
}
