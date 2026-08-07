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
export import(path : "onshape/std/mateconnectoraxistype.gen.fs", version : "2815.0");
modifiedFormed::import(path : "5418313fd7f629d9c7f1ac10", version : "b97acafda22e3375bf349519"); //modifiedFormedUtils.fs
import(path : "onshape/std/instantiator.fs", version : "2815.0");
import(path : "onshape/std/vector.fs", version : "2815.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2815.0");
import(path : "onshape/std/holeAttribute.fs", version : "2815.0");

/**
 * Variable name used to store and retrieve the custom feature name for Amalgamate.
 * This constant must match the corresponding constant in amalgamTag.fs.
 * Variables can be retrieved from the source Part Studio context using buildFunction.
 */
const AMALGAM_FEATURE_NAME_VAR = "amalgamFeatureName";

/**
 * Separator used between "Amalgamate" and the custom feature name in the feature tree display.
 */
const FEATURE_NAME_SEPARATOR = " - ";

/**
 * Abuses the Sheet Metal Formed functionality to tag part studios as new, additive, and subtractive bodies for non-sheet metal parts
 * This feature mirrors the Sheet Metal Form tool but performs traditional boolean operations so it can be used outside sheet metal.
 *
 * Feature Name Template: The template "Amalgamate#featureName" references the computed parameter 'featureName'.
 * Onshape will substitute #featureName with the actual value at runtime, displaying as "Amalgamate - [custom name]"
 * or just "Amalgamate" if featureName is empty.
 */
annotation { "Feature Type Name" : "Amalgamate", "Feature Name Template" : "Amalgamate#featureName" }
export const amalgamate = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
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

        annotation { "Name" : "Flip direction", "UIHint" : [UIHint.OPPOSITE_DIRECTION, UIHint.FIRST_IN_ROW] }
        definition.flipDirection is boolean;

        annotation { "Name" : "Reorient secondary axis", "UIHint" : UIHint.MATE_CONNECTOR_AXIS_TYPE }
        definition.secondaryAxisType is MateConnectorAxisType;

        annotation { "Name" : "Subtraction Scope", "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES }
        definition.subtractionTargets is Query;

        annotation { "Name" : "Union Scope", "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES }
        definition.unionTargets is Query;

        annotation { "Name" : "Include bodies tagged as New", "Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        definition.createNewBodies is boolean;

        annotation { "Name" : "Form thickness (only needed for sheet metal tagged form tools)", "Default" : millimeter, "UIHint" : UIHint.ALWAYS_HIDDEN }
        isLength(definition.thickness, LENGTH_BOUNDS);

        // Hidden computed parameter used for Feature Name Template. Not a user input.
        // This field is populated at runtime from the variable stored by Amalgam Tag.
        annotation { "Name" : "Feature name (computed)", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.featureName is string;

    }
    {
        const subtractionSolids = definition.subtractionTargets; //->qBodyType(BodyType.SOLID);
        const unionSolids = definition.unionTargets; //->qBodyType(BodyType.SOLID);

        if (isQueryEmpty(context, definition.locations))
        {
            throw regenError(ErrorStringEnum.FORMED_SELECT_LOCATION, ["locations"]);
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
            throw regenError(ErrorStringEnum.FORMED_FAILED_TO_DERIVE, ["formPartStudio"]);
        }

        performFormBooleans(context, id, subtractionSolids, unionSolids, allFormedBodies, definition.createNewBodies);

        // Retrieve the feature name from the source Part Studio context.
        // Call buildFunction to get the source context, then getVariable to retrieve the name.
        // This works because variables are stored in the Part Studio context.
        var featureName = "";
        try silent
        {
            // Build the source Part Studio context with its configuration
            // Use the same configuration that was used for instantiation
            var sourceConfig = {};
            if (definition.formPartStudio.configuration != undefined)
            {
                sourceConfig = definition.formPartStudio.configuration;
            }

            // Call buildFunction to create the source Part Studio context
            const sourceContext = definition.formPartStudio.buildFunction(sourceConfig);

            // Retrieve the variable from that context
            const retrievedName = getVariable(sourceContext, AMALGAM_FEATURE_NAME_VAR);

            // Type check and format
            if (retrievedName != undefined && retrievedName is string && retrievedName != "")
            {
                featureName = FEATURE_NAME_SEPARATOR ~ retrievedName;
            }
        }
        setFeatureComputedParameter(context, id, { "name" : "featureName", "value" : featureName });
    },
    {
            "flipDirection" : false,
            "secondaryAxisType" : MateConnectorAxisType.PLUS_X,
            "subtractionTargets" : qAllModifiableSolidBodies(),
            "unionTargets" : qAllModifiableSolidBodies(),
            "thickness" : 1 * millimeter,
            "createNewBodies" : true,
            "featureName" : ""
        });

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

        // Apply secondary axis rotation around Z-axis
        // Based on implementation from Point Derive and standard library transformation features
        var xAxis = cSys.xAxis;
        var zAxis = cSys.zAxis;

        if (definition.secondaryAxisType == MateConnectorAxisType.PLUS_Y)
        {
            xAxis = cross(zAxis, xAxis);
        }
        else if (definition.secondaryAxisType == MateConnectorAxisType.MINUS_X)
        {
            xAxis = -xAxis;
        }
        else if (definition.secondaryAxisType == MateConnectorAxisType.MINUS_Y)
        {
            xAxis = -cross(zAxis, xAxis);
        }
        // PLUS_X is the default, no change needed

        cSys = coordSystem(cSys.origin, xAxis, zAxis);

        const formedBodies = addInstance(instantiator, definition.formPartStudio, {
                    "transform" : toWorld(cSys),
                    "identity" : location,
                    "configurationOverride" : configurationOverride,
                    "mateConnector" : qBodiesWithAnyFormAttribute(modifiedFormed::FORM_BODY_CSYS_MATE_CONNECTOR)
                });
        allFormedBodies = qUnion(allFormedBodies, formedBodies);
    }

    return allFormedBodies;
}

/**
 * Apply the positive and negative form bodies to the selected target solids, then clean up helper geometry.
 * When subtraction tool bodies carry HoleAttribute annotation data and the target is an active sheet metal
 * model, propagates those hole attributes to the resulting sheet metal geometry to support hole tables
 * and drawing annotations on sheet metal parts.
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

        // Determine whether any subtraction targets are active sheet metal bodies. If so, collect hole
        // annotation attributes from the tool bodies before the boolean consumes them. These attributes
        // will be propagated to the resulting sheet metal geometry after the boolean.
        const smQueries = separateSheetMetalQueries(context, subtractionTargets);
        const hasSheetMetalTargets = !isQueryEmpty(context, smQueries.sheetMetalQueries);
        var holeAttributesFromTools = [];
        if (hasSheetMetalTargets)
        {
            holeAttributesFromTools = getHoleAttributes(context, qOwnedByBody(negativeBodies, EntityType.FACE));
        }

        booleanBodies(context, id + "formRemove", {
                    "tools" : negativeBodies,
                    "targets" : subtractionTargets,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "targetsAndToolsNeedGrouping" : true
                });

        // After the subtraction, propagate hole attributes to any sheet metal targets whose corresponding
        // tool bodies carried HoleAttribute annotation data (e.g. from a notHole.fs tool).
        if (hasSheetMetalTargets && holeAttributesFromTools != [])
        {
            propagateHoleAttributesToSheetMetal(context, id + "formRemove", smQueries.sheetMetalQueries, holeAttributesFromTools, id);
        }
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
 * Propagates hole attributes from subtraction tool bodies to sheet metal targets after a boolean subtraction.
 * Mirrors the attribute mapping behavior of the standard hole feature for sheet metal parts, ensuring that
 * hole tables and drawing annotations are populated for holes cut via Amalgamate into sheet metal bodies.
 *
 * When booleanBodies subtracts a notHole.fs tool from a sheet metal solid, corresponding circular edges
 * are created in the underlying SM definition (SHEET) bodies. This function finds those edges, resolves
 * their associated SM faces via SM association attributes, evaluates the cylinder geometry to determine
 * hole diameter, and applies the best-matching HoleAttribute from the original tool bodies.
 *
 * @param booleanId {Id} : Operation ID of the subtraction boolean, used to find newly created edges.
 * @param sheetMetalTargets {Query} : Active sheet metal solid bodies that were subtracted from.
 * @param holeAttributesFromTools {array} : HoleAttribute objects read from the negative tool body faces before the boolean.
 * @param topLevelId {Id} : Top-level feature ID for error reporting context.
 */
function propagateHoleAttributesToSheetMetal(context is Context, booleanId is Id, sheetMetalTargets is Query,
    holeAttributesFromTools is array, topLevelId is Id)
{
    // Collect the underlying SM definition (SHEET) bodies for all affected SM targets.
    // These are the hidden master bodies that track the sheet metal geometry state.
    var sheetMetalModels = qNothing();
    for (var smBody in evaluateQuery(context, sheetMetalTargets))
    {
        sheetMetalModels = qUnion([sheetMetalModels, getSheetMetalModelForPart(context, smBody)]);
    }

    if (isQueryEmpty(context, sheetMetalModels))
    {
        return;
    }

    // When booleanBodies operates on an SM solid body, Onshape creates corresponding edges in the
    // underlying SHEET definition body. Query for circular/arc edges created by this boolean in those models.
    const createdEdges = qBodyType(qCreatedBy(booleanId, EntityType.EDGE), BodyType.SHEET);
    const smEdges = qOwnedByBody(createdEdges, sheetMetalModels);
    const circularEdges = qUnion([qGeometry(smEdges, GeometryType.CIRCLE), qGeometry(smEdges, GeometryType.ARC)]);

    const circularEdgesEvaluated = evaluateQuery(context, circularEdges);
    if (circularEdgesEvaluated == [])
    {
        return;
    }

    for (var circularEdge in circularEdgesEvaluated)
    {
        // SM association attributes link edges in the SHEET model to corresponding faces in the definition body.
        // We use this to find the faces to attribute with hole data, mirroring assignSheetMetalHoleAttributes.
        var associations = getSMAssociationAttributes(context, circularEdge);
        for (var association in associations)
        {
            // Resolve associated SM faces. Includes private patch faces so attribute propagates downstream.
            const holeFacesQ = qEntityFilter(qAttributeQuery(association), EntityType.FACE);
            const holeFaces = evaluateQuery(context, holeFacesQ);
            if (size(holeFaces) == 0)
            {
                continue;
            }

            // Evaluate the surface geometry of the associated face to get the cylinder radius.
            // Sheet metal holes always produce cylindrical faces in the definition body.
            var cylinder = evSurfaceDefinition(context, { "face" : holeFacesQ });
            if (!(cylinder is Cylinder))
            {
                continue;
            }
            const holeDiameter = cylinder.radius * 2;

            // Match the best HoleAttribute from the original tool bodies based on hole diameter.
            // This correctly handles multiple tool bodies with different hole sizes in one Amalgamate operation.
            const matchedAttribute = findMatchingHoleAttribute(holeAttributesFromTools, holeDiameter);
            if (matchedAttribute == undefined)
            {
                continue;
            }

            // Apply the matched hole attribute to the circular edge and the associated SM faces.
            // The attribute carries all annotation data needed for hole tables and drawing callouts.
            try
            {
                setAttribute(context, {
                            "entities" : qUnion([circularEdge, holeFacesQ]),
                            "attribute" : matchedAttribute
                        });
            }
            catch
            {
                reportFeatureInfo(context, topLevelId, ErrorStringEnum.HOLE_PARTIAL_FAILURE);
            }
        }
    }
}

/**
 * Returns the HoleAttribute from an array whose holeDiameter is closest to the given target diameter.
 * When there is only one attribute (the common case), it is returned regardless of diameter.
 * Returns undefined if the array is empty or no attributes contain a holeDiameter field.
 *
 * @param holeAttributes {array} : Array of HoleAttribute objects to search (from getHoleAttributes).
 * @param targetDiameter {ValueWithUnits} : Hole diameter evaluated from the SM cylinder face geometry.
 */
function findMatchingHoleAttribute(holeAttributes is array, targetDiameter)
{
    if (size(holeAttributes) == 0)
    {
        return undefined;
    }

    var bestMatch = undefined;
    var bestDiff = undefined;

    for (var attribute in holeAttributes)
    {
        if (attribute.holeDiameter == undefined)
        {
            continue;
        }
        const diff = abs(attribute.holeDiameter - targetDiameter);
        if (bestDiff == undefined || diff < bestDiff)
        {
            bestDiff = diff;
            bestMatch = attribute;
        }
    }

    return bestMatch;
}
