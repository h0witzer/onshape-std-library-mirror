FeatureScript 2815;

// Special thanks to Michael Pascoe for stability improvements in V4
// This script is maintained by Derek Van Allen
// For support or improvement requests please contact me on the Onshape forums or comment on the main thread
// https://forum.onshape.com/discussion/28078/new-feature-poly-mate-connectors

import(path : "onshape/std/common.fs", version : "2815.0");
import(path : "onshape/std/queryVariable.fs", version : "2815.0");
icon::import(path : "88ad2302bdebe0131df8f002", version : "95e1f1cb002a1db5c49b7878");


annotation { "Feature Type Name" : "Poly-Mate Connectors",
        "Feature Type Description" : "Adds multiple explicit mate connectors on the locations of implicit mate connectors or sketch vertices",
        "Feature Name Template" : "Poly-Mate Connectors#featureName" ,
                "Icon" : icon::BLOB_DATA }
// Promoting them
// King me
export const duplicateMateConnectors = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
             annotation { "Name" : "Mate connectors or sketch points", 
                        "Filter" : EntityType.VERTEX && SketchObject.YES && ModifiableEntityOnly.YES || BodyType.MATE_CONNECTOR,
                        "UIHint" : UIHint.ALLOW_QUERY_ORDER }
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
        
        // Pre-evaluate mate connectors for performance when processing many entities
        const mateConnectorArray = evaluateQuery(context, qBodyType(definition.connectors, BodyType.MATE_CONNECTOR));
        
        // Build a map for efficient membership testing
        var mateConnectorSet = {};
        for (var mc in mateConnectorArray)
        {
            mateConnectorSet[mc] = true;
        }

        // 2. Iterate by Index (i) instead of by Topology
        for (var i = 0; i < size(entities); i += 1)
        {
            const entity = entities[i];

            var connectorCsys;
            var owningPart;
            
            // Determine if the entity is a sketch vertex or a mate connector
            const isMateConnector = (mateConnectorSet[entity] != undefined);
            
            if (!isMateConnector)
            {
                // Handle sketch vertex: get its position and use the sketch plane for orientation
                const vertexPoint = evVertexPoint(context, { "vertex" : entity });
                const ownerSketchPlane = try silent(evOwnerSketchPlane(context, { "entity" : entity }));
                
                if (ownerSketchPlane == undefined)
                {
                    // If we can't get the sketch plane, use a default coordinate system at the point
                    connectorCsys = coordSystem(vertexPoint, X_DIRECTION, Z_DIRECTION);
                }
                else
                {
                    // Convert the sketch plane to a coordinate system and position it at the vertex
                    const planeCsys = planeToCSys(ownerSketchPlane);
                    connectorCsys = coordSystem(vertexPoint, planeCsys.xAxis, planeCsys.zAxis);
                }
                
                // For sketch vertices, determine owner from user input or attempt to find owner body
                if (definition.specifyOwnerPart)
                {
                    owningPart = definition.ownerPart;
                }
                else
                {
                    // Try to get owner body, but it may not exist for sketch vertices
                    const ownerBody = try silent(qOwnerBody(entity));
                    owningPart = (ownerBody != undefined) ? ownerBody : qNothing();
                }
            }
            else
            {
                // Handle existing mate connector
                connectorCsys = evMateConnector(context, {
                            "mateConnector" : entity
                        });

                owningPart = definition.specifyOwnerPart ?
                    definition.ownerPart :
                    qOwnerBody(entity);
            }

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
