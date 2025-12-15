FeatureScript 2837;
// Experimental feature: Test what types of geometry can be hidden using sheet metal annotations
// Expanded experiment to test faces, edges, vertices, and bodies

export import(path : "onshape/std/query.fs", version : "2837.0");

import(path : "onshape/std/attributes.fs", version : "2837.0");
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
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
 * Experimental feature to test what types of geometry can be hidden using sheet metal annotations.
 * Tests copying various entity types and applying sheet metal attributes to see what becomes hidden.
 */
annotation { "Feature Type Name" : "Test SM Hiding (Experiment)" }
export const hideEdgeSurface = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Entities to copy and hide",
                    "Description" : "Select faces, edges, vertices, or bodies to test hiding",
                    "Filter" : (EntityType.FACE || EntityType.EDGE || EntityType.VERTEX || EntityType.BODY) && ConstructionObject.NO }
        definition.targetEntities is Query;

        annotation { "Name" : "Apply SM Annotation",
                    "Description" : "Enable to apply sheet metal annotation (testing if it hides the geometry)" }
        definition.applySMAnnotation is boolean;
    }
    {
        // Validate that we have at least one entity selected
        const entityArray = evaluateQuery(context, definition.targetEntities);
        if (size(entityArray) == 0)
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["targetEntities"]);
        }

        println("Selected " ~ size(entityArray) ~ " entities to test hiding");

        // Copy faces to create surface bodies
        const faceQuery = qEntityFilter(definition.targetEntities, EntityType.FACE);
        const faceArray = evaluateQuery(context, faceQuery);
        
        if (size(faceArray) > 0)
        {
            try
            {
                opExtractSurface(context, id + "extractSurface", {
                    "faces" : faceQuery
                });
                println("Copied " ~ size(faceArray) ~ " faces to surface bodies");
            }
            catch
            {
                println("Warning: Failed to copy faces");
            }
        }

        // Get the created surface bodies (if any were created from faces)
        const createdSurfaces = qCreatedBy(id + "extractSurface", EntityType.BODY);
        if (!isQueryEmpty(context, createdSurfaces))
        {
            debug(context, createdSurfaces, DebugColor.CYAN);
        }

        // Apply sheet metal annotation if requested
        if (definition.applySMAnnotation == true && !isQueryEmpty(context, createdSurfaces))
        {
            // Set up minimal annotation arguments for sheet metal
            var annotationArgs = {
                "surfaceBodies" : createdSurfaces,
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
                // KEY FINDING: annotateSmSurfaceBodies successfully hides surfaces
                annotateSmSurfaceBodies(context, id, annotationArgs, 0);
                println("Applied sheet metal annotation - surfaces should now be hidden");
            }
            catch (error)
            {
                println("Error during sheet metal annotation: " ~ toString(error));
            }
        }
        
        println("Experiment complete - check visibility in part studio");
    }, {});
