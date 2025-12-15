FeatureScript 2837;
import(path : "onshape/std/geometry.fs", version : "2837.0");
export import(path : "onshape/std/tool.fs", version : "2837.0");
export import(path : "onshape/std/profilecontrolmode.gen.fs", version : "2837.0");
import(path : "onshape/std/boolean.fs", version : "2837.0");
import(path : "onshape/std/sweep.fs", version : "2837.0");

export import(path : "e82d5f644c476dabe15157c8/6d2ab01646b5814249c8e2ef/58ce6c94dd2a64cc938d3bfc", version : "87a948b20ec75b7cfd88fbc9");//3dSpiral.fs

/**
 * Helper function to set wall thickness for thin sweeps
 * Converts thickness parameters to wallThickness_1 and wallThickness_2
 */
function setWallThickness(definition is map) returns map
{
    definition.wallThickness_1 = definition.thickness1;
    definition.wallThickness_2 = definition.thickness2;

    if (definition.midplane)
    {
        definition.wallThickness_1 = definition.thickness / 2;
        definition.wallThickness_2 = definition.wallThickness_1;
        return definition;
    }
    if (definition.flipWall)
    {
        definition.wallThickness_1 = definition.thickness2;
        definition.wallThickness_2 = definition.thickness1;
    }
    return definition;
}

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
        else if (definition.bodyType == ExtendedToolBodyType.THIN)
        {
            annotation { "Name" : "Edges and sketch curves to sweep", "Filter" : (EntityType.EDGE || EntityType.FACE || (EntityType.BODY && BodyType.WIRE && SketchObject.NO)) && ConstructionObject.NO }
            definition.wallShape is Query;

            annotation { "Name" : "Mid plane", "Default" : false }
            definition.midplane is boolean;

            if (!definition.midplane)
            {
                annotation { "Name" : "Thickness 1" }
                isLength(definition.thickness1, ZERO_INCLUSIVE_OFFSET_BOUNDS);

                annotation { "Name" : "Flip wall", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.flipWall is boolean;

                annotation { "Name" : "Thickness 2" }
                isLength(definition.thickness2, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);
            }
            else
            {
                annotation { "Name" : "Thickness" }
                isLength(definition.thickness, ZERO_INCLUSIVE_OFFSET_BOUNDS);
            }
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

        if (definition.bodyType == ExtendedToolBodyType.SOLID || definition.bodyType == ExtendedToolBodyType.THIN)
        {
            booleanStepTypePredicate(definition);
        }
        else
        {
            surfaceOperationTypePredicate(definition);
        }

        if (definition.bodyType == ExtendedToolBodyType.SOLID || definition.bodyType == ExtendedToolBodyType.THIN)
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
        else if (definition.bodyType == ExtendedToolBodyType.THIN)
        {
            // For thin sweeps, we need to extract edges from faces and wire bodies
            definition = setWallThickness(definition);
            
            // Extract edges from any planar faces
            const faces = qEntityFilter(definition.wallShape, EntityType.FACE);
            if (!isQueryEmpty(context, faces))
            {
                const extractedEdges = qAdjacent(faces, AdjacencyType.EDGE, EntityType.EDGE);
                definition.wallShape = qSubtraction(definition.wallShape, faces);
                definition.wallShape = qUnion([definition.wallShape, extractedEdges]);
            }

            // Extract edges from any wire bodies
            const wireBodies = qEntityFilter(definition.wallShape, EntityType.BODY);
            if (!isQueryEmpty(context, wireBodies))
            {
                const extractedEdges = wireBodies->qOwnedByBody(EntityType.EDGE);
                definition.wallShape = qSubtraction(definition.wallShape, wireBodies);
                definition.wallShape = qUnion([definition.wallShape, extractedEdges]);
            }

            profileQuery = qConstructionFilter(definition.wallShape, ConstructionObject.NO);
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
        // For thin sweeps, we sweep as a surface and then thicken
        var sweepBodyType = definition.bodyType;
        if (definition.bodyType == ExtendedToolBodyType.THIN)
        {
            sweepBodyType = ExtendedToolBodyType.SURFACE;
        }
        
        var sweepDefinition = {
                "bodyType" : sweepBodyType,
                "path" : sweepPath,
                "profileControl" : ProfileControlMode.LOCK_FACES,
                "lockFaces" : qCreatedBy(id + "loftedSurface", EntityType.FACE)
            };
        
        // Add profiles based on body type
        // Note: opSweep expects 'profiles' field for both SOLID and SURFACE types
        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            sweepDefinition.profiles = definition.profiles;
        }
        else if (definition.bodyType == ExtendedToolBodyType.SURFACE)
        {
            sweepDefinition.profiles = definition.surfaceProfiles;
        }
        else if (definition.bodyType == ExtendedToolBodyType.THIN)
        {
            sweepDefinition.profiles = profileQuery;
        }

        // Perform the sweep operation
        opSweep(context, id + "sweep", sweepDefinition);
        
        // Step 3b: For thin sweeps, thicken the swept surface
        if (definition.bodyType == ExtendedToolBodyType.THIN)
        {
            const sweptBody = qBodyType(qCreatedBy(id + "sweep", EntityType.BODY), BodyType.SHEET);
            
            opThicken(context, id + "thicken", {
                        "entities" : sweptBody,
                        "thickness1" : definition.wallThickness_1,
                        "thickness2" : definition.wallThickness_2
                    });
            
            // Delete the swept surface body after thickening
            opDeleteBodies(context, id + "deleteSurfaceBody", {
                        "entities" : sweptBody
                    });
        }

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
                
                // Recreate thickening for thin sweeps
                if (definition.bodyType == ExtendedToolBodyType.THIN)
                {
                    const sweptBody = qBodyType(qCreatedBy(id + "sweep", EntityType.BODY), BodyType.SHEET);
                    
                    opThicken(context, id + "thicken", {
                                "entities" : sweptBody,
                                "thickness1" : definition.wallThickness_1,
                                "thickness2" : definition.wallThickness_2
                            });
                    
                    opDeleteBodies(context, id + "deleteSurfaceBody", {
                                "entities" : sweptBody
                            });
                }
                
                // Clean up helper geometry
                opDeleteBodies(context, id + "cleanup", {
                            "entities" : qUnion([
                                        qCreatedBy(id + "spiral", EntityType.BODY),
                                        qCreatedBy(id + "loftedSurface", EntityType.BODY)
                                    ])
                        });
            };

        // Step 5: Handle boolean operations based on body type
        if (definition.bodyType == ExtendedToolBodyType.SOLID || definition.bodyType == ExtendedToolBodyType.THIN)
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
