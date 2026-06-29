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
 * Both ends are strictly tangent to the source: arc one is seeded with the
 * curve's start tangent, and the chain is closed with a biarc whose terminal
 * tangent exactly equals the curve's end tangent, so there is no end-tangent
 * tolerance to trade off.  A single tangent arc through two fixed endpoints has
 * only its start tangent free, so it cannot also pin its end tangent; the closing
 * biarc adds the free junction that makes exact two-tangent closure possible.
 * Multiple curves are solved in input order and stitched so each curve's exact
 * end tangent feeds the next, yielding one continuous chain across the whole
 * selection.  Intended for frozen import geometry, so the per-rebuild solve cost
 * is acceptable.
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
    }
    {
        approximateCurvesWithArcs(context, id, definition);
    }, { tolerance : 0.001 * meter, sampleCount : 80 });

// =================== Top-level solve and chain construction ===================

/**
 * Solve each connected source path with a tangent-arc DP and build the arcs.
 *
 * Fields used from `definition`: curves (Query), tolerance (ValueWithUnits),
 * sampleCount (number).  Reports the worst measured deviation across the whole
 * chain as a computed parameter so it can be surfaced to the user.
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
 * tangent of the best chain reaching each breakpoint.  Interior breakpoints are
 * joined by single tangent arcs (start tangent fixed, end tangent free); the
 * chain is then closed by a biarc whose terminal tangent equals the path end
 * tangent exactly, so both ends are strictly tangent with no angular tolerance.
 * Tie-breaks equal counts on summed deviation for smoothness.
 * Returns { samplePoints, arcs, endTangent }, where endTangent is the exact path
 * end tangent.
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

    // Single tangent arcs reach the interior breakpoints; the terminal index
    // sampleCount is intentionally left for the strict biarc closure below.
    for (var i = 0; i < sampleCount; i += 1)
    {
        if (bestCount[i] == undefined)
            continue;
        const tangent = tangentAt[i];
        for (var j = i + 1; j < sampleCount; j += 1)
        {
            const arc = getTangentArcData(samplePoints[i], tangent, samplePoints[j]);
            if (!arc.valid)
                continue;
            const deviation = maxArcDeviation(samplePoints, i, j, arc);
            if (deviation > definition.tolerance)
                continue;
            const arcEndTangent = arcEndTangentOf(arc, samplePoints[j]);
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

    // Close from every reachable interior breakpoint with a biarc that hits the
    // path end tangent exactly; pick the closure that minimizes arc count, then
    // summed deviation.  The biarc adds two arcs.
    var closingFrom = undefined;
    var closingBiArc = undefined;
    var closingCount = undefined;
    var closingDeviation = 0 * meter;
    for (var i = 0; i < sampleCount; i += 1)
    {
        if (bestCount[i] == undefined)
            continue;
        const biArc = getBiArcData(samplePoints[i], tangentAt[i], samplePoints[sampleCount], endTangent);
        if (!biArc.valid)
            continue;
        const deviation = maxBiArcDeviation(samplePoints, i, sampleCount, biArc);
        if (deviation > definition.tolerance)
            continue;
        const count = bestCount[i] + 2;
        const summed = bestDeviation[i] + deviation;
        if (closingCount == undefined || count < closingCount ||
            (count == closingCount && summed < closingDeviation))
        {
            closingFrom = i;
            closingBiArc = biArc;
            closingCount = count;
            closingDeviation = summed;
        }
    }

    if (closingFrom == undefined)
        throw regenError("No arc chain meets the deviation tolerance; increase tolerance or sample count.", ["tolerance", "sampleCount"]);

    // Reconstruct interior breakpoints from the biarc start backwards.
    var breakpoints = [closingFrom];
    var cursor = closingFrom;
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
    // Strict end tangency: the closing biarc's two arcs land on the exact end tangent.
    arcs = append(arcs, { "start" : samplePoints[closingFrom], "mid" : closingBiArc.firstArc.mid, "end" : closingBiArc.junction });
    arcs = append(arcs, { "start" : closingBiArc.junction, "mid" : closingBiArc.secondArc.mid, "end" : samplePoints[sampleCount] });

    return { "samplePoints" : samplePoints, "arcs" : arcs, "endTangent" : endTangent };
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
        const deviation = pointArcDeviation(samplePoints[k], arcData);
        if (deviation > maxDeviation)
            maxDeviation = deviation;
    }
    return maxDeviation;
}

/**
 * Radial/axial distance of a single point to an unbuilt arc.  Inputs: point
 * (Vector), arcData (center, radius, normal).  Output: distance as ValueWithUnits.
 */
function pointArcDeviation(point is Vector, arcData is map) returns ValueWithUnits
{
    const offset = point - arcData.center;
    const axial = dot(offset, arcData.normal);
    const planar = offset - axial * arcData.normal;
    const radial = abs(norm(planar) - arcData.radius);
    return sqrt(radial * radial + axial * axial);
}

/**
 * Worst deviation between path samples in [startIndex, endIndex] and a closing
 * biarc.  Inputs: samplePoints (array of Vector), startIndex/endIndex (number),
 * biArc (firstArc, secondArc, junction).  Output: max distance as ValueWithUnits,
 * each sample scored against whichever of the two arcs it lies closer to.
 */
function maxBiArcDeviation(samplePoints is array, startIndex is number, endIndex is number, biArc is map) returns ValueWithUnits
{
    var maxDeviation = 0 * meter;
    for (var k = startIndex + 1; k < endIndex; k += 1)
    {
        const toFirst = pointArcDeviation(samplePoints[k], biArc.firstArc);
        const toSecond = pointArcDeviation(samplePoints[k], biArc.secondArc);
        const deviation = min(toFirst, toSecond);
        if (deviation > maxDeviation)
            maxDeviation = deviation;
    }
    return maxDeviation;
}

// Cosine-error threshold for the biarc junction bisection: the end tangent is
// considered exactly matched once 1 - dot(endDir, endTangent) falls below this.
const BIARC_TANGENT_MATCH_TOLERANCE = 1e-6;

/**
 * Compute the two-arc (biarc) chain that joins a start point with a fixed start
 * tangent to an end point with a fixed end tangent, hitting both tangents exactly.
 * Inputs: startPoint, startTangent, endPoint, endTangent (Vectors).  A single arc
 * cannot pin both tangents through fixed endpoints, so the free junction is slid
 * along the chord by bisection until the second arc's terminal tangent matches the
 * requested end tangent.  Output: { valid, junction, firstArc, secondArc } where
 * each arc is getTangentArcData output; valid is false if no junction yields two
 * buildable arcs that meet the end tangent.  Mirrors the bisection in custom
 * feature 3dArcUtils.fs opBiArc3d, but stays in math so the DP can score it cheaply.
 */
function getBiArcData(startPoint is Vector, startTangent is Vector, endPoint is Vector, endTangent is Vector) returns map
{
    const chord = endPoint - startPoint;
    var parameter = 0.5;
    var step = 0.25;
    var firstArc = { valid : false };
    var secondArc = { valid : false };
    var junction = startPoint;
    var matched = false;
    for (var i = 0; i < 40; i += 1)
    {
        junction = startPoint + parameter * chord;
        firstArc = getTangentArcData(startPoint, startTangent, junction);
        if (!firstArc.valid)
        {
            parameter += step;
            step *= 0.5;
            continue;
        }
        const firstTangent = arcEndTangentOf(firstArc, junction);
        secondArc = getTangentArcData(junction, firstTangent, endPoint);
        if (!secondArc.valid)
        {
            parameter -= step;
            step *= 0.5;
            continue;
        }
        const endDir = arcEndTangentOf(secondArc, endPoint);
        const diff = dot(endDir, endTangent) - 1;
        if (abs(diff) < BIARC_TANGENT_MATCH_TOLERANCE)
        {
            matched = true;
            break;
        }
        if (diff > 0)
            parameter += step;
        else
            parameter -= step;
        step *= 0.5;
    }
    if (!matched || !firstArc.valid || !secondArc.valid)
        return { valid : false };
    return { valid : true, "junction" : junction, "firstArc" : firstArc, "secondArc" : secondArc };
}