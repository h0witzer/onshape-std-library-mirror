// Laser It slices a selected body into a grid of extruded rectangles to prepare geometry for laser cutting.
// Inputs:
//  - selectedBody : Body query to slice
//  - planeSpacing : Distance between slicing planes along the X and Y axes of the reference frame
//  - offset : Boolean to enable XY offsets
//  - xOffset, yOffset : Offsets applied when offset is enabled
//  - matThick : Material thickness that controls extrusion depth
//  - defRefFrame : Boolean to select a mate connector as the slicing reference frame
//  - referenceFrame : Mate connector query when defRefFrame is true
FeatureScript 2815;
import(path : "onshape/std/geometry.fs", version : "2815.0");
import(path : "onshape/std/box.fs", version : "2815.0");

annotation { "Feature Type Name" : "Laser It" }
export const laserIt = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        definition.selectedBody is Query;

        annotation { "Name" : "Plane Spacing" }
        isLength(definition.planeSpacing, LENGTH_BOUNDS);

        annotation { "Name" : "Offset" }
        definition.offset is boolean;

        if (definition.offset)
        {
            annotation { "Name" : "X Offset" }
            isLength(definition.xOffset, LENGTH_BOUNDS);

            annotation { "Name" : "Y Offset" }
            isLength(definition.yOffset, LENGTH_BOUNDS);

        }

        annotation { "Name" : "Material Thickness" }
        isLength(definition.matThick, LENGTH_BOUNDS);


        annotation { "Name" : "Define Reference Frame" }
        definition.defRefFrame is boolean;


        if (definition.defRefFrame)
        {
            annotation { "Name" : "Reference Frame", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
            definition.referenceFrame is Query;
        }

    }
    {
        // Establish the coordinate system used for slicing
        var referenceFrame = WORLD_COORD_SYSTEM;
        var xOffsetDistance = 0 * millimeter;
        var yOffsetDistance = 0 * millimeter;

        if (definition.defRefFrame == true)
        {
            referenceFrame = evMateConnector(context, {
                        "mateConnector" : definition.referenceFrame
                    });
        }

        if (definition.offset)
        {
            xOffsetDistance = definition.xOffset;
            yOffsetDistance = definition.yOffset;
        }

        // Use the coordinate system if provided to define the bounding box (start and end of planes)
        var orientedBoundingBox = evBox3d(context, {
                "topology" : definition.selectedBody,
                "cSys" : referenceFrame,
                "tight" : true
            });

        referenceFrame.origin = toWorld(referenceFrame, box3dCenter(orientedBoundingBox));

        var referenceFrameToWorldTransform = toWorld(referenceFrame);

        var numberOfXPlanes = (orientedBoundingBox.maxCorner[0] - orientedBoundingBox.minCorner[0]) / definition.planeSpacing;
        var numberOfYPlanes = (orientedBoundingBox.maxCorner[1] - orientedBoundingBox.minCorner[1]) / definition.planeSpacing;
        var bodiesToDelete = qNothing();

        // Build a stack of slicing planes perpendicular to X, then Y, that span the oriented bounding box of the target body.
        // Each loop: create a sketch-sized rectangle around the body, extrude it to the material thickness, and retain the raw
        // sheets for a later trimming pass against the selected part.
        var xSliceResult = generateSheets(context, id, "X", numberOfXPlanes, orientedBoundingBox, xOffsetDistance, definition.planeSpacing, referenceFrameToWorldTransform, definition.matThick, bodiesToDelete);
        bodiesToDelete = xSliceResult.bodiesToDelete;

        var ySliceResult = generateSheets(context, id, "Y", numberOfYPlanes, orientedBoundingBox, yOffsetDistance, definition.planeSpacing, referenceFrameToWorldTransform, definition.matThick, bodiesToDelete);
        bodiesToDelete = ySliceResult.bodiesToDelete;

        // Intersect each sheet with the target solid to retain only in-bounds material before generating cross-slot geometry.
        var trimmedSheetsResult = trimSheetsToSolid(context, id, xSliceResult.sliceIds, ySliceResult.sliceIds, definition.selectedBody, bodiesToDelete);
        bodiesToDelete = trimmedSheetsResult.bodiesToDelete;

        // The XY nested loop takes every X slice and intersects it with every Y slice to form individual grid cells.
        // For each cell, it resolves all intersecting bodies, averages aligned edges to infer a mid-surface, and splits the
        // cell into two halves so the original X and Y slice sets can be trimmed against each other.
        var intersectionResult = generateCrossSlotGeometryForSlices(context, id, trimmedSheetsResult.xIntersectionIds, trimmedSheetsResult.yIntersectionIds, referenceFrame, bodiesToDelete);
        bodiesToDelete = intersectionResult.bodiesToDelete;

        // After trimming the intersecting grid, thicken every face that lies on an X-oriented plane to create individual
        // ribs aligned with the X direction.
        for (var xPlaneIndex = 0; xPlaneIndex < size(xSliceResult.slicePlanes); xPlaneIndex += 1)
        {
            normalizeSliceGeometryForLasercutting(context, id + "XExtrude" + xPlaneIndex + "extrudeIntersection", xSliceResult.slicePlanes[xPlaneIndex], trimmedSheetsResult.xIntersectionIds[xPlaneIndex], definition.matThick, xSliceResult.slicePlanes[xPlaneIndex].normal);

        }


        // Repeat the thickening pass for faces lying on Y-oriented planes to generate the orthogonal rib set.
        for (var yPlaneIndex = 0; yPlaneIndex < size(ySliceResult.slicePlanes); yPlaneIndex += 1)
        {
            normalizeSliceGeometryForLasercutting(context, id + "YExtrude" + yPlaneIndex + "extrudeIntersection", ySliceResult.slicePlanes[yPlaneIndex], trimmedSheetsResult.yIntersectionIds[yPlaneIndex], definition.matThick, ySliceResult.slicePlanes[yPlaneIndex].normal);

        }

        // Clean up intermediate construction geometry and sketch bodies
        opDeleteBodies(context, id + "deleteBodies1", {
                    "entities" : bodiesToDelete
                });

    });

// Create rectangular sheets along a specified axis, returning plane definitions for downstream trimming and rib generation.
// Inputs:
//  - featureIdPrefix : Base id used when naming all geometry created in this helper
//  - axisLabel : Either "X" or "Y" to select the normal and sketch dimensions for the slicing plane
//  - numberOfPlanes : Loop bound describing how many slices exist along the specified axis
//  - orientedBoundingBox : Tight bounding box for the selected body in the reference frame
//  - offsetDistance : Optional offset distance used to shift the slice origins
//  - planeSpacing : Distance between slices
//  - referenceFrameToWorldTransform : Transform aligning the local slice planes with world coordinates
//  - materialThickness : Extrusion depth for the raw sheet
//  - bodiesToDelete : Accumulated query of cleanup bodies to extend
// Returns: map containing the updated bodiesToDelete query and the ordered list of slice planes
export function generateSheets(context is Context, featureIdPrefix is Id, axisLabel is string, numberOfPlanes is number, orientedBoundingBox is Box3d, offsetDistance is ValueWithUnits, planeSpacing is ValueWithUnits, referenceFrameToWorldTransform is Transform, materialThickness is ValueWithUnits, bodiesToDelete is Query)
{
    var slicePlanes = [] as array;
    var sliceIds = [] as array;
    var planeNormal = vector([1, 0, 0]);
    var planeUpVector = vector([0, 1, 0]);
    var rectangleWidth = orientedBoundingBox.maxCorner[1] - orientedBoundingBox.minCorner[1];
    var rectangleHeight = orientedBoundingBox.maxCorner[2] - orientedBoundingBox.minCorner[2];
    var boundingSpan = orientedBoundingBox.maxCorner[0] - orientedBoundingBox.minCorner[0];

    if (axisLabel == "Y")
    {
        planeNormal = vector([0, 1, 0]);
        planeUpVector = vector([0, 0, 1]);
        rectangleWidth = orientedBoundingBox.maxCorner[2] - orientedBoundingBox.minCorner[2];
        rectangleHeight = orientedBoundingBox.maxCorner[0] - orientedBoundingBox.minCorner[0];
        boundingSpan = orientedBoundingBox.maxCorner[1] - orientedBoundingBox.minCorner[1];
    }

    for (var planeIndex = 0; planeIndex < numberOfPlanes; planeIndex += 1)
    {
        var planeLocation = -(boundingSpan) / 2 + offsetDistance + planeIndex * planeSpacing;
        var sliceOrigin = vector([0 * millimeter, 0 * millimeter, 0 * millimeter]);

        if (axisLabel == "X")
        {
            sliceOrigin = vector([planeLocation, 0 * millimeter, 0 * millimeter]);
        }
        else
        {
            sliceOrigin = vector([0 * millimeter, planeLocation, 0 * millimeter]);
        }

        var slicePlane = referenceFrameToWorldTransform * plane(sliceOrigin, planeNormal, planeUpVector);
        var sliceId = featureIdPrefix + axisLabel + planeIndex;
        var sliceResult = generateSliceSheet(context, sliceId, slicePlane, rectangleWidth, rectangleHeight, planeNormal, materialThickness, bodiesToDelete);
        bodiesToDelete = sliceResult.bodiesToDelete;
        slicePlanes = append(slicePlanes, slicePlane);
        sliceIds = append(sliceIds, sliceId);
    }

    return { "bodiesToDelete" : bodiesToDelete, "slicePlanes" : slicePlanes, "sliceIds" : sliceIds };
}

// Intersect every raw sheet with the target body to keep only the in-bounds material for follow-on trimming.
// Inputs:
//  - featureIdPrefix : Base id used to regenerate the X/Y identifiers for each slice
//  - xSliceIds, ySliceIds : Ordered identifiers for X- and Y-oriented slice bodies
//  - targetBody : Body query representing the part being sliced
//  - bodiesToDelete : Accumulated query of cleanup bodies to extend
// Returns: map containing the updated bodiesToDelete query and intersection ids
export function trimSheetsToSolid(context is Context, featureIdPrefix is Id, xSliceIds is array, ySliceIds is array, targetBody is Query, bodiesToDelete is Query)
{
    var xIntersectionIds = [] as array;
    var yIntersectionIds = [] as array;

    for (var xPlaneIndex = 0; xPlaneIndex < size(xSliceIds); xPlaneIndex += 1)
    {
        var xSliceId = xSliceIds[xPlaneIndex];
        var xIntersectionId = featureIdPrefix + "XIntersection" + xPlaneIndex;
        opBoolean(context, xIntersectionId, {
                    "tools" : qUnion([qCreatedBy(xSliceId + "extrudeRectangle", EntityType.BODY), targetBody]),
                    "operationType" : BooleanOperationType.INTERSECTION,
                    "keepTools" : true
                });

        bodiesToDelete = qUnion([bodiesToDelete, qCreatedBy(xIntersectionId)]);
        xIntersectionIds = append(xIntersectionIds, xIntersectionId);
    }

    for (var yPlaneIndex = 0; yPlaneIndex < size(ySliceIds); yPlaneIndex += 1)
    {
        var ySliceId = ySliceIds[yPlaneIndex];
        var yIntersectionId = featureIdPrefix + "YIntersection" + yPlaneIndex;
        opBoolean(context, yIntersectionId, {
                    "tools" : qUnion([qCreatedBy(ySliceId + "extrudeRectangle", EntityType.BODY), targetBody]),
                    "operationType" : BooleanOperationType.INTERSECTION,
                    "keepTools" : true
                });

        bodiesToDelete = qUnion([bodiesToDelete, qCreatedBy(yIntersectionId)]);
        yIntersectionIds = append(yIntersectionIds, yIntersectionId);
    }

    return { "bodiesToDelete" : bodiesToDelete, "xIntersectionIds" : xIntersectionIds, "yIntersectionIds" : yIntersectionIds };
}

// Intersect every X and Y slice pairing, derive mid-planes through overlapping solids, and trim the orthogonal slice sets.
// Inputs:
//  - featureIdPrefix : Base id used to regenerate the X/Y boolean identifiers for each intersection cell
//  - xIntersectionIds, yIntersectionIds : Ordered identifiers for the trimmed X and Y slice bodies
//  - referenceFrame : Coordinate system establishing the Z axis for mid-plane evaluation
//  - bodiesToDelete : Accumulated query of cleanup bodies to extend
// Returns: map containing the updated bodiesToDelete query
export function generateCrossSlotGeometryForSlices(context is Context, featureIdPrefix is Id, xIntersectionIds is array, yIntersectionIds is array, referenceFrame is CoordSystem, bodiesToDelete is Query)
{
    for (var xPlaneIndex = 0; xPlaneIndex < size(xIntersectionIds); xPlaneIndex += 1)
    {
        for (var yPlaneIndex = 0; yPlaneIndex < size(yIntersectionIds); yPlaneIndex += 1)
        {
            try
            {
                // For a given X/Y plane pairing, step through every intersection body produced by earlier booleans so that
                // each overlapping solid gets intersected independently. This keeps disjoint islands from being skipped.
                var xIntersectionIndex = 0;
                var yIntersectionIndex = 0;
                for (var xIntersectionSlice in evaluateQuery(context, qCreatedBy(xIntersectionIds[xPlaneIndex], EntityType.BODY)))
                {

                    for (var yIntersectionSlice in evaluateQuery(context, qCreatedBy(yIntersectionIds[yPlaneIndex], EntityType.BODY)))
                    {
                        try
                        {

                            opBoolean(context, featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "boolean1" + xIntersectionIndex + yIntersectionIndex + "Subunit", {
                                        "tools" : qUnion([xIntersectionSlice, yIntersectionSlice]),
                                        "operationType" : BooleanOperationType.INTERSECTION,
                                        "keepTools" : true
                                    });
                        }
                        xIntersectionIndex = xIntersectionIndex + 1;
                    }
                    yIntersectionIndex = yIntersectionIndex + 1;
                }

                var allIntersectionEdges = evaluateQuery(context, qOwnedByBody(qCreatedBy(featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "boolean1", EntityType.BODY), EntityType.EDGE));
                var zCoordinateAccumulator = 0 * millimeter;
                var numberOfAlignedEdges = 0;
                // Measure every edge and average the ones parallel to the reference Z axis to find a reliable mid-plane
                // through the slice that can split it into mirrored halves.
                for (var intersectionEdge in allIntersectionEdges)
                {
                    try
                    {
                        var evaluatedEdgeLine = evLine(context, {
                                "edge" : intersectionEdge
                            });
                        if (abs(dot(evaluatedEdgeLine.direction, referenceFrame.zAxis)) > .9999) // Normal tolerance of ParallelVectors is too tight for some reason after error accumulates
                        {
                            // Find the midpoint of the edge and add to average
                            var edgeTangentLine = evEdgeTangentLine(context, {
                                    "edge" : intersectionEdge,
                                    "parameter" : .5
                                });
                            numberOfAlignedEdges += 1;
                            zCoordinateAccumulator += dot(edgeTangentLine.origin, referenceFrame.zAxis);
                        }
                    }
                }
                if (numberOfAlignedEdges > 0)
                {
                    var slicePlaneOrigin = (referenceFrame.zAxis * zCoordinateAccumulator / numberOfAlignedEdges) / squaredNorm(referenceFrame.zAxis);
                    var slicePlane = plane(slicePlaneOrigin, referenceFrame.zAxis);
                    opPlane(context, featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "plane1", {
                                "plane" : slicePlane
                            });
                    opSplitPart(context, featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "splitPart1", {
                                "targets" : qCreatedBy(featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "boolean1", EntityType.BODY),
                                "tool" : qCreatedBy(featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "plane1", EntityType.BODY)
                            });
                    opBoolean(context, featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "boolean2", {
                                "tools" : qFarthestAlong(qOwnerBody(qCreatedBy(featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "splitPart1")), referenceFrame.zAxis),
                                "targets" : qCreatedBy(xIntersectionIds[xPlaneIndex], EntityType.BODY),
                                "operationType" : BooleanOperationType.SUBTRACTION
                            });

                    opBoolean(context, featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "boolean3", {
                                "tools" : qFarthestAlong(qOwnerBody(qCreatedBy(featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "splitPart1")), -referenceFrame.zAxis),
                                "targets" : qCreatedBy(yIntersectionIds[yPlaneIndex], EntityType.BODY),
                                "operationType" : BooleanOperationType.SUBTRACTION
                            });
                    bodiesToDelete = qUnion([bodiesToDelete, qCreatedBy(featureIdPrefix + "XY" + xPlaneIndex + yPlaneIndex + "plane1")]);
                }
            }
        }
    }

    return { "bodiesToDelete" : bodiesToDelete };
}

// Sketch and extrude a rectangular slice at the provided plane, retaining the untrimmed sheet for a later boolean against the target body.
// Inputs:
//  - sliceId : Unique Id prefix used for sketch and extrusion operations
//  - slicePlane : Plane definition representing the slice location and orientation
//  - rectangleWidth, rectangleHeight : Dimensions of the rectangle sketched on the plane
//  - extrusionDirection : Direction vector for the slice thickening
//  - materialThickness : Extrusion depth matching the stock thickness
//  - bodiesToDelete : Accumulated query of cleanup bodies to extend
// Returns: map containing the updated bodiesToDelete query and the slice plane
export function generateSliceSheet(context is Context, sliceId is Id, slicePlane is Plane, rectangleWidth is ValueWithUnits, rectangleHeight is ValueWithUnits, extrusionDirection is Vector, materialThickness is ValueWithUnits, bodiesToDelete is Query)
{
    var sliceSketch = newSketchOnPlane(context, sliceId + "sketch", {
                "sketchPlane" : slicePlane
            });

    skRectangle(sliceSketch, "rectangle1", {
                "firstCorner" : -vector([rectangleWidth, rectangleHeight]) / 2,
                "secondCorner" : vector([rectangleWidth, rectangleHeight]) / 2
            });

    skSolve(sliceSketch);

    bodiesToDelete = qUnion([bodiesToDelete, qCreatedBy(sliceId + "sketch")]);

    // Extrude a rectangular slice surrounding the object
    opExtrude(context, sliceId + "extrudeRectangle", {
                "entities" : qSketchRegion(sliceId + "sketch", false),
                "direction" : extrusionDirection,
                "endBound" : BoundingType.BLIND,
                "endDepth" : materialThickness
            });

    bodiesToDelete = qUnion([bodiesToDelete, qCreatedBy(sliceId + "extrudeRectangle", EntityType.BODY)]);

    return { "bodiesToDelete" : bodiesToDelete, "slicePlane" : slicePlane };
}

// Extrude every face coincident with the given plane from the provided boolean intersection into ribs along the supplied direction.
// Inputs:
//  - extrudeIdPrefix : Id prefix used for created ribs
//  - slicingPlane : Plane the faces must coincide with
//  - intersectionBooleanId : Id of the prior trimmed intersection operation for this slice
//  - materialThickness : Extrusion depth used for rib generation
//  - ribDirection : Direction vector for the rib extrusion
export function normalizeSliceGeometryForLasercutting(context is Context, extrudeIdPrefix is Id, slicingPlane is Plane, intersectionBooleanId is Id, materialThickness is ValueWithUnits, ribDirection is Vector)
{
    var subunitIndex = 0;
    for (var sliceFace in evaluateQuery(context, qCoincidesWithPlane(qOwnedByBody(qOwnerBody(qCreatedBy(intersectionBooleanId)), EntityType.FACE), slicingPlane)))
    {
        try
        {
            opExtrude(context, extrudeIdPrefix + subunitIndex + "subUnitOp", {
                        "entities" : sliceFace,
                        "direction" : ribDirection,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : materialThickness
                    });
        }
        subunitIndex = subunitIndex + 1;
    }
}
