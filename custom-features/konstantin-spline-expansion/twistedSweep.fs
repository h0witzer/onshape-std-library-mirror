FeatureScript 2837;
import(path : "onshape/std/geometry.fs", version : "2837.0");
export import(path : "onshape/std/tool.fs", version : "2837.0");
export import(path : "onshape/std/profilecontrolmode.gen.fs", version : "2837.0");
import(path : "onshape/std/boolean.fs", version : "2837.0");
import(path : "onshape/std/sweep.fs", version : "2837.0");

// Import the 3dSpiral feature with correct path
export import(path : "58ce6c94dd2a64cc938d3bfc", version : "fc04ea61c3b34b2d5c037b9a");

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
        // Calculate twist radius based on the furthest vertex of the profile from the path
        var twistRadius = 1 * millimeter; // Default fallback
        
        // Get the profile query based on body type
        var profileQuery;
        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            profileQuery = definition.profiles;
        }
        else if (definition.bodyType == ExtendedToolBodyType.SURFACE)
        {
            profileQuery = definition.surfaceProfiles;
        }
        
        // Get vertices from the profile
        var profileVertices = qAdjacent(profileQuery, AdjacencyType.VERTEX, EntityType.VERTEX);
        
        // Find the furthest vertex from the path
        if (!isQueryEmpty(context, profileVertices))
        {
            var maxDistance = 0 * meter;
            var vertices = evaluateQuery(context, profileVertices);
            
            for (var vertex in vertices)
            {
                var distResult = evDistance(context, {
                            "side0" : vertex,
                            "side1" : definition.pathEdge
                        });
                
                if (distResult.distance > maxDistance)
                {
                    maxDistance = distResult.distance;
                }
            }
            
            // Use the maximum distance found, with a small multiplier for safety
            if (maxDistance > 0 * meter)
            {
                twistRadius = maxDistance * 1.5;
            }
        }
        
        // Step 1: Create the spiral using the existing 3dSpiral feature
        var spiralDefinition = {
                "splineEdge" : definition.pathEdge,
                "radius" : twistRadius,
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
                    "bodyType" : ExtendedToolBodyType.SURFACE,
                    "profileSubqueries" : [definition.pathEdge, qCreatedBy(id + "spiral", EntityType.EDGE)]
                });

        // Step 3: Perform the sweep with face locking using the sweep feature
        var sweepDefinition = {
                "bodyType" : definition.bodyType,
                "operationType" : definition.operationType,
                "surfaceOperationType" : definition.surfaceOperationType,
                "path" : definition.pathEdge,
                "profileControl" : ProfileControlMode.LOCK_FACES,
                "lockFaces" : qCreatedBy(id + "loftedSurface", EntityType.FACE)
            };
        
        // Add profiles based on body type
        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            sweepDefinition.profiles = definition.profiles;
        }
        else if (definition.bodyType == ExtendedToolBodyType.SURFACE)
        {
            sweepDefinition.surfaceProfiles = definition.surfaceProfiles;
        }

        // Call the sweep feature (handles boolean operations internally)
        sweep(context, id + "sweep", sweepDefinition);

        // Step 4: Clean up helper geometry (spiral and lofted surface)
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
