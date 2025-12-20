FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/curvetype.gen.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/splineUtils.fs", version : "2837.0");

/**
 * Kerf bending utilities using analytical NURBS-based approach for performance.
 * This implementation leverages FeatureScript's curve evaluation functions to
 * directly calculate cut positions without discretization.
 */

/**
 * Calculate the kerf angle - the angle by which a single cut bends the material.
 * 
 * @param cutWidth : The thickness of the blade/cutting tool with length units
 * @param cutDepth : The depth of the cut (should be 10-15% less than material thickness) with length units
 * @returns {ValueWithUnits} : The kerf angle in radians representing how much one cut bends the material
 */
export function calculateKerfAngle(cutWidth is ValueWithUnits, cutDepth is ValueWithUnits) returns ValueWithUnits
precondition
{
    isLength(cutWidth);
    isLength(cutDepth);
    cutWidth > 0 * meter;
    cutDepth > 0 * meter;
}
{
    return 2 * atan2(cutWidth, 2 * cutDepth);
}

/**
 * Type representing the complete kerf bending solution for a curve.
 * 
 * @type {{
 *      @field cutParameters {array} : Array of curve parameters (0 to 1) where cuts should be made
 *      @field cutPositions {array} : Array of 3D positions where cuts should be made with length units
 *      @field cutDistances {array} : Array of distances between consecutive cuts with length units
 *      @field totalLength {ValueWithUnits} : Total length of the curve with length units
 *      @field numberOfCuts {number} : Total number of cuts required
 *      @field kerfAngle {ValueWithUnits} : The kerf angle used in the calculation with angle units
 *      @field curvatureSigns {array} : Array of curvature signs at each cut location
 * }}
 */
export type KerfBendingSolution typecheck canBeKerfBendingSolution;

export predicate canBeKerfBendingSolution(value)
{
    value is map;
    value.cutParameters is array;
    value.cutPositions is array;
    value.cutDistances is array;
    isLength(value.totalLength);
    value.numberOfCuts is number;
    isAngle(value.kerfAngle);
    value.curvatureSigns is array;
}

/**
 * Generate kerf bending solution using analytical curve evaluation.
 * Uses FeatureScript's NURBS utilities for direct calculation without discretization.
 * 
 * @param context : The context
 * @param curveEdge : Query for the curve edge
 * @param cutWidth : The thickness of the cutting tool with length units
 * @param cutDepth : The depth of the cut with length units
 * @param minimumCutSpacing : Minimum distance between cuts (default: 2 * cutWidth) with length units
 * @returns {KerfBendingSolution} : Complete solution containing all cut information
 */
export function generateAnalyticalKerfSolution(context is Context,
                                              curveEdge is Query,
                                              cutWidth is ValueWithUnits,
                                              cutDepth is ValueWithUnits,
                                              minimumCutSpacing is ValueWithUnits) returns KerfBendingSolution
precondition
{
    isLength(cutWidth);
    isLength(cutDepth);
    isLength(minimumCutSpacing);
    cutWidth > 0 * meter;
    cutDepth > 0 * meter;
    minimumCutSpacing > 0 * meter;
}
{
    // Calculate kerf angle
    const kerfAngle = calculateKerfAngle(cutWidth, cutDepth);
    
    // Get total curve length
    const totalLength = evLength(context, { "entities" : curveEdge });
    
    // Check if curve is a circle or arc - use analytical solution for constant curvature
    const curveDefinition = evCurveDefinition(context, { "edge" : curveEdge });
    if (curveDefinition.curveType == CurveType.CIRCLE)
    {
        // Circle has constant curvature = 1/radius
        // For circles/arcs, we can directly calculate cut spacing analytically
        return generateCircularKerfSolution(context, curveEdge, kerfAngle, minimumCutSpacing, totalLength, curveDefinition, useHalfKerfOffset);
    }
    
    // Start from parameter 0 and walk along curve
    // With arc length parameterization, parameters are unitless arc length values
    var cutParameters = [0.0];
    var cutPositions = [];
    var curvatureSigns = [];
    
    // Get initial position - parameter is unitless when arcLengthParameterization is true
    var currentParam = 0.0;
    var tangentLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
    cutPositions = append(cutPositions, tangentLine.origin);
    
    // Get initial curvature
    var curvatureResult = evEdgeCurvature(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
    const initialCurvatureSign = getCurvatureSign(curvatureResult.curvature);
    curvatureSigns = append(curvatureSigns, initialCurvatureSign);
    
    // Walk along curve by integrating tangent angle changes
    // With arcLengthParameterization: true, parameters range from 0 to 1
    // Use adaptive step sizing based on curvature for efficiency
    const baseParameterStepSize = 0.01; // Base step (1% of curve) - adaptive based on curvature
    const minParameterStepSize = 0.001; // Minimum step in high curvature regions (0.1%)
    var accumulatedAngle = 0.0 * radian;
    var previousTangent = tangentLine.direction;
    var lastCutParam = 0.0;
    
    currentParam = baseParameterStepSize;
    while (currentParam <= 1.0)
    {
        // Get tangent at current parameter
        tangentLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
        const currentTangent = tangentLine.direction;
        
        // Calculate angle change between previous and current tangent
        // Using acos(dot product) for angle between unit vectors
        const dotProduct = dot(previousTangent, currentTangent);
        const clampedDot = max(-1.0, min(1.0, dotProduct)); // Clamp to valid acos domain
        const angleChange = acos(clampedDot); // acos returns radians directly
        
        accumulatedAngle += angleChange;
        
        // Check if we've accumulated enough angle for a cut
        if (accumulatedAngle >= kerfAngle)
        {
            // Check minimum spacing constraint
            const distanceFromLastCut = (currentParam - lastCutParam) * totalLength;
            
            if (distanceFromLastCut >= minimumCutSpacing)
            {
                // Add cut at current position
                cutParameters = append(cutParameters, currentParam);
                cutPositions = append(cutPositions, tangentLine.origin);
                
                // Get curvature sign at this point
                curvatureResult = evEdgeCurvature(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
                const curvatureSign = getCurvatureSign(curvatureResult.curvature);
                curvatureSigns = append(curvatureSigns, curvatureSign);
                
                // Reset accumulated angle and update last cut parameter
                accumulatedAngle = 0.0 * radian;
                lastCutParam = currentParam;
            }
        }
        
        // Update for next iteration
        previousTangent = currentTangent;
        
        // Adaptive step sizing: use larger steps in low curvature regions, smaller in high curvature
        // Sample curvature at current point to adjust next step
        curvatureResult = evEdgeCurvature(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
        const curvatureMagnitude = abs(curvatureResult.curvature);
        
        // Adjust step size: smaller steps when curvature is high
        // If curvature is near zero (straight line or inflection), use large steps
        // If curvature is high, use small steps
        var adaptiveStepSize = baseParameterStepSize;
        if (curvatureMagnitude > 1.0 / meter)
        {
            // Scale step inversely with curvature magnitude
            // Higher curvature → smaller steps
            const curvatureScale = min(1.0, 10.0 / (curvatureMagnitude * meter + 10.0));
            adaptiveStepSize = minParameterStepSize + (baseParameterStepSize - minParameterStepSize) * curvatureScale;
        }
        
        currentParam = currentParam + adaptiveStepSize;
    }
    
    // Always add end point if we haven't already and we have at least one cut
    if (@size(cutParameters) > 0)
    {
        // Check if the last parameter is not already at the end
        const lastParam = cutParameters[@size(cutParameters) - 1];
        
        if (lastParam < 1.0)
        {
            tangentLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : 1.0, "arcLengthParameterization" : true });
            const lastPos = cutPositions[@size(cutPositions) - 1];
            const endDist = norm(tangentLine.origin - lastPos);
            
            if (endDist >= minimumCutSpacing)
            {
                cutParameters = append(cutParameters, 1.0);
                cutPositions = append(cutPositions, tangentLine.origin);
                curvatureResult = evEdgeCurvature(context, { "edge" : curveEdge, "parameter" : 1.0, "arcLengthParameterization" : true });
                curvatureSigns = append(curvatureSigns, getCurvatureSign(curvatureResult.curvature));
            }
        }
    }
    
    // Calculate distances between cuts
    var cutDistances = [];
    for (var i = 0; i < @size(cutPositions) - 1; i += 1)
    {
        const distance = norm(cutPositions[i + 1] - cutPositions[i]);
        cutDistances = append(cutDistances, distance);
    }
    
    return {
        "cutParameters" : cutParameters,
        "cutPositions" : cutPositions,
        "cutDistances" : cutDistances,
        "totalLength" : totalLength,
        "numberOfCuts" : @size(cutParameters),
        "kerfAngle" : kerfAngle,
        "curvatureSigns" : curvatureSigns
    } as KerfBendingSolution;
}

/**
 * Analytical solution for circles and arcs with constant curvature.
 * Much faster than integration since curvature is constant.
 */
function generateCircularKerfSolution(context is Context,
                                     curveEdge is Query,
                                     kerfAngle is ValueWithUnits,
                                     minimumCutSpacing is ValueWithUnits,
                                     totalLength is ValueWithUnits,
                                     curveDefinition is map) returns KerfBendingSolution
{
    // For circles, curvature is constant = 1/radius
    // Arc length between cuts = kerfAngle / curvature = kerfAngle * radius
    const radius = curveDefinition.radius;
    const curvature = 1.0 / radius;
    var arcLengthBetweenCuts = kerfAngle * radius;
    
    // Respect minimum spacing constraint
    if (arcLengthBetweenCuts < minimumCutSpacing)
    {
        arcLengthBetweenCuts = minimumCutSpacing;
    }
    
    // Calculate number of cuts needed
    const numberOfCuts = ceil(totalLength / arcLengthBetweenCuts);
    const actualSpacing = totalLength / numberOfCuts;
    
    // Generate evenly-spaced cut positions
    var cutParameters = [];
    var cutPositions = [];
    var curvatureSigns = [];
    
    // Calculate offset for half-kerf on ends if enabled
    const startOffset = useHalfKerfOffset ? (actualSpacing / (2 * totalLength)) : 0.0;
    
    for (var i = 0; i <= numberOfCuts; i += 1)
    {
        const parameter = startOffset + (i / numberOfCuts) * (1.0 - 2 * startOffset); // Normalized 0-1 range with optional offset
        cutParameters = append(cutParameters, parameter);
        
        const tangentLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : parameter, "arcLengthParameterization" : true });
        cutPositions = append(cutPositions, tangentLine.origin);
        
        const curvatureResult = evEdgeCurvature(context, { "edge" : curveEdge, "parameter" : parameter, "arcLengthParameterization" : true });
        curvatureSigns = append(curvatureSigns, getCurvatureSign(curvatureResult.curvature));
    }
    
    // Calculate distances between cuts
    var cutDistances = [];
    for (var i = 0; i < @size(cutPositions) - 1; i += 1)
    {
        const distance = norm(cutPositions[i + 1] - cutPositions[i]);
        cutDistances = append(cutDistances, distance);
    }
    
    return {
        "cutParameters" : cutParameters,
        "cutPositions" : cutPositions,
        "cutDistances" : cutDistances,
        "totalLength" : totalLength,
        "numberOfCuts" : @size(cutParameters),
        "kerfAngle" : kerfAngle,
        "curvatureSigns" : curvatureSigns
    } as KerfBendingSolution;
}

/**
 * Overloaded version with default minimum spacing
 */
export function generateAnalyticalKerfSolution(context is Context,
                                              curveEdge is Query,
                                              cutWidth is ValueWithUnits,
                                              cutDepth is ValueWithUnits) returns KerfBendingSolution
precondition
{
    isLength(cutWidth);
    isLength(cutDepth);
    cutWidth > 0 * meter;
    cutDepth > 0 * meter;
}
{
    return generateAnalyticalKerfSolution(context, curveEdge, cutWidth, cutDepth, cutWidth * 2);
}

/**
 * Get the sign of curvature for visualization.
 * Curvature has units of 1/meter, so we need to normalize it.
 */
function getCurvatureSign(curvature is ValueWithUnits) returns number
{
    // curvature has units of 1/meter, multiply by meter to get unitless value
    const curvVal = curvature * meter;
    if (curvVal > 1e-6)
    {
        return 1;
    }
    else if (curvVal < -1e-6)
    {
        return -1;
    }
    else
    {
        return 0;
    }
}

/**
 * Calculate the flattened cut positions along a linear axis.
 * 
 * @param cutDistances : Array of distances between consecutive cuts with length units
 * @param centerOrigin : If true, positions are centered around zero; if false, start from zero
 * @returns {array} : Array of 1D positions with length units where cuts should be made
 */
export function calculateFlattenedCutPositions(cutDistances is array, centerOrigin is boolean) returns array
precondition
{
    @size(cutDistances) >= 0;
    for (var distance in cutDistances)
        isLength(distance);
}
{
    // Handle edge case of empty cutDistances (single point)
    if (@size(cutDistances) == 0)
    {
        return [0 * meter];
    }
    
    var totalLength = 0 * meter;
    for (var distance in cutDistances)
    {
        totalLength = totalLength + distance;
    }
    
    var currentPosition = centerOrigin ? -totalLength / 2 : 0 * meter;
    var flattenedPositions = [currentPosition];
    
    for (var distance in cutDistances)
    {
        currentPosition = currentPosition + distance;
        flattenedPositions = append(flattenedPositions, currentPosition);
    }
    
    return flattenedPositions;
}

/**
 * Create a summary map with key information about a kerf bending solution.
 * 
 * @param solution : A KerfBendingSolution object
 * @returns {map} : Map containing formatted summary information
 */
export function createKerfBendingSummary(solution is KerfBendingSolution) returns map
{
    var minSpacing = solution.totalLength;
    var maxSpacing = 0 * meter;
    
    for (var distance in solution.cutDistances)
    {
        if (distance < minSpacing)
        {
            minSpacing = distance;
        }
        if (distance > maxSpacing)
        {
            maxSpacing = distance;
        }
    }
    
    const avgSpacing = solution.numberOfCuts > 1 ? 
        solution.totalLength / (solution.numberOfCuts - 1) : 
        solution.totalLength;
    
    return {
        "totalLength" : solution.totalLength,
        "numberOfCuts" : solution.numberOfCuts,
        "kerfAngleDegrees" : solution.kerfAngle / degree,
        "averageCutSpacing" : avgSpacing,
        "minimumCutSpacing" : minSpacing,
        "maximumCutSpacing" : maxSpacing
    };
}

