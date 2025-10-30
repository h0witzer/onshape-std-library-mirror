FeatureScript 1660;
import(path : "onshape/std/common.fs", version : "1660.0");

/**
 * Returns the coordinate system representing a sketch vertex or a mate connector.
 *
 * @param arg {{
 *      @field vertex {Query} : A sketch vertex or mate connector.
 * }}
 */
export function evVertexCoordSystem(context is Context, arg is map) returns CoordSystem
precondition
{
    arg.vertex is Query;
    !isQueryEmpty(context, qUnion([
                        arg.vertex->qNthElement(0)->qEntityFilter(EntityType.VERTEX)->qSketchFilter(SketchObject.YES),
                        arg.vertex->qNthElement(0)->qBodyType(BodyType.MATE_CONNECTOR)
                    ]));
}
{
    if (!isQueryEmpty(context, arg.vertex->qNthElement(0)->qEntityFilter(EntityType.VERTEX)->qSketchFilter(SketchObject.YES)))
    {
        var plane = evOwnerSketchPlane(context, { "entity" : arg.vertex });
        plane.origin = evVertexPoint(context, { "vertex" : arg.vertex });
        return coordSystem(plane);
    }
    else if (!isQueryEmpty(context, arg.vertex->qNthElement(0)->qBodyType(BodyType.MATE_CONNECTOR)))
    {
        return evMateConnector(context, { "mateConnector" : arg.vertex });
    }
}

/**
 * Returns the next element of an array.
 * @param index : @autocomplete `i`
 */
export function getNext(inputArray is array, index is number) returns map
{
    return inputArray[(index + 1) % size(inputArray)]; // if index = size(geometry), returns geometry[0]
}

/**
 * Returns the index of the next element of an array.
 * @param arraySize : @autocomplete `size(inputArray)`
 * @param index : @autocomplete `i`
 */
export function getNext(arraySize is number, index is number) returns number
{
    return (index + 1) % arraySize;
}

/**
 * Returns the previous element of an array.
 * @param index : @autocomplete `i`
 */
export function getPrevious(inputArray is array, index is number) returns map
{
    return inputArray[(index + size(inputArray) - 1) % size(inputArray)]; // if index = 0, returns geometry[size(geometry)]
}

/**
 * Returns the index of the previous element of an array.
 * @param arraySize : @autocomplete `size(inputArray)`
 * @param index : @autocomplete `i`
 */
export function getPrevious(arraySize is number, index is number) returns number
{
    return (index + arraySize - 1) % arraySize;
}

/**
 * Calculates the size of the diagonal of the bounding box. Represents the theoetical maximum length of an operation like opExtrude.
 * Based on a function from the Onshape STD Library.
 *
 * @param entities : @autocomplete `qEverything(EntityType.BODY)`
 */
export function calculateLength(context is Context, entities is Query) returns ValueWithUnits
{
    const partBoundingBox = evBox3d(context, { "topology" : entities, "tight" : false });
    return norm(partBoundingBox.maxCorner - partBoundingBox.minCorner);
}

export function getCenter(context is Context, entities is Query) returns Vector
{
    return evBox3d(context, { "topology" : entities, "tight" : false })->box3dCenter();
}

/**
 * Initializes a box with an empty array.
 * @seealso [clearBox]
 */
export function initializeBox()
{
    return new box([]);
}

/**
 * Deletes the contents of boxToClear if it is not an empty array.
 * @param boxToClear : @autocomplete `toDelete`
 * @seealso [initializeBox]
 */
export function clearBox(context is Context, id is Id, boxToClear is box)
{
    if (!isQueryEmpty(context, qUnion(boxToClear[])))
    {
        opDeleteBodies(context, id + "clearBox", { "entities" : qUnion(boxToClear[]) });
    }
}

export function projectToPlane(point is Vector, plane is Plane) returns Vector
{
    return worldToPlane(plane, project(plane, point));
}

export function projectToPlane(plane is Plane, point is Vector) returns Vector
{
    return projectToPlane(point, plane);
}

const DEBUG_ID_STRING = "debug314159";

export function addDebugEdge(context is Context, from is Vector, to is Vector, color is DebugColor)
{
    const arrowId = getLastActiveId(context) + DEBUG_ID_STRING + "edge";
    startFeature(context, arrowId, {});
    try
    {
        opFitSpline(context, arrowId + "fitSpline", {
                    "points" : [from, to]
                });
        addDebugEntities(context, qCreatedBy(arrowId, EntityType.EDGE), color);
    }
    abortFeature(context, arrowId);
}

/**
 * Returns the endpoints of a line tangental to two circles. Flipping the chirality has the same effect as reversing the arguments.
 * Returns undefined if the circles have the same center.
 * @param chirality {boolean} : The chirality of the connection. Flips depending on the way things are selected.
 */
export function circleToCircle(vertex1 is Vector, radius1 is ValueWithUnits, vertex2 is Vector, radius2 is ValueWithUnits, chirality is boolean) returns array
{
    if (tolerantEquals(vertex1, vertex2))
    {
        return undefined;
    }

    radius1 *= (chirality ? -1 : 1);
    radius2 *= (chirality ? -1 : 1);
    const delta = normalize(vertex2 - vertex1);
    const cross = vector(-delta[1], delta[0]);
    const alpha = (radius1 - radius2) / norm(vertex2 - vertex1);
    const beta = sqrt(1 - alpha ^ 2);

    var point1 = vertex1 + (alpha * delta + beta * cross) * radius1;
    var point2 = vertex2 + (alpha * delta + beta * cross) * radius2;
    return [point1, point2];
}

/**
 * Returns the endpoints of a line through a point tangental to a circle.
 * Returns undefined if the point is within the radius of the circle.
 * @param chirality {boolean} : The chirality of the connection.
 *
 * Function borrowed from here:
 * https://stackoverflow.com/questions/49968720/find-tangent-points-in-a-circle-from-a-point/49981991
 */
export function pointToCircle(point is Vector, circleCenter is Vector, radius is ValueWithUnits, chirality is boolean) // returns array or undefined
{
    if (norm(point - circleCenter) + TOLERANCE.zeroLength * meter <= radius)
    {
        return undefined;
    }

    const centerToCenter = norm(point - circleCenter);
    const angle = acos(radius / centerToCenter);
    const angleOffset = atan2(point[1] - circleCenter[1], point[0] - circleCenter[0]);

    if (chirality)
    {
        const angle1 = angleOffset + angle;
        return [point, circleCenter + vector(radius * cos(angle1), radius * sin(angle1))];
    }
    else
    {
        const angle2 = angleOffset - angle;
        return [point, circleCenter + vector(radius * cos(angle2), radius * sin(angle2))];
    }
}

export function pointToCircle(circleCenter is Vector, radius is ValueWithUnits, point is Vector, chirality is boolean)
{
    const result = pointToCircle(point, circleCenter, radius, !chirality);
    return (result == undefined ? undefined : reverse(result));
}
