FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

// Non-standard imports. bernsteinPolynomialUtils is a same-document element import (the module
// is deliberately unpublished so sweep refinement never forces republish cascades) - its ids
// churn on re-paste, matching bernsteinPolynomialUtilsTester.fs. splineRefinementUtils is the
// published cross-document pin. NOTE for MCP harness runs: the same-document import cannot
// resolve from the harness document; the test payload is assembled by inlining the Bernstein
// module's body in place of that import line.
import(path : "8b495c3bb1037b467ca1d02e", version : "3968d1ef5b507302198a917b"); //bernsteinPolynomialUtils.fs
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * SOLID SWEEP - envelope function layer (spec: docs/specs/SOLID_SWEEP_SPEC.md sections 6.0
 * through 6.2). Pure module: no Context anywhere. Consumes unit-stripped data (plain numbers,
 * meters implied) produced by swSweepEmit.fs extraction and by the motion module.
 *
 * The envelope function of a face S(u, v) under motion h(t) = (A(t), b(t)) is
 *     f(u, v, t) = < A(t) * N(u, v), A'(t) * S(u, v) + b'(t) >,   N = S_u x S_v (unnormalized).
 * Writing A's columns as C_i(t) so that A * w = sum_i w_i * C_i, the function FACTORS:
 *     f = sum_ij (N_i * S_j)(u, v) * M_ij(t)  +  sum_i N_i(u, v) * T_i(t)
 * with M_ij = <C_i, C'_j> and T_i = <C_i, b'> - twelve scalar t-polynomials per motion span.
 * Patch factors store only the S and N coefficient grids plus their value ranges; the loose
 * block screen runs on RANGES alone (range(N_i) x range(S_j) x range(M_ij) interval products),
 * so a dead block costs a handful of interval multiplies and no grid arithmetic at all. The
 * nine N_i*S_j product grids - the expensive objects - are built lazily by
 * buildEnvelopePatchProducts only for patches that survive screening, and a block's full
 * coefficient tensor is materialized only from those. Everything runs on
 * bernsteinPolynomialUtils arithmetic.
 *
 * Representation contracts:
 *   - Stripped spline / surface: the plain maps swSweepEmit.fs stores (degree/knots/
 *     controlPoints as plain-number Vectors; isRational must be falsy here - the coefficient
 *     path REQUIRES non-rational input, and extraction supplies freeform faces non-rationally.
 *     Analytic faces never enter this path; they get closed forms in the solver).
 *   - Stripped motion: { columnX, columnY, columnZ, translation } - four stripped 3D splines
 *     on ONE shared knot vector (the motion module's own storage, units removed).
 *   - Bernstein data follows bernsteinPolynomialUtils conventions: coefficient arrays on
 *     [0, 1], grids indexed [u][v], vectors as [x, y, z] arrays of coefficient arrays/grids.
 *   - Block-local coordinates: a patch or span stores its global domain (uStart/uEnd etc.);
 *     all Bernstein evaluation happens in local [0, 1] coordinates within it.
 *
 * The self-test feature is selection-free and context-free in its math (fixtures are
 * hand-built stripped maps), so the MCP harness can run it in one call.
 */

// ============================= Envelope Math Self Test =============================

annotation { "Feature Type Name" : "Sweep Envelope Math Self Test" }
export const sweepEnvelopeMathSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";

        // Fixture surface: bicubic, two spans per direction (four Bezier patches), wavy in z,
        // non-rational, clamped. Gentle waves keep N_z positive everywhere, which test 3 uses.
        const fixtureKnots = [0, 0, 0, 0, 0.5, 1, 1, 1, 1];
        var fixtureNet = makeArray(5);
        for (var rowIndex = 0; rowIndex < 5; rowIndex += 1)
        {
            var netRow = makeArray(5);
            for (var columnIndex = 0; columnIndex < 5; columnIndex += 1)
            {
                netRow[columnIndex] = vector(rowIndex * 0.02, columnIndex * 0.02,
                            0.004 * sin(80 * degree * rowIndex) + 0.003 * cos(70 * degree * columnIndex));
            }
            fixtureNet[rowIndex] = netRow;
        }
        const fixtureSurface = {
                "uDegree" : 3, "vDegree" : 3,
                "uKnots" : fixtureKnots, "vKnots" : fixtureKnots,
                "controlPoints" : fixtureNet,
                "isRational" : false,
                "isUPeriodic" : false, "isVPeriodic" : false
            };

        // Motion A - pure translation along a curved path (identity rotation): every rotation
        // column is a constant spline. Motion B - a varying, deliberately non-orthogonal A(t):
        // the factorization is algebra, valid for ANY coefficients, and a non-orthogonal A
        // exercises every term. Both single-span cubics; motion C is a four-span translation.
        const singleSpanKnots = [0, 0, 0, 0, 1, 1, 1, 1];
        const motionA = {
                "columnX" : constantColumnSpline(vector(1, 0, 0), singleSpanKnots),
                "columnY" : constantColumnSpline(vector(0, 1, 0), singleSpanKnots),
                "columnZ" : constantColumnSpline(vector(0, 0, 1), singleSpanKnots),
                "translation" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0, 0, 0.02), vector(0, 0, 0.045), vector(0, 0, 0.06)]
                }
            };
        const motionB = {
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

        // Test 1: factored materialization agrees with the independent pointwise path.
        // The pointwise path evaluates the ORIGINAL splines with splineRefinementUtils
        // evaluators end to end - a genuinely different code path from the coefficient
        // assembly, so agreement validates both.
        const patchFactors = buildEnvelopePatchFactors(fixtureSurface);
        const testFractions = [0.08, 0.31, 0.5, 0.77, 0.94];
        var worstAgreement = 0;
        for (var motionPass = 0; motionPass < 2; motionPass += 1)
        {
            const motion = motionPass == 0 ? motionA : motionB;
            const spans = buildMotionSpanPolynomials(motion);
            for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
            {
                for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
                {
                    const patch = patchFactors.patches[uSegment][vSegment];
                    const block = materializeEnvelopeBlock(buildEnvelopePatchProducts(patch), spans[0]);
                    for (var fraction in testFractions)
                    {
                        const localU = fraction;
                        const localV = 1 - fraction * 0.83;
                        const localT = 0.15 + 0.7 * fraction;
                        const materialized = evaluateMaterializedBlock(block, localU, localV, localT);
                        const globalU = patch.uStart + (patch.uEnd - patch.uStart) * localU;
                        const globalV = patch.vStart + (patch.vEnd - patch.vStart) * localV;
                        const globalT = spans[0].tStart + (spans[0].tEnd - spans[0].tStart) * localT;
                        const pointwise = evaluateEnvelopePointwise(motion, fixtureSurface, globalU, globalV, globalT);
                        const disagreement = abs(materialized - pointwise) / (1 + abs(pointwise));
                        if (disagreement > worstAgreement)
                        {
                            worstAgreement = disagreement;
                        }
                    }
                }
            }
        }
        println("[ENVELOPE MATH SELF TEST] factored vs pointwise worst relative disagreement: " ~ worstAgreement);
        if (worstAgreement > 1e-9)
        {
            failures = failures ~ " factored and pointwise paths disagree by " ~ worstAgreement ~ ".";
        }

        // Test 2: screening. Pure +Z translation against a patch whose N_z never vanishes must
        // kill every block at the LOOSE screen, before any materialization. A tilted
        // translation must leave live blocks.
        const spansA = buildMotionSpanPolynomials(motionA);
        var looseDeadCount = 0;
        var blockCount = 0;
        for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
        {
            for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
            {
                blockCount += 1;
                const screen = screenEnvelopeBlock(patchFactors.patches[uSegment][vSegment], spansA[0]);
                if (!screen.canVanish)
                {
                    looseDeadCount += 1;
                }
            }
        }
        println("[ENVELOPE MATH SELF TEST] +Z-dominated translation: " ~ looseDeadCount ~ " of " ~
            blockCount ~ " blocks killed by the loose screen");
        if (looseDeadCount != blockCount)
        {
            failures = failures ~ " +Z translation left " ~ (blockCount - looseDeadCount) ~
                " block(s) alive in the loose screen.";
        }

        const motionTilted = {
                "columnX" : motionA.columnX, "columnY" : motionA.columnY, "columnZ" : motionA.columnZ,
                "translation" : {
                    "degree" : 3, "knots" : singleSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0.02, 0, 0.003), vector(0.04, 0, 0.006), vector(0.06, 0, 0.009)]
                }
            };
        const spansTilted = buildMotionSpanPolynomials(motionTilted);
        var liveCount = 0;
        var hullDeadCount = 0;
        for (var uSegment = 0; uSegment < patchFactors.uSegments; uSegment += 1)
        {
            for (var vSegment = 0; vSegment < patchFactors.vSegments; vSegment += 1)
            {
                const patch = patchFactors.patches[uSegment][vSegment];
                const screen = screenEnvelopeBlock(patch, spansTilted[0]);
                if (!screen.canVanish)
                {
                    hullDeadCount += 1;
                    continue;
                }
                const block = materializeEnvelopeBlock(buildEnvelopePatchProducts(patch), spansTilted[0]);
                if (envelopeBlockExcludesZero(block, 0))
                {
                    hullDeadCount += 1;
                }
                else
                {
                    liveCount += 1;
                }
            }
        }
        println("[ENVELOPE MATH SELF TEST] tilted translation: " ~ liveCount ~ " live / " ~
            hullDeadCount ~ " dead of " ~ blockCount ~ " blocks");
        if (liveCount < 1)
        {
            failures = failures ~ " tilted translation produced no live blocks (grazing set lost).";
        }

        // Test 3: multi-span motion bookkeeping - four spans, aligned across all four motion
        // splines, with factored/pointwise agreement holding on a middle span.
        const fourSpanKnots = [0, 0, 0, 0, 0.25, 0.5, 0.75, 1, 1, 1, 1];
        const motionC = {
                "columnX" : constantColumnSpline(vector(1, 0, 0), fourSpanKnots),
                "columnY" : constantColumnSpline(vector(0, 1, 0), fourSpanKnots),
                "columnZ" : constantColumnSpline(vector(0, 0, 1), fourSpanKnots),
                "translation" : {
                    "degree" : 3, "knots" : fourSpanKnots, "isRational" : false,
                    "controlPoints" : [vector(0, 0, 0), vector(0.01, 0.002, 0.012), vector(0.025, 0.006, 0.03),
                                vector(0.045, 0.011, 0.041), vector(0.06, 0.014, 0.055), vector(0.075, 0.016, 0.06), vector(0.09, 0.017, 0.07)]
                }
            };
        const spansC = buildMotionSpanPolynomials(motionC);
        if (size(spansC) != 4)
        {
            failures = failures ~ " four-span motion decomposed into " ~ size(spansC) ~ " spans.";
        }
        else
        {
            const patch = patchFactors.patches[1][0];
            const block = materializeEnvelopeBlock(buildEnvelopePatchProducts(patch), spansC[2]);
            const materialized = evaluateMaterializedBlock(block, 0.4, 0.6, 0.3);
            const pointwise = evaluateEnvelopePointwise(motionC, fixtureSurface,
                patch.uStart + (patch.uEnd - patch.uStart) * 0.4,
                patch.vStart + (patch.vEnd - patch.vStart) * 0.6,
                spansC[2].tStart + (spansC[2].tEnd - spansC[2].tStart) * 0.3);
            const spanDisagreement = abs(materialized - pointwise) / (1 + abs(pointwise));
            println("[ENVELOPE MATH SELF TEST] four-span middle-span disagreement: " ~ spanDisagreement);
            if (spanDisagreement > 1e-9)
            {
                failures = failures ~ " multi-span materialization disagrees by " ~ spanDisagreement ~ ".";
            }
        }

        // Test 4: the strip function against a hand dot product, and the time derivative
        // against a central difference.
        const stripNormal = vector(0.2, -0.3, 0.93);
        const stripPoint = vector(0.04, 0.02, 0.01);
        const stripValue = evaluateContactFunctionAtPoint(motionB, stripNormal, stripPoint, 0.37);
        const sampleAtT = evaluateMotionSample(motionB, 0.37);
        const handValue = dot(sampleAtT.rotation * stripNormal, sampleAtT.rotationDerivative * stripPoint + sampleAtT.translationDerivative);
        if (abs(stripValue - handValue) > 1e-12 * (1 + abs(handValue)))
        {
            failures = failures ~ " strip function disagrees with the hand dot product.";
        }
        const timeDerivative = evaluateEnvelopeTimeDerivativePointwise(motionB, fixtureSurface, 0.3, 0.6, 0.5);
        const centralStep = 1e-5;
        const centralDifference = (evaluateEnvelopePointwise(motionB, fixtureSurface, 0.3, 0.6, 0.5 + centralStep) -
                evaluateEnvelopePointwise(motionB, fixtureSurface, 0.3, 0.6, 0.5 - centralStep)) / (2 * centralStep);
        const derivativeDisagreement = abs(timeDerivative - centralDifference) / (1 + abs(centralDifference));
        println("[ENVELOPE MATH SELF TEST] f_t vs central difference relative disagreement: " ~ derivativeDisagreement);
        if (derivativeDisagreement > 1e-7)
        {
            failures = failures ~ " f_t disagrees with the central difference by " ~ derivativeDisagreement ~ ".";
        }

        // Test 5: workload counters sized to match the 2026-08-22 baseline profile (which
        // measured 13 s in the eager factor build), split per stage so the profiler
        // attributes factor builds, lazy product builds, and materializations separately.
        var factorBuildCount = 0;
        var productBuildCount = 0;
        var materializedCount = 0;
        for (var repetition = 0; repetition < 12; repetition += 1)
        {
            const repeatedFactors = buildEnvelopePatchFactors(fixtureSurface);
            factorBuildCount += 1;
            for (var uSegment = 0; uSegment < repeatedFactors.uSegments; uSegment += 1)
            {
                for (var vSegment = 0; vSegment < repeatedFactors.vSegments; vSegment += 1)
                {
                    const products = buildEnvelopePatchProducts(repeatedFactors.patches[uSegment][vSegment]);
                    productBuildCount += 1;
                    for (var spanIndex = 0; spanIndex < size(spansC); spanIndex += 1)
                    {
                        const block = materializeEnvelopeBlock(products, spansC[spanIndex]);
                        materializedCount += size(block);
                    }
                }
            }
        }
        println("[ENVELOPE MATH SELF TEST] workload: " ~ factorBuildCount ~ " factor builds (4 patches each), " ~
            productBuildCount ~ " lazy product builds, " ~ materializedCount ~
            " coefficient grids materialized across " ~ (productBuildCount * size(spansC)) ~ " blocks");

        reportTestVerdict(context, id, "ENVELOPE MATH SELF TEST", failures,
            "factored coefficients match the independent pointwise path to machine precision on " ~
            "both motions and across spans; loose screen kills all blocks under +Z translation; tilted " ~
            "translation leaves a live grazing set; strip function and f_t verified.");
    });

// ============================= Motion span polynomials =============================

/**
 * Decompose a stripped motion into per-span Bernstein data. All four motion splines share one
 * knot vector by the motion module's contract; this throws if their Bezier breakpoints
 * disagree.
 *
 * Each span: {
 *     tStart, tEnd {number},
 *     columns {array} : [C1, C2, C3] - A(t)'s columns as Bernstein vectors on local [0, 1],
 *     columnDerivatives {array} : [C1', C2', C3'] in GLOBAL t units,
 *     translationDerivative {array} : b' as a Bernstein vector in global t units,
 *     velocityDots {array} : M[i][j] = <C_i, C_j'> coefficient arrays, all one shared degree,
 *     translationDots {array} : T[i] = <C_i, b'> coefficient arrays, same degree as M,
 *     velocityDotRanges, translationDotRanges {arrays} : {minimum, maximum} per polynomial,
 *         precomputed for the block screen
 * }
 */
export function buildMotionSpanPolynomials(strippedMotion is map) returns array
{
    const columnSegments = [
            decomposeIntoBezierSegments(strippedMotion.columnX),
            decomposeIntoBezierSegments(strippedMotion.columnY),
            decomposeIntoBezierSegments(strippedMotion.columnZ)
        ];
    const translationSegments = decomposeIntoBezierSegments(strippedMotion.translation);
    const spanCount = size(translationSegments);
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        if (size(columnSegments[columnIndex]) != spanCount)
        {
            throw "swEnvelopeMath: motion splines decompose into different span counts (" ~
                size(columnSegments[columnIndex]) ~ " vs " ~ spanCount ~ ") - they must share one knot vector.";
        }
    }

    var spans = makeArray(spanCount);
    for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
    {
        const tStart = translationSegments[spanIndex].domainStart;
        const tEnd = translationSegments[spanIndex].domainEnd;
        const spanWidth = tEnd - tStart;

        var columns = makeArray(3);
        var columnDerivatives = makeArray(3);
        for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
        {
            const segment = columnSegments[columnIndex][spanIndex];
            if (abs(segment.domainStart - tStart) > 1e-12 || abs(segment.domainEnd - tEnd) > 1e-12)
            {
                throw "swEnvelopeMath: motion spline span domains disagree at span " ~ spanIndex ~ ".";
            }
            columns[columnIndex] = componentCoefficientArrays(segment.controlPoints);
            columnDerivatives[columnIndex] = differentiateBernsteinVector(columns[columnIndex], spanWidth);
        }
        const translationVector = componentCoefficientArrays(translationSegments[spanIndex].controlPoints);
        const translationDerivative = differentiateBernsteinVector(translationVector, spanWidth);

        var velocityDots = makeArray(3);
        var translationDots = makeArray(3);
        var sharedDegree = 0;
        for (var i = 0; i < 3; i += 1)
        {
            var dotRow = makeArray(3);
            for (var j = 0; j < 3; j += 1)
            {
                dotRow[j] = dotBernsteinVectors(columns[i], columnDerivatives[j]);
                sharedDegree = max(sharedDegree, size(dotRow[j]) - 1);
            }
            velocityDots[i] = dotRow;
            translationDots[i] = dotBernsteinVectors(columns[i], translationDerivative);
            sharedDegree = max(sharedDegree, size(translationDots[i]) - 1);
        }
        var velocityDotRanges = makeArray(3);
        var translationDotRanges = makeArray(3);
        for (var i = 0; i < 3; i += 1)
        {
            var rangeRow = makeArray(3);
            for (var j = 0; j < 3; j += 1)
            {
                velocityDots[i][j] = elevateBernstein(velocityDots[i][j], sharedDegree);
                rangeRow[j] = bernsteinRange(velocityDots[i][j]);
            }
            velocityDotRanges[i] = rangeRow;
            translationDots[i] = elevateBernstein(translationDots[i], sharedDegree);
            translationDotRanges[i] = bernsteinRange(translationDots[i]);
        }

        spans[spanIndex] = {
                "tStart" : tStart,
                "tEnd" : tEnd,
                "columns" : columns,
                "columnDerivatives" : columnDerivatives,
                "translationDerivative" : translationDerivative,
                "velocityDots" : velocityDots,
                "translationDots" : translationDots,
                "velocityDotRanges" : velocityDotRanges,
                "translationDotRanges" : translationDotRanges
            };
    }
    return spans;
}

// ============================= Face patch factors =============================

/**
 * Build the motion-independent envelope factors of one non-rational stripped surface:
 * per Bezier patch, the S and N coefficient grids plus their value ranges - everything the
 * loose block screen needs and nothing it does not. The expensive N_i * S_j product grids are
 * NOT built here; buildEnvelopePatchProducts supplies them lazily for patches that survive
 * screening. Throws on rational input - the coefficient path requires the non-rational form
 * extraction supplies for freeform faces.
 *
 * Returns { uSegments, vSegments, patches : 2D array of {
 *     uStart, uEnd, vStart, vEnd,
 *     surfaceGrids {array} : S as [xGrid, yGrid, zGrid],
 *     normalGrids {array} : N = S_u x S_v (global-parameter derivatives),
 *     surfaceRanges, normalRanges {arrays} : {minimum, maximum} per component grid
 * } }
 */
export function buildEnvelopePatchFactors(strippedSurface is map) returns map
{
    if (strippedSurface.isRational == true)
    {
        throw "swEnvelopeMath: buildEnvelopePatchFactors requires a non-rational surface. " ~
            "Freeform faces are extracted non-rationally for the coefficient path; analytic " ~
            "faces take closed forms and never reach this assembly.";
    }
    const patchRows = decomposeSurfaceIntoBezierPatches(strippedSurface);
    const uSegments = size(patchRows);
    const vSegments = size(patchRows[0]);
    var factorRows = makeArray(uSegments);
    for (var uSegment = 0; uSegment < uSegments; uSegment += 1)
    {
        var factorRow = makeArray(vSegments);
        for (var vSegment = 0; vSegment < vSegments; vSegment += 1)
        {
            const patch = patchRows[uSegment][vSegment];
            const uWidth = patch.uDomainEnd - patch.uDomainStart;
            const vWidth = patch.vDomainEnd - patch.vDomainStart;
            const surfaceGrids = componentCoefficientGrids(patch.controlPoints);
            var uTangentGrids = makeArray(3);
            var vTangentGrids = makeArray(3);
            for (var component = 0; component < 3; component += 1)
            {
                uTangentGrids[component] = scaleBernsteinGrid(differentiateBernsteinGridU(surfaceGrids[component]), 1 / uWidth);
                vTangentGrids[component] = scaleBernsteinGrid(differentiateBernsteinGridV(surfaceGrids[component]), 1 / vWidth);
            }
            const normalGrids = crossBernsteinVectorGrids(uTangentGrids, vTangentGrids);

            var surfaceRanges = makeArray(3);
            var normalRanges = makeArray(3);
            for (var component = 0; component < 3; component += 1)
            {
                surfaceRanges[component] = bernsteinGridRange(surfaceGrids[component]);
                normalRanges[component] = bernsteinGridRange(normalGrids[component]);
            }

            factorRow[vSegment] = {
                    "uStart" : patch.uDomainStart, "uEnd" : patch.uDomainEnd,
                    "vStart" : patch.vDomainStart, "vEnd" : patch.vDomainEnd,
                    "surfaceGrids" : surfaceGrids,
                    "normalGrids" : normalGrids,
                    "surfaceRanges" : surfaceRanges,
                    "normalRanges" : normalRanges
                };
        }
        factorRows[uSegment] = factorRow;
    }
    return { "uSegments" : uSegments, "vSegments" : vSegments, "patches" : factorRows };
}

// ============================= Block screening and materialization =============================

/**
 * The lazily built product data of one patch - only patches that survive screening pay for
 * this. Returns {
 *     productGrids {array} : G[i][j] = N_i * S_j at one shared uv degree,
 *     elevatedNormalGrids {array} : H[i] = N_i elevated to that degree,
 *     termMatrix {Matrix} : the twelve term grids flattened row-major into a 12 x (rows*cols)
 *         matrix, ordered [G00..G02, G10..G12, G20..G22, H0, H1, H2] - materialization is then
 *         one native matrix product per block,
 *     gridRowCount, gridColumnCount {number} : the shared grid dimensions for unflattening
 * }.
 */
export function buildEnvelopePatchProducts(patchFactor is map) returns map
{
    var productGrids = makeArray(3);
    var targetUDegree = 0;
    var targetVDegree = 0;
    for (var i = 0; i < 3; i += 1)
    {
        var productRow = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            productRow[j] = multiplyBernsteinGrids(patchFactor.normalGrids[i], patchFactor.surfaceGrids[j]);
            targetUDegree = max(targetUDegree, size(productRow[j]) - 1);
            targetVDegree = max(targetVDegree, size(productRow[j][0]) - 1);
        }
        productGrids[i] = productRow;
    }
    var elevatedNormalGrids = makeArray(3);
    var termRows = makeArray(12);
    for (var i = 0; i < 3; i += 1)
    {
        for (var j = 0; j < 3; j += 1)
        {
            productGrids[i][j] = elevateBernsteinGrid(productGrids[i][j], targetUDegree, targetVDegree);
            termRows[i * 3 + j] = concatenateArrays(productGrids[i][j]);
        }
        elevatedNormalGrids[i] = elevateBernsteinGrid(patchFactor.normalGrids[i], targetUDegree, targetVDegree);
        termRows[9 + i] = concatenateArrays(elevatedNormalGrids[i]);
    }
    return {
            "productGrids" : productGrids,
            "elevatedNormalGrids" : elevatedNormalGrids,
            "termMatrix" : matrix(termRows),
            "gridRowCount" : targetUDegree + 1,
            "gridColumnCount" : targetVDegree + 1
        };
}

/**
 * Loose interval screen of one (patch x t-span) block, from precomputed ranges alone - a few
 * dozen interval multiplies, no grid arithmetic. Every factor's value on the block lies in
 * its Bernstein hull range, so the summed interval product contains the true range of f and
 * canVanish == false is a certificate.
 * Returns { canVanish {boolean}, looseMin, looseMax {number} }.
 */
export function screenEnvelopeBlock(patchFactor is map, spanPolynomials is map, valueTolerance is number) returns map
{
    var lowerBound = 0;
    var upperBound = 0;
    for (var i = 0; i < 3; i += 1)
    {
        for (var j = 0; j < 3; j += 1)
        {
            const term = multiplyIntervals(
                multiplyIntervals(patchFactor.normalRanges[i], patchFactor.surfaceRanges[j]),
                spanPolynomials.velocityDotRanges[i][j]);
            lowerBound += term.minimum;
            upperBound += term.maximum;
        }
        const translationTerm = multiplyIntervals(patchFactor.normalRanges[i], spanPolynomials.translationDotRanges[i]);
        lowerBound += translationTerm.minimum;
        upperBound += translationTerm.maximum;
    }
    return {
            "canVanish" : lowerBound <= valueTolerance && upperBound >= -valueTolerance,
            "looseMin" : lowerBound,
            "looseMax" : upperBound
        };
}

/** Two-argument convenience: screen with zero tolerance. */
export function screenEnvelopeBlock(patchFactor is map, spanPolynomials is map) returns map
{
    return screenEnvelopeBlock(patchFactor, spanPolynomials, 0);
}

/**
 * The relative floor below which a value coming out of the coefficient path carries no sign:
 * one block's f is a twelve-term sum of degree-elevated grid products, so cancellation there
 * costs a few thousand machine epsilons of the terms' own magnitude. Four orders above that,
 * and - measured on the spec 6.7 fixture - ten orders below a real block's |f| range.
 */
export const ENVELOPE_RELATIVE_SIGN_TOLERANCE = 1e-12;

/**
 * screenEnvelopeBlock with the zero threshold taken from the BLOCK'S OWN value range rather
 * than from an absolute number a caller guessed: `relativeTolerance` times the larger end of
 * the loose range, never below `absoluteFloor`. Returns screenEnvelopeBlock's record plus
 * `signTolerance` - the threshold, which the caller uses for every sign test it makes on this
 * block, so that screening and the signs it later reads agree on what zero means.
 *
 * An absolute threshold cannot work here: the same number is a certificate on a block whose
 * |f| runs to 1e-2 and pure noise on one that runs to 1e-14, and nothing upstream of a block
 * knows which it is.
 */
export function screenEnvelopeBlockScaled(patchFactor is map, spanPolynomials is map,
    relativeTolerance is number, absoluteFloor is number) returns map
{
    const loose = screenEnvelopeBlock(patchFactor, spanPolynomials, 0);
    const signTolerance = max(absoluteFloor,
        relativeTolerance * max(abs(loose.looseMin), abs(loose.looseMax)));
    return {
            "canVanish" : loose.looseMin <= signTolerance && loose.looseMax >= -signTolerance,
            "looseMin" : loose.looseMin,
            "looseMax" : loose.looseMax,
            "signTolerance" : signTolerance
        };
}

/**
 * Materialize one block's full coefficient tensor: f on the block as a Bernstein polynomial
 * in local t whose coefficients are uv grids - result[m] is the grid multiplying the m-th
 * Bernstein basis function in t. The whole accumulation is ONE native matrix product per
 * block: the (t-coefficients x 12) weight matrix times the patch's flattened 12-row term
 * matrix, with each result row sliced back into a grid. Consumes the lazily built patch
 * products; call it only on blocks the screen left alive (or in testers).
 */
export function materializeEnvelopeBlock(patchProducts is map, spanPolynomials is map) returns array
{
    const timeCoefficientCount = size(spanPolynomials.translationDots[0]);
    var weightRows = makeArray(timeCoefficientCount);
    for (var m = 0; m < timeCoefficientCount; m += 1)
    {
        var weights = makeArray(12, 0);
        var termCursor = 0;
        for (var i = 0; i < 3; i += 1)
        {
            for (var j = 0; j < 3; j += 1)
            {
                weights[termCursor] = spanPolynomials.velocityDots[i][j][m];
                termCursor += 1;
            }
        }
        for (var i = 0; i < 3; i += 1)
        {
            weights[9 + i] = spanPolynomials.translationDots[i][m];
        }
        weightRows[m] = weights;
    }
    const flattenedBlocks = matrix(weightRows) * patchProducts.termMatrix;

    const columnCount = patchProducts.gridColumnCount;
    var blockGrids = makeArray(timeCoefficientCount);
    for (var m = 0; m < timeCoefficientCount; m += 1)
    {
        const flatRow = flattenedBlocks[m];
        var grid = makeArray(patchProducts.gridRowCount);
        for (var rowIndex = 0; rowIndex < patchProducts.gridRowCount; rowIndex += 1)
        {
            grid[rowIndex] = subArray(flatRow, rowIndex * columnCount, (rowIndex + 1) * columnCount);
        }
        blockGrids[m] = grid;
    }
    return blockGrids;
}

/**
 * Exact convex-hull screen of a materialized block: true when every coefficient across all
 * t-coefficient grids has one strict sign (beyond valueTolerance), so f cannot vanish there.
 */
export function envelopeBlockExcludesZero(blockGrids is array, valueTolerance is number) returns boolean
{
    var minimum = undefined;
    var maximum = undefined;
    for (var grid in blockGrids)
    {
        const range = bernsteinGridRange(grid);
        minimum = minimum == undefined ? range.minimum : min(minimum, range.minimum);
        maximum = maximum == undefined ? range.maximum : max(maximum, range.maximum);
    }
    return minimum > valueTolerance || maximum < -valueTolerance;
}

/** Evaluate a materialized block at local block coordinates (all in [0, 1]). */
export function evaluateMaterializedBlock(blockGrids is array, localU is number, localV is number, localT is number) returns number
{
    var timeCoefficients = makeArray(size(blockGrids));
    for (var m = 0; m < size(blockGrids); m += 1)
    {
        timeCoefficients[m] = evaluateBernsteinGrid(blockGrids[m], localU, localV);
    }
    return evaluateBernstein(timeCoefficients, localT);
}

// ============================= Pointwise evaluation =============================

/**
 * The motion state at global parameter t, straight from the stripped motion splines:
 * { rotation, rotationDerivative, rotationSecondDerivative {matrices},
 *   translation, translationDerivative, translationSecondDerivative {Vectors} }.
 * This is the module's own evaluation path, independent of the coefficient assembly.
 */
export function evaluateMotionSample(strippedMotion is map, t is number) returns map
{
    const xDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnX, t, 2);
    const yDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnY, t, 2);
    const zDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.columnZ, t, 2);
    const translationDerivatives = evaluateBSplineCurveDerivatives(strippedMotion.translation, t, 2);
    return {
            "rotation" : matrixFromColumns(xDerivatives[0], yDerivatives[0], zDerivatives[0]),
            "rotationDerivative" : matrixFromColumns(xDerivatives[1], yDerivatives[1], zDerivatives[1]),
            "rotationSecondDerivative" : matrixFromColumns(xDerivatives[2], yDerivatives[2], zDerivatives[2]),
            "translation" : translationDerivatives[0],
            "translationDerivative" : translationDerivatives[1],
            "translationSecondDerivative" : translationDerivatives[2]
        };
}

/**
 * Pointwise envelope function f(u, v, t) evaluated end to end from the original stripped
 * splines - the polish/certification path, and the independent check of the coefficient
 * assembly. u, v are in the surface's knot domain; t in the motion's.
 */
export function evaluateEnvelopePointwise(strippedMotion is map, strippedSurface is map, u is number, v is number, t is number) returns number
{
    const derivatives = evaluateBSplineSurfaceDerivatives(strippedSurface, u, v, 1, 1);
    const normal = cross(derivatives[1][0], derivatives[0][1]);
    const sample = evaluateMotionSample(strippedMotion, t);
    return dot(sample.rotation * normal, sample.rotationDerivative * derivatives[0][0] + sample.translationDerivative);
}

/**
 * Pointwise time derivative f_t(u, v, t): with N fixed in t,
 * f_t = <A' N, A' S + b'> + <A N, A'' S + b''>.
 */
export function evaluateEnvelopeTimeDerivativePointwise(strippedMotion is map, strippedSurface is map, u is number, v is number, t is number) returns number
{
    const derivatives = evaluateBSplineSurfaceDerivatives(strippedSurface, u, v, 1, 1);
    const normal = cross(derivatives[1][0], derivatives[0][1]);
    const surfacePoint = derivatives[0][0];
    const sample = evaluateMotionSample(strippedMotion, t);
    const velocity = sample.rotationDerivative * surfacePoint + sample.translationDerivative;
    const acceleration = sample.rotationSecondDerivative * surfacePoint + sample.translationSecondDerivative;
    return dot(sample.rotationDerivative * normal, velocity) + dot(sample.rotation * normal, acceleration);
}

/**
 * The contact (strip) function at one sample: g = <A(t) n, A'(t) p + b'(t)> for a one-sided
 * normal n at a point p. This is the co-edge strip function of spec section 6.2 evaluated at
 * one (sample, t), and equally the sharp-vertex function when p is the vertex and n a cone
 * normal.
 */
export function evaluateContactFunctionAtPoint(strippedMotion is map, normal is Vector, point is Vector, t is number) returns number
{
    const sample = evaluateMotionSample(strippedMotion, t);
    return dot(sample.rotation * normal, sample.rotationDerivative * point + sample.translationDerivative);
}

/**
 * The strip function on a co-edge side's shared sample arrays: g[sampleIndex][tIndex] over
 * the given global t values. normals and points are swSweepEmit's sideNormals / edgePoints
 * arrays - the SAME arrays every adjacent consumer reads, which is what keeps seams exact.
 */
export function buildStripFunctionGrid(strippedMotion is map, normals is array, points is array, tValues is array) returns array
{
    var grid = makeArray(size(normals));
    for (var sampleIndex = 0; sampleIndex < size(normals); sampleIndex += 1)
    {
        var row = makeArray(size(tValues));
        for (var tIndex = 0; tIndex < size(tValues); tIndex += 1)
        {
            row[tIndex] = evaluateContactFunctionAtPoint(strippedMotion, normals[sampleIndex], points[sampleIndex], tValues[tIndex]);
        }
        grid[sampleIndex] = row;
    }
    return grid;
}

// ============================= Internal helpers =============================

/** A constant 3D spline (every control point equal) on the given clamped knot vector. */
function constantColumnSpline(value is Vector, knots is array) returns map
{
    const degree = 3;
    const pointCount = size(knots) - degree - 1;
    var controlPoints = makeArray(pointCount);
    for (var pointIndex = 0; pointIndex < pointCount; pointIndex += 1)
    {
        controlPoints[pointIndex] = value;
    }
    return { "degree" : degree, "knots" : knots, "controlPoints" : controlPoints, "isRational" : false };
}

/** Bezier-segment control points (array of 3-vectors) to a Bernstein vector [x, y, z] arrays. */
function componentCoefficientArrays(segmentControlPoints is array) returns array
{
    var components = makeArray(3);
    for (var component = 0; component < 3; component += 1)
    {
        var coefficients = makeArray(size(segmentControlPoints));
        for (var pointIndex = 0; pointIndex < size(segmentControlPoints); pointIndex += 1)
        {
            coefficients[pointIndex] = segmentControlPoints[pointIndex][component];
        }
        components[component] = coefficients;
    }
    return components;
}

/** Patch control net (grid of 3-vectors) to a Bernstein vector grid [xGrid, yGrid, zGrid]. */
function componentCoefficientGrids(patchControlPoints is array) returns array
{
    var components = makeArray(3);
    for (var component = 0; component < 3; component += 1)
    {
        var grid = makeArray(size(patchControlPoints));
        for (var rowIndex = 0; rowIndex < size(patchControlPoints); rowIndex += 1)
        {
            var row = makeArray(size(patchControlPoints[rowIndex]));
            for (var columnIndex = 0; columnIndex < size(patchControlPoints[rowIndex]); columnIndex += 1)
            {
                row[columnIndex] = patchControlPoints[rowIndex][columnIndex][component];
            }
            grid[rowIndex] = row;
        }
        components[component] = grid;
    }
    return components;
}

/** Differentiate a Bernstein vector on local [0, 1], rescaled to global units by spanWidth. */
function differentiateBernsteinVector(vectorCoefficients is array, spanWidth is number) returns array
{
    var derivative = makeArray(3);
    for (var component = 0; component < 3; component += 1)
    {
        derivative[component] = scaleBernstein(differentiateBernstein(vectorCoefficients[component]), 1 / spanWidth);
    }
    return derivative;
}

/** Interval product of two {minimum, maximum} ranges (bernsteinRange / bernsteinGridRange form). */
function multiplyIntervals(rangeA is map, rangeB is map) returns map
{
    const products = [rangeA.minimum * rangeB.minimum, rangeA.minimum * rangeB.maximum,
                rangeA.maximum * rangeB.minimum, rangeA.maximum * rangeB.maximum];
    return {
            "minimum" : min(min(products[0], products[1]), min(products[2], products[3])),
            "maximum" : max(max(products[0], products[1]), max(products[2], products[3]))
        };
}

/** A 3x3 matrix from three column Vectors. */
function matrixFromColumns(columnX is Vector, columnY is Vector, columnZ is Vector) returns Matrix
{
    return matrix([[columnX[0], columnY[0], columnZ[0]],
                [columnX[1], columnY[1], columnZ[1]],
                [columnX[2], columnY[2], columnZ[2]]]);
}
