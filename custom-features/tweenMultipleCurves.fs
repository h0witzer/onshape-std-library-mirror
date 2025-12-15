FeatureScript 2837;

/**
 * Tween Multiple Curves Feature
 * 
 * This feature extends the basic tween curve functionality to handle arbitrary numbers
 * of connected curves (paths) on each side. It interpolates between two paths to create
 * a middle curve, matching subsegments using one of two methods:
 * 
 * 1. Nearest Distance: Uses evDistance to find break points where vertices from one path
 *    map to the other path. This ensures that natural connection points are matched.
 * 
 * 2. Path Length Parameterization: Uses path length ratios from vertices on both paths
 *    to determine unified sampling points. This creates more uniform distribution along
 *    the paths regardless of individual edge lengths.
 * 
 * The feature works by:
 * - Constructing ordered paths from the selected edge groups
 * - Determining sample parameters using the chosen method
 * - Sampling both paths at those parameters
 * - Interpolating (tweening) the sampled 3D points
 * - Creating a fitted spline through the interpolated points
 * 
 * This approach is inspired by the auto-loft-connections feature but adapted for
 * creating a single interpolated curve rather than loft connection lines.
 */

// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/path.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");

/**
 * Defines the method for matching curve segments between two paths.
 */
export enum TweenConnectionMethod
{
    annotation { "Name" : "Nearest distance" }
    NEAREST_DISTANCE,
    annotation { "Name" : "Path length parameterization" }
    PATH_LENGTH
}

export const TWEEN_FRACTION_BOUNDS = { (unitless) : [0, 0.5, 1] } as RealBoundSpec;

annotation { 
    "Feature Type Name" : "Tween Multiple Curves",
    "Feature Type Description" : "Interpolates between two paths made of multiple connected curves. Matches subsegments using either nearest distance or path length parameterization.",
    "UIHint" : "NO_PREVIEW_PROVIDED" 
}
export const tweenMultipleCurves = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Connection method", "UIHint" : UIHint.SHOW_LABEL }
        definition.connectionMethod is TweenConnectionMethod;
        
        annotation { "Name" : "First curve or edge group", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO }
        definition.curves1 is Query;
        
        annotation { "Name" : "Second curve or edge group", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO }
        definition.curves2 is Query;
        
        annotation { "Name" : "Tween fraction" }
        isReal(definition.fraction, TWEEN_FRACTION_BOUNDS);
    }
    {
        // Get all connected edges for each input
        const edgeGroup1 = qTangentConnectedEdges(definition.curves1);
        const edgeGroup2 = qTangentConnectedEdges(definition.curves2);
        
        const edgeCount1 = evaluateQueryCount(context, edgeGroup1);
        const edgeCount2 = evaluateQueryCount(context, edgeGroup2);
        
        // For single edges, we could use the original tweenCurves function for better B-spline interpolation,
        // but for now we'll use the same path-based approach for consistency
        // Single curve case will still work with the sampling method below
        
        // Generate sample parameters based on the selected method
        var sampleParameters = [];
        if (definition.connectionMethod == TweenConnectionMethod.PATH_LENGTH)
        {
            sampleParameters = generatePathLengthSampleParameters(context, edgeGroup1, edgeGroup2);
        }
        else
        {
            sampleParameters = generateNearestDistanceSampleParameters(context, edgeGroup1, edgeGroup2);
        }
        
        // Sample both paths at the determined parameters
        const path1 = constructPath(context, edgeGroup1);
        const path2 = constructPath(context, edgeGroup2);
        
        const samples1 = evPathTangentLines(context, path1, sampleParameters);
        const samples2 = evPathTangentLines(context, path2, sampleParameters);
        
        // Tween the sampled points
        var tweenedPoints = [];
        for (var i = 0; i < size(sampleParameters); i += 1)
        {
            const point1 = samples1.tangentLines[i].origin;
            const point2 = samples2.tangentLines[i].origin;
            const tweenedPoint = point1 * (1 - definition.fraction) + point2 * definition.fraction;
            tweenedPoints = append(tweenedPoints, tweenedPoint);
        }
        
        // Create a B-spline curve through the tweened points
        createSplineThroughPoints(context, id, tweenedPoints);
    });

/**
 * Generate sample parameters using nearest distance method (evDistance).
 * Uses vertex positions from both paths to determine where to sample.
 * 
 * Returns an array of path parameters (0 to 1) where samples should be taken.
 */
function generateNearestDistanceSampleParameters(context is Context, edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Build paths to get total lengths
    const path1 = constructPath(context, edgeGroup1);
    const path2 = constructPath(context, edgeGroup2);
    
    const totalLength1 = evPathLength(context, path1);
    const totalLength2 = evPathLength(context, path2);
    
    // Get all vertices from both edge groups
    const allVertices1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
    const allVertices2 = qAdjacent(edgeGroup2, AdjacencyType.VERTEX, EntityType.VERTEX);
    
    // Calculate path length ratios for vertices in path 1
    var pathRatios = [];
    for (var i = 0; i < evaluateQueryCount(context, allVertices1); i += 1)
    {
        const vertex = qNthElement(allVertices1, i);
        const ratio = calculatePathLengthRatioForVertex(context, vertex, path1, totalLength1);
        pathRatios = append(pathRatios, ratio);
    }
    
    // For each vertex in path 2, find where it maps to on path 1 using evDistance
    for (var j = 0; j < evaluateQueryCount(context, allVertices2); j += 1)
    {
        const vertex = qNthElement(allVertices2, j);
        
        // Find the closest point on path 1
        const edgeDist = evDistance(context, {
                    "side0" : vertex,
                    "side1" : edgeGroup1,
                    "arcLengthParameterization" : true
                });
        
        // Convert this to a path ratio on path 1
        const ratio = convertEdgeParameterToPathRatio(context, path1, totalLength1, 
                                                      edgeDist.sides[1].index, edgeDist.sides[1].parameter);
        pathRatios = append(pathRatios, ratio);
    }
    
    // Remove duplicates and sort
    pathRatios = removeDuplicateRatios(pathRatios);
    pathRatios = sortRatios(pathRatios);
    
    // Ensure we have start and end points
    if (size(pathRatios) == 0 || pathRatios[0] > TOLERANCE.zeroLength)
    {
        pathRatios = concatenateArrays([[0.0], pathRatios]);
    }
    if (size(pathRatios) == 0 || pathRatios[size(pathRatios) - 1] < 1.0 - TOLERANCE.zeroLength)
    {
        pathRatios = append(pathRatios, 1.0);
    }
    
    // Add intermediate samples for smoother curves (optional)
    pathRatios = addIntermediateSamples(pathRatios);
    
    return pathRatios;
}

/**
 * Generate sample parameters using path length parameterization method.
 * Uses path length ratios from vertices on both paths to determine sampling points.
 * 
 * Returns an array of path parameters (0 to 1) where samples should be taken.
 */
function generatePathLengthSampleParameters(context is Context, edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Construct ordered paths from the edge groups
    const path1 = constructPath(context, edgeGroup1);
    const path2 = constructPath(context, edgeGroup2);
    
    const totalLength1 = evPathLength(context, path1);
    const totalLength2 = evPathLength(context, path2);
    
    // Get all vertices from both paths
    const allVertices1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
    const allVertices2 = qAdjacent(edgeGroup2, AdjacencyType.VERTEX, EntityType.VERTEX);
    
    // Calculate path length ratios for all vertices in path 1
    var pathRatios = [];
    for (var i = 0; i < evaluateQueryCount(context, allVertices1); i += 1)
    {
        const currentPoint = qNthElement(allVertices1, i);
        const pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, path1, totalLength1);
        pathRatios = append(pathRatios, pathLengthRatio);
    }
    
    // Calculate path length ratios for all vertices in path 2
    for (var j = 0; j < evaluateQueryCount(context, allVertices2); j += 1)
    {
        const currentPoint = qNthElement(allVertices2, j);
        const pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, path2, totalLength2);
        pathRatios = append(pathRatios, pathLengthRatio);
    }
    
    // Merge and sort all unique path length ratios
    pathRatios = removeDuplicateRatios(pathRatios);
    pathRatios = sortRatios(pathRatios);
    
    // Add start (0) and end (1) ratios if not present
    if (size(pathRatios) == 0 || pathRatios[0] > TOLERANCE.zeroLength)
    {
        pathRatios = concatenateArrays([[0.0], pathRatios]);
    }
    if (size(pathRatios) == 0 || pathRatios[size(pathRatios) - 1] < 1.0 - TOLERANCE.zeroLength)
    {
        pathRatios = append(pathRatios, 1.0);
    }
    
    // Add intermediate samples for smoother curves
    pathRatios = addIntermediateSamples(pathRatios);
    
    return pathRatios;
}

/**
 * Convert an edge index and parameter to a global path ratio.
 * 
 * @param context: The Onshape context
 * @param path: The path object
 * @param totalLength: Total length of the path
 * @param edgeIndex: Index of the edge in the path
 * @param edgeParameter: Parameter (0 to 1) on that edge (in arc length parameterization)
 * @returns Global path ratio from 0 to 1
 */
function convertEdgeParameterToPathRatio(context is Context, path is Path, totalLength is ValueWithUnits,
                                         edgeIndex is number, edgeParameter is number) returns number
{
    var accumulatedLength = 0 * meter;
    
    // Accumulate length up to this edge
    for (var i = 0; i < edgeIndex; i += 1)
    {
        accumulatedLength += evLength(context, { "entities" : path.edges[i] });
    }
    
    // Add length within this edge
    const edgeLength = evLength(context, { "entities" : path.edges[edgeIndex] });
    var lengthIntoEdge = edgeLength * edgeParameter;
    
    // Account for edge flipping
    if (path.flipped[edgeIndex])
    {
        lengthIntoEdge = edgeLength - lengthIntoEdge;
    }
    
    const totalPathLengthToPoint = accumulatedLength + lengthIntoEdge;
    return totalPathLengthToPoint / totalLength;
}

/**
 * Add intermediate sample points between existing samples for smoother curves.
 * Adds 2-3 intermediate samples between each pair of existing samples.
 * 
 * @param samples: Array of sample parameters (0 to 1)
 * @returns Augmented array with intermediate samples
 */
function addIntermediateSamples(samples is array) returns array
{
    if (size(samples) <= 1)
    {
        return samples;
    }
    
    var augmented = [samples[0]];
    
    for (var i = 0; i < size(samples) - 1; i += 1)
    {
        const start = samples[i];
        const end = samples[i + 1];
        const gap = end - start;
        
        // Add 2 intermediate samples if the gap is large enough
        if (gap > 0.05)
        {
            augmented = append(augmented, start + gap * 0.33);
            augmented = append(augmented, start + gap * 0.67);
        }
        else if (gap > 0.02)
        {
            // Add 1 intermediate sample for smaller gaps
            augmented = append(augmented, start + gap * 0.5);
        }
        
        augmented = append(augmented, end);
    }
    
    return augmented;
}

/**
 * Calculate the path length ratio (0 to 1) of a vertex position along a path.
 */
function calculatePathLengthRatioForVertex(context is Context, vertex is Query, path is Path, totalLength is ValueWithUnits) returns number
{
    const vertexPosition = evVertexPoint(context, { "vertex" : vertex });
    
    // Find which edge in the ordered path contains this vertex
    var pathLengthBeforeEdge = 0 * meter;
    
    for (var i = 0; i < size(path.edges); i += 1)
    {
        const edge = path.edges[i];
        const edgeLength = evLength(context, { "entities" : edge });
        
        // Check if vertex is on this edge by using evDistance
        const dist = evDistance(context, {
                    "side0" : vertex,
                    "side1" : edge,
                    "arcLengthParameterization" : true
                });
        
        // If the vertex is very close to this edge, it's on this edge
        if (dist.distance < TOLERANCE.zeroLength * meter)
        {
            var arcLengthParameter = dist.sides[1].parameter;
            var lengthAlongEdge = edgeLength * arcLengthParameter;
            
            // Account for edge flipping in the path
            if (path.flipped[i])
            {
                lengthAlongEdge = edgeLength - lengthAlongEdge;
            }
            
            const totalPathLengthToVertex = pathLengthBeforeEdge + lengthAlongEdge;
            return totalPathLengthToVertex / totalLength;
        }
        
        pathLengthBeforeEdge = pathLengthBeforeEdge + edgeLength;
    }
    
    // If we didn't find the vertex on any edge, return 0 (shouldn't happen)
    return 0;
}



/**
 * Remove duplicate ratios from an array (within tolerance).
 */
function removeDuplicateRatios(ratios is array) returns array
{
    var unique = [];
    for (var ratio in ratios)
    {
        var isDuplicate = false;
        for (var existing in unique)
        {
            if (abs(ratio - existing) < TOLERANCE.zeroLength)
            {
                isDuplicate = true;
                break;
            }
        }
        if (!isDuplicate)
        {
            unique = append(unique, ratio);
        }
    }
    return unique;
}

/**
 * Sort an array of numbers using bubble sort.
 */
function sortRatios(arr is array) returns array
{
    var sorted = arr;
    const n = size(sorted);
    for (var i = 0; i < n - 1; i += 1)
    {
        for (var j = 0; j < n - i - 1; j += 1)
        {
            if (sorted[j] > sorted[j + 1])
            {
                const temp = sorted[j];
                sorted[j] = sorted[j + 1];
                sorted[j + 1] = temp;
            }
        }
    }
    return sorted;
}

/**
 * Create a B-spline curve through a set of points.
 * Uses the curve fitting capabilities to create a smooth interpolating spline.
 * 
 * @param context: The Onshape context
 * @param id: Feature ID
 * @param points: Array of 3D points to interpolate
 */
function createSplineThroughPoints(context is Context, id is Id, points is array)
{
    if (size(points) < 2)
    {
        throw regenError("Need at least 2 points to create a curve.", ["curves1", "curves2"]);
    }
    
    // Use opFitSpline to create a curve through the points
    opFitSpline(context, id + "tweenedSpline", {
                "points" : points
            });
}
