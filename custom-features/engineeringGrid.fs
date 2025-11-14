FeatureScript 2770;
import(path : "onshape/std/common.fs", version : "2770.0");
import(path : "dd01b194146fb5491e125fc5/b8531ec7d23c866c45889f32/0497316eced6618710ada5ec", version : "b073a5aea18d646b9dbb7ddb");
icon::import(path : "a1b0d90ee3b7dcef5111a3d4", version : "3bb7f3ccd2b27674726f82bd");
featureimage::import(path : "9ce139822d898b55238f4bd0", version : "8cb849eb3f05f54d585cb52b");





export enum borderSelection
{
    annotation { "Name" : "Grid Within Border Only" }
    WITHIN,
    annotation { "Name" : "Extend Grid To Border" }
    EXTEND,
}

export enum centerSelection
{
    annotation { "Name" : "Vertex Centric" }
    VERTEX,
    annotation { "Name" : "Body Centric" }
    CENTER,
    annotation { "Name" : "From Edge" }
    FROM_EDGE,
}

export enum gridSelection
{
    annotation { "Name" : "ISOGrid" }
    ISOGRID,
    annotation { "Name" : "OrthoGrid" }
    ORTHOGRID,
}


export const PERCENT_BOUNDS = { (unitless) : [0, 30, 100] } as RealBoundSpec;
export const ANGLE_OFFSET_BOUNDS = { (degree) : [-360, 0, 360] } as AngleBoundSpec;
export const WIDTH_BOUNDS = { (inch) : [0, 0.1, 100] } as LengthBoundSpec;
export const DEPTH_BOUNDS = { (inch) : [0.00001, 0.125, 100] } as LengthBoundSpec;
export const FILLET_BOUNDS = { (inch) : [0, 0.0625, 100] } as LengthBoundSpec;


annotation { "Feature Type Name" : "Engineering Grid", "Feature Type Description" : "Create ISO grid and Ortho grid patterns on the selected face. Delete any pockets that are small. Add internal fillets and delete any bodies that have fillets impossible to machine." , "Icon" : icon::BLOB_DATA,"Description Image" : featureimage::BLOB_DATA}
export const engineeringGrid = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Grid Selection", "UIHint" : [UIHint.HORIZONTAL_ENUM,UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.gridSelection is gridSelection;

        annotation { "Name" : "Select Face", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
        definition.selectedFace is Query;

        annotation { "Name" : "Merge Scope", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        definition.lightenedBodies is Query;

        annotation { "Name" : "Direction (Optional)", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.direction is Query;

        //  || GeometryType.LINE || GeometryType.CIRCLE || GeometryType.PLANE || GeometryType.ARC || EntityType.FACE

        annotation { "Group Name" : "Grid Definition", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Depth" , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
            isLength(definition.depth, DEPTH_BOUNDS);

            annotation { "Name" : "Invert", "UIHint" : UIHint.OPPOSITE_DIRECTION }
            definition.invert is boolean;

            annotation { "Name" : "Rib Thickness" , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
            isLength(definition.ribThickness, WIDTH_BOUNDS);

            if (definition.gridSelection == gridSelection.ISOGRID)
            {
                annotation { "Name" : "Side Length", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.sideLength, LENGTH_BOUNDS);
            }
            else if (definition.gridSelection == gridSelection.ORTHOGRID)
            {
                annotation { "Name" : "Rectangle Height", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.rectangleHeight, LENGTH_BOUNDS);
                annotation { "Name" : "Rectangle Width" , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
                isLength(definition.rectangleWidth, LENGTH_BOUNDS);


            }
            if (definition.gridSelection == gridSelection.ISOGRID)
            {
                annotation { "Name" : "Holes", "UIHint" : UIHint.DISPLAY_SHORT , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
            definition.holeBoolean is boolean;
            if (definition.holeBoolean)
            {
                annotation { "Name" : "Hole Diameter", "UIHint" : UIHint.DISPLAY_SHORT , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
                isLength(definition.holeDiameter, WIDTH_BOUNDS);

            }
            }
            
            annotation { "Name" : "Angle Offset" }
            isAngle(definition.angleOffset, ANGLE_OFFSET_BOUNDS);


        }

        annotation { "Group Name" : "Fillets", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Fillet Radius" , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
            isLength(definition.filletRadius, FILLET_BOUNDS);

            annotation { "Name" : "Fillet Inside Edges" }
            definition.filletLowerEdgesBool is boolean;

            if (definition.filletLowerEdgesBool)
            {
                annotation { "Name" : "Inside Fillet Radius" }
                isLength(definition.lowerFilletRadius, FILLET_BOUNDS);

            }
            annotation { "Name" : "Remove Bad Fillet Bodies" }
            definition.removeBadFilletBodies is boolean;
            
            annotation { "Name" : "Show Bad Fillet Bodies" }
            definition.showBadFilletBodies is boolean;
            
        }
        annotation { "Group Name" : "Border", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Border Selection", "Default" : borderSelection.EXTEND , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
            definition.borderSelection is borderSelection;

            annotation { "Name" : "Border Width" , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
            isLength(definition.borderWidth, WIDTH_BOUNDS);

            if (definition.borderSelection == borderSelection.EXTEND)
            {
                annotation { "Name" : "Remove Small Bodies", "Description" : "Remove small bodies intersecting the border." , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
                definition.removeSmallgrids is boolean;

                if (definition.removeSmallgrids)
                {
                    annotation { "Name" : "Percent Cut-off", "Description" : "Percent cut-off defining 'small'." , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
                    isReal(definition.percentCutOff, PERCENT_BOUNDS);
                }
            }
            // annotation { "Name" : "Double Invert", "Description" : "Invert the direction of the grid when the invert is in the wrong direction to begin with." }
            // definition.doubleInvert is boolean;
            
        }

        annotation { "Group Name" : "Grid Center", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Center Selection" , "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE}
            definition.centerSelection is centerSelection;

            if (definition.centerSelection == centerSelection.FROM_EDGE)
            {
                annotation { "Name" : "Flip From Edge", "UIHint" : [UIHint.OPPOSITE_DIRECTION, UIHint.DISPLAY_SHORT], "Description" : "Flip the grids to align with the edge within the perimeter of the selected face." }
                definition.flipFromEdge is boolean;

            }
        }



        annotation { "Name" : "debug", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.debugBoolean is boolean;

        if (definition.debugBoolean)
        {
            annotation { "Group Name" : "debug", "Collapsed By Default" : false, "Driving Parameter" : "debugBoolean" }
            {
                annotation { "Name" : "Sketch Plane (red)" }
                definition.debugSketchPlane is boolean;

                annotation { "Name" : "Center Line (green)" }
                definition.debugCenterLine is boolean;

                annotation { "Name" : "All Array Points (rainbow)" }
                definition.debugAllArrayPoints is boolean;

                annotation { "Name" : "All grids (cyan)" }
                definition.debugAllgrids is boolean;

                annotation { "Name" : "Final Array Points (red)" }
                definition.debugFinalArrayPoints is boolean;

                annotation { "Name" : "Final grids (orange)" }
                definition.debugFinalgrids is boolean;

                annotation { "Name" : "Bounding Box (blue)" }
                definition.debugBoundingBox is boolean;

                annotation { "Name" : "Border Subtractor (magenta)" }
                definition.debugBorderSubtractor is boolean;

            }

        }

    }
    {
        var part = makeRobustQuery(context, qOwnerBody(definition.selectedFace));
        // definition.lightenedBodies = qOwnerBody(definition.selectedFace);
        const isISOGrid = definition.gridSelection == gridSelection.ISOGRID;
        const isOrthoGrid = definition.gridSelection == gridSelection.ORTHOGRID;
        var sideLength = definition.sideLength;
        var gridHeight = sideLength * sqrt(3) / 2;
        var xDimension = sideLength + definition.ribThickness * 1.73205;
        var yDimension = (2 * gridHeight + 3 * definition.ribThickness);
        var gridYPositiveHeight = gridHeight * 2 / 3 + definition.ribThickness;
        var gridFromEdgeOffset = gridHeight / 3 + definition.borderWidth;
        definition.lowerFilletRadius = definition.filletLowerEdgesBool ? definition.lowerFilletRadius : 0 * inch;

        var isoHolesBoolean = definition.holeBoolean && isISOGrid;

        if (isOrthoGrid)
        {
            xDimension = definition.rectangleWidth + definition.ribThickness;
            yDimension = definition.rectangleHeight + definition.ribThickness;
            gridYPositiveHeight = definition.rectangleHeight / 2 + definition.ribThickness / 2;
            gridFromEdgeOffset = definition.rectangleHeight / 2 + definition.borderWidth;
        }

        definition.percentCutOff = definition.borderSelection == borderSelection.WITHIN ? 99.9 : definition.percentCutOff;
        if (isQueryEmpty(context, definition.selectedFace) && isQueryEmpty(context, definition.lightenedBodies))
        {
            return;
        } else if (isQueryEmpty(context, definition.lightenedBodies) && !isQueryEmpty(context, definition.selectedFace))
        {
            throw regenError("Please select a body to merge the pattern.");
        } else if (!isQueryEmpty(context, definition.lightenedBodies) && isQueryEmpty(context, definition.selectedFace))
        {
            throw regenError("Please select a face to apply the pattern.");
        } else if (isQueryEmpty(context, definition.direction) && definition.centerSelection == centerSelection.FROM_EDGE)
        {
            throw regenError("Must select a direction when selecting 'From Edge' grid center.");
        }
        
        
        var selectedPlane = evPlane(context, {
                "face" : definition.selectedFace
            });
        var selectedCoordSys = coordSystem(selectedPlane);
        const centroid = evApproximateCentroid(context, {
                    "entities" : definition.selectedFace
                });

        selectedPlane = plane(centroid, selectedCoordSys.zAxis, selectedCoordSys.xAxis);
        var directionAxis = { "direction" : yAxis(selectedCoordSys) };

        var centerAxis = line(centroid, selectedCoordSys.zAxis);


        if (!isQueryEmpty(context, definition.direction))
        {
            if (definition.centerSelection != centerSelection.FROM_EDGE)
            {
                directionAxis = evAxis(context, {
                            "axis" : definition.direction
                        });
                println(directionAxis);

                selectedPlane = plane(selectedPlane.origin, selectedCoordSys.zAxis, -directionAxis.direction);
                try {
                selectedCoordSys = coordSystem(selectedPlane);
                } catch (error)
                {
                    throw regenError("Direction selection failed. Please select a direction parallel to the selected face.");
                }
                if (definition.centerSelection == centerSelection.VERTEX)
                {
                    selectedPlane = plane(centerAxis.origin - yAxis(selectedCoordSys) * (gridYPositiveHeight), selectedCoordSys.zAxis, selectedCoordSys.xAxis);
                    selectedCoordSys = coordSystem(selectedPlane);
                }
            }
            else
            {

                directionAxis = evAxis(context, {
                            "axis" : definition.direction
                        });

                selectedPlane = plane(selectedPlane.origin, selectedCoordSys.zAxis, -directionAxis.direction);
                try {
                selectedCoordSys = coordSystem(selectedPlane);
                } catch (error)
                {
                    throw regenError("Direction selection failed. Please select a direction parallel to the selected face.");
                }

            }
        }
        else
        {
            if (definition.centerSelection == centerSelection.VERTEX)
            {
                selectedPlane = plane(centerAxis.origin - yAxis(selectedCoordSys) * (gridYPositiveHeight), selectedCoordSys.zAxis, selectedCoordSys.xAxis);
                selectedCoordSys = coordSystem(selectedPlane);
            }
        }

        selectedCoordSys = coordSystem(selectedPlane);

        var fromEdgeFlip = definition.flipFromEdge ? -1 : 1;
        if (definition.centerSelection == centerSelection.FROM_EDGE)
        {
            
                directionAxis = evAxis(context, {
                            "axis" : definition.direction
                        });
            
            selectedPlane = evFaceTangentPlaneAtEdge(context, {
                        "edge" : definition.direction,
                        "face" : definition.selectedFace,
                        "parameter" : 0.5
                    });
            selectedCoordSys = coordSystem(selectedPlane);

            selectedPlane = plane(selectedPlane.origin - yAxis(selectedCoordSys) * (gridFromEdgeOffset) * fromEdgeFlip, selectedCoordSys.zAxis, -directionAxis.direction * fromEdgeFlip);
            centerAxis = line(selectedPlane.origin - yAxis(selectedCoordSys) * (gridYPositiveHeight) * fromEdgeFlip, selectedCoordSys.zAxis);
            selectedCoordSys = coordSystem(selectedPlane);

        }

        selectedPlane = plane(selectedPlane.origin, selectedCoordSys.zAxis, rotationMatrix3d(selectedCoordSys.zAxis, definition.angleOffset) * selectedCoordSys.xAxis);
        selectedCoordSys = coordSystem(selectedPlane);

        const bbox = evBox3d(context, {
                    "topology" : definition.selectedFace,
                    "tight" : true,
                    "cSys" : selectedCoordSys
                });

        betterDebug(context, bbox, DebugColor.BLUE, definition.debugBoolean && definition.debugBoundingBox);

        const xWidth = abs(bbox.maxCorner[0] - bbox.minCorner[0]);
        const yWidth = abs(bbox.maxCorner[1] - bbox.minCorner[1]);
        const xNum = ceil((xWidth / (xDimension)));
        println("xNum: " ~ toString(xNum) ~ " xWidth: " ~ toString(xWidth));
        const yNum = ceil(yWidth / (yDimension));
        println("yNum: " ~ toString(yNum) ~ " yWidth: " ~ toString(yWidth));


        if (definition.centerSelection == centerSelection.FROM_EDGE)
        {
            var directionToCentroid = selectedPlane.origin - centroid;
            var xOffsetNum = round(xNum / 4 * cos(angleBetween(directionToCentroid, selectedCoordSys.xAxis)));
            var yOffsetNum = round(yNum / 4 * cos(angleBetween(directionToCentroid, yAxis(selectedCoordSys))));
            println("xOffsetNum " ~ toString(xOffsetNum) ~ " angle " ~ toString(cos(angleBetween(directionToCentroid, selectedCoordSys.xAxis))));
            println("yOffsetNum " ~ toString(yOffsetNum) ~ " angle " ~ toString(cos(angleBetween(directionToCentroid, yAxis(selectedCoordSys)))));
            selectedPlane = plane(selectedPlane.origin - yAxis(selectedCoordSys) * (yOffsetNum * yDimension) - selectedCoordSys.xAxis * xOffsetNum * xDimension, selectedCoordSys.zAxis, selectedCoordSys.xAxis);
            selectedCoordSys = coordSystem(selectedPlane);
        }

        centerAxis = line(selectedPlane.origin + yAxis(selectedCoordSys) * (gridYPositiveHeight), selectedCoordSys.zAxis);

        // debug(context, selectedCoordSys.xAxis, DebugColor.MAGENTA);
        //     debug(context, yAxis(selectedCoordSys), DebugColor.YELLOW);

        if (definition.centerSelection == centerSelection.VERTEX)
        {
            
            centerAxis = line(centroid, selectedCoordSys.zAxis);
            if (definition.gridSelection == gridSelection.ISOGRID)
            {
                selectedPlane = plane(centroid - yAxis(selectedCoordSys) * (gridYPositiveHeight), selectedCoordSys.zAxis, selectedCoordSys.xAxis);
            } else if (definition.gridSelection == gridSelection.ORTHOGRID)
            {
                selectedPlane = plane(centroid - yAxis(selectedCoordSys) * (gridYPositiveHeight) - selectedCoordSys.xAxis * (definition.rectangleWidth / 2 + definition.ribThickness / 2), selectedCoordSys.zAxis, selectedCoordSys.xAxis);
            }
            
            selectedCoordSys = coordSystem(selectedPlane);
        }
        
        // if (definition.doubleInvert)
        // {
        //     selectedPlane = plane(selectedCoordSys.origin, -selectedCoordSys.zAxis, selectedCoordSys.xAxis);
        //     selectedCoordSys = planeToCSys(selectedPlane);
        //     centerAxis = line(centerAxis.origin,- selectedCoordSys.zAxis);
        // }

        const point1 = centerAxis.origin;

        betterDebug(context, selectedPlane, DebugColor.RED, definition.debugBoolean && definition.debugSketchPlane);
        betterDebug(context, centerAxis, DebugColor.GREEN, definition.debugBoolean && definition.debugCenterLine);

        if (definition.debugBoolean && definition.debugAllArrayPoints)
        {
            addDebugPoint(context, point1, DebugColor.BLACK);

        }

        var pointArray = pointGrid(context, definition, {
                "origin" : point1,
                "coordSys" : selectedCoordSys,
                "xDimension" : xDimension,
                "yDimension" : yDimension,
                "xNum" : xNum,
                "yNum" : yNum
            });

        opExtrude(context, id + "pointCheck1", {
                    "entities" : definition.selectedFace,
                    "direction" : selectedCoordSys.zAxis,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : definition.depth,
                    "startBound" : BoundingType.BLIND,
                    "startDepth" : definition.depth
                });
        betterDebug(context, qCreatedBy(id + "pointCheck1", EntityType.BODY), DebugColor.YELLOW, definition.debugBoolean);
        opOffsetFace(context, id + "borderOffset1", {
                    "moveFaces" : qFacesParallelToDirection(qCreatedBy(id + "pointCheck1", EntityType.FACE), selectedCoordSys.zAxis),
                    "offsetDistance" : max(xDimension,yDimension)
                });
        betterDebug(context, qCreatedBy(id + "pointCheck1", EntityType.BODY), DebugColor.RED, definition.debugBoolean);
        // betterDebug(context, qCreatedBy(id + "pointCheck2", EntityType.BODY), DebugColor.RED, definition.debugBoolean);
        var deleteArray = [qCreatedBy(id + "pointCheck1", EntityType.BODY)];

        var pointCheckArray = [];
        var pointStep = 0;
        for (var point in pointArray)
        {

            if (!isQueryEmpty(context, qContainsPoint(qCreatedBy(id + "pointCheck1", EntityType.BODY), point)))
            {
                if (definition.debugBoolean && definition.debugFinalArrayPoints)
                {
                    addDebugPoint(context, point, DebugColor.RED);
                }
                pointCheckArray = append(pointCheckArray, point);
            }
            else
            {
                pointArray[pointStep] = 0;

            }
            pointStep += 1;
        }

        pointArray = pointCheckArray;
        const gridSketchId = id + "isoGrid";
        if (isISOGrid)
        {
            isoGridDrawing(context, { "sketchId" : gridSketchId,
                        "sketchPlane" : selectedPlane,
                        "sideLength" : sideLength,
                        "filletRadius" : 0 * inch,
                        "ribWidth" : definition.ribThickness,
                        "angleFromVertical" : 0 * degree,
                        "direction" : directionAxis
                    });
        }
        else if (isOrthoGrid)
        {
            orthoGridDrawing(context, {"sketchId" : gridSketchId,
                        "sketchPlane" : selectedPlane,
                        "sideLengthX" : definition.rectangleWidth,
                        "sideLengthY" : definition.rectangleHeight,
                        "filletRadius" : 0 * inch,
                        "ribWidth" : definition.ribThickness,
                        "angleFromVertical" : 0 * degree,
                        "direction" : directionAxis
                    });
        }

        deleteArray = append(deleteArray, qCreatedBy(gridSketchId));

        const isoHoleSketchId = id + "isoHole";
        var holeGrid = [];
        const holeExtrudeId = id + "holes";

        var invertCoefficient = definition.invert ? -1 : 1;
        if (isoHolesBoolean)
        {
            isoHoleDrawing(context, { "sketchId" : isoHoleSketchId,
                        "sketchPlane" : selectedPlane,
                        "triangleHeight" : gridHeight,
                        "ribThickness" : definition.ribThickness,
                        "holeDiameter" : definition.holeDiameter });
            deleteArray = append(deleteArray, qCreatedBy(isoHoleSketchId));
            opExtrude(context, holeExtrudeId, {
                        "entities" : qSketchRegion(isoHoleSketchId, false),
                        "direction" : -selectedCoordSys.zAxis * invertCoefficient,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : definition.depth
                    });
            holeGrid = [qCreatedBy(holeExtrudeId, EntityType.BODY)];
        }

        opExtrude(context, id + "extrude1", {
                    "entities" : qSketchRegion(gridSketchId, false),
                    "direction" : -selectedCoordSys.zAxis * invertCoefficient,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : definition.depth
                });

        var lowerFilletplane = plane(selectedPlane.origin - selectedCoordSys.zAxis * definition.depth * (invertCoefficient + abs(invertCoefficient)) / 2, selectedCoordSys.zAxis, selectedCoordSys.xAxis);

        if (definition.filletRadius != 0 * inch)
        {
            var filletEdges = qParallelEdges(qOwnedByBody(qCreatedBy(id + "extrude1", EntityType.BODY), EntityType.EDGE), selectedCoordSys.zAxis);
            opFillet(context, id + "gridFillet", {
                        "entities" : filletEdges,
                        "radius" : definition.filletRadius
                    });
        }

        var holeVolume = evVolume(context, {
                "entities" : qUnion(holeGrid),
                "accuracy" : VolumeAccuracy.HIGH
            });
        holeVolume = holeVolume / 2;

        const gridVolume = evVolume(context, {
                    "entities" : qCreatedBy(id + "extrude1", EntityType.BODY),
                    "accuracy" : VolumeAccuracy.HIGH
                });

        var gridArray = [qCreatedBy(id + "extrude1", EntityType.BODY)];
        var initialGrid = gridArray;

        if (definition.gridSelection == gridSelection.ISOGRID)
        {
            opPattern(context, id + "pattern1", {
                        "entities" : qCreatedBy(id + "extrude1", EntityType.BODY),
                        "transforms" : [rotationAround(centerAxis, 60 * degree), rotationAround(centerAxis, 120 * degree), rotationAround(centerAxis, 180 * degree)],
                        "instanceNames" : ["rotation1", "rotation2", "rotation3"]
                    });

            gridArray = append(gridArray, qCreatedBy(id + "pattern1", EntityType.BODY));
            deleteArray = append(deleteArray, qCreatedBy(id + "pattern1"));
        }

        initialGrid = gridArray;
        var allGridBodies = gridArray;
        allGridBodies = append(allGridBodies, qCreatedBy(holeExtrudeId, EntityType.BODY));

        var transforms = gridTransforms(context, { "pointArray" : pointArray, "origin" : centerAxis.origin });

        opPattern(context, id + "patternAll", {
                    "entities" : qUnion(initialGrid),
                    "transforms" : transforms.gridArray,
                    "instanceNames" : transforms.gridNames
                });
        if (isoHolesBoolean)
        {
            opPattern(context, id + "patternAllHoles", {
                        "entities" : qUnion(holeGrid),
                        "transforms" : transforms.gridArray,
                        "instanceNames" : transforms.gridNames
                    });
            holeGrid = append(holeGrid, qCreatedBy(id + "patternAllHoles", EntityType.BODY));
            allGridBodies = append(allGridBodies, qCreatedBy(id + "patternAllHoles", EntityType.BODY));
        }


        gridArray = append(gridArray, qCreatedBy(id + "patternAll", EntityType.BODY));
        deleteArray = append(deleteArray, qCreatedBy(id + "patternAll", EntityType.BODY));
        allGridBodies = append(allGridBodies, qCreatedBy(id + "patternAll", EntityType.BODY));

        opExtrude(context, id + "outline1", {
                    "entities" : definition.selectedFace,
                    "direction" : selectedCoordSys.zAxis,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : 0.01 * inch + definition.borderWidth + definition.depth * 2,
                    "startBound" : BoundingType.BLIND,
                    "startDepth" : definition.borderWidth + definition.depth * 3
                });

        opOffsetFace(context, id + "offsetFace1", {
                    "moveFaces" : qCreatedBy(id + "outline1", EntityType.FACE),
                    "offsetDistance" : -definition.borderWidth
                });

        opExtrude(context, id + "outline2", {
                    "entities" : definition.selectedFace,
                    "direction" : selectedCoordSys.zAxis,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : definition.depth * 2,
                    "startBound" : BoundingType.BLIND,
                    "startDepth" : definition.depth * 2
                });
        var offsetDistanceDivisor = definition.gridSelection == gridSelection.ISOGRID ? 1.5 : 2;
        opOffsetFace(context, id + "offsetFace2", {
                    "moveFaces" : qFacesParallelToDirection(qCreatedBy(id + "outline2", EntityType.FACE), selectedCoordSys.zAxis),
                    "offsetDistance" : max(xWidth, yWidth) / offsetDistanceDivisor
                });
        if (definition.invert)
        {
            opExtrude(context, id + "invert", {
                        "entities" : definition.selectedFace,
                        "direction" : selectedCoordSys.zAxis,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : definition.depth,
                        // "startBound" : BoundingType.BLIND,
                        // "startDepth" : 0.01 * inch
                    });
                    
                    // betterDebug(context, qCreatedBy(id + "invert", EntityType.BODY), DebugColor.RED, definition.debugBoolean);
        }

        opBoolean(context, id + "boolean2", {
                    "tools" : qCreatedBy(id + "outline1", EntityType.BODY),
                    "targets" : qCreatedBy(id + "outline2", EntityType.BODY),
                    "operationType" : BooleanOperationType.SUBTRACTION
                });

        betterDebug(context, qCreatedBy(id + "outline2", EntityType.BODY), DebugColor.MAGENTA, definition.debugBoolean && definition.debugBorderSubtractor);
        var subtractArray = gridArray;
        if (isoHolesBoolean)
        {
            subtractArray = append(subtractArray, qCreatedBy(holeExtrudeId, EntityType.BODY));
            subtractArray = append(subtractArray, qCreatedBy(id + "patternAllHoles", EntityType.BODY));

        }

        betterDebug(context, qUnion(subtractArray), DebugColor.CYAN, definition.debugBoolean && definition.debugAllgrids);
        opBoolean(context, id + "boolean3", {
                    "tools" : qCreatedBy(id + "outline2", EntityType.BODY),
                    "targets" : qUnion(subtractArray),
                    "operationType" : BooleanOperationType.SUBTRACTION,
                // "targetAndToolsNeedGrouping" : true
                });



        deleteSmallBodies(context, id, { "gridArray" : gridArray,
                    "coordSys" : selectedCoordSys,
                    "gridVolume" : gridVolume,
                    "filletRadius" : definition.filletRadius,
                    "percentCutOff" : definition.percentCutOff,
                    "removeSmallGrids" : definition.removeSmallgrids,
                    "lowerFilletRadius" : definition.lowerFilletRadius,
                    "lowerFilletPlane" : lowerFilletplane,
                    "deleteBadFillets" : definition.removeBadFilletBodies,
                    "showBadFillets" : definition.showBadFilletBodies
                });

        if (isoHolesBoolean)
        {

            deleteSmallHoles(context, id, { "holeArray" : holeGrid,
                        "coordSys" : selectedCoordSys,
                        "gridVolume" : gridVolume,
                        "filletRadius" : definition.filletRadius,
                        "percentCutOff" : definition.percentCutOff,
                        "removeSmallGrids" : definition.removeSmallgrids,
                        "holeVolume" : holeVolume,
                        "deleteBadFillets" : definition.removeBadFilletBodies,
                    "showBadFillets" : definition.showBadFilletBodies
                    });
        }

        if (definition.filletLowerEdgesBool)
        {
            lowerFillet(context, id, { "gridArray" : allGridBodies,
                        "coordSys" : selectedCoordSys,
                        "lowerFilletRadius" : definition.lowerFilletRadius,
                        "lowerFilletPlane" : lowerFilletplane,
                        "deleteBadFillets" : definition.removeBadFilletBodies,
                    "showBadFillets" : definition.showBadFilletBodies
                    });
        }

        betterDebug(context, qUnion(subtractArray), DebugColor.ORANGE, definition.debugBoolean && definition.debugFinalgrids);

        if (definition.invert)
        {

            opBoolean(context, id + "boolean5", {
                        "tools" : qUnion(definition.lightenedBodies, qCreatedBy(id + "invert", EntityType.BODY)),
                        "operationType" : BooleanOperationType.UNION
                    });
        }

        opBoolean(context, id + "boolean1", {
                    "tools" : qUnion(subtractArray),
                    "targets" : definition.lightenedBodies,
                    "operationType" : BooleanOperationType.SUBTRACTION
                });

        opDeleteBodies(context, id + "deleteBodies1", {
                    "entities" : qUnion(deleteArray)
                });
    });

function isoGridDrawing(context is Context, inputs is map)
{
    const sketchId = inputs.sketchId;
    const sketchPlane = inputs.sketchPlane;
    const sideLength = inputs.sideLength;
    const filletRadius = inputs.filletRadius;
    const ribWidth = inputs.ribWidth;
    const angleFromVertical = inputs.angleFromVertical;
    const direction = inputs.direction;
    const vertexRadius = sqrt(3) / 3 * sideLength;
    var firstVertex = vector(0 * inch, vertexRadius);

    const isoGridSketch = newSketchOnPlane(context, sketchId, {
                "sketchPlane" : sketchPlane
            });

    skRegularPolygon(isoGridSketch, "grid1", {
                "center" : vector(0, 0) * inch,
                "firstVertex" : firstVertex,
                "sides" : 3
            });

    skSolve(isoGridSketch);
}

function isoHoleDrawing(context is Context, inputs is map)
{
    const sketchId = inputs.sketchId;
    const sketchPlane = inputs.sketchPlane;
    const holeDiameter = inputs.holeDiameter;
    const triangleHeight = inputs.triangleHeight;
    const ribThickness = inputs.ribThickness;

    const centerPoint = vector(0 * inch, (ribThickness + triangleHeight * 2 / 3));
    const centerPoint2 = -vector((ribThickness + triangleHeight * 2 / 3) * cos(30 * degree), (ribThickness + triangleHeight * 2 / 3) * sin(30 * degree));

    const isoCircleSketch = newSketchOnPlane(context, sketchId, {
                "sketchPlane" : sketchPlane
            });
    skCircle(isoCircleSketch, "circle1", {
                "center" : centerPoint,
                "radius" : holeDiameter / 2
            });
    skCircle(isoCircleSketch, "circle2", {
                "center" : centerPoint2,
                "radius" : holeDiameter / 2
            });
    skSolve(isoCircleSketch);
}

function orthoGridDrawing(context is Context, inputs is map)
{
    const sketchId = inputs.sketchId;
    const sketchPlane = inputs.sketchPlane;
    const sideLengthX = inputs.sideLengthX;
    const sideLengthY = inputs.sideLengthY;
    const filletRadius = inputs.filletRadius;
    const ribWidth = inputs.ribWidth;
    const angleFromVertical = inputs.angleFromVertical;
    const direction = inputs.direction;
    
    const orthoGridSketch = newSketchOnPlane(context, sketchId, {
            "sketchPlane" : sketchPlane
    });
    skRectangle(orthoGridSketch, "rectangle1", {
            "firstCorner" : vector(sideLengthX / 2, sideLengthY/2),
            "secondCorner" :  vector(-sideLengthX / 2, -sideLengthY/2)
    });
    skSolve(orthoGridSketch);
}


function pointGrid(context is Context, definition is map, inputs is map)
{
    var origin = inputs.origin;
    var selectedCoordSys = inputs.coordSys;
    var xDimension = inputs.xDimension;
    var yDimension = inputs.yDimension;
    var xNum = inputs.xNum;
    var yNum = inputs.yNum;
    var pointArray = [origin];

    for (var i = 1; i <= xNum; i += 1)
    {
        var pointXPlus = origin + selectedCoordSys.xAxis * xDimension * i;
        var pointXMinus = origin - selectedCoordSys.xAxis * xDimension * i;
        pointArray = append(pointArray, pointXPlus);
        pointArray = append(pointArray, pointXMinus);
        if (definition.debugBoolean && definition.debugAllArrayPoints)
        {
            addDebugPoint(context, pointXPlus, DebugColor.MAGENTA);
            addDebugPoint(context, pointXMinus, DebugColor.MAGENTA);
        }

        for (var j = 1; j <= yNum; j += 1)
        {
            var pointYPlus = pointXPlus + yAxis(selectedCoordSys) * yDimension * (j);
            var pointYMinus = pointXPlus - yAxis(selectedCoordSys) * yDimension * (j);
            pointArray = append(pointArray, pointYPlus);
            pointArray = append(pointArray, pointYMinus);

            if (definition.debugBoolean && definition.debugAllArrayPoints)
            {
                addDebugPoint(context, pointYPlus, DebugColor.YELLOW);
                addDebugPoint(context, pointYMinus, DebugColor.YELLOW);
            }

            pointYPlus = pointXMinus + yAxis(selectedCoordSys) * yDimension * (j);
            pointYMinus = pointXMinus - yAxis(selectedCoordSys) * yDimension * (j);
            pointArray = append(pointArray, pointYPlus);
            pointArray = append(pointArray, pointYMinus);

            if (definition.debugBoolean && definition.debugAllArrayPoints)
            {
                addDebugPoint(context, pointYPlus, DebugColor.GREEN);
                addDebugPoint(context, pointYMinus, DebugColor.GREEN);
            }

        }
    }
    for (var i = 1; i <= yNum; i += 1)
    {
        var pointPlus = origin + yAxis(selectedCoordSys) * yDimension * (i);
        var pointMinus = origin - yAxis(selectedCoordSys) * yDimension * (i);
        pointArray = append(pointArray, pointPlus);
        pointArray = append(pointArray, pointMinus);

        if (definition.debugBoolean && definition.debugAllArrayPoints)
        {
            addDebugPoint(context, pointPlus, DebugColor.BLUE);
            addDebugPoint(context, pointMinus, DebugColor.BLUE);
        }
    }
    return pointArray;
}

function deleteSmallBodies(context is Context, id is Id, inputs is map)
{
    var gridArray = inputs.gridArray;
    var selectedCoordSys = inputs.coordSys;
    var gridVolume = inputs.gridVolume;
    var filletRadius = inputs.filletRadius;
    var percentCutOff = inputs.percentCutOff;
    var removeSmallGrids = inputs.removeSmallGrids;
    var deleteBadFillets = inputs.deleteBadFillets;
    var showBadFillets = inputs.showBadFillets;

    forEachEntity(context, id + "deleteLoop", qUnion(gridArray), function(entity is Query, id is Id)
        {
            var filletEdges = qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis);
            // debug(context, qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis), DebugColor.GREEN);
            var startingEdgesNum = evaluateQueryCount(context, filletEdges);
            var filletEdgeArray = evaluateQuery(context, filletEdges);
            filletEdgeArray = makeRobustQueriesBatched(context, qUnion(filletEdgeArray));
            
            var smallgridVolume = evVolume(context, {
                    "entities" : entity,
                    "accuracy" : VolumeAccuracy.HIGH
                });




            // println(filletEdgeArray);

            var gridSameVolumeBoolean = roundToPrecision(smallgridVolume / (inch ^ 3), 6) == roundToPrecision(gridVolume / (inch ^ 3), 6);
            var gridVolumeCheck = gridSameVolumeBoolean ? false : smallgridVolume / gridVolume > percentCutOff / 100 || !removeSmallGrids;


            if (gridVolumeCheck)
            {
                if (filletRadius > 0 * inch)
                {
                    try silent
                    {
                        opFillet(context, id + "fillet1", {
                                    "entities" : filletEdges,
                                    "radius" : filletRadius
                                });

                    }
                    var resultingEdgesNum = evaluateQueryCount(context, qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis));
                    // debug(context, qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis), DebugColor.BLUE);
                    
                    
                    // && startingEdgesNum != 6 || (resultingEdgesNum != 2 * startingEdgesNum)
                    if (resultingEdgesNum == startingEdgesNum )
                    {
                        println(startingEdgesNum);
                    println(resultingEdgesNum);
                        filletEdgeArray = qSubtraction(qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis), qParallelEdges(qAdjacent(qFilletFaces(qOwnedByBody(entity, EntityType.FACE), CompareType.EQUAL), AdjacencyType.EDGE, EntityType.EDGE), selectedCoordSys.zAxis));
                        filletEdgeArray = evaluateQuery(context, filletEdgeArray);
                        // debug(context, qParallelEdges(qAdjacent(qFilletFaces(qOwnedByBody(entity, EntityType.FACE), CompareType.EQUAL), AdjacencyType.EDGE, EntityType.EDGE), selectedCoordSys.zAxis), DebugColor.GREEN);

                        var step = 1;
                        for (var edge in filletEdgeArray)
                        {
                            try silent
                            {
                                opFillet(context, id + step + "fillet2", {
                                            "entities" : edge,
                                            "radius" : filletRadius
                                        });
                            }
                            step += 1;
                        }
                        resultingEdgesNum = evaluateQueryCount(context, qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis));
                        debug(context, qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis), DebugColor.RED);
                        println(startingEdgesNum);
                        println(resultingEdgesNum);

                        if (resultingEdgesNum != startingEdgesNum * 2)
                        {
                            if (deleteBadFillets)
                            {
                                opDeleteBodies(context, id + "deleteBadFilletBodies1", {
                                        "entities" : entity
                                    });
                            } else if (showBadFillets)
                            {
                                debug(context, entity, DebugColor.YELLOW);
                            }
                            
                        }
                        

                    }
                    else
                    {

                    }
                }


            }
            else if (!gridSameVolumeBoolean)
            {
                if (removeSmallGrids)
                {
                    opDeleteBodies(context, id + "deleteBodiesLoop", {
                                "entities" : entity
                            });
                }
            }


        });
}

function lowerFillet(context is Context, id is Id, inputs is map)
{
    var gridArray = inputs.gridArray;
    var selectedCoordSys = inputs.coordSys;
    var lowerFilletRadius = inputs.lowerFilletRadius == undefined ? 0 * inch : inputs.lowerFilletRadius;
    var lowerFilletPlane = inputs.lowerFilletPlane;
    var deleteBadFillets = inputs.deleteBadFillets;
    var showBadFillets = inputs.showBadFillets;

    forEachEntity(context, id + "filletLoop", qUnion(gridArray), function(entity is Query, id is Id)
        {
            var lowerFilletEdges = qCoincidesWithPlane(qOwnedByBody(entity, EntityType.EDGE), lowerFilletPlane);
            // debug(context, lowerFilletEdges, DebugColor.RED);
            var lowerFilletEdgeArray = evaluateQuery(context, lowerFilletEdges);
            // println(lowerFilletEdgeArray);
            var startingLowerEdgesNum = evaluateQueryCount(context, lowerFilletEdges);
            lowerFilletEdgeArray = makeRobustQueriesBatched(context, qUnion(lowerFilletEdgeArray));
            if (lowerFilletRadius > 0 * inch)
            {
                try silent
                {
                    opFillet(context, id + "lowerFillet1", {
                                "entities" : lowerFilletEdges,
                                "radius" : lowerFilletRadius
                            });

                }
                var lowerFilletCoordSys = coordSystem(lowerFilletPlane);
                var resultingEdgesNum = evaluateQueryCount(context, qCoincidesWithPlane(qOwnedByBody(entity, EntityType.EDGE), plane(lowerFilletCoordSys.origin + lowerFilletCoordSys.zAxis * lowerFilletRadius, lowerFilletCoordSys.zAxis)));
                // debug(context, qCoincidesWithPlane(qOwnedByBody(entity, EntityType.EDGE), plane(lowerFilletCoordSys.origin + lowerFilletCoordSys.zAxis * lowerFilletRadius,lowerFilletCoordSys.zAxis)), DebugColor.BLUE);
                // println(resultingEdgesNum);
                // println(startingEdgesNum - resultingEdgesNum);
                if (resultingEdgesNum != startingLowerEdgesNum || resultingEdgesNum == 3)
                {
                    // lowerFilletEdgeArray = qSubtraction(qCoincidesWithPlane(qOwnedByBody(entity, EntityType.EDGE), lowerFilletPlane),qCoincidesWithPlane(qAdjacent(qFilletFaces(qOwnedByBody(entity, EntityType.FACE), CompareType.EQUAL), AdjacencyType.EDGE, EntityType.EDGE), lowerFilletPlane));
                    // lowerFilletEdgeArray = evaluateQuery(context, lowerFilletEdgeArray);
                    // debug(context, qCoincidesWithPlane(qAdjacent(qFilletFaces(qOwnedByBody(entity, EntityType.FACE), CompareType.EQUAL), AdjacencyType.EDGE, EntityType.EDGE), lowerFilletPlane), DebugColor.BLUE);
                    if (deleteBadFillets)
                    {
                        opDeleteBodies(context, id + "deleteBadFilletBodies", {
                                    "entities" : entity
                                });
                    }
                    else
                    {
                        var step = 1;
                        for (var edge in lowerFilletEdgeArray)
                        {
                            try silent
                            {
                                opFillet(context, id + step + "lowerFillet2", {
                                            "entities" : edge,
                                            "radius" : lowerFilletRadius
                                        });
                            }
                            step += 1;
                        }
                    }
                    if (showBadFillets)
                    {
                        debug(context, entity, DebugColor.YELLOW);
                    }
                    
                }
                else
                {
                    // println(resultingEdgesNum);
                    // println(startingLowerEdgesNum - resultingEdgesNum);
                }
            }
        });
}


function deleteSmallHoles(context is Context, id is Id, inputs is map)
{
    var holeArray = inputs.holeArray;
    var selectedCoordSys = inputs.coordSys;
    var gridVolume = inputs.gridVolume;
    var filletRadius = inputs.filletRadius;
    var percentCutOff = inputs.percentCutOff;
    var removeSmallGrids = inputs.removeSmallGrids;
    var holeVolume = inputs.holeVolume;
    var deleteBadFillets = inputs.deleteBadFillets;
    var showBadFillets = inputs.showBadFillets;

    forEachEntity(context, id + "deleteHoles", qUnion(holeArray), function(entity is Query, id is Id)
        {
            var smallgridVolume = evVolume(context, {
                    "entities" : entity,
                    "accuracy" : VolumeAccuracy.HIGH
                });
            // println(smallgridVolume);

            var gridSameVolumeBoolean = roundToPrecision(smallgridVolume / (inch ^ 3), 8) == roundToPrecision(holeVolume / (inch ^ 3), 8);
            var gridVolumeCheck = gridSameVolumeBoolean ? false : smallgridVolume / gridVolume > percentCutOff / 100 || !removeSmallGrids;

            if (gridVolumeCheck)
            {
                var filletEdges = qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis);
                var startingEdgesNum = evaluateQueryCount(context, filletEdges);
                var filletEdgeArray = evaluateQuery(context, filletEdges);
                filletEdgeArray = makeRobustQueriesBatched(context, qUnion(filletEdgeArray));
                if (filletRadius > 0 * inch)
                {
                    try silent
                    {
                        opFillet(context, id + "fillet1", {
                                    "entities" : filletEdges,
                                    "radius" : filletRadius
                                });
                    }
                    var resultingEdgesNum = evaluateQueryCount(context, qParallelEdges(qOwnedByBody(entity, EntityType.EDGE), selectedCoordSys.zAxis));
                    // println(resultingEdgesNum);
                    // println(startingEdgesNum - resultingEdgesNum);
                    if (resultingEdgesNum == startingEdgesNum)
                    {
                        if (deleteBadFillets)
                        {
                            opDeleteBodies(context, id + "deleteBadFilletBodies2", {
                                        "entities" : entity
                                    });
                        }
                        else
                        {
                            var step = 1;
                            for (var edge in filletEdgeArray)
                            {
                                try silent
                                {
                                    opFillet(context, id + step + "fillet2", {
                                                "entities" : edge,
                                                "radius" : filletRadius
                                            });
                                }
                                step += 1;
                            }
                            
                            if (showBadFillets)
                    {
                        debug(context, entity, DebugColor.YELLOW);
                    }
                        }
                    }

                }
            }
            else if (!gridSameVolumeBoolean)
            {
                if (removeSmallGrids)
                {
                    opDeleteBodies(context, id + "deleteBodiesLoop", {
                                "entities" : entity
                            });
                }
            }
        });
}

function gridTransforms(context is Context, inputs is map)
{
    var pointArray = inputs.pointArray;
    var origin = inputs.origin;
    var transformGridArray = [];
    var transformGridNames = [];
    var pointStep = 0;
    for (var point in pointArray)
    {
        var translate = point - origin;
        var translateDistance = evDistance(context, {
                "side0" : origin,
                "side1" : point
            });
        if (translateDistance.distance != 0 * inch)
        {
            // println([transform(-yAxis(selectedCoordSys) * (gridHeight / 3 + definition.ribThickness / 2) * 6 * (1 )), transform(yAxis(selectedCoordSys) * (gridHeight / 3 + definition.ribThickness / 2) * 6 * (1))]);
            translate = { "linear" : diagonalMatrix([1, 1, 1]), "translation" : translate };
            // println(translate.translation);
            transformGridArray = append(transformGridArray, translate);
            transformGridNames = append(transformGridNames, "pointNext" ~ toString(pointStep));

        }
        pointStep += 1;
    }
    var transforms = { "gridArray" : transformGridArray, "gridNames" : transformGridNames };
    return transforms;
}

