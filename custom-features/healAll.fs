FeatureScript 2737;
import(path : "onshape/std/common.fs", version : "2737.0");

annotation 
{ 
    "Feature Type Name" : "Heal All", 
    "Feature Type Description" : "Heals all holes in selected faces"
    }
export const HealAll = defineFeature(function(context is Context, id is Id, definition is map)

precondition
{
    annotation 
    { 
        "Name" : "Faces to Heal", 
        "Filter" : EntityType.FACE
        ,"UIHint" : UIHint.SHOW_CREATE_SELECTION 
    }
    definition.faces is Query;
    
    annotation 
    { 
        "Name" : "Additional Faces to Delete", 
        "Filter" : EntityType.FACE
        ,"UIHint" : UIHint.SHOW_CREATE_SELECTION 
    }
    definition.remove is Query;
    
    annotation { "Name" : "Interior", "Default" : true}
    definition.interior is boolean;

    annotation { "Name" : "Perimeter" }
    definition.perimeter is boolean;
}
{
    if(!definition.interior && !definition.perimeter)
        reportFeatureWarning(context, id, "Must select Interior and/or Perimeter");
    
    const picks = evaluateQuery(context, definition.faces);

    var garbage = [];

    for (var face in picks)
    {
        const index = indexOf(picks, face);

        var loops = {};
            loops.a = evaluateQuery(context, qLoopEdges(face));
            loops.mid = [];

        for (var edge in loops.a)
            loops.mid = append(loops.mid, 
                evEdgeTangentLine(context, {"edge" : edge, "parameter" : 0.5}).origin);

        var groups = groupClosedCurvesByAdj(context, qUnion(loops.a), false);

        var maxLength = 0 * meter;
        var boundaryIndex = -1;
        for (var group in groups)
        {
            var l = evLength(context, {"entities" : qUnion(group)});
            if(l > maxLength)
            {
                maxLength = l;
                boundaryIndex = indexOf(groups, group);
            }
        }        

        if(definition.interior)
        {
            for (var i = 0; i < size(groups); i += 1)
            {
                if(i != boundaryIndex)
                {
                    for (var curve in groups[i])
                    {
                        garbage = append(garbage, qAdjacent(curve, AdjacencyType.EDGE, EntityType.FACE) -> qSubtraction(face));
    
                        addDebugEntities(context, curve);
                    }
                }                
            }
        }

        if(definition.perimeter)
        {
            const surf = evApproximateBSplineSurface(context, {"face" : face});
            
            const bssname = "bSplineSurface"~index;
            
            opCreateBSplineSurface(context, id + bssname, {"bSplineSurface" : surf.bSplineSurface});        
            
            var surfLoops = evaluateQuery(context, qLoopEdges(qCreatedBy(id + bssname, EntityType.FACE)));
    
            for (var curve in groups[boundaryIndex])
            {
                const i = boundaryIndex;
            
                var curveNum = indexOf(loops.a, curve); 
    
                var valid = true;
            
                for (var sl in surfLoops)
                {
                    const dist = evDistance(context, {"side0" : loops.mid[curveNum],"side1" : sl}).distance;
                    
                    if(tolerantEqualsZero(dist))
                    {
                        valid = false;
            
                        break;
                    }
                }
                
                if(valid)
                {
                    garbage = append(garbage, qAdjacent(curve, AdjacencyType.EDGE, EntityType.FACE) -> qSubtraction(face));

                    addDebugEntities(context, curve);
                }
            }
            
            const deletename = "deleteSurface"~index;

            opDeleteBodies(context, id + deletename, {"entities" : qOwnerBody(qCreatedBy(id + bssname))});
        }
        
    }
    
    try
    {
        for (var trash in garbage)
            garbage = append(garbage, qConcaveConnectedFaces(trash));
    
        for (var r in evaluateQuery(context, definition.remove))
            garbage = append(garbage, r);
        
        addDebugEntities(context, qUnion(garbage));

        opDeleteFace(context, id + "deleteFace1", {
            "deleteFaces" : qUnion(garbage),
            "includeFillet" : false,
            "capVoid" : false,
            "leaveOpen" : false
        });
    }

});

export function groupClosedCurvesByAdj(context is Context, edgeQuery is Query, debugOpen is boolean) returns array
{
    // 1) Collect edges and neighbor indices
    var edges = evaluateQuery(context, edgeQuery);
    const n = size(edges);

    var neighbors = []; // array<array<number>>
    for (var i = 0; i < n; i += 1)
    {
        var adj = evaluateQuery(context, qAdjacent(edges[i], AdjacencyType.VERTEX, EntityType.EDGE));
        var idxs = [];
        for (var e in adj)
        {
            const j = indexOf(edges, e) as number;
            if (j != -1 && j != i)
                idxs = append(idxs, j);
        }
        neighbors = append(neighbors, idxs);
    }

    // 2) Walk loops
    var used = [];
    for (var i = 0; i < n; i += 1) used = append(used, false);

    var groups = [];

    for (var seed = 0; seed < n; seed += 1)
    {
        if (used[seed]) continue;

        // Circle (no vertex adjacency): detect by endpoints equal at [0,1]
        if (size(neighbors[seed]) == 0)
        {
            var tl = evEdgeTangentLines(context, { "edge" : edges[seed], "parameters" : [0, 1] });
            if (tolerantEquals(tl[0].origin, tl[1].origin))
            {
                used[seed] = true;
                groups = append(groups, [edges[seed]]);
            }
            else if (debugOpen)
            {
                addDebugEntities(context, edges[seed]);
            }
            continue;
        }

        var chainIdx = [];
        var curr = seed as number;
        var prev = -1 as number;
        var closed = false;

        // Up to n+1 steps is sufficient to either close or stall on simple loops
        for (var steps = 0; steps < n + 2; steps += 1)
        {
            chainIdx = append(chainIdx, curr);
            used[curr] = true;

            if (curr == seed && size(chainIdx) > 1) { closed = true; break; }

            // Next candidates: neighbors excluding where we just came from
            var cand = [];
            for (var k = 0; k < size(neighbors[curr]); k += 1)
            {
                const j = neighbors[curr][k];
                if (j != prev) cand = append(cand, j);
            }

            // Prefer an unused neighbor
            var next = -1 as number;
            for (var k = 0; k < size(cand); k += 1)
                if (!used[cand[k]]) { next = cand[k]; break; }

            // If none unused, but seed is adjacent, we can close
            if (next == -1)
            {
                var canClose = false;
                for (var k = 0; k < size(neighbors[curr]); k += 1)
                    if (neighbors[curr][k] == seed && size(chainIdx) > 1) { canClose = true; break; }

                if (canClose) { closed = true; }
                break;
            }

            prev = curr;
            curr = next;
        }

        if (closed)
        {
            var g = [];
            for (var t = 0; t < size(chainIdx); t += 1) g = append(g, edges[chainIdx[t]]);
            groups = append(groups, g);
        }
        else
        {
            // Release marks so another seed/path can try (keeps things non-destructive)
            for (var t = 0; t < size(chainIdx); t += 1) used[chainIdx[t]] = false;
        }
    }

    return groups;
}






