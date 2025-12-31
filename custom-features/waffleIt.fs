// Waffle It slices a selected body into a grid of extruded rectangles to prepare geometry for laser cutting.
// Inputs:
//  - selectedBody : Body query to slice
//  - planeSpacing : Distance between slicing planes along the X and Y axes of the reference frame
//  - matThick : Material thickness that controls extrusion depth
//  - defRefFrame : Boolean to select a mate connector as the slicing reference frame
//  - referenceFrame : Mate connector query when defRefFrame is true, defines the placement of the slicing grid
//  - outputSheetMetal : Boolean to output results as sheet metal bodies
//  - deleteInputBody : Boolean to delete the input body after the waffling operation is completed
FeatureScript 2815;
// import(path : "onshape/std/geometry.fs", version : "2815.0");
import(path : "onshape/std/common.fs", version : "2815.0");
import(path : "onshape/std/query.fs", version : "2815.0");
import(path : "onshape/std/box.fs", version : "2815.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2815.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2815.0");
import(path : "onshape/std/evaluate.fs", version : "2815.0");
import(path : "onshape/std/topologyUtils.fs", version : "2815.0");
import(path : "onshape/std/attributes.fs", version : "2815.0");
import(path : "onshape/std/primitives.fs", version : "2815.0");
import(path : "onshape/std/fillSurface.fs", version : "2815.0");

annotation { "Feature Type Name" : "Waffle It" }
export const sheetMetalStart = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        definition.selectedBody is Query;

        annotation { "Name" : "Plane Spacing" }
        isLength(definition.planeSpacing, LENGTH_BOUNDS);

        annotation { "Name" : "Material Thickness" }
        isLength(definition.matThick, LENGTH_BOUNDS);


        annotation { "Name" : "Define Reference Frame" }
        definition.defRefFrame is boolean;


        if (definition.defRefFrame)
        {
            annotation { "Name" : "Reference Frame", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
            definition.referenceFrame is Query;
        }

        annotation { "Name" : "Normalize Geometry" }
        definition.normalizeGeometry is boolean;

        annotation { "Name" : "Output as Sheet Metal" }
        definition.outputSheetMetal is boolean;

        if (definition.outputSheetMetal)
        {
            annotation { "Name" : "Bend Radius" }
            isLength(definition.bendRadius, SM_BEND_RADIUS_BOUNDS);

            annotation { "Name" : "K Factor" }
            isReal(definition.kFactor, K_FACTOR_BOUNDS);

            annotation { "Name" : "Minimal Clearance" }
            isLength(definition.minimalClearance, SM_MINIMAL_CLEARANCE_BOUNDS);
        }

        annotation { "Name" : "Delete input body" }
        definition.deleteInputBody is boolean;

    }
    {
        // Establish the coordinate system used for slicing
        var referenceFrame = WORLD_COORD_SYSTEM;

        if (definition.defRefFrame == true)
        {
            referenceFrame = evMateConnector(context, {
                        "mateConnector" : definition.referenceFrame
                    });
        }

        // Get a world-aligned bounding box for creating the cuboid to intersect with
        // This avoids transformation complexity - the cuboid will always be axis-aligned
        var worldBoundingBox = evBox3d(context, {
                "topology" : definition.selectedBody,
                "tight" : true
            });

        var referenceFrameToWorldTransform = toWorld(referenceFrame);

        // Build slice sets for X and Y orientations
        const xSliceSetDefinition = {
            "featureIdPrefix" : id,
            "setLabel" : "X",
            "normalVector" : vector([1, 0, 0]),
            "upVector" : vector([0, 1, 0]),
            "planeSpacing" : definition.planeSpacing,
            "referenceFrameToWorldTransform" : referenceFrameToWorldTransform,
            "materialThickness" : definition.matThick,
            "worldBoundingBox" : worldBoundingBox,
            "targetBody" : definition.selectedBody
        };
        
        const ySliceSetDefinition = {
            "featureIdPrefix" : id,
            "setLabel" : "Y",
            "normalVector" : vector([0, 1, 0]),
            "upVector" : vector([0, 0, 1]),
            "planeSpacing" : definition.planeSpacing,
            "referenceFrameToWorldTransform" : referenceFrameToWorldTransform,
            "materialThickness" : definition.matThick,
            "worldBoundingBox" : worldBoundingBox,
            "targetBody" : definition.selectedBody
        };
        
        var xSliceResult = generateSliceSet(context, xSliceSetDefinition);
        var ySliceResult = generateSliceSet(context, ySliceSetDefinition);

        // Intersect each sheet with the target solid to retain only in-bounds material before generating cross-slot geometry.
        const sliceSets = [xSliceResult, ySliceResult];
        const trimmedSliceSets = trimSliceSetsToSolid(context, id, sliceSets, definition.selectedBody);

        // First normalization pass: Remove any geometry that would be deleted by normalization before generating
        // cross-slot geometry. This prevents wasting computation on cross slots in regions that will be removed.
        // Since normalization is already iterative, there is no performance penalty for running it twice.
        if (definition.normalizeGeometry == true)
        {
            // Process all slice bodies together using attribute queries to find cap faces
            for (var sliceSet in trimmedSliceSets)
            {
                const allSliceBodies = qUnion(mapArray(sliceSet.sliceIds, function(sliceId)
                        {
                            return qCreatedBy(sliceId + "extrudeSlice", EntityType.BODY);
                        }));
                normalizeSliceGeometryForLasercutting(context, id + "preNormalize" + sliceSet.setLabel, allSliceBodies, definition.matThick);
            }
        }

        // Generate cross-slot geometry
        generateSlotsForSliceSets(context, id, trimmedSliceSets, referenceFrame);

        // Second normalization pass: Normalize any faces created by the cross-slot generation.
        // This pass will skip faces that were already normalized in the first pass (START caps, END caps, and
        // vertical walls), so it only processes new geometry created during cross-slot generation.
        // After trimming the intersecting grid, find all non-cap faces on each slice and project their geometry to
        // the START cap face. Thicken the flattened projections and remove the results from the slice.
        // This subtractive operation guarantees the slices lie inside of the original target volume.
        if (definition.normalizeGeometry == true)
        {
            // Process all slice bodies together using attribute queries to find cap faces
            for (var sliceSet in trimmedSliceSets)
            {
                const allSliceBodies = qUnion(mapArray(sliceSet.sliceIds, function(sliceId)
                        {
                            return qCreatedBy(sliceId + "extrudeSlice", EntityType.BODY);
                        }));
                normalizeSliceGeometryForLasercutting(context, id + "postNormalize" + sliceSet.setLabel, allSliceBodies, definition.matThick);
            }
        }

        // Convert to sheet metal if requested
        if (definition.outputSheetMetal == true)
        {
            convertSlicesToSheetMetal(context, id, trimmedSliceSets, definition);
        }

        // Delete the input body if requested
        if (definition.deleteInputBody)
        {
            opDeleteBodies(context, id + "deleteInputBody", {
                        "entities" : definition.selectedBody
                    });
        }

    }, {});

// Generate a set of slice bodies by creating planes at regular intervals along a normal direction,
// intersecting each plane with a bounding box to get the slice profile, and extruding to create bodies.
// Supports arbitrary slice orientations for future curve-based slicing features.
// Inputs:
//  - context : Execution context
//  - sliceSetDefinition : Map containing:
//      - featureIdPrefix : Base id for naming geometry
//      - setLabel : String label for this set (e.g., "X", "Y")
//      - normalVector : Normal vector for the slicing planes in reference frame coordinates
//      - upVector : Up vector for orienting the slices in reference frame coordinates
//      - planeSpacing : Distance between consecutive slices
//      - referenceFrameToWorldTransform : Transform from reference frame to world coordinates
//      - materialThickness : Extrusion depth for the slices
//      - worldBoundingBox : World-aligned bounding box of the target body
// Returns: map containing { slicePlanes, sliceIds, setLabel, normalVector, upVector }
export function generateSliceSet(context is Context, sliceSetDefinition is map) returns map
{
    const featureIdPrefix = sliceSetDefinition.featureIdPrefix;
    const setLabel = sliceSetDefinition.setLabel;
    const normalVector = sliceSetDefinition.normalVector;
    const upVector = sliceSetDefinition.upVector;
    const planeSpacing = sliceSetDefinition.planeSpacing;
    const referenceFrameToWorldTransform = sliceSetDefinition.referenceFrameToWorldTransform;
    const materialThickness = sliceSetDefinition.materialThickness;
    const worldBoundingBox = sliceSetDefinition.worldBoundingBox;
    const targetBody = sliceSetDefinition.targetBody;
    
    var slicePlanes = [] as array;
    var sliceIds = [] as array;
    
    // Calculate the center of the bounding box in reference frame coordinates
    const boxCenterWorld = (worldBoundingBox.minCorner + worldBoundingBox.maxCorner) / 2;
    const worldToReferenceTransform = inverse(referenceFrameToWorldTransform);
    const boxCenter = worldToReferenceTransform * boxCenterWorld;
    
    // Find the extent of the world bounding box along the normal direction in reference frame
    const boundingBoxCornersWorld = [
        worldBoundingBox.minCorner,
        vector([worldBoundingBox.maxCorner[0], worldBoundingBox.minCorner[1], worldBoundingBox.minCorner[2]]),
        vector([worldBoundingBox.minCorner[0], worldBoundingBox.maxCorner[1], worldBoundingBox.minCorner[2]]),
        vector([worldBoundingBox.minCorner[0], worldBoundingBox.minCorner[1], worldBoundingBox.maxCorner[2]]),
        vector([worldBoundingBox.maxCorner[0], worldBoundingBox.maxCorner[1], worldBoundingBox.minCorner[2]]),
        vector([worldBoundingBox.maxCorner[0], worldBoundingBox.minCorner[1], worldBoundingBox.maxCorner[2]]),
        vector([worldBoundingBox.minCorner[0], worldBoundingBox.maxCorner[1], worldBoundingBox.maxCorner[2]]),
        worldBoundingBox.maxCorner
    ];
    
    var minDepth = undefined;
    var maxDepth = undefined;
    
    for (var cornerWorld in boundingBoxCornersWorld)
    {
        const cornerLocal = worldToReferenceTransform * cornerWorld;
        const depthProjection = dot(cornerLocal, normalVector);
        if (minDepth == undefined)
        {
            minDepth = depthProjection;
            maxDepth = depthProjection;
        }
        else
        {
            minDepth = min(minDepth, depthProjection);
            maxDepth = max(maxDepth, depthProjection);
        }
    }
    
    // Create a world-aligned bounding box body to intersect with (ensures properly sized slices)
    const boundingBoxId = featureIdPrefix + setLabel + "BoundingBox";
    
    // Create axis-aligned box directly in world coordinates
    fCuboid(context, boundingBoxId, {
                "corner1" : worldBoundingBox.minCorner,
                "corner2" : worldBoundingBox.maxCorner
            });
    
    const boundingBoxBody = qCreatedBy(boundingBoxId, EntityType.BODY);
    
    // Calculate which plane indices are needed to cover the bounding box along the normal direction
    const firstPlaneIndex = ceil(minDepth / planeSpacing);
    const lastPlaneIndex = floor(maxDepth / planeSpacing);
    
    var planeCounter = 0;
    for (var planeIndex = firstPlaneIndex; planeIndex <= lastPlaneIndex; planeIndex += 1)
    {
        const planeDepth = planeIndex * planeSpacing;
        
        // Calculate the plane origin in reference frame coordinates
        // Position slice plane at correct depth along normal, centered on bounding box
        const sliceOrigin = boxCenter + (normalVector * (planeDepth - dot(boxCenter, normalVector)));
        
        // Create the plane and transform it to world coordinates
        // Use the input upVector for plane orientation
        const localPlane = plane(sliceOrigin, normalVector, upVector);
        const slicePlane = referenceFrameToWorldTransform * localPlane;
        
        const sliceId = featureIdPrefix + setLabel + planeCounter;
        const extrusionDirectionWorld = referenceFrameToWorldTransform.linear * normalVector;
        
        generateSliceSheet(context, sliceId, slicePlane, boundingBoxBody, extrusionDirectionWorld, materialThickness);
        
        slicePlanes = append(slicePlanes, slicePlane);
        sliceIds = append(sliceIds, sliceId);
        planeCounter += 1;
    }
    
    // Clean up the bounding box body after all slices are generated
    opDeleteBodies(context, featureIdPrefix + setLabel + "cleanupBoundingBox", {
                "entities" : boundingBoxBody
            });
    
    return {
        "slicePlanes" : slicePlanes,
        "sliceIds" : sliceIds,
        "setLabel" : setLabel,
        "normalVector" : normalVector,
        "upVector" : upVector
    };
}

// Trim an array of slice sets to the target body, removing any slices that don't intersect.
// Generic function that works with an arbitrary number of slice sets with any orientations.
// Inputs:
//  - context : Execution context
//  - featureIdPrefix : Base id for operations
//  - sliceSets : Array of slice set result maps, each containing { sliceIds, setLabel, ... }
//  - targetBody : Body query representing the part being sliced
// Returns: Array of trimmed slice sets with the same structure as input, containing only valid slice IDs
export function trimSliceSetsToSolid(context is Context, featureIdPrefix is Id, sliceSets is array, targetBody is Query) returns array
{
    var trimmedSliceSets = [] as array;
    
    // Build queries for all slice bodies across all sets
    var allSliceBodiesArray = [] as array;
    for (var sliceSet in sliceSets)
    {
        const setBodies = qUnion(mapArray(sliceSet.sliceIds, function(sliceId)
                {
                    return qCreatedBy(sliceId + "extrudeSlice", EntityType.BODY);
                }));
        allSliceBodiesArray = append(allSliceBodiesArray, setBodies);
    }
    const allSliceBodies = qUnion(allSliceBodiesArray);
    
    // Perform single batch SUBTRACT_COMPLEMENT operation for all slices at once
    // This preserves attributes and is much more efficient than iterative operations
    opBoolean(context, featureIdPrefix + "batchIntersection", {
                "tools" : targetBody,
                "targets" : allSliceBodies,
                "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                "keepTools" : true
            });
    
    // Check each slice in each set to see if it survived and has valid caps
    for (var sliceSet in sliceSets)
    {
        var validSliceIds = [] as array;
        const originalSliceIds = sliceSet.sliceIds;
        
        for (var sliceIndex = 0; sliceIndex < size(originalSliceIds); sliceIndex += 1)
        {
            const sliceId = originalSliceIds[sliceIndex];
            const sliceBody = qCreatedBy(sliceId + "extrudeSlice", EntityType.BODY);
            
            if (!isQueryEmpty(context, sliceBody))
            {
                // Verify START/END caps still exist using cap entity queries
                const startCapQuery = qCapEntity(sliceId + "extrudeSlice", CapType.START, EntityType.FACE);
                const endCapQuery = qCapEntity(sliceId + "extrudeSlice", CapType.END, EntityType.FACE);
                const remainingStartCaps = evaluateQuery(context, startCapQuery);
                const remainingEndCaps = evaluateQuery(context, endCapQuery);
                
                // Delete body if we don't have at least one face of each cap type
                if (size(remainingStartCaps) == 0 || size(remainingEndCaps) == 0)
                {
                    opDeleteBodies(context, featureIdPrefix + "delete" + sliceSet.setLabel + "Slice" + sliceIndex, {
                                "entities" : sliceBody
                            });
                    continue;
                }
                
                // Store the slice ID for later robust cap querying
                validSliceIds = append(validSliceIds, sliceId);
            }
        }
        
        // Create a trimmed slice set with the same metadata but only valid slice IDs
        // Use proper object copying to avoid modifying the original sliceSet
        var trimmedSet = {
            "sliceIds" : validSliceIds,
            "slicePlanes" : sliceSet.slicePlanes,
            "setLabel" : sliceSet.setLabel,
            "normalVector" : sliceSet.normalVector,
            "upVector" : sliceSet.upVector
        };
        trimmedSliceSets = append(trimmedSliceSets, trimmedSet);
    }
    
    return trimmedSliceSets;
}

// Generate slots between multiple slice sets where they intersect.
// Generic function that can handle N slice sets with arbitrary orientations.
// NOTE: Current implementation handles the two perpendicular slice set case directly.
// Future: Implement generic N-set slotting with configurable pairing strategies.
// Inputs:
//  - context : Execution context
//  - featureIdPrefix : Base id for operations
//  - sliceSets : Array of trimmed slice sets with metadata (sliceIds, normalVector, etc.)
//  - referenceFrame : Coordinate system for splitting operations
// Returns: None (modifies slice bodies in place)
export function generateSlotsForSliceSets(context is Context, featureIdPrefix is Id, sliceSets is array, referenceFrame is CoordSystem)
{
    // Handle the common case of exactly 2 perpendicular slice sets
    if (size(sliceSets) == 2)
    {
        const set0SliceIds = sliceSets[0].sliceIds;
        const set1SliceIds = sliceSets[1].sliceIds;
        
        var set0CopyIds = [] as array;
        var set1CopyIds = [] as array;
        var splitPlaneIds = [] as array;
        var splitIds = [] as array;

        // 1. Create Copies of both slice sets
        for (var set0Index = 0; set0Index < size(set0SliceIds); set0Index += 1)
        {
            const sliceBody = qCreatedBy(set0SliceIds[set0Index] + "extrudeSlice", EntityType.BODY);
            if (!isQueryEmpty(context, sliceBody))
            {
                const copyId = featureIdPrefix + "slotCopy" + sliceSets[0].setLabel + set0Index;
                opPattern(context, copyId, {
                            "entities" : sliceBody,
                            "transforms" : [identityTransform()],
                            "instanceNames" : ["1"]
                        });
                set0CopyIds = append(set0CopyIds, copyId);
            }
        }

        for (var set1Index = 0; set1Index < size(set1SliceIds); set1Index += 1)
        {
            const sliceBody = qCreatedBy(set1SliceIds[set1Index] + "extrudeSlice", EntityType.BODY);
            if (!isQueryEmpty(context, sliceBody))
            {
                const copyId = featureIdPrefix + "slotCopy" + sliceSets[1].setLabel + set1Index;
                opPattern(context, copyId, {
                            "entities" : sliceBody,
                            "transforms" : [identityTransform()],
                            "instanceNames" : ["1"]
                        });
                set1CopyIds = append(set1CopyIds, copyId);
            }
        }

        const copiedSet0Slices = qUnion(mapArray(set0CopyIds, function(copyId)
                {
                    return qCreatedBy(copyId, EntityType.BODY);
                }));
        const copiedSet1Slices = qUnion(mapArray(set1CopyIds, function(copyId)
                {
                    return qCreatedBy(copyId, EntityType.BODY);
                }));

        // 2. Generate Intersection Geometry (Subtract Complement)
        opBoolean(context, featureIdPrefix + "BatchCrossSlots", {
                    "tools" : copiedSet0Slices,
                    "targets" : copiedSet1Slices,
                    "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                    "keepTargets" : true
                });

        var splitToolsForSet0 = [] as array;
        var splitToolsForSet1 = [] as array;

        // 3. Evaluate Intersections and Split them
        var intersectionCells = evaluateQuery(context, copiedSet1Slices);
        var cellIndex = 0;
        for (var intersectionCell in intersectionCells)
        {
            var allIntersectionEdges = evaluateQuery(context, qGeometry(qOwnedByBody(intersectionCell, EntityType.EDGE), GeometryType.LINE));
            var zCoordinateAccumulator = 0 * millimeter;
            var numberOfAlignedEdges = 0;

            for (var intersectionEdge in allIntersectionEdges)
            {
                try
                {
                    var evaluatedEdgeLine = evLine(context, {
                            "edge" : intersectionEdge
                        });
                    if (abs(dot(evaluatedEdgeLine.direction, referenceFrame.zAxis)) > .9999)
                    {
                        var edgeTangentLine = evEdgeTangentLine(context, {
                                "edge" : intersectionEdge,
                                "parameter" : .5
                            });
                        numberOfAlignedEdges += 1;
                        zCoordinateAccumulator += dot(edgeTangentLine.origin, referenceFrame.zAxis);
                    }
                }
            }

            if (numberOfAlignedEdges == 0)
            {
                cellIndex += 1;
                continue;
            }

            var slicePlaneOrigin = (referenceFrame.zAxis * zCoordinateAccumulator / numberOfAlignedEdges) / squaredNorm(referenceFrame.zAxis);
            var slicePlane = plane(slicePlaneOrigin, referenceFrame.zAxis);
            const planeId = featureIdPrefix + "intersection" + cellIndex + "plane";
            opPlane(context, planeId, {
                        "plane" : slicePlane
                    });
            const splitId = featureIdPrefix + "intersection" + cellIndex + "split";
            opSplitPart(context, splitId, {
                        "targets" : intersectionCell,
                        "tool" : qCreatedBy(planeId, EntityType.BODY)
                    });

            splitToolsForSet0 = append(splitToolsForSet0, qFarthestAlong(qOwnerBody(qCreatedBy(splitId)), referenceFrame.zAxis));
            splitToolsForSet1 = append(splitToolsForSet1, qFarthestAlong(qOwnerBody(qCreatedBy(splitId)), -referenceFrame.zAxis));

            splitPlaneIds = append(splitPlaneIds, planeId);
            splitIds = append(splitIds, splitId);

            cellIndex += 1;
        }

        // 4. Perform Final Subtraction on Original Slices
        const originalSet0Slices = qUnion(mapArray(set0SliceIds, function(id)
                {
                    return qCreatedBy(id + "extrudeSlice", EntityType.BODY);
                }));

        const originalSet1Slices = qUnion(mapArray(set1SliceIds, function(id)
                {
                    return qCreatedBy(id + "extrudeSlice", EntityType.BODY);
                }));

        if (size(splitToolsForSet0) > 0)
        {
            opBoolean(context, featureIdPrefix + "boolean" + sliceSets[0].setLabel + "Slots", {
                        "tools" : qUnion(splitToolsForSet0),
                        "targets" : originalSet0Slices,
                        "operationType" : BooleanOperationType.SUBTRACTION
                    });
        }

        if (size(splitToolsForSet1) > 0)
        {
            opBoolean(context, featureIdPrefix + "boolean" + sliceSets[1].setLabel + "Slots", {
                        "tools" : qUnion(splitToolsForSet1),
                        "targets" : originalSet1Slices,
                        "operationType" : BooleanOperationType.SUBTRACTION
                    });
        }

        const splitPlanes = qUnion(mapArray(splitPlaneIds, function(splitPlaneId)
                {
                    return qCreatedBy(splitPlaneId, EntityType.BODY);
                }));
        const splitBodies = qUnion(mapArray(splitIds, function(splitId)
                {
                    return qCreatedBy(splitId, EntityType.BODY);
                }));

        opDeleteBodies(context, featureIdPrefix + "deleteCrossSlotHelpers", {
                    "entities" : qUnion([copiedSet0Slices, copiedSet1Slices, splitPlanes, splitBodies])
                });
    }
    else if (size(sliceSets) != 2)
    {
        // N-set slot generation not yet implemented
        // For now, slices will be created without cross-slots when using non-standard configurations
        // This is acceptable for single-set or 3+ set scenarios where slotting logic differs
        // TODO: Implement generic N-set slot generation when needed for curve-based slicing
    }
}

// Generate a single slice body by intersecting a construction plane with the bounding box to create
// a closed profile curve, filling the curve to create a surface, and extruding to the material thickness.
// The resulting slice body has START and END cap faces tagged with attributes for later processing.
// Inputs:
//  - sliceId : Unique Id prefix for all operations in this function
//  - slicePlane : Plane definition representing the slice location and orientation
//  - boundingBoxBody : Query for the bounding box body to intersect with
//  - extrusionDirection : Direction vector for the slice thickening (perpendicular to slice plane)
//  - materialThickness : Extrusion depth matching the stock thickness
// Returns: none
export function generateSliceSheet(context is Context, sliceId is Id, slicePlane is Plane, boundingBoxBody is Query, extrusionDirection is Vector, materialThickness is ValueWithUnits)
{
    // Create a construction plane at the slice location
    // Planes are infinite, so size parameters are just for UI visualization
    opPlane(context, sliceId + "plane", {
                "plane" : slicePlane
            });
    
    const constructionPlane = qCreatedBy(sliceId + "plane", EntityType.BODY);
    const planeFace = qOwnedByBody(constructionPlane, EntityType.FACE);
    
    // Get all faces from the bounding box body
    const boundingBoxFaces = qOwnedByBody(boundingBoxBody, EntityType.FACE);
    
    // Create intersection curves where the plane intersects the bounding box
    try
    {
        opIntersectFaces(context, sliceId + "intersect", {
                    "tools" : planeFace,
                    "targets" : boundingBoxFaces
                });
    }
    catch
    {
        // No intersection found - clean up and skip this slice
        opDeleteBodies(context, sliceId + "cleanupPlane", {
                    "entities" : constructionPlane
                });
        return;
    }
    
    const intersectionCurveBodies = qCreatedBy(sliceId + "intersect", EntityType.BODY);
    
    // Check if any intersection curves were created
    if (isQueryEmpty(context, intersectionCurveBodies))
    {
        opDeleteBodies(context, sliceId + "cleanupPlane2", {
                    "entities" : constructionPlane
                });
        return;
    }
    
    // The intersection creates wire bodies - opFillSurface needs edges from those wire bodies
    // that form closed loops (which plane-box intersections produce)
    opFillSurface(context, sliceId + "fillSurface", {
                "edgesG0" : qOwnedByBody(intersectionCurveBodies, EntityType.EDGE)
            });
    
    const filledSurface = qCreatedBy(sliceId + "fillSurface", EntityType.FACE);
    const filledSurfaceBody = qCreatedBy(sliceId + "fillSurface", EntityType.BODY);
    
    // Extrude the filled surface to create the slice body
    opExtrude(context, sliceId + "extrudeSlice", {
                "entities" : filledSurface,
                "direction" : extrusionDirection,
                "endBound" : BoundingType.BLIND,
                "endDepth" : materialThickness / 2,
                "startBound" : BoundingType.BLIND,
                "startDepth" : materialThickness / 2
            });
    
    const sliceBody = qCreatedBy(sliceId + "extrudeSlice", EntityType.BODY);
    
    // Tag the START cap face with an attribute
    const startCapFace = qCapEntity(sliceId + "extrudeSlice", CapType.START, EntityType.FACE);
    setAttribute(context, {
                "entities" : startCapFace,
                "name" : "laserItStartCap",
                "attribute" : true
            });
    
    // Tag the END cap face with an attribute
    const endCapFace = qCapEntity(sliceId + "extrudeSlice", CapType.END, EntityType.FACE);
    setAttribute(context, {
                "entities" : endCapFace,
                "name" : "laserItEndCap",
                "attribute" : true
            });
    
    // Clean up temporary geometry including the filled surface body
    opDeleteBodies(context, sliceId + "cleanupTemp", {
                "entities" : qUnion([constructionPlane, intersectionCurveBodies, filledSurfaceBody])
            });
}

// Normalize slice geometry for laser cutting by projecting non-cap faces onto the START cap face and subtracting.
// This function processes each body independently, using the START cap face (identified by attribute)
// as the projection target. Non-cap faces (excluding START/END caps and vertical walls) are projected
// onto the START cap and the thickened projection is subtracted.
// This ensures all output geometry can be laser cut flat without overhangs.
// Inputs:
//  - idPrefix : Id prefix used for created helper operations
//  - sliceBodies : Query for bodies to normalize
//  - materialThickness : Thickness used when removing projected material
// Returns: none
export function normalizeSliceGeometryForLasercutting(context is Context, idPrefix is Id, sliceBodies is Query, materialThickness is ValueWithUnits)
{
    // Process each body independently using the START cap face marked with an attribute
    var bodyArray = evaluateQuery(context, sliceBodies);
    var bodyCounter = 0;

    for (var body in bodyArray)
    {
        const bodyId = idPrefix + "Body" + bodyCounter;

        // Find all faces on this body
        const bodyFaces = qOwnedByBody(body, EntityType.FACE);
        const totalFaces = size(evaluateQuery(context, bodyFaces));

        // Get the START cap faces on this body using the attribute
        const startCapFacesOnBody = qIntersection([qHasAttribute(bodyFaces, "laserItStartCap"), bodyFaces]);
        const startCapFacesArray = evaluateQuery(context, startCapFacesOnBody);

        // Verify we have at least one START cap face
        if (size(startCapFacesArray) == 0)
        {
            bodyCounter += 1;
            continue;
        }

        // Use the first START cap face as the projection target
        const primaryCapFace = startCapFacesArray[0];

        // Get the plane definition from the START cap face
        var targetPlane = evPlane(context, {
                "face" : primaryCapFace
            });

        // Identify "Good" faces (don't need normalization):
        // - START cap faces (identified by attribute)
        const startCapFaces = qHasAttribute(bodyFaces, "laserItStartCap");
        // - END cap faces (identified by attribute)
        const endCapFaces = qHasAttribute(bodyFaces, "laserItEndCap");
        // - Vertical cut walls (parallel to the START cap's normal vector)
        const verticalWallFaces = qFacesParallelToDirection(bodyFaces, targetPlane.normal);

        // Combine START caps, END caps, and vertical walls into a "skip list"
        const validFaces = qUnion([startCapFaces, endCapFaces, verticalWallFaces]);

        // Subtract valid faces from all faces to find non-cap, non-vertical faces that need projection
        const nonNormalFaces = qSubtraction(bodyFaces, validFaces);

        const nonNormalFacesCount = size(evaluateQuery(context, nonNormalFaces));

        // Check for null case (nothing to normalize)
        if (isQueryEmpty(context, nonNormalFaces))
        {
            bodyCounter += 1;
            continue;
        }

        // Create a construction plane at the target plane location for projection
        const projectionPlaneId = bodyId + "projectionPlane";
        opPlane(context, projectionPlaneId, { "plane" : targetPlane });
        const projectionTarget = qCreatedBy(projectionPlaneId, EntityType.FACE);

        // Extract surfaces from faces to normalize
        const extractedOutlineToolsId = bodyId + "extract";
        opExtractSurface(context, extractedOutlineToolsId, {
                    "faces" : nonNormalFaces,
                    "offset" : 0 * meter
                });

        const extractedOutlineTools = qCreatedBy(extractedOutlineToolsId, EntityType.BODY);

        // Project onto the construction plane
        const outlineId = bodyId + "outline";

        try
        {
            opCreateOutline(context, outlineId, {
                        "tools" : extractedOutlineTools,
                        "target" : projectionTarget
                    });
        }
        catch
        {
            // Clean up and continue to next body
            opDeleteBodies(context, bodyId + "cleanupFailed", {
                        "entities" : qUnion([projectionTarget, extractedOutlineTools])
                    });
            bodyCounter += 1;
            continue;
        }

        const projectionFaces = qCreatedBy(outlineId, EntityType.FACE);

        // Check if any projection was created
        if (isQueryEmpty(context, projectionFaces))
        {
            opDeleteBodies(context, bodyId + "cleanup1", {
                        "entities" : qUnion([projectionTarget, extractedOutlineTools])
                    });
            bodyCounter += 1;
            continue;
        }

        // Thicken the projected outlines
        // Thicken only in thickness2 direction (away from the face normal) since the projection plane
        // is not centered on the slices and face normals point outward
        const thickenId = bodyId + "thicken";

        try
        {
            opThicken(context, thickenId, {
                        "entities" : projectionFaces,
                        "thickness1" : 0 * meter,
                        "thickness2" : materialThickness,
                        "keepTools" : true
                    });
        }
        catch
        {
            // Clean up helper geometry and continue to next body
            opDeleteBodies(context, bodyId + "cleanup1", {
                        "entities" : qUnion([projectionTarget, extractedOutlineTools, qCreatedBy(outlineId, EntityType.BODY)])
                    });
            bodyCounter += 1;
            continue;
        }

        const thickenedBodies = qCreatedBy(thickenId, EntityType.BODY);

        // Subtract the thickened projection from the current body being normalized
        // Target only the specific body being processed, not all slice bodies
        try
        {
            opBoolean(context, bodyId + "subtract", {
                        "tools" : thickenedBodies,
                        "targets" : body,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "keepTools" : false
                    });
        }
        catch
        {
            // Clean up the thickened bodies if boolean failed
            opDeleteBodies(context, bodyId + "cleanupFailedBoolean", {
                        "entities" : thickenedBodies
                    });
        }

        // Clean up helper geometry
        opDeleteBodies(context, bodyId + "cleanup2", {
                    "entities" : qUnion([projectionTarget, extractedOutlineTools, qCreatedBy(outlineId, EntityType.BODY)])
                });

        bodyCounter += 1;
    }
}

// Convert normalized slice bodies to sheet metal by extracting surfaces from START cap faces,
// then annotating and finalizing them following the canonical sheetMetalStart pattern.
// This matches the pattern used in sheetMetalStart.fs: extract surfaces with a sub-ID,
// then annotate and finalize using the base ID for proper sheet metal context management.
// Inputs:
//  - context : Execution context
//  - id : Feature ID for sheet metal operations (base ID, not sub-ID)
//  - trimmedSliceSets : Array of trimmed slice sets, each containing sliceIds arrays
//  - definition : Feature definition containing sheet metal parameters (bendRadius, matThick, kFactor, minimalClearance)
// Returns: none
export function convertSlicesToSheetMetal(context is Context, id is Id, trimmedSliceSets is array, definition is map)
{
    // Step 1: Collect all START cap faces from all slice bodies using attribute queries
    var allBodiesArray = [] as array;
    
    for (var sliceSet in trimmedSliceSets)
    {
        const setBodies = qUnion(mapArray(sliceSet.sliceIds, function(sliceId)
                {
                    return qCreatedBy(sliceId + "extrudeSlice", EntityType.BODY);
                }));
        allBodiesArray = append(allBodiesArray, setBodies);
    }
    
    const allBodies = qUnion(allBodiesArray);

    // Query all START cap faces using the attribute across all bodies
    const allStartCapFaces = qHasAttribute(qOwnedByBody(allBodies, EntityType.FACE), "laserItStartCap");

    const startCapFacesCount = size(evaluateQuery(context, allStartCapFaces));

    // Verify we have faces to convert
    if (isQueryEmpty(context, allStartCapFaces))
    {
        return;
    }

    // Step 2: Extract surfaces from the START cap faces
    // Use a sub-ID for the operation (following sheetMetalStart pattern from convertFaces function)
    const extractSurfaceId = id + "extractSurface";
    try
    {
        opExtractSurface(context, extractSurfaceId, {
                    "faces" : allStartCapFaces,
                    "offset" : 0 * meter,
                    "useFacesAroundToTrimOffset" : false
                });
    }
    catch
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_THICKEN);
    }

    // Step 3: Delete original solid bodies BEFORE annotation
    // This ensures qCreatedBy(id, EntityType.BODY) only finds the extracted surfaces
    try
    {
        opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : allBodies
                });
    }
    catch
    {
        // Non-critical if deletion fails
    }

    // Step 4: Annotate the extracted surface bodies with sheet metal attributes
    // CRITICAL: Use base id for queries, not extractSurfaceId (per SHEET_METAL_GOTCHAS.md)
    // After deleting original bodies, qCreatedBy(id, ...) only finds the extracted surfaces
    try
    {
        annotateSmSurfaceBodies(context, id, {
                    "surfaceBodies" : qCreatedBy(id, EntityType.BODY),
                    "bendEdgesAndFaces" : qNothing(),
                    "specialRadiiBends" : [],
                    "defaultRadius" : definition.bendRadius,
                    "controlsThickness" : true,
                    "thickness" : definition.matThick,
                    "thicknessDirection" : SMThicknessDirection.BACK,
                    "minimalClearance" : definition.minimalClearance,
                    "kFactor" : definition.kFactor,
                    "flipDirectionUp" : false,
                    "defaultTwoCornerStyle" : SMReliefStyle.SIMPLE,
                    "defaultThreeCornerStyle" : SMReliefStyle.SIMPLE,
                    "defaultBendReliefStyle" : SMReliefStyle.OBROUND,
                    "defaultCornerReliefScale" : 1.5,
                    "defaultRoundReliefDiameter" : 0 * meter,
                    "defaultSquareReliefWidth" : 0 * meter,
                    "defaultBendReliefDepthScale" : 2.0,
                    "defaultBendReliefScale" : 1.0625
                }, 0);
        // Check for errors after annotation (pattern from annotateConvertedFaces)
        if (getFeatureError(context, id) != undefined)
        {
            return;
        }
    }
    catch (error)
    {
        var messageAsEnum = try silent(error.message as ErrorStringEnum);
        if (messageAsEnum != undefined)
        {
            throw error;
        }
        throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
    }

    // Step 5: Finalize sheet metal geometry with updateSheetMetalGeometry
    // CRITICAL: Use base id for queries, not extractSurfaceId (per SHEET_METAL_GOTCHAS.md)
    // This matches the pattern from annotateConvertedFaces in sheetMetalStart.fs
    try
    {
        updateSheetMetalGeometry(context, id, {
                    "entities" : qUnion([qCreatedBy(id, EntityType.FACE), qCreatedBy(id, EntityType.EDGE)])
                });
    }
    catch (error)
    {
        var messageAsEnum = try silent(error.message as ErrorStringEnum);
        if (messageAsEnum == ErrorStringEnum.BOOLEAN_INVALID)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
        }
        else if (messageAsEnum == ErrorStringEnum.BAD_GEOMETRY ||
            messageAsEnum == ErrorStringEnum.THICKEN_FAILED)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_THICKEN);
        }
        else
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
        }
    }
}
