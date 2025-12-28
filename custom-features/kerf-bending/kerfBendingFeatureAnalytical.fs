FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "d206287863cb4400750c9aff", version : "36c04743534bad7be94aac56"); // kerfBendingAnalytical.fs

/**
 * Performant kerf bending feature using analytical curve evaluation.
 * Directly calculates cut positions using NURBS curvature - no discretization needed.
 * Now supports 3D solid body cutting for CNC manufacturing.
 */
annotation { "Feature Type Name" : "Kerf Bending" }
export const kerfBendingFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Mode" }
        definition.mode is KerfBendingMode;
        
        if (definition.mode == KerfBendingMode.VISUALIZATION)
        {
            annotation { "Name" : "Curve", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
            definition.curveEdge is Query;
        }
        
        if (definition.mode == KerfBendingMode.GENERATE_3D_CUTS)
        {
            annotation { "Name" : "Solid body", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 1 }
            definition.solidBody is Query;
            
            annotation { "Name" : "Curve edge", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
            definition.curveEdge is Query;
            
            annotation { "Name" : "Board thickness", "Description" : "Total thickness of the board material" }
            isLength(definition.boardThickness, BLEND_BOUNDS);
        }
        
        annotation { "Name" : "Blade width" }
        isLength(definition.bladeWidth, BLEND_BOUNDS);
        
        annotation { "Name" : "Cut depth", "Description" : "Depth of cut (should be less than board thickness to leave flexible material)" }
        isLength(definition.cutDepth, BLEND_BOUNDS);
        
        annotation { "Name" : "Show debug info", "Default" : true }
        definition.showDebug is boolean;
        
        annotation { "Name" : "Advanced settings", "Default" : false }
        definition.showAdvanced is boolean;
        
        if (definition.showAdvanced)
        {
            annotation { "Name" : "Minimum cut spacing", "Description" : "Minimum distance between cuts" }
            isLength(definition.minimumCutSpacing, BLEND_BOUNDS);
            
            annotation { "Name" : "Use half-kerf offset on ends (circles only)", "Default" : false, "Description" : "For circles/arcs, offset first and last cuts by half the spacing" }
            definition.useHalfKerfOffset is boolean;
            
            if (definition.mode == KerfBendingMode.GENERATE_3D_CUTS)
            {
                annotation { "Name" : "Cut extension", "Description" : "Extend cuts beyond the curve in perpendicular direction", "Default" : 0 * millimeter }
                isLength(definition.cutExtension, BLEND_BOUNDS);
            }
        }
    }
    {
        // Use default minimum spacing if not specified
        const minimumCutSpacing = definition.showAdvanced ? 
            definition.minimumCutSpacing : 
            definition.bladeWidth * 2;
        
        // Use default half-kerf offset setting if not specified
        const useHalfKerfOffset = definition.showAdvanced && definition.useHalfKerfOffset;
        
        // Use default cut extension if not specified
        const cutExtension = (definition.showAdvanced && definition.mode == KerfBendingMode.GENERATE_3D_CUTS) ? 
            definition.cutExtension : 
            0 * meter;
        
        // Generate the kerf bending solution using analytical approach
        const solution = generateAnalyticalKerfSolution(
            context,
            definition.curveEdge,
            definition.bladeWidth,
            definition.cutDepth,
            minimumCutSpacing,
            useHalfKerfOffset
        );
        
        // Create summary for display
        const summary = createKerfBendingSummary(solution);
        
        // Display results in the console with better formatting
        println("=== Kerf Bending Solution (Analytical) ===");
        println("Mode: " ~ definition.mode);
        println("Blade width: " ~ toString(definition.bladeWidth));
        println("Cut depth: " ~ toString(definition.cutDepth));
        
        if (definition.mode == KerfBendingMode.GENERATE_3D_CUTS)
        {
            println("Board thickness: " ~ toString(definition.boardThickness));
            const flexibleThickness = definition.boardThickness - definition.cutDepth;
            println("Flexible layer thickness: " ~ toString(flexibleThickness));
        }
        
        println("Minimum cut spacing: " ~ toString(minimumCutSpacing));
        println("Total curve length: " ~ toString(solution.totalLength));
        println("Number of cuts: " ~ solution.numberOfCuts);
        println("Kerf angle (degrees): " ~ toString(solution.kerfAngle / degree));
        println("Kerf angle (radians): " ~ toString(solution.kerfAngle / radian));
        println("Average cut spacing: " ~ toString(summary.averageCutSpacing));
        println("Minimum cut spacing: " ~ toString(summary.minimumCutSpacing));
        println("Maximum cut spacing: " ~ toString(summary.maximumCutSpacing));
        
        if (definition.showDebug)
        {
            // Create debug visualization of cut positions on the actual curve
            for (var i = 0; i < @size(solution.cutPositions); i += 1)
            {
                const cutPos = solution.cutPositions[i];
                const curvSign = solution.curvatureSigns[i];
                
                // Use different colors based on curvature sign
                const debugColor = curvSign > 0 ? DebugColor.BLUE : (curvSign < 0 ? DebugColor.RED : DebugColor.GREEN);
                
                // Draw a visible point at each cut position on the curve
                addDebugPoint(context, cutPos, debugColor);
                
                // Also print to console for verification
                println("Cut " ~ i ~ " at parameter " ~ solution.cutParameters[i] ~ ": " ~ cutPos);
            }
            
            // Draw lines connecting cut positions on the curve
            for (var i = 0; i < @size(solution.cutPositions) - 1; i += 1)
            {
                debug(context, solution.cutPositions[i], solution.cutPositions[i + 1], DebugColor.YELLOW);
            }
        }
        
        // Mode-specific operations
        if (definition.mode == KerfBendingMode.VISUALIZATION)
        {
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
        }
        else if (definition.mode == KerfBendingMode.GENERATE_3D_CUTS)
        {
            // Generate 3D kerf cuts on the solid body
            const cutsGenerated = generate3DKerfCuts(
                context,
                id + "cuts",
                definition.solidBody,
                definition.curveEdge,
                solution,
                definition.bladeWidth,
                definition.cutDepth,
                definition.boardThickness,
                cutExtension
            );
            
            if (cutsGenerated)
            {
                println("Successfully generated " ~ solution.numberOfCuts ~ " 3D kerf cuts");
            }
            else
            {
                println("Warning: Failed to generate some kerf cuts");
            }
        }
    },
    {
        mode : KerfBendingMode.VISUALIZATION,
        bladeWidth : 2.7 * millimeter,
        cutDepth : 35 * millimeter,
        boardThickness : 40 * millimeter,
        showDebug : true,
        showAdvanced : false
    });

/**
 * Enum for kerf bending feature modes
 */
export enum KerfBendingMode
{
    annotation { "Name" : "Visualization only (2D sketch)" }
    VISUALIZATION,
    annotation { "Name" : "Generate 3D cuts on solid" }
    GENERATE_3D_CUTS
}

