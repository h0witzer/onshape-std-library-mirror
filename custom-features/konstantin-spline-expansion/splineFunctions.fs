FeatureScript 1324;
import(path : "onshape/std/geometry.fs", version : "1324.0");

export function evPathTransfromArray(context is Context, definition is map) returns array
precondition
{
    definition.path is Path;
    definition.paramArr is array;
    for (var item in definition.paramArr)
    {
        item is number && item >= 0 && item <= 1;
    }
}
{
    const tangentLines = evPathTangentLines(context,
                definition.path,
                definition.paramArr
            ).tangentLines;

    // Compute each transformation independently from the initial reference frame
    // This eliminates accumulated drift and ensures loop closure consistency
    const referenceTangent = tangentLines[0];
    var trArr = [];
    
    for (var i = 0; i < size(tangentLines); i += 1)
    {
        // Each transformation is computed directly from the reference, not accumulated
        var tr = transform(referenceTangent, tangentLines[i]);
        trArr = append(trArr, tr);
    }

    return trArr;
}
