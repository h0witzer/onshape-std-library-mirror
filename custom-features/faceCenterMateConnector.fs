FeatureScript 2878;

// Simple feature that places a mate connector at the center of a face
// with the normal aligned with the face's normal at that point

import(path : "onshape/std/common.fs", version : "2878.0");
export import(path : "onshape/std/mateconnectoraxistype.gen.fs", version : "2878.0");

/**
 * Feature that places a mate connector at the center of a selected face,
 * with the Z-axis aligned to the face's normal at the center point.
 * Uses evFaceTangentPlane at parameter (0.5, 0.5) to get both the center
 * point and normal in one evaluation.
 *
 * @param id : Feature identifier
 *      @autocomplete `id + "faceCenterMateConnector1"`
 * @param definition {{
 *      @field face {Query} : The face on which to place the mate connector at its center
 *      @field flipNormal {boolean} : @optional Whether to flip the normal direction (Z-axis)
 *          Defaults to `false`.
 *      @field secondaryAxisType {MateConnectorAxisType} : @optional The secondary axis orientation
 *          Defaults to `MateConnectorAxisType.PLUS_X`.
 *      @field requireOwnerPart {boolean} : @optional Whether to require an owner part selection
 *          Defaults to `true`.
 *      @field ownerPart {Query} : @requiredIf {`requireOwnerPart` is `true`}
 *          The part to which the mate connector should be attached
 * }}
 */
annotation { "Feature Type Name" : "Face Center Mate Connector",
        "Feature Type Description" : "Place a mate connector at the center of a face with normal alignment",
        "Editing Logic Function" : "faceCenterMateConnectorEditLogic" }
export const faceCenterMateConnector = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face",
                     "Filter" : EntityType.FACE && ConstructionObject.NO,
                     "MaxNumberOfPicks" : 1 }
        definition.face is Query;

        annotation { "Name" : "Flip normal",
                     "UIHint" : ["OPPOSITE_DIRECTION", "FIRST_IN_ROW"] }
        definition.flipNormal is boolean;

        annotation { "Name" : "Reorient secondary axis",
                     "UIHint" : UIHint.MATE_CONNECTOR_AXIS_TYPE,
                     "Default" : MateConnectorAxisType.PLUS_X }
        definition.secondaryAxisType is MateConnectorAxisType;

        annotation { "Name" : "Require owner part",
                     "Default" : true }
        definition.requireOwnerPart is boolean;

        if (definition.requireOwnerPart)
        {
            annotation { "Name" : "Owner part",
                        "Filter" : EntityType.BODY && (BodyType.SOLID || GeometryType.MESH || BodyType.SHEET || BodyType.WIRE || BodyType.COMPOSITE)
                        && AllowMeshGeometry.YES && ModifiableEntityOnly.YES,
                        "MaxNumberOfPicks" : 1 }
            definition.ownerPart is Query;
        }
    }
    {
        // Verify face selection is not empty
        verifyNonemptyQuery(context, definition, "face", ErrorStringEnum.CANNOT_RESOLVE_ENTITIES);

        // Get the tangent plane at the center of the face in parameter space
        // Parameter vector(0.5, 0.5) represents the center in normalized parameter space
        // The plane's origin is at the center point and the normal is the face normal
        const tangentPlane = evFaceTangentPlane(context, {
            "face" : definition.face,
            "parameter" : vector(0.5, 0.5)
        });

        // Extract the center point from the tangent plane's origin
        const centerPoint = tangentPlane.origin;

        // Determine the primary axis (Z-axis) direction
        var primaryDirection = tangentPlane.normal;
        if (definition.flipNormal)
        {
            primaryDirection = -primaryDirection;
        }

        // Determine the secondary axis (X-axis) based on the tangent plane
        // Use the X-axis of the tangent plane as a reference
        var secondaryDirection = tangentPlane.x;

        // Create the base coordinate system for the mate connector
        // The coordSystem function takes origin, xAxis, and zAxis
        var baseCoordSystem = coordSystem(centerPoint, secondaryDirection, primaryDirection);

        // Apply secondary axis type transformation if needed
        var mateConnectorCoordSystem = baseCoordSystem;
        if (definition.secondaryAxisType == MateConnectorAxisType.PLUS_Y)
        {
            // Rotate 90 degrees to align Y axis where X was
            mateConnectorCoordSystem = coordSystem(centerPoint, yAxis(baseCoordSystem), primaryDirection);
        }
        else if (definition.secondaryAxisType == MateConnectorAxisType.MINUS_X)
        {
            // Flip the X axis
            mateConnectorCoordSystem = coordSystem(centerPoint, -baseCoordSystem.xAxis, primaryDirection);
        }
        else if (definition.secondaryAxisType == MateConnectorAxisType.MINUS_Y)
        {
            // Rotate -90 degrees to align -Y where X was
            mateConnectorCoordSystem = coordSystem(centerPoint, -yAxis(baseCoordSystem), primaryDirection);
        }

        // Verify owner part if required
        if (definition.requireOwnerPart)
        {
            verifyNonemptyQuery(context, definition, "ownerPart", ErrorStringEnum.MATECONNECTOR_OWNER_PART_NOT_RESOLVED);
        }

        // Determine the owner part query
        const ownerPartQuery = definition.requireOwnerPart ? definition.ownerPart : qNothing();

        // Create the mate connector
        opMateConnector(context, id, {
            "coordSystem" : mateConnectorCoordSystem,
            "owner" : ownerPartQuery
        });
    },
    {
        "flipNormal" : false,
        "secondaryAxisType" : MateConnectorAxisType.PLUS_X,
        "requireOwnerPart" : true,
        "ownerPart" : qNothing()
    });

/**
 * Editing logic function to automatically determine the owner part
 * when the face selection changes.
 */
export function faceCenterMateConnectorEditLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    specifiedParameters is map) returns map
{
    // If owner part wasn't explicitly specified or the face selection changed
    if (specifiedParameters.ownerPart != true ||
        (oldDefinition.face != definition.face && isQueryEmpty(context, definition.ownerPart)))
    {
        // If there's no face selected, reset owner part
        if (isQueryEmpty(context, definition.face))
        {
            definition.ownerPart = qNothing();
            return definition;
        }

        // Try to automatically determine the owner part from the face
        const ownerBody = qOwnerBody(definition.face);
        
        if (!isQueryEmpty(context, ownerBody))
        {
            definition.ownerPart = ownerBody;
        }
        else
        {
            // If we can't find an owner, check if there's only one part in the studio
            const allParts = qBodyType(qEverything(EntityType.BODY), BodyType.SOLID);
            const sheetBodies = qBodyType(qEverything(EntityType.BODY), BodyType.SHEET);
            const nonSketchSheets = qSketchFilter(sheetBodies, SketchObject.NO);
            const nonConstructionSheets = qConstructionFilter(nonSketchSheets, ConstructionObject.NO);
            const allSurfaces = qModifiableEntityFilter(nonConstructionSheets);
            const allBodies = qUnion([allParts, allSurfaces]);
            
            if (size(evaluateQuery(context, allBodies)) == 1)
            {
                definition.ownerPart = allBodies;
            }
            else
            {
                definition.ownerPart = qNothing();
            }
        }
    }
    
    return definition;
}
