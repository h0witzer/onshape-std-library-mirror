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
 * The function works by iterating over every modifiable body in the context and
 * checking whether the intersection of the caller-supplied connector query and the
 * connectors owned by that body is non-empty. Because the test requires evaluating
 * queries against the context this is a context-taking function rather than a pure
 * query builder.
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

    const candidateBodies = evaluateQuery(context, allModifiableBodies);

    // Accumulate bodies that own at least one of the supplied mate connectors.
    var ownerBodies = [] as array;

    for (var candidateBody in candidateBodies)
    {
        // qMateConnectorsOfParts returns all mate connectors whose owner field was
        // set to this body when opMateConnector was called.
        const connectorsOwnedByThisBody = qMateConnectorsOfParts(candidateBody);

        // If the intersection with the caller's connector query is non-empty, this
        // body is an owner of at least one requested connector.
        const matchingConnectors = qIntersection([mateConnectors, connectorsOwnedByThisBody]);

        if (!isQueryEmpty(context, matchingConnectors))
        {
            ownerBodies = append(ownerBodies, candidateBody);
        }
    }

    return qUnion(ownerBodies);
}
