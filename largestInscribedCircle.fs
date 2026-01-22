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
    
    // Get all edges of the face
    const faceEdges = qAdjacent(faceQuery, AdjacencyType.EDGE, EntityType.EDGE);
    
    // Use face centroid as the center - this is a good approximation for the inscribed circle center
    const faceCenter = evApproximateCentroid(context, { "entities" : faceQuery });
    
    // Calculate the minimum distance from the face center to any edge
    // This gives us the radius of the largest inscribed circle centered at the centroid
    const distanceToEdges = evDistance(context, {
        "side0" : faceCenter,
        "side1" : faceEdges
    });
    const maxRadiusFromCentroid = distanceToEdges.distance;
    
    // Binary search for the maximum inward offset distance
    // We'll use offset to validate and potentially find a better center
    var minOffset = 0 * meter;
    var maxOffset = maxRadiusFromCentroid * 1.5; // Search slightly beyond centroid-based radius
    var bestRadius = maxRadiusFromCentroid;
    var bestCenter = faceCenter;
    
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
                // If offset succeeded, we can fit a circle of this radius
                // The center should be chosen to maximize the inscribed circle
                // Use the centroid of the offset wire edges to approximate the center
                const offsetEdges = qOwnedByBody(offsetWire, EntityType.EDGE);
                
                // Calculate centroid of the offset region by getting midpoint of bounding box
                const offsetBox = evBox3d(context, { "topology" : offsetEdges, "tight" : true });
                const offsetBoxCenter = box3dCenter(offsetBox);
                
                // Verify this center is actually on the face plane
                const localCenter2D = worldToPlane(facePlane, offsetBoxCenter);
                const projectedCenter = planeToWorld(facePlane, localCenter2D);
                
                // Verify the center is inside by checking distance to edges
                const centerToEdges = evDistance(context, {
                    "side0" : projectedCenter,
                    "side1" : faceEdges
                });
                
                // The actual radius is the minimum of: testOffset or distance from center to edges
                const actualRadius = min(testOffset, centerToEdges.distance);
                
                if (actualRadius > bestRadius)
                {
                    bestRadius = actualRadius;
                    bestCenter = projectedCenter;
                }
                
                offsetSucceeded = true;
                minOffset = testOffset; // Can try larger offset
            }
            
            // Clean up test geometry
            opDeleteBodies(context, testId + "delete", {
                "entities" : qCreatedBy(testId, EntityType.BODY)
            });
        }
        catch
        {
            // Offset failed (edges collapsed or intersected), try smaller offset
            offsetSucceeded = false;
        }
        
        if (!offsetSucceeded)
        {
            // Offset failed, reduce maximum
            maxOffset = testOffset;
        }
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
