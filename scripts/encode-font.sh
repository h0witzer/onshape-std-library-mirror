#!/bin/bash

# Custom Font Encoder for Onshape FeatureScript
# 
# This script encodes a TTF or OTF font file to base64 format
# so it can be uploaded to Onshape and used with the customFontText feature.
#
# Usage:
#   ./encode-font.sh MyCustomFont.ttf
#
# Output:
#   MyCustomFont.txt (base64-encoded, ready to upload to Onshape)

set -e

# Check if a file was provided
if [ $# -eq 0 ]; then
    echo "Usage: $0 <font-file.ttf|otf>"
    echo ""
    echo "Example: $0 MyFont.ttf"
    echo ""
    echo "This will create MyFont.txt which you can upload to Onshape"
    exit 1
fi

INPUT_FILE="$1"

# Check if file exists
if [ ! -f "$INPUT_FILE" ]; then
    echo "Error: File '$INPUT_FILE' not found"
    exit 1
fi

# Get the base name without extension
BASENAME="${INPUT_FILE%.*}"
OUTPUT_FILE="${BASENAME}.txt"

# Check if output file already exists
if [ -f "$OUTPUT_FILE" ]; then
    echo "Warning: $OUTPUT_FILE already exists"
    read -p "Overwrite? (y/n) " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Aborted"
        exit 1
    fi
fi

echo "Encoding $INPUT_FILE to base64..."

# Encode the font file to base64
# Using -w 76 for line wrapping (standard MIME format)
base64 -w 76 "$INPUT_FILE" > "$OUTPUT_FILE"

# Get file sizes
INPUT_SIZE=$(stat -f%z "$INPUT_FILE" 2>/dev/null || stat -c%s "$INPUT_FILE" 2>/dev/null)
OUTPUT_SIZE=$(stat -f%z "$OUTPUT_FILE" 2>/dev/null || stat -c%s "$OUTPUT_FILE" 2>/dev/null)

echo ""
echo "✓ Success!"
echo ""
echo "Input file:  $INPUT_FILE ($INPUT_SIZE bytes)"
echo "Output file: $OUTPUT_FILE ($OUTPUT_SIZE bytes)"
echo ""
echo "Next steps:"
echo "1. Upload $OUTPUT_FILE to your Onshape document"
echo "2. In FeatureScript, import it:"
echo "   myFont::import(path : \"doc-id/element-id\", version : \"version-id\");"
echo "3. Use the customFontText feature with myFont::BLOB_DATA"
echo ""
