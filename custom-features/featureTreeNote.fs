FeatureScript 2909;
import(path : "onshape/std/feature.fs", version : "2909.0");
import(path : "onshape/std/featureList.fs", version : "2909.0");
ICON::import(path : "5db36f2dbd94dae497f2c12f", version : "e12a2e27652d4be6e377c94f");

// NOTE: Onshape does not expose a public FeatureScript API for creating free-floating
// text annotations (datum labels, MBD notes, GTol callouts) in the 3D viewport from
// custom features.  The internal datum/note/GTol annotation types used in Onshape's own
// MBD panel are not accessible to custom feature code.
//
// What IS available for 3D visualization from FeatureScript:
//   - setHighlightedEntities  : persistent colored highlight on selected geometry
//   - setDimensionedEntities  : numbered inspection-dimension balloons (shows a
//                               measurement VALUE, not custom text)
//
// This feature therefore:
//   1. Highlights the referenced geometry in the Part Studio viewport via
//      setHighlightedEntities so there is a clear 3D visual indicator.
//   2. Surfaces the note text (subject + description) as a feature-status info
//      message that appears in the feature panel and tooltip when the note is
//      selected or hovered.

annotation { "Feature Type Name" : "Feature tree note",
            "Feature Name Template" : "#subject",
            "UIHint" : UIHint.NO_PREVIEW_PROVIDED,
            "Tooltip Template" : "#subject: #description",
            "Icon" : ICON::BLOB_DATA,
            "Feature Type Description" : "Stores notes in the feature tree. Referenced geometry is highlighted persistently in the 3D viewport. The note text (subject and description) is surfaced as a feature-status info message visible when the feature is selected." }

export const featureTreeNote = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Subject", "UIHint" : UIHint.VARIABLE_NAME, "MaxLength" : 256 }
        definition.subject is string;

        annotation { "Name" : "Description (shows on hover)", "MaxLength" : 512 }
        definition.description is string;

        annotation { "Name" : "More text here", "MaxLength" : 640000 }
        definition.text is string;

        annotation { "Name" : "Reference geometry", "Filter" : AllowMeshGeometry.YES && AllowFlattenedGeometry.YES,
            "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS }
        definition.geometry is Query;

        annotation { "Name" : "Reference features", "UIHint" : UIHint.ALLOW_FLAT_SKETCH_SELECTION }
        definition.features is FeatureList;
    }
    {
        // Persistently highlight the referenced geometry with a colored overlay in the
        // 3D viewport so the note's associated faces/edges remain visually marked after
        // the feature dialog is closed.
        try
        {
            setHighlightedEntities(context, { "entities" : definition.geometry });
        }

        // Surface the note subject and description as a feature-status info message.
        // This text appears in the feature panel tooltip and in the info bubble on the
        // feature in the feature tree when the note is selected or hovered.
        // Only build and emit the status string when subject is non-empty.
        if (definition.subject != "")
        {
            var noteStatusText is string = definition.subject;
            if (definition.description != "")
            {
                noteStatusText = noteStatusText ~ ": " ~ definition.description;
            }
            reportFeatureInfo(context, id, noteStatusText);
        }
    });
