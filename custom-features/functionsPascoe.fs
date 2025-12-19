FeatureScript 1948;
import(path : "onshape/std/common.fs", version : "1948.0");

// annotation { "Feature Type Name" : "My Feature" }
// export const myFeature = defineFeature(function(context is Context, id is Id, definition is map)
//     precondition
//     {
//         annotation { "Name" : "Boolean enum", "UIHint" : UIHint.HORIZONTAL_ENUM }
//         definition.booleanEnum is BooleonScopeEnumPascoe;

//     }
//     {
//         // Define the function's action
//     });


export function BooleanFunctionPascoe(context is Context, id is Id, booleanScope, tools, mergeScope, keepTools)
{
    if (booleanScope != "SPLIT")
    {
        if (booleanScope != "NEW")
        {
            var boolType;
            var boolDef;

            if (booleanScope == "SUBTRACT")
            {
                //debug(context, tools, DebugColor.YELLOW); //Show what is being removed

                boolType = BooleanOperationType.SUBTRACTION;
                boolDef = {
                        "tools" : tools,
                        "targets" : mergeScope,
                        "operationType" : boolType,
                        "targetsAndToolsNeedGrouping" : true,
                        "keepTools" : true
                    };
            }

            else if (booleanScope == "ADD")
            {
                boolType = BooleanOperationType.UNION;

                if (size(evaluateQuery(context, mergeScope)) != 0)
                {
                    boolDef = {
                            "tools" : tools,
                            "targets" : mergeScope,
                            "operationType" : boolType,
                            "targetsAndToolsNeedGrouping" : true
                        };
                }
                else
                {
                    boolDef = {
                            "tools" : qSubtraction(tools, evaluateQuery(context, tools)[0]),
                            "targets" : evaluateQuery(context, tools)[0],
                            "operationType" : boolType,
                            "targetsAndToolsNeedGrouping" : true
                        };
                }
            }
            else if (booleanScope == "INTERSECT")
            {
                //Reference Onshape library source
                //https://cad.onshape.com/documents/12312312345abcabcabcdeff/w/a855e4161c814f2e9ab3698a/e/4eea9f271b60454e84089e38

                opPattern(context, id + "bodyCopy", {
                            "entities" : mergeScope,
                            "transforms" : [identityTransform()],
                            "instanceNames" : ["copy"]
                        });

                const copy = qCreatedBy(id + "bodyCopy", EntityType.BODY);

                // debug(context, tools, DebugColor.YELLOW); //Show what is being intersected

                boolType = BooleanOperationType.SUBTRACT_COMPLEMENT;
                boolDef = {
                        "tools" : tools,
                        "targets" : copy,
                        "operationType" : boolType,
                        "targetsAndToolsNeedGrouping" : true,
                        "keepTools" : true
                    };
            }

            try
            {
                opBoolean(context, id + "boolean999", boolDef);
            }
        }
    }
    else
    {
        const faceTools = qOwnedByBody(tools, EntityType.FACE);

        //Reference: Wood Grain by Tim Rice, Thanks Tim!
        //https://cad.onshape.com/documents/f5cd9f4b2ec8e9eea7266f1e/v/7b36726274b62ee2f6b6f979/e/4626b80fb148952bd0b75c92

        opSplitFace(context, id + "splitFace1", {
                    "faceTargets" : qEntityFilter(mergeScope, EntityType.FACE),
                    "faceTools" : faceTools
                });
    }
}

export function qExternalFaces(context, id, body)
{
    opPoint(context, id + "point1", {
                "point" : vector(10000, 0, 0) * inch
            });

    const veryFarAway = vector(10000, 0, 0) * inch;
    const allPartFaces = qAdjacent(body, AdjacencyType.VERTEX, EntityType.FACE);
    const allPartFacesArray = evaluateQuery(context, allPartFaces);
    const closestExternalFace = qClosestTo(allPartFaces, veryFarAway);

    var externalFaces = closestExternalFace;
    var facesToSearch = [closestExternalFace];
    var alreadySearched = qNothing();

    for (var i = 0; i < size(facesToSearch); i += 1)
    {
        const thisFace = facesToSearch[i];

        alreadySearched = qUnion([alreadySearched, thisFace]);

        var adjFaces = qAdjacent(thisFace, AdjacencyType.EDGE, EntityType.FACE);
        adjFaces = qSubtraction(adjFaces, alreadySearched);
        externalFaces = qUnion([externalFaces, adjFaces]);
        const adjFacesArray = evaluateQuery(context, adjFaces);

        facesToSearch = concatenateArrays([facesToSearch, adjFacesArray]);
    }

    return externalFaces;
}

export function qInternalFaces(context, id, body)
{
    opPoint(context, id + "point1", {
                "point" : vector(10000, 0, 0) * inch
            });

    const veryFarAway = vector(10000, 0, 0) * inch;
    const allPartFaces = qAdjacent(body, AdjacencyType.VERTEX, EntityType.FACE);
    const allPartFacesArray = evaluateQuery(context, allPartFaces);
    const closestExternalFace = qClosestTo(allPartFaces, veryFarAway);

    var externalFaces = closestExternalFace;
    var facesToSearch = [closestExternalFace];
    var alreadySearched = qNothing();

    for (var i = 0; i < size(facesToSearch); i += 1)
    {
        const thisFace = facesToSearch[i];

        alreadySearched = qUnion([alreadySearched, thisFace]);

        var adjFaces = qAdjacent(thisFace, AdjacencyType.EDGE, EntityType.FACE);
        adjFaces = qSubtraction(adjFaces, alreadySearched);
        externalFaces = qUnion([externalFaces, adjFaces]);
        const adjFacesArray = evaluateQuery(context, adjFaces);

        facesToSearch = concatenateArrays([facesToSearch, adjFacesArray]);
    }

    const internalFaces = qSubtraction(allPartFaces, externalFaces);

    return internalFaces;
}

export function qClosestVector(vectors is array, target is Vector)
{
    var closest = undefined;
    var closestDist = undefined;

    for (var v in vectors)
    {
        var d = v - target;
        var dist2 = dot(d, d); // squared distance

        if (closest == undefined || dist2 < closestDist)
        {
            closest = v;
            closestDist = dist2;
        }
    }

    return closest;
}



