FeatureScript 2559;
import(path : "onshape/std/common.fs", version : "2559.0");

export import(path : "292286148f0044bbd7ef4042", version : "51203dd1425955172d13b65b");//ctPointsBackEndAlt.fs

annotation { "Feature Type Name" : "CT SKETCH",
        "Feature Type Description" : "Creates sketch points from CT points on a selected face",
        "Manipulator Change Function" : "createPointsManipulatorChange",
        "Editing Logic Function" : "createPointsEditingLogic", }
export const createPoints = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Select the face where the sketch will be created.
        annotation { "Name" : "Face", "Filter" : EntityType.FACE && AllowFlattenedGeometry.YES }
        definition.face is Query;

        // (Other CT point parameters are defined and set up via internalCTpointsPredicate.)
        internalCTpointsPredicate(definition);
    }
    {
        // Get the CT points from your function.
        // Ensure that doCreatePoints returns the list of points.
        var ctPoints = doCreatePoints(context, id, definition);

        // --- Step 1. Get the Face and its Tangent Plane ---
        var faceEntities = evaluateQuery(context, definition.face);
        if (size(faceEntities) == 0)
        {
            throw "No face selected.";
        }
        var faceEntity = faceEntities[0];
        var evPlane = evFaceTangentPlane(context, {
                "face" : faceEntity,
                "parameter" : vector(0.5, 0.5)
            });

        // Extract the plane's origin and normal.
        var origin = evPlane.origin;
        var normal = evPlane.normal;

        // --- Step 2. Build a Coordinate System from the Plane ---
        // Compute a candidate x-axis.
        var candidateX = vector(normal[1], -normal[0], 0);
        if (norm(candidateX) < 1e-6)
        {
            candidateX = vector(1, 0, 0);
        }
        var xDir = normalize(candidateX);

        // Create a coordinate system using the origin, computed x-direction, and the plane's normal as z-axis.
        var cs = coordSystem(origin, xDir, normal);
        // (The y-axis is implicitly defined via yAxis(cs).)

        // Create a sketch plane from this coordinate system.
        var sketchPlane = plane(cs);

        // --- Step 3. Create a Sketch on the Face's Plane ---
        var sketch = newSketchOnPlane(context, id + "sketch", {
                "sketchPlane" : sketchPlane
            });

        // --- Step 4. Convert CT Points from World to Sketch Coordinates ---
        // Convert each 3D CT point to the sketch's coordinate system.
        for (var i = 0; i < size(ctPoints); i += 1)
        {
            var ctPoint3D = ctPoints[i]; // A 3D vector in world space.
            // Convert the point to the coordinate system of the sketch.
            var localPt = fromWorld(cs, ctPoint3D);
            // Use only x and y for the sketch point.
            skPoint(sketch, "ctPoint" ~ i, {
                        "position" : vector(localPt[0], localPt[1])
                    });
        }

        // --- Step 5. Solve the Sketch ---
        skSolve(sketch);
    });
