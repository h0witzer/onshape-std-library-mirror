# Custom Font Import Guide

## Overview
This guide explains how to work with custom fonts in Onshape FeatureScript features. While Onshape doesn't currently support direct TTF/OTF font file imports, there are practical workflows you can use today.

## Quick Answer: Can I Import TTF Files Like SVG?

**No**, TTF font files cannot simply be renamed to .txt and imported like SVG files because:

1. **TTF files are binary format** - They contain complex data structures for glyph outlines, metrics, and hinting
2. **SVG files are text format** - They're XML-based text files that can be read directly
3. **No font parser exists** - FeatureScript doesn't have built-in TTF/OTF parsing capabilities

## Current Options for Custom Fonts

### Option 1: Use Built-in Fonts (Simplest)

The `textPascoe.fs` feature and Onshape's native text features support these built-in fonts:

- OpenSans (Regular, Bold, Italic, BoldItalic)
- AllertaStencil
- Arimo
- DroidSansMono
- NotoSans (with CJK variants for Japanese, Korean, Simplified/Traditional Chinese)
- NotoSerif
- RobotoSlab
- Tinos

**Example usage:**
```featurescript
skText(sketch1, "text1", {
    "text" : "Hello World",
    "fontName" : "OpenSans-Bold.ttf",
    "firstCorner" : vector(0, 0) * inch,
    "secondCorner" : vector(1, 0.5) * inch
});
```

### Option 2: Convert Text to Vector Paths (Recommended for Custom Fonts)

This is the most practical approach when you need a specific font:

#### Step-by-Step Process:

**1. Create Your Text in a Vector Graphics Program**

Use any of these free or commercial tools:
- **Inkscape** (Free, Open Source) - Recommended
- Adobe Illustrator
- CorelDRAW
- Online tools like FontDrop or Text-to-SVG converters

**2. Convert Text to Paths**

In Inkscape:
1. Type your text using your custom font
2. Select the text object
3. Go to `Path > Object to Path` (or Ctrl+Shift+C)
4. This converts the text from a font reference to vector paths

In Illustrator:
1. Type your text
2. Select the text
3. Go to `Type > Create Outlines` (or Ctrl+Shift+O)

**3. Export the Paths**

You have two options:

**Option A: Export as DXF**
- File > Save As > DXF
- Import the DXF directly into an Onshape sketch
- Use sketch constraints to position it

**Option B: Export as SVG**
- File > Save As > Plain SVG
- This creates a text-based file you can import
- Note: Complex SVG features may not be fully supported

**4. Import into Onshape**

For DXF:
1. Create a sketch in Onshape
2. Click `Sketch > Import...`
3. Select your DXF file
4. Position and scale as needed

For SVG geometry:
1. Use the `customTextFromVectors.fs` feature (included in this repository)
2. Select the imported sketch geometry
3. Configure positioning, scaling, and extrusion options

**5. Create 3D Geometry**

- Extrude the imported curves to create solid text
- Use boolean operations to add/subtract from existing parts
- Apply patterns, mirrors, or other transformations

### Option 3: Create a Glyph Library (Advanced)

For repeated use of specific characters or logos:

1. Create parametric FeatureScript features for each glyph
2. Store them in a document or library
3. Compose text by instantiating and positioning glyphs
4. This gives full parametric control

**Example glyph feature structure:**
```featurescript
annotation { "Feature Type Name" : "Custom Letter A" }
export const customLetterA = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Height" }
        isLength(definition.height, LENGTH_BOUNDS);
        
        annotation { "Name" : "Position" }
        definition.position is Query;
    }
    {
        // Create the glyph geometry
        // This would contain the specific curve data for the letter
        // extracted from your font
    });
```

## Understanding Why Direct TTF Import Doesn't Work

### Technical Comparison

**SVG Files (Text-Based):**
```xml
<?xml version="1.0"?>
<svg xmlns="http://www.w3.org/2000/svg">
  <path d="M10 10 L90 90" stroke="black"/>
</svg>
```
- Human-readable XML
- Can be renamed to .txt without issue
- Already contains vector path data
- Easy to parse

**TTF Files (Binary Format):**
```
Binary data: 00 01 00 00 00 0F 00 80 00 03 00 70 4F 53 2F 32...
```
- Binary data structures
- Contains font tables (glyf, head, hhea, hmtx, etc.)
- Requires specialized parser
- Character mapping, hinting, kerning data
- Cannot be directly used as text

### What Would Be Required for TTF Support

For Onshape to support custom TTF/OTF fonts, they would need to:

1. **Add a font file parser** to FeatureScript to read binary font formats
2. **Implement glyph rendering** to convert font data to geometric curves
3. **Handle font encoding** (Unicode mappings, kerning tables)
4. **Support font formats** (TTF, OTF, WOFF, WOFF2)
5. **Manage licensing** (many fonts have usage restrictions)
6. **Provide a blob import mechanism** similar to images but for fonts

### Base64 Encoding Approach (Theoretical)

If Onshape added font support, the workflow might look like:

**1. Encode the font file:**
```bash
# On Linux/Mac
base64 MyCustomFont.ttf > MyCustomFont.txt

# On Windows PowerShell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("MyCustomFont.ttf")) | Out-File MyCustomFont.txt
```

**2. Import in FeatureScript:**
```featurescript
// Hypothetical future syntax
customFont::import(path : "document-id/version-id", version : "version");

skText(sketch1, "text1", {
    "text" : "Custom Font Text",
    "fontData" : customFont::BLOB_DATA,  // Hypothetical
    "firstCorner" : vector(0, 0) * inch,
    "secondCorner" : vector(1, 1) * inch
});
```

**3. Font parser would:**
- Decode the Base64 data
- Parse the TTF/OTF tables
- Extract glyph outlines
- Render text as FeatureScript curves

## Best Practices

### For Production Use
1. **Use built-in fonts** when possible for consistency
2. **Convert to paths** for one-off custom text
3. **Create templates** if you use the same custom text frequently
4. **Keep source files** of your vector text for future modifications

### For Parametric Models
1. **Use text variables** in FeatureScript for dynamic text
2. **Create custom features** that accept text parameters
3. **Build libraries** of commonly used text elements
4. **Document your workflow** for team consistency

### File Management
1. **Organize SVG/DXF files** in Onshape documents
2. **Version control** your text assets
3. **Name clearly** to indicate font and style used
4. **Keep original font files** for legal compliance

## Common Issues and Solutions

### Issue: Imported text looks distorted
**Solution:** Check the scale when importing. Use the scale factor parameter in the import dialog or in the customTextFromVectors feature.

### Issue: Characters are missing or incomplete
**Solution:** Ensure you converted text to paths before exporting. Some export formats don't preserve font information.

### Issue: Boolean operations fail with text
**Solution:** Make sure the text is fully closed curves. Check for self-intersecting paths and fix them in your vector editor.

### Issue: Text is too complex (many curves)
**Solution:** Simplify paths in your vector editor before exporting. Use fewer nodes while maintaining shape.

## Examples

### Example 1: Simple Extruded Text
```featurescript
// Import sketch with pre-converted text geometry
// Use customTextFromVectors feature:
// - Select sketch curves
// - Set scale to 1.0
// - Set extrusion depth to 0.25 inch
// - Operation: New
```

### Example 2: Debossed Text
```featurescript
// Import text geometry
// Use customTextFromVectors feature:
// - Select sketch curves
// - Set target plane to top face of part
// - Set extrusion depth to 0.05 inch
// - Set opposite direction: true
// - Operation: Subtract
// - Select target body
```

### Example 3: Multiple Lines
```featurescript
// Create text in Inkscape with multiple lines
// Convert each line to paths
// Export as DXF
// Import into Onshape sketch
// Extrude or use customTextFromVectors
```

## Legal Considerations

### Font Licensing
Most fonts have licensing restrictions:
- **Desktop licenses** may not allow embedding in 3D models
- **Web fonts** are typically restricted to web use
- **Commercial fonts** often require proper licensing for product use
- **Free fonts** may have restrictions on redistribution

**Best Practice:** 
- Use fonts you have proper licenses for
- Check font license before using in commercial products
- Consider open-source fonts (SIL Open Font License)
- Document which fonts you used for compliance

### Open-Source Fonts (Safe Options)
These are freely available with permissive licenses:
- **Google Fonts** (most are under SIL OFL)
- **Font Squirrel** (filtered by license)
- **League of Moveable Type**
- **Adobe Source fonts** (Source Sans, Source Serif, Source Code)

## Resources

### Tools
- [Inkscape](https://inkscape.org/) - Free vector graphics editor
- [FontForge](https://fontforge.org/) - Free font editor
- [Font Squirrel](https://www.fontsquirrel.com/) - Free fonts with clear licenses

### Documentation
- [Onshape FeatureScript Documentation](https://cad.onshape.com/FsDoc/)
- [skText Function](https://cad.onshape.com/FsDoc/library.html#skText-Sketch-string-map)
- [SVG Specification](https://www.w3.org/TR/SVG2/)

### Community
- [Onshape Forum](https://forum.onshape.com/) - Ask questions and share solutions
- [FeatureScript GitHub Repositories](https://github.com/topics/featurescript) - Example code

## Summary

While you **cannot directly import TTF font files as text** like SVG files, you can achieve custom font text in Onshape by:

1. Converting your text to vector paths in an external tool
2. Importing those paths as DXF or SVG geometry
3. Using the geometry as sketch curves or with features like customTextFromVectors

This workflow gives you complete control over text appearance with any font while working within Onshape's current capabilities.

For feedback or feature requests regarding native custom font support, contact Onshape support or participate in community discussions on the Onshape forum.
