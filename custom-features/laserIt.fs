FeatureScript 1010;
import(path : "onshape/std/geometry.fs", version : "1010.0");



annotation { "Feature Type Name" : "Laser It" }
export const laserIt = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        definition.selectedBody is Query;

        annotation { "Name" : "Plane Spacing" }
        isLength(definition.planeSpacing, LENGTH_BOUNDS);

        annotation { "Name" : "Offset" }
        definition.offset is boolean;

        if (definition.offset)
        {
            annotation { "Name" : "X Offset" }
            isLength(definition.xOffset, LENGTH_BOUNDS);

            annotation { "Name" : "Y Offset" }
            isLength(definition.yOffset, LENGTH_BOUNDS);

        }

        annotation { "Name" : "Material Thickness" }
        isLength(definition.matThick, LENGTH_BOUNDS);


        annotation { "Name" : "Define Reference Frame" }
        definition.defRefFrame is boolean;


        if (definition.defRefFrame)
        {
            annotation { "Name" : "Reference Frame", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
            definition.referenceFrame is Query;
        }

    }
    {
        var refFrame = WORLD_COORD_SYSTEM;
        var xOffset = 0 * millimeter;
        var yOffset = 0 * millimeter;

        if (definition.defRefFrame == true)
        {
            refFrame = evMateConnector(context, {
                        "mateConnector" : definition.referenceFrame
                    });
        }

        if (definition.offset)
        {
            xOffset = definition.xOffset;
            yOffset = definition.yOffset;
        }

        // Use the coordinate system if provided to define the bounding box (start and end of planes)
        var obBox = evBox3d(context, {
                "topology" : definition.selectedBody,
                "cSys" : refFrame,
                "tight" : true
            });

        refFrame.origin = toWorld(refFrame, box3dCenter(obBox));

        var refFrameToWorld = toWorld(refFrame);

        var numXPlanes = (obBox.maxCorner[0] - obBox.minCorner[0]) / definition.planeSpacing;
        var numYPlanes = (obBox.maxCorner[1] - obBox.minCorner[1]) / definition.planeSpacing;
        var toDelete = qNothing();

        // Two loops are run to construct X and Y planes respectively
        // TODO Get rid of repeaded code and functionalize
        for (var i = 0; i < numXPlanes; i += 1)
        {
            var xLoc = -(obBox.maxCorner[0] - obBox.minCorner[0]) / 2 + xOffset + i * definition.planeSpacing; // Translate X coord into new ref frame

            var xPlane = refFrameToWorld * plane(vector([xLoc, 0 * millimeter, 0 * millimeter]), vector([1, 0, 0]), vector([0, 1, 0]));

            var sketchRectangle = newSketchOnPlane(context, id + "X" + i + "sketch", {
                    "sketchPlane" : xPlane
                });

            skRectangle(sketchRectangle, "rectangle1", {
                        "firstCorner" : -vector([obBox.maxCorner[1] - obBox.minCorner[1], obBox.maxCorner[2] - obBox.minCorner[2]]) / 2,
                        "secondCorner" : vector([obBox.maxCorner[1] - obBox.minCorner[1], obBox.maxCorner[2] - obBox.minCorner[2]]) / 2
                    });
                    
            skSolve(sketchRectangle);

            toDelete = qUnion([toDelete, qCreatedBy(id + "X" + i + "sketch")]);

            // Extrude a rectangular slice surrounding the object
            opExtrude(context, id + "X" + i + "extrudeRectangle", {
                        "entities" : qSketchRegion(id + "X" + i + "sketch", false),
                        "direction" : xPlane.normal,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : definition.matThick
                    });

            opBoolean(context, id + "X" + i + "booleanIntersection", {
                        "tools" : qUnion([qCreatedBy(id + "X" + i + "extrudeRectangle", EntityType.BODY), definition.selectedBody]),
                        "operationType" : BooleanOperationType.INTERSECTION,
                        "keepTools" : true
                    });
            toDelete = qUnion([toDelete, qCreatedBy(id + "X" + i + "extrudeRectangle", EntityType.BODY)]);

            toDelete = qUnion([toDelete, qCreatedBy(id + "X" + i + "booleanIntersection")]);

        }


        // Here is the Y loop
        for (var i = 0; i < numYPlanes; i += 1)
        {
            var yLoc = -(obBox.maxCorner[1] - obBox.minCorner[1]) / 2 + yOffset + i * definition.planeSpacing; // Translate X coord into new ref frame

            var yPlane = refFrameToWorld * plane(vector([0 * millimeter, yLoc, 0 * millimeter]), vector([0, 1, 0]), vector([0, 0, 1]));

            var sketchRectangle = newSketchOnPlane(context, id + "Y" + i + "sketch", {
                    "sketchPlane" : yPlane
                });

            skRectangle(sketchRectangle, "rectangle1", {
                        "firstCorner" : -vector([obBox.maxCorner[2] - obBox.minCorner[2], obBox.maxCorner[0] - obBox.minCorner[0]]) / 2,
                        "secondCorner" : vector([obBox.maxCorner[2] - obBox.minCorner[2], obBox.maxCorner[0] - obBox.minCorner[0]]) / 2
                    });

            skSolve(sketchRectangle);
            
            toDelete = qUnion([toDelete, qCreatedBy(id + "Y" + i + "sketch")]);

            // Extrude a rectangular slice surrounding the object
            opExtrude(context, id + "Y" + i + "extrudeRectangle", {
                        "entities" : qSketchRegion(id + "Y" + i + "sketch", false),
                        "direction" : yPlane.normal,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : definition.matThick
                    });

            opBoolean(context, id + "Y" + i + "booleanIntersection", {
                        "tools" : qUnion([qCreatedBy(id + "Y" + i + "extrudeRectangle", EntityType.BODY), definition.selectedBody]),
                        "operationType" : BooleanOperationType.INTERSECTION,
                        "keepTools" : true
                    });

            toDelete = qUnion([toDelete, qCreatedBy(id + "Y" + i + "extrudeRectangle", EntityType.BODY)]);
            toDelete = qUnion([toDelete, qCreatedBy(id + "Y" + i + "booleanIntersection")]);

        }


        for (var i = 0; i < numXPlanes; i += 1)
        {
            for (var j = 0; j < numYPlanes; j += 1)
            {
                try
                {
                    // Go through evaluateQuery to find booleans which result in multiple bodies
                    var k = 0;
                    var l = 0;
                    for (var subUnitX in evaluateQuery(context, qCreatedBy(id + "X" + i + "booleanIntersection", EntityType.BODY)))
                    {

                        for (var subUnitY in evaluateQuery(context, qCreatedBy(id + "Y" + j + "booleanIntersection", EntityType.BODY)))
                        {
                            try
                            {

                                opBoolean(context, id + "XY" + i + j + "boolean1" + k + l + "Subunit", {
                                            "tools" : qUnion([subUnitX, subUnitY]),
                                            "operationType" : BooleanOperationType.INTERSECTION,
                                            "keepTools" : true
                                        });
                            }
                            k = k + 1;
                        }
                        l = l + 1;
                    }

                    var allEdges = evaluateQuery(context, qOwnedByBody(qCreatedBy(id + "XY" + i + j + "boolean1", EntityType.BODY), EntityType.EDGE));
                    var accumulator = 0 * millimeter;
                    var numAdded = 0;
                    for (var edge in allEdges)
                    {
                        try
                        {
                            var isLine = evLine(context, {
                                    "edge" : edge
                                });
                            if (abs(dot(isLine.direction, refFrame.zAxis)) > .9999) // Normal tolerance of ParallelVectors is too tight for some reason after error accumulates
                            {
                                // Find the midpoint of the edge and add to average
                                var tanLine = evEdgeTangentLine(context, {
                                        "edge" : edge,
                                        "parameter" : .5
                                    });
                                numAdded += 1;
                                accumulator += dot(tanLine.origin, refFrame.zAxis);
                            }
                        }
                    }
                    if (numAdded > 0)
                    {
                        var slicePlaneOrigin = (refFrame.zAxis * accumulator / numAdded) / squaredNorm(refFrame.zAxis);
                        var slicePlane = plane(slicePlaneOrigin, refFrame.zAxis);
                        opPlane(context, id + "XY" + i + j + "plane1", {
                                    "plane" : slicePlane
                                });
                        opSplitPart(context, id + "XY" + i + j + "splitPart1", {
                                    "targets" : qCreatedBy(id + "XY" + i + j + "boolean1", EntityType.BODY),
                                    "tool" : qCreatedBy(id + "XY" + i + j + "plane1", EntityType.BODY)
                                });
                        opBoolean(context, id + "XY" + i + j + "boolean2", {
                                    "tools" : qFarthestAlong(qOwnerBody(qCreatedBy(id + "XY" + i + j + "splitPart1")), refFrame.zAxis),
                                    "targets" : qCreatedBy(id + "X" + i + "booleanIntersection", EntityType.BODY),
                                    "operationType" : BooleanOperationType.SUBTRACTION
                                });

                        opBoolean(context, id + "XY" + i + j + "boolean3", {
                                    "tools" : qFarthestAlong(qOwnerBody(qCreatedBy(id + "XY" + i + j + "splitPart1")), -refFrame.zAxis),
                                    "targets" : qCreatedBy(id + "Y" + j + "booleanIntersection", EntityType.BODY),
                                    "operationType" : BooleanOperationType.SUBTRACTION
                                });
                        toDelete = qUnion([toDelete, qCreatedBy(id + "XY" + i + j + "plane1")]);
                    }
                }
            }
        }

        for (var i = 0; i < numXPlanes; i += 1)
        {
            var xLoc = -(obBox.maxCorner[0] - obBox.minCorner[0]) / 2 + xOffset + i * definition.planeSpacing; // Translate X coord into new ref frame

            var xPlane = refFrameToWorld * plane(vector([xLoc, 0 * millimeter, 0 * millimeter]), vector([1, 0, 0]), vector([0, 1, 0]));
            var j = 0;
            for (var subUnitX in evaluateQuery(context, qCoincidesWithPlane(qOwnedByBody(qOwnerBody(qCreatedBy(id + "X" + i + "booleanIntersection")), EntityType.FACE), xPlane)))
            {
                try
                {
                    opExtrude(context, id + "XExtrude" + i + "extrudeIntersection" + j + "subUnitOp", {
                                "entities" : subUnitX,
                                "direction" : xPlane.normal,
                                "endBound" : BoundingType.BLIND,
                                "endDepth" : definition.matThick
                            });
                }
                j = j + 1;
            }

        }
        
        
        for (var i = 0; i < numYPlanes; i += 1)
        {
           var yLoc = -(obBox.maxCorner[1] - obBox.minCorner[1]) / 2 + yOffset + i * definition.planeSpacing; // Translate X coord into new ref frame

            var yPlane = refFrameToWorld * plane(vector([0 * millimeter, yLoc, 0 * millimeter]), vector([0, 1, 0]), vector([0, 0, 1]));

            var j = 0;
            for (var subUnitY in evaluateQuery(context, qCoincidesWithPlane(qOwnedByBody(qOwnerBody(qCreatedBy(id + "Y" + i + "booleanIntersection")), EntityType.FACE), yPlane)))
            {
                try
                {
                    opExtrude(context, id + "YExtrude" + i + "extrudeIntersection" + j + "subUnitOp", {
                                "entities" : subUnitY,
                                "direction" : yPlane.normal,
                                "endBound" : BoundingType.BLIND,
                                "endDepth" : definition.matThick
                            });
                }
                j = j + 1;
            }

        }
        
        // Clean up
        opDeleteBodies(context, id + "deleteBodies1", {
                    "entities" : toDelete
                });

    });


