FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2837.0");
import(path : "onshape/std/transform.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");

// Import font parsing utilities
// Note: In actual use, update these paths to point to your uploaded versions
// of base64Decoder.fs and fontParser.fs in your Onshape document
// import(path : "doc-id/base64Decoder.fs", version : "version");
// import(path : "doc-id/fontParser.fs", version : "version");

/**
 * Custom Font Text Feature
 * 
 * Creates text geometry using custom TTF or OTF font files that have been:
 * 1. Base64-encoded to text format
 * 2. Uploaded to Onshape as .txt files
 * 3. Imported using the import statement
 * 
 * WORKFLOW TO USE THIS FEATURE:
 * 
 * 1. Encode your font file to base64:
 *    - Linux/Mac: base64 MyFont.ttf > MyFont.txt
 *    - Windows: [Convert]::ToBase64String([IO.File]::ReadAllBytes("MyFont.ttf")) | Out-File MyFont.txt
 *    - Online: Use https://base64.guru/converter/encode/file
 * 
 * 2. Upload MyFont.txt to your Onshape document
 * 
 * 3. In your FeatureScript, import the font:
 *    myCustomFont::import(path : "doc-id/MyFont-element-id", version : "version-id");
 * 
 * 4. Pass myCustomFont::BLOB_DATA to this feature's "Font data" parameter
 * 
 * 5. Enter your text and configure appearance
 * 
 * EXAMPLE:
 *   // At the top of your FeatureScript file:
 *   myFont::import(path : "abc123.../def456...", version : "xyz789...");
 *   
 *   // Then use this feature and select myFont::BLOB_DATA as the font source
 */

annotation {
    "Feature Type Name" : "Custom Font Text",
    "Feature Type Description" : "Create text using custom TTF/OTF fonts imported as base64-encoded files"
}
export const customFontText = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Text to create" }
        definition.text is string;
        
        annotation { "Name" : "Font data (BLOB_DATA from imported font)",
                     "Description" : "Select the BLOB_DATA from your imported base64-encoded font file" }
        definition.fontData is map;
        
        annotation { "Group Name" : "Position and Size", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Placement", 
                         "Filter" : EntityType.FACE || BodyType.MATE_CONNECTOR,
                         "MaxNumberOfPicks" : 1 }
            definition.placement is Query;
            
            annotation { "Name" : "Text height (font size)" }
            isLength(definition.textHeight, LENGTH_BOUNDS);
            
            annotation { "Name" : "Horizontal position" }
            isLength(definition.horizontalPosition, LENGTH_BOUNDS);
            
            annotation { "Name" : "Vertical position" }
            isLength(definition.verticalPosition, LENGTH_BOUNDS);
            
            annotation { "Name" : "Character spacing multiplier", "Default" : 1.0 }
            isReal(definition.characterSpacing, POSITIVE_REAL_BOUNDS);
        }
        
        annotation { "Group Name" : "3D Options", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Create 3D text", "Default" : true }
            definition.create3D is boolean;
            
            if (definition.create3D)
            {
                annotation { "Name" : "Depth" }
                isLength(definition.extrusionDepth, LENGTH_BOUNDS);
                
                annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.oppositeDirection is boolean;
            }
        }
        
        annotation { "Group Name" : "Boolean Operation", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Operation", "Default" : CustomFontTextOperation.NEW }
            definition.operation is CustomFontTextOperation;
            
            if (definition.operation != CustomFontTextOperation.NEW)
            {
                annotation { "Name" : "Merge with", "Filter" : EntityType.BODY }
                definition.booleanScope is Query;
            }
        }
    }
    {
        // Parse the font file
        var fontData;
        try
        {
            // Decode base64 and parse font
            // const fontBytes = getBytesFromBlob(definition.fontData);
            // fontData = parseFont(fontBytes);
            
            // For now, throw helpful error since we need the import paths set up
            throw "Font parsing requires base64Decoder.fs and fontParser.fs to be imported. " ~
                  "Please update the import statements at the top of this file to point to " ~
                  "your uploaded versions of these utilities.";
        }
        catch (error)
        {
            throw "Failed to parse font: " ~ error;
        }
        
        // Get placement plane
        var placementPlane is Plane;
        
        if (size(evaluateQuery(context, definition.placement)) > 0)
        {
            const placement = evaluateQuery(context, definition.placement)[0];
            
            // Try as face first
            try
            {
                placementPlane = evFaceTangentPlane(context, {
                    "face" : definition.placement,
                    "parameter" : vector(0.5, 0.5)
                });
            }
            catch
            {
                // Try as mate connector
                try
                {
                    const mc = evMateConnector(context, {
                        "mateConnector" : definition.placement
                    });
                    placementPlane = mc.coordSystem;
                }
                catch
                {
                    // Default to XY plane
                    placementPlane = plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0));
                }
            }
        }
        else
        {
            // Default to XY plane at origin
            placementPlane = plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0));
        }
        
        // Create sketch for text geometry
        const textSketch = newSketchOnPlane(context, id + "textSketch", {
            "sketchPlane" : placementPlane
        });
        
        // Get font scaling factor (unitsPerEm is the design grid size)
        const unitsPerEm = fontData.head.unitsPerEm;
        const scaleFactor = definition.textHeight / (unitsPerEm * meter);
        
        // Current pen position
        var penX = definition.horizontalPosition;
        var penY = definition.verticalPosition;
        
        // Render each character
        const textLength = length(definition.text);
        
        for (var charIndex = 0; charIndex < textLength; charIndex += 1)
        {
            const character = definition.text[charIndex];
            
            // Skip spaces
            if (character == " ")
            {
                // Advance pen by space width (roughly 1/4 em)
                penX += (unitsPerEm / 4) * scaleFactor * definition.characterSpacing;
                continue;
            }
            
            // Get glyph index for character
            const glyphIndex = getGlyphIndex(fontData, character);
            
            if (glyphIndex == 0)
            {
                // Character not found in font, skip
                continue;
            }
            
            // Get glyph outline
            const glyphOutline = getGlyphOutline(fontData, glyphIndex);
            
            // Render glyph contours as sketch curves
            renderGlyphToSketch(textSketch, glyphOutline, penX, penY, scaleFactor, charIndex);
            
            // Advance pen position by glyph width
            const glyphWidth = (glyphOutline.xMax - glyphOutline.xMin) * scaleFactor;
            penX += glyphWidth * definition.characterSpacing;
        }
        
        // Solve the sketch
        skSolve(textSketch);
        
        // Extract geometry for 3D extrusion if requested
        if (definition.create3D)
        {
            // Get sketch regions
            const sketchRegions = qSketchRegion(id + "textSketch", true);
            
            if (!isQueryEmpty(context, sketchRegions))
            {
                // Extract surfaces from sketch regions
                opExtractSurface(context, id + "extractSurfaces", {
                    "faces" : sketchRegions,
                    "offset" : 0 * meter
                });
                
                const extractedBodies = qCreatedBy(id + "extractSurfaces", EntityType.BODY);
                const extractedFaces = qOwnedByBody(extractedBodies, EntityType.FACE);
                
                // Extrude to create solid text
                const direction = definition.oppositeDirection ? -1 : 1;
                
                opThicken(context, id + "thickenText", {
                    "entities" : extractedFaces,
                    "thickness1" : 0 * meter,
                    "thickness2" : direction * definition.extrusionDepth
                });
                
                const solidText = qCreatedBy(id + "thickenText", EntityType.BODY);
                
                // Perform boolean operation if requested
                if (definition.operation != CustomFontTextOperation.NEW && 
                    !isQueryEmpty(context, definition.booleanScope))
                {
                    var boolType = BooleanOperationType.UNION;
                    
                    if (definition.operation == CustomFontTextOperation.ADD)
                    {
                        boolType = BooleanOperationType.UNION;
                    }
                    else if (definition.operation == CustomFontTextOperation.SUBTRACT)
                    {
                        boolType = BooleanOperationType.SUBTRACTION;
                    }
                    else if (definition.operation == CustomFontTextOperation.INTERSECT)
                    {
                        boolType = BooleanOperationType.INTERSECTION;
                    }
                    
                    opBoolean(context, id + "booleanText", {
                        "tools" : solidText,
                        "targets" : definition.booleanScope,
                        "operationType" : boolType
                    });
                }
            }
        }
    });

/**
 * Enumeration of boolean operations for custom font text
 */
export enum CustomFontTextOperation
{
    annotation { "Name" : "New body" }
    NEW,
    annotation { "Name" : "Add" }
    ADD,
    annotation { "Name" : "Subtract" }
    SUBTRACT,
    annotation { "Name" : "Intersect" }
    INTERSECT
}

/**
 * Render a single glyph outline to a sketch
 * Converts TrueType quadratic Bezier curves to sketch splines
 * 
 * @param sketch : The sketch to add curves to
 * @param glyphOutline : Parsed glyph outline data
 * @param offsetX : Horizontal offset for glyph position
 * @param offsetY : Vertical offset for glyph position
 * @param scale : Scaling factor from font units to world units
 * @param glyphNumber : Index for unique curve IDs
 */
function renderGlyphToSketch(
    sketch is Sketch,
    glyphOutline is map,
    offsetX is ValueWithUnits,
    offsetY is ValueWithUnits,
    scale is number,
    glyphNumber is number)
{
    const contours = glyphOutline.contours;
    
    for (var contourIndex = 0; contourIndex < size(contours); contourIndex += 1)
    {
        const contour = contours[contourIndex];
        const numPoints = size(contour);
        
        if (numPoints < 2)
        {
            continue; // Skip invalid contours
        }
        
        // Build spline control points
        var splinePoints = [];
        
        for (var pointIndex = 0; pointIndex < numPoints; pointIndex += 1)
        {
            const point = contour[pointIndex];
            
            // Transform point to world coordinates
            const worldX = (point.x * scale * meter) + offsetX;
            const worldY = (point.y * scale * meter) + offsetY;
            
            splinePoints = append(splinePoints, vector(worldX, worldY));
        }
        
        // Close the contour by connecting back to first point
        const firstPoint = contour[0];
        const closeX = (firstPoint.x * scale * meter) + offsetX;
        const closeY = (firstPoint.y * scale * meter) + offsetY;
        splinePoints = append(splinePoints, vector(closeX, closeY));
        
        // Create a closed spline for this contour
        // Note: TrueType uses quadratic Bezier curves with on-curve and off-curve points
        // For simplicity, we're creating an interpolated spline
        // A more sophisticated implementation would convert quadratic Beziers to cubic
        
        try
        {
            skFitSpline(sketch, "glyph" ~ glyphNumber ~ "contour" ~ contourIndex, {
                "points" : splinePoints
            });
        }
        catch
        {
            // If spline creation fails, try polyline as fallback
            try
            {
                for (var i = 0; i < size(splinePoints) - 1; i += 1)
                {
                    skLineSegment(sketch, "glyph" ~ glyphNumber ~ "contour" ~ contourIndex ~ "seg" ~ i, {
                        "start" : splinePoints[i],
                        "end" : splinePoints[i + 1]
                    });
                }
            }
        }
    }
}

// Placeholder functions that would be imported from fontParser.fs
// In actual use, these would be imported properly

function getGlyphIndex(fontData is map, character is string) returns number
{
    // This would call the actual implementation from fontParser.fs
    throw "getGlyphIndex requires fontParser.fs to be properly imported";
}

function getGlyphOutline(fontData is map, glyphIndex is number) returns map
{
    // This would call the actual implementation from fontParser.fs
    throw "getGlyphOutline requires fontParser.fs to be properly imported";
}
