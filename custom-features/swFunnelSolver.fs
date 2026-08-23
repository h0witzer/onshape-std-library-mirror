FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

// Non-standard imports. bernsteinPolynomialUtils and swEnvelopeMath are same-document element
// imports (unpublished on purpose - see swEnvelopeMath.fs); their ids churn on every re-paste,
// so bump these when the owner re-pastes either module. splineRefinementUtils is the published
// cross-document pin. NOTE for MCP harness runs: same-document imports cannot resolve from the
// harness document; the test payload inlines both module bodies in place of these two lines.
import(path : "8b495c3bb1037b467ca1d02e", version : "3968d1ef5b507302198a917b"); //bernsteinPolynomialUtils.fs
import(path : "eede4083ca591e1a7adb8440", version : "3968d1ef5b507302198a917b"); //swEnvelopeMath.fs (owner combined tab; fix version on paste)
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * SOLID SWEEP - funnel solver (spec: docs/specs/SOLID_SWEEP_SPEC.md sections 6.3 and 6.4).
 * Pure module: no Context anywhere. Sits on swEnvelopeMath's factored representation and on
 * the splineRefinementUtils evaluators; consumes the unit-stripped records swSweepEmit builds.
 *
 * The layers, in the papers' dimension-increasing order:
 *   - Sliding audit (section 6.4): per (patch x t-span) block, detect |f| ~ 0 over the whole
 *     block - the face slides along itself there. Runs BEFORE any other solver stage; the
 *     caller routes benign subcases and rejects the rest.
 *   - Factored cell isolation: recursive subdivision of a block in (u, v, t) that splits the
 *     S and N coefficient grids and the twelve t-polynomials per cell and re-screens with
 *     range products at every node - a dead verdict is a certificate, and f is never
 *     materialized during the descent. Output: the leaf cells that may touch the grazing set.
 *   - Vertex layer: 1D contact-function roots from sign-change brackets on a station grid,
 *     refined by bisection-safeguarded Newton; sharp-vertex contact intervals from the sign
 *     pattern across the vertex's cone normals.
 *   - Co-edge layer: the strip function g(s, t) on a co-edge side's SHARED sample arrays -
 *     per-column roots in t chained into branches across neighboring samples (the marching
 *     happens at exactly the shared s samples, which is what keeps seams exact downstream).
 *   - Funnel census: a coarse value grid over D x I evaluated from screened/materialized
 *     blocks (never pointwise), trim masking by even-odd ray crossings, flood fill over
 *     mixed-sign cells with an explicit u-seam wrap for periodic faces, and component
 *     classification (boundary-touching vs grazing island, seam-crossing flagged).
 *   - Island refinement: 3-variable Newton on (f, f_u, f_v) = 0 with EXACT partials read off
 *     differentiated coefficient nets of a materialized block - t-extremes of grazing islands
 *     to machine precision, no sampling.
 *   - Section layer: predictor-corrector marching of f(., ., t) = 0 in (u, v) between
 *     boundary anchors, arc-length resampling to fixed fractions q, and the rigid lift
 *     Phi = A(t) S(u, v) + b(t).
 *
 * Periodic seam doctrine (deferred from step 4): the census treats u as cyclic when the face
 * is u-periodic, so a component crossing the extraction seam is ONE component with
 * crossesUSeam set. Downstream (fit assembly, step 6) that flag triggers the rewindow
 * strategy: rewindow the face via splineRefinementUtils so the funnel band avoids the seam -
 * which invalidates the face's affine UV calibration, so crossings fall back to 3D point
 * inversion - or pre-split the band at an isocurve.
 *
 * Both self-test features (pointwise and factored) are selection-free and context-free
 * (fixtures are hand-built stripped maps with exact analytic answers), so the MCP harness
 * runs each in one call - split in two so each harness payload stays within size discipline.
 */

// ============================= Funnel Solver Self Test =============================

annotation { "Feature Type Name" : "Sweep Funnel Pointwise Self Test" }
export const sweepFunnelPointwiseSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // ---------- Pointwise layer ----------

        // Contact roots: constant-rotation motion whose velocity x-component is
        // (t - 0.3)(t - 0.7) - roots at exactly 0.3 and 0.7.
        const parabolicMotion = translationMotionFromQuadraticVelocity(
            [0.21, -0.29, 0.21], [0, 0, 0], [1, 1, 1], singleSpanKnots);
        const parabolicRoots = findContactFunctionRoots(parabolicMotion,
            vector(1, 0, 0), vector(0, 0, 0), 0, 1, 16, 1e-13);
        if (size(parabolicRoots) != 2 ||
            abs(parabolicRoots[0].t - 0.3) > 1e-10 || abs(parabolicRoots[1].t - 0.7) > 1e-10)
        {
            failures = failures ~ " contact roots expected {0.3, 0.7}, got " ~ toString(parabolicRoots) ~ ".";
        }
        else
        {
            println("[FUNNEL SELF TEST] contact roots at " ~ parabolicRoots[0].t ~ ", " ~ parabolicRoots[1].t ~
                " (errors " ~ abs(parabolicRoots[0].t - 0.3) ~ ", " ~ abs(parabolicRoots[1].t - 0.7) ~ ")");
        }

        // Contact-function time derivative against a central difference on a varying,
        // non-orthogonal motion (every term of g_t exercised).
        const twistingMotion = {
                "columnX" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(1, 0, 0), vector(0.95, 0.15, 0.02), vector(0.88, 0.28, 0.06), vector(0.8, 0.4, 0.1)]
                },
                "columnY" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 1, 0), vector(-0.12, 0.97, 0.05), vector(-0.24, 0.92, 0.09), vector(-0.35, 0.85, 0.14)]
                },
                "columnZ" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 1), vector(0.03, -0.06, 0.99), vector(0.07, -0.11, 0.97), vector(0.12, -0.18, 0.93)]
                },
                "translation" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0.02, 0.01, 0.01), vector(0.05, 0.015, 0.03), vector(0.09, 0.02, 0.04)]
                }
            };
        const derivativeNormal = vector(0.2, -0.3, 0.93);
        const derivativePoint = vector(0.04, 0.02, 0.01);
        var worstDerivativeDisagreement = 0;
        for (var tSample in [0.22, 0.5, 0.81])
        {
            const analytic = evaluateContactFunctionTimeDerivative(twistingMotion, derivativeNormal, derivativePoint, tSample);
            const step = 1e-5;
            const central = (evaluateContactFunctionAtPoint(twistingMotion, derivativeNormal, derivativePoint, tSample + step) -
                    evaluateContactFunctionAtPoint(twistingMotion, derivativeNormal, derivativePoint, tSample - step)) / (2 * step);
            const disagreement = abs(analytic - central) / (1 + abs(central));
            worstDerivativeDisagreement = max(worstDerivativeDisagreement, disagreement);
        }
        println("[FUNNEL SELF TEST] g_t vs central difference worst relative disagreement: " ~ worstDerivativeDisagreement);
        if (worstDerivativeDisagreement > 1e-7)
        {
            failures = failures ~ " g_t disagrees with the central difference by " ~ worstDerivativeDisagreement ~ ".";
        }

        // Vertex contact intervals: cone normals (1,0,0) and (0,0,1) under the parabolic
        // motion - s_1 goes negative exactly on (0.3, 0.7), s_2 stays positive.
        const vertexIntervals = solveVertexContactIntervals(parabolicMotion,
            [vector(1, 0, 0), vector(0, 0, 1)], vector(0, 0, 0), 0, 1, 16, 1e-13);
        if (size(vertexIntervals) != 1 ||
            abs(vertexIntervals[0].tStart - 0.3) > 1e-9 || abs(vertexIntervals[0].tEnd - 0.7) > 1e-9)
        {
            failures = failures ~ " vertex contact interval expected (0.3, 0.7), got " ~ toString(vertexIntervals) ~ ".";
        }
        else
        {
            println("[FUNNEL SELF TEST] vertex contact interval (" ~ vertexIntervals[0].tStart ~ ", " ~
                vertexIntervals[0].tEnd ~ ")");
        }

        // Strip marching: radial normals around a circle, velocity direction swinging from
        // (1, 0.8) to (-1, 0.8) - g(s, t) = cos(2 pi s)(1 - 2t) + 0.8 sin(2 pi s). The zero
        // set inside the strip is three branches (near s = 0, s = 0.5, s = 1); every root is
        // analytic: t = (1 + 0.8 tan(2 pi s)) / 2.
        const swingMotion = translationMotionFromQuadraticVelocity(
            [1, 0, -1], [0.8, 0.8, 0.8], [0, 0, 0], singleSpanKnots);
        const stripSampleCount = 33;
        var stripNormals = makeArray(stripSampleCount);
        var stripPoints = makeArray(stripSampleCount);
        for (var sampleIndex = 0; sampleIndex < stripSampleCount; sampleIndex += 1)
        {
            const angle = 360 * degree * sampleIndex / (stripSampleCount - 1);
            stripNormals[sampleIndex] = vector(cos(angle), sin(angle), 0);
            stripPoints[sampleIndex] = 0.02 * stripNormals[sampleIndex];
        }
        const stripBranches = marchStripZeroCurves(swingMotion, stripNormals, stripPoints, 0, 1, 21, 1e-12);
        var branchSizes = "";
        var worstStripError = 0;
        for (var branch in stripBranches)
        {
            branchSizes = branchSizes ~ " " ~ size(branch.samples);
            for (var sample in branch.samples)
            {
                const angle = 360 * degree * sample.sampleIndex / (stripSampleCount - 1);
                const exact = (1 + 0.8 * tan(angle)) / 2;
                worstStripError = max(worstStripError, abs(sample.t - exact));
            }
        }
        println("[FUNNEL SELF TEST] strip branches sizes:" ~ branchSizes ~ "; worst root error vs analytic: " ~ worstStripError);
        if (size(stripBranches) != 3 || worstStripError > 1e-9)
        {
            failures = failures ~ " strip marching expected 3 branches matching the analytic roots (sizes" ~
                branchSizes ~ ", worst error " ~ worstStripError ~ ").";
        }

        // Envelope gradient: every component against central differences of the INDEPENDENT
        // pointwise path, on a curved surface under a varying non-orthogonal motion (the
        // rotation terms of f_u and f_v are only exercised here).
        var worstGradientDisagreement = 0;
        for (var gradientProbe in [[0.35, 0.6, 0.45], [0.7, 0.3, 0.2]])
        {
            const gradient = evaluateEnvelopeGradientPointwise(twistingMotion, islandFixtureSurface(),
                gradientProbe[0], gradientProbe[1], gradientProbe[2]);
            const step = 1e-5;
            for (var axisIndex = 0; axisIndex < 3; axisIndex += 1)
            {
                var lowProbe = gradientProbe;
                var highProbe = gradientProbe;
                lowProbe[axisIndex] = lowProbe[axisIndex] - step;
                highProbe[axisIndex] = highProbe[axisIndex] + step;
                const central = (evaluateEnvelopePointwise(twistingMotion, islandFixtureSurface(),
                            highProbe[0], highProbe[1], highProbe[2]) -
                        evaluateEnvelopePointwise(twistingMotion, islandFixtureSurface(),
                            lowProbe[0], lowProbe[1], lowProbe[2])) / (2 * step);
                const analytic = axisIndex == 0 ? gradient.uDerivative :
                    (axisIndex == 1 ? gradient.vDerivative : gradient.tDerivative);
                worstGradientDisagreement = max(worstGradientDisagreement,
                    abs(analytic - central) / (1 + abs(central)));
            }
        }
        println("[FUNNEL SELF TEST] envelope gradient vs central differences worst relative disagreement: " ~
            worstGradientDisagreement);
        if (worstGradientDisagreement > 1e-6)
        {
            failures = failures ~ " envelope gradient disagrees with central differences by " ~
                worstGradientDisagreement ~ ".";
        }

        // Section marching: S = (u, v, 0.15u^2 - 0.18u + 0.05uv) under velocity (1, 0, 0.06)
        // gives f = 0.24 - 0.3u - 0.05v - the section is the exact line u = 0.8 - v/6.
        const slantSurface = slantFixtureSurface();
        const slantMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.06, 0.06, 0.06], singleSpanKnots);
        const sectionMarch = marchSectionCurve(slantMotion, slantSurface, 0.5,
            [0.8, 0], [0.8 - 1 / 6, 1], { "stepSize" : 0.05, "maxSteps" : 200, "tolerance" : 1e-12 });
        var worstSectionLineError = 0;
        for (var uvPoint in sectionMarch.uvPoints)
        {
            worstSectionLineError = max(worstSectionLineError, abs(0.24 - 0.3 * uvPoint[0] - 0.05 * uvPoint[1]));
        }
        println("[FUNNEL SELF TEST] section march: " ~ size(sectionMarch.uvPoints) ~ " points, reachedEnd " ~
            sectionMarch.reachedEnd ~ ", worst |f| on line: " ~ worstSectionLineError);
        if (!sectionMarch.reachedEnd || worstSectionLineError > 1e-9)
        {
            failures = failures ~ " section marching failed (reachedEnd " ~ sectionMarch.reachedEnd ~
                ", worst |f| " ~ worstSectionLineError ~ ").";
        }

        const resampled = resampleAndPolishSection(slantMotion, slantSurface, 0.5, sectionMarch.uvPoints, 9, 1e-12);
        const expectedFirstLift = vector(1.3, 0, -0.018);
        const firstLiftError = norm(resampled.liftedSamples[0] - expectedFirstLift);
        println("[FUNNEL SELF TEST] section resample: worst residual " ~ resampled.worstResidual ~
            ", first lift error vs hand value: " ~ firstLiftError);
        if (size(resampled.uvSamples) != 9 || resampled.worstResidual > 1e-10 || firstLiftError > 1e-9)
        {
            failures = failures ~ " section resample/lift failed (residual " ~ resampled.worstResidual ~
                ", lift error " ~ firstLiftError ~ ").";
        }

        reportTestVerdict(context, id, "FUNNEL POINTWISE SELF TEST", failures,
            "contact roots, g_t, vertex intervals, strip marching, envelope gradient, and " ~
            "section march/resample/lift all match their analytic answers.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Factored Self Test" }
export const sweepFunnelFactoredSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // Sliding audit: a flat plane under in-plane translation slides everywhere; under
        // normal translation it is screen-dead with no sliding.
        const planeSurface = planeFixtureSurface();
        const planeFactors = buildEnvelopePatchFactors(planeSurface);
        const inPlaneMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0.3, 0.3, 0.3], [0, 0, 0], singleSpanKnots);
        const inPlaneAudit = auditEnvelopeSliding(planeFactors, buildMotionSpanPolynomials(inPlaneMotion), 1e-12);
        const normalMotion = translationMotionFromQuadraticVelocity(
            [0, 0, 0], [0, 0, 0], [1, 1, 1], singleSpanKnots);
        const normalAudit = auditEnvelopeSliding(planeFactors, buildMotionSpanPolynomials(normalMotion), 1e-12);
        println("[FUNNEL SELF TEST] sliding audit: in-plane slides " ~ inPlaneAudit.slides ~
            " (" ~ size(inPlaneAudit.slidingBlocks) ~ " blocks); normal slides " ~ normalAudit.slides ~
            ", live " ~ size(normalAudit.liveBlocks) ~ ", dead " ~ normalAudit.deadBlockCount);
        if (!inPlaneAudit.slides || size(inPlaneAudit.slidingBlocks) != 1)
        {
            failures = failures ~ " in-plane translation not reported as sliding.";
        }
        if (normalAudit.slides || size(normalAudit.liveBlocks) != 0 || normalAudit.deadBlockCount != 1)
        {
            failures = failures ~ " normal translation should be screen-dead with no sliding.";
        }

        // The island fixture: z_u = 0.8 u(1-u) v(1-v) peaks at 0.05 in the patch center;
        // w = (1, 0, wz(t)) with wz = 0.05 + 0.4 (t-0.3)(t-0.7) dips below the peak exactly
        // for t in (0.3, 0.7). f = wz(t) - z_u(u, v): a grazing island born and dying at
        // exactly (u, v, t) = (0.5, 0.5, 0.3) and (0.5, 0.5, 0.7).
        const islandSurface = islandFixtureSurface();
        const islandFactors = buildEnvelopePatchFactors(islandSurface);
        const islandMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.134, -0.066, 0.134], singleSpanKnots);
        const islandSpans = buildMotionSpanPolynomials(islandMotion);
        const islandPatch = islandFactors.patches[0][0];

        // Factored cell isolation: live cells must cover the four exact contact points and
        // stay inside the true t-window (plus screening slack).
        const isolation = isolateEnvelopeCells(islandPatch, islandSpans[0],
            { "minCellWidthU" : 1 / 8, "minCellWidthV" : 1 / 8, "minCellWidthT" : 1 / 8, "valueTolerance" : 0 });
        const contactAnchors = [[0.5, 0.5, 0.3], [0.5, 0.5, 0.7], [0.21716, 0.5, 0.5], [0.78284, 0.5, 0.5]];
        var anchorsCovered = 0;
        for (var anchor in contactAnchors)
        {
            for (var cell in isolation.liveCells)
            {
                if (anchor[0] >= cell.globalUStart - 1e-12 && anchor[0] <= cell.globalUEnd + 1e-12 &&
                    anchor[1] >= cell.globalVStart - 1e-12 && anchor[1] <= cell.globalVEnd + 1e-12 &&
                    anchor[2] >= cell.globalTStart - 1e-12 && anchor[2] <= cell.globalTEnd + 1e-12)
                {
                    anchorsCovered += 1;
                    break;
                }
            }
        }
        var cellsOutsideTimeWindow = 0;
        for (var cell in isolation.liveCells)
        {
            if (cell.globalTEnd < 0.15 || cell.globalTStart > 0.85)
            {
                cellsOutsideTimeWindow += 1;
            }
        }
        println("[FUNNEL SELF TEST] cell isolation: " ~ size(isolation.liveCells) ~ " live cells, " ~
            isolation.screenedCount ~ " screens, " ~ isolation.deadCount ~ " dead; anchors covered " ~
            anchorsCovered ~ "/4; cells outside t-window " ~ cellsOutsideTimeWindow);
        if (anchorsCovered != 4 || cellsOutsideTimeWindow != 0 || size(isolation.liveCells) == 0)
        {
            failures = failures ~ " cell isolation missed contact anchors (" ~ anchorsCovered ~
                "/4) or leaked cells outside the t-window (" ~ cellsOutsideTimeWindow ~ ").";
        }

        // Subdivision consistency at the block level: the native split-matrix children must
        // reproduce the parent exactly.
        const islandBlock = materializeEnvelopeBlock(buildEnvelopePatchProducts(islandPatch), islandSpans[0]);
        var worstSplitDisagreement = 0;
        for (var m = 0; m < size(islandBlock); m += 1)
        {
            const uSplit = subdivideBernsteinGridU(islandBlock[m], 0.5);
            const vSplit = subdivideBernsteinGridV(islandBlock[m], 0.5);
            for (var probePair in [[0.31, 0.77], [0.62, 0.18]])
            {
                // low(x) reproduces parent(x / 2); high(y) reproduces parent(0.5 + y / 2).
                worstSplitDisagreement = max(worstSplitDisagreement,
                    abs(evaluateBernsteinGrid(uSplit.low, probePair[0], probePair[1]) -
                        evaluateBernsteinGrid(islandBlock[m], probePair[0] / 2, probePair[1])));
                worstSplitDisagreement = max(worstSplitDisagreement,
                    abs(evaluateBernsteinGrid(vSplit.high, probePair[0], probePair[1]) -
                        evaluateBernsteinGrid(islandBlock[m], probePair[0], 0.5 + probePair[1] / 2)));
            }
        }
        println("[FUNNEL SELF TEST] block-level subdivision consistency: " ~ worstSplitDisagreement);
        if (worstSplitDisagreement > 1e-13)
        {
            failures = failures ~ " native grid subdivision disagrees with the parent by " ~ worstSplitDisagreement ~ ".";
        }

        // Island t-extremes: exact coefficient-net Newton must land on (0.5, 0.5, 0.3) and
        // (0.5, 0.5, 0.7) from nearby seeds, and the results must satisfy the INDEPENDENT
        // pointwise envelope function to machine precision.
        var worstExtremeError = 0;
        var worstExtremeResidual = 0;
        for (var extremeCase in [{ "seed" : [0.46, 0.55, 0.34], "expected" : [0.5, 0.5, 0.3] },
                { "seed" : [0.54, 0.46, 0.66], "expected" : [0.5, 0.5, 0.7] }])
        {
            const refined = refineBlockStationaryPoint(islandBlock,
                extremeCase.seed[0], extremeCase.seed[1], extremeCase.seed[2],
                { "iterationLimit" : 30, "stepTolerance" : 1e-13 });
            if (!refined.converged)
            {
                failures = failures ~ " island extreme Newton failed to converge from seed " ~
                    toString(extremeCase.seed) ~ ".";
                continue;
            }
            for (var axisIndex = 0; axisIndex < 3; axisIndex += 1)
            {
                const refinedValue = axisIndex == 0 ? refined.localU : (axisIndex == 1 ? refined.localV : refined.localT);
                worstExtremeError = max(worstExtremeError, abs(refinedValue - extremeCase.expected[axisIndex]));
            }
            worstExtremeResidual = max(worstExtremeResidual,
                abs(evaluateEnvelopePointwise(islandMotion, islandSurface, refined.localU, refined.localV, refined.localT)));
        }
        println("[FUNNEL SELF TEST] island t-extremes: worst coordinate error " ~ worstExtremeError ~
            ", worst pointwise |f| " ~ worstExtremeResidual);
        if (worstExtremeError > 1e-9 || worstExtremeResidual > 1e-12)
        {
            failures = failures ~ " island t-extreme refinement missed (coordinate error " ~ worstExtremeError ~
                ", pointwise residual " ~ worstExtremeResidual ~ ").";
        }

        reportTestVerdict(context, id, "FUNNEL FACTORED SELF TEST", failures,
            "sliding audit, factored cell isolation, native subdivision, and island " ~
            "t-extremes all match their analytic answers.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Census Self Test" }
export const sweepFunnelCensusSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // The same island fixture as the factored self test (see there for the algebra).
        const islandSurface = islandFixtureSurface();
        const islandFactors = buildEnvelopePatchFactors(islandSurface);
        const islandMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.134, -0.066, 0.134], singleSpanKnots);
        const islandSpans = buildMotionSpanPolynomials(islandMotion);

        // Census: the island fixture has exactly one component, a true grazing island.
        const censusOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 17,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false };
        const islandCensus = censusFunnelComponents(islandFactors, islandSpans, censusOptions);
        println("[FUNNEL SELF TEST] island census: " ~ size(islandCensus.components) ~ " component(s)" ~
            (size(islandCensus.components) == 1 ? (", isIsland " ~ islandCensus.components[0].isIsland) : ""));
        if (size(islandCensus.components) != 1 || !islandCensus.components[0].isIsland)
        {
            failures = failures ~ " island census expected exactly one island component.";
        }

        // Census with a square trim mask [0.35, 0.65]^2: the island's mid-life loop lies
        // entirely outside the valid square, so the shell splits into two trim-touching caps.
        var squareLoop = [[0.35, 0.35], [0.65, 0.35], [0.65, 0.65], [0.35, 0.65]];
        var trimmedOptions = censusOptions;
        trimmedOptions.trimLoops = [squareLoop];
        const trimmedCensus = censusFunnelComponents(islandFactors, islandSpans, trimmedOptions);
        var trimTouchCount = 0;
        var trimmedIslandCount = 0;
        for (var componentRecord in trimmedCensus.components)
        {
            if (componentRecord.touchesTrimBoundary)
            {
                trimTouchCount += 1;
            }
            if (componentRecord.isIsland)
            {
                trimmedIslandCount += 1;
            }
        }
        println("[FUNNEL SELF TEST] trimmed census: " ~ size(trimmedCensus.components) ~
            " components, " ~ trimTouchCount ~ " trim-touching, " ~ trimmedIslandCount ~ " islands");
        if (size(trimmedCensus.components) != 2 || trimTouchCount != 2 || trimmedIslandCount != 0)
        {
            failures = failures ~ " trimmed census expected two trim-touching caps.";
        }

        // Winding-number spot checks on a concave polygon.
        const concaveLoop = [[0, 0], [1, 0], [1, 1], [0.5, 0.3], [0, 1]];
        if (!uvPointInsideLoops([concaveLoop], 0.1, 0.1) || !uvPointInsideLoops([concaveLoop], 0.25, 0.5) ||
            uvPointInsideLoops([concaveLoop], 0.5, 0.9) || uvPointInsideLoops([concaveLoop], 2, 0.5))
        {
            failures = failures ~ " even-odd point classification failed on the concave polygon.";
        }

        // Periodic seam: z_u = 0.8 (u - 0.5)^2 v(1-v) under constant w = (1, 0, 0.02) puts one
        // contact lobe against each u edge (the same physical band on a closed face). Without
        // the wrap the census sees two boundary components; with uPeriodic they are ONE
        // seam-crossing component.
        const seamSurface = seamFixtureSurface();
        const seamFactors = buildEnvelopePatchFactors(seamSurface);
        const seamMotion = translationMotionFromQuadraticVelocity(
            [1, 1, 1], [0, 0, 0], [0.02, 0.02, 0.02], singleSpanKnots);
        const seamSpans = buildMotionSpanPolynomials(seamMotion);
        // The seam fixture is t-constant, so a coarse t axis suffices.
        var seamOptions = censusOptions;
        seamOptions.tNodesPerSpan = 5;
        const openCensus = censusFunnelComponents(seamFactors, seamSpans, seamOptions);
        var periodicOptions = seamOptions;
        periodicOptions.uPeriodic = true;
        const wrappedCensus = censusFunnelComponents(seamFactors, seamSpans, periodicOptions);
        println("[FUNNEL SELF TEST] seam census: open " ~ size(openCensus.components) ~
            " component(s); wrapped " ~ size(wrappedCensus.components) ~ " component(s)" ~
            (size(wrappedCensus.components) == 1 ? (", crossesUSeam " ~ wrappedCensus.components[0].crossesUSeam) : ""));
        if (size(openCensus.components) != 2 || size(wrappedCensus.components) != 1 ||
            !wrappedCensus.components[0].crossesUSeam)
        {
            failures = failures ~ " periodic seam census expected 2 open / 1 wrapped seam-crossing component.";
        }

        reportTestVerdict(context, id, "FUNNEL CENSUS SELF TEST", failures,
            "census components (plain island, trimmed caps, periodic seam wrap) and the " ~
            "even-odd trim classification all match their analytic answers.");
    });

annotation { "Feature Type Name" : "Sweep Funnel Mask Self Test" }
export const sweepFunnelMaskSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // The two census masks: the cyclic trim mask a closed face needs, and the degeneracy
        // mask that keeps a collapsed net boundary from joining every component through itself.
        // Selection-free; the fixtures' answers are all closed-form. Split from the census self
        // test so each feature's step count stays inside one regeneration budget.
        var failures = "";
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];

        // Two trim loops that WIND the u seam, bounding the band 0.25 + 0.05 sin(2 pi u) to 0.75.
        // Both are given as { points, winding } records, which is what buildFaceTrimLoops emits.
        const lowerTrim = { "points" : sinusoidalSeamLoop(0.25, 0.05, 32), "winding" : 1 };
        const upperTrim = { "points" : sinusoidalSeamLoop(0.75, 0, 32), "winding" : 1 };
        const band = [lowerTrim, upperTrim];
        var bandFailures = 0;
        for (var u in [0, 0.2, 0.5, 0.95])
        {
            if (!uvPointInsideLoopsCyclic(band, u, 0.5, 1))
            {
                bandFailures += 1;
            }
            if (uvPointInsideLoopsCyclic(band, u, 0.05, 1) || uvPointInsideLoopsCyclic(band, u, 0.95, 1))
            {
                bandFailures += 1;
            }
        }
        // At u = 0.25 the wavy trim sits at exactly 0.30, so 0.28 is out and 0.32 is in - the
        // mask resolves the loop's shape, not just its average height.
        if (uvPointInsideLoopsCyclic(band, 0.25, 0.28, 1) || !uvPointInsideLoopsCyclic(band, 0.25, 0.32, 1))
        {
            bandFailures += 1;
        }
        println("[FUNNEL MASK SELF TEST] cyclic band: " ~ bandFailures ~ " misclassification(s) of 13");
        if (bandFailures != 0)
        {
            failures = failures ~ " the cyclic band mask misclassified " ~ bandFailures ~ " point(s).";
        }

        // A hole centred on the seam, unwrapped to u in [-0.06, 0.06] and handed over as a bare
        // point array - the census reads that shape as a closed loop of winding zero. It must
        // mask from BOTH sides of the seam, which is what the periodic images are for.
        const seamHole = seamStraddlingHole(0.06, 24);
        const bandWithHole = [lowerTrim, upperTrim, seamHole];
        var holeFailures = 0;
        for (var u in [0, 0.03, 0.97, 0.999])
        {
            if (uvPointInsideLoopsCyclic(bandWithHole, u, 0.5, 1))
            {
                holeFailures += 1;
            }
        }
        for (var u in [0.2, 0.5, 0.8])
        {
            if (!uvPointInsideLoopsCyclic(bandWithHole, u, 0.5, 1))
            {
                holeFailures += 1;
            }
        }
        println("[FUNNEL MASK SELF TEST] seam-straddling hole: " ~ holeFailures ~
            " misclassification(s) of 7");
        if (holeFailures != 0)
        {
            failures = failures ~ " the seam-straddling hole was misclassified at " ~ holeFailures ~
                " point(s).";
        }

        // The seam fixture again (see the census self test for its algebra), now masked. First a
        // band that excludes nothing: every node runs through the crossing test and not one
        // verdict may change.
        const seamSurface = seamFixtureSurface();
        const seamFactors = buildEnvelopePatchFactors(seamSurface);
        const seamSpans = buildMotionSpanPolynomials(translationMotionFromQuadraticVelocity(
                    [1, 1, 1], [0, 0, 0], [0.02, 0.02, 0.02], singleSpanKnots));
        var seamOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 5,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : true };
        const unmasked = censusFunnelComponents(seamFactors, seamSpans, seamOptions);
        var wideOptions = seamOptions;
        wideOptions.trimLoops = [{ "points" : sinusoidalSeamLoop(-0.1, 0, 32), "winding" : 1 },
                { "points" : sinusoidalSeamLoop(1.1, 0, 32), "winding" : 1 }];
        const wideMasked = censusFunnelComponents(seamFactors, seamSpans, wideOptions);
        println("[FUNNEL MASK SELF TEST] seam fixture unmasked: " ~ size(unmasked.components) ~
            " component(s), " ~ unmasked.components[0].cellCount ~ " cells; band excluding nothing: " ~
            size(wideMasked.components) ~ " component(s), " ~
            (size(wideMasked.components) == 1 ? (wideMasked.components[0].cellCount ~ " cells, trim " ~
                    wideMasked.components[0].touchesTrimBoundary ~ ", seam " ~
                    wideMasked.components[0].crossesUSeam) : ""));
        if (size(unmasked.components) != 1 || unmasked.components[0].cellCount != 80 ||
            size(wideMasked.components) != 1 || wideMasked.components[0].cellCount != 80 ||
            wideMasked.components[0].touchesTrimBoundary || !wideMasked.components[0].crossesUSeam)
        {
            failures = failures ~ " a trim band that excludes nothing changed the seam census.";
        }

        // Now a band that cuts: v in [0.3, 0.7] keeps only the two mid-v arcs of the contact
        // curve, which no longer meet across the seam - one wrapped component becomes two
        // trim-touching ones.
        var tightOptions = seamOptions;
        tightOptions.trimLoops = [{ "points" : sinusoidalSeamLoop(0.3, 0, 32), "winding" : 1 },
                { "points" : sinusoidalSeamLoop(0.7, 0, 32), "winding" : 1 }];
        const tightMasked = censusFunnelComponents(seamFactors, seamSpans, tightOptions);
        var tightTrimTouching = 0;
        var tightSeamCrossing = 0;
        var tightCells = 0;
        for (var component in tightMasked.components)
        {
            tightTrimTouching += component.touchesTrimBoundary ? 1 : 0;
            tightSeamCrossing += component.crossesUSeam ? 1 : 0;
            tightCells += component.cellCount;
        }
        println("[FUNNEL MASK SELF TEST] band [0.3, 0.7]: " ~ size(tightMasked.components) ~
            " component(s), " ~ tightCells ~ " cells total, " ~ tightTrimTouching ~
            " trim-touching, " ~ tightSeamCrossing ~ " seam-crossing");
        if (size(tightMasked.components) != 2 || tightCells != 16 || tightTrimTouching != 2 ||
            tightSeamCrossing != 0)
        {
            failures = failures ~ " the cutting trim band did not split the seam component in two.";
        }

        // The degeneracy mask. On the pole fixture f = v (1 - a(t) c'(u)) with c' = 0.6 u (1 - u)
        // and a(t) = 8 + 12 t, so the contact set is two sheets at u = 0.5 +/- sqrt(0.25 - 1 /
        // (0.6 a)) - well inside the domain and separate everywhere except v = 0, where the
        // collapsed control row makes f vanish identically. Unmasked, that zero slab joins them.
        const poleSurface = poleFixtureSurface();
        const poleFactors = buildEnvelopePatchFactors(poleSurface);
        const poleSpans = buildMotionSpanPolynomials(translationMotionFromQuadraticVelocity(
                    [8, 14, 20], [0, 0, 0], [1, 1, 1], singleSpanKnots));
        var poleOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 9,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false };
        const poleUnmasked = censusFunnelComponents(poleFactors, poleSpans, poleOptions);
        var poleMaskedOptions = poleOptions;
        poleMaskedOptions.degenerate = { "uStart" : false, "uEnd" : false, "vStart" : true, "vEnd" : false };
        const poleMasked = censusFunnelComponents(poleFactors, poleSpans, poleMaskedOptions);
        var poleCells = "";
        var poleReported = 0;
        var poleIslands = 0;
        for (var component in poleMasked.components)
        {
            poleCells = poleCells ~ " " ~ component.cellCount;
            poleReported += component.touchesDegenerateBoundary ? 1 : 0;
            poleIslands += component.isIsland ? 1 : 0;
        }
        println("[FUNNEL MASK SELF TEST] pole fixture unmasked: " ~ size(poleUnmasked.components) ~
            " component(s), " ~ poleUnmasked.components[0].cellCount ~ " cells; masked: " ~
            size(poleMasked.components) ~ " component(s), cells" ~ poleCells ~ ", " ~ poleReported ~
            " reporting the pole, " ~ poleIslands ~ " island(s)");
        if (size(poleUnmasked.components) != 1 || poleUnmasked.components[0].cellCount != 204)
        {
            failures = failures ~ " the unmasked pole fixture was expected to flood into one " ~
                "204-cell component.";
        }
        if (size(poleMasked.components) != 2 || poleReported != 2 || poleIslands != 0 ||
            poleMasked.components[0].cellCount != 70 || poleMasked.components[1].cellCount != 70)
        {
            failures = failures ~ " the degeneracy mask did not split the pole fixture into two " ~
                "70-cell sheets.";
        }

        reportTestVerdict(context, id, "FUNNEL MASK SELF TEST", failures,
            "the cyclic trim mask (band, wave, seam-straddling hole), a trim band that " ~
            "excludes nothing, a band that cuts, and the degeneracy mask all match their " ~
            "closed-form answers.");
    });

// ============================= Sliding audit (spec 6.4) =============================

/**
 * Audit every (patch x t-span) block of one face for the sliding degeneracy: |f| within
 * `valueTolerance` over the WHOLE block, meaning the face slides along itself there (plane
 * parallel to translation, cylinder along its own axis). Blocks that survive screening are
 * materialized once; their coefficient tensors are kept on the live records so downstream
 * stages (census, island refinement) never pay for materialization twice.
 *
 * Returns {
 *     slides {boolean} : true when any block slid,
 *     slidingBlocks {array} : { uSegment, vSegment, spanIndex, tStart, tEnd },
 *     liveBlocks {array} : { uSegment, vSegment, spanIndex, blockGrids } - can vanish, does
 *         not slide,
 *     deadBlockCount {number} : blocks certified sign-definite
 * }
 */
export function auditEnvelopeSliding(patchFactors is map, spans is array, valueTolerance is number) returns map
{
    var slidingBlocks = [];
    var liveBlocks = [];
    var deadBlockCount = 0;
    for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
    {
        for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
        {
            const patch = patchFactors.patches[uSegment][vSegment];
            var products = undefined;
            for (var spanIndex = 0; spanIndex < size(spans); spanIndex += 1)
            {
                const screen = screenEnvelopeBlock(patch, spans[spanIndex], valueTolerance);
                if (!screen.canVanish)
                {
                    deadBlockCount += 1;
                    continue;
                }
                if (products == undefined)
                {
                    products = buildEnvelopePatchProducts(patch);
                }
                const blockGrids = materializeEnvelopeBlock(products, spans[spanIndex]);
                var blockMinimum = undefined;
                var blockMaximum = undefined;
                for (var grid in blockGrids)
                {
                    const range = bernsteinGridRange(grid);
                    blockMinimum = blockMinimum == undefined ? range.minimum : min(blockMinimum, range.minimum);
                    blockMaximum = blockMaximum == undefined ? range.maximum : max(blockMaximum, range.maximum);
                }
                if (blockMinimum >= -valueTolerance && blockMaximum <= valueTolerance)
                {
                    slidingBlocks = append(slidingBlocks, {
                                "uSegment" : uSegment, "vSegment" : vSegment, "spanIndex" : spanIndex,
                                "tStart" : spans[spanIndex].tStart, "tEnd" : spans[spanIndex].tEnd
                            });
                }
                else if (blockMinimum > valueTolerance || blockMaximum < -valueTolerance)
                {
                    deadBlockCount += 1;
                }
                else
                {
                    liveBlocks = append(liveBlocks, {
                                "uSegment" : uSegment, "vSegment" : vSegment, "spanIndex" : spanIndex,
                                "blockGrids" : blockGrids
                            });
                }
            }
        }
    }
    return {
            "slides" : size(slidingBlocks) > 0,
            "slidingBlocks" : slidingBlocks,
            "liveBlocks" : liveBlocks,
            "deadBlockCount" : deadBlockCount
        };
}

// ============================= Factored cell isolation =============================

/**
 * Isolate the possible grazing set of one (patch x t-span) block by FACTORED subdivision:
 * each split halves the cell in u, v, or t by subdividing the S and N coefficient grids
 * (native split-matrix products) or the twelve t-polynomials, then re-screens the child with
 * range products alone. A dead verdict is a certificate; f is never materialized during the
 * descent, matching the spec 6.0 doctrine that most live-block work never materializes f.
 *
 * options: {
 *     minCellWidthU, minCellWidthV, minCellWidthT {number} : leaf sizes as LOCAL fractions of
 *         the block (defaults 1/8),
 *     maxSplitDepth {number} : safety cap (default 24),
 *     valueTolerance {number} : screening tolerance (default 0)
 * }
 *
 * Returns {
 *     liveCells {array} : { uStart..tEnd local fractions, globalUStart..globalTEnd },
 *     screenedCount, deadCount {number}
 * }
 */
export function isolateEnvelopeCells(patchFactor is map, spanPolynomials is map, options is map) returns map
{
    const filledOptions = mergeMaps({
                "minCellWidthU" : 1 / 8, "minCellWidthV" : 1 / 8, "minCellWidthT" : 1 / 8,
                "maxSplitDepth" : 24, "valueTolerance" : 0
            }, options);
    const bounds = { "u0" : 0, "u1" : 1, "v0" : 0, "v1" : 1, "t0" : 0, "t1" : 1 };
    const collected = collectLiveEnvelopeCells(patchFactor, spanPolynomials, bounds, filledOptions, filledOptions.maxSplitDepth);
    var liveCells = makeArray(size(collected.cells));
    for (var cellIndex = 0; cellIndex < size(collected.cells); cellIndex += 1)
    {
        var cell = collected.cells[cellIndex];
        cell.globalUStart = patchFactor.uStart + (patchFactor.uEnd - patchFactor.uStart) * cell.uStart;
        cell.globalUEnd = patchFactor.uStart + (patchFactor.uEnd - patchFactor.uStart) * cell.uEnd;
        cell.globalVStart = patchFactor.vStart + (patchFactor.vEnd - patchFactor.vStart) * cell.vStart;
        cell.globalVEnd = patchFactor.vStart + (patchFactor.vEnd - patchFactor.vStart) * cell.vEnd;
        cell.globalTStart = spanPolynomials.tStart + (spanPolynomials.tEnd - spanPolynomials.tStart) * cell.tStart;
        cell.globalTEnd = spanPolynomials.tStart + (spanPolynomials.tEnd - spanPolynomials.tStart) * cell.tEnd;
        liveCells[cellIndex] = cell;
    }
    return { "liveCells" : liveCells, "screenedCount" : collected.screened, "deadCount" : collected.dead };
}

/** Recursive descent for isolateEnvelopeCells; bounds are local fractions of the root block. */
function collectLiveEnvelopeCells(cellFactor is map, cellSpan is map, bounds is map, options is map,
    depthRemaining is number) returns map
{
    const screen = screenEnvelopeBlock(cellFactor, cellSpan, options.valueTolerance);
    if (!screen.canVanish)
    {
        return { "cells" : [], "screened" : 1, "dead" : 1 };
    }
    const uWidth = bounds.u1 - bounds.u0;
    const vWidth = bounds.v1 - bounds.v0;
    const tWidth = bounds.t1 - bounds.t0;
    const uPressure = uWidth / options.minCellWidthU;
    const vPressure = vWidth / options.minCellWidthV;
    const tPressure = tWidth / options.minCellWidthT;
    if (depthRemaining <= 0 || (uPressure <= 1.0000001 && vPressure <= 1.0000001 && tPressure <= 1.0000001))
    {
        return {
                "cells" : [{
                            "uStart" : bounds.u0, "uEnd" : bounds.u1,
                            "vStart" : bounds.v0, "vEnd" : bounds.v1,
                            "tStart" : bounds.t0, "tEnd" : bounds.t1,
                            "looseMin" : screen.looseMin, "looseMax" : screen.looseMax
                        }],
                "screened" : 1, "dead" : 0
            };
    }

    var lowFactor = cellFactor;
    var highFactor = cellFactor;
    var lowSpan = cellSpan;
    var highSpan = cellSpan;
    var lowBounds = bounds;
    var highBounds = bounds;
    if (uPressure >= vPressure && uPressure >= tPressure)
    {
        const split = splitCellFactor(cellFactor, true);
        lowFactor = split.low;
        highFactor = split.high;
        const midpoint = 0.5 * (bounds.u0 + bounds.u1);
        lowBounds.u1 = midpoint;
        highBounds.u0 = midpoint;
    }
    else if (vPressure >= tPressure)
    {
        const split = splitCellFactor(cellFactor, false);
        lowFactor = split.low;
        highFactor = split.high;
        const midpoint = 0.5 * (bounds.v0 + bounds.v1);
        lowBounds.v1 = midpoint;
        highBounds.v0 = midpoint;
    }
    else
    {
        const split = splitCellSpan(cellSpan);
        lowSpan = split.low;
        highSpan = split.high;
        const midpoint = 0.5 * (bounds.t0 + bounds.t1);
        lowBounds.t1 = midpoint;
        highBounds.t0 = midpoint;
    }
    const lowResult = collectLiveEnvelopeCells(lowFactor, lowSpan, lowBounds, options, depthRemaining - 1);
    const highResult = collectLiveEnvelopeCells(highFactor, highSpan, highBounds, options, depthRemaining - 1);
    return {
            "cells" : concatenateArrays([lowResult.cells, highResult.cells]),
            "screened" : 1 + lowResult.screened + highResult.screened,
            "dead" : lowResult.dead + highResult.dead
        };
}

/** Split a cell's S and N grids at the local midpoint of u (splitU true) or v. */
function splitCellFactor(cellFactor is map, splitU is boolean) returns map
{
    var lowSurfaceGrids = makeArray(3);
    var highSurfaceGrids = makeArray(3);
    var lowNormalGrids = makeArray(3);
    var highNormalGrids = makeArray(3);
    var lowSurfaceRanges = makeArray(3);
    var highSurfaceRanges = makeArray(3);
    var lowNormalRanges = makeArray(3);
    var highNormalRanges = makeArray(3);
    for (var component = 0; component < 3; component += 1)
    {
        const surfaceSplit = splitU ? subdivideBernsteinGridU(cellFactor.surfaceGrids[component], 0.5) :
            subdivideBernsteinGridV(cellFactor.surfaceGrids[component], 0.5);
        const normalSplit = splitU ? subdivideBernsteinGridU(cellFactor.normalGrids[component], 0.5) :
            subdivideBernsteinGridV(cellFactor.normalGrids[component], 0.5);
        lowSurfaceGrids[component] = surfaceSplit.low;
        highSurfaceGrids[component] = surfaceSplit.high;
        lowNormalGrids[component] = normalSplit.low;
        highNormalGrids[component] = normalSplit.high;
        lowSurfaceRanges[component] = bernsteinGridRange(surfaceSplit.low);
        highSurfaceRanges[component] = bernsteinGridRange(surfaceSplit.high);
        lowNormalRanges[component] = bernsteinGridRange(normalSplit.low);
        highNormalRanges[component] = bernsteinGridRange(normalSplit.high);
    }
    var low = cellFactor;
    low.surfaceGrids = lowSurfaceGrids;
    low.normalGrids = lowNormalGrids;
    low.surfaceRanges = lowSurfaceRanges;
    low.normalRanges = lowNormalRanges;
    var high = cellFactor;
    high.surfaceGrids = highSurfaceGrids;
    high.normalGrids = highNormalGrids;
    high.surfaceRanges = highSurfaceRanges;
    high.normalRanges = highNormalRanges;
    return { "low" : low, "high" : high };
}

/** Split a cell's twelve t-polynomials at the local midpoint of t. */
function splitCellSpan(cellSpan is map) returns map
{
    var lowVelocityDots = makeArray(3);
    var highVelocityDots = makeArray(3);
    var lowVelocityRanges = makeArray(3);
    var highVelocityRanges = makeArray(3);
    var lowTranslationDots = makeArray(3);
    var highTranslationDots = makeArray(3);
    var lowTranslationRanges = makeArray(3);
    var highTranslationRanges = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var lowRow = makeArray(3);
        var highRow = makeArray(3);
        var lowRangeRow = makeArray(3);
        var highRangeRow = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            const split = subdivideBernstein(cellSpan.velocityDots[i][j], 0.5);
            lowRow[j] = split.left;
            highRow[j] = split.right;
            lowRangeRow[j] = bernsteinRange(split.left);
            highRangeRow[j] = bernsteinRange(split.right);
        }
        lowVelocityDots[i] = lowRow;
        highVelocityDots[i] = highRow;
        lowVelocityRanges[i] = lowRangeRow;
        highVelocityRanges[i] = highRangeRow;
        const translationSplit = subdivideBernstein(cellSpan.translationDots[i], 0.5);
        lowTranslationDots[i] = translationSplit.left;
        highTranslationDots[i] = translationSplit.right;
        lowTranslationRanges[i] = bernsteinRange(translationSplit.left);
        highTranslationRanges[i] = bernsteinRange(translationSplit.right);
    }
    var low = cellSpan;
    low.velocityDots = lowVelocityDots;
    low.velocityDotRanges = lowVelocityRanges;
    low.translationDots = lowTranslationDots;
    low.translationDotRanges = lowTranslationRanges;
    var high = cellSpan;
    high.velocityDots = highVelocityDots;
    high.velocityDotRanges = highVelocityRanges;
    high.translationDots = highTranslationDots;
    high.translationDotRanges = highTranslationRanges;
    return { "low" : low, "high" : high };
}

// ============================= Funnel component census =============================

/**
 * Census of the funnel components of one face over D x I (spec 6.3 step 3): a coarse value
 * grid evaluated from screened/materialized blocks (never pointwise splines), trim masking by
 * even-odd ray crossings, and sign-change flood fill with an explicit u-seam wrap for
 * periodic faces. Kernel-oracle seeds (spec 2.2) merge in at a higher layer - this census is
 * pure and self-contained.
 *
 * options: {
 *     uNodesPerPatch, vNodesPerPatch, tNodesPerSpan {number} : grid nodes per patch/span
 *         (>= 3 recommended),
 *     valueTolerance {number} : |value| below this counts as a zero sign,
 *     trimLoops {array} : uv trim loops in the face's knot domain; empty means the whole
 *         rectangle is valid; a node is valid when an even-odd crossing count over all loops
 *         is odd. Each entry is either a bare point array ([ [u, v], ... ], implicitly closed)
 *         or the { points, winding } record swSweepEmit's buildFaceTrimLoops produces - the
 *         winding is what tells a loop that WRAPS a periodic seam (stored open, its ends one
 *         period apart) from one that closes on itself,
 *     uPeriodic {boolean} : link the first and last u cell columns during flood fill, and mask
 *         with the cyclic +v ray instead of the +u one,
 *     trimBoundaryTolerance {number} : a node within this distance of a trim loop counts as
 *         VALID whatever the crossing test says. The trim boundary belongs to the face, and a
 *         face that fills its whole surface has a trim loop lying exactly ON the domain
 *         rectangle - where an even-odd ray cast is a coin flip that would silently delete the
 *         boundary cell rows, which is precisely where co-edge components live. Defaults to
 *         1e-6 of the smaller domain span,
 *     degenerate {map} : { uStart, uEnd, vStart, vEnd } booleans, straight from the face
 *         record's `degenerate` - the collapsed control-net boundaries where the surface normal
 *         vanishes. f is identically zero along such a boundary, so without masking it every
 *         real component that reaches the pole floods through it into every other one
 * }
 *
 * Returns { components {array}, uNodes, vNodes, tNodes {arrays of global parameters} }.
 * Component: {
 *     cellCount {number},
 *     parameterBounds {map} : uMin..tMax over member cell corners (seam-crossing components
 *         smear across the seam - read crossesUSeam first),
 *     touchesTStart, touchesTEnd, touchesDomainBoundaryUv, touchesTrimBoundary,
 *     touchesDegenerateBoundary, crossesUSeam, isIsland {booleans},
 *     minTCellCenter, maxTCellCenter {maps} : { u, v, t } cell centers at the component's
 *         t-extremes - Newton seeds for island refinement
 * }
 */
export function censusFunnelComponents(patchFactors is map, spans is array, censusOptions is map) returns map
{
    const options = mergeMaps({ "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false,
                "degenerate" : { "uStart" : false, "uEnd" : false, "vStart" : false, "vEnd" : false } },
            censusOptions);
    const uNodesPerPatch = options.uNodesPerPatch;
    const vNodesPerPatch = options.vNodesPerPatch;
    const tNodesPerSpan = options.tNodesPerSpan;
    const uNodeCount = patchFactors.uSegments * (uNodesPerPatch - 1) + 1;
    const vNodeCount = patchFactors.vSegments * (vNodesPerPatch - 1) + 1;
    const tNodeCount = size(spans) * (tNodesPerSpan - 1) + 1;

    // Global node parameter arrays.
    var uNodes = makeArray(uNodeCount, 0);
    var vNodes = makeArray(vNodeCount, 0);
    var tNodes = makeArray(tNodeCount, 0);
    for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
    {
        const patch = patchFactors.patches[uSegment][0];
        for (var offset = 0; offset < uNodesPerPatch; offset += 1)
        {
            uNodes[uSegment * (uNodesPerPatch - 1) + offset] =
                patch.uStart + (patch.uEnd - patch.uStart) * offset / (uNodesPerPatch - 1);
        }
    }
    for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
    {
        const patch = patchFactors.patches[0][vSegment];
        for (var offset = 0; offset < vNodesPerPatch; offset += 1)
        {
            vNodes[vSegment * (vNodesPerPatch - 1) + offset] =
                patch.vStart + (patch.vEnd - patch.vStart) * offset / (vNodesPerPatch - 1);
        }
    }
    for (var spanIndex = 0; spanIndex < size(spans); spanIndex += 1)
    {
        for (var offset = 0; offset < tNodesPerSpan; offset += 1)
        {
            tNodes[spanIndex * (tNodesPerSpan - 1) + offset] =
                spans[spanIndex].tStart + (spans[spanIndex].tEnd - spans[spanIndex].tStart) * offset / (tNodesPerSpan - 1);
        }
    }

    // Block-wise value fill: dead blocks get their certified constant sign, live blocks are
    // materialized once and evaluated on their local node grid.
    var values = makeArray(uNodeCount);
    for (var uIndex = 0; uIndex < uNodeCount; uIndex += 1)
    {
        var plane = makeArray(vNodeCount);
        for (var vIndex = 0; vIndex < vNodeCount; vIndex += 1)
        {
            plane[vIndex] = makeArray(tNodeCount, 0);
        }
        values[uIndex] = plane;
    }
    for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
    {
        for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
        {
            const patch = patchFactors.patches[uSegment][vSegment];
            var products = undefined;
            for (var spanIndex = 0; spanIndex < size(spans); spanIndex += 1)
            {
                const screen = screenEnvelopeBlock(patch, spans[spanIndex], options.valueTolerance);
                var blockGrids = undefined;
                var fillValue = 0;
                if (!screen.canVanish)
                {
                    fillValue = screen.looseMin > 0 ? screen.looseMin : screen.looseMax;
                }
                else
                {
                    if (products == undefined)
                    {
                        products = buildEnvelopePatchProducts(patch);
                    }
                    blockGrids = materializeEnvelopeBlock(products, spans[spanIndex]);
                }
                for (var uOffset = 0; uOffset < uNodesPerPatch; uOffset += 1)
                {
                    const uIndex = uSegment * (uNodesPerPatch - 1) + uOffset;
                    const localU = uOffset / (uNodesPerPatch - 1);
                    for (var vOffset = 0; vOffset < vNodesPerPatch; vOffset += 1)
                    {
                        const vIndex = vSegment * (vNodesPerPatch - 1) + vOffset;
                        const localV = vOffset / (vNodesPerPatch - 1);
                        for (var tOffset = 0; tOffset < tNodesPerSpan; tOffset += 1)
                        {
                            const tIndex = spanIndex * (tNodesPerSpan - 1) + tOffset;
                            values[uIndex][vIndex][tIndex] = blockGrids == undefined ? fillValue :
                                evaluateMaterializedBlock(blockGrids, localU, localV, tOffset / (tNodesPerSpan - 1));
                        }
                    }
                }
            }
        }
    }

    // Two uv node masks, both independent of t. The trim mask is even-odd against the loops -
    // cast in +v when u is cyclic, since a ray along a cyclic direction has no outside to start
    // from. The degeneracy mask is the collapsed control-net boundaries: f vanishes identically
    // there, so the pole line reads as one connected zero set joining everything that reaches
    // it.
    const uPeriodForMask = uNodes[uNodeCount - 1] - uNodes[0];
    const resolvedBoundaryTolerance = options.trimBoundaryTolerance != undefined ?
        options.trimBoundaryTolerance :
        1e-6 * min(uNodes[uNodeCount - 1] - uNodes[0], vNodes[vNodeCount - 1] - vNodes[0]);
    const degenerate = options.degenerate;
    const hasTrimLoops = size(options.trimLoops) > 0;
    const hasDegenerateBoundary = degenerate.uStart == true || degenerate.uEnd == true ||
        degenerate.vStart == true || degenerate.vEnd == true;
    var nodeValid = makeArray(uNodeCount);
    var nodeDegenerate = makeArray(uNodeCount);
    for (var uIndex = 0; uIndex < uNodeCount; uIndex += 1)
    {
        var validRow = makeArray(vNodeCount, true);
        var degenerateRow = makeArray(vNodeCount, false);
        if (hasTrimLoops || hasDegenerateBoundary)
        {
            const uOnDegenerateBoundary = (degenerate.uStart == true && uIndex == 0) ||
                (degenerate.uEnd == true && uIndex == uNodeCount - 1);
            for (var vIndex = 0; vIndex < vNodeCount; vIndex += 1)
            {
                if (hasTrimLoops)
                {
                    validRow[vIndex] = options.uPeriodic ?
                        uvPointInsideLoopsCyclic(options.trimLoops, uNodes[uIndex], vNodes[vIndex], uPeriodForMask) :
                        uvPointInsideLoops(options.trimLoops, uNodes[uIndex], vNodes[vIndex]);
                    if (!validRow[vIndex])
                    {
                        // Only a rejected node can be rescued by the boundary tolerance, so the
                        // distance sweep runs on the minority of nodes rather than all of them.
                        validRow[vIndex] = uvPointOnLoops(options.trimLoops, uNodes[uIndex], vNodes[vIndex],
                            resolvedBoundaryTolerance, options.uPeriodic ? uPeriodForMask : 0);
                    }
                }
                degenerateRow[vIndex] = uOnDegenerateBoundary ||
                    (degenerate.vStart == true && vIndex == 0) ||
                    (degenerate.vEnd == true && vIndex == vNodeCount - 1);
            }
        }
        nodeValid[uIndex] = validRow;
        nodeDegenerate[uIndex] = degenerateRow;
    }

    // Cell classification: 0 invalid (a trim-masked or pole corner), 1 uniform sign, 2 mixed.
    const cellCountU = uNodeCount - 1;
    const cellCountV = vNodeCount - 1;
    const cellCountT = tNodeCount - 1;
    var mixedCellCount = 0;
    var cellClass = makeArray(cellCountU);
    for (var i = 0; i < cellCountU; i += 1)
    {
        var classPlane = makeArray(cellCountV);
        for (var j = 0; j < cellCountV; j += 1)
        {
            var classColumn = makeArray(cellCountT, 1);
            if (cellHasMaskedCorner(nodeValid, i, j) || cellHasDegenerateCorner(nodeDegenerate, i, j))
            {
                for (var k = 0; k < cellCountT; k += 1)
                {
                    classColumn[k] = 0;
                }
            }
            else
            {
                for (var k = 0; k < cellCountT; k += 1)
                {
                    var minimumSign = 1;
                    var maximumSign = -1;
                    for (var corner = 0; corner < 8; corner += 1)
                    {
                        const cornerValue = values[i + (corner % 2)][j + (floor(corner / 2) % 2)][k + floor(corner / 4)];
                        const cornerSign = cornerValue > options.valueTolerance ? 1 :
                            (cornerValue < -options.valueTolerance ? -1 : 0);
                        minimumSign = min(minimumSign, cornerSign);
                        maximumSign = max(maximumSign, cornerSign);
                    }
                    classColumn[k] = (minimumSign < 1 && maximumSign > -1) ? 2 : 1;
                    if (classColumn[k] == 2)
                    {
                        mixedCellCount += 1;
                    }
                }
            }
            classPlane[j] = classColumn;
        }
        cellClass[i] = classPlane;
    }

    // Flood fill over mixed cells, 6-connectivity, u wrap when periodic.
    var visited = makeArray(cellCountU);
    for (var i = 0; i < cellCountU; i += 1)
    {
        var visitedPlane = makeArray(cellCountV);
        for (var j = 0; j < cellCountV; j += 1)
        {
            visitedPlane[j] = makeArray(cellCountT, false);
        }
        visited[i] = visitedPlane;
    }
    // One queue for every component: append() copies, so growing one per component makes the
    // flood fill quadratic in the component size. Every mixed cell is visited exactly once
    // across all components, so a single buffer of that length is enough for all of them.
    var queue = makeArray(max(mixedCellCount, 1), [0, 0, 0]);
    var components = [];
    for (var i = 0; i < cellCountU; i += 1)
    {
        for (var j = 0; j < cellCountV; j += 1)
        {
            for (var k = 0; k < cellCountT; k += 1)
            {
                if (visited[i][j][k] || cellClass[i][j][k] != 2)
                {
                    continue;
                }
                var cellCount = 0;
                var crossesUSeam = false;
                var touchesTStart = false;
                var touchesTEnd = false;
                var touchesDomainBoundaryUv = false;
                var touchesTrimBoundary = false;
                var touchesDegenerateBoundary = false;
                var uMin = uNodes[uNodeCount - 1];
                var uMax = uNodes[0];
                var vMin = vNodes[vNodeCount - 1];
                var vMax = vNodes[0];
                var tMin = tNodes[tNodeCount - 1];
                var tMax = tNodes[0];
                var minTCell = undefined;
                var maxTCell = undefined;
                queue[0] = [i, j, k];
                var queueLength = 1;
                var queueCursor = 0;
                visited[i][j][k] = true;
                while (queueCursor < queueLength)
                {
                    const currentCell = queue[queueCursor];
                    queueCursor += 1;
                    const ci = currentCell[0];
                    const cj = currentCell[1];
                    const ck = currentCell[2];
                    cellCount += 1;
                    uMin = min(uMin, uNodes[ci]);
                    uMax = max(uMax, uNodes[ci + 1]);
                    vMin = min(vMin, vNodes[cj]);
                    vMax = max(vMax, vNodes[cj + 1]);
                    tMin = min(tMin, tNodes[ck]);
                    tMax = max(tMax, tNodes[ck + 1]);
                    if (minTCell == undefined || tNodes[ck] < tNodes[minTCell[2]])
                    {
                        minTCell = currentCell;
                    }
                    if (maxTCell == undefined || tNodes[ck + 1] > tNodes[maxTCell[2] + 1])
                    {
                        maxTCell = currentCell;
                    }
                    if (ck == 0)
                    {
                        touchesTStart = true;
                    }
                    if (ck == cellCountT - 1)
                    {
                        touchesTEnd = true;
                    }
                    if (cj == 0 || cj == cellCountV - 1 || (!options.uPeriodic && (ci == 0 || ci == cellCountU - 1)))
                    {
                        touchesDomainBoundaryUv = true;
                    }
                    for (var direction = 0; direction < 6; direction += 1)
                    {
                        var ni = ci + (direction == 0 ? 1 : (direction == 1 ? -1 : 0));
                        const nj = cj + (direction == 2 ? 1 : (direction == 3 ? -1 : 0));
                        const nk = ck + (direction == 4 ? 1 : (direction == 5 ? -1 : 0));
                        var wrapped = false;
                        if (options.uPeriodic && ni < 0)
                        {
                            ni = cellCountU - 1;
                            wrapped = true;
                        }
                        if (options.uPeriodic && ni > cellCountU - 1)
                        {
                            ni = 0;
                            wrapped = true;
                        }
                        if (ni < 0 || ni > cellCountU - 1 || nj < 0 || nj > cellCountV - 1 || nk < 0 || nk > cellCountT - 1)
                        {
                            continue;
                        }
                        if (cellClass[ni][nj][nk] == 0)
                        {
                            // Which mask blocked it is a uv question, so the node masks answer
                            // it directly - a cell can be blocked by both.
                            if (cellHasMaskedCorner(nodeValid, ni, nj))
                            {
                                touchesTrimBoundary = true;
                            }
                            if (cellHasDegenerateCorner(nodeDegenerate, ni, nj))
                            {
                                touchesDegenerateBoundary = true;
                            }
                            continue;
                        }
                        if (cellClass[ni][nj][nk] != 2 || visited[ni][nj][nk])
                        {
                            continue;
                        }
                        if (wrapped)
                        {
                            crossesUSeam = true;
                        }
                        visited[ni][nj][nk] = true;
                        queue[queueLength] = [ni, nj, nk];
                        queueLength += 1;
                    }
                }
                components = append(components, {
                            "cellCount" : cellCount,
                            "parameterBounds" : { "uMin" : uMin, "uMax" : uMax, "vMin" : vMin, "vMax" : vMax,
                                "tMin" : tMin, "tMax" : tMax },
                            "touchesTStart" : touchesTStart,
                            "touchesTEnd" : touchesTEnd,
                            "touchesDomainBoundaryUv" : touchesDomainBoundaryUv,
                            "touchesTrimBoundary" : touchesTrimBoundary,
                            "touchesDegenerateBoundary" : touchesDegenerateBoundary,
                            "crossesUSeam" : crossesUSeam,
                            "isIsland" : !touchesDomainBoundaryUv && !touchesTrimBoundary &&
                                !touchesDegenerateBoundary && !touchesTStart && !touchesTEnd,
                            "minTCellCenter" : cellCenter(uNodes, vNodes, tNodes, minTCell),
                            "maxTCellCenter" : cellCenter(uNodes, vNodes, tNodes, maxTCell)
                        });
            }
        }
    }
    return { "components" : components, "uNodes" : uNodes, "vNodes" : vNodes, "tNodes" : tNodes };
}

/**
 * Even-odd point-in-loops classification for a face that is not cyclic in u: casts a ray in +u
 * and counts crossings over every loop, each implicitly closed. Odd count = inside. Orientation
 * of the loops does not matter, so boundary-plus-holes trim sets work unmodified.
 *
 * Loop entries take either shape the census accepts: a bare point array or a { points, winding }
 * record. Winding is meaningless here - a face with a winding trim loop is cyclic in u by
 * construction, and that is uvPointInsideLoopsCyclic's job.
 */
export function uvPointInsideLoops(trimLoops is array, u is number, v is number) returns boolean
{
    var crossings = 0;
    for (var trimLoopEntry in trimLoops)
    {
        const trimLoop = trimLoopPoints(trimLoopEntry);
        const pointCount = size(trimLoop);
        for (var index = 0; index < pointCount; index += 1)
        {
            const start = trimLoop[index];
            const end = trimLoop[(index + 1) % pointCount];
            if ((start[1] > v) != (end[1] > v))
            {
                const crossingU = start[0] + (v - start[1]) / (end[1] - start[1]) * (end[0] - start[0]);
                if (crossingU > u)
                {
                    crossings += 1;
                }
            }
        }
    }
    return crossings % 2 == 1;
}

/**
 * Even-odd point-in-loops classification for a face that is CYCLIC in u: casts the ray in +v
 * and counts crossings against every periodic image of the test point.
 *
 * The +u ray of uvPointInsideLoops has no outside to start from on a closed face, because a ray
 * along a cyclic direction never leaves the domain. The v direction always does, and casting it
 * handles both kinds of loop a closed face produces with one test:
 *   - a loop that WINDS the seam is stored as an open chain whose two ends are the same point
 *     one period apart, so its segments are walked with no implicit closing segment. The ray
 *     crosses such a loop once from below, which is what makes the band between two winding
 *     trims come out odd and everything outside it even.
 *   - a loop that does not wind closes on itself, and if it straddles the seam its u values run
 *     a little past the domain edge. Testing every periodic image of the point is what finds it
 *     from both sides of the seam.
 *
 * Loop entries take either shape the census accepts: a bare point array (winding 0) or a
 * { points, winding } record.
 */
export function uvPointInsideLoopsCyclic(trimLoops is array, u is number, v is number, uPeriod is number) returns boolean
{
    var crossings = 0;
    for (var trimLoop in trimLoops)
    {
        const loopPoints = trimLoopPoints(trimLoop);
        const pointCount = size(loopPoints);
        const segmentCount = trimLoopWinding(trimLoop) != 0 ? pointCount - 1 : pointCount;
        var uMin = loopPoints[0][0];
        var uMax = loopPoints[0][0];
        for (var loopPoint in loopPoints)
        {
            uMin = min(uMin, loopPoint[0]);
            uMax = max(uMax, loopPoint[0]);
        }
        const firstImage = floor((uMin - u) / uPeriod);
        const lastImage = ceil((uMax - u) / uPeriod);
        for (var index = 0; index < segmentCount; index += 1)
        {
            const start = loopPoints[index];
            const end = loopPoints[(index + 1) % pointCount];
            for (var image = firstImage; image <= lastImage; image += 1)
            {
                const imageU = u + image * uPeriod;
                if ((start[0] > imageU) != (end[0] > imageU))
                {
                    const crossingV = start[1] + (imageU - start[0]) / (end[0] - start[0]) * (end[1] - start[1]);
                    if (crossingV > v)
                    {
                        crossings += 1;
                    }
                }
            }
        }
    }
    return crossings % 2 == 1;
}

/**
 * Whether (u, v) lies within `tolerance` of any trim loop segment - the on-the-boundary case
 * that no even-odd ray cast can decide. `uPeriod` nonzero also tests the point's periodic
 * images, matching the cyclic mask; a winding loop is walked without its implicit closure, the
 * same way.
 */
export function uvPointOnLoops(trimLoops is array, u is number, v is number, tolerance is number,
    uPeriod is number) returns boolean
{
    const toleranceSquared = tolerance * tolerance;
    for (var trimLoop in trimLoops)
    {
        const loopPoints = trimLoopPoints(trimLoop);
        const pointCount = size(loopPoints);
        const segmentCount = trimLoopWinding(trimLoop) != 0 ? pointCount - 1 : pointCount;
        for (var index = 0; index < segmentCount; index += 1)
        {
            const start = loopPoints[index];
            const end = loopPoints[(index + 1) % pointCount];
            const imageU = uPeriod == 0 ? u : u + round((0.5 * (start[0] + end[0]) - u) / uPeriod) * uPeriod;
            if (pointToSegmentSquaredDistance(vector(imageU, v), start, end) <= toleranceSquared)
            {
                return true;
            }
        }
    }
    return false;
}

/** Squared distance from a uv point to a segment, clamped to the segment's ends. */
function pointToSegmentSquaredDistance(point is Vector, start is Vector, end is Vector) returns number
{
    const along = end - start;
    const alongLengthSquared = squaredNorm(along);
    if (alongLengthSquared == 0)
    {
        return squaredNorm(point - start);
    }
    const projection = dot(point - start, along) / alongLengthSquared;
    const clamped = max(0, min(1, projection));
    return squaredNorm(point - (start + clamped * along));
}

/** The points of a trim loop given in either accepted shape. */
function trimLoopPoints(trimLoop) returns array
{
    return trimLoop is array ? trimLoop : trimLoop.points;
}

/** The u winding of a trim loop; a bare point array is a closed polygon, so zero. */
function trimLoopWinding(trimLoop) returns number
{
    return trimLoop is array ? 0 : trimLoop.winding;
}

/** Whether any of the four uv corners of cell (i, j) is trimmed away. */
function cellHasMaskedCorner(nodeValid is array, i is number, j is number) returns boolean
{
    return !nodeValid[i][j] || !nodeValid[i + 1][j] || !nodeValid[i][j + 1] || !nodeValid[i + 1][j + 1];
}

/** Whether any of the four uv corners of cell (i, j) sits on a collapsed net boundary. */
function cellHasDegenerateCorner(nodeDegenerate is array, i is number, j is number) returns boolean
{
    return nodeDegenerate[i][j] || nodeDegenerate[i + 1][j] || nodeDegenerate[i][j + 1] ||
        nodeDegenerate[i + 1][j + 1];
}

/** The center of one census cell in global parameters. */
function cellCenter(uNodes is array, vNodes is array, tNodes is array, cellIndices is array) returns map
{
    return {
            "u" : 0.5 * (uNodes[cellIndices[0]] + uNodes[cellIndices[0] + 1]),
            "v" : 0.5 * (vNodes[cellIndices[1]] + vNodes[cellIndices[1] + 1]),
            "t" : 0.5 * (tNodes[cellIndices[2]] + tNodes[cellIndices[2] + 1])
        };
}

// ============================= Island refinement =============================

/**
 * 3-variable Newton on (f, f_u, f_v) = 0 over one materialized block, with EXACT partials
 * read off differentiated coefficient nets - no finite differences, no sampling. This is the
 * grazing-island t-extreme refinement of spec 6.3 step 3; the same stationary condition finds
 * where an island is born or dies.
 *
 * All coordinates are block-local ([0, 1]^3). options: { iterationLimit (default 30),
 * stepTolerance (default 1e-13) }.
 * Returns { converged {boolean}, localU, localV, localT, functionValue, gradientU, gradientV }.
 */
export function refineBlockStationaryPoint(blockGrids is array, seedU is number, seedV is number, seedT is number,
    options is map) returns map
{
    const iterationLimit = options.iterationLimit == undefined ? 30 : options.iterationLimit;
    const stepTolerance = options.stepTolerance == undefined ? 1e-13 : options.stepTolerance;
    const timeCoefficientCount = size(blockGrids);

    var uDerivativeGrids = makeArray(timeCoefficientCount);
    var vDerivativeGrids = makeArray(timeCoefficientCount);
    var uuGrids = makeArray(timeCoefficientCount);
    var uvGrids = makeArray(timeCoefficientCount);
    var vvGrids = makeArray(timeCoefficientCount);
    for (var m = 0; m < timeCoefficientCount; m += 1)
    {
        uDerivativeGrids[m] = differentiateBernsteinGridU(blockGrids[m]);
        vDerivativeGrids[m] = differentiateBernsteinGridV(blockGrids[m]);
        uuGrids[m] = differentiateBernsteinGridU(uDerivativeGrids[m]);
        uvGrids[m] = differentiateBernsteinGridV(uDerivativeGrids[m]);
        vvGrids[m] = differentiateBernsteinGridV(vDerivativeGrids[m]);
    }

    var u = seedU;
    var v = seedV;
    var t = seedT;
    var converged = false;
    var functionValue = 0;
    var gradientU = 0;
    var gradientV = 0;
    for (var iteration = 0; iteration < iterationLimit; iteration += 1)
    {
        var valueCoefficients = makeArray(timeCoefficientCount, 0);
        var uCoefficients = makeArray(timeCoefficientCount, 0);
        var vCoefficients = makeArray(timeCoefficientCount, 0);
        var uuCoefficients = makeArray(timeCoefficientCount, 0);
        var uvCoefficients = makeArray(timeCoefficientCount, 0);
        var vvCoefficients = makeArray(timeCoefficientCount, 0);
        for (var m = 0; m < timeCoefficientCount; m += 1)
        {
            valueCoefficients[m] = evaluateBernsteinGrid(blockGrids[m], u, v);
            uCoefficients[m] = evaluateBernsteinGrid(uDerivativeGrids[m], u, v);
            vCoefficients[m] = evaluateBernsteinGrid(vDerivativeGrids[m], u, v);
            uuCoefficients[m] = evaluateBernsteinGrid(uuGrids[m], u, v);
            uvCoefficients[m] = evaluateBernsteinGrid(uvGrids[m], u, v);
            vvCoefficients[m] = evaluateBernsteinGrid(vvGrids[m], u, v);
        }
        functionValue = evaluateBernstein(valueCoefficients, t);
        gradientU = evaluateBernstein(uCoefficients, t);
        gradientV = evaluateBernstein(vCoefficients, t);
        const timeDerivative = evaluateBernstein(differentiateBernstein(valueCoefficients), t);
        const uu = evaluateBernstein(uuCoefficients, t);
        const uv = evaluateBernstein(uvCoefficients, t);
        const vv = evaluateBernstein(vvCoefficients, t);
        const ut = evaluateBernstein(differentiateBernstein(uCoefficients), t);
        const vt = evaluateBernstein(differentiateBernstein(vCoefficients), t);

        const solved = solveThreeByThree(
            [[gradientU, gradientV, timeDerivative],
                [uu, uv, ut],
                [uv, vv, vt]],
            [-functionValue, -gradientU, -gradientV]);
        if (solved == undefined)
        {
            break;
        }
        var stepU = clampMagnitude(solved[0], 0.25);
        var stepV = clampMagnitude(solved[1], 0.25);
        var stepT = clampMagnitude(solved[2], 0.25);
        u = clampToUnit(u + stepU);
        v = clampToUnit(v + stepV);
        t = clampToUnit(t + stepT);
        if (max(max(abs(stepU), abs(stepV)), abs(stepT)) < stepTolerance)
        {
            converged = true;
            break;
        }
    }
    return {
            "converged" : converged,
            "localU" : u, "localV" : v, "localT" : t,
            "functionValue" : functionValue, "gradientU" : gradientU, "gradientV" : gradientV
        };
}

// ============================= Vertex layer (1D contact roots) =============================

/**
 * The time derivative of the contact function at one sample:
 * g_t = <A' n, A' p + b'> + <A n, A'' p + b''>.
 */
export function evaluateContactFunctionTimeDerivative(strippedMotion is map, normal is Vector, point is Vector,
    t is number) returns number
{
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * point + sample.translationDerivative;
    const acceleration = sample.rotationSecondDerivative * point + sample.translationSecondDerivative;
    return dot(sample.rotationDerivative * normal, velocity) + dot(sample.rotation * normal, acceleration);
}

/**
 * All roots of the contact function g(t) = <A n, A' p + b'> on [tStart, tEnd]: sign-change
 * brackets on a station grid, each refined by bisection-safeguarded Newton (spec 6.3 step 1).
 * A tangential (non-crossing) zero that stays one-signed between stations is not detected -
 * the sliding audit owns the identically-zero case, and grazing tangencies belong to the
 * island machinery.
 * Returns an array of { t, value }, ascending in t.
 */
export function findContactFunctionRoots(strippedMotion is map, normal is Vector, point is Vector,
    tStart is number, tEnd is number, stationCount is number, tTolerance is number) returns array
{
    var stationValues = makeArray(stationCount, 0);
    var stationParameters = makeArray(stationCount, 0);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        stationParameters[stationIndex] = tStart + (tEnd - tStart) * stationIndex / (stationCount - 1);
        stationValues[stationIndex] = evaluateContactFunctionAtPoint(strippedMotion, normal, point,
            stationParameters[stationIndex]);
    }
    var roots = [];
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        if (stationValues[stationIndex] == 0)
        {
            roots = append(roots, { "t" : stationParameters[stationIndex], "value" : 0 });
            continue;
        }
        if (stationIndex == stationCount - 1 || stationValues[stationIndex] * stationValues[stationIndex + 1] >= 0)
        {
            continue;
        }
        roots = append(roots, refineContactRoot(strippedMotion, normal, point,
                stationParameters[stationIndex], stationParameters[stationIndex + 1],
                stationValues[stationIndex], stationValues[stationIndex + 1], tTolerance));
    }
    // Deduplicate roots that landed within tolerance of each other (a zero on a station).
    var deduplicated = [];
    for (var root in roots)
    {
        if (size(deduplicated) == 0 || root.t - deduplicated[size(deduplicated) - 1].t > 10 * tTolerance)
        {
            deduplicated = append(deduplicated, root);
        }
    }
    return deduplicated;
}

/**
 * Sharp-vertex contact intervals (spec section 8): the sub-intervals of [tStart, tEnd] where
 * the vertex's cone-normal contact functions s_i(t) do not all share one sign. Breakpoints
 * are the union of every s_i's roots; each gap is classified by its midpoint sign pattern and
 * adjacent qualifying gaps merge.
 * Returns an array of { tStart, tEnd }.
 */
export function solveVertexContactIntervals(strippedMotion is map, coneNormals is array, point is Vector,
    tStart is number, tEnd is number, stationCount is number, tTolerance is number) returns array
{
    var breakpoints = [tStart, tEnd];
    for (var normal in coneNormals)
    {
        for (var root in findContactFunctionRoots(strippedMotion, normal, point, tStart, tEnd, stationCount, tTolerance))
        {
            breakpoints = append(breakpoints, root.t);
        }
    }
    breakpoints = sort(breakpoints, function(a, b)
        {
            return a - b;
        });
    var intervals = [];
    for (var index = 0; index < size(breakpoints) - 1; index += 1)
    {
        if (breakpoints[index + 1] - breakpoints[index] < 10 * tTolerance)
        {
            continue;
        }
        const midpoint = 0.5 * (breakpoints[index] + breakpoints[index + 1]);
        var hasPositive = false;
        var hasNegative = false;
        for (var normal in coneNormals)
        {
            const value = evaluateContactFunctionAtPoint(strippedMotion, normal, point, midpoint);
            if (value > 0)
            {
                hasPositive = true;
            }
            if (value < 0)
            {
                hasNegative = true;
            }
        }
        if (!(hasPositive && hasNegative))
        {
            continue;
        }
        if (size(intervals) > 0 && abs(intervals[size(intervals) - 1].tEnd - breakpoints[index]) < 10 * tTolerance)
        {
            var merged = intervals[size(intervals) - 1];
            merged.tEnd = breakpoints[index + 1];
            intervals[size(intervals) - 1] = merged;
        }
        else
        {
            intervals = append(intervals, { "tStart" : breakpoints[index], "tEnd" : breakpoints[index + 1] });
        }
    }
    return intervals;
}

// ============================= Co-edge layer (strip marching) =============================

/**
 * March the zero set of the strip function g(s, t) across a co-edge side's SHARED sample
 * arrays (spec 6.2/6.3 step 2): per sample column, every t root is found by brackets plus
 * safeguarded Newton; roots in neighboring columns chain into branches by linear prediction.
 * The result lives at exactly the shared s samples, which is what makes seam stitching exact
 * downstream.
 *
 * normals and points are swSweepEmit's sideNormals / edgePoints arrays for one side.
 * Returns an array of branches { startColumn, endColumn, samples : [{ sampleIndex, t, value }] }.
 */
export function marchStripZeroCurves(strippedMotion is map, normals is array, points is array,
    tStart is number, tEnd is number, stationCount is number, tTolerance is number) returns array
{
    const columnCount = size(normals);
    const stationSpacing = (tEnd - tStart) / (stationCount - 1);
    var rootsPerColumn = makeArray(columnCount);
    var claimedPerColumn = makeArray(columnCount);
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        rootsPerColumn[columnIndex] = findContactFunctionRoots(strippedMotion, normals[columnIndex],
            points[columnIndex], tStart, tEnd, stationCount, tTolerance);
        claimedPerColumn[columnIndex] = makeArray(size(rootsPerColumn[columnIndex]), false);
    }

    var branches = [];
    for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
    {
        for (var rootIndex = 0; rootIndex < size(rootsPerColumn[columnIndex]); rootIndex += 1)
        {
            if (claimedPerColumn[columnIndex][rootIndex])
            {
                continue;
            }
            claimedPerColumn[columnIndex][rootIndex] = true;
            var samples = [{
                        "sampleIndex" : columnIndex,
                        "t" : rootsPerColumn[columnIndex][rootIndex].t,
                        "value" : rootsPerColumn[columnIndex][rootIndex].value
                    }];
            var previousT = undefined;
            var currentT = rootsPerColumn[columnIndex][rootIndex].t;
            for (var nextColumn = columnIndex + 1; nextColumn < columnCount; nextColumn += 1)
            {
                // First link: nearest root within a generous window (the local slope is still
                // unknown). Later links: linear prediction with a slope-aware window.
                const predicted = previousT == undefined ? currentT : 2 * currentT - previousT;
                const window = previousT == undefined ? 4 * stationSpacing :
                    2 * stationSpacing + 2 * abs(currentT - previousT);
                var bestIndex = undefined;
                var bestDistance = window;
                for (var candidateIndex = 0; candidateIndex < size(rootsPerColumn[nextColumn]); candidateIndex += 1)
                {
                    if (claimedPerColumn[nextColumn][candidateIndex])
                    {
                        continue;
                    }
                    const distance = abs(rootsPerColumn[nextColumn][candidateIndex].t - predicted);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = candidateIndex;
                    }
                }
                if (bestIndex == undefined)
                {
                    break;
                }
                claimedPerColumn[nextColumn][bestIndex] = true;
                previousT = currentT;
                currentT = rootsPerColumn[nextColumn][bestIndex].t;
                samples = append(samples, {
                            "sampleIndex" : nextColumn,
                            "t" : currentT,
                            "value" : rootsPerColumn[nextColumn][bestIndex].value
                        });
            }
            branches = append(branches, {
                        "startColumn" : samples[0].sampleIndex,
                        "endColumn" : samples[size(samples) - 1].sampleIndex,
                        "samples" : samples
                    });
        }
    }
    return branches;
}

// ============================= Section layer =============================

/**
 * The pointwise envelope function and its full gradient at one (u, v, t) - the polish and
 * marching evaluator (order-2 surface derivatives; rational-correct through the
 * splineRefinementUtils evaluators).
 * Returns { value, uDerivative, vDerivative, tDerivative }.
 */
export function evaluateEnvelopeGradientPointwise(strippedMotion is map, strippedSurface is map,
    u is number, v is number, t is number) returns map
{
    const derivatives = evaluateBSplineSurfaceDerivatives(strippedSurface, u, v, 2, 2);
    const surfacePoint = derivatives[0][0];
    const uTangent = derivatives[1][0];
    const vTangent = derivatives[0][1];
    const normal = cross(uTangent, vTangent);
    const uNormalDerivative = cross(derivatives[2][0], vTangent) + cross(uTangent, derivatives[1][1]);
    const vNormalDerivative = cross(derivatives[1][1], vTangent) + cross(uTangent, derivatives[0][2]);
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * surfacePoint + sample.translationDerivative;
    const acceleration = sample.rotationSecondDerivative * surfacePoint + sample.translationSecondDerivative;
    return {
            "value" : dot(sample.rotation * normal, velocity),
            "uDerivative" : dot(sample.rotation * uNormalDerivative, velocity) +
                dot(sample.rotation * normal, sample.rotationDerivative * uTangent),
            "vDerivative" : dot(sample.rotation * vNormalDerivative, velocity) +
                dot(sample.rotation * normal, sample.rotationDerivative * vTangent),
            "tDerivative" : dot(sample.rotationDerivative * normal, velocity) +
                dot(sample.rotation * normal, acceleration)
        };
}

/**
 * March the section p-curve f(., ., tGlobal) = 0 in the uv domain from startUv toward endUv
 * (spec 6.3 step 4): predictor perpendicular to the gradient, corrector along it (1-3 Newton
 * steps per station). Marching stops when the end anchor is within one step, when the
 * gradient degenerates, or when the step budget runs out.
 *
 * startUv / endUv are [u, v] arrays in the surface's knot domain. options: { stepSize (uv
 * units), maxSteps (default 400), tolerance (residual, default 1e-10) }.
 * Returns { uvPoints {array of [u, v]}, reachedEnd {boolean} }.
 */
export function marchSectionCurve(strippedMotion is map, strippedSurface is map, tGlobal is number,
    startUv is array, endUv is array, options is map) returns map
{
    const stepSize = options.stepSize;
    const maxSteps = options.maxSteps == undefined ? 400 : options.maxSteps;
    const tolerance = options.tolerance == undefined ? 1e-10 : options.tolerance;
    const domain = knotDomainOfStrippedSurface(strippedSurface);

    var uv = correctOntoSection(strippedMotion, strippedSurface, tGlobal, startUv, domain, tolerance);
    var uvPoints = [uv];
    var previousTangent = undefined;
    var reachedEnd = false;
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
        if (previousTangent == undefined)
        {
            if (tangent[0] * (endUv[0] - uv[0]) + tangent[1] * (endUv[1] - uv[1]) < 0)
            {
                tangent = [-tangent[0], -tangent[1]];
            }
        }
        else if (tangent[0] * previousTangent[0] + tangent[1] * previousTangent[1] < 0)
        {
            tangent = [-tangent[0], -tangent[1]];
        }
        var predicted = [uv[0] + stepSize * tangent[0], uv[1] + stepSize * tangent[1]];
        predicted = [clampToRange(predicted[0], domain.uMin, domain.uMax),
            clampToRange(predicted[1], domain.vMin, domain.vMax)];
        const corrected = correctOntoSection(strippedMotion, strippedSurface, tGlobal, predicted, domain, tolerance);
        uvPoints = append(uvPoints, corrected);
        previousTangent = tangent;
        const remainingSquared = (corrected[0] - endUv[0]) ^ 2 + (corrected[1] - endUv[1]) ^ 2;
        if (remainingSquared <= stepSize ^ 2)
        {
            uvPoints = append(uvPoints, correctOntoSection(strippedMotion, strippedSurface, tGlobal, endUv, domain, tolerance));
            reachedEnd = true;
            break;
        }
        uv = corrected;
    }
    return { "uvPoints" : uvPoints, "reachedEnd" : reachedEnd };
}

/**
 * Resample a marched section polyline at fixed fractions of its LIFTED (3D) arc length,
 * re-Newton every resampled point onto f = 0, and lift it rigidly (Phi = A S + b). The fixed
 * fractions q are what make sections from different stations line up into the (q, t) fit grid.
 * Returns { uvSamples, liftedSamples, worstResidual }.
 */
export function resampleAndPolishSection(strippedMotion is map, strippedSurface is map, tGlobal is number,
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
    const domain = knotDomainOfStrippedSurface(strippedSurface);

    var uvSamples = makeArray(sampleCount);
    var liftedSamples = makeArray(sampleCount);
    var worstResidual = 0;
    var cursor = 0;
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1)
    {
        const targetLength = totalLength * sampleIndex / (sampleCount - 1);
        while (cursor < pointCount - 2 && cumulativeLengths[cursor + 1] < targetLength)
        {
            cursor += 1;
        }
        const segmentLength = cumulativeLengths[cursor + 1] - cumulativeLengths[cursor];
        const fraction = segmentLength < 1e-300 ? 0 : (targetLength - cumulativeLengths[cursor]) / segmentLength;
        var uv = [uvPoints[cursor][0] + fraction * (uvPoints[cursor + 1][0] - uvPoints[cursor][0]),
            uvPoints[cursor][1] + fraction * (uvPoints[cursor + 1][1] - uvPoints[cursor][1])];
        uv = correctOntoSection(strippedMotion, strippedSurface, tGlobal, uv, domain, tolerance);
        const residual = abs(evaluateEnvelopePointwise(strippedMotion, strippedSurface, uv[0], uv[1], tGlobal));
        worstResidual = max(worstResidual, residual);
        uvSamples[sampleIndex] = uv;
        liftedSamples[sampleIndex] = liftContactPoint(strippedMotion, strippedSurface, uv[0], uv[1], tGlobal);
    }
    return { "uvSamples" : uvSamples, "liftedSamples" : liftedSamples, "worstResidual" : worstResidual };
}

/** The rigid lift of one contact point: Phi(u, v, t) = A(t) S(u, v) + b(t). */
export function liftContactPoint(strippedMotion is map, strippedSurface is map, u is number, v is number,
    t is number) returns Vector
{
    const derivatives = evaluateBSplineSurfaceDerivatives(strippedSurface, u, v, 0, 0);
    const sample = evaluateMotionSample(strippedMotion, t);
    return sample.rotation * derivatives[0][0] + sample.translation;
}

// ===================== Internal helpers (pointwise layers) =====================

/** Bisection-safeguarded Newton on the contact function inside a sign-change bracket. */
function refineContactRoot(strippedMotion is map, normal is Vector, point is Vector,
    bracketLow is number, bracketHigh is number, valueLow is number, valueHigh is number,
    tTolerance is number) returns map
{
    var low = bracketLow;
    var high = bracketHigh;
    var lowValue = valueLow;
    var t = 0.5 * (low + high);
    var value = 0;
    for (var iteration = 0; iteration < 80; iteration += 1)
    {
        value = evaluateContactFunctionAtPoint(strippedMotion, normal, point, t);
        if (value == 0)
        {
            break;
        }
        if (value * lowValue > 0)
        {
            low = t;
            lowValue = value;
        }
        else
        {
            high = t;
        }
        const derivative = evaluateContactFunctionTimeDerivative(strippedMotion, normal, point, t);
        var next = derivative == 0 ? undefined : t - value / derivative;
        if (next == undefined || next <= low || next >= high)
        {
            next = 0.5 * (low + high);
        }
        if (abs(next - t) < tTolerance)
        {
            t = next;
            value = evaluateContactFunctionAtPoint(strippedMotion, normal, point, t);
            break;
        }
        t = next;
    }
    return { "t" : t, "value" : value };
}

/** Newton corrector onto f(., ., tGlobal) = 0 along the uv gradient, clamped to the domain. */
function correctOntoSection(strippedMotion is map, strippedSurface is map, tGlobal is number,
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
function knotDomainOfStrippedSurface(strippedSurface is map) returns map
{
    return {
            "uMin" : strippedSurface.uKnots[strippedSurface.uDegree],
            "uMax" : strippedSurface.uKnots[size(strippedSurface.uKnots) - strippedSurface.uDegree - 1],
            "vMin" : strippedSurface.vKnots[strippedSurface.vDegree],
            "vMax" : strippedSurface.vKnots[size(strippedSurface.vKnots) - strippedSurface.vDegree - 1]
        };
}

/** Clamp a value to [low, high]. */
export function clampToRange(value is number, low is number, high is number) returns number
{
    return value < low ? low : (value > high ? high : value);
}

// ===================== Internal helpers (factored layers) =====================

/** Solve a 3x3 linear system by Cramer's rule; undefined when the determinant degenerates. */
function solveThreeByThree(rows is array, rightHandSide is array)
{
    const determinant =
        rows[0][0] * (rows[1][1] * rows[2][2] - rows[1][2] * rows[2][1]) -
        rows[0][1] * (rows[1][0] * rows[2][2] - rows[1][2] * rows[2][0]) +
        rows[0][2] * (rows[1][0] * rows[2][1] - rows[1][1] * rows[2][0]);
    if (abs(determinant) < 1e-30)
    {
        return undefined;
    }
    var solution = makeArray(3, 0);
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        var modified = [
                [rows[0][0], rows[0][1], rows[0][2]],
                [rows[1][0], rows[1][1], rows[1][2]],
                [rows[2][0], rows[2][1], rows[2][2]]
            ];
        for (var rowIndex = 0; rowIndex < 3; rowIndex += 1)
        {
            modified[rowIndex][columnIndex] = rightHandSide[rowIndex];
        }
        solution[columnIndex] =
            (modified[0][0] * (modified[1][1] * modified[2][2] - modified[1][2] * modified[2][1]) -
                    modified[0][1] * (modified[1][0] * modified[2][2] - modified[1][2] * modified[2][0]) +
                    modified[0][2] * (modified[1][0] * modified[2][1] - modified[1][1] * modified[2][0])) / determinant;
    }
    return solution;
}

/** Clamp a value to [0, 1]. */
function clampToUnit(value is number) returns number
{
    return value < 0 ? 0 : (value > 1 ? 1 : value);
}

/** Clamp a value's magnitude. */
function clampMagnitude(value is number, limit is number) returns number
{
    return value > limit ? limit : (value < -limit ? -limit : value);
}

// ============================= Self-test fixtures =============================

/**
 * The seam fixture: z = 0.8 ((u - 0.5)^3/3 + 1/24) v(1 - v), so that
 * z_u = 0.8 (u - 0.5)^2 v(1 - v) peaks against BOTH u edges - one contact lobe per edge under
 * constant w = (1, 0, 0.02), the same physical band on a closed face.
 */
function seamFixtureSurface() returns map
{
    const uCoefficients = [0, 1 / 12, 0, 1 / 12];
    const vCoefficients = [0, 0.5, 0];
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville3[i], greville2[j], 0.8 * uCoefficients[i] * vCoefficients[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/** A flat plane z = 0 at degrees (3, 2) - the sliding-audit fixture. */
function planeFixtureSurface() returns map
{
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville3[i], greville2[j], 0);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * A trim loop that WINDS the u seam: v = base + amplitude sin(2 pi u), sampled over one full
 * period with both ends included. Its first and last samples are the same point one period
 * apart, which is the stored form the cyclic mask expects of a winding loop.
 */
function sinusoidalSeamLoop(base is number, amplitude is number, segmentCount is number) returns array
{
    var samples = makeArray(segmentCount + 1, vector(0, base));
    for (var index = 0; index <= segmentCount; index += 1)
    {
        const u = index / segmentCount;
        samples[index] = vector(u, base + amplitude * sin(2 * PI * u * radian));
    }
    return samples;
}

/**
 * A small circular hole centred on the seam, UNWRAPPED so its u runs from minus to plus the
 * radius. Returned as a bare point array - the shape the census reads as a closed loop of
 * winding zero.
 */
function seamStraddlingHole(radius is number, sampleCount is number) returns array
{
    var samples = makeArray(sampleCount, vector(0, 0.5));
    for (var index = 0; index < sampleCount; index += 1)
    {
        const angle = 2 * PI * index / sampleCount * radian;
        samples[index] = vector(radius * cos(angle), 0.5 + radius * sin(angle));
    }
    return samples;
}

/**
 * The pole fixture: S(u, v) = v (u, 1, c(u)) with c = 0.3 u^2 - 0.2 u^3, degrees (3, 1). The
 * v = 0 control row is collapsed onto the origin, so S_u vanishes along it and the normal with
 * it - f is identically zero on that whole boundary, byte-exactly, because the control-row
 * differences the coefficient nets are built from are exactly zero.
 *
 * Under velocity (a(t), 0, 1) the envelope function is exactly f = v (1 - a(t) c'(u)) with
 * c' = 0.6 u (1 - u), so the contact set is two sheets at u = 0.5 +/- sqrt(0.25 - 1 / (0.6 a)) -
 * separate everywhere except along v = 0.
 */
function poleFixtureSurface() returns map
{
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const heightCoefficients = [0, 0, 0.1, 0.1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        net[i] = [vector(0, 0, 0), vector(greville3[i], 1, heightCoefficients[i])];
    }
    return {
            "uDegree" : 3, "vDegree" : 1,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}
