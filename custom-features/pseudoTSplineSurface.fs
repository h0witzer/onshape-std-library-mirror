FeatureScript 2909;

/**
 * Pseudo T-Spline Surface Feature
 *
 * Decomposes an input face into a grid of cubic Bezier patches arranged in the patch
 * architecture that underpins T-spline topology. Each patch is an independent sheet body
 * carrying its own 4x4 cubic Bezier control grid, so local post-processing (move face,
 * replace face, further subdivision) operates on a single bounded patch instead of
 * re-evaluating a global surface.
 *
 * Design rationale (vs. a single large B-spline):
 *   - Memory cost is O(active patches) rather than O(full UV parameter domain).
 *   - The Parasolid kernel ingests small cubic patches trivially; there is no per-span
 *     basis-function setup overhead for spans that hold no design intent.
 *   - C1 continuity at patch boundaries is guaranteed analytically by the Bezier extraction
 *     algorithm: adjacent patches share the identical boundary control points that were
 *     produced by inserting knots until every interior knot reaches full multiplicity.
 *
 * Algorithm:
 *   1. Obtain a cubic, non-rational B-spline approximation of the input face via
 *      evApproximateBSplineSurface (forceCubic, forceNonRational).
 *   2. Optionally pre-insert uniform knots in U and/or V to increase patch density
 *      before extraction (each extra subdivision level doubles the patch count).
 *   3. Apply Bezier extraction in the U direction:
 *      - For each V-column, insert interior knots to reach full multiplicity (= degree),
 *        using Boehm's single-knot insertion algorithm.
 *      - The resulting column control points partition exactly into (nUSpans) groups of
 *        (degree + 1) = 4 points.
 *   4. Apply Bezier extraction in the V direction:
 *      - For each U-patch, iterate over its 4 U-rows and apply the same column procedure
 *        using the original V knot vector.
 *   5. Each (iU, iV) combination yields one 4x4 control point matrix.
 *   6. Call opCreateBSplineSurface once per patch.
 *   7. Stitch all patch sheet bodies with opBoolean UNION. If the stitch fails (can happen
 *      when the approximation introduces gaps above kernel tolerance), the patches are left
 *      as separate bodies and a notice is posted.
 *
 * References:
 *   Piegl, L. & Tiller, W. (1997). "The NURBS Book" (2nd ed.). Springer.
 *   Algorithm A5.6 (Bezier decomposition of a NURBS curve into Bezier segments), p. 173.
 */

import(path : "onshape/std/boolean.fs",          version : "2909.0");
import(path : "onshape/std/common.fs",            version : "2909.0");
import(path : "onshape/std/containers.fs",        version : "2909.0");
import(path : "onshape/std/curveGeometry.fs",     version : "2909.0");
import(path : "onshape/std/error.fs",             version : "2909.0");
import(path : "onshape/std/evaluate.fs",          version : "2909.0");
import(path : "onshape/std/feature.fs",           version : "2909.0");
import(path : "onshape/std/geomOperations.fs",    version : "2909.0");
import(path : "onshape/std/math.fs",              version : "2909.0");
import(path : "onshape/std/query.fs",             version : "2909.0");
import(path : "onshape/std/string.fs",            version : "2909.0");
import(path : "onshape/std/surfaceGeometry.fs",   version : "2909.0");
import(path : "onshape/std/units.fs",             version : "2909.0");
import(path : "onshape/std/valueBounds.fs",       version : "2909.0");
import(path : "onshape/std/vector.fs",            version : "2909.0");

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Floating-point tolerance used when comparing knot values.
 * Two knots are considered identical when their absolute difference is below this threshold.
 * 1e-14 is well below the numerical noise floor for knot values that are normalised to [0, 1].
 */
const KNOT_TOLERANCE = 1e-14;

// ---------------------------------------------------------------------------
// Bound specs
// ---------------------------------------------------------------------------

/** Allowed range for additional pre-extraction subdivision levels per direction (0 = natural spans). */
const SUBDIVISION_LEVEL_BOUNDS =
{
    (unitless) : [0, 0, 4]
} as IntegerBoundSpec;

// ---------------------------------------------------------------------------
// Feature definition
// ---------------------------------------------------------------------------

/**
 * Decomposes a selected face into an array of cubic Bezier patch sheet bodies whose
 * boundary edges are exactly coincident, then stitches them into a single surface body.
 *
 * The resulting body is geometrically identical to the input face (to the accuracy of the
 * B-spline approximation) but is internally represented as a collection of small NURBS
 * patches, enabling the local-refinement workflows characteristic of T-spline modelling.
 */
annotation { "Feature Type Name" : "Pseudo T-Spline Surface",
             "Feature Type Description" : "Decomposes a face into cubic Bezier patches using Bezier extraction. Each patch is an independent NURBS surface, enabling T-spline-style local modification.",
             "UIHint" : "NO_PREVIEW_PROVIDED" }
export const pseudoTSplineSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Source face",
                     "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO,
                     "MaxNumberOfPicks" : 1 }
        definition.sourceFace is Query;

        annotation { "Name" : "Extra U subdivisions",
                     "Description" : "Pre-extraction uniform knot insertions in U. Each level doubles the U patch count (0 = natural spans from the approximation)." }
        isInteger(definition.extraUSubdivisions, SUBDIVISION_LEVEL_BOUNDS);

        annotation { "Name" : "Extra V subdivisions",
                     "Description" : "Pre-extraction uniform knot insertions in V. Each level doubles the V patch count (0 = natural spans from the approximation)." }
        isInteger(definition.extraVSubdivisions, SUBDIVISION_LEVEL_BOUNDS);

        annotation { "Name" : "Keep source surface",
                     "Description" : "Retain the original input face body. Uncheck to delete it after patch construction." }
        definition.keepSourceSurface is boolean;

        annotation { "Name" : "Enable diagnostics" }
        definition.enableDiagnostics is boolean;

        annotation { "Group Name" : "Developer diagnostics",
                     "Driving Parameter" : "enableDiagnostics",
                     "Collapsed By Default" : true }
        {
            if (definition.enableDiagnostics)
            {
                annotation { "Name" : "Report span counts" }
                definition.diagnosticSpanCounts is boolean;

                annotation { "Name" : "Report knot vectors" }
                definition.diagnosticKnotVectors is boolean;

                annotation { "Name" : "Report control point grid" }
                definition.diagnosticControlPoints is boolean;
            }
        }
    }
    {
        if (isQueryEmpty(context, definition.sourceFace))
        {
            throw regenError("Select a source face to decompose.", ["sourceFace"]);
        }

        // Obtain the cubic non-rational B-spline representation of the input face.
        const approximationResult = evApproximateBSplineSurface(context, {
                    "face"             : definition.sourceFace,
                    "forceCubic"       : true,
                    "forceNonRational" : true
                });

        var sourceSurface = approximationResult.bSplineSurface;

        if (definition.enableDiagnostics && definition.diagnosticSpanCounts)
        {
            const nURows       = size(sourceSurface.controlPoints);
            const nVColumns    = size(sourceSurface.controlPoints[0]);
            const naturalUSpans = nURows    - sourceSurface.uDegree;
            const naturalVSpans = nVColumns - sourceSurface.vDegree;
            println("Pseudo T-Spline: source surface - "
                    ~ nURows ~ " U control rows, "
                    ~ nVColumns ~ " V control columns, "
                    ~ naturalUSpans ~ " natural U spans, "
                    ~ naturalVSpans ~ " natural V spans");
        }

        if (definition.enableDiagnostics && definition.diagnosticKnotVectors)
        {
            println("Pseudo T-Spline: uKnots = " ~ toString(sourceSurface.uKnots));
            println("Pseudo T-Spline: vKnots = " ~ toString(sourceSurface.vKnots));
        }

        // Apply pre-extraction uniform subdivisions if requested.
        // Each level halves every knot span by inserting a midpoint knot.
        if (definition.extraUSubdivisions > 0)
        {
            sourceSurface = applyUniformSubdivisions(sourceSurface, definition.extraUSubdivisions, true);
        }
        if (definition.extraVSubdivisions > 0)
        {
            sourceSurface = applyUniformSubdivisions(sourceSurface, definition.extraVSubdivisions, false);
        }

        // Perform full Bezier extraction to decompose the surface into patches.
        const patchGrid = extractBezierPatchGrid(sourceSurface, definition);

        const nUPatches = size(patchGrid);
        const nVPatches = (nUPatches > 0) ? size(patchGrid[0]) : 0;

        if (nUPatches == 0 || nVPatches == 0)
        {
            throw regenError("Bezier extraction produced no patches. The source surface may be degenerate.");
        }

        if (definition.enableDiagnostics && definition.diagnosticSpanCounts)
        {
            println("Pseudo T-Spline: extracted " ~ nUPatches ~ " x " ~ nVPatches
                    ~ " = " ~ (nUPatches * nVPatches) ~ " cubic Bezier patches");
        }

        // Create one sheet body per patch.
        for (var iU = 0; iU < nUPatches; iU += 1)
        {
            for (var iV = 0; iV < nVPatches; iV += 1)
            {
                const patchControlPointMatrix = patchGrid[iU][iV];

                const patchSurface = bSplineSurface({
                            "uDegree"      : 3,
                            "vDegree"      : 3,
                            "isUPeriodic"  : false,
                            "isVPeriodic"  : false,
                            "controlPoints" : controlPointMatrix(patchControlPointMatrix)
                            // Knots omitted: bSplineSurface() generates uniform [0,0,0,0,1,1,1,1]
                            // which is the correct clamped-Bezier parameterization for 4 control points.
                        });

                opCreateBSplineSurface(context, id + "patch" + iU + "_" + iV, {
                            "bSplineSurface" : patchSurface
                        });
            }
        }

        // Stitch all patch bodies into a single composite surface.
        // Adjacent patches share their boundary control points exactly (guaranteed by Bezier
        // extraction), so their boundary curves are coincident within kernel tolerance.
        const allPatchBodies = qCreatedBy(id, EntityType.BODY);
        const patchBodyCount = evaluateQueryCount(context, allPatchBodies);

        if (patchBodyCount > 1)
        {
            try
            {
                opBoolean(context, id + "stitchPatches", {
                            "tools"         : allPatchBodies,
                            "operationType" : BooleanOperationType.UNION
                        });
            }
            catch
            {
                // Surface union failed, likely due to approximation-induced gaps exceeding
                // kernel stitching tolerance. Leave the patches as separate bodies and notify.
                reportFeatureInfo(context, id, "Patch stitching failed. The "
                        ~ patchBodyCount
                        ~ " Bezier patches were left as separate bodies. Gaps between patches may exceed the kernel tolerance for the given approximation quality.");
            }
        }

        // Optionally remove the source body.
        if (!definition.keepSourceSurface)
        {
            const sourceBodies = evaluateQuery(context, qOwnerBody(definition.sourceFace));
            if (size(sourceBodies) > 0)
            {
                opDeleteBodies(context, id + "deleteSource", {
                            "entities" : qOwnerBody(definition.sourceFace)
                        });
            }
        }

    }, {
        extraUSubdivisions   : 0,
        extraVSubdivisions   : 0,
        keepSourceSurface    : true,
        enableDiagnostics    : false,
        diagnosticSpanCounts : false,
        diagnosticKnotVectors : false,
        diagnosticControlPoints : false
    });


// ===========================================================================
// Pre-extraction uniform subdivision
// ===========================================================================

/**
 * Inserts midpoint knots into every interior span of the surface in the requested direction,
 * applied `subdivisionLevels` times so that each level doubles the patch count.
 *
 * Works by updating both the knot vector and the corresponding control point rows/columns
 * via repeated single-knot insertions.
 *
 * @param surface     {map}     : The BSplineSurface map to refine.
 * @param subdivisionLevels {number} : Number of doubling passes (1 = 2x spans, 2 = 4x, etc.).
 * @param forUDirection {boolean} : true refines in U (along columns), false refines in V (along rows).
 * @returns {map} : Updated BSplineSurface with additional interior knots.
 */
function applyUniformSubdivisions(surface is map, subdivisionLevels is number, forUDirection is boolean) returns map
{
    var currentSurface = surface;

    for (var level = 0; level < subdivisionLevels; level += 1)
    {
        currentSurface = insertMidpointKnotsInDirection(currentSurface, forUDirection);
    }

    return currentSurface;
}

/**
 * Inserts one midpoint knot into every unique non-empty interior knot span in the
 * requested direction, refining the surface by 2x in that direction.
 *
 * @param surface       {map}     : The BSplineSurface map.
 * @param forUDirection {boolean} : true = refine along U (modify uKnots and V-columns),
 *                                  false = refine along V (modify vKnots and U-rows).
 * @returns {map} : Updated BSplineSurface map.
 */
function insertMidpointKnotsInDirection(surface is map, forUDirection is boolean) returns map
{
    const knotVector = forUDirection ? surface.uKnots : surface.vKnots;
    const degree     = forUDirection ? surface.uDegree : surface.vDegree;

    // Identify the unique interior span midpoints.
    var midpointKnotsToInsert = [];
    for (var i = degree; i < size(knotVector) - degree - 1; i += 1)
    {
        const spanStart = knotVector[i];
        const spanEnd   = knotVector[i + 1];
        if (spanEnd - spanStart > KNOT_TOLERANCE)
        {
            midpointKnotsToInsert = append(midpointKnotsToInsert, (spanStart + spanEnd) / 2);
        }
    }

    // Apply each midpoint insertion to the surface in the correct direction.
    var updatedSurface = surface;
    for (var midpointKnot in midpointKnotsToInsert)
    {
        updatedSurface = insertKnotIntoSurface(updatedSurface, midpointKnot, forUDirection);
    }

    return updatedSurface;
}

/**
 * Inserts a single knot value into a BSplineSurface in either the U or V direction.
 *
 * For U insertion: applies Boehm's algorithm to every V-column independently
 * (all columns share the same U knot vector), then rebuilds the control point matrix.
 *
 * For V insertion: applies Boehm's algorithm to every U-row independently
 * (all rows share the same V knot vector), then rebuilds the control point matrix.
 *
 * @param surface       {map}     : The BSplineSurface map to modify.
 * @param newKnot       {number}  : The knot value to insert.
 * @param forUDirection {boolean} : true = insert into uKnots, false = insert into vKnots.
 * @returns {map} : Updated BSplineSurface map with the new knot and refined control points.
 */
function insertKnotIntoSurface(surface is map, newKnot is number, forUDirection is boolean) returns map
{
    const controlPoints  = surface.controlPoints;
    const nURows         = size(controlPoints);
    const nVColumns      = size(controlPoints[0]);

    var newControlPoints = undefined;
    var newUKnots        = surface.uKnots;
    var newVKnots        = surface.vKnots;

    if (forUDirection)
    {
        // Insert into the U knot vector by processing each V-column.
        // A V-column is the sequence [controlPoints[0][j], controlPoints[1][j], ...] for fixed j.
        const uKnots = surface.uKnots;
        const uDegree = surface.uDegree;

        // All columns produce the same new knot vector; compute it once from column 0.
        const column0 = extractColumn(controlPoints, 0);
        const knotInsertResult0 = insertKnot(column0, uKnots, uDegree, newKnot);
        newUKnots = knotArray(knotInsertResult0.knotVector);

        const newNURows = size(knotInsertResult0.controlPoints);

        // Build the new control point matrix from the updated columns.
        newControlPoints = makeArray(newNURows);
        for (var i = 0; i < newNURows; i += 1)
        {
            newControlPoints[i] = makeArray(nVColumns);
        }

        for (var j = 0; j < nVColumns; j += 1)
        {
            const column = extractColumn(controlPoints, j);
            const result = insertKnot(column, uKnots, uDegree, newKnot);
            for (var i = 0; i < newNURows; i += 1)
            {
                newControlPoints[i][j] = result.controlPoints[i];
            }
        }
    }
    else
    {
        // Insert into the V knot vector by processing each U-row.
        const vKnots  = surface.vKnots;
        const vDegree = surface.vDegree;

        // Compute the new V knot vector from the first row.
        const row0Result = insertKnot(controlPoints[0], vKnots, vDegree, newKnot);
        newVKnots = knotArray(row0Result.knotVector);

        const newNVColumns = size(row0Result.controlPoints);
        newControlPoints   = makeArray(nURows);

        for (var i = 0; i < nURows; i += 1)
        {
            const result = insertKnot(controlPoints[i], vKnots, vDegree, newKnot);
            newControlPoints[i] = result.controlPoints;
        }
    }

    return {
        "uDegree"      : surface.uDegree,
        "vDegree"      : surface.vDegree,
        "isRational"   : surface.isRational,
        "isUPeriodic"  : surface.isUPeriodic,
        "isVPeriodic"  : surface.isVPeriodic,
        "controlPoints" : newControlPoints,
        "weights"       : surface.weights,
        "uKnots"        : newUKnots,
        "vKnots"        : newVKnots
    };
}


// ===========================================================================
// Bezier patch grid extraction
// ===========================================================================

/**
 * Decomposes a BSplineSurface into a 2D grid of cubic Bezier patches via Bezier extraction.
 *
 * Bezier extraction in U:
 *   For every V-column of control points (parameterized by uKnots), insert interior knots
 *   to reach full multiplicity (= uDegree) so the column partitions into cubic Bezier segments.
 *   After extraction, the column data is reorganized so that each U-span owns 4 rows.
 *
 * Bezier extraction in V:
 *   For each of the 4 rows belonging to a U-span, apply the same procedure using vKnots.
 *   Each row then partitions into cubic Bezier segments.
 *
 * The result is a 2D array patchGrid[iU][iV] where each entry is a 4x4 array of 3D
 * length vectors representing the cubic Bezier control polygon for that patch.
 *
 * @param surface    {map} : The BSplineSurface map (must be cubic, non-rational).
 * @param definition {map} : The feature definition (used for diagnostic output).
 * @returns {array} : 2D array patchGrid[iU][iV] of 4x4 control point matrices.
 */
function extractBezierPatchGrid(surface is map, definition is map) returns array
{
    const controlPoints = surface.controlPoints;
    const uKnotsArray   = surface.uKnots;
    const vKnotsArray   = surface.vKnots;
    const uDegree       = surface.uDegree;
    const vDegree       = surface.vDegree;
    const nURows        = size(controlPoints);
    const nVColumns     = size(controlPoints[0]);

    // Step 1: Bezier extraction in U direction.
    // Process each V-column independently; all columns share the same uKnots.
    // Result shape: extractedUColumns[vColumnIndex][uSpanIndex][uPointIndex]
    var extractedUColumns = makeArray(nVColumns);
    for (var j = 0; j < nVColumns; j += 1)
    {
        const column = extractColumn(controlPoints, j);
        extractedUColumns[j] = extractBezierSegments1D(column, uKnotsArray, uDegree);
    }

    const nUSpans = size(extractedUColumns[0]);

    if (definition.enableDiagnostics && definition.diagnosticSpanCounts)
    {
        println("Pseudo T-Spline: U Bezier extraction → " ~ nUSpans ~ " U spans");
    }

    // Step 2: Bezier extraction in V direction for each U-span.
    // For each U-span iU, we have 4 rows (k = 0..3).
    // Row k is: [extractedUColumns[j][iU][k] for j in 0..nVColumns-1]
    // Apply Bezier extraction to each row using vKnots.

    var patchGrid = makeArray(nUSpans);
    for (var iU = 0; iU < nUSpans; iU += 1)
    {
        // Gather the 4 U-rows belonging to this U-span across all V-columns.
        var uSpanRows = makeArray(uDegree + 1);
        for (var k = 0; k <= uDegree; k += 1)
        {
            uSpanRows[k] = makeArray(nVColumns);
            for (var j = 0; j < nVColumns; j += 1)
            {
                uSpanRows[k][j] = extractedUColumns[j][iU][k];
            }
        }

        // Apply Bezier extraction in V to each of the 4 U-rows.
        // All rows share the same vKnots.
        var vSegmentsPerRow = makeArray(uDegree + 1);
        for (var k = 0; k <= uDegree; k += 1)
        {
            vSegmentsPerRow[k] = extractBezierSegments1D(uSpanRows[k], vKnotsArray, vDegree);
        }

        const nVSpans = size(vSegmentsPerRow[0]);

        if (definition.enableDiagnostics && definition.diagnosticSpanCounts && iU == 0)
        {
            println("Pseudo T-Spline: V Bezier extraction → " ~ nVSpans ~ " V spans");
        }

        // Assemble the patch grid row for U-span iU.
        patchGrid[iU] = makeArray(nVSpans);
        for (var iV = 0; iV < nVSpans; iV += 1)
        {
            // Build a 4x4 control point matrix for patch (iU, iV).
            // patchControlPoints[k][l] = l-th control point of V-segment iV from U-row k.
            var patchControlPoints = makeArray(uDegree + 1);
            for (var k = 0; k <= uDegree; k += 1)
            {
                patchControlPoints[k] = vSegmentsPerRow[k][iV];
            }

            if (definition.enableDiagnostics && definition.diagnosticControlPoints)
            {
                println("Pseudo T-Spline: patch (" ~ iU ~ ", " ~ iV ~ ") control points:");
                for (var k = 0; k <= uDegree; k += 1)
                {
                    println("  row " ~ k ~ ": " ~ toString(patchControlPoints[k]));
                }
            }

            patchGrid[iU][iV] = patchControlPoints;
        }
    }

    return patchGrid;
}


// ===========================================================================
// 1D Bezier extraction
// ===========================================================================

/**
 * Decomposes a B-spline curve into its constituent Bezier segments via knot insertion.
 *
 * For each unique interior knot value t in the knot vector, the algorithm inserts t
 * repeatedly until its multiplicity reaches `degree`, using Boehm's single-knot insertion.
 * After all insertions, the control points form a sequence of degree+1 contiguous segments
 * that share single junction points.
 *
 * For a cubic curve (degree = 3) with N spans:
 *   - Total control points after extraction = 3N + 1
 *   - Segment k occupies indices [3k .. 3k+3] (4 points, where 3k = 3(k-1)+3 from previous)
 *
 * @param controlPoints {array}  : 1D array of 3D length Vectors (the curve control polygon).
 * @param knotVector    {array}  : Fully padded knot vector (as stored in the BSplineSurface type).
 * @param degree        {number} : The polynomial degree of the B-spline (must be >= 1).
 * @returns {array} : Array of segment arrays. segments[i] is an array of (degree + 1) Vectors.
 */
function extractBezierSegments1D(controlPoints is array, knotVector is array, degree is number) returns array
{
    // Collect unique interior knot values and the number of additional insertions each requires.
    // Interior knots are those strictly between the first and last (boundary) knot values.
    const startKnot = knotVector[0];
    const endKnot   = knotVector[size(knotVector) - 1];

    var uniqueInteriorKnots = [];
    for (var i = degree; i < size(knotVector) - degree - 1; i += 1)
    {
        const t = knotVector[i];
        if (t <= startKnot + KNOT_TOLERANCE || t >= endKnot - KNOT_TOLERANCE)
        {
            continue;
        }
        var alreadyListed = false;
        for (var listed in uniqueInteriorKnots)
        {
            if (abs(listed - t) < KNOT_TOLERANCE)
            {
                alreadyListed = true;
                break;
            }
        }
        if (!alreadyListed)
        {
            uniqueInteriorKnots = append(uniqueInteriorKnots, t);
        }
    }

    // Insert each interior knot until its multiplicity equals degree.
    var currentControlPoints = controlPoints;
    var currentKnotVector    = knotVector;

    for (var t in uniqueInteriorKnots)
    {
        const currentMultiplicity = countKnotMultiplicity(currentKnotVector, t);
        const insertionsNeeded    = degree - currentMultiplicity;

        for (var k = 0; k < insertionsNeeded; k += 1)
        {
            const result         = insertKnot(currentControlPoints, currentKnotVector, degree, t);
            currentControlPoints = result.controlPoints;
            currentKnotVector    = result.knotVector;
        }
    }

    // Partition the resulting control points into (degree+1)-sized Bezier segments.
    // Segment i occupies indices [i * degree .. i * degree + degree].
    //
    // Correct span count formula: after full Bézier extraction each interior knot has been
    // raised to multiplicity = degree, so `size(controlPoints) - degree` overcounts because
    // it counts each zero-length repeated-knot interval as a separate span. The exact count
    // is uniqueInteriorKnots.size + 1 (one span between each consecutive pair of unique
    // knot values, including the two boundary values).
    const totalSpans = size(uniqueInteriorKnots) + 1;
    var segments     = makeArray(totalSpans);

    for (var spanIndex = 0; spanIndex < totalSpans; spanIndex += 1)
    {
        var segment = makeArray(degree + 1);
        for (var k = 0; k <= degree; k += 1)
        {
            segment[k] = currentControlPoints[spanIndex * degree + k];
        }
        segments[spanIndex] = segment;
    }

    return segments;
}


// ===========================================================================
// Boehm's single knot insertion (Algorithm A5.1, The NURBS Book)
// ===========================================================================

/**
 * Inserts a single new knot into a B-spline curve using Boehm's algorithm.
 *
 * For a curve of degree p with control points P[0..n] and knot vector U[0..n+p+1]:
 *   - Find the knot span r such that U[r] <= newKnot < U[r+1].
 *   - The n+2 new control points Q[i] are:
 *       Q[i] = P[i]                                                   for i <= r - p
 *       Q[i] = alpha_i * P[i] + (1 - alpha_i) * P[i-1]               for r-p+1 <= i <= r
 *       Q[i] = P[i-1]                                                 for i >= r+1
 *     where alpha_i = (newKnot - U[i]) / (U[i+p] - U[i]).
 *
 * @param controlPoints {array}  : 1D array of 3D length Vectors.
 * @param knotVector    {array}  : The current fully padded knot vector (plain numbers).
 * @param degree        {number} : Polynomial degree.
 * @param newKnot       {number} : The knot value to insert.
 * @returns {map} : { controlPoints: array, knotVector: array } — both one element longer.
 */
function insertKnot(controlPoints is array, knotVector is array, degree is number, newKnot is number) returns map
{
    const r = findKnotSpanIndex(knotVector, degree, newKnot);
    const n = size(controlPoints);

    // Build the new control point array (n+1 points).
    var newControlPoints = makeArray(n + 1);

    // Points before the affected region are unchanged.
    for (var i = 0; i <= r - degree; i += 1)
    {
        newControlPoints[i] = controlPoints[i];
    }

    // Points in the blending region.
    for (var i = r - degree + 1; i <= r; i += 1)
    {
        const denominator = knotVector[i + degree] - knotVector[i];
        // When two adjacent knots coincide the associated control point collapses
        // to its upper neighbour (alpha = 1).
        const alpha = (denominator < KNOT_TOLERANCE) ? 1.0 : (newKnot - knotVector[i]) / denominator;
        newControlPoints[i] = alpha * controlPoints[i] + (1.0 - alpha) * controlPoints[i - 1];
    }

    // Points after the affected region shift up by one.
    for (var i = r + 1; i <= n; i += 1)
    {
        newControlPoints[i] = controlPoints[i - 1];
    }

    // Build the new knot vector (one longer than before).
    var newKnotVector = makeArray(size(knotVector) + 1);
    for (var i = 0; i <= r; i += 1)
    {
        newKnotVector[i] = knotVector[i];
    }
    newKnotVector[r + 1] = newKnot;
    for (var i = r + 2; i < size(newKnotVector); i += 1)
    {
        newKnotVector[i] = knotVector[i - 1];
    }

    return { "controlPoints" : newControlPoints, "knotVector" : newKnotVector };
}

/**
 * Finds the index r of the knot span containing parameter value t.
 *
 * The knot span is the largest index r such that knotVector[r] <= t < knotVector[r+1].
 * Special case: when t equals the last knot value, returns the index of the last
 * non-zero-length span to avoid an out-of-range access.
 *
 * Uses binary search for O(log n) performance, matching Algorithm A2.1 in The NURBS Book.
 *
 * @param knotVector {array}  : Fully padded knot vector.
 * @param degree     {number} : Polynomial degree.
 * @param t          {number} : The parameter value to locate.
 * @returns {number} : Index r such that knotVector[r] <= t < knotVector[r+1].
 */
function findKnotSpanIndex(knotVector is array, degree is number, t is number) returns number
{
    // For a B-spline with (n+1) control points and degree p, the knot vector has
    // n+p+2 entries (indices 0..n+p+1).  The index of the last control point is
    // n = size(knotVector) - degree - 2.  The right boundary of the parametric domain
    // is the last entry of the knot vector.
    const lastControlPointIndex = size(knotVector) - degree - 2;
    const endKnot               = knotVector[size(knotVector) - 1];

    // Special case: t is at or beyond the end of the domain.
    // Return the last valid span index so callers can safely access knotVector[r] and knotVector[r+1].
    if (t >= endKnot - KNOT_TOLERANCE)
    {
        return lastControlPointIndex;
    }

    var low  = degree;
    var high = lastControlPointIndex + 1;
    var mid  = floor((low + high) / 2);

    while (t < knotVector[mid] || t >= knotVector[mid + 1])
    {
        if (t < knotVector[mid])
        {
            high = mid;
        }
        else
        {
            low = mid;
        }
        mid = floor((low + high) / 2);
    }

    return mid;
}


// ===========================================================================
// Utility functions
// ===========================================================================

/**
 * Counts the multiplicity of a specific knot value in a knot vector.
 *
 * Multiplicity is defined as the number of times the value appears in the vector,
 * using an absolute tolerance of 1e-14 to handle floating-point representation.
 *
 * @param knotVector {array}  : The knot vector to search.
 * @param t          {number} : The knot value whose multiplicity is being counted.
 * @returns {number} : The number of occurrences of t in knotVector.
 */
function countKnotMultiplicity(knotVector is array, t is number) returns number
{
    var count = 0;
    for (var k in knotVector)
    {
        if (abs(k - t) < KNOT_TOLERANCE)
        {
            count += 1;
        }
    }
    return count;
}

/**
 * Extracts a single V-column from a 2D control point matrix as a 1D array.
 *
 * The column at index j is the sequence of points that varies in U for fixed V = j,
 * i.e., [controlPoints[0][j], controlPoints[1][j], ..., controlPoints[nURows-1][j]].
 *
 * @param controlPoints {array}  : 2D array of 3D length Vectors (ControlPointMatrix).
 * @param columnIndex   {number} : The V-index of the column to extract.
 * @returns {array} : 1D array of Vectors, one per U row.
 */
function extractColumn(controlPoints is array, columnIndex is number) returns array
{
    const nURows = size(controlPoints);
    var column   = makeArray(nURows);
    for (var i = 0; i < nURows; i += 1)
    {
        column[i] = controlPoints[i][columnIndex];
    }
    return column;
}
