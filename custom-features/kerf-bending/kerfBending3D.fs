FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "d206287863cb4400750c9aff", version : "36c04743534bad7be94aac56"); // kerfBendingAnalytical.fs

/**
 * 3D Kerf Bending feature for CNC manufacturing.
 * Generates kerf cuts on solid bodies by selecting a face to bend along.
 * Automatically determines board thickness and generates cuts perpendicular to the bend line.
 */
annotation { "Feature Type Name" : "Kerf Bending 3D" }
export const kerfBending3D = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Bend face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1, "Description" : "Face defining the bend surface" }
        definition.bendFace is Query;
        
        annotation { "Name" : "Bend edge", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1, "Description" : "Edge along which to create kerf cuts" }
        definition.bendEdge is Query;
        
        annotation { "Name" : "Blade width" }
        isLength(definition.bladeWidth, BLEND_BOUNDS);
        
        annotation { "Name" : "Cut depth", "Description" : "Depth of cut (will be relative to board thickness)" }
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
            
            annotation { "Name" : "Cut extension", "Description" : "Extend cuts beyond the curve in perpendicular direction", "Default" : 0 * millimeter }
            isLength(definition.cutExtension, BLEND_BOUNDS);
        }
    }
    {
        // Get the solid body from the selected face
        const solidBody = qOwnerBody(definition.bendFace);
        
        // Measure the thickness at the selected face
        // We'll get the face normal and measure the distance to the opposite face
        const faceNormal = evFaceTangentPlane(context, {
            "face" : definition.bendFace,
            "parameter" : vector(0.5, 0.5)
        }).normal;
        
        // Get a point on the face
        const facePoint = evFaceTangentPlane(context, {
            "face" : definition.bendFace,
            "parameter" : vector(0.5, 0.5)
        }).origin;
        
        // Measure distance to opposite face by casting a ray
        // We'll use a simple approach: measure distance along the normal
        var boardThickness = definition.cutDepth * 1.2; // Default fallback
        
        try
        {
            // Try to find the opposite face by looking at adjacent faces
            const allFaces = qOwnedByBody(solidBody, EntityType.FACE);
            const faceArray = evaluateQuery(context, allFaces);
            
            // Find the face most opposite to our selected face
            var maxDistance = 0 * meter;
            for (var face in faceArray)
            {
                const otherFacePlane = evFaceTangentPlane(context, {
                    "face" : face,
                    "parameter" : vector(0.5, 0.5)
                });
                
                // Check if normals are roughly opposite
                const normalDot = dot(faceNormal, otherFacePlane.normal);
                if (normalDot < -0.9) // Roughly opposite
                {
                    // Measure distance
                    const distance = dot(otherFacePlane.origin - facePoint, faceNormal);
                    if (abs(distance) > maxDistance)
                    {
                        maxDistance = abs(distance);
                    }
                }
            }
            
            if (maxDistance > 0 * meter)
            {
                boardThickness = maxDistance;
            }
        }
        
        // Use default minimum spacing if not specified
        const minimumCutSpacing = definition.showAdvanced ? 
            definition.minimumCutSpacing : 
            definition.bladeWidth * 2;
        
        // Use default half-kerf offset setting if not specified
        const useHalfKerfOffset = definition.showAdvanced && definition.useHalfKerfOffset;
        
        // Use default cut extension if not specified
        const cutExtension = definition.showAdvanced ? 
            definition.cutExtension : 
            0 * meter;
        
        // Generate the kerf bending solution using analytical approach
        const solution = generateAnalyticalKerfSolution(
            context,
            definition.bendEdge,
            definition.bladeWidth,
            definition.cutDepth,
            minimumCutSpacing,
            useHalfKerfOffset
        );
        
        // Create summary for display
        const summary = createKerfBendingSummary(solution);
        
        // Display results in the console with better formatting
        println("=== Kerf Bending 3D Solution ===");
        println("Blade width: " ~ toString(definition.bladeWidth));
        println("Cut depth: " ~ toString(definition.cutDepth));
        println("Board thickness (measured): " ~ toString(boardThickness));
        const flexibleThickness = boardThickness - definition.cutDepth;
        println("Flexible layer thickness: " ~ toString(flexibleThickness));
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
        
        // Generate 3D kerf cuts on the solid body
        const cutsGenerated = generate3DKerfCuts(
            context,
            id + "cuts",
            solidBody,
            definition.bendEdge,
            solution,
            definition.bladeWidth,
            definition.cutDepth,
            boardThickness,
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
    },
    {
        bladeWidth : 2.7 * millimeter,
        cutDepth : 16 * millimeter,
        showDebug : true,
        showAdvanced : false
    });

