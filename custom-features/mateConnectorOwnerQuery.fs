FeatureScript 2892;
import(path : "onshape/std/common.fs", version : "2892.0");

/**
 * Mate Connector Owner Query Utilities
 *
 * This module provides a helper function that resolves which parts own a given set
 * of mate connectors. This functionality fills the gap described in the Onshape
 * forums where qOwnerBody() and qAdjacent() do not work on mate connectors because
 * a mate connector is itself a body with BodyType.MATE_CONNECTOR, not a sub-entity
 * of an owning part.
 *
 * Key limitation to understand:
 *   Mate connectors that are created "live" inside a feature dialog (by selecting a
 *   face/edge/vertex interactively without having saved them as a named mate connector
 *   feature) have NO owner part. Those implicit, ephemeral connectors will not be
 *   associated with any body and will therefore not appear in the results of this
 *   function. Only named, explicitly saved mate connector features that were given an
 *   owner at creation time will resolve to an owner part here.
 *
 * Reference: https://forum.onshape.com/discussion (June 2020 thread by kevin_o_toole_1)
 */

/**
 * Returns a query for every modifiable body (solid, sheet, wire, or composite) that
 * owns at least one of the mate connectors matched by `mateConnectors`.
 *
 * The function works by iterating over every modifiable body in the context via
 * `qNthElement`, evaluating `qMateConnectorsOfParts` for each body, and comparing
 * the resulting transient entity references against the pre-evaluated `mateConnectors`
 * array. `qNthElement` is used instead of iterating over `evaluateQuery` results because
 * passing a `QueryType.TRANSIENT` query as the subquery of `qMateConnectorsOfParts`
 * silently returns nothing; the ownership lookup only works when the subquery is a
 * non-transient query type such as `QueryType.NTH_ELEMENT`.
 *
 * Mate connectors that were created without an explicit owner part (e.g. implicit
 * connectors generated inside a feature dialog) will not match any body and are
 * silently ignored; the returned query simply will not include an owner for them.
 *
 * @param context      : The active FeatureScript context.
 * @param mateConnectors {Query} : A query that resolves to one or more mate connector
 *                                 bodies (BodyType.MATE_CONNECTOR). Passing a query
 *                                 that resolves to nothing returns qNothing().
 * @returns {Query} : A union of every modifiable body that owns at least one connector
 *                    matched by `mateConnectors`, or qNothing() if no owner is found.
 */
export function qOwnerPartsOfMateConnectors(context is Context, mateConnectors is Query) returns Query
{
    // Pre-evaluate the requested connectors to transient entity references once.
    // These are used for direct entity comparison in the inner loop.
    const requestedConnectorEntities = evaluateQuery(context, mateConnectors);
    if (requestedConnectorEntities == [])
    {
        return qNothing();
    }

    // Collect all candidate owner bodies: solid parts, surface bodies, wire bodies,
    // and composite parts. This mirrors the filter used on the ownerPart field in
    // the mateConnector feature definition so that every legally ownable body type
    // is considered.
    const allModifiableBodies = qModifiableEntityFilter(
        qBodyType(
            qEverything(EntityType.BODY),
            [BodyType.SOLID, BodyType.SHEET, BodyType.WIRE, BodyType.COMPOSITE]
        )
    );

    const bodyCount = evaluateQueryCount(context, allModifiableBodies);

    // Accumulate bodies that own at least one of the supplied mate connectors.
    var ownerBodies = [] as array;

    for (var bodyIndex = 0; bodyIndex < bodyCount; bodyIndex += 1)
    {
        // Use qNthElement rather than a transient entity reference from evaluateQuery.
        // Passing a QueryType.TRANSIENT query as the subquery of qMateConnectorsOfParts
        // causes the ownership lookup to silently return nothing; qNthElement keeps the
        // query in a form the runtime can correctly resolve against the ownership table.
        const singleBodyQuery = qNthElement(allModifiableBodies, bodyIndex);

        // Evaluate the connectors owned by this body to transient entity references.
        const ownedConnectorEntities = evaluateQuery(context, qMateConnectorsOfParts(singleBodyQuery));

        // Walk both entity lists looking for any connector that appears in both sets.
        // Entity comparison with == on two transient queries from evaluateQuery is
        // reliable because the runtime assigns stable transient IDs within a single
        // feature execution.
        var foundOwnership = false;
        for (var ownedConnector in ownedConnectorEntities)
        {
            for (var requestedConnector in requestedConnectorEntities)
            {
                if (ownedConnector == requestedConnector)
                {
                    foundOwnership = true;
                    break;
                }
            }
            if (foundOwnership)
            {
                break;
            }
        }

        if (foundOwnership)
        {
            ownerBodies = append(ownerBodies, singleBodyQuery);
        }
    }

    return qUnion(ownerBodies);
}
