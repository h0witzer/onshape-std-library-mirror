FeatureScript 2737;
import(path : "onshape/std/common.fs", version : "2737.0");
export import(path : "b63010b830aad0279f62bbe9", version : "1eabff6f3be0312571cf279d");
EULER::import(path : "102af5632e474e72d44cd0c7", version : "7801b5f9ef5cf99580be984e");

type EulerAngleHandler typecheck canBeEulerAngleHandler;

predicate canBeEulerAngleHandler(value)
{
    value is map;
    value.angleToMatrix is function;
    value.isCanonical is function;
    value.atGimbalLock is function;
}

const EULER_AXIS_TO_DIR = {
        EulerAxis.X : X_DIRECTION,
        EulerAxis.Y : Y_DIRECTION,
        EulerAxis.Z : Z_DIRECTION,
    };

function eulerAxisToDir(axis is EulerAxis) returns Vector
{
    return EULER_AXIS_TO_DIR[axis];
}

export const SYMMETRIC_360_ACROSS_ZERO =
{
            (degree) : [-180, 0.0, 180],
        } as AngleBoundSpec;

annotation {
        "Feature Type Name" : "Euler to MC",
        "Feature Name Template" : "Euler (#angleConvention) to MC",
        "Feature Type Description" : "Creates a mate connector at an orientation specified by Euler angles with respect to the fixed starting coordinate system.",
        "Icon" : EULER::BLOB_DATA
    }
export const eulerToMCFromUi = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Convention", "UIHint": [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.SHOW_LABEL], "Default": AngleConvention.XYZ }
        definition.angleConvention is AngleConvention;

        annotation { "Name" : "Start coordinate system", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
        definition.startFixedCs is Query;

        annotation { "Name" : "(Optional) Destination origin vertex", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
        definition.destOrigin is Query;

        //these ranges are too broad for any particular convention, so detecting canonical sets is handled
        //in logic not in ui.
        annotation { "Name" : "Alpha (1st intrinsic rotation angle)" }
        isAngle(definition.alpha, SYMMETRIC_360_ACROSS_ZERO);

        annotation { "Name" : "Beta (2nd intrinsic rotation angle)" }
        isAngle(definition.beta, SYMMETRIC_360_ACROSS_ZERO);

        annotation { "Name" : "Gamma (3rd intrinsic rotation angle)" }
        isAngle(definition.gamma, SYMMETRIC_360_ACROSS_ZERO);

        annotation { "Name" : "Save rotation matrix as variable" }
        definition.doSaveMatrix is boolean;
        if (definition.doSaveMatrix)
        {
            annotation { "Name" : "Name", "UIHint" : [UIHint.UNCONFIGURABLE, UIHint.VARIABLE_NAME], "MaxLength" : 10000 }
            definition.name is string;
        }
    }
    {
        eulerToMc(context, id, definition);
    });

function eulerToMc(context is Context, id is Id, definition is map)
{
    verify(!isQueryEmpty(context, definition.startFixedCs), "Select a starting coordinate system mate connector.", { "faultyParameters" : ["startFixedCs"] });
    const handler = eulerAngleHandler(definition.angleConvention);
    const angleCheck = handler.isCanonical(definition.alpha, definition.beta, definition.gamma);
    verify(angleCheck.alpha, "Alpha is not canonical for this convention: " ~ angleCheck.alphaBounds, { "faultyParameters" : ["alpha"] });
    verify(angleCheck.beta, "Beta is not canonical for this convention: " ~ angleCheck.betaBounds, { "faultyParameters" : ["beta"] });
    verify(angleCheck.gamma, "Gamma is not canonical for this convention: " ~ angleCheck.gammaBounds, { "faultyParameters" : ["gamma"] });
    verify(!handler.atGimbalLock(definition.alpha, definition.beta, definition.gamma), "At gimbal lock. Unable to compute valid tranform matrix.");

    //so now we know we have a canonical set of angles
    const startCs = evMateConnector(context, {
                "mateConnector" : definition.startFixedCs
            });
    //compute the transform from the euler angles, create a mate connector at the startCs, then transform it.
    const rotationMatrix = handler.angleToMatrix(definition.alpha, definition.beta, definition.gamma);
    const rotationOnlyTransform = transform(rotationMatrix, vector(0, 0, 0) * meter);
    const startTransform = transform(plane(startCs), XY_PLANE);
    //we are in a matrix-on-the-left, so transforms are applied right to left.
    //We are applying the Euler angle matrix to the world coordinate system so that the extrinsic angles work.
    //then transforming the destCs back to make it relative to the startTransform.
    //we could have just applied the partial transforms successively but I like this better.
    //This also handles the translation to and from the origin.
    var destCs = inverse(startTransform) * rotationOnlyTransform * WORLD_COORD_SYSTEM;
    if (!isQueryEmpty(context, definition.destOrigin))
    {
        const destOriginWCS = evVertexPoint(context, {
                    "vertex" : definition.destOrigin
                });
        destCs.origin = destOriginWCS;
    }

    opMateConnector(context, id + "destMc", {
                "coordSystem" : destCs,
                "owner" : qNothing()
            });

    if (definition.doSaveMatrix)
    {
        verifyName(definition.name, "name");
        publishVariableValue(definition.name, context, id, rotationMatrix, "");
    }
}

//Gimbal lock checks and canonical checks are the same within "Euler" and "Tait-Bryan" convention sets,
//But the logic across conventions is easier to express if we return these as a set of functions.
function eulerAngleHandler(convention is AngleConvention) returns EulerAngleHandler
{
    const isCanonicalEuler = function(alpha, beta, gamma)
        {
            return isCanonical([-PI, PI], [0, PI], [-PI, PI], alpha, beta, gamma);
        };
    const isCanonicalTaitBryan = function(alpha, beta, gamma)
        {
            return isCanonical([-PI, PI], [-PI / 2, PI / 2], [-PI, PI], alpha, beta, gamma);
        };
    const isAtGimbalLockEuler = function(alpha, beta, gamma)
        {
            return tolerantEquals(beta, 0 * degree);
        };
    const isAtGimbalLockTaitBryan = function(alpha, beta, gamma)
        {
            return tolerantEquals(abs(beta), 90 * degree);
        };

    if (convention == AngleConvention.ZYZ)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.Z, EulerAxis.Y, EulerAxis.Z, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalEuler(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockEuler(alpha, beta, gamma),
                } as EulerAngleHandler;
    }

    if (convention == AngleConvention.XYX)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.X, EulerAxis.Y, EulerAxis.X, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalEuler(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockEuler(alpha, beta, gamma),
                } as EulerAngleHandler;
    }

    if (convention == AngleConvention.YZY)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.Y, EulerAxis.Z, EulerAxis.Y, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalEuler(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockEuler(alpha, beta, gamma),
                } as EulerAngleHandler;
    }
    if (convention == AngleConvention.XZX)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.X, EulerAxis.Z, EulerAxis.X, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalEuler(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockEuler(alpha, beta, gamma),
                } as EulerAngleHandler;
    }
    if (convention == AngleConvention.YXY)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.Y, EulerAxis.X, EulerAxis.Y, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalEuler(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockEuler(alpha, beta, gamma),
                } as EulerAngleHandler;
    }
    if (convention == AngleConvention.ZXZ)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.Z, EulerAxis.X, EulerAxis.Z, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalEuler(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockEuler(alpha, beta, gamma),
                } as EulerAngleHandler;
    }

    //TAIT-BRYAN
    //By analogy with 'proper' Euler angles, we see all the alpha and gamma calcs are divided by cos(beta).
    //So these terms explode (algebraic gimbal lock) when beta = +/- 90 degrees (cos 90/-90 = 0)
    if (convention == AngleConvention.XZY)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.X, EulerAxis.Z, EulerAxis.Y, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalTaitBryan(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockTaitBryan(alpha, beta, gamma),
                } as EulerAngleHandler;
    }
    if (convention == AngleConvention.XYZ)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.X, EulerAxis.Y, EulerAxis.Z, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalTaitBryan(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockTaitBryan(alpha, beta, gamma),
                } as EulerAngleHandler;
    }
    if (convention == AngleConvention.YXZ)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.Y, EulerAxis.X, EulerAxis.Z, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalTaitBryan(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockTaitBryan(alpha, beta, gamma),
                } as EulerAngleHandler;
    }
    if (convention == AngleConvention.YZX)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.Y, EulerAxis.Z, EulerAxis.X, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalTaitBryan(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockTaitBryan(alpha, beta, gamma),
                } as EulerAngleHandler;
    }
    if (convention == AngleConvention.ZYX)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.Z, EulerAxis.Y, EulerAxis.X, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalTaitBryan(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockTaitBryan(alpha, beta, gamma),
                } as EulerAngleHandler;
    }
    if (convention == AngleConvention.ZXY)
    {
        return {
                    "angleToMatrix" : (alpha, beta, gamma)
                    =>angleToMatrix(EulerAxis.Z, EulerAxis.X, EulerAxis.Y, alpha, beta, gamma),
                    "isCanonical" : (alpha, beta, gamma)
                    =>isCanonicalTaitBryan(alpha, beta, gamma),
                    "atGimbalLock" : (alpha, beta, gamma)
                    =>isAtGimbalLockTaitBryan(alpha, beta, gamma),
                } as EulerAngleHandler;
    }

    throw "Unimplemented!";
}

function angleToMatrix(firstIntrinsicAxis is EulerAxis, secondIntrinsicAxis is EulerAxis, thirdIntrinsicAxis is EulerAxis, alpha is ValueWithUnits, beta is ValueWithUnits, gamma is ValueWithUnits) returns Matrix
{
    //euler angles are defined in terms of intrinsic axes, axes of the body.
    //but it's easier to use fixed, extrinsic axes of the starting coordinate system
    //which by definition are the basis vectors expressed in the starting coordinate system,
    //so x is [1,0,0], y is [0,1,0].Obviously it's easy to make rotation axes around these fixed extrinsic axes.
    //The only 'trick' is that these produce equivalent matrixes provides they are applied in the opposite order.
    //This is why the order is reversed. The caller thinks in terms of intrinsic but the function calculates in extrinsic.
    const gammaRotation = rotationMatrix3d(eulerAxisToDir(thirdIntrinsicAxis), gamma);
    const betaRotation = rotationMatrix3d(eulerAxisToDir(secondIntrinsicAxis), beta);
    const alphaRotation = rotationMatrix3d(eulerAxisToDir(firstIntrinsicAxis), alpha);
    const finalRotation = alphaRotation * betaRotation * gammaRotation;
    return finalRotation;
}

function isCanonical(alphaRange is array, betaRange is array, gammaRange is array, alpha is ValueWithUnits, beta is ValueWithUnits, gamma is ValueWithUnits) returns map
{
    var alphaInBounds = withinBounds(alpha, alphaRange);
    var betaInBounds = withinBounds(beta, betaRange);
    var gammaInBounds = withinBounds(gamma, alphaRange);
    return canonicalMap(alphaInBounds, betaInBounds, gammaInBounds, alphaRange, betaRange, gammaRange);
}

function withinBounds(test is ValueWithUnits, bounds is array) returns boolean
{
    verify(size(bounds) == 2, "Bounds improperly defined.");
    const min = bounds[0] * radian;
    const max = bounds[1] * radian;
    return tolerantLessThanOrEqual(min, test) && tolerantGreaterThanOrEqual(max, test);
}

function canonicalMap(alpha is boolean, beta is boolean, gamma is boolean, alphaBounds is array, betaBounds is array, gammaBounds is array) returns map
{
    return {
            "alpha" : alpha,
            "beta" : beta,
            "gamma" : gamma,
            "isCanonical" : alpha && beta && gamma,
            "alphaBounds" : alphaBounds,
            "betaBounds" : betaBounds,
            "gammaBounds" : gammaBounds
        };
}

function verifyName(name is string, faultyParameter is string)
{
    verify(length(name) < 10000, ErrorStringEnum.VARIABLE_NAME_TOO_LONG, {"faultyParameters" : [faultyParameter]});
    const replaceNameWithRegExpShouldBeBlank = replace(name, '[a-zA-Z_][a-zA-Z_0-9]*', '');
    verify(name != '' && replaceNameWithRegExpShouldBeBlank == '', ErrorStringEnum.VARIABLE_NAME_INVALID, {"faultyParameters" : [faultyParameter]});
}

function publishVariableValue(name is string, context is Context, id is Id, value, description)
{
    setVariable(context, name, value, (description is string) ? description : "");
    const quotedValue = (value is string) ? ('"' ~ value ~ '"') : value;
    setFeatureComputedParameter(context, id, { "name" : "value", "value" : quotedValue });
}
