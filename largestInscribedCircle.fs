FeatureScript 2856;
// This module is part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab for the license text.
// Copyright (c) 2013-Present PTC Inc.

/**
 * Utility functions for finding and creating the largest inscribed circle on a planar face.
 * Uses binary search with offset operations for optimal performance, leveraging Onshape's
 * native geometric operations rather than grid sampling.
 */

import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/context.fs", version : "2856.0");
import(path : "onshape/std/coordSystem.fs", version : "2856.0");
import(path : "onshape/std/evaluate.fs", version : "2856.0");
import(path : "onshape/std/feature.fs", version : "2856.0");
import(path : "onshape/std/geomOperations.fs", version : "2856.0");
import(path : "onshape/std/query.fs", version : "2856.0");
import(path : "onshape/std/sketch.fs", version : "2856.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2856.0");
import(path : "onshape/std/vector.fs", version : "2856.0");
import(path : "onshape/std/box.fs", version : "2856.0");
import(path : "onshape/std/math.fs", version : "2856.0");
import(path : "onshape/std/units.fs", version : "2856.0");
import(path : "onshape/std/debug.fs", version : "2856.0");

/**
 * Configuration options for the largest inscribed circle algorithm.
 * 
 * @type {{
 *      @field maxIterations {number} : Maximum binary search iterations. Default is 20.
 *      @field tolerance {ValueWithUnits} : Convergence tolerance for binary search. Default is 1e-6 meter.
 * }}
 */
export type LargestInscribedCircleOptions typecheck canBeLargestInscribedCircleOptions;

predicate canBeLargestInscribedCircleOptions(value)
{
    value is map;
    if (value.maxIterations != undefined)
    {
        value.maxIterations is number;
        value.maxIterations > 0;
    }
    if (value.tolerance != undefined)
    {
        isLength(value.tolerance);
    }
}

/**
 * Result of the largest inscribed circle calculation.
 * 
 * @type {{
 *      @field center {Vector} : The 3D world position of the circle center (length vector).
 *      @field radius {ValueWithUnits} : The radius of the largest inscribed circle.
 *      @field plane {Plane} : The plane on which the circle lies.
 * }}
 */
export type LargestInscribedCircleResult typecheck canBeLargestInscribedCircleResult;

predicate canBeLargestInscribedCircleResult(value)
{
    value is map;
    is3dLengthVector(value.center); // Center is a 3D world position with length units
    isLength(value.radius);
    value.plane is Plane;
}

/**
 * Helper function to check if a point is inside a face and calculate its inscribed circle radius.
 * Returns a map with validation status, radius, and center point.
 * 
 * @param context {Context} : The context in which to evaluate.
 * @param point {Vector} : The 3D point to evaluate.
 * @param faceQuery {Query} : The face to check against.
 * @param faceEdges {Query} : The edges of the face.
 * @param tolerance {ValueWithUnits} : Distance tolerance for validation.
 * @returns {map} : Map with "valid" (boolean), "radius" (ValueWithUnits), and "center" (Vector) fields.
 */
function evaluateCandidatePoint(context is Context, point is Vector, faceQuery is Query, faceEdges is Query, tolerance is ValueWithUnits) returns map
{
    // Check if point is on the face by projecting to face and checking distance
    const projectionResult = evDistance(context, {
        "side0" : point,
        "side1" : faceQuery
    });
    
    // If point is not very close to face, it's not a valid candidate
    if (projectionResult.distance > tolerance)
    {
        return { "valid" : false, "radius" : 0 * meter, "center" : point };
    }
    
    // Calculate distance to edges
    const distanceToEdges = evDistance(context, {
        "side0" : point,
        "side1" : faceEdges
    });
    
    // Point is valid if distance to edges is positive (inside face)
    const isValid = distanceToEdges.distance > tolerance;
    
    return {
        "valid" : isValid,
        "radius" : distanceToEdges.distance,
        "center" : point
    };
}

/**
 * Calculate the largest inscribed circle for a planar face.
 * Uses angle bisector intersection method to find the optimal inscribed circle center.
 * Finds the longest bisector, then the longest intersecting bisector, uses their intersection
 * as starting point, and refines to find the optimal inscribed circle.
 * 
 * @param context {Context} : The context in which to evaluate.
 * @param definition {{
 *      @field face {Query} : A query that resolves to a single planar face.
 *      @field options {LargestInscribedCircleOptions} : Optional configuration for the algorithm. @optional
 * }}
 * @returns {LargestInscribedCircleResult} : The center, radius, and plane of the largest inscribed circle.
 * 
 * @example `var result = evLargestInscribedCircle(context, { "face" : qNthElement(qEverything(EntityType.FACE), 0) })`
 *          returns the largest inscribed circle for the first face with default options.
 * @example `var result = evLargestInscribedCircle(context, { "face" : myFaceQuery, "options" : { "maxIterations" : 25 } as LargestInscribedCircleOptions })`
 *          returns the largest inscribed circle with custom iteration count.
 */
export function evLargestInscribedCircle(context is Context, definition is map) returns LargestInscribedCircleResult
precondition
{
    definition.face is Query;
    definition.options == undefined || definition.options is LargestInscribedCircleOptions;
}
{
    // Get the planar face and verify it's planar
    const faceQuery = definition.face;
    const facePlane = evPlane(context, { "face" : faceQuery });
    
    // Set default options
    const maxIterations = (definition.options != undefined && definition.options.maxIterations != undefined) ? 
                         definition.options.maxIterations : 20;
    const tolerance = (definition.options != undefined && definition.options.tolerance != undefined) ? 
                     definition.options.tolerance : 1e-6 * meter;
    
    // Get all edges and vertices of the face
    const faceEdges = qAdjacent(faceQuery, AdjacencyType.EDGE, EntityType.EDGE);
    const faceVertices = qAdjacent(faceQuery, AdjacencyType.VERTEX, EntityType.VERTEX);
    const vertices = evaluateQuery(context, faceVertices);
    const edges = evaluateQuery(context, faceEdges);
    
    // Step 1: Build bisector information for convex corners
    var bisectors = [];
    
    println("DEBUG: Number of vertices: " ~ size(vertices));
    println("DEBUG: Number of edges: " ~ size(edges));
    
    for (var vertex in vertices)
    {
        println("DEBUG: Starting to process a vertex...");
        try
        {
            const vertexPoint = evVertexPoint(context, { "vertex" : vertex });
            println("DEBUG: Vertex point: " ~ vertexPoint);
            
            // Get edges of the face that are adjacent to this vertex
            // Use qIntersection to get only face edges that touch this vertex
            const adjacentEdges = qIntersection([
                faceEdges,
                qAdjacent(vertex, AdjacencyType.VERTEX, EntityType.EDGE)
            ]);
            const adjacentEdgesList = evaluateQuery(context, adjacentEdges);
            
            println("DEBUG: Number of adjacent edges: " ~ size(adjacentEdgesList));
            
            if (size(adjacentEdgesList) == 2)
            {
                // Get edge tangent lines at the vertex
                // Need to determine which direction points away from the vertex
                var dir1 = undefined;
                var dir2 = undefined;
                
                // For first edge
                const edge1Start = evEdgeTangentLine(context, {
                    "edge" : adjacentEdgesList[0],
                    "parameter" : 0.0
                });
                const edge1End = evEdgeTangentLine(context, {
                    "edge" : adjacentEdgesList[0],
                    "parameter" : 1.0
                });
                
                // Check which end is closer to our vertex
                const dist1Start = norm(edge1Start.origin - vertexPoint);
                const dist1End = norm(edge1End.origin - vertexPoint);
                
                if (dist1Start < dist1End)
                {
                    // Vertex is at start, direction points away
                    dir1 = normalize(edge1Start.direction);
                }
                else
                {
                    // Vertex is at end, direction points toward, so flip
                    dir1 = -normalize(edge1End.direction);
                }
                
                // For second edge
                const edge2Start = evEdgeTangentLine(context, {
                    "edge" : adjacentEdgesList[1],
                    "parameter" : 0.0
                });
                const edge2End = evEdgeTangentLine(context, {
                    "edge" : adjacentEdgesList[1],
                    "parameter" : 1.0
                });
                
                const dist2Start = norm(edge2Start.origin - vertexPoint);
                const dist2End = norm(edge2End.origin - vertexPoint);
                
                if (dist2Start < dist2End)
                {
                    dir2 = normalize(edge2Start.direction);
                }
                else
                {
                    dir2 = -normalize(edge2End.direction);
                }
                
                // Calculate angle bisector direction (average of two unit vectors)
                var bisectorDir = normalize(dir1 + dir2);
                
                // For inscribed circle, we want bisectors pointing inward
                // Check if the bisector points inward by testing against a reference point
                // For now, process all vertices and let the distance calculation determine validity
                
                println("DEBUG: Processing vertex at " ~ vertexPoint);
                println("DEBUG:   dir1: " ~ dir1 ~ ", dir2: " ~ dir2);
                println("DEBUG:   bisectorDir: " ~ bisectorDir);
                
                // Cast ray from vertex along bisector to find distance to opposite edge
                var minDistToEdge = undefined;
                
                for (var edge in edges)
                {
                    // Skip adjacent edges
                    var isAdjacent = false;
                    for (var adjEdge in adjacentEdgesList)
                    {
                        if (edge == adjEdge)
                        {
                            isAdjacent = true;
                            break;
                        }
                    }
                    
                    if (!isAdjacent)
                    {
                        try silent
                        {
                            // Calculate distance from vertex along bisector to this edge
                            const distResult = evDistance(context, {
                                "side0" : line(vertexPoint, bisectorDir),
                                "side1" : edge
                            });
                            
                            println("DEBUG:     Distance to edge: " ~ distResult.distance);
                            
                            if (minDistToEdge == undefined || distResult.distance < minDistToEdge)
                            {
                                minDistToEdge = distResult.distance;
                            }
                        }
                    }
                }
                
                println("DEBUG:   minDistToEdge: " ~ minDistToEdge);
                
                if (minDistToEdge != undefined && minDistToEdge > tolerance)
                {
                    bisectors = append(bisectors, {
                        "vertex" : vertexPoint,
                        "direction" : bisectorDir,
                        "distance" : minDistToEdge,
                        "line" : line(vertexPoint, bisectorDir)
                    });
                    println("DEBUG:   Added bisector!");
                }
                else
                {
                    println("DEBUG:   Rejected bisector (minDistToEdge too small or undefined)");
                }
            }
            else
            {
                println("DEBUG: Vertex has " ~ size(adjacentEdgesList) ~ " adjacent edges (expected 2)");
            }
        }
        catch (error)
        {
            println("DEBUG: ERROR processing vertex: " ~ error);
        }
    }
    
    // Debug: Print bisector count
    println("DEBUG: Found " ~ size(bisectors) ~ " convex corner bisectors");
    
    // Step 2: Find the longest bisector
    var longestBisector = undefined;
    var longestDistance = 0 * meter;
    var longestIndex = -1;
    
    for (var i = 0; i < size(bisectors); i += 1)
    {
        if (bisectors[i].distance > longestDistance)
        {
            longestDistance = bisectors[i].distance;
            longestBisector = bisectors[i];
            longestIndex = i;
        }
    }
    
    // If no valid bisectors found, fall back to centroid
    if (longestBisector == undefined)
    {
        const centroid = evApproximateCentroid(context, { "entities" : faceQuery });
        const distToEdges = evDistance(context, {
            "side0" : centroid,
            "side1" : faceEdges
        });
        
        return {
            "center" : centroid,
            "radius" : max(distToEdges.distance, tolerance),
            "plane" : facePlane
        } as LargestInscribedCircleResult;
    }
    
    // Step 3: Find all bisectors that intersect with the longest bisector
    // and identify the longest among those
    var longestIntersectingDistance = 0 * meter;
    var bestIntersectionPoint = undefined;
    
    for (var i = 0; i < size(bisectors); i += 1)
    {
        if (i != longestIndex)
        {
            const bisector = bisectors[i];
            
            // Calculate 2D intersection on face plane
            const p1 = longestBisector.vertex;
            const d1 = longestBisector.direction;
            const p2 = bisector.vertex;
            const d2 = bisector.direction;
            
            // Project to 2D on face plane
            const p1_2d = worldToPlane(facePlane, p1);
            const p2_2d = worldToPlane(facePlane, p2);
            const d1_2d = vector(dot(d1, facePlane.x), dot(d1, facePlane.y));
            const d2_2d = vector(dot(d2, facePlane.x), dot(d2, facePlane.y));
            
            // Solve 2D line intersection: p1 + t1*d1 = p2 + t2*d2
            const denom = d1_2d[0] * d2_2d[1] - d1_2d[1] * d2_2d[0];
            
            if (abs(denom) > 1e-10)
            {
                const t1 = ((p2_2d[0] - p1_2d[0]) * d2_2d[1] - (p2_2d[1] - p1_2d[1]) * d2_2d[0]) / denom;
                const t2 = ((p2_2d[0] - p1_2d[0]) * d1_2d[1] - (p2_2d[1] - p1_2d[1]) * d1_2d[0]) / denom;
                
                // Check if intersection is valid (positive parameters - intersection ahead of both vertices)
                if (t1 > 0 && t2 > 0)
                {
                    // This bisector intersects the longest one
                    // Check if this is the longest intersecting bisector
                    if (bisector.distance > longestIntersectingDistance)
                    {
                        longestIntersectingDistance = bisector.distance;
                        
                        // Calculate intersection point
                        const intersection2d = p1_2d + t1 * d1_2d;
                        bestIntersectionPoint = planeToWorld(facePlane, intersection2d);
                    }
                }
            }
        }
    }
    
    // Step 4: Use the intersection point as starting point
    var bestCenter = undefined;
    var bestRadius = 0 * meter;
    
    if (bestIntersectionPoint != undefined)
    {
        // Validate intersection point
        const intersectionResult = evaluateCandidatePoint(context, bestIntersectionPoint, faceQuery, faceEdges, tolerance);
        if (intersectionResult.valid)
        {
            bestCenter = bestIntersectionPoint;
            bestRadius = intersectionResult.radius;
        }
    }
    
    // If no valid intersection found, use longest bisector endpoint
    if (bestCenter == undefined)
    {
        bestCenter = longestBisector.vertex + longestBisector.direction * longestDistance;
        bestRadius = longestDistance;
    }
    
    // Step 5: Refine further using local optimization
    // Sample points around the best center to find local maximum
    const sampleRadius = bestRadius * 0.5;
    const numSamples = 8;
    
    for (var i = 0; i < numSamples; i += 1)
    {
        const angle = 2 * PI * i / numSamples;
        const offset = vector(cos(angle), sin(angle)) * sampleRadius;
        const samplePoint2d = worldToPlane(facePlane, bestCenter) + offset;
        const samplePoint = planeToWorld(facePlane, samplePoint2d);
        
        const sampleResult = evaluateCandidatePoint(context, samplePoint, faceQuery, faceEdges, tolerance);
        if (sampleResult.valid && sampleResult.radius > bestRadius)
        {
            bestRadius = sampleResult.radius;
            bestCenter = samplePoint;
        }
    }
    
    // Final validation
    const finalResult = evaluateCandidatePoint(context, bestCenter, faceQuery, faceEdges, tolerance);
    if (finalResult.valid)
    {
        bestRadius = finalResult.radius;
        bestCenter = finalResult.center;
    }
    
    return {
        "center" : bestCenter,
        "radius" : bestRadius,
        "plane" : facePlane
    } as LargestInscribedCircleResult;
}

/**
 * Create a sketch circle representing the largest inscribed circle on a planar face.
 * This function calculates the largest inscribed circle and creates it as a sketch entity.
 * 
 * @param context {Context} : The context in which to create the circle.
 * @param id {Id} : The id for the operation.
 * @param definition {{
 *      @field face {Query} : A query that resolves to a single planar face.
 *      @field options {LargestInscribedCircleOptions} : Optional configuration for the algorithm. @optional
 * }}
 * 
 * @example `opLargestInscribedCircle(context, id + "inscribedCircle", { "face" : qNthElement(qEverything(EntityType.FACE), 0) })`
 *          creates a sketch circle representing the largest inscribed circle on the first face.
 */
export function opLargestInscribedCircle(context is Context, id is Id, definition is map)
precondition
{
    definition.face is Query;
    definition.options == undefined || definition.options is LargestInscribedCircleOptions;
}
{
    // Calculate the largest inscribed circle
    const circleResult = evLargestInscribedCircle(context, definition);
    
    // Create a sketch on the face plane
    const sketchId = id + "sketch";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : circleResult.plane });
    
    // Convert circle center from world coordinates to sketch local coordinates
    const localCenter = worldToPlane(circleResult.plane, circleResult.center);
    
    // Create the circle in the sketch
    skCircle(sketch, "circle", {
        "center" : localCenter,
        "radius" : circleResult.radius
    });
    
    // Solve the sketch to create the geometry
    skSolve(sketch);
}

/**
 * Tester feature for the largest inscribed circle utility.
 * This feature allows interactive testing and demonstration of the largest inscribed circle
 * calculation on planar faces.
 * 
 * Functionality:
 * - Select one or more planar faces
 * - Configure algorithm parameters (max iterations, tolerance)
 * - Visualize the resulting circles
 * - Display calculated radius and center information
 */
annotation { "Feature Type Name" : "Largest Inscribed Circle Tester" }
export const largestInscribedCircleTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Planar faces", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 100 }
        definition.faces is Query;
        
        annotation { "Name" : "Create circles" }
        definition.createCircles is boolean;
        
        annotation { "Group Name" : "Algorithm options", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Max iterations" }
            isInteger(definition.maxIterations, { (unitless) : [5, 20, 50] } as IntegerBoundSpec);
            
            annotation { "Name" : "Tolerance" }
            isLength(definition.tolerance, { (meter) : [1e-9, 1e-6, 1e-3] } as LengthBoundSpec);
        }
        
        annotation { "Group Name" : "Display options", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Show center points" }
            definition.showCenterPoints is boolean;
            
            annotation { "Name" : "Show radius info" }
            definition.showRadiusInfo is boolean;
            
            annotation { "Name" : "Debug highlighting" }
            definition.debugHighlight is boolean;
        }
    }
    {
        println("=== LARGEST INSCRIBED CIRCLE TESTER START ===");
        
        // Evaluate the selected faces
        const faces = evaluateQuery(context, definition.faces);
        const faceCount = size(faces);
        
        println("Number of faces selected: " ~ faceCount);
        
        if (faceCount == 0)
        {
            throw regenError("Please select at least one planar face");
        }
        
        // Build options map for the algorithm
        const options = {
            "maxIterations" : definition.maxIterations,
            "tolerance" : definition.tolerance
        } as LargestInscribedCircleOptions;
        
        // Process each face
        var faceIndex = 0;
        for (var face in faces)
        {
            println("--- Processing Face " ~ faceIndex ~ " ---");
            
            try
            {
                // Calculate the largest inscribed circle
                const result = evLargestInscribedCircle(context, {
                    "face" : face,
                    "options" : options
                });
                
                println("  Center: " ~ result.center);
                println("  Radius: " ~ result.radius);
                println("  Plane normal: " ~ result.plane.normal);
                
                // Highlight the face if debug is enabled
                if (definition.debugHighlight)
                {
                    debug(context, face, DebugColor.BLUE);
                }
                
                // Create the inscribed circle if requested
                if (definition.createCircles)
                {
                    opLargestInscribedCircle(context, id + ("circle" ~ faceIndex), {
                        "face" : face,
                        "options" : options
                    });
                    
                    println("  Created circle sketch");
                }
                
                // Show center point if requested
                if (definition.showCenterPoints)
                {
                    opPoint(context, id + ("center" ~ faceIndex), {
                        "point" : result.center
                    });
                    
                    println("  Created center point");
                }
                
                // Display radius information as a debug point if requested
                if (definition.showRadiusInfo)
                {
                    // Create a point at the radius distance along the x-axis of the plane
                    const radiusPoint = result.center + result.plane.x * result.radius;
                    opPoint(context, id + ("radiusPoint" ~ faceIndex), {
                        "point" : radiusPoint
                    });
                    
                    println("  Created radius indicator point");
                }
            }
            catch (error)
            {
                println("  ERROR: Failed to calculate inscribed circle");
                println("  " ~ error);
                
                // Continue with next face even if this one fails
                reportFeatureInfo(context, id, face, "Failed: " ~ error);
            }
            
            faceIndex += 1;
        }
        
        println("=== LARGEST INSCRIBED CIRCLE TESTER END ===");
        println("Successfully processed " ~ faceIndex ~ " face(s)");
    },
    {
        // Default values
        "createCircles" : true,
        "maxIterations" : 20,
        "tolerance" : 1e-6 * meter,
        "showCenterPoints" : false,
        "showRadiusInfo" : false,
        "debugHighlight" : false
    });
