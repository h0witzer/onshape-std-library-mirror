FeatureScript 2837;

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
 * - 0 = coincident with surface A
 * - 0.5 = median surface (default)
 * - 1 = coincident with surface B
 */
annotation { "Feature Type Name" : "Tween Surfaces",
        "Feature Type Description" : "Creates a median surface between two input surfaces. At fraction 0.5, creates a neutral sheet that is equidistant between the two surfaces.",
        "UIHint" : "NO_PREVIEW_PROVIDED" }
export const tweenSurfaces = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "First surface", "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.surfaceA is Query;
        
        annotation { "Name" : "Second surface", "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.surfaceB is Query;
        
        annotation { "Name" : "Tween fraction", "Description" : "Position of the median surface: 0 = first surface, 0.5 = middle, 1 = second surface" }
        isReal(definition.fraction, SURFACE_TWEEN_FRACTION_BOUNDS);
        
        annotation { "Name" : "Sample resolution", "Description" : "Number of sample points per direction (higher = more accurate but slower)" }
        isInteger(definition.sampleResolution, POSITIVE_COUNT_BOUNDS);
    }
    {
        // Validate inputs
        if (evaluateQueryCount(context, definition.surfaceA) == 0)
            throw regenError("Select first surface.", ["surfaceA"]);
        if (evaluateQueryCount(context, definition.surfaceB) == 0)
            throw regenError("Select second surface.", ["surfaceB"]);
        
        const faceA = evaluateQuery(context, definition.surfaceA)[0];
        const faceB = evaluateQuery(context, definition.surfaceB)[0];
        
        // Create the tweened surface
        createTweenedSurface(context, id, faceA, faceB, definition.fraction, definition.sampleResolution);
    }, { fraction : 0.5, sampleResolution : 10 });


/**
 * Creates a tweened surface between two faces by sampling points and creating a loft.
 * 
 * @param context {Context} : The context
 * @param id {Id} : The feature id
 * @param faceA {Query} : The first face
 * @param faceB {Query} : The second face
 * @param fraction {number} : The tween fraction (0 to 1)
 * @param sampleResolution {number} : Number of samples per direction
 */
function createTweenedSurface(context is Context, id is Id, 
        faceA is Query, faceB is Query, fraction is number, sampleResolution is number)
{
    // Sample points on both surfaces in a grid pattern using parametric coordinates
    var profiles = [];
    const numProfiles = sampleResolution;
    
    for (var i = 0; i < numProfiles; i += 1)
    {
        const vParam = i / max(numProfiles - 1, 1);
        
        // Create a profile curve at this v parameter
        var profilePoints = [];
        
        for (var j = 0; j < sampleResolution; j += 1)
        {
            const uParam = j / max(sampleResolution - 1, 1);
            
            // Sample points on both surfaces using parametric evaluation
            const pointA = sampleSurfacePoint(context, faceA, uParam, vParam);
            const pointB = sampleSurfacePoint(context, faceB, uParam, vParam);
            
            if (pointA != undefined && pointB != undefined)
            {
                // Calculate the tweened point: (1 - fraction) * pointA + fraction * pointB
                // For fraction = 0.5, this gives the exact median
                const tweenedPoint = pointA * (1 - fraction) + pointB * fraction;
                profilePoints = append(profilePoints, tweenedPoint);
            }
        }
        
        // Create a spline curve through the profile points if we have enough points
        if (size(profilePoints) >= 2)
        {
            const splineDef = bSplineCurve({
                "degree" : min(3, size(profilePoints) - 1),
                "controlPoints" : profilePoints,
                "isPeriodic" : false
            });
            
            opCreateBSplineCurve(context, id + ("profile" ~ i), { "bSplineCurve" : splineDef });
            profiles = append(profiles, qCreatedBy(id + ("profile" ~ i), EntityType.EDGE));
        }
    }
    
    // Create a loft through all the profiles to form the tweened surface
    if (size(profiles) >= 2)
    {
        opLoft(context, id + "loft", {
            "profileSubqueries" : profiles
        });
    }
    else
    {
        throw regenError("Unable to create sufficient profile curves for the tweened surface. Try adjusting the sample resolution.");
    }
}


/**
 * Samples a point on a surface at given parametric coordinates.
 * Uses the evFaceTangentPlane function which samples a face using UV parameter space.
 * 
 * @param context {Context} : The context
 * @param face {Query} : The face to sample
 * @param u {number} : The u parameter (0 to 1)
 * @param v {number} : The v parameter (0 to 1)
 * @returns {Vector} : The sampled point, or undefined if sampling fails
 */
function sampleSurfacePoint(context is Context, face is Query, u is number, v is number) returns Vector
{
    try
    {
        // Use evFaceTangentPlane to sample the surface at the parametric position
        // The parameter is a 2D vector in parameter space coordinates
        const tangentPlane = evFaceTangentPlane(context, {
            "face" : face,
            "parameter" : vector(u, v)
        });
        
        // Return the origin of the tangent plane, which is the point on the surface
        return tangentPlane.origin;
    }
    catch
    {
        // If sampling fails (e.g., parameter is outside valid range), return undefined
        return undefined;
    }
}
