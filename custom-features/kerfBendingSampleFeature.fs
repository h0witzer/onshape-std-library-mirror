FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "d206287863cb4400750c9aff", version : "36c04743534bad7be94aac56"); // kerfBendingUtils.fs

/**
 * Sample feature demonstrating kerf bending utilities.
 * This feature calculates kerf cut positions for a parabolic curve and displays the results.
 * 
 * Input methods:
 * 1. Select a Bezier curve edge from a sketch
 * 2. Select 3 vertices (start, control, end) to define a Bezier curve
 */
annotation { "Feature Type Name" : "Kerf Bending Sample" }
export const kerfBendingSample = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Input type", "UIHint" : UIHint.SHOW_LABEL }
        definition.inputType is InputType;
        
        if (definition.inputType == InputType.CURVE)
        {
            annotation { "Name" : "Bezier curve", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
            definition.curveEdge is Query;
        }
        else
        {
            annotation { "Name" : "Start point", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
            definition.startVertex is Query;
            
            annotation { "Name" : "Control point", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
            definition.controlVertex is Query;
            
            annotation { "Name" : "End point", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
            definition.endVertex is Query;
        }
        
        annotation { "Name" : "Blade width" }
        isLength(definition.bladeWidth, BLEND_BOUNDS);
        
        annotation { "Name" : "Cut depth" }
        isLength(definition.cutDepth, BLEND_BOUNDS);
        
        annotation { "Name" : "Curve samples" }
        isInteger(definition.curveSamples, POSITIVE_COUNT_BOUNDS);
        
        annotation { "Name" : "Search window" }
        isInteger(definition.searchWindow, POSITIVE_COUNT_BOUNDS);
        
        annotation { "Name" : "Show debug info", "Default" : true }
        definition.showDebug is boolean;
    }
    {
        var controlPoints;
        
        // Extract control points based on input type
        if (definition.inputType == InputType.CURVE)
        {
            // Get control points from Bezier curve edge
            controlPoints = extractBezierControlPoints(context, definition.curveEdge);
        }
        else
        {
            // Get the vertex positions
            const startPoint = evVertexPoint(context, { "vertex" : definition.startVertex });
            const controlPoint = evVertexPoint(context, { "vertex" : definition.controlVertex });
            const endPoint = evVertexPoint(context, { "vertex" : definition.endVertex });
            
            // Create control points array for quadratic Bezier
            controlPoints = [startPoint, controlPoint, endPoint];
        }
        
        // Generate the kerf bending solution
        const solution = generateKerfBendingSolution(
            controlPoints,
            definition.bladeWidth,
            definition.cutDepth,
            definition.curveSamples,
            definition.searchWindow
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
        inputType : InputType.CURVE,
        bladeWidth : 2.7 * millimeter,
        cutDepth : 35 * millimeter,
        curveSamples : 600,
        searchWindow : 80,
        showDebug : true
    });

/**
 * Enum for input type selection
 */
export enum InputType
{
    annotation { "Name" : "Bezier curve from sketch" }
    CURVE,
    annotation { "Name" : "Three vertices" }
    VERTICES
}

/**
 * Extract control points from a curve edge.
 * Samples the curve at strategic points to approximate a quadratic Bezier.
 * Works with any curve type - Bezier, spline, arc, etc.
 * 
 * @param context : The context
 * @param curveEdge : Query for the curve edge
 * @returns {array} : Array of three 3D control points [start, control, end]
 */
function extractBezierControlPoints(context is Context, curveEdge is Query) returns array
{
    // Sample at t=0 (start), t=0.5 (middle), t=1 (end)
    const startLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : 0.0 });
    const midLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : 0.5 });
    const endLine = evEdgeTangentLine(context, { "edge" : curveEdge, "parameter" : 1.0 });
    
    const p0 = startLine.origin;
    const p2 = endLine.origin;
    const midPoint = midLine.origin;
    
    // For a quadratic Bezier: midPoint = 0.25*p0 + 0.5*p1 + 0.25*p2
    // Solving for p1: p1 = 2*midPoint - 0.5*(p0 + p2)
    const p1 = 2 * midPoint - 0.5 * (p0 + p2);
    
    return [p0, p1, p2];
}

