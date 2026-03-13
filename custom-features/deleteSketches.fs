FeatureScript 2892;
// Delete Sketches
// A cleanup utility feature that removes sketch bodies (wire curves and sheet regions)
// from a part studio. Designed to produce cleaner geometry for Derive operations by
// stripping unreferenced or unwanted sketch entities before export.
//
// Two modes are available:
//   ALL      - Delete every modifiable sketch wire and region body in the part studio.
//   SELECTED - Delete only the explicitly selected sketch bodies.

import(path : "onshape/std/common.fs", version : "2892.0");
import(path : "onshape/std/query.fs", version : "2892.0");
import(path : "onshape/std/feature.fs", version : "2892.0");
import(path : "onshape/std/geomOperations.fs", version : "2892.0");
import(path : "onshape/std/error.fs", version : "2892.0");
import(path : "onshape/std/containers.fs", version : "2892.0");
import(path : "onshape/std/string.fs", version : "2892.0");

/**
 * Determines which sketch bodies the Delete Sketches feature will remove.
 *
 * @value ALL      : Every modifiable sketch wire body and sketch region (sheet body)
 *                   present in the part studio is deleted.
 * @value SELECTED : Only the sketch bodies explicitly chosen in the selection box
 *                   are deleted.
 */
export enum DeleteSketchesMode
{
    annotation { "Name" : "Delete all sketches" }
    ALL,
    annotation { "Name" : "Delete selected sketches" }
    SELECTED
}

/**
 * Delete Sketches feature.
 *
 * Removes sketch wire bodies and sketch region sheet bodies from the part studio.
 * This is useful as a final cleanup step before a Derive or export so that the
 * resulting body contains only solid/surface geometry and no leftover sketch entities.
 *
 * In ALL mode the feature finds every modifiable body that carries the SketchObject
 * attribute and deletes them in a single opDeleteBodies call.
 *
 * In SELECTED mode the user picks individual sketch bodies (wire curves or closed
 * regions) from the viewport and only those bodies are removed.
 */
annotation { "Feature Type Name" : "Delete sketches",
             "Feature Type Description" : "Removes sketch wire and region bodies from the part studio for cleaner derives." }
export const deleteSketches = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Scope selector: delete everything or only a hand-picked subset
        annotation { "Name" : "Scope" }
        definition.deleteMode is DeleteSketchesMode;

        if (definition.deleteMode == DeleteSketchesMode.SELECTED)
        {
            // Wire bodies represent sketch curves; sheet bodies represent closed sketch regions.
            // Both carry SketchObject.YES and can be selected here.
            annotation { "Name" : "Sketch bodies to delete",
                         "Filter" : EntityType.BODY && SketchObject.YES && ModifiableEntityOnly.YES,
                         "Description" : "Select sketch wire or region bodies to delete. Click sketch edges or faces in the viewport." }
            definition.sketchBodies is Query;
        }
    }
    {
        // Resolve which bodies to target based on the chosen scope mode
        var sketchBodiesToDelete;

        if (definition.deleteMode == DeleteSketchesMode.ALL)
        {
            // Collect every modifiable sketch body (wire curves + closed regions) in the studio
            sketchBodiesToDelete = qSketchFilter(
                qModifiableEntityFilter(qEverything(EntityType.BODY)),
                SketchObject.YES
            );
        }
        else
        {
            sketchBodiesToDelete = definition.sketchBodies;
        }

        // Count what we are about to delete so we can give useful feedback
        const sketchBodyCount = size(evaluateQuery(context, sketchBodiesToDelete));

        if (sketchBodyCount == 0)
        {
            if (definition.deleteMode == DeleteSketchesMode.ALL)
            {
                // A studio with no sketches is a valid state - report as informational
                reportFeatureWarning(context, id, "No sketch bodies found in the part studio.");
            }
            else
            {
                throw regenError("No sketch bodies selected. Pick one or more sketch bodies to delete.", ["sketchBodies"]);
            }
            return;
        }

        // Delete the resolved sketch bodies
        opDeleteBodies(context, id, { "entities" : sketchBodiesToDelete });

        // Inform the user how many bodies were removed
        reportFeatureInfo(context, id, toString(sketchBodyCount) ~
            (sketchBodyCount == 1 ? " sketch body deleted." : " sketch bodies deleted."));
    }, { deleteMode : DeleteSketchesMode.ALL });
