FeatureScript 2878;

// Ear Clipping Label Placement
// Implements the ear clipping algorithm from the markdown document
// Works with ALL face types by sampling edges (polygonal, splines, circles, arcs)

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
    "Feature Type Description" : "Places a mate connector using ear clipping triangle analysis",
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
        verifyNonemptyQuery(context, definition, "face", "Select a planar face");
        
        const face = definition.face;
        
        // Get the plane the face lies on (simpler than evFaceTangentPlane for planar faces)
        const plane = evPlane(context, {"face" : face});
        
        // Sample edges to create polygon (works for all edge types)
        const edges = evaluateQuery(context, qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE));
        var polygon2D = [];
        
        for (var edge in edges)
        {
            // Sample edge at multiple points to capture curvature
            const numSamples = 4;
            var parameters = [];
            for (var i = 0; i < numSamples; i += 1)
            {
                parameters = append(parameters, i / numSamples);
            }
            
            const tangentLines = evEdgeTangentLines(context, {
                "edge" : edge,
                "parameters" : parameters,
                "arcLengthParameterization" : true
            });
            
            for (var line in tangentLines)
            {
                const point2D = project2DPoint(plane, line.origin);
                polygon2D = append(polygon2D, point2D);
            }
        }
        
        if (size(polygon2D) < 3)
        {
            throw regenError("Could not sample enough points from face edges");
        }
        
        // Find ear triangle
        const ear = findEarTriangle(polygon2D);
        
        if (ear == undefined)
        {
            throw regenError("Could not find ear triangle in polygon");
        }
        
        // Calculate incenter
        const incenter2D = calculateTriangleIncenter(ear.p1, ear.p2, ear.p3);
        
        // Convert back to 3D
        const placement3D = unproject2DPoint(plane, incenter2D);
        
        // Create coordinate system (X-axis along first ear edge)
        // Unproject edge endpoints to get 3D direction
        const edge1_3D = unproject2DPoint(plane, ear.p1);
        const edge2_3D = unproject2DPoint(plane, ear.p2);
        const edgeDir3D = edge2_3D - edge1_3D;
        
        const placementCsys = coordSystem(placement3D, edgeDir3D, plane.normal);
        
        opMateConnector(context, id + "mateConnector", {
            "coordSystem" : placementCsys,
            "owner" : qNothing()
        });
    });

// Find an ear triangle
function findEarTriangle(polygon is array) returns map
{
    const n = size(polygon);
    
    for (var i = 0; i < n; i += 1)
    {
        const p1 = polygon[i];
        const p2 = polygon[(i + 1) % n];
        const p3 = polygon[(i + 2) % n];
        
        // Check if convex angle at p2
        if (!isConvexAngle(p1, p2, p3))
        {
            continue;
        }
        
        // Check no other vertices inside triangle
        var hasInterior = false;
        for (var j = 0; j < n; j += 1)
        {
            if (j == i || j == (i + 1) % n || j == (i + 2) % n)
            {
                continue;
            }
            
            if (isPointInTriangle(polygon[j], p1, p2, p3))
            {
                hasInterior = true;
                break;
            }
        }
        
        if (!hasInterior)
        {
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

// Check if angle at p2 is convex
function isConvexAngle(p1 is Vector, p2 is Vector, p3 is Vector) returns boolean
{
    const v1 = p1 - p2;
    const v2 = p3 - p2;
    const crossProduct = v1[0] * v2[1] - v1[1] * v2[0];
    return crossProduct > 0;
}

// Check if point is inside triangle (barycentric)
function isPointInTriangle(p is Vector, a is Vector, b is Vector, c is Vector) returns boolean
{
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
    
    return (u >= 0) && (v >= 0) && (u + v <= 1);
}

// Calculate triangle incenter
function calculateTriangleIncenter(p1 is Vector, p2 is Vector, p3 is Vector) returns Vector
{
    const a = norm(p2 - p3);
    const b = norm(p1 - p3);
    const c = norm(p1 - p2);
    
    const perimeter = a + b + c;
    
    if (perimeter < TOLERANCE.zeroLength)
    {
        return (p1 + p2 + p3) / 3;
    }
    
    return (a * p1 + b * p2 + c * p3) / perimeter;
}
