# Custom Font Import Scripts

This directory contains helper scripts to encode TTF/OTF font files to base64 format for use with Onshape FeatureScript.

## Available Scripts

### encode-font.sh (Linux/Mac)
Bash script for Unix-like systems.

**Usage:**
```bash
./encode-font.sh MyCustomFont.ttf
```

### encode-font.ps1 (Windows)
PowerShell script for Windows systems.

**Usage:**
```powershell
.\encode-font.ps1 MyCustomFont.ttf
```

### encode-font.py (Cross-platform)
Python script that works on all platforms with Python 3 installed.

**Usage:**
```bash
python3 encode-font.py MyCustomFont.ttf
```

## What These Scripts Do

1. Read your TTF or OTF font file as binary data
2. Encode it to base64 text format
3. Add line breaks every 76 characters (MIME standard)
4. Save as a `.txt` file with the same name as your font

## Output

All scripts produce the same output: a `.txt` file containing the base64-encoded font data.

**Example:**
- Input: `Roboto-Bold.ttf` (173 KB)
- Output: `Roboto-Bold.txt` (230 KB) - ready to upload to Onshape

## Next Steps After Encoding

1. **Upload to Onshape:**
   - Open your Onshape document
   - Click the `+` button → Upload
   - Select your `.txt` file

2. **Import in FeatureScript:**
   ```featurescript
   myFont::import(path : "document-id/element-id", version : "version-id");
   ```

3. **Use with customFontText feature:**
   - Select `myFont::BLOB_DATA` as the font data source
   - Enter your text
   - Configure size and positioning

## Manual Encoding (Without Scripts)

### Using Command Line

**Linux/Mac:**
```bash
base64 -w 76 MyFont.ttf > MyFont.txt
```

**Windows PowerShell:**
```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("MyFont.ttf")) | Out-File MyFont.txt -Encoding ASCII
```

### Using Online Tools

Visit: https://base64.guru/converter/encode/file
1. Upload your font file
2. Click "Encode"
3. Download the result
4. Save as `.txt`

## Font Licensing

⚠️ **Important:** Ensure you have the legal right to use and embed the font in your models. Many fonts have licensing restrictions on embedding in software or products.

Safe options:
- Fonts you've purchased with embedding rights
- Open-source fonts (SIL Open Font License, Apache License, etc.)
- Google Fonts (most are free for commercial use)

## Troubleshooting

### Script Permission Denied (Linux/Mac)
```bash
chmod +x encode-font.sh
./encode-font.sh MyFont.ttf
```

### Python Not Found
Install Python 3 from https://www.python.org/ or use your package manager:
```bash
# Ubuntu/Debian
sudo apt install python3

# macOS with Homebrew
brew install python3
```

### PowerShell Execution Policy Error
Run PowerShell as Administrator and execute:
```powershell
Set-ExecutionPolicy RemoteSigned -Scope CurrentUser
```

## File Size Considerations

- Original TTF/OTF: 50-500 KB typically
- Base64-encoded: ~33% larger
- Onshape upload limit: Check current limits in Onshape documentation

## See Also

- [CUSTOM_FONT_TTF_IMPORT.md](../CUSTOM_FONT_TTF_IMPORT.md) - Complete guide
- [customFontText.fs](../custom-features/customFontText.fs) - Main feature
- [base64Decoder.fs](../custom-features/base64Decoder.fs) - Decoding utility
- [fontParser.fs](../custom-features/fontParser.fs) - Font parsing utility
