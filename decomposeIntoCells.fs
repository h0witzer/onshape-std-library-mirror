FeatureScript 2679;
import(path : "onshape/std/common.fs", version : "2679.0");
import(path : "onshape/std/geomOperations.fs", version : "2679.0");
import(path : "onshape/std/transform.fs", version : "2679.0");
import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2679.0");

/**
 * Decompose the regions defined by a set of bodies into non overlapping cells.
 * Each resulting body corresponds to the unique intersection of one or more
 * input bodies with all other bodies removed.
 *
 * @param context   {@link Context}
 * @param id        {@link Id} used as a base for created operations
 * @param bodies    {Query} query selecting any number of bodies
 * @returns {Query} union of all created cell bodies
 */
export function decomposeIntoCells(context is Context, id is Id, bodies is Query) returns Query
precondition
{
    bodies is Query;
}
{
    const bodyArray = evaluateQuery(context, bodies);
    const bodyCount = size(bodyArray);
    var cellQueries = [];
    var subsetIndex = 0;

    const subsets = generateIndexSubsets(bodyCount);
    for (var subset in subsets)
    {
        var subsetBodies = [];
        var otherBodies = [];
        for (var i = 0; i < bodyCount; i += 1)
        {
            if (subsetContains(subset, i))
            {
                subsetBodies = append(subsetBodies, bodyArray[i]);
            }
            else
            {
                otherBodies = append(otherBodies, bodyArray[i]);
            }
        }

        // Only create subsets if the input parts are touching or intersecting
        if (!bodiesInterfereOrContain(context, subsetBodies))
        {
            subsetIndex += 1;
            continue;
        }

        var resultQ;
        if (size(subsetBodies) == 1)
        {
            const copyId = id + unstableIdComponent(subsetIndex) + "copy";
            opPattern(context, copyId, {
                        "entities" : subsetBodies[0],
                        "transforms" : [identityTransform()],
                        "instanceNames" : ["1"]
                    });
            resultQ = qCreatedBy(copyId, EntityType.BODY);
        }
        else
        {
            const intersectId = id + unstableIdComponent(subsetIndex) + "intersect";
            opBoolean(context, intersectId, {
                        "tools" : qUnion(subsetBodies),
                        "operationType" : BooleanOperationType.INTERSECTION,
                        "keepTools" : true
                    });
            resultQ = qCreatedBy(intersectId, EntityType.BODY);
        }
        if (isQueryEmpty(context, resultQ))
        {
            subsetIndex += 1;
            continue;
        }

        if (size(otherBodies) > 0)
        {
            const subtractId = id + unstableIdComponent(subsetIndex) + "subtract";
            opBoolean(context, subtractId, {
                        "targets" : resultQ,
                        "tools" : qUnion(otherBodies),
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "keepTools" : true
                    });
            resultQ = qCreatedBy(subtractId, EntityType.BODY);
        }

        if (!isQueryEmpty(context, resultQ))
        {
            cellQueries = append(cellQueries, resultQ);
        }
        subsetIndex += 1;
    }

    const cellsQ = qUnion(cellQueries);

    // Remove the original bodies so only the decomposed cells remain
    const cleanupId = id + unstableIdComponent(subsetIndex) + "deleteOriginals";
    opDeleteBodies(context, cleanupId, { "entities" : bodies });

    return cellsQ;
}

function subsetContains(subset is array, value is number) returns boolean
{
    for (var elem in subset)
    {
        if (elem == value)
        {
            return true;
        }
    }
    return false;
}


function buildSubsets(prefix is array, start is number, count is number) returns array
{
    var result = [];
    for (var i = start; i < count; i += 1)
    {
        var next = append(prefix, i);
        result = concatenateArrays([result, [next], buildSubsets(next, i + 1, count)]);
    }
    return result;
}

function generateIndexSubsets(count is number) returns array
{
    return buildSubsets([], 0, count);
}

function bodiesInterfereOrContain(context is Context, bodies is array) returns boolean
{
    // If there is only one body in the subset we treat it as valid
    if (size(bodies) <= 1)
        return true;

    // Ensure every pair of bodies either interferes or one contains the other
    for (var i = 0; i < size(bodies); i += 1)
    {
        for (var j = i + 1; j < size(bodies); j += 1)
        {
            const collisions = evCollision(context, {
                        "tools" : bodies[i],
                        "targets" : bodies[j]
                    });

            var pairValid = false;
            for (var collision in collisions)
            {
                const clashType = collision['type'];
                if (clashType == ClashType.INTERFERE ||
                    clashType == ClashType.TARGET_IN_TOOL ||
                    clashType == ClashType.TOOL_IN_TARGET)
                {
                    pairValid = true;
                    break;
                }
            }

            if (!pairValid)
            {
                return false;
            }
        }
    }

    return true;
}
