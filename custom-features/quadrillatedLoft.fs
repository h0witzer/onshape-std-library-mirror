FeatureScript 2878;
/**
 * Quadrillated Loft Feature
 * 
 * Creates a loft between two profiles using quadrilateral facets instead of triangular tessellation.
 * Uses curve traversal logic similar to kerf bending analytical approach to walk curves by tracking
 * angular change, then facets both curves with matched segment counts to create parallel quadrilateral
 * faces suitable for press braking operations.
 */

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/containers.fs", version : "2878.0");
import(path : "onshape/std/curveGeometry.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/geomOperations.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/string.fs", version : "2878.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2878.0");
import(path : "onshape/std/topologyUtils.fs", version : "2878.0");
import(path : "onshape/std/valueBounds.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");
import(path : "onshape/std/debug.fs", version : "2878.0");
import(path : "onshape/std/math.fs", version : "2878.0");
import(path : "onshape/std/sketch.fs", version : "2878.0");

const FACET_ANGLE_BOUNDS = {
            (degree) : [0.1, 10, 90],
            (radian) : 0.174533
        } as AngleBoundSpec;

const MIN_SEGMENTS_BOUNDS = {
            (unitless) : [3, 10, 1000]
        } as IntegerBoundSpec;

/**
 * Create a quadrillated loft between two profiles with quadrilateral facets instead of triangular tessellation.
 * Suitable for sheet metal and press braking operations.
 */
annotation { "Feature Type Name" : "Quadrillated Loft" }
export const quadrillatedLoft = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Profile 1",
                    "Filter" : ((EntityType.EDGE || EntityType.BODY && BodyType.WIRE && SketchObject.NO) && ConstructionObject.NO),
                    "AdditionalBoxSelectFilter" : (EntityType.EDGE && !EntityType.BODY) }
        definition.profile1 is Query;
        
        annotation { "Name" : "Profile 2",
                    "Filter" : ((EntityType.EDGE || EntityType.BODY && BodyType.WIRE && SketchObject.NO) && ConstructionObject.NO),
                    "AdditionalBoxSelectFilter" : (EntityType.EDGE && !EntityType.BODY) }
        definition.profile2 is Query;
        
        annotation { "Name" : "Facet angle", "Description" : "Angular change threshold for creating new segments" }
        isAngle(definition.facetAngle, FACET_ANGLE_BOUNDS);
        
        annotation { "Name" : "Minimum segments", "Description" : "Minimum number of segments per profile" }
        isInteger(definition.minSegments, MIN_SEGMENTS_BOUNDS);
    }
    {
        // Validate profiles are selected
        if (isQueryEmpty(context, definition.profile1))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["profile1"]);
        }
        if (isQueryEmpty(context, definition.profile2))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["profile2"]);
        }
        
        // Get profile edges
        const profile1Edges = getProfileEdges(context, definition.profile1);
        const profile2Edges = getProfileEdges(context, definition.profile2);
        
        // Check that profiles don't intersect
        const distanceBetweenProfiles = evDistance(context, { 
            "side0" : profile1Edges, 
            "side1" : profile2Edges,
            "arcLengthParameterization" : false 
        });
        
        if (distanceBetweenProfiles.distance < TOLERANCE.zeroLength * meter)
        {
            throw regenError(ErrorStringEnum.LOFT_SELECT_PROFILES);
        }
        
        // Facet both profiles by angular change
        const profile1Facets = facetCurveByAngle(context, profile1Edges, definition.facetAngle, definition.minSegments);
        const profile2Facets = facetCurveByAngle(context, profile2Edges, definition.facetAngle, definition.minSegments);
        
        // Match segment counts between profiles
        const matchedFacets = matchSegmentCounts(profile1Facets, profile2Facets);
        
        // Create quadrilateral loft between matched segments
        createQuadrilateralLoft(context, id, matchedFacets.profile1Points, matchedFacets.profile2Points);
    },
    {
        "facetAngle" : 10 * degree,
        "minSegments" : 10
    });

/**
 * Extract edges from profile query (handles both edges and wire bodies)
 */
function getProfileEdges(context is Context, profile is Query) returns Query
{
    const edges = qEntityFilter(profile, EntityType.EDGE);
    const wireBodies = qEntityFilter(profile, EntityType.BODY)->qBodyType(BodyType.WIRE);
    
    if (!isQueryEmpty(context, edges))
    {
        return edges;
    }
    else if (!isQueryEmpty(context, wireBodies))
    {
        return wireBodies->qOwnedByBody(EntityType.EDGE);
    }
    else
    {
        throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES);
    }
}

/**
 * Facet a curve by walking it and creating segments based on angular change threshold.
 * Similar to kerf bending analytical approach - accumulates angle change along curve.
 * 
 * @param context : The context
 * @param curveEdges : Query for the curve edges to facet
 * @param facetAngle : Angular change threshold for creating new segment
 * @param minSegments : Minimum number of segments to create
 * @returns Array of 3D points defining the facet vertices
 */
function facetCurveByAngle(context is Context, curveEdges is Query, facetAngle is ValueWithUnits, minSegments is number) returns array
{
    // Get all edges in the profile
    const edges = evaluateQuery(context, curveEdges);
    
    if (size(edges) == 0)
    {
        throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES);
    }
    
    var allPoints = [];
    var totalLength = 0 * meter;
    
    // Process each edge in the profile
    for (var edge in edges)
    {
        const edgeLength = evLength(context, { "entities" : edge });
        totalLength += edgeLength;
        
        var edgePoints = [];
        var currentParam = 0.0;
        var accumulatedAngle = 0.0 * radian;
        
        // Get initial tangent
        var tangentLine = evEdgeTangentLine(context, { 
            "edge" : edge, 
            "parameter" : currentParam, 
            "arcLengthParameterization" : true 
        });
        
        edgePoints = append(edgePoints, tangentLine.origin);
        var previousTangent = tangentLine.direction;
        
        // Walk along edge accumulating angle changes
        const parameterStep = 0.01; // 1% steps
        currentParam = parameterStep;
        
        while (currentParam <= 1.0)
        {
            tangentLine = evEdgeTangentLine(context, { 
                "edge" : edge, 
                "parameter" : currentParam, 
                "arcLengthParameterization" : true 
            });
            
            const currentTangent = tangentLine.direction;
            
            // Calculate angle change
            const dotProduct = dot(previousTangent, currentTangent);
            const clampedDot = max(-1.0, min(1.0, dotProduct));
            const angleChange = acos(clampedDot);
            
            accumulatedAngle += angleChange;
            
            // Check if accumulated angle exceeds threshold
            if (accumulatedAngle >= facetAngle)
            {
                edgePoints = append(edgePoints, tangentLine.origin);
                accumulatedAngle = 0.0 * radian;
            }
            
            previousTangent = currentTangent;
            currentParam = currentParam + parameterStep;
        }
        
        // Add end point if not already added
        if (currentParam > 1.0)
        {
            tangentLine = evEdgeTangentLine(context, { 
                "edge" : edge, 
                "parameter" : 1.0, 
                "arcLengthParameterization" : true 
            });
            
            const lastPoint = edgePoints[size(edgePoints) - 1];
            if (norm(tangentLine.origin - lastPoint) > TOLERANCE.zeroLength * meter)
            {
                edgePoints = append(edgePoints, tangentLine.origin);
            }
        }
        
        // Append points from this edge (skip first point if continuing from previous edge)
        if (size(allPoints) > 0)
        {
            edgePoints = subArray(edgePoints, 1, size(edgePoints));
        }
        allPoints = concatenateArrays([allPoints, edgePoints]);
    }
    
    // Ensure minimum segment count by uniform subdivision if needed
    if (size(allPoints) < minSegments + 1)
    {
        allPoints = uniformlySubdivideCurve(context, curveEdges, minSegments);
    }
    
    return allPoints;
}

/**
 * Uniformly subdivide a curve to create a minimum number of segments
 */
function uniformlySubdivideCurve(context is Context, curveEdges is Query, numSegments is number) returns array
{
    var points = [];
    const edges = evaluateQuery(context, curveEdges);
    
    // Calculate total length
    var totalLength = 0 * meter;
    for (var edge in edges)
    {
        totalLength += evLength(context, { "entities" : edge });
    }
    
    // Create uniform parameter spacing
    for (var i = 0; i <= numSegments; i += 1)
    {
        const targetDistance = (i / numSegments) * totalLength;
        var accumulatedLength = 0 * meter;
        
        // Find which edge this parameter falls on
        for (var edge in edges)
        {
            const edgeLength = evLength(context, { "entities" : edge });
            
            if (accumulatedLength + edgeLength >= targetDistance || i == numSegments)
            {
                // This point is on this edge
                const paramOnEdge = (targetDistance - accumulatedLength) / edgeLength;
                const clampedParam = max(0.0, min(1.0, paramOnEdge));
                
                const tangentLine = evEdgeTangentLine(context, {
                    "edge" : edge,
                    "parameter" : clampedParam,
                    "arcLengthParameterization" : true
                });
                
                points = append(points, tangentLine.origin);
                break;
            }
            
            accumulatedLength += edgeLength;
        }
    }
    
    return points;
}

/**
 * Match segment counts between two profiles to ensure quadrilateral faces.
 * Takes the maximum count and subdivides the other profile to match.
 * Maintains planar parallelism between matched segments.
 */
function matchSegmentCounts(profile1Points is array, profile2Points is array) returns map
{
    const count1 = size(profile1Points) - 1;
    const count2 = size(profile2Points) - 1;
    
    if (count1 == count2)
    {
        return {
            "profile1Points" : profile1Points,
            "profile2Points" : profile2Points
        };
    }
    
    // Use the maximum count
    const targetCount = max(count1, count2);
    
    var newProfile1Points = profile1Points;
    var newProfile2Points = profile2Points;
    
    // Subdivide the profile with fewer segments
    if (count1 < targetCount)
    {
        newProfile1Points = subdividePointsToCount(profile1Points, targetCount);
    }
    
    if (count2 < targetCount)
    {
        newProfile2Points = subdividePointsToCount(profile2Points, targetCount);
    }
    
    return {
        "profile1Points" : newProfile1Points,
        "profile2Points" : newProfile2Points
    };
}

/**
 * Subdivide an array of points to achieve a target segment count.
 * Uses proportional spacing along the polyline.
 */
function subdividePointsToCount(points is array, targetSegmentCount is number) returns array
{
    if (size(points) - 1 >= targetSegmentCount)
    {
        return points;
    }
    
    // Calculate cumulative distances along polyline
    var cumulativeDistances = [0 * meter];
    var totalLength = 0 * meter;
    
    for (var i = 1; i < size(points); i += 1)
    {
        const segmentLength = norm(points[i] - points[i - 1]);
        totalLength += segmentLength;
        cumulativeDistances = append(cumulativeDistances, totalLength);
    }
    
    // Create new points at uniform distances
    var newPoints = [];
    
    for (var i = 0; i <= targetSegmentCount; i += 1)
    {
        const targetDistance = (i / targetSegmentCount) * totalLength;
        
        // Find which segment this distance falls on
        for (var j = 0; j < size(points) - 1; j += 1)
        {
            if (cumulativeDistances[j + 1] >= targetDistance)
            {
                // Interpolate between points[j] and points[j+1]
                const segmentStart = cumulativeDistances[j];
                const segmentEnd = cumulativeDistances[j + 1];
                const segmentLength = segmentEnd - segmentStart;
                
                var t = 0.0;
                if (segmentLength > TOLERANCE.zeroLength * meter)
                {
                    t = (targetDistance - segmentStart) / segmentLength;
                }
                
                const interpolatedPoint = points[j] + t * (points[j + 1] - points[j]);
                newPoints = append(newPoints, interpolatedPoint);
                break;
            }
        }
    }
    
    return newPoints;
}

/**
 * Create a quadrilateral loft by generating faces between matched point arrays.
 * Each quad face connects corresponding segments from both profiles.
 */
function createQuadrilateralLoft(context is Context, id is Id, profile1Points is array, profile2Points is array)
{
    const numSegments = min(size(profile1Points), size(profile2Points)) - 1;
    
    if (numSegments < 1)
    {
        throw regenError("Insufficient points to create loft");
    }
    
    // Create a sketch for each quadrilateral face
    for (var i = 0; i < numSegments; i += 1)
    {
        const p1 = profile1Points[i];
        const p2 = profile1Points[i + 1];
        const p3 = profile2Points[i + 1];
        const p4 = profile2Points[i];
        
        // Create a plane for this quad face
        // Use the first three points to define the plane
        const v1 = p2 - p1;
        const v2 = p4 - p1;
        const normal = cross(v1, v2);
        const normalMag = norm(normal);
        
        if (normalMag < TOLERANCE.zeroLength * meter)
        {
            // Degenerate quad - skip
            continue;
        }
        
        const unitNormal = normal / normalMag;
        
        // Create coordinate system for the quad
        const xDir = normalize(v1);
        const yDir = normalize(cross(unitNormal, xDir));
        
        const cSys = coordSystem(p1, xDir, yDir);
        const plane = plane(cSys);
        
        // Create sketch on this plane
        const sketchId = id + ("quad" ~ i);
        var sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane });
        
        // Transform points to 2D sketch coordinates
        const p1_2d = worldToPlane(plane, p1);
        const p2_2d = worldToPlane(plane, p2);
        const p3_2d = worldToPlane(plane, p3);
        const p4_2d = worldToPlane(plane, p4);
        
        // Draw quadrilateral
        skLineSegment(sketch, "line1", { "start" : p1_2d, "end" : p2_2d });
        skLineSegment(sketch, "line2", { "start" : p2_2d, "end" : p3_2d });
        skLineSegment(sketch, "line3", { "start" : p3_2d, "end" : p4_2d });
        skLineSegment(sketch, "line4", { "start" : p4_2d, "end" : p1_2d });
        
        skSolve(sketch);
        
        // Extrude the sketch face to create a surface
        try
        {
            opExtrude(context, id + ("extrude" ~ i), {
                "entities" : qSketchRegion(sketchId),
                "direction" : unitNormal,
                "endBound" : BoundingType.BLIND,
                "endDepth" : 0 * meter
            });
        }
    }
}
