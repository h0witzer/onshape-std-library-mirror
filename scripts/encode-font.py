#!/usr/bin/env python3
"""
Custom Font Encoder for Onshape FeatureScript

This script encodes a TTF or OTF font file to base64 format
so it can be uploaded to Onshape and used with the customFontText feature.

Usage:
    python3 encode-font.py MyCustomFont.ttf

Output:
    MyCustomFont.txt (base64-encoded, ready to upload to Onshape)
"""

import sys
import os
import base64


def encode_font(input_file):
    """Encode a font file to base64 text format"""
    
    # Check if file exists
    if not os.path.exists(input_file):
        print(f"Error: File '{input_file}' not found")
        return 1
    
    # Get base name without extension
    base_name = os.path.splitext(input_file)[0]
    output_file = f"{base_name}.txt"
    
    # Check if output exists
    if os.path.exists(output_file):
        response = input(f"Warning: {output_file} already exists. Overwrite? (y/n) ")
        if response.lower() != 'y':
            print("Aborted")
            return 1
    
    print(f"Encoding {input_file} to base64...")
    
    # Read font file as binary
    with open(input_file, 'rb') as f:
        font_data = f.read()
    
    # Encode to base64 with line breaks every 76 characters (MIME standard)
    encoded = base64.b64encode(font_data).decode('ascii')
    
    # Add line breaks every 76 characters
    wrapped = '\n'.join(encoded[i:i+76] for i in range(0, len(encoded), 76))
    
    # Write to output file
    with open(output_file, 'w') as f:
        f.write(wrapped)
        f.write('\n')  # Final newline
    
    # Get file sizes
    input_size = os.path.getsize(input_file)
    output_size = os.path.getsize(output_file)
    
    print()
    print("✓ Success!")
    print()
    print(f"Input file:  {input_file} ({input_size:,} bytes)")
    print(f"Output file: {output_file} ({output_size:,} bytes)")
    print()
    print("Next steps:")
    print(f"1. Upload {output_file} to your Onshape document")
    print("2. In FeatureScript, import it:")
    print('   myFont::import(path : "doc-id/element-id", version : "version-id");')
    print("3. Use the customFontText feature with myFont::BLOB_DATA")
    print()
    
    return 0


def main():
    if len(sys.argv) != 2:
        print("Usage: python3 encode-font.py <font-file.ttf|otf>")
        print()
        print("Example: python3 encode-font.py MyFont.ttf")
        print()
        print("This will create MyFont.txt which you can upload to Onshape")
        return 1
    
    input_file = sys.argv[1]
    return encode_font(input_file)


if __name__ == "__main__":
    sys.exit(main())
