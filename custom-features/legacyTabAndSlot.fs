FeatureScript 1963;
import(path : "onshape/std/common.fs", version : "1963.0");
import(path : "onshape/std/moveFace.fs", version : "1963.0");
import(path : "onshape/std/extrude.fs", version : "1963.0");
import(path : "onshape/std/frameUtils.fs", version : "1963.0");
import(path : "onshape/std/chamfer.fs", version : "1963.0");
import(path : "onshape/std/fillet.fs", version : "1963.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "1963.0");
import(path : "onshape/std/sheetMetalCornerBreak.fs", version : "1963.0");
import(path : "onshape/std/sheetMetalTab.fs", version : "1963.0");
icon::import(path : "abebe1e7ae76b79e077d6a66/b58f0b9e95666fbf779940bd/512a5d550eea6d4335609aa5", version : "c624d21737599f02a059d781");
image::import(path : "abebe1e7ae76b79e077d6a66/b58f0b9e95666fbf779940bd/63f723ff87c91f232fd7d573", version : "9a4a7bdcb42b4b935567f235");


export enum TabBoundingType
{
    annotation { "Name" : "Blind" }
    BLIND,
    annotation { "Name" : "Up to Face" }
    UP_TO_FACE,
    annotation { "Name" : "Offset from Face" }
    OFFSET_FROM_FACE
}

export enum TabEdgeType
{
    annotation { "Name" : "None" }
    NONE,
    annotation { "Name" : "Fillet" }
    FILLET,
    annotation { "Name" : "Chamfer" }
    CHAMFER
}


annotation { "Feature Type Name" : "Tab and Slot", "Editing Logic Function" : "setDefaults", "Feature Type Description" : "Construct tabs and slots with Frame and Sheet Metal parts.", "Icon" : icon::BLOB_DATA, "Description Image" : image::BLOB_DATA }
export const tabAndSlot = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {


        annotation { "Group Name" : "Slot Parameters", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Slot Face", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
            definition.slotFace is Query;
            annotation { "Name" : "Slot offset" }
            isLength(definition.slotOffset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);
        }

        annotation { "Group Name" : "Tab End Condition", "Collapsed By Default" : false }
        {
            annotation { "Name" : "End Condition" }
            definition.limitType is TabBoundingType;

            if (definition.limitType == TabBoundingType.BLIND)
            {
                annotation { "Name" : "Tab Height" }
                isLength(definition.tabHeight, NONNEGATIVE_LENGTH_BOUNDS);
            }
            if (definition.limitType == TabBoundingType.UP_TO_FACE || definition.limitType == TabBoundingType.OFFSET_FROM_FACE)
            {
                annotation { "Name" : "Face", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
                definition.extrudeFace is Query;
            }
            if (definition.limitType == TabBoundingType.OFFSET_FROM_FACE)
            {
                annotation { "Name" : "Face Offset" }
                isLength(definition.faceOffset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

                annotation { "Name" : "Offset Direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.offsetDirection is boolean;

            }
        }
        annotation { "Group Name" : "Tab Corner Type", "Collapsed By Default" : false }
        {

            annotation { "Name" : "Tab Corner Type", "UIHint" : UIHint.SHOW_LABEL }
            definition.tabEdgeType is TabEdgeType;

            if (definition.tabEdgeType == TabEdgeType.FILLET)
            {
                annotation { "Name" : "Radius" }
                isLength(definition.radius, { (inch) : [0, 0.1, 10000] } as LengthBoundSpec);

            }

            if (definition.tabEdgeType == TabEdgeType.CHAMFER)
            {
                annotation { "Name" : "Side Length" }
                isLength(definition.side, { (inch) : [0, 0.1, 10000] } as LengthBoundSpec);

            }
        }



        annotation { "Name" : "Tab Edges", "Item name" : "Tab Edge", "Item label template" : "#tabEdge" }
        definition.tabEdges is array;
        for (var edge in definition.tabEdges)
        {
            annotation { "Name" : "Tab Edge", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
            edge.tabEdge is Query;

            annotation { "Name" : "Number of Tabs" }
            isInteger(edge.numTabs, { (unitless) : [1, 1, 1e5] } as IntegerBoundSpec);

            annotation { "Name" : "Tab Length" }
            isLength(edge.tabLength, NONNEGATIVE_LENGTH_BOUNDS);

            annotation { "Name" : "Equal Spacing", "Default" : true }
            edge.equalSpacing is boolean;

            if (!edge.equalSpacing)
            {
                annotation { "Name" : "Distance" }
                isLength(edge.tabDistance, NONNEGATIVE_LENGTH_BOUNDS);
            }

            annotation { "Name" : "Centered", "Default" : true }
            edge.centered is boolean;

            if (edge.centered == false)
            {
                annotation { "Name" : "Switch Starting Point" }
                edge.switchPoint is boolean;

                annotation { "Name" : "Start Offset" }
                isLength(edge.startOffset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

                if (edge.equalSpacing)
                {
                    annotation { "Name" : "End Offset" }
                    isLength(edge.endOffset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);
                }
            }
        }

    }
    {
        if (isQueryEmpty(context, definition.slotFace))
        {
            throw regenError("Slot Face selection required", ["slotFace"]);

        }


        var e = 0;
        //cycles through all edges defined in the array
        for (var edge in definition.tabEdges)
        {
            //A tab edge must be selected
            if (isQueryEmpty(context, edge.tabEdge))
            {
                throw regenError("Tab Edge selection required", ["tabEdges[" ~ e ~ "].tabEdge"]);

            }

            const units = inch;
            const tabEdge = edge.tabEdge;
            const numTabs = edge.numTabs;
            const adjacentFaces = qAdjacent(tabEdge, AdjacencyType.EDGE, EntityType.FACE);
            var tabPart = qOwnerBody(tabEdge);
            const slotOffset = definition.slotOffset;
            const faces = evaluateQuery(context, adjacentFaces);
            const edgeLine = evEdgeTangentLine(context, {
                        "edge" : tabEdge,
                        "parameter" : edge.switchPoint ? 1 : 0
                    });
            const tabLength = edge.tabLength;
            const tabHeight = definition.tabHeight;
            const edgeLength = evLength(context, {
                        "entities" : tabEdge
                    });
            const tabDistance = edge.equalSpacing ? 0 * inch : edge.tabDistance;
            var startOffset = edge.centered == false ? edge.startOffset : (numTabs == 1 ? edgeLength / 2 - (tabLength / 2) :
                    (edge.equalSpacing ? 0 * inch : (edgeLength - numTabs * tabLength - (numTabs - 1) * tabDistance) / 2));
            var endOffset = edge.centered == false ? (edge.equalSpacing ? edge.endOffset : (edgeLength - numTabs * tabLength - (numTabs - 1) * tabDistance - startOffset)) : (numTabs == 1 ? edgeLength / 2 - (tabLength / 2) :
                    (edge.equalSpacing ? 0 * inch : (edgeLength - numTabs * tabLength - (numTabs - 1) * tabDistance) / 2));

            const slotPart = qOwnerBody(definition.slotFace);
            const sketchXDir = edge.switchPoint ? -edgeLine.direction : edgeLine.direction;
            var csys;
            var yDim;
            var tabFace;
            var facePlane;
            //calculate the distance between tabs
            var patternLength = edge.equalSpacing ? edgeLength - startOffset - endOffset - (numTabs * tabLength) : tabDistance;
            var nextStartPoint = startOffset;
            var patternDist = numTabs == 1 ? patternLength : (edge.equalSpacing == true ? patternLength / (numTabs - 1) : patternLength);
            var sketchPlane;
            const offsetDirection = definition.offsetDirection == true ? -1 : 1;
            const selectedFacePlane = definition.limitType == TabBoundingType.UP_TO_FACE || definition.limitType == TabBoundingType.OFFSET_FROM_FACE ? evPlane(context, { "face" : definition.extrudeFace }) : undefined; //plane for offset from face



            if (isQueryEmpty(context, qCorrespondingInFlat(tabPart)) && isQueryEmpty(context, qFrameStartFace(tabPart)))
            {
                //only sheet metal or frames allowed
                throw regenError("The tab part must be on a Sheet Metal or Frame part.", edge.tabEdge);
            }


            if (roundToPrecision(patternLength / inch, 6) < 0)
            {
                throw regenError("Tabs are intersecting.", ["tabEdges[" ~ e ~ "].tabLength", "tabEdges[" ~ e ~ "].numTabs", "tabEdges[" ~ e ~ "].endOffset", "tabEdges[" ~ e ~ "].startOffset"]);
            }

            //actions for if it is sheet metal
            if (!isQueryEmpty(context, qCorrespondingInFlat(tabPart)))
            {
                // println("EDGE LENGTH: " ~ edgeLength/inch);
                // println("END OFFSET: " ~ endOffset/inch);
                // println("START OFFSET: " ~ startOffset/inch);
                // println("GAP LENGTHS: " ~ (numTabs - 1) * patternDist/inch );
                // println("TAB LENGTHS: " ~ numTabs * tabLength/inch);

                if (roundToPrecision((edgeLength - endOffset - startOffset - (numTabs - 1) * patternDist - numTabs * tabLength) / inch, 6) < 0 || roundToPrecision(startOffset / inch, 6) < 0)
                {
                    //prevents tabs from getting too large on sheet metal

                    throw regenError("Tab Length too long for edge or too many tabs.", ["tabEdges[" ~ e ~ "].tabLength", "tabEdges[" ~ e ~ "].numTabs"]);
                }



                const associations = getSMAssociationAttributes(context, tabEdge);
                tabFace = qIntersection(adjacentFaces, qAttributeQuery(associations[0]));
                const tabFacePlane = evPlane(context, {
                            "face" : tabFace
                        });

                var sketchFace = qSubtraction(adjacentFaces, tabFace); //grab the face that the tab will be coincident with for sketching
                facePlane = evPlane(context, {
                            "face" : sketchFace
                        });


                sketchPlane = plane(edgeLine.origin, cross(sketchXDir, tabFacePlane.normal), sketchXDir);
                var sketch = newSketchOnPlane(context, id + e + "sketch1", {
                        "sketchPlane" : sketchPlane
                    });

                for (var i = 0; i < numTabs; i += 1)
                {

                    const pointA = vector(nextStartPoint, 0 * inch);
                    const pointB = vector(nextStartPoint + tabLength, 0 * inch);

                    const segmentData = calculateSegmentData(definition, context, pointA, pointB, tabLength, sketchPlane, nextStartPoint);
                    const virtualSharp1 = segmentData[0];
                    const virtualSharp2 = segmentData[2];
                    const setback1 = segmentData[1];
                    const setback2 = segmentData[3];


                    const arcStart1 = vector(nextStartPoint, virtualSharp1[1] - setback1);
                    const center1 = arcStart1 + vector(definition.radius, 0 * meter);
                    const directionFromCenter1 = definition.tabEdgeType == TabEdgeType.NONE ? virtualSharp1 : normalize(virtualSharp1 - center1);
                    const arcMid1 = definition.tabEdgeType == TabEdgeType.NONE ? undefined : center1 + definition.radius * directionFromCenter1;
                    const arcEnd1 = virtualSharp1 - setback1 * normalize(virtualSharp1 / inch - virtualSharp2 / inch);

                    const arcStart2 = vector(nextStartPoint + tabLength, virtualSharp2[1] - setback2);

                    const center2 = arcStart2 + vector(-definition.radius, 0 * meter);

                    const directionFromCenter2 = definition.tabEdgeType == TabEdgeType.NONE ? virtualSharp2 : normalize(virtualSharp2 - center2);

                    const arcMid2 = definition.tabEdgeType == TabEdgeType.NONE ? undefined : center2 + definition.radius * directionFromCenter2;
                    const arcEnd2 = virtualSharp2 - setback2 * normalize(virtualSharp2 / inch - virtualSharp1 / inch);
                    

                    if ((definition.tabEdgeType == TabEdgeType.FILLET && definition.radius > virtualSharp1[1] && definition.radius > virtualSharp2[1]) || (definition.tabEdgeType == TabEdgeType.CHAMFER && definition.side > virtualSharp1[1] && definition.side > virtualSharp2[1]))
                    {
                        throw regenError("Fillet or chamfer incompatible with other inputs", ["radius", "side"]);
                    }

                    const smAttribute = getSheetMetalAttribute(context, tabPart);
                    if (smAttribute == undefined)
                        throw regenError("Should be unreachable!");

                    const thickness = smAttribute.frontThickness is undefined ? smAttribute.backThickness.value : (smAttribute.backThickness is undefined ? smAttribute.frontThickness.value : smAttribute.frontThickness.value + smAttribute.backThickness.value);



                    if (thickness > virtualSharp1[1] || thickness > virtualSharp2[1])
                    {
                        throw regenError("The tab height must be at least equivalent to the thickness of the sheet metal part to avoid errors.", ["tabHeight", "faceOffset", "extrudeFace"]);
                    }

                    skLineSegment(sketch, i ~ "line", {
                                "start" : vector(nextStartPoint, 0 * inch),
                                "end" : vector(nextStartPoint, virtualSharp1[1] - setback1)
                            });

                    if (definition.tabEdgeType == TabEdgeType.FILLET)
                    {
                        skArc(sketch, i ~ "arc", {
                                    "start" : arcStart1,
                                    "mid" : arcMid1,
                                    "end" : arcEnd1
                                });

                        skArc(sketch, i ~ "arc2", {
                                    "start" : arcStart2,
                                    "mid" : arcMid2,
                                    "end" : arcEnd2
                                });
                    }

                    if (definition.tabEdgeType == TabEdgeType.CHAMFER)
                    {
                        skLineSegment(sketch, i ~ "line5", {
                                    "start" : arcStart1,
                                    "end" : arcEnd1
                                });

                        skLineSegment(sketch, i ~ "line6", {
                                    "start" : arcStart2,
                                    "end" : arcEnd2
                                });
                    }

                    skLineSegment(sketch, i ~ "line2", {
                                "start" : arcEnd1,
                                "end" : arcEnd2
                            });
                    skLineSegment(sketch, i ~ "line3", {
                                "start" : vector(nextStartPoint + tabLength, 0 * inch),
                                "end" : vector(nextStartPoint + tabLength, virtualSharp2[1] - setback2)
                            });

                    skLineSegment(sketch, i ~ "line4", {
                                "start" : vector(nextStartPoint + tabLength, 0 * inch),
                                "end" : vector(nextStartPoint, 0 * inch)
                            });

                    nextStartPoint += tabLength + patternDist;

                }

                skSolve(sketch);
                const toExtrude = qSketchRegion(id + e + "sketch1");
                setAttribute(context, {
                            "entities" : tabPart,
                            "name" : "tabPart",
                            "attribute" : true
                        });

                sheetMetalTab(context, id + e + "tab", { "tabFaces" : toExtrude, "booleanUnionScope" : qSubtraction(adjacentFaces, tabFace), "booleanOffset" : definition.slotOffset, "booleanSubtractScope" : slotPart });


                const newFaces = qSubtraction(qCreatedBy(id + e + "tab", EntityType.FACE), qOwnedByBody(slotPart));

                if (definition.limitType != TabBoundingType.BLIND && isQueryEmpty(context, qParallelPlanes(newFaces, selectedFacePlane)))
                {
                    throw regenError("Cannot create tab with sheet metal that creates an inconsistent thickness.", ["extrudeFace"]);
                }



            }

            //actions for if it is a frame
            if (!isQueryEmpty(context, qFrameStartFace(tabPart)))
            {
                tabFace = qIntersection(adjacentFaces, qUnion([qFrameStartFace(tabPart), qFrameEndFace(tabPart)]));
                facePlane = evPlane(context, {
                            "face" : tabFace
                        });
                csys = coordSystem(edgeLine.origin, sketchXDir, facePlane.normal);

                const frameInfo = calculateFrameInfo(context, definition, e, csys, tabFace);



                const lengthDiff = frameInfo.lengthDiff;
                const maxDim = frameInfo.maxDim;
                yDim = frameInfo.yDim;
                startOffset = edge.centered && edge.equalSpacing == false ? startOffset : (edge.numTabs == 1 ? startOffset : (startOffset + (lengthDiff < 0 ? 0 * inch : lengthDiff / 2)));
                endOffset = edge.centered && edge.equalSpacing == false ? endOffset : (edge.equalSpacing == false && edge.centered == false ? 0 * inch : (edge.numTabs == 1 ? endOffset : (endOffset + (lengthDiff < 0 ? 0 * inch : lengthDiff / 2))));


                //need to update these with the new offsets
                var nextStartPoint = startOffset;
                var patternLength = edge.equalSpacing ? edgeLength - startOffset - endOffset - (numTabs * edge.tabLength) : tabDistance;
                var patternDist = numTabs == 1 ? 0 * inch : (edge.equalSpacing == true ? patternLength / (numTabs - 1) : patternLength);

                if (patternDist < 0)
                {
                    throw regenError("Tabs are intersecting.", ["tabEdges[" ~ e ~ "].tabLength", "tabEdges[" ~ e ~ "].numTabs", "tabEdges[" ~ e ~ "].endOffset", "tabEdges[" ~ e ~ "].startOffset"]);
                }

                var offsetChange = roundToPrecision(lengthDiff / inch, 6) < 0 ? 0 * inch : lengthDiff;


                // println("EDGE LENGTH: " ~ edgeLength/inch);
                // println("END OFFSET: " ~ endOffset/inch);
                // println("START OFFSET: " ~ startOffset/inch);
                // println("GAP LENGTHS: " ~ (numTabs - 1) * patternDist/inch );
                // println("TAB LENGTHS: " ~ numTabs * tabLength/inch);
                // println("OFFSET: " ~ offsetChange/inch);

                if (roundToPrecision((edgeLength - (endOffset == 0 * inch ? offsetChange / 2 : endOffset) - startOffset - (numTabs - 1) * patternDist - numTabs * tabLength) / inch, 6) < 0 || roundToPrecision(startOffset / inch, 6) < roundToPrecision(offsetChange / 2 / inch, 6))
                {
                    //prevents tabs from getting too large on frames

                    throw regenError("Tab Length too long for edge or too many tabs.", ["tabEdges[" ~ e ~ "].tabLength", "tabEdges[" ~ e ~ "].numTabs"]);
                }



                sketchPlane = plane(edgeLine.origin, facePlane.normal, sketchXDir);

                var sketch = newSketchOnPlane(context, id + e + "sketch1", {
                        "sketchPlane" : sketchPlane
                    });

                for (var i = 0; i < numTabs; i += 1)
                {
                    skRectangle(sketch, "rectangle" ~ i, {
                                "firstCorner" : vector(nextStartPoint, 0 * inch),
                                "secondCorner" : vector(nextStartPoint + tabLength, yDim)
                            });

                    nextStartPoint += tabLength + patternDist;

                }

                skSolve(sketch);

                const angle = definition.limitType == TabBoundingType.BLIND ? 0 : roundToPrecision(angleBetween(selectedFacePlane.normal, sketchPlane.normal) / radian, 6);

                const offsetDistance = angle == 0 ? definition.faceOffset : definition.faceOffset / cos(angle * radian);



                const toExtrude = qSketchRegion(id + e + "sketch1");

                try silent
                {
                    extrude(context, id + e + "extrude1", {
                                "entities" : toExtrude,
                                "endBound" : definition.limitType == TabBoundingType.BLIND ? BoundingType.BLIND : BoundingType.UP_TO_SURFACE,
                                "endBoundEntityFace" : definition.extrudeFace,
                                "depth" : tabHeight,
                                "hasOffset" : definition.limitType == TabBoundingType.OFFSET_FROM_FACE ? true : false,
                                "offsetOppositeDirection" : definition.offsetDirection == true ? false : true,
                                "offsetDistance" : offsetDistance
                            });
                }
                catch
                {
                    throw regenError("Tab does not intersect face", ["extrudeFace"]);
                }


                opBoolean(context, id + e + "boolean1", {
                            "tools" : qUnion(tabPart, qCreatedBy(id + e + "extrude1", EntityType.BODY)),
                            "operationType" : BooleanOperationType.UNION
                        });

                tabPart = qOwnerBody(qCreatedBy(id + e + "boolean1"));

                //plane the edges for chamfer or fillet should be on
                var endPlane = definition.limitType == TabBoundingType.UP_TO_FACE ? selectedFacePlane :
                (definition.limitType == TabBoundingType.OFFSET_FROM_FACE ? plane(selectedFacePlane.origin + (definition.faceOffset * offsetDirection * selectedFacePlane.normal), selectedFacePlane.normal, selectedFacePlane.x) : plane(sketchPlane.origin + tabHeight * sketchPlane.normal, sketchPlane.normal, sketchPlane.x));
                booleanBodies(context, id + e + "boolean", {
                            "operationType" : BooleanOperationType.SUBTRACTION,
                            "tools" : tabPart,
                            "targets" : slotPart,
                            "keepTools" : true });

                const moveFaces = qCreatedBy(id + e + "boolean", EntityType.FACE);


                if (isQueryEmpty(context, moveFaces))
                {
                    throw regenError("Check inputs. Tab(s) do not intersect slot face.");
                }

                moveFace(context, id + e + "moveFace", {
                            "outputType" : MoveFaceOutputType.MOVE,
                            "moveFaces" : moveFaces,
                            "moveFaceType" : MoveFaceType.OFFSET,
                            "limitType" : MoveFaceBoundingType.BLIND,
                            "offsetDistance" : slotOffset,
                            "oppositeDirection" : true,
                            "oppositeOffsetDirection" : true,
                        });


                //find edges for fillet or chamfer

                var edgesOnEndPlane = evaluateQuery(context, qAdjacent(qIntersectsPlane(qParallelPlanes(qOwnedByBody(tabPart, EntityType.FACE), endPlane), endPlane), AdjacencyType.EDGE, EntityType.EDGE));

                var edgeLength;
                if (definition.limitType != TabBoundingType.BLIND)
                {
                    const intersectionA = intersection(selectedFacePlane, line(planeToWorld(sketchPlane, vector(0, 0) * inch), sketchPlane.normal));
                    const intersectionB = intersection(selectedFacePlane, line(planeToWorld(sketchPlane, vector(0 * inch, yDim)), sketchPlane.normal));


                    edgeLength = evDistance(context, {
                                        "side0" : intersectionA.intersection,
                                        "side1" : intersectionB.intersection
                                    }).distance / inch;
                }
                else
                {
                    edgeLength = yDim / inch;
                }

                var brokenEdges = [];

                for (var edge in edgesOnEndPlane)
                {

                    var length = evLength(context, {
                            "entities" : edge
                        });

                    if (abs(roundToPrecision(length / inch, 6)) == abs(roundToPrecision(edgeLength, 6)))
                    {
                        brokenEdges = append(brokenEdges, edge);

                    }
                }


                brokenEdges = qUnion(brokenEdges);


                try
                {
                    if (definition.tabEdgeType == TabEdgeType.FILLET)
                    {
                        fillet(context, id + e + "fillet", {
                                    "filletType" : FilletType.EDGE,
                                    "entities" : brokenEdges,
                                    "radius" : definition.radius
                                });
                    }

                    //complete fillet or chamfer

                    if (definition.tabEdgeType == TabEdgeType.CHAMFER)
                    {
                        chamfer(context, id + e + "chamfer", {
                                    "entities" : brokenEdges,
                                    "chamferType" : ChamferType.EQUAL_OFFSETS,
                                    "width" : definition.side
                                });
                    }
                }
                catch
                {
                    throw regenError("Fillet or chamfer incompatible with other inputs", ["radius", "side"]);
                }



            }

            //delete sketches
            const originalSketches = qSketchFilter(qCreatedBy(id, EntityType.BODY), SketchObject.YES);
            opDeleteBodies(context, id + e + "deleteBodies1", {
                        "entities" : originalSketches
                    });

            e += 1;

        }


    });

//calcs for sheetmetal segments
export function calculateSegmentData(definition is map, context is Context, pointA is Vector, pointB is Vector, tabLength is ValueWithUnits, sketchPlane is Plane, nextStartPoint is ValueWithUnits)
{

    var virtualSharp1;
    var virtualSharp2;

    var setback1 = definition.tabEdgeType == TabEdgeType.FILLET ? definition.radius : (definition.tabEdgeType == TabEdgeType.CHAMFER ? definition.side : 0 * inch);
    var setback2 = definition.tabEdgeType == TabEdgeType.FILLET ? definition.radius : (definition.tabEdgeType == TabEdgeType.CHAMFER ? definition.side : 0 * inch);

    if (definition.limitType == TabBoundingType.UP_TO_FACE || definition.limitType == TabBoundingType.OFFSET_FROM_FACE)
    {
        const selectedFacePlane = evPlane(context, { "face" : definition.extrudeFace });
        const intersectionA = intersection(selectedFacePlane, line(planeToWorld(sketchPlane, pointA), yAxis(sketchPlane)));
        const intersectionB = intersection(selectedFacePlane, line(planeToWorld(sketchPlane, pointB), yAxis(sketchPlane)));

        //Need to make sure the parallel offset is exactly what was entered
        const angle = roundToPrecision(angleBetween(selectedFacePlane.normal, yAxis(sketchPlane)) / radian, 6);
        const offset = definition.limitType == TabBoundingType.OFFSET_FROM_FACE ? (angle == 0 ? definition.faceOffset : definition.faceOffset / cos(angle * radian)) : 0 * inch;

        const offsetDir = definition.offsetDirection == true ? -1 : 1;
        try silent
        {
            virtualSharp1 = worldToPlane(sketchPlane, intersectionA.intersection) + vector(0 * inch, offset * offsetDir);
            virtualSharp2 = worldToPlane(sketchPlane, intersectionB.intersection) + vector(0 * inch, offset * offsetDir);
        }
        catch
        {
            throw regenError("Tab does not intersect face", ["extrudeFace"]);
        }

        const direction1 = vector(0 * inch, virtualSharp1[1], 0 * inch) / inch;
        const direction2 = append((virtualSharp1 - virtualSharp2) / inch, 0);


        // the angle is acos((xa⋅xb+ya⋅yb)/((xa^2+ya^2)^(1/2)⋅(xb^2+yb^2)^(1/2)))

        var magA = (direction1[0] ^ 2 + direction1[1] ^ 2) ^ (1 / 2);
        var magB = (direction2[0] ^ 2 + direction2[1] ^ 2) ^ (1 / 2);

        const angle1 = acos(dot(direction1, direction2) / (magA * magB));


        const angle2 = 180 * degree - angle1;
        if (definition.tabEdgeType == TabEdgeType.FILLET)
        {
            setback1 = definition.radius / tan(angle1 / 2);
            setback2 = definition.radius / tan(angle2 / 2);
        }



    }
    else
    {
        virtualSharp1 = vector(nextStartPoint, definition.tabHeight);
        virtualSharp2 = vector(nextStartPoint + tabLength, definition.tabHeight);

    }

    return [virtualSharp1, setback1, virtualSharp2, setback2];
}

//calcs for sheetmetal segments
export function calculateFrameInfo(context is Context, definition, e is number, csys is CoordSystem, tabFace is Query)
{
    const tabEdge = definition.tabEdges[e].tabEdge;
    const numTabs = definition.tabEdges[e].numTabs;
    const tabDistance = definition.tabEdges[e].equalSpacing ? 0 * inch : definition.tabEdges[e].tabDistance;

    //grab all parallel edges to the selected edge
    var allEdges = evaluateQuery(context, qParallelEdges(qAdjacent(tabFace, AdjacencyType.EDGE, EntityType.EDGE), csys.xAxis));
    var edgeCount = size(allEdges);
    var edgeLines = [];

    //evaluate each edge to a line
    for (var edge in allEdges)
    {
        edgeLines = append(edgeLines, { "line" : evLine(context, {
                            "edge" : edge
                        }), "length" : evLength(context, {
                            "entities" : edge
                        }) });
    }

    //translate the origin of each line to the planned sketch coordinate system
    for (var i = 0; i < edgeCount; i += 1)
    {
        edgeLines[i].line.origin = fromWorld(csys, edgeLines[i].line.origin);
    }
    //sort the lines by their y coordinate
    edgeLines = sort(edgeLines, function(a, b)
        {
            return abs(a.line.origin[1]) - abs(b.line.origin[1]);
        });

    const edgeLength = evLength(context, {
                "entities" : definition.tabEdges[e].tabEdge
            });
    const length1 = roundToPrecision(edgeLines[0].length / inch, 6) * inch;
    const length2 = roundToPrecision(edgeLines[1].length / inch, 6) * inch;
    const lengthDiff = length1 - length2;

    const maxDim = length1 >= length2 ? length2 : length1;
    //find the thickness for the tab
    const yDim = roundToPrecision(edgeLines[1].line.origin[1] / inch, 6) * inch;
    return { "maxDim" : maxDim, "lengthDiff" : lengthDiff, "yDim" : yDim };

}

export function setDefaults(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean) returns map
{
    //This sets the tab length to the size of the edge if the tab length is at its default value and they just selected a new edge
    var numEdges = size(definition.tabEdges);
    var oldNumEdges;
    try silent
    {
        oldNumEdges = size(oldDefinition.tabEdges);
    }

    if (numEdges == oldNumEdges)
    {
        for (var i = 0; i < numEdges; i += 1)
        {

            var edge = definition.tabEdges[i];
            var oldEdge = oldDefinition.tabEdges[i];
            var tabEdge = edge.tabEdge;

            if (!isQueryEmpty(context, tabEdge) && isQueryEmpty(context, oldEdge.tabEdge) && edge.tabLength == 1 * inch)
            {
                if (!isQueryEmpty(context, qFrameStartFace(qOwnerBody(tabEdge))))
                {
                    const adjacentFaces = qAdjacent(tabEdge, AdjacencyType.EDGE, EntityType.FACE);
                    const tabPart = qOwnerBody(edge.tabEdge);
                    const tabFace = qIntersection(adjacentFaces, qUnion([qFrameStartFace(tabPart), qFrameEndFace(tabPart)]));
                    const edgeLine = evEdgeTangentLine(context, {
                                "edge" : tabEdge,
                                "parameter" : 0
                            });
                    const sketchFace = qSubtraction(adjacentFaces, tabFace); //grab the face that the tab will be coincident with for sketching
                    const facePlane = evPlane(context, {
                                "face" : sketchFace
                            });
                    const csys = coordSystem(edgeLine.origin, edgeLine.direction, facePlane.normal);

                    const frameInfo = calculateFrameInfo(context, definition, i, csys, tabFace);
                    definition.tabEdges[i].tabLength = frameInfo.maxDim;
                }
                else
                {
                    definition.tabEdges[i].tabLength = evLength(context, { "entities" : edge.tabEdge });
                }

            }


        }
    }
    return definition;
}

// Get the sheet metal attribute of the part
function getSheetMetalAttribute(context is Context, partQuery is Query)
{
    const attributes = context->getSmObjectTypeAttributes(
        context->getSMDefinitionEntitiesOutsideSheetMetalFeature(partQuery, undefined)->qUnion()->qEntityFilter(EntityType.BODY),
        SMObjectType.MODEL
        );

    if (size(attributes) != 1)
        return undefined;

    return attributes[0];
}

// This is copied from sheetMetalAttribute.fs and edited to always include inactive models.
// If entityType is undefined, disregard it.
function getSMDefinitionEntitiesOutsideSheetMetalFeature(context is Context, selection is Query, entityType) returns array
{
    const entityAssociations = try silent(getSMAssociationAttributes(context, qBodyType(selection, BodyType.SOLID)));
    if (entityAssociations == undefined)
        return [];

    const allSheets = qAttributeQuery(asSMAttribute({ "objectType" : SMObjectType.MODEL }));
    const allSMDefinitionEntitiesOfType = (entityType != undefined) ? qOwnedByBody(allSheets, entityType) : qOwnedByBody(allSheets);

    var out = [];
    for (var attribute in entityAssociations)
        out = append(out, evaluateQuery(context, qIntersection([qAttributeQuery(attribute), allSMDefinitionEntitiesOfType])));

    return concatenateArrays(out);
}

