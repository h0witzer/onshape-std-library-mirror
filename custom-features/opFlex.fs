FeatureScript 1793;
export import(path : "onshape/std/geometry.fs", version : "1793.0");

icon::import(path : "c624ed6117bc781df9db34a7", version : "8c1fdbdfcd870db45cc00916");
image::import(path : "2150aaa7a0e55c57536755d0", version : "24ccd4a440ee9c760daa3bc1");


/**
 * Taper types.
 */
export enum TaperType
{
    annotation { "Name" : "Scale uniformly" }
    TAPER_UNIFORMLY,
    annotation { "Name" : "Scale horizontally" }
    TAPER_HORIZONTALLY,
    annotation { "Name" : "Scale vertically" }
    TAPER_VERTICALLY
}


/**
 * Performs flex (taper, twist and deform) for a body, face, edge or vertex.
 *
 * @param context {Context}
 * @param id : @autocomplete `id + "flex"`
 * @param definition {{
 *      @field entities {Query}:
 *              The parts to flex.
 *      @field baseLine {Query}:
 *              The base line edge.
 *      @field isBaseLineFlipped {boolean}:
 *              The base line direction.
 *
 *      @field edgeSamplingStep {ValueWithUnits}:
 *              Edge sampling step.
 *      @field faceSamplingStep {ValueWithUnits}:
 *              Face sampling step.
 *
 *      @field isTaper {boolean}:
 *              Taper control.
 *      @field taperType {TaperType}:       @requiredif {`isTaper` is true}
 *              Taper type.
 *      @field startScale {number}:         @requiredif {`isTaper` is true}
 *              Taper start scale.
 *      @field endScale {number}:           @requiredif {`isTaper` is true}
 *              Taper end scale.
 *      @field isScaleOverflow {boolean}:   @requiredif {`isTaper` is true}
 *              Taper overflow scale.
 *
 *      @field isTwist {boolean}:
 *              Twist control.
 *      @field startAngle {ValueWithUnits}: @requiredif {`isTwist` is true}
 *              Twist start angle.
 *      @field endAngle {ValueWithUnits}:   @requiredif {`isTwist` is true}
 *              Twist end angle.
 *
 *      @field isDeform {boolean}:
 *              Deform control.
 *      @field targetCurve {Query}:         @requiredif {`isDeform` is true}
 *              The target path.
 *
 *      @field isSoftenTransition {boolean}:
 *              Soften flex transition.
 *
 *
 * }}
 */
annotation {
        "Feature Type Name" : "Flex",
        "Icon" : icon::BLOB_DATA,
        "Description Image" : image::BLOB_DATA,
        "Feature Type Description" : "Flex (taper, twist or deform) selected entities.",
        "Editing Logic Function" : "onFlexFeatureChange" }
export const opFlex = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        /* Base entities ***************************************************************/
        annotation {
                    "Name" : "Base entities",
                    "Description" : "Bodies (solids), faces, edges or vertices to be flexed.",
                    "Filter" : ((EntityType.BODY && BodyType.SOLID) || EntityType.FACE || EntityType.EDGE || EntityType.VERTEX) && ConstructionObject.NO }
        definition.entities is Query;

        /* Base line *******************************************************************/
        annotation {
                    "Name" : "Base line (edge on sketch)",
                    "Description" : "Sketch edge that defines base x-axis and base plane.",
                    "Filter" : GeometryType.LINE, "MaxNumberOfPicks" : 1 }
        definition.baseLine is Query;

        annotation {
                    "Name" : "Base line direction",
                    "Description" : "Direction of base axis.",
                    "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.isBaseLineFlipped is boolean;

        /* Sampling ********************************************************************/
        annotation {
                    "Name" : "Edge sampling step",
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.edgeSamplingStep, { (millimeter) : [0.1, 0.5, 1000] } as LengthBoundSpec);

        annotation {
                    "Name" : "Face sampling step",
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.faceSamplingStep, { (millimeter) : [0.1, 2, 1000] } as LengthBoundSpec);

        annotation {
                    "Name" : "Show samples" }
        definition.isShowSamples is boolean;

        /* Taper ***********************************************************************/
        annotation {
                    "Name" : "Taper" }
        definition.isTaper is boolean;
        if (definition.isTaper)
        {
            annotation {
                        "Group Name" : "Taper",
                        "Driving Parameter" :
                        "isTaper",
                        "Collapsed By Default" : false }
            {
                annotation {
                            "Name" : "Taper type" }
                definition.taperType is TaperType;

                annotation {
                            "Name" : "Width start scale",
                            "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE,
                            "Default" : 1.0 }
                isReal(definition.startScale, SCALE_BOUNDS);
                annotation {
                            "Name" : "Width end scale",
                            "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE,
                            "Default" : 1.0 }
                isReal(definition.endScale, SCALE_BOUNDS);

                annotation {
                            "Name" : "Scale overflow" }
                definition.isScaleOverflow is boolean;
            }
        }

        /* Twist ***********************************************************************/
        annotation {
                    "Name" : "Twist" }
        definition.isTwist is boolean;
        if (definition.isTwist)
        {
            annotation {
                        "Group Name" : "Twist",
                        "Driving Parameter" : "isTwist",
                        "Collapsed By Default" : false }
            {
                annotation {
                            "Name" : "Start angle",
                            "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
                isAngle(definition.startAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                annotation {
                            "Name" : "End angle",
                            "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
                isAngle(definition.endAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
            }
        }

        /* Deform **********************************************************************/
        annotation {
                    "Name" : "Deform" }
        definition.isDeform is boolean;
        if (definition.isDeform)
        {
            annotation {
                        "Group Name" : "Deform",
                        "Driving Parameter" : "isDeform",
                        "Collapsed By Default" : false }
            {
                annotation {
                            "Name" : "Target curve (edge on sketch)",
                            "Description" : "Sketch path that defines path and plane where entities are transformed.",
                            "Filter" : EntityType.EDGE && SketchObject.YES, "MaxNumberOfPicks" : 1 }
                definition.targetCurve is Query;
            }
        }

        /* Configurations **************************************************************/
        annotation {
                    "Name" : "Soften flex transition",
                    "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.isSoftenTransition is boolean;

        /* Debug ***********************************************************************/
        annotation {
                    "Name" : "Debug",
                // "UIHint" : UIHint.ALWAYS_HIDDEN
                }
        definition.isDebug is boolean;
        if (definition.isDebug)
        {
            annotation {
                        "Group Name" : "Debug",
                        "Driving Parameter" : "isDebug",
                        "Collapsed By Default" : false }
            {
                annotation { "Name" : "1 - Split faces and bodies with cube mesh" }
                definition.isDebug1 is boolean;
                annotation { "Name" : "2 - Translate edges of splitted faces into target space" }
                definition.isDebug2 is boolean;
                annotation { "Name" : "3 - Fill translated edges to create target faces" }
                definition.isDebug3 is boolean;
                annotation { "Name" : "4 - Join target faces to create faces and bodies" }
                definition.isDebug4 is boolean;
            }
        }
    }
    {
        /* Check input values **********************************************************/
        /* Illegal input */
        var emptyInput = [];
        if (isQueryEmpty(context, definition.entities) == true)
        {
            emptyInput = append(emptyInput, "entities");
        }
        if (isQueryEmpty(context, definition.baseLine) == true)
        {
            emptyInput = append(emptyInput, "baseLine");
        }
        if (size(emptyInput) > 0)
        {
            throw regenError(ErrorStringEnum.CANNOT_BE_EMPTY, emptyInput);
        }

        /* Adjust inputs */
        if (isQueryEmpty(context, definition.targetCurve) == true)
        {
            definition.isDeform = false;
        }
        if (definition.isDebug == false)
        {
            definition.isDebug1 = true;
            definition.isDebug2 = true;
            definition.isDebug3 = true;
            definition.isDebug4 = true;
        }
        if (definition.isTaper == false &&
            definition.isTwist == false &&
            definition.isDeform == false)
        {
            return;
        }


        /* Feature bahaviour ***********************************************************/
        /* Base path and base coordinate system */
        try
        {
            /* Base path and base plane */
            definition.basePath = constructPath(context, definition.baseLine);
            definition.basePathLength = evPathLength(context, definition.basePath);

            /* Base path direction (flipped) */
            definition.basePath.flipped[0] = definition.isBaseLineFlipped;

            /* First base path point (origin and direction) */
            var firstPoint = evPathTangentLines(context, definition.basePath, [0]).tangentLines[0];

            for (var i = 0; i < 1; i += 1)
            {
                /* Try create base coordinate system using base line and target sketch plane */
                try silent
                {
                    if (definition.isDeform == true)
                    {
                        var targetPlane = evOwnerSketchPlane(context, { "entity" : definition.targetCurve });
                        definition.baseCoordSys = coordSystem(firstPoint.origin, firstPoint.direction, targetPlane.normal);
                        break;
                    }
                }

                /* Try create base coordinate system using base line and sketch plane */
                try silent
                {
                    var basePlane = evOwnerSketchPlane(context, { "entity" : definition.baseLine });
                    definition.baseCoordSys = coordSystem(firstPoint.origin, firstPoint.direction, basePlane.normal);
                    break;
                }

                /* Base coordinate system using base line and arbitrary z-direction */
                var zDirection = perpendicularVector(firstPoint.direction);
                definition.baseCoordSys = coordSystem(firstPoint.origin, firstPoint.direction, zDirection);

                break;
            }

            /* Show base coordinate system */
            addDebugCoordinateSystem(context, definition.baseCoordSys, definition.basePathLength);
        }
        catch (error)
        {
            debug(context, definition.baseLine, DebugColor.RED);
            throw error;
        }

        /* Target path and target coordinate system */
        try
        {
            if (definition.isDeform == true)
            {
                definition.targetPath = constructPath(context, definition.targetCurve);
                definition.targetPathLength = evPathLength(context, definition.targetPath);

                /* Figure out first point and direction */
                var firstPoint = evPathTangentLines(context, definition.targetPath, [0]).tangentLines[0];

                /* Figure out target coordination system at the beginning of path */
                var targetPlane = evOwnerSketchPlane(context, { "entity" : definition.targetCurve });
                definition.targetCoordSys = coordSystem(firstPoint.origin, firstPoint.direction, targetPlane.normal);

                /* Show target coordinate system */
                addDebugCoordinateSystem(context, definition.targetCoordSys, definition.basePathLength);
            }
            else
            {
                /* Target path and plane same as base */
                definition.targetPath = definition.basePath;
                definition.targetPathLength = definition.basePathLength;
                definition.targetCoordSys = definition.baseCoordSys;
            }
        }
        catch (error)
        {
            debug(context, definition.targetCurve, DebugColor.RED);
            throw error;
        }


        /* Flex entities */
        definition.convertFunction = function(point)
            {
                return convertPoint(context, definition, point);
            };
        flexEntities(context, id, definition);
    }, {
            /* Default values */
            isBaseLineFlipped : false,
            edgeSamplingStep : 1.0 * millimeter,
            faceSamplingStep : 5.0 * millimeter,
            isShowSamples : false,
            isTaper : false,
            isTwist : false,
            isDeform : false,
            isSoftenTransition : true,
            isDebug : false
        });


/**
 * Editing logic function.
 */
export function onFlexFeatureChange(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, specifiedParameters is map, hiddenBodies is Query) returns map
{
    var newValue = undefined;

    /* Find out changed configuration */
    if (oldDefinition.isDebug1 != definition.isDebug1)
    {
        newValue = definition.isDebug1;
    }
    if (oldDefinition.isDebug2 != definition.isDebug2)
    {
        newValue = definition.isDebug2;
    }
    if (oldDefinition.isDebug3 != definition.isDebug3)
    {
        newValue = definition.isDebug3;
    }
    if (oldDefinition.isDebug4 != definition.isDebug4)
    {
        newValue = definition.isDebug4;
    }

    /* Propagate changed value */
    if (newValue == true)
    {
        if (definition.isDebug4 == true)
        {
            definition.isDebug3 = true;
        }
        if (definition.isDebug3 == true)
        {
            definition.isDebug2 = true;
        }
        if (definition.isDebug2 == true)
        {
            definition.isDebug1 = true;
        }
    }
    if (newValue == false)
    {
        if (definition.isDebug1 == false)
        {
            definition.isDebug2 = false;
        }
        if (definition.isDebug2 == false)
        {
            definition.isDebug3 = false;
        }
        if (definition.isDebug3 == false)
        {
            definition.isDebug4 = false;
        }
    }

    return definition;
}


/**
 * Flex (twist, taper, deform) given entities (body, face, edge, point).
 */
function flexEntities(context is Context, id is Id, definition is map)
{
    if (definition.isDebug1 == false)
    {
        return;
    }

    /* Selected entities ***********************************************************/
    var bodyTable = evaluateQuery(context, qEntityFilter(definition.entities, EntityType.BODY));
    var faceTable = evaluateQuery(context, qEntityFilter(definition.entities, EntityType.FACE));
    var edgeTable = evaluateQuery(context, qEntityFilter(definition.entities, EntityType.EDGE));
    var vertexTable = evaluateQuery(context, qEntityFilter(definition.entities, EntityType.VERTEX));

    /* Split entities into sub faces ***********************************************/
    const subFaces = splitEntities(context, id + "splitEntities", definition, qUnion(concatenateArrays([bodyTable, faceTable])));
    var baseSubFaceTable = evaluateQuery(context, subFaces);

    /* Temporary edges (edges of subfaces) *****************************************/
    var temporaryEdges = qAdjacent(qUnion(baseSubFaceTable), AdjacencyType.EDGE, EntityType.EDGE);
    for (var edge in evaluateQuery(context, temporaryEdges))
    {
        edge.temporary = true;
        edgeTable = append(edgeTable, edge);
    }

    /* Tables for temporary entities ***********************************************/
    var temporaryEdgeTable = [];

    /* Show all edges **************************************************************/
    if (definition.isShowSamples == true)
    {
        addDebugEntities(context, qUnion(edgeTable), DebugColor.GREEN);
    }

    /* Create target edges *********************************************************/
    if (definition.isDebug2 == false)
    {
        return;
    }
    for (var edgeIndex, edge in edgeTable)
    {
        /* Flex source edge -> target edge  */
        try
        {
            /* Create target edge */
            var targetEdge = flexEdge(context, id + "flexEdge" + edgeIndex, definition, edge);

            /* Link base edge to the target edge */
            setAttribute(context, {
                        "entities" : edge,
                        "name" : "targetEdges",
                        "attribute" : targetEdge
                    });

            /* Show target edge */
            if (definition.isShowSamples == true)
            {
                addDebugEntities(context, targetEdge, DebugColor.BLUE);
            }

            /* Check if temporary */
            if (edge.temporary == true)
            {
                temporaryEdgeTable = append(temporaryEdgeTable, targetEdge);
            }
        }
        catch
        {
            reportFeatureWarning(context, id, "Translating edges failed.");

            /* Show error in preview */
            addDebugEntities(context, edge, DebugColor.RED);

            return;
        }
    }

    /* Create target vertices ******************************************************/
    for (var vertexIndex, vertex in vertexTable)
    {
        /* Flex source vertex -> target vertex  */
        try
        {
            /* Create target vertex */
            var targetVertex = flexVertex(context, id + "flexVertex" + vertexIndex, definition, vertex);

            addDebugEntities(context, targetVertex, DebugColor.BLACK);
        }
        catch
        {
            reportFeatureWarning(context, id, "Translating vertices failed.");

            /* Show error in preview */
            addDebugEntities(context, vertex, DebugColor.RED);

            return;
        }
    }

    /* Create new subfaces **********************************************************/
    if (definition.isDebug3 == false)
    {
        return;
    }
    for (var faceIndex, face in baseSubFaceTable)
    {
        /* Base edges for this face */
        var baseEdges = qLoopEdges(face);

        /* Find target edges linked to base edges */
        var targetEdges = getAttributes(context, {
                "entities" : baseEdges,
                "name" : "targetEdges"
            });

        /* Surface filling id */
        var fillSurfaceId = id + ("fillSurface" ~ faceIndex);

        try
        {
            /* Create new face either using loft or fill surface */
            try silent
            {
                if (size(targetEdges) == 2)
                {
                    opLoft(context, fillSurfaceId + 1, {
                                "profileSubqueries" : [targetEdges[0], targetEdges[1]],
                                "bodyType" : ToolBodyType.SURFACE
                            });
                }
                else if (size(targetEdges) == 3)
                {
                    /* Use loft if only three edges (avoid INTERSECTING_EDGES with opFillSurface ) */
                    opLoft(context, fillSurfaceId + 2, {
                                "profileSubqueries" : [targetEdges[0], targetEdges[1]],
                                "guideSubqueries" : [targetEdges[2]],
                                "bodyType" : ToolBodyType.SURFACE
                            });
                }
                else
                {
                    throw "Not lofted";
                }
            }
            catch
            {
                /* Get tangent points of base sub face */
                const tangentPlanes = getAttribute(context, {
                            "entity" : face,
                            "name" : "tangentPlanes"
                        });

                /* Create guide points in taget space */
                var pointsId = id + ("point" ~ faceIndex);
                for (var tangentIndex, tangent in tangentPlanes)
                {
                    var point = definition.convertFunction(tangent.origin);

                    opPoint(context, pointsId + tangentIndex, {
                                "point" : point
                            });

                    if (definition.isShowSamples == true)
                    {
                        addDebugPoint(context, tangent.origin, DebugColor.GREEN);
                        addDebugPoint(context, point, DebugColor.BLUE);
                    }
                }

                /* Fill surface */
                try silent
                {
                    opFillSurface(context, fillSurfaceId + 3, {
                                "edgesG0" : qUnion(targetEdges),
                                "edgesG1" : qNothing(),
                                "edgesG2" : qNothing(),
                                "guideVertices" : qCreatedBy(pointsId),
                                "showIsocurves" : false,
                            });
                }
                catch
                {
                    opFillSurface(context, fillSurfaceId + 4, {
                                "edgesG0" : qUnion(targetEdges),
                                "edgesG1" : qNothing(),
                                "edgesG2" : qNothing(),
                                "guideVertices" : qNothing(),
                                "showIsocurves" : false,
                            });
                }

                /* Delete points */
                try silent
                {
                    opDeleteBodies(context, id + ("deletePoints" ~ faceIndex), {
                                "entities" : qCreatedBy(pointsId)
                            });
                }
            }

            var targetFaces = qCreatedBy(fillSurfaceId, EntityType.BODY);

            /* Link base face to the target face */
            setAttribute(context, {
                        "entities" : face,
                        "name" : "targetFaces",
                        "attribute" : targetFaces
                    });
        }

        catch
        {
            reportFeatureWarning(context, id, "Filling edges failed. (Try to adjust edge sampling and grid size.)");

            /* Show error in preview */
            addDebugEntities(context, baseEdges, DebugColor.RED);
            addDebugEntities(context, qUnion(targetEdges), DebugColor.RED);

            return;
        }
    }

    /* Delete temporary edges *******************************************************/
    try silent
    {
        opDeleteBodies(context, id + "deleteEdges", {
                    "entities" : qUnion(temporaryEdgeTable)
                });
    }


    /* Create target bodies ********************************************************/
    if (definition.isDebug4 == false)
    {
        return;
    }
    for (var entintyIndex, entity in concatenateArrays([bodyTable, faceTable]))
    {
        /* Sub faces */
        var subFaces = getAttributes(context, {
                "entities" : entity,
                "name" : "subFaces"
            });

        /* Find target subfaces linked to these base subfaces */
        var targetSubFaces = getAttributes(context, {
                "entities" : qUnion(subFaces),
                "name" : "targetFaces"
            });

        try
        {
            /* Join sub faces into surface or solid */
            try silent
            {
                if (size(targetSubFaces) > 1)
                {
                    opBoolean(context, id + ("booleanBody" ~ entintyIndex), {
                                "tools" : qUnion(targetSubFaces),
                                "operationType" : BooleanOperationType.UNION,
                                "makeSolid" : true,
                                "keepTools" : false,
                            });
                }
            }
            catch
            {
                var bodies = evaluateQuery(context, qEntityFilter(entity, EntityType.BODY));
                if (size(bodies) == 0)
                {
                    throw "Joining sub faces failed";
                }
                else
                {
                    /* Enclose bodies if boolean failed to make solid */
                    enclose(context, id + ("enclose" ~ entintyIndex), {
                                "entities" : qUnion(targetSubFaces),
                                "mergeResults" : false,
                                "keepTools" : false
                            });
                }
            }
        }
        catch
        {
            reportFeatureWarning(context, id, "Joining target faces failed.  (Try to adjust edge sampling and grid size.)");

            /* Show error in preview */
            debug(context, qUnion(subFaces), DebugColor.RED);
            debug(context, qUnion(targetSubFaces), DebugColor.RED);

            return;
        }
    }

    /* Delete temporary subfaces ****************************************************/
    try silent
    {
        opDeleteBodies(context, id + "deleteSubFaces", {
                    "entities" : qUnion(baseSubFaceTable)
                });
    }
}


/**
 * Convert point from the base coordinate system to the target coordinate system.
 */
function convertPoint(context is Context, definition is map, point is Vector) returns Vector
{
    /* Map to base coordinate system ***********************************************/
    point = fromWorld(definition.baseCoordSys, point);

    /* Calculate point relative position on base line ******************************/
    var pathPosition = point[0] / definition.basePathLength;
    var pathPositionOverflow = undefined;
    if (pathPosition < 0.0)
    {
        pathPosition = 0.0;
        pathPositionOverflow = point[0];
    }
    if (pathPosition > 1.0)
    {
        pathPosition = 1.0;
        pathPositionOverflow = point[0] - definition.basePathLength;
    }

    /* Calculate tool force (soften if requested) **********************************/
    var toolForce = pathPosition;
    if (definition.isSoftenTransition)
    {
        /* Use parabola to soften */
        if (pathPosition < 0.5)
        {
            toolForce = 2 * pathPosition * pathPosition;
        }
        else
        {
            toolForce = 1 - 2 * (1 - pathPosition) * (1 - pathPosition);
        }
    }

    /* Taper (in base coordinate system) *******************************************/
    if (definition.isTaper == true)
    {
        /* Calculate scale on current path position */
        var scale = (1.0 - toolForce) * definition.startScale + toolForce * definition.endScale;

        /* Scale point coordinates */
        var transform;
        if (definition.taperType == TaperType.TAPER_UNIFORMLY)
        {
            transform = scaleNonuniformly(1.0, scale, scale);
        }
        else if (definition.taperType == TaperType.TAPER_HORIZONTALLY)
        {
            transform = scaleNonuniformly(1.0, scale, 1.0);
        }
        else if (definition.taperType == TaperType.TAPER_VERTICALLY)
        {
            transform = scaleNonuniformly(1.0, 1.0, scale);
        }
        point = transform.linear * point;

        /* Scale overflow */
        if (definition.isScaleOverflow)
        {
            if (pathPositionOverflow != undefined)
            {
                pathPositionOverflow *= scale;
            }
        }
    }

    /* Twist (in base coordinate system) *******************************************/
    if (definition.isTwist == true)
    {
        /* Calculate angle on current path position */
        var angle = (1.0 - toolForce) * definition.startAngle + toolForce * definition.endAngle;

        /* Rotate point around x-axis */
        const transform = rotationAround(X_AXIS, angle);

        point = transform.linear * point;
    }

    /* Convert point coordinates into target path **********************************/
    /* Tangent at [pathPosition] */
    var targetTangent = evPathTangentLines(context, definition.targetPath, [pathPosition]).tangentLines[0];

    /* Normal at [pathPosition] */
    const rotMat = rotationMatrix3d(definition.targetCoordSys.zAxis, 90 * degree);
    const targetNormal = rotMat * targetTangent.direction;

    /* Use path extension if overflow */
    if (pathPositionOverflow != undefined)
    {
        targetTangent.origin += targetTangent.direction * pathPositionOverflow;
    }

    var targetPoint = targetTangent.origin + point[1] * targetNormal + point[2] * definition.targetCoordSys.zAxis;

    return targetPoint;
}


/**
 * Show coordinate system.
 */
function addDebugCoordinateSystem(context is Context, coordSystem is CoordSystem, length is ValueWithUnits)
{
    const ARROW_RADIUS = 0.05 * length;

    addDebugArrow(context, coordSystem.origin,
        coordSystem.origin + coordSystem.xAxis * length, ARROW_RADIUS, DebugColor.BLUE);
    addDebugArrow(context, coordSystem.origin,
        coordSystem.origin + yAxis(coordSystem) * length, ARROW_RADIUS, DebugColor.YELLOW);
    addDebugArrow(context, coordSystem.origin,
        coordSystem.origin + coordSystem.zAxis * length, ARROW_RADIUS, DebugColor.RED);

}


/**
 * Create target vertex.
 */
function flexVertex(context is Context, id is Id, definition is map, vertex is Query) returns Query
{
    const sourcePoint = evVertexPoint(context, { "vertex" : vertex });
    const targetPoint = definition.convertFunction(sourcePoint);

    /* Create new vertex */
    opPoint(context, id, {
                "point" : targetPoint
            });

    return qCreatedBy(id);
}

/**
 * Create target edge.
 */
function flexEdge(context is Context, id is Id, definition is map, edge is Query) returns Query
{
    const targetPoints = flexEdgePoints(context, definition, edge);

    /* Create new edge (3D cubic spline through target points) */
    opFitSpline(context, id, { "points" : targetPoints });

    return qCreatedBy(id, EntityType.EDGE);
}

/**
 * Sample an edge and return target points (array).
 */
function flexEdgePoints(context is Context, definition is map, edge is Query) returns array
{
    /* Default values **************************************************************/
    if (definition.edgeSamplingStep == undefined)
    {
        definition.edgeSamplingStep = 0.5 * millimeter;
    }

    /* Sample count on edge ********************************************************/
    var edgeLength = evLength(context, { "entities" : edge });
    var sampleCount = round(edgeLength / definition.edgeSamplingStep);
    if (sampleCount % 2 == 0)
    {
        sampleCount += 1; // Make odd so that we have point on 0.5
    }
    if (sampleCount < 3)
    {
        sampleCount = 3;
    }

    /* Source edge tangents ********************************************************/
    const distributionArray = range(0, 1, sampleCount);
    var sourceTangents = evEdgeTangentLines(context, {
            "edge" : edge,
            "parameters" : distributionArray,
            "arcLengthParameterization" : true
        });

    /* evEdgeTangentLines does not return start/end vertices accurately,
       replace them with original */
    if (isClosed(context, edge) == false)
    {
        sourceTangents[0].origin = evVertexPoint(context, { "vertex" : qEdgeVertex(edge, true) });
        sourceTangents[size(sourceTangents) - 1].origin = evVertexPoint(context, { "vertex" : qEdgeVertex(edge, false) });
    }
    /* Convert source edge points to target coordinate system one by one ***********/
    const targetPoints = mapArray(sourceTangents, function(tangent)
        {
            var point = definition.convertFunction(tangent.origin);

            if (definition.isShowSamples == true)
            {
                addDebugPoint(context, tangent.origin, DebugColor.GREEN);
                addDebugPoint(context, point, DebugColor.BLUE);
            }

            return point;
        });

    return targetPoints;
}

/**
 * Create subfaces by splitting entities (faces and bodies).
 */
function splitEntities(context is Context, id is Id, definition is map, entities is Query)
{
    /* Something to do? ************************************************************/
    if (isQueryEmpty(context, entities) == true)
    {
        return entities;
    }

    /* Calculate surface boundaries ************************************************/
    const entityBounds = evBox3d(context, {
                "topology" : entities,
                "tight" : false
            });

    /* Round to the closest boundary */
    var splitDistance = definition.faceSamplingStep;
    var surfaceSize = entityBounds.maxCorner - entityBounds.minCorner;
    surfaceSize[0] = ceil(surfaceSize[0] / splitDistance + 0.001) * splitDistance;
    surfaceSize[1] = ceil(surfaceSize[1] / splitDistance + 0.001) * splitDistance;
    surfaceSize[2] = ceil(surfaceSize[2] / splitDistance + 0.001) * splitDistance;

    /* Figure out middle point */
    var surfaceMiddle = entityBounds.minCorner + surfaceSize / 2;


    /* Create cutting planes *******************************************************/
    var planeIndex = 0;

    /* Planes with normal X */
    var x = 0 * millimeter;
    while (x <= surfaceSize[0])
    {
        opPlane(context, id + "plane" + planeIndex, {
                    "plane" : plane(vector(entityBounds.minCorner[0] + x, surfaceMiddle[1], surfaceMiddle[2]), X_DIRECTION),
                    "width" : surfaceSize[1],
                    "height" : surfaceSize[2]
                });

        x = x + splitDistance;
        planeIndex += 1;
    }

    /* Planes with normal Y */
    var y = 0 * millimeter;
    while (y <= surfaceSize[1])
    {
        opPlane(context, id + "plane" + planeIndex, {
                    "plane" : plane(vector(surfaceMiddle[0], entityBounds.minCorner[1] + y, surfaceMiddle[2]), Y_DIRECTION),
                    "width" : surfaceSize[0],
                    "height" : surfaceSize[2]
                });

        y = y + splitDistance;
        planeIndex += 1;
    }

    /* Planes with normal Z */
    var z = 0 * millimeter;
    while (z <= surfaceSize[2])
    {
        opPlane(context, id + "plane" + planeIndex, {
                    "plane" : plane(vector(surfaceMiddle[0], surfaceMiddle[1], entityBounds.minCorner[2] + z), Z_DIRECTION),
                    "width" : surfaceSize[0],
                    "height" : surfaceSize[1]
                });

        z = z + splitDistance;
        planeIndex += 1;
    }

    /* Split faces one by one ******************************************************/
    var totalSubFaceTable = [];

    for (var entityIndex, entity in evaluateQuery(context, entities))
    {
        /* Create temporary copy of the surfaces ***************************************/
        var bodies = evaluateQuery(context, qEntityFilter(entity, EntityType.BODY));
        if (size(bodies) > 0)
        {
            offsetSurface(context, id + ("offsetSurface" ~ entityIndex), {
                        "surfacesAndFaces" : qOwnedByBody(entity, EntityType.FACE),
                        "offset" : 0.0 * meter
                    });
        }
        else
        {
            offsetSurface(context, id + ("offsetSurface" ~ entityIndex), {
                        "surfacesAndFaces" : entity,
                        "offset" : 0.0 * meter
                    });
        }
        var subFaces = qCreatedBy(id + ("offsetSurface" ~ entityIndex), EntityType.FACE);

        /* Split entity into subfaces ****************************************************/
        opSplitFace(context, id + ("splitFace" ~ entityIndex), {
                    "faceTargets" : subFaces,
                    "planeTools" : qCreatedBy(id + "plane", EntityType.FACE),
                    "keepToolSurfaces" : false
                });

        /* Link parent entity to the subfaces */
        setAttribute(context, {
                    "entities" : entity,
                    "name" : "subFaces",
                    "attribute" : subFaces
                });

        /* Find quige points to the face */
        for (var subFace in evaluateQuery(context, subFaces))
        {
            var tangentPlanes = evFaceTangentPlanes(context, {
                    "face" : subFace,
                    "parameters" : [
                        vector(0.50, 0.50),
                        vector(0.50, 0.05),
                        vector(0.50, 0.95),
                        vector(0.05, 0.50),
                        vector(0.95, 0.50),
                    ]
                });

            setAttribute(context, {
                        "entities" : subFace,
                        "name" : "tangentPlanes",
                        "attribute" : tangentPlanes
                    });
        }

        /* Add sub faces into query */
        totalSubFaceTable = append(totalSubFaceTable, subFaces);
    }

    /* Delete cutting planes *******************************************************/
    opDeleteBodies(context, id + "deleteBodies", {
                "entities" : qUnion([qCreatedBy(id + "plane")])
            });

    return qUnion(totalSubFaceTable);
}


