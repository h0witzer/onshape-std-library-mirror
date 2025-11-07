FeatureScript 2656;
import(path : "onshape/std/common.fs", version : "2656.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2656.0"); // For defineSheetMetalFeature, updateSheetMetalGeometry
icon::import(path : "135d90cd35918a6db8a38c62", version : "9dd9b3a4ad6d02492bd7f34b");


// Written by Derek Van Allen
// I created this script because I need to create mirrors of components that maintain all of the geometric Ids that are being used in assembly context
// Assembly mirror isn't a thing in Onshape at the time of writing and this covers a lot of the use-case that people are asking for that improvement for
// Anyway this isn't a mirror script, this is a scaling script applied wrongly

annotation { "Feature Type Name" : "Lefty Flip", "Icon" : icon::BLOB_DATA, "Feature Type Description" : "Identity preserving mirror via nonuniform scaling transform" }
export const leftyFlip = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Bodies To 'Mirror'", "Filter" : EntityType.BODY }
        definition.mirrorBodies is Query;

        annotation { "Name" : "(Optional) Mirror Coordinate System", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
        definition.mirrorReference is Query;
    }
    {

        var cSys = undefined;
        try
        {
            cSys = evMateConnector(context, {
                        "mateConnector" : definition.mirrorReference
                    });
        }

        // Separate sheet metal bodies from non-sheet metal bodies
        const separated = separateSheetMetalQueries(context, definition.mirrorBodies);
        const smBodiesQ = separated.sheetMetalQueries; // These are queries for the *active sheet metal models*
        const nonSmBodiesQ = separated.nonSheetMetalQueries;

        // Determine the transformation based on the mirror reference
        var mirrorTransform = scaleNonuniformly(-1, 1, 1); // Default if no CSYS, I chose mirroring across the right plane because left and right handedness
        if (cSys != undefined)
        {
            mirrorTransform = scaleNonuniformly(-1, 1, 1, cSys); // Scale about the CSYS
        }

        // 1. Handle Non-Sheet Metal Bodies: Apply transform directly
        if (!isQueryEmpty(context, nonSmBodiesQ))
        {
            opTransform(context, id + "transform_non_sm", { //Mrofsnart
                        "bodies" : nonSmBodiesQ,
                        "transform" : mirrorTransform
                    });
        }

        // 2. Handle Sheet Metal Bodies: Currently does the whole context at once but probably if you're using this script instead of a basic mirror that's what you were gonna do anyway
        if (!isQueryEmpty(context, smBodiesQ))
        {
            // Get the actual underlying sheet metal models.
            const sheetMetalModelsToTransform = getSheetMetalModelForPart(context, smBodiesQ);

            // Start tracking entities for attribute management.
            const initialData = getInitialEntitiesAndAttributes(context, sheetMetalModelsToTransform);
            const smEntitiesToTrack = qUnion([
                        qOwnedByBody(sheetMetalModelsToTransform, EntityType.EDGE),
                        qOwnedByBody(sheetMetalModelsToTransform, EntityType.FACE),
                        sheetMetalModelsToTransform
                    ]);
            const tracking = startTracking(context, smEntitiesToTrack);

            // Apply the transform to the sheet metal master bodies.
            opTransform(context, id + "transform_sm", {
                        "bodies" : sheetMetalModelsToTransform,
                        "transform" : mirrorTransform
                    });

            // Assign new SMAttributes to any new or split entities on the master body
            // It ensures that features like bends, rips, etc., are correctly associated after the transform.
            const toUpdate = assignSMAttributesToNewOrSplitEntities(context, sheetMetalModelsToTransform, initialData, id);

            // Update the 3D folded body and flat pattern based on the modified master body.
            callSubfeatureAndProcessStatus(id, updateSheetMetalGeometry, context, id + "smUpdate", {
                        "entities" : qUnion([toUpdate.modifiedEntities, qCreatedBy(id + "transform_sm"), tracking]),
                        "deletedAttributes" : toUpdate.deletedAttributes,
                        "associatedChanges" : tracking // Pass tracking queries for associated changes
                    });
        }
    }, {});
