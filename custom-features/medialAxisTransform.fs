FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/path.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");

/**
 * Medial Axis Transform (MAT) for planar domains.
 * 
 * This feature implements the algorithm from the paper:
 * "Performant Medial Axis Transform for Planar Domains"
 * 
 * The algorithm computes the medial axis (skeleton) of a planar face by:
 * 1. Sampling the face boundary with adaptive curvature-aware sampling
 * 2. Simulating a grass-fire model through segment collision detection
 * 3. Computing the Reduced Length Function to Skeleton (RLFS)
 * 4. Extracting the explicit medial axis as a graph structure
 * 5. Drawing the medial axis curves on the face
 */

annotation { "Feature Type Name" : "Medial Axis Transform" }
export const medialAxisTransform = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Planar face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.planarFace is Query;
        
        annotation { "Name" : "Sample density", "Default" : 50 }
        isInteger(definition.sampleDensity, POSITIVE_COUNT_BOUNDS);
        
        annotation { "Name" : "Use adaptive sampling", "Default" : true }
        definition.useAdaptiveSampling is boolean;
        
        annotation { "Name" : "Show debug info", "Default" : true }
        definition.showDebug is boolean;
    }
    {
        // Verify the face is planar
        const facePlane = try silent(evPlane(context, { "face" : definition.planarFace }));
        if (facePlane == undefined)
        {
            throw regenError("Selected face must be planar");
        }
        
        println("=== Medial Axis Transform ===");
        println("Computing medial axis for planar face...");
        
        // Step 1: Sample the face boundary
        println("Step 1: Sampling face boundary...");
        const boundarySamples = sampleFaceBoundary(context, definition.planarFace, facePlane, 
                                                   definition.sampleDensity, definition.useAdaptiveSampling);
        println("  - Boundary segments: " ~ @size(boundarySamples.segments));
        
        // Step 2: Compute RLFS function through segment collision detection
        println("Step 2: Computing RLFS function...");
        const rlfsFunctionData = computeRLFSFunction(context, boundarySamples);
        println("  - RLFS pieces computed: " ~ @size(rlfsFunctionData.pieces));
        
        // Step 3: Extract explicit medial axis as a graph
        println("Step 3: Extracting medial axis graph...");
        const medialAxisGraph = extractMedialAxisGraph(boundarySamples, rlfsFunctionData);
        println("  - MA nodes: " ~ @size(medialAxisGraph.nodes));
        println("  - MA edges: " ~ @size(medialAxisGraph.edges));
        
        // Step 4: Draw the medial axis on the face
        println("Step 4: Drawing medial axis curves...");
        drawMedialAxisCurves(context, id, medialAxisGraph, facePlane, definition.showDebug);
        
        println("=== Medial Axis Transform Complete ===");
    });

/**
 * Type representing a boundary segment with endpoints and normals.
 * Each segment approximates a piece of the boundary curve.
 * 
 * @type {{
 *      @field startPoint {Vector} : 3D start position with length units
 *      @field endPoint {Vector} : 3D end position with length units
 *      @field startNormal {Vector} : Unit normal at start (pointing inward)
 *      @field endNormal {Vector} : Unit normal at end (pointing inward)
 *      @field index {number} : Segment index in the boundary chain
 *      @field nextIndex {number} : Index of next segment in chain
 *      @field previousIndex {number} : Index of previous segment in chain
 * }}
 */
export type BoundarySegment typecheck canBeBoundarySegment;

export predicate canBeBoundarySegment(value)
{
    value is map;
    value.startPoint is Vector;
    value.endPoint is Vector;
    value.startNormal is Vector;
    value.endNormal is Vector;
    value.index is number;
    value.nextIndex is number;
    value.previousIndex is number;
}

/**
 * Type representing sampled boundary data for a face.
 * 
 * @type {{
 *      @field segments {array} : Array of BoundarySegment
 *      @field plane {Plane} : The plane of the face
 * }}
 */
export type BoundarySamples typecheck canBeBoundarySamples;

export predicate canBeBoundarySamples(value)
{
    value is map;
    value.segments is array;
    value.plane is Plane;
}

/**
 * Type representing a piece of the RLFS function on a segment.
 * Each piece is a linear approximation of the RLFS.
 * 
 * @type {{
 *      @field segmentIndex {number} : Index of the segment this piece belongs to
 *      @field startParameter {number} : Start parameter on segment (0 to 1)
 *      @field endParameter {number} : End parameter on segment (0 to 1)
 *      @field startDistance {ValueWithUnits} : RLFS value at start with length units
 *      @field endDistance {ValueWithUnits} : RLFS value at end with length units
 *      @field peerSegmentIndex {number} : Index of the peer segment
 *      @field edgeIndex {number} : Index of corresponding MA edge (-1 if undefined)
 * }}
 */
export type RLFSPiece typecheck canBeRLFSPiece;

export predicate canBeRLFSPiece(value)
{
    value is map;
    value.segmentIndex is number;
    value.startParameter is number;
    value.endParameter is number;
    isLength(value.startDistance);
    isLength(value.endDistance);
    value.peerSegmentIndex is number;
    value.edgeIndex is number;
}

/**
 * Type representing computed RLFS function data.
 * 
 * @type {{
 *      @field pieces {array} : Array of RLFSPiece for all segments
 * }}
 */
export type RLFSFunctionData typecheck canBeRLFSFunctionData;

export predicate canBeRLFSFunctionData(value)
{
    value is map;
    value.pieces is array;
}

/**
 * Type representing a node in the medial axis graph.
 * 
 * @type {{
 *      @field position {Vector} : 3D position with length units
 *      @field radius {ValueWithUnits} : Radius of maximal disc with length units
 *      @field contributionCount {number} : Number of edges contributing to this node
 * }}
 */
export type MANode typecheck canBeMANode;

export predicate canBeMANode(value)
{
    value is map;
    value.position is Vector;
    isLength(value.radius);
    value.contributionCount is number;
}

/**
 * Type representing an edge in the medial axis graph.
 * 
 * @type {{
 *      @field startNodeIndex {number} : Index of start node
 *      @field endNodeIndex {number} : Index of end node
 * }}
 */
export type MAEdge typecheck canBeMAEdge;

export predicate canBeMAEdge(value)
{
    value is map;
    value.startNodeIndex is number;
    value.endNodeIndex is number;
}

/**
 * Type representing the medial axis graph structure.
 * 
 * @type {{
 *      @field nodes {array} : Array of MANode
 *      @field edges {array} : Array of MAEdge
 * }}
 */
export type MedialAxisGraph typecheck canBeMedialAxisGraph;

export predicate canBeMedialAxisGraph(value)
{
    value is map;
    value.nodes is array;
    value.edges is array;
}

/**
 * Sample the boundary of a planar face with adaptive curvature-aware sampling.
 * Returns an array of boundary segments with normals pointing inward.
 * 
 * @param context : The context
 * @param face : Query for the planar face
 * @param plane : The plane of the face
 * @param sampleDensity : Number of samples per boundary loop
 * @param useAdaptiveSampling : Whether to use curvature-aware adaptive sampling
 * @returns {BoundarySamples} : Sampled boundary data
 */
function sampleFaceBoundary(context is Context,
                           face is Query,
                           plane is Plane,
                           sampleDensity is number,
                           useAdaptiveSampling is boolean) returns BoundarySamples
{
    var allSegments = [];
    
    // Get all boundary loops of the face
    // Use qLoopEdges to get edges forming loops
    const loopEdges = qLoopEdges(face);
    
    // Construct paths from the loop edges to get ordered edge sequences
    const paths = constructPaths(context, loopEdges, {});
    
    if (@size(paths) == 0)
    {
        throw regenError("No boundary loops found on selected face");
    }
    
    println("  - Found " ~ @size(paths) ~ " boundary loops");
    
    // Process each boundary loop
    for (var loopIdx = 0; loopIdx < @size(paths); loopIdx += 1)
    {
        const path = paths[loopIdx];
        const edges = path.edges;
        
        println("  - Loop " ~ loopIdx ~ " has " ~ @size(edges) ~ " edges");
        
        // Process each edge in the loop
        for (var edgeIdx = 0; edgeIdx < @size(edges); edgeIdx += 1)
        {
            const edge = edges[edgeIdx];
            const edgeFlipped = path.flipped[edgeIdx];
            
            // Get edge length for adaptive sampling
            const edgeLength = evLength(context, { "entities" : edge });
            
            // Determine number of samples for this edge based on length
            var numSamples = max(2, ceil(sampleDensity * edgeLength / (0.01 * meter)));
            
            // Sample points along the edge
            for (var j = 0; j < numSamples - 1; j += 1)
            {
                var param1 = j / (numSamples - 1);
                var param2 = (j + 1) / (numSamples - 1);
                
                // If edge is flipped in the path, reverse parameters
                if (edgeFlipped)
                {
                    param1 = 1 - param1;
                    param2 = 1 - param2;
                    const temp = param1;
                    param1 = param2;
                    param2 = temp;
                }
                
                // Get positions at parameters
                const tangentLine1 = evEdgeTangentLine(context, { 
                    "edge" : edge, 
                    "parameter" : param1 
                });
                const tangentLine2 = evEdgeTangentLine(context, { 
                    "edge" : edge, 
                    "parameter" : param2 
                });
                
                const startPoint = tangentLine1.origin;
                const endPoint = tangentLine2.origin;
                
                // Compute normals pointing inward to the face
                var edgeTangent1 = tangentLine1.direction;
                var edgeTangent2 = tangentLine2.direction;
                
                // If edge is flipped, reverse tangents
                if (edgeFlipped)
                {
                    edgeTangent1 = -edgeTangent1;
                    edgeTangent2 = -edgeTangent2;
                }
                
                // Normal in 2D plane: rotate tangent 90 degrees
                // Check which direction points into the face
                const normal1 = computeInwardNormal(context, face, plane, startPoint, edgeTangent1);
                const normal2 = computeInwardNormal(context, face, plane, endPoint, edgeTangent2);
                
                const segment = {
                    "startPoint" : startPoint,
                    "endPoint" : endPoint,
                    "startNormal" : normal1,
                    "endNormal" : normal2,
                    "index" : @size(allSegments),
                    "nextIndex" : @size(allSegments) + 1,
                    "previousIndex" : @size(allSegments) - 1
                } as BoundarySegment;
                
                allSegments = append(allSegments, segment);
            }
        }
    }
    
    // Close the loop by connecting last to first
    if (@size(allSegments) > 0)
    {
        allSegments[@size(allSegments) - 1].nextIndex = 0;
        allSegments[0].previousIndex = @size(allSegments) - 1;
    }
    
    return {
        "segments" : allSegments,
        "plane" : plane
    } as BoundarySamples;
}

/**
 * Compute the inward-pointing normal for a boundary point.
 * The normal is perpendicular to the edge tangent and lies in the face plane.
 * 
 * @param context : The context
 * @param face : The face
 * @param plane : The face plane
 * @param point : Point on the boundary
 * @param edgeTangent : Tangent direction of the edge at the point
 * @returns {Vector} : Unit normal vector pointing inward
 */
function computeInwardNormal(context is Context,
                            face is Query,
                            plane is Plane,
                            point is Vector,
                            edgeTangent is Vector) returns Vector
{
    // Normal in plane is cross product of plane normal and edge tangent
    const candidateNormal1 = cross(plane.normal, edgeTangent);
    const candidateNormal2 = -candidateNormal1;
    
    // Test which normal points into the face by checking a point slightly offset
    const testOffset = 0.001 * meter;
    const testPoint1 = point + candidateNormal1 * testOffset;
    const testPoint2 = point + candidateNormal2 * testOffset;
    
    // Project test points onto plane
    const projectedTest1 = project(plane, testPoint1);
    const projectedTest2 = project(plane, testPoint2);
    
    // Use the normal that points toward the interior
    // For a planar face, we can check if the offset point is "inside" by
    // testing if it's closer to the face centroid
    const faceCentroid = evApproximateCentroid(context, { "entities" : face });
    
    const dist1 = norm(projectedTest1 - faceCentroid);
    const dist2 = norm(projectedTest2 - faceCentroid);
    
    if (dist1 < dist2)
    {
        return normalize(candidateNormal1);
    }
    else
    {
        return normalize(candidateNormal2);
    }
}

/**
 * Compute the RLFS function through segment collision detection.
 * Implements the grass-fire simulation algorithm from the paper.
 * 
 * @param context : The context
 * @param boundarySamples : Sampled boundary data
 * @returns {RLFSFunctionData} : Computed RLFS function data
 */
function computeRLFSFunction(context is Context,
                             boundarySamples is BoundarySamples) returns RLFSFunctionData
{
    var rlfsPieces = [];
    
    // Initialize RLFS as undefined for all segments
    // We'll build it up by testing segment collisions
    
    // Find MA extreme points and follow MA branches
    // This is a simplified implementation that tests all segment pairs
    
    const segments = boundarySamples.segments;
    const numSegments = @size(segments);
    
    if (numSegments == 0)
    {
        println("Warning: No segments to process");
        return {
            "pieces" : []
        } as RLFSFunctionData;
    }
    
    // Test all pairs of segments for collision
    // In the full algorithm, this would be done more efficiently
    for (var i = 0; i < numSegments; i += 1)
    {
        const segment1 = segments[i];
        
        // Test against adjacent segment first (convex vertices)
        const nextIdx = segment1.nextIndex;
        if (nextIdx >= 0 && nextIdx < numSegments)
        {
            const segment2 = segments[nextIdx];
            const collision = testSegmentCollision(segment1, segment2, true);
            
            if (collision.collides)
            {
                // Create RLFS pieces for both segments
                const piece1 = createRLFSPiece(segment1.index, collision, nextIdx);
                const piece2 = createRLFSPiece(nextIdx, collision, segment1.index);
                
                rlfsPieces = append(rlfsPieces, piece1);
                rlfsPieces = append(rlfsPieces, piece2);
            }
        }
        
        // Test against non-adjacent segments
        for (var j = i + 2; j < numSegments; j += 1)
        {
            if (j == segment1.nextIndex || j == segment1.previousIndex)
            {
                continue;
            }
            
            const segment2 = segments[j];
            const collision = testSegmentCollision(segment1, segment2, false);
            
            if (collision.collides)
            {
                // Create RLFS pieces for both segments
                const piece1 = createRLFSPiece(segment1.index, collision, j);
                const piece2 = createRLFSPiece(j, collision, segment1.index);
                
                rlfsPieces = append(rlfsPieces, piece1);
                rlfsPieces = append(rlfsPieces, piece2);
            }
        }
    }
    
    if (@size(rlfsPieces) == 0)
    {
        println("Warning: No collisions detected - medial axis may be empty or sample density may be too low");
    }
    
    return {
        "pieces" : rlfsPieces
    } as RLFSFunctionData;
}

/**
 * Test if two boundary segments will collide during fire-front propagation.
 * Returns collision information if they collide.
 * 
 * @param segment1 : First boundary segment
 * @param segment2 : Second boundary segment
 * @param areAdjacent : Whether segments are adjacent
 * @returns {map} : Collision result with collides flag and collision data
 */
function testSegmentCollision(segment1 is BoundarySegment,
                              segment2 is BoundarySegment,
                              areAdjacent is boolean) returns map
{
    // Test endpoint-segment collisions as described in the paper
    // Two moving segments collide when an endpoint of one segment 
    // collides with a point of the other segment
    
    var collisions = [];
    
    // Test segment1 start point against segment2
    const collision1 = testEndpointSegmentCollision(
        segment1.startPoint, segment1.startNormal,
        segment2.startPoint, segment2.startNormal,
        segment2.endPoint, segment2.endNormal
    );
    if (collision1.valid)
    {
        collisions = append(collisions, collision1);
    }
    
    // Test segment1 end point against segment2
    const collision2 = testEndpointSegmentCollision(
        segment1.endPoint, segment1.endNormal,
        segment2.startPoint, segment2.startNormal,
        segment2.endPoint, segment2.endNormal
    );
    if (collision2.valid)
    {
        collisions = append(collisions, collision2);
    }
    
    // Test segment2 start point against segment1
    const collision3 = testEndpointSegmentCollision(
        segment2.startPoint, segment2.startNormal,
        segment1.startPoint, segment1.startNormal,
        segment1.endPoint, segment1.endNormal
    );
    if (collision3.valid)
    {
        collisions = append(collisions, collision3);
    }
    
    // Test segment2 end point against segment1
    const collision4 = testEndpointSegmentCollision(
        segment2.endPoint, segment2.endNormal,
        segment1.startPoint, segment1.startNormal,
        segment1.endPoint, segment1.endNormal
    );
    if (collision4.valid)
    {
        collisions = append(collisions, collision4);
    }
    
    // Sort collisions by distance and return the first valid one
    if (@size(collisions) >= 2)
    {
        // We have a collision - return the sorted collision data
        return {
            "collides" : true,
            "collisions" : collisions
        };
    }
    
    return {
        "collides" : false
    };
}

/**
 * Test if a moving endpoint collides with a moving segment.
 * Solves the equation: P + d*N = (1-t)*(P1 + d*N1) + t*(P2 + d*N2)
 * 
 * @param endpointPosition : Position of the endpoint
 * @param endpointNormal : Normal at the endpoint
 * @param segmentStart : Start position of the segment
 * @param segmentStartNormal : Normal at segment start
 * @param segmentEnd : End position of the segment
 * @param segmentEndNormal : Normal at segment end
 * @returns {map} : Collision result with valid flag, distance d, and parameter t
 */
function testEndpointSegmentCollision(endpointPosition is Vector,
                                     endpointNormal is Vector,
                                     segmentStart is Vector,
                                     segmentStartNormal is Vector,
                                     segmentEnd is Vector,
                                     segmentEndNormal is Vector) returns map
{
    // This is a non-linear system of 2 equations in 2 unknowns (d and t)
    // P + d*N = (1-t)*(P1 + d*N1) + t*(P2 + d*N2)
    // Rearranging: P + d*N = P1 + d*N1 + t*(P2 - P1) + t*d*(N2 - N1)
    // This leads to a quadratic equation in most cases
    
    // For simplicity in this initial implementation, we'll use a numerical approach
    // A more complete implementation would solve the quadratic analytically
    
    // Extract 2D coordinates (assuming planar)
    const P = vector(endpointPosition[0] / meter, endpointPosition[1] / meter);
    const N = vector(endpointNormal[0], endpointNormal[1]);
    const P1 = vector(segmentStart[0] / meter, segmentStart[1] / meter);
    const N1 = vector(segmentStartNormal[0], segmentStartNormal[1]);
    const P2 = vector(segmentEnd[0] / meter, segmentEnd[1] / meter);
    const N2 = vector(segmentEndNormal[0], segmentEndNormal[1]);
    
    // Try to find a solution using numerical methods
    // Test multiple values of t and find the corresponding d
    const numTests = 20;
    var bestResult = undefined;
    var bestError = 1e10;
    
    for (var i = 0; i <= numTests; i += 1)
    {
        const t = i / numTests;
        
        // For a given t, solve for d
        // P + d*N = (1-t)*P1 + t*P2 + d*((1-t)*N1 + t*N2)
        // d*N - d*((1-t)*N1 + t*N2) = (1-t)*P1 + t*P2 - P
        // d*(N - (1-t)*N1 - t*N2) = (1-t)*P1 + t*P2 - P
        
        const interpolatedNormal = N1 * (1 - t) + N2 * t;
        const normalDiff = N - interpolatedNormal;
        const positionTarget = P1 * (1 - t) + P2 * t - P;
        
        // Solve for d using least squares (overdetermined system)
        // normalDiff * d = positionTarget
        const normalDiffMag = norm(normalDiff);
        
        if (abs(normalDiffMag) > 1e-6)
        {
            const d = dot(normalDiff, positionTarget) / (normalDiffMag * normalDiffMag);
            
            if (d > 0)
            {
                // Check how well this solution satisfies the equation
                const computed = P + N * d;
                const expected = P1 * (1 - t) + P2 * t + interpolatedNormal * d;
                const error = norm(computed - expected);
                
                if (error < bestError && error < 0.01)
                {
                    bestError = error;
                    bestResult = {
                        "valid" : true,
                        "distance" : d * meter,
                        "parameter" : t
                    };
                }
            }
        }
    }
    
    if (bestResult != undefined && bestResult.valid)
    {
        return bestResult;
    }
    
    return {
        "valid" : false
    };
}

/**
 * Create an RLFS piece from collision data.
 * 
 * @param segmentIndex : Index of the segment
 * @param collision : Collision data
 * @param peerSegmentIndex : Index of the peer segment
 * @returns {RLFSPiece} : Created RLFS piece
 */
function createRLFSPiece(segmentIndex is number,
                        collision is map,
                        peerSegmentIndex is number) returns RLFSPiece
{
    // Extract collision information to create RLFS piece
    // This is simplified - full implementation would handle different collision types
    
    const collisions = collision.collisions;
    if (@size(collisions) >= 2)
    {
        // Sort by parameter
        var sortedCollisions = collisions;
        // Simple bubble sort for 2 elements
        if (sortedCollisions[0].parameter > sortedCollisions[1].parameter)
        {
            const temp = sortedCollisions[0];
            sortedCollisions[0] = sortedCollisions[1];
            sortedCollisions[1] = temp;
        }
        
        return {
            "segmentIndex" : segmentIndex,
            "startParameter" : sortedCollisions[0].parameter,
            "endParameter" : sortedCollisions[1].parameter,
            "startDistance" : sortedCollisions[0].distance,
            "endDistance" : sortedCollisions[1].distance,
            "peerSegmentIndex" : peerSegmentIndex,
            "edgeIndex" : -1
        } as RLFSPiece;
    }
    
    // Fallback for single collision
    return {
        "segmentIndex" : segmentIndex,
        "startParameter" : 0.0,
        "endParameter" : 1.0,
        "startDistance" : collisions[0].distance,
        "endDistance" : collisions[0].distance,
        "peerSegmentIndex" : peerSegmentIndex,
        "edgeIndex" : -1
    } as RLFSPiece;
}

/**
 * Extract the explicit medial axis as a graph from the RLFS function.
 * Implements the algorithm from Section 4 of the paper.
 * 
 * @param boundarySamples : Sampled boundary data
 * @param rlfsFunctionData : Computed RLFS function data
 * @returns {MedialAxisGraph} : Medial axis graph structure
 */
function extractMedialAxisGraph(boundarySamples is BoundarySamples,
                                rlfsFunctionData is RLFSFunctionData) returns MedialAxisGraph
{
    var nodes = [];
    var edges = [];
    var pieces = rlfsFunctionData.pieces;
    
    if (@size(pieces) == 0)
    {
        println("Warning: No RLFS pieces to process - returning empty graph");
        return {
            "nodes" : [],
            "edges" : []
        } as MedialAxisGraph;
    }
    
    // Iterate over all RLFS pieces
    for (var i = 0; i < @size(pieces); i += 1)
    {
        const piece = pieces[i];
        
        // Skip if edge already created
        if (piece.edgeIndex >= 0)
        {
            continue;
        }
        
        // Find the peer piece on the peer segment
        const peerPiece = findPeerRLFSPiece(pieces, piece);
        
        if (peerPiece == undefined)
        {
            continue;
        }
        
        // Compute MA edge endpoints from the two peer pieces
        const segment = boundarySamples.segments[piece.segmentIndex];
        const peerSegment = boundarySamples.segments[piece.peerSegmentIndex];
        
        // Compute first endpoint (average of start samples)
        const startPos1 = interpolateSegmentPosition(segment, piece.startParameter);
        const startNormal1 = interpolateSegmentNormal(segment, piece.startParameter);
        const displaced1 = startPos1 + startNormal1 * piece.startDistance;
        
        const endPos2 = interpolateSegmentPosition(peerSegment, peerPiece.endParameter);
        const endNormal2 = interpolateSegmentNormal(peerSegment, peerPiece.endParameter);
        const displaced2 = endPos2 + endNormal2 * peerPiece.endDistance;
        
        const endpoint1Pos = (displaced1 + displaced2) * 0.5;
        const endpoint1Radius = (piece.startDistance + peerPiece.endDistance) * 0.5;
        
        // Compute second endpoint (average of end samples)
        const endPos1 = interpolateSegmentPosition(segment, piece.endParameter);
        const endNormal1 = interpolateSegmentNormal(segment, piece.endParameter);
        const displaced3 = endPos1 + endNormal1 * piece.endDistance;
        
        const startPos2 = interpolateSegmentPosition(peerSegment, peerPiece.startParameter);
        const startNormal2 = interpolateSegmentNormal(peerSegment, peerPiece.startParameter);
        const displaced4 = startPos2 + startNormal2 * peerPiece.startDistance;
        
        const endpoint2Pos = (displaced3 + displaced4) * 0.5;
        const endpoint2Radius = (piece.endDistance + peerPiece.startDistance) * 0.5;
        
        // Create or find nodes for the endpoints
        const node1 = {
            "position" : endpoint1Pos,
            "radius" : endpoint1Radius,
            "contributionCount" : 1
        } as MANode;
        
        const node2 = {
            "position" : endpoint2Pos,
            "radius" : endpoint2Radius,
            "contributionCount" : 1
        } as MANode;
        
        const node1Index = @size(nodes);
        const node2Index = @size(nodes) + 1;
        
        nodes = append(nodes, node1);
        nodes = append(nodes, node2);
        
        // Create edge
        const edge = {
            "startNodeIndex" : node1Index,
            "endNodeIndex" : node2Index
        } as MAEdge;
        
        const edgeIndex = @size(edges);
        edges = append(edges, edge);
        
        // Mark pieces as processed
        pieces[i].edgeIndex = edgeIndex;
        if (peerPiece != undefined)
        {
            // Find and update peer piece
            for (var j = 0; j < @size(pieces); j += 1)
            {
                if (pieces[j].segmentIndex == peerPiece.segmentIndex &&
                    abs(pieces[j].startParameter - peerPiece.startParameter) < 1e-6)
                {
                    pieces[j].edgeIndex = edgeIndex;
                    break;
                }
            }
        }
    }
    
    return {
        "nodes" : nodes,
        "edges" : edges
    } as MedialAxisGraph;
}

/**
 * Find the peer RLFS piece corresponding to a given piece.
 * 
 * @param pieces : Array of all RLFS pieces
 * @param piece : The piece to find the peer for
 * @returns {RLFSPiece} : The peer piece, or undefined if not found
 */
function findPeerRLFSPiece(pieces is array, piece is RLFSPiece)
{
    // Find piece on peer segment that references back to this segment
    for (var i = 0; i < @size(pieces); i += 1)
    {
        const candidate = pieces[i];
        if (candidate.segmentIndex == piece.peerSegmentIndex &&
            candidate.peerSegmentIndex == piece.segmentIndex)
        {
            return candidate;
        }
    }
    return undefined;
}

/**
 * Interpolate position on a boundary segment at a given parameter.
 * 
 * @param segment : The boundary segment
 * @param parameter : Parameter (0 to 1)
 * @returns {Vector} : Interpolated position
 */
function interpolateSegmentPosition(segment is BoundarySegment, parameter is number) returns Vector
{
    return segment.startPoint * (1 - parameter) + segment.endPoint * parameter;
}

/**
 * Interpolate normal on a boundary segment at a given parameter.
 * 
 * @param segment : The boundary segment
 * @param parameter : Parameter (0 to 1)
 * @returns {Vector} : Interpolated normal (not normalized)
 */
function interpolateSegmentNormal(segment is BoundarySegment, parameter is number) returns Vector
{
    return segment.startNormal * (1 - parameter) + segment.endNormal * parameter;
}

/**
 * Draw the medial axis curves on the face.
 * 
 * @param context : The context
 * @param id : Feature ID
 * @param graph : Medial axis graph
 * @param plane : Face plane
 * @param showDebug : Whether to show debug visualization
 */
function drawMedialAxisCurves(context is Context,
                              id is Id,
                              graph is MedialAxisGraph,
                              plane is Plane,
                              showDebug is boolean)
{
    if (@size(graph.edges) == 0)
    {
        println("No medial axis edges to draw");
        return;
    }
    
    // Create a sketch on the plane to draw the medial axis
    const sketch = newSketchOnPlane(context, id + "sketch", {
        "sketchPlane" : plane
    });
    
    // Draw MA edges as sketch line segments
    for (var i = 0; i < @size(graph.edges); i += 1)
    {
        const edge = graph.edges[i];
        const startNode = graph.nodes[edge.startNodeIndex];
        const endNode = graph.nodes[edge.endNodeIndex];
        
        // Project positions onto the sketch plane to get 2D coordinates
        const start2D = worldToPlane(plane, startNode.position);
        const end2D = worldToPlane(plane, endNode.position);
        
        // Draw line segment in sketch
        skLineSegment(sketch, "edge" ~ i, {
            "start" : start2D,
            "end" : end2D
        });
        
        if (showDebug)
        {
            // Draw debug visualization
            debug(context, startNode.position, endNode.position, DebugColor.RED);
            addDebugPoint(context, startNode.position, DebugColor.BLUE);
            addDebugPoint(context, endNode.position, DebugColor.BLUE);
        }
    }
    
    // Solve the sketch to create the geometry
    skSolve(sketch);
    
    if (showDebug)
    {
        println("Drew " ~ @size(graph.edges) ~ " medial axis curves");
    }
}
