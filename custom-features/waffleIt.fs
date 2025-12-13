// Waffle It slices a selected body into a grid of extruded rectangles to prepare geometry for laser cutting.
// Inputs:
//  - selectedBody : Body query to slice
//  - planeSpacing : Distance between slicing planes along the X and Y axes of the reference frame
//  - matThick : Material thickness that controls extrusion depth
//  - defRefFrame : Boolean to select a mate connector as the slicing reference frame
//  - referenceFrame : Mate connector query when defRefFrame is true, defines the placement of the slicing grid
//  - enableUAxis : Boolean to enable U-axis slicing with adjustable skew angle
//  - uAxisSkewAngle : Angle of U-axis relative to Y-axis (only when enableUAxis is true)
//  - enableVAxis : Boolean to enable V-axis for three-directional slicing (requires U-axis)
//  - outputSheetMetal : Boolean to output results as sheet metal bodies
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

// Bounds for angle parameters
const SKEW_ANGLE_LIMIT = 89;
export const SKEW_ANGLE_BOUNDS = { (degree) : [-SKEW_ANGLE_LIMIT, 0, SKEW_ANGLE_LIMIT] } as AngleBoundSpec;

// Three-axis hexagonal pattern constants
// In three-axis mode, the U and V axes are at fixed angles to create a hexagonal pattern:
// - X-axis: 0 degrees (reference direction)
// - U-axis: 120 degrees (30 degrees skew from Y-axis, which is at 90 degrees)
// - V-axis: 60 degrees (creates 60 degrees angles between all three axes)
const THREE_AXIS_U_SKEW_ANGLE = 30 * degree;  // Skew angle of U-axis relative to Y-axis
const THREE_AXIS_V_ANGLE = 60 * degree;       // Absolute angle of V-axis in XY plane

// Callback function for feature changes to manage axis dependencies
export function waffleItOnFeatureChange(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean) returns map
{
    // If U-axis is disabled, also disable V-axis
    // Handle undefined case: if enableUAxis is not defined or is false
    if (definition.enableUAxis == undefined || definition.enableUAxis == false)
    {
        definition.enableVAxis = false;
    }
    
    // When enabling V-axis for the first time, enforce three-axis mode constraints
    const wasVAxisDisabled = (oldDefinition.enableVAxis == undefined || oldDefinition.enableVAxis == false);
    if (wasVAxisDisabled && definition.enableVAxis == true)
    {
        // In three-axis mode, U-axis skew angle is fixed for hexagonal pattern
        definition.uAxisSkewAngle = THREE_AXIS_U_SKEW_ANGLE;
        // Section spacing must be at least 3x section width to avoid triple intersections
        if (definition.planeSpacing < 3 * definition.matThick)
        {
            definition.planeSpacing = 3 * definition.matThick;
        }
    }
    
    // Lock U-axis angle when V-axis is enabled to maintain hexagonal pattern
    if (definition.enableVAxis == true)
    {
        definition.uAxisSkewAngle = THREE_AXIS_U_SKEW_ANGLE;
    }
    
    return definition;
}

annotation { "Feature Type Name" : "Waffle It", "Editing Logic Function" : "waffleItOnFeatureChange" }
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

        annotation { "Name" : "Enable U-Axis Slicing" }
        definition.enableUAxis is boolean;

        if (definition.enableUAxis)
        {
            annotation { "Name" : "U-Axis Skew Angle", "Description" : "Angle between Y-axis and U-axis. Fixed to 30 degrees in three-axis mode." }
            isAngle(definition.uAxisSkewAngle, SKEW_ANGLE_BOUNDS);

            annotation { "Name" : "Enable V-Axis (Three-Axis Mode)", "Description" : "Creates a third slicing direction at -30 degrees for hexagonal waffle pattern" }
            definition.enableVAxis is boolean;
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

        // Build a stack of slicing planes perpendicular to X, then Y (or U/V for alternate modes)
        // The function calculates which planes are needed based on the bounding box and spacing.
        // Each loop: create a sketch-sized rectangle around the body, extrude it to the material thickness, and retain the raw
        // sheets for a later trimming pass against the selected part.
        var xSliceResult = generateSheets(context, id, "X", orientedBoundingBox, definition.planeSpacing, referenceFrameToWorldTransform, definition.matThick);

        var secondAxisResult = {};
        var thirdAxisResult = {};
        
        if (definition.enableUAxis)
        {
            // U-axis is at an angle relative to Y-axis
            // Convert skew angle (relative to Y-axis at 90 degrees) to absolute angle in XY plane
            // Example: 30 degrees skew from Y-axis (90 degrees) = 120 degrees absolute angle in XY plane
            const uAxisAngle = definition.uAxisSkewAngle + 90 * degree;
            secondAxisResult = generateSheetsAtAngle(context, id, "U", uAxisAngle, orientedBoundingBox, definition.planeSpacing, referenceFrame, referenceFrameToWorldTransform, definition.matThick, definition.selectedBody);
            
            if (definition.enableVAxis)
            {
                // V-axis angle creates hexagonal pattern with 60 degrees between all axes
                thirdAxisResult = generateSheetsAtAngle(context, id, "V", THREE_AXIS_V_ANGLE, orientedBoundingBox, definition.planeSpacing, referenceFrame, referenceFrameToWorldTransform, definition.matThick, definition.selectedBody);
            }
        }
        else
        {
            // Standard Y-axis (orthogonal to X)
            secondAxisResult = generateSheets(context, id, "Y", orientedBoundingBox, definition.planeSpacing, referenceFrameToWorldTransform, definition.matThick);
        }

        // Intersect each sheet with the target solid to retain only in-bounds material before generating cross-slot geometry.
        var trimmedSheetsResult = {};
        if (definition.enableVAxis)
        {
            trimmedSheetsResult = trimSheetsToSolidThreeAxis(context, id, xSliceResult, secondAxisResult, thirdAxisResult, definition.selectedBody);
        }
        else
        {
            trimmedSheetsResult = trimSheetsToSolid(context, id, xSliceResult, secondAxisResult, definition.selectedBody);
        }

        // Generate cross-slot geometry for all axis pairs
        if (definition.enableVAxis)
        {
            // Three-axis mode: cut slots between X-U, X-V, and U-V pairs
            generateCrossSlotGeometryForSlicesThreeAxis(context, id, trimmedSheetsResult, referenceFrame, definition.uAxisSkewAngle);
        }
        else if (definition.enableUAxis)
        {
            // Two-axis mode with non-orthogonal angle
            generateCrossSlotGeometryForSlicesNonOrthogonal(context, id, trimmedSheetsResult.xIntersectionIds, trimmedSheetsResult.yIntersectionIds, referenceFrame, 0 * degree, definition.uAxisSkewAngle + 90 * degree);
        }
        else
        {
            // Standard orthogonal two-axis mode
            generateCrossSlotGeometryForSlices(context, id, trimmedSheetsResult.xIntersectionIds, trimmedSheetsResult.yIntersectionIds, referenceFrame);
        }

        // After trimming the intersecting grid, find all non-cap faces on each slice and project their geometry to
        // the START cap face. Thicken the flattened projections and remove the results from the slice.
        // This subtractive operation guarantees the slices lie inside of the original target volume.
        if (definition.normalizeGeometry == true)
        {
            // Process all X slice bodies together using attribute queries to find cap faces
            const allXSliceBodies = qUnion(mapArray(trimmedSheetsResult.xIntersectionIds, function(xSliceId)
                    {
                        return qCreatedBy(xSliceId + "extrudeRectangle", EntityType.BODY);
                    }));
            normalizeSliceGeometryForLasercutting(context, id + "XNormalize", allXSliceBodies, definition.matThick);

            // Process all second axis slice bodies (Y, U, or U in three-axis mode)
            const allSecondAxisBodies = qUnion(mapArray(trimmedSheetsResult.yIntersectionIds, function(ySliceId)
                    {
                        return qCreatedBy(ySliceId + "extrudeRectangle", EntityType.BODY);
                    }));
            normalizeSliceGeometryForLasercutting(context, id + "YNormalize", allSecondAxisBodies, definition.matThick);
            
            // Process V-axis bodies if three-axis mode is enabled
            if (definition.enableVAxis)
            {
                const allVSliceBodies = qUnion(mapArray(trimmedSheetsResult.vIntersectionIds, function(vSliceId)
                        {
                            return qCreatedBy(vSliceId + "extrudeRectangle", EntityType.BODY);
                        }));
                normalizeSliceGeometryForLasercutting(context, id + "VNormalize", allVSliceBodies, definition.matThick);
            }
        }

        // Convert to sheet metal if requested
        if (definition.outputSheetMetal == true)
        {
            convertSlicesToSheetMetal(context, id, trimmedSheetsResult, definition);
        }

    }, {});

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

// Create rectangular sheets along an axis at an arbitrary angle in the XY plane of the reference frame.
// This generalized version supports non-orthogonal slicing directions for U-axis and V-axis.
// Inputs:
//  - featureIdPrefix : Base id used when naming all geometry created in this helper
//  - axisLabel : Label for this axis (e.g., "U", "V") 
//  - axisAngle : Angle in the XY plane (0 degrees = +X direction, 90 degrees = +Y direction)
//  - orientedBoundingBox : Tight bounding box for the selected body in the reference frame
//  - planeSpacing : Distance between slices
//  - referenceFrame : Reference coordinate system
//  - referenceFrameToWorldTransform : Transform aligning the local slice planes with world coordinates
//  - materialThickness : Extrusion depth for the raw sheet
//  - targetBody : Query for the body being sliced (used to compute rotated bounding box)
// Returns: map containing the ordered list of slice planes and slice IDs
export function generateSheetsAtAngle(context is Context, featureIdPrefix is Id, axisLabel is string, axisAngle is ValueWithUnits, orientedBoundingBox is Box3d, planeSpacing is ValueWithUnits, referenceFrame is CoordSystem, referenceFrameToWorldTransform is Transform, materialThickness is ValueWithUnits, targetBody is Query) returns map
{
    var slicePlanes = [] as array;
    var sliceIds = [] as array;
    
    // Calculate the local axis direction in the XY plane
    const localAxisDirection = vector([cos(axisAngle), sin(axisAngle), 0]);
    
    // The plane normal is perpendicular to the axis direction in the XY plane
    const planeNormal = vector([-sin(axisAngle), cos(axisAngle), 0]);
    const planeUpVector = vector([0, 0, 1]);
    
    // To determine the extent along this axis, we need to rotate the coordinate system
    // and compute the bounding box in that rotated frame
    const rotatedCoordSystem = coordSystem(
        referenceFrame.origin,
        referenceFrameToWorldTransform.linear * localAxisDirection,
        referenceFrame.zAxis
    );
    
    const rotatedBoundingBox = evBox3d(context, {
        "topology" : targetBody,
        "cSys" : rotatedCoordSystem,
        "tight" : true
    });
    
    // The extent along the rotated X-axis tells us how far we need to place slices
    const boundingMin = rotatedBoundingBox.minCorner[0];
    const boundingMax = rotatedBoundingBox.maxCorner[0];
    
    // Rectangle dimensions are based on the full bounding box extents
    const rectangleWidth = orientedBoundingBox.maxCorner[1] - orientedBoundingBox.minCorner[1];
    const rectangleHeight = orientedBoundingBox.maxCorner[2] - orientedBoundingBox.minCorner[2];
    const rectangleCenterY = (orientedBoundingBox.maxCorner[1] + orientedBoundingBox.minCorner[1]) / 2;
    const rectangleCenterZ = (orientedBoundingBox.maxCorner[2] + orientedBoundingBox.minCorner[2]) / 2;
    
    // Calculate diagonal dimension to ensure rectangle covers body at any angle
    const diagonalSize = sqrt(rectangleWidth^2 + rectangleHeight^2);
    const rectangleWidthExpanded = diagonalSize;
    const rectangleHeightExpanded = diagonalSize;
    
    // Calculate which plane indices are needed to cover the bounding box
    const firstPlaneIndex = ceil(boundingMin / planeSpacing);
    const lastPlaneIndex = floor(boundingMax / planeSpacing);
    
    var planeCounter = 0;
    for (var planeIndex = firstPlaneIndex; planeIndex <= lastPlaneIndex; planeIndex += 1)
    {
        // Position along the axis direction
        const planeLocation = planeIndex * planeSpacing;
        
        // Calculate origin in local reference frame coordinates
        // The plane is at distance planeLocation along the axis direction
        const sliceOrigin = planeLocation * localAxisDirection + 
                          vector([0 * meter, 0 * meter, rectangleCenterZ]);
        
        const slicePlane = referenceFrameToWorldTransform * plane(sliceOrigin, planeNormal, planeUpVector);
        const sliceId = featureIdPrefix + axisLabel + planeCounter;
        
        // Transform the extrusion direction from local to world coordinates
        const extrusionDirectionWorld = referenceFrameToWorldTransform.linear * planeNormal;
        
        generateSliceSheet(context, sliceId, slicePlane, rectangleWidthExpanded, rectangleHeightExpanded, extrusionDirectionWorld, materialThickness);
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

            // Delete body if we don't have at least one face of each cap type
            if (size(remainingStartCaps) == 0 || size(remainingEndCaps) == 0)
            {
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

            // Delete body if we don't have at least one face of each cap type
            if (size(remainingStartCaps) == 0 || size(remainingEndCaps) == 0)
            {
                opDeleteBodies(context, featureIdPrefix + "deleteYSlice" + yPlaneIndex, {
                            "entities" : sliceBody
                        });
                continue;
            }

            // Store the slice ID for later robust cap querying
            ySliceIds = append(ySliceIds, ySliceId);
        }
    }

    // With SUBTRACT_COMPLEMENT, the bodies are the original extrusion bodies (modified in place)
    // So we return the slice IDs for querying the bodies, not the intersection operation IDs
    return {
            "xIntersectionIds" : xSliceIds, // These are now the extrusion slice IDs, not boolean operation IDs
            "yIntersectionIds" : ySliceIds,
            "xSliceIds" : xSliceIds,
            "ySliceIds" : ySliceIds
        };
}

// Helper function to process slices for one axis after batch intersection
// Verifies that each slice has valid START and END cap faces
// Inputs:
//  - context : Execution context
//  - featureIdPrefix : Base id used for delete operations
//  - originalSliceIds : Array of slice IDs to process
//  - axisLabel : Label for this axis (e.g., "X", "U", "V") used in operation IDs
// Returns: array of valid slice IDs that survived the intersection
function processAxisSlicesAfterIntersection(context is Context, featureIdPrefix is Id, originalSliceIds is array, axisLabel is string) returns array
{
    var validSliceIds = [] as array;
    for (var sliceIndex = 0; sliceIndex < size(originalSliceIds); sliceIndex += 1)
    {
        var sliceId = originalSliceIds[sliceIndex];
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
                opDeleteBodies(context, featureIdPrefix + "delete" + axisLabel + "Slice" + sliceIndex, {
                            "entities" : sliceBody
                        });
                continue;
            }

            // Store the slice ID for later robust cap querying
            validSliceIds = append(validSliceIds, sliceId);
        }
    }
    return validSliceIds;
}

// Three-axis version of trimSheetsToSolid that handles X, U, and V axes
// Uses the same efficient batch SUBTRACT_COMPLEMENT approach
// Inputs:
//  - featureIdPrefix : Base id used to regenerate the X/U/V identifiers for each slice
//  - xSliceResult, uSliceResult, vSliceResult : Maps containing sliceIds arrays for X-, U-, and V-oriented slices
//  - targetBody : Body query representing the part being sliced
// Returns: map containing the slice IDs for all three axes
export function trimSheetsToSolidThreeAxis(context is Context, featureIdPrefix is Id, xSliceResult is map, uSliceResult is map, vSliceResult is map, targetBody is Query) returns map
{
    var xSliceIds = [] as array;
    var uSliceIds = [] as array;
    var vSliceIds = [] as array;

    const xOriginalSliceIds = xSliceResult.sliceIds;
    const uOriginalSliceIds = uSliceResult.sliceIds;
    const vOriginalSliceIds = vSliceResult.sliceIds;

    // Build queries for all X, U, and V slice bodies
    const allXSliceBodies = qUnion(mapArray(xOriginalSliceIds, function(sliceId)
            {
                return qCreatedBy(sliceId + "extrudeRectangle", EntityType.BODY);
            }));
    const allUSliceBodies = qUnion(mapArray(uOriginalSliceIds, function(sliceId)
            {
                return qCreatedBy(sliceId + "extrudeRectangle", EntityType.BODY);
            }));
    const allVSliceBodies = qUnion(mapArray(vOriginalSliceIds, function(sliceId)
            {
                return qCreatedBy(sliceId + "extrudeRectangle", EntityType.BODY);
            }));
    const allSliceBodies = qUnion([allXSliceBodies, allUSliceBodies, allVSliceBodies]);

    // Perform single batch SUBTRACT_COMPLEMENT operation for all slices at once
    opBoolean(context, featureIdPrefix + "batchIntersection", {
                "tools" : targetBody,
                "targets" : allSliceBodies,
                "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                "keepTools" : true
            });

    // Process each axis using the helper function
    xSliceIds = processAxisSlicesAfterIntersection(context, featureIdPrefix, xOriginalSliceIds, "X");
    uSliceIds = processAxisSlicesAfterIntersection(context, featureIdPrefix, uOriginalSliceIds, "U");
    vSliceIds = processAxisSlicesAfterIntersection(context, featureIdPrefix, vOriginalSliceIds, "V");

    // Return both explicit axis names and Y-axis compatibility mapping
    // The Y-axis fields map to the U-axis for compatibility with existing two-axis functions
    // that expect xIntersectionIds and yIntersectionIds as their second axis parameter
    return {
            "xIntersectionIds" : xSliceIds,
            "yIntersectionIds" : uSliceIds,  // Compatibility: second axis maps to U in three-axis mode
            "uIntersectionIds" : uSliceIds,
            "vIntersectionIds" : vSliceIds,
            "xSliceIds" : xSliceIds,
            "ySliceIds" : uSliceIds,  // Compatibility: second axis maps to U in three-axis mode
            "uSliceIds" : uSliceIds,
            "vSliceIds" : vSliceIds
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

    // Early return if no slices were copied (nothing to process)
    if (size(xSlotIds) == 0 || size(ySlotIds) == 0)
    {
        return { "xSlotIds" : xSlotIds, "ySlotIds" : ySlotIds };
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

// Generate cross-slot geometry for non-orthogonal axes (e.g., X and U with skew angle)
// Similar to generateCrossSlotGeometryForSlices but handles arbitrary angles between axes
// Inputs:
//  - featureIdPrefix : Base id used for operations
//  - xIntersectionIds, yIntersectionIds : Slice IDs for the two axes
//  - referenceFrame : Reference coordinate system
//  - xAxisAngle, yAxisAngle : Angles of the two axes in the XY plane of reference frame
// Returns: map containing slot information
export function generateCrossSlotGeometryForSlicesNonOrthogonal(context is Context, featureIdPrefix is Id, xIntersectionIds is array, yIntersectionIds is array, referenceFrame is CoordSystem, xAxisAngle is ValueWithUnits, yAxisAngle is ValueWithUnits) returns map
{
    // For non-orthogonal angles, we use the same approach but the split plane calculation
    // needs to account for the angle between the axes
    // The midplane should be at the angle bisecting the two axes
    
    var xSlotIds = [] as array;
    var ySlotIds = [] as array;
    var splitPlaneIds = [] as array;
    var splitIds = [] as array;

    // 1. Create Copies
    for (var xPlaneIndex = 0; xPlaneIndex < size(xIntersectionIds); xPlaneIndex += 1)
    {
        const xSliceBody = qCreatedBy(xIntersectionIds[xPlaneIndex] + "extrudeRectangle", EntityType.BODY);
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

    // Early return if no slices were copied (nothing to process)
    if (size(xSlotIds) == 0 || size(ySlotIds) == 0)
    {
        return { "xSlotIds" : xSlotIds, "ySlotIds" : ySlotIds };
    }

    const copiedXSlices = qUnion(mapArray(xSlotIds, function(slotId)
            {
                return qCreatedBy(slotId, EntityType.BODY);
            }));
    const copiedYSlices = qUnion(mapArray(ySlotIds, function(slotId)
            {
                return qCreatedBy(slotId, EntityType.BODY);
            }));

    // 2. Generate Intersection Geometry
    opBoolean(context, featureIdPrefix + "BatchCrossSlots", {
                "tools" : copiedXSlices,
                "targets" : copiedYSlices,
                "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                "keepTargets" : true
            });

    var splitToolsForX = [] as array;
    var splitToolsForY = [] as array;

    // 3. Evaluate Intersections and Split them
    // For non-orthogonal angles, the split plane is still horizontal (perpendicular to Z)
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
                    "targets" : originalXSlices,
                    "operationType" : BooleanOperationType.SUBTRACTION
                });
    }

    if (size(splitToolsForY) > 0)
    {
        opBoolean(context, featureIdPrefix + "booleanYSlots", {
                    "tools" : qUnion(splitToolsForY),
                    "targets" : originalYSlices,
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
                "entities" : qUnion([copiedXSlices, copiedYSlices, splitPlanes, splitBodies])
            });

    return { "xSlotIds" : xSlotIds, "ySlotIds" : ySlotIds };
}

// Generate cross-slot geometry for three axes (X, U, V) in hexagonal pattern
// Cuts slots between all three pairs: X-U, X-V, and U-V
// Inputs:
//  - featureIdPrefix : Base id for operations
//  - trimmedSheetsResult : Map containing xIntersectionIds, uIntersectionIds, vIntersectionIds
//  - referenceFrame : Reference coordinate system
//  - uAxisSkewAngle : Skew angle of U-axis (should be 30 degrees in three-axis mode)
// Returns: none
export function generateCrossSlotGeometryForSlicesThreeAxis(context is Context, featureIdPrefix is Id, trimmedSheetsResult is map, referenceFrame is CoordSystem, uAxisSkewAngle is ValueWithUnits)
{
    const xIntersectionIds = trimmedSheetsResult.xIntersectionIds;
    const uIntersectionIds = trimmedSheetsResult.uIntersectionIds;
    const vIntersectionIds = trimmedSheetsResult.vIntersectionIds;
    
    // Convert U-axis skew angle to absolute angle in XY plane
    const uAxisAbsoluteAngle = uAxisSkewAngle + 90 * degree;
    
    // Process X-U pair (X at 0 degrees, U at ~120 degrees for 30 degrees skew)
    generateCrossSlotGeometryForSlicesNonOrthogonal(context, featureIdPrefix + "XU", xIntersectionIds, uIntersectionIds, referenceFrame, 0 * degree, uAxisAbsoluteAngle);
    
    // Process X-V pair (X at 0 degrees, V at 60 degrees)
    generateCrossSlotGeometryForSlicesNonOrthogonal(context, featureIdPrefix + "XV", xIntersectionIds, vIntersectionIds, referenceFrame, 0 * degree, THREE_AXIS_V_ANGLE);
    
    // Process U-V pair (U at ~120 degrees, V at 60 degrees)
    generateCrossSlotGeometryForSlicesNonOrthogonal(context, featureIdPrefix + "UV", uIntersectionIds, vIntersectionIds, referenceFrame, uAxisAbsoluteAngle, THREE_AXIS_V_ANGLE);
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

    // Get all bodies from X and second axis slices
    const allXBodies = qUnion(mapArray(xIntersectionIds, function(xId)
            {
                return qCreatedBy(xId + "extrudeRectangle", EntityType.BODY);
            }));
    const allYBodies = qUnion(mapArray(yIntersectionIds, function(yId)
            {
                return qCreatedBy(yId + "extrudeRectangle", EntityType.BODY);
            }));
    
    // Add V-axis bodies if present
    var allBodies = qUnion([allXBodies, allYBodies]);
    if (trimmedSheetsResult.vIntersectionIds != undefined && size(trimmedSheetsResult.vIntersectionIds) > 0)
    {
        const allVBodies = qUnion(mapArray(trimmedSheetsResult.vIntersectionIds, function(vId)
                {
                    return qCreatedBy(vId + "extrudeRectangle", EntityType.BODY);
                }));
        allBodies = qUnion([allBodies, allVBodies]);
    }

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

    var bodiesToDelete = qUnion([allXSliceBodies, allYSliceBodies]);
    
    // Add V-axis bodies if present
    if (trimmedSheetsResult.vIntersectionIds != undefined && size(trimmedSheetsResult.vIntersectionIds) > 0)
    {
        const allVSliceBodies = qUnion(mapArray(trimmedSheetsResult.vIntersectionIds, function(vSliceId)
                {
                    return qCreatedBy(vSliceId + "extrudeRectangle", EntityType.BODY);
                }));
        bodiesToDelete = qUnion([bodiesToDelete, allVSliceBodies]);
    }

    try
    {
        opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : bodiesToDelete
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
