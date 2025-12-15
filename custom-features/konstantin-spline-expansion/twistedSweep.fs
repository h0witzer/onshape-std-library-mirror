FeatureScript 2837;
import(path : "onshape/std/geometry.fs", version : "2837.0");
export import(path : "onshape/std/tool.fs", version : "2837.0");
export import(path : "onshape/std/profilecontrolmode.gen.fs", version : "2837.0");
import(path : "onshape/std/boolean.fs", version : "2837.0");
import(path : "onshape/std/sweep.fs", version : "2837.0");

export import(path : "e82d5f644c476dabe15157c8/6d2ab01646b5814249c8e2ef/58ce6c94dd2a64cc938d3bfc", version : "87a948b20ec75b7cfd88fbc9");//3dSpiral.fs


annotation { "Feature Type Name" : "Tweep" }
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

        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            booleanStepScopePredicate(definition);
        }
        else
        {
            surfaceJoinStepScopePredicate(definition);
        }
    }
   {
        // Convert the path query to individual edges when a wire body is selected to
        // avoid invalid edge evaluations in the downstream sweep logic
        var sweepPath = definition.pathEdge;
        if (!isQueryEmpty(context, qEntityFilter(sweepPath, EntityType.BODY)))
        {
            sweepPath = qOwnedByBody(sweepPath, EntityType.EDGE);
        }

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
                            "side1" : sweepPath
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
                "splineEdge" : sweepPath,
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
                    "profileSubqueries" : [sweepPath, qCreatedBy(id + "spiral", EntityType.EDGE)]
                });

        // Step 3: Perform the sweep with face locking
        var sweepDefinition = {
                "bodyType" : definition.bodyType,
                "path" : sweepPath,
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

        // Perform the sweep operation
        opSweep(context, id + "sweep", sweepDefinition);

        // Step 4: Clean up helper geometry (spiral and lofted surface)
        opDeleteBodies(context, id + "cleanup", {
                    "entities" : qUnion([
                                qCreatedBy(id + "spiral", EntityType.BODY),
                                qCreatedBy(id + "loftedSurface", EntityType.BODY)
                            ])
                });

        // Define the reconstruction operation for boolean handling
        const reconstructOp = function(id)
            {
                // Recreate the spiral
                spiral3d(context, id + "spiral", spiralDefinition);
                
                // Recreate the lofted surface
                opLoft(context, id + "loftedSurface", {
                            "bodyType" : ExtendedToolBodyType.SURFACE,
                            "profileSubqueries" : [sweepPath, qCreatedBy(id + "spiral", EntityType.EDGE)]
                        });
                
                // Recreate the sweep
                opSweep(context, id + "sweep", sweepDefinition);
                
                // Clean up helper geometry
                opDeleteBodies(context, id + "cleanup", {
                            "entities" : qUnion([
                                        qCreatedBy(id + "spiral", EntityType.BODY),
                                        qCreatedBy(id + "loftedSurface", EntityType.BODY)
                                    ])
                        });
            };

        // Step 5: Handle boolean operations based on body type
        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            processNewBodyIfNeeded(context, id, definition, reconstructOp);
        }
        else if (definition.surfaceOperationType == NewSurfaceOperationType.ADD)
        {
            joinSurfaceBodiesWithAutoMatching(context, id, definition, false, reconstructOp);
        }
    },
    {
        bodyType : ExtendedToolBodyType.SOLID,
        operationType : NewBodyOperationType.NEW,
        spiralType : SpiralType.REVOLUTIONS,
        surfaceOperationType : NewSurfaceOperationType.NEW,
        defaultSurfaceScope : true
    });
