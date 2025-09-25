
//_______________________________________________________________________________________________________________________________________________
//
// This FeatureScript is owned by Michael Pascoe and is distributed by CADSharp LLC. 
// You may not redistribute it for commercial purposes without the permission of said owner and CADSharp LLC. Copyright (c) 2023 Michael Pascoe.
//_______________________________________________________________________________________________________________________________________________


FeatureScript 1349;
import(path : "onshape/std/geometry.fs", version : "1349.0");

// CADSharp
export import(path : "cbeb3dcf671e00785597bd76/409d65a3744fe434f32bdffc/a75ab01def146a42f55baa7f", version : "381046010d5aea697e433948");

icon::import(path : "df90f5aeb3090a82986a8715", version : "f5e052facb765917b1b4276f"); //To add an icon, click the import button above. Select your file, it must be an svg file. Then place "IconNamespace::" in front of the import.

annotation {
        "Feature Type Name" : "Topographinator",
        "Icon" : icon::BLOB_DATA, // To add an icon, you need " "Icon" : icon::BLOB_DATA"
        "Manipulator Change Function" : "topographinatorManipulatorChange",
        "Feature Type Description" : "<br> <b>Summary</b> <br> Extrudes offset layers similar to a topographic map.",
        "Description Image" : cadsharpLogo::BLOB_DATA,
        "Editing Logic Function" : "cadsharpUrlEditLogic"
    } //Required for manipulator.
export const topographinator = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Layer Type", "UIHint" : "HORIZONTAL_ENUM" } //Required to create a horizontal tab menu.
        definition.layerType is LayerType;
        annotation { "Name" : "Select Face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.center is Query;
        annotation { "Name" : "Layer Thickness" }
        isLength(definition.layerThickness, { (inch) : [0, 1, 1e5] } as LengthBoundSpec);


        if (definition.layerType == LayerType.AUTO_LAYERS) //Places everything below in a horizontal tab menu.
        {
            annotation { "Name" : "Number of Layers" }
            isInteger(definition.numberOfLayers, { (unitless) : [2, 5, 100000] } as IntegerBoundSpec);
            annotation { "Name" : "Center Size %" }
            isInteger(definition.setOffsetPercentage, { (unitless) : [1, 30, 99] } as IntegerBoundSpec); //minimum 1, starts at 30, max is 99.
        }

        annotation { "Name" : "Invert" }
        definition.invert is boolean;
        annotation { "Name" : "Split part into layers", "Default" : false }
        definition.splitPart is boolean;

        if (definition.layerType == LayerType.AUTO_LAYERS) //Places everything below in a horizontal tab menu.
        {
            annotation { "Name" : "Center Point Guide" }
            definition.centerPointBoolean is boolean;

            if (definition.centerPointBoolean == true)
            {
                annotation { "Name" : "Select Center Point", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
                definition.centerPoint is Query;
            }
        }

        cadsharpUrlPredicate(definition);
    }
    {

        if (definition.layerType == LayerType.MANUAL_LAYERS) //Places all items within the "if" inside a horizontal tab menu.
        {
            var extrudePlane1 is Plane = evFaceTangentPlane(context, { //Required for manipulator.
                    "face" : definition.center,
                    "parameter" : vector(0.5, 0.5)
                });
            var layerThickness = definition.layerThickness;
            var seedFace = definition.center;
            var adjacentFace = qNothing();
            var usedFaces = definition.center;
            var orderedFaces = [definition.center];
            var endLoop = 0;

            for (var i = 0; endLoop == 0; i += 1)
            {
                // find the adjacent face, excluding already used faces
                adjacentFace = qSubtraction(qAdjacent(seedFace, AdjacencyType.EDGE, EntityType.FACE), usedFaces);
                // update the list of used faces
                usedFaces = qUnion([usedFaces, adjacentFace]);
                // use the adjacent face as the seed face to find the next adjacent face
                seedFace = adjacentFace;
                //  When there aren't any more adjacent faces left, end the loop. I thought I could just say "if (adjacentFace == qNothing())", but it wasn't working, so i hacked it this way.
                var count = size(evaluateQuery(context, adjacentFace));
                if (count != 0)
                {
                    // the running list of all of the faces in order radiating from the center face
                    orderedFaces = append(orderedFaces, adjacentFace);
                }
                else
                {
                    endLoop = 1;
                }
            }
            // now ordered faces is an array of the faces in the right order that you can apply desired operations to in another loop.
            var faceCount = size(orderedFaces);
            var bodies = qNothing();
            var depth;

            for (var i = 0; i < faceCount; i += 1)
            {
                depth = definition.invert ? layerThickness * (i + 1) : layerThickness * (faceCount - i);
                opExtrude(context, id + "extrude1" + i, {
                            "entities" : orderedFaces[i],
                            "direction" : evOwnerSketchPlane(context, { "entity" : definition.center }).normal,
                            "endBound" : BoundingType.BLIND,
                            "endDepth" : depth
                        });

                bodies = qUnion([bodies, qCreatedBy(id + "extrude1" + i, EntityType.BODY)]);
            }

            opBoolean(context, id + "boolean1", {
                        "tools" : bodies,
                        "operationType" : BooleanOperationType.UNION
                    });

            var parts = qCreatedBy(id + "extrude1", EntityType.BODY);

            if (!definition.splitPart == false) //If the splitPart check box is checked, run the following "for" loop.
            {
                for (var i = 0; i < faceCount; i += 1) //"for" is a loop. If the variable created "i" is less than the size of the array "size(faces)", the the loop will continue untill "i" is equal to the "size(faces).
                {
                    var depth = definition.layerThickness * (i + 1); //Multiplies the depth by the current loop count +1.

                    opPlane(context, id + "plane1" + i, { //Plane created based on evaluated plane and definition.offset. Adds i to the id each loop. Creates a surface using bottomFace. Remember to add a name "extractSurface" for later use.
                                "plane" : plane(extrudePlane1.origin + extrudePlane1.normal * depth, extrudePlane1.normal, extrudePlane1.x)
                            });
                }
            }

            //var part = qCreatedBy(id + "extrude", EntityType.BODY); //Creates a variable and finds the part created by "extrude".
            if (!definition.splitPart == false) //If the splitPart check box is checked, run the following "for" loop.
            {
                for (var i = 0; i < faceCount; i += 1) //"for" is a loop. If the variable created "i" is less than the size of the array "size(faces)", the the loop will continue untill "i" is equal to the "size(faces).
                {
                    var splitPlanes = qCreatedBy(id + "plane1" + i, EntityType.BODY); //Finds created planes. +i changes the id each loop so that if finds all of the planes created in the previous loop.

                    opSplitPart(context, id + "splitPart1" + i, { //Adds i to the id each loop. Remember to add a name "extractSurface" for later use.
                                "targets" : parts,
                                "tool" : splitPlanes
                            });
                }

                for (var i = 0; i < faceCount; i += 1) //"for" is a loop. If the variable created "i" is less than the size of the array "size(faces)", the the loop will continue untill "i" is equal to the "size(faces).
                {
                    var splitPlanes = qCreatedBy(id + "plane1" + i, EntityType.BODY); //Finds created planes. +i changes the id each loop so that if finds all of the planes created in the previous loop.

                    opDeleteBodies(context, id + "deleteBodies1" + i, {
                                "entities" : qCreatedBy(id + "plane1" + i, EntityType.BODY)
                            });
                }
            }
        }

        if (definition.layerType == LayerType.AUTO_LAYERS) //Required to create a horizontal tab menu.
        {

            var correctedLayerQuantity = definition.numberOfLayers - 1;
            var offsetPercentage = 1 - definition.setOffsetPercentage / 100;
            // var loftPlaneHeight = correctedLayerQuantity * (definition.layerThickness/inch) + centerOffsetPercentage; // ".value" turns a map into a value so that you can use it as a value.
            var centroid = evApproximateCentroid(context, { //Finds the centroid of the selected face.
                    "entities" : definition.center
                });
            var edge = qLoopEdges(definition.center); //Findes edge that belongs to selected face.
            var evalPlane is Plane = evPlane(context, { //Finds the plane that the selected face is on. Gets the plane object for the plane query
                    "face" : definition.center
                });

            var centroid2d;
            if (definition.centerPointBoolean == true)
            {
                var selectedPoint = evVertexPoint(context, {
                        "vertex" : definition.centerPoint
                    });
                centroid2d = worldToPlane(evalPlane, selectedPoint);
            }
            else
            {
                centroid2d = worldToPlane(evalPlane, centroid);
            }

            var sketch = newSketchOnPlane(context, id + "sketch1", { "sketchPlane" : plane(evalPlane.origin + evalPlane.normal * -1 * inch, evalPlane.normal, evalPlane.x) }); //Creates a sketch on a plane.
            var point = skPoint(sketch, "point1", { "position" : centroid2d });

            skSolve(sketch); //Completes the sketch.

            var evalPoint = qSketchFilter(qCreatedBy(id + "sketch1", EntityType.BODY), SketchObject.YES); //Finds the point on the sketch, as a query so that it can be used in a loft.

            opLoft(context, id + "loft1", {
                        "profileSubqueries" : [definition.center, evalPoint], //Loft selection, edge and point.
                        "bodyType" : ToolBodyType.SOLID //Sets the loft type to surface or solid.
                    });

            var loftedPart = qCreatedBy(id + "loft1", EntityType.BODY);

            for (var i = 0; i < correctedLayerQuantity; i += 1) //"for" is a loop. If the variable created "i" is less than the size of the array "size(faces)", the the loop will continue untill "i" is equal to the "size(faces).
            {
                var depth = 1 / correctedLayerQuantity * (i + 1) * offsetPercentage; //Multiplies the depth by the current loop count +1.

                opPlane(context, id + "plane2" + i, { //Plane created based on evaluated plane and definition.offset. Adds i to the id each loop. Creates a surface using bottomFace. Remember to add a name "extractSurface" for later use.
                            "plane" : plane(evalPlane.origin + evalPlane.normal * -depth * inch, evalPlane.normal, evalPlane.x)
                        });
            }

            for (var i = 0; i < correctedLayerQuantity; i += 1) //"for" is a loop. If the variable created "i" is less than the size of the array "size(faces)", the the loop will continue untill "i" is equal to the "size(faces).
            {
                var splitPlanes2 = qCreatedBy(id + "plane2" + i, EntityType.BODY); //Finds created planes. +i changes the id each loop so that if finds all of the planes created in the previous loop.

                opSplitPart(context, id + "splitPart2" + i, { //Adds i to the id each loop. Remember to add a name "extractSurface" for later use.
                            "targets" : loftedPart,
                            "tool" : splitPlanes2
                        });
            }

            var splitParts = qCreatedBy(id + "loft1", EntityType.BODY);
            var evalSplitParts = evaluateQuery(context, splitParts); //The original oporation must be queried, not the splitting operation.

            var splitFaces = qCreatedBy(id + "splitPart2", EntityType.FACE);

            var evalSplitPlanes2 = evaluateQuery(context, qCreatedBy(id + "plane2", EntityType.BODY));
            var splitPlane = evalSplitPlanes2[0];

            var bottomFace = qCoincidesWithPlane(qCreatedBy(id + "loft1", EntityType.FACE), evalPlane);

            for (var i = 0; i < correctedLayerQuantity; i += 1)
            {
                //the faces for each body is found. qIntersection only keeps the items found in both queries. "extrudeFace" is evaluated and returns 2 faces for each part, evalExtrudeFace[0] is used so that only one of the faces is selected for the extrude.
                var splitPartFaces = qOwnedByBody(evalSplitParts[i + 1], EntityType.FACE);
                var extrudeFace = qIntersection(splitFaces, splitPartFaces);
                var evalExtrudeFace = evaluateQuery(context, extrudeFace);

                opExtrude(context, id + "extrudeFace" + i, { //extrudes each queried face to a new height.
                            "entities" : evalExtrudeFace[0],
                            "direction" : evalPlane.normal,
                            "endBound" : BoundingType.UP_TO_SURFACE,
                            "endBoundEntity" : bottomFace
                            //,
                            //"endTranslationalOffset" : definition.invert ? definition.layerThickness * (correctedLayerQuantity - i) : definition.layerThickness * (i + 1 + 1),
                            //"isStartBoundOpposite" : false,
                            //"startBound" : BoundingType.UP_TO_SURFACE,
                            //"startBoundEntity" : bottomFace
                            //"startTranslationalOffset" : -.25 * inch
                        });
            }

            opExtrude(context, id + "extrudeOriginalShape", {
                        "entities" : bottomFace,
                        "direction" : evalPlane.normal,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : 1 * inch
                    });

            var extrudedParts2 = qUnion([qCreatedBy(id + "extrudeFace", EntityType.BODY), qCreatedBy(id + "extrudeOriginalShape", EntityType.BODY)]);
            opBoolean(context, id + "booleanExtrudedParts2", {
                        "tools" : extrudedParts2,
                        "operationType" : BooleanOperationType.UNION
                    });

            var facesFromExtrude = qCreatedBy(id + "extrudeFace", EntityType.FACE);
            var normalFromExtrude = qFacesParallelToDirection(facesFromExtrude, evalPlane.normal);
            var facesToExtrude1 = qSubtraction(facesFromExtrude, normalFromExtrude);
            var evalFacesToExtrude1 = evaluateQuery(context, facesToExtrude1);
            //debug(context, facesToExtrude1, DebugColor.BLUE);

            var facesFromExtrude2 = qCreatedBy(id + "extrudeOriginalShape", EntityType.FACE);
            var normalFaces = qFacesParallelToDirection(facesFromExtrude2, evalPlane.normal);
            var parallelFaces = qSubtraction(facesFromExtrude2, normalFaces);
            var facesToExtrude2 = qCoincidesWithPlane(parallelFaces, evalPlane);

            var allFacesToExtrude = qUnion([facesToExtrude1, facesToExtrude2]);
            var evalAllFacesToExtrude = evaluateQuery(context, allFacesToExtrude);
            //debug(context, allFacesToExtrude, DebugColor.RED);

            for (var i = 1; i < correctedLayerQuantity; i += 1)
            {

                opExtrude(context, id + "extrudeFace2" + i, {
                            "entities" : evalAllFacesToExtrude[i],
                            "direction" : evalPlane.normal,
                            "endBound" : BoundingType.UP_TO_SURFACE,
                            "endBoundEntity" : bottomFace,
                            "endTranslationalOffset" : definition.invert ? definition.layerThickness * (correctedLayerQuantity - i + 1) : definition.layerThickness * (i + 1),
                            "isStartBoundOpposite" : false,
                            "startBound" : BoundingType.UP_TO_SURFACE,
                            "startBoundEntity" : bottomFace
                            //"startTranslationalOffset" : -.25 * inch
                        });
            }

            opExtrude(context, id + "extrudeOriginalShape2", {
                        "entities" : facesToExtrude2,
                        "direction" : evalPlane.normal,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : definition.invert ? definition.layerThickness * (correctedLayerQuantity + 1) : definition.layerThickness
                    });

            opExtrude(context, id + "extrudeCenterShape2", {
                        "entities" : evalFacesToExtrude1[0],
                        "direction" : evalPlane.normal,
                        "endBound" : BoundingType.UP_TO_SURFACE,
                        "endBoundEntity" : bottomFace,
                        "endTranslationalOffset" : definition.invert ? definition.layerThickness : definition.layerThickness * (correctedLayerQuantity + 1),
                        "isStartBoundOpposite" : false,
                        "startBound" : BoundingType.UP_TO_SURFACE,
                        "startBoundEntity" : bottomFace
                        //"startTranslationalOffset" : -.25 * inch
                    });

            if (!definition.splitPart == false) //If the splitPart check box is checked, run the following "for" loop.
            {
                for (var i = 0; i < correctedLayerQuantity; i += 1) //"for" is a loop. If the variable created "i" is less than the size of the array "size(faces)", the the loop will continue untill "i" is equal to the "size(faces).
                {
                    var depth = definition.layerThickness * (i + 1); //Multiplies the depth by the current loop count +1.

                    opPlane(context, id + "plane3" + i, { //Plane created based on evaluated plane and definition.offset. Adds i to the id each loop. Creates a surface using bottomFace. Remember to add a name "extractSurface" for later use.
                                "plane" : plane(evalPlane.origin + evalPlane.normal * depth, evalPlane.normal, evalPlane.x)
                            });
                }
            }

            const partsToSplit = qUnion([
                        qCreatedBy(id + "extrudeFace2", EntityType.BODY),
                        qCreatedBy(id + "extrudeOriginalShape2", EntityType.BODY),
                        qCreatedBy(id + "extrudeCenterShape2", EntityType.BODY)]);

            try
            {
                opBoolean(context, id + "booleanPartsToSplit", {
                            "tools" : partsToSplit,
                            "operationType" : BooleanOperationType.UNION
                        });
            }
            catch
            {
                var facesToMove = qUnion([
                        qCreatedBy(id + "extrudeFace2", EntityType.FACE),
                        qCreatedBy(id + "extrudeOriginalShape2", EntityType.FACE),
                        qCreatedBy(id + "extrudeCenterShape2", EntityType.FACE)]);
                var topFaces = qPlanesParallelToDirection(facesToMove, evalPlane.origin);
                var adjacentFaces = qAdjacent(topFaces, AdjacencyType.EDGE, EntityType.FACE);

                opOffsetFace(context, id + "offsetFace1", {
                            "moveFaces" : adjacentFaces,
                            "offsetDistance" : .001 * inch

                        });

                opBoolean(context, id + "booleanPartsToSplit2", {
                            "tools" : partsToSplit,
                            "operationType" : BooleanOperationType.UNION
                        });
            }

            if (!definition.splitPart == false) //If the splitPart check box is checked, run the following "for" loop.
            {
                for (var i = 0; i < correctedLayerQuantity; i += 1) //"for" is a loop. If the variable created "i" is less than the size of the array "size(faces)", the the loop will continue untill "i" is equal to the "size(faces).
                {
                    var splitPlanes2 = qCreatedBy(id + "plane3" + i, EntityType.BODY); //Finds created planes. +i changes the id each loop so that if finds all of the planes created in the previous loop.

                    opSplitPart(context, id + "splitPart3" + i, { //Adds i to the id each loop. Remember to add a name "extractSurface" for later use.
                                "targets" : partsToSplit,
                                "tool" : splitPlanes2
                            });
                }

                for (var i = 0; i < correctedLayerQuantity; i += 1) //"for" is a loop. If the variable created "i" is less than the size of the array "size(faces)", the the loop will continue untill "i" is equal to the "size(faces).
                {
                    var splitPlanes = qCreatedBy(id + "plane3" + i, EntityType.BODY); //Finds created planes. +i changes the id each loop so that if finds all of the planes created in the previous loop.

                    opDeleteBodies(context, id + "deleteBodies2" + i, {
                                "entities" : qCreatedBy(id + "plane3" + i, EntityType.BODY)
                            });
                }
            }

            opDeleteBodies(context, id + "deleteBodies3", { //Deletes sketch1.
                        "entities" : qCreatedBy(id + "sketch1"),

                    });

            opDeleteBodies(context, id + "deleteBodies4", {
                        "entities" : qUnion([loftedPart, extrudedParts2])
                    });

            opDeleteBodies(context, id + "deleteBodies5", {
                        "entities" : qCreatedBy(id + "plane2", EntityType.BODY)
                    });
        }
    }, {});

//Required for manipulator.
export function topographinatorManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    var newDepth is ValueWithUnits = newManipulators["myManipulator"].offset;
    definition.depth = abs(newDepth);
    definition.invert = newDepth > 0;
    return definition;
}

export enum LayerType //Required to create a horizontal tab menu.
{
    annotation { "Name" : "Auto Layers" }
    AUTO_LAYERS,

    annotation { "Name" : "Manual Layers" }
    MANUAL_LAYERS
}
