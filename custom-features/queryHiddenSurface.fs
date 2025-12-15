FeatureScript 2837;
// Experimental feature: Query for hidden surfaces created with sheet metal annotations
// This explores whether surfaces hidden using defineSheetMetalFeature can be queried and have their properties retrieved

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
 * Feature that queries for surfaces hidden with sheet metal annotations and retrieves their stored properties.
 * This demonstrates whether hidden surfaces maintain their attributes and can be found with broader queries.
 */
annotation { "Feature Type Name" : "Query Hidden Surface (Experiment)" }
export const queryHiddenSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Search scope",
                    "Description" : "Optional: limit search to specific bodies",
                    "Filter" : EntityType.BODY }
        definition.searchScope is Query;

        annotation { "Name" : "Experiment type filter",
                    "Description" : "Filter for specific experiment type" }
        definition.experimentTypeFilter is string;
    }
    {
        println("=== Querying for Hidden Surfaces ===");

        // Method 0: Query for named experiment attributes
        println("\n--- Method 0: Query by named attribute 'experimentData' ---");
        const experimentDataEntities = qHasAttribute("experimentData");
        const experimentDataArray = evaluateQuery(context, experimentDataEntities);
        
        println("Found " ~ size(experimentDataArray) ~ " entities with experimentData attribute");
        
        for (var entity in experimentDataArray)
        {
            const expData = getAttribute(context, {
                "entity" : entity,
                "name" : "experimentData"
            });
            if (expData != undefined && expData.experimentType == definition.experimentTypeFilter)
            {
                println("\nFound entity with experiment data:");
                println("  Entity: " ~ toString(entity));
                println("  Custom Property: " ~ expData.customProperty);
                println("  Experiment Type: " ~ expData.experimentType);
            }
        }

        // Method 1: Query for all surfaces with SMObjectType.WALL attributes
        println("\n--- Method 1: Query by SMObjectType.WALL ---");
        const wallAttributes = getAttributes(context, {
            "entities" : qEverything(),
            "attributePattern" : asSMAttribute({ "objectType" : SMObjectType.WALL })
        });
        
        println("Found " ~ size(wallAttributes) ~ " WALL attributes");
        
        for (var wallAttr in wallAttributes)
        {
            if (wallAttr.experimentType != undefined && wallAttr.experimentType == definition.experimentTypeFilter)
            {
                println("\nFound hidden copied face:");
                println("  Attribute ID: " ~ wallAttr.attributeId);
                println("  Custom Property: " ~ wallAttr.customProperty);
                println("  Experiment Type: " ~ wallAttr.experimentType);
            }
        }

        // Method 2: Query using qAttributeQuery to find entities with WALL attributes
        println("\n--- Method 2: Query using qAttributeQuery ---");
        const entitiesWithWallAttr = qAttributeQuery(asSMAttribute({ "objectType" : SMObjectType.WALL }));
        const foundEntities = evaluateQuery(context, entitiesWithWallAttr);
        
        // Debug visualization to show found entities (red color indicates hidden sheet metal surfaces)
        debug(context, entitiesWithWallAttr, DebugColor.RED);
        println("Found " ~ size(foundEntities) ~ " entities with WALL attributes via qAttributeQuery");

        // Method 3: Try to query all sheet metal bodies (hidden surfaces should be SHEET BodyType)
        println("\n--- Method 3: Query all SHEET bodies ---");
        const allSheetBodies = qBodyType(qEverything(), BodyType.SHEET);
        const sheetBodiesArray = evaluateQuery(context, allSheetBodies);
        
        println("Found " ~ size(sheetBodiesArray) ~ " total SHEET bodies");
        
        // Get attributes from sheet bodies
        for (var sheetBody in sheetBodiesArray)
        {
            const sheetFaces = qOwnedByBody(sheetBody, EntityType.FACE);
            const faceArray = evaluateQuery(context, sheetFaces);
            
            for (var face in faceArray)
            {
                const attrs = getAttributes(context, {
                    "entities" : face,
                    "attributePattern" : asSMAttribute({})
                });
                
                for (var attr in attrs)
                {
                    if (attr.experimentType != undefined && attr.experimentType == definition.experimentTypeFilter)
                    {
                        println("\nFound sheet body with hidden copied face:");
                        println("  Face: " ~ toString(face));
                        println("  Custom Property: " ~ attr.customProperty);
                    }
                }
            }
        }

        // Method 4: Query using getSMDefinitionEntities if we have a search scope
        if (!isQueryEmpty(context, definition.searchScope))
        {
            println("\n--- Method 4: Query using getSMDefinitionEntities ---");
            const smDefinitionEntities = getSMDefinitionEntities(context, definition.searchScope);
            
            println("Found " ~ size(smDefinitionEntities) ~ " SM definition entities from search scope");
            
            for (var entity in smDefinitionEntities)
            {
                const attrs = getAttributes(context, {
                    "entities" : entity,
                    "attributePattern" : asSMAttribute({})
                });
                
                for (var attr in attrs)
                {
                    if (attr.experimentType != undefined)
                    {
                        println("\nSM definition entity found:");
                        println("  Entity: " ~ toString(entity));
                        println("  Experiment Type: " ~ attr.experimentType);
                        println("  Custom Property: " ~ attr.customProperty);
                    }
                }
            }
        }

        // Method 5: Try broader query patterns
        println("\n--- Method 5: Broader query patterns ---");
        
        // Query for all entities (including hidden)
        const allEntities = evaluateQuery(context, qEverything());
        println("Total entities in context: " ~ size(allEntities));
        
        // Query specifically for surface faces
        const allFaces = evaluateQuery(context, qEntityFilter(qEverything(), EntityType.FACE));
        println("Total faces in context: " ~ size(allFaces));
        
        // Count how many have our custom experiment attribute
        var customAttributeCount = 0;
        for (var face in allFaces)
        {
            const attrs = getAttributes(context, {
                "entities" : face,
                "attributePattern" : asSMAttribute({})
            });
            
            for (var attr in attrs)
            {
                if (attr.experimentType != undefined && attr.experimentType == definition.experimentTypeFilter)
                {
                    customAttributeCount += 1;
                }
            }
        }
        println("Faces with experiment attribute: " ~ customAttributeCount);

        // Method 6: Query using qAttributeFilter for legacy unnamed attributes
        println("\n--- Method 6: Query using qAttributeFilter for unnamed attributes ---");
        const experimentEntities = qAttributeFilter(qEverything(), asSMAttribute({
            "experimentType" : definition.experimentTypeFilter
        }));
        const experimentEntitiesArray = evaluateQuery(context, experimentEntities);
        
        println("Found " ~ size(experimentEntitiesArray) ~ " entities with matching experimentType via qAttributeFilter");
        
        for (var entity in experimentEntitiesArray)
        {
            // Get unnamed attributes on this entity
            const attrs = getAttributes(context, {
                "entities" : entity,
                "attributePattern" : asSMAttribute({})
            });
            
            for (var attr in attrs)
            {
                if (attr.experimentType != undefined && attr.experimentType == definition.experimentTypeFilter)
                {
                    println("\nEntity with matching attribute:");
                    println("  Entity: " ~ toString(entity));
                    println("  Custom Property: " ~ attr.customProperty);
                }
            }
        }

        println("\n=== Query Complete ===");
    });
