//_______________________________________________________________________________________________________________________________________________
//
// Text Multi-Location Wrapper for Pascoe Text Feature
// This wrapper allows running the Pascoe text feature at multiple mate connector locations
//_______________________________________________________________________________________________________________________________________________


FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/projectiontype.gen.fs", version : "2837.0");

// Import textFunctionsPascoe.fs from the same custom-features directory
// This contains TextFunctionPascoe, BooleanScopeLocal, BodyOptions, FontEnumLocal, and predicates
export import(path : "5d523046fa535976e27cb329", version : "aa12e73ebc144f7df0925779"); //textFunctionsPascoe.fs

// CADSharp
export import(path : "cbeb3dcf671e00785597bd76/409d65a3744fe434f32bdffc/a75ab01def146a42f55baa7f", version : "381046010d5aea697e433948");

annotation {
        "Feature Type Name" : "Text - Multi Location",
        "Feature Type Description" : "<b> Summary </b> <br> Creates text at multiple mate connector locations. <br>",
        "Editing Logic Function" : "editLogicMultiText" }
export const textMultiLocation = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        TextMultiLocationMainPredicate(definition);
        TextSettingsPredicatePascoe(definition);
    }
    {
        // Evaluate all mate connectors from the query
        const mateConnectors = evaluateQuery(context, definition.locations);
        
        if (size(mateConnectors) == 0)
        {
            throw regenError("Please select at least one mate connector location");
        }
        
        // Iterate through each mate connector and call TextFunctionPascoe
        for (var index = 0; index < size(mateConnectors); index += 1)
        {
            const currentMateConnector = mateConnectors[index];
            
            // Call TextFunctionPascoe for this location
            TextFunctionPascoe(
                context,
                id + ("text" ~ index),
                definition.booleanEnum,
                definition.bodyOption,
                definition.text,
                currentMateConnector,
                definition.mergeScope,
                definition.depth,
                definition.oppositeDirection,
                definition.textHeight,
                definition.font,
                definition.bold,
                definition.italic,
                definition.mirrorHorizontal,
                definition.mirrorVertical);
        }
    });


/**
 * Precondition for the multi-location text feature main parameters.
 * Defines the UI and validation for the feature inputs.
 * @param definition : The feature definition map containing user inputs
 */
export predicate TextMultiLocationMainPredicate(definition is map)
{
    annotation { "Name" : "Boolean enum", "Default" : BooleanScopeLocal.NEW, "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
    definition.booleanEnum is BooleanScopeLocal;

    if (definition.booleanEnum == BooleanScopeLocal.NEW)
    {
        annotation { "Name" : "Body type", "Default" : BodyOptions.SOLID, "UIHint" : [UIHint.SHOW_LABEL, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.bodyOption is BodyOptions;
    }

    annotation { "Name" : "Text (abc)", "Default" : "Words" }
    definition.text is string;

    annotation { "Name" : "Mate Connectors", "Filter" : BodyType.MATE_CONNECTOR }
    definition.locations is Query;

    if (definition.booleanEnum != BooleanScopeLocal.NEW)
    {
        annotation { "Name" : "Merge scope", "Filter" : EntityType.FACE || EntityType.BODY }
        definition.mergeScope is Query;
    }

    if (definition.booleanEnum != BooleanScopeLocal.SPLIT)
    {
        annotation { "Name" : "Depth", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.depth, LENGTH_BOUNDS);

        annotation { "Name" : "Opposite direction", "UIHint" : [UIHint.OPPOSITE_DIRECTION, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.oppositeDirection is boolean;
    }
}


/**
 * Editing logic function for the multi-location text feature.
 * Handles dynamic behavior and auto-population of fields based on user selections.
 * @param context : The current context
 * @param id : The feature id
 * @param oldDefinition : The previous feature definition
 * @param definition : The current feature definition
 * @param isCreating : True if this is a new feature being created
 * @param specifiedParameters : Map of parameters that were explicitly set by the user
 * @returns {map} : The updated definition
 */
export function editLogicMultiText(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, specifiedParameters is map) returns map
{
    definition = cadsharpUrlFunctionForPreExistingEditLogic(oldDefinition, definition);
    
    return definition;
}
