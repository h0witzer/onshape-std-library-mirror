# Complete Example: Using Custom Fonts in Onshape

This is a step-by-step walkthrough showing exactly how to use custom TTF/OTF fonts in your Onshape FeatureScript features.

## Prerequisites

- An Onshape account
- A custom font file (TTF or OTF)
- A web browser (for the conversion tool)

## Step 1: Convert Your Font File

**Easiest Method: Browser Tool**

1. Open `web-tool/font-converter.html` from this repository
   - Just double-click it, or right-click and "Open With" your browser
   - Works offline - no internet needed!

2. Drag and drop your font file (e.g., `MyAwesomeFont-Regular.ttf`) onto the page
   - Or click the box to browse for your file

3. Click "Download as .txt file"

**Result:**
```
✓ Conversion Complete!

Original: MyAwesomeFont-Regular.ttf (156.8 KB)
Encoded: MyAwesomeFont-Regular.txt (209.1 KB)

[Download as .txt file]
```

**Alternative: Online Converter**

If you prefer not to download the HTML file:
1. Go to https://base64.guru/converter/encode/file
2. Upload your font file
3. Download the result
4. Save as `.txt` file

## Step 2: Upload to Onshape

1. Open your Onshape document (or create a new one)
2. Click the **+** button in the bottom-left tabs panel
3. Select **Upload**
4. Choose `MyAwesomeFont-Regular.txt`
5. Wait for upload to complete

## Step 3: Get Import Path Information

1. Right-click on the uploaded `MyAwesomeFont-Regular.txt` tab
2. Select **Copy link** or **Copy ID** (exact option varies)
3. The URL will look like: `https://cad.onshape.com/documents/{docId}/w/{workspaceId}/e/{elementId}`
4. Note down the `docId` and `elementId`

## Step 4: Create or Update Your FeatureScript

### Option A: Using the Provided customFontText Feature

1. Create a new Feature Studio in your document
2. Upload or copy the following files to your document:
   - `base64Decoder.fs`
   - `fontParser.fs`
   - `customFontText.fs`

3. In a new Feature Studio, create this code:

```featurescript
FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

// Import the uploaded font file (REPLACE THESE IDS)
myAwesomeFont::import(path : "YOUR-DOC-ID/YOUR-ELEMENT-ID", version : "YOUR-VERSION-ID");

// Import the custom font utilities (REPLACE THESE IDS WITH YOUR UPLOADED FILES)
import(path : "YOUR-DOC-ID/base64Decoder-element-id", version : "version-id");
import(path : "YOUR-DOC-ID/fontParser-element-id", version : "version-id");
import(path : "YOUR-DOC-ID/customFontText-element-id", version : "version-id");

annotation { "Feature Type Name" : "My Custom Text" }
export const myCustomText = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Text to create" }
        definition.text is string;
        
        annotation { "Name" : "Face or plane", "Filter" : EntityType.FACE }
        definition.placement is Query;
        
        annotation { "Name" : "Text height" }
        isLength(definition.textHeight, LENGTH_BOUNDS);
        
        annotation { "Name" : "Extrusion depth" }
        isLength(definition.depth, LENGTH_BOUNDS);
    }
    {
        // Call the custom font text feature with your font
        customFontText(context, id, {
            "text" : definition.text,
            "fontData" : myAwesomeFont::BLOB_DATA,
            "placement" : definition.placement,
            "textHeight" : definition.textHeight,
            "horizontalPosition" : 0 * inch,
            "verticalPosition" : 0 * inch,
            "characterSpacing" : 1.0,
            "create3D" : true,
            "extrusionDepth" : definition.depth,
            "oppositeDirection" : false,
            "operation" : CustomFontTextOperation.NEW
        });
    });
```

### Option B: Inline Implementation (Simpler)

For a simpler approach that doesn't require separate files:

```featurescript
FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

// Import your encoded font
myFont::import(path : "YOUR-DOC-ID/YOUR-ELEMENT-ID", version : "version-id");

annotation { "Feature Type Name" : "Simple Custom Text" }
export const simpleCustomText = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Text" }
        definition.text is string;
        
        annotation { "Name" : "Placement", "Filter" : EntityType.FACE }
        definition.face is Query;
    }
    {
        // For initial version, you can use the textPascoe.fs approach
        // but with custom font import path when Onshape supports it
        
        // This is placeholder - the actual implementation requires
        // the full font parsing infrastructure
        
        const plane = evFaceTangentPlane(context, {
            "face" : definition.face,
            "parameter" : vector(0.5, 0.5)
        });
        
        const sketch = newSketchOnPlane(context, id + "sketch", {
            "sketchPlane" : plane
        });
        
        // NOTE: This is where you'd decode myFont::BLOB_DATA
        // parse the font, extract glyphs, and render them
        // See customFontText.fs for the complete implementation
        
        skSolve(sketch);
    });
```

## Step 5: Use Your Feature

1. Insert a cube or other geometry in your Part Studio
2. Add your custom text feature
3. Enter text: "Hello World"
4. Select the top face of the cube
5. Set text height: 10 mm
6. Set extrusion depth: 2 mm
7. Click ✓ (checkmark) to create

## Step 6: Verify the Result

Your custom font text should now appear on the selected face, extruded to create 3D geometry!

## Troubleshooting

### Error: "Failed to parse font"
- Verify the font file is a valid TTF or OTF
- Check that the encoding script completed successfully
- Ensure the `.txt` file was uploaded correctly

### Error: "BLOB_DATA not found"
- Double-check your import path (doc ID and element ID)
- Make sure you're using the correct version ID
- Try refreshing the Feature Studio

### Text doesn't appear
- Check that the placement face is valid
- Verify text height is appropriate for the model scale
- Look in the console for error messages

### Characters are missing
- Not all characters may be in the font
- Check that the font supports the characters you're using
- Try a different font to isolate the issue

## Performance Considerations

- **Large fonts**: Fonts with many glyphs (like CJK fonts) will be slower to parse
- **Long text**: More characters = more geometry = slower regeneration
- **Font caching**: The first use of a font will be slowest; subsequent uses may be faster if caching is implemented

## Alternative: Using Built-in Fonts

If custom font support proves too complex, remember that Onshape provides several built-in fonts through the `textPascoe.fs` approach:

```featurescript
skText(sketch, "text1", {
    "text" : "Hello World",
    "fontName" : "OpenSans-Bold.ttf",
    "firstCorner" : vector(0, 0) * inch,
    "secondCorner" : vector(2, 0.5) * inch
});
```

Available fonts: OpenSans, AllertaStencil, Arimo, DroidSansMono, NotoSans, NotoSerif, RobotoSlab, Tinos

## Example Fonts to Try

### Free Commercial-Use Fonts:

1. **Roboto** (Google Fonts)
   - Modern, clean sans-serif
   - Download: https://fonts.google.com/specimen/Roboto

2. **Source Sans Pro** (Adobe)
   - Professional sans-serif
   - Download: https://github.com/adobe-fonts/source-sans-pro

3. **Montserrat** (Google Fonts)
   - Geometric sans-serif
   - Download: https://fonts.google.com/specimen/Montserrat

4. **Lato** (Google Fonts)
   - Humanist sans-serif
   - Download: https://fonts.google.com/specimen/Lato

## Next Steps

- Experiment with different fonts
- Try different text sizes and extrusion depths
- Combine custom text with other features
- Share your creations with the Onshape community!

## Need Help?

- Check the [CUSTOM_FONT_TTF_IMPORT.md](CUSTOM_FONT_TTF_IMPORT.md) guide
- Review the source code in `custom-features/`
- Ask questions on the Onshape Forum
- Open an issue on GitHub

## Legal Note

Always ensure you have the rights to use fonts in your models, especially for commercial purposes. Most commercial fonts require specific licensing for embedding in software or products.
