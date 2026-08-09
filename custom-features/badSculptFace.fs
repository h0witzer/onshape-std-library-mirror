FeatureScript 2679;
// Experimental feature for editing a B-spline surface directly. Control
// points are exposed through manipulators similar to the Edit Curve feature.
// Utility imports from the Standard Library
import(path : "onshape/std/common.fs", version : "2679.0");
import(path : "onshape/std/feature.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/manipulator.fs", version : "2679.0");
import(path : "onshape/std/containers.fs", version : "2679.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2679.0");
import(path : "onshape/std/matrix.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/valueBounds.fs", version : "2679.0");
import(path : "onshape/std/curveGeometry.fs", version : "2679.0");
import(path : "onshape/std/math.fs", version : "2679.0");
import(path : "onshape/std/approximationUtils.fs", version : "2679.0");

// Maximum number of control points allowed when editing a surface.
export const SURFACE_MAX_CONTROL_POINTS = 10000;
// Maximum surface degree allowed for approximations.
const MAX_SURFACE_DEGREE = 15;

/**
 * Convert a set of relative weights into a non-uniform parameter array
 * spanning 0..1. The returned array has length `count`.
 */
function makeWeightedParameterArray(weights is array, count is number) returns array
{
    var total = 0;
    for (var w in weights)
    {
        total += w;
    }
    if (total == 0)
    {
        var params = [];
        for (var i = 0; i < count; i += 1)
        {
            params = append(params, i / (count - 1));
        }
        return params;
    }

    var cumulative = [];
    var running = 0;
    for (var i = 0; i < size(weights); i += 1)
    {
        running += weights[i];
        cumulative = append(cumulative, running / total);
    }

    var result = [];
    for (var i = 0; i < count; i += 1)
    {
        var t = i / (count - 1);
        var j = 0;
        while (j < size(cumulative) - 1 && cumulative[j] < t)
        {
            j += 1;
        }
        var prev = (j == 0 ? 0 : cumulative[j - 1]);
        var frac = (cumulative[j] - prev == 0 ? 0 : (t - prev) / (cumulative[j] - prev));
        result = append(result, (j + frac) / size(weights));
    }
    return result;
}

/**
 * Produce a parameter array by looking at the spacing of an existing knot array.
 * The returned parameters span `0..1` matching the normalized parameter range
 * expected by evaluation functions such as `evFaceTangentPlanes`.
 */
function parametersFromKnots(knots is KnotArray, count is number) returns array
{
    var uniqueKnots = [];
    for (var i = 0; i < size(knots); i += 1)
    {
        if (i == 0 || knots[i] != knots[i - 1])
        {
            uniqueKnots = append(uniqueKnots, knots[i]);
        }
    }

    if (size(uniqueKnots) <= 1)
    {
        var params = [];
        for (var i = 0; i < count; i += 1)
        {
            params = append(params, i / (count - 1));
        }
        return params;
    }

    var weights = [];
    for (var i = 0; i < size(uniqueKnots) - 1; i += 1)
    {
        weights = append(weights, uniqueKnots[i + 1] - uniqueKnots[i]);
    }

    return makeWeightedParameterArray(weights, count);
}

/**
 * Determine sampling parameters for a surface based on its existing knot arrays.
 */
function computeParametersFromSurface(surface is BSplineSurface, uCount is number, vCount is number) returns map
{
    return { "u" : parametersFromKnots(surface.uKnots, uCount),
             "v" : parametersFromKnots(surface.vKnots, vCount) };
}
export const SURFACE_DEGREE_BOUND =
{
    (unitless) : [1, 3, MAX_SURFACE_DEGREE]
} as IntegerBoundSpec;
export const CONTROL_POINT_INDEX_BOUND =
{
    (unitless) : [0, 0, SURFACE_MAX_CONTROL_POINTS - 1]
} as IntegerBoundSpec;

const INDEX_MANIPULATOR = "indexManipulator";
const OFFSET_MANIPULATOR = "offsetManipulator";

annotation { "Feature Type Name" : "Sculpt Face",
        "Manipulator Change Function" : "onSculptFaceManipulatorChange" }
export const sculptFace = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.face is Query;

        annotation { "Name" : "Approximate" }
        definition.approximate is boolean;
        annotation { "Group Name" : "Approximation parameters", "Driving Parameter" : "approximate", "Collapsed By Default" : false }
        {
            if (definition.approximate)
            {
                annotation { "Name" : "U degree" }
                isInteger(definition.uDegree, SURFACE_DEGREE_BOUND);

                annotation { "Name" : "V degree" }
                isInteger(definition.vDegree, SURFACE_DEGREE_BOUND);

                annotation { "Name" : "U control points" }
                isInteger(definition.uCount, { (unitless) : [2, 4, SURFACE_MAX_CONTROL_POINTS] } as IntegerBoundSpec);

                annotation { "Name" : "V control points" }
                isInteger(definition.vCount, { (unitless) : [2, 4, SURFACE_MAX_CONTROL_POINTS] } as IntegerBoundSpec);

                annotation { "Name" : "Tolerance" }
                isLength(definition.tolerance, TOLERANCE_BOUND);
            }
        }

        annotation { "Name" : "Edit control points" }
        definition.editControlPoints is boolean;
        annotation { "Group Name" : "Edit control points", "Driving Parameter" : "editControlPoints", "Collapsed By Default" : false }
        {
            if (definition.editControlPoints)
            {
                annotation { "Name" : "Selected index" }
                isInteger(definition.selectedIndex, CONTROL_POINT_INDEX_BOUND);

                annotation { "Name" : "Control point edits", "Item name" : "edit", "Item label template" : "#index", "UIHint" : UIHint.PREVENT_ARRAY_REORDER }
                definition.controlPointEdits is array;
                for (var edit in definition.controlPointEdits)
                {
                    annotation { "Name" : "Index" }
                    isInteger(edit.index, CONTROL_POINT_INDEX_BOUND);

                    annotation { "Name" : "X offset" }
                    isLength(edit.x, ZERO_DEFAULT_LENGTH_BOUNDS);
                    annotation { "Name" : "Y offset" }
                    isLength(edit.y, ZERO_DEFAULT_LENGTH_BOUNDS);
                    annotation { "Name" : "Z offset" }
                    isLength(edit.z, ZERO_DEFAULT_LENGTH_BOUNDS);
                }
            }
        }
    }
    {
        if (isQueryEmpty(context, definition.face))
        {
            return;
        }

        const approximation = evApproximateBSplineSurface(context, { "face" : definition.face });
        var surface = approximation.bSplineSurface;
        if (definition.approximate)
        {
            surface = approximateFace(context, approximation.bSplineSurface, definition.face,
                    definition.uDegree, definition.vDegree, definition.uCount, definition.vCount);
        }

        surface = applyControlPointEdits(context, id, surface, definition);

        showControlPointManipulators(context, id, surface.controlPoints, definition.editControlPoints ? definition.selectedIndex : -1);

        opCreateBSplineSurface(context, id + "editedSurface", {
                    "bSplineSurface" : surface,
                    "boundaryBSplineCurves" : approximation.boundaryBSplineCurves
                });
    }, { approximate : false, uDegree : 3, vDegree : 3, uCount : 4, vCount : 4,
         tolerance : 1e-4 * meter, editControlPoints : false, selectedIndex : 0 });


/**
 * Approximate `face` with a new B-spline surface using tangent plane sampling.
 * Sampling parameters are derived from the original surface's knot spacing.
 */
function approximateFace(context is Context, baseSurface is BSplineSurface, face is Query,
                         uDegree is number, vDegree is number,
                         uCount is number, vCount is number) returns map
{
    const params = computeParametersFromSurface(baseSurface, uCount, vCount);
    var sampleParams = [];
    for (var i = 0; i < uCount; i += 1)
    {
        for (var j = 0; j < vCount; j += 1)
        {
            sampleParams = append(sampleParams, vector(params.u[i], params.v[j]));
        }
    }

    const planes = evFaceTangentPlanes(context, { "face" : face, "parameters" : sampleParams });
    var cpMatrix = [];
    var index = 0;
    for (var u = 0; u < uCount; u += 1)
    {
        var row = [];
        for (var v = 0; v < vCount; v += 1)
        {
            row = append(row, planes[index].origin);
            index += 1;
        }
        cpMatrix = append(cpMatrix, row);
    }

    return bSplineSurface({
                "uDegree" : uDegree,
                "vDegree" : vDegree,
                "isUPeriodic" : false,
                "isVPeriodic" : false,
                "controlPoints" : controlPointMatrix(cpMatrix),
                "uKnots" : makeUniformKnotArray(uDegree, uCount, false),
                "vKnots" : makeUniformKnotArray(vDegree, vCount, false)
            });
}


/**
 * Apply user-specified offsets to the surface control points. If the selected
 * index is valid a triad manipulator is shown at that location.
 */
function applyControlPointEdits(context is Context, id is Id, surface is map, definition is map) returns map
{
    if (!definition.editControlPoints)
    {
        return surface;
    }

    const uCount = size(surface.controlPoints);
    const vCount = size(surface.controlPoints[0]);
    var selectedBase = vector(0, 0, 0) * meter;
    var selectedOffset = vector(0, 0, 0) * meter;

    if (definition.selectedIndex >= 0 && definition.selectedIndex < uCount * vCount)
    {
        const sRow = floor(definition.selectedIndex / vCount);
        const sColumn = definition.selectedIndex - sRow * vCount;
        if (sRow < uCount && sColumn < vCount)
        {
            selectedBase = surface.controlPoints[sRow][sColumn];
        }
    }

    for (var edit in definition.controlPointEdits)
    {
        const index = edit.index;
        const row = floor(index / vCount);
        const column = index - row * vCount;
        if (row >= uCount || column >= vCount)
        {
            continue;
        }
        const cp = surface.controlPoints[row][column];
        if (index == definition.selectedIndex)
        {
            selectedOffset = vector(edit.x, edit.y, edit.z);
        }
        surface.controlPoints[row][column] = cp + vector(edit.x, edit.y, edit.z);
    }

    if (definition.selectedIndex >= 0 && definition.selectedIndex < uCount * vCount)
    {
        showTriadManipulator(context, id, selectedBase, selectedOffset);
    }
    return surface;
}

/**
 * Flatten a `ControlPointMatrix` into a simple array of vectors.
 */
function flattenControlPoints(cpMatrix is ControlPointMatrix) returns array
{
    var result = [];
    for (var i = 0; i < size(cpMatrix); i += 1)
    {
        for (var j = 0; j < size(cpMatrix[0]); j += 1)
        {
            result = append(result, cpMatrix[i][j]);
        }
    }
    return result;
}

/**
 * Display a points manipulator for all control points with the currently
 * selected index highlighted.
 */
function showControlPointManipulators(context is Context, id is Id, cpMatrix is ControlPointMatrix, selectedIndex is number)
{
    const points = flattenControlPoints(cpMatrix);
    const indexManip = pointsManipulator({ "points" : points, "index" : selectedIndex });
    addManipulators(context, id, { (INDEX_MANIPULATOR) : indexManip });
}

/**
 * Display a triad manipulator for editing the currently selected control point.
 */
function showTriadManipulator(context is Context, id is Id, basePoint is Vector, offset is Vector)
{
    const triad = triadManipulator({ "base" : basePoint, "offset" : offset });
    addManipulators(context, id, { (OFFSET_MANIPULATOR) : triad });
}

/**
 * Handle manipulator changes for the edit surface feature.
 */
export function onSculptFaceManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    if (newManipulators[INDEX_MANIPULATOR] is map)
    {
        definition.editControlPoints = true;
        definition.selectedIndex = newManipulators[INDEX_MANIPULATOR].index;
    }
    if (newManipulators[OFFSET_MANIPULATOR] is map)
    {
        var found = false;
        for (var i = 0; i < size(definition.controlPointEdits); i += 1)
        {
            if (definition.controlPointEdits[i].index == definition.selectedIndex)
            {
                definition.controlPointEdits[i].x = newManipulators[OFFSET_MANIPULATOR].offset[0];
                definition.controlPointEdits[i].y = newManipulators[OFFSET_MANIPULATOR].offset[1];
                definition.controlPointEdits[i].z = newManipulators[OFFSET_MANIPULATOR].offset[2];
                found = true;
                break;
            }
        }
        if (!found)
        {
            var newEdit = {
                "index" : definition.selectedIndex,
                "x" : newManipulators[OFFSET_MANIPULATOR].offset[0],
                "y" : newManipulators[OFFSET_MANIPULATOR].offset[1],
                "z" : newManipulators[OFFSET_MANIPULATOR].offset[2]
            };
            definition.controlPointEdits = append(definition.controlPointEdits, newEdit);
        }
    }
    return definition;
}

