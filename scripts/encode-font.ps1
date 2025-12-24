# Custom Font Encoder for Onshape FeatureScript (PowerShell)
# 
# This script encodes a TTF or OTF font file to base64 format
# so it can be uploaded to Onshape and used with the customFontText feature.
#
# Usage:
#   .\encode-font.ps1 MyCustomFont.ttf
#
# Output:
#   MyCustomFont.txt (base64-encoded, ready to upload to Onshape)

param(
    [Parameter(Mandatory=$true)]
    [string]$InputFile
)

# Check if file exists
if (-not (Test-Path $InputFile)) {
    Write-Error "Error: File '$InputFile' not found"
    exit 1
}

# Get the base name without extension
$BaseName = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
$OutputFile = "$BaseName.txt"

# Check if output file already exists
if (Test-Path $OutputFile) {
    $response = Read-Host "Warning: $OutputFile already exists. Overwrite? (y/n)"
    if ($response -ne 'y') {
        Write-Host "Aborted"
        exit 1
    }
}

Write-Host "Encoding $InputFile to base64..."

# Read the font file as bytes
$fontBytes = [System.IO.File]::ReadAllBytes($InputFile)

# Convert to base64 with line wrapping
$base64String = [Convert]::ToBase64String($fontBytes, [System.Base64FormattingOptions]::InsertLineBreaks)

# Write to output file
[System.IO.File]::WriteAllText($OutputFile, $base64String, [System.Text.Encoding]::ASCII)

# Get file sizes
$inputSize = (Get-Item $InputFile).Length
$outputSize = (Get-Item $OutputFile).Length

Write-Host ""
Write-Host "✓ Success!" -ForegroundColor Green
Write-Host ""
Write-Host "Input file:  $InputFile ($inputSize bytes)"
Write-Host "Output file: $OutputFile ($outputSize bytes)"
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Upload $OutputFile to your Onshape document"
Write-Host "2. In FeatureScript, import it:"
Write-Host '   myFont::import(path : "doc-id/element-id", version : "version-id");'
Write-Host "3. Use the customFontText feature with myFont::BLOB_DATA"
Write-Host ""
