FeatureScript 2345;
import(path : "onshape/std/common.fs", version : "2345.0");

export enum InputMethod
{
    annotation { "Name" : "Body entities" }
    ENTIRE_PART,
    annotation { "Name" : "All entities" }
    EVERYTHING
}

export enum BoxType
{
    annotation { "Name" : "Rectangular" }
    RECTANGULAR,
    annotation { "Name" : "Best fit" }
    BEST_FIT,
    annotation { "Name" : "Best fit smooth" }
    BEST_FIT_SPLINE
}

export enum Rotation
{
    annotation { "Name" : "Rotate all" }
    ROTATE_ALL,
    annotation { "Name" : "None" }
    NONE,
    annotation { "Name" : "Rotate around X" }
    ROTATE_X,
    annotation { "Name" : "Rotate around Y" }
    ROTATE_Y,
    annotation { "Name" : "Rotate around Z" }
    ROTATE_Z,
}

export enum Quality
{
    annotation { "Name" : "Fast" }
    FAST,
    annotation { "Name" : "High quality" }
    HIGH_QUALITY
}

export enum CalculateFrom
{
    annotation { "Name" : "First face" }
    FIRST_FACE,
    annotation { "Name" : "Mate connector" }
    MATE_CONNECTOR,
    annotation { "Name" : "World cSys" }
    WORLD_CSYS
}

export enum AXIS
{
    xAxis,
    yAxis,
    zAxis
}

/**
 * Returns an aligned bounding box or the measurements of that box.
 *
 * @param definition {{
 *      @field xyzOnly {boolean} : If true, returns only the xyz values.
 *      @field inputMethod {InputMethod} : Only a solid body, or any type of entity.
 *      @field entities {Query} : Entities to measure.
 *      @field calculateFrom {CalculateFrom} : The method used to calculate the bounding box.
 *      @field mate {Query} : The mate connector used as the reference coordinate system (if calculateFrom is MATE_CONNECTOR).
 *      @field rotation {Rotation} : The rotation type applied to the bounding box.
 *      @field quality {Quality} : The quality of the rotation (if rotation is ROTATE_ALL).
 *      @field tolerance {Length} : The tolerance for box change (if rotation is ROTATE_ALL).
 *      @field showMyWork {boolean} : Flag to indicate whether to show calculation boxes.
 *      @field offsets {boolean} : Flag to indicate whether offsets are applied.
 *      @field offset {Length} : The offset value (if offsets is true).
 *      @field intersectCurves {boolean} : Flag to indicate whether intersection curves are included.
 *      @field keepBox {boolean} : Flag to indicate whether to keep the box after calculation.
 *      @field showResultBox {boolean} : Shows the resulting bounding box.
 *      @field boxType {BoxType} : Choose if the box type is rectangular or best fit.
 * }}
 *
 * @return {{
 *      @field boxQuery {query} : Bounding box as a solid body.
 *      @field x {length} : x length of the box.
 *      @field y {length} : y length of the box.
 *      @field z {length} : z length of the box.
 * }}
 */
export function AlignedBoundingBoxFunction(context is Context, id is Id, definition is map)
{
    var toReturn = {};
    toReturn.boxQuery = qNothing();
    toReturn.x = undefined;
    toReturn.y = undefined;
    toReturn.z = undefined;

    var toColor = qNothing();
    var toDelete = qNothing();

    const isVertex = size(evaluateQuery(context, qEntityFilter(definition.entities, EntityType.VERTEX)));
    const isBody = size(evaluateQuery(context, qEntityFilter(definition.entities, EntityType.BODY)));
    const isFace = size(evaluateQuery(context, qEntityFilter(definition.entities, EntityType.FACE)));
    const isEdge = size(evaluateQuery(context, qEntityFilter(definition.entities, EntityType.EDGE)));
    const isSketch = size(evaluateQuery(context, qSketchFilter(definition.entities, SketchObject.YES)));

    const maxLoopCount = 10;
    const sizeEntities = size(evaluateQuery(context, definition.entities));

    // var defEntities = definition.entities;

    // for (var i = 0; i < size(evEntities); i += 1)
    // {
    if (definition.inputMethod == InputMethod.ENTIRE_PART)
    {
        try silent
        {
            definition.entities = qUnion([definition.entities, qOwnerBody(definition.entities)]);
        }
    }
    // }

    var evEntities = evaluateQuery(context, definition.entities);

    addDebugEntities(context, definition.entities, DebugColor.BLACK);

    var boxMap = {};
    var allBoxesArray = [];

    if (!(sizeEntities == 1 && isVertex == 1) && !(isVertex == 2 && sizeEntities == 2))
    {
        boxMap.csys = WORLD_COORD_SYSTEM;

        if (definition.calculateFrom == CalculateFrom.FIRST_FACE)
        {
            try silent
            {
                const facePlane = evFaceTangentPlane(context, {
                            "face" : evEntities[0],
                            "parameter" : vector(0.5, 0.5)
                        });

                boxMap.csys = coordSystem(facePlane);
            }

            if (sizeEntities == isSketch)
            {
                definition.rotation = Rotation.ROTATE_Z;
            }
        }
        else if (definition.calculateFrom == CalculateFrom.MATE_CONNECTOR)
        {
            boxMap.csys = evMateConnector(context, { "mateConnector" : definition.mate });
        }

        if (definition.rotation == Rotation.ROTATE_ALL)
        {
            var p = 1; // Remove this if using the loop below.


            // for (var p = 0; p < 1; p += p) // Creating an "Infinite" loop error
            // {
            // z x y
            boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.zAxis, maxLoopCount);
            // allBoxesArray = append(allBoxesArray, boxMap.allBoxes);
            boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.xAxis, maxLoopCount);
            boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.yAxis, maxLoopCount);
            // }

            if (definition.quality == Quality.HIGH_QUALITY)
            {
                reportFeatureInfo(context, id, "WARNING: High quality mode will impact regen time.");

                // x y z
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.xAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.yAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.zAxis, maxLoopCount);

                // y x z
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.yAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.xAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.zAxis, maxLoopCount);

                // z y x
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.zAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.xAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.yAxis, maxLoopCount);

                // x z y
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.xAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.zAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.yAxis, maxLoopCount);

                // y z x
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.yAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.zAxis, maxLoopCount);
                boxMap = smallestBox(context, id + p, definition, boxMap.csys, AXIS.xAxis, maxLoopCount);
            }
        }
        else if (definition.rotation == Rotation.ROTATE_X)
        {
            boxMap = smallestBox(context, id, definition, boxMap.csys, AXIS.xAxis, maxLoopCount);
        }
        else if (definition.rotation == Rotation.ROTATE_Y)
        {
            boxMap = smallestBox(context, id, definition, boxMap.csys, AXIS.yAxis, maxLoopCount);
        }
        else if (definition.rotation == Rotation.ROTATE_Z)
        {
            boxMap = smallestBox(context, id, definition, boxMap.csys, AXIS.zAxis, maxLoopCount);
        }
        else if (definition.rotation == Rotation.NONE)
        {
            // Loop only once to keep from rotating
            const singleLoopCount = 1;
            boxMap = smallestBox(context, id, definition, boxMap.csys, AXIS.zAxis, singleLoopCount);
        }

        const bboxSize = boxMap.bbox.maxCorner - boxMap.bbox.minCorner;

        if (definition.showResultBox)
            debug(context, boxMap.bbox, boxMap.csys, DebugColor.CYAN);
        // addDebugLine(context, toWorld(boxMap.csys, vector(0 * inch, 0 * inch, 0 * inch)), vector(bboxSize / 2, 0 * inch, 0 * inch), DebugColor.RED);

        toReturn.x = bboxSize[0];
        toReturn.y = bboxSize[1];
        toReturn.z = bboxSize[2];

        if (definition.xyzOnly)
            return toReturn;

        // Variables

        var vCount = 0;
        var loop = true;

        //Check if previous variable exists
        while (loop)
        {
            vCount += 1;

            try silent
            {
                getVariable(context, "box" ~ vCount ~ "_X");
            }
            catch
            {
                loop = false;
            }

            // Create variables
            setVariable(context, "box" ~ vCount ~ "_X", bboxSize[0]);
            setVariable(context, "box" ~ vCount ~ "_Y", bboxSize[1]);
            setVariable(context, "box" ~ vCount ~ "_Z", bboxSize[2]);
        }

        var surfaceCheck = 0;

        if (bboxSize[0] < 0.001 * inch)
            surfaceCheck += 1;

        if (bboxSize[1] < 0.001 * inch)
            surfaceCheck += 1;

        if (bboxSize[2] < 0.001 * inch)
            surfaceCheck += 1;

        const z = 0.01 * inch;

        if (surfaceCheck == 1) // 2d surface bbox
        {
            boxMap.bbox = extendBox3d(boxMap.bbox, z, 0);
        }

        if (definition.offsets)
        {
            boxMap.bbox = extendBox3d(boxMap.bbox, definition.offset, 0);
        }

        fCuboid(context, id + "cuboid1", {
                    "corner1" : boxMap.bbox.minCorner,
                    "corner2" : boxMap.bbox.maxCorner
                });

        const cube = qCreatedBy(id + "cuboid1", EntityType.BODY); //cuboid.query;
        toColor = cube;
        toReturn.boxQuery = cube;

        opTransform(context, id + "transform1", {
                    "bodies" : cube,
                    "transform" : transform(XY_PLANE, plane(boxMap.csys))
                });

        if (surfaceCheck == 1) // Surface needed
        {
            const cubeFaces = qCreatedBy(id + "cuboid1", EntityType.FACE);
            var faceToMove = evaluateQuery(context, qLargest(cubeFaces))[0];
            faceToMove = makeRobustQuery(context, faceToMove);

            var sideFaces = qSubtraction(cubeFaces, faceToMove);
            sideFaces = qSubtraction(sideFaces, qLargest(sideFaces));

            opOffsetFace(context, id + "offsetFaceCube", {
                        "moveFaces" : qUnion([faceToMove, sideFaces]),
                        "offsetDistance" : -z
                    });

            opExtractSurface(context, id + "extractFace", { "faces" : faceToMove });
            toColor = qCreatedBy(id + "extractFace", EntityType.BODY);
            toDelete = cube;
        }
        else
        {
            if (definition.offsets)
            {
                opOffsetFace(context, id + "offsetFace1", {
                            "moveFaces" : qCreatedBy(id + "cuboid1", EntityType.FACE),
                            "offsetDistance" : definition.offset
                        });
            }
        }

        if (!definition.keepBox)
        {
            toDelete = qUnion([toDelete, toColor]);
        }

        if (surfaceCheck < 2) // If surface or solid body
        {
            setProperty(context, {
                        "entities" : toColor,
                        "propertyType" : PropertyType.APPEARANCE,
                        "value" : color(.5, .8, 1, .3)
                    });

            setProperty(context, {
                        "entities" : toColor,
                        "propertyType" : PropertyType.NAME,
                        "value" : "Bounding box" ~ vCount
                    });
        }

        if (definition.intersectCurves)
        {
            if (surfaceCheck == 0)
            {
                try
                {
                    const tools = qAdjacent(toColor, AdjacencyType.VERTEX, EntityType.FACE);
                    const targets = qAdjacent(definition.entities, AdjacencyType.VERTEX, EntityType.FACE);

                    opIntersectFaces(context, id + "intersectFaces1", {
                                "tools" : tools,
                                "targets" : targets
                            });

                    const intersectedEdges = qCreatedBy(id + "intersectFaces1", EntityType.BODY);

                    addDebugEntities(context, intersectedEdges, DebugColor.RED);

                    opCreateCompositePart(context, id + "compositePart1", {
                                "bodies" : intersectedEdges,
                                "closed" : true
                            });

                    setProperty(context, {
                                "entities" : qCreatedBy(id + "compositePart1", EntityType.BODY),
                                "propertyType" : PropertyType.NAME,
                                "value" : "Box" ~ vCount ~ " intersection curves"
                            });
                }
            }
            else if (surfaceCheck == 1)
            {
                // Split surface in case more than one point contacts a side
                opPattern(context, id + "patternCopyFace", {
                            "entities" : toColor,
                            "transforms" : [transform(XY_PLANE, XY_PLANE)],
                            "instanceNames" : ["copy"]
                        });

                const copy = qCreatedBy(id + "patternCopyFace", EntityType.BODY);
                toDelete = qUnion([toDelete, copy]);

                const copyFace = qAdjacent(copy, AdjacencyType.VERTEX, EntityType.FACE);
                var faceEdges = qAdjacent(copyFace, AdjacencyType.VERTEX, EntityType.EDGE);

                opSplitEdges(context, id + "splitEdges1", {
                            "edges" : faceEdges,
                            "parameters" :
                            [[0, .1, .2, .3, .4, .5, .6, .7, .8, .9],
                                [0, .1, .2, .3, .4, .5, .6, .7, .8, .9],
                                [0, .1, .2, .3, .4, .5, .6, .7, .8, .9],
                                [0, .1, .2, .3, .4, .5, .6, .7, .8, .9]]
                        });

                const evEdges = evaluateQuery(context, faceEdges);

                for (var m = 0; m < size(evEdges); m += 1)
                {
                    const evD = evDistance(context, {
                                "side0" : evEdges[m],
                                "side1" : definition.entities
                            });

                    if (evD.distance < 0.000001 * inch)
                    {
                        opPoint(context, id + m + "pointIntersect", {
                                    "point" : evD.sides[0].point
                                });

                        if (definition.showResultBox)
                            debug(context, evD.sides[0].point, DebugColor.RED);
                    }
                }
            }
        }

        if (!isQueryEmpty(context, toDelete))
        {
            opDeleteBodies(context, id + "deleteBodiesB", {
                        "entities" : toDelete
                    });
        }
    }
    else // two points, create a line
    {
        const point1 = evVertexPoint(context, {
                    "vertex" : evEntities[0]
                });

        const point2 = evVertexPoint(context, {
                    "vertex" : evEntities[1]
                });

        opFitSpline(context, id + "fitSpline1", {
                    "points" : [
                        point1,
                        point2
                    ]
                });
    }

    return toReturn;
}


export function smallestBox(context, id, definition, csys, axis is AXIS, maxLoopCount is number)
{
    var toReturn = {};
    toReturn.csys;
    toReturn.bbox;
    toReturn.allBoxes = [];

    var smallest = vector(10000, 10000, 10000) * inch;
    var smallestCsys = csys;
    var lastDiff = 10000 * inch;
    var smallestBbox;

    var startAngle = 0 * degree;
    var endAngle = 180 * degree;
    var keepLooping = true;
    var count = 0;

    var previousAngles = [];

    while (keepLooping)
    {
        const loopSize = maxLoopCount;
        const angleSegment = (endAngle - startAngle) / loopSize;
        var newStartAngle;
        var newEndAngle;

        var thisSmallest = vector(10000, 10000, 10000) * inch;
        var thisSmallestCsys = csys;
        var thisLastDiff = 10000 * inch;
        var thisSmallestBbox;

        for (var i = 0; i < loopSize; i += 1)
        {
            const thisAngle = startAngle + (angleSegment * i);

            for (var k = 0; k < size(previousAngles); k += 1)
            {
                if (previousAngles[k] == thisAngle)
                {
                    continue;
                }
            }

            const thisBox = bboxRotation(context, id, definition, csys, axis, thisAngle);

            if (i > 0)
            {
                const comparedBoxes = compareBox(thisSmallestBbox, thisBox.bbox);

                if (comparedBoxes.differenceSum.value < 0)
                {
                    thisSmallest = thisBox.size;
                    thisSmallestCsys = thisBox.csys;
                    thisLastDiff = comparedBoxes.differenceSum;
                    thisSmallestBbox = thisBox.bbox;
                    newStartAngle = startAngle + (angleSegment * (i - 1));
                    newEndAngle = startAngle + (angleSegment * (i + 1));
                }
            }
            else
            {
                thisSmallest = thisBox.size;
                thisSmallestCsys = thisBox.csys;
                thisSmallestBbox = thisBox.bbox;
                newStartAngle = startAngle + (angleSegment * (i - 1));
                newEndAngle = startAngle + (angleSegment * (i + 1));
            }

            if (definition.showMyWork)
                debug(context, thisBox.bbox, thisBox.csys, DebugColor.RED);
                
            toReturn.allBoxes = append(toReturn.allBoxes, thisBox.bbox);
        }

        if (count > 0)
        {
            const comparedBoxes = compareBox(smallestBbox, thisSmallestBbox);

            if (comparedBoxes.differenceSum.value < 0 || count > 10)
            {
                smallest = thisSmallest;
                smallestCsys = thisSmallestCsys;
                smallestBbox = thisSmallestBbox;

                if (abs(comparedBoxes.differenceSum.value) <= definition.tolerance.value || count > 10)
                {
                    keepLooping = false;
                }
            }
        }
        else
        {
            smallest = thisSmallest;
            smallestCsys = thisSmallestCsys;
            smallestBbox = thisSmallestBbox;
        }

        // if (axis == AXIS.zAxis)
        // {
        //     if (smallest[0] == 0 * inch || smallest[1] == 0 * inch)
        //         keepLooping = false;
        // }
        // else if (axis == AXIS.xAxis)
        // {
        //     if (smallest[1] == 0 * inch || smallest[2] == 0 * inch)
        //         keepLooping = false;
        // }
        // else if (axis == AXIS.yAxis)
        // {
        //     if (smallest[0] == 0 * inch || smallest[2] == 0 * inch)
        //         keepLooping = false;
        // }

        startAngle = newStartAngle;
        endAngle = newEndAngle;

        count += 1;
    }

    toReturn.csys = smallestCsys;
    toReturn.bbox = smallestBbox;

    return toReturn;
}

/**
 * Compares two evBox.
 *
 * @param boxA {box} : A box created from evBox;
 * @param boxB {box} : A box created from evBox;
 *
 * @return {{
 *          @field differenceSum {ValueWithUnits} : Sum of the differences of x, y, and z.
 *          @field largerBox {Box} : The larger of the two boxes.
 *          @field smallerBox {Box} : The smaller of the two boxes.
 *
 * }}
 **/
export function compareBox(boxA, boxB)
{
    var toReturn = {};
    toReturn.differenceSum;
    toReturn.largerBox;
    toReturn.smallerBox;

    const boxSizeA = boxA.maxCorner - boxA.minCorner;
    const boxSizeB = boxB.maxCorner - boxB.minCorner;

    var a = {};
    a.x = boxSizeA[0];
    a.y = boxSizeA[1];
    a.z = boxSizeA[2];

    a.x = a.x != 0 * inch ? a.x : .01 * inch;
    a.y = a.y != 0 * inch ? a.y : .01 * inch;
    a.z = a.z != 0 * inch ? a.z : .01 * inch;

    var b = {};
    b.x = boxSizeB[0];
    b.y = boxSizeB[1];
    b.z = boxSizeB[2];

    b.x = b.x != 0 * inch ? b.x : .01 * inch;
    b.y = b.y != 0 * inch ? b.y : .01 * inch;
    b.z = b.z != 0 * inch ? b.z : .01 * inch;

    toReturn.differenceSum = (b.x + b.y + b.z) - (a.x + a.y + a.z);

    // Difference volume
    //(b.x * b.y * b.z) - (a.x * a.y * a.z);

    if (toReturn.differenceSum < 0)
    {
        toReturn.smallerBox = boxB;
        toReturn.largerBox = boxA;
    }
    else
    {
        toReturn.smallerBox = boxA;
        toReturn.largerBox = boxB;
    }

    return toReturn;
}

export function bboxRotation(context, id, definition, csys, axis is AXIS, degrees)
{
    var toReturn = {};
    toReturn.size = qNothing();
    toReturn.csys = csys;
    toReturn.bbox = qNothing();

    var rotationAxis;

    if (axis == AXIS.yAxis)
    {
        rotationAxis = yAxis(csys);
    }
    else
    {
        rotationAxis = csys[toString(axis)];
    }

    const localCsys = rotationAround(line(csys.origin, rotationAxis), degrees) * csys;

    const bbox = evBox3d(context, {
                "topology" : definition.entities,
                "cSys" : localCsys,
                "tight" : true
            });

    toReturn.size = bbox.maxCorner - bbox.minCorner;
    toReturn.csys = localCsys;
    toReturn.bbox = bbox;

    return toReturn;
}
