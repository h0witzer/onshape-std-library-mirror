FeatureScript 2780;
import(path : "onshape/std/geometry.fs", version : "2780.0");
icon::import(path : "ddcaad9d83a8c732426fcb5c", version : "15aeedeb3d5aa8eb66ba004f");

export enum RibBoundingType
{
    annotation { "Name" : "Blind" }
    BLIND,
    annotation { "Name" : "Up to next" }
    UP_TO_NEXT,
    annotation { "Name" : "Up to face" }
    UP_TO_FACE,
    annotation { "Name" : "Up to vertex" }
    UP_TO_VERTEX
}

annotation { "Feature Type Name" : "Rib Network", "Icon" : icon::BLOB_DATA, "Feature Type Description" : "This custom feature creates multiple ribs from sketch geometry" }
export const ribNetwork = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Network sketch", "Filter" : EntityType.EDGE && SketchObject.YES && ConstructionObject.NO }
        definition.edges is Query;

        annotation { "Name" : "Thickness", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.ribThickness, { (millimeter) : [0.1, 2, 100] } as LengthBoundSpec); // [min, default, max]

        annotation { "Name" : "End type" }
        definition.endBound is RibBoundingType;

        if (definition.endBound == RibBoundingType.BLIND)
        {
            annotation { "Name" : "Depth", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.depth, LENGTH_BOUNDS);
        }

        if (definition.endBound == RibBoundingType.UP_TO_FACE)
        {
            annotation { "Name" : "Face", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
            definition.face is Query;
        }

        if (definition.endBound == RibBoundingType.UP_TO_VERTEX)
        {
            annotation { "Name" : "Vertex", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
            definition.vertex is Query;
        }

        if (definition.endBound == RibBoundingType.BLIND || definition.endBound == RibBoundingType.UP_TO_NEXT)
        {
            annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
            definition.oppositeDirection is boolean;
        }

        annotation { "Name" : "Draft", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.draft is boolean;

        if (definition.draft)
        {
            annotation { "Group Name" : "Draft", "Collapsed By Default" : false, "Driving Parameter" : "draft" }
            {
                annotation { "Name" : "Angle", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isAngle(definition.draftAngle, ANGLE_STRICT_90_BOUNDS);

                annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.draftDirection is boolean;

                annotation { "Name" : "End faces", "Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                definition.draftEndFaces is boolean;
            }
        }

        annotation { "Name" : "Fillet", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.fillet is boolean;

        if (definition.fillet)
        {
            annotation { "Group Name" : "Fillets", "Collapsed By Default" : false, "Driving Parameter" : "fillet" }
            {
                annotation { "Name" : "Intersecting corners", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "- The corners where two or more sketch elements intersect" }
                definition.filletCorners is boolean;

                annotation { "Group Name" : "Fillets", "Collapsed By Default" : false, "Driving Parameter" : "filletCorners" }
                {
                    if (definition.filletCorners)
                    {
                        annotation { "Name" : "Radius", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                        isLength(definition.cornerFillet, { (millimeter) : [0, 1, 10] } as LengthBoundSpec);

                        annotation { "Name" : "Variable", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                        definition.variableCorners is boolean;

                        annotation { "Group Name" : "Fillets", "Collapsed By Default" : false, "Driving Parameter" : "variableCorners" }
                        {
                            if (definition.variableCorners)
                            {
                                annotation { "Name" : "Base radius", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                                isLength(definition.variableCornerFillet, { (millimeter) : [0, 1, 10] } as LengthBoundSpec);
                            }
                        }

                        annotation { "Name" : "Sharp corners", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "- The corners where two sketch elements join at a single vertex<br>- Uses the same radius or variable radius as intersecting corners" }
                        definition.filletSharpCorners is boolean;
                    }
                }

                annotation { "Name" : "End faces", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "- Uses the same radius as intersecting corners" }
                definition.filletEndFaces is boolean;

                annotation { "Group Name" : "Fillets", "Collapsed By Default" : false, "Driving Parameter" : "filletEndFaces" }
                {
                    if (definition.filletEndFaces)
                    {
                        annotation { "Name" : "Full round", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                        definition.filletEndFacesFull is boolean;
                    }
                }

                annotation { "Name" : "Top faces", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                definition.ribTop is boolean;

                annotation { "Group Name" : "Top face", "Collapsed By Default" : false, "Driving Parameter" : "ribTop" }
                {
                    if (definition.ribTop)
                    {
                        annotation { "Name" : "Radius", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                        isLength(definition.topFillet, { (millimeter) : [0, 1, 10] } as LengthBoundSpec);
                    }
                }

                annotation { "Group Name" : "Part", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Walls", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "- The corners where the vertical edges of the rib touch the part<br>- A part must be selected" }
                    definition.filletPartWall is boolean;

                    annotation { "Group Name" : "Walls", "Collapsed By Default" : false, "Driving Parameter" : "filletPartWall" }
                    {
                        if (definition.filletPartWall)
                        {
                            annotation { "Name" : "Radius", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                            isLength(definition.wallFillet, { (millimeter) : [0, 1, 10] } as LengthBoundSpec);
                        }
                    }

                    annotation { "Name" : "Base", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "- The corners where the base edges of the rib touch the part<br>- A part must be selected" }
                    definition.filletPartBase is boolean;

                    annotation { "Group Name" : "Fillets", "Collapsed By Default" : false, "Driving Parameter" : "filletPartBase" }
                    {
                        if (definition.filletPartBase)
                        {
                            annotation { "Name" : "Radius", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                            isLength(definition.baseFillet, { (millimeter) : [0, 1, 10] } as LengthBoundSpec);
                        }
                    }

                    annotation { "Name" : "All round", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "- Tangent propagation for top face and base fillets<br>- A part must be selected" }
                    definition.filletAllRound is boolean;
                }
            }
        }

        annotation { "Name" : "Merge scope", "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES && AllowMeshGeometry.YES }
        definition.part is Query;
    }
    {
        verifyNonemptyQuery(context, definition, "edges", "Select a sketch from the feature list or individual sketch edges.");

        const ribPlane = evOwnerSketchPlane(context, {
                    "entity" : definition.edges
                });

        if (evaluateQueryCount(context, definition.edges) != evaluateQueryCount(context, qCoincidesWithPlane(definition.edges, ribPlane)))
            throw regenError("Sketch entities must lie in the same plane.", ["edges"]);

        const mergePart = !isQueryEmpty(context, definition.part);
        var topFaceCapType = CapType.START;
        var baseFaceCapType = CapType.END;
        var ribDepth = definition.depth;
        var ribThickness = definition.ribThickness;
        var ribDirection = -ribPlane.normal;
        var draftDirection = definition.draftDirection ? ribDirection : -ribDirection;

        if (definition.endBound == RibBoundingType.UP_TO_NEXT && definition.oppositeDirection)
            ribDirection = ribPlane.normal;

        if (definition.endBound == RibBoundingType.BLIND)
        {
            if (definition.oppositeDirection)
                ribDirection *= -1;

            if (definition.draft && definition.draftAngle > 0 * degree)
            {
                if (draftDirection == ribDirection)
                {
                    ribThickness = ribThickness + 2 * ribDepth * tan(definition.draftAngle);
                    topFaceCapType = CapType.END;
                    baseFaceCapType = CapType.START;
                }
            }
        }

        if (isIn(definition.endBound, [RibBoundingType.UP_TO_FACE, RibBoundingType.UP_TO_VERTEX]))
        {
            const boundType = definition.endBound == RibBoundingType.UP_TO_FACE ? "face" : "vertex";

            verifyNonemptyQuery(context, definition, boundType, "Select a " ~ boundType ~ " to extrude up to.");

            if (boundType == "face" && isQueryEmpty(context, qParallelPlanes(definition.face, ribPlane, true)))
                throw regenError("Face must be parallel to the rib sketch plane", ["face"]);

            ribDepth = evDistance(context, {
                            "side0" : ribPlane,
                            "side1" : definition[boundType]
                        }).distance;

            if (!isQueryEmpty(context, qInFrontOfPlane(definition[boundType], ribPlane)))
                ribDirection = ribPlane.normal;
        }

        if (mergePart)
        {
            // Test if sketch is on gace of part
            const midPoint = evEdgeTangentLine(context, {
                            "edge" : qNthElement(definition.edges, 0),
                            "parameter" : 0.5
                        }).origin;

            if (!isQueryEmpty(context, qContainsPoint(qOwnedByBody(definition.part, EntityType.FACE), midPoint)))
            {
                topFaceCapType = CapType.END;
                baseFaceCapType = CapType.START;

                if (definition.draft && definition.draftAngle > 0 * degree)
                    if (draftDirection == ribDirection && ribThickness == definition.ribThickness)
                    ribThickness = ribThickness + 2 * ribDepth * tan(definition.draftAngle);
            }
        }

        if (tolerantEqualsZero(ribDepth))
            throw regenError("Selection causes rib depth to be zero.");

        var ribTopFace;
        var ribBaseFace;
        var ribTopEdges;
        var trackEndEdges = [];
        const extrudeRibs = id + "extrudeRibs";

        for (var i, rib in evaluateQuery(context, definition.edges))
        {
            sweepFace(context, extrudeRibs + i + "surface", rib, ribPlane, ribThickness);

            trackEndEdges = append(trackEndEdges, startTracking(context, qCapEntity(extrudeRibs + i + "surface", CapType.EITHER, EntityType.EDGE)));

            try silent
            {
                opExtrude(context, extrudeRibs + i, {
                            "entities" : qCreatedBy(extrudeRibs + i + "surface", EntityType.FACE),
                            "direction" : ribDirection,
                            "endBound" : definition.endBound == RibBoundingType.UP_TO_NEXT ? BoundingType.UP_TO_NEXT : BoundingType.BLIND,
                            "endDepth" : ribDepth,
                        });
            }
            catch
            {
                addDebugEntities(context, rib, DebugColor.RED);
                reportFeatureWarning(context, id, "One or more ribs failed. Check sketch or change end bound condition.");
            }
        }

        opDeleteBodies(context, id + "deleteSurfaces", {
                    "entities" : qCreatedBy(extrudeRibs + ANY_ID + "surface", EntityType.BODY)
                });

        const ribBodies = qCreatedBy(extrudeRibs, EntityType.BODY);
        var ribBodyCount = evaluateQueryCount(context, ribBodies);

        if (ribBodyCount > 1) // boolean all rib elements together first
        {
            opBoolean(context, id + "booleanRibs", {
                        "tools" : ribBodies,
                        "operationType" : BooleanOperationType.UNION
                    });

            ribBodyCount = evaluateQueryCount(context, ribBodies);
        }

        if (mergePart)
        {
            // Start with subtracting the part from the ribs to remove any ribs outside the boundary of the part
            opBoolean(context, id + "booleanSubtract", {
                        "targets" : ribBodies,
                        "tools" : definition.part,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "keepTools" : true
                    });

            if (evaluateQueryCount(context, ribBodies) != ribBodyCount) // rib cut by part
            {
                var centerPointBodies = [];

                // Using the center point of each sketch element to determine which bodies are "inside" the target part
                // Edges that are too far outside of the target part will fail and it is the user's fault for bad sketching
                for (var edge in evaluateQuery(context, definition.edges))
                    centerPointBodies = append(centerPointBodies, qContainsPoint(ribBodies, evEdgeTangentLine(context, {
                                            "edge" : edge,
                                            "parameter" : 0.5
                                        }).origin));

                opDeleteBodies(context, id + "deleteExcess", {
                            "entities" : ribBodies->qSubtraction(qUnion(centerPointBodies))
                        });
            }
        }

        ribTopFace = qCapEntity(extrudeRibs, topFaceCapType, EntityType.FACE);
        ribBaseFace = qCapEntity(extrudeRibs, baseFaceCapType, EntityType.FACE);
        ribTopEdges = makeRobustQuery(context, qAdjacent(ribTopFace, AdjacencyType.EDGE, EntityType.EDGE));

        if (mergePart)
        {
            // Add trimmed ribs to part
            if (!isQueryEmpty(context, ribBodies))
                opBoolean(context, id + "booleanPart", {
                            "tools" : qUnion(definition.part, ribBodies),
                            "operationType" : BooleanOperationType.UNION
                        });
        }

        // Finally, evaluate end faces after extrudes and booleans
        const ribEndFaces = makeRobustQuery(context, qUnion(mapArray(trackEndEdges, function(edge)
                    {
                        return qUnion(evaluateQuery(context, edge))->qEntityFilter(EntityType.FACE);
                    })));

        const ribEndFaceEdges = qAdjacent(ribEndFaces, AdjacencyType.EDGE, EntityType.EDGE);
        const ribEndTopEdges = makeRobustQuery(context, ribEndFaceEdges->qIntersection(ribTopEdges));
        var cornersToDelete = [];

        // Check for all top edges that are less than rib thickness to find open corners
        for (var ribEndTopEdge in evaluateQuery(context, ribEndTopEdges))
        {
            const edgeLength = evLength(context, {
                        "entities" : ribEndTopEdge
                    });

            if (edgeLength < ribThickness - TOLERANCE.zeroLength * meter)
                cornersToDelete = append(cornersToDelete, qIntersection(ribEndFaces, qAdjacent(ribEndTopEdge, AdjacencyType.EDGE, EntityType.FACE)));
        }


        try silent // and remove them
        {
            opDeleteFace(context, id + "deleteRibCorners", {
                        "deleteFaces" : qUnion(cornersToDelete),
                        "includeFillet" : false,
                        "capVoid" : false,
                        "leaveOpen" : false
                    });
        }

        const ribSideFaces = qNonCapEntity(extrudeRibs, EntityType.FACE)->qSubtraction(ribEndFaces);
        const ribIntersectionEdges = getIntersectionEdges(context, ribSideFaces, qUnion(ribEndFaceEdges, ribTopEdges));
        const ribTopIntersectionEdges = qCreatedBy(id + "booleanPart", EntityType.EDGE)->qIntersection(qAdjacent(ribTopFace, AdjacencyType.EDGE, EntityType.EDGE));

        const ribConvexCornerEdges = qCreatedBy(id + "deleteRibCorners", EntityType.EDGE)->qSubtraction(ribIntersectionEdges);
        const ribConcaveCornerEdges = getConcaveCornerEdges(context, id, ribConvexCornerEdges, ribTopEdges);
        const ribCornerEdges = qCreatedBy(id + "booleanRibs", EntityType.EDGE)->qSubtraction(ribConcaveCornerEdges);

        const ribEndSideEdges = qAdjacent(ribEndTopEdges, AdjacencyType.VERTEX, EntityType.EDGE)->qIntersection(ribEndFaceEdges);
        const ribEndBaseEdges = makeRobustQuery(context, ribEndFaceEdges->qSubtraction(qUnion(ribTopEdges, ribEndSideEdges)));

        const partWallEdges = makeRobustQuery(context, qAdjacent(ribTopEdges, AdjacencyType.VERTEX, EntityType.EDGE)->qIntersection(ribIntersectionEdges));
        const ribBaseEdges = makeRobustQuery(context, ribIntersectionEdges->qSubtraction(qUnion(partWallEdges, ribEndBaseEdges)));
        const partBaseEdges = qAdjacent(ribBaseEdges, AdjacencyType.EDGE, EntityType.FACE)->qSubtraction(ribSideFaces)->qAdjacent(AdjacencyType.EDGE, EntityType.EDGE)->qSubtraction(ribEndBaseEdges);

        if (definition.draft)
        {
            opDraft(context, id + "draftRibs", {
                        "draftType" : DraftType.REFERENCE_SURFACE,
                        "draftFaces" : definition.draftEndFaces ? qUnion(ribSideFaces, ribEndFaces) : ribSideFaces,
                        "referenceSurface" : ribPlane,
                        "pullVec" : draftDirection,
                        "angle" : definition.draftAngle
                    });
        }

        if (definition.fillet)
        {
            if (definition.filletCorners)
            {
                const concaveEdges = ribCornerEdges->qUnion(definition.filletSharpCorners ? ribConcaveCornerEdges : qNothing());

                if (!isQueryEmpty(context, concaveEdges))
                {
                    filletCorners(context, id + "filletCornerEdges", definition, concaveEdges, ribTopEdges, definition.cornerFillet, definition.variableCornerFillet);

                    if (!isQueryEmpty(context, ribConvexCornerEdges))
                    {
                        if (definition.filletSharpCorners)
                            filletCorners(context, id + "filletSharpEdges", definition, ribConvexCornerEdges, ribTopEdges, definition.cornerFillet + definition.ribThickness, definition.variableCornerFillet + definition.ribThickness);
                    }
                }
            }

            if (definition.filletEndFaces)
            {
                if (definition.filletEndFacesFull)
                {
                    for (var i, ribEndFace in evaluateQuery(context, ribEndFaces))
                    {
                        const adjacentFaces = qAdjacent(ribEndFace, AdjacencyType.EDGE, EntityType.FACE)->qIntersection(ribSideFaces);

                        opFullRoundFillet(context, id + "fullRoundFillet" + i, {
                                    "side1Face" : qNthElement(adjacentFaces, 0),
                                    "side2Face" : qNthElement(adjacentFaces, 1),
                                    "centerFaces" : ribEndFace
                                });
                    }
                }
                else
                {
                    var maxEndTopFilletSize = definition.cornerFillet;

                    if (maxEndTopFilletSize > ribThickness / 2)
                        maxEndTopFilletSize = ribThickness / 2;

                    var maxEndBaseFilletSize = maxEndTopFilletSize;

                    if (definition.variableCorners)
                    {
                        maxEndBaseFilletSize = definition.variableCornerFillet;

                        for (var endBaseEdge in evaluateQuery(context, ribEndBaseEdges))
                        {
                            const halfLength = evLength(context, {
                                            "entities" : endBaseEdge
                                        }) / 2;

                            if (halfLength < maxEndBaseFilletSize)
                                maxEndBaseFilletSize = halfLength;
                        }
                    }

                    filletCorners(context, id + "filletEndFaces", definition, ribEndSideEdges, ribTopEdges, maxEndTopFilletSize, maxEndBaseFilletSize);
                }
            }

            if (definition.filletAllRound)
            {
                filletWalls(context, id, definition, partWallEdges, qNothing());
                filletBase(context, id, definition, ribBaseEdges, partBaseEdges);
                var filletTopEdges = ribTopEdges->qSubtraction(ribEndTopEdges);

                if (definition.filletPartWall)
                    filletTopEdges = qUnion(filletTopEdges, ribTopIntersectionEdges);

                filletTop(context, id, definition, filletTopEdges);
            }
            else
            {
                const filletTopEdges = qTangentConnectedEdges(partWallEdges)->qIntersection(filletTop(context, id, definition, qSubtraction(ribTopEdges, ribEndTopEdges)));
                filletBase(context, id, definition, ribBaseEdges, partBaseEdges);
                filletWalls(context, id, definition, partWallEdges, filletTopEdges);
            }
        }
    });

function filletCorners(context is Context, id is Id, definition is map, edges is Query, ribTopEdges is Query, topFilletSize is ValueWithUnits, baseFilletSize is ValueWithUnits)
{
    var vertexArray = [];

    if (definition.variableCorners)
    {
        const ribVertices = qAdjacent(edges, AdjacencyType.VERTEX, EntityType.VERTEX);
        const ribTopVertices = qAdjacent(ribTopEdges, AdjacencyType.VERTEX, EntityType.VERTEX)->qIntersection(ribVertices);
        const ribBaseVertices = qSubtraction(ribVertices, ribTopVertices);

        for (var topVertex in evaluateQuery(context, ribTopVertices))
            vertexArray = append(vertexArray,
                { "vertex" : topVertex,
                        "vertexRadius" : topFilletSize
                    });

        for (var baseVertex in evaluateQuery(context, ribBaseVertices))
            vertexArray = append(vertexArray,
                { "vertex" : baseVertex,
                        "vertexRadius" : baseFilletSize
                    });
    }

    try
    {
        opFillet(context, id, {
                    "entities" : edges,
                    "radius" : topFilletSize,
                    "isVariable" : definition.variableCorners,
                    "smoothTransition" : false,
                    "vertexSettings" : vertexArray
                });
    }

}

function filletTop(context is Context, id is Id, definition is map, ribTopEdges is Query)
{
    if (definition.ribTop)
    {
        try
        {
            opFillet(context, id + "filletTopFaces", {
                        "entities" : ribTopEdges,
                        "radius" : definition.topFillet,
                        "tangentPropagation" : true
                    });
        }
    }

    return qCreatedBy(id + "filletTopFaces", EntityType.EDGE);
}

function filletWalls(context is Context, id is Id, definition is map, partWallEdges is Query, filletTopEdges is Query)
{
    if (definition.filletPartWall)
    {
        if (isQueryEmpty(context, definition.part))
        {
            reportFeatureWarning(context, id, "Wall fillet requires a part to merge to.");
            return;
        }

        try
        {
            opFillet(context, id + "filletPartWalls", {
                        "entities" : qUnion(partWallEdges, filletTopEdges),
                        "radius" : definition.wallFillet
                    });
        }
    }
}

function filletBase(context is Context, id is Id, definition is map, ribBaseEdges is Query, partBaseEdges is Query)
{
    if (definition.filletPartBase)
    {
        if (isQueryEmpty(context, definition.part))
        {
            reportFeatureWarning(context, id, "Base fillet requires a part to merge to.");
            return;
        }

        if (definition.filletAllRound)
            ribBaseEdges = qUnion(ribBaseEdges, partBaseEdges);

        try
        {
            opFillet(context, id + "filletPartBase", {
                        "entities" : ribBaseEdges,
                        "radius" : definition.baseFillet,
                        "tangentPropagation" : true
                    });
        }
    }
}

function sweepFace(context is Context, id is Id, edge is Query, ribPlane is Plane, ribThickness is ValueWithUnits)
{
    const edgeVector = evEdgeTangentLine(context, {
                "edge" : edge,
                "parameter" : 0
            });

    const sketchPlane = plane(edgeVector.origin, ribPlane.normal, edgeVector.direction);

    const ribProfile = newSketchOnPlane(context, id + "sketch", {
                "sketchPlane" : sketchPlane
            });

    skLineSegment(ribProfile, "line", {
                "start" : vector(0 * meter, ribThickness / 2),
                "end" : vector(0 * meter, -ribThickness / 2)
            });

    skSolve(ribProfile);

    opSweep(context, id + "sweep", {
                "profiles" : qCreatedBy(id + "sketch", EntityType.EDGE),
                "path" : edge
            });
}

function getIntersectionEdges(context is Context, ribSideFaces is Query, excludeEdges is Query)
{
    var removeEdges = [];
    const edges = qAdjacent(ribSideFaces, AdjacencyType.EDGE, EntityType.EDGE)->qSubtraction(excludeEdges);

    for (var edge in evaluateQuery(context, edges))
        if (evaluateQueryCount(context, qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE)->qIntersection(ribSideFaces)) > 1)
            removeEdges = append(removeEdges, edge);

    return edges->qSubtraction(qUnion(removeEdges));
}

function getConcaveCornerEdges(context is Context, id is Id, ribConvexCornerEdges is Query, ribTopEdges is Query)
{
    var concaveCornerEdges = [];

    for (var convexCornerEdge in evaluateQuery(context, ribConvexCornerEdges))
    {
        const convexEdgeVertices = qAdjacent(convexCornerEdge, AdjacencyType.VERTEX, EntityType.VERTEX);
        const convexEdgeTopVertex = evVertexPoint(context, {
                    "vertex" : qAdjacent(ribTopEdges, AdjacencyType.VERTEX, EntityType.VERTEX)->qIntersection(convexEdgeVertices)
                });

        concaveCornerEdges = append(concaveCornerEdges, qClosestTo(qCreatedBy(id + "booleanRibs", EntityType.EDGE), convexEdgeTopVertex));
    }

    return qUnion(concaveCornerEdges);
}
