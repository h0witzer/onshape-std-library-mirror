FeatureScript 2892;
import(path : "onshape/std/common.fs", version : "2892.0");

/**
 * Mate Connector Owner Query Tester
 *
 * This feature provides an interactive testing environment for the
 * qOwnerPartsOfMateConnectors() function defined in mateConnectorOwnerQuery.fs.
 *
 * Usage:
 *   1. Create one or more named mate connector features in the Part Studio, each
 *      associated with an owner part.
 *   2. Add this tester feature to the feature tree.
 *   3. Select the mate connector(s) you want to resolve in the "Mate Connectors" field.
 *   4. The feature will report how many owner parts were found and highlight them in
 *      the 3D viewport using debug coloring.
 *
 * Expected results:
 *   - Named mate connectors that were created with an explicit owner part should resolve
 *     to that owner body. The owner body will be highlighted in green.
 *   - Mate connectors that have no owner (implicit / live connectors from a dialog) will
 *     not match any body; the feature will report zero owners found for those connectors.
 *   - Selecting multiple connectors owned by different parts should highlight all of
 *     those distinct owner parts.
 *
 * This feature imports the helper function from mateConnectorOwnerQuery.fs. When
 * deploying to an Onshape document, replace the import below with the correct
 * document-external path for your copy of mateConnectorOwnerQuery.fs.
 *
 * Non-standard dependency:
 *   qOwnerPartsOfMateConnectors() -- defined in
 *   custom-features/mateConnectorOwnerQuery.fs (this repository)
 */

// NOTE: In a deployed Onshape document, replace the inline function definition at
// the bottom of this file with an external-document import for mateConnectorOwnerQuery.fs
// once it has been published, e.g.:
//   import(path : "<document-id>", version : "<version-id>"); // mateConnectorOwnerQuery.fs

/**
 * Enumeration controlling which subset of debug information is reported.
 */
export enum MateConnectorOwnerDisplayMode
{
    annotation { "Name" : "Highlight owner parts" }
    HIGHLIGHT_OWNERS,
    annotation { "Name" : "Report counts only" }
    REPORT_COUNTS_ONLY
}

annotation { "Feature Type Name" : "Mate Connector Owner Query Tester",
             "Feature Name Template" : "MC Owner Tester",
             "UIHint" : UIHint.NO_PREVIEW_PROVIDED }
export const mateConnectorOwnerQueryTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Mate Connectors",
                     "Filter" : BodyType.MATE_CONNECTOR,
                     "UIHint" : UIHint.ALLOW_QUERY_ORDER }
        definition.mateConnectors is Query;

        annotation { "Name" : "Display mode" }
        definition.displayMode is MateConnectorOwnerDisplayMode;
    }
    {
        // Validate that the user has selected at least one mate connector.
        if (isQueryEmpty(context, definition.mateConnectors))
        {
            reportFeatureInfo(context, id,
                "No mate connectors selected. Select one or more named mate connectors " ~
                "to resolve their owner parts.");
            return;
        }

        // Evaluate the selected mate connectors so we can report the count.
        const selectedConnectors = evaluateQuery(context, definition.mateConnectors);
        const selectedConnectorCount = size(selectedConnectors);

        // -------------------------------------------------------------------
        // Core call: resolve owner parts for the selected mate connectors.
        // -------------------------------------------------------------------
        const ownerPartsQuery = qOwnerPartsOfMateConnectors(context, definition.mateConnectors);
        const ownerPartEntities = evaluateQuery(context, ownerPartsQuery);
        const ownerPartCount = size(ownerPartEntities);

        // -------------------------------------------------------------------
        // Report results to the feature info panel.
        // -------------------------------------------------------------------
        if (ownerPartCount == 0)
        {
            reportFeatureInfo(context, id,
                "No owner parts found for " ~ selectedConnectorCount ~ " selected connector(s). " ~
                "This is expected for implicit/live mate connectors that have no saved owner part.");
        }
        else
        {
            const connectorWord = (selectedConnectorCount == 1) ? "connector" : "connectors";
            const partWord      = (ownerPartCount == 1) ? "part" : "parts";
            reportFeatureInfo(context, id,
                "Found " ~ ownerPartCount ~ " owner " ~ partWord ~
                " for " ~ selectedConnectorCount ~ " selected " ~ connectorWord ~ ".");
        }

        // -------------------------------------------------------------------
        // Optionally highlight the resolved owner parts in the viewport.
        // -------------------------------------------------------------------
        if (definition.displayMode == MateConnectorOwnerDisplayMode.HIGHLIGHT_OWNERS
                && ownerPartCount > 0)
        {
            debug(context, ownerPartsQuery, DebugColor.GREEN);
        }
    },
    {
        "displayMode" : MateConnectorOwnerDisplayMode.HIGHLIGHT_OWNERS
    });

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
