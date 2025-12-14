FeatureScript 2837;
import(path : "onshape/std/geometry.fs", version : "2837.0");
export import(path : "onshape/std/tool.fs", version : "2837.0");
export import(path : "onshape/std/profilecontrolmode.gen.fs", version : "2837.0");
import(path : "onshape/std/boolean.fs", version : "2837.0");
import(path : "bb423a46a0203bb01d6f6409", version : "b53602a655f9004e78da482b");//splineFunctions.fs

// Import the 3dSpiral feature and its types
import(path : "bb423a46a0203bb01d6f6409/3dSpiral", version : "b53602a655f9004e78da482b");

// Re-export SpiralType from 3dSpiral for use in preconditions
export import(path : "bb423a46a0203bb01d6f6409/3dSpiral", version : "b53602a655f9004e78da482b") as SpiralTypeImport;

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

        annotation { "Name" : "Twist radius" }
        isLength(definition.twistRadius, NONNEGATIVE_LENGTH_BOUNDS);

        annotation { "Name" : "Spiral type", "UIHint" : UIHint.SHOW_LABEL }
        definition.spiralType is SpiralType;

        if (definition.spiralType == SpiralType.REVOLUTIONS)
        {
            annotation { "Name" : "Number of revolutions" }
            isInteger(definition.revNumber, POSITIVE_COUNT_BOUNDS);
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

        annotation { "Name" : "Flip twist direction", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
        definition.flipDir is boolean;

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
        // Step 1: Create the spiral using the existing 3dSpiral feature
        const spiralDefinition = {
                "splineEdge" : definition.pathEdge,
                "radius" : definition.twistRadius,
                "spiralType" : definition.spiralType,
                "flipDir" : definition.flipDir
            };
        
        // Add the appropriate parameter based on spiral type
        if (definition.spiralType == SpiralType.REVOLUTIONS)
        {
            spiralDefinition.revNumber = definition.revNumber;
        }
        else if (definition.spiralType == SpiralType.PITCH)
        {
            spiralDefinition.pitch = definition.pitch;
        }
        else if (definition.spiralType == SpiralType.TWIST_ANGLE)
        {
            spiralDefinition.twistAngle = definition.twistAngle;
        }

        // Call the 3dSpiral feature to generate the spiral curve
        spiral3d(context, id + "spiral", spiralDefinition);

        // Step 2: Create a lofted surface between the original path and the spiral
        opLoft(context, id + "loftedSurface", {
                    "profiles" : [definition.pathEdge, qCreatedBy(id + "spiral", EntityType.EDGE)]
                });

        // Step 3: Perform the sweep with face locking
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

        // Step 4: Apply boolean operations if needed
        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            processNewBodyIfNeeded(context, id, definition, reconstructOp);
        }
        else if (definition.surfaceOperationType == NewSurfaceOperationType.ADD)
        {
            joinSurfaceBodiesWithAutoMatching(context, id, definition, false, reconstructOp);
        }

        // Step 5: Clean up helper geometry (spiral and lofted surface)
        opDeleteBodies(context, id + "cleanup", {
                    "entities" : qUnion([
                                qCreatedBy(id + "spiral", EntityType.BODY),
                                qCreatedBy(id + "loftedSurface", EntityType.BODY)
                            ])
                });
    },
    {
        bodyType : ExtendedToolBodyType.SOLID,
        operationType : NewBodyOperationType.NEW,
        spiralType : SpiralType.REVOLUTIONS,
        surfaceOperationType : NewSurfaceOperationType.NEW
    });
