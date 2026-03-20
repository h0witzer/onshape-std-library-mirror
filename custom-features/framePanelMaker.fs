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
 *   2. Extract sweep tangent directions (used for panel normal computation only) via the
 *      Cut List strategy: straight swept edges, cylindrical faces, toroidal faces, or arc
 *      swept edges in that priority order.
 *   3. Compute panel normal N = normalize(cross(dir_i, dir_j)) from the first two
 *      non-parallel sweep directions.
 *   4. Validate coplanarity: every sweep direction in the sweepDirections array (already
 *      computed in step 2 from swept edges/faces, no cap faces) must be perpendicular to
 *      openingNormal. Uses perpendicularVectors() from the standard library — the same
 *      tolerance as parallelVectors() used in step 3.
 *   5. Build the panel coordinate system (Z = N, X = first sweep direction). Use evBox3d
 *      in that system to derive the three alignment positions from the frame depth.
 *   6. Sort the selected members into a closed loop via a body-adjacency graph and a
 *      Hamiltonian cycle search.
 *   7. For each consecutive pair in the loop, find the inner swept face with
 *      findInnerSweptFace(). This selects the face whose panel-plane intersection curve
 *      correctly constrains the panel opening for any profile — including L-channels, where
 *      the groove wall face (not the outer leg tip) must be used. Get the 2D constraint via
 *      getInnerFaceConstraint2D and solve for the corner position.
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

        // ── 2. Extract sweep directions using the Cut List strategy ─────────────────────
        //
        // Mirrors the direction evaluation used by the Onshape Cut List feature:
        //   1. Straight swept edges (rectangular tube, I-beam, channel): evEdgeTangentLine
        //      on the first straight swept edge gives the exact sweep axis.
        //   2. Cylindrical swept faces (round tube, pipe with no swept edges):
        //      evSurfaceDefinition on the first cylindrical swept face; its coordSystem.zAxis
        //      is the cylinder axis and sweep direction.
        //   3. Arc or other swept edges (curved members): evEdgeTangentLine at parameter 0
        //      gives the sweep tangent at the start of the arc.
        //
        // Cap face normals are intentionally NOT used here. A mitered cap face has a normal
        // angled to the actual sweep path, producing wrong directions for planarity checks.
        //
        // Two samples per member (path parameters 0 and 1) ensure that curved members have
        // both arc endpoints validated for planarity, not just one end.

        var sweepDirections = makeArray(memberCount * 2);
        var sweepDirectionCount = 0;

        for (var memberIndex = 0; memberIndex < memberCount; memberIndex += 1)
        {
            sweepDirections[sweepDirectionCount]     = getFrameSweepDirection(context, frameMemberBodies[memberIndex], 0);
            sweepDirections[sweepDirectionCount + 1] = getFrameSweepDirection(context, frameMemberBodies[memberIndex], 1);
            sweepDirectionCount += 2;
        }

        // ── 3. Compute panel normal N ───────────────────────────────────────────────────
        //
        // Find the first pair of sweep directions that are not parallel to each other.
        // parallelVectors() from the standard library handles the tolerance comparison.

        var openingNormal = vector(0, 0, 1);
        var panelNormalFound = false;

        for (var outerIndex = 0; outerIndex < sweepDirectionCount && !panelNormalFound; outerIndex += 1)
        {
            for (var innerIndex = outerIndex + 1; innerIndex < sweepDirectionCount && !panelNormalFound; innerIndex += 1)
            {
                if (!parallelVectors(sweepDirections[outerIndex], sweepDirections[innerIndex]))
                {
                    openingNormal = normalize(cross(sweepDirections[outerIndex], sweepDirections[innerIndex]));
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

        // ── 4. Validate coplanarity using the sweep directions already computed ──────────────
        //
        // A frame ring is coplanar when every member's sweep path lies in a single common
        // plane. That plane has already been characterised by openingNormal (step 3). The
        // membership condition is exactly: every sweep direction must be perpendicular to
        // openingNormal (i.e. the direction must lie IN the panel plane).
        //
        // Why sweep directions and NOT swept-face surface geometry:
        //   Inspecting surface types introduces a dependency on the profile shape. A
        //   straight round tube produces a cylindrical swept face whose axis is the sweep
        //   direction (perpendicular to openingNormal), while an arc-swept round tube
        //   produces a toroidal swept face whose axis equals openingNormal. Both cases are
        //   valid coplanar rings, but the correct axis orientation is opposite in the two
        //   cases — so any single axis check either passes one and blocks the other, or
        //   passes both without actually validating coplanarity.
        //
        //   The sweep directions themselves carry exactly the information we need and are
        //   already available in the sweepDirections array computed in step 2.  No
        //   additional geometry queries are required, and no cap faces are involved.
        //
        // Why perpendicularVectors and NOT a manual dot product:
        //   perpendicularVectors() is a standard library function (math.fs) that encapsulates
        //   the angular tolerance consistently with every other directional comparison in
        //   the Onshape Standard Library. Using a manual dot product would require choosing
        //   an arbitrary threshold and risk inconsistency with the tolerance used in step 3
        //   (parallelVectors) to find the normal in the first place.

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

        // ── 5. Build panel coordinate system ────────────────────────────────────────────
        //
        // Origin: centroid of all member body centroids (approximate center of the frame ring).
        // Z axis: openingNormal (the panel normal, perpendicular to all sweep directions).
        // X axis: first sweep direction (guaranteed in the panel plane by the planarity check).
        // Y axis: cross(Z, X) for a right-handed system.

        var panelCentroid = vector(0, 0, 0) * meter;
        for (var memberIndex = 0; memberIndex < memberCount; memberIndex += 1)
        {
            panelCentroid = panelCentroid + evApproximateCentroid(context, {
                        "entities" : frameMemberBodies[memberIndex]
                    });
        }
        panelCentroid = panelCentroid / memberCount;

        const panelXAxis = normalize(sweepDirections[0]);
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

        // ── 9. Compute panel boundary corners from inner swept faces ─────────────────────
        //
        // For each consecutive pair (A, B) in the loop, the panel corner is the intersection
        // of their inner swept face constraint lines/curves in panel XY at Z = alignmentZ.
        //
        // findInnerSweptFace() replaces the previous qClosestTo(qFrameSweptFace, panelCentroid)
        // approach, which incorrectly selected the outer leg-tip face of an L-channel member
        // (the tip of the horizontal leg is geometrically closer to the panel centroid than
        // the groove wall) instead of the groove wall that actually constrains the panel void.
        // findInnerSweptFace() scores by void-direction alignment of the projected face normal
        // and breaks ties by minimum signed offset (rhs), picking the innermost face.

        var cornerPoints = makeArray(memberCount);
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const currentMemberBody = frameMemberBodies[loopOrder[loopStep]];
            const nextMemberBody    = frameMemberBodies[loopOrder[(loopStep + 1) % memberCount]];

            const innerFaceCurrent = findInnerSweptFace(context, currentMemberBody, panelCentroid, openingNormal);
            const innerFaceNext    = findInnerSweptFace(context, nextMemberBody,    panelCentroid, openingNormal);

            cornerPoints[loopStep] = computeCornerPoint(context,
                    innerFaceCurrent, currentMemberBody,
                    innerFaceNext,    nextMemberBody,
                    panelCSys, alignmentZ);
        }

        // ── DEBUG: Alignment and boundary construction geometry ────────────────────────────
        //
        // Diagnostic visualization to help identify panel misalignment and "twisties".
        // Remove this block when the feature is working correctly.
        //
        // Color key:
        //   RED/GREEN/BLUE : Panel coordinate system — X = first sweep direction (RED),
        //                    Y (GREEN), Z = opening normal (BLUE).
        //                    If the panel is twisted, check whether RED is pointing in an
        //                    unexpected direction; sweepDirections[0] drives panelXAxis and
        //                    a sign flip here will mirror the constraint RHS values.
        //   YELLOW         : Panel centroid (coordinate system origin).
        //   CYAN           : Inner swept face per member. The inner face determines the
        //                    boundary constraint — if the wrong wall of the profile is
        //                    CYAN, the corner will be computed from the wrong face.
        //   BLACK          : Alignment front point (YELLOW = center, ORANGE = back).
        //                    The panel sits at YELLOW for alignmentIndex 1 (centered).
        //   ORANGE         : Alignment back point; also void-direction arrows.
        //                    Each arrow runs from a member centroid to the panel centroid
        //                    projected onto the panel plane.  findInnerSweptFace ranks
        //                    candidate faces by alignment with this direction — a wrong
        //                    arrow means a wrong face score.
        //   MAGENTA        : Per-member body centroids (void-direction arrow tails).
        //   GREEN          : Constraint lines in the panel plane at Z = alignmentZ.
        //                    One line per loop step.  Each pair of adjacent GREEN lines
        //                    should intersect exactly at the adjacent RED corner point.
        //                    Circle constraints (rolled tube) appear as BLUE center points.
        //   RED (points)   : Computed corner vertices before polyline construction.

        // Panel coordinate system axes
        debug(context, panelCSys, DebugColor.RED, DebugColor.GREEN, DebugColor.BLUE);
        addDebugPoint(context, panelCentroid, DebugColor.YELLOW);

        // Alignment depth reference points
        addDebugPoint(context, alignmentPointFront,  DebugColor.BLACK);
        addDebugPoint(context, alignmentPointCenter, DebugColor.YELLOW);
        addDebugPoint(context, alignmentPointBack,   DebugColor.ORANGE);

        // Per-member: centroid (MAGENTA), void-direction arrow (ORANGE), inner face (CYAN)
        for (var debugMemberIndex = 0; debugMemberIndex < memberCount; debugMemberIndex += 1)
        {
            const debugMemberBody     = frameMemberBodies[debugMemberIndex];
            const debugMemberCentroid = evApproximateCentroid(context, { "entities" : debugMemberBody });
            addDebugPoint(context, debugMemberCentroid, DebugColor.MAGENTA);

            // Void direction: panel-plane projection of (panelCentroid - memberCentroid).
            // findInnerSweptFace scores candidate faces by alignment with this direction.
            const voidDelta   = panelCentroid - debugMemberCentroid;
            const voidInPlane = voidDelta - dot(voidDelta, openingNormal) * openingNormal;
            if (norm(voidInPlane) > 1e-6 * meter)
            {
                addDebugArrow(context, debugMemberCentroid, debugMemberCentroid + voidInPlane,
                        1.5 * millimeter, DebugColor.ORANGE);
            }

            // Inner swept face: the boundary constraint face selected for this member
            addDebugEntities(context,
                    findInnerSweptFace(context, debugMemberBody, panelCentroid, openingNormal),
                    DebugColor.CYAN);
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
            const debugInnerFace   = findInnerSweptFace(context, debugCurrentBody, panelCentroid, openingNormal);
            const debugConstraint  = getInnerFaceConstraint2D(context, debugInnerFace, debugCurrentBody, debugNextBody, panelCSys);

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
 * 1 = end). Mirrors the direction evaluation strategy used by the Onshape Cut List feature:
 *
 *   1. Straight swept edges (rectangular tube, I-beam, channel, angle iron):
 *      evEdgeTangentLine on the first LINE-geometry swept edge. Returns the exact sweep axis
 *      regardless of how the ends are cut (perpendicular, mitered, compound).
 *   2. Cylindrical swept faces (round tube / pipe with no swept edges):
 *      evSurfaceDefinition on the first CYLINDER-geometry swept face; coordSystem.zAxis is
 *      the cylinder axis = the sweep direction (same at all parameters for a straight sweep).
 *   3. Arc or other curved swept edges (arc-swept curved member):
 *      evEdgeTangentLine on the first available swept edge at the requested parameter.
 *      This gives the arc tangent at that point, which lies in the panel plane for any
 *      frame member whose sweep path is a planar arc.
 *
 * @param context        {Context} : The active feature context.
 * @param memberBody     {Query}   : A single frame member solid body.
 * @param pathParameter  {number}  : Path parameter in [0, 1]; 0 = start end, 1 = end end.
 * @returns {Vector} : Unit vector along the sweep direction at the requested parameter.
 */
function getFrameSweepDirection(context is Context, memberBody is Query, pathParameter is number) returns Vector
{
    // Strategy 1: straight swept edge — most common for profiled sections.
    const sweptEdges = qFrameSweptEdge(memberBody);
    const straightEdges = evaluateQuery(context, qGeometry(sweptEdges, GeometryType.LINE));
    if (size(straightEdges) > 0)
    {
        return evEdgeTangentLine(context, {
                    "edge"      : straightEdges[0],
                    "parameter" : pathParameter
                }).direction;
    }

    // Strategy 2: cylindrical swept face axis — used for straight round tubes and pipes.
    const sweptFaces = qFrameSweptFace(memberBody);
    const cylinderFaces = evaluateQuery(context, qGeometry(sweptFaces, GeometryType.CYLINDER));
    if (size(cylinderFaces) > 0)
    {
        return evSurfaceDefinition(context, { "face" : cylinderFaces[0] }).coordSystem.zAxis;
    }

    // Strategy 2b: toroidal swept face — used for rolled (arc-swept) round tube members.
    //
    // When a round tube is swept along an arc, its swept face is a torus rather than a
    // cylinder, so Strategy 2 finds no faces. The sweep tangent at the requested end is
    // cross(torusAxis, radialDirection), where radialDirection is the in-plane vector from
    // the torus center to a point on the torus boundary edge at that end of the arc.
    //
    // The torus face has exactly two boundary circle edges — one at each end of the arc.
    // Evaluating the midpoint (parameter 0.5) of each boundary circle gives a point at the
    // correct angular position without consulting cap faces. Cap faces are not used here
    // because mitered or compound-cut caps have arbitrary orientations and may not exist.
    const torusFaces = evaluateQuery(context, qGeometry(sweptFaces, GeometryType.TORUS));
    if (size(torusFaces) > 0)
    {
        const torusDefinition    = evSurfaceDefinition(context, { "face" : torusFaces[0] });
        const torusCenter        = torusDefinition.coordSystem.origin;
        const torusAxis          = torusDefinition.coordSystem.zAxis;

        const torusBoundaryEdges = evaluateQuery(context,
                qAdjacent(torusFaces[0], AdjacencyType.EDGE, EntityType.EDGE));
        const boundaryEdgeIndex  = (pathParameter < 0.5) ? 0 : (size(torusBoundaryEdges) - 1);
        const referencePoint     = evEdgeTangentLine(context, {
                    "edge"      : torusBoundaryEdges[boundaryEdgeIndex],
                    "parameter" : 0.5
                }).origin;

        // Project the radial vector into the torus equatorial (sweep) plane.
        const radialVec      = referencePoint - torusCenter;
        const axialComponent = dot(radialVec, torusAxis);
        const radialInPlane  = radialVec - axialComponent * torusAxis;
        return normalize(cross(torusAxis, radialInPlane / meter));
    }

    // Strategy 3: arc or general swept edge — used for curved (arc-swept) members.
    const allSweptEdges = evaluateQuery(context, sweptEdges);
    if (size(allSweptEdges) > 0)
    {
        return evEdgeTangentLine(context, {
                    "edge"      : allSweptEdges[0],
                    "parameter" : pathParameter
                }).direction;
    }

    // Fallback: should not be reached for valid frame members created by the Frame feature.
    reportFeatureError(context, makeId("panelMakerFeature"),
        "Could not determine sweep direction for a selected frame member.");
    return vector(1, 0, 0);
}

/**
 * Selects the swept face of a frame member that forms the panel boundary on this member's
 * side of the frame opening.
 *
 * This replaces qClosestTo(qFrameSweptFace, panelCentroid), which selects the face whose
 * surface geometry is nearest to the panel-ring centroid. For non-convex profiles such as
 * L-channels, the outer tip of the horizontal leg is geometrically nearer to the panel
 * centroid than the inner groove wall, causing the wrong face to be selected and the panel
 * boundary to terminate at the outer leg extent rather than the groove wall.
 *
 * Algorithm:
 *   1. For each planar swept face: skip faces whose normals are approximately parallel to
 *      the panel normal — they are parallel to the panel plane and their intersection with
 *      the panel plane is degenerate (the step face of an L-channel is this type of face).
 *   2. Score each remaining planar face by dot(projectedNormal, voidDirection), where
 *      voidDirection is the panel-plane projection of (panelCentroid - memberCentroid).
 *      Faces aligned with the void direction score near +1; faces opposing it score near -1.
 *   3. Among faces with equal top score, break ties by selecting the face with the smallest
 *      signed offset (rhs = dot(normal, origin) / meter). A smaller rhs means the face's
 *      plane is closer to the member's material centre in the void direction — i.e., the
 *      innermost constraining face. For an L-channel, the groove wall (rhs ≈ 2mm) beats the
 *      outer leg tip (rhs ≈ 10mm) via this tiebreaker.
 *   4. Non-planar faces (cylindrical, toroidal, etc.) are used as a fallback if no planar
 *      face with a positive void-direction score is found.
 *
 * @param context       {Context} : The active feature context.
 * @param memberBody    {Query}   : The frame member body to search.
 * @param panelCentroid {Vector}  : World-space centroid of the frame ring (with units).
 * @param openingNormal {Vector}  : Panel normal unit vector (dimensionless).
 * @returns {Query} : The swept face to use for the panel boundary constraint.
 */
function findInnerSweptFace(context is Context, memberBody is Query, panelCentroid is Vector, openingNormal is Vector) returns Query
{
    const memberCentroid = evApproximateCentroid(context, { "entities" : memberBody });

    // Void direction: direction from member centroid toward panel interior, projected to
    // the panel XY plane (panel normal component removed).
    const rawVoidVector = (panelCentroid - memberCentroid) -
                          dot(panelCentroid - memberCentroid, openingNormal) * openingNormal;
    const voidMagnitude  = norm(rawVoidVector);
    // Fallback when the member centroid coincides with the panel centroid: pick any vector
    // perpendicular to openingNormal. Guard against the degenerate case where openingNormal
    // is parallel to (1,0,0), which would make cross(openingNormal,(1,0,0)) a zero vector.
    const fallbackPerpendicular = (abs(dot(openingNormal, vector(1, 0, 0))) < 0.9) ?
                                  vector(1, 0, 0) : vector(0, 1, 0);
    const voidDirection  = (voidMagnitude > 1e-8 * meter) ?
                           rawVoidVector / voidMagnitude :
                           normalize(cross(openingNormal, fallbackPerpendicular));

    var bestFace  = qNothing();
    var bestScore = -2.0;
    var bestRhs   = 1e30;

    for (var sweptFace in evaluateQuery(context, qFrameSweptFace(memberBody)))
    {
        const surfaceDefinition = evSurfaceDefinition(context, { "face" : sweptFace });

        if (surfaceDefinition is Plane)
        {
            // Skip faces that are parallel (or nearly parallel) to the panel plane.
            // Their projected normal in the panel XY plane is near zero, giving no useful
            // intersection line with the panel mid-plane.
            if (abs(dot(surfaceDefinition.normal, openingNormal)) > 0.99)
            {
                continue;
            }

            // Project the face normal into the panel XY plane and normalise.
            const normalInPanel = surfaceDefinition.normal -
                                  dot(surfaceDefinition.normal, openingNormal) * openingNormal;
            const normalMagnitude = norm(normalInPanel);
            if (normalMagnitude < 1e-6)
            {
                continue;
            }
            const unitNormal = normalInPanel / normalMagnitude;
            const score      = dot(unitNormal, voidDirection);

            // rhs: signed offset of this face's plane from the world origin (dimensionless
            // after dividing by meter). Among faces with the same projected-normal direction,
            // the face with the smallest rhs is the innermost — it constrains the panel most.
            const rhs = dot(surfaceDefinition.normal, surfaceDefinition.origin) / meter;

            if (score > bestScore + 1e-6)
            {
                bestScore = score;
                bestRhs   = rhs;
                bestFace  = sweptFace;
            }
            else if (score > bestScore - 1e-6 && rhs < bestRhs)
            {
                bestRhs  = rhs;
                bestFace = sweptFace;
            }
        }
        else
        {
            // Non-planar face (cylindrical, toroidal, etc.): use as a fallback only when no
            // planar face facing the void has been found yet.
            if (bestScore <= 0.0)
            {
                bestFace = sweptFace;
            }
        }
    }

    // Final fallback: if nothing was selected (empty swept face set), use the original
    // closest-to-centroid approach so the feature degrades gracefully.
    if (isQueryEmpty(context, bestFace))
    {
        bestFace = qClosestTo(qFrameSweptFace(memberBody), panelCentroid);
    }

    return bestFace;
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
 * Returns the tangent plane of an inner swept face at the boundary edge where that face
 * meets the cap face closest to the specified adjacent member.
 *
 * This determines which end of the inner face is the "corner end" for the panel boundary
 * computation using only standard library queries and evaluation functions:
 *   1. qClosestTo on the two cap faces of the member selects the one nearest to the
 *      adjacent member's centroid — no manual distance comparison needed.
 *   2. qAdjacent + qIntersection finds the edge shared between the inner swept face
 *      and that nearest cap face — the exact geometric corner of this face end.
 *   3. evFaceTangentPlaneAtEdge gives the tangent plane of the inner face along that
 *      corner edge, which for planar faces equals evPlane and for curved faces gives
 *      the local tangent at the correct arc end.
 *
 * @param context       {Context} : The active feature context.
 * @param innerFace     {Query}   : The inner swept face of the member at this corner.
 * @param memberBody    {Query}   : The frame member body that owns innerFace.
 * @param adjacentBody  {Query}   : The adjacent frame member at this corner.
 * @returns {Plane} : Tangent plane of innerFace at the end edge closest to adjacentBody.
 */
function getInnerFaceCornerPlane(context is Context, innerFace is Query, memberBody is Query, adjacentBody is Query) returns Plane
{
    const adjacentBodyCentroid = evApproximateCentroid(context, { "entities" : adjacentBody });

    // The inner swept face has two types of edges:
    //   - Longitudinal swept-path edges: run along the length of the member (in qFrameSweptEdge).
    //   - End edges: lie across the cross-section at each end of the sweep; these are the
    //     intersection of the swept face with the end of the member.
    //
    // Subtracting the longitudinal swept edges from the face's edge set leaves only the
    // end edges. Among those, qClosestTo selects the end edge nearest to the adjacent
    // member — the correct junction end — without consulting cap faces.
    //
    // Cap faces (qFrameStartFace / qFrameEndFace) are intentionally not used here:
    // they may be mitered, compound-cut, or otherwise absent, giving unreliable geometry.
    const cornerEdge = qClosestTo(
        qSubtraction(
            qAdjacent(innerFace, AdjacencyType.EDGE, EntityType.EDGE),
            qFrameSweptEdge(memberBody)
        ),
        adjacentBodyCentroid
    );

    return evFaceTangentPlaneAtEdge(context, {
                "face"      : innerFace,
                "edge"      : cornerEdge,
                "parameter" : 0.5
            });
}

/**
 * Computes the world-space corner point at the junction of two adjacent frame members,
 * placed at the chosen panel alignment depth.
 *
 * Method:
 *   1. Derive a 2D constraint for each member's inner swept face in the panel XY plane
 *      via getInnerFaceConstraint2D:
 *        - Planar/cylindrical face → line constraint from the tangent plane at the corner edge.
 *        - Toroidal face (rolled round tube) → circle constraint from the torus inner arc.
 *   2. Intersect the two constraints (line-line, line-circle, or circle-circle).
 *      Circle-circle is reduced to line-circle via the radical axis.
 *   3. Reconstruct the world-space corner with toWorld at Z = alignmentZ.
 *
 * For straight (planar/cylindrical) inner faces, the constraint is a line derived from the
 * tangent plane at the corner edge — the same approach used previously.
 * For toroidal inner faces (rolled round tube members), the constraint is a circle: the arc
 * at radius (torus.radius - torus.minorRadius) from the torus axis projected to the panel
 * plane. Line-circle and circle-circle intersections are solved analytically. The correct
 * root (of the two intersection candidates) is chosen by proximity to the cap face centroid
 * at the junction end of the rolled member.
 *
 * @param context           {Context}        : The active feature context.
 * @param innerFaceCurrent  {Query}          : Inner swept face of the current member.
 * @param currentBody       {Query}          : Current frame member body.
 * @param innerFaceNext     {Query}          : Inner swept face of the next member.
 * @param nextBody          {Query}          : Next frame member body.
 * @param panelCSys         {CoordSystem}    : Panel coordinate system (Z = normal, origin = centroid).
 * @param alignmentZ        {ValueWithUnits} : Alignment depth along the panel normal from the panel origin.
 * @returns {Vector} : World-space corner position with meter units.
 */
function computeCornerPoint(context is Context, innerFaceCurrent is Query, currentBody is Query, innerFaceNext is Query, nextBody is Query, panelCSys is CoordSystem, alignmentZ is ValueWithUnits) returns Vector
{
    const constraintCurrent = getInnerFaceConstraint2D(context, innerFaceCurrent, currentBody, nextBody,    panelCSys);
    const constraintNext    = getInnerFaceConstraint2D(context, innerFaceNext,    nextBody,    currentBody, panelCSys);

    var cornerX is number = 0;
    var cornerY is number = 0;

    if (constraintCurrent.kind == "line" && constraintNext.kind == "line")
    {
        // Two straight (or planar) members: solve the 2x2 linear system
        //   nx1*x + ny1*y = rhs1
        //   nx2*x + ny2*y = rhs2
        const det = constraintCurrent.nx * constraintNext.ny - constraintCurrent.ny * constraintNext.nx;

        if (parallelVectors(vector(constraintCurrent.nx, constraintCurrent.ny, 0),
                            vector(constraintNext.nx,    constraintNext.ny,    0)))
        {
            // Parallel faces (T-junction or collinear): midpoint of the two foot-of-perpendicular projections.
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
        // At least one rolled member: handle line-circle or circle-circle.
        //
        // For circle-circle, subtract the two circle equations to obtain the radical axis
        // (a line), then reduce to the line-circle case.

        var lineConstraint  = { "nx" : 0, "ny" : 1, "rhs" : 0 };
        var circleConstraint = constraintCurrent;
        var rolledBody  = currentBody;
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
            lineConstraint   = { "nx" : dx / len, "ny" : dy / len, "rhs" : radicalRhs };
            circleConstraint = constraintCurrent;
            rolledBody       = currentBody;
            adjacentBodyForRoot = nextBody;
        }
        else if (constraintCurrent.kind == "circle")
        {
            lineConstraint   = constraintNext;
            circleConstraint = constraintCurrent;
            rolledBody       = currentBody;
            adjacentBodyForRoot = nextBody;
        }
        else
        {
            lineConstraint   = constraintCurrent;
            circleConstraint = constraintNext;
            rolledBody       = nextBody;
            adjacentBodyForRoot = currentBody;
        }

        // Line-circle intersection.
        // Signed distance from circle center to the line (positive on the normal side):
        const nx = lineConstraint.nx;
        const ny = lineConstraint.ny;
        const b  = lineConstraint.rhs;
        const cx = circleConstraint.cx;
        const cy = circleConstraint.cy;
        const r  = circleConstraint.radius;
        const d  = nx * cx + ny * cy - b;
        const discriminant = r ^ 2 - d ^ 2;
        const t  = (discriminant > 0) ? sqrt(discriminant) : 0;

        // Foot of perpendicular from circle center to line, then offset along the line direction (ny, -nx).
        const fx = cx - nx * d;
        const fy = cy - ny * d;
        const x1 = fx + ny * t;
        const y1 = fy - nx * t;
        const x2 = fx - ny * t;
        const y2 = fy + nx * t;

        // Pick the root nearest to the junction end of the rolled member.
        // The torus face has two boundary circle edges, one at each end of the arc.
        // Evaluate the midpoint of each boundary circle and pick the one closer to the
        // adjacent member centroid — no cap faces consulted.
        const adjacentCentroid       = evApproximateCentroid(context, { "entities" : adjacentBodyForRoot });
        const torusBoundaryEdges     = evaluateQuery(context,
                qAdjacent(
                    qGeometry(qFrameSweptFace(rolledBody), GeometryType.TORUS),
                    AdjacencyType.EDGE,
                    EntityType.EDGE));
        const torusEdge0Sample       = evEdgeTangentLine(context, {
                    "edge"      : torusBoundaryEdges[0],
                    "parameter" : 0.5
                }).origin;
        const torusEdgeLastSample    = evEdgeTangentLine(context, {
                    "edge"      : torusBoundaryEdges[size(torusBoundaryEdges) - 1],
                    "parameter" : 0.5
                }).origin;
        const junctionCentroid       = (norm(torusEdge0Sample    - adjacentCentroid) <=
                                        norm(torusEdgeLastSample  - adjacentCentroid)) ?
                                       torusEdge0Sample : torusEdgeLastSample;
        const junctionLocal = fromWorld(panelCSys, junctionCentroid);
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
 * Returns a 2D constraint map for the inner swept face of a frame member at a panel corner,
 * expressed in the panel coordinate system. Two constraint types are possible:
 *
 *   "line"   : { "kind", "nx", "ny", "rhs" }
 *              Normal-form line equation  nx*x + ny*y = rhs.
 *              Used for planar and cylindrical (straight) inner faces.
 *              (nx, ny) is the face normal projected to the panel XY plane (unit vector).
 *              rhs is computed from the face origin projected by fromWorld.
 *
 *   "circle" : { "kind", "cx", "cy", "radius" }
 *              Circle  (x-cx)^2 + (y-cy)^2 = radius^2.
 *              Used for toroidal (rolled round tube) inner faces.
 *              Center = torus axis projected to panel XY by fromWorld.
 *              Radius = torus.radius - torus.minorRadius (inner boundary arc in the panel plane).
 *
 * All dimensionless values are expressed in meters.
 *
 * @param context       {Context}     : The active feature context.
 * @param innerFace     {Query}       : The inner swept face of the member.
 * @param memberBody    {Query}       : The frame member body owning innerFace.
 * @param adjacentBody  {Query}       : The adjacent frame member at this corner.
 * @param panelCSys     {CoordSystem} : Panel coordinate system (Z = panel normal, origin = centroid).
 * @returns {map} : Constraint map with "kind" key equal to "line" or "circle".
 */
function getInnerFaceConstraint2D(context is Context, innerFace is Query, memberBody is Query, adjacentBody is Query, panelCSys is CoordSystem) returns map
{
    const surfaceDefinition = evSurfaceDefinition(context, { "face" : innerFace });

    if (surfaceDefinition is Torus)
    {
        // Rolled member: the inner boundary arc in the panel plane is a circle.
        // Center = torus axis projected to panel XY via fromWorld.
        // Radius = torus major radius minus tube minor radius (the innermost arc).
        const torusCenterLocal = fromWorld(panelCSys, surfaceDefinition.coordSystem.origin);
        return {
            "kind"   : "circle",
            "cx"     : torusCenterLocal[0] / meter,
            "cy"     : torusCenterLocal[1] / meter,
            "radius" : (surfaceDefinition.radius - surfaceDefinition.minorRadius) / meter
        };
    }

    // Planar or cylindrical face: derive a line constraint from the corner tangent plane.
    // fromWorld projects the plane origin to panel local coordinates.
    // yAxis(panelCSys) provides the panel Y axis without manual cross-product recomputation.
    const cornerPlane = getInnerFaceCornerPlane(context, innerFace, memberBody, adjacentBody);
    const localOrigin = fromWorld(panelCSys, cornerPlane.origin);
    const nx          = dot(cornerPlane.normal, panelCSys.xAxis);
    const ny          = dot(cornerPlane.normal, yAxis(panelCSys));
    const ox          = localOrigin[0] / meter;
    const oy          = localOrigin[1] / meter;
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
