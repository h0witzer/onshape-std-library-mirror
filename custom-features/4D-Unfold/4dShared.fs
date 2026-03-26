FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");

export const UNFOLD_ATTRIBUTE = "unfoldData";
export const TAG_FACES_ATTRIBUTE = "edgeFaces";

/**
 * Set known attribute to face to make it easier to find later.
 */
export function tagFaceForTruncation(context is Context, face is Query)
{
    setAttribute(context, {
                "entities" : face,
                "name" : TAG_FACES_ATTRIBUTE,
                "attribute" : true
            });
}

export enum PartType
{
    HINGE,
    MAIN
}

export function tagPartForUnfold(context is Context, part is Query, spec is map)
{
    if (!isQueryEmpty(context, part))
    {
        const existingAttribute = getAttribute(context, {
                "entity" : part,
                "name" : UNFOLD_ATTRIBUTE
        });
        
        const newMap = isUndefinedOrEmptyString(existingAttribute) ? spec : mergeMaps(existingAttribute, spec);
        setAttribute(context, {
                    "entities" : part,
                    "name" : UNFOLD_ATTRIBUTE,
                    "attribute" : newMap
                });
    }
}
