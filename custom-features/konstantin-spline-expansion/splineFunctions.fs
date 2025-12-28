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

    var accumulatedTr = identityTransform();
    var trArr = [accumulatedTr];
    for (var i = 0; i < size(tangentLines) - 1; i += 1)
    {
        var tr = transform(tangentLines[i], tangentLines[i + 1]);
        accumulatedTr = tr * accumulatedTr;
        trArr = append(trArr, accumulatedTr);
    }

    return trArr;
}
