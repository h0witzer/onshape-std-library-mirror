// opCreateOutline Test Feature
// 
// This is a comprehensive test feature for the opCreateOutline operation.
// It allows testing all parameters of opCreateOutline with different settings.
// 
// opCreateOutline creates a 2D outline of 3D tool bodies/surfaces projected onto a target face.
// The target face must be a plane, cylinder, or extruded surface.
// 
// Parameters tested:
// - tools: The tool parts or surfaces to project
// - target: The face whose surface will be used to create outline (plane/cylinder/extruded)
// - offsetFaces (optional): Faces in tools which are offsets of target face
//
// This feature allows toggling the optional offsetFaces parameter to test both modes.

FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/topologyUtils.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");

annotation { "Feature Type Name" : "Test opCreateOutline" }
export const testOpCreateOutline = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { 
            "Name" : "Tool bodies and faces", 
            "Filter" : (EntityType.FACE || EntityType.BODY && (BodyType.SOLID || BodyType.SHEET)) && ConstructionObject.NO,
            "MaxNumberOfPicks" : 100
        }
        definition.tools is Query;

        annotation { 
            "Name" : "Target face",
            "Filter" : EntityType.FACE && (GeometryType.PLANE || GeometryType.CYLINDER || GeometryType.EXTRUDED),
            "MaxNumberOfPicks" : 1
        }
        definition.target is Query;

        annotation { "Name" : "Use offset faces parameter" }
        definition.useOffsetFaces is boolean;

        if (definition.useOffsetFaces)
        {
            annotation { 
                "Name" : "Offset faces (optional)", 
                "Filter" : EntityType.FACE,
                "MaxNumberOfPicks" : 100
            }
            definition.offsetFaces is Query;
        }

        annotation { "Name" : "Show debug information" }
        definition.showDebug is boolean;

        annotation { "Name" : "Keep intermediate bodies" }
        definition.keepIntermediateBodies is boolean;
    }
    {
        if (definition.showDebug)
        {
            println("=== opCreateOutline Test Feature START ===");
        }

        // Prepare tools - handle both bodies and faces
        var toolsToUse is Query = definition.tools;
        var intermediateBodiesToDelete is Query = qNothing();
        
        // Check if we have faces in the tools selection
        const toolFacesQuery is Query = toolsToUse->qEntityFilter(EntityType.FACE);
        const toolFaces = evaluateQuery(context, toolFacesQuery);
        
        if (size(toolFaces) > 0)
        {
            if (definition.showDebug)
            {
                println("Processing " ~ size(toolFaces) ~ " tool faces");
                println("Extracting surfaces from selected faces...");
            }
            
            // Extract surfaces from faces to create surface bodies
            opExtractSurface(context, id + "extractToolSurfaces", {
                "faces" : toolFacesQuery,
                "offset" : 0 * meter
            });

            const extractedBodies is Query = qCreatedBy(id + "extractToolSurfaces");
            
            // Update tools to use extracted bodies instead of faces
            toolsToUse = (toolsToUse->qSubtraction(toolFacesQuery))->qUnion(extractedBodies->qEntityFilter(EntityType.BODY));
            intermediateBodiesToDelete = intermediateBodiesToDelete->qUnion(extractedBodies);
            
            if (definition.showDebug)
            {
                println("Extracted " ~ size(evaluateQuery(context, extractedBodies)) ~ " surface bodies from faces");
            }
        }

        // Evaluate target
        const targetFaces = evaluateQuery(context, definition.target);
        if (size(targetFaces) == 0)
        {
            throw regenError("No target face selected");
        }
        
        if (definition.showDebug)
        {
            println("Target face count: " ~ size(targetFaces));
            
            // Get target face geometry type
            const targetFace = targetFaces[0];
            const targetSurface = evSurfaceDefinition(context, {
                "face" : targetFace
            });
            println("Target surface type: " ~ targetSurface.surfaceType);
        }

        // Evaluate tools
        const toolBodies = evaluateQuery(context, toolsToUse);
        if (size(toolBodies) == 0)
        {
            throw regenError("No tool bodies selected");
        }
        
        if (definition.showDebug)
        {
            println("Tool bodies count: " ~ size(toolBodies));
        }

        // Build the opCreateOutline definition
        var createOutlineDefinition is map = {
            "tools" : toolsToUse,
            "target" : definition.target
        };

        // Add offsetFaces parameter if enabled
        if (definition.useOffsetFaces)
        {
            const offsetFaces = evaluateQuery(context, definition.offsetFaces);
            if (size(offsetFaces) > 0)
            {
                createOutlineDefinition.offsetFaces = definition.offsetFaces;
                
                if (definition.showDebug)
                {
                    println("Using offsetFaces parameter with " ~ size(offsetFaces) ~ " faces");
                }
            }
            else
            {
                if (definition.showDebug)
                {
                    println("WARNING: useOffsetFaces is true but no offset faces selected");
                }
            }
        }
        else
        {
            if (definition.showDebug)
            {
                println("offsetFaces parameter NOT used");
            }
        }

        // Execute opCreateOutline
        if (definition.showDebug)
        {
            println("Executing opCreateOutline...");
        }

        try
        {
            opCreateOutline(context, id + "createOutline", createOutlineDefinition);
            
            if (definition.showDebug)
            {
                println("opCreateOutline executed successfully");
                
                // Report created entities
                const createdFaces = evaluateQuery(context, qCreatedBy(id + "createOutline", EntityType.FACE));
                const createdEdges = evaluateQuery(context, qCreatedBy(id + "createOutline", EntityType.EDGE));
                const createdBodies = evaluateQuery(context, qCreatedBy(id + "createOutline", EntityType.BODY));
                
                println("Created entities:");
                println("  Bodies: " ~ size(createdBodies));
                println("  Faces: " ~ size(createdFaces));
                println("  Edges: " ~ size(createdEdges));
                
                // Highlight created faces if debug is enabled
                for (var face in createdFaces)
                {
                    debug(context, face, DebugColor.GREEN);
                }
            }
        }
        catch (error)
        {
            if (definition.showDebug)
            {
                println("ERROR in opCreateOutline: " ~ toString(error));
            }
            
            // Clean up intermediate bodies before rethrowing
            if (!definition.keepIntermediateBodies && intermediateBodiesToDelete != qNothing())
            {
                opDeleteBodies(context, id + "cleanupAfterError", {
                    "entities" : intermediateBodiesToDelete
                });
            }
            
            throw error;
        }

        // Clean up intermediate bodies if requested
        if (!definition.keepIntermediateBodies && intermediateBodiesToDelete != qNothing())
        {
            if (definition.showDebug)
            {
                const toDeleteCount = size(evaluateQuery(context, intermediateBodiesToDelete));
                println("Cleaning up " ~ toDeleteCount ~ " intermediate bodies");
            }
            
            opDeleteBodies(context, id + "cleanup", {
                "entities" : intermediateBodiesToDelete
            });
        }

        if (definition.showDebug)
        {
            println("=== opCreateOutline Test Feature END ===");
        }
    });
