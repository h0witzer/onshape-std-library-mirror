FeatureScript 2892;
import(path : "onshape/std/common.fs", version : "2892.0");
import(path : "df6ff502d6e067dbccb62db7", version : "48e4b06a3a673f47ce0c5c2f"); // mateConnectorOwnerQuery.fs

/**
 * Mate Connector Owner Query Tester
 *
 * This feature provides an interactive diagnostic environment for the two directions
 * of mate-connector ownership resolution:
 *
 *   Mode 1 - Connector -> Owner Parts  (qOwnerPartsOfMateConnectors)
 *     Select one or more named mate connector bodies. The feature resolves which
 *     modifiable body (solid, sheet, wire, or composite) owns each connector and
 *     highlights those owner bodies in GREEN.
 *
 *   Mode 2 - Part -> Owned Connectors  (qMateConnectorsOfParts)
 *     Select a modifiable part body. The feature calls qMateConnectorsOfParts
 *     directly on that part and highlights the mate connectors it owns in BLUE.
 *     Use this mode to verify that qMateConnectorsOfParts is returning results
 *     before diagnosing the reverse lookup.
 *
 * Non-standard dependency:
 *   qOwnerPartsOfMateConnectors() -- imported from mateConnectorOwnerQuery.fs
 *   (path : "df6ff502d6e067dbccb62db7", version : "48e4b06a3a673f47ce0c5c2f")
 */

/**
 * Selects which direction of the ownership relationship to explore.
 *   OWNER_PARTS_OF_CONNECTOR : connector selection -> owner part lookup
 *   CONNECTORS_OF_PART       : part selection -> owned connector lookup
 */
export enum TesterMode
{
    annotation { "Name" : "Connector -> Owner Parts (qOwnerPartsOfMateConnectors)" }
    OWNER_PARTS_OF_CONNECTOR,
    annotation { "Name" : "Part -> Owned Connectors (qMateConnectorsOfParts)" }
    CONNECTORS_OF_PART
}

annotation { "Feature Type Name" : "Mate Connector Owner Query Tester",
             "Feature Name Template" : "MC Owner Tester",
             "UIHint" : UIHint.NO_PREVIEW_PROVIDED }
export const mateConnectorOwnerQueryTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Test Mode" }
        definition.testerMode is TesterMode;

        if (definition.testerMode == TesterMode.OWNER_PARTS_OF_CONNECTOR)
        {
            annotation { "Name" : "Mate Connectors",
                         "Filter" : BodyType.MATE_CONNECTOR,
                         "UIHint" : UIHint.ALLOW_QUERY_ORDER }
            definition.mateConnectors is Query;
        }

        if (definition.testerMode == TesterMode.CONNECTORS_OF_PART)
        {
            annotation { "Name" : "Part",
                         "Filter" : EntityType.BODY && (BodyType.SOLID || GeometryType.MESH
                                 || BodyType.SHEET || BodyType.WIRE || BodyType.COMPOSITE)
                                 && AllowMeshGeometry.YES && ModifiableEntityOnly.YES,
                         "MaxNumberOfPicks" : 1 }
            definition.ownerPart is Query;
        }

        annotation { "Name" : "Highlight Results", "Default" : true }
        definition.highlightResults is boolean;
    }
    {
        if (definition.testerMode == TesterMode.OWNER_PARTS_OF_CONNECTOR)
        {
            testOwnerPartsOfConnector(context, id, definition);
        }
        else if (definition.testerMode == TesterMode.CONNECTORS_OF_PART)
        {
            testConnectorsOfPart(context, id, definition);
        }
    },
    {
        "testerMode"       : TesterMode.OWNER_PARTS_OF_CONNECTOR,
        "highlightResults" : true
    });

/**
 * Runs the OWNER_PARTS_OF_CONNECTOR test:
 * Calls qOwnerPartsOfMateConnectors() on the user-selected connectors and
 * reports how many owner parts were resolved. Owner parts are highlighted
 * in GREEN when "Highlight Results" is enabled.
 *
 * @param context    : Active FeatureScript context.
 * @param id         : Feature ID.
 * @param definition : Feature definition map containing:
 *                     - mateConnectors {Query}     : Selected mate connector bodies.
 *                     - highlightResults {boolean}  : Whether to show debug highlighting.
 */
function testOwnerPartsOfConnector(context is Context, id is Id, definition is map)
{
    if (isQueryEmpty(context, definition.mateConnectors))
    {
        reportFeatureInfo(context, id,
            "No mate connectors selected. Select one or more named mate connectors " ~
            "to resolve their owner parts.");
        return;
    }

    const selectedConnectorCount = size(evaluateQuery(context, definition.mateConnectors));

    const ownerPartsQuery    = qOwnerPartsOfMateConnectors(context, definition.mateConnectors);
    const ownerPartEntities  = evaluateQuery(context, ownerPartsQuery);
    const ownerPartCount     = size(ownerPartEntities);

    if (ownerPartCount == 0)
    {
        reportFeatureInfo(context, id,
            "No owner parts found for " ~ selectedConnectorCount ~ " selected connector(s). " ~
            "Try Mode 2 (Part -> Owned Connectors) to verify qMateConnectorsOfParts returns " ~
            "results for the part you expect to be the owner.");
    }
    else
    {
        const connectorWord = (selectedConnectorCount == 1) ? "connector" : "connectors";
        const partWord      = (ownerPartCount == 1) ? "part" : "parts";
        reportFeatureInfo(context, id,
            "Found " ~ ownerPartCount ~ " owner " ~ partWord ~
            " for " ~ selectedConnectorCount ~ " selected " ~ connectorWord ~ ".");
    }

    if (definition.highlightResults && ownerPartCount > 0)
    {
        debug(context, ownerPartsQuery, DebugColor.GREEN);
    }
}

/**
 * Runs the CONNECTORS_OF_PART test:
 * Calls qMateConnectorsOfParts() directly on the user-selected part and reports
 * how many mate connectors are owned by that part. Owned connectors are highlighted
 * in BLUE when "Highlight Results" is enabled.
 *
 * Use this mode to confirm that qMateConnectorsOfParts works for a given part
 * before diagnosing the reverse lookup in Mode 1.
 *
 * @param context    : Active FeatureScript context.
 * @param id         : Feature ID.
 * @param definition : Feature definition map containing:
 *                     - ownerPart {Query}           : The part body to query.
 *                     - highlightResults {boolean}  : Whether to show debug highlighting.
 */
function testConnectorsOfPart(context is Context, id is Id, definition is map)
{
    if (isQueryEmpty(context, definition.ownerPart))
    {
        reportFeatureInfo(context, id,
            "No part selected. Select a solid, surface, wire, or composite body to " ~
            "see which mate connectors are owned by it.");
        return;
    }

    const ownedConnectorsQuery   = qMateConnectorsOfParts(definition.ownerPart);
    const ownedConnectorEntities = evaluateQuery(context, ownedConnectorsQuery);
    const ownedConnectorCount    = size(ownedConnectorEntities);

    if (ownedConnectorCount == 0)
    {
        reportFeatureInfo(context, id,
            "qMateConnectorsOfParts returned 0 connectors for the selected part. " ~
            "If you expect connectors here, confirm they were created with this part " ~
            "explicitly set as the owner body (requireOwnerPart must be true and the " ~
            "correct part must have been selected in the mate connector feature).");
    }
    else
    {
        const connectorWord = (ownedConnectorCount == 1) ? "connector" : "connectors";
        reportFeatureInfo(context, id,
            "qMateConnectorsOfParts found " ~ ownedConnectorCount ~ " owned " ~
            connectorWord ~ " for the selected part.");
    }

    if (definition.highlightResults && ownedConnectorCount > 0)
    {
        debug(context, ownedConnectorsQuery, DebugColor.BLUE);
    }
}

