FeatureScript 2837;

/**
 * Medial Axis Curve Generator
 * 
 * This feature generates an approximation of the medial axis (skeleton) of a selected face.
 * The medial axis is the locus of centers of maximal inscribed circles/spheres that fit
 * within the face boundaries.
 * 
 * The implementation uses a distance field sampling approach:
 * 1. Samples the face at multiple parametric locations using isoparametric curves
 * 2. Computes the distance from each sample point to the nearest boundary edge
 * 3. Identifies points that are local maxima of this distance field (medial axis candidates)
 * 4. Fits a smooth curve through these candidate points
 * 
 * This approach:
 * - Works on arbitrary 3D surfaces (not limited to planar faces)
 * - Is more performant than Delaunay triangulation
 * - Produces smooth, continuous curves suitable for CAD operations
 * 
 * Key Parameters:
 * - Sample resolution: Controls the density of sampling points (higher = more accurate but slower)
 * - Distance threshold: Minimum distance from boundary to consider a point (filters edge noise)
 */

// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/context.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/box.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");

/**
 * Defines the main feature for medial axis curve generation
 */
annotation { "Feature Type Name" : "Medial Axis Curve" }
export const medialAxisCurve = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face", "Filter" : EntityType.FACE && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.face is Query;

        annotation { "Name" : "Sample resolution", "Default" : 20 }
        isInteger(definition.sampleResolution, { (unitless) : [5, 20, 100] } as IntegerBoundSpec);

        annotation { "Name" : "Distance threshold", "Default" : 0.01 * millimeter }
        isLength(definition.distanceThreshold, NONNEGATIVE_LENGTH_BOUNDS);

        annotation { "Name" : "Create composite curve", "Default" : true }
        definition.createComposite is boolean;
    }
    {
        // Validate input
        if (isQueryEmpty(context, definition.face))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["face"]);
        }

        // Get boundary edges of the face
        const boundaryEdges = qAdjacent(definition.face, AdjacencyType.EDGE, EntityType.EDGE);
        if (isQueryEmpty(context, boundaryEdges))
        {
            throw regenError("Selected face has no detectable boundary edges", ["face"]);
        }

        // Sample the face and find medial axis points
        const medialAxisPoints = computeMedialAxisPoints(context, definition.face, boundaryEdges, 
                                                          definition.sampleResolution, definition.distanceThreshold);

        if (size(medialAxisPoints) < 2)
        {
            throw regenError("Insufficient medial axis points found. Try adjusting sample resolution or distance threshold.", ["sampleResolution", "distanceThreshold"]);
        }

        // Create curve through medial axis points
        createMedialAxisCurve(context, id, medialAxisPoints, definition.createComposite);

        // Report success information
        reportFeatureInfo(context, id, "Created medial axis curve with " ~ size(medialAxisPoints) ~ " control points");
    });

/**
 * Computes points lying on the approximate medial axis of a face
 * 
 * @param context : The current context
 * @param face : The face to analyze
 * @param boundaryEdges : Query for the boundary edges of the face
 * @param sampleResolution : Number of samples in each parametric direction
 * @param distanceThreshold : Minimum distance from boundary to consider
 * 
 * @returns : Array of 3D points (Vectors) representing the medial axis
 */
function computeMedialAxisPoints(context is Context, face is Query, boundaryEdges is Query, 
                                   sampleResolution is number, distanceThreshold is ValueWithUnits) returns array
{
    var medialAxisPoints = [];
    
    // Sample the face using parametric coordinates
    const numSamples = sampleResolution;
    
    // Build distance field by sampling the face
    var distanceField = [];
    var samplePoints = [];
    
    for (var uIndex = 0; uIndex < numSamples; uIndex += 1)
    {
        for (var vIndex = 0; vIndex < numSamples; vIndex += 1)
        {
            const u = uIndex / (numSamples - 1);
            const v = vIndex / (numSamples - 1);
            
            // Get 3D point at this parametric location
            var tangentPlane;
            try
            {
                tangentPlane = evFaceTangentPlane(context, {
                    "face" : face,
                    "parameter" : vector(u, v)
                });
            }
            catch
            {
                // Skip points where evaluation fails (outside parametric domain)
                continue;
            }
            
            const samplePoint = tangentPlane.origin;
            
            // Compute minimum distance to boundary edges
            const minDistance = computeMinDistanceToBoundary(context, samplePoint, boundaryEdges);
            
            // Store sample data
            samplePoints = append(samplePoints, {
                "point" : samplePoint,
                "u" : u,
                "v" : v,
                "distance" : minDistance,
                "uIndex" : uIndex,
                "vIndex" : vIndex
            });
        }
    }
    
    // Find local maxima in the distance field (medial axis candidates)
    for (var i = 0; i < size(samplePoints); i += 1)
    {
        const sample = samplePoints[i];
        
        // Skip points too close to boundary
        if (sample.distance < distanceThreshold)
        {
            continue;
        }
        
        // Check if this is a local maximum compared to neighbors
        var isLocalMaximum = true;
        
        for (var j = 0; j < size(samplePoints); j += 1)
        {
            if (i == j)
            {
                continue;
            }
            
            const neighbor = samplePoints[j];
            
            // Check if neighbor is adjacent in parametric space
            const uDiff = abs(sample.uIndex - neighbor.uIndex);
            const vDiff = abs(sample.vIndex - neighbor.vIndex);
            
            if (uDiff <= 1 && vDiff <= 1)
            {
                // This is a neighbor
                if (neighbor.distance > sample.distance)
                {
                    isLocalMaximum = false;
                    break;
                }
            }
        }
        
        if (isLocalMaximum)
        {
            medialAxisPoints = append(medialAxisPoints, sample.point);
        }
    }
    
    // Sort points to create a connected curve
    if (size(medialAxisPoints) > 1)
    {
        medialAxisPoints = sortPointsByProximity(medialAxisPoints);
    }
    
    return medialAxisPoints;
}

/**
 * Computes the minimum distance from a point to any edge in a query
 * 
 * @param context : The current context
 * @param point : The 3D point to measure from
 * @param edges : Query containing edges to measure to
 * 
 * @returns : Minimum distance as ValueWithUnits
 */
function computeMinDistanceToBoundary(context is Context, point is Vector, edges is Query) returns ValueWithUnits
{
    const edgeArray = evaluateQuery(context, edges);
    var minDistance = undefined;
    
    for (var edge in edgeArray)
    {
        try
        {
            const distanceResult = evDistance(context, {
                "side0" : point,
                "side1" : edge
            });
            
            const distance = distanceResult.distance;
            
            if (minDistance == undefined || distance < minDistance)
            {
                minDistance = distance;
            }
        }
        catch
        {
            // Skip edges where distance computation fails
        }
    }
    
    // Return a default if no valid distance was found
    if (minDistance == undefined)
    {
        minDistance = 0 * meter;
    }
    
    return minDistance;
}

/**
 * Sorts points by proximity to create a connected path
 * Uses a greedy nearest-neighbor approach
 * 
 * @param points : Array of 3D points (Vectors)
 * 
 * @returns : Sorted array of points
 */
function sortPointsByProximity(points is array) returns array
{
    if (size(points) < 2)
    {
        return points;
    }
    
    var sortedPoints = [points[0]];
    var remainingPoints = [];
    
    for (var i = 1; i < size(points); i += 1)
    {
        remainingPoints = append(remainingPoints, points[i]);
    }
    
    while (size(remainingPoints) > 0)
    {
        const currentPoint = sortedPoints[size(sortedPoints) - 1];
        var nearestIndex = 0;
        var nearestDistance = norm(remainingPoints[0] - currentPoint);
        
        for (var i = 1; i < size(remainingPoints); i += 1)
        {
            const distance = norm(remainingPoints[i] - currentPoint);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }
        
        sortedPoints = append(sortedPoints, remainingPoints[nearestIndex]);
        
        // Remove the nearest point from remaining points
        var newRemainingPoints = [];
        for (var i = 0; i < size(remainingPoints); i += 1)
        {
            if (i != nearestIndex)
            {
                newRemainingPoints = append(newRemainingPoints, remainingPoints[i]);
            }
        }
        remainingPoints = newRemainingPoints;
    }
    
    return sortedPoints;
}

/**
 * Creates a curve through the computed medial axis points
 * 
 * @param context : The current context
 * @param id : The feature ID
 * @param points : Array of 3D points defining the medial axis
 * @param createComposite : Whether to create a composite curve from multiple segments
 */
function createMedialAxisCurve(context is Context, id is Id, points is array, createComposite is boolean)
{
    // Create a 3D fit spline through the medial axis points
    opFitSpline(context, id + "medialSpline", {
        "points" : points
    });
    
    if (createComposite)
    {
        const curveBodies = qCreatedBy(id + "medialSpline", EntityType.BODY);
        if (!isQueryEmpty(context, curveBodies))
        {
            try
            {
                opCreateCompositePart(context, id + "composite", {
                    "bodies" : curveBodies,
                    "closed" : false
                });
            }
            catch
            {
                // If composite creation fails, just keep the individual curves
            }
        }
    }
}
