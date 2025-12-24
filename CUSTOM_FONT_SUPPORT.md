# Custom Font Support for Text Features in Onshape FeatureScript

## Problem Statement

Users on the Onshape forums have requested the ability to use custom fonts (TTF/OTF files) in text-based features. The question is whether TTF files can be saved as TXT and imported into FeatureScript, similar to how SVG files can be handled.

## Understanding the Technical Context

### How SVG Import Works
SVG (Scalable Vector Graphics) files are **already text-based** XML files. When someone saves an SVG as ".txt", they're not performing any encoding - they're simply renaming a text file. The file contents remain valid XML/text that can be parsed.

Example SVG content:
```xml
<?xml version="1.0"?>
<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
  <circle cx="50" cy="50" r="40" fill="blue" />
</svg>
```

### How TTF/OTF Files Work
TTF (TrueType Font) and OTF (OpenType Font) files are **binary format files** containing:
- Glyph outlines (bezier curves, splines)
- Font metrics (kerning, spacing)
- Metadata (font name, copyright)
- Hinting information
- Multiple character mappings

These are NOT text files and cannot simply be renamed to ".txt" to work.

## Why TTF Cannot Be Directly Imported Like SVG

### Key Differences:
1. **Binary vs Text**: TTF/OTF files are binary, SVG files are text
2. **Encoding Required**: Binary data needs encoding (like Base64) to become text
3. **Size**: Encoded binary fonts become very large text files
4. **Parsing**: FeatureScript would need a complete font parser to read TTF/OTF format

## Current Solution: Built-in Fonts

The existing Onshape text features (like `textPascoe.fs`) use the `skText()` function which accepts a `fontName` parameter referencing built-in fonts:

```featurescript
skText(sketch1, "text1", {
    "text" : "Hello World",
    "fontName" : "OpenSans-Regular.ttf",
    "firstCorner" : vector(0, 0) * inch,
    "secondCorner" : vector(1, 1) * inch
});
```

Available built-in fonts include:
- OpenSans
- AllertaStencil
- Arimo
- DroidSansMono
- NotoSans (including CJK variants)
- NotoSerif
- RobotoSlab
- Tinos

Each with variants: Regular, Bold, Italic, BoldItalic

## Workaround: Converting Fonts to Geometry

While custom TTF files cannot be directly imported, there are practical workarounds:

### Option 1: Font to SVG Conversion (Recommended)
1. Use external tools to convert your text with custom font to SVG paths
2. Import the SVG geometry into Onshape
3. Use the SVG paths directly as sketch geometry

**Tools for conversion:**
- Inkscape: Type text, then "Path > Object to Path"
- Adobe Illustrator: "Type > Create Outlines"
- Online converters: text-to-svg tools

### Option 2: Create Custom Geometry Library
Create a library of custom glyphs as parametric FeatureScript features that can be instantiated and composed to form text.

### Option 3: Base64-Encoded Font Data (Advanced)

If Onshape FeatureScript were to support custom fonts through blob imports, it would require:

1. **Encoding the font file:**
```bash
base64 MyCustomFont.ttf > MyCustomFont.txt
```

2. **Importing in FeatureScript:**
```featurescript
fontData::import(path : "document-id/MyCustomFont.txt", version : "version-id");
```

3. **A font parser in FeatureScript** to decode and render the font (not currently available)

## Current Best Practice

**For users wanting custom fonts NOW:**

1. **Design your text externally** using your custom font in:
   - Inkscape (free, open-source)
   - Adobe Illustrator
   - Online text-to-vector tools

2. **Convert text to vector paths** (not text objects)

3. **Export as DXF or import as SVG**:
   - DXF: Import directly into Onshape sketch
   - SVG: May need additional processing depending on complexity

4. **Use in your Onshape model** as sketch geometry

## Future Possibilities

For Onshape to support custom fonts natively, they would need to:

1. Add a font parser to FeatureScript
2. Support font blob imports (similar to image blobs)
3. Extend `skText()` to accept custom font blob data
4. Handle font licensing and legal considerations

## Conclusion

**Answer to the original question:**

**No**, TTF files cannot simply be saved as TXT and imported like SVG files because:
- TTF files are binary, not text
- SVG files are already text-based XML
- Font files require specialized parsing that FeatureScript doesn't currently support

**Recommended approach:**
Convert your text to vector paths using external tools, then import the geometry into Onshape.

## Implementation Example

See the companion file `customTextFromSVG.fs` for an example feature that demonstrates the recommended workflow for importing custom text geometry.

## References

- [Onshape FeatureScript Documentation](https://cad.onshape.com/FsDoc/)
- [skText Function Reference](https://cad.onshape.com/FsDoc/library.html#skText-Sketch-string-map)
- [SVG Specification](https://www.w3.org/TR/SVG2/)
- [TrueType Font File Format](https://docs.microsoft.com/en-us/typography/opentype/spec/)
