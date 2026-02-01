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
        
        // Determine segment parameters based on combined angular changes from both profiles
        const segmentParameters = computeUnifiedSegmentParameters(context, profile1Edges, profile2Edges, 
                                                                   definition.facetAngle, definition.minSegments);
        
        // Sample both profiles at the same relative parameters to maintain alignment
        const profile1Points = sampleCurveAtParameters(context, profile1Edges, segmentParameters);
        const profile2Points = sampleCurveAtParameters(context, profile2Edges, segmentParameters);
        
        // Create quadrilateral loft between aligned segments
        createQuadrilateralLoft(context, id, profile1Points, profile2Points);
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
 * Compute unified segment parameters by analyzing angular changes across both profiles.
 * This ensures corresponding segments on both profiles represent similar curve positions.
 * 
 * @param context : The context
 * @param curve1Edges : Query for first curve edges
 * @param curve2Edges : Query for second curve edges
 * @param facetAngle : Angular change threshold
 * @param minSegments : Minimum number of segments
 * @returns Array of normalized parameters (0 to 1) where segments should be created
 */
function computeUnifiedSegmentParameters(context is Context, 
                                         curve1Edges is Query, 
                                         curve2Edges is Query,
                                         facetAngle is ValueWithUnits, 
                                         minSegments is number) returns array
{
    // Get total lengths for both curves
    const edges1 = evaluateQuery(context, curve1Edges);
    const edges2 = evaluateQuery(context, curve2Edges);
    
    var totalLength1 = 0 * meter;
    for (var edge in edges1)
    {
        totalLength1 += evLength(context, { "entities" : edge });
    }
    
    var totalLength2 = 0 * meter;
    for (var edge in edges2)
    {
        totalLength2 += evLength(context, { "entities" : edge });
    }
    
    // Walk both curves simultaneously tracking angular changes
    // Use normalized parameter space (0 to 1) for both curves
    var segmentParams = [0.0];
    var currentParam = 0.0;
    
    // Track accumulated angles for both curves
    var accumulatedAngle1 = 0.0 * radian;
    var accumulatedAngle2 = 0.0 * radian;
    
    // Get initial tangents for both curves
    var tangent1 = evEdgeTangentLine(context, {
        "edge" : edges1[0],
        "parameter" : 0.0,
        "arcLengthParameterization" : true
    }).direction;
    
    var tangent2 = evEdgeTangentLine(context, {
        "edge" : edges2[0],
        "parameter" : 0.0,
        "arcLengthParameterization" : true
    }).direction;
    
    var previousTangent1 = tangent1;
    var previousTangent2 = tangent2;
    
    const parameterStep = 0.01; // 1% steps
    currentParam = parameterStep;
    
    while (currentParam <= 1.0)
    {
        // Sample tangents at current parameter on both curves
        const tangent1Current = sampleTangentAtParameter(context, edges1, currentParam);
        const tangent2Current = sampleTangentAtParameter(context, edges2, currentParam);
        
        // Calculate angle changes for both curves
        const angle1Change = acos(max(-1.0, min(1.0, dot(previousTangent1, tangent1Current))));
        const angle2Change = acos(max(-1.0, min(1.0, dot(previousTangent2, tangent2Current))));
        
        accumulatedAngle1 += angle1Change;
        accumulatedAngle2 += angle2Change;
        
        // Use the maximum accumulated angle to drive segmentation
        const maxAccumulatedAngle = max(accumulatedAngle1, accumulatedAngle2);
        
        if (maxAccumulatedAngle >= facetAngle)
        {
            segmentParams = append(segmentParams, currentParam);
            accumulatedAngle1 = 0.0 * radian;
            accumulatedAngle2 = 0.0 * radian;
        }
        
        previousTangent1 = tangent1Current;
        previousTangent2 = tangent2Current;
        currentParam = currentParam + parameterStep;
    }
    
    // Always add end parameter if not already present
    if (segmentParams[size(segmentParams) - 1] < 1.0)
    {
        segmentParams = append(segmentParams, 1.0);
    }
    
    // Ensure minimum segment count
    if (size(segmentParams) - 1 < minSegments)
    {
        segmentParams = [];
        for (var i = 0; i <= minSegments; i += 1)
        {
            segmentParams = append(segmentParams, i / minSegments);
        }
    }
    
    return segmentParams;
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
 * Sample curve positions at given normalized parameters.
 * This ensures both curves are sampled at corresponding relative positions.
 */
function sampleCurveAtParameters(context is Context, curveEdges is Query, normalizedParams is array) returns array
{
    const edges = evaluateQuery(context, curveEdges);
    
    // Calculate cumulative lengths
    var cumulativeLengths = [0 * meter];
    var totalLength = 0 * meter;
    
    for (var edge in edges)
    {
        const edgeLength = evLength(context, { "entities" : edge });
        totalLength += edgeLength;
        cumulativeLengths = append(cumulativeLengths, totalLength);
    }
    
    var points = [];
    
    for (var normalizedParam in normalizedParams)
    {
        const targetLength = normalizedParam * totalLength;
        
        // Find which edge contains this parameter
        for (var i = 0; i < size(edges); i += 1)
        {
            if (cumulativeLengths[i + 1] >= targetLength || normalizedParam >= 1.0)
            {
                // This edge contains the target parameter
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
                
                points = append(points, tangentLine.origin);
                break;
            }
        }
    }
    
    return points;
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
    
    // Create 3D polylines for both profiles using fitSpline
    const profile1PolylineId = id + "profile1Polyline";
    opFitSpline(context, profile1PolylineId, {
        "points" : profile1Points,
        "tolerance" : 0 * meter
    });
    
    const profile2PolylineId = id + "profile2Polyline";
    opFitSpline(context, profile2PolylineId, {
        "points" : profile2Points,
        "tolerance" : 0 * meter
    });
    
    // Now create ruled surface lofts between corresponding segments
    const numSegments = numPoints - 1;
    
    for (var i = 0; i < numSegments; i += 1)
    {
        // Create individual line segments for this quad face
        const seg1Id = id + ("seg1_" ~ i);
        const seg2Id = id + ("seg2_" ~ i);
        
        // Create line segment on profile 1 between points i and i+1
        opFitSpline(context, seg1Id, {
            "points" : [profile1Points[i], profile1Points[i + 1]],
            "tolerance" : 0 * meter
        });
        
        // Create line segment on profile 2 between points i and i+1
        opFitSpline(context, seg2Id, {
            "points" : [profile2Points[i], profile2Points[i + 1]],
            "tolerance" : 0 * meter
        });
        
        // Loft between these two segments to create a quadrilateral surface
        try
        {
            opLoft(context, id + ("loft" ~ i), {
                "profileSubqueries" : [
                    qCreatedBy(seg1Id, EntityType.EDGE),
                    qCreatedBy(seg2Id, EntityType.EDGE)
                ],
                "bodyType" : ToolBodyType.SURFACE
            });
        }
        catch (error)
        {
            // If loft fails for this segment, continue to next
            // This can happen for degenerate segments
        }
    }
    
    // Clean up helper curves
    opDeleteBodies(context, id + "cleanup", {
        "entities" : qUnion([
            qCreatedBy(profile1PolylineId, EntityType.BODY),
            qCreatedBy(profile2PolylineId, EntityType.BODY),
            qCreatedBy(id, EntityType.BODY)->qBodyType(BodyType.WIRE)
        ])
    });
}
