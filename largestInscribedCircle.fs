FeatureScript 2856;
// This module is part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab for the license text.
// Copyright (c) 2013-Present PTC Inc.

/**
 * Utility functions for finding and creating the largest inscribed circle on a planar face.
 * This module provides efficient algorithms for calculating the maximum inscribed circle
 * within a planar polygonal boundary.
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

/**
 * Configuration options for the largest inscribed circle algorithm.
 * 
 * @type {{
 *      @field gridResolution {number} : Number of grid cells to use in each dimension for the initial search.
 *                                       Higher values give more accurate results but take longer. Default is 20.
 *      @field refinementIterations {number} : Number of refinement iterations to perform. Default is 3.
 *      @field tolerance {ValueWithUnits} : Tolerance for distance calculations. Default is 1e-7 meter.
 * }}
 */
export type LargestInscribedCircleOptions typecheck canBeLargestInscribedCircleOptions;

predicate canBeLargestInscribedCircleOptions(value)
{
    value is map;
    if (value.gridResolution != undefined)
    {
        value.gridResolution is number;
        value.gridResolution > 0;
    }
    if (value.refinementIterations != undefined)
    {
        value.refinementIterations is number;
        value.refinementIterations >= 0;
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
 *      @field center {Vector} : The 3D world position of the circle center.
 *      @field radius {ValueWithUnits} : The radius of the largest inscribed circle.
 *      @field plane {Plane} : The plane on which the circle lies.
 * }}
 */
export type LargestInscribedCircleResult typecheck canBeLargestInscribedCircleResult;

predicate canBeLargestInscribedCircleResult(value)
{
    value is map;
    is3dLengthVector(value.center);
    isLength(value.radius);
    value.plane is Plane;
}

/**
 * Calculate the largest inscribed circle for a planar face.
 * Uses a grid-based search algorithm with successive refinement to find the point
 * inside the face that is farthest from all edges (the Chebyshev center).
 * 
 * @param context {Context} : The context in which to evaluate.
 * @param face {Query} : A query that resolves to a single planar face.
 * @param options {LargestInscribedCircleOptions} : Optional configuration for the algorithm. @optional
 * @returns {LargestInscribedCircleResult} : The center, radius, and plane of the largest inscribed circle.
 * 
 * @example `var result = evLargestInscribedCircle(context, { "face" : qNthElement(qEverything(EntityType.FACE), 0) })`
 *          returns the largest inscribed circle for the first face.
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
    const gridResolution = (definition.options != undefined && definition.options.gridResolution != undefined) ? 
                          definition.options.gridResolution : 20;
    const refinementIterations = (definition.options != undefined && definition.options.refinementIterations != undefined) ? 
                                 definition.options.refinementIterations : 3;
    const tolerance = (definition.options != undefined && definition.options.tolerance != undefined) ? 
                     definition.options.tolerance : 1e-7 * meter;
    
    // Get bounding box of the face in 3D
    const boundingBox3d = evBox3d(context, { "topology" : faceQuery, "tight" : true });
    
    // Get all edges of the face for distance calculations
    const faceEdges = qAdjacent(faceQuery, AdjacencyType.EDGE, EntityType.EDGE);
    
    // Transform bounding box to 2D on the face plane
    // Sample bounding box corners and project to plane
    const corners = box3dAllCorners(boundingBox3d);
    const firstLocal = worldToPlane(facePlane, corners[0]);
    var minX = firstLocal[0];
    var maxX = firstLocal[0];
    var minY = firstLocal[1];
    var maxY = firstLocal[1];
    
    for (var cornerIndex = 1; cornerIndex < size(corners); cornerIndex += 1)
    {
        const localPoint = worldToPlane(facePlane, corners[cornerIndex]);
        minX = min(minX, localPoint[0]);
        maxX = max(maxX, localPoint[0]);
        minY = min(minY, localPoint[1]);
        maxY = max(maxY, localPoint[1]);
    }
    
    // Perform grid search with successive refinement
    var searchMinX = minX;
    var searchMaxX = maxX;
    var searchMinY = minY;
    var searchMaxY = maxY;
    var bestCenter = facePlane.origin;
    var bestRadius = 0 * meter;
    
    for (var iteration = 0; iteration <= refinementIterations; iteration += 1)
    {
        const stepX = (searchMaxX - searchMinX) / gridResolution;
        const stepY = (searchMaxY - searchMinY) / gridResolution;
        
        for (var gridX = 0; gridX <= gridResolution; gridX += 1)
        {
            for (var gridY = 0; gridY <= gridResolution; gridY += 1)
            {
                const localX = searchMinX + gridX * stepX;
                const localY = searchMinY + gridY * stepY;
                const testPoint2d = vector(localX, localY);
                const testPoint3d = planeToWorld(facePlane, testPoint2d);
                
                // Check if point is inside the face using evDistance with the face itself
                var isInside = false;
                try silent
                {
                    const distanceToFaceResult = evDistance(context, {
                        "side0" : testPoint3d,
                        "side1" : faceQuery
                    });
                    
                    // If distance is very small, the point is on or very near the face
                    if (distanceToFaceResult.distance < tolerance)
                    {
                        isInside = true;
                    }
                }
                
                if (!isInside)
                {
                    continue;
                }
                
                // Calculate minimum distance to all edges
                var minDistanceToEdge = undefined;
                try silent
                {
                    const distanceToEdgesResult = evDistance(context, {
                        "side0" : testPoint3d,
                        "side1" : faceEdges
                    });
                    minDistanceToEdge = distanceToEdgesResult.distance;
                }
                catch
                {
                    continue;
                }
                
                if (minDistanceToEdge == undefined)
                {
                    continue;
                }
                
                // Update best point if this is better
                if (minDistanceToEdge > bestRadius)
                {
                    bestRadius = minDistanceToEdge;
                    bestCenter = testPoint3d;
                }
            }
        }
        
        // Refine search area around best point
        if (iteration < refinementIterations)
        {
            const bestLocal = worldToPlane(facePlane, bestCenter);
            const searchWidth = max(stepX, stepY) * 2;
            searchMinX = bestLocal[0] - searchWidth;
            searchMaxX = bestLocal[0] + searchWidth;
            searchMinY = bestLocal[1] - searchWidth;
            searchMaxY = bestLocal[1] + searchWidth;
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
