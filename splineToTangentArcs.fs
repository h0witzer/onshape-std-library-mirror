FeatureScript 2656;
import(path : "onshape/std/common.fs", version : "2656.0");

// This module approximates a BSpline with a set of tangent arcs.
// Each pair of consecutive sample points is converted to two arcs
// using the biarc construction so that the arcs are tangent at the join.
import(path : "onshape/std/splineUtils.fs", version : "2656.0");
import(path : "onshape/std/evaluate.fs", version : "2656.0");
import(path : "onshape/std/vector.fs", version : "2656.0");
import(path : "onshape/std/math.fs", version : "2656.0");
import(path : "onshape/std/feature.fs", version :"2656.0");
import(path : "onshape/std/query.fs", version : "2656.0");
import(path : "onshape/std/valueBounds.fs", version :"2656.0");
import(path : "onshape/std/error.fs", version : "2656.0");
import(path : "onshape/std/errorstringenum.gen.fs", version : "2656.0");
import(path : "onshape/std/geomOperations.fs", version : "2656.0");

export type TangentArc typecheck isTangentArc;

export predicate isTangentArc(value)
{
    value is map;
    value.start is Vector;
    value.end is Vector;
    value.center is Vector;
    value.radius is ValueWithUnits;
    value.normal is Vector;
}

function lineIntersection(p0 is Vector, v0 is Vector, p1 is Vector, v1 is Vector) returns Vector
{
    // Compute the closest points between two lines. If lines are nearly parallel,
    // return undefined.
    const crossDir = cross(normalize(v0), normalize(v1));
    if (norm(crossDir) < 1e-7)
        return undefined;
    const w0 = p0 - p1;
    const a = dot(v0, v0);
    const b = dot(v0, v1);
    const c = dot(v1, v1);
    const d = dot(v0, w0);
    const e = dot(v1, w0);
    const denom = a * c - b * b;
    if (abs(denom) < 1e-9)
        return undefined;
    const sc = (b * e - c * d) / denom;
    const tc = (a * e - b * d) / denom;
    const psc = p0 + v0 * sc;
    const ptc = p1 + v1 * tc;
    return (psc + ptc) / 2;
}

function circleFromPointTangentEnd(p0 is Vector, t0 is Vector, p1 is Vector) returns map
{
    t0 = normalize(t0);
    const d = p1 - p0;
    const n = cross(t0, d);
    // Avoid degenerate cases where the point lies on the tangent line
    // if (norm(n) < 1e-9 * meter)
    //     return undefined;
    const u = normalize(cross(n, t0));
    const denom = dot(d, u);
    // if (abs(denom) < 1e-9 * meter)
    //     return undefined;
    const r = dot(d, d) / (2 * denom);
    const center = p0 + u * r;
    return { 'center' : center, 'radius' : abs(r), 'normal' : normalize(n) };
}

export function splineToTangentArcs(bspline is BSplineCurve, segments is number) returns array
precondition
{
    segments > 0;
}
{
    var arcs = [];
    var parameters = [];
    for (var i = 0; i <= segments; i += 1)
        parameters = append(parameters, i / segments);
    const evalPos = evaluateSpline({ 'spline' : bspline, 'parameters' : parameters });
    const evalTan = evaluateSpline({ 'spline' : bspline, 'parameters' : parameters, 'nDerivatives' : 1 })[1];

    for (var i = 0; i < segments; i += 1)
    {
        const p0 = evalPos[0][i];
        const p1 = evalPos[0][i + 1];
        const t0 = normalize(evalTan[i]);
        const t1 = normalize(evalTan[i + 1]);

        var q = lineIntersection(p0, t0, p1, -t1);
        if (q == undefined)
            q = (p0 + p1) / 2;

        const arc1 = circleFromPointTangentEnd(p0, t0, q);
        const arc2 = circleFromPointTangentEnd(p1, -t1, q);
        if (arc1 != undefined)
            arcs = append(arcs, { 'start' : p0, 'end' : q, 'center' : arc1.center, 'radius' : arc1.radius, 'normal' : arc1.normal } as TangentArc);
        if (arc2 != undefined)
            arcs = append(arcs, { 'start' : q, 'end' : p1, 'center' : arc2.center, 'radius' : arc2.radius, 'normal' : arc2.normal } as TangentArc);
    }
    return arcs;
}

function arcToBSpline(start is Vector, end is Vector, center is Vector, normal is Vector) returns BSplineCurve
{
    const radius = norm(start - center);
    const xDir = normalize(start - center);
    const yDir = cross(normalize(normal), xDir);
    const v1 = normalize(end - center);
    const phi = angleBetween(xDir, v1, normal);
    const halfPhi = phi / 2;
    const mid = center + radius * (cos(halfPhi) * xDir + sin(halfPhi) * yDir);
    const weight = cos(halfPhi);
    return bSplineCurve({
                'degree' : 2,
                'isPeriodic' : false,
                'controlPoints' : [start, mid, end],
                'weights' : [1, weight, 1]
            });
}

annotation { "Feature Type Name" : "Spline to tangent arcs" }
export const splineToTangentArcsFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Spline edge", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.edge is Query;

        annotation { "Name" : "Segments" }
        isInteger(definition.segments, { (unitless) : [1, 1, 100] } as IntegerBoundSpec);
    }
    {
        const edge = try(evaluateQuery(context, definition.edge)[0]);
        if (edge == undefined)
            throw regenError(ErrorStringEnum.EXTRACT_WIRES_NEEDS_EDGES, ["edge"]);

        const bspline = evCurveDefinition(context, { 'edge' : edge, 'returnBSplinesAsOther' : false });
        if (!(bspline is BSplineCurve))
            throw regenError("yup");

        const arcs = splineToTangentArcs(bspline, definition.segments);
        for (var i = 0; i < size(arcs); i += 1)
        {
            const a = arcs[i];
            const bs = arcToBSpline(a.start, a.end, a.center, a.normal);
            opCreateBSplineCurve(context, id + i, { 'bSplineCurve' : bs });
        }
    }, { segments : 8, edge : qNothing() });
