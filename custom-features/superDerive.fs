FeatureScript 1993;
import(path : "onshape/std/common.fs", version : "1993.0");
export import(path : "onshape/std/boolean.fs", version : "1993.0");
modifiedInstantiator::import(path : "99c0429c4a3d059a83d06c19", version : "1936d50b6518656932c79d0e");
Icon::import(path : "6937aa461119700a3a978600/1c0bdf09cb3203fed18172e2/bb923bc7f1e0230e2d3d342f", version : "7a4e83be67edc1aa2c53238b");

annotation { "Feature Type Name" : "Super derive",
             "Feature Type Description" : "Like the built-in Derived feature, but allows specifying where the derived part is placed, deriving to multiple locations, deriving variables, and performing a boolean operation at the end.",
             "Icon" : Icon::BLOB_DATA }
export const superDerive = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        booleanStepTypePredicate(definition);

        annotation { "Name" : "Part studio" }
        definition.partStudio is PartStudioData;

        annotation { "Name" : "Location(s)", "Filter" : BodyType.MATE_CONNECTOR }
        definition.location is Query;

        annotation { "Name" : "Use origin", "Default" : true, "Description" : "<b>On</b> - use the origin of the derived parts<br><b>Off</b> - use the first mate connector of the first derived part" }
        definition.origin is boolean;

        annotation { "Name" : "Include mate connectors", "Default" : false, "Description" : "- keep or delete all derived mate connectors<br>- merged parts inherit derived mate connectors" }
        definition.keep is boolean;

        annotation { "Name" : "Include variables" }
        definition.includeVariables is boolean;

        if (definition.includeVariables)
        {
            annotation { "Name" : "Variable prefix" }
            definition.variablePrefix is string;

            annotation { "Name" : "Variable suffix" }
            definition.variableSuffix is string;
        }

        booleanStepScopePredicate(definition);
    }
    {
        const selectedParts = definition.partStudio.partQuery;

        if (selectedParts == undefined || selectedParts.subqueries == [])
        {
            throw regenError("No Part Studio data selected", ["partStudio"]);
        }

        const replacePrefixWithRegExpShouldBeBlank = replace(definition.variablePrefix, '[a-zA-Z_][a-zA-Z_0-9]*', '');

        if (replacePrefixWithRegExpShouldBeBlank != '')
            throw regenError("Invalid prefix", ["variablePrefix"]);

        const replaceSuffixWithRegExpShouldBeBlank = replace(definition.variableSuffix, '[a-zA-Z_0-9]*', '');

        if (replaceSuffixWithRegExpShouldBeBlank != '')
            throw regenError("Invalid suffix", ["variableSuffix"]);

        const remainderTransform = getRemainderPatternTransform(context, { "references" : definition.location });

        // Gets MCs from parts and composites
        const mateConnectorsOfParts = qMateConnectorsOfParts(qFlattenedCompositeParts(selectedParts));
        definition.partStudio.partQuery = qUnion(selectedParts, mateConnectorsOfParts);

        const instantiator = modifiedInstantiator::newInstantiator(id, definition);
        const mateConnectors = evaluateQuery(context, definition.location);

        if (mateConnectors == [])
        {
            modifiedInstantiator::addInstance(instantiator, definition.partStudio,
                        { "mateConnector" : definition.origin ? undefined : mateConnectorsOfParts });
        }
        else
        {
            if (definition.operationType == NewBodyOperationType.NEW && size(mateConnectors) > 1)
                reportFeatureInfo(context, id, "Using this feature to make instances is not recommended. Consider using an assembly instead.");

            for (var mateConnector in mateConnectors)
            {
                const location = evMateConnector(context, { "mateConnector" : mateConnector });

                modifiedInstantiator::addInstance(instantiator, definition.partStudio, {
                            "transform" : toWorld(location),
                            "identity" : mateConnector,
                            "mateConnector" : definition.origin ? undefined : mateConnectorsOfParts
                        });
            }
        }

        modifiedInstantiator::instantiate(context, instantiator); // modified instantiator is unchanged

        const validBodies = qCreatedBy(id, EntityType.BODY)
            ->qBodyType([BodyType.SOLID, BodyType.SHEET])
            ->qSketchFilter(SketchObject.NO);

        var derivedBodies = [];

        for (var body in evaluateQuery(context, validBodies))
        {
            // tracking faces is different for new/add/remove/intersect
            // separating the queries is the only way this will work
            derivedBodies = append(derivedBodies, {
                        "newFaces" : makeRobustQuery(context, qOwnedByBody(body, EntityType.FACE)),
                        "mergedFaces" : startTracking(context, qOwnedByBody(body, EntityType.FACE)),
                        "mateConnectors" : makeRobustQuery(context, qMateConnectorsOfParts(body))
                    });
        }

        if (evaluateQuery(context, qCreatedBy(id, EntityType.BODY)) != [])
            transformResultIfNecessary(context, id, remainderTransform);

        if (evaluateQuery(context, qCreatedBy(id, EntityType.BODY)->qBodyType(BodyType.SOLID)) != [])
            processNewBodyIfNeeded(context, id, definition, function(id)
                {
                });

        // Requirement is for MC inheritance. Currently with derived, if a body is merged
        // the MC still belongs to the original body, so if the result is derived again or
        // assembled the MCs are lost. The following code replaces the MCs with new ones
        // so that the ids remain constant if the user swaps between New/Add/Remove/Intersect.

        var mateConnectorsToDelete = [];

        if (definition.keep)
        {
            for (var i, body in derivedBodies)
            {
                const ownerBody = qOwnerBody(qUnion(body.newFaces, body.mergedFaces));

                // if body does not exist it was removed by a merge
                if (isQueryEmpty(context, ownerBody))
                {
                    mateConnectorsToDelete = append(mateConnectorsToDelete, body.mateConnectors);
                    continue;
                }

                for (var j, mateConnector in evaluateQuery(context, body.mateConnectors))
                {
                    const mateConnectorId = id + unstableIdComponent("mateConnector" ~ i ~ "-" ~ j);
                    setExternalDisambiguation(context, mateConnectorId, mateConnector);

                    try
                    {
                        opMateConnector(context, mateConnectorId, {
                                    "coordSystem" : evMateConnector(context, {
                                            "mateConnector" : mateConnector
                                        }),
                                    "owner" : qNthElement(ownerBody, 0)
                                });
                    }

                    mateConnectorsToDelete = append(mateConnectorsToDelete, mateConnector);
                }
            }
        }
        else
        {
            mateConnectorsToDelete = [qCreatedBy(id, EntityType.BODY)->qBodyType(BodyType.MATE_CONNECTOR)];
        }

        if (!isQueryEmpty(context, qUnion(mateConnectorsToDelete)))
        {
            opDeleteBodies(context, id + "deleteMateConnectors", {
                        "entities" : qUnion(mateConnectorsToDelete)
                    });
        }
    });
