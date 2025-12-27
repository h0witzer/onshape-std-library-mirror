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

        // Calculate first derivatives at start and end for better curvature continuity
        // Use a hybrid approach: finite differences from many nearby points for robustness
        var startDerivative;
        var endDerivative;
        var intermediateDerivatives = {};
        
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
            
            // Detect transition points by examining the rate of change in tangent direction along the path
            // Add derivative constraints at and around points where the curvature of the path changes significantly
            const tangentLines = evPathTangentLines(context, path, range(0, 1, pointNumber)).tangentLines;
            
            // Threshold for detecting curvature discontinuities (empirically chosen to identify edge transitions)
            // This represents the magnitude of the second finite difference of tangent directions
            const curvatureChangeThreshold = 0.005;
            
            // Track which indices should have derivative constraints
            var transitionIndices = [];
            
            // Calculate the "curvature" of the tangent path as the rate of change of tangent direction
            for (var i = 2; i < pointNumber - 2; i += 1)
            {
                // Look at three consecutive tangents to detect regions of changing curvature
                const tang_im1 = tangentLines[i - 1].direction;
                const tang_i = tangentLines[i].direction;
                const tang_ip1 = tangentLines[i + 1].direction;
                
                // Calculate the "curvature" as the second finite difference of tangent direction
                // This detects where the path itself has discontinuous curvature (like line-to-arc transitions)
                const deltaTangent1 = tang_i - tang_im1;
                const deltaTangent2 = tang_ip1 - tang_i;
                const secondDeltaTangent = deltaTangent2 - deltaTangent1;
                
                const curvatureChange = norm(secondDeltaTangent);
                
                // If we detect a significant change in path curvature, mark this as a transition point
                if (curvatureChange > curvatureChangeThreshold)
                {
                    transitionIndices = append(transitionIndices, i);
                }
            }
            
            // For each detected transition, add derivative constraints in a neighborhood
            // This provides better control over the spline fitting in transition regions
            for (var transitionIdx in transitionIndices)
            {
                // Add constraint at the transition point itself
                const derivative = (pointList[transitionIdx - 2] - 8 * pointList[transitionIdx - 1] + 8 * pointList[transitionIdx + 1] - pointList[transitionIdx + 2]) / (12 * parameterSpacing);
                intermediateDerivatives[transitionIdx] = derivative;
                
                // Add constraints on both sides of the transition for smoother blending
                // Before transition (if not already added and has enough neighbors)
                if (transitionIdx >= 3 && transitionIdx - 1 >= 2)
                {
                    const derivBefore = (pointList[transitionIdx - 3] - 8 * pointList[transitionIdx - 2] + 8 * pointList[transitionIdx] - pointList[transitionIdx + 1]) / (12 * parameterSpacing);
                    intermediateDerivatives[transitionIdx - 1] = derivBefore;
                }
                
                // After transition (if not already added and has enough neighbors)
                if (transitionIdx + 1 < pointNumber - 2 && transitionIdx + 3 < pointNumber)
                {
                    const derivAfter = (pointList[transitionIdx - 1] - 8 * pointList[transitionIdx] + 8 * pointList[transitionIdx + 2] - pointList[transitionIdx + 3]) / (12 * parameterSpacing);
                    intermediateDerivatives[transitionIdx + 1] = derivAfter;
                }
            }
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
            // Build the definition with all available derivative information
            var fitSplineDefinition = { "points" : pointList };
            
            if (startDerivative != undefined)
            {
                fitSplineDefinition.startDerivative = startDerivative;
            }
            
            if (endDerivative != undefined)
            {
                fitSplineDefinition.endDerivative = endDerivative;
            }
            
            if (size(intermediateDerivatives) > 0)
            {
                fitSplineDefinition.derivatives = intermediateDerivatives;
            }
            
            opFitSpline(context, id + "fitSplineSpiral", fitSplineDefinition);
        }
    },
    {
        spiralType : SpiralType.REVOLUTIONS
    });
