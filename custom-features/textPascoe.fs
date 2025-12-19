
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
import(path : "c7c08274a0d273b9a5f5b47d/f89b4047602171084b43cd13/af743c73c677355164494e79", version : "41b9668ee979c27b9ac0cb92");//functionsPascoe.fs
export import(path : "12312312345abcabcabcdeff/8e6d551b2d41cf932b1681f6/ec739638f0a117a475a744af", version : "312fee4ee687399466575a10");
export import(path : "5d523046fa535976e27cb329", version : "a0d2d211a3e891400f53d991");//textFunctionsPascoe.fs
icon::import(path : "4cba10e99d49d95b30708864", version : "7882b03d25a2320567ce70df");

annotation {
        "Feature Type Name" : "Text",
        "Icon" : icon::BLOB_DATA,
        "Description Image" : cadsharpLogo::BLOB_DATA,
        "Feature Type Description" : "<b> Summary </b> <br> Creates text. <br>",
        "Editing Logic Function" : "editLogic" }
export const text = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        TextMainPredicatePascoe(definition);
        TextSettingsPredicatePascoe(definition);
    }
    {
        TextFunctionPascoe(
            context,
            id,
            definition.booleanEnum,
            definition.bodyOption,
            definition.text,
            definition.location,
            definition.mergeScope,
            definition.depth,
            definition.oppositeDirection,
            definition.textHeight,
            definition.font,
            definition.bold,
            definition.italic,
            definition.mirrorHorizontal,
            definition.mirrorVertical);

    });

