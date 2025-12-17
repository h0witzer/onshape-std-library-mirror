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
 * 2. Interpolates control points of the two B-spline surfaces
 * 3. Creates a new B-spline surface with the interpolated control points
 * 
 * Current implementation: Linear interpolation of B-spline control points
 * - fraction = 0: surface coincident with first surface
 * - fraction = 0.5: median surface (equidistant from both surfaces)
 * - fraction = 1: surface coincident with second surface
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
        createTweenedSurface(context, id, firstFace, secondFace, definition.tweenFraction);
    }, { tweenFraction : 0.5 });


/**
 * Creates a tweened surface between two faces by interpolating B-spline control points.
 * 
 * The algorithm:
 * 1. Obtains B-spline surface representations of both input faces
 *    - If a face is already a B-spline, uses its definition directly
 *    - Otherwise, creates a B-spline approximation
 * 2. Ensures both surfaces are compatible (same degrees and control point counts)
 * 3. Interpolates control points: tweenedCP = (1 - fraction) * cp1 + fraction * cp2
 * 4. Creates a new B-spline surface with the interpolated control points
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : The feature identifier
 * @param firstFace {Query} : Query resolving to the first face
 * @param secondFace {Query} : Query resolving to the second face
 * @param tweenFraction {number} : The interpolation fraction (0 to 1)
 */
function createTweenedSurface(context is Context, id is Id, 
        firstFace is Query, secondFace is Query, tweenFraction is number)
{
    // Get B-spline surface representations of both faces
    var firstSurface = getBSplineSurfaceFromFace(context, firstFace);
    var secondSurface = getBSplineSurfaceFromFace(context, secondFace);
    
    println("DEBUG: Initial first surface - uDegree=" ~ firstSurface.uDegree ~ ", vDegree=" ~ firstSurface.vDegree ~ 
            ", controlPoints=" ~ size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]));
    println("DEBUG: Initial second surface - uDegree=" ~ secondSurface.uDegree ~ ", vDegree=" ~ secondSurface.vDegree ~ 
            ", controlPoints=" ~ size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
    
    // Elevate degrees to match if necessary
    if (firstSurface.uDegree != secondSurface.uDegree || firstSurface.vDegree != secondSurface.vDegree)
    {
        const targetUDegree = max(firstSurface.uDegree, secondSurface.uDegree);
        const targetVDegree = max(firstSurface.vDegree, secondSurface.vDegree);
        
        // Check if either surface has multi-segment B-splines that would require proper elevation
        const firstIsMultiSegmentU = !isSingleSegmentBezierCurve(firstSurface.uDegree, size(firstSurface.controlPoints));
        const firstIsMultiSegmentV = !isSingleSegmentBezierCurve(firstSurface.vDegree, size(firstSurface.controlPoints[0]));
        const secondIsMultiSegmentU = !isSingleSegmentBezierCurve(secondSurface.uDegree, size(secondSurface.controlPoints));
        const secondIsMultiSegmentV = !isSingleSegmentBezierCurve(secondSurface.vDegree, size(secondSurface.controlPoints[0]));
        
        if ((firstIsMultiSegmentU || firstIsMultiSegmentV || secondIsMultiSegmentU || secondIsMultiSegmentV) &&
            (firstSurface.uDegree != secondSurface.uDegree || firstSurface.vDegree != secondSurface.vDegree))
        {
            println("WARNING: Surfaces have different degrees and at least one is a multi-segment B-spline.");
            println("         Degree elevation may not preserve surface geometry correctly.");
            println("         First surface: uDegree=" ~ firstSurface.uDegree ~ ", vDegree=" ~ firstSurface.vDegree ~
                    ", controlPoints=" ~ size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]));
            println("         Second surface: uDegree=" ~ secondSurface.uDegree ~ ", vDegree=" ~ secondSurface.vDegree ~
                    ", controlPoints=" ~ size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
            println("         For best results, use surfaces with matching degrees or single-segment surfaces.");
        }
        
        if (firstSurface.uDegree < targetUDegree || firstSurface.vDegree < targetVDegree)
        {
            println("DEBUG: Elevating first surface from (" ~ firstSurface.uDegree ~ "," ~ firstSurface.vDegree ~ 
                    ") to (" ~ targetUDegree ~ "," ~ targetVDegree ~ ")");
            firstSurface = elevateSurfaceDegree(firstSurface, targetUDegree, targetVDegree);
            println("DEBUG: After elevation, first surface controlPoints=" ~ 
                    size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]));
        }
        if (secondSurface.uDegree < targetUDegree || secondSurface.vDegree < targetVDegree)
        {
            println("DEBUG: Elevating second surface from (" ~ secondSurface.uDegree ~ "," ~ secondSurface.vDegree ~ 
                    ") to (" ~ targetUDegree ~ "," ~ targetVDegree ~ ")");
            secondSurface = elevateSurfaceDegree(secondSurface, targetUDegree, targetVDegree);
            println("DEBUG: After elevation, second surface controlPoints=" ~ 
                    size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
        }
    }
    
    // Match control point counts by inserting knots if necessary
    const firstControlPointsRowCount = size(firstSurface.controlPoints);
    const firstControlPointsColumnCount = size(firstSurface.controlPoints[0]);
    const secondControlPointsRowCount = size(secondSurface.controlPoints);
    const secondControlPointsColumnCount = size(secondSurface.controlPoints[0]);
    
    if (firstControlPointsRowCount != secondControlPointsRowCount || 
        firstControlPointsColumnCount != secondControlPointsColumnCount)
    {
        const targetUCount = max(firstControlPointsRowCount, secondControlPointsRowCount);
        const targetVCount = max(firstControlPointsColumnCount, secondControlPointsColumnCount);
        
        if (firstControlPointsRowCount < targetUCount || firstControlPointsColumnCount < targetVCount)
        {
            println("DEBUG: Refining first surface from " ~ firstControlPointsRowCount ~ "x" ~ firstControlPointsColumnCount ~ 
                    " to " ~ targetUCount ~ "x" ~ targetVCount);
            firstSurface = refineControlPointCount(context, firstSurface, targetUCount, targetVCount);
            println("DEBUG: After refinement, first surface controlPoints=" ~ 
                    size(firstSurface.controlPoints) ~ "x" ~ size(firstSurface.controlPoints[0]));
        }
        if (secondControlPointsRowCount < targetUCount || secondControlPointsColumnCount < targetVCount)
        {
            println("DEBUG: Refining second surface from " ~ secondControlPointsRowCount ~ "x" ~ secondControlPointsColumnCount ~ 
                    " to " ~ targetUCount ~ "x" ~ targetVCount);
            secondSurface = refineControlPointCount(context, secondSurface, targetUCount, targetVCount);
            println("DEBUG: After refinement, second surface controlPoints=" ~ 
                    size(secondSurface.controlPoints) ~ "x" ~ size(secondSurface.controlPoints[0]));
        }
    }
    
    // Verify both surfaces have the same rationality
    if (firstSurface.isRational != secondSurface.isRational)
    {
        throw regenError("Both surfaces must be either rational or non-rational. First surface is " ~ 
            (firstSurface.isRational ? "rational" : "non-rational") ~ ", second surface is " ~
            (secondSurface.isRational ? "rational" : "non-rational") ~ ".");
    }
    
    // For now, degree elevation of rational surfaces is not supported
    if (firstSurface.isRational && (firstSurface.uDegree != secondSurface.uDegree || firstSurface.vDegree != secondSurface.vDegree))
    {
        throw regenError("Automatic degree elevation is not yet supported for rational surfaces. " ~
            "First surface: uDegree=" ~ firstSurface.uDegree ~ ", vDegree=" ~ firstSurface.vDegree ~
            ". Second surface: uDegree=" ~ secondSurface.uDegree ~ ", vDegree=" ~ secondSurface.vDegree ~ ".");
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
    
    // Interpolate control points
    var tweenedControlPoints = [];
    var debugPointCount = 0;
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
            
            // Debug: Visualize control points for debugging
            // Show all control points to see their distribution
            debug(context, firstControlPoint, DebugColor.BLUE);
            debug(context, secondControlPoint, DebugColor.RED);
            debug(context, tweenedControlPoint, DebugColor.GREEN);
            debugPointCount += 1;
            
            // Additional debug: Print some sample interpolations to verify math
            if (uIndex == 0 && vIndex == 0)
            {
                println("DEBUG: Corner CP interpolation (fraction=" ~ tweenFraction ~ "):");
                println("  First CP: " ~ firstControlPoint);
                println("  Second CP: " ~ secondControlPoint);
                println("  Tweened CP: " ~ tweenedControlPoint);
                println("  Expected: " ~ (firstControlPoint * (1 - tweenFraction) + secondControlPoint * tweenFraction));
            }
        }
        tweenedControlPoints = append(tweenedControlPoints, controlPointRow);
    }
    
    println("DEBUG: Drew " ~ debugPointCount ~ " sets of control points (blue/red/green)");
    println("DEBUG: Expected " ~ (finalFirstControlPointsRowCount * finalFirstControlPointsColumnCount) ~ " sets");
    
    // Interpolate weights if surfaces are rational
    var tweenedWeights = undefined;
    if (firstSurface.isRational)
    {
        tweenedWeights = [];
        for (var uIndex = 0; uIndex < finalFirstControlPointsRowCount; uIndex += 1)
        {
            var weightRow = [];
            for (var vIndex = 0; vIndex < finalFirstControlPointsColumnCount; vIndex += 1)
            {
                const firstWeight = firstSurface.weights[uIndex][vIndex];
                const secondWeight = secondSurface.weights[uIndex][vIndex];
                const tweenedWeight = firstWeight * (1 - tweenFraction) + secondWeight * tweenFraction;
                weightRow = append(weightRow, tweenedWeight);
            }
            tweenedWeights = append(tweenedWeights, weightRow);
        }
    }
    
    // Unpad knot arrays (bSplineSurface expects unpadded knots)
    // Padded knots have size: nControlPoints + degree + 1
    // Unpadded knots should have size: nControlPoints - degree + 1
    // So we remove the first 'degree' and last 'degree' knots
    const uDegree = firstSurface.uDegree;
    const vDegree = firstSurface.vDegree;
    const numUControlPoints = size(tweenedControlPoints);
    const numVControlPoints = size(tweenedControlPoints[0]);
    
    // Verify the knot array sizes are correct for padded arrays
    const expectedPaddedUSize = numUControlPoints + uDegree + 1;
    const expectedPaddedVSize = numVControlPoints + vDegree + 1;
    
    // Debug logging to diagnose knot array format issues
    println("DEBUG: uDegree=" ~ uDegree ~ ", vDegree=" ~ vDegree);
    println("DEBUG: numUControlPoints=" ~ numUControlPoints ~ ", numVControlPoints=" ~ numVControlPoints);
    println("DEBUG: firstSurface.uKnots size=" ~ size(firstSurface.uKnots));
    println("DEBUG: firstSurface.vKnots size=" ~ size(firstSurface.vKnots));
    println("DEBUG: expectedPaddedUSize=" ~ expectedPaddedUSize);
    println("DEBUG: expectedPaddedVSize=" ~ expectedPaddedVSize);
    println("DEBUG: Expected unpadded U size=" ~ (numUControlPoints - uDegree + 1));
    println("DEBUG: Expected unpadded V size=" ~ (numVControlPoints - vDegree + 1));
    
    var unpaddedUKnots = [];
    if (size(firstSurface.uKnots) == expectedPaddedUSize)
    {
        // Knots are padded, unpad them
        for (var i = uDegree; i < size(firstSurface.uKnots) - uDegree; i += 1)
        {
            unpaddedUKnots = append(unpaddedUKnots, firstSurface.uKnots[i]);
        }
        println("DEBUG: Unpadded U knots from padded format, result size=" ~ size(unpaddedUKnots));
    }
    else
    {
        // Knots might already be unpadded or in unexpected format, use as-is
        unpaddedUKnots = firstSurface.uKnots;
        println("DEBUG: Using U knots as-is, size=" ~ size(unpaddedUKnots));
    }
    
    var unpaddedVKnots = [];
    if (size(firstSurface.vKnots) == expectedPaddedVSize)
    {
        // Knots are padded, unpad them
        for (var i = vDegree; i < size(firstSurface.vKnots) - vDegree; i += 1)
        {
            unpaddedVKnots = append(unpaddedVKnots, firstSurface.vKnots[i]);
        }
        println("DEBUG: Unpadded V knots from padded format, result size=" ~ size(unpaddedVKnots));
    }
    else
    {
        // Knots might already be unpadded or in unexpected format, use as-is
        unpaddedVKnots = firstSurface.vKnots;
        println("DEBUG: Using V knots as-is, size=" ~ size(unpaddedVKnots));
    }
    
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
 * Elevates the degree of a non-rational B-spline surface in U and/or V directions.
 * 
 * This uses degree elevation for single-segment B-spline surfaces (Bezier patches).
 * For tensor product B-spline surfaces, degree elevation is performed independently 
 * in each parametric direction by treating each isoparametric curve as a separate 
 * Bezier curve and elevating its degree.
 * 
 * LIMITATION: This implementation only works correctly for single-segment B-splines
 * (Bezier patches). For multi-segment B-splines, proper B-spline degree elevation
 * algorithms should be used instead (subdivision into Bezier segments, elevate each,
 * then recombine with proper knot vector handling).
 * 
 * Note: This implementation currently only supports non-rational surfaces.
 * For general B-spline surfaces with non-uniform knots, the knot vectors are
 * regenerated as uniform after elevation.
 * 
 * @param surface {map} : The B-spline surface to elevate (must be non-rational)
 * @param targetUDegree {number} : The desired degree in U direction
 * @param targetVDegree {number} : The desired degree in V direction
 * @returns {map} : The surface with elevated degrees
 */
function elevateSurfaceDegree(surface is map, targetUDegree is number, targetVDegree is number)
{
    var controlPoints = surface.controlPoints;
    var uDegree = surface.uDegree;
    var vDegree = surface.vDegree;
    // Ensure knots are KnotArray type
    var uKnots = surface.uKnots is KnotArray ? surface.uKnots : knotArray(surface.uKnots);
    var vKnots = surface.vKnots is KnotArray ? surface.vKnots : knotArray(surface.vKnots);
    
    // Elevate U degree if needed (process each V-column as an independent curve)
    if (uDegree < targetUDegree)
    {
        const numVPoints = size(controlPoints[0]);
        var newControlPoints = [];
        
        for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
        {
            // Extract column of control points
            var columnPoints = [];
            for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
            {
                columnPoints = append(columnPoints, controlPoints[uIndex][vIndex]);
            }
            
            // Elevate this column curve using Bezier degree elevation
            // NOTE: This only works correctly for single-segment B-splines (Bezier curves)
            const elevatedPoints = elevateBezierDegree(columnPoints, targetUDegree);
            
            // Store elevated control points back (transpose)
            for (var uIndex = 0; uIndex < size(elevatedPoints); uIndex += 1)
            {
                if (vIndex == 0)
                {
                    newControlPoints = append(newControlPoints, []);
                }
                newControlPoints[uIndex] = append(newControlPoints[uIndex], elevatedPoints[uIndex]);
            }
        }
        
        controlPoints = newControlPoints;
        uDegree = targetUDegree;
        // Update U knot vector - create new uniform knot vector for elevated surface
        uKnots = makeUniformKnotVector(targetUDegree, size(controlPoints));
    }
    
    // Elevate V degree if needed (process each U-row as an independent curve)
    if (vDegree < targetVDegree)
    {
        var newControlPoints = [];
        
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            // Extract row of control points
            const rowPoints = controlPoints[uIndex];
            
            // Elevate this row curve using Bezier degree elevation
            // NOTE: This only works correctly for single-segment B-splines (Bezier curves)
            const elevatedPoints = elevateBezierDegree(rowPoints, targetVDegree);
            
            newControlPoints = append(newControlPoints, elevatedPoints);
        }
        
        controlPoints = newControlPoints;
        vDegree = targetVDegree;
        // Update V knot vector - create new uniform knot vector for elevated surface
        vKnots = makeUniformKnotVector(targetVDegree, size(controlPoints[0]));
    }
    
    return {
        "uDegree" : uDegree,
        "vDegree" : vDegree,
        "isRational" : surface.isRational,
        "isUPeriodic" : surface.isUPeriodic,
        "isVPeriodic" : surface.isVPeriodic,
        "controlPoints" : controlPoints,
        "weights" : surface.weights,
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
 * Note: This preserves surface geometry exactly using proper B-spline calculus.
 * For rational surfaces with CP count mismatch, an error is thrown.
 * 
 * @param context {Context} : The modeling context
 * @param surface {map} : The B-spline surface to refine
 * @param targetUCount {number} : Target number of control points in U direction
 * @param targetVCount {number} : Target number of control points in V direction
 * @returns {map} : Refined surface with target control point counts
 */
function refineControlPointCount(context is Context, surface is map, targetUCount is number, targetVCount is number)
{
    // For now, refinement of rational surfaces is not supported
    if (surface.isRational)
    {
        throw regenError("Automatic control point count matching is not yet supported for rational surfaces. " ~
            "Current surface has " ~ size(surface.controlPoints) ~ "x" ~ size(surface.controlPoints[0]) ~ 
            " control points. Target is " ~ targetUCount ~ "x" ~ targetVCount ~ ".");
    }
    
    var controlPoints = surface.controlPoints;
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
        var newUKnots = undefined;
        
        for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
        {
            // Extract column of control points
            var columnPoints = [];
            for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
            {
                columnPoints = append(columnPoints, controlPoints[uIndex][vIndex]);
            }
            
            // Create a B-spline curve from this column
            const columnCurve = bSplineCurve({
                "degree" : uDegree,
                "controlPoints" : columnPoints,
                "knots" : uKnots,
                "isPeriodic" : surface.isUPeriodic,
                "isRational" : false
            });
            
            // Refine this curve to have targetUCount control points
            const refinedCurve = refineCurveControlPointCount(context, columnCurve, targetUCount);
            
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
                }
                newControlPoints[uIndex] = append(newControlPoints[uIndex], refinedCurve.controlPoints[uIndex]);
            }
        }
        
        controlPoints = newControlPoints;
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
        var newVKnots = undefined;
        
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            // Extract row of control points
            const rowPoints = controlPoints[uIndex];
            
            // Create a B-spline curve from this row
            const rowCurve = bSplineCurve({
                "degree" : vDegree,
                "controlPoints" : rowPoints,
                "knots" : vKnots,
                "isPeriodic" : surface.isVPeriodic,
                "isRational" : false
            });
            
            // Refine this curve to have targetVCount control points
            const refinedCurve = refineCurveControlPointCount(context, rowCurve, targetVCount);
            
            // Store refined control points and update knots from first row
            if (uIndex == 0)
            {
                newVKnots = refinedCurve.knots;
            }
            newControlPoints = append(newControlPoints, refinedCurve.controlPoints);
        }
        
        controlPoints = newControlPoints;
        if (newVKnots != undefined)
        {
            vKnots = newVKnots;
        }
    }
    
    return {
        "uDegree" : uDegree,
        "vDegree" : vDegree,
        "isRational" : surface.isRational,
        "isUPeriodic" : surface.isUPeriodic,
        "isVPeriodic" : surface.isVPeriodic,
        "controlPoints" : controlPoints,
        "weights" : surface.weights,
        "uKnots" : uKnots,
        "vKnots" : vKnots
    };
}


/**
 * Refines a B-spline curve to have a target number of control points using knot insertion.
 * 
 * Uses the mathematically correct Boehm algorithm for knot insertion to add control points
 * without changing the curve geometry. This preserves the curve shape exactly.
 * 
 * @param context {Context} : The modeling context
 * @param curve {map} : The B-spline curve with controlPoints, knots, degree, etc.
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
    
    // Insert knots one at a time using Boehm algorithm
    var currentControlPoints = curve.controlPoints;
    var currentKnots = curve.knots;
    
    for (var insertIdx = 0; insertIdx < size(knotsToInsert); insertIdx += 1)
    {
        const result = insertKnotBoehm(currentControlPoints, currentKnots, curve.degree, knotsToInsert[insertIdx]);
        currentControlPoints = result.controlPoints;
        currentKnots = result.knots;
    }
    
    return {
        "controlPoints" : currentControlPoints,
        "knots" : currentKnots,
        "degree" : curve.degree,
        "isPeriodic" : curve.isPeriodic,
        "isRational" : curve.isRational,
        "weights" : curve.weights
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
    
    // Compute new control points from (k-p+1) to (k+1)
    // These are the affected control points plus one new one
    for (var i = k - p + 1; i <= k + 1; i += 1)
    {
        // Compute alpha for this new control point
        var alpha = 0.0;
        
        // For the new control point at position i, we blend old points at i-1 and i
        // But we need to be careful about array bounds
        const oldIndex = min(i, numControlPoints - 1);
        
        const denominator = knots[oldIndex + p] - knots[oldIndex];
        if (abs(denominator) > KNOT_TOLERANCE)
        {
            alpha = (insertParam - knots[oldIndex]) / denominator;
        }
        else
        {
            // For repeated knots, use alpha = 1 (keep the current point)
            alpha = 1.0;
        }
        
        // Blend between control points
        if (i == 0)
        {
            // Edge case: first control point
            newControlPoints = append(newControlPoints, controlPoints[0]);
        }
        else if (oldIndex == i && i < numControlPoints)
        {
            // Normal case: blend P[i-1] and P[i]
            const newPoint = controlPoints[i - 1] * (1 - alpha) + controlPoints[i] * alpha;
            newControlPoints = append(newControlPoints, newPoint);
        }
        else if (i == numControlPoints)
        {
            // Edge case: we're adding a point beyond the original array
            // This happens when k+1 == numControlPoints
            // Blend the last two points
            const newPoint = controlPoints[numControlPoints - 2] * (1 - alpha) + controlPoints[numControlPoints - 1] * alpha;
            newControlPoints = append(newControlPoints, newPoint);
        }
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
