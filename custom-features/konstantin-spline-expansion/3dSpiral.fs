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

        // Calculate first and second derivatives at start and end for better curvature continuity
        // Using higher-order finite differences from the generated spiral points provides
        // accurate estimates that match the discrete geometry
        var startDerivative;
        var endDerivative;
        var startSecondDerivative;
        var endSecondDerivative;
        
        if (!path.closed)
        {
            const parameterSpacing = 1.0 / (pointNumber - 1);
            
            if (pointNumber >= 4)
            {
                // Use 3-point forward difference for first derivatives
                // Formula: f'(0) ≈ (-3*f(0) + 4*f(1) - f(2)) / (2*h)
                startDerivative = (-3 * pointList[0] + 4 * pointList[1] - pointList[2]) / (2 * parameterSpacing);
                endDerivative = (3 * pointList[pointNumber - 1] - 4 * pointList[pointNumber - 2] + pointList[pointNumber - 3]) / (2 * parameterSpacing);
                
                // Use 3-point forward/backward difference for second derivatives
                // Formula: f''(0) ≈ (f(0) - 2*f(1) + f(2)) / h^2
                startSecondDerivative = (pointList[0] - 2 * pointList[1] + pointList[2]) / (parameterSpacing * parameterSpacing);
                endSecondDerivative = (pointList[pointNumber - 1] - 2 * pointList[pointNumber - 2] + pointList[pointNumber - 3]) / (parameterSpacing * parameterSpacing);
            }
            else if (pointNumber >= 3)
            {
                // Use 3-point formulas for first derivatives only
                startDerivative = (-3 * pointList[0] + 4 * pointList[1] - pointList[2]) / (2 * parameterSpacing);
                endDerivative = (3 * pointList[pointNumber - 1] - 4 * pointList[pointNumber - 2] + pointList[pointNumber - 3]) / (2 * parameterSpacing);
            }
            else
            {
                // Fallback to simple difference if we don't have enough points
                startDerivative = (pointList[1] - pointList[0]) / parameterSpacing;
                endDerivative = (pointList[pointNumber - 1] - pointList[pointNumber - 2]) / parameterSpacing;
            }
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
            var fitSplineDefinition = {
                "points" : pointList,
                "startDerivative" : startDerivative,
                "endDerivative" : endDerivative
            };
            
            // Add second derivatives if they were calculated (improves curvature continuity)
            if (startSecondDerivative != undefined)
            {
                fitSplineDefinition.start2ndDerivative = startSecondDerivative;
            }
            if (endSecondDerivative != undefined)
            {
                fitSplineDefinition.end2ndDerivative = endSecondDerivative;
            }
            
            opFitSpline(context, id + "fitSplineSpiral", fitSplineDefinition);
        }
    },
    {
        spiralType : SpiralType.REVOLUTIONS
    });
