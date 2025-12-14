FeatureScript 2559;
import(path : "onshape/std/common.fs", version : "2559.0");
export import(path : "292286148f0044bbd7ef4042", version : "51203dd1425955172d13b65b");//ctPointsBackEndAlt.fs

annotation { "Feature Type Name" : "CT MATE GEN",
             "Feature Type Description" : "Generates mate connectors at CT points on a selected face",
             "Manipulator Change Function" : "createPointsManipulatorChange",
             "Editing Logic Function" : "createPointsEditingLogic", }
export const createPoints = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Setup CT point parameters (provided by internalCTpointsPredicate).
        internalCTpointsPredicate(definition);
        
        // Owner body for the mate connectors.
        annotation { "Name" : "Owner Body", "Filter" : EntityType.BODY }
        definition.owner is Query;
        
        // Face on which the mate connector will be based.
        annotation { "Name" : "Face", "Filter" : EntityType.FACE }
        definition.face is Query;
        
        // Optionally, an edge for determining the mate connector's x-axis.
        annotation { "Name" : "Edge", "Filter" : EntityType.EDGE, "Optional" : true }
        definition.xedge is Query;
    }
    {
        // Get the CT points from your function.
        // (Assumes doCreatePoints returns a list of 3D points.)
        var ctPoints = doCreatePoints(context, id, definition);
        
        // Evaluate the owner body.
        var ownerEntities = evaluateQuery(context, definition.owner);
        if (size(ownerEntities) == 0)
        {
            throw "No owner body selected.";
        }
        var ownerBody = ownerEntities[0];
        
        // Evaluate the face.
        var faceEntities = evaluateQuery(context, definition.face);
        if (size(faceEntities) == 0)
        {
            throw "No face selected.";
        }
        var faceEntity = faceEntities[0];
        
        // Obtain the face normal by evaluating a tangent plane (using a default parameter).
        var evPlane = evFaceTangentPlane(context, {
            "face" : faceEntity,
            "parameter" : vector(0.5, 0.5)
        });
        var faceNormal = evPlane.normal;
        
        // Determine the x-axis for the mate connector.
        var xAxis;
        var edgeEntities = evaluateQuery(context, definition.edge);
        if (size(edgeEntities) > 0)
        {
            // Use the edge’s tangent direction.
            var edgeEntity = edgeEntities[0];
            var evTangent = evEdgeTangentLine(context, {
                "edge" : edgeEntity,
                "parameter" : 0.5,
                "arcLengthParameterization" : true
            });
            xAxis = normalize(evTangent.direction);
        }
        else
        {
            // Otherwise compute a default x-axis from the face normal.
            var candidateX = vector(faceNormal[1], -faceNormal[0], 0);
            if (norm(candidateX) < 1e-6)
            {
                candidateX = vector(1, 0, 0);
            }
            xAxis = normalize(candidateX);
        }
        
        // For each CT point, create a mate connector at that location.
        var pCount = 0;
        for (var i = 0; i < size(ctPoints); i += 1)
        {
            // The CT point is the origin for the connector.
            var pt = ctPoints[i];
            // Create a coordinate system at pt using the determined x-axis and the face normal as z.
            var cs = coordSystem(pt, xAxis, faceNormal);
            
            // Create a mate connector using a unique id.
            opMateConnector(context, id + ("mateConnector" ~ pCount), {
                "coordSystem" : cs,
                "owner" : ownerBody
            });
            pCount += 1;
        }
    });
