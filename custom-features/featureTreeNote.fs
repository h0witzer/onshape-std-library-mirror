FeatureScript 2796;
import(path : "onshape/std/feature.fs", version : "2796.0");
ICON::import(path : "5db36f2dbd94dae497f2c12f", version : "e12a2e27652d4be6e377c94f");

annotation { "Feature Type Name" : "Feature tree note",
            "Feature Name Template" : "#subject",
            "UIHint" : UIHint.NO_PREVIEW_PROVIDED,
            "Tooltip Template" : "#subject: #description",
            "Icon" : ICON::BLOB_DATA,
            "Feature Type Description" : "This feature does nothing, it just allows you to store and show some notes in the feature tree, like a READ ME note" }
            
export const featureTreeNote = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Subject", "UIHint" : UIHint.VARIABLE_NAME, "MaxLength" : 256 }
        definition.subject is string;

        annotation { "Name" : "Description (shows on hover)", "MaxLength" : 512, "Default" : ""  }
        definition.description is string;
        
        annotation { "Name" : "More text here" }
        definition.text is string;

    }
    {
        const subject = definition.subject;
        const description  = definition.description;
    });
