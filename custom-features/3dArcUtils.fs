FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");

import(path : "onshape/std/geometry.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/coordSystem.fs", version : "2679.0");
import(path : "onshape/std/transform.fs", version : "2679.0");
import(path : "onshape/std/math.fs", version : "2679.0");
import(path : "onshape/std/geomOperations.fs", version : "2679.0");
import(path : "onshape/std/units.fs", version : "2679.0");
import(path : "onshape/std/matrix.fs", version : "2679.0");
import(path : "onshape/std/error.fs", version : "2679.0");

/**
 * Create a circular arc wire through three points in 3D space using sketch geometry.
 * Returns a query for the created wire body.
 *
 * @param id : @autocomplete `id + "arc"`
 * @param definition {{
 *      @field start {Vector} : Start of the arc.
 *      @field mid {Vector} : A point on the arc between start and end.
 *      @field end {Vector} : End of the arc.
 * }}
 */
export function opArc3d(context is Context, id is Id, definition is map)
precondition
{
    is3dLengthVector(definition.start);
    is3dLengthVector(definition.mid);
    is3dLengthVector(definition.end);
}
{
    // Determine a coordinate system whose XY plane contains the three points
    const v1 = definition.mid - definition.start;
    const normal = cross(v1, definition.end - definition.start);
    const xAxis = normalize(v1);
    const planeCSys = coordSystem(definition.start, xAxis, normalize(normal));
    const sketchPlane = plane(planeCSys);
    const start2d = vector(0 * meter, 0 * meter);
    const midLocal = fromWorld(planeCSys, definition.mid);
    const endLocal = fromWorld(planeCSys, definition.end);
    const mid2d = vector(midLocal[0], midLocal[1]);
    const end2d = vector(endLocal[0], endLocal[1]);
    // Create temporary sketch arc
    const sketchId = id + "sketch";
    {
        const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
        skArc(sketch, "arc", { "start" : start2d, "mid" : mid2d, "end" : end2d });
        skSolve(sketch);
    }
    // Extract wire body from the sketch arc
    const edgeQuery = qCreatedBy(sketchId, EntityType.EDGE);
    opExtractWires(context, id + "wire", { "edges" : edgeQuery });
    // Delete sketch bodies
    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });
    return qCreatedBy(id + "wire", EntityType.BODY);
}


/**
 * Create a full circle wire in 3D space using sketch geometry.
 * Returns a query for the created wire body.
 *
 * @param id : @autocomplete `id + "circle"`
 * @param definition {{
 *      @field center {Vector} : Center of the circle.
 *      @field normal {Vector} : Normal direction of the circle plane.
 *      @field radius {ValueWithUnits} : Circle radius.
 *      @field xDirection {Vector} : Optional in-plane x direction.
 * }}
 */
export function opCircle3d(context is Context, id is Id, definition is map)
precondition
{
    is3dLengthVector(definition.center);
    is3dDirection(definition.normal);
    isLength(definition.radius);
    definition.xDirection == undefined || is3dDirection(definition.xDirection);
}
{
    var xDir = definition.xDirection;
    if (xDir == undefined)
        xDir = perpendicularVector(definition.normal);
    const cSys = coordSystem(definition.center, normalize(xDir), normalize(definition.normal));
    const sketchId = id + "sketch";
    {
        const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(cSys) });
        skCircle(sketch, "circle", { "center" : vector(0, 0) * meter, "radius" : definition.radius });
        skSolve(sketch);
    }
    opExtractWires(context, id + "wire", { "edges" : qCreatedBy(sketchId, EntityType.EDGE) });
        opDropCurve(context, id + "wire", {
            "tools" : qCreatedBy(sketchId, EntityType.EDGE),
            "targets" : qCreatedBy(planeId, EntityType.FACE),
            "projectionType" : ProjectionType.NORMAL_TO_TARGET
    });
    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });
    return qCreatedBy(id + "wire", EntityType.BODY);
}


/**
 * Create a circular wire body through three points in 3D space.
 * The resulting circle lies in the plane defined by the points and is
 * created with a sketch circle that is converted to a wire using
 * [opDropCurve].
 * Returns a query for the created wire body.
 *
 * @param id : @autocomplete `id + "circle"`
 * @param definition {{
 *      @field first {Vector} : First vertex defining the circle.
 *      @field second {Vector} : Second vertex defining the circle.
 *      @field third {Vector} : Third vertex defining the circle.
 * }}
 */
export function op3PointCircle3d(context is Context, id is Id, definition is map)
precondition
{
    is3dLengthVector(definition.first);
    is3dLengthVector(definition.second);
    is3dLengthVector(definition.third);
}
{
    const p1 = definition.first;
    const p2 = definition.second;
    const p3 = definition.third;

    // Determine a coordinate system whose XY plane contains the three points
    const v1 = p2 - p1;
    var normal = cross(v1, p3 - p1);
    if (norm(normal) < 1e-8 * meter^2)
        throw regenError(ErrorStringEnum.READ_FAILED, id);
    const xAxis = normalize(v1);
    normal = normalize(normal);
    const planeCSys = coordSystem(p1, xAxis, normal);

    const p2Local = fromWorld(planeCSys, p2);
    const p3Local = fromWorld(planeCSys, p3);
    const p1d = vector(0 * meter, 0 * meter);
    const p2d = vector(p2Local[0], p2Local[1]);
    const p3d = vector(p3Local[0], p3Local[1]);

    const circleData = circumcenter2d(p1d, p2d, p3d);
    if (circleData.invalid)
        throw regenError(ErrorStringEnum.READ_FAILED, id);

    const planeId = id + "plane";
    opPlane(context, planeId, { "plane" : plane(planeCSys) });

    const sketchId = id + "sketch";
    {
        const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(planeCSys) });
        skCircle(sketch, "circle", { "center" : circleData.center, "radius" : circleData.radius });
        skSolve(sketch);
    }

    const edgeQuery = qCreatedBy(sketchId, EntityType.EDGE);
    opDropCurve(context, id + "wire", {
            "tools" : edgeQuery,
            "targets" : qCreatedBy(planeId, EntityType.FACE),
            "projectionType" : ProjectionType.NORMAL_TO_TARGET
    });

    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });
    opDeleteBodies(context, id + "deletePlane", { "entities" : qCreatedBy(planeId, EntityType.BODY) });
    return qCreatedBy(id + "wire", EntityType.BODY);
}

function circumcenter2d(p1 is Vector, p2 is Vector, p3 is Vector) returns map
{
    const d = 2 * (p1[0] * (p2[1] - p3[1]) + p2[0] * (p3[1] - p1[1]) + p3[0] * (p1[1] - p2[1]));
    if (abs(d) < 1e-9 * meter ^ 2)
        return { "invalid" : true };

    const ux = ((squaredNorm(p1) * (p2[1] - p3[1]) + squaredNorm(p2) * (p3[1] - p1[1]) + squaredNorm(p3) * (p1[1] - p2[1])) / d);
    const uy = ((squaredNorm(p1) * (p3[0] - p2[0]) + squaredNorm(p2) * (p1[0] - p3[0]) + squaredNorm(p3) * (p2[0] - p1[0])) / d);
    const center = vector(ux, uy);
    const radius = sqrt(squaredNorm(center - p1));
    return { "center" : center, "radius" : radius, "invalid" : false };
}


/**
 * Create a circular arc wire through a start and end point such that the arc is
 * tangent to a given edge at the start point.
 * Returns a query for the created wire body.
 *
 * @param id : @autocomplete `id + "tangentArc"`
 * @param definition {{
 *      @field start {Vector} : Start of the arc.
 *      @field tangentEdge {Query} : Edge used to determine the tangent direction at the start.
 *      @field end {Vector} : End of the arc.
 * }}
 */
export function opTangentArc3d(context is Context, id is Id, definition is map)
precondition
{
    is3dLengthVector(definition.start);
    definition.tangentEdge is Query;
    is3dLengthVector(definition.end);
}
{
    const start = definition.start;
    const end = definition.end;
    // Determine tangent direction at the start vertex
    const endpoints = evEdgeTangentLines(context,
        { "edge" : definition.tangentEdge, "parameters" : [0, 1] });
    var tangentLine = endpoints[0];
    if (norm(start - endpoints[1].origin) > norm(start - endpoints[0].origin))
        tangentLine = endpoints[1];
    if (norm(start - tangentLine.origin) > 0)
    {
        const param = evDistance(context,
                { "side0" : start, "side1" : definition.tangentEdge }).sides[1].parameter;
        tangentLine = evEdgeTangentLine(context,
            { "edge" : definition.tangentEdge, "parameter" : param });
    }
    var tangentDir = normalize(tangentLine.direction);
    if (tangentLine == endpoints[0])
    {
        tangentDir = -tangentDir;
    }
    const chord = end - start;
    // Component of chord perpendicular to the tangent direction
    var radialDir = cross(tangentDir, cross(chord, tangentDir));
    radialDir = normalize(radialDir);
    var radius = squaredNorm(end - start) / (2 * dot(chord, radialDir));
    if (radius < 0)
    {
        radius = -radius;
        radialDir = -radialDir;
    }
    const arcData = getTangentArcData(start, tangentDir, end);
    if (!arcData.valid)
        throw regenError(ErrorStringEnum.READ_FAILED, id);
    const planeCSys = coordSystem(arcData.center,
                                  normalize(start - arcData.center),
                                  arcData.normal);
    const start2d = vector(arcData.radius, 0 * meter);
    const midLocal = fromWorld(planeCSys, arcData.mid);
    const endLocal = fromWorld(planeCSys, end);
    const mid2d = vector(midLocal[0], midLocal[1]);
    const end2d = vector(endLocal[0], endLocal[1]);
    const sketchId = id + "sketch";
    {
        const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(planeCSys) });
        skArc(sketch, "arc", { "start" : start2d, "mid" : mid2d, "end" : end2d });
        skSolve(sketch);
    }

    const edgeQuery = qCreatedBy(sketchId, EntityType.EDGE);
    opExtractWires(context, id + "wire", { "edges" : edgeQuery });
    opDeleteBodies(context, id + "deleteSketch", { "entities" : qCreatedBy(sketchId, EntityType.BODY) });
    return qCreatedBy(id + "wire", EntityType.BODY);
}

/**
 * Create a pair of tangent circular arcs connecting the start and end of a path.
 *
 * The arcs are tangent to the input path at the start and end and form a
 * tangent arc chain with each other.
 *
 * @param id : @autocomplete `id + "biArc"`
 * @param definition {{
 *      @field startPath {Query} : Path providing the start point and
 *          start tangent constraint.
 *      @field endPath {Query} : Path providing the end point and
 *          end tangent constraint.
 * }}
 */
export function opBiArc3d(context is Context, id is Id, definition is map)
precondition
{
        is3dLengthVector(definition.start);

    definition.tangentStartEdge is Query;
    definition.tangentEndEdge is Query;
        is3dLengthVector(definition.end);

}
{
    const startPath = constructPath(context, definition.tangentStartEdge);
    const endPath = constructPath(context, definition.tangentEndEdge);

    const startEval = evPathTangentLines(context, startPath, [1]).tangentLines[0];
    const endEval = evPathTangentLines(context, endPath, [0]).tangentLines[0];

    const startPoint = definition.start;
    const startTangent = normalize(startEval.direction);
    const endPoint = definition.end;
    const endTangent = normalize(endEval.direction);

    const chord = endPoint - startPoint;
    var parameter = 0.5;
    var step = 0.25;
    var junction;
    var firstArc;
    var firstTangent;
    var secondArc;
    for (var i = 0; i < 8; i += 1)
    {
        junction = startPoint + parameter * chord;
        firstArc = getTangentArcData(startPoint, startTangent, junction);
        if (!firstArc.valid)
        {
            parameter += step;
            step *= 0.5;
            continue;
        }

        firstTangent = normalize(cross(firstArc.normal, junction - firstArc.center));
        secondArc = getTangentArcData(junction, firstTangent, endPoint);
        if (!secondArc.valid)
        {
            parameter -= step;
            step *= 0.5;
            continue;
        }

        const endDir = normalize(cross(secondArc.normal, endPoint - secondArc.center));
        const diff = dot(endDir, endTangent) - 1;
        if (abs(diff) < 1e-4)
            break;
        if (diff > 0)
            parameter += step;
        else
            parameter -= step;
        step *= 0.5;
    }

    const firstWire = opArc3d(context, id + "a1", { "start" : startPoint, "mid" : firstArc.mid, "end" : junction });
    const secondWire = opArc3d(context, id + "a2", { "start" : junction, "mid" : secondArc.mid, "end" : endPoint });
    return qUnion([firstWire, secondWire]);
}




/**
 * Compute parameters describing the circular arc defined by a start point,
 * a tangent direction at that point and an end point.
 *
 * Returns a map containing the center, radius, mid-point and plane normal
 * of the arc. If the construction fails the `valid` field will be false.
 */
export function getTangentArcData(start is Vector, tangentDir is Vector, end is Vector) returns map
{
    const chord = end - start;
    var radialDir = cross(tangentDir, cross(chord, tangentDir));
    if (norm(radialDir) < 1e-8 * meter)
        return { valid : false };
    radialDir = normalize(radialDir);
    var radius = squaredNorm(end - start) / (2 * dot(chord, radialDir));
    if (radius < 0 * meter)
    {
        radius = -radius;
        radialDir = -radialDir;
    }
    const center = start + radius * radialDir;
    const planeNormal = cross(tangentDir, radialDir);
    if (norm(planeNormal) < 1e-8)
        return { valid : false };
    const startVec = start - center;
    const endVec = end - center;
    var sweepAngle = atan2(dot(planeNormal, cross(startVec, endVec)), dot(startVec, endVec));
    if (sweepAngle < 0)
        sweepAngle += 2 * PI * radian;
    const midVec = rotationMatrix3d(normalize(planeNormal), sweepAngle / 2) * startVec;
    const mid = center + midVec;
    return { valid : true, ("center") : center, ("radius") : radius, normal : normalize(planeNormal), ("mid") : mid };
}
