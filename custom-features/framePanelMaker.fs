FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/frameAttributes.fs", version : "2909.0");
import(path : "onshape/std/frameUtils.fs", version : "2909.0");

// Unit conversion constants for panel dimension display
const METERS_TO_MM = 1000;
const MM_PER_INCH = 25.4;
const METERS_TO_INCHES = METERS_TO_MM / MM_PER_INCH;

// Minimum number of frame members required to enclose a panel (triangle = 3 sides minimum).
const MINIMUM_FRAME_MEMBERS = 3;

// Number of decimal places for rounding computed panel dimensions in meters
// (3 places = 1 mm resolution).
const DIMENSION_ROUNDING_PRECISION = 3;

// Integer bound spec for the alignment manipulator index.
// 0 = panel flush with the positive panel-normal face of the frames
// 1 = panel centered in the frame depth (default)
// 2 = panel flush with the negative panel-normal face of the frames
const PANEL_ALIGNMENT_BOUND = { (unitless) : [0, 1, 2] } as IntegerBoundSpec;

// Maximum body-to-body distance for two frame members to be considered adjacent/touching.
const JOINT_CONTACT_TOLERANCE = 1e-4 * meter;

/**
 * Formats a dimensionless length value (in meters) as a fractional-inches string,
 * reduced to lowest terms. For example, 0.047625 m => "1-7/8".
 *
 * @param lengthInMeters {number} : Dimensionless number representing the length in meters.
 * @param maximumDenominator {number} : Finest denominator to use; must be a positive power
 *                                      of 2 (e.g. 16 for 1/16-inch resolution).
 * @returns {string} : Formatted string such as "12", "3-1/2", or "0-3/16".
 */
function formatInchFractionString(lengthInMeters is number, maximumDenominator is number) returns string
{
    const totalInches = lengthInMeters * METERS_TO_INCHES;
    const wholePart = floor(totalInches);
    var numerator = round((totalInches - wholePart) * maximumDenominator);
    var denominator = maximumDenominator;

    if (numerator >= denominator)
    {
        return toString(wholePart + 1);
    }

    if (numerator == 0)
    {
        return toString(wholePart);
    }

    while (numerator % 2 == 0)
    {
        numerator = numerator / 2;
        denominator = denominator / 2;
    }

    if (wholePart == 0)
    {
        return toString(numerator) ~ "/" ~ toString(denominator);
    }
    return toString(wholePart) ~ "-" ~ toString(numerator) ~ "/" ~ toString(denominator);
}

/**
 * Builds the display name string for the panel body given its computed bounding-box
 * dimensions (width and height in the panel plane).
 *
 * @param prefix {string} : User-specified name prefix (e.g. "Panel").
 * @param panelDim1 {number} : Dimensionless first panel dimension in meters.
 * @param panelDim2 {number} : Dimensionless second panel dimension in meters.
 * @param useMillimeters {boolean} : When true, formats as "Xmm x Ymm";
 *                                   when false, formats as fractional inches.
 * @returns {string} : The complete formatted name string.
 */
function buildPanelName(prefix is string, panelDim1 is number, panelDim2 is number, useMillimeters is boolean) returns string
{
    if (useMillimeters)
    {
        const dim1Mm = roundToPrecision(panelDim1 * METERS_TO_MM, 1);
        const dim2Mm = roundToPrecision(panelDim2 * METERS_TO_MM, 1);
        return prefix ~ " - " ~ toString(dim1Mm) ~ "mm x " ~ toString(dim2Mm) ~ "mm";
    }
    else
    {
        const dim1InchString = formatInchFractionString(panelDim1, 16);
        const dim2InchString = formatInchFractionString(panelDim2, 16);
        return prefix ~ " - " ~ dim1InchString ~ " in x " ~ dim2InchString ~ " in";
    }
}

/**
 * Creates a solid panel body that fills the void enclosed by a closed loop of frame members.
 *
 * Algorithm overview:
 *   1. Validate member count and confirm all selections are Frame feature members.
 *   2. Collect 3 centroid points per member (start cap face, body, end cap face) and
 *      compute their world-coordinate bounding box. The axis with the minimum extent is
 *      the panel opening normal — this is the "bounding box of the full sweep curve" spec:
 *      a member whose path lies in the XY plane has all centroids at Z ≈ 0, giving extentZ ≈ 0.
 *   3. If no world axis has a clearly minimal extent (frame not axis-aligned), fall back to the
 *      cross-product-of-tangents approach using getFrameSweepDirection with corrected strategy
 *      order (arc swept edges checked before cylindrical faces to prevent rolled box tube members
 *      from returning the opening normal direction as a "sweep direction").
 *   4. Coplanarity validated on the cross-product fallback path via perpendicularVectors().
 *      The bounding box path is self-validating (minExtent < 20 % of maxExtent).
 *   5. Build the panel coordinate system (Z = N, X = first member sweep direction). Use evBox3d
 *      in that system to derive the three alignment positions from the frame depth.
 *   6. Sort the selected members into a closed loop via a body-adjacency graph and a
 *      Hamiltonian cycle search.
 *   7. For each consecutive pair in the loop, compute the 2D boundary constraint via
 *      computeConstraint2D() — bounding box for most members, circle arc for rolled round
 *      tubes — and solve for the corner position.
 *   8. opPolyline -> opFillSurface -> opExtrude (+/-thickness/2 along N).
 *   9. Apply edge gap via opOffsetFace on the panel's non-cap perimeter faces.
 *  10. Name the panel body from its XY bounding box dimensions.
 *
 * Alignment manipulator:
 *   Index 0 - panel flush with the +normal face of the frames (front)
 *   Index 1 - panel centered between the two frame faces (default)
 *   Index 2 - panel flush with the -normal face of the frames (back)
 */
annotation { "Feature Type Name" : "Panel Maker",
             "Feature Type Description" : "Creates a panel that fills the void of a closed loop of frame members",
             "Manipulator Change Function" : "panelMakerManipulatorChange" }
export const panelMakerFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Frame Members", "Filter" : EntityType.BODY && BodyType.SOLID }
        definition.frameMembers is Query;

        annotation { "Group Name" : "Detail Parameters" }
        {
            annotation { "Name" : "Panel Thickness", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.thickness, {(millimeter) : [0.1, 5.6, 100]} as LengthBoundSpec);

            annotation { "Name" : "Edge Gap", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.gap, {(millimeter) : [0.0, 3, 100]} as LengthBoundSpec);
        }

        annotation { "Name" : "Alignment position", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isInteger(definition.alignmentIndex, PANEL_ALIGNMENT_BOUND);

        annotation { "Name" : "Name Dimensions in mm" }
        definition.mm is boolean;

        annotation { "Name" : "Name Prefix", "Default" : "Panel" }
        definition.namePrefix is string;
    }
    {
        // ── 1. Resolve and validate the selected frame members ──────────────────────────

        const frameMemberBodies = evaluateQuery(context, definition.frameMembers);
        const memberCount = size(frameMemberBodies);

        if (memberCount < MINIMUM_FRAME_MEMBERS)
        {
            reportFeatureError(context, id,
                "At least " ~ toString(MINIMUM_FRAME_MEMBERS) ~ " frame members must be selected to enclose a panel");
            return;
        }

        for (var memberIndex = 0; memberIndex < memberCount; memberIndex += 1)
        {
            const frameProfileQuery = qHasAttribute(frameMemberBodies[memberIndex], FRAME_ATTRIBUTE_PROFILE_NAME);
            if (isQueryEmpty(context, frameProfileQuery))
            {
                reportFeatureError(context, id,
                    "All selections must be frame members created by the Frame feature");
                return;
            }
        }

        // ── 2. Collect path centroid points for bounding box planarity detection ─────────
        //
        // Three centroid samples per member: start cap face centroid, member body centroid,
        // and end cap face centroid. The start and end cap centroids are points on the sweep
        // path at each end of the member. The body centroid approximates the midpoint of the
        // path. Together they faithfully trace the sweep path for straight, arc-swept, and
        // torus-swept members without needing direct access to the path curve itself.
        //
        // For a torus-swept (rolled round tube) member this is especially important: the
        // previous tangent-vector approach fired Strategy 2 (cylinder face axis) first for
        // rolled box tube members, returning the opening normal direction instead of a path
        // tangent. Using centroid coordinates avoids all surface-type strategy logic for the
        // purpose of plane detection, since the centroids simply lie on the path.

        const totalCentroidCount = memberCount * 3;
        var pathCentroids = makeArray(totalCentroidCount);

        for (var memberIndex = 0; memberIndex < memberCount; memberIndex += 1)
        {
            pathCentroids[memberIndex * 3]     = evApproximateCentroid(context, { "entities" : qFrameStartFace(frameMemberBodies[memberIndex]) });
            pathCentroids[memberIndex * 3 + 1] = evApproximateCentroid(context, { "entities" : frameMemberBodies[memberIndex] });
            pathCentroids[memberIndex * 3 + 2] = evApproximateCentroid(context, { "entities" : qFrameEndFace(frameMemberBodies[memberIndex]) });
        }

        // ── 3. Bounding box in world coordinates → minimum-extent axis = opening normal ─
        //
        // For a frame ring that lies in the XY plane, all path centroid points have Z ≈ 0,
        // so extentZ ≈ 0 while extentX and extentY reflect the frame's in-plane dimensions.
        // The axis with the smallest bounding-box extent is the panel normal.
        //
        // This directly implements the spec:
        //   "Evaluate the curve in its entirety with a tight bounding box calc. If that
        //   bounding box has a 0 dimension in any axis, it's planar. Find direction of
        //   planarity from 0 direction. This handles any circular case or wiggly spline
        //   case."
        //
        // If no world axis has a clearly minimal extent (frame is not axis-aligned), fall
        // through to the cross-product-of-tangents fallback for general orientation support.

        var pathMinX is ValueWithUnits = pathCentroids[0][0];
        var pathMaxX is ValueWithUnits = pathCentroids[0][0];
        var pathMinY is ValueWithUnits = pathCentroids[0][1];
        var pathMaxY is ValueWithUnits = pathCentroids[0][1];
        var pathMinZ is ValueWithUnits = pathCentroids[0][2];
        var pathMaxZ is ValueWithUnits = pathCentroids[0][2];

        for (var pointIndex = 1; pointIndex < totalCentroidCount; pointIndex += 1)
        {
            const pathPoint = pathCentroids[pointIndex];
            if (pathPoint[0] < pathMinX) pathMinX = pathPoint[0];
            if (pathPoint[0] > pathMaxX) pathMaxX = pathPoint[0];
            if (pathPoint[1] < pathMinY) pathMinY = pathPoint[1];
            if (pathPoint[1] > pathMaxY) pathMaxY = pathPoint[1];
            if (pathPoint[2] < pathMinZ) pathMinZ = pathPoint[2];
            if (pathPoint[2] > pathMaxZ) pathMaxZ = pathPoint[2];
        }

        const pathExtentX = pathMaxX - pathMinX;
        const pathExtentY = pathMaxY - pathMinY;
        const pathExtentZ = pathMaxZ - pathMinZ;

        var openingNormal = vector(0, 0, 1);
        var panelNormalFound = false;

        // Determine the maximum in-plane extent of the centroid cloud.
        var pathMaxExtent is ValueWithUnits = pathExtentX;
        if (pathExtentY > pathMaxExtent) pathMaxExtent = pathExtentY;
        if (pathExtentZ > pathMaxExtent) pathMaxExtent = pathExtentZ;

        if (pathMaxExtent > 1e-6 * meter)
        {
            // An axis qualifies as the panel normal when its centroid extent is no more
            // than 20 % of the largest extent. This allows small floating-point deviations
            // while still rejecting frames that are clearly not axis-aligned.
            const planarity_threshold = 0.20;

            if (pathExtentZ <= pathExtentX && pathExtentZ <= pathExtentY &&
                    pathExtentZ < pathMaxExtent * planarity_threshold)
            {
                openingNormal   = vector(0, 0, 1);
                panelNormalFound = true;
            }
            else if (pathExtentY <= pathExtentX && pathExtentY <= pathExtentZ &&
                    pathExtentY < pathMaxExtent * planarity_threshold)
            {
                openingNormal   = vector(0, 1, 0);
                panelNormalFound = true;
            }
            else if (pathExtentX <= pathExtentY && pathExtentX <= pathExtentZ &&
                    pathExtentX < pathMaxExtent * planarity_threshold)
            {
                openingNormal   = vector(1, 0, 0);
                panelNormalFound = true;
            }
        }

        // ── 4. Fallback: cross-product of non-parallel tangents (non-axis-aligned frames) ─
        //
        // If the bounding box approach did not find a world-axis-aligned normal (e.g., for
        // frames tilted at an oblique angle), fall back to the original cross-product method.
        // Coplanarity is verified on this path by checking that every tangent is perpendicular
        // to the derived opening normal.

        if (!panelNormalFound)
        {
            var sweepDirections = makeArray(memberCount * 2);
            var sweepDirectionCount = 0;

            for (var fallbackIndex = 0; fallbackIndex < memberCount; fallbackIndex += 1)
            {
                sweepDirections[sweepDirectionCount]     = getFrameSweepDirection(context, frameMemberBodies[fallbackIndex], 0);
                sweepDirections[sweepDirectionCount + 1] = getFrameSweepDirection(context, frameMemberBodies[fallbackIndex], 1);
                sweepDirectionCount += 2;
            }

            for (var outerIndex = 0; outerIndex < sweepDirectionCount && !panelNormalFound; outerIndex += 1)
            {
                for (var innerIndex = outerIndex + 1; innerIndex < sweepDirectionCount && !panelNormalFound; innerIndex += 1)
                {
                    if (!parallelVectors(sweepDirections[outerIndex], sweepDirections[innerIndex]))
                    {
                        openingNormal   = normalize(cross(sweepDirections[outerIndex], sweepDirections[innerIndex]));
                        panelNormalFound = true;
                    }
                }
            }

            if (!panelNormalFound)
            {
                reportFeatureError(context, id,
                    "All selected frame members appear to sweep in the same direction and cannot enclose a panel");
                return;
            }

            for (var directionIndex = 0; directionIndex < sweepDirectionCount; directionIndex += 1)
            {
                if (!perpendicularVectors(sweepDirections[directionIndex], openingNormal))
                {
                    reportFeatureError(context, id,
                        "Selected frame members do not all lie in a common plane. " ~
                        "Panel Maker requires all sweep paths to be coplanar.");
                    return;
                }
            }
        }

        // ── 5. Build panel coordinate system ────────────────────────────────────────────
        //
        // Origin: centroid of all member body centroids (approximate center of the frame ring).
        // Z axis: openingNormal (the panel normal).
        // X axis: sweep direction of the first member at its start end (in the panel plane).
        // Y axis: cross(Z, X) for a right-handed system.

        var panelCentroid = vector(0, 0, 0) * meter;
        for (var memberIndex = 0; memberIndex < memberCount; memberIndex += 1)
        {
            panelCentroid = panelCentroid + evApproximateCentroid(context, {
                        "entities" : frameMemberBodies[memberIndex]
                    });
        }
        panelCentroid = panelCentroid / memberCount;

        // panelXAxis is the sweep direction of frameMemberBodies[0] at its start end.
        // Selection order determines which member is [0]; for consistent orientation the
        // user should select members in a predictable order (e.g., left-to-right).
        const panelXAxis = normalize(getFrameSweepDirection(context, frameMemberBodies[0], 0));
        const panelYAxis = normalize(cross(openingNormal, panelXAxis));
        const panelCSys  = coordSystem(panelCentroid, panelXAxis, openingNormal);

        // ── 6. Bounding box alignment extents ───────────────────────────────────────────
        //
        // evBox3d in the panel coordinate system gives the total depth extent of all frame
        // members along the panel normal (Z axis). Z_min and Z_max bound the frame material.
        //   Index 0: panel mid-plane at Z_max - thickness/2  (flush with front face)
        //   Index 1: panel mid-plane at (Z_max + Z_min) / 2  (centered, default)
        //   Index 2: panel mid-plane at Z_min + thickness/2  (flush with back face)

        const frameBox = evBox3d(context, {
                    "topology" : qUnion(frameMemberBodies),
                    "cSys"     : panelCSys,
                    "tight"    : true
                });

        const frameDepthZ  = frameBox.maxCorner[2] - frameBox.minCorner[2];
        const frameCenterZ = (frameBox.maxCorner[2] + frameBox.minCorner[2]) / 2;

        if (definition.thickness >= frameDepthZ)
        {
            reportFeatureWarning(context, id,
                "Panel thickness equals or exceeds the frame depth; alignment adjustment is not available");
        }

        const maxAlignmentOffset = (frameDepthZ > definition.thickness) ?
            (frameDepthZ - definition.thickness) / 2 : 0 * meter;

        const flushFrontZ = frameCenterZ + maxAlignmentOffset;
        const flushBackZ  = frameCenterZ - maxAlignmentOffset;

        var alignmentZ is ValueWithUnits = frameCenterZ;
        if (definition.alignmentIndex == 0)
        {
            alignmentZ = flushFrontZ;
        }
        else if (definition.alignmentIndex == 2)
        {
            alignmentZ = flushBackZ;
        }

        // ── 7. Three-point alignment manipulator ─────────────────────────────────────────

        const alignmentPointFront  = panelCentroid + flushFrontZ  * openingNormal;
        const alignmentPointCenter = panelCentroid + frameCenterZ * openingNormal;
        const alignmentPointBack   = panelCentroid + flushBackZ   * openingNormal;

        addManipulators(context, id, {
                    "alignmentManipulator" : pointsManipulator({
                                "points" : [
                                    alignmentPointFront,
                                    alignmentPointCenter,
                                    alignmentPointBack
                                ],
                                "index" : definition.alignmentIndex
                            })
                });

        // ── 8. Build connected loop of frame members ─────────────────────────────────────

        const loopOrder = buildLoopFromBodyAdjacency(context, frameMemberBodies, JOINT_CONTACT_TOLERANCE);

        if (size(loopOrder) == 0)
        {
            reportFeatureError(context, id,
                "Selected frame members do not form a closed loop; verify that each member touches at least two others in the selection");
            return;
        }

        // ── 9. Compute panel boundary corners from member bounding boxes ─────────────────────
        //
        // For each consecutive pair (A, B) in the loop, the panel corner is the intersection
        // of their boundary constraints in panel XY at Z = alignmentZ.
        //
        // computeConstraint2D() replaces the previous face-based approach:
        //   - For rolled round tube members (torus swept face): circle constraint from the
        //     inner arc of the OUTER torus face. The outer face is identified by the largest
        //     major radius, which correctly selects the outer surface of a hollow tube and
        //     avoids the hollow interior face being mis-selected.
        //   - For all other members: line constraint from the inner bounding box face. The
        //     bounding box is computed in a local coordinate system aligned with the member's
        //     sweep direction at the junction end, giving the correct cross-section extent
        //     perpendicular to the sweep regardless of profile shape (box tube, I-beam,
        //     channel, angle iron). This replaces the previous scored-face heuristic, which
        //     incorrectly selected internal hollow faces of box tubes and the wrong inner
        //     walls of complex profiles.

        var cornerPoints = makeArray(memberCount);
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const currentMemberBody = frameMemberBodies[loopOrder[loopStep]];
            const nextMemberBody    = frameMemberBodies[loopOrder[(loopStep + 1) % memberCount]];

            cornerPoints[loopStep] = computeCornerPoint(context,
                    currentMemberBody, nextMemberBody,
                    openingNormal, panelCentroid, panelCSys, alignmentZ);
        }

        // ── DEBUG: Alignment and boundary construction geometry ────────────────────────────
        //
        // Diagnostic visualization to help identify panel misalignment and "twisties".
        // Remove this block when the feature is working correctly.
        //
        // Color key:
        //   RED/GREEN/BLUE : Panel coordinate system — X = first member sweep direction (RED),
        //                    Y (GREEN), Z = opening normal (BLUE).
        //                    A twisted panel almost always traces to RED pointing the wrong
        //                    way; panelXAxis comes from getFrameSweepDirection on member 0,
        //                    a sign flip mirrors all constraint RHS values.
        //   YELLOW         : Panel centroid (coordinate system origin) and alignment center.
        //   CYAN           : Boundary constraint source per member.
        //                    - Non-torus: bounding box rectangle projected to alignment plane.
        //                      The inner edge (toward panel centroid) drives the GREEN line.
        //                    - Torus (rolled round tube): outer torus face highlighted.
        //                      The inner arc of this face drives the BLUE circle constraint.
        //   BLACK          : Alignment front depth point (YELLOW = center, ORANGE = back).
        //   ORANGE         : Alignment back depth point; also void-direction arrows per member.
        //   MAGENTA        : Per-member body centroids (void-direction arrow tails).
        //   GREEN          : Constraint lines in the panel plane at Z = alignmentZ, 200mm long.
        //                    Adjacent GREEN pairs must intersect at the adjacent RED corner point.
        //   BLUE (point)   : Circle constraint center for rolled round tube members.
        //   RED (points)   : Computed corner vertices. Must sit at GREEN line intersections.

        // Panel coordinate system axes
        debug(context, panelCSys, DebugColor.RED, DebugColor.GREEN, DebugColor.BLUE);
        addDebugPoint(context, panelCentroid, DebugColor.YELLOW);

        // Alignment depth reference points
        addDebugPoint(context, alignmentPointFront,  DebugColor.BLACK);
        addDebugPoint(context, alignmentPointCenter, DebugColor.YELLOW);
        addDebugPoint(context, alignmentPointBack,   DebugColor.ORANGE);

        // Per-member: centroid (MAGENTA), void-direction arrow (ORANGE), constraint source (CYAN)
        for (var debugMemberIndex = 0; debugMemberIndex < memberCount; debugMemberIndex += 1)
        {
            const debugMemberBody     = frameMemberBodies[debugMemberIndex];
            const debugMemberCentroid = evApproximateCentroid(context, { "entities" : debugMemberBody });
            addDebugPoint(context, debugMemberCentroid, DebugColor.MAGENTA);

            // Void direction: panel-plane projection of (panelCentroid - memberCentroid).
            const voidDelta   = panelCentroid - debugMemberCentroid;
            const voidInPlane = voidDelta - dot(voidDelta, openingNormal) * openingNormal;
            if (norm(voidInPlane) > 1e-6 * meter)
            {
                addDebugArrow(context, debugMemberCentroid, debugMemberCentroid + voidInPlane,
                        1.5 * millimeter, DebugColor.ORANGE);
            }

            // CYAN: bounding box rectangle (non-torus) or outer torus face (rolled tube).
            const debugSweptFaces    = qFrameSweptFace(debugMemberBody);
            const debugTorusFaceList = evaluateQuery(context, qGeometry(debugSweptFaces, GeometryType.TORUS));

            if (size(debugTorusFaceList) > 0)
            {
                // Rolled round tube: highlight the outer torus face (largest major radius).
                var debugOuterTorusFace = debugTorusFaceList[0];
                var debugOuterRadius    = evSurfaceDefinition(context, {
                            "face" : debugTorusFaceList[0]
                        }).radius;
                for (var debugTorusIndex = 1; debugTorusIndex < size(debugTorusFaceList); debugTorusIndex += 1)
                {
                    const debugCandidateRadius = evSurfaceDefinition(context, {
                                "face" : debugTorusFaceList[debugTorusIndex]
                            }).radius;
                    if (debugCandidateRadius > debugOuterRadius)
                    {
                        debugOuterTorusFace = debugTorusFaceList[debugTorusIndex];
                        debugOuterRadius    = debugCandidateRadius;
                    }
                }
                addDebugEntities(context, debugOuterTorusFace, DebugColor.CYAN);
            }
            else
            {
                // Non-torus member: draw the bounding box rectangle projected to alignment plane.
                // Local CSys: X = sweep direction (param 0), Z = panel normal, Y = cross(Z, X).
                const debugSweepDir   = getFrameSweepDirection(context, debugMemberBody, 0);
                const debugLocalYAxis = normalize(cross(openingNormal, debugSweepDir));
                const debugLocalCSys  = coordSystem(debugMemberCentroid, debugSweepDir, openingNormal);
                const debugMemberBox  = evBox3d(context, {
                            "topology" : debugMemberBody,
                            "cSys"     : debugLocalCSys,
                            "tight"    : true
                        });

                // Four corners of the bounding box rectangle in world space
                // (at Z = 0 relative to the member centroid in the local CSys).
                const debugCornerAWorld = debugMemberCentroid
                        + debugMemberBox.minCorner[0] * debugSweepDir
                        + debugMemberBox.minCorner[1] * debugLocalYAxis;
                const debugCornerBWorld = debugMemberCentroid
                        + debugMemberBox.maxCorner[0] * debugSweepDir
                        + debugMemberBox.minCorner[1] * debugLocalYAxis;
                const debugCornerCWorld = debugMemberCentroid
                        + debugMemberBox.maxCorner[0] * debugSweepDir
                        + debugMemberBox.maxCorner[1] * debugLocalYAxis;
                const debugCornerDWorld = debugMemberCentroid
                        + debugMemberBox.minCorner[0] * debugSweepDir
                        + debugMemberBox.maxCorner[1] * debugLocalYAxis;

                // Project each corner to the panel alignment depth (alignmentZ).
                const debugLocalA = fromWorld(panelCSys, debugCornerAWorld);
                const debugLocalB = fromWorld(panelCSys, debugCornerBWorld);
                const debugLocalC = fromWorld(panelCSys, debugCornerCWorld);
                const debugLocalD = fromWorld(panelCSys, debugCornerDWorld);

                const debugProjA = toWorld(panelCSys, vector(debugLocalA[0], debugLocalA[1], alignmentZ));
                const debugProjB = toWorld(panelCSys, vector(debugLocalB[0], debugLocalB[1], alignmentZ));
                const debugProjC = toWorld(panelCSys, vector(debugLocalC[0], debugLocalC[1], alignmentZ));
                const debugProjD = toWorld(panelCSys, vector(debugLocalD[0], debugLocalD[1], alignmentZ));

                addDebugLine(context, debugProjA, debugProjB, DebugColor.CYAN);
                addDebugLine(context, debugProjB, debugProjC, DebugColor.CYAN);
                addDebugLine(context, debugProjC, debugProjD, DebugColor.CYAN);
                addDebugLine(context, debugProjD, debugProjA, DebugColor.CYAN);
            }
        }

        // Constraint lines and corner points, indexed by loop order.
        // 100mm half-length gives a ~200mm visible segment — roughly 4-8x typical tube
        // depth — so lines are readable without dominating the viewport for normal frames.
        const debugSegmentHalfLength = 100 * millimeter;
        for (var debugLoopStep = 0; debugLoopStep < memberCount; debugLoopStep += 1)
        {
            // Show the constraint contributed by the current member at this loop step.
            const debugCurrentBody = frameMemberBodies[loopOrder[debugLoopStep]];
            const debugNextBody    = frameMemberBodies[loopOrder[(debugLoopStep + 1) % memberCount]];
            const debugConstraint  = computeConstraint2D(context, debugCurrentBody, debugNextBody,
                    openingNormal, panelCentroid, panelCSys);

            if (debugConstraint.kind == "line")
            {
                // Foot of perpendicular from the panel origin to the constraint line,
                // then extend ±halfLen along the line direction (-ny, nx) for visibility.
                const debugNx      = debugConstraint.nx;
                const debugNy      = debugConstraint.ny;
                const debugRhs     = debugConstraint.rhs;
                const debugHalfLen = debugSegmentHalfLength / meter;
                const lineA = toWorld(panelCSys, vector(
                            (debugNx * debugRhs + debugNy * debugHalfLen) * meter,
                            (debugNy * debugRhs - debugNx * debugHalfLen) * meter,
                            alignmentZ));
                const lineB = toWorld(panelCSys, vector(
                            (debugNx * debugRhs - debugNy * debugHalfLen) * meter,
                            (debugNy * debugRhs + debugNx * debugHalfLen) * meter,
                            alignmentZ));
                addDebugLine(context, lineA, lineB, DebugColor.GREEN);
            }
            else
            {
                // Circle constraint (rolled round tube): show center at alignmentZ depth
                addDebugPoint(context, toWorld(panelCSys, vector(
                            debugConstraint.cx * meter,
                            debugConstraint.cy * meter,
                            alignmentZ)), DebugColor.BLUE);
            }

            // Corner vertex: should sit at the intersection of adjacent GREEN lines
            addDebugPoint(context, cornerPoints[debugLoopStep], DebugColor.RED);
        }
        // ── END DEBUG ──────────────────────────────────────────────────────────────────────

        // ── 10. Build closed boundary wire ───────────────────────────────────────────────

        var boundaryPoints = makeArray(memberCount + 1);
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            boundaryPoints[loopStep] = cornerPoints[loopStep];
        }
        boundaryPoints[memberCount] = boundaryPoints[0];

        opPolyline(context, id + "boundaryPolyline", {
                    "points" : boundaryPoints
                });

        // ── 11. Fill the boundary polygon to a flat panel surface ────────────────────────

        opFillSurface(context, id + "panelFill", {
                    "edgesG0" : qCreatedBy(id + "boundaryPolyline", EntityType.EDGE)
                });

        // ── 12. Extrude the fill face into a solid panel ─────────────────────────────────
        //
        // Extrude symmetrically by thickness/2 in each direction along openingNormal.
        // The fill face is at Z = alignmentZ, so the solid spans
        // [alignmentZ - thickness/2, alignmentZ + thickness/2] along openingNormal.

        opExtrude(context, id + "panelExtrude", {
                    "entities"   : qCreatedBy(id + "panelFill", EntityType.FACE),
                    "direction"  : openingNormal,
                    "endBound"   : BoundingType.BLIND,
                    "endDepth"   : definition.thickness / 2,
                    "startBound" : BoundingType.BLIND,
                    "startDepth" : definition.thickness / 2
                });

        const panelBodyQuery = qCreatedBy(id + "panelExtrude", EntityType.BODY);

        // ── 13. Clean up intermediate bodies ─────────────────────────────────────────────

        opDeleteBodies(context, id + "deleteWire", {
                    "entities" : qCreatedBy(id + "boundaryPolyline", EntityType.BODY)
                });
        opDeleteBodies(context, id + "deleteFill", {
                    "entities" : qCreatedBy(id + "panelFill", EntityType.BODY)
                });

        // ── 14. Apply edge gap ────────────────────────────────────────────────────────────
        //
        // Offset only the perimeter (non-cap) faces inward by definition.gap.
        // qNonCapEntity excludes the front and back extrude cap faces.

        if (definition.gap > 0 * meter)
        {
            opOffsetFace(context, id + "panelGapOffset", {
                        "moveFaces"      : qNonCapEntity(id + "panelExtrude", EntityType.FACE),
                        "offsetDistance" : -definition.gap
                    });
        }

        // ── 15. Name the panel body ───────────────────────────────────────────────────────

        const panelBox = evBox3d(context, {
                    "topology" : panelBodyQuery,
                    "cSys"     : panelCSys,
                    "tight"    : true
                });
        const panelDim1 = roundToPrecision(
            (panelBox.maxCorner[0] - panelBox.minCorner[0]) / meter, DIMENSION_ROUNDING_PRECISION);
        const panelDim2 = roundToPrecision(
            (panelBox.maxCorner[1] - panelBox.minCorner[1]) / meter, DIMENSION_ROUNDING_PRECISION);

        setProperty(context, {
                    "entities"     : panelBodyQuery,
                    "propertyType" : PropertyType.NAME,
                    "value"        : buildPanelName(definition.namePrefix, panelDim1, panelDim2, definition.mm)
                });
    });

/**
 * Returns the sweep direction of a frame member at the specified path parameter (0 = start,
 * 1 = end). Uses a priority-ordered set of strategies to obtain the path tangent from the
 * frame member's geometry:
 *
 *   1. Straight swept edges (rectangular tube, I-beam, channel, angle iron):
 *      evEdgeTangentLine on the first LINE-geometry swept edge. Returns the exact sweep axis
 *      regardless of how the ends are cut (perpendicular, mitered, compound).
 *   2. Arc or other curved swept edges (rolled box tube, rolled channel, etc.):
 *      evEdgeTangentLine on the first available swept edge at the requested parameter.
 *      This MUST come before the cylindrical-face check (Strategy 3) because rolled box
 *      tubes have cylindrical swept faces whose zAxis equals the arc rotation axis (= panel
 *      opening normal), not the path tangent.
 *   3. Cylindrical swept faces (straight round tube / pipe with no swept edges):
 *      evSurfaceDefinition on the first CYLINDER-geometry swept face; coordSystem.zAxis is
 *      the cylinder axis = the sweep direction for a straight round tube.
 *   4. Toroidal swept face (rolled round tube with no swept edges):
 *      The torus axis is the panel normal, not the path tangent, so the tangent is computed
 *      as cross(torusAxis, radialDirection) using the boundary edge nearest the requested
 *      cap face.
 *
 * @param context        {Context} : The active feature context.
 * @param memberBody     {Query}   : A single frame member solid body.
 * @param pathParameter  {number}  : Path parameter in [0, 1]; 0 = start end, 1 = end end.
 * @returns {Vector} : Unit vector along the sweep direction at the requested parameter.
 */
function getFrameSweepDirection(context is Context, memberBody is Query, pathParameter is number) returns Vector
{
    // Strategy 1: straight swept edge — most common for profiled sections (box tube, I-beam,
    // channel, angle iron). A straight edge tangent is the exact sweep direction.
    const sweptEdges = qFrameSweptEdge(memberBody);
    const straightEdges = evaluateQuery(context, qGeometry(sweptEdges, GeometryType.LINE));
    if (size(straightEdges) > 0)
    {
        return evEdgeTangentLine(context, {
                    "edge"      : straightEdges[0],
                    "parameter" : pathParameter
                }).direction;
    }

    // Strategy 2: arc or general swept edge — used for rolled (arc-swept) profiled sections
    // such as rolled box tube, rolled rectangular tube, or rolled channel.
    //
    // IMPORTANT: This check must come BEFORE the cylindrical-face check (Strategy 3).
    // A rolled box tube swept along an arc in the XY plane has cylindrical swept faces whose
    // coordSystem.zAxis is the arc's rotation axis — which equals the PANEL OPENING NORMAL,
    // not a path tangent. Using the arc edge tangent via evEdgeTangentLine correctly gives a
    // direction IN the panel plane, avoiding a false planarity failure.
    const allSweptEdges = evaluateQuery(context, sweptEdges);
    if (size(allSweptEdges) > 0)
    {
        return evEdgeTangentLine(context, {
                    "edge"      : allSweptEdges[0],
                    "parameter" : pathParameter
                }).direction;
    }

    // Strategy 3: cylindrical swept face axis — used for straight round tubes and pipes.
    // A straight round tube has no corner swept edges, so the cylinder face axis is the
    // only available source of the sweep direction.
    const sweptFaces = qFrameSweptFace(memberBody);
    const cylinderFaces = evaluateQuery(context, qGeometry(sweptFaces, GeometryType.CYLINDER));
    if (size(cylinderFaces) > 0)
    {
        return evSurfaceDefinition(context, { "face" : cylinderFaces[0] }).coordSystem.zAxis;
    }

    // Strategy 4: toroidal swept face — used for rolled (arc-swept) round tube members.
    //
    // When a round tube is swept along an arc, its swept face is a torus rather than a
    // cylinder, so Strategy 3 finds no faces. The sweep tangent at the requested end is
    // cross(torusAxis, radialDirection), where radialDirection is the in-plane vector from
    // the torus center to a point on the torus boundary edge at that end of the arc.
    //
    // The boundary edge at the requested end is selected by proximity to the corresponding
    // cap face centroid via qClosestTo. This avoids depending on the arbitrary evaluation
    // order of qAdjacent results, which is not guaranteed to be start-to-end.
    const torusFaces = evaluateQuery(context, qGeometry(sweptFaces, GeometryType.TORUS));
    if (size(torusFaces) > 0)
    {
        const torusDefinition = evSurfaceDefinition(context, { "face" : torusFaces[0] });
        const torusCenter     = torusDefinition.coordSystem.origin;
        const torusAxis       = torusDefinition.coordSystem.zAxis;

        const endCapFace      = (pathParameter < 0.5) ? qFrameStartFace(memberBody) : qFrameEndFace(memberBody);
        const endCapCentroid  = evApproximateCentroid(context, { "entities" : endCapFace });
        const closestBoundaryEdge = qClosestTo(
                qAdjacent(torusFaces[0], AdjacencyType.EDGE, EntityType.EDGE),
                endCapCentroid);
        const referencePoint  = evEdgeTangentLine(context, {
                    "edge"      : closestBoundaryEdge,
                    "parameter" : 0.5
                }).origin;

        // Project the radial vector into the torus equatorial (sweep) plane.
        const radialVec      = referencePoint - torusCenter;
        const axialComponent = dot(radialVec, torusAxis);
        const radialInPlane  = radialVec - axialComponent * torusAxis;
        return normalize(cross(torusAxis, radialInPlane / meter));
    }

    // Fallback: should not be reached for valid frame members created by the Frame feature.
    reportFeatureError(context, makeId("panelMakerFeature"),
        "Could not determine sweep direction for a selected frame member.");
    return vector(1, 0, 0);
}

/**
 * Builds an ordered closed loop of frame member indices using a body-adjacency graph.
 *
 * For each pair of selected bodies, evDistance determines whether they are touching.
 * A Hamiltonian cycle is then found in the resulting adjacency graph via depth-first
 * search. This correctly handles both end-to-end frame joints and T-intersections
 * where long spanning members extend beyond the panel boundary.
 *
 * @param context          {Context}        : The active feature context.
 * @param frameBodies      {array}          : Array of frame member body queries.
 * @param contactTolerance {ValueWithUnits} : Maximum body-to-body distance for adjacency.
 * @returns {array} : Ordered member indices (length == memberCount) forming a closed loop,
 *                    or an empty array if no valid Hamiltonian cycle exists.
 */
function buildLoopFromBodyAdjacency(context is Context, frameBodies is array, contactTolerance is ValueWithUnits) returns array
{
    const memberCount = size(frameBodies);
    var adjacencyTable = makeArray(memberCount * memberCount, false);

    for (var outerIndex = 0; outerIndex < memberCount; outerIndex += 1)
    {
        for (var innerIndex = outerIndex + 1; innerIndex < memberCount; innerIndex += 1)
        {
            const distanceResult = evDistance(context, {
                        "side0" : frameBodies[outerIndex],
                        "side1" : frameBodies[innerIndex]
                    });
            if (distanceResult.distance <= contactTolerance)
            {
                adjacencyTable[outerIndex * memberCount + innerIndex] = true;
                adjacencyTable[innerIndex * memberCount + outerIndex] = true;
            }
        }
    }

    var initialVisited = makeArray(memberCount, false);
    initialVisited[0] = true;
    var initialPath = makeArray(memberCount, 0);

    return findHamiltonianCycleDFS(adjacencyTable, initialVisited, initialPath, memberCount, 1);
}

/**
 * Recursive depth-first search that extends a partial Hamiltonian path to a complete
 * cycle through all frame members.
 *
 * @param adjacencyTable  {array}  : Flat n*n boolean adjacency table.
 * @param visitedMembers  {array}  : Boolean flags for members already in the path.
 * @param cyclePath       {array}  : Current partial path of member indices.
 * @param memberCount     {number} : Total number of frame members.
 * @param currentDepth    {number} : Number of members placed in cyclePath so far.
 * @returns {array} : Complete cycle path (length == memberCount) if found, else empty array.
 */
function findHamiltonianCycleDFS(adjacencyTable is array, visitedMembers is array, cyclePath is array, memberCount is number, currentDepth is number) returns array
{
    if (currentDepth == memberCount)
    {
        if (adjacencyTable[cyclePath[currentDepth - 1] * memberCount + cyclePath[0]])
        {
            return cyclePath;
        }
        return [];
    }

    const previousMemberIndex = cyclePath[currentDepth - 1];

    for (var candidateIndex = 0; candidateIndex < memberCount; candidateIndex += 1)
    {
        if (!visitedMembers[candidateIndex] &&
            adjacencyTable[previousMemberIndex * memberCount + candidateIndex])
        {
            var branchVisited = visitedMembers;
            branchVisited[candidateIndex] = true;
            var branchPath = cyclePath;
            branchPath[currentDepth] = candidateIndex;

            const searchResult = findHamiltonianCycleDFS(
                    adjacencyTable, branchVisited, branchPath, memberCount, currentDepth + 1);
            if (size(searchResult) == memberCount)
            {
                return searchResult;
            }
        }
    }

    return [];
}

/**
 * Computes the world-space corner point at the junction of two adjacent frame members,
 * placed at the chosen panel alignment depth.
 *
 * Method:
 *   1. Derive a 2D constraint for each member via computeConstraint2D:
 *        - Rolled round tube (torus swept face) → circle constraint from the inner arc
 *          of the outer torus face.
 *        - All other members → line constraint from the inner bounding box face.
 *   2. Intersect the two constraints (line-line, line-circle, or circle-circle).
 *      Circle-circle is reduced to line-circle via the radical axis.
 *   3. Reconstruct the world-space corner with toWorld at Z = alignmentZ.
 *
 * @param context       {Context}        : The active feature context.
 * @param currentBody   {Query}          : Current frame member body.
 * @param nextBody      {Query}          : Next frame member body.
 * @param openingNormal {Vector}         : Panel normal unit vector.
 * @param panelCentroid {Vector}         : World-space centroid of the frame ring (with units).
 * @param panelCSys     {CoordSystem}    : Panel coordinate system (Z = normal, origin = centroid).
 * @param alignmentZ    {ValueWithUnits} : Alignment depth along the panel normal from the panel origin.
 * @returns {Vector} : World-space corner position with meter units.
 */
function computeCornerPoint(context is Context, currentBody is Query, nextBody is Query,
        openingNormal is Vector, panelCentroid is Vector, panelCSys is CoordSystem, alignmentZ is ValueWithUnits) returns Vector
{
    const constraintCurrent = computeConstraint2D(context, currentBody, nextBody, openingNormal, panelCentroid, panelCSys);
    const constraintNext    = computeConstraint2D(context, nextBody, currentBody, openingNormal, panelCentroid, panelCSys);

    var cornerX is number = 0;
    var cornerY is number = 0;

    if (constraintCurrent.kind == "line" && constraintNext.kind == "line")
    {
        // Two non-torus members: solve the 2x2 linear system
        //   nx1*x + ny1*y = rhs1
        //   nx2*x + ny2*y = rhs2
        const det = constraintCurrent.nx * constraintNext.ny - constraintCurrent.ny * constraintNext.nx;

        if (parallelVectors(vector(constraintCurrent.nx, constraintCurrent.ny, 0),
                            vector(constraintNext.nx,    constraintNext.ny,    0)))
        {
            // Parallel constraints (T-junction or collinear): midpoint of the two
            // foot-of-perpendicular projections.
            cornerX = (constraintCurrent.rhs * constraintCurrent.nx + constraintNext.rhs * constraintNext.nx) / 2;
            cornerY = (constraintCurrent.rhs * constraintCurrent.ny + constraintNext.rhs * constraintNext.ny) / 2;
        }
        else
        {
            cornerX = (constraintCurrent.rhs * constraintNext.ny - constraintNext.rhs * constraintCurrent.ny) / det;
            cornerY = (constraintCurrent.nx * constraintNext.rhs - constraintNext.nx * constraintCurrent.rhs) / det;
        }
    }
    else
    {
        // At least one rolled round tube member: handle line-circle or circle-circle.
        //
        // For circle-circle, subtract the two circle equations to obtain the radical axis
        // (a line), then reduce to the line-circle case.

        var lineConstraint      = { "nx" : 0, "ny" : 1, "rhs" : 0 };
        var circleConstraint    = constraintCurrent;
        var rolledBody          = currentBody;
        var adjacentBodyForRoot = nextBody;

        if (constraintCurrent.kind == "circle" && constraintNext.kind == "circle")
        {
            // Radical axis: 2*(cx2-cx1)*x + 2*(cy2-cy1)*y = r1^2 - r2^2 + cx2^2 - cx1^2 + cy2^2 - cy1^2
            const dx  = constraintNext.cx - constraintCurrent.cx;
            const dy  = constraintNext.cy - constraintCurrent.cy;
            const len = sqrt(dx * dx + dy * dy);
            const radicalRhs = (constraintNext.cx ^ 2 - constraintCurrent.cx ^ 2
                              + constraintNext.cy ^ 2 - constraintCurrent.cy ^ 2
                              + constraintCurrent.radius ^ 2 - constraintNext.radius ^ 2) / (2 * len);
            lineConstraint      = { "nx" : dx / len, "ny" : dy / len, "rhs" : radicalRhs };
            circleConstraint    = constraintCurrent;
            rolledBody          = currentBody;
            adjacentBodyForRoot = nextBody;
        }
        else if (constraintCurrent.kind == "circle")
        {
            lineConstraint      = constraintNext;
            circleConstraint    = constraintCurrent;
            rolledBody          = currentBody;
            adjacentBodyForRoot = nextBody;
        }
        else
        {
            lineConstraint      = constraintCurrent;
            circleConstraint    = constraintNext;
            rolledBody          = nextBody;
            adjacentBodyForRoot = currentBody;
        }

        // Line-circle intersection.
        // Signed distance d from circle center to the line; two candidate roots offset
        // along the line by ±t where t = sqrt(r^2 - d^2).
        const nx = lineConstraint.nx;
        const ny = lineConstraint.ny;
        const b  = lineConstraint.rhs;
        const cx = circleConstraint.cx;
        const cy = circleConstraint.cy;
        const r  = circleConstraint.radius;
        const d  = nx * cx + ny * cy - b;
        const discriminant = r ^ 2 - d ^ 2;
        const t  = (discriminant > 0) ? sqrt(discriminant) : 0;

        // Foot of perpendicular from circle center to line, then offset along the line.
        const fx = cx - nx * d;
        const fy = cy - ny * d;
        const x1 = fx + ny * t;
        const y1 = fy - nx * t;
        const x2 = fx - ny * t;
        const y2 = fy + nx * t;

        // Pick the root nearest to the junction cap face of the rolled member.
        // Cap face centroids reliably identify the junction end without consulting
        // boundary edge evaluation order.
        const adjacentCentroid    = evApproximateCentroid(context, { "entities" : adjacentBodyForRoot });
        const startCapCentroid    = evApproximateCentroid(context, { "entities" : qFrameStartFace(rolledBody) });
        const endCapCentroid      = evApproximateCentroid(context, { "entities" : qFrameEndFace(rolledBody) });
        const distToStart         = norm(startCapCentroid - adjacentCentroid);
        const distToEnd           = norm(endCapCentroid   - adjacentCentroid);
        const junctionCapCentroid = (distToStart <= distToEnd) ? startCapCentroid : endCapCentroid;

        const junctionLocal = fromWorld(panelCSys, junctionCapCentroid);
        const jx = junctionLocal[0] / meter;
        const jy = junctionLocal[1] / meter;

        const dist1 = (x1 - jx) ^ 2 + (y1 - jy) ^ 2;
        const dist2 = (x2 - jx) ^ 2 + (y2 - jy) ^ 2;
        cornerX = (dist1 <= dist2) ? x1 : x2;
        cornerY = (dist1 <= dist2) ? y1 : y2;
    }

    return toWorld(panelCSys, vector(cornerX * meter, cornerY * meter, alignmentZ));
}

/**
 * Computes a 2D constraint for a frame member's boundary in the panel coordinate plane,
 * representing the inner surface of the member at the corner where it meets adjacentBody.
 *
 * Two constraint types are returned:
 *
 *   "line"   : { "kind", "nx", "ny", "rhs" }
 *              Normal-form line equation  nx*x + ny*y = rhs  (all dimensionless, in meters).
 *              Used for all members except rolled round tubes (torus swept face).
 *              The constraint comes from the inner face of the member's bounding box in a
 *              local coordinate system aligned with the member's sweep direction at the
 *              junction end:
 *                - Local X = sweep direction (along member, at the junction end)
 *                - Local Y = cross(openingNormal, sweepDir)  (perp to sweep, in panel plane)
 *                - Local Z = openingNormal
 *              The bounding box Y face closest to the panel centroid is the inner face.
 *              This approach is robust to hollow profiles (box tube, round tube) because
 *              evBox3d captures the total body extent — the outer surface — regardless of
 *              internal geometry or which individual swept face is selected.
 *
 *   "circle" : { "kind", "cx", "cy", "radius" }
 *              Circle  (x-cx)^2 + (y-cy)^2 = radius^2  (all dimensionless, in meters).
 *              Used for rolled round tube members (torus swept face).
 *              The outer torus face is identified as the one with the largest major radius,
 *              which correctly selects the outer surface of a hollow round tube over the
 *              inner hollow surface. Center = outer torus axis projected to panel XY.
 *              Radius = torus.radius - torus.minorRadius (inner boundary arc).
 *
 * @param context       {Context}     : The active feature context.
 * @param memberBody    {Query}       : The frame member body.
 * @param adjacentBody  {Query}       : The adjacent frame member at this corner.
 * @param openingNormal {Vector}      : Panel normal unit vector (dimensionless).
 * @param panelCentroid {Vector}      : World-space centroid of the frame ring (with units).
 * @param panelCSys     {CoordSystem} : Panel coordinate system (Z = panel normal, origin = centroid).
 * @returns {map} : Constraint map with "kind" key equal to "line" or "circle".
 */
function computeConstraint2D(context is Context, memberBody is Query, adjacentBody is Query,
        openingNormal is Vector, panelCentroid is Vector, panelCSys is CoordSystem) returns map
{
    // ── Torus path: rolled round tube ─────────────────────────────────────────────────────
    // For a round tube swept along an arc, the swept face is a torus. The panel boundary is
    // the inner arc of the outer torus face at radius = major_radius - tube_radius.
    // The outer torus face has the largest major radius; for a hollow tube this correctly
    // selects the outer surface and not the inner bore face.
    const sweptFaces    = qFrameSweptFace(memberBody);
    const torusFaceList = evaluateQuery(context, qGeometry(sweptFaces, GeometryType.TORUS));
    if (size(torusFaceList) > 0)
    {
        var outerTorusFace       = torusFaceList[0];
        var outerTorusDefinition = evSurfaceDefinition(context, { "face" : torusFaceList[0] });
        var outerTorusRadius     = outerTorusDefinition.radius;
        for (var torusIndex = 1; torusIndex < size(torusFaceList); torusIndex += 1)
        {
            const candidateDefinition = evSurfaceDefinition(context, { "face" : torusFaceList[torusIndex] });
            if (candidateDefinition.radius > outerTorusRadius)
            {
                outerTorusFace       = torusFaceList[torusIndex];
                outerTorusDefinition = candidateDefinition;
                outerTorusRadius     = candidateDefinition.radius;
            }
        }

        const torusCenterLocal = fromWorld(panelCSys, outerTorusDefinition.coordSystem.origin);
        return {
            "kind"   : "circle",
            "cx"     : torusCenterLocal[0] / meter,
            "cy"     : torusCenterLocal[1] / meter,
            "radius" : (outerTorusDefinition.radius - outerTorusDefinition.minorRadius) / meter
        };
    }

    // ── Bounding box path: all other members ──────────────────────────────────────────────
    // Build a local coordinate system aligned with the member's sweep direction at the
    // junction end (the end of the member closest to adjacentBody). The bounding box in this
    // CSys has correct Y extents for any cross-section profile.
    //
    // Cap face centroids identify the junction end reliably. qFrameStartFace and
    // qFrameEndFace are more robust than edge evaluation order for determining which end
    // of the member is closest to the adjacent member.
    const memberCentroid   = evApproximateCentroid(context, { "entities" : memberBody });
    const adjacentCentroid = evApproximateCentroid(context, { "entities" : adjacentBody });
    const startCapCentroid = evApproximateCentroid(context, { "entities" : qFrameStartFace(memberBody) });
    const endCapCentroid   = evApproximateCentroid(context, { "entities" : qFrameEndFace(memberBody) });

    const distToStart          = norm(startCapCentroid - adjacentCentroid);
    const distToEnd            = norm(endCapCentroid   - adjacentCentroid);
    const cornerPathParameter  = (distToStart <= distToEnd) ? 0 : 1;
    const sweepDir             = getFrameSweepDirection(context, memberBody, cornerPathParameter);

    // Local coordinate system: X = sweep direction, Z = panel normal, Y = cross(Z, X).
    const localYAxis = normalize(cross(openingNormal, sweepDir));
    const localCSys  = coordSystem(memberCentroid, sweepDir, openingNormal);

    // Bounding box of the member in the local CSys.
    // The corners are in local coordinates relative to memberCentroid (the localCSys origin).
    const memberBox = evBox3d(context, {
                "topology" : memberBody,
                "cSys"     : localCSys,
                "tight"    : true
            });

    // The inner Y face is the bounding box side that faces toward the panel centroid.
    // panelLocalY is the signed Y displacement of the panel centroid from the member centroid
    // along localYAxis. Positive → panel centroid is on the +Y side → inner face at max_y.
    const panelLocalY = dot(panelCentroid - memberCentroid, localYAxis);
    const innerY      = (panelLocalY > 0 * meter) ? memberBox.maxCorner[1] : memberBox.minCorner[1];

    // World-space point on the constraint line: the inner Y face at the member centroid position.
    const constraintOriginWorld = memberCentroid + innerY * localYAxis;

    // Express as nx*x + ny*y = rhs in panel coordinates (all dimensionless, in meters).
    const constraintOriginLocal = fromWorld(panelCSys, constraintOriginWorld);
    const nx  = dot(localYAxis, panelCSys.xAxis);
    const ny  = dot(localYAxis, yAxis(panelCSys));
    const ox  = constraintOriginLocal[0] / meter;
    const oy  = constraintOriginLocal[1] / meter;
    return {
        "kind" : "line",
        "nx"   : nx,
        "ny"   : ny,
        "rhs"  : nx * ox + ny * oy
    };
}

/**
 * Manipulator change function for panelMakerFeature.
 *
 * @param context {Context} : The active feature context.
 * @param definition {map} : The current feature definition.
 * @param newManipulators {map} : Map of manipulator keys to their updated state.
 * @returns {map} : The updated feature definition.
 */
export function panelMakerManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    const newManipulator = newManipulators["alignmentManipulator"];
    if (newManipulator != undefined)
    {
        definition.alignmentIndex = newManipulator.index;
    }
    return definition;
}
