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
 *   2. Extract sweep directions using the same strategy as the Onshape Cut List feature:
 *      straight swept edges via evEdgeTangentLine, cylindrical swept faces via
 *      evSurfaceDefinition, or arc swept edges for curved members. This is correct for
 *      mitered ends, compound cuts, and curved arc frames — unlike cap face normals.
 *   3. Compute panel normal N = normalize(cross(dir_i, dir_j)) from the first two
 *      non-parallel sweep directions (determined with parallelVectors from the std lib).
 *      Validate every direction is perpendicular to N (perpendicularVectors from std lib).
 *   4. Build the panel coordinate system (Z = N, X = first sweep direction). Use evBox3d
 *      in that system to derive the three alignment positions from the frame depth.
 *   5. Sort the selected members into a closed loop via a body-adjacency graph and a
 *      Hamiltonian cycle search.
 *   6. For each consecutive pair in the loop, find the inner swept face with
 *      qClosestTo(qFrameSweptFace(body), openingCenter). Get the constraint plane at the
 *      correct corner end via evFaceTangentPlaneAtEdge on the edge shared between the
 *      inner face and the cap face nearest to the adjacent member (found with qClosestTo).
 *      Solve a 2x2 system in panel XY for the corner position.
 *   7. opPolyline -> opFillSurface -> opExtrude (+/-thickness/2 along N).
 *   8. Apply edge gap via opOffsetFace on the panel's non-cap perimeter faces.
 *   9. Name the panel body from its XY bounding box dimensions.
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

        // ── 4. Validate planarity ───────────────────────────────────────────────────────
        //
        // Every sweep direction must be perpendicular to N for the frame to lie in a plane.
        // perpendicularVectors() from the standard library handles the tolerance comparison.

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
        // of their inner swept face constraint lines in panel XY at Z = alignmentZ.
        //
        // The inner swept face is found with qClosestTo(qFrameSweptFace(body), openingCenter),
        // using the frame topology attribute to select only the profile-swept lateral faces.
        // The frame opening center is the centroid of all member bodies; the swept face whose
        // geometry is closest to that point is the face bordering the void — no manual vector
        // scoring needed.

        var cornerPoints = makeArray(memberCount);
        for (var loopStep = 0; loopStep < memberCount; loopStep += 1)
        {
            const currentMemberBody = frameMemberBodies[loopOrder[loopStep]];
            const nextMemberBody    = frameMemberBodies[loopOrder[(loopStep + 1) % memberCount]];

            const innerFaceCurrent = qClosestTo(qFrameSweptFace(currentMemberBody), panelCentroid);
            const innerFaceNext    = qClosestTo(qFrameSweptFace(nextMemberBody),    panelCentroid);

            cornerPoints[loopStep] = computeCornerPoint(context,
                    innerFaceCurrent, currentMemberBody,
                    innerFaceNext,    nextMemberBody,
                    panelXAxis, panelYAxis, openingNormal, panelCentroid, alignmentZ);
        }

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

    // Strategy 2: cylindrical swept face axis — used for round tubes and pipes.
    const sweptFaces = qFrameSweptFace(memberBody);
    const cylinderFaces = evaluateQuery(context, qGeometry(sweptFaces, GeometryType.CYLINDER));
    if (size(cylinderFaces) > 0)
    {
        return evSurfaceDefinition(context, { "face" : cylinderFaces[0] }).coordSystem.zAxis;
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
 * @returns {Plane} : Tangent plane of innerFace at the edge connecting it to the
 *                    cap face nearest to adjacentBody.
 */
function getInnerFaceCornerPlane(context is Context, innerFace is Query, memberBody is Query, adjacentBody is Query) returns Plane
{
    // Find the cap face (start or end) of this member that is closest to the adjacent member.
    const adjacentBodyCentroid = evApproximateCentroid(context, { "entities" : adjacentBody });
    const nearestCapFace = qClosestTo(
        qUnion([qFrameStartFace(memberBody), qFrameEndFace(memberBody)]),
        adjacentBodyCentroid
    );

    // The corner edge is the edge shared between the inner swept face and that cap face.
    const cornerEdge = qIntersection([
        qAdjacent(innerFace,     AdjacencyType.EDGE, EntityType.EDGE),
        qAdjacent(nearestCapFace, AdjacencyType.EDGE, EntityType.EDGE)
    ]);

    // Tangent plane of the inner face along the corner edge (midpoint of the edge).
    // For planar faces this equals evPlane; for cylindrical or other curved faces this
    // gives the local tangent at the correct arc end, improving corner accuracy.
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
 *   1. Get the constraint plane of each member's inner swept face at its corner end via
 *      getInnerFaceCornerPlane. For planar faces this is exact; for curved faces it is
 *      the local tangent at the relevant arc end.
 *   2. Project each plane's normal and origin to the panel XY coordinate system
 *      (dimensionless, units stripped) to obtain two 2D constraint lines.
 *   3. Solve the 2x2 system for the corner XY position, then reconstruct the world point
 *      at Z = alignmentZ.
 *
 * If the two inner face planes are parallel (T-junction or collinear members, detected with
 * parallelVectors from the standard library), a midpoint fallback is used.
 *
 * @param context           {Context}        : The active feature context.
 * @param innerFaceCurrent  {Query}          : Inner swept face of the current member.
 * @param currentBody       {Query}          : Current frame member body.
 * @param innerFaceNext     {Query}          : Inner swept face of the next member.
 * @param nextBody          {Query}          : Next frame member body.
 * @param panelXAxis        {Vector}         : Dimensionless X axis of the panel system.
 * @param panelYAxis        {Vector}         : Dimensionless Y axis of the panel system.
 * @param panelNormal       {Vector}         : Dimensionless panel normal (Z axis).
 * @param panelOrigin       {Vector}         : World-space origin of the panel coordinate system.
 * @param alignmentZ        {ValueWithUnits} : Alignment depth along panelNormal from panelOrigin.
 * @returns {Vector} : World-space corner position with meter units.
 */
function computeCornerPoint(context is Context, innerFaceCurrent is Query, currentBody is Query, innerFaceNext is Query, nextBody is Query, panelXAxis is Vector, panelYAxis is Vector, panelNormal is Vector, panelOrigin is Vector, alignmentZ is ValueWithUnits) returns Vector
{
    const planeCurrent = getInnerFaceCornerPlane(context, innerFaceCurrent, currentBody, nextBody);
    const planeNext    = getInnerFaceCornerPlane(context, innerFaceNext,    nextBody,    currentBody);

    // Project each plane normal to panel XY (the 2D constraint directions).
    const nCx = dot(planeCurrent.normal, panelXAxis);
    const nCy = dot(planeCurrent.normal, panelYAxis);
    const nNx = dot(planeNext.normal,    panelXAxis);
    const nNy = dot(planeNext.normal,    panelYAxis);

    // Project each plane origin to panel XY, units stripped to dimensionless meters.
    const oCx = dot(planeCurrent.origin - panelOrigin, panelXAxis) / meter;
    const oCy = dot(planeCurrent.origin - panelOrigin, panelYAxis) / meter;
    const oNx = dot(planeNext.origin    - panelOrigin, panelXAxis) / meter;
    const oNy = dot(planeNext.origin    - panelOrigin, panelYAxis) / meter;

    // 2x2 system:  nCx*x + nCy*y = bC   and   nNx*x + nNy*y = bN
    const bC = nCx * oCx + nCy * oCy;
    const bN = nNx * oNx + nNy * oNy;
    const det = nCx * nNy - nCy * nNx;

    var cornerX is number = 0;
    var cornerY is number = 0;

    // parallelVectors() from the standard library detects when the inner face planes
    // are parallel (T-junction or collinear members), avoiding division by zero.
    if (parallelVectors(planeCurrent.normal, planeNext.normal))
    {
        cornerX = (oCx + oNx) / 2;
        cornerY = (oCy + oNy) / 2;
    }
    else
    {
        cornerX = (bC * nNy - bN * nCy) / det;
        cornerY = (nCx * bN - nNx * bC) / det;
    }

    return panelOrigin
        + cornerX * meter * panelXAxis
        + cornerY * meter * panelYAxis
        + alignmentZ * panelNormal;
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
