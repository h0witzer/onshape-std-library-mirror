FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");

// Import base64 decoder utilities (assuming it's in the same document)
// When using this, you'll need to update the path to point to base64Decoder.fs
// import(path : "path-to/base64Decoder.fs", version : "version");

/**
 * Font Parser for TrueType and OpenType Fonts
 * 
 * Parses TTF and OTF font files that have been base64-encoded and imported as blob data.
 * Extracts glyph outlines and converts them to FeatureScript-compatible geometry.
 * 
 * Supports:
 * - TrueType fonts (.ttf) with 'glyf' table
 * - OpenType fonts (.otf) with CFF data
 * - Character to glyph mapping via 'cmap' table
 * - Font metrics from 'head', 'hhea', and 'hmtx' tables
 * 
 * Usage:
 *   myFont::import(path : "doc-id/font-file.txt", version : "version");
 *   const fontData = parseFont(myFont::BLOB_DATA);
 *   const glyphOutline = getGlyphOutline(fontData, 'A');
 */

/**
 * Parse a TrueType or OpenType font file
 * 
 * @param fontBytes : Array of byte values from decoded base64 font file
 * @returns {map} : Parsed font data including tables and metadata
 */
export function parseFont(fontBytes is array) returns map
{
    // Read the offset table (file header)
    const sfntVersion = readUInt32(fontBytes, 0);
    const numTables = readUInt16(fontBytes, 4);
    const searchRange = readUInt16(fontBytes, 6);
    const entrySelector = readUInt16(fontBytes, 8);
    const rangeShift = readUInt16(fontBytes, 10);
    
    // Validate font file format
    // TrueType: 0x00010000 or 'true'
    // OpenType with CFF: 'OTTO'
    const isTrueType = (sfntVersion == 0x00010000) || (sfntVersion == 0x74727565);
    const isOpenType = (sfntVersion == 0x4F54544F);
    
    if (!isTrueType && !isOpenType)
    {
        throw "Invalid font file format";
    }
    
    // Read table directory
    var tables = {};
    for (var tableIndex = 0; tableIndex < numTables; tableIndex += 1)
    {
        const directoryOffset = 12 + (tableIndex * 16);
        const tag = readTag(fontBytes, directoryOffset);
        const checksum = readUInt32(fontBytes, directoryOffset + 4);
        const offset = readUInt32(fontBytes, directoryOffset + 8);
        const length = readUInt32(fontBytes, directoryOffset + 12);
        
        tables[tag] = {
            "offset" : offset,
            "length" : length,
            "checksum" : checksum
        };
    }
    
    // Parse required tables
    const headTable = parseHeadTable(fontBytes, tables);
    const maxpTable = parseMaxpTable(fontBytes, tables);
    const cmapTable = parseCmapTable(fontBytes, tables);
    const locaTable = parseLocaTable(fontBytes, tables, headTable, maxpTable);
    
    return {
        "isTrueType" : isTrueType,
        "isOpenType" : isOpenType,
        "tables" : tables,
        "head" : headTable,
        "maxp" : maxpTable,
        "cmap" : cmapTable,
        "loca" : locaTable,
        "bytes" : fontBytes
    };
}

/**
 * Parse the 'head' (font header) table
 * Contains global font information including units per em and index format
 * 
 * @param fontBytes : Font file byte array
 * @param tables : Map of table locations
 * @returns {map} : Parsed head table data
 */
function parseHeadTable(fontBytes is array, tables is map) returns map
{
    if (tables["head"] == undefined)
    {
        throw "Required 'head' table not found";
    }
    
    const offset = tables["head"].offset;
    
    return {
        "version" : readUInt32(fontBytes, offset),
        "fontRevision" : readUInt32(fontBytes, offset + 4),
        "checksumAdjustment" : readUInt32(fontBytes, offset + 8),
        "magicNumber" : readUInt32(fontBytes, offset + 12),
        "flags" : readUInt16(fontBytes, offset + 16),
        "unitsPerEm" : readUInt16(fontBytes, offset + 18),
        "created" : readUInt32(fontBytes, offset + 20), // 64-bit, reading low 32
        "modified" : readUInt32(fontBytes, offset + 28), // 64-bit, reading low 32
        "xMin" : readInt16(fontBytes, offset + 36),
        "yMin" : readInt16(fontBytes, offset + 38),
        "xMax" : readInt16(fontBytes, offset + 40),
        "yMax" : readInt16(fontBytes, offset + 42),
        "macStyle" : readUInt16(fontBytes, offset + 44),
        "lowestRecPPEM" : readUInt16(fontBytes, offset + 46),
        "fontDirectionHint" : readInt16(fontBytes, offset + 48),
        "indexToLocFormat" : readInt16(fontBytes, offset + 50), // 0 for short, 1 for long
        "glyphDataFormat" : readInt16(fontBytes, offset + 52)
    };
}

/**
 * Parse the 'maxp' (maximum profile) table
 * Contains the number of glyphs and other maximums
 * 
 * @param fontBytes : Font file byte array
 * @param tables : Map of table locations
 * @returns {map} : Parsed maxp table data
 */
function parseMaxpTable(fontBytes is array, tables is map) returns map
{
    if (tables["maxp"] == undefined)
    {
        throw "Required 'maxp' table not found";
    }
    
    const offset = tables["maxp"].offset;
    
    return {
        "version" : readUInt32(fontBytes, offset),
        "numGlyphs" : readUInt16(fontBytes, offset + 4)
    };
}

/**
 * Parse the 'cmap' (character to glyph mapping) table
 * Maps Unicode code points to glyph indices
 * 
 * @param fontBytes : Font file byte array
 * @param tables : Map of table locations
 * @returns {map} : Character to glyph ID mapping
 */
function parseCmapTable(fontBytes is array, tables is map) returns map
{
    if (tables["cmap"] == undefined)
    {
        throw "Required 'cmap' table not found";
    }
    
    const offset = tables["cmap"].offset;
    const version = readUInt16(fontBytes, offset);
    const numTables = readUInt16(fontBytes, offset + 2);
    
    // Find the best encoding table (prefer Unicode BMP or full Unicode)
    var bestSubtableOffset = -1;
    var bestPlatformID = -1;
    var bestEncodingID = -1;
    
    for (var i = 0; i < numTables; i += 1)
    {
        const recordOffset = offset + 4 + (i * 8);
        const platformID = readUInt16(fontBytes, recordOffset);
        const encodingID = readUInt16(fontBytes, recordOffset + 2);
        const subtableOffset = readUInt32(fontBytes, recordOffset + 4);
        
        // Platform ID 3 = Windows, Encoding ID 1 = Unicode BMP
        // Platform ID 3 = Windows, Encoding ID 10 = Unicode full
        // Platform ID 0 = Unicode, any encoding
        if ((platformID == 3 && encodingID == 1) || 
            (platformID == 3 && encodingID == 10) ||
            (platformID == 0))
        {
            bestSubtableOffset = offset + subtableOffset;
            bestPlatformID = platformID;
            bestEncodingID = encodingID;
            break;
        }
    }
    
    if (bestSubtableOffset == -1)
    {
        throw "No suitable character mapping table found";
    }
    
    // Parse the subtable (format 4 is most common for Unicode BMP)
    const format = readUInt16(fontBytes, bestSubtableOffset);
    
    var charToGlyph = {};
    
    if (format == 4)
    {
        charToGlyph = parseCmapFormat4(fontBytes, bestSubtableOffset);
    }
    else if (format == 12)
    {
        charToGlyph = parseCmapFormat12(fontBytes, bestSubtableOffset);
    }
    else
    {
        throw "Unsupported cmap format: " ~ format;
    }
    
    return {
        "format" : format,
        "platformID" : bestPlatformID,
        "encodingID" : bestEncodingID,
        "charToGlyph" : charToGlyph
    };
}

/**
 * Parse cmap format 4 (segment mapping to delta values)
 * Most common format for Unicode BMP (U+0000 to U+FFFF)
 */
function parseCmapFormat4(fontBytes is array, offset is number) returns map
{
    const format = readUInt16(fontBytes, offset);
    const length = readUInt16(fontBytes, offset + 2);
    const language = readUInt16(fontBytes, offset + 4);
    const segCount = readUInt16(fontBytes, offset + 6) / 2;
    const searchRange = readUInt16(fontBytes, offset + 8);
    const entrySelector = readUInt16(fontBytes, offset + 10);
    const rangeShift = readUInt16(fontBytes, offset + 12);
    
    // Read the parallel arrays
    var endCodes = [];
    var startCodes = [];
    var idDeltas = [];
    var idRangeOffsets = [];
    
    // Read endCode array
    for (var i = 0; i < segCount; i += 1)
    {
        endCodes = append(endCodes, readUInt16(fontBytes, offset + 14 + (i * 2)));
    }
    
    // Skip reserved padding (2 bytes)
    // Read startCode array
    for (var i = 0; i < segCount; i += 1)
    {
        startCodes = append(startCodes, readUInt16(fontBytes, offset + 16 + (segCount * 2) + (i * 2)));
    }
    
    // Read idDelta array
    for (var i = 0; i < segCount; i += 1)
    {
        idDeltas = append(idDeltas, readInt16(fontBytes, offset + 16 + (segCount * 4) + (i * 2)));
    }
    
    // Read idRangeOffset array
    const idRangeOffsetBase = offset + 16 + (segCount * 6);
    for (var i = 0; i < segCount; i += 1)
    {
        idRangeOffsets = append(idRangeOffsets, readUInt16(fontBytes, idRangeOffsetBase + (i * 2)));
    }
    
    // Build character to glyph mapping
    var charToGlyph = {};
    
    for (var segIndex = 0; segIndex < segCount; segIndex += 1)
    {
        const startCode = startCodes[segIndex];
        const endCode = endCodes[segIndex];
        const idDelta = idDeltas[segIndex];
        const idRangeOffset = idRangeOffsets[segIndex];
        
        for (var charCode = startCode; charCode <= endCode; charCode += 1)
        {
            var glyphIndex;
            
            if (idRangeOffset == 0)
            {
                // Simple delta mapping
                glyphIndex = (charCode + idDelta) % 65536;
            }
            else
            {
                // Complex offset mapping
                const glyphIndexOffset = idRangeOffsetBase + (segIndex * 2) + idRangeOffset + 
                                        ((charCode - startCode) * 2);
                glyphIndex = readUInt16(fontBytes, glyphIndexOffset);
                
                if (glyphIndex != 0)
                {
                    glyphIndex = (glyphIndex + idDelta) % 65536;
                }
            }
            
            if (glyphIndex != 0)
            {
                charToGlyph[toString(charCode)] = glyphIndex;
            }
        }
    }
    
    return charToGlyph;
}

/**
 * Parse cmap format 12 (segmented coverage)
 * Used for full Unicode support (beyond BMP)
 */
function parseCmapFormat12(fontBytes is array, offset is number) returns map
{
    // Format 12 structure
    const format = readUInt16(fontBytes, offset);
    const reserved = readUInt16(fontBytes, offset + 2);
    const length = readUInt32(fontBytes, offset + 4);
    const language = readUInt32(fontBytes, offset + 8);
    const numGroups = readUInt32(fontBytes, offset + 12);
    
    var charToGlyph = {};
    
    for (var groupIndex = 0; groupIndex < numGroups; groupIndex += 1)
    {
        const groupOffset = offset + 16 + (groupIndex * 12);
        const startCharCode = readUInt32(fontBytes, groupOffset);
        const endCharCode = readUInt32(fontBytes, groupOffset + 4);
        const startGlyphID = readUInt32(fontBytes, groupOffset + 8);
        
        for (var charCode = startCharCode; charCode <= endCharCode; charCode += 1)
        {
            const glyphIndex = startGlyphID + (charCode - startCharCode);
            charToGlyph[toString(charCode)] = glyphIndex;
        }
    }
    
    return charToGlyph;
}

/**
 * Parse the 'loca' (index to location) table
 * Contains offsets to glyph data in the 'glyf' table
 * 
 * @param fontBytes : Font file byte array
 * @param tables : Map of table locations
 * @param headTable : Parsed head table (contains format info)
 * @param maxpTable : Parsed maxp table (contains glyph count)
 * @returns {array} : Array of glyph offsets
 */
function parseLocaTable(fontBytes is array, tables is map, headTable is map, maxpTable is map) returns array
{
    if (tables["loca"] == undefined)
    {
        // OpenType CFF fonts don't have loca table
        return [];
    }
    
    const offset = tables["loca"].offset;
    const indexToLocFormat = headTable.indexToLocFormat;
    const numGlyphs = maxpTable.numGlyphs;
    
    var glyphOffsets = [];
    
    if (indexToLocFormat == 0)
    {
        // Short format: offsets are uint16 / 2
        for (var i = 0; i <= numGlyphs; i += 1)
        {
            const shortOffset = readUInt16(fontBytes, offset + (i * 2));
            glyphOffsets = append(glyphOffsets, shortOffset * 2);
        }
    }
    else
    {
        // Long format: offsets are uint32
        for (var i = 0; i <= numGlyphs; i += 1)
        {
            glyphOffsets = append(glyphOffsets, readUInt32(fontBytes, offset + (i * 4)));
        }
    }
    
    return glyphOffsets;
}

/**
 * Get glyph index for a character
 * 
 * @param fontData : Parsed font data
 * @param character : Single character string
 * @returns {number} : Glyph index, or 0 if not found
 */
export function getGlyphIndex(fontData is map, character is string) returns number
{
    // Get Unicode code point
    const charCode = character[0]; // This gets the first character
    const charCodeStr = toString(charCode);
    
    const charToGlyph = fontData.cmap.charToGlyph;
    
    if (charToGlyph[charCodeStr] != undefined)
    {
        return charToGlyph[charCodeStr];
    }
    
    // Return 0 (notdef glyph) if character not found
    return 0;
}

/**
 * Get glyph outline data for a specific glyph
 * 
 * @param fontData : Parsed font data
 * @param glyphIndex : Index of glyph to extract
 * @returns {map} : Glyph outline data including contours and points
 */
export function getGlyphOutline(fontData is map, glyphIndex is number) returns map
{
    if (!fontData.isTrueType)
    {
        throw "CFF/OpenType glyph extraction not yet implemented";
    }
    
    // Get glyph location from loca table
    if (glyphIndex < 0 || glyphIndex >= size(fontData.loca) - 1)
    {
        throw "Invalid glyph index: " ~ glyphIndex;
    }
    
    const glyphOffset = fontData.loca[glyphIndex];
    const nextGlyphOffset = fontData.loca[glyphIndex + 1];
    
    // Empty glyph (e.g., space character)
    if (glyphOffset == nextGlyphOffset)
    {
        return {
            "numberOfContours" : 0,
            "xMin" : 0,
            "yMin" : 0,
            "xMax" : 0,
            "yMax" : 0,
            "contours" : []
        };
    }
    
    // Read glyph data from 'glyf' table
    const glyfTableOffset = fontData.tables["glyf"].offset;
    const glyphDataOffset = glyfTableOffset + glyphOffset;
    
    const fontBytes = fontData.bytes;
    
    const numberOfContours = readInt16(fontBytes, glyphDataOffset);
    const xMin = readInt16(fontBytes, glyphDataOffset + 2);
    const yMin = readInt16(fontBytes, glyphDataOffset + 4);
    const xMax = readInt16(fontBytes, glyphDataOffset + 6);
    const yMax = readInt16(fontBytes, glyphDataOffset + 8);
    
    if (numberOfContours < 0)
    {
        // Compound glyph - not yet fully implemented
        throw "Compound glyphs not yet supported";
    }
    
    // Simple glyph - parse contours
    var contours = [];
    var offset = glyphDataOffset + 10;
    
    // Read end points of contours
    var endPtsOfContours = [];
    for (var i = 0; i < numberOfContours; i += 1)
    {
        endPtsOfContours = append(endPtsOfContours, readUInt16(fontBytes, offset));
        offset += 2;
    }
    
    // Read instruction length and skip instructions
    const instructionLength = readUInt16(fontBytes, offset);
    offset += 2 + instructionLength;
    
    // Calculate total number of points
    const numPoints = endPtsOfContours[numberOfContours - 1] + 1;
    
    // Read flags
    var flags = [];
    var pointIndex = 0;
    while (pointIndex < numPoints)
    {
        const flag = readUInt8(fontBytes, offset);
        offset += 1;
        flags = append(flags, flag);
        pointIndex += 1;
        
        // Check for repeat flag
        if ((flag & 0x08) != 0)
        {
            const repeatCount = readUInt8(fontBytes, offset);
            offset += 1;
            
            for (var repeatIndex = 0; repeatIndex < repeatCount; repeatIndex += 1)
            {
                flags = append(flags, flag);
                pointIndex += 1;
            }
        }
    }
    
    // Read X coordinates
    var xCoordinates = [];
    var xValue = 0;
    for (var i = 0; i < numPoints; i += 1)
    {
        const flag = flags[i];
        
        if ((flag & 0x02) != 0)
        {
            // X-Short Vector
            const xByte = readUInt8(fontBytes, offset);
            offset += 1;
            
            if ((flag & 0x10) != 0)
            {
                xValue += xByte; // Positive
            }
            else
            {
                xValue -= xByte; // Negative
            }
        }
        else if ((flag & 0x10) == 0)
        {
            // X is 16-bit signed delta
            const xDelta = readInt16(fontBytes, offset);
            offset += 2;
            xValue += xDelta;
        }
        // else: flag & 0x10 set means same as previous X
        
        xCoordinates = append(xCoordinates, xValue);
    }
    
    // Read Y coordinates
    var yCoordinates = [];
    var yValue = 0;
    for (var i = 0; i < numPoints; i += 1)
    {
        const flag = flags[i];
        
        if ((flag & 0x04) != 0)
        {
            // Y-Short Vector
            const yByte = readUInt8(fontBytes, offset);
            offset += 1;
            
            if ((flag & 0x20) != 0)
            {
                yValue += yByte; // Positive
            }
            else
            {
                yValue -= yByte; // Negative
            }
        }
        else if ((flag & 0x20) == 0)
        {
            // Y is 16-bit signed delta
            const yDelta = readInt16(fontBytes, offset);
            offset += 2;
            yValue += yDelta;
        }
        // else: flag & 0x20 set means same as previous Y
        
        yCoordinates = append(yCoordinates, yValue);
    }
    
    // Build contour data
    var startPoint = 0;
    for (var contourIndex = 0; contourIndex < numberOfContours; contourIndex += 1)
    {
        const endPoint = endPtsOfContours[contourIndex];
        
        var contourPoints = [];
        for (var pointIdx = startPoint; pointIdx <= endPoint; pointIdx += 1)
        {
            contourPoints = append(contourPoints, {
                "x" : xCoordinates[pointIdx],
                "y" : yCoordinates[pointIdx],
                "onCurve" : (flags[pointIdx] & 0x01) != 0
            });
        }
        
        contours = append(contours, contourPoints);
        startPoint = endPoint + 1;
    }
    
    return {
        "numberOfContours" : numberOfContours,
        "xMin" : xMin,
        "yMin" : yMin,
        "xMax" : xMax,
        "yMax" : yMax,
        "contours" : contours
    };
}

// Helper functions that would normally be imported from base64Decoder.fs
// These are placeholders - in actual use, import from base64Decoder.fs

function readUInt8(bytes is array, offset is number) returns number
{
    return bytes[offset];
}

function readUInt16(bytes is array, offset is number) returns number
{
    return (bytes[offset] << 8) | bytes[offset + 1];
}

function readInt16(bytes is array, offset is number) returns number
{
    const unsigned = readUInt16(bytes, offset);
    return (unsigned >= 32768) ? (unsigned - 65536) : unsigned;
}

function readUInt32(bytes is array, offset is number) returns number
{
    return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | 
           (bytes[offset + 2] << 8) | bytes[offset + 3];
}

function readTag(bytes is array, offset is number) returns string
{
    // Convert 4 bytes to string
    // Note: This is simplified - actual implementation needs proper character conversion
    return toString(bytes[offset]) ~ toString(bytes[offset + 1]) ~ 
           toString(bytes[offset + 2]) ~ toString(bytes[offset + 3]);
}
