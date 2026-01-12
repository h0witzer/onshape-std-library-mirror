FeatureScript 2837;
import(path : "onshape/std/feature.fs", version : "2837.0");

/**
 * Example Feature Using Step-Through Diagnostics
 * 
 * This example demonstrates how to use the stepThrough utility
 * to debug a simple pattern operation.
 * 
 * NOTE: To use this, you must first import stepThrough.fs from the
 * custom-features directory. Uncomment the import line below and
 * adjust the path to match your document structure.
 */

// Uncomment this line and adjust the path to import stepThrough:
// import(path : "path/to/stepThrough.fs", version : "2837.0");

annotation { 
    "Feature Type Name" : "Step-Through Example",
    "Feature Name Template" : "Debug Example"
}
export const stepThroughExample = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Select entities to pattern", "Filter" : EntityType.BODY }
        definition.entities is Query;

        annotation { "Name" : "Pattern count" }
        isInteger(definition.count, POSITIVE_COUNT_BOUNDS);

        annotation { "Name" : "Pattern distance" }
        isLength(definition.distance, NONNEGATIVE_LENGTH_BOUNDS);

        annotation { "Name" : "Enable debug checkpoints", "Default" : false }
        definition.enableDebug is boolean;
    }
    {
        // Checkpoint 1: Show initial state before the loop
        if (definition.enableDebug)
        {
            // Uncomment to use stepThrough:
            // stepThrough(context, id + "initialState", {
            //     "phase" : "Before pattern loop",
            //     "selectedEntities" : size(evaluateQuery(context, definition.entities)),
            //     "patternCount" : definition.count,
            //     "patternDistance" : definition.distance
            // });
            
            println("DEBUG: Initial state");
            println("  Selected entities: " ~ size(evaluateQuery(context, definition.entities)));
            println("  Pattern count: " ~ definition.count);
            println("  Pattern distance: " ~ definition.distance);
        }

        // Perform the pattern operation with debugging in the loop
        for (var index = 0; index < definition.count; index += 1)
        {
            const offset = definition.distance * index;
            
            // Checkpoint 2: Show state at each iteration
            if (definition.enableDebug)
            {
                // Uncomment to use stepThrough:
                // stepThrough(context, id + "iteration" ~ index, {
                //     "phase" : "Pattern iteration",
                //     "currentIndex" : index,
                //     "totalIterations" : definition.count,
                //     "currentOffset" : offset,
                //     "entitiesProcessed" : index
                // });
                
                println("DEBUG: Iteration " ~ index ~ " of " ~ definition.count);
                println("  Current offset: " ~ offset);
            }

            // Create copy at offset
            opTransform(context, id + ("transform" ~ index), {
                "bodies" : definition.entities,
                "transform" : transform(vector(offset, 0 * meter, 0 * meter))
            });
        }

        // Checkpoint 3: Show final state after the loop
        if (definition.enableDebug)
        {
            const finalQuery = qCreatedBy(id);
            
            // Uncomment to use stepThrough with query inspection:
            // stepThroughWithQuery(context, id + "finalState", {
            //     "phase" : "After pattern complete",
            //     "totalCreated" : size(evaluateQuery(context, finalQuery)),
            //     "iterations" : definition.count
            // }, finalQuery);
            
            println("DEBUG: Final state");
            println("  Total entities created: " ~ size(evaluateQuery(context, qCreatedBy(id))));
        }
    });

