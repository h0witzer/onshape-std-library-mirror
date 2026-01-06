FeatureScript 2837;

/**
 * Free-Form Deformation (FFD) Feature
 * 
 * This feature implements the FFD algorithm presented in Sederberg & Parry's 1986 paper,
 * "Free-Form Deformation of Solid Geometric Models". The algorithm allows deformation of
 * NURBS surfaces by manipulating control points of a 3D lattice that embeds the surface(s).
 * 
 * The FFD algorithm works as follows:
 * 1. Create a 3D lattice (control point grid) around the input surface(s) based on their unified bounding box
 * 2. Map each control point of each surface to parametric (S, T, U) coordinates in the lattice space
 * 3. Evaluate the trivariate Bernstein polynomial using the (potentially modified) lattice control points
 * 4. The result gives deformed positions for all surface control points
 * 
 * Multiple Surface Support:
 * - Select multiple surfaces to deform them together using the same lattice
 * - All surfaces are embedded in a single unified lattice volume
 * - The bounding box is computed to encompass all selected surfaces
 * - Each surface is deformed independently but within the same lattice context
 * - This enables coherent deformation of multiple surfaces as a group
 * 
 * The lattice is parameterized with S, T, U coordinates (each ranging from 0 to 1):
 * - S: Corresponds to the X-axis direction of the bounding box
 * - T: Corresponds to the Y-axis direction of the bounding box
 * - U: Corresponds to the Z-axis direction of the bounding box
 * 
 * The number of spans in each direction determines the lattice resolution:
 * - More spans = finer control but more control points
 * - Fewer spans = coarser control but simpler manipulation
 * - Number of control points in direction = spans + 1
 * 
 * Mathematical basis:
 * The FFD volume is a trivariate tensor-product Bernstein polynomial:
 * X(s,t,u) = Σ Σ Σ B_i,l(s) * B_j,m(t) * B_k,n(u) * P_ijk
 * 
 * where:
 * - B_i,n(u) = C(n,i) * (1-u)^(n-i) * u^i is the Bernstein basis function
 * - C(n,i) = n! / (i! * (n-i)!) is the binomial coefficient
 * - P_ijk are the lattice control points
 * - l, m, n are the number of spans in S, T, U directions
 * 
 * Usage:
 * 1. Select one or more surface faces to deform
 * 2. Set the number of lattice spans in each direction (S, T, U)
 * 3. Enable "Edit lattice control points" to begin interactive manipulation
 * 4. Click on any lattice control point to select it (shown as small spheres)
 * 5. Drag the triad manipulator to move that control point
 * 6. All selected surfaces will deform according to the modified lattice
 * 
 * Tips:
 * - Start with fewer spans (e.g., 2x2x2) for global deformations
 * - Use more spans (e.g., 4x4x4 or higher) for localized deformations
 * - Use diagnostics to visualize the lattice and understand control point indexing
 * - Control point indices are linear: index = i * (countT * countU) + j * countU + k
 *   where i, j, k are indices in S, T, U directions respectively
 * - When deforming multiple surfaces, they all share the same lattice volume
 * 
 * Implementation references:
 * - JavaScript reference: non-featurescript-functions-reference/free-form-deformation-master/ffd.js
 * - Whitepaper: whitepaper-references/"Free-Form Deformation of Parametric CAD Geometry.pdf"
 * - Related utilities: custom-features/tweenSurfaces.fs
 */

// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/context.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
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


// Bounds for lattice span counts
export const FFD_SPAN_COUNT_BOUNDS = {
    (unitless) : [1, 2, 8]
} as IntegerBoundSpec;

// Manipulator identifiers
const LATTICE_POINTS_MANIPULATOR = "latticePointsManipulator";
const LATTICE_TRIAD_MANIPULATOR = "latticeTriadManipulator";


/**
 * Free-Form Deformation feature definition
 * 
 * Allows user to deform a NURBS surface by manipulating a 3D lattice of control points.
 * The surface is embedded in a trivariate Bernstein polynomial volume defined by the lattice.
 */
annotation { "Feature Type Name" : "Free-Form Deformation",
        "Feature Type Description" : "Deform one or more NURBS surfaces using a shared 3D control point lattice. Select multiple surfaces to deform them together in the same volume.",
        "UIHint" : "NO_PREVIEW_PROVIDED",
        "Manipulator Change Function" : "ffdManipulator" }
export const freeFormDeformation = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Surfaces to deform", 
                     "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO }
        definition.surfacesToDeform is Query;
        
        annotation { "Name" : "Lattice spans in S direction (X-axis)", 
                     "Description" : "Number of spans in the S direction of the FFD lattice" }
        isInteger(definition.spanCountS, FFD_SPAN_COUNT_BOUNDS);
        
        annotation { "Name" : "Lattice spans in T direction (Y-axis)", 
                     "Description" : "Number of spans in the T direction of the FFD lattice" }
        isInteger(definition.spanCountT, FFD_SPAN_COUNT_BOUNDS);
        
        annotation { "Name" : "Lattice spans in U direction (Z-axis)", 
                     "Description" : "Number of spans in the U direction of the FFD lattice" }
        isInteger(definition.spanCountU, FFD_SPAN_COUNT_BOUNDS);
        
        annotation { "Name" : "Selected control point index", "UIHint" : UIHint.ALWAYS_HIDDEN }
        isInteger(definition.selectedPointIndex, { (unitless) : [0, 0, 1000] } as IntegerBoundSpec);
        
        annotation { "Name" : "Edit lattice control points" }
        definition.editLatticePoints is boolean;
        
        annotation { "Group Name" : "Edit lattice control points", 
                     "Driving Parameter" : "editLatticePoints", 
                     "Collapsed By Default" : false }
        {
            if (definition.editLatticePoints)
            {
                annotation { "Name" : "Lattice point offsets", 
                             "Item name" : "point offset", 
                             "Item label template" : "#index: #x;#y;#z", 
                             "UIHint" : UIHint.PREVENT_ARRAY_REORDER }
                definition.latticePointOffsets is array;
                for (var latticePointOffset in definition.latticePointOffsets)
                {
                    annotation { "Name" : "Control point index" }
                    isInteger(latticePointOffset.index, { (unitless) : [0, 0, 1000] } as IntegerBoundSpec);
                    
                    annotation { "Name" : "X offset" }
                    isLength(latticePointOffset.x, ZERO_DEFAULT_LENGTH_BOUNDS);
                    
                    annotation { "Name" : "Y offset" }
                    isLength(latticePointOffset.y, ZERO_DEFAULT_LENGTH_BOUNDS);
                    
                    annotation { "Name" : "Z offset" }
                    isLength(latticePointOffset.z, ZERO_DEFAULT_LENGTH_BOUNDS);
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
                annotation { "Name" : "Show lattice control points" }
                definition.showLatticeControlPoints is boolean;
                
                annotation { "Name" : "Show bounding box" }
                definition.showBoundingBox is boolean;
                
                annotation { "Name" : "Print lattice information" }
                definition.printLatticeInfo is boolean;
                
                annotation { "Name" : "Print deformation details" }
                definition.printDeformationDetails is boolean;
            }
        }
    }
    {
        // Validate input surface selection
        const surfaceCount = evaluateQueryCount(context, definition.surfacesToDeform);
        if (surfaceCount == 0)
            throw regenError("Select at least one surface to deform.", ["surfacesToDeform"]);
        
        const inputFaces = evaluateQuery(context, definition.surfacesToDeform);
        
        // Apply FFD deformation to all selected surfaces
        applyFFDDeformationToMultipleSurfaces(context, id, inputFaces, definition);
    }, {
        spanCountS : 2,
        spanCountT : 2,
        spanCountU : 2,
        editLatticePoints : false,
        selectedPointIndex : 0,
        latticePointOffsets : [],
        enableDiagnostics : false,
        showLatticeControlPoints : false,
        showBoundingBox : false,
        printLatticeInfo : false,
        printDeformationDetails : false
    });



/**
 * Applies FFD deformation to multiple input surfaces using a shared lattice
 * 
 * This function extends the FFD algorithm to work on multiple surfaces simultaneously:
 * 1. Collect B-spline surface representations for all input faces
 * 2. Compute a unified bounding box that encompasses all surfaces
 * 3. Build a single FFD lattice based on the unified bounding box
 * 4. Apply the same lattice deformation to each surface
 * 5. Create all deformed surfaces
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : The feature identifier
 * @param inputFaces {array} : Array of faces to deform
 * @param definition {map} : Feature definition containing lattice parameters and offsets
 */
function applyFFDDeformationToMultipleSurfaces(context is Context, id is Id, inputFaces is array, definition is map)
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
    
    // Step 2: Compute a unified bounding box that encompasses all surfaces
    const boundingBox = computeUnifiedBoundingBox(allControlPoints);
    
    if (definition.showBoundingBox)
    {
        visualizeBoundingBox(context, id + "bbox", boundingBox);
    }
    
    // Step 3: Build the FFD lattice based on the unified bounding box
    const spanCounts = [definition.spanCountS, definition.spanCountT, definition.spanCountU];
    var latticeOriginal = buildFFDLattice(boundingBox, spanCounts);
    
    // Build a modified lattice WITH offsets for visualization and deformation
    var lattice = buildFFDLattice(boundingBox, spanCounts);
    if (definition.editLatticePoints && size(definition.latticePointOffsets) > 0)
    {
        // Extract, modify, and reassign control points
        var modifiedControlPoints = lattice.controlPoints;
        for (var offsetEntry in definition.latticePointOffsets)
        {
            const pointIndex = offsetEntry.index;
            if (pointIndex >= 0 && pointIndex < lattice.totalControlPoints)
            {
                const offset = vector(offsetEntry.x, offsetEntry.y, offsetEntry.z);
                modifiedControlPoints[pointIndex] = modifiedControlPoints[pointIndex] + offset;
            }
        }
        lattice.controlPoints = modifiedControlPoints;
    }
    
    if (definition.printLatticeInfo)
    {
        printLatticeInformation(lattice);
    }
    
    // Build display points from the MODIFIED lattice (with offsets) for manipulator visualization
    var latticeControlPointsForDisplay = lattice.controlPoints;
    
    // Add manipulators for interactive control point manipulation
    addFFDManipulators(context, id, latticeOriginal, latticeControlPointsForDisplay, definition.selectedPointIndex, 
                      definition.editLatticePoints, definition.latticePointOffsets);
    
    if (definition.showLatticeControlPoints)
    {
        visualizeLatticeControlPoints(context, id + "lattice", lattice);
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
                
                // FFD operates on the actual 3D control point positions
                // Convert to STU parametric space
                const stuCoords = convertWorldToSTU(originalPoint, lattice);
                
                // Evaluate trivariate Bernstein polynomial to get deformed position
                const deformedPoint = evaluateTrivariateBernstein(stuCoords, lattice);
                
                deformedRow = append(deformedRow, deformedPoint);
                
                // Debug output for first control point of first surface
                if (definition.printDeformationDetails && surfaceIndex == 0 && uIndex == 0 && vIndex == 0)
                {
                    println("DEBUG: First control point deformation (Surface " ~ surfaceIndex ~ "):");
                    println("  Original: " ~ originalPoint);
                    println("  STU coords: " ~ stuCoords);
                    println("  Deformed: " ~ deformedPoint);
                }
            }
            deformedControlPoints = append(deformedControlPoints, deformedRow);
        }
        
        // Step 5: Create the deformed B-spline surface
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
 * Computes an axis-aligned bounding box from an array of control points
 * 
 * @param controlPoints {array} : 2D array of control points (Vector3 with units)
 * @returns {map} : Map containing minCorner (Vector3) and maxCorner (Vector3)
 */
function computeControlPointBoundingBox(controlPoints is array) returns map
{
    // Validate that we have at least one control point
    if (size(controlPoints) == 0 || size(controlPoints[0]) == 0)
    {
        throw "Cannot compute bounding box from empty control points array";
    }
    
    // Initialize with first point
    var minX = controlPoints[0][0][0];
    var maxX = controlPoints[0][0][0];
    var minY = controlPoints[0][0][1];
    var maxY = controlPoints[0][0][1];
    var minZ = controlPoints[0][0][2];
    var maxZ = controlPoints[0][0][2];
    
    // Find min/max in each dimension
    for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
    {
        for (var vIndex = 0; vIndex < size(controlPoints[0]); vIndex += 1)
        {
            const point = controlPoints[uIndex][vIndex];
            minX = min(minX, point[0]);
            maxX = max(maxX, point[0]);
            minY = min(minY, point[1]);
            maxY = max(maxY, point[1]);
            minZ = min(minZ, point[2]);
            maxZ = max(maxZ, point[2]);
        }
    }
    
    return {
        "minCorner" : vector(minX, minY, minZ),
        "maxCorner" : vector(maxX, maxY, maxZ)
    };
}


/**
 * Computes an axis-aligned bounding box from a flat array of control points
 * 
 * This function is used for multiple surfaces where all control points are collected
 * into a single flat array to compute a unified bounding box.
 * 
 * @param controlPoints {array} : Flat array of control points (Vector3 with units)
 * @returns {map} : Map containing minCorner (Vector3) and maxCorner (Vector3)
 */
function computeUnifiedBoundingBox(controlPoints is array) returns map
{
    // Validate that we have at least one control point
    if (size(controlPoints) == 0)
    {
        throw "Cannot compute bounding box from empty control points array";
    }
    
    // Initialize with first point
    var minX = controlPoints[0][0];
    var maxX = controlPoints[0][0];
    var minY = controlPoints[0][1];
    var maxY = controlPoints[0][1];
    var minZ = controlPoints[0][2];
    var maxZ = controlPoints[0][2];
    
    // Find min/max in each dimension
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
 * Builds an FFD lattice structure from a bounding box and span counts
 * 
 * The lattice is a 3D grid of control points that initially match the
 * corners and edges of the bounding box. Control points are uniformly
 * distributed in each parametric direction.
 * 
 * @param boundingBox {map} : Map with minCorner and maxCorner (Vector3)
 * @param spanCounts {array} : Array of 3 integers [spanS, spanT, spanU]
 * @returns {map} : Lattice structure with control points, axes, span counts, etc.
 */
function buildFFDLattice(boundingBox is map, spanCounts is array) returns map
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
        // Pre-compute cross products for coordinate conversion (performance optimization)
        "crossS" : cross(axisT, axisU),  // T × U for S parameter
        "crossT" : cross(axisS, axisU),  // S × U for T parameter
        "crossU" : cross(axisS, axisT)   // S × T for U parameter
    };
}


/**
 * Converts a 3D point in world space to parametric (S, T, U) coordinates in the lattice space
 * 
 * The conversion uses the lattice's local coordinate system defined by its three axes.
 * Each parametric coordinate ranges from 0 to 1 across the lattice.
 * 
 * The transformation is:
 * For each axis i: stu[i] = (axisJ cross axisK) dot (point - minCorner) / (axisJ cross axisK) dot axisI
 * where J and K are the other two axes (cyclic permutation)
 * 
 * @param worldPoint {Vector3} : Point in world coordinates (with units)
 * @param lattice {map} : Lattice structure containing axes and origin
 * @returns {Vector3} : Parametric coordinates (S, T, U), each in range [0, 1] (unitless)
 */
function convertWorldToSTU(worldPoint is Vector, lattice is map) returns Vector
{
    // Vector from minimum corner to the world point
    const minToWorld = worldPoint - lattice.minCorner;
    
    // Use pre-computed cross products from lattice structure (for performance)
    // Cross products were computed during lattice construction
    const crossS = lattice.crossS;
    const crossT = lattice.crossT;
    const crossU = lattice.crossU;
    
    // Compute parametric coordinates using scalar triple product
    // Add small epsilon to avoid division by zero for degenerate cases
    // Epsilon must have units of volume (meter^3) to match the denominators
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
    
    // Return unitless parametric coordinates
    return vector(paramS, paramT, paramU);
}


/**
 * Evaluates the trivariate Bernstein polynomial at parametric coordinates (s, t, u)
 * 
 * This is the core of the FFD algorithm. The trivariate Bernstein polynomial is:
 * X(s,t,u) = Σ_i Σ_j Σ_k B_i,l(s) * B_j,m(t) * B_k,n(u) * P_ijk
 * 
 * where:
 * - B_i,n(u) is the Bernstein basis function
 * - P_ijk are the lattice control points
 * - l, m, n are the span counts in S, T, U directions
 * 
 * The evaluation is performed using nested loops following the tensor product structure.
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
    
    // Initialize result as zero vector (with proper units from first control point)
    var evaluatedPoint = vector(0 * meter, 0 * meter, 0 * meter);
    
    // Triple nested loop following the tensor product structure
    for (var indexS = 0; indexS < lattice.controlPointCountS; indexS += 1)
    {
        var intermediatePointT = vector(0 * meter, 0 * meter, 0 * meter);
        
        for (var indexT = 0; indexT < lattice.controlPointCountT; indexT += 1)
        {
            var intermediatePointU = vector(0 * meter, 0 * meter, 0 * meter);
            
            for (var indexU = 0; indexU < lattice.controlPointCountU; indexU += 1)
            {
                // Get control point at position (indexS, indexT, indexU)
                const linearIndex = getTernaryIndex(indexS, indexT, indexU, lattice);
                const controlPoint = lattice.controlPoints[linearIndex];
                
                // Compute Bernstein basis function for U direction
                const basisU = bernsteinPolynomial(lattice.spanCountU, indexU, paramU);
                
                // Accumulate contribution from this U control point
                intermediatePointU = intermediatePointU + controlPoint * basisU;
            }
            
            // Compute Bernstein basis function for T direction
            const basisT = bernsteinPolynomial(lattice.spanCountT, indexT, paramT);
            
            // Accumulate contribution from this T slice
            intermediatePointT = intermediatePointT + intermediatePointU * basisT;
        }
        
        // Compute Bernstein basis function for S direction
        const basisS = bernsteinPolynomial(lattice.spanCountS, indexS, paramS);
        
        // Accumulate contribution from this S slice
        evaluatedPoint = evaluatedPoint + intermediatePointT * basisS;
    }
    
    return evaluatedPoint;
}


/**
 * Computes the Bernstein basis function B_{i,n}(u)
 * 
 * The Bernstein polynomial is defined as:
 * B_{i,n}(u) = C(n,i) * (1-u)^(n-i) * u^i
 * 
 * where C(n,i) = n! / (i! * (n-i)!) is the binomial coefficient
 * 
 * @param degree {number} : Degree of the polynomial (n)
 * @param index {number} : Index of the basis function (i)
 * @param parameter {number} : Parameter value u in range [0, 1]
 * @returns {number} : Value of the Bernstein basis function (unitless)
 */
function bernsteinPolynomial(degree is number, index is number, parameter is number) returns number
{
    // Compute binomial coefficient C(n,i) = n! / (i! * (n-i)!)
    const binomialCoefficient = factorial(degree) / (factorial(index) * factorial(degree - index));
    
    // Compute (1-u)^(n-i)
    const termOneMinusU = (1 - parameter) ^ (degree - index);
    
    // Compute u^i
    const termU = parameter ^ index;
    
    return binomialCoefficient * termOneMinusU * termU;
}


/**
 * Computes the factorial of a non-negative integer
 * 
 * Note: For typical FFD lattices (1-8 spans), the degrees are small (≤8),
 * so factorial computation is not a performance bottleneck. For higher degrees,
 * consider implementing binomial coefficient caching or Pascal's triangle.
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
 * Converts ternary lattice indices (i, j, k) to a linear array index
 * 
 * The lattice is stored as a 1D array but conceptually organized as a 3D grid.
 * This function computes the linear index from 3D coordinates.
 * 
 * Storage order: U varies fastest, then T, then S
 * Linear index = i * (countT * countU) + j * countU + k
 * 
 * @param indexS {number} : Index in S direction
 * @param indexT {number} : Index in T direction
 * @param indexU {number} : Index in U direction
 * @param lattice {map} : Lattice structure with dimension information
 * @returns {number} : Linear index into the control points array
 */
function getTernaryIndex(indexS is number, indexT is number, indexU is number, lattice is map) returns number
{
    return indexS * lattice.controlPointCountT * lattice.controlPointCountU + 
           indexT * lattice.controlPointCountU + 
           indexU;
}


/**
 * Prints detailed information about the FFD lattice
 * 
 * @param lattice {map} : Lattice structure
 */
function printLatticeInformation(lattice is map)
{
    println("=== FFD Lattice Information ===");
    println("Bounding box:");
    println("  Min corner: " ~ lattice.minCorner);
    println("  Max corner: " ~ lattice.maxCorner);
    println("Axes:");
    println("  S axis: " ~ lattice.axisS);
    println("  T axis: " ~ lattice.axisT);
    println("  U axis: " ~ lattice.axisU);
    println("Span counts: [" ~ lattice.spanCountS ~ ", " ~ lattice.spanCountT ~ ", " ~ lattice.spanCountU ~ "]");
    println("Control point counts: [" ~ lattice.controlPointCountS ~ ", " ~ 
            lattice.controlPointCountT ~ ", " ~ lattice.controlPointCountU ~ "]");
    println("Total control points: " ~ lattice.totalControlPoints);
    println("");
    println("USAGE: To deform the surface:");
    println("  1. Enable 'Edit lattice control points' checkbox");
    println("  2. Click on any lattice control point (shown as small spheres)");
    println("  3. Drag the triad manipulator to move that control point");
    println("  4. The surface will deform based on the modified lattice");
    println("  Control point indices range from 0 to " ~ (lattice.totalControlPoints - 1));
    println("===============================");
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
    
    // Draw edges of the bounding box
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
    
    // Bottom face edges
    debug(context, corners[0], DebugColor.BLUE);
    debug(context, corners[1], DebugColor.BLUE);
    debug(context, corners[2], DebugColor.BLUE);
    debug(context, corners[3], DebugColor.BLUE);
    
    // Top face edges
    debug(context, corners[4], DebugColor.BLUE);
    debug(context, corners[5], DebugColor.BLUE);
    debug(context, corners[6], DebugColor.BLUE);
    debug(context, corners[7], DebugColor.BLUE);
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
    // Draw each control point as a debug point
    for (var i = 0; i < lattice.totalControlPoints; i += 1)
    {
        debug(context, lattice.controlPoints[i], DebugColor.RED);
    }
}



/**
 * Manipulator handler for FFD feature
 * 
 * Handles two types of manipulators:
 * 1. INDEX_MANIPULATOR (pointsManipulator): When a lattice control point is clicked, 
 *    enables editing and sets selectedPointIndex
 * 2. OFFSET_MANIPULATOR (triadManipulator): When the triad is dragged, updates or creates
 *    an offset entry in latticePointOffsets array
 * 
 * This follows the exact pattern used in Edit Curve feature.
 * 
 * @param context {Context} : The modeling context
 * @param definition {map} : Current feature definition
 * @param newManipulators {map} : Map of manipulator changes from user interaction
 * @returns {map} : Updated definition with manipulator-driven changes
 */
export function ffdManipulator(context is Context, definition is map, newManipulators is map) returns map
{
    // Handle point selection via pointsManipulator
    if (newManipulators[LATTICE_POINTS_MANIPULATOR] is map)
    {
        // User clicked on a lattice control point - enable editing and set the selected index
        definition.editLatticePoints = true;
        definition.selectedPointIndex = newManipulators[LATTICE_POINTS_MANIPULATOR].index;
    }
    
    // Handle triad manipulation for moving the selected control point
    if (newManipulators[LATTICE_TRIAD_MANIPULATOR] is map)
    {
        // Extract offset vector once for efficiency and readability
        const manipulatorOffset = newManipulators[LATTICE_TRIAD_MANIPULATOR].offset;
        
        var foundOffset = false;
        
        // Check if an offset entry already exists for the selected point
        for (var i = 0; i < size(definition.latticePointOffsets); i += 1)
        {
            if (definition.latticePointOffsets[i].index == definition.selectedPointIndex)
            {
                // Update existing offset entry
                definition.latticePointOffsets[i].x = manipulatorOffset[0];
                definition.latticePointOffsets[i].y = manipulatorOffset[1];
                definition.latticePointOffsets[i].z = manipulatorOffset[2];
                foundOffset = true;
                break;
            }
        }
        
        // If no offset entry exists for this point, create one
        if (!foundOffset)
        {
            var newOffset = {
                "index" : definition.selectedPointIndex,
                "x" : manipulatorOffset[0],
                "y" : manipulatorOffset[1],
                "z" : manipulatorOffset[2]
            };
            definition.latticePointOffsets = append(definition.latticePointOffsets, newOffset);
        }
    }
    
    return definition;
}


/**
 * Finds the offset vector for a given lattice point index
 * 
 * Searches the latticePointOffsets array for an entry matching the given index.
 * Returns the offset vector if found, or a zero vector if no offset exists.
 * 
 * @param latticePointOffsets {array} : Array of offset entries
 * @param pointIndex {number} : Index of the point to find offset for
 * @returns {Vector} : Offset vector with units, or zero vector if not found
 */
function findOffsetForPoint(latticePointOffsets is array, pointIndex is number) returns Vector
{
    for (var offsetEntry in latticePointOffsets)
    {
        if (offsetEntry.index == pointIndex)
        {
            return vector(offsetEntry.x, offsetEntry.y, offsetEntry.z);
        }
    }
    return vector(0 * meter, 0 * meter, 0 * meter);
}



/**
 * Adds manipulators for interactive FFD lattice control point manipulation
 * 
 * Creates two types of manipulators following the Edit Curve pattern:
 * 1. pointsManipulator: Shows selectable points at all lattice control point locations
 *    (displays current positions including offsets)
 * 2. triadManipulator: Shows a 3D triad at the selected point for dragging (if editLatticePoints is enabled)
 *    (base is original position, offset is user modification)
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : Feature identifier for manipulator registration
 * @param originalLattice {map} : Original lattice structure (before offsets) for base positions
 * @param displayPoints {array} : Lattice control points with offsets applied (for visualization)
 * @param selectedIndex {number} : Currently selected control point index
 * @param editLatticePoints {boolean} : Whether lattice point editing is enabled
 * @param latticePointOffsets {array} : Array of offsets to compute offset for triad
 */
function addFFDManipulators(context is Context, id is Id, originalLattice is map, displayPoints is array,
                           selectedIndex is number, editLatticePoints is boolean, latticePointOffsets is array)
{
    // Create points manipulator showing all lattice control points at their current positions
    // Users can click on any point to select it
    const pointsManip = pointsManipulator({
        "points" : displayPoints,
        "index" : editLatticePoints ? selectedIndex : -1
    });
    
    addManipulators(context, id, {
        (LATTICE_POINTS_MANIPULATOR) : pointsManip
    });
    
    // If editing is enabled and a valid point is selected, show triad manipulator
    if (editLatticePoints && selectedIndex >= 0 && selectedIndex < originalLattice.totalControlPoints)
    {
        // Find the current offset for the selected point using helper function
        const selectedPointOffset = findOffsetForPoint(latticePointOffsets, selectedIndex);
        
        // Base position is the ORIGINAL lattice control point position (before offset)
        const selectedPointBase = originalLattice.controlPoints[selectedIndex];
        
        // Create triad manipulator at the selected point
        const triadManip = triadManipulator({
            "base" : selectedPointBase,
            "offset" : selectedPointOffset
        });
        
        addManipulators(context, id, {
            (LATTICE_TRIAD_MANIPULATOR) : triadManip
        });
    }
}
