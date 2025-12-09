// Laser It slices a selected body into a grid of extruded rectangles to prepare geometry for laser cutting.
// Inputs:
//  - selectedBody : Body query to slice
//  - generationMode : Selection between Waffle Mode (grid-based) and Rib Mode (sketch-based)
//  - planeSpacing : Distance between slicing planes along the X and Y axes of the reference frame (Waffle Mode only)
//  - matThick : Material thickness that controls extrusion depth
//  - defRefFrame : Boolean to select a mate connector as the slicing reference frame (Waffle Mode only)
//  - referenceFrame : Mate connector query when defRefFrame is true, defines the placement of the slicing grid (Waffle Mode only)
//  - sketchLines : Sketch edges to extrude as slices (Rib Mode only)
FeatureScript 2815;
import(path : "onshape/std/geometry.fs", version : "2815.0");
import(path : "onshape/std/query.fs", version : "2815.0");
import(path : "onshape/std/box.fs", version : "2815.0");

const PARALLEL_THRESHOLD_COS = cos(5 * degree); // Approximately 0.996

export enum LaserItGenerationMode
{
    annotation { "Name" : "Waffle Mode" }
    WAFFLE,
    annotation { "Name" : "Rib Mode" }
    RIB
}

annotation { "Feature Type Name" : "Laser It" }
export const laserIt = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        definition.selectedBody is Query;

        annotation { "Name" : "Generation Mode" }
        definition.generationMode is LaserItGenerationMode;

        annotation { "Name" : "Material Thickness" }
        isLength(definition.matThick, LENGTH_BOUNDS);

        if (definition.generationMode == LaserItGenerationMode.WAFFLE)
        {
            annotation { "Name" : "Plane Spacing" }
            isLength(definition.planeSpacing, LENGTH_BOUNDS);

            annotation { "Name" : "Define Reference Frame" }
            definition.defRefFrame is boolean;

            if (definition.defRefFrame)
            {
                annotation { "Name" : "Reference Frame", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
                definition.referenceFrame is Query;
            }
        }

        if (definition.generationMode == LaserItGenerationMode.RIB)
        {
            annotation { "Name" : "Sketch Lines", "Filter" : EntityType.EDGE && SketchObject.YES && ConstructionObject.NO && GeometryType.LINE }
            definition.sketchLines is Query;
        }

    }
    {
        if (definition.generationMode == LaserItGenerationMode.WAFFLE)
        {
            // Waffle Mode: Grid-based slicing with X and Y planes
            processWaffleMode(context, id, definition);
        }
        else if (definition.generationMode == LaserItGenerationMode.RIB)
        {
            // Rib Mode: Sketch-based slicing with arbitrary line orientations
            processRibMode(context, id, definition);
        }
    });

// Process Waffle Mode: Grid-based slicing with X and Y planes
// Inputs:
//  - context : Context for geometry operations
//  - id : Feature ID
//  - definition : Feature definition map containing all user inputs
// Returns: none
function processWaffleMode(context is Context, id is Id, definition is map)
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
}

// Process Rib Mode: Sketch-based slicing with arbitrary line orientations
// Inputs:
//  - context : Context for geometry operations
//  - id : Feature ID
//  - definition : Feature definition map containing all user inputs
// Returns: none
function processRibMode(context is Context, id is Id, definition is map)
{
    // Verify sketch lines were selected
    verifyNonemptyQuery(context, definition, "sketchLines", "Select sketch lines to extrude as slices.");

    // Get the sketch plane that all lines should lie in
    const sketchPlane = evOwnerSketchPlane(context, {
                "entity" : definition.sketchLines
            });

    // Verify all selected edges are lines in the same plane
    const selectedEdges = evaluateQuery(context, definition.sketchLines);
    for (var edge in selectedEdges)
    {
        try
        {
            // Validate that the edge is a straight line
            evLine(context, {
                    "edge" : edge
                });
        }
        catch
        {
            throw regenError("All sketch edges must be straight lines.", ["sketchLines"]);
        }
    }

    // Generate sheets from sketch lines
    var sliceResults = generateSheetsFromSketch(context, id, definition.sketchLines, sketchPlane, definition.matThick);

    // Trim sheets to the target solid
    var trimmedSliceIds = trimSheetsToSolidGeneric(context, id, sliceResults, definition.selectedBody);

    // Generate cross-slot geometry for arbitrary slice orientations
    generateCrossSlotGeometryGeneric(context, id, trimmedSliceIds, sliceResults.slicePlanes);

    // Normalize all slices for laser cutting
    const allSliceBodies = qUnion(mapArray(trimmedSliceIds, function(sliceId)
        {
            return qCreatedBy(sliceId, EntityType.BODY);
        }));
    normalizeSliceGeometryForLasercutting(context, id + "Normalize", allSliceBodies, definition.matThick);
}

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
// Returns: map containing the intersection ids
export function trimSheetsToSolid(context is Context, featureIdPrefix is Id, xSliceResult is map, ySliceResult is map, targetBody is Query)
{
    var xIntersectionIds = [] as array;
    var yIntersectionIds = [] as array;
    
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

    return { "xIntersectionIds" : xIntersectionIds, "yIntersectionIds" : yIntersectionIds };
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
        
        println("=== opCreateOutline Entry Diagnostics ===");
        println("Body counter: " ~ bodyCounter);
        println("Target plane origin: " ~ targetPlane.origin);
        println("Target plane normal: " ~ targetPlane.normal);
        println("Projection target query empty: " ~ isQueryEmpty(context, projectionTarget));
        println("Projection target count: " ~ size(evaluateQuery(context, projectionTarget)));
        
        // Extract surfaces from faces to normalize
        const extractedOutlineToolsId = bodyId + "extract";
        opExtractSurface(context, extractedOutlineToolsId, {
            "faces" : nonNormalFaces,
            "offset" : 0 * meter
        });
        
        const extractedOutlineTools = qCreatedBy(extractedOutlineToolsId, EntityType.BODY);
        
        println("Extracted outline tools query empty: " ~ isQueryEmpty(context, extractedOutlineTools));
        println("Extracted outline tools count: " ~ size(evaluateQuery(context, extractedOutlineTools)));
        
        // Project onto the construction plane
        const outlineId = bodyId + "outline";
        println("About to call opCreateOutline with ID: " ~ outlineId);
        
        try
        {
            opCreateOutline(context, outlineId, {
                "tools" : extractedOutlineTools,
                "target" : projectionTarget
            });
            println("opCreateOutline succeeded");
        }
        catch (error)
        {
            println("opCreateOutline FAILED with error: " ~ error);
            println("=== opCreateOutline Exit Diagnostics (FAILED) ===");
            // Clean up and continue to next body
            opDeleteBodies(context, bodyId + "cleanupFailed", {
                "entities" : qUnion([projectionTarget, extractedOutlineTools])
            });
            bodyCounter += 1;
            continue;
        }
        
        println("=== opCreateOutline Exit Diagnostics (SUCCESS) ===");
        
        const projectionFaces = qCreatedBy(outlineId, EntityType.FACE);
        
        println("Projection faces query empty: " ~ isQueryEmpty(context, projectionFaces));
        println("Projection faces count: " ~ size(evaluateQuery(context, projectionFaces)));
        
        // Check if any projection was created
        if (isQueryEmpty(context, projectionFaces))
        {
            println("No projection faces created, skipping normalization for this body");
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
        println("About to thicken projection faces in thickness2 direction");
        
        try
        {
            opThicken(context, thickenId, {
                "entities" : projectionFaces,
                "thickness1" : 0 * meter,
                "thickness2" : materialThickness,
                "keepTools" : true
            });
            println("Thicken operation succeeded");
        }
        catch (error)
        {
            println("Thicken operation FAILED with error: " ~ error);
            println("Skipping normalization for this body and continuing");
            // Clean up helper geometry and continue to next body
            opDeleteBodies(context, bodyId + "cleanup1", {
                "entities" : qUnion([projectionTarget, extractedOutlineTools, qCreatedBy(outlineId, EntityType.BODY)])
            });
            bodyCounter += 1;
            continue;
        }
        
        const thickenedBodies = qCreatedBy(thickenId, EntityType.BODY);
        println("Thickened bodies count: " ~ size(evaluateQuery(context, thickenedBodies)));
        
        // Subtract the thickened projection from the current body being normalized
        // Target only the specific body being processed, not all slice bodies
        println("About to subtract thickened bodies from current body");
        
        try
        {
            opBoolean(context, bodyId + "subtract", {
                "tools" : thickenedBodies,
                "targets" : body,
                "operationType" : BooleanOperationType.SUBTRACTION,
                "keepTools" : false
            });
            println("Boolean subtraction succeeded");
            println("=== Normalization complete for this body ===");
        }
        catch (error)
        {
            println("Boolean subtraction FAILED with error: " ~ error);
            println("Skipping normalization for this body and continuing");
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

// Generate sheets from sketch lines for Rib Mode
// Inputs:
//  - context : Context for geometry operations
//  - featureIdPrefix : Base id used when naming all geometry created
//  - sketchLines : Query for sketch line edges to extrude as slices
//  - sketchPlane : Plane that all sketch lines lie in
//  - materialThickness : Extrusion depth for the raw sheet
// Returns: map containing slicePlanes array and sliceIds array
function generateSheetsFromSketch(context is Context, featureIdPrefix is Id, sketchLines is Query, sketchPlane is Plane, materialThickness is ValueWithUnits)
{
    var slicePlanes = [] as array;
    var sliceIds = [] as array;

    // Process each sketch line
    var lineEdges = evaluateQuery(context, sketchLines);
    var lineCounter = 0;
    
    for (var lineEdge in lineEdges)
    {
        // Get the line geometry
        var lineGeometry = evLine(context, {
                "edge" : lineEdge
            });

        // Calculate the plane perpendicular to the sketch plane, containing the line
        // The line direction becomes the "up" vector of the slice plane
        // The cross product of sketch normal and line direction becomes the slice normal
        var lineDirection = lineGeometry.direction;
        var sliceNormal = cross(sketchPlane.normal, lineDirection);
        
        // Check that the cross product is non-zero (line is not parallel to sketch normal)
        if (squaredNorm(sliceNormal) < TOLERANCE.zeroLength * TOLERANCE.zeroLength)
        {
            throw regenError("Sketch lines cannot be perpendicular to the sketch plane.", ["sketchLines"]);
        }
        
        // Normalize the slice normal
        sliceNormal = normalize(sliceNormal);

        // Get a point on the line to use as origin
        var lineMidpoint = lineGeometry.origin;

        // Create the slice plane perpendicular to the sketch plane, through the line
        var slicePlane = plane(lineMidpoint, sliceNormal, lineDirection);
        
        var sliceId = featureIdPrefix + "Rib" + lineCounter;
        
        // Generate the slice sheet by sweeping a profile along the line
        generateSliceSheetFromLine(context, sliceId, lineEdge, sketchPlane, materialThickness);
        
        slicePlanes = append(slicePlanes, slicePlane);
        sliceIds = append(sliceIds, sliceId);
        lineCounter += 1;
    }

    return { "slicePlanes" : slicePlanes, "sliceIds" : sliceIds };
}

// Create a slice sheet by sweeping a profile along a sketch line
// This creates a slice only where the line is, not a large rectangle
// Inputs:
//  - context : Context for geometry operations
//  - sliceId : Unique Id prefix used for sketch and sweep operations
//  - lineEdge : The sketch line edge to sweep along
//  - sketchPlane : The plane containing the sketch line
//  - materialThickness : Thickness of the slice material
// Returns: none
function generateSliceSheetFromLine(context is Context, sliceId is Id, lineEdge is Query, sketchPlane is Plane, materialThickness is ValueWithUnits)
{
    // Get a point on the line to establish the sweep profile plane
    const edgeVector = evEdgeTangentLine(context, {
                "edge" : lineEdge,
                "parameter" : 0
            });

    // Create a plane perpendicular to the sketch plane, through the line
    // The profile will be a rectangle perpendicular to the sketch plane
    const profilePlane = plane(edgeVector.origin, sketchPlane.normal, edgeVector.direction);

    // Create a sketch on this plane for the sweep profile
    const profileSketch = newSketchOnPlane(context, sliceId + "sketch", {
                "sketchPlane" : profilePlane
            });

    // Draw a rectangle representing the material thickness
    // We need a 2D region to sweep into a 3D solid body
    // The rectangle is centered on the sketch plane, extending perpendicular to it
    // Make it very small in X direction (along the path) and materialThickness in Y (perpendicular to sketch)
    const tinyWidth = 0.001 * millimeter; // Small dimension along sweep path
    skRectangle(profileSketch, "rectangle", {
                "firstCorner" : vector(-tinyWidth / 2, -materialThickness / 2),
                "secondCorner" : vector(tinyWidth / 2, materialThickness / 2)
            });

    skSolve(profileSketch);

    // Sweep the profile region along the line edge to create a solid body
    opSweep(context, sliceId + "sweep", {
                "profiles" : qSketchRegion(sliceId + "sketch", false),
                "path" : lineEdge
            });
    
    // Delete the sketch
    opDeleteBodies(context, sliceId + "deleteSketch", {
                "entities" : qCreatedBy(sliceId + "sketch")
            });
}

// Trim sheets to solid for generic/arbitrary orientations (Rib Mode)
// Inputs:
//  - context : Context for geometry operations
//  - featureIdPrefix : Base id used for intersection operations
//  - sliceResults : Map containing slicePlanes and sliceIds arrays
//  - targetBody : Body query representing the part being sliced
// Returns: array of intersection IDs for successfully trimmed slices
function trimSheetsToSolidGeneric(context is Context, featureIdPrefix is Id, sliceResults is map, targetBody is Query)
{
    var intersectionIds = [] as array;
    
    const sliceIds = sliceResults.sliceIds;
    const slicePlanes = sliceResults.slicePlanes;

    for (var sliceIndex = 0; sliceIndex < size(sliceIds); sliceIndex += 1)
    {
        var sliceId = sliceIds[sliceIndex];
        var slicePlane = slicePlanes[sliceIndex];
        var intersectionId = featureIdPrefix + "Intersection" + sliceIndex;
        
        // Start tracking the start and end cap faces separately before the intersection operation
        const originalSheetBody = qCreatedBy(sliceId + "sweep", EntityType.BODY);
        const originalSheetFaces = qOwnedByBody(originalSheetBody, EntityType.FACE);
        const originalCapFaces = qParallelPlanes(originalSheetFaces, slicePlane);
        
        // Track each cap separately (there should be exactly 2 cap faces)
        const startCapFace = qNthElement(originalCapFaces, 0);
        const endCapFace = qNthElement(originalCapFaces, 1);
        const trackingStartCap = startTracking(context, startCapFace);
        const trackingEndCap = startTracking(context, endCapFace);
        
        opBoolean(context, intersectionId, {
                    "tools" : qUnion([originalSheetBody, targetBody]),
                    "operationType" : BooleanOperationType.INTERSECTION,
                    "keepTools" : true
                });
        
        // Check if EITHER cap has been completely destroyed after intersection
        const intersectionBodies = qCreatedBy(intersectionId, EntityType.BODY);
        if (!isQueryEmpty(context, intersectionBodies))
        {
            const remainingStartCapFaces = evaluateQuery(context, trackingStartCap);
            const remainingEndCapFaces = evaluateQuery(context, trackingEndCap);
            
            // Delete body if EITHER the start cap OR the end cap is completely gone
            if (size(remainingStartCapFaces) == 0 || size(remainingEndCapFaces) == 0)
            {
                opDeleteBodies(context, intersectionId + "deleteNoCapBody", {
                    "entities" : intersectionBodies
                });
                continue;
            }
        }

        intersectionIds = append(intersectionIds, intersectionId);
    }

    // Delete all raw slices
    const rawSlices = qUnion(mapArray(sliceIds, function(sliceId)
            {
                return qCreatedBy(sliceId + "sweep", EntityType.BODY);
            }));

    opDeleteBodies(context, featureIdPrefix + "deleteRawSlices", {
                "entities" : rawSlices
            });

    return intersectionIds;
}

// Generate cross-slot geometry for arbitrary slice orientations (Rib Mode)
// This handles slices that don't follow a simple X/Y grid pattern
// For each pair of non-parallel slices that actually intersect, create notches
// Inputs:
//  - context : Context for geometry operations
//  - featureIdPrefix : Base id used for boolean operations
//  - sliceIntersectionIds : Array of IDs for trimmed slice bodies
//  - slicePlanes : Array of planes corresponding to each slice
// Returns: none (modifies slices in place)
function generateCrossSlotGeometryGeneric(context is Context, featureIdPrefix is Id, sliceIntersectionIds is array, slicePlanes is array)
{
    // Build a map of which slices actually touch using collision detection
    // This avoids O(n²) boolean operations on all pairs
    var sliceCount = size(sliceIntersectionIds);
    
    // Create array of slice body queries for collision detection
    var sliceBodies = [] as array;
    for (var sliceIndex = 0; sliceIndex < sliceCount; sliceIndex += 1)
    {
        sliceBodies = append(sliceBodies, qCreatedBy(sliceIntersectionIds[sliceIndex], EntityType.BODY));
    }
    
    // Use evCollision to efficiently find which slices actually interfere
    const allSlices = qUnion(sliceBodies);
    const collisions = evCollision(context, {
                "tools" : allSlices,
                "targets" : allSlices
            });
    
    // Build a map of slice pairs that interfere
    var touchingPairs = [] as array;
    for (var collision in collisions)
    {
        // Skip self-collisions
        if (collision.toolBody == collision.targetBody)
            continue;
        
        const clashType = collision['type'];
        // Only process slices that interfere (overlap), not just touch
        if (clashType == ClashType.INTERFERE ||
            clashType == ClashType.TARGET_IN_TOOL ||
            clashType == ClashType.TOOL_IN_TARGET)
        {
            // Find the indices of the interfering slices
            var indexI = -1;
            var indexJ = -1;
            for (var idx = 0; idx < sliceCount; idx += 1)
            {
                const bodyQuery = sliceBodies[idx];
                const bodyArray = evaluateQuery(context, bodyQuery);
                if (size(bodyArray) > 0 && bodyArray[0] == collision.toolBody)
                {
                    indexI = idx;
                }
                if (size(bodyArray) > 0 && bodyArray[0] == collision.targetBody)
                {
                    indexJ = idx;
                }
            }
            
            // Only add each pair once (i < j)
            if (indexI >= 0 && indexJ >= 0 && indexI < indexJ)
            {
                touchingPairs = append(touchingPairs, { "i" : indexI, "j" : indexJ });
            }
        }
    }
    
    // Process only the pairs that actually interfere
    for (var pair in touchingPairs)
    {
        const sliceIndexI = pair.i;
        const sliceIndexJ = pair.j;
        
        // Check if the planes are significantly different (not parallel)
        const planeI = slicePlanes[sliceIndexI];
        const planeJ = slicePlanes[sliceIndexJ];
        const normalAlignment = abs(dot(planeI.normal, planeJ.normal));
        
        // Skip if planes are too parallel (within 5 degrees)
        if (normalAlignment >= PARALLEL_THRESHOLD_COS)
        {
            continue;
        }

        // Get the actual slice bodies
        const sliceBodyI = sliceBodies[sliceIndexI];
        const sliceBodyJ = sliceBodies[sliceIndexJ];
        
        // Create copies to find intersection without modifying originals yet
        const copyIdI = featureIdPrefix + "TestCopy" + sliceIndexI + "_" + sliceIndexJ + "I";
        const copyIdJ = featureIdPrefix + "TestCopy" + sliceIndexI + "_" + sliceIndexJ + "J";
        
        opPattern(context, copyIdI, {
                    "entities" : sliceBodyI,
                    "transforms" : [identityTransform()],
                    "instanceNames" : ["1"]
                });
        opPattern(context, copyIdJ, {
                    "entities" : sliceBodyJ,
                    "transforms" : [identityTransform()],
                    "instanceNames" : ["1"]
                });
        
        const copyBodyI = qCreatedBy(copyIdI, EntityType.BODY);
        const copyBodyJ = qCreatedBy(copyIdJ, EntityType.BODY);
        
        // Find the intersection between the two slices
        const intersectionId = featureIdPrefix + "Intersect" + sliceIndexI + "_" + sliceIndexJ;
        
        try silent
        {
            opBoolean(context, intersectionId, {
                        "tools" : copyBodyI,
                        "targets" : copyBodyJ,
                        "operationType" : BooleanOperationType.INTERSECTION,
                        "keepTools" : false,
                        "keepTargets" : false
                    });
            
            const intersectionBodies = qCreatedBy(intersectionId, EntityType.BODY);
            
            // If intersection exists, split it and subtract from each slice
            if (!isQueryEmpty(context, intersectionBodies))
            {
                // Find a splitting plane - use the average of the two normals as the split direction
                // This bisects the angle between the two slice planes
                var splitDirection = planeI.normal + planeJ.normal;
                
                // Safety check: if normals are nearly opposite, sum could be close to zero
                if (squaredNorm(splitDirection) < 0.01) // Less than ~6 degree angle between them
                {
                    // Use cross product instead to get a perpendicular direction
                    splitDirection = cross(planeI.normal, planeJ.normal);
                }
                
                splitDirection = normalize(splitDirection);
                
                // Find the centroid of the intersection
                var intersectionCentroid = evApproximateCentroid(context, {
                        "entities" : intersectionBodies
                    });
                
                // Create a split plane through the centroid
                var splitPlane = plane(intersectionCentroid, splitDirection);
                
                const splitPlaneId = intersectionId + "SplitPlane";
                const splitId = intersectionId + "Split";
                
                try silent
                {
                    opPlane(context, splitPlaneId, {
                                "plane" : splitPlane
                            });
                    
                    opSplitPart(context, splitId, {
                                "targets" : intersectionBodies,
                                "tool" : qCreatedBy(splitPlaneId, EntityType.BODY)
                            });
                    
                    // Assign one half to slice I and the other to slice J
                    const splitHalfI = qFarthestAlong(qCreatedBy(splitId), splitDirection);
                    const splitHalfJ = qFarthestAlong(qCreatedBy(splitId), -splitDirection);
                    
                    // Subtract the halves from the original slices
                    try silent
                    {
                        opBoolean(context, intersectionId + "SubtractI", {
                                    "tools" : splitHalfI,
                                    "targets" : sliceBodyI,
                                    "operationType" : BooleanOperationType.SUBTRACTION,
                                    "keepTools" : false
                                });
                    }
                    
                    try silent
                    {
                        opBoolean(context, intersectionId + "SubtractJ", {
                                    "tools" : splitHalfJ,
                                    "targets" : sliceBodyJ,
                                    "operationType" : BooleanOperationType.SUBTRACTION,
                                    "keepTools" : false
                                });
                    }
                    
                    // Clean up split plane
                    opDeleteBodies(context, splitPlaneId + "delete", {
                                "entities" : qCreatedBy(splitPlaneId, EntityType.BODY)
                            });
                }
            }
        }
    }
}
