FeatureScript 638;
import(path : "onshape/std/geometry.fs", version : "638.0");

export enum ExtendTypeEnum
{
    annotation { "Name" : "Extend Full Surface by Distance" }
    EXTEND_BY_DISTANCE_FULL,
    annotation { "Name" : "Extend Edges by Distance" }
    EXTEND_BY_DISTANCE_EDGES
    
    // ,annotation { "Name" : "Extend Edges to Surface" }
    // EXTEND_UP_TO_SURFACE
}


annotation { "Feature Type Name" : "Extend Surface" }
export const extendSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Extend Type" }
        definition.extendType is ExtendTypeEnum;

        if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_FULL)
        {
            annotation { "Name" : "Surface to Extend", "Filter" : (EntityType.BODY && BodyType.SHEET), "MaxNumberOfPicks" : 1 }
            definition.surfaceToExtend is Query;
        }

        if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_EDGES)
        {
            annotation { "Name" : "Edges to Extend", "Filter" : EntityType.EDGE}
            definition.extendEdges is Query;
        }
        
        // if (definition.extendType == ExtendTypeEnum.EXTEND_UP_TO_SURFACE)
        // {
        //     annotation { "Name" : "Edges to Extend", "Filter" : EntityType.EDGE }
        //     definition.extendToSurfaceEdges is Query;
        //     annotation { "Name" : "Limit Surface", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        //     definition.limitSurface is Query;
            
        // }
        
        if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_FULL || definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_EDGES)
        {
             annotation { "Name" : "Extend Distance" }
        isLength(definition.extendDistance, LENGTH_BOUNDS);
        }

    }
    {
        var isBody = evaluateQuery(context, qEntityFilter(definition.surfaceToExtend, EntityType.BODY));
        var extendDefinition = {};

        // if (isBody == [])
        // {
        //     opExtractSurface(context, id + "extractSurface", { "faces" : definition.surfaceToExtend });
        //     // debug(context, );
        //     extendDefinition = { "extendMethod" : "EXTEND_BY_DISTANCE", "entities" : qCreatedBy(id + "extractSurface", EntityType.BODY), "distance" : definition.extendDistance };
        // }
        // else
        // {

            if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_FULL)
            {
                extendDefinition = { "extendMethod" : "EXTEND_BY_DISTANCE", "entities" : definition.surfaceToExtend, "distance" : definition.extendDistance };
            }
            else if (definition.extendType == ExtendTypeEnum.EXTEND_BY_DISTANCE_EDGES)
            {
                extendDefinition = { "extendMethod" : "EXTEND_BY_DISTANCE", "entities" : definition.extendEdges, "distance" : definition.extendDistance };
            }
            // else if (definition.extendType == ExtendTypeEnum.EXTEND_UP_TO_SURFACE)
            // {
            //     var edges = evaluateQuery(context, definition.extendToSurfaceEdges);
            //     var edgeLimitOptions = [];
            //     for (var i = 0; i < size(edges); i += 1)
            //     {
            //         edgeLimitOptions = append(edgeLimitOptions, { "edge" : edges[i], "limitEntity" : definition.limitSurface, "faceToExtend" : qEntityFilter(qOwnerBody(edges[i]), EntityType.FACE) });
            //     }
            //     extendDefinition = { "extendMethod" : "EXTEND_TO_SURFACE", "entities" : definition.extendToSurfaceEdges, "limitEntity" : definition.limitSurface, "oppositeDirection" : true};
            //     debug(context, definition.extendToSurfaceEdges);
            //     debug(context, definition.limitSurface);
            // }
        // }

        opExtendSheetBody(context, id, extendDefinition);
    });
