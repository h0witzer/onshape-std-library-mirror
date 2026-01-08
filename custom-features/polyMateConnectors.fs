FeatureScript 2815;

// Special thanks to Michael Pascoe for stability improvements in V4
// This script is maintained by Derek Van Allen
// For support or improvement requests please contact me on the Onshape forums or comment on the main thread
// https://forum.onshape.com/discussion/28078/new-feature-poly-mate-connectors

import(path : "onshape/std/common.fs", version : "2815.0");
import(path : "onshape/std/queryVariable.fs", version : "2815.0");
icon::import(path : "88ad2302bdebe0131df8f002", version : "95e1f1cb002a1db5c49b7878");


annotation { "Feature Type Name" : "Poly-Mate Connectors",
        "Feature Type Description" : "Adds multiple explicit mate connectors on the locations of implicit mate connectors",
        "Feature Name Template" : "Poly-Mate Connectors#featureName" ,
                "Icon" : icon::BLOB_DATA }
// Promoting them
// King me
export const duplicateMateConnectors = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
             annotation { "Name" : "Mate connectors", "Filter" : BodyType.MATE_CONNECTOR }
        // "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS }
        definition.connectors is Query;

        annotation { "Name" : "Specify owner part" }
        definition.specifyOwnerPart is boolean;

        if (definition.specifyOwnerPart)
        {
            annotation { "Name" : "Owner part",
                        "Filter" : EntityType.BODY && (BodyType.SOLID || GeometryType.MESH
                                || BodyType.SHEET || BodyType.WIRE || BodyType.COMPOSITE)
                        && AllowMeshGeometry.YES && ModifiableEntityOnly.YES,
                        "MaxNumberOfPicks" : 1 }
            definition.ownerPart is Query;
        }
        annotation { "Name" : "Create query variable" }
        definition.createQueryVariable is boolean;

        if (definition.createQueryVariable)
        {
            annotation { "Name" : "Query variable name", "Default" : "polyMateConnectors" }
            definition.queryVariableName is string;
        }
    }
    {
        if (definition.specifyOwnerPart)
        {
            verifyNonemptyQuery(context, definition, "ownerPart",
                ErrorStringEnum.MATECONNECTOR_OWNER_PART_NOT_RESOLVED);
        }

        // 1. Resolve all selected entities into a list
        const entities = evaluateQuery(context, definition.connectors);

        // 2. Iterate by Index (i) instead of by Topology
        for (var i = 0; i < size(entities); i += 1)
        {
            const entity = entities[i];

            const connectorCsys = evMateConnector(context, {
                        "mateConnector" : entity
                    });

            const owningPart = definition.specifyOwnerPart ?
                definition.ownerPart :
                qOwnerBody(entity);

            // 3. Create the mate connector using a stable ID based on the loop index (id + i)
            // If you swap geometry at index 0, the ID remains (id + 0)
            opMateConnector(context, id + i, { "coordSystem" : connectorCsys, "owner" : owningPart });
        }

        if (definition.createQueryVariable)
        {
            verifyVariableNameIsValid(definition.queryVariableName, "queryVariableName");

            var variableExists = false;
            try silent
            {
                getVariable(context, definition.queryVariableName);
                variableExists = true;
            }
            if (variableExists)
            {
                throw regenError(ErrorStringEnum.QUERY_VARIABLE_NAME_ALREADY_USED_IN_NON_QUERY_VARIABLE,
                    ["queryVariableName"]);
            }

            const createdMateConnectors = qCreatedBy(id, EntityType.BODY);
            setQueryVariable(context, definition.queryVariableName, createdMateConnectors);
        }

        const featureName = definition.createQueryVariable ?
            " [QV: " ~ definition.queryVariableName ~ "]" :
            "";

        setFeatureComputedParameter(context, id, { "name" : "featureName", "value" : featureName });
    },
    {});
