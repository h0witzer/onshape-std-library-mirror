FeatureScript 2837;

/**
 * Free-Form Deformation with Plane-Based Manipulation
 * 
 * This feature provides an alternative manipulation method for FFD (Free-Form Deformation)
 * that uses planes instead of individual control points. This approach is designed to be
 * more stable and intuitive for common deformation tasks.
 * 
 * Key Differences from Standard FFD:
 * - Assumes 1 span in all directions except the selected manipulation direction
 * - Manipulates entire planes of control points together
 * - Planes can be translated, rotated, and scaled as a unit
 * - More stable indexing when adding or removing planes
 * - Simpler UI with fewer individual control points to manage
 * 
 * Concept:
 * 1. Choose a primary manipulation direction (S, T, or U)
 * 2. The lattice has 1 span in the other two directions (creating a 2x2 grid per plane)
 * 3. Multiple spans can exist in the selected direction, creating multiple planes
 * 4. Each plane can be manipulated as a whole unit
 * 5. All four control points on a plane move together maintaining plane geometry
 * 
 * Usage:
 * 1. Select one or more surface faces to deform
 * 2. Choose manipulation direction (S, T, or U)
 * 3. Set number of planes (spans + 1) in the manipulation direction
 * 4. Enable "Edit planes" to begin manipulation
 * 5. Select a plane and use manipulators to translate/rotate it
 * 6. All control points on that plane follow the plane transformation
 * 
 * Benefits:
 * - Easier to create smooth, controlled deformations
 * - More predictable behavior when adjusting geometry
 * - Simpler mental model: think in terms of cross-sections
 * - Stable indexing even when inserting/deleting planes
 * 
 * Implementation references:
 * - Based on: custom-features/freeFormDeformation.fs
 * - Whitepaper: whitepaper-references/"Free-Form Deformation of Parametric CAD Geometry.pdf"
 */

// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/context.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/manipulator.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/box.fs", version : "2837.0");
import(path : "onshape/std/nurbsUtils.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/transform.fs", version : "2837.0");


// Manipulation direction enum
export enum FFDPlaneDirection
{
    annotation { "Name" : "S direction (X-axis)" }
    S_DIRECTION,
    annotation { "Name" : "T direction (Y-axis)" }
    T_DIRECTION,
    annotation { "Name" : "U direction (Z-axis)" }
    U_DIRECTION
}

// Bounds for number of planes in manipulation direction
export const FFD_PLANE_COUNT_BOUNDS = {
    (unitless) : [2, 3, 12]
} as IntegerBoundSpec;

// Manipulator identifiers
const PLANE_SELECTION_MANIPULATOR = "planeSelectionManipulator";
const PLANE_TRIAD_MANIPULATOR = "planeTriadManipulator";


/**
 * Free-Form Deformation with Plane-Based Manipulation feature definition
 */
annotation { "Feature Type Name" : "FFD - Plane Manipulation",
        "Feature Type Description" : "Deform surfaces using plane-based control. Choose a direction and manipulate entire cross-sectional planes instead of individual points.",
        "UIHint" : "NO_PREVIEW_PROVIDED",
        "Manipulator Change Function" : "ffdPlaneManipulator" }
export const freeFormDeformationPlanes = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Surfaces to deform", 
                     "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO }
        definition.surfacesToDeform is Query;
        
        annotation { "Name" : "Manipulation direction",
                     "Description" : "Direction along which planes are created and manipulated" }
        definition.manipulationDirection is FFDPlaneDirection;
        
        annotation { "Name" : "Number of planes",
                     "Description" : "Number of control planes in the manipulation direction (2 to 12)" }
        isInteger(definition.planeCount, FFD_PLANE_COUNT_BOUNDS);
        
        annotation { "Name" : "Selected plane index", "UIHint" : UIHint.ALWAYS_HIDDEN }
        isInteger(definition.selectedPlaneIndex, { (unitless) : [0, 0, 100] } as IntegerBoundSpec);
        
        annotation { "Name" : "Edit planes" }
        definition.editPlanes is boolean;
        
        annotation { "Group Name" : "Edit planes", 
                     "Driving Parameter" : "editPlanes", 
                     "Collapsed By Default" : false }
        {
            if (definition.editPlanes)
            {
                annotation { "Name" : "Plane transformations", 
                             "Item name" : "plane transform", 
                             "Item label template" : "Plane #index", 
                             "UIHint" : UIHint.PREVENT_ARRAY_REORDER }
                definition.planeTransformations is array;
                for (var planeTransform in definition.planeTransformations)
                {
                    annotation { "Name" : "Plane index" }
                    isInteger(planeTransform.index, { (unitless) : [0, 0, 100] } as IntegerBoundSpec);
                    
                    // Store transform as decomposed components (rotation matrix + translation vector)
                    // Use isAnything for rotationMatrix to avoid "Cannot define array within array" error
                    annotation { "Name" : "Rotation matrix", "UIHint" : UIHint.ALWAYS_HIDDEN }
                    isAnything(planeTransform.rotationMatrix);
                    
                    annotation { "Name" : "Translation X", "Group Name" : "Transform" }
                    isLength(planeTransform.translateX, ZERO_DEFAULT_LENGTH_BOUNDS);
                    
                    annotation { "Name" : "Translation Y", "Group Name" : "Transform" }
                    isLength(planeTransform.translateY, ZERO_DEFAULT_LENGTH_BOUNDS);
                    
                    annotation { "Name" : "Translation Z", "Group Name" : "Transform" }
                    isLength(planeTransform.translateZ, ZERO_DEFAULT_LENGTH_BOUNDS);
                }
            }
        }
        
        annotation { "Name" : "Enable diagnostics" }
        definition.enableDiagnostics is boolean;
        
        annotation { "Group Name" : "Developer diagnostics", 
                     "Driving Parameter" : "enableDiagnostics", 
                     "Collapsed By Default" : true }
        {
            if (definition.enableDiagnostics)
            {
                annotation { "Name" : "Show planes" }
                definition.showPlanes is boolean;
                
                annotation { "Name" : "Show lattice control points" }
                definition.showLatticeControlPoints is boolean;
                
                annotation { "Name" : "Show bounding box" }
                definition.showBoundingBox is boolean;
                
                annotation { "Name" : "Print lattice information" }
                definition.printLatticeInfo is boolean;
            }
        }
    }
    {
        // Validate input surface selection
        const surfaceCount = evaluateQueryCount(context, definition.surfacesToDeform);
        if (surfaceCount == 0)
            throw regenError("Select at least one surface to deform.", ["surfacesToDeform"]);
        
        const inputFaces = evaluateQuery(context, definition.surfacesToDeform);
        
        // Apply plane-based FFD deformation
        applyPlaneBasedFFDDeformation(context, id, inputFaces, definition);
    }, {
        manipulationDirection : FFDPlaneDirection.U_DIRECTION,
        planeCount : 3,
        editPlanes : false,
        selectedPlaneIndex : 0,
        planeTransformations : [],
        enableDiagnostics : false,
        showPlanes : false,
        showLatticeControlPoints : false,
        showBoundingBox : false,
        printLatticeInfo : false
    });


/**
 * Applies plane-based FFD deformation to multiple input surfaces
 * 
 * This function creates a lattice with 1 span in two directions and multiple spans
 * in the selected manipulation direction, then applies transformations plane by plane.
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : The feature identifier
 * @param inputFaces {array} : Array of faces to deform
 * @param definition {map} : Feature definition containing plane parameters and transformations
 */
function applyPlaneBasedFFDDeformation(context is Context, id is Id, inputFaces is array, definition is map)
{
    // Step 1: Extract B-spline surface representations for all input faces
    var surfaceDefinitions = [];
    var allControlPoints = [];
    
    for (var faceIndex = 0; faceIndex < size(inputFaces); faceIndex += 1)
    {
        const inputFace = inputFaces[faceIndex];
        
        // Get B-spline surface representation
        var surfaceDefinition = evSurfaceDefinition(context, {
            "face" : inputFace
        });
        
        // If not already a B-spline, approximate it
        if (surfaceDefinition.surfaceType != SurfaceType.SPLINE)
        {
            const approximation = evApproximateBSplineSurface(context, {
                "face" : inputFace
            });
            surfaceDefinition = approximation.bSplineSurface;
        }
        
        surfaceDefinitions = append(surfaceDefinitions, surfaceDefinition);
        
        // Collect all control points from this surface for unified bounding box
        const controlPoints = surfaceDefinition.controlPoints;
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            for (var vIndex = 0; vIndex < size(controlPoints[0]); vIndex += 1)
            {
                allControlPoints = append(allControlPoints, controlPoints[uIndex][vIndex]);
            }
        }
    }
    
    // Step 2: Compute a unified bounding box
    const boundingBox = computeUnifiedBoundingBox(allControlPoints);
    
    if (definition.showBoundingBox)
    {
        visualizeBoundingBox(context, id + "bbox", boundingBox);
    }
    
    // Step 3: Build the plane-based FFD lattice
    // Determine span counts based on manipulation direction
    const spanCounts = getSpanCountsForDirection(definition.manipulationDirection, definition.planeCount);
    
    var latticeOriginal = buildPlaneBasedFFDLattice(boundingBox, spanCounts, definition.manipulationDirection);
    
    // Build a modified lattice with plane transformations applied
    var lattice = buildPlaneBasedFFDLattice(boundingBox, spanCounts, definition.manipulationDirection);
    if (definition.editPlanes && size(definition.planeTransformations) > 0)
    {
        if (definition.printLatticeInfo)
        {
            println("Applying " ~ size(definition.planeTransformations) ~ " plane transformations");
        }
        lattice = applyPlaneTransformationsToLattice(lattice, definition.planeTransformations, definition.manipulationDirection);
        
        if (definition.printLatticeInfo)
        {
            println("Sample control point before transform: " ~ latticeOriginal.controlPoints[0]);
            println("Sample control point after transform: " ~ lattice.controlPoints[0]);
        }
    }
    
    if (definition.printLatticeInfo)
    {
        printPlaneLatticeInformation(lattice, definition.manipulationDirection);
    }
    
    // Add manipulators for interactive plane manipulation
    addPlaneManipulators(context, id, latticeOriginal, lattice, definition.selectedPlaneIndex, 
                        definition.editPlanes, definition.planeTransformations, definition.manipulationDirection);
    
    if (definition.showLatticeControlPoints)
    {
        visualizeLatticeControlPoints(context, id + "lattice", lattice);
    }
    
    if (definition.showPlanes)
    {
        visualizePlanes(context, id + "planes", lattice, definition.manipulationDirection, definition.planeTransformations);
    }
    
    // Step 4: Apply the same lattice deformation to each surface
    for (var surfaceIndex = 0; surfaceIndex < size(surfaceDefinitions); surfaceIndex += 1)
    {
        const surfaceDefinition = surfaceDefinitions[surfaceIndex];
        const controlPoints = surfaceDefinition.controlPoints;
        const weights = surfaceDefinition.weights;
        
        // Deform surface control points using FFD
        var deformedControlPoints = [];
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            var deformedRow = [];
            for (var vIndex = 0; vIndex < size(controlPoints[0]); vIndex += 1)
            {
                const originalPoint = controlPoints[uIndex][vIndex];
                
                // Convert to STU parametric space using ORIGINAL lattice geometry
                // The original lattice defines the parametric space; we don't want transformed control points to affect this
                const stuCoords = convertWorldToSTU(originalPoint, latticeOriginal);
                
                // Evaluate trivariate Bernstein polynomial using MODIFIED lattice control points
                // This is where the deformation actually happens
                const deformedPoint = evaluateTrivariateBernstein(stuCoords, lattice);
                
                deformedRow = append(deformedRow, deformedPoint);
            }
            deformedControlPoints = append(deformedControlPoints, deformedRow);
        }
        
        // Create the deformed B-spline surface
        const deformedSurfaceDefinition = bSplineSurface({
            "uDegree" : surfaceDefinition.uDegree,
            "vDegree" : surfaceDefinition.vDegree,
            "isUPeriodic" : surfaceDefinition.isUPeriodic,
            "isVPeriodic" : surfaceDefinition.isVPeriodic,
            "controlPoints" : controlPointMatrix(deformedControlPoints),
            "weights" : weights == undefined ? undefined : matrix(weights),
            "uKnots" : surfaceDefinition.uKnots,
            "vKnots" : surfaceDefinition.vKnots
        });
        
        // Create each deformed surface with a unique sub-ID
        opCreateBSplineSurface(context, id + ("surface" ~ surfaceIndex), {
            "bSplineSurface" : deformedSurfaceDefinition
        });
    }
}


/**
 * Determines span counts based on manipulation direction
 * 
 * For plane-based manipulation, we use 1 span in the two non-manipulation directions
 * and (planeCount - 1) spans in the manipulation direction.
 * 
 * @param direction {FFDPlaneDirection} : The manipulation direction
 * @param planeCount {number} : Number of planes (spans + 1)
 * @returns {array} : Array of 3 integers [spanS, spanT, spanU]
 */
function getSpanCountsForDirection(direction is FFDPlaneDirection, planeCount is number) returns array
{
    const spansInManipulationDirection = planeCount - 1;
    
    if (direction == FFDPlaneDirection.S_DIRECTION)
    {
        return [spansInManipulationDirection, 1, 1];
    }
    else if (direction == FFDPlaneDirection.T_DIRECTION)
    {
        return [1, spansInManipulationDirection, 1];
    }
    else // U_DIRECTION
    {
        return [1, 1, spansInManipulationDirection];
    }
}


/**
 * Builds a plane-based FFD lattice structure
 * 
 * Similar to the standard FFD lattice but organized with plane information.
 * Each plane is a set of 4 control points (2x2 grid in the non-manipulation directions).
 * 
 * @param boundingBox {map} : Map with minCorner and maxCorner (Vector3)
 * @param spanCounts {array} : Array of 3 integers [spanS, spanT, spanU]
 * @param direction {FFDPlaneDirection} : The manipulation direction
 * @returns {map} : Lattice structure with control points, axes, plane information, etc.
 */
function buildPlaneBasedFFDLattice(boundingBox is map, spanCounts is array, direction is FFDPlaneDirection) returns map
{
    const minCorner = boundingBox.minCorner;
    const maxCorner = boundingBox.maxCorner;
    
    // Compute axes (directions and magnitudes)
    const axisS = vector(maxCorner[0] - minCorner[0], 0 * meter, 0 * meter);
    const axisT = vector(0 * meter, maxCorner[1] - minCorner[1], 0 * meter);
    const axisU = vector(0 * meter, 0 * meter, maxCorner[2] - minCorner[2]);
    
    const spanCountS = spanCounts[0];
    const spanCountT = spanCounts[1];
    const spanCountU = spanCounts[2];
    
    // Number of control points = spans + 1
    const controlPointCountS = spanCountS + 1;
    const controlPointCountT = spanCountT + 1;
    const controlPointCountU = spanCountU + 1;
    
    const totalControlPoints = controlPointCountS * controlPointCountT * controlPointCountU;
    
    // Initialize control points in a 3D grid
    var controlPoints = [];
    for (var indexS = 0; indexS < controlPointCountS; indexS += 1)
    {
        for (var indexT = 0; indexT < controlPointCountT; indexT += 1)
        {
            for (var indexU = 0; indexU < controlPointCountU; indexU += 1)
            {
                // Compute position: origin + (i/spanS)*axisS + (j/spanT)*axisT + (k/spanU)*axisU
                const fractionS = indexS / spanCountS;
                const fractionT = indexT / spanCountT;
                const fractionU = indexU / spanCountU;
                
                const position = minCorner + axisS * fractionS + axisT * fractionT + axisU * fractionU;
                controlPoints = append(controlPoints, position);
            }
        }
    }
    
    // Organize control points into planes based on manipulation direction
    var planeData = [];
    var planeCount = 0;
    
    if (direction == FFDPlaneDirection.S_DIRECTION)
    {
        planeCount = controlPointCountS;
        for (var indexS = 0; indexS < controlPointCountS; indexS += 1)
        {
            var planePoints = [];
            for (var indexT = 0; indexT < controlPointCountT; indexT += 1)
            {
                for (var indexU = 0; indexU < controlPointCountU; indexU += 1)
                {
                    const linearIndex = indexS * controlPointCountT * controlPointCountU + 
                                      indexT * controlPointCountU + indexU;
                    planePoints = append(planePoints, linearIndex);
                }
            }
            planeData = append(planeData, {
                "pointIndices" : planePoints,
                "planeIndex" : indexS
            });
        }
    }
    else if (direction == FFDPlaneDirection.T_DIRECTION)
    {
        planeCount = controlPointCountT;
        for (var indexT = 0; indexT < controlPointCountT; indexT += 1)
        {
            var planePoints = [];
            for (var indexS = 0; indexS < controlPointCountS; indexS += 1)
            {
                for (var indexU = 0; indexU < controlPointCountU; indexU += 1)
                {
                    const linearIndex = indexS * controlPointCountT * controlPointCountU + 
                                      indexT * controlPointCountU + indexU;
                    planePoints = append(planePoints, linearIndex);
                }
            }
            planeData = append(planeData, {
                "pointIndices" : planePoints,
                "planeIndex" : indexT
            });
        }
    }
    else // U_DIRECTION
    {
        planeCount = controlPointCountU;
        for (var indexU = 0; indexU < controlPointCountU; indexU += 1)
        {
            var planePoints = [];
            for (var indexS = 0; indexS < controlPointCountS; indexS += 1)
            {
                for (var indexT = 0; indexT < controlPointCountT; indexT += 1)
                {
                    const linearIndex = indexS * controlPointCountT * controlPointCountU + 
                                      indexT * controlPointCountU + indexU;
                    planePoints = append(planePoints, linearIndex);
                }
            }
            planeData = append(planeData, {
                "pointIndices" : planePoints,
                "planeIndex" : indexU
            });
        }
    }
    
    return {
        "minCorner" : minCorner,
        "maxCorner" : maxCorner,
        "axisS" : axisS,
        "axisT" : axisT,
        "axisU" : axisU,
        "spanCountS" : spanCountS,
        "spanCountT" : spanCountT,
        "spanCountU" : spanCountU,
        "controlPointCountS" : controlPointCountS,
        "controlPointCountT" : controlPointCountT,
        "controlPointCountU" : controlPointCountU,
        "totalControlPoints" : totalControlPoints,
        "controlPoints" : controlPoints,
        "planeData" : planeData,
        "planeCount" : planeCount,
        "manipulationDirection" : direction,
        // Pre-compute cross products for coordinate conversion
        "crossS" : cross(axisT, axisU),
        "crossT" : cross(axisS, axisU),
        "crossU" : cross(axisS, axisT)
    };
}


/**
 * Applies plane transformations to the lattice control points
 * 
 * For each transformed plane, all control points on that plane are moved together
 * according to the plane's translation and rotation.
 * 
 * @param lattice {map} : Lattice structure (will be modified)
 * @param planeTransformations {array} : Array of plane transformation entries
 * @param direction {FFDPlaneDirection} : The manipulation direction
 * @returns {map} : The modified lattice structure with transformed control points
 */
function applyPlaneTransformationsToLattice(lattice is map, planeTransformations is array, direction is FFDPlaneDirection) returns map
{
    // Store the original control points before any transformations
    const originalControlPoints = lattice.controlPoints;
    
    // Create a deep copy of control points array so modifications don't affect the original
    var modifiedControlPoints = [];
    for (var pointIndex = 0; pointIndex < size(originalControlPoints); pointIndex += 1)
    {
        modifiedControlPoints = append(modifiedControlPoints, originalControlPoints[pointIndex]);
    }
    
    for (var transformEntry in planeTransformations)
    {
        const planeIndex = transformEntry.index;
        if (planeIndex >= 0 && planeIndex < lattice.planeCount)
        {
            // Get the plane data for this index
            const planeInfo = lattice.planeData[planeIndex];
            const pointIndices = planeInfo.pointIndices;
            
            // Calculate plane center from the ORIGINAL (untransformed) control points
            var originalPlaneCenter = vector(0 * meter, 0 * meter, 0 * meter);
            for (var pointIndex in pointIndices)
            {
                originalPlaneCenter = originalPlaneCenter + originalControlPoints[pointIndex];
            }
            originalPlaneCenter = originalPlaneCenter / size(pointIndices);
            
            // Get the stored transform components for this plane
            const rotationMatrix = transformEntry.rotationMatrix;
            const translateX = transformEntry.translateX;
            const translateY = transformEntry.translateY;
            const translateZ = transformEntry.translateZ;
            
            // Reconstruct the transform from stored components
            var planeTransform = identityTransform();
            if (rotationMatrix != undefined && size(rotationMatrix) == 9)
            {
                // Convert flat array to Matrix
                const matrix3x3 = matrix([
                    [rotationMatrix[0], rotationMatrix[1], rotationMatrix[2]],
                    [rotationMatrix[3], rotationMatrix[4], rotationMatrix[5]],
                    [rotationMatrix[6], rotationMatrix[7], rotationMatrix[8]]
                ]);
                planeTransform = transform(matrix3x3, vector(translateX, translateY, translateZ));
            }
            
            // Create a coordinate system at the original plane center
            // Determine plane orientation based on manipulation direction
            var planeNormal = vector(0, 0, 1);
            var planeX = vector(1, 0, 0);
            if (direction == FFDPlaneDirection.S_DIRECTION)
            {
                planeNormal = vector(1, 0, 0);
                planeX = vector(0, 1, 0);
            }
            else if (direction == FFDPlaneDirection.T_DIRECTION)
            {
                planeNormal = vector(0, 1, 0);
                planeX = vector(0, 0, 1);
            }
            else // U_DIRECTION
            {
                planeNormal = vector(0, 0, 1);
                planeX = vector(1, 0, 0);
            }
            
            const planeCoordSys = coordSystem(originalPlaneCenter, planeX, planeNormal);
            
            // Apply the plane-relative transform using the sandwich pattern
            // The fullTriadManipulator gives us a transform relative to the base coordinate system
            // Apply the INVERSE to get the correct rotation direction
            const inverseTransform = inverse(planeTransform);
            const worldTransform = toWorld(planeCoordSys) * inverseTransform * fromWorld(planeCoordSys);
            
            // Apply transformation to all points on this plane
            // Use the ORIGINAL point positions as the base for transformation
            for (var pointIndex in pointIndices)
            {
                const originalPoint = originalControlPoints[pointIndex];
                const transformedPoint = worldTransform * originalPoint;
                modifiedControlPoints[pointIndex] = transformedPoint;
            }
        }
    }
    
    lattice.controlPoints = modifiedControlPoints;
    return lattice;
}


/**
 * Computes an axis-aligned bounding box from a flat array of control points
 * 
 * @param controlPoints {array} : Flat array of control points (Vector3 with units)
 * @returns {map} : Map containing minCorner (Vector3) and maxCorner (Vector3)
 */
function computeUnifiedBoundingBox(controlPoints is array) returns map
{
    if (size(controlPoints) == 0)
    {
        throw "Cannot compute bounding box from empty control points array";
    }
    
    var minX = controlPoints[0][0];
    var maxX = controlPoints[0][0];
    var minY = controlPoints[0][1];
    var maxY = controlPoints[0][1];
    var minZ = controlPoints[0][2];
    var maxZ = controlPoints[0][2];
    
    for (var pointIndex = 0; pointIndex < size(controlPoints); pointIndex += 1)
    {
        const point = controlPoints[pointIndex];
        minX = min(minX, point[0]);
        maxX = max(maxX, point[0]);
        minY = min(minY, point[1]);
        maxY = max(maxY, point[1]);
        minZ = min(minZ, point[2]);
        maxZ = max(maxZ, point[2]);
    }
    
    return {
        "minCorner" : vector(minX, minY, minZ),
        "maxCorner" : vector(maxX, maxY, maxZ)
    };
}


/**
 * Converts a 3D point in world space to parametric (S, T, U) coordinates in the lattice space
 * 
 * @param worldPoint {Vector3} : Point in world coordinates (with units)
 * @param lattice {map} : Lattice structure containing axes and origin
 * @returns {Vector3} : Parametric coordinates (S, T, U), each in range [0, 1] (unitless)
 */
function convertWorldToSTU(worldPoint is Vector, lattice is map) returns Vector
{
    const minToWorld = worldPoint - lattice.minCorner;
    
    const crossS = lattice.crossS;
    const crossT = lattice.crossT;
    const crossU = lattice.crossU;
    
    const epsilon = 1e-10 * meter * meter * meter;
    
    const numeratorS = dot(crossS, minToWorld);
    var denominatorS = dot(crossS, lattice.axisS);
    if (abs(denominatorS) < epsilon)
        denominatorS = epsilon;
    const paramS = numeratorS / denominatorS;
    
    const numeratorT = dot(crossT, minToWorld);
    var denominatorT = dot(crossT, lattice.axisT);
    if (abs(denominatorT) < epsilon)
        denominatorT = epsilon;
    const paramT = numeratorT / denominatorT;
    
    const numeratorU = dot(crossU, minToWorld);
    var denominatorU = dot(crossU, lattice.axisU);
    if (abs(denominatorU) < epsilon)
        denominatorU = epsilon;
    const paramU = numeratorU / denominatorU;
    
    return vector(paramS, paramT, paramU);
}


/**
 * Evaluates the trivariate Bernstein polynomial at parametric coordinates (s, t, u)
 * 
 * @param stuCoords {Vector3} : Parametric coordinates (s, t, u) in range [0, 1]
 * @param lattice {map} : Lattice structure with control points and dimensions
 * @returns {Vector3} : Evaluated point in world space (with units)
 */
function evaluateTrivariateBernstein(stuCoords is Vector, lattice is map) returns Vector
{
    const paramS = stuCoords[0];
    const paramT = stuCoords[1];
    const paramU = stuCoords[2];
    
    var evaluatedPoint = vector(0 * meter, 0 * meter, 0 * meter);
    
    for (var indexS = 0; indexS < lattice.controlPointCountS; indexS += 1)
    {
        var intermediatePointT = vector(0 * meter, 0 * meter, 0 * meter);
        
        for (var indexT = 0; indexT < lattice.controlPointCountT; indexT += 1)
        {
            var intermediatePointU = vector(0 * meter, 0 * meter, 0 * meter);
            
            for (var indexU = 0; indexU < lattice.controlPointCountU; indexU += 1)
            {
                const linearIndex = indexS * lattice.controlPointCountT * lattice.controlPointCountU + 
                                  indexT * lattice.controlPointCountU + indexU;
                const controlPoint = lattice.controlPoints[linearIndex];
                
                const basisU = bernsteinPolynomial(lattice.spanCountU, indexU, paramU);
                
                intermediatePointU = intermediatePointU + controlPoint * basisU;
            }
            
            const basisT = bernsteinPolynomial(lattice.spanCountT, indexT, paramT);
            
            intermediatePointT = intermediatePointT + intermediatePointU * basisT;
        }
        
        const basisS = bernsteinPolynomial(lattice.spanCountS, indexS, paramS);
        
        evaluatedPoint = evaluatedPoint + intermediatePointT * basisS;
    }
    
    return evaluatedPoint;
}


/**
 * Computes the Bernstein basis function B_{i,n}(u)
 * 
 * @param degree {number} : Degree of the polynomial (n)
 * @param index {number} : Index of the basis function (i)
 * @param parameter {number} : Parameter value u in range [0, 1]
 * @returns {number} : Value of the Bernstein basis function (unitless)
 */
function bernsteinPolynomial(degree is number, index is number, parameter is number) returns number
{
    const binomialCoefficient = factorial(degree) / (factorial(index) * factorial(degree - index));
    const termOneMinusU = (1 - parameter) ^ (degree - index);
    const termU = parameter ^ index;
    
    return binomialCoefficient * termOneMinusU * termU;
}


/**
 * Computes the factorial of a non-negative integer
 * 
 * @param n {number} : Non-negative integer
 * @returns {number} : n! = n * (n-1) * ... * 2 * 1
 */
function factorial(n is number) returns number
{
    if (n <= 0)
        return 1;
    
    var result = 1;
    for (var i = n; i > 1; i -= 1)
    {
        result = result * i;
    }
    
    return result;
}


/**
 * Prints detailed information about the plane-based FFD lattice
 * 
 * @param lattice {map} : Lattice structure
 * @param direction {FFDPlaneDirection} : The manipulation direction
 */
function printPlaneLatticeInformation(lattice is map, direction is FFDPlaneDirection)
{
    println("=== Plane-Based FFD Lattice Information ===");
    println("Manipulation direction: " ~ direction);
    println("Number of planes: " ~ lattice.planeCount);
    println("Points per plane: " ~ size(lattice.planeData[0].pointIndices));
    println("Bounding box:");
    println("  Min corner: " ~ lattice.minCorner);
    println("  Max corner: " ~ lattice.maxCorner);
    println("Span counts: [" ~ lattice.spanCountS ~ ", " ~ lattice.spanCountT ~ ", " ~ lattice.spanCountU ~ "]");
    println("Total control points: " ~ lattice.totalControlPoints);
    println("");
    println("USAGE: To deform the surface:");
    println("  1. Enable 'Edit planes' checkbox");
    println("  2. Click on a plane (shown at plane centers)");
    println("  3. Drag the triad manipulator to translate the plane");
    println("  4. All points on that plane will move together");
    println("===========================================");
}


/**
 * Visualizes the bounding box using debug geometry
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : Identifier for the debug geometry
 * @param boundingBox {map} : Map with minCorner and maxCorner
 */
function visualizeBoundingBox(context is Context, id is Id, boundingBox is map)
{
    const minCorner = boundingBox.minCorner;
    const maxCorner = boundingBox.maxCorner;
    
    const corners = [
        minCorner,
        vector(maxCorner[0], minCorner[1], minCorner[2]),
        vector(maxCorner[0], maxCorner[1], minCorner[2]),
        vector(minCorner[0], maxCorner[1], minCorner[2]),
        vector(minCorner[0], minCorner[1], maxCorner[2]),
        vector(maxCorner[0], minCorner[1], maxCorner[2]),
        maxCorner,
        vector(minCorner[0], maxCorner[1], maxCorner[2])
    ];
    
    for (var corner in corners)
    {
        debug(context, corner, DebugColor.BLUE);
    }
}


/**
 * Visualizes the FFD lattice control points using debug geometry
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : Identifier for the debug geometry
 * @param lattice {map} : Lattice structure with control points
 */
function visualizeLatticeControlPoints(context is Context, id is Id, lattice is map)
{
    for (var pointIndex = 0; pointIndex < lattice.totalControlPoints; pointIndex += 1)
    {
        debug(context, lattice.controlPoints[pointIndex], DebugColor.RED);
    }
}


/**
 * Visualizes the planes using debug geometry
 * 
 * Shows actual plane definitions with origin and normal for each plane in the lattice.
 * Uses proper Plane debug visualization to show plane orientation and position.
 * Applies transforms to show rotated planes correctly.
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : Identifier for the debug geometry
 * @param lattice {map} : Lattice structure with plane data
 * @param direction {FFDPlaneDirection} : The manipulation direction
 * @param planeTransformations {array} : Array of plane transformations
 */
function visualizePlanes(context is Context, id is Id, lattice is map, direction is FFDPlaneDirection, planeTransformations is array)
{
    for (var planeIndex = 0; planeIndex < size(lattice.planeData); planeIndex += 1)
    {
        const planeInfo = lattice.planeData[planeIndex];
        const pointIndices = planeInfo.pointIndices;
        
        // Calculate plane center from the current (possibly transformed) control points
        var planeCenter = vector(0 * meter, 0 * meter, 0 * meter);
        for (var pointIndex in pointIndices)
        {
            planeCenter = planeCenter + lattice.controlPoints[pointIndex];
        }
        planeCenter = planeCenter / size(pointIndices);
        
        // Determine initial plane normal based on manipulation direction
        var planeNormal = vector(0, 0, 1);
        var planeX = vector(1, 0, 0);
        if (direction == FFDPlaneDirection.S_DIRECTION)
        {
            planeNormal = vector(1, 0, 0);
            planeX = vector(0, 1, 0);
        }
        else if (direction == FFDPlaneDirection.T_DIRECTION)
        {
            planeNormal = vector(0, 1, 0);
            planeX = vector(0, 0, 1);
        }
        else // U_DIRECTION
        {
            planeNormal = vector(0, 0, 1);
            planeX = vector(1, 0, 0);
        }
        
        // Apply rotation from transform if it exists
        const transformData = findTransformForPlane(planeTransformations, planeIndex);
        if (transformData.rotationMatrix != undefined && size(transformData.rotationMatrix) == 9)
        {
            const rotationMatrix = transformData.rotationMatrix;
            const matrix3x3 = matrix([
                [rotationMatrix[0], rotationMatrix[1], rotationMatrix[2]],
                [rotationMatrix[3], rotationMatrix[4], rotationMatrix[5]],
                [rotationMatrix[6], rotationMatrix[7], rotationMatrix[8]]
            ]);
            
            // Apply inverse rotation to plane normal to show the rotated plane
            const inverseMatrix = inverse(matrix3x3);
            planeNormal = inverseMatrix * planeNormal;
            planeX = inverseMatrix * planeX;
        }
        
        // Create and debug the plane using proper Plane type with transformed normal
        const planeGeometry = plane(planeCenter, planeNormal, planeX);
        debug(context, planeGeometry, DebugColor.GREEN);
    }
}


/**
 * Manipulator handler for plane-based FFD feature
 * 
 * Handles plane selection and transformation manipulators.
 * 
 * @param context {Context} : The modeling context
 * @param definition {map} : Current feature definition
 * @param newManipulators {map} : Map of manipulator changes from user interaction
 * @returns {map} : Updated definition with manipulator-driven changes
 */
export function ffdPlaneManipulator(context is Context, definition is map, newManipulators is map) returns map
{
    // Handle plane selection
    if (newManipulators[PLANE_SELECTION_MANIPULATOR] is map)
    {
        definition.editPlanes = true;
        definition.selectedPlaneIndex = newManipulators[PLANE_SELECTION_MANIPULATOR].index;
    }
    
    // Handle plane transformation from fullTriadManipulator
    if (newManipulators[PLANE_TRIAD_MANIPULATOR] is map)
    {
        const manipulator = newManipulators[PLANE_TRIAD_MANIPULATOR];
        const planeTransform = manipulator.transform;
        
        // Decompose the transform into components that can be stored in precondition
        // Convert the 3x3 rotation matrix to a flat array of 9 values
        const linearMatrix = planeTransform.linear;
        const rotationMatrix = [
            linearMatrix[0][0], linearMatrix[0][1], linearMatrix[0][2],
            linearMatrix[1][0], linearMatrix[1][1], linearMatrix[1][2],
            linearMatrix[2][0], linearMatrix[2][1], linearMatrix[2][2]
        ];
        
        const translation = planeTransform.translation;
        
        var foundTransform = false;
        
        // Check if a transformation entry already exists for the selected plane
        for (var transformIndex = 0; transformIndex < size(definition.planeTransformations); transformIndex += 1)
        {
            if (definition.planeTransformations[transformIndex].index == definition.selectedPlaneIndex)
            {
                // Update existing transformation entry
                definition.planeTransformations[transformIndex].rotationMatrix = rotationMatrix;
                definition.planeTransformations[transformIndex].translateX = translation[0];
                definition.planeTransformations[transformIndex].translateY = translation[1];
                definition.planeTransformations[transformIndex].translateZ = translation[2];
                foundTransform = true;
                break;
            }
        }
        
        // If no transformation entry exists for this plane, create one
        if (!foundTransform)
        {
            var newTransform = {
                "index" : definition.selectedPlaneIndex,
                "rotationMatrix" : rotationMatrix,
                "translateX" : translation[0],
                "translateY" : translation[1],
                "translateZ" : translation[2]
            };
            definition.planeTransformations = append(definition.planeTransformations, newTransform);
        }
    }
    
    return definition;
}


/**
 * Finds the transformation data for a given plane index
 * 
 * @param planeTransformations {array} : Array of transformation entries
 * @param planeIndex {number} : Index of the plane to find transformation for
 * @returns {map} : Transform components (rotationMatrix, translateX/Y/Z), returns identity if not found
 */
function findTransformForPlane(planeTransformations is array, planeIndex is number) returns map
{
    for (var transformEntry in planeTransformations)
    {
        if (transformEntry.index == planeIndex)
        {
            return {
                "rotationMatrix" : transformEntry.rotationMatrix,
                "translateX" : transformEntry.translateX,
                "translateY" : transformEntry.translateY,
                "translateZ" : transformEntry.translateZ
            };
        }
    }
    // Return identity transformation if no transformation exists for this plane
    return {
        "rotationMatrix" : undefined,
        "translateX" : 0 * meter,
        "translateY" : 0 * meter,
        "translateZ" : 0 * meter
    };
}


/**
 * Adds manipulators for interactive plane manipulation
 * 
 * Creates manipulators for selecting and transforming planes.
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : Feature identifier for manipulator registration
 * @param originalLattice {map} : Original lattice structure (before transformations)
 * @param lattice {map} : Current lattice structure (with transformations applied)
 * @param selectedIndex {number} : Currently selected plane index
 * @param editPlanes {boolean} : Whether plane editing is enabled
 * @param planeTransformations {array} : Array of transformations
 * @param direction {FFDPlaneDirection} : The manipulation direction
 */
function addPlaneManipulators(context is Context, id is Id, originalLattice is map, lattice is map,
                             selectedIndex is number, editPlanes is boolean, planeTransformations is array,
                             direction is FFDPlaneDirection)
{
    // Calculate plane centers for selection manipulator
    var planeCenters = [];
    for (var planeInfo in lattice.planeData)
    {
        const pointIndices = planeInfo.pointIndices;
        var planeCenter = vector(0 * meter, 0 * meter, 0 * meter);
        for (var pointIndex in pointIndices)
        {
            planeCenter = planeCenter + lattice.controlPoints[pointIndex];
        }
        planeCenter = planeCenter / size(pointIndices);
        planeCenters = append(planeCenters, planeCenter);
    }
    
    // Create points manipulator for plane selection
    const pointsManip = pointsManipulator({
        "points" : planeCenters,
        "index" : editPlanes ? selectedIndex : -1
    });
    
    addManipulators(context, id, {
        (PLANE_SELECTION_MANIPULATOR) : pointsManip
    });
    
    // If editing is enabled and a valid plane is selected, show triad manipulator
    if (editPlanes && selectedIndex >= 0 && selectedIndex < originalLattice.planeCount)
    {
        // Calculate original plane center
        const planeInfo = originalLattice.planeData[selectedIndex];
        const pointIndices = planeInfo.pointIndices;
        var originalPlaneCenter = vector(0 * meter, 0 * meter, 0 * meter);
        for (var pointIndex in pointIndices)
        {
            originalPlaneCenter = originalPlaneCenter + originalLattice.controlPoints[pointIndex];
        }
        originalPlaneCenter = originalPlaneCenter / size(pointIndices);
        
        // Determine plane orientation based on manipulation direction
        var planeNormal = vector(0, 0, 1);
        var planeX = vector(1, 0, 0);
        if (direction == FFDPlaneDirection.S_DIRECTION)
        {
            planeNormal = vector(1, 0, 0);
            planeX = vector(0, 1, 0);
        }
        else if (direction == FFDPlaneDirection.T_DIRECTION)
        {
            planeNormal = vector(0, 1, 0);
            planeX = vector(0, 0, 1);
        }
        else // U_DIRECTION
        {
            planeNormal = vector(0, 0, 1);
            planeX = vector(1, 0, 0);
        }
        
        // Create coordinate system at the plane center
        const planeCoordSys = coordSystem(originalPlaneCenter, planeX, planeNormal);
        
        // Find the current transformation for the selected plane
        const planeTransformData = findTransformForPlane(planeTransformations, selectedIndex);
        var currentTransform = identityTransform();
        if (planeTransformData.rotationMatrix != undefined)
        {
            const rotationMatrix = planeTransformData.rotationMatrix;
            if (size(rotationMatrix) == 9)
            {
                // Convert flat array to Matrix
                const matrix3x3 = matrix([
                    [rotationMatrix[0], rotationMatrix[1], rotationMatrix[2]],
                    [rotationMatrix[3], rotationMatrix[4], rotationMatrix[5]],
                    [rotationMatrix[6], rotationMatrix[7], rotationMatrix[8]]
                ]);
                const translation = vector(
                    planeTransformData.translateX,
                    planeTransformData.translateY,
                    planeTransformData.translateZ
                );
                currentTransform = transform(matrix3x3, translation);
            }
        }
        
        // Create full triad manipulator at the selected plane center
        const triadManip = fullTriadManipulator({
            "base" : planeCoordSys,
            "transform" : currentTransform,
            "displayEditView" : true
        });
        
        addManipulators(context, id, {
            (PLANE_TRIAD_MANIPULATOR) : triadManip
        });
    }
}
