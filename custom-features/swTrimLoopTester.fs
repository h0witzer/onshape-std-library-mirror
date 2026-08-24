FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/projectiontype.gen.fs", version : "3044.0"); // ProjectionType is not re-exported by common.fs

// Non-standard imports - same-document element imports; fix the ids/versions on paste. For MCP
// harness runs the payload inlines the module bodies instead of resolving these lines.
import(path : "0000000000000000000000aa", version : "0000000000000000000000bb"); //swSweepEmit.fs
import(path : "0000000000000000000000cc", version : "0000000000000000000000dd"); //swEnvelopeMath.fs, swFunnelSolver.fs
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * LIVE test of the trim loop plumbing (spec sections 5, 6.3.3): the one step that cannot be
 * checked from fixtures, because the question is whether real extraction output converts into
 * loops the census can mask with.
 *
 * The fixture is a parabolic sheet SPLIT by a projected circle, which yields two faces on ONE
 * surface: an annulus whose trim set is the domain rectangle plus a hole, and the disc inside
 * it. Both are exactly extracted B-splines, so neither carries kernel trim curves and both
 * take the co-edge pcurve path - the path that has no other source and had never run.
 *
 * Everything about it is closed form. The surface is S(u, v) = (0.2u, 0.15v, 0.1u^2), so
 *
 *     S_u = (0.2, 0, 0.2u)    S_v = (0, 0.15, 0)    N = (-0.03u, 0, 0.03)
 *
 * and under the constant velocity (1, 0, 0.5) the envelope function is exactly
 * f = 0.03 (0.5 - u): the contact set is the plane u = 0.5, a slab spanning every v and t.
 * Inverting the projected circle costs nothing either, since u and v depend only on x and y -
 * the hole is the ELLIPSE u = 0.5 + 0.15 cos(theta), v = 0.5 + 0.2 sin(theta).
 *
 * That fixes every number this test asserts. On a nine-node grid the only nodes inside the
 * ellipse are the five of the plus shape centred on (0.5, 0.5), so masking removes the cells
 * around them and cuts the slab in two: ONE 128-cell component becomes TWO of 32 cells, both
 * reporting the trim boundary.
 *
 * It also exercises the boundary tolerance, which is what the annulus's outer loop needs: that
 * loop lies exactly ON the domain rectangle, where an even-odd ray cast is a coin flip. Without
 * the tolerance the u = 1 and v = 1 node lines come back masked and the boundary cell rows
 * vanish - and the boundary is where co-edge components live.
 */

annotation { "Feature Type Name" : "Sweep Trim Loop Live Test" }
export const sweepTrimLoopLiveTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";

        // First, pure: chainer output straight into the cyclic mask. This round trip lives here
        // because it is the only place both modules are visible, and it is the one thing neither
        // self test can check - the mask had only ever seen hand-built loops, whose stored
        // convention is not the one the chainer produces for a WINDING loop.
        var roundTripFailures = 0;
        for (var seamSegmentCount in [8, 1])
        {
            const seamLoops = chainUvPolylinesIntoLoops(
                    [seamArcSamples(0.2, seamSegmentCount), seamArcSamples(0.8, seamSegmentCount)], 1e-9, 1);
            if (size(seamLoops) != 2)
            {
                roundTripFailures += 1;
                continue;
            }
            var maskLoops = makeArray(2);
            for (var loopIndex = 0; loopIndex < 2; loopIndex += 1)
            {
                maskLoops[loopIndex] = { "points" : seamLoops[loopIndex].points,
                        "winding" : seamLoops[loopIndex].winding };
            }
            for (var probeU in [0, 0.125, 0.5, 0.875, 0.99])
            {
                if (!uvPointInsideLoopsCyclic(maskLoops, probeU, 0.5, 1) ||
                    uvPointInsideLoopsCyclic(maskLoops, probeU, 0.05, 1) ||
                    uvPointInsideLoopsCyclic(maskLoops, probeU, 0.95, 1))
                {
                    roundTripFailures += 1;
                }
            }
        }
        println("[TRIM LIVE TEST] chainer to cyclic mask round trip: " ~ roundTripFailures ~
            " misclassification(s) of 30");
        if (roundTripFailures != 0)
        {
            failures = failures ~ " chained winding loops were misclassified by the cyclic mask at " ~
                roundTripFailures ~ " probe(s).";
        }

        // The sheet: degrees (2, 2) reproduce S(u, v) = (0.2u, 0.15v, 0.1u^2) exactly, since x
        // and y are linear and z is a quadratic in u alone.
        const xCoefficients = [0, 0.1, 0.2];
        const yCoefficients = [0, 0.075, 0.15];
        const zCoefficients = [0, 0, 0.1];
        var netRows = makeArray(3);
        for (var i = 0; i < 3; i += 1)
        {
            var row = makeArray(3);
            for (var j = 0; j < 3; j += 1)
            {
                row[j] = vector(xCoefficients[i], yCoefficients[j], zCoefficients[i]) * meter;
            }
            netRows[i] = row;
        }
        const sheetId = id + "sheet";
        opCreateBSplineSurface(context, sheetId, {
                    "bSplineSurface" : bSplineSurface({
                                "uDegree" : 2,
                                "vDegree" : 2,
                                "isUPeriodic" : false,
                                "isVPeriodic" : false,
                                "controlPoints" : controlPointMatrix(netRows)
                            })
                });

        // Split the sheet with a circle projected straight up. Splitting rather than deleting
        // leaves BOTH faces on the same surface: the annulus carries the hole, the disc does
        // not, and the two must disagree about the domain centre.
        const sketchId = id + "holeSketch";
        var holeSketch = newSketchOnPlane(context, sketchId, {
                    "sketchPlane" : plane(vector(0, 0, 0) * meter, vector(0, 0, 1))
                });
        skCircle(holeSketch, "hole", {
                    "center" : vector(0.1, 0.075) * meter,
                    "radius" : 0.03 * meter
                });
        skSolve(holeSketch);
        opSplitFace(context, id + "split", {
                    "faceTargets" : qOwnedByBody(qCreatedBy(sheetId, EntityType.BODY), EntityType.FACE),
                    "edgeTools" : qCreatedBy(sketchId, EntityType.EDGE),
                    "projectionType" : ProjectionType.DIRECTION,
                    "direction" : vector(0, 0, 1)
                });
        opDeleteBodies(context, id + "dropSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

        // Scoped to the sheet this feature created, never qEverything: the harness Part Studio
        // carries bodies from earlier runs, and a live run that reads "every sheet body" reads
        // those too - measured, five faces instead of two.
        const toolBody = qCreatedBy(sheetId, EntityType.BODY);
        const faceRecords = extractToolFaceRecords(context, toolBody, 1e-6);
        println("[TRIM LIVE TEST] " ~ summarizeFaceRecords(faceRecords));
        if (size(faceRecords) != 2)
        {
            reportFeatureInfo(context, id, "FAIL: the split sheet has " ~ size(faceRecords) ~
                " face(s), expected 2.");
            return;
        }
        const coEdgeRecords = extractCoEdgeRecords(context, toolBody, faceRecords, 33);
        println("[TRIM LIVE TEST] " ~ summarizeCoEdgeRecords(coEdgeRecords));

        // The annulus is the larger face. Both records must be exact B-splines, or the loops
        // would be coming from kernel trim curves instead of the pcurve path this test is for.
        var annulusIndex = 0;
        var discIndex = 1;
        if (evArea(context, { "entities" : faceRecords[0].faceQuery }) <
            evArea(context, { "entities" : faceRecords[1].faceQuery }))
        {
            annulusIndex = 1;
            discIndex = 0;
        }
        for (var record in faceRecords)
        {
            if (!record.splineIsExact)
            {
                failures = failures ~ " face " ~ record.faceIndex ~ " came back as " ~
                    record.surfaceClass ~ ", so it took the approximation path and the co-edge " ~
                    "pcurve path is not what ran.";
            }
        }

        const annulusTrim = buildFaceTrimLoops(faceRecords[annulusIndex], coEdgeRecords, {});
        const discTrim = buildFaceTrimLoops(faceRecords[discIndex], coEdgeRecords, {});
        println("[TRIM LIVE TEST] annulus " ~ summarizeTrimLoops(annulusTrim));
        println("[TRIM LIVE TEST] disc    " ~ summarizeTrimLoops(discTrim));
        if (annulusTrim.source != "coEdges" || annulusTrim.loopCount != 2 || !annulusTrim.usable ||
            annulusTrim.windingLoopCount != 0)
        {
            failures = failures ~ " the annulus did not convert to two closed co-edge loops.";
        }
        if (discTrim.source != "coEdges" || discTrim.loopCount != 1 || !discTrim.usable)
        {
            failures = failures ~ " the disc did not convert to one closed co-edge loop.";
        }

        // The mask geometry, read off the ellipse. Sixteen probes at 0.5 and 1.6 times the hole
        // radius, taken as points ON the surface so their uv is exact: the inner ring must be
        // masked out of the annulus and kept by the disc, and the outer ring the reverse.
        const holeCentre = vector(0.1, 0.075);
        var innerFailures = 0;
        var outerFailures = 0;
        for (var probeIndex = 0; probeIndex < 8; probeIndex += 1)
        {
            const angle = 2 * PI * probeIndex / 8 * radian;
            const direction = vector(cos(angle), sin(angle));
            const innerUv = surfacePointUv(holeCentre + 0.5 * 0.03 * direction);
            const outerUv = surfacePointUv(holeCentre + 1.6 * 0.03 * direction);
            if (uvPointInsideLoops(annulusTrim.loops, innerUv[0], innerUv[1]) ||
                !uvPointInsideLoops(discTrim.loops, innerUv[0], innerUv[1]))
            {
                innerFailures += 1;
            }
            if (!uvPointInsideLoops(annulusTrim.loops, outerUv[0], outerUv[1]) ||
                uvPointInsideLoops(discTrim.loops, outerUv[0], outerUv[1]))
            {
                outerFailures += 1;
            }
        }
        println("[TRIM LIVE TEST] mask probes: " ~ innerFailures ~ " inner and " ~ outerFailures ~
            " outer misclassification(s) of 8 each");
        if (innerFailures != 0 || outerFailures != 0)
        {
            failures = failures ~ " the converted loops misclassified " ~ (innerFailures + outerFailures) ~
                " of 16 probes around the hole.";
        }

        // The domain corners: the annulus's outer loop runs exactly along them, so this is the
        // boundary-tolerance path.
        var cornerFailures = 0;
        for (var cornerU in [0, 1])
        {
            for (var cornerV in [0, 1])
            {
                if (!uvPointInsideLoops(annulusTrim.loops, cornerU, cornerV) &&
                    !uvPointOnLoops(annulusTrim.loops, cornerU, cornerV, 1e-6, 0))
                {
                    cornerFailures += 1;
                }
            }
        }
        println("[TRIM LIVE TEST] domain corners rejected by the mask: " ~ cornerFailures ~ " of 4");
        if (cornerFailures != 0)
        {
            failures = failures ~ " the boundary tolerance did not keep the domain corners.";
        }

        // The census, which is the whole point: the same face, masked with its own trim loops.
        const factors = buildEnvelopePatchFactors(faceRecords[annulusIndex].spline);
        const spans = buildMotionSpanPolynomials(constantVelocityTranslationMotion(vector(1, 0, 0.5)));
        // No sign tolerance is supplied, and none is needed: this fixture's contact set lands
        // exactly ON the u = 0.5 node line, where f is zero in closed form but comes out of the
        // coefficient path at ~1e-18 with a sign that is pure rounding, and the census derives
        // its own threshold from each block's value range. The guessed 1e-12 this test used to
        // pass is what spec 12.1 item 5 removed; with an absolute zero the slab came out a ragged
        // 80 cells instead of 128, per v node.
        var censusOptions = { "uNodesPerPatch" : 9, "vNodesPerPatch" : 9, "tNodesPerSpan" : 9,
                "valueTolerance" : 0, "trimLoops" : [], "uPeriodic" : false };
        const unmasked = censusFunnelComponents(factors, spans, censusOptions);
        var maskedOptions = censusOptions;
        maskedOptions.trimLoops = annulusTrim.loops;
        maskedOptions.degenerate = faceRecords[annulusIndex].degenerate;
        const masked = censusFunnelComponents(factors, spans, maskedOptions);
        var maskedCells = "";
        var maskedCellTotal = 0;
        var maskedTrimTouching = 0;
        for (var component in masked.components)
        {
            maskedCells = maskedCells ~ " " ~ component.cellCount;
            maskedCellTotal += component.cellCount;
            maskedTrimTouching += component.touchesTrimBoundary ? 1 : 0;
        }
        println("[TRIM LIVE TEST] census sign tolerance derived from the block range: " ~
            unmasked.signTolerance.minimum ~ ", " ~ unmasked.signTolerance.zeroSignNodeCount ~
            " zero-sign node(s) of 729 (closed form: the 81 of the u = 0.5 plane)");
        println("[TRIM LIVE TEST] census unmasked: " ~ size(unmasked.components) ~ " component(s), " ~
            (size(unmasked.components) > 0 ? (unmasked.components[0].cellCount ~ " cells") : "") ~
            "; masked with the face's own loops: " ~ size(masked.components) ~ " component(s), cells" ~
            maskedCells ~ ", " ~ maskedTrimTouching ~ " trim-touching (closed form: 128 cells " ~
            "cut to 32 + 32)");
        if (size(unmasked.components) != 1 || unmasked.components[0].cellCount != 128)
        {
            failures = failures ~ " the unmasked census was expected to be one 128-cell slab at " ~
                "u = 0.5, got " ~ size(unmasked.components) ~ " component(s).";
        }
        // The hole spans the four middle v cell rows of eight, so exactly half the slab survives,
        // in two equal halves.
        if (size(masked.components) != 2 || maskedTrimTouching != 2 ||
            masked.components[0].cellCount != 32 || masked.components[1].cellCount != 32)
        {
            failures = failures ~ " the hole did not cut the slab into two 32-cell halves (" ~
                maskedCellTotal ~ " of " ~ unmasked.components[0].cellCount ~ " cells survived).";
        }

        reportTestVerdict(context, id, "TRIM LOOP LIVE TEST", failures,
            "a real face's co-edge pcurves converted into closed uv loops, the loops " ~
            "classified 20 of 20 probes including the domain corners, and the census masked " ~
            "with them split the contact slab exactly as the closed form predicts.");
    });

/** A v-constant trim run across the whole seam of a unit-period u domain. */
function seamArcSamples(v is number, segmentCount is number) returns array
{
    var samples = makeArray(segmentCount + 1, vector(0, v));
    for (var index = 0; index <= segmentCount; index += 1)
    {
        samples[index] = vector(index / segmentCount, v);
    }
    return samples;
}

/**
 * The uv of a point on the fixture surface, from its x and y alone: S(u, v) = (0.2u, 0.15v, ...)
 * inverts by division, so the probe positions carry no inversion error of their own.
 */
function surfacePointUv(xy is Vector) returns Vector
{
    return vector(xy[0] / 0.2, xy[1] / 0.15);
}

