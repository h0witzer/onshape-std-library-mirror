FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/extendendtype.gen.fs", version : "2909.0");

import(path : "9c4e6800da09c31ac968b6c1", version : "3c04376b78cee7e5337e5b7d");//4dShared.fs

export enum FINISHING_OPTIONS
{
    annotation { "Name" : "Composite" }
    COMPOSITE,
    annotation { "Name" : "Boolean union" }
    BOOL
}

annotation { "Feature Type Name" : "Finish 4D fold", "Feature Type Description" : "" }
export const finish4DFold = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        //Should put a selection into here to handle multiple bent parts in the same part studio. Along with futher changes of qAll in some areas.

        annotation { "Name" : "Finishing" }
        definition.finishing is FINISHING_OPTIONS;

    }
    {
        const allPartsArray = evaluateQuery(context, qAllNonMeshSolidBodies());

        const foldData = getFoldAttributedData(context, allPartsArray);

        const sortedFolds = sortUnfolds(context, id, foldData.hingeMap, foldData.foldPartsArray);

        const translations = performUnfolds(context, id, foldData.hingeMap, sortedFolds, vector(0, 0, 0) * millimeter);

        opUnWrapHinge(context, id + "unwrap", foldData.hingeMap, translations);

        if (definition.finishing == FINISHING_OPTIONS.COMPOSITE)
        {
            opModifyCompositePart(context, id + "addNewHinges", {
                        "composite" : qCompositePartsContaining(qUnion(allPartsArray)),
                        "toAdd" : qCreatedBy(id, EntityType.BODY),
                        "toRemove" : qNothing()
                    });
        }
        else
        {
            opDeleteBodies(context, id + "deleteComposite", { "entities" : qCompositePartsContaining(qUnion(allPartsArray)) });
            opBoolean(context, id + "booleanAll", {
                        "tools" : qAllNonMeshSolidBodies(),
                        "operationType" : BooleanOperationType.UNION
                    });
        }
    });


function opUnWrapHinge(context is Context, id is Id, hingeMap is map, translations is map)
{
    const basePlane = hingeMap[[]][0].basePlane;

    var i = 0;
    for (var mapData in hingeMap)
    {
        for (var j, item in mapData.value)
        {
            const localId = id + i + j;
            const hingeData = getHingeData(context, item.entity);

            const hingeRotationAngle = item.rotationAngle;

            const pointId = localId + "point";
            opPoint(context, pointId, { "point" : hingeData.cylinderDef.coordSystem.origin });
            const pointQuery = qCreatedBy(pointId, EntityType.BODY);
            const pointInFront = qInFrontOfPlane(pointQuery, basePlane);

            const directionCheck = isQueryEmpty(context, pointInFront);
            const planeNormal = directionCheck ? basePlane.normal : -1 * basePlane.normal;
            opDeleteBodies(context, localId + "deletePoint", { "entities" : pointQuery });

            const wrapBasePlane = plane(basePlane.origin, planeNormal, basePlane.x);
            const wrapBaseFace = directionCheck ? hingeData.faces.inner : hingeData.faces.outer;

            const wrapTopPlane = plane(basePlane.origin + basePlane.normal * hingeData.thickness, planeNormal, basePlane.x);
            const wrapTopFace = !directionCheck ? hingeData.faces.inner : hingeData.faces.outer;

            const baseWrapSheetData = opPerformUnwrap(context, localId + "baseWrap", {
                        "cylFace" : wrapBaseFace,
                        "wrapPlane" : wrapBasePlane,
                        "anchorDirection" : hingeData.cylinderDef.coordSystem.zAxis
                    });

            const topWrapSheetData = opPerformUnwrap(context, localId + "upperWrap", {
                        "cylFace" : wrapTopFace,
                        "wrapPlane" : wrapTopPlane,
                        "anchorDirection" : hingeData.cylinderDef.coordSystem.zAxis
                    });

            const targetWidth = (baseWrapSheetData.crossDistance + topWrapSheetData.crossDistance) / 2;

            const scaleFactorTop = baseWrapSheetData.crossDistance / targetWidth;
            const scaleFactorBottom = topWrapSheetData.crossDistance / targetWidth;

            const baseConnectionPoints = scaleAndReturnConnectionPoints(context, localId + "baseScale", {
                        "entity" : baseWrapSheetData.surface,
                        "scale" : scaleFactorTop,
                        "cSys" : topWrapSheetData.cSys
                    });

            const topConnectionPoints = scaleAndReturnConnectionPoints(context, localId + "topScale", {
                        "entity" : topWrapSheetData.surface,
                        "scale" : scaleFactorBottom,
                        "cSys" : topWrapSheetData.cSys
                    });

            var connectionEntitiesArray = [];
            for (var point in evaluateQuery(context, baseConnectionPoints))
            {
                const closestPoint = qClosestTo(topConnectionPoints, evVertexPoint(context, { "vertex" : point }));
                connectionEntitiesArray = append(connectionEntitiesArray, qUnion(point, closestPoint));
            }

            // This loft produce some non-accurate unfolded shapes but works okay for the purpose of hinges
            opLoft(context, localId + "loft", {
                        "profileSubqueries" : [baseWrapSheetData.surface, topWrapSheetData.surface],
                        "connections" : [
                            { "connectionEntities" : connectionEntitiesArray[0], "connectionEdges" : [], "connectionEdgeParameters" : [] },
                            { "connectionEntities" : connectionEntitiesArray[1], "connectionEdges" : [], "connectionEdgeParameters" : [] },
                            { "connectionEntities" : connectionEntitiesArray[2], "connectionEdges" : [], "connectionEdgeParameters" : [] },
                            { "connectionEntities" : connectionEntitiesArray[3], "connectionEdges" : [], "connectionEdgeParameters" : [] }]
                    });

            opDeleteBodies(context, localId + "deleteSurfaces", { "entities" : qUnion(baseWrapSheetData.surface, topWrapSheetData.surface) });

            if (!directionCheck) // Only do this for folds upwards
            {
                const nearSplitPlane = plane(topWrapSheetData.anchorPoint, cross(-planeNormal, topWrapSheetData.cSys.xAxis));

                opSplitPart(context, localId + "splitNear", {
                            "targets" : item.entity,
                            "tool" : nearSplitPlane
                        });

                const splitParts = qOwnerBody(qCreatedBy(localId + "splitNear"));
                const nearPart = qInFrontOfPlane(splitParts, nearSplitPlane);
                const resultingHinge = qSubtraction(splitParts, nearPart);

                const resultingHingeData = getHingeData(context, resultingHinge);

                const innerFaceParallelEdges = qParallelEdges(qLoopEdges(resultingHingeData.faces.inner), topWrapSheetData.cSys.xAxis);
                const closestEdge = qClosestTo(innerFaceParallelEdges, topWrapSheetData.anchorPoint);
                const furthestAwayEdge = qSubtraction(innerFaceParallelEdges, closestEdge);
                const faceTangentPlaneAtEdge = evFaceTangentPlaneAtEdge(context, {
                            "edge" : furthestAwayEdge,
                            "face" : resultingHingeData.faces.inner,
                            "parameter" : 0
                        });

                const farSplitPlane = plane(faceTangentPlaneAtEdge.origin, cross(faceTangentPlaneAtEdge.normal, topWrapSheetData.cSys.xAxis));

                opSplitPart(context, localId + "splitFar", {
                            "targets" : resultingHinge,
                            "tool" : farSplitPlane,
                            "keepType" : SplitOperationKeepType.KEEP_BACK // I'm not sure if this is 100% safe to assume but works for now
                        });

                const splitFarBody = qOwnerBody(qCreatedBy(localId + "splitFar"));
                const rotationLine = line(resultingHingeData.cylinderDef.coordSystem.origin, topWrapSheetData.cSys.xAxis);

                const basicTransform = transform(targetWidth * nearSplitPlane.normal) * rotationAround(rotationLine, hingeRotationAngle);

                opTransform(context, localId + "transformSpritFar", {
                            "bodies" : splitFarBody,
                            "transform" : basicTransform
                        });
            }
            else
            {
                opDeleteBodies(context, localId + "deleteOppositeFoldHinge", { "entities" : item.entity });
            }

        }

        i += 1;
    }
}

//item.entity
function getHingeData(context is Context, body is Query) returns map
{
    const allCylinderFaces = qGeometry(qOwnedByBody(body, EntityType.FACE), GeometryType.CYLINDER);
    const cylinderFacesArray = evaluateQuery(context, allCylinderFaces);

    const face0 = cylinderFacesArray[0];
    const face1 = cylinderFacesArray[1];
    const face0cylDef = evSurfaceDefinition(context, { "face" : face0 });
    const face1cylDef = evSurfaceDefinition(context, { "face" : face1 });

    const hingeThickness = abs(face0cylDef.radius - face1cylDef.radius);

    const cylinderFaces = (face0cylDef.radius > face1cylDef.radius) ? { "outer" : face0, "inner" : face1 } : { "outer" : face1, "inner" : face0 };

    return { "faces" : cylinderFaces, "thickness" : hingeThickness, "cylinderDef" : face0cylDef };
}


/**
 * Need to scale the upper and lower sheets as one is extanded and one is compressed when bent.
 */
function scaleAndReturnConnectionPoints(context is Context, id is Id, definition is map) returns Query
{
    opTransform(context, id, {
                "bodies" : definition.entity,
                "transform" : scaleNonuniformly(1, 1 / definition.scale, 1, definition.cSys)
            });

    const parallellEdges = qParallelEdges(qOwnedByBody(definition.entity, EntityType.EDGE), definition.cSys.xAxis);

    if (size(evaluateQuery(context, parallellEdges)) != 2)
    {
        throw "Something off with the wrapping";
    }

    return qUnion(qEdgeVertex(parallellEdges, true), qEdgeVertex(parallellEdges, false));
}

/**
 * Make the wrap of a cylindrical face onto a plane. Cylinder needs to have at least one point that lie on the plane.
 */
function opPerformUnwrap(context is Context, id is Id, definition is map) returns map
{
    const cylFaceEdges = qLoopEdges(definition.cylFace);
    const cylFaceVerticies = qUnion(qEdgeVertex(cylFaceEdges, true), qEdgeVertex(cylFaceEdges, false)); //Probably some better than this
    const anchorPointQuery = qNthElement(qCoincidesWithPlane(cylFaceVerticies, definition.wrapPlane), 0);
    const anchorPoint = evVertexPoint(context, { "vertex" : anchorPointQuery });

    const destination = makeWrapPlane(definition.wrapPlane, anchorPoint, definition.anchorDirection); //makeWrapPlane(firstBasePlane);
    const source = makeWrapCylinder(context, definition.cylFace, anchorPoint, definition.anchorDirection);

    opWrap(context, id, {
                "wrapType" : WrapType.SIMPLE,
                "entities" : definition.cylFace,
                "source" : source,
                "destination" : destination
            });

    const wrapSurface = qCreatedBy(id, EntityType.BODY);

    const cSys = coordSystem(anchorPoint, definition.anchorDirection, definition.wrapPlane.normal);
    const bBox = evBox3d(context, {
                "topology" : qCreatedBy(id, EntityType.BODY),
                "tight" : false,
                "cSys" : cSys
            });

    const crossDistance = bBox.minCorner[1] - bBox.maxCorner[1];

    return { "surface" : wrapSurface, "crossDistance" : crossDistance, "cSys" : cSys, "anchorPoint" : anchorPoint };
}


/**
 * Gather up the parts that have fold attribute data, separate out base parts into an array and hinge parts into a map with the key as the partId.
 * Multiple parts with the same key get addet to an array.
 */
function getFoldAttributedData(context is Context, allPartsArray is array) returns map
{
    var allHingesMap = {};
    var foldSpec = [];

    for (var part in allPartsArray)
    {
        var foldData = getAttribute(context, { "entity" : part, "name" : UNFOLD_ATTRIBUTE });

        if (!isUndefinedOrEmptyString(foldData))
        {
            if (foldData.partType == PartType.MAIN)
            {
                foldData.entity = part;
                foldData.retainedId = foldData.partId;
                foldSpec = append(foldSpec, foldData);
            }
            else
            {
                foldData.entity = part;

                allHingesMap[foldData.partId] = (isUndefinedOrEmptyString(allHingesMap[foldData.partId])) ? [foldData] : append(allHingesMap[foldData.partId], foldData);
            }
        }
    }

    return { "hingeMap" : allHingesMap, "foldPartsArray" : foldSpec };
}

/**
 * Perform the opTransform to unfold the parts. The living hinges are in the correct place but stil in bent shape.
 */
function performUnfolds(context is Context, id is Id, allHingesMap is map, sortedFolds is array, parentTranslation is Vector) returns map
{
    var translationMap = {};
    for (var i, fold in sortedFolds)
    {
        const rotationLine = line(fold.rotationLine.origin + parentTranslation, fold.rotationLine.direction);

        const transform = transform(fold.translationVector) * rotationAround(rotationLine, -fold.rotationAngle);

        opTransform(context, id + i + "transform", {
                    "bodies" : fold.entity,
                    "transform" : transform
                });

        const nextParentTranslation = parentTranslation + fold.nudgeVector + fold.translationVector;

        translationMap[fold.retainedId] = fold.translationVector;

        if (!isUndefinedOrEmptyString(fold.child))
        {
            const childFolds = performUnfolds(context, id + i, allHingesMap, fold.child, nextParentTranslation);
            translationMap = mergeMaps(translationMap, childFolds);
        }
    }

    return translationMap;
}

/**
 * Data coming in from the attribute gathering is not sorted into a nested array/map set, sorts this according to fold order dependency.
 */
function sortUnfolds(context is Context, id is Id, allHingesMap is map, foldSpec is array) returns array
{
    var i = 0;
    var parentFolds = [];
    var carryOn = true;
    while (carryOn)
    {
        var thisBranchChildren = [];
        var thisBranchParent;

        for (var fold in foldSpec)
        {
            const localId = fold.partId[0];

            if (fold.partType == PartType.MAIN)
            {
                if (localId == i && size(fold.partId) > 1)
                {
                    fold.partId = removeElementAt(fold.partId, 0);

                    thisBranchChildren = append(thisBranchChildren, fold);
                }
                else if (localId == i && size(fold.partId) == 1)
                {
                    thisBranchParent = fold;
                }
            }
        }

        if (size(thisBranchChildren) == 1)
        {
            thisBranchParent.child = thisBranchChildren;
            const thisParentHingesArray = allHingesMap[thisBranchParent.retainedId];

            var hingeParts = qNothing();
            for (var hinge in thisParentHingesArray)
            {
                hingeParts = qUnion(hingeParts, hinge.entity);
            }

            thisBranchParent.entity = qUnion([thisBranchParent.entity, thisBranchChildren[0].entity, hingeParts]);
            parentFolds = append(parentFolds, thisBranchParent);
        }
        else if (isUndefinedOrEmptyString(thisBranchParent))
        {
            carryOn = false;
        }
        else
        {
            thisBranchParent.child = sortUnfolds(context, id + i, allHingesMap, thisBranchChildren);

            for (var child in thisBranchParent.child)
            {
                const thisParentHingesArray = allHingesMap[thisBranchParent.retainedId];

                var hingeParts = qNothing();
                for (var hinge in thisParentHingesArray)
                {
                    hingeParts = qUnion(hingeParts, hinge.entity);
                }

                thisBranchParent.entity = qUnion([thisBranchParent.entity, child.entity, hingeParts]);
            }

            parentFolds = append(parentFolds, thisBranchParent);
        }

        i += 1;
    }
    return parentFolds;
}
