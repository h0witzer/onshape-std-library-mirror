FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");

import(path : "onshape/std/feature.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/coordSystem.fs", version : "2679.0");
import(path : "onshape/std/transform.fs", version : "2679.0");
import(path : "onshape/std/math.fs", version : "2679.0");
import(path : "onshape/std/geomOperations.fs", version : "2679.0");
import(path : "onshape/std/units.fs", version : "2679.0");
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

function tangentArcData(start is Vector, end is Vector, tangentDir is Vector,
    radialDir is Vector) returns map
{
    var r = squaredNorm(end - start) / (2 * dot(end - start, radialDir));
    if (r < 0)
    {
        r = -r;
        radialDir = -radialDir;
    }
    const c = start + r * radialDir;
    const normal = cross(tangentDir, radialDir);
    const sv = start - c;
    const ev = end - c;
    var ang = atan2(dot(normal, cross(sv, ev)), dot(sv, ev));
    if (ang < 0)
        ang += 2 * PI * radian;
    return { "radius" : r, "center" : c, "planeNormal" : normal,
            "sweep" : ang, "rad" : radialDir };
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
    var baseRad = cross(tangentDir, cross(chord, tangentDir));
    baseRad = normalize(baseRad);
    const arc = tangentArcData(start, end, tangentDir, baseRad);

    const radialDir = arc.rad;
    const radius = arc.radius;
    const center = arc.center;
    const planeNormal = arc.planeNormal;
    const sweepAngle = arc.sweep;
    const startVec = start - center;
    const midVec = rotationMatrix3d(normalize(planeNormal), sweepAngle / 2) * startVec;
    const mid = center + midVec;
    const planeCSys = coordSystem(center, normalize(startVec), normalize(planeNormal));
    const start2d = vector(radius, 0 * meter);
    const midLocal = fromWorld(planeCSys, mid);
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
