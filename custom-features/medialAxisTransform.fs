FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");

/**
 * Medial Axis Transform for Planar Domains
 * 
 * Implementation of the algorithm described in:
 * "Performant Medial Axis Transform for Planar Domains"
 * 
 * This algorithm computes the medial axis (skeleton) of a planar region using a grass-fire
 * simulation model. The implementation uses the Reach to Locus of First collision in the 
 * grass-fire Simulation (RLFS) function as an implicit representation of the medial axis.
 * 
 * Time complexity: O((1 + genus) * n) where:
 *   - genus = number of holes in the region
 *   - n = number of boundary segments
 * 
 * The algorithm has three main phases:
 * 1. Boundary discretization with adaptive sampling
 * 2. RLFS function computation via fire-front collision tests
 * 3. Explicit medial axis extraction from RLFS
 */

annotation { "Feature Type Name" : "Medial Axis Transform" }
export const medialAxisTransform = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Planar face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.planarFace is Query;
        
        annotation { "Name" : "Sampling density", "Description" : "Number of samples per unit length for adaptive boundary discretization" }
        isInteger(definition.samplingDensity, POSITIVE_COUNT_BOUNDS);
        
        annotation { "Name" : "Show debug visualization", "Default" : true }
        definition.showDebug is boolean;
        
        annotation { "Name" : "Advanced settings", "Default" : false }
        definition.showAdvanced is boolean;
        
        if (definition.showAdvanced)
        {
            annotation { "Name" : "Curvature threshold", "Description" : "Threshold for adaptive sampling based on curvature" }
            isReal(definition.curvatureThreshold, POSITIVE_REAL_BOUNDS);
            
            annotation { "Name" : "Generate offset curves", "Default" : false }
            definition.generateOffsetCurves is boolean;
        }
    }
    {
        // Verify face is planar
        const faceDefinition = evFaceDefinition(context, { "face" : definition.planarFace });
        if (faceDefinition.surfaceType != SurfaceType.PLANE)
        {
            throw regenError("Selected face must be planar", ["planarFace"]);
        }
        
        // Set default values for advanced settings
        const curvatureThreshold = definition.showAdvanced ? definition.curvatureThreshold : 0.1;
        const generateOffsetCurves = definition.showAdvanced && definition.generateOffsetCurves;
        
        println("=== Medial Axis Transform ===");
        println("Computing medial axis for planar face...");
        
        // Phase 1: Extract and discretize boundary
        const boundarySegments = discretizeBoundary(context, definition.planarFace, definition.samplingDensity, curvatureThreshold);
        println("Boundary discretized into " ~ @size(boundarySegments) ~ " segments");
        
        // Phase 2: Compute RLFS function (implicit medial axis)
        const startTime = getCurrentTime();
        computeRLFSFunction(context, boundarySegments);
        const rlfsTime = getCurrentTime() - startTime;
        println("RLFS computation time: " ~ rlfsTime ~ " ms");
        
        // Phase 3: Extract explicit medial axis
        const medialAxisGraph = extractExplicitMedialAxis(context, boundarySegments);
        println("Medial axis extracted: " ~ @size(medialAxisGraph.nodes) ~ " nodes, " ~ @size(medialAxisGraph.edges) ~ " edges");
        
        // Phase 4: Create sketch entities for visualization
        const sketchPlane = evFaceTangentPlane(context, { "face" : definition.planarFace, "parameter" : vector(0.5, 0.5) });
        visualizeMedialAxis(context, id, medialAxisGraph, sketchPlane, definition.showDebug);
        
        if (generateOffsetCurves)
        {
            // Optional: Generate offset curves using RLFS function
            generateOffsetCurvesFromRLFS(context, id + "offsetCurves", boundarySegments, sketchPlane);
        }
        
        println("=== Medial Axis Transform Complete ===");
    });

// ==================== Data Structures ====================

/**
 * RLFS piece representation - a piecewise linear approximation of the
 * Reach to Locus of First collision in the grass-fire Simulation function
 * 
 * @field parameterStart : Start parameter t on the segment (0 to 1)
 * @field displacementStart : Distance d to first collision at start
 * @field parameterEnd : End parameter t on the segment (0 to 1)
 * @field displacementEnd : Distance d to first collision at end
 * @field peerSegmentIndex : Index of segment that caused this collision (PSI)
 * @field peerParameterStart : Parameter s on peer segment at start
 * @field peerParameterEnd : Parameter s on peer segment at end
 * @field edgeIndex : Index of MA edge this piece corresponds to (-1 if undefined)
 * @field reverseEndpoints : Flag indicating whether endpoints should be reversed for this piece
 */
export type RLFSPiece typecheck canBeRLFSPiece;

export predicate canBeRLFSPiece(value)
{
    value is map;
    value.parameterStart is number;
    value.displacementStart is ValueWithUnits;
    value.parameterEnd is number;
    value.displacementEnd is ValueWithUnits;
    value.peerSegmentIndex is number;
    value.peerParameterStart is number;
    value.peerParameterEnd is number;
    value.edgeIndex is number;
    value.reverseEndpoints is boolean;
}

/**
 * Boundary segment representation
 * 
 * @field startPoint : 3D position of segment start with length units
 * @field endPoint : 3D position of segment end with length units
 * @field startNormal : Normal vector at start (unit vector)
 * @field endNormal : Normal vector at end (unit vector)
 * @field rlfsPieces : Ordered list of RLFS pieces on this segment
 * @field isDummy : Flag indicating if this is a dummy segment from reflex vertex splitting
 * @field segmentIndex : Index of this segment in the global list
 */
export type BoundarySegment typecheck canBeBoundarySegment;

export predicate canBeBoundarySegment(value)
{
    value is map;
    is3dLengthVector(value.startPoint);
    is3dLengthVector(value.endPoint);
    is3dDirection(value.startNormal);
    is3dDirection(value.endNormal);
    value.rlfsPieces is array;
    value.isDummy is boolean;
    value.segmentIndex is number;
}

/**
 * Medial axis node representation
 * 
 * @field position : 3D position of the node with length units
 * @field radius : Radius of maximal disc at this node with length units
 * @field contributionCount : Number of edge endpoints that contributed to this node's attributes
 */
export type MANode typecheck canBeMANode;

export predicate canBeMANode(value)
{
    value is map;
    is3dLengthVector(value.position);
    isLength(value.radius);
    value.contributionCount is number;
}

/**
 * Medial axis edge representation
 * 
 * @field startNodeIndex : Index of start node in nodes array
 * @field endNodeIndex : Index of end node in nodes array
 */
export type MAEdge typecheck canBeMAEdge;

export predicate canBeMAEdge(value)
{
    value is map;
    value.startNodeIndex is number;
    value.endNodeIndex is number;
}

/**
 * Medial axis graph representation
 * 
 * @field nodes : Array of MA nodes
 * @field edges : Array of MA edges
 */
export type MAGraph typecheck canBeMAGraph;

export predicate canBeMAGraph(value)
{
    value is map;
    value.nodes is array;
    value.edges is array;
}

// ==================== Phase 1: Boundary Discretization ====================

/**
 * Discretize the boundary of a planar face into straight-line segments.
 * Uses adaptive sampling based on curvature for smooth portions.
 * Handles reflex vertices by splitting them into dummy segments with smoothly varying normals.
 * 
 * @param context : The context
 * @param planarFace : Query for the planar face
 * @param samplingDensity : Number of samples per unit length
 * @param curvatureThreshold : Threshold for adaptive sampling
 * @returns {array} : Array of BoundarySegment objects
 */
function discretizeBoundary(context is Context, planarFace is Query, samplingDensity is number, curvatureThreshold is number) returns array
{
    var segments = [];
    
    // Get boundary edges of the face
    const boundaryEdges = qOwnedByBody(qEdgeTopologyFilter(qOwnerEdge(planarFace), EdgeTopology.FULL), qUnionQuery(planarFace));
    const edges = evaluateQuery(context, boundaryEdges);
    
    if (@size(edges) == 0)
    {
        throw "No boundary edges found on planar face";
    }
    
    println("Processing " ~ @size(edges) ~ " boundary edges");
    
    // Process each boundary edge
    for (var i = 0; i < @size(edges); i += 1)
    {
        const edge = edges[i];
        const edgeSegments = discretizeEdge(context, edge, samplingDensity, curvatureThreshold);
        segments = concatenate(segments, edgeSegments);
    }
    
    // Process reflex vertices and insert dummy segments
    // Reflex vertices have inner angle > π and need special handling
    segments = processReflexVertices(segments);
    
    // Assign segment indices after reflex vertex processing
    for (var i = 0; i < @size(segments); i += 1)
    {
        segments[i].segmentIndex = i;
    }
    
    return segments;
}

/**
 * Discretize a single edge into segments using adaptive sampling.
 * 
 * @param context : The context
 * @param edge : Query for the edge
 * @param samplingDensity : Number of samples per unit length
 * @param curvatureThreshold : Threshold for adaptive sampling
 * @returns {array} : Array of BoundarySegment objects for this edge
 */
function discretizeEdge(context is Context, edge is Query, samplingDensity is number, curvatureThreshold is number) returns array
{
    var segments = [];
    
    // Get edge definition and length
    const edgeDefinition = evCurveDefinition(context, { "edge" : edge });
    const edgeLength = evLength(context, { "entities" : edge });
    
    // For line segments, create single segment
    if (edgeDefinition.curveType == CurveType.LINE)
    {
        const startTangent = evEdgeTangentLine(context, { "edge" : edge, "parameter" : 0.0, "arcLengthParameterization" : true });
        const endTangent = evEdgeTangentLine(context, { "edge" : edge, "parameter" : 1.0, "arcLengthParameterization" : true });
        
        // For a line segment, normal is perpendicular to tangent
        // We need to determine the inward normal direction based on face orientation
        const startNormal = computeInwardNormal(context, edge, startTangent.direction, 0.0);
        const endNormal = computeInwardNormal(context, edge, endTangent.direction, 1.0);
        
        segments = append(segments, {
            "startPoint" : startTangent.origin,
            "endPoint" : endTangent.origin,
            "startNormal" : startNormal,
            "endNormal" : endNormal,
            "rlfsPieces" : [],
            "isDummy" : false,
            "segmentIndex" : -1
        } as BoundarySegment);
    }
    else
    {
        // For curved edges, use adaptive sampling based on curvature
        segments = adaptiveSampleCurvedEdge(context, edge, edgeLength, samplingDensity, curvatureThreshold);
    }
    
    return segments;
}

/**
 * Adaptively sample a curved edge based on curvature.
 * 
 * @param context : The context
 * @param edge : Query for the edge
 * @param edgeLength : Total length of the edge
 * @param samplingDensity : Base number of samples per unit length
 * @param curvatureThreshold : Threshold for adaptive refinement
 * @returns {array} : Array of BoundarySegment objects
 */
function adaptiveSampleCurvedEdge(context is Context, edge is Query, edgeLength is ValueWithUnits, 
                                  samplingDensity is number, curvatureThreshold is number) returns array
{
    var segments = [];
    
    // Calculate base number of samples based on edge length and density
    const baseNumSamples = max(10, floor(samplingDensity * edgeLength / meter));
    
    // Sample the edge at regular intervals
    var samplePoints = [];
    var sampleNormals = [];
    var sampleParameters = [];
    
    for (var i = 0; i <= baseNumSamples; i += 1)
    {
        const parameter = i / baseNumSamples;
        const tangentLine = evEdgeTangentLine(context, { "edge" : edge, "parameter" : parameter, "arcLengthParameterization" : true });
        const normal = computeInwardNormal(context, edge, tangentLine.direction, parameter);
        
        samplePoints = append(samplePoints, tangentLine.origin);
        sampleNormals = append(sampleNormals, normal);
        sampleParameters = append(sampleParameters, parameter);
    }
    
    // Perform adaptive refinement based on curvature
    samplePoints, sampleNormals, sampleParameters = refineBasedOnCurvature(context, edge, samplePoints, sampleNormals, sampleParameters, curvatureThreshold);
    
    // Create segments from sample points
    for (var i = 0; i < @size(samplePoints) - 1; i += 1)
    {
        segments = append(segments, {
            "startPoint" : samplePoints[i],
            "endPoint" : samplePoints[i + 1],
            "startNormal" : sampleNormals[i],
            "endNormal" : sampleNormals[i + 1],
            "rlfsPieces" : [],
            "isDummy" : false,
            "segmentIndex" : -1
        } as BoundarySegment);
    }
    
    return segments;
}

/**
 * Refine sampling based on curvature - add more samples in high curvature regions.
 * 
 * @param context : The context
 * @param edge : Query for the edge
 * @param points : Array of sample points
 * @param normals : Array of normal vectors
 * @param parameters : Array of parameters
 * @param curvatureThreshold : Threshold for refinement
 * @returns {array, array, array} : Refined arrays of points, normals, and parameters
 */
function refineBasedOnCurvature(context is Context, edge is Query, points is array, normals is array, 
                               parameters is array, curvatureThreshold is number) returns array
{
    // Simple refinement: check angle change between consecutive segments
    // If angle change is large, add midpoint sample
    var refinedPoints = [points[0]];
    var refinedNormals = [normals[0]];
    var refinedParameters = [parameters[0]];
    
    for (var i = 0; i < @size(points) - 1; i += 1)
    {
        const currentNormal = normals[i];
        const nextNormal = normals[i + 1];
        
        // Calculate angle change between normals
        const dotProduct = dot(currentNormal, nextNormal);
        const angleChange = acos(max(-1.0, min(1.0, dotProduct)));
        
        // If angle change exceeds threshold, add midpoint
        if (abs(angleChange) > curvatureThreshold)
        {
            const midParameter = (parameters[i] + parameters[i + 1]) / 2.0;
            const midTangent = evEdgeTangentLine(context, { "edge" : edge, "parameter" : midParameter, "arcLengthParameterization" : true });
            const midNormal = computeInwardNormal(context, edge, midTangent.direction, midParameter);
            
            refinedPoints = append(refinedPoints, midTangent.origin);
            refinedNormals = append(refinedNormals, midNormal);
            refinedParameters = append(refinedParameters, midParameter);
        }
        
        // Add the next point
        refinedPoints = append(refinedPoints, points[i + 1]);
        refinedNormals = append(refinedNormals, normals[i + 1]);
        refinedParameters = append(refinedParameters, parameters[i + 1]);
    }
    
    return refinedPoints, refinedNormals, refinedParameters;
}

/**
 * Compute the inward normal direction for a boundary edge.
 * The inward normal points toward the interior of the face.
 * 
 * @param context : The context
 * @param edge : Query for the edge
 * @param tangent : Tangent direction vector
 * @param parameter : Parameter along edge (0 to 1)
 * @returns {Vector} : Unit inward normal vector
 */
function computeInwardNormal(context is Context, edge is Query, tangent is Vector, parameter is number) returns Vector
{
    // Get the face that owns this edge
    const owningFace = qOwnerEdge(edge);
    const faces = evaluateQuery(context, owningFace);
    
    if (@size(faces) == 0)
    {
        throw "Edge has no owning face";
    }
    
    const face = faces[0];
    
    // Get face normal at a point near the edge
    const edgeTangentLine = evEdgeTangentLine(context, { "edge" : edge, "parameter" : parameter, "arcLengthParameterization" : true });
    const faceNormal = evFaceNormalAtEdge(context, { "edge" : edge, "face" : face, "parameter" : parameter });
    
    // Compute perpendicular to tangent in the plane of the face
    // For a planar face, the inward normal is: faceNormal × tangent
    const perpendicular = cross(faceNormal, tangent);
    
    // Normalize to unit vector
    return normalize(perpendicular);
}

/**
 * Process reflex vertices and insert dummy segments with smoothly varying normals.
 * 
 * A reflex vertex is one where the inner angle is greater than π (180 degrees).
 * According to the paper, reflex vertices must be split into dummy zero-length segments
 * to ensure the boundary remains closed during grass-fire simulation.
 * 
 * Number of dummy segments = ceil((innerAngle - π) / (π/10))  [one per π/18 radians as default]
 * 
 * @param segments : Array of BoundarySegment objects
 * @returns {array} : Array with dummy segments inserted at reflex vertices
 */
function processReflexVertices(segments is array) returns array
{
    if (@size(segments) == 0)
    {
        return segments;
    }
    
    var processedSegments = [];
    const numSegments = @size(segments);
    
    for (var i = 0; i < numSegments; i += 1)
    {
        const currentSegment = segments[i];
        const nextSegment = segments[(i + 1) % numSegments];
        
        // Check if junction between current and next is a reflex vertex
        // At junction: currentSegment.endPoint == nextSegment.startPoint
        
        // Calculate inner angle at junction
        const dir1 = normalize(currentSegment.endPoint - currentSegment.startPoint);
        const dir2 = normalize(nextSegment.endPoint - nextSegment.startPoint);
        
        // Use cross product to determine turn direction
        // In 2D (assuming planar): crossZ = dir1.x * dir2.y - dir1.y * dir2.x
        const crossZ = dir1[0] * dir2[1] - dir1[1] * dir2[0];
        
        // Use dot product to get angle magnitude
        const dotProduct = dot(dir1, dir2);
        const angle = acos(max(-1.0, min(1.0, dotProduct)));
        
        // Determine inner angle
        // If crossZ < 0, it's a right turn (convex), inner angle < π
        // If crossZ > 0, it's a left turn, could be reflex if angle > π/2
        var innerAngle = angle;
        if (crossZ < 0)
        {
            // Right turn - this is typically convex
            innerAngle = angle;
        }
        else
        {
            // Left turn or straight - check if reflex
            innerAngle = 2 * PI - angle;
        }
        
        // Add current segment
        processedSegments = append(processedSegments, currentSegment);
        
        // Check if this is a reflex vertex (inner angle > π)
        if (innerAngle > PI + 0.01) // Small tolerance
        {
            // Calculate number of dummy segments
            const angleDifference = innerAngle - PI;
            const numDummies = max(1, ceil(angleDifference / (PI / 10.0)));
            
            // Get normals at junction
            const normal1 = currentSegment.endNormal;
            const normal2 = nextSegment.startNormal;
            
            // Create dummy segments with smoothly varying normals
            const junctionPoint = currentSegment.endPoint;
            
            for (var j = 1; j <= numDummies; j += 1)
            {
                const t = j / (numDummies + 1.0);
                
                // Interpolate normal direction
                const interpolatedNormal = normalize((1.0 - t) * normal1 + t * normal2);
                
                // Create zero-length dummy segment
                const dummySegment = {
                    "startPoint" : junctionPoint,
                    "endPoint" : junctionPoint,
                    "startNormal" : interpolatedNormal,
                    "endNormal" : interpolatedNormal,
                    "rlfsPieces" : [],
                    "isDummy" : true,
                    "segmentIndex" : -1
                } as BoundarySegment;
                
                processedSegments = append(processedSegments, dummySegment);
            }
            
            println("Inserted " ~ numDummies ~ " dummy segments at reflex vertex (inner angle: " ~ (innerAngle / degree) ~ " degrees)");
        }
    }
    
    return processedSegments;
}

// ==================== Phase 2: RLFS Computation ====================

/**
 * Compute the RLFS (Reach to Locus of First collision) function for all boundary segments.
 * This implements the grass-fire simulation via collision tests between segment pairs.
 * 
 * The RLFS function is computed in three main steps:
 * 1. Find MA Extreme Points - locate starting points for MA branches
 * 2. Follow MA - track MA branches by marching along boundary
 * 3. Fix MA - handle holes by finding peer segments for undefined RLFS regions
 * 
 * @param context : The context
 * @param boundarySegments : Array of BoundarySegment objects (modified in place)
 */
function computeRLFSFunction(context is Context, boundarySegments is array)
{
    println("Computing RLFS function...");
    
    // Step 1: Find MA extreme points and track branches
    findMAExtremePointsAndTrack(context, boundarySegments);
    
    // Step 2: Fix MA for regions with holes (segments with undefined RLFS)
    fixMAForHoles(context, boundarySegments);
    
    println("RLFS function computation complete");
}

/**
 * Find MA extreme points (convex vertices and LMPC regions) and track branches.
 * This is the main routine that marches along the boundary looking for MA starting points.
 * 
 * @param context : The context
 * @param boundarySegments : Array of BoundarySegment objects (modified in place)
 */
function findMAExtremePointsAndTrack(context is Context, boundarySegments is array)
{
    const numSegments = @size(boundarySegments);
    
    // March along boundary looking for MA extreme points
    for (var i = 0; i < numSegments; i += 1)
    {
        const currentSegment = boundarySegments[i];
        const nextSegment = boundarySegments[(i + 1) % numSegments];
        
        // Check for collision between adjacent segments (Type 3 collision)
        // This occurs at regions of locally maximal positive curvature (LMPC)
        const adjacentCollision = testAdjacentSegmentCollision(currentSegment, nextSegment);
        
        if (adjacentCollision.collides)
        {
            // Found an MA extreme point - track branch from here
            followMABranch(context, boundarySegments, i, (i + 1) % numSegments);
        }
        
        // Check for convex vertex at segment junction
        if (isConvexVertex(currentSegment, nextSegment))
        {
            // Convex vertex is also an MA extreme point - track branch
            followMABranch(context, boundarySegments, i, (i + 1) % numSegments);
        }
    }
}

/**
 * Test for collision between two adjacent boundary segments.
 * Returns collision information if segments collide during grass-fire propagation.
 * 
 * @param segment1 : First segment
 * @param segment2 : Second segment (adjacent to segment1)
 * @returns {map} : Map with "collides" boolean and collision details
 */
function testAdjacentSegmentCollision(segment1 is BoundarySegment, segment2 is BoundarySegment) returns map
{
    // Type 3 collision: D crosses AB where D is from CD and AB are adjacent segments
    // For adjacent segments sharing an endpoint, test if end of segment2 crosses segment1
    
    const collision = solveEndpointSegmentCollision(
        segment2.endPoint,
        segment2.endNormal,
        segment1.startPoint,
        segment1.endPoint,
        segment1.startNormal,
        segment1.endNormal
    );
    
    return collision;
}

/**
 * Check if junction between two segments forms a convex vertex.
 * A convex vertex has an inner angle less than π.
 * 
 * @param segment1 : First segment
 * @param segment2 : Second segment (adjacent to segment1)
 * @returns {boolean} : True if junction is a convex vertex
 */
function isConvexVertex(segment1 is BoundarySegment, segment2 is BoundarySegment) returns boolean
{
    // Calculate the angle between the segments
    const dir1 = normalize(segment1.endPoint - segment1.startPoint);
    const dir2 = normalize(segment2.endPoint - segment2.startPoint);
    
    // Use cross product to determine if angle is convex (inner angle < π)
    // For 2D in a plane, we check the z-component of cross product
    // Positive means convex (counterclockwise turn)
    const crossZ = dir1[0] * dir2[1] - dir1[1] * dir2[0];
    
    return crossZ > 0;
}

/**
 * Solve for endpoint-segment collision during grass-fire propagation.
 * 
 * This implements Equation 1 from the paper:
 * P + d*N = (1-t)(P1 + d*N1) + t(P2 + d*N2)
 * 
 * where:
 *   P = endpoint position
 *   N = endpoint normal (unit vector)
 *   P1, P2 = segment endpoints
 *   N1, N2 = segment normals (unit vectors)
 *   d = displacement distance (unknown, with length units)
 *   t = parameter on segment [0,1] (unknown, unitless)
 * 
 * The paper states this leads to a quadratic equation via substitution method.
 * We solve algebraically for both solutions and check validity.
 * 
 * @param endpointPos : Position of the endpoint
 * @param endpointNormal : Normal at the endpoint (unit vector)
 * @param segmentStart : Start position of the segment
 * @param segmentEnd : End position of the segment
 * @param segmentNormalStart : Normal at segment start (unit vector)
 * @param segmentNormalEnd : Normal at segment end (unit vector)
 * @returns {map} : Map with "collides" boolean, "distance" d, and "parameter" t if collision occurs
 */
function solveEndpointSegmentCollision(endpointPos is Vector, endpointNormal is Vector,
                                      segmentStart is Vector, segmentEnd is Vector,
                                      segmentNormalStart is Vector, segmentNormalEnd is Vector) returns map
{
    const P = endpointPos;
    const N = endpointNormal;
    const P1 = segmentStart;
    const P2 = segmentEnd;
    const N1 = segmentNormalStart;
    const N2 = segmentNormalEnd;
    
    // Extract components for 2D solution (work in plane)
    // Using first two components (x, y) assuming planar configuration
    const px = P[0] / meter;
    const py = P[1] / meter;
    const nx = N[0];
    const ny = N[1];
    const p1x = P1[0] / meter;
    const p1y = P1[1] / meter;
    const p2x = P2[0] / meter;
    const p2y = P2[1] / meter;
    const n1x = N1[0];
    const n1y = N1[1];
    const n2x = N2[0];
    const n2y = N2[1];
    
    // Equation (unitless after dividing by meter):
    // px + d*nx = (1-t)*p1x + t*p2x + d*((1-t)*n1x + t*n2x)
    // py + d*ny = (1-t)*p1y + t*p2y + d*((1-t)*n1y + t*n2y)
    
    // Rearranging:
    // px - p1x + t*(p1x - p2x) = d*(n1x - nx + t*(n2x - n1x))
    // py - p1y + t*(p1y - p2y) = d*(n1y - ny + t*(n2y - n1y))
    
    // Define: ax = px - p1x, ay = py - p1y
    //         bx = p1x - p2x, by = p1y - p2y
    //         cx = n1x - nx, cy = n1y - ny
    //         dx = n2x - n1x, dy = n2y - n1y
    
    const ax = px - p1x;
    const ay = py - p1y;
    const bx = p1x - p2x;
    const by = p1y - p2y;
    const cx = n1x - nx;
    const cy = n1y - ny;
    const dx = n2x - n1x;
    const dy = n2y - n1y;
    
    // System: ax + t*bx = d*(cx + t*dx)
    //         ay + t*by = d*(cy + t*dy)
    
    // Eliminate t by cross-multiplication:
    // (ax + t*bx)*(cy + t*dy) = (ay + t*by)*(cx + t*dx)
    
    // Expanding:
    // ax*cy + ax*t*dy + t*bx*cy + t^2*bx*dy = ay*cx + ay*t*dx + t*by*cx + t^2*by*dx
    
    // Collecting by powers of t:
    // t^2: bx*dy - by*dx
    // t: ax*dy + bx*cy - ay*dx - by*cx  
    // 1: ax*cy - ay*cx
    
    // This gives: At^2 + Bt + C = 0 where:
    const At = bx * dy - by * dx;
    const Bt = ax * dy + bx * cy - ay * dx - by * cx;
    const Ct = ax * cy - ay * cx;
    
    const tolerance = 1e-12;
    var tSolutions = [];
    
    if (abs(At) < tolerance)
    {
        // Linear in t: Bt + C = 0
        if (abs(Bt) > tolerance)
        {
            const t = -Ct / Bt;
            tSolutions = append(tSolutions, t);
        }
    }
    else
    {
        // Quadratic in t: solve At^2 + Bt + C = 0
        const discriminant = Bt * Bt - 4.0 * At * Ct;
        
        if (discriminant >= 0)
        {
            const sqrtDisc = sqrt(discriminant);
            const t1 = (-Bt + sqrtDisc) / (2.0 * At);
            const t2 = (-Bt - sqrtDisc) / (2.0 * At);
            
            tSolutions = append(tSolutions, t1);
            if (abs(t1 - t2) > 1e-9)
            {
                tSolutions = append(tSolutions, t2);
            }
        }
    }
    
    // For each valid t, solve for d and verify
    var bestSolution = { "collides" : false, "distance" : 0 * meter, "parameter" : 0.0 };
    var minDistance = 1e10 * meter;
    
    for (var i = 0; i < @size(tSolutions); i += 1)
    {
        const t = tSolutions[i];
        
        // Check if t is in valid range
        if (t < 0.0 || t > 1.0)
        {
            continue;
        }
        
        // Solve for d from: ax + t*bx = d*(cx + t*dx)
        const denominator = cx + t * dx;
        
        if (abs(denominator) < tolerance)
        {
            // Try y equation instead
            const denominatorY = cy + t * dy;
            if (abs(denominatorY) < tolerance)
            {
                continue; // Singular case
            }
            const d = (ay + t * by) / denominatorY;
            
            if (d > 0)
            {
                // Verify with x equation
                const check = ax + t * bx - d * (cx + t * dx);
                if (abs(check) < 1e-6 && d * meter < minDistance)
                {
                    minDistance = d * meter;
                    bestSolution = {
                        "collides" : true,
                        "distance" : d * meter,
                        "parameter" : t
                    };
                }
            }
        }
        else
        {
            const d = (ax + t * bx) / denominator;
            
            if (d > 0)
            {
                // Verify with y equation
                const check = ay + t * by - d * (cy + t * dy);
                if (abs(check) < 1e-6 && d * meter < minDistance)
                {
                    minDistance = d * meter;
                    bestSolution = {
                        "collides" : true,
                        "distance" : d * meter,
                        "parameter" : t
                    };
                }
            }
        }
    }
    
    return bestSolution;
}

/**
 * Follow an MA branch by walking along boundary in opposite directions from a starting pair.
 * This implements the Follow MA routine from the paper (Section 3.4.3).
 * 
 * The algorithm alternates between:
 * 1. Keeping left fixed, moving right forward (next direction)
 * 2. Keeping right fixed, moving left backward (previous direction)
 * 
 * Continues until no RLFS changes occur or branching point detected.
 * 
 * @param context : The context
 * @param boundarySegments : Array of BoundarySegment objects (modified in place)
 * @param leftStart : Index of left-side segment in starting pair
 * @param rightStart : Index of right-side segment in starting pair
 */
function followMABranch(context is Context, boundarySegments is array, leftStart is number, rightStart is number)
{
    const numSegments = @size(boundarySegments);
    
    var left = leftStart;
    var right = rightStart;
    
    // Perform initial collision test between starting pair
    var madeProgress = testAndUpdateSegmentPair(boundarySegments, left, right);
    
    // Main loop: alternate between moving right and moving left
    var continueTracking = true;
    var maxIterations = numSegments * 2; // Prevent infinite loops
    var iterations = 0;
    
    while (continueTracking && iterations < maxIterations)
    {
        iterations += 1;
        continueTracking = false;
        
        // Phase 1: Keep left fixed, move right forward (next direction)
        var rightMoving = right;
        var foundBranchingPoint = false;
        
        for (var i = 0; i < numSegments && !foundBranchingPoint; i += 1)
        {
            // Move to next segment
            rightMoving = (rightMoving + 1) % numSegments;
            
            // Test collision between left and rightMoving
            const updated = testAndUpdateSegmentPair(boundarySegments, left, rightMoving);
            
            if (updated)
            {
                continueTracking = true;
                right = rightMoving;
                
                // Check for branching point on left segment
                foundBranchingPoint = detectAndHandleBranchingPoint(context, boundarySegments, left);
                if (foundBranchingPoint)
                {
                    break;
                }
            }
            else
            {
                // No more collisions in this direction
                break;
            }
        }
        
        if (foundBranchingPoint)
        {
            continue; // Branching point found, restart tracking from new branch
        }
        
        // Step right back one position
        right = (right + numSegments - 1) % numSegments;
        
        // Phase 2: Keep right fixed, move left backward (previous direction)
        var leftMoving = left;
        foundBranchingPoint = false;
        
        for (var i = 0; i < numSegments && !foundBranchingPoint; i += 1)
        {
            // Move to previous segment
            leftMoving = (leftMoving + numSegments - 1) % numSegments;
            
            // Test collision between leftMoving and right
            const updated = testAndUpdateSegmentPair(boundarySegments, leftMoving, right);
            
            if (updated)
            {
                continueTracking = true;
                left = leftMoving;
                
                // Check for branching point on right segment
                foundBranchingPoint = detectAndHandleBranchingPoint(context, boundarySegments, right);
                if (foundBranchingPoint)
                {
                    break;
                }
            }
            else
            {
                // No more collisions in this direction
                break;
            }
        }
        
        if (foundBranchingPoint)
        {
            continue; // Branching point found, restart tracking from new branch
        }
        
        // Step left back one position
        left = (left + 1) % numSegments;
    }
}

/**
 * Test collision between two segments and update RLFS pieces if they collide.
 * 
 * @param boundarySegments : Array of BoundarySegment objects (modified in place)
 * @param index1 : Index of first segment
 * @param index2 : Index of second segment
 * @returns {boolean} : True if RLFS was updated for either segment
 */
function testAndUpdateSegmentPair(boundarySegments is array, index1 is number, index2 is number) returns boolean
{
    const segment1 = boundarySegments[index1];
    const segment2 = boundarySegments[index2];
    
    // Perform 4 endpoint-segment collision tests as described in the paper
    var collisionEvents = [];
    
    // Test 1: segment1.start crosses segment2
    var collision = solveEndpointSegmentCollision(
        segment1.startPoint, segment1.startNormal,
        segment2.startPoint, segment2.endPoint,
        segment2.startNormal, segment2.endNormal
    );
    if (collision.collides)
    {
        collisionEvents = append(collisionEvents, {
            "distance" : collision.distance,
            "parameter" : 0.0,
            "peerParameter" : collision.parameter,
            "segmentIndex" : index1,
            "peerSegmentIndex" : index2
        });
    }
    
    // Test 2: segment1.end crosses segment2
    collision = solveEndpointSegmentCollision(
        segment1.endPoint, segment1.endNormal,
        segment2.startPoint, segment2.endPoint,
        segment2.startNormal, segment2.endNormal
    );
    if (collision.collides)
    {
        collisionEvents = append(collisionEvents, {
            "distance" : collision.distance,
            "parameter" : 1.0,
            "peerParameter" : collision.parameter,
            "segmentIndex" : index1,
            "peerSegmentIndex" : index2
        });
    }
    
    // Test 3: segment2.start crosses segment1
    collision = solveEndpointSegmentCollision(
        segment2.startPoint, segment2.startNormal,
        segment1.startPoint, segment1.endPoint,
        segment1.startNormal, segment1.endNormal
    );
    if (collision.collides)
    {
        collisionEvents = append(collisionEvents, {
            "distance" : collision.distance,
            "parameter" : collision.parameter,
            "peerParameter" : 0.0,
            "segmentIndex" : index1,
            "peerSegmentIndex" : index2
        });
    }
    
    // Test 4: segment2.end crosses segment1
    collision = solveEndpointSegmentCollision(
        segment2.endPoint, segment2.endNormal,
        segment1.startPoint, segment1.endPoint,
        segment1.startNormal, segment1.endNormal
    );
    if (collision.collides)
    {
        collisionEvents = append(collisionEvents, {
            "distance" : collision.distance,
            "parameter" : collision.parameter,
            "peerParameter" : 1.0,
            "segmentIndex" : index1,
            "peerSegmentIndex" : index2
        });
    }
    
    // Sort collision events by distance and create RLFS pieces
    if (@size(collisionEvents) >= 2)
    {
        // Sort by distance
        collisionEvents = sortCollisionEvents(collisionEvents);
        
        // Identify collision type and create RLFS pieces
        const updated = createRLFSPiecesFromCollision(boundarySegments, collisionEvents, index1, index2);
        return updated;
    }
    
    return false;
}
        segment1.startPoint, segment1.endPoint,
        segment1.startNormal, segment1.endNormal
    );
    if (collision.collides)
    {
        collisionEvents = append(collisionEvents, {
            "distance" : collision.distance,
            "parameter" : collision.parameter,
            "peerParameter" : 1.0,
            "segmentIndex" : index1,
            "peerSegmentIndex" : index2
        });
    }
    
    // Sort collision events by distance and create RLFS pieces
    if (@size(collisionEvents) >= 2)
    {
        // Sort by distance
        collisionEvents = sortCollisionEvents(collisionEvents);
        
        // Identify collision type and create RLFS pieces
        createRLFSPiecesFromCollision(boundarySegments, collisionEvents, index1, index2);
    }
}

/**
 * Sort collision events by distance (ascending).
 * 
 * @param events : Array of collision events
 * @returns {array} : Sorted array
 */
function sortCollisionEvents(events is array) returns array
{
    // Simple bubble sort for small arrays
    var sorted = events;
    const n = @size(sorted);
    
    for (var i = 0; i < n - 1; i += 1)
    {
        for (var j = 0; j < n - i - 1; j += 1)
        {
            if (sorted[j].distance > sorted[j + 1].distance)
            {
                // Swap
                const temp = sorted[j];
                sorted[j] = sorted[j + 1];
                sorted[j + 1] = temp;
            }
        }
    }
    
    return sorted;
}

/**
 * Create RLFS pieces from collision events.
 * Implements the logic from Section 3.3 of the paper for handling the three collision types.
 * 
 * @param boundarySegments : Array of BoundarySegment objects (modified in place)
 * @param collisionEvents : Sorted array of collision events
 * @param index1 : Index of first segment
 * @param index2 : Index of second segment
 * @returns {boolean} : True if any RLFS was actually updated
 */
function createRLFSPiecesFromCollision(boundarySegments is array, collisionEvents is array, index1 is number, index2 is number) returns boolean
{
    // Get first two collision events
    const event1 = collisionEvents[0];
    const event2 = collisionEvents[1];
    
    // Determine collision type and create RLFS pieces accordingly
    // Type 1: Different endpoints cross different segments (e.g., A crosses CD, then C crosses AB)
    // Type 2: Both endpoints of one segment cross the other (e.g., A and B cross CD)
    // Type 3: Adjacent segments (handled separately)
    
    // Create RLFS piece for segment 1
    const rlfs1 = {
        "parameterStart" : min(event1.parameter, event2.parameter),
        "displacementStart" : min(event1.distance, event2.distance),
        "parameterEnd" : max(event1.parameter, event2.parameter),
        "displacementEnd" : max(event1.distance, event2.distance),
        "peerSegmentIndex" : index2,
        "peerParameterStart" : event1.peerParameter,
        "peerParameterEnd" : event2.peerParameter,
        "edgeIndex" : -1,
        "reverseEndpoints" : false
    } as RLFSPiece;
    
    // Add RLFS piece to segment 1 (using minimum operator for overlaps)
    const updated1 = addRLFSPieceToSegment(boundarySegments[index1], rlfs1);
    
    // Create RLFS piece for segment 2
    const rlfs2 = {
        "parameterStart" : min(event1.peerParameter, event2.peerParameter),
        "displacementStart" : min(event1.distance, event2.distance),
        "parameterEnd" : max(event1.peerParameter, event2.peerParameter),
        "displacementEnd" : max(event1.distance, event2.distance),
        "peerSegmentIndex" : index1,
        "peerParameterStart" : event1.parameter,
        "peerParameterEnd" : event2.parameter,
        "edgeIndex" : -1,
        "reverseEndpoints" : true // Peer segment pieces run in opposite direction
    } as RLFSPiece;
    
    // Add RLFS piece to segment 2
    const updated2 = addRLFSPieceToSegment(boundarySegments[index2], rlfs2);
    
    return updated1 || updated2;
}

/**
 * Add an RLFS piece to a segment, handling overlaps with minimum operator.
 * Returns true if piece was actually added (RLFS updated).
 * 
 * @param segment : BoundarySegment object (modified in place)
 * @param newPiece : RLFS piece to add
 * @returns {boolean} : True if RLFS was updated
 */
function addRLFSPieceToSegment(segment is BoundarySegment, newPiece is RLFSPiece) returns boolean
{
    // Check if this piece provides smaller displacement values than existing pieces
    // If not, don't add it (minimum operator)
    
    var shouldAdd = true;
    var updated = false;
    
    // Simple implementation for now: check if interval overlaps with existing pieces
    // and compare displacement values
    for (var i = 0; i < @size(segment.rlfsPieces); i += 1)
    {
        const existingPiece = segment.rlfsPieces[i];
        
        // Check for interval overlap
        const overlapStart = max(newPiece.parameterStart, existingPiece.parameterStart);
        const overlapEnd = min(newPiece.parameterEnd, existingPiece.parameterEnd);
        
        if (overlapStart < overlapEnd)
        {
            // Overlaps - check if new piece has smaller displacement
            // Sample at middle of overlap
            const midParam = (overlapStart + overlapEnd) / 2.0;
            
            // Interpolate displacement for new piece
            const newT = (midParam - newPiece.parameterStart) / (newPiece.parameterEnd - newPiece.parameterStart);
            const newDisp = (1.0 - newT) * newPiece.displacementStart + newT * newPiece.displacementEnd;
            
            // Interpolate displacement for existing piece
            const existingT = (midParam - existingPiece.parameterStart) / (existingPiece.parameterEnd - existingPiece.parameterStart);
            const existingDisp = (1.0 - existingT) * existingPiece.displacementStart + existingT * existingPiece.displacementEnd;
            
            if (newDisp >= existingDisp)
            {
                // New piece doesn't improve RLFS in this overlap region
                shouldAdd = false;
                break;
            }
        }
    }
    
    if (shouldAdd)
    {
        segment.rlfsPieces = append(segment.rlfsPieces, newPiece);
        updated = true;
    }
    
    return updated;
}

/**
 * Detect and handle MA branching points on a segment.
 * 
 * A branching point is characterized by two adjacent RLFS pieces that satisfy:
 * 1. They were produced by collisions against two non-adjacent segments, OR
 * 2. The parameter s is not 0 and 1 for the end/start of the pieces
 * 
 * @param context : The context
 * @param boundarySegments : Array of BoundarySegment objects
 * @param segmentIndex : Index of segment to check for branching points
 * @returns {boolean} : True if branching point found and handled
 */
function detectAndHandleBranchingPoint(context is Context, boundarySegments is array, segmentIndex is number) returns boolean
{
    const segment = boundarySegments[segmentIndex];
    const pieces = segment.rlfsPieces;
    
    if (@size(pieces) < 2)
    {
        return false;
    }
    
    // Check each pair of adjacent RLFS pieces
    for (var i = 0; i < @size(pieces) - 1; i += 1)
    {
        const piece1 = pieces[i];
        const piece2 = pieces[i + 1];
        
        // Check if pieces are continuously adjacent (piece1.end == piece2.start)
        if (abs(piece1.parameterEnd - piece2.parameterStart) > 1e-6)
        {
            continue;
        }
        
        // Check condition 1: non-adjacent peer segments
        const peer1 = piece1.peerSegmentIndex;
        const peer2 = piece2.peerSegmentIndex;
        const numSegments = @size(boundarySegments);
        
        const isAdjacent = (peer1 == (peer2 + 1) % numSegments) || (peer2 == (peer1 + 1) % numSegments);
        
        if (!isAdjacent)
        {
            // Found branching point! Track new branch from peer segments
            println("Branching point detected at segment " ~ segmentIndex ~ " between pieces " ~ i ~ " and " ~ (i+1));
            followMABranch(context, boundarySegments, peer1, peer2);
            return true;
        }
        
        // Check condition 2: parameter s values
        // This is for adjacent peer segments that define an MA extreme point
        if (isAdjacent)
        {
            const s1End = piece1.peerParameterEnd;
            const s2Start = piece2.peerParameterStart;
            
            // Check if s values are not 0 and 1 at the junction
            if (abs(s1End - 1.0) > 1e-6 || abs(s2Start - 0.0) > 1e-6)
            {
                // This indicates an MA branching point from adjacent segments
                println("Branching point (adjacent peers) detected at segment " ~ segmentIndex);
                followMABranch(context, boundarySegments, peer1, peer2);
                return true;
            }
        }
    }
    
    return false;
}

/**
 * Fix MA for regions with holes by finding segments with undefined RLFS.
 * This implements the Fix MA routine from the paper.
 * 
 * @param context : The context
 * @param boundarySegments : Array of BoundarySegment objects (modified in place)
 */
function fixMAForHoles(context is Context, boundarySegments is array)
{
    // Search for segments with undefined or discontinuous RLFS
    for (var i = 0; i < @size(boundarySegments); i += 1)
    {
        const segment = boundarySegments[i];
        
        if (@size(segment.rlfsPieces) == 0)
        {
            // Found segment with undefined RLFS - perform global search for peer segments
            println("Found segment with undefined RLFS at index " ~ i);
            
            // Test collision with all other segments
            for (var j = 0; j < @size(boundarySegments); j += 1)
            {
                if (i != j)
                {
                    testAndUpdateSegmentPair(boundarySegments, i, j);
                }
            }
            
            // Track new MA branches from discovered peer segments
            if (@size(segment.rlfsPieces) > 0)
            {
                const peerIndex = segment.rlfsPieces[0].peerSegmentIndex;
                followMABranch(context, boundarySegments, i, peerIndex);
            }
        }
    }
}

// ==================== Phase 3: Explicit MA Extraction ====================

/**
 * Extract explicit medial axis graph from RLFS function.
 * This implements the algorithm from Section 4 of the paper.
 * 
 * @param context : The context
 * @param boundarySegments : Array of BoundarySegment objects with computed RLFS
 * @returns {MAGraph} : Medial axis graph with nodes and edges
 */
function extractExplicitMedialAxis(context is Context, boundarySegments is array) returns MAGraph
{
    var nodes = [];
    var edges = [];
    
    println("Extracting explicit medial axis...");
    
    // Iterate over all RLFS pieces to construct MA edges
    for (var segmentIndex = 0; segmentIndex < @size(boundarySegments); segmentIndex += 1)
    {
        const segment = boundarySegments[segmentIndex];
        
        for (var pieceIndex = 0; pieceIndex < @size(segment.rlfsPieces); pieceIndex += 1)
        {
            var piece = segment.rlfsPieces[pieceIndex];
            
            // Skip if this piece already has an edge assigned
            if (piece.edgeIndex != -1)
            {
                continue;
            }
            
            // Find corresponding piece on peer segment
            const peerSegmentIndex = piece.peerSegmentIndex;
            if (peerSegmentIndex < 0 || peerSegmentIndex >= @size(boundarySegments))
            {
                continue;
            }
            
            const peerSegment = boundarySegments[peerSegmentIndex];
            var peerPiece = undefined;
            var peerPieceIndex = -1;
            
            // Search for peer piece that refers back to current segment
            for (var k = 0; k < @size(peerSegment.rlfsPieces); k += 1)
            {
                if (peerSegment.rlfsPieces[k].peerSegmentIndex == segmentIndex)
                {
                    peerPiece = peerSegment.rlfsPieces[k];
                    peerPieceIndex = k;
                    break;
                }
            }
            
            if (peerPiece == undefined)
            {
                continue;
            }
            
            // Compute MA edge endpoints by averaging displaced samples
            const endpoint1 = computeMAEndpoint(segment, piece, true, peerSegment, peerPiece, false);
            const endpoint2 = computeMAEndpoint(segment, piece, false, peerSegment, peerPiece, true);
            
            // Create MA edge
            const edgeIndex = @size(edges);
            
            // Create or find nodes for endpoints
            const node1Index = findOrCreateNode(nodes, endpoint1.position, endpoint1.radius);
            const node2Index = findOrCreateNode(nodes, endpoint2.position, endpoint2.radius);
            
            // Create edge
            edges = append(edges, {
                "startNodeIndex" : node1Index,
                "endNodeIndex" : node2Index
            } as MAEdge);
            
            // Update piece with edge index
            piece.edgeIndex = edgeIndex;
            boundarySegments[segmentIndex].rlfsPieces[pieceIndex] = piece;
            
            // Update peer piece with edge index
            peerPiece.edgeIndex = edgeIndex;
            boundarySegments[peerSegmentIndex].rlfsPieces[peerPieceIndex] = peerPiece;
        }
    }
    
    println("Created " ~ @size(nodes) ~ " nodes and " ~ @size(edges) ~ " edges");
    
    return {
        "nodes" : nodes,
        "edges" : edges
    } as MAGraph;
}

/**
 * Compute an MA edge endpoint by averaging displaced samples from peer segments.
 * 
 * @param segment : BoundarySegment
 * @param piece : RLFS piece on segment
 * @param useStart : If true, use start of piece; otherwise use end
 * @param peerSegment : Peer boundary segment
 * @param peerPiece : RLFS piece on peer segment
 * @param usePeerEnd : If true, use end of peer piece; otherwise use start
 * @returns {map} : Map with "position" and "radius" of endpoint
 */
function computeMAEndpoint(segment is BoundarySegment, piece is RLFSPiece, useStart is boolean,
                          peerSegment is BoundarySegment, peerPiece is RLFSPiece, usePeerEnd is boolean) returns map
{
    // Get parameter and displacement from piece
    const parameter = useStart ? piece.parameterStart : piece.parameterEnd;
    const displacement = useStart ? piece.displacementStart : piece.displacementEnd;
    
    // Get peer parameter and displacement
    const peerParameter = usePeerEnd ? peerPiece.parameterEnd : peerPiece.parameterStart;
    const peerDisplacement = usePeerEnd ? peerPiece.displacementEnd : peerPiece.displacementStart;
    
    // Sample boundary at parameters and displace along normal
    const boundaryPoint = (1 - parameter) * segment.startPoint + parameter * segment.endPoint;
    const normal = normalize((1 - parameter) * segment.startNormal + parameter * segment.endNormal);
    const displacedPoint1 = boundaryPoint + displacement * normal;
    
    const peerBoundaryPoint = (1 - peerParameter) * peerSegment.startPoint + peerParameter * peerSegment.endPoint;
    const peerNormal = normalize((1 - peerParameter) * peerSegment.startNormal + peerParameter * peerSegment.endNormal);
    const displacedPoint2 = peerBoundaryPoint + peerDisplacement * peerNormal;
    
    // Average the two estimates
    const position = (displacedPoint1 + displacedPoint2) / 2;
    const radius = (displacement + peerDisplacement) / 2;
    
    return {
        "position" : position,
        "radius" : radius
    };
}

/**
 * Find existing node at position or create new node.
 * Updates node attributes if found (for branching nodes).
 * 
 * @param nodes : Array of MA nodes (modified in place)
 * @param position : Position to search for
 * @param radius : Radius for new node or to average with existing
 * @returns {number} : Index of node
 */
function findOrCreateNode(nodes is array, position is Vector, radius is ValueWithUnits) returns number
{
    const tolerance = 1e-6 * meter;
    
    // Search for existing node at this position
    for (var i = 0; i < @size(nodes); i += 1)
    {
        if (norm(nodes[i].position - position) < tolerance)
        {
            // Found existing node - update attributes with average
            const count = nodes[i].contributionCount;
            nodes[i].position = (nodes[i].position * count + position) / (count + 1);
            nodes[i].radius = (nodes[i].radius * count + radius) / (count + 1);
            nodes[i].contributionCount = count + 1;
            return i;
        }
    }
    
    // Create new node
    const newNode = {
        "position" : position,
        "radius" : radius,
        "contributionCount" : 1
    } as MANode;
    
    nodes = append(nodes, newNode);
    return @size(nodes) - 1;
}

// ==================== Phase 4: Visualization ====================

/**
 * Visualize the medial axis graph by creating sketch entities.
 * 
 * @param context : The context
 * @param id : Feature id
 * @param maGraph : Medial axis graph
 * @param sketchPlane : Plane for sketch creation
 * @param showDebug : Whether to show debug visualization
 */
function visualizeMedialAxis(context is Context, id is Id, maGraph is MAGraph, sketchPlane is Plane, showDebug is boolean)
{
    // Create a sketch on the plane
    const sketchId = id + "maSketch";
    newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
    
    // Draw MA edges as sketch lines
    for (var i = 0; i < @size(maGraph.edges); i += 1)
    {
        const edge = maGraph.edges[i];
        const startNode = maGraph.nodes[edge.startNodeIndex];
        const endNode = maGraph.nodes[edge.endNodeIndex];
        
        // Convert 3D positions to 2D sketch coordinates
        const startPoint2D = worldToPlane(sketchPlane, startNode.position);
        const endPoint2D = worldToPlane(sketchPlane, endNode.position);
        
        // Create sketch line
        skLineSegment(context, sketchId + ("edge" ~ i), {
            "start" : startPoint2D,
            "end" : endPoint2D
        });
        
        // Debug visualization: draw nodes as points
        if (showDebug)
        {
            addDebugPoint(context, startNode.position, DebugColor.RED);
            addDebugPoint(context, endNode.position, DebugColor.BLUE);
        }
    }
    
    // Solve the sketch
    skSolve(context, sketchId);
}

/**
 * Optional: Generate offset curves from RLFS function.
 * Implementation placeholder for Section 5 of the paper.
 * 
 * @param context : The context
 * @param id : Feature id
 * @param boundarySegments : Array of boundary segments with RLFS
 * @param sketchPlane : Plane for sketch creation
 */
function generateOffsetCurvesFromRLFS(context is Context, id is Id, boundarySegments is array, sketchPlane is Plane)
{
    // Placeholder for offset curve generation
    // Would iterate over RLFS pieces and create offset curves
    println("Offset curve generation not yet implemented");
}

/**
 * Helper function to get current time in milliseconds.
 * Note: FeatureScript doesn't have a built-in timer, this is a placeholder.
 * 
 * @returns {number} : Current time (placeholder)
 */
function getCurrentTime() returns number
{
    // FeatureScript doesn't have timing functions
    // This is a placeholder that would need to be implemented differently
    return 0;
}

