FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/sketch.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2837.0");
import(path : "onshape/std/facecurvecreationtype.gen.fs", version : "2837.0");
import(path : "1e97dc34e0d8907329a69da7", version : "806f058e8aae45b01b222494"); // kerfBendingAnalytical.fs

/**
 * 3D Kerf Bending feature for CNC manufacturing.
 * Generates kerf cuts on solid bodies by selecting a face to bend along.
 * Automatically determines board thickness and detects the curvier U/V curve for bending.
 */
annotation { "Feature Type Name" : "Kerf Bending 3D" }
export const kerfBending3D = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Bend face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1, "Description" : "Face defining the bend surface" }
        definition.bendFace is Query;
        
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
        }
    }
    {
        // Get the solid body from the selected face
        const solidBody = qOwnerBody(definition.bendFace);
        
        // Get the face's center point and normal to measure thickness
        const facePlane = evFaceTangentPlane(context, {
            "face" : definition.bendFace,
            "parameter" : vector(0.5, 0.5)
        });
        
        const faceNormal = facePlane.normal;
        const facePoint = facePlane.origin;
        
        // Measure board thickness by finding opposite face
        var boardThickness = definition.cutDepth * 1.2; // Default fallback
        
        try
        {
            const allFaces = qOwnedByBody(solidBody, EntityType.FACE);
            const faceArray = evaluateQuery(context, allFaces);
            
            var maxDistance = 0 * meter;
            for (var face in faceArray)
            {
                const otherFacePlane = evFaceTangentPlane(context, {
                    "face" : face,
                    "parameter" : vector(0.5, 0.5)
                });
                
                const normalDot = dot(faceNormal, otherFacePlane.normal);
                if (normalDot < -0.9)
                {
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
        
        // Automatically detect which direction (U or V) is curvier
        // Evaluate face curvature directly at the center point
        const faceCurvature = evFaceCurvature(context, {
            "face" : definition.bendFace,
            "parameter" : vector(0.5, 0.5)
        });
        
        // The face has two principal curvatures (minCurvature and maxCurvature)
        // These correspond to the two parametric directions
        // Use the direction with maximum curvature as the bend curve
        const dir1Curvature = abs(faceCurvature.minCurvature);
        const dir2Curvature = abs(faceCurvature.maxCurvature);
        
        // The max curvature direction is typically what we want for bending
        const useDIR2 = dir2Curvature > dir1Curvature;
        
        println("Face principal curvatures at (0.5, 0.5):");
        println("  Min curvature: " ~ toString(faceCurvature.minCurvature));
        println("  Max curvature: " ~ toString(faceCurvature.maxCurvature));
        println("Using direction with " ~ (useDIR2 ? "max" : "min") ~ " curvature as bend curve");
        
        // Create only the curve we need in the chosen direction using proper curveDefinition
        const bendCurveId = id + "bendCurve";
        const faceCurveType = useDIR2 ? FaceCurveCreationType.DIR2_ISO : FaceCurveCreationType.DIR1_ISO;
        const curveDef = curveOnFaceDefinition(definition.bendFace, faceCurveType, ["bendCurve"], [0.5]);
        
        opCreateCurvesOnFace(context, bendCurveId, {
            "curveDefinition" : [curveDef],
            "showCurves" : true,
            "useFaceParameter" : true
        });
        
        const bendCurveEdge = qCreatedBy(bendCurveId, EntityType.EDGE);
        
        // Use default minimum spacing if not specified
        const minimumCutSpacing = definition.showAdvanced ? 
            definition.minimumCutSpacing : 
            definition.bladeWidth * 2;
        
        const useHalfKerfOffset = definition.showAdvanced && definition.useHalfKerfOffset;
        
        // Generate the kerf bending solution using analytical approach
        const solution = generateAnalyticalKerfSolution(
            context,
            bendCurveEdge,
            definition.bladeWidth,
            definition.cutDepth,
            minimumCutSpacing,
            useHalfKerfOffset
        );
        
        // Create summary for display
        const summary = createKerfBendingSummary(solution);
        
        // Display results
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
        
        if (definition.showDebug)
        {
            for (var i = 0; i < @size(solution.cutPositions); i += 1)
            {
                const cutPos = solution.cutPositions[i];
                const curvSign = solution.curvatureSigns[i];
                const debugColor = curvSign > 0 ? DebugColor.BLUE : (curvSign < 0 ? DebugColor.RED : DebugColor.GREEN);
                addDebugPoint(context, cutPos, debugColor);
            }
        }
        
        // Generate 3D kerf cuts using the new geometry approach
        generate3DKerfCutsOnBentSurface(
            context,
            id + "cuts",
            solidBody,
            definition.bendFace,
            bendCurveEdge,
            solution,
            definition.bladeWidth,
            definition.cutDepth,
            boardThickness
        );
        
        println("Successfully generated " ~ solution.numberOfCuts ~ " 3D kerf cuts");
    },
    {
        bladeWidth : 2.7 * millimeter,
        cutDepth : 16 * millimeter,
        showDebug : true,
        showAdvanced : false
    });


