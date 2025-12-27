FeatureScript 2837;
import(path : "onshape/std/geometry.fs", version : "2837.0");
import(path : "bb423a46a0203bb01d6f6409", version : "b53602a655f9004e78da482b");//splineFunctions.fs

/**
 * Describes the type of parameter used to define the spiral.
 * @value REVOLUTIONS : Define the spiral by specifying the number of revolutions (twists).
 * @value PITCH : Define the spiral by specifying the distance between each revolution along the path.
 * @value TWIST_ANGLE : Define the spiral by specifying the total angle of twist around the path.
 */
export enum SpiralType
{
    annotation { "Name" : "Revolutions" }
    REVOLUTIONS,
    annotation { "Name" : "Pitch" }
    PITCH,
    annotation { "Name" : "Twist angle" }
    TWIST_ANGLE
}

annotation { "Feature Type Name" : "3D Spiral" }
export const spiral3d = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Spline edge", "Filter" : EntityType.EDGE || ConstructionObject.NO }
        definition.splineEdge is Query;

        annotation { "Name" : "Radius" }
        isLength(definition.radius, LENGTH_BOUNDS);

        annotation { "Name" : "Spiral type", "UIHint" : UIHint.SHOW_LABEL }
        definition.spiralType is SpiralType;

        if (definition.spiralType == SpiralType.REVOLUTIONS)
        {
            annotation { "Name" : "Number of revolutions" }
            isReal(definition.revNumber, POSITIVE_REAL_BOUNDS);
        }
        else if (definition.spiralType == SpiralType.PITCH)
        {
            annotation { "Name" : "Pitch" }
            isLength(definition.pitch, NONNEGATIVE_LENGTH_BOUNDS);
        }
        else if (definition.spiralType == SpiralType.TWIST_ANGLE)
        {
            annotation { "Name" : "Twist angle" }
            isAngle(definition.twistAngle, ANGLE_360_FULL_DEFAULT_BOUNDS);
        }

        annotation { "Name" : "Flip spiral direction", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
        definition.flipDir is boolean;

    }
    {
        const splineLength = evLength(context, { "entities" : definition.splineEdge });
        const spiralRadius = definition.radius;

        // Calculate the number of revolutions based on the spiral type
        var numberOfRevolutions;
        if (definition.spiralType == SpiralType.REVOLUTIONS)
        {
            numberOfRevolutions = definition.revNumber;
        }
        else if (definition.spiralType == SpiralType.PITCH)
        {
            // Pitch is the axial distance per revolution
            // For a 3D spiral along a curve, the pitch relates to the curve length
            numberOfRevolutions = splineLength / definition.pitch;
        }
        else if (definition.spiralType == SpiralType.TWIST_ANGLE)
        {
            // Convert twist angle to number of revolutions
            numberOfRevolutions = definition.twistAngle / (360 * degree);
        }

        var pointNumber = hypot(2 * PI * spiralRadius * numberOfRevolutions, splineLength) / (10 * millimeter) + 10 * numberOfRevolutions;
        pointNumber = round(pointNumber);

        const path = constructPath(context, definition.splineEdge);

        var initTangent = evPathTangentLines(context, path, [0]).tangentLines[0];
        if (definition.flipDir)
            initTangent.direction *= -1;

        // Generate transformations for uniformly spaced path parameters (arc-length based)
        const trArr = evPathTransfromArray(context, {
                    "path" : path,
                    "paramArr" : range(0, 1, pointNumber)
                });

        const angleArr = range(0 * degree, 360 * degree * numberOfRevolutions, pointNumber);

        const initPoint = initTangent.origin + perpendicularVector(initTangent.direction) * spiralRadius;
        
        // Generate initial spiral points
        var pointList = [];
        for (var i = 0; i < pointNumber; i += 1)
        {
            var point = rotationAround(initTangent, angleArr[i]) * initPoint;
            pointList = append(pointList, trArr[i] * point);
        }
        
        // Resample points to achieve uniform 3D arc-length spacing
        // This addresses non-uniform CP density caused by interaction between rotation and transformation
        var resampledPoints = [];
        resampledPoints = append(resampledPoints, pointList[0]);
        
        // Calculate total spiral length
        var totalSpiralLength = 0 * meter;
        for (var i = 1; i < size(pointList); i += 1)
        {
            totalSpiralLength += norm(pointList[i] - pointList[i - 1]);
        }
        
        if (path.closed)
        {
            // For closed paths, add the closure distance
            totalSpiralLength += norm(pointList[0] - pointList[size(pointList) - 1]);
        }
        
        // Target spacing between points
        const targetSpacing = totalSpiralLength / (pointNumber - (path.closed ? 0 : 1));
        
        var accumulatedDistance = 0 * meter;
        var currentSourceIndex = 0;
        
        for (var targetIndex = 1; targetIndex < pointNumber - (path.closed ? 0 : 1); targetIndex += 1)
        {
            const targetDistance = targetIndex * targetSpacing;
            
            // Find the segment containing the target distance
            while (currentSourceIndex < size(pointList) - 1)
            {
                const segmentStart = pointList[currentSourceIndex];
                const segmentEnd = pointList[currentSourceIndex + 1];
                const segmentLength = norm(segmentEnd - segmentStart);
                
                if (accumulatedDistance + segmentLength >= targetDistance)
                {
                    // Interpolate within this segment
                    const remainingDistance = targetDistance - accumulatedDistance;
                    const t = segmentLength > (TOLERANCE.zeroLength * meter) ? remainingDistance / segmentLength : 0;
                    const interpolatedPoint = segmentStart + t * (segmentEnd - segmentStart);
                    resampledPoints = append(resampledPoints, interpolatedPoint);
                    break;
                }
                
                accumulatedDistance += segmentLength;
                currentSourceIndex += 1;
            }
        }
        
        // Add the last point for non-closed paths
        if (!path.closed)
        {
            resampledPoints = append(resampledPoints, pointList[size(pointList) - 1]);
        }
        
        // Use resampled points for the spline
        pointList = resampledPoints;

        // Calculate first derivatives at start and end for better curvature continuity
        // Use a hybrid approach: finite differences from many nearby points for robustness
        var startDerivative;
        var endDerivative;
        
        if (!path.closed && pointNumber >= 5)
        {
            // Use 5-point finite differences for better accuracy and smoothness
            // This averages over more points, reducing sensitivity to local discretization artifacts
            const parameterSpacing = 1.0 / (pointNumber - 1);
            
            // 5-point forward difference at start: f'(0) ≈ (-25f(0) + 48f(1) - 36f(2) + 16f(3) - 3f(4)) / (12h)
            startDerivative = (-25 * pointList[0] + 48 * pointList[1] - 36 * pointList[2] + 16 * pointList[3] - 3 * pointList[4]) / (12 * parameterSpacing);
            
            // 5-point backward difference at end
            const n = pointNumber - 1;
            endDerivative = (25 * pointList[n] - 48 * pointList[n - 1] + 36 * pointList[n - 2] - 16 * pointList[n - 3] + 3 * pointList[n - 4]) / (12 * parameterSpacing);
        }
        else if (!path.closed && pointNumber >= 3)
        {
            // Fallback to 3-point formulas
            const parameterSpacing = 1.0 / (pointNumber - 1);
            startDerivative = (-3 * pointList[0] + 4 * pointList[1] - pointList[2]) / (2 * parameterSpacing);
            endDerivative = (3 * pointList[pointNumber - 1] - 4 * pointList[pointNumber - 2] + pointList[pointNumber - 3]) / (2 * parameterSpacing);
        }
        else if (!path.closed && pointNumber >= 2)
        {
            // Fallback to 2-point formulas
            const parameterSpacing = 1.0 / (pointNumber - 1);
            startDerivative = (pointList[1] - pointList[0]) / parameterSpacing;
            endDerivative = (pointList[pointNumber - 1] - pointList[pointNumber - 2]) / parameterSpacing;
        }

        if (path.closed)
        {
            pointList = subArray(pointList, 0, size(pointList) - 2);
            pointList = append(pointList, pointList[0]);

            opPoint(context, id + "initialPoint", {
                        "point" : pointList[0]
                    });
            
            opFitSpline(context, id + "fitSplineSpiral", {
                        "points" : pointList
                    });
        }
        else
        {
            // Only include derivatives if they were calculated
            if (startDerivative != undefined && endDerivative != undefined)
            {
                opFitSpline(context, id + "fitSplineSpiral", {
                    "points" : pointList,
                    "startDerivative" : startDerivative,
                    "endDerivative" : endDerivative
                });
            }
            else
            {
                // Fall back to no derivatives if not enough points
                opFitSpline(context, id + "fitSplineSpiral", {
                    "points" : pointList
                });
            }
        }
    },
    {
        spiralType : SpiralType.REVOLUTIONS
    });
