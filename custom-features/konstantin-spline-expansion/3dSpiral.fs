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
        
        println("=== RESAMPLING DEBUG ===");
        println("path.closed: " ~ path.closed);
        println("Initial pointList size: " ~ size(pointList));
        println("pointNumber: " ~ pointNumber);
        
        // Calculate cumulative distances along the original spiral
        var cumulativeDistances = [0 * meter];
        for (var i = 1; i < size(pointList); i += 1)
        {
            const segmentLength = norm(pointList[i] - pointList[i - 1]);
            cumulativeDistances = append(cumulativeDistances, cumulativeDistances[i - 1] + segmentLength);
        }
        
        var totalSpiralLength = cumulativeDistances[size(cumulativeDistances) - 1];
        println("Length before closure: " ~ totalSpiralLength);
        
        var closureDistance = 0 * meter;
        if (path.closed)
        {
            // For closed paths, add the closure distance (from last point back to first)
            closureDistance = norm(pointList[0] - pointList[size(pointList) - 1]);
            totalSpiralLength += closureDistance;
            println("Closure distance: " ~ closureDistance);
        }
        
        println("Total spiral length: " ~ totalSpiralLength);
        
        // Target spacing between points
        const targetSpacing = totalSpiralLength / (pointNumber - 1);
        println("Target spacing: " ~ targetSpacing);
        println("Number of resampled points to generate: " ~ (pointNumber - 1));
        
        // Resample at uniform intervals
        var resampledPoints = [pointList[0]];
        
        var notFoundCount = 0;
        for (var targetIndex = 1; targetIndex < pointNumber; targetIndex += 1)
        {
            const targetDistance = targetIndex * targetSpacing;
            var interpolatedPoint;
            var found = false;
            
            // Find which segment contains this target distance
            for (var segIdx = 0; segIdx < size(pointList); segIdx += 1)
            {
                var segmentStartDist;
                var segmentEndDist;
                var segmentStart;
                var segmentEnd;
                
                if (segIdx < size(pointList) - 1)
                {
                    // Normal segment
                    segmentStartDist = cumulativeDistances[segIdx];
                    segmentEndDist = cumulativeDistances[segIdx + 1];
                    segmentStart = pointList[segIdx];
                    segmentEnd = pointList[segIdx + 1];
                }
                else if (path.closed)
                {
                    // Wrap-around segment for closed paths
                    segmentStartDist = cumulativeDistances[segIdx];
                    segmentEndDist = totalSpiralLength;
                    segmentStart = pointList[segIdx];
                    segmentEnd = pointList[0];
                }
                else
                {
                    // No more segments for open paths
                    break;
                }
                
                // Check if target distance falls within this segment
                // Use tolerance for boundary checks to handle floating point precision
                const tolerance = TOLERANCE.zeroLength * meter;
                if (targetDistance >= segmentStartDist - tolerance && targetDistance <= segmentEndDist + tolerance)
                {
                    const segmentLength = segmentEndDist - segmentStartDist;
                    const distanceIntoSegment = targetDistance - segmentStartDist;
                    const t = segmentLength > (TOLERANCE.zeroLength * meter) ? 
                        distanceIntoSegment / segmentLength : 0;
                    interpolatedPoint = segmentStart + t * (segmentEnd - segmentStart);
                    found = true;
                    
                    // Debug last few points for closed paths
                    if (path.closed && targetIndex >= pointNumber - 5)
                    {
                        println("Target " ~ targetIndex ~ ": distance=" ~ targetDistance ~ 
                                ", segIdx=" ~ segIdx ~ ", t=" ~ t);
                    }
                    break;
                }
            }
            
            if (found)
            {
                resampledPoints = append(resampledPoints, interpolatedPoint);
            }
            else
            {
                notFoundCount += 1;
                // Fallback: use the last available point
                resampledPoints = append(resampledPoints, pointList[size(pointList) - 1]);
                println("WARNING: Could not find segment for target " ~ targetIndex ~ 
                        " at distance " ~ targetDistance);
            }
        }
        
        println("Points not found: " ~ notFoundCount);
        println("Final resampledPoints size: " ~ size(resampledPoints));
        
        // For closed paths, the last resampled point should wrap back close to the start
        // For open paths, add the final endpoint
        if (!path.closed)
        {
            resampledPoints = append(resampledPoints, pointList[size(pointList) - 1]);
            println("Added final point for open path. New size: " ~ size(resampledPoints));
        }
        else
        {
            // Measure gap between last and first points
            const lastPoint = resampledPoints[size(resampledPoints) - 1];
            const firstPoint = resampledPoints[0];
            const gapDistance = norm(lastPoint - firstPoint);
            println("Closed path - no additional endpoint needed");
            println("Gap between last and first point: " ~ gapDistance);
            println("Gap as % of target spacing: " ~ (gapDistance / targetSpacing * 100) ~ "%");
            
            // Debug visualization: draw start and end points
            opPoint(context, id + "debugStart", { "point": firstPoint, "origin": DebugColor.RED });
            opPoint(context, id + "debugEnd", { "point": lastPoint, "origin": DebugColor.BLUE });
        }
        
        println("=== END RESAMPLING DEBUG ===");
        
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
            println("=== CLOSED PATH PROCESSING ===");
            println("pointList size before subArray: " ~ size(pointList));
            println("Last point before processing: " ~ pointList[size(pointList) - 1]);
            println("First point: " ~ pointList[0]);
            println("Distance last to first before processing: " ~ norm(pointList[size(pointList) - 1] - pointList[0]));
            
            pointList = subArray(pointList, 0, size(pointList) - 2);
            pointList = append(pointList, pointList[0]);
            
            println("pointList size after subArray+append: " ~ size(pointList));
            println("Last point after processing: " ~ pointList[size(pointList) - 1]);
            println("Distance new last to first: " ~ norm(pointList[size(pointList) - 1] - pointList[0]));
            println("=== END CLOSED PATH PROCESSING ===");

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
