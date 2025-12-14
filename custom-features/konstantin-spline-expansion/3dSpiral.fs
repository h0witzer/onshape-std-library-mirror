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
        
        // Validate that the path has edges
        if (size(path.edges) == 0)
        {
            throw regenError("The selected edge does not form a valid path.");
        }
        
        // Validate that we can evaluate tangents on the path before proceeding
        // This prevents errors when the path contains edges that don't support tangent evaluation
        var initTangentResult;
        try silent
        {
            initTangentResult = evPathTangentLines(context, path, [0]);
        }
        catch
        {
            throw regenError("Cannot evaluate tangent lines on the selected edge. The edge may be degenerate, or may be a type that does not support tangent evaluation. Please select a different edge.");
        }
        
        // Validate that we got valid tangent lines
        if (initTangentResult == undefined || initTangentResult.tangentLines == undefined || size(initTangentResult.tangentLines) == 0)
        {
            throw regenError("Failed to evaluate tangent lines on the selected edge. Please select a different edge.");
        }
        
        var initTangent = initTangentResult.tangentLines[0];
        if (definition.flipDir)
            initTangent.direction *= -1;

        var trArr;
        try silent
        {
            trArr = evPathTransfromArray(context, {
                        "path" : path,
                        "paramArr" : range(0, 1, pointNumber)
                    });
        }
        catch
        {
            throw regenError("Cannot create transformation array along the path. The edge may contain discontinuities or unsupported geometry.");
        }

        const angleArr = range(0 * degree, 360 * degree * numberOfRevolutions, pointNumber);

        const initPoint = initTangent.origin + perpendicularVector(initTangent.direction) * spiralRadius;
        var pointList = [];

        for (var i = 0; i < pointNumber; i += 1)
        {
            var point = rotationAround(initTangent, angleArr[i]) * initPoint;
            pointList = append(pointList, trArr[i] * point);
        }

        if (path.closed)
        {
            pointList = subArray(pointList, 0, size(pointList) - 2);
            pointList = append(pointList, pointList[0]);

            opPoint(context, id + "initialPoint", {
                        "point" : pointList[0]
                    });
        }

        opFitSpline(context, id + "fitSplineSpiral", {
                    "points" : pointList
                });
    },
    {
        spiralType : SpiralType.REVOLUTIONS
    });
