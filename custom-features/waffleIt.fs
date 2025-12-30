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

        // Use the coordinate system to define the bounding box (start and end of planes).
        // The reference frame position directly controls where the slicing grid is placed.
        var orientedBoundingBox = evBox3d(context, {
                "topology" : definition.selectedBody,
                "cSys" : referenceFrame,
                "tight" : true
            });

        var referenceFrameToWorldTransform = toWorld(referenceFrame);

        // Build a stack of slicing planes perpendicular to X, then Y, that span the oriented bounding box of the target body.
        // The function calculates which planes are needed based on the bounding box and spacing.
        // Each loop: create a sketch-sized rectangle around the body, extrude it to the material thickness, and retain the raw
        // sheets for a later trimming pass against the selected part.
        var xSliceResult = generateSheets(context, id, "X", orientedBoundingBox, definition.planeSpacing, referenceFrameToWorldTransform, definition.matThick);

        var ySliceResult = generateSheets(context, id, "Y", orientedBoundingBox, definition.planeSpacing, referenceFrameToWorldTransform, definition.matThick);

        // Intersect each sheet with the target solid to retain only in-bounds material before generating cross-slot geometry.
        var trimmedSheetsResult = trimSheetsToSolid(context, id, xSliceResult, ySliceResult, definition.selectedBody);

        // The XY nested loop takes every X slice and intersects it with every Y slice to form individual grid cells.
        // For each cell, it resolves all intersecting bodies, averages aligned edges to infer a mid-surface, and splits the
        // cell into two halves so the original X and Y slice sets can be trimmed against each other.
        generateCrossSlotGeometryForSlices(context, id, trimmedSheetsResult.xIntersectionIds, trimmedSheetsResult.yIntersectionIds, referenceFrame);

        // After trimming the intersecting grid, find all non-cap faces on each slice and project their geometry to
        // the START cap face. Thicken the flattened projections and remove the results from the slice.
        // This subtractive operation guarantees the slices lie inside of the original target volume.
        if (definition.normalizeGeometry == true)
        {
            // Process all X slice bodies together using attribute queries to find cap faces
            // After SUBTRACT_COMPLEMENT change, xIntersectionIds contains slice IDs (extrusion IDs)
            const allXSliceBodies = qUnion(mapArray(trimmedSheetsResult.xIntersectionIds, function(xSliceId)
                    {
                        return qCreatedBy(xSliceId + "extrudeRectangle", EntityType.BODY);
                    }));
            normalizeSliceGeometryForLasercutting(context, id + "XNormalize", allXSliceBodies, definition.matThick);

            // Process all Y slice bodies together using attribute queries to find cap faces
            // After SUBTRACT_COMPLEMENT change, yIntersectionIds contains slice IDs (extrusion IDs)
            const allYSliceBodies = qUnion(mapArray(trimmedSheetsResult.yIntersectionIds, function(ySliceId)
                    {
                        return qCreatedBy(ySliceId + "extrudeRectangle", EntityType.BODY);
                    }));
            normalizeSliceGeometryForLasercutting(context, id + "YNormalize", allYSliceBodies, definition.matThick);
        }

        // Convert to sheet metal if requested
        if (definition.outputSheetMetal == true)
        {
            convertSlicesToSheetMetal(context, id, trimmedSheetsResult, definition);
        }

        // Delete the input body if requested
        if (definition.deleteInputBody)
        {
            opDeleteBodies(context, id + "deleteInputBody", {
                        "entities" : definition.selectedBody
                    });
        }

    }, {});

// Generate a set of slice bodies based on a slice set definition with arbitrary orientation.
// This is a generic function that can handle any slice orientation, not just orthogonal X/Y axes.
// Inputs:
//  - context : Execution context
//  - sliceSetDefinition : Map containing:
//      - featureIdPrefix : Base id for naming geometry
//      - setLabel : String label for this set (e.g., "X", "Y", "Set0")
//      - normalVector : Normal vector for the slicing planes in reference frame coordinates
//      - upVector : Up vector for orienting the slice rectangles in reference frame coordinates
//      - planeSpacing : Distance between slices
//      - referenceFrameToWorldTransform : Transform from reference frame to world coordinates
//      - materialThickness : Extrusion depth for the slices
//      - orientedBoundingBox : Tight bounding box of the target body in reference frame coordinates
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
    const orientedBoundingBox = sliceSetDefinition.orientedBoundingBox;
    
    var slicePlanes = [] as array;
    var sliceIds = [] as array;
    
    // Calculate a perpendicular vector to the normal for determining rectangle dimensions
    // Choose the perpendicular that's most aligned with the up vector
    var rectangleWidthVector = cross(normalVector, upVector);
    var actualUpVector = upVector;
    
    if (norm(rectangleWidthVector) < TOLERANCE.zeroLength)
    {
        // If normal and up are parallel, pick an arbitrary perpendicular
        // and recalculate a proper up vector that's perpendicular to normal
        rectangleWidthVector = perpendicularVector(normalVector);
        actualUpVector = cross(normalVector, rectangleWidthVector);
        actualUpVector = normalize(actualUpVector);
    }
    else
    {
        rectangleWidthVector = normalize(rectangleWidthVector);
    }
    
    var rectangleHeightVector = cross(normalVector, rectangleWidthVector);
    rectangleHeightVector = normalize(rectangleHeightVector);
    
    // Project bounding box onto the slice plane coordinate system to find rectangle dimensions
    // Rectangle should be large enough to cover the entire bounding box
    const boundingBoxCorners = [
        orientedBoundingBox.minCorner,
        vector([orientedBoundingBox.maxCorner[0], orientedBoundingBox.minCorner[1], orientedBoundingBox.minCorner[2]]),
        vector([orientedBoundingBox.minCorner[0], orientedBoundingBox.maxCorner[1], orientedBoundingBox.minCorner[2]]),
        vector([orientedBoundingBox.minCorner[0], orientedBoundingBox.minCorner[1], orientedBoundingBox.maxCorner[2]]),
        vector([orientedBoundingBox.maxCorner[0], orientedBoundingBox.maxCorner[1], orientedBoundingBox.minCorner[2]]),
        vector([orientedBoundingBox.maxCorner[0], orientedBoundingBox.minCorner[1], orientedBoundingBox.maxCorner[2]]),
        vector([orientedBoundingBox.minCorner[0], orientedBoundingBox.maxCorner[1], orientedBoundingBox.maxCorner[2]]),
        orientedBoundingBox.maxCorner
    ];
    
    // Find the extent of the bounding box along each axis
    var minWidth = dot(boundingBoxCorners[0], rectangleWidthVector);
    var maxWidth = minWidth;
    var minHeight = dot(boundingBoxCorners[0], rectangleHeightVector);
    var maxHeight = minHeight;
    var minDepth = dot(boundingBoxCorners[0], normalVector);
    var maxDepth = minDepth;
    
    for (var corner in boundingBoxCorners)
    {
        const widthProjection = dot(corner, rectangleWidthVector);
        const heightProjection = dot(corner, rectangleHeightVector);
        const depthProjection = dot(corner, normalVector);
        
        minWidth = min(minWidth, widthProjection);
        maxWidth = max(maxWidth, widthProjection);
        minHeight = min(minHeight, heightProjection);
        maxHeight = max(maxHeight, heightProjection);
        minDepth = min(minDepth, depthProjection);
        maxDepth = max(maxDepth, depthProjection);
    }
    
    const rectangleWidth = maxWidth - minWidth;
    const rectangleHeight = maxHeight - minHeight;
    const rectangleCenterWidth = (maxWidth + minWidth) / 2;
    const rectangleCenterHeight = (maxHeight + minHeight) / 2;
    
    // Calculate which plane indices are needed to cover the bounding box along the normal direction
    const firstPlaneIndex = ceil(minDepth / planeSpacing);
    const lastPlaneIndex = floor(maxDepth / planeSpacing);
    
    var planeCounter = 0;
    for (var planeIndex = firstPlaneIndex; planeIndex <= lastPlaneIndex; planeIndex += 1)
    {
        const planeDepth = planeIndex * planeSpacing;
        
        // Calculate the plane origin in reference frame coordinates
        const sliceOrigin = (normalVector * planeDepth) + 
                           (rectangleWidthVector * rectangleCenterWidth) + 
                           (rectangleHeightVector * rectangleCenterHeight);
        
        // Create the plane and transform it to world coordinates
        // Use rectangleHeightVector (calculated from normal x width) as the actual up vector
        // This ensures the plane orientation matches the rectangle orientation
        const localPlane = plane(sliceOrigin, normalVector, rectangleHeightVector);
        const slicePlane = referenceFrameToWorldTransform * localPlane;
        
        const sliceId = featureIdPrefix + setLabel + planeCounter;
        const extrusionDirectionWorld = referenceFrameToWorldTransform.linear * normalVector;
        
        generateSliceSheet(context, sliceId, slicePlane, rectangleWidth, rectangleHeight, extrusionDirectionWorld, materialThickness);
        
        slicePlanes = append(slicePlanes, slicePlane);
        sliceIds = append(sliceIds, sliceId);
        planeCounter += 1;
    }
    
    return {
        "slicePlanes" : slicePlanes,
        "sliceIds" : sliceIds,
        "setLabel" : setLabel,
        "normalVector" : normalVector,
        "upVector" : upVector
    };
}

// DEPRECATED: Legacy wrapper for generateSliceSet() that uses X/Y axis labels.
// Maintained for backward compatibility with existing code.
// Create rectangular sheets along a specified axis, returning plane definitions for downstream trimming and rib generation.
// Inputs:
//  - featureIdPrefix : Base id used when naming all geometry created in this helper
//  - axisLabel : Either "X" or "Y" to select the normal and sketch dimensions for the slicing plane
//  - orientedBoundingBox : Tight bounding box for the selected body in the reference frame
//  - planeSpacing : Distance between slices
//  - referenceFrameToWorldTransform : Transform aligning the local slice planes with world coordinates
//  - materialThickness : Extrusion depth for the raw sheet
// Returns: map containing the ordered list of slice planes
export function generateSheets(context is Context, featureIdPrefix is Id, axisLabel is string, orientedBoundingBox is Box3d, planeSpacing is ValueWithUnits, referenceFrameToWorldTransform is Transform, materialThickness is ValueWithUnits)
{
    // Map legacy X/Y axis labels to normal and up vectors
    var normalVector = vector([1, 0, 0]);
    var upVector = vector([0, 1, 0]);
    
    if (axisLabel == "Y")
    {
        normalVector = vector([0, 1, 0]);
        upVector = vector([0, 0, 1]);
    }
    
    // Call the generic slice set generation function
    const sliceSetDefinition = {
        "featureIdPrefix" : featureIdPrefix,
        "setLabel" : axisLabel,
        "normalVector" : normalVector,
        "upVector" : upVector,
        "planeSpacing" : planeSpacing,
        "referenceFrameToWorldTransform" : referenceFrameToWorldTransform,
        "materialThickness" : materialThickness,
        "orientedBoundingBox" : orientedBoundingBox
    };
    
    return generateSliceSet(context, sliceSetDefinition);
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
                    return qCreatedBy(sliceId + "extrudeRectangle", EntityType.BODY);
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
            const sliceBody = qCreatedBy(sliceId + "extrudeRectangle", EntityType.BODY);
            
            if (!isQueryEmpty(context, sliceBody))
            {
                // Verify START/END caps still exist using cap entity queries
                const startCapQuery = qCapEntity(sliceId + "extrudeRectangle", CapType.START, EntityType.FACE);
                const endCapQuery = qCapEntity(sliceId + "extrudeRectangle", CapType.END, EntityType.FACE);
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

// DEPRECATED: Legacy wrapper for trimSliceSetsToSolid() that uses separate X/Y parameters.
// Maintained for backward compatibility with existing code.
// Intersect every raw sheet with the target body to keep only the in-bounds material for follow-on trimming.
// Uses a single batch SUBTRACT_COMPLEMENT operation for all slices to preserve attributes and avoid iterative issues.
// Inputs:
//  - featureIdPrefix : Base id used to regenerate the X/Y identifiers for each slice
//  - xSliceResult, ySliceResult : Maps containing sliceIds arrays for X- and Y-oriented slices
//  - targetBody : Body query representing the part being sliced
// Returns: map containing the slice IDs for robust cap querying
export function trimSheetsToSolid(context is Context, featureIdPrefix is Id, xSliceResult is map, ySliceResult is map, targetBody is Query)
{
    // Convert X and Y slice results to array of slice sets
    const sliceSets = [xSliceResult, ySliceResult];
    
    // Call the generic trimming function
    const trimmedSliceSets = trimSliceSetsToSolid(context, featureIdPrefix, sliceSets, targetBody);
    
    // Extract X and Y results from the trimmed sets
    const trimmedXSet = trimmedSliceSets[0];
    const trimmedYSet = trimmedSliceSets[1];
    
    // Return in the legacy format for backward compatibility
    return {
            "xIntersectionIds" : trimmedXSet.sliceIds,
            "yIntersectionIds" : trimmedYSet.sliceIds,
            "xSliceIds" : trimmedXSet.sliceIds,
            "ySliceIds" : trimmedYSet.sliceIds
        };
}

// Generate slots between multiple slice sets where they intersect.
// Generic function that can handle N slice sets with arbitrary orientations.
// NOTE: Current implementation is a placeholder - actual generic slot generation is complex
// and depends on the specific slotting strategy (perpendicular pairs, all-to-all, etc.)
// For now, this delegates to the legacy two-set perpendicular implementation when applicable.
// Inputs:
//  - context : Execution context
//  - featureIdPrefix : Base id for operations
//  - sliceSets : Array of trimmed slice sets with metadata (sliceIds, normalVector, etc.)
//  - referenceFrame : Coordinate system for splitting operations
// Returns: None (modifies slice bodies in place)
export function generateSlotsForSliceSets(context is Context, featureIdPrefix is Id, sliceSets is array, referenceFrame is CoordSystem)
{
    // For now, handle the common case of exactly 2 perpendicular slice sets
    // Future: Implement generic N-set slotting with configurable pairing strategies
    if (size(sliceSets) == 2)
    {
        // Delegate to the existing two-set implementation
        const set0SliceIds = sliceSets[0].sliceIds;
        const set1SliceIds = sliceSets[1].sliceIds;
        generateCrossSlotGeometryForSlices(context, featureIdPrefix, set0SliceIds, set1SliceIds, referenceFrame);
    }
    else
    {
        // TODO: Implement generic N-set slot generation
        // This would need to:
        // 1. Determine which sets should have slots cut between them
        // 2. For each pair, copy slices and find intersections
        // 3. Split intersection cells appropriately based on set orientations
        // 4. Subtract slot geometry from appropriate slice sets
    }
}

// DEPRECATED: Legacy function for two perpendicular slice sets (specifically X and Y).
// This function is retained for backward compatibility and as a reference implementation.
// Copy all trimmed slices, then perform a single subtract-complement boolean using the copied X slices as tools and the copied Y slices as targets.
// This trims the Y slice set against all X slices in one operation to reduce the number of booleans required for slot generation.
// Then split the resultant slot intersection cells in half by a length averaging heuristic to determine placement and assign
// each split cell half to a slice set for boolean subtraction. Finally remove the slot geometry from the slices in one subtraction operation per slice set.
// Inputs:
//  - featureIdPrefix : Base id used to regenerate the X/Y boolean identifiers for each intersection cell
//  - xIntersectionIds, yIntersectionIds : Ordered identifiers for the trimmed X and Y slice bodies
//  - referenceFrame : Coordinate system establishing the Z axis for mid-plane evaluation (retained for compatibility)
// Returns: map containing the copied slot ids used downstream
export function generateCrossSlotGeometryForSlices(context is Context, featureIdPrefix is Id, xIntersectionIds is array, yIntersectionIds is array, referenceFrame is CoordSystem)
{
    var xSlotIds = [] as array;
    var ySlotIds = [] as array;
    var splitPlaneIds = [] as array;
    var splitIds = [] as array;


    // 1. Create Copies
    // xIntersectionIds and yIntersectionIds now contain slice IDs (extrusion IDs) after SUBTRACT_COMPLEMENT change
    for (var xPlaneIndex = 0; xPlaneIndex < size(xIntersectionIds); xPlaneIndex += 1)
    {
        const xSliceBody = qCreatedBy(xIntersectionIds[xPlaneIndex] + "extrudeRectangle", EntityType.BODY);
        // Skip if body doesn't exist (was deleted during intersection)
        if (!isQueryEmpty(context, xSliceBody))
        {
            const xCopyId = featureIdPrefix + "XCopy" + xPlaneIndex;
            opPattern(context, xCopyId, {
                        "entities" : xSliceBody,
                        "transforms" : [identityTransform()],
                        "instanceNames" : ["1"]
                    });
            xSlotIds = append(xSlotIds, xCopyId);
        }
    }

    for (var yPlaneIndex = 0; yPlaneIndex < size(yIntersectionIds); yPlaneIndex += 1)
    {
        const ySliceBody = qCreatedBy(yIntersectionIds[yPlaneIndex] + "extrudeRectangle", EntityType.BODY);
        // Skip if body doesn't exist (was deleted during intersection)
        if (!isQueryEmpty(context, ySliceBody))
        {
            const yCopyId = featureIdPrefix + "YCopy" + yPlaneIndex;
            opPattern(context, yCopyId, {
                        "entities" : ySliceBody,
                        "transforms" : [identityTransform()],
                        "instanceNames" : ["1"]
                    });
            ySlotIds = append(ySlotIds, yCopyId);
        }
    }

    const copiedXSlices = qUnion(mapArray(xSlotIds, function(slotId)
            {
                return qCreatedBy(slotId, EntityType.BODY);
            }));
    const copiedYSlices = qUnion(mapArray(ySlotIds, function(slotId)
            {
                return qCreatedBy(slotId, EntityType.BODY);
            }));

    // 2. Generate Intersection Geometry (Subtract Complement)
    opBoolean(context, featureIdPrefix + "BatchCrossSlots", {
                "tools" : copiedXSlices,
                "targets" : copiedYSlices,
                "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                "keepTargets" : true
            });

    var splitToolsForX = [] as array;
    var splitToolsForY = [] as array;

    // 3. Evaluate Intersections and Split them
    var intersectionCells = evaluateQuery(context, copiedYSlices);
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
        const planeId = featureIdPrefix + "XY" + cellIndex + "plane1";
        opPlane(context, planeId, {
                    "plane" : slicePlane
                });
        const splitId = featureIdPrefix + "XY" + cellIndex + "splitPart1";
        opSplitPart(context, splitId, {
                    "targets" : intersectionCell,
                    "tool" : qCreatedBy(planeId, EntityType.BODY)
                });

        splitToolsForX = append(splitToolsForX, qFarthestAlong(qOwnerBody(qCreatedBy(splitId)), referenceFrame.zAxis));
        splitToolsForY = append(splitToolsForY, qFarthestAlong(qOwnerBody(qCreatedBy(splitId)), -referenceFrame.zAxis));

        splitPlaneIds = append(splitPlaneIds, planeId);
        splitIds = append(splitIds, splitId);

        cellIndex += 1;
    }

    // 4. Perform Final Subtraction on Original Slices

    // Helper to resolve slice IDs to Body queries (after SUBTRACT_COMPLEMENT change, these are extrusion IDs)
    const originalXSlices = qUnion(mapArray(xIntersectionIds, function(id)
            {
                return qCreatedBy(id + "extrudeRectangle", EntityType.BODY);
            }));

    const originalYSlices = qUnion(mapArray(yIntersectionIds, function(id)
            {
                return qCreatedBy(id + "extrudeRectangle", EntityType.BODY);
            }));

    if (size(splitToolsForX) > 0)
    {
        opBoolean(context, featureIdPrefix + "booleanXSlots", {
                    "tools" : qUnion(splitToolsForX),
                    "targets" : originalXSlices, // Target the original X slices
                    "operationType" : BooleanOperationType.SUBTRACTION
                });
    }
    // const xSlotFaces = qCreatedBy(featureIdPrefix + "booleanXSlots", EntityType.FACE); // Potential future clearance support

    if (size(splitToolsForY) > 0)
    {
        opBoolean(context, featureIdPrefix + "booleanYSlots", {
                    "tools" : qUnion(splitToolsForY),
                    "targets" : originalYSlices, // Target the original Y slices
                    "operationType" : BooleanOperationType.SUBTRACTION
                });
    }
    // const ySlotFaces = qCreatedBy(featureIdPrefix + "booleanYSlots", EntityType.FACE); // Potential future clearance support

    const splitPlanes = qUnion(mapArray(splitPlaneIds, function(splitPlaneId)
            {
                return qCreatedBy(splitPlaneId, EntityType.BODY);
            }));
    const splitBodies = qUnion(mapArray(splitIds, function(splitId)
            {
                return qCreatedBy(splitId, EntityType.BODY);
            }));

    opDeleteBodies(context, featureIdPrefix + "deleteCrossSlotHelpers", {
                "entities" : qUnion([copiedXSlices, copiedYSlices, splitPlanes, splitBodies])
            });

    return { "xSlotIds" : xSlotIds, "ySlotIds" : ySlotIds };
}

// Sketch and extrude a rectangular slice at the provided plane, retaining the untrimmed sheet for a later boolean against the target body.
// Inputs:
//  - sliceId : Unique Id prefix used for sketch and extrusion operations
//  - slicePlane : Plane definition representing the slice location and orientation
//  - rectangleWidth, rectangleHeight : Dimensions of the rectangle sketched on the plane
//  - extrusionDirection : Direction vector for the slice thickening
//  - materialThickness : Extrusion depth matching the stock thickness
// Returns: none
export function generateSliceSheet(context is Context, sliceId is Id, slicePlane is Plane, rectangleWidth is ValueWithUnits, rectangleHeight is ValueWithUnits, extrusionDirection is Vector, materialThickness is ValueWithUnits)
{
    var sliceSketch = newSketchOnPlane(context, sliceId + "sketch", {
            "sketchPlane" : slicePlane
        });

    skRectangle(sliceSketch, "rectangle1", {
                "firstCorner" : -vector([rectangleWidth, rectangleHeight]) / 2,
                "secondCorner" : vector([rectangleWidth, rectangleHeight]) / 2
            });

    skSolve(sliceSketch);

    // Extrude a rectangular slice surrounding the object symmetrically
    // Use startBound and endBound with equal depths to achieve symmetric extrusion
    opExtrude(context, sliceId + "extrudeRectangle", {
                "entities" : qSketchRegion(sliceId + "sketch", false),
                "direction" : extrusionDirection,
                "endBound" : BoundingType.BLIND,
                "endDepth" : materialThickness / 2,
                "startBound" : BoundingType.BLIND,
                "startDepth" : materialThickness / 2
            });

    // Tag the START cap face with an attribute so it can be reliably found after topology changes
    const startCapFace = qCapEntity(sliceId + "extrudeRectangle", CapType.START, EntityType.FACE);
    const startCapCount = size(evaluateQuery(context, startCapFace));
    setAttribute(context, {
                "entities" : startCapFace,
                "name" : "laserItStartCap",
                "attribute" : true
            });

    // Tag the END cap face with an attribute so it can be reliably found after topology changes
    const endCapFace = qCapEntity(sliceId + "extrudeRectangle", CapType.END, EntityType.FACE);
    const endCapCount = size(evaluateQuery(context, endCapFace));
    setAttribute(context, {
                "entities" : endCapFace,
                "name" : "laserItEndCap",
                "attribute" : true
            });

    opDeleteBodies(context, sliceId + "deleteSketch", {
                "entities" : qCreatedBy(sliceId + "sketch")
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
//  - trimmedSheetsResult : Map containing xIntersectionIds and yIntersectionIds arrays
//  - definition : Feature definition containing sheet metal parameters (bendRadius, matThick, kFactor, minimalClearance)
// Returns: none
export function convertSlicesToSheetMetal(context is Context, id is Id, trimmedSheetsResult is map, definition is map)
{

    // Step 1: Collect all START cap faces from all slice bodies using attribute queries
    const xIntersectionIds = trimmedSheetsResult.xIntersectionIds;
    const yIntersectionIds = trimmedSheetsResult.yIntersectionIds;

    // Get all bodies from X and Y slices (after SUBTRACT_COMPLEMENT change, these are extrusion IDs)
    const allXBodies = qUnion(mapArray(xIntersectionIds, function(xId)
            {
                return qCreatedBy(xId + "extrudeRectangle", EntityType.BODY);
            }));
    const allYBodies = qUnion(mapArray(yIntersectionIds, function(yId)
            {
                return qCreatedBy(yId + "extrudeRectangle", EntityType.BODY);
            }));
    const allBodies = qUnion([allXBodies, allYBodies]);

    const totalBodies = size(evaluateQuery(context, allBodies));

    // Query all START cap faces using the attribute across all bodies
    const allStartCapFaces = qHasAttribute(qOwnedByBody(allBodies, EntityType.FACE), "laserItStartCap");

    const startCapFacesCount = size(evaluateQuery(context, allStartCapFaces));

    // Verify we have faces to convert
    if (isQueryEmpty(context, allStartCapFaces))
    {
        println("  ERROR: No START cap faces found - returning");
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
    const allXSliceBodies = qUnion(mapArray(xIntersectionIds, function(xSliceId)
            {
                return qCreatedBy(xSliceId + "extrudeRectangle", EntityType.BODY);
            }));
    const allYSliceBodies = qUnion(mapArray(yIntersectionIds, function(ySliceId)
            {
                return qCreatedBy(ySliceId + "extrudeRectangle", EntityType.BODY);
            }));

    try
    {
        opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : qUnion([allXSliceBodies, allYSliceBodies])
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
