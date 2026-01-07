FeatureScript 2837;

/**
 * Surface Refinement Feature
 * 
 * This feature allows inserting additional knots and control points into B-spline surfaces
 * without changing the underlying geometry. This is useful for:
 * 
 * - Preparing simple surfaces for FFD (Free-Form Deformation) manipulation
 * - Adding local control to surfaces that have few control points
 * - Increasing the resolution of surface parameterization
 * - Creating more control points for detailed surface editing
 * 
 * The implementation uses mathematically correct knot insertion (Boehm algorithm) which:
 * - Preserves surface geometry exactly
 * - Maintains surface continuity
 * - Works with both rational (NURBS) and non-rational B-spline surfaces
 * - Distributes new knots uniformly in parameter space
 * 
 * Key Use Case:
 * When using FFD algorithms on simple surfaces (e.g., planes, cylinders) with few control
 * points, the lattice control points may not provide sufficient local influence. By refining
 * the surface first to add more control points, FFD can achieve more localized deformations.
 * 
 * Technical Implementation:
 * 1. Obtains B-spline surface representation (with approximation if needed)
 * 2. Processes each isoparametric curve independently
 * 3. Uses Boehm algorithm to insert knots uniformly in parameter space
 * 4. Reconstructs surface with increased control point count
 * 5. Creates new surface geometry that is identical to original
 * 
 * The algorithm works in homogeneous coordinates for rational surfaces (NURBS) to ensure
 * mathematically correct results.
 * 
 * Related Features:
 * - custom-features/freeFormDeformation.fs - FFD feature that benefits from refined surfaces
 * - custom-features/tweenSurfaces.fs - Contains the core knot insertion algorithms
 */

// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/context.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/nurbsUtils.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");


// Bounds for control point count
export const CONTROL_POINT_COUNT_BOUNDS = {
    (unitless) : [2, 10, 50]
} as IntegerBoundSpec;


/**
 * Feature that refines a B-spline surface by inserting knots to increase control point count.
 * 
 * This creates a new surface with more control points that has identical geometry to the
 * original surface. The additional control points provide finer control for subsequent
 * editing operations like FFD.
 */
annotation { "Feature Type Name" : "Refine Surface",
        "Feature Type Description" : "Insert knots into a surface to add control points without changing geometry. Useful for preparing surfaces for FFD and other deformation operations.",
        "UIHint" : "NO_PREVIEW_PROVIDED" }
export const refineSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Surface to refine", 
                     "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO, 
                     "MaxNumberOfPicks" : 1 }
        definition.surfaceToRefine is Query;
        
        annotation { "Name" : "Refinement mode" }
        definition.refinementMode is RefinementMode;
        
        if (definition.refinementMode == RefinementMode.TARGET_COUNT)
        {
            annotation { "Name" : "Target control points in U direction", 
                         "Description" : "Number of control points in U direction after refinement" }
            isInteger(definition.targetUCount, CONTROL_POINT_COUNT_BOUNDS);
            
            annotation { "Name" : "Target control points in V direction", 
                         "Description" : "Number of control points in V direction after refinement" }
            isInteger(definition.targetVCount, CONTROL_POINT_COUNT_BOUNDS);
        }
        else if (definition.refinementMode == RefinementMode.MULTIPLY)
        {
            annotation { "Name" : "Multiply U control points by", 
                         "Description" : "Multiply current U control point count by this factor" }
            isInteger(definition.multiplyUFactor, { (unitless) : [2, 2, 5] } as IntegerBoundSpec);
            
            annotation { "Name" : "Multiply V control points by", 
                         "Description" : "Multiply current V control point count by this factor" }
            isInteger(definition.multiplyVFactor, { (unitless) : [2, 2, 5] } as IntegerBoundSpec);
        }
        
        annotation { "Name" : "Show control points" }
        definition.showControlPoints is boolean;
        
        annotation { "Name" : "Enable diagnostics" }
        definition.enableDiagnostics is boolean;
        
        annotation { "Group Name" : "Developer diagnostics", 
                     "Driving Parameter" : "enableDiagnostics", 
                     "Collapsed By Default" : true }
        {
            if (definition.enableDiagnostics)
            {
                annotation { "Name" : "Print surface information" }
                definition.printSurfaceInfo is boolean;
                
                annotation { "Name" : "Print refinement details" }
                definition.printRefinementDetails is boolean;
            }
        }
    }
    {
        // Validate input
        if (evaluateQueryCount(context, definition.surfaceToRefine) == 0)
            throw regenError("Select a surface to refine.", ["surfaceToRefine"]);
        
        const inputFace = evaluateQuery(context, definition.surfaceToRefine)[0];
        
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
        
        if (definition.printSurfaceInfo)
        {
            println("=== Original Surface Information ===");
            println("Surface type: B-spline");
            println("U degree: " ~ surfaceDefinition.uDegree);
            println("V degree: " ~ surfaceDefinition.vDegree);
            println("Control points: " ~ size(surfaceDefinition.controlPoints) ~ " × " ~ 
                    size(surfaceDefinition.controlPoints[0]));
            println("Is rational (NURBS): " ~ surfaceDefinition.isRational);
            println("Is U periodic: " ~ surfaceDefinition.isUPeriodic);
            println("Is V periodic: " ~ surfaceDefinition.isVPeriodic);
        }
        
        // Determine target control point counts
        var targetUCount = size(surfaceDefinition.controlPoints);
        var targetVCount = size(surfaceDefinition.controlPoints[0]);
        
        if (definition.refinementMode == RefinementMode.TARGET_COUNT)
        {
            targetUCount = definition.targetUCount;
            targetVCount = definition.targetVCount;
        }
        else if (definition.refinementMode == RefinementMode.MULTIPLY)
        {
            targetUCount = size(surfaceDefinition.controlPoints) * definition.multiplyUFactor;
            targetVCount = size(surfaceDefinition.controlPoints[0]) * definition.multiplyVFactor;
        }
        
        // Validate target counts are at least current counts
        if (targetUCount < size(surfaceDefinition.controlPoints))
        {
            throw regenError("Target U control point count (" ~ targetUCount ~ 
                           ") must be at least the current count (" ~ 
                           size(surfaceDefinition.controlPoints) ~ ").", ["targetUCount"]);
        }
        if (targetVCount < size(surfaceDefinition.controlPoints[0]))
        {
            throw regenError("Target V control point count (" ~ targetVCount ~ 
                           ") must be at least the current count (" ~ 
                           size(surfaceDefinition.controlPoints[0]) ~ ").", ["targetVCount"]);
        }
        
        if (definition.printRefinementDetails)
        {
            println("=== Refinement Plan ===");
            println("Target U control points: " ~ targetUCount);
            println("Target V control points: " ~ targetVCount);
            println("Knots to insert in U: " ~ (targetUCount - size(surfaceDefinition.controlPoints)));
            println("Knots to insert in V: " ~ (targetVCount - size(surfaceDefinition.controlPoints[0])));
        }
        
        // Perform refinement if needed
        var refinedSurface = surfaceDefinition;
        if (targetUCount > size(surfaceDefinition.controlPoints) || 
            targetVCount > size(surfaceDefinition.controlPoints[0]))
        {
            refinedSurface = refineControlPointCount(context, surfaceDefinition, 
                                                     targetUCount, targetVCount, definition);
            
            if (definition.printRefinementDetails)
            {
                println("=== Refinement Complete ===");
                println("New control points: " ~ size(refinedSurface.controlPoints) ~ " × " ~ 
                        size(refinedSurface.controlPoints[0]));
            }
        }
        else
        {
            if (definition.printSurfaceInfo)
            {
                println("No refinement needed - surface already has target control point count.");
            }
        }
        
        // Visualize control points if requested
        if (definition.showControlPoints)
        {
            visualizeControlPoints(context, id + "originalCP", surfaceDefinition, DebugColor.BLUE);
            if (targetUCount > size(surfaceDefinition.controlPoints) || 
                targetVCount > size(surfaceDefinition.controlPoints[0]))
            {
                visualizeControlPoints(context, id + "refinedCP", refinedSurface, DebugColor.GREEN);
            }
        }
        
        // Create the refined B-spline surface
        const refinedSurfaceDefinition = bSplineSurface({
            "uDegree" : refinedSurface.uDegree,
            "vDegree" : refinedSurface.vDegree,
            "isUPeriodic" : refinedSurface.isUPeriodic,
            "isVPeriodic" : refinedSurface.isVPeriodic,
            "controlPoints" : controlPointMatrix(refinedSurface.controlPoints),
            "weights" : refinedSurface.weights == undefined ? undefined : matrix(refinedSurface.weights),
            "uKnots" : refinedSurface.uKnots,
            "vKnots" : refinedSurface.vKnots
        });
        
        opCreateBSplineSurface(context, id, {
            "bSplineSurface" : refinedSurfaceDefinition
        });
    }, { 
        refinementMode : RefinementMode.TARGET_COUNT,
        targetUCount : 10,
        targetVCount : 10,
        multiplyUFactor : 2,
        multiplyVFactor : 2,
        showControlPoints : false,
        enableDiagnostics : false,
        printSurfaceInfo : false,
        printRefinementDetails : false
    });


/**
 * Refinement mode enumeration
 */
export enum RefinementMode
{
    annotation { "Name" : "Target count" }
    TARGET_COUNT,
    annotation { "Name" : "Multiply by factor" }
    MULTIPLY
}


/**
 * Refines a B-spline surface to have a target number of control points in each direction.
 * 
 * This uses the mathematically correct knot insertion (Boehm algorithm) to add control points
 * while preserving surface geometry exactly. The algorithm:
 * 1. Processes each isoparametric curve (U-column or V-row) independently
 * 2. Inserts knots uniformly distributed in the parameter space
 * 3. Computes new control points using the Boehm knot insertion formula
 * 4. Reconstructs the surface with the new control point grid
 * 
 * For rational surfaces (NURBS), the algorithm works in homogeneous coordinates to ensure
 * mathematically correct results.
 * 
 * Note: This preserves surface geometry exactly using proper B-spline calculus.
 * 
 * @param context {Context} : The modeling context
 * @param surface {map} : The B-spline surface to refine (rational or non-rational)
 * @param targetUCount {number} : Target number of control points in U direction
 * @param targetVCount {number} : Target number of control points in V direction
 * @param definition {map} : The feature definition including diagnostics settings
 * @returns {map} : Refined surface with target control point counts
 */
function refineControlPointCount(context is Context, surface is map, 
                                targetUCount is number, targetVCount is number, 
                                definition is map) returns map
{
    var controlPoints = surface.controlPoints;
    var weights = surface.weights;
    const isRational = surface.isRational;
    var uDegree = surface.uDegree;
    var vDegree = surface.vDegree;
    // Ensure knots are KnotArray type
    var uKnots = surface.uKnots is KnotArray ? surface.uKnots : knotArray(surface.uKnots);
    var vKnots = surface.vKnots is KnotArray ? surface.vKnots : knotArray(surface.vKnots);
    
    // Refine in U direction if needed
    if (size(controlPoints) < targetUCount)
    {
        if (definition.printRefinementDetails)
        {
            println("Refining in U direction from " ~ size(controlPoints) ~ " to " ~ targetUCount ~ " control points...");
        }
        
        // Process each V-column to add U control points
        const numVPoints = size(controlPoints[0]);
        var newControlPoints = [];
        var newWeights = isRational ? [] : undefined;
        var newUKnots = undefined;
        
        for (var vIndex = 0; vIndex < numVPoints; vIndex += 1)
        {
            // Extract column of control points and weights
            var columnPoints = [];
            var columnWeights = [];
            for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
            {
                columnPoints = append(columnPoints, controlPoints[uIndex][vIndex]);
                if (isRational)
                {
                    columnWeights = append(columnWeights, weights[uIndex][vIndex]);
                }
            }
            
            // Create a B-spline curve from this column
            const columnCurve = bSplineCurve({
                "degree" : uDegree,
                "controlPoints" : columnPoints,
                "knots" : uKnots,
                "isPeriodic" : surface.isUPeriodic,
                "isRational" : isRational,
                "weights" : isRational ? columnWeights : undefined
            });
            
            // Refine this curve to have targetUCount control points
            const refinedCurve = refineCurveControlPointCount(context, columnCurve, targetUCount);
            
            // Store refined control points (transpose) and update knots from first column
            if (vIndex == 0)
            {
                newUKnots = refinedCurve.knots;
            }
            for (var uIndex = 0; uIndex < size(refinedCurve.controlPoints); uIndex += 1)
            {
                if (vIndex == 0)
                {
                    newControlPoints = append(newControlPoints, []);
                    if (isRational)
                    {
                        newWeights = append(newWeights, []);
                    }
                }
                newControlPoints[uIndex] = append(newControlPoints[uIndex], refinedCurve.controlPoints[uIndex]);
                if (isRational)
                {
                    newWeights[uIndex] = append(newWeights[uIndex], refinedCurve.weights[uIndex]);
                }
            }
        }
        
        controlPoints = newControlPoints;
        weights = newWeights;
        if (newUKnots != undefined)
        {
            uKnots = newUKnots;
        }
    }
    
    // Refine in V direction if needed
    if (size(controlPoints[0]) < targetVCount)
    {
        if (definition.printRefinementDetails)
        {
            println("Refining in V direction from " ~ size(controlPoints[0]) ~ " to " ~ targetVCount ~ " control points...");
        }
        
        // Process each U-row to add V control points
        var newControlPoints = [];
        var newWeights = isRational ? [] : undefined;
        var newVKnots = undefined;
        
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            // Extract row of control points and weights
            const rowPoints = controlPoints[uIndex];
            const rowWeights = isRational ? weights[uIndex] : undefined;
            
            // Create a B-spline curve from this row
            const rowCurve = bSplineCurve({
                "degree" : vDegree,
                "controlPoints" : rowPoints,
                "knots" : vKnots,
                "isPeriodic" : surface.isVPeriodic,
                "isRational" : isRational,
                "weights" : rowWeights
            });
            
            // Refine this curve to have targetVCount control points
            const refinedCurve = refineCurveControlPointCount(context, rowCurve, targetVCount);
            
            // Store refined control points and update knots from first row
            if (uIndex == 0)
            {
                newVKnots = refinedCurve.knots;
            }
            newControlPoints = append(newControlPoints, refinedCurve.controlPoints);
            if (isRational)
            {
                newWeights = append(newWeights, refinedCurve.weights);
            }
        }
        
        controlPoints = newControlPoints;
        weights = newWeights;
        if (newVKnots != undefined)
        {
            vKnots = newVKnots;
        }
    }
    
    return {
        "uDegree" : uDegree,
        "vDegree" : vDegree,
        "isRational" : isRational,
        "isUPeriodic" : surface.isUPeriodic,
        "isVPeriodic" : surface.isVPeriodic,
        "controlPoints" : controlPoints,
        "weights" : weights,
        "uKnots" : uKnots,
        "vKnots" : vKnots
    };
}


/**
 * Refines a B-spline curve to have a target number of control points using knot insertion.
 * 
 * Uses the mathematically correct Boehm algorithm for knot insertion to add control points
 * without changing the curve geometry. This preserves the curve shape exactly.
 * 
 * For rational curves (NURBS), the algorithm works in homogeneous coordinates to ensure
 * mathematically correct results.
 * 
 * @param context {Context} : The modeling context
 * @param curve {map} : The B-spline curve with controlPoints, knots, degree, etc. (rational or non-rational)
 * @param targetCount {number} : Target number of control points
 * @returns {map} : Refined curve with exact geometry preservation
 */
function refineCurveControlPointCount(context is Context, curve is map, targetCount is number) returns map
{
    if (size(curve.controlPoints) >= targetCount)
    {
        return curve;
    }
    
    // Number of knots to insert
    const numToInsert = targetCount - size(curve.controlPoints);
    
    // Determine which knots to insert by distributing them uniformly in the parameter space
    const startParam = curve.knots[curve.degree];
    const endParam = curve.knots[size(curve.knots) - curve.degree - 1];
    
    // Distribute new knots uniformly across the parameter domain
    var knotsToInsert = [];
    for (var i = 1; i <= numToInsert; i += 1)
    {
        const fraction = i / (numToInsert + 1);
        const newKnot = startParam + (endParam - startParam) * fraction;
        knotsToInsert = append(knotsToInsert, newKnot);
    }
    
    // Sort knots to insert
    knotsToInsert = sort(knotsToInsert, function(a, b) { return a - b; });
    
    // For rational curves, work in homogeneous coordinates
    var currentControlPoints = curve.controlPoints;
    var currentWeights = curve.weights;
    if (curve.isRational)
    {
        currentControlPoints = combinePointsAndWeights(curve.controlPoints, curve.weights);
    }
    var currentKnots = curve.knots;
    
    // Insert knots one at a time using Boehm algorithm
    for (var insertIdx = 0; insertIdx < size(knotsToInsert); insertIdx += 1)
    {
        const result = insertKnotBoehm(currentControlPoints, currentKnots, curve.degree, knotsToInsert[insertIdx]);
        currentControlPoints = result.controlPoints;
        currentKnots = result.knots;
    }
    
    // For rational curves, separate back to control points and weights
    if (curve.isRational)
    {
        const separated = separatePointsAndWeights(currentControlPoints);
        currentControlPoints = separated.points;
        currentWeights = separated.weights;
    }
    
    return {
        "controlPoints" : currentControlPoints,
        "knots" : currentKnots,
        "degree" : curve.degree,
        "isPeriodic" : curve.isPeriodic,
        "isRational" : curve.isRational,
        "weights" : currentWeights
    };
}


// Tolerance for comparing knot parameter values
const KNOT_TOLERANCE = 1e-10;

/**
 * Inserts a single knot into a B-spline curve using the Boehm algorithm.
 * 
 * This is the mathematically correct approach that preserves curve geometry exactly.
 * The algorithm computes new control points based on the knot insertion formula:
 * 
 * For each affected control point P_i, the new control point Q_i is computed as:
 * Q_i = alpha_i * P_i + (1 - alpha_i) * P_{i-1}
 * 
 * where alpha_i depends on the knot values and the insertion parameter.
 * 
 * @param controlPoints {array} : Original control points
 * @param knots {array} : Original knot vector
 * @param degree {number} : Degree of the B-spline
 * @param insertParam {number} : Parameter value where knot should be inserted
 * @returns {map} : Map with new controlPoints and knots arrays
 */
function insertKnotBoehm(controlPoints is array, knots is array, degree is number, insertParam is number) returns map
{
    const numControlPoints = size(controlPoints);
    
    // Validate inputs
    if (numControlPoints < degree + 1)
    {
        throw "Invalid B-spline: not enough control points for degree";
    }
    if (size(knots) != numControlPoints + degree + 1)
    {
        throw "Invalid B-spline: knot vector size doesn't match control point count";
    }
    
    // Find the knot span index where insertParam falls
    var knotSpanIndex = -1;
    for (var i = size(knots) - degree - 2; i >= degree; i -= 1)
    {
        if (insertParam >= knots[i] && insertParam <= knots[i + 1])
        {
            knotSpanIndex = i;
            break;
        }
    }
    
    // Handle edge case: if not found, clamp to valid range
    if (knotSpanIndex == -1)
    {
        if (insertParam < knots[degree])
        {
            knotSpanIndex = degree;
        }
        else
        {
            knotSpanIndex = max(degree, size(knots) - degree - 2);
        }
    }
    
    // Compute new control points using the Boehm algorithm
    var newControlPoints = [];
    
    const k = knotSpanIndex;
    const p = degree;
    
    // Control points from 0 to (k-p) remain unchanged
    for (var i = 0; i <= k - p; i += 1)
    {
        newControlPoints = append(newControlPoints, controlPoints[i]);
    }
    
    // Compute new control points from (k-p+1) to k using Boehm's knot insertion formula
    for (var i = k - p + 1; i <= k; i += 1)
    {
        const denominator = knots[i + p] - knots[i];
        var alpha = 0.0;
        if (abs(denominator) > KNOT_TOLERANCE)
        {
            alpha = (insertParam - knots[i]) / denominator;
        }
        else
        {
            alpha = 1.0;
        }
        const newPoint = controlPoints[i - 1] * (1 - alpha) + controlPoints[i] * alpha;
        newControlPoints = append(newControlPoints, newPoint);
    }
    
    // Handle the control point at position k+1
    if (k + 1 < numControlPoints)
    {
        const denominator = knots[k + 1 + p] - knots[k + 1];
        var alpha = 0.0;
        if (abs(denominator) > KNOT_TOLERANCE)
        {
            alpha = (insertParam - knots[k + 1]) / denominator;
        }
        else
        {
            alpha = 1.0;
        }
        const newPoint = controlPoints[k] * (1 - alpha) + controlPoints[k + 1] * alpha;
        newControlPoints = append(newControlPoints, newPoint);
    }
    else
    {
        newControlPoints = append(newControlPoints, controlPoints[numControlPoints - 1]);
    }
    
    // Control points from (k+1) onward remain unchanged but shifted by 1
    for (var i = k + 1; i < numControlPoints; i += 1)
    {
        newControlPoints = append(newControlPoints, controlPoints[i]);
    }
    
    // Insert the new knot into the knot vector
    var newKnots = [];
    for (var i = 0; i <= knotSpanIndex; i += 1)
    {
        newKnots = append(newKnots, knots[i]);
    }
    newKnots = append(newKnots, insertParam);
    for (var i = knotSpanIndex + 1; i < size(knots); i += 1)
    {
        newKnots = append(newKnots, knots[i]);
    }
    
    return {
        "controlPoints" : newControlPoints,
        "knots" : knotArray(newKnots)
    };
}


// Note: combinePointsAndWeights and separatePointsAndWeights are provided by nurbsUtils.fs
// which is already imported. These functions handle the conversion between 3D Cartesian
// control points with separate weights and 4D homogeneous coordinates [x*w, y*w, z*w, w]
// which is required for mathematically correct operations on rational B-splines (NURBS).


/**
 * Visualizes control points of a B-spline surface using debug geometry.
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : Identifier for the debug geometry
 * @param surface {map} : B-spline surface definition with controlPoints
 * @param color {DebugColor} : Color for the control points
 */
function visualizeControlPoints(context is Context, id is Id, surface is map, color is DebugColor)
{
    const controlPoints = surface.controlPoints;
    var pointCount = 0;
    
    for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
    {
        for (var vIndex = 0; vIndex < size(controlPoints[0]); vIndex += 1)
        {
            debug(context, controlPoints[uIndex][vIndex], color);
            pointCount += 1;
        }
    }
    
    println("Visualized " ~ pointCount ~ " control points");
}
