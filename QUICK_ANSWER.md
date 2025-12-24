# Quick Answer: Can TTF Fonts Be Imported Like SVG Files?

## Short Answer

**YES**, but with one additional step:

- **SVG files**: Can be renamed `.svg` → `.txt` and imported directly (they're already text)
- **TTF/OTF files**: Must be **base64-encoded first**, then saved as `.txt` and imported

## The One-Line Solution

```bash
base64 MyFont.ttf > MyFont.txt
```

Then upload `MyFont.txt` to Onshape and import it like any other resource.

## Why This Works

Both approaches use Onshape's blob import mechanism:

```featurescript
// SVG (text-based, no encoding needed)
mySvg::import(path : "doc-id/file.txt", version : "version");

// Font (binary, base64-encoded to text)
myFont::import(path : "doc-id/font.txt", version : "version");
```

## Complete Workflow

1. **Encode your font:**
   ```bash
   base64 MyFont.ttf > MyFont.txt
   ```

2. **Upload `MyFont.txt` to Onshape**

3. **Import in FeatureScript:**
   ```featurescript
   myFont::import(path : "doc-id/element-id", version : "version");
   ```

4. **Use the font data:**
   ```featurescript
   const fontData = myFont::BLOB_DATA;
   // Parse and render (see customFontText.fs for full implementation)
   ```

## What You Get

This repository provides:

- ✅ **Base64 encoding scripts** (Bash, PowerShell, Python) - [scripts/](scripts/)
- ✅ **Font parser** for TTF/OTF files - [custom-features/fontParser.fs](custom-features/fontParser.fs)
- ✅ **Base64 decoder** for FeatureScript - [custom-features/base64Decoder.fs](custom-features/base64Decoder.fs)
- ✅ **Complete text feature** using custom fonts - [custom-features/customFontText.fs](custom-features/customFontText.fs)
- ✅ **Full documentation** - [CUSTOM_FONT_TTF_IMPORT.md](CUSTOM_FONT_TTF_IMPORT.md)
- ✅ **Usage examples** - [EXAMPLE_USAGE.md](EXAMPLE_USAGE.md)

## Key Differences from SVG

| Aspect | SVG Files | TTF/OTF Files |
|--------|-----------|---------------|
| Format | Text (XML) | Binary |
| Encoding Needed | ❌ No | ✅ Yes (base64) |
| Rename to .txt | ✅ Yes | ❌ No (encode first) |
| Parsing Complexity | Simple | Complex (font tables) |
| File Size Increase | 0% | +33% (from base64) |

## Bottom Line

The forum question "can TTF be saved as TXT like SVG" has a nuanced answer:

- **SVG→TXT**: Just rename (already text)
- **TTF→TXT**: Base64 encode, then save (convert binary to text)

**Both can be imported the same way once in text format.**

## Get Started

1. **Pick an encoding script:**
   - Linux/Mac: `scripts/encode-font.sh`
   - Windows: `scripts/encode-font.ps1`
   - Cross-platform: `scripts/encode-font.py`

2. **Encode your font:**
   ```bash
   ./scripts/encode-font.sh YourFont.ttf
   ```

3. **Follow the guide:**
   - Quick start: [EXAMPLE_USAGE.md](EXAMPLE_USAGE.md)
   - Full details: [CUSTOM_FONT_TTF_IMPORT.md](CUSTOM_FONT_TTF_IMPORT.md)

## See Also

- [Scripts README](scripts/README.md) - Encoding tool documentation
- [Font Parser](custom-features/fontParser.fs) - TTF/OTF parsing implementation
- [Custom Font Text Feature](custom-features/customFontText.fs) - Complete working example

---

**TL;DR:** Yes, TTF files can be imported like SVG files, but you need to base64-encode them first. This repo gives you everything you need to do that.
