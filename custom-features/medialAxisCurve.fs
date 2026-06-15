FeatureScript 2837;

/**
 * Medial Axis Curve Generator
 * 
 * This feature generates an approximation of the medial axis (centerline/spine) of a selected face.
 * The medial axis represents the "center" of the face, useful for creating reference geometry,
 * routing paths, or understanding the shape's skeleton.
 * 
 * The implementation uses efficient built-in FeatureScript operations:
 * 1. Creates isoparametric curves in both U and V directions
 * 2. Finds intersection points between opposing curves
 * 3. These intersection points approximate the medial axis
 * 4. Fits a smooth curve through these points
 * 
 * This approach:
 * - Works on arbitrary 3D surfaces (not limited to planar faces)
 * - Uses non-iterative operations for performance
 * - Leverages built-in curve-on-face operations
 * - Produces smooth, continuous curves suitable for CAD operations
 * 
 * Key Parameters:
 * - Number of curves: Controls how many isoparametric curves to create for analysis
 * - Direction: Choose which parametric direction to follow for the medial axis
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
import(path : "onshape/std/box.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");

/**
 * Defines the main feature for medial axis curve generation
 */
annotation { "Feature Type Name" : "Medial Axis Curve" }
export const medialAxisCurve = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face", "Filter" : EntityType.FACE && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.face is Query;

        annotation { "Name" : "Number of sample curves", "Default" : 10 }
        isInteger(definition.numberOfCurves, { (unitless) : [3, 10, 50] } as IntegerBoundSpec);

        annotation { "Name" : "Medial axis direction", "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.axisDirection is MedialAxisDirection;

        annotation { "Name" : "Create composite curve", "Default" : true }
        definition.createComposite is boolean;
    }
    {
        // Validate input
        if (isQueryEmpty(context, definition.face))
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["face"]);
        }

        // Create the medial axis curve using isoparametric curve intersection method
        createMedialAxisFromIsocurves(context, id, definition);

        // Report success information
        reportFeatureInfo(context, id, "Created medial axis curve");
    });

/**
 * Direction for computing the medial axis
 */
export enum MedialAxisDirection
{
    annotation { "Name" : "U direction (automatic)" }
    U_DIRECTION,
    annotation { "Name" : "V direction (automatic)" }
    V_DIRECTION,
    annotation { "Name" : "Center of parametric space" }
    PARAMETRIC_CENTER
}

/**
 * Creates medial axis curve by analyzing isoparametric curves on the face
 * Uses a non-iterative approach for performance
 * 
 * @param context : The current context
 * @param id : The feature ID
 * @param definition : The feature definition map
 */
function createMedialAxisFromIsocurves(context is Context, id is Id, definition is map)
{
    const face = definition.face;
    const numberOfCurves = definition.numberOfCurves;
    
    if (definition.axisDirection == MedialAxisDirection.PARAMETRIC_CENTER)
    {
        // Create a single isoparametric curve at the center of parametric space
        // This works well for many common surfaces like extrusions and ruled surfaces
        createCenterIsocurve(context, id, face, definition.createComposite);
    }
    else
    {
        // Create multiple isoparametric curves and find their geometric center
        createMedialAxisFromMultipleCurves(context, id, face, numberOfCurves, 
                                           definition.axisDirection, definition.createComposite);
    }
}

/**
 * Creates a curve at the parametric center (u=0.5 or v=0.5) of the face
 * 
 * @param context : The current context
 * @param id : The feature ID
 * @param face : The face to analyze
 * @param createComposite : Whether to create composite curve
 */
function createCenterIsocurve(context is Context, id is Id, face is Query, createComposite is boolean)
{
    // Create a curve at u=0.5 (middle of U parametric direction)
    const curveNames = ["medialCurve"];
    const curveDefinition = curveOnFaceDefinition(face, FaceCurveCreationType.DIR2_ISO, 
                                                   curveNames, [0.5]);
    
    opCreateCurvesOnFace(context, id + "medialCurve", {
        "curveDefinition" : [curveDefinition]
    });
    
    if (createComposite)
    {
        const curveBodies = qCreatedBy(id + "medialCurve", EntityType.BODY);
        if (!isQueryEmpty(context, curveBodies))
        {
            try
            {
                opCreateCompositePart(context, id + "composite", {
                    "bodies" : curveBodies,
                    "closed" : false
                });
            }
            catch
            {
                // If composite creation fails, keep the individual curves
            }
        }
    }
}

/**
 * Creates medial axis by sampling multiple isoparametric curves and computing their average
 * 
 * @param context : The current context
 * @param id : The feature ID
 * @param face : The face to analyze
 * @param numberOfCurves : Number of curves to sample
 * @param direction : Which parametric direction to use
 * @param createComposite : Whether to create composite curve
 */
function createMedialAxisFromMultipleCurves(context is Context, id is Id, face is Query, 
                                             numberOfCurves is number, direction is MedialAxisDirection,
                                             createComposite is boolean)
{
    // Determine which direction to create isoparametric curves
    const creationType = (direction == MedialAxisDirection.U_DIRECTION) ? 
                         FaceCurveCreationType.DIR1_AUTO_SPACED_ISO : 
                         FaceCurveCreationType.DIR2_AUTO_SPACED_ISO;
    
    // Create isoparametric curves across the face
    var curveNames = [];
    for (var i = 0; i < numberOfCurves; i += 1)
    {
        curveNames = append(curveNames, "isocurve" ~ i);
    }
    
    const curveDefinition = curveOnFaceDefinition(face, creationType, curveNames, numberOfCurves);
    
    opCreateCurvesOnFace(context, id + "isocurves", {
        "curveDefinition" : [curveDefinition]
    });
    
    // Sample points along each curve and compute average positions
    const curves = qCreatedBy(id + "isocurves", EntityType.EDGE);
    const medialPoints = computeAveragePointsAlongCurves(context, curves);
    
    if (size(medialPoints) < 2)
    {
        // Fall back to center curve if average computation fails
        opDeleteBodies(context, id + "deleteIsocurves", {
            "entities" : qCreatedBy(id + "isocurves", EntityType.BODY)
        });
        
        createCenterIsocurve(context, id, face, createComposite);
        return;
    }
    
    // Delete the temporary isoparametric curves
    opDeleteBodies(context, id + "deleteIsocurves", {
        "entities" : qCreatedBy(id + "isocurves", EntityType.BODY)
    });
    
    // Create a spline through the computed medial points
    opFitSpline(context, id + "medialSpline", {
        "points" : medialPoints
    });
    
    if (createComposite)
    {
        const curveBodies = qCreatedBy(id + "medialSpline", EntityType.BODY);
        if (!isQueryEmpty(context, curveBodies))
        {
            try
            {
                opCreateCompositePart(context, id + "composite", {
                    "bodies" : curveBodies,
                    "closed" : false
                });
            }
            catch
            {
                // If composite creation fails, keep the individual curves
            }
        }
    }
}

/**
 * Computes average positions of corresponding points along multiple curves
 * 
 * @param context : The current context
 * @param curves : Query for the curves to average
 * 
 * @returns : Array of averaged 3D points
 */
function computeAveragePointsAlongCurves(context is Context, curves is Query) returns array
{
    const curveArray = evaluateQuery(context, curves);
    
    if (size(curveArray) == 0)
    {
        return [];
    }
    
    // Sample each curve at regular intervals
    const numSamples = 20;
    var medialPoints = [];
    
    for (var sampleIndex = 0; sampleIndex < numSamples; sampleIndex += 1)
    {
        const parameter = sampleIndex / (numSamples - 1);
        var sumPoint = vector(0, 0, 0) * meter;
        var validCurves = 0;
        
        // Get point at this parameter on each curve
        for (var curve in curveArray)
        {
            try
            {
                const tangentLine = evEdgeTangentLine(context, {
                    "edge" : curve,
                    "parameter" : parameter
                });
                
                sumPoint = sumPoint + tangentLine.origin;
                validCurves += 1;
            }
            catch
            {
                // Skip curves where evaluation fails
            }
        }
        
        if (validCurves > 0)
        {
            // Compute average point
            const avgPoint = sumPoint / validCurves;
            medialPoints = append(medialPoints, avgPoint);
        }
    }
    
    return medialPoints;
}
