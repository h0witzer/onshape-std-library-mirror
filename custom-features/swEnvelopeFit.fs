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
 *   - Strips: a component whose boundary alternates lateral and cap arcs more than four times
 *     is not one rectangle - a lateral branch turns over in t there, and the section's arc
 *     count changes across that turn. The decomposition cuts every branch at its refined t
 *     extrema, bands the t range at those times, pairs each band's sides into arcs by
 *     marching, and hands neighbouring strips ONE marched seam arc to resample. Merge ends are
 *     MARKED, not remedied: a tangency is a square-root event in t, and clustering stations
 *     there is measured not to improve the strip's certified deviation (spec 7.10).
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
            [1, 1, 1], [0, 0, 0], ISLAND_BUMP_VELOCITY_Z, singleSpanKnots);
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
                    worstIslandResidual = max(worstIslandResidual,
                        islandFixtureMembershipResidual(fitted, ISLAND_BUMP_VELOCITY_Z));
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

// ===================== Island Cap Live Test (spec 7.4 emission) =====================

annotation { "Feature Type Name" : "Sweep Island Cap Live Test" }
export const sweepIslandCapLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = islandFixtureSurface();
        const motion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], ISLAND_CAP_VELOCITY_Z, [0, 0, 0, 0, 1, 1, 1, 1]);
        const poleTime = 0;
        const boundaryTime = ISLAND_PEAK_HEIGHT_SLOPE / ISLAND_CAP_SLOPE;

        // Born at t = 0 at the bump's peak, CLIPPED at t = 0.0375 - one pole and one genuine
        // loop boundary. The island's far end is not a pole: at t = 0.125 the level set
        // degenerates onto the patch boundary instead, so nothing past the clip is fitted.
        const fit = fitIslandComponent(motion, surface, {
                    "birth" : { "u" : 0.5, "v" : 0.5, "t" : poleTime },
                    "death" : { "u" : 0.5, "v" : 0.5, "t" : boundaryTime },
                    "tEnd" : ISLAND_CAP_T_END,
                    "tolerance" : ISLAND_CAP_TOLERANCE,
                    "initialQCount" : 8, "initialStationCount" : 8,
                    "maxQCount" : 15, "maxStationCount" : 15, "maxRefinementRounds" : 1
                });
        tally = checkThat(tally, !fit.failed, "the clipped island fit failed: " ~ fit.reason ~ ".");
        if (!fit.failed)
        {
            println("[ISLAND CAP LIVE TEST] fit: " ~ fit.stationCount ~ "x" ~ fit.qCount ~
                " grid in " ~ fit.refinementRounds ~ " round(s), deviation " ~ fit.worstDeviation ~
                " (q " ~ fit.worstQDeviation ~ ", t " ~ fit.worstTDeviation ~ "), poles " ~
                fit.poleAtStart ~ "/" ~ fit.poleAtEnd ~ ", budgetHit " ~ fit.budgetHit);
            tally = checkThat(tally, !fit.budgetHit && fit.worstDeviation <= ISLAND_CAP_TOLERANCE,
                "the cap fit missed " ~ ISLAND_CAP_TOLERANCE ~ " (deviation " ~ fit.worstDeviation ~ ").");
            tally = checkThat(tally, fit.poleAtStart && !fit.poleAtEnd,
                "the clip produced poles " ~ fit.poleAtStart ~ "/" ~ fit.poleAtEnd ~
                " instead of one at the start only.");

            // ONE collapsed row, so the orientation certificate skips one row and no more.
            const expectedSamples = (fit.stationCount - 1) * fit.qCount;
            println("[ISLAND CAP LIVE TEST] orientation: " ~ fit.orientation.sampleCount ~
                " samples (one pole row skipped, expected " ~ expectedSamples ~ "), lambda sign " ~
                fit.orientation.lambdaSign ~ " consistent " ~ fit.orientation.lambdaSignConsistent ~
                ", worst fold margin " ~ fit.orientation.worstFoldMargin ~ ", stationary " ~
                fit.orientation.stationarySamples ~ ", degenerate " ~ fit.orientation.degenerateSamples ~
                ", difference " ~ fit.orientation.differenceAgreements ~ "/" ~
                fit.orientation.differenceChecked ~ " agree, q reversed " ~ fit.qReversed ~
                ", consistent " ~ fit.orientation.consistent);
            tally = checkThat(tally, fit.orientation.sampleCount == expectedSamples,
                "the cap's single collapsed pole row was not skipped exactly once (" ~
                fit.orientation.sampleCount ~ " samples against " ~ expectedSamples ~ ").");

            // The fold certificate, the whole point of clipping: lambda = wz' + z_uu with
            // |wz'| = 0.4 against max |z_uu| = 0.1095 on the clip loop, so lambda holds -1 with
            // a fold margin of 0.570 - and f_t is a genuine 0.4, not the stationary case.
            tally = checkThat(tally, fit.orientation.lambdaSignConsistent && fit.orientation.consistent,
                "the fold certificate did not clear on the clipped cap (lambda sign " ~
                fit.orientation.lambdaSign ~ ", consistent " ~ fit.orientation.consistent ~ ").");
            tally = checkThat(tally, fit.orientation.lambdaSign == -1,
                "lambda came out " ~ fit.orientation.lambdaSign ~ " where wz' = -0.4 dominates z_uu.");
            tally = checkThat(tally, fit.orientation.worstFoldMargin > 0.5,
                "the worst fold margin is " ~ fit.orientation.worstFoldMargin ~ ", under the 0.570 " ~
                "the closed form predicts.");
            tally = checkThat(tally, !fit.orientation.contactStationary,
                "the contact set reported stationary, but this motion accelerates in z.");

            // Pole closure at the collapsed end, and a genuine loop at the clipped end.
            const fitDomain = fitSurfaceKnotDomain(fit.surface);
            var worstPoleError = 0;
            var loopSpan = 0;
            for (var vFraction in [0, 0.31, 0.5, 0.77, 1])
            {
                const vv = fitDomain.vMin + (fitDomain.vMax - fitDomain.vMin) * vFraction;
                worstPoleError = max(worstPoleError,
                    norm(evaluateBSplineSurfacePoint(fit.surface, fitDomain.uMin, vv) - fit.poleStartPoint));
                loopSpan = max(loopSpan,
                    norm(evaluateBSplineSurfacePoint(fit.surface, fitDomain.uMax, vv) -
                            evaluateBSplineSurfacePoint(fit.surface, fitDomain.uMax, fitDomain.vMin)));
            }
            println("[ISLAND CAP LIVE TEST] pole closure error " ~ worstPoleError ~
                ", clipped-end loop span " ~ loopSpan ~ " m");
            tally = checkWithin(tally, worstPoleError, 1e-12, "the cap's pole row closure");
            tally = checkThat(tally, loopSpan > 0.1,
                "the clipped end collapsed too (span " ~ loopSpan ~ " m), so this is not a one-pole cap.");

            // EMISSION - the question this fixture exists to answer. A net with ONE collapsed
            // boundary row and a closed v direction has never been handed to the kernel; spec
            // 7.4 only established that BOTH rows collapsed is refused.
            const emission = emitIslandPatches(context, id, fit, {});
            println("[ISLAND CAP LIVE TEST] emission: refused " ~ emission.refused ~ ", shape " ~
                emission.shape ~ ", poles " ~ emission.poleCount ~ ", patches " ~
                emission.patchCount ~ ", faces " ~ emission.faceCount ~ ", declared periodic " ~
                toString(emission.declaredPeriodic));
            tally = checkThat(tally, !emission.refused,
                "the fold-free cap was refused: " ~ (emission.reason == undefined ? "" : emission.reason));
            tally = checkThat(tally, emission.faceCount == 1,
                "the one-pole cap emitted " ~ emission.faceCount ~ " faces instead of one.");

            if (emission.faceCount == 1)
            {
                // Kernel certification against fresh envelope loops at t values that are
                // neither stations (multiples of 0.0375/7) nor the midpoints the fit already
                // certified against - in BOTH held-out families, which the first live run
                // showed is not a luxury: the loops' own arc-length fractions come back at
                // exactly 0 m, because this fit's t direction is right to 9e-7 and the
                // evaluator does not resolve that. The q MIDPOINTS carry the whole of this
                // fit's error, so they are the number the kernel has to agree with.
                var freshRowPoints = [];
                var freshMidPoints = [];
                for (var freshT in [0.013, 0.031])
                {
                    const freshLoop = islandLoopSamples(motion, surface, [0.5, 0.5], freshT, 8, {});
                    if (freshLoop.failed)
                    {
                        tally = checkThat(tally, false, "fresh loop at t = " ~ freshT ~ " failed.");
                        continue;
                    }
                    for (var point in freshLoop.liftedRow)
                    {
                        freshRowPoints = append(freshRowPoints, point * meter);
                    }
                    for (var point in freshLoop.midLifted)
                    {
                        freshMidPoints = append(freshMidPoints, point * meter);
                    }
                }
                if (size(freshMidPoints) > 0)
                {
                    const patchFaces = qCreatedBy(emission.patchIds[0], EntityType.FACE);
                    const rowDeviation = evPointsDeviation(context, {
                                    "points" : freshRowPoints, "topologies" : patchFaces
                                })[0].deviation / meter;
                    const midDeviation = evPointsDeviation(context, {
                                    "points" : freshMidPoints, "topologies" : patchFaces
                                })[0].deviation / meter;
                    println("[ISLAND CAP LIVE TEST] kernel deviation vs fresh envelope loops: " ~
                        rowDeviation ~ " m at the loops' own q fractions, " ~ midDeviation ~
                        " m at their q midpoints (the fit certifies " ~ fit.worstDeviation ~ ")");
                    tally = checkWithin(tally, midDeviation, 1e-3,
                        "the emitted cap's deviation from fresh envelope points");
                    tally = checkThat(tally, midDeviation <= 2 * fit.certifiedBound + 1e-9,
                        "the kernel measures " ~ midDeviation ~ " m against a certified bound of " ~
                        fit.certifiedBound ~ " m, so the emitted face is not the surface that " ~
                        "was certified.");
                }

                // And an independent analytic check: an envelope point is where the swept
                // family's height function has a double root in u.
                var worstMembership = 0;
                for (var i = 1; i <= 3; i += 1)
                {
                    for (var j = 0; j <= 2; j += 1)
                    {
                        const uu = fitDomain.uMin + (fitDomain.uMax - fitDomain.uMin) * i / 4;
                        const vv = fitDomain.vMin + (fitDomain.vMax - fitDomain.vMin) * j / 2;
                        worstMembership = max(worstMembership, islandFixtureMembershipResidual(
                                evaluateBSplineSurfacePoint(fit.surface, uu, vv), ISLAND_CAP_VELOCITY_Z));
                    }
                }
                println("[ISLAND CAP LIVE TEST] analytic envelope membership: " ~ worstMembership);
                tally = checkWithin(tally, worstMembership, 1e-4, "the cap's analytic membership residual");

                // THE SPLIT SHAPE, exercised here rather than on the two-pole island: that
                // island folds by construction (see emitIslandPatches), and the kernel refuses
                // its halves in both declarations for reasons that have nothing to do with the
                // split. Cutting this FOLD-FREE cap at a mid station produces exactly the two
                // shapes the two-pole route would need - a 4-row one-pole cap and a 5-row
                // pole-free tube - meeting on one shared loop row.
                const split = splitIslandFitGrid(fit);
                tally = checkThat(tally, !split.failed,
                    "the cap split failed: " ~ (split.reason == undefined ? "" : split.reason));
                if (!split.failed)
                {
                    const startPatch = emitFitSurfacePatch(context, id + "splitStart", split.startSurface);
                    const endPatch = emitFitSurfacePatch(context, id + "splitEnd", split.endSurface);
                    println("[ISLAND CAP LIVE TEST] split at station " ~ split.cut ~ " of " ~
                        (fit.stationCount - 1) ~ ": nets " ~ size(split.startSurface.controlPoints) ~
                        "x" ~ size(split.startSurface.controlPoints[0]) ~ " and " ~
                        size(split.endSurface.controlPoints) ~ "x" ~
                        size(split.endSurface.controlPoints[0]) ~ ", faces " ~
                        startPatch.faceCount ~ " + " ~ endPatch.faceCount ~ ", periodic " ~
                        startPatch.declaredPeriodic ~ "/" ~ endPatch.declaredPeriodic ~
                        (startPatch.refused ? (", start refused: " ~ startPatch.reason) : "") ~
                        (endPatch.refused ? (", end refused: " ~ endPatch.reason) : ""));
                    tally = checkThat(tally, startPatch.faceCount == 1 && endPatch.faceCount == 1,
                        "the fold-free split emitted " ~ startPatch.faceCount ~ " and " ~
                        endPatch.faceCount ~ " faces instead of one each.");

                    // The shared row has to be ONE curve, not two through the same points.
                    const naiveStart = interpolateFitGrid(subArray(fit.liftedGrid, 0, split.cut + 1), 3, 3, true);
                    const naiveEnd = interpolateFitGrid(subArray(fit.liftedGrid, split.cut, fit.stationCount), 3, 3, true);
                    const naiveBoundary = naiveStart.controlPoints[size(naiveStart.controlPoints) - 1];
                    var naiveGap = 0;
                    for (var index = 0; index < size(naiveBoundary); index += 1)
                    {
                        naiveGap = max(naiveGap, norm(naiveBoundary[index] - naiveEnd.controlPoints[0][index]));
                    }
                    println("[ISLAND CAP LIVE TEST] shared control row gap: " ~ split.seamError ~
                        " m with the whole grid's v parameters, " ~ naiveGap ~ " m with each half's own");
                    tally = checkWithin(tally, split.seamError, 1e-15, "the two caps' shared control row gap");
                    tally = checkThat(tally, naiveGap > 1e-9,
                        "each half's own v parameters gave a gap of only " ~ naiveGap ~ " m, so this " ~
                        "fixture no longer shows why the parameters must be prescribed.");

                    if (startPatch.faceCount == 1 && endPatch.faceCount == 1)
                    {
                        var sharedPoints = [];
                        for (var point in split.sharedRow)
                        {
                            sharedPoints = append(sharedPoints, point * meter);
                        }
                        for (var patch in [startPatch, endPatch])
                        {
                            const deviation = evPointsDeviation(context, {
                                            "points" : sharedPoints,
                                            "topologies" : qCreatedBy(patch.id, EntityType.FACE)
                                        })[0].deviation / meter;
                            println("[ISLAND CAP LIVE TEST] shared row deviation from the " ~
                                (patch.declaredPeriodic ? "periodic" : "clamped") ~ " patch: " ~
                                deviation ~ " m");
                            tally = checkWithin(tally, deviation, 1e-9,
                                "a split cap's deviation from the shared loop row");
                        }
                    }
                }
            }
        }

        reportCheckTally(context, id, "ISLAND CAP LIVE TEST", tally,
            "a clipped island fits fold-free, closes exactly on its single pole, and emits ONE " ~
            "kernel face that certifies against fresh envelope loops.");
    });

// ===================== Island Split Live Test (spec 7.4 emission shape) =====================

annotation { "Feature Type Name" : "Sweep Island Split Live Test" }
export const sweepIslandSplitLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = islandFixtureSurface();
        const motion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], ISLAND_BUMP_VELOCITY_Z, [0, 0, 0, 0, 1, 1, 1, 1]);
        const birth = { "u" : 0.5, "v" : 0.5, "t" : 0.3 };
        const death = { "u" : 0.5, "v" : 0.5, "t" : 0.7 };

        // WHY a two-pole island always folds, measured rather than argued: at a t-extreme the
        // contact set is a single point where f_u = f_v = 0, so lambda IS f_t there. The loop
        // shrinks to a point at both ends with the same sign of f inside it, which forces the
        // same Hessian definiteness at both - and that forces OPPOSITE f_t signs. Here
        // f_t = wz' = 0.8t - 0.4, so -0.16 at the birth and +0.16 at the death.
        var worstPoleLambdaGap = 0;
        var poleTimeDerivatives = [];
        for (var pole in [birth, death])
        {
            const sample = envelopeOrientationSample(motion, surface, pole.u, pole.v, pole.t);
            worstPoleLambdaGap = max(worstPoleLambdaGap, abs(sample.lambda - sample.tDerivative));
            poleTimeDerivatives = append(poleTimeDerivatives, sample.tDerivative);
        }
        println("[ISLAND SPLIT LIVE TEST] at the poles: f_t = " ~ toString(poleTimeDerivatives) ~
            ", worst |lambda - f_t| = " ~ worstPoleLambdaGap);
        tally = checkWithin(tally, worstPoleLambdaGap, 1e-15, "|lambda - f_t| at the island's poles");
        tally = checkThat(tally, poleTimeDerivatives[0] * poleTimeDerivatives[1] < 0,
            "the two poles' f_t signs agree (" ~ toString(poleTimeDerivatives) ~ "), which would make a " ~
            "fold-free two-pole island possible after all.");

        const fit = fitIslandComponent(motion, surface, {
                    "birth" : birth, "death" : death,
                    "tolerance" : 5e-4,
                    "initialQCount" : 8, "initialStationCount" : 8,
                    "maxQCount" : 15, "maxStationCount" : 15, "maxRefinementRounds" : 1
                });
        tally = checkThat(tally, !fit.failed, "the two-pole island fit failed: " ~ fit.reason ~ ".");
        if (!fit.failed)
        {
            println("[ISLAND SPLIT LIVE TEST] fit: " ~ fit.stationCount ~ "x" ~ fit.qCount ~
                " grid, deviation " ~ fit.worstDeviation ~ ", poles " ~ fit.poleAtStart ~ "/" ~
                fit.poleAtEnd ~ ", lambda consistent " ~ fit.orientation.lambdaSignConsistent);
            tally = checkThat(tally, fit.poleAtStart && fit.poleAtEnd,
                "the unclipped island did not come out with two poles.");
            tally = checkThat(tally, !fit.orientation.lambdaSignConsistent,
                "the fold certificate did not fire on the two-pole island, whose lambda provably " ~
                "spans both signs across t in (0.385, 0.615).");

            // v1 policy: a folded component is reported, never emitted.
            const refusal = emitIslandPatches(context, id + "refused", fit, {});
            println("[ISLAND SPLIT LIVE TEST] default emission: refused " ~ refusal.refused ~
                ", faces " ~ refusal.faceCount);
            tally = checkThat(tally, refusal.refused && refusal.faceCount == 0,
                "a folded island was emitted instead of reported.");

            // The emission SHAPE, exercised with the fold gate lifted: two single-pole caps
            // sharing one row. The shared control row must be identical coordinate for
            // coordinate - not merely close - which is what the prescribed v parameters buy.
            const split = splitIslandFitGrid(fit);
            tally = checkThat(tally, !split.failed,
                "the island split failed: " ~ (split.reason == undefined ? "" : split.reason));
            if (!split.failed)
            {
                println("[ISLAND SPLIT LIVE TEST] split at station " ~ split.cut ~ " of " ~
                    (fit.stationCount - 1) ~ ": nets " ~ size(split.startSurface.controlPoints) ~ "x" ~
                    size(split.startSurface.controlPoints[0]) ~ " and " ~
                    size(split.endSurface.controlPoints) ~ "x" ~
                    size(split.endSurface.controlPoints[0]));
                // The split's own arithmetic still holds on a folded island - it is grid
                // algebra, not geometry - so the shared row is exact here too, against each
                // half averaging its OWN v parameters. Same eight points, two
                // parameterizations, two curves: that gap is what prescribing them removes.
                const naiveStart = interpolateFitGrid(subArray(fit.liftedGrid, 0, split.cut + 1), 3, 3, true);
                const naiveEnd = interpolateFitGrid(subArray(fit.liftedGrid, split.cut, fit.stationCount), 3, 3, true);
                const naiveBoundary = naiveStart.controlPoints[size(naiveStart.controlPoints) - 1];
                var naiveGap = 0;
                for (var index = 0; index < size(naiveBoundary); index += 1)
                {
                    naiveGap = max(naiveGap, norm(naiveBoundary[index] - naiveEnd.controlPoints[0][index]));
                }
                println("[ISLAND SPLIT LIVE TEST] shared control row gap: " ~ split.seamError ~
                    " m with the whole grid's v parameters, " ~ naiveGap ~ " m with each half's own");
                tally = checkWithin(tally, split.seamError, 1e-15,
                    "the two caps' shared control row gap");
                tally = checkThat(tally, naiveGap > 1e-9,
                    "each half's own v parameters gave a gap of only " ~ naiveGap ~ " m, so this " ~
                    "fixture no longer shows why the parameters must be prescribed.");

                // What handing the folded halves to the kernel anyway does was measured on
                // 2026-08-23 with `requireFoldFree` false: it answers
                // CANNOT_MAKE_BSPLINESURFACE for BOTH halves in BOTH declarations - four
                // refusals, all caught, nothing thrown. That is the second and independent
                // reason a two-pole island is never emitted, and it is not re-run here on
                // purpose: a caught kernel notice suppresses the console output this test
                // reports through, so the run that proves it cannot also print its verdict.
                // The split SHAPE is exercised where it can be - on the fold-free clipped cap,
                // in the island cap live test, where both halves come back as one face each.
            }
        }

        reportCheckTally(context, id, "ISLAND SPLIT LIVE TEST", tally,
            "a two-pole island's poles carry opposite f_t, so lambda cannot hold one sign across " ~
            "it; the fold certificate fires, v1 reports instead of emitting, and the split's " ~
            "shared row is exact even where the kernel will not take the halves.");
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

// ===================== Strip Decomposition Self Test =====================

annotation { "Feature Type Name" : "Sweep Strip Decomposition Self Test" }
export const sweepStripDecompositionSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = mergeFixtureSurface();
        const motion = mergeFixtureMotion();
        const branches = [mergeFixtureCapBranch(0), mergeFixtureCapBranch(1), mergeFixtureWallBranch()];

        // The fixture's own branch samples have to be on f = 0 before anything reads them as a
        // boundary: an anchor is only as exact as the array it comes out of.
        var worstBranchResidual = 0;
        for (var branch in branches)
        {
            for (var index = 0; index < size(branch.tSamples); index += 1)
            {
                worstBranchResidual = max(worstBranchResidual, abs(evaluateEnvelopePointwise(motion, surface,
                                branch.uvSamples[index][0], branch.uvSamples[index][1], branch.tSamples[index])));
            }
        }
        println("[STRIP DECOMPOSITION SELF TEST] fixture branch samples worst |f|: " ~ worstBranchResidual);
        tally = checkWithin(tally, worstBranchResidual, 1e-15, "the fixture branch samples' worst |f|");

        const decomposition = decomposeFunnelComponentIntoStrips(motion, surface, {
                    "tStart" : 0, "tEnd" : 1, "branches" : branches, "sectionTolerance" : 1e-13
                });
        if (decomposition.failed)
        {
            tally = checkThat(tally, false, "the merge component's decomposition failed: " ~ decomposition.reason);
        }
        else
        {
            const alternations = decomposition.alternations;
            println("[STRIP DECOMPOSITION SELF TEST] alternations " ~ alternations.alternationCount ~
                " (" ~ alternations.endpointsAtStart ~ " endpoints at t0, " ~ alternations.endpointsAtEnd ~
                " at t1; cap arcs " ~ alternations.capArcsAtStart ~ "/" ~ alternations.capArcsAtEnd ~
                "), sides " ~ size(decomposition.sides) ~ ", split times " ~ toString(decomposition.splitTimes) ~
                ", bands " ~ size(decomposition.bands) ~ ", strips " ~ size(decomposition.strips) ~
                ", seams " ~ size(decomposition.seams));

            tally = checkThat(tally, alternations.alternationCount == 6,
                "the merge component's boundary alternates " ~ alternations.alternationCount ~
                " times, not the 6 its two-arc start and one-arc end make.");
            tally = checkThat(tally, alternations.endpointsAtStart == 4 && alternations.endpointsAtEnd == 2,
                "the cap endpoint counts are " ~ alternations.endpointsAtStart ~ "/" ~
                alternations.endpointsAtEnd ~ ", not 4/2.");
            tally = checkThat(tally, alternations.exceedsRectangle,
                "a 6-alternation component was not reported as exceeding a rectangle.");
            tally = checkThat(tally, size(decomposition.sides) == 4,
                "the three branches cut into " ~ size(decomposition.sides) ~ " sides, not 4.");
            tally = checkThat(tally, size(decomposition.splitTimes) == 1,
                "the decomposition found " ~ size(decomposition.splitTimes) ~ " interior cut times, not 1.");

            // ---------- The refined merge time ----------
            // The wall branch's t is exactly SPLIT_TIME (1 - s^2), so its maximum is the fixture
            // constant. The golden section runs on sigma, where t is quadratic, so the answer is
            // expected at machine precision even though sigma itself converges to only ~1e-9.
            if (size(decomposition.splitTimes) == 1)
            {
                println("[STRIP DECOMPOSITION SELF TEST] refined merge time: " ~ decomposition.splitTimes[0] ~
                    " (exact " ~ MERGE_FIXTURE_SPLIT_TIME ~ ")");
                tally = checkWithin(tally, decomposition.splitTimes[0] - MERGE_FIXTURE_SPLIT_TIME, 1e-12,
                    "the refined merge time's error against the fixture's closed form");
            }

            // ---------- The cut sample is ONE sample, shared ----------
            var wallSides = [];
            var capSides = [];
            for (var side in decomposition.sides)
            {
                if (side.branchIndex == 2)
                {
                    wallSides = append(wallSides, side);
                }
                else if (side.branchIndex == 0)
                {
                    capSides = append(capSides, side);
                }
            }
            tally = checkThat(tally, size(wallSides) == 2,
                "the turning wall branch cut into " ~ size(wallSides) ~ " sides, not 2.");
            tally = checkThat(tally, size(capSides) == 1,
                "the monotone v = 0 branch was cut into " ~ size(capSides) ~ " sides instead of being left alone.");
            if (size(capSides) == 1)
            {
                tally = checkThat(tally, uvSampleArraysIdentical(capSides[0].uvSamples, branches[0].uvSamples),
                    "a monotone branch's uv samples were not carried through the split untouched.");
            }
            if (size(wallSides) == 2)
            {
                const lower = wallSides[0];
                const upper = wallSides[1];
                const cutIndex = size(lower.tSamples) - 1;
                const cutTime = lower.tSamples[cutIndex];
                tally = checkThat(tally,
                    cutTime == upper.tSamples[0] &&
                    lower.uvSamples[cutIndex][0] == upper.uvSamples[0][0] &&
                    lower.uvSamples[cutIndex][1] == upper.uvSamples[0][1],
                    "the two sides of the cut do not carry the SAME cut sample - they only agree numerically.");
                // And the anchors read out of them at the cut time have to be that sample, bit for
                // bit, or the two strips' shared corner is two points.
                const anchorBelow = anchorUvAtStation(lower, motion, surface, cutTime, 1e-13);
                const anchorAbove = anchorUvAtStation(upper, motion, surface, cutTime, 1e-13);
                tally = checkThat(tally,
                    anchorBelow[0] == anchorAbove[0] && anchorBelow[1] == anchorAbove[1] &&
                    anchorBelow[0] == lower.uvSamples[cutIndex][0] &&
                    anchorBelow[1] == lower.uvSamples[cutIndex][1],
                    "the two sides' anchors at the cut time are not the shared cut sample itself " ~
                    "(" ~ toString(anchorBelow) ~ " against " ~ toString(anchorAbove) ~ ").");
                println("[STRIP DECOMPOSITION SELF TEST] shared cut sample: t " ~ cutTime ~ ", uv " ~
                    toString(anchorBelow) ~ ", sides " ~ size(lower.tSamples) ~ " + " ~ size(upper.tSamples) ~
                    " samples of the branch's 20");
            }

            // ---------- Bands, strips and the pairing ----------
            tally = checkThat(tally, size(decomposition.bands) == 2,
                "the merge component made " ~ size(decomposition.bands) ~ " bands, not 2.");
            tally = checkThat(tally, size(decomposition.strips) == 3,
                "the merge component made " ~ size(decomposition.strips) ~ " strips, not the two legs " ~
                "and one trunk its shape calls for.");
            if (size(decomposition.strips) == 3 && size(decomposition.splitTimes) == 1)
            {
                const splitTime = decomposition.splitTimes[0];
                var legCount = 0;
                var trunkCount = 0;
                var pairingCorrect = true;
                var rangesExact = true;
                for (var strip in decomposition.strips)
                {
                    const startBranch = strip.startAnchor.branchIndex;
                    const endBranch = strip.endAnchor.branchIndex;
                    if (strip.bandIndex == 0)
                    {
                        legCount += 1;
                        rangesExact = rangesExact && strip.tStart == 0 && strip.tEnd == splitTime;
                        // A leg runs from one cap wall to the turning wall branch, and its wall
                        // side must be the one on its own side of v = 1/2.
                        const wallIsEnd = endBranch == 2;
                        const exactlyOneWall = wallIsEnd ? startBranch != 2 : startBranch == 2;
                        const capBranch = wallIsEnd ? startBranch : endBranch;
                        const wallSide = wallIsEnd ? strip.endAnchor : strip.startAnchor;
                        const wallV = anchorUvAtStation(wallSide, motion, surface, 0.5 * splitTime, 1e-13)[1];
                        pairingCorrect = pairingCorrect && exactlyOneWall &&
                            ((capBranch == 0) == (wallV < 0.5));
                    }
                    else
                    {
                        trunkCount += 1;
                        rangesExact = rangesExact && strip.tStart == splitTime && strip.tEnd == 1;
                        pairingCorrect = pairingCorrect && startBranch + endBranch == 1;
                    }
                }
                tally = checkThat(tally, legCount == 2 && trunkCount == 1,
                    "the strips split " ~ legCount ~ "/" ~ trunkCount ~ " across the two bands, not 2/1.");
                tally = checkThat(tally, rangesExact,
                    "a strip's t range does not match its band's bounds exactly - a seam station " ~
                    "that does not compare equal cannot share arrays.");
                tally = checkThat(tally, pairingCorrect,
                    "the marched pairing did not join each cap wall to the wall-branch side on its " ~
                    "own side of v = 1/2.");
            }

            // ---------- The seam: one marched arc, resampled by all three strips ----------
            tally = checkThat(tally, size(decomposition.seams) == 1,
                "the decomposition reported " ~ size(decomposition.seams) ~ " seams, not 1.");
            if (size(decomposition.seams) == 1 && size(decomposition.strips) == 3)
            {
                const seam = decomposition.seams[0];
                const coarse = decomposition.strips[seam.coarseStripIndex];
                println("[STRIP DECOMPOSITION SELF TEST] seam at t " ~ seam.t ~ ": coarse strip " ~
                    seam.coarseStripIndex ~ " (band " ~ coarse.bandIndex ~ "), fine strips " ~
                    toString(seam.fineStripIndices) ~ ", shared polyline " ~ seam.polylinePointCount ~ " points");
                tally = checkThat(tally, coarse.bandIndex == 1,
                    "the seam's merged side is band " ~ coarse.bandIndex ~ " - it must be the " ~
                    "single-arc band, which is the one that is smooth across the merge.");
                tally = checkThat(tally, size(seam.fineStripIndices) == 2,
                    "the seam attached " ~ size(seam.fineStripIndices) ~ " fine strips to its shared arc, not 2.");
                tally = checkThat(tally, coarse.startSectionPolyline != undefined &&
                    coarse.endSectionPolyline == undefined &&
                    coarse.mergeAtStart == false && coarse.mergeAtEnd == false,
                    "the merged strip should carry the shared polyline at its START and no merge " ~
                    "mark (it is the smooth side of the merge).");

                if (size(seam.fineStripIndices) == 2 && coarse.startSectionPolyline != undefined)
                {
                    const shared = coarse.startSectionPolyline;
                    var interiorShared = 0;
                    var sharingExact = true;
                    var gradingCorrect = true;
                    for (var fineStripIndex in seam.fineStripIndices)
                    {
                        const fine = decomposition.strips[fineStripIndex];
                        gradingCorrect = gradingCorrect && fine.mergeAtEnd == true &&
                            fine.mergeAtStart == false && fine.gradeEnd == false;
                        sharingExact = sharingExact && fine.endSectionPolyline != undefined &&
                            uvSampleArraysIdentical(fine.endSectionPolyline, shared);
                        const clipped = clipSectionPolylineToAnchors(shared,
                            anchorUvAtStation(fine.startAnchor, motion, surface, seam.t, 1e-13),
                            anchorUvAtStation(fine.endAnchor, motion, surface, seam.t, 1e-13));
                        if (clipped.failed)
                        {
                            sharingExact = false;
                        }
                        else
                        {
                            const run = sharedPolylineRunLength(shared, clipped.uvPoints);
                            sharingExact = sharingExact && run >= 0;
                            interiorShared += max(0, run);
                        }
                    }
                    println("[STRIP DECOMPOSITION SELF TEST] shared interior vertices used by the two " ~
                        "legs: " ~ interiorShared ~ " of the arc's " ~ (size(shared) - 2));
                    tally = checkThat(tally, sharingExact,
                        "a leg strip's seam row is not the merged strip's own polyline, vertex for vertex.");
                    // The two clipped runs partition the shared arc's interior vertices. One short
                    // is also correct: when the junction anchor's closest point on the arc lands
                    // exactly ON a vertex, that vertex retires into the anchor and belongs to
                    // neither run.
                    tally = checkThat(tally, interiorShared >= size(shared) - 3 &&
                        interiorShared <= size(shared) - 2,
                        "the two legs' clipped seam rows cover " ~ interiorShared ~ " of the shared " ~
                        "arc's " ~ (size(shared) - 2) ~ " interior vertices - they must partition it.");
                    tally = checkThat(tally, gradingCorrect,
                        "the merging (fine) strips did not have their merge end MARKED, or picked " ~
                        "up station grading the decomposition does not turn on by default.");
                }
            }
        }

        // ---------- Control: a genuine rectangle must NOT decompose ----------
        // The curved fixture's two boundary branches are monotone in t by construction, so this
        // is the case the >4-alternation test has to stay silent on.
        const curvedDecomposition = decomposeFunnelComponentIntoStrips(
            translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0], [0.06, 0.15, 0.06]),
            curvedFixtureSurface(), {
                    "tStart" : 0, "tEnd" : 1, "sectionTolerance" : 1e-13,
                    "branches" : [curvedFixtureBranchAnchor(0), curvedFixtureBranchAnchor(1)]
                });
        if (curvedDecomposition.failed)
        {
            tally = checkThat(tally, false, "the rectangle control decomposition failed: " ~
                curvedDecomposition.reason);
        }
        else
        {
            println("[STRIP DECOMPOSITION SELF TEST] rectangle control: alternations " ~
                curvedDecomposition.alternations.alternationCount ~ ", sides " ~
                size(curvedDecomposition.sides) ~ ", strips " ~ size(curvedDecomposition.strips) ~
                ", seams " ~ size(curvedDecomposition.seams));
            tally = checkThat(tally, curvedDecomposition.alternations.alternationCount == 4 &&
                !curvedDecomposition.alternations.exceedsRectangle,
                "the rectangle control reported " ~ curvedDecomposition.alternations.alternationCount ~
                " alternations instead of 4.");
            tally = checkThat(tally, size(curvedDecomposition.strips) == 1 &&
                size(curvedDecomposition.seams) == 0 && size(curvedDecomposition.sides) == 2,
                "the rectangle control decomposed into " ~ size(curvedDecomposition.strips) ~
                " strips and " ~ size(curvedDecomposition.seams) ~ " seams instead of one strip and no seam.");
            if (size(curvedDecomposition.strips) == 1)
            {
                tally = checkThat(tally, curvedDecomposition.strips[0].mergeAtStart == false &&
                    curvedDecomposition.strips[0].mergeAtEnd == false &&
                    curvedDecomposition.strips[0].startSectionPolyline == undefined &&
                    curvedDecomposition.strips[0].endSectionPolyline == undefined,
                    "the rectangle control's single strip picked up a merge mark or a prescribed " ~
                    "section.");
            }

        }

        reportCheckTally(context, id, "STRIP DECOMPOSITION SELF TEST", tally,
            "a 6-alternation component splits at its refined merge time into two legs and a " ~
            "trunk, the cut sample and the seam arc are shared arrays rather than numbers that " ~
            "agree, and a genuine rectangle still comes out as one strip.");
    });

// ===================== Strip Fit Self Test =====================

// Both strips are fitted at FIXED grids with the refinement loop switched off, and that is a
// finding rather than a convenience. A merging strip's certified deviation does not fall cleanly
// with grid size - its (q, t) chart has a square-root corner at the merge - and an independent
// recomputation of this fixture's own certification gives, for the leg on uniform stations,
// 8.5e-4 at 6x6, 5.5e-4 at 8x8, 3.0e-4 at 12x8, 2.1e-4 at 15x8, with the graded series
// non-monotone (spec 7.10). So a refine-to-tolerance loop pointed at a target the chart cannot
// reach doubles its way straight into the interpreter's step limit, which is exactly what the
// first two live attempts at this test did. Hence fixed grids, with the deviation REPORTED and
// scale-free assertions around it: whatever accuracy the fit reaches, the patch has to be the
// envelope to that accuracy and its seam edge has to be on the shared contact arc to that
// accuracy.
//
// The grid sizes come from the same recomputation. The leg's section is a monotone half of a
// parabola, so 8 q samples are plenty (q deviation 2.9e-5) and the strip is t-limited. The trunk's
// section crosses the parabola's apex, where arc length turns hardest, so it is q-limited and
// needs 16 (1.3e-4 at 6x16 against 5.4e-4 at 6x12); its t direction is free either way, both of
// its boundary curves being polynomial in t.
annotation { "Feature Type Name" : "Sweep Strip Fit Self Test" }
export const sweepStripFitSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var tally = newCheckTally();
        const surface = mergeFixtureSurface();
        const motion = mergeFixtureMotion();
        const decomposition = decomposeFunnelComponentIntoStrips(motion, surface, {
                    "tStart" : 0, "tEnd" : 1, "sectionTolerance" : 1e-13,
                    "branches" : [mergeFixtureCapBranch(0), mergeFixtureCapBranch(1), mergeFixtureWallBranch()]
                });
        if (decomposition.failed || size(decomposition.seams) != 1 ||
            size(decomposition.seams[0].fineStripIndices) != 2)
        {
            tally = checkThat(tally, false, "the decomposition this test fits did not come out as " ~
                "one seam with two merging strips (see the strip decomposition self test).");
        }
        else
        {
            const seam = decomposition.seams[0];
            // maxRefinementRounds 1 pins the grid; the tolerance still has to be the REAL target
            // for the strip, because the fit spends whatever slack is left under it on knot
            // removal - hand it a loose tolerance and it will happily displace the net by that
            // much and still report itself certified.
            const fixedGrid = { "sectionTolerance" : 1e-13, "maxRefinementRounds" : 1 };
            const legFit = fitEnvelopeComponent(motion, surface,
                mergeMaps(mergeMaps(fixedGrid, { "initialQCount" : 8, "initialStationCount" : 8,
                            "tolerance" : LEG_STRIP_DEVIATION_LIMIT }),
                    decomposition.strips[seam.fineStripIndices[0]]));
            const trunkFit = fitEnvelopeComponent(motion, surface,
                mergeMaps(mergeMaps(fixedGrid, { "initialQCount" : 16, "initialStationCount" : 6,
                            "tolerance" : TRUNK_STRIP_DEVIATION_LIMIT }),
                    decomposition.strips[seam.coarseStripIndex]));
            // The leg's t range ENDS at the seam and the trunk's starts there, so they read
            // opposite edges of their own patches. The limits are twice the recomputed deviation at
            // these grids (5.5e-4 and 1.3e-4): loose enough not to be a tolerance test, tight
            // enough that a chart or seam regression fails them.
            tally = reportStripFit(tally, "STRIP FIT SELF TEST", "leg", legFit, seam.t, true,
                LEG_STRIP_DEVIATION_LIMIT);
            tally = reportStripFit(tally, "STRIP FIT SELF TEST", "trunk", trunkFit, seam.t, false,
                TRUNK_STRIP_DEVIATION_LIMIT);
        }


        // ---------- The graded station layout, on its own ----------
        // Grading is opt-in because it does not improve a merging strip's certified deviation
        // (spec 7.10), but the layout itself has to be right for the caller who asks for it: ends
        // exact, spacing shrinking toward the graded end, and uniform in sqrt of the distance to
        // it - which for stations at (1 - (1 - k/(n-1))^2) means sqrt(1 - fraction) is linear.
        const gradedStations = buildFitStations(0.25, 0.75, 6, [], 1e-9, { "gradeEnd" : true });
        const uniformStations = buildFitStations(0.25, 0.75, 6, [], 1e-9, {});
        println("[STRIP FIT SELF TEST] graded stations " ~ toString(gradedStations) ~
            ", uniform " ~ toString(uniformStations));
        var gradedShrinks = true;
        var gradedSqrtLinear = 0;
        for (var index = 1; index + 1 < size(gradedStations); index += 1)
        {
            if (gradedStations[index + 1] - gradedStations[index] >=
                gradedStations[index] - gradedStations[index - 1])
            {
                gradedShrinks = false;
            }
            gradedSqrtLinear = max(gradedSqrtLinear,
                abs(sqrt(0.75 - gradedStations[index]) - sqrt(0.75 - gradedStations[index - 1]) -
                    (sqrt(0.75 - gradedStations[index + 1]) - sqrt(0.75 - gradedStations[index]))));
        }
        tally = checkThat(tally, size(gradedStations) == 6 && gradedStations[0] == 0.25 &&
            gradedStations[5] == 0.75 && uniformStations[0] == 0.25 && uniformStations[5] == 0.75,
            "a station set did not land its ends exactly on the strip's own t range - a seam " ~
            "station that does not compare equal cannot share arrays.");
        tally = checkThat(tally, gradedShrinks,
            "graded stations do not close up toward the graded end.");
        tally = checkWithin(tally, gradedSqrtLinear, 1e-14,
            "the graded stations' departure from uniform spacing in sqrt(distance to the merge)");

        reportCheckTally(context, id, "STRIP FIT SELF TEST", tally,
            "both strips of a 6-alternation component fit at their fixed grids, each patch is the " ~
            "envelope to its own measured deviation, and both seam edges lie on the one exact " ~
            "contact arc at the merge time - which is the seam statement, two curves within d of " ~
            "the same arc being within 2d of each other.");
    });

/**
 * The four checks the strip fit test makes per strip, and their console lines. `seamAtEnd` says
 * which t end of the strip is the seam - the merging strips end there, the merged one starts
 * there - and `deviationLimit` is the absolute ceiling for this strip at this grid.
 */
function reportStripFit(tally is map, testName is string, label is string, fit is map,
    seamTime is number, seamAtEnd is boolean, deviationLimit is number) returns map
{
    if (fit.failed)
    {
        return checkThat(tally, false, "the " ~ label ~ " strip fit failed: " ~ fit.reason);
    }
    println("[" ~ testName ~ "] " ~ label ~ ": " ~ fit.stationCount ~ "x" ~ fit.qCount ~
        " grid in " ~ fit.refinementRounds ~ " rounds, deviation " ~ fit.worstDeviation ~
        " (q " ~ fit.worstQDeviation ~ ", t " ~ fit.worstTDeviation ~ "), removal " ~
        fit.removalDeviation ~ ", certified " ~ fit.certifiedBound ~ ", budgetHit " ~
        fit.budgetHit ~ ", section residual " ~ fit.worstSectionResidual);
    println("[" ~ testName ~ "] " ~ label ~ " orientation: lambda " ~ fit.orientation.lambdaSign ~
        " consistent " ~ fit.orientation.lambdaSignConsistent ~ ", fold margin " ~
        fit.orientation.worstFoldMargin ~ ", difference " ~ fit.orientation.differenceAgreements ~
        "/" ~ fit.orientation.differenceChecked ~ " agree, q reversed " ~ fit.qReversed);

    var result = checkThat(tally, fit.refinementRounds == 1 && !fit.budgetHit,
        "the " ~ label ~ " strip did not fit its fixed grid once and certify there: rounds " ~
        fit.refinementRounds ~ ", budgetHit " ~ fit.budgetHit ~ ".");
    result = checkWithin(result, fit.certifiedBound, deviationLimit,
        "the " ~ label ~ " strip's certified bound at its fixed grid");

    // Scale-free from here: the patch has to BE the envelope, and its seam edge has to be on the
    // shared contact arc, to whatever accuracy the module certified for the surface it returned.
    const accuracy = 3 * fit.certifiedBound + 1e-6;
    const membership = worstMergeFixtureResidual(fit.surface, 6);
    println("[" ~ testName ~ "] " ~ label ~ " closed-form envelope membership: " ~ membership);
    result = checkWithin(result, membership, accuracy,
        "the " ~ label ~ " patch's closed-form envelope membership against its own deviation");

    // Both strips resample ONE marched arc at the seam (spec 2.3), so both patch edges interpolate
    // samples of the same curve - and the way to see that without fitting the neighbour is to
    // measure each edge against the arc itself, which this fixture knows in closed form.
    const domain = fitSurfaceKnotDomain(fit.surface);
    const seamU = seamAtEnd ? domain.uMax : domain.uMin;
    var worstSeamGap = 0;
    for (var index = 0; index <= 6; index += 1)
    {
        worstSeamGap = max(worstSeamGap, mergeFixtureArcDistance(
                evaluateBSplineSurfacePoint(fit.surface, seamU,
                    domain.vMin + (domain.vMax - domain.vMin) * index / 6), seamTime));
    }
    println("[" ~ testName ~ "] " ~ label ~ " worst seam-edge gap to the exact contact arc at t = " ~
        seamTime ~ ": " ~ worstSeamGap);
    return checkWithin(result, worstSeamGap, accuracy,
        "the " ~ label ~ " patch's seam edge against the exact contact arc");
}

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
    return buildFitStations(tStart, tEnd, stationCount, eventTimes, mergeTolerance, {});
}

/**
 * As above, with optional end grading: { gradeStart, gradeEnd } booleans. A graded end clusters
 * stations QUADRATICALLY toward it, which is uniform in sqrt(distance to that end) - the right
 * spacing for a strip whose t range ends at a section merge, where the section's boundary point
 * runs like a square root of the distance to the merge (spec 7.1). Ungraded, the spacing is
 * uniform, which is what every non-merging component wants.
 *
 * The two endpoints are ASSIGNED rather than computed: a strip's seam station has to compare
 * equal to its neighbour's for the shared arrays to be shared exactly, and
 * tStart + (tEnd - tStart) is not tEnd for every pair of doubles.
 */
export function buildFitStations(tStart is number, tEnd is number, stationCount is number,
    eventTimes is array, mergeTolerance is number, grading is map) returns array
{
    const gradeStart = grading.gradeStart == true;
    const gradeEnd = grading.gradeEnd == true;
    var stations = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        const uniform = index / (stationCount - 1);
        var fraction = uniform;
        if (gradeStart && gradeEnd)
        {
            fraction = 0.5 * (1 - cos(uniform * 180 * degree));
        }
        else if (gradeStart)
        {
            fraction = uniform ^ 2;
        }
        else if (gradeEnd)
        {
            fraction = 1 - (1 - uniform) ^ 2;
        }
        stations[index] = tStart + (tEnd - tStart) * fraction;
    }
    stations[0] = tStart;
    stations[stationCount - 1] = tEnd;
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
 *     tSamples must be monotone along the branch - splitBranchAtTimeExtrema cuts a branch at
 *     its t extrema for exactly that reason, and each side it returns is monotone.
 *
 * A station landing exactly on one of the branch's own end times returns that end SAMPLE, with
 * no interpolation and no polish. That is the shared-array rule (spec 2.3) at its sharpest: two
 * sides cut from one branch carry the same cut sample, and the strips on either side of the cut
 * have to read the same numbers out of it, which interpolating to fraction 1 and then polishing
 * along a different segment would not give.
 */
export function anchorUvAtStation(anchor is map, strippedMotion is map, strippedSurface is map,
    t is number, sectionTolerance is number) returns array
{
    if (anchor.anchorKind == "fixedUv")
    {
        return anchor.uv;
    }
    const sampleCount = size(anchor.tSamples);
    if (t == anchor.tSamples[0])
    {
        return anchor.uvSamples[0];
    }
    if (t == anchor.tSamples[sampleCount - 1])
    {
        return anchor.uvSamples[sampleCount - 1];
    }
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
 * (uv units; default anchor distance / (2q)), marchMaxSteps (default max(400, 40q)),
 * sectionPolyline (a SHARED marched polyline for this station - clipped to these anchors and
 * resampled instead of marching, which is how two strips meeting at a seam resample one arc
 * rather than two; spec 2.3 and 7.1) }.
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
    if (options.sectionPolyline != undefined)
    {
        const clipped = clipSectionPolylineToAnchors(options.sectionPolyline, startUv, endUv);
        if (clipped.failed)
        {
            return { "failed" : true, "reason" : clipped.reason ~ " (t = " ~ t ~ ")" };
        }
        return splitResampledRow(resampleAndPolishSection(strippedMotion, strippedSurface, t,
                clipped.uvPoints, 2 * qCount - 1, sectionTolerance), qCount);
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
    return interpolateFitGrid(grid, uDegree, vDegree, vPeriodic, undefined);
}

/**
 * Same, with the v parameters PRESCRIBED rather than averaged off this grid's own rows.
 *
 * That is what lets two patches share a boundary row EXACTLY (spec 7.4's island split). A
 * clamped u interpolation puts its end CONTROL row exactly at its end DATA row, so two halves
 * cut at a shared station already carry the same boundary data - but each half then interpolates
 * that row in v at its own averaged parameters, and two different parameterizations of the same
 * eight points are two different curves. Measured on the island fixture: 7.2e-7 m apart with
 * each half's own parameters, bit-identical with the whole grid's.
 */
export function interpolateFitGrid(grid is array, uDegree is number, vDegree is number,
    vPeriodic is boolean, vParametersOverride) returns map
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
    const uParameters = averageParameterSets(uParameterSets, rowCount);
    const vParameters = vParametersOverride == undefined ?
        fitGridVParameters(grid, vPeriodic, columnCount) : vParametersOverride;

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

/** A fit grid's averaged v parameters, over the rows that HAVE a chord parameterization - a
    collapsed pole row has none. */
function fitGridVParameters(grid is array, vPeriodic is boolean, columnCount is number) returns array
{
    var vParameterSets = [];
    for (var rowIndex = 0; rowIndex < size(grid); rowIndex += 1)
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
    return averageParameterSets(vParameterSets, columnCount);
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
                "maxRefinementRounds" : 8, "orientOutward" : true,
                "gradeStart" : false, "gradeEnd" : false
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
        const stations = buildFitStations(input.tStart, input.tEnd, stationCount, input.eventTimes,
            0.25 * spacing, { "gradeStart" : input.gradeStart, "gradeEnd" : input.gradeEnd });

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var uvRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            // A seam station resamples the polyline the neighbouring strip marched, so both
            // sides of the seam read one array of numbers (spec 2.3). Interior stations and every
            // held-out certification row below march their own.
            var stationOptions = input;
            if (stationIndex == 0 && input.startSectionPolyline != undefined)
            {
                stationOptions = mergeMaps(input, { "sectionPolyline" : input.startSectionPolyline });
            }
            else if (stationIndex == size(stations) - 1 && input.endSectionPolyline != undefined)
            {
                stationOptions = mergeMaps(input, { "sectionPolyline" : input.endSectionPolyline });
            }
            const row = sectionSamplesAtStation(strippedMotion, strippedSurface, stations[stationIndex],
                input.startAnchor, input.endAnchor, qCount, stationOptions);
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
 * Fit one grazing island as a pole-collapsed patch (spec 7.1, probe 4): stations span the
 * fitted t range, a pole row is the lifted stationary point repeated, and every other station
 * is a closed loop seeded on the fixed +u ray out of the interpolated island center.
 * Certification and refinement follow fitEnvelopeComponent; knot cleanup is skipped so a pole
 * row stays exactly collapsed.
 *
 * The fitted range need not be the whole island. tStart / tEnd CLIP it, and a clipped end
 * carries a full loop row instead of a pole - which is what makes the three island patch
 * shapes (spec 7.4): two poles, one pole, or none. Only the last two are emittable, and only
 * the last two can be fold-free: lambda at a t-extreme is exactly f_t there (f_u = f_v = 0 at
 * an extreme), and the two extremes of one island have opposite f_t signs, so a TWO-pole island
 * always folds somewhere between them.
 *
 * islandInput: {
 *     birth, death {map} : { u, v, t } from island refinement (refineBlockStationaryPoint),
 *     tStart {number, default birth.t}, tEnd {number, default death.t} : the clipped range,
 *     tolerance {number},
 *     initialQCount {default 12}, initialStationCount {default 16},
 *     maxQCount {default 60}, maxStationCount {default 60},
 *     maxRefinementRounds {default 8}, sectionTolerance {default 1e-12}
 * }
 *
 * Returns the fitEnvelopeComponent result shape plus poleStartPoint / poleEndPoint (the
 * lifted 3D poles, plain numbers - meaningful only where the matching poleAtStart /
 * poleAtEnd is true), poleAtStart / poleAtEnd {boolean}, and liftedGrid, the (station, q) grid
 * of lifted points as fitted, which is what the emission splits.
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
    const tStart = input.tStart == undefined ? birth.t : input.tStart;
    const tEnd = input.tEnd == undefined ? death.t : input.tEnd;
    // A clip lands on a pole only when it lands ON the extreme; anywhere else the row is a
    // full loop. The window is relative to the island's own t extent, not absolute.
    const poleWindow = 1e-9 * max(1e-30, death.t - birth.t);
    const poleAtStart = abs(tStart - birth.t) <= poleWindow;
    const poleAtEnd = abs(tEnd - death.t) <= poleWindow;
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
        const stations = buildFitStations(tStart, tEnd, stationCount, [], 0);

        var liftedGrid = makeArray(size(stations));
        var qMidRows = makeArray(size(stations));
        var uvRows = makeArray(size(stations));
        var worstSectionResidual = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            const atStart = stationIndex == 0 && poleAtStart;
            const atEnd = stationIndex == size(stations) - 1 && poleAtEnd;
            if (atStart || atEnd)
            {
                const pole = atStart ? birth : death;
                liftedGrid[stationIndex] = makeArray(qCount, atStart ? poleStartPoint : poleEndPoint);
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
            for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
            {
                if (qMidRows[stationIndex] == undefined)
                {
                    continue;                                   // a pole row: reversing it is a no-op
                }
                liftedGrid[stationIndex] = reverseFitGridRow(liftedGrid[stationIndex], true);
                qMidRows[stationIndex] = reverseFitGridMidRow(qMidRows[stationIndex]);
            }
        }
        const fitSurface = interpolateFitGrid(liftedGrid, 3, 3, true);

        var worstQ = 0;
        for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
        {
            if (qMidRows[stationIndex] == undefined)
            {
                continue;
            }
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
                "poleEndPoint" : poleEndPoint,
                "poleAtStart" : poleAtStart,
                "poleAtEnd" : poleAtEnd,
                "tStart" : tStart,
                "tEnd" : tEnd,
                "liftedGrid" : liftedGrid
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

// ============================= Island patch emission =============================

/**
 * Emit an island component's fitted patch as kernel B-spline face(s) (spec 7.4). The three
 * shapes and why only two of them ship:
 *
 *   - NO pole (both ends clipped by the sweep's own t range): cylinder topology, two genuine
 *     boundary loops, one closed direction. Emitted as ONE v-periodic face, which is the shape
 *     spec 7.7 proved the kernel accepts.
 *   - ONE pole (the other end clipped): a cap, one collapsed boundary row against one genuine
 *     loop. Probe 4's accepted shape.
 *   - TWO poles: rejected by opCreateBSplineSurface outright (spec 7.4 - a net with both
 *     u-boundary rows collapsed and v closed has no non-degenerate boundary to form a face
 *     from), so it splits at a mid-t station into two single-pole caps that share that station's
 *     loop row EXACTLY. Both halves interpolate the shared row against the WHOLE grid's v
 *     parameters, without which the two boundary curves pass through the same points along
 *     different parameterizations and miss each other by 7.2e-7 m on this module's fixture.
 *
 * A two-pole island cannot be fold-free, so v1 never actually emits one: lambda at a t-extreme
 * IS f_t there, and an island's two extremes carry opposite f_t signs (whichever way the loop
 * shrinks to a point, the inside sign of f is the same at both ends, so the two Hessians have
 * the same definiteness and the f_t signs must differ). `requireFoldFree` is therefore the
 * default, and the split path exists for the emission SHAPE - which the trimming work of spec
 * 10 will need once a folded component can be cut down to its fold-free pieces.
 *
 * options: { requireFoldFree {boolean, default true} : refuse a component whose lambda changes
 * sign, reporting SWEEP_ISLAND_UNSUPPORTED rather than emitting a folded patch }.
 *
 * Returns { refused {boolean}, reason {string, when refused}, poleCount, shape {string},
 * patchCount, faceCount, declaredPeriodic {array of boolean}, patchIds {array of Id},
 * seamError {number, 0 unless split}, cut {number, the shared station index when split} }.
 */
export function emitIslandPatches(context is Context, id is Id, islandFit is map, options is map) returns map
{
    const settings = mergeMaps({ "requireFoldFree" : true }, options);
    const poleCount = (islandFit.poleAtStart ? 1 : 0) + (islandFit.poleAtEnd ? 1 : 0);
    if (settings.requireFoldFree && !islandFit.orientation.lambdaSignConsistent)
    {
        return {
                "refused" : true,
                "reason" : "SWEEP_ISLAND_UNSUPPORTED: lambda changes sign across this island, so " ~
                "the envelope folds inside it and the patch would be locally self-intersecting.",
                "poleCount" : poleCount,
                "shape" : "folded",
                "patchCount" : 0, "faceCount" : 0,
                "declaredPeriodic" : [], "patchIds" : [], "seamError" : 0
            };
    }
    if (poleCount < 2)
    {
        const patch = emitFitSurfacePatch(context, id + "islandPatch", islandFit.surface);
        return {
                "refused" : patch.refused,
                "reason" : patch.refused ? ("SWEEP_ISLAND_UNSUPPORTED: " ~ patch.reason) : "",
                "poleCount" : poleCount,
                "shape" : poleCount == 0 ? "loop ends" : "one pole",
                "patchCount" : patch.refused ? 0 : 1, "faceCount" : patch.faceCount,
                "declaredPeriodic" : patch.refused ? [] : [patch.declaredPeriodic],
                "patchIds" : patch.refused ? [] : [patch.id], "seamError" : 0
            };
    }
    const split = splitIslandFitGrid(islandFit);
    if (split.failed)
    {
        return {
                "refused" : true, "reason" : split.reason, "poleCount" : poleCount,
                "shape" : "two poles", "patchCount" : 0, "faceCount" : 0,
                "declaredPeriodic" : [], "patchIds" : [], "seamError" : 0
            };
    }
    const startCap = emitFitSurfacePatch(context, id + "islandCapStart", split.startSurface);
    const endCap = emitFitSurfacePatch(context, id + "islandCapEnd", split.endSurface);
    var patchIds = [];
    var declaredPeriodic = [];
    for (var cap in [startCap, endCap])
    {
        if (!cap.refused)
        {
            patchIds = append(patchIds, cap.id);
            declaredPeriodic = append(declaredPeriodic, cap.declaredPeriodic);
        }
    }
    const refused = startCap.refused || endCap.refused;
    return {
            "refused" : refused,
            "reason" : refused ? ("SWEEP_ISLAND_UNSUPPORTED: start cap: " ~ startCap.reason ~
                    " end cap: " ~ endCap.reason) : "",
            "poleCount" : poleCount,
            "shape" : "two poles split at a shared row",
            "patchCount" : size(patchIds), "faceCount" : startCap.faceCount + endCap.faceCount,
            "declaredPeriodic" : declaredPeriodic,
            "patchIds" : patchIds,
            "seamError" : split.seamError, "cut" : split.cut,
            "startSurface" : split.startSurface, "endSurface" : split.endSurface
        };
}

/**
 * Split a two-pole island's fitted grid at a mid-t station into two single-pole caps (spec
 * 7.4), each interpolated against the WHOLE grid's v parameters so the shared row's boundary
 * curve is one curve and not two.
 *
 * The cut is the middle station, held so that each half keeps the four rows a cubic
 * interpolation needs - which is why an island fitted on fewer than seven stations reports
 * rather than splits.
 *
 * Returns { failed, reason? , cut, startSurface, endSurface, sharedRow, seamError } where
 * seamError is the worst coordinate gap between the two halves' shared CONTROL row - zero by
 * construction, and reported so that a change which breaks it cannot pass unnoticed.
 */
export function splitIslandFitGrid(islandFit is map) returns map
{
    const grid = islandFit.liftedGrid;
    const rowCount = size(grid);
    if (rowCount < 7)
    {
        return { "failed" : true, "reason" : "an island split needs at least 7 stations so that " ~
                    "both halves keep 4 rows for a cubic interpolation, got " ~ rowCount ~ "." };
    }
    const cut = min(max(floor((rowCount - 1) / 2), 3), rowCount - 4);
    const vParameters = islandFit.surface.vParameters;
    const startSurface = interpolateFitGrid(subArray(grid, 0, cut + 1), 3, 3, true, vParameters);
    const endSurface = interpolateFitGrid(subArray(grid, cut, rowCount), 3, 3, true, vParameters);

    const startBoundary = startSurface.controlPoints[size(startSurface.controlPoints) - 1];
    const endBoundary = endSurface.controlPoints[0];
    var seamError = 0;
    for (var index = 0; index < size(startBoundary); index += 1)
    {
        seamError = max(seamError, norm(startBoundary[index] - endBoundary[index]));
    }
    return {
            "failed" : false, "cut" : cut,
            "startSurface" : startSurface, "endSurface" : endSurface,
            "sharedRow" : grid[cut], "seamError" : seamError
        };
}

/**
 * One kernel B-spline face from a fit surface: the periodic declaration first, then the
 * closed-clamped non-periodic declaration if the kernel refuses the first (spec 7.4 found the
 * flag is not what a whole island fails on, but a shape the kernel has never been asked for
 * gets asked both ways rather than assumed).
 *
 * BOTH attempts are guarded, and each takes its own operation id because a thrown operation
 * still registers the id it was given. A refusal is REPORTED, never thrown: emission has to
 * degrade to a named seam rather than take the whole feature down with it (spec 9), and the
 * live run that found this was the two-pole island split, where the kernel refuses both
 * declarations and the unguarded second attempt aborted the test before it printed anything.
 *
 * Returns { faceCount, declaredPeriodic {boolean}, id, refused {boolean}, reason {string} }.
 */
function emitFitSurfacePatch(context is Context, id is Id, surface is map) returns map
{
    const withUnits = attachFitSurfaceUnits(surface);
    var reason = "";
    for (var declarePeriodic in [true, false])
    {
        const attemptName = declarePeriodic ? "periodic" : "clamped";
        const attemptId = id + attemptName;
        try
        {
            opCreateBSplineSurface(context, attemptId, {
                        "bSplineSurface" : kernelFitSurface(withUnits, declarePeriodic)
                    });
        }
        catch (error)
        {
            reason = reason ~ " " ~ attemptName ~ ": " ~ toString(error);
        }
        const faces = evaluateQuery(context, qCreatedBy(attemptId, EntityType.FACE));
        if (size(faces) > 0)
        {
            return { "faceCount" : size(faces), "declaredPeriodic" : declarePeriodic,
                    "id" : attemptId, "refused" : false, "reason" : "" };
        }
    }
    return {
            "faceCount" : 0, "declaredPeriodic" : false, "id" : undefined, "refused" : true,
            "reason" : "the kernel refused a " ~ size(surface.controlPoints) ~ "x" ~
            size(surface.controlPoints[0]) ~ " net in both declarations -" ~ reason
        };
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

// ============================= Strip decomposition (spec 7.1) =============================

/**
 * Strip decomposition - the more-than-four-alternation case of spec 7.1.
 *
 * The (q, t) rectangle exists only while every station's section is ONE arc running between the
 * same two lateral boundary curves. That stops being true where a lateral branch reaches a t
 * extremum: there the section is tangent to the face boundary, and two arcs merge into one (or
 * one splits into two, going the other way in t). Counted the way spec 7.1 counts, this is a
 * boundary loop with more than four lateral/cap alternations, because one end of the t range
 * then carries more than one cap arc.
 *
 * So the decomposition is in t, at the extremum times, and its pieces are:
 *   - SIDES: each lateral branch cut at its own refined interior t extrema into t-monotone
 *     pieces. The two pieces meeting at a cut carry the SAME appended sample - one array
 *     element, shared - so the corner where two strips meet on the face boundary is one number
 *     rather than two that agree.
 *   - BANDS: the t intervals between consecutive cut times, with the component's own t range as
 *     the outer bounds.
 *   - STRIPS: per band, the sides spanning it paired into arcs. Pairing is decided by MARCHING.
 *     Which two boundary points one section arc joins is not readable off their positions, and a
 *     march aimed at the wrong partner can only get there by crawling along the domain boundary
 *     once its own arc has run out - which is what the pairing test rejects.
 *   - SEAMS: at each interior cut time the arc is marched ONCE, on whichever side of the cut has
 *     fewer arcs (the merged side), and the strips on the other side resample THAT polyline
 *     instead of marching their own. Both sides of a seam then resample one array of numbers
 *     (spec 2.3), so the seam carries fit error only, with no independent marching error
 *     underneath it.
 *
 * What the split does not remove: a strip whose t range ENDS at a merge is not smooth in t
 * there. The merging arc's endpoint runs along the boundary like sqrt(t* - t) - a tangency is a
 * square-root event - so the (q, t) chart of the splitting side has a square-root corner. The
 * decomposition MARKS those ends (mergeAtStart / mergeAtEnd) and leaves the remedy to the caller,
 * because the obvious remedy does not work: clustering stations quadratically toward the merge
 * fixes the q = 1 boundary curve in isolation (28x better at 8 stations, recomputed
 * independently) and does NOT improve the strip's certified deviation, which is dominated by
 * held-out samples off that curve. Measured on the merge fixture at matched grids: 6x6 graded
 * 1.4e-3 against uniform 8.5e-4, 8x8 graded 5.1e-4 against uniform 5.5e-4, 12x8 graded 1.6e-4
 * against uniform 3.0e-4 - inconsistent in both directions. `gradeMergeEnds` turns the clustering
 * on for a caller who wants it; the default is off, and the open question is recorded in spec 7.10
 * rather than answered by a flag.
 *
 * A consequence worth knowing before pointing the refinement loop at a merging strip: its
 * deviation does not fall cleanly with grid size (uniform 6x6 8.5e-4, 8x8 5.5e-4, 12x8 3.0e-4,
 * 15x8 2.1e-4, and the graded series is non-monotone), so a refine-to-tolerance loop given a
 * target below what the chart can reach will double its way to the interpreter's step limit. The
 * merged side has no such problem and converges normally (trunk 6x16 1.3e-4, 6x23 1.9e-5).
 *
 * Trim loops: the pairing and seam tests read the knot-domain rectangle as the boundary, which is
 * the domain of every v1 fixture. A face whose funnel is bounded by interior trim loops needs the
 * same two tests taken against the loops (spec 6.7's masking) rather than against the rectangle.
 */

/**
 * uv on a branch's sample polyline at the continuous sample parameter sigma (a fractional sample
 * index). The polyline IS the boundary as far as the fit is concerned - anchorUvAtStation
 * interpolates it and polishes ALONG it, never off it - so the extremum search runs on the same
 * polyline the anchors will, and the split time it reports is that polyline's own extremum rather
 * than the underlying curve's.
 */
function branchUvAtParameter(branch is map, sigma is number) returns array
{
    const lastIndex = size(branch.uvSamples) - 1;
    const clamped = clampToRange(sigma, 0, lastIndex);
    const low = min(floor(clamped), lastIndex - 1);
    const fraction = clamped - low;
    const uvLow = branch.uvSamples[low];
    const uvHigh = branch.uvSamples[low + 1];
    return [uvLow[0] + fraction * (uvHigh[0] - uvLow[0]), uvLow[1] + fraction * (uvHigh[1] - uvLow[1])];
}

/**
 * The branch's own t at sigma: the interpolated sample t is a seed, and 1D Newton on
 * f(uv(sigma), .) = 0 is the answer. f_t does not vanish at a boundary tangency - the tangency is
 * in uv, not in t - so this Newton stays well conditioned exactly where the extremum search needs
 * it most.
 *
 * Returns { failed, t, residual, uv }.
 */
function branchTimeAtParameter(strippedMotion is map, strippedSurface is map, branch is map,
    sigma is number, tStart is number, tEnd is number, tolerance is number) returns map
{
    const uv = branchUvAtParameter(branch, sigma);
    const lastIndex = size(branch.tSamples) - 1;
    const clamped = clampToRange(sigma, 0, lastIndex);
    const low = min(floor(clamped), lastIndex - 1);
    const fraction = clamped - low;
    const stepLimit = 0.25 * abs(tEnd - tStart);
    var t = branch.tSamples[low] + fraction * (branch.tSamples[low + 1] - branch.tSamples[low]);
    var residual = 1e300;
    for (var iteration = 0; iteration < 20; iteration += 1)
    {
        const gradient = evaluateEnvelopeGradientPointwise(strippedMotion, strippedSurface, uv[0], uv[1], t);
        residual = abs(gradient.value);
        if (residual <= tolerance)
        {
            break;
        }
        if (abs(gradient.tDerivative) < 1e-30)
        {
            return { "failed" : true, "t" : t, "residual" : residual, "uv" : uv };
        }
        t = t - clampToRange(gradient.value / gradient.tDerivative, -stepLimit, stepLimit);
    }
    return { "failed" : false, "t" : t, "residual" : residual, "uv" : uv };
}

/**
 * Refine one bracketed interior t extremum of a branch: golden section on sigma, and the Newton
 * solve at the converged sigma is the reported t. Searching sigma rather than d t / d sigma is
 * deliberate - t is quadratic in sigma near a smooth extremum, so a sigma good to 1e-9 pins t to
 * machine precision, and no second derivative of the polyline is needed (a piecewise-linear
 * polyline has none).
 *
 * The reported t sits at or below a maximum (at or above a minimum) of the polyline's own t,
 * which is the safe side: the band that ends there stays valid.
 *
 * Returns { failed, reason?, sigma, t, uv, residual, isMaximum }.
 */
function refineBranchTimeExtremum(strippedMotion is map, strippedSurface is map, branch is map,
    lowSigma is number, highSigma is number, isMaximum is boolean, tStart is number,
    tEnd is number, tolerance is number) returns map
{
    const golden = (sqrt(5) - 1) / 2;
    const stalled = { "failed" : true,
            "reason" : "the branch time solve stalled inside a t extremum bracket" };
    var low = lowSigma;
    var high = highSigma;
    var probeA = high - golden * (high - low);
    var probeB = low + golden * (high - low);
    var valueA = branchTimeAtParameter(strippedMotion, strippedSurface, branch, probeA, tStart, tEnd, tolerance);
    var valueB = branchTimeAtParameter(strippedMotion, strippedSurface, branch, probeB, tStart, tEnd, tolerance);
    if (valueA.failed || valueB.failed)
    {
        return stalled;
    }
    for (var iteration = 0; iteration < 40; iteration += 1)
    {
        if (high - low <= 1e-9)
        {
            break;
        }
        if (isMaximum ? valueA.t > valueB.t : valueA.t < valueB.t)
        {
            high = probeB;
            probeB = probeA;
            valueB = valueA;
            probeA = high - golden * (high - low);
            valueA = branchTimeAtParameter(strippedMotion, strippedSurface, branch, probeA, tStart, tEnd, tolerance);
            if (valueA.failed)
            {
                return stalled;
            }
        }
        else
        {
            low = probeA;
            probeA = probeB;
            valueA = valueB;
            probeB = low + golden * (high - low);
            valueB = branchTimeAtParameter(strippedMotion, strippedSurface, branch, probeB, tStart, tEnd, tolerance);
            if (valueB.failed)
            {
                return stalled;
            }
        }
    }
    const sigma = 0.5 * (low + high);
    const settled = branchTimeAtParameter(strippedMotion, strippedSurface, branch, sigma, tStart, tEnd, tolerance);
    if (settled.failed)
    {
        return stalled;
    }
    return { "failed" : false, "sigma" : sigma, "t" : settled.t, "uv" : settled.uv,
            "residual" : settled.residual, "isMaximum" : isMaximum };
}

/**
 * Cut one lateral branch into t-monotone sides at its refined interior t extrema (spec 7.1). The
 * cut sample is APPENDED to the side below and PREPENDED to the side above as the same value, so
 * the two sides share it exactly; every other sample is carried over from the input arrays
 * untouched.
 *
 * options: { tStart, tEnd (the component's t range, for the Newton step limit), sectionTolerance
 * (residual, default 1e-12), plateauTolerance (a sample-to-sample t difference at or below this
 * counts as flat and is not read as a turn, default 0) }.
 *
 * Returns { failed, reason?, sides {array}, extrema {array} }. Each side is a branch anchor
 * (anchorKind "branch", tSamples, uvSamples) carrying tMin, tMax, rising, and cutAtStart /
 * cutAtEnd - which of its ends is a shared cut rather than an original branch end.
 */
export function splitBranchAtTimeExtrema(strippedMotion is map, strippedSurface is map,
    branch is map, options is map) returns map
{
    const tolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const plateauTolerance = options.plateauTolerance == undefined ? 0 : options.plateauTolerance;
    const tSamples = branch.tSamples;
    const sampleCount = size(tSamples);
    if (sampleCount < 2 || size(branch.uvSamples) != sampleCount)
    {
        return { "failed" : true, "reason" : "a lateral branch needs at least two samples and " ~
                    "matching t and uv arrays" };
    }

    // Turns are read off the SIGN RUNS of the sample differences rather than off single
    // differences, and the reason is the symmetric case: a branch sampled symmetrically about its
    // own extremum has two samples at exactly the same t, so the difference between them is zero
    // and the two differences flanking any single sample never have opposite signs. Walking the
    // runs and remembering the last nonzero sign brackets that turn between the last rising
    // sample and the first falling one, plateau or no plateau. (Live, 2026-08-23: this is what a
    // 20-sample even layout on the merge fixture does, and the single-difference test missed the
    // one turn the fixture has.)
    var extrema = [];
    var runStartIndex = undefined;
    var runSign = 0;
    for (var index = 0; index + 1 < sampleCount; index += 1)
    {
        const difference = tSamples[index + 1] - tSamples[index];
        if (abs(difference) <= plateauTolerance)
        {
            continue;
        }
        const sign = difference > 0 ? 1 : -1;
        if (runSign != 0 && sign != runSign)
        {
            const refined = refineBranchTimeExtremum(strippedMotion, strippedSurface, branch,
                runStartIndex, index + 1, runSign > 0, options.tStart, options.tEnd, tolerance);
            if (refined.failed)
            {
                return { "failed" : true, "reason" : refined.reason };
            }
            extrema = append(extrema, refined);
        }
        runStartIndex = index;
        runSign = sign;
    }

    const cutCount = size(extrema);
    var sides = makeArray(cutCount + 1);
    var lowerIndex = 0;
    for (var sideIndex = 0; sideIndex <= cutCount; sideIndex += 1)
    {
        var upperIndex = sampleCount;
        if (sideIndex < cutCount)
        {
            upperIndex = lowerIndex;
            while (upperIndex < sampleCount && upperIndex < extrema[sideIndex].sigma)
            {
                upperIndex += 1;
            }
        }
        var sideT = subArray(tSamples, lowerIndex, upperIndex);
        var sideUv = subArray(branch.uvSamples, lowerIndex, upperIndex);
        if (sideIndex > 0)
        {
            sideT = concatenateArrays([[extrema[sideIndex - 1].t], sideT]);
            sideUv = concatenateArrays([[extrema[sideIndex - 1].uv], sideUv]);
        }
        if (sideIndex < cutCount)
        {
            sideT = concatenateArrays([sideT, [extrema[sideIndex].t]]);
            sideUv = concatenateArrays([sideUv, [extrema[sideIndex].uv]]);
        }
        if (size(sideT) < 2)
        {
            return { "failed" : true, "reason" : "two t extrema fell inside one branch sample " ~
                        "interval - the branch is sampled too coarsely to split" };
        }
        // Monotonicity is "never both directions", not "never against the overall rise": a side
        // running from one t back to the same t has no overall rise to compare against, and
        // testing against zero would call it monotone whatever it does in between.
        var tMin = sideT[0];
        var tMax = sideT[0];
        var sawRise = false;
        var sawFall = false;
        for (var index = 1; index < size(sideT); index += 1)
        {
            tMin = min(tMin, sideT[index]);
            tMax = max(tMax, sideT[index]);
            const step = sideT[index] - sideT[index - 1];
            if (step > plateauTolerance)
            {
                sawRise = true;
            }
            else if (step < -plateauTolerance)
            {
                sawFall = true;
            }
        }
        if (sawRise && sawFall)
        {
            return { "failed" : true, "reason" : "a branch side still turns in t after splitting " ~
                        "at its bracketed t extrema (t from " ~ tMin ~ " to " ~ tMax ~ ")" };
        }
        sides[sideIndex] = {
                "anchorKind" : "branch", "tSamples" : sideT, "uvSamples" : sideUv,
                "tMin" : tMin, "tMax" : tMax, "rising" : sawRise,
                "cutAtStart" : sideIndex > 0, "cutAtEnd" : sideIndex < cutCount
            };
        lowerIndex = upperIndex;
    }
    return { "failed" : false, "sides" : sides, "extrema" : extrema };
}

/**
 * Spec 7.1's alternation count, read off the sides. Cap arcs live only at the two ends of the t
 * range and no two of them can be adjacent around the boundary loop (a lateral arc separates
 * them), so each cap arc contributes exactly two alternations and the count is just the number of
 * side endpoints landing on the two caps. A rectangle is 4 - one arc at each end; the shape this
 * decomposition exists for is anything above that.
 *
 * Returns { endpointsAtStart, endpointsAtEnd, capArcsAtStart, capArcsAtEnd, alternationCount,
 * exceedsRectangle }.
 */
export function componentBoundaryAlternations(sides is array, tStart is number, tEnd is number,
    capTolerance is number) returns map
{
    var atStart = 0;
    var atEnd = 0;
    for (var side in sides)
    {
        if (abs(side.tMin - tStart) <= capTolerance)
        {
            atStart += 1;
        }
        if (abs(side.tMax - tEnd) <= capTolerance)
        {
            atEnd += 1;
        }
    }
    return {
            "endpointsAtStart" : atStart, "endpointsAtEnd" : atEnd,
            "capArcsAtStart" : atStart / 2, "capArcsAtEnd" : atEnd / 2,
            "alternationCount" : atStart + atEnd, "exceedsRectangle" : atStart + atEnd > 4
        };
}

/**
 * March the section arc between two lateral sides at one station. Returns the marched polyline
 * plus the two tests a pairing decision rests on: whether the march arrived, and whether its
 * INTERIOR stayed off the domain boundary. A march aimed at a boundary point its own arc does not
 * reach can still arrive - by crawling along the boundary through the stretch where the section
 * leaves the domain - so arriving is not enough.
 *
 * Returns { failed, reason?, uvPoints, length, reachedEnd, crawled, startUv, endUv }.
 */
function marchSectionArcBetweenSides(strippedMotion is map, strippedSurface is map, t is number,
    startSide is map, endSide is map, domain is map, options is map) returns map
{
    const tolerance = options.sectionTolerance == undefined ? 1e-12 : options.sectionTolerance;
    const divisions = options.marchDivisions == undefined ? 24 : options.marchDivisions;
    const startUv = anchorUvAtStation(startSide, strippedMotion, strippedSurface, t, tolerance);
    const endUv = anchorUvAtStation(endSide, strippedMotion, strippedSurface, t, tolerance);
    const distance = sqrt((endUv[0] - startUv[0]) ^ 2 + (endUv[1] - startUv[1]) ^ 2);
    if (distance < 1e-12)
    {
        return { "failed" : true, "reason" : "a strip's section collapses to a point at t = " ~ t };
    }
    const stepSize = distance / divisions;
    const march = marchSectionCurve(strippedMotion, strippedSurface, t, startUv, endUv,
        { "stepSize" : stepSize, "maxSteps" : max(400, 20 * divisions), "tolerance" : tolerance });
    var length = 0;
    for (var index = 1; index < size(march.uvPoints); index += 1)
    {
        length += sqrt((march.uvPoints[index][0] - march.uvPoints[index - 1][0]) ^ 2 +
                (march.uvPoints[index][1] - march.uvPoints[index - 1][1]) ^ 2);
    }
    return {
            "failed" : false, "uvPoints" : march.uvPoints, "length" : length,
            "reachedEnd" : march.reachedEnd, "startUv" : startUv, "endUv" : endUv,
            "crawled" : sectionInteriorTouchesDomain(march.uvPoints, domain, 0.4 * stepSize)
        };
}

/**
 * True when a marched section's interior runs along the domain boundary. The three points at each
 * end are exempt: an arc's own endpoints ARE on the boundary, and the samples next to them are
 * within a step of it.
 */
function sectionInteriorTouchesDomain(uvPoints is array, domain is map, tolerance is number) returns boolean
{
    for (var index = 3; index + 3 < size(uvPoints); index += 1)
    {
        const uv = uvPoints[index];
        if (uv[0] - domain.uMin <= tolerance || domain.uMax - uv[0] <= tolerance ||
            uv[1] - domain.vMin <= tolerance || domain.vMax - uv[1] <= tolerance)
        {
            return true;
        }
    }
    return false;
}

/**
 * Pair one band's lateral sides into section arcs at the band's midpoint station, by marching
 * (see marchSectionArcBetweenSides). Among the candidates that arrive without crawling the
 * shortest march wins - a defensive tie-break, since a genuine arc is the shortest way between
 * its own two ends.
 *
 * Returns { failed, reason?, pairs {array of [i, j] index pairs into sideIndices} }.
 */
function pairBandSides(strippedMotion is map, strippedSurface is map, t is number, sides is array,
    sideIndices is array, domain is map, options is map) returns map
{
    const count = size(sideIndices);
    var paired = makeArray(count, false);
    var pairs = makeArray(count / 2);
    var pairCount = 0;
    for (var first = 0; first < count; first += 1)
    {
        if (paired[first])
        {
            continue;
        }
        var bestSecond = undefined;
        var bestLength = 1e300;
        for (var second = first + 1; second < count; second += 1)
        {
            if (paired[second])
            {
                continue;
            }
            const arc = marchSectionArcBetweenSides(strippedMotion, strippedSurface, t,
                sides[sideIndices[first]], sides[sideIndices[second]], domain, options);
            if (arc.failed || !arc.reachedEnd || arc.crawled || arc.length >= bestLength)
            {
                continue;
            }
            bestLength = arc.length;
            bestSecond = second;
        }
        if (bestSecond == undefined)
        {
            return { "failed" : true, "reason" : "no section arc joins one of the lateral " ~
                        "boundary points at t = " ~ t ~ " to another - the component's boundary " ~
                        "curves are incomplete or the band is degenerate" };
        }
        paired[first] = true;
        paired[bestSecond] = true;
        pairs[pairCount] = [first, bestSecond];
        pairCount += 1;
    }
    return { "failed" : false, "pairs" : pairs };
}

/** The closest point on a uv polyline: { position (a continuous vertex index), squaredDistance }. */
function closestPositionOnPolyline(uvPoints is array, uv is array) returns map
{
    var bestPosition = 0;
    var bestSquaredDistance = 1e300;
    for (var index = 0; index + 1 < size(uvPoints); index += 1)
    {
        const start = uvPoints[index];
        const delta = [uvPoints[index + 1][0] - start[0], uvPoints[index + 1][1] - start[1]];
        const lengthSquared = delta[0] ^ 2 + delta[1] ^ 2;
        const fraction = lengthSquared < 1e-300 ? 0 :
            clampToRange(((uv[0] - start[0]) * delta[0] + (uv[1] - start[1]) * delta[1]) / lengthSquared, 0, 1);
        const offset = [start[0] + fraction * delta[0] - uv[0], start[1] + fraction * delta[1] - uv[1]];
        const squaredDistance = offset[0] ^ 2 + offset[1] ^ 2;
        if (squaredDistance < bestSquaredDistance)
        {
            bestSquaredDistance = squaredDistance;
            bestPosition = index + fraction;
        }
    }
    return { "position" : bestPosition, "squaredDistance" : bestSquaredDistance };
}

/**
 * The sub-polyline of a SHARED section polyline between two anchors: the interior vertices are
 * the shared polyline's own numbers and the two ends are the anchors themselves, so strips on
 * either side of a seam resample one marched arc instead of marching their own (spec 2.3). The
 * order is reversed when the anchors run against the polyline.
 *
 * Returns { failed, reason?, uvPoints, startPosition, endPosition }.
 */
function clipSectionPolylineToAnchors(uvPoints is array, startUv is array, endUv is array) returns map
{
    const pointCount = size(uvPoints);
    if (pointCount < 2)
    {
        return { "failed" : true, "reason" : "a shared section polyline needs at least two points" };
    }
    const startPosition = closestPositionOnPolyline(uvPoints, startUv).position;
    const endPosition = closestPositionOnPolyline(uvPoints, endUv).position;
    if (abs(endPosition - startPosition) < 1e-9)
    {
        return { "failed" : true, "reason" : "both anchors land on one point of the shared " ~
                    "section polyline" };
    }
    const reversed = endPosition < startPosition;
    const low = reversed ? endPosition : startPosition;
    const high = reversed ? startPosition : endPosition;
    var firstInterior = 0;
    while (firstInterior < pointCount && firstInterior <= low + 1e-12)
    {
        firstInterior += 1;
    }
    var lastInterior = pointCount - 1;
    while (lastInterior >= 0 && lastInterior >= high - 1e-12)
    {
        lastInterior -= 1;
    }
    const interior = firstInterior <= lastInterior ? subArray(uvPoints, firstInterior, lastInterior + 1) : [];
    return {
            "failed" : false,
            "uvPoints" : concatenateArrays([[startUv], reversed ? reverse(interior) : interior, [endUv]]),
            "startPosition" : startPosition, "endPosition" : endPosition
        };
}

/**
 * Decompose one funnel component's boundary into rectangle-able strips (spec 7.1).
 *
 * input: {
 *     tStart, tEnd {number} : the component's t range,
 *     branches {array} : one branch anchor per lateral boundary curve of the component, in the
 *         shape anchorUvAtStation consumes ({ anchorKind : "branch", tSamples, uvSamples }) -
 *         marchStripZeroCurves' output carried to uv, which is the same array the adjacent sharp
 *         edge's lateral trim reads (spec 2.3),
 *     sectionTolerance {number, default 1e-12},
 *     capTolerance {number, default 1e-9} : how close a side's t extreme must be to the
 *         component's own t range end to count as landing on a cap,
 *     marchDivisions {number, default 24} : march steps per anchor distance, for the pairing and
 *         seam marches,
 *     gradeMergeEnds {boolean, default false} : also set gradeStart / gradeEnd on the strips whose
 *         t range ends at a merge. The merge is marked either way (mergeAtStart / mergeAtEnd);
 *         this only says whether to cluster stations there, which is measured NOT to help - see
 *         the section comment above
 * }
 *
 * Returns {
 *     failed, reason?,
 *     alternations {map} : componentBoundaryAlternations' record,
 *     sides {array} : every branch's t-monotone pieces, each carrying its branchIndex,
 *     splitTimes {array} : the interior cut times, ascending,
 *     bands {array} : { tStart, tEnd, sideIndices, pairs },
 *     strips {array} : each a map that merges straight into a fitEnvelopeComponent input -
 *         tStart, tEnd, startAnchor, endAnchor, startSectionPolyline / endSectionPolyline where a
 *         seam prescribes one, mergeAtStart / mergeAtEnd where that end is a merge (and
 *         gradeStart / gradeEnd to match, when gradeMergeEnds is on) - plus bandIndex and the two
 *         side indices,
 *     seams {array} : { t, coarseStripIndex, fineStripIndices, polylinePointCount }
 * }
 */
export function decomposeFunnelComponentIntoStrips(strippedMotion is map, strippedSurface is map,
    input is map) returns map
{
    const options = mergeMaps({ "sectionTolerance" : 1e-12, "capTolerance" : 1e-9,
                "marchDivisions" : 24, "gradeMergeEnds" : false }, input);
    const capTolerance = options.capTolerance;
    const domain = fitSurfaceKnotDomain(strippedSurface);

    // 1. Lateral branches to t-monotone sides, and their interior cut times.
    var sides = [];
    var splitTimes = [];
    for (var branchIndex = 0; branchIndex < size(input.branches); branchIndex += 1)
    {
        const split = splitBranchAtTimeExtrema(strippedMotion, strippedSurface,
            input.branches[branchIndex], options);
        if (split.failed)
        {
            return { "failed" : true, "reason" : split.reason };
        }
        for (var side in split.sides)
        {
            sides = append(sides, mergeMaps(side, { "branchIndex" : branchIndex }));
        }
        for (var extremum in split.extrema)
        {
            // A merge landing on a cap splits the branch but adds no band: there is no t range on
            // the far side of it.
            if (extremum.t > input.tStart + capTolerance && extremum.t < input.tEnd - capTolerance)
            {
                splitTimes = append(splitTimes, extremum.t);
            }
        }
    }
    const alternations = componentBoundaryAlternations(sides, input.tStart, input.tEnd, capTolerance);

    // 2. Bands. Cut times within capTolerance of one another are one event.
    const sortedTimes = sort(splitTimes, function(a, b)
        {
            return a - b;
        });
    var bandBounds = [input.tStart];
    for (var splitTime in sortedTimes)
    {
        if (abs(splitTime - bandBounds[size(bandBounds) - 1]) > capTolerance)
        {
            bandBounds = append(bandBounds, splitTime);
        }
    }
    bandBounds = append(bandBounds, input.tEnd);

    var bands = makeArray(size(bandBounds) - 1);
    var strips = [];
    for (var bandIndex = 0; bandIndex + 1 < size(bandBounds); bandIndex += 1)
    {
        const tLow = bandBounds[bandIndex];
        const tHigh = bandBounds[bandIndex + 1];
        var sideIndices = [];
        for (var sideIndex = 0; sideIndex < size(sides); sideIndex += 1)
        {
            if (sides[sideIndex].tMin <= tLow + capTolerance &&
                sides[sideIndex].tMax >= tHigh - capTolerance)
            {
                sideIndices = append(sideIndices, sideIndex);
            }
        }
        if (size(sideIndices) == 0 || size(sideIndices) % 2 != 0)
        {
            return { "failed" : true, "reason" : "the band t in [" ~ tLow ~ ", " ~ tHigh ~ "] is " ~
                        "bounded by " ~ size(sideIndices) ~ " lateral sides - a section's " ~
                        "endpoints come in pairs, so the component's boundary curves are " ~
                        "incomplete" };
        }
        const pairing = pairBandSides(strippedMotion, strippedSurface, 0.5 * (tLow + tHigh), sides,
            sideIndices, domain, options);
        if (pairing.failed)
        {
            return { "failed" : true, "reason" : pairing.reason };
        }
        bands[bandIndex] = { "tStart" : tLow, "tEnd" : tHigh, "sideIndices" : sideIndices,
                "pairs" : pairing.pairs };
        for (var pair in pairing.pairs)
        {
            strips = append(strips, {
                        "bandIndex" : bandIndex, "tStart" : tLow, "tEnd" : tHigh,
                        "startSideIndex" : sideIndices[pair[0]], "endSideIndex" : sideIndices[pair[1]],
                        "startAnchor" : sides[sideIndices[pair[0]]],
                        "endAnchor" : sides[sideIndices[pair[1]]],
                        "mergeAtStart" : false, "mergeAtEnd" : false,
                        "gradeStart" : false, "gradeEnd" : false
                    });
        }
    }

    // 3. Seams. At each interior cut time the merged side - whichever band carries fewer arcs -
    // marches the arc once, and the strips of the other band resample that same polyline.
    var seams = [];
    for (var boundIndex = 1; boundIndex + 1 < size(bandBounds); boundIndex += 1)
    {
        const seamTime = bandBounds[boundIndex];
        const lowerBand = boundIndex - 1;
        const upperBand = boundIndex;
        const coarseBand = size(bands[upperBand].pairs) < size(bands[lowerBand].pairs) ?
            upperBand : lowerBand;
        const fineBand = coarseBand == lowerBand ? upperBand : lowerBand;
        for (var coarseStripIndex = 0; coarseStripIndex < size(strips); coarseStripIndex += 1)
        {
            if (strips[coarseStripIndex].bandIndex != coarseBand)
            {
                continue;
            }
            const arc = marchSectionArcBetweenSides(strippedMotion, strippedSurface, seamTime,
                strips[coarseStripIndex].startAnchor, strips[coarseStripIndex].endAnchor, domain, options);
            if (arc.failed || !arc.reachedEnd)
            {
                return { "failed" : true, "reason" : arc.failed ? arc.reason :
                            ("the shared section march at the seam t = " ~ seamTime ~ " did not " ~
                            "reach its end anchor") };
            }
            var coarseStrip = strips[coarseStripIndex];
            if (coarseBand == lowerBand)
            {
                coarseStrip.endSectionPolyline = arc.uvPoints;
            }
            else
            {
                coarseStrip.startSectionPolyline = arc.uvPoints;
            }
            strips[coarseStripIndex] = coarseStrip;
            const onPolylineTolerance = 0.25 * arc.length / options.marchDivisions;
            var fineStripIndices = [];
            for (var fineStripIndex = 0; fineStripIndex < size(strips); fineStripIndex += 1)
            {
                if (strips[fineStripIndex].bandIndex != fineBand)
                {
                    continue;
                }
                var fineStrip = strips[fineStripIndex];
                const startHit = closestPositionOnPolyline(arc.uvPoints,
                    anchorUvAtStation(fineStrip.startAnchor, strippedMotion, strippedSurface,
                        seamTime, options.sectionTolerance));
                const endHit = closestPositionOnPolyline(arc.uvPoints,
                    anchorUvAtStation(fineStrip.endAnchor, strippedMotion, strippedSurface,
                        seamTime, options.sectionTolerance));
                if (max(startHit.squaredDistance, endHit.squaredDistance) > onPolylineTolerance ^ 2)
                {
                    continue;
                }
                if (fineBand == lowerBand)
                {
                    fineStrip.endSectionPolyline = arc.uvPoints;
                    fineStrip.mergeAtEnd = true;
                    fineStrip.gradeEnd = options.gradeMergeEnds;
                }
                else
                {
                    fineStrip.startSectionPolyline = arc.uvPoints;
                    fineStrip.mergeAtStart = true;
                    fineStrip.gradeStart = options.gradeMergeEnds;
                }
                strips[fineStripIndex] = fineStrip;
                fineStripIndices = append(fineStripIndices, fineStripIndex);
            }
            seams = append(seams, { "t" : seamTime, "coarseStripIndex" : coarseStripIndex,
                        "fineStripIndices" : fineStripIndices,
                        "polylinePointCount" : size(arc.uvPoints) });
        }
    }

    return { "failed" : false, "alternations" : alternations, "sides" : sides,
            "splitTimes" : subArray(bandBounds, 1, size(bandBounds) - 1), "bands" : bands,
            "strips" : strips, "seams" : seams };
}

/**
 * Fit every strip of a decomposed component (spec 7.1). Each strip record already carries the fit
 * input keys the decomposition owns - t range, anchors, the shared seam polylines, the grading
 * flags - so the caller supplies only what is common to all of them (tolerance, grid counts,
 * refinement budget).
 *
 * One caution on that shared tolerance: a strip with a merge end (`mergeAtStart` / `mergeAtEnd`)
 * has a square-root corner in its chart and its deviation does not fall cleanly with grid size, so
 * a target below what that chart reaches makes the refinement loop double to the budget cap
 * instead of converging (spec 7.10). Size those strips, or give them their own tolerance.
 *
 * Returns { failed, reason?, fits {array, index-aligned with the strips}, worstCertifiedBound,
 * budgetHit }.
 */
export function fitComponentStrips(strippedMotion is map, strippedSurface is map,
    decomposition is map, baseInput is map) returns map
{
    var fits = makeArray(size(decomposition.strips));
    var worstCertifiedBound = 0;
    var budgetHit = false;
    for (var stripIndex = 0; stripIndex < size(decomposition.strips); stripIndex += 1)
    {
        const fit = fitEnvelopeComponent(strippedMotion, strippedSurface,
            mergeMaps(baseInput, decomposition.strips[stripIndex]));
        if (fit.failed)
        {
            return { "failed" : true, "reason" : "strip " ~ stripIndex ~ ": " ~ fit.reason };
        }
        fits[stripIndex] = fit;
        worstCertifiedBound = max(worstCertifiedBound, fit.certifiedBound);
        budgetHit = budgetHit || fit.budgetHit;
    }
    return { "failed" : false, "fits" : fits, "worstCertifiedBound" : worstCertifiedBound,
            "budgetHit" : budgetHit };
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

/**
 * The two island motions, both translations with velocity (1, 0, wz(t)) over the bump fixture
 * islandFixtureSurface, whose z_u = 0.8 u(1-u) v(1-v) peaks at 0.05 at (0.5, 0.5). The contact
 * set is therefore the LEVEL SET z_u = wz(t), a closed loop for wz in (0, 0.05), and
 *     lambda = wz' + z_uu,   max |z_uu| on the loop = 0.2 sqrt(1 - 20 wz),
 * so lambda holds one sign exactly where |wz'| beats that.
 *
 * TWO POLES (the step-6 fixture): wz = 0.134 - 0.4t + 0.4t^2 dips below the peak on t in
 * (0.3, 0.7), so the island is born at t = 0.3 and dies at t = 0.7. |wz'| = |0.8t - 0.4| falls
 * to 0 at t = 0.5, so lambda spans both signs across t in (0.385, 0.615): the sweep is locally
 * self-intersecting there. That is not this fixture's accident - see emitIslandPatches.
 *
 * ONE POLE (the emission fixture): wz = 0.05 - 0.4t is born at t = 0 and the fit is CLIPPED at
 * t = 0.0375, where wz = 0.035 and max |z_uu| on the loop is 0.1095 - a fold margin of
 * (0.4 - 0.1095) / (0.4 + 0.1095) = 0.570 with lambda one-signed at -1 throughout. The island's
 * other end is not a pole at all: wz reaches 0 at t = 0.125, where the level set degenerates
 * onto the patch boundary, and nothing past the clip is fitted.
 */
const ISLAND_BUMP_VELOCITY_Z = [0.134, -0.066, 0.134];
const ISLAND_PEAK_HEIGHT_SLOPE = 0.05;      // max z_u of the bump, at (0.5, 0.5)
const ISLAND_CAP_SLOPE = 0.4;               // wz = 0.05 - 0.4 t
const ISLAND_CAP_VELOCITY_Z = [0.05, -0.15, -0.35];
const ISLAND_CAP_T_END = 0.0375;
const ISLAND_CAP_TOLERANCE = 5e-4;          // 8x8 measures 2.73e-4 in simulation, q-limited

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
function islandFixtureMembershipResidual(fitted is Vector, velocityZ is array) returns number
{
    const y = fitted[1];
    var best = 1e300;
    var bestU = 0;
    const scanCount = 96;
    for (var index = 0; index <= scanCount; index += 1)
    {
        const u = index / scanCount;
        const h = abs(islandFixtureHeightMismatch(u, y, fitted[0], fitted[2], velocityZ));
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
        const valueA = abs(islandFixtureHeightMismatch(probeA, y, fitted[0], fitted[2], velocityZ));
        const valueB = abs(islandFixtureHeightMismatch(probeB, y, fitted[0], fitted[2], velocityZ));
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

/** The island fixture's swept-family height mismatch at tool parameter u, under the motion
    whose z velocity is the given degree-2 Bernstein triple. */
function islandFixtureHeightMismatch(u is number, v is number, x is number, z is number,
    velocityZ is array) returns number
{
    const t = x - u;
    const toolZ = 0.8 * (u ^ 2 / 2 - u ^ 3 / 3) * v * (1 - v);
    return toolZ + quadraticVelocityIntegral(velocityZ, t) - z;
}

/**
 * The merge fixture (spec 7.1 strip decomposition). S = (u, v, 0.1(u^2/2 + 2u(v - 1/2)^2)) on the
 * unit square, translating with velocity (1, 0, 0.1 w(t)), w(t) = 1.2 - 0.4t. Since the surface
 * normal is (-z_u, -z_v, 1) and the velocity's y component is zero, the envelope function is
 * exactly f = 0.1(w(t) - u - 2(v - 1/2)^2): every section is the parabola
 * u = w(t) - 2(v - 1/2)^2, peaking at u = w(t) on the v = 1/2 meridian.
 *
 * That is the whole point of the fixture. While w > 1 the peak is outside the domain, so the
 * u = 1 wall cuts the section into TWO arcs; at w = 1 the section is tangent to the wall; below
 * it there is one arc. w(1/2) = 1, so the component's boundary carries two cap arcs at t = 0 and
 * one at t = 1 - six alternations - and it must decompose into two leg strips and one trunk
 * strip meeting at t = 1/2.
 */
const MERGE_FIXTURE_LEVEL_START = 1.2;    // w(0)
const MERGE_FIXTURE_LEVEL_RATE = 0.4;     // -w'
const MERGE_FIXTURE_PROFILE = 2;          // the (v - 1/2)^2 coefficient of z_u / SCALE
const MERGE_FIXTURE_SCALE = 0.1;          // height scale, so the fixture is 1 m wide and 0.1 m tall
const MERGE_FIXTURE_SPLIT_TIME = 0.5;     // (LEVEL_START - 1) / LEVEL_RATE - where w = 1

/** What each strip of the merge fixture certifies at, at the fixed grids the strip fit self test
    uses, with a 2x margin over the independently recomputed deviation there (leg 5.5e-4 at 8x8
    uniform, trunk 1.3e-4 at 6x16). These are not tolerances the fit is being asked to reach by
    refining - see the strip fit self test on why that loop must stay off for a merging strip. */
const LEG_STRIP_DEVIATION_LIMIT = 1.2e-3;
const TRUNK_STRIP_DEVIATION_LIMIT = 3e-4;


/**
 * The merge fixture surface: an exact degree (2, 2) Bezier patch. z is quadratic in each
 * direction, so the control net represents it exactly, and z_u is LINEAR in u - which is what
 * keeps every contact curve a closed-form parabola with no Newton anywhere in the answers this
 * fixture is checked against.
 */
function mergeFixtureSurface() returns map
{
    const uSquared = [0, 0, 1];
    const uLinear = [0, 0.5, 1];
    const vShifted = [0.5, -0.5, 0.5];        // Bernstein coefficients of 2 (v - 1/2)^2
    const greville2 = [0, 0.5, 1];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville2[i], greville2[j],
                    MERGE_FIXTURE_SCALE * (0.5 * uSquared[i] + uLinear[i] * vShifted[j]));
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

/** The merge fixture's motion: velocity (1, 0, SCALE w(t)) as the degree-2 Bernstein velocity
    translationMotionFromQuadraticVelocity takes - a linear a + bt is [a, a + b/2, a + b]. */
function mergeFixtureMotion() returns map
{
    const level = MERGE_FIXTURE_SCALE * MERGE_FIXTURE_LEVEL_START;
    const rate = MERGE_FIXTURE_SCALE * MERGE_FIXTURE_LEVEL_RATE;
    return translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0],
        [level, level - rate / 2, level - rate]);
}

/**
 * The merge fixture's lateral branch on the v = vEdge wall, in the shape strip marching hands a
 * branch to the fit. The crossing is u = w(t) - 2(vEdge - 1/2)^2, monotone in t, so this branch
 * never splits. Sampling it by UNIFORM t rather than uniform u is deliberate: it puts the
 * branch's end times exactly on the component's own t range ends, which is what lets a cap
 * station and a branch endpoint compare equal.
 */
function mergeFixtureCapBranch(vEdge is number) returns map
{
    const sampleCount = 21;
    var tSamples = makeArray(sampleCount);
    var uvSamples = makeArray(sampleCount);
    for (var index = 0; index < sampleCount; index += 1)
    {
        const t = 1 - index / (sampleCount - 1);
        tSamples[index] = t;
        uvSamples[index] = [MERGE_FIXTURE_LEVEL_START - MERGE_FIXTURE_LEVEL_RATE * t -
                MERGE_FIXTURE_PROFILE * (vEdge - 0.5) ^ 2, vEdge];
    }
    return { "anchorKind" : "branch", "tSamples" : tSamples, "uvSamples" : uvSamples };
}

/**
 * The merge fixture's u = 1 wall branch - the one that turns over. The two crossings
 * v = 1/2 +- h with h^2 = (w - 1) / 2 exist only while w > 1, and writing v - 1/2 = h(0) s gives
 * t = SPLIT_TIME (1 - s^2) exactly: a parabola in the branch parameter with its maximum at
 * s = 0. Sampled with an EVEN interval count so that maximum falls strictly between two samples
 * and the extremum refinement has real work to do.
 */
function mergeFixtureWallBranch() returns map
{
    const sampleCount = 20;
    const halfWidth = sqrt((MERGE_FIXTURE_LEVEL_START - 1) / MERGE_FIXTURE_PROFILE);
    var tSamples = makeArray(sampleCount);
    var uvSamples = makeArray(sampleCount);
    for (var index = 0; index < sampleCount; index += 1)
    {
        const s = -1 + 2 * index / (sampleCount - 1);
        tSamples[index] = MERGE_FIXTURE_SPLIT_TIME * (1 - s ^ 2);
        uvSamples[index] = [1, 0.5 + halfWidth * s];
    }
    return { "anchorKind" : "branch", "tSamples" : tSamples, "uvSamples" : uvSamples };
}

/**
 * Analytic envelope membership for the merge fixture. A fitted point (x, y, z) has v = y, and
 * x = u + t with u = w(t) - 2(v - 1/2)^2 solves for t outright,
 * t = (x - w(0) + 2(y - 1/2)^2) / (1 + w'), so the residual is the z mismatch against S(u, v)
 * plus the integrated z velocity. Closed form throughout - no Newton, and nothing borrowed from
 * the module under test.
 */
/**
 * The merge fixture's exact lifted contact point at (t, v): the section is
 * u = w(t) - 2(v - 1/2)^2 and the lift is S(u, v) plus the integrated translation, so the whole
 * contact arc at one station is a closed-form curve in v alone.
 */
function mergeFixtureArcPoint(t is number, v is number) returns Vector
{
    const shifted = MERGE_FIXTURE_PROFILE * (v - 0.5) ^ 2;
    const u = MERGE_FIXTURE_LEVEL_START - MERGE_FIXTURE_LEVEL_RATE * t - shifted;
    return vector(u + t, v, MERGE_FIXTURE_SCALE * (0.5 * u ^ 2 + u * shifted +
                MERGE_FIXTURE_LEVEL_START * t - 0.5 * MERGE_FIXTURE_LEVEL_RATE * t ^ 2));
}

/**
 * Distance from a point to that arc, by golden section on v. This is how a fitted patch's seam
 * edge is checked: against the arc itself, with no marched polyline in between - a chordal
 * polyline at this fixture's step size carries ~9e-4 of sagitta, which would swamp the tolerance
 * being measured.
 */
function mergeFixtureArcDistance(point is Vector, t is number) returns number
{
    const golden = (sqrt(5) - 1) / 2;
    var low = 0;
    var high = 1;
    var probeA = high - golden * (high - low);
    var probeB = low + golden * (high - low);
    var valueA = norm(mergeFixtureArcPoint(t, probeA) - point);
    var valueB = norm(mergeFixtureArcPoint(t, probeB) - point);
    for (var iteration = 0; iteration < 60; iteration += 1)
    {
        if (valueA < valueB)
        {
            high = probeB;
            probeB = probeA;
            valueB = valueA;
            probeA = high - golden * (high - low);
            valueA = norm(mergeFixtureArcPoint(t, probeA) - point);
        }
        else
        {
            low = probeA;
            probeA = probeB;
            valueA = valueB;
            probeB = low + golden * (high - low);
            valueB = norm(mergeFixtureArcPoint(t, probeB) - point);
        }
    }
    return min(valueA, valueB);
}

function mergeFixtureMembershipResidual(fitted is Vector) returns number
{
    const shifted = MERGE_FIXTURE_PROFILE * (fitted[1] - 0.5) ^ 2;
    const t = (fitted[0] - MERGE_FIXTURE_LEVEL_START + shifted) / (1 - MERGE_FIXTURE_LEVEL_RATE);
    const u = fitted[0] - t;
    const exactZ = MERGE_FIXTURE_SCALE * (0.5 * u ^ 2 + u * shifted +
            MERGE_FIXTURE_LEVEL_START * t - 0.5 * MERGE_FIXTURE_LEVEL_RATE * t ^ 2);
    return abs(fitted[2] - exactZ);
}

/** The worst analytic membership residual of a fitted merge-fixture patch, over interior
    parameters (the patch's own data rows are not sampled). */
function worstMergeFixtureResidual(fitSurface is map, samplesPerDirection is number) returns number
{
    const domain = fitSurfaceKnotDomain(fitSurface);
    var worst = 0;
    for (var i = 1; i < samplesPerDirection; i += 1)
    {
        for (var j = 1; j < samplesPerDirection; j += 1)
        {
            worst = max(worst, mergeFixtureMembershipResidual(evaluateBSplineSurfacePoint(fitSurface,
                            domain.uMin + (domain.uMax - domain.uMin) * i / samplesPerDirection,
                            domain.vMin + (domain.vMax - domain.vMin) * j / samplesPerDirection)));
        }
    }
    return worst;
}

/**
 * How many of a clipped section polyline's interior points came, bit for bit, from CONSECUTIVE
 * vertices of the polyline it was clipped out of. This is the exact-sharing check the strip
 * decomposition exists to make possible (spec 2.3): -1 means a point is not the shared
 * polyline's own number, or the run is not consecutive.
 */
function sharedPolylineRunLength(shared is array, clipped is array) returns number
{
    var previous = undefined;
    for (var index = 1; index + 1 < size(clipped); index += 1)
    {
        var found = undefined;
        for (var sharedIndex = 0; sharedIndex < size(shared); sharedIndex += 1)
        {
            if (shared[sharedIndex][0] == clipped[index][0] && shared[sharedIndex][1] == clipped[index][1])
            {
                found = sharedIndex;
                break;
            }
        }
        if (found == undefined || (previous != undefined && abs(found - previous) != 1))
        {
            return -1;
        }
        previous = found;
    }
    return max(0, size(clipped) - 2);
}

/** True when two uv sample arrays are the same numbers, element for element - not merely close. */
function uvSampleArraysIdentical(first is array, second is array) returns boolean
{
    if (size(first) != size(second))
    {
        return false;
    }
    for (var index = 0; index < size(first); index += 1)
    {
        if (first[index][0] != second[index][0] || first[index][1] != second[index][1])
        {
            return false;
        }
    }
    return true;
}
