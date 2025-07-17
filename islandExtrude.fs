

FeatureScript 1403;
import(path : "onshape/std/geometry.fs", version : "1403.0");

// CADSharp
export import(path : "cbeb3dcf671e00785597bd76/409d65a3744fe434f32bdffc/a75ab01def146a42f55baa7f", version : "381046010d5aea697e433948");

icon::import(path : "ee897d51881fbe2005fcb7d9", version : "442fb32a1851ab7b8f437b7c");

annotation {
        "Feature Type Name" : "Island Extrude",
        "Icon" : icon::BLOB_DATA,
        "Feature Type Description" : "<br> <b>Summary</b> <br> Extrudes and offset island from the original face.",
        "Description Image" : cadsharpLogo::BLOB_DATA,
        "Editing Logic Function" : "cadsharpUrlEditLogic"
    }
export const islandExtrude = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        //Required to create a horizontal tab menu.
        annotation { "Name" : "Operation Type", "UIHint" : "HORIZONTAL_ENUM" }
        definition.operationType is OperationType;

        annotation { "Name" : "Face", "Filter" : EntityType.FACE }
        definition.face is Query;

        annotation { "Name" : "Offset Width" }
        isLength(definition.offsetWidth, { (inch) : [-1e5, 1, 1e5] } as LengthBoundSpec);

        annotation { "Name" : "Opposite Direction Width", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.oppositeDirectionWidth is boolean;

        annotation { "Name" : "Offset Depth" }
        isLength(definition.offsetDepth, { (inch) : [-1e5, .5, 1e5] } as LengthBoundSpec);

        annotation { "Name" : "Opposite Direction Depth", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.oppositeDirectionDepth is boolean;

        cadsharpUrlPredicate(definition);
    }
    {
        var evalFaces = evaluateQuery(context, definition.face);
        //var queryAdjacentFaces;
        var qtyFaces = size(evalFaces);
        var queryExtrudedBody;
        var queryOwnerBody;

        for (var i = 0; i < qtyFaces; i += 1)
        {

            var evalPlane = evFaceTangentPlane(context, {
                    "face" : evalFaces[i],
                    "parameter" : vector(0.5, 0.5)
                });

            var subtractLogic = definition.operationType == OperationType.SUBTRACT ? -1 : 1;

            extrude(context, id + i + "extrude", {
                        "entities" : evalFaces[i],
                        "endBound" : BoundingType.BLIND,
                        "depth" : (definition.oppositeDirectionDepth ? -1 * subtractLogic : 1 * subtractLogic) * definition.offsetDepth
                    });

            var ownerBody = qOwnerBody(evalFaces[i]);
            var extrudedBody = qCreatedBy(id + i + "extrude", EntityType.BODY);
            var extrudedFaces = qCreatedBy(id + i + "extrude", EntityType.FACE);
            var topBottomFace = qParallelPlanes(extrudedFaces, evalPlane);
            var evalTopBottom = evaluateQuery(context, topBottomFace);

            var adjacentFaces = qAdjacent(evalTopBottom[0], AdjacencyType.EDGE, EntityType.FACE);

            opOffsetFace(context, id + i + "offsetFace1", {
                        "moveFaces" : adjacentFaces,
                        "offsetDistance" : definition.oppositeDirectionWidth ? definition.offsetWidth : -definition.offsetWidth
                    });

            //queryAdjacentFaces = qUnion([queryAdjacentFaces, adjacentFaces]);
            queryExtrudedBody = i == 0 ? extrudedBody : qUnion([queryExtrudedBody, extrudedBody]);
            queryOwnerBody = i == 0 ? ownerBody : qUnion([queryOwnerBody, ownerBody]);
        }



        if (definition.operationType == OperationType.ADD)
        {
            opBoolean(context, id + "boolean1", {
                        "tools" : qUnion([queryOwnerBody, queryExtrudedBody]),
                        "operationType" : BooleanOperationType.UNION
                    });

        }

        if (definition.operationType == OperationType.SUBTRACT)
        {
            opBoolean(context, id + "boolean1", {
                        "tools" : queryExtrudedBody,
                        "targets" : queryOwnerBody,
                        "operationType" : BooleanOperationType.SUBTRACTION
                    });
        }


    });


//Required to create a horizontal tab menu.
export enum OperationType
{
    annotation { "Name" : "New" }
    NEW,

    annotation { "Name" : "Add" }
    ADD,

    annotation { "Name" : "Subtract" }
    SUBTRACT

}
