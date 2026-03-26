FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");
export import(path : "9c4e6800da09c31ac968b6c1", version : "3c04376b78cee7e5337e5b7d");//4dShared.fs

const NULL_PLANE = plane(vector(1000, 1000, 1000) * meter, vector(1, 2, 3), vector(3, 2, 1)); //A reference point of an "epmty" plane, seems unlikely to ever come up. 

/**
 * The hinges are too long, and now that the base parts have been truncated, those truncated faces can be used to further truncate the ends of the hinges.
 */
export function opCleanUpLivingHinges(context, id, definition, bendData)
{
    for (var i = 0; i < size(bendData.thisOrderLivingHinges); i += 1)
    {
        const primarySideBody = qUnion([qUnion(evaluateQuery(context, bendData.startBodyTracker)), bendData.startBody]);
        const secondarySideBody = qUnion([qUnion(evaluateQuery(context, bendData.endBodyTracker[i])), bendData.endBodies[i]]);

        const hinge = bendData.thisOrderLivingHinges[i];
        const allHingeFaces = qOwnedByBody(hinge, EntityType.FACE);
        const cylinderFaces = qGeometry(qOwnedByBody(hinge, EntityType.FACE), GeometryType.CYLINDER);
        const hingeCylinderFace0 = qNthElement(cylinderFaces, 0);
        const hingeCylinderFace1 = qNthElement(cylinderFaces, 1);
        const cylinderSpec0 = evSurfaceDefinition(context, { "face" : hingeCylinderFace0 });
        const cylinderSpec1 = evSurfaceDefinition(context, { "face" : hingeCylinderFace1 });
        const hingeDirection = cylinderSpec0.coordSystem.zAxis;
        const hingeCenter = evApproximateCentroid(context, { "entities" : hinge });

        const capFaces = qParallelPlanes(allHingeFaces, hingeDirection, true);
        const capPoint0 = evApproximateCentroid(context, { "entities" : qNthElement(capFaces, 0) });
        const capPoint1 = evApproximateCentroid(context, { "entities" : qNthElement(capFaces, 1) });

        const orderedCylinderFaces = switch (cylinderSpec0.radius > cylinderSpec1.radius) {
                    true : { "inner" : hingeCylinderFace1, "outer" : hingeCylinderFace0 },
                    false : { "inner" : hingeCylinderFace0, "outer" : hingeCylinderFace1 }
                };

        const innerCentroid = evApproximateCentroid(context, { "entities" : orderedCylinderFaces.inner });
        const outerCentroid = evApproximateCentroid(context, { "entities" : orderedCylinderFaces.outer });

        const inwardsHingeNormal = normalize(innerCentroid - outerCentroid);
        const hingeOutOfPlane = normalize(cross(inwardsHingeNormal, hingeDirection));
        const hingePlane = plane(cylinderSpec0.coordSystem.origin, hingeOutOfPlane, hingeDirection);

        const hingeCentroid = evApproximateCentroid(context, { "entities" : hinge });

        const primarySplitFaces = getSplitFaces(context, primarySideBody, hingeCentroid);
        const secondarySplitFaces = getSplitFaces(context, secondarySideBody, hingeCentroid);

        opTruncateHingeEnds(context, id + i + "point0", hinge, primarySplitFaces, secondarySplitFaces, capPoint0, hingeCenter, hingePlane);
        opTruncateHingeEnds(context, id + i + "point1", hinge, primarySplitFaces, secondarySplitFaces, capPoint1, hingeCenter, hingePlane);

        opCleanUpLivingHinges(context, id + i, definition, bendData.childBendData[i]);
    }
}

/**
 * Perform the truncation of the hinges
 */
function opTruncateHingeEnds(context is Context, id is Id, hinge is Query, primarySplitFaces, secondarySplitFaces, point, centerPoint, hingePlane)
{
    const splitPrimary = qClosestTo(primarySplitFaces, point);
    const splitSecondary = qClosestTo(secondarySplitFaces, point);

    opSplitPart(context, id + "primarySplit", { "targets" : hinge, "tool" : splitPrimary });
    opSplitPart(context, id + "secondarySplit", { "targets" : hinge, "tool" : splitSecondary });

    const inFrontPrimary = qInFrontOfPlane(hinge, evPlane(context, { "face" : splitPrimary }));
    const inFrontSecondary = qInFrontOfPlane(hinge, evPlane(context, { "face" : splitSecondary }));

    const primaryNormal = evPlane(context, { "face" : splitPrimary }).normal;
    const secondaryNormal = evPlane(context, { "face" : splitSecondary }).normal;
    const combinedNormal = primaryNormal + secondaryNormal;
    const adjustedCombinedNormal = normalize(project(hingePlane.x, combinedNormal));

    hingePlane.normal = switch (round(dot(hingePlane.x, adjustedCombinedNormal))) {
                1 : hingePlane.normal,
                -1 : -hingePlane.normal
            };

    const bothFaces = qUnion(splitPrimary, splitSecondary);
    const faceInFront = qInFrontOfPlane(bothFaces, hingePlane);
    const faceBehind = qSubtraction(bothFaces, faceInFront);

    const inFrontNormal = evPlane(context, { "face" : faceInFront }).normal;
    const behindNormal = evPlane(context, { "face" : faceBehind }).normal;

    const angle = angleBetween(inFrontNormal, behindNormal, point - centerPoint);

    const cornerBodies = switch (angle < 0 * degree) {
                true : qEntityFilter(qIntersection(inFrontPrimary, inFrontSecondary), EntityType.BODY),
                false : qEntityFilter(qUnion(inFrontPrimary, inFrontSecondary), EntityType.BODY)
            };

    if (!isQueryEmpty(context, cornerBodies))
    {
        opDeleteBodies(context, id + "deleteCorner", { "entities" : cornerBodies });

        if (size(evaluateQuery(context, hinge)) > 1)
            opBoolean(context, id + "boolean1", { "tools" : hinge, "operationType" : BooleanOperationType.UNION });
    }
}

/**
 * Gather up all possible truncation faces for the hinges
 */
function getSplitFaces(context is Context, body, centroid) returns Query
{
    const allFaces = qOwnedByBody(body, EntityType.FACE);

    const potentialHingeFaces = qClosestTo(allFaces, centroid);

    var allTaggedFaces = qNothing();
    for (var face in evaluateQuery(context, qOwnedByBody(body, EntityType.FACE)))
    {
        const isFaceOfInterest = getAttribute(context, { "entity" : face, "name" : TAG_FACES_ATTRIBUTE });

        if (isFaceOfInterest == true)
            allTaggedFaces = qUnion(allTaggedFaces, face);
    }

    const hingeFace = qIntersection(potentialHingeFaces, allTaggedFaces);

    const splitFaces = qIntersection(allTaggedFaces, qAdjacent(hingeFace, AdjacencyType.EDGE, EntityType.FACE));

    return splitFaces;
}


/**
 * In order to make a fold, parts that meet from a fold needs to be trunkated to make room.
 */
export function opTruncateSlaveEdges(context is Context, id is Id, definition is map, bendData is map)
{
    const allParts = bendData.allFoldedParts;

    var allPartsArray = evaluateQuery(context, allParts);

    const allCapEntities = qCapEntity(id, CapType.END, EntityType.FACE);

    const count = size(allPartsArray);
    var localId = id + "bools";

    for (var i = 0; i < count; i += 1)
    {
        localId = localId + i;

        var primaryTool = allPartsArray[i];

        for (var j = 0; j < count; j += 1)
        {
            if (i != j)
            {
                localId = localId + j;

                const secondaryTool = allPartsArray[j];

                const splitPlane = makeSplitPlane(context, localId + "primary", primaryTool, secondaryTool, allCapEntities);

                if (splitPlane != NULL_PLANE)
                {
                    const primaryKeepBody = opSplitPartByMidplane(context, localId, definition, primaryTool, splitPlane);

                    allPartsArray[i] = primaryKeepBody.keepBody;
                    primaryTool = primaryKeepBody.keepBody;

                    const reversedPlane = plane(splitPlane.origin, -splitPlane.normal);
                    const secondaryKeepBody = opSplitPartByMidplane(context, localId + "secondary", definition, secondaryTool, reversedPlane);

                    allPartsArray[j] = secondaryKeepBody.keepBody;
                }
            }
        }
    }
}

/**
 * Make the split of the part at the split plane and optinally offset.
 */
function opSplitPartByMidplane(context is Context, localId is Id, definition is map, part is Query, splitPlane is Plane) returns map
{
    const partCentroid = evApproximateCentroid(context, { "entities" : part });

    const splitId = localId + "split";

    opSplitPart(context, splitId, {
                "targets" : part,
                "tool" : splitPlane,
                "keepType" : SplitOperationKeepType.KEEP_FRONT
            });

    const createdBodies = qOwnerBody(qCreatedBy(splitId));

    const keepBody = qClosestTo(createdBodies, partCentroid);

    const createdFace = qEntityFilter(qCreatedBy(splitId), EntityType.FACE);

    opOffsetFace(context, localId + "offset", { "moveFaces" : createdFace, "offsetDistance" : -definition.printOffset });

    tagFaceForTruncation(context, createdFace);

    return { "keepBody" : keepBody };
}

/**
 * The truncation needs a split plane to reference both sides from effectively at the middle plane of the two sides.
 * If no plane can be found returns the NULL_PLANE.
 */
function makeSplitPlane(context is Context, localId is Id, primaryTool is Query, secondaryTool is Query, allCapEntities is Query) returns Plane
{

    opPattern(context, localId + "pattern1", {
                "entities" : primaryTool,
                "transforms" : [identityTransform()],
                "instanceNames" : ["0"]
            });

    opPattern(context, localId + "pattern2", {
                "entities" : secondaryTool,
                "transforms" : [identityTransform()],
                "instanceNames" : ["0"]
            });

    opBoolean(context, localId + "boolean", {
                "tools" : qUnion(qCreatedBy(localId + "pattern1", EntityType.BODY), qCreatedBy(localId + "pattern2", EntityType.BODY)),
                "operationType" : BooleanOperationType.INTERSECTION
            });
    const booleanResult = qCreatedBy(localId + "boolean");

    if (!isQueryEmpty(context, booleanResult))
    {
        const intersectionBodyEdges = qOwnedByBody(booleanResult, EntityType.EDGE);

        const primaryToolFace = qIntersection(allCapEntities, qOwnedByBody(primaryTool, EntityType.FACE));
        const edgeCandidatesFromPrimary = qCoincidesWithPlane(intersectionBodyEdges, evPlane(context, { "face" : primaryToolFace }));

        const secondaryToolFace = qIntersection(allCapEntities, qOwnedByBody(secondaryTool, EntityType.FACE));
        const edgeCandidatesFromSecondary = qCoincidesWithPlane(intersectionBodyEdges, evPlane(context, { "face" : secondaryToolFace }));

        const closestEdge = qIntersection(edgeCandidatesFromPrimary, edgeCandidatesFromSecondary);

        if (!isQueryEmpty(context, closestEdge))
        {
            const closestEdgeMidPointData = evEdgeTangentLine(context, { "edge" : closestEdge, "parameter" : .5 });

            const primaryNormal = evSurfaceDefinition(context, { "face" : primaryToolFace }).normal;
            const faceToFaceAngle = angleBetween(primaryNormal, evSurfaceDefinition(context, { "face" : secondaryToolFace }).normal, closestEdgeMidPointData.direction);

            const planeNormal = cross(closestEdgeMidPointData.direction, rotationMatrix3d(closestEdgeMidPointData.direction, faceToFaceAngle / 2) * primaryNormal);

            opDeleteBodies(context, localId + "deleteHelpBody", { "entities" : qCreatedBy(localId + "boolean", EntityType.BODY) });

            const remainingEdges = qSubtraction(intersectionBodyEdges, closestEdge);

            const returnPlane = plane(closestEdgeMidPointData.origin, planeNormal);
            
            if (returnPlane == NULL_PLANE)
            {
                throw "WOW I DID NOT EXPECT THE NULL PLANE TO EVER COME UP, WELL DONE!";
            }
            
            return returnPlane;
        }
    }

    return NULL_PLANE;
}
