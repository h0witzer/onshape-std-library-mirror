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
    
    // Verify surfaces are compatible for tweening
    if (firstSurface.uDegree != secondSurface.uDegree || firstSurface.vDegree != secondSurface.vDegree)
    {
        throw regenError("Surfaces must have matching B-spline degrees in U and V directions. First surface: uDegree=" ~ firstSurface.uDegree ~ 
            ", vDegree=" ~ firstSurface.vDegree ~ ". Second surface: uDegree=" ~ secondSurface.uDegree ~ 
            ", vDegree=" ~ secondSurface.vDegree ~ ".");
    }
    
    const firstControlPointsRowCount = size(firstSurface.controlPoints);
    const firstControlPointsColumnCount = size(firstSurface.controlPoints[0]);
    const secondControlPointsRowCount = size(secondSurface.controlPoints);
    const secondControlPointsColumnCount = size(secondSurface.controlPoints[0]);
    
    if (firstControlPointsRowCount != secondControlPointsRowCount || 
        firstControlPointsColumnCount != secondControlPointsColumnCount)
    {
        throw regenError("Surfaces must have matching control point counts in U and V directions. First surface: " ~ 
            firstControlPointsRowCount ~ "x" ~ firstControlPointsColumnCount ~ 
            ". Second surface: " ~ secondControlPointsRowCount ~ "x" ~ secondControlPointsColumnCount ~ ".");
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
    
    // Interpolate control points
    var tweenedControlPoints = [];
    for (var uIndex = 0; uIndex < firstControlPointsRowCount; uIndex += 1)
    {
        var controlPointRow = [];
        for (var vIndex = 0; vIndex < firstControlPointsColumnCount; vIndex += 1)
        {
            const firstControlPoint = firstSurface.controlPoints[uIndex][vIndex];
            const secondControlPoint = secondSurface.controlPoints[uIndex][vIndex];
            
            // Linear interpolation: tweenedCP = (1 - fraction) * cp1 + fraction * cp2
            const tweenedControlPoint = firstControlPoint * (1 - tweenFraction) + secondControlPoint * tweenFraction;
            controlPointRow = append(controlPointRow, tweenedControlPoint);
        }
        tweenedControlPoints = append(tweenedControlPoints, controlPointRow);
    }
    
    // Interpolate weights if surfaces are rational
    var tweenedWeights = undefined;
    if (firstSurface.isRational)
    {
        tweenedWeights = [];
        for (var uIndex = 0; uIndex < firstControlPointsRowCount; uIndex += 1)
        {
            var weightRow = [];
            for (var vIndex = 0; vIndex < firstControlPointsColumnCount; vIndex += 1)
            {
                const firstWeight = firstSurface.weights[uIndex][vIndex];
                const secondWeight = secondSurface.weights[uIndex][vIndex];
                const tweenedWeight = firstWeight * (1 - tweenFraction) + secondWeight * tweenFraction;
                weightRow = append(weightRow, tweenedWeight);
            }
            tweenedWeights = append(tweenedWeights, weightRow);
        }
    }
    
    // Create the tweened B-spline surface
    const tweenedSurfaceDefinition = bSplineSurface({
        "uDegree" : firstSurface.uDegree,
        "vDegree" : firstSurface.vDegree,
        "isUPeriodic" : firstSurface.isUPeriodic,
        "isVPeriodic" : firstSurface.isVPeriodic,
        "controlPoints" : tweenedControlPoints,
        "weights" : tweenedWeights,
        "uKnots" : firstSurface.uKnots,
        "vKnots" : firstSurface.vKnots
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
