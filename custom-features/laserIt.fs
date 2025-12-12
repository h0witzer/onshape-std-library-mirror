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
        var intersectionResult = generateCrossSlotGeometryForSlices(context, id, trimmedSheetsResult.xIntersectionIds, trimmedSheetsResult.yIntersectionIds, referenceFrame);

        // After trimming the intersecting grid, find all non-normal cut faces on a given slice and project their geometry to
        // the surface of the slice. Thicken the flattened projections and remove the results from the slice.
        // This subtractive operation guarantees the slices lie inside of the original target volume, where additive methods wouldn't.
        // Process all X slice bodies together
        const allXSliceBodies = qUnion(mapArray(trimmedSheetsResult.xIntersectionIds, function(xIntersectionId)
            {
                return qCreatedBy(xIntersectionId, EntityType.BODY);
            }));
        normalizeSliceGeometryForLasercutting(context, id + "XNormalize", allXSliceBodies, definition.matThick);

        // Process all Y slice bodies together
        const allYSliceBodies = qUnion(mapArray(trimmedSheetsResult.yIntersectionIds, function(yIntersectionId)
            {
                return qCreatedBy(yIntersectionId, EntityType.BODY);
            }));
        normalizeSliceGeometryForLasercutting(context, id + "YNormalize", allYSliceBodies, definition.matThick);

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
// Inputs:
//  - featureIdPrefix : Base id used to regenerate the X/Y identifiers for each slice
//  - xSliceResult, ySliceResult : Maps containing sliceIds and slicePlanes arrays for X- and Y-oriented slices
//  - targetBody : Body query representing the part being sliced
// Returns: map containing the intersection ids and tracking queries for cap faces
export function trimSheetsToSolid(context is Context, featureIdPrefix is Id, xSliceResult is map, ySliceResult is map, targetBody is Query)
{
    var xIntersectionIds = [] as array;
    var yIntersectionIds = [] as array;
    var xTrackedCapFaces = [] as array;
    var yTrackedCapFaces = [] as array;
    
    const xSliceIds = xSliceResult.sliceIds;
    const xSlicePlanes = xSliceResult.slicePlanes;
    const ySliceIds = ySliceResult.sliceIds;
    const ySlicePlanes = ySliceResult.slicePlanes;

    for (var xPlaneIndex = 0; xPlaneIndex < size(xSliceIds); xPlaneIndex += 1)
    {
        var xSliceId = xSliceIds[xPlaneIndex];
        var xSlicePlane = xSlicePlanes[xPlaneIndex];
        var xIntersectionId = featureIdPrefix + "XIntersection" + xPlaneIndex;
        
        // Start tracking the start and end cap faces separately before the intersection operation
        const originalSheetBody = qCreatedBy(xSliceId + "extrudeRectangle", EntityType.BODY);
        const originalSheetFaces = qOwnedByBody(originalSheetBody, EntityType.FACE);
        const originalCapFaces = qParallelPlanes(originalSheetFaces, xSlicePlane);
        
        // Track each cap separately (there should be exactly 2 cap faces)
        const startCapFace = qNthElement(originalCapFaces, 0);
        const endCapFace = qNthElement(originalCapFaces, 1);
        const trackingStartCap = startTracking(context, startCapFace);
        const trackingEndCap = startTracking(context, endCapFace);
        
        opBoolean(context, xIntersectionId, {
                    "tools" : qUnion([originalSheetBody, targetBody]),
                    "operationType" : BooleanOperationType.INTERSECTION,
                    "keepTools" : true
                });
        
        // Check if EITHER cap has been completely destroyed after intersection
        const intersectionBodies = qCreatedBy(xIntersectionId, EntityType.BODY);
        if (!isQueryEmpty(context, intersectionBodies))
        {
            const remainingStartCapFaces = evaluateQuery(context, trackingStartCap);
            const remainingEndCapFaces = evaluateQuery(context, trackingEndCap);
            
            // Delete body if EITHER the start cap OR the end cap is completely gone
            if (size(remainingStartCapFaces) == 0 || size(remainingEndCapFaces) == 0)
            {
                opDeleteBodies(context, xIntersectionId + "deleteNoCapBody", {
                    "entities" : intersectionBodies
                });
                continue;
            }
            
            // Store tracking queries for successful slices
            // Use startCap as the primary face (could use either, just pick one consistently)
            xTrackedCapFaces = append(xTrackedCapFaces, trackingStartCap);
        }

        xIntersectionIds = append(xIntersectionIds, xIntersectionId);
    }

    for (var yPlaneIndex = 0; yPlaneIndex < size(ySliceIds); yPlaneIndex += 1)
    {
        var ySliceId = ySliceIds[yPlaneIndex];
        var ySlicePlane = ySlicePlanes[yPlaneIndex];
        var yIntersectionId = featureIdPrefix + "YIntersection" + yPlaneIndex;
        
        // Start tracking the start and end cap faces separately before the intersection operation
        const originalSheetBody = qCreatedBy(ySliceId + "extrudeRectangle", EntityType.BODY);
        const originalSheetFaces = qOwnedByBody(originalSheetBody, EntityType.FACE);
        const originalCapFaces = qParallelPlanes(originalSheetFaces, ySlicePlane);
        
        // Track each cap separately (there should be exactly 2 cap faces)
        const startCapFace = qNthElement(originalCapFaces, 0);
        const endCapFace = qNthElement(originalCapFaces, 1);
        const trackingStartCap = startTracking(context, startCapFace);
        const trackingEndCap = startTracking(context, endCapFace);
        
        opBoolean(context, yIntersectionId, {
                    "tools" : qUnion([originalSheetBody, targetBody]),
                    "operationType" : BooleanOperationType.INTERSECTION,
                    "keepTools" : true
                });
        
        // Check if EITHER cap has been completely destroyed after intersection
        const intersectionBodies = qCreatedBy(yIntersectionId, EntityType.BODY);
        if (!isQueryEmpty(context, intersectionBodies))
        {
            const remainingStartCapFaces = evaluateQuery(context, trackingStartCap);
            const remainingEndCapFaces = evaluateQuery(context, trackingEndCap);
            
            // Delete body if EITHER the start cap OR the end cap is completely gone
            if (size(remainingStartCapFaces) == 0 || size(remainingEndCapFaces) == 0)
            {
                opDeleteBodies(context, yIntersectionId + "deleteNoCapBody", {
                    "entities" : intersectionBodies
                });
                continue;
            }
            
            // Store tracking queries for successful slices
            // Use startCap as the primary face (could use either, just pick one consistently)
            yTrackedCapFaces = append(yTrackedCapFaces, trackingStartCap);
        }

        yIntersectionIds = append(yIntersectionIds, yIntersectionId);
    }

    const rawXSlices = qUnion(mapArray(xSliceIds, function(xSliceId)
            {
                return qCreatedBy(xSliceId + "extrudeRectangle", EntityType.BODY);
            }));
    const rawYSlices = qUnion(mapArray(ySliceIds, function(ySliceId)
            {
                return qCreatedBy(ySliceId + "extrudeRectangle", EntityType.BODY);
            }));

    opDeleteBodies(context, featureIdPrefix + "deleteRawSlices", {
                "entities" : qUnion([rawXSlices, rawYSlices])
            });

    return { 
        "xIntersectionIds" : xIntersectionIds, 
        "yIntersectionIds" : yIntersectionIds,
        "xTrackedCapFaces" : xTrackedCapFaces,
        "yTrackedCapFaces" : yTrackedCapFaces
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
    for (var xPlaneIndex = 0; xPlaneIndex < size(xIntersectionIds); xPlaneIndex += 1)
    {
        const xCopyId = featureIdPrefix + "XCopy" + xPlaneIndex;
        opPattern(context, xCopyId, {
                    "entities" : qCreatedBy(xIntersectionIds[xPlaneIndex], EntityType.BODY),
                    "transforms" : [identityTransform()],
                    "instanceNames" : ["1"]
                });
        xSlotIds = append(xSlotIds, xCopyId);
    }

    for (var yPlaneIndex = 0; yPlaneIndex < size(yIntersectionIds); yPlaneIndex += 1)
    {
        const yCopyId = featureIdPrefix + "YCopy" + yPlaneIndex;
        opPattern(context, yCopyId, {
                    "entities" : qCreatedBy(yIntersectionIds[yPlaneIndex], EntityType.BODY),
                    "transforms" : [identityTransform()],
                    "instanceNames" : ["1"]
                });
        ySlotIds = append(ySlotIds, yCopyId);
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

    // Helper to resolve original IDs to Body queries
    const originalXSlices = qUnion(mapArray(xIntersectionIds, function(id)
            {
                return qCreatedBy(id, EntityType.BODY);
            }));

    const originalYSlices = qUnion(mapArray(yIntersectionIds, function(id)
            {
                return qCreatedBy(id, EntityType.BODY);
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

    opDeleteBodies(context, sliceId + "deleteSketch", {
                "entities" : qCreatedBy(sliceId + "sketch")
            });
}

// Normalize slice geometry for laser cutting by projecting non-planar faces onto the largest planar face and subtracting.
// This function processes each body independently, finding the largest flat planar face on the body to use as the
// projection target, then projects any slanted/chamfered faces onto it and subtracts the thickened projection.
// This ensures all output geometry can be laser cut flat without overhangs.
// Inputs:
//  - idPrefix : Id prefix used for created helper operations
//  - sliceBodies : Query for bodies to normalize
//  - materialThickness : Thickness used when removing projected material
// Returns: none
export function normalizeSliceGeometryForLasercutting(context is Context, idPrefix is Id, sliceBodies is Query, materialThickness is ValueWithUnits)
{
    // Process each body independently
    var bodyArray = evaluateQuery(context, sliceBodies);
    var bodyCounter = 0;
    
    for (var body in bodyArray)
    {
        const bodyId = idPrefix + "Body" + bodyCounter;
        
        // Find all faces on this body
        const bodyFaces = qOwnedByBody(body, EntityType.FACE);
        
        // Find the largest planar face to use as projection target
        var largestPlanarFace = undefined;
        var largestArea = 0 * meter^2;
        
        var faceArray = evaluateQuery(context, bodyFaces);
        for (var face in faceArray)
        {
            try
            {
                // Check if face is planar
                var surfaceDefinition = evSurfaceDefinition(context, {
                    "face" : face
                });
                
                if (surfaceDefinition is Plane)
                {
                    var faceArea = evArea(context, {
                        "entities" : face
                    });
                    
                    if (faceArea > largestArea)
                    {
                        largestArea = faceArea;
                        largestPlanarFace = face;
                    }
                }
            }
            catch
            {
                // Skip faces that can't be evaluated
            }
        }
        
        // If no planar face found, skip this body
        if (largestPlanarFace == undefined)
        {
            bodyCounter += 1;
            continue;
        }
        
        // Get the plane definition from the largest planar face
        var targetPlane = evPlane(context, {
            "face" : largestPlanarFace
        });
        
        // Identify "Good" faces (don't need normalization) using robust query-based approach
        // - Top/Bottom caps (Parallel to the target plane itself)
        const topBottomFaces = qParallelPlanes(bodyFaces, targetPlane);
        // - Vertical cut walls (Parallel to the target plane's NORMAL vector)
        const verticalWallFaces = qFacesParallelToDirection(bodyFaces, targetPlane.normal);
        
        // Combine them into a "skip list"
        const validFaces = qUnion([topBottomFaces, verticalWallFaces]);
        
        // Subtract valid faces from all faces to find the "Bad" (slanted/chamfered) ones
        const nonNormalFaces = qSubtraction(bodyFaces, validFaces);
        
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
        catch (error)
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
        catch (error)
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
        catch (error)
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
// Convert normalized slice bodies to sheet metal by thickening the primary tracked cap faces.
// Uses the tracked face queries established in trimSheetsToSolid to ensure only valid slices
// (not wedgelets) are converted. Follows exact pattern from sheetMetalStart's thickenToSheetMetal.
// Inputs:
//  - context : Execution context
//  - id : Feature ID for sheet metal operations
//  - trimmedSheetsResult : Map containing tracked cap faces for each valid slice
//  - definition : Feature definition containing sheet metal parameters
export function convertSlicesToSheetMetal(context is Context, id is Id, trimmedSheetsResult is map, definition is map)
{
    // Wrapper function that calls the standard library sheetMetalStart as subfeatures
    // This allows us to leverage the full functionality of defineSheetMetalFeature
    // including automatic surface hiding and proper context naming
    
    // Step 1: Collect the primary tracked cap face from each valid X slice
    // These tracking queries were established in trimSheetsToSolid and automatically filter out wedgelets
    const xTrackedCapFaces = trimmedSheetsResult.xTrackedCapFaces;
    const yTrackedCapFaces = trimmedSheetsResult.yTrackedCapFaces;
    
    var faceQueries = [] as array;
    
    // Add all X tracked cap faces
    for (var xTrackedCap in xTrackedCapFaces)
    {
        const trackedFaces = evaluateQuery(context, xTrackedCap);
        // Each tracking query should resolve to exactly one face (the primary cap)
        if (size(trackedFaces) > 0)
        {
            faceQueries = append(faceQueries, xTrackedCap);
        }
    }
    
    // Add all Y tracked cap faces
    for (var yTrackedCap in yTrackedCapFaces)
    {
        const trackedFaces = evaluateQuery(context, yTrackedCap);
        // Each tracking query should resolve to exactly one face (the primary cap)
        if (size(trackedFaces) > 0)
        {
            faceQueries = append(faceQueries, yTrackedCap);
        }
    }
    
    if (size(faceQueries) == 0)
    {
        return;
    }
    
    // Step 2: Collect all faces into a single query
    var allFacesToConvert = qUnion(faceQueries);
    
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
            "oppositeDirection" : false,
            "kFactor" : definition.kFactor
        });
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
    const xIntersectionIds = trimmedSheetsResult.xIntersectionIds;
    const yIntersectionIds = trimmedSheetsResult.yIntersectionIds;
    
    const allXSliceBodies = qUnion(mapArray(xIntersectionIds, function(xIntersectionId)
        {
            return qCreatedBy(xIntersectionId, EntityType.BODY);
        }));
    const allYSliceBodies = qUnion(mapArray(yIntersectionIds, function(yIntersectionId)
        {
            return qCreatedBy(yIntersectionId, EntityType.BODY);
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

