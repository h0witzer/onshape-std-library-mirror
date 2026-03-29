FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/geomOperations.fs", version : "2909.0");
import(path : "onshape/std/frameAttributes.fs", version : "2909.0");
import(path : "onshape/std/frameUtils.fs", version : "2909.0");
// External Part Studio: Kirigami Bend Constructor.  This is a template part studio whose
// geometry represents the unfolded bend tab inserted at each miter joint.  One instance is
// derived into the active studio per unique joint; the downstream flat-layout script locates
// each instance via KirigamiBendAttribute and booleans the unfolded segments together for
// laser-cut export.
KirigamiBendConstructor::import(path : "1173cc57cdf5a7d688426b78", version : "d2c5a6c4ebd8f320a741122a");

// Named key used when attaching KirigamiBendAttribute to instantiated bodies.
const KIRIGAMI_BEND_ATTRIBUTE_NAME = "kirigamiBendData";

/**
 * Attribute stored on each Kirigami Bend Constructor body placed by this feature.
 * A downstream flat-layout script queries these attributes to locate, orient, and
 * boolean the unfolded segments together for laser-cut export.
 *
 * Fields:
 *   instanceIndex    {number}         : Zero-based counter across all apex edges of all selected bodies.
 *   apexOrigin       {Vector}         : World-space midpoint of the apex edge (origin of the local frame).
 *   zAxis            {Vector}         : Fold-line direction -- local Z, along the outer apex edge,
 *                                       derived as cross(capFaceNormal, outerWallNormal).
 *   xAxis            {Vector}         : Miter cap face outward normal -- local X, perpendicular to the fold plane.
 *   boxTubeHeight    {ValueWithUnits} : Cross-section bounding-box extent measured along the Z axis of the
 *                                       local joint coordinate system (apexCoordSystem.zAxis = the fold-line
 *                                       direction, cross(capFaceNormal, outerWallNormal)).  This is the tube
 *                                       profile dimension that runs along the outer apex edge.
 *   boxTubeWidth     {ValueWithUnits} : Cross-section bounding-box extent along the remaining direction
 *                                       perpendicular to both the tube sweep axis and the fold-line axis
 *                                       (cross(tubeAxis, foldLineDirection)).  Neither world-axis references
 *                                       nor any world-alignment assumption are used in either dimension.
 *   miterAngle       {ValueWithUnits} : Angle between the cap face normal and the tube sweep axis, in [0, 90]
 *                                       degrees.  Matches the Onshape frame cut-list convention (0 = perpendicular
 *                                       cut, 45 = 45-degree miter).
 *   bendOutsideRadius {ValueWithUnits}: User-specified bend outside radius passed through from the feature input.
 *   offsetToInteriorSweepLine {ValueWithUnits}: Perpendicular distance from the outer wall face to the nearest
 *                                               longitudinal sweep edge that borders a swept face whose normal is
 *                                               parallel to the local Z axis (the fold-line direction).  For a
 *                                               hollow box tube this equals the wall thickness measured perpendicular
 *                                               to both the tube sweep axis and the fold-line direction.
 */
export type KirigamiBendAttribute typecheck canBeKirigamiBendAttribute;

export predicate canBeKirigamiBendAttribute(value)
{
    value is map;
    value.instanceIndex is number;
    is3dLengthVector(value.apexOrigin);
    is3dDirection(value.zAxis);
    is3dDirection(value.xAxis);
    isLength(value.boxTubeHeight);
    isLength(value.boxTubeWidth);
    isAngle(value.miterAngle);
    isLength(value.bendOutsideRadius);
    isLength(value.offsetToInteriorSweepLine);
}

/**
 * For each selected Onshape frame body, locates the single outer apex edge at every
 * shared miter joint and places one Kirigami Bend Constructor instance there.
 *
 * A joint is eligible for a constructor only when ALL of the following hold:
 *   1. The joint is SHARED: the same cap face midpoint must appear in the collected edges
 *      of at least two selected bodies.  Free ends (cap faces with no counterpart on another
 *      selected body) are automatically excluded because their midpoint is seen only once.
 *      This also means the feature requires at least two bodies to be selected.
 *   2. The miter angle is non-zero: a 0-degree cut (cap face normal parallel to the tube
 *      axis) is a perpendicular butt joint that requires no kirigami geometry.
 *   3. The miter is a simple single-axis rotation: the component of the cap face normal
 *      perpendicular to the tube axis must be parallel to one of the tube's swept wall face
 *      normals.  Compound miters (rotation around two profile axes simultaneously) are not
 *      achievable with this flat-pattern technique and are skipped.
 *
 * The isFrameTerminus flag is intentionally NOT used as an eligibility filter.  Terminus
 * data reflects the frame's topology in the context of the entire frame network and can mark
 * valid miter joints as terminus faces.  Shared-midpoint detection is the sole gate for
 * determining whether a joint involves two selected bodies.
 *
 * "Outer apex edge" is the Frame Unroll bounding-box edge at the extreme-Y extent of the
 * miter joint -- the fold line at maximum bend radius (the outside of the corner).
 *
 * Closed-ring topology (a pure cycle where every selected body has exactly two shared joints)
 * is detected by inspecting each body's degree in the joint graph.  When a pure cycle is
 * detected, a graph traversal starting from body 0 (the first body in the selection list)
 * visits N-1 of the N joints and deliberately omits the final "closing" joint.  This leaves
 * two open ends on the linearised chain so the flat-layout strip can be unfolded.  For an
 * open chain (at least one body has only one shared joint) all shared joints are emitted
 * unchanged.
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

        annotation { "Name" : "Bend Outside Radius",
                     "Description" : "Outside radius of the tube bend at each miter joint, used by the downstream flat-layout script to size the kirigami geometry",
                     "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.bendOutsideRadius, NONNEGATIVE_ZERO_INCLUSIVE_LENGTH_BOUNDS);
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
        if (size(frameBodiesArray) < 2)
            throw regenError("Select at least two touching Onshape frame bodies. " ~
                "A single body has no shared joints with another selected body.", ["frameBodies"]);

        // Collect one outer apex edge per eligible cap face per body.
        // Two geometry filters are applied before collecting an edge:
        //   - miter angle != 0         (non-perpendicular cut)
        //   - simple single-axis miter (no compound miters)
        // Each entry is stored as a map { "edgeQuery", "bodyIndex" } so that
        // collectSharedApexEdges can build a connectivity graph, detect cycles, and
        // break a closed ring at the correct joint.
        var allOuterEdgeData = [];

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

            // All cap faces on this body, regardless of isFrameTerminus.
            // Terminus flags reflect network topology and can mark valid miter joints;
            // shared-midpoint detection (done after the full loop) is the sole gate.
            const allCapFaceQuery = qHasAttributeWithValueMatching(
                    qOwnedByBody(frameBody, EntityType.FACE),
                    FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                    { "topologyType" : FrameTopologyType.CAP_FACE });

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

            const allCapFacesArray = evaluateQuery(context, allCapFaceQuery);

            for (var capFaceIndex = 0; capFaceIndex < size(allCapFacesArray); capFaceIndex += 1)
            {
                const capFace = allCapFacesArray[capFaceIndex];
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
                        frameBody, capFace, tubeAxis, capFaceNormal);
                allOuterEdgeData = append(allOuterEdgeData, {
                        "edgeQuery"  : outerEdge,
                        "bodyIndex"  : bodyIndex,
                        "frameBody"  : frameBody,
                        "tubeAxis"   : tubeAxis
                    });
            }
        }

        // Retain only shared joints (midpoints contributed by two distinct bodies).
        // For a pure cycle, N-1 of the N joints are returned; the closing joint is dropped
        // to leave two open ends on the linearised strip.
        // Each entry is a data map { "edgeQuery", "bodyIndex", "frameBody", "tubeAxis" }.
        const sharedJoints = collectSharedApexEdges(context, allOuterEdgeData, size(frameBodiesArray));

        // Queue one KirigamiBendConstructor instance per shared outer apex edge.
        const instantiator = newInstantiator(id + "bendConstructorInstances");
        var pendingInstances = [];

        // Cache offsetToInteriorSweepLine by FrameProfileAttribute so bodies that share
        // the same frame section profile only pay the geometry evaluation cost once.
        // FrameProfileAttribute is a string→string map of cutlist column values that is
        // identical on every body produced from the same profile entry in the frame table.
        var profileOffsetCache = {};

        for (var instanceIndex = 0; instanceIndex < size(sharedJoints); instanceIndex += 1)
        {
            const jointData = sharedJoints[instanceIndex];
            const apexEdge = jointData.edgeQuery;
            const apexCoordSystem = buildApexCoordSystem(context, id, instanceIndex, apexEdge,
                    jointData.tubeAxis);
            const jointDimensions = computeJointDimensions(context, jointData.frameBody,
                    jointData.tubeAxis, apexCoordSystem);

            // Look up or compute the offset to the interior sweep line.
            // All bodies with the same FrameProfileAttribute have identical cross-section
            // geometry, so this result is the same for every joint on the same profile.
            // toString produces the same string for any two identically-valued attributes
            // because Onshape's Frame feature populates the map keys in table-column order,
            // making insertion order deterministic across all bodies from the same profile.
            const frameProfileAttribute = try(getFrameProfileAttribute(context, jointData.frameBody));
            const profileCacheKey = (frameProfileAttribute != undefined) ? toString(frameProfileAttribute) : undefined;

            var offsetToInteriorSweepLine;
            if (profileCacheKey != undefined && profileOffsetCache[profileCacheKey] != undefined)
            {
                offsetToInteriorSweepLine = profileOffsetCache[profileCacheKey];
            }
            else
            {
                offsetToInteriorSweepLine = computeOffsetToInteriorSweepLine(context,
                        jointData.frameBody, jointData.tubeAxis, apexCoordSystem);
                if (profileCacheKey != undefined)
                    profileOffsetCache[profileCacheKey] = offsetToInteriorSweepLine;
            }

            // toWorld(apexCoordSystem) is the Transform that carries geometry from the
            // constructor's local origin to the correct world-space position and orientation.
            const instanceQuery = addInstance(instantiator, KirigamiBendConstructor::build, {
                        "configuration" : {
                            "boxTubeHeight"            : jointDimensions.boxTubeHeight,
                            "boxTubeWidth"             : jointDimensions.boxTubeWidth,
                            "miterAngle"               : jointDimensions.miterAngle,
                            "bendOutsideRadius"        : definition.bendOutsideRadius,
                            "offsetToInteriorSweepLine": offsetToInteriorSweepLine
                        },
                        "transform" : toWorld(apexCoordSystem),
                        "name" : "bend" ~ instanceIndex
                    });

            pendingInstances = append(pendingInstances, {
                        "query"                    : instanceQuery,
                        "coordSystem"              : apexCoordSystem,
                        "instanceIndex"            : instanceIndex,
                        "boxTubeHeight"            : jointDimensions.boxTubeHeight,
                        "boxTubeWidth"             : jointDimensions.boxTubeWidth,
                        "miterAngle"               : jointDimensions.miterAngle,
                        "bendOutsideRadius"        : definition.bendOutsideRadius,
                        "offsetToInteriorSweepLine": offsetToInteriorSweepLine
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
                            "instanceIndex"            : instance.instanceIndex,
                            "apexOrigin"               : apexCS.origin,
                            "zAxis"                    : apexCS.zAxis,
                            "xAxis"                    : apexCS.xAxis,
                            "boxTubeHeight"            : instance.boxTubeHeight,
                            "boxTubeWidth"             : instance.boxTubeWidth,
                            "miterAngle"               : instance.miterAngle,
                            "bendOutsideRadius"        : instance.bendOutsideRadius,
                            "offsetToInteriorSweepLine": instance.offsetToInteriorSweepLine
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
// Candidate edges are those shared between the cap face and any adjacent swept face.
// For a simple single-axis miter the outer apex is the candidate edge whose midpoint
// projects furthest along the transverse direction:
//
//   transverseDirection = normalize(capFaceNormal - dot(capFaceNormal, tubeAxis) * tubeAxis)
//
// This is the component of the cap face normal perpendicular to the tube axis, which points
// from the tube centre toward the outer wall face for any simple miter regardless of
// orientation or corner topology.  Using this direction instead of a coordinate system
// derived from an adjacent swept face normal avoids arc-face sensitivity: for a corner-radius
// box tube (typically 16 swept faces) the first adjacent swept face can be an arc face
// whose normal at UV (0.5, 0.5) is diagonal, making any coordinate system built from it
// give unreliable Y-projections and an incorrect face-centre sign test.
//
// @param context        : Active context.
// @param id             : Feature id used for error reporting.
// @param bodyIndex      : Zero-based index of this body in the selection (for error messages).
// @param frameBody      : Query resolving to a single solid frame body.
// @param capFace        : Query resolving to one cap face on frameBody.
// @param tubeAxis       : Tube sweep direction (dimensionless unit vector).
// @param capFaceNormal  : Outward normal of capFace (dimensionless unit vector).
// @returns Query        : The single outer apex edge on this cap face.
function findOuterApexEdgeForCapFace(context is Context, id is Id, bodyIndex is number,
    frameBody is Query, capFace is Query, tubeAxis is Vector, capFaceNormal is Vector) returns Query
{
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

    // Candidate apex edges: edges that border both the cap face and any swept face.
    const candidateEdges = qIntersection([
                qAdjacent(capFace,                     AdjacencyType.EDGE, EntityType.EDGE),
                qAdjacent(sweptFacesAdjacentToCapFace, AdjacencyType.EDGE, EntityType.EDGE)
            ]);

    if (isQueryEmpty(context, candidateEdges))
        throw regenError("No apex edges found on a cap face of frame body " ~ (bodyIndex + 1) ~ ". " ~
            "Ensure the body has a mitered end face adjacent to its tube wall faces.",
            ["frameBodies"]);

    // Transverse direction: component of the cap face normal perpendicular to the tube axis.
    // Points from the tube centre toward the outer wall face for any simple single-axis miter.
    // Derived entirely from flat planar face normals; no arc-face normals are involved.
    const transverseDirection = normalize(capFaceNormal - dot(capFaceNormal, tubeAxis) * tubeAxis);

    // Return the candidate edge whose midpoint projects furthest in the transverse direction.
    const candidateEdgesArray = evaluateQuery(context, candidateEdges);
    var maximumProjection = undefined;
    var outerApexEdge     = candidateEdgesArray[0];

    for (var candidateEdge in candidateEdgesArray)
    {
        const edgeMidpoint = evEdgeTangentLine(context, {
                    "edge"      : candidateEdge,
                    "parameter" : 0.5
                }).origin;
        const projection = dot(edgeMidpoint, transverseDirection);

        if (maximumProjection == undefined || projection > maximumProjection)
        {
            maximumProjection = projection;
            outerApexEdge     = candidateEdge;
        }
    }

    return outerApexEdge;
}

// Collects the outer apex edges representing the joints that should receive bend constructors.
//
// After each body contributes its outer apex edges (tagged with their source body index via
// outerEdgeData), this function performs three operations:
//
//   1. Pre-computes the world-space midpoint of every input edge (one evEdgeTangentLine per
//      edge, cached for all subsequent passes).
//   2. Builds a joint graph: identifies every unique midpoint that is contributed by at least
//      TWO DISTINCT bodies (a genuinely shared joint) and records which two body indices meet
//      at each such joint.  Midpoints contributed by only one body are free ends and are
//      silently discarded.
//   3. Cycle detection and ring-breaking:
//        - Count each selected body's degree (how many shared joints it participates in).
//        - If every body has degree == 2, the topology is a pure closed ring.
//        - For an open chain (at least one body has degree < 2 or the count of shared joints
//          is less than the total body count), all shared joints are returned unchanged.
//        - For a pure cycle of N bodies: a graph traversal starting from body 0 (the first
//          body in the selection list) visits N-1 joints in chain order and intentionally
//          omits the final "closing" joint that would return to body 0.  This leaves two
//          open ends on the linearised strip so the downstream flat-layout can unfold it.
//          The traversal direction is determined by which joint connecting body 0 appears
//          first in sharedEdgeBodyPairs (body-index order, cap-face order within each body),
//          making the result deterministic for any given selection order.
//
// Midpoint comparison uses tolerantEquals(Vector, Vector) from vector.fs, which applies
// TOLERANCE.zeroLength for length vectors -- appropriate for edges that are truly coincident
// (same miter cut) but not for edges that are merely close.
//
// @param context        : Active context.
// @param outerEdgeData  : Array of map { "edgeQuery" : Query, "bodyIndex" : number,
//                         "frameBody" : Query, "tubeAxis" : Vector }, one entry per eligible
//                         cap face per body.
// @param totalBodyCount : Total number of selected frame bodies (size of frameBodiesArray).
// @returns array        : Array of data map (same schema as outerEdgeData entries), one per joint that
//                         should receive a bend constructor.
function collectSharedApexEdges(context is Context, outerEdgeData is array, totalBodyCount is number) returns array
{
    // Pre-compute all midpoints once to avoid redundant evEdgeTangentLine calls in later passes.
    var midpoints = [];
    for (var item in outerEdgeData)
    {
        midpoints = append(midpoints, evEdgeTangentLine(context, {
                        "edge" : item.edgeQuery,
                        "parameter" : 0.5
                    }).origin);
    }

    // Pass 1: build the joint graph.
    // For each unique world-space midpoint, track how many input edges share it and which
    // DISTINCT body indices contributed those edges.
    var uniqueMidpoints        = [];  // One entry per distinct world-space location.
    var uniqueCounts           = [];  // Total edge count at each unique midpoint.
    var uniqueBodyContributors = [];  // Array of body-index arrays, one set per unique midpoint.

    for (var edgeIndex = 0; edgeIndex < size(outerEdgeData); edgeIndex += 1)
    {
        const midpoint  = midpoints[edgeIndex];
        const bodyIndex = outerEdgeData[edgeIndex].bodyIndex;

        var foundIndex = -1;
        for (var uniqueMidpointIndex = 0; uniqueMidpointIndex < size(uniqueMidpoints); uniqueMidpointIndex += 1)
        {
            if (tolerantEquals(midpoint, uniqueMidpoints[uniqueMidpointIndex]))
            {
                foundIndex = uniqueMidpointIndex;
                break;
            }
        }

        if (foundIndex == -1)
        {
            // New unique midpoint: record it with this body as its first contributor.
            uniqueMidpoints        = append(uniqueMidpoints,        midpoint);
            uniqueCounts           = append(uniqueCounts,           1);
            uniqueBodyContributors = append(uniqueBodyContributors, [bodyIndex]);
        }
        else
        {
            // Existing midpoint: increment count and add this body index if not already present.
            uniqueCounts[foundIndex] = uniqueCounts[foundIndex] + 1;

            var bodyAlreadyPresent = false;
            for (var presentBodyIndex in uniqueBodyContributors[foundIndex])
            {
                if (presentBodyIndex == bodyIndex)
                {
                    bodyAlreadyPresent = true;
                    break;
                }
            }
            if (!bodyAlreadyPresent)
                uniqueBodyContributors[foundIndex] = append(uniqueBodyContributors[foundIndex], bodyIndex);
        }
    }

    // Pass 2: collect shared joints (midpoints contributed by two or more distinct bodies).
    // Also build sharedEdgeBodyPairs for cycle detection and traversal.
    // sharedJointData stores the FULL outerEdgeData map for each shared joint, preserving
    // frameBody, tubeAxis, and capFace for downstream dimension computation.
    var sharedJointData     = [];
    var sharedEdgeBodyPairs = [];  // Parallel to sharedJointData: [bodyA, bodyB] per joint.
    var claimedMidpoints    = [];

    for (var edgeIndex = 0; edgeIndex < size(outerEdgeData); edgeIndex += 1)
    {
        const midpoint = midpoints[edgeIndex];

        // Skip if already emitted for this midpoint.
        var alreadyClaimed = false;
        for (var claimedMidpoint in claimedMidpoints)
        {
            if (tolerantEquals(midpoint, claimedMidpoint))
            {
                alreadyClaimed = true;
                break;
            }
        }
        if (alreadyClaimed)
            continue;

        // Locate this midpoint in the unique list and emit if it is a genuine shared joint.
        for (var uniqueMidpointIndex = 0; uniqueMidpointIndex < size(uniqueMidpoints); uniqueMidpointIndex += 1)
        {
            if (tolerantEquals(midpoint, uniqueMidpoints[uniqueMidpointIndex]))
            {
                if (size(uniqueBodyContributors[uniqueMidpointIndex]) >= 2)
                {
                    sharedJointData     = append(sharedJointData,     outerEdgeData[edgeIndex]);
                    sharedEdgeBodyPairs = append(sharedEdgeBodyPairs, uniqueBodyContributors[uniqueMidpointIndex]);
                    claimedMidpoints    = append(claimedMidpoints,    midpoint);
                }
                break;
            }
        }
    }

    // Cycle detection: a pure closed ring exists when:
    //   a) The number of shared joints equals the number of selected bodies (N joints for N bodies).
    //   b) Every selected body has degree 2 in the joint graph (participates in exactly two
    //      shared joints).  A body with degree 0 has no neighbours in the selection (isolated),
    //      and a body with degree 1 is a chain endpoint.  Either prevents a pure cycle.
    var bodyDegrees = makeArray(totalBodyCount, 0);
    for (var pair in sharedEdgeBodyPairs)
    {
        for (var contributingBodyIndex in pair)
            bodyDegrees[contributingBodyIndex] = bodyDegrees[contributingBodyIndex] + 1;
    }

    var isPureCycle = (size(sharedJointData) == totalBodyCount);
    if (isPureCycle)
    {
        for (var degree in bodyDegrees)
        {
            if (degree != 2)
            {
                isPureCycle = false;
                break;
            }
        }
    }

    // Open chain: return all shared joints immediately.
    if (!isPureCycle)
        return sharedJointData;

    // Pure cycle: traverse N-1 joints starting from body 0, dropping the final closing joint.
    //
    // At each step we advance from currentBodyIndex to the neighbour connected by a joint that
    // has not yet been visited and does not backtrack to previousBodyIndex.  After N-1 steps
    // the chain covers all bodies in a single open strip; the Nth joint (which would reconnect
    // the last body to body 0) is intentionally omitted.
    var cycleOrderedJoints = [];
    var currentBodyIndex   = 0;
    var previousBodyIndex  = -1;

    for (var stepIndex = 0; stepIndex < totalBodyCount - 1; stepIndex += 1)
    {
        var nextBodyIndex      = -1;
        var selectedJointIndex = -1;

        for (var jointIndex = 0; jointIndex < size(sharedEdgeBodyPairs); jointIndex += 1)
        {
            const pair = sharedEdgeBodyPairs[jointIndex];

            // Only process simple 2-body joints.  Multi-way intersections (T-junctions,
            // 3-way corners) prevent isPureCycle from being true and cannot reach this path,
            // but skip them explicitly for defensive correctness.
            if (size(pair) < 2)
                continue;

            const pairBodyA = pair[0];
            const pairBodyB = pair[1];

            if (pairBodyA != currentBodyIndex && pairBodyB != currentBodyIndex)
                continue; // This joint does not touch the current body.

            // Identify the body on the other side of this joint.
            var neighbourBodyIndex = pairBodyA;
            if (pairBodyA == currentBodyIndex)
                neighbourBodyIndex = pairBodyB;

            if (neighbourBodyIndex == previousBodyIndex)
                continue; // Do not backtrack to where we came from.

            nextBodyIndex      = neighbourBodyIndex;
            selectedJointIndex = jointIndex;
            break;
        }

        if (selectedJointIndex == -1)
            throw regenError("Could not complete cycle traversal at step " ~ (stepIndex + 1) ~
                ". The selected frame bodies appear to form a closed ring but the joint " ~
                "connectivity is inconsistent. Ensure all selected bodies form a single " ~
                "unambiguous connected cycle.", ["frameBodies"]);

        cycleOrderedJoints = append(cycleOrderedJoints, sharedJointData[selectedJointIndex]);
        previousBodyIndex  = currentBodyIndex;
        currentBodyIndex   = nextBodyIndex;
    }

    return cycleOrderedJoints;
}

// Builds a coordinate system centered on the midpoint of the outer apex edge.
//
// Axis convention:
//   origin : world-space midpoint of the outer apex edge
//   X axis : outward normal of the adjacent cap (miter) face
//   Z axis : cross(capFaceNormal, tubeAxis) -- fold line direction in the cross-section plane
//   Y axis : implied by the right-hand rule (cross(Z, X))
//
// The fold line direction (Z) is derived from the cap face normal and the tube axis.
// This guarantees it is perpendicular to the tube axis (required by coordSystem's precondition
// and by the bounding-box evaluation in computeJointDimensions).  For box tubes with corner
// radii the outer apex edge may border a cylindrical arc face; using that face's normal
// instead of tubeAxis produces a fold line with a nonzero tube-axis component, which fails
// the perpendicularVectors precondition.
//
// @param context       : Active context.
// @param id            : Feature id, passed through for error reporting in sub-calls.
// @param instanceIndex : Global instance counter (for error messages and instance naming).
// @param apexEdge      : Query resolving to a single outer apex edge on a mitered frame body.
// @param tubeAxis      : Tube sweep direction (dimensionless unit vector).
// @returns CoordSystem aligned to the miter joint at the outer apex edge midpoint.
function buildApexCoordSystem(context is Context, id is Id, instanceIndex is number,
    apexEdge is Query, tubeAxis is Vector) returns CoordSystem
{
    const edgeMidpoint = evEdgeTangentLine(context, {
                "edge" : apexEdge,
                "parameter" : 0.5
            }).origin;

    const miterFace = findCapFaceAdjacentToApexEdge(context, id, instanceIndex, apexEdge);

    // X axis: outward normal of the miter (cap) face.
    const capFaceNormal = evFaceTangentPlane(context, {
                "face" : miterFace,
                "parameter" : vector(0.5, 0.5)
            }).normal;

    // Z axis: fold line direction.
    // cross(capFaceNormal, tubeAxis) is perpendicular to both inputs by definition,
    // satisfying coordSystem's perpendicularVectors(xAxis, zAxis) precondition.
    const foldLineDirection = normalize(cross(capFaceNormal, tubeAxis));

    return coordSystem(edgeMidpoint, capFaceNormal, foldLineDirection);
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

// Computes the three joint configuration variables stored in KirigamiBendAttribute.
//
// BoxTubeHeight
//   The cross-section extent of the frame body measured along the fold-line direction
//   (apexCoordSystem.zAxis = cross(capFaceNormal, tubeAxis)).  For a single-axis miter
//   this is the tube dimension parallel to the fold line.  Evaluated as the X extent of
//   a bounding-box frame whose X axis = foldLineDirection and Z axis = tubeAxis.
//
// BoxTubeWidth
//   The cross-section extent in the direction perpendicular to both tubeAxis and
//   foldLineDirection: cross(tubeAxis, foldLineDirection).  Evaluated as the Y extent of
//   the same bounding-box frame.
//
// MiterAngle
//   Angle between the cap face normal (apexCoordSystem.xAxis) and the tube sweep axis.
//   Clamped to [0, 90] degrees, matching the Onshape frame cut-list convention
//   (CUTLIST_ANGLE_1 / CUTLIST_ANGLE_2 in frameUtils.fs): 0 degrees for a perpendicular
//   cut (butt joint), 45 degrees for a 45-degree miter.
//
// @param context          : Active context.
// @param frameBody        : Query resolving to a single solid frame body.
// @param tubeAxis         : Tube sweep direction (dimensionless unit vector).
// @param apexCoordSystem  : Coordinate system at the outer apex edge, as built by
//                           buildApexCoordSystem.  xAxis = capFaceNormal, zAxis = foldLineDirection.
// @returns map with keys: "boxTubeHeight" (ValueWithUnits length),
//                         "boxTubeWidth"  (ValueWithUnits length),
//                         "miterAngle"    (ValueWithUnits angle).
function computeJointDimensions(context is Context, frameBody is Query, tubeAxis is Vector,
    apexCoordSystem is CoordSystem) returns map
{
    // Fold-line direction: apexCoordSystem.zAxis = cross(capFaceNormal, tubeAxis).
    // By construction this is always perpendicular to tubeAxis, so it is a valid
    // cross-section axis for the bounding-box evaluation below.
    const foldLineDirection = apexCoordSystem.zAxis;

    // Evaluation frame:
    //   X axis = foldLineDirection  (BoxTubeHeight direction)
    //   Z axis = tubeAxis           (sweep direction, discarded from the bounding-box result)
    //   Y axis = cross(tubeAxis, foldLineDirection)  (BoxTubeWidth direction)
    const crossSectionCS = coordSystem(WORLD_ORIGIN, foldLineDirection, tubeAxis);

    const crossSectionBoundingBox = evBox3d(context, {
                "topology" : frameBody,
                "cSys"     : crossSectionCS,
                "tight"    : true
            });

    // X extent (index 0): dimension along foldLineDirection = local joint csys Z = BoxTubeHeight.
    // Y extent (index 1): dimension along cross(tubeAxis, foldLineDirection)  = BoxTubeWidth.
    const boxTubeHeight = crossSectionBoundingBox.maxCorner[0] - crossSectionBoundingBox.minCorner[0];
    const boxTubeWidth  = crossSectionBoundingBox.maxCorner[1] - crossSectionBoundingBox.minCorner[1];

    // Miter angle: angle between the cap face normal and the tube sweep axis.
    // angleBetween returns a value in [0, 180]; clamp to [0, 90] and zero near-zero values
    // to match the Onshape frame cut-list convention.
    var miterAngle = angleBetween(apexCoordSystem.xAxis, tubeAxis);
    if (miterAngle > 90 * degree)
        miterAngle = 180 * degree - miterAngle;
    if (miterAngle < TOLERANCE.zeroAngle * degree)
        miterAngle = 0 * degree;

    return {
        "boxTubeHeight" : boxTubeHeight,
        "boxTubeWidth"  : boxTubeWidth,
        "miterAngle"    : miterAngle
    };
}

// Computes the perpendicular distance from the outer wall face to the nearest longitudinal
// sweep edge on a swept face whose outward normal is parallel to the fold-line direction
// (apexCoordSystem.zAxis).  These are the "top" and "bottom" faces in the local joint
// coordinate system.
//
// Geometry:
//   - The outer wall face is the flat swept face whose normal is parallel to the transverse
//     direction (perpendicular to both tubeAxis and foldLineDirection) and that lies furthest
//     outward in that direction.  For a corner-radius box tube the outer apex edge borders
//     a cylindrical arc face, not the flat outer wall face; the transverse-direction approach
//     finds the correct flat face regardless of corner topology.
//   - "Top/bottom faces" are swept faces where parallelVectors(faceNormal, foldLine) is true.
//   - "Longitudinal sweep edges" on those faces are edges whose tangent is parallel to tubeAxis.
//   - Outer-boundary edges of the top/bottom faces (shared with the outer wall face or with
//     arc faces adjacent to it) are excluded; the minimum distance over the remaining edges
//     is returned.
//
// For a thin-walled hollow box tube this equals approximately the wall thickness (within the
// inner corner radius).  For a solid bar it equals the full cross-section dimension in the
// transverse direction.
//
// Returns 0 * meter if no qualifying edges are found (degenerate profile).
//
// @param context          : Active context.
// @param frameBody        : Query resolving to the single solid frame body.
// @param tubeAxis         : Tube sweep direction (dimensionless unit vector).
// @param apexCoordSystem  : Coordinate system at the outer apex edge (xAxis = cap face normal,
//                           zAxis = fold-line direction).
// @returns ValueWithUnits (length) : Offset distance from outer wall to nearest interior sweep edge.
function computeOffsetToInteriorSweepLine(context is Context, frameBody is Query,
    tubeAxis is Vector, apexCoordSystem is CoordSystem) returns ValueWithUnits
{
    const foldLineDirection = apexCoordSystem.zAxis;

    // Transverse direction: perpendicular to both tubeAxis and foldLineDirection.
    // This is the direction of the outer wall face normal for a single-axis miter.
    const transverseDirection = normalize(cross(tubeAxis, foldLineDirection));

    const allSweptFacesQuery = qHasAttributeWithValueMatching(
                qOwnedByBody(frameBody, EntityType.FACE),
                FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                { "topologyType" : FrameTopologyType.SWEPT_FACE });

    // Identify the flat outer wall face: the swept face whose normal is parallel to
    // transverseDirection (allowing antiparallel) and whose plane origin is furthest
    // outward in the transverse direction.  qParallelPlanes only matches planar faces,
    // so cylindrical arc faces are automatically excluded.
    const transverseSweptFaces = evaluateQuery(context,
                qParallelPlanes(allSweptFacesQuery, transverseDirection));

    var outerWallFace  = undefined;
    var outerWallPlane = undefined;
    for (var transverseFace in transverseSweptFaces)
    {
        const facePlane = evFaceTangentPlane(context, {
                    "face"      : transverseFace,
                    "parameter" : vector(0.5, 0.5)
                });
        if (outerWallPlane == undefined ||
            dot(facePlane.origin, transverseDirection) > dot(outerWallPlane.origin, transverseDirection))
        {
            outerWallFace  = transverseFace;
            outerWallPlane = facePlane;
        }
    }

    if (outerWallFace == undefined)
        return 0 * meter; // No flat face in the transverse direction (degenerate profile).

    // "Top/bottom" swept faces: planar faces whose normal is parallel to foldLineDirection.
    // qParallelPlanes excludes arc faces automatically.
    const zParallelSweptFaces = qParallelPlanes(allSweptFacesQuery, foldLineDirection);

    // Build the exclusion set: edges of the outer wall face plus edges of any swept faces
    // adjacent to the outer wall face (the outer corner arcs for a corner-radius tube).
    // This ensures outer-boundary edges of the top/bottom faces are excluded whether or not
    // they are directly shared with the outer wall face.
    const arcFacesAdjacentToOuterWall = qIntersection([
                qAdjacent(outerWallFace, AdjacencyType.EDGE, EntityType.FACE),
                allSweptFacesQuery
            ]);
    const outerBoundaryEdges = qUnion([
                qAdjacent(outerWallFace, AdjacencyType.EDGE, EntityType.EDGE),
                qAdjacent(arcFacesAdjacentToOuterWall, AdjacencyType.EDGE, EntityType.EDGE)
            ]);
    const interiorCandidateEdges = evaluateQuery(context, qSubtraction(
                qAdjacent(zParallelSweptFaces, AdjacencyType.EDGE, EntityType.EDGE),
                outerBoundaryEdges));

    var minimumPositiveDistance = undefined;

    for (var candidateEdge in interiorCandidateEdges)
    {
        // Only longitudinal sweep edges (tangent parallel to tubeAxis) represent the interior
        // sweep lines we care about; cross-sectional and miter-cut edges are discarded here.
        const edgeTangentLine = evEdgeTangentLine(context, {
                    "edge"      : candidateEdge,
                    "parameter" : 0.5
                });

        if (!parallelVectors(edgeTangentLine.direction, tubeAxis))
            continue;

        // Perpendicular distance from the outer wall face plane to this edge midpoint.
        const signedDistance = dot(edgeTangentLine.origin - outerWallPlane.origin,
                outerWallPlane.normal);
        const distance = abs(signedDistance);

        if (minimumPositiveDistance == undefined || distance < minimumPositiveDistance)
            minimumPositiveDistance = distance;
    }

    if (minimumPositiveDistance == undefined)
        return 0 * meter;

    return minimumPositiveDistance;
}
