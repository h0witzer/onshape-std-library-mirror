FeatureScript 2837;
import(path : "onshape/std/geometry.fs", version : "2837.0");
export import(path : "onshape/std/tool.fs", version : "2837.0");
export import(path : "onshape/std/profilecontrolmode.gen.fs", version : "2837.0");
import(path : "onshape/std/boolean.fs", version : "2837.0");
import(path : "bb423a46a0203bb01d6f6409", version : "b53602a655f9004e78da482b");//splineFunctions.fs

/**
 * Describes the type of parameter used to define the twist in the sweep.
 * @value REVOLUTIONS : Define the twist by specifying the number of revolutions (twists).
 * @value PITCH : Define the twist by specifying the distance between each revolution along the path.
 * @value TWIST_ANGLE : Define the twist by specifying the total angle of twist around the path.
 */
export enum TwistType
{
    annotation { "Name" : "Revolutions" }
    REVOLUTIONS,
    annotation { "Name" : "Pitch" }
    PITCH,
    annotation { "Name" : "Twist angle" }
    TWIST_ANGLE
}

annotation { "Feature Type Name" : "Twisted Sweep" }
export const twistedSweep = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Creation type", "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.bodyType is ExtendedToolBodyType;

        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            annotation { "Name" : "Faces and sketch regions to sweep",
                        "Filter" : (EntityType.FACE && GeometryType.PLANE) && ConstructionObject.NO }
            definition.profiles is Query;
        }
        else if (definition.bodyType == ExtendedToolBodyType.SURFACE)
        {
            annotation { "Name" : "Edges and sketch curves to sweep",
                        "Filter" : (EntityType.EDGE && ConstructionObject.NO) || (EntityType.BODY && BodyType.WIRE && SketchObject.NO)}
            definition.surfaceProfiles is Query;
        }

        annotation { "Name" : "Sweep path", "Filter" : (EntityType.EDGE && ConstructionObject.NO) || (EntityType.BODY && BodyType.WIRE && SketchObject.NO) }
        definition.pathEdge is Query;

        annotation { "Name" : "Twist type", "UIHint" : UIHint.SHOW_LABEL }
        definition.twistType is TwistType;

        if (definition.twistType == TwistType.REVOLUTIONS)
        {
            annotation { "Name" : "Number of revolutions" }
            isInteger(definition.revNumber, POSITIVE_COUNT_BOUNDS);
        }
        else if (definition.twistType == TwistType.PITCH)
        {
            annotation { "Name" : "Pitch" }
            isLength(definition.pitch, NONNEGATIVE_LENGTH_BOUNDS);
        }
        else if (definition.twistType == TwistType.TWIST_ANGLE)
        {
            annotation { "Name" : "Twist angle" }
            isAngle(definition.twistAngle, ANGLE_360_FULL_DEFAULT_BOUNDS);
        }

        annotation { "Name" : "Flip twist direction", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
        definition.flipTwistDir is boolean;

        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            booleanStepTypePredicate(definition);
        }
        else
        {
            surfaceOperationTypePredicate(definition);
        }
    }
    {
        // Step 1: Generate the spiral using the 3dSpiral logic
        const splineLength = evLength(context, { "entities" : definition.pathEdge });
        
        // Use a small fixed radius for the spiral offset - radius doesn't affect the twist
        const spiralRadius = 0.001 * meter;

        // Calculate the number of revolutions based on the twist type
        var numberOfRevolutions;
        if (definition.twistType == TwistType.REVOLUTIONS)
        {
            numberOfRevolutions = definition.revNumber;
        }
        else if (definition.twistType == TwistType.PITCH)
        {
            numberOfRevolutions = splineLength / definition.pitch;
        }
        else if (definition.twistType == TwistType.TWIST_ANGLE)
        {
            numberOfRevolutions = definition.twistAngle / (360 * degree);
        }

        var pointNumber = max(20, round(splineLength / (1 * millimeter))) + round(10 * numberOfRevolutions);

        var path;
        try
        {
            path = constructPath(context, definition.pathEdge);
        }
        catch
        {
            throw regenError("Failed to construct path from selected edges");
        }

        const pathTangentResult = evPathTangentLines(context, path, [0]);
        var initTangent = pathTangentResult.tangentLines[0];
        if (definition.flipTwistDir)
            initTangent.direction *= -1;

        const trArr = evPathTransfromArray(context, {
                    "path" : path,
                    "paramArr" : range(0, 1, pointNumber)
                });

        const angleArr = range(0 * degree, 360 * degree * numberOfRevolutions, pointNumber);

        const initPoint = initTangent.origin + perpendicularVector(initTangent.direction) * spiralRadius;
        var spiralPointList = [];

        for (var i = 0; i < pointNumber; i += 1)
        {
            var point = rotationAround(initTangent, angleArr[i]) * initPoint;
            spiralPointList = append(spiralPointList, trArr[i] * point);
        }

        if (path.closed)
        {
            spiralPointList = subArray(spiralPointList, 0, size(spiralPointList) - 2);
            spiralPointList = append(spiralPointList, spiralPointList[0]);
        }

        // Step 2: Create the spiral curve
        opFitSpline(context, id + "spiralCurve", {
                    "points" : spiralPointList
                });

        // Step 3: Create a lofted surface between the original path and the spiral
        opLoft(context, id + "loftedSurface", {
                    "profiles" : [definition.pathEdge, qCreatedBy(id + "spiralCurve", EntityType.EDGE)]
                });

        // Step 4: Perform the sweep with face locking
        const sweepDefinition = {
                "path" : definition.pathEdge,
                "profileControl" : ProfileControlMode.LOCK_FACES,
                "lockFaces" : qCreatedBy(id + "loftedSurface", EntityType.FACE),
                "profiles" : (definition.bodyType == ExtendedToolBodyType.SOLID) ? definition.profiles : undefined,
                "surfaceProfiles" : (definition.bodyType == ExtendedToolBodyType.SURFACE) ? definition.surfaceProfiles : undefined
            };

        const reconstructOp = function(id)
            {
                opSweep(context, id + "sweep", sweepDefinition);
            };

        opSweep(context, id + "sweep", sweepDefinition);

        // Step 5: Apply boolean operations if needed
        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            processNewBodyIfNeeded(context, id, definition, reconstructOp);
        }
        else if (definition.surfaceOperationType == NewSurfaceOperationType.ADD)
        {
            joinSurfaceBodiesWithAutoMatching(context, id, definition, false, reconstructOp);
        }

        // Step 6: Clean up helper geometry
        opDeleteBodies(context, id + "cleanup", {
                    "entities" : qUnion([
                                qCreatedBy(id + "spiralCurve", EntityType.BODY),
                                qCreatedBy(id + "loftedSurface", EntityType.BODY)
                            ])
                });
    },
    {
        bodyType : ExtendedToolBodyType.SOLID,
        operationType : NewBodyOperationType.NEW,
        twistType : TwistType.REVOLUTIONS,
        surfaceOperationType : NewSurfaceOperationType.NEW
    });
