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


export enum TextSourceType
{
    annotation { "Name" : "Manual text" }
    MANUAL,
    annotation { "Name" : "Part name" }
    PART_NAME,
    annotation { "Name" : "Part number" }
    PART_NUMBER,
    annotation { "Name" : "Part description" }
    PART_DESCRIPTION
}

const DEFAULT_TEXT = "Text";


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
        
        // Build array of text strings and merge scopes for each location
        var textArray = [];
        var mergeScopeArray = [];
        
        for (var textInstanceIndex, currentMateConnector in mateConnectors)
        {
            // Get text for this location based on text source type
            var currentText = definition.text;
            if (definition.textSourceType != TextSourceType.MANUAL)
            {
                // Get the text from stored property if available, otherwise use default
                if (definition.textArray != undefined && size(definition.textArray) > textInstanceIndex)
                {
                    currentText = definition.textArray[textInstanceIndex];
                }
                else
                {
                    currentText = DEFAULT_TEXT;
                }
            }
            
            textArray = append(textArray, currentText);
            
            // Determine merge scope for this location
            var currentMergeScope = qNothing();
            if (definition.mergeScopeArray != undefined && size(definition.mergeScopeArray) > textInstanceIndex)
            {
                currentMergeScope = definition.mergeScopeArray[textInstanceIndex];
            }
            else if (definition.mergeScope != undefined)
            {
                currentMergeScope = definition.mergeScope;
            }
            
            mergeScopeArray = append(mergeScopeArray, currentMergeScope);
            
            // Call TextFunctionPascoe for this location
            TextFunctionPascoe(
                context,
                id + ("text" ~ textInstanceIndex),
                definition.booleanEnum,
                definition.bodyOption,
                currentText,
                currentMateConnector,
                currentMergeScope,
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

    annotation { "Name" : "Text source", "Default" : TextSourceType.MANUAL, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
    definition.textSourceType is TextSourceType;

    if (definition.textSourceType == TextSourceType.MANUAL)
    {
        annotation { "Name" : "Text (abc)", "Default" : "Words" }
        definition.text is string;
    }

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

    if (definition.textSourceType != TextSourceType.MANUAL && 
        (definition.booleanEnum == BooleanScopeLocal.ADD || 
         definition.booleanEnum == BooleanScopeLocal.SUBTRACT ||
         definition.booleanEnum == BooleanScopeLocal.INTERSECT))
    {
        annotation { "Name" : "Update from part properties", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
        definition.updatePartProperties is boolean;
    }
    
    // Hidden parameters for storing per-location data (populated by editing logic)
    annotation { "Name" : "Text array (internal)", "UIHint" : UIHint.ALWAYS_HIDDEN }
    definition.textArray is array;
    
    annotation { "Name" : "Merge scope array (internal)", "UIHint" : UIHint.ALWAYS_HIDDEN }
    definition.mergeScopeArray is array;
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
    
    const mateConnectors = evaluateQuery(context, definition.locations);
    const locationsChanged = definition.locations != oldDefinition.locations;
    
    // Auto-populate merge scope for each mate connector when locations change
    if (locationsChanged && (definition.booleanEnum == BooleanScopeLocal.ADD || 
                             definition.booleanEnum == BooleanScopeLocal.SUBTRACT || 
                             definition.booleanEnum == BooleanScopeLocal.INTERSECT))
    {
        var mergeScopeArray = [];
        
        for (var mateConnector in mateConnectors)
        {
            const faceAtMateConnector = getFaceAtMateConnectorOrigin(context, mateConnector);
            if (!isQueryEmpty(context, faceAtMateConnector))
            {
                mergeScopeArray = append(mergeScopeArray, qOwnerBody(faceAtMateConnector));
            }
            else
            {
                mergeScopeArray = append(mergeScopeArray, qNothing());
            }
        }
        
        definition.mergeScopeArray = mergeScopeArray;
    }
    
    // Update part properties when button is pressed or text source type changes
    if (definition.textSourceType != TextSourceType.MANUAL && 
        (definition.booleanEnum == BooleanScopeLocal.ADD || 
         definition.booleanEnum == BooleanScopeLocal.SUBTRACT ||
         definition.booleanEnum == BooleanScopeLocal.INTERSECT))
    {
        const shouldUpdate = (definition.updatePartProperties != oldDefinition.updatePartProperties) ||
                            (definition.textSourceType != oldDefinition.textSourceType) ||
                            locationsChanged;
        
        if (shouldUpdate)
        {
            // Reset the button state
            definition.updatePartProperties = oldDefinition.updatePartProperties;
            
            var textArray = [];
            
            // Get merge scope array or fall back to single merge scope
            var mergeScopeArray = definition.mergeScopeArray;
            if (mergeScopeArray == undefined)
            {
                mergeScopeArray = [];
                for (var i = 0; i < size(mateConnectors); i += 1)
                {
                    mergeScopeArray = append(mergeScopeArray, definition.mergeScope);
                }
            }
            
            for (var mergeScope in mergeScopeArray)
            {
                var currentText = DEFAULT_TEXT;
                
                try silent
                {
                    if (!isQueryEmpty(context, mergeScope))
                    {
                        var propertyType = PropertyType.NAME;
                        
                        if (definition.textSourceType == TextSourceType.PART_NUMBER)
                        {
                            propertyType = PropertyType.PART_NUMBER;
                        }
                        else if (definition.textSourceType == TextSourceType.PART_DESCRIPTION)
                        {
                            propertyType = PropertyType.DESCRIPTION;
                        }
                        
                        const propertyValue = getProperty(context, {
                                "entity" : mergeScope,
                                "propertyType" : propertyType
                            });
                        
                        if (propertyValue != undefined)
                        {
                            currentText = propertyValue;
                        }
                    }
                }
                catch
                {
                    // If property retrieval fails, use default text
                    currentText = DEFAULT_TEXT;
                }
                
                textArray = append(textArray, currentText);
            }
            
            definition.textArray = textArray;
        }
    }
    else if (definition.textSourceType == TextSourceType.MANUAL)
    {
        // Clear text array when switching to manual mode
        definition.textArray = undefined;
    }
    
    return definition;
}
