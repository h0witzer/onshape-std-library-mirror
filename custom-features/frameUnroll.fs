FeatureScript 2796;
import(path : "onshape/std/common.fs", version : "2796.0");
SVG::import(path : "860fa41c0703e97894ae733c", version : "9bbf338b8b3bc7fdaa4461cc");
PNG::import(path : "f6248719bfc049007c1043ac", version : "30d7bae202967798da9d8b89");

annotation { "Feature Type Name" : "Unfold frame", "Feature Type Description" : "Adds notches in tubes for laser cutting and bending", "Description Image" : PNG::BLOB_DATA, "Icon" : SVG::BLOB_DATA }
export const unfoldFrame = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Frame parts", "Filter" : EntityType.BODY && (BodyType.SOLID || BodyType.COMPOSITE) }
        definition.bodies is Query;
    }
    {
        if (!isQueryEmpty(context, qBodyType(definition.bodies, BodyType.COMPOSITE)))
        {
            if (evaluateQueryCount(context, definition.bodies) > 1)
                throw regenError("Only one composite part allowed.");

            definition.bodies = qContainedInCompositeParts(qNthElement(definition.bodies, 0));
        }
        else if (evaluateQueryCount(context, definition.bodies) < 2)
            throw regenError("More than one frame part required");

        const bodies = evaluateQuery(context, definition.bodies);
        const bodyData = getBodyData(context, bodies);
        var bodyGroup = [];

        for (var body in bodyData.order)
        {
            if (body == 0)
            {
                bodyGroup = [];
                continue;
            }

            bodyGroup = append(bodyGroup, body);
            var bodiesToTransform = [];

            for (var group in bodyGroup)
                bodiesToTransform = append(bodiesToTransform, bodies[group]);

            opTransform(context, id + "transform" + body, {
                        "bodies" : qUnion(bodiesToTransform),
                        "transform" : bodyData.transforms[body]
                    });
        }

        opBoolean(context, id + "boolean", {
                    "tools" : definition.bodies,
                    "operationType" : BooleanOperationType.UNION
                });
    });

function getBodyData(context is Context, bodies is array)
{
    const numberOfBodies = size(bodies);
    var bodyOrder = [0];
    var bodyTransforms = {};

    var lastBodyFaces = qOwnedByBody(bodies[0], EntityType.FACE)->qGeometry(GeometryType.PLANE);

    for (var i = 0; i < numberOfBodies; i += 1)
    {
        const nextBodyIndex = getNextBodyIndex(bodyOrder, numberOfBodies);
        var nextBodyFound = false;

        for (var j = nextBodyIndex; j < numberOfBodies; j += 1)
        {
            if (isIn(j, bodyOrder)) // don't process bodies more than once
                continue;

            var nextBodyFaces = qOwnedByBody(bodies[j], EntityType.FACE)->qGeometry(GeometryType.PLANE);

            for (var face in evaluateQuery(context, lastBodyFaces))
            {
                const facePlane = evPlane(context, {
                            "face" : face
                        });

                const matchingFace = getMatchingFace(context, facePlane, face, nextBodyFaces);

                if (!isQueryEmpty(context, matchingFace)) // mitered corner found
                {
                    bodyOrder = append(bodyOrder, j);
                    bodyTransforms[j] = getBodyTransform(context, facePlane, face, matchingFace, j);

                    lastBodyFaces = qOwnedByBody(bodies[j], EntityType.FACE)->qGeometry(GeometryType.PLANE);
                    nextBodyFound = true;
                    break;
                }
            }

            if (nextBodyFound)
                break;
        }

        if (!nextBodyFound && i < numberOfBodies - 1) // check other direction
        {
            bodyOrder = append(bodyOrder, 0);
            lastBodyFaces = qOwnedByBody(bodies[0], EntityType.FACE)->qGeometry(GeometryType.PLANE);
        }
    }

    return { "order" : reverse(bodyOrder), "transforms" : bodyTransforms };
}

function getMatchingFace(context is Context, facePlane is Plane, face is Query, nextBodyFaces is Query)
{
    var matchingFace = qCoincidesWithPlane(nextBodyFaces, facePlane)->
    qParallelPlanes(facePlane, true)->
    qSubtraction(qParallelPlanes(nextBodyFaces, facePlane, false));

    if (!isQueryEmpty(context, matchingFace))
    {
        var faceFound = false;
        const faceVertices = qAdjacent(face, AdjacencyType.VERTEX, EntityType.VERTEX);

        for (var vertex in evaluateQuery(context, faceVertices))
        {
            const point = evVertexPoint(context, {
                        "vertex" : vertex
                    });

            if (!isQueryEmpty(context, qContainsPoint(matchingFace, point)))
            {
                faceFound = true;
                break;
            }
        }

        if (!faceFound)
            matchingFace = qNothing();
    }

    return matchingFace;
}

function getBodyTransform(context is Context, facePlane is Plane, firstFace is Query, secondFace is Query, j is number)
{
    const firstSetOfFaces = getFacesTouchingPlane(facePlane, firstFace);
    const secondSetOfFaces = getFacesTouchingPlane(facePlane, secondFace);

    var edgesInPlane = qCoincidesWithPlane(qAdjacent(firstSetOfFaces, AdjacencyType.EDGE, EntityType.EDGE), facePlane);

    const edgeMidPointLine = evEdgeTangentLine(context, {
                "edge" : edgesInPlane,
                "parameter" : 0.5
            });

    const faceNormal = evFaceTangentPlane(context, {
                "face" : qNthElement(firstSetOfFaces, 0),
                "parameter" : vector(0.5, 0.5)
            });

    const cSys = coordSystem(edgeMidPointLine.origin, edgeMidPointLine.direction, faceNormal.normal);

    const boundingBox = evBox3d(context, {
                "topology" : qOwnerBody(firstFace),
                "cSys" : cSys,
                "tight" : true
            });

    var outerEdge;

    for (var corner in [boundingBox.minCorner, boundingBox.maxCorner])
    {
        const endPlane = plane(toWorld(cSys, corner), yAxis(cSys));

        outerEdge = qCoincidesWithPlane(qAdjacent(secondSetOfFaces, AdjacencyType.EDGE, EntityType.EDGE), endPlane)->
            qIntersection(qCoincidesWithPlane(qAdjacent(secondSetOfFaces, AdjacencyType.EDGE, EntityType.EDGE), facePlane));

        if (!isQueryEmpty(context, outerEdge))
            break;
    }

    const outerMidPoint = evEdgeTangentLine(context, {
                    "edge" : outerEdge,
                    "parameter" : 0.5
                }).origin;

    edgesInPlane = qCoincidesWithPlane(qAdjacent(secondSetOfFaces, AdjacencyType.EDGE, EntityType.EDGE), facePlane);

    const innerEdge = qClosestTo(edgesInPlane->qSubtraction(outerEdge), outerMidPoint);

    const innerMidPoint = evEdgeTangentLine(context, {
                    "edge" : innerEdge,
                    "parameter" : 0.5
                }).origin;

    const firstAngledFace = qContainsPoint(firstSetOfFaces, innerMidPoint);
    const secondAngledFace = qAdjacent(innerEdge, AdjacencyType.EDGE, EntityType.FACE)->qIntersection(secondSetOfFaces);

    const firstNormal = evFaceTangentPlane(context, {
                    "face" : firstAngledFace,
                    "parameter" : vector(0.5, 0.5)
                }).normal;

    const secondNormal = evFaceTangentPlane(context, {
                    "face" : secondAngledFace,
                    "parameter" : vector(0.5, 0.5)
                }).normal;

    const innerEdgeLine = evEdgeTangentLine(context, {
                "edge" : innerEdge,
                "parameter" : 0.5
            });

    return rotationAround(innerEdgeLine, angleBetween(firstNormal, secondNormal, -innerEdgeLine.direction));
}

function getFacesTouchingPlane(facePlane is Plane, face is Query)
{
    // Get faces adjacent to miter plane (along the frame length)
    const sideFaces = qAdjacent(face, AdjacencyType.EDGE, EntityType.FACE)->qGeometry(GeometryType.PLANE);
    // Remove any faces that are normal to the miter plane
    const angledFaces = sideFaces->qSubtraction(qPlanesParallelToDirection(sideFaces, facePlane.normal));
    // Get any remaining faces that touch the miter plane
    const facesTouchingPlane = qIntersectsPlane(angledFaces, facePlane);

    return facesTouchingPlane;
}

function getNextBodyIndex(bodyOrder is array, numberOfBodies is number)
{
    for (var i = 0; i < numberOfBodies; i += 1)
        if (!isIn(i, bodyOrder))
            return i;

    return numberOfBodies;
}

