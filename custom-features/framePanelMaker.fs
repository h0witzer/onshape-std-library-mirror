FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/frameAttributes.fs", version : "2909.0");
import(path : "onshape/std/frameUtils.fs", version : "2909.0");

// Conversion constants
const METERS_TO_MM = 1000;
const MM_PER_INCH = 25.4;
const METERS_TO_INCHES = METERS_TO_MM / MM_PER_INCH;

// Minimum cross-product magnitude used to verify that two sweep axes are not parallel.
// A value of 1e-6 corresponds to an angle deviation of less than 0.06 degrees from parallel.
const PERPENDICULARITY_TOLERANCE = 1e-6;

// Number of decimal places kept when rounding computed panel dimensions in meters
// (3 places = 1 mm resolution).
const DIMENSION_ROUNDING_PRECISION = 3;

/**
 * Returns the sweep axis of a frame body as a normalized Vector, computed from
 * the vector connecting the start cap face centroid to the end cap face centroid.
 * This approach is robust for both perpendicular and mitered end cuts.
 *
 * @param context : The active feature context.
 * @param frameBody {Query} : A query resolving to a single frame body.
 * @returns {Vector} : A normalized 3D vector along the member's sweep direction.
 */
function getFrameSweepAxis(context is Context, frameBody is Query) returns Vector
{
    const startCapPlane = evPlane(context, { "face" : qFrameStartFace(frameBody) });
    const endCapPlane = evPlane(context, { "face" : qFrameEndFace(frameBody) });
    return normalize(endCapPlane.origin - startCapPlane.origin);
}

/**
 * Formats a dimensionless length value (in meters) as a fractional-inches string,
 * reduced to lowest terms. For example, 0.047625 m => "1-7/8".
 *
 * @param lengthInMeters {number} : Dimensionless number representing the length in meters.
 * @param maximumDenominator {number} : Finest denominator to use; must be a positive power of 2
 *                                      (e.g. 16 for 1/16-inch resolution).
 * @returns {string} : Formatted string such as "12", "3-1/2", or "0-3/16".
 */
function formatInchFractionString(lengthInMeters is number, maximumDenominator is number) returns string
{
    const totalInches = lengthInMeters * METERS_TO_INCHES;
    const wholePart = floor(totalInches);
    var numerator = round((totalInches - wholePart) * maximumDenominator);
    var denominator = maximumDenominator;

    // Guard against rounding the fractional part up to a full inch
    if (numerator >= denominator)
    {
        return toString(wholePart + 1);
    }

    if (numerator == 0)
    {
        return toString(wholePart);
    }

    // Reduce the fraction by dividing numerator and denominator by 2 until
    // the numerator is odd (fully reduced form)
    while (numerator % 2 == 0)
    {
        numerator = numerator / 2;
        denominator = denominator / 2;
    }

    return toString(wholePart) ~ "-" ~ toString(numerator) ~ "/" ~ toString(denominator);
}

/**
 * Builds the display name string for the panel body given its computed dimensions.
 *
 * @param prefix {string} : User-specified name prefix (e.g. "Panel").
 * @param panelDim1 {number} : Dimensionless first panel dimension in meters.
 * @param panelDim2 {number} : Dimensionless second panel dimension in meters.
 * @param useMillimeters {boolean} : When true, formats dimensions as "Xmm x Ymm";
 *                                   when false, formats as fractional inches "W-N/D in x W-N/D in".
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
 * Creates a solid panel body sized to fit within the clear opening formed by four
 * adjacent frame members. The panel extends into the channel of each member by
 * (Channel Depth - Edge Gap) on every side.
 *
 * Selections must be four frame members (created by the Frame feature) chosen in
 * sequential order so that each consecutive pair shares a corner, forming a closed loop.
 * Members 0 and 2 are treated as one opposite pair; members 1 and 3 as the other.
 *
 * Frame topology attributes are used to:
 *   - Validate that all selections are proper frame members.
 *   - Derive each member's sweep axis from its cap face positions.
 *   - Identify the swept faces that bound the clear opening (instead of bounding-box
 *     cuboid approximations), giving correct results for any profile shape.
 */
annotation { "Feature Type Name" : "Panel Maker", "Feature Type Description" : "Creates a panel within a clear opening bounded by four frame members" }
export const panelMakerFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Frame Members", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 4 }
        definition.frameMembers is Query;

        annotation { "Group Name" : "Detail Parameters" }
        {
            annotation { "Name" : "Panel Thickness", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.thickness, {(millimeter) : [0.1, 5.6, 100]} as LengthBoundSpec);

            annotation { "Name" : "Channel Depth", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.depth, {(millimeter) : [0.1, 9.18, 100]} as LengthBoundSpec);

            annotation { "Name" : "Edge Gap", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.gap, {(millimeter) : [0.0, 3, 100]} as LengthBoundSpec);
        }

        annotation { "Name" : "Name Dimensions in mm" }
        definition.mm is boolean;

        annotation { "Name" : "Name Prefix", "Default" : "Panel" }
        definition.namePrefix is string;
    }
    {
        // Resolve and count the selected bodies
        const frameMemberBodies = evaluateQuery(context, definition.frameMembers);

        if (size(frameMemberBodies) != 4)
        {
            reportFeatureError(context, id, "Exactly four frame members must be selected");
            return;
        }

        // Validate that every selected body is a proper frame member by checking for
        // the frame profile attribute assigned by the Frame feature
        for (var memberIndex = 0; memberIndex < 4; memberIndex += 1)
        {
            const frameProfileQuery = qHasAttribute(frameMemberBodies[memberIndex], FRAME_ATTRIBUTE_PROFILE_NAME);
            if (isQueryEmpty(context, frameProfileQuery))
            {
                reportFeatureError(context, id, "All selections must be frame members created by the Frame feature");
                return;
            }
        }

        // Derive each member's sweep axis from its cap face centroids.
        // Using the centroid-to-centroid direction (rather than the raw cap face normal)
        // keeps the axis correct for both perpendicular cuts and mitered end cuts.
        var memberSweepAxis = makeArray(4);
        for (var memberIndex = 0; memberIndex < 4; memberIndex += 1)
        {
            memberSweepAxis[memberIndex] = getFrameSweepAxis(context, frameMemberBodies[memberIndex]);
        }

        // Verify that sequential members are touching (zero gap), confirming they form
        // a closed rectangular loop
        for (var memberIndex = 0; memberIndex < 4; memberIndex += 1)
        {
            const nextIndex = (memberIndex == 3) ? 0 : memberIndex + 1;
            const gapDistance = evDistance(context, {
                        "side0" : frameMemberBodies[memberIndex],
                        "side1" : frameMemberBodies[nextIndex]
                    }).distance;

            if (gapDistance != 0 * meter)
            {
                reportFeatureWarning(context, id, "Selections must be sequential and form a closed loop");
                return;
            }
        }

        // Compute the opening normal (the panel thickness direction) as the cross product
        // of two adjacent members' perpendicular sweep axes
        const sweepAxisCross = cross(memberSweepAxis[0], memberSweepAxis[1]);
        if (norm(sweepAxisCross) < PERPENDICULARITY_TOLERANCE)
        {
            reportFeatureError(context, id, "Members 1 and 2 must sweep in perpendicular directions; select four sequential adjacent members");
            return;
        }
        const openingNormal = normalize(sweepAxisCross);

        // For each member, separate its swept faces into two groups:
        //   frontBackSweptFaces : faces parallel to the opening plane (define panel thickness extents)
        //   sideSweptFaces      : the remaining faces (include the inner channel face and outer face)
        var memberSideSweptFaces = makeArray(4);
        for (var memberIndex = 0; memberIndex < 4; memberIndex += 1)
        {
            const allSweptFaces = qFrameSweptFace(frameMemberBodies[memberIndex]);
            const frontBackSweptFaces = qParallelPlanes(allSweptFaces, openingNormal, true);
            memberSideSweptFaces[memberIndex] = qSubtraction(allSweptFaces, frontBackSweptFaces);
        }

        // Compute the clear-opening edge geometry for each opposite pair.
        // evDistance between the side swept faces of opposite members automatically finds
        // the inner channel faces (they are the closest faces across the opening).
        //   Pair 0: members 0 and 2 (one axis of the opening)
        //   Pair 1: members 1 and 3 (perpendicular axis of the opening)
        var openingEdgeStart = makeArray(2);
        var openingEdgeEnd = makeArray(2);
        var openingEdgeMid = makeArray(2);
        var openingEdgeMidPlane = makeArray(2);

        for (var pairIndex = 0; pairIndex < 2; pairIndex += 1)
        {
            const oppositeMemberIndex = pairIndex + 2;
            const distanceResult = evDistance(context, {
                        "side0" : memberSideSweptFaces[pairIndex],
                        "side1" : memberSideSweptFaces[oppositeMemberIndex]
                    });

            openingEdgeStart[pairIndex] = distanceResult.sides[0].point;
            openingEdgeEnd[pairIndex] = distanceResult.sides[1].point;
            openingEdgeMid[pairIndex] = (openingEdgeStart[pairIndex] + openingEdgeEnd[pairIndex]) / 2;
            openingEdgeMidPlane[pairIndex] = plane(
                openingEdgeMid[pairIndex],
                normalize(openingEdgeEnd[pairIndex] - openingEdgeStart[pairIndex]));
        }

        // Locate the front and back faces of member 0 (swept faces parallel to the opening
        // plane) to establish the center plane of the opening in the thickness direction
        const member0AllSweptFaces = qFrameSweptFace(frameMemberBodies[0]);
        const member0FrontBackFaces = evaluateQuery(context, qParallelPlanes(member0AllSweptFaces, openingNormal, true));

        if (size(member0FrontBackFaces) < 2)
        {
            reportFeatureError(context, id, "Could not determine the panel plane from frame member swept faces; verify the frame profile has planar faces parallel to the opening");
            return;
        }

        // Bisect the front-to-back distance to find the true center plane of the opening
        const frontBackDistanceResult = evDistance(context, {
                    "side0" : member0FrontBackFaces[0],
                    "side1" : member0FrontBackFaces[1]
                });
        const openingFrontCenter = frontBackDistanceResult.sides[0].point;
        const openingBackCenter = frontBackDistanceResult.sides[1].point;
        const centerPlane = plane((openingFrontCenter + openingBackCenter) / 2, openingNormal);

        // Project all edge points onto the center plane, collapsing out the thickness
        // component. Also project each pair's points onto the midplane of the other pair
        // so corner points land at the true intersections of the opening edges.
        var openingEdgeAxis = makeArray(2);
        for (var pairIndex = 0; pairIndex < 2; pairIndex += 1)
        {
            const otherPairIndex = pairIndex == 0 ? 1 : 0;

            openingEdgeStart[pairIndex] = project(openingEdgeMidPlane[otherPairIndex], openingEdgeStart[pairIndex]);
            openingEdgeEnd[pairIndex]   = project(openingEdgeMidPlane[otherPairIndex], openingEdgeEnd[pairIndex]);

            openingEdgeStart[pairIndex] = project(centerPlane, openingEdgeStart[pairIndex]);
            openingEdgeEnd[pairIndex]   = project(centerPlane, openingEdgeEnd[pairIndex]);

            openingEdgeAxis[pairIndex] = normalize(openingEdgeEnd[pairIndex] - openingEdgeStart[pairIndex]);
        }

        // Compute diagonally opposite corners of the panel cuboid.
        // Each corner sits at the intersection of both opening-edge planes, offset by
        // half the panel thickness in the opening normal direction.
        const panelCorner0 = project(plane(openingEdgeStart[0], openingEdgeAxis[0]), openingEdgeStart[1]) +
            (definition.thickness / 2) * openingNormal;

        const panelCorner1 = project(plane(openingEdgeEnd[0], openingEdgeAxis[0]), openingEdgeEnd[1]) -
            (definition.thickness / 2) * openingNormal;

        // Create the panel cuboid sized to the clear opening
        fCuboid(context, id + "panel", { "corner1" : panelCorner0, "corner2" : panelCorner1 });

        // Identify the four panel side faces (not parallel to the center plane).
        // These are the faces that will be extended into the frame channels.
        const allPanelFaces = qCreatedBy(id + "panel", EntityType.FACE);
        const panelFrontBackFaces = qParallelPlanes(allPanelFaces, centerPlane, true);
        const panelSideFaces = qSubtraction(allPanelFaces, panelFrontBackFaces);

        // Offset each side face inward by (channel depth - edge gap) to seat the panel
        // inside the frame channels while respecting the specified clearance gap
        const channelInsertionDepth = definition.depth - definition.gap;

        opOffsetFace(context, id + "offsetFace1", {
                    "moveFaces" : panelSideFaces,
                    "offsetDistance" : channelInsertionDepth
                });

        // Calculate the final panel dimensions (clear opening + channel insertion on both sides)
        const panelDim1 = roundToPrecision(
            (norm(openingEdgeEnd[0] - openingEdgeStart[0]) + 2 * channelInsertionDepth) / meter, DIMENSION_ROUNDING_PRECISION);
        const panelDim2 = roundToPrecision(
            (norm(openingEdgeEnd[1] - openingEdgeStart[1]) + 2 * channelInsertionDepth) / meter, DIMENSION_ROUNDING_PRECISION);

        // Apply the formatted name to the finished panel body
        setProperty(context, {
                    "entities" : qCreatedBy(id + "panel", EntityType.BODY),
                    "propertyType" : PropertyType.NAME,
                    "value" : buildPanelName(definition.namePrefix, panelDim1, panelDim2, definition.mm)
                });
    });
