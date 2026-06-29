FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");

import(path : "onshape/std/feature.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/path.fs", version : "2679.0");
import(path : "onshape/std/sketch.fs", version : "2679.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2679.0");
import(path : "onshape/std/valueBounds.fs", version : "2679.0");
import(path : "onshape/std/math.fs", version : "2679.0");
import(path : "onshape/std/units.fs", version : "2679.0");

/**
 * Splines to arcs.
 *
 * Approximates one or more planar input curves with a chain of tangent-continuous
 * circular arcs.  The source is greedily split into spans that stay within tolerance
 * and never cross a curvature inflection; each span is seeded with a three-point arc
 * and its source midpoint is fixed and pinned onto the arc so the arc cannot flip to
 * the major (wrong-way) solution.  Consecutive arcs are joined coincident + tangent,
 * and short fixed construction lines force the first and last arcs to share the
 * source start and end tangents.  The solver satisfies all of those at once, so
 * tangency (including at the final arc) is solved rather than hand-built.
 *
 * Locked to the planar domain: every selected curve must lie in one plane within
 * the deviation tolerance or the feature errors.  Intended for frozen import
 * geometry, so the per-rebuild solve cost is acceptable.
 */
annotation { "Feature Type Name" : "Splines to arcs" }
export const splinesToArcs = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Curves", "Filter" : EntityType.EDGE || (EntityType.BODY && BodyType.WIRE) }
        definition.curves is Query;

        annotation { "Name" : "Maximum deviation" }
        isLength(definition.tolerance, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation { "Name" : "Sample count" }
        // Samples used to size arc spans and to score each span's deviation; higher
        // values track tight curvature better at the cost of more sketch entities.
        isInteger(definition.sampleCount, { (unitless) : [20, 80, 400] } as IntegerBoundSpec);
    }
    {
        approximateCurvesWithArcs(context, id, definition);
    }, { tolerance : 0.001 * meter, sampleCount : 80 });

// =================== Top-level solve and sketch construction ===================

/**
 * Sample each source path, fit a common plane, build a tangent-arc sketch, and
 * solve it.  Fields used from `definition`: curves (Query), tolerance
 * (ValueWithUnits), sampleCount (number).  Reports the realized worst deviation
 * against the source as a computed parameter for a single continuous selection.
 */
function approximateCurvesWithArcs(context is Context, id is Id, definition is map)
{
    // Accept either directly selected edges or whole wire/sketch bodies: union the
    // edges the selection itself names with the edges owned by any selected body.
    const edges = qUnion([qEntityFilter(definition.curves, EntityType.EDGE),
                qOwnedByBody(definition.curves, EntityType.EDGE)]);
    const paths = constructPaths(context, edges, {});
    if (size(paths) == 0)
        throw regenError(ErrorStringEnum.INVALID_INPUT, ["curves"], definition.curves);

    // Sample every path up front; sample points seed and size the arcs.
    var pathSamples = [];
    for (var pathIndex = 0; pathIndex < size(paths); pathIndex += 1)
        pathSamples = append(pathSamples, samplePath(context, paths[pathIndex], definition.sampleCount));

    // Fit a single plane to all samples and lock to the planar domain.
    const sketchPlane = fitPlaneToSamples(pathSamples);
    if (sketchPlane == undefined)
        throw regenError("Selected curves do not lie in a single plane; this feature only supports planar input.", ["curves"]);
    for (var pathIndex = 0; pathIndex < size(pathSamples); pathIndex += 1)
    {
        if (maxPlaneDeviation(sketchPlane, pathSamples[pathIndex]) > definition.tolerance)
            throw regenError("Selected curves are not planar within the deviation tolerance.", ["curves", "tolerance"]);
    }

    var sketch = newSketchOnPlane(context, id + "splineToArcsSketch", { "sketchPlane" : sketchPlane });
    var arcCount = 0;
    for (var pathIndex = 0; pathIndex < size(paths); pathIndex += 1)
        arcCount += addTangentArcChain(sketch, sketchPlane, pathSamples[pathIndex], definition.tolerance, arcCount);
    skSolve(sketch);

    // True realized deviation, reported only for a single continuous selection.
    // S-shaped or self-touching paths can be rejected by evMaxPathDeviation
    // (CONSTRUCT_PATH_EDGES_OVERLAP); the deviation is informational, so skip it
    // gracefully rather than fail the feature.
    var maxDeviation = 0 * meter;
    if (size(paths) == 1)
    {
        try
        {
            maxDeviation = evMaxPathDeviation(context, {
                        "side1" : qOwnedByBody(qCreatedBy(id + "splineToArcsSketch", EntityType.BODY), EntityType.EDGE),
                        "side2" : edges }).deviation;
        }
    }

    setFeatureComputedParameter(context, id, { "name" : "arcCount", "value" : arcCount });
    setFeatureComputedParameter(context, id, { "name" : "maxDeviation", "value" : maxDeviation });
}

// =================== Arc-span construction and constraints ===================

/**
 * Add one path's tangent-arc span chain to a sketch and constrain it.  Inputs:
 * sketch (Sketch), plane (Plane), samples (samplePath map), tolerance
 * (ValueWithUnits), startCount (number, running arc index for unique ids).  Greedily
 * groups samples into spans within tolerance that do not cross an inflection, places
 * one three-point arc per span, fixes each span midpoint onto its arc to lock the
 * bow direction, then joins arcs coincident + tangent and pins both source endpoints
 * and tangents with fixed lines.  Output: number of arcs added.
 */
function addTangentArcChain(sketch is Sketch, plane is Plane, samples is map, tolerance is ValueWithUnits, startCount is number) returns number
{
    const breakpoints = segmentSamples(samples.worldPoints, tolerance, plane.normal);
    const arcCount = size(breakpoints) - 1;

    for (var arcIndex = 0; arcIndex < arcCount; arcIndex += 1)
    {
        const startSample = breakpoints[arcIndex];
        const endSample = breakpoints[arcIndex + 1];
        const midSample = floor((startSample + endSample) / 2);
        const arcId = "arc" ~ (startCount + arcIndex);
        skArc(sketch, arcId, {
                    "start" : worldToPlane(plane, samples.worldPoints[startSample]),
                    "mid" : worldToPlane(plane, samples.worldPoints[midSample]),
                    "end" : worldToPlane(plane, samples.worldPoints[endSample]) });

        // Fix a sketch point on the source midpoint and pull the arc through it. This
        // pins which side the arc bows, so tangency can never be satisfied by the
        // major arc swinging the long way around an inflection.
        const midId = arcId ~ "Mid";
        skPoint(sketch, midId, { "position" : worldToPlane(plane, samples.worldPoints[midSample]) });
        skConstraint(sketch, midId ~ "Fix", { "constraintType" : ConstraintType.FIX, "localFirst" : midId });
        skConstraint(sketch, midId ~ "OnArc", { "constraintType" : ConstraintType.COINCIDENT,
                    "localFirst" : midId, "localSecond" : arcId });

        // Each arc's start meets the previous arc's end, tangentially.
        if (arcIndex > 0)
        {
            const previousArc = "arc" ~ (startCount + arcIndex - 1);
            skConstraint(sketch, arcId ~ "coincident", {
                        "constraintType" : ConstraintType.COINCIDENT,
                        "localFirst" : previousArc ~ ".end", "localSecond" : arcId ~ ".start" });
            skConstraint(sketch, arcId ~ "tangent", {
                        "constraintType" : ConstraintType.TANGENT,
                        "localFirst" : previousArc, "localSecond" : arcId });
        }
    }

    // Pin both source endpoints and seed fixed construction lines along the source
    // tangents so the solver makes the first and last arcs strictly tangent.
    const firstArc = "arc" ~ startCount;
    const lastArc = "arc" ~ (startCount + arcCount - 1);
    pinEndTangent(sketch, plane, firstArc ~ ".start", firstArc, samples.worldPoints[0], samples.tangents[0]);
    pinEndTangent(sketch, plane, lastArc ~ ".end", lastArc, samples.worldPoints[size(samples.worldPoints) - 1], samples.tangents[size(samples.tangents) - 1]);

    return arcCount;
}

// =================== Sampling, planar fit, and segmentation ===================

/**
 * Sample a path at evenly spaced arc-length parameters.  Inputs: context, path
 * (Path), sampleCount (number).  Output: { worldPoints (array of Vector), tangents
 * (array of unit Vector) }, including both endpoints.
 */
function samplePath(context is Context, path is Path, sampleCount is number) returns map
{
    var parameters = makeArray(sampleCount + 1, 0);
    for (var i = 0; i <= sampleCount; i += 1)
        parameters[i] = i / sampleCount;
    const tangentLines = evPathTangentLines(context, path, parameters).tangentLines;
    var worldPoints = makeArray(sampleCount + 1, undefined);
    var tangents = makeArray(sampleCount + 1, undefined);
    for (var i = 0; i <= sampleCount; i += 1)
    {
        worldPoints[i] = tangentLines[i].origin;
        tangents[i] = normalize(tangentLines[i].direction);
    }
    return { "worldPoints" : worldPoints, "tangents" : tangents };
}

/**
 * Fit a plane to the sample points of every path.  Input: pathSamples (array of
 * samplePath maps).  Output: Plane, or undefined when the points are collinear so
 * no plane is defined.  Normal is the summed cross product of chords from the first
 * point, robust for points sharing one plane; the planar lock check validates fit.
 */
function fitPlaneToSamples(pathSamples is array)
{
    const origin = pathSamples[0].worldPoints[0];
    var normalSum = vector(0, 0, 0) * meter * meter;
    for (var p = 0; p < size(pathSamples); p += 1)
    {
        const points = pathSamples[p].worldPoints;
        for (var i = 1; i + 1 < size(points); i += 1)
            normalSum += cross(points[i] - origin, points[i + 1] - origin);
    }
    if (norm(normalSum) < TOLERANCE.zeroLength * TOLERANCE.zeroLength * meter * meter)
        return undefined;
    const planeX = firstChordDirection(pathSamples[0].worldPoints);
    if (planeX == undefined)
        return undefined;
    return plane(origin, normalize(normalSum), planeX);
}

/**
 * Pick a stable in-plane x direction from the first nondegenerate chord.  Input:
 * points (array of Vector).  Output: unit Vector, or undefined if all coincident.
 */
function firstChordDirection(points is array)
{
    for (var i = 1; i < size(points); i += 1)
    {
        const chord = points[i] - points[0];
        if (norm(chord) > TOLERANCE.zeroLength * meter)
            return normalize(chord);
    }
    return undefined;
}

/**
 * Worst distance from any sample to the plane.  Inputs: plane (Plane), samples
 * (samplePath map).  Output: ValueWithUnits.  Used for the planar lock check.
 */
function maxPlaneDeviation(plane is Plane, samples is map) returns ValueWithUnits
{
    var maxDeviation = 0 * meter;
    for (var i = 0; i < size(samples.worldPoints); i += 1)
    {
        const deviation = abs(dot(samples.worldPoints[i] - plane.origin, plane.normal));
        if (deviation > maxDeviation)
            maxDeviation = deviation;
    }
    return maxDeviation;
}

/**
 * Greedily group sample indices into arc spans within tolerance.  Inputs:
 * worldPoints (array of Vector), tolerance (ValueWithUnits), planeNormal (unit
 * Vector for the sketch plane).  Each span grows until an interior sample's
 * distance from the span chord exceeds tolerance, or until the path's turning
 * direction reverses (an inflection): a single circular arc can only bend one way,
 * so spanning an inflection would swing the arc the long way around.  Output: array
 * of sample indices including 0 and the last index; arc tangency is solved
 * separately by the constraints.
 */
function segmentSamples(worldPoints is array, tolerance is ValueWithUnits, planeNormal is Vector) returns array
{
    const lastIndex = size(worldPoints) - 1;
    var breakpoints = [0];
    var startIndex = 0;
    while (startIndex < lastIndex)
    {
        var endIndex = inflectionLimit(worldPoints, planeNormal, startIndex);
        for (var candidate = startIndex + 2; candidate <= endIndex; candidate += 1)
        {
            if (maxChordDeviation(worldPoints, startIndex, candidate) > tolerance)
            {
                endIndex = candidate - 1;
                break;
            }
        }
        if (endIndex <= startIndex)
            endIndex = startIndex + 1;
        breakpoints = append(breakpoints, endIndex);
        startIndex = endIndex;
    }
    return breakpoints;
}

/**
 * Find the next sample index at or after startIndex where the path's turning
 * direction reverses.  Inputs: worldPoints (array of Vector), planeNormal (unit
 * Vector), startIndex (number).  Turning sense is the sign of the chord cross
 * product projected on the plane normal; the first interior index whose sign
 * opposes the span's initial sense caps the span there, else lastIndex.  Output:
 * sample index, used to keep each arc span on one side of an inflection.
 */
function inflectionLimit(worldPoints is array, planeNormal is Vector, startIndex is number) returns number
{
    const lastIndex = size(worldPoints) - 1;
    var initialSign = 0;
    for (var i = startIndex + 1; i < lastIndex; i += 1)
    {
        const turn = dot(cross(worldPoints[i] - worldPoints[i - 1], worldPoints[i + 1] - worldPoints[i]), planeNormal);
        const turnSign = turn > 0 * meter * meter ? 1 : (turn < 0 * meter * meter ? -1 : 0);
        if (turnSign == 0)
            continue;
        if (initialSign == 0)
            initialSign = turnSign;
        else if (turnSign != initialSign)
            return i;
    }
    return lastIndex;
}


/**
 * Worst perpendicular distance of interior samples from the chord between two
 * samples.  Inputs: worldPoints (array of Vector), startIndex/endIndex (number).
 * Output: ValueWithUnits, the span's straightness used to size arc spans.
 */
function maxChordDeviation(worldPoints is array, startIndex is number, endIndex is number) returns ValueWithUnits
{
    const start = worldPoints[startIndex];
    const chord = worldPoints[endIndex] - start;
    const chordLength = norm(chord);
    if (chordLength < TOLERANCE.zeroLength * meter)
        return 0 * meter;
    const chordDirection = chord / chordLength;
    var maxDeviation = 0 * meter;
    for (var k = startIndex + 1; k < endIndex; k += 1)
    {
        const offset = worldPoints[k] - start;
        maxDeviation = max(maxDeviation, norm(offset - dot(offset, chordDirection) * chordDirection));
    }
    return maxDeviation;
}
