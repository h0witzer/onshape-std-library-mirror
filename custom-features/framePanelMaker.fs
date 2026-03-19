FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/frameAttributes.fs", version : "2909.0");
import(path : "onshape/std/frameUtils.fs", version : "2909.0");

// Conversion constants
const METERS_TO_MM = 1000;
const MM_PER_INCH = 25.4;
const METERS_TO_INCHES = METERS_TO_MM / MM_PER_INCH;

// Minimum cross-product magnitude used to verify that three joint positions are
// non-collinear (i.e., they define a valid plane). Value of 1e-6 corresponds to
// an angle deviation of less than 0.06 degrees from collinear.
const PERPENDICULARITY_TOLERANCE = 1e-6;

// Minimum frame members required to enclose a panel (triangle = 3 sides minimum).
const MINIMUM_FRAME_MEMBERS = 3;

// Number of decimal places kept when rounding computed panel dimensions in meters
// (3 places = 1 mm resolution).
const DIMENSION_ROUNDING_PRECISION = 3;

// Integer bound spec for the alignment manipulator index.
// 0 = panel flush with the +openingNormal face of the frames
// 1 = panel centered between the two parallel frame faces (default)
// 2 = panel flush with the -openingNormal face of the frames
const PANEL_ALIGNMENT_BOUND = { (unitless) : [0, 1, 2] } as IntegerBoundSpec;

// Maximum distance between the closest points of two consecutive frame members
// before a "not touching" warning is issued. 0.1 mm accounts for normal CAD tolerances.
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
 * dimensions (width and height of the tightest bounding rectangle in the panel plane).
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
 * Creates a solid panel body whose edges follow the intersection boundary between the
 * panel plane and each bordering frame member. The panel fills the void enclosed by the
 * closed loop of frame members, regardless of the number of members (3 or more), the
 * selection order, or the frame profile shape (box tube, round tube, L-channel, etc.).
 *
 * Algorithm overview:
 *   1. Sort the selected frame members into a connected closed loop by building a
 *      body-adjacency graph from pairwise evDistance calls and finding a Hamiltonian
 *      cycle. This correctly handles both end-to-end frame joints and T-intersections
 *      where spanning members extend beyond the panel opening.
 *   2. Compute the panel plane normal using Newell's method on the joint positions
 *      (midpoints of the closest-point pairs between consecutive bodies in the loop).
 *   3. Build a flat fill surface from a closed polyline through the joint contact
 *      points shifted to the chosen alignment position along the panel normal.
 *      The frame members are assumed to define a planar opening; non-planar frames
 *      are not supported.
 *   4. Refine the boundary by splitting the fill surface with the lateral (sweep)
 *      faces of each frame member. The interior fragment is the exact panel surface
 *      for any profile shape — box tube, round tube, C-channel, etc.
 *   5. Extract the interior fragment into an independent surface body and thicken it
 *      symmetrically to produce the solid panel body.
 *   6. Apply the edge gap AFTER thickening by offsetting only the non-cap faces of
 *      the thickened panel body inward by definition.gap. The cap faces (front and
 *      back) are identified explicitly via qNonCapEntity; only the perimeter walls
 *      are moved so the gap is uniform from the frame inner-face geometry.
 *
 * Alignment is controlled by a three-point manipulator along the opening normal:
 *   Index 0 - panel flush with the +openingNormal face of the frames
 *   Index 1 - panel centered between the two frame faces (default)
 *   Index 2 - panel flush with the -openingNormal face of the frames
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

        // ── 2. Sort members into a closed loop (graph-based body adjacency) ─────────────
        //
        // For each pair of selected bodies, evDistance determines whether they are
        // touching. A Hamiltonian cycle is then found in the resulting adjacency graph.
        // This approach correctly handles both traditional end-to-end frame joints and
        // T-intersections where a long spanning member extends beyond the panel opening —
        // proximity is determined from solid body geometry, not cap face centroid positions.

        const loopOrder = buildLoopFromBodyAdjacency(context, frameMemberBodies, JOINT_CONTACT_TOLERANCE);

        if (size(loopOrder) == 0)
        {
            reportFeatureError(context, id,
                "Selected frame members do not form a closed loop; verify that each member touches at least two others in the selection");
            return;
        }

        // ── 3. Compute joint positions and the panel plane ────────────────────────────
        //
        // Joint position i = midpoint of the cap face centroids of the two members
        // adjacent at loop position i.
        //
        // Cap face centroids (via qFrameStartFace / qFrameEndFace + evApproximateCentroid) are
        // used instead of the midpoint of an evDistance closest-point pair on the full
        // member bodies. evDistance on full bodies finds points on the outer surfaces of
        // the members. At different joints those outer-surface contact points can land on
        // different lateral faces of the cross-section (e.g. the +Y face at one corner
        // and the -Y face at another), making the joint polygon non-coplanar and
        // producing a twisted opFillSurface. Cap face centroids lie on the member axis
        // (the cross-section centroid) regardless of which outer surface happened to be
        // geometrically closest, so the resulting joint polygon is coplanar for any
        // planar frame.
        //
        // For each member, qClosestTo selects the cap face (start or end) nearest to
        // the other member's body centroid — this naturally picks the end cap at or
        // nearest to the shared joint for both corner joints and butt-style T-joints.
        var jointPositions = makeArray(memberCount);
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const currentMemberIdx = loopOrder[loopStep];
            const nextMemberIdx    = loopOrder[(loopStep + 1) % memberCount];

            // Contact check: evDistance on full bodies detects non-touching member pairs.
            const jointContactResult = evDistance(context, {
                        "side0" : frameMemberBodies[currentMemberIdx],
                        "side1" : frameMemberBodies[nextMemberIdx]
                    });

            if (jointContactResult.distance > JOINT_CONTACT_TOLERANCE)
            {
                reportFeatureWarning(context, id,
                    "Frame members at loop position " ~ toString(loopStep) ~
                    " do not touch; results may be incorrect");
            }

            const currentMemberCentroid = evApproximateCentroid(context, {
                        "entities" : frameMemberBodies[currentMemberIdx]
                    });
            const nextMemberCentroid = evApproximateCentroid(context, {
                        "entities" : frameMemberBodies[nextMemberIdx]
                    });

            const currentCapFaces = qUnion([
                        qFrameStartFace(frameMemberBodies[currentMemberIdx]),
                        qFrameEndFace(frameMemberBodies[currentMemberIdx])
                    ]);
            const nextCapFaces = qUnion([
                        qFrameStartFace(frameMemberBodies[nextMemberIdx]),
                        qFrameEndFace(frameMemberBodies[nextMemberIdx])
                    ]);

            const closestCapOfCurrent = qClosestTo(currentCapFaces, nextMemberCentroid);
            const closestCapOfNext    = qClosestTo(nextCapFaces, currentMemberCentroid);

            const capCentroidOfCurrent = evApproximateCentroid(context, { "entities" : closestCapOfCurrent });
            const capCentroidOfNext    = evApproximateCentroid(context, { "entities" : closestCapOfNext });

            jointPositions[loopStep] = (capCentroidOfCurrent + capCentroidOfNext) / 2;

            println("[DBG] jointPositions[" ~ toString(loopStep) ~ "] = " ~
                toString(jointPositions[loopStep]));
        }

        // Centroid of joint positions = interior point of the panel (used later for
        // body identification and as the manipulator base).
        var panelCentroid = vector(0, 0, 0) * meter;
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            panelCentroid = panelCentroid + jointPositions[loopStep];
        }
        panelCentroid = panelCentroid / memberCount;

        println("[DBG] panelCentroid = " ~ toString(panelCentroid));

        // Use Newell's method to compute the best-fit normal of the joint polygon.
        // Summing cross products of centroid-relative edge pairs gives a normal that
        // is exact for planar arrangements and a robust area-weighted average for
        // non-planar ones. This is more reliable than taking the first non-collinear
        // triple, which can pick a degenerate near-collinear triple on skewed polygons.
        var newellSum = vector(0, 0, 0);
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const p1 = (jointPositions[loopStep] - panelCentroid) / meter;
            const p2 = (jointPositions[(loopStep + 1) % memberCount] - panelCentroid) / meter;
            newellSum = newellSum + cross(p1, p2);
        }

        if (norm(newellSum) < PERPENDICULARITY_TOLERANCE)
        {
            reportFeatureError(context, id,
                "All selected frame members appear to be collinear; they must form a non-degenerate closed loop");
            return;
        }
        const openingNormal = normalize(newellSum);

        println("[DBG] Newell sum magnitude = " ~ toString(norm(newellSum)));
        println("[DBG] openingNormal = " ~ toString(openingNormal));

        // Compute the frame extent in the opening-normal direction for alignment.
        // Use the bounding box of all frame bodies in the panel coordinate system.
        //
        // Build a panel-plane X axis that is guaranteed to be perpendicular to
        // openingNormal. Projection removes any residual floating-point imprecision
        // so that the coordSystem perpendicularity precondition is always satisfied.
        //
        // Strategy:
        //   1. Seed panelXAxis from the world basis vector most perpendicular to
        //      openingNormal (guarantees a valid non-degenerate axis in all cases).
        //   2. Try each joint-to-centroid direction in order; the first whose projected
        //      magnitude exceeds the collinearity tolerance overrides the seed — this
        //      gives a geometry-driven axis aligned with the actual panel opening.
        const worldBasisVectors = [vector(1, 0, 0), vector(0, 1, 0), vector(0, 0, 1)];
        var panelXAxis = vector(1, 0, 0);
        var bestWorldProjectionMagnitude = 0;
        for (var basisIndex = 0; basisIndex < 3; basisIndex += 1)
        {
            const projected = projectOntoPlane(worldBasisVectors[basisIndex], openingNormal);
            if (norm(projected) > bestWorldProjectionMagnitude)
            {
                bestWorldProjectionMagnitude = norm(projected);
                panelXAxis = normalize(projected);
            }
        }
        println("[DBG] panelXAxis world-basis seed = " ~ toString(panelXAxis) ~
            "  |dot(xAxis,normal)| = " ~ toString(abs(dot(panelXAxis, openingNormal))));

        for (var xAxisSearchIndex = 0; xAxisSearchIndex < memberCount; xAxisSearchIndex += 1)
        {
            // Divide by meter to produce a dimensionless direction vector suitable for
            // dot products and norm comparisons (joint positions carry meter units).
            const projectedDir = projectOntoPlane(
                (jointPositions[xAxisSearchIndex] - panelCentroid) / meter, openingNormal);
            if (norm(projectedDir) > PERPENDICULARITY_TOLERANCE)
            {
                panelXAxis = normalize(projectedDir);
                println("[DBG] panelXAxis overridden by joint " ~ toString(xAxisSearchIndex) ~
                    " = " ~ toString(panelXAxis) ~
                    "  |dot(xAxis,normal)| = " ~ toString(abs(dot(panelXAxis, openingNormal))));
                break;
            }
            else
            {
                println("[DBG] joint " ~ toString(xAxisSearchIndex) ~
                    " projected magnitude too small (" ~ toString(norm(projectedDir)) ~
                    "), skipping for xAxis");
            }
        }
        const centeredCSys = coordSystem(panelCentroid, panelXAxis, openingNormal);
        println("[DBG] centeredCSys origin = " ~ toString(centeredCSys.origin));
        println("[DBG] centeredCSys xAxis  = " ~ toString(centeredCSys.xAxis));
        println("[DBG] centeredCSys zAxis  = " ~ toString(centeredCSys.zAxis));

        const frameBox = evBox3d(context, {
                    "topology" : qUnion(frameMemberBodies),
                    "cSys"     : centeredCSys,
                    "tight"    : true
                });
        const frameDepth   = frameBox.maxCorner[2] - frameBox.minCorner[2];

        println("[DBG] frameBox.minCorner = " ~ toString(frameBox.minCorner));
        println("[DBG] frameBox.maxCorner = " ~ toString(frameBox.maxCorner));
        println("[DBG] frameDepth = " ~ toString(frameDepth));

        // Midpoint of the frame assembly in the opening-normal direction, measured from
        // panelCentroid along openingNormal. For frames that are symmetric about the joint
        // plane this is ~0; for asymmetric frames it can be a significant non-zero offset,
        // which is the root cause of alignment points appearing outside the frame window
        // when this offset is ignored.
        const frameCenterZ = (frameBox.maxCorner[2] + frameBox.minCorner[2]) / 2;

        println("[DBG] frameCenterZ = " ~ toString(frameCenterZ));

        // ── 5. Compute alignment shift ─────────────────────────────────────────────────

        const maxAlignmentOffset = (frameDepth > definition.thickness) ?
            (frameDepth - definition.thickness) / 2 : 0 * meter;
        if (definition.thickness >= frameDepth)
        {
            reportFeatureWarning(context, id,
                "Panel thickness equals or exceeds the frame depth; alignment adjustment is not available");
        }

        // Panel mid-surface positions along openingNormal, measured from panelCentroid:
        //   flushFrontZ  = frameBox.maxCorner[2] - thickness/2  (flush with +normal face)
        //   frameCenterZ = (max + min) / 2                      (centered in frame depth)
        //   flushBackZ   = frameBox.minCorner[2] + thickness/2  (flush with -normal face)
        const flushFrontZ = frameCenterZ + maxAlignmentOffset;
        const flushBackZ  = frameCenterZ - maxAlignmentOffset;

        println("[DBG] maxAlignmentOffset = " ~ toString(maxAlignmentOffset));
        println("[DBG] flushFrontZ = " ~ toString(flushFrontZ));
        println("[DBG] flushBackZ  = " ~ toString(flushBackZ));

        var alignmentShift is ValueWithUnits = frameCenterZ;
        if (definition.alignmentIndex == 0)
        {
            alignmentShift = flushFrontZ;
        }
        else if (definition.alignmentIndex == 2)
        {
            alignmentShift = flushBackZ;
        }

        println("[DBG] alignmentIndex = " ~ toString(definition.alignmentIndex) ~
            "  alignmentShift = " ~ toString(alignmentShift));

        // ── 6. Register the three-point alignment manipulator ─────────────────────────
        //
        // Each manipulator point is placed at the in-plane centroid of the panel but at the
        // exact normal-direction position the panel mid-surface would occupy for that alignment
        // choice. Anchoring to flushFrontZ/frameCenterZ/flushBackZ (not to ±maxAlignmentOffset
        // from panelCentroid) guarantees all three points always appear INSIDE the frame body
        // depth regardless of whether the frame is symmetric about the joint plane.

        const alignmentPointPositive = panelCentroid + flushFrontZ  * openingNormal;
        const alignmentPointCenter   = panelCentroid + frameCenterZ * openingNormal;
        const alignmentPointNegative = panelCentroid + flushBackZ   * openingNormal;

        println("[DBG] manipulator[0] flushFront  = " ~ toString(alignmentPointPositive));
        println("[DBG] manipulator[1] center       = " ~ toString(alignmentPointCenter));
        println("[DBG] manipulator[2] flushBack    = " ~ toString(alignmentPointNegative));

        addManipulators(context, id, {
                    "alignmentManipulator" : pointsManipulator({
                                "points" : [
                                    alignmentPointPositive,
                                    alignmentPointCenter,
                                    alignmentPointNegative
                                ],
                                "index" : definition.alignmentIndex
                            })
                });

        // ── 7. Build alignment-positioned boundary polyline ─────────────────────────────
        //
        // Each joint is adjusted by the alignment shift: an offset along openingNormal so
        // the panel mid-surface sits at the chosen alignment position (flush front, centered,
        // flush back). The edge gap is NOT applied here — it is applied after the frame-face
        // intersection trim (step 9b) so the gap is uniform from the actual frame inner-face
        // geometry and not consumed/overridden by the trim.
        //
        // opPolyline produces straight-line segments between the joint positions; for a
        // planar frame opening this gives the exact flat boundary polygon.

        var boundaryPoints = makeArray(memberCount + 1);

        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            boundaryPoints[loopStep] = jointPositions[loopStep] + alignmentShift * openingNormal;
            println("[DBG] boundaryPoints[" ~ toString(loopStep) ~ "] = " ~
                toString(boundaryPoints[loopStep]));
        }

        // Close the polyline by repeating the first point as the last point.
        boundaryPoints[memberCount] = boundaryPoints[0];

        opPolyline(context, id + "boundaryPolyline", {
                    "points" : boundaryPoints
                });

        // ── 8. Fill the boundary polyline to create a flat panel surface ─────────────
        //
        // opFillSurface spans the closed polyline wire with a smooth surface. For a
        // planar frame opening the result is a flat patch; the frame members are assumed
        // to define a planar loop so no guide vertices or tangency constraints are used.

        opFillSurface(context, id + "panelFill", {
                    "edgesG0" : qCreatedBy(id + "boundaryPolyline", EntityType.EDGE)
                });

        // ── 8b. Refine the boundary via frame-face intersection ───────────────────────
        //
        // For non-rectangular profiles (C-channel, round tube, L-angle, etc.) the
        // true panel boundary is the curve where the panel mid-surface meets the inner
        // (sweep) faces of each frame member at the alignment depth. opSplitFace cuts
        // the first-pass fill surface along those exact geometric curves. The interior
        // fragment is the correctly bounded panel surface at that alignment position.
        //
        // For rectangular box tube the lateral inner faces are coincident with the
        // fill-surface edge; opSplitFace adds no interior splits and the first-pass
        // surface is used directly.

        // Collect lateral (sweep) faces for all frame members by excluding the start
        // and end cap faces. These are the profile-facing surfaces that define the
        // inner boundary geometry for any cross-section shape.
        var capFaceQueryArray = makeArray(memberCount);
        for (var memberIdx = 0; memberIdx < memberCount; memberIdx += 1)
        {
            capFaceQueryArray[memberIdx] = qUnion([
                qFrameStartFace(frameMemberBodies[memberIdx]),
                qFrameEndFace(frameMemberBodies[memberIdx])
            ]);
        }
        const allLateralFaces = qSubtraction(
            qOwnedByBody(qUnion(frameMemberBodies), EntityType.FACE),
            qUnion(capFaceQueryArray)
        );

        // Split the first-pass fill surface along the lateral frame face boundaries.
        // Wrapped in try: if no frame face crosses the fill surface interior (e.g., a
        // gap-reduced fill that no longer reaches the frame inner faces, or box tube
        // where the frame faces are flush with the fill edge) the fill surface is left
        // intact and the fragment search below returns it unchanged.
        try(opSplitFace(context, id + "splitPanelFill", {
                    "faceTargets" : qCreatedBy(id + "panelFill", EntityType.FACE),
                    "faceTools"   : allLateralFaces
                }));

        // Identify the interior fragment: the face containing (or closest to) the
        // panel centroid translated to the alignment plane. The centroid is inside the
        // frame opening by construction and therefore always inside the interior fragment.
        const centroidAtAlignment = panelCentroid + alignmentShift * openingNormal;
        var refinedFillFaces = qContainsPoint(
            qCreatedBy(id + "panelFill", EntityType.FACE),
            centroidAtAlignment
        );
        if (isQueryEmpty(context, refinedFillFaces))
        {
            refinedFillFaces = qClosestTo(
                qCreatedBy(id + "panelFill", EntityType.FACE),
                centroidAtAlignment
            );
        }

        // Extract the interior fragment into a new independent surface body.
        // opExtractSurface copies the selected face(s) without modifying the original
        // fill body, which may now hold outer fragments from the split.
        opExtractSurface(context, id + "panelFillRefined", {
                    "faces" : refinedFillFaces
                });

        // The original first-pass fill body (and any outer fragments) is no longer needed.
        opDeleteBodies(context, id + "deleteFirstPassFill", {
                    "entities" : qCreatedBy(id + "panelFill", EntityType.BODY)
                });

        // ── 9. Thicken the refined fill surface into a solid panel ────────────────────
        //
        // Thicken symmetrically about the refined mid-surface. The surface is already
        // at the alignment position and has the correct boundary for that depth.
        // Equal offsets in both normal directions give the correct panel thickness at
        // every point, including curved boundary regions for non-rectangular profiles.

        opThicken(context, id + "panelThicken", {
                    "entities"   : qCreatedBy(id + "panelFillRefined", EntityType.BODY),
                    "thickness1" : definition.thickness / 2,
                    "thickness2" : definition.thickness / 2
                });

        const panelBodyQuery = qCreatedBy(id + "panelThicken", EntityType.BODY);

        // Clean up intermediate bodies now that the solid panel exists.
        // The boundary polyline wire and refined fill surface are no longer needed.
        opDeleteBodies(context, id + "deleteWire", {
                    "entities" : qCreatedBy(id + "boundaryPolyline", EntityType.BODY)
                });
        opDeleteBodies(context, id + "deleteFill", {
                    "entities" : qCreatedBy(id + "panelFillRefined", EntityType.BODY)
                });

        // ── 9b. Apply edge gap by offsetting non-cap panel faces inward ──────────────
        //
        // The gap is applied AFTER thickening so that it measures a uniform distance
        // from the actual frame inner-face geometry — the geometry established by the
        // opSplitFace intersection trim above.
        //
        // qNonCapEntity(id + "panelThicken", EntityType.FACE) returns only the perimeter
        // wall faces produced by the thicken operation — the faces swept from the boundary
        // edges of the fill surface. The two cap faces (front and back, produced by
        // offsetting the fill surface itself) are excluded because they bear the alignment
        // position and should not move. opOffsetFace with a negative distance shrinks the
        // panel boundary inward on all sides simultaneously.

        if (definition.gap > 0 * meter)
        {
            opOffsetFace(context, id + "panelGapOffset", {
                        "moveFaces"      : qNonCapEntity(id + "panelThicken", EntityType.FACE),
                        "offsetDistance" : -definition.gap
                    });
        }

        // ── 10. Name the panel body ───────────────────────────────────────────────────

        // Use the panel body's bounding box in the panel coordinate system to derive
        // width and height dimensions for the name string. This works for any panel shape.
        const panelBox = evBox3d(context, {
                    "topology" : panelBodyQuery,
                    "cSys"     : centeredCSys,
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
 * Builds an ordered closed loop of frame member indices using a body-adjacency graph.
 *
 * For each pair of selected bodies, evDistance is used to determine whether they are
 * touching. A Hamiltonian cycle is then found in the resulting adjacency graph via
 * depth-first search. This approach correctly handles both traditional end-to-end
 * frame joints and T-intersections where long spanning members extend beyond the panel
 * boundary — adjacency is determined from solid body geometry, not cap face positions.
 *
 * The adjacency table is stored as a flat array indexed by
 * (outerMemberIndex * memberCount + innerMemberIndex) to avoid nested-array mutation.
 *
 * @param context          {Context}        : The active feature context.
 * @param frameBodies      {array}          : Array of frame member body queries.
 * @param contactTolerance {ValueWithUnits} : Maximum body-to-body distance for two
 *                                            members to be considered adjacent/touching.
 * @returns {array} : Ordered array of member indices (length == memberCount) forming a
 *                    closed loop, or an empty array if no valid Hamiltonian cycle exists.
 */
function buildLoopFromBodyAdjacency(context is Context, frameBodies is array, contactTolerance is ValueWithUnits) returns array
{
    const memberCount = size(frameBodies);

    // Flat adjacency table: index = outerIndex * memberCount + innerIndex.
    // adjacencyTable[i * memberCount + j] is true when bodies i and j are touching.
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

    // Find a Hamiltonian cycle starting from member 0 via depth-first search.
    // FeatureScript arrays are value types (copy-on-write), so backtracking works
    // correctly without explicit undo: each recursive call gets its own copy of
    // visitedMembers and cyclePath.
    var initialVisited = makeArray(memberCount, false);
    initialVisited[0] = true;
    var initialPath = makeArray(memberCount, 0);

    return findHamiltonianCycleDFS(adjacencyTable, initialVisited, initialPath, memberCount, 1);
}

/**
 * Recursive depth-first search that attempts to extend a partial Hamiltonian path
 * to a complete cycle through all frame members.
 *
 * Because FeatureScript arrays are value types, each call operates on independent
 * copies of visitedMembers and cyclePath; backtracking is implicit.
 *
 * @param adjacencyTable  {array}  : Flat n*n boolean adjacency table (see buildLoopFromBodyAdjacency).
 * @param visitedMembers  {array}  : Boolean array, true for each member already in the path.
 * @param cyclePath       {array}  : Current partial path of member indices.
 * @param memberCount     {number} : Total number of frame members.
 * @param currentDepth    {number} : Number of members placed in cyclePath so far.
 * @returns {array} : Complete cycle path (length == memberCount) if found, otherwise empty array.
 */
function findHamiltonianCycleDFS(adjacencyTable is array, visitedMembers is array, cyclePath is array, memberCount is number, currentDepth is number) returns array
{
    if (currentDepth == memberCount)
    {
        // All members placed — verify that the last member connects back to the first
        // to close the cycle.
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
            // Create independent copies for this branch (value-type copy-on-write).
            var branchVisited = visitedMembers;
            branchVisited[candidateIndex] = true;
            var branchPath = cyclePath;
            branchPath[currentDepth] = candidateIndex;

            const searchResult = findHamiltonianCycleDFS(adjacencyTable, branchVisited, branchPath, memberCount, currentDepth + 1);
            if (size(searchResult) == memberCount)
            {
                return searchResult;
            }
        }
    }

    return [];
}

/**
 * Projects a dimensionless direction vector onto the plane defined by planeNormal,
 * removing any component along planeNormal. Used to guarantee that the panel
 * coordinate system X axis is perpendicular to openingNormal regardless of any
 * residual floating-point imprecision in the input direction vector.
 *
 * Both inputs and the return value are dimensionless vectors (no unit attachment).
 *
 * @param directionVector {Vector} : Dimensionless 3D vector to project.
 * @param planeNormal     {Vector} : Unit normal of the plane to project onto.
 * @returns {Vector} : Projection of directionVector onto the plane; may be zero if
 *                     directionVector is parallel to planeNormal.
 */
function projectOntoPlane(directionVector is Vector, planeNormal is Vector) returns Vector
{
    return directionVector - dot(directionVector, planeNormal) * planeNormal;
}

/**
 * Manipulator change function for panelMakerFeature.
 *
 * When the user clicks one of the three alignment points, this function updates
 * definition.alignmentIndex to the newly selected index (0, 1, or 2) and returns
 * the updated definition so the feature regenerates with the new alignment position.
 *
 * @param context : The active feature context.
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
