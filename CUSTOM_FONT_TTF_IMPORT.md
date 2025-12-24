# Custom Font Support - TTF/OTF Import Guide

## Direct Answer to: "Can TTF files be saved as TXT and imported like SVG?"

**YES**, TTF/OTF font files CAN be imported into Onshape FeatureScript by encoding them as base64 text, similar to how SVG files work, but with an encoding step.

**The key difference from SVG:**
- SVG files are already text (XML), so they can be renamed `.svg` → `.txt` directly
- TTF/OTF files are binary, so they must be **base64-encoded** first, then saved as `.txt`

This repository provides a complete implementation for importing and using custom fonts in Onshape.

## The Key Difference

### SVG Files
- Already text-based (XML)
- Can be renamed from `.svg` to `.txt` directly
- No encoding needed

### TTF/OTF Font Files  
- Binary format
- **MUST be base64-encoded first** before saving as `.txt`
- Can then be imported as blob data
- Requires a font parser in FeatureScript to render

## How to Import TTF/OTF Fonts

### Step 1: Encode Your Font File

The easiest way is to use the browser-based converter (no installation needed):

**Method 1: Browser Tool (Recommended - No Installation)**
1. Open `web-tool/font-converter.html` from this repository in your browser
2. Drag and drop your TTF/OTF file onto the page
3. Click "Download as .txt file"
4. Done! You now have a `.txt` file ready for Onshape

**Method 2: Online Converter (No Download)**
- Visit: https://base64.guru/converter/encode/file
- Upload your TTF/OTF file
- Download the base64-encoded text
- Save as `.txt` file

**Method 3: Command Line (For Advanced Users)**

*Only use this if you're comfortable with terminal/command prompt:*

**On Linux/Mac:**
```bash
base64 -w 76 MyCustomFont.ttf > MyCustomFont.txt
```

**On Windows PowerShell:**
```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("MyCustomFont.ttf")) | Out-File MyCustomFont.txt -Encoding ASCII
```

**Using Python:**
```python
import base64
with open('MyCustomFont.ttf', 'rb') as f:
    encoded = base64.b64encode(f.read())
with open('MyCustomFont.txt', 'w') as f:
    f.write(encoded.decode('ascii'))
```

### Step 2: Upload to Onshape

1. In your Onshape document, click the `+` button
2. Select "Upload"
3. Upload your `MyCustomFont.txt` file
4. The file will appear in your document tabs

### Step 3: Import in FeatureScript

```featurescript
// Import the base64-encoded font data
customFont::import(path : "document-id/element-id", version : "version-id");

// The font data is now available as customFont::BLOB_DATA
```

## Implementation: Font Text Feature

See the `customFontText.fs` file in this repository for a complete implementation that:

1. Imports base64-encoded font files as blob data
2. Decodes the base64 data back to binary TTF/OTF
3. Parses the font file structure (TrueType/OpenType tables)
4. Extracts glyph outlines for specified text
5. Renders the glyphs as FeatureScript curves
6. Creates 3D text geometry

## Font File Format Basics

### TrueType/OpenType Structure

A TTF/OTF file contains tables with specific data:

```
Offset Table (File Header)
├── sfnt version
├── numTables
├── searchRange
├── entrySelector
└── rangeShift

Table Directory
├── 'cmap' - Character to glyph mapping
├── 'glyf' - Glyph outlines (TrueType)
├── 'CFF ' - Compact Font Format (OpenType)
├── 'head' - Font header
├── 'hhea' - Horizontal header
├── 'hmtx' - Horizontal metrics
├── 'loca' - Index to location
├── 'maxp' - Maximum profile
├── 'name' - Font naming
├── 'post' - PostScript information
└── ...
```

### Glyph Outline Data

- **Simple Glyphs**: Series of on-curve and off-curve points
- **Compound Glyphs**: References to other glyphs with transforms
- **Quadratic Bezier Curves**: TrueType uses quadratic curves
- **Cubic Bezier Curves**: OpenType CFF uses cubic curves

## Example Usage

```featurescript
FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

// Import your custom font (after encoding to base64)
myFont::import(path : "your-doc-id/your-font-txt-id", version : "version");

annotation { "Feature Type Name" : "My Custom Text" }
export const myCustomText = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Text" }
        definition.text is string;
        
        annotation { "Name" : "Font size" }
        isLength(definition.fontSize, LENGTH_BOUNDS);
        
        annotation { "Name" : "Location", "Filter" : EntityType.FACE }
        definition.location is Query;
    }
    {
        // Use the custom font
        const fontData = myFont::BLOB_DATA;
        
        // Parse and render (see customFontText.fs for full implementation)
        renderCustomFontText(context, id, {
            "text" : definition.text,
            "fontData" : fontData,
            "fontSize" : definition.fontSize,
            "location" : definition.location
        });
    });
```

## Key Advantages

1. **No preprocessing required** - Just encode and upload
2. **Same workflow as SVG** - Import as blob data
3. **Full font support** - Access all glyphs, kerning, ligatures
4. **Parametric** - Text remains editable in FeatureScript
5. **Any font** - Works with any TTF or OTF file

## Limitations and Considerations

### Performance
- Font parsing can be computationally intensive
- Large fonts (with many glyphs) will be slower
- Consider caching parsed font data

### File Size
- Base64 encoding increases file size by ~33%
- Typical font file: 50-500 KB
- Encoded: 66-660 KB
- Onshape has upload size limits

### Font Licensing
- Ensure you have rights to use the font
- Check font license for embedding permissions
- Some fonts prohibit embedding in software

### Complexity
- Font parsing is non-trivial
- TrueType and OpenType have different formats
- Hinting and advanced features may not render perfectly

## Technical Implementation Details

### Base64 Decoding in FeatureScript

```featurescript
/**
 * Decode base64 string to binary byte array
 * @param base64String : Base64-encoded string
 * @returns {array} : Array of byte values (0-255)
 */
function decodeBase64(base64String is string) returns array
{
    const base64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    var bytes = [];
    
    // Implementation of base64 decoding
    // See customFontText.fs for complete code
    
    return bytes;
}
```

### Reading Font Tables

```featurescript
/**
 * Parse TTF/OTF file structure
 * @param fontBytes : Array of byte values
 * @returns {map} : Font data including tables
 */
function parseFontFile(fontBytes is array) returns map
{
    // Read offset table
    const sfntVersion = readUInt32(fontBytes, 0);
    const numTables = readUInt16(fontBytes, 4);
    
    // Read table directory
    var tables = {};
    for (var i = 0; i < numTables; i += 1)
    {
        const offset = 12 + (i * 16);
        const tag = readTag(fontBytes, offset);
        const checksum = readUInt32(fontBytes, offset + 4);
        const tableOffset = readUInt32(fontBytes, offset + 8);
        const tableLength = readUInt32(fontBytes, offset + 12);
        
        tables[tag] = {
            "offset" : tableOffset,
            "length" : tableLength
        };
    }
    
    return { "tables" : tables };
}
```

### Converting Glyphs to Curves

```featurescript
/**
 * Extract glyph outline and convert to FeatureScript curves
 * @param fontData : Parsed font data
 * @param glyphId : ID of glyph to extract
 * @returns {array} : Array of curve definitions
 */
function extractGlyphCurves(fontData is map, glyphId is number) returns array
{
    // Read glyph data from 'glyf' table
    // Parse contours and points
    // Convert to spline/bezier curves
    // Return array of curve definitions
    
    return curves;
}
```

## Comparison with Existing Solutions

### Built-in Fonts (textPascoe.fs)
- **Pros**: Simple, fast, pre-integrated
- **Cons**: Limited font selection (10 fonts)

### SVG/DXF Preprocessing
- **Pros**: Simple conversion, widely supported
- **Cons**: Not parametric, requires external tools, static geometry

### Base64-Encoded TTF/OTF (This Solution)
- **Pros**: Direct font import, fully parametric, any font
- **Cons**: Requires encoding step, more complex parsing

## Getting Started

1. Choose your custom font (ensure you have proper license)
2. Encode it to base64 using one of the methods above
3. Upload the `.txt` file to your Onshape document
4. Use the `customFontText.fs` feature to render text
5. Enjoy your custom fonts in Onshape!

## References

- TrueType Font Specification: https://developer.apple.com/fonts/TrueType-Reference-Manual/
- OpenType Specification: https://docs.microsoft.com/en-us/typography/opentype/spec/
- Base64 Encoding: https://en.wikipedia.org/wiki/Base64
- FeatureScript Documentation: https://cad.onshape.com/FsDoc/

## See Also

- `customFontText.fs` - Complete implementation
- `fontParser.fs` - Font parsing utilities
- `base64Decoder.fs` - Base64 decoding utilities
