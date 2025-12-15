FeatureScript 2837;
// Simplified query feature to find geometry hidden with sheet metal annotations
// Uses qAttributeQuery with SMObjectType.WALL (verified working method)

export import(path : "onshape/std/query.fs", version : "2837.0");

import(path : "onshape/std/attributes.fs", version : "2837.0");
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2837.0");
import(path : "onshape/std/smobjecttype.gen.fs", version : "2837.0");
import(path : "onshape/std/string.fs", version : "2837.0");

/**
 * Simplified query feature to find hidden geometry with sheet metal annotations.
 * Uses the verified working method: qAttributeQuery with SMObjectType.WALL
 */
annotation { "Feature Type Name" : "Query Hidden Geometry (Experiment)" }
export const queryHiddenSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // No parameters needed - just query for all hidden SM geometry
    }
    {
        println("=== Querying for Hidden Sheet Metal Annotated Geometry ===");

        // VERIFIED WORKING METHOD: Query using qAttributeQuery with WALL attributes
        const entitiesWithWallAttr = qAttributeQuery(asSMAttribute({ "objectType" : SMObjectType.WALL }));
        const foundEntities = evaluateQuery(context, entitiesWithWallAttr);
        
        // Debug visualization to show found entities (red = hidden SM geometry)
        debug(context, entitiesWithWallAttr, DebugColor.RED);
        println("\nFound " ~ size(foundEntities) ~ " entities with SM WALL attributes");
        
        // Print details about each found entity
        for (var entity in foundEntities)
        {
            println("  - " ~ toString(entity));
        }
        
        println("\n=== Query Complete ===");
    });
