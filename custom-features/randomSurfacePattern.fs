/*
    Random Surface Pattern

    Scatters copies of a "stamp" body across a selected face on a jittered UV grid, then
    optionally booleans them into (or out of) the face's owner body. Each instance is randomly
    perturbed in position and cut depth, driven by a seedable pseudo-random generator, so a given
    seed always reproduces the same layout.

    Grid density can be specified two ways:
        By count  - a fixed number of instances across the face's U and V parameter directions
                    (the original behavior; spacing then depends on how big the face is).
        By length - a target spacing (length between instances). The feature measures the face's
                    physical span along U and V and derives the counts, so the on-part spacing is
                    consistent regardless of face size or parameterization.

    Credits:
        - The pseudo-random number generator (lcprng) was written by Ilya Baran.
        - The original surface pattern generator was written by maximilian.schommer@students.olin.edu;
          most of the grid/placement logic is his.

        1.0 - Original surface pattern generator.
        2.0 - Updated to FeatureScript 3029 conventions; added length-based (unit-length) density mode.
*/

FeatureScript 3029;
import(path : "onshape/std/geometry.fs", version : "3029.0");

// The lcprng generator yields numbers in roughly [0, 20]; dividing by upperValue maps them to
// roughly [0, 1] for use as a normalized fraction. (Preserved from the original for seed stability.)
const upperValue = 20;

// Hard ceiling on total instances (uCount * vCount) to keep regeneration times sane.
const MAX_INSTANCES = 25000;

// Number of segments sampled along each mid-isocurve when measuring physical U/V spans.
const UV_SAMPLES = 16;

const ANGLE_90_BOUNDS =
{
            (degree) : [0, 0, 90]
        } as AngleBoundSpec;

const CUT_DEPTH_BOUNDS =
{
            (inch) : [-100, 0, 100]
        } as LengthBoundSpec;

const COUNT_BOUNDS =
{
            (unitless) : [1, 5, 500]
        } as IntegerBoundSpec;

const PERCENT_BOUNDS =
{
            (unitless) : [0, 0, 100]
        } as IntegerBoundSpec;

const SEED_BOUNDS =
{
            (unitless) : [-999, 0, 999]
        } as IntegerBoundSpec;

// Target distance between instances when using length-based density.
const SPACING_BOUNDS =
{
            (millimeter) : [0.1, 25.0, 1.0e5],
            (centimeter) : 2.5,
            (inch) : 1.0
        } as LengthBoundSpec;

export enum BoolOpts
{
    annotation { "Name" : "Subtract" }
    SUBTRACT,
    annotation { "Name" : "Add" }
    ADD,
    annotation { "Name" : "New Bodies" }
    NEW_BODIES
}

// How the grid density is specified.
export enum SpacingMode
{
    annotation { "Name" : "By count" }
    COUNT,
    annotation { "Name" : "By length (spacing)" }
    DENSITY
}

annotation { "Feature Type Name" : "Random Surface Pattern",
        "Feature Type Description" : "Scatters copies of a stamp body across a face on a jittered " ~
        "UV grid, with random position and cut-depth variation from a seedable generator. Grid " ~
        "density can be set by count, or by a target spacing (length) that is normalized to the " ~
        "face's real size so the pattern spacing stays consistent across faces." }
export const surfacePattern = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Group Name" : "Pattern", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Select Face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
            definition.Face is Query;

            annotation { "Name" : "Select Pattern Part", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1,
                        "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.patternPart is Query;

            annotation { "Name" : "Select Mate Connector", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1,
                        "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.mateConnector is Query;

            annotation { "Name" : "Pattern Part Angle", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isAngle(definition.obAngle, ANGLE_90_BOUNDS);

            annotation { "Name" : "Boolean", "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
            definition.bool is BoolOpts;
        }

        annotation { "Group Name" : "Grid", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Density", "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
            definition.spacingMode is SpacingMode;

            if (definition.spacingMode == SpacingMode.COUNT)
            {
                annotation { "Name" : "U count", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isInteger(definition.uCount, COUNT_BOUNDS);

                annotation { "Name" : "V count", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isInteger(definition.vCount, COUNT_BOUNDS);
            }
            else
            {
                annotation { "Name" : "U spacing", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.uSpacing, SPACING_BOUNDS);

                annotation { "Name" : "V spacing", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.vSpacing, SPACING_BOUNDS);
            }

            annotation { "Name" : "Grid Angle", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isAngle(definition.gridAngle, ANGLE_90_BOUNDS);

            annotation { "Name" : "Max X/Y Random Percent", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isInteger(definition.maxXYrandom, PERCENT_BOUNDS);

            annotation { "Name" : "Seed", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isInteger(definition.rNumSeed, SEED_BOUNDS);
        }

        annotation { "Group Name" : "Cut depth", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Cut Depth Min", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.cutDepthMin, CUT_DEPTH_BOUNDS);

            annotation { "Name" : "Cut Depth Max", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.cutDepthMax, CUT_DEPTH_BOUNDS);
        }
    }
    {
        // Resolve the grid counts: either taken directly, or derived from a target spacing by
        // measuring the face's real U/V span (unit-length density normalization).
        var uCount = 1;
        var vCount = 1;
        if (definition.spacingMode == SpacingMode.DENSITY)
        {
            const spans = measureFaceUVLengths(context, definition.Face);
            uCount = max(1, round(spans.u / definition.uSpacing));
            vCount = max(1, round(spans.v / definition.vSpacing));
            reportFeatureInfo(context, id, "Length density resolved to " ~ uCount ~ " x " ~ vCount ~ " instances.");
        }
        else
        {
            uCount = definition.uCount;
            vCount = definition.vCount;
        }

        if (uCount * vCount > MAX_INSTANCES)
        {
            throw regenError("Maximum instance count (" ~ MAX_INSTANCES ~ ") exceeded - reduce the count or increase the spacing.",
                    definition.spacingMode == SpacingMode.DENSITY ? ["uSpacing", "vSpacing"] : ["uCount", "vCount"]);
        }

        const rNum = lcprng(definition.rNumSeed);
        const coordGrid = genPointsAndNormals(context, definition.Face, uCount, vCount, definition.gridAngle, definition.maxXYrandom, rNum);
        const gridSize = 2 * max(uCount, vCount);

        createBodyPattern(context, id, coordGrid, gridSize, definition, rNum);
    });

// Approximate the face's physical span along each normalized UV parameter axis by summing straight
// segments between points sampled along the mid-isocurves (v = 0.5 for the U span, u = 0.5 for the
// V span). Used to convert a target spacing into an instance count. Approximate on curved faces,
// which is fine for a scatter pattern.
function measureFaceUVLengths(context is Context, face is Query) returns map
{
    var uLen = 0 * meter;
    var vLen = 0 * meter;

    var prevU = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0, 0.5) }).origin;
    var prevV = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0) }).origin;

    for (var i = 1; i <= UV_SAMPLES; i += 1)
    {
        const t = i / UV_SAMPLES;
        const curU = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(t, 0.5) }).origin;
        const curV = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, t) }).origin;
        uLen += norm(curU - prevU);
        vLen += norm(curV - prevV);
        prevU = curU;
        prevV = curV;
    }

    return { "u" : uLen, "v" : vLen };
}

// Builds a (2*max(uCount,vCount)) square grid of coordinate systems on the face. Grid points are
// laid out in normalized UV parameter space, jittered by up to maxXY percent of a cell, rotated by
// the grid angle, then kept only if they still land inside the [0,1] x [0,1] parameter box. Cells
// with no valid point are left as 0.
function genPointsAndNormals(context is Context, surface is Query, uCount is number, vCount is number,
    angle is ValueWithUnits, maxXY is number, rNum is function) returns array
{
    const gridSize = 2 * max(uCount, vCount);
    var grid = makeArray(gridSize, makeArray(gridSize, 0));

    const rotMatrix = gen2dRotMat(-angle);

    const uDenom = uCount + 1 / (2 * uCount);
    const vDenom = vCount + 1 / (2 * vCount);

    for (var u = 0; u < gridSize; u += 1)
    {
        for (var v = 0; v < gridSize; v += 1)
        {
            // PRNG call order is preserved from the original so a given seed reproduces the same
            // layout: two draws pick each jitter sign, one draw scales each magnitude.
            const xSign = rNum() > rNum() ? 1 : -1;
            const ySign = rNum() > rNum() ? 1 : -1;
            const xDir = (maxXY / 100) * (xSign * ((rNum() / upperValue) / uDenom));
            const yDir = (maxXY / 100) * (ySign * ((rNum() / upperValue) / vDenom));

            var param = vector((-1 + u / uDenom) + xDir, (v / vDenom) + yDir);
            param = rotMatrix * param;

            if (param[0] > 0 && param[0] < 1 && param[1] > 0 && param[1] < 1)
            {
                const tanPlane = evFaceTangentPlane(context, { "face" : surface, "parameter" : param });
                grid[u][v] = coordSystem(tanPlane.origin, tanPlane.x, tanPlane.normal);
            }
        }
    }

    return grid;
}

// Places one stamp per valid grid point that actually lies on the trimmed face, offset into the
// surface by a random cut depth, then booleans per the selected operation.
function createBodyPattern(context is Context, id is Id, coordGrid is array, gridSize is number, definition is map, rNum is function)
{
    const c1 = evMateConnector(context, { "mateConnector" : definition.mateConnector });
    const rotTransform = rotationAround(line(c1.origin, c1.zAxis), definition.obAngle);

    const minCut = min(definition.cutDepthMin, definition.cutDepthMax);
    const maxCut = max(definition.cutDepthMin, definition.cutDepthMax);
    const cutDiff = maxCut - minCut;

    var transforms = [];
    var instanceNames = [];

    for (var u = 0; u < gridSize; u += 1)
    {
        for (var v = 0; v < gridSize; v += 1)
        {
            var cSys = coordGrid[u][v];
            if (cSys == 0)
                continue;

            // Skip points whose UV location is inside the parameter box but off the trimmed face.
            if (isQueryEmpty(context, qContainsPoint(definition.Face, cSys.origin)))
                continue;

            const thisCut = ((rNum() / upperValue) * cutDiff) + minCut;
            cSys.origin = cSys.origin - normalize(cSys.zAxis) * thisCut;

            transforms = append(transforms, transformByMateConnector(c1, cSys) * rotTransform);
            instanceNames = append(instanceNames, "stamp" ~ u ~ "_" ~ v);
        }
    }

    if (size(transforms) == 0)
        throw regenError("No pattern instances landed on the selected face - check the face selection, the counts/spacing, and the grid angle.", ["Face"]);

    opPattern(context, id + "pattern1", {
                "entities" : definition.patternPart,
                "transforms" : transforms,
                "instanceNames" : instanceNames
            });

    if (definition.bool != BoolOpts.NEW_BODIES)
    {
        if (definition.bool == BoolOpts.ADD)
        {
            opBoolean(context, id + "boolean1", {
                        "tools" : qUnion([qOwnerBody(definition.Face), qCreatedBy(id + "pattern1", EntityType.BODY)]),
                        "operationType" : BooleanOperationType.UNION
                    });
        }
        else // SUBTRACT
        {
            opBoolean(context, id + "boolean1", {
                        "tools" : qCreatedBy(id + "pattern1", EntityType.BODY),
                        "targets" : qOwnerBody(definition.Face),
                        "operationType" : BooleanOperationType.SUBTRACTION
                    });
        }
    }
}

// Transform that maps mate connector c1 onto the target coordinate system c2.
function transformByMateConnector(c1 is CoordSystem, c2 is CoordSystem) returns Transform
{
    return toWorld(c2) * inverse(toWorld(c1));
}

function gen2dRotMat(angle is ValueWithUnits) returns Matrix
{
    var rotMatrix = zeroMatrix(2, 2);
    rotMatrix[0][0] = cos(angle);
    rotMatrix[0][1] = -sin(angle);
    rotMatrix[1][0] = sin(angle);
    rotMatrix[1][1] = cos(angle);
    return rotMatrix;
}

// Seedable linear-congruential pseudo-random generator by Ilya Baran. Returns a closure that
// yields a new value in roughly [0, 20] on each call.
function lcprng(seed is number) returns function
{
    const a = 1103515245;
    const c = 12345;
    const m = 2 ^ 31;
    var state = new box(seed);
    return function()
        {
            state[] = ((a * state[] + c) % m) / 1e8;
            return state[];
        };
}
