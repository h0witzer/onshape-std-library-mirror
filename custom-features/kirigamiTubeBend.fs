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
KirigamiBendConstructor::import(path : "1173cc57cdf5a7d688426b78", version : "e932c34342b55c79ca83a38e");

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
 *                                               longitudinal sweep edge on an interior (cavity-surface) planar
 *                                               swept face whose normal is aligned with the fold-line direction.
 *                                               Interior faces are identified by their outward normal pointing
 *                                               toward the body centroid.  Exterior faces and fillet (non-planar)
 *                                               faces are excluded.  For a hollow box tube without corner fillets
 *                                               this equals the wall thickness.  For a tube with interior corner
 *                                               fillets the value includes the fillet radius.
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

        // The offset to the interior sweep line depends only on the frame profile dimensions
        // (wall thickness).  All selected frame bodies are assumed to share the same profile,
        // as required for a valid kirigami strip (mismatched profiles would produce incorrect
        // geometry regardless).  Compute the offset once from the first joint and reuse the
        // cached value for all subsequent joints to avoid redundant geometry evaluations.
        var cachedOffsetToInteriorSweepLine = undefined;

        for (var instanceIndex = 0; instanceIndex < size(sharedJoints); instanceIndex += 1)
        {
            const jointData = sharedJoints[instanceIndex];
            const apexEdge = jointData.edgeQuery;

            // Build a query union of both frame bodies sharing this joint.  This is used later
            // as the target set for the boolean subtraction that cuts the kirigami notch.
            // jointData.jointBodyIndices is an array of body index values; for...in yields
            // each value directly (not a positional index into frameBodiesArray).
            var jointFrameBodies = [];
            for (var frameBodyIndex in jointData.jointBodyIndices)
                jointFrameBodies = append(jointFrameBodies, frameBodiesArray[frameBodyIndex]);
            const jointFrameBodyTargets = qUnion(jointFrameBodies);
            const apexCoordSystem = buildApexCoordSystem(context, id, instanceIndex, apexEdge);
            // computeJointDimensions uses apexCoordSystem.zAxis (the fold-line direction, which IS the
            // local-csys Z) as the BoxTubeHeight axis, and apexCoordSystem.xAxis as the cap face normal
            // for the miter angle.  No world-axis references are used.
            const jointDimensions = computeJointDimensions(context, jointData.frameBody,
                    jointData.tubeAxis, apexCoordSystem);

            // Distance from the outer wall face to the nearest longitudinal sweep edge on a
            // top/bottom swept face (normal parallel to local Z).  For a hollow tube this equals
            // the wall thickness measured perpendicular to both the tube axis and the fold line.
            // Computed only for the first joint; all subsequent joints reuse the cached value.
            if (cachedOffsetToInteriorSweepLine == undefined)
                cachedOffsetToInteriorSweepLine = computeOffsetToInteriorSweepLine(context,
                        jointData.frameBody, jointData.tubeAxis, apexCoordSystem, apexEdge);

            // toWorld(apexCoordSystem) is the Transform that carries geometry from the
            // constructor's local origin to the correct world-space position and orientation.
            const instanceQuery = addInstance(instantiator, KirigamiBendConstructor::build, {
                        "configuration" : {
                            "boxTubeHeight"            : jointDimensions.boxTubeHeight,
                            "boxTubeWidth"             : jointDimensions.boxTubeWidth,
                            "miterAngle"               : jointDimensions.miterAngle,
                            "bendOutsideRadius"        : definition.bendOutsideRadius,
                            "offsetToInteriorSweepLine": cachedOffsetToInteriorSweepLine
                        },
                        "transform" : toWorld(apexCoordSystem),
                        "name" : "bend" ~ instanceIndex
                    });

            pendingInstances = append(pendingInstances, {
                        "query"                    : instanceQuery,
                        "coordSystem"              : apexCoordSystem,
                        "instanceIndex"            : instanceIndex,
                        "jointBodyIndices"         : jointData.jointBodyIndices,
                        "boxTubeHeight"            : jointDimensions.boxTubeHeight,
                        "boxTubeWidth"             : jointDimensions.boxTubeWidth,
                        "miterAngle"               : jointDimensions.miterAngle,
                        "bendOutsideRadius"        : definition.bendOutsideRadius,
                        "offsetToInteriorSweepLine": cachedOffsetToInteriorSweepLine,
                        "frameBodyTargets"         : jointFrameBodyTargets
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

        // Boolean subtract each bend constructor tool from both of its adjacent frame bodies.
        // keepTools is true so the tool bodies -- which carry KirigamiBendAttribute for the
        // downstream flat-layout script -- are preserved after the cut operation.
        // After each subtraction, a mate connector is placed at the centroid of every planar
        // face introduced by that cut, oriented with its Z axis along the face outward normal.
        // Each cut face is then swept along the tool arc edge to produce a bent tube section body
        // that fills the void zone.  All bent tube section bodies are collected across every joint
        // and composited together with the input frame bodies in one closed composite at the end.
        var allBentTubeSectionBodies = [];

        for (var instanceIndex = 0; instanceIndex < size(pendingInstances); instanceIndex += 1)
        {
            const instance = pendingInstances[instanceIndex];
            const booleanCutId = id + ("booleanCut" ~ instanceIndex);

            // Subtract the bend constructor from both frame bodies sharing this joint.
            // The instantiator may bring in non-solid bodies (e.g. mate connectors from the
            // KirigamiBendConstructor part studio).  opBoolean requires all tool bodies to be
            // solid, so filter instance.query to BodyType.SOLID before passing it as tools.
            opBoolean(context, booleanCutId, {
                        "tools"         : qBodyType(instance.query, BodyType.SOLID),
                        "targets"       : instance.frameBodyTargets,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "keepTools"     : true
                    });

            // Query the planar faces introduced on the target bodies by this boolean cut.
            // qCreatedBy scopes the result to faces that did not exist before this opBoolean.
            const cutPlanarFacesArray = evaluateQuery(context,
                    qGeometry(qCreatedBy(booleanCutId, EntityType.FACE), GeometryType.PLANE));

            // Place one mate connector at the centroid of each planar cut face.
            // Z axis = face outward normal; X axis = an arbitrary perpendicular chosen
            // consistently for the same normal direction by perpendicularVector.
            // evApproximateCentroid is used rather than faceTangentPlane.origin: the
            // tangent plane origin corresponds to the UV parameter (0.5, 0.5) evaluation
            // point, which is the UV center of the face and does not necessarily coincide
            // with the geometric centroid for non-rectangular cut faces.
            for (var cutFaceIndex = 0; cutFaceIndex < size(cutPlanarFacesArray); cutFaceIndex += 1)
            {
                const cutFace = cutPlanarFacesArray[cutFaceIndex];
                const faceTangentPlane = evFaceTangentPlane(context, {
                            "face" : cutFace,
                            "parameter" : vector(0.5, 0.5)
                        });
                const faceCentroid = evApproximateCentroid(context, { "entities" : cutFace });

                const mateConnectorCS = coordSystem(faceCentroid,
                        perpendicularVector(faceTangentPlane.normal),
                        faceTangentPlane.normal);

                opMateConnector(context,
                        id + ("bendCutFaceMateConnector" ~ instanceIndex ~ "_" ~ cutFaceIndex), {
                            "coordSystem" : mateConnectorCS,
                            "owner"       : qOwnerBody(cutFace)
                        });
            }

            // Sweep cut faces along the tool's arc edge to fill the void zone removed by the
            // boolean subtraction, reconstructing the tube wall geometry in the bent state.
            //
            // Only cut faces owned by the first frame body at this joint are used as sweep
            // profiles.  Both frame bodies receive cut faces from the boolean subtraction, but
            // sweeping from both would produce identical overlapping fill geometry.  Assigning
            // the fill to jointBodyIndices[0] gives each joint a single unambiguous owner.
            //
            // The KirigamiBendConstructor solid carries two arc edges (inner and outer bend
            // radius).  Both span the same angular sweep from frame body A's cut plane to
            // frame body B's cut plane, so either is a valid path for the fill sweep.
            const solidToolBody = qBodyType(instance.query, BodyType.SOLID);
            const toolArcEdges = qGeometry(
                    qOwnedByBody(solidToolBody, EntityType.EDGE),
                    GeometryType.ARC);

            if (!isQueryEmpty(context, toolArcEdges))
            {
                // Pick the first arc edge as the sweep path.
                const sweepPathEdge = qNthElement(toolArcEdges, 0);

                // Filter cut faces to those owned by the primary (first) frame body at this joint.
                const primaryFrameBody = frameBodiesArray[instance.jointBodyIndices[0]];
                const primaryFrameBodyCutFaces = evaluateQuery(context,
                        qIntersection([
                            qOwnedByBody(primaryFrameBody, EntityType.FACE),
                            qUnion(cutPlanarFacesArray)
                        ]));

                // Sweep each cut face along the arc, creating one bent tube section body per face.
                var bentTubeSectionBodies = [];
                for (var tubeCutFaceIndex = 0; tubeCutFaceIndex < size(primaryFrameBodyCutFaces); tubeCutFaceIndex += 1)
                {
                    const bentTubeSectionId = id + ("bentTubeSection" ~ instanceIndex ~ "_" ~ tubeCutFaceIndex);
                    opSweep(context, bentTubeSectionId, {
                                "profiles" : primaryFrameBodyCutFaces[tubeCutFaceIndex],
                                "path"     : sweepPathEdge
                            });
                    bentTubeSectionBodies = append(bentTubeSectionBodies,
                            qCreatedBy(bentTubeSectionId, EntityType.BODY));
                }

                // Accumulate this joint's bent tube section bodies for the final composite step.
                allBentTubeSectionBodies = concatenateArrays([allBentTubeSectionBodies, bentTubeSectionBodies]);
            }
        }

        // Composite all bent tube section bodies together with the input frame bodies into a
        // single closed composite part.  A closed composite consumes its constituent solid
        // bodies so that they are presented as one unit to the downstream flat-layout script.
        // This is done once after all joints are processed so every swept tube section and every
        // frame segment are grouped in a single composite regardless of how many joints exist.
        if (size(allBentTubeSectionBodies) > 0)
        {
            opCreateCompositePart(context, id + "bentTubeFrameComposite", {
                        "bodies" : qUnion(concatenateArrays([allBentTubeSectionBodies, [definition.frameBodies]])),
                        "closed" : true
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
    // Transverse component of the cap face normal: its projection onto the cross-section plane
    // (perpendicular to tubeAxis).  For a simple single-axis miter this vector is parallel to
    // exactly one swept wall face normal.  Pre-condition: capFaceNormal is not parallel to
    // tubeAxis (ensured by the 0-degree filter at the call site).
    const capFaceNormalTransverseComponent = projectFoldLineOntoCrossSection(capFaceNormal, tubeAxis);

    for (var faceIndex = 0; faceIndex < size(sweptFacesArray); faceIndex += 1)
    {
        const sweptFaceNormal = evFaceTangentPlane(context, {
                    "face" : sweptFacesArray[faceIndex],
                    // Evaluate at the face center to obtain a representative outward normal
                    // for each planar swept face.
                    "parameter" : vector(0.5, 0.5)
                }).normal;

        if (parallelVectors(capFaceNormalTransverseComponent, sweptFaceNormal))
            return true;
    }

    return false;
}

// Finds the single outer apex edge on one cap face of a frame body.
//
// The outer apex edge is the cap face perimeter edge at the body's extreme extent along the
// tube sweep direction -- i.e., the edge on the outside (convex) face of the bend that
// reaches farthest from the joint centre.
//
// Approach (directly follows frameUnroll.fs getBodyTransform + getFacesTouchingPlane):
//   1. Evaluate the miter plane of the cap face.
//   2. Collect the tube wall faces that touch the miter plane using getFacesTouchingPlane-style
//      filtering: (a) adjacent to capFace, (b) planar, (c) NOT normal to the miter plane
//      (removes top/bottom faces), (d) intersects the miter plane.  For a hollow box tube
//      this naturally excludes inner wall faces (which do not intersect the miter plane) and
//      top/bottom faces (whose normals are parallel to the miter plane normal).
//   3. Build a local coordinate system from the filtered face set: xAxis = direction of one
//      fold edge in the miter plane, zAxis = outward normal of one adjacent filtered wall face.
//   4. Evaluate the body bounding box in that coordinate system.
//   5. For each extreme corner (minCorner, maxCorner) create an end plane at that corner with
//      normal = yAxis.  The outer apex edge lies simultaneously in the end plane and the miter
//      plane.  Try both corners; the first non-empty result is the outer apex edge.
//
// @param context          : Active context.
// @param id               : Feature id used for error reporting.
// @param bodyIndex        : Zero-based index of this body in the selection (for error messages).
// @param frameBody        : Query resolving to a single solid frame body.
// @param capFace          : Query resolving to one cap face on frameBody.
// @returns Query          : The single outer apex edge on this cap face.
function findOuterApexEdgeForCapFace(context is Context, id is Id, bodyIndex is number,
    frameBody is Query, capFace is Query) returns Query
{
    // Miter plane of the cap face.
    const capFacePlane = evPlane(context, { "face" : capFace });

    // Collect the tube wall faces that touch the miter plane using the same three-step filter
    // as Neil's getFacesTouchingPlane in frameUnroll.fs:
    //   1. Get planar faces adjacent to the cap face via shared edges.
    //   2. Remove faces whose normals are parallel to the miter plane normal (top/bottom faces).
    //   3. Keep only faces that geometrically intersect the miter plane.
    // For a hollow box tube this naturally excludes inner wall faces (they don't intersect
    // the miter plane) and top/bottom faces (their normals are parallel to the cap face normal).
    const sideFaces = qAdjacent(capFace, AdjacencyType.EDGE, EntityType.FACE)->qGeometry(GeometryType.PLANE);
    const angledFaces = sideFaces->qSubtraction(qPlanesParallelToDirection(sideFaces, capFacePlane.normal));
    const facesTouchingMiterPlane = qIntersectsPlane(angledFaces, capFacePlane);

    if (isQueryEmpty(context, facesTouchingMiterPlane))
        throw regenError("Frame body " ~ (bodyIndex + 1) ~ " cap face has no adjacent tube wall faces " ~
            "touching the miter plane. Ensure the selected body is an Onshape frame member " ~
            "created with the Frame feature.", ["frameBodies"]);

    // Local coordinate system (identical to frameUnroll.fs getBodyTransform):
    //   xAxis = direction of one fold edge in the miter plane (edge on the filtered face set
    //           that coincides with the miter plane)
    //   zAxis = outward normal of one adjacent filtered wall face
    //   yAxis = cross(zAxis, xAxis) -- approximately the tube sweep direction
    const edgesInMiterPlane = qCoincidesWithPlane(
            qAdjacent(facesTouchingMiterPlane, AdjacencyType.EDGE, EntityType.EDGE),
            capFacePlane);

    if (isQueryEmpty(context, edgesInMiterPlane))
        throw regenError("Frame body " ~ (bodyIndex + 1) ~ ": no edges found in the miter plane on the " ~
            "adjacent tube wall faces. Ensure the selected body is an Onshape frame member " ~
            "created with the Frame feature.", ["frameBodies"]);

    const foldEdgeLine = evEdgeTangentLine(context, {
                "edge" : edgesInMiterPlane,
                "parameter" : 0.5
            });
    const wallFaceNormal = evFaceTangentPlane(context, {
                "face" : qNthElement(facesTouchingMiterPlane, 0),
                "parameter" : vector(0.5, 0.5)
            }).normal;

    const localCoordSystem = coordSystem(foldEdgeLine.origin, foldEdgeLine.direction, wallFaceNormal);

    // Bounding box of the full body in the local coordinate system.
    const bodyBoundingBox = evBox3d(context, {
                "topology" : frameBody,
                "cSys"     : localCoordSystem,
                "tight"    : true
            });

    // Find the outer apex edge: the edge on the filtered face set that lies simultaneously in
    // the end plane (at the body's extreme extent along yAxis) and in the miter plane.
    // This is precisely Neil's dual-plane intersection from frameUnroll.fs getBodyTransform.
    var outerEdge = undefined;
    for (var corner in [bodyBoundingBox.minCorner, bodyBoundingBox.maxCorner])
    {
        const endPlane = plane(toWorld(localCoordSystem, corner), yAxis(localCoordSystem));

        outerEdge = qCoincidesWithPlane(
                qAdjacent(facesTouchingMiterPlane, AdjacencyType.EDGE, EntityType.EDGE), endPlane)->
            qIntersection(qCoincidesWithPlane(
                qAdjacent(facesTouchingMiterPlane, AdjacencyType.EDGE, EntityType.EDGE), capFacePlane));

        if (!isQueryEmpty(context, outerEdge))
            break;
    }

    if (outerEdge == undefined || isQueryEmpty(context, outerEdge))
        throw regenError("Could not determine the outer apex edge on a cap face of frame body " ~
            (bodyIndex + 1) ~ ". Ensure the body is a properly formed Onshape frame member.",
            ["frameBodies"]);

    return qNthElement(outerEdge, 0);
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
// @returns array        : Array of data map (same schema as outerEdgeData entries, plus
//                         "jointBodyIndices" : array of two body indices for the two bodies
//                         that share each joint), one entry per joint that should receive
//                         a bend constructor.
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
                    // Embed the pair of body indices directly in the joint data map so that
                    // the caller can identify both frame bodies at this joint without needing
                    // a separate parallel array.
                    var jointEntry = outerEdgeData[edgeIndex];
                    jointEntry["jointBodyIndices"] = uniqueBodyContributors[uniqueMidpointIndex];
                    sharedJointData     = append(sharedJointData,     jointEntry);
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

// Projects a vector onto the plane perpendicular to a given axis, then normalizes the result.
//
// Used to strip any tubeAxis component that builds up in foldLineDirection when a frame body
// is created without a reference face.  Without this, the fold-line direction would differ from
// the exactly-perpendicular swept face normals by a tiny floating-point angle, causing
// parallelVectors checks to fail.  For ideal geometry this is a no-op.
//
// Manual vector rejection (v - dot(v, axis) * axis) is used because no standard library
// ev* or q* function exists for projecting a direction vector onto a plane perpendicular to
// an arbitrary axis.  The operation is a single subtraction and scalar multiply; no
// coordinate transformation function applies to a free direction vector without a body context.
//
// Pre-condition: the input vector must not be parallel to axis (the projection would be a
// zero vector and normalize() undefined).  The caller must guard against this case.
//
// @param vector    : Vector to project (does not need to be a unit vector).
// @param axis      : Axis to project out of vector (dimensionless unit vector).
// @returns Vector (unit) : vector with its axis component removed and re-normalized.
function projectFoldLineOntoCrossSection(vector is Vector, axis is Vector) returns Vector
{
    return normalize(vector - dot(vector, axis) * axis);
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
    // When the Frame feature has no reference face assigned, the outer wall face normal used
    // to build apexCoordSystem may carry a small floating-point component along tubeAxis.
    // projectFoldLineOntoCrossSection strips that component and normalizes the result,
    // guaranteeing orthogonality with tubeAxis.  For ideal geometry this is a no-op.
    const foldLineDirection = apexCoordSystem.zAxis;

    // Guard: if foldLineDirection is parallel to tubeAxis the cross-section projection
    // would produce a zero vector (normalize() undefined).  Checked here in the caller
    // rather than inside projectFoldLineOntoCrossSection so the error message can report
    // the specific feature context; the function's pre-condition documents this expectation.
    if (parallelVectors(foldLineDirection, tubeAxis))
        throw regenError("Cannot compute joint cross-section dimensions: the fold-line " ~
            "direction is parallel to the tube sweep axis. Verify that all selected bodies " ~
            "are Onshape frame members with valid single-axis miter joints.", ["frameBodies"]);

    const foldLineInCrossSection = projectFoldLineOntoCrossSection(foldLineDirection, tubeAxis);

    // Build an evaluation frame where:
    //   X axis = foldLineInCrossSection  (local joint csys Z, the BoxTubeHeight direction)
    //   Z axis = tubeAxis                (sweep direction, gives the tube length extent -- discarded)
    //   Y axis = cross(Z, X) = cross(tubeAxis, foldLineInCrossSection)  (BoxTubeWidth direction)
    const crossSectionCS = coordSystem(WORLD_ORIGIN, foldLineInCrossSection, tubeAxis);

    const crossSectionBoundingBox = evBox3d(context, {
                "topology" : frameBody,
                "cSys"     : crossSectionCS,
                "tight"    : true
            });

    // X extent (index 0): dimension along foldLineInCrossSection = local joint csys Z = BoxTubeHeight.
    // Y extent (index 1): dimension along cross(tubeAxis, foldLineInCrossSection) = BoxTubeWidth.
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
// sweep edge on an interior (cavity-surface) planar swept face whose normal is aligned with
// the fold-line direction (the bend edge direction).
//
// Interior face identification:
//   An interior face's outward normal points toward the body centroid (into the tube cavity).
//   An exterior face's outward normal points away from the body centroid.  A single dot
//   product -- dot(faceNormal, bodyCentroid - faceCenter) -- distinguishes the two: positive
//   means interior, negative means exterior.  This covers all profile geometries: simple box
//   tube without fillets, box tube with exterior fillets, interior fillets, or both.
//
// On each qualifying interior face, only longitudinal sweep edges (tangent parallel to
// tubeAxis) are considered.  The minimum perpendicular distance from the outer wall face
// plane to any such edge is returned.
//
// For a hollow box tube without corner fillets, this equals the wall thickness.
// For a tube with interior corner fillets, this equals the wall thickness plus the interior
// fillet radius (the inner planar face is narrower, so its nearest edge is set back).
// For a solid (non-hollow) tube, no interior face exists and 0 * meter is returned.
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

    // Outer wall face plane for distance measurement.
    const outerWallPlane = evFaceTangentPlane(context, {
                "face" : outerWallFace,
                "parameter" : vector(0.5, 0.5)
            });

    // Fold-line direction: project apexCoordSystem.zAxis onto the plane perpendicular to
    // tubeAxis so that any floating-point tubeAxis component introduced when the frame body
    // was created without a reference face is removed.  Swept face normals are exactly
    // perpendicular to tubeAxis; without the projection, parallelVectors(faceNormal, foldLine)
    // fails for all top/bottom faces and the function returns the 0-fallback.
    // For standard box-tube geometry the projected component is zero and this is a no-op.
    const foldLineDirection = projectFoldLineOntoCrossSection(apexCoordSystem.zAxis, tubeAxis);

    // Body centroid for interior/exterior face classification.  Interior faces have outward
    // normals pointing toward the body centroid; exterior faces point away from it.
    const bodyCentroid = evApproximateCentroid(context, { "entities" : frameBody });

    // All planar swept faces on this body.  The qGeometry(PLANE) filter excludes fillet
    // (cylindrical) faces so they never participate regardless of inner/outer classification.
    const allPlanarSweptFaces = evaluateQuery(context, qHasAttributeWithValueMatching(
                qOwnedByBody(frameBody, EntityType.FACE),
                FRAME_ATTRIBUTE_TOPOLOGY_NAME,
                { "topologyType" : FrameTopologyType.SWEPT_FACE })
            ->qGeometry(GeometryType.PLANE));

    var minimumPositiveDistance = undefined;

    for (var sweptFace in allPlanarSweptFaces)
    {
        const faceTangentPlane = evFaceTangentPlane(context, {
                    "face" : sweptFace,
                    "parameter" : vector(0.5, 0.5)
                });

        // Only process faces whose outward normal is parallel to the fold-line direction
        // (the "top" and "bottom" faces in the local joint coordinate system).
        if (!parallelVectors(faceTangentPlane.normal, foldLineDirection))
            continue;

        // Only process interior (cavity surface) faces.  An interior face's outward normal
        // points toward the body centroid (into the hollow cavity).  An exterior face's
        // normal points away from the body centroid (out of the tube body).
        if (dot(faceTangentPlane.normal, bodyCentroid - faceTangentPlane.origin) <= 0 * meter)
            continue;

        // Iterate over the edges of this interior top/bottom face.
        const faceEdges = evaluateQuery(context, qAdjacent(sweptFace, AdjacencyType.EDGE, EntityType.EDGE));

        for (var faceEdge in faceEdges)
        {
            // Only consider longitudinal sweep edges: edges whose tangent is parallel to
            // the tube axis.  Cross-sectional edges (running across the tube width or along
            // the miter cut) are not sweep lines and must be excluded.
            const edgeTangentLine = evEdgeTangentLine(context, {
                        "edge" : faceEdge,
                        "parameter" : 0.5
                    });

            if (!parallelVectors(edgeTangentLine.direction, tubeAxis))
                continue;

            // Perpendicular distance from the outer wall face plane to this edge midpoint.
            // The edge is parallel to tubeAxis which is perpendicular to the outer wall
            // normal, so the distance is constant along the entire edge.
            const signedDistance = dot(edgeTangentLine.origin - outerWallPlane.origin,
                    outerWallPlane.normal);
            const distance = (signedDistance >= 0 * meter) ? signedDistance : -signedDistance;

            if (minimumPositiveDistance == undefined || distance < minimumPositiveDistance)
                minimumPositiveDistance = distance;
        }
    }

    // Fallback: return zero if no qualifying edge was found (solid tube, no cavity).
    if (minimumPositiveDistance == undefined)
        return 0 * meter;

    return minimumPositiveDistance;
}
