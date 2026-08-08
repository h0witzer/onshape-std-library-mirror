FeatureScript 3044;
// Standard Library Imports
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/evaluate.fs", version : "3044.0");
import(path : "onshape/std/geomOperations.fs", version : "3044.0");
import(path : "onshape/std/query.fs", version : "3044.0");
import(path : "onshape/std/vector.fs", version : "3044.0");
import(path : "onshape/std/units.fs", version : "3044.0");
import(path : "onshape/std/valueBounds.fs", version : "3044.0");
import(path : "onshape/std/curveGeometry.fs", version : "3044.0"); // For bSplineCurve() constructor and BSplineCurve type
import(path : "onshape/std/error.fs", version : "3044.0"); // For reportFeatureWarning
import(path : "onshape/std/approximationUtils.fs", version : "3044.0");
import(path : "onshape/std/splineUtils.fs", version : "3044.0");
import(path : "onshape/std/containers.fs", version : "3044.0");
import(path : "f42f46716945f2a9bda5a481/eabbc18661ba5776e0ba962d/97730412fb61f53dcd526c08", version : "a24da502290d2ae4706c631f"); // 3d Arc Utilities
import(path : "eca0e7b6ed29c5239f39f868/7182747cabf6b534da6a21d3/9a2b77793cdc37bace6d915a", version : "6af3b02bb9bbcbd0eccecd1e"); // splineRefinementUtils.fs


export const TWEEN_FRACTION_BOUNDS = { (unitless) : [0, 0.5, 1] } as RealBoundSpec;


/**
 * Create a curve that is the tween between two input curves.
 *
 * All compatibility checks and conversions are handled internally.
 */
export function tweenCurves(context is Context, id is Id,
        curve1 is Query, curve2 is Query, fraction is number)
{
    if (evaluateQueryCount(context, curve1) == 0)
        throw regenError("Select first curve.", ["curve1"]);
    if (evaluateQueryCount(context, curve2) == 0)
        throw regenError("Select second curve.", ["curve2"]);

    const edge1 = evaluateQuery(context, curve1)[0];
    const edge2 = evaluateQuery(context, curve2)[0];

    const def1 = evCurveDefinition(context, { "edge" : edge1, "returnBSplinesAsOther" : true });
    const def2 = evCurveDefinition(context, { "edge" : edge2, "returnBSplinesAsOther" : true });

    if ((def1 is Circle && def2 is Circle) ||
        (def1 is Circle && def2 is Line) ||
        (def1 is Line && def2 is Circle))
    {
        tweenCircleOrLine(context, id, edge1, edge2, fraction);
        return;
    }

    const rawSpline1 = getBSplineFromInput(context, curve1);
    const rawSpline2 = getBSplineFromInput(context, curve2);

    if (rawSpline1 == undefined || rawSpline2 == undefined)
        throw regenError("Could not get B-spline representation for input curves.");

    // Canonical form up front: always rational, periodicity preserved (never silently
    // clamped), knot padding rebuilt from the domain knots. Everything below reasons about
    // these, not about whatever shape the kernel happened to hand back.
    var spline1 = normalizeSplineDefinition(rawSpline1);
    var spline2 = normalizeSplineDefinition(rawSpline2);

    const bothPeriodic = spline1.isPeriodic == true && spline2.isPeriodic == true;
    if ((spline1.isPeriodic == true) != (spline2.isPeriodic == true))
    {
        reportFeatureWarning(context, id, "Curves have different periodicity. The closed curve is opened so the two can be matched, and the tweened curve is open.");
    }

    // === ALIGNMENT ===
    // How curve 2 lays against curve 1 is a genuine degree of freedom, not an approximation -
    // there is no canonical correspondence between two curves. What matters is that whatever
    // is chosen gets APPLIED exactly, as a reparameterization that leaves curve 2's geometry
    // untouched, and that it happens BEFORE the knot vectors are merged. Reversing or rotating
    // control point arrays AFTER merging - which this feature used to do - silently distorts
    // the curve unless the shared knot vector happens to be uniform, because it moves control
    // points relative to knots that did not move with them.
    if (bothPeriodic)
    {
        spline2 = alignPeriodicSplineToReference(spline1, spline2);
    }
    else if (shouldReverseToMatchDirection(spline1, spline2))
    {
        spline2 = reverseSpline(spline2);
    }

    // === EXACT COMPATIBILITY (spec sections 2.4 and 3.1) ===
    // Both curves land on a common degree AND a common knot vector, so blending control point
    // i of curve 1 against control point i of curve 2 is exactly blending the curves
    // themselves (spec section 3.2's affine argument). "Same degree, same count" is necessary
    // but not sufficient: two curves can have both while control point i means something
    // different on each - a line's default [0, 1] domain against an arc-derived spline's own
    // native parameterization, or two splines with different interior knot structure.
    // makeSplinesCompatible remaps both domains before merging, so mismatched domains are
    // handled too. Periodic pairs stay periodic all the way through (the exact
    // periodic-preserving refinement in splineRefinementUtils.fs); mixed pairs are clamped,
    // which is what the warning above is about. Replaces the old elevateDegree + matchCPCount
    // pipeline entirely - matchCPCount sampled and refit through approximateSpline, so it was
    // never exact, and it only ever ran on the both-periodic path.
    const compatible = makeSplinesCompatible(spline1, spline2);
    const finalSpline1 = compatible.a;
    const finalSpline2 = compatible.b;

    // Defensive only: makeSplinesCompatible guarantees both. Reaching either throw means a
    // module regression, not bad input.
    if (finalSpline1.degree != finalSpline2.degree)
    {
        throw regenError("Internal error: curve degrees still differ after compatibility processing.", ["curve1", "curve2"]);
    }
    if (size(finalSpline1.controlPoints) != size(finalSpline2.controlPoints))
    {
        throw regenError("Internal error: curves have different B-spline control point counts (" ~
                size(finalSpline1.controlPoints) ~ " vs " ~ size(finalSpline2.controlPoints) ~
                ") after compatibility processing.", ["curve1", "curve2"]);
    }

    const controlPointCount = size(finalSpline1.controlPoints);
    var tweenedControlPoints = makeArray(controlPointCount, finalSpline1.controlPoints[0]);
    var tweenedWeights = makeArray(controlPointCount, 1);

    for (var pointIndex = 0; pointIndex < controlPointCount; pointIndex += 1)
    {
        const weight1 = finalSpline1.weights[pointIndex];
        const weight2 = finalSpline2.weights[pointIndex];
        const blendedWeight = weight1 * (1 - fraction) + weight2 * fraction;

        // Rational splines interpolate in homogeneous coordinates: weight each control point,
        // blend, then divide back out by the blended weight.
        const weightedPosition1 = finalSpline1.controlPoints[pointIndex] * weight1;
        const weightedPosition2 = finalSpline2.controlPoints[pointIndex] * weight2;
        const blendedWeightedPosition = weightedPosition1 * (1 - fraction) + weightedPosition2 * fraction;

        tweenedControlPoints[pointIndex] = blendedWeightedPosition / blendedWeight;
        tweenedWeights[pointIndex] = blendedWeight;
    }

    // The shared knot vector is carried through to the result. Omitting it - which this
    // feature used to do - makes bSplineCurve synthesize a UNIFORM one, throwing away the
    // entire point of exact knot sharing: at fraction 0 the result would not reproduce curve 1
    // unless curve 1's knots happened to already be uniform.
    const tweenedCurve = bSplineCurve({
            "degree" : finalSpline1.degree,
            "isPeriodic" : bothPeriodic,
            "controlPoints" : tweenedControlPoints,
            "weights" : tweenedWeights,
            "knots" : finalSpline1.knots
        });

    opCreateBSplineCurve(context, id + "tweenedCpSpline", { "bSplineCurve" : tweenedCurve });
}

// Bounds on the geometric sample count used to choose a periodic seam alignment. The search is
// O(count^2), so this is the feature's single most performance-sensitive number. The count is
// taken from the curves' own control point counts - matching the resolution this feature has
// always aligned at, since it used to search over control point indices directly - and then
// clamped: the floor keeps very simple curves from aligning on too little evidence, and the
// ceiling keeps a dense curve from quadratically blowing up regeneration time, which the
// control-point-index search had no protection against at all.
const PERIODIC_ALIGNMENT_MIN_SAMPLES = 16;
const PERIODIC_ALIGNMENT_MAX_SAMPLES = 48;

/**
 * Choose how curve 2's period lays against curve 1's, and apply that choice EXACTLY.
 *
 * Both candidate operations - reversal, and moving the seam - are exact reparameterizations
 * (see reverseSpline and rewindowPeriodicSpline), so curve 2's geometry is untouched. Only the
 * labelling of which parameter is "the start" changes, and that is precisely what decides
 * which control point of curve 1 blends against which control point of curve 2 once
 * makeSplinesCompatible has merged the knot vectors.
 *
 * The choice is made on SAMPLED GEOMETRY rather than on control points. Control point arrays
 * are only comparable between two curves that already share a parameterization - which is
 * exactly what has not been established yet at this point in the pipeline - whereas sampled
 * points are comparable always.
 */
function alignPeriodicSplineToReference(reference is map, candidate is map) returns map
{
    const complexity = max(size(reference.controlPoints), size(candidate.controlPoints));
    const sampleCount = min(PERIODIC_ALIGNMENT_MAX_SAMPLES, max(PERIODIC_ALIGNMENT_MIN_SAMPLES, complexity));

    const referenceSamples = samplePeriodicSplineUniformly(reference, sampleCount);
    const forwardSamples = samplePeriodicSplineUniformly(candidate, sampleCount);
    // The reversed candidate's samples are a cyclic reversal of the forward ones, so they need
    // no third evaluation pass: reverseSpline maps t to -t, so sampling the reversed spline at
    // its own domainStart + T*j/N evaluates the original at a + T*(N - j)/N.
    const reversedSamples = cyclicReverse(forwardSamples);

    const forward = bestCyclicAlignment(referenceSamples, forwardSamples);
    const reversed = bestCyclicAlignment(referenceSamples, reversedSamples);

    // Correlations are directly comparable between the two directions - see
    // bestCyclicAlignment - so no follow-up distance sums are needed to break the tie.
    const useReversed = reversed.correlation > forward.correlation;
    const chosen = useReversed ? reverseSpline(candidate) : candidate;
    const chosenShift = useReversed ? reversed.shift : forward.shift;

    // Shift m means "curve 1 at fraction j/N corresponds to curve 2 at fraction (j + m)/N", so
    // curve 2's seam moves forward by that same fraction of its own period.
    const domain = periodicSplineDomain(chosen);
    return rewindowPeriodicSpline(chosen, domain.start + domain.period * chosenShift / sampleCount);
}

/**
 * Best cyclic alignment of `candidate` onto `reference` (equal-length arrays of uniformly
 * spaced samples): the shift s maximizing the circular cross-correlation
 * sum_j (reference[j] . candidate[(j + s) mod N]), returned with that correlation.
 *
 * Maximizing correlation is equivalent to minimizing the sum of SQUARED distances, because
 * sum|A_j - B_(j+s)|^2 = sum|A_j|^2 + sum|B_j|^2 - 2*sum(A_j . B_(j+s)), and both leading terms
 * are independent of s - a cyclic shift only permutes which points get summed. That identity is
 * what makes this affordable: the inner loop is a dot product rather than a norm, so an O(N^2)
 * search costs no square roots at all. Choosing least squares over sum-of-distances is a
 * deliberate change of criterion (it is the standard registration criterion), not a silent
 * substitution of one for the other - see the squaredNorm-vs-norm caveat about summed metrics.
 *
 * Correlations are comparable across the forward and reversed candidates for the same reason:
 * reversal permutes the same multiset of points, leaving both constant terms untouched.
 */
function bestCyclicAlignment(reference is array, candidate is array) returns map
{
    const count = size(reference);
    var bestShift = 0;
    var bestCorrelation = -1e30 * meter * meter;
    for (var shift = 0; shift < count; shift += 1)
    {
        var correlation = 0 * meter * meter;
        for (var index = 0; index < count; index += 1)
        {
            var sourceIndex = index + shift;
            if (sourceIndex >= count)
            {
                sourceIndex -= count;
            }
            correlation += dot(reference[index], candidate[sourceIndex]);
        }
        if (correlation > bestCorrelation)
        {
            bestCorrelation = correlation;
            bestShift = shift;
        }
    }
    return { "shift" : bestShift, "correlation" : bestCorrelation };
}

/** Reverse a cyclic sample array in place of its parameterization: result[j] is
    elements[(N - j) mod N], so element 0 (the seam) stays put and the rest run backwards. */
function cyclicReverse(elements is array) returns array
{
    const count = size(elements);
    var reversed = makeArray(count, elements[0]);
    for (var index = 1; index < count; index += 1)
    {
        reversed[index] = elements[count - index];
    }
    return reversed;
}

/** A spline's parameter domain: [start, start + period], read off the knot array's clamped or
    periodic domain positions (both live at the same indices). */
function periodicSplineDomain(spline is map) returns map
{
    const domainStart = spline.knots[spline.degree];
    const domainEnd = spline.knots[size(spline.knots) - spline.degree - 1];
    return { "start" : domainStart, "period" : domainEnd - domainStart };
}

/** Build a kernel BSplineCurve from a normalized spline map, preserving its periodicity and
    its exact knot vector, so std's own evaluator can be used on it. */
function toKernelCurve(spline is map) returns BSplineCurve
{
    return bSplineCurve({
                "degree" : spline.degree,
                "isPeriodic" : spline.isPeriodic == true,
                "controlPoints" : spline.controlPoints,
                "weights" : spline.weights,
                "knots" : spline.knots is KnotArray ? spline.knots : knotArray(spline.knots)
            });
}

/** Sample a periodic spline at `sampleCount` uniformly spaced parameters spanning exactly one
    period, excluding the wrap-around duplicate at the far end. */
function samplePeriodicSplineUniformly(spline is map, sampleCount is number) returns array
{
    const domain = periodicSplineDomain(spline);
    var parameters = makeArray(sampleCount, 0);
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        parameters[sampleIndex] = domain.start + domain.period * sampleIndex / sampleCount;
    }
    return evaluateSpline({ "spline" : toKernelCurve(spline), "parameters" : parameters })[0];
}

/**
 * For two curves that are not both closed, decide whether curve 2 should be reversed so the
 * pair runs the same way. Compares the two possible endpoint pairings: start-to-start plus
 * end-to-end, against start-to-end plus end-to-start. Endpoints are evaluated rather than read
 * off the control point array so this works whatever form each spline is in (a periodic
 * spline's first control point is not its start point); a closed curve's two endpoints
 * coincide, making both pairings equal, so it correctly abstains rather than reversing on
 * noise.
 */
function shouldReverseToMatchDirection(reference is map, candidate is map) returns boolean
{
    const referenceEnds = splineEndPoints(reference);
    const candidateEnds = splineEndPoints(candidate);
    const sameDirection = squaredNorm(referenceEnds.start - candidateEnds.start) + squaredNorm(referenceEnds.end - candidateEnds.end);
    const reversedDirection = squaredNorm(referenceEnds.start - candidateEnds.end) + squaredNorm(referenceEnds.end - candidateEnds.start);
    return reversedDirection < sameDirection;
}

/** A spline's two domain-endpoint positions. */
function splineEndPoints(spline is map) returns map
{
    const domain = periodicSplineDomain(spline);
    const points = evaluateSpline({
                "spline" : toKernelCurve(spline),
                "parameters" : [domain.start, domain.start + domain.period]
            })[0];
    return { "start" : points[0], "end" : points[1] };
}

/**
 * rotateArray with a guaranteed non-negative step. `rotateArray(elements, -shift)` is the
 * natural spelling, but FeatureScript's `%` returns a NEGATIVE remainder for a negative left
 * operand, so a negative step makes rotateArray compute a backwards subArray range internally.
 * Rotating by `count - shift` is the same rotation with a positive step.
 */
function rotateArrayForward(elements is array, shift is number) returns array
{
    const count = size(elements);
    if (count == 0 || shift % count == 0)
    {
        return elements;
    }
    return rotateArray(elements, count - (shift % count));
}

//==================================================================
//=================== Stealing from editCurve ======================
//==================================================================

//==================================================================
//======================== Input Processing ========================
//==================================================================

function checkBSpline(bspline is BSplineCurve, wire is Query)
{
    if (bspline.degree > MAX_DEGREE)
    {
        throw regenError(ErrorStringEnum.EDIT_CURVE_DEGREE_TOO_HIGH, ["wire"], wire);
    }
    if (size(bspline.controlPoints) > MAX_CONTROL_POINTS)
    {
        throw regenError(ErrorStringEnum.EDIT_CURVE_TOO_MANY_CONTROL_POINTS, ["wire"], wire);
    }
}

function getBSplineFromInput(context is Context, definition is map) returns map
{
    var bspline;
    const edgesQuery = getAllEdgesQuery(definition);
    {
        const edges = evaluateQuery(context, edgesQuery);
        if (size(edges) > 1)
        {
            throw regenError(ErrorStringEnum.EDIT_CURVE_MULTIPLE_EDGES, ["wire"], definition);
        }
        const edge = edges[0];
        const curveDef = evCurveDefinition(context, {
                    "edge" : edge,
                    "simplify" : true
                });
        if (curveDef is Line)
        {
            const edgeVertices = evaluateQuery(context, qAdjacent(edge, AdjacencyType.VERTEX, EntityType.VERTEX));
            bspline = bSplineCurve({
                        "degree" : 1,
                        "isPeriodic" : false,
                        "controlPoints" : [evVertexPoint(context, { "vertex" : edgeVertices[0] }), evVertexPoint(context, { "vertex" : edgeVertices[1] })]
                    });
        }
        else if (curveDef is BSplineCurve)
        {
            bspline = curveDef;
            checkBSpline(bspline, definition);
        }
        else
        {
            bspline = evApproximateBSplineCurve(context, {
                        "edge" : edge
                    });
            if (isAtVersionOrLater(context, FeatureScriptVersionNumber.V2554_EDIT_CURVE_CHECK_BSPLINE_APPROXIMATION))
            {
                checkBSpline(bspline, definition);
            }
        }
    }
    // Canonicalization (force-rational, periodic form) is normalizeSplineDefinition's job now,
    // and the caller runs it immediately. This used to additionally call a local copy of std
    // editCurve.fs's cleanUpPeriodicBSplineDefinition, which was removed rather than kept: its
    // "knots[0] != 0 means this needs reinterpreting" heuristic corrupts a perfectly canonical
    // periodic curve (every one of them has knots[0] < 0 from its own padding), and for
    // degree 1 it would rewrite the knot vector's ends outright.
    return bspline;
}



//==================================================================
//=========================== Utilities ============================
//==================================================================

function getAllEdgesQuery(query is Query) returns Query
{
    return qUnion([qEntityFilter(query, EntityType.EDGE), qEntityFilter(query, EntityType.BODY)->qOwnedByBody(EntityType.EDGE)]);
}

/**
 * Sum of SQUARED point distances between two equal-length point arrays — the least-squares
 * registration residual. Squared throughout, matching bestPeriodicShift, so the shift and the
 * forward-vs-reversed decision in alignCircleToCurve are chosen under one criterion rather than
 * two. No square roots: nothing here is ever reported, only compared.
 */
function sumSquaredDistances(points1 is array, points2 is array) returns ValueWithUnits
{
    var total = 0 * meter ^ 2;
    for (var i = 0; i < size(points1); i += 1)
    {
        total += squaredNorm(points1[i] - points2[i]);
    }
    return total;
}

/**
 * Find the rotation of `candidate` that best matches `reference`: the shift s minimizing the
 * total SQUARED distance between reference[i] and candidate[(i + s) mod n], which is the same
 * pairing rotateArrayForward(candidate, s) produces. Indexes directly rather than building each
 * rotated array, since this is O(n^2) terms and allocating an n-element array per candidate
 * shift dominates otherwise.
 */
function bestPeriodicShift(reference is array, candidate is array) returns number
{
    const count = size(reference);
    var bestShift = 0;
    var bestDistance = 1e30 * meter^2;
    for (var shift = 0; shift < count; shift += 1)
    {
        var distance = 0 * meter^2;
        for (var index = 0; index < count; index += 1)
        {
            var sourceIndex = index + shift;
            if (sourceIndex >= count)
            {
                sourceIndex -= count;
            }
            distance += squaredNorm(reference[index] - candidate[sourceIndex]);
        }
        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestShift = shift;
        }
    }
    return bestShift;
}


function collinearPoints(p1 is Vector, p2 is Vector, p3 is Vector) returns boolean
{
    return parallelVectors(p2 - p1, p3 - p1);
}

function alignCircleToCurve(context is Context, circleEdge is Query, otherEdge is Query, baseParams is array) returns array
{
    const sampleCount = 8;
    var sampleParams = [];
    for (var i = 0; i < sampleCount; i += 1)
        sampleParams = append(sampleParams, i / sampleCount);

    const circlePts = mapArray(evEdgeTangentLines(context, { "edge" : circleEdge, "parameters" : sampleParams }),
        line
        =>line.origin);
    const otherPts = mapArray(evEdgeTangentLines(context, { "edge" : otherEdge, "parameters" : sampleParams }),
        line
        =>line.origin);

    const normalShift = bestPeriodicShift(circlePts, otherPts);
    const normalRot = rotateArrayForward(circlePts, normalShift);
    const reversed = reverse(circlePts);
    const revShift = bestPeriodicShift(reversed, otherPts);
    const revRot = rotateArrayForward(reversed, revShift);
    const distNorm = sumSquaredDistances(normalRot, otherPts);
    const distRev = sumSquaredDistances(revRot, otherPts);

    if (distRev < distNorm)
        return rotateArrayForward(reverse(baseParams), revShift);
    return rotateArrayForward(baseParams, normalShift);
}

function tweenCircleOrLine(context is Context, id is Id, edge1 is Query, edge2 is Query, fraction is number)
{
    const baseParams = [0, 0.5, 1];
    var params1 = baseParams;
    var params2 = baseParams;

    const info1 = evEdgeTangentLine(context, { "edge" : edge1, "parameter" : 0 });
    const info2 = evEdgeTangentLine(context, { "edge" : edge2, "parameter" : 0 });
    const dir1 = normalize(info1.direction);
    const dir2 = normalize(info2.direction);
    if (dot(dir1, dir2) < 0)
        params2 = reverse(params2);

    const def1 = evCurveDefinition(context, { "edge" : edge1, "returnBSplinesAsOther" : true });
    const def2 = evCurveDefinition(context, { "edge" : edge2, "returnBSplinesAsOther" : true });

    if (def1 is Circle && def2 is Circle)
    {
        params2 = alignCircleToCurve(context, edge2, edge1, params2);
    }
    else if (def1 is Circle && !(def2 is Circle))
    {
        params1 = alignCircleToCurve(context, edge1, edge2, params1);
    }
    else if (def2 is Circle && !(def1 is Circle))
    {
        params2 = alignCircleToCurve(context, edge2, edge1, params2);
    }

    const tangents1 = evEdgeTangentLines(context, { "edge" : edge1, "parameters" : params1 });
    const tangents2 = evEdgeTangentLines(context, { "edge" : edge2, "parameters" : params2 });

    var points = [];
    for (var i = 0; i < size(baseParams); i += 1)
    {
        const pos1 = tangents1[i].origin;
        const pos2 = tangents2[i].origin;
        points = append(points, pos1 * (1 - fraction) + pos2 * fraction);
    }

    if (collinearPoints(points[0], points[1], points[2]))
    {
        const lineDef = bSplineCurve({ "degree" : 1, "isPeriodic" : false, "controlPoints" : [points[0], points[2]] });
        opCreateBSplineCurve(context, id + "tweenedCpSpline", { "bSplineCurve" : lineDef });
        return;
    }

    if (squaredNorm(points[0] - points[2]) < 1e-8 * meter * meter)
    {
        const center = def1.coordSystem.origin * (1 - fraction) + def2.coordSystem.origin * fraction;
        const radius = def1.radius * (1 - fraction) + def2.radius * fraction;
        const normal = normalize(def1.coordSystem.zAxis * (1 - fraction) + def2.coordSystem.zAxis * fraction);
        opCircle3d(context, id + "tweenedCpSpline", { "center" : center, "normal" : normal, "radius" : radius });
    }
    else
    {
        opArc3d(context, id + "tweenedCpSpline", { "start" : points[0], "mid" : points[1], "end" : points[2] });
    }
}
