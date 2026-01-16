FeatureScript 2815;
/* Automatically generated version */
// This module is part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab for the license text.
// Copyright (c) 2013-Present PTC Inc.

import(path : "onshape/std/common.fs", version : "2815.0");
import(path : "onshape/std/coordSystem.fs", version : "2815.0");
import(path : "onshape/std/evaluate.fs", version : "2815.0");
import(path : "onshape/std/feature.fs", version : "2815.0");
standardFormed::import(path : "onshape/std/formedUtils.fs", version : "2815.0");
modifiedFormed::import(path : "5418313fd7f629d9c7f1ac10", version : "b97acafda22e3375bf349519"); //modifiedFormedUtils.fs
import(path : "onshape/std/instantiator.fs", version : "2815.0");
import(path : "onshape/std/vector.fs", version : "2815.0");
export import(path : "onshape/std/manipulator.fs", version : "2815.0");

const AMALGAMATE_MANIPULATOR = "amalgamateManipulator";

const AMALGAMATE_INDEX_BOUNDS =
{
    (unitless) : [0, 0, 1e5]
} as IntegerBoundSpec;

/**
 * Abuses the Sheet Metal Formed functionality to tag part studios as new, additive, and subtractive bodies for non-sheet metal parts
 * This feature mirrors the Sheet Metal Form tool but performs traditional boolean operations so it can be used outside sheet metal.
 */
annotation { "Feature Type Name" : "Amalgamate",
        "Manipulator Change Function" : "amalgamateManipulatorChange" }
export const amalgamate = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Location Index", "UIHint" : UIHint.ALWAYS_HIDDEN }
        isInteger(definition.locationIndex, AMALGAMATE_INDEX_BOUNDS);
        annotation {
                    "Library Definition" : "65dcc2bb2c4ff1c239467eca",
                    "Name" : "Amalgam Tool Part Studio",
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

        annotation { "Name" : "Subtraction Scope", "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES }
        definition.subtractionTargets is Query;

        annotation { "Name" : "Union Scope", "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES }
        definition.unionTargets is Query;
        
        annotation { "Name" : "Include bodies tagged as New", "Default" : true }
        definition.createNewBodies is boolean;
        
        annotation { "Name" : "Form thickness (only needed for sheet metal tagged form tools)", "Default" : millimeter }
        isLength(definition.thickness, LENGTH_BOUNDS);

    }
    {
        const subtractionSolids = definition.subtractionTargets;//->qBodyType(BodyType.SOLID);
        const unionSolids = definition.unionTargets;//->qBodyType(BodyType.SOLID);

        if (isQueryEmpty(context, definition.locations))
        {
            throw regenError(ErrorStringEnum.FORMED_SELECT_LOCATION, ["locations"]);
        }

        const instantiator = newInstantiator(id, {});
        
        // Instantiate first location without transform to get mate connector data
        const firstInstanceData = addFirstFormInstanceForMateConnectorData(context, id, definition, instantiator);
        
        // Add remaining instances (will compute transform after first instantiation)
        const remainingInstancesData = addRemainingFormInstancesPlaceholder(context, id, definition, instantiator);
        
        try
        {
            instantiate(context, instantiator);
        }
        catch
        {
            throw regenError(ErrorStringEnum.FORMED_FAILED_TO_DERIVE, ["formPartStudio"]);
        }
        
        // Compute mate connector transform from the first instance
        const mateConnectorData = computeMateConnectorTransformFromFirstInstance(context, definition, firstInstanceData);
        
        // Apply mate connector transform to the first instance retroactively
        // The transform is in the tool's local space, so we need to apply it in each instance's coordinate system
        if (mateConnectorData.transform != identityTransform())
        {
            // For first instance, transform in its local coordinate system
            const firstLocationTransform = toWorld(firstInstanceData.firstLocation);
            const localTransform = firstLocationTransform * mateConnectorData.transform * inverse(firstLocationTransform);
            
            opTransform(context, id + "transformFirst", {
                "bodies" : firstInstanceData.bodies,
                "transform" : localTransform
            });
            
            // Also apply transform to remaining instances in their local coordinate systems
            const locationQueries = evaluateQuery(context, definition.locations);
            for (var i = 1; i < size(locationQueries); i += 1)
            {
                var locationCSys = evaluateCSys(context, locationQueries[i]);
                if (definition.flipDirection)
                {
                    locationCSys.zAxis = -locationCSys.zAxis;
                }
                
                const locationTransform = toWorld(locationCSys);
                const localTransformRemaining = locationTransform * mateConnectorData.transform * inverse(locationTransform);
                
                opTransform(context, id + "transformRemaining" + (i-1), {
                    "bodies" : remainingInstancesData[i-1],
                    "transform" : localTransformRemaining
                });
            }
        }

        // Add manipulator for mate connector selection (only for first location)
        // This must be done before performFormBooleans as it deletes the mate connectors
        if (size(mateConnectorData.points) > 0)
        {
            addFormToolManipulator(context, id, definition, mateConnectorData.points);
        }

        // Combine first instance with remaining instances for boolean operations
        var allBodiesArray = [firstInstanceData.bodies];
        for (var remainingBodies in remainingInstancesData)
        {
            allBodiesArray = append(allBodiesArray, remainingBodies);
        }
        const allFormedBodies = qUnion(allBodiesArray);
        performFormBooleans(context, id, subtractionSolids, unionSolids, allFormedBodies, definition.createNewBodies);
    },
    {
            "locationIndex" : 0,
            "flipDirection" : false,
            "subtractionTargets" : qAllModifiableSolidBodies(),
            "unionTargets" : qAllModifiableSolidBodies(),
            "thickness" : 1 * millimeter,
            "createNewBodies" : true
        });

/**
 * Add the first instance without mate connector transform to get mate connector data.
 * @returns map with "bodies" query and "firstLocation" coordinate system
 */
function addFirstFormInstanceForMateConnectorData(context is Context, id is Id, definition is map, instantiator is Instantiator) returns map
{
    const configurationOverride = { "thickness" : definition.thickness };
    const locationQueries = evaluateQuery(context, definition.locations);
    
    if (size(locationQueries) == 0)
    {
        return { "bodies" : qNothing(), "firstLocation" : WORLD_COORD_SYSTEM };
    }
    
    // Get the first location
    const firstLocation = locationQueries[0];
    var firstLocationCSys = evaluateCSys(context, firstLocation);
    if (definition.flipDirection)
    {
        firstLocationCSys.zAxis = -firstLocationCSys.zAxis;
    }

    // Instantiate first location WITHOUT mate connector transform
    const formedBodies = addInstance(instantiator, definition.formPartStudio, {
                "transform" : toWorld(firstLocationCSys),
                "identity" : firstLocation,
                "configurationOverride" : configurationOverride,
                "mateConnector" : qBodiesWithAnyFormAttribute(modifiedFormed::FORM_BODY_CSYS_MATE_CONNECTOR)
            });
    
    return { "bodies" : formedBodies, "firstLocation" : firstLocationCSys };
}

/**
 * Compute the mate connector transform from the first instantiated instance.
 * @returns map with "transform" and "points" fields
 */
function computeMateConnectorTransformFromFirstInstance(context is Context, definition is map, firstInstanceData is map) returns map
{
    // Query mate connectors from the first instantiated form tool
    const mateConnectors = qBodiesWithAnyFormAttribute(firstInstanceData.bodies, modifiedFormed::FORM_BODY_CSYS_MATE_CONNECTOR);
    
    if (isQueryEmpty(context, mateConnectors))
    {
        return { "transform" : identityTransform(), "points" : [] };
    }
    
    const mateConnectorQueries = evaluateQuery(context, mateConnectors);
    if (size(mateConnectorQueries) == 0)
    {
        return { "transform" : identityTransform(), "points" : [] };
    }
    
    // Get coordinate systems for all mate connectors
    var mateConnectorCSys = [];
    for (var mateConnector in mateConnectorQueries)
    {
        const cSys = evMateConnector(context, { "mateConnector" : mateConnector });
        mateConnectorCSys = append(mateConnectorCSys, cSys);
    }
    
    // Clamp index to valid range
    const clampedIndex = clamp(definition.locationIndex, 0, size(mateConnectorCSys) - 1);
    
    // Compute transform from selected mate connector to origin
    const selectedMateConnectorCSys = mateConnectorCSys[clampedIndex];
    const mateConnectorTransform = fromWorld(selectedMateConnectorCSys);
    
    // Transform mate connector points to world space at the first location
    var mateConnectorPoints = [];
    for (var cSys in mateConnectorCSys)
    {
        const transformedPoint = toWorld(firstInstanceData.firstLocation) * mateConnectorTransform * cSys.origin;
        mateConnectorPoints = append(mateConnectorPoints, transformedPoint);
    }
    
    return { "transform" : mateConnectorTransform, "points" : mateConnectorPoints };
}

/**
 * Add instances for remaining locations (skip first) without transform - will apply transform later.
 * @returns Array of queries, one for each remaining location's bodies.
 */
function addRemainingFormInstancesPlaceholder(context is Context, id is Id, definition is map, instantiator is Instantiator) returns array
{
    var bodiesArray = [];
    const configurationOverride = { "thickness" : definition.thickness };
    const locationQueries = evaluateQuery(context, definition.locations);
    
    // Start from index 1 to skip the first location (already added)
    for (var i = 1; i < size(locationQueries); i += 1)
    {
        var cSys = evaluateCSys(context, locationQueries[i]);
        if (definition.flipDirection)
        {
            cSys.zAxis = -cSys.zAxis;
        }

        const formedBodies = addInstance(instantiator, definition.formPartStudio, {
                    "transform" : toWorld(cSys),
                    "identity" : locationQueries[i],
                    "configurationOverride" : configurationOverride,
                    "mateConnector" : qBodiesWithAnyFormAttribute(modifiedFormed::FORM_BODY_CSYS_MATE_CONNECTOR)
                });
        bodiesArray = append(bodiesArray, formedBodies);
    }

    return bodiesArray;
}

/**
 * Apply the positive and negative form bodies to the selected target solids, then clean up helper geometry.
 */
function performFormBooleans(context is Context, id is Id, subtractionTargets is Query, unionTargets is Query, allFormedBodies is Query, createNewBodies is boolean)
{
    const positiveBodies = qBodiesWithAnyFormAttribute(allFormedBodies, modifiedFormed::FORM_BODY_POSITIVE_PART);
    const negativeBodies = qBodiesWithAnyFormAttribute(allFormedBodies, modifiedFormed::FORM_BODY_NEGATIVE_PART);
    const newBodies = qBodiesWithAnyFormAttribute(allFormedBodies, modifiedFormed::FORM_BODY_NEW_PART);

    if (!isQueryEmpty(context, negativeBodies))
    {
        if (isQueryEmpty(context, subtractionTargets))
        {
            throw regenError(ErrorStringEnum.BOOLEAN_NEED_ONE_SOLID, ["subtractionTargets"]);
        }
        //         debug(context, positiveBodies, DebugColor.YELLOW);
        // debug(context, subtractionTargets, DebugColor.GREEN);
        booleanBodies(context, id + "formRemove", {
                    "tools" : negativeBodies,
                    "targets" : subtractionTargets,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "targetsAndToolsNeedGrouping" : true
                });
    }

    if (!isQueryEmpty(context, positiveBodies))
    {
        if (isQueryEmpty(context, unionTargets))
        {
            throw regenError(ErrorStringEnum.BOOLEAN_NEED_ONE_SOLID, ["unionTargets"]);
        }
        // debug(context, positiveBodies, DebugColor.RED);
        // debug(context, unionTargets, DebugColor.CYAN);
        opBoolean(context, id + "formAdd", {
                    "tools" : qUnion([unionTargets, positiveBodies]),
                    "targets" : unionTargets,
                    "operationType" : BooleanOperationType.UNION

                });
    }

    const cleanupBodies = qBodiesWithAnyFormAttributes(allFormedBodies, [modifiedFormed::FORM_BODY_SKETCH_FOR_FLAT_VIEW, modifiedFormed::FORM_BODY_CSYS_MATE_CONNECTOR]);
    const unusedBodies = qSubtraction(allFormedBodies, qUnion([positiveBodies, negativeBodies, (createNewBodies ? newBodies : qNothing())]));
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

/**
 * Query helper that returns bodies tagged with the requested form attribute in either the standard or modified form utilities.
 */
function qBodiesWithAnyFormAttribute(attribute is string) returns Query
{
    return qUnion([modifiedFormed::qBodiesWithFormAttribute(attribute),
                standardFormed::qBodiesWithFormAttribute(attribute)]);
}

/**
 * Filter a provided query for bodies tagged with the requested form attribute across both tag formats.
 */
function qBodiesWithAnyFormAttribute(queryToFilter is Query, attribute is string) returns Query
{
    return qUnion([modifiedFormed::qBodiesWithFormAttribute(queryToFilter, attribute),
                standardFormed::qBodiesWithFormAttribute(queryToFilter, attribute)]);
}

/**
 * Filter a provided query for any of the given form attributes using either attribute definition.
 */
function qBodiesWithAnyFormAttributes(queryToFilter is Query, attributes is array) returns Query
{
    return qUnion([modifiedFormed::qBodiesWithFormAttributes(queryToFilter, attributes),
                standardFormed::qBodiesWithFormAttributes(queryToFilter, attributes)]);
}

/**
 * Adds a manipulator to visualize and allow selection between different mate connector points
 * from the instantiated form tool. This allows users to switch which mate connector is used
 * as the placement origin when multiple mate connectors are defined in the tool.
 * Only shows manipulators for the first location to reduce draw overhead.
 */
function addFormToolManipulator(context is Context, id is Id, definition is map, mateConnectorPoints is array)
{
    if (size(mateConnectorPoints) <= 1)
    {
        // No need for manipulator if only one or no mate connectors
        return;
    }
    
    const clampedIndex = clamp(definition.locationIndex, 0, size(mateConnectorPoints) - 1);
    
    const mateConnectorManipulator = pointsManipulator({
        "points" : mateConnectorPoints,
        "index" : clampedIndex
    });
    
    addManipulators(context, id, {
        (AMALGAMATE_MANIPULATOR) : mateConnectorManipulator
    });
}

/**
 * The manipulator change function for the Amalgamate feature.
 * Updates the locationIndex when a different location point is selected.
 */
export function amalgamateManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    if (newManipulators[AMALGAMATE_MANIPULATOR] is Manipulator)
    {
        definition.locationIndex = newManipulators[AMALGAMATE_MANIPULATOR].index;
    }
    
    return definition;
}
