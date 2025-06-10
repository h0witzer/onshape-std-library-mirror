FeatureScript 2679; /* Custom feature for approximating a spline with tangent arcs */
import(path : "onshape/std/common.fs", version : "2679.0");
import(path : "onshape/std/feature.fs", version : "2679.0");
import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/math.fs", version : "2679.0");
import(path : "onshape/std/valueBounds.fs", version : "2679.0");
import(path : "onshape/std/error.fs", version : "2679.0");
import(path : "onshape/std/errorstringenum.gen.fs", version : "2679.0");

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

annotation { "Feature Type Name" : "Spline to minimal tangent arcs",
        "Editing Logic Function" : "splineToMinimalTangentArcsEditLogic",
        "Description" : "Custom feature for approximating a spline with tangent arcs"}
export const splineToMinimalTangentArcs = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Spline edge", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.edge is Query;

        annotation { "Name" : "Deviation tolerance", "Default" : 1 * millimeter }
        isLength(definition.tolerance, NONNEGATIVE_LENGTH_BOUNDS);

        annotation { "UIHint" : UIHint.ALWAYS_HIDDEN }
        isAnything(definition.cachedArcs);
        annotation { "Name" : "Refine" }
        isButton(definition.refine);
    }
    {
        if (definition.cachedArcs == undefined)
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["edge"]);

        for (var i = 0; i < definition.cachedArcs; i += 1)
        {
            const a = definition.cachedArcs[i];
            const bs = arcToBSpline(a.start, a.end, a.center, a.normal);
            opCreateBSplineCurve(context, id + i, { 'bSplineCurve' : bs });
        }
    }, { tolerance : 1 * millimeter, edge : qNothing(), cachedArcs : undefined });


function lineIntersection(p0 is Vector, v0 is Vector, p1 is Vector, v1 is Vector) returns Vector
{
    // if (norm(v0) <= TOLERANCE.zeroLength * meter || norm(v1) <= TOLERANCE.zeroLength * meter)
    //     return undefined;

    const crossDir = cross(normalize(v0), normalize(v1));
    // if (norm(crossDir) <= TOLERANCE.zeroLength)
    //     return undefined;

    const w0 = p0 - p1;
    const a = dot(v0, v0);
    const b = dot(v0, v1);
    const c = dot(v1, v1);
    const d = dot(v0, w0);
    const e = dot(v1, w0);
    const denom = a * c - b * b;
    // if (abs(denom) <= TOLERANCE.zeroLength)
    //     return undefined;

    const sc = (b * e - c * d) / denom;
    const tc = (a * e - b * d) / denom;
    const psc = p0 + v0 * sc;
    const ptc = p1 + v1 * tc;
    return (psc + ptc) / 2;
}

function circleFromPointTangentEnd(p0 is Vector, t0 is Vector, p1 is Vector) returns map
{
    if (norm(t0) <= TOLERANCE.zeroLength * meter)
        return undefined;

    t0 = normalize(t0);
    const d = p1 - p0;
    if (norm(d) <= TOLERANCE.zeroLength * meter)
        return undefined;

    const n = cross(t0, d);

    if (norm(n) <= TOLERANCE.zeroLength * meter)
        return undefined;

    const uVec = cross(n, t0);
    if (norm(uVec) <= TOLERANCE.zeroLength * meter)
        return undefined;
    const u = normalize(uVec);

    const denom = dot(d, u);

    if (abs(denom) <= TOLERANCE.zeroLength * meter)
        return undefined;

    const r = dot(d, d) / (2 * denom);
    if (r != r)
        return undefined;

    const center = p0 + u * r;
    return { 'center' : center, 'radius' : abs(r), 'normal' : normalize(n) };
}

function positionOnArc(arc is TangentArc, fraction is number) returns Vector
precondition
{
    fraction >= 0; fraction <= 1;
}
{
    const radiusVec = arc.start - arc.center;
    const endVec = arc.end - arc.center;
    const angle = angleBetween(radiusVec, endVec, arc.normal) * fraction;
    const xDir = normalize(radiusVec);
    const yDir = cross(normalize(arc.normal), xDir);
    return arc.center + arc.radius * (cos(angle) * xDir + sin(angle) * yDir);
}

function evaluatePosTan(bspline is BSplineCurve, parameter is number) returns map
{
    const vals = evaluateSpline({ 'spline' : bspline, 'parameters' : [parameter], 'nDerivatives' : 1 });
    return { 'pos' : vals[0][0], 'tan' : vals[1][0] };
}

function computeBiarc(bspline is BSplineCurve, t0 is number, t1 is number) returns array
{
    const start = evaluatePosTan(bspline, t0);
    const end = evaluatePosTan(bspline, t1);
    var startTan = start.tan;
    var endTan = end.tan;

    if (norm(startTan) <= TOLERANCE.zeroLength * meter)
        startTan = end.pos - start.pos;
    if (norm(endTan) <= TOLERANCE.zeroLength * meter)
        endTan = start.pos - end.pos;

    var q = lineIntersection(start.pos, startTan, end.pos, -endTan);
    if (q == undefined)
        q = (start.pos + end.pos) / 2;
    const arc1 = circleFromPointTangentEnd(start.pos, startTan, q);
    const arc2 = circleFromPointTangentEnd(end.pos, -endTan, q);
    var arcs = [];
    if (arc1 != undefined)
        arcs = append(arcs, { 'start' : start.pos, 'end' : q, 'center' : arc1.center, 'radius' : arc1.radius, 'normal' : arc1.normal } as TangentArc);
    if (arc2 != undefined)
        arcs = append(arcs, { 'start' : q, 'end' : end.pos, 'center' : arc2.center, 'radius' : arc2.radius, 'normal' : arc2.normal } as TangentArc);
    return arcs;
}

function deviationFromBiarc(bspline is BSplineCurve, t0 is number, t1 is number, arcs is array) returns ValueWithUnits
{
    const samples = [0.25, 0.5, 0.75];
    var maxDev = 0 * meter;
    if (size(arcs) == 0)
        return maxDev;
    for (var f in samples)
    {
        const param = t0 + (t1 - t0) * f;
        const splinePos = evaluateSpline({ 'spline' : bspline, 'parameters' : [param] })[0][0];
        var arcPos;
        if (f < 0.5)
            arcPos = positionOnArc(arcs[0], f * 2);
        else
            arcPos = positionOnArc(arcs[size(arcs) - 1], (f - 0.5) * 2);
        const dev = norm(splinePos - arcPos);
        if (maxDev < dev)
            maxDev = dev;
    }
    return maxDev;
}

function approximateSegment(bspline is BSplineCurve, t0 is number, t1 is number, tolerance is ValueWithUnits, depth is number) returns array
{
    const arcs = computeBiarc(bspline, t0, t1);
    if (size(arcs) == 0)
        return [];

    const dev = deviationFromBiarc(bspline, t0, t1, arcs);
    if (dev > tolerance && depth < 10)
    {
        const mid = (t0 + t1) / 2;
        return approximateSegment(bspline, t0, mid, tolerance, depth + 1) ~ approximateSegment(bspline, mid, t1, tolerance, depth + 1);
    }
    return arcs;
}

function splineToMinimalArcs(bspline is BSplineCurve, tolerance is ValueWithUnits) returns array
{
    return approximateSegment(bspline, 0, 1, tolerance, 0);
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

export function splineToMinimalTangentArcsEditLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, clickedButton is string) returns map
{
    if (definition.cachedArcs != undefined && clickedButton != "refine" &&
        oldDefinition != {} &&
        definition.edge == oldDefinition.edge &&
        definition.tolerance == oldDefinition.tolerance)
    {
        return definition;
    }

    if (isQueryEmpty(context, definition.edge))
        return definition;

    const edge = try(evaluateQuery(context, definition.edge)[0]);
    if (edge == undefined)
        return definition;

    const bspline = evCurveDefinition(context, { 'edge' : edge, 'returnBSplinesAsOther' : false });
    if (!(bspline is BSplineCurve))
        return definition;

    const arcs = splineToMinimalArcs(bspline, definition.tolerance);
    definition.cachedArcs = arcs;
    return definition;
}
