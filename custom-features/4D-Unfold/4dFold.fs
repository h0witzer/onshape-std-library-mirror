FeatureScript 2909;

import(path : "onshape/std/common.fs", version : "2909.0");

import(path : "377708296177d214c08be84f", version : "5ad8acdcd262901fffc03e7c");//4dGUI.fs
import(path : "5dec8d1bfbe93f72ac65c85e", version : "80d79d2310406cf26a5b1df5");//4dUnfold.fs
import(path : "82d9b2605f1b760623b556d2", version : "ee0638812877161cb71a927c");//4dFoldCleanup.fs

const foldErrorMessage = "Living hinge error, make hinge thinner or bend radius larger.";

annotation { "Feature Type Name" : "4D fold model" }
export const make4DFoldModel = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        userInput(definition);
    }
    {
        const facesArray = evaluateQuery(context, definition.faces);
        definition.seenFaces = [facesArray[0]];

        const firstFace = facesArray[0];
        const foldArray = getFolds(context, definition, firstFace);

        definition.basePlane = evPlane(context, { "face" : firstFace });

        for (var i, face in facesArray)
        {
            const extrudId = id + "nominalExtrude" + i;
            opExtrude(context, extrudId, {
                        "entities" : face,
                        "direction" : evOwnerSketchPlane(context, { "entity" : face }).normal,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : definition.nominalThickness
                    });

            const nonCapFaces = qNonCapEntity(extrudId, EntityType.FACE);

            setAttribute(context, {
                        "entities" : nonCapFaces,
                        "name" : TAG_FACES_ATTRIBUTE,
                        "attribute" : true
                    });
        }

        definition.defaultBendOffset = getBendOffsets(context, id, definition, definition.defaultFoldAngle);
        if (definition.defaultBendOffset.error)
        {
            reportFeatureWarning(context, id, foldErrorMessage);
            return;
        }

        const bendData = makeFoldReliefAndLivingHinge(context, id, definition, foldArray);
        if (bendData.error)
        {
            reportFeatureWarning(context, id, foldErrorMessage);
            return;
        }

        if (!definition.showUnfolded)
        {
            const startId = [];
            for (var i, hinge in bendData.thisOrderLivingHinges)
            {
                tagPartForUnfold(context, hinge, {
                            "partType" : PartType.HINGE,
                            "transform" : bendData.bendTransformVectors[i],
                            "rotationAngle" : foldArray[i].foldAngle,
                            "translationVector" : bendData.nudgeBackVectors[i] - bendData.bendTransformVectors[i],
                            "partId" : startId,
                            "basePlane" : definition.basePlane
                        });
            }

            performFolds(context, id + "fold", definition, bendData, foldArray, startId);

            opTruncateSlaveEdges(context, id, definition, bendData);

            opCleanUpLivingHinges(context, id + "cleanUpHinges", definition, bendData);
        }
        else
        {
            reportFeatureInfo(context, id, "Do not leave the model in unfolded state, use the Finish 4D fold feature instead.");
        }

        opCreateCompositePart(context, id + "compositePart", { "bodies" : qCreatedBy(id, EntityType.BODY), "closed" : true });
    });

/**
 * Reverse order of folding the sections up to the folded state.
 */
function performFolds(context is Context, id is Id, definition is map, bendData is map, foldArray is array, partId is array) returns Query
{
    var bentParts = qNothing();

    for (var i = size(foldArray) - 1; i > -1; i -= 1)
    {
        const thisPartId = append(partId, i);

        var foldData = performFolds(context, id + i, definition, bendData.childBendData[i], foldArray[i].childFolds, thisPartId);

        const childLivingHinges = qUnion(bendData.childBendData[i].thisOrderLivingHinges);

        const thisPart = bendData.endBodies[i];
        const thisPartHinges = qUnion(bendData.thisOrderLivingHinges);

        const bendNow = qUnion([foldData, thisPart, childLivingHinges]);

        const rotationTransform = rotationAround(bendData.bentLivingEdgeRotationLines[i], foldArray[i].foldAngle);

        const translationTransform = transform(bendData.bendTransformVectors[i]);

        opTransform(context, id + i + "bend", {
                    "bodies" : bendNow,
                    "transform" : rotationTransform * translationTransform
                });

        tagPartForUnfold(context, thisPart, {
                    "partType" : PartType.MAIN,
                    "rotationLine" : bendData.bentLivingEdgeRotationLines[i],
                    "rotationAngle" : foldArray[i].foldAngle,
                    "translationVector" : bendData.nudgeBackVectors[i] - bendData.bendTransformVectors[i],
                    "nudgeVector" : bendData.bendTransformVectors[i],
                    "partId" : thisPartId
                });

        tagPartForUnfold(context, childLivingHinges, {
                    "partType" : PartType.HINGE,
                    "partId" : thisPartId,
                    "basePlane" : definition.basePlane
                });

        for (var hinge in bendData.thisOrderLivingHinges)
        {
            tagPartForUnfold(context, hinge, {
                        "rotationAngle" : foldArray[i].foldAngle,
                        "partId" : partId,
                    });
        }

        bentParts = qUnion(bentParts, bendNow);
    }

    return bentParts;
}

/**
 * Calculate the face offsets per side for the living hinge.
 */
function getBendOffsets(context is Context, id is Id, definition is map, foldAngle is ValueWithUnits) returns map
{
    const centerlineRadius = definition.livingHingeOuterRadius - definition.livingHingeThickness / 2;

    const bendCenterLineLength = foldAngle * centerlineRadius / radian;

    const startFaceOffset = definition.livingHingeOuterRadius * sqrt((1 - cos(foldAngle)) / (1 + cos(foldAngle)));

    const endFaceOffset = bendCenterLineLength - startFaceOffset;

    if (endFaceOffset < 0 * millimeter)
        return { "error" : true };

    return { "startFaceOffset" : startFaceOffset, "endFaceOffset" : endFaceOffset, "error" : false };
}

/**
 * Creates the chamfered edges to make room for the fold. Makes the hinges. And stres all information in nested map of child-bends.
 * TODO: This is a beast of a function. Could be useful to refactor at some point.
 */
function makeFoldReliefAndLivingHinge(context is Context, id is Id, definition is map, foldArray is array) returns map
{
    var bentLivingEdgeRotationLines = [];
    var bendTransformVectors = [];
    var endBodies = [];
    var childBendData = [];
    var allFoldedParts = qNothing();
    var thisOrderLivingHinges = [];
    var startBodyExternalQuery = qNothing();
    var endBodiesExternalTracker = [];
    var endFaceNudgeBackVectors = [];

    for (var j, fold in foldArray)
    {
        //---------- GATHER UP THE FOLD SETTINGS

        var foldAngle = definition.defaultFoldAngle;
        var bendOffset = definition.defaultBendOffset;
        if (!fold.defaultBend)
        {
            foldAngle = fold.foldAngle;
            bendOffset = getBendOffsets(context, id, definition, foldAngle);

            if (bendOffset.error)
                return { "error" : true };

        }

        const foldDirectionFlipMultiplier = switch (fold.flipFoldDirection) {
                    true : -1,
                    false : 1
                };

        //---------- GATHER BODY AND FACE DATA

        const foldFaces = qEntityFilter(qUnion(evaluateQuery(context, fold.edgeTracking)), EntityType.FACE);
        const foldFacesArray = evaluateQuery(context, foldFaces);

        const startFaceCenterPoint = evApproximateCentroid(context, { "entities" : fold.startBaseFace });
        const endFaceCenterPoint = evApproximateCentroid(context, { "entities" : fold.endBaseFace });

        const body0 = qOwnerBody(foldFacesArray[0]);
        const body1 = qOwnerBody(foldFacesArray[1]);

        const startBody = qClosestTo(qUnion(body0, body1), startFaceCenterPoint);
        const startBodyTracker = evaluateQuery(context, startBody);
        const startFace = qIntersection(qOwnedByBody(startBody, EntityType.FACE), foldFaces);

        const endBody = qClosestTo(qUnion(body0, body1), endFaceCenterPoint);
        const endBodyTracker = evaluateQuery(context, endBody);
        const endFace = qIntersection(qOwnedByBody(endBody, EntityType.FACE), foldFaces);

        //---------- FOLD MATH

        const defaultFoldLine = evLine(context, { "edge" : fold.foldEdge });
        const defaultFoldLineDirection = defaultFoldLine.direction;
        const startBaseFaceNormal = evSurfaceDefinition(context, { "face" : fold.startBaseFace }).normal;
        const referenceNormal = evSurfaceDefinition(context, { "face" : startFace }).normal;
        const decidingDirection = cross(defaultFoldLineDirection, referenceNormal);
        const directionInverter = dot(decidingDirection, startBaseFaceNormal);

        //---------- START FACE

        const startFaceTracker = startTracking(context, startFace);
        const startFaceNormal = evSurfaceDefinition(context, { "face" : startFace }).normal;
        const startFaceOffsetVector = -1 * startFaceNormal * bendOffset.startFaceOffset;

        const startFaceTransform = switch (fold.flipFoldDirection) {
                    true : transform(startFaceOffsetVector), // if it's folded backwards the edges don't need to be angled, only translation
                    false : transform(startFaceOffsetVector) * rotationAround(defaultFoldLine, directionInverter * foldAngle / 2) // translation and angling of the meeting faces
                };
        opMoveFace(context, id + j + "moveStartFace", {
                    "moveFaces" : startFace,
                    "transform" : startFaceTransform
                });
        const startMovedFace = evaluateQuery(context, startFaceTracker)[0];
        tagFaceForTruncation(context, startMovedFace);

        //---------- END FACE

        const endFaceTracker = startTracking(context, endFace);
        const endFaceNormal = -1 * evSurfaceDefinition(context, { "face" : endFace }).normal;
        const nudgeDistance = bendOffset.startFaceOffset - bendOffset.endFaceOffset;
        const endFaceOffsetVector = endFaceNormal * (bendOffset.endFaceOffset + nudgeDistance);
        const endFaceNudgeBackVector = -1 * endFaceNormal * nudgeDistance; // Used in unfold

        const endFaceTransform = switch (fold.flipFoldDirection) {
                    true : transform(endFaceOffsetVector), // if it's folded backwards the edges don't need to be angled, only translation
                    false : transform(endFaceOffsetVector) * rotationAround(defaultFoldLine, (-1 * directionInverter) * foldAngle / 2) // translation and angling of the meeting faces
                };
        opMoveFace(context, id + j + "moveEndFace", {
                    "moveFaces" : endFace,
                    "transform" : endFaceTransform
                });

        const endMovedFace = evaluateQuery(context, endFaceTracker)[0];
        tagFaceForTruncation(context, endMovedFace);

        //---------- RECURSION

        // Do the same thing recursively for all the child folds.
        const thisChildBendData = makeFoldReliefAndLivingHinge(context, id + j, definition, fold.childFolds);
        if (thisChildBendData.error)
            return { "error" : true };

        //---------- HINGE

        var rotationOrigin = defaultFoldLine.origin + startFaceOffsetVector + foldDirectionFlipMultiplier * startBaseFaceNormal * definition.livingHingeOuterRadius;
        rotationOrigin = fold.flipFoldDirection ? rotationOrigin + startBaseFaceNormal * definition.livingHingeThickness : rotationOrigin;
        const bentLivingEdgeRotationLine = line(rotationOrigin, foldDirectionFlipMultiplier * directionInverter * defaultFoldLine.direction);
        const livingHingeBodies = opHinge(context, id + j, definition, {
                    "fold" : fold,
                    "defaultFoldLine" : defaultFoldLine,
                    "startFaceOffsetVector" : startFaceOffsetVector,
                    "startFaceNormal" : startFaceNormal,
                    "directionInverter" : directionInverter,
                    "bentLivingEdgeRotationLine" : bentLivingEdgeRotationLine,
                    "foldAngle" : foldAngle,
                    "startMovedFace" : startMovedFace
                });

        //---------- Gather up all the return arrays
        bentLivingEdgeRotationLines = append(bentLivingEdgeRotationLines, bentLivingEdgeRotationLine);
        bendTransformVectors = append(bendTransformVectors, startFaceOffsetVector - endFaceOffsetVector);
        endBodies = append(endBodies, qUnion(endBodyTracker));
        endBodiesExternalTracker = append(endBodiesExternalTracker, startTracking(context, qUnion(endBodyTracker)));
        childBendData = append(childBendData, thisChildBendData);
        allFoldedParts = qUnion([allFoldedParts, startBodyTracker[0], qUnion(endBodies), thisChildBendData.allFoldedParts]);
        startBodyExternalQuery = qUnion(startBodyExternalQuery, qUnion(startBodyTracker));
        thisOrderLivingHinges = append(thisOrderLivingHinges, livingHingeBodies.hinge);
        endFaceNudgeBackVectors = append(endFaceNudgeBackVectors, endFaceNudgeBackVector);
    }

    return { "bentLivingEdgeRotationLines" : bentLivingEdgeRotationLines, "allFoldedParts" : allFoldedParts, "thisOrderLivingHinges" : thisOrderLivingHinges, "bendTransformVectors" : bendTransformVectors, "endBodies" : endBodies, "startBody" : startBodyExternalQuery, "startBodyTracker" : startTracking(context, startBodyExternalQuery), "childBendData" : childBendData, "endBodyTracker" : endBodiesExternalTracker, "error" : false, "nudgeBackVectors" : endFaceNudgeBackVectors };
}

/**
 * Create the living hinge body in folded state. The returned body is much too long and is to be later truncated.
 */
function opHinge(context is Context, id is Id, definition is map, spec is map) returns map
{
    const bottomEdgeStartPoint = evEdgeTangentLine(context, { "edge" : spec.fold.foldEdge, "parameter" : 0 }).origin;
    const bottomEdgeEndPoint = evEdgeTangentLine(context, { "edge" : spec.fold.foldEdge, "parameter" : 1 }).origin;

    const livingHingePlane = plane(spec.defaultFoldLine.origin + spec.startFaceOffsetVector, spec.startFaceNormal, -1 * spec.directionInverter * spec.defaultFoldLine.direction);
    const startWorldPointOnPlane = project(livingHingePlane, bottomEdgeStartPoint);
    const startPointOnPlane = worldToPlane(livingHingePlane, startWorldPointOnPlane);
    const endWorldPointOnPlane = project(livingHingePlane, bottomEdgeEndPoint);
    const endPointOnPlane = worldToPlane(livingHingePlane, endWorldPointOnPlane);

    const sketchId = id + "livingHingeSketch";
    const livingHingeSketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : livingHingePlane });
    skRectangle(livingHingeSketch, "rectangle", {
                "firstCorner" : startPointOnPlane,
                "secondCorner" : endPointOnPlane + vector(0 * millimeter, definition.livingHingeThickness)
            });
    skSolve(livingHingeSketch);
    const sketchRegion = qSketchRegion(sketchId);
    const sketchBody = qSketchRegion(sketchId);

    opRevolve(context, id + "revolve", {
                "entities" : sketchRegion,
                "axis" : spec.bentLivingEdgeRotationLine,
                "angleForward" : spec.foldAngle
            });

    const distance = evDistance(context, {
                    "side0" : startWorldPointOnPlane,
                    "side1" : endWorldPointOnPlane
                }).distance;

    // Offset end faces to make the hinge setup more stable.
    const moveFaces = qIntersection(qNonCapEntity(id + "revolve", EntityType.FACE), qGeometry(qCreatedBy(id + "revolve", EntityType.FACE), GeometryType.PLANE));
    opOffsetFace(context, id + "offsetFace1", {
                "moveFaces" : moveFaces,
                "offsetDistance" : distance / 2
            });

    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });

    // This section is not needed if the edge is folded backwards, as the faces are not angled then.
    if (!spec.fold.flipFoldDirection)
    {
        const livingHingeEndCapFace = qCapEntity(id + "revolve", CapType.END, EntityType.FACE);
        const livingHingeEndCapPlane = evPlane(context, { "face" : livingHingeEndCapFace });
        const livingHingeStartCapFace = qCapEntity(id + "revolve", CapType.START, EntityType.FACE);
        const livingHingeStartCapPlane = evPlane(context, { "face" : livingHingeStartCapFace });

        opExtrude(context, id + "startSideExtrude", {
                    "entities" : livingHingeStartCapFace,
                    "direction" : -1 * spec.startFaceNormal,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : distance / 2
                });

        const endPlane = evPlane(context, { "face" : spec.startMovedFace });
        opSplitPart(context, id + "splitPart1", {
                    "targets" : qCreatedBy(id + "startSideExtrude", EntityType.BODY),
                    "tool" : endPlane,
                    "keepType" : SplitOperationKeepType.KEEP_FRONT
                });

        const surfaceDef = evSurfaceDefinition(context, { "face" : livingHingeEndCapFace });

        const xDir = spec.directionInverter * spec.defaultFoldLine.direction;
        const zDir = -1 * surfaceDef.normal;
        const yDir = cross(zDir, xDir);
        const to = plane(surfaceDef.origin - definition.livingHingeThickness / 2 * yDir, zDir, xDir);
        const livingHingeEndTransform = transform(livingHingePlane, to);

        opPattern(context, id + "pattern1", {
                    "entities" : qCreatedBy(id + "startSideExtrude", EntityType.BODY),
                    "transforms" : [livingHingeEndTransform],
                    "instanceNames" : ["0"]
                });

        opBoolean(context, id + "boolean1", {
                    "tools" : qUnion([qCreatedBy(id + "revolve", EntityType.BODY), qCreatedBy(id + "startSideExtrude", EntityType.BODY), qCreatedBy(id + "pattern1", EntityType.BODY)]),
                    "operationType" : BooleanOperationType.UNION
                });
    }

    return { "hinge" : qCreatedBy(id + "revolve", EntityType.BODY) };
}

/**
 * Makes an array containing the map pointing out the start face, end face, along with tracking for it, and the fold edge for each fold. Makes nested format for child folds.
 */
function getFolds(context is Context, definition is map, referenceFace is Query) returns array
{
    var childFolds = [];
    const nextPossibleFoldFaces = qIntersection(definition.faces, qAdjacent(referenceFace, AdjacencyType.EDGE, EntityType.FACE));
    const nextPossibleFoldFacesArray = evaluateQuery(context, nextPossibleFoldFaces);

    for (var nextFoldFace in nextPossibleFoldFacesArray)
    {
        if (!isIn(nextFoldFace, definition.seenFaces))
        {
            var flipFoldDirection = definition.defaultFoldDirection;
            var foldAngle = definition.defaultFoldAngle;
            var defaultBend = true;

            definition.seenFaces = append(definition.seenFaces, nextFoldFace);

            const thisFoldEdges = qLoopEdges(referenceFace);
            const nextFoldEdges = qLoopEdges(nextFoldFace);

            const foldEdge = qIntersection([thisFoldEdges, nextFoldEdges]);

            const faceNormal = evPlane(context, { "face" : nextFoldFace }).normal;

            if (definition.enableOverrides)
            {
                for (var uniqueFold in definition.overrides)
                {
                    try
                    {
                        const engeLine = evLine(context, { "edge" : uniqueFold.edge });

                        if (!isQueryEmpty(context, qCoincidesWithPlane(foldEdge, plane(engeLine.origin, cross(faceNormal, engeLine.direction)))))
                        {
                            flipFoldDirection = (flipFoldDirection == uniqueFold.foldDirection) ? false : true;

                            foldAngle = uniqueFold.foldAngle;

                            defaultBend = false;
                        }
                    }
                }

            }

            childFolds = append(childFolds, { "defaultBend" : defaultBend, "flipFoldDirection" : flipFoldDirection, "foldAngle" : foldAngle, "startBaseFace" : referenceFace, "endBaseFace" : nextFoldFace, "foldEdge" : foldEdge, "edgeTracking" : startTracking(context, foldEdge), "childFolds" : getFolds(context, definition, nextFoldFace) });
        }
    }

    return childFolds;
}
