FeatureScript 2716;
import(path : "onshape/std/common.fs", version : "2716.0");
import(path : "onshape/std/boolean.fs", version : "2716.0");
import(path : "onshape/std/hole.fs", version : "2716.0");
import(path : "onshape/std/formedUtils.fs", version : "2716.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2716.0");

annotation { "Feature Type Name" : "Cut out",  "Editing Logic Function" : "cutOutEditLogic"}
export const sheetMetalCutOut = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
         annotation {
            "Library Definition" : "65dcc2bb2c4ff1c239467eca", // This is the id of the Onshape Form Library definition
            "Name" : "Form Part Studio",
            "Filter" : PartStudioItemType.ENTIRE_PART_STUDIO,
            "ComputedConfigurationInputs" : [ "thickness" ],
            "MaxNumberOfPicks" : 1,
            "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
        }
        definition.formPartStudio is PartStudioData;
        annotation { "Name" : "Location(s)", "Filter" : BodyType.MATE_CONNECTOR || (EntityType.VERTEX && SketchObject.YES && ModifiableEntityOnly.YES) }
        definition.locations is Query;
        
        annotation { "Name" : "Target part(s)", "Filter" : EntityType.BODY && ActiveSheetMetal.YES && ModifiableEntityOnly.YES }
        definition.targetBodies is Query;
    }
    {
        checkNotInFeaturePattern(context, qUnion(definition.targetBodies, definition.locations), ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);

        if (isQueryEmpty(context, definition.locations))
        {
            throw regenError(ErrorStringEnum.FORMED_SELECT_LOCATION, ["locations"]);
        }
        if (isQueryEmpty(context, definition.targetBodies))
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_SELECT_PARTS, ["targetBodies"]);
        }

        if (definition.formPartStudio.partQuery == undefined || definition.formPartStudio.partQuery.subqueries == [])
        {
            throw regenError(ErrorStringEnum.FORMED_NO_PART_STUDIO_SELECTED, ["formPartStudio"]);
        }

        const instantiator = newInstantiator(id, {});
        const targetBodyToCutOuts = checkLocationsAndAddInstances(context, id, definition, instantiator);
        try
        {
            instantiate(context, instantiator);
        }
        catch //details will be in FS notices
        {
            throw regenError(ErrorStringEnum.FORMED_FAILED_TO_DERIVE, ["formPartStudio"]);
        }
      
        var cutCount = 0;
        const cutId = id + "cut";
        for ( var bodyAndCutOuts in targetBodyToCutOuts)
        {
            booleanBodies(context, cutId + unstableIdComponent(cutCount), 
                { "tools" : qUnion(bodyAndCutOuts.value), 
                  "operationType" : BooleanOperationType.SUBTRACTION,
                  "targets" : bodyAndCutOuts.key });
            cutCount += 1;
        }
    });
    
    
    function checkLocationsAndAddInstances(context is Context, id is Id, definition is map, instantiator is Instantiator) returns map
    {
        var addInstancePartStudio = definition.formPartStudio;
        addInstancePartStudio.partQuery = qBodiesWithFormAttributes(definition.formPartStudio.partQuery, [FORM_BODY_NEGATIVE_PART]);
        var mateConnectorsNotOnFace = [];
        var targetBodyToCutOuts = {};
        for (var location in evaluateQuery(context, definition.locations))
        {
            var cSys = evaluateCSys(context, location);
            const faces = evaluateQuery(context, definition.targetBodies->qOwnedByBody(EntityType.FACE)->qContainsPoint(cSys.origin));
            const nFaces = size(faces);
            if (nFaces == 0)
            {
                mateConnectorsNotOnFace = append(mateConnectorsNotOnFace, location);
                continue;
            }
            else if (nFaces > 1)
            {
                throw regenError(ErrorStringEnum.FORMED_LOCATION_ON_MULTIPLE_FACES, ["locations", "targetBodies"], qUnion(location, qUnion(faces)));
            }
            const definitionEntities = getSMDefinitionEntities(context, faces[0]);
            if (size(definitionEntities) != 1)
            {
                throw regenError(ErrorStringEnum.FORMED_NOT_ON_HOLE_FORMED_FACE, ["locations"], location);
            }
            const smDefinitionModels = evaluateQuery(context, qOwnerBody(definitionEntities[0]));
            if (size(smDefinitionModels) != 1)
            {
                throw "size(smDefinitionModels) is " ~ size(smDefinitionModels);
            }
            const smAttributes = getSmObjectTypeAttributes(context, smDefinitionModels[0], SMObjectType.MODEL);
            if (size(smAttributes) != 1)
            {
                throw "Found model with more than one SMObjectType.MODEL attribute";
            }
            const thickness = smAttributes[0].frontThickness == undefined ? smAttributes[0].backThickness : smAttributes[0].frontThickness;
            if (thickness == undefined || thickness.value == undefined)
            {
                throw "Thickness of sheet metal model undefined";
            }

            const formedBodies = addInstance(instantiator, addInstancePartStudio, {
                                    "transform" : toWorld(cSys),
                                    "identity" : location,
                                    "configurationOverride" : { "thickness" : thickness.value },
                                    "mateConnector" : qBodiesWithFormAttribute(FORM_BODY_CSYS_MATE_CONNECTOR)
                                });
            const targetBody = evaluateQuery(context, faces[0]->qOwnerBody())[0];
            targetBodyToCutOuts = insertIntoMapOfArrays(targetBodyToCutOuts, targetBody, formedBodies);
        }
        if (targetBodyToCutOuts == {})
        {
            throw "No proper locations";
        }
        return targetBodyToCutOuts;
    }
    
    export function cutOutEditLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean) returns map
    {
        if (definition.locations == oldDefinition.locations)
        {
            return definition;
        }
    
        definition.locations = qUnion(clusterVertexQueries(context, definition.locations));
    
        definition.targetBodies = qNothing();
        var targetFaces = [];
        const activeSMFaces = qAllModifiableSolidBodiesNoMesh()->qActiveSheetMetalFilter(ActiveSheetMetal.YES)->qOwnedByBody(EntityType.FACE);
        for (var location in evaluateQuery(context, definition.locations))
        {
            const cSys = evaluateCSys(context, location);
            targetFaces = append(targetFaces, activeSMFaces->qParallelPlanes(cSys.zAxis, true)->qContainsPoint(cSys.origin));
        }
        // Make sure non-SheetMetalDefinitionEntityType.FACE faces do not sneak in
        const targetFacesQ = qUnion(targetFaces);
        targetFaces = [];
        for (var targetFace in evaluateQuery(context, targetFacesQ))
        {
            const definitionEntities = getSMDefinitionEntities(context, targetFace);
            if (size(definitionEntities) == 1 && !isQueryEmpty(context, qEntityFilter(definitionEntities[0], EntityType.FACE)))
            {
                targetFaces = append(targetFaces, targetFace);
            }
        }
        definition.targetBodies = qUnion(evaluateQuery(context, qUnion(targetFaces)->qOwnerBody()));
    
        return definition;
    }

    
    function evaluateCSys(context is Context, location is Query) returns CoordSystem
    {
        if (isQueryEmpty(context, location->qBodyType(BodyType.MATE_CONNECTOR)))
        {
            const skPlane = evOwnerSketchPlane(context, {
                    "entity" : location
            });
            const point = evVertexPoint(context, {
                    "vertex" : location
            });
            return coordSystem(point, skPlane.x, skPlane.normal);
        }
        else
        {
            return evMateConnector(context, { "mateConnector" : location });
        }
    }
