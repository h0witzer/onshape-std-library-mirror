FeatureScript 1660;
import(path : "onshape/std/common.fs", version : "1660.0");
export import(path : "4c21d0c3c89c0a81aadfdac6/339ad59968f272385a348f5e/2a1cde3849e2f7d4ef1b6676", version : "642a153807b1078b5ec27945");

export import(path : "onshape/std/manipulator.fs", version : "1660.0");
export import(path : "onshape/std/boolean.fs", version : "1660.0");

export import(path : "e4756c88ac6f6b2cc1eb915e", version : "7616d6f438bbdcf11ebefed8");

export const INDEX_BOUNDS =
{
            (unitless) : [0, 0, 1e5]
        } as IntegerBoundSpec;

export predicate locationsPredicate(definition is map)
{
    annotation { "Name" : "Locations", "Filter" : (EntityType.VERTEX && SketchObject.YES) || BodyType.MATE_CONNECTOR }
    definition.locations is Query;

    // annotation { "Name" : "Primary axis", "UIHint" : ["SHOW_LABEL", "DISPLAY_SHORT"] }
    // definition.primaryAxis is PrimaryAxis;

    annotation { "Name" : "Flip primary axis", "UIHint" : ["PRIMARY_AXIS", "FIRST_IN_ROW"] }
    definition.flipPrimaryAxis is boolean;

    annotation { "Name" : "Reorient secondary axis", "UIHint" : ["MATE_CONNECTOR_AXIS_TYPE"] }
    definition.secondaryAxisType is MateConnectorAxisType;
}

/**
 * A feature performing [opPointDerive].
 *
 * @param definition {{
 *          @field index {number} : @optional
 *                  The index of the currently selected point. Automatically filled in by `pointDeriveManipulatorChange`.
 *          @field operationType {NewBodyOperationType} : @optional
 *                  The boolean operation to apply to imported parts.
 *                  Defaults to `NewBodyOperationType.NEW`.
 *          @field entitiesToImport {PartStudioData} :
 *                  `PartStudioData` representing one or more entities to import.
 *          @field locations {Query} : @optional
 *                  One or more sketch vertices or mate connectors to use as locations to derive parts to.
 *                  If undefined, the entities are placed at the `WORLD_ORIGIN`.
 *                  Defaults to `qNothing()`.
 *          @field flipPrimaryAxis {boolean} : @optional
 *                  Whether to flip the primary axis of derived parts.
 *                  Defaults to `false`.
 *          @field secondaryAxisType {MateConnectorAxisType} : @optional
 *                  The secondary axis to use when orienting derived parts.
 *                  Defaults to `MateConnectorAxisType.PLUS_X`.
 *          @field transform {boolean} : @optional
 *              Whether to change the origin position with `translationX`, `translationY`, `translationZ`, 'rotationType', and `rotation`.
 *              Defaults to `false`.
 *          @field translationX {ValueWithUnits} : @requiredIf {`transform` is `true`}
 *                  Distance to move the resulting origin along the world X direction.
 *          @field translationY {ValueWithUnits} : @requiredIf {`transform` is `true`}
 *                  Distance to move the resulting origin along the world Y direction.
 *          @field translationZ {ValueWithUnits} : @requiredIf {`transform` is `true`}
 *                  Distance to move the resulting origin along the world Z direction.
 *          @field rotationType {RotationType} : @optional
 *                  The axis to rotate around.
 *                  Defaults to `RotationType.ABOUT_Z`.
 *          @field rotation {ValueWithUnits} : @optional
 *                  The angle to rotate by.
 *                  Defaults to `0 * degree`.
 *          @field deletePlanesAndSketches {boolean} : @optional
 *                  Whether to delete planes and sketches in `entitiesToImport`.
 *                  Defaults to `true`.
 *          @field deleteMateConnectors {boolean} : @optional
 *                  Whether to delete mate connectors in `entitiesToImport` after they have been instantiated as points.
 *                  Defaults to `true`.
 *          @field defaultScope {boolean} : @requiredif `operationType != NewBodyOpertionType.NEW && booleanScope == undefined`
 *                  Whether to boolean bodies to everything in the part studio.
 *                  Defaults to `false`.
 *          @field booleanScope {Query} : @requiredif `operationType != NewBodyOperationType.NEW && defaultScope == false`
 *                  Bodies in context to boolean to.
 * }}
 */
annotation { "Feature Type Name" : "Point derive",
        // "Editing Logic Function" : "pointDeriveEditLogic",
        "Manipulator Change Function" : "pointDeriveManipulatorChange",
        "Feature Type Description" : "Derive parts to specific locations in part studios using mate connectors attatched to those parts.<br>" ~
        "For full documentation, visit: <br>" ~
        "alexkempen.github.io/alex-featurescript-docs<br>" ~
        "FeatureScript by Alex Kempen."
    }
export const pointDerive = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Index", "UIHint" : ["ALWAYS_HIDDEN"] }
        isInteger(definition.index, INDEX_BOUNDS);

        booleanStepTypePredicate(definition);

        annotation { "Name" : "Entities to import", "ComputedConfigurationInputs" : ["length"] }
        definition.partStudioData is PartStudioData;

        locationsPredicate(definition);
        
        annotation { "Name" : "Move" }
        definition.transform is boolean;

        if (definition.transform)
        {
            annotation { "Name" : "X translation" }
            isLength(definition.translationX, ZERO_DEFAULT_LENGTH_BOUNDS);

            annotation { "Name" : "Y translation" }
            isLength(definition.translationY, ZERO_DEFAULT_LENGTH_BOUNDS);

            annotation { "Name" : "Z translation" }
            isLength(definition.translationZ, ZERO_DEFAULT_LENGTH_BOUNDS);

            annotation { "Name" : "Rotation axis", "Default" : RotationType.ABOUT_Z }
            definition.rotationType is RotationType;

            annotation { "Name" : "Rotation angle" }
            isAngle(definition.rotation, ANGLE_360_ZERO_DEFAULT_BOUNDS);
        }

        annotation { "Name" : "Delete planes and sketches", "Default" : true, "UIHint" : ["REMEMBER_PREVIOUS_VALUE"] }
        definition.deletePlanesAndSketches is boolean;

        annotation { "Name" : "Delete mate connectors", "Default" : true, "UIHint" : ["REMEMBER_PREVIOUS_VALUE"] }
        definition.deleteMateConnectors is boolean;

        booleanStepScopePredicate(definition);
    }
    {
        verifyNonemptyStudioReference(context, definition, "partStudioData", ErrorStringEnum.IMPORT_DERIVED_NO_PARTS);

        const remainingTransform = getRemainderPatternTransform(context, { "references" : definition.locations });

        if (definition.deletePlanesAndSketches)
        {
            definition.partStudioData = removePlanesAndSketches(definition.partStudioData);
        }

        definition = adjustPointDeriveDefinitionForOp(context, definition);
        
        definition.importQuery = instantiatePointDerivePartStudio(context, id, definition.partStudioData);

        const pointDeriveResult = opPointDerive(context, id, definition);

        addPointDeriveManipulator(context, id, definition, {
                    "manipulatorKey" : POINT_DERIVE_MANIPULATOR,
                    "points" : pointDeriveResult.points,
                    "index" : pointDeriveResult.index
                });

        if (definition.deleteMateConnectors && !isQueryEmpty(context, qCreatedBy(id, EntityType.BODY)->qBodyType(BodyType.MATE_CONNECTOR)))
        {
            opDeleteBodies(context, id + "deleteTools", { "entities" : qCreatedBy(id, EntityType.BODY)->qBodyType(BodyType.MATE_CONNECTOR) });
        }

        transformResultIfNecessary(context, id, remainingTransform);

        if (!isQueryEmpty(context, qCreatedBy(id, EntityType.BODY)->qBodyType(BodyType.SOLID)))
        {
            const reconstructOp = function(id)
                {
                    opPointDerive(context, id, definition);
                };
            // since processNewBodyIfNeeded throws ErrorStringEnum warnings, and those are broken while a part studio entity is selected,
            // most errors will not get reported properly (even though you would otherwise expect them to).
            // See also: https://forum.onshape.com/discussion/16063/using-a-part-studio-reference-parameter-breaks-errorstringenum-functionality
            processNewBodyIfNeeded(context, id, definition, reconstructOp);
        }
    },
    {
            index : 0,
            operationType : NewBodyOperationType.NEW,
            locations : qNothing(),
            primaryAxis : PrimaryAxis.Z,
            flipPrimaryAxis : false,
            secondaryAxisType : MateConnectorAxisType.PLUS_X,
            "transform" : false,
            rotationType : RotationType.ABOUT_Z,
            rotation : 0 * degree,
            deletePlanesAndSketches : true,
            deleteMateConnectors : true,
            defaultScope : false,
            booleanScope : qNothing()
        }); // end of feature definition

/**
* Adjusts the `definition` of a `pointDerive` feature to be suitiable for passing into `opPointDerive`.
 */
function adjustPointDeriveDefinitionForOp(context is Context, definition is map) returns map
{
    definition.rotation = adjustAngle(context, definition.rotation);
     
    if (isQueryEmpty(context, definition.locations))
    {
        definition.identities = [qCreatedBy(makeId("Origin"), EntityType.VERTEX)];
        definition.locations = [WORLD_COORD_SYSTEM];
    }
    else
    {
        definition.identities = evaluateQuery(context, definition.locations);
        definition.locations = mapArray(definition.identities, function(query)
            {
                // evVertexCoordSystem is one of my personal library functions
                return evVertexCoordSystem(context, { "vertex" : query });
            });
    }

    return definition;
}

/**
 * @internal
 * The editing logic function for the [pointDerive] feature.
 * Currently unused since part studios cannot be imported in edit logic, meaning boolean scope autofill behavior cannot be implemented.
 */
// export function pointDeriveEditLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean)
// {
//     return definition;
// }

/**
 * @internal
 * The manipulator change function for the [pointDerive] feature.
 */
export function pointDeriveManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    if (newManipulators[POINT_DERIVE_MANIPULATOR] is Manipulator)
    {
        definition.index = newManipulators[POINT_DERIVE_MANIPULATOR].index;
    }

    return definition;
}
