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
import(path : "97730412fb61f53dcd526c08", version : "e7466f17a5e8f9cda49e262e");


/**
 * Approximates a curve with tangent circular arcs.
 *
 * This feature subdivides a user selected curve into consecutive
 * arc segments.  The subdivision attempts to use the fewest number
 * of arcs possible while ensuring that the deviation between the
 * original curve and the arc spline stays under the specified
 * tolerance.
 */
annotation { "Feature Type Name" : "Arc approximation" }
export const arcApproximation = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Curve", "Filter" : EntityType.EDGE || (EntityType.BODY && BodyType.WIRE && SketchObject.NO) }
        definition.curve is Query;

        annotation { "Name" : "Maximum deviation" }
        isLength(definition.tolerance, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);
    }
    {
        approximateCurveWithArcs(context, id, definition.curve, definition.tolerance);
    }, { tolerance : 0.01 * meter });

/**
 * Find an optimal sequence of tangent arcs approximating the input curve.
 *
 * The algorithm samples the curve at regular parameter intervals and builds a
 * table of every arc segment that satisfies the deviation tolerance. A dynamic
 * programming search then picks the combination of segments that minimizes the
 * total number of arcs.
 */
function approximateCurveWithArcs(context is Context, id is Id, curve is Query, tolerance is ValueWithUnits)
{
    var path;
    try silent
    {
        path = constructPath(context, qOwnedByBody(curve, EntityType.EDGE), { "tolerance" : tolerance }).path;
    }
    catch (error)
    {
        throw regenError(error, curve);
    }

    // Sample the curve uniformly in parameter space so we can test all
    // potential segment end points.  More samples give better fidelity but
    // increase computation cost.
    const sampleCount = 60;
    var parameters = [];
    for (var i = 0; i <= sampleCount; i += 1)
        parameters = append(parameters, i / sampleCount);

    const tangentLines = evPathTangentLines(context, path, parameters).tangentLines;
    var samplePoints = [];
    var sampleTangents = [];
    for (var line in tangentLines)
    {
        samplePoints = append(samplePoints, line.origin);
        sampleTangents = append(sampleTangents, normalize(line.direction));
    }

    // Pre-compute arcs between every pair of sample points that meet the
    // deviation requirement.  Each entry contains the end index and the
    // pre-calculated arc parameters.
    const deviationSamples = 10;
    var candidateArcs = makeArray(sampleCount, undefined);
    for (var i = 0; i < sampleCount; i += 1)
    {
        var arcsFromI = [];
        for (var j = i + 1; j <= sampleCount; j += 1)
        {
            const data = tangentArcData(samplePoints[i], sampleTangents[i], samplePoints[j]);
            if (!data.valid)
                continue;

            const deviation = maxArcDeviation(context, path, parameters[i], parameters[j], data, deviationSamples);
            if (deviation <= tolerance)
                arcsFromI = append(arcsFromI, { "endIndex" : j, "data" : data });
        }
        candidateArcs[i] = arcsFromI;
    }

    // Dynamic programming arrays tracking the optimal arc count and next segment
    // for each sampled point.  bestCount[i] stores the minimal number of arcs
    // required to approximate the curve starting from sample i, while nextIndex
    // and nextData hold the chosen arc end index and tangent arc parameters.
    var bestCount = makeArray(sampleCount + 1, undefined);
    var nextIndex = makeArray(sampleCount + 1, undefined);
    var nextData = makeArray(sampleCount + 1, undefined);
    bestCount[sampleCount] = 0;

    // Iterate backwards so every "future" state has already been computed.
    for (var i = sampleCount - 1; i >= 0; i -= 1)
    {
        var best = undefined;
        var bestEnd = undefined;
        var bestArc = undefined;
        for (var candidate in candidateArcs[i])
        {
            const endIdx = candidate.endIndex;
            if (bestCount[endIdx] == undefined)
                continue;

            const total = bestCount[endIdx] + 1;
            if (best == undefined || total < best)
            {
                best = total;
                bestEnd = endIdx;
                bestArc = candidate.data;
            }
        }

        if (best != undefined)
        {
            bestCount[i] = best;
            nextIndex[i] = bestEnd;
            nextData[i] = bestArc;
        }
    }

    if (bestCount[0] == undefined)
        throw regenError(ErrorStringEnum.OPERATION_FAILED, curve);

    // Build the tangent arc wires following the optimal sequence starting at 0
    var previousArc = undefined;
    var arcIndex = 0;
    var current = 0;
    while (current < sampleCount)
    {
        const endIdx = nextIndex[current];
        if (endIdx == undefined)
            break;

        const startPoint = samplePoints[current];
        const endPoint = samplePoints[endIdx];
        const data = nextData[current];

        if (arcIndex == 0)
            previousArc = opArc3d(context, id + ("arc" ~ arcIndex),
                                   { "start" : startPoint, "mid" : data.mid, "end" : endPoint });
        else{
        const edge = qOwnedByBody(previousArc, EntityType.EDGE);
            previousArc = opTangentArc3d(context, id + ("arc" ~ arcIndex),
                                         { "start" : startPoint, "tangentEdge" : edge, "end" : endPoint });
        }

        arcIndex += 1;
        current = endIdx;
    }
}

/**
 * Compute parameters describing the circular arc defined by a
 * start point, a tangent direction at that point and an end point.
 *
 * Returns a map containing the center, radius, mid-point and plane
 * normal of the arc.  If the construction fails (points are collinear
 * or the sweep is undefined) `valid` will be false.
 */
function tangentArcData(start is Vector, tangentDir is Vector, end is Vector) returns map
{
    const chord = end - start;
    var radialDir = cross(tangentDir, cross(chord, tangentDir));
    if (norm(radialDir) < 1e-8 * meter)
        return { valid : false };
    radialDir = normalize(radialDir);
    var radius = squaredNorm(end - start) / (2 * dot(chord, radialDir));
    if (radius < 0 * meter)
    {
        radius = -radius;
        radialDir = -radialDir;
    }
    const center = start + radius * radialDir;
    const planeNormal = cross(tangentDir, radialDir);
    if (norm(planeNormal) < 1e-8)
        return { valid : false };
    const startVec = start - center;
    const endVec = end - center;
    var sweepAngle = atan2(dot(planeNormal, cross(startVec, endVec)), dot(startVec, endVec));
    if (sweepAngle < 0)
        sweepAngle += 2 * PI * radian;
    const midVec = rotationMatrix3d(normalize(planeNormal), sweepAngle / 2) * startVec;
    const mid = center + midVec;
    return { valid : true, ("center") : center, ("radius") : radius, normal : normalize(planeNormal), ("mid") : mid };
}

/**
 * Compute the maximum distance between the source path and a test arc
 * over the parameter range `[t0, t1]`.
 *
 * The arc is sampled at the given number of intermediate points.  The
 * deviation is measured in 3D space (both radial and axial components).
 */
function maxArcDeviation(context is Context, path is Path, t0 is number, t1 is number, arcData is map, samples is number) returns ValueWithUnits
{
    var maxDev = 0 * meter;
    for (var i = 1; i < samples; i += 1)
    {
        const t = t0 + (t1 - t0) * (i / samples);
        const p = evPathTangentLines(context, path, [t]).tangentLines[0].origin;
        const vec = p - arcData.center;
        const normalComponent = dot(vec, arcData.normal);
        const planar = vec - normalComponent * arcData.normal;
        const radialDev = abs(norm(planar) - arcData.radius);
        const pointDev = sqrt(radialDev * radialDev + normalComponent * normalComponent);
        if (pointDev > maxDev)
            maxDev = pointDev;
    }
    return maxDev;
}
