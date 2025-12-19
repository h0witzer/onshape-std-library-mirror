
//_______________________________________________________________________________________________________________________________________________
//
// This FeatureScript is owned by Michael Pascoe and is distributed by CADSharp LLC. 
// You may not redistribute it for commercial purposes without the permission of said owner and CADSharp LLC. Copyright (c) 2023 Michael Pascoe.
//_______________________________________________________________________________________________________________________________________________


FeatureScript 2260;
import(path : "onshape/std/common.fs", version : "2260.0");

// CADSharp
export import(path : "cbeb3dcf671e00785597bd76/409d65a3744fe434f32bdffc/a75ab01def146a42f55baa7f", version : "381046010d5aea697e433948");

// export import(path : "c7c08274a0d273b9a5f5b47d/16aa9873224c4a0cc2412bfd/6b9d1a8ad65ac54fe2125581", version : "b1c9eb4800a01f8a29c6c12a");
// export import(path : "c7c08274a0d273b9a5f5b47d/16aa9873224c4a0cc2412bfd/5859f548d83656e6f7c0d427", version : "5474fc5269dcc948135c4857");
import(path : "c7c08274a0d273b9a5f5b47d/f89b4047602171084b43cd13/af743c73c677355164494e79", version : "41b9668ee979c27b9ac0cb92");
export import(path : "12312312345abcabcabcdeff/8e6d551b2d41cf932b1681f6/ec739638f0a117a475a744af", version : "312fee4ee687399466575a10");

icon::import(path : "4cba10e99d49d95b30708864", version : "7882b03d25a2320567ce70df");

export enum BooleanScopeLocal
{
    annotation { "Name" : "New" }
    NEW,
    annotation { "Name" : "Add" }
    ADD,
    annotation { "Name" : "Subtract" }
    SUBTRACT,
    annotation { "Name" : "Intersect" }
    INTERSECT,
    annotation { "Name" : "Split" }
    SPLIT
}

export enum BodyOptions
{
    annotation { "Name" : "Sketch" }
    SKETCH,
    annotation { "Name" : "Wire" }
    WIRE,
    annotation { "Name" : "Surface" }
    SURFACE,
    annotation { "Name" : "Solid" }
    SOLID,
}

export enum FontEnumLocal
{
    annotation { "Name" : "OpenSans" }
    OpenSans,
    annotation { "Name" : "AllertaStencil" }
    AllertaStencil,
    annotation { "Name" : "Arimo" }
    Arimo,
    annotation { "Name" : "DroidSansMono" }
    DroidSansMono,
    annotation { "Name" : "NotoSans" }
    NotoSans,
    annotation { "Name" : "NotoSansCJKjp" }
    NotoSansCJKjp,
    annotation { "Name" : "NotoSansCJKkr" }
    NotoSansCJKkr,
    annotation { "Name" : "NotoSansCJKsc" }
    NotoSansCJKsc,
    annotation { "Name" : "NotoSansCJKtc" }
    NotoSansCJKtc,
    annotation { "Name" : "NotoSerif" }
    NotoSerif,
    annotation { "Name" : "RobotoSlab" }
    RobotoSlab,
    annotation { "Name" : "Tinos" }
    Tinos
}

// const FontMap = {
//   "0": undefined,
//   "1": undefined,
//   "2": undefined,
//   "3": undefined,
//   "4": undefined,
//   "5": undefined,
//   "6": undefined,
//   "7": undefined,
//   "8": undefined,
//   "9": undefined,
//   ".": undefined,
//   ",": undefined,
//   ":": undefined,
//   ";": undefined,
//   "‘": undefined,
//   "“": undefined,
//   "/": undefined,
//   "<": undefined,
//   ">": undefined,
//   "!": undefined,
//   "@": undefined,
//   "#": undefined,
//   "$": undefined,
//   "%": undefined,
//   "^": undefined,
//   "&": undefined,
//   "*": undefined,
//   "(": undefined,
//   ")": undefined,
//   "_": undefined,
//   "+": undefined,
//   "-": undefined,
//   "=": undefined,
//   "{": undefined,
//   "}": undefined,
//   "[": undefined,
//   "]": undefined,
//   "A": undefined,
//   "B": undefined,
//   "C": undefined,
//   "D": undefined,
//   "E": undefined,
//   "F": undefined,
//   "G": undefined,
//   "H": undefined,
//   "I": undefined,
//   "J": undefined,
//   "K": undefined,
//   "L": undefined,
//   "M": undefined,
//   "N": undefined,
//   "O": undefined,
//   "P": undefined,
//   "Q": undefined,
//   "R": undefined,
//   "S": undefined,
//   "T": undefined,
//   "U": undefined,
//   "V": undefined,
//   "W": undefined,
//   "X": undefined,
//   "Y": undefined,
//   "Z": undefined,
//   "a": undefined,
//   "b": undefined,
//   "c": undefined,
//   "d": undefined,
//   "e": undefined,
//   "f": undefined,
//   "g": undefined,
//   "h": undefined,
//   "i": undefined,
//   "j": undefined,
//   "k": undefined,
//   "l": undefined,
//   "m": undefined,
//   "n": undefined,
//   "o": undefined,
//   "p": undefined,
//   "q": undefined,
//   "r": undefined,
//   "s": undefined,
//   "t": undefined,
//   "u": undefined,
//   "v": undefined,
//   "w": undefined,
//   "x": undefined,
//   "y": undefined,
//   "z": undefined
// };

export predicate TextMainPredicatePascoe(definition)
{
    annotation { "Name" : "Boolean enum", "Default" : BooleanScopeLocal.NEW, "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
    definition.booleanEnum is BooleanScopeLocal;

    if (definition.booleanEnum == BooleanScopeLocal.NEW)
    {
        annotation { "Name" : "Body type", "Default" : BodyOptions.SOLID, "UIHint" : [UIHint.SHOW_LABEL, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.bodyOption is BodyOptions;
    }

    annotation { "Name" : "Text (abc)", "Default" : "Words" }
    definition.text is string;

    annotation { "Name" : "Face", "Filter" : EntityType.FACE || BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
    definition.location is Query;

    if (definition.booleanEnum != BooleanScopeLocal.NEW)
    {
        annotation { "Name" : "Merge scope", "Filter" : EntityType.FACE || EntityType.BODY }
        definition.mergeScope is Query;
    }

    if (definition.booleanEnum != BooleanScopeLocal.SPLIT)
    {
        annotation { "Name" : "Depth", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.depth, LENGTH_BOUNDS);

        annotation { "Name" : "Opposite direction", "UIHint" : [UIHint.OPPOSITE_DIRECTION, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.oppositeDirection is boolean;
    }
}

export predicate TextSettingsPredicatePascoe(definition)
{
    annotation { "Group Name" : "Font Settings", "Collapsed By Default" : true }
    {
        annotation { "Name" : "Text height", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.textHeight, LENGTH_BOUNDS);

        annotation { "Name" : "Font", "Icon" : icon::BLOB_DATA, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.font is FontEnumLocal;

        annotation { "Name" : "Bold", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.bold is boolean;

        annotation { "Name" : "Italic", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.italic is boolean;

        annotation { "Name" : "Mirror horizontal", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.mirrorHorizontal is boolean;

        annotation { "Name" : "Mirror verticcal", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.mirrorVertical is boolean;
    }

    cadsharpUrlPredicate(definition);
}


export function TextFunctionPascoe(
    context is Context,
    id is Id,
    booleanScope is BooleanScopeLocal,
    bodyOption is BodyOptions,
    text is string,
    location,
    mergeScope is Query,
    depth is ValueWithUnits,
    oppositeDirection is boolean,
    textHeight is ValueWithUnits,
    font is FontEnumLocal,
    bold is boolean,
    italic is boolean,
    mirrorHorizontal is boolean,
    mirrorVertical is boolean)
{
    var definition = {};
    definition.booleanEnum = booleanScope;
    definition.bodyOption = bodyOption;
    definition.text = text;
    definition.location = location;
    definition.mergeScope = mergeScope;
    definition.depth = depth;
    definition.oppositeDirection = oppositeDirection;
    definition.textHeight = textHeight;
    definition.font = font;
    definition.bold = bold;
    definition.italic = italic;
    definition.mirrorHorizontal = mirrorHorizontal;
    definition.mirrorVertical = mirrorVertical;

    var evalPlane = qNothing();

    if (definition.location is Plane)
    {
        evalPlane = definition.location;
    }
    else if (isFace(context, definition))
    {
        evalPlane = evFaceTangentPlane(context, {
                    "face" : definition.location,
                    "parameter" : vector(0.5, 0.5)
                });
    }
    else
    {
        evalPlane = evMateConnector(context, {
                        "mateConnector" : definition.location
                    })->plane();
    }

    const sketch1 = newSketchOnPlane(context, id + "sketch1", {
                "sketchPlane" : evalPlane
            });

    skText(sketch1, "text1", {
                "text" : definition.text,
                "fontName" : handleFont(definition.font, definition.bold, definition.italic),
                "firstCorner" : vector(0, 0) * inch,
                "secondCorner" : vector(1 * inch, definition.textHeight),
                "mirrorHorizontal" : definition.mirrorHorizontal,
                "mirrorVertical" : definition.mirrorVertical,
            });

    skSolve(sketch1);

    var toDelete = qNothing();
    var toBoolean = qNothing();
    var sketchEdges = qCreatedBy(id + "sketch1", EntityType.EDGE);

    const longestEdge = evaluateQuery(context, qLargest(sketchEdges))[0];
    const halfLength = evLength(context, {
                    "entities" : longestEdge
                }) / 2;

    const t = transform(evalPlane, plane(planeToWorld3D(evalPlane) * vector(-halfLength, -definition.textHeight / 2, 0 * inch), evalPlane.normal, evalPlane.x));
    const sketchFaces = qSketchRegion(id + "sketch1", true);

    var surface = qNothing();
    var toThicken = qNothing();
    var tools = qNothing();

    // If not sketch
    if (!(definition.booleanEnum == BooleanScopeLocal.NEW && definition.bodyOption == BodyOptions.SKETCH))
    {
        opExtractSurface(context, id + "extractFace", {
                    "faces" : sketchFaces,
                    "offset" : 0 * inch });

        surface = qCreatedBy(id + "extractFace", EntityType.BODY);
    }
    else
    {
        const constructionEdges = qConstructionFilter(sketchEdges, ConstructionObject.YES);
        sketchEdges = qSubtraction(sketchEdges, constructionEdges);
        toDelete = qUnion([toDelete, constructionEdges]);
    }

    opTransform(context, id + "transform1", {
                "bodies" : qUnion([surface, qCreatedBy(id + "sketch1", EntityType.BODY)]),
                "transform" : t
            });

    toThicken = qCreatedBy(id + "extractFace", EntityType.FACE);
    tools = qCreatedBy(id + "extractFace", EntityType.BODY);

    const remainingTransform = getRemainderPatternTransform(context, { "references" : tools });
    transformResultIfNecessary(context, id, remainingTransform);

    if (definition.booleanEnum == BooleanScopeLocal.SPLIT)
    {
        addDebugEntities(context, toThicken, DebugColor.GREEN);
        toDelete = qUnion([toDelete, sketchEdges]);
        toDelete = qUnion([toDelete, tools]);
        var edges = qNothing();
        const evFaces = evaluateQuery(context, toThicken);

        // Loop through all of the faces to get the boundaries of each one
        for (var i = 0; i < size(evFaces); i += 1)
        {
            edges = qUnion([edges, qLoopEdges(evFaces[i])]);
        }

        opSplitFace(context, id + "splitFace1", {
                    "faceTargets" : definition.mergeScope,
                    "edgeTools" : edges,
                    "projectionType" : ProjectionType.NORMAL_TO_TARGET,
                });
    }
    else
    {
        const isSolidType = definition.booleanEnum != BooleanScopeLocal.NEW || (definition.booleanEnum == BooleanScopeLocal.NEW && definition.bodyOption == BodyOptions.SOLID);

        if (definition.booleanEnum == BooleanScopeLocal.NEW)
        {
            if (definition.bodyOption == BodyOptions.SKETCH)
            {
                toDelete = qUnion([toDelete, surface]);
            }
            else if (definition.bodyOption == BodyOptions.SURFACE)
            {
                toDelete = qUnion([toDelete, sketchEdges]);

                setProperty(context, {
                            "entities" : surface,
                            "propertyType" : PropertyType.APPEARANCE,
                            "value" : color(0, 0, 0)
                        });
            }
            else if (definition.bodyOption == BodyOptions.WIRE)
            {
                toDelete = qUnion([toDelete, sketchEdges]);
                toDelete = qUnion([toDelete, tools]);

                const evEdges = evaluateQuery(context, qConstructionFilter(sketchEdges, ConstructionObject.NO));

                for (var i = 0; i < size(evEdges); i += 1)
                {
                    opExtractWires(context, id + "opExtractWires1" + i, {
                                "edges" : evEdges[i]
                            });
                }
            }
            else
            {
                toDelete = qUnion([toDelete, sketchEdges]);
                toDelete = qUnion([toDelete, tools]);
            }
        }
        else
        {
            toDelete = qUnion([toDelete, sketchEdges]);
            toDelete = qUnion([toDelete, tools]);
        }

        const direction = definition.oppositeDirection ? -1 : 1;
        const thickness1 = definition.booleanEnum == BooleanScopeLocal.NEW || definition.booleanEnum == BooleanScopeLocal.ADD || definition.booleanEnum == BooleanScopeLocal.ADD ? definition.depth : 0 * inch;
        const thickness2 = definition.booleanEnum == BooleanScopeLocal.SUBTRACT || definition.booleanEnum == BooleanScopeLocal.INTERSECT ? definition.depth : 0 * inch;

        if (isSolidType)
        {
            opThicken(context, id + "thicken1", {
                        "entities" : toThicken,
                        "thickness1" : definition.oppositeDirection ? thickness2 : thickness1,
                        "thickness2" : definition.oppositeDirection ? thickness1 : thickness2,
                    });

            const thickenned = qCreatedBy(id + "thicken1", EntityType.BODY);

            addDebugEntities(context, thickenned, DebugColor.GREEN);

            if (definition.booleanEnum != BooleanScopeLocal.NEW && definition.booleanEnum != BooleanScopeLocal.SPLIT)
            {
                toDelete = qUnion([toDelete, thickenned]);
            }
            else
            {
                setProperty(context, {
                            "entities" : thickenned,
                            "propertyType" : PropertyType.APPEARANCE,
                            "value" : color(0, 0, 0)
                        });
            }

            tools = qUnion([tools, thickenned]);
            tools = qIntersection([tools, qBodyType(tools, BodyType.SOLID)]);

            BooleanFunctionPascoe(context, id, definition.booleanEnum->toString(), tools, definition.mergeScope->qOwnerBody(), false);
        }
    }

    if (!isQueryEmpty(context, toDelete))
    {
        opDeleteBodies(context, id + "deleteBodies1", {
                    "entities" : toDelete
                });
    }
}


// A font name, with extension ".ttf" or ".otf". To change font weight, replace "-Regular" with "-Bold", "-Italic", or "-BoldItalic".
function handleFont(fontName is FontEnumLocal, bold is boolean, italic is boolean)
{
    var weight = "-Regular";

    if (!bold && !italic)
    {

    }
    else if (bold && !italic)
    {
        weight = "-Bold";
    }
    else if (italic && !bold)
    {
        weight = "-Italic";
    }
    else if (bold && italic)
    {
        weight = "-BoldItalic";
    }

    return fontName ~ weight ~ ".ttf";

}


function isFace(context, definition)
{
    var isFace = false;

    try
    {
        isFace = size(evaluateQuery(context, qEntityFilter(definition.location, EntityType.FACE))) > 0;
    }

    return isFace;
}


export function editLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, specifiedParameters is map) returns map
{
    definition = cadsharpUrlFunctionForPreExistingEditLogic(oldDefinition, definition);

    var noScope = evaluateQuery(context, definition.mergeScope)->size() == 0;

    if (noScope && definition.booleanEnum == BooleanScopeLocal.SPLIT)
    {
        if (isFace(context, definition))
        {
            definition.mergeScope = definition.location;
        }
    }

    return definition;
}
