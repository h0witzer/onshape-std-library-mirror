
//_______________________________________________________________________________________________________________________________________________
//
// This FeatureScript is owned by Michael Pascoe and is distributed by CADSharp LLC. 
// You may not redistribute it for commercial purposes without the permission of said owner and CADSharp LLC. Copyright (c) 2023 Michael Pascoe.
//_______________________________________________________________________________________________________________________________________________


// FeatureScript 1378;
// import(path : "onshape/std/common.fs", version : "1378.0");

FeatureScript 2599;
import(path : "onshape/std/common.fs", version : "2599.0");

// CADSharp
export import(path : "cbeb3dcf671e00785597bd76/409d65a3744fe434f32bdffc/a75ab01def146a42f55baa7f", version : "381046010d5aea697e433948");

//custom library function by Alex Kempen
import(path : "4c21d0c3c89c0a81aadfdac6/636ff98c3710f0d81fcecbf8/3732a1478a38cc5723a9801f", version : "17c016200f841487aca7f892");

// //Table
// import(path : "onshape/std/table.fs", version : "1378.0");

icon::import(path : "b2cc0d522c6fa22ed4a4447f", version : "f3212841fad8ee55899307ba");

//aligned bounding box
export import(path : "1191ad078261c48e44f33209/71772266066c4d67841687c9/905d9c769056ba52d974e529", version : "3e8fc0245b42f51be880e156");

// qMatchingBodies
import(path : "c7c08274a0d273b9a5f5b47d/d2eda0957a9bebd31afb9143/be9004406b5841b3a7443f48", version : "4156173b16331158ef3c616b");

// decimal to fraction
import(path : "c7c08274a0d273b9a5f5b47d/44d8b2e8b7276dd13435be44/0f412781b1b19b04fbb0b9aa", version : "46678579b063767d508a96a7");

const tableDataVariableName = "MeasureCutListData";
const tableDataColumnVariableName = "MeasureCutListColumnData";

export enum MapLengths
{
    annotation { "Name" : "Column 3" }
    COLUMN3,
    annotation { "Name" : "Column 4" }
    COLUMN4,
    annotation { "Name" : "Column 5" }
    COLUMN5,
}

/** Measure the distance between two sets of entities and set the result to a variable */
annotation { "Feature Type Name" : "Measure cut list",
        "UIHint" : "NO_PREVIEW_PROVIDED",
        "Icon" : icon::BLOB_DATA,
        "Editing Logic Function" : "measure",
        "Description Image" : cadsharpLogo::BLOB_DATA,
        "Feature Type Description" : "<b> Summary </b> <br> Creates a custom table of measurements for using as a cut list. <br>", }
export const measureList = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Group Name" : "Settings", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Table name", "Default" : "Measure cut list" }
            definition.tableName is string;


            annotation { "Name" : "Prefix", "Default" : "" }
            definition.prefixName is string;

            annotation { "Name" : "Add to quantity" }
            isReal(definition.addToQuantity, { (unitless) : [-1e5, 0, 1e5] } as RealBoundSpec);

            annotation { "Name" : "x offset" }
            isLength(definition.initialLengthOffset, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);

            annotation { "Name" : "y offset" }
            isLength(definition.initialWidthOffset, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);

            annotation { "Name" : "z offset" }
            isLength(definition.initialThicknessOffset, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);

            annotation { "Name" : "Use part names (STATIC)", "Description" : "Will not update automatically.", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.usePartNames is boolean;

            if (definition.usePartNames)
            {
                annotation { "Name" : "Update part names", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
                definition.updatePartNames is boolean;
            }

            annotation { "Name" : "Use part materials (STATIC)", "Description" : "Will not update automatically.", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.usePartMaterials is boolean;

            if (definition.usePartMaterials)
            {
                annotation { "Name" : "Update part names", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
                definition.updatePartMaterials is boolean;
            }

            annotation { "Name" : "Measure duplicate parts", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.measureDuplicateParts is boolean;

            annotation { "Name" : "Flatten sheet metal", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.flattenSheetMetal is boolean;

            annotation { "Name" : "Export variables", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.exportVariables is boolean;

            annotation { "Name" : "Create bounding box", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.createBoundingBox is boolean;

            annotation { "Name" : "Set part names", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.setPartNames is boolean;

            annotation { "Name" : "Set descriptions to size", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.setPartDescriptionsToSize is boolean;

            if (!definition.setPartDescriptionsToSize)
            {
                annotation { "Name" : "Set part descriptions to match table ID", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                definition.setPartDescriptions is boolean;
            }

            annotation { "Name" : "Use fractional inches", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.useFractionalInches is boolean;

            annotation { "Name" : "Round table values", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.roundPrecision, { (inch) : [0, 0.001, 10000] } as LengthBoundSpec);

            annotation { "Name" : "Column 1", "Default" : "Id", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.column1 is string;

            annotation { "Name" : "Column 2", "Default" : "Qty", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.column2 is string;

            annotation { "Name" : "Column 3", "Default" : "X", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.column3 is string;

            annotation { "Name" : "Column 4", "Default" : "Y", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.column4 is string;

            annotation { "Name" : "Column 5", "Default" : "Z", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.column5 is string;

            // annotation { "Name" : "Match world XYZ" }
            // definition.matchWorldXYZ is boolean;

            // if (!definition.matchWorldXYZ)
            // {
            annotation { "Name" : "Map longest to", "Default" : MapLengths.COLUMN3, "UIHint" : UIHint.SHOW_LABEL, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.mapLongest is MapLengths;

            annotation { "Name" : "Map shortest to", "Default" : MapLengths.COLUMN5, "UIHint" : UIHint.SHOW_LABEL, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.mapShortest is MapLengths;
            // }
        }

        annotation {
                    "Name" : "Measure cut list",
                    "Driven query" : "entities",
                    "Item name" : "Item",
                    "Item label template" :
                    "#name", "UIHint" : UIHint.COLLAPSE_ARRAY_ITEMS }
        definition.groups is array;
        for (var group in definition.groups)
        {
            annotation { "Name" : "Method", "UIHint" : "HORIZONTAL_ENUM" }
            group.method is Method;

            annotation { "Name" : "Item group name", "Default" : "Loading...", "UIHint" : UIHint.ALWAYS_HIDDEN } //hidden name to combign itemName and prefix.
            group.name is string;

            annotation { "Name" : "Item material", "Default" : "----", "UIHint" : UIHint.ALWAYS_HIDDEN }
            group.itemMaterial is string;

            annotation { "Name" : "Item name", "Default" : "Item" }
            group.itemName is string;

            if (group.method == Method.MEASURE)
            {
                annotation { "Name" : "Quantity" }
                isReal(group.quantity, { (unitless) : [-1e5, 1, 1e5] } as RealBoundSpec);
            }

            annotation { "Name" : "Override offsets" }
            group.offsets is boolean;

            if (group.offsets)
            {
                annotation { "Name" : "Offset x" }
                isLength(group.lengthOffsetG, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);

                annotation { "Name" : "Offset y" }
                isLength(group.widthOffsetG, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);

                annotation { "Name" : "Offset z" }
                isLength(group.thicknessOffsetG, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);
            }

            // annotation { "Name" : "Use previous values" }
            // group.usePreviousValues is boolean;

            if (group.method == Method.AUTO)
            {
                annotation { "Name" : "Entities", "Filter" : (EntityType.FACE || EntityType.BODY) && BodyType.SOLID, "MaxNumberOfPicks" : 1 }
                group.entities is Query;
            }
            else if (group.method == Method.MEASURE)
            {
                //_______________________________________________________________________
                annotation { "Name" : "lengthInputReturned", "UIHint" : UIHint.ALWAYS_HIDDEN }
                group.lengthInputReturned is boolean;

                if (group.lengthInputReturned == false)
                {
                    annotation { "Name" : "Length", "Filter" : EntityType.FACE || EntityType.EDGE || EntityType.VERTEX, "MaxNumberOfPicks" : 2 }
                    group.lengthEntities is Query;
                }

                if (group.lengthInputReturned == true)
                {
                    annotation { "Name" : "Length" }
                    isLength(group.dx, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);
                }

                annotation { "Name" : "Measure", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                group.lengthInputMethod is boolean;

                //_______________________________________________________________________

                //_______________________________________________________________________
                annotation { "Name" : "widthInputReturned", "UIHint" : UIHint.ALWAYS_HIDDEN }
                group.widthInputReturned is boolean;

                if (group.widthInputReturned == false)
                {
                    annotation { "Name" : "Width", "Filter" : EntityType.FACE || EntityType.EDGE || EntityType.VERTEX, "MaxNumberOfPicks" : 2 }
                    group.widthEntities is Query;
                }

                if (group.widthInputReturned == true)
                {
                    annotation { "Name" : "Width" }
                    isLength(group.dy, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);
                }

                annotation { "Name" : "Measure", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                group.widthInputMethod is boolean;
                //_______________________________________________________________________

                //_______________________________________________________________________
                annotation { "Name" : "thicknessInputReturned", "UIHint" : UIHint.ALWAYS_HIDDEN }
                group.thicknessInputReturned is boolean;

                if (group.thicknessInputReturned == false)
                {
                    annotation { "Name" : "Thickness", "Filter" : EntityType.FACE || EntityType.EDGE || EntityType.VERTEX, "MaxNumberOfPicks" : 2 }
                    group.thicknessEntities is Query;
                }

                if (group.thicknessInputReturned == true)
                {
                    annotation { "Name" : "Thickness" }
                    isLength(group.dz, { (inch) : [-1e5, 0, 1e5] } as LengthBoundSpec);
                }

                annotation { "Name" : "Measure", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                group.thicknessInputMethod is boolean;
                //_______________________________________________________________________
            }
        }

        cadsharpUrlPredicate(definition);
    }
    {
        var groups = {};
        var g = -1;
        var aBoxResults = undefined;
        var allParts = qNothing();
        var toDelete = qNothing();

        for (var i = 0; i < size(definition.groups); i += 1)
        {
            const evParts = evaluateQuery(context, definition.groups[i].entities);

            if (definition.flattenSheetMetal)
            {
                for (var k = 0; k < size(evParts); k += 1)
                {
                    const thisPart = qOwnerBody(evParts[k]);

                    if (isQueryEmpty(context, thisPart))
                    {
                        definition.groups[i].entities = qSubtraction(definition.groups[i].entities, evParts[k]);
                        continue;
                    }

                    const sheetMetalQuery = qCorrespondingInFlat(thisPart);

                    if (!isQueryEmpty(context, sheetMetalQuery))
                    {
                        const patternId = id + "sheetMetalPattern" + i + k;
                        opPattern(context, patternId, {
                                    "entities" : sheetMetalQuery,
                                    "transforms" : [identityTransform()],
                                    "instanceNames" : ["copy"]
                                });
                        const copy = qCreatedBy(patternId, EntityType.BODY);

                        definition.groups[i].entities = qSubtraction(definition.groups[i].entities, evParts[k]);
                        definition.groups[i].entities = qUnion(definition.groups[i].entities, copy);
                        // toDelete = qUnion([toDelete, copy]);
                    }
                }
            }

            var parts = qOwnerBody(definition.groups[i].entities);
            allParts = qUnion([allParts, parts]);
        }

        var matchingBodies = qMatchingBodies(context, {
                "entities" : allParts,
                "tolerance" : .01
            });

        for (var group in definition.groups)
        {
            g = g + 1;
            const db = definition.groups[g].debug;
            definition.groups[g].matchingEntities = qNothing(); // Initialize the matching entities

            if (definition.groups[g].method == Method.AUTO)
            {

                if (!definition.measureDuplicateParts)
                {
                    // Handle matching bodies for quantity
                    if (isQueryEmpty(context, qIntersection(qUnion(matchingBodies), definition.groups[g].entities->qOwnerBody())))
                    {
                        // Matching bodies have already been measured and handled.
                        continue;
                    }
                    else // Matching bodies need to be measured for the first time
                    {
                        var matchFound = false;

                        for (var i = 0; i < size(matchingBodies); i += 1)
                        {
                            if (matchFound)
                            {
                                continue;
                            }

                            if (!isQueryEmpty(context, qIntersection(matchingBodies[i], definition.groups[g].entities->qOwnerBody())))
                            {
                                matchFound = true;
                                definition.groups[g].quantity = size(evaluateQuery(context, matchingBodies[i]));

                                // Add matching bodies to a new definition so we can add them to the table later
                                definition.groups[g].matchingEntities = matchingBodies[i];

                                if (definition.groups[g].quantity > 1)
                                {
                                    addDebugEntities(context, matchingBodies[i], DebugColor.GREEN);
                                }

                                matchingBodies = removeElementAt(matchingBodies, i);
                            }
                        }
                    }
                }

                var rotationType = Rotation.ROTATE_ALL;

                if (size(qEntityFilter(definition.groups[g].entities, EntityType.FACE)) != 0)
                {
                    rotationType = Rotation.ROTATE_Z;
                }

                aBoxResults = AlignedBoundingBoxFunction(context, id + g, {
                            "xyzOnly" : !definition.createBoundingBox,
                            "inputMethod" : InputMethod.ENTIRE_PART,
                            "entities" : definition.groups[g].entities,
                            "calculateFrom" : CalculateFrom.FIRST_FACE,
                            "mate" : qNothing(),
                            "rotation" : rotationType,
                            "quality" : Quality.FAST,
                            "tolerance" : 0.001 * inch,
                            "showMyWork" : false,
                            "offsets" : false,
                            "offset" : 0 * inch,
                            "intersectCurves" : false,
                            "keepBox" : definition.createBoundingBox,
                            "showResultBox" : true
                        });
            }


            var valueArray = [];
            //valueArray[0]  length
            //valueArray[1]  width
            //valueArray[2]  thickness
            //valueArray[3]  quantity
            //valueArray[4]  query
            //valueArray[5]  material

            var measure;
            var distance;
            var entities = [
                definition.groups[g].lengthEntities,
                definition.groups[g].widthEntities,
                definition.groups[g].thicknessEntities
            ];
            var matchFound = false;

            // Loop 3 times
            // X
            // Y
            // Z
            for (var i = 0; i < 3; i += 1)
            {
                try
                {
                    var colorEntity;
                    const entitiesThisLoop = entities[i];

                    if (definition.groups[g].method == Method.MEASURE && (definition.groups[g].lengthInputMethod == false && i == 0 ||
                                definition.groups[g].widthInputMethod == false && i == 1 ||
                                definition.groups[g].thicknessInputMethod == false && i == 2))
                    {
                        //single edge
                        if (size(evaluateQuery(context, entities[i])) == 1)
                        {
                            measure = evLength(context, {
                                        "entities" : entities[i]
                                    });
                            distance = measure;
                            colorEntity = entities[i];
                        }
                        else
                        {
                            var entity0 = evaluateQuery(context, entitiesThisLoop)[0];

                            const entity0EdgeLogic = size(evaluateQuery(context, qEntityFilter(entity0, EntityType.EDGE))) == 1;
                            const entity0PointLogic = size(evaluateQuery(context, qEntityFilter(entity0, EntityType.VERTEX))) == 1;
                            const entity0FaceLogic = size(evaluateQuery(context, qEntityFilter(entity0, EntityType.FACE))) == 1;
                            var entity0Origin;

                            var entity1 = evaluateQuery(context, entitiesThisLoop)[1];
                            const entity1EdgeLogic = size(evaluateQuery(context, qEntityFilter(entity1, EntityType.EDGE))) == 1;
                            const entity1PointLogic = size(evaluateQuery(context, qEntityFilter(entity1, EntityType.VERTEX))) == 1;
                            const entity1FaceLogic = size(evaluateQuery(context, qEntityFilter(entity1, EntityType.FACE))) == 1;
                            var entity1Origin;

                            //parallel to face distance
                            if (entity0FaceLogic == true)
                            {
                                const plane0 = evFaceTangentPlane(context, {
                                            "face" : entity0,
                                            "parameter" : vector(0.5, 0.5)
                                        });
                                entity0 = plane0;
                            }
                            if (entity1FaceLogic == true)
                            {
                                const plane1 = evFaceTangentPlane(context, {
                                            "face" : entity1,
                                            "parameter" : vector(0.5, 0.5)
                                        });
                                entity1 = plane1;
                            }

                            //__________________________________________________________________________________________________________________________

                            //Reference measure multiple by Jason G, Thanks Jason!
                            //https://cad.onshape.com/documents/ae5a7898d688af24f3831820/v/944bb5d5b4dcf1591689be91/e/d41b87348a35784df8a45f08
                            //__________________________________________________________________________________________________________________________
                            measure = evDistance(context, {
                                        "side0" : entity0,
                                        "side1" : entity1
                                    });

                            opFitSpline(context, id + g + i + "fitSpline", {
                                        "points" : [measure.sides[0].point, measure.sides[1].point]
                                    });
                            const spline = qCreatedBy(id + g + i + "fitSpline", EntityType.BODY);

                            distance = measure.distance;
                            colorEntity = spline;

                            toDelete = qUnion([toDelete, spline]);
                        }
                        //valueArray[1] equals width
                        //valueArray[2] equals thickness
                        //valueArray[3] equals quantity
                        //valueArray[4] equals query
                        //valueArray[5] equals material

                        if (i == 0)
                        {
                            // debug(context, colorEntity, DebugColor.RED);
                            addDebugEntities(context, colorEntity, DebugColor.RED);
                        }
                        if (i == 1)
                        {
                            // debug(context, colorEntity, DebugColor.GREEN);
                            addDebugEntities(context, colorEntity, DebugColor.GREEN);
                        }
                        if (i == 2)
                        {
                            // debug(context, colorEntity, DebugColor.BLUE);
                            addDebugEntities(context, colorEntity, DebugColor.BLUE);
                        }

                    }
                    else if (definition.groups[g].method == Method.AUTO)
                    {
                        if (i == 0)
                        {
                            distance = aBoxResults.x;
                        }
                        if (i == 1)
                        {
                            distance = aBoxResults.y;
                        }
                        if (i == 2)
                        {
                            distance = aBoxResults.z;
                        }
                    }

                    if (definition.groups[g].lengthInputMethod == true && i == 0 ||
                        definition.groups[g].widthInputMethod == true && i == 1 ||
                        definition.groups[g].thicknessInputMethod == true && i == 2)
                    {
                        if (i == 0)
                        {
                            distance = definition.groups[g].dx;
                        }
                        if (i == 1)
                        {
                            distance = definition.groups[g].dy;
                        }
                        if (i == 2)
                        {
                            distance = definition.groups[g].dz;
                        }
                    }


                    //offsets
                    if (definition.groups[g].offsets == false)
                    {
                        if (definition.initialLengthOffset != 0 && i == 0)
                        {
                            distance = distance + definition.initialLengthOffset;
                        }
                        if (definition.initialWidthOffset != 0 && i == 1)
                        {
                            distance = distance + definition.initialWidthOffset;
                        }
                        if (definition.initialThicknessOffset != 0 && i == 2)
                        {
                            distance = distance + definition.initialThicknessOffset;
                        }
                    }

                    if (definition.groups[g].offsets == true)
                    {
                        if (i == 0)
                        {
                            distance = distance + definition.groups[g].lengthOffsetG;
                        }
                        if (i == 1)
                        {
                            distance = distance + definition.groups[g].widthOffsetG;
                        }
                        if (i == 2)
                        {
                            distance = distance + definition.groups[g].thicknessOffsetG;
                        }
                    }
                }
                catch (error)
                {
                    distance = 0 * inch;
                }

                valueArray = append(valueArray, distance);

                // //Export variables
                // if (definition.exportVariables == true)
                // {
                //     if (i == 0) // X
                //     {
                //         setVariable(context, definition.prefixName ~ "_" ~ group.itemName ~ "_length", distance);
                //     }
                //     if (i == 1) // Y
                //     {
                //         setVariable(context, definition.prefixName ~ "_" ~ group.itemName ~ "_width", distance);
                //     }
                //     if (i == 2) // Z
                //     {
                //         setVariable(context, definition.prefixName ~ "_" ~ group.itemName ~ "_thickness", distance);
                //     }
                // }
            }

            if (!isQueryEmpty(context, toDelete))
            {
                opDeleteBodies(context, id + "deleteJunk", {
                            "entities" : toDelete
                        });
            }

            // Check for duplicate sizes
            var matchKey;

            for (var m, value in groups)
            {
                // Also checks if x and y are swapped
                const matchX = tolerantEquals(valueArray[0], groups[m][0]) || (tolerantEquals(valueArray[0], groups[m][1]) && tolerantEquals(valueArray[1], groups[m][0]));
                const matchY = tolerantEquals(valueArray[1], groups[m][1]) || (tolerantEquals(valueArray[0], groups[m][1]) && tolerantEquals(valueArray[1], groups[m][0]));
                const matchZ = tolerantEquals(valueArray[2], groups[m][2]);

                if (matchX && matchY && matchZ)
                {
                    groups[m][3] += 1;
                    matchFound = true;

                    // Add entities to existing group
                    groups[m][4] = qUnion([groups[m][4], definition.groups[g].entities]);
                }
            }

            // 3) Quantity
            if (definition.groups[g].quantity == 0)
            {
                valueArray = append(valueArray, 0);

                definition.exportVariables ?
                    setVariable(context, definition.prefixName ~ "_" ~ group.itemName ~ "_quantity", 0) :
                    false;
            }
            else
            {
                valueArray = append(valueArray, definition.addToQuantity + definition.groups[g].quantity);

                definition.exportVariables ?
                    setVariable(context, definition.prefixName ~ "_" ~ group.itemName ~ "_quantity", definition.addToQuantity + definition.groups[g].quantity) :
                    false;
            }

            const rowIdName = "(" ~ g + 1 ~ ")" ~ " " ~ definition.prefixName ~ " " ~ group.itemName;

            // 4) Add the query to the array
            var tableQuery = qUnion([definition.groups[g].entities, definition.groups[g].matchingEntities])->qOwnerBody();
            valueArray = append(valueArray, tableQuery);

            // 5) Material
            const rowMaterial = group.itemMaterial;
            valueArray = append(valueArray, rowMaterial);


            if (!matchFound)
            {
                groups[rowIdName] = valueArray;
            }
        }

        var count = 0;
        for (var group in groups)
        {
            count += 1;
            var thisArray = group.value;

            // Sort measure columns based on users' preferences
            var xyz = sort([thisArray[0], thisArray[1], thisArray[2]], function(a, b)
            {
                return b - a;
            });

            // Determine the order of indices for mapLongest and mapShortest
            var order = [0, 1, 2]; // Default order
            if (definition.mapLongest == MapLengths.COLUMN3)
            {
                order = [0, 1, 2];
                if (definition.mapShortest == MapLengths.COLUMN4)
                {
                    order = [0, 2, 1];
                }
            }
            else if (definition.mapLongest == MapLengths.COLUMN4)
            {
                order = [1, 0, 2];
                if (definition.mapShortest == MapLengths.COLUMN3)
                {
                    order = [2, 0, 1];
                }
            }
            else if (definition.mapLongest == MapLengths.COLUMN5)
            {
                order = [2, 1, 0];
                if (definition.mapShortest == MapLengths.COLUMN4)
                {
                    order = [1, 2, 0];
                }
            }

            // Apply the order to thisArray
            thisArray[0] = xyz[order[0]];
            thisArray[1] = xyz[order[1]];
            thisArray[2] = xyz[order[2]];

            if (definition.mapLongest == definition.mapShortest)
            {
                reportFeatureWarning(context, id, "WARNING: Can not map shortest and longest to the same column.");
            }

            //Export variables
            if (definition.exportVariables == true)
            {
                var groupName = "CutListItem";

                setVariable(context, definition.prefixName ~ "_" ~ groupName ~ count ~ "_" ~ definition.column3, thisArray[0]);
                setVariable(context, definition.prefixName ~ "_" ~ groupName ~ count ~ "_" ~ definition.column4, thisArray[1]);
                setVariable(context, definition.prefixName ~ "_" ~ groupName ~ count ~ "_" ~ definition.column5, thisArray[2]);
            }

            // ---------------------------------------------------------------------------------------------------------

            // Round to precision
            groups[group.key][0] = round(thisArray[0], definition.roundPrecision);
            groups[group.key][1] = round(thisArray[1], definition.roundPrecision);
            groups[group.key][2] = round(thisArray[2], definition.roundPrecision);

            // Table fractions
            if (definition.useFractionalInches)
            {
                groups[group.key][0] = decimalToFractionalInches(thisArray[0], definition.roundPrecision) ~ " in";
                groups[group.key][1] = decimalToFractionalInches(thisArray[1], definition.roundPrecision) ~ " in";
                groups[group.key][2] = decimalToFractionalInches(thisArray[2], definition.roundPrecision) ~ " in";
            }

            if (definition.setPartNames)
            {
                setProperty(context, {
                            "entities" : qOwnerBody(thisArray[4]),
                            "propertyType" : PropertyType.NAME,
                            "value" : definition.prefixName ~ definition.groups[count].itemName ~ " (" ~ groups[group.key][0] ~ " X " ~ groups[group.key][1] ~ " X " ~ groups[group.key][2] ~ ")"
                        });
            }

            if (definition.setPartDescriptionsToSize)
            {
                setProperty(context, {
                            "entities" : qOwnerBody(thisArray[4]),
                            "propertyType" : PropertyType.DESCRIPTION,
                            "value" : "(" ~ groups[group.key][0] ~ " X " ~ groups[group.key][1] ~ " X " ~ groups[group.key][2] ~ ")"
                        });
            }
            else if (definition.setPartDescriptions)
            {
                var matchingQuery = qUnion([definition.groups[g].entities, definition.groups[g].matchingEntities])->qOwnerBody();

                setProperty(context, {
                            "entities" : qOwnerBody(thisArray[4]),
                            "propertyType" : PropertyType.DESCRIPTION,
                            "value" : group.key
                        });
            }
        }

        //column ID's
        var valueArray2 = [];
        valueArray2 = append(valueArray2, definition.tableName);
        valueArray2 = append(valueArray2, definition.column1);
        valueArray2 = append(valueArray2, definition.column2);
        valueArray2 = append(valueArray2, definition.column3);
        valueArray2 = append(valueArray2, definition.column4);
        valueArray2 = append(valueArray2, definition.column5);

        const columnDataArray = getVariable(context, tableDataColumnVariableName, []);
        setVariable(context, tableDataColumnVariableName, append(columnDataArray, valueArray2));

        //Creates variable list
        const dataArray = getVariable(context, tableDataVariableName, []);
        setVariable(context, tableDataVariableName, append(dataArray, groups));
    });

export enum Method //Required to create a horizontal tab menu.
{
    annotation { "Name" : "Auto" }
    AUTO,
    annotation { "Name" : "Measure" }
    MEASURE,
}

export enum Method2 //Required to create a horizontal tab menu.
{
    annotation { "Name" : "Measure" }
    MEASURE,

    annotation { "Name" : "Manual" }
    MANUAL,
}

//__________________________________________________________________________________________________________________________

//Table code coppied from Neil Cooke's beam feature. Thank you Neil!
// https://cad.onshape.com/documents/e15c2c668d138f01242d0c80/w/0664d65a957c7bfba7cfbddd/e/2d5660fc1012df9598f00251
//__________________________________________________________________________________________________________________________

export enum SORT_COLUMNS
{
    annotation { "Name" : "Id / Column 1" }
    id,
    annotation { "Name" : "Quantity / Column 2" }
    quantity,
    annotation { "Name" : "X / Column 3" }
    length,
    annotation { "Name" : "Y / Column 4" }
    width,
    annotation { "Name" : "Z / Column 5" }
    thickness
}

export enum SORT_ORDER
{
    annotation { "Name" : "Ascending" }
    ascending,
    annotation { "Name" : "Descending" }
    descending
}

annotation { "Table Type Name" : "Measure cut list", "Icon" : icon::BLOB_DATA }
export const cutListTable = defineTable(function(context is Context, definition is map) returns TableArray
    precondition
    {
        annotation { "Name" : "Sort by", "UIHint" : "SHOW_LABEL" }
        definition.sortBy is SORT_COLUMNS;

        annotation { "Name" : "Sort order", "UIHint" : "SHOW_LABEL" }
        definition.sortOrder is SORT_ORDER;
    }
    {
        const dataArray = getVariable(context, tableDataVariableName);
        const columnDataArray = getVariable(context, tableDataColumnVariableName);
        var myTableArray = [];


        for (var i = 0; i < size(dataArray); i += 1)
        {
            //_________________________________________________________________
            //Gets the measured variables from the feature
            var variableList = dataArray[i];
            var columnId = columnDataArray[i];

            var varValTableTitle = columnId[0] ~ " (" ~ i + 1 ~ ")";
            var varValColumn1 = columnId[1];
            var varValColumn2 = columnId[2];
            var varValColumn3 = columnId[3];
            var varValColumn4 = columnId[4];
            var varValColumn5 = columnId[5];
            var varValColumn6 = "Material";

            //Creates an array of columns
            var columns = [
                tableColumnDefinition("id", varValColumn1),
                tableColumnDefinition("quantity", varValColumn2),
                tableColumnDefinition("length", varValColumn3),
                tableColumnDefinition("width", varValColumn4),
                tableColumnDefinition("thickness", varValColumn5),
                tableColumnDefinition("material", varValColumn6)
                // tableColumnDefinition("count", "Item Number"),
                // tableColumnDefinition("varVal", "Qty"),
            ];

            //Creates an array of rows
            var rows = [];

            for (var i = 0; i < size(variableList); i += 1)
            {
                //valueArray[0] equals length
                //valueArray[1] equals width
                //valueArray[2] equals thickness
                //valueArray[3] equals quantity
                //valueArray[4] equals query
                //valueArray[5] equals material

                var groupName = keys(variableList)[i];
                var varValLength = variableList[keys(variableList)[i]][0];
                var varValWidth = variableList[keys(variableList)[i]][1];
                var varValThickness = variableList[keys(variableList)[i]][2];
                var varValQuantity = variableList[keys(variableList)[i]][3];
                var varValQuery = variableList[keys(variableList)[i]][4];
                var varValMaterial = variableList[keys(variableList)[i]][5];

                rows = append(rows, tableRow({
                                // "count" : i + 1,
                                "id" : groupName,
                                "length" : varValLength,
                                "width" : varValWidth,
                                "thickness" : varValThickness,
                                "quantity" : varValQuantity,
                                "material" : varValMaterial
                            }, varValQuery));
            }

            //Returns the table
            // return table("Measurements", columns, rows);
            //_________________________________________________________________



            const order = definition.sortOrder == SORT_ORDER.ascending ? 1 : -1;

            rows = sort(rows, function(row1, row2)
                {
                    return order * sortRows(row1.columnIdToCell[toString(definition.sortBy)], row2.columnIdToCell[toString(definition.sortBy)]);
                });

            myTableArray = append(myTableArray, table(varValTableTitle, columns, rows));
        }

        return tableArray(myTableArray);
    });

function sortRows(row1, row2)
{
    if (row1 is TableCellError)
        row1 = row1.value;
    if (row2 is TableCellError)
        row2 = row2.value;

    if (row1 is ValueWithUnits || row1 is number)
    {
        return row1 - row2;
    }

    var sortMap = {};

    sortMap[row1] = true;
    sortMap[row2] = true;

    if (size(sortMap) == 1)
        return 0;

    if (keys(sortMap)[0] == row1)
        return -1;

    return 1;
}

export function measure(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, specifiedParameters is map) returns map
{
    definition = cadsharpUrlFunctionForPreExistingEditLogic(oldDefinition, definition);

    if (definition.usePartNames)
    {
        if (definition.updatePartNames != oldDefinition.updatePartNames || definition.usePartNames != oldDefinition.usePartNames)
        {
            definition.updatePartNames = oldDefinition.updatePartNames;

            for (var g = 0; g < size(definition.groups); g += 1)
            {
                var partName = getProperty(context, {
                        "entity" : qNthElement(definition.groups[g].entities, 0)->qOwnerBody(),
                        "propertyType" : PropertyType.NAME
                    });

                definition.groups[g].itemName = partName;
            }
        }
    }
    else if (!definition.usePartNames && definition.usePartNames != oldDefinition.usePartNames)
    {
        for (var g = 0; g < size(definition.groups); g += 1)
        {
            definition.groups[g].itemName = "Item";
        }
    }

    if (definition.usePartMaterials)
    {
        if (definition.updatePartMaterials != oldDefinition.updatePartMaterials || definition.usePartMaterials != oldDefinition.usePartMaterials)
        {
            definition.updatePartMaterials = oldDefinition.updatePartMaterials;

            for (var g = 0; g < size(definition.groups); g += 1)
            {
                try silent
                {
                    var partMaterial = getProperty(context, {
                            "entity" : qNthElement(definition.groups[g].entities, 0)->qOwnerBody(),
                            "propertyType" : PropertyType.MATERIAL
                        });

                    definition.groups[g].itemMaterial = partMaterial.name;
                }
                catch
                {
                    definition.groups[g].itemMaterial = undefined;
                }
            }
        }
    }
    else if (!definition.usePartMaterials && definition.usePartMaterials != oldDefinition.usePartMaterials)
    {
        for (var g = 0; g < size(definition.groups); g += 1)
        {
            definition.groups[g].itemMaterial = "----";
        }
    }

    if (arrayParameterChanges(oldDefinition.groups, definition.groups))
    {
        for (var g = 0; g < size(definition.groups); g += 1)
        {
            // if partQuery changes, and partQuery is a solid body which does not evaluate to nothing (i.e. a solid part is selected):
            // this condition is used because we don't need to update the material unless the user changes which parts they've selected
            if (oldDefinition.groups[g].lengthInputMethod != definition.groups[g].lengthInputMethod)
            {
                definition.groups[g].lengthInputReturned = definition.groups[g].lengthInputMethod;
            }

            if (oldDefinition.groups[g].widthInputMethod != definition.groups[g].widthInputMethod)
            {
                definition.groups[g].widthInputReturned = definition.groups[g].widthInputMethod;
            }

            if (oldDefinition.groups[g].thicknessInputMethod != definition.groups[g].thicknessInputMethod)
            {
                definition.groups[g].thicknessInputReturned = definition.groups[g].thicknessInputMethod;
            }
        }
    }

    for (var g = 0; g < size(definition.groups); g += 1)
    {
        if (definition.prefixName != "" || definition.prefixName != " " || definition.prefixName != "  ")
        {
            definition.groups[g].name = definition.prefixName ~ " " ~ definition.groups[g].itemName
                ~ " (" ~ toString(definition.addToQuantity + definition.groups[g].quantity) ~ "X)";
        }
        if (definition.prefixName == "" || definition.prefixName == " " || definition.prefixName == "  ")
        {
            definition.groups[g].name = definition.groups[g].itemName
                ~ " (" ~ toString(definition.addToQuantity + definition.groups[g].quantity) ~ "X)";
        }
        if (definition.groups[g].quantity == 0)
        {
            definition.groups[g].name = definition.groups[g].itemName
                ~ " (" ~ toString(0) ~ "X)";
        }
    }

    return definition;
}

