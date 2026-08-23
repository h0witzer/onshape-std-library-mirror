FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

// Non-standard imports - same-document element imports; fix the ids/versions on paste. For MCP
// harness runs the payload inlines the module bodies instead of resolving these lines.
import(path : "0000000000000000000000aa", version : "0000000000000000000000bb"); //swOrientation.fs
import(path : "0000000000000000000000cc", version : "0000000000000000000000dd"); //swEnvelopeMath.fs, swFunnelSolver.fs
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * Self test for swOrientation.fs (spec section 6.6). Selection-free and context-free: every
 * fixture is a hand-built spline whose orientation answers are known in CLOSED FORM, so a wrong
 * sign cannot hide behind a plausible-looking number.
 *
 * The fixture that carries the smooth-face half is a PARABOLIC CYLINDER, S = (u, v, c u^2 / 2)
 * at degrees (2, 1), swept by a translation whose velocity is (1, 0, w_z(t)). It is the
 * smallest surface for which the whole invariant is nontrivial and exact:
 *
 *     N = S_u x S_v = (-c u, 0, 1)          f = w_z(t) - c u
 *     f_u = -c        f_v = 0               f_t = w_z'(t)
 *     alpha = 1       beta = 0              lambda = w_z'(t) + c
 *
 * and the contact point's own velocity works out to (lambda / c) S_u - so lambda = 0 is
 * literally where the contact point stops and the envelope cusps, which is what makes this
 * fixture able to dial the fold on and off with one number. With w_z LINEAR in t the funnel is
 * one sheet with constant lambda (self test 1); with w_z QUADRATIC each column of the co-edge
 * strip has exactly two roots whose time derivatives must alternate (self test 2), and the two
 * sheets land on OPPOSITE sides of lambda = 0 - one is the real envelope, the other is
 * occluded. That pair is the point of the second test: alternation alone does not orient
 * anything, it only says f_t flips. Both signs are needed.
 *
 * Split in two features so each MCP harness payload stays inside size discipline: the first
 * needs only swOrientation and the spline evaluators, the second also needs the funnel solver's
 * strip marching.
 */

// ============================= Self test 1: the smooth-face invariant =============================

annotation { "Feature Type Name" : "Sweep Orientation Self Test" }
export const sweepOrientationSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        var checks = 0;

        // The fixture: c = -2 (a solid under a downward parabola, so the surface is convex and
        // its outward normal is the +z-ish one), velocity z linear and negative so the contact
        // ruling u* = w_z / c sits inside [0, 1] for the whole sweep.
        const c = -2;
        const z0 = -0.6;
        const z1 = -0.5;
        const surface = parabolicCylinderSurface(c);
        const motion = translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0],
                [z0, z0 + z1 / 2, z0 + z1]);

        // ---------- The invariant against its closed form ----------
        // f_t = z1 and lambda = z1 + c EVERYWHERE on this funnel, independent of u, v and t.
        const expectedLambda = z1 + c;
        var worstLambda = 0;
        var worstTimeDerivative = 0;
        var worstValue = 0;
        var worstResidual = 0;
        var worstNormal = 0;
        var worstCoordinates = 0;
        for (var stationIndex = 0; stationIndex <= 8; stationIndex += 1)
        {
            const t = stationIndex / 8;
            const u = contactRuling(c, z0, z1, t);
            for (var vIndex = 0; vIndex <= 4; vIndex += 1)
            {
                const v = vIndex / 4;
                const orientation = envelopeOrientationSample(motion, surface, u, v, t);
                worstValue = max(worstValue, abs(orientation.value));
                worstLambda = max(worstLambda, abs(orientation.lambda - expectedLambda));
                worstTimeDerivative = max(worstTimeDerivative, abs(orientation.tDerivative - z1));
                worstResidual = max(worstResidual, orientation.tangentialResidual);
                worstNormal = max(worstNormal, norm(orientation.outwardNormal -
                            normalize(vector(-c * u, 0, 1))));
                // On this fixture the velocity IS S_u exactly, so (alpha, beta) = (1, 0).
                worstCoordinates = max(worstCoordinates,
                    abs(orientation.alpha - 1) + abs(orientation.beta));
            }
        }
        println("[ORIENTATION SELF TEST] on-funnel |f| " ~ worstValue ~ "; velocity out of the " ~
            "tangent plane " ~ worstResidual ~ "; (alpha, beta) vs (1, 0) " ~ worstCoordinates);
        println("[ORIENTATION SELF TEST] lambda vs the closed form " ~ expectedLambda ~ ": " ~ worstLambda ~
            "; f_t vs " ~ z1 ~ ": " ~ worstTimeDerivative);
        println("[ORIENTATION SELF TEST] outward normal vs the transported unit normal: " ~ worstNormal);
        checks += 4;
        if (worstValue > 1e-15)
        {
            failures = failures ~ " the closed-form contact ruling is off the funnel by " ~ worstValue ~ ".";
        }
        if (worstLambda > 1e-14 || worstTimeDerivative > 1e-14)
        {
            failures = failures ~ " lambda / f_t disagree with the closed form by " ~
                max(worstLambda, worstTimeDerivative) ~ ".";
        }
        if (worstResidual > 1e-14 || worstCoordinates > 1e-14)
        {
            failures = failures ~ " the velocity's tangent coordinates are wrong by " ~
                max(worstResidual, worstCoordinates) ~ ".";
        }
        if (worstNormal > 1e-15)
        {
            failures = failures ~ " the outward normal is off by " ~ worstNormal ~ ".";
        }

        // ---------- The chart identity, against finite differences of the chart itself ----------
        // Psi(u, v) = Phi(u, v, t(u)) with t(u) = (c u - z0) / z1 solving f = 0. The identity
        // says Psi_u x Psi_v = (lambda / f_t) A N, and on this fixture that is exactly
        // ((z1 + c) / z1) (-c u, 0, 1) - an independent geometric check of the same two numbers.
        const chartU = 0.42;
        const chartV = 0.55;
        const step = 1e-5;
        const chartNormal = cross(
                (1 / (2 * step)) * (funnelChartPoint(motion, surface, c, z0, z1, chartU + step, chartV) -
                    funnelChartPoint(motion, surface, c, z0, z1, chartU - step, chartV)),
                (1 / (2 * step)) * (funnelChartPoint(motion, surface, c, z0, z1, chartU, chartV + step) -
                    funnelChartPoint(motion, surface, c, z0, z1, chartU, chartV - step)));
        const predictedNormal = (expectedLambda / z1) * vector(-c * chartU, 0, 1);
        const chartError = norm(chartNormal - predictedNormal) / norm(predictedNormal);
        println("[ORIENTATION SELF TEST] chart normal vs (lambda / f_t) A N: relative " ~ chartError ~
            " (chart " ~ chartNormal ~ ", predicted " ~ predictedNormal ~ ")");
        checks += 1;
        if (chartError > 1e-8)
        {
            failures = failures ~ " the (u, v) chart normal disagrees with (lambda / f_t) A N by " ~
                chartError ~ ".";
        }

        // ---------- The patch verdict, and that the q direction is what flips it ----------
        const forwardQ = fitPatchOrientationAt(motion, surface, contactRuling(c, z0, z1, 0.5), 0.5, 0.5, [0, 1]);
        const backwardQ = fitPatchOrientationAt(motion, surface, contactRuling(c, z0, z1, 0.5), 0.5, 0.5, [0, -1]);
        println("[ORIENTATION SELF TEST] lambda sign " ~ forwardQ.lambdaSign ~ ", f_t sign " ~
            forwardQ.timeDerivativeSign ~ ", chart sign " ~ forwardQ.chartSign ~
            "; q along +v: kappa " ~ forwardQ.kappaSign ~ " faces outward " ~ forwardQ.facesOutward ~
            "; q along -v: kappa " ~ backwardQ.kappaSign ~ " faces outward " ~ backwardQ.facesOutward);
        checks += 1;
        if (forwardQ.lambdaSign != -1 || forwardQ.timeDerivativeSign != -1 || forwardQ.chartSign != 1)
        {
            failures = failures ~ " the fixture's signs came out (" ~ forwardQ.lambdaSign ~ ", " ~
                forwardQ.timeDerivativeSign ~ ", " ~ forwardQ.chartSign ~ ") instead of (-1, -1, 1).";
        }
        if (!(forwardQ.kappaSign == 1 && forwardQ.facesOutward) ||
            !(backwardQ.kappaSign == -1 && !backwardQ.facesOutward))
        {
            failures = failures ~ " reversing q did not reverse the patch verdict.";
        }

        // ---------- The grid certificate, and its finite-difference cross-check ----------
        const grid = buildFunnelFitGrid(motion, surface, c, z0, z1, 9, 7, false);
        const certificate = certifyFitGridOrientation(motion, surface, grid.uvRows, grid.stations,
                { "liftedGrid" : grid.liftedGrid });
        println("[ORIENTATION SELF TEST] grid certificate: " ~ certificate.sampleCount ~ " samples, " ~
            "lambda sign " ~ certificate.lambdaSign ~ " consistent " ~ certificate.lambdaSignConsistent ~
            ", faces outward " ~ certificate.facesOutward ~ " unanimous " ~ certificate.verdictUnanimous ~
            ", worst fold margin " ~ certificate.worstFoldMargin ~ ", difference check " ~
            certificate.differenceAgreements ~ "/" ~ certificate.differenceChecked ~
            " agree, worst alignment " ~ certificate.worstDifferenceAlignment);
        checks += 2;
        if (!certificate.consistent || !certificate.facesOutward || certificate.lambdaSign != -1)
        {
            failures = failures ~ " the grid certificate did not come back consistent and outward.";
        }
        if (certificate.differenceChecked == 0 || certificate.differenceDisagreements != 0)
        {
            failures = failures ~ " the finite-difference cross-check disagreed with the analytic " ~
                "verdict at " ~ certificate.differenceDisagreements ~ " of " ~
                certificate.differenceChecked ~ " samples.";
        }

        const reversedGrid = buildFunnelFitGrid(motion, surface, c, z0, z1, 9, 7, true);
        const reversedCertificate = certifyFitGridOrientation(motion, surface, reversedGrid.uvRows,
                reversedGrid.stations, { "liftedGrid" : reversedGrid.liftedGrid });
        println("[ORIENTATION SELF TEST] the same grid with q reversed: faces outward " ~
            reversedCertificate.facesOutward ~ ", unanimous " ~ reversedCertificate.verdictUnanimous ~
            ", difference check " ~ reversedCertificate.differenceAgreements ~ "/" ~
            reversedCertificate.differenceChecked ~ " agree");
        checks += 1;
        if (reversedCertificate.facesOutward || !reversedCertificate.verdictUnanimous ||
            reversedCertificate.differenceDisagreements != 0)
        {
            failures = failures ~ " reversing the grid's q direction did not flip the certificate " ~
                "while keeping the two routes in agreement.";
        }

        // ---------- Cap classification ----------
        // At t = 0 the fixture's f is w_z(0) - c u = -0.6 + 2 u, so the ingress cap keeps
        // u < 0.3 and the egress cap keeps u > 0.3. Sampled either side of the ruling.
        const ingressInside = classifyCapSample(motion, surface, 0.1, 0.5, 0, true, 0);
        const ingressOutside = classifyCapSample(motion, surface, 0.5, 0.5, 0, true, 0);
        const egressInside = classifyCapSample(motion, surface, 0.5, 0.5, 0, false, 0);
        println("[ORIENTATION SELF TEST] cap classification at t = 0: f(0.1) " ~ ingressInside.value ~
            " ingress keeps " ~ ingressInside.keep ~ "; f(0.5) " ~ ingressOutside.value ~
            " ingress keeps " ~ ingressOutside.keep ~ ", egress keeps " ~ egressInside.keep);
        checks += 1;
        if (!ingressInside.keep || ingressOutside.keep || !egressInside.keep ||
            abs(ingressInside.value - (z0 - c * 0.1)) > 1e-15)
        {
            failures = failures ~ " cap classification disagreed with the sign of f.";
        }

        // ---------- Exact net reversal ----------
        const reversalError = checkSurfaceReversal(rationalTestSurface());
        println("[ORIENTATION SELF TEST] rational net reversed: worst point mismatch at mirrored " ~
            "parameters " ~ reversalError.pointError ~ ", worst normal-flip mismatch " ~
            reversalError.normalError);
        checks += 2;
        if (reversalError.pointError > 1e-15 || reversalError.normalError > 1e-14)
        {
            failures = failures ~ " reversing a fit net's q direction was not exact (" ~
                reversalError.pointError ~ " / " ~ reversalError.normalError ~ ").";
        }

        // ---------- The row reversal helpers ----------
        const clampedRow = reverseFitGridRow([10, 11, 12, 13, 14], false);
        const closedRow = reverseFitGridRow([10, 11, 12, 13, 14], true);
        const midRow = reverseFitGridMidRow([20, 21, 22, 23]);
        println("[ORIENTATION SELF TEST] row reversal: clamped " ~ clampedRow ~ ", closed " ~
            closedRow ~ ", midpoints " ~ midRow);
        checks += 1;
        if (clampedRow != [14, 13, 12, 11, 10] || closedRow != [10, 14, 13, 12, 11] ||
            midRow != [23, 22, 21, 20])
        {
            failures = failures ~ " a grid row reversal helper returned the wrong permutation.";
        }

        reportTestVerdict(context, id, "ORIENTATION SELF TEST", checks, failures,
            ("lambda, f_t and the outward normal match the parabolic " ~
            "cylinder's closed forms, the (u, v) chart normal is (lambda / f_t) A N by finite " ~
            "difference, the q direction is what flips the patch verdict, the grid certificate " ~
            "agrees with its own finite differences both ways round, cap classification follows " ~
            "the sign of f, and net reversal is exact."));
    });

// ============================= Self test 2: co-edges and sharp features =============================

annotation { "Feature Type Name" : "Sweep Orientation Sharp Feature Self Test" }
export const sweepOrientationSharpSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        var checks = 0;

        // ---------- Strip alternation on a co-edge with two roots per column ----------
        // Same parabolic cylinder, but the velocity's z component is now the QUADRATIC
        // w_z(t) = -2.1 + 8.8 t (1 - t). Along the co-edge v = 0 the strip function is
        // g(s, t) = 2 s + w_z(t), so each column has exactly the two roots
        // t = 0.5 -/+ 0.5 sqrt((0.4 + 8 s) / 8.8), and g_t = w_z'(t) = 8.8 (1 - 2 t) is positive
        // at the first and negative at the second. That is the alternation rule, in closed form.
        const c = -2;
        const stripMotion = translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0], [-2.1, 2.3, -2.1]);
        const columnCount = 21;
        var sampleParameters = makeArray(columnCount, 0);
        var normals = makeArray(columnCount);
        var points = makeArray(columnCount);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            const s = columnIndex / (columnCount - 1);
            sampleParameters[columnIndex] = s;
            normals[columnIndex] = vector(-c * s, 0, 1);
            points[columnIndex] = vector(s, 0, 0.5 * c * s * s);
        }
        const branches = marchStripZeroCurves(stripMotion, normals, points, 0, 1, 41, 1e-13);
        var worstRootError = 0;
        for (var branch in branches)
        {
            for (var sample in branch.samples)
            {
                const s = sampleParameters[sample.sampleIndex];
                const offset = 0.5 * sqrt((0.4 + 8 * s) / 8.8);
                worstRootError = max(worstRootError, min(abs(sample.t - (0.5 - offset)),
                            abs(sample.t - (0.5 + offset))));
            }
        }
        const oriented = orientStripBranches(stripMotion, normals, points, branches, {});
        var signs = [];
        for (var branchResult in oriented.branches)
        {
            signs = append(signs, branchResult.timeDerivativeSign);
        }
        println("[ORIENTATION SHARP SELF TEST] strip: " ~ size(branches) ~ " branches, worst root " ~
            "error vs the closed form " ~ worstRootError ~ ", branch f_t signs " ~ signs ~
            ", alternation " ~ oriented.alternationChecked ~ " pairs checked, " ~
            size(oriented.alternationViolations) ~ " violations, " ~ oriented.tangencyCount ~
            " tangencies");
        checks += 3;
        if (size(branches) != 2)
        {
            failures = failures ~ " the strip produced " ~ size(branches) ~ " branches, not 2.";
        }
        if (worstRootError > 1e-11)
        {
            failures = failures ~ " strip roots are off the closed form by " ~ worstRootError ~ ".";
        }
        if (!oriented.alternationConsistent || oriented.alternationChecked != columnCount)
        {
            failures = failures ~ " the alternation certificate failed (" ~ oriented.alternationChecked ~
                " pairs, " ~ size(oriented.alternationViolations) ~ " violations).";
        }
        if (size(signs) == 2 && signs[0] * signs[1] != -1)
        {
            failures = failures ~ " the two branches did not come out with opposite f_t signs.";
        }

        // Both branches pass alternation, and their lambda signs are OPPOSITE - the mid-column
        // sample of each. That is the whole reason orientation needs lambda as well as f_t: the
        // alternation rule says only that f_t flips between neighboring roots, and here one
        // sheet is the real envelope while the other is occluded (its contact point runs
        // backwards, lambda > 0 against the other's lambda < 0).
        const stripSurface = parabolicCylinderSurface(c);
        var lambdaSigns = [];
        for (var branch in branches)
        {
            const midSample = branch.samples[floor(size(branch.samples) / 2)];
            const orientation = envelopeOrientationSample(stripMotion, stripSurface,
                sampleParameters[midSample.sampleIndex], 0, midSample.t);
            lambdaSigns = append(lambdaSigns, orientation.lambdaSign);
        }
        println("[ORIENTATION SHARP SELF TEST] the two sheets' lambda signs at mid column: " ~ lambdaSigns);
        checks += 1;
        if (size(lambdaSigns) == 2 && lambdaSigns[0] * lambdaSigns[1] != -1)
        {
            failures = failures ~ " the two sheets did not land on opposite sides of lambda = 0.";
        }

        // ---------- Co-edge sense and direction bookkeeping ----------
        // The left face's co-edge runs +s and the right face's runs -s, and an envelope co-edge
        // follows sign(lambda / f_t) of that. With lambda fixed on a component, the two branches
        // above generate co-edges running OPPOSITE ways - which is what the alternation rule
        // buys, stated as topology rather than as a sign.
        const leftSense = coEdgeSense("left");
        const rightSense = coEdgeSense("right");
        const alongPositive = envelopeCoEdgeDirection(leftSense, -1, -1);
        const alongNegative = envelopeCoEdgeDirection(leftSense, -1, 1);
        println("[ORIENTATION SHARP SELF TEST] senses left " ~ leftSense ~ " right " ~ rightSense ~
            "; envelope directions for the two f_t signs " ~ alongPositive ~ " / " ~ alongNegative ~
            "; partner of the first " ~ partnerCoEdgeDirection(alongPositive));
        checks += 1;
        if (leftSense != 1 || rightSense != -1 || alongPositive != 1 || alongNegative != -1 ||
            partnerCoEdgeDirection(alongPositive) != -1)
        {
            failures = failures ~ " the co-edge direction bookkeeping is wrong.";
        }

        // ---------- Sharp-edge face orientation ----------
        // A convex ridge along +x whose two side normals sit at +/-45 degrees about +z, under a
        // translation in +y. The two side contact functions differ in sign, so the edge grazes;
        // the sheet's parametric normal (A e') x velocity is then +z, squarely inside the cone.
        const ridgeMotion = translationMotionFromQuadraticVelocity([0, 0, 0], [1, 1, 1], [0, 0, 0]);
        const leftNormal = normalize(vector(0, -1, 1));
        const rightNormal = normalize(vector(0, 1, 1));
        const ridge = orientSharpEdgeFace(ridgeMotion, vector(0, 0, 0), vector(1, 0, 0),
                leftNormal, rightNormal, 0.5);
        const flippedRidge = orientSharpEdgeFace(ridgeMotion, vector(0, 0, 0), vector(-1, 0, 0),
                leftNormal, rightNormal, 0.5);
        println("[ORIENTATION SHARP SELF TEST] ridge along +x: inside cone " ~ ridge.insideCone ~
            ", flip " ~ ridge.flipRequired ~ ", outward " ~ ridge.outwardNormal ~ ", margin " ~
            ridge.coneMargin ~ "; the same ridge parameterized backwards: flip " ~
            flippedRidge.flipRequired ~ ", outward " ~ flippedRidge.outwardNormal);
        checks += 2;
        if (!ridge.insideCone || ridge.flipRequired ||
            norm(ridge.outwardNormal - vector(0, 0, 1)) > 1e-15)
        {
            failures = failures ~ " the sharp-edge sheet's outward normal came out wrong.";
        }
        if (!flippedRidge.insideCone || !flippedRidge.flipRequired ||
            norm(flippedRidge.outwardNormal - vector(0, 0, 1)) > 1e-15)
        {
            failures = failures ~ " reversing the edge parameterization did not flip the sheet " ~
                "while keeping the outward normal.";
        }

        // The same ridge under a translation along -z grazes nowhere: both side contact
        // functions are negative, and neither candidate normal lies in the cone.
        const plungeMotion = translationMotionFromQuadraticVelocity([0, 0, 0], [0, 0, 0], [-1, -1, -1]);
        const plunge = orientSharpEdgeFace(plungeMotion, vector(0, 0, 0), vector(1, 0, 0),
                leftNormal, rightNormal, 0.5);
        println("[ORIENTATION SHARP SELF TEST] the same ridge plunging along -z: inside cone " ~
            plunge.insideCone ~ ", degenerate " ~ plunge.degenerate);
        checks += 1;
        if (plunge.insideCone || !plunge.degenerate)
        {
            failures = failures ~ " a non-grazing sharp edge was not reported as outside the cone.";
        }

        // ---------- Sharp-vertex co-edge orientation ----------
        // The ridge's start vertex at the origin, its edge running toward +x, the face's outward
        // normal +z, the vertex trajectory running +y. The face has to be on the co-edge's left,
        // which puts the co-edge on -y: n x w must point toward +x, and (0,0,1) x (0,-1,0) does.
        const vertexCoEdge = orientSharpVertexCoEdge(ridgeMotion, vector(0, 0, 0), vector(1, 0, 0),
                vector(0, 0, 1), 0.5);
        println("[ORIENTATION SHARP SELF TEST] sharp-vertex co-edge: direction " ~
            vertexCoEdge.direction ~ ", tangent " ~ vertexCoEdge.tangent ~ ", test " ~
            vertexCoEdge.testValue);
        checks += 1;
        if (vertexCoEdge.direction != -1 || norm(vertexCoEdge.tangent - vector(0, -1, 0)) > 1e-15 ||
            norm(cross(vector(0, 0, 1), vertexCoEdge.tangent) - vector(1, 0, 0)) > 1e-15)
        {
            failures = failures ~ " the sharp-vertex co-edge was oriented the wrong way.";
        }

        // The far vertex of the same edge takes -e'(s1) as its interior direction, so its
        // co-edge runs the other way - which is exactly what closes the loop around the face.
        const farVertex = orientSharpVertexCoEdge(ridgeMotion, vector(0, 0, 0), vector(-1, 0, 0),
                vector(0, 0, 1), 0.5);
        println("[ORIENTATION SHARP SELF TEST] the edge's far vertex: direction " ~ farVertex.direction);
        checks += 1;
        if (farVertex.direction != 1)
        {
            failures = failures ~ " the far vertex's co-edge did not reverse.";
        }

        reportTestVerdict(context, id, "ORIENTATION SHARP SELF TEST", checks, failures,
            ("the two strip roots per column alternate in f_t exactly " ~
            "as Rolle requires and land on opposite sides of lambda = 0, the co-edge direction " ~
            "bookkeeping follows sign(lambda / f_t) times the input sense, a convex ridge's " ~
            "sheet normal is cone-selected and flips with the edge parameterization, a " ~
            "non-grazing ridge is reported outside the cone, and the two ends of one sharp " ~
            "edge orient their vertex co-edges oppositely."));
    });

// ============================= Self test 3: closed rows, poles, and the fold =============================

/**
 * The two things the rectangle path cannot reach, on a funnel with genuinely CLOSED section
 * loops: the closed-row reversal (which holds q's origin fixed instead of reversing outright),
 * collapsed pole rows, and the lambda-sign fold certificate actually FIRING.
 *
 * Fixture is the funnel solver's island bump, z_u = 0.8 u(1-u) v(1-v) under velocity
 * (1, 0, w_z(t)) with w_z = 0.134 - 0.4t + 0.4t^2. Its level sets are closed loops around
 * (0.5, 0.5), born at t = 0.3 and dying at t = 0.7 where w_z meets the bump's peak of 0.05.
 * Here alpha = 1 and beta = 0 again, so
 *
 *     f = w_z - z_u,   f_u = -z_uu,   f_t = w_z',   lambda = w_z' + z_uu
 *
 * and z_uu = 0.8(1 - 2u) v(1 - v) CHANGES SIGN at u = 0.5 - which the loop encircles. On the
 * loop max |z_uu| = 0.2 sqrt(1 - 20 w_z), attained at the v = 0.5 extremes, so lambda keeps one
 * sign exactly where |w_z'| > 0.2 sqrt(1 - 20 w_z). That splits the fixture in two:
 *
 *   - t in [0.30, 0.36] (just after birth): lambda one sign, worst margin 0.031 - an
 *     orientable component, and the clean closed-row test.
 *   - t in [0.44, 0.60] (the middle band): lambda spans both signs at EVERY station - the
 *     envelope folds, and the certificate has to say so.
 *
 * That second half is the point. It means this bump fixture is a LOCALLY SELF-INTERSECTING
 * sweep through its middle, which v1 must reject (spec 3, spec 10 detector 1) - and it is the
 * only live exercise the fold certificate has.
 */
annotation { "Feature Type Name" : "Sweep Orientation Grid Self Test" }
export const sweepOrientationGridSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        var checks = 0;

        const surface = islandBumpSurface();
        const motion = translationMotionFromQuadraticVelocity([1, 1, 1], [0, 0, 0], [0.134, -0.066, 0.134]);
        const qCount = 8;

        // ---------- The orientable window, with a collapsed birth pole ----------
        const simpleGrid = buildLoopGrid(motion, surface, [0.30, 0.315, 0.33, 0.345, 0.36], qCount);
        const simple = certifyFitGridOrientation(motion, surface, simpleGrid.uvRows,
                simpleGrid.stations, { "liftedGrid" : simpleGrid.liftedGrid });
        const expectedSamples = (size(simpleGrid.stations) - 1) * qCount;
        println("[ORIENTATION GRID SELF TEST] orientable window: " ~ simple.sampleCount ~ " samples " ~
            "(pole row skipped, expected " ~ expectedSamples ~ "), lambda sign " ~ simple.lambdaSign ~
            " consistent " ~ simple.lambdaSignConsistent ~ ", faces outward " ~ simple.facesOutward ~
            " unanimous " ~ simple.verdictUnanimous ~ ", worst fold margin " ~ simple.worstFoldMargin ~
            ", difference " ~ simple.differenceAgreements ~ "/" ~ simple.differenceChecked ~
            " agree, stationary " ~ simple.stationarySamples ~ ", consistent " ~ simple.consistent);
        checks += 3;
        if (simple.sampleCount != expectedSamples)
        {
            failures = failures ~ " the collapsed pole row was not skipped (" ~ simple.sampleCount ~
                " samples against " ~ expectedSamples ~ ").";
        }
        if (!simple.consistent || simple.lambdaSign != -1 || !simple.verdictUnanimous)
        {
            failures = failures ~ " the orientable window did not certify (lambda sign " ~
                simple.lambdaSign ~ ", consistent " ~ simple.consistent ~ ").";
        }
        if (simple.differenceChecked == 0 || simple.differenceDisagreements != 0)
        {
            failures = failures ~ " the finite-difference route disagreed on " ~
                simple.differenceDisagreements ~ " of " ~ simple.differenceChecked ~ " closed-row samples.";
        }

        // ---------- The same window with every closed row reversed ----------
        // A closed row reverses by holding sample 0 and reversing the rest, so q's origin does
        // not move. The verdict must flip and the two routes must still agree.
        // EVERY row, pole included. Reversing a collapsed row is a no-op, and reversing only
        // some of them would leave the grid inconsistently ordered between rows - which is
        // precisely what made this check fail the first time it ran.
        var reversedUv = simpleGrid.uvRows;
        var reversedLifted = simpleGrid.liftedGrid;
        for (var rowIndex = 0; rowIndex < size(simpleGrid.stations); rowIndex += 1)
        {
            reversedUv[rowIndex] = reverseFitGridRow(simpleGrid.uvRows[rowIndex], true);
            reversedLifted[rowIndex] = reverseFitGridRow(simpleGrid.liftedGrid[rowIndex], true);
        }
        const reversed = certifyFitGridOrientation(motion, surface, reversedUv, simpleGrid.stations,
                { "liftedGrid" : reversedLifted });
        const originHeld = squaredNorm(reversedLifted[1][0] - simpleGrid.liftedGrid[1][0]) < 1e-28;
        println("[ORIENTATION GRID SELF TEST] closed rows reversed: faces outward " ~
            reversed.facesOutward ~ " (against " ~ simple.facesOutward ~ "), unanimous " ~
            reversed.verdictUnanimous ~ ", difference " ~ reversed.differenceAgreements ~ "/" ~
            reversed.differenceChecked ~ " agree, consistent " ~ reversed.consistent ~
            ", q origin held " ~ originHeld);
        checks += 3;
        if (reversed.facesOutward == simple.facesOutward)
        {
            failures = failures ~ " reversing the closed rows did not flip the verdict.";
        }
        if (!reversed.consistent || reversed.differenceDisagreements != 0)
        {
            failures = failures ~ " the reversed closed-row grid did not certify (" ~
                reversed.differenceDisagreements ~ " disagreements).";
        }
        if (!originHeld)
        {
            failures = failures ~ " the closed-row reversal moved q's origin.";
        }

        // ---------- The fold window: the certificate must FIRE ----------
        const foldGrid = buildLoopGrid(motion, surface, [0.44, 0.48, 0.52, 0.56, 0.60], qCount);
        const fold = certifyFitGridOrientation(motion, surface, foldGrid.uvRows,
                foldGrid.stations, { "liftedGrid" : foldGrid.liftedGrid });
        println("[ORIENTATION GRID SELF TEST] fold window: " ~ fold.sampleCount ~ " samples, " ~
            "lambda sign consistent " ~ fold.lambdaSignConsistent ~ ", worst fold margin " ~
            fold.worstFoldMargin ~ ", faces outward " ~ fold.facesOutward ~ " unanimous " ~
            fold.verdictUnanimous ~ ", consistent " ~ fold.consistent);
        checks += 2;
        if (fold.lambdaSignConsistent)
        {
            failures = failures ~ " the fold certificate did NOT fire on a component whose " ~
                "lambda provably spans both signs at every station.";
        }
        if (fold.consistent)
        {
            failures = failures ~ " a folded component was reported consistent.";
        }

        reportTestVerdict(context, id, "ORIENTATION GRID SELF TEST", checks, failures,
            ("a collapsed pole row is skipped, a closed-row grid " ~
            "certifies with its finite-difference cross-check agreeing, reversing the closed " ~
            "rows flips the verdict while holding q's origin, and the lambda-sign fold " ~
            "certificate fires on the bump fixture's middle band - which is therefore a " ~
            "locally self-intersecting sweep, not a valid one."));
    });

// ============================= Fixtures =============================

/** Where the contact ruling sits at time t: f = 0 gives u = w_z(t) / c with w_z linear. */
function contactRuling(c is number, z0 is number, z1 is number, t is number) returns number
{
    return (z0 + z1 * t) / c;
}

/** The time at which the ruling passes through u - the inverse of contactRuling. */
function contactTime(c is number, z0 is number, z1 is number, u is number) returns number
{
    return (c * u - z0) / z1;
}

/** The funnel's project-to-(u, v) chart: Psi(u, v) = Phi(u, v, t(u)). */
function funnelChartPoint(motion is map, surface is map, c is number, z0 is number, z1 is number,
    u is number, v is number) returns Vector
{
    return liftContactPoint(motion, surface, u, v, contactTime(c, z0, z1, u));
}

/**
 * A fit grid on the fixture's funnel: rows are stations, columns are the ruling's v samples -
 * which IS the section here, since f does not depend on v. `reverseQ` walks v the other way, so
 * the caller can check that the q direction alone decides the patch verdict.
 * Returns { stations, uvRows, liftedGrid }.
 */
function buildFunnelFitGrid(motion is map, surface is map, c is number, z0 is number, z1 is number,
    stationCount is number, qCount is number, reverseQ is boolean) returns map
{
    var stations = makeArray(stationCount, 0);
    var uvRows = makeArray(stationCount);
    var liftedGrid = makeArray(stationCount);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        const t = stationIndex / (stationCount - 1);
        const u = contactRuling(c, z0, z1, t);
        stations[stationIndex] = t;
        var uvRow = makeArray(qCount);
        var liftedRow = makeArray(qCount);
        for (var qIndex = 0; qIndex < qCount; qIndex += 1)
        {
            const fraction = qIndex / (qCount - 1);
            const v = reverseQ ? 1 - fraction : fraction;
            uvRow[qIndex] = [u, v];
            liftedRow[qIndex] = liftContactPoint(motion, surface, u, v, t);
        }
        uvRows[stationIndex] = uvRow;
        liftedGrid[stationIndex] = liftedRow;
    }
    return { "stations" : stations, "uvRows" : uvRows, "liftedGrid" : liftedGrid };
}

/**
 * The funnel solver's island bump: S = (u, v, z) with z = 0.8 (u^2/2 - u^3/3) v(1 - v), so that
 * z_u = 0.8 u(1 - u) v(1 - v) peaks at exactly 0.05 in the middle. Degrees (3, 2), one Bezier
 * patch, non-rational. Under velocity (1, 0, w_z) the contact set is the level set
 * z_u = w_z - a CLOSED LOOP around (0.5, 0.5) for any 0 < w_z < 0.05, which is what makes this
 * the fixture for the closed-row and pole machinery.
 */
function islandBumpSurface() returns map
{
    const uCoefficients = [0, 0, 1 / 6, 1 / 6];
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

/**
 * Where the contact loop crosses the ray leaving (0.5, 0.5) at `theta`, by bisection on the
 * module's OWN envelope evaluator - f is negative at the centre (the bump's peak beats w_z) and
 * positive out near the domain edge, so the bracket is unconditional and there is exactly one
 * crossing. Returns [u, v]. A collapsed pole returns the centre itself.
 *
 * The pole test carries a TOLERANCE, learned live. At birth f(centre) is exactly zero in closed
 * form, but through de Boor it lands a few 1e-18 either side of zero - and on the negative side
 * an exact `>= 0` test misses the pole and bisection hands back a tiny loop instead of the
 * centre. The row then is not collapsed, the certificate does not skip it, and (worse) a caller
 * that reverses only the non-pole rows leaves the grid inconsistently ordered.
 */
function loopPointOnRay(motion is map, surface is map, theta is number, t is number) returns array
{
    const rayU = cos(theta * radian);
    const rayV = sin(theta * radian);
    const outer = 0.49;
    if (evaluateEnvelopePointwise(motion, surface, 0.5, 0.5, t) >= -1e-12)
    {
        return [0.5, 0.5];
    }
    var low = 0;
    var high = outer;
    for (var iteration = 0; iteration < 60; iteration += 1)
    {
        const mid = 0.5 * (low + high);
        if (evaluateEnvelopePointwise(motion, surface, 0.5 + mid * rayU, 0.5 + mid * rayV, t) < 0)
        {
            low = mid;
        }
        else
        {
            high = mid;
        }
    }
    const radius = 0.5 * (low + high);
    return [0.5 + radius * rayU, 0.5 + radius * rayV];
}

/**
 * A CLOSED-row fit grid on the bump's funnel: one row per station, q running counterclockwise
 * around the loop at n DISTINCT angles with no repeated closer - the periodic convention the
 * island and tube fits store. A station whose loop has collapsed contributes a collapsed row,
 * which is exactly the pole case the certificate has to skip.
 * Returns { stations, uvRows, liftedGrid }.
 */
function buildLoopGrid(motion is map, surface is map, stations is array, qCount is number) returns map
{
    var uvRows = makeArray(size(stations));
    var liftedGrid = makeArray(size(stations));
    for (var stationIndex = 0; stationIndex < size(stations); stationIndex += 1)
    {
        const t = stations[stationIndex];
        var uvRow = makeArray(qCount);
        var liftedRow = makeArray(qCount);
        for (var qIndex = 0; qIndex < qCount; qIndex += 1)
        {
            const uv = loopPointOnRay(motion, surface, 2 * PI * qIndex / qCount, t);
            uvRow[qIndex] = uv;
            liftedRow[qIndex] = liftContactPoint(motion, surface, uv[0], uv[1], t);
        }
        uvRows[stationIndex] = uvRow;
        liftedGrid[stationIndex] = liftedRow;
    }
    return { "stations" : stations, "uvRows" : uvRows, "liftedGrid" : liftedGrid };
}

/** A rational, deliberately v-asymmetric degree (2, 2) net - the reversal fixture. Asymmetry is
    the point: a symmetric net would pass a broken mirror. */
function rationalTestSurface() returns map
{
    const zGrid = [[0, 0.13, -0.07], [0.21, -0.05, 0.31], [-0.11, 0.27, 0.04]];
    const weightGrid = [[1, 0.6, 1.4], [0.8, 1, 0.7], [1.2, 0.9, 1]];
    var net = makeArray(3);
    var weights = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(0.5 * i, 0.5 * j + 0.1 * i, zGrid[i][j]);
        }
        net[i] = row;
        weights[i] = weightGrid[i];
    }
    return {
            "uDegree" : 2, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "weights" : weights, "isRational" : true,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * That reversing a net's q direction is exact: the reversed surface at mirrored v must be the
 * same point, and its parametric normal must be the exact negative there.
 * Returns { pointError, normalError }.
 */
function checkSurfaceReversal(surface is map) returns map
{
    const reversed = reverseFitSurfaceQDirection(surface);
    const vStart = surface.vKnots[0];
    const vEnd = surface.vKnots[size(surface.vKnots) - 1];
    var pointError = 0;
    var normalError = 0;
    for (var i = 0; i <= 6; i += 1)
    {
        for (var j = 0; j <= 6; j += 1)
        {
            const u = i / 6;
            const v = vStart + (vEnd - vStart) * j / 6;
            const mirroredV = vStart + vEnd - v;
            const original = evaluateBSplineSurfaceDerivatives(surface, u, v, 1, 1);
            const mirrored = evaluateBSplineSurfaceDerivatives(reversed, u, mirroredV, 1, 1);
            pointError = max(pointError, norm(original[0][0] - mirrored[0][0]));
            normalError = max(normalError, norm(cross(original[1][0], original[0][1]) +
                        cross(mirrored[1][0], mirrored[0][1])));
        }
    }
    return { "pointError" : pointError, "normalError" : normalError };
}
