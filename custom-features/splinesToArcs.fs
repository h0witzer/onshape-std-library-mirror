FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");

import(path : "onshape/std/feature.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/path.fs", version : "2679.0");
import(path : "onshape/std/valueBounds.fs", version : "2679.0");
import(path : "onshape/std/math.fs", version : "2679.0");
import(path : "onshape/std/units.fs", version : "2679.0");

// Tangent-arc construction primitives (opArc3d, opTangentArc3d, getTangentArcData)
// live in the custom feature 3dArcUtils.fs, imported here by document id.
import(path : "97730412fb61f53dcd526c08", version : "e7466f17a5e8f9cda49e262e");


/**
 * Splines to arcs.
 *
 * Approximates one or more input curves with the fewest tangent circular arcs
 * that stay within a chosen deviation tolerance, producing a tangent-continuous
 * arc chain.  This is Method C: the breakpoints fully determine the chain (each
 * arc's end tangent seeds the next arc's start tangent), so a dynamic-programming
 * search over candidate breakpoints finds a globally minimal-count chain rather
 * than a greedy one.
 *
 * Both ends are tangent to the source: arc one is seeded with the curve's start
 * tangent, and chains whose terminal arc does not match the curve's end tangent
 * within the angular tolerance are rejected.  Multiple curves are solved in input
 * order and stitched so each curve's end tangent feeds the next, yielding one
 * continuous chain across the whole selection.  Intended for frozen import
 * geometry, so the per-rebuild solve cost is acceptable.
 */
annotation { "Feature Type Name" : "Splines to arcs" }
export const splinesToArcs = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Curves", "Filter" : EntityType.EDGE || (EntityType.BODY && BodyType.WIRE && SketchObject.NO) }
        definition.curves is Query;

        annotation { "Name" : "Maximum deviation" }
        isLength(definition.tolerance, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation { "Name" : "Sample count" }
        // More samples = better breakpoint resolution and fidelity, but the DP is
        // O(sampleCount^2) arc evaluations; the original feature used 60.
        isInteger(definition.sampleCount, { (unitless) : [20, 80, 400] } as IntegerBoundSpec);

        annotation { "Name" : "End tangent tolerance" }
        isAngle(definition.endTangentTolerance, { (degree) : [0.1, 5, 30] } as AngleBoundSpec);
    }
    {
        approximateCurvesWithArcs(context, id, definition);
    }, { tolerance : 0.001 * meter, sampleCount : 80, endTangentTolerance : 5 * degree });

// =================== Top-level solve and chain construction ===================

/**
 * Solve each connected source path with a tangent-arc DP and build the arcs.
 *
 * Fields used from `definition`: curves (Query), tolerance (ValueWithUnits),
 * sampleCount (number), endTangentTolerance (ValueWithUnits angle).
 * Reports the worst measured deviation across the whole chain as a computed
 * parameter so it can be surfaced to the user.
 */
function approximateCurvesWithArcs(context is Context, id is Id, definition is map)
{
    const paths = constructPaths(context, qOwnedByBody(definition.curves, EntityType.EDGE), {});
    if (size(paths) == 0)
        throw regenError(ErrorStringEnum.INVALID_INPUT, ["curves"], definition.curves);

    var carriedTangent = undefined;     // end tangent handed off between curves
    var previousArcEdge = undefined;    // last created edge, for opTangentArc3d
    var arcCount = 0;

    for (var pathIndex = 0; pathIndex < size(paths); pathIndex += 1)
    {
        const solved = solveTangentArcChain(context, paths[pathIndex], definition, carriedTangent);
        const arcs = solved.arcs;

        for (var i = 0; i < size(arcs); i += 1)
        {
            const arc = arcs[i];
            const arcId = id + ("arc" ~ arcCount);
            if (previousArcEdge == undefined)
                opArc3d(context, arcId, { "start" : arc.start, "mid" : arc.mid, "end" : arc.end });
            else
                opTangentArc3d(context, arcId, { "start" : arc.start, "tangentEdge" : previousArcEdge, "end" : arc.end });

            previousArcEdge = qOwnedByBody(qCreatedBy(arcId, EntityType.BODY), EntityType.EDGE);
            arcCount += 1;
        }
        carriedTangent = solved.endTangent;
    }

    // Validate the realized chain against the source once when it is a single
    // continuous path: this is the true server-side deviation, kept out of the
    // inner solve loop for cost.  Disjoint multi-curve inputs skip the report.
    var maxDeviation = 0 * meter;
    if (size(paths) == 1)
        maxDeviation = evMaxPathDeviation(context, {
                    "side1" : qOwnedByBody(qCreatedBy(id, EntityType.BODY), EntityType.EDGE),
                    "side2" : qOwnedByBody(definition.curves, EntityType.EDGE) }).deviation;

    setFeatureComputedParameter(context, id, { "name" : "arcCount", "value" : arcCount });
    setFeatureComputedParameter(context, id, { "name" : "maxDeviation", "value" : maxDeviation });
}

// =================== Dynamic-programming arc-chain solver ===================

/**
 * Find a minimal-count tangent-arc chain for a single path.
 *
 * Inputs: path (Path), definition (feature map), incomingTangent (Vector or
 * undefined for the free start tangent).  Bulk-samples the path once, then runs
 * a forward DP keyed on breakpoint index, carrying the deterministic incoming
 * tangent of the best chain reaching each breakpoint.  Tie-breaks equal counts
 * on summed deviation for smoothness, and rejects terminal arcs that miss the
 * path end tangent by more than endTangentTolerance.
 * Returns { breakpoints, samplePoints, arcs, endTangent }.
 */
function solveTangentArcChain(context is Context, path is Path, definition is map, incomingTangent) returns map
{
    const sampleCount = definition.sampleCount;
    var parameters = makeArray(sampleCount + 1, 0);
    for (var i = 0; i <= sampleCount; i += 1)
        parameters[i] = i / sampleCount;

    const tangentLines = evPathTangentLines(context, path, parameters).tangentLines;
    var samplePoints = makeArray(sampleCount + 1, undefined);
    for (var i = 0; i <= sampleCount; i += 1)
        samplePoints[i] = tangentLines[i].origin;

    const startTangent = incomingTangent != undefined ? incomingTangent : normalize(tangentLines[0].direction);
    const endTangent = normalize(tangentLines[sampleCount].direction);

    // bestCount[i]   : fewest arcs to reach breakpoint i (undefined if unreached)
    // bestDeviation  : summed deviation of that best chain (smoothness tie-break)
    // bestPrevious[i]: predecessor breakpoint, for reconstruction
    // tangentAt[i]   : incoming tangent of the best chain arriving at i
    var bestCount = makeArray(sampleCount + 1, undefined);
    var bestDeviation = makeArray(sampleCount + 1, 0 * meter);
    var bestPrevious = makeArray(sampleCount + 1, undefined);
    var tangentAt = makeArray(sampleCount + 1, undefined);
    bestCount[0] = 0;
    tangentAt[0] = startTangent;

    for (var i = 0; i < sampleCount; i += 1)
    {
        if (bestCount[i] == undefined)
            continue;
        const tangent = tangentAt[i];
        for (var j = i + 1; j <= sampleCount; j += 1)
        {
            const arc = getTangentArcData(samplePoints[i], tangent, samplePoints[j]);
            if (!arc.valid)
                continue;
            const deviation = maxArcDeviation(samplePoints, i, j, arc);
            if (deviation > definition.tolerance)
                continue;
            const arcEndTangent = arcEndTangentOf(arc, samplePoints[j]);
            // Reject the final arc unless it lands on the path end tangent.
            if (j == sampleCount && abs(angleBetween(arcEndTangent, endTangent)) > definition.endTangentTolerance)
                continue;
            const count = bestCount[i] + 1;
            const summed = bestDeviation[i] + deviation;
            if (bestCount[j] == undefined || count < bestCount[j] ||
                (count == bestCount[j] && summed < bestDeviation[j]))
            {
                bestCount[j] = count;
                bestDeviation[j] = summed;
                bestPrevious[j] = i;
                tangentAt[j] = arcEndTangent;
            }
        }
    }

    if (bestCount[sampleCount] == undefined)
        throw regenError("No arc chain meets the deviation tolerance; increase tolerance or sample count.", ["tolerance", "sampleCount"]);

    // Reconstruct breakpoints from the end backwards.
    var breakpoints = [sampleCount];
    var cursor = sampleCount;
    while (cursor != 0)
    {
        cursor = bestPrevious[cursor];
        breakpoints = append(breakpoints, cursor);
    }
    breakpoints = reverse(breakpoints);

    var arcs = [];
    var tangent = startTangent;
    for (var i = 0; i < size(breakpoints) - 1; i += 1)
    {
        const start = samplePoints[breakpoints[i]];
        const end = samplePoints[breakpoints[i + 1]];
        const arc = getTangentArcData(start, tangent, end);
        arcs = append(arcs, { "start" : start, "mid" : arc.mid, "end" : end });
        tangent = arcEndTangentOf(arc, end);
    }

    return { "breakpoints" : breakpoints, "samplePoints" : samplePoints, "arcs" : arcs, "endTangent" : tangent };
}

// =================== Geometry helpers ===================

/**
 * Tangent direction at the end of an arc.  Inputs: arcData from getTangentArcData
 * (center, radius, normal), endPoint (Vector).  Output: unit Vector pointing along
 * the arc at its end, used as the next arc's incoming tangent.
 */
function arcEndTangentOf(arcData is map, endPoint is Vector) returns Vector
{
    return normalize(cross(arcData.normal, endPoint - arcData.center));
}

/**
 * Worst deviation between path samples in [startIndex, endIndex] and a candidate
 * arc.  Inputs: samplePoints (array of Vector), startIndex/endIndex (number),
 * arcData (center, radius, normal).  Output: max distance as ValueWithUnits.
 * Used to score candidate arcs during the DP search.  Manual radial/axial math
 * is deliberate: no std query measures a sampled point against an unbuilt arc, and
 * building a wire per candidate would be far too costly.  The chosen chain is
 * validated with evMaxPathDeviation afterward.
 */
function maxArcDeviation(samplePoints is array, startIndex is number, endIndex is number, arcData is map) returns ValueWithUnits
{
    var maxDeviation = 0 * meter;
    for (var k = startIndex + 1; k < endIndex; k += 1)
    {
        const offset = samplePoints[k] - arcData.center;
        const axial = dot(offset, arcData.normal);
        const planar = offset - axial * arcData.normal;
        const radial = abs(norm(planar) - arcData.radius);
        const deviation = sqrt(radial * radial + axial * axial);
        if (deviation > maxDeviation)
            maxDeviation = deviation;
    }
    return maxDeviation;
}
