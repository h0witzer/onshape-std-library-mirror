FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");

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
    
    // Start from parameter 0 and walk along curve
    var cutParameters = [0.0];
    var cutPositions = [];
    var curvatureSigns = [];
    
    // Get initial position
    var currentParam = 0.0;
    var tangentLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
    cutPositions = append(cutPositions, tangentLine.origin);
    
    // Get initial curvature
    var curvatureResult = evEdgeCurvature(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
    const initialCurvatureSign = getCurvatureSign(curvatureResult.curvature);
    curvatureSigns = append(curvatureSigns, initialCurvatureSign);
    
    // Walk along curve, adding cuts based on curvature
    while (currentParam < 1.0)
    {
        // Get curvature at current point
        curvatureResult = evEdgeCurvature(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
        const curvatureMagnitude = abs(curvatureResult.curvature);
        
        // Calculate arc length to next cut
        // For constant curvature: arcLength = kerfAngle / curvature
        // Use minimum spacing as fallback for low curvature
        var arcLengthToNextCut;
        if (curvatureMagnitude > (1e-6 / meter))
        {
            arcLengthToNextCut = abs(kerfAngle / curvatureMagnitude);
            // Ensure we don't go below minimum spacing
            arcLengthToNextCut = max(arcLengthToNextCut, minimumCutSpacing);
        }
        else
        {
            // Very low curvature - use minimum spacing
            arcLengthToNextCut = minimumCutSpacing;
        }
        
        // Convert arc length to parameter delta (approximate)
        const paramDelta = arcLengthToNextCut / totalLength;
        currentParam = currentParam + paramDelta;
        
        // Check if we've reached the end
        if (currentParam >= 1.0)
        {
            break;
        }
        
        // Add this cut
        cutParameters = append(cutParameters, currentParam);
        
        tangentLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
        cutPositions = append(cutPositions, tangentLine.origin);
        
        curvatureResult = evEdgeCurvature(context, { "edge" : curveEdge, "parameter" : currentParam, "arcLengthParameterization" : true });
        const curvatureSign = getCurvatureSign(curvatureResult.curvature);
        curvatureSigns = append(curvatureSigns, curvatureSign);
    }
    
    // Always add end point if we have room
    if (currentParam < 1.0 && @size(cutParameters) > 0)
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
 * Get the sign of curvature for visualization
 */
function getCurvatureSign(curvature is ValueWithUnits) returns number
{
    const curvVal = curvature / (1 / meter);
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
    @size(cutDistances) > 0;
    for (var distance in cutDistances)
        isLength(distance);
}
{
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

