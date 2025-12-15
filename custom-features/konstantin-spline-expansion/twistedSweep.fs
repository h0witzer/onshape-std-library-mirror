FeatureScript 2837;
import(path : "onshape/std/geometry.fs", version : "2837.0");
export import(path : "onshape/std/tool.fs", version : "2837.0");
export import(path : "onshape/std/profilecontrolmode.gen.fs", version : "2837.0");
import(path : "onshape/std/boolean.fs", version : "2837.0");
import(path : "onshape/std/sweep.fs", version : "2837.0");

export import(path : "e82d5f644c476dabe15157c8/6d2ab01646b5814249c8e2ef/58ce6c94dd2a64cc938d3bfc", version : "87a948b20ec75b7cfd88fbc9");//3dSpiral.fs

// Constants for domain matching loft operations
const ZERO_LENGTH_TOLERANCE = TOLERANCE.zeroLength * meter;
const RATIO_TOLERANCE = TOLERANCE.computational; // Dimensionless tolerance for ratio comparison

/**
 * Generate loft connections using evDistance method for closed single-edge loops.
 * This ensures proper alignment at the start/end of the loop to prevent loft failures.
 * 
 * @param context : The context
 * @param edgeGroup1 : Query for the first edge group (the closed loop)
 * @param edgeGroup2 : Query for the second edge group (the spiral)
 * @returns array : Array of connection maps for opLoft
 */
function generateEvDistanceConnections(context is Context, edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    var loftConnections = [];
    
    // Get the start vertex of the loop edge
    var vertices1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
    
    if (isQueryEmpty(context, vertices1))
    {
        // No vertices found, return empty connections
        return loftConnections;
    }
    
    // Get the first vertex and find its closest point on the spiral
    var startVertex = qNthElement(vertices1, 0);
    
    // Use evDistance to find the closest point on the spiral to the start vertex
    var distResult = evDistance(context, {
                "side0" : startVertex,
                "side1" : edgeGroup2,
                "arcLengthParameterization" : true
            });
    
    // Get the edge and parameter on the spiral
    var spiralEdge = qNthElement(edgeGroup2, distResult.sides[1].index);
    var spiralParameter = distResult.sides[1].parameter;
    
    // Get the loop edge
    var loopEdge = qNthElement(edgeGroup1, 0);
    
    // Create a connection at parameter 0 on the loop edge to the closest point on the spiral
    var connectionMap = {
        "connectionEntities" : qUnion([loopEdge, spiralEdge]),
        "connectionEdges" : [loopEdge, spiralEdge],
        "connectionEdgeParameters" : [0.0, spiralParameter]
    };
    
    loftConnections = append(loftConnections, connectionMap);
    
    return loftConnections;
}

/**
 * Generate path length parameterized loft connections between two edge groups.
 * This ensures proper domain matching when one curve is segmented and the other is smooth.
 * 
 * @param context : The context
 * @param edgeGroup1 : Query for the first edge group (typically the segmented sweep path)
 * @param edgeGroup2 : Query for the second edge group (typically the smooth spiral)
 * @returns array : Array of connection maps for opLoft
 */
function generatePathLengthLoftConnections(context is Context, edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Construct ordered paths from the edge groups
    var path1 = constructPath(context, edgeGroup1);
    var path2 = constructPath(context, edgeGroup2);
    
    var totalLength1 = evPathLength(context, path1);
    var totalLength2 = evPathLength(context, path2);
    
    // Check if path1 is a closed loop with only one edge
    var isClosedSingleEdge = path1.closed && size(path1.edges) == 1;
    
    // Get all vertices from path1 (the segmented path)
    var allVertices1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);
    
    // Calculate path length ratios for all vertices in path1
    var pathRatios = [];
    for (var i = 0; i < evaluateQueryCount(context, allVertices1); i += 1)
    {
        var currentPoint = qNthElement(allVertices1, i);
        var pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, path1, totalLength1);
        pathRatios = append(pathRatios, pathLengthRatio);
    }
    
    // Special handling for closed loop with single edge: use evDistance to align start/end
    if (isClosedSingleEdge && size(pathRatios) <= 1)
    {
        return generateEvDistanceConnections(context, edgeGroup1, edgeGroup2);
    }
    
    // Remove duplicates and sort
    pathRatios = removeDuplicateRatios(pathRatios);
    pathRatios = sortRatios(pathRatios);
    
    // Create connections at all path length ratios
    var loftConnections = [];
    for (var ratio in pathRatios)
    {
        // Calculate edge and parameter on path2 (spiral) for this ratio
        var edge2Info = calculateLocalEdgeParameterFromRatio(context, path2, ratio, totalLength2);
        
        // Find the edge in path1 that contains this ratio
        var edge1Info = calculateLocalEdgeParameterFromRatio(context, path1, ratio, totalLength1);
        
        // Create connection from edge1 to edge2
        var connectionMap = {
            "connectionEntities" : qUnion([edge1Info.edge, edge2Info.edge]),
            "connectionEdges" : [edge1Info.edge, edge2Info.edge],
            "connectionEdgeParameters" : [edge1Info.parameter, edge2Info.parameter]
        };
        
        loftConnections = append(loftConnections, connectionMap);
    }
    
    return loftConnections;
}

/**
 * Calculate the path length ratio (0 to 1) of a vertex position along a path.
 */
function calculatePathLengthRatioForVertex(context is Context, vertex is Query, path is Path, totalLength is ValueWithUnits) returns number
{
    // Find which edge in the ordered path contains this vertex
    var pathLengthBeforeEdge = 0 * meter;
    
    for (var i = 0; i < size(path.edges); i += 1)
    {
        var edge = path.edges[i];
        var edgeLength = evLength(context, {
                    "entities" : edge
                });
        
        // Check if vertex is on this edge by using evDistance
        var dist = evDistance(context, {
                    "side0" : vertex,
                    "side1" : edge,
                    "arcLengthParameterization" : true
                });
        
        // If the vertex is very close to this edge, it's on this edge
        if (dist.distance < ZERO_LENGTH_TOLERANCE)
        {
            var arcLengthParameter = dist.sides[1].parameter;
            var lengthAlongEdge = edgeLength * arcLengthParameter;
            
            // Account for edge flipping in the path
            if (path.flipped[i])
            {
                lengthAlongEdge = edgeLength - lengthAlongEdge;
            }
            
            var totalPathLengthToVertex = pathLengthBeforeEdge + lengthAlongEdge;
            return totalPathLengthToVertex / totalLength;
        }
        
        pathLengthBeforeEdge = pathLengthBeforeEdge + edgeLength;
    }
    
    // If we didn't find the vertex on any edge, return 0 (shouldn't happen)
    return 0;
}

/**
 * Calculate the local edge and parameter for a given global path ratio.
 */
function calculateLocalEdgeParameterFromRatio(context is Context, path is Path, globalRatio is number, totalLength is ValueWithUnits) returns map
{
    // Clamp the ratio to [0, 1] to handle numerical errors
    globalRatio = max(0.0, min(1.0, globalRatio));
    
    var targetPathLength = totalLength * globalRatio;
    var accumulatedLength = 0 * meter;
    
    // Find which edge contains this path length
    var edgeIndex = 0;
    for (var i = 0; i < size(path.edges); i += 1)
    {
        var edgeLength = evLength(context, {
                    "entities" : path.edges[i]
                });
        
        if (accumulatedLength + edgeLength >= targetPathLength || i == size(path.edges) - 1)
        {
            edgeIndex = i;
            break;
        }
        
        accumulatedLength += edgeLength;
    }
    
    // Calculate the local parameter on this edge
    var edgeLength = evLength(context, {
                "entities" : path.edges[edgeIndex]
            });
    
    var lengthIntoEdge = targetPathLength - accumulatedLength;
    var parameterOnEdge = lengthIntoEdge / edgeLength;
    
    // Account for edge flipping
    if (path.flipped[edgeIndex])
    {
        parameterOnEdge = 1.0 - parameterOnEdge;
    }
    
    // Clamp parameter to [0, 1] to avoid numerical errors
    parameterOnEdge = max(0.0, min(1.0, parameterOnEdge));
    
    return {
        "edge" : path.edges[edgeIndex],
        "parameter" : parameterOnEdge
    };
}

/**
 * Remove duplicate ratios from an array (within tolerance).
 */
function removeDuplicateRatios(ratios is array) returns array
{
    var unique = [];
    for (var ratio in ratios)
    {
        var isDuplicate = false;
        for (var existing in unique)
        {
            if (abs(ratio - existing) < RATIO_TOLERANCE)
            {
                isDuplicate = true;
                break;
            }
        }
        if (!isDuplicate)
        {
            unique = append(unique, ratio);
        }
    }
    return unique;
}

/**
 * Sort an array of numbers using bubble sort.
 * Performance is acceptable for typical twisted sweep use cases (small arrays of vertex ratios).
 */
function sortRatios(arr is array) returns array
{
    var sorted = arr;
    var n = size(sorted);
    for (var i = 0; i < n - 1; i += 1)
    {
        for (var j = 0; j < n - i - 1; j += 1)
        {
            if (sorted[j] > sorted[j + 1])
            {
                var temp = sorted[j];
                sorted[j] = sorted[j + 1];
                sorted[j + 1] = temp;
            }
        }
    }
    return sorted;
}

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
        // Use path length parameterized connections for proper domain matching
        var spiralEdges = qCreatedBy(id + "spiral", EntityType.EDGE);
        var loftConnections = generatePathLengthLoftConnections(context, sweepPath, spiralEdges);
        
        opLoft(context, id + "loftedSurface", {
                    "bodyType" : ExtendedToolBodyType.SURFACE,
                    "profileSubqueries" : [sweepPath, spiralEdges],
                    "connections" : loftConnections
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
                
                // Recreate the lofted surface with path length connections
                var spiralEdgesRecon = qCreatedBy(id + "spiral", EntityType.EDGE);
                var loftConnectionsRecon = generatePathLengthLoftConnections(context, sweepPath, spiralEdgesRecon);
                
                opLoft(context, id + "loftedSurface", {
                            "bodyType" : ExtendedToolBodyType.SURFACE,
                            "profileSubqueries" : [sweepPath, spiralEdgesRecon],
                            "connections" : loftConnectionsRecon
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
