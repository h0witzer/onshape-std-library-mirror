FeatureScript 3044;

/**
 * Surface Tween Feature
 * 
 * This feature creates a median (tweened) surface between two input surfaces.
 * It is inspired by the Parasolid PK_neutral_method_medial_c function which creates
 * a neutral sheet that is an "average" mid-surface between two faces.
 * 
 * The implementation works with B-spline surface representations directly:
 * 1. Obtains B-spline surface definitions for both input faces (using approximation if needed)
 * 2. Detects and corrects for misaligned parametric directions (U/V flips and swaps) using corner
 *    points, applied as an exact reindexing of the control grid AND its knot vectors
 * 3. Makes the two surfaces exactly compatible - common degree AND a common knot vector per
 *    direction - via splineRefinementUtils.fs's makeSurfacesCompatible
 * 4. Interpolates the two control nets, in homogeneous coordinates
 * 5. Creates a new B-spline surface on the SHARED knot vectors
 *
 * Key Features:
 * - Automatic alignment matching: Tests 8 possible transformations (U-flip, V-flip, UV-swap,
 *   and combinations) to find the best correspondence between surfaces
 * - Full NURBS support: rational throughout (unit weights are supplied where a surface had none),
 *   so there is a single homogeneous-coordinate code path rather than parallel rational and
 *   non-rational ones
 * - Periodic directions are PRESERVED, not clamped: a cylinder, cone, or revolve keeps its seam
 *   closed through refinement, elevation, and knot sharing. Only a direction where one surface
 *   wraps and the other does not is clamped, since "closed" has no shared meaning there
 * - Exact endpoint preservation: at fraction=0 the result reproduces the first surface exactly,
 *   and at fraction=1 the second. This now actually holds. It did not before: the old pipeline
 *   INTERPOLATED the two surfaces' knot vectors, producing a vector belonging to neither, so
 *   fraction=0 only reproduced the first surface when both happened to be parameterized alike.
 *   Sharing one exact knot vector is what makes control-net interpolation mean what it claims
 *   (the affine argument - basis functions are a partition of unity, so blending control nets
 *   over a SHARED basis is exactly blending the surfaces).
 *
 * Interpolation behavior:
 * - fraction = 0: surface coincident with first surface (EXACT)
 * - fraction = 0.5: median surface (equidistant from both surfaces)
 * - fraction = 1: surface coincident with second surface (EXACT)
 *
 * Future enhancement: Support for the Parasolid-style parameter p where
 * each point satisfies (1 - p) D1 = (1 + p) D2, allowing for weighted median surfaces.
 */

// Standard Library Imports
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/context.fs", version : "3044.0");
import(path : "onshape/std/evaluate.fs", version : "3044.0");
import(path : "onshape/std/feature.fs", version : "3044.0");
import(path : "onshape/std/geomOperations.fs", version : "3044.0");
import(path : "onshape/std/query.fs", version : "3044.0");
import(path : "onshape/std/vector.fs", version : "3044.0");
import(path : "onshape/std/units.fs", version : "3044.0");
import(path : "onshape/std/valueBounds.fs", version : "3044.0");
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0");
import(path : "onshape/std/error.fs", version : "3044.0");
import(path : "onshape/std/coordSystem.fs", version : "3044.0");
import(path : "onshape/std/containers.fs", version : "3044.0");
import(path : "onshape/std/splineUtils.fs", version : "3044.0");
import(path : "onshape/std/nurbsUtils.fs", version : "3044.0");
import(path : "onshape/std/math.fs", version : "3044.0");

// splineRefinementUtils.fs — exact B-spline refinement, degree elevation, and periodic-preserving knot sharing
import(path : "eca0e7b6ed29c5239f39f868/36185a3777394c9ecb0bfc3e/9a2b77793cdc37bace6d915a", version : "db7f981bb900fa1e8c01effc");


export const SURFACE_TWEEN_FRACTION_BOUNDS = { (unitless) : [0, 0.5, 1] } as RealBoundSpec;


/**
 * Feature that creates a median (tweened) surface between two input surfaces.
 * 
 * This creates a neutral sheet that is an "average" mid-surface between the two selected surfaces.
 * The tween fraction controls the position of the resulting surface:
 * - fraction = 0: coincident with first surface
 * - fraction = 0.5: median surface (default, equidistant from both surfaces)
 * - fraction = 1: coincident with second surface
 * 
 * The implementation obtains B-spline representations of both surfaces and directly
 * interpolates their control points to create a new B-spline surface.
 */
annotation { "Feature Type Name" : "Tween Surfaces",
        "Feature Type Description" : "Creates a median surface between two input surfaces by interpolating B-spline control points. At fraction 0.5, creates a neutral sheet that is equidistant between the two surfaces.",
        "UIHint" : "NO_PREVIEW_PROVIDED" }
export const tweenSurfaces = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "First surface", "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO, "MaxNumberOfPicks" : 1 }
        definition.firstSurface is Query;
        
        annotation { "Name" : "Second surface", "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO, "MaxNumberOfPicks" : 1 }
        definition.secondSurface is Query;
        
        annotation { "Name" : "Tween fraction", "Description" : "Position of the median surface: 0 = first surface, 0.5 = middle, 1 = second surface" }
        isReal(definition.tweenFraction, SURFACE_TWEEN_FRACTION_BOUNDS);
        
        annotation { "Name" : "Enable diagnostics" }
        definition.enableDiagnostics is boolean;
        
        annotation { "Group Name" : "Developer diagnostics", "Driving Parameter" : "enableDiagnostics", "Collapsed By Default" : true }
        {
            if (definition.enableDiagnostics)
            {
                annotation { "Name" : "Surface degree information" }
                definition.diagnosticSurfaceDegreeInfo is boolean;
                
                annotation { "Name" : "Surface alignment matching" }
                definition.diagnosticSurfaceAlignment is boolean;
                
                annotation { "Name" : "Degree elevation details" }
                definition.diagnosticDegreeElevation is boolean;
                
                annotation { "Name" : "Control point refinement details" }
                definition.diagnosticControlPointRefinement is boolean;
                
                annotation { "Name" : "Control point interpolation" }
                definition.diagnosticControlPointInterpolation is boolean;
                
                annotation { "Name" : "Control point visualization" }
                definition.diagnosticControlPointVisualization is boolean;
                
                annotation { "Name" : "Knot vector processing" }
                definition.diagnosticKnotVectorProcessing is boolean;
            }
        }
    }
    {
        // Validate inputs
        if (evaluateQueryCount(context, definition.firstSurface) == 0)
            throw regenError("Select first surface.", ["firstSurface"]);
        if (evaluateQueryCount(context, definition.secondSurface) == 0)
            throw regenError("Select second surface.", ["secondSurface"]);
        
        const firstFace = evaluateQuery(context, definition.firstSurface)[0];
        const secondFace = evaluateQuery(context, definition.secondSurface)[0];
        
        // Create the tweened surface
        createTweenedSurface(context, id, firstFace, secondFace, definition.tweenFraction, definition);
    }, { 
        tweenFraction : 0.5,
        enableDiagnostics : false,
        diagnosticSurfaceDegreeInfo : false,
        diagnosticDegreeElevation : false,
        diagnosticControlPointRefinement : false,
        diagnosticSurfaceAlignment : false,
        diagnosticControlPointInterpolation : false,
        diagnosticControlPointVisualization : false,
        diagnosticKnotVectorProcessing : false
    });


/**
 * Creates a tweened surface between two faces by interpolating B-spline control points.
 * 
 * The algorithm:
 * 1. Obtains B-spline surface representations of both input faces
 *    - If a face is already a B-spline, uses its definition directly
 *    - Otherwise, creates a B-spline approximation
 * 2. Finds preliminary alignment using corner points to determine correct parametric orientation
 * 3. Applies alignment transformation (UV flip/swap) to second surface
 * 4. Elevates degrees to match if necessary (now working in correct parametric space)
 * 5. Refines control point counts to match if necessary (now working in correct parametric space)
 * 6. Interpolates control points: tweenedCP = (1 - fraction) * cp1 + fraction * cp2
 * 7. Creates a new B-spline surface with the interpolated control points
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : The feature identifier
 * @param firstFace {Query} : Query resolving to the first face
 * @param secondFace {Query} : Query resolving to the second face
 * @param tweenFraction {number} : The interpolation fraction (0 to 1)
 * @param definition {map} : The feature definition including diagnostics settings
 */
function createTweenedSurface(context is Context, id is Id,
        firstFace is Query, secondFace is Query, tweenFraction is number, definition is map)
{
    // Get B-spline surface representations of both faces
    var firstSurface = getBSplineSurfaceFromFace(context, firstFace);
    var secondSurface = getBSplineSurfaceFromFace(context, secondFace);

    if (definition.diagnosticSurfaceDegreeInfo)
    {
        println("DEBUG: Initial first surface - uDegree=" ~ firstSurface.uDegree ~ ", vDegree=" ~ firstSurface.vDegree ~
                ", controlPoints=" ~ size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]) ~
                ", isUPeriodic=" ~ (firstSurface.isUPeriodic == true) ~ ", isVPeriodic=" ~ (firstSurface.isVPeriodic == true));
        println("DEBUG: Initial second surface - uDegree=" ~ secondSurface.uDegree ~ ", vDegree=" ~ secondSurface.vDegree ~
                ", controlPoints=" ~ size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]) ~
                ", isUPeriodic=" ~ (secondSurface.isUPeriodic == true) ~ ", isVPeriodic=" ~ (secondSurface.isVPeriodic == true));
        // RAW dump of the FULL grid, before ANY processing. Every row, not row 0 alone - the
        // question this exists to answer is whether rows n..n+degree-1 of a kernel periodic
        // surface are literal copies of rows 0..degree-1 (the wrap convention) or the second
        // half of the closed shape with only the LAST row repeating the FIRST (the
        // closed-clamped convention). Row 0 is identical under both, and a fixture was once
        // built - wrongly - from less than the full picture.
        for (var rowIndex = 0; rowIndex < size(firstSurface.controlPoints); rowIndex += 1)
        {
            println("DEBUG: RAW first row " ~ rowIndex ~ ": " ~ firstSurface.controlPoints[rowIndex] ~
                (firstSurface.weights == undefined ? "" : " | weights " ~ firstSurface.weights[rowIndex]));
        }
        for (var rowIndex = 0; rowIndex < size(secondSurface.controlPoints); rowIndex += 1)
        {
            println("DEBUG: RAW second row " ~ rowIndex ~ ": " ~ secondSurface.controlPoints[rowIndex] ~
                (secondSurface.weights == undefined ? "" : " | weights " ~ secondSurface.weights[rowIndex]));
        }
        println("DEBUG: RAW first surface uKnots: " ~ firstSurface.uKnots ~ ", vKnots: " ~ firstSurface.vKnots);
        println("DEBUG: RAW second surface uKnots: " ~ secondSurface.uKnots ~ ", vKnots: " ~ secondSurface.vKnots);
    }

    // === ALIGNMENT ===
    // Which way the second surface's UV grid lays against the first is a genuine degree of
    // freedom - there is no canonical correspondence between two surfaces - so this picks one and
    // applies it EXACTLY. Every operation used is a reindexing of the control grid paired with the
    // matching transform of the knot vectors, so the second surface's geometry never moves; only
    // the labelling of which parameter is which changes, which is what decides control point
    // (i, j) of one blends against (i, j) of the other. It must happen BEFORE compatibility:
    // flipping a control grid after the two share a knot vector, without reversing that vector
    // too, silently distorts the surface unless the vector happens to be symmetric.

    // Step 1: UV swap. When the two surfaces' PERIODICITY PATTERNS differ - one wraps in U, the
    // other in V - the swap is forced, and that is a far more reliable signal than corner
    // geometry. When BOTH directions of both surfaces wrap (a torus against a torus) periodicity
    // says nothing and neither do corners, so both orientations are carried forward and settled by
    // the exact net comparison below. Otherwise corner control points decide, which is exact data
    // for a clamped direction - a clamped direction's first control point IS the surface corner.
    const firstUPeriodic = firstSurface.isUPeriodic == true;
    const firstVPeriodic = firstSurface.isVPeriodic == true;
    const secondUPeriodic = secondSurface.isUPeriodic == true;
    const secondVPeriodic = secondSurface.isVPeriodic == true;
    const periodicityIsDecisive = (firstUPeriodic != firstVPeriodic) && (secondUPeriodic != secondVPeriodic);
    const bothDirectionsWrap = firstUPeriodic && firstVPeriodic && secondUPeriodic && secondVPeriodic;

    const preliminaryAlignmentResult = findPreliminaryAlignment(firstSurface.controlPoints, secondSurface.controlPoints);
    var swapCandidates = [preliminaryAlignmentResult.swapUV];
    if (periodicityIsDecisive)
    {
        swapCandidates = [firstUPeriodic != secondUPeriodic];
    }
    else if (bothDirectionsWrap)
    {
        swapCandidates = [false, true];
    }

    // Steps 2-4: for each surviving orientation, settle the flips, make the pair compatible, and
    // search the seams - then keep whichever orientation scored best. Compatibility has to happen
    // inside the loop because it is what puts the two control nets in a shared basis, and that
    // shared basis is the only thing that makes the seam comparison exact rather than sampled.
    var bestScore = -1e30;
    var bestPair = undefined;
    var bestReport = "";
    for (var swapUV in swapCandidates)
    {
        const oriented = swapUV ? applyAlignmentTransform(secondSurface, false, false, true) : secondSurface;

        // A flip is only meaningful from corner geometry in a CLAMPED direction. In a periodic
        // direction there are no corners - index 0 is an arbitrary seam - so the corner answer
        // there is noise, and traversal direction is settled by the net search instead, jointly
        // with the seam offset it cannot be separated from.
        const uIsPeriodicPair = firstUPeriodic && (oriented.isUPeriodic == true);
        const vIsPeriodicPair = firstVPeriodic && (oriented.isVPeriodic == true);
        const cornerResult = swapUV ? findPreliminaryAlignment(firstSurface.controlPoints, oriented.controlPoints)
            : preliminaryAlignmentResult;
        const flipU = uIsPeriodicPair ? false : cornerResult.flipU;
        const flipV = vIsPeriodicPair ? false : cornerResult.flipV;
        const flipped = (flipU || flipV) ? applyAlignmentTransform(oriented, flipU, flipV, false) : oriented;

        const sharedPair = makeSurfacesCompatible(firstSurface, flipped);
        const seam = bestPeriodicNetAlignment(sharedPair.a, sharedPair.b, uIsPeriodicPair, vIsPeriodicPair);

        if (seam.score > bestScore)
        {
            bestScore = seam.score;
            bestReport = "flipU=" ~ flipU ~ ", flipV=" ~ flipV ~ ", swapUV=" ~ swapUV ~
                ", reverseU=" ~ seam.reverseU ~ ", reverseV=" ~ seam.reverseV ~
                ", seam shift=(" ~ seam.shiftU ~ ", " ~ seam.shiftV ~ ")" ~
                ", normalized net correlation=" ~ seam.score;

            // Apply the winning reversal first. Reversing a direction also reverses its knot
            // vector, so the pair stops sharing one and has to be re-shared before anything else
            // can reason about corresponding indices.
            //
            // The reversal is the MODULE's reverseSurfaceDirection, NOT applyAlignmentTransform:
            // pairB is a normalized wrap-form surface here, and the legacy hand-rolled
            // `1 - knot` flip leaves a reversed periodic direction's seam multiplicity run
            // split across the domain boundary — a noncanonical spelling of the same structure
            // that made the re-share below demand phantom seam-image insertions above the
            // multiplicity cap (the live cone-to-cylinder failure). applyAlignmentTransform
            // remains correct for the RAW pre-normalization forms it is used on above.
            var pairA = sharedPair.a;
            var pairB = sharedPair.b;
            if (seam.reverseU || seam.reverseV)
            {
                var reversedB = pairB;
                if (seam.reverseU)
                {
                    reversedB = reverseSurfaceDirection(reversedB, true);
                }
                if (seam.reverseV)
                {
                    reversedB = reverseSurfaceDirection(reversedB, false);
                }
                const reshared = makeSurfacesShareKnotVectors(pairA, reversedB);
                pairA = reshared.a;
                pairB = reshared.b;
            }

            // Re-run the shift search on the post-reversal structure rather than reusing the
            // indices found before it. Re-sharing can refine the knot vector, which changes what
            // an index means; carrying a stale index across that boundary is a silent misalignment.
            if (uIsPeriodicPair || vIsPeriodicPair)
            {
                const finalSeam = bestPeriodicNetAlignment(pairA, pairB, uIsPeriodicPair, vIsPeriodicPair);
                // alignPeriodicSurfaceSeams moves BOTH seams, splitting the required offset between
                // them so each lands on a multiplicity-1 knot. Re-windowing only surface B - the
                // obvious approach, and the one that shipped broken - cannot do this: a revolve's
                // circular direction is stored as Bezier arcs, so every nonzero seam position on
                // one surface is the C0 arc joint, and a C0 seam makes the periodic surface
                // invalid (PERIODIC_BSPLINESURFACE_NOT_SMOOTH).
                if (finalSeam.shiftU != 0)
                {
                    const seamAligned = alignPeriodicSurfaceSeams(pairA, pairB, true, finalSeam.shiftU);
                    pairA = seamAligned.a;
                    pairB = seamAligned.b;
                }
                if (finalSeam.shiftV != 0)
                {
                    const seamAligned = alignPeriodicSurfaceSeams(pairA, pairB, false, finalSeam.shiftV);
                    pairA = seamAligned.a;
                    pairB = seamAligned.b;
                }
            }
            bestPair = { "a" : pairA, "b" : pairB };
        }
    }

    if (definition.diagnosticSurfaceAlignment)
    {
        println("DEBUG: Corner alignment suggested - flipU: " ~ preliminaryAlignmentResult.flipU ~
                ", flipV: " ~ preliminaryAlignmentResult.flipV ~
                ", swapUV: " ~ preliminaryAlignmentResult.swapUV ~
                ", corner distance: " ~ preliminaryAlignmentResult.distance);
        println("DEBUG: Resolved alignment - " ~ bestReport);
    }

    // === EXACT COMPATIBILITY (spec sections 2.4 and 3.1) ===
    // Already done, inside the alignment loop above - makeSurfacesCompatible had to run there
    // because a shared basis is the precondition for comparing the two control nets exactly. The
    // winning orientation's compatible pair is what comes out.
    //
    // That one call replaced the whole elevate-then-refine-then-hope pipeline this feature used to
    // run. It brings both surfaces to a common degree AND a common knot vector per direction, so
    // blending control point (i, j) of one against (i, j) of the other is exactly blending the
    // surfaces themselves (spec section 3.2's affine argument).
    //
    // "Same degrees, same control point counts" - what this feature used to check - is necessary
    // but nowhere near sufficient: two surfaces can match on both while control point (i, j)
    // means something different on each. That is the root cause of "Tween Surfaces never quite
    // worked". The old code papered over it by INTERPOLATING the two knot vectors, which is not a
    // knot vector for either surface and not one for the result; at fraction 0 it did not even
    // reproduce the first surface unless the two happened to be parameterized identically.
    //
    // Periodic directions stay periodic all the way through (exact periodic-preserving refinement
    // in splineRefinementUtils.fs), so a cylinder, cone, or revolve keeps its seam closed instead
    // of being clamped open. Only a direction where ONE surface wraps and the other does not gets
    // clamped, because there "closed" has no shared meaning to preserve.
    if (bestPair == undefined)
    {
        throw regenError("Internal error: no surface orientation was evaluated.", ["firstSurface", "secondSurface"]);
    }
    const first = bestPair.a;
    const second = bestPair.b;

    if (definition.diagnosticSurfaceAlignment)
    {
        // Re-run the full 8-transform search on the FINAL grids. The alignment was chosen from
        // corner points before compatibility; if this now reports a further flip or swap would
        // fit better, the corner heuristic picked wrong for these two surfaces - which is worth
        // seeing, since nothing downstream can detect it.
        const verificationResult = findBestSurfaceAlignment(first.controlPoints, second.controlPoints, second.weights);
        println("DEBUG: Final alignment verification - flipU: " ~ verificationResult.flipU ~
                ", flipV: " ~ verificationResult.flipV ~
                ", swapUV: " ~ verificationResult.swapUV ~
                ", total squared distance: " ~ verificationResult.distance);
        if (verificationResult.flipU || verificationResult.flipV || verificationResult.swapUV)
        {
            println("WARNING: A further transform would fit better - the corner-point alignment likely picked wrong.");
        }
    }

    if (definition.diagnosticDegreeElevation || definition.diagnosticControlPointRefinement ||
        definition.diagnosticKnotVectorProcessing)
    {
        println("DEBUG: After exact compatibility - uDegree=" ~ first.uDegree ~ ", vDegree=" ~ first.vDegree ~
                ", controlPoints=" ~ size(first.controlPoints) ~ "x" ~ size(first.controlPoints[0]));
        println("DEBUG:   shared U knots (" ~ size(first.uKnots) ~ "): " ~ first.uKnots);
        println("DEBUG:   shared V knots (" ~ size(first.vKnots) ~ "): " ~ first.vKnots);
        println("DEBUG:   isUPeriodic=" ~ (first.isUPeriodic == true) ~ ", isVPeriodic=" ~ (first.isVPeriodic == true) ~
                ", wasClampedFromPeriodic=" ~ (first.wasClampedFromPeriodic == true || second.wasClampedFromPeriodic == true));
    }

    // Defensive only: makeSurfacesCompatible guarantees each of these. Reaching one means a
    // module regression, not bad input.
    if (first.uDegree != second.uDegree || first.vDegree != second.vDegree)
    {
        throw regenError("Internal error: surface degrees still differ after compatibility processing.");
    }
    if (size(first.controlPoints) != size(second.controlPoints) ||
        size(first.controlPoints[0]) != size(second.controlPoints[0]))
    {
        throw regenError("Internal error: surfaces have different control point grids (" ~
                size(first.controlPoints) ~ "x" ~ size(first.controlPoints[0]) ~ " vs " ~
                size(second.controlPoints) ~ "x" ~ size(second.controlPoints[0]) ~ ") after compatibility processing.");
    }

    // === CONTROL NET INTERPOLATION ===
    // Always rational here: normalizeSurfaceDefinition gives every surface unit weights when it
    // had none, so there is one code path rather than a rational branch and a non-rational branch
    // that have to be kept in step. Rational surfaces interpolate in homogeneous coordinates -
    // weight each control point, blend, divide back out by the blended weight.
    const rowCount = size(first.controlPoints);
    const columnCount = size(first.controlPoints[0]);
    var tweenedControlPoints = makeArray(rowCount, 0);
    var tweenedWeights = makeArray(rowCount, 0);
    var debugPointCount = 0;

    for (var uIndex = 0; uIndex < rowCount; uIndex += 1)
    {
        var controlPointRow = makeArray(columnCount, first.controlPoints[0][0]);
        var weightRow = makeArray(columnCount, 1);
        for (var vIndex = 0; vIndex < columnCount; vIndex += 1)
        {
            const firstControlPoint = first.controlPoints[uIndex][vIndex];
            const secondControlPoint = second.controlPoints[uIndex][vIndex];
            const firstWeight = first.weights[uIndex][vIndex];
            const secondWeight = second.weights[uIndex][vIndex];

            const blendedWeight = firstWeight * (1 - tweenFraction) + secondWeight * tweenFraction;
            const blendedWeightedPoint = (firstControlPoint * firstWeight) * (1 - tweenFraction) +
                (secondControlPoint * secondWeight) * tweenFraction;

            controlPointRow[vIndex] = blendedWeightedPoint / blendedWeight;
            weightRow[vIndex] = blendedWeight;

            if (definition.diagnosticControlPointVisualization)
            {
                debug(context, firstControlPoint, DebugColor.BLUE);
                debug(context, secondControlPoint, DebugColor.RED);
                debug(context, controlPointRow[vIndex], DebugColor.GREEN);
                debugPointCount += 1;
            }
            if (definition.diagnosticControlPointInterpolation && uIndex == 0 && vIndex == 0)
            {
                println("DEBUG: Corner CP interpolation (fraction=" ~ tweenFraction ~ "):");
                println("  First CP: " ~ firstControlPoint ~ ", weight: " ~ firstWeight);
                println("  Second CP: " ~ secondControlPoint ~ ", weight: " ~ secondWeight);
                println("  Tweened CP: " ~ controlPointRow[vIndex] ~ ", weight: " ~ blendedWeight);
            }
        }
        tweenedControlPoints[uIndex] = controlPointRow;
        tweenedWeights[uIndex] = weightRow;
    }

    if (definition.diagnosticControlPointVisualization)
    {
        println("DEBUG: Drew " ~ debugPointCount ~ " sets of control points (blue/red/green)");
    }

    // The SHARED knot vectors are carried straight through to the result - not interpolated
    // (see above), and not unpadded first.
    //
    // Periodic directions are emitted in the CLOSED CLAMPED form: clamped knots over one
    // period, last control point coinciding with the first, isPeriodic kept true. That is the
    // convention the kernel itself returns for a revolve's periodic direction (confirmed by a
    // full raw dump) and provably accepts (a raw creation probe on exactly that form
    // succeeded), and it sidesteps the wrap-form seam-multiplicity restriction: a converted
    // revolve's seam knot carries multiplicity == degree, which the kernel rejects as a
    // non-smooth periodic WRAP seam while accepting the identical curve closed-clamped, judging
    // the closure from the control point geometry instead. The conversion is exact (clamped
    // extraction over one period), and it is the exact inverse of the input-side conversion in
    // normalizeSurfaceDefinition - so a no-op tween emits the kernel's own original arrays.
    var emitted = {
            "uDegree" : first.uDegree,
            "vDegree" : first.vDegree,
            "isUPeriodic" : first.isUPeriodic == true,
            "isVPeriodic" : first.isVPeriodic == true,
            "isRational" : true,
            "controlPoints" : tweenedControlPoints,
            "weights" : tweenedWeights,
            "uKnots" : first.uKnots,
            "vKnots" : first.vKnots
        };
    if (emitted.isUPeriodic)
    {
        emitted = toClosedClampedSurfaceDirection(emitted, true);
    }
    if (emitted.isVPeriodic)
    {
        emitted = toClosedClampedSurfaceDirection(emitted, false);
    }

    const tweenedSurfaceDefinition = bSplineSurface({
                "uDegree" : emitted.uDegree,
                "vDegree" : emitted.vDegree,
                "isUPeriodic" : emitted.isUPeriodic,
                "isVPeriodic" : emitted.isVPeriodic,
                "controlPoints" : controlPointMatrix(emitted.controlPoints),
                "weights" : matrix(emitted.weights),
                "uKnots" : emitted.uKnots is KnotArray ? emitted.uKnots : knotArray(emitted.uKnots),
                "vKnots" : emitted.vKnots is KnotArray ? emitted.vKnots : knotArray(emitted.vKnots)
            });

    opCreateBSplineSurface(context, id, {
                "bSplineSurface" : tweenedSurfaceDefinition
            });
}


/**
 * Choose the seam alignment for the periodic directions of an ALREADY-COMPATIBLE pair, and score
 * it — exactly, with no sampling anywhere.
 *
 * Why this can be exact at all: makeSurfacesCompatible has put both surfaces' control points in
 * the SAME basis, so control point (i, j) of one and (i, j) of the other are coefficients of the
 * same basis function. Comparing them is arithmetic on exact data, and by partition of unity the
 * distance between two control nets bounds the distance between the surfaces they describe. So the
 * discrete question "which cyclic shift lines these nets up" answers the continuous question
 * "which seam alignment lines these surfaces up", without evaluating either surface at a single
 * point. An earlier version sampled rings of points and correlated those; this replaces it, and
 * nothing downstream needs a tolerance or a sample count.
 *
 * Nets are CENTERED on their own centroid first, which is what lets two revolves of different
 * radius and position be compared: a cylinder and a cone are neither concentric nor the same size,
 * and uncentered distance would rank every rotation about equally. The score is a NORMALIZED
 * correlation (cosine similarity), so it stays comparable across candidates whose grids differ in
 * size — which matters when a UV swap is one of the candidates being weighed.
 *
 * Reversal is folded into the same search rather than decided separately, because a reversal and a
 * seam shift are not independent: reversing a closed direction also moves where its seam lands, so
 * choosing one without the other picks the wrong pair. Both directions are searched JOINTLY for the
 * same reason (a torus tweened against a torus has two free seams, and the best pair is not
 * generally the pair of individual bests).
 *
 * @returns {map} : { "shiftU", "shiftV", "reverseU", "reverseV", "score" }
 */
function bestPeriodicNetAlignment(reference is map, candidate is map, uIsPeriodicPair is boolean, vIsPeriodicPair is boolean) returns map
{
    const uDegree = reference.uDegree;
    const vDegree = reference.vDegree;
    const rowCount = size(reference.controlPoints);
    const columnCount = size(reference.controlPoints[0]);
    // A periodic direction stores `degree` overlap rows/columns beyond its fundamental period;
    // those are copies, so including them would just weight part of the net twice.
    const fundamentalRowCount = uIsPeriodicPair ? rowCount - uDegree : rowCount;
    const fundamentalColumnCount = vIsPeriodicPair ? columnCount - vDegree : columnCount;

    const referenceNet = centeredFundamentalNet(reference.controlPoints, fundamentalRowCount, fundamentalColumnCount);
    const referenceMagnitude = netMagnitudeSquared(referenceNet);

    var best = { "shiftU" : 0, "shiftV" : 0, "reverseU" : false, "reverseV" : false, "score" : -1e30 };

    const reverseUOptions = uIsPeriodicPair ? [false, true] : [false];
    const reverseVOptions = vIsPeriodicPair ? [false, true] : [false];
    for (var reverseU in reverseUOptions)
    {
        for (var reverseV in reverseVOptions)
        {
            // Score the candidate through the SAME permutation applyAlignmentTransform will
            // perform, rather than an equivalent-up-to-a-shift one: a reversal of the stored array
            // lands the fundamental sequence offset by (degree - 1), and scoring one convention
            // while applying another silently misaligns by exactly that much.
            const orientedCandidate = (reverseU || reverseV)
                ? applyAlignmentTransform(candidate, reverseU, reverseV, false) : candidate;
            const candidateNet = centeredFundamentalNet(orientedCandidate.controlPoints,
                    fundamentalRowCount, fundamentalColumnCount);
            const candidateMagnitude = netMagnitudeSquared(candidateNet);
            const normalizer = sqrt(referenceMagnitude * candidateMagnitude);

            const shiftURange = uIsPeriodicPair ? fundamentalRowCount : 1;
            const shiftVRange = vIsPeriodicPair ? fundamentalColumnCount : 1;
            for (var shiftU = 0; shiftU < shiftURange; shiftU += 1)
            {
                for (var shiftV = 0; shiftV < shiftVRange; shiftV += 1)
                {
                    var correlation = 0 * meter * meter;
                    for (var rowIndex = 0; rowIndex < fundamentalRowCount; rowIndex += 1)
                    {
                        var sourceRow = rowIndex + shiftU;
                        if (sourceRow >= fundamentalRowCount)
                        {
                            sourceRow -= fundamentalRowCount;
                        }
                        for (var columnIndex = 0; columnIndex < fundamentalColumnCount; columnIndex += 1)
                        {
                            var sourceColumn = columnIndex + shiftV;
                            if (sourceColumn >= fundamentalColumnCount)
                            {
                                sourceColumn -= fundamentalColumnCount;
                            }
                            correlation += dot(referenceNet[rowIndex][columnIndex], candidateNet[sourceRow][sourceColumn]);
                        }
                    }
                    // Maximizing correlation minimizes squared distance between the centered nets,
                    // since both magnitude terms are constant under a permutation of the same net.
                    // Normalizing keeps candidates with different grid sizes comparable.
                    const score = normalizer == 0 * meter * meter ? 0 : correlation / normalizer;
                    if (score > best.score)
                    {
                        best = { "shiftU" : shiftU, "shiftV" : shiftV, "reverseU" : reverseU, "reverseV" : reverseV, "score" : score };
                    }
                }
            }
        }
    }
    return best;
}

/** The fundamental (non-overlap) part of a control net, translated so its centroid is the origin.
    Centering is what makes two surfaces of different size and position comparable. */
function centeredFundamentalNet(controlPoints is array, fundamentalRowCount is number, fundamentalColumnCount is number) returns array
{
    var centroid = vector(0, 0, 0) * meter;
    for (var rowIndex = 0; rowIndex < fundamentalRowCount; rowIndex += 1)
    {
        for (var columnIndex = 0; columnIndex < fundamentalColumnCount; columnIndex += 1)
        {
            centroid += controlPoints[rowIndex][columnIndex];
        }
    }
    centroid = centroid / (fundamentalRowCount * fundamentalColumnCount);

    var net = makeArray(fundamentalRowCount, 0);
    for (var rowIndex = 0; rowIndex < fundamentalRowCount; rowIndex += 1)
    {
        var row = makeArray(fundamentalColumnCount, vector(0, 0, 0) * meter);
        for (var columnIndex = 0; columnIndex < fundamentalColumnCount; columnIndex += 1)
        {
            row[columnIndex] = controlPoints[rowIndex][columnIndex] - centroid;
        }
        net[rowIndex] = row;
    }
    return net;
}

/** Sum of squared magnitudes over a centered net — the normalizer for cosine similarity. */
function netMagnitudeSquared(net is array) returns ValueWithUnits
{
    var total = 0 * meter * meter;
    for (var rowIndex = 0; rowIndex < size(net); rowIndex += 1)
    {
        for (var columnIndex = 0; columnIndex < size(net[rowIndex]); columnIndex += 1)
        {
            total += squaredNorm(net[rowIndex][columnIndex]);
        }
    }
    return total;
}

/**
 * Obtains a B-spline surface representation from a face.
 * 
 * If the face is already a B-spline surface, returns its definition directly.
 * Otherwise, creates and returns a B-spline approximation of the face.
 * 
 * @param context {Context} : The modeling context
 * @param face {Query} : Query resolving to the face
 * @returns {map} : A B-spline surface definition with control points, degrees, knots, etc.
 */
function getBSplineSurfaceFromFace(context is Context, face is Query)
{
    // Try to get the surface definition
    var surfaceDefinition = evSurfaceDefinition(context, {
        "face" : face
    });
    
    // If it's already a B-spline, return it
    if (surfaceDefinition.surfaceType == SurfaceType.SPLINE)
    {
        return surfaceDefinition;
    }
    
    // Otherwise, create a B-spline approximation
    const approximation = evApproximateBSplineSurface(context, {
        "face" : face
    });
    
    return approximation.bSplineSurface;
}


/**
 * Computes the sum of squared distances between corresponding control points of two surfaces.
 * 
 * This is used as a metric to determine the best alignment between surfaces.
 * Lower distance indicates better alignment.
 * 
 * @param controlPoints1 {array} : Control point matrix for first surface (2D array)
 * @param controlPoints2 {array} : Control point matrix for second surface (2D array)
 * @returns {ValueWithUnits} : Sum of squared distances between corresponding points
 */
function surfaceControlPointDistanceSquared(controlPoints1 is array, controlPoints2 is array) returns ValueWithUnits
{
    var totalDistanceSquared = 0 * meter * meter;
    const numUPoints = size(controlPoints1);
    const numVPoints = size(controlPoints1[0]);
    
    for (var uIndex = 0; uIndex < numUPoints; uIndex += 1)
    {
        for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
        {
            const point1 = controlPoints1[uIndex][vIndex];
            const point2 = controlPoints2[uIndex][vIndex];
            totalDistanceSquared += squaredNorm(point1 - point2);
        }
    }
    
    return totalDistanceSquared;
}


/**
 * Flips the U direction of a control point matrix.
 * 
 * This reverses the order of rows in the control point matrix.
 * For a surface with control points CP[u][v], this produces CP[numU-1-u][v].
 * 
 * @param controlPoints {array} : Control point matrix (2D array)
 * @returns {array} : Control point matrix with U direction reversed
 */
function flipControlPointsU(controlPoints is array) returns array
{
    var flippedControlPoints = [];
    for (var uIndex = size(controlPoints) - 1; uIndex >= 0; uIndex -= 1)
    {
        flippedControlPoints = append(flippedControlPoints, controlPoints[uIndex]);
    }
    return flippedControlPoints;
}


/**
 * Flips the V direction of a control point matrix.
 * 
 * This reverses the order of columns in the control point matrix.
 * For a surface with control points CP[u][v], this produces CP[u][numV-1-v].
 * 
 * @param controlPoints {array} : Control point matrix (2D array)
 * @returns {array} : Control point matrix with V direction reversed
 */
function flipControlPointsV(controlPoints is array) returns array
{
    var flippedControlPoints = [];
    for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
    {
        var flippedRow = [];
        for (var vIndex = size(controlPoints[uIndex]) - 1; vIndex >= 0; vIndex -= 1)
        {
            flippedRow = append(flippedRow, controlPoints[uIndex][vIndex]);
        }
        flippedControlPoints = append(flippedControlPoints, flippedRow);
    }
    return flippedControlPoints;
}


/**
 * Transposes (swaps U and V) a control point matrix.
 * 
 * This swaps the U and V parametric directions.
 * For a surface with control points CP[u][v], this produces CP[v][u].
 * 
 * @param controlPoints {array} : Control point matrix (2D array)
 * @returns {array} : Control point matrix with U and V swapped
 */
function transposeControlPoints(controlPoints is array) returns array
{
    const numUPoints = size(controlPoints);
    const numVPoints = size(controlPoints[0]);
    
    var transposedControlPoints = [];
    for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
    {
        var newRow = [];
        for (var uIndex = 0; uIndex < numUPoints; uIndex += 1)
        {
            newRow = append(newRow, controlPoints[uIndex][vIndex]);
        }
        transposedControlPoints = append(transposedControlPoints, newRow);
    }
    return transposedControlPoints;
}


/**
 * Flips the U direction of a weights matrix.
 * 
 * @param weights {array} : Weights matrix (2D array)
 * @returns {array} : Weights matrix with U direction reversed
 */
function flipWeightsU(weights is array) returns array
{
    var flippedWeights = [];
    for (var uIndex = size(weights) - 1; uIndex >= 0; uIndex -= 1)
    {
        flippedWeights = append(flippedWeights, weights[uIndex]);
    }
    return flippedWeights;
}


/**
 * Flips the V direction of a weights matrix.
 * 
 * @param weights {array} : Weights matrix (2D array)
 * @returns {array} : Weights matrix with V direction reversed
 */
function flipWeightsV(weights is array) returns array
{
    var flippedWeights = [];
    for (var uIndex = 0; uIndex < size(weights); uIndex += 1)
    {
        var flippedRow = [];
        for (var vIndex = size(weights[uIndex]) - 1; vIndex >= 0; vIndex -= 1)
        {
            flippedRow = append(flippedRow, weights[uIndex][vIndex]);
        }
        flippedWeights = append(flippedWeights, flippedRow);
    }
    return flippedWeights;
}


/**
 * Transposes (swaps U and V) a weights matrix.
 * 
 * @param weights {array} : Weights matrix (2D array)
 * @returns {array} : Weights matrix with U and V swapped
 */
function transposeWeights(weights is array) returns array
{
    const numUPoints = size(weights);
    const numVPoints = size(weights[0]);
    
    var transposedWeights = [];
    for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
    {
        var newRow = [];
        for (var uIndex = 0; uIndex < numUPoints; uIndex += 1)
        {
            newRow = append(newRow, weights[uIndex][vIndex]);
        }
        transposedWeights = append(transposedWeights, newRow);
    }
    return transposedWeights;
}


/**
 * Finds preliminary alignment between two surfaces using corner points only.
 * 
 * This function works with surfaces of any dimensions by comparing only the four corner
 * control points. It tests all 8 possible alignments and returns the transformation that
 * minimizes the sum of squared distances between corresponding corners.
 * 
 * This is used as a first pass to determine the correct parametric orientation before
 * performing degree elevation and control point refinement.
 * 
 * Optimized for performance: uses pre-computed index mappings to avoid conditional logic
 * and redundant distance calculations.
 * 
 * @param controlPoints1 {array} : Control point matrix for first surface (reference)
 * @param controlPoints2 {array} : Control point matrix for second surface (to be aligned)
 * @returns {map} : Map with fields: flipU {boolean}, flipV {boolean}, swapUV {boolean}, distance {ValueWithUnits}
 */
function findPreliminaryAlignment(controlPoints1 is array, controlPoints2 is array) returns map
{
    const numU1 = size(controlPoints1);
    const numV1 = size(controlPoints1[0]);
    const numU2 = size(controlPoints2);
    const numV2 = size(controlPoints2[0]);
    
    // Extract corner points from first surface
    const c1 = [
        controlPoints1[0][0],           // [0] = corner (0,0)
        controlPoints1[0][numV1 - 1],   // [1] = corner (0,V)
        controlPoints1[numU1 - 1][0],   // [2] = corner (U,0)
        controlPoints1[numU1 - 1][numV1 - 1]  // [3] = corner (U,V)
    ];
    
    // Extract corner points from second surface
    const c2 = [
        controlPoints2[0][0],           // [0] = corner (0,0)
        controlPoints2[0][numV2 - 1],   // [1] = corner (0,V)
        controlPoints2[numU2 - 1][0],   // [2] = corner (U,0)
        controlPoints2[numU2 - 1][numV2 - 1]  // [3] = corner (U,V)
    ];
    
    // Pre-computed corner mapping for all 8 transformations
    // Each entry maps: [c1[0]->c2[?], c1[1]->c2[?], c1[2]->c2[?], c1[3]->c2[?]]
    // Format: [[mapping], flipU, flipV, swapUV]
    const transformations = [
        [[0, 1, 2, 3], false, false, false],  // No transformation
        [[2, 3, 0, 1], true,  false, false],  // U-flip
        [[1, 0, 3, 2], false, true,  false],  // V-flip
        [[3, 2, 1, 0], true,  true,  false],  // UV-flip
        [[0, 2, 1, 3], false, false, true],   // UV-swap
        [[1, 3, 0, 2], true,  false, true],   // UV-swap + U-flip
        [[2, 0, 3, 1], false, true,  true],   // UV-swap + V-flip
        [[3, 1, 2, 0], true,  true,  true]    // UV-swap + UV-flip
    ];
    
    var bestDistance = 1e30 * meter * meter;
    var bestConfig = 0;
    
    // Test all 8 transformations
    for (var transformationIndex = 0; transformationIndex < 8; transformationIndex += 1)
    {
        const mapping = transformations[transformationIndex][0];
        
        // Compute total squared distance for this transformation
        // Unrolled loop for performance
        const d0 = c1[0] - c2[mapping[0]];
        const d1 = c1[1] - c2[mapping[1]];
        const d2 = c1[2] - c2[mapping[2]];
        const d3 = c1[3] - c2[mapping[3]];
        
        const distance = squaredNorm(d0) + squaredNorm(d1) + squaredNorm(d2) + squaredNorm(d3);
        
        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestConfig = transformationIndex;
        }
    }
    
    return {
        "flipU" : transformations[bestConfig][1],
        "flipV" : transformations[bestConfig][2],
        "swapUV" : transformations[bestConfig][3],
        "distance" : bestDistance
    };
}


/**
 * Finds the best alignment between two surfaces by testing different transformations.
 * 
 * Tests all 8 possible alignments (including UV swap):
 * 1. Normal (no transformation)
 * 2. U-flipped
 * 3. V-flipped
 * 4. U and V flipped
 * 5. UV-swapped
 * 6. UV-swapped + U-flipped (in swapped space)
 * 7. UV-swapped + V-flipped (in swapped space)
 * 8. UV-swapped + both flipped
 * 
 * Returns the transformation that minimizes the sum of squared distances between
 * corresponding control points.
 * 
 * This function requires that both surfaces have the same dimensions (after any UV swap).
 * For preliminary alignment with mismatched dimensions, use findPreliminaryAlignment.
 * 
 * @param controlPoints1 {array} : Control point matrix for first surface (reference)
 * @param controlPoints2 {array} : Control point matrix for second surface (to be aligned)
 * @param weights2 {array|undefined} : Weights matrix for second surface (undefined if non-rational)
 * @returns {map} : Map with fields: flipU {boolean}, flipV {boolean}, swapUV {boolean}, distance {ValueWithUnits}
 */
function findBestSurfaceAlignment(controlPoints1 is array, controlPoints2 is array, weights2) returns map
{
    const numU1 = size(controlPoints1);
    const numV1 = size(controlPoints1[0]);
    const numU2 = size(controlPoints2);
    const numV2 = size(controlPoints2[0]);
    
    var bestDistance = 1e30 * meter * meter;
    var bestFlipU = false;
    var bestFlipV = false;
    var bestSwapUV = false;
    
    // Test all 8 possible transformations
    // We need to test both with and without UV swap, and for each, test the 4 flip combinations
    
    for (var testSwapUV = 0; testSwapUV < 2; testSwapUV += 1)
    {
        const swapUV = (testSwapUV == 1);
        
        // Check if dimensions match after swap
        var transformedNumU = swapUV ? numV2 : numU2;
        var transformedNumV = swapUV ? numU2 : numV2;
        
        // Skip this swap configuration if dimensions don't match
        if (transformedNumU != numU1 || transformedNumV != numV1)
        {
            continue;
        }
        
        // Get the base transformed control points
        var baseTransformedCP = swapUV ? transposeControlPoints(controlPoints2) : controlPoints2;
        
        // Test 4 flip combinations
        for (var testFlipU = 0; testFlipU < 2; testFlipU += 1)
        {
            for (var testFlipV = 0; testFlipV < 2; testFlipV += 1)
            {
                const flipU = (testFlipU == 1);
                const flipV = (testFlipV == 1);
                
                // Apply flips
                var transformedCP = baseTransformedCP;
                if (flipU)
                {
                    transformedCP = flipControlPointsU(transformedCP);
                }
                if (flipV)
                {
                    transformedCP = flipControlPointsV(transformedCP);
                }
                
                // Compute distance
                const distance = surfaceControlPointDistanceSquared(controlPoints1, transformedCP);
                
                // Update best if this is better
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestFlipU = flipU;
                    bestFlipV = flipV;
                    bestSwapUV = swapUV;
                }
            }
        }
    }
    
    return {
        "flipU" : bestFlipU,
        "flipV" : bestFlipV,
        "swapUV" : bestSwapUV,
        "distance" : bestDistance
    };
}


/**
 * Applies alignment transformation to a B-spline surface.
 * 
 * This transforms the control points, weights, and knot vectors according to the
 * specified flips and swap operations to align the surface with a reference surface.
 *
 * RAW KERNEL FORMS ONLY. The flip's `1 - knot` reflection assumes a [0, 1] domain and is exact
 * for the clamped and closed-clamped forms evApproximateBSplineSurface returns; on a NORMALIZED
 * wrap-form periodic direction it leaves the reflected seam multiplicity run split across the
 * domain boundary (a noncanonical form the sharing machinery rejects — the live cone-to-cylinder
 * failure). For normalized surfaces use the module's reverseSurfaceDirection instead, as the
 * post-net-search reversal above now does. The UV swap half is form-agnostic (a pure transpose).
 *
 * @param surface {map} : B-spline surface definition with controlPoints, weights, knots, etc.
 * @param flipU {boolean} : Whether to flip the U direction
 * @param flipV {boolean} : Whether to flip the V direction
 * @param swapUV {boolean} : Whether to swap U and V directions
 * @returns {map} : Transformed surface definition
 */
function applyAlignmentTransform(surface is map, flipU is boolean, flipV is boolean, swapUV is boolean) returns map
{
    var controlPoints = surface.controlPoints;
    var weights = surface.weights;
    var uKnots = surface.uKnots;
    var vKnots = surface.vKnots;
    var uDegree = surface.uDegree;
    var vDegree = surface.vDegree;
    var isUPeriodic = surface.isUPeriodic;
    var isVPeriodic = surface.isVPeriodic;
    
    // Apply UV swap first if needed
    if (swapUV)
    {
        controlPoints = transposeControlPoints(controlPoints);
        if (weights != undefined)
        {
            weights = transposeWeights(weights);
        }
        
        // Swap knot vectors
        const tempKnots = uKnots;
        uKnots = vKnots;
        vKnots = tempKnots;
        
        // Swap degrees
        const tempDegree = uDegree;
        uDegree = vDegree;
        vDegree = tempDegree;
        
        // Swap periodicity
        const tempPeriodic = isUPeriodic;
        isUPeriodic = isVPeriodic;
        isVPeriodic = tempPeriodic;
    }
    
    // Apply U flip if needed
    if (flipU)
    {
        controlPoints = flipControlPointsU(controlPoints);
        if (weights != undefined)
        {
            weights = flipWeightsU(weights);
        }
        // Reverse U knot vector: new_knot[i] = 1 - old_knot[n-1-i]
        var reversedUKnots = [];
        for (var i = size(uKnots) - 1; i >= 0; i -= 1)
        {
            reversedUKnots = append(reversedUKnots, 1.0 - uKnots[i]);
        }
        uKnots = knotArray(reversedUKnots);
    }
    
    // Apply V flip if needed
    if (flipV)
    {
        controlPoints = flipControlPointsV(controlPoints);
        if (weights != undefined)
        {
            weights = flipWeightsV(weights);
        }
        // Reverse V knot vector: new_knot[i] = 1 - old_knot[n-1-i]
        var reversedVKnots = [];
        for (var i = size(vKnots) - 1; i >= 0; i -= 1)
        {
            reversedVKnots = append(reversedVKnots, 1.0 - vKnots[i]);
        }
        vKnots = knotArray(reversedVKnots);
    }
    
    return {
        "uDegree" : uDegree,
        "vDegree" : vDegree,
        "isRational" : surface.isRational,
        "isUPeriodic" : isUPeriodic,
        "isVPeriodic" : isVPeriodic,
        "controlPoints" : controlPoints,
        "weights" : weights,
        "uKnots" : uKnots,
        "vKnots" : vKnots
    };
}
