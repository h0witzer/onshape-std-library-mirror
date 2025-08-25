
//_______________________________________________________________________________________________________________________________________________
//
// This FeatureScript is owned by Michael Pascoe and is distributed by CADSharp LLC. 
// You may not redistribute it for commercial purposes without the permission of said owner and CADSharp LLC. Copyright (c) 2023 Michael Pascoe.
//_______________________________________________________________________________________________________________________________________________


FeatureScript 1847;
import(path : "onshape/std/geometry.fs", version : "1847.0");

icon::import(path : "8df84641116a406e7415a397", version : "1a6d707cb5ecf4badfd549d3");
descImage::import(path : "a78e15fc45b09cf35c3d11ac", version : "035c947d72841f5d909af7b7");

export import(path : "cbeb3dcf671e00785597bd76/144bf6a7fdc989e9e28ce5ea/a75ab01def146a42f55baa7f", version : "dc78e9b85c9f16ea9e131d3f");
export import(path : "905d9c769056ba52d974e529", version : "ba58d03914136922a2acb617");
export import(path : "c7c08274a0d273b9a5f5b47d/25bc950e6d6cf7a9a4eba62f/0abb9be049d15f1839c40841", version : "664b5281a81636b2d1fc8ff3");

export const suffix = "CADSharp";

// export enum BoxShape
// {
//     annotation {"Name" : "Square"}
//     SQUARE,
// }


annotation {
        "Feature Type Name" : "Aligned bounding box",
        "Icon" : icon::BLOB_DATA,
        "Feature Type Description" : "<b>Summary</b> <br> Creates a bounding box aligned with the selected entities. ",
        "Description Image" : descImage::BLOB_DATA,
        "Editing Logic Function" : "myEditLogic" }
export const alignedBoundingBox = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Input method", "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.inputMethod is InputMethod;

        annotation { "Name" : "Box type", "UIHint" : UIHint.SHOW_LABEL }
        definition.boxType is BoxType;

        annotation { "Name" : "Entities", "Filter" : EntityType.BODY || SketchObject.YES || EntityType.FACE || EntityType.EDGE || EntityType.VERTEX }
        definition.entities is Query;

        QFinderPredicate(definition, suffix);

        annotation { "Group Name" : "Calculations", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Calculate from", "UIHint" : UIHint.SHOW_LABEL }
            definition.calculateFrom is CalculateFrom;

            if (definition.calculateFrom == CalculateFrom.MATE_CONNECTOR)
            {
                annotation { "Name" : "Starting Coord system", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
                definition.mate is Query;
            }

            annotation { "Name" : "Rotation type", "UIHint" : UIHint.SHOW_LABEL }
            definition.rotation is Rotation;

            if (definition.rotation == Rotation.ROTATE_ALL)
            {
                if (definition.rotation == Rotation.ROTATE_ALL)
                {
                    annotation { "Name" : "Quality", "UIHint" : UIHint.SHOW_LABEL }
                    definition.quality is Quality;
                }

                annotation { "Name" : "Box change tolerance" }
                isLength(definition.tolerance, { (inch) : [0.000001, 0.00001, 10000] } as LengthBoundSpec);
            }

            annotation { "Name" : "Show calculation boxes", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.showMyWork is boolean;

            annotation { "Name" : "Show result box", "Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.showResultBox is boolean;
        }

        annotation { "Name" : "Offsets" }
        definition.offsets is boolean;

        if (definition.offsets)
        {
            annotation { "Group Name" : "Offsets group", "Driving Parameter" : "offsets", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Offset" }
                isLength(definition.offset, { (inch) : [-10000, 0, 10000] } as LengthBoundSpec);
            }
        }

        annotation { "Name" : "Intersection curves" }
        definition.intersectCurves is boolean;

        annotation { "Name" : "Keep box", "Default" : true }
        definition.keepBox is boolean;

        cadsharpUrlPredicate(definition);
    }
    {
        definition.xyzOnly = false;
        definition.entities = QFinderFunction(context, definition, suffix);

        if (!isQueryEmpty(context, definition["exclude" ~ suffix]))
            addDebugEntities(context, definition["exclude" ~ suffix], DebugColor.RED);

        AlignedBoundingBoxFunction(context, id, definition);
    });

export function qAllVertices()
{
    var vertices = qEverything(EntityType.VERTEX);
    vertices = qSubtraction(vertices, qConstructionFilter(vertices, ConstructionObject.YES));
    return vertices;
}

export function qAllSketches()
{
    const allBodies = qEverything();
    var surfaces = qSketchFilter(allBodies, SketchObject.YES);

    return surfaces;
}

export function myEditLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, specifiedParameters is map) returns map
{
    definition = cadsharpUrlFunctionForPreExistingEditLogic(oldDefinition, definition);

    definition = QFinderSetDefaultsAndVisibility(context, definition, oldDefinition, {
                "showExclude" : true,
                "showIncludeConstructionEntities" : false,
                "showIncludeSketchEntities" : false,
                "showWires" : true,
                "showEdges" : true,
                "showSurfaces" : true,
                "showFaces" : true,
                "showSolids" : true,
                "showComposites" : true,
                "showMateConnectors" : true
            }, {
                "queryFinder" : true,
                "searchType" : SearchType.EVERYTHING,
                "includeConstructionEntities" : false,
                "includeSketchEntities" : false,
                "wires" : false,
                "edges" : false,
                "surfaces" : true,
                "faces" : false,
                "solids" : true,
                "composites" : true,
                "mateConnectors" : false
            }, suffix);

    return definition;
}

