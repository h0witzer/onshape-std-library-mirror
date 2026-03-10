FeatureScript 2892;
import(path : "onshape/std/common.fs", version : "2892.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2892.0");

/**
 * Flatten Full Round Sketch
 *
 * Takes a source sketch containing full-round (approximately 180-degree) arcs and
 * produces a new sketch on the same plane where each such arc is replaced by a flat
 * line segment. The flat is positioned at the apex of the original arc.
 *
 * The two lines adjacent to each full-round arc are extended until they reach the
 * "apex plane" — a line through the arc midpoint perpendicular to the arc radius at
 * that midpoint. The flat replacement line connects the two new extension endpoints,
 * so its midpoint sits at the arc's apex. This prepares the sketch for re-filleting
 * at user-chosen radii.
 *
 * Algorithm for each detected full-round arc:
 *   1. Sample the arc at t=0 (start p1), t=0.5 (apex pm), t=1 (end p2).
 *   2. The tangent at pm defines the apex plane (perpendicular = apex direction).
 *   3. The tangent at p1 points "toward the apex" — extend in that direction until
 *      the apex plane is reached to get new endpoint np1.
 *   4. The tangent at p2 points "away from the apex" — negate it, then extend
 *      from p2 toward the apex plane to get new endpoint np2.
 *   5. Emit a flat line np1 → np2 instead of the arc.
 *   6. Any adjacent edge whose original endpoint was at p1 or p2 is redrawn with
 *      its endpoint moved to np1 or np2.
 *
 * Input parameters:
 *   sourceSketch   : The sketch whose full-round arcs should be flattened.
 *   thresholdAngle : Minimum arc central angle for an arc to be treated as a full
 *                    round. Default 150 deg; accepts 90–180 deg.
 *
 * Output:
 *   A new sketch on the same plane as the source sketch, with full-round arcs
 *   replaced by flat line segments and all other geometry preserved.
 */

// Minimum position-match tolerance when identifying shared vertices (meters).
const VERTEX_MATCH_TOLERANCE = 1e-6 * meter;

// ----------------------------------------------------------------------------------
// Feature definition
// ----------------------------------------------------------------------------------

annotation { "Feature Type Name" : "Flatten Full Round Sketch" }
export const flattenFullRoundSketch = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Source Sketch", "Filter" : SketchObject.YES, "MaxNumberOfPicks" : 1 }
        definition.sourceSketch is FeatureList;

        annotation { "Name" : "Minimum Arc Angle" }
        isAngle(definition.thresholdAngle, { (degree) : [90, 150, 180] } as AngleBoundSpec);
    }
    {
        // Validate source sketch selection.
        const sketchKeys = keys(definition.sourceSketch);
        if (size(sketchKeys) == 0)
            throw regenError("Select a source sketch.", ["sourceSketch"]);

        const sketchKey = sketchKeys[0];

        // Collect all non-construction wire edges from the source sketch.
        const sourceEdges = evaluateQuery(context, qBodyType(
            qConstructionFilter(qCreatedBy(sketchKey, EntityType.EDGE), ConstructionObject.NO),
            BodyType.WIRE));

        if (size(sourceEdges) == 0)
            throw regenError("The selected sketch contains no geometry.", ["sourceSketch"]);

        // Obtain the sketch plane from the first edge.
        const sketchPlane = evOwnerSketchPlane(context, { "entity" : sourceEdges[0] });

        // Precompute the sketch plane's y-axis once.
        const planeYAxis = cross(sketchPlane.normal, sketchPlane.x);

        // ------------------------------------------------------------------
        // Pass 1 – identify full-round arcs and compute their replacements.
        // ------------------------------------------------------------------
        // fullRoundData is an array of maps:
        //   { "arcTransientId", "p1_3d", "p2_3d", "np1_2d", "np2_2d" }
        var fullRoundData = [];

        for (var sourceEdge in sourceEdges)
        {
            const curveDefinition = evCurveDefinition(context, { "edge" : sourceEdge });

            if (curveDefinition.curveType != CurveType.CIRCLE)
                continue;

            // Sample the arc at start (t=0), apex (t=0.5), and end (t=1).
            const tangentAtStart = evEdgeTangentLine(context, { "edge" : sourceEdge, "parameter" : 0 });
            const tangentAtMid   = evEdgeTangentLine(context, { "edge" : sourceEdge, "parameter" : 0.5 });
            const tangentAtEnd   = evEdgeTangentLine(context, { "edge" : sourceEdge, "parameter" : 1 });

            const arcStart  = tangentAtStart.origin;
            const arcApex   = tangentAtMid.origin;
            const arcEnd    = tangentAtEnd.origin;
            const arcRadius = curveDefinition.radius;

            // Determine whether this arc spans approximately the threshold angle.
            // For a central angle theta: chord = 2*r*sin(theta/2),
            // so chord/(2*r) = sin(theta/2).  Compare to sin(threshold/2).
            const chordLength    = norm(arcEnd - arcStart);
            const chordOverDiam  = chordLength / (2 * arcRadius);
            const thresholdRatio = sin(definition.thresholdAngle / 2);

            if (chordOverDiam < thresholdRatio)
                continue;

            // ---- Compute the flat replacement endpoints (np1, np2) in 2D. ----
            //
            // Apex plane: passes through pm_2d and is perpendicular to the radius
            // at pm.  The arc tangent at pm is tangent to the arc; the perpendicular
            // to it (the apex direction) is the local radius direction.
            //
            // Apex direction: rotate the tangent at mid 90 degrees CCW in 2D.
            //   tangentAtMid_2d = (tx, ty)  =>  apexDir_2d = (-ty, tx)
            // (Sign does not matter for the apex-plane equation dot(q-pm, apexDir)=0.)
            //
            // Wall extension directions:
            //   - At p1: tangent points toward the apex => extend in tangent direction.
            //   - At p2: tangent points away from the apex => extend in -tangent dir.
            //
            // Intersection parameter:
            //   s = dot(pm_2d - endpoint_2d, apexDir_2d) / dot(extensionDir_2d, apexDir_2d)

            const pm_2d = worldToPlane(sketchPlane, arcApex);
            const p1_2d = worldToPlane(sketchPlane, arcStart);
            const p2_2d = worldToPlane(sketchPlane, arcEnd);

            // Project the arc's tangent at the midpoint into 2D sketch coordinates.
            const midTangent3d = tangentAtMid.direction;
            const midTangent2d = vector(dot(sketchPlane.x, midTangent3d), dot(planeYAxis, midTangent3d));

            // Apex direction = 90-degree rotation of the midpoint tangent (CCW).
            const apexDir2d = vector(-midTangent2d[1], midTangent2d[0]);

            // Project the endpoint tangent directions into 2D.
            const startTangent3d = tangentAtStart.direction;
            const endTangent3d   = tangentAtEnd.direction;
            const t1_2d = vector(dot(sketchPlane.x, startTangent3d), dot(planeYAxis, startTangent3d));
            // Negate the end tangent: the end tangent goes away from the apex, so
            // the extension back toward the apex uses the negated direction.
            const t2ext_2d = vector(-dot(sketchPlane.x, endTangent3d), -dot(planeYAxis, endTangent3d));

            const denom1 = dot(t1_2d, apexDir2d);
            const denom2 = dot(t2ext_2d, apexDir2d);

            // Default to the original arc endpoints if the geometry is degenerate
            // (tangent exactly parallel to the apex plane — vanishingly rare).
            var np1_2d = p1_2d;
            var np2_2d = p2_2d;

            if (abs(denom1) > 1e-9 && abs(denom2) > 1e-9)
            {
                const s1 = dot(pm_2d - p1_2d, apexDir2d) / denom1;
                const s2 = dot(pm_2d - p2_2d, apexDir2d) / denom2;
                np1_2d = p1_2d + s1 * t1_2d;
                np2_2d = p2_2d + s2 * t2ext_2d;
            }

            // Store the transient entity ID for fast edge-identity lookup later.
            fullRoundData = append(fullRoundData,
                {
                    "arcTransientId" : sourceEdge.transientId,
                    "p1_3d"          : arcStart,
                    "p2_3d"          : arcEnd,
                    "np1_2d"         : np1_2d,
                    "np2_2d"         : np2_2d
                });
        }

        // ------------------------------------------------------------------
        // Pass 2 – build the new sketch.
        // ------------------------------------------------------------------
        const newSketch = newSketchOnPlane(context, id + "flattenedSketch", {
                    "sketchPlane" : sketchPlane
                });

        var sketchLineCount = 0;
        var sketchArcCount  = 0;

        for (var sourceEdge in sourceEdges)
        {
            // Skip full-round arcs — they are replaced in the step below.
            if (isFullRoundArc(sourceEdge, fullRoundData))
                continue;

            const curveDefinition = evCurveDefinition(context, { "edge" : sourceEdge });

            // Get the 3D start and end positions of this edge.
            const startTangent = evEdgeTangentLine(context, { "edge" : sourceEdge, "parameter" : 0 });
            const endTangent   = evEdgeTangentLine(context, { "edge" : sourceEdge, "parameter" : 1 });

            // Remap any endpoint that was originally on a full-round arc
            // to the corresponding extended position.
            const start_2d = remapEndpoint(startTangent.origin, sketchPlane, fullRoundData);
            const end_2d   = remapEndpoint(endTangent.origin, sketchPlane, fullRoundData);

            if (curveDefinition.curveType == CurveType.LINE)
            {
                skLineSegment(newSketch, "copiedLine" ~ sketchLineCount, {
                            "start" : start_2d,
                            "end"   : end_2d
                        });
                sketchLineCount += 1;
            }
            else if (curveDefinition.curveType == CurveType.CIRCLE)
            {
                // Non-full-round arc: reproduce using start / mid / end points.
                // (Endpoint remapping is applied; the midpoint is always interior
                // to the arc and is never a shared full-round arc vertex.)
                const midTangent = evEdgeTangentLine(context, { "edge" : sourceEdge, "parameter" : 0.5 });
                const mid_2d     = worldToPlane(sketchPlane, midTangent.origin);

                skArc(newSketch, "copiedArc" ~ sketchArcCount, {
                            "start" : start_2d,
                            "mid"   : mid_2d,
                            "end"   : end_2d
                        });
                sketchArcCount += 1;
            }
            else
            {
                // Spline or other curve type: approximate with a 32-segment polyline.
                var polylinePoints = [];
                for (var polylineSegmentIndex = 0; polylineSegmentIndex <= 32; polylineSegmentIndex += 1)
                {
                    const parameter  = polylineSegmentIndex / 32;
                    const sampleTangent = evEdgeTangentLine(context, {
                                "edge"      : sourceEdge,
                                "parameter" : parameter
                            });
                    polylinePoints = append(polylinePoints,
                        worldToPlane(sketchPlane, sampleTangent.origin));
                }
                skPolyline(newSketch, "copiedCurve" ~ sketchLineCount, { "points" : polylinePoints });
                sketchLineCount += 1;
            }
        }

        // Emit a flat replacement line for each detected full-round arc.
        for (var arcData in fullRoundData)
        {
            skLineSegment(newSketch, "flatReplacement" ~ sketchLineCount, {
                        "start" : arcData.np1_2d,
                        "end"   : arcData.np2_2d
                    });
            sketchLineCount += 1;
        }

        skSolve(newSketch);

        // Report the number of arcs that were flattened.
        reportFeatureInfo(context, id, "Flattened " ~ toString(size(fullRoundData)) ~
            (size(fullRoundData) == 1 ? " full-round arc." : " full-round arcs."));
    });

// ----------------------------------------------------------------------------------
// Helper functions
// ----------------------------------------------------------------------------------

/**
 * Returns true if the given edge was identified as a full-round arc during pass 1.
 * Comparison uses the transient entity ID stored in fullRoundData.
 *
 * Parameters:
 *   candidateEdge : Query — the edge to test
 *   fullRoundData : array — replacement records from pass 1
 *
 * Returns:
 *   boolean — true when candidateEdge is a known full-round arc
 */
function isFullRoundArc(candidateEdge is Query, fullRoundData is array) returns boolean
{
    if (candidateEdge.queryType != QueryType.TRANSIENT)
        return false;
    for (var arcData in fullRoundData)
    {
        if (candidateEdge.transientId == arcData.arcTransientId)
            return true;
    }
    return false;
}

/**
 * Converts a 3D world-space position to 2D sketch-plane coordinates, replacing
 * the position with the extended apex-plane endpoint if the position coincides
 * with an original full-round arc endpoint (within VERTEX_MATCH_TOLERANCE).
 *
 * Parameters:
 *   position3d    : Vector — 3D world-space point to convert / remap
 *   sketchPlane   : Plane  — the target sketch plane
 *   fullRoundData : array  — replacement records from pass 1
 *
 * Returns:
 *   Vector — 2D point in sketch-plane coordinates
 */
function remapEndpoint(position3d is Vector, sketchPlane is Plane, fullRoundData is array) returns Vector
{
    for (var arcData in fullRoundData)
    {
        if (norm(position3d - arcData.p1_3d) < VERTEX_MATCH_TOLERANCE)
            return arcData.np1_2d;
        if (norm(position3d - arcData.p2_3d) < VERTEX_MATCH_TOLERANCE)
            return arcData.np2_2d;
    }
    return worldToPlane(sketchPlane, position3d);
}
