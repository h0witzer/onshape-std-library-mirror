FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/manipulator.fs", version : "2837.0");
import(path : "onshape/std/mathUtils.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/box.fs", version : "2837.0");
import(path : "onshape/std/string.fs", version : "2837.0");

/**
 * Free-Form Deformation (FFD) Feature
 * 
 * Implements the algorithm from Sederberg & Parry's 1986 paper:
 * "Free-Form Deformation of Solid Geometric Models"
 * 
 * This feature allows deformation of B-spline surfaces by manipulating
 * a 3D lattice of control points around the surface.
 */

// Constants for numerical stability and UI limits
const PARAMETER_SPACE_EPSILON = 1e-10; // Threshold for division by zero protection
const MAX_CONTROL_POINTS_FOR_FULL_DISPLAY = 27; // Maximum total control points to show all manipulators (3×3×3)

/**
 * Lattice data structure to store FFD control information
 * @type {{
 *      @field boundingBox {Box3d} : The bounding box of the undeformed lattice
 *      @field spanCounts {array} : Number of spans in S, T, U directions [sSpans, tSpans, uSpans]
 *      @field controlPointCounts {array} : Number of control points in each direction [s, t, u]
 *      @field controlPoints {array} : Flattened array of control point positions (3D vectors with units)
 *      @field axes {array} : The S, T, U axes as vectors with units [sAxis, tAxis, uAxis]
 * }}
 */
export type FFDLattice typecheck canBeFFDLattice;

/** Typecheck for FFDLattice */
export predicate canBeFFDLattice(value)
{
    value is map;
    value.boundingBox is Box3d;
    value.spanCounts is array;
    value.controlPointCounts is array;
    value.controlPoints is array;
    value.axes is array;
}

annotation { "Feature Type Name" : "Free-Form Deformation",
            "Manipulator Change Function" : "freeFormDeformationManipulatorChange" }
export const freeFormDeformation = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face or surface to deform",
                    "Filter" : EntityType.FACE && ConstructionObject.NO && SketchObject.NO && ModifiableEntityOnly.YES,
                    "MaxNumberOfPicks" : 1 }
        definition.targetFace is Query;

        annotation { "Name" : "S direction spans (length)" }
        isInteger(definition.sSpans, POSITIVE_COUNT_BOUNDS);

        annotation { "Name" : "T direction spans (width)" }
        isInteger(definition.tSpans, POSITIVE_COUNT_BOUNDS);

        annotation { "Name" : "U direction spans (height)" }
        isInteger(definition.uSpans, POSITIVE_COUNT_BOUNDS);
        
        annotation { "Name" : "Show control lattice",
                    "Default" : true }
        definition.showLattice is boolean;
        
        annotation { "Name" : "Control point adjustments",
                    "UIHint" : UIHint.ALWAYS_HIDDEN,
                    "Default" : {} }
        isAnything(definition.controlPointOffsets);
    }
    {
        // Verify that the target face exists
        const targetFaces = evaluateQuery(context, definition.targetFace);
        if (size(targetFaces) == 0)
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["targetFace"]);
        }
        
        const targetFace = targetFaces[0];
        
        // Get the bounding box of the target face
        const faceBoundingBox = evBox3d(context, {
            "topology" : targetFace,
            "tight" : true
        });
        
        // Create the FFD lattice
        var lattice = createFFDLattice(faceBoundingBox, 
                                       [definition.sSpans, definition.tSpans, definition.uSpans]);
        
        // Apply any control point offsets from manipulators
        applyControlPointOffsets(lattice, definition.controlPointOffsets);
        
        // Add manipulators for control points
        if (definition.showLattice)
        {
            addControlPointManipulators(context, id, lattice, definition);
            visualizeLattice(context, id, lattice);
        }
        
        // Perform the FFD deformation on the target face
        performFFDDeformation(context, id, definition, lattice, targetFace);
    });

/**
 * Manipulator change function for FFD
 * Updates control point offsets when manipulators are moved
 */
export function freeFormDeformationManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    // Update control point offsets based on manipulator changes
    for (var entry in newManipulators)
    {
        const manipulatorId = entry.key;
        const manipulator = entry.value;
        
        // Extract control point index from manipulator ID
        if (startsWith(manipulatorId, "controlPoint_"))
        {
            // Parse the index from the manipulator ID (format: "controlPoint_123")
            const underscoreIndex = indexOf(manipulatorId, "_");
            if (underscoreIndex != -1 && underscoreIndex < size(manipulatorId) - 1)
            {
                const indexString = substring(manipulatorId, underscoreIndex + 1, size(manipulatorId));
                try
                {
                    const controlPointIndex = stringToNumber(indexString);
                    
                    // Store the offset for this control point
                    definition.controlPointOffsets[controlPointIndex] = manipulator.offset;
                }
                catch
                {
                    // Invalid index format - skip this manipulator
                }
            }
        }
    }
    
    return definition;
}

/**
 * Creates an FFD lattice structure around the given bounding box
 * 
 * @param boundingBox : The 3D bounding box to create the lattice around
 * @param spanCounts : Array of [sSpans, tSpans, uSpans] integers
 * @returns FFDLattice structure
 */
function createFFDLattice(boundingBox is Box3d, spanCounts is array) returns FFDLattice
{
    const sSpans = spanCounts[0];
    const tSpans = spanCounts[1];
    const uSpans = spanCounts[2];
    
    const controlPointCounts = [sSpans + 1, tSpans + 1, uSpans + 1];
    const totalControlPoints = controlPointCounts[0] * controlPointCounts[1] * controlPointCounts[2];
    
    // Calculate the axes of the lattice (S, T, U directions)
    const sAxis = vector(boundingBox.maxCorner[0] - boundingBox.minCorner[0], 0 * meter, 0 * meter);
    const tAxis = vector(0 * meter, boundingBox.maxCorner[1] - boundingBox.minCorner[1], 0 * meter);
    const uAxis = vector(0 * meter, 0 * meter, boundingBox.maxCorner[2] - boundingBox.minCorner[2]);
    
    // Initialize control points in a regular grid
    var controlPoints = [];
    for (var i = 0; i < controlPointCounts[0]; i += 1)
    {
        for (var j = 0; j < controlPointCounts[1]; j += 1)
        {
            for (var k = 0; k < controlPointCounts[2]; k += 1)
            {
                const sParam = i / sSpans;
                const tParam = j / tSpans;
                const uParam = k / uSpans;
                
                const position = boundingBox.minCorner + 
                                sParam * sAxis +
                                tParam * tAxis +
                                uParam * uAxis;
                
                controlPoints = append(controlPoints, position);
            }
        }
    }
    
    return {
        "boundingBox" : boundingBox,
        "spanCounts" : spanCounts,
        "controlPointCounts" : controlPointCounts,
        "controlPoints" : controlPoints,
        "axes" : [sAxis, tAxis, uAxis]
    } as FFDLattice;
}

/**
 * Applies control point offsets from user manipulations
 * 
 * @param lattice : The FFD lattice to modify
 * @param offsets : Map of control point indices to offset vectors
 */
function applyControlPointOffsets(lattice is FFDLattice, offsets is map)
{
    // Apply any stored offsets to control points
    for (var entry in offsets)
    {
        const index = entry.key;
        const offset = entry.value;
        if (index < size(lattice.controlPoints))
        {
            lattice.controlPoints[index] = lattice.controlPoints[index] + offset;
        }
    }
}

/**
 * Adds triad manipulators for FFD control points
 * For performance, only adds manipulators for corner and edge control points
 * 
 * @param context : The context
 * @param id : Feature ID
 * @param lattice : The FFD lattice
 * @param definition : Feature definition
 */
function addControlPointManipulators(context is Context, id is Id, lattice is FFDLattice, definition is map)
{
    const sCount = lattice.controlPointCounts[0];
    const tCount = lattice.controlPointCounts[1];
    const uCount = lattice.controlPointCounts[2];
    
    var manipulators = {};
    
    // Add manipulators for a subset of control points to keep UI manageable
    // We'll add manipulators for corners and some edge midpoints
    const indices = getManipulatorIndices(sCount, tCount, uCount);
    
    for (var index in indices)
    {
        const controlPoint = lattice.controlPoints[index];
        const offset = definition.controlPointOffsets[index];
        const actualOffset = offset != undefined ? offset : vector(0, 0, 0) * meter;
        
        const manipulator = triadManipulator({
            "base" : controlPoint - actualOffset,
            "offset" : actualOffset,
            "primaryParameterId" : "controlPointOffsets"
        });
        
        manipulators["controlPoint_" ~ index] = manipulator;
    }
    
    addManipulators(context, id, manipulators);
}

/**
 * Returns indices of control points that should have manipulators
 * For large lattices, only show corner and select edge control points
 * 
 * @param sCount : Number of control points in S direction
 * @param tCount : Number of control points in T direction
 * @param uCount : Number of control points in U direction
 * @returns Array of control point indices
 */
function getManipulatorIndices(sCount is number, tCount is number, uCount is number) returns array
{
    var indices = [];
    
    // For small lattices (up to 3×3×3), show all control points
    const totalPoints = sCount * tCount * uCount;
    if (totalPoints <= MAX_CONTROL_POINTS_FOR_FULL_DISPLAY)
    {
        for (var i = 0; i < totalPoints; i += 1)
        {
            indices = append(indices, i);
        }
        return indices;
    }
    
    // For larger lattices, only show corner points and some strategic points
    // Corner points (8 corners of the lattice)
    const corners = [
        getControlPointIndex(0, 0, 0, tCount, uCount),
        getControlPointIndex(sCount - 1, 0, 0, tCount, uCount),
        getControlPointIndex(0, tCount - 1, 0, tCount, uCount),
        getControlPointIndex(sCount - 1, tCount - 1, 0, tCount, uCount),
        getControlPointIndex(0, 0, uCount - 1, tCount, uCount),
        getControlPointIndex(sCount - 1, 0, uCount - 1, tCount, uCount),
        getControlPointIndex(0, tCount - 1, uCount - 1, tCount, uCount),
        getControlPointIndex(sCount - 1, tCount - 1, uCount - 1, tCount, uCount)
    ];
    
    indices = indices + corners;
    
    // Add midpoints of edges if lattice is large enough
    if (sCount > 2)
    {
        const sMid = floor(sCount / 2);
        const tMid = floor(tCount / 2);
        const uMid = floor(uCount / 2);
        
        // Mid edges on bottom face
        indices = append(indices, getControlPointIndex(sMid, 0, 0, tCount, uCount));
        indices = append(indices, getControlPointIndex(sMid, tCount - 1, 0, tCount, uCount));
        indices = append(indices, getControlPointIndex(0, tMid, 0, tCount, uCount));
        indices = append(indices, getControlPointIndex(sCount - 1, tMid, 0, tCount, uCount));
        
        // Mid edges on top face
        indices = append(indices, getControlPointIndex(sMid, 0, uCount - 1, tCount, uCount));
        indices = append(indices, getControlPointIndex(sMid, tCount - 1, uCount - 1, tCount, uCount));
        indices = append(indices, getControlPointIndex(0, tMid, uCount - 1, tCount, uCount));
        indices = append(indices, getControlPointIndex(sCount - 1, tMid, uCount - 1, tCount, uCount));
        
        // Vertical mid edges
        indices = append(indices, getControlPointIndex(0, 0, uMid, tCount, uCount));
        indices = append(indices, getControlPointIndex(sCount - 1, 0, uMid, tCount, uCount));
        indices = append(indices, getControlPointIndex(0, tCount - 1, uMid, tCount, uCount));
        indices = append(indices, getControlPointIndex(sCount - 1, tCount - 1, uMid, tCount, uCount));
    }
    
    return indices;
}

/**
 * Converts 3D indices (i, j, k) to a flat array index
 * 
 * @param i : S direction index
 * @param j : T direction index
 * @param k : U direction index
 * @param tCount : Number of control points in T direction
 * @param uCount : Number of control points in U direction
 * @returns Flat array index
 */
function getControlPointIndex(i is number, j is number, k is number, tCount is number, uCount is number) returns number
{
    return i * tCount * uCount + j * uCount + k;
}

/**
 * Visualizes the FFD lattice as a wireframe by creating lines between control points
 * 
 * @param context : The context
 * @param id : Feature ID
 * @param lattice : The FFD lattice to visualize
 */
function visualizeLattice(context is Context, id is Id, lattice is FFDLattice)
{
    const sCount = lattice.controlPointCounts[0];
    const tCount = lattice.controlPointCounts[1];
    const uCount = lattice.controlPointCounts[2];
    
    // Create lines along S direction
    for (var j = 0; j < tCount; j += 1)
    {
        for (var k = 0; k < uCount; k += 1)
        {
            var linePoints = [];
            for (var i = 0; i < sCount; i += 1)
            {
                const index = getControlPointIndex(i, j, k, tCount, uCount);
                linePoints = append(linePoints, lattice.controlPoints[index]);
            }
            
            if (size(linePoints) >= 2)
            {
                opFitSpline(context, id + ("latticeS_" ~ j ~ "_" ~ k), {
                    "points" : linePoints
                });
            }
        }
    }
    
    // Create lines along T direction
    for (var i = 0; i < sCount; i += 1)
    {
        for (var k = 0; k < uCount; k += 1)
        {
            var linePoints = [];
            for (var j = 0; j < tCount; j += 1)
            {
                const index = getControlPointIndex(i, j, k, tCount, uCount);
                linePoints = append(linePoints, lattice.controlPoints[index]);
            }
            
            if (size(linePoints) >= 2)
            {
                opFitSpline(context, id + ("latticeT_" ~ i ~ "_" ~ k), {
                    "points" : linePoints
                });
            }
        }
    }
    
    // Create lines along U direction
    for (var i = 0; i < sCount; i += 1)
    {
        for (var j = 0; j < tCount; j += 1)
        {
            var linePoints = [];
            for (var k = 0; k < uCount; k += 1)
            {
                const index = getControlPointIndex(i, j, k, tCount, uCount);
                linePoints = append(linePoints, lattice.controlPoints[index]);
            }
            
            if (size(linePoints) >= 2)
            {
                opFitSpline(context, id + ("latticeU_" ~ i ~ "_" ~ j), {
                    "points" : linePoints
                });
            }
        }
    }
}

/**
 * Calculates factorial of n
 * 
 * Note: In FeatureScript, module-level caching is safe as each feature execution
 * gets its own context. The cache improves performance for repeated Bernstein
 * polynomial calculations without risking cross-execution contamination.
 * 
 * @param n : Non-negative integer
 * @returns n! (factorial of n)
 */
function factorial(n is number) returns number
{
    // Simple factorial calculation - values are typically small (n <= 8 for most lattices)
    // so caching provides minimal benefit and is removed for clarity and thread safety
    var result = 1;
    for (var i = n; i > 1; i -= 1)
    {
        result = result * i;
    }
    return result;
}

/**
 * Evaluates the Bernstein polynomial B(n,k,u)
 * 
 * The Bernstein polynomial is: B(n,k,u) = C(n,k) * (1-u)^(n-k) * u^k
 * where C(n,k) is the binomial coefficient n!/(k!(n-k)!)
 * 
 * @param n : Degree of the polynomial
 * @param k : Index (0 <= k <= n)
 * @param u : Parameter value (0 <= u <= 1)
 * @returns Value of the Bernstein polynomial
 */
function bernsteinPolynomial(n is number, k is number, u is number) returns number
{
    // Binomial coefficient: n! / (k! * (n-k)!)
    const binomialCoefficient = factorial(n) / (factorial(k) * factorial(n - k));
    
    // Bernstein polynomial: C(n,k) * (1-u)^(n-k) * u^k
    return binomialCoefficient * (1 - u)^(n - k) * u^k;
}

/**
 * Converts a world space point to parameter space (s, t, u) within the lattice
 * 
 * @param lattice : The FFD lattice
 * @param worldPoint : Point in world space (3D vector with units)
 * @returns Vector representing (s, t, u) parameters (unitless, each in range [0,1])
 */
function worldToParameterSpace(lattice is FFDLattice, worldPoint is Vector) returns Vector
{
    // Vector from minimum corner of bounding box to the world point
    const minToWorld = worldPoint - lattice.boundingBox.minCorner;
    
    // Calculate cross products for parameter space conversion
    const crossTU = cross(lattice.axes[1], lattice.axes[2]);
    const crossSU = cross(lattice.axes[0], lattice.axes[2]);
    const crossST = cross(lattice.axes[0], lattice.axes[1]);
    
    // Calculate parameters using the formula from the paper
    const sNumerator = dot(crossTU, minToWorld);
    const sDenominator = dot(crossTU, lattice.axes[0]);
    
    const tNumerator = dot(crossSU, minToWorld);
    const tDenominator = dot(crossSU, lattice.axes[1]);
    
    const uNumerator = dot(crossST, minToWorld);
    const uDenominator = dot(crossST, lattice.axes[2]);
    
    // Guard against division by zero (degenerate lattice)
    // Use epsilon threshold to handle near-zero denominators
    
    var sParameter = 0.0;
    if (abs(sDenominator) > PARAMETER_SPACE_EPSILON)
    {
        sParameter = sNumerator / sDenominator;
    }
    
    var tParameter = 0.0;
    if (abs(tDenominator) > PARAMETER_SPACE_EPSILON)
    {
        tParameter = tNumerator / tDenominator;
    }
    
    var uParameter = 0.0;
    if (abs(uDenominator) > PARAMETER_SPACE_EPSILON)
    {
        uParameter = uNumerator / uDenominator;
    }
    
    return vector(sParameter, tParameter, uParameter);
}

/**
 * Evaluates the FFD trivariate tensor product at parameter (s, t, u)
 * 
 * This implements the core FFD equation:
 * X(s,t,u) = Sum(i=0 to l) Sum(j=0 to m) Sum(k=0 to n) 
 *            B(l,i,s) * B(m,j,t) * B(n,k,u) * P(i,j,k)
 * 
 * @param lattice : The FFD lattice with control points
 * @param s : S parameter (0 to 1)
 * @param t : T parameter (0 to 1)
 * @param u : U parameter (0 to 1)
 * @returns Deformed point in world space (3D vector with units)
 */
function evaluateFFDTrivariate(lattice is FFDLattice, s is number, t is number, u is number) returns Vector
{
    const sSpans = lattice.spanCounts[0];
    const tSpans = lattice.spanCounts[1];
    const uSpans = lattice.spanCounts[2];
    
    const sCount = lattice.controlPointCounts[0];
    const tCount = lattice.controlPointCounts[1];
    const uCount = lattice.controlPointCounts[2];
    
    var evaluatedPoint = vector(0, 0, 0) * meter;
    
    // Triple sum over all control points
    for (var i = 0; i < sCount; i += 1)
    {
        const bernsteinS = bernsteinPolynomial(sSpans, i, s);
        
        for (var j = 0; j < tCount; j += 1)
        {
            const bernsteinT = bernsteinPolynomial(tSpans, j, t);
            
            for (var k = 0; k < uCount; k += 1)
            {
                const bernsteinU = bernsteinPolynomial(uSpans, k, u);
                
                // Get control point at (i, j, k) using the shared index function
                const controlPointIndex = getControlPointIndex(i, j, k, tCount, uCount);
                const controlPoint = lattice.controlPoints[controlPointIndex];
                
                // Add weighted contribution of this control point
                const weight = bernsteinS * bernsteinT * bernsteinU;
                evaluatedPoint = evaluatedPoint + weight * controlPoint;
            }
        }
    }
    
    return evaluatedPoint;
}

/**
 * Evaluates the FFD at a world space point
 * 
 * @param lattice : The FFD lattice
 * @param worldPoint : Point in world space
 * @returns Deformed point in world space
 */
function evaluateFFDAtWorldPoint(lattice is FFDLattice, worldPoint is Vector) returns Vector
{
    const parameterSpacePoint = worldToParameterSpace(lattice, worldPoint);
    return evaluateFFDTrivariate(lattice, parameterSpacePoint[0], parameterSpacePoint[1], parameterSpacePoint[2]);
}

/**
 * Performs the FFD deformation on the target face
 * 
 * @param context : The context
 * @param id : Feature ID
 * @param definition : Feature definition
 * @param lattice : The FFD lattice
 * @param targetFace : The face to deform
 */
function performFFDDeformation(context is Context, id is Id, definition is map, 
                               lattice is FFDLattice, targetFace is Query)
{
    // Get the B-spline approximation of the surface
    const surfaceApproximation = evApproximateBSplineSurface(context, {
        "face" : targetFace,
        "forceCubic" : false,
        "forceNonRational" : false
    });
    
    const originalSurface = surfaceApproximation.bSplineSurface;
    
    // Deform the control points of the B-spline surface using FFD
    var deformedControlPoints = [];
    
    for (var uRow in originalSurface.controlPoints)
    {
        var deformedRow = [];
        for (var controlPoint in uRow)
        {
            // Apply FFD transformation to each control point
            const deformedPoint = evaluateFFDAtWorldPoint(lattice, controlPoint);
            deformedRow = append(deformedRow, deformedPoint);
        }
        deformedControlPoints = append(deformedControlPoints, deformedRow);
    }
    
    // Create the deformed B-spline surface
    var deformedSurface = {
        "uDegree" : originalSurface.uDegree,
        "vDegree" : originalSurface.vDegree,
        "isRational" : originalSurface.isRational,
        "isUPeriodic" : originalSurface.isUPeriodic,
        "isVPeriodic" : originalSurface.isVPeriodic,
        "controlPoints" : deformedControlPoints,
        "uKnots" : originalSurface.uKnots,
        "vKnots" : originalSurface.vKnots
    };
    
    // Add weights if the surface is rational
    if (originalSurface.isRational)
    {
        deformedSurface.weights = originalSurface.weights;
    }
    
    deformedSurface = deformedSurface as BSplineSurface;
    
    // Create the deformed surface geometry
    opCreateBSplineSurface(context, id + "deformedSurface", {
        "bSplineSurface" : deformedSurface
    });
}
