FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

// Non-standard imports. swEnvelopeMath and swFunnelSolver are same-document element imports
// (unpublished on purpose - see swEnvelopeMath.fs); their ids churn on every re-paste, so bump
// these when the owner re-pastes either module. splineRefinementUtils is the published
// cross-document pin. NOTE for MCP harness runs: same-document imports cannot resolve from the
// harness document; the test payload inlines the needed function bodies in place of these lines.
import(path : "eede4083ca591e1a7adb8440", version : "3968d1ef5b507302198a917b"); //owner combined tab: swEnvelopeMath.fs, swFunnelSolver.fs (fix id/version on paste; split into one line per tab if they live separately)
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs

/**
 * SOLID SWEEP - the two remaining degeneracy detectors of spec section 6.4
 * (docs/specs/SOLID_SWEEP_SPEC.md). Pure module: no Context in any function. Its own module for
 * the same reason swOrientation and swAnalyticContact are - the funnel solver sits at the
 * payload size that keeps a single-call MCP harness run possible, and both detectors reach only
 * a handful of declarations, so they cost a small payload here and a large one there.
 *
 *   - SWEEP_FUNNEL_TANGENT_TO_SLICE (detector 2): |f_t| -> 0 somewhere along a section p-curve
 *     f(., ., t_j) = 0. The funnel is tangent to that station's slice there, so the section is
 *     an extremum of the contact set in t and the (q, t) fit grid must not span it.
 *   - SWEEP_EDGE_SWEEP_SINGULARITY (detector 3): the velocity of a point on a sharp edge runs
 *     parallel to the transported edge tangent, so the sharp edge's envelope sheet
 *     Phi(s, t) = A e(s) + b has no normal there. The papers' Lemma 15; rejected in v1.
 *
 * WHAT MAKES DETECTOR 2 MORE THAN A SIGN SCAN. A constant-velocity translation has f_t
 * identically zero on every section, because f_t = <A'N, v> + <AN, a> and both terms vanish
 * when A' = 0 and a = 0. That is not a tangency - it is a STATIONARY section, the ordinary
 * translational sweep whose envelope is the extrusion of one contact curve, and splitting it
 * would mean splitting at every point of a curve that has no tangency on it at all. An absolute
 * |f_t| threshold cannot tell the two apart, so the audit reads the scale off the terms that
 * build f_t - evaluateEnvelopeGradientPointwise's tDerivativeScale, the Cauchy-Schwarz bound
 * |A'N||v| + |AN||a| - and compares against that, the same argument the census makes for taking
 * its sign tolerance from a block's own range.
 *
 * That reference alone is not enough, and the constant-velocity case is exactly why: there
 * tDerivativeScale is zero too, so the ratio is 0/0 and any rounding noise in f_t reads as a
 * full-scale signal. The audit therefore carries a SECOND reference, `valueScale` = |AN||v| -
 * the size of f itself - divided by the motion parameter's own span, and calls the section
 * stationary when |f_t| is under the relative floor against EITHER. The second reference is
 * dimensionally f per unit t rather than f_t's own bound, which is why the span is an explicit
 * option instead of an assumption; the module's normalization puts it at 1.
 *
 * Three verdicts come out, not two:
 *
 *     stationarySection : |f_t| is at the scale's own noise floor everywhere on the section.
 *                         Benign for a translational sweep; the caller must NOT split.
 *     tangencies        : isolated zeros of f_t, refined onto the p-curve. The section splits
 *                         at each, and adjacent pieces share the split point EXACTLY (one uv
 *                         array element reused, not two numbers that agree) - spec 2.3.
 *     nearTangencies    : interior minima of |f_t| that dip under tolerance without crossing.
 *                         Reported, never split: from one section a merged pair and a genuine
 *                         near miss look the same, and guessing which would be inventing
 *                         topology. Same reason swAnalyticContact reports nearTangency on a
 *                         double root of a trigonometric polynomial instead of resolving it.
 *
 * WHY DETECTOR 3 NEEDS REFINEMENT AND NOT ONLY A GRID. v parallel to A e' is TWO scalar
 * conditions on a two-parameter (s, t) domain - a 3-vector cross product vanishing, minus the
 * one component it satisfies identically - so its solutions are generically ISOLATED POINTS,
 * which is exactly what a grid scan misses. The grid supplies candidates (local minima of the
 * normalized sine, plus the global minimum unconditionally); Levenberg-damped Gauss-Newton on
 * the 3-residual, 2-unknown system decides. The refined sine is reported, so a candidate that
 * turns out to be a near miss is recorded as a near miss rather than as a hit.
 *
 * The measure is the NORMALIZED sine |A e' x v| / (|A e'||v|), never the raw cross product: the
 * raw one shrinks with the tool's own scale and with the speed, so a slow sweep of a small part
 * would trip an absolute threshold everywhere.
 */

// ============================= Detector 2: funnel tangent to slice =============================

/**
 * The relative floor under which f_t on a section carries no sign. f_t is a four-vector
 * inner-product sum through the rational surface evaluators, so cancellation there costs a few
 * thousand machine epsilons of its own terms' magnitude; this sits three orders above that.
 */
export const SECTION_TIME_DERIVATIVE_RELATIVE_FLOOR = 1e-12;

/**
 * Audit one marched section p-curve for spec 6.4's SWEEP_FUNNEL_TANGENT_TO_SLICE.
 *
 * `uvPoints` is marchSectionCurve's output for station `tGlobal` - [u, v] arrays in the
 * surface's knot domain, in march order. Nothing is re-marched here: the audit evaluates f_t at
 * the points the march already produced, then refines only inside the brackets it finds.
 *
 * options: {
 *     relativeTolerance : the stationary-section test - |f_t| below this fraction of EITHER
 *         reference everywhere (default SECTION_TIME_DERIVATIVE_RELATIVE_FLOOR),
 *     timeSpan : the motion parameter's own span, for the |f| / span reference (default 1,
 *         which is what the module's normalization gives),
 *     tangencyTolerance : absolute |f_t| counting as zero AT A SAMPLE (default 0, so a tangency
 *         has to show as a sign change between samples),
 *     nearTangencyTolerance : relative |f_t| / scale under which an interior minimum with no
 *         sign change is reported as a near tangency (default 1e-3),
 *     sectionTolerance : the corrector's |f| residual target while refining (default 1e-10),
 *     maxRefineIterations : default 60
 * }
 *
 * Returns {
 *     detected {boolean} : one or more isolated tangencies - the section MUST be split,
 *     stationarySection {boolean} : f_t is at its own noise floor along the whole section,
 *     tangencies {array} : { segmentIndex, fraction, uv, timeDerivative, timeDerivativeScale,
 *         sectionResidual, atSample } in march order,
 *     nearTangencies {array} : { sampleIndex, uv, timeDerivative, relativeMagnitude },
 *     sampleTimeDerivatives {array}, sampleSigns {array}, sampleScales {array},
 *     minimumMagnitude {number}, minimumRelative {number}, minimumSampleIndex {number},
 *     scaleReference {number} : the largest tDerivativeScale on the section,
 *     valueScaleReference {number} : the largest valueScale on the section,
 *     stationaryFloor {number} : the |f_t| the two references together put the noise floor at,
 *     worstSectionResidual {number} : the largest |f| at the marched points - a read on the
 *         input polyline rather than on this audit
 * }
 */
export function auditSectionTangency(strippedMotion is map, strippedSurface is map,
    tGlobal is number, uvPoints is array, options is map) returns map
{
    const relativeTolerance = options.relativeTolerance == undefined ?
        SECTION_TIME_DERIVATIVE_RELATIVE_FLOOR : options.relativeTolerance;
    const tangencyTolerance = options.tangencyTolerance == undefined ? 0 : options.tangencyTolerance;
    const nearTangencyTolerance = options.nearTangencyTolerance == undefined ? 1e-3 :
        options.nearTangencyTolerance;
    const sectionTolerance = options.sectionTolerance == undefined ? 1e-10 : options.sectionTolerance;
    const maxRefineIterations = options.maxRefineIterations == undefined ? 60 : options.maxRefineIterations;
    const timeSpan = options.timeSpan == undefined ? 1 : options.timeSpan;

    const pointCount = size(uvPoints);
    var derivatives = makeArray(pointCount, 0);
    var scales = makeArray(pointCount, 0);
    var signs = makeArray(pointCount, 0);
    var scaleReference = 0;
    var valueScaleReference = 0;
    var worstSectionResidual = 0;
    var largestMagnitude = 0;
    var minimumMagnitude = undefined;
    var minimumSampleIndex = 0;
    for (var index = 0; index < pointCount; index += 1)
    {
        const gradient = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface,
            uvPoints[index][0], uvPoints[index][1], tGlobal);
        derivatives[index] = gradient.tDerivative;
        scales[index] = gradient.tDerivativeScale;
        scaleReference = max(scaleReference, gradient.tDerivativeScale);
        valueScaleReference = max(valueScaleReference, gradient.valueScale);
        worstSectionResidual = max(worstSectionResidual, abs(gradient.value));
        const magnitude = abs(gradient.tDerivative);
        largestMagnitude = max(largestMagnitude, magnitude);
        if (minimumMagnitude == undefined || magnitude < minimumMagnitude)
        {
            minimumMagnitude = magnitude;
            minimumSampleIndex = index;
        }
    }
    if (minimumMagnitude == undefined)
    {
        minimumMagnitude = 0;
    }
    const minimumRelative = scaleReference <= 0 ? 0 : minimumMagnitude / scaleReference;

    // The stationary test comes before any sign is read: a section whose f_t never rises above
    // its own terms' noise floor has no tangency to find, and every sign on it would be noise.
    // Whichever reference is larger decides, so the constant-velocity case - where f_t's own
    // bound collapses to zero along with f_t - is carried by the size of f instead.
    const stationaryFloor = max(tangencyTolerance,
        relativeTolerance * max(scaleReference,
            timeSpan <= 0 ? 0 : valueScaleReference / timeSpan));
    const stationarySection = largestMagnitude <= stationaryFloor;
    if (stationarySection)
    {
        return {
                "detected" : false,
                "stationarySection" : true,
                "tangencies" : [],
                "nearTangencies" : [],
                "sampleTimeDerivatives" : derivatives,
                "sampleSigns" : signs,
                "sampleScales" : scales,
                "minimumMagnitude" : minimumMagnitude,
                "minimumRelative" : minimumRelative,
                "minimumSampleIndex" : minimumSampleIndex,
                "scaleReference" : scaleReference,
                "valueScaleReference" : valueScaleReference,
                "stationaryFloor" : stationaryFloor,
                "worstSectionResidual" : worstSectionResidual
            };
    }

    for (var index = 0; index < pointCount; index += 1)
    {
        signs[index] = signWithDeadband(derivatives[index], tangencyTolerance);
    }

    var tangencies = [];
    for (var index = 0; index < pointCount; index += 1)
    {
        // A zero that landed on a sample is recorded at the sample itself rather than
        // bracketed, and the brackets on either side of it are then not sign changes at all.
        if (signs[index] == 0)
        {
            tangencies = append(tangencies, {
                        "segmentIndex" : index == 0 ? 0 : index - 1,
                        "fraction" : index == 0 ? 0 : 1,
                        "uv" : uvPoints[index],
                        "timeDerivative" : derivatives[index],
                        "timeDerivativeScale" : scales[index],
                        "sectionResidual" : abs(evaluateEnvelopePointwise(strippedMotion,
                                strippedSurface, uvPoints[index][0], uvPoints[index][1], tGlobal)),
                        "atSample" : index
                    });
            continue;
        }
        if (index + 1 >= pointCount || signs[index + 1] == 0 || signs[index + 1] == signs[index])
        {
            continue;
        }
        tangencies = append(tangencies, refineSectionTangency(strippedMotion, strippedSurface,
                tGlobal, uvPoints[index], uvPoints[index + 1], derivatives[index],
                derivatives[index + 1], index, sectionTolerance, maxRefineIterations));
    }

    // Near tangencies: interior dips of |f_t| that never cross. Reported only where no tangency
    // was already located in the same neighbourhood, so one event is never counted twice.
    var nearTangencies = [];
    for (var index = 1; index + 1 < pointCount; index += 1)
    {
        const magnitude = abs(derivatives[index]);
        const previousMagnitude = abs(derivatives[index - 1]);
        const nextMagnitude = abs(derivatives[index + 1]);
        // A dip, so at least one side has to be STRICTLY larger: a march that ends by appending
        // its end anchor twice leaves two equal samples, and a non-strict test would read that
        // repeated pair as an interior minimum of a curve that has none there.
        if (magnitude > previousMagnitude || magnitude > nextMagnitude ||
            (magnitude == previousMagnitude && magnitude == nextMagnitude))
        {
            continue;
        }
        const relativeMagnitude = scaleReference <= 0 ? 0 : magnitude / scaleReference;
        if (relativeMagnitude > nearTangencyTolerance)
        {
            continue;
        }
        if (tangencyOnSegment(tangencies, index - 1) || tangencyOnSegment(tangencies, index))
        {
            continue;
        }
        nearTangencies = append(nearTangencies, {
                    "sampleIndex" : index,
                    "uv" : uvPoints[index],
                    "timeDerivative" : derivatives[index],
                    "relativeMagnitude" : relativeMagnitude
                });
    }

    return {
            "detected" : size(tangencies) > 0,
            "stationarySection" : false,
            "tangencies" : tangencies,
            "nearTangencies" : nearTangencies,
            "sampleTimeDerivatives" : derivatives,
            "sampleSigns" : signs,
            "sampleScales" : scales,
            "minimumMagnitude" : minimumMagnitude,
            "minimumRelative" : minimumRelative,
            "minimumSampleIndex" : minimumSampleIndex,
            "scaleReference" : scaleReference,
            "valueScaleReference" : valueScaleReference,
            "stationaryFloor" : stationaryFloor,
            "worstSectionResidual" : worstSectionResidual
        };
}

export function auditSectionTangency(strippedMotion is map, strippedSurface is map,
    tGlobal is number, uvPoints is array) returns map
{
    return auditSectionTangency(strippedMotion, strippedSurface, tGlobal, uvPoints, {});
}

/**
 * Split a marched section at its tangencies (spec 6.4: "section extraction splits at the
 * tangency"), so that no piece handed to arc-length resampling spans a point where the funnel
 * is tangent to the slice.
 *
 * Adjacent pieces SHARE the split point as the same value - the tangency's own uv, appended as
 * the last element of the piece before it and the first of the piece after it - which is what
 * spec 2.3 asks for: the seam is one number, not two that agree to tolerance.
 *
 * A piece that comes out with fewer than two distinct points (a tangency at the very start or
 * end of the march, or two tangencies inside one step) is dropped and counted, never emitted as
 * a degenerate section.
 *
 * Returns { pieces {array of uv polylines}, splitCount, droppedPieces }.
 */
export function splitSectionAtTangencies(uvPoints is array, tangencyAudit is map) returns map
{
    const tangencies = tangencyAudit.tangencies;
    if (size(tangencies) == 0)
    {
        return { "pieces" : [uvPoints], "splitCount" : 0, "droppedPieces" : 0 };
    }
    const ordered = sort(tangencies, function(first, second)
        {
            return first.segmentIndex == second.segmentIndex ?
                first.fraction - second.fraction : first.segmentIndex - second.segmentIndex;
        });

    var pieces = [];
    var droppedPieces = 0;
    var current = [uvPoints[0]];
    var pointIndex = 1;
    for (var tangencyIndex = 0; tangencyIndex < size(ordered); tangencyIndex += 1)
    {
        const tangency = ordered[tangencyIndex];
        while (pointIndex <= tangency.segmentIndex && pointIndex < size(uvPoints))
        {
            current = appendUnlessDuplicate(current, uvPoints[pointIndex]);
            pointIndex += 1;
        }
        current = appendUnlessDuplicate(current, tangency.uv);
        if (size(current) >= 2)
        {
            pieces = append(pieces, current);
        }
        else
        {
            droppedPieces += 1;
        }
        current = [tangency.uv];
    }
    while (pointIndex < size(uvPoints))
    {
        current = appendUnlessDuplicate(current, uvPoints[pointIndex]);
        pointIndex += 1;
    }
    if (size(current) >= 2)
    {
        pieces = append(pieces, current);
    }
    else
    {
        droppedPieces += 1;
    }
    return { "pieces" : pieces, "splitCount" : size(ordered), "droppedPieces" : droppedPieces };
}

/**
 * Arc-length resample each piece of a split section independently, through the funnel solver's
 * own resampleAndPolishSection - so the q fractions run 0..1 WITHIN a piece. That is the point
 * of splitting: q fractions measured across a tangency would slide along the section from
 * station to station, which is what the fixed-fraction convention exists to prevent.
 *
 * Returns { sections {array of resampleAndPolishSection results}, worstResidual }.
 */
export function resampleSectionPieces(strippedMotion is map, strippedSurface is map,
    tGlobal is number, pieces is array, sampleCount is number, tolerance is number) returns map
{
    var sections = makeArray(size(pieces));
    var worstResidual = 0;
    for (var pieceIndex = 0; pieceIndex < size(pieces); pieceIndex += 1)
    {
        const resampled = resampleAndPolishSection(strippedMotion, strippedSurface, tGlobal,
                pieces[pieceIndex], sampleCount, tolerance);
        sections[pieceIndex] = resampled;
        worstResidual = max(worstResidual, resampled.worstResidual);
    }
    return { "sections" : sections, "worstResidual" : worstResidual };
}

// ============================= Detector 3: edge sweep singularity =============================

/**
 * How far one point of a sharp edge is from spec 6.4's SWEEP_EDGE_SWEEP_SINGULARITY at one
 * station: the NORMALIZED sine between the transported edge tangent A e' and the point's
 * velocity A' e + b'. The sharp edge's envelope sheet is Phi(s, t) = A e(s) + b, whose
 * parametric normal is (A e') x velocity, so this sine vanishing is precisely that sheet losing
 * its normal - the same quantity swOrientation's orientSharpEdgeFace reports degenerate on,
 * measured here instead of only refused.
 *
 * `edgeTangent` is e'(s) in the tool frame, any positive multiple (extraction's edgeTangents
 * are unit). Returns { sine, cosine, speed, tangentNorm, degenerate } - degenerate meaning a
 * vanishing velocity or tangent, where there is no angle to measure.
 */
export function edgeSweepSingularityMeasure(strippedMotion is map, edgePoint is Vector,
    edgeTangent is Vector, t is number) returns map
{
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * edgePoint + sample.translationDerivative;
    const transportedTangent = sample.rotation * edgeTangent;
    const speed = norm(velocity);
    const tangentNorm = norm(transportedTangent);
    if (speed < 1e-300 || tangentNorm < 1e-300)
    {
        return { "sine" : 1, "cosine" : 0, "speed" : speed, "tangentNorm" : tangentNorm,
                "degenerate" : true };
    }
    const scale = 1 / (speed * tangentNorm);
    return {
            "sine" : scale * norm(cross(transportedTangent, velocity)),
            "cosine" : scale * dot(transportedTangent, velocity),
            "speed" : speed,
            "tangentNorm" : tangentNorm,
            "degenerate" : false
        };
}

/**
 * Audit one co-edge for spec 6.4's SWEEP_EDGE_SWEEP_SINGULARITY over the (s, t) grid of the
 * SHARED extraction arrays - the same edgePoints / edgeTangents the strip function ran on, so
 * no geometry is re-derived and a reported sample index means the same thing here as it does in
 * every other consumer of that co-edge.
 *
 * The grid alone cannot decide: the singular set is generically made of ISOLATED POINTS in
 * (s, t), and a grid node lands on one only by accident. So the grid SCREENS - local minima of
 * the sine, plus the global minimum unconditionally - and, when the caller supplies the edge's
 * stripped B-spline through `strippedCurve`, Levenberg-damped Gauss-Newton decides each
 * candidate. Every refined sine is reported, so a candidate the refinement rules out is
 * recorded as ruled out rather than dropped.
 *
 * options: {
 *     strippedCurve {map} : the co-edge record's spline3d, in the curve's OWN parameter. That
 *         parameter and the arc-length sampleParameters are different parameterizations of the
 *         same curve, so each candidate's seed is obtained by inverting its edgePoint onto the
 *         curve rather than by reusing the sample parameter,
 *     sineTolerance : a refined sine (or, with no curve, a grid sine) at or under this is a
 *         singularity (default 1e-7),
 *     candidateTolerance : the SCREEN - a grid sine under this makes a local minimum a
 *         candidate (default 0.05). Not a verdict: raising it costs refinements, never
 *         correctness,
 *     mergeTolerance : two refined singularities closer than this in (s, t) are ONE (default
 *         1e-6, relative to each domain's span). Neighbouring grid minima straddling the same
 *         isolated point both converge onto it, and a detector that reports two where there is
 *         one would put the wrong count in the caller's rejection message,
 *     maxRefineIterations : default 40
 * }
 *
 * Returns {
 *     detected {boolean},
 *     singularities {array} : { sampleIndex, timeIndex, t, gridSine, refined, converged,
 *         curveParameter, refinedT, refinedSine, inversionResidual },
 *     candidates {array} : { sampleIndex, timeIndex, t, sine } - every screened node, whatever
 *         the refinement then said about it,
 *     ruledOut {array} : candidates whose refinement finished above sineTolerance,
 *     degenerateNodes {array} : { sampleIndex, timeIndex, t, speed, tangentNorm },
 *     minimumSine, minimumSampleIndex, minimumTimeIndex, minimumT,
 *     sineGrid {array} : [sampleIndex][timeIndex],
 *     nodeCount, refinedCount, mergedCount {number} : candidates that refined onto a
 *         singularity another candidate had already found
 * }
 */
export function auditEdgeSweepSingularity(strippedMotion is map, edgePoints is array,
    edgeTangents is array, tValues is array, options is map) returns map
{
    const strippedCurve = options.strippedCurve;
    const sineTolerance = options.sineTolerance == undefined ? 1e-7 : options.sineTolerance;
    const candidateTolerance = options.candidateTolerance == undefined ? 0.05 : options.candidateTolerance;
    const mergeTolerance = options.mergeTolerance == undefined ? 1e-6 : options.mergeTolerance;
    const maxRefineIterations = options.maxRefineIterations == undefined ? 40 : options.maxRefineIterations;
    const sampleCount = size(edgePoints);
    const stationCount = size(tValues);

    var sineGrid = makeArray(sampleCount);
    var degenerateNodes = [];
    var minimumSine = undefined;
    var minimumSampleIndex = 0;
    var minimumTimeIndex = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        var row = makeArray(stationCount, 1);
        for (var timeIndex = 0; timeIndex < stationCount; timeIndex += 1)
        {
            const measure = edgeSweepSingularityMeasure(strippedMotion, edgePoints[sampleIndex],
                    edgeTangents[sampleIndex], tValues[timeIndex]);
            row[timeIndex] = measure.sine;
            if (measure.degenerate)
            {
                degenerateNodes = append(degenerateNodes, {
                            "sampleIndex" : sampleIndex,
                            "timeIndex" : timeIndex,
                            "t" : tValues[timeIndex],
                            "speed" : measure.speed,
                            "tangentNorm" : measure.tangentNorm
                        });
                continue;
            }
            if (minimumSine == undefined || measure.sine < minimumSine)
            {
                minimumSine = measure.sine;
                minimumSampleIndex = sampleIndex;
                minimumTimeIndex = timeIndex;
            }
        }
        sineGrid[sampleIndex] = row;
    }
    if (minimumSine == undefined)
    {
        minimumSine = 1;
    }

    // Screen: axis-neighbour local minima under the screening threshold, and the global minimum
    // whatever its value - so the refinement always gets one shot at the grid's best point.
    var candidates = [];
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        for (var timeIndex = 0; timeIndex < stationCount; timeIndex += 1)
        {
            const sine = sineGrid[sampleIndex][timeIndex];
            const isGlobalMinimum = sampleIndex == minimumSampleIndex && timeIndex == minimumTimeIndex;
            if (!isGlobalMinimum &&
                (sine > candidateTolerance || !isAxisNeighbourMinimum(sineGrid, sampleIndex, timeIndex)))
            {
                continue;
            }
            candidates = append(candidates, {
                        "sampleIndex" : sampleIndex,
                        "timeIndex" : timeIndex,
                        "t" : tValues[timeIndex],
                        "sine" : sine
                    });
        }
    }

    const firstT = tValues[0];
    const lastT = tValues[stationCount - 1];
    const tLow = min(firstT, lastT);
    const tHigh = max(firstT, lastT);
    const curveSpan = strippedCurve == undefined ? 1 :
        curveParameterDomain(strippedCurve).high - curveParameterDomain(strippedCurve).low;
    var singularities = [];
    var ruledOut = [];
    var refinedCount = 0;
    var mergedCount = 0;
    for (var candidateIndex = 0; candidateIndex < size(candidates); candidateIndex += 1)
    {
        const candidate = candidates[candidateIndex];
        if (strippedCurve == undefined)
        {
            if (candidate.sine <= sineTolerance)
            {
                singularities = append(singularities, {
                            "sampleIndex" : candidate.sampleIndex,
                            "timeIndex" : candidate.timeIndex,
                            "t" : candidate.t,
                            "gridSine" : candidate.sine,
                            "refined" : false,
                            "converged" : false,
                            "curveParameter" : undefined,
                            "refinedT" : candidate.t,
                            "refinedSine" : candidate.sine,
                            "inversionResidual" : undefined
                        });
            }
            continue;
        }
        const inversion = curveParameterNearestPoint(strippedCurve, edgePoints[candidate.sampleIndex]);
        const refined = refineEdgeSweepSingularity(strippedMotion, strippedCurve,
                inversion.parameter, candidate.t,
                { "tLow" : tLow, "tHigh" : tHigh, "maxIterations" : maxRefineIterations });
        refinedCount += 1;
        const record = {
                "sampleIndex" : candidate.sampleIndex,
                "timeIndex" : candidate.timeIndex,
                "t" : candidate.t,
                "gridSine" : candidate.sine,
                "refined" : true,
                "converged" : refined.converged,
                "curveParameter" : refined.curveParameter,
                "refinedT" : refined.t,
                "refinedSine" : refined.sine,
                "inversionResidual" : inversion.residual
            };
        if (refined.sine > sineTolerance)
        {
            ruledOut = append(ruledOut, record);
            continue;
        }
        if (!singularityAlreadyFound(singularities, refined.curveParameter, refined.t,
                mergeTolerance * max(curveSpan, 1e-30), mergeTolerance * max(tHigh - tLow, 1e-30)))
        {
            singularities = append(singularities, record);
        }
        else
        {
            mergedCount += 1;
        }
    }

    return {
            "detected" : size(singularities) > 0,
            "singularities" : singularities,
            "candidates" : candidates,
            "ruledOut" : ruledOut,
            "degenerateNodes" : degenerateNodes,
            "minimumSine" : minimumSine,
            "minimumSampleIndex" : minimumSampleIndex,
            "minimumTimeIndex" : minimumTimeIndex,
            "minimumT" : tValues[minimumTimeIndex],
            "sineGrid" : sineGrid,
            "nodeCount" : sampleCount * stationCount,
            "refinedCount" : refinedCount,
            "mergedCount" : mergedCount
        };
}

export function auditEdgeSweepSingularity(strippedMotion is map, edgePoints is array,
    edgeTangents is array, tValues is array) returns map
{
    return auditEdgeSweepSingularity(strippedMotion, edgePoints, edgeTangents, tValues, {});
}

/**
 * Refine one edge-sweep-singularity candidate to the point where the transported edge tangent
 * and the velocity are actually parallel, in the edge curve's own parameter and in t.
 *
 * Least squares rather than a square solve, on purpose: the residual is the 3-vector
 * cross(unit A e', unit velocity) against two unknowns, and its norm IS the sine being driven
 * to zero - so the iteration that locates the point also measures how singular the point it
 * found is. Levenberg damping with a halving line search keeps a candidate that is only a near
 * miss from wandering off the domain in search of a zero that does not exist.
 *
 * options: { tLow, tHigh (the t clamp; default is the seed itself, i.e. s only),
 *     maxIterations (default 40), sineTarget (default 1e-15) }.
 *
 * Returns { curveParameter, t, sine, converged, iterations }.
 */
export function refineEdgeSweepSingularity(strippedMotion is map, strippedCurve is map,
    seedParameter is number, seedT is number, options is map) returns map
{
    const domain = curveParameterDomain(strippedCurve);
    const tLow = options.tLow == undefined ? seedT : options.tLow;
    const tHigh = options.tHigh == undefined ? seedT : options.tHigh;
    const maxIterations = options.maxIterations == undefined ? 40 : options.maxIterations;
    const sineTarget = options.sineTarget == undefined ? 1e-15 : options.sineTarget;
    const parameterStep = 1e-6 * max(domain.high - domain.low, 1e-6);
    const timeStep = 1e-6 * max(tHigh - tLow, 1e-6);

    var s = clampToRange(seedParameter, domain.low, domain.high);
    var t = clampToRange(seedT, tLow, tHigh);
    var residual = edgeSingularityResidual(strippedMotion, strippedCurve, s, t);
    var sine = norm(residual);
    var damping = 1e-10;
    var iterations = 0;
    for (var iteration = 0; iteration < maxIterations; iteration += 1)
    {
        iterations = iteration + 1;
        if (sine <= sineTarget)
        {
            break;
        }
        const sPlus = edgeSingularityResidual(strippedMotion, strippedCurve,
                clampToRange(s + parameterStep, domain.low, domain.high), t);
        const sMinus = edgeSingularityResidual(strippedMotion, strippedCurve,
                clampToRange(s - parameterStep, domain.low, domain.high), t);
        const tPlus = edgeSingularityResidual(strippedMotion, strippedCurve, s,
                clampToRange(t + timeStep, tLow, tHigh));
        const tMinus = edgeSingularityResidual(strippedMotion, strippedCurve, s,
                clampToRange(t - timeStep, tLow, tHigh));
        const sColumn = (1 / (2 * parameterStep)) * (sPlus - sMinus);
        const tColumn = (1 / (2 * timeStep)) * (tPlus - tMinus);
        const a11 = squaredNorm(sColumn);
        const a12 = dot(sColumn, tColumn);
        const a22 = squaredNorm(tColumn);
        const g1 = dot(sColumn, residual);
        const g2 = dot(tColumn, residual);
        if (a11 + a22 < 1e-300)
        {
            break;
        }
        var trialDamping = max(damping * (a11 + a22), 1e-300);
        var accepted = false;
        for (var attempt = 0; attempt < 12; attempt += 1)
        {
            const determinant = (a11 + trialDamping) * (a22 + trialDamping) - a12 * a12;
            if (abs(determinant) < 1e-300)
            {
                trialDamping = trialDamping * 10;
                continue;
            }
            const stepS = (-g1 * (a22 + trialDamping) + g2 * a12) / determinant;
            const stepT = (-g2 * (a11 + trialDamping) + g1 * a12) / determinant;
            const trialS = clampToRange(s + stepS, domain.low, domain.high);
            const trialT = clampToRange(t + stepT, tLow, tHigh);
            const trialResidual = edgeSingularityResidual(strippedMotion, strippedCurve, trialS, trialT);
            const trialSine = norm(trialResidual);
            if (trialSine < sine)
            {
                s = trialS;
                t = trialT;
                residual = trialResidual;
                sine = trialSine;
                damping = max(damping * 0.3, 1e-16);
                accepted = true;
                break;
            }
            trialDamping = trialDamping * 10;
        }
        if (!accepted)
        {
            break;
        }
    }
    return {
            "curveParameter" : s,
            "t" : t,
            "sine" : sine,
            "converged" : sine <= sineTarget * 1e6,
            "iterations" : iterations
        };
}

/**
 * The parameter on a stripped B-spline curve whose point is nearest `target`: a coarse scan of
 * the knot span for a bracket, then Newton on d/ds |e(s) - target|^2 / 2 = <e - target, e'>.
 * This is the bridge between a co-edge's arc-length sample parameters and its spline's own
 * parameter - two different parameterizations of the same curve, which is why a sample index
 * cannot be handed to the refinement directly.
 *
 * Returns { parameter, residual } - the residual being |e(s) - target|.
 */
export function curveParameterNearestPoint(strippedCurve is map, target is Vector) returns map
{
    const domain = curveParameterDomain(strippedCurve);
    const scanCount = 32;
    var bestParameter = domain.low;
    var bestDistanceSquared = undefined;
    for (var scanIndex = 0; scanIndex <= scanCount; scanIndex += 1)
    {
        const parameter = domain.low + (domain.high - domain.low) * scanIndex / scanCount;
        const distanceSquared = squaredNorm(
                evaluateBSplineCurveDerivatives(strippedCurve, parameter, 0)[0] - target);
        if (bestDistanceSquared == undefined || distanceSquared < bestDistanceSquared)
        {
            bestDistanceSquared = distanceSquared;
            bestParameter = parameter;
        }
    }
    var parameter = bestParameter;
    for (var iteration = 0; iteration < 20; iteration += 1)
    {
        const derivatives = evaluateBSplineCurveDerivatives(strippedCurve, parameter, 2);
        const offset = derivatives[0] - target;
        const value = dot(offset, derivatives[1]);
        const slope = squaredNorm(derivatives[1]) + dot(offset, derivatives[2]);
        if (abs(slope) < 1e-300)
        {
            break;
        }
        const next = clampToRange(parameter - value / slope, domain.low, domain.high);
        const converged = abs(next - parameter) < 1e-14 * max(abs(domain.high - domain.low), 1);
        parameter = next;
        if (converged)
        {
            break;
        }
    }
    return {
            "parameter" : parameter,
            "residual" : norm(evaluateBSplineCurveDerivatives(strippedCurve, parameter, 0)[0] - target)
        };
}

/** The knot-parameter span of a stripped B-spline curve. */
export function curveParameterDomain(strippedCurve is map) returns map
{
    return {
            "low" : strippedCurve.knots[strippedCurve.degree],
            "high" : strippedCurve.knots[size(strippedCurve.knots) - strippedCurve.degree - 1]
        };
}

// ============================= Internal helpers =============================

/** cross(unit transported edge tangent, unit velocity) at (s, t); its norm is the sine. */
function edgeSingularityResidual(strippedMotion is map, strippedCurve is map, s is number,
    t is number) returns Vector
{
    const derivatives = evaluateBSplineCurveDerivatives(strippedCurve, s, 1);
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * derivatives[0] + sample.translationDerivative;
    const transportedTangent = sample.rotation * derivatives[1];
    const speed = norm(velocity);
    const tangentNorm = norm(transportedTangent);
    if (speed < 1e-300 || tangentNorm < 1e-300)
    {
        return vector(1, 0, 0);
    }
    return cross((1 / tangentNorm) * transportedTangent, (1 / speed) * velocity);
}

/** True when some already-recorded singularity sits at the same refined (s, t). */
function singularityAlreadyFound(singularities is array, curveParameter is number, t is number,
    parameterTolerance is number, timeTolerance is number) returns boolean
{
    for (var index = 0; index < size(singularities); index += 1)
    {
        if (abs(singularities[index].curveParameter - curveParameter) <= parameterTolerance &&
            abs(singularities[index].refinedT - t) <= timeTolerance)
        {
            return true;
        }
    }
    return false;
}

/** True when a node is no larger than each of its existing axis neighbours. */
function isAxisNeighbourMinimum(sineGrid is array, sampleIndex is number, timeIndex is number) returns boolean
{
    const sine = sineGrid[sampleIndex][timeIndex];
    if (sampleIndex > 0 && sineGrid[sampleIndex - 1][timeIndex] < sine)
    {
        return false;
    }
    if (sampleIndex + 1 < size(sineGrid) && sineGrid[sampleIndex + 1][timeIndex] < sine)
    {
        return false;
    }
    if (timeIndex > 0 && sineGrid[sampleIndex][timeIndex - 1] < sine)
    {
        return false;
    }
    if (timeIndex + 1 < size(sineGrid[sampleIndex]) && sineGrid[sampleIndex][timeIndex + 1] < sine)
    {
        return false;
    }
    return true;
}

/**
 * Bisection-safeguarded false position on f_t inside a section bracket, with every iterate
 * corrected back onto f(., ., tGlobal) = 0 first - so the root found is a point OF THE SECTION
 * at which f_t vanishes, not a point of the chord between two section samples.
 */
function refineSectionTangency(strippedMotion is map, strippedSurface is map, tGlobal is number,
    lowUv is array, highUv is array, lowDerivative is number, highDerivative is number,
    segmentIndex is number, sectionTolerance is number, maxIterations is number) returns map
{
    var lowFraction = 0;
    var highFraction = 1;
    var lowValue = lowDerivative;
    var highValue = highDerivative;
    var fraction = 0.5;
    var uv = lowUv;
    var gradient = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface,
        lowUv[0], lowUv[1], tGlobal);
    for (var iteration = 0; iteration < maxIterations; iteration += 1)
    {
        // False position while the two ends still bracket, bisection whenever it would step
        // outside - the usual safeguard, and here it also keeps the corrector's seed close.
        var candidate = 0.5 * (lowFraction + highFraction);
        if (highValue != lowValue)
        {
            const secant = lowFraction - lowValue * (highFraction - lowFraction) / (highValue - lowValue);
            if (secant > lowFraction && secant < highFraction)
            {
                candidate = secant;
            }
        }
        const seed = [lowUv[0] + candidate * (highUv[0] - lowUv[0]),
            lowUv[1] + candidate * (highUv[1] - lowUv[1])];
        uv = correctPointOntoSection(strippedMotion, strippedSurface, tGlobal, seed, sectionTolerance);
        gradient = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, uv[0], uv[1], tGlobal);
        fraction = candidate;
        const value = gradient.tDerivative;
        if (value == 0 || highFraction - lowFraction < 1e-14)
        {
            break;
        }
        if (value * lowValue > 0)
        {
            lowFraction = candidate;
            lowValue = value;
        }
        else
        {
            highFraction = candidate;
            highValue = value;
        }
    }
    return {
            "segmentIndex" : segmentIndex,
            "fraction" : fraction,
            "uv" : uv,
            "timeDerivative" : gradient.tDerivative,
            "timeDerivativeScale" : gradient.tDerivativeScale,
            "sectionResidual" : abs(gradient.value),
            "atSample" : undefined
        };
}

/** Sign with a dead band: zero inside the tolerance rather than an arbitrary side of it. */
function signWithDeadband(value is number, tolerance is number) returns number
{
    if (abs(value) <= tolerance)
    {
        return 0;
    }
    return value > 0 ? 1 : -1;
}

/** True when some tangency was located on the given march segment. */
function tangencyOnSegment(tangencies is array, segmentIndex is number) returns boolean
{
    for (var index = 0; index < size(tangencies); index += 1)
    {
        if (tangencies[index].segmentIndex == segmentIndex)
        {
            return true;
        }
    }
    return false;
}

/** Append unless the point repeats the last one - split points are shared, not duplicated. */
function appendUnlessDuplicate(points is array, point is array) returns array
{
    const last = points[size(points) - 1];
    if ((point[0] - last[0]) ^ 2 + (point[1] - last[1]) ^ 2 <= 1e-28)
    {
        return points;
    }
    return append(points, point);
}
