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

// splineRefinementUtils.fs — exact B-spline refinement, degree elevation, and periodic-preserving
// knot sharing. NOTE: this version id must be bumped to the module's latest publish whenever the
// module changes; unlike splineRefinementTester.fs, this feature lives in a DIFFERENT document, so
// it pins whatever version it names rather than following the module's source.
import(path : "eca0e7b6ed29c5239f39f868/5af0a6517c6c2a36b76de6b2/9a2b77793cdc37bace6d915a", version : "4ec63fc1540150aa04e32934");


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
    }

    // === ALIGNMENT ===
    // Which way the second surface's UV grid lays against the first is a genuine degree of
    // freedom - there is no canonical correspondence between two surfaces - so this picks one by
    // corner distance and applies it EXACTLY, as a flip/transpose of the control grid paired with
    // the matching reversal of the knot vectors (applyAlignmentTransform). It must happen BEFORE
    // compatibility: flipping a control grid after the two surfaces share a knot vector, without
    // reversing that vector too, silently distorts the surface unless the vector is symmetric.
    const preliminaryAlignmentResult = findPreliminaryAlignment(firstSurface.controlPoints, secondSurface.controlPoints);
    if (definition.diagnosticSurfaceAlignment)
    {
        println("DEBUG: Preliminary alignment - flipU: " ~ preliminaryAlignmentResult.flipU ~
                ", flipV: " ~ preliminaryAlignmentResult.flipV ~
                ", swapUV: " ~ preliminaryAlignmentResult.swapUV ~
                ", corner distance: " ~ preliminaryAlignmentResult.distance);
    }
    if (preliminaryAlignmentResult.flipU || preliminaryAlignmentResult.flipV || preliminaryAlignmentResult.swapUV)
    {
        secondSurface = applyAlignmentTransform(secondSurface, preliminaryAlignmentResult.flipU,
                preliminaryAlignmentResult.flipV, preliminaryAlignmentResult.swapUV);
        if (definition.diagnosticSurfaceAlignment)
        {
            println("DEBUG: After alignment, second surface - uDegree=" ~ secondSurface.uDegree ~
                    ", vDegree=" ~ secondSurface.vDegree ~
                    ", controlPoints=" ~ size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
        }
    }

    // === EXACT COMPATIBILITY (spec sections 2.4 and 3.1) ===
    // One call replaces the whole elevate-then-refine-then-hope pipeline this feature used to
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
    const compatible = makeSurfacesCompatible(firstSurface, secondSurface);
    const first = compatible.a;
    const second = compatible.b;

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
    // (see above), and not unpadded first. bSplineSurface takes a full padded knot array as-is
    // whenever it is correctly sized; handing it a truncated one instead just makes it
    // reconstruct padding, which for a periodic direction throws away the very structure that
    // makes the direction periodic.
    const tweenedSurfaceDefinition = bSplineSurface({
                "uDegree" : first.uDegree,
                "vDegree" : first.vDegree,
                "isUPeriodic" : first.isUPeriodic == true,
                "isVPeriodic" : first.isVPeriodic == true,
                "controlPoints" : controlPointMatrix(tweenedControlPoints),
                "weights" : matrix(tweenedWeights),
                "uKnots" : first.uKnots,
                "vKnots" : first.vKnots
            });

    opCreateBSplineSurface(context, id, {
                "bSplineSurface" : tweenedSurfaceDefinition
            });
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
