FeatureScript 2796;
/* Automatically generated version */
// This module is part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab for the license text.
// Copyright (c) 2013-Present PTC Inc.

import(path : "onshape/std/common.fs", version : "2796.0");
import(path : "onshape/std/coordSystem.fs", version : "2796.0");
import(path : "onshape/std/evaluate.fs", version : "2796.0");
import(path : "onshape/std/feature.fs", version : "2796.0");
// import(path : "onshape/std/formedUtils.fs", version : "2796.0");
import(path : "5418313fd7f629d9c7f1ac10", version : "b97acafda22e3375bf349519"); //modifiedFormedUtils.fs
import(path : "onshape/std/instantiator.fs", version : "2796.0");
import(path : "onshape/std/vector.fs", version : "2796.0");

/**
 * Creates a form using bodies authored in a form Part Studio and applies it to standard solid parts.
 * This feature mirrors the Sheet Metal Form tool but performs traditional boolean operations so it can be used outside sheet metal.
 */
annotation { "Feature Type Name" : "Form (part)" }
export const formedPart = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
                    "Library Definition" : "65dcc2bb2c4ff1c239467eca",
                    "Name" : "Form Part Studio",
                    "Filter" : PartStudioItemType.ENTIRE_PART_STUDIO,
                    "ComputedConfigurationInputs" : ["thickness"],
                    "MaxNumberOfPicks" : 1,
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
                }
        definition.formPartStudio is PartStudioData;
        annotation { "Name" : "Location(s)", "Filter" : BodyType.MATE_CONNECTOR || (EntityType.VERTEX && SketchObject.YES && ModifiableEntityOnly.YES) }
        definition.locations is Query;

        annotation { "Name" : "Flip direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.flipDirection is boolean;

        annotation { "Name" : "Target body/bodies", "Filter" : BodyType.SOLID && ModifiableEntityOnly.YES }
        definition.targets is Query;

        annotation { "Name" : "Form thickness", "Default" : millimeter }
        isLength(definition.thickness, LENGTH_BOUNDS);

    }
    {
        const targetSolids = definition.targets->qBodyType(BodyType.SOLID);
        if (isQueryEmpty(context, definition.locations))
        {
            throw regenError(ErrorStringEnum.FORMED_SELECT_LOCATION, ["locations"]);
        }
        if (isQueryEmpty(context, targetSolids))
        {
            throw regenError(ErrorStringEnum.BOOLEAN_NEED_ONE_SOLID, ["targets"]);
        }

        const instantiator = newInstantiator(id, {});
        const allFormedBodies = addFormInstances(context, id, definition, instantiator);

        try
        {
            debug(context, instantiator, DebugColor.RED);
            instantiate(context, instantiator);
        }
        catch
        {
            // throw regenError(ErrorStringEnum.FORMED_FAILED_TO_DERIVE, ["formPartStudio"]);
        }

        performFormBooleans(context, id, targetSolids, allFormedBodies);
    },
    { "flipDirection" : false, "targets" : qAllModifiableSolidBodies(), "thickness" : 1 * millimeter });

/**
 * Create an instance of the form Part Studio at each requested location.
 * @returns Query containing all formed tool bodies that were instantiated.
 */
function addFormInstances(context is Context, id is Id, definition is map, instantiator is Instantiator) returns Query
{
    var allFormedBodies = qNothing();
    const configurationOverride = { "thickness" : definition.thickness };

    for (var location in evaluateQuery(context, definition.locations))
    {
        var cSys = evaluateCSys(context, location);
        if (definition.flipDirection)
        {
            cSys.zAxis = -cSys.zAxis;
        }

        const formedBodies = addInstance(instantiator, definition.formPartStudio, {
                    "transform" : toWorld(cSys),
                    "identity" : location,
                    "configurationOverride" : configurationOverride,
                    "mateConnector" : qBodiesWithFormAttribute(FORM_BODY_CSYS_MATE_CONNECTOR)
                });
        allFormedBodies = qUnion(allFormedBodies, formedBodies);
    }

    return allFormedBodies;
}

/**
 * Apply the positive and negative form bodies to the target solids, then clean up helper geometry.
 */
function performFormBooleans(context is Context, id is Id, targetSolids is Query, allFormedBodies is Query)
{
    const positiveBodies = qBodiesWithFormAttribute(allFormedBodies, FORM_BODY_POSITIVE_PART);
    const negativeBodies = qBodiesWithFormAttribute(allFormedBodies, FORM_BODY_NEGATIVE_PART);
    const newBodies = qBodiesWithFormAttribute(allFormedBodies, FORM_BODY_NEW_PART);

    if (!isQueryEmpty(context, negativeBodies))
    {
        //         debug(context, positiveBodies, DebugColor.YELLOW);
        // debug(context, targetSolids, DebugColor.GREEN);
        opBoolean(context, id + "formRemove", {
                    "tools" : negativeBodies,
                    "targets" : targetSolids,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "targetsAndToolsNeedGrouping" : true
                });
        if (!isQueryEmpty(context, positiveBodies))
        {
            // debug(context, positiveBodies, DebugColor.RED);
            // debug(context, targetSolids, DebugColor.CYAN);
            opBoolean(context, id + "formAdd", {
                        "tools" : qUnion([targetSolids, positiveBodies]),
                        "targets" : targetSolids,
                        "operationType" : BooleanOperationType.UNION

                    });
        }


    }

    const cleanupBodies = qBodiesWithFormAttributes(allFormedBodies, [FORM_BODY_SKETCH_FOR_FLAT_VIEW, FORM_BODY_CSYS_MATE_CONNECTOR]);
    const unusedBodies = qSubtraction(allFormedBodies, qUnion([positiveBodies, negativeBodies, newBodies]));
    const deleteCandidates = qUnion([cleanupBodies, unusedBodies]);

    if (!isQueryEmpty(context, deleteCandidates))
    {
        opDeleteBodies(context, id + "formCleanup", { "entities" : deleteCandidates });
    }
}

/**
 * Build a coordinate system for a mate connector or sketch point location.
 */
function evaluateCSys(context is Context, location is Query) returns CoordSystem
{
    if (isQueryEmpty(context, location->qBodyType(BodyType.MATE_CONNECTOR)))
    {
        const sketchPlane = evOwnerSketchPlane(context, { "entity" : location });
        const point = evVertexPoint(context, { "vertex" : location });
        return coordSystem(point, sketchPlane.x, sketchPlane.normal);
    }

    return evMateConnector(context, { "mateConnector" : location });
}
