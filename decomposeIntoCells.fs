FeatureScript 2679;

import(path : "onshape/std/context.fs", version : "2679.0");
import(path : "onshape/std/geomOperations.fs", version : "2679.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2679.0");
import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/containers.fs", version : "2679.0");

/**
 * Recursively break down the supplied bodies into non-overlapping cells. Each
 * pair of bodies is intersected and the intersection removed from the sources
 * until no overlaps remain. Returns a query for all resulting cell bodies.
 */
export function decomposeIntoCells(context is Context, id is Id, bodies is Query) returns Query
precondition
{
    bodies is Query;
}
{
    var bodyList = evaluateQuery(context, bodies);
    var cells = [];
    var changed = true;
    var counter = 0;
    while (changed)
    {
        changed = false;
        for (var i = 0; i < size(bodyList); i += 1)
        {
            for (var j = i + 1; j < size(bodyList); j += 1)
            {
                const baseId = id + unstableIdComponent(counter);
                try silent(opBoolean(context, baseId + "intersect", {
                                "tools" : qUnion([bodyList[i], bodyList[j]]),
                                "operationType" : BooleanOperationType.INTERSECTION,
                                "keepTools" : true
                            }));
                const intersectionQ = qCreatedBy(baseId + "intersect", EntityType.BODY);
                if (isQueryEmpty(context, intersectionQ))
                    continue;
                changed = true;
                try silent(opBoolean(context, baseId + "subA", {
                                "tools" : intersectionQ,
                                "targets" : bodyList[i],
                                "operationType" : BooleanOperationType.SUBTRACTION,
                                "keepTools" : true
                            }));
                bodyList[i] = qCreatedBy(baseId + "subA", EntityType.BODY);
                try silent(opBoolean(context, baseId + "subB", {
                                "tools" : intersectionQ,
                                "targets" : bodyList[j],
                                "operationType" : BooleanOperationType.SUBTRACTION,
                                "keepTools" : true
                            }));
                bodyList[j] = qCreatedBy(baseId + "subB", EntityType.BODY);
                cells = append(cells, intersectionQ);
                counter += 1;
            }
        }
    }
    const result = concatenateArrays([bodyList, cells]);
    return qUnion(result);
}
