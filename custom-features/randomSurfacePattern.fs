FeatureScript 1095;
import(path : "onshape/std/geometry.fs", version : "1095.0");

const upperValue = 20;

// This custom feature makes use of a pseudo-random number generator written by Ilya Baran.
// I have absolutely no idea how it works, other than for a given seed value, it generates a number between 0 and 20
// I am using upperValue to convert this output to a number between 0 and 100

// The original surface pattern generator was written by maximilian.schommer@students.olin.edu - most of this code is his.

const ANGLE_90_BOUNDS = {
            (degree) : [0, 0, 90]
        } as AngleBoundSpec;

const CUT_DEPTH_BOUNDS = {
            (inch) : [-100, 0, 100]
        } as LengthBoundSpec;

const COUNT_BOUNDS = {
            (unitless) : [1, 5, 500]
        } as IntegerBoundSpec;

const PERCENT_BOUNDS = {
            (unitless) : [0, 0, 100]
        } as IntegerBoundSpec;

const SEED_NUM = {
            (unitless) : [-999, 0, 999]
        } as IntegerBoundSpec;


export enum BoolOpts
{
    annotation { "Name" : "Subtract" }
    SUBTRACT,
    annotation { "Name" : "Add" }
    ADD,
    annotation { "Name" : "New Bodies" }
    NEW_BODIES
}





annotation { "Feature Type Name" : "Random Surface Pattern" }
export const surfacePattern = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Select Face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.Face is Query;

        annotation { "Name" : "Boolean" }
        definition.bool is BoolOpts;

        annotation { "Name" : "Select Pattern Part", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        definition.patternPart is Query;

        annotation { "Name" : "Select Mate Connector", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
        definition.mateConnector is Query;

        annotation { "Name" : "Pattern Part Angle" }
        isAngle(definition.obAngle, ANGLE_90_BOUNDS);

        annotation { "Name" : "Cut Depth Min" }
        isLength(definition.cutDepthMin, CUT_DEPTH_BOUNDS);

        annotation { "Name" : "Cut Depth Max" }
        isLength(definition.cutDepthMax, CUT_DEPTH_BOUNDS);

        annotation { "Name" : "U count" }
        isInteger(definition.uCount, COUNT_BOUNDS);

        annotation { "Name" : "V count" }
        isInteger(definition.vCount, COUNT_BOUNDS);

        annotation { "Name" : "Grid Angle" }
        isAngle(definition.gridAngle, ANGLE_90_BOUNDS);

        annotation { "Name" : "Max X/Y Random Percent" }
        isInteger(definition.maxXYrandom, PERCENT_BOUNDS);

        annotation { "Name" : "Any Number" }
        isInteger(definition.rNumSeed, SEED_NUM);
    }

    {
        if (definition.uCount * definition.vCount > 2500)
        {
            throw regenError("Maximum Instance Count Exeeded");
        }

        var rNum = lcprng(definition.rNumSeed);

        var coordMatrix = genPointsAndNormals(id, context, definition.Face, definition.uCount, definition.vCount, definition.gridAngle, definition.maxXYrandom, rNum);

        var matSize = [2 * max(definition.uCount, definition.vCount), 2 * max(definition.uCount, definition.vCount)];

        createBodyPattern(id, context, definition.patternPart, definition.mateConnector, coordMatrix, matSize, definition, definition.cutDepthMin, definition.cutDepthMax, rNum);

    });


function genPointsAndNormals(id is Id, context is Context, surface, uCount, vCount, angle, maxXY, rNum is function)
{
    var surfCoordSysArray = zeroMatrix(2 * max(uCount, vCount), 2 * max(uCount, vCount));
    debug(context, matrixSize(surfCoordSysArray));
    var tanPlane = 0;
    var arrayPoint = vector(0, 0);
    const rotMatrix = gen2dRotMat(-angle);

    var uDenom = uCount + 1 / (2 * uCount);
    var vDenom = vCount + 1 / (2 * vCount);

    for (var u = 0; u < (2 * max(uCount, vCount)); u += 1)
    {
        for (var v = 0; v < (2 * max(uCount, vCount)); v += 1)
        {

            var xDir = rNum() > rNum() ? 1 : -1;
            var yDir = rNum() > rNum() ? 1 : -1;
            xDir = (maxXY / 100) * (xDir * ((rNum() / upperValue) / uDenom));
            yDir = (maxXY / 100) * (yDir * ((rNum() / upperValue) / vDenom));

            arrayPoint[0] = (-1 + (u) / uDenom) + xDir;
            arrayPoint[1] = (v / vDenom) + yDir;

            arrayPoint = rotMatrix * arrayPoint;

            if ((arrayPoint[0] > 0) && (arrayPoint[0] < 1) && (arrayPoint[1] > 0) && (arrayPoint[1] < 1))
            {
                tanPlane = evFaceTangentPlane(context, {
                            "face" : surface,
                            "parameter" : arrayPoint
                        });

                surfCoordSysArray[u][v] = coordSystem(tanPlane.origin, tanPlane.x, tanPlane.normal);
            }
        }
    }

    return surfCoordSysArray;
}

function createBodyPattern(id is Id, context is Context, baseBody, mateConnector, coordMatrix, matSize, definition, cutDepthMin, cutDepthMax, rNum is function)
{

    var loopCounter = 0;
    var transform = [];
    var instanceNames = [];
    const c1 = evMateConnector(context, {
                "mateConnector" : mateConnector
            });

    const rotTransform = rotationAround(line(c1.origin, c1.zAxis), definition.obAngle);

    var minCut = cutDepthMin < cutDepthMax ? cutDepthMin : cutDepthMax;
    var maxCut = cutDepthMax > cutDepthMin ? cutDepthMax : cutDepthMin;
    var cutDiff = maxCut - minCut;

    for (var u = 0; u < matSize[0]; u += 1)
    {
        for (var v = 0; v < matSize[1]; v += 1)
        {
            if (coordMatrix[u][v] == 0)
            {
                continue;
            }

            var onSurface = qContainsPoint(definition.Face, coordMatrix[u][v].origin);
            if (evaluateQuery(context, onSurface) == evaluateQuery(context, qNothing()))
            {
                println("not on surface");
                continue;
            }

            loopCounter = loopCounter + 1;

            var thisCut = ((rNum() / upperValue) * cutDiff) + minCut;

            coordMatrix[u][v].origin = coordMatrix[u][v].origin - normalize(coordMatrix[u][v].zAxis) * thisCut;

            transform = resize(transform, loopCounter, transformByMateConnector(context, id, c1, coordMatrix[u][v]) * rotTransform);
            instanceNames = resize(instanceNames, loopCounter, "stamp" ~ u ~ "_" ~ v);
        }
    }

    opPattern(context, id + "pattern1", {
                "entities" : baseBody,
                "transforms" : transform,
                "instanceNames" : instanceNames
            });

    if (definition.bool != BoolOpts.NEW_BODIES)
    {
        var boolType = BooleanOperationType.SUBTRACTION;
        var boolDef = {
            "tools" : qCreatedBy(id + "pattern1", EntityType.BODY),
            "targets" : qOwnerBody(definition.Face),
            "operationType" : boolType,
        };

        if (definition.bool == BoolOpts.ADD)
        {
            boolType = BooleanOperationType.UNION;
            boolDef = {
                    "tools" : qUnion([qOwnerBody(definition.Face), qCreatedBy(id + "pattern1", EntityType.BODY)]),
                    "operationType" : boolType,
                };
        }


        opBoolean(context, id + "boolean1", boolDef);
    }



}

function mirrorPointByAngle(context, angle, point)
{
    var mirrorVec = vector(1, 0);

    mirrorVec = gen2dRotMat(angle) * mirrorVec;
    var reflectedVec = 2 * dot(point, mirrorVec) / dot(mirrorVec, mirrorVec) * mirrorVec - point;
    return reflectedVec;
}


function transformByMateConnector(context is Context, id is Id, c1, c2)
{
    var xAxis = c2.xAxis;
    var zAxis = c2.zAxis;

    const A = toWorld(c1);
    const B = toWorld(coordSystem(c2.origin, xAxis, zAxis));
    var transformMatrix = (B * inverse(A));
    return transformMatrix;
}

function gen2dRotMat(angle)
{
    var rotMatrix is Matrix = zeroMatrix(2, 2);
    rotMatrix[0][0] = cos(angle);
    rotMatrix[0][1] = -1 * sin(angle);
    rotMatrix[1][0] = sin(angle);
    rotMatrix[1][1] = cos(angle);

    return rotMatrix;
}


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
