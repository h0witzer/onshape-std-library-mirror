FeatureScript 2581;
import(path : "onshape/std/common.fs", version : "2581.0");
icon::import(path : "e598e4130b076e3e9e6f2cbf", version : "4f1438810e70ec79d297f437");
image::import(path : "bb4c161872fababb5baf8dd8", version : "fb76764707994abe71bdf65c");

/**
 * Performs a pattern of faces within a face. The instances are placed in a hexagonal or square pattern and no instances will be
 * created that cross the boundary of the face. If a border is set then no instances are created within a border of that size
 * @param definition {{
 *      @field entities A collection of faces that will be patterned
 *      @field target A planar face that contains the 'entities' to pattern
 *      @field direction Specifies the alignment of the pattern in the face
 *      @field patternType Specifies hexagonal, square or diamond pattern shape
 *      @field distance The distance between the center of each instance
 *      @field border The width of the "exclusion zone" at every edge of the target face
 * }}
 */

export enum FillPatternType
{
    annotation { "Name" : "Hexagonal" }
    HEX,
    annotation { "Name" : "Square" }
    SQUARE,
    annotation { "Name" : "Diamond" }
    DIAMOND
}

annotation { "Feature Type Name" : "Fill pattern",
        "Filter Selector" : "allparts",
        "Feature Name Template" : "Fill pattern (#instances)",
        "Feature Type Description" : "Creates a fill pattern of selected faces within the target face.",
        "Description Image" : image::BLOB_DATA,
        "Icon" : icon::BLOB_DATA }
export const fillPattern = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Faces to pattern", "UIHint" : UIHint.SHOW_CREATE_SELECTION, "Filter" : EntityType.FACE && ConstructionObject.NO && SketchObject.NO && ModifiableEntityOnly.YES }
        definition.entities is Query;

        annotation { "Name" : "Target boundary", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1,
                    "Description" : "A planar face or sketch that contains the entities to pattern" }
        definition.target is Query;

        annotation { "Name" : "Direction", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION || BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1,
                    "Description" : "Align pattern to an edge, face or the X axis of a mate connector" }
        definition.direction is Query;

        annotation { "Name" : "Pattern type" }
        definition.patternType is FillPatternType;

        annotation { "Name" : "Distance",
                    "Description" : "The distance between the center of each instance" }
        isLength(definition.distance, NONNEGATIVE_LENGTH_BOUNDS);

        annotation { "Name" : "Border",
                    "Description" : "The width of the exclusion zone at every edge of the target face" }
        isLength(definition.border, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);
    }
    {
        // Check inputs
        verifyNonemptyQuery(context, definition, "entities", "Select faces to pattern.");
        verifyNonemptyQuery(context, definition, "target", "Select a planar face to fill.");
        verifyNonemptyQuery(context, definition, "direction", "Select an edge, face or mate connector to define pattern direction.");

        const facePlane = evPlane(context, { "face" : definition.target });
        const normal = facePlane.normal;

        var edgesInFace = qCoincidesWithPlane(qAdjacent(definition.entities, AdjacencyType.EDGE, EntityType.EDGE), facePlane);

        if (isQueryEmpty(context, qSketchFilter(definition.target, SketchObject.NO)))
        {
            edgesInFace = startTracking(context, {
                        "subquery" : edgesInFace,
                        "lastOperationId" : lastModifyingOperationId(context, edgesInFace) });
                        
            const sketchId = lastModifyingOperationId(context, last(evaluateQuery(context, edgesInFace)));
            edgesInFace = qIntersection(edgesInFace, qCreatedBy(sketchId));
        }

        const targetEdges = qSubtraction(qAdjacent(definition.target, AdjacencyType.EDGE, EntityType.EDGE), edgesInFace);

        if (isQueryEmpty(context, edgesInFace))
            throw regenError("The faces to pattern must share edges with the target face.", ["entities"]);

        const path = constructPath(context, edgesInFace);

        if (!path.closed)
            throw regenError("Selected faces do not form a closed loop.", ["entities"]);

        var direction;

        if (!isQueryEmpty(context, qBodyType(definition.direction, BodyType.MATE_CONNECTOR)))
        {
            // Unconventionally, get the X axis of the mate connector
            direction = evMateConnector(context, {
                            "mateConnector" : definition.direction
                        }).xAxis;
        }
        else
        {
            direction = extractDirection(context, definition.direction);
        }

        if (parallelVectors(direction, normal))
            throw regenError("Direction cannot be perpendicular to the target face.", ["direction"]);

        var transforms = [];
        var instanceNames = [];
        var newTransforms = [];
        var newInstanceNames = [];
        var remainingTransform = {};
        var patternDefinition = {};
        const featureId = id + "process";

        startFeature(context, featureId);
        {
            // Ensure direction vector is in-plane
            direction -= dot(direction, normal) * normal;

            if (definition.patternType == FillPatternType.DIAMOND)
                direction = rotationMatrix3d(normal, 45 * degree) * direction;

            // Get cSys and transforms for target face
            const cSys = coordSystem(facePlane.origin, direction, normal);

            direction *= definition.distance;

            // For a hexagonal pattern there are two directions, one at an angle of 60º from the other
            // Patterning in both those directions produces a hexagonal pattern with equal spacing
            const angle = definition.patternType == FillPatternType.HEX ? 60 * degree : 90 * degree;

            const patternAngle = rotationMatrix3d(normal, angle) * direction;

            opExtractWires(context, featureId + "seed", {
                        "edges" : edgesInFace
                    });

            const seed = qCreatedBy(featureId + "seed", EntityType.BODY);

            checkSpacing(context, featureId, definition, seed, direction, patternAngle);

            const borderEdges = startTracking(context, targetEdges);
            const borderHighlight = startTracking(context, targetEdges);

            opExtractSurface(context, featureId + "targetFace", {
                        "faces" : definition.target,
                        "offset" : 0 * meter
                    });

            const targetFace = qCreatedBy(featureId + "targetFace", EntityType.FACE);
            const faceEdges = evaluateQuery(context, borderEdges);
            const border = -definition.border + 1e-6 * meter;
            var edgeChangeOptions = [];

            for (var edge in faceEdges)
            {
                edgeChangeOptions = append(edgeChangeOptions, {
                            "edge" : edge,
                            "face" : targetFace,
                            "offset" : border
                        });
            }

            try silent
            {
                opEdgeChange(context, featureId + "offset", {
                            "edgeChangeOptions" : edgeChangeOptions
                        });
            }
            catch
            {
                throw regenError("Border width too large.", ["border"]);
            }

            opExtractWires(context, featureId + "target", {
                        "edges" : qAdjacent(targetFace, AdjacencyType.EDGE, EntityType.EDGE)
                    });

            const faceBox = try(evBox3d(context, {
                            "topology" : targetFace,
                            "cSys" : cSys
                        }));

            const toolBox = try(evBox3d(context, {
                            "topology" : edgesInFace,
                            "cSys" : cSys
                        }));

            var toolCenter = box3dCenter(toolBox);

            // Estimate the maximum number of instances
            const instanceCount = getInstanceCount(definition, faceBox, toolCenter, angle);

            if (instanceCount.estimate > 10000) // this is an arbitrary number to prevent an infinite spinner
                throw regenError("Too many instances in the pattern (~" ~ instanceCount.estimate ~ "). Try a larger spacing.");

            toolCenter = toWorld(cSys, toolCenter);

            // Loop in two directions to see if an instance should be included
            for (var x = instanceCount.minX; x < instanceCount.maxX; x += 1)
            {
                const xDirection = direction * x;

                for (var y = instanceCount.minY; y < instanceCount.maxY; y += 1)
                {
                    if (x == 0 && y == 0) // Zero transform = initial position => Skip
                        continue;

                    const translation = xDirection + patternAngle * y;
                    const toolInstanceCenter = translation + toolCenter;

                    if (!isQueryEmpty(context, qContainsPoint(targetFace, toolInstanceCenter)))
                    {
                        transforms = append(transforms, transform(translation));
                        instanceNames = append(instanceNames, x ~ "/" ~ y);
                    }
                }
            }

            if (size(instanceNames) == 0)
                throw regenError("No instances created. Check distance or border values.", ["distance", "border"]);

            patternDefinition = {
                    "transforms" : transforms,
                    "instanceNames" : instanceNames,
                    "copyPropertiesAndAttributes" : true,
                    "entities" : seed
                };

            const testPattern = featureId + "testPattern";

            remainingTransform = getRemainderPatternTransform(context, { "references" : patternDefinition.entities });

            applyPattern(context, testPattern, patternDefinition, remainingTransform);

            const borderOverlaps = evCollision(context, {
                        "tools" : qCreatedBy(testPattern, EntityType.BODY),
                        "targets" : qCreatedBy(featureId + "target", EntityType.BODY)
                    });

            if (size(borderOverlaps) > 0)
            {
                var borderInstances = makeArray(size(borderOverlaps));

                for (var i, borderOverlap in borderOverlaps)
                    borderInstances[i] = borderOverlap.toolBody;

                const borderInstanceQuery = qUnion(borderInstances);

                highlightEntities(context, borderInstanceQuery, DebugColor.RED);

                for (var i, instanceName in instanceNames)
                {
                    if (isQueryEmpty(context, qIntersection(borderInstanceQuery, qPatternInstances(testPattern, instanceName, EntityType.BODY))))
                    {
                        newTransforms = newTransforms->append(transforms[i]);
                        newInstanceNames = newInstanceNames->append(instanceName);
                    }
                }
            }
            else
            {
                newTransforms = transforms;
                newInstanceNames = instanceNames;
            }

            highlightEntities(context, qUnion(evaluateQuery(context, borderHighlight)), DebugColor.BLUE);

            abortFeature(context, featureId);
        }

        patternDefinition = {
                "patternType" : PatternType.FACE,
                "transforms" : newTransforms,
                "instanceNames" : newInstanceNames,
                "copyPropertiesAndAttributes" : true,
                "entities" : definition.entities
            };

        remainingTransform = getRemainderPatternTransform(context, { "references" : patternDefinition.entities });

        try
        {
            applyPattern(context, id + "patternFaces", patternDefinition, remainingTransform);
        }
        catch (error)
        {
            throw regenError("Pattern failed due to non-manifold geometry. Try increasing border width.", ["border"]);
        }

        reportFeatureInfo(context, id, "Pattern contains " ~ size(newInstanceNames) + 1 ~ " instances.");

        setFeatureComputedParameter(context, id, {
                    "name" : "instances",
                    "value" : size(newInstanceNames) + 1
                });
    });

function checkSpacing(context is Context, id is Id, definition is map, seed is Query, direction is Vector, patternAngle is Vector)
{
    var transforms = [];
    var instanceNames = [];

    for (var x = -1; x <= 1; x += 1)
    {
        const xDirection = direction * x;

        for (var y = -1; y <= 1; y += 1)
        {
            if (x == 0 && y == 0) // Zero transform = initial position => Skip
                continue;

            const translation = xDirection + patternAngle * y;
            transforms = append(transforms, transform(translation));
            instanceNames = append(instanceNames, x ~ "-" ~ y);
        }
    }

    opPattern(context, id + "checkPattern", {
                "entities" : seed,
                "transforms" : transforms,
                "instanceNames" : instanceNames
            });

    const overlap = evDistance(context, {
                "side0" : seed,
                "side1" : qCreatedBy(id + "checkPattern", EntityType.BODY)
            });

    if (tolerantLessThanOrEqual(overlap.distance, 0 * meter))
    {
        highlightEntities(context, qCreatedBy(id + "checkPattern", EntityType.BODY), DebugColor.RED);
        throw regenError("Pattern instances self-intersect.", ["distance"]);
    }
}

function getInstanceCount(definition is map, face is Box3d, toolCenter is Vector, angle is ValueWithUnits) returns map
{
    const spacing = sin(angle) * definition.distance;

    var minX = floor((face.minCorner[0] - toolCenter[0]) / spacing);
    var maxX = ceil((face.maxCorner[0] - toolCenter[0]) / spacing);
    const minY = floor((face.minCorner[1] - toolCenter[1]) / spacing);
    const maxY = ceil((face.maxCorner[1] - toolCenter[1]) / spacing);

    if (definition.patternType == FillPatternType.HEX)
    {
        // Hex pattern creates a parallelogram shape outside of the face
        // so removing excess instances in X direction for performance
        minX -= ceil(maxY / 3);
        maxX -= floor(minY / 3);
    }

    const estimate = (abs(minX) + maxX + 1) * (abs(minY) + maxY + 1);

    return { "minX" : minX, "maxX" : maxX, "minY" : minY, "maxY" : maxY, "estimate" : estimate };
}

function highlightEntities(context is Context, entities is Query, color is DebugColor)
{
    for (var i = 0; i < 3; i += 1)
        addDebugEntities(context, entities, color);
}
