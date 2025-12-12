// Laser It slices a selected body into a grid of extruded rectangles to prepare geometry for laser cutting.
// Inputs:
//  - selectedBody : Body query to slice
//  - planeSpacing : Distance between slicing planes along the X and Y axes of the reference frame
//  - matThick : Material thickness that controls extrusion depth
//  - defRefFrame : Boolean to select a mate connector as the slicing reference frame
//  - referenceFrame : Mate connector query when defRefFrame is true, defines the placement of the slicing grid
//  - outputSheetMetal : Boolean to output results as sheet metal bodies
FeatureScript 2815;
import(path : "onshape/std/geometry.fs", version : "2815.0");
import(path : "onshape/std/query.fs", version : "2815.0");
import(path : "onshape/std/box.fs", version : "2815.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2815.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2815.0");
import(path : "onshape/std/sheetMetalStart.fs", version : "2815.0");
import(path : "onshape/std/evaluate.fs", version : "2815.0");
import(path : "onshape/std/topologyUtils.fs", version : "2815.0");
import(path : "onshape/std/attributes.fs", version : "2815.0");

annotation { "Feature Type Name" : "Laser It" }
export const laserIt = defineFeature(function(context is Context, id is Id, definition is map)
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
        
        println("After generateCrossSlotGeometryForSlices");

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
            println("Starting normalization for X slices, body count = " ~ size(evaluateQuery(context, allXSliceBodies)));
            normalizeSliceGeometryForLasercutting(context, id + "XNormalize", allXSliceBodies, definition.matThick);

            // Process all Y slice bodies together using attribute queries to find cap faces
            // After SUBTRACT_COMPLEMENT change, yIntersectionIds contains slice IDs (extrusion IDs)
            const allYSliceBodies = qUnion(mapArray(trimmedSheetsResult.yIntersectionIds, function(ySliceId)
                {
                    return qCreatedBy(ySliceId + "extrudeRectangle", EntityType.BODY);
                }));
            println("Starting normalization for Y slices, body count = " ~ size(evaluateQuery(context, allYSliceBodies)));
            normalizeSliceGeometryForLasercutting(context, id + "YNormalize", allYSliceBodies, definition.matThick);
        }

        // Convert to sheet metal if requested
        if (definition.outputSheetMetal == true)
        {
            convertSlicesToSheetMetal(context, id, trimmedSheetsResult, definition);
        }

    });

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
    var slicePlanes = [] as array;
    var sliceIds = [] as array;
    var planeNormal = vector([1, 0, 0]);
    var planeUpVector = vector([0, 1, 0]);
    var rectangleWidth = orientedBoundingBox.maxCorner[1] - orientedBoundingBox.minCorner[1];
    var rectangleHeight = orientedBoundingBox.maxCorner[2] - orientedBoundingBox.minCorner[2];
    var rectangleCenterY = (orientedBoundingBox.maxCorner[1] + orientedBoundingBox.minCorner[1]) / 2;
    var rectangleCenterZ = (orientedBoundingBox.maxCorner[2] + orientedBoundingBox.minCorner[2]) / 2;
    var boundingMin = orientedBoundingBox.minCorner[0];
    var boundingMax = orientedBoundingBox.maxCorner[0];

    if (axisLabel == "Y")
    {
        planeNormal = vector([0, 1, 0]);
        planeUpVector = vector([0, 0, 1]);
        rectangleWidth = orientedBoundingBox.maxCorner[2] - orientedBoundingBox.minCorner[2];
        rectangleHeight = orientedBoundingBox.maxCorner[0] - orientedBoundingBox.minCorner[0];
        rectangleCenterY = (orientedBoundingBox.maxCorner[0] + orientedBoundingBox.minCorner[0]) / 2;
        rectangleCenterZ = (orientedBoundingBox.maxCorner[2] + orientedBoundingBox.minCorner[2]) / 2;
        boundingMin = orientedBoundingBox.minCorner[1];
        boundingMax = orientedBoundingBox.maxCorner[1];
    }

    // Calculate which plane indices are needed to cover the bounding box
    // Reference frame origin is at position 0, planes are at positions index * spacing
    // We need planes from the first one >= boundingMin to the last one <= boundingMax
    var firstPlaneIndex = ceil(boundingMin / planeSpacing);
    var lastPlaneIndex = floor(boundingMax / planeSpacing);
    
    var planeCounter = 0;
    for (var planeIndex = firstPlaneIndex; planeIndex <= lastPlaneIndex; planeIndex += 1)
    {
        // Position planes relative to the reference frame origin (0 in reference frame coordinates)
        // Planes can be at negative, zero, or positive positions depending on bounding box
        var planeLocation = planeIndex * planeSpacing;
        var sliceOrigin = vector([0 * millimeter, 0 * millimeter, 0 * millimeter]);

        if (axisLabel == "X")
        {
            sliceOrigin = vector([planeLocation, rectangleCenterY, rectangleCenterZ]);
        }
        else
        {
            sliceOrigin = vector([rectangleCenterY, planeLocation, rectangleCenterZ]);
        }

        var slicePlane = referenceFrameToWorldTransform * plane(sliceOrigin, planeNormal, planeUpVector);
        var sliceId = featureIdPrefix + axisLabel + planeCounter;
        // Transform the extrusion direction from local to world coordinates
        var extrusionDirectionWorld = referenceFrameToWorldTransform.linear * planeNormal;
        generateSliceSheet(context, sliceId, slicePlane, rectangleWidth, rectangleHeight, extrusionDirectionWorld, materialThickness);
        slicePlanes = append(slicePlanes, slicePlane);
        sliceIds = append(sliceIds, sliceId);
        planeCounter += 1;
    }

    return { "slicePlanes" : slicePlanes, "sliceIds" : sliceIds };
}

// Intersect every raw sheet with the target body to keep only the in-bounds material for follow-on trimming.
// Uses a single batch SUBTRACT_COMPLEMENT operation for all slices to preserve attributes and avoid iterative issues.
// Inputs:
//  - featureIdPrefix : Base id used to regenerate the X/Y identifiers for each slice
//  - xSliceResult, ySliceResult : Maps containing sliceIds arrays for X- and Y-oriented slices
//  - targetBody : Body query representing the part being sliced
// Returns: map containing the slice IDs for robust cap querying
export function trimSheetsToSolid(context is Context, featureIdPrefix is Id, xSliceResult is map, ySliceResult is map, targetBody is Query)
{
    var xSliceIds = [] as array;
    var ySliceIds = [] as array;
    
    const xOriginalSliceIds = xSliceResult.sliceIds;
    const yOriginalSliceIds = ySliceResult.sliceIds;

    // Build queries for all X and Y slice bodies
    const allXSliceBodies = qUnion(mapArray(xOriginalSliceIds, function(sliceId)
        {
            return qCreatedBy(sliceId + "extrudeRectangle", EntityType.BODY);
        }));
    const allYSliceBodies = qUnion(mapArray(yOriginalSliceIds, function(sliceId)
        {
            return qCreatedBy(sliceId + "extrudeRectangle", EntityType.BODY);
        }));
    const allSliceBodies = qUnion([allXSliceBodies, allYSliceBodies]);
    
    // Perform single batch SUBTRACT_COMPLEMENT operation for all slices at once
    // This preserves attributes and is much more efficient than iterative operations
    opBoolean(context, featureIdPrefix + "batchIntersection", {
                "tools" : targetBody,
                "targets" : allSliceBodies,
                "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                "keepTools" : true
            });
    
    // Now check each slice to see if it survived and has valid caps
    for (var xPlaneIndex = 0; xPlaneIndex < size(xOriginalSliceIds); xPlaneIndex += 1)
    {
        var xSliceId = xOriginalSliceIds[xPlaneIndex];
        const sliceBody = qCreatedBy(xSliceId + "extrudeRectangle", EntityType.BODY);
        
        if (!isQueryEmpty(context, sliceBody))
        {
            // Check if attributes persisted (they should with SUBTRACT_COMPLEMENT)
            const attributedStartCaps = evaluateQuery(context, qHasAttribute(qOwnedByBody(sliceBody, EntityType.FACE), "laserItStartCap"));
            
            // Also verify START/END caps still exist using cap entity queries
            const startCapQuery = qCapEntity(xSliceId + "extrudeRectangle", CapType.START, EntityType.FACE);
            const endCapQuery = qCapEntity(xSliceId + "extrudeRectangle", CapType.END, EntityType.FACE);
            const remainingStartCaps = evaluateQuery(context, startCapQuery);
            const remainingEndCaps = evaluateQuery(context, endCapQuery);
            
            println("X slice " ~ xPlaneIndex ~ ": startCaps=" ~ size(remainingStartCaps) ~ ", endCaps=" ~ size(remainingEndCaps) ~ ", attributed=" ~ size(attributedStartCaps));
            
            // Delete body if we don't have at least one face of each cap type
            if (size(remainingStartCaps) == 0 || size(remainingEndCaps) == 0)
            {
                println("  DELETING X slice " ~ xPlaneIndex ~ " - missing caps");
                opDeleteBodies(context, featureIdPrefix + "deleteXSlice" + xPlaneIndex, {
                    "entities" : sliceBody
                });
                continue;
            }
            
            // Store the slice ID for later robust cap querying
            xSliceIds = append(xSliceIds, xSliceId);
        }
    }

    for (var yPlaneIndex = 0; yPlaneIndex < size(yOriginalSliceIds); yPlaneIndex += 1)
    {
        var ySliceId = yOriginalSliceIds[yPlaneIndex];
        const sliceBody = qCreatedBy(ySliceId + "extrudeRectangle", EntityType.BODY);
        
        if (!isQueryEmpty(context, sliceBody))
        {
            // Check if attributes persisted (they should with SUBTRACT_COMPLEMENT)
            const attributedStartCaps = evaluateQuery(context, qHasAttribute(qOwnedByBody(sliceBody, EntityType.FACE), "laserItStartCap"));
            
            // Also verify START/END caps still exist using cap entity queries
            const startCapQuery = qCapEntity(ySliceId + "extrudeRectangle", CapType.START, EntityType.FACE);
            const endCapQuery = qCapEntity(ySliceId + "extrudeRectangle", CapType.END, EntityType.FACE);
            const remainingStartCaps = evaluateQuery(context, startCapQuery);
            const remainingEndCaps = evaluateQuery(context, endCapQuery);
            
            println("Y slice " ~ yPlaneIndex ~ ": startCaps=" ~ size(remainingStartCaps) ~ ", endCaps=" ~ size(remainingEndCaps) ~ ", attributed=" ~ size(attributedStartCaps));
            
            // Delete body if we don't have at least one face of each cap type
            if (size(remainingStartCaps) == 0 || size(remainingEndCaps) == 0)
            {
                println("  DELETING Y slice " ~ yPlaneIndex ~ " - missing caps");
                opDeleteBodies(context, featureIdPrefix + "deleteYSlice" + yPlaneIndex, {
                    "entities" : sliceBody
                });
                continue;
            }
            
            // Store the slice ID for later robust cap querying
            ySliceIds = append(ySliceIds, ySliceId);
        }
    }

    println("After trimSheetsToSolid: xSliceIds count = " ~ size(xSliceIds) ~ ", ySliceIds count = " ~ size(ySliceIds));
    
    // With SUBTRACT_COMPLEMENT, the bodies are the original extrusion bodies (modified in place)
    // So we return the slice IDs for querying the bodies, not the intersection operation IDs
    return { 
        "xIntersectionIds" : xSliceIds,  // These are now the extrusion slice IDs, not boolean operation IDs
        "yIntersectionIds" : ySliceIds,
        "xSliceIds" : xSliceIds,
        "ySliceIds" : ySliceIds
    };
}

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
    println("Setting attribute on " ~ sliceId ~ " START cap, face count = " ~ startCapCount);
    setAttribute(context, {
        "entities" : startCapFace,
        "name" : "laserItStartCap",
        "attribute" : true
    });

    // Tag the END cap face with an attribute so it can be reliably found after topology changes
    const endCapFace = qCapEntity(sliceId + "extrudeRectangle", CapType.END, EntityType.FACE);
    const endCapCount = size(evaluateQuery(context, endCapFace));
    println("Setting attribute on " ~ sliceId ~ " END cap, face count = " ~ endCapCount);
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
    
    println("normalizeSliceGeometryForLasercutting: Processing " ~ size(bodyArray) ~ " bodies");
    
    for (var body in bodyArray)
    {
        const bodyId = idPrefix + "Body" + bodyCounter;
        
        // Find all faces on this body
        const bodyFaces = qOwnedByBody(body, EntityType.FACE);
        const totalFaces = size(evaluateQuery(context, bodyFaces));
        
        // Get the START cap faces on this body using the attribute
        const startCapFacesOnBody = qIntersection([qHasAttribute(bodyFaces, "laserItStartCap"), bodyFaces]);
        const startCapFacesArray = evaluateQuery(context, startCapFacesOnBody);
        
        println("  Body " ~ bodyCounter ~ ": totalFaces=" ~ totalFaces ~ ", startCapFaces=" ~ size(startCapFacesArray));
        
        // Verify we have at least one START cap face
        if (size(startCapFacesArray) == 0)
        {
            println("  Body " ~ bodyCounter ~ ": SKIPPED - No START cap faces found");
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
        println("  Body " ~ bodyCounter ~ ": nonNormalFaces=" ~ nonNormalFacesCount);
        
        // Check for null case (nothing to normalize)
        if (isQueryEmpty(context, nonNormalFaces))
        {
            println("  Body " ~ bodyCounter ~ ": SKIPPED - No non-normal faces to normalize");
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

// Convert normalized slice bodies to sheet metal by thickening their cap faces.
// This is a simplified version stripped down from sheetMetalStart's thickenToSheetMetal function.
// Takes face queries, extracts surfaces, annotates them with sheet metal attributes, and finalizes.
// The original solid slice bodies are deleted.
// Inputs:
//  - id : Feature ID used for sheet metal operations
//  - trimmedSheetsResult : Map containing xIntersectionIds and yIntersectionIds arrays
//  - xSliceResult : Map containing slicePlanes for X slices
//  - ySliceResult : Map containing slicePlanes for Y slices
//  - definition : Feature definition map containing sheet metal parameters
// Returns: none
// Convert normalized slice bodies to sheet metal by thickening the START cap faces.
// Uses attribute queries to find START cap faces, which persist through topology changes.
// Each body will have exactly one START cap face to work with, even if prior operations split bodies.
// Inputs:
//  - context : Execution context
//  - id : Feature ID for sheet metal operations
//  - trimmedSheetsResult : Map containing intersection IDs
//  - definition : Feature definition containing sheet metal parameters
export function convertSlicesToSheetMetal(context is Context, id is Id, trimmedSheetsResult is map, definition is map)
{
    println("convertSlicesToSheetMetal: Starting sheet metal conversion");
    
    // Wrapper function that calls the standard library sheetMetalStart
    // This allows us to leverage the full functionality of defineSheetMetalFeature
    
    // Step 1: Collect all START cap faces from all slice bodies using attribute queries
    const xIntersectionIds = trimmedSheetsResult.xIntersectionIds;
    const yIntersectionIds = trimmedSheetsResult.yIntersectionIds;
    
    println("  xIntersectionIds count = " ~ size(xIntersectionIds) ~ ", yIntersectionIds count = " ~ size(yIntersectionIds));
    
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
    println("  Total bodies = " ~ totalBodies);
    
    // Query all START cap faces using the attribute across all bodies
    const allStartCapFaces = qHasAttribute(qOwnedByBody(allBodies, EntityType.FACE), "laserItStartCap");
    
    const startCapFacesCount = size(evaluateQuery(context, allStartCapFaces));
    println("  START cap faces found = " ~ startCapFacesCount);
    
    // Verify we have faces to convert
    if (isQueryEmpty(context, allStartCapFaces))
    {
        println("  ERROR: No START cap faces found - returning");
        return;
    }
    
    // Step 2: Use the attribute-identified START cap faces
    var allFacesToConvert = allStartCapFaces;
    
    // Step 3: Call the standard library's sheetMetalStart with THICKEN process
    // This gives us the benefits of defineSheetMetalFeature (surface hiding, proper context naming)
    try
    {
        sheetMetalStart(context, id + "sheetMetal", {
            "initEntities" : allFacesToConvert,
            "process" : SMProcessType.THICKEN,
            "regions" : allFacesToConvert,
            "bends" : qNothing(),
            "radius" : definition.bendRadius,
            "minimalClearance" : definition.minimalClearance,
            "thickness" : definition.matThick,
            "oppositeDirection" : true,
            "kFactor" : definition.kFactor
        });
        
        // Step 3.5: Update the sheet metal model attributes to show "Laser It" as the controlling feature name
        // Query all created sheet metal model bodies
        const sheetMetalBodies = qCreatedBy(id + "sheetMetal", EntityType.BODY);
        
        // Get the sheet metal model attributes from the created bodies
        const modelAttributes = getAttributes(context, {
            "entities" : sheetMetalBodies,
            "attributePattern" : asSMAttribute({ "objectType" : SMObjectType.MODEL })
        });
        
        // Update each model attribute to use "Laser It" as the controlling feature name
        for (var existingAttribute in modelAttributes)
        {
            // Create a copy of the attribute with modified controllingFeatureId fields
            var updatedAttribute = existingAttribute;
            
            // Helper array of field names that contain controllingFeatureId to update
            const fieldsToUpdate = [
                "frontThickness",
                "backThickness",
                "k-factor",
                "minimalClearance",
                "defaultCornerReliefScale",
                "defaultRoundReliefDiameter",
                "defaultSquareReliefWidth",
                "defaultBendReliefScale",
                "defaultBendReliefDepthScale"
            ];
            
            // Update controllingFeatureId for each field that has it
            for (var fieldName in fieldsToUpdate)
            {
                if (updatedAttribute[fieldName] != undefined && updatedAttribute[fieldName].controllingFeatureId != undefined)
                {
                    updatedAttribute[fieldName].controllingFeatureId = "Laser It";
                }
            }
            
            // Update the attributeId to also reference "Laser It"
            updatedAttribute.attributeId = "Laser It";
            
            // Use replaceSMAttribute to properly update the attribute
            replaceSMAttribute(context, existingAttribute, updatedAttribute);
        }
    }
    catch (error)
    {
        // Propagate sheet metal errors appropriately
        var messageAsEnum = try silent(error.message as ErrorStringEnum);
        if (messageAsEnum != undefined)
        {
            throw error;
        }
        throw regenError(ErrorStringEnum.SHEET_METAL_REBUILD_ERROR);
    }
    
    // Step 4: Delete original solid bodies after successful sheet metal creation
    // Use the xIntersectionIds and yIntersectionIds already declared above (now extrusion IDs)
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
        // Non-critical if deletion fails - sheet metal bodies are already created
    }
}

