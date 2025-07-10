FeatureScript 701;
import(path : "onshape/std/geometry.fs", version : "701.0");
icon::import(path : "8190f12a75ccfe4b4dbc21b9", version : "99f86c3c458b9f9bf7e8d55f");

annotation { "Feature Type Name" : "Sculpt Face", "Manipulator Change Function" : "surfaceManipulatorChange", "Icon" : icon::BLOB_DATA  }
export const sculptFace = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sculpt Method", "UIHint" : "HORIZONTAL_ENUM" }
        definition.sculptType is SculptType;

        annotation { "Name" : "Sculpt by Reference" }
        definition.getRef is boolean;

        if (definition.sculptType == SculptType.EDGE_STATIC)
        {

            if (definition.getRef)
            {
                annotation { "Name" : "Reference Sketch Points", "Filter" : EntityType.VERTEX }
                definition.refSketchPoints is Query;
            }

        }

        if (definition.sculptType == SculptType.EDGE_DYNAMIC)
        {
            if (definition.getRef)
            {
                annotation { "Name" : "U Sketch Splines", "Filter" : EntityType.EDGE }
                definition.refSketchUSplines is Query;

                annotation { "Name" : "V Sketch Splines", "Filter" : EntityType.EDGE }
                definition.refSketchVSplines is Query;
            }
        }

        annotation { "Name" : "Build Sculpt", "Default" : true }
        definition.buildSculpt is boolean;

        annotation { "Name" : "Face to Sculpt",
                    "UIHint" : "SHOW_CREATE_SELECTION",
                    "Filter" : (EntityType.FACE) && ConstructionObject.NO && SketchObject.NO && ModifiableEntityOnly.YES,
                    "MaxNumberOfPicks" : 1 }
        definition.sculptFace is Query;


        if (!definition.getRef)
        {
            annotation { "Name" : "U Count" }
            isInteger(definition.uCount, POSITIVE_COUNT_BOUNDS);

            annotation { "Name" : "V Count" }
            isInteger(definition.vCount, POSITIVE_COUNT_BOUNDS);
        }



        //Hidden paramater used to store the manipulator's depth values.
        annotation { "Name" : "DepthMap", "UIHint" : "ALWAYS_HIDDEN", "Default" : "{}" }
        isAnything(definition.depthMap);

    }
    {
        //If the depthMap is 0, then initialize it as a map.
        //The depthMap is used to store all of the manipulators offset values. The reason a
        //map is used is because it is easily expandable. We will simply create an arbitrary number
        //of key-value pairs based on how many manipulators we want.
        if (definition.depthMap == 0)
        {
            definition.depthMap = {};
        }

        var uCount = definition.uCount;
        var vCount = definition.vCount;

        var tanPlanesMatrix = [];
        var parameterVectors = [];
        var tanPlanes = [];

        if (definition.sculptType == SculptType.EDGE_DYNAMIC)
        {
            if (!definition.getRef)
            {
                //An array of vectors that determine the planes extracted from the surface using the vector
                //values as paramaters for the surface paramatarization.
                parameterVectors = initializeFlatMatrix(uCount, vCount, 0);

                //Tangent planes created using paramater vectors.
                tanPlanes = evFaceTangentPlanes(context, {
                            "face" : definition.sculptFace,
                            "parameters" : parameterVectors
                        });

                tanPlanesMatrix = convertToMatrix(tanPlanes, vCount, uCount);
            }
            else
            {
                var uSplinesRef = evaluateQuery(context, definition.refSketchUSplines);
                var vSplinesRef = evaluateQuery(context, definition.refSketchVSplines);
                uCount = size(uSplinesRef);
                vCount = size(vSplinesRef);

                var uSplines = getSplinesFromRef(context, id + "getSplines1", uSplinesRef, vSplinesRef, definition.sculptFace);
                var vSplines = getSplinesFromRef(context, id + "getSplines2", vSplinesRef, uSplinesRef, definition.sculptFace);

                var skPlane = evOwnerSketchPlane(context, {
                        "entity" : uSplinesRef[0]
                    });
                var uSplines2d = getSketchVectors(skPlane, uSplines);
                var vSplines2d = getSketchVectors(skPlane, vSplines);

                var splineExtrudeSketch = newSketchOnPlane(context, id + "splineExtrudeSketch", {
                        "sketchPlane" : skPlane
                    });

                skFitSpline(splineExtrudeSketch, "spline1", {
                            "points" : uSplines2d[0]
                        });
                skFitSpline(splineExtrudeSketch, "spline2", {
                            "points" : uSplines2d[size(uSplines2d) - 1]
                        });
                skFitSpline(splineExtrudeSketch, "spline3", {
                            "points" : vSplines2d[0]
                        });

                skFitSpline(splineExtrudeSketch, "spline4", {
                            "points" : vSplines2d[size(vSplines2d) - 1]
                        });

                skSolve(splineExtrudeSketch);
                opExtrude(context, id + "extrude1", {
                            "entities" : qSketchRegion(id + "splineExtrudeSketch"),
                            "direction" : skPlane.normal,
                            "endBound" : BoundingType.BLIND,
                            "endDepth" : 1 * inch
                        });
                opDeleteBodies(context, id + "deleteBodiesSketch", {
                            "entities" : qUnion([qCreatedBy(id + "splineExtrudeSketch", EntityType.EDGE),
                                        qCreatedBy(id + "splineExtrudeSketch", EntityType.VERTEX)])
                        });
                try
                {
                    opReplaceFace(context, id + "replaceFace_capReplace1", {
                                "replaceFaces" : qEntityFilter(qCapEntity(id + "extrude1", false), EntityType.FACE),
                                "templateFace" : qEntityFilter(definition.sculptFace, EntityType.FACE),
                                "oppositeSense" : false
                            });
                }
                catch
                {
                    opReplaceFace(context, id + "replaceFace_capReplace2", {
                                "replaceFaces" : qEntityFilter(qCapEntity(id + "extrude1", false), EntityType.FACE),
                                "templateFace" : qEntityFilter(definition.sculptFace, EntityType.FACE),
                                "oppositeSense" : true
                            });
                }

                var extrudeCaps = qUnion([qCapEntity(id + "extrude1", true), qCapEntity(id + "extrude1", false)]);
                var sides = qSubtraction(qCreatedBy(id + "extrude1", EntityType.FACE), extrudeCaps);

                opOffsetFace(context, id + "offsetFace1", {
                            "moveFaces" : sides,
                            "offsetDistance" : 0.01 * inch
                        });
                for (var i = 0; i < size(vSplines); i += 1)
                {
                    var vSplineProj = [];
                    for (var j = 0; j < size(vSplines[0]); j += 1)
                    {
                        var pointLine = line(vSplines[i][j], skPlane.normal);
                        var pointCol = getRayIntersection(context, id + ("vSplines" ~ i ~ "_" ~ j), pointLine);
                        tanPlanes = append(tanPlanes, plane(pointCol, skPlane.normal, skPlane.x));
                        vSplineProj = append(vSplineProj, pointCol);
                    }
                }
                opDeleteBodies(context, id + "deleteBodiesExtrude", {
                            "entities" : qCreatedBy(id + "extrude1", EntityType.BODY)
                        });
                tanPlanesMatrix = convertToMatrix(tanPlanes, uCount, vCount);
            }
        }
        if (definition.sculptType == SculptType.EDGE_STATIC)
        {
            if (!definition.getRef)
            {
                parameterVectors = initializeFlatMatrix(uCount, vCount, .5);

                //Tangent planes created using paramater vectors.
                tanPlanes = evFaceTangentPlanes(context, {
                            "face" : definition.sculptFace,
                            "parameters" : parameterVectors
                        });
                var origPoints = [];
                for (var i = 0; i < size(tanPlanes); i += 1)
                {

                    if (evaluateQuery(context, qContainsPoint(definition.sculptFace, tanPlanes[i].origin)) != [])
                    {
                        origPoints = append(origPoints, tanPlanes[i]);
                    }

                }

                tanPlanesMatrix = convertToMatrix(origPoints, size(origPoints), 1);

            }
            else
            {
                var origVertices = evaluateQuery(context, definition.refSketchPoints);
                var origPoints = zeroVector(size(origVertices));

                for (var i = 0; i < size(origPoints); i += 1)
                {

                    origPoints[i] = evVertexPoint(context, {
                                "vertex" : origVertices[i]
                            });
                    var skPlane = evOwnerSketchPlane(context, { "entity" : origVertices[i] });
                    var pointLine = line(origPoints[i], skPlane.normal);

                    var pointCol = getRayIntersection(context, id + i, pointLine);

                    origPoints[i] = plane(pointCol, skPlane.normal, skPlane.x);
                }
                tanPlanesMatrix = convertToMatrix(origPoints, size(origPoints), 1);
            }
        }

        //Modify tanPlanesMatrix based on the manipulators
        tanPlanesMatrix = manipulatePlanes(context, id, tanPlanesMatrix, definition.depthMap);

        tanPlanes = flattenMatrix(tanPlanesMatrix);
        var splines = [];
        if (definition.sculptType == SculptType.EDGE_DYNAMIC)
        {

            splines = createSplines(context, id, tanPlanes, uCount, vCount);
        }

        if (definition.buildSculpt)
        {
            var newSurface = [];
            if (definition.sculptType == SculptType.EDGE_DYNAMIC)
            {
                newSurface = makeNewSurface(context, id, splines.uSplines, splines.vSplines);
            }
            if (definition.sculptType == SculptType.EDGE_STATIC)
            {
                newSurface = makeNewSurfaceFill(context, id, tanPlanes, definition.sculptFace);
            }

            //Try both directions of normals for the surface to replace the face of the solid.
            try silent
            {
                opReplaceFace(context, id + "replaceFace1", {
                            "replaceFaces" : definition.sculptFace,
                            "templateFace" : newSurface,
                            "oppositeSense" : true
                        });
            }
            catch
            {
                opReplaceFace(context, id + "replaceFace2", {
                            "replaceFaces" : definition.sculptFace,
                            "templateFace" : newSurface,
                            "oppositeSense" : false
                        });
            }
            // return false;
            //Delete the tool surface.
            opDeleteBodies(context, id + "deleteBodiesFinal", {
                        "entities" : newSurface
                    });
        }
    }, { buildSculpt : true });



export function getSketchVectors(sketchPlane, splineArray)
{
    var res = [];
    for (var i = 0; i < size(splineArray); i += 1)
    {
        var spline = [];
        for (var j = 0; j < size(splineArray[i]); j += 1)
        {
            spline = append(spline, worldToPlane(sketchPlane, splineArray[i][j]));
        }

        res = append(res, spline);
    }
    return res;
}


export function getSplinesFromRef(context is Context, id is Id, uSplines is array, vSplines is array, Face is Query)
{
    var everything = evaluateQuery(context, qEverything(EntityType.BODY));
    var res = [];
    var originPlane = evOwnerSketchPlane(context, { "entity" : uSplines[0] });
    for (var i = 0; i < size(uSplines); i += 1)
    {
        opExtrude(context, id + ("extrudeU_" ~ i), {
                    "entities" : uSplines[i],
                    "direction" : originPlane.normal,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : 1 * inch
                });
    }

    for (var i = 0; i < size(vSplines); i += 1)
    {
        opExtrude(context, id + ("extrudeV_" ~ i), {
                    "entities" : vSplines[i],
                    "direction" : originPlane.normal,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : 1 * inch
                });
    }



    for (var i = 0; i < size(uSplines); i += 1)
    {
        var splinePoints = [];

        //Find the "origin" of the uSpline.
        var endPoint1 = evEdgeTangentLine(context, {
                    "edge" : uSplines[i],
                    "parameter" : 0
                }).origin;
        var endPoint2 = evEdgeTangentLine(context, {
                    "edge" : uSplines[i],
                    "parameter" : 1
                }).origin;

        var firstPoint = endPoint1;
        var lastPoint = endPoint2;
        var rev = false;
        //If the first vSpline is closest to the first origin spline, then we're good. Else we should
        //reverse the vSpline list.
        if (evaluateQuery(context, qClosestTo(qUnion(vSplines), endPoint1))[0] != vSplines[0])
        {
            firstPoint = endPoint2;
            lastPoint = endPoint1;

        }

        splinePoints = append(splinePoints, firstPoint);
        if (size(vSplines) > 2)
        {


            for (var j = 1; j < size(vSplines) - 1; j += 1)
            {
                opSplitPart(context, id + ("splitPart_" ~ i ~ "_" ~ j), {
                            "targets" : qCreatedBy(id + ("extrudeU_" ~ i), EntityType.BODY),
                            "tool" : qCreatedBy(id + ("extrudeV_" ~ j), EntityType.BODY),
                            "keepTools" : true
                        });
                var createdVertices = qCreatedBy(id + ("splitPart_" ~ i ~ "_" ~ j), EntityType.VERTEX);
                var resultingVertices = qClosestTo(createdVertices, originPlane.origin);
                var newSplinePoint = evVertexPoint(context, {
                        "vertex" : qClosestTo(resultingVertices, originPlane.origin)
                    });
                splinePoints = append(splinePoints, newSplinePoint);

            }
        }
        splinePoints = append(splinePoints, lastPoint);
        if (rev)
        {
            splinePoints = reverse(splinePoints);
        }
        res = append(res, splinePoints);
    }
    var newEverything = qEverything(EntityType.BODY);
    opDeleteBodies(context, id + "deleteBodies1", {
                "entities" : qSubtraction(newEverything, qUnion(everything))
            });
    return res;
}


//Flattens a matrix into an array of size rows*columns
export function flattenMatrix(matrix)
{
    var result = [];
    for (var i = 0; i < size(matrix); i += 1)
    {
        for (var j = 0; j < size(matrix[0]); j += 1)
        {
            result = append(result, matrix[i][j]);
        }
    }
    return result;
}

//Takes a flat array, and using the input uCount and vCount converts
//the array to a matrix with uCount rows and vCount columns
export function convertToMatrix(flatArray, uCount, vCount)
{
    var result = zeroMatrix(uCount, vCount);
    for (var i = 0; i < uCount; i += 1)
    {
        for (var j = 0; j < vCount; j += 1)
        {
            result[i][j] = flatArray[i * vCount + j];
        }
    }
    return result;
}


//Creates three manipulators based on the tanPlanesMatrix, and uses the manipulators to
//output a modified tanPlanesMatrix who's origins have been modified by the manipulators.
export function manipulatePlanes(context is Context, id is Id, tanPlanesMatrix, depthMap)
{

    var iSize = size(tanPlanesMatrix);
    var jSize = size(tanPlanesMatrix[0]);
    //Arrays that store the offsets for the manipulators.
    var primOffset is array = zeroMatrix(iSize, jSize);
    var secondOffset is array = zeroMatrix(iSize, jSize);
    var thirdOffset is array = zeroMatrix(iSize, jSize);

    //Matrices can't have units, so here we are adding units.
    for (var i = 0; i < iSize; i += 1)
    {
        for (var j = 0; j < jSize; j += 1)
        {
            primOffset[i][j] = 0 * inch;
            secondOffset[i][j] = 0 * inch;
            thirdOffset[i][j] = 0 * inch;
        }
    }

    //Check if either the U direction or the V direction are closed, and assign values.
    var loopDir1 = false;
    var loopDir2 = false;
    if (tolerantEquals(tanPlanesMatrix[0][0].origin, tanPlanesMatrix[iSize - 1][0].origin))
    {
        loopDir1 = true;
    }
    else if (tolerantEquals(tanPlanesMatrix[0][0].origin, tanPlanesMatrix[0][jSize - 1].origin))
    {
        loopDir2 = true;
    }

    //Primary manipulator loop, i.e. the "normal" manipulator.
    for (var i = 0; i < iSize; i += 1)
    {
        for (var j = 0; j < jSize; j += 1)
        {
            //If this manipulator has been changed, then define the offset using the depth map.
            if (depthMap[toString((jSize * i + j + 0 * iSize * jSize))] != undefined)
            {
                primOffset[i][j] = depthMap[toString((jSize * i + j))];
            }

            //If the splines loop, then bind the looped points togeather and don't create another manipulator.
            if (loopDir1 && i == iSize - 1)
            {
                tanPlanesMatrix[i][j].origin = tanPlanesMatrix[0][j].origin;
                continue;
            }
            if (loopDir2 && j == jSize - 1) //Test this, not sure how rn.
            {
                tanPlanesMatrix[i][j].origin = tanPlanesMatrix[i][0].origin;
                continue;
            }
        }
    }

    //U manipulator loop. These are deriven from the u-splines generated.
    var manNum = (iSize * jSize + 1); //An offset for the map-key entries so that unique new entries are made for the U and V directions.
    for (var i = 0; i < iSize; i += 1)
    {
        for (var j = 0; j < jSize; j += 1)
        {
            //If this manipulator has been changed, then define the offset using the depth map.
            if (depthMap[toString(jSize * i + j + manNum)] != undefined)
            {
                secondOffset[i][j] = depthMap[toString(jSize * i + j + manNum)];
            }

            //If the splines loop, then bind the looped points togeather and don't create another manipulator.
            if (loopDir1 && i == iSize - 1)
            {

                tanPlanesMatrix[i][j].origin = tanPlanesMatrix[0][j].origin;
                continue;
            }
            if (loopDir2 && j == jSize - 1) //Test this, not sure how rn.
            {
                tanPlanesMatrix[i][j].origin = tanPlanesMatrix[i][0].origin;
                continue;
            }

        }
    }

    //V manipulator loop
    for (var i = 0; i < iSize; i += 1)
    {
        for (var j = 0; j < jSize; j += 1)
        {

            //If this manipulator has been changed, then define the offset using the depth map.
            if (depthMap[toString(jSize * i + j + 2 * manNum)] != undefined)
            {
                thirdOffset[i][j] = depthMap[toString(jSize * i + j + 2 * manNum)]; //Multiply manNum by 2 because this is the third direction
            }

            //If the splines loop, then bind the looped points togeather and don't create another manipulator.
            if ((loopDir1 && i == iSize - 1) && iSize != 1)
            {

                tanPlanesMatrix[i][j].origin = tanPlanesMatrix[0][j].origin;
                continue;
            }

            if ((loopDir2 && j == jSize - 1) && jSize != 1) //Test this, not sure how rn.
            {
                tanPlanesMatrix[i][j].origin = tanPlanesMatrix[i][0].origin;
                continue;
            }

            var surfaceManipulatorPrimary = 0;
            var surfaceManipulatorU = 0;
            var surfaceManipulatorV = 0;

            var vDir = cross(tanPlanesMatrix[i][j].x, tanPlanesMatrix[i][j].normal);

            //The primary manipulator's origin must include the offsets generated by the other two directions.

            //Primary (normal) Manipulator added
            surfaceManipulatorPrimary = linearManipulator(
                    tanPlanesMatrix[i][j].origin + tanPlanesMatrix[i][j].x * secondOffset[i][j] + vDir * thirdOffset[i][j], //Origin must be offset by other two directions
                    tanPlanesMatrix[i][j].normal,
                    primOffset[i][j]
                );
            addManipulators(context, id, {
                        (toString((jSize * i + j))) : surfaceManipulatorPrimary
                    });

            //Secondary (U direction) Manipulator
            surfaceManipulatorU = linearManipulator(
                    tanPlanesMatrix[i][j].origin + tanPlanesMatrix[i][j].normal * primOffset[i][j] + vDir * thirdOffset[i][j], //Origin must be offset by other two directions
                    tanPlanesMatrix[i][j].x,
                    secondOffset[i][j]
                );
            addManipulators(context, id, {
                        (toString(jSize * i + j + manNum)) : surfaceManipulatorU
                    });

            //Tertiary (V direction) Manipulator
            surfaceManipulatorV = linearManipulator(
                    tanPlanesMatrix[i][j].origin + tanPlanesMatrix[i][j].normal * primOffset[i][j] + tanPlanesMatrix[i][j].x * secondOffset[i][j], //Origin must be offset by other two directions
                    vDir,
                    thirdOffset[i][j]
                );
            addManipulators(context, id, {
                        (toString(jSize * i + j + 2 * manNum)) : surfaceManipulatorV
                    });

            //If a manipulator is changed and thus added to the depth map, then move the origin for the
            //tanPlanesMatrix entry to reflect the manipulator change.
            if (depthMap[toString((jSize * i + j))] != undefined)
            {
                tanPlanesMatrix[i][j].origin += tanPlanesMatrix[i][j].normal * primOffset[i][j];
            }
            if (depthMap[toString((jSize * i + j + manNum))] != undefined)
            {
                tanPlanesMatrix[i][j].origin += tanPlanesMatrix[i][j].x * secondOffset[i][j];
            }
            if (depthMap[toString((jSize * i + j + 2 * manNum))] != undefined)
            {
                tanPlanesMatrix[i][j].origin += vDir * thirdOffset[i][j];
            }
        }
    }

    return tanPlanesMatrix;
}


export function makeNewSurfaceFill(context is Context, id is Id, originPlanes is array, sculptFace is Query)
{
    var guidePoints = qNothing();
    for (var i = 0; i < size(originPlanes); i += 1)
    {

        opPoint(context, id + ("point_" ~ i), {
                    "point" : originPlanes[i].origin
                });

        guidePoints = qUnion([guidePoints, qCreatedBy(id + ("point_" ~ i))]);

    }


    var surroundingEdges = qEdgeAdjacent(sculptFace, EntityType.EDGE);
    opFillSurface(context, id + "opFillSurface1", {
                "edgesG0" : surroundingEdges,
                "guideVertices" : guidePoints
            });

    opDeleteBodies(context, id + "deleteBodies1", {
                "entities" : guidePoints
            });

    return qCreatedBy(id + "opFillSurface1", EntityType.FACE);
}


//Creates splines along the u and v directions of an array (flattened matrix) originPlanes.
//Uses uCount and vCount to reconstruct the matrix.
export function createSplines(context, id, originPlanes, uCount, vCount)
{
    var uSplines = [];
    for (var i = 0; i < vCount; i += 1)
    {
        var splinePoints = [];

        for (var j = 0; j < uCount; j += 1)
        {
            splinePoints = append(splinePoints, originPlanes[uCount * i + j].origin);

        }

        opFitSpline(context, id + ("U_fitSpline_" ~ i), {
                    "points" : splinePoints
                });
        var uSpline = qCreatedBy(id + ("U_fitSpline_" ~ i), EntityType.EDGE);
        addDebugEntities(context, uSpline);
        uSplines = append(uSplines, uSpline);
    }

    var vSplines = [];
    for (var i = 0; i < uCount; i += 1)
    {
        var splinePoints = [];
        for (var j = 0; j < vCount; j += 1)
        {
            splinePoints = append(splinePoints, originPlanes[uCount * j + i].origin);
        }

        opFitSpline(context, id + ("V_fitSpline_" ~ i), {
                    "points" : splinePoints
                });
        var vSpline = qCreatedBy(id + ("V_fitSpline_" ~ i), EntityType.EDGE);
        addDebugEntities(context, vSpline);

        vSplines = append(vSplines, vSpline);
    }

    return { "uSplines" : uSplines, "vSplines" : vSplines };
}


function addDebugEntities(context is Context, entities is Query)
{
    @addDebugEntities(context, { "entities" : entities });
}

//Creates a new surface using the splin-loft method. Lofts between uSplines using
//vSplines as guide curves, or lofts between vSplines using usplines as guide curves
//depending on whether the splines are closed or not.
export function makeNewSurface(context, id, uSplines, vSplines)
{
    //Check if one of the axes are closed splines. If it is, then loft in the other direction.
    var surfaceId = id + "loft1";
    if (isClosed(context, vSplines[0]))
    {
        opLoft(context, surfaceId, {
                    "profileSubqueries" : vSplines,
                    "guideSubqueries" : resize(uSplines, size(uSplines) - 1), //The duplicate spline at the end (created because the profile splines are closed) can't be used
                    "bodyType" : ToolBodyType.SURFACE
                });
    }
    else if (isClosed(context, uSplines[0]))
    {
        opLoft(context, surfaceId, {
                    "profileSubqueries" : uSplines,
                    "guideSubqueries" : resize(vSplines, size(vSplines) - 1), //The duplicate spline at the end (created because the profile splines are closed) can't be used
                    "bodyType" : ToolBodyType.SURFACE
                });
    }
    else //If none of the splines are closed, then use all of the splines for lofting.
    {
        opLoft(context, surfaceId, {
                    "profileSubqueries" : uSplines,
                    "guideSubqueries" : vSplines,
                    "bodyType" : ToolBodyType.SURFACE
                });
    }


    opDeleteBodies(context, id + "deleteBodies1", {
                "entities" : qUnion([qUnion(uSplines), qUnion(vSplines)])
            });
    return qCreatedBy(surfaceId, EntityType.FACE);
}

//Creates an array of 2d vectors, where the vectors x and y components span the range
//between 0 and 1 evenly, using uCount and vCount to determine how many vectors to create in
//each direction.
export function initializeFlatMatrix(uCount, vCount, offset)
{
    var addVal = 0;
    if (offset != 0)
    {
        addVal = 1;
    }
    var result = [];
    for (var i = 0; i < (vCount - offset / vCount); i += 1)
    {
        for (var j = 0; j < (uCount - offset / uCount); j += 1)
        {
            result = append(result, vector((i + offset) / (vCount - 1 + addVal), (j + offset) / (uCount - 1 + addVal)));
        }
    }

    return result;
}

//Manipulator change function for all of the manipulators.
export function surfaceManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    //Depth map will start out as 0, which is not a map. We always want a map, so check for this condition and reassign as a map.
    if (definition.depthMap == 0)
    {
        definition.depthMap = {};
    }

    //The name will be the key (there's only one) in newManipulators. This is because
    //new manipulators will be created one at a time in for loops in the manipulatePlanes function.
    //Each manipulator's name is a string of numbers, which will be extracted using the stringToNumber
    //function.
    var name = stringToNumber(keys(newManipulators)[0]);
    var currentDepthMap = definition.depthMap;

    var newDepth is ValueWithUnits = newManipulators[toString(name)].offset;
    //The new depth is added to the map, and the definition is returned.
    currentDepthMap[toString(name)] = newDepth;

    definition.depthMap = currentDepthMap;

    return definition;
}

export function getRayEdge(context is Context, id is Id, surface is Query, rayLine is Line)
{
    var closeEdges = evaluateQuery(context, qClosestTo(qOwnedByBody(qOwnerBody(surface), EntityType.EDGE), rayLine.origin));
    for (var i = 0; i < size(closeEdges); i += 1)
    {
        var edge = closeEdges[i];

        var edgeDirection = evLine(context, {
                    "edge" : edge
                }).direction;

        if (tolerantEquals(edgeDirection, rayLine.direction))
        {
            return edge;
        }
    }
    throw regenError("Couldn't find matching Edge");
}

export function getRayIntersection(context is Context, id is Id, rayToTrace is Line)
{
    var createdBodies = qNothing();
    opFitSpline(context, id + "extrudeSpline", {
                "points" : [
                        rayToTrace.origin,
                        rayToTrace.origin + perpendicularVector(rayToTrace.direction) / 1000000 * inch
                    ]
            });

    createdBodies = qUnion([createdBodies, qCreatedBy(id + "extrudeSpline")]);

    opExtrude(context, id + "extrude1", {
                "entities" : qCreatedBy(id + "extrudeSpline", EntityType.EDGE),
                "direction" : rayToTrace.direction,
                "endBound" : BoundingType.UP_TO_NEXT
            });
    createdBodies = qUnion([createdBodies, qCreatedBy(id + "extrude1")]);



    var rayEdge = [];
    try silent
    {
        rayEdge = getRayEdge(context, id, qCreatedBy(id + "extrude1", EntityType.FACE), rayToTrace);
    }
    catch
    {
        opFitSpline(context, id + "extrudeSplineOpDir", {
                    "points" : [
                            rayToTrace.origin,
                            rayToTrace.origin - perpendicularVector(rayToTrace.direction) / 1000000 * inch
                        ]
                });
        createdBodies = qUnion([createdBodies, qCreatedBy(id + "extrudeSplineOpDir")]);
        opExtrude(context, id + "extrudeReverse", {
                    "entities" : qCreatedBy(id + "extrudeSplineOpDir", EntityType.EDGE),
                    "direction" : rayToTrace.direction,
                    "endBound" : BoundingType.UP_TO_NEXT
                });
        createdBodies = qUnion([createdBodies, qCreatedBy(id + "extrudeReverse")]);


        rayEdge = getRayEdge(context, id, qCreatedBy(id + "extrudeReverse", EntityType.FACE), rayToTrace);
    }


    var rayEndPoint = evEdgeTangentLine(context, {
                "edge" : rayEdge,
                "parameter" : 1
            }).origin;

    opFitSpline(context, id + "fitSpline1", {
                "points" : [
                        rayToTrace.origin,
                        rayEndPoint
                    ]
            });
    createdBodies = qUnion([createdBodies, qCreatedBy(id + "fitSpline1")]);

    opDeleteBodies(context, id + "deleteBodies1", {
                "entities" : createdBodies
            });

    return rayEndPoint;
}


export enum SculptType
{
    annotation { "Name" : "Edge Dynamic" }
    EDGE_DYNAMIC,
    annotation { "Name" : "Edge Static" }
    EDGE_STATIC
}
