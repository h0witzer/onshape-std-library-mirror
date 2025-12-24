# Font Converter Web Tool

A simple, browser-based tool to convert TTF/OTF font files to text format for use in Onshape FeatureScript.

## How to Use

### Option 1: Use the Local HTML File (Recommended)

1. **Download** `font-converter.html` from this folder
2. **Open** it in any web browser (Chrome, Firefox, Safari, Edge)
3. **Drag and drop** your TTF or OTF font file onto the page
4. **Click** "Download as .txt file"
5. **Upload** the .txt file to your Onshape document

### Option 2: Use an Online Converter

If you prefer not to download the HTML file, use any of these online converters:

- **Base64.guru**: https://base64.guru/converter/encode/file
  - Upload your font file
  - Download the base64 result
  - Save as `.txt` file

- **Base64 Encode**: https://www.base64encode.org/
  - Upload your font file  
  - Download the encoded text
  - Save as `.txt` file

## Why Conversion is Needed

- **SVG files** are already text (XML format) → can be renamed `.svg` to `.txt` directly
- **TTF/OTF files** are binary format → must be converted to text first

Onshape only accepts `.txt` or `.csv` files for imports, so binary font files need to be encoded as text using base64.

## Features

✅ **No installation required** - Just open the HTML file in your browser  
✅ **100% local** - No data uploaded to any server  
✅ **Drag and drop** - Simple, intuitive interface  
✅ **Instant conversion** - Processes fonts in seconds  
✅ **Cross-platform** - Works on Windows, Mac, Linux  
✅ **Mobile friendly** - Works on tablets and phones  

## Technical Details

The tool converts binary font data to base64 text format with line breaks every 76 characters (MIME standard). This is the same format produced by command-line tools like:

```bash
base64 -w 76 font.ttf > font.txt  # Linux/Mac
```

But requires no command-line knowledge or installation.

## Next Steps

After converting your font:

1. Upload the `.txt` file to your Onshape document
2. Import it in FeatureScript:
   ```featurescript
   myFont::import(path : "doc-id/element-id", version : "version");
   ```
3. Use it with the `customFontText` feature

See the parent directory's documentation for complete usage instructions.

## Troubleshooting

**Q: The page isn't working**  
A: Make sure JavaScript is enabled in your browser. The tool requires JavaScript to work.

**Q: Can I convert multiple fonts at once?**  
A: The current version processes one font at a time. Simply convert each font individually.

**Q: Is my font file secure?**  
A: Yes! All processing happens entirely in your browser. No data is sent to any server.

**Q: What browsers are supported?**  
A: Any modern browser from the last 5 years. Chrome, Firefox, Safari, and Edge all work perfectly.

## File Size Considerations

Base64 encoding increases file size by approximately 33%:
- Original: 100 KB font file
- Encoded: ~133 KB text file

This is normal and expected. Onshape handles these files without issues.
