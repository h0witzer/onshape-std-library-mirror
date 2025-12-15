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

        // Increase point density for better resolution, especially at segment transitions
        // Using 5mm instead of 10mm divisor for approximately 2x more points
        var pointNumber = hypot(2 * PI * spiralRadius * numberOfRevolutions, splineLength) / (5 * millimeter) + 10 * numberOfRevolutions;
        pointNumber = round(pointNumber);

        const path = constructPath(context, definition.splineEdge);

        var initTangent = evPathTangentLines(context, path, [0]).tangentLines[0];
        if (definition.flipDir)
            initTangent.direction *= -1;

        const trArr = evPathTransfromArray(context, {
                    "path" : path,
                    "paramArr" : range(0, 1, pointNumber)
                });

        const angleArr = range(0 * degree, 360 * degree * numberOfRevolutions, pointNumber);

        const initPoint = initTangent.origin + perpendicularVector(initTangent.direction) * spiralRadius;
        var pointList = [];

        for (var i = 0; i < pointNumber; i += 1)
        {
            var point = rotationAround(initTangent, angleArr[i]) * initPoint;
            pointList = append(pointList, trArr[i] * point);
        }

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
