FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/geomOperations.fs", version : "2909.0");
import(path : "onshape/std/frameAttributes.fs", version : "2909.0");
import(path : "onshape/std/frameUtils.fs", version : "2909.0");
// External Part Studio: Kirigami Bend Constructor.  Provides the bend geometry that is
// placed at each apex edge and later unfolded by the downstream flat-layout script.
KirigamiBendConstructor::import(path : "1173cc57cdf5a7d688426b78", version : "d2c5a6c4ebd8f320a741122a");

// Named key used when attaching KirigamiBendAttribute to instantiated bodies.
const KIRIGAMI_BEND_ATTRIBUTE_NAME = "kirigamiBendData";

/**
 * Attribute stored on each Kirigami Bend Constructor body placed by this feature.
 * A downstream flat-layout script queries these attributes to locate, orient, and
 * boolean the unfolded segments together for laser-cut export.
 *
 * Fields:
 *   instanceIndex {number} : Zero-based counter across all apex edges of all selected bodies.
 *   apexOrigin    {Vector} : World-space midpoint of the apex edge (origin of the local frame).
 *   zAxis         {Vector} : Fold-line direction -- local Z, along the outer apex edge,
 *                            derived as cross(capFaceNormal, outerWallNormal).
 *   xAxis         {Vector} : Miter cap face outward normal -- local X, perpendicular to the fold plane.
 */
export type KirigamiBendAttribute typecheck canBeKirigamiBendAttribute;

export predicate canBeKirigamiBendAttribute(value)
{
    value is map;
    value.instanceIndex is number;
    is3dLengthVector(value.apexOrigin);
    is3dDirection(value.zAxis);
    is3dDirection(value.xAxis);
}

/**
 * For each selected Onshape frame body, locates the single outer apex edge at every
 * shared miter joint and places one Kirigami Bend Constructor instance there.
 *
 * A joint is eligible for a constructor only when ALL of the following hold:
 *   1. The cap face is an INTERNAL joint face (FrameTopologyAttribute.isFrameTerminus = false).
 *      Free-end terminus faces (isFrameTerminus = true) are skipped entirely.
 *   2. The miter angle is non-zero: a 0-degree cut (cap face normal parallel to the tube
 *      axis) is a perpendicular butt joint that requires no kirigami geometry.
 *   3. The miter is a simple single-axis rotation: the component of the cap face normal
 *      perpendicular to the tube axis must be parallel to one of the tube's swept wall face
 *      normals.  Compound miters (rotation around two profile axes simultaneously) are not
 *      achievable with this flat-pattern technique and are skipped.
 *
 * "Outer apex edge" is the Frame Unroll bounding-box edge at the extreme-Y extent of the
 * miter joint -- the fold line at maximum bend radius (the outside of the corner).
 *
 * When multiple selected bodies share a miter joint, each independently yields one outer
 * apex edge at the same world-space location.  Midpoint-based deduplication collapses those
 * to a single entry so exactly one constructor is placed per unique physical joint.
 *
 * The constructor is oriented so that:
 *   - the origin sits at the midpoint of the outer apex edge,
 *   - the X axis is the outward normal of the miter (cap) face, and
 *   - the Z axis is cross(capFaceNormal, outerWallNormal) -- the fold-line direction derived
 *     entirely from stable, outward-pointing face normals, with no dependency on B-rep edge
 *     orientation (which can flip).
 *
 * Composite frame bodies are automatically unpacked to their constituent solid segments.
 */
annotation { "Feature Type Name" : "Kirigami Tube Bend",
             "Feature Type Description" : "Places one Kirigami Bend Constructor at the outer apex edge of each shared, non-zero, single-axis miter joint on the selected Onshape frame bodies." }
export const kirigamiTubeBend = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Frame Bodies",
                     "Filter" : EntityType.BODY && (BodyType.SOLID || BodyType.COMPOSITE),
                     "Description" : "Select Onshape frame bodies created with the Frame feature" }
        definition.frameBodies is Query;
    }
    {
        // Unpack a composite selection to its constituent solid segments, matching the
        // body-handling pattern used by the Frame Unroll feature.
        if (!isQueryEmpty(context, qBodyType(definition.frameBodies, BodyType.COMPOSITE)))
        {
            if (evaluateQueryCount(context, definition.frameBodies) > 1)
                throw regenError("When selecting a composite body, only one composite may be selected at a time. " ~
                    "To process multiple segments, select their individual solid bodies or a single composite.", ["frameBodies"]);

            definition.frameBodies = qContainedInCompositeParts(qNthElement(definition.frameBodies, 0));
        }

        const frameBodiesArray = evaluateQuery(context, definition.frameBodies);
        if (size(frameBodiesArray) == 0)
            throw regenError("Select at least one Onshape frame body.", ["frameBodies"]);

        // Collect one outer apex edge per eligible cap face per body.
        // Three filters are applied before collecting an edge:
        //   - isFrameTerminus = false  (internal joint only, not a free end)
        //   - miter angle != 0         (non-perpendicular cut)
        //   - simple single-axis miter (no compound miters)
        // After collection, midpoint deduplication removes the duplicate edge that arises
        // when both bodies at a shared joint are selected.
        var allOuterEdges = [];

        for (var bodyIndex = 0; bodyIndex < size(frameBodiesArray); bodyIndex += 1)
        {
            const frameBody = frameBodiesArray[bodyIndex];

            if (isQueryEmpty(context, qHasAttributeWithValueMatching(
                        qOwnedByBody(frameBody, EntityType.FACE),
                        FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                        { "topologyType" : FrameTopologyType.CAP_FACE })))
                throw regenError("Frame body " ~ (bodyIndex + 1) ~ " has no cap faces. " ~
                    "Ensure all selected bodies were created with the Onshape Frame feature.",
                    ["frameBodies"]);

            // Only internal joint cap faces.  Terminus (free-end) faces carry
            // isFrameTerminus = true and are deliberately excluded.
            const internalCapFaceQuery = qHasAttributeWithValueMatching(
                    qOwnedByBody(frameBody, EntityType.FACE),
                    FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                    { "topologyType" : FrameTopologyType.CAP_FACE, "isFrameTerminus" : false });

            if (isQueryEmpty(context, internalCapFaceQuery))
                continue; // Body has no joints with adjacent frame members (all free ends).

            // Tube axis: the local sweep direction at this body, derived from two non-parallel
            // swept wall face normals via cross product.  No dependency on evLine or edge
            // B-rep orientation.  NOTE: qAdjacent does not work across bodies; all queries
            // here stay strictly within frameBody.
            const sweptFacesArray = evaluateQuery(context, qHasAttributeWithValueMatching(
                        qOwnedByBody(frameBody, EntityType.FACE),
                        FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                        { "topologyType" : FrameTopologyType.SWEPT_FACE }));

            const tubeAxis = getTubeAxisFromSweptFaces(context, sweptFacesArray);
            if (tubeAxis == undefined)
                continue; // Cannot determine tube axis (fewer than 2 non-parallel swept faces).

            const internalCapFacesArray = evaluateQuery(context, internalCapFaceQuery);

            for (var capFaceIndex = 0; capFaceIndex < size(internalCapFacesArray); capFaceIndex += 1)
            {
                const capFace = internalCapFacesArray[capFaceIndex];
                const capFaceNormal = evFaceTangentPlane(context, {
                                "face" : capFace,
                                "parameter" : vector(0.5, 0.5)
                            }).normal;

                // Skip 0-degree (perpendicular) cuts: cap face normal parallel to tube axis
                // means no miter tilt, so no kirigami geometry is needed.
                if (parallelVectors(capFaceNormal, tubeAxis))
                    continue;

                // Skip compound miters: the projection of the cap face normal onto the plane
                // perpendicular to the tube axis must be parallel to one of the swept wall
                // face normals (one primary profile axis).  If it is not, the miter tilts
                // around two profile axes simultaneously and cannot be produced by this
                // flat-pattern technique.
                if (!isCapFaceSimpleMiter(context, sweptFacesArray, capFaceNormal, tubeAxis))
                    continue;

                const outerEdge = findOuterApexEdgeForCapFace(context, id, bodyIndex,
                        frameBody, capFace);
                allOuterEdges = append(allOuterEdges, outerEdge);
            }
        }

        // Remove coincident outer edges that arise when two selected bodies share a miter joint.
        // Each unique world-space midpoint represents one joint and therefore one import.
        const uniqueOuterEdges = deduplicateApexEdgesByMidpoint(context, allOuterEdges);

        // Queue one KirigamiBendConstructor instance per unique outer apex edge.
        const instantiator = newInstantiator(id + "bendConstructorInstances");
        var pendingInstances = [];

        for (var instanceIndex = 0; instanceIndex < size(uniqueOuterEdges); instanceIndex += 1)
        {
            const apexEdge = uniqueOuterEdges[instanceIndex];
            const apexCoordSystem = buildApexCoordSystem(context, id, instanceIndex, apexEdge);

            // toWorld(apexCoordSystem) is the Transform that carries geometry from the
            // constructor's local origin to the correct world-space position and orientation.
            const instanceQuery = addInstance(instantiator, KirigamiBendConstructor::build, {
                        "transform" : toWorld(apexCoordSystem),
                        "name" : "bend" ~ instanceIndex
                    });

            pendingInstances = append(pendingInstances, {
                        "query" : instanceQuery,
                        "coordSystem" : apexCoordSystem,
                        "instanceIndex" : instanceIndex
                    });
        }

        // Bring all queued instances into the context in one batched call.
        instantiate(context, instantiator);

        // Tag each placed body with its local frame data for downstream flat-layout processing.
        for (var instance in pendingInstances)
        {
            const apexCS = instance.coordSystem;
            setAttribute(context, {
                        "entities" : instance.query,
                        "name" : KIRIGAMI_BEND_ATTRIBUTE_NAME,
                        "attribute" : {
                            "instanceIndex" : instance.instanceIndex,
                            "apexOrigin" : apexCS.origin,
                            "zAxis" : apexCS.zAxis,
                            "xAxis" : apexCS.xAxis
                        } as KirigamiBendAttribute
                    });
        }
    });

// Derives the tube sweep axis direction from the normals of two non-parallel swept wall faces.
//
// For a straight box tube the swept wall faces are planar, and any two adjacent (non-opposite)
// faces have normals that are both perpendicular to the tube axis.  Their cross product is
// therefore parallel to the tube axis.  This approach requires no evLine call on swept edges
// and is independent of edge B-rep orientation.
//
// If all swept face normals are parallel (degenerate profile, or a single-face round tube)
// the function returns undefined and the caller should skip the body.
//
// NOTE: The caller must ensure that sweptFacesArray contains queries for faces on a single
// body only -- qAdjacent does not cross body boundaries, and the face normals are meaningful
// only within the context of their own solid.
//
// @param context          : Active context.
// @param sweptFacesArray  : Array of Query, each resolving to one SWEPT_FACE on the body.
// @returns Vector (direction) or undefined if the tube axis cannot be determined.
function getTubeAxisFromSweptFaces(context is Context, sweptFacesArray is array)
{
    if (size(sweptFacesArray) < 2)
        return undefined;

    const firstNormal = evFaceTangentPlane(context, {
                "face" : sweptFacesArray[0],
                "parameter" : vector(0.5, 0.5)
            }).normal;

    for (var faceIndex = 1; faceIndex < size(sweptFacesArray); faceIndex += 1)
    {
        const otherNormal = evFaceTangentPlane(context, {
                    "face" : sweptFacesArray[faceIndex],
                    "parameter" : vector(0.5, 0.5)
                }).normal;

        if (!parallelVectors(firstNormal, otherNormal))
            return normalize(cross(firstNormal, otherNormal));
    }

    return undefined; // All swept face normals are parallel -- cannot determine tube axis.
}

// Returns true if the given cap face represents a simple single-axis miter.
//
// A simple miter is a rotation of the cut plane around exactly ONE of the tube's primary
// cross-section axes.  For a box tube those axes are the outward normals of the swept wall
// faces.  The test is: project the cap face normal onto the plane perpendicular to the tube
// axis; if the resulting vector is parallel to any swept wall face normal, the miter is
// a single-axis rotation.  If not, the cut is tilted around two profile axes simultaneously
// (a compound miter), which cannot be produced by this flat-pattern kirigami technique.
//
// Pre-condition: capFaceNormal must NOT be parallel to tubeAxis (0-degree cut case must be
// filtered before calling this function; the perpendicular projection would be near-zero).
//
// @param context          : Active context.
// @param sweptFacesArray  : Array of Query for the SWEPT_FACEs of the same body as capFace.
// @param capFaceNormal    : Outward normal of the cap face (dimensionless direction vector).
// @param tubeAxis         : Tube sweep direction (dimensionless direction vector).
// @returns boolean
function isCapFaceSimpleMiter(context is Context, sweptFacesArray is array,
    capFaceNormal is Vector, tubeAxis is Vector) returns boolean
{
    // Component of the cap face normal in the plane perpendicular to the tube axis.
    // Referred to as the "transverse component" because it lies in the cross-section plane.
    // For a simple single-axis miter this vector is parallel to exactly one swept wall face normal.
    const capFaceNormalTransverseComponent = capFaceNormal - dot(capFaceNormal, tubeAxis) * tubeAxis;

    for (var faceIndex = 0; faceIndex < size(sweptFacesArray); faceIndex += 1)
    {
        const sweptFaceNormal = evFaceTangentPlane(context, {
                    "face" : sweptFacesArray[faceIndex],
                    "parameter" : vector(0.5, 0.5)
                }).normal;

        if (parallelVectors(capFaceNormalTransverseComponent, sweptFaceNormal))
            return true;
    }

    return false;
}

// Finds the single outer apex edge on one cap face of a frame body.
//
// Implements the same bounding-box outer-edge detection used by Neil Cooke's Frame Unroll
// feature.  A local coordinate system is built from the first candidate edge's tangent
// (X axis) and the adjacent swept face's outward normal (Z axis).  The body is evaluated in
// that frame with evBox3d; the outer apex edge is the candidate edge that lies in a plane
// through the extreme-Y bounding-box corner -- i.e., the edge at the outermost extent of
// the tube in the direction perpendicular to both the fold line and the tube wall.
//
// minCorner is checked before maxCorner, matching Frame Unroll's iteration order.  Only one
// of the two corners will coincide with a candidate edge; that edge is the outer apex edge.
//
// @param context    : Active context.
// @param id         : Feature id used for error reporting.
// @param bodyIndex  : Zero-based index of this body in the selection (for error messages).
// @param frameBody  : Query resolving to a single solid frame body.
// @param capFace    : Query resolving to one cap face on frameBody.
// @returns Query    : The single outer apex edge on this cap face.
function findOuterApexEdgeForCapFace(context is Context, id is Id, bodyIndex is number,
    frameBody is Query, capFace is Query) returns Query
{
    // Swept faces of this body that border the cap face.  Using FrameTopologyAttribute
    // (the same layer as qFrameStartFace / qFrameEndFace) avoids any geometry comparison.
    const allBodySweptFaces = qHasAttributeWithValueMatching(
            qOwnedByBody(frameBody, EntityType.FACE),
            FRAME_ATTRIBUTE_TOPOLOGY_NAME,
            { "topologyType" : FrameTopologyType.SWEPT_FACE });

    const sweptFacesAdjacentToCapFace = qIntersection([
                qAdjacent(capFace, AdjacencyType.EDGE, EntityType.FACE),
                allBodySweptFaces
            ]);

    if (isQueryEmpty(context, sweptFacesAdjacentToCapFace))
        throw regenError("Frame body " ~ (bodyIndex + 1) ~ " cap face has no adjacent swept (tube wall) faces. " ~
            "Ensure the selected body is an Onshape frame member created with the Frame feature.",
            ["frameBodies"]);

    // Candidate apex edges: edges that border both the cap face and a swept face.
    // For a rectangular box tube this yields the 4 perimeter edges of the cap face.
    const candidateEdges = qIntersection([
                qAdjacent(capFace,                   AdjacencyType.EDGE, EntityType.EDGE),
                qAdjacent(sweptFacesAdjacentToCapFace, AdjacencyType.EDGE, EntityType.EDGE)
            ]);

    if (isQueryEmpty(context, candidateEdges))
        throw regenError("No apex edges found on a cap face of frame body " ~ (bodyIndex + 1) ~ ". " ~
            "Ensure the body has a mitered end face adjacent to its tube wall faces.",
            ["frameBodies"]);

    // Build a local coordinate system identical to the one Frame Unroll constructs:
    //   X axis = tangent of the first candidate edge at its midpoint
    //   Z axis = outward normal of the first adjacent swept face
    //   Y axis = cross(Z, X)  [implicit in CoordSystem]
    // The Y axis points across the miter face toward the outer/inner extents of the tube.
    // NOTE: this is a helper frame solely for bounding-box analysis.  The placement coord
    // system built by buildApexCoordSystem uses a different axis convention (cap face normal
    // = X, edge tangent = Z) that orients the constructor geometry correctly at the joint.
    const firstEdgeLine = evEdgeTangentLine(context, {
                "edge" : qNthElement(candidateEdges, 0),
                "parameter" : 0.5
            });

    const sweptFaceNormal = evFaceTangentPlane(context, {
                "face" : qNthElement(sweptFacesAdjacentToCapFace, 0),
                "parameter" : vector(0.5, 0.5)
            }).normal;

    const localCoordSystem = coordSystem(firstEdgeLine.origin, firstEdgeLine.direction, sweptFaceNormal);

    // Bounding box of the body measured in the local coordinate system.
    // The Y extents (minCorner.y and maxCorner.y) locate the outermost and innermost
    // cap-face boundary edges relative to the tube wall.
    const bodyBoundingBox = evBox3d(context, {
                "topology" : frameBody,
                "cSys" : localCoordSystem,
                "tight" : true
            });

    // The outer apex edge lies in a plane at one of the bounding-box Y extremes.
    // A plane through the bounding-box corner with normal = yAxis(localCoordSystem) is a
    // constant-Y plane in the local frame; qCoincidesWithPlane selects the candidate edge
    // that lies entirely in that plane.  minCorner is checked first, then maxCorner,
    // matching the iteration order used by Frame Unroll.
    var outerEdge = qNothing();

    for (var corner in [bodyBoundingBox.minCorner, bodyBoundingBox.maxCorner])
    {
        const cornerPlane = plane(toWorld(localCoordSystem, corner), yAxis(localCoordSystem));
        outerEdge = qCoincidesWithPlane(candidateEdges, cornerPlane);
        if (!isQueryEmpty(context, outerEdge))
            break;
    }

    if (isQueryEmpty(context, outerEdge))
        throw regenError("Could not determine the outer apex edge on a cap face of frame body " ~
            (bodyIndex + 1) ~ ". Ensure the body is a properly formed Onshape frame member.",
            ["frameBodies"]);

    return qNthElement(outerEdge, 0);
}

// Removes geometrically duplicate outer apex edges from the input array.
//
// When two selected frame bodies share a miter joint, each body contributes one outer apex
// edge at the same world-space location.  This function retains only the first edge found
// at each unique midpoint, ensuring exactly one Kirigami Bend Constructor is placed per
// physical miter joint regardless of how many of the adjacent bodies are selected.
//
// Midpoint comparison uses tolerantEquals(Vector, Vector) from vector.fs, which applies
// TOLERANCE.zeroLength for length vectors -- appropriate for edges that are truly coincident
// (same miter cut) but not for edges that are merely close.
//
// @param context    : Active context.
// @param outerEdges : Array of Query, one outer apex edge per cap face per selected body.
// @returns array    : Deduplicated array of Query, one per unique joint location.
function deduplicateApexEdgesByMidpoint(context is Context, outerEdges is array) returns array
{
    var uniqueEdges = [];
    var seenMidpoints = [];

    for (var edgeQuery in outerEdges)
    {
        const midpoint = evEdgeTangentLine(context, {
                    "edge" : edgeQuery,
                    "parameter" : 0.5
                }).origin;

        var isDuplicate = false;
        for (var seenMidpoint in seenMidpoints)
        {
            if (tolerantEquals(midpoint, seenMidpoint))
            {
                isDuplicate = true;
                break;
            }
        }

        if (!isDuplicate)
        {
            uniqueEdges    = append(uniqueEdges,    edgeQuery);
            seenMidpoints  = append(seenMidpoints,  midpoint);
        }
    }

    return uniqueEdges;
}

// Builds a stable coordinate system centered on the midpoint of the given apex edge.
//
// Axis convention:
//   origin : world-space midpoint of the outer apex edge
//   X axis : outward normal of the adjacent cap (miter) face
//   Z axis : cross(capFaceNormal, outerWallNormal) -- direction along the fold line
//   Y axis : implied by the right-hand rule (cross(Z, X))
//
// Stability guarantee: both the X and Z axes are derived exclusively from outward face
// normals returned by evFaceTangentPlane, which always gives the outward-pointing normal
// for a solid face and is entirely independent of B-rep edge orientation.  The previous
// implementation used evEdgeTangentLine.direction for the Z axis; that tangent can be
// returned in either direction (+/-) depending on how the edge happens to be stored in the
// B-rep, causing intermittent CSYS flips.  Replacing it with cross(capFaceNormal, outerWallNormal)
// removes that dependency: both inputs are stable face normals, so the cross product is
// deterministic for any given geometry.
//
// cross(capFaceNormal, outerWallNormal) is always perpendicular to capFaceNormal
// (by the definition of the cross product), satisfying the coordSystem precondition that
// xAxis and zAxis must be mutually perpendicular.
//
// @param context       : Active context.
// @param id            : Feature id, passed through for error reporting in sub-calls.
// @param instanceIndex : Global instance counter (for error messages and instance naming).
// @param apexEdge      : Query resolving to a single outer apex edge on a mitered frame body.
// @returns CoordSystem aligned to the miter joint at the outer apex edge midpoint.
function buildApexCoordSystem(context is Context, id is Id, instanceIndex is number, apexEdge is Query) returns CoordSystem
{
    // Origin: world-space midpoint of the outer apex edge.
    const edgeMidpoint = evEdgeTangentLine(context, {
                "edge" : apexEdge,
                "parameter" : 0.5
            }).origin;

    // Identify the two faces bordering the outer apex edge.
    const miterFace    = findCapFaceAdjacentToApexEdge(context, id, instanceIndex, apexEdge);
    const outerWallFace = findSweptFaceAdjacentToApexEdge(context, id, instanceIndex, apexEdge);

    // X axis: outward normal of the miter (cap) face.
    // For a planar face the normal is constant at any UV parameter, so (0.5, 0.5) is safe.
    const capFaceNormal = evFaceTangentPlane(context, {
                "face" : miterFace,
                "parameter" : vector(0.5, 0.5)
            }).normal;

    // Z axis: direction along the outer apex edge (the fold line).
    // Derived from the cross product of the two outward face normals bounding that edge.
    // cross(capFaceNormal, outerWallNormal) lies at the intersection of the two face planes
    // (which is exactly the outer apex edge) and is determined entirely by stable face normals.
    const outerWallNormal = evFaceTangentPlane(context, {
                "face" : outerWallFace,
                "parameter" : vector(0.5, 0.5)
            }).normal;

    const foldLineDirection = normalize(cross(capFaceNormal, outerWallNormal));

    return coordSystem(edgeMidpoint, capFaceNormal, foldLineDirection);
}

// Finds the swept wall face (SWEPT_FACE) adjacent to a single outer apex edge.
//
// The outer apex edge is bounded by exactly two faces: the cap face on one side and
// the swept (tube wall) face on the other.  This function retrieves the swept face by
// filtering the edge's adjacent faces to those carrying a SWEPT_FACE attribute.
//
// @param context       : Active context.
// @param id            : Feature id used for error reporting.
// @param instanceIndex : Global instance counter (for error messages).
// @param apexEdge      : Query resolving to a single outer apex edge on a mitered frame body.
// @returns Query       : The swept wall face adjacent to the apex edge.
function findSweptFaceAdjacentToApexEdge(context is Context, id is Id, instanceIndex is number, apexEdge is Query) returns Query
{
    const adjacentFaces = qAdjacent(apexEdge, AdjacencyType.EDGE, EntityType.FACE);
    const sweptWallFaceQuery = qHasAttributeWithValueMatching(adjacentFaces,
            FRAME_ATTRIBUTE_TOPOLOGY_NAME,
            { "topologyType" : FrameTopologyType.SWEPT_FACE });

    if (isQueryEmpty(context, sweptWallFaceQuery))
        throw regenError("Instance " ~ (instanceIndex + 1) ~ ": outer apex edge has no adjacent swept wall face. " ~
            "Unexpected topology -- ensure the selected body is an unmodified Onshape frame member.",
            ["frameBodies"]);

    return qNthElement(sweptWallFaceQuery, 0);
}

// Finds the frame cap face (mitered end face) adjacent to a single apex edge.
//
// Filters the two faces bordering the edge to the one carrying a CAP_FACE
// FrameTopologyAttribute, using qHasAttributeWithValueMatching -- identical to the
// qFrameTopology helper in frameUtils.fs that backs qFrameStartFace and qFrameEndFace.
//
// Because the apex edge is produced by findOuterApexEdgeForCapFace, which selects only
// edges shared by a cap face and a swept face, the empty-query guard here is a safety
// check for unexpected topology rather than normal flow.
//
// @param context       : Active context.
// @param id            : Feature id used for error reporting.
// @param instanceIndex : Global instance counter (for error messages).
// @param apexEdge      : Query resolving to a single apex edge on a mitered frame body.
// @returns Query       : The cap face adjacent to the apex edge.
function findCapFaceAdjacentToApexEdge(context is Context, id is Id, instanceIndex is number, apexEdge is Query) returns Query
{
    const adjacentFaces = qAdjacent(apexEdge, AdjacencyType.EDGE, EntityType.FACE);
    const capFaceQuery  = qHasAttributeWithValueMatching(adjacentFaces,
            FRAME_ATTRIBUTE_TOPOLOGY_NAME,
            { "topologyType" : FrameTopologyType.CAP_FACE });

    if (isQueryEmpty(context, capFaceQuery))
        throw regenError("Instance " ~ (instanceIndex + 1) ~ ": apex edge has no adjacent cap face. " ~
            "Unexpected topology -- ensure the selected body is an unmodified Onshape frame member.",
            ["frameBodies"]);

    // For a well-formed miter joint each apex edge borders exactly one cap face.
    return qNthElement(capFaceQuery, 0);
}
