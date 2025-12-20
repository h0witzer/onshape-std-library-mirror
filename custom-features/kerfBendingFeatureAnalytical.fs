FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "d206287863cb4400750c9aff", version : "36c04743534bad7be94aac56"); // kerfBendingAnalytical.fs

/**
 * Performant kerf bending feature using analytical curve evaluation.
 * Directly calculates cut positions using NURBS curvature - no discretization needed.
 */
annotation { "Feature Type Name" : "Kerf Bending" }
export const kerfBendingFeature = defineFeature(function(context is Context, id is Id, definition is map)
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
            annotation { "Name" : "Minimum cut spacing", "Description" : "Minimum distance between cuts" }
            isLength(definition.minimumCutSpacing, BLEND_BOUNDS);
        }
    }
    {
        // Use default minimum spacing if not specified
        const minimumCutSpacing = definition.showAdvanced ? 
            definition.minimumCutSpacing : 
            definition.bladeWidth * 2;
        
        // Generate the kerf bending solution using analytical approach
        const solution = generateAnalyticalKerfSolution(
            context,
            definition.curveEdge,
            definition.bladeWidth,
            definition.cutDepth,
            minimumCutSpacing
        );
        
        // Create summary for display
        const summary = createKerfBendingSummary(solution);
        
        // Display results in the console
        println("=== Kerf Bending Solution (Analytical) ===");
        println("Total curve length: " ~ solution.totalLength);
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
            
            // Draw lines connecting cut positions
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

