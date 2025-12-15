FeatureScript 2837;

// Standard Library Imports
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/curveGeometry.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/path.fs", version : "2837.0");

// Import tweenCurves function and utilities
import(path : "eba90c822a38b2ab9d2b67c5", version : "028e645c08deafca1e158865"); // tweenTwoCurves.fs (includes tweenCurves)
import(path : "f42f46716945f2a9bda5a481/eabbc18661ba5776e0ba962d/97730412fb61f53dcd526c08", version : "a24da502290d2ae4706c631f"); // 3d Arc Utilities

/**
 * Defines the method for matching curve segments between two paths.
 */
export enum TweenConnectionMethod
{
    annotation { "Name" : "Nearest distance" }
    NEAREST_DISTANCE,
    annotation { "Name" : "Path length parameterization" }
    PATH_LENGTH
}

export const TWEEN_FRACTION_BOUNDS = { (unitless) : [0, 0.5, 1] } as RealBoundSpec;

annotation { 
    "Feature Type Name" : "Tween Multiple Curves",
    "Feature Type Description" : "Interpolates B-spline control points between two paths of multiple curves. Uses the standard tweenCurves function.",
    "UIHint" : "NO_PREVIEW_PROVIDED" 
}
export const tweenMultipleCurves = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Connection method", "UIHint" : UIHint.SHOW_LABEL }
        definition.connectionMethod is TweenConnectionMethod;
        
        annotation { "Name" : "First curve or edge group", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO }
        definition.curves1 is Query;
        
        annotation { "Name" : "Second curve or edge group", "Filter" : EntityType.EDGE && SketchObject.NO && ConstructionObject.NO }
        definition.curves2 is Query;
        
        annotation { "Name" : "Tween fraction" }
        isReal(definition.fraction, TWEEN_FRACTION_BOUNDS);
    }
    {
        // Check if we have single edges first, before any query manipulation
        const initialCount1 = evaluateQueryCount(context, definition.curves1);
        const initialCount2 = evaluateQueryCount(context, definition.curves2);
        
        // If both are single edges, pass the original queries directly to tweenCurves
        // This ensures identical behavior to tweenTwoCurves
        if (initialCount1 == 1 && initialCount2 == 1)
        {
            tweenCurves(context, id, definition.curves1, definition.curves2, definition.fraction);
            return;
        }
        
        // Get all connected edges for each input (only for multi-curve case)
        const edgeGroup1 = qTangentConnectedEdges(definition.curves1);
        const edgeGroup2 = qTangentConnectedEdges(definition.curves2);
        
        const edgeCount1 = evaluateQueryCount(context, edgeGroup1);
        const edgeCount2 = evaluateQueryCount(context, edgeGroup2);
        
        // For multiple edges, we need to determine break points and tween subsegments
        // Build paths
        const path1 = constructPath(context, edgeGroup1);
        const path2 = constructPath(context, edgeGroup2);
        
        // Generate break points based on method
        var breakPoints = [];
        if (definition.connectionMethod == TweenConnectionMethod.PATH_LENGTH)
        {
            breakPoints = generatePathLengthBreakPoints(context, path1, path2, edgeGroup1, edgeGroup2);
        }
        else
        {
            breakPoints = generateNearestDistanceBreakPoints(context, path1, path2, edgeGroup1, edgeGroup2);
        }
        
        // Tween each segment pair
        for (var i = 0; i < size(breakPoints) - 1; i += 1)
        {
            const start1 = breakPoints[i].segment1;
            const end1 = breakPoints[i + 1].segment1;
            const start2 = breakPoints[i].segment2;
            const end2 = breakPoints[i + 1].segment2;
            
            // For now, if segment is a full edge, tween it using the imported function
            if (start1.edge == end1.edge && start1.parameter == 0.0 && end1.parameter == 1.0)
            {
                if (start2.edge == end2.edge && start2.parameter == 0.0 && end2.parameter == 1.0)
                {
                    tweenCurves(context, id + ("seg_" ~ i), start1.edge, start2.edge, definition.fraction);
                }
            }
        }
    });

// Multi-curve helper functions

function generateNearestDistanceBreakPoints(context is Context, path1 is Path, path2 is Path,
                                             edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Simplified: just return start and end for now
    return [
        {
            "segment1" : { "edge" : path1.edges[0], "parameter" : 0.0 },
            "segment2" : { "edge" : path2.edges[0], "parameter" : 0.0 }
        },
        {
            "segment1" : { "edge" : path1.edges[size(path1.edges) - 1], "parameter" : 1.0 },
            "segment2" : { "edge" : path2.edges[size(path2.edges) - 1], "parameter" : 1.0 }
        }
    ];
}

function generatePathLengthBreakPoints(context is Context, path1 is Path, path2 is Path,
                                        edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Simplified: just return start and end for now
    return [
        {
            "segment1" : { "edge" : path1.edges[0], "parameter" : 0.0 },
            "segment2" : { "edge" : path2.edges[0], "parameter" : 0.0 }
        },
        {
            "segment1" : { "edge" : path1.edges[size(path1.edges) - 1], "parameter" : 1.0 },
            "segment2" : { "edge" : path2.edges[size(path2.edges) - 1], "parameter" : 1.0 }
        }
    ];
}
