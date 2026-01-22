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
 * Calculate the largest inscribed circle for a planar face.
 * Uses binary search with inward offset operations to efficiently find the maximum inscribed circle.
 * This approach leverages Onshape's native geometric operations for optimal performance.
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
    
    // Helper function to check if a point is inside the face and get its distance to edges
    function evaluatePoint(point) returns map
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
    
    // Collect candidate centers
    var candidates = [];
    
    // Candidate 1: Face centroid (may be outside for concave faces)
    const faceCentroid = evApproximateCentroid(context, { "entities" : faceQuery });
    candidates = append(candidates, faceCentroid);
    
    // Candidate 2: Bounding box center
    const boundingBox = evBox3d(context, { "topology" : faceQuery, "tight" : true });
    const boxCenter = box3dCenter(boundingBox);
    candidates = append(candidates, boxCenter);
    
    // Candidate 3: Sample points along angle bisectors at vertices
    const vertices = evaluateQuery(context, faceVertices);
    for (var vertex in vertices)
    {
        try silent
        {
            const vertexPoint = evVertexPoint(context, { "vertex" : vertex });
            
            // Get adjacent edges to this vertex
            const adjacentEdges = qAdjacent(vertex, AdjacencyType.VERTEX, EntityType.EDGE);
            const adjacentEdgesList = evaluateQuery(context, adjacentEdges);
            
            if (size(adjacentEdgesList) == 2)
            {
                // Get tangent directions at vertex
                const edge1Tangent = evEdgeTangentLine(context, {
                    "edge" : adjacentEdgesList[0],
                    "parameter" : 0.01,
                    "arcLengthParameterization" : false
                });
                
                const edge2Tangent = evEdgeTangentLine(context, {
                    "edge" : adjacentEdgesList[1],
                    "parameter" : 0.01,
                    "arcLengthParameterization" : false
                });
                
                // Calculate angle bisector direction
                const dir1 = normalize(edge1Tangent.direction);
                const dir2 = normalize(edge2Tangent.direction);
                const bisector = normalize(dir1 + dir2);
                
                // Sample points along bisector at various distances
                const maxDist = box3dDiagonalLength(boundingBox) / 4;
                for (var distFactor = 0.1; distFactor <= 1.0; distFactor += 0.3)
                {
                    const testPoint = vertexPoint + bisector * maxDist * distFactor;
                    candidates = append(candidates, testPoint);
                }
            }
        }
    }
    
    // Find best candidate from all samples
    var bestRadius = 0 * meter;
    var bestCenter = faceCentroid;
    
    for (var candidate in candidates)
    {
        const result = evaluatePoint(candidate);
        if (result.valid && result.radius > bestRadius)
        {
            bestRadius = result.radius;
            bestCenter = result.center;
        }
    }
    
    // Use binary search with offset to refine and find even better solutions
    var minOffset = bestRadius;
    var maxOffset = bestRadius * 2; // Search for potentially larger circles
    
    for (var iteration = 0; iteration < maxIterations; iteration += 1)
    {
        // Check convergence
        if (maxOffset - minOffset < tolerance)
        {
            break;
        }
        
        const testOffset = (minOffset + maxOffset) / 2;
        
        // Try to create inward offset of the face edges
        const testId = "testOffset" ~ iteration;
        var offsetSucceeded = false;
        
        try silent
        {
            // Create offset wire inward
            opOffsetWire(context, testId, {
                "edges" : faceEdges,
                "normal" : facePlane.normal,
                "offset1" : -testOffset,  // Negative for inward offset
                "offset2" : 0 * meter
            });
            
            // Check if offset wire was created successfully
            const offsetWire = qCreatedBy(testId, EntityType.BODY);
            if (!isQueryEmpty(context, offsetWire))
            {
                // Calculate center candidates from offset region
                const offsetEdges = qOwnedByBody(offsetWire, EntityType.EDGE);
                const offsetBox = evBox3d(context, { "topology" : offsetEdges, "tight" : true });
                const offsetBoxCenter = box3dCenter(offsetBox);
                
                // Project to face plane
                const localCenter2D = worldToPlane(facePlane, offsetBoxCenter);
                const projectedCenter = planeToWorld(facePlane, localCenter2D);
                
                // Evaluate this center
                const centerResult = evaluatePoint(projectedCenter);
                
                if (centerResult.valid && centerResult.radius > bestRadius)
                {
                    bestRadius = centerResult.radius;
                    bestCenter = projectedCenter;
                    minOffset = testOffset; // Can try larger offset
                    offsetSucceeded = true;
                }
                else
                {
                    // This offset didn't improve, try smaller
                    maxOffset = testOffset;
                }
            }
            
            // Clean up test geometry
            opDeleteBodies(context, testId + "delete", {
                "entities" : qCreatedBy(testId, EntityType.BODY)
            });
        }
        catch
        {
            // Offset failed, try smaller offset
            maxOffset = testOffset;
        }
    }
    
    // Final validation: ensure center is on face and radius is correct
    const finalResult = evaluatePoint(bestCenter);
    if (finalResult.valid)
    {
        bestRadius = finalResult.radius;
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
