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
 *   2. Compute the panel plane using Newell's method on the joint positions (the
 *      midpoints of the closest-point pairs between consecutive bodies in the loop).
 *   3. Build a first-pass fill surface from a polyline through the joint contact
 *      points shifted to the chosen alignment position (alignment shift baked in).
 *      The surface sits at the exact depth where the panel will live.
 *   4. Refine the boundary by splitting that surface with the lateral (sweep) faces
 *      of each frame member AT the alignment position. The interior fragment is the
 *      exact panel boundary for any profile shape at that depth: box tube, round tube,
 *      C-channel, etc. The intersection is correctly different for each alignment choice
 *      because the geometry really is different at each depth.
 *   5. Extract the interior fragment into an independent surface body and thicken it
 *      symmetrically to produce the solid panel body.
 *   6. Apply the edge gap AFTER thickening by offsetting the lateral (non-cap) faces
 *      of the thickened panel body inward by definition.gap. The cap faces (front and
 *      back surfaces parallel to openingNormal) are excluded; only the perimeter walls
 *      are moved so the gap is uniform from the actual frame inner-face geometry.
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
        // Joint position i = midpoint of the closest-point pair between member i and the
        // next member in the loop. evDistance finds the exact contact point for any frame
        // profile shape (round tube, box tube, mitered end) and any junction type,
        // including T-intersections where the contact lies mid-body on a spanning member.
        var jointPositions = makeArray(memberCount);
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const currentMemberIdx = loopOrder[loopStep];
            const nextMemberIdx    = loopOrder[(loopStep + 1) % memberCount];

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
        const frameDepth   = frameBox.maxCorner[2] - frameBox.minCorner[2];

        // Midpoint of the frame assembly in the opening-normal direction, measured from
        // panelCentroid along openingNormal. For frames that are symmetric about the joint
        // plane this is ~0; for asymmetric frames it can be a significant non-zero offset,
        // which is the root cause of alignment points appearing outside the frame window
        // when this offset is ignored.
        const frameCenterZ = (frameBox.maxCorner[2] + frameBox.minCorner[2]) / 2;

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

        var alignmentShift is ValueWithUnits = frameCenterZ;
        if (definition.alignmentIndex == 0)
        {
            alignmentShift = flushFrontZ;
        }
        else if (definition.alignmentIndex == 2)
        {
            alignmentShift = flushBackZ;
        }

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

        // ── 7. Build alignment-positioned boundary polyline through joint positions ────
        //
        // Each joint is adjusted by the alignment shift: an offset along openingNormal so
        // the panel mid-surface sits at the chosen alignment position (flush front, centered,
        // flush back). The edge gap is NOT applied here — it is applied after the frame-face
        // intersection trim (step 9b) so the gap is uniform from the actual frame inner-face
        // geometry and not consumed/overridden by the trim.
        //
        // opPolyline (straight line segments) is used so that rectangular and polygonal
        // frames produce flat-faced panels with straight edges. The polyline is closed
        // by repeating the first adjusted point as the last point.

        var boundaryPoints = makeArray(memberCount + 1);

        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            // Alignment: push along the best-fit panel normal to the chosen depth.
            boundaryPoints[loopStep] = jointPositions[loopStep] + alignmentShift * openingNormal;
        }

        // Close the polyline by repeating the first point as the last point.
        boundaryPoints[memberCount] = boundaryPoints[0];

        opPolyline(context, id + "boundaryPolyline", {
                    "points" : boundaryPoints
                });

        // ── 8. Fill the alignment-positioned boundary to create the first-pass surface ──
        //
        // opFillSurface spans the closed polyline wire at the chosen alignment depth.
        // For coplanar joints the result is a flat patch; for non-coplanar joints it
        // produces a smooth surface following the 3D frame geometry. This surface is
        // both the location and the template for the frame-face intersection in step 8b.

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

        // ── 9b. Apply edge gap by offsetting lateral panel faces inward ──────────────
        //
        // The gap is applied AFTER thickening so that it measures a uniform distance
        // from the actual frame inner-face geometry — the geometry that was established
        // by the opSplitFace intersection trim above.
        //
        // The deletion of panelFillRefined is intentionally deferred to after this step.
        // While the refined fill surface body still exists in the context, Onshape tracks
        // the thickened solid's cap faces (front and back) as descendants of the surface
        // faces via qCreatedBy. Subtracting those from the full face set of the solid
        // gives the perimeter wall faces explicitly, without relying on normal-direction
        // geometry tests. The wire body and surface are cleaned up immediately afterward.
        //
        // opOffsetFace with a negative offset pushes each wall face inward by gap,
        // shrinking the panel boundary by that distance on all sides simultaneously.

        if (definition.gap > 0 * meter)
        {
            const panelLateralFaces = qSubtraction(
                qOwnedByBody(panelBodyQuery, EntityType.FACE),
                qCreatedBy(id + "panelFillRefined", EntityType.FACE)
            );
            opOffsetFace(context, id + "panelGapOffset", {
                        "moveFaces"      : panelLateralFaces,
                        "offsetDistance" : -definition.gap
                    });
        }

        // Delete the polyline wire body and the refined fill surface.
        opDeleteBodies(context, id + "deleteWire", {
                    "entities" : qCreatedBy(id + "boundaryPolyline", EntityType.BODY)
                });
        opDeleteBodies(context, id + "deleteFill", {
                    "entities" : qCreatedBy(id + "panelFillRefined", EntityType.BODY)
                });

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
