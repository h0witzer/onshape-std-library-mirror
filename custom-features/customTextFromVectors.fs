FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/extrude.fs", version : "2837.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2837.0");

/**
 * Custom Text from Vector Geometry
 * 
 * This feature demonstrates the recommended workflow for using custom fonts in Onshape.
 * Since TTF/OTF files cannot be directly imported, this feature provides a framework
 * for importing pre-converted text geometry from SVG or DXF sources.
 * 
 * WORKFLOW:
 * 1. Design your text in an external tool (Inkscape, Illustrator, etc.) with your custom font
 * 2. Convert text to vector paths ("Object to Path" in Inkscape or "Create Outlines" in Illustrator)
 * 3. Export as SVG or DXF
 * 4. Import into Onshape:
 *    - For DXF: Import directly into a sketch
 *    - For SVG: Use this feature to reference the imported sketch geometry
 * 5. Extrude or use as needed
 * 
 * This feature provides additional utilities for working with imported text geometry:
 * - Position and scale the text
 * - Extrude to create 3D text
 * - Boolean operations with existing geometry
 * - Mirroring and transformations
 */

annotation {
    "Feature Type Name" : "Custom Text from Vectors",
    "Feature Type Description" : "Import and manipulate custom text geometry from SVG/DXF with custom fonts"
}
export const customTextFromVectors = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sketch or curves containing text geometry", 
                     "Filter" : EntityType.EDGE || EntityType.FACE,
                     "MaxNumberOfPicks" : 1000 }
        definition.textGeometry is Query;
        
        annotation { "Name" : "Operation", "Default" : CustomTextOperation.NEW }
        definition.operation is CustomTextOperation;
        
        annotation { "Group Name" : "Positioning", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Reference plane or face", 
                         "Filter" : EntityType.FACE || BodyType.MATE_CONNECTOR,
                         "MaxNumberOfPicks" : 1 }
            definition.targetPlane is Query;
            
            annotation { "Name" : "Scale factor", "Default" : 1.0 }
            isReal(definition.scaleFactor, POSITIVE_REAL_BOUNDS);
            
            annotation { "Name" : "Horizontal offset" }
            isLength(definition.horizontalOffset, LENGTH_BOUNDS);
            
            annotation { "Name" : "Vertical offset" }
            isLength(definition.verticalOffset, LENGTH_BOUNDS);
            
            annotation { "Name" : "Mirror horizontally" }
            definition.mirrorHorizontal is boolean;
            
            annotation { "Name" : "Mirror vertically" }
            definition.mirrorVertical is boolean;
        }
        
        annotation { "Group Name" : "3D Options", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Create 3D text", "Default" : true }
            definition.create3D is boolean;
            
            if (definition.create3D)
            {
                annotation { "Name" : "Extrusion depth" }
                isLength(definition.extrusionDepth, LENGTH_BOUNDS);
                
                annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.oppositeDirection is boolean;
            }
        }
        
        if (definition.operation != CustomTextOperation.NEW)
        {
            annotation { "Name" : "Merge with", "Filter" : EntityType.BODY }
            definition.booleanScope is Query;
        }
    }
    {
        // Evaluate the target plane for positioning
        var targetPlane is Plane;
        
        if (size(evaluateQuery(context, definition.targetPlane)) > 0)
        {
            try
            {
                // Try to get plane from face
                targetPlane = evFaceTangentPlane(context, {
                    "face" : definition.targetPlane,
                    "parameter" : vector(0.5, 0.5)
                });
            }
            catch
            {
                try
                {
                    // Try to get plane from mate connector
                    targetPlane = evMateConnector(context, {
                        "mateConnector" : definition.targetPlane
                    }).coordSystem;
                }
                catch
                {
                    // Default to XY plane
                    targetPlane = plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0));
                }
            }
        }
        else
        {
            // Default to XY plane at origin
            targetPlane = plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0));
        }
        
        // Create a copy of the text geometry for transformation
        opPattern(context, id + "copyText", {
            "entities" : definition.textGeometry,
            "transforms" : [identityTransform()],
            "instanceNames" : ["text"]
        });
        
        var textBodies = qCreatedBy(id + "copyText", EntityType.BODY);
        
        // Apply transformations
        var transformMatrix = identityTransform();
        
        // Apply scale if not 1.0
        if (abs(definition.scaleFactor - 1.0) > TOLERANCE.zeroLength)
        {
            transformMatrix = transformMatrix * scaleNonuniformly(
                definition.scaleFactor,
                definition.scaleFactor,
                definition.scaleFactor
            );
        }
        
        // Apply mirroring
        if (definition.mirrorHorizontal)
        {
            transformMatrix = transformMatrix * scaleNonuniformly(-1, 1, 1);
        }
        
        if (definition.mirrorVertical)
        {
            transformMatrix = transformMatrix * scaleNonuniformly(1, -1, 1);
        }
        
        // Apply offsets
        transformMatrix = transformMatrix * transform(
            vector(definition.horizontalOffset, definition.verticalOffset, 0 * meter)
        );
        
        // Transform to target plane
        transformMatrix = transformMatrix * toWorld(targetPlane);
        
        opTransform(context, id + "transformText", {
            "bodies" : textBodies,
            "transform" : transformMatrix
        });
        
        // Create 3D text if requested
        if (definition.create3D)
        {
            // Extract surfaces if dealing with sketch regions
            var facesToExtrude = qOwnedByBody(textBodies, EntityType.FACE);
            
            if (!isQueryEmpty(context, facesToExtrude))
            {
                opExtractSurface(context, id + "extractSurfaces", {
                    "faces" : facesToExtrude
                });
                
                var surfaceBodies = qCreatedBy(id + "extractSurfaces", EntityType.BODY);
                var surfaceFaces = qOwnedByBody(surfaceBodies, EntityType.FACE);
                
                // Extrude the surfaces to create solid text
                const extrudeDirection = definition.oppositeDirection ? -1 : 1;
                
                opThicken(context, id + "thickenText", {
                    "entities" : surfaceFaces,
                    "thickness1" : 0 * meter,
                    "thickness2" : extrudeDirection * definition.extrusionDepth
                });
                
                var solidTextBodies = qCreatedBy(id + "thickenText", EntityType.BODY);
                
                // Perform boolean operation if requested
                if (definition.operation != CustomTextOperation.NEW && 
                    !isQueryEmpty(context, definition.booleanScope))
                {
                    var booleanType = BooleanOperationType.UNION;
                    
                    if (definition.operation == CustomTextOperation.ADD)
                    {
                        booleanType = BooleanOperationType.UNION;
                    }
                    else if (definition.operation == CustomTextOperation.SUBTRACT)
                    {
                        booleanType = BooleanOperationType.SUBTRACTION;
                    }
                    else if (definition.operation == CustomTextOperation.INTERSECT)
                    {
                        booleanType = BooleanOperationType.INTERSECTION;
                    }
                    
                    opBoolean(context, id + "booleanText", {
                        "tools" : solidTextBodies,
                        "targets" : definition.booleanScope,
                        "operationType" : booleanType
                    });
                }
            }
        }
        
        // Clean up temporary geometry if needed
        if (definition.create3D)
        {
            try
            {
                opDeleteBodies(context, id + "cleanup", {
                    "entities" : qSubtraction(textBodies, qBodyType(textBodies, BodyType.SOLID))
                });
            }
        }
    });

/**
 * Enumeration of operations that can be performed with custom text
 */
export enum CustomTextOperation
{
    annotation { "Name" : "New" }
    NEW,
    annotation { "Name" : "Add" }
    ADD,
    annotation { "Name" : "Subtract" }
    SUBTRACT,
    annotation { "Name" : "Intersect" }
    INTERSECT
}
