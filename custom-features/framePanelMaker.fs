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

// Scale factor applied to the furthest joint radius when sizing each thin intersection
// slab. A factor of 3 guarantees the slab covers the widest frame body cross-section
// (e.g., a wide L-channel leg that extends far from the joint point).
const SLAB_RADIUS_SCALE = 3.0;

// Thickness of the thin intersection slab used to extract panel boundary curves.
// 0.1 mm is robust for all normal frame cross-section sizes while keeping the slab
// thin enough to produce clean, precise intersection edges.
const CROSS_SECTION_SLAB_THICKNESS = 1e-4 * meter;

// Squared-distance tolerance (dimensionless, in m²/m² units) for the loop-closure
// check. sqrt(1e-6) ≈ 0.001 m = 1 mm maximum acceptable closure gap.
const LOOP_CLOSURE_TOLERANCE_SQ = 1e-6;

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
 *   1. Sort the selected frame members into a connected closed loop using greedy
 *      nearest-neighbor matching on cap face centroid positions.
 *   2. Compute the panel plane by fitting a plane to the joint positions (the points
 *      where consecutive members are closest to each other).
 *   3. Create a large flat slab body centered on the panel plane and thick enough
 *      to span the full panel thickness (with alignment shift applied).
 *   4. Boolean-subtract all frame bodies from the slab simultaneously. The frame
 *      bodies cut through the slab, leaving an inner panel region that exactly
 *      follows the frame intersection boundary curves.
 *   5. Identify the inner panel region using qContainsPoint and delete all outer
 *      slab fragments.
 *   6. If Edge Gap > 0, offset the panel side faces inward to add clearance.
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

        // ── 2. Collect cap face centroid positions for loop sorting ─────────────────────

        var memberStartPoints = makeArray(memberCount);
        var memberEndPoints   = makeArray(memberCount);
        for (var memberIndex = 0; memberIndex < memberCount; memberIndex += 1)
        {
            const startFacePlane = evPlane(context, { "face" : qFrameStartFace(frameMemberBodies[memberIndex]) });
            const endFacePlane   = evPlane(context, { "face" : qFrameEndFace(frameMemberBodies[memberIndex]) });
            memberStartPoints[memberIndex] = startFacePlane.origin;
            memberEndPoints[memberIndex]   = endFacePlane.origin;
        }

        // ── 3. Sort members into a closed loop (any input order) ───────────────────────

        const loopEntries = sortFrameMembersIntoLoop(memberStartPoints, memberEndPoints);

        // Verify the loop closes back on itself: the last member's exit point should
        // be close to the first member's entry point.
        const lastEntry  = loopEntries[memberCount - 1];
        const firstEntry = loopEntries[0];

        const lastExitPoint  = lastEntry.exitIsEnd  ? memberEndPoints[lastEntry.memberIndex]
                                                     : memberStartPoints[lastEntry.memberIndex];
        const firstEntryPoint = firstEntry.exitIsEnd ? memberStartPoints[firstEntry.memberIndex]
                                                      : memberEndPoints[firstEntry.memberIndex];

        const closureGapSq = squaredNorm((lastExitPoint - firstEntryPoint) / meter);
        if (closureGapSq > LOOP_CLOSURE_TOLERANCE_SQ)
        {
            reportFeatureWarning(context, id,
                "Selected frame members do not form a closed loop; verify all members are connected end-to-end");
        }

        // ── 4. Compute joint positions and the panel plane ─────────────────────────────

        // Joint position i = midpoint between the closest points of member i and
        // the next member in the loop. Using evDistance gives the exact contact point
        // for any frame profile shape (round, square, mitered, etc.).
        var jointPositions = makeArray(memberCount);
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const currentMemberIdx = loopEntries[loopStep].memberIndex;
            const nextMemberIdx    = loopEntries[(loopStep + 1) % memberCount].memberIndex;

            const jointDistResult = evDistance(context, {
                        "side0" : frameMemberBodies[currentMemberIdx],
                        "side1" : frameMemberBodies[nextMemberIdx]
                    });

            if (jointDistResult.distance > JOINT_CONTACT_TOLERANCE)
            {
                reportFeatureWarning(context, id,
                    "Frame members at loop position " ~ toString(loopStep) ~
                    " do not touch; results may be incorrect");
            }

            jointPositions[loopStep] = (jointDistResult.sides[0].point +
                                        jointDistResult.sides[1].point) / 2;
        }

        // Centroid of joint positions = interior point of the panel (used later for
        // body identification and as the manipulator base).
        var panelCentroid = vector(0, 0, 0) * meter;
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            panelCentroid = panelCentroid + jointPositions[loopStep];
        }
        panelCentroid = panelCentroid / memberCount;

        // Fit the panel plane normal by scanning consecutive joint triples until a
        // non-collinear triple is found (cross-product magnitude above tolerance).
        var openingNormal = vector(0, 0, 1); // placeholder; replaced below
        var foundNormal = false;
        for (var loopStep = 0; loopStep < memberCount && !foundNormal; loopStep += 1)
        {
            const v1 = (jointPositions[(loopStep + 1) % memberCount] -
                        jointPositions[loopStep]) / meter;
            const v2 = (jointPositions[(loopStep + 2) % memberCount] -
                        jointPositions[(loopStep + 1) % memberCount]) / meter;
            const crossed = cross(v1, v2);
            if (norm(crossed) > PERPENDICULARITY_TOLERANCE)
            {
                openingNormal = normalize(crossed);
                foundNormal = true;
            }
        }

        if (!foundNormal)
        {
            reportFeatureError(context, id,
                "All selected frame members appear to be collinear; they must form a non-degenerate closed loop");
            return;
        }

        // Compute the frame extent in the opening-normal direction for alignment.
        // Use the bounding box of all frame bodies in the panel coordinate system.
        // Choose a stable in-plane X axis from the centroid toward the first joint.
        const panelXAxis = normalize((jointPositions[0] - panelCentroid) / meter);
        const centeredCSys = coordSystem(panelCentroid, panelXAxis, openingNormal);

        const frameBox = evBox3d(context, {
                    "topology" : qUnion(frameMemberBodies),
                    "cSys"     : centeredCSys,
                    "tight"    : true
                });
        const frameDepth = frameBox.maxCorner[2] - frameBox.minCorner[2];

        // ── 5. Compute alignment shift ─────────────────────────────────────────────────

        const maxAlignmentOffset = (frameDepth > definition.thickness) ?
            (frameDepth - definition.thickness) / 2 : 0 * meter;
        if (definition.thickness >= frameDepth)
        {
            reportFeatureWarning(context, id,
                "Panel thickness equals or exceeds the frame depth; alignment adjustment is not available");
        }

        var alignmentShift is ValueWithUnits = 0 * meter;
        if (definition.alignmentIndex == 0)
        {
            alignmentShift = maxAlignmentOffset;
        }
        else if (definition.alignmentIndex == 2)
        {
            alignmentShift = -maxAlignmentOffset;
        }

        // ── 6. Register the three-point alignment manipulator ─────────────────────────

        const manipulatorBasePoint = panelCentroid;
        const alignmentPointPositive = manipulatorBasePoint + maxAlignmentOffset * openingNormal;
        const alignmentPointCenter   = manipulatorBasePoint;
        const alignmentPointNegative = manipulatorBasePoint - maxAlignmentOffset * openingNormal;
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

        // ── 7. Extract panel boundary edges via per-member thin-slab intersection ───────
        //
        // For each frame member a thin slab (CROSS_SECTION_SLAB_THICKNESS thick) is
        // placed at the panel mid-surface (panel centroid + alignmentShift in the
        // openingNormal direction) and boolean-intersected with that member. This
        // produces exactly one cross-section body per member with no body-identification
        // ambiguity. The edge on the cross-section's flat cap face that is closest to the
        // panel centroid in the in-plane direction is the boundary where the panel surface
        // meets this frame member's inner face. Chaining all N such edges gives the closed
        // boundary wire used by opFill.
        //
        // This approach handles every frame profile (T-slot, round tube, L-channel, box
        // tube, etc.) and naturally produces a nonplanar fill surface when the joint
        // positions are not coplanar.

        // Slab half-size: large enough to cover the widest frame member cross-section.
        var maxInPlaneRadiusSq = 0.0;
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const inPlaneOffset = (jointPositions[loopStep] - panelCentroid) / meter;
            const radiusSq = squaredNorm(inPlaneOffset);
            if (radiusSq > maxInPlaneRadiusSq)
            {
                maxInPlaneRadiusSq = radiusSq;
            }
        }
        const inPlaneExtentHalfX = max(abs(frameBox.maxCorner[0]), abs(frameBox.minCorner[0]));
        const inPlaneExtentHalfY = max(abs(frameBox.maxCorner[1]), abs(frameBox.minCorner[1]));
        const inPlaneExtentNum   = max(inPlaneExtentHalfX, inPlaneExtentHalfY) / meter;
        const maxInPlaneRadius   = sqrt(maxInPlaneRadiusSq) + inPlaneExtentNum;
        const slabHalfSizeNum    = maxInPlaneRadius * SLAB_RADIUS_SCALE;

        // Thin slab dimensions in the panel coordinate frame (local Z = openingNormal).
        const thinHalfNum  = CROSS_SECTION_SLAB_THICKNESS / (2 * meter);
        const slabShiftNum = alignmentShift / meter;

        var panelBoundaryEdges = makeArray(memberCount);

        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const memberIdx    = loopEntries[loopStep].memberIndex;
            const memberSlabId = id + ("xSlab" ~ toString(loopStep));
            const memberXSecId = id + ("xSect" ~ toString(loopStep));

            // Create a fresh thin slab at the panel mid-surface, centered at the origin
            // in panel-plane local coordinates. opTransform then maps it to world space.
            fCuboid(context, memberSlabId, {
                        "corner1" : vector(-slabHalfSizeNum, -slabHalfSizeNum,
                                           slabShiftNum - thinHalfNum) * meter,
                        "corner2" : vector( slabHalfSizeNum,  slabHalfSizeNum,
                                           slabShiftNum + thinHalfNum) * meter
                    });
            opTransform(context, memberSlabId + "T", {
                        "bodies"    : qCreatedBy(memberSlabId, EntityType.BODY),
                        "transform" : toWorld(centeredCSys)
                    });

            // Intersect the thin slab with this frame member. The result is the member's
            // cross-section profile at the panel mid-surface — exactly one body.
            opBoolean(context, memberXSecId, {
                        "tools"         : frameMemberBodies[memberIdx],
                        "targets"       : qCreatedBy(memberSlabId, EntityType.BODY),
                        "operationType" : BooleanOperationType.INTERSECT,
                        "keepTools"     : true
                    });

            const xSectBodies = evaluateQuery(context,
                qCreatedBy(memberXSecId, EntityType.BODY));
            if (size(xSectBodies) == 0)
            {
                reportFeatureError(context, id,
                    "Frame member at loop position " ~ toString(loopStep) ~
                    " does not intersect the panel plane. Verify the frame members " ~
                    "are close enough to coplanar for a panel at this alignment position.");
                return;
            }

            // The cross-section body has two flat cap faces (parallel to the opening
            // normal) and side faces from the frame member surfaces.
            // Among the edges on those cap faces, select the one whose midpoint is
            // closest to the panel centroid in the in-plane direction. This is the
            // edge where the panel surface meets this member's inner (void-facing) face.
            const xSectFaces    = qOwnedByBody(xSectBodies[0], EntityType.FACE);
            const xSectCapFaces = qParallelPlanes(xSectFaces, openingNormal, true);
            const capEdges      = qAdjacent(xSectCapFaces, AdjacencyType.EDGE, EntityType.EDGE);
            const capEdgeArray  = evaluateQuery(context, capEdges);

            if (size(capEdgeArray) == 0)
            {
                reportFeatureError(context, id,
                    "No boundary edges found on cross-section cap face for frame member " ~
                    "at loop position " ~ toString(loopStep) ~
                    ". The frame member cross-section may be degenerate at this panel position.");
                return;
            }

            var innerEdge        = capEdgeArray[0];
            // innerEdgeDistSq is only meaningful after firstEdgeSeen becomes false;
            // the initial value of 0 is never compared before the first valid assignment.
            var innerEdgeDistSq  is number  = 0;
            var firstEdgeSeen    is boolean = true;

            for (var edgeIdx = 0; edgeIdx < size(capEdgeArray); edgeIdx += 1)
            {
                // Midpoint of this edge in 3D world space.
                const edgeMid = evEdgeTangentLine(context, {
                            "edge"                      : capEdgeArray[edgeIdx],
                            "parameter"                 : 0.5,
                            "arcLengthParameterization" : false
                        }).origin;

                // In-plane squared distance from edge midpoint to panel centroid.
                // Remove the openingNormal component so only the in-plane proximity counts.
                const toMid      = (edgeMid - panelCentroid) / meter;
                const normalComp = toMid[0] * openingNormal[0] +
                                   toMid[1] * openingNormal[1] +
                                   toMid[2] * openingNormal[2];
                const inPlane    = vector(toMid[0] - normalComp * openingNormal[0],
                                          toMid[1] - normalComp * openingNormal[1],
                                          toMid[2] - normalComp * openingNormal[2]);
                const distSq     = squaredNorm(inPlane);

                if (firstEdgeSeen || distSq < innerEdgeDistSq)
                {
                    innerEdge       = capEdgeArray[edgeIdx];
                    innerEdgeDistSq = distSq;
                    firstEdgeSeen   = false;
                }
            }

            panelBoundaryEdges[loopStep] = innerEdge;
        }

        // ── 8. Fill the boundary to create the panel mid-surface ─────────────────────
        //
        // opFill creates a surface spanning the closed wire formed by the N inner boundary
        // edges. For coplanar edges the result is a flat patch; for non-coplanar edges it
        // produces a smooth interpolated surface that conforms to the 3D frame geometry.

        opFill(context, id + "panelFill", {
                    "edges" : qUnion(panelBoundaryEdges)
                });

        // ── 9. Thicken the surface into a solid panel ─────────────────────────────────
        //
        // Thicken symmetrically about the mid-surface. The alignment shift was encoded
        // in the slab placement, so the fill surface IS already the panel mid-surface
        // for the selected alignment position; equal offsets in both normal directions
        // give the correct panel thickness and position.

        opThicken(context, id + "panelThicken", {
                    "entities"   : qCreatedBy(id + "panelFill", EntityType.BODY),
                    "thickness1" : definition.thickness / 2,
                    "thickness2" : definition.thickness / 2
                });

        // The fill surface is no longer needed once the solid exists.
        opDeleteBodies(context, id + "deleteFill", {
                    "entities" : qCreatedBy(id + "panelFill", EntityType.BODY)
                });

        // Delete all temporary thin cross-section bodies. The opFill has already
        // consumed the boundary edges topologically, so these bodies can be removed.
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const xSectBodiesCleanup = evaluateQuery(context,
                qCreatedBy(id + ("xSect" ~ toString(loopStep)), EntityType.BODY));
            if (size(xSectBodiesCleanup) > 0)
            {
                opDeleteBodies(context, id + ("delXSect" ~ toString(loopStep)), {
                            "entities" : qCreatedBy(id + ("xSect" ~ toString(loopStep)), EntityType.BODY)
                        });
            }
        }

        const panelBodyQuery = qCreatedBy(id + "panelThicken", EntityType.BODY);

        // ── 10. Apply edge gap ─────────────────────────────────────────────────────────

        // The side faces of the panel (those NOT parallel to the opening normal) are
        // offset inward by the edge gap to create clearance between the panel edges
        // and the frame body surfaces.
        if (definition.gap > 0 * meter)
        {
            const allPanelFaces     = qOwnedByBody(panelBodyQuery, EntityType.FACE);
            const panelFrontBackFaces = qParallelPlanes(allPanelFaces, openingNormal, true);
            const panelSideFaces    = qSubtraction(allPanelFaces, panelFrontBackFaces);

            opOffsetFace(context, id + "edgeGap", {
                        "moveFaces"      : panelSideFaces,
                        "offsetDistance" : -definition.gap
                    });
        }

        // ── 11. Name the panel body ────────────────────────────────────────────────────

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
 * Sorts an unordered set of frame members into a connected closed loop using greedy
 * nearest-neighbor matching on cap face centroid positions.
 *
 * Each iteration finds the unused member whose start or end cap centroid is closest
 * to the current traversal exit point, appends it to the loop, and advances the exit
 * point to the far end of the newly appended member.
 *
 * Returns an array of maps (one per member in loop order) with fields:
 *   "memberIndex" {number}  : Original (unsorted) index into frameMemberBodies.
 *   "exitIsEnd"   {boolean} : true  → this member's end cap is the exit toward the next member;
 *                             false → this member's start cap is the exit toward the next member.
 *
 * @param startPoints {array} : Array of start cap face centroid positions (ValueWithUnits vectors).
 * @param endPoints   {array} : Array of end cap face centroid positions (ValueWithUnits vectors).
 * @returns {array}           : Sorted loop descriptor array.
 */
function sortFrameMembersIntoLoop(startPoints is array, endPoints is array) returns array
{
    const memberCount = size(startPoints);
    var loop = makeArray(memberCount);
    var usedByStep = makeArray(memberCount, false);

    // Seed the loop with member 0; treat its end cap as the initial exit point.
    loop[0] = { "memberIndex" : 0, "exitIsEnd" : true };
    usedByStep[0] = true;
    var currentExitPoint = endPoints[0];

    for (var step = 1; step < memberCount; step += 1)
    {
        var bestMemberIndex = -1;
        // bestDistSq is only meaningful after bestMemberIndex is first assigned
        // (guarded by the bestMemberIndex == -1 condition below).
        var bestDistSq is number = 0;
        var bestEntryIsStart is boolean = true;

        for (var candidateIndex = 0; candidateIndex < memberCount; candidateIndex += 1)
        {
            if (!usedByStep[candidateIndex])
            {
                // Squared distances (dimensionless) from the current exit point to both
                // caps of this candidate. squaredNorm avoids a sqrt and keeps units clean.
                const diffToStart = (startPoints[candidateIndex] - currentExitPoint) / meter;
                const distSqToStart = squaredNorm(diffToStart);

                const diffToEnd = (endPoints[candidateIndex] - currentExitPoint) / meter;
                const distSqToEnd = squaredNorm(diffToEnd);

                if (bestMemberIndex == -1 || distSqToStart < bestDistSq)
                {
                    bestMemberIndex    = candidateIndex;
                    bestDistSq         = distSqToStart;
                    bestEntryIsStart   = true;
                }

                if (distSqToEnd < bestDistSq)
                {
                    bestMemberIndex    = candidateIndex;
                    bestDistSq         = distSqToEnd;
                    bestEntryIsStart   = false;
                }
            }
        }

        // If the entry is via the start cap, the exit is via the end cap, and vice versa.
        const exitIsEnd = bestEntryIsStart;
        loop[step] = { "memberIndex" : bestMemberIndex, "exitIsEnd" : exitIsEnd };
        usedByStep[bestMemberIndex] = true;
        currentExitPoint = exitIsEnd ? endPoints[bestMemberIndex] : startPoints[bestMemberIndex];
    }

    return loop;
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
