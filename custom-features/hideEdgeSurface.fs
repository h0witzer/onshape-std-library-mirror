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
import(path : "onshape/std/sheetMetalStart.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");
import(path : "onshape/std/smobjecttype.gen.fs", version : "2837.0");
import(path : "onshape/std/smreliefstyle.gen.fs", version : "2837.0");
import(path : "onshape/std/string.fs", version : "2837.0");
import(path : "onshape/std/topologyUtils.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");

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

        // Use the proper sheet metal annotation workflow from sheetMetalStart
        // This annotates surfaces with sheet metal attributes properly
        var annotationArgs = {
            "surfaceBodies" : createdSurface,
            "bendEdgesAndFaces" : qNothing(),
            "specialRadiiBends" : [],
            "defaultRadius" : 0.01 * meter,
            "controlsThickness" : false,
            "thickness" : 0.01 * meter,
            "thicknessDirection" : SMThicknessDirection.BOTH,
            "minimalClearance" : 0.001 * meter,
            "kFactor" : 0.45,
            "kFactorRolled" : 0.45,
            "flipDirectionUp" : false,
            "defaultTwoCornerStyle" : SMReliefStyle.SIMPLE,
            "defaultThreeCornerStyle" : SMReliefStyle.SIMPLE,
            "defaultBendReliefStyle" : SMReliefStyle.OBROUND,
            "defaultCornerReliefScale" : 1.5,
            "defaultRoundReliefDiameter" : 0 * meter,
            "defaultSquareReliefWidth" : 0 * meter,
            "defaultBendReliefDepthScale" : 2.0,
            "defaultBendReliefScale" : 1.0625,
            "bendCalculationType" : SMBendCalculationType.K_FACTOR
        };
        
        try
        {
            // Annotate the surface bodies with proper sheet metal attributes
            annotateSmSurfaceBodies(context, id, annotationArgs, 0);
            
            // Add custom properties after annotation
            const surfaceFaces = qOwnedByBody(createdSurface, EntityType.FACE);
            for (var face in evaluateQuery(context, surfaceFaces))
            {
                var attrs = getAttributes(context, {
                    "entities" : face,
                    "attributePattern" : asSMAttribute({})
                });
                if (size(attrs) > 0)
                {
                    var attr = attrs[0];
                    attr.customProperty = definition.customProperty;
                    attr.experimentType = "hiddenCopiedFace";
                    setAttribute(context, {
                        "entities" : face,
                        "attribute" : attr
                    });
                }
            }
            
            // Finalize the sheet metal geometry to trigger hiding
            updateSheetMetalGeometry(context, id, {
                "entities" : qUnion([qOwnedByBody(createdSurface, EntityType.FACE), qOwnedByBody(createdSurface, EntityType.EDGE)])
            });
            
            // Log confirmation
            println("Copied face annotated and finalized with custom property: " ~ definition.customProperty);
        }
        catch (error)
        {
            println("Error during sheet metal annotation: " ~ toString(error));
            throw regenError("Failed to apply sheet metal attributes to copied face.", ["targetFace"]);
        }
    }, {});
