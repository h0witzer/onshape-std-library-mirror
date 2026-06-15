FeatureScript 2815;
/** Query Variable Analysis - Shadow and Isocline analysis operations
 *
 * This feature creates query variables from shadow visibility and isocline (draft angle) analysis operations.
 * These operations modify model geometry by splitting faces while generating useful query results.
 *
 * Operation types:
 * - Shadow Visibility: Analyzes visible/invisible faces from a view direction
 * - Isocline (Draft Analysis): Analyzes steep/non-steep faces at a specified draft angle
 */

import(path : "onshape/std/common.fs", version : "2815.0");
import(path : "onshape/std/feature.fs", version : "2815.0");
import(path : "onshape/std/evaluate.fs", version : "2815.0");
import(path : "onshape/std/geomOperations.fs", version : "2815.0");
import(path : "onshape/std/query.fs", version : "2815.0");
import(path : "onshape/std/variable.fs", version : "2815.0");
import(path : "onshape/std/valueBounds.fs", version : "2815.0");

/**
 * Defines the types of operations that can be performed.
 */
export enum OperationType
{
    annotation { "Name" : "Shadow visibility" }
    SHADOW_VISIBILITY,
    annotation { "Name" : "Isocline (draft analysis)" }
    ISOCLINE
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
 * Defines which faces to return from isocline analysis.
 */
export enum IsoclineResultType
{
    annotation { "Name" : "Steep faces" }
    STEEP,
    annotation { "Name" : "Non-steep faces" }
    NON_STEEP,
    annotation { "Name" : "Boundary edges" }
    BOUNDARY_EDGES
}

/**
 * Query Variable Operation feature creates query variables from shadow and isocline analysis operations.
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
 *      
 *      For ISOCLINE:
 *      @field faces {Query} : The faces on which to imprint isoclines.
 *      @field direction {Query} : The reference direction for draft angle analysis.
 *      @field oppositeDirection {boolean} : Whether to flip the direction.
 *      @field angle {ValueWithUnits} : The isocline angle with respect to the direction in the (-90, 90) degree range.
 *      @field resultType {IsoclineResultType} : Which result to return (steep faces, non-steep faces, or boundary edges).
 * }}
 */
annotation { "Feature Type Name" : "Query variable analysis", "Feature Name Template" : "###name", "UIHint" : UIHint.NO_PREVIEW_PROVIDED,
        "Tooltip Template" : "###name #description" }
export const queryVariableAnalysis = defineFeature(function(context is Context, id is Id, definition is map)
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
        else if (definition.operationType == OperationType.ISOCLINE)
        {
            annotation { "Name" : "Faces", "Filter" : EntityType.FACE && ConstructionObject.NO && SketchObject.NO }
            definition.faces is Query;

            annotation { "Name" : "Direction", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
            definition.direction is Query;

            annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION, "Default" : false }
            definition.oppositeDirection is boolean;

            annotation { "Name" : "Angle" }
            isAngle(definition.angle, ANGLE_STRICT_90_BOUNDS);

            annotation { "Name" : "Result type", "Default" : IsoclineResultType.STEEP }
            definition.resultType is IsoclineResultType;
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
        else if (definition.operationType == OperationType.ISOCLINE)
        {
            query = performIsocline(context, id, definition);
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
 * Performs isocline (draft analysis) operation.
 * @param context {Context} : The execution context.
 * @param id {Id} : The feature ID to use for the isocline operation.
 * @param definition {map} : Parameters for isocline analysis.
 * @returns {Query} : Query containing the steep faces, non-steep faces, or boundary edges.
 */
function performIsocline(context is Context, id is Id, definition is map) returns Query
{
    const faces = definition.faces as Query;
    const directionQuery = definition.direction as Query;
    const oppositeDirection = definition.oppositeDirection == true;
    const angle = definition.angle;
    const resultType = definition.resultType as IsoclineResultType;

    if (isQueryEmpty(context, faces))
    {
        throw regenError(ErrorStringEnum.INVALID_INPUT, ["faces"]);
    }

    var direction = evaluateDirectionReference(context, directionQuery, "direction");
    
    // Apply the opposite direction toggle if selected
    if (oppositeDirection)
    {
        direction = -direction;
    }

    // Perform the isocline split operation
    const isoclineId = id + "isoclineSplit";
    const isoclineResult = opSplitByIsocline(context, isoclineId, {
                "faces" : faces,
                "direction" : direction,
                "angle" : angle
            });

    // Return the appropriate result based on result type
    if (resultType == IsoclineResultType.STEEP)
    {
        return qUnion(isoclineResult.steepFaces);
    }
    else if (resultType == IsoclineResultType.NON_STEEP)
    {
        return qUnion(isoclineResult.nonSteepFaces);
    }
    else // BOUNDARY_EDGES
    {
        return qUnion(isoclineResult.boundaryEdges);
    }
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
