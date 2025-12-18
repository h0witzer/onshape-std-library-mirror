FeatureScript 2837;

/**
 * Surface Tween Feature
 * 
 * This feature creates a median (tweened) surface between two input surfaces.
 * It is inspired by the Parasolid PK_neutral_method_medial_c function which creates
 * a neutral sheet that is an "average" mid-surface between two faces.
 * 
 * The implementation works with B-spline surface representations directly:
 * 1. Obtains B-spline surface definitions for both input faces (using approximation if needed)
 * 2. Detects and corrects for misaligned parametric directions (U/V flips and swaps) using corner points
 * 3. Elevates surface degrees to match if necessary (supports both non-rational and rational surfaces)
 * 4. Refines control point counts to match if necessary (supports both non-rational and rational surfaces)
 * 5. Interpolates control points of the two B-spline surfaces
 * 6. Creates a new B-spline surface with the interpolated control points
 * 
 * Key Features:
 * - Automatic alignment matching: Tests 8 possible transformations (U-flip, V-flip, UV-swap, 
 *   and combinations) to find the best correspondence between surfaces
 * - Full NURBS support: Handles both non-rational and rational (NURBS) surfaces with proper
 *   homogeneous coordinate mathematics for degree elevation, control point refinement, and interpolation
 * - Exact endpoint preservation: At fraction=0, produces exactly the first surface; at fraction=1, 
 *   produces exactly the second surface
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
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/context.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/splineUtils.fs", version : "2837.0");
import(path : "onshape/std/nurbsUtils.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");


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
                
                annotation { "Name" : "Curve refinement details" }
                definition.diagnosticCurveRefinement is boolean;
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
        diagnosticKnotVectorProcessing : false,
        diagnosticCurveRefinement : false
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
                ", controlPoints=" ~ size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]));
        println("DEBUG: Initial second surface - uDegree=" ~ secondSurface.uDegree ~ ", vDegree=" ~ secondSurface.vDegree ~ 
                ", controlPoints=" ~ size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
    }
    
    // === PRELIMINARY ALIGNMENT MATCHING ===
    // Find the best alignment between surfaces using corner points before degree elevation and refinement.
    // This ensures we work in the correct parametric space from the start, avoiding unnecessary computation.
    if (definition.diagnosticSurfaceAlignment)
    {
        println("DEBUG: Checking preliminary surface alignment using corner points...");
    }
    const preliminaryAlignmentResult = findPreliminaryAlignment(firstSurface.controlPoints, secondSurface.controlPoints);
    if (definition.diagnosticSurfaceAlignment)
    {
        println("DEBUG: Preliminary alignment - flipU: " ~ preliminaryAlignmentResult.flipU ~ 
                ", flipV: " ~ preliminaryAlignmentResult.flipV ~ 
                ", swapUV: " ~ preliminaryAlignmentResult.swapUV ~ 
                ", corner distance: " ~ preliminaryAlignmentResult.distance);
    }
    
    // Apply the preliminary alignment transformation to the second surface
    if (preliminaryAlignmentResult.flipU || preliminaryAlignmentResult.flipV || preliminaryAlignmentResult.swapUV)
    {
        secondSurface = applyAlignmentTransform(secondSurface, preliminaryAlignmentResult.flipU, 
                                                 preliminaryAlignmentResult.flipV, preliminaryAlignmentResult.swapUV);
        if (definition.diagnosticSurfaceAlignment)
        {
            println("DEBUG: Applied preliminary alignment transform to second surface");
            println("DEBUG: After preliminary alignment, second surface - uDegree=" ~ secondSurface.uDegree ~ 
                    ", vDegree=" ~ secondSurface.vDegree ~ 
                    ", controlPoints=" ~ size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
        }
    }
    
    // Elevate degrees to match if necessary
    if (firstSurface.uDegree != secondSurface.uDegree || firstSurface.vDegree != secondSurface.vDegree)
    {
        const targetUDegree = max(firstSurface.uDegree, secondSurface.uDegree);
        const targetVDegree = max(firstSurface.vDegree, secondSurface.vDegree);
        
        // Check if either surface has multi-segment B-splines
        const firstIsMultiSegmentU = !isSingleSegmentBezierCurve(firstSurface.uDegree, size(firstSurface.controlPoints));
        const firstIsMultiSegmentV = !isSingleSegmentBezierCurve(firstSurface.vDegree, size(firstSurface.controlPoints[0]));
        const secondIsMultiSegmentU = !isSingleSegmentBezierCurve(secondSurface.uDegree, size(secondSurface.controlPoints));
        const secondIsMultiSegmentV = !isSingleSegmentBezierCurve(secondSurface.vDegree, size(secondSurface.controlPoints[0]));
        
        if ((firstIsMultiSegmentU || firstIsMultiSegmentV || secondIsMultiSegmentU || secondIsMultiSegmentV) &&
            (firstSurface.uDegree != secondSurface.uDegree || firstSurface.vDegree != secondSurface.vDegree))
        {
            if (definition.diagnosticDegreeElevation)
            {
                println("INFO: Surfaces have different degrees and at least one is a multi-segment B-spline.");
                println("      Using proper B-spline degree elevation to preserve geometry.");
                println("      First surface: uDegree=" ~ firstSurface.uDegree ~ ", vDegree=" ~ firstSurface.vDegree ~
                        ", controlPoints=" ~ size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]));
                println("      Second surface: uDegree=" ~ secondSurface.uDegree ~ ", vDegree=" ~ secondSurface.vDegree ~
                        ", controlPoints=" ~ size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
            }
        }
        
        if (firstSurface.uDegree < targetUDegree || firstSurface.vDegree < targetVDegree)
        {
            if (definition.diagnosticDegreeElevation)
            {
                println("DEBUG: Elevating first surface from (" ~ firstSurface.uDegree ~ "," ~ firstSurface.vDegree ~ 
                        ") to (" ~ targetUDegree ~ "," ~ targetVDegree ~ ")");
            }
            firstSurface = elevateSurfaceDegree(firstSurface, targetUDegree, targetVDegree);
            if (definition.diagnosticDegreeElevation)
            {
                println("DEBUG: After elevation, first surface controlPoints=" ~ 
                        size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]));
            }
        }
        if (secondSurface.uDegree < targetUDegree || secondSurface.vDegree < targetVDegree)
        {
            if (definition.diagnosticDegreeElevation)
            {
                println("DEBUG: Elevating second surface from (" ~ secondSurface.uDegree ~ "," ~ secondSurface.vDegree ~ 
                        ") to (" ~ targetUDegree ~ "," ~ targetVDegree ~ ")");
            }
            secondSurface = elevateSurfaceDegree(secondSurface, targetUDegree, targetVDegree);
            if (definition.diagnosticDegreeElevation)
            {
                println("DEBUG: After elevation, second surface controlPoints=" ~ 
                        size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
            }
        }
    }
    
    // Match control point counts and knot vectors by inserting knots if necessary
    // Instead of uniformly distributing knots, we merge the knot vectors from both surfaces
    // to ensure they have identical knot vectors after refinement
    const firstControlPointsRowCount = size(firstSurface.controlPoints);
    const firstControlPointsColumnCount = size(firstSurface.controlPoints[0]);
    const secondControlPointsRowCount = size(secondSurface.controlPoints);
    const secondControlPointsColumnCount = size(secondSurface.controlPoints[0]);
    
    if (firstControlPointsRowCount != secondControlPointsRowCount || 
        firstControlPointsColumnCount != secondControlPointsColumnCount)
    {
        if (definition.diagnosticControlPointRefinement)
        {
            println("DEBUG: Control point count mismatch - first: " ~ firstControlPointsRowCount ~ "x" ~ firstControlPointsColumnCount ~ 
                    ", second: " ~ secondControlPointsRowCount ~ "x" ~ secondControlPointsColumnCount);
        }
        
        // Refine both surfaces to have matching knot vectors
        const refinementResult = refineToMatchingKnotVectors(context, firstSurface, secondSurface, definition);
        firstSurface = refinementResult.firstSurface;
        secondSurface = refinementResult.secondSurface;
        
        if (definition.diagnosticControlPointRefinement)
        {
            println("DEBUG: After knot vector matching, first surface: " ~ 
                    size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]));
            println("DEBUG: After knot vector matching, second surface: " ~ 
                    size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
        }
    }
    
    // === ALIGNMENT VERIFICATION ===
    // Verify alignment is optimal after degree elevation and refinement.
    // Since we already applied preliminary alignment before elevation/refinement, this should
    // typically show no additional transformation is needed (flipU=false, flipV=false, swapUV=false).
    if (definition.diagnosticSurfaceAlignment)
    {
        println("DEBUG: Verifying final surface alignment after elevation and refinement...");
        const verificationResult = findBestSurfaceAlignment(firstSurface.controlPoints, secondSurface.controlPoints, 
                                                           secondSurface.weights);
        println("DEBUG: Final alignment verification - flipU: " ~ verificationResult.flipU ~ 
                ", flipV: " ~ verificationResult.flipV ~ 
                ", swapUV: " ~ verificationResult.swapUV ~ 
                ", total distance: " ~ verificationResult.distance);
        
        if (verificationResult.flipU || verificationResult.flipV || verificationResult.swapUV)
        {
            println("WARNING: Final alignment verification suggests additional transformation needed.");
            println("         This may indicate the preliminary alignment was incorrect or surfaces are unusual.");
        }
    }
    
    // Verify both surfaces have the same rationality
    if (firstSurface.isRational != secondSurface.isRational)
    {
        throw regenError("Both surfaces must be either rational or non-rational. First surface is " ~ 
            (firstSurface.isRational ? "rational" : "non-rational") ~ ", second surface is " ~
            (secondSurface.isRational ? "rational" : "non-rational") ~ ".");
    }
    
    // Verify knot vectors match
    if (size(firstSurface.uKnots) != size(secondSurface.uKnots) || 
        size(firstSurface.vKnots) != size(secondSurface.vKnots))
    {
        throw regenError("Surfaces must have matching knot vector sizes. Use surfaces with compatible parameterizations.");
    }
    
    // Update control point counts after potential refinement
    const finalFirstControlPointsRowCount = size(firstSurface.controlPoints);
    const finalFirstControlPointsColumnCount = size(firstSurface.controlPoints[0]);
    
    // Interpolate control points and weights
    // For rational surfaces (NURBS), we must interpolate in homogeneous coordinates:
    // - Weighted CP = CP * weight
    // - Interpolate weighted CPs and weights separately
    // - Divide interpolated weighted CP by interpolated weight to get final CP
    var tweenedControlPoints = [];
    var tweenedWeights = undefined;
    var debugPointCount = 0;
    
    if (firstSurface.isRational)
    {
        // Rational surface interpolation (NURBS)
        tweenedWeights = [];
        for (var uIndex = 0; uIndex < finalFirstControlPointsRowCount; uIndex += 1)
        {
            var controlPointRow = [];
            var weightRow = [];
            for (var vIndex = 0; vIndex < finalFirstControlPointsColumnCount; vIndex += 1)
            {
                const firstControlPoint = firstSurface.controlPoints[uIndex][vIndex];
                const secondControlPoint = secondSurface.controlPoints[uIndex][vIndex];
                const firstWeight = firstSurface.weights[uIndex][vIndex];
                const secondWeight = secondSurface.weights[uIndex][vIndex];
                
                // Interpolate in homogeneous coordinates
                const weightedFirstCP = firstControlPoint * firstWeight;
                const weightedSecondCP = secondControlPoint * secondWeight;
                const interpolatedWeightedCP = weightedFirstCP * (1 - tweenFraction) + weightedSecondCP * tweenFraction;
                const interpolatedWeight = firstWeight * (1 - tweenFraction) + secondWeight * tweenFraction;
                
                // Convert back to Cartesian coordinates
                const tweenedControlPoint = interpolatedWeightedCP / interpolatedWeight;
                
                controlPointRow = append(controlPointRow, tweenedControlPoint);
                weightRow = append(weightRow, interpolatedWeight);
                
                // Debug visualization
                if (definition.diagnosticControlPointVisualization)
                {
                    debug(context, firstControlPoint, DebugColor.BLUE);
                    debug(context, secondControlPoint, DebugColor.RED);
                    debug(context, tweenedControlPoint, DebugColor.GREEN);
                    debugPointCount += 1;
                }
                
                // Debug logging for corner point
                if (definition.diagnosticControlPointInterpolation && uIndex == 0 && vIndex == 0)
                {
                    println("DEBUG: Corner CP interpolation (RATIONAL, fraction=" ~ tweenFraction ~ "):");
                    println("  First CP: " ~ firstControlPoint ~ ", weight: " ~ firstWeight);
                    println("  Second CP: " ~ secondControlPoint ~ ", weight: " ~ secondWeight);
                    println("  Weighted First CP: " ~ weightedFirstCP);
                    println("  Weighted Second CP: " ~ weightedSecondCP);
                    println("  Interpolated Weighted CP: " ~ interpolatedWeightedCP);
                    println("  Interpolated Weight: " ~ interpolatedWeight);
                    println("  Tweened CP: " ~ tweenedControlPoint);
                }
            }
            tweenedControlPoints = append(tweenedControlPoints, controlPointRow);
            tweenedWeights = append(tweenedWeights, weightRow);
        }
    }
    else
    {
        // Non-rational surface interpolation (simple linear interpolation)
        for (var uIndex = 0; uIndex < finalFirstControlPointsRowCount; uIndex += 1)
        {
            var controlPointRow = [];
            for (var vIndex = 0; vIndex < finalFirstControlPointsColumnCount; vIndex += 1)
            {
                const firstControlPoint = firstSurface.controlPoints[uIndex][vIndex];
                const secondControlPoint = secondSurface.controlPoints[uIndex][vIndex];
                
                // Linear interpolation: tweenedCP = (1 - fraction) * cp1 + fraction * cp2
                const tweenedControlPoint = firstControlPoint * (1 - tweenFraction) + secondControlPoint * tweenFraction;
                controlPointRow = append(controlPointRow, tweenedControlPoint);
                
                // Debug visualization
                if (definition.diagnosticControlPointVisualization)
                {
                    debug(context, firstControlPoint, DebugColor.BLUE);
                    debug(context, secondControlPoint, DebugColor.RED);
                    debug(context, tweenedControlPoint, DebugColor.GREEN);
                    debugPointCount += 1;
                }
                
                // Debug logging for corner point
                if (definition.diagnosticControlPointInterpolation && uIndex == 0 && vIndex == 0)
                {
                    println("DEBUG: Corner CP interpolation (NON-RATIONAL, fraction=" ~ tweenFraction ~ "):");
                    println("  First CP: " ~ firstControlPoint);
                    println("  Second CP: " ~ secondControlPoint);
                    println("  Tweened CP: " ~ tweenedControlPoint);
                }
            }
            tweenedControlPoints = append(tweenedControlPoints, controlPointRow);
        }
    }
    
    if (definition.diagnosticControlPointVisualization)
    {
        println("DEBUG: Drew " ~ debugPointCount ~ " sets of control points (blue/red/green)");
        println("DEBUG: Expected " ~ (finalFirstControlPointsRowCount * finalFirstControlPointsColumnCount) ~ " sets");
    }
    
    // Interpolate knot vectors
    // Even when control point counts match, knot vectors can differ, representing different parameterizations.
    // We interpolate the knot vectors to create a smooth transition in parameterization.
    const uDegree = firstSurface.uDegree;
    const vDegree = firstSurface.vDegree;
    const numUControlPoints = size(tweenedControlPoints);
    const numVControlPoints = size(tweenedControlPoints[0]);
    
    // Interpolate U knots
    var interpolatedUKnots = [];
    for (var i = 0; i < size(firstSurface.uKnots); i += 1)
    {
        const interpolatedKnot = firstSurface.uKnots[i] * (1 - tweenFraction) + secondSurface.uKnots[i] * tweenFraction;
        interpolatedUKnots = append(interpolatedUKnots, interpolatedKnot);
    }
    
    // Interpolate V knots
    var interpolatedVKnots = [];
    for (var i = 0; i < size(firstSurface.vKnots); i += 1)
    {
        const interpolatedKnot = firstSurface.vKnots[i] * (1 - tweenFraction) + secondSurface.vKnots[i] * tweenFraction;
        interpolatedVKnots = append(interpolatedVKnots, interpolatedKnot);
    }
    
    // Unpad knot arrays (bSplineSurface expects unpadded knots)
    // Padded knots have size: nControlPoints + degree + 1
    // Unpadded knots should have size: nControlPoints - degree + 1
    // So we remove the first 'degree' and last 'degree' knots
    const expectedPaddedUSize = numUControlPoints + uDegree + 1;
    const expectedPaddedVSize = numVControlPoints + vDegree + 1;
    
    // Debug logging to diagnose knot array format issues
    if (definition.diagnosticKnotVectorProcessing)
    {
        println("DEBUG: uDegree=" ~ uDegree ~ ", vDegree=" ~ vDegree);
        println("DEBUG: numUControlPoints=" ~ numUControlPoints ~ ", numVControlPoints=" ~ numVControlPoints);
        println("DEBUG: interpolatedUKnots size=" ~ size(interpolatedUKnots));
        println("DEBUG: interpolatedVKnots size=" ~ size(interpolatedVKnots));
        println("DEBUG: expectedPaddedUSize=" ~ expectedPaddedUSize);
        println("DEBUG: expectedPaddedVSize=" ~ expectedPaddedVSize);
        println("DEBUG: Expected unpadded U size=" ~ (numUControlPoints - uDegree + 1));
        println("DEBUG: Expected unpadded V size=" ~ (numVControlPoints - vDegree + 1));
    }
    
    var unpaddedUKnots = [];
    if (size(interpolatedUKnots) == expectedPaddedUSize)
    {
        // Knots are padded, unpad them
        for (var i = uDegree; i < size(interpolatedUKnots) - uDegree; i += 1)
        {
            unpaddedUKnots = append(unpaddedUKnots, interpolatedUKnots[i]);
        }
        if (definition.diagnosticKnotVectorProcessing)
        {
            println("DEBUG: Unpadded U knots from padded format, result size=" ~ size(unpaddedUKnots));
        }
    }
    else
    {
        // Knots might already be unpadded or in unexpected format, use as-is
        unpaddedUKnots = interpolatedUKnots;
        if (definition.diagnosticKnotVectorProcessing)
        {
            println("DEBUG: Using U knots as-is, size=" ~ size(unpaddedUKnots));
        }
    }
    
    var unpaddedVKnots = [];
    if (size(interpolatedVKnots) == expectedPaddedVSize)
    {
        // Knots are padded, unpad them
        for (var i = vDegree; i < size(interpolatedVKnots) - vDegree; i += 1)
        {
            unpaddedVKnots = append(unpaddedVKnots, interpolatedVKnots[i]);
        }
        if (definition.diagnosticKnotVectorProcessing)
        {
            println("DEBUG: Unpadded V knots from padded format, result size=" ~ size(unpaddedVKnots));
        }
    }
    else
    {
        // Knots might already be unpadded or in unexpected format, use as-is
        unpaddedVKnots = interpolatedVKnots;
        if (definition.diagnosticKnotVectorProcessing)
        {
            println("DEBUG: Using V knots as-is, size=" ~ size(unpaddedVKnots));
        }
    }
    
    if (definition.diagnosticKnotVectorProcessing)
    {
        println("DEBUG: Final unpaddedUKnots size=" ~ size(unpaddedUKnots));
        println("DEBUG: Final unpaddedVKnots size=" ~ size(unpaddedVKnots));
        
        // Debug: Print knot values to check they're valid (handle small arrays)
        if (size(unpaddedUKnots) > 0)
        {
            print("DEBUG: U knots: ");
            for (var i = 0; i < size(unpaddedUKnots); i += 1)
            {
                print(unpaddedUKnots[i]);
                if (i < size(unpaddedUKnots) - 1)
                    print(", ");
            }
            println("");
        }
        if (size(unpaddedVKnots) > 0)
        {
            print("DEBUG: V knots: ");
            for (var i = 0; i < size(unpaddedVKnots); i += 1)
            {
                print(unpaddedVKnots[i]);
                if (i < size(unpaddedVKnots) - 1)
                    print(", ");
            }
            println("");
        }
    }
    
    // Create the tweened B-spline surface
    const tweenedSurfaceDefinition = bSplineSurface({
        "uDegree" : uDegree,
        "vDegree" : vDegree,
        "isUPeriodic" : firstSurface.isUPeriodic,
        "isVPeriodic" : firstSurface.isVPeriodic,
        "controlPoints" : controlPointMatrix(tweenedControlPoints),
        "weights" : tweenedWeights == undefined ? undefined : matrix(tweenedWeights),
        "uKnots" : knotArray(unpaddedUKnots),
        "vKnots" : knotArray(unpaddedVKnots)
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
 * Checks if a B-spline curve is a single-segment Bezier curve.
 * 
 * A Bezier curve of degree p has exactly p+1 control points and no internal knots.
 * 
 * @param degree {number} : The degree of the B-spline curve
 * @param numControlPoints {number} : The number of control points
 * @returns {boolean} : True if this is a single-segment Bezier curve
 */
function isSingleSegmentBezierCurve(degree is number, numControlPoints is number) returns boolean
{
    return numControlPoints == degree + 1;
}


/**
 * Elevates the degree of a B-spline surface in U and/or V directions.
 * 
 * This implements proper B-spline degree elevation that preserves surface geometry exactly.
 * For tensor product B-spline surfaces, degree elevation is performed independently in each
 * parametric direction by processing each isoparametric curve:
 * 
 * - For single-segment B-splines (Bezier curves): Uses fast Bezier degree elevation
 * - For multi-segment B-splines: Subdivides into Bezier segments, elevates each segment,
 *   then recombines with proper knot vector handling
 * 
 * The algorithm automatically detects whether each isoparametric curve is single-segment
 * or multi-segment and applies the appropriate elevation method.
 * 
 * For rational surfaces (NURBS), the algorithm works in homogeneous coordinates by combining
 * control points with their weights before elevation, then separating them afterward.
 * 
 * @param surface {map} : The B-spline surface to elevate (rational or non-rational)
 * @param targetUDegree {number} : The desired degree in U direction (must be >= current U degree)
 * @param targetVDegree {number} : The desired degree in V direction (must be >= current V degree)
 * @returns {map} : The surface with elevated degrees, preserving geometry exactly
 */
function elevateSurfaceDegree(surface is map, targetUDegree is number, targetVDegree is number)
{
    var controlPoints = surface.controlPoints;
    var weights = surface.weights;
    var uDegree = surface.uDegree;
    var vDegree = surface.vDegree;
    const isRational = surface.isRational;
    // Ensure knots are KnotArray type
    var uKnots = surface.uKnots is KnotArray ? surface.uKnots : knotArray(surface.uKnots);
    var vKnots = surface.vKnots is KnotArray ? surface.vKnots : knotArray(surface.vKnots);
    
    // Elevate U degree if needed (process each V-column as an independent curve)
    if (uDegree < targetUDegree)
    {
        const numVPoints = size(controlPoints[0]);
        var newControlPoints = [];
        var newWeights = isRational ? [] : undefined;
        
        var newUKnots = undefined;
        
        for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
        {
            // Extract column of control points
            var columnPoints = [];
            var columnWeights = [];
            for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
            {
                columnPoints = append(columnPoints, controlPoints[uIndex][vIndex]);
                if (isRational)
                {
                    columnWeights = append(columnWeights, weights[uIndex][vIndex]);
                }
            }
            
            // For rational surfaces, work in homogeneous coordinates
            var workingPoints = isRational ? combinePointsAndWeights(columnPoints, columnWeights) : columnPoints;
            
            // Elevate this column curve using proper B-spline degree elevation
            // This handles both single-segment (Bezier) and multi-segment B-splines correctly
            var elevatedPoints;
            if (isSingleSegmentBezierCurve(uDegree, size(workingPoints)))
            {
                // For single-segment B-splines (Bezier curves), use fast Bezier elevation
                elevatedPoints = elevateBezierDegree(workingPoints, targetUDegree);
            }
            else
            {
                // For multi-segment B-splines, use proper B-spline elevation
                const elevationResult = elevateBSplineCurve(workingPoints, uKnots, uDegree, targetUDegree);
                elevatedPoints = elevationResult.controlPoints;
                // Update knot vector from first column (all columns should produce same knots)
                if (vIndex == 0)
                {
                    newUKnots = elevationResult.knots;
                }
            }
            
            // For rational surfaces, separate back to control points and weights
            var elevatedControlPoints;
            var elevatedWeights;
            if (isRational)
            {
                const separated = separatePointsAndWeights(elevatedPoints);
                elevatedControlPoints = separated.points;
                elevatedWeights = separated.weights;
            }
            else
            {
                elevatedControlPoints = elevatedPoints;
            }
            
            // Store elevated control points back (transpose)
            for (var uIndex = 0; uIndex < size(elevatedControlPoints); uIndex += 1)
            {
                if (vIndex == 0)
                {
                    newControlPoints = append(newControlPoints, []);
                    if (isRational)
                    {
                        newWeights = append(newWeights, []);
                    }
                }
                newControlPoints[uIndex] = append(newControlPoints[uIndex], elevatedControlPoints[uIndex]);
                if (isRational)
                {
                    newWeights[uIndex] = append(newWeights[uIndex], elevatedWeights[uIndex]);
                }
            }
        }
        
        controlPoints = newControlPoints;
        weights = newWeights;
        uDegree = targetUDegree;
        // Update U knot vector
        if (newUKnots != undefined)
        {
            uKnots = knotArray(newUKnots);
        }
        else
        {
            // For Bezier curves, create new uniform knot vector
            uKnots = makeUniformKnotVector(targetUDegree, size(controlPoints));
        }
    }
    
    // Elevate V degree if needed (process each U-row as an independent curve)
    if (vDegree < targetVDegree)
    {
        var newControlPoints = [];
        var newWeights = isRational ? [] : undefined;
        
        var newVKnots = undefined;
        
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            // Extract row of control points and weights
            const rowPoints = controlPoints[uIndex];
            const rowWeights = isRational ? weights[uIndex] : undefined;
            
            // For rational surfaces, work in homogeneous coordinates
            var workingPoints = isRational ? combinePointsAndWeights(rowPoints, rowWeights) : rowPoints;
            
            // Elevate this row curve using proper B-spline degree elevation
            // This handles both single-segment (Bezier) and multi-segment B-splines correctly
            var elevatedPoints;
            if (isSingleSegmentBezierCurve(vDegree, size(workingPoints)))
            {
                // For single-segment B-splines (Bezier curves), use fast Bezier elevation
                elevatedPoints = elevateBezierDegree(workingPoints, targetVDegree);
            }
            else
            {
                // For multi-segment B-splines, use proper B-spline elevation
                const elevationResult = elevateBSplineCurve(workingPoints, vKnots, vDegree, targetVDegree);
                elevatedPoints = elevationResult.controlPoints;
                // Update knot vector from first row (all rows should produce same knots)
                if (uIndex == 0)
                {
                    newVKnots = elevationResult.knots;
                }
            }
            
            // For rational surfaces, separate back to control points and weights
            if (isRational)
            {
                const separated = separatePointsAndWeights(elevatedPoints);
                newControlPoints = append(newControlPoints, separated.points);
                newWeights = append(newWeights, separated.weights);
            }
            else
            {
                newControlPoints = append(newControlPoints, elevatedPoints);
            }
        }
        
        controlPoints = newControlPoints;
        weights = newWeights;
        vDegree = targetVDegree;
        // Update V knot vector
        if (newVKnots != undefined)
        {
            vKnots = knotArray(newVKnots);
        }
        else
        {
            // For Bezier curves, create new uniform knot vector
            vKnots = makeUniformKnotVector(targetVDegree, size(controlPoints[0]));
        }
    }
    
    return {
        "uDegree" : uDegree,
        "vDegree" : vDegree,
        "isRational" : isRational,
        "isUPeriodic" : surface.isUPeriodic,
        "isVPeriodic" : surface.isVPeriodic,
        "controlPoints" : controlPoints,
        "weights" : weights,
        "uKnots" : uKnots,
        "vKnots" : vKnots
    };
}


/**
 * Creates a uniform knot vector for a B-spline with given degree and number of control points.
 * 
 * For a B-spline of degree p with n control points, the knot vector has n + p + 1 knots.
 * This function creates a clamped uniform knot vector with multiplicity p+1 at both ends.
 * 
 * @param degree {number} : The degree of the B-spline
 * @param numControlPoints {number} : The number of control points
 * @returns {array} : Uniform knot vector
 */
function makeUniformKnotVector(degree is number, numControlPoints is number)
{
    var knots = [];
    
    // Total number of knots = numControlPoints + degree + 1
    const totalKnots = numControlPoints + degree + 1;
    
    // Start with degree+1 zeros (clamped at start)
    for (var i = 0; i <= degree; i += 1)
    {
        knots = append(knots, 0.0);
    }
    
    // Internal knots (if any)
    const numInternalKnots = totalKnots - 2 * (degree + 1);
    if (numInternalKnots > 0)
    {
        for (var i = 1; i <= numInternalKnots; i += 1)
        {
            knots = append(knots, i / (numInternalKnots + 1.0));
        }
    }
    
    // End with degree+1 ones (clamped at end)
    for (var i = 0; i <= degree; i += 1)
    {
        knots = append(knots, 1.0);
    }
    
    return knots;
}


/**
 * Refines a B-spline surface to have a target number of control points in each direction.
 * 
 * This uses the mathematically correct knot insertion (Boehm algorithm) to add control points
 * while preserving surface geometry exactly. The algorithm:
 * 1. Processes each isoparametric curve (U-column or V-row) independently
 * 2. Inserts knots uniformly distributed in the parameter space
 * 3. Computes new control points using the Boehm knot insertion formula
 * 4. Reconstructs the surface with the new control point grid
 * 
 * For rational surfaces (NURBS), the algorithm works in homogeneous coordinates to ensure
 * mathematically correct results.
 * 
 * Note: This preserves surface geometry exactly using proper B-spline calculus.
 * 
 * @param context {Context} : The modeling context
 * @param surface {map} : The B-spline surface to refine (rational or non-rational)
 * @param targetUCount {number} : Target number of control points in U direction
 * @param targetVCount {number} : Target number of control points in V direction
 * @param definition {map} : The feature definition including diagnostics settings
 * @returns {map} : Refined surface with target control point counts
 */
function refineControlPointCount(context is Context, surface is map, targetUCount is number, targetVCount is number, definition is map)
{
    var controlPoints = surface.controlPoints;
    var weights = surface.weights;
    const isRational = surface.isRational;
    var uDegree = surface.uDegree;
    var vDegree = surface.vDegree;
    // Ensure knots are KnotArray type
    var uKnots = surface.uKnots is KnotArray ? surface.uKnots : knotArray(surface.uKnots);
    var vKnots = surface.vKnots is KnotArray ? surface.vKnots : knotArray(surface.vKnots);
    
    // Refine in U direction if needed
    if (size(controlPoints) < targetUCount)
    {
        // Process each V-column to add U control points
        const numVPoints = size(controlPoints[0]);
        var newControlPoints = [];
        var newWeights = isRational ? [] : undefined;
        var newUKnots = undefined;
        
        for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
        {
            // Extract column of control points and weights
            var columnPoints = [];
            var columnWeights = [];
            for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
            {
                columnPoints = append(columnPoints, controlPoints[uIndex][vIndex]);
                if (isRational)
                {
                    columnWeights = append(columnWeights, weights[uIndex][vIndex]);
                }
            }
            
            // Create a B-spline curve from this column
            const columnCurve = bSplineCurve({
                "degree" : uDegree,
                "controlPoints" : columnPoints,
                "knots" : uKnots,
                "isPeriodic" : surface.isUPeriodic,
                "isRational" : isRational,
                "weights" : isRational ? columnWeights : undefined
            });
            
            // Refine this curve to have targetUCount control points
            if (definition.diagnosticCurveRefinement)
            {
                println("DEBUG: Refining U curve - before: " ~ size(columnCurve.controlPoints) ~ " CP, after target: " ~ targetUCount);
                println("DEBUG: Curve knots before refinement: " ~ columnCurve.knots);
            }
            const refinedCurve = refineCurveControlPointCount(context, columnCurve, targetUCount);
            if (definition.diagnosticCurveRefinement)
            {
                println("DEBUG: After refinement: " ~ size(refinedCurve.controlPoints) ~ " CP");
                println("DEBUG: Refined knots: " ~ refinedCurve.knots);
                if (vIndex == 0)
                {
                    println("DEBUG: First column refined control points:");
                    for (var i = 0; i < size(refinedCurve.controlPoints); i += 1)
                    {
                        println("  [" ~ i ~ "]: " ~ refinedCurve.controlPoints[i]);
                    }
                }
            }
            
            // Store refined control points (transpose) and update knots from first column
            if (vIndex == 0)
            {
                newUKnots = refinedCurve.knots;
            }
            for (var uIndex = 0; uIndex < size(refinedCurve.controlPoints); uIndex += 1)
            {
                if (vIndex == 0)
                {
                    newControlPoints = append(newControlPoints, []);
                    if (isRational)
                    {
                        newWeights = append(newWeights, []);
                    }
                }
                newControlPoints[uIndex] = append(newControlPoints[uIndex], refinedCurve.controlPoints[uIndex]);
                if (isRational)
                {
                    newWeights[uIndex] = append(newWeights[uIndex], refinedCurve.weights[uIndex]);
                }
            }
        }
        
        controlPoints = newControlPoints;
        weights = newWeights;
        if (newUKnots != undefined)
        {
            uKnots = newUKnots;
        }
    }
    
    // Refine in V direction if needed
    if (size(controlPoints[0]) < targetVCount)
    {
        // Process each U-row to add V control points
        var newControlPoints = [];
        var newWeights = isRational ? [] : undefined;
        var newVKnots = undefined;
        
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            // Extract row of control points and weights
            const rowPoints = controlPoints[uIndex];
            const rowWeights = isRational ? weights[uIndex] : undefined;
            
            // Create a B-spline curve from this row
            const rowCurve = bSplineCurve({
                "degree" : vDegree,
                "controlPoints" : rowPoints,
                "knots" : vKnots,
                "isPeriodic" : surface.isVPeriodic,
                "isRational" : isRational,
                "weights" : rowWeights
            });
            
            // Refine this curve to have targetVCount control points
            const refinedCurve = refineCurveControlPointCount(context, rowCurve, targetVCount);
            
            // Store refined control points and update knots from first row
            if (uIndex == 0)
            {
                newVKnots = refinedCurve.knots;
            }
            newControlPoints = append(newControlPoints, refinedCurve.controlPoints);
            if (isRational)
            {
                newWeights = append(newWeights, refinedCurve.weights);
            }
        }
        
        controlPoints = newControlPoints;
        weights = newWeights;
        if (newVKnots != undefined)
        {
            vKnots = newVKnots;
        }
    }
    
    return {
        "uDegree" : uDegree,
        "vDegree" : vDegree,
        "isRational" : isRational,
        "isUPeriodic" : surface.isUPeriodic,
        "isVPeriodic" : surface.isVPeriodic,
        "controlPoints" : controlPoints,
        "weights" : weights,
        "uKnots" : uKnots,
        "vKnots" : vKnots
    };
}


/**
 * Refines two B-spline surfaces to have matching knot vectors by inserting missing knots.
 * 
 * This function merges the knot vectors from both surfaces by:
 * 1. Finding knots that exist in surface1 but not in surface2, and inserting them into surface2
 * 2. Finding knots that exist in surface2 but not in surface1, and inserting them into surface1
 * 3. After refinement, both surfaces have identical knot vectors and control point counts
 * 
 * This ensures that at tweenFraction=0, the result exactly matches surface1, and at
 * tweenFraction=1, the result exactly matches surface2, because no new knots are introduced.
 * 
 * @param context {Context} : The modeling context
 * @param firstSurface {map} : The first B-spline surface
 * @param secondSurface {map} : The second B-spline surface
 * @param definition {map} : The feature definition including diagnostics settings
 * @returns {map} : Map with "firstSurface" and "secondSurface" keys, both with matching knot vectors
 */
function refineToMatchingKnotVectors(context is Context, firstSurface is map, secondSurface is map, definition is map)
{
    // Process U and V directions independently
    
    // U direction: Merge knot vectors
    const knotsToInsertInFirst_U = findMissingKnots(secondSurface.uKnots, firstSurface.uKnots, firstSurface.uDegree);
    const knotsToInsertInSecond_U = findMissingKnots(firstSurface.uKnots, secondSurface.uKnots, secondSurface.uDegree);
    
    // V direction: Merge knot vectors
    const knotsToInsertInFirst_V = findMissingKnots(secondSurface.vKnots, firstSurface.vKnots, firstSurface.vDegree);
    const knotsToInsertInSecond_V = findMissingKnots(firstSurface.vKnots, secondSurface.vKnots, secondSurface.vDegree);
    
    if (definition.diagnosticControlPointRefinement)
    {
        println("DEBUG: Knots to insert in first surface - U: " ~ size(knotsToInsertInFirst_U) ~ ", V: " ~ size(knotsToInsertInFirst_V));
        println("DEBUG: Knots to insert in second surface - U: " ~ size(knotsToInsertInSecond_U) ~ ", V: " ~ size(knotsToInsertInSecond_V));
    }
    
    // Refine first surface if needed
    var refinedFirstSurface = firstSurface;
    if (size(knotsToInsertInFirst_U) > 0 || size(knotsToInsertInFirst_V) > 0)
    {
        refinedFirstSurface = refineByInsertingKnots(context, firstSurface, knotsToInsertInFirst_U, knotsToInsertInFirst_V, definition);
    }
    
    // Refine second surface if needed
    var refinedSecondSurface = secondSurface;
    if (size(knotsToInsertInSecond_U) > 0 || size(knotsToInsertInSecond_V) > 0)
    {
        refinedSecondSurface = refineByInsertingKnots(context, secondSurface, knotsToInsertInSecond_U, knotsToInsertInSecond_V, definition);
    }
    
    return {
        "firstSurface" : refinedFirstSurface,
        "secondSurface" : refinedSecondSurface
    };
}


/**
 * Finds knots that exist in sourceKnots but not in targetKnots (accounting for multiplicity).
 * 
 * Returns a list of knot values to insert into the target, excluding the clamped end knots.
 * 
 * @param sourceKnots {array} : Knot vector to search for missing knots
 * @param targetKnots {array} : Knot vector to check against
 * @param degree {number} : Degree of the B-spline (used to identify clamped end knots)
 * @returns {array} : List of knot values to insert (may include duplicates for multiplicity)
 */
function findMissingKnots(sourceKnots is array, targetKnots is array, degree is number) returns array
{
    // Get padded knot arrays
    // For unpadded knots: size = n - p + 1, so n = size + p - 1
    const sourceNumCP = size(sourceKnots) + degree - 1;
    const targetNumCP = size(targetKnots) + degree - 1;
    const sourcePadded = padKnotArray(sourceKnots, degree, sourceNumCP);
    const targetPadded = padKnotArray(targetKnots, degree, targetNumCP);
    
    var missingKnots = [];
    
    // For each knot in source (excluding clamped ends), check if it exists in target
    const startIdx = degree;
    const endIdx = size(sourcePadded) - degree - 1;
    
    for (var i = startIdx; i <= endIdx; i += 1)
    {
        const knotValue = sourcePadded[i];
        
        // Count multiplicity in source and target
        var sourceMultiplicity = 0;
        for (var j = startIdx; j <= endIdx; j += 1)
        {
            if (abs(sourcePadded[j] - knotValue) < KNOT_TOLERANCE)
            {
                sourceMultiplicity += 1;
            }
        }
        
        var targetMultiplicity = 0;
        for (var j = degree; j < size(targetPadded) - degree; j += 1)
        {
            if (abs(targetPadded[j] - knotValue) < KNOT_TOLERANCE)
            {
                targetMultiplicity += 1;
            }
        }
        
        // If target has fewer instances of this knot, add the difference
        if (targetMultiplicity < sourceMultiplicity)
        {
            // Check if we've already added this knot value
            var alreadyAdded = 0;
            for (var k = 0; k < size(missingKnots); k += 1)
            {
                if (abs(missingKnots[k] - knotValue) < KNOT_TOLERANCE)
                {
                    alreadyAdded += 1;
                }
            }
            
            // Add the missing instances
            const numToAdd = sourceMultiplicity - targetMultiplicity - alreadyAdded;
            for (var k = 0; k < numToAdd; k += 1)
            {
                missingKnots = append(missingKnots, knotValue);
            }
        }
    }
    
    return missingKnots;
}


/**
 * Pads a knot array to the expected size for a B-spline.
 * 
 * @param knots {array} : Unpadded knot array
 * @param degree {number} : Degree of the B-spline
 * @param numControlPoints {number} : Number of control points
 * @returns {array} : Padded knot array with size = numControlPoints + degree + 1
 */
function padKnotArray(knots is array, degree is number, numControlPoints is number) returns array
{
    const expectedSize = numControlPoints + degree + 1;
    if (size(knots) == expectedSize)
    {
        // Already padded
        return knots;
    }
    
    // Assume knots is unpadded, add degree+1 copies of first and last knots
    var padded = [];
    
    // Add degree+1 copies of first knot
    for (var i = 0; i <= degree; i += 1)
    {
        padded = append(padded, knots[0]);
    }
    
    // Add internal knots
    for (var i = 1; i < size(knots) - 1; i += 1)
    {
        padded = append(padded, knots[i]);
    }
    
    // Add degree+1 copies of last knot
    for (var i = 0; i <= degree; i += 1)
    {
        padded = append(padded, knots[size(knots) - 1]);
    }
    
    return padded;
}


/**
 * Refines a B-spline surface by inserting specific knots in U and V directions.
 * 
 * @param context {Context} : The modeling context
 * @param surface {map} : The B-spline surface to refine
 * @param knotsToInsertU {array} : List of knot values to insert in U direction
 * @param knotsToInsertV {array} : List of knot values to insert in V direction
 * @param definition {map} : The feature definition including diagnostics settings
 * @returns {map} : Refined surface
 */
function refineByInsertingKnots(context is Context, surface is map, knotsToInsertU is array, knotsToInsertV is array, definition is map)
{
    var controlPoints = surface.controlPoints;
    var weights = surface.weights;
    const isRational = surface.isRational;
    var uDegree = surface.uDegree;
    var vDegree = surface.vDegree;
    var uKnots = surface.uKnots is KnotArray ? surface.uKnots : knotArray(surface.uKnots);
    var vKnots = surface.vKnots is KnotArray ? surface.vKnots : knotArray(surface.vKnots);
    
    // Insert U knots (process each V-column)
    if (size(knotsToInsertU) > 0)
    {
        const numVPoints = size(controlPoints[0]);
        var newControlPoints = [];
        var newWeights = isRational ? [] : undefined;
        var newUKnots = undefined;
        
        for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
        {
            // Extract column
            var columnPoints = [];
            var columnWeights = [];
            for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
            {
                columnPoints = append(columnPoints, controlPoints[uIndex][vIndex]);
                if (isRational)
                {
                    columnWeights = append(columnWeights, weights[uIndex][vIndex]);
                }
            }
            
            // Create curve and insert knots
            const columnCurve = bSplineCurve({
                "degree" : uDegree,
                "controlPoints" : columnPoints,
                "knots" : uKnots,
                "isPeriodic" : surface.isUPeriodic,
                "isRational" : isRational,
                "weights" : isRational ? columnWeights : undefined
            });
            
            const refinedCurve = insertKnotsIntoCurve(context, columnCurve, knotsToInsertU);
            
            // Store results
            if (vIndex == 0)
            {
                newUKnots = refinedCurve.knots;
            }
            for (var uIndex = 0; uIndex < size(refinedCurve.controlPoints); uIndex += 1)
            {
                if (vIndex == 0)
                {
                    newControlPoints = append(newControlPoints, []);
                    if (isRational)
                    {
                        newWeights = append(newWeights, []);
                    }
                }
                newControlPoints[uIndex] = append(newControlPoints[uIndex], refinedCurve.controlPoints[uIndex]);
                if (isRational)
                {
                    newWeights[uIndex] = append(newWeights[uIndex], refinedCurve.weights[uIndex]);
                }
            }
        }
        
        controlPoints = newControlPoints;
        weights = newWeights;
        uKnots = newUKnots;
    }
    
    // Insert V knots (process each U-row)
    if (size(knotsToInsertV) > 0)
    {
        var newControlPoints = [];
        var newWeights = isRational ? [] : undefined;
        var newVKnots = undefined;
        
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            // Extract row
            const rowPoints = controlPoints[uIndex];
            const rowWeights = isRational ? weights[uIndex] : undefined;
            
            // Create curve and insert knots
            const rowCurve = bSplineCurve({
                "degree" : vDegree,
                "controlPoints" : rowPoints,
                "knots" : vKnots,
                "isPeriodic" : surface.isVPeriodic,
                "isRational" : isRational,
                "weights" : rowWeights
            });
            
            const refinedCurve = insertKnotsIntoCurve(context, rowCurve, knotsToInsertV);
            
            // Store results
            if (uIndex == 0)
            {
                newVKnots = refinedCurve.knots;
            }
            newControlPoints = append(newControlPoints, refinedCurve.controlPoints);
            if (isRational)
            {
                newWeights = append(newWeights, refinedCurve.weights);
            }
        }
        
        controlPoints = newControlPoints;
        weights = newWeights;
        vKnots = newVKnots;
    }
    
    return {
        "uDegree" : uDegree,
        "vDegree" : vDegree,
        "isRational" : isRational,
        "isUPeriodic" : surface.isUPeriodic,
        "isVPeriodic" : surface.isVPeriodic,
        "controlPoints" : controlPoints,
        "weights" : weights,
        "uKnots" : uKnots,
        "vKnots" : vKnots
    };
}


/**
 * Inserts a list of knots into a B-spline curve.
 * 
 * @param context {Context} : The modeling context
 * @param curve {map} : The B-spline curve
 * @param knotsToInsert {array} : List of knot values to insert
 * @returns {map} : Refined curve with inserted knots
 */
function insertKnotsIntoCurve(context is Context, curve is map, knotsToInsert is array)
{
    // Sort knots to insert
    const sortedKnots = sort(knotsToInsert, function(a, b) { return a - b; });
    
    // For rational curves, work in homogeneous coordinates
    var currentControlPoints = curve.controlPoints;
    var currentWeights = curve.weights;
    if (curve.isRational)
    {
        currentControlPoints = combinePointsAndWeights(curve.controlPoints, curve.weights);
    }
    var currentKnots = curve.knots;
    
    // Insert knots one at a time using Boehm algorithm
    for (var insertIdx = 0; insertIdx < size(sortedKnots); insertIdx += 1)
    {
        const result = insertKnotBoehm(currentControlPoints, currentKnots, curve.degree, sortedKnots[insertIdx]);
        currentControlPoints = result.controlPoints;
        currentKnots = result.knots;
    }
    
    // For rational curves, separate back to control points and weights
    if (curve.isRational)
    {
        const separated = separatePointsAndWeights(currentControlPoints);
        currentControlPoints = separated.points;
        currentWeights = separated.weights;
    }
    
    return {
        "controlPoints" : currentControlPoints,
        "knots" : currentKnots,
        "degree" : curve.degree,
        "isPeriodic" : curve.isPeriodic,
        "isRational" : curve.isRational,
        "weights" : currentWeights
    };
}


/**
 * Refines a B-spline curve to have a target number of control points using knot insertion.
 * 
 * Uses the mathematically correct Boehm algorithm for knot insertion to add control points
 * without changing the curve geometry. This preserves the curve shape exactly.
 * 
 * For rational curves (NURBS), the algorithm works in homogeneous coordinates to ensure
 * mathematically correct results.
 * 
 * @param context {Context} : The modeling context
 * @param curve {map} : The B-spline curve with controlPoints, knots, degree, etc. (rational or non-rational)
 * @param targetCount {number} : Target number of control points
 * @returns {map} : Refined curve with exact geometry preservation
 */
function refineCurveControlPointCount(context is Context, curve is map, targetCount is number)
{
    if (size(curve.controlPoints) >= targetCount)
    {
        return curve;
    }
    
    // Number of knots to insert
    const numToInsert = targetCount - size(curve.controlPoints);
    
    // Determine which knots to insert by distributing them uniformly in the parameter space
    const startParam = curve.knots[curve.degree];
    const endParam = curve.knots[size(curve.knots) - curve.degree - 1];
    
    // Find distinct internal knots and spaces between them
    var knotsToInsert = [];
    
    // Distribute new knots uniformly across the parameter domain
    for (var i = 1; i <= numToInsert; i += 1)
    {
        const fraction = i / (numToInsert + 1);
        const newKnot = startParam + (endParam - startParam) * fraction;
        knotsToInsert = append(knotsToInsert, newKnot);
    }
    
    // Sort knots to insert
    knotsToInsert = sort(knotsToInsert, function(a, b) { return a - b; });
    
    // For rational curves, work in homogeneous coordinates
    var currentControlPoints = curve.controlPoints;
    var currentWeights = curve.weights;
    if (curve.isRational)
    {
        currentControlPoints = combinePointsAndWeights(curve.controlPoints, curve.weights);
    }
    var currentKnots = curve.knots;
    
    // Insert knots one at a time using Boehm algorithm
    for (var insertIdx = 0; insertIdx < size(knotsToInsert); insertIdx += 1)
    {
        const result = insertKnotBoehm(currentControlPoints, currentKnots, curve.degree, knotsToInsert[insertIdx]);
        currentControlPoints = result.controlPoints;
        currentKnots = result.knots;
    }
    
    // For rational curves, separate back to control points and weights
    if (curve.isRational)
    {
        const separated = separatePointsAndWeights(currentControlPoints);
        currentControlPoints = separated.points;
        currentWeights = separated.weights;
    }
    
    return {
        "controlPoints" : currentControlPoints,
        "knots" : currentKnots,
        "degree" : curve.degree,
        "isPeriodic" : curve.isPeriodic,
        "isRational" : curve.isRational,
        "weights" : currentWeights
    };
}


// Tolerance for comparing knot parameter values
const KNOT_TOLERANCE = 1e-10;

/**
 * Inserts a single knot into a B-spline curve using the Boehm algorithm.
 * 
 * This is the mathematically correct approach that preserves curve geometry exactly.
 * The algorithm computes new control points based on the knot insertion formula:
 * 
 * For each affected control point P_i, the new control point Q_i is computed as:
 * Q_i = alpha_i * P_i + (1 - alpha_i) * P_{i-1}
 * 
 * where alpha_i depends on the knot values and the insertion parameter.
 * 
 * @param controlPoints {array} : Original control points
 * @param knots {array} : Original knot vector
 * @param degree {number} : Degree of the B-spline
 * @param insertParam {number} : Parameter value where knot should be inserted
 * @returns {map} : Map with new controlPoints and knots arrays
 */
function insertKnotBoehm(controlPoints is array, knots is array, degree is number, insertParam is number)
{
    const numControlPoints = size(controlPoints);
    
    // Validate inputs
    if (numControlPoints < degree + 1)
    {
        throw "Invalid B-spline: not enough control points for degree";
    }
    if (size(knots) != numControlPoints + degree + 1)
    {
        throw "Invalid B-spline: knot vector size doesn't match control point count";
    }
    
    // Find the knot span index where insertParam falls
    // Prefer the rightmost span when insertParam equals a knot value
    var knotSpanIndex = -1;
    for (var i = size(knots) - degree - 2; i >= degree; i -= 1)
    {
        if (insertParam >= knots[i] && insertParam <= knots[i + 1])
        {
            knotSpanIndex = i;
            break;
        }
    }
    
    // Handle edge case: if not found, clamp to valid range
    if (knotSpanIndex == -1)
    {
        if (insertParam < knots[degree])
        {
            knotSpanIndex = degree;
        }
        else
        {
            knotSpanIndex = max(degree, size(knots) - degree - 2);
        }
    }
    
    // Compute new control points using the Boehm algorithm
    // After knot insertion, we'll have numControlPoints + 1 control points
    var newControlPoints = [];
    
    // Determine the affected range: control points from (k-p+1) to k are affected
    // where k is the knot span index and p is the degree
    const k = knotSpanIndex;
    const p = degree;
    
    // Control points from 0 to (k-p) remain unchanged
    for (var i = 0; i <= k - p; i += 1)
    {
        newControlPoints = append(newControlPoints, controlPoints[i]);
    }
    
    // Compute new control points from (k-p+1) to k using Boehm's knot insertion formula
    // The formula for the new control point Q_i is:
    // Q_i = alpha_i * P_i + (1 - alpha_i) * P_{i-1}
    // where alpha_i = (u - t_i) / (t_{i+p} - t_i) and u is the parameter to insert
    for (var i = k - p + 1; i <= k; i += 1)
    {
        const denominator = knots[i + p] - knots[i];
        var alpha = 0.0;
        if (abs(denominator) > KNOT_TOLERANCE)
        {
            alpha = (insertParam - knots[i]) / denominator;
        }
        else
        {
            // For repeated knots, keep the right control point
            alpha = 1.0;
        }
        const newPoint = controlPoints[i - 1] * (1 - alpha) + controlPoints[i] * alpha;
        newControlPoints = append(newControlPoints, newPoint);
    }
    
    // The last new control point (at position k+1) is special:
    // If k+1 < numControlPoints, it's P_{k+1} from the old array
    // If k+1 == numControlPoints, we need to compute it differently
    if (k + 1 < numControlPoints)
    {
        // This shouldn't happen for single knot insertion in a clamped B-spline
        // but handle it anyway
        const denominator = knots[k + 1 + p] - knots[k + 1];
        var alpha = 0.0;
        if (abs(denominator) > KNOT_TOLERANCE)
        {
            alpha = (insertParam - knots[k + 1]) / denominator;
        }
        else
        {
            alpha = 1.0;
        }
        const newPoint = controlPoints[k] * (1 - alpha) + controlPoints[k + 1] * alpha;
        newControlPoints = append(newControlPoints, newPoint);
    }
    else
    {
        // k+1 >= numControlPoints, so this is the last control point
        // Just keep the last control point from the original array
        newControlPoints = append(newControlPoints, controlPoints[numControlPoints - 1]);
    }
    
    // Control points from (k+1) onward in the original array remain unchanged
    // but are shifted by 1 in the new array
    for (var i = k + 1; i < numControlPoints; i += 1)
    {
        newControlPoints = append(newControlPoints, controlPoints[i]);
    }
    
    // Insert the new knot into the knot vector
    var newKnots = [];
    for (var i = 0; i <= knotSpanIndex; i += 1)
    {
        newKnots = append(newKnots, knots[i]);
    }
    newKnots = append(newKnots, insertParam);
    for (var i = knotSpanIndex + 1; i < size(knots); i += 1)
    {
        newKnots = append(newKnots, knots[i]);
    }
    
    return {
        "controlPoints" : newControlPoints,
        "knots" : knotArray(newKnots)
    };
}


/**
 * Subdivides a B-spline curve into its constituent Bezier segments.
 * 
 * This function implements the subdivision algorithm that splits a B-spline curve
 * at each distinct internal knot value, producing an array of Bezier curves that
 * together represent the original B-spline.
 * 
 * Adapted from editCurve.fs subdivideIntoBeziers function.
 * 
 * @param controlPoints {array} : Control points of the B-spline curve
 * @param knots {array} : Knot vector (clamped format with multiplicity)
 * @param curveDegree {number} : Degree of the B-spline
 * @returns {array} : Array of Bezier control point arrays
 */
function subdivideIntoBeziers(controlPoints is array, knots is array, curveDegree is number) returns array
{
    var numberOfSplits = 0;
    for (var i = curveDegree + 1; i < size(knots) - curveDegree - 1; i += 1)
    {
        if (knots[i] != knots[i + 1])
        {
            numberOfSplits += 1;
        }
    }
    
    var currentKnots = knots;
    var currentPoints = controlPoints;
    var bezierSegments = makeArray(numberOfSplits + 1);
    
    for (var i = 0; i < numberOfSplits; i += 1)
    {
        const splitResult = splitAtFirstKnot(currentPoints, currentKnots, curveDegree);
        bezierSegments[i] = splitResult.bezier;
        currentPoints = splitResult.bspline;
        currentKnots = splitResult.knots;
    }
    bezierSegments[numberOfSplits] = currentPoints;
    
    return bezierSegments;
}


/**
 * Splits a B-spline curve at its first internal knot using DeBoor's algorithm.
 * 
 * Returns the first Bezier segment and the remaining B-spline curve.
 * This implements segment subdivision that gives the first Bezier piece and
 * updates the B-spline to represent the remaining segments.
 * 
 * Adapted from editCurve.fs splitAtFirstKnot function.
 * 
 * @param controlPoints {array} : Control points of the B-spline
 * @param knots {array} : Knot vector
 * @param curveDegree {number} : Degree of the curve
 * @returns {map} : Map with "bezier" (first Bezier segment control points),
 *                  "bspline" (remaining B-spline control points), and
 *                  "knots" (remaining knot vector)
 */
function splitAtFirstKnot(controlPoints is array, knots is array, curveDegree is number) returns map
{
    if (size(controlPoints) == curveDegree + 1)
    {
        // This is already a Bezier curve, no need to split
        return { "bezier" : controlPoints, "bspline" : [], "knots" : [] };
    }
    
    // First internal knot index is degree + 1
    var knotIndex = curveDegree + 1;
    // Value of first internal knot
    const knotValue = knots[knotIndex];
    // Multiplicity of the knot
    var knotMultiplicity = 1;
    for (var i = knotIndex + 1; i < size(knots); i += 1)
    {
        if (knots[i] != knots[knotIndex])
        {
            break;
        }
        knotMultiplicity += 1;
        knotIndex += 1;
    }
    
    // Apply DeBoor's algorithm
    const h = curveDegree - knotMultiplicity;
    var deBoorResult = makeArray(h + 1);
    deBoorResult[0] = subArray(controlPoints, knotIndex - curveDegree, knotIndex - knotMultiplicity + 1);
    
    for (var r = 1; r <= h; r += 1)
    {
        deBoorResult[r] = makeArray(curveDegree - knotMultiplicity - r + 1);
        for (var i = 0; i <= curveDegree - knotMultiplicity - r; i += 1)
        {
            const currentKnotIndex = i + knotIndex - curveDegree + r;
            const alpha = (knotValue - knots[currentKnotIndex]) / 
                         (knots[currentKnotIndex + curveDegree - r + 1] - knots[currentKnotIndex]);
            deBoorResult[r][i] = (1 - alpha) * deBoorResult[r - 1][i] + alpha * deBoorResult[r - 1][i + 1];
        }
    }
    
    // Extract Bezier control points and remaining B-spline control points
    const bezierPointsBeforeDeBoor = subArray(controlPoints, 0, knotIndex - curveDegree);
    const bsplinePointsAfterDeBoor = subArray(controlPoints, knotIndex - knotMultiplicity + 1, size(controlPoints));
    
    var bezierPointsFromDeBoor = makeArray(h + 1);
    var bsplinePointsFromDeBoor = makeArray(h + 1);
    for (var r = 0; r <= h; r += 1)
    {
        bezierPointsFromDeBoor[r] = deBoorResult[r][0];
        bsplinePointsFromDeBoor[r] = deBoorResult[h - r][size(deBoorResult[h - r]) - 1];
    }
    
    const bezierControlPoints = concatenateArrays([bezierPointsBeforeDeBoor, bezierPointsFromDeBoor]);
    const bsplineControlPoints = concatenateArrays([bsplinePointsFromDeBoor, bsplinePointsAfterDeBoor]);
    const newKnots = concatenateArrays([makeArray(curveDegree + 1, knotValue), 
                                        subArray(knots, knotIndex + 1, size(knots))]);
    
    return {
        "bezier" : bezierControlPoints,
        "bspline" : bsplineControlPoints,
        "knots" : newKnots
    };
}


/**
 * Elevates the degree of a general B-spline curve (including multi-segment curves).
 * 
 * This properly handles multi-segment B-splines by:
 * 1. Subdividing the B-spline into Bezier segments at each internal knot
 * 2. Elevating each Bezier segment independently using elevateBezierDegree
 * 3. Recombining the elevated segments with proper knot vector handling
 * 4. Removing unnecessary knots to simplify the result
 * 
 * This is the correct approach for B-spline degree elevation that preserves
 * geometry exactly.
 * 
 * Adapted from editCurve.fs elevateBSpline function.
 * 
 * @param controlPoints {array} : Original B-spline control points
 * @param knots {array} : Original knot vector
 * @param originalDegree {number} : Current degree of the B-spline
 * @param newDegree {number} : Target degree for elevation
 * @returns {map} : Map with "controlPoints" and "knots" for the elevated B-spline
 */
function elevateBSplineCurve(controlPoints is array, knots is array, originalDegree is number, newDegree is number) returns map
{
    // First subdivide the B-spline into Bezier curves
    var bezierSegments = subdivideIntoBeziers(controlPoints, knots, originalDegree);
    
    // Elevate each Bezier curve separately
    for (var i = 0; i < size(bezierSegments); i += 1)
    {
        bezierSegments[i] = elevateBezierDegree(bezierSegments[i], newDegree);
    }
    
    // Combine the Bezier curves back into one B-spline
    var elevatedControlPoints = [bezierSegments[0][0]];
    for (var i = 0; i < size(bezierSegments); i += 1)
    {
        for (var j = 1; j < size(bezierSegments[i]); j += 1)
        {
            elevatedControlPoints = append(elevatedControlPoints, bezierSegments[i][j]);
        }
    }
    
    // Create the corresponding knot vector with added multiplicity
    const lastKnot = knots[size(knots) - originalDegree - 1];
    var i = originalDegree + 1;
    var currentKnot = knots[i];
    var elevatedKnots = makeArray(newDegree + 1, knots[i - 1]);
    
    while (currentKnot != lastKnot)
    {
        elevatedKnots = concatenateArrays([elevatedKnots, makeArray(newDegree, currentKnot)]);
        // Skip identical knots
        while (knots[i] == currentKnot)
        {
            i += 1;
        }
        currentKnot = knots[i];
    }
    elevatedKnots = concatenateArrays([elevatedKnots, makeArray(newDegree + 1, lastKnot)]);
    
    // Simplify by removing unnecessary knots
    const simplified = removeKnots(elevatedControlPoints, elevatedKnots, newDegree);
    
    return {
        "controlPoints" : simplified.points,
        "knots" : simplified.knots
    };
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
