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
        
        annotation { "Name" : "Planarize surfaces", "Description" : "Replace curved surfaces with planar patches for better sheet metal compatibility" }
        definition.planarizeSurfaces is boolean;
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
        
        // Determine segment parameters based on angular changes and closest-point correspondence
        const segmentData = computeCorrespondingSegmentPoints(context, profile1Edges, profile2Edges, 
                                                               definition.facetAngle, definition.minSegments);
        
        // Create quadrilateral loft between corresponding points
        createQuadrilateralLoft(context, id, segmentData.profile1Points, segmentData.profile2Points);
        
        // Planarize surfaces if requested (cleanup step for better sheet metal compatibility)
        if (definition.planarizeSurfaces)
        {
            planarizeQuadSurfaces(context, id);
        }
    },
    {
        "facetAngle" : 10 * degree,
        "minSegments" : 10,
        "planarizeSurfaces" : true
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
 * Compute segment points with proper geometric correspondence between curves.
 * Uses closest-point matching to ensure points align geometrically, not just parametrically.
 * This prevents twisting when curves have different shapes (e.g., S-curve and mirror S-curve).
 * 
 * @param context : The context
 * @param curve1Edges : Query for first curve edges
 * @param curve2Edges : Query for second curve edges
 * @param facetAngle : Angular change threshold
 * @param minSegments : Minimum number of segments
 * @returns Map with profile1Points and profile2Points arrays
 */
function computeCorrespondingSegmentPoints(context is Context, 
                                           curve1Edges is Query, 
                                           curve2Edges is Query,
                                           facetAngle is ValueWithUnits, 
                                           minSegments is number) returns map
{
    const edges1 = evaluateQuery(context, curve1Edges);
    const edges2 = evaluateQuery(context, curve2Edges);
    
    // Walk curve1 based on angular change, then find corresponding points on curve2
    var profile1Points = [];
    var profile2Points = [];
    
    // Start with first point on curve1, find corresponding point on curve2
    const startPoint1 = evEdgeTangentLine(context, {
        "edge" : edges1[0],
        "parameter" : 0.0,
        "arcLengthParameterization" : true
    }).origin;
    
    // For start, we want curve2's start point to ensure proper progression
    const startPoint2 = evEdgeTangentLine(context, {
        "edge" : edges2[0],
        "parameter" : 0.0,
        "arcLengthParameterization" : true
    }).origin;
    
    profile1Points = append(profile1Points, startPoint1);
    profile2Points = append(profile2Points, startPoint2);
    
    // Track the last parameter on curve2 to ensure monotonic progression
    var lastParam2 = 0.0;
    
    // Walk curve1 tracking angular changes
    var currentParam1 = 0.0;
    var accumulatedAngle = 0.0 * radian;
    
    var previousTangent = evEdgeTangentLine(context, {
        "edge" : edges1[0],
        "parameter" : 0.0,
        "arcLengthParameterization" : true
    }).direction;
    
    const parameterStep = 0.01; // 1% steps
    currentParam1 = parameterStep;
    
    while (currentParam1 <= 1.0)
    {
        const point1 = samplePositionAtParameter(context, edges1, currentParam1);
        const tangent1 = sampleTangentAtParameter(context, edges1, currentParam1);
        
        // Calculate angle change
        const angleChange = acos(max(-1.0, min(1.0, dot(previousTangent, tangent1))));
        accumulatedAngle += angleChange;
        
        // Check if we've accumulated enough angle for a segment
        if (accumulatedAngle >= facetAngle)
        {
            // Find corresponding point on curve2, searching forward from last parameter
            const correspondenceResult = findCorrespondingPointProgressive(context, edges2, point1, lastParam2);
            
            profile1Points = append(profile1Points, point1);
            profile2Points = append(profile2Points, correspondenceResult.point);
            
            // Update last parameter to ensure forward progression
            lastParam2 = correspondenceResult.parameter;
            
            accumulatedAngle = 0.0 * radian;
        }
        
        previousTangent = tangent1;
        currentParam1 = currentParam1 + parameterStep;
    }
    
    // Always add end points
    const endPoint1 = evEdgeTangentLine(context, {
        "edge" : edges1[size(edges1) - 1],
        "parameter" : 1.0,
        "arcLengthParameterization" : true
    }).origin;
    
    const endPoint2 = evEdgeTangentLine(context, {
        "edge" : edges2[size(edges2) - 1],
        "parameter" : 1.0,
        "arcLengthParameterization" : true
    }).origin;
    
    // Only add if not already added
    const lastPoint1 = profile1Points[size(profile1Points) - 1];
    if (norm(endPoint1 - lastPoint1) > TOLERANCE.zeroLength * meter)
    {
        profile1Points = append(profile1Points, endPoint1);
        profile2Points = append(profile2Points, endPoint2);
    }
    
    // Ensure minimum segment count
    if (size(profile1Points) - 1 < minSegments)
    {
        return createUniformCorrespondingPoints(context, curve1Edges, curve2Edges, minSegments);
    }
    
    return {
        "profile1Points" : profile1Points,
        "profile2Points" : profile2Points
    };
}

/**
 * Find corresponding point on curve2, searching forward from lastParameter to avoid backtracking.
 * This ensures monotonic progression along both curves and prevents lofting back on itself.
 */
function findCorrespondingPointProgressive(context is Context, edges is array, referencePoint is Vector, lastParameter is number) returns map
{
    // Calculate cumulative lengths for parameter conversion
    var cumulativeLengths = [0 * meter];
    var totalLength = 0 * meter;
    
    for (var edge in edges)
    {
        const edgeLength = evLength(context, { "entities" : edge });
        totalLength += edgeLength;
        cumulativeLengths = append(cumulativeLengths, totalLength);
    }
    
    const startSearchLength = lastParameter * totalLength;
    
    // Search for closest point, but only forward from lastParameter
    var minDistance = 1e10 * meter;
    var bestPoint = undefined;
    var bestParameter = lastParameter;
    
    // Sample curve from lastParameter forward in small steps
    const searchStep = 0.02; // 2% steps for search
    var searchParam = max(lastParameter, 0.0);
    
    while (searchParam <= 1.0)
    {
        const samplePoint = samplePositionAtParameter(context, edges, searchParam);
        const distance = norm(samplePoint - referencePoint);
        
        if (distance < minDistance)
        {
            minDistance = distance;
            bestPoint = samplePoint;
            bestParameter = searchParam;
        }
        
        searchParam += searchStep;
    }
    
    // If we didn't find anything (shouldn't happen), use end point
    if (bestPoint == undefined)
    {
        const lastEdge = edges[size(edges) - 1];
        bestPoint = evEdgeTangentLine(context, {
            "edge" : lastEdge,
            "parameter" : 1.0,
            "arcLengthParameterization" : true
        }).origin;
        bestParameter = 1.0;
    }
    
    return {
        "point" : bestPoint,
        "parameter" : bestParameter
    };
}

/**
 * Find the corresponding point on curve2 for a given point on curve1.
 * Uses closest point to ensure geometric correspondence.
 */
function findCorrespondingPoint(context is Context, curveEdges is Query, referencePoint is Vector) returns Vector
{
    const distanceResult = evDistance(context, {
        "side0" : referencePoint,
        "side1" : curveEdges,
        "arcLengthParameterization" : false
    });
    
    return distanceResult.sides[1].point;
}

/**
 * Create uniformly spaced points with closest-point correspondence.
 * Used when minimum segment count is not met.
 */
function createUniformCorrespondingPoints(context is Context, 
                                          curve1Edges is Query, 
                                          curve2Edges is Query,
                                          numSegments is number) returns map
{
    var profile1Points = [];
    var profile2Points = [];
    
    for (var i = 0; i <= numSegments; i += 1)
    {
        const param = i / numSegments;
        const point1 = samplePositionAtParameter(context, evaluateQuery(context, curve1Edges), param);
        const point2 = findCorrespondingPoint(context, curve2Edges, point1);
        
        profile1Points = append(profile1Points, point1);
        profile2Points = append(profile2Points, point2);
    }
    
    return {
        "profile1Points" : profile1Points,
        "profile2Points" : profile2Points
    };
}

/**
 * Sample position at a normalized parameter on a curve.
 */
function samplePositionAtParameter(context is Context, edges is array, normalizedParam is number) returns Vector
{
    // Calculate cumulative lengths
    var cumulativeLengths = [0 * meter];
    var totalLength = 0 * meter;
    
    for (var edge in edges)
    {
        const edgeLength = evLength(context, { "entities" : edge });
        totalLength += edgeLength;
        cumulativeLengths = append(cumulativeLengths, totalLength);
    }
    
    const targetLength = normalizedParam * totalLength;
    
    // Find which edge contains this parameter
    for (var i = 0; i < size(edges); i += 1)
    {
        if (cumulativeLengths[i + 1] >= targetLength || normalizedParam >= 1.0)
        {
            const edgeLength = cumulativeLengths[i + 1] - cumulativeLengths[i];
            var paramOnEdge = 0.0;
            
            if (edgeLength > TOLERANCE.zeroLength * meter && normalizedParam < 1.0)
            {
                paramOnEdge = (targetLength - cumulativeLengths[i]) / edgeLength;
            }
            else if (normalizedParam >= 1.0)
            {
                paramOnEdge = 1.0;
            }
            
            const tangentLine = evEdgeTangentLine(context, {
                "edge" : edges[i],
                "parameter" : paramOnEdge,
                "arcLengthParameterization" : true
            });
            
            return tangentLine.origin;
        }
    }
    
    // Fallback to last edge
    const lastEdge = edges[size(edges) - 1];
    return evEdgeTangentLine(context, {
        "edge" : lastEdge,
        "parameter" : 1.0,
        "arcLengthParameterization" : true
    }).origin;
}

/**
 * Sample tangent direction at a normalized parameter on a curve (which may have multiple edges).
 */
function sampleTangentAtParameter(context is Context, edges is array, normalizedParam is number) returns Vector
{
    // Calculate cumulative lengths to find which edge this parameter falls on
    var cumulativeLengths = [0 * meter];
    var totalLength = 0 * meter;
    
    for (var edge in edges)
    {
        const edgeLength = evLength(context, { "entities" : edge });
        totalLength += edgeLength;
        cumulativeLengths = append(cumulativeLengths, totalLength);
    }
    
    const targetLength = normalizedParam * totalLength;
    
    // Find which edge contains this parameter
    for (var i = 0; i < size(edges); i += 1)
    {
        if (cumulativeLengths[i + 1] >= targetLength)
        {
            // This edge contains the target parameter
            const edgeLength = cumulativeLengths[i + 1] - cumulativeLengths[i];
            var paramOnEdge = 0.0;
            
            if (edgeLength > TOLERANCE.zeroLength * meter)
            {
                paramOnEdge = (targetLength - cumulativeLengths[i]) / edgeLength;
            }
            
            const tangentLine = evEdgeTangentLine(context, {
                "edge" : edges[i],
                "parameter" : paramOnEdge,
                "arcLengthParameterization" : true
            });
            
            return tangentLine.direction;
        }
    }
    
    // Fallback to last edge
    const lastEdge = edges[size(edges) - 1];
    return evEdgeTangentLine(context, {
        "edge" : lastEdge,
        "parameter" : 1.0,
        "arcLengthParameterization" : true
    }).direction;
}

/**
 * Planarize quadrilateral surfaces by replacing each with a planar patch.
 * Samples each surface at its midpoint to determine the best-fit plane,
 * then replaces the surface with a planar patch bounded by the same edges.
 * This improves sheet metal compatibility by ensuring perfectly planar faces.
 */
function planarizeQuadSurfaces(context is Context, id is Id)
{
    // Get all the surface faces created by the loft operations
    const loftFaces = qCreatedBy(id, EntityType.FACE);
    const faces = evaluateQuery(context, loftFaces);
    
    for (var i = 0; i < size(faces); i += 1)
    {
        const face = faces[i];
        
        // Sample face at midpoint to get a representative plane
        // If this fails, just skip this face - it's an optional optimization
        const faceTangentPlaneResult = try silent(evFaceTangentPlane(context, {
            "face" : face,
            "parameter" : vector(0.5, 0.5)
        }));
        
        if (faceTangentPlaneResult == undefined)
        {
            continue;
        }
        
        // Get the boundary edges of this face
        const boundaryEdges = qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE);
        
        // Check if we can create a planar surface - need at least 3 edges
        const edgeCount = evaluateQueryCount(context, boundaryEdges);
        
        if (edgeCount >= 3 && edgeCount <= 4)
        {
            // Try to create a planar surface using opFillSurface with the boundary edges
            const fillId = id + ("fill" ~ i);
            const fillResult = try silent(opFillSurface(context, fillId, {
                "surfaceEdges" : boundaryEdges
            }));
            
            // Only delete original if fill succeeded
            if (fillResult != undefined)
            {
                try silent(opDeleteBodies(context, id + ("deleteOld" ~ i), {
                    "entities" : face
                }));
            }
        }
    }
}

/**
 * Create a quadrilateral loft by generating ruled surfaces between matched segments.
 * Creates polylines from the matched points and lofts between adjacent pairs.
 */
function createQuadrilateralLoft(context is Context, id is Id, profile1Points is array, profile2Points is array)
{
    const numPoints = min(size(profile1Points), size(profile2Points));
    
    if (numPoints < 2)
    {
        throw regenError("Insufficient points to create loft");
    }
    
    // Now create ruled surface lofts between corresponding segments
    const numSegments = numPoints - 1;
    
    for (var i = 0; i < numSegments; i += 1)
    {
        // Check if this segment is degenerate (points too close)
        const dist1 = norm(profile1Points[i + 1] - profile1Points[i]);
        const dist2 = norm(profile2Points[i + 1] - profile2Points[i]);
        
        if (dist1 < TOLERANCE.zeroLength * meter || dist2 < TOLERANCE.zeroLength * meter)
        {
            // Skip degenerate segments
            continue;
        }
        
        // Create individual line segments for this quad face
        const seg1Id = id + ("seg1_" ~ i);
        const seg2Id = id + ("seg2_" ~ i);
        
        // Create line segment on profile 1 between points i and i+1
        opFitSpline(context, seg1Id, {
            "points" : [profile1Points[i], profile1Points[i + 1]]
        });
        
        // Create line segment on profile 2 between points i and i+1
        opFitSpline(context, seg2Id, {
            "points" : [profile2Points[i], profile2Points[i + 1]]
        });
        
        // Loft between these two segments to create a quadrilateral surface
        // If loft fails (degenerate segment), skip it
        try silent(opLoft(context, id + ("loft" ~ i), {
            "profileSubqueries" : [
                qCreatedBy(seg1Id, EntityType.EDGE),
                qCreatedBy(seg2Id, EntityType.EDGE)
            ],
            "bodyType" : ToolBodyType.SURFACE
        }));
    }
    
    // Clean up helper curves
    opDeleteBodies(context, id + "cleanup", {
        "entities" : qCreatedBy(id, EntityType.BODY)->qBodyType(BodyType.WIRE)
    });
}
