FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "d206287863cb4400750c9aff", version : "36c04743534bad7be94aac56"); // kerfBendingUtils.fs

/**
 * Kerf bending feature that calculates cut positions along a curve.
 * Takes any sketch curve (Bezier, spline, arc, etc.) and generates kerf cut positions
 * for creating flexible bends in flat materials like plywood.
 */
annotation { "Feature Type Name" : "Kerf Bending" }
export const kerfBendingSample = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Curve", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
        definition.curveEdge is Query;
        
        annotation { "Name" : "Blade width" }
        isLength(definition.bladeWidth, BLEND_BOUNDS);
        
        annotation { "Name" : "Cut depth" }
        isLength(definition.cutDepth, BLEND_BOUNDS);
        
        annotation { "Name" : "Show debug info", "Default" : true }
        definition.showDebug is boolean;
        
        annotation { "Name" : "Advanced settings", "Default" : false }
        definition.showAdvanced is boolean;
        
        if (definition.showAdvanced)
        {
            annotation { "Name" : "Curve samples", "Description" : "Number of points to sample along the curve (higher = more accurate)" }
            isInteger(definition.curveSamples, POSITIVE_COUNT_BOUNDS);
            
            annotation { "Name" : "Search window", "Description" : "Size of the search window for finding cut points" }
            isInteger(definition.searchWindow, POSITIVE_COUNT_BOUNDS);
        }
    }
    {
        // Get control points from curve edge
        const controlPoints = extractBezierControlPoints(context, definition.curveEdge);
        
        // Use default values if advanced settings not shown
        const curveSamples = definition.showAdvanced ? definition.curveSamples : 600;
        const searchWindow = definition.showAdvanced ? definition.searchWindow : 80;
        
        // Generate the kerf bending solution
        const solution = generateKerfBendingSolution(
            controlPoints,
            definition.bladeWidth,
            definition.cutDepth,
            curveSamples,
            searchWindow
        );
        
        // Create summary for display
        const summary = createKerfBendingSummary(solution);
        
        // Display results in the console
        println("=== Kerf Bending Solution ===");
        println("Total workpiece length: " ~ solution.totalLength);
        println("Number of cuts: " ~ solution.numberOfCuts);
        println("Kerf angle (degrees): " ~ (solution.kerfAngle / degree));
        println("Average cut spacing: " ~ summary.averageCutSpacing);
        println("Minimum cut spacing: " ~ summary.minimumCutSpacing);
        println("Maximum cut spacing: " ~ summary.maximumCutSpacing);
        
        if (definition.showDebug)
        {
            // Create debug visualization of cut positions
            for (var i = 0; i < size(solution.cutPositions); i += 1)
            {
                const cutPos = solution.cutPositions[i];
                const curvSign = solution.curvatureSigns[i];
                
                // Use different colors based on curvature sign
                const debugColor = curvSign > 0 ? DebugColor.BLUE : (curvSign < 0 ? DebugColor.RED : DebugColor.GREEN);
                
                // Draw a point at each cut position
                debug(context, cutPos, debugColor);
            }
            
            // Draw the Bezier curve approximation
            for (var i = 0; i < size(solution.cutPositions) - 1; i += 1)
            {
                debug(context, [solution.cutPositions[i], solution.cutPositions[i + 1]], DebugColor.YELLOW);
            }
        }
        
        // Create a sketch with flattened cut positions for manufacturing
        const sketchPlane = plane(vector(0, 0, 0) * meter, vector(0, 0, 1), vector(1, 0, 0));
        const flattenedPositions = calculateFlattenedCutPositions(solution.cutDistances, true);
        
        const sketchId = id + "flattenedSketch";
        var sketch1 = newSketchOnPlane(context, sketchId, {
            "sketchPlane" : sketchPlane
        });
        
        // Draw vertical lines at each flattened position to represent cuts
        for (var i = 0; i < size(flattenedPositions); i += 1)
        {
            const xPos = flattenedPositions[i];
            const lineStart = vector(xPos, 0 * meter);
            const lineEnd = vector(xPos, 10 * millimeter); // 10mm tall lines
            
            skLineSegment(sketch1, "line" ~ i, {
                "start" : lineStart,
                "end" : lineEnd
            });
        }
        
        skSolve(sketch1);
    },
    {
        bladeWidth : 2.7 * millimeter,
        cutDepth : 35 * millimeter,
        showDebug : true,
        showAdvanced : false
    });

/**
 * Extract control points from a curve edge.
 * Uses evCurveDefinition to get the actual curve data, or evApproximateBSplineCurve
 * to get a B-spline approximation, then extracts/converts to quadratic Bezier control points.
 * 
 * @param context : The context
 * @param curveEdge : Query for the curve edge
 * @returns {array} : Array of three 3D control points [start, control, end]
 */
function extractBezierControlPoints(context is Context, curveEdge is Query) returns array
{
    // First try to get the curve definition directly
    const curveDef = evCurveDefinition(context, { "edge" : curveEdge });
    
    // Check if it's a line
    if (curveDef.curveType == CurveType.LINE)
    {
        // For a line, use start and end points with control point at midpoint
        const line = curveDef as Line;
        const startLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : 0.0 });
        const endLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : 1.0 });
        const p0 = startLine.origin;
        const p2 = endLine.origin;
        const p1 = (p0 + p2) / 2; // Control point at midpoint for straight line
        return [p0, p1, p2];
    }
    else if (curveDef.curveType == CurveType.CIRCLE || curveDef.curveType == CurveType.ELLIPSE)
    {
        // For circles and ellipses, approximate with B-spline then extract control points
        return extractFromBSpline(context, curveEdge);
    }
    else
    {
        // For splines and other curves, use B-spline approximation
        return extractFromBSpline(context, curveEdge);
    }
}

/**
 * Extract control points from a curve by getting its B-spline representation.
 * 
 * @param context : The context
 * @param curveEdge : Query for the curve edge
 * @returns {array} : Array of three 3D control points [start, control, end]
 */
function extractFromBSpline(context is Context, curveEdge is Query) returns array
{
    // Get B-spline approximation of the curve
    const bSpline = evApproximateBSplineCurve(context, { "edge" : curveEdge });
    
    const controlPoints = bSpline.controlPoints;
    const numPoints = size(controlPoints);
    
    if (numPoints == 3 && bSpline.degree == 2)
    {
        // Perfect - it's already a quadratic Bezier
        return controlPoints;
    }
    else if (numPoints == 2 && bSpline.degree == 1)
    {
        // Linear B-spline (line segment)
        const p0 = controlPoints[0];
        const p2 = controlPoints[1];
        const p1 = (p0 + p2) / 2;
        return [p0, p1, p2];
    }
    else if (numPoints == 4 && bSpline.degree == 3)
    {
        // Cubic B-spline/Bezier - convert to quadratic approximation
        // For cubic Bezier: approximate middle control point
        const p0 = controlPoints[0];
        const p1 = (3 * controlPoints[1] + 3 * controlPoints[2]) / 6;
        const p2 = controlPoints[3];
        return [p0, p1, p2];
    }
    else
    {
        // Higher order or different structure - fit quadratic Bezier
        // Use start, end, and approximate middle control point
        const p0 = controlPoints[0];
        const p2 = controlPoints[numPoints - 1];
        
        // Estimate middle control point from the B-spline control points
        // Use weighted average of interior points
        var weightedSum = vector(0, 0, 0) * meter;
        for (var i = 1; i < numPoints - 1; i += 1)
        {
            weightedSum = weightedSum + controlPoints[i];
        }
        const p1 = weightedSum / (numPoints - 2);
        
        return [p0, p1, p2];
    }
}

