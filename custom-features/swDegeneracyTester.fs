FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

// Non-standard imports - same-document element imports; fix the ids/versions on paste. For MCP
// harness runs the payload inlines the module bodies instead of resolving these lines.
import(path : "0000000000000000000000a1", version : "0000000000000000000000a2"); //swDegeneracy.fs
import(path : "0000000000000000000000cc", version : "0000000000000000000000dd"); //swEnvelopeMath.fs, swFunnelSolver.fs
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * Self test for swDegeneracy.fs - spec section 6.4's detectors 2 and 3
 * (SWEEP_FUNNEL_TANGENT_TO_SLICE, SWEEP_EDGE_SWEEP_SINGULARITY). Selection-free and
 * context-free: both fixtures are polynomials whose degeneracies are known in CLOSED FORM, and
 * every number the detectors report is checked against that closed form rather than against a
 * previous run. A detector is only worth having if it fires where it must and stays silent where
 * it must, so each feature here runs BOTH halves.
 *
 * DETECTOR 2's fixture is the paraboloid-cubic patch S = (u, v, u^2/2 + (v - 1/2)^3 / 6) at
 * degrees (2, 3), under a translation whose velocity at t = 1/2 is (1, 0, 1/2) with acceleration
 * (0, 1, c). With A = I the whole audit reduces to two lines:
 *
 *     N = S_u x S_v = (-u, -(v - 1/2)^2 / 2, 1)
 *     f   = <N, b'>  = 1/2 - u              so the section p-curve is EXACTLY u = 1/2
 *     f_t = <N, b''> = c - (v - 1/2)^2 / 2  so f_t vanishes at v = 1/2 +/- sqrt(2c)
 *
 * One number, c, moves the fixture through all three verdicts, and it is the same fixture each
 * time - so a detector that fires on the wrong one cannot blame a change of geometry:
 *
 *     c = +0.03 : two interior tangencies at v = 1/2 +/- sqrt(0.06). Fires; section splits in 3.
 *     c = -0.03 : no crossing, |f_t| dips to 0.03 at v = 1/2. Near tangency; no split.
 *     c = -0.30 : |f_t| >= 0.3 everywhere. Silent.
 *
 * The fourth case is the funnel solver's OWN section fixture - the slant patch under the
 * constant velocity (1, 0, 0.06) that its section marching already passes on - and it is the
 * case that justifies the second reference scale. There f_t is identically zero because a
 * constant velocity has no acceleration, and so is f_t's own Cauchy-Schwarz bound: the ratio of
 * the two is 0/0. Only |f| itself is left to measure against, and with it the verdict comes out
 * stationarySection rather than a tangency at every point of the section.
 *
 * DETECTOR 3's fixture is the cubic edge e(s) = (s, s^2/2, c s^3 / 6), whose tangent
 * e'(s) = (1, s, c s^2 / 2) is polynomial, under the translation velocity
 * b'(t) = (1, t, c t^2 / 2 + eps (t - t*)). Both have first component 1, so parallelism forces
 * s = t from the second component and then eps (t - t*) = 0 from the third: the singular set is
 * the single ISOLATED POINT (s, t) = (t*, t*), which is the generic case the spec argues for.
 * The grid is deliberately 12 x 12 on i/11 so that no node lands on t* = 1/2 - the grid's own
 * minimum sine comes out near 0.0136, four orders above the 1e-7 verdict threshold, so the test
 * fails unless the refinement does the work. The silent half turns the velocity to (0, 0.2, 1),
 * whose x component 0 can never match the tangent's 1.
 *
 * MEASURED (2026-08-23, live PASS first try, both features, 25 + 17 checks):
 *
 * Detector 2. Fixture algebra 5.6e-17 (f) and 1.7e-16 (f_t) against their closed forms.
 *   c = +0.03: 21 marched points, 2 tangencies at v = 0.2550510257216817 and 0.7449489742783185
 *     against 1/2 -/+ 0.2449489742783178 - worst dv 7.8e-16, du 0, |f_t| 5.6e-17, |f| 0. Split
 *     3 pieces (7/11/7), 0 dropped, seams equal exactly, 0 f_t sign changes inside any piece,
 *     resample of all three at 7 samples worst |f| 0, and 0 near tangencies.
 *   c = -0.03: 0 tangencies, 1 near tangency at v = 1/2, |f_t| 0.030000 (2.7 % of scale), 1 piece.
 *   c = -0.30: 0 tangencies, 0 near tangencies, minimum |f_t| 0.300000 (25.5 % of scale) - one
 *     order clear of the near-tangency threshold, which is the separation the two cases are for.
 *   Slant fixture under constant velocity: f_t scale EXACTLY 0, f scale 1.0044, floor 1.0e-12,
 *     largest |f_t| 0, verdict stationary. On the first reference alone this section would have
 *     reported a tangency at every sample, which is why the second one exists.
 *
 * Detector 3. Spline e'(s) against its polynomial 1.3e-16; the normalized sine over all 144 grid
 *   nodes against its closed form 2.2e-16; at the exact (1/2, 1/2) sine 0 and cosine 1.
 *   Grid only: minimum sine 0.01360141 at (6/11, 6/11), 4 candidates, NOT detected - the grid
 *     misses the singularity by four orders, as the fixture intends.
 *   With the curve: 4 candidates -> 1 singularity, 3 merged, found at (s, t) = (0.5, 0.5) with
 *     refined sine 0 from a candidate whose grid sine was 0.0447; inversion residual 1.2e-16.
 *   Crossing velocity: minimum sine 0.8598, 1 candidate, 1 ruled out, not detected, 0 degenerate.
 */

// ===================== Detector 2: funnel tangent to slice =====================

annotation { "Feature Type Name" : "Sweep Section Tangency Self Test" }
export const sweepSectionTangencySelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = tangencyFixtureSurface();
        const station = 0.5;
        const marchOptions = { "stepSize" : 0.05, "maxSteps" : 400, "tolerance" : 1e-12 };

        // ---------- The fixture's own algebra, before any detector runs ----------
        // If f is not 1/2 - u and f_t is not c - (v - 1/2)^2 / 2 then every verdict below is
        // about a different surface than the one this test reasons about.
        const algebraOffset = 0.03;
        const algebraMotion = tangencyFixtureMotion(algebraOffset);
        var worstValue = 0;
        var worstTimeDerivative = 0;
        for (var uIndex = 0; uIndex <= 4; uIndex += 1)
        {
            for (var vIndex = 0; vIndex <= 4; vIndex += 1)
            {
                const u = uIndex / 4;
                const v = vIndex / 4;
                worstValue = max(worstValue, abs(evaluateEnvelopePointwise(algebraMotion, surface,
                                u, v, station) - (0.5 - u)));
                worstTimeDerivative = max(worstTimeDerivative,
                    abs(evaluateEnvelopeTimeDerivativePointwise(algebraMotion, surface, u, v, station) -
                        (algebraOffset - 0.5 * (v - 0.5) ^ 2)));
            }
        }
        println("[SECTION TANGENCY] fixture algebra at t = 0.5: worst |f - (1/2 - u)| " ~ worstValue ~
            ", worst |f_t - (c - (v-1/2)^2/2)| " ~ worstTimeDerivative);
        tally = checkWithin(tally, worstValue, 1e-15, "f against its closed form");
        tally = checkWithin(tally, worstTimeDerivative, 1e-15, "f_t against its closed form");

        // ---------- Case A: two interior tangencies; the section must split in three ----------
        const marched = marchSectionCurve(algebraMotion, surface, station, [0.5, 0], [0.5, 1], marchOptions);
        tally = checkThat(tally, marched.reachedEnd,
            "the section march did not reach the end anchor, so the audit ran on a partial section.");
        const audit = auditSectionTangency(algebraMotion, surface, station, marched.uvPoints,
                { "nearTangencyTolerance" : 0.05 });
        const expectedV = sqrt(2 * algebraOffset);
        println("[SECTION TANGENCY] c = +0.03: " ~ size(marched.uvPoints) ~ " marched points, " ~
            size(audit.tangencies) ~ " tangencies, " ~ size(audit.nearTangencies) ~
            " near tangencies, stationary " ~ audit.stationarySection ~ ", scale " ~
            audit.scaleReference ~ ", value scale " ~ audit.valueScaleReference ~ ", floor " ~
            audit.stationaryFloor ~ ", worst section |f| " ~ audit.worstSectionResidual);
        tally = checkThat(tally, audit.detected && !audit.stationarySection,
            "the two-tangency case reported detected " ~ audit.detected ~ " stationary " ~
            audit.stationarySection ~ ".");
        tally = checkThat(tally, size(audit.tangencies) == 2,
            "the two-tangency case found " ~ size(audit.tangencies) ~ " tangencies, not 2.");
        // The samples flanking each tangency dip to a |f_t| of 0.00125 - 0.1% of the scale, well
        // inside the near-tangency threshold - so this also checks that one event is not counted
        // twice, once as a tangency and again as the near tangency beside it.
        tally = checkThat(tally, size(audit.nearTangencies) == 0,
            "the two-tangency case also reported " ~ size(audit.nearTangencies) ~
            " near tangencies, double-counting its own tangencies.");
        if (size(audit.tangencies) == 2)
        {
            var worstTangencyU = 0;
            var worstTangencyV = 0;
            var worstTangencyDerivative = 0;
            var worstTangencyResidual = 0;
            for (var index = 0; index < 2; index += 1)
            {
                const tangency = audit.tangencies[index];
                const predictedV = index == 0 ? 0.5 - expectedV : 0.5 + expectedV;
                worstTangencyU = max(worstTangencyU, abs(tangency.uv[0] - 0.5));
                worstTangencyV = max(worstTangencyV, abs(tangency.uv[1] - predictedV));
                worstTangencyDerivative = max(worstTangencyDerivative, abs(tangency.timeDerivative));
                worstTangencyResidual = max(worstTangencyResidual, tangency.sectionResidual);
            }
            println("[SECTION TANGENCY] tangencies at v = " ~ audit.tangencies[0].uv[1] ~ " and " ~
                audit.tangencies[1].uv[1] ~ " against 1/2 -/+ " ~ expectedV ~ ": worst dv " ~
                worstTangencyV ~ ", worst du " ~ worstTangencyU ~ ", worst |f_t| " ~
                worstTangencyDerivative ~ ", worst |f| " ~ worstTangencyResidual);
            tally = checkWithin(tally, worstTangencyV, 1e-11, "the refined tangency v");
            tally = checkWithin(tally, worstTangencyU, 1e-14, "the refined tangency u");
            tally = checkWithin(tally, worstTangencyDerivative, 1e-12, "|f_t| at the refined tangency");
            tally = checkWithin(tally, worstTangencyResidual, 1e-12, "|f| at the refined tangency");
        }

        const split = splitSectionAtTangencies(marched.uvPoints, audit);
        println("[SECTION TANGENCY] split: " ~ size(split.pieces) ~ " pieces (" ~
            pieceSizes(split.pieces) ~ "), splitCount " ~ split.splitCount ~ ", dropped " ~
            split.droppedPieces);
        tally = checkThat(tally, size(split.pieces) == 3 && split.droppedPieces == 0,
            "the split produced " ~ size(split.pieces) ~ " pieces and dropped " ~
            split.droppedPieces ~ ", not 3 and 0.");
        if (size(split.pieces) == 3)
        {
            // Spec 2.3: the seam is ONE value, so exact equality is the right test - a
            // tolerance here would pass a split that merely agreed to tolerance.
            var seamsShared = true;
            for (var pieceIndex = 0; pieceIndex + 1 < 3; pieceIndex += 1)
            {
                const tail = split.pieces[pieceIndex][size(split.pieces[pieceIndex]) - 1];
                const head = split.pieces[pieceIndex + 1][0];
                seamsShared = seamsShared && tail[0] == head[0] && tail[1] == head[1];
            }
            tally = checkThat(tally, seamsShared,
                "adjacent pieces do not share their split point exactly.");

            // The point of splitting: no piece's interior crosses f_t = 0.
            var interiorSignChanges = 0;
            for (var pieceIndex = 0; pieceIndex < 3; pieceIndex += 1)
            {
                const piece = split.pieces[pieceIndex];
                var previousSign = 0;
                for (var pointIndex = 1; pointIndex + 1 < size(piece); pointIndex += 1)
                {
                    const derivative = evaluateEnvelopeTimeDerivativePointwise(algebraMotion, surface,
                            piece[pointIndex][0], piece[pointIndex][1], station);
                    const currentSign = derivative > 0 ? 1 : -1;
                    if (previousSign != 0 && currentSign != previousSign)
                    {
                        interiorSignChanges += 1;
                    }
                    previousSign = currentSign;
                }
            }
            println("[SECTION TANGENCY] f_t sign changes inside the three pieces: " ~ interiorSignChanges);
            tally = checkThat(tally, interiorSignChanges == 0,
                "a piece's interior still crosses f_t = 0 " ~ interiorSignChanges ~ " times.");

            const resampled = resampleSectionPieces(algebraMotion, surface, station, split.pieces, 7, 1e-12);
            println("[SECTION TANGENCY] resampled 3 pieces at 7 samples: worst |f| " ~ resampled.worstResidual);
            tally = checkWithin(tally, resampled.worstResidual, 1e-12,
                "the worst |f| over the resampled pieces");
        }

        // ---------- Case B: a near tangency, reported and NOT split ----------
        const nearMotion = tangencyFixtureMotion(-0.03);
        const nearMarched = marchSectionCurve(nearMotion, surface, station, [0.5, 0], [0.5, 1], marchOptions);
        const nearAudit = auditSectionTangency(nearMotion, surface, station, nearMarched.uvPoints,
                { "nearTangencyTolerance" : 0.05 });
        const nearSplit = splitSectionAtTangencies(nearMarched.uvPoints, nearAudit);
        println("[SECTION TANGENCY] c = -0.03: " ~ size(nearAudit.tangencies) ~ " tangencies, " ~
            size(nearAudit.nearTangencies) ~ " near tangencies, minimum |f_t| " ~
            nearAudit.minimumMagnitude ~ " (relative " ~ nearAudit.minimumRelative ~ "), pieces " ~
            size(nearSplit.pieces));
        tally = checkThat(tally, !nearAudit.detected && !nearAudit.stationarySection &&
                size(nearAudit.tangencies) == 0,
            "the near-tangency case reported " ~ size(nearAudit.tangencies) ~ " tangencies.");
        tally = checkThat(tally, size(nearAudit.nearTangencies) == 1,
            "the near-tangency case reported " ~ size(nearAudit.nearTangencies) ~
            " near tangencies, not 1.");
        tally = checkWithin(tally, nearAudit.minimumMagnitude - 0.03, 1e-14,
            "the near tangency's own |f_t| against the closed-form 0.03");
        if (size(nearAudit.nearTangencies) == 1)
        {
            tally = checkWithin(tally, nearAudit.nearTangencies[0].uv[1] - 0.5, 1e-12,
                "the near tangency's v against the closed-form 1/2");
        }
        tally = checkThat(tally, size(nearSplit.pieces) == 1 && nearSplit.splitCount == 0,
            "a near tangency split the section into " ~ size(nearSplit.pieces) ~ " pieces.");

        // ---------- Case C: silent ----------
        const quietMotion = tangencyFixtureMotion(-0.3);
        const quietMarched = marchSectionCurve(quietMotion, surface, station, [0.5, 0], [0.5, 1], marchOptions);
        const quietAudit = auditSectionTangency(quietMotion, surface, station, quietMarched.uvPoints,
                { "nearTangencyTolerance" : 0.05 });
        println("[SECTION TANGENCY] c = -0.30: " ~ size(quietAudit.tangencies) ~ " tangencies, " ~
            size(quietAudit.nearTangencies) ~ " near tangencies, minimum |f_t| " ~
            quietAudit.minimumMagnitude ~ " (relative " ~ quietAudit.minimumRelative ~ ")");
        tally = checkThat(tally, !quietAudit.detected && !quietAudit.stationarySection &&
                size(quietAudit.tangencies) == 0 && size(quietAudit.nearTangencies) == 0,
            "the silent case reported " ~ size(quietAudit.tangencies) ~ " tangencies and " ~
            size(quietAudit.nearTangencies) ~ " near tangencies.");
        tally = checkWithin(tally, quietAudit.minimumMagnitude - 0.3, 1e-14,
            "the silent case's minimum |f_t| against the closed-form 0.3");

        // ---------- Case D: the funnel solver's own section fixture, constant velocity ----------
        // f_t is identically zero here and so is its own scale, which is the whole reason the
        // audit carries a second reference. The verdict must be stationary, never a tangency.
        const slantSurface = slantFixtureSurface();
        const slantMotion = constantVelocityTranslationMotion(vector(1, 0, 0.06));
        const slantMarched = marchSectionCurve(slantMotion, slantSurface, station, [0.8, 0],
                [(0.24 - 0.05) / 0.3, 1], { "stepSize" : 0.05, "maxSteps" : 400, "tolerance" : 1e-12 });
        const slantAudit = auditSectionTangency(slantMotion, slantSurface, station, slantMarched.uvPoints,
                { "nearTangencyTolerance" : 0.05 });
        const slantSplit = splitSectionAtTangencies(slantMarched.uvPoints, slantAudit);
        println("[SECTION TANGENCY] slant fixture, constant velocity: stationary " ~
            slantAudit.stationarySection ~ ", |f_t| scale " ~ slantAudit.scaleReference ~
            ", |f| scale " ~ slantAudit.valueScaleReference ~ ", floor " ~ slantAudit.stationaryFloor ~
            ", largest |f_t| " ~ largestOf(slantAudit.sampleTimeDerivatives) ~ ", pieces " ~
            size(slantSplit.pieces));
        tally = checkThat(tally, slantAudit.stationarySection && !slantAudit.detected &&
                size(slantAudit.tangencies) == 0,
            "the constant-velocity section came out stationary " ~ slantAudit.stationarySection ~
            " with " ~ size(slantAudit.tangencies) ~ " tangencies.");
        tally = checkThat(tally, slantAudit.valueScaleReference > 0.5,
            "the |f| reference on the slant fixture is " ~ slantAudit.valueScaleReference ~
            ", too small to carry the stationary test.");
        tally = checkThat(tally, slantAudit.scaleReference < 1e-14,
            "the constant-velocity fixture's own f_t scale is " ~ slantAudit.scaleReference ~
            ", so it is not the degenerate reference this case is meant to exercise.");
        tally = checkThat(tally, size(slantSplit.pieces) == 1,
            "a stationary section was split into " ~ size(slantSplit.pieces) ~ " pieces.");

        reportCheckTally(context, id, "SECTION TANGENCY", tally,
            "detector 2 fires on two closed-form tangencies, splits the section into three " ~
            "pieces sharing their seams exactly, reports the near tangency without splitting, " ~
            "and stays silent on both the far case and the constant-velocity section.");
    });

// ===================== Detector 3: edge sweep singularity =====================

annotation { "Feature Type Name" : "Sweep Edge Singularity Self Test" }
export const sweepEdgeSingularitySelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const curvature = 1.2;
        const epsilon = 0.35;
        const singularT = 0.5;
        const edgeCurve = singularEdgeCurve(curvature);
        const gridCount = 12;

        // The (s, t) grid deliberately misses t* = 1/2: with 12 nodes at i/11 the nearest are
        // 5/11 and 6/11, so nothing here can succeed by landing on the answer.
        var sampleParameters = makeArray(gridCount);
        var edgePoints = makeArray(gridCount);
        var edgeTangents = makeArray(gridCount);
        var tValues = makeArray(gridCount);
        for (var index = 0; index < gridCount; index += 1)
        {
            const s = index / (gridCount - 1);
            const derivatives = evaluateBSplineCurveDerivatives(edgeCurve, s, 1);
            sampleParameters[index] = s;
            edgePoints[index] = derivatives[0];
            edgeTangents[index] = (1 / norm(derivatives[1])) * derivatives[1];
            tValues[index] = s;
        }

        // ---------- The fixture's own algebra ----------
        // e'(s) = (1, s, c s^2 / 2) from the spline against the polynomial it is meant to be.
        var worstTangent = 0;
        for (var index = 0; index < gridCount; index += 1)
        {
            const s = sampleParameters[index];
            const predicted = vector(1, s, 0.5 * curvature * s ^ 2);
            worstTangent = max(worstTangent, norm(edgeTangents[index] -
                        (1 / norm(predicted)) * predicted));
        }
        println("[EDGE SINGULARITY] fixture algebra: worst unit e'(s) against (1, s, c s^2 / 2): " ~
            worstTangent);
        tally = checkWithin(tally, worstTangent, 1e-15, "the spline edge tangent against its polynomial");

        const singularMotion = singularEdgeMotion(curvature, epsilon, singularT);

        // The singularity itself, from the closed forms alone: at (t*, t*) the velocity IS the
        // edge tangent, so the measure must read zero without any search.
        const atSingularity = edgeSweepSingularityMeasure(singularMotion,
                evaluateBSplineCurveDerivatives(edgeCurve, singularT, 0)[0],
                evaluateBSplineCurveDerivatives(edgeCurve, singularT, 1)[1], singularT);
        println("[EDGE SINGULARITY] closed-form check at (s, t) = (0.5, 0.5): sine " ~
            atSingularity.sine ~ ", cosine " ~ atSingularity.cosine ~ ", speed " ~ atSingularity.speed);
        tally = checkWithin(tally, atSingularity.sine, 1e-15, "the sine at the closed-form singularity");
        tally = checkThat(tally, atSingularity.cosine > 0.999999 && !atSingularity.degenerate,
            "the velocity at the singularity is not the transported tangent: cosine " ~
            atSingularity.cosine ~ ".");

        // The measure against the closed form over the whole grid - the load-bearing cross-path
        // check, spline evaluation and motion sampling against hand-written polynomials.
        var worstMeasure = 0;
        for (var sampleIndex = 0; sampleIndex < gridCount; sampleIndex += 1)
        {
            for (var timeIndex = 0; timeIndex < gridCount; timeIndex += 1)
            {
                const s = sampleParameters[sampleIndex];
                const t = tValues[timeIndex];
                const measure = edgeSweepSingularityMeasure(singularMotion, edgePoints[sampleIndex],
                        edgeTangents[sampleIndex], t);
                const tangent = vector(1, s, 0.5 * curvature * s ^ 2);
                const velocity = vector(1, t, 0.5 * curvature * t ^ 2 + epsilon * (t - singularT));
                const predicted = norm(cross(tangent, velocity)) / (norm(tangent) * norm(velocity));
                worstMeasure = max(worstMeasure, abs(measure.sine - predicted));
            }
        }
        println("[EDGE SINGULARITY] the normalized sine over " ~ (gridCount * gridCount) ~
            " nodes against its closed form: worst " ~ worstMeasure);
        tally = checkWithin(tally, worstMeasure, 1e-15, "the grid sine against its closed form");

        // ---------- The grid alone must NOT be enough ----------
        const gridOnly = auditEdgeSweepSingularity(singularMotion, edgePoints, edgeTangents, tValues);
        println("[EDGE SINGULARITY] grid only (no curve): minimum sine " ~ gridOnly.minimumSine ~
            " at sample " ~ gridOnly.minimumSampleIndex ~ ", t " ~ gridOnly.minimumT ~
            "; candidates " ~ size(gridOnly.candidates) ~ ", detected " ~ gridOnly.detected);
        tally = checkThat(tally, gridOnly.minimumSine > 1e-3 && gridOnly.minimumSine < 0.05,
            "the grid's minimum sine is " ~ gridOnly.minimumSine ~
            ", so this grid either lands on the singularity or never screens it.");
        tally = checkThat(tally, !gridOnly.detected,
            "the grid alone claimed to detect the singularity, so the refinement is untested.");
        tally = checkThat(tally, size(gridOnly.candidates) >= 1,
            "the grid screened " ~ size(gridOnly.candidates) ~ " candidates, so nothing would refine.");

        // ---------- With the curve, the refinement must find the isolated point ----------
        const refinedAudit = auditEdgeSweepSingularity(singularMotion, edgePoints, edgeTangents,
                tValues, { "strippedCurve" : edgeCurve });
        println("[EDGE SINGULARITY] refined: " ~ size(refinedAudit.candidates) ~ " candidates, " ~
            size(refinedAudit.singularities) ~ " singularities, " ~ size(refinedAudit.ruledOut) ~
            " ruled out, " ~ refinedAudit.mergedCount ~ " merged, detected " ~ refinedAudit.detected);
        tally = checkThat(tally, refinedAudit.detected,
            "the refinement did not detect the isolated singularity at (0.5, 0.5).");
        tally = checkThat(tally, size(refinedAudit.singularities) == 1,
            "the refinement reported " ~ size(refinedAudit.singularities) ~ " singularities, not 1.");
        if (size(refinedAudit.singularities) >= 1)
        {
            const found = refinedAudit.singularities[0];
            println("[EDGE SINGULARITY] found (s, t) = (" ~ found.curveParameter ~ ", " ~
                found.refinedT ~ ") against (0.5, 0.5): sine " ~ found.refinedSine ~
                " from a grid sine of " ~ found.gridSine ~ ", inversion residual " ~
                found.inversionResidual);
            tally = checkWithin(tally, found.curveParameter - singularT, 1e-8,
                "the refined curve parameter against the closed-form 1/2");
            tally = checkWithin(tally, found.refinedT - singularT, 1e-8,
                "the refined t against the closed-form 1/2");
            tally = checkWithin(tally, found.refinedSine, 1e-12, "the refined sine");
            tally = checkWithin(tally, found.inversionResidual, 1e-12,
                "the arc-sample-to-curve-parameter inversion residual");
        }

        // ---------- The silent half: a velocity that can never match the tangent ----------
        // e' always has first component 1; this velocity's is 0, so no (s, t) is parallel.
        const crossingMotion = constantVelocityTranslationMotion(vector(0, 0.2, 1));
        const quietAudit = auditEdgeSweepSingularity(crossingMotion, edgePoints, edgeTangents,
                tValues, { "strippedCurve" : edgeCurve });
        println("[EDGE SINGULARITY] crossing velocity: minimum sine " ~ quietAudit.minimumSine ~
            ", candidates " ~ size(quietAudit.candidates) ~ ", ruled out " ~
            size(quietAudit.ruledOut) ~ ", detected " ~ quietAudit.detected ~
            ", degenerate nodes " ~ size(quietAudit.degenerateNodes));
        tally = checkThat(tally, !quietAudit.detected && size(quietAudit.singularities) == 0,
            "the crossing velocity reported " ~ size(quietAudit.singularities) ~ " singularities.");
        tally = checkThat(tally, quietAudit.minimumSine > 0.5,
            "the crossing velocity's minimum sine is " ~ quietAudit.minimumSine ~
            ", closer to parallel than this fixture is meant to be.");
        tally = checkThat(tally, size(quietAudit.ruledOut) == size(quietAudit.candidates),
            "the crossing velocity ruled out " ~ size(quietAudit.ruledOut) ~ " of " ~
            size(quietAudit.candidates) ~ " candidates - a screened candidate went unaccounted for.");
        tally = checkThat(tally, size(quietAudit.degenerateNodes) == 0,
            "the crossing velocity reported " ~ size(quietAudit.degenerateNodes) ~ " degenerate nodes.");

        reportCheckTally(context, id, "EDGE SINGULARITY", tally,
            "detector 3 refines a grid miss of 0.0136 onto the closed-form isolated singularity " ~
            "at (1/2, 1/2), reports nothing from the grid alone, and rules out every candidate " ~
            "under a velocity that can never be parallel to the edge.");
    });

// ===================== Fixtures =====================

/**
 * S = (u, v, u^2 / 2 + (v - 1/2)^3 / 6) as a single Bezier patch at degrees (2, 3). The z net is
 * the sum of the two directions' own Bernstein coefficients, which is exact for a separable
 * polynomial: [0, 0, 1/2] for u^2 / 2 and [-1, 1, -1, 1] / 48 for (v - 1/2)^3 / 6.
 *
 * The cubic in v is what makes the fixture able to produce a near tangency as well as a crossing
 * one: it puts (v - 1/2)^2 into the normal, so f_t comes out with an interior extremum in v
 * rather than monotone.
 */
function tangencyFixtureSurface() returns map
{
    const grevilleU = [0, 0.5, 1];
    const grevilleV = [0, 1 / 3, 2 / 3, 1];
    const uCoefficients = [0, 0, 0.5];
    const vCoefficients = [-1 / 48, 1 / 48, -1 / 48, 1 / 48];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(4);
        for (var j = 0; j < 4; j += 1)
        {
            row[j] = vector(grevilleU[i], grevilleV[j], uCoefficients[i] + vCoefficients[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 3,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 0, 0, 1, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The translation whose velocity at t = 1/2 is (1, 0, 1/2) and whose acceleration there is
 * (0, 1, timeDerivativeOffset). A degree-2 Bernstein velocity has V(1/2) = (q0 + 2q1 + q2) / 4
 * and V'(1/2) = q2 - q0, which is what fixes each triple; the z triple is the only one that
 * moves, so one number carries the fixture through all three verdicts.
 */
function tangencyFixtureMotion(timeDerivativeOffset is number) returns map
{
    return translationMotionFromQuadraticVelocity([1, 1, 1], [-0.5, 0, 0.5],
        [-0.5 * timeDerivativeOffset, 1, 0.5 * timeDerivativeOffset]);
}

/**
 * The edge e(s) = (s, s^2 / 2, c s^3 / 6) as a single cubic Bezier, unit-stripped. Its tangent
 * e'(s) = (1, s, c s^2 / 2) is polynomial in s, which is what lets a degree-2 Bernstein velocity
 * match it exactly at one station and nowhere else.
 */
function singularEdgeCurve(curvature is number) returns map
{
    return {
            "degree" : 3,
            "knots" : [0, 0, 0, 0, 1, 1, 1, 1],
            "isRational" : false,
            "controlPoints" : [vector(0, 0, 0), vector(1 / 3, 0, 0), vector(2 / 3, 1 / 6, 0),
                    vector(1, 0.5, curvature / 6)]
        };
}

/**
 * The translation whose velocity is b'(t) = (1, t, c t^2 / 2 + eps (t - t*)). Against the edge
 * above, matching first components forces s = t and then eps (t - t*) = 0, so the parallelism is
 * an isolated point at (t*, t*) rather than the whole diagonal that eps = 0 would give.
 */
function singularEdgeMotion(curvature is number, epsilon is number, singularT is number) returns map
{
    // Monomials of the z velocity, converted to degree-2 Bernstein by
    // b_k = sum_{i <= k} C(k,i)/C(2,i) m_i.
    const m0 = -epsilon * singularT;
    const m1 = epsilon;
    const m2 = 0.5 * curvature;
    return translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0.5, 1],
        [m0, m0 + 0.5 * m1, m0 + m1 + m2]);
}

/** "a/b/c" of the piece sizes, for one readable println. */
function pieceSizes(pieces is array) returns string
{
    var text = "";
    for (var index = 0; index < size(pieces); index += 1)
    {
        text = text ~ (index == 0 ? "" : "/") ~ size(pieces[index]);
    }
    return text;
}

/** The largest magnitude in an array of numbers. */
function largestOf(values is array) returns number
{
    var largest = 0;
    for (var index = 0; index < size(values); index += 1)
    {
        largest = max(largest, abs(values[index]));
    }
    return largest;
}
