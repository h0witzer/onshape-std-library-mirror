FeatureScript 2399;
import(path : "onshape/std/geometry.fs", version : "2399.0");

icon::import(path : "9bd53007580f92cdab82577a", version : "4bb5f0b4912ea4406ae4bae7");

annotation { "Feature Type Name" : "3D Printed Hole",
        "Icon" : icon::BLOB_DATA,
    }
export const printedHole = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {

        annotation { "Name" : "Hole faces to convert", "Filter" : EntityType.FACE && GeometryType.CYLINDER }
        definition.holeFaces is Query;

        annotation { "Name" : "Select all holes" }
        definition.selectAll is boolean;

        if (!definition.selectAll)
        {
            annotation { "Name" : "Select patterns", "Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.selectPatterns is boolean;
        }

        annotation { "Name" : "Printer z direction", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION || BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
        definition.printerZ is Query;

        annotation { "Name" : "Flip printer z direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.flipPrinterZ is boolean;

        annotation { "Name" : "Overhang angle", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isAngle(definition.overhangAngle, { (degree) : [.00001, 45, 89.00009] } as AngleBoundSpec);

        annotation { "Name" : "Truncate", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.truncate is boolean;

        if (definition.truncate)
        {
            annotation { "Name" : "Bridge layer clearance", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.bridgeLayerClearance, { (millimeter) : [-1e6, 0, 1e6] } as LengthBoundSpec);
        }

        annotation { "Group Name" : "Display options", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Non-hole selections (red)", "Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.displayNonHole is boolean;

            annotation { "Name" : "Unchanged holes (green)" }
            definition.displayNonModified is boolean;

            annotation { "Name" : "Printer z direction (blue)", "Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.displayArrow is boolean;
        }


    }

    // Execute feature
    {
        var printerZ = getPrinterZDirection(context, definition);
        var angle = definition.overhangAngle;

        var holeFaces = definition.holeFaces;

        var additionalHoles = []; // a place to put either all holes or matching holes

        // if select all holes
        if (definition.selectAll)
        {
            var ownerBodies = evaluateQuery(context, qOwnerBody(definition.holeFaces));
            // println(ownerBodies);

            for (var body in ownerBodies)
            {
                var allCylinderFaces = evaluateQuery(context, qOwnedByBody(body, EntityType.FACE)->qGeometry(GeometryType.CYLINDER));
                for (var face in allCylinderFaces)
                {
                    additionalHoles = append(additionalHoles, face);
                }
            }
        }

        // if selecting matching hole faces
        if (definition.selectPatterns && !definition.selectAll)
        {
            for (var face in evaluateQuery(context, holeFaces))
            {
                var matchingFace = qMatching(face);

                if (!isQueryEmpty(context, matchingFace))
                    additionalHoles = append(additionalHoles, matchingFace);
            }
        }

        additionalHoles = qUnion(additionalHoles);

        holeFaces = qUnion([holeFaces, additionalHoles]);

        // boxes to notify user of events later
        var nonHoleSelection = new box("");
        var tooSteep = new box("");

        // a box to store all bodies for a single opBoolean later
        var dropletBodies = new box([]);
        var targets = new box([]);

        // Jelte: edited. for optional truncation after boolean.
        var truncateError = new box([]);
        const chamferSavingOffset = 0.001 * millimeter;

        // to get rid of sliver faces later (these occur when the tips of the droplet don't quite extend far enough even though they are as long as the hole, for example if the hole is on a slant.
        var endFaces = new box([]);

        forEachEntity(context, id + "printedHole", holeFaces, function(face is Query, id is Id)
            {
                // testing for negative curvature. If it's positive, it's not a hole.
                var isHole = evFaceCurvature(context, { "face" : face, "parameter" : vector(0.5, 0.5) }).minCurvature < 0;

                // if it's not a hole highlight red and tell the user
                if (!isHole)
                {
                    if (definition.displayNonHole)
                    {
                        addDebugEntities(context, face);
                        nonHoleSelection[] = "true";
                    }
                }

                else
                {
                    const centerAxis = evAxis(context, { "axis" : face });
                    const axis = centerAxis.direction;


                    var axisAngle = angleBetween(printerZ, axis);
                    axisAngle = axisAngle > 90 * degree ? 180 * degree - axisAngle : axisAngle;

                    // checks whether the hole is steep enough to print without being changed
                    if (!(axisAngle > angle))
                    {
                        if (definition.displayNonModified)
                        {
                            addDebugEntities(context, face, DebugColor.GREEN);
                            tooSteep[] = "true";
                        }
                    }

                    // if it needs to be modified
                    else
                    {

                        // create a sketch plane at one end of the hole
                        const origin = centerAxis.origin; /*origin location is arbitrary along the axis*/
                        var sketchPlane = plane(origin, axis); /*plane with arbitrary origin*/

                        //finding the bounding box of the hole in order to size it
                        var boundingBox = evBox3d(context, {
                                "topology" : face,
                                "tight" : true,
                                "cSys" : coordSystem(sketchPlane)
                            });

                        // these are using the box coordinates in world space, so the vector's xyz relate to the world coordinates not the coordinates of the hole
                        var distFromHoleEnd = WORLD_ORIGIN[2] - boundingBox.minCorner[2];
                        var holeLength = boundingBox.maxCorner[2] - boundingBox.minCorner[2];
                        var endPlaneOffset = 0.001 * millimeter; /*the ammount to offset the droplet from either side of the hole, which leaves something to delete later. The goal is to eliminate sliver faces without deleting the bottoms of counterbores etc, and this is one way to garuntee only deleting what we want.*/

                        sketchPlane.origin = sketchPlane.origin - (axis * (distFromHoleEnd + endPlaneOffset)); /*moving the origin of the plane to one side of the hole, and adding a bit to give */


                        var sketch = newSketchOnPlane(context, id + "sketch", {
                                "sketchPlane" : sketchPlane
                            });

                        // get radius of selected hole face
                        var radius = evSurfaceDefinition(context, {
                                    "face" : face
                                }).radius;

                        // sketch a circle with same radius
                        skCircle(sketch, "circle1", {
                                    "center" : vector(0, 0) * inch,
                                    "radius" : radius
                                });

                        skSolve(sketch);

                        var sketchFace = qSketchRegion(id + "sketch");
                        opExtrude(context, id + "circleExtrude", {
                                    "entities" : sketchFace,
                                    "direction" : axis,
                                    "endBound" : BoundingType.BLIND,
                                    "endDepth" : holeLength - (endPlaneOffset * 2) /*again, subtracting a little to give something to delete later. *2 since the other end is already offset*/
                                });

                        // cleanup sketch entities
                        opDeleteBodies(context, id + "deleteBodies1", {
                                    "entities" : qCreatedBy(id + "sketch", EntityType.BODY)
                                });

                        var sideFace = qNonCapEntity(id + "circleExtrude", EntityType.FACE);

                        var dropletApexPlane = getDropletApexPlane(context, id, printerZ, face);

                        opSplitByIsocline(context, id + "splitByIsocline", {
                                    "faces" : sideFace,
                                    "direction" : printerZ,
                                    "angle" : angle
                                });

                        const splitEdges = evaluateQuery(context, qCreatedBy(id + "splitByIsocline", EntityType.EDGE));
                        const splitFaces = evaluateQuery(context, qAdjacent(splitEdges[0], AdjacencyType.EDGE, EntityType.FACE));
                        const upFace = qFarthestAlong(qUnion(splitFaces), printerZ);
                        const downFace = qFarthestAlong(qUnion(splitFaces), -printerZ);
                        const extrude1Dir = -yAxis(evFaceTangentPlaneAtEdge(context, { "edge" : splitEdges[0], "face" : upFace, "parameter" : 0.5 }));
                        const extrude2Dir = -yAxis(evFaceTangentPlaneAtEdge(context, { "edge" : splitEdges[1], "face" : upFace, "parameter" : 0.5 }));

                        opExtrude(context, id + "extrude1", {
                                    "entities" : splitEdges[0],
                                    "direction" : extrude1Dir,
                                    "endBound" : BoundingType.UP_TO_SURFACE,
                                    "endDepth" : 1 * inch,
                                    "endBoundEntity" : dropletApexPlane
                                });

                        opExtrude(context, id + "extrude2", {
                                    "entities" : splitEdges[1],
                                    "direction" : extrude2Dir,
                                    "endBound" : BoundingType.UP_TO_SURFACE,
                                    "endDepth" : 1 * inch,
                                    "endBoundEntity" : dropletApexPlane
                                });

                        // cleanup endBound plane
                        opDeleteBodies(context, id + "deleteEndBoundPlane", {
                                    "entities" : dropletApexPlane
                                });

                        var dropletFaces = qUnion([qCreatedBy(id + "extrude1", EntityType.BODY), qCreatedBy(id + "extrude2", EntityType.BODY), qCreatedBy(id + "circleExtrude", EntityType.BODY)]);

                        const deleteFaces = qUnion(qCapEntity(id + "circleExtrude", CapType.EITHER, EntityType.FACE), downFace);

                        // delete all parts of circle extrude except down face
                        opDeleteFace(context, id + "deleteFace1", {
                                    "deleteFaces" : deleteFaces,
                                    "includeFillet" : false,
                                    "capVoid" : false,
                                    "leaveOpen" : true
                                });

                        // getting edges of the created faces that are coincident with the sketchPlane (format is an array like [startDropletEdges, endDropletEdges] so we can use a loop for the fills.
                        const dropletShapeEdges = [qUnion([qCreatedBy(id + "circleExtrude", EntityType.EDGE), qCreatedBy(id + "extrude1", EntityType.EDGE), qCreatedBy(id + "extrude2", EntityType.EDGE)])->qCoincidesWithPlane(sketchPlane), qUnion([qCreatedBy(id + "circleExtrude", EntityType.EDGE), qCreatedBy(id + "extrude1", EntityType.EDGE), qCreatedBy(id + "extrude2", EntityType.EDGE)])->qCoincidesWithPlane(plane(sketchPlane.origin + (sketchPlane.normal * (holeLength - endPlaneOffset * 2)), sketchPlane.normal))];

                        // capping the ends of the droplet body
                        for (var i = 0; i < 2; i += 1)
                        {
                            opFillSurface(context, id + i + "dropletShapeFace", {
                                        "edgesG0" : dropletShapeEdges[i],
                                        "edgesG1" : qNothing(),
                                        "edgesG2" : qNothing(),
                                        "guideVertices" : qNothing()
                                    });
                        }

                        var endFillSurfaceFaces = qCreatedBy(id + ANY_ID + "dropletShapeFace", EntityType.BODY);

                        // merging and making solid all of the droplet body faces
                        const dropletCutBodyFaces = qUnion([endFillSurfaceFaces, downFace, dropletFaces]);

                        opBoolean(context, id + "dropletCutBody", {
                                    "tools" : dropletCutBodyFaces,
                                    "operationType" : BooleanOperationType.UNION,
                                    "makeSolid" : true
                                });

                        var dropletBody = qOwnerBody(qCreatedBy(id + "extrude1", EntityType.FACE));


                        if (definition.truncate) //Jelte: chamfer the created body in case of truncate
                        {

                            var clearance = definition.bridgeLayerClearance <= 0 * millimeter ? chamferSavingOffset : definition.bridgeLayerClearance;
                            //Jelte: Chamfer only works for positive non-zero clearances. Later, a move face operation removes this offset.

                            var apexEdge = qOwnedByBody(dropletBody, EntityType.EDGE)->qParallelEdges(axis)->qFarthestAlong(printerZ);


                            var height = radius / (sin(angle)); // Jelte: little bit of trigonometry to determine the chamfer width
                            var truncation = height - (radius + clearance);
                            var chamferWidth = truncation / (cos(angle));

                            //Jelte: chamfer droplet tip
                            try
                            {
                                opChamfer(context, id + "truncationChamfer", {
                                            "entities" : apexEdge,
                                            "chamferType" : ChamferType.EQUAL_OFFSETS,
                                            "width" : chamferWidth
                                        });
                            }
                            catch
                            {
                                truncateError[] = true;
                            }

                            if (definition.bridgeLayerClearance <= 0 * millimeter) // Jelte: removes the offset added to allow the chamfer to work
                            {
                                var moveDistance = chamferSavingOffset - definition.bridgeLayerClearance;
                        
                                    var chamferFace = qCreatedBy(id + "truncationChamfer", EntityType.FACE);

                                    var moveDirection = evFaceTangentPlane(context, {
                                            "face" : chamferFace,
                                            "parameter" : vector(0.5, 0.5)
                                        });

                                    opMoveFace(context, id + "moveTruncatedFace", {
                                                "moveFaces" : chamferFace,
                                                "transform" : transform(moveDirection.normal * moveDistance)
                                            });
                            }

                        }

                        // adding the droplet body and targets to their boxes for later opBoolean
                        dropletBodies[] = append(dropletBodies[], dropletBody);
                        targets[] = append(targets[], qOwnerBody(face));

                        var fillFaces = startTracking(context, qCreatedBy(id + ANY_ID + "dropletShapeFace", EntityType.FACE));
                        endFaces[] = append(endFaces[], fillFaces);
                    }
                }
            });
        dropletBodies = qUnion(dropletBodies[]);

        targets = qUnion(targets[]);

        opBoolean(context, id + "boolean1", {
                    "tools" : dropletBodies,
                    "targets" : targets,
                    "keepTools" : false,
                    "operationType" : BooleanOperationType.SUBTRACTION
                });

        var remainingSliverFaces = qUnion(endFaces[]);


        if (!(evaluateQuery(context, remainingSliverFaces) == []))
        {
            forEachEntity(context, id + "deleteSliverFaces", remainingSliverFaces, function(face is Query, id is Id)
                {
                    try /*start with opDelete since it's lighter weight*/
                    {
                        opDeleteFace(context, id + "sliverRemovalDeleteFace", {
                                    "deleteFaces" : face,
                                    "includeFillet" : false,
                                    "capVoid" : false,
                                    "leaveOpen" : false
                                });
                    }
                    catch /*if opDelete fails, we'll extrude the face up to the face's owner body*/
                    {
                        var extrudeDirection = -(evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) }).normal);

                        opExtrude(context, id + "sliverRemovalExtrude", {
                                    "entities" : face,
                                    "direction" : extrudeDirection,
                                    "endBound" : BoundingType.UP_TO_BODY,
                                    "endBoundEntity" : qOwnerBody(face),
                                });

                        opBoolean(context, id + "sliverRemovalBoolean", {
                                    "tools" : qCreatedBy(id + "sliverRemovalExtrude", EntityType.BODY),
                                    "targets" : qOwnerBody(face),
                                    "operationType" : BooleanOperationType.SUBTRACTION
                                });
                    }
                });

            if (definition.truncate && truncateError[] == true) //Jelte: the main creation of the truncation is now in the dropletbody definition part of the code before the boolean to make the chamfering less dependent on final geometry. Here it's just error handling.
                {
                    reportFeatureInfo(context, id, ErrorStringEnum.NO_ERROR, ["bridgeLayerClearance"]);
                    reportFeatureInfo(context, id, "Truncation failed. Clearance may be too big for the geometry");
                }
        }



        var reportString = "";
        if (nonHoleSelection[] == "true" && definition.displayNonHole)
        {
            reportString ~= "Red faces are not holes and are ignored. ";
        }
        if (tooSteep[] == "true" && definition.displayNonModified)
        {
            reportString ~= "Green faces are steeper than the overhang angle, and can print without modification.";
        }

        if (reportString != "")
        {
            reportFeatureInfo(context, id, reportString);
        }


    });

// 👇 support functions 👇

export function getPrinterZDirection(context is Context, definition is map)
{
    var printerZ;

    if (isQueryEmpty(context, definition.printerZ))
    {
        printerZ = WORLD_COORD_SYSTEM.zAxis;
    }
    else
    {
        if (isQueryEmpty(context, definition.printerZ->qBodyType(BodyType.MATE_CONNECTOR)))
        {
            printerZ = extractDirection(context, definition.printerZ);
        }
        else
        {
            printerZ = evMateConnector(context, {
                            "mateConnector" : definition.printerZ
                        }).zAxis;
        }
    }
    if (definition.flipPrinterZ)
    {
        printerZ = -printerZ;
    }


    // size the display arrow appropriately relative to part size
    if (definition.displayArrow)
    {
        var evaluatedArrowLength = 0 * millimeter as ValueWithUnits;

        try //Jelte: added a Try here to make the feature solve even if the evDistance doesn't.
        {

            const evaluateDistance = evDistance(context, { //Jelte: put this in a new constant enable the try
                        "side0" : qEverything(EntityType.BODY)->qBodyType(BodyType.SOLID)->qFarthestAlong(printerZ),
                        "side1" : vector(0, 0, 0) * meter,
                        "maximum" : true
                    });
            evaluatedArrowLength = evaluateDistance.distance;
        }

        var arrowLength = max(25 * millimeter, evaluatedArrowLength); //Jelte: put this in a separate line to enable the try

        addDebugArrow(context, zeroVector(3) * millimeter, printerZ * arrowLength, arrowLength * .1, DebugColor.BLUE);
    }

    return printerZ;
}

export function getDropletApexPlane(context is Context, id is Id, printerZ is Vector, face is Query) returns Query
{
    const axis = evAxis(context, { "axis" : face });
    const centerPlane = plane(axis.origin, cross(axis.direction, printerZ));
    opPlane(context, id + "extrudeStop", { "plane" : centerPlane });
    const dropletApexPlane = qCreatedBy(id + "extrudeStop", EntityType.FACE);

    return dropletApexPlane;
}

export function createDropletFaces(context is Context, id is Id, face is Query, printerZ is Vector, angle is ValueWithUnits) returns Query
{
    var dropletApexPlane = getDropletApexPlane(context, id, printerZ, face);

    opSplitByIsocline(context, id + "splitByIsocline", {
                "faces" : face,
                "direction" : printerZ,
                "angle" : angle
            });

    const splitEdges = evaluateQuery(context, qCreatedBy(id + "splitByIsocline", EntityType.EDGE));
    const splitFaces = evaluateQuery(context, qAdjacent(splitEdges[0], AdjacencyType.EDGE, EntityType.FACE));
    const upFace = startTracking(context, qFarthestAlong(qUnion(splitFaces), printerZ));
    const extrude1Dir = -yAxis(evFaceTangentPlaneAtEdge(context, { "edge" : splitEdges[0], "face" : upFace, "parameter" : 0.5 }));
    const extrude2Dir = -yAxis(evFaceTangentPlaneAtEdge(context, { "edge" : splitEdges[1], "face" : upFace, "parameter" : 0.5 }));

    opExtrude(context, id + "extrude1", {
                "entities" : splitEdges[0],
                "direction" : extrude1Dir,
                "endBound" : BoundingType.UP_TO_SURFACE,
                "endDepth" : 1 * inch,
                "endBoundEntity" : dropletApexPlane
            });

    opExtrude(context, id + "extrude2", {
                "entities" : splitEdges[1],
                "direction" : extrude2Dir,
                "endBound" : BoundingType.UP_TO_SURFACE,
                "endDepth" : 1 * inch,
                "endBoundEntity" : dropletApexPlane
            });

    opDeleteBodies(context, id + "deleteEndBoundPlane", {
                "entities" : dropletApexPlane
            });

    var dropletFaces = qUnion([qCreatedBy(id + "extrude1", EntityType.BODY), qCreatedBy(id + "extrude2", EntityType.BODY)]);

    return dropletFaces;
}
