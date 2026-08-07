
// This custom feature is owned by Evan Reese and distributed by The Onsherpa. It may not be redistributed without permission of the owner. Copywright © 2026 Evan Reese.

FeatureScript 2837;
export import(path : "onshape/std/common.fs", version : "2837.0");
export import(path : "12312312345abcabcabcdeff/a6fb0cf8f4a5191f6485f2f7/0e71899789a4f9231b17a016", version : "9fe08ef797aa5da6e1169033");//modified constrained surface
import(path : "9f4c9835d8018ff7dbdb5683/19b8a1cd32b9e506c26669f7/f937aebd788e8d724a4de67b", version : "1e843593890adcd0071cee82");//Reese Number Utils
import(path : "9f4c9835d8018ff7dbdb5683/19b8a1cd32b9e506c26669f7/c60f4a6e12f07acee647a2a1", version : "e532e824c39a1c21153d1e57");//Reese Container Utils
import(path : "9f4c9835d8018ff7dbdb5683/3cd2a8498da14926f9a4e0c1/7693e3e7037d37a1b5de5daa", version : "7549934bbf80eb5017fb8469");//Reese Debug Utils

//icon::import(path : "aba2f3c2d97998a2a8d9c6d3", version : "7b2763f3db45d7d706ab71db");
//tooltip::import(path : "7fe5e3f0f81675f29f1df87e", version : "4135cc0a4beab9b98c3b5549");


export enum DisplacementSurfaceType
{
    annotation { "Name" : "B-spline Surface" }
    B_SPLINE,
    annotation { "Name" : "Constrained surface" }
    CONSTRAINED_SURFACE,
}

// How the tiling grid density is specified.
export enum TileSpacingMode
{
    annotation { "Name" : "By size" }
    BY_SIZE,
    annotation { "Name" : "By count" }
    BY_COUNT,
}

// Bounds for the image-tiling controls.
const CELL_SIZE_BOUNDS = { (millimeter) : [0.1, 25.0, 1.0e5], (centimeter) : 2.5, (inch) : 1.0 } as LengthBoundSpec;
const TILE_COUNT_BOUNDS = { (unitless) : [1, 3, 500] } as IntegerBoundSpec;

// Hard ceiling on total cells (uCount * vCount) to keep regeneration times sane.
const MAX_TILES = 4000;

// Footprint sample resolution (per axis) used to decide whether a cell overlaps the trimmed face.
const TILE_OVERLAP_SAMPLES = 3;

annotation { "Feature Type Name" : "Displacement map",
        "Feature Type Description" : "Use pixel values to create surface deformations. Useful for texturing parts, modeling topology, and creating lithophanes. The image must be converted to CSV using the free tool at https://theonsherpa.github.io/Image-to-CSV/",
        "Editing Logic Function" : "displacementEditingLogic",
        //"Icon" : icon::BLOB_DATA,
        //"Description Image" : tooltip::BLOB_DATA }
}
export const displacementMap = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Image file (CSV)", "Description" : "To convert your image go to https://theonsherpa.github.io/Image-to-CSV/" }
        definition.imageTable is TableData;

        annotation { "Name" : "Surface to displace", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.face is Query;

        annotation { "Name" : "flip", "UIHint" : ["OPPOSITE_DIRECTION", "FIRST_IN_ROW"] }
        definition.flip is boolean;

        annotation { "Name" : "Reorient secondary axis", "UIHint" : ["MATE_CONNECTOR_AXIS_TYPE", "DISPLAY_SHORT"], "Default" : MateConnectorAxisType.PLUS_X }
        definition.secondaryAxisType is MateConnectorAxisType;

        annotation { "Name" : "Black value" }
        isLength(definition.blackValue, { (millimeter) : [-1e6, 0, 1e6] } as LengthBoundSpec);

        annotation { "Name" : "White value" }
        isLength(definition.whiteValue, { (millimeter) : [-1e6, 1, 1e6] } as LengthBoundSpec);


        // Hidden for now since constrained surface seems to fail too easily to be useful, but we can turn it back on easily this way.
        annotation { "Name" : "Surface type", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.surfaceType is DisplacementSurfaceType;

        if (definition.surfaceType == DisplacementSurfaceType.CONSTRAINED_SURFACE)
        {

            annotation { "Name" : "Tolerance" }
            isLength(definition.tolerance, TOLERANCE_BOUND);

            annotation { "Name" : "Optimize", "UIHint" : UIHint.SHOW_LABEL }
            definition.optimize is OptimizationMethod;
        }

        if (definition.surfaceType == DisplacementSurfaceType.B_SPLINE)
        {
            annotation { "Name" : "Degree" }
            isInteger(definition.degree, { (unitless) : [2, /* min (inclusive) */
                                5, /* default */
                                15 /* max (inclusive) */] } as IntegerBoundSpec);
        }

        annotation { "Name" : "Tile image", "Default" : false,
                    "Description" : "Repeat the image as a unit cell across the face, memoizing identical cells as copies. Planar faces only for now; other surfaces fall back to a single displacement surface." }
        definition.tileImage is boolean;

        if (definition.tileImage)
        {
            annotation { "Group Name" : "Tiling", "Driving Parameter" : "tileImage", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Cell sizing", "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.tileSpacingMode is TileSpacingMode;

                if (definition.tileSpacingMode == TileSpacingMode.BY_SIZE)
                {
                    annotation { "Name" : "Cell size (tile period)", "Description" : "Tile-to-tile repeat distance. With one control point per pixel and a uniform lattice, the pixel pitch is fixed at period/N (N = image pixels along that axis) - there is no separate seam column, so texture scale is fixed and the lattice stays uniform across tile seams." }
                    isLength(definition.cellWidth, CELL_SIZE_BOUNDS);

                    annotation { "Name" : "Lock cell height to image aspect", "Default" : true }
                    definition.lockAspect is boolean;

                    if (!definition.lockAspect)
                    {
                        annotation { "Name" : "Cell height (tile period)" }
                        isLength(definition.cellHeight, CELL_SIZE_BOUNDS);
                    }
                }
                else
                {
                    annotation { "Name" : "Cell columns (across width)" }
                    isInteger(definition.uCount, TILE_COUNT_BOUNDS);

                    annotation { "Name" : "Cell rows (across height)" }
                    isInteger(definition.vCount, TILE_COUNT_BOUNDS);
                }

                annotation { "Name" : "Merge into one surface", "Default" : true,
                            "Description" : "Knit the cells and seam patches into a single surface. Turn off to leave the raw cell and patch bodies separate." }
                definition.mergeTiles is boolean;

                annotation { "Name" : "Debug seam fills", "Default" : false,
                            "Description" : "Print the first gutter's query counts to the console and highlight its target points, resolved faces, and resolved edges for troubleshooting." }
                definition.debugSeams is boolean;
            }
        }

        annotation { "Name" : "Replace face", "Default" : false }
        definition.replaceFace is boolean;

        // if (definition.replaceFace)
        // {
        //     annotation { "Name" : "Flip alignment", "Default" : true}
        //     definition.oppositeSense is boolean;
        // }

        annotation { "Name" : "Use caching", "Description" : "When caching is used, control point positions are not regenerated. This speeds up the feature considerably, but it will not update if you change the base surface unless you click \" Update cache \". (In tiled mode the unit-cell seed is cached.)" }
        definition.useCaching is boolean;

        if (definition.useCaching)
        {
            annotation { "Group Name" : "Use caching", "Driving Parameter" : "useCaching", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Update cache" }
                isButton(definition.updateCache);

                annotation { "Name" : "Warn when out of date", "Description" : "When on, monitors the input face for changes and notifies the user when it is out of date." }
                definition.warnWhenOutOfDate is boolean;

                annotation { "Name" : "Cached face", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isAnything(definition.cachedFace);

                annotation { "Name" : "Cached points", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isAnything(definition.cachedPoints);
            }
        }

        annotation { "Name" : "Show image coordinates" }
        definition.showCoord is boolean;

    }
    {

        const faceIsUpToDate = checkIfFaceIsOutOfDate(definition, context, id);

        if (definition.tileImage)
            buildTiledDisplacement(context, definition, id);
        else
            buildSingleDisplacement(context, definition, id);

        if (definition.replaceFace)
        {
            // Try one orientation, then the other. NOTE: plain try/catch, NOT `try silent` - `try silent` swallows the
            // first failure WITHOUT running the catch, so the opposite-sense retry never fired and the template got
            // deleted anyway, leaving the target unchanged (the long-standing "replace face does nothing" bug).
            var replaced = true;
            try
            {
                opReplaceFace(context, id + "replaceFace1", {
                            "replaceFaces" : definition.face,
                            "templateFace" : qCreatedBy(id, EntityType.FACE),
                            "oppositeSense" : false
                        });
            }
            catch (error)
            {
                try
                {
                    // Distinct id: a thrown op still registers its id, so the retry cannot reuse "replaceFace1".
                    opReplaceFace(context, id + "replaceFace2", {
                                "replaceFaces" : definition.face,
                                "templateFace" : qCreatedBy(id, EntityType.FACE),
                                "oppositeSense" : true
                            });
                }
                catch (error2)
                {
                    replaced = false;
                    reportFeatureWarning(context, id, "Replace face failed (both orientations); leaving the displacement surfaces. First: " ~ error ~ " Second: " ~ error2);
                }
            }

            // Only remove the template surfaces if the replace actually happened, so a failure leaves something visible.
            if (replaced)
                opDeleteBodies(context, id + "deleteBodies1", {
                            "entities" : qCreatedBy(id, EntityType.FACE),
                        });
        }

        if (definition.showCoord)
            showImageCsys(definition, context, id);

    });

function showImageCsys(definition is map, context is Context, id is Id)
{
    var imagePlane = evFaceTangentPlane(context, {
            "face" : definition.face,
            "parameter" : vector(0.5, 0.5)
        });

    if (!definition.flip)
    {
        imagePlane.normal *= -1;
    }

    var planeAxis = line(imagePlane.origin, imagePlane.normal);
    var planeTransform = definition.flip ?
    switch (definition.secondaryAxisType)
        {
                MateConnectorAxisType.PLUS_X : rotationAround(planeAxis, -90 * degree),
                MateConnectorAxisType.PLUS_Y : rotationAround(planeAxis, -180 * degree),
                MateConnectorAxisType.MINUS_X : rotationAround(planeAxis, -270 * degree),
                MateConnectorAxisType.MINUS_Y : rotationAround(planeAxis, 0 * degree)
            } :

    switch (definition.secondaryAxisType)
        {
                MateConnectorAxisType.PLUS_X : rotationAround(planeAxis, 90 * degree),
                MateConnectorAxisType.PLUS_Y : rotationAround(planeAxis, 180 * degree),
                MateConnectorAxisType.MINUS_X : rotationAround(planeAxis, 270 * degree),
                MateConnectorAxisType.MINUS_Y : rotationAround(planeAxis, 0 * degree)
            };

    imagePlane = planeTransform * imagePlane;

    debugCoord(coordSystem(imagePlane), context);
}

function vectorToArray(v)
{
    return [v[0], v[1], v[2]];
}

function normalizeBSplineSurfaceDefinition(bSplineSurface)
{
    var result = bSplineSurface;
    result.controlPoints = mapArray(result.controlPoints, row
        =>mapArray(row, p
            =>p is Vector ? vectorToArray(p) : p
            )
        );
    return result as map;
}


/**
 * Caching strips out the vector designation, so this adds it back.
 */
function rehydrateCachedPointsTable(cachedPointsTable is array) returns array
{
    return mapArray(cachedPointsTable, function(row)
        {
            return mapArray(row, function(vec)
                {
                    return vec as Vector;
                });
        });
}

// The tiled seed is built by FeatureScript arithmetic on mixed units (cell size in inches, face geometry in meters), so its
// Vector components carry verbose unit metadata that serializes far larger than the kernel-returned points the single-
// surface cache stores. Strip each point to plain base-unit (meter) numbers before caching so the stored grid stays compact
// (this is what let a 600x600 single-surface cache fit while a 307 tiled seed overflowed).
function stripSeedForCache(seed is array) returns array
{
    return mapArray(seed, function(row)
        {
            return mapArray(row, function(v)
                {
                    return [v[0] / meter, v[1] / meter, v[2] / meter];
                });
        });
}

// Rebuild the tiled seed from the plain-number form written by stripSeedForCache. Scalar-on-the-left (`meter * vector`) is
// the length-vector idiom this file relies on; `vector * meter` does NOT reliably yield a 3d length vector.
function rehydrateTiledSeed(stored is array) returns array
{
    return mapArray(stored, function(row)
        {
            return mapArray(row, function(a)
                {
                    return meter * vector(a[0], a[1], a[2]);
                });
        });
}

// TODO get this working or comment it out for initial release
function checkIfFaceIsOutOfDate(definition is map, context is Context, id is Id) returns boolean
{
    var result = true;
    if (definition.useCaching && definition.warnWhenOutOfDate)
    {
        // need to strip out all of the type stuff that gets lost in editing logic so it's an apples to apples comparrison.
        const currentFace = evApproximateBSplineSurface(context, { "face" : definition.face }).bSplineSurface->normalizeBSplineSurfaceDefinition().controlPoints as array;

        if (definition.cachedFace != currentFace)
        {
            result = false;
            reportFeatureWarning(context, id, "Input face has been modified. Edit the feature to update the cache.");
        }
    }

    return result;
}

function doConstrainedSurfaceMethod(pointsTable is array, definition is map, id is Id, context is Context)
{
    var constrainedSurfaceDefinition = {};
    constrainedSurfaceDefinition.smooth = definition.optimize == OptimizationMethod.SMOOTH;
    constrainedSurfaceDefinition.tolerance = definition.tolerance;

    constrainedSurfaceDefinition.points = mapArray(concatenateArrays(pointsTable), function(point)
        {
            return { "point" : point };
        });

    opConstrainedSurface(context, id, constrainedSurfaceDefinition);
}

function doBSplineSurfaceMethod(pointsTable is array, definition is map, id is Id, context is Context)
{
    const facePeriodicity = evFacePeriodicity(context, { "face" : definition.face });

    const bSplineSurface = bSplineSurface({
                "uDegree" : definition.degree,
                "vDegree" : definition.degree,
                "isUPeriodic" : facePeriodicity[0],
                "isVPeriodic" : facePeriodicity[1],
                "controlPoints" : controlPointMatrix(pointsTable)
            });

    opCreateBSplineSurface(context, id, {
                "bSplineSurface" : bSplineSurface
            });
}

// Apply the image's flip and secondary-axis rotation, returning the oriented pixel table. Shared by the single-surface
// and tiled paths so both interpret the image identically.
function orientImageTable(definition is map) returns array
{
    var table = definition.imageTable.csvData;

    if (definition.flip)
        table = reverse(table);

    const rotateCount = switch (definition.secondaryAxisType)
        {
                MateConnectorAxisType.PLUS_X : 0,
                MateConnectorAxisType.PLUS_Y : 1,
                MateConnectorAxisType.MINUS_X : 2,
                MateConnectorAxisType.MINUS_Y : 3
            };

    for (var i = 0; i < rotateCount; i += 1)
        table = transposeNestedArray(table)->reverse();

    return table;
}

function createPointsTable(context is Context, definition is map, id is Id) returns array
{
    const table = orientImageTable(definition);

    const rowCount = size(table);
    const columnCount = size(table[0]);

    reportFeatureInfo(context, id, rowCount * columnCount ~ " samples");

    var pointsTable = [];

    for (var i, row in table)
    {
        var pointRow = [];

        for (var j, item in row)
        {

            var facePlane = evFaceTangentPlane(context, {
                    "face" : definition.face,
                    "parameter" : vector(i / (rowCount - 1), j / (columnCount - 1))
                });

            const offset = remap(item, 0, 255, definition.blackValue / meter, definition.whiteValue / meter) * meter;

            facePlane.origin = facePlane.origin + offset * facePlane.normal;

            pointRow = append(pointRow, facePlane.origin);
        }

        pointsTable = append(pointsTable, pointRow);
    }

    return pointsTable;
}

// ============================ Single-surface (original) path ============================

function buildSingleDisplacement(context is Context, definition is map, id is Id)
{
    var pointsTable;
    if (definition.useCaching && definition.cachedPoints != 0)
        pointsTable = rehydrateCachedPointsTable(definition.cachedPoints);
    else
        pointsTable = createPointsTable(context, definition, id);

    if (definition.surfaceType == DisplacementSurfaceType.CONSTRAINED_SURFACE)
        doConstrainedSurfaceMethod(pointsTable, definition, id + "constrainedSurface", context);

    if (definition.surfaceType == DisplacementSurfaceType.B_SPLINE)
        doBSplineSurfaceMethod(pointsTable, definition, id + "bSplineSurface", context);
}

// ================================= Tiled (unit-cell) path =================================

function buildTiledDisplacement(context is Context, definition is map, id is Id)
{
    const surfaceType = evSurfaceDefinition(context, { "face" : definition.face, "returnBSplinesAsOther" : true }).surfaceType;

    if (surfaceType == SurfaceType.PLANE)
    {
        buildPlanarTiledDisplacement(context, definition, id);
    }
    else
    {
        // HOOK: cylinder tiling uses rigid rotate-about-axis + translate-along-axis copies (with wrap handling for
        //       full cylinders); cone / sphere / freeform fall back to per-cell evFaceTangentPlane sampling with no
        //       memoized copies. Until those land, non-planar faces fall back to a single displacement surface.
        reportFeatureInfo(context, id, "Image tiling currently supports planar faces only; building a single displacement surface for this face.");
        buildSingleDisplacement(context, definition, id);
    }
}

// Cheap layout: the face frame, tile periods/pitches, grid counts, and degree. Recomputed each regen (a handful of geometry
// evals); the EXPENSIVE seed extraction is kept separate (tiledSeedFromLayout) so it can be cached.
function tiledLayout(context is Context, definition is map) returns map
{
    const table = orientImageTable(definition);
    const imgRows = size(table);       // image pixel rows (height)
    const imgCols = size(table[0]);    // image pixel columns (width)

    // In-plane frame: rows follow the face U tangent, columns the perpendicular in-plane direction; displacement follows
    // the face normal.
    const centerPlane = evFaceTangentPlane(context, { "face" : definition.face, "parameter" : vector(0.5, 0.5) });
    const normal = centerPlane.normal;
    const rowDir = normalize(centerPlane.x);
    const colDir = normalize(cross(normal, rowDir));

    // Measure the face extent in that frame (x = colDir, y = rowDir, so the z axis is -normal) to lay out the grid.
    const gridCsys = coordSystem(centerPlane.origin, colDir, -1 * normal);
    const faceBox = evBox3d(context, { "topology" : definition.face, "cSys" : gridCsys, "tight" : true });
    const spanC = faceBox.maxCorner[0] - faceBox.minCorner[0];
    const spanR = faceBox.maxCorner[1] - faceBox.minCorner[1];
    const gridAnchor = centerPlane.origin + faceBox.minCorner[0] * colDir + faceBox.minCorner[1] * rowDir;

    // The cell-size input is the tile PERIOD. With one control point per pixel and a uniform lattice the pixel pitch is
    // period/N - no separate seam column - so the lattice stays uniform across tile seams (which makes the memoized pieces
    // geometrically congruent) and the texture scale is fixed.
    const grid = resolvePlanarGrid(definition, imgRows, imgCols, spanC, spanR);

    return {
            "table" : table,
            "imgRows" : imgRows,
            "imgCols" : imgCols,
            "normal" : normal,
            "rowDir" : rowDir,
            "colDir" : colDir,
            "gridAnchor" : gridAnchor,
            "periodC" : grid.periodC,
            "periodR" : grid.periodR,
            "pitchC" : grid.periodC / imgCols,
            "pitchR" : grid.periodR / imgRows,
            "uCount" : grid.uCount,
            "vCount" : grid.vCount,
            // Degree is honored in both directions; clamp so each axis has enough pixels to carry it.
            "degree" : min(min(definition.degree, imgRows - 1), imgCols - 1)
        };
}

// The expensive step: build ONE seed tile by knot-refinement (Boehm) extraction of the conceptual uniform-knot surface S
// whose control grid is the image tiled infinitely. The window reads only one period plus `degree` wrapped neighbours per
// edge, so we never instantiate the giant surface. The extracted clamped tile reproduces S exactly on its sub-domain, so
// the patterned copies meet it with S's own C^(degree-1) continuity - genuine curvature continuity for degree >= 3 - and
// never overshoot (every seed CP is a convex combination of real pixels). See docs/specs/DISPLACEMENT_MAP_TILING_SPEC.md.
// FUTURE (blur): a wrap-aware Gaussian blur of the seam-band pixel heights slots into buildTileWindow, before the
// lift/extract, to soften seams on images that do not tile cleanly. This is the exact (blur-off) path.
function tiledSeedFromLayout(layout is map, definition is map) returns array
{
    const window = buildTileWindow(layout.gridAnchor, layout.colDir, layout.rowDir, layout.normal, layout.pitchC, layout.pitchR, layout.table, definition.blackValue, definition.whiteValue, layout.degree);
    return refineTileSeed(window, layout.degree, layout.imgRows, layout.imgCols);
}

function buildPlanarTiledDisplacement(context is Context, definition is map, id is Id)
{
    const layout = tiledLayout(context, definition);
    const imgRows = layout.imgRows;
    const imgCols = layout.imgCols;
    const normal = layout.normal;
    const rowDir = layout.rowDir;
    const colDir = layout.colDir;
    const gridAnchor = layout.gridAnchor;
    const periodC = layout.periodC;
    const periodR = layout.periodR;
    const pitchC = layout.pitchC;
    const pitchR = layout.pitchR;
    const uCount = layout.uCount;
    const vCount = layout.vCount;
    const degree = layout.degree;

    // Use the cached seed when present (displacementEditingLogic stores it, compacted, at edit time); otherwise extract it
    // now via the optimized weight-table path.
    const usedCache = definition.useCaching && definition.cachedPoints != 0;
    const seedCP = usedCache
        ? rehydrateTiledSeed(definition.cachedPoints)
        : tiledSeedFromLayout(layout, definition);

    if (definition.debugSeams)
    {
        const dbg = "[dispMap] image " ~ imgRows ~ "x" ~ imgCols ~ " px, degree " ~ degree ~ ", seed CP grid " ~ size(seedCP) ~ "x" ~ size(seedCP[0])
            ~ ", pitch " ~ (pitchC / millimeter) ~ "/" ~ (pitchR / millimeter) ~ " mm, period " ~ (periodC / millimeter) ~ "/" ~ (periodR / millimeter) ~ " mm"
            ~ (usedCache ? " [seed from cache]" : " [seed built]");
        println(dbg);
        reportFeatureInfo(context, id, dbg);
    }

    // Keep cells whose period footprint overlaps the (possibly trimmed) face; drop cells entirely off it.
    var kept = makeArray(uCount, 0);
    var keptCount = 0;
    for (var tc = 0; tc < uCount; tc += 1)
    {
        var col = makeArray(vCount, false);
        for (var tr = 0; tr < vCount; tr += 1)
        {
            if (cellOverlapsFace(context, definition.face, gridAnchor, colDir, rowDir, tc, tr, periodC, periodR))
            {
                col[tr] = true;
                keptCount += 1;
            }
        }
        kept[tc] = col;
    }

    if (keptCount == 0)
        throw regenError("No image cells overlap the selected face. Check the cell size and the face selection.", ["face"]);

    // One real seed tile at the grid origin; the identical copies are memoized as rigid-translation pattern instances.
    createBSplinePatch(context, id + "seedCell", seedCP, degree, degree);

    var transforms = [];
    var instanceNames = [];
    for (var tc = 0; tc < uCount; tc += 1)
    {
        for (var tr = 0; tr < vCount; tr += 1)
        {
            if (!kept[tc][tr] || (tc == 0 && tr == 0))
                continue;   // the origin cell is the pattern seed itself
            transforms = append(transforms, transform(tc * periodC * colDir + tr * periodR * rowDir));
            instanceNames = append(instanceNames, "cell_" ~ tc ~ "_" ~ tr);
        }
    }

    if (size(transforms) > 0)
        opPattern(context, id + "cellPattern", {
                    "entities" : qCreatedBy(id + "seedCell", EntityType.BODY),
                    "transforms" : transforms,
                    "instanceNames" : instanceNames
                });

    // If the origin cell isn't on the face, it only existed as the pattern seed - remove it.
    if (!kept[0][0])
        opDeleteBodies(context, id + "deleteSeed", { "entities" : qCreatedBy(id + "seedCell", EntityType.BODY) });

    // Knit only when we actually need a single-body output. When replaceFace is on it is redundant: opReplaceFace sews the
    // coincident-edge patchwork itself, and pre-booleaning just relocates (and slightly increases) that cost. Skipping it
    // here lets replaceFace do the one sew instead of paying for it twice.
    if (definition.mergeTiles && !definition.replaceFace)
    {
        try
        {
            opBoolean(context, id + "knit", {
                        "tools" : qCreatedBy(id, EntityType.BODY),
                        "operationType" : BooleanOperationType.UNION
                    });
        }
        catch (error)
        {
            reportFeatureWarning(context, id, "Could not knit cells into a single surface (leaving separate bodies): " ~ error);
        }
    }

    reportFeatureInfo(context, id, keptCount ~ " cells (" ~ uCount ~ " x " ~ vCount ~ " grid), pitch " ~ (pitchC / millimeter) ~ " mm, degree " ~ degree ~ ".");
}

// Resolve the tile period (repeat distance) and grid counts. The cell-size input IS the period; the pixel pitch is
// derived elsewhere as period/N (N = image pixels along that axis), so the texture scale is fixed.
function resolvePlanarGrid(definition is map, imgRows is number, imgCols is number, spanC is ValueWithUnits, spanR is ValueWithUnits) returns map
{
    var periodC;
    var periodR;
    var uCount;
    var vCount;

    if (definition.tileSpacingMode == TileSpacingMode.BY_SIZE)
    {
        periodC = definition.cellWidth;
        // Lock aspect so the pixel pitch matches in both directions: periodR/imgRows == periodC/imgCols.
        periodR = definition.lockAspect ? definition.cellWidth * (imgRows / imgCols) : definition.cellHeight;
        uCount = max(1, ceil(spanC / periodC));
        vCount = max(1, ceil(spanR / periodR));
    }
    else
    {
        uCount = definition.uCount;
        vCount = definition.vCount;
        periodC = spanC / uCount;
        periodR = spanR / vCount;
    }

    if (uCount * vCount > MAX_TILES)
        throw regenError("Too many image cells (" ~ (uCount * vCount) ~ "). The maximum is " ~ MAX_TILES ~ ". Increase the cell size or reduce the cell count.",
                definition.tileSpacingMode == TileSpacingMode.BY_SIZE ? ["cellWidth"] : ["uCount", "vCount"]);

    return { "periodC" : periodC, "periodR" : periodR, "uCount" : uCount, "vCount" : vCount };
}

// Proper positive modulo (FeatureScript % can return a negative remainder for negative operands).
function positiveMod(a is number, n is number) returns number
{
    var r = a % n;
    if (r < 0)
        r += n;
    return r;
}

// One period of the conceptual infinite tiled control grid PLUS `degree` wrapped neighbours on every edge, as world-space
// control-point positions (grayscale height along the normal, uniform pitch in-plane). Pixel heights are read with `mod`
// wrapping, so this only ever touches one image period, never the whole tiled grid. window[i][j]: down a column (i) is U
// (rows), across a row (j) is V (cols). Global index of window[i][j] is (i - degree, j - degree); heights wrap while the
// in-plane positions run linearly through the window (window[i + N] = window[i] + period, which is what makes the
// extracted tiles congruent under translation).
// FUTURE (blur): apply the wrap-aware Gaussian blur to the seam-band pixel heights HERE, before lifting to 3D.
function buildTileWindow(anchor is Vector, colDir is Vector, rowDir is Vector, normal is Vector, pitchC is ValueWithUnits, pitchR is ValueWithUnits, table is array, blackValue is ValueWithUnits, whiteValue is ValueWithUnits, degree is number) returns array
{
    const nRows = size(table);
    const nCols = size(table[0]);
    const winRows = nRows + 2 * degree + 1;
    const winCols = nCols + 2 * degree + 1;

    // The in-plane column offset (gj + 0.5)*pitchC*colDir depends only on the column, so precompute it once and reuse it
    // for every row instead of rebuilding it ~winRows times.
    var colStep = makeArray(winCols, colDir);
    for (var wj = 0; wj < winCols; wj += 1)
        colStep[wj] = (wj - degree + 0.5) * pitchC * colDir;    // gj = wj - degree

    // Preallocate rows and assign by index - `append` in a loop is O(n^2) (it copies the growing array each call).
    var window = makeArray(winRows, 0);
    for (var wi = 0; wi < winRows; wi += 1)
    {
        const gi = wi - degree;                    // global row index (may be < 0 or >= nRows)
        const ti = positiveMod(gi, nRows);         // wrapped image row
        const rowBase = anchor + (gi + 0.5) * pitchR * rowDir;   // in-plane origin of this window row
        const tableRow = table[ti];
        var row = makeArray(winCols, anchor);
        for (var wj = 0; wj < winCols; wj += 1)
        {
            const tj = positiveMod(wj - degree, nCols);   // wrapped image column
            const offset = remap(tableRow[tj], 0, 255, blackValue / meter, whiteValue / meter) * meter;
            row[wj] = rowBase + colStep[wj] + offset * normal;
        }
        window[wi] = row;
    }
    return window;
}

// Elementwise a*arrA + b*arrB over coefficient rows (used only while building the extraction weight table).
function axpy(a is number, arrA is array, b is number, arrB is array) returns array
{
    var out = makeArray(size(arrA), 0);
    for (var i = 0; i < size(arrA); i += 1)
        out[i] = a * arrA[i] + b * arrB[i];
    return out;
}

// The clamped extraction of one period from a UNIFORM degree-`degree` B-spline of M = n + 2*degree + 1 control points is a
// FIXED linear map: it depends only on (degree, n), not on the control-point values, and it is the SAME map for every row
// and every column. So we compute it ONCE as a sparse weight table and reuse it, instead of re-running knot insertion per
// row/column - the difference between a few dozen insertions and several thousand. result[o] = list of { "idx", "w" }
// giving output control point o as a weighted sum of the M inputs (most are a single identity term, w = 1).
//
// The knot arithmetic here is Boehm single knot insertion (validate vs docs/specs/DISPLACEMENT_MAP_TILING_SPEC.md section 5.3): insert
// the domain-start knot `degree` times and the one-period-later knot `degree` times to clamp both ends, then slice out the
// piece. Running it on unit-basis coefficient rows makes the accumulated coefficients BE the weights. Extracted interior
// knots stay uniform, so opCreateBSplineSurface's default clamped-uniform knots match - no explicit knot vector needed.
function extractionWeights(degree is number, n is number) returns array
{
    const M = n + 2 * degree + 1;
    var cp = makeArray(M, 0);              // each entry is a length-M coefficient row (unit basis to start)
    for (var i = 0; i < M; i += 1)
    {
        var e = makeArray(M, 0);
        e[i] = 1;
        cp[i] = e;
    }
    var knots = [];
    for (var i = 0; i < M + degree + 1; i += 1)
        knots = append(knots, i);          // uniform 0, 1, 2, ...
    const a = degree;                      // tile's first seam parameter (domain start)
    const b = n + degree;                  // one period later (tile's second seam parameter)

    for (var pass = 0; pass < 2; pass += 1)
    {
        const ubar = pass == 0 ? a : b;
        for (var rep = 0; rep < degree; rep += 1)   // raise to multiplicity degree+1 -> clamps that end
        {
            var k = -1;
            for (var i = 0; i < size(knots) - 1; i += 1)
                if (knots[i] <= ubar && ubar < knots[i + 1])
                {
                    k = i;
                    break;
                }

            const nn = size(cp);
            var q = makeArray(nn + 1, 0);
            for (var i = 0; i <= nn; i += 1)
            {
                if (i <= k - degree)
                    q[i] = cp[i];
                else if (i >= k + 1)
                    q[i] = cp[i - 1];
                else
                {
                    const denom = knots[i + degree] - knots[i];
                    const alpha = denom == 0 ? 0 : (ubar - knots[i]) / denom;
                    q[i] = axpy(alpha, cp[i], 1 - alpha, cp[i - 1]);
                }
            }
            cp = q;

            var newKnots = [];
            for (var i = 0; i <= k; i += 1)
                newKnots = append(newKnots, knots[i]);
            newKnots = append(newKnots, ubar);
            for (var i = k + 1; i < size(knots); i += 1)
                newKnots = append(newKnots, knots[i]);
            knots = newKnots;
        }
    }

    var ia = -1;
    for (var i = 0; i < size(knots); i += 1)
        if (knots[i] == a)
        {
            ia = i;
            break;
        }
    var ibLast = -1;
    for (var i = 0; i < size(knots); i += 1)
        if (knots[i] == b)
            ibLast = i;

    const cpCount = (ibLast - ia + 1) - (degree + 1);
    var weights = makeArray(cpCount, 0);
    for (var o = 0; o < cpCount; o += 1)
    {
        const coeffs = cp[ia + o];
        var sparse = [];
        for (var j = 0; j < M; j += 1)
            if (abs(coeffs[j]) > 1e-12)
                sparse = append(sparse, { "idx" : j, "w" : coeffs[j] });
        weights[o] = sparse;
    }
    return weights;
}

// Apply a precomputed sparse extraction weight table to one row of control-point Vectors -> the extracted Vectors. The
// clamped extraction leaves interior control points unchanged, so most outputs are a single identity term (w = 1) and copy
// straight through with no arithmetic; only the ~degree points near each end are genuine blends.
function applyExtraction(weights is array, row is array) returns array
{
    var out = makeArray(size(weights), row[0]);
    for (var o = 0; o < size(weights); o += 1)
    {
        const terms = weights[o];
        if (size(terms) == 1 && terms[0].w == 1)
        {
            out[o] = row[terms[0].idx];        // identity - pure copy
        }
        else
        {
            var acc = terms[0].w * row[terms[0].idx];
            for (var t = 1; t < size(terms); t += 1)
                acc = acc + terms[t].w * row[terms[t].idx];
            out[o] = acc;
        }
    }
    return out;
}

// Tensor-product extraction of one clamped seed tile from the window: extract along V (columns) for every window row, then
// along U (rows) for every resulting column. Result is an (nRows + degree) x (nCols + degree) control net that reproduces
// the conceptual tiled surface exactly over one tile; the patterned copies therefore meet it with C^(degree-1) continuity.
// Each axis's extraction is a fixed linear map, so it is computed ONCE (extractionWeights) and applied cheaply to every
// row/column - this is what keeps the one-time seed build fast. The copies are then rigid translations (the perf win).
function refineTileSeed(window is array, degree is number, nRows is number, nCols is number) returns array
{
    const wCol = extractionWeights(degree, nCols);   // one-time map: window row -> (nCols + degree) CPs
    var inter = makeArray(size(window), 0);
    for (var i = 0; i < size(window); i += 1)
        inter[i] = applyExtraction(wCol, window[i]);

    const wRow = extractionWeights(degree, nRows);   // one-time map: window column -> (nRows + degree) CPs
    const interRows = size(inter);         // nRows + 2*degree + 1
    const interCols = size(inter[0]);      // nCols + degree
    var seed = makeArray(nRows + degree, 0);
    for (var r = 0; r < nRows + degree; r += 1)
        seed[r] = makeArray(interCols, inter[0][0]);   // placeholder, overwritten below

    for (var j = 0; j < interCols; j += 1)
    {
        var colVec = makeArray(interRows, inter[0][0]);
        for (var i = 0; i < interRows; i += 1)
            colVec[i] = inter[i][j];
        const extracted = applyExtraction(wRow, colVec);   // length nRows + degree
        for (var r = 0; r < nRows + degree; r += 1)
            seed[r][j] = extracted[r];
    }
    return seed;
}

// Create a non-periodic B-spline sheet body from a control-point grid.
function createBSplinePatch(context is Context, id is Id, cp is array, uDegree is number, vDegree is number)
{
    opCreateBSplineSurface(context, id, {
                "bSplineSurface" : bSplineSurface({
                            "uDegree" : uDegree,
                            "vDegree" : vDegree,
                            "isUPeriodic" : false,
                            "isVPeriodic" : false,
                            "controlPoints" : controlPointMatrix(cp)
                        })
            });
}

// True if any point sampled across the cell's period footprint lands on the trimmed face.
function cellOverlapsFace(context is Context, face is Query, gridAnchor is Vector, colDir is Vector, rowDir is Vector, tc is number, tr is number, periodC is ValueWithUnits, periodR is ValueWithUnits) returns boolean
{
    const base = gridAnchor + tc * periodC * colDir + tr * periodR * rowDir;
    for (var i = 0; i < TILE_OVERLAP_SAMPLES; i += 1)
    {
        const fi = TILE_OVERLAP_SAMPLES > 1 ? i / (TILE_OVERLAP_SAMPLES - 1) : 0.5;
        for (var j = 0; j < TILE_OVERLAP_SAMPLES; j += 1)
        {
            const fj = TILE_OVERLAP_SAMPLES > 1 ? j / (TILE_OVERLAP_SAMPLES - 1) : 0.5;
            if (!isQueryEmpty(context, qContainsPoint(face, base + fj * periodC * colDir + fi * periodR * rowDir)))
                return true;
        }
    }
    return false;
}

export function displacementEditingLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, specifiedParameters is map, hiddenBodies is Query, clickedButton is string) returns map
{

    if (definition.useCaching)
    {
        // Tiled mode caches the compacted unit-cell seed; single-surface mode caches the full displaced point table.
        if (definition.tileImage)
            definition.cachedPoints = stripSeedForCache(tiledSeedFromLayout(tiledLayout(context, definition), definition));
        else
            definition.cachedPoints = createPointsTable(context, definition, id);

        if (definition.cachedPoints == 0 || clickedButton == "updateCache" || (!oldDefinition.useCaching && definition.useCaching))
        {
            definition.cachedFace = evApproximateBSplineSurface(context, { "face" : definition.face }).bSplineSurface->normalizeBSplineSurfaceDefinition().controlPoints as array;
        }
    }

    return definition;
}

