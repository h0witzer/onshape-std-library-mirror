FeatureScript 2909;
import(path : "onshape/std/feature.fs", version : "2909.0");
import(path : "onshape/std/featureList.fs", version : "2909.0");
import(path : "onshape/std/valueBounds.fs", version : "2909.0");
import(path : "onshape/std/featuredimensiontype.gen.fs", version : "2909.0");
ICON::import(path : "5db36f2dbd94dae497f2c12f", version : "e12a2e27652d4be6e377c94f");

annotation { "Feature Type Name" : "Feature tree note",
            "Feature Name Template" : "#subject",
            "UIHint" : UIHint.NO_PREVIEW_PROVIDED,
            "Tooltip Template" : "#subject: #description",
            "Icon" : ICON::BLOB_DATA,
            "Feature Type Description" : "Stores notes in the feature tree, highlights referenced geometry, and optionally registers a 3D MBD inspection annotation on selected faces via the inspection dimension infrastructure." }

export const featureTreeNote = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Note identification displayed in the feature tree name and tooltip
        annotation { "Name" : "Subject", "UIHint" : UIHint.VARIABLE_NAME, "MaxLength" : 256 }
        definition.subject is string;

        annotation { "Name" : "Description (shows on hover)", "MaxLength" : 512 }
        definition.description is string;

        annotation { "Name" : "More text here", "MaxLength" : 640000 }
        definition.text is string;

        // Geometry reference - highlighted persistently in the 3D viewport
        annotation { "Name" : "Reference geometry", "Filter" : AllowMeshGeometry.YES && AllowFlattenedGeometry.YES,
            "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS }
        definition.geometry is Query;

        annotation { "Name" : "Reference features", "UIHint" : UIHint.ALLOW_FLAT_SKETCH_SELECTION }
        definition.features is FeatureList;

        // Optional face selection used to anchor a 3D MBD inspection annotation to this note.
        // Bastardizes the inspection dimension infrastructure: the anchor dimension value is
        // not a meaningful measurement - it serves only as a 3D visual marker linking the
        // geometry to this note in the feature tree and in the inspection dimensions table.
        annotation { "Name" : "Annotation anchor faces",
            "Filter" : EntityType.FACE,
            "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS }
        definition.annotationFaces is Query;

        // Hidden length parameter required by setDimensionedEntities to register the 3D
        // inspection annotation.  Defaults to 0 (dimensionless anchor); the user may
        // optionally apply tolerances via the CAN_BE_TOLERANT toggle that appears once
        // annotation faces are selected, to record manufacturing intent alongside the note.
        annotation { "Name" : "Note anchor", "UIHint" : [UIHint.CAN_BE_TOLERANT, UIHint.ALWAYS_HIDDEN] }
        isLength(definition.noteAnchor, ZERO_DEFAULT_LENGTH_BOUNDS);
    }
    {
        // Persistently highlight reference geometry in the 3D viewport so the note's
        // associated entities are always visible, not just while editing the feature.
        try
        {
            setHighlightedEntities(context, { "entities" : definition.geometry });
        }

        // Register a 3D MBD inspection annotation on the selected faces, placing a
        // visible inspection-dimension marker in the Part Studio viewport and adding
        // an entry to the inspection dimensions table.  The "distance" is always the
        // value of noteAnchor (default 0 mm) since both queries reference the same
        // face selection - the value is intentionally meaningless as a measurement;
        // its purpose is purely to anchor the note visually in 3D space.
        try
        {
            if (size(evaluateQuery(context, definition.annotationFaces)) > 0)
            {
                setDimensionedEntities(context, {
                    "parameterId"   : "noteAnchor",
                    // Both queries intentionally reference the same face selection so that
                    // the measured "distance" is always 0 mm.  The value itself is not
                    // meaningful; the sole purpose is to place a visual inspection-dimension
                    // marker on the face and add this note to the inspection dimensions table.
                    "queries"       : [definition.annotationFaces, definition.annotationFaces],
                    "dimensionType" : FeatureDimensionType.DISTANCE
                });
            }
        }
    });
