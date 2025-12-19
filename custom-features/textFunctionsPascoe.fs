
//_______________________________________________________________________________________________________________________________________________
//
// This FeatureScript is owned by Michael Pascoe and is distributed by CADSharp LLC. 
// You may not redistribute it for commercial purposes without the permission of said owner and CADSharp LLC. Copyright (c) 2023 Michael Pascoe.
//_______________________________________________________________________________________________________________________________________________


FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/projectiontype.gen.fs", version : "2837.0");

// CADSharp
export import(path : "cbeb3dcf671e00785597bd76/409d65a3744fe434f32bdffc/a75ab01def146a42f55baa7f", version : "381046010d5aea697e433948");
import(path : "c7c08274a0d273b9a5f5b47d/f89b4047602171084b43cd13/af743c73c677355164494e79", version : "41b9668ee979c27b9ac0cb92");//functionsPascoe.fs

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

export enum TextSourceType
{
    annotation { "Name" : "Manual text" }
    MANUAL,
    annotation { "Name" : "Part name" }
    PART_NAME,
    annotation { "Name" : "Part number" }
    PART_NUMBER,
    annotation { "Name" : "Part description" }
    PART_DESCRIPTION
}

export predicate TextMainPredicatePascoe(definition)
{
    annotation { "Name" : "Boolean enum", "Default" : BooleanScopeLocal.NEW, "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
    definition.booleanEnum is BooleanScopeLocal;

    if (definition.booleanEnum == BooleanScopeLocal.NEW)
    {
        annotation { "Name" : "Body type", "Default" : BodyOptions.SOLID, "UIHint" : [UIHint.SHOW_LABEL, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.bodyOption is BodyOptions;
    }

    annotation { "Name" : "Text source", "Default" : TextSourceType.MANUAL, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
    definition.textSourceType is TextSourceType;

    if (definition.textSourceType == TextSourceType.MANUAL)
    {
        annotation { "Name" : "Text (abc)", "Default" : "Words" }
        definition.text is string;
    }

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
    
    if (definition.booleanEnum == BooleanScopeLocal.SUBTRACT)
    {
        annotation { "Name" : "Delete island bodies", "Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.deleteIslandBodies is boolean;
    }
    
    if (definition.textSourceType != TextSourceType.MANUAL && 
        (definition.booleanEnum == BooleanScopeLocal.ADD || 
         definition.booleanEnum == BooleanScopeLocal.SUBTRACT ||
         definition.booleanEnum == BooleanScopeLocal.INTERSECT))
    {
        annotation { "Name" : "Update from part properties" }
        isButton(definition.updatePartProperties);
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
            
            // Delete island bodies created by subtraction operation
            if (definition.booleanEnum == BooleanScopeLocal.SUBTRACT && definition.deleteIslandBodies == true)
            {
                deleteIslandBodies(context, id, definition.mergeScope->qOwnerBody());
            }
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


/**
 * Check if the location query contains a face entity.
 * @param context : The current context
 * @param definition : The feature definition containing the location query
 * @returns {boolean} : True if location contains a face, false otherwise
 */
function isFace(context, definition)
{
    var isFace = false;

    try
    {
        isFace = size(evaluateQuery(context, qEntityFilter(definition.location, EntityType.FACE))) > 0;
    }

    return isFace;
}


/**
 * Check if the location query contains a mate connector.
 * @param context : The current context
 * @param definition : The feature definition containing the location query
 * @returns {boolean} : True if location contains a mate connector, false otherwise
 */
function isMateConnector(context is Context, definition is map) returns boolean
{
    var isMateConnectorResult = false;

    try
    {
        isMateConnectorResult = size(evaluateQuery(context, qBodyType(definition.location, BodyType.MATE_CONNECTOR))) > 0;
    }
    catch
    {
        // If evaluation fails, return false
        isMateConnectorResult = false;
    }

    return isMateConnectorResult;
}


/**
 * Find the face or body that the mate connector is touching.
 * Uses the mate connector's origin point to find the closest face.
 * Only returns faces belonging to modifiable SOLID or SHEET bodies.
 * @param context : The current context
 * @param mateConnectorQuery : Query for the mate connector
 * @returns {Query} : Query for the face/body at the mate connector location, or qNothing() if not found
 */
export function getFaceAtMateConnectorOrigin(context is Context, mateConnectorQuery is Query) returns Query
{
    try
    {
        // Get the mate connector's coordinate system
        const mateConnectorCoordSys = evMateConnector(context, {
                    "mateConnector" : mateConnectorQuery
                });

        // Get all valid target bodies (SOLID or SHEET, modifiable, non-construction, non-sketch)
        var validBodies = qModifiableEntityFilter(qBodyType(qEverything(EntityType.BODY), BodyType.SOLID));
        validBodies = qUnion([validBodies, 
                              qModifiableEntityFilter(qBodyType(qConstructionFilter(qSketchFilter(qEverything(EntityType.BODY), SketchObject.NO), ConstructionObject.NO), BodyType.SHEET))]);

        // Get all faces owned by valid bodies
        var allFaces = qOwnedByBody(validBodies, EntityType.FACE);

        // Find the closest face to the mate connector origin
        const closestFace = qClosestTo(allFaces, mateConnectorCoordSys.origin);

        // Check if we found a face and if it's very close (touching)
        const evaluatedFaces = evaluateQuery(context, closestFace);
        if (size(evaluatedFaces) > 0)
        {
            // Use evDistance to verify the face is actually at the mate connector origin (touching)
            const distanceResult = try silent(evDistance(context, {
                            "side0" : mateConnectorCoordSys.origin,
                            "side1" : closestFace
                        }));

            // Compare using tolerantEquals or check if distance is less than zeroLength tolerance
            if (distanceResult != undefined && distanceResult.distance < (TOLERANCE.zeroLength * meter))
            {
                return closestFace;
            }
        }
    }
    catch
    {
        // If any evaluation fails, return qNothing()
        return qNothing();
    }

    return qNothing();
}


/**
 * Delete small island bodies created by subtraction operations.
 * Identifies bodies that are disconnected from the main body and deletes them if they're smaller.
 * @param context : The current context
 * @param id : The feature id
 * @param mergeScope : Query for the bodies to check for islands
 */
function deleteIslandBodies(context is Context, id is Id, mergeScope is Query)
{
    try
    {
        const bodies = evaluateQuery(context, mergeScope);
        
        if (size(bodies) <= 1)
        {
            // No islands if only one body or no bodies
            return;
        }
        
        // Evaluate the volume/mass of each body to identify the largest (main) body
        var bodyMasses = [];
        for (var body in bodies)
        {
            try silent
            {
                const massProperties = evApproximateMassProperties(context, {
                        "entities" : body
                    });
                bodyMasses = append(bodyMasses, {
                        "body" : body,
                        "volume" : massProperties.volume
                    });
            }
        }
        
        if (size(bodyMasses) <= 1)
        {
            return;
        }
        
        // Find the largest body
        var largestBodyData = bodyMasses[0];
        for (var bodyData in bodyMasses)
        {
            if (bodyData.volume > largestBodyData.volume)
            {
                largestBodyData = bodyData;
            }
        }
        
        // Collect island bodies (all bodies except the largest)
        var islandBodies = qNothing();
        for (var bodyData in bodyMasses)
        {
            if (bodyData.body != largestBodyData.body)
            {
                islandBodies = qUnion([islandBodies, bodyData.body]);
            }
        }
        
        // Delete the island bodies
        if (!isQueryEmpty(context, islandBodies))
        {
            opDeleteBodies(context, id + "deleteIslands", {
                    "entities" : islandBodies
                });
        }
    }
    catch
    {
        // If island deletion fails, continue without deleting
        // This is not a critical error
    }
}


export function editLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, specifiedParameters is map, clickedButton is string) returns map
{
    definition = cadsharpUrlFunctionForPreExistingEditLogic(oldDefinition, definition);

    // Auto-populate merge scope when location changes and user hasn't manually specified merge scope
    if (definition.location != oldDefinition.location && !specifiedParameters.mergeScope)
    {
        const noScope = evaluateQuery(context, definition.mergeScope)->size() == 0;
        
        // Only auto-populate for operations that require merge scope
        if (noScope && (definition.booleanEnum == BooleanScopeLocal.SPLIT || 
                        definition.booleanEnum == BooleanScopeLocal.ADD || 
                        definition.booleanEnum == BooleanScopeLocal.SUBTRACT || 
                        definition.booleanEnum == BooleanScopeLocal.INTERSECT))
        {
            // If location is a face, use it directly for SPLIT operation
            if (definition.booleanEnum == BooleanScopeLocal.SPLIT && isFace(context, definition))
            {
                definition.mergeScope = definition.location;
            }
            // If location is a mate connector, find the face/body it's touching
            else if (isMateConnector(context, definition))
            {
                const faceAtMateConnector = getFaceAtMateConnectorOrigin(context, definition.location);
                if (!isQueryEmpty(context, faceAtMateConnector))
                {
                    // For SPLIT, use the face; for other operations, use the owner body
                    if (definition.booleanEnum == BooleanScopeLocal.SPLIT)
                    {
                        definition.mergeScope = faceAtMateConnector;
                    }
                    else
                    {
                        definition.mergeScope = qOwnerBody(faceAtMateConnector);
                    }
                }
            }
        }
    }
    
    // Update text from part properties when button is pressed or settings change
    if (definition.textSourceType != TextSourceType.MANUAL && 
        (definition.booleanEnum == BooleanScopeLocal.ADD || 
         definition.booleanEnum == BooleanScopeLocal.SUBTRACT ||
         definition.booleanEnum == BooleanScopeLocal.INTERSECT))
    {
        const shouldUpdate = (clickedButton == "updatePartProperties") ||
                            (definition.textSourceType != oldDefinition.textSourceType) ||
                            (definition.mergeScope != oldDefinition.mergeScope);
        
        if (shouldUpdate)
        {
            // Retrieve text from part property
            if (!isQueryEmpty(context, definition.mergeScope))
            {
                try silent
                {
                    var propertyType = PropertyType.NAME;
                    
                    if (definition.textSourceType == TextSourceType.PART_NUMBER)
                    {
                        propertyType = PropertyType.PART_NUMBER;
                    }
                    else if (definition.textSourceType == TextSourceType.PART_DESCRIPTION)
                    {
                        propertyType = PropertyType.DESCRIPTION;
                    }
                    
                    const propertyValue = getProperty(context, {
                            "entity" : definition.mergeScope,
                            "propertyType" : propertyType
                        });
                    
                    if (propertyValue != undefined && propertyValue != "")
                    {
                        definition.text = propertyValue;
                    }
                }
                catch
                {
                    // If property retrieval fails, keep existing text
                }
            }
        }
    }

    return definition;
}
