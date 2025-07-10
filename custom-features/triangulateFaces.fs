FeatureScript 2625;
import(path : "onshape/std/common.fs", version : "2625.0");
import(path : "cc448676dec18cad9d8b2b57/8ddc053d4b428dcbe7ac83d5/904929219cb958b738066bde", version : "e2be23fa102d55b1091840f0");

//Borrowing this convex hull function from Konstantin, every other method I tried for face triangulation was less robust

annotation { "Feature Type Name" : "Triangulate Faces" }
export const triangulateFaces = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Input Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        definition.inputBody is Query;
    }
    {
        if (isQueryEmpty(context, definition.inputBody))
        {
            throw regenError("Please select a body.", ["inputBody"]);
        }

        const allFacesOfBody = qOwnedByBody(definition.inputBody, EntityType.FACE);
        var nonPlanarTargetFaceQueries = evaluateQuery(context, qGeometry(allFacesOfBody, GeometryType.OTHER_SURFACE));

        if (size(nonPlanarTargetFaceQueries) == 0)
        {
            reportFeatureWarning(context, id, "No non-planar faces found on the selected body.");
            return;
        }

        var bodyCentroid = vector(0, 0, 0) * meter;
        if (!isQueryEmpty(context, definition.inputBody))
        {
            try silent
            {
                bodyCentroid = evApproximateCentroid(context, { "entities" : definition.inputBody });
            }
        }

        // Phase 1: Create all polyhedra and determine boolean operations
        var polyhedraToProcess = []; // Array to store {polyhedronQuery: Query, operation: BooleanOperationType}

        for (var i = 0; i < size(nonPlanarTargetFaceQueries); i += 1)
        {
            const singleFaceQuery = nonPlanarTargetFaceQueries[i];
            const faceVerticesQuery = qAdjacent(singleFaceQuery, AdjacencyType.VERTEX, EntityType.VERTEX);

            if (isQueryEmpty(context, faceVerticesQuery))
            {
                reportFeatureWarning(context, id + "skipVertices" + i, "A non-planar face (" ~ i ~ ") has no identifiable vertices. Skipping.");
                continue;
            }

            // Each call to convexPolyhedron needs a unique base ID for its operations
            const convexHullOpIdSuffix = "convexHull" ~ i;
            const currentConvexHullOpId = id + convexHullOpIdSuffix;

            // Call your custom convexPolyhedron function
            // This assumes convexPolyhedron creates a new body that can be queried by 'currentConvexHullOpId'
            // (or an ID derived predictably from it if it's a multi-op feature).
            convexPolyhedron(context, currentConvexHullOpId, {
                        "vertices" : faceVerticesQuery
                        // If your convexPolyhedron was modified to take a uniqueSuffix for its *internal* ops,
                        // you wouldn't need it here as currentConvexHullOpId is already unique for each call.
                        // The key is that convexPolyhedron must use 'currentConvexHullOpId' as the root for *its own* internal Ids.
                    });

            const polyhedronBodyQuery = qCreatedBy(currentConvexHullOpId, EntityType.BODY);

            if (isQueryEmpty(context, polyhedronBodyQuery))
            {
                reportFeatureWarning(context, id + "skipHull" + i, "Convex polyhedron creation failed for face " ~ i ~ ".");
                continue;
            }

            // Determine boolean operation based on curvature
            var currentOperationType = BooleanOperationType.UNION; // Default to ADD

            try
            {
                const facePlane = evFaceTangentPlane(context, { "face" : singleFaceQuery, "parameter" : vector(0.5, 0.5) });
                const pointOnFace = facePlane.origin;
                const faceNormal = facePlane.normal;
                const vectorToCentroid = bodyCentroid - pointOnFace;
                // isOutwardPointing is true if the face normal generally points away from the body's bulk
                const isOutwardPointing = dot(faceNormal, vectorToCentroid) < (0 * meter);

                const curvatureData = evFaceCurvature(context, {
                            "face" : singleFaceQuery,
                            "parameter" : vector(0.5, 0.5)
                        });

                // Use a threshold to consider near-flat surfaces as flat
                const curvatureThreshold = definition.curvatureThreshold == undefined ? (0.000001 / meter) : definition.curvatureThreshold; // User-defined or small default

                // Determine the nature of the surface at the sample point
                // Gaussian curvature: K = minC * maxC
                // Mean curvature: H = (minC + maxC) / 2
                const K = curvatureData.minCurvature * curvatureData.maxCurvature;
                const H = (curvatureData.minCurvature + curvatureData.maxCurvature) / 2;

                if (isOutwardPointing)
                {
                    // Face points OUT from the material
                    if (curvatureData.minCurvature > curvatureThreshold && curvatureData.maxCurvature > curvatureThreshold)
                    {
                        // Both principal curvatures are positive: Elliptic convex (like a sphere bump)
                        // Material is convex -> SUBTRACT
                        currentOperationType = BooleanOperationType.SUBTRACTION;
                    }
                    else if (curvatureData.minCurvature < -curvatureThreshold && curvatureData.maxCurvature < -curvatureThreshold)
                    {
                        // Both principal curvatures are negative: Elliptic concave (like a bowl indent)
                        // Material is concave -> ADD
                        currentOperationType = BooleanOperationType.UNION;
                    }
                    else if (K < 0 / meter ^ 2) // Saddle shape
                    {
                        // For a saddle on an outward face, if the "bulge" (positive H) is dominant, treat as convex feature
                        // If the "dip" (negative H) is dominant, treat as concave feature
                        if (H > curvatureThreshold)
                        { // Dominated by positive curvature
                            currentOperationType = BooleanOperationType.SUBTRACTION;
                        }
                        else if (H < -curvatureThreshold)
                        { // Dominated by negative curvature
                            currentOperationType = BooleanOperationType.UNION;
                        }
                        else
                        { // Near-zero mean curvature, treat as flat or default to ADD
                            currentOperationType = BooleanOperationType.UNION;
                        }
                    }
                    else // One curvature is near zero (cylindrical or flat)
                    {
                        if (H > curvatureThreshold)
                        { // Cylindrical convex
                            currentOperationType = BooleanOperationType.SUBTRACTION;
                        }
                        else if (H < -curvatureThreshold)
                        { // Cylindrical concave
                            currentOperationType = BooleanOperationType.UNION;
                        }
                        else
                        { // Flat
                            currentOperationType = BooleanOperationType.UNION;
                            // Or based on specific need for flat
                        }
                    }
                }
                else // Face points IN towards the material (e.g. internal cavity face)
                {
                    // For inward pointing faces, the logic reverses for what ADD/SUBTRACT means for the overall part
                    if (curvatureData.minCurvature > curvatureThreshold && curvatureData.maxCurvature > curvatureThreshold)
                    {
                        // Both positive: Elliptic convex surface (like inside of a sphere shell, material is concave) -> ADD
                        currentOperationType = BooleanOperationType.UNION;
                    }
                    else if (curvatureData.minCurvature < -curvatureThreshold && curvatureData.maxCurvature < -curvatureThreshold)
                    {
                        currentOperationType = BooleanOperationType.SUBTRACTION;
                    }
                    else if (K < 0 / meter ^ 2) // Saddle shape on an internal surface
                    {
                        if (H > curvatureThreshold)
                        { // Material is locally concave
                            currentOperationType = BooleanOperationType.UNION;
                        }
                        else if (H < -curvatureThreshold)
                        { // Material is locally convex
                            currentOperationType = BooleanOperationType.SUBTRACTION;
                        }
                        else
                        {
                            currentOperationType = BooleanOperationType.UNION;
                        }
                    }
                    else // One curvature is near zero (cylindrical or flat on an internal surface)
                    {
                        if (H > curvatureThreshold)
                        { // Material is locally concave
                            currentOperationType = BooleanOperationType.UNION;
                        }
                        else if (H < -curvatureThreshold)
                        { // Material is locally convex
                            currentOperationType = BooleanOperationType.SUBTRACTION;
                        }
                        else
                        { // Flat internal surface
                            currentOperationType = BooleanOperationType.UNION;
                        }
                    }
                }
            }
            catch (e)
            {
                reportFeatureWarning(context, id + "curvatureError" + i, "Curvature eval failed for face " ~ i ~ ". Defaulting to Add. Error: " ~ e);
                currentOperationType = BooleanOperationType.UNION;
            }


            // Store the query to the new polyhedron and its intended operation
            polyhedraToProcess = append(polyhedraToProcess, {
                        "polyhedronQuery" : polyhedronBodyQuery,
                        "operation" : currentOperationType,
                        "originalFaceIndex" : i // For debugging/logging if needed
                    });
        }

        // Phase 2: Apply all boolean operations
        // The target for booleans needs to be the evolving body.
        // We can reference the input body initially, and subsequent ops will target the result of previous ones.
        var currentTargetBody = definition.inputBody;

        for (var k = 0; k < size(polyhedraToProcess); k += 1)
        {
            const processItem = polyhedraToProcess[k];
            const booleanOpId = id + "booleanOp" + k; // Unique ID for each boolean

            // The target for the opBoolean is the main body as it's being modified
            // If it's the first boolean, the target is the original definition.inputBody.
            // Otherwise, it's the result of the previous boolean.
            // This requires opBoolean to correctly modify and return/allow querying of the modified target.
            // However, opBoolean modifies in place. So, definition.inputBody is always the evolving target.

            opBoolean(context, booleanOpId, {
                        "tools" : processItem.polyhedronQuery,
                        "targets" : definition.inputBody, // This should correctly target the evolving body
                        "operationType" : processItem.operation,
                        "targetsAndToolsNeedGrouping" : true
                    });
        }
    }
    );
