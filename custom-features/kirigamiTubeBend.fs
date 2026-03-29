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
                        frameBody, capFace);
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

        for (var instanceIndex = 0; instanceIndex < size(sharedJoints); instanceIndex += 1)
        {
            const jointData = sharedJoints[instanceIndex];
            const apexEdge = jointData.edgeQuery;
            const apexCoordSystem = buildApexCoordSystem(context, id, instanceIndex, apexEdge);
            // computeJointDimensions uses apexCoordSystem.zAxis (the fold-line direction, which IS the
            // local-csys Z) as the BoxTubeHeight axis, and apexCoordSystem.xAxis as the cap face normal
            // for the miter angle.  No world-axis references are used.
            const jointDimensions = computeJointDimensions(context, jointData.frameBody,
                    jointData.tubeAxis, apexCoordSystem);

            // Distance from the outer wall face to the nearest longitudinal sweep edge on a
            // top/bottom swept face (normal parallel to local Z).  For a hollow tube this equals
            // the wall thickness measured perpendicular to both the tube axis and the fold line.
            const offsetToInteriorSweepLine = computeOffsetToInteriorSweepLine(context,
                    jointData.frameBody, jointData.tubeAxis, apexCoordSystem, apexEdge);

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
    // Referred to as the "transverse component" because it lies in the cross-section plane.
    // This is a standard vector projection: v_perp = v - (v · axis) * axis.
    // No standard library function exists for this specific plane-perpendicular projection;
    // the inline form is used after confirming that ev*/q* functions do not cover this case.
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
// A local coordinate system is built from the first candidate edge's tangent (X axis) and
// the adjacent swept face's outward normal (Z axis).  Candidate edge midpoints are projected
// onto the local Y axis and both the minimum-Y and maximum-Y edges are tracked.  The outer
// apex is at one of the two Y extremes; which one depends on whether the local Y axis points
// into or away from the tube body.  That direction is resolved cheaply by projecting the
// world-space centre of the first adjacent swept face (obtained for free from the same
// evFaceTangentPlane call used to establish Z) onto Y: a negative projection means the tube
// body extends in -Y and the outer apex is at MAX local Y; a positive projection means the
// tube body extends in +Y and the outer apex is at MIN local Y.
//
// Replaces the original evBox3d(tight)+qCoincidesWithPlane approach, which forced evaluation
// of the entire body geometry to obtain a bounding box used only for its Y extents.  The new
// approach spends only N-1 evEdgeTangentLine calls for the midpoint loop (edge 0 is free)
// and one evFaceTangentPlane call that was already required, making it substantially cheaper.
//
// Works correctly for both the idealised 4-face box tube and real box tube sections that
// have inner and outer corner radii (typically 16 or more swept faces total), because the
// swept-face centre is always far from the outer-apex plane regardless of face count.
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
    // For an idealised 4-faced box tube this yields 4 perimeter edges; for a real box
    // tube with inner and outer corner radii there can be 12–16 or more swept faces
    // and a correspondingly larger candidate set.  The Y-projection logic below handles
    // any number of candidates correctly.
    const candidateEdges = qIntersection([
                qAdjacent(capFace,                   AdjacencyType.EDGE, EntityType.EDGE),
                qAdjacent(sweptFacesAdjacentToCapFace, AdjacencyType.EDGE, EntityType.EDGE)
            ]);

    if (isQueryEmpty(context, candidateEdges))
        throw regenError("No apex edges found on a cap face of frame body " ~ (bodyIndex + 1) ~ ". " ~
            "Ensure the body has a mitered end face adjacent to its tube wall faces.",
            ["frameBodies"]);

    // Build a local coordinate system for Y-axis projection (matching Frame Unroll's frame):
    //   X axis = tangent of the first candidate edge at its midpoint
    //   Z axis = outward normal of the first adjacent swept face
    //   Y axis = cross(Z, X)  [implicit in CoordSystem]
    // The Y axis points across the miter face toward the outer/inner extents of the tube.
    // NOTE: this is a helper frame solely for outer-edge detection.  The placement coord
    // system built by buildApexCoordSystem uses a different axis convention (cap face normal
    // = X, edge tangent = Z) that orients the constructor geometry correctly at the joint.
    const firstEdgeLine = evEdgeTangentLine(context, {
                "edge" : qNthElement(candidateEdges, 0),
                "parameter" : 0.5
            });

    // Save both the normal and the world-space centre point of the first adjacent swept face.
    // The centre is used below to determine which Y extreme is the outer apex without any
    // additional ev calls.  That face spans the full tube length in the Y direction, so its
    // centre is always far (in Y) from the outer-apex plane even on real-world box tubes with
    // corner radii (which typically have 16 swept faces rather than the idealised 4).
    const sweptFacePlane  = evFaceTangentPlane(context, {
                "face" : qNthElement(sweptFacesAdjacentToCapFace, 0),
                "parameter" : vector(0.5, 0.5)
            });
    const sweptFaceNormal = sweptFacePlane.normal;

    const localCoordSystem = coordSystem(firstEdgeLine.origin, firstEdgeLine.direction, sweptFaceNormal);

    // Project each candidate edge midpoint onto the local Y axis, tracking both the
    // minimum-Y and maximum-Y edges.  The outer apex edge sits at one of these two extremes;
    // the inner apex (and diagonal miter edges) sit between them.
    //
    // Edge 0's midpoint is already available from firstEdgeLine -- no extra ev call needed.
    const candidateEdgesArray = evaluateQuery(context, candidateEdges);
    const yDirection          = yAxis(localCoordSystem);
    const localOrigin         = localCoordSystem.origin;

    var minimumYProjection = dot(firstEdgeLine.origin - localOrigin, yDirection);
    var maximumYProjection = minimumYProjection;
    var minimumYEdge       = candidateEdgesArray[0];
    var maximumYEdge       = candidateEdgesArray[0];

    for (var edgeIndex = 1; edgeIndex < size(candidateEdgesArray); edgeIndex += 1)
    {
        const edgeMidpoint = evEdgeTangentLine(context, {
                    "edge"      : candidateEdgesArray[edgeIndex],
                    "parameter" : 0.5
                }).origin;
        const yProjection = dot(edgeMidpoint - localOrigin, yDirection);

        if (yProjection < minimumYProjection)
        {
            minimumYProjection = yProjection;
            minimumYEdge       = candidateEdgesArray[edgeIndex];
        }
        if (yProjection > maximumYProjection)
        {
            maximumYProjection = yProjection;
            maximumYEdge       = candidateEdgesArray[edgeIndex];
        }
    }

    // Determine which Y extreme holds the outer apex edge by projecting the swept-face
    // centre onto the local Y axis.
    //
    // The outer wall face (first adjacent swept face) spans the full tube length in Y.
    // Its world-space centre therefore lies well inside the tube body, far from the
    // outer-apex plane in local Y.  Depending on whether the local Y axis points toward
    // or away from the tube interior from the cap face:
    //
    //   outerWallFaceCenterY < 0  →  local Y points away from the tube body
    //                                 tube body extends in -Y; outer apex is at MAX local Y
    //   outerWallFaceCenterY > 0  →  local Y points into the tube body
    //                                 tube body extends in +Y; outer apex is at MIN local Y
    //
    // This replaces the previous unconditional "return minimumYEdge" which was only correct
    // when the local Y axis happened to point into the tube (about half of all orientations).
    // The incorrect sign caused the inner apex edge to be returned, making
    // collectSharedApexEdges find no matching midpoint and produce no geometry.
    const outerWallFaceCenterY = dot(sweptFacePlane.origin - localOrigin, yDirection);
    return (outerWallFaceCenterY < 0 * meter) ? maximumYEdge : minimumYEdge;
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

// Computes the three joint configuration variables stored in KirigamiBendAttribute.
//
// All geometry is derived from the apex coordinate system built by buildApexCoordSystem,
// which is based exclusively on outward face normals.  No world-axis references are used.
//
// BoxTubeHeight
//   The cross-section extent of the frame body measured along the Z axis of the local
//   joint coordinate system.  apexCoordSystem.zAxis is the fold-line direction:
//   cross(capFaceNormal, outerWallNormal).  For a simple single-axis miter this direction
//   is always perpendicular to the tube sweep axis, making it a clean cross-section axis.
//   Evaluated as the X extent of a bounding-box frame whose X axis = foldLineDirection
//   and Z axis = tubeAxis.
//
// BoxTubeWidth
//   The cross-section extent in the direction perpendicular to both the tube sweep axis
//   and the fold-line direction: cross(tubeAxis, foldLineDirection).  Evaluated as the
//   Y extent of the same bounding-box frame.
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
    // --- Cross-section bounding box ---
    // The fold-line direction is apexCoordSystem.zAxis: cross(capFaceNormal, outerWallNormal).
    // For a simple single-axis miter this is always perpendicular to tubeAxis, so it is a
    // valid axis for measuring the tube cross-section without mixing in the sweep direction.
    const foldLineDirection = apexCoordSystem.zAxis;

    // Build an evaluation frame where:
    //   X axis = foldLineDirection  (local joint csys Z, the BoxTubeHeight direction)
    //   Z axis = tubeAxis           (sweep direction, gives the tube length extent -- discarded)
    //   Y axis = cross(Z, X) = cross(tubeAxis, foldLineDirection)  (BoxTubeWidth direction)
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

    // --- Miter angle ---
    // apexCoordSystem.xAxis is the outward cap face normal (set by buildApexCoordSystem).
    // angleBetween returns the angle between the two vectors, always in [0, 180].
    // Clamping to [0, 90] and zeroing near-zero angles matches the Onshape frame cut-list
    // convention used by getDimensionsForStraightBeam in cutlistMath.fs.
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
// sweep edge that borders a swept face whose outward normal is parallel to the fold-line
// direction (apexCoordSystem.zAxis).  These are the "top" and "bottom" faces in the local
// joint coordinate system.
//
// Geometry:
//   - The outer wall face is the SWEPT_FACE adjacent to the outer apex edge.
//   - "Top/bottom faces" are swept faces where parallelVectors(faceNormal, foldLine) is true.
//   - "Longitudinal sweep edges" on those faces are edges whose tangent is parallel to tubeAxis.
//   - Edges already shared with the outer wall face are at distance zero (outer corner edges)
//     and are excluded; the minimum positive distance over the remaining edges is returned.
//
// For a thin-walled hollow box tube this equals the wall thickness measured in the direction
// perpendicular to both the tube sweep axis and the fold-line direction.  For a solid bar it
// equals the full cross-section dimension in that direction.
//
// A fallback of 0 * meter is returned if no qualifying edges are found (degenerate geometry).
//
// Performance:
//   qParallelPlanes replaces iterating all swept faces + evFaceTangentPlane per face.
//   qParallelPlanes only matches PLANAR faces; for real box tubes with corner radii (typically
//   16 swept faces) the cylindrical arc faces are excluded automatically -- this is correct
//   because the original parallelVectors check on evFaceTangentPlane normals at (0.5, 0.5)
//   also excluded them (arc-face normals at their UV centre are diagonal, not fold-aligned).
//   qSubtraction replaces one qIntersection query evaluation per edge.
//   A single evaluateQuery call replaces one call per Z-parallel face.
//
// @param context          : Active context.
// @param frameBody        : Query resolving to the single solid frame body.
// @param tubeAxis         : Tube sweep direction (dimensionless unit vector).
// @param apexCoordSystem  : Coordinate system at the outer apex edge (xAxis = cap face normal,
//                           zAxis = fold-line direction).
// @param apexEdge         : Query resolving to the outer apex edge on the frame body.
// @returns ValueWithUnits (length) : Offset distance from outer wall to nearest interior sweep edge.
function computeOffsetToInteriorSweepLine(context is Context, frameBody is Query,
    tubeAxis is Vector, apexCoordSystem is CoordSystem, apexEdge is Query) returns ValueWithUnits
{
    // Outer wall face: the swept face adjacent to the apex edge.
    const outerWallFace = qNthElement(qHasAttributeWithValueMatching(
                qAdjacent(apexEdge, AdjacencyType.EDGE, EntityType.FACE),
                FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                { "topologyType" : FrameTopologyType.SWEPT_FACE }), 0);

    // Pre-compute the outer wall face plane for distance measurement.
    const outerWallPlane = evFaceTangentPlane(context, {
                "face" : outerWallFace,
                "parameter" : vector(0.5, 0.5)
            });

    const foldLineDirection = apexCoordSystem.zAxis;

    // qParallelPlanes finds all planar swept faces whose normal is parallel to the
    // fold-line direction without iterating faces or calling evFaceTangentPlane per face.
    const allSweptFacesQuery = qHasAttributeWithValueMatching(
                qOwnedByBody(frameBody, EntityType.FACE),
                FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                { "topologyType" : FrameTopologyType.SWEPT_FACE });

    const zParallelSweptFaces = qParallelPlanes(allSweptFacesQuery, foldLineDirection);

    // Collect all edges of the Z-parallel faces in one batch query, then subtract the
    // outer wall face edges in a single qSubtraction operation.  This eliminates the
    // previous per-face evaluateQuery + per-edge qIntersection pattern.
    const outerWallFaceEdges     = qAdjacent(outerWallFace, AdjacencyType.EDGE, EntityType.EDGE);
    const interiorCandidateEdges = evaluateQuery(context, qSubtraction(
                qAdjacent(zParallelSweptFaces, AdjacencyType.EDGE, EntityType.EDGE),
                outerWallFaceEdges));

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
        // The edge is parallel to tubeAxis which is perpendicular to the outer wall normal,
        // so the distance is constant along the entire edge.
        const signedDistance = dot(edgeTangentLine.origin - outerWallPlane.origin,
                outerWallPlane.normal);
        const distance = abs(signedDistance);

        if (minimumPositiveDistance == undefined || distance < minimumPositiveDistance)
            minimumPositiveDistance = distance;
    }

    // Fallback: return zero if no qualifying edge was found (e.g., degenerate profile).
    if (minimumPositiveDistance == undefined)
        return 0 * meter;

    return minimumPositiveDistance;
}
