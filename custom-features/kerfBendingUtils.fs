FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");

/**
 * Kerf bending is a technique in which cuts (kerfs) are made partway through a material, 
 * typically plywood, to create a flexible hinge point, allowing flat panels to be bent 
 * into curved surfaces.
 * 
 * This module provides utility functions for calculating kerf bend patterns along curves,
 * converting the mathematics from the Python implementation to FeatureScript utilities.
 */

/**
 * Calculate the kerf angle - the angle by which a single cut bends the material.
 * This is based on the geometry where the cut width and cut depth create a hinge point.
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
    // Calculate kerf angle: kerf_angle = 2 * atan2(cut_width, 2 * cut_depth)
    return 2 * atan2(cutWidth, 2 * cutDepth);
}

/**
 * Calculate the distance between two 2D or 3D points.
 * 
 * @param point1 : First point as a Vector with length units
 * @param point2 : Second point as a Vector with length units
 * @returns {ValueWithUnits} : The distance between the points with length units
 */
export function calculatePointDistance(point1 is Vector, point2 is Vector) returns ValueWithUnits
precondition
{
    isLengthVector(point1);
    isLengthVector(point2);
    @size(point1) == @size(point2);
    @size(point1) >= 2;
}
{
    return norm(point2 - point1);
}

/**
 * Calculate the smallest difference between two angles, accounting for wraparound.
 * The result will be in the range [-PI, PI] radians.
 * 
 * @param angle1 : First angle with angle units
 * @param angle2 : Second angle with angle units
 * @returns {ValueWithUnits} : The smallest angular difference with angle units
 */
export function calculateAngleDifference(angle1 is ValueWithUnits, angle2 is ValueWithUnits) returns ValueWithUnits
precondition
{
    isAngle(angle1);
    isAngle(angle2);
}
{
    // Normalize the difference to [0, 2*PI)
    var difference = (angle1 - angle2) % (2 * PI * radian);
    
    // Convert to [-PI, PI] range
    if (difference > PI * radian)
    {
        difference = difference - 2 * PI * radian;
    }
    else if (difference < -PI * radian)
    {
        difference = difference + 2 * PI * radian;
    }
    
    return difference;
}

/**
 * Type representing a point along a curve with its associated tangent angle.
 * 
 * @type {{
 *      @field position {Vector} : 3D position on the curve with length units (x, y, z)
 *      @field tangentAngle {ValueWithUnits} : Angle of the tangent at this point with angle units
 *      @field curvatureSign {number} : Sign of curvature (-1, 0, or 1) indicating curve direction
 * }}
 */
export type CurvePoint typecheck canBeCurvePoint;

/**
 * Typecheck for CurvePoint
 */
export predicate canBeCurvePoint(value)
{
    value is map;
    is3dLengthVector(value.position);
    isAngle(value.tangentAngle);
    value.curvatureSign is number;
}

/**
 * Construct a CurvePoint from its components.
 */
export function curvePoint(position is Vector, tangentAngle is ValueWithUnits, curvatureSign is number) returns CurvePoint
precondition
{
    is3dLengthVector(position);
    isAngle(tangentAngle);
    curvatureSign is number;
}
{
    return {
        "position" : position,
        "tangentAngle" : tangentAngle,
        "curvatureSign" : curvatureSign
    } as CurvePoint;
}

/**
 * Create quadratic Bezier curve points with tangent angles and curvature information.
 * A quadratic Bezier curve is defined by three control points and forms a parabolic arc.
 * 
 * @param controlPoints : Array of three 3D control points with length units
 * @param numberOfSamples : Number of sample points to generate along the curve
 * @returns {array} : Array of CurvePoint objects representing the discretized curve
 */
export function createQuadraticBezierCurvePoints(controlPoints is array, numberOfSamples is number) returns array
precondition
{
    @size(controlPoints) == 3;
    for (var point in controlPoints)
        is3dLengthVector(point);
    numberOfSamples >= 2;
}
{
    const p0 = controlPoints[0];
    const p1 = controlPoints[1];
    const p2 = controlPoints[2];
    
    var curvePoints = [];
    
    for (var i = 0; i <= numberOfSamples; i += 1)
    {
        const t = i / numberOfSamples;
        const oneMinusT = 1 - t;
        
        // Calculate point on curve: P(t) = (1-t)^2 * P0 + 2(1-t)t * P1 + t^2 * P2
        const pointOnCurve = oneMinusT * oneMinusT * p0 + 
                            2 * oneMinusT * t * p1 + 
                            t * t * p2;
        
        // Calculate first derivative (tangent vector): P'(t) = 2(1-t)(P1-P0) + 2t(P2-P1)
        const tangentVector = 2 * oneMinusT * (p1 - p0) + 2 * t * (p2 - p1);
        
        // Calculate tangent angle from the derivative
        const tangentAngle = atan2(tangentVector[1], tangentVector[0]);
        
        // Calculate second derivative for curvature: P''(t) = 2(P2 - 2*P1 + P0)
        const secondDerivative = 2 * (p2 - 2 * p1 + p0);
        
        // Calculate curvature sign using cross product of first and second derivatives
        // For 2D curves embedded in 3D, we look at the z-component of the cross product
        const crossProductZ = tangentVector[0] * secondDerivative[1] - tangentVector[1] * secondDerivative[0];
        const curvatureSign = crossProductZ / meter / meter > 0 ? 1 : (crossProductZ / meter / meter < 0 ? -1 : 0);
        
        curvePoints = append(curvePoints, curvePoint(pointOnCurve, tangentAngle, curvatureSign));
    }
    
    return curvePoints;
}

/**
 * Find the index of the leftmost point in a curve (smallest x-coordinate).
 * 
 * @param curvePoints : Array of CurvePoint objects
 * @returns {number} : Index of the leftmost point
 */
export function findLeftmostPointIndex(curvePoints is array) returns number
precondition
{
    @size(curvePoints) > 0;
    for (var point in curvePoints)
        canBeCurvePoint(point);
}
{
    var leftmostIndex = 0;
    var leftmostX = curvePoints[0].position[0];
    
    for (var i = 1; i < @size(curvePoints); i += 1)
    {
        if (curvePoints[i].position[0] < leftmostX)
        {
            leftmostX = curvePoints[i].position[0];
            leftmostIndex = i;
        }
    }
    
    return leftmostIndex;
}

/**
 * Find the next cut point along a curve by searching for a point with a target tangent angle.
 * This implements an efficient windowed search to find the best matching angle.
 * 
 * @param curvePoints : Array of CurvePoint objects representing the discretized curve
 * @param currentIndex : Current position index in the curve
 * @param targetAngle : The desired tangent angle to search for with angle units
 * @param searchWindow : Maximum number of indices to search in the given direction
 * @param searchDirection : Direction to search (1 for forward, -1 for backward)
 * @param minimumDistance : Minimum distance between cuts to prevent clustering with length units
 * @returns {number} : Index of the next cut point, or currentIndex if no suitable point found
 */
export function findNextCutIndex(curvePoints is array, 
                                currentIndex is number, 
                                targetAngle is ValueWithUnits,
                                searchWindow is number,
                                searchDirection is number,
                                minimumDistance is ValueWithUnits) returns number
precondition
{
    @size(curvePoints) > 0;
    for (var point in curvePoints)
        canBeCurvePoint(point);
    currentIndex >= 0 && currentIndex < @size(curvePoints);
    isAngle(targetAngle);
    searchWindow > 0;
    searchDirection == 1 || searchDirection == -1;
    isLength(minimumDistance);
}
{
    const startIndex = searchDirection > 0 ? currentIndex : max(0, currentIndex - searchWindow);
    const endIndex = searchDirection > 0 ? min(@size(curvePoints), currentIndex + searchWindow) : currentIndex;
    
    var bestIndex = currentIndex;
    var minimumAngleDifference = 1000000 * radian; // Large initial value
    
    var searchIndex = startIndex;
    while (searchIndex < endIndex)
    {
        if (searchIndex != currentIndex)
        {
            // Skip if the point is too close to the current one (prevents tight clustering)
            const distance = calculatePointDistance(curvePoints[searchIndex].position, 
                                                    curvePoints[currentIndex].position);
            
            if (distance >= minimumDistance)
            {
                const angleDiff = abs(calculateAngleDifference(curvePoints[searchIndex].tangentAngle, targetAngle));
                
                if (angleDiff < minimumAngleDifference)
                {
                    minimumAngleDifference = angleDiff;
                    bestIndex = searchIndex;
                }
            }
        }
        
        searchIndex += (searchDirection > 0 ? 1 : -1);
    }
    
    // If we couldn't find a good match in our window and the window is smaller than a quarter of the curve,
    // we could extend the search, but for simplicity we return the best found
    return bestIndex;
}

/**
 * Calculate kerf cut positions along a quadratic Bezier curve.
 * This is the main algorithm that determines where cuts should be made to achieve the desired curve.
 * 
 * @param controlPoints : Array of three 3D control points defining the Bezier curve with length units
 * @param cutWidth : The thickness of the cutting tool with length units
 * @param cutDepth : The depth of the cut with length units
 * @param curveSamples : Number of samples to use when discretizing the curve (higher = more accurate)
 * @param searchWindow : Size of the search window for finding next cut points
 * @returns {{
 *      @field cutIndices {array} : Array of indices into the curve points where cuts should be made
 *      @field curvePoints {array} : Array of all CurvePoint objects along the curve
 *      @field kerfAngle {ValueWithUnits} : The calculated kerf angle for this configuration
 * }}
 */
export function calculateKerfCutPositions(controlPoints is array,
                                         cutWidth is ValueWithUnits,
                                         cutDepth is ValueWithUnits,
                                         curveSamples is number,
                                         searchWindow is number) returns map
precondition
{
    @size(controlPoints) == 3;
    for (var point in controlPoints)
        is3dLengthVector(point);
    isLength(cutWidth);
    isLength(cutDepth);
    cutWidth > 0 * meter;
    cutDepth > 0 * meter;
    curveSamples >= 10;
    searchWindow > 0;
}
{
    // Calculate the kerf angle
    const kerfAngle = calculateKerfAngle(cutWidth, cutDepth);
    
    // Generate curve points with tangent information
    const curvePoints = createQuadraticBezierCurvePoints(controlPoints, curveSamples);
    
    // Find starting point (leftmost)
    const leftmostIndex = findLeftmostPointIndex(curvePoints);
    
    // Minimum distance is twice the cut width to prevent overlapping cuts
    const minimumDistance = cutWidth * 2;
    
    // Initialize cut indices array with the leftmost point
    var cutIndices = [leftmostIndex];
    
    // Process curve from leftmost point moving right
    var currentIndex = leftmostIndex;
    while (currentIndex < @size(curvePoints) - 1)
    {
        const currentAngle = curvePoints[currentIndex].tangentAngle;
        const currentCurvature = curvePoints[currentIndex].curvatureSign;
        
        // Adjust angle based on curvature direction
        const angleAdjustment = kerfAngle * currentCurvature;
        const nextTargetAngle = currentAngle + angleAdjustment;
        
        // Find next cut point
        const nextIndex = findNextCutIndex(curvePoints, currentIndex, nextTargetAngle, 
                                          searchWindow, 1, minimumDistance);
        
        // If we're not making progress, stop
        if (nextIndex <= currentIndex || nextIndex >= @size(curvePoints))
        {
            break;
        }
        
        cutIndices = append(cutIndices, nextIndex);
        currentIndex = nextIndex;
    }
    
    // Process curve from leftmost point moving left
    currentIndex = leftmostIndex;
    var leftCutIndices = [];
    
    while (currentIndex > 0)
    {
        const currentAngle = curvePoints[currentIndex].tangentAngle;
        const currentCurvature = curvePoints[currentIndex].curvatureSign;
        
        // For moving backwards, flip the angle adjustment direction
        const angleAdjustment = -kerfAngle * currentCurvature;
        const nextTargetAngle = currentAngle + angleAdjustment;
        
        // Find next cut point moving backward
        const nextIndex = findNextCutIndex(curvePoints, currentIndex, nextTargetAngle,
                                          searchWindow, -1, minimumDistance);
        
        // If we're not making progress, stop
        if (nextIndex >= currentIndex || nextIndex < 0)
        {
            break;
        }
        
        leftCutIndices = append(leftCutIndices, nextIndex);
        currentIndex = nextIndex;
    }
    
    // Reverse left indices and prepend to cutIndices
    for (var i = @size(leftCutIndices) - 1; i >= 0; i -= 1)
    {
        cutIndices = insert(cutIndices, leftCutIndices[i], 0);
    }
    
    return {
        "cutIndices" : cutIndices,
        "curvePoints" : curvePoints,
        "kerfAngle" : kerfAngle
    };
}

/**
 * Calculate the distances between consecutive cut points.
 * These distances represent the spacing between kerf cuts on the flat material.
 * 
 * @param cutPositions : Array of 3D position vectors with length units
 * @returns {array} : Array of distances with length units between consecutive cuts
 */
export function calculateCutDistances(cutPositions is array) returns array
precondition
{
    @size(cutPositions) >= 2;
    for (var position in cutPositions)
        is3dLengthVector(position);
}
{
    var distances = [];
    
    for (var i = 0; i < @size(cutPositions) - 1; i += 1)
    {
        const distance = calculatePointDistance(cutPositions[i], cutPositions[i + 1]);
        distances = append(distances, distance);
    }
    
    return distances;
}

/**
 * Calculate the total length of the flattened workpiece.
 * This is the sum of all distances between consecutive cuts.
 * 
 * @param cutDistances : Array of distances with length units
 * @returns {ValueWithUnits} : Total length with length units
 */
export function calculateTotalLength(cutDistances is array) returns ValueWithUnits
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
    
    return totalLength;
}

