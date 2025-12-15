FeatureScript 2837;
// Experimental feature: Test if directly applying SM attributes to entities hides them
// No copying/extraction - just testing if attribute application alone is sufficient

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
 * Experimental feature to test if applying sheet metal attributes directly to entities hides them.
 * No copying - just applying attributes to selected geometry within defineSheetMetalFeature context.
 */
annotation { "Feature Type Name" : "Test Direct SM Attributes (Experiment)" }
export const hideEdgeSurface = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Entities to annotate",
                    "Description" : "Select faces, edges, vertices, or bodies to test direct attribute application",
                    "Filter" : (EntityType.FACE || EntityType.EDGE || EntityType.VERTEX || EntityType.BODY) && ConstructionObject.NO }
        definition.targetEntities is Query;

        annotation { "Name" : "Attribute Type",
                    "UIHint" : UIHint.SHOW_LABEL,
                    "Default" : SMObjectType.WALL }
        definition.attributeType is SMObjectType;
    }
    {
        // Validate that we have at least one entity selected
        const entityArray = evaluateQuery(context, definition.targetEntities);
        if (size(entityArray) == 0)
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["targetEntities"]);
        }

        println("Testing direct SM attribute application on " ~ size(entityArray) ~ " entities");
        debug(context, definition.targetEntities, DebugColor.CYAN);
        
        // Try directly applying sheet metal attributes to the selected entities
        try
        {
            // Create a simple SM attribute
            var smAttribute = makeSMAttribute(toString(id));
            smAttribute.objectType = definition.attributeType;
            
            // Apply attribute directly to entities
            setAttribute(context, {
                "entities" : definition.targetEntities,
                "attribute" : smAttribute
            });
            
            println("Applied " ~ definition.attributeType ~ " attribute to entities");
            println("Check if entities are now hidden from standard queries");
        }
        catch (error)
        {
            println("Error applying SM attribute: " ~ toString(error));
        }
        
        println("Experiment complete - run query feature to check visibility");
    }, {});
