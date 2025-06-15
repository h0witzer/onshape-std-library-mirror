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
 * @returns {Query} union of all created cell bodies. Tracking queries are used
 *                  so the returned query references the cells even after the
 *                  input bodies are deleted.
 */
export function decomposeIntoCells(context is Context, id is Id, bodies is Query) returns Query
precondition
{
    bodies is Query;
}
{
    const bodyArray = evaluateQuery(context, bodies);
    const bodyCount = size(bodyArray);
    const touchMap = buildTouchingBodiesMap(context, bodyArray);
    const originalsQ = qUnion(bodyArray);
    var createdCellsQ = qNothing();
    var subsetIndex = 0;

    const subsets = generateTouchingSubsets(touchMap, bodyArray);
    for (var subset in subsets)
    {
        var subsetBodies = [];
        var includedIndices = {};
        for (var index in subset)
        {
            subsetBodies = append(subsetBodies, bodyArray[index]);
            includedIndices[index] = true;
        }

        var otherBodies = [];
        for (var i = 0; i < bodyCount; i += 1)
        {
            if (includedIndices[i] == undefined)
            {
                otherBodies = append(otherBodies, bodyArray[i]);
            }
        }

        // Only create subsets if the input parts are touching or intersecting
        if (!bodiesInterfereOrContain(touchMap, subsetBodies))
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
        }

        if (!isQueryEmpty(context, resultQ))
        {
            createdCellsQ = qUnion([createdCellsQ, resultQ]);
        }
        subsetIndex += 1;
    }


    // Remove the original bodies so only the decomposed cells remain
    const cleanupId = id + unstableIdComponent(subsetIndex) + "deleteOriginals";
    opDeleteBodies(context, cleanupId, { "entities" : originalsQ });
    // Build and return a query for all newly created cell bodies
    return createdCellsQ;

}


/**
 * Generate all index subsets where every body touches all others
 * in the subset based on the provided touching map. This avoids
 * creating subsets for completely disjoint bodies.
 */

function buildTouchingSubsetsRec(prefix is array, start is number, bodies is array,
    touchMap is map) returns array
{
    var subsets = [];
    if (size(prefix) > 0)
        subsets = append(subsets, prefix);

    for (var i = start; i < size(bodies); i += 1)
    {
        var canAdd = true;
        const candidate = bodies[i];
        for (var index in prefix)
        {
            const existing = bodies[index];
            var touches = false;
            if (touchMap[existing] != undefined)
            {
                for (var entry in touchMap[existing])
                {
                    if (entry == candidate)
                    {
                        touches = true;
                        break;
                    }
                }
            }
            if (!touches)
            {
                canAdd = false;
                break;
            }
        }

        if (canAdd)
            subsets = concatenateArrays([subsets,
                        buildTouchingSubsetsRec(append(prefix,
                            i),
                        i + 1,
                        bodies,
                        touchMap)]);
    }


    return subsets;
}

function generateTouchingSubsets(touchMap is map, bodies is array) returns array
{
    return buildTouchingSubsetsRec([], 0, bodies, touchMap);
}

function buildTouchingBodiesMap(context is Context, bodies is array) returns map
{
    var touchingMap = {};
    const collisions = evCollision(context, { "tools" : qUnion(bodies),
                "targets" : qUnion(bodies) });
    for (var collision in collisions)
    {
        if (collision.toolBody == collision.targetBody)
            continue;

        const clashType = collision['type'];
        if (clashType == ClashType.INTERFERE ||
            clashType == ClashType.TARGET_IN_TOOL ||
            clashType == ClashType.TOOL_IN_TARGET)
        {
            if (touchingMap[collision.toolBody] == undefined)
                touchingMap[collision.toolBody] = [];
            if (touchingMap[collision.targetBody] == undefined)
                touchingMap[collision.targetBody] = [];
            touchingMap[collision.toolBody] = append(touchingMap[collision.toolBody], collision.targetBody);
            touchingMap[collision.targetBody] = append(touchingMap[collision.targetBody], collision.toolBody);
        }
    }
    return touchingMap;
}

function bodiesInterfereOrContain(touchMap is map, bodies is array) returns boolean
{
    // If there is only one body in the subset we treat it as valid
    if (size(bodies) <= 1)
        return true;

    // Ensure every pair of bodies either interferes or one contains the other
    for (var i = 0; i < size(bodies); i += 1)
    {
        for (var j = i + 1; j < size(bodies); j += 1)
        {

            var pairValid = false;
            if (touchMap[bodies[i]] != undefined)
            {

                for (var entry in touchMap[bodies[i]])
                {

                    if (entry == bodies[j])
                    {
                        pairValid = true;
                        break;
                    }
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
