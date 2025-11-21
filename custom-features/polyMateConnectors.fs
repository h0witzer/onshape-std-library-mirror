FeatureScript 2679;

export import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/feature.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/geomOperations.fs", version : "2679.0");

annotation { "Feature Type Name" : "Poly-Mate Connectors",
        "Feature Type Description" : "Adds multiple explicit mate connectors on the locations of implicit mate connectors" }
// Promoting them
// King me
export const duplicateMateConnectors = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Mate connectors", "Filter" : BodyType.MATE_CONNECTOR,
                     "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS }
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
    }
    {
        if (definition.specifyOwnerPart)
        {
            verifyNonemptyQuery(context, definition, "ownerPart",
                ErrorStringEnum.MATECONNECTOR_OWNER_PART_NOT_RESOLVED);
        }
        forEachEntity(context, id, definition.connectors, function(sourceConnector is Query, innerId is Id)
        {
            const connectorCsys = evMateConnector(context, { "mateConnector" : sourceConnector });
            const owningPart = definition.specifyOwnerPart ?
                    definition.ownerPart :
                    qOwnerBody(sourceConnector);
            // Create the new mate connector
            opMateConnector(context, innerId, { "coordSystem" : connectorCsys, "owner" : owningPart });
        });
    },
    {});
