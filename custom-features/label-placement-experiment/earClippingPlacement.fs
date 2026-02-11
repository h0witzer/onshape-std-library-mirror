FeatureScript 2878;

// Ear Clipping Label Placement
// Implements the fast ear clipping algorithm for label placement
// described in "Planar Face Label Placement Strategies.md"
// Places a mate connector at the incenter of the first detected ear triangle

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/coordSystem.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/mateConnector.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");
import(path : "1470642f04a4ab4b999322bb", version : "44894d58ba1e712a741fef9d");  // Shared utilities

annotation {
    "Feature Type Name" : "Ear Clipping Placement",
    "Feature Type Description" : "Places a mate connector at a guaranteed interior point using the ear clipping algorithm - fast but not optimized for width",
    "Feature Name Template" : "Ear Clipping"
}
export const earClippingPlacement = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
            "Name" : "Planar face",
            "Filter" : EntityType.FACE && GeometryType.PLANE,
            "MaxNumberOfPicks" : 1
        }
        definition.face is Query;
    }
    {
        // Verify face is selected
        verifyNonemptyQuery(context, definition, "face", "Select a planar face");
        
        const face = definition.face;
        
        // Get the tangent plane for 2D projection
        const tangentPlane = evFaceTangentPlane(context, {
            "face" : face,
            "parameter" : vector(0.5, 0.5)
        });
        
        // Get all vertices of the face boundary
        const vertices = evaluateQuery(context, qAdjacent(face, AdjacencyType.VERTEX, EntityType.VERTEX));
        
        if (size(vertices) < 3)
        {
            throw regenError("Face must have at least 3 vertices");
        }
        
        // Sort vertices into a contiguous loop and project to 2D
        const polygon2D = sortPolygonVertices(context, face, vertices, tangentPlane);
        
        if (size(polygon2D) < 3)
        {
            throw regenError("Could not construct valid polygon from face");
        }
        
        // Find an ear triangle
        const ear = findEarTriangle(polygon2D);
        
        if (ear == undefined)
        {
            throw regenError("Could not find ear triangle. Face may be degenerate or have complex topology.");
        }
        
        // Calculate incenter of the ear triangle
        const incenter2D = calculateTriangleIncenter(ear.p1, ear.p2, ear.p3);
        
        // Convert back to 3D
        const placement3D = unproject2DPoint(tangentPlane, incenter2D);
        
        // Create coordinate system for mate connector
        // Z-axis is face normal, X-axis aligned with one edge of the ear
        const edgeDirection2D = normalize(ear.p2 - ear.p1);
        const planeY = cross(tangentPlane.normal, tangentPlane.x);
        const edgeDirection3D = tangentPlane.x * edgeDirection2D[0] + planeY * edgeDirection2D[1];
        
        const placementCsys = coordSystem(placement3D, edgeDirection3D, tangentPlane.normal);
        
        // Create the mate connector
        opMateConnector(context, id + "mateConnector", {
            "coordSystem" : placementCsys,
            "owner" : qNothing()
        });
    });

// Find an ear triangle in the polygon
// An ear is a triangle formed by three consecutive vertices where:
// 1. The angle at the middle vertex is convex
// 2. No other vertex lies inside the triangle
function findEarTriangle(polygon is array) returns map
{
    const numVertices = size(polygon);
    
    if (numVertices < 3)
    {
        return undefined;
    }
    
    // Try each consecutive triplet
    for (var i = 0; i < numVertices; i += 1)
    {
        const p1 = polygon[i];
        const p2 = polygon[(i + 1) % numVertices];
        const p3 = polygon[(i + 2) % numVertices];
        
        // Check if angle at p2 is convex
        if (!isConvexAngle(p1, p2, p3))
        {
            continue;
        }
        
        // Check if any other vertex is inside the triangle
        var hasInteriorVertex = false;
        for (var j = 0; j < numVertices; j += 1)
        {
            // Skip the three vertices forming the triangle
            if (j == i || j == (i + 1) % numVertices || j == (i + 2) % numVertices)
            {
                continue;
            }
            
            const testPoint = polygon[j];
            if (isPointInTriangle(testPoint, p1, p2, p3))
            {
                hasInteriorVertex = true;
                break;
            }
        }
        
        if (!hasInteriorVertex)
        {
            // Found an ear!
            return {
                "p1" : p1,
                "p2" : p2,
                "p3" : p3,
                "index" : i
            };
        }
    }
    
    return undefined;
}

// Check if angle at p2 is convex (using cross product)
function isConvexAngle(p1 is Vector, p2 is Vector, p3 is Vector) returns boolean
{
    const v1 = p1 - p2;
    const v2 = p3 - p2;
    
    // Cross product in 2D: v1.x * v2.y - v1.y * v2.x
    const crossProduct = v1[0] * v2[1] - v1[1] * v2[0];
    
    // Positive cross product means counter-clockwise (convex in standard orientation)
    return crossProduct > 0;
}

// Check if a point is inside a triangle using barycentric coordinates
function isPointInTriangle(p is Vector, a is Vector, b is Vector, c is Vector) returns boolean
{
    // Calculate barycentric coordinates
    const v0 = c - a;
    const v1 = b - a;
    const v2 = p - a;
    
    const dot00 = dot(v0, v0);
    const dot01 = dot(v0, v1);
    const dot02 = dot(v0, v2);
    const dot11 = dot(v1, v1);
    const dot12 = dot(v1, v2);
    
    const invDenom = 1 / (dot00 * dot11 - dot01 * dot01);
    const u = (dot11 * dot02 - dot01 * dot12) * invDenom;
    const v = (dot00 * dot12 - dot01 * dot02) * invDenom;
    
    // Check if point is in triangle
    return (u >= 0) && (v >= 0) && (u + v <= 1);
}

// Calculate the incenter of a triangle
// The incenter is the center of the inscribed circle, equidistant from all sides
function calculateTriangleIncenter(p1 is Vector, p2 is Vector, p3 is Vector) returns Vector
{
    // Calculate side lengths (norm of dimensionless vectors gives dimensionless result)
    const a = norm(p2 - p3);  // Side opposite to p1
    const b = norm(p1 - p3);  // Side opposite to p2
    const c = norm(p1 - p2);  // Side opposite to p3
    
    const perimeter = a + b + c;
    
    // Use dimensionless tolerance since we're in 2D projected space with dimensionless coordinates
    if (perimeter < TOLERANCE.zeroLength)
    {
        // Degenerate triangle, return centroid
        return (p1 + p2 + p3) / 3;
    }
    
    // Incenter formula: weighted average by opposite side lengths
    const incenter = (a * p1 + b * p2 + c * p3) / perimeter;
    
    return incenter;
}
