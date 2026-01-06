FeatureScript 2837;

/**
 * Free-Form Deformation (FFD) Feature
 * 
 * This feature implements the FFD algorithm presented in Sederberg & Parry's 1986 paper,
 * "Free-Form Deformation of Solid Geometric Models". The algorithm allows deformation of
 * NURBS surfaces by manipulating control points of a 3D lattice that embeds the surface.
 * 
 * The FFD algorithm works as follows:
 * 1. Create a 3D lattice (control point grid) around the input surface based on its bounding box
 * 2. Map each control point of the surface to parametric (S, T, U) coordinates in the lattice space
 * 3. Evaluate the trivariate Bernstein polynomial using the (potentially modified) lattice control points
 * 4. The result gives deformed positions for the surface control points
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
 * 1. Select a surface face to deform
 * 2. Set the number of lattice spans in each direction (S, T, U)
 * 3. Enable lattice manipulation and specify a control point index to modify
 * 4. Set offset values (X, Y, Z) to move that control point
 * 5. The surface will deform according to the modified lattice
 * 
 * Tips:
 * - Start with fewer spans (e.g., 2x2x2) for global deformations
 * - Use more spans (e.g., 4x4x4 or higher) for localized deformations
 * - Use diagnostics to visualize the lattice and understand control point indexing
 * - Control point indices are linear: index = i * (countT * countU) + j * countU + k
 *   where i, j, k are indices in S, T, U directions respectively
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
        "Feature Type Description" : "Deform a NURBS surface using a 3D control point lattice. Drag control points to deform the surface interactively.",
        "UIHint" : "NO_PREVIEW_PROVIDED",
        "Manipulator Change Function" : "ffdManipulator" }
export const freeFormDeformation = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Surface to deform", 
                     "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO, 
                     "MaxNumberOfPicks" : 1 }
        definition.surfaceToDeform is Query;
        
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
        
        // Note: latticeOffsets is NOT declared in precondition to allow manipulator to manage it
        // without interference from FeatureScript's parameter system. It's managed entirely by
        // the manipulator change function (ffdManipulator), similar to how triadTransform works.
        
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
        if (evaluateQueryCount(context, definition.surfaceToDeform) == 0)
            throw regenError("Select a surface to deform.", ["surfaceToDeform"]);
        
        const inputFace = evaluateQuery(context, definition.surfaceToDeform)[0];
        
        // Apply FFD deformation
        applyFFDDeformation(context, id, inputFace, definition);
    }, {
        spanCountS : 2,
        spanCountT : 2,
        spanCountU : 2,
        selectedPointIndex : 0,
        // latticeOffsets intentionally NOT in defaults - managed by manipulator
        // to prevent reset to [] on every regeneration
        enableDiagnostics : false,
        showLatticeControlPoints : false,
        showBoundingBox : false,
        printLatticeInfo : false,
        printDeformationDetails : false
    });


/**
 * Applies FFD deformation to the input surface
 * 
 * This function orchestrates the FFD algorithm:
 * 1. Extract B-spline surface from the input face
 * 2. Compute the bounding box for the surface
 * 3. Build the FFD lattice based on the bounding box and span counts
 * 4. Apply any user-specified control point offsets
 * 5. Deform each surface control point using trivariate Bernstein evaluation
 * 6. Create the deformed surface
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : The feature identifier
 * @param inputFace {Query} : The face to deform
 * @param definition {map} : Feature definition containing lattice parameters and offsets
 */
function applyFFDDeformation(context is Context, id is Id, inputFace is Query, definition is map)
{
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
    
    const controlPoints = surfaceDefinition.controlPoints;
    const weights = surfaceDefinition.weights;
    // Note: isRational flag is available but weights preservation is automatic
    // since we only modify control point positions, not weights
    
    // Compute bounding box from surface control points
    const boundingBox = computeControlPointBoundingBox(controlPoints);
    
    if (definition.showBoundingBox)
    {
        visualizeBoundingBox(context, id + "bbox", boundingBox);
    }
    
    // Build FFD lattice
    const spanCounts = [definition.spanCountS, definition.spanCountT, definition.spanCountU];
    const lattice = buildFFDLattice(boundingBox, spanCounts);
    
    if (definition.printLatticeInfo)
    {
        printLatticeInformation(lattice);
    }
    
    // Apply stored lattice offsets from manipulator interactions
    if (definition.latticeOffsets != undefined)
    {
        applyLatticeOffsets(lattice, definition.latticeOffsets);
    }
    
    // Add manipulators for interactive control point manipulation
    addFFDManipulators(context, id, lattice, definition.selectedPointIndex);
    
    if (definition.showLatticeControlPoints)
    {
        visualizeLatticeControlPoints(context, id + "lattice", lattice);
    }
    
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
            
            // Debug output for first control point
            if (definition.printDeformationDetails && uIndex == 0 && vIndex == 0)
            {
                println("DEBUG: First control point deformation:");
                println("  Original: " ~ originalPoint);
                println("  STU coords: " ~ stuCoords);
                println("  Deformed: " ~ deformedPoint);
            }
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
    
    opCreateBSplineSurface(context, id, {
        "bSplineSurface" : deformedSurfaceDefinition
    });
}


/**
 * Computes an axis-aligned bounding box from an array of control points
 * 
 * @param controlPoints {array} : 2D array of control points (Vector3 with units)
 * @returns {map} : Map containing minCorner (Vector3) and maxCorner (Vector3)
 */
function computeControlPointBoundingBox(controlPoints is array) returns map
{
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
    println("  1. Enable 'lattice control point manipulation'");
    println("  2. Choose a control point index (0 to " ~ (lattice.totalControlPoints - 1) ~ ")");
    println("  3. Set non-zero offset values (e.g., 0.01m in X/Y/Z)");
    println("  4. The surface will deform based on the modified lattice");
    println("  Note: With all offsets at 0, surface is unchanged (expected behavior)");
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
 */
export function ffdManipulator(context is Context, definition is map, newManipulators is map) returns map
{
    // Initialize latticeOffsets if it doesn't exist
    if (definition.latticeOffsets == undefined)
    {
        definition.latticeOffsets = [];
    }
    
    if (newManipulators[LATTICE_POINTS_MANIPULATOR] is map)
    {
        const oldIndex = definition.selectedPointIndex;
        var newIndex = newManipulators[LATTICE_POINTS_MANIPULATOR].index;
        
        // Adjust index to account for the currently selected point not being in the array
        // (we skip it to avoid interference with the triad manipulator)
        if (newIndex >= oldIndex)
        {
            newIndex = newIndex + 1;
        }
        
        println("=== POINT CLICK ===");
        println("Old index: " ~ oldIndex);
        println("New index: " ~ newIndex);
        
        definition.selectedPointIndex = newIndex;
    }
    
    if (newManipulators[LATTICE_TRIAD_MANIPULATOR] is map)
    {
        const transform = newManipulators[LATTICE_TRIAD_MANIPULATOR].transform;
        const selectedIndex = definition.selectedPointIndex;
        
        println("=== TRIAD DRAG ===");
        println("Selected index: " ~ selectedIndex);
        println("Transform translation: " ~ transform.translation);
        println("Offsets array size before: " ~ size(definition.latticeOffsets));
        
        // Find the array index for this control point index
        var arrayIndex = -1;
        for (var i = 0; i < size(definition.latticeOffsets); i += 1)
        {
            if (definition.latticeOffsets[i].index == selectedIndex)
            {
                arrayIndex = i;
                break;
            }
        }
        
        println("Array index found: " ~ arrayIndex);
        
        // If offset entry exists, update it; otherwise create new one
        if (arrayIndex >= 0)
        {
            // Update existing offset entry
            // Note: Cannot directly modify array element fields in manipulator context
            // Must rebuild the entire array
            println("Updating existing offset at array index " ~ arrayIndex);
            
            // Build new element with updated values
            var updatedOffset = definition.latticeOffsets[arrayIndex];
            updatedOffset.offsetX = transform.translation[0];
            updatedOffset.offsetY = transform.translation[1];
            updatedOffset.offsetZ = transform.translation[2];
            
            // Rebuild entire array with updated element
            var newOffsets = [];
            for (var i = 0; i < size(definition.latticeOffsets); i += 1)
            {
                if (i == arrayIndex)
                    newOffsets = append(newOffsets, updatedOffset);
                else
                    newOffsets = append(newOffsets, definition.latticeOffsets[i]);
            }
            definition.latticeOffsets = newOffsets;
        }
        else
        {
            // Create new offset entry
            println("Creating new offset entry");
            const newOffset = {
                "index" : selectedIndex,
                "offsetX" : transform.translation[0],
                "offsetY" : transform.translation[1],
                "offsetZ" : transform.translation[2]
            };
            definition.latticeOffsets = append(definition.latticeOffsets, newOffset);
        }
        
        println("Offsets array size after: " ~ size(definition.latticeOffsets));
        
        // Debug: print the offset we just updated
        if (arrayIndex >= 0 && arrayIndex < size(definition.latticeOffsets))
        {
            println("Updated offset at array index " ~ arrayIndex ~ ": index=" ~ definition.latticeOffsets[arrayIndex].index ~ 
                    ", offsetX=" ~ definition.latticeOffsets[arrayIndex].offsetX ~
                    ", offsetY=" ~ definition.latticeOffsets[arrayIndex].offsetY ~
                    ", offsetZ=" ~ definition.latticeOffsets[arrayIndex].offsetZ);
        }
        else if (size(definition.latticeOffsets) > 0)
        {
            println("Last offset (new entry): index=" ~ definition.latticeOffsets[size(definition.latticeOffsets)-1].index ~ 
                    ", offsetX=" ~ definition.latticeOffsets[size(definition.latticeOffsets)-1].offsetX ~
                    ", offsetY=" ~ definition.latticeOffsets[size(definition.latticeOffsets)-1].offsetY ~
                    ", offsetZ=" ~ definition.latticeOffsets[size(definition.latticeOffsets)-1].offsetZ);
        }
    }
    
    return definition;
}


function applyLatticeOffsets(lattice is map, offsets is array)
{
    for (var offsetEntry in offsets)
    {
        const index = offsetEntry.index;
        // Reconstruct vector from individual X/Y/Z components (following Routing Curve pattern)
        const offset = vector(offsetEntry.offsetX, offsetEntry.offsetY, offsetEntry.offsetZ);
        if (index >= 0 && index < lattice.totalControlPoints)
        {
            lattice.controlPoints[index] = lattice.controlPoints[index] + offset;
        }
    }
}


function addFFDManipulators(context is Context, id is Id, lattice is map, selectedIndex is number)
{
    // Build map of all manipulators to add at once
    var manipulators = {};
    
    // Create array of lattice control point positions, excluding the selected one
    var pointPositions = [];
    for (var i = 0; i < lattice.totalControlPoints; i += 1)
    {
        if (i != selectedIndex)
        {
            pointPositions = append(pointPositions, lattice.controlPoints[i]);
        }
    }
    
    // Add points manipulator to show all lattice control points (except selected)
    // Pass -1 as index to indicate no point is selected in the manipulator itself
    const pointsManip = pointsManipulator({
        "points" : pointPositions,
        "index" : -1
    });
    manipulators[LATTICE_POINTS_MANIPULATOR] = pointsManip;
    
    // Add triad manipulator at the selected control point
    if (selectedIndex >= 0 && selectedIndex < lattice.totalControlPoints)
    {
        // The lattice already has offsets applied, so use the current position
        const selectedPoint = lattice.controlPoints[selectedIndex];
        const triadManip = fullTriadManipulator({
            "base" : coordSystem(selectedPoint, vector(1, 0, 0), vector(0, 1, 0)),
            "transform" : identityTransform(),
            "displayEditView" : true
        });
        manipulators[LATTICE_TRIAD_MANIPULATOR] = triadManip;
    }
    
    // Add all manipulators at once
    addManipulators(context, id, manipulators);
}
