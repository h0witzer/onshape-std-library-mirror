FeatureScript 2837;
// Experimental feature: Copy a face and hide it using sheet metal annotations
// This explores whether sheet metal's hidden body concept can be adapted for Frame-like attributes

export import(path : "onshape/std/query.fs", version : "2837.0");

import(path : "onshape/std/attributes.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2837.0");
import(path : "onshape/std/smobjecttype.gen.fs", version : "2837.0");
import(path : "onshape/std/string.fs", version : "2837.0");
import(path : "onshape/std/topologyUtils.fs", version : "2837.0");

/**
 * Feature that copies a face and hides it using sheet metal annotations.
 * This is an experiment to explore whether surfaces can be hidden from regular queries while maintaining
 * their existence and stored properties for later retrieval.
 */
annotation { "Feature Type Name" : "Hide Copied Face (Experiment)" }
export const hideEdgeSurface = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face to copy and hide",
                    "Filter" : EntityType.FACE && ConstructionObject.NO }
        definition.targetFace is Query;

        annotation { "Name" : "Custom property value",
                    "Description" : "A custom property to store in the sheet metal attribute" }
        definition.customProperty is string;
    }
    {
        // Validate that we have at least one face selected
        const faceArray = evaluateQuery(context, definition.targetFace);
        if (size(faceArray) == 0)
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["targetFace"]);
        }

        // Extract/copy the selected face(s) to create a new surface body
        try
        {
            opExtractSurface(context, id + "extractSurface", {
                "faces" : definition.targetFace
            });
        }
        catch
        {
            throw regenError("Failed to copy face. Check face selection.", ["targetFace"]);
        }

        // Get the created surface body
        const createdSurface = qCreatedBy(id + "extractSurface", EntityType.BODY);
        const surfaceFaces = qOwnedByBody(createdSurface, EntityType.FACE);

        // Create a custom sheet metal attribute to store on the surface
        // We'll use a WALL attribute with custom properties
        const featureIdString = toAttributeId(id);
        var customAttribute = makeSMWallAttribute(featureIdString);
        
        // Add custom properties to the attribute
        customAttribute.customProperty = definition.customProperty;
        customAttribute.experimentType = "hiddenCopiedFace";

        // Set the attribute on the surface faces
        setAttribute(context, {
            "entities" : surfaceFaces,
            "attribute" : customAttribute
        });

        // Note: We don't call updateSheetMetalGeometry here because we're not actually
        // building sheet metal geometry - we're just testing if annotating surfaces with
        // sheet metal attributes causes them to be hidden from regular queries

        // Log confirmation that the surface was created and annotated
        println("Copied face annotated with custom property: " ~ definition.customProperty);
    }, {});
