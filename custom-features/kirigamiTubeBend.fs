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
 *   zAxis         {Vector} : Edge tangent direction -- local Z, runs along the fold line.
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
 * For each selected Onshape frame body, automatically locates the single outer apex edge
 * at each mitered end face and places one Kirigami Bend Constructor instance there.
 *
 * "Outer apex edge" is defined the same way Neil Cooke's Frame Unroll feature defines it:
 * the edge at the outermost extent of the miter joint, found by building a local bounding
 * box and selecting the edge that coincides with the extreme Y plane of that box.  This is
 * the fold line at the outside of the bend (maximum bend radius).
 *
 * When multiple selected bodies share a miter joint, both contribute an outer apex edge at
 * the same world-space position.  Those duplicates are collapsed to a single import so
 * exactly one Kirigami Bend Constructor is placed per unique joint location.
 *
 * The constructor is oriented so that:
 *   - the local origin sits at the midpoint of the outer apex edge,
 *   - the local Z axis runs along the edge tangent direction, and
 *   - the local X axis aligns with the outward normal of the adjacent frame cap face.
 *
 * Each placed body is tagged with a KirigamiBendAttribute for downstream flat-layout export.
 * Composite frame bodies are automatically unpacked to their constituent solid segments.
 */
annotation { "Feature Type Name" : "Kirigami Tube Bend",
             "Feature Type Description" : "Finds the outer apex edge at every mitered joint on selected Onshape frame bodies and places one Kirigami Bend Constructor per unique joint." }
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

        // Collect exactly one outer apex edge per cap face per body.
        // A rectangular box tube has two cap faces (one at each mitered end), so this yields
        // at most two outer edges per body.  When two selected bodies share a miter joint each
        // contributes its own outer edge at the same world position -- deduplication below
        // collapses those to a single entry.
        var allOuterEdges = [];

        for (var bodyIndex = 0; bodyIndex < size(frameBodiesArray); bodyIndex += 1)
        {
            const frameBody = frameBodiesArray[bodyIndex];
            const capFaceQuery = qHasAttributeWithValueMatching(
                    qOwnedByBody(frameBody, EntityType.FACE),
                    FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                    { "topologyType" : FrameTopologyType.CAP_FACE });

            if (isQueryEmpty(context, capFaceQuery))
                throw regenError("Frame body " ~ (bodyIndex + 1) ~ " has no cap faces. " ~
                    "Ensure all selected bodies were created with the Onshape Frame feature.",
                    ["frameBodies"]);

            const capFacesArray = evaluateQuery(context, capFaceQuery);

            for (var capFaceIndex = 0; capFaceIndex < size(capFacesArray); capFaceIndex += 1)
            {
                const outerEdge = findOuterApexEdgeForCapFace(context, id, bodyIndex,
                        frameBody, capFacesArray[capFaceIndex]);
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

// Builds a coordinate system centered on the midpoint of the given apex edge.
//
// The local frame is defined as:
//   origin : world-space midpoint of the apex edge
//   Z axis : edge tangent direction at the midpoint (runs along the fold line)
//   X axis : outward normal of the adjacent frame cap face (the mitered end face)
//
// @param context       : Active context.
// @param id            : Feature id, passed through for error reporting in sub-calls.
// @param instanceIndex : Global instance counter (for error messages and instance naming).
// @param apexEdge      : Query resolving to a single apex edge on a mitered frame body.
// @returns CoordSystem aligned to the local miter geometry at the apex edge midpoint.
function buildApexCoordSystem(context is Context, id is Id, instanceIndex is number, apexEdge is Query) returns CoordSystem
{
    // Evaluate the edge midpoint (origin) and tangent direction (Z axis).
    const edgeMidpointLine = evEdgeTangentLine(context, {
                "edge" : apexEdge,
                "parameter" : 0.5
            });
    const edgeMidpoint       = edgeMidpointLine.origin;
    const edgeTangentDirection = edgeMidpointLine.direction;

    // Identify the miter end face via FrameTopologyAttribute -- no geometric heuristics.
    const miterFace = findCapFaceAdjacentToApexEdge(context, id, instanceIndex, apexEdge);

    // Sample the cap face normal at the exact edge midpoint location.
    // evFaceTangentPlaneAtEdge evaluates the normal at a specific edge parameter rather than
    // at an arbitrary interior UV parameter of the face, keeping the frame axes consistent.
    const miterFacePlane = evFaceTangentPlaneAtEdge(context, {
                "edge" : apexEdge,
                "face" : miterFace,
                "parameter" : 0.5
            });
    const xAxisDirection = miterFacePlane.normal;

    return coordSystem(edgeMidpoint, xAxisDirection, edgeTangentDirection);
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
