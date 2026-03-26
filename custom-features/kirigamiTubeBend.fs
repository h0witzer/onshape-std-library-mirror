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
 * For each selected Onshape frame body, automatically locates every apex edge
 * (the edges where a mitered cap face meets a swept tube wall face) and places one
 * Kirigami Bend Constructor instance there.
 *
 * The constructor is oriented so that:
 *   - the local origin sits at the midpoint of the apex edge,
 *   - the local Z axis runs along the edge tangent direction, and
 *   - the local X axis aligns with the outward normal of the adjacent frame cap face,
 *     identified via the FrameTopologyAttribute assigned by the Onshape Frame feature --
 *     the same attribute layer the cut list reads to find start/end cap faces.
 *
 * Each placed body is tagged with a KirigamiBendAttribute for downstream flat-layout export.
 * Composite frame bodies are automatically unpacked to their constituent solid segments.
 */
annotation { "Feature Type Name" : "Kirigami Tube Bend",
             "Feature Type Description" : "Automatically finds and processes all mitered apex edges on selected Onshape frame bodies, placing a Kirigami Bend Constructor at each one." }
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

        // Queue one KirigamiBendConstructor instance per apex edge across all selected bodies.
        // instanceCount provides a unique name/index for every placed instance regardless of
        // which body or edge it came from.
        const instantiator = newInstantiator(id + "bendConstructorInstances");
        var pendingInstances = [];
        var instanceCount = 0;

        for (var bodyIndex = 0; bodyIndex < size(frameBodiesArray); bodyIndex += 1)
        {
            const frameBody = frameBodiesArray[bodyIndex];
            const apexEdgesOnBody = evaluateQuery(context,
                findApexEdgesOnFrameBody(context, id, bodyIndex, frameBody));

            for (var edgeIndex = 0; edgeIndex < size(apexEdgesOnBody); edgeIndex += 1)
            {
                const apexEdge = apexEdgesOnBody[edgeIndex];
                const apexCoordSystem = buildApexCoordSystem(context, id, instanceCount, apexEdge);

                // toWorld(apexCoordSystem) is the Transform that carries geometry from the
                // constructor's local origin to the correct world-space position and orientation.
                const instanceQuery = addInstance(instantiator, KirigamiBendConstructor::build, {
                            "transform" : toWorld(apexCoordSystem),
                            "name" : "bend" ~ instanceCount
                        });

                pendingInstances = append(pendingInstances, {
                            "query" : instanceQuery,
                            "coordSystem" : apexCoordSystem,
                            "instanceIndex" : instanceCount
                        });

                instanceCount += 1;
            }
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

// Automatically finds all apex edges on a single frame body.
//
// An apex edge is any edge that lies on the boundary of both a frame cap face (the mitered
// end face, FrameTopologyType.CAP_FACE) and a swept face (a tube wall face,
// FrameTopologyType.SWEPT_FACE).  These are the fold lines where the kirigami bend geometry
// must be placed.
//
// Both face types are identified purely through their FrameTopologyAttribute, which the
// Onshape Frame feature assigns during body creation -- the same attribute that qFrameStartFace,
// qFrameEndFace, and the cut list internals read to locate cap faces on frame bodies.
//
// @param context   : Active context.
// @param id        : Feature id used for error reporting.
// @param bodyIndex : Zero-based index of this body in the selection (for error messages).
// @param frameBody : Query resolving to a single solid frame body.
// @returns Query   : All apex edges on this frame body.
function findApexEdgesOnFrameBody(context is Context, id is Id, bodyIndex is number, frameBody is Query) returns Query
{
    const ownedFaces = qOwnedByBody(frameBody, EntityType.FACE);

    // Retrieve cap faces and swept faces via FrameTopologyAttribute.
    // This is the same qFrameTopology filter pattern used by qFrameStartFace / qFrameEndFace
    // in frameUtils.fs and throughout cutlistMath.fs, frameTrim.fs, and frame.fs.
    const capFaces = qHasAttributeWithValueMatching(ownedFaces,
            FRAME_ATTRIBUTE_TOPOLOGY_NAME,
            { "topologyType" : FrameTopologyType.CAP_FACE });

    const sweptFaces = qHasAttributeWithValueMatching(ownedFaces,
            FRAME_ATTRIBUTE_TOPOLOGY_NAME,
            { "topologyType" : FrameTopologyType.SWEPT_FACE });

    if (isQueryEmpty(context, capFaces))
        throw regenError("Frame body " ~ (bodyIndex + 1) ~ " has no cap faces. " ~
            "Ensure all selected bodies were created with the Onshape Frame feature.",
            ["frameBodies"]);

    // Edges that bound a cap face AND a swept face are the miter fold lines (apex edges).
    // qAdjacent with AdjacencyType.EDGE on a face query returns the edges on the perimeter
    // of those faces; the intersection of the two sets yields only edges shared by both types.
    const capFaceEdges   = qAdjacent(capFaces,   AdjacencyType.EDGE, EntityType.EDGE);
    const sweptFaceEdges = qAdjacent(sweptFaces, AdjacencyType.EDGE, EntityType.EDGE);
    const apexEdges      = qIntersection([capFaceEdges, sweptFaceEdges]);

    if (isQueryEmpty(context, apexEdges))
        throw regenError("No apex edges found on frame body " ~ (bodyIndex + 1) ~ ". " ~
            "Ensure the body has a mitered end face adjacent to its tube wall faces.",
            ["frameBodies"]);

    return apexEdges;
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
// Because apex edges are only produced by findApexEdgesOnFrameBody, which already guarantees
// that each edge borders at least one cap face, the empty-query guard here serves as a
// safety check for unexpected topology rather than normal flow.
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
