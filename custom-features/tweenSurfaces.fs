FeatureScript 2837;

/**
 * Surface Tween Feature
 * 
 * This feature creates a median (tweened) surface between two input surfaces.
 * It is inspired by the Parasolid PK_neutral_method_medial_c function which creates
 * a neutral sheet that is an "average" mid-surface between two faces.
 * 
 * The implementation samples points on both surfaces using parametric coordinates
 * and creates a lofted surface through the interpolated profile curves.
 * 
 * Current implementation: Linear interpolation between surfaces
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
 * The implementation samples both surfaces at corresponding parametric positions and
 * creates profile curves through the linearly interpolated points. These profiles are
 * then lofted to create the final tweened surface.
 */
annotation { "Feature Type Name" : "Tween Surfaces",
        "Feature Type Description" : "Creates a median surface between two input surfaces. At fraction 0.5, creates a neutral sheet that is equidistant between the two surfaces.",
        "UIHint" : "NO_PREVIEW_PROVIDED" }
export const tweenSurfaces = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "First surface", "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.firstSurface is Query;
        
        annotation { "Name" : "Second surface", "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.secondSurface is Query;
        
        annotation { "Name" : "Tween fraction", "Description" : "Position of the median surface: 0 = first surface, 0.5 = middle, 1 = second surface" }
        isReal(definition.tweenFraction, SURFACE_TWEEN_FRACTION_BOUNDS);
        
        annotation { "Name" : "Sample resolution", "Description" : "Number of sample points per direction (higher = more accurate but slower)" }
        isInteger(definition.sampleResolution, POSITIVE_COUNT_BOUNDS);
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
        createTweenedSurface(context, id, firstFace, secondFace, definition.tweenFraction, definition.sampleResolution);
    }, { tweenFraction : 0.5, sampleResolution : 10 });


/**
 * Creates a tweened surface between two faces by sampling points and creating a loft.
 * 
 * The algorithm:
 * 1. Samples both surfaces at a grid of parametric positions (u, v)
 * 2. For each sampled point pair, calculates the interpolated point:
 *    tweenedPoint = (1 - fraction) * pointA + fraction * pointB
 * 3. Creates profile curves (B-splines) through rows of interpolated points
 * 4. Lofts through all profile curves to create the final surface
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : The feature identifier
 * @param firstFace {Query} : Query resolving to the first face
 * @param secondFace {Query} : Query resolving to the second face
 * @param tweenFraction {number} : The interpolation fraction (0 to 1)
 * @param sampleResolution {number} : Number of samples per parametric direction
 */
function createTweenedSurface(context is Context, id is Id, 
        firstFace is Query, secondFace is Query, tweenFraction is number, sampleResolution is number)
{
    // Sample points on both surfaces in a grid pattern using parametric coordinates
    var profileCurves = [];
    const numberOfProfiles = sampleResolution;
    
    for (var profileIndex = 0; profileIndex < numberOfProfiles; profileIndex += 1)
    {
        const vParameter = profileIndex / max(numberOfProfiles - 1, 1);
        
        // Create a profile curve at this v parameter by sampling along the u direction
        var interpolatedPoints = [];
        
        for (var pointIndex = 0; pointIndex < sampleResolution; pointIndex += 1)
        {
            const uParameter = pointIndex / max(sampleResolution - 1, 1);
            
            // Sample corresponding points on both surfaces using parametric evaluation
            const firstSurfacePoint = sampleSurfacePoint(context, firstFace, uParameter, vParameter);
            const secondSurfacePoint = sampleSurfacePoint(context, secondFace, uParameter, vParameter);
            
            if (firstSurfacePoint != undefined && secondSurfacePoint != undefined)
            {
                // Calculate the linearly interpolated point between the two surfaces
                // tweenedPoint = (1 - fraction) * point1 + fraction * point2
                // For fraction = 0.5, this gives the exact median point
                const tweenedPoint = firstSurfacePoint * (1 - tweenFraction) + secondSurfacePoint * tweenFraction;
                interpolatedPoints = append(interpolatedPoints, tweenedPoint);
            }
        }
        
        // Create a B-spline curve through the interpolated points if we have enough points
        if (size(interpolatedPoints) >= 2)
        {
            const splineDefinition = bSplineCurve({
                "degree" : min(3, size(interpolatedPoints) - 1),
                "controlPoints" : interpolatedPoints,
                "isPeriodic" : false
            });
            
            opCreateBSplineCurve(context, id + ("profile" ~ profileIndex), { "bSplineCurve" : splineDefinition });
            profileCurves = append(profileCurves, qCreatedBy(id + ("profile" ~ profileIndex), EntityType.EDGE));
        }
    }
    
    // Create a loft through all the profile curves to form the tweened surface
    if (size(profileCurves) >= 2)
    {
        opLoft(context, id + "loft", {
            "profileSubqueries" : profileCurves
        });
    }
    else
    {
        throw regenError("Unable to create sufficient profile curves for the tweened surface. Try adjusting the sample resolution.");
    }
}


/**
 * Samples a point on a surface at given parametric coordinates.
 * 
 * Uses evFaceTangentPlane to evaluate the surface at a parametric position in UV space.
 * The parametric coordinates (u, v) are normalized to the range [0, 1] and correspond
 * to the parameter-space bounding box of the face.
 * 
 * @param context {Context} : The modeling context
 * @param face {Query} : Query resolving to the face to sample
 * @param uParameter {number} : The u parameter in parametric space (0 to 1)
 * @param vParameter {number} : The v parameter in parametric space (0 to 1)
 * @returns {Vector | undefined} : The sampled 3D point on the surface, or undefined if sampling fails
 */
function sampleSurfacePoint(context is Context, face is Query, uParameter is number, vParameter is number)
{
    try
    {
        // Use evFaceTangentPlane to sample the surface at the parametric position
        // The parameter is a 2D vector in normalized parameter space coordinates [0, 1]
        const tangentPlane = evFaceTangentPlane(context, {
            "face" : face,
            "parameter" : vector(uParameter, vParameter)
        });
        
        // Return the origin of the tangent plane, which is the point on the surface
        return tangentPlane.origin;
    }
    catch
    {
        // If sampling fails (e.g., parameter is outside valid range for the surface),
        // return undefined to allow the calling code to skip this point
        return undefined;
    }
}
