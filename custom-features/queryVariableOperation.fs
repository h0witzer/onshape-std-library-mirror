FeatureScript 2815;
/** Query Variable Operation - Sister feature to Query Variable Plus for operations that modify geometry
 *
 * This feature creates query variables from operations that may modify model geometry while generating
 * query results. Unlike Query Variable Plus (pure queries), these operations perform geometry modifications
 * as a side effect but return useful query results that can be stored as variables.
 *
 * Current operation types:
 * - Shadow Visibility: Uses opSplitBySelfShadow to analyze visible/invisible faces from a view direction
 *
 * Future operations can be added (e.g., split by isocline, etc.)
 */

import(path : "onshape/std/common.fs", version : "2815.0");
import(path : "onshape/std/feature.fs", version : "2815.0");
import(path : "onshape/std/evaluate.fs", version : "2815.0");
import(path : "onshape/std/geomOperations.fs", version : "2815.0");
import(path : "onshape/std/query.fs", version : "2815.0");
import(path : "onshape/std/variable.fs", version : "2815.0");

/**
 * Defines the types of operations that can be performed.
 */
export enum OperationType
{
    annotation { "Name" : "Shadow visibility" }
    SHADOW_VISIBILITY
}

/**
 * Defines whether to return visible or invisible faces from shadow visibility analysis.
 */
export enum ShadowVisibilityType
{
    annotation { "Name" : "Visible faces" }
    VISIBLE,
    annotation { "Name" : "Invisible faces" }
    INVISIBLE
}

/**
 * Query Variable Operation feature creates query variables from operations that may modify geometry.
 * This is a sister feature to Query Variable Plus, specifically for operations that perform geometry
 * modifications while generating useful query results.
 *
 * @param id : @autocomplete `id + "operationQuery1"`
 * @param definition {{
 *      @field name {string} : The name of the query variable to create.
 *      @field description {string} : Description of the variable (optional).
 *      @field operationType {OperationType} : The type of operation to perform.
 *      
 *      For SHADOW_VISIBILITY:
 *      @field bodies {Query} : The bodies to analyze for shadow visibility.
 *      @field viewDirection {Query} : The direction from which to evaluate visibility.
 *              Direction is automatically inverted for intuitive UX (selecting a face looks at it head-on).
 *      @field oppositeDirection {boolean} : Whether to flip the view direction (applied after automatic inversion).
 *      @field visibilityType {ShadowVisibilityType} : Whether to return visible or invisible faces.
 * }}
 */
annotation { "Feature Type Name" : "Query variable operation", "Feature Name Template" : "###name", "UIHint" : UIHint.NO_PREVIEW_PROVIDED,
        "Tooltip Template" : "###name #description" }
export const queryVariableOperation = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Name", "UIHint" : [UIHint.UNCONFIGURABLE, UIHint.QUERY_VARIABLE_NAME], "MaxLength" : 10000 }
        definition.name is string;

        annotation { "Name" : "Description", "MaxLength" : 256, "Default" : "" }
        definition.description is string;

        annotation { "Name" : "Operation type" }
        definition.operationType is OperationType;

        if (definition.operationType == OperationType.SHADOW_VISIBILITY)
        {
            annotation { "Name" : "Bodies", "Filter" : EntityType.BODY }
            definition.bodies is Query;

            annotation { "Name" : "View direction", "Filter" : EntityType.EDGE || QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
            definition.viewDirection is Query;

            annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION, "Default" : false }
            definition.oppositeDirection is boolean;

            annotation { "Name" : "Visibility type", "Default" : ShadowVisibilityType.VISIBLE }
            definition.visibilityType is ShadowVisibilityType;
        }
    }
    {
        if (length(definition.name) == 0)
        {
            throw regenError(ErrorStringEnum.QUERY_VARIABLE_EMPTY_NAME);
        }
        checkQueryVariableName(context, definition.name);

        var query;
        if (definition.operationType == OperationType.SHADOW_VISIBILITY)
        {
            query = performShadowVisibility(context, id, definition);
        }
        else
        {
            throw regenError(ErrorStringEnum.INVALID_INPUT, ["operationType"]);
        }

        setQueryVariable(context, definition.name, definition.description, query);
        setHighlightedEntities(context, { "entities" : query });
    });

/**
 * Performs shadow visibility analysis operation.
 * @param context {Context} : The execution context.
 * @param id {Id} : The feature ID to use for the shadow operation.
 * @param definition {map} : Parameters for shadow visibility.
 * @returns {Query} : Query containing the visible or invisible faces.
 */
function performShadowVisibility(context is Context, id is Id, definition is map) returns Query
{
    const bodies = definition.bodies as Query;
    const viewDirectionQuery = definition.viewDirection as Query;
    const visibilityType = definition.visibilityType as ShadowVisibilityType;
    const oppositeDirection = definition.oppositeDirection == true;

    if (isQueryEmpty(context, bodies))
    {
        throw regenError(ErrorStringEnum.INVALID_INPUT, ["bodies"]);
    }

    var viewDirection = evaluateDirectionReference(context, viewDirectionQuery, "viewDirection");
    
    // Invert the direction by default for intuitive UX - when selecting a face, 
    // users typically want to look at that face head-on from the viewport
    viewDirection = -viewDirection;
    
    // Apply the opposite direction toggle if selected
    if (oppositeDirection)
    {
        viewDirection = -viewDirection;
    }

    // Perform the shadow split operation
    const shadowId = id + "shadowSplit";
    const shadowResult = opSplitBySelfShadow(context, shadowId, {
                "bodies" : bodies,
                "viewDirection" : viewDirection
            });

    // Return the appropriate faces based on visibility type
    if (visibilityType == ShadowVisibilityType.VISIBLE)
    {
        return qUnion(shadowResult.visibleFaces);
    }
    
    return qUnion(shadowResult.invisibleFaces);
}

/**
 * Helper function to evaluate a direction reference from a query.
 * @param context {Context} : The execution context.
 * @param directionQuery {Query} : Query for the entity that defines the direction.
 * @param parameterName {string} : Name of the parameter for error reporting.
 * @returns {Vector} : The evaluated direction vector.
 */
function evaluateDirectionReference(context is Context, directionQuery is Query, parameterName is string) returns Vector
{
    const directionEntities = evaluateQuery(context, directionQuery);
    if (size(directionEntities) != 1)
    {
        throw regenError(ErrorStringEnum.INVALID_INPUT, [parameterName]);
    }

    try
    {
        return extractDirection(context, directionQuery);
    }
    catch
    {
        throw regenError(ErrorStringEnum.INVALID_INPUT, [parameterName]);
    }
}
