FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0"); // bSplineSurface, controlPointMatrix, KnotArray

// Non-standard imports. swFunnelSolver, swSweepEmit and swOrientation are same-document element
// imports (unpublished on purpose - see swEnvelopeMath.fs); their ids churn on every re-paste, so
// bump these when the owner re-pastes any of them. splineRefinementUtils is the published
// cross-document pin. NOTE for MCP harness runs: same-document imports cannot resolve from the
// harness document; the test payload inlines the needed function bodies in place of these lines.
import(path : "eede4083ca591e1a7adb8440", version : "3968d1ef5b507302198a917b"); //owner combined tab: swFunnelSolver.fs, swEnvelopeMath.fs, swSweepEmit.fs, swOrientation.fs (fix id/version on paste; split into one line per tab if they live separately)
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * SOLID SWEEP - envelope fitting and certification (spec: docs/specs/SOLID_SWEEP_SPEC.md
 * section 7). Pure module: no Context in any library function. Consumes the funnel solver's
 * section layer (marching, resampling, lifting) and produces certified B-spline surface fits
 * of funnel components, parameterized on the (q, t) rectangle - q the fixed arc-length
 * fraction along each section, t the motion parameter.
 *
 * The layers:
 *   - Fitting stations: uniform t stations with event times (island births/deaths, vertex
 *     roots) merged in exactly - snapped onto the nearest station inside a tolerance,
 *     inserted otherwise.
 *   - Anchors: where each station's section march starts and ends. A "fixedUv" anchor is a
 *     constant uv (a vertex); a "branch" anchor interpolates a strip-marching branch's
 *     (t, uv) samples and polishes the result onto f = 0 ALONG the branch polyline, so the
 *     anchor stays on the shared boundary arrays that make seams exact downstream.
 *   - Grid assembly: each station's section is marched anchor to anchor, then resampled at
 *     2q - 1 arc-length fractions in ONE call - the even fractions are the fit grid row, the
 *     odd fractions are fresh q-midpoint samples the fit never sees, held for certification.
 *   - Interpolation: NURBS Book A9.4 (interpolate columns in u, then coefficient rows in v)
 *     composed from splineRefinementUtils' prescribed-parameter curve interpolator. Built
 *     here rather than calling interpolateBSplineSurfaceThroughGrid because a pole-collapsed
 *     row (an island's birth or death, every point identical) has no chord parameterization -
 *     this assembly skips degenerate rows when averaging parameters and interpolates them to
 *     exactly collapsed control rows. Validated against the published function on
 *     nondegenerate grids in the self test.
 *   - Certification: every fresh sample (q midpoints at stations, whole fresh sections at
 *     midpoint stations) is projected onto the fitted surface by damped closest-point Newton
 *     (swSweepEmit's invertPointOnSurface); the worst 3D distance is the certified deviation.
 *     The three sample families attribute deviation by direction: q midpoints at stations
 *     measure the q direction, station midpoints at grid fractions measure t, and their
 *     combination bounds the rest.
 *   - Refinement: the deficient direction's sample count doubles (n to 2n - 1) and the fit
 *     reruns; both counts cap at 60 and a fit still over tolerance there reports
 *     SWEEP_FIT_BUDGET_HIT with its best surface, never a silent failure.
 *   - Cleanup: removeRedundantSurfaceKnots with the slack remaining under tolerance, so the
 *     certified bound (fit deviation + worst removal displacement) still clears it.
 *   - Orientation: every fit runs its (q, t) grid through swOrientation before interpolating,
 *     and reverses q when the net would otherwise face into the swept volume (spec 6.6). The
 *     same pass certifies that lambda keeps one sign over the component, which is what says the
 *     component does not fold; the verdict and that certificate ride out on the result.
 *   - Islands: pole-collapsed patches (probe 4). Stations span birth to death inclusive; the
 *     pole rows are the lifted stationary points repeated; interior stations march the closed
 *     section loop from a seed found on a fixed +u ray out of the (interpolated) island
 *     center, oriented counterclockwise, so the q origin and direction are consistent from
 *     station to station. Clamped interpolation puts the first and last CONTROL rows exactly
 *     at the shared pole data point, so the patch closes to the pole exactly. Knot cleanup is
 *     skipped for island patches: removal perturbs control rows, and an exactly collapsed
 *     pole row beats a sparser net.
 *
 * All geometry here is plain numbers (meters implied). Units are attached only where a
 * consumer demands them: removeRedundantSurfaceKnots inside the cleanup step, and emission
 * (attachFitSurfaceUnits / kernelFitSurface for the live tester and step 9).
 */

// ============================= Envelope Fit Self Test =============================

annotation { "Feature Type Name" : "Sweep Envelope Fit Self Test" }
export const sweepEnvelopeFitSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // ---------- Station builder ----------
        const stations = buildFitStations(0, 1, 5, [0.33, 0.5000001], 0.02);
        var stationsContainEvents = false;
        for (var station in stations)
        {
            if (station == 0.33)
            {
                stationsContainEvents = true;
            }
        }
        var stationsSorted = true;
        for (var index = 1; index < size(stations); index += 1)
        {
            if (stations[index] <= stations[index - 1])
            {
                stationsSorted = false;
            }
        }
        println("[FIT SELF TEST] stations with events: " ~ toString(stations));
        if (size(stations) != 6 || !stationsContainEvents || !stationsSorted ||
            stations[0] != 0 || stations[size(stations) - 1] != 1 ||
            abs(stations[3] - 0.5000001) > 1e-12)
        {
            failures = failures ~ " station builder expected 6 sorted stations with 0.33 inserted and 0.5 snapped to the event.";
        }

        // ---------- Fixture 1: ruled envelope, fixed anchors ----------
        // S = (u, v, 0.15u^2 - 0.18u + 0.05uv) under constant velocity (1, 0, 0.06):
        // f = 0.24 - 0.3u - 0.05v, the section is the fixed line u = 0.8 - v/6, and the
        // envelope is that line's lift translated along (t, 0, 0.06t).
        const slantSurface = slantFixtureSurface();
        const slantMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.06, 0.06], singleSpanKnots);
        const slantFit = fitEnvelopeComponent(slantMotion, slantSurface, {
                    "tStart" : 0, "tEnd" : 1,
                    "startAnchor" : { "anchorKind" : "fixedUv", "uv" : [0.8, 0] },
                    "endAnchor" : { "anchorKind" : "fixedUv", "uv" : [0.8 - 1 / 6, 1] },
                    "tolerance" : 1e-7,
                    "initialQCount" : 6, "initialStationCount" : 6,
                    "maxRefinementRounds" : 3
                });
        if (slantFit.failed)
        {
            failures = failures ~ " ruled fixture fit failed: " ~ slantFit.reason ~ ".";
        }
        else
        {
            println("[FIT SELF TEST] ruled fixture: " ~ slantFit.stationCount ~ "x" ~ slantFit.qCount ~
                " grid, deviation " ~ slantFit.worstDeviation ~ " (q " ~ slantFit.worstQDeviation ~
                ", t " ~ slantFit.worstTDeviation ~ "), removal " ~ slantFit.removalDeviation ~
                ", certified bound " ~ slantFit.certifiedBound ~ ", budgetHit " ~ slantFit.budgetHit);
            println("[FIT SELF TEST] ruled fixture orientation: lambda sign " ~
                slantFit.orientation.lambdaSign ~ " consistent " ~ slantFit.orientation.lambdaSignConsistent ~
                ", faces outward " ~ slantFit.orientation.facesOutward ~ " unanimous " ~
                slantFit.orientation.verdictUnanimous ~ ", q reversed " ~ slantFit.qReversed ~
                ", worst fold margin " ~ slantFit.orientation.worstFoldMargin ~ ", difference " ~
                slantFit.orientation.differenceAgreements ~ "/" ~ slantFit.orientation.differenceChecked ~
                " agree, consistent " ~ slantFit.orientation.consistent ~ ", stationary " ~
                slantFit.orientation.stationarySamples ~ "/" ~ slantFit.orientation.sampleCount);
            if (!slantFit.orientation.consistent)
            {
                failures = failures ~ " ruled fixture orientation certificate came back inconsistent.";
            }
            // This fixture translates at CONSTANT velocity, so A' and b'' vanish and f_t is
            // identically zero: every sample must report a stationary contact set (spec 7.7),
            // and that must not spoil the patch verdict.
            if (!slantFit.orientation.contactStationary)
            {
                failures = failures ~ " a constant-velocity translation did not report a " ~
                    "stationary contact set (" ~ slantFit.orientation.stationarySamples ~ " of " ~
                    slantFit.orientation.sampleCount ~ " samples).";
            }
            if (slantFit.budgetHit || slantFit.certifiedBound > 1e-7)
            {
                failures = failures ~ " ruled fixture missed tolerance (bound " ~ slantFit.certifiedBound ~ ").";
            }
            // Independent analytic membership: a fitted point (x, y, z) must satisfy v = y,
            // u = 0.8 - v/6, t = x - u, z = surface z + 0.06t.
            var worstRuledResidual = 0;
            const ruledDomain = fitSurfaceKnotDomain(slantFit.surface);
            for (var i = 1; i <= 5; i += 1)
            {
                for (var j = 1; j <= 5; j += 1)
                {
                    const uu = ruledDomain.uMin + (ruledDomain.uMax - ruledDomain.uMin) * i / 6;
                    const vv = ruledDomain.vMin + (ruledDomain.vMax - ruledDomain.vMin) * j / 6;
                    const fitted = evaluateBSplineSurfacePoint(slantFit.surface, uu, vv);
                    const toolV = fitted[1];
                    const toolU = 0.8 - toolV / 6;
                    const motionT = fitted[0] - toolU;
                    const exactZ = 0.15 * toolU ^ 2 - 0.18 * toolU + 0.05 * toolU * toolV + 0.06 * motionT;
                    worstRuledResidual = max(worstRuledResidual, abs(fitted[2] - exactZ));
                }
            }
            println("[FIT SELF TEST] ruled fixture analytic membership residual: " ~ worstRuledResidual);
            if (worstRuledResidual > 1e-6)
            {
                failures = failures ~ " ruled fixture analytic residual " ~ worstRuledResidual ~ ".";
            }

            // The same component with its anchors SWAPPED. That reverses the section march, so
            // kappa flips and exactly one of the two runs has to reverse q - which is how the
            // orientation pass and its held-out-row bookkeeping get exercised whichever way the
            // fixture happens to fall. Both must land outward, at the same certified deviation:
            // reversing q moves no geometry.
            const swappedFit = fitEnvelopeComponent(slantMotion, slantSurface, {
                        "tStart" : 0, "tEnd" : 1,
                        "startAnchor" : { "anchorKind" : "fixedUv", "uv" : [0.8 - 1 / 6, 1] },
                        "endAnchor" : { "anchorKind" : "fixedUv", "uv" : [0.8, 0] },
                        "tolerance" : 1e-7,
                        "initialQCount" : 6, "initialStationCount" : 6,
                        "maxRefinementRounds" : 3
                    });
            if (swappedFit.failed)
            {
                failures = failures ~ " anchor-swapped ruled fit failed: " ~ swappedFit.reason ~ ".";
            }
            else
            {
                println("[FIT SELF TEST] anchors swapped: deviation " ~ swappedFit.worstDeviation ~
                    ", certified bound " ~ swappedFit.certifiedBound ~ ", q reversed " ~
                    swappedFit.qReversed ~ " (against " ~ slantFit.qReversed ~ "), faces outward " ~
                    swappedFit.orientation.facesOutward ~ ", consistent " ~
                    swappedFit.orientation.consistent);
                if (!swappedFit.orientation.consistent)
                {
                    failures = failures ~ " anchor-swapped orientation certificate inconsistent.";
                }
                if (swappedFit.qReversed == slantFit.qReversed)
                {
                    failures = failures ~ " swapping the anchors did not flip the q reversal " ~
                        "decision, so the reversal path went untested.";
                }
                if (abs(swappedFit.worstDeviation - slantFit.worstDeviation) > 1e-9)
                {
                    failures = failures ~ " reversing q changed the certified deviation from " ~
                        slantFit.worstDeviation ~ " to " ~ swappedFit.worstDeviation ~
                        " - the held-out rows are mis-aligned against the net's q parameters.";
                }
            }
        }

        // ---------- Fixture 2: curvature in both directions, branch anchors ----------
        // S = (u, v, 0.15u^2 + 0.05uv^2) under velocity (1, 0, w(t)), w = 0.06 + 0.18t - 0.18t^2:
        // f = w(t) - 0.3u - 0.05v^2, sections are moving parabolas u = (w - 0.05v^2)/0.3.
        const curvedSurface = curvedFixtureSurface();
        const curvedMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.15, 0.06], singleSpanKnots);
        const curvedFit = fitEnvelopeComponent(curvedMotion, curvedSurface, {
                    "tStart" : 0, "tEnd" : 1,
                    "startAnchor" : curvedFixtureBranchAnchor(0),
                    "endAnchor" : curvedFixtureBranchAnchor(1),
                    "tolerance" : 1e-6,
                    "maxQCount" : 24, "maxStationCount" : 24, "maxRefinementRounds" : 3
                });
        if (curvedFit.failed)
        {
            failures = failures ~ " curved fixture fit failed: " ~ curvedFit.reason ~ ".";
        }
        else
        {
            println("[FIT SELF TEST] curved fixture: " ~ curvedFit.stationCount ~ "x" ~ curvedFit.qCount ~
                " grid, deviation " ~ curvedFit.worstDeviation ~ " (q " ~ curvedFit.worstQDeviation ~
                ", t " ~ curvedFit.worstTDeviation ~ "), certified bound " ~ curvedFit.certifiedBound ~
                ", budgetHit " ~ curvedFit.budgetHit);
            if (curvedFit.budgetHit || curvedFit.certifiedBound > 1e-6)
            {
                failures = failures ~ " curved fixture missed tolerance (bound " ~ curvedFit.certifiedBound ~ ").";
            }
            const curvedResidual = worstCurvedFixtureResidual(curvedFit.surface, 6);
            println("[FIT SELF TEST] curved fixture analytic membership residual: " ~ curvedResidual);
            if (curvedResidual > 2e-6)
            {
                failures = failures ~ " curved fixture analytic residual " ~ curvedResidual ~ ".";
            }
        }

        reportTestVerdict(context, id, "FIT SELF TEST", failures,
            "station builder, ruled and curved rectangle fits (fixed and branch anchors), " ~
            "direction-resolved certification, and knot cleanup all match their analytic answers.");
    });

// ============================= Envelope Fit Refinement Self Test =============================

// A separate feature (not a section of the main self test) because Onshape's per-regeneration
// interpreter step budget cannot absorb three certified fits in one feature - measured
// 2026-08-22: two fits plus a refinement loop tripped "Too many steps".
annotation { "Feature Type Name" : "Sweep Envelope Fit Refinement Self Test" }
export const sweepEnvelopeFitRefinementSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // The curved fixture from the main self test, started too coarse for the tolerance so
        // the direction-resolved doubling has to run (measured 2026-08-22: 6x6 needs one q
        // doubling and two station doublings to clear 1e-6; the t direction floors at ~2.5e-7
        // under the 24-station cap, so 1e-7 is not reachable inside these caps).
        const curvedSurface = curvedFixtureSurface();
        const curvedMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.15, 0.06], singleSpanKnots);
        const refinedFit = fitEnvelopeComponent(curvedMotion, curvedSurface, {
                    "tStart" : 0, "tEnd" : 1,
                    "startAnchor" : curvedFixtureBranchAnchor(0),
                    "endAnchor" : curvedFixtureBranchAnchor(1),
                    "tolerance" : 1e-6,
                    "initialQCount" : 6, "initialStationCount" : 6,
                    "maxQCount" : 24, "maxStationCount" : 24, "maxRefinementRounds" : 3
                });
        if (refinedFit.failed)
        {
            failures = failures ~ " refinement run failed: " ~ refinedFit.reason ~ ".";
        }
        else
        {
            println("[FIT REFINEMENT SELF TEST] refinement run: " ~ refinedFit.stationCount ~ "x" ~ refinedFit.qCount ~
                " grid after " ~ refinedFit.refinementRounds ~ " round(s), deviation " ~
                refinedFit.worstDeviation ~ " (q " ~ refinedFit.worstQDeviation ~ ", t " ~
                refinedFit.worstTDeviation ~ "), budgetHit " ~ refinedFit.budgetHit);
            if (refinedFit.budgetHit || refinedFit.worstDeviation > 1e-6)
            {
                failures = failures ~ " refinement run missed tolerance (deviation " ~ refinedFit.worstDeviation ~ ").";
            }
            if (refinedFit.qCount <= 6 && refinedFit.stationCount <= 6)
            {
                failures = failures ~ " refinement run never refined from its 6x6 start.";
            }
        }

        reportTestVerdict(context, id, "FIT REFINEMENT SELF TEST", failures,
            "the direction-resolved refinement loop grew a too-coarse start to tolerance.");
    });

// ============================= Envelope Fit Interpolation Self Test =============================

// The pole-capable grid assembly against the published interpolator: separate from the island
// test so the island's closed-loop marching gets the whole per-regeneration step budget.
annotation { "Feature Type Name" : "Sweep Envelope Fit Interpolation Self Test" }
export const sweepEnvelopeFitInterpolationSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";

        // ---------- Interpolation cross-check on a nondegenerate grid ----------
        // The pole-capable assembly must agree with the published surface interpolator
        // wherever both are defined.
        var checkGrid = makeArray(5);
        var checkGridWithUnits = makeArray(5);
        for (var i = 0; i < 5; i += 1)
        {
            var row = makeArray(5);
            var rowWithUnits = makeArray(5);
            for (var j = 0; j < 5; j += 1)
            {
                row[j] = vector(0.1 * i, 0.2 * j, 0.03 * i * i - 0.02 * j * j + 0.015 * i * j);
                rowWithUnits[j] = row[j] * meter;
            }
            checkGrid[i] = row;
            checkGridWithUnits[i] = rowWithUnits;
        }
        const customFit = interpolateFitGrid(checkGrid, 3, 3);
        const libraryFit = interpolateBSplineSurfaceThroughGrid(checkGridWithUnits, 3, 3);
        var worstInterpolationDisagreement = 0;
        for (var i = 0; i < 5; i += 1)
        {
            for (var j = 0; j < 5; j += 1)
            {
                worstInterpolationDisagreement = max(worstInterpolationDisagreement,
                    norm(customFit.controlPoints[i][j] * meter - libraryFit.controlPoints[i][j]) / meter);
            }
        }
        for (var index = 0; index < size(customFit.uKnots); index += 1)
        {
            worstInterpolationDisagreement = max(worstInterpolationDisagreement,
                abs(customFit.uKnots[index] - libraryFit.uKnots[index]));
            worstInterpolationDisagreement = max(worstInterpolationDisagreement,
                abs(customFit.vKnots[index] - libraryFit.vKnots[index]));
        }
        println("[FIT INTERPOLATION SELF TEST] pole-capable vs published interpolation disagreement: " ~
            worstInterpolationDisagreement);
        if (worstInterpolationDisagreement > 1e-12)
        {
            failures = failures ~ " pole-capable interpolation disagrees with the published one by " ~
                worstInterpolationDisagreement ~ ".";
        }

        // ---------- Pole collapse through the interpolation itself ----------
        // A fully collapsed first row must come back as a fully collapsed first CONTROL row:
        // this is the property the island patch's exact pole closure rests on, checked here
        // without paying for a fit.
        var poleGrid = makeArray(5);
        const poleApex = vector(0.2, 0.3, 0.9);
        for (var i = 0; i < 5; i += 1)
        {
            var row = makeArray(5);
            for (var j = 0; j < 5; j += 1)
            {
                row[j] = i == 0 ? poleApex :
                    vector(0.2 + 0.1 * i * cos(360 * degree * j / 4), 0.3 + 0.1 * i * sin(360 * degree * j / 4),
                        0.9 - 0.05 * i);
            }
            poleGrid[i] = row;
        }
        const poleSurface = interpolateFitGrid(poleGrid, 3, 3);
        var worstPoleRowError = 0;
        for (var j = 0; j < 5; j += 1)
        {
            worstPoleRowError = max(worstPoleRowError, norm(poleSurface.controlPoints[0][j] - poleApex));
        }
        const poleDomain = fitSurfaceKnotDomain(poleSurface);
        for (var vFraction in [0, 0.37, 1])
        {
            const vv = poleDomain.vMin + (poleDomain.vMax - poleDomain.vMin) * vFraction;
            worstPoleRowError = max(worstPoleRowError,
                norm(evaluateBSplineSurfacePoint(poleSurface, poleDomain.uMin, vv) - poleApex));
        }
        println("[FIT INTERPOLATION SELF TEST] collapsed-row closure error: " ~ worstPoleRowError);
        if (worstPoleRowError > 1e-14)
        {
            failures = failures ~ " a collapsed data row did not interpolate to a collapsed control row (error " ~
                worstPoleRowError ~ ").";
        }

        // ---------- Periodic row interpolation against an analytic circle ----------
        // A unit circle sampled at 12 equal angles: the periodic interpolant must pass through
        // every sample, stay on the circle BETWEEN samples (a clamped fit would not, at the
        // seam), and cross the seam smoothly - one-sided tangents there must agree.
        const circleSampleCount = 12;
        var circlePoints = makeArray(circleSampleCount);
        var circleParameters = makeArray(circleSampleCount, 0);
        for (var index = 0; index < circleSampleCount; index += 1)
        {
            const angle = 360 * degree * index / circleSampleCount;
            circlePoints[index] = vector(cos(angle), sin(angle), 0);
            circleParameters[index] = index / circleSampleCount;
        }
        const periodicCircle = interpolatePeriodicRow(circlePoints, 3, circleParameters, 1);
        const circleSpline = { "degree" : 3, "isPeriodic" : true, "isRational" : false,
                "controlPoints" : periodicCircle.controlPoints, "knots" : periodicCircle.knots };
        var worstCircleInterpolationError = 0;
        var worstCircleRadiusError = 0;
        for (var index = 0; index < circleSampleCount; index += 1)
        {
            const atSample = evaluateBSplineCurveDerivatives(circleSpline, circleParameters[index], 0)[0];
            worstCircleInterpolationError = max(worstCircleInterpolationError, norm(atSample - circlePoints[index]));
            const between = evaluateBSplineCurveDerivatives(circleSpline,
                    circleParameters[index] + 0.5 / circleSampleCount, 0)[0];
            worstCircleRadiusError = max(worstCircleRadiusError, abs(norm(between) - 1));
        }
        // Seam smoothness, stated exactly: for a genuinely periodic curve the domain end IS the
        // domain start, so position, tangent and second derivative there must agree to machine
        // precision. (Sampling two nearby points either side of the seam instead measures the
        // curve's own curvature across the gap - measured 2026-08-23, a 2e-6 parameter gap on
        // this circle reads 1.3e-5 of perfectly smooth tangent change.) A clamped fit of the
        // same closed data fails this check outright, which is the point.
        const atSeamStart = evaluateBSplineCurveDerivatives(circleSpline, 0, 2);
        const atSeamEnd = evaluateBSplineCurveDerivatives(circleSpline, 1, 2);
        const seamPositionError = norm(atSeamEnd[0] - atSeamStart[0]);
        const seamTangentError = norm(atSeamEnd[1] - atSeamStart[1]) / norm(atSeamStart[1]);
        const seamCurvatureError = norm(atSeamEnd[2] - atSeamStart[2]) / norm(atSeamStart[2]);
        println("[FIT INTERPOLATION SELF TEST] periodic circle: interpolation " ~ worstCircleInterpolationError ~
            ", between-sample radius error " ~ worstCircleRadiusError ~ " (cubic theory h^4/384 = " ~
            ((2 * PI / circleSampleCount) ^ 4 / 384) ~ "), seam position " ~ seamPositionError ~
            ", relative seam tangent " ~ seamTangentError ~ ", relative seam curvature " ~ seamCurvatureError);
        if (worstCircleInterpolationError > 1e-13)
        {
            failures = failures ~ " periodic interpolation missed its own data points by " ~
                worstCircleInterpolationError ~ ".";
        }
        // 12 cubic segments around a unit circle: h^4/384 is about 2e-4, so this bounds the
        // construction, not the discretization.
        if (worstCircleRadiusError > 5e-4)
        {
            failures = failures ~ " periodic circle radius error " ~ worstCircleRadiusError ~ ".";
        }
        if (seamPositionError > 1e-13 || seamTangentError > 1e-12 || seamCurvatureError > 1e-12)
        {
            failures = failures ~ " the periodic seam is not C2 (position " ~ seamPositionError ~
                ", tangent " ~ seamTangentError ~ ", curvature " ~ seamCurvatureError ~ ").";
        }

        reportTestVerdict(context, id, "FIT INTERPOLATION SELF TEST", failures,
            "the pole-capable grid interpolation matches the published interpolator on a " ~
            "nondegenerate grid, collapses a degenerate row exactly, and the periodic row " ~
            "interpolation reproduces a circle smoothly across its seam.");
    });

// ============================= Envelope Fit Island Self Test =============================

annotation { "Feature Type Name" : "Sweep Envelope Fit Island Self Test" }
export const sweepEnvelopeFitIslandSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // ---------- Island fixture: pole-collapsed patch ----------
        // z_u = 0.8u(1-u)v(1-v) peaks at 0.05; wz(t) = 0.134 - 0.4t + 0.4t^2 dips below the
        // peak exactly on t in (0.3, 0.7): birth/death at (0.5, 0.5, 0.3) and (0.5, 0.5, 0.7).
        const islandSurface = islandFixtureSurface();
        const islandMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.134, -0.066, 0.134], singleSpanKnots);
        // One fit at a modest grid: the interpreter's per-regeneration step budget cannot
        // absorb an island refinement loop (measured 2026-08-22 - closed-loop marching plus
        // its fresh-station certification is the most expensive path in the module). 8x8
        // measures 2.2e-4 with the q and t directions balanced, so 5e-4 asserts the
        // construction rather than the discretization.
        const islandFit = fitIslandComponent(islandMotion, islandSurface, {
                    "birth" : { "u" : 0.5, "v" : 0.5, "t" : 0.3 },
                    "death" : { "u" : 0.5, "v" : 0.5, "t" : 0.7 },
                    "tolerance" : 5e-4,
                    "initialQCount" : 8, "initialStationCount" : 8,
                    "maxQCount" : 15, "maxStationCount" : 15, "maxRefinementRounds" : 1
                });
        if (islandFit.failed)
        {
            failures = failures ~ " island fit failed: " ~ islandFit.reason ~ ".";
        }
        else
        {
            println("[FIT ISLAND SELF TEST] island fit: " ~ islandFit.stationCount ~ "x" ~ islandFit.qCount ~
                " grid after " ~ islandFit.refinementRounds ~ " round(s), deviation " ~
                islandFit.worstDeviation ~ " (q " ~ islandFit.worstQDeviation ~ ", t " ~
                islandFit.worstTDeviation ~ "), budgetHit " ~ islandFit.budgetHit);
            if (islandFit.budgetHit || islandFit.worstDeviation > 5e-4)
            {
                failures = failures ~ " island fit missed tolerance (deviation " ~ islandFit.worstDeviation ~ ").";
            }

            // Orientation (spec 6.6). Two things get checked here that no other fit reaches.
            //
            // (1) The collapsed POLE rows must be skipped by the certificate: an island's birth
            //     and death rows are a single repeated point, so they carry no q direction.
            // (2) This fixture's lambda spans BOTH SIGNS. On the loop max |z_uu| is
            //     0.2 sqrt(1 - 20 w_z), so lambda = w_z' + z_uu keeps one sign only where
            //     |w_z'| exceeds that - true just after birth and just before death, FALSE
            //     across t in about (0.39, 0.61). The bump fixture is therefore a LOCALLY
            //     SELF-INTERSECTING sweep through its middle band, which v1 must reject
            //     (spec 3, spec 10 detector 1), and the fold certificate has to say so. The fit
            //     still certifies to tolerance because a deviation check cannot see a fold -
            //     which is exactly why the orientation pass is a gate and not a formality.
            const islandPoleRows = 2;
            const expectedIslandSamples = (islandFit.stationCount - islandPoleRows) * islandFit.qCount;
            println("[FIT ISLAND SELF TEST] orientation: " ~ islandFit.orientation.sampleCount ~
                " samples (pole rows skipped, expected " ~ expectedIslandSamples ~ "), lambda sign " ~
                islandFit.orientation.lambdaSign ~ " consistent " ~
                islandFit.orientation.lambdaSignConsistent ~ ", worst fold margin " ~
                islandFit.orientation.worstFoldMargin ~ ", faces outward " ~
                islandFit.orientation.facesOutward ~ " unanimous " ~
                islandFit.orientation.verdictUnanimous ~ ", q reversed " ~ islandFit.qReversed ~
                ", difference " ~ islandFit.orientation.differenceAgreements ~ "/" ~
                islandFit.orientation.differenceChecked ~ " agree, consistent " ~
                islandFit.orientation.consistent);
            if (islandFit.orientation.sampleCount != expectedIslandSamples)
            {
                failures = failures ~ " the island's collapsed pole rows were not skipped (" ~
                    islandFit.orientation.sampleCount ~ " samples against " ~
                    expectedIslandSamples ~ ").";
            }
            if (islandFit.orientation.lambdaSignConsistent || islandFit.orientation.consistent)
            {
                failures = failures ~ " the fold certificate did NOT fire on the bump fixture, " ~
                    "whose lambda provably spans both signs across its middle band.";
            }

            // Pole closure: the fitted surface's u-start and u-end edges must BE the poles.
            const islandDomain = fitSurfaceKnotDomain(islandFit.surface);
            var worstPoleError = 0;
            for (var vFraction in [0, 0.31, 0.5, 0.77, 1])
            {
                const vv = islandDomain.vMin + (islandDomain.vMax - islandDomain.vMin) * vFraction;
                worstPoleError = max(worstPoleError,
                    norm(evaluateBSplineSurfacePoint(islandFit.surface, islandDomain.uMin, vv) - islandFit.poleStartPoint));
                worstPoleError = max(worstPoleError,
                    norm(evaluateBSplineSurfacePoint(islandFit.surface, islandDomain.uMax, vv) - islandFit.poleEndPoint));
            }
            println("[FIT ISLAND SELF TEST] pole closure error: " ~ worstPoleError);
            if (worstPoleError > 1e-12)
            {
                failures = failures ~ " pole rows did not collapse exactly (error " ~ worstPoleError ~ ").";
            }

            // Independent analytic membership: an envelope point is where the swept family's
            // height function h(u) = z(u, v) + Wz(x - u) - zFitted has a DOUBLE root in u, so
            // min |h| over u must vanish to fit tolerance.
            var worstIslandResidual = 0;
            for (var i = 1; i <= 3; i += 1)
            {
                for (var j = 0; j <= 2; j += 1)
                {
                    const uu = islandDomain.uMin + (islandDomain.uMax - islandDomain.uMin) * i / 4;
                    const vv = islandDomain.vMin + (islandDomain.vMax - islandDomain.vMin) * j / 2;
                    const fitted = evaluateBSplineSurfacePoint(islandFit.surface, uu, vv);
                    worstIslandResidual = max(worstIslandResidual, islandFixtureMembershipResidual(fitted));
                }
            }
            println("[FIT ISLAND SELF TEST] island analytic membership residual: " ~ worstIslandResidual);
            if (worstIslandResidual > 1e-4)
            {
                failures = failures ~ " island analytic residual " ~ worstIslandResidual ~ ".";
            }
        }

        reportTestVerdict(context, id, "FIT ISLAND SELF TEST", failures,
            "the island pole-collapsed patch certifies with exact pole closure and analytic " ~
            "envelope membership.");
    });

// ============================= Envelope Fit Live Test =============================

annotation { "Feature Type Name" : "Sweep Envelope Fit Tube Self Test" }
export const sweepEnvelopeFitTubeSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const barrel = tubeFixtureSurface();
        // Velocity mostly along the barrel axis with a growing sideways component, so the
        // contact loop starts as the flat v = 0.5 iso-curve and tilts as t runs.
        const motion = translationMotionFromQuadraticVelocity([0, 0.12, 0.25], [0, 0, 0], [1, 1, 1],
            [0, 0, 0, 0, 1, 1, 1, 1]);
        const uSeed = 5;
        const tubeOptions = { "vMargin" : 0.05, "sectionTolerance" : 1e-13, "meridianSamples" : 24 };
        const uPeriod = 8;

        // ---------- One station: the loop wraps the seam once and sits on the analytic
        // contact curve ----------
        const probeT = 0.7;
        const probeLoop = tubeLoopSamples(motion, barrel, probeT, uSeed, 12, tubeOptions);
        if (probeLoop.failed)
        {
            failures = failures ~ " tube loop at t = " ~ probeT ~ " failed: " ~ probeLoop.reason ~ ".";
        }
        else
        {
            var worstAnalyticV = 0;
            for (var uv in probeLoop.uvRow)
            {
                worstAnalyticV = max(worstAnalyticV,
                    abs(uv[1] - tubeFixtureExactV(motion, uv[0], probeT)));
            }
            println("[TUBE SELF TEST] loop at t = " ~ probeT ~ ": uTravel " ~ probeLoop.uTravel ~
                " (period " ~ uPeriod ~ "), section residual " ~ probeLoop.worstResidual ~
                ", worst v vs the closed form " ~ worstAnalyticV);
            if (probeLoop.uTravel != uPeriod)
            {
                failures = failures ~ " the loop travelled " ~ probeLoop.uTravel ~ " in u, not one period.";
            }
            if (probeLoop.worstResidual > 1e-11)
            {
                failures = failures ~ " marched section residual " ~ probeLoop.worstResidual ~ ".";
            }
            if (worstAnalyticV > 1e-11)
            {
                failures = failures ~ " marched v is " ~ worstAnalyticV ~ " off the closed-form contact curve.";
            }
        }

        // ---------- The fit ----------
        const tubeFit = fitTubeComponent(motion, barrel, mergeMaps(tubeOptions, {
                        "tStart" : 0, "tEnd" : 1, "uSeed" : uSeed, "tolerance" : TUBE_SELF_TEST_TOLERANCE,
                        "initialQCount" : 14, "initialStationCount" : 8,
                        "maxQCount" : 27, "maxStationCount" : 15, "maxRefinementRounds" : 2
                    }));
        if (tubeFit.failed)
        {
            failures = failures ~ " tube fit failed: " ~ tubeFit.reason ~ ".";
        }
        else
        {
            println("[TUBE SELF TEST] tube fit: " ~ tubeFit.stationCount ~ "x" ~ tubeFit.qCount ~
                " grid in " ~ tubeFit.refinementRounds ~ " round(s), deviation " ~ tubeFit.worstDeviation ~
                " (q " ~ tubeFit.worstQDeviation ~ ", t " ~ tubeFit.worstTDeviation ~
                "), section residual " ~ tubeFit.worstSectionResidual ~ ", budgetHit " ~ tubeFit.budgetHit);
            if (tubeFit.budgetHit || tubeFit.worstDeviation > TUBE_SELF_TEST_TOLERANCE)
            {
                failures = failures ~ " tube fit missed tolerance (deviation " ~ tubeFit.worstDeviation ~ ").";
            }
            if (tubeFit.surface.isVPeriodic != true)
            {
                failures = failures ~ " the tube fit is not periodic in q.";
            }

            // Orientation (spec 6.6). This is the ONLY path whose rows carry u UNWRAPPED past
            // the seam, so it is the only live test of the rule that a q-direction difference
            // must never wrap the sample index: a wrapping difference would be a period-sized
            // jump pointing the wrong way, flipping kappa at exactly one column per station and
            // breaking unanimity. This barrel's lambda is one-signed with a fold margin near
            // 0.95, so unanimity here IS that check. Isolated samples where f_t vanishes (the
            // profile's cy' = 0 points, where the contact curve is tangent to the station) are
            // expected and must count as stationary, not as degeneracy.
            println("[TUBE SELF TEST] orientation: " ~ tubeFit.orientation.sampleCount ~
                " samples, lambda sign " ~ tubeFit.orientation.lambdaSign ~ " consistent " ~
                tubeFit.orientation.lambdaSignConsistent ~ ", worst fold margin " ~
                tubeFit.orientation.worstFoldMargin ~ ", faces outward " ~
                tubeFit.orientation.facesOutward ~ " unanimous " ~
                tubeFit.orientation.verdictUnanimous ~ ", q reversed " ~ tubeFit.qReversed ~
                ", difference " ~ tubeFit.orientation.differenceAgreements ~ "/" ~
                tubeFit.orientation.differenceChecked ~ " agree, stationary " ~
                tubeFit.orientation.stationarySamples ~ ", degenerate " ~
                tubeFit.orientation.degenerateSamples ~ ", consistent " ~
                tubeFit.orientation.consistent);
            if (!tubeFit.orientation.verdictUnanimous)
            {
                failures = failures ~ " the tube's outward verdict was not unanimous - an " ~
                    "unwrapped-u row produced a spurious kappa flip at the seam.";
            }
            if (!tubeFit.orientation.consistent || tubeFit.orientation.lambdaSign != -1)
            {
                failures = failures ~ " the tube orientation certificate did not come back " ~
                    "consistent with one negative lambda sign.";
            }

            // Analytic envelope membership at t values no station and no midpoint station used.
            var worstMembership = 0;
            for (var freshT in [0.31, 0.83])
            {
                for (var index = 0; index < 9; index += 1)
                {
                    const u = 3 + uPeriod * index / 9;
                    const analyticPoint = liftContactPoint(motion, barrel, u,
                        tubeFixtureExactV(motion, u, freshT), freshT);
                    worstMembership = max(worstMembership,
                        invertPointOnSurfaceFromGrid(tubeFit.surface, analyticPoint, 8).residual);
                }
            }
            println("[TUBE SELF TEST] closed-form envelope membership: " ~ worstMembership);
            if (worstMembership > TUBE_SELF_TEST_TOLERANCE)
            {
                failures = failures ~ " closed-form envelope membership " ~ worstMembership ~ ".";
            }

            // The q seam: the domain END and the domain START are the SAME point of a periodic
            // direction, so they must agree in position, tangent, and curvature. Sampling two
            // nearby parameters either side instead would measure the patch's own curvature
            // across the gap and read as a false kink (spec 7.4).
            const fitDomain = fitSurfaceKnotDomain(tubeFit.surface);
            const seamU = 0.5 * (fitDomain.uMin + fitDomain.uMax);
            const atStart = evaluateBSplineSurfaceDerivatives(tubeFit.surface, seamU, fitDomain.vMin, 0, 2);
            const atEnd = evaluateBSplineSurfaceDerivatives(tubeFit.surface, seamU, fitDomain.vMax, 0, 2);
            const seamPosition = norm(atEnd[0][0] - atStart[0][0]);
            const seamTangent = norm(atEnd[0][1] - atStart[0][1]) / max(1e-300, norm(atStart[0][1]));
            const seamCurvature = norm(atEnd[0][2] - atStart[0][2]) / max(1e-300, norm(atStart[0][2]));
            println("[TUBE SELF TEST] q seam: position " ~ seamPosition ~ ", relative tangent " ~
                seamTangent ~ ", relative curvature " ~ seamCurvature);
            if (seamPosition > 1e-12 || seamTangent > 1e-10 || seamCurvature > 1e-9)
            {
                failures = failures ~ " the q seam is not C2 (position " ~ seamPosition ~ ").";
            }
        }

        reportTestVerdict(context, id, "TUBE SELF TEST", failures,
            "the wrapping section march closes on the closed-form contact curve after exactly " ~
            "one period of u, and the tube fit certifies with a C2 periodic q seam.");
    });

/**
 * ROTATING TUBE SELF TEST (spec 7.9, section 12.1's rotation-through-a-fitted-patch gap).
 *
 * Every step-7 measurement before this one was a pure translation, which is also the one case
 * where the envelope degenerates to a profile sweep (spec 7.7) and where lambda is constant by
 * construction. This is the first fitted patch whose motion actually rotates, so it is the first
 * test in which the contact set MOVES across the tool and the fold certificate has a varying
 * lambda to certify.
 *
 * Everything it asserts is closed form - see rotatingTubeMotion's comment for the derivation and
 * for why rotation about the barrel's own axis is non-degenerate only because the profile is an
 * ellipse. The one number that is not closed form is the stored rotation's rigidity drift, which
 * no polynomial rotation can avoid; it is measured and bounded rather than assumed away.
 */
annotation { "Feature Type Name" : "Sweep Rotating Tube Self Test" }
export const sweepRotatingTubeSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const barrel = tubeFixtureSurface();
        const motion = rotatingTubeMotion();
        const straight = constantVelocityTranslationMotion(
            vector(ROTATING_TUBE_CROSS_SPEED, 0, ROTATING_TUBE_AXIAL_SPEED));
        const uSeed = 5;
        const uPeriod = TUBE_FIXTURE_PROFILE_COUNT;
        const tubeOptions = { "vMargin" : 0.05, "sectionTolerance" : 1e-13, "meridianSamples" : 24 };

        // ---------- The stored rotation: how rigid, and is M's third row really zero ----------
        var worstDrift = 0;
        var worstThirdRow = 0;
        var worstTurn = 0;
        for (var index = 0; index <= 40; index += 1)
        {
            const t = index / 40;
            const pullback = rotatingTubePullback(motion, t);
            worstDrift = max(worstDrift, orthonormalityDefect(pullback.columns));
            for (var entry in pullback.thirdRow)
            {
                worstThirdRow = max(worstThirdRow, abs(entry));
            }
            worstTurn = max(worstTurn,
                abs(pullback.columns[0][0] - cos(rotatingTubeTurn(t) * radian)));
        }
        println("[ROTATING TUBE SELF TEST] stored rotation: drift " ~ worstDrift ~
            ", worst |cos - stored| " ~ worstTurn ~ ", worst |M third row| " ~ worstThirdRow);
        tally = checkWithin(tally, worstDrift, ROTATING_TUBE_DRIFT_LIMIT,
            "the stored rotation's orthonormality drift");
        // Zero by construction - the z column is constant, so zhat . x' = zhat . y' = 0 - but the
        // column arrives through a derivative spline built from differences, so what survives is
        // rounding (3.6e-15 measured), not structure. A nonzero entry HERE would make f cubic in
        // v and void the closed form, so the bound has to be tight enough to catch that.
        tally = checkWithin(tally, worstThirdRow, 1e-13,
            "M's third row - a nonzero entry there makes f cubic in v and voids the closed form");

        // ---------- The closed form is a root of the shipped f ----------
        var worstClosedForm = 0;
        for (var timeIndex = 0; timeIndex <= 8; timeIndex += 1)
        {
            const t = timeIndex / 8;
            for (var uIndex = 0; uIndex < 24; uIndex += 1)
            {
                const u = TUBE_FIXTURE_DOMAIN_START + uPeriod * uIndex / 24;
                worstClosedForm = max(worstClosedForm,
                    abs(evaluateEnvelopePointwise(motion, barrel, u, rotatingTubeExactV(motion, u, t), t)));
            }
        }
        println("[ROTATING TUBE SELF TEST] |f| at the closed-form contact v: " ~ worstClosedForm);
        tally = checkWithin(tally, worstClosedForm, 1e-15, "|f| at the closed-form contact v");

        // ---------- The contact set MOVES, and it is the rotation that moves it ----------
        var worstTravel = 0;
        var worstRotationGap = 0;
        var worstBand = 0;
        for (var uIndex = 0; uIndex < 24; uIndex += 1)
        {
            const u = TUBE_FIXTURE_DOMAIN_START + uPeriod * uIndex / 24;
            const atStart = rotatingTubeExactV(motion, u, 0);
            for (var timeIndex = 0; timeIndex <= 8; timeIndex += 1)
            {
                const t = timeIndex / 8;
                const moving = rotatingTubeExactV(motion, u, t);
                worstTravel = max(worstTravel, abs(moving - atStart));
                worstBand = max(worstBand, abs(moving - 0.5));
                worstRotationGap = max(worstRotationGap,
                    abs(moving - rotatingTubeExactV(straight, u, t)));
            }
        }
        println("[ROTATING TUBE SELF TEST] contact set: travel " ~ worstTravel ~
            ", worst |v - 0.5| " ~ worstBand ~ ", gap against the same motion with A = I " ~
            worstRotationGap);
        tally = checkThat(tally, worstTravel > 0.1,
            "the contact set travelled only " ~ worstTravel ~ " - a one-parameter subgroup " ~
            "would give a t-independent contact set, which is what this fixture exists to avoid.");
        tally = checkThat(tally, worstRotationGap > 0.1,
            "the rotation moved the contact curve by only " ~ worstRotationGap ~ ", so this is " ~
            "not meaningfully a rotation test.");
        tally = checkThat(tally, worstBand < 0.45,
            "the contact curve reached |v - 0.5| = " ~ worstBand ~ ", off the barrel.");

        // ---------- One marched station lands on the closed-form curve ----------
        const probeT = 0.7;
        const probeLoop = tubeLoopSamples(motion, barrel, probeT, uSeed, 12, tubeOptions);
        tally = checkThat(tally, !probeLoop.failed,
            "the rotating tube loop at t = " ~ probeT ~ " failed: " ~ probeLoop.reason ~ ".");
        if (!probeLoop.failed)
        {
            var worstMarched = 0;
            for (var uv in probeLoop.uvRow)
            {
                worstMarched = max(worstMarched, abs(uv[1] - rotatingTubeExactV(motion, uv[0], probeT)));
            }
            println("[ROTATING TUBE SELF TEST] marched loop at t = " ~ probeT ~ ": uTravel " ~
                probeLoop.uTravel ~ " (period " ~ uPeriod ~ "), section residual " ~
                probeLoop.worstResidual ~ ", worst v vs the closed form " ~ worstMarched);
            tally = checkThat(tally, probeLoop.uTravel == uPeriod,
                "the loop travelled " ~ probeLoop.uTravel ~ " in u, not one period.");
            tally = checkWithin(tally, probeLoop.worstResidual, 1e-11, "the marched section residual");
            // The marcher stops once |f| <= sectionTolerance, and a value residual converts to a
            // v error of sectionTolerance / |f_v|. This fixture's contact function is FLAT in v -
            // the quadratic coefficient K is small - so |f_v| runs about 5e-4 and the floor sits
            // near 2e-10, two orders looser than the straight-translation barrel where a steeper
            // f_v hides the same residual. Tightening sectionTolerance would buy nothing: the fit
            // is q-limited at 3e-5, ten orders above this.
            tally = checkWithin(tally, worstMarched, 1e-9,
                "the marched v against the closed-form contact curve");
        }

        // ---------- The fit, and the fold certificate on a varying lambda ----------
        const tubeFit = fitTubeComponent(motion, barrel, mergeMaps(tubeOptions, {
                        "tStart" : 0, "tEnd" : 1, "uSeed" : uSeed, "tolerance" : ROTATING_TUBE_TOLERANCE,
                        "initialQCount" : 14, "initialStationCount" : 8,
                        "maxQCount" : 55, "maxStationCount" : 29, "maxRefinementRounds" : 3
                    }));
        tally = checkThat(tally, !tubeFit.failed, "the rotating tube fit failed: " ~ tubeFit.reason ~ ".");
        if (!tubeFit.failed)
        {
            println("[ROTATING TUBE SELF TEST] fit: " ~ tubeFit.stationCount ~ "x" ~ tubeFit.qCount ~
                " grid in " ~ tubeFit.refinementRounds ~ " round(s), deviation " ~
                tubeFit.worstDeviation ~ " (q " ~ tubeFit.worstQDeviation ~ ", t " ~
                tubeFit.worstTDeviation ~ "), section residual " ~ tubeFit.worstSectionResidual ~
                ", budgetHit " ~ tubeFit.budgetHit);
            tally = checkThat(tally, !tubeFit.budgetHit && tubeFit.worstDeviation <= ROTATING_TUBE_TOLERANCE,
                "the rotating tube fit missed tolerance (deviation " ~ tubeFit.worstDeviation ~ ").");
            tally = checkThat(tally, tubeFit.surface.isVPeriodic == true,
                "the rotating tube fit is not periodic in q.");

            // This is the point of the whole fixture: unlike every earlier step-7 measurement,
            // lambda here is not constant by construction, so one-signedness is a real result
            // rather than an identity.
            println("[ROTATING TUBE SELF TEST] orientation: " ~ tubeFit.orientation.sampleCount ~
                " samples, lambda sign " ~ tubeFit.orientation.lambdaSign ~ " consistent " ~
                tubeFit.orientation.lambdaSignConsistent ~ ", worst fold margin " ~
                tubeFit.orientation.worstFoldMargin ~ ", faces outward " ~
                tubeFit.orientation.facesOutward ~ " unanimous " ~
                tubeFit.orientation.verdictUnanimous ~ ", difference " ~
                tubeFit.orientation.differenceAgreements ~ "/" ~
                tubeFit.orientation.differenceChecked ~ " agree, stationary " ~
                tubeFit.orientation.stationarySamples ~ ", degenerate " ~
                tubeFit.orientation.degenerateSamples ~ ", consistent " ~
                tubeFit.orientation.consistent);
            tally = checkThat(tally, tubeFit.orientation.verdictUnanimous,
                "the rotating tube's outward verdict was not unanimous.");
            tally = checkThat(tally, tubeFit.orientation.lambdaSignConsistent,
                "lambda changed sign across the rotating component, so the fold certificate " ~
                "fired - the fixture is meant to keep it one-signed at a fold margin near 0.73.");
            tally = checkThat(tally, tubeFit.orientation.consistent,
                "the rotating tube orientation certificate did not come back consistent.");

            // Closed-form membership at t values no station and no midpoint station used.
            var worstMembership = 0;
            for (var freshT in [0.31, 0.83])
            {
                for (var index = 0; index < 9; index += 1)
                {
                    const u = TUBE_FIXTURE_DOMAIN_START + uPeriod * index / 9;
                    const analyticPoint = liftContactPoint(motion, barrel, u,
                        rotatingTubeExactV(motion, u, freshT), freshT);
                    worstMembership = max(worstMembership,
                        invertPointOnSurfaceFromGrid(tubeFit.surface, analyticPoint, 8).residual);
                }
            }
            println("[ROTATING TUBE SELF TEST] closed-form envelope membership: " ~ worstMembership);
            tally = checkWithin(tally, worstMembership, ROTATING_TUBE_TOLERANCE,
                "closed-form envelope membership");
        }

        reportCheckTally(context, id, "ROTATING TUBE SELF TEST", tally,
            "a genuinely rotating motion carries the contact set across the barrel, the quadratic " ~
            "closed form tracks it, and the fitted periodic patch certifies with a one-signed " ~
            "lambda - the first fitted patch in the project whose motion is not a pure translation.");
    });

annotation { "Feature Type Name" : "Sweep Tube Ellipsoid Live Test" }
export const sweepTubeEllipsoidLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        // Half an ellipse revolved about the global X axis: one smooth face, a seam, and a
        // degenerate pole at each end - the first REAL tool the tube path sees, and the shape
        // the whole of step 7 is aimed at.
        const semiAxial = 0.05;
        const semiRadial = 0.03;
        const sketchId = id + "ellipsoidSketch";
        const profileSketch = newSketchOnPlane(context, sketchId, {
                    "sketchPlane" : plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0))
                });
        skEllipse(profileSketch, "profile", {
                    "center" : vector(0, 0) * meter,
                    "majorRadius" : semiAxial * meter,
                    "minorRadius" : semiRadial * meter
                });
        skLineSegment(profileSketch, "axisCut", {
                    "start" : vector(-2 * semiAxial, 0) * meter,
                    "end" : vector(2 * semiAxial, 0) * meter
                });
        skSolve(profileSketch);
        opRevolve(context, id + "ellipsoid", {
                    "entities" : qNthElement(qSketchRegion(sketchId), 0),
                    "axis" : line(vector(0, 0, 0) * meter, vector(1, 0, 0)),
                    "angleForward" : 360 * degree
                });
        opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

        const records = extractToolFaceRecords(context, qCreatedBy(id + "ellipsoid", EntityType.BODY), 1e-7);
        println("[ELLIPSOID LIVE TEST] " ~ summarizeFaceRecords(records));
        if (size(records) != 1 || records[0].spline == undefined)
        {
            failures = failures ~ " the ellipsoid did not extract as one spline-bearing face.";
        }
        else
        {
            const record = records[0];
            println("[ELLIPSOID LIVE TEST] extracted spline: " ~ describeSurfaceShape(record.spline));
            // A surface of revolution is periodic in exactly one direction and collapsed at
            // both ends of the other. Extraction reports both; nothing here assumes which
            // direction the kernel chose.
            const uPeriodic = record.spline.isUPeriodic == true;
            const vPeriodic = record.spline.isVPeriodic == true;
            const poles = record.degenerate;
            const polesAcrossV = poles.vStart && poles.vEnd;
            const polesAcrossU = poles.uStart && poles.uEnd;
            if (uPeriodic == vPeriodic)
            {
                failures = failures ~ " the ellipsoid is periodic in " ~ (uPeriodic ? "both" : "neither") ~
                    " direction, expected exactly one.";
            }
            if (!(uPeriodic && polesAcrossV) && !(vPeriodic && polesAcrossU))
            {
                failures = failures ~ " the poles were not found at both ends of the non-periodic direction.";
            }

            // Everything below wants u circumferential, which is how the tube marcher is
            // written; transposing costs nothing and makes the test independent of the
            // kernel's choice.
            const toolSurface = vPeriodic ? transposeSurface(record.spline) : record.spline;
            const domain = surfaceKnotDomain(toolSurface);
            const uPeriod = domain.uEnd - domain.uStart;
            const vSpan = domain.vEnd - domain.vStart;

            // Velocity mostly along the ellipsoid's own axis, tilting sideways as t runs. The
            // contact set of an ellipsoid under a translation is exactly its intersection with
            // the CENTRAL PLANE <grad F, w> = 0, so the marched loop has a closed-form test
            // that owes nothing to this module.
            const motion = translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0.06, 0.12], [0, 0, 0],
                [0, 0, 0, 0, 1, 1, 1, 1]);
            const probeT = 0.6;
            const loop = tubeLoopSamples(motion, toolSurface, probeT, domain.uStart + 0.5 * uPeriod, 10, {
                        "vMargin" : 0.1 * vSpan, "sectionTolerance" : 1e-13, "meridianSamples" : 32
                    });
            if (loop.failed)
            {
                failures = failures ~ " the ellipsoid tube loop failed: " ~ loop.reason ~ ".";
            }
            else
            {
                const velocity = evaluateMotionSample(motion, probeT).translationDerivative;
                const velocityDirection = velocity / norm(velocity);
                var worstRadial = 0;
                var worstPlaneSine = 0;
                for (var uv in loop.uvRow)
                {
                    const wrapped = domain.uStart + positiveModulo(uv[0] - domain.uStart, uPeriod);
                    const point = evaluateBSplineSurfacePoint(toolSurface, wrapped, uv[1]);
                    const scaled = vector(point[0] / semiAxial ^ 2, point[1] / semiRadial ^ 2,
                        point[2] / semiRadial ^ 2);
                    worstRadial = max(worstRadial, abs(sqrt(point[0] ^ 2 / semiAxial ^ 2 +
                                    (point[1] ^ 2 + point[2] ^ 2) / semiRadial ^ 2) - 1) * semiRadial);
                    worstPlaneSine = max(worstPlaneSine, abs(dot(scaled / norm(scaled), velocityDirection)));
                }
                println("[ELLIPSOID LIVE TEST] tube loop at t = " ~ probeT ~ ": uTravel " ~ loop.uTravel ~
                    " (period " ~ uPeriod ~ "), section residual " ~ loop.worstResidual ~
                    ", off the ellipsoid " ~ worstRadial ~ " m, off the analytic contact plane " ~
                    worstPlaneSine ~ " (sine)");
                if (loop.uTravel != uPeriod)
                {
                    failures = failures ~ " the ellipsoid loop travelled " ~ loop.uTravel ~ ", not one period.";
                }
                if (worstRadial > 1e-5)
                {
                    failures = failures ~ " marched points sit " ~ worstRadial ~ " m off the ellipsoid.";
                }
                if (worstPlaneSine > 1e-3)
                {
                    failures = failures ~ " marched normals are " ~ worstPlaneSine ~ " off perpendicular to the velocity.";
                }
            }
        }

        reportTestVerdict(context, id, "ELLIPSOID LIVE TEST", failures,
            "a revolved ellipsoid extracts non-rational with both poles found, and its " ~
            "wrapping contact loop lands on the analytic central-plane section.");
    });

annotation { "Feature Type Name" : "Sweep Tube Patch Live Test" }
export const sweepTubePatchLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";

        // ---------- Regression: the periodic freeform wall that forceNonRational broke ----------
        // An elliptical extrude's wall is the step-4 fixture whose forced non-rational
        // approximation comes back in a periodic spelling normalizeSurfaceDefinition refuses to
        // guess at. Extraction now asks for the rational form, which converts exactly.
        const wallSketchId = id + "wallSketch";
        const wallSketch = newSketchOnPlane(context, wallSketchId, {
                    "sketchPlane" : plane(vector(0.25, 0, 0) * meter, vector(0, 0, 1))
                });
        skEllipse(wallSketch, "ellipse1", {
                    "center" : vector(0, 0) * meter,
                    "majorRadius" : 0.03 * meter,
                    "minorRadius" : 0.015 * meter
                });
        skSolve(wallSketch);
        opExtrude(context, id + "wallExtrude", {
                    "entities" : qSketchRegion(wallSketchId),
                    "direction" : vector(0, 0, 1),
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : 0.05 * meter
                });
        opDeleteBodies(context, id + "deleteWallSketch", {
                    "entities" : qCreatedBy(wallSketchId, EntityType.BODY)
                });
        const wallRecords = extractToolFaceRecords(context,
            qCreatedBy(id + "wallExtrude", EntityType.BODY), 1e-6);
        println("[TUBE PATCH LIVE TEST] elliptical extrude: " ~ summarizeFaceRecords(wallRecords));
        var wallSpline = undefined;
        for (var record in wallRecords)
        {
            if (record.spline != undefined)
            {
                wallSpline = record.spline;
            }
        }
        if (wallSpline == undefined)
        {
            failures = failures ~ " the elliptical extrude wall produced no spline.";
        }
        else
        {
            println("[TUBE PATCH LIVE TEST] wall spline: " ~ describeSurfaceShape(wallSpline) ~
                ", control net " ~ size(wallSpline.controlPoints) ~ "x" ~ size(wallSpline.controlPoints[0]));
            const wallInversion = invertPointOnSurfaceFromGrid(wallSpline,
                evaluateBSplineSurfacePoint(wallSpline, 0.31, 0.42), 6);
            println("[TUBE PATCH LIVE TEST] wall inversion round trip: " ~ wallInversion.residual ~ " m");
            if (wallInversion.residual > 1e-9)
            {
                failures = failures ~ " wall inversion residual " ~ wallInversion.residual ~ " m.";
            }
        }

        // ---------- The ellipsoid's lateral envelope ----------
        const tool = ellipsoidToolFixture(context, id + "tool", ELLIPSOID_SEMI_AXIAL, ELLIPSOID_SEMI_RADIAL, 1e-6);
        println("[TUBE PATCH LIVE TEST] tool: " ~ describeSurfaceShape(tool.surface) ~
            ", control net " ~ size(tool.surface.controlPoints) ~ "x" ~ size(tool.surface.controlPoints[0]));

        // A STRAIGHT translation, deliberately off the ellipsoid's own axis. Straight is the
        // case whose swept volume is exact - a Minkowski sum with a segment - which is the
        // check the whole assembly gets measured against once the caps land.
        const direction = normalize(vector(1, 0.35, 0));
        const travel = 0.18;
        const displacement = travel * direction;
        const motion = translationMotionFromQuadraticVelocity(
            [displacement[0], displacement[0], displacement[0]],
            [displacement[1], displacement[1], displacement[1]],
            [displacement[2], displacement[2], displacement[2]],
            [0, 0, 0, 0, 1, 1, 1, 1]);

        const fit = fitTubeComponent(motion, tool.surface, {
                    "tStart" : 0, "tEnd" : 1,
                    "uSeed" : tool.domain.uMin + 0.5 * (tool.domain.uMax - tool.domain.uMin),
                    "tolerance" : 1e-5,
                    "vMargin" : 0.08 * (tool.domain.vMax - tool.domain.vMin),
                    "sectionTolerance" : 1e-13, "meridianSamples" : 32,
                    "initialQCount" : 16, "initialStationCount" : 5,
                    "maxQCount" : 31, "maxStationCount" : 9, "maxRefinementRounds" : 2
                });
        if (fit.failed)
        {
            failures = failures ~ " the ellipsoid tube fit failed: " ~ fit.reason ~ ".";
        }
        else
        {
            println("[TUBE PATCH LIVE TEST] fit: " ~ fit.stationCount ~ "x" ~ fit.qCount ~ " grid in " ~
                fit.refinementRounds ~ " round(s), deviation " ~ fit.worstDeviation ~ " (q " ~
                fit.worstQDeviation ~ ", t " ~ fit.worstTDeviation ~ "), budgetHit " ~ fit.budgetHit);
            if (fit.budgetHit || fit.worstDeviation > 1e-5)
            {
                failures = failures ~ " the tube fit missed 1e-5 (deviation " ~ fit.worstDeviation ~ ").";
            }

            // The unproven kernel question: a v-PERIODIC net is cylinder topology, not the
            // whole-island shape section 7.4 found rejected. Emit it and see.
            opCreateBSplineSurface(context, id + "tubePatch", {
                        "bSplineSurface" : kernelFitSurface(attachFitSurfaceUnits(fit.surface), true)
                    });
            const patchFaces = evaluateQuery(context, qCreatedBy(id + "tubePatch", EntityType.FACE));
            println("[TUBE PATCH LIVE TEST] emitted " ~ size(patchFaces) ~ " face(s) from a " ~
                size(fit.surface.controlPoints) ~ "x" ~ size(fit.surface.controlPoints[0]) ~
                " periodic net");
            if (size(patchFaces) != 1)
            {
                failures = failures ~ " the periodic tube net emitted " ~ size(patchFaces) ~ " faces.";
            }

            // Kernel certification against fresh envelope points at stations the fit never used.
            var freshPoints = [];
            for (var freshT in [0.27, 0.63])
            {
                const freshLoop = tubeLoopSamples(motion, tool.surface, freshT,
                    tool.domain.uMin + 0.5 * (tool.domain.uMax - tool.domain.uMin), 8, {
                            "vMargin" : 0.08 * (tool.domain.vMax - tool.domain.vMin),
                            "sectionTolerance" : 1e-13, "meridianSamples" : 32
                        });
                if (freshLoop.failed)
                {
                    failures = failures ~ " fresh loop at t = " ~ freshT ~ " failed.";
                    continue;
                }
                for (var point in freshLoop.liftedRow)
                {
                    freshPoints = append(freshPoints, point * meter);
                }
            }
            if (size(freshPoints) > 0)
            {
                const patchDeviation = evPointsDeviation(context, {
                                "points" : freshPoints,
                                "topologies" : qCreatedBy(id + "tubePatch", EntityType.FACE)
                            })[0].deviation;
                println("[TUBE PATCH LIVE TEST] kernel deviation vs fresh envelope points: " ~ patchDeviation);
                if (patchDeviation > 1e-4 * meter)
                {
                    failures = failures ~ " emitted patch deviation " ~ (patchDeviation / meter) ~ " m.";
                }
            }
        }

        reportTestVerdict(context, id, "TUBE PATCH LIVE TEST", failures,
            "the ellipsoid's lateral envelope fits, emits as ONE periodic B-spline face, and " ~
            "kernel-certifies against fresh envelope points.");
    });

/** The ellipsoid the step-7 live tests sweep: semi-axes in meters, axis along global X. */
const ELLIPSOID_SEMI_AXIAL = 0.05;
const ELLIPSOID_SEMI_RADIAL = 0.03;

/**
 * Build the step-7 tool - half an ellipse revolved about the global X axis - and extract it
 * ready for the tube path: one smooth face, non-rational, with the CIRCUMFERENTIAL direction
 * transposed into u whichever way the kernel chose to parameterize the revolve (spec 7.6).
 * Returns { body {Query}, record, surface, domain }.
 */
function ellipsoidToolFixture(context is Context, id is Id, semiAxial is number, semiRadial is number,
    extractTolerance is number) returns map
{
    const sketchId = id + "sketch";
    const profileSketch = newSketchOnPlane(context, sketchId, {
                "sketchPlane" : plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0))
            });
    skEllipse(profileSketch, "profile", {
                "center" : vector(0, 0) * meter,
                "majorRadius" : semiAxial * meter,
                "minorRadius" : semiRadial * meter
            });
    skLineSegment(profileSketch, "axisCut", {
                "start" : vector(-2 * semiAxial, 0) * meter,
                "end" : vector(2 * semiAxial, 0) * meter
            });
    skSolve(profileSketch);
    opRevolve(context, id + "revolve", {
                "entities" : qNthElement(qSketchRegion(sketchId), 0),
                "axis" : line(vector(0, 0, 0) * meter, vector(1, 0, 0)),
                "angleForward" : 360 * degree
            });
    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

    const body = qCreatedBy(id + "revolve", EntityType.BODY);
    const records = extractToolFaceRecords(context, body, extractTolerance);
    if (size(records) != 1 || records[0].spline == undefined)
    {
        throw "the ellipsoid tool did not extract as one spline-bearing face.";
    }
    const record = records[0];
    // transposeSurface normalizes on the way in, which re-attaches a unit weight grid and
    // re-flags the net rational; dropping them again keeps the coefficient path's guarantee.
    const surface = record.spline.isVPeriodic == true ?
        dropUniformWeights(transposeSurface(record.spline)) : record.spline;
    const knotDomain = surfaceKnotDomain(surface);
    return {
            "body" : body,
            "record" : record,
            "surface" : surface,
            "domain" : { "uMin" : knotDomain.uStart, "uMax" : knotDomain.uEnd,
                "vMin" : knotDomain.vStart, "vMax" : knotDomain.vEnd }
        };
}

annotation { "Feature Type Name" : "Sweep Envelope Fit Live Test" }
export const sweepEnvelopeFitLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // ---------- Rectangle patch: fit, emit, kernel-certify ----------
        const curvedSurface = curvedFixtureSurface();
        const curvedMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.15, 0.06], singleSpanKnots);
        // Modest grids on both patches: this feature pays for two fits plus kernel work, and
        // the point here is that the emitted geometry matches the envelope, which the
        // self tests already measured at finer grids.
        const curvedFit = fitEnvelopeComponent(curvedMotion, curvedSurface, {
                    "tStart" : 0, "tEnd" : 1,
                    "startAnchor" : curvedFixtureBranchAnchor(0),
                    "endAnchor" : curvedFixtureBranchAnchor(1),
                    "tolerance" : 1e-4,
                    "initialQCount" : 8, "initialStationCount" : 8,
                    "maxQCount" : 15, "maxStationCount" : 15, "maxRefinementRounds" : 1
                });
        if (curvedFit.failed || curvedFit.budgetHit)
        {
            failures = failures ~ " rectangle fit did not certify for emission.";
        }
        else
        {
            opCreateBSplineSurface(context, id + "rectPatch", {
                        "bSplineSurface" : kernelFitSurface(attachFitSurfaceUnits(curvedFit.surface))
                    });
            // Fresh envelope points at t stations the fit never used, kernel-projected.
            var rectPoints = [];
            for (var tFresh in [0.23, 0.61])
            {
                const row = sectionSamplesAtStation(curvedMotion, curvedSurface, tFresh,
                    curvedFixtureBranchAnchor(0), curvedFixtureBranchAnchor(1), 5, {});
                if (row.failed)
                {
                    failures = failures ~ " fresh rectangle section at t = " ~ tFresh ~ " failed.";
                    continue;
                }
                for (var point in row.liftedRow)
                {
                    rectPoints = append(rectPoints, point * meter);
                }
            }
            const rectDeviation = evPointsDeviation(context, {
                            "points" : rectPoints,
                            "topologies" : qCreatedBy(id + "rectPatch", EntityType.FACE)
                        })[0].deviation;
            println("[FIT LIVE TEST] rectangle patch kernel deviation vs fresh envelope points: " ~ rectDeviation);
            if (rectDeviation > 1e-4 * meter)
            {
                failures = failures ~ " rectangle patch kernel deviation " ~ (rectDeviation / meter) ~ ".";
            }
        }

        // ---------- Island patch: fit, emit pole-collapsed, kernel-certify ----------
        // Islands are NOT emitted here: a whole-island net is rejected by the kernel (see
        // kernelFitSurface). Their fit is covered by the island self test, and their emission
        // shape is the spec section 7.1 fallback, settled with the caps work.

        reportTestVerdict(context, id, "FIT LIVE TEST", failures,
            "the rectangle envelope patch emitted through opCreateBSplineSurface and " ~
            "kernel-certified against fresh envelope samples.");
    });

// ============================= Fitting stations =============================

/**
 * Uniform stations over [tStart, tEnd] with event times merged in EXACTLY: an event inside
 * mergeTolerance of an interior station replaces it; anywhere else in the open interval it is
 * inserted. Endpoints are never displaced. Returns the sorted station array (size >= the
 * requested count).
 */
export function buildFitStations(tStart is number, tEnd is number, stationCount is number,
    eventTimes is array, mergeTolerance is number) returns array
{
    var stations = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        stations[index] = tStart + (tEnd - tStart) * index / (stationCount - 1);
    }
    for (var eventTime in eventTimes)
    {
        if (eventTime <= tStart + mergeTolerance || eventTime >= tEnd - mergeTolerance)
        {
            continue;
        }
        var nearestIndex = 0;
        for (var index = 1; index < size(stations); index += 1)
        {
            if (abs(stations[index] - eventTime) < abs(stations[nearestIndex] - eventTime))
            {
                nearestIndex = index;
            }
        }
        if (abs(stations[nearestIndex] - eventTime) <= mergeTolerance &&
            nearestIndex != 0 && nearestIndex != size(stations) - 1)
        {
            stations[nearestIndex] = eventTime;
        }
        else
        {
            stations = append(stations, eventTime);
        }
    }
    return sort(stations, function(a, b)
        {
            return a - b;
        });
}

// ============================= Anchors =============================

/**
 * The uv where a station's section march starts or ends.
 *
 * anchor kinds:
 *   { anchorKind : "fixedUv", uv : [u, v] } - a constant anchor (a vertex contact).
 *   { anchorKind : "branch", tSamples : [t...], uvSamples : [[u, v]...] } - a co-edge branch:
 *     the bracketing samples in t are interpolated linearly, then the anchor is polished onto
 *     f = 0 ALONG the local sample segment, so it never leaves the shared boundary polyline.
 *     tSamples must be monotone along the branch (components are split at branch t-extremes
 *     upstream, so each side handed here is monotone).
 */
export function anchorUvAtStation(anchor is map, strippedMotion is map, strippedSurface is map,
    t is number, sectionTolerance is number) returns array
{
    if (anchor.anchorKind == "fixedUv")
    {
        return anchor.uv;
    }
    const sampleCount = size(anchor.tSamples);
    var segmentIndex = undefined;
    for (var index = 0; index + 1 < sampleCount; index += 1)
    {
        if ((anchor.tSamples[index] - t) * (anchor.tSamples[index + 1] - t) <= 0)
        {
            segmentIndex = index;
            break;
        }
    }
    if (segmentIndex == undefined)
    {
        segmentIndex = abs(anchor.tSamples[0] - t) < abs(anchor.tSamples[sampleCount - 1] - t) ?
            0 : sampleCount - 2;
    }
    const tLow = anchor.tSamples[segmentIndex];
    const tHigh = anchor.tSamples[segmentIndex + 1];
    const fraction = abs(tHigh - tLow) < 1e-300 ? 0 : clampToRange((t - tLow) / (tHigh - tLow), 0, 1);
    const uvLow = anchor.uvSamples[segmentIndex];
    const uvHigh = anchor.uvSamples[segmentIndex + 1];
    var uv = [uvLow[0] + fraction * (uvHigh[0] - uvLow[0]), uvLow[1] + fraction * (uvHigh[1] - uvLow[1])];
    const segment = [uvHigh[0] - uvLow[0], uvHigh[1] - uvLow[1]];
    return polishAnchorAlongDirection(strippedMotion, strippedSurface, t, uv, segment, sectionTolerance);
}

/**
 * 1D Newton on f restricted to a line through uv along the (unnormalized) direction - the
 * stay-on-boundary polish. The step is capped at twice the direction's own length so a nearly
 * tangential section cannot fling the anchor off its segment.
 */
function polishAnchorAlongDirection(strippedMotion is map, strippedSurface is map, t is number,
    seedUv is array, direction is array, sectionTolerance is number) returns array
{
    const directionLength = sqrt(direction[0] ^ 2 + direction[1] ^ 2);
    if (directionLength < 1e-300)
    {
        return seedUv;
    }
    const unit = [direction[0] / directionLength, direction[1] / directionLength];
    var uv = seedUv;
    var traveled = 0;
    for (var iteration = 0; iteration < 12; iteration += 1)
    {
        const gradient = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, uv[0], uv[1], t);
        if (abs(gradient.value) <= sectionTolerance)
        {
            break;
        }
        const directionalDerivative = gradient.uDerivative * unit[0] + gradient.vDerivative * unit[1];
        if (abs(directionalDerivative) < 1e-30)
        {
            break;
        }
        var step = -gradient.value / directionalDerivative;
        step = clampToRange(step, -2 * directionLength - traveled, 2 * directionLength - traveled);
        traveled += step;
        uv = [uv[0] + step * unit[0], uv[1] + step * unit[1]];
    }
    return uv;
}

// ============================= Section sampling =============================

/**
 * One station's section, marched anchor to anchor and resampled at 2q - 1 arc-length
 * fractions: the even fractions are the station's fit grid row, the odd fractions are fresh
 * q-midpoint samples held out for certification.
 *
 * options (all optional): { sectionTolerance (residual, default 1e-12), marchStepSize
 * (uv units; default anchor distance / (2q)), marchMaxSteps (default max(400, 40q)) }.
 * Returns { failed, reason?, uvRow, liftedRow, midLifted, worstResidual }.
 */
export function sectionSamplesAtStation(strippedMotion is map, strippedSurface is map, t is number,
    startAnchor is map, endAnchor is map, qCount is number, options is map) returns map
{
    const sectionTolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const startUv = anchorUvAtStation(startAnchor, strippedMotion, strippedSurface, t, sectionTolerance);
    const endUv = anchorUvAtStation(endAnchor, strippedMotion, strippedSurface, t, sectionTolerance);
    const anchorDistance = sqrt((endUv[0] - startUv[0]) ^ 2 + (endUv[1] - startUv[1]) ^ 2);
    if (anchorDistance < 1e-12)
    {
        return { "failed" : true, "reason" : "coincident section anchors at t = " ~ t };
    }
    const stepSize = options.marchStepSize == undefined ? anchorDistance / (2 * qCount) : options.marchStepSize;
    const maxSteps = options.marchMaxSteps == undefined ? max(400, 40 * qCount) : options.marchMaxSteps;
    const march = marchSectionCurve(strippedMotion, strippedSurface, t, startUv, endUv,
        { "stepSize" : stepSize, "maxSteps" : maxSteps, "tolerance" : sectionTolerance });
    if (!march.reachedEnd)
    {
        return { "failed" : true, "reason" : "section march did not reach the end anchor at t = " ~ t };
    }
    const resampled = resampleAndPolishSection(strippedMotion, strippedSurface, t, march.uvPoints,
        2 * qCount - 1, sectionTolerance);
    return splitResampledRow(resampled, qCount);
}

/** Split a 2q - 1 resample into the q grid samples (even fractions) and q - 1 fresh midpoints. */
function splitResampledRow(resampled is map, qCount is number) returns map
{
    var uvRow = makeArray(qCount);
    var liftedRow = makeArray(qCount);
    var midLifted = makeArray(qCount - 1);
    for (var index = 0; index < qCount; index += 1)
    {
        uvRow[index] = resampled.uvSamples[2 * index];
        liftedRow[index] = resampled.liftedSamples[2 * index];
        if (index < qCount - 1)
        {
            midLifted[index] = resampled.liftedSamples[2 * index + 1];
        }
    }
    return { "failed" : false, "uvRow" : uvRow, "liftedRow" : liftedRow, "midLifted" : midLifted,
            "worstResidual" : resampled.worstResidual };
}

// ============================= Closed sections (islands) =============================

/**
 * March the closed section loop f(., ., tGlobal) = 0 from a seed already near it: predictor
 * perpendicular to the gradient, corrector along it, terminating when the march returns to
 * within one step of its (corrected) starting point after a minimum step count. The exact
 * start point is appended so the polyline closes identically.
 *
 * options: { stepSize (required), maxSteps (default 400), tolerance (default 1e-12),
 * minimumSteps (default 6) }. Returns { uvPoints, closed }.
 */
export function marchClosedSectionLoop(strippedMotion is map, strippedSurface is map, tGlobal is number,
    seedUv is array, options is map) returns map
{
    const stepSize = options.stepSize;
    const maxSteps = options.maxSteps == undefined ? 400 : options.maxSteps;
    const tolerance = options.tolerance == undefined ? 1e-12 : options.tolerance;
    const minimumSteps = options.minimumSteps == undefined ? 6 : options.minimumSteps;
    const domain = fitSurfaceKnotDomain(strippedSurface);

    var uv = correctUvOntoSection(strippedMotion, strippedSurface, tGlobal, seedUv, domain, tolerance);
    const startUv = uv;
    var uvPoints = [uv];
    var previousTangent = undefined;
    var closed = false;
    for (var step = 0; step < maxSteps; step += 1)
    {
        const gradient = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, uv[0], uv[1], tGlobal);
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            break;
        }
        const gradientNorm = sqrt(gradientNormSquared);
        var tangent = [-gradient.vDerivative / gradientNorm, gradient.uDerivative / gradientNorm];
        if (previousTangent != undefined && tangent[0] * previousTangent[0] + tangent[1] * previousTangent[1] < 0)
        {
            tangent = [-tangent[0], -tangent[1]];
        }
        var predicted = [uv[0] + stepSize * tangent[0], uv[1] + stepSize * tangent[1]];
        predicted = [clampToRange(predicted[0], domain.uMin, domain.uMax),
            clampToRange(predicted[1], domain.vMin, domain.vMax)];
        const corrected = correctUvOntoSection(strippedMotion, strippedSurface, tGlobal, predicted, domain, tolerance);
        uvPoints = append(uvPoints, corrected);
        previousTangent = tangent;
        if (step + 1 >= minimumSteps &&
            (corrected[0] - startUv[0]) ^ 2 + (corrected[1] - startUv[1]) ^ 2 <= stepSize ^ 2)
        {
            uvPoints = append(uvPoints, startUv);
            closed = true;
            break;
        }
        uv = corrected;
    }
    return { "uvPoints" : uvPoints, "closed" : closed };
}

/**
 * The first section crossing on a uv ray: sample outward from fromUv until f changes sign
 * against its value at fromUv, then bisect the bracketing segment. Returns { found, uv }.
 */
export function findSectionCrossingOnRay(strippedMotion is map, strippedSurface is map, t is number,
    fromUv is array, direction is array, maxDistance is number, sampleCount is number) returns map
{
    const startValue = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface,
            fromUv[0], fromUv[1], t).value;
    var lowFraction = 0;
    var lowValue = startValue;
    var highFraction = undefined;
    for (var index = 1; index <= sampleCount; index += 1)
    {
        const fraction = index / sampleCount;
        const uv = [fromUv[0] + direction[0] * fraction * maxDistance,
            fromUv[1] + direction[1] * fraction * maxDistance];
        const value = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, uv[0], uv[1], t).value;
        if (value * startValue <= 0)
        {
            highFraction = fraction;
            break;
        }
        lowFraction = fraction;
        lowValue = value;
    }
    if (highFraction == undefined)
    {
        return { "found" : false };
    }
    for (var iteration = 0; iteration < 60; iteration += 1)
    {
        const midFraction = 0.5 * (lowFraction + highFraction);
        const uv = [fromUv[0] + direction[0] * midFraction * maxDistance,
            fromUv[1] + direction[1] * midFraction * maxDistance];
        const value = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, uv[0], uv[1], t).value;
        if (value * lowValue > 0)
        {
            lowFraction = midFraction;
            lowValue = value;
        }
        else
        {
            highFraction = midFraction;
        }
    }
    const fraction = 0.5 * (lowFraction + highFraction);
    return { "found" : true, "uv" : [fromUv[0] + direction[0] * fraction * maxDistance,
                fromUv[1] + direction[1] * fraction * maxDistance] };
}

/**
 * One island station's closed loop, sampled for the fit grid: seed on the fixed +u ray out of
 * centerUv (the SAME ray every station, which is what keeps the q origin consistent), marched
 * closed, oriented counterclockwise, and resampled at 2q - 1 fractions of its arc length
 * (the last fraction returns to the seed, so the row closes on itself).
 *
 * options: { sectionTolerance (default 1e-12) }.
 * Returns { failed, reason?, uvRow, liftedRow, midLifted, radius, worstResidual }.
 */
export function islandLoopSamples(strippedMotion is map, strippedSurface is map, centerUv is array,
    t is number, qCount is number, options is map) returns map
{
    const sectionTolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const crossing = findSectionCrossingOnRay(strippedMotion, strippedSurface, t, centerUv,
        [1, 0], domain.uMax - centerUv[0], 64);
    if (!crossing.found)
    {
        return { "failed" : true, "reason" : "no island loop crossing on the +u ray at t = " ~ t };
    }
    // Two marched stations per resample interval. Denser marching buys nothing: every
    // resampled point is re-Newtoned onto f = 0 regardless, so polyline density affects only
    // how evenly q lands along the loop, never how far the samples sit from the envelope.
    const radius = sqrt((crossing.uv[0] - centerUv[0]) ^ 2 + (crossing.uv[1] - centerUv[1]) ^ 2);
    const stepSize = max(1e-9, 2 * PI * radius / (2 * (2 * qCount - 2)));
    const loop = marchClosedSectionLoop(strippedMotion, strippedSurface, t, crossing.uv,
        { "stepSize" : stepSize, "maxSteps" : max(400, 40 * qCount), "tolerance" : sectionTolerance });
    if (!loop.closed)
    {
        return { "failed" : true, "reason" : "island loop march did not close at t = " ~ t };
    }
    const oriented = orientLoopCounterClockwise(loop.uvPoints);
    const resampled = resampleClosedLoopSection(strippedMotion, strippedSurface, t, oriented,
        2 * qCount, sectionTolerance);
    var result = splitClosedRow(resampled, qCount);
    result.radius = radius;
    return result;
}

/**
 * Resample a marched CLOSED loop at fixed fractions of its lifted arc length, re-Newton every
 * sample onto f = 0, and lift it. Unlike the open-section resampler the fractions are
 * sampleIndex / sampleCount, so the last sample stops one step short of the start instead of
 * duplicating it - a periodic fit wants n distinct points, and a duplicated closing point would
 * make its collocation system singular.
 * Returns { uvSamples, liftedSamples, worstResidual }.
 */
export function resampleClosedLoopSection(strippedMotion is map, strippedSurface is map, tGlobal is number,
    uvPoints is array, sampleCount is number, tolerance is number) returns map
{
    const pointCount = size(uvPoints);
    var liftedPolyline = makeArray(pointCount);
    for (var index = 0; index < pointCount; index += 1)
    {
        liftedPolyline[index] = liftContactPoint(strippedMotion, strippedSurface,
            uvPoints[index][0], uvPoints[index][1], tGlobal);
    }
    var cumulativeLengths = makeArray(pointCount, 0);
    for (var index = 1; index < pointCount; index += 1)
    {
        cumulativeLengths[index] = cumulativeLengths[index - 1] + norm(liftedPolyline[index] - liftedPolyline[index - 1]);
    }
    const totalLength = cumulativeLengths[pointCount - 1];
    const domain = fitSurfaceKnotDomain(strippedSurface);

    var uvSamples = makeArray(sampleCount);
    var liftedSamples = makeArray(sampleCount);
    var worstResidual = 0;
    var cursor = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        const targetLength = totalLength * sampleIndex / sampleCount;
        while (cursor < pointCount - 2 && cumulativeLengths[cursor + 1] < targetLength)
        {
            cursor += 1;
        }
        const segmentLength = cumulativeLengths[cursor + 1] - cumulativeLengths[cursor];
        const fraction = segmentLength < 1e-300 ? 0 : (targetLength - cumulativeLengths[cursor]) / segmentLength;
        var uv = [uvPoints[cursor][0] + fraction * (uvPoints[cursor + 1][0] - uvPoints[cursor][0]),
            uvPoints[cursor][1] + fraction * (uvPoints[cursor + 1][1] - uvPoints[cursor][1])];
        uv = correctUvOntoSection(strippedMotion, strippedSurface, tGlobal, uv, domain, tolerance);
        worstResidual = max(worstResidual,
            abs(evaluateEnvelopePointwise(strippedMotion, strippedSurface, uv[0], uv[1], tGlobal)));
        uvSamples[sampleIndex] = uv;
        liftedSamples[sampleIndex] = liftContactPoint(strippedMotion, strippedSurface, uv[0], uv[1], tGlobal);
    }
    return { "uvSamples" : uvSamples, "liftedSamples" : liftedSamples, "worstResidual" : worstResidual };
}

/** Split a 2q closed-loop resample into q grid samples and the q midpoints between them - the
    last midpoint spans the wrap, which is exactly where a clamped fit would kink. */
function splitClosedRow(resampled is map, qCount is number) returns map
{
    var uvRow = makeArray(qCount);
    var liftedRow = makeArray(qCount);
    var midLifted = makeArray(qCount);
    for (var index = 0; index < qCount; index += 1)
    {
        uvRow[index] = resampled.uvSamples[2 * index];
        liftedRow[index] = resampled.liftedSamples[2 * index];
        midLifted[index] = resampled.liftedSamples[2 * index + 1];
    }
    return { "failed" : false, "uvRow" : uvRow, "liftedRow" : liftedRow, "midLifted" : midLifted,
            "worstResidual" : resampled.worstResidual };
}

/** Reverse a closed uv polyline (first == last) when its signed area is clockwise. */
function orientLoopCounterClockwise(uvPoints is array) returns array
{
    var signedArea = 0;
    for (var index = 0; index + 1 < size(uvPoints); index += 1)
    {
        signedArea += uvPoints[index][0] * uvPoints[index + 1][1] - uvPoints[index + 1][0] * uvPoints[index][1];
    }
    if (signedArea >= 0)
    {
        return uvPoints;
    }
    const count = size(uvPoints);
    var reversed = makeArray(count);
    for (var index = 0; index < count; index += 1)
    {
        reversed[index] = uvPoints[count - 1 - index];
    }
    return reversed;
}

// ============================= Grid interpolation =============================

/**
 * The tensor-product B-spline surface through a rectangular grid of PLAIN-number points
 * (grid[i][j]: i down the station/u direction, j across the q/v direction) - NURBS Book A9.4
 * composed from the prescribed-parameter curve interpolator, with one extension: a fully
 * collapsed row (an island pole, every point identical) has no chord parameterization, so
 * degenerate rows are skipped when the v parameters are averaged. Clamped interpolation then
 * carries the collapse through exactly: every column's end control point IS its end data
 * point, so a collapsed data row becomes a collapsed control row and the surface closes to
 * the pole with zero error.
 *
 * Returns { uDegree, vDegree, isRational, isUPeriodic, isVPeriodic, controlPoints, uKnots,
 * vKnots, uParameters, vParameters }.
 */
export function interpolateFitGrid(grid is array, uDegree is number, vDegree is number) returns map
{
    return interpolateFitGrid(grid, uDegree, vDegree, false);
}

/**
 * Same, with the v (q) direction optionally PERIODIC - the island case. A closed section loop
 * fitted with a clamped v direction carries a tangent kink where the loop closes; measured
 * 2026-08-23 on the island fixture, that kink dominated the whole patch's certified deviation
 * (6.4e-3 in q against 2.7e-5 of true envelope error). A periodic v direction removes it, and
 * pole rows plus periodic longitude is exactly how a NURBS sphere is represented.
 *
 * Periodic input rows carry n DISTINCT points with no repeated closing point; the result is
 * stored in the module's wrap-padded convention (n + vDegree control columns against
 * n + 2*vDegree + 1 knots) that splineRefinementUtils' evaluators read directly.
 */
export function interpolateFitGrid(grid is array, uDegree is number, vDegree is number, vPeriodic is boolean) returns map
{
    const rowCount = size(grid);
    const columnCount = size(grid[0]);

    var uParameterSets = [];
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        var column = makeArray(rowCount);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            column[rowIndex] = grid[rowIndex][columnIndex];
        }
        const chord = chordParametersPlain(column);
        if (chord.degenerate)
        {
            throw "swEnvelopeFit: fit grid column " ~ columnIndex ~ " is a single repeated point - " ~
                "the motion holds this q sample stationary, so there is no surface to fit.";
        }
        uParameterSets = append(uParameterSets, chord.parameters);
    }
    var vParameterSets = [];
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        const chord = vPeriodic ? closedChordParametersPlain(grid[rowIndex]) : chordParametersPlain(grid[rowIndex]);
        if (!chord.degenerate)
        {
            vParameterSets = append(vParameterSets, chord.parameters);
        }
    }
    if (size(vParameterSets) == 0)
    {
        throw "swEnvelopeFit: every fit grid row is collapsed - nothing to fit.";
    }
    const uParameters = averageParameterSets(uParameterSets, rowCount);
    const vParameters = averageParameterSets(vParameterSets, columnCount);

    // Stage 1 - interpolate every column in u at the shared parameters.
    var stageOneControls = makeArray(columnCount);
    var uKnots = undefined;
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        var column = makeArray(rowCount);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            column[rowIndex] = grid[rowIndex][columnIndex];
        }
        const curve = interpolateBSplineCurveThroughPoints(column, uDegree, uParameters);
        stageOneControls[columnIndex] = curve.controlPoints;
        uKnots = curve.knots;
    }

    // Stage 2 - interpolate every stage-1 coefficient row in v.
    var controlPoints = makeArray(rowCount);
    var vKnots = undefined;
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        var coefficientRow = makeArray(columnCount);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            coefficientRow[columnIndex] = stageOneControls[columnIndex][rowIndex];
        }
        const curve = vPeriodic ? interpolatePeriodicRow(coefficientRow, vDegree, vParameters, 1) :
            interpolateBSplineCurveThroughPoints(coefficientRow, vDegree, vParameters);
        controlPoints[rowIndex] = curve.controlPoints;
        vKnots = curve.knots;
    }

    return {
            "uDegree" : uDegree,
            "vDegree" : vDegree,
            "isRational" : false,
            "isUPeriodic" : false,
            "isVPeriodic" : vPeriodic,
            "controlPoints" : controlPoints,
            "uKnots" : uKnots,
            "vKnots" : vKnots,
            "uParameters" : uParameters,
            "vParameters" : vParameters,
            "vPeriod" : 1
        };
}

/**
 * Interpolate n points at n prescribed parameters with a PERIODIC B-spline of the given degree
 * over one period: a knot at every data parameter makes the collocation system square, and the
 * n unknown control points wrap. Returns the wrap-padded pair the evaluators expect -
 * n + degree control points, n + 2*degree + 1 knots, with knots[j + n] = knots[j] + period so
 * the padding supplies every knot de Boor needs across [knots[degree], knots[degree + n]].
 *
 * parameters must be strictly increasing inside [parameters[0], parameters[0] + period).
 */
export function interpolatePeriodicRow(points is array, degree is number, parameters is array,
    period is number) returns map
{
    const count = size(points);
    if (count < degree + 1)
    {
        throw "swEnvelopeFit: a periodic degree-" ~ degree ~ " interpolation needs at least " ~
            (degree + 1) ~ " points around the loop, got " ~ count ~ ".";
    }
    const knots = periodicInterpolationKnots(parameters, degree, period);

    var collocationRows = makeArray(count);
    for (var dataIndex = 0; dataIndex < count; dataIndex += 1)
    {
        var row = makeArray(count, 0);
        const spanIndex = findEvaluationSpanIndex(knots, degree, parameters[dataIndex]);
        const basisValues = bSplineBasisValues(knots, degree, spanIndex, parameters[dataIndex]);
        for (var offset = 0; offset <= degree; offset += 1)
        {
            const wrappedIndex = (spanIndex - degree + offset) % count;
            row[wrappedIndex] = row[wrappedIndex] + basisValues[offset];
        }
        collocationRows[dataIndex] = row;
    }
    const inverseRows = inverse(matrix(collocationRows));

    var corePoints = makeArray(count, 0 * points[0]);
    for (var controlIndex = 0; controlIndex < count; controlIndex += 1)
    {
        var accumulated = 0 * points[0];
        for (var dataIndex = 0; dataIndex < count; dataIndex += 1)
        {
            accumulated = accumulated + inverseRows[controlIndex][dataIndex] * points[dataIndex];
        }
        corePoints[controlIndex] = accumulated;
    }

    var controlPoints = makeArray(count + degree, 0 * points[0]);
    for (var controlIndex = 0; controlIndex < count + degree; controlIndex += 1)
    {
        controlPoints[controlIndex] = corePoints[controlIndex % count];
    }
    return { "controlPoints" : controlPoints, "knots" : knots, "parameters" : parameters };
}

/** The wrap-padded knot array with a knot at every data parameter: knots[j] = t(j - degree). */
function periodicInterpolationKnots(parameters is array, degree is number, period is number) returns array
{
    const count = size(parameters);
    var knots = makeArray(count + 2 * degree + 1, 0);
    for (var knotIndex = 0; knotIndex < count + 2 * degree + 1; knotIndex += 1)
    {
        const baseIndex = knotIndex - degree;
        const wraps = floor(baseIndex / count);
        knots[knotIndex] = parameters[baseIndex - wraps * count] + wraps * period;
    }
    return knots;
}

/** Chord-length parameters of a plain-number polyline; degenerate when its total length vanishes. */
function chordParametersPlain(points is array) returns map
{
    const count = size(points);
    var distances = makeArray(count, 0);
    var totalLength = 0;
    for (var index = 1; index < count; index += 1)
    {
        distances[index] = norm(points[index] - points[index - 1]);
        totalLength += distances[index];
    }
    if (totalLength <= 1e-14)
    {
        return { "degenerate" : true };
    }
    var parameters = makeArray(count, 0);
    for (var index = 1; index < count - 1; index += 1)
    {
        parameters[index] = parameters[index - 1] + distances[index] / totalLength;
    }
    parameters[count - 1] = 1;
    return { "degenerate" : false, "parameters" : parameters };
}

/**
 * Chord-length parameters of a CLOSED polyline given by its n distinct points: the return
 * segment from the last point back to the first counts toward the period, so the parameters
 * land in [0, 1) and the period is exactly 1.
 */
function closedChordParametersPlain(points is array) returns map
{
    const count = size(points);
    var distances = makeArray(count, 0);
    var totalLength = 0;
    for (var index = 0; index < count; index += 1)
    {
        distances[index] = norm(points[(index + 1) % count] - points[index]);
        totalLength += distances[index];
    }
    if (totalLength <= 1e-14)
    {
        return { "degenerate" : true };
    }
    var parameters = makeArray(count, 0);
    for (var index = 1; index < count; index += 1)
    {
        parameters[index] = parameters[index - 1] + distances[index - 1] / totalLength;
    }
    return { "degenerate" : false, "parameters" : parameters };
}

/** Elementwise average of equal-length parameter arrays. */
function averageParameterSets(parameterSets is array, expectedCount is number) returns array
{
    var averaged = makeArray(expectedCount, 0);
    for (var index = 0; index < expectedCount; index += 1)
    {
        var total = 0;
        for (var parameterSet in parameterSets)
        {
            total += parameterSet[index];
        }
        averaged[index] = total / size(parameterSets);
    }
    return averaged;
}

// ============================= Rectangle component fit =============================

/**
 * Fit one boundary-touching funnel component on the (q, t) rectangle (spec 7.2): assemble the
 * grid from anchored section marches, interpolate at bicubic degree, certify against fresh
 * Newton-converged envelope samples the fit never saw, double the deficient direction until
 * tolerance or the 60x60 budget, then remove redundant knots with the remaining slack.
 *
 * fitInput: {
 *     tStart, tEnd {number} : the component's t range,
 *     startAnchor, endAnchor {map} : see anchorUvAtStation,
 *     tolerance {number} : certified 3D deviation target (meters implied),
 *     eventTimes {array, default []} : t values every station set must contain,
 *     initialQCount {default 12}, initialStationCount {default 16},
 *     maxQCount {default 60}, maxStationCount {default 60},
 *     maxRefinementRounds {default 8},
 *     orientOutward {default true} : reverse the q direction when swOrientation says the net
 *         would otherwise face into the swept volume (spec 6.6). Set false only to A/B the
 *         orientation pass - a knit needs the outward net.
 *     sectionTolerance, marchStepSize, marchMaxSteps : see sectionSamplesAtStation
 * }
 *
 * Returns { failed, reason? } or {
 *     failed : false, surface (plain numbers, emission via attachFitSurfaceUnits),
 *     worstDeviation, worstQDeviation, worstTDeviation, removalDeviation, certifiedBound,
 *     budgetHit ("SWEEP_FIT_BUDGET_HIT" semantics - best surface returned, over tolerance),
 *     refinementRounds, qCount, stationCount, stations, worstSectionResidual,
 *     orientation : certifyFitGridOrientation's map for the grid AS MARCHED - the lambda sign,
 *         its consistency (a component whose lambda flips FOLDS and must not be emitted), the
 *         outward verdict and its finite-difference cross-check,
 *     qReversed {boolean} : whether the q direction was then flipped. With orientOutward true
 *         and a decidable certificate, the returned surface faces outward either way; read
 *         `orientation.consistent` before trusting it, and `qReversed` only to line up other
 *         data against the emitted net's q parameters
 * }
 */
export function fitEnvelopeComponent(strippedMotion is map, strippedSurface is map, fitInput is map) returns map
{
    const input = mergeMaps({
                "eventTimes" : [],
                "initialQCount" : 12, "initialStationCount" : 16,
                "maxQCount" : 60, "maxStationCount" : 60,
                "maxRefinementRounds" : 8, "orientOutward" : true
            }, fitInput);
    var qCount = max(4, input.initialQCount);
    var stationCount = max(4, input.initialStationCount);
    var budgetHit = false;
    var rounds = 0;
    var result = undefined;

    for (var round = 0; round < input.maxRefinementRounds; round += 1)
    {
        rounds = round + 1;
        const spacing = (input.tEnd - input.tStart) / (stationCount - 1);
        const stations = buildFitStations(input.tStart, input.tEnd, stationCount, input.eventTimes, 0.25 * spacing);

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var uvRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            const row = sectionSamplesAtStation(strippedMotion, strippedSurface, stations[stationIndex],
                input.startAnchor, input.endAnchor, qCount, input);
            if (row.failed)
            {
                return { "failed" : true, "reason" : row.reason };
            }
            liftedGrid[stationIndex] = row.liftedRow;
            qMidRows[stationIndex] = row.midLifted;
            uvRows[stationIndex] = row.uvRow;
            worstSectionResidual = max(worstSectionResidual, row.worstResidual);
        }

        // Orientation (spec 6.6). The net's normal is Phi_t x Phi_q, so which way the section
        // march ran decides whether it faces out of the swept volume; reversing q is exact and
        // costs nothing here, before interpolation. The certificate also carries the lambda
        // sign check, which is what says this component does not fold.
        const orientation = certifyFitGridOrientation(strippedMotion, strippedSurface, uvRows,
                stations, { "liftedGrid" : liftedGrid });
        const flipQ = input.orientOutward && orientation.flipRequired;
        if (flipQ)
        {
            for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
            {
                liftedGrid[stationIndex] = reverseFitGridRow(liftedGrid[stationIndex], false);
                qMidRows[stationIndex] = reverseFitGridMidRow(qMidRows[stationIndex]);
            }
        }
        const fitSurface = interpolateFitGrid(liftedGrid, 3, 3);

        // Certification: q midpoints at the stations attribute the q direction; fresh
        // sections at station midpoints attribute t (grid fractions) and the rest (odd
        // fractions).
        var worstQ = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            for (var midIndex = 0; midIndex < qCount - 1; midIndex += 1)
            {
                const seed = vector(fitSurface.uParameters[stationIndex],
                    0.5 * (fitSurface.vParameters[midIndex] + fitSurface.vParameters[midIndex + 1]));
                worstQ = max(worstQ, invertPointOnSurface(fitSurface, qMidRows[stationIndex][midIndex], seed).residual);
            }
        }
        var worstT = 0;
        var worstBoth = 0;
        for (var stationIndex = 0; stationIndex + 1 < size(stations); stationIndex += 1)
        {
            const tMid = 0.5 * (stations[stationIndex] + stations[stationIndex + 1]);
            const uSeed = 0.5 * (fitSurface.uParameters[stationIndex] + fitSurface.uParameters[stationIndex + 1]);
            const freshRow = sectionSamplesAtStation(strippedMotion, strippedSurface, tMid,
                input.startAnchor, input.endAnchor, qCount, input);
            if (freshRow.failed)
            {
                return { "failed" : true, "reason" : freshRow.reason };
            }
            // A held-out row is marched anchor to anchor like the grid rows were, so a flipped
            // q direction has to be applied to it too or every q parameter it is checked
            // against is mirrored.
            const freshLifted = flipQ ? reverseFitGridRow(freshRow.liftedRow, false) : freshRow.liftedRow;
            const freshMid = flipQ ? reverseFitGridMidRow(freshRow.midLifted) : freshRow.midLifted;
            for (var qIndex = 0; qIndex < qCount; qIndex += 1)
            {
                worstT = max(worstT, invertPointOnSurface(fitSurface, freshLifted[qIndex],
                            vector(uSeed, fitSurface.vParameters[qIndex])).residual);
                if (qIndex < qCount - 1)
                {
                    worstBoth = max(worstBoth, invertPointOnSurface(fitSurface, freshMid[qIndex],
                                vector(uSeed, 0.5 * (fitSurface.vParameters[qIndex] + fitSurface.vParameters[qIndex + 1]))).residual);
                }
            }
        }
        const worstDeviation = max(worstQ, max(worstT, worstBoth));

        result = {
                "failed" : false,
                "surface" : fitSurface,
                "worstDeviation" : worstDeviation,
                "worstQDeviation" : worstQ,
                "worstTDeviation" : worstT,
                "removalDeviation" : 0,
                "certifiedBound" : worstDeviation,
                "budgetHit" : false,
                "refinementRounds" : rounds,
                "qCount" : qCount,
                "stationCount" : size(stations),
                "stations" : stations,
                "worstSectionResidual" : worstSectionResidual,
                "orientation" : orientation,
                "qReversed" : flipQ
            };
        if (worstDeviation <= input.tolerance)
        {
            break;
        }
        var needQ = worstQ > input.tolerance;
        var needT = worstT > input.tolerance;
        if (!needQ && !needT)
        {
            needQ = true;
            needT = true;
        }
        const grownQ = min(2 * qCount - 1, input.maxQCount);
        const grownStations = min(2 * stationCount - 1, input.maxStationCount);
        const canGrow = (needQ && grownQ > qCount) || (needT && grownStations > stationCount);
        if (!canGrow || round == input.maxRefinementRounds - 1)
        {
            budgetHit = true;
            break;
        }
        if (needQ)
        {
            qCount = grownQ;
        }
        if (needT)
        {
            stationCount = grownStations;
        }
    }

    result.budgetHit = budgetHit;
    if (!budgetHit)
    {
        const slack = input.tolerance - result.worstDeviation;
        if (slack > 0)
        {
            const cleaned = removeRedundantSurfaceKnots(attachFitSurfaceUnits(result.surface), slack * meter);
            result.removalDeviation = cleaned.deviation / meter;
            var cleanedPlain = stripFitSurfaceUnits(cleaned);
            cleanedPlain.uParameters = result.surface.uParameters;
            cleanedPlain.vParameters = result.surface.vParameters;
            result.surface = cleanedPlain;
            result.certifiedBound = result.worstDeviation + result.removalDeviation;
        }
    }
    return result;
}

// ============================= Island component fit =============================

/**
 * Fit one grazing island as a pole-collapsed patch (spec 7.1, probe 4): stations span birth
 * to death inclusive, the two pole rows are the lifted stationary points repeated, interior
 * stations are closed loops seeded on the fixed +u ray out of the interpolated island center.
 * Certification and refinement follow fitEnvelopeComponent; knot cleanup is skipped so the
 * pole rows stay exactly collapsed.
 *
 * islandInput: {
 *     birth, death {map} : { u, v, t } from island refinement (refineBlockStationaryPoint),
 *     tolerance {number},
 *     initialQCount {default 12}, initialStationCount {default 16},
 *     maxQCount {default 60}, maxStationCount {default 60},
 *     maxRefinementRounds {default 8}, sectionTolerance {default 1e-12}
 * }
 *
 * Returns the fitEnvelopeComponent result shape plus poleStartPoint / poleEndPoint (the
 * lifted 3D poles, plain numbers).
 */
export function fitIslandComponent(strippedMotion is map, strippedSurface is map, islandInput is map) returns map
{
    const input = mergeMaps({
                "initialQCount" : 12, "initialStationCount" : 16,
                "maxQCount" : 60, "maxStationCount" : 60,
                "maxRefinementRounds" : 8, "orientOutward" : true
            }, islandInput);
    const birth = input.birth;
    const death = input.death;
    const poleStartPoint = liftContactPoint(strippedMotion, strippedSurface, birth.u, birth.v, birth.t);
    const poleEndPoint = liftContactPoint(strippedMotion, strippedSurface, death.u, death.v, death.t);
    var qCount = max(4, input.initialQCount);
    var stationCount = max(4, input.initialStationCount);
    var budgetHit = false;
    var rounds = 0;
    var result = undefined;

    for (var round = 0; round < input.maxRefinementRounds; round += 1)
    {
        rounds = round + 1;
        const stations = buildFitStations(birth.t, death.t, stationCount, [], 0);

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var uvRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            if (stationIndex == 0 || stationIndex == size(stations) - 1)
            {
                const pole = stationIndex == 0 ? birth : death;
                liftedGrid[stationIndex] = makeArray(qCount, stationIndex == 0 ? poleStartPoint : poleEndPoint);
                qMidRows[stationIndex] = undefined;
                // A collapsed uv row, which the orientation certificate recognizes and skips -
                // there is no q direction at a pole.
                uvRows[stationIndex] = makeArray(qCount, [pole.u, pole.v]);
                continue;
            }
            const loop = islandLoopSamples(strippedMotion, strippedSurface,
                islandCenterAt(birth, death, stations[stationIndex]), stations[stationIndex], qCount, input);
            if (loop.failed)
            {
                return { "failed" : true, "reason" : loop.reason };
            }
            liftedGrid[stationIndex] = loop.liftedRow;
            qMidRows[stationIndex] = loop.midLifted;
            uvRows[stationIndex] = loop.uvRow;
            worstSectionResidual = max(worstSectionResidual, loop.worstResidual);
        }

        // Orientation (spec 6.6). The island's loops are marched counterclockwise in uv, which
        // is a consistent q origin and direction but says nothing about which way the patch
        // faces - lambda does. Reversing a CLOSED row holds its first sample fixed, so the q
        // origin the loop marching established survives the flip.
        const orientation = certifyFitGridOrientation(strippedMotion, strippedSurface, uvRows,
                stations, { "liftedGrid" : liftedGrid });
        const flipQ = input.orientOutward && orientation.flipRequired;
        if (flipQ)
        {
            for (var stationIndex = 1; stationIndex + 1 < size(stations); stationIndex += 1)
            {
                liftedGrid[stationIndex] = reverseFitGridRow(liftedGrid[stationIndex], true);
                qMidRows[stationIndex] = reverseFitGridMidRow(qMidRows[stationIndex]);
            }
        }
        const fitSurface = interpolateFitGrid(liftedGrid, 3, 3, true);

        var worstQ = 0;
        for (var stationIndex = 1; stationIndex + 1 < size(stations); stationIndex += 1)
        {
            for (var midIndex = 0; midIndex < qCount; midIndex += 1)
            {
                const seed = vector(fitSurface.uParameters[stationIndex],
                    periodicMidParameter(fitSurface.vParameters, midIndex, 1));
                worstQ = max(worstQ, invertPointOnSurface(fitSurface, qMidRows[stationIndex][midIndex], seed).residual);
            }
        }
        var worstT = 0;
        var worstBoth = 0;
        for (var stationIndex = 0; stationIndex + 1 < size(stations); stationIndex += 1)
        {
            const tMid = 0.5 * (stations[stationIndex] + stations[stationIndex + 1]);
            const uSeed = 0.5 * (fitSurface.uParameters[stationIndex] + fitSurface.uParameters[stationIndex + 1]);
            const freshLoop = islandLoopSamples(strippedMotion, strippedSurface,
                islandCenterAt(birth, death, tMid), tMid, qCount, input);
            if (freshLoop.failed)
            {
                return { "failed" : true, "reason" : freshLoop.reason };
            }
            // Held-out loops are marched the same way the grid rows were, so a flipped q has
            // to reach them too.
            const freshLifted = flipQ ? reverseFitGridRow(freshLoop.liftedRow, true) : freshLoop.liftedRow;
            const freshMid = flipQ ? reverseFitGridMidRow(freshLoop.midLifted) : freshLoop.midLifted;
            for (var qIndex = 0; qIndex < qCount; qIndex += 1)
            {
                worstT = max(worstT, invertPointOnSurface(fitSurface, freshLifted[qIndex],
                            vector(uSeed, fitSurface.vParameters[qIndex])).residual);
                worstBoth = max(worstBoth, invertPointOnSurface(fitSurface, freshMid[qIndex],
                            vector(uSeed, periodicMidParameter(fitSurface.vParameters, qIndex, 1))).residual);
            }
        }
        const worstDeviation = max(worstQ, max(worstT, worstBoth));

        result = {
                "failed" : false,
                "surface" : fitSurface,
                "worstDeviation" : worstDeviation,
                "worstQDeviation" : worstQ,
                "worstTDeviation" : worstT,
                "removalDeviation" : 0,
                "certifiedBound" : worstDeviation,
                "budgetHit" : false,
                "refinementRounds" : rounds,
                "qCount" : qCount,
                "stationCount" : size(stations),
                "stations" : stations,
                "worstSectionResidual" : worstSectionResidual,
                "orientation" : orientation,
                "qReversed" : flipQ,
                "poleStartPoint" : poleStartPoint,
                "poleEndPoint" : poleEndPoint
            };
        if (worstDeviation <= input.tolerance)
        {
            break;
        }
        var needQ = worstQ > input.tolerance;
        var needT = worstT > input.tolerance;
        if (!needQ && !needT)
        {
            needQ = true;
            needT = true;
        }
        const grownQ = min(2 * qCount - 1, input.maxQCount);
        const grownStations = min(2 * stationCount - 1, input.maxStationCount);
        const canGrow = (needQ && grownQ > qCount) || (needT && grownStations > stationCount);
        if (!canGrow || round == input.maxRefinementRounds - 1)
        {
            budgetHit = true;
            break;
        }
        if (needQ)
        {
            qCount = grownQ;
        }
        if (needT)
        {
            stationCount = grownStations;
        }
    }

    result.budgetHit = budgetHit;
    return result;
}

/** The parameter halfway between sample index and its successor around a period - the last one
    spans the wrap, which is the seam a clamped fit would kink at. */
function periodicMidParameter(parameters is array, index is number, period is number) returns number
{
    const count = size(parameters);
    const nextParameter = index + 1 < count ? parameters[index + 1] : parameters[0] + period;
    return 0.5 * (parameters[index] + nextParameter);
}

/** The island center's uv at a station, interpolated linearly between birth and death. */
function islandCenterAt(birth is map, death is map, t is number) returns array
{
    const fraction = (t - birth.t) / (death.t - birth.t);
    return [birth.u + fraction * (death.u - birth.u), birth.v + fraction * (death.v - birth.v)];
}

// ============================= Tube component fit =============================

/**
 * Fit one funnel component whose sections are CLOSED LOOPS THAT WRAP THE PERIODIC u SEAM of the
 * tool face (spec 7.5) - the shape a closed smooth tool makes when it travels roughly along its
 * own parameterization axis, and the lateral surface of the first watertight swept solid.
 *
 * It is the third component shape, between the two the module already fits. A rectangle
 * component is anchored at two co-edge branches and open in q; an island is a closed loop that
 * shrinks to a pole at each end of its t range; a tube is a closed loop at EVERY station, all
 * the way to t0 and t1, so the patch is a cylinder: q periodic, t clamped, no poles, and its
 * two t-edges are the contact loops the start and end caps are trimmed to.
 *
 * The periodic seam needs no rewindow and no pre-split, which settles the question deferred out
 * of steps 4 and 5. Nothing about a section march cares where the face's seam is: the marcher
 * carries u UNWRAPPED and wraps it only on the way into an evaluator, so one loop is one
 * monotone strip of arc length whatever the seam does, and the (q, t) reparameterization hands
 * the kernel a rectangle that never mentions u. What the seam costs is one flag: the q direction
 * of the fit must be declared periodic, exactly as the island's q must be (spec 7.4).
 *
 * tubeInput: {
 *     tStart, tEnd {number} : the component's t range,
 *     tolerance {number} : certified 3D deviation target (meters implied),
 *     uSeed {number, default the u domain midpoint} : the FIXED meridian every station is
 *         seeded on, which is what pins the q origin from row to row,
 *     vMargin {number, default 0} : v excluded at both ends of the domain - set it past the
 *         degenerate pole rows of a surface of revolution, where the normal vanishes and f is
 *         identically zero for reasons that have nothing to do with contact,
 *     eventTimes {array, default []}, initialQCount {default 12}, initialStationCount
 *         {default 16}, maxQCount {default 60}, maxStationCount {default 60},
 *     maxRefinementRounds {default 8}, sectionTolerance {default 1e-12},
 *     meridianSamples {default 32}
 * }
 *
 * Returns the fitEnvelopeComponent result shape (failed / surface / worstDeviation /
 * worstQDeviation / worstTDeviation / certifiedBound / budgetHit / refinementRounds / qCount /
 * stationCount / stations / worstSectionResidual) plus startLoop and endLoop, the lifted t0 and
 * t1 section rows the caps are cut with. Knot cleanup is skipped for the same reason the island
 * skips it: removal perturbs control rows, and here it would also have to preserve the wrap.
 */
export function fitTubeComponent(strippedMotion is map, strippedSurface is map, tubeInput is map) returns map
{
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const input = mergeMaps({
                "eventTimes" : [],
                "initialQCount" : 12, "initialStationCount" : 16,
                "maxQCount" : 60, "maxStationCount" : 60,
                "maxRefinementRounds" : 8, "orientOutward" : true,
                "uSeed" : 0.5 * (domain.uMin + domain.uMax)
            }, tubeInput);
    var qCount = max(4, input.initialQCount);
    var stationCount = max(4, input.initialStationCount);
    var budgetHit = false;
    var rounds = 0;
    var result = undefined;

    for (var round = 0; round < input.maxRefinementRounds; round += 1)
    {
        rounds = round + 1;
        const spacing = (input.tEnd - input.tStart) / (stationCount - 1);
        const stations = buildFitStations(input.tStart, input.tEnd, stationCount, input.eventTimes, 0.25 * spacing);

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var uvRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        // Each station seeds its meridian search from its predecessor's crossing, so a face
        // whose meridian is cut more than once stays on one branch instead of hopping.
        var previousSeedV = undefined;
        var firstSeedV = undefined;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            const loop = tubeLoopSamples(strippedMotion, strippedSurface, stations[stationIndex],
                input.uSeed, qCount, mergeMaps(input, { "preferV" : previousSeedV }));
            if (loop.failed)
            {
                return { "failed" : true, "reason" : loop.reason };
            }
            liftedGrid[stationIndex] = loop.liftedRow;
            qMidRows[stationIndex] = loop.midLifted;
            uvRows[stationIndex] = loop.uvRow;
            worstSectionResidual = max(worstSectionResidual, loop.worstResidual);
            previousSeedV = loop.seedV;
            if (stationIndex == 0)
            {
                firstSeedV = loop.seedV;
            }
        }

        // Orientation (spec 6.6). The tube's loops carry u unwrapped, so their q direction is
        // whichever way the wrapping march happened to run - lambda decides whether that faces
        // out. Reversing a CLOSED row holds the seed meridian's sample fixed.
        const orientation = certifyFitGridOrientation(strippedMotion, strippedSurface, uvRows,
                stations, { "liftedGrid" : liftedGrid });
        const flipQ = input.orientOutward && orientation.flipRequired;
        if (flipQ)
        {
            for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
            {
                liftedGrid[stationIndex] = reverseFitGridRow(liftedGrid[stationIndex], true);
                qMidRows[stationIndex] = reverseFitGridMidRow(qMidRows[stationIndex]);
            }
        }
        const fitSurface = interpolateFitGrid(liftedGrid, 3, 3, true);

        // Certification, direction-resolved exactly as the rectangle and island fits do it:
        // q midpoints at the stations attribute q, fresh whole loops at midpoint stations
        // attribute t, and the midpoints of those bound the rest. Every q midpoint counts here,
        // the wrap one included - it is the sample a clamped q would have kinked at.
        var worstQ = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            for (var midIndex = 0; midIndex < qCount; midIndex += 1)
            {
                const seed = vector(fitSurface.uParameters[stationIndex],
                    periodicMidParameter(fitSurface.vParameters, midIndex, 1));
                worstQ = max(worstQ, invertPointOnSurface(fitSurface, qMidRows[stationIndex][midIndex], seed).residual);
            }
        }
        var worstT = 0;
        var worstBoth = 0;
        var freshSeedV = undefined;
        for (var stationIndex = 0; stationIndex + 1 < size(stations); stationIndex += 1)
        {
            const tMid = 0.5 * (stations[stationIndex] + stations[stationIndex + 1]);
            const uSeed = 0.5 * (fitSurface.uParameters[stationIndex] + fitSurface.uParameters[stationIndex + 1]);
            const freshLoop = tubeLoopSamples(strippedMotion, strippedSurface, tMid, input.uSeed, qCount,
                mergeMaps(input, { "preferV" : freshSeedV == undefined ? firstSeedV : freshSeedV }));
            if (freshLoop.failed)
            {
                return { "failed" : true, "reason" : freshLoop.reason };
            }
            freshSeedV = freshLoop.seedV;
            // Held-out loops are marched the same way the grid rows were, so a flipped q has
            // to reach them too.
            const freshLifted = flipQ ? reverseFitGridRow(freshLoop.liftedRow, true) : freshLoop.liftedRow;
            const freshMid = flipQ ? reverseFitGridMidRow(freshLoop.midLifted) : freshLoop.midLifted;
            for (var qIndex = 0; qIndex < qCount; qIndex += 1)
            {
                worstT = max(worstT, invertPointOnSurface(fitSurface, freshLifted[qIndex],
                            vector(uSeed, fitSurface.vParameters[qIndex])).residual);
                worstBoth = max(worstBoth, invertPointOnSurface(fitSurface, freshMid[qIndex],
                            vector(uSeed, periodicMidParameter(fitSurface.vParameters, qIndex, 1))).residual);
            }
        }
        const worstDeviation = max(worstQ, max(worstT, worstBoth));

        result = {
                "failed" : false,
                "surface" : fitSurface,
                "worstDeviation" : worstDeviation,
                "worstQDeviation" : worstQ,
                "worstTDeviation" : worstT,
                "removalDeviation" : 0,
                "certifiedBound" : worstDeviation,
                "budgetHit" : false,
                "refinementRounds" : rounds,
                "qCount" : qCount,
                "stationCount" : size(stations),
                "stations" : stations,
                "worstSectionResidual" : worstSectionResidual,
                "orientation" : orientation,
                "qReversed" : flipQ,
                "startLoop" : liftedGrid[0],
                "endLoop" : liftedGrid[size(stations) - 1]
            };
        if (worstDeviation <= input.tolerance)
        {
            break;
        }
        var needQ = worstQ > input.tolerance;
        var needT = worstT > input.tolerance;
        if (!needQ && !needT)
        {
            needQ = true;
            needT = true;
        }
        const grownQ = min(2 * qCount - 1, input.maxQCount);
        const grownStations = min(2 * stationCount - 1, input.maxStationCount);
        const canGrow = (needQ && grownQ > qCount) || (needT && grownStations > stationCount);
        if (!canGrow || round == input.maxRefinementRounds - 1)
        {
            budgetHit = true;
            break;
        }
        if (needQ)
        {
            qCount = grownQ;
        }
        if (needT)
        {
            stationCount = grownStations;
        }
    }

    result.budgetHit = budgetHit;
    return result;
}

/**
 * One tube station's wrapping loop, sampled for the fit grid: seeded on the fixed meridian,
 * marched with u wrapping, and resampled at 2q arc-length fractions in one call - the even
 * fractions are the fit row, the odd ones are held-out midpoints for certification, and the
 * last midpoint spans the wrap.
 *
 * options: everything fitTubeComponent's input carries; preferV picks the meridian crossing.
 * Returns { failed, reason?, uvRow, liftedRow, midLifted, worstResidual, seedV, uTravel }.
 */
export function tubeLoopSamples(strippedMotion is map, strippedSurface is map, t is number,
    uSeed is number, qCount is number, options is map) returns map
{
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const sectionTolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const seed = tubeSeedOnMeridian(strippedMotion, strippedSurface, t, uSeed, options);
    if (!seed.found)
    {
        return { "failed" : true, "reason" : "no tube section crossing on the u = " ~ uSeed ~
                    " meridian at t = " ~ t };
    }
    // Two marched points per resample interval, as the island loop uses: every resampled point
    // is re-Newtoned onto f = 0 regardless, so density only evens out where q lands.
    const period = domain.uMax - domain.uMin;
    const stepSize = max(1e-9, period / (4 * qCount));
    const loop = marchWrappingSectionLoop(strippedMotion, strippedSurface, t, seed.uv,
        mergeMaps(options, { "stepSize" : stepSize, "maxSteps" : max(400, 40 * qCount),
                    "tolerance" : sectionTolerance }));
    if (!loop.closed)
    {
        return { "failed" : true, "reason" : "the tube loop march did not close at t = " ~ t ~
                    (loop.hitVBoundary ? " - it ran into the v boundary, so this component reaches a " ~
                        "pole or a face edge and is not a tube" : "") };
    }
    if (loop.uTravel == 0)
    {
        return { "failed" : true, "reason" : "the section loop at t = " ~ t ~ " closed without " ~
                    "wrapping the u seam - it is an island, not a tube" };
    }
    const resampled = resampleWrappingLoopSection(strippedMotion, strippedSurface, t, loop.uvPoints,
        2 * qCount, sectionTolerance);
    var result = splitClosedRow(resampled, qCount);
    result.seedV = seed.uv[1];
    result.uTravel = loop.uTravel;
    return result;
}

/**
 * Where f(., ., t) = 0 crosses the FIXED meridian u = uSeed: a Newton continuation from the
 * previous station's crossing when there is one, else a v scan between the margins for a sign
 * change, bisected. Seeding every station on the same meridian is what keeps the q origin from
 * drifting around the loop - the island fit's fixed +u ray, one dimension over.
 *
 * options: { vMargin (default 0), meridianSamples (default 32), preferV (default undefined) }.
 * Returns { found, uv, crossingCount } - crossingCount 0 on the continuation path, which never
 * scans.
 */
export function tubeSeedOnMeridian(strippedMotion is map, strippedSurface is map, t is number,
    uSeed is number, options is map) returns map
{
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const uPeriodic = strippedSurface.isUPeriodic == true;
    const margin = options.vMargin == undefined ? 0 : options.vMargin;
    const sampleCount = options.meridianSamples == undefined ? 32 : options.meridianSamples;
    const vLow = domain.vMin + margin;
    const vHigh = domain.vMax - margin;

    if (options.preferV != undefined)
    {
        const continued = polishMeridianCrossing(strippedMotion, strippedSurface, t, uSeed,
            options.preferV, vLow, vHigh, domain, uPeriodic);
        if (continued.converged)
        {
            return { "found" : true, "uv" : [uSeed, continued.v], "crossingCount" : 0 };
        }
    }

    var previousV = vLow;
    var previousValue = wrappedEnvelopeGradient(strippedMotion, strippedSurface, [uSeed, vLow], t,
            domain, uPeriodic).value;
    var bracketLow = undefined;
    var bracketHigh = undefined;
    var bracketLowValue = 0;
    var crossingCount = 0;
    for (var index = 1; index <= sampleCount; index += 1)
    {
        const v = vLow + (vHigh - vLow) * index / sampleCount;
        const value = wrappedEnvelopeGradient(strippedMotion, strippedSurface, [uSeed, v], t,
                domain, uPeriodic).value;
        if (previousValue * value <= 0)
        {
            crossingCount += 1;
            const midpoint = 0.5 * (previousV + v);
            if (bracketLow == undefined || (options.preferV != undefined &&
                        abs(midpoint - options.preferV) < abs(0.5 * (bracketLow + bracketHigh) - options.preferV)))
            {
                bracketLow = previousV;
                bracketHigh = v;
                bracketLowValue = previousValue;
            }
        }
        previousV = v;
        previousValue = value;
    }
    if (bracketLow == undefined)
    {
        return { "found" : false, "crossingCount" : 0 };
    }
    var low = bracketLow;
    var high = bracketHigh;
    var lowValue = bracketLowValue;
    for (var iteration = 0; iteration < 48; iteration += 1)
    {
        const mid = 0.5 * (low + high);
        const value = wrappedEnvelopeGradient(strippedMotion, strippedSurface, [uSeed, mid], t,
                domain, uPeriodic).value;
        if (value * lowValue > 0)
        {
            low = mid;
            lowValue = value;
        }
        else
        {
            high = mid;
        }
    }
    const polished = polishMeridianCrossing(strippedMotion, strippedSurface, t, uSeed,
        0.5 * (low + high), vLow, vHigh, domain, uPeriodic);
    return { "found" : true, "uv" : [uSeed, polished.v], "crossingCount" : crossingCount };
}

/**
 * March the closed section loop of a face at one station, letting it WRAP the periodic u seam
 * rather than stalling against it. The polyline is carried in UNWRAPPED u and wrapped only on
 * the way into an evaluator, so arc length, closure, and the resample all see one monotone
 * strip instead of a curve that teleports at the seam.
 *
 * Closure is tested against the seed's images u0 + k * period for k in {1, 0, -1}, wrap first.
 * k = 0 means the loop closed WITHOUT wrapping - an island, not a tube - and is reported as
 * uTravel 0 rather than quietly fitted as a tube. The march also stops if it reaches vMargin of
 * the v domain, because a component that runs into a pole or a face edge is not a tube either.
 *
 * options: { stepSize, maxSteps (default 400), tolerance (default 1e-12), minimumSteps
 * (default 6), vMargin (default 0) }.
 * Returns { closed, uvPoints (unwrapped u, the seed's closing image appended), uTravel,
 * hitVBoundary }.
 */
export function marchWrappingSectionLoop(strippedMotion is map, strippedSurface is map, tGlobal is number,
    seedUv is array, options is map) returns map
{
    const stepSize = options.stepSize;
    const maxSteps = options.maxSteps == undefined ? 400 : options.maxSteps;
    const tolerance = options.tolerance == undefined ? 1e-12 : options.tolerance;
    const minimumSteps = options.minimumSteps == undefined ? 6 : options.minimumSteps;
    const margin = options.vMargin == undefined ? 0 : options.vMargin;
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const uPeriodic = strippedSurface.isUPeriodic == true;
    const period = uPeriodic ? domain.uMax - domain.uMin : 0;

    var uv = correctUvWrapped(strippedMotion, strippedSurface, tGlobal, seedUv, domain, uPeriodic, tolerance);
    const startUv = uv;
    var uvPoints = makeArray(maxSteps + 2, uv);
    var pointCount = 1;
    var previousTangent = undefined;
    var closed = false;
    var uTravel = 0;
    var hitVBoundary = false;
    for (var step = 0; step < maxSteps; step += 1)
    {
        const gradient = wrappedEnvelopeGradient(strippedMotion, strippedSurface, uv, tGlobal, domain, uPeriodic);
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            break;
        }
        const gradientNorm = sqrt(gradientNormSquared);
        var tangent = [-gradient.vDerivative / gradientNorm, gradient.uDerivative / gradientNorm];
        if (previousTangent == undefined)
        {
            // The first step fixes the direction of travel as +u, so every station traverses q
            // the same way around the tool and a closing uTravel comes out positive.
            if (tangent[0] < 0)
            {
                tangent = [-tangent[0], -tangent[1]];
            }
        }
        else if (tangent[0] * previousTangent[0] + tangent[1] * previousTangent[1] < 0)
        {
            tangent = [-tangent[0], -tangent[1]];
        }
        const predicted = [uv[0] + stepSize * tangent[0], uv[1] + stepSize * tangent[1]];
        const corrected = correctUvWrapped(strippedMotion, strippedSurface, tGlobal, predicted,
            domain, uPeriodic, tolerance);
        if (corrected[1] <= domain.vMin + margin || corrected[1] >= domain.vMax - margin)
        {
            hitVBoundary = true;
            break;
        }
        uvPoints[pointCount] = corrected;
        pointCount += 1;
        previousTangent = tangent;
        if (step + 1 >= minimumSteps)
        {
            const closure = closureImageShift(corrected, startUv, period, stepSize);
            if (closure.closed)
            {
                uvPoints[pointCount] = [startUv[0] + closure.uTravel, startUv[1]];
                pointCount += 1;
                closed = true;
                uTravel = closure.uTravel;
                break;
            }
        }
        uv = corrected;
    }
    return { "closed" : closed, "uvPoints" : subArray(uvPoints, 0, pointCount), "uTravel" : uTravel,
            "hitVBoundary" : hitVBoundary };
}

/**
 * Resample a marched WRAPPING loop at fixed fractions of its lifted arc length, re-Newton every
 * sample onto f = 0, and lift it. Same contract as resampleClosedLoopSection - fractions are
 * sampleIndex / sampleCount, so the last sample stops one step short of the start and the
 * periodic collocation system stays nonsingular - but every evaluation wraps u, so a sample may
 * legitimately carry an unwrapped u outside the face's domain.
 * Returns { uvSamples, liftedSamples, worstResidual }.
 */
export function resampleWrappingLoopSection(strippedMotion is map, strippedSurface is map, tGlobal is number,
    uvPoints is array, sampleCount is number, tolerance is number) returns map
{
    const domain = fitSurfaceKnotDomain(strippedSurface);
    const uPeriodic = strippedSurface.isUPeriodic == true;
    const pointCount = size(uvPoints);
    var liftedPolyline = makeArray(pointCount);
    for (var index = 0; index < pointCount; index += 1)
    {
        liftedPolyline[index] = wrappedLiftContactPoint(strippedMotion, strippedSurface, uvPoints[index],
            tGlobal, domain, uPeriodic);
    }
    var cumulativeLengths = makeArray(pointCount, 0);
    for (var index = 1; index < pointCount; index += 1)
    {
        cumulativeLengths[index] = cumulativeLengths[index - 1] + norm(liftedPolyline[index] - liftedPolyline[index - 1]);
    }
    const totalLength = cumulativeLengths[pointCount - 1];

    var uvSamples = makeArray(sampleCount);
    var liftedSamples = makeArray(sampleCount);
    var worstResidual = 0;
    var cursor = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        const targetLength = totalLength * sampleIndex / sampleCount;
        while (cursor < pointCount - 2 && cumulativeLengths[cursor + 1] < targetLength)
        {
            cursor += 1;
        }
        const segmentLength = cumulativeLengths[cursor + 1] - cumulativeLengths[cursor];
        const fraction = segmentLength < 1e-300 ? 0 : (targetLength - cumulativeLengths[cursor]) / segmentLength;
        var uv = [uvPoints[cursor][0] + fraction * (uvPoints[cursor + 1][0] - uvPoints[cursor][0]),
            uvPoints[cursor][1] + fraction * (uvPoints[cursor + 1][1] - uvPoints[cursor][1])];
        uv = correctUvWrapped(strippedMotion, strippedSurface, tGlobal, uv, domain, uPeriodic, tolerance);
        worstResidual = max(worstResidual, abs(wrappedEnvelopeGradient(strippedMotion, strippedSurface,
                        uv, tGlobal, domain, uPeriodic).value));
        uvSamples[sampleIndex] = uv;
        liftedSamples[sampleIndex] = wrappedLiftContactPoint(strippedMotion, strippedSurface, uv,
            tGlobal, domain, uPeriodic);
    }
    return { "uvSamples" : uvSamples, "liftedSamples" : liftedSamples, "worstResidual" : worstResidual };
}

/** Whether a marched point has returned to one of the seed's images u0 + k * period, wrap first
    so a genuine tube is never mistaken for a loop that merely passed near its own start. */
function closureImageShift(uv is array, startUv is array, period is number, stepSize is number) returns map
{
    for (var seamShift in (period == 0 ? [0] : [period, 0, -period]))
    {
        if ((uv[0] - startUv[0] - seamShift) ^ 2 + (uv[1] - startUv[1]) ^ 2 <= stepSize ^ 2)
        {
            return { "closed" : true, "uTravel" : seamShift };
        }
    }
    return { "closed" : false, "uTravel" : 0 };
}

/** Newton in v alone at a fixed meridian, held inside [vLow, vHigh]. Returns { converged, v }. */
function polishMeridianCrossing(strippedMotion is map, strippedSurface is map, t is number, uSeed is number,
    seedV is number, vLow is number, vHigh is number, domain is map, uPeriodic is boolean) returns map
{
    var v = clampToRange(seedV, vLow, vHigh);
    var converged = false;
    for (var iteration = 0; iteration < 12; iteration += 1)
    {
        const gradient = wrappedEnvelopeGradient(strippedMotion, strippedSurface, [uSeed, v], t, domain, uPeriodic);
        if (abs(gradient.value) <= 1e-14)
        {
            converged = true;
            break;
        }
        if (abs(gradient.vDerivative) < 1e-30)
        {
            break;
        }
        const stepped = v - gradient.value / gradient.vDerivative;
        if (stepped < vLow || stepped > vHigh)
        {
            break;
        }
        v = stepped;
    }
    return { "converged" : converged, "v" : v };
}

/**
 * Newton corrector onto f(., ., tGlobal) = 0 that leaves u UNWRAPPED: the step is taken along
 * the uv gradient with u free to run past the domain, while v still clamps, because v is a
 * genuine boundary and u on a periodic face is not.
 */
function correctUvWrapped(strippedMotion is map, strippedSurface is map, tGlobal is number, seedUv is array,
    domain is map, uPeriodic is boolean, tolerance is number) returns array
{
    var uv = seedUv;
    for (var iteration = 0; iteration < 8; iteration += 1)
    {
        const gradient = wrappedEnvelopeGradient(strippedMotion, strippedSurface, uv, tGlobal, domain, uPeriodic);
        if (abs(gradient.value) <= tolerance)
        {
            break;
        }
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            break;
        }
        uv = [uv[0] - gradient.value * gradient.uDerivative / gradientNormSquared,
            clampToRange(uv[1] - gradient.value * gradient.vDerivative / gradientNormSquared, domain.vMin, domain.vMax)];
    }
    return uv;
}

/** The envelope gradient at a uv whose u may sit outside a periodic domain. */
function wrappedEnvelopeGradient(strippedMotion is map, strippedSurface is map, uv is array, t is number,
    domain is map, uPeriodic is boolean) returns map
{
    return evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface,
        wrapOrClampU(uv[0], domain, uPeriodic), clampToRange(uv[1], domain.vMin, domain.vMax), t);
}

/** The lifted envelope point at a uv whose u may sit outside a periodic domain. */
function wrappedLiftContactPoint(strippedMotion is map, strippedSurface is map, uv is array, t is number,
    domain is map, uPeriodic is boolean) returns Vector
{
    return liftContactPoint(strippedMotion, strippedSurface,
        wrapOrClampU(uv[0], domain, uPeriodic), clampToRange(uv[1], domain.vMin, domain.vMax), t);
}

/** u brought back into a periodic u domain; a non-periodic u is clamped to it instead. */
function wrapOrClampU(u is number, domain is map, uPeriodic is boolean) returns number
{
    if (!uPeriodic)
    {
        return clampToRange(u, domain.uMin, domain.uMax);
    }
    const period = domain.uMax - domain.uMin;
    const offset = u - domain.uMin;
    const remainder = offset - floor(offset / period) * period;
    return domain.uMin + (remainder >= period || remainder < 0 ? 0 : remainder);
}

// ============================= Units and emission =============================

/** Attach meters to a plain-number fit surface's control points (emission and knot cleanup). */
export function attachFitSurfaceUnits(surface is map) returns map
{
    var result = surface;
    var controlPoints = makeArray(size(surface.controlPoints));
    for (var rowIndex = 0; rowIndex < size(surface.controlPoints); rowIndex += 1)
    {
        var row = makeArray(size(surface.controlPoints[rowIndex]));
        for (var columnIndex = 0; columnIndex < size(surface.controlPoints[rowIndex]); columnIndex += 1)
        {
            row[columnIndex] = surface.controlPoints[rowIndex][columnIndex] * meter;
        }
        controlPoints[rowIndex] = row;
    }
    result.controlPoints = controlPoints;
    return result;
}

/** Strip meters back off a unit-bearing fit surface's control points. */
export function stripFitSurfaceUnits(surface is map) returns map
{
    var result = surface;
    var controlPoints = makeArray(size(surface.controlPoints));
    for (var rowIndex = 0; rowIndex < size(surface.controlPoints); rowIndex += 1)
    {
        var row = makeArray(size(surface.controlPoints[rowIndex]));
        for (var columnIndex = 0; columnIndex < size(surface.controlPoints[rowIndex]); columnIndex += 1)
        {
            row[columnIndex] = surface.controlPoints[rowIndex][columnIndex] / meter;
        }
        controlPoints[rowIndex] = row;
    }
    result.controlPoints = controlPoints;
    return result;
}

/**
 * The kernel-facing BSplineSurface of a unit-bearing, non-rational fit surface (the
 * editSurface.fs emission pattern): a periodic direction is converted to the CLOSED CLAMPED
 * form the kernel returns and provably accepts - handing it this module's wrap-padded form is
 * how you earn a PERIODIC_BSPLINESURFACE_NOT_SMOOTH - the periodic flag stays true through the
 * conversion, and the knot arrays get explicit KnotArray casts because typecheck types do not
 * propagate through array operations.
 */
export function kernelFitSurface(surface is map) returns BSplineSurface
{
    return kernelFitSurface(surface, true);
}

/**
 * Same, with control over whether a periodic direction is DECLARED periodic to the kernel.
 *
 * LIVE FINDING (2026-08-23, two runs): `opCreateBSplineSurface` answers
 * CANNOT_MAKE_BSPLINESURFACE for a whole-island net - BOTH u-boundary rows collapsed to poles
 * and the v direction closed - whether v is declared periodic or converted to closed-clamped
 * and declared non-periodic. The flag is not the problem; such a net has no non-degenerate
 * boundary left to form a face from. Probe 4's accepted case collapsed ONE boundary row of an
 * open patch, which is a different shape. Emitting whole islands is therefore reopened as the
 * spec section 7.1 fallback (split at the t-extremes into two single-pole caps that share their
 * mid-t loop exactly, or SWEEP_ISLAND_UNSUPPORTED); the fit itself is unaffected and certifies.
 *
 * Declaring a periodic direction non-periodic costs nothing geometrically: clamping is knot
 * insertion to full multiplicity at the seam, which reproduces the same curve, so a patch stays
 * C2 across its seam and its two v-edges stay coincident for the knit to sew.
 */
export function kernelFitSurface(surface is map, declarePeriodic is boolean) returns BSplineSurface
{
    var emitted = surface;
    if (emitted.isUPeriodic == true || emitted.isVPeriodic == true)
    {
        // The conversion runs on homogeneous points, so it needs a weights grid even for a
        // non-rational net. Unit weights survive it exactly - knot insertion rows sum to one -
        // so the weights are dropped again below and the emitted surface stays non-rational.
        emitted.weights = unitWeightGrid(size(emitted.controlPoints), size(emitted.controlPoints[0]));
        if (emitted.isUPeriodic == true)
        {
            emitted = toClosedClampedSurfaceDirection(emitted, true);
        }
        if (emitted.isVPeriodic == true)
        {
            emitted = toClosedClampedSurfaceDirection(emitted, false);
        }
    }
    return bSplineSurface({
                "uDegree" : emitted.uDegree,
                "vDegree" : emitted.vDegree,
                "isUPeriodic" : declarePeriodic && emitted.isUPeriodic == true,
                "isVPeriodic" : declarePeriodic && emitted.isVPeriodic == true,
                "controlPoints" : controlPointMatrix(emitted.controlPoints),
                "uKnots" : emitted.uKnots is KnotArray ? emitted.uKnots : knotArray(emitted.uKnots),
                "vKnots" : emitted.vKnots is KnotArray ? emitted.vKnots : knotArray(emitted.vKnots)
            });
}

// ============================= Internal helpers =============================

/** A rowCount x columnCount grid of weights, all one. */
function unitWeightGrid(rowCount is number, columnCount is number) returns array
{
    var weights = makeArray(rowCount);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        weights[rowIndex] = makeArray(columnCount, 1);
    }
    return weights;
}

/** Newton corrector onto f(., ., tGlobal) = 0 along the uv gradient, clamped to the domain. */
function correctUvOntoSection(strippedMotion is map, strippedSurface is map, tGlobal is number,
    seedUv is array, domain is map, tolerance is number) returns array
{
    var uv = seedUv;
    for (var iteration = 0; iteration < 8; iteration += 1)
    {
        const gradient = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, uv[0], uv[1], tGlobal);
        if (abs(gradient.value) <= tolerance)
        {
            break;
        }
        const gradientNormSquared = gradient.uDerivative ^ 2 + gradient.vDerivative ^ 2;
        if (gradientNormSquared < 1e-30)
        {
            break;
        }
        uv = [clampToRange(uv[0] - gradient.value * gradient.uDerivative / gradientNormSquared, domain.uMin, domain.uMax),
            clampToRange(uv[1] - gradient.value * gradient.vDerivative / gradientNormSquared, domain.vMin, domain.vMax)];
    }
    return uv;
}

/** The knot-domain rectangle of a stripped surface. */
function fitSurfaceKnotDomain(surface is map) returns map
{
    return {
            "uMin" : surface.uKnots[surface.uDegree],
            "uMax" : surface.uKnots[size(surface.uKnots) - surface.uDegree - 1],
            "vMin" : surface.vKnots[surface.vDegree],
            "vMax" : surface.vKnots[size(surface.vKnots) - surface.vDegree - 1]
        };
}

// ============================= Self-test fixtures =============================

/** Geometry of the tube fixture: eight fundamental profile points, a [3, 11] u domain of
    period 8, and a total height of 0.1 m over v in [0, 1] - which, z being linear in v, is
    also z prime. */
const TUBE_FIXTURE_PROFILE_COUNT = 8;
const TUBE_FIXTURE_DOMAIN_START = 3;
const TUBE_FIXTURE_HEIGHT = 0.1;

/** The tube self test's certified target. The barrel's contact loop is a cubic B-spline profile
    tilted by the motion, so its q interpolation converges at h^4 off a coarse start: 2.2e-4 at
    q = 14, 2.2e-5 at q = 27, 3.8e-6 at q = 54 (measured independently). 5e-5 is the target the
    refinement loop clears in exactly one doubling, which is what the test wants to exercise. */
const TUBE_SELF_TEST_TOLERANCE = 5e-5;

/**
 * The tube fixture: a closed BARREL, S(u, v) = (g(v) cx(u), g(v) cy(u), z(v)) - a periodic
 * cubic profile c(u) of eight fundamental control points on a 60 x 45 mm ellipse, scaled
 * radially by g(v) = 0.5 + v - v^2 and lifted by z(v) = 0.1 v. A product of a u basis and a v
 * basis is a tensor product, so this is an exact non-rational B-spline surface, u periodic in
 * the wrap-padded convention, and it has NO poles.
 *
 * Its contact curve under a translation is CLOSED FORM. With N = S_u x S_v,
 *     N = g * ( z' cy', -z' cx', g' (cx' cy - cy' cx) ),
 * so for velocity w, f = <N, w> = g * [ z' (cy' wx - cx' wy) + g'(v) A(u) wz ], where
 * A = cx' cy - cy' cx. Since g' = 1 - 2v is LINEAR, f = 0 solves for v outright:
 *     v(u, t) = 0.5 * (1 + z' B(u) / (A(u) wz)),   B = cy' wx - cx' wy.
 * The velocities the tube self test uses hold v inside (0.26, 0.74) across t in [0, 1], so
 * every section is a graph over u: it wraps the seam exactly once and never nears v's boundary.
 */
function tubeFixtureSurface() returns map
{
    const profile = tubeFixtureProfileCurve();
    const radiusScales = [0.5, 1.0, 0.5];
    const heights = [0, 0.05, TUBE_FIXTURE_HEIGHT];
    const rowCount = size(profile.controlPoints);
    var controlPoints = makeArray(rowCount);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        var row = makeArray(3);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            row[columnIndex] = vector(radiusScales[columnIndex] * profile.controlPoints[rowIndex][0],
                radiusScales[columnIndex] * profile.controlPoints[rowIndex][1], heights[columnIndex]);
        }
        controlPoints[rowIndex] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2, "isRational" : false,
            "isUPeriodic" : true, "isVPeriodic" : false,
            "controlPoints" : controlPoints,
            "uKnots" : profile.knots,
            "vKnots" : [0, 0, 0, 1, 1, 1]
        };
}

/**
 * The tube fixture's periodic cubic profile, wrap-padded: eight fundamental control points on a
 * 60 x 45 mm ellipse repeated to n + degree, and n + 2*degree + 1 unit-spaced knots, which puts
 * the domain at [3, 11] with period 8.
 */
function tubeFixtureProfileCurve() returns map
{
    const storedCount = TUBE_FIXTURE_PROFILE_COUNT + 3;
    var controlPoints = makeArray(storedCount);
    for (var index = 0; index < storedCount; index += 1)
    {
        const angle = 360 * degree * (index % TUBE_FIXTURE_PROFILE_COUNT) / TUBE_FIXTURE_PROFILE_COUNT;
        controlPoints[index] = vector(0.06 * cos(angle), 0.045 * sin(angle));
    }
    var knots = makeArray(TUBE_FIXTURE_PROFILE_COUNT + 7);
    for (var index = 0; index < TUBE_FIXTURE_PROFILE_COUNT + 7; index += 1)
    {
        knots[index] = index;
    }
    return { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : controlPoints };
}

/** The tube fixture's closed-form contact v at parameter u (wrapped) and time t. */
function tubeFixtureExactV(strippedMotion is map, u is number, t is number) returns number
{
    const profile = tubeFixtureProfileCurve();
    const wrapped = u - floor((u - TUBE_FIXTURE_DOMAIN_START) / TUBE_FIXTURE_PROFILE_COUNT) *
        TUBE_FIXTURE_PROFILE_COUNT;
    const derivatives = evaluateBSplineCurveDerivatives(profile, wrapped, 1);
    const c = derivatives[0];
    const cPrime = derivatives[1];
    const velocity = evaluateMotionSample(strippedMotion, t).translationDerivative;
    const A = cPrime[0] * c[1] - cPrime[1] * c[0];
    const B = cPrime[1] * velocity[0] - cPrime[0] * velocity[1];
    return 0.5 * (1 + TUBE_FIXTURE_HEIGHT * B / (A * velocity[2]));
}

/**
 * The ROTATING tube fixture (spec 7.9): the same barrel, swept by a motion whose rotation is
 * not identity - rotation about the barrel's own z axis by theta(t) plus a constant world
 * velocity with a component across that axis.
 *
 * Why those two ingredients together. For ANY one-parameter subgroup of the rigid motions the
 * pullback velocity A^T(A' p + b') is independent of t, so the contact set never moves and f_t
 * vanishes identically - a plain helix (constant angular speed along its own axis) is the
 * sliding case in disguise. Two things break the group structure: theta' VARIES with t, which
 * makes the rotation term K vary, and the translation has a component PERPENDICULAR to the
 * rotation axis, so beta = A^T b' turns under A. Measured contact-set travel goes from 5e-4 for
 * the helix to 0.168 here.
 *
 * Why rotation about z stays non-degenerate: K carries the factor d/du (cx^2 + cy^2), which is
 * identically zero on a circle - that IS the sliding case - and vanishes only at the four axis
 * points of the barrel's ELLIPSE.
 *
 * Why the contact curve is still CLOSED FORM. With w = M S + beta, M = A^T A', beta = A^T b',
 * the v-degree of f is set by M's THIRD ROW: the term g' P (M_20 g cx + M_21 g cy) is cubic in
 * v. Holding the z column of A at zhat makes that row exactly zero - zhat . x' = zhat . y' = 0
 * for columns that stay in the xy plane - with no rigidity assumption anywhere. Then, writing
 * z' for the (constant) height derivative and P = cx' cy - cy' cx,
 *
 *     f / g = K g + L + g' P beta_z
 *     K = z' (m00 cy' cx + m01 cy' cy - m10 cx' cx - m11 cx' cy)
 *     L = z' (cy' beta_x - cx' beta_y)
 *
 * and since g = 0.5 + v - v^2 with g' = 1 - 2v, f = 0 is the QUADRATIC
 *
 *     -K v^2 + (K - 2R) v + (0.5 K + L + R) = 0,   R = P beta_z
 *
 * whose K -> 0 limit is exactly the straight-translation form tubeFixtureExactV solves. Note
 * f = <A N, A' S + b'> equals <N, A^T A' S + A^T b'> by transpose alone, needing no
 * orthogonality, so the closed form matches the shipped f whatever the stored rotation's drift.
 *
 * On drift: no non-constant polynomial curve lies in SO(3) - p^2 + q^2 == 1 forces p and q
 * constant by a leading-term argument - so a non-rational B-spline rotation ALWAYS drifts, which
 * is why swMotionSpline certifies orthogonalityDrift instead of assuming exactness. A drifting
 * rotation is therefore the realistic input, not a compromise. Each column here is a cubic
 * Hermite of cos/sin per span over eight spans, stored in Bezier form (interior knot
 * multiplicity 3), which holds the drift near 1.2e-6 - forty times under the fit tolerance - and
 * keeps every motion spline at degree 3, the degree the motion module already produces.
 */
const ROTATING_TUBE_TOTAL_TURN = 25 * PI / 180;   // radians, as a plain number
const ROTATING_TUBE_LINEAR_SHARE = 0.35;          // theta(t) = TURN (share t + (1 - share) t^2)
const ROTATING_TUBE_SPANS = 8;
const ROTATING_TUBE_CROSS_SPEED = 0.04;           // world velocity across the rotation axis
const ROTATING_TUBE_AXIAL_SPEED = 0.15;           // world velocity along it
const ROTATING_TUBE_DRIFT_LIMIT = 3e-6;
const ROTATING_TUBE_TOLERANCE = 1e-4;

/** The rotation angle at t, in radians as a plain number. */
function rotatingTubeTurn(t is number) returns number
{
    return ROTATING_TUBE_TOTAL_TURN *
        (ROTATING_TUBE_LINEAR_SHARE * t + (1 - ROTATING_TUBE_LINEAR_SHARE) * t * t);
}

/** Its t derivative - the angular speed, which is what makes K vary. */
function rotatingTubeTurnRate(t is number) returns number
{
    return ROTATING_TUBE_TOTAL_TURN *
        (ROTATING_TUBE_LINEAR_SHARE + 2 * (1 - ROTATING_TUBE_LINEAR_SHARE) * t);
}

/**
 * The rotating fixture's motion: columnX = (cos theta, sin theta, 0), columnY = (-sin, cos, 0),
 * columnZ = zhat, each a degree-3 Bezier-form spline over ROTATING_TUBE_SPANS spans, and the
 * translation the single-span cubic whose derivative is exactly the world velocity.
 */
function rotatingTubeMotion() returns map
{
    const controlCount = 3 * ROTATING_TUBE_SPANS + 1;
    var xControls = makeArray(controlCount, vector(0, 0, 0));
    var yControls = makeArray(controlCount, vector(0, 0, 0));
    var zControls = makeArray(controlCount, vector(0, 0, 1));
    for (var span = 0; span < ROTATING_TUBE_SPANS; span += 1)
    {
        const tStart = span / ROTATING_TUBE_SPANS;
        const tEnd = (span + 1) / ROTATING_TUBE_SPANS;
        const width = tEnd - tStart;
        const angleStart = rotatingTubeTurn(tStart);
        const angleEnd = rotatingTubeTurn(tEnd);
        const cosStart = cos(angleStart * radian);
        const sinStart = sin(angleStart * radian);
        const cosEnd = cos(angleEnd * radian);
        const sinEnd = sin(angleEnd * radian);
        // Hermite cubic in Bezier form: [f0, f0 + d0/3, f1 - d1/3, f1], with d the derivative
        // with respect to the span-local parameter.
        const rateStart = rotatingTubeTurnRate(tStart) * width;
        const rateEnd = rotatingTubeTurnRate(tEnd) * width;
        const cosSegment = [cosStart, cosStart - sinStart * rateStart / 3,
                cosEnd + sinEnd * rateEnd / 3, cosEnd];
        const sinSegment = [sinStart, sinStart + cosStart * rateStart / 3,
                sinEnd - cosEnd * rateEnd / 3, sinEnd];
        for (var pointIndex = 0; pointIndex < 4; pointIndex += 1)
        {
            xControls[3 * span + pointIndex] = vector(cosSegment[pointIndex], sinSegment[pointIndex], 0);
            yControls[3 * span + pointIndex] = vector(-sinSegment[pointIndex], cosSegment[pointIndex], 0);
        }
    }
    var knots = makeArray(controlCount + 4, 1);
    for (var index = 0; index < 4; index += 1)
    {
        knots[index] = 0;
    }
    for (var span = 1; span < ROTATING_TUBE_SPANS; span += 1)
    {
        for (var repeat = 0; repeat < 3; repeat += 1)
        {
            knots[1 + 3 * span + repeat] = span / ROTATING_TUBE_SPANS;
        }
    }
    const translation = constantVelocityTranslationMotion(
        vector(ROTATING_TUBE_CROSS_SPEED, 0, ROTATING_TUBE_AXIAL_SPEED)).translation;
    return {
            "columnX" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : xControls },
            "columnY" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : yControls },
            "columnZ" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : zControls },
            "translation" : translation
        };
}

/**
 * The pullback quantities the closed form needs, read off the STORED splines rather than an
 * idealised rotation: the four upper-left entries of M = A^T A', the three of beta = A^T b',
 * and M's third row so a test can assert it really is zero.
 */
function rotatingTubePullback(strippedMotion is map, t is number) returns map
{
    const xDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnX, t, 1);
    const yDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnY, t, 1);
    const zDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnZ, t, 1);
    const translationDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.translation, t, 1);
    const velocity = translationDerivatives[1];
    return {
            "m00" : dot(xDerivatives[0], xDerivatives[1]),
            "m01" : dot(xDerivatives[0], yDerivatives[1]),
            "m10" : dot(yDerivatives[0], xDerivatives[1]),
            "m11" : dot(yDerivatives[0], yDerivatives[1]),
            "thirdRow" : [dot(zDerivatives[0], xDerivatives[1]),
                    dot(zDerivatives[0], yDerivatives[1]),
                    dot(zDerivatives[0], zDerivatives[1])],
            "betaX" : dot(xDerivatives[0], velocity),
            "betaY" : dot(yDerivatives[0], velocity),
            "betaZ" : dot(zDerivatives[0], velocity),
            "columns" : [xDerivatives[0], yDerivatives[0], zDerivatives[0]]
        };
}

/**
 * The rotating fixture's closed-form contact v at parameter u (wrapped) and time t: the root of
 * -K v^2 + (K - 2R) v + (0.5 K + L + R) nearer the mid-surface, with the linear branch taken at
 * the four u where K vanishes.
 */
function rotatingTubeExactV(strippedMotion is map, u is number, t is number) returns number
{
    const profile = tubeFixtureProfileCurve();
    const wrapped = u - floor((u - TUBE_FIXTURE_DOMAIN_START) / TUBE_FIXTURE_PROFILE_COUNT) *
        TUBE_FIXTURE_PROFILE_COUNT;
    const derivatives = evaluateBSplineCurveDerivatives(profile, wrapped, 1);
    const c = derivatives[0];
    const cPrime = derivatives[1];
    const pullback = rotatingTubePullback(strippedMotion, t);
    const P = cPrime[0] * c[1] - cPrime[1] * c[0];
    const K = TUBE_FIXTURE_HEIGHT * (pullback.m00 * cPrime[1] * c[0] + pullback.m01 * cPrime[1] * c[1] -
            pullback.m10 * cPrime[0] * c[0] - pullback.m11 * cPrime[0] * c[1]);
    const L = TUBE_FIXTURE_HEIGHT * (cPrime[1] * pullback.betaX - cPrime[0] * pullback.betaY);
    const R = P * pullback.betaZ;
    const quadratic = -K;
    const linear = K - 2 * R;
    const constant = 0.5 * K + L + R;
    // Stable root pair: q = -(b + sign(b) sqrt(disc)) / 2, whose roots are c/q and q/a. Written
    // the textbook way, the root near -c/b loses itself to cancellation at the four u where K
    // vanishes (the ellipse's axis points, where the quadratic coefficient falls to 1e-12 while
    // the linear one holds at 5e-4) - measured worst |f| 5.2e-12 naive against 1.2e-19 here.
    // c/q also covers the K = 0 case outright, so no separate linear branch is needed.
    const root = sqrt(linear * linear - 4 * quadratic * constant);
    const q = -0.5 * (linear + (linear >= 0 ? root : -root));
    if (q == 0)
    {
        return quadratic == 0 ? 0.5 : -linear / quadratic;
    }
    const nearRoot = constant / q;
    if (quadratic == 0)
    {
        return nearRoot;
    }
    const farRoot = q / quadratic;
    return abs(nearRoot - 0.5) <= abs(farRoot - 0.5) ? nearRoot : farRoot;
}

/**
 * The curved fixture: S = (u, v, 0.15u^2 + 0.05uv^2), degrees (2, 2). Under velocity
 * (1, 0, w(t)) the envelope function is f = w(t) - 0.3u - 0.05v^2, so the sections are the
 * moving parabolas u = (w(t) - 0.05v^2) / 0.3.
 */
function curvedFixtureSurface() returns map
{
    const uSquared = [0, 0, 1];
    const uLinear = [0, 0.5, 1];
    const vSquared = [0, 0, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville2[i], greville2[j], 0.15 * uSquared[i] + 0.05 * uLinear[i] * vSquared[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The curved fixture's analytic boundary branches at v = 0 and v = 1, sampled the way strip
 * marching hands branches to the fit: u(t) = (w(t) - 0.05 v^2) / 0.3 with
 * w(t) = 0.06 + 0.18t - 0.18t^2.
 */
function curvedFixtureBranchAnchor(vEdge is number) returns map
{
    const sampleCount = 41;
    var tSamples = makeArray(sampleCount);
    var uvSamples = makeArray(sampleCount);
    for (var index = 0; index < sampleCount; index += 1)
    {
        const t = index / (sampleCount - 1);
        const w = 0.06 + 0.18 * t - 0.18 * t ^ 2;
        tSamples[index] = t;
        uvSamples[index] = [(w - 0.05 * vEdge ^ 2) / 0.3, vEdge];
    }
    return { "anchorKind" : "branch", "tSamples" : tSamples, "uvSamples" : uvSamples };
}

/**
 * Analytic envelope-membership residual for the curved fixture: given a fitted point
 * (x, y, z), the tool preimage satisfies v = y and 0.3(x - t) + 0.05y^2 - w(t) = 0 (monotone
 * in t, Newton), and the residual is the z mismatch against S + the translation
 * W(t) = 0.06t + 0.09t^2 - 0.06t^3.
 */
function worstCurvedFixtureResidual(fitSurface is map, samplesPerDirection is number) returns number
{
    const domain = fitSurfaceKnotDomain(fitSurface);
    var worst = 0;
    for (var i = 1; i < samplesPerDirection; i += 1)
    {
        for (var j = 1; j < samplesPerDirection; j += 1)
        {
            const uu = domain.uMin + (domain.uMax - domain.uMin) * i / samplesPerDirection;
            const vv = domain.vMin + (domain.vMax - domain.vMin) * j / samplesPerDirection;
            const fitted = evaluateBSplineSurfacePoint(fitSurface, uu, vv);
            const y = fitted[1];
            var t = 0.5;
            for (var iteration = 0; iteration < 30; iteration += 1)
            {
                const g = 0.3 * (fitted[0] - t) + 0.05 * y ^ 2 - (0.06 + 0.18 * t - 0.18 * t ^ 2);
                const gPrime = -0.3 - (0.18 - 0.36 * t);
                t = t - g / gPrime;
            }
            const toolU = fitted[0] - t;
            const exactZ = 0.15 * toolU ^ 2 + 0.05 * toolU * y ^ 2 + 0.06 * t + 0.09 * t ^ 2 - 0.06 * t ^ 3;
            worst = max(worst, abs(fitted[2] - exactZ));
        }
    }
    return worst;
}

/**
 * Analytic envelope-membership residual for the island fixture: an envelope point makes the
 * swept family's height mismatch h(u) = z(u, v) + Wz(x - u) - zFitted have a DOUBLE root in
 * u, so min |h| over u must vanish to fit tolerance. z as in islandFixtureSurface;
 * Wz(t) = 0.134t - 0.2t^2 + 0.4t^3/3. Dense scan plus local golden-section refinement.
 */
function islandFixtureMembershipResidual(fitted is Vector) returns number
{
    const y = fitted[1];
    var best = 1e300;
    var bestU = 0;
    const scanCount = 96;
    for (var index = 0; index <= scanCount; index += 1)
    {
        const u = index / scanCount;
        const h = abs(islandFixtureHeightMismatch(u, y, fitted[0], fitted[2]));
        if (h < best)
        {
            best = h;
            bestU = u;
        }
    }
    var low = max(0, bestU - 1 / scanCount);
    var high = min(1, bestU + 1 / scanCount);
    const golden = (sqrt(5) - 1) / 2;
    for (var iteration = 0; iteration < 40; iteration += 1)
    {
        const probeA = high - golden * (high - low);
        const probeB = low + golden * (high - low);
        const valueA = abs(islandFixtureHeightMismatch(probeA, y, fitted[0], fitted[2]));
        const valueB = abs(islandFixtureHeightMismatch(probeB, y, fitted[0], fitted[2]));
        best = min(best, min(valueA, valueB));
        if (valueA < valueB)
        {
            high = probeB;
        }
        else
        {
            low = probeA;
        }
    }
    return best;
}

/** The island fixture's swept-family height mismatch at tool parameter u. */
function islandFixtureHeightMismatch(u is number, v is number, x is number, z is number) returns number
{
    const t = x - u;
    const toolZ = 0.8 * (u ^ 2 / 2 - u ^ 3 / 3) * v * (1 - v);
    const translationZ = 0.134 * t - 0.2 * t ^ 2 + 0.4 * t ^ 3 / 3;
    return toolZ + translationZ - z;
}
