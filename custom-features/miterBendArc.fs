FeatureScript 2892;
import(path : "onshape/std/common.fs", version : "2892.0");
import(path : "onshape/std/geomOperations.fs", version : "2892.0");

// Geometric tolerances used throughout the feature:
//   LENGTH_NEAR_ZERO    - length threshold for coincident-point and near-zero length-vector checks
//   AREA_NEAR_ZERO      - area threshold for near-zero cross-product checks (cross of two length-vectors, units: m^2)
//   DIRECTION_NEAR_ZERO - magnitude threshold for dimensionless direction vectors after normalization
const LENGTH_NEAR_ZERO = 1e-8 * meter;
const AREA_NEAR_ZERO = 1e-10 * meter ^ 2;
const DIRECTION_NEAR_ZERO = 1e-8;

/**
 * Miter Bend Arc Feature
 *
 * Identifies miter joints between adjacent frame body segments and draws a
 * tangent circular arc wire body at each joint. The arc represents the outer
 * (convex) surface profile of the bent tube in the folded state - tangent to
 * both adjacent frame segments at the miter interface.
 *
 * This is Phase 1 of a kirigami tube bending visualization tool. Select two
 * or more mitered frame body segments. The feature finds each miter interface
 * and creates a circular arc wire tangent to both frame sweeps at the joint,
 * showing the true bending radius geometry.
 */
annotation { "Feature Type Name" : "Miter bend arc",
             "Feature Type Description" : "Draws a tangent circular arc at miter joints between frame bodies to visualize the folded bend geometry." }
export const miterBendArc = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Frame parts",
                     "Filter" : EntityType.BODY && (BodyType.SOLID || BodyType.COMPOSITE) }
        definition.bodies is Query;
    }
    {
        // Expand composite parts to their contained solid bodies
        if (!isQueryEmpty(context, qBodyType(definition.bodies, BodyType.COMPOSITE)))
        {
            if (evaluateQueryCount(context, definition.bodies) > 1)
                throw regenError("Only one composite part allowed.");
            definition.bodies = qContainedInCompositeParts(qNthElement(definition.bodies, 0));
        }
        else if (evaluateQueryCount(context, definition.bodies) < 2)
            throw regenError("More than one frame part required.");

        const bodies = evaluateQuery(context, definition.bodies);
        const numberOfBodies = size(bodies);
        var arcCount = 0;

        // Iterate over all ordered pairs of bodies to find miter joints between them
        for (var indexA = 0; indexA < numberOfBodies; indexA += 1)
        {
            const bodyA = bodies[indexA];
            const planarFacesOfBodyA = qOwnedByBody(bodyA, EntityType.FACE)->qGeometry(GeometryType.PLANE);

            for (var indexB = indexA + 1; indexB < numberOfBodies; indexB += 1)
            {
                const bodyB = bodies[indexB];
                const planarFacesOfBodyB = qOwnedByBody(bodyB, EntityType.FACE)->qGeometry(GeometryType.PLANE);

                // Search all planar faces of body A for one that has a matching miter face on body B
                for (var candidateFaceA in evaluateQuery(context, planarFacesOfBodyA))
                {
                    const miterPlane = evPlane(context, { "face" : candidateFaceA });
                    const matchingFaceB = findMatchingMiterFace(context, miterPlane, candidateFaceA, planarFacesOfBodyB);

                    if (!isQueryEmpty(context, matchingFaceB))
                    {
                        // Miter joint found - compute and draw the bend arc
                        drawMiterBendArc(context, id + arcCount, candidateFaceA, matchingFaceB, miterPlane);
                        arcCount += 1;
                        break;
                    }
                }
            }
        }

        if (arcCount == 0)
            reportFeatureWarning(context, id, "No miter joints found between the selected frame bodies.");
    });


// =====================================================================
// findMatchingMiterFace
//
// Given a candidate miter face on body A (described by its plane) and
// the set of planar faces on body B, returns a face on body B that is
// coplanar with and adjacent to the candidate face (shares at least one
// vertex). This identifies the matching miter cap faces at a joint.
//
// Input:
//   miterPlane      - plane of the candidate face on body A
//   candidateFaceA  - the planar face on body A to match against
//   planarFacesOfB  - all planar faces of body B
//
// Returns the matching face query or qNothing() if no match is found.
// Logic adapted from Neil Cooke's frameUnroll.fs.
// =====================================================================

function findMatchingMiterFace(context is Context, miterPlane is Plane, candidateFaceA is Query, planarFacesOfBodyB is Query) returns Query
{
    // Find faces of body B that lie in the same plane as the candidate face of body A
    var coplanarFacesOnBodyB = qCoincidesWithPlane(planarFacesOfBodyB, miterPlane)->
        qParallelPlanes(miterPlane, true)->
        qSubtraction(qParallelPlanes(planarFacesOfBodyB, miterPlane, false));

    if (isQueryEmpty(context, coplanarFacesOnBodyB))
        return qNothing();

    // Confirm adjacency: at least one vertex of face A must lie on the coplanar
    // face of B, ruling out coincident-but-non-touching face pairs
    const verticesOfFaceA = qAdjacent(candidateFaceA, AdjacencyType.VERTEX, EntityType.VERTEX);
    for (var vertex in evaluateQuery(context, verticesOfFaceA))
    {
        const vertexPosition = evVertexPoint(context, { "vertex" : vertex });
        if (!isQueryEmpty(context, qContainsPoint(coplanarFacesOnBodyB, vertexPosition)))
            return coplanarFacesOnBodyB;
    }

    return qNothing();
}


// =====================================================================
// getAngledWallFacesAtMiter
//
// Given a miter plane and one of the miter cap faces (the diagonal cut
// face at the end of a frame tube), returns the tube wall (swept) faces
// that are cut by the miter. These are the faces adjacent to the miter
// cap face that are NOT perpendicular to the miter plane and that
// intersect it.
//
// Input:
//   miterPlane   - the plane of the miter cut
//   miterCapFace - the diagonal end cap face at the miter
//
// Returns the angled wall faces that intersect the miter plane.
// Logic adapted from Neil Cooke's frameUnroll.fs getFacesTouchingPlane.
// =====================================================================

function getAngledWallFacesAtMiter(miterPlane is Plane, miterCapFace is Query) returns Query
{
    // Planar faces sharing an edge with the miter cap face (the tube side walls)
    const adjacentSideFaces = qAdjacent(miterCapFace, AdjacencyType.EDGE, EntityType.FACE)->
        qGeometry(GeometryType.PLANE);

    // Exclude faces whose normal is perpendicular to the miter plane normal
    // (those wall faces lie perpendicular to the miter cut, not angled by it)
    const angledSideFaces = adjacentSideFaces->
        qSubtraction(qPlanesParallelToDirection(adjacentSideFaces, miterPlane.normal));

    // Keep only the faces that actually intersect the miter plane
    return qIntersectsPlane(angledSideFaces, miterPlane);
}


// =====================================================================
// computeTangentArcData
//
// Given a start point, a tangent direction at the start, and an end
// point, computes the unique circular arc that begins at startPoint,
// is tangent to tangentDirection there, and passes through endPoint.
//
// For properly mitered frame joints this arc is automatically tangent
// to the second frame's sweep direction at the end point as well.
//
// Input:
//   startPoint       - 3D start point of the arc (with length units)
//   tangentDirection - unit direction vector of the arc at the start
//   endPoint         - 3D end point of the arc (with length units)
//
// Returns a map with fields:
//   valid   {boolean}        - false if arc cannot be computed
//   center  {Vector}         - 3D center of the arc circle
//   radius  {ValueWithUnits} - arc radius
//   normal  {Vector}         - unit normal to the arc plane
//   mid     {Vector}         - midpoint along the arc (for construction)
//
// Adapted from getTangentArcData in custom-features/3dArcUtils.fs.
// =====================================================================

function computeTangentArcData(startPoint is Vector, tangentDirection is Vector, endPoint is Vector) returns map
{
    const chordVector = endPoint - startPoint;

    // Compute the radial direction perpendicular to the tangent, lying in the bending plane.
    // Note: radialDirection has length units at this stage (cross of length-vector with
    // dimensionless tangent gives a length-vector), so the threshold is in meters.
    var radialDirection = cross(tangentDirection, cross(chordVector, tangentDirection));
    if (norm(radialDirection) < LENGTH_NEAR_ZERO)
        return { "valid" : false };
    radialDirection = normalize(radialDirection);

    // Arc radius from the tangent-arc formula: R = |chord|^2 / (2 * chord . radialDir)
    var arcRadius = squaredNorm(endPoint - startPoint) / (2 * dot(chordVector, radialDirection));
    if (arcRadius < 0 * meter)
    {
        arcRadius = -arcRadius;
        radialDirection = -radialDirection;
    }

    const arcCenter = startPoint + arcRadius * radialDirection;
    const arcPlaneNormal = cross(tangentDirection, radialDirection);
    if (norm(arcPlaneNormal) < DIRECTION_NEAR_ZERO)
        return { "valid" : false };

    // Find the arc midpoint by rotating the start-to-center vector halfway around the sweep angle
    const startRadiusVector = startPoint - arcCenter;
    const endRadiusVector = endPoint - arcCenter;
    var sweepAngle = atan2(
        dot(arcPlaneNormal, cross(startRadiusVector, endRadiusVector)),
        dot(startRadiusVector, endRadiusVector));

    if (sweepAngle < 0 * radian)
        sweepAngle = sweepAngle + 2 * PI * radian;

    const midRadiusVector = rotationMatrix3d(normalize(arcPlaneNormal), sweepAngle / 2) * startRadiusVector;
    const arcMidPoint = arcCenter + midRadiusVector;

    return {
        "valid"  : true,
        "center" : arcCenter,
        "radius" : arcRadius,
        "normal" : normalize(arcPlaneNormal),
        "mid"    : arcMidPoint
    };
}


// =====================================================================
// createArcWireBody
//
// Creates a circular arc wire body in 3D space passing through three
// points (startPoint, midPoint, endPoint). The arc lies in the plane
// defined by these three points.
//
// Uses a temporary sketch to generate the arc geometry, extracts it
// as a persistent wire body, then deletes the sketch.
//
// Input:
//   id         - unique feature sub-id for sketch and wire operations
//   startPoint - 3D start of the arc
//   midPoint   - 3D point on the arc between start and end
//   endPoint   - 3D end of the arc
//
// Returns a query for the created wire body.
// Adapted from opArc3d in custom-features/3dArcUtils.fs.
// =====================================================================

function createArcWireBody(context is Context, id is Id, startPoint is Vector, midPoint is Vector, endPoint is Vector) returns Query
{
    // Build a coordinate system whose XY plane contains the three arc points
    const startToMidVector = midPoint - startPoint;
    const arcPlaneNormal = cross(startToMidVector, endPoint - startPoint);

    if (norm(arcPlaneNormal) < AREA_NEAR_ZERO)
    {
        // Points are collinear - cannot define an arc
        reportFeatureWarning(context, id, "Miter arc points are collinear; skipping this arc.");
        return qNothing();
    }

    const planeXAxis = normalize(startToMidVector);
    const planeCSys = coordSystem(startPoint, planeXAxis, normalize(arcPlaneNormal));
    const sketchPlane = plane(planeCSys);

    // Project all three arc points into the 2D sketch coordinate system
    const startPoint2d = vector(0 * meter, 0 * meter);
    const midPointLocal = fromWorld(planeCSys, midPoint);
    const endPointLocal = fromWorld(planeCSys, endPoint);
    const midPoint2d = vector(midPointLocal[0], midPointLocal[1]);
    const endPoint2d = vector(endPointLocal[0], endPointLocal[1]);

    // Build a temporary sketch containing the arc
    const sketchId = id + "sketch";
    const arcSketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
    skArc(arcSketch, "bendArc", { "start" : startPoint2d, "mid" : midPoint2d, "end" : endPoint2d });
    skSolve(arcSketch);

    // Extract the arc edge as a persistent wire body
    opExtractWires(context, id + "wire", { "edges" : qCreatedBy(sketchId, EntityType.EDGE) });

    // Remove the temporary sketch bodies
    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

    return qCreatedBy(id + "wire", EntityType.BODY);
}


// =====================================================================
// drawMiterBendArc
//
// Computes the full geometry of a miter joint between two frame bodies
// and creates a tangent circular arc wire body at the outer (convex)
// surface of the joint.
//
// Algorithm:
//   1. Get the angled wall faces of each body at the miter plane
//   2. Build a reference coordinate system from body A's miter geometry
//   3. Find body B's outer ridge edge using body A's bounding box extents
//   4. Find the inner fold crease edge of body B
//   5. Find body A's outer ridge edge as the miter-plane edge farthest
//      from the inner fold crease
//   6. Compute the sweep direction of body A from the fold geometry
//   7. Compute and create the tangent arc from body A's outer ridge
//      midpoint to body B's outer ridge midpoint
//
// Input:
//   miterFaceA - the miter cap face of frame body A
//   miterFaceB - the matching miter cap face of frame body B (same plane)
//   miterPlane - the plane containing both miter faces
// =====================================================================

function drawMiterBendArc(context is Context, id is Id, miterFaceA is Query, miterFaceB is Query, miterPlane is Plane)
{
    // Get the tube wall faces angled and cut by the miter for each body
    const angledFacesOfBodyA = getAngledWallFacesAtMiter(miterPlane, miterFaceA);
    const angledFacesOfBodyB = getAngledWallFacesAtMiter(miterPlane, miterFaceB);

    if (isQueryEmpty(context, angledFacesOfBodyA) || isQueryEmpty(context, angledFacesOfBodyB))
        return;

    // Collect all edges of body A's angled wall faces that lie in the miter plane.
    // For a rectangular tube, these are 2 edges: the inner fold crease and the outer ridge.
    const miterPlaneEdgesOfBodyA = qCoincidesWithPlane(
        qAdjacent(angledFacesOfBodyA, AdjacencyType.EDGE, EntityType.EDGE),
        miterPlane);

    if (isQueryEmpty(context, miterPlaneEdgesOfBodyA))
        return;

    // Build a reference coordinate system for the miter joint geometry:
    //   Origin:  midpoint of body A's first miter-plane edge
    //   X-axis:  direction along the fold crease (perpendicular to the bend plane)
    //   Z-axis:  outward normal of body A's first angled wall face
    const foldCreaseMidLine = evEdgeTangentLine(context, { "edge" : miterPlaneEdgesOfBodyA, "parameter" : 0.5 });
    const referenceWallFaceOfBodyA = qNthElement(angledFacesOfBodyA, 0);
    const referenceWallFaceTangentPlane = evFaceTangentPlane(context, {
                "face" : referenceWallFaceOfBodyA,
                "parameter" : vector(0.5, 0.5)
            });
    const miterReferenceCSys = coordSystem(
        foldCreaseMidLine.origin,
        foldCreaseMidLine.direction,
        referenceWallFaceTangentPlane.normal);

    // Compute body A's bounding box in the miter reference coordinate system
    // to find the sweep-direction extents of the tube cross-section
    const bodyABoundingBox = evBox3d(context, {
                "topology" : qOwnerBody(miterFaceA),
                "cSys" : miterReferenceCSys,
                "tight" : true
            });

    // Find the outer ridge edge of body B at the miter.
    // It lies in the miter plane at a sweep-direction extreme of body A's bounding box.
    var outerRidgeEdgeOfBodyB = qNothing();
    for (var boxCorner in [bodyABoundingBox.minCorner, bodyABoundingBox.maxCorner])
    {
        const sweepExtremePlane = plane(toWorld(miterReferenceCSys, boxCorner), yAxis(miterReferenceCSys));
        outerRidgeEdgeOfBodyB =
            qCoincidesWithPlane(qAdjacent(angledFacesOfBodyB, AdjacencyType.EDGE, EntityType.EDGE), sweepExtremePlane)->
            qIntersection(qCoincidesWithPlane(qAdjacent(angledFacesOfBodyB, AdjacencyType.EDGE, EntityType.EDGE), miterPlane));

        if (!isQueryEmpty(context, outerRidgeEdgeOfBodyB))
            break;
    }

    if (isQueryEmpty(context, outerRidgeEdgeOfBodyB))
        return;

    // Arc end point P2 - midpoint of body B's outer ridge at the miter
    const outerRidgeMidpointOfBodyB = evEdgeTangentLine(context, {
                "edge" : outerRidgeEdgeOfBodyB,
                "parameter" : 0.5
            }).origin;

    // Find body B's inner fold crease edge - the edge in the miter plane
    // closest to the outer ridge midpoint (excluding the outer ridge itself)
    const allMiterPlaneEdgesOfBodyB = qCoincidesWithPlane(
        qAdjacent(angledFacesOfBodyB, AdjacencyType.EDGE, EntityType.EDGE),
        miterPlane);

    const innerFoldCreaseEdgeOfBodyB = qClosestTo(
        allMiterPlaneEdgesOfBodyB->qSubtraction(outerRidgeEdgeOfBodyB),
        outerRidgeMidpointOfBodyB);

    if (isQueryEmpty(context, innerFoldCreaseEdgeOfBodyB))
        return;

    const innerFoldCreaseLine = evEdgeTangentLine(context, {
                "edge" : innerFoldCreaseEdgeOfBodyB,
                "parameter" : 0.5
            });

    // Find body A's outer ridge edge - the edge in body A's miter-plane edge set
    // that is farthest from the inner fold crease line
    var outerRidgeEdgeOfBodyA = qNothing();
    var maximumDistanceFromFoldCrease = -1 * meter;

    for (var candidateEdge in evaluateQuery(context, miterPlaneEdgesOfBodyA))
    {
        const edgeMidpoint = evEdgeTangentLine(context, {
                    "edge" : candidateEdge,
                    "parameter" : 0.5
                }).origin;

        // Project the edge midpoint onto the inner fold crease line and measure distance
        const projectedOntoFoldCrease = project(innerFoldCreaseLine, edgeMidpoint);
        const distanceFromFoldCrease = norm(edgeMidpoint - projectedOntoFoldCrease);

        if (distanceFromFoldCrease > maximumDistanceFromFoldCrease)
        {
            maximumDistanceFromFoldCrease = distanceFromFoldCrease;
            outerRidgeEdgeOfBodyA = candidateEdge;
        }
    }

    if (isQueryEmpty(context, outerRidgeEdgeOfBodyA))
        return;

    // Arc start point P1 - midpoint of body A's outer ridge at the miter
    const outerRidgeMidpointOfBodyA = evEdgeTangentLine(context, {
                "edge" : outerRidgeEdgeOfBodyA,
                "parameter" : 0.5
            }).origin;

    // Verify the two arc endpoints are distinct before proceeding
    if (norm(outerRidgeMidpointOfBodyA - outerRidgeMidpointOfBodyB) < LENGTH_NEAR_ZERO)
        return;

    // Compute the sweep direction of body A at the miter joint.
    // Cross product of the fold crease direction with body A's reference wall
    // face normal gives a vector along the tube's sweep axis at the miter.
    var sweepDirectionOfBodyA = normalize(cross(innerFoldCreaseLine.direction, referenceWallFaceTangentPlane.normal));

    // Ensure the sweep direction points away from body A's interior, toward the miter joint,
    // by checking alignment with the outward normal of the miter cap face
    if (dot(sweepDirectionOfBodyA, miterPlane.normal) < 0)
        sweepDirectionOfBodyA = -sweepDirectionOfBodyA;

    // Compute the tangent arc from body A's outer ridge midpoint to body B's outer ridge midpoint.
    // For a properly mitered joint, this arc is automatically tangent to body B's sweep
    // direction at the end point as well.
    const arcGeometryData = computeTangentArcData(outerRidgeMidpointOfBodyA, sweepDirectionOfBodyA, outerRidgeMidpointOfBodyB);

    if (!arcGeometryData.valid)
    {
        reportFeatureWarning(context, id, "Could not compute tangent arc at miter joint.");
        return;
    }

    // Create the arc wire body visible in the part studio
    createArcWireBody(context, id, outerRidgeMidpointOfBodyA, arcGeometryData.mid, outerRidgeMidpointOfBodyB);
}
