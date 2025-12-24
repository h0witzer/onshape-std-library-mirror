FeatureScript 2837;

/**
 * Base64 Decoder Utility
 * 
 * Provides functions to decode base64-encoded strings back to binary byte arrays.
 * This is essential for importing binary font files (TTF/OTF) that have been
 * encoded as text for import into Onshape.
 * 
 * Usage:
 *   const encodedFont = "AAEAAAAOAIAAAwBgT1MvMj3hS..."; // Base64 string
 *   const fontBytes = decodeBase64(encodedFont);
 *   // fontBytes is now an array of integers 0-255 representing the binary data
 */

/**
 * Decode a base64-encoded string to an array of byte values (0-255)
 * 
 * @param base64String : The base64-encoded string to decode
 * @returns {array} : Array of integers (0-255) representing binary bytes
 */
export function decodeBase64(base64String is string) returns array
{
    // Base64 character set
    const base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    
    // Create lookup map for character to value
    var charToValue = {};
    for (var i = 0; i < 64; i += 1)
    {
        charToValue[base64Alphabet[i]] = i;
    }
    
    // Remove whitespace and padding from input
    var cleanedInput = "";
    var paddingCount = 0;
    
    for (var i = 0; i < length(base64String); i += 1)
    {
        const char = base64String[i];
        if (char == "=")
        {
            paddingCount += 1;
        }
        else if (char != " " && char != "\n" && char != "\r" && char != "\t")
        {
            cleanedInput = cleanedInput ~ char;
        }
    }
    
    const inputLength = length(cleanedInput);
    var bytes = [];
    
    // Process in groups of 4 base64 characters -> 3 bytes
    for (var i = 0; i < inputLength; i += 4)
    {
        // Get 4 characters (or less for final group)
        const char1 = cleanedInput[i];
        const char2 = (i + 1 < inputLength) ? cleanedInput[i + 1] : "A";
        const char3 = (i + 2 < inputLength) ? cleanedInput[i + 2] : "A";
        const char4 = (i + 3 < inputLength) ? cleanedInput[i + 3] : "A";
        
        // Convert characters to 6-bit values
        const val1 = charToValue[char1];
        const val2 = charToValue[char2];
        const val3 = charToValue[char3];
        const val4 = charToValue[char4];
        
        // Combine into 24 bits
        const bits24 = (val1 << 18) | (val2 << 12) | (val3 << 6) | val4;
        
        // Extract 3 bytes
        const byte1 = (bits24 >> 16) & 0xFF;
        const byte2 = (bits24 >> 8) & 0xFF;
        const byte3 = bits24 & 0xFF;
        
        // Add bytes to output
        bytes = append(bytes, byte1);
        
        // Check if we should add byte2 and byte3 based on padding
        if (i + 2 < inputLength || paddingCount < 2)
        {
            bytes = append(bytes, byte2);
        }
        
        if (i + 3 < inputLength || paddingCount < 1)
        {
            bytes = append(bytes, byte3);
        }
    }
    
    return bytes;
}

/**
 * Extract a blob data string from an imported file
 * Blob data from imports comes as a map with various fields
 * 
 * @param blobData : The BLOB_DATA from an import statement
 * @returns {string} : The actual data string to decode
 */
export function extractBlobString(blobData is map) returns string
{
    // Blob data structure varies, but typically contains a 'data' field
    // or the blob itself may be the string
    if (blobData is map && blobData.data != undefined)
    {
        return blobData.data;
    }
    
    // If blob is already a string, return it
    if (blobData is string)
    {
        return blobData;
    }
    
    // Try to extract bytes if available
    if (blobData is map && blobData.bytes != undefined)
    {
        return blobData.bytes;
    }
    
    throw "Unable to extract string from blob data";
}

/**
 * Helper function to read bytes from blob data
 * Handles the conversion from blob import to usable byte array
 * 
 * @param blobData : The BLOB_DATA from font import
 * @returns {array} : Array of byte values (0-255)
 */
export function getBytesFromBlob(blobData is map) returns array
{
    try
    {
        const dataString = extractBlobString(blobData);
        return decodeBase64(dataString);
    }
    catch (error)
    {
        throw "Failed to decode font data: " ~ error;
    }
}

/**
 * Read an unsigned 8-bit integer from byte array
 * 
 * @param bytes : Array of byte values
 * @param offset : Offset in array to read from
 * @returns {number} : Value (0-255)
 */
export function readUInt8(bytes is array, offset is number) returns number
{
    if (offset < 0 || offset >= size(bytes))
    {
        throw "Offset out of bounds";
    }
    
    return bytes[offset];
}

/**
 * Read an unsigned 16-bit integer (big-endian) from byte array
 * 
 * @param bytes : Array of byte values
 * @param offset : Offset in array to read from
 * @returns {number} : Value (0-65535)
 */
export function readUInt16(bytes is array, offset is number) returns number
{
    if (offset < 0 || offset + 1 >= size(bytes))
    {
        throw "Offset out of bounds";
    }
    
    return (bytes[offset] << 8) | bytes[offset + 1];
}

/**
 * Read a signed 16-bit integer (big-endian) from byte array
 * 
 * @param bytes : Array of byte values
 * @param offset : Offset in array to read from
 * @returns {number} : Value (-32768 to 32767)
 */
export function readInt16(bytes is array, offset is number) returns number
{
    const unsigned = readUInt16(bytes, offset);
    
    // Convert to signed
    if (unsigned >= 32768)
    {
        return unsigned - 65536;
    }
    
    return unsigned;
}

/**
 * Read an unsigned 32-bit integer (big-endian) from byte array
 * 
 * @param bytes : Array of byte values
 * @param offset : Offset in array to read from
 * @returns {number} : Value (0-4294967295)
 */
export function readUInt32(bytes is array, offset is number) returns number
{
    if (offset < 0 || offset + 3 >= size(bytes))
    {
        throw "Offset out of bounds";
    }
    
    return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | 
           (bytes[offset + 2] << 8) | bytes[offset + 3];
}

/**
 * Read a 4-character tag from byte array
 * Font tables are identified by 4-character ASCII tags
 * 
 * @param bytes : Array of byte values
 * @param offset : Offset in array to read from
 * @returns {string} : 4-character tag string
 */
export function readTag(bytes is array, offset is number) returns string
{
    if (offset < 0 || offset + 3 >= size(bytes))
    {
        throw "Offset out of bounds";
    }
    
    // Convert 4 bytes to ASCII characters
    const char1 = toString(bytes[offset]);
    const char2 = toString(bytes[offset + 1]);
    const char3 = toString(bytes[offset + 2]);
    const char4 = toString(bytes[offset + 3]);
    
    return char1 ~ char2 ~ char3 ~ char4;
}
