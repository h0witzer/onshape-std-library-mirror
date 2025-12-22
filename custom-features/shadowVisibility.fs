FeatureScript 2815;
/** Custom feature for analyzing shadow visibility of faces from a given view direction.
 *
 * This feature uses opSplitBySelfShadow to split faces into visible and invisible regions,
 * allowing you to select faces that are visible or invisible from a specified viewing direction.
 *
 * **Note**: This operation modifies the model geometry by adding shadow curve edges to the bodies.
 * The shadow curves represent transitions between visible and invisible regions on the faces.
 */

import(path : "onshape/std/common.fs", version : "2815.0");
import(path : "onshape/std/feature.fs", version : "2815.0");
import(path : "onshape/std/evaluate.fs", version : "2815.0");
import(path : "onshape/std/geomOperations.fs", version : "2815.0");
import(path : "onshape/std/query.fs", version : "2815.0");

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
 * Shadow visibility feature analyzes faces from a viewing direction and returns either
 * visible or invisible faces. The operation splits bodies by shadow curves, creating edges
 * at the transition between visible and invisible regions.
 *
 * @param id : @autocomplete `id + "shadowVisibility1"`
 * @param definition {{
 *      @field bodies {Query} : The bodies to analyze for shadow visibility.
 *      @field viewDirection {Query} : The direction from which to evaluate visibility.
 *              Direction is automatically inverted for intuitive UX (selecting a face looks at it head-on).
 *      @field oppositeDirection {boolean} : Whether to flip the view direction (applied after automatic inversion).
 *      @field visibilityType {ShadowVisibilityType} : Whether to return visible or invisible faces.
 * }}
 */
annotation { "Feature Type Name" : "Shadow visibility", "UIHint" : UIHint.NO_PREVIEW_PROVIDED }
export const shadowVisibility = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
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
        const shadowId = id + "split";
        const shadowResult = opSplitBySelfShadow(context, shadowId, {
                    "bodies" : bodies,
                    "viewDirection" : viewDirection
                });

        // Return the appropriate faces based on visibility type
        var resultFaces;
        if (visibilityType == ShadowVisibilityType.VISIBLE)
        {
            resultFaces = qUnion(shadowResult.visibleFaces);
        }
        else
        {
            resultFaces = qUnion(shadowResult.invisibleFaces);
        }

        // Highlight the resulting faces
        setHighlightedEntities(context, { "entities" : resultFaces });
    });

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
