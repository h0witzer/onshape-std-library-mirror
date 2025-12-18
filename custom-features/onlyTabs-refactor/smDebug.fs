FeatureScript 2780;
import(path : "onshape/std/common.fs", version : "2780.0");
import(path : "onshape/std/query.fs", version : "2780.0");
import(path : "onshape/std/debug.fs", version : "2780.0");
import(path : "onshape/std/debugcolor.gen.fs", version : "2780.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2780.0");
import(path : "onshape/std/string.fs", version : "2780.0");

/**
 * Determines which sheet metal attribute types should be visualized.
 */
export enum SheetMetalAttributeFocus
{
    annotation { "Name" : "All sheet metal attributes" }
    ALL,
    annotation { "Name" : "Model attributes only" }
    MODEL_ONLY,
    annotation { "Name" : "Wall attributes only" }
    WALL_ONLY,
    annotation { "Name" : "Joint attributes only" }
    JOINT_ONLY,
    annotation { "Name" : "Corner attributes only" }
    CORNER_ONLY,
    annotation { "Name" : "Unclassified sheet metal attributes" }
    UNCLASSIFIED_ONLY
}

annotation { "Feature Type Name" : "Sheet metal attribute inspector" }
export const sheetMetalAttributeInspector = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Entities to inspect", "Filter" : (EntityType.BODY || EntityType.FACE || EntityType.EDGE || EntityType.VERTEX), "MaxNumberOfPicks" : 1 }
        definition.selection is Query;

        annotation { "Name" : "Attribute focus", "Default" : SheetMetalAttributeFocus.ALL }
        definition.attributeFocus is SheetMetalAttributeFocus;

        annotation { "Name" : "Show master associations", "Default" : false }
        definition.showMasterAssociations is boolean;

        annotation { "Name" : "Colorize entire sheet metal context", "Default" : false }
        definition.colorizeSheetMetalContext is boolean;
    }
    {
        const evaluatedSelection = evaluateQuery(context, definition.selection);
        if (size(evaluatedSelection) == 0)
        {
            throw regenError("Select at least one entity", ["selection"]);
        }

        const attributeGroups = collectSheetMetalAttributeGroups(context, definition.selection);
        var displayedGroup is boolean = false;

        println("Sheet metal attribute inspector results:");
        for (var group in attributeGroups)
        {
            if (!groupMatchesFocus(definition.attributeFocus, group.objectType))
            {
                continue;
            }

            const debugColor = getDebugColorForObjectType(group.objectType);
            debug(context, group.selectionPortion, debugColor);
            displayedGroup = true;

            const label = getSheetMetalAttributeLabel(group.objectType);
            const countString = group.entityCount == 1 ? "1 entity" : group.entityCount ~ " entities";
            println(" • " ~ label ~ ": " ~ countString);
            const sourceSummary = describeGroupSources(group.hasDirect, group.hasMaster);
            if (sourceSummary != "")
            {
                println("   Source: " ~ sourceSummary);
            }
            if (group.hasDirect)
            {
                if (size(group.directAttributeIds) > 0)
                {
                    println("   Direct attribute IDs: " ~ joinWithCommas(group.directAttributeIds));
                }
                else
                {
                    println("   Direct attribute IDs: none available");
                }
            }
            if (group.hasMaster)
            {
                if (size(group.masterAttributeIds) > 0)
                {
                    println("   Master attribute IDs: " ~ joinWithCommas(group.masterAttributeIds));
                }
                else
                {
                    println("   Master attribute IDs: none available");
                }
            }
        }

        if (!displayedGroup)
        {
            debug(context, definition.selection, DebugColor.RED);
            println("No sheet metal attributes matched the requested focus.");
        }

        if (definition.showMasterAssociations)
        {
            displayMasterAssociationInformation(context, definition.selection);
        }

        if (definition.colorizeSheetMetalContext)
        {
            colorizeSheetMetalContexts(context, definition.selection, definition.attributeFocus);
        }
    });

/**
 * Build summary information for each sheet metal attribute type found on the selection.
 * Searches both direct sheet metal attributes on the picked entities and, if necessary,
 * master sheet metal definition entities referenced by association attributes.
 * @param selection {Query} : Entities chosen by the user for inspection.
 * @return {array} : A collection of maps describing the attribute type, highlighted query, ids, and entity count.
 */
function collectSheetMetalAttributeGroups(context is Context, selection is Query) returns array
{
    const evaluatedSelection = evaluateQuery(context, selection);
    var groupsByType = {};

    for (var entity in evaluatedSelection)
    {
        const entityQuery = qUnion([entity]);
        const attributeDetails = gatherSheetMetalAttributeDetails(context, entityQuery);

        for (var detail in attributeDetails)
        {
            const key = objectTypeKey(detail.objectType);
            if (groupsByType[key] == undefined)
            {
                groupsByType[key] = {
                        "objectType" : detail.objectType,
                        "queries" : [entityQuery],
                        "directAttributeIds" : detail.directIds,
                        "masterAttributeIds" : detail.masterIds,
                        "hasDirect" : detail.hasDirect,
                        "hasMaster" : detail.hasMaster
                    };
            }
            else
            {
                groupsByType[key].queries = append(groupsByType[key].queries, entityQuery);
                for (var directId in detail.directIds)
                {
                    groupsByType[key].directAttributeIds = append(groupsByType[key].directAttributeIds, directId);
                }
                for (var masterId in detail.masterIds)
                {
                    groupsByType[key].masterAttributeIds = append(groupsByType[key].masterAttributeIds, masterId);
                }
                groupsByType[key].hasDirect = groupsByType[key].hasDirect || detail.hasDirect;
                groupsByType[key].hasMaster = groupsByType[key].hasMaster || detail.hasMaster;
            }
        }
    }

    var groups = [];
    for (var entry in groupsByType)
    {
        const queryUnion = qUnion(entry.value.queries);
        const entityCount = size(evaluateQuery(context, queryUnion));
        const directIds = deduplicateStrings(entry.value.directAttributeIds);
        const masterIds = deduplicateStrings(entry.value.masterAttributeIds);
        groups = append(groups, {
                    "objectType" : entry.value.objectType,
                    "selectionPortion" : queryUnion,
                    "directAttributeIds" : directIds,
                    "masterAttributeIds" : masterIds,
                    "hasDirect" : entry.value.hasDirect,
                    "hasMaster" : entry.value.hasMaster,
                    "entityCount" : entityCount
                });
    }

    return groups;
}

/**
 * Highlight entire sheet metal bodies related to the selection, coloring all faces by
 * their sheet metal attribute classification and reporting counts per category.
 * Faces without any sheet metal attributes are highlighted in red when all types are requested.
 */
function colorizeSheetMetalContexts(context is Context, selection is Query, focus is SheetMetalAttributeFocus)
{
    const sheetMetalBodies = collectSheetMetalBodies(context, selection);
    if (size(sheetMetalBodies) == 0)
    {
        println("No sheet metal bodies were found to colorize in the current selection.");
        return;
    }

    println("Colorizing sheet metal contexts with debug colors:");
    var bodyIndex = 1;
    for (var bodyQuery in sheetMetalBodies)
    {
        const colorizationResult = highlightSheetMetalBody(context, bodyQuery, focus);
        const summaryPrefix = " Sheet metal body " ~ toString(bodyIndex) ~ ":";
        if (colorizationResult.highlighted)
        {
            println(summaryPrefix);
            for (var entry in colorizationResult.summaries)
            {
                const countText = entry.count == 1 ? "1 face" : toString(entry.count) ~ " faces";
                println("   " ~ entry.label ~ ": " ~ countText);
            }
        }
        else
        {
            const faceWord = colorizationResult.totalFaceCount == 1 ? "face" : "faces";
            println(summaryPrefix ~ " no faces matched the requested focus (" ~ toString(colorizationResult.totalFaceCount) ~ " " ~ faceWord ~ " evaluated).");
        }
        bodyIndex += 1;
    }
}

/**
 * Gather sheet metal bodies associated with the selection that contain definition data.
 */
function collectSheetMetalBodies(context is Context, selection is Query) returns array
{
    const evaluatedSelection = evaluateQuery(context, selection);
    if (size(evaluatedSelection) == 0)
    {
        return [];
    }

    var potentialBodies = [];
    for (var entity in evaluatedSelection)
    {
        const entityQuery = qUnion([entity]);
        potentialBodies = append(potentialBodies, qEntityFilter(entityQuery, EntityType.BODY));
        potentialBodies = append(potentialBodies, qOwnerBody(entityQuery));
    }

    if (size(potentialBodies) == 0)
    {
        return [];
    }

    const combinedBodies = qEntityFilter(qUnion(potentialBodies), EntityType.BODY);
    const evaluatedBodies = evaluateQuery(context, combinedBodies);

    var sheetMetalBodies = [];
    for (var body in evaluatedBodies)
    {
        const bodyQuery = qUnion([body]);
        const definitionEntities = getSMDefinitionEntities(context, bodyQuery);
        if (size(definitionEntities) > 0)
        {
            sheetMetalBodies = append(sheetMetalBodies, bodyQuery);
        }
    }

    return sheetMetalBodies;
}

/**
 * Highlight a single sheet metal body and report per-category summaries for its faces.
 */
function highlightSheetMetalBody(context is Context, bodyQuery is Query, focus is SheetMetalAttributeFocus) returns map
{
    const classification = classifySheetMetalBodyFaces(context, bodyQuery);

    var highlighted = false;
    var summaries = [];

    for (var group in classification.typedGroups)
    {
        if (shouldHighlightObjectType(focus, group.objectType))
        {
            debug(context, group.query, getDebugColorForObjectType(group.objectType));
            summaries = append(summaries, {
                        "label" : getSheetMetalAttributeLabel(group.objectType),
                        "count" : group.count
                    });
            highlighted = true;
        }
    }

    if ((focus == SheetMetalAttributeFocus.ALL || focus == SheetMetalAttributeFocus.UNCLASSIFIED_ONLY) && classification.unclassifiedCount > 0)
    {
        debug(context, classification.unclassifiedQuery, getDebugColorForObjectType(undefined));
        summaries = append(summaries, {
                    "label" : getSheetMetalAttributeLabel(undefined),
                    "count" : classification.unclassifiedCount
                });
        highlighted = true;
    }

    if (focus == SheetMetalAttributeFocus.ALL && classification.unattributedCount > 0)
    {
        debug(context, classification.unattributedQuery, DebugColor.RED);
        summaries = append(summaries, {
                    "label" : "Faces without sheet metal attributes",
                    "count" : classification.unattributedCount
                });
        highlighted = true;
    }

    return {
            "highlighted" : highlighted,
            "summaries" : summaries,
            "totalFaceCount" : classification.totalFaceCount
        };
}

/**
 * Determine if an object type should be highlighted for the requested focus.
 */
function shouldHighlightObjectType(focus is SheetMetalAttributeFocus, objectType) returns boolean
{
    if (focus == SheetMetalAttributeFocus.ALL)
    {
        return true;
    }
    if (focus == SheetMetalAttributeFocus.UNCLASSIFIED_ONLY)
    {
        return false;
    }

    const focusType = focusToObjectType(focus);
    if (focusType == undefined)
    {
        return false;
    }
    return objectType == focusType;
}

/**
 * Convert accumulated face queries for a sheet metal object type into a summary group.
 */
function createSheetMetalFaceGroup(context is Context, groupData is map) returns map
{
    const combinedQuery = qUnion(groupData.queries);
    const count = size(evaluateQuery(context, combinedQuery));
    return {
            "objectType" : groupData.objectType,
            "query" : combinedQuery,
            "count" : count
        };
}

/**
 * Classify all faces of a sheet metal body by their sheet metal attribute assignments.
 */
function classifySheetMetalBodyFaces(context is Context, bodyQuery is Query) returns map
{
    const facesQuery = qEntityFilter(qOwnedByBody(bodyQuery, EntityType.FACE), EntityType.FACE);
    const faces = evaluateQuery(context, facesQuery);

    var typedGroupsByType = {};
    var unclassifiedFaces = [];
    var facesWithoutAttributes = [];

    for (var face in faces)
    {
        const faceQuery = qUnion([face]);
        const details = gatherSheetMetalAttributeDetails(context, faceQuery);
        if (size(details) == 0)
        {
            facesWithoutAttributes = append(facesWithoutAttributes, faceQuery);
            continue;
        }

        var hasRecognizedType = false;
        var hasUnclassifiedType = false;

        for (var detail in details)
        {
            const objectType = detail.objectType;
            if (objectType == undefined)
            {
                hasUnclassifiedType = true;
                continue;
            }

            const key = objectTypeKey(objectType);
            if (typedGroupsByType[key] == undefined)
            {
                typedGroupsByType[key] = {
                        "objectType" : objectType,
                        "queries" : [faceQuery]
                    };
            }
            else
            {
                typedGroupsByType[key].queries = append(typedGroupsByType[key].queries, faceQuery);
            }
            hasRecognizedType = true;
        }

        if (hasUnclassifiedType && !hasRecognizedType)
        {
            unclassifiedFaces = append(unclassifiedFaces, faceQuery);
        }
    }

    var typedGroups = [];
    var processedKeys = {};
    const preferredOrder = [SMObjectType.MODEL, SMObjectType.WALL, SMObjectType.JOINT, SMObjectType.CORNER];
    for (var orderedType in preferredOrder)
    {
        const orderedKey = objectTypeKey(orderedType);
        if (typedGroupsByType[orderedKey] == undefined)
        {
            continue;
        }
        typedGroups = append(typedGroups, createSheetMetalFaceGroup(context, typedGroupsByType[orderedKey]));
        processedKeys[orderedKey] = true;
    }

    for (var entry in typedGroupsByType)
    {
        if (processedKeys[entry.key] == true)
        {
            continue;
        }
        typedGroups = append(typedGroups, createSheetMetalFaceGroup(context, entry.value));
    }

    var unclassifiedQuery = qNothing();
    var unclassifiedCount = 0;
    if (size(unclassifiedFaces) > 0)
    {
        unclassifiedQuery = qUnion(unclassifiedFaces);
        unclassifiedCount = size(evaluateQuery(context, unclassifiedQuery));
    }

    var unattributedQuery = qNothing();
    var unattributedCount = 0;
    if (size(facesWithoutAttributes) > 0)
    {
        unattributedQuery = qUnion(facesWithoutAttributes);
        unattributedCount = size(evaluateQuery(context, unattributedQuery));
    }

    return {
            "typedGroups" : typedGroups,
            "unclassifiedQuery" : unclassifiedQuery,
            "unclassifiedCount" : unclassifiedCount,
            "unattributedQuery" : unattributedQuery,
            "unattributedCount" : unattributedCount,
            "totalFaceCount" : size(faces)
        };
}

/**
 * Determine whether an attribute group should be displayed for the requested focus.
 */
function groupMatchesFocus(focus is SheetMetalAttributeFocus, objectType) returns boolean
{
    if (focus == SheetMetalAttributeFocus.ALL)
    {
        return true;
    }

    if (focus == SheetMetalAttributeFocus.UNCLASSIFIED_ONLY)
    {
        return objectType == undefined;
    }

    const focusType = focusToObjectType(focus);
    if (focusType == undefined)
    {
        return false;
    }

    return objectType == focusType;
}

/**
 * Map an attribute focus enum value to its corresponding SMObjectType.
 */
function focusToObjectType(focus is SheetMetalAttributeFocus)
{
    return {
                SheetMetalAttributeFocus.MODEL_ONLY : SMObjectType.MODEL,
                SheetMetalAttributeFocus.WALL_ONLY : SMObjectType.WALL,
                SheetMetalAttributeFocus.JOINT_ONLY : SMObjectType.JOINT,
                SheetMetalAttributeFocus.CORNER_ONLY : SMObjectType.CORNER
            }[focus];
}

/**
 * Choose a debug color for the provided sheet metal object type.
 */
function getDebugColorForObjectType(objectType) returns DebugColor
{
    if (objectType == SMObjectType.MODEL)
    {
        return DebugColor.YELLOW;
    }
    if (objectType == SMObjectType.WALL)
    {
        return DebugColor.BLUE;
    }
    if (objectType == SMObjectType.JOINT)
    {
        return DebugColor.MAGENTA;
    }
    if (objectType == SMObjectType.CORNER)
    {
        return DebugColor.CYAN;
    }
    return DebugColor.ORANGE;
}

/**
 * Provide a readable description for the sheet metal object type.
 */
function getSheetMetalAttributeLabel(objectType) returns string
{
    if (objectType == SMObjectType.MODEL)
    {
        return "Model attributes";
    }
    if (objectType == SMObjectType.WALL)
    {
        return "Wall attributes";
    }
    if (objectType == SMObjectType.JOINT)
    {
        return "Joint attributes";
    }
    if (objectType == SMObjectType.CORNER)
    {
        return "Corner attributes";
    }
    return "Unclassified sheet metal attributes";
}

/**
 * Build attribute information for a single selected entity by inspecting both direct attributes
 * and sheet metal master definition entities referenced through association attributes.
 */
function gatherSheetMetalAttributeDetails(context is Context, entityQuery is Query) returns array
{
    var detailsByKey = {};

    const directAttributes = getAttributes(context, {
                "entities" : entityQuery,
                "attributePattern" : asSMAttribute({})
            });
    for (var attribute in directAttributes)
    {
        const objectType = extractObjectType(attribute);
        const key = objectTypeKey(objectType);
        if (detailsByKey[key] == undefined)
        {
            detailsByKey[key] = initializeAttributeDetail(objectType);
        }
        const attributeId = extractAttributeId(attribute);
        if (attributeId != undefined)
        {
            detailsByKey[key].directIds = append(detailsByKey[key].directIds, attributeId);
        }
        detailsByKey[key].hasDirect = true;
    }

    const masterAttributes = collectMasterDefinitionAttributes(context, entityQuery);
    for (var attribute in masterAttributes)
    {
        const objectType = extractObjectType(attribute);
        const key = objectTypeKey(objectType);
        if (detailsByKey[key] == undefined)
        {
            detailsByKey[key] = initializeAttributeDetail(objectType);
        }
        const attributeId = extractAttributeId(attribute);
        if (attributeId != undefined)
        {
            detailsByKey[key].masterIds = append(detailsByKey[key].masterIds, attributeId);
        }
        detailsByKey[key].hasMaster = true;
    }

    var details = [];
    for (var entry in detailsByKey)
    {
        entry.value.directIds = deduplicateStrings(entry.value.directIds);
        entry.value.masterIds = deduplicateStrings(entry.value.masterIds);
        details = append(details, entry.value);
    }
    return details;
}

/**
 * Create a consistent structure for collecting attribute ids and source tracking per object type.
 */
function initializeAttributeDetail(objectType) returns map
{
    return {
            "objectType" : objectType,
            "directIds" : [],
            "masterIds" : [],
            "hasDirect" : false,
            "hasMaster" : false
        };
}

/**
 * Collect sheet metal attributes from master definition entities linked to the provided query.
 */
function collectMasterDefinitionAttributes(context is Context, entityQuery is Query) returns array
{
    const definitionEntities = getSMDefinitionEntities(context, entityQuery);
    if (size(definitionEntities) == 0)
    {
        return [];
    }

    var attributes = [];
    for (var definitionEntity in definitionEntities)
    {
        const definitionQuery = qUnion([definitionEntity]);
        const definitionAttributes = getAttributes(context, {
                    "entities" : definitionQuery,
                    "attributePattern" : asSMAttribute({})
                });
        for (var attribute in definitionAttributes)
        {
            attributes = append(attributes, attribute);
        }
    }
    return attributes;
}

/**
 * Translate a sheet metal object type to a lookup key for grouping.
 */
function objectTypeKey(objectType) returns string
{
    return objectType == undefined ? "UNCLASSIFIED" : toString(objectType);
}

/**
 * Generate readable text describing where attribute data was found for a group.
 */
function describeGroupSources(hasDirect is boolean, hasMaster is boolean) returns string
{
    if (hasDirect && hasMaster)
    {
        return "direct and master definition attributes";
    }
    if (hasDirect)
    {
        return "direct attributes";
    }
    if (hasMaster)
    {
        return "master definition attributes";
    }
    return "";
}

/**
 * Display association attributes for the selection, if any, using debug geometry.
 */
function displayMasterAssociationInformation(context is Context, selection is Query)
{
    const associations = getSMAssociationAttributes(context, selection);
    if (size(associations) == 0)
    {
        println("No sheet metal master associations were found for the selection.");
        return;
    }

    var associationQueries = [];
    var associationIds = [];
    for (var association in associations)
    {
        associationQueries = append(associationQueries, qAttributeQuery(association));
        const associationId = extractAttributeId(association);
        if (associationId != undefined)
        {
            associationIds = append(associationIds, associationId);
        }
    }

    const combinedAssociations = qUnion(associationQueries);
    debug(context, combinedAssociations, DebugColor.GREEN);

    if (size(associationIds) > 0)
    {
        println("Master association IDs: " ~ joinWithCommas(deduplicateStrings(associationIds)));
    }
    else
    {
        println("Master association IDs: none available");
    }
}

/**
 * Extract the object type from an SMAttribute map, accommodating legacy fields.
 */
function extractObjectType(attribute) returns SMObjectType
{
    if (attribute.objectType != undefined)
    {
        return attribute.objectType;
    }
    return attribute.object_type;
}

/**
 * Extract the attribute identifier from either an SMAttribute or an association attribute.
 */
function extractAttributeId(attribute) returns string
{
    if (attribute.attributeId != undefined)
    {
        return attribute.attributeId;
    }
    return attribute.attribute_id;
}

/**
 * Deduplicate an array of strings while preserving order.
 */
function deduplicateStrings(values is array) returns array
{
    var seen = {};
    var result = [];
    for (var value in values)
    {
        if (seen[value] == true)
        {
            continue;
        }
        seen[value] = true;
        result = append(result, value);
    }
    return result;
}

/**
 * Combine an array of strings into a comma separated list.
 */
function joinWithCommas(values is array) returns string
{
    if (size(values) == 0)
    {
        return "";
    }

    var assembled = "";
    for (var index = 0; index < size(values); index += 1)
    {
        if (index != 0)
        {
            assembled = assembled ~ ", ";
        }
        assembled = assembled ~ values[index];
    }
    return assembled;
}

