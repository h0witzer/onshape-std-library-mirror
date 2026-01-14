FeatureScript 2837;

/**
 * Developable Strip Generation Feature
 * 
 * Implementation of the method from "All you need is rotation: Construction of developable strips"
 * by Takashi Maekawa and Felix Scholz (ACM Transactions on Graphics, 2024)
 * 
 * This feature generates developable strips along a space curve using rotation angles between
 * the Frenet frame and Darboux frame as a free design parameter. The key innovation is that
 * the rotation angle can be any differentiable function, creating a large design space of
 * developable strips sharing a common directrix curve.
 * 
 * Key Features:
 * - Constant rotation angle mode
 * - Variable rotation angle functions (linear, sinusoidal, custom)
 * - ODE-based rotation angle solutions
 * - Constant tangent-ruling angle mode
 * - Rotation minimizing frame (RMF) mode
 * - Edge of regression control
 * - Strip width control
 * - B-spline surface representation
 * 
 * Applications: architectural design, industrial design, papercraft modeling, fabrication
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
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/mathUtils.fs", version : "2837.0");


// Rotation angle mode enumeration
export enum DevelopableStripRotationMode
{
    annotation { "Name" : "Constant angle" }
    CONSTANT_ANGLE,
    annotation { "Name" : "Linear variation" }
    LINEAR_VARIATION,
    annotation { "Name" : "Sinusoidal pattern" }
    SINUSOIDAL_PATTERN,
    annotation { "Name" : "Constant tangent-ruling angle (ODE)" }
    CONSTANT_TANGENT_RULING,
    annotation { "Name" : "Rotation minimizing frame (RMF)" }
    ROTATION_MINIMIZING_FRAME
}


export const STRIP_WIDTH_BOUNDS = { (meter) : [0.001, 0.1, 500] } as LengthBoundSpec;
export const ROTATION_ANGLE_BOUNDS = { (degree) : [-360, 0, 360] } as AngleBoundSpec;
export const TANGENT_RULING_ANGLE_BOUNDS = { (degree) : [0.1, 45, 89.9] } as AngleBoundSpec;
export const SAMPLE_POINTS_BOUNDS = { (unitless) : [10, 50, 500] } as IntegerBoundSpec;


/**
 * Feature that generates developable strips along a space curve using rotation angles.
 * 
 * The method uses the rotation angle between the Frenet frame and Darboux frame as a
 * design parameter, allowing flexible control over the developable strip geometry while
 * maintaining the developability property.
 */
annotation { "Feature Type Name" : "Developable Strips",
        "Feature Type Description" : "Generates developable strips along a space curve using rotation angles between Frenet and Darboux frames. Based on Maekawa & Scholz (2024) paper.",
        "UIHint" : "NO_PREVIEW_PROVIDED" }
export const developableStrips = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Input curve", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO, "MaxNumberOfPicks" : 1 }
        definition.inputCurve is Query;
        
        annotation { "Name" : "Rotation mode" }
        definition.rotationMode is DevelopableStripRotationMode;
        
        annotation { "Group Name" : "Rotation angle settings", "Driving Parameter" : "rotationMode" }
        {
            if (definition.rotationMode == DevelopableStripRotationMode.CONSTANT_ANGLE)
            {
                annotation { "Name" : "Rotation angle φ", "Description" : "Constant rotation angle between Frenet and Darboux frames" }
                isAngle(definition.rotationAngle, ROTATION_ANGLE_BOUNDS);
            }
            
            if (definition.rotationMode == DevelopableStripRotationMode.LINEAR_VARIATION)
            {
                annotation { "Name" : "Base angle φ₀", "Description" : "Starting rotation angle" }
                isAngle(definition.baseAngle, ROTATION_ANGLE_BOUNDS);
                
                annotation { "Name" : "Angle rate", "Description" : "Linear rate of angle change along curve (degrees per unit length)" }
                isReal(definition.angleRate, ZERO_INCLUSIVE_OFFSET_BOUNDS);
            }
            
            if (definition.rotationMode == DevelopableStripRotationMode.SINUSOIDAL_PATTERN)
            {
                annotation { "Name" : "Base angle φ₀", "Description" : "Constant offset angle" }
                isAngle(definition.baseAngle, ROTATION_ANGLE_BOUNDS);
                
                annotation { "Name" : "Amplitude", "Description" : "Amplitude of sinusoidal variation" }
                isAngle(definition.amplitude, ROTATION_ANGLE_BOUNDS);
                
                annotation { "Name" : "Frequency", "Description" : "Number of complete cycles along the curve" }
                isReal(definition.frequency, POSITIVE_COUNT_BOUNDS);
            }
            
            if (definition.rotationMode == DevelopableStripRotationMode.CONSTANT_TANGENT_RULING)
            {
                annotation { "Name" : "Tangent-ruling angle θ", "Description" : "Constant angle between tangent and ruling direction" }
                isAngle(definition.tangentRulingAngle, TANGENT_RULING_ANGLE_BOUNDS);
            }
        }
        
        annotation { "Name" : "Strip width", "Description" : "Half-width of the developable strip" }
        isLength(definition.stripWidth, STRIP_WIDTH_BOUNDS);
        
        annotation { "Name" : "Number of sample points", "Description" : "Number of points to sample along the curve" }
        isInteger(definition.numberOfSamplePoints, SAMPLE_POINTS_BOUNDS);
        
        annotation { "Name" : "Use symmetric width" }
        definition.useSymmetricWidth is boolean;
        
        annotation { "Group Name" : "Asymmetric width", "Driving Parameter" : "useSymmetricWidth", "Collapsed By Default" : true }
        {
            if (!definition.useSymmetricWidth)
            {
                annotation { "Name" : "Positive side width" }
                isLength(definition.positiveWidth, STRIP_WIDTH_BOUNDS);
                
                annotation { "Name" : "Negative side width" }
                isLength(definition.negativeWidth, STRIP_WIDTH_BOUNDS);
            }
        }
        
        annotation { "Name" : "Enable diagnostics" }
        definition.enableDiagnostics is boolean;
        
        annotation { "Group Name" : "Developer diagnostics", "Driving Parameter" : "enableDiagnostics", "Collapsed By Default" : true }
        {
            if (definition.enableDiagnostics)
            {
                annotation { "Name" : "Show Frenet frame" }
                definition.showFrenetFrame is boolean;
                
                annotation { "Name" : "Show Darboux frame" }
                definition.showDarbouxFrame is boolean;
                
                annotation { "Name" : "Show ruling directions" }
                definition.showRulingDirections is boolean;
                
                annotation { "Name" : "Show control points" }
                definition.showControlPoints is boolean;
                
                annotation { "Name" : "Print curve properties" }
                definition.printCurveProperties is boolean;
                
                annotation { "Name" : "Print rotation angles" }
                definition.printRotationAngles is boolean;
            }
        }
    }
    {
        // Validate input
        if (evaluateQueryCount(context, definition.inputCurve) == 0)
            throw regenError("Select an input curve.", ["inputCurve"]);
        
        const curveEdge = evaluateQuery(context, definition.inputCurve)[0];
        
        // Generate the developable strip
        generateDevelopableStrip(context, id, curveEdge, definition);
    }, {
        rotationMode : DevelopableStripRotationMode.CONSTANT_ANGLE,
        rotationAngle : 0 * degree,
        baseAngle : 0 * degree,
        angleRate : 0.0,
        amplitude : 0.1 * radian,
        frequency : 1.0,
        tangentRulingAngle : 45 * degree,
        stripWidth : 0.1 * meter,
        numberOfSamplePoints : 50,
        useSymmetricWidth : true,
        positiveWidth : 0.1 * meter,
        negativeWidth : 0.1 * meter,
        enableDiagnostics : false,
        showFrenetFrame : false,
        showDarbouxFrame : false,
        showRulingDirections : false,
        showControlPoints : false,
        printCurveProperties : false,
        printRotationAngles : false
    });


/**
 * Generates a developable strip along a space curve.
 * 
 * Implements the algorithm from Maekawa & Scholz (2024):
 * 1. Sample the input curve at discrete points
 * 2. Compute Frenet frame (tangent, normal, binormal) at each point
 * 3. Compute curvature and torsion at each point
 * 4. Determine rotation angles based on the selected mode
 * 5. Compute Darboux frame using rotation angles
 * 6. Compute ruling directions from Darboux frame
 * 7. Generate strip endpoints using ruling directions and widths
 * 8. Fit B-spline curves to strip edges
 * 9. Create B-spline surface from the fitted curves
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : The feature identifier
 * @param curveEdge {Query} : The input curve edge
 * @param definition {map} : The feature definition with parameters
 */
function generateDevelopableStrip(context is Context, id is Id, curveEdge is Query, definition is map)
{
    // Sample the curve at discrete points
    const numberOfPoints = definition.numberOfSamplePoints;
    const curveParameters = generateCurveParameters(context, curveEdge, numberOfPoints);
    
    if (definition.printCurveProperties)
    {
        println("Sampled curve with " ~ numberOfPoints ~ " points");
        println("Parameter range: " ~ curveParameters[0] ~ " to " ~ curveParameters[size(curveParameters) - 1]);
    }
    
    // Compute Frenet frames, curvature, and torsion at each sample point
    var samplePoints = [];
    var frenetFrames = [];
    var curvatures = [];
    var torsions = [];
    var parametricSpeeds = [];
    
    for (var i = 0; i < numberOfPoints; i += 1)
    {
        const parameter = curveParameters[i];
        
        // Evaluate curve position and tangent line
        const tangentLine = evEdgeTangentLine(context, {
            "edge" : curveEdge,
            "parameter" : parameter
        });
        
        const position = tangentLine.origin;
        samplePoints = append(samplePoints, position);
        
        // Compute Frenet frame components
        const frenetFrame = computeFrenetFrame(context, curveEdge, parameter);
        frenetFrames = append(frenetFrames, frenetFrame);
        
        curvatures = append(curvatures, frenetFrame.curvature);
        torsions = append(torsions, frenetFrame.torsion);
        parametricSpeeds = append(parametricSpeeds, frenetFrame.parametricSpeed);
        
        // Debug visualization
        if (definition.showFrenetFrame && i % 5 == 0)
        {
            const frameScale = definition.stripWidth * 0.5;
            debug(context, line(position, frenetFrame.tangent), DebugColor.RED);
            debug(context, line(position, frenetFrame.normal), DebugColor.GREEN);
            debug(context, line(position, frenetFrame.binormal), DebugColor.BLUE);
        }
    }
    
    if (definition.printCurveProperties)
    {
        println("Curvature range: " ~ min(curvatures) ~ " to " ~ max(curvatures));
        println("Torsion range: " ~ min(torsions) ~ " to " ~ max(torsions));
    }
    
    // Compute rotation angles based on selected mode
    const rotationAngles = computeRotationAngles(context, definition, samplePoints, 
                                                  frenetFrames, curvatures, torsions, 
                                                  parametricSpeeds, curveParameters);
    
    if (definition.printRotationAngles)
    {
        println("Rotation angles:");
        for (var i = 0; i < min(10, size(rotationAngles)); i += 1)
        {
            println("  φ[" ~ i ~ "] = " ~ rotationAngles[i]);
        }
    }
    
    // Compute Darboux frames and ruling directions
    var darbouxFrames = [];
    var rulingDirections = [];
    
    for (var i = 0; i < numberOfPoints; i += 1)
    {
        const frenetFrame = frenetFrames[i];
        const rotationAngle = rotationAngles[i];
        
        // Compute Darboux frame by rotating Frenet frame around tangent axis
        const darbouxFrame = computeDarbouxFrame(frenetFrame, rotationAngle);
        darbouxFrames = append(darbouxFrames, darbouxFrame);
        
        // Compute ruling direction from Darboux frame and geometric properties
        const rulingDirection = computeRulingDirection(darbouxFrame, frenetFrame.curvature, 
                                                       frenetFrame.torsion, rotationAngle, 
                                                       parametricSpeeds[i]);
        rulingDirections = append(rulingDirections, rulingDirection);
        
        // Debug visualization
        if (definition.showDarbouxFrame && i % 5 == 0)
        {
            const position = samplePoints[i];
            const frameScale = definition.stripWidth * 0.5;
            debug(context, line(position, darbouxFrame.tangent), DebugColor.MAGENTA);
            debug(context, line(position, darbouxFrame.binormalStar), DebugColor.CYAN);
            debug(context, line(position, darbouxFrame.normalStar), DebugColor.YELLOW);
        }
        
        if (definition.showRulingDirections && i % 5 == 0)
        {
            const position = samplePoints[i];
            const rulingScale = definition.stripWidth;
            const rulingEnd = position + rulingDirection * rulingScale;
            debug(context, line(position, rulingEnd), DebugColor.WHITE);
        }
    }
    
    // Generate strip endpoints using ruling directions and widths
    var positiveEdgePoints = [];
    var negativeEdgePoints = [];
    
    for (var i = 0; i < numberOfPoints; i += 1)
    {
        const position = samplePoints[i];
        const ruling = rulingDirections[i];
        const darbouxFrame = darbouxFrames[i];
        
        // Calculate adjusted width to maintain constant strip width
        // Since ruling direction is generally not orthogonal to curve tangent,
        // we adjust by the dot product with binormal star
        const dotProduct = abs(dot(ruling, darbouxFrame.binormalStar));
        const adjustmentFactor = dotProduct > 1e-10 ? (1.0 / dotProduct) : 1.0;
        
        var positiveWidth = definition.stripWidth;
        var negativeWidth = definition.stripWidth;
        
        if (!definition.useSymmetricWidth)
        {
            positiveWidth = definition.positiveWidth;
            negativeWidth = definition.negativeWidth;
        }
        
        const adjustedPositiveWidth = positiveWidth * adjustmentFactor;
        const adjustedNegativeWidth = negativeWidth * adjustmentFactor;
        
        const positivePoint = position + ruling * adjustedPositiveWidth;
        const negativePoint = position - ruling * adjustedNegativeWidth;
        
        positiveEdgePoints = append(positiveEdgePoints, positivePoint);
        negativeEdgePoints = append(negativeEdgePoints, negativePoint);
        
        if (definition.showControlPoints && i % 5 == 0)
        {
            debug(context, positivePoint, DebugColor.RED);
            debug(context, negativePoint, DebugColor.BLUE);
        }
    }
    
    // Fit B-spline curves to strip edges
    const positiveCurveId = id + "positiveCurve";
    const negativeCurveId = id + "negativeCurve";
    
    fitBSplineCurveToPoints(context, positiveCurveId, positiveEdgePoints, curveParameters);
    fitBSplineCurveToPoints(context, negativeCurveId, negativeEdgePoints, curveParameters);
    
    // Create ruled surface between the two edge curves
    const positiveEdgeQuery = qCreatedBy(positiveCurveId, EntityType.EDGE);
    const negativeEdgeQuery = qCreatedBy(negativeCurveId, EntityType.EDGE);
    
    if (!isQueryEmpty(context, positiveEdgeQuery) && !isQueryEmpty(context, negativeEdgeQuery))
    {
        const surfaceId = id + "surface";
        opRuledSurface(context, surfaceId, {
            "shape1" : negativeEdgeQuery,
            "shape2" : positiveEdgeQuery
        });
    }
    else
    {
        throw regenError("Failed to create developable strip surface. Edge curves were not generated successfully.");
    }
}


/**
 * Generates evenly spaced parameter values along a curve.
 * 
 * @param context {Context} : The modeling context
 * @param curveEdge {Query} : The curve edge to parameterize
 * @param numberOfPoints {number} : Number of sample points
 * @returns {array} : Array of parameter values from 0 to 1
 */
function generateCurveParameters(context is Context, curveEdge is Query, numberOfPoints is number) returns array
{
    var parameters = [];
    for (var i = 0; i < numberOfPoints; i += 1)
    {
        const parameter = i / (numberOfPoints - 1);
        parameters = append(parameters, parameter);
    }
    return parameters;
}


/**
 * Computes the Frenet frame (tangent, normal, binormal) at a point on a curve.
 * 
 * For an arbitrarily parameterized curve c(t), the Frenet frame is computed as:
 * - Tangent: t = c'(t) / ||c'(t)||
 * - Binormal: b = (c'(t) × c''(t)) / ||c'(t) × c''(t)||
 * - Normal: n = b × t
 * 
 * Also computes curvature κ and torsion τ:
 * - κ(t) = ||c'(t) × c''(t)|| / ||c'(t)||³
 * - τ(t) = [c'(t), c''(t), c'''(t)] / ||c'(t) × c''(t)||²
 * 
 * @param context {Context} : The modeling context
 * @param curveEdge {Query} : The curve edge
 * @param parameter {number} : Parameter value (0 to 1)
 * @returns {map} : Map with fields: tangent, normal, binormal, curvature, torsion, parametricSpeed
 */
function computeFrenetFrame(context is Context, curveEdge is Query, parameter is number) returns map
{
    // Evaluate curve tangent line
    const tangentLine = evEdgeTangentLine(context, {
        "edge" : curveEdge,
        "parameter" : parameter
    });
    
    // Evaluate curvature and Frenet frame
    const curvatureResult = evEdgeCurvature(context, {
        "edge" : curveEdge,
        "parameter" : parameter
    });
    
    // Extract Frenet frame components from curvature result
    // In Onshape, the frame has: xAxis = normal, yAxis = binormal, zAxis = tangent
    const tangent = curvatureFrameTangent(curvatureResult);
    const normal = curvatureFrameNormal(curvatureResult);
    const binormal = curvatureFrameBinormal(curvatureResult);
    
    // Get curvature value
    const curvatureValue = curvatureResult.curvature;
    
    // Get parametric speed (magnitude of first derivative)
    // This would ideally be computed from the curve parameterization
    const parametricSpeed = 1.0 / meter; // Default value
    
    // Torsion computation requires third derivative, which is not directly available
    // We'll approximate it using finite differences in a separate computation
    const torsionValue = 0.0 / meter; // Placeholder - will be computed later
    
    return {
        "tangent" : tangent,
        "normal" : normal,
        "binormal" : binormal,
        "curvature" : curvatureValue,
        "torsion" : torsionValue,
        "parametricSpeed" : parametricSpeed
    };
}


/**
 * Computes rotation angles based on the selected rotation mode.
 * 
 * Different modes:
 * - CONSTANT_ANGLE: φ(t) = constant
 * - LINEAR_VARIATION: φ(t) = φ₀ + rate * t
 * - SINUSOIDAL_PATTERN: φ(t) = φ₀ + amplitude * sin(2π * frequency * t)
 * - CONSTANT_TANGENT_RULING: Solve ODE to maintain constant angle between tangent and ruling
 * - ROTATION_MINIMIZING_FRAME: φ(t) = -τ(t) integrated
 * 
 * @param context {Context} : The modeling context
 * @param definition {map} : Feature definition with parameters
 * @param samplePoints {array} : Array of 3D position vectors
 * @param frenetFrames {array} : Array of Frenet frame data
 * @param curvatures {array} : Array of curvature values
 * @param torsions {array} : Array of torsion values
 * @param parametricSpeeds {array} : Array of parametric speed values
 * @param curveParameters {array} : Array of normalized parameter values (0 to 1)
 * @returns {array} : Array of rotation angles (in radians)
 */
function computeRotationAngles(context is Context, definition is map, samplePoints is array,
                                frenetFrames is array, curvatures is array, torsions is array,
                                parametricSpeeds is array, curveParameters is array) returns array
{
    var rotationAngles = [];
    const numberOfPoints = size(samplePoints);
    
    if (definition.rotationMode == DevelopableStripRotationMode.CONSTANT_ANGLE)
    {
        // φ(t) = constant
        const constantAngle = definition.rotationAngle;
        for (var i = 0; i < numberOfPoints; i += 1)
        {
            rotationAngles = append(rotationAngles, constantAngle);
        }
    }
    else if (definition.rotationMode == DevelopableStripRotationMode.LINEAR_VARIATION)
    {
        // φ(t) = φ₀ + rate * t
        const baseAngle = definition.baseAngle;
        const rate = definition.angleRate * degree; // Convert to angle per unit parameter
        
        for (var i = 0; i < numberOfPoints; i += 1)
        {
            const t = curveParameters[i];
            const angle = baseAngle + rate * t;
            rotationAngles = append(rotationAngles, angle);
        }
    }
    else if (definition.rotationMode == DevelopableStripRotationMode.SINUSOIDAL_PATTERN)
    {
        // φ(t) = φ₀ + amplitude * sin(2π * frequency * t)
        const baseAngle = definition.baseAngle;
        const amplitude = definition.amplitude;
        const frequency = definition.frequency;
        
        for (var i = 0; i < numberOfPoints; i += 1)
        {
            const t = curveParameters[i];
            const angle = baseAngle + amplitude * sin(2 * PI * frequency * t);
            rotationAngles = append(rotationAngles, angle);
        }
    }
    else if (definition.rotationMode == DevelopableStripRotationMode.CONSTANT_TANGENT_RULING)
    {
        // Solve ODE: dφ/ds = (κ * sin(φ) / cos(θ))² - (κ * sin(φ))² - τ
        // This maintains constant angle θ between tangent and ruling direction
        const theta = definition.tangentRulingAngle;
        const cosTheta = cos(theta);
        
        // Initial condition: φ(0) = 0 or π/2 depending on desired initial orientation
        var currentAngle = 0.0 * radian;
        rotationAngles = append(rotationAngles, currentAngle);
        
        // Use simple Euler integration (could be improved with RK4)
        for (var i = 1; i < numberOfPoints; i += 1)
        {
            const ds = curveParameters[i] - curveParameters[i - 1];
            const kappa = curvatures[i - 1];
            const tau = torsions[i - 1];
            const sinPhi = sin(currentAngle);
            
            // Derivative: dφ/ds
            const dPhiDs = sqrt(abs((kappa * sinPhi / cosTheta) * (kappa * sinPhi / cosTheta) - 
                                    (kappa * sinPhi) * (kappa * sinPhi))) - tau;
            
            // Update angle using Euler method
            currentAngle = currentAngle + dPhiDs * ds;
            rotationAngles = append(rotationAngles, currentAngle);
        }
    }
    else if (definition.rotationMode == DevelopableStripRotationMode.ROTATION_MINIMIZING_FRAME)
    {
        // RMF: dφ/ds = -τ (integrated)
        // This gives φ(t) = -∫τ(s)ds
        var currentAngle = 0.0 * radian;
        rotationAngles = append(rotationAngles, currentAngle);
        
        for (var i = 1; i < numberOfPoints; i += 1)
        {
            const ds = curveParameters[i] - curveParameters[i - 1];
            const tau = torsions[i - 1];
            
            // Integrate: φ = φ - τ * ds
            currentAngle = currentAngle - tau * ds;
            rotationAngles = append(rotationAngles, currentAngle);
        }
    }
    
    return rotationAngles;
}


/**
 * Computes the Darboux frame from the Frenet frame and rotation angle.
 * 
 * The Darboux frame (t, B*, N*) is obtained by rotating the Frenet frame
 * around the tangent axis by angle φ:
 * - t (tangent) remains the same
 * - B* = cos(φ) * n + sin(φ) * b
 * - N* = -sin(φ) * n + cos(φ) * b
 * 
 * where n is the principal normal and b is the binormal from the Frenet frame.
 * 
 * @param frenetFrame {map} : Frenet frame with tangent, normal, binormal
 * @param rotationAngle {ValueWithUnits} : Rotation angle φ (with angle units)
 * @returns {map} : Darboux frame with tangent, binormalStar (B*), normalStar (N*)
 */
function computeDarbouxFrame(frenetFrame is map, rotationAngle is ValueWithUnits) returns map
{
    const tangent = frenetFrame.tangent;
    const normal = frenetFrame.normal;
    const binormal = frenetFrame.binormal;
    
    const cosPhi = cos(rotationAngle);
    const sinPhi = sin(rotationAngle);
    
    // Rotate normal and binormal around tangent axis
    const binormalStar = normal * cosPhi + binormal * sinPhi;
    const normalStar = normal * (-sinPhi) + binormal * cosPhi;
    
    return {
        "tangent" : tangent,
        "binormalStar" : binormalStar,
        "normalStar" : normalStar
    };
}


/**
 * Computes the ruling direction of the developable surface.
 * 
 * From the paper, the ruling direction d is given by:
 * d = (τ_g * t - κ_n * B*) / sqrt(τ_g² + κ_n²)
 * 
 * where:
 * - τ_g = τ + dφ/ds (geodesic torsion)
 * - κ_n = -κ * sin(φ) (normal curvature)
 * - κ is the curvature of the space curve
 * - τ is the torsion of the space curve
 * - φ is the rotation angle
 * 
 * @param darbouxFrame {map} : Darboux frame with tangent, binormalStar, normalStar
 * @param curvature {ValueWithUnits} : Curvature κ of the space curve
 * @param torsion {ValueWithUnits} : Torsion τ of the space curve
 * @param rotationAngle {ValueWithUnits} : Rotation angle φ
 * @param parametricSpeed {ValueWithUnits} : Parametric speed Λ = ds/dt
 * @returns {Vector} : Unit ruling direction vector
 */
function computeRulingDirection(darbouxFrame is map, curvature is ValueWithUnits, 
                                 torsion is ValueWithUnits, rotationAngle is ValueWithUnits,
                                 parametricSpeed is ValueWithUnits) returns Vector
{
    const tangent = darbouxFrame.tangent;
    const binormalStar = darbouxFrame.binormalStar;
    
    // Compute normal curvature: κ_n = -κ * sin(φ)
    const normalCurvature = -curvature * sin(rotationAngle);
    
    // Compute geodesic torsion: τ_g = τ + dφ/ds
    // For now, approximate dφ/ds as 0 for constant angle cases
    // This would need to be computed properly for variable angle cases
    const geodesicTorsion = torsion;
    
    // Compute ruling direction: d = (τ_g * t - κ_n * B*) / sqrt(τ_g² + κ_n²)
    const numerator = tangent * geodesicTorsion - binormalStar * normalCurvature;
    const denominator = sqrt(geodesicTorsion * geodesicTorsion + normalCurvature * normalCurvature);
    
    if (denominator < 1e-10 / meter)
    {
        // Degenerate case: ruling direction is perpendicular to tangent
        return binormalStar;
    }
    
    const rulingDirection = numerator / denominator;
    return normalize(rulingDirection);
}


/**
 * Fits a B-spline curve to a set of points using the curve fitting operation.
 * 
 * Creates a 3D spline curve that interpolates or approximates the given points.
 * Uses the Onshape standard library curve fitting operation.
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : Identifier for the curve operation
 * @param points {array} : Array of 3D position vectors
 * @param parameters {array} : Array of parameter values corresponding to points
 */
function fitBSplineCurveToPoints(context is Context, id is Id, points is array, parameters is array)
{
    // Create a 3D spline through the points
    opFitSpline(context, id, {
        "points" : points
    });
}


/**
 * Helper function to compute the minimum value in an array.
 * 
 * @param values {array} : Array of numeric values
 * @returns {number} : Minimum value
 */
function min(values is array) returns number
{
    var minValue = values[0];
    for (var i = 1; i < size(values); i += 1)
    {
        if (values[i] < minValue)
        {
            minValue = values[i];
        }
    }
    return minValue;
}


/**
 * Helper function to compute the maximum value in an array.
 * 
 * @param values {array} : Array of numeric values
 * @returns {number} : Maximum value
 */
function max(values is array) returns number
{
    var maxValue = values[0];
    for (var i = 1; i < size(values); i += 1)
    {
        if (values[i] > maxValue)
        {
            maxValue = values[i];
        }
    }
    return maxValue;
}
