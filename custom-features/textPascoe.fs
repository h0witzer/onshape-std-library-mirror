
//_______________________________________________________________________________________________________________________________________________
//
// This FeatureScript is owned by Michael Pascoe and is distributed by CADSharp LLC. 
// You may not redistribute it for commercial purposes without the permission of said owner and CADSharp LLC. Copyright (c) 2023 Michael Pascoe.
//_______________________________________________________________________________________________________________________________________________


FeatureScript 2260;
import(path : "onshape/std/common.fs", version : "2260.0");

// Tolerance for detecting if a mate connector is touching a face
// This is used in the editLogic to determine proximity
const MATE_CONNECTOR_TOUCHING_TOLERANCE = 1e-5 * meter;

// CADSharp
export import(path : "cbeb3dcf671e00785597bd76/409d65a3744fe434f32bdffc/a75ab01def146a42f55baa7f", version : "381046010d5aea697e433948");

// export import(path : "c7c08274a0d273b9a5f5b47d/16aa9873224c4a0cc2412bfd/6b9d1a8ad65ac54fe2125581", version : "b1c9eb4800a01f8a29c6c12a");
// export import(path : "c7c08274a0d273b9a5f5b47d/16aa9873224c4a0cc2412bfd/5859f548d83656e6f7c0d427", version : "5474fc5269dcc948135c4857");
import(path : "c7c08274a0d273b9a5f5b47d/f89b4047602171084b43cd13/af743c73c677355164494e79", version : "41b9668ee979c27b9ac0cb92");
export import(path : "12312312345abcabcabcdeff/8e6d551b2d41cf932b1681f6/ec739638f0a117a475a744af", version : "312fee4ee687399466575a10");
export import(path : "b2fd066143872bf1cd73ced7", version : "21c1652f0a3e50a5aa263d9f");
icon::import(path : "8842c738c4b2ed64b2c83a97", version : "2370ac0bd968616284b5a4ef");

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

/**
 * Editing logic function for the text feature.
 * Automatically detects and populates the mergeScope when a mate connector is selected
 * for the location input by finding the face or body that the mate connector is touching.
 * 
 * This function is called whenever the feature definition changes in the UI.
 * It automatically fills in the mergeScope field when a mate connector is selected
 * for the location, similar to how the sheet metal tab feature handles wiring.
 */
export function editLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    specifiedParameters is map, hiddenBodies is Query) returns map
{
    // Only auto-populate mergeScope if the location has changed and mergeScope was not manually specified
    if (definition.location != oldDefinition.location && !specifiedParameters.mergeScope)
    {
        // Check if the location query contains a mate connector
        const mateConnectorQuery = qBodyType(definition.location, BodyType.MATE_CONNECTOR);
        const mateConnectorEntities = evaluateQuery(context, mateConnectorQuery);
        
        if (size(mateConnectorEntities) > 0)
        {
            // First approach: Try to get the owner body directly from the mate connector
            // This should return the body/part that the mate connector is attached to
            const ownerBodyQuery = qOwnerBody(mateConnectorQuery);
            const ownerBodies = try silent(evaluateQuery(context, ownerBodyQuery));
            
            if (ownerBodies != undefined && size(ownerBodies) > 0)
            {
                // Successfully found the owner body - use it as the merge scope
                definition.mergeScope = ownerBodyQuery;
            }
            else
            {
                // Fallback approach: Find the closest face to the mate connector's position
                // and use its owner body as the merge scope
                const mateConnectorCoordSys = try silent(evMateConnector(context, {
                    "mateConnector" : mateConnectorQuery
                }));
                
                if (mateConnectorCoordSys != undefined)
                {
                    // Get visible solid bodies (excluding hidden bodies and non-solid types)
                    const visibleSolidBodies = qSubtraction(
                        qBodyType(qEverything(EntityType.BODY), BodyType.SOLID),
                        hiddenBodies
                    );
                    
                    // Get faces owned by these solid bodies for better performance
                    const visibleFaces = qOwnedByBody(visibleSolidBodies, EntityType.FACE);
                    
                    // Find the closest face to the mate connector's origin point
                    const closestFaceQuery = qClosestTo(visibleFaces, mateConnectorCoordSys.origin);
                    const closestFaces = try silent(evaluateQuery(context, closestFaceQuery));
                    
                    if (closestFaces != undefined && size(closestFaces) > 0)
                    {
                        // Check if the mate connector is actually touching/near the face
                        // by checking the distance is very small (within tolerance)
                        const distanceResult = try silent(evDistance(context, {
                            "side0" : mateConnectorCoordSys.origin,
                            "side1" : closestFaceQuery
                        }));
                        
                        if (distanceResult != undefined && distanceResult.distance < MATE_CONNECTOR_TOUCHING_TOLERANCE)
                        {
                            // Get the body that owns this face
                            const ownerBodyFromFace = qOwnerBody(closestFaceQuery);
                            definition.mergeScope = ownerBodyFromFace;
                        }
                    }
                }
            }
        }
    }
    
    return definition;
}

