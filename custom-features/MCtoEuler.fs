FeatureScript 2737;
import(path : "onshape/std/common.fs", version : "2737.0");

import(path : "df88be8643ff7d84d2720051/4f80fc42ce192ca8e614efee/41dd957a2ecc40be846e951e", version : "cdb27e067dc389f07ef5bab0");
import(path : "b9cc96e40d33ef8f43eb34e4/8071f953eda68cf1dc80fb55/b684b3564b223c2304d75940", version : "82faa11894d7466456a5de48");
export import(path : "b63010b830aad0279f62bbe9", version : "1eabff6f3be0312571cf279d");
RIGHT_EULER::import(path : "1ae8932880da9557c54c796b", version : "a426dd74853287a55d36cf36");

annotation { 
    "Feature Type Name" : "MC to Euler", 
    "Feature Name Template" : "MC to Euler (#angleConvention)",
    "Feature Type Description" : "Find ZYZ Euler angles for transforming a starting, fixed coordinate system to a destination coordinate system.",
    "Icon" : RIGHT_EULER::BLOB_DATA
    }
export const mcToEulerFromUi = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Angle convention", "UIHint": [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.SHOW_LABEL]}
        definition.angleConvention is AngleConvention;

        annotation { "Name" : "Source/Fixed CS", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
        definition.startCs is Query;

        annotation { "Name" : "Destination CS", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
        definition.endCs is Query;

        annotation { "Group Name" : "Display", "Collapsed By Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        {
            annotation { "Name" : "Units", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.displayUnits is AngleDisplayUnits;

            annotation { "Name" : "Precision", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isInteger(definition.precision, DISPLAY_PRECISION_BOUNDS);
        }
    }
    {
        doOneGetEulerAngles(context, id, definition);
    });

function doOneGetEulerAngles(context is Context, id is Id, definition is map)
{
    verify(!isQueryEmpty(context, definition.startCs), "Select a start coordinate system.", { "faultyParameters" : ["startCs"] });
    verify(!isQueryEmpty(context, definition.endCs), "Select a destination coordinate system.", { "faultyParameters" : ["endCs"] });

    const startCs = evMateConnector(context, {
                "mateConnector" : definition.startCs
            });
    const endCs = evMateConnector(context, {
                "mateConnector" : definition.endCs
            });

    const angleSet = getEulerAnglesFromMC(context, id, startCs, endCs, definition.angleConvention);
    verify(angleSet?.alpha != undefined && angleSet?.beta != undefined && angleSet?.gamma != undefined, "At gimbal lock with " ~ keys(angleSet)[0] ~ " at " ~ values(angleSet)[0] ~ ". Unable to determine angle set.");
    const displayString = getDisplayString(context, angleSet, definition.precision, definition.displayUnits);
    const units = AngleDisplayEnumToUnit[definition.displayUnits];
    reportFeatureInfo(context, id, displayString);
}

export function getEulerAnglesFromMC(context is Context, id is Id, startCs is CoordSystem, endCs is CoordSystem, convention is AngleConvention) returns map
{
    //There are many ways to do this. We could keep our expression of all basis vectors in the worldCs or we can transform everything back to
    //the worldCs and then transform it back at the end.
    //I prefer the latter, because then the rotation matrixes are simple to compute and algebraically easy to work with.
    const srcToWorldT = transform(plane(startCs), XY_PLANE);
    const destCsPrime = srcToWorldT * endCs;
    const srcPrime = srcToWorldT * startCs; // should be XY_PLANE
    verify(tolerantEquals(plane(srcPrime), XY_PLANE), "Source transform is incorrect. Expected XY_PLANE, found: " ~ srcPrime);
    const worldToDestPrimeT = transform(XY_PLANE, plane(destCsPrime));
    //The extrinsic Euler angles are NOT W.R.T "worldCs". They are extrinsic with respect to the fixed starting Cs.
    //Now extract angles according to convention.
    const angleSet = getAnglesFromRotationMatrixForConvention(context, worldToDestPrimeT.linear, convention);
    dbg(context, "angles", "alpha: " ~ angleSet?.alpha);
    dbg(context, "angles", "beta: " ~ angleSet?.beta);
    dbg(context, "angles", "gamma: " ~ angleSet?.gamma);
    return angleSet;
}

function getAnglesFromRotationMatrixForConvention(context is Context, rotMatrix is Matrix, convention is AngleConvention)
{
    //the derived algebraic relations are well-documented on wikipedia, as our the trig identities used.
    //We are in a matrix-on-the-left so the transforms are interpreted and solved for that structure.
    //In such a system:
    //T = Ralpha*Rbeta*Rgamma
    //with Rgamma being applied first (rotation around the fixed gamma axis, where specific axes are defined by convention).
    //The euler angles returned will be in the canonical set defined per convention and are thus symmetric with the operation of the eulerToMC featurescript.
    //The algebra depends on the convention, as do the detected gimbal lock states.
    const angleSet = switch (convention)
        {
                //euler
                AngleConvention.ZYZ : getAnglesZYZ(context, rotMatrix),
                AngleConvention.ZXZ : getAnglesZXZ(context, rotMatrix),
                AngleConvention.YZY : getAnglesYZY(context, rotMatrix),
                AngleConvention.YXY : getAnglesYXY(context, rotMatrix),
                AngleConvention.XZX : getAnglesXZX(context, rotMatrix),
                AngleConvention.XYX : getAnglesXYX(context, rotMatrix),
                //tait-bryan
                AngleConvention.XZY : getAnglesXZY(context, rotMatrix),
                AngleConvention.XYZ : getAnglesXYZ(context, rotMatrix),
                AngleConvention.YXZ : getAnglesYXZ(context, rotMatrix),
                AngleConvention.YZX : getAnglesYZX(context, rotMatrix),
                AngleConvention.ZYX : getAnglesZYX(context, rotMatrix),
                AngleConvention.ZXY : getAnglesZXY(context, rotMatrix),
            };
    verify(angleSet != undefined, "Unimplemented convention.");
    return angleSet;
}

//I include this expanded form of the algebra for later readers.
function getAnglesZYZ(context is Context, rotMatrix is Matrix) returns map
{
    // rotMatrix[0][2] = c_alpha * s_beta;
    // rotMatrix[1][2] = s_alpha * s_beta;
    // and we also remember that c_alpha^2 + s_alpha^2 = 1
    // so algebra:
    // r02^2 + r12^2 = (c_alpha * s_beta)^2+(s_alpha * s_beta)^2
    // expand and isolate s_beta:
    // r02^2 + r12^2 = (c_alpha^2 * s_alpha^2) * s_beta^2 = (1) * s_beta^2
    // sqrt(r02^2 + r12^2) = sqrt(s_alpha^2)
    // but here you see _why_ we have to adopt the "beta input [0, 180]".
    // Over the range [-90,+90] cos is always >=0. But the sin computed this way
    // cannot be negative (sqrt). Which means we will produce an angle
    // that is [0-180], even if the input angle was <0.
    // That's why we place the convention on the beta input value - to force the output to stay unambiguous.
    const s_beta = sqrt(rotMatrix[0][2] ^ 2 + rotMatrix[1][2] ^ 2);
    const beta = atan2(s_beta, rotMatrix[2][2]);
    dbg(context, "beta", "s_beta: " ~ s_beta ~ " beta: " ~ beta);
    //if we do the full algebra, we can see that the terms below are all divided by sin(beta).
    //But if beta = 0, then sin(beta) = 0, which will cause these values to explode.
    //So that's where we see gimbal lock manifest algebraically.
    //We see gimbal lock geometrically because the first and third rotation axes are the same, so there is no single solution in this case.
    //As well as being unable to reach certain configurations because we have "lost" a degree of freedom.
    if (tolerantEquals(beta, 0 * degree))
    {
        return { "beta" : beta }; //gimbal lock case
    }
    //also interesting is that because s_beta shows up in both terms, we can just remove it.
    const s_alpha = rotMatrix[1][2]; // / s_beta;
    const c_alpha = rotMatrix[0][2]; // / s_beta;
    const alpha = atan2(s_alpha, c_alpha);
    dbg(context, "alpha", "s_alpha: " ~ s_alpha ~ " c_alpha: " ~ c_alpha ~ " alpha: " ~ alpha);
    const s_gamma = rotMatrix[2][1]; // / s_beta;
    const c_gamma = -rotMatrix[2][0]; // / s_beta;
    const gamma = atan2(s_gamma, c_gamma);

    return {
            "alpha" : alpha,
            "beta" : beta,
            "gamma" : gamma
        };
}

function getAnglesXYX(context is Context, m is Matrix) returns map
{
    return doEulerAngleAlgebra(
        m[0][1], m[0][2], m[0][0],
        m[1][0], -m[2][0],
        m[0][1], m[0][2]
        );
}

function getAnglesYXY(context is Context, m is Matrix) returns map
{
    return doEulerAngleAlgebra(
        m[1][0], -m[1][2], m[1][1],
        m[0][1], m[2][1],
        m[1][0], -m[1][2]
        );
}

function getAnglesZXZ(context is Context, m is Matrix) returns map
{
    return doEulerAngleAlgebra(
        m[0][2], m[1][2], m[2][2],
        m[0][2], -m[1][2],
        m[2][0], m[2][1]
        );
}

function getAnglesYZY(context is Context, m is Matrix) returns map
{
    return doEulerAngleAlgebra(
        m[1][0], m[1][2], m[1][1],
        m[2][1], -m[0][1],
        m[1][2], m[1][0]
        );
}

function getAnglesXZX(context is Context, m is Matrix) returns map
{
    return doEulerAngleAlgebra(
        m[0][2], -m[0][1], m[0][0],
        m[2][0], m[1][0],
        m[0][2], -m[0][1]
        );
}

function doEulerAngleAlgebra(cbs, sbs, bc, sa, ca, sg, cg)
{
    const beta = atan2(sqrt(cbs ^ 2 + sbs ^ 2), bc);
    if (tolerantEquals(beta, 0 * degree))
    {
        return { "beta" : beta }; //gimbal lock case
    }
    const alpha = atan2(sa, ca);
    const gamma = atan2(sg, cg);
    return {
            "alpha" : alpha,
            "beta" : beta,
            "gamma" : gamma
        };
}

function doTaitBryanAngleAlgebra(cbc, cbs, bs, sa, ca, sg, cg)
{
    const beta = atan2(bs, sqrt(cbc ^ 2 + cbs ^ 2));
    if (tolerantEquals(abs(beta), 90 * degree))
    {
        return { "beta" : beta }; //gimbal lock case
    }
    const alpha = atan2(sa, ca);
    const gamma = atan2(sg, cg);
    return {
            "alpha" : alpha,
            "beta" : beta,
            "gamma" : gamma
        };
}

function getAnglesXZY(context is Context, m is Matrix) returns map
{
    return doTaitBryanAngleAlgebra(
        m[0][0], m[0][2], -m[0][1],
        m[2][1], m[1][1],
        m[0][2], m[0][0]
        );
}

function getAnglesXYZ(context is Context, m is Matrix) returns map
{
    return doTaitBryanAngleAlgebra(
        m[0][0], -m[0][1], m[0][2],
        -m[1][2], m[2][2],
        -m[0][1], m[0][0]
        );
}

function getAnglesYXZ(context is Context, m is Matrix) returns map
{
    return doTaitBryanAngleAlgebra(
        m[1][1], m[1][0], -m[1][2],
        m[0][2], m[2][2],
        m[1][0], m[1][1]
        );
}

function getAnglesYZX(context is Context, m is Matrix) returns map
{
    return doTaitBryanAngleAlgebra(
        m[1][1], -m[1][2], m[1][0],
        -m[2][0], m[0][0],
        -m[1][2], m[1][1]
        );
}

function getAnglesZYX(context is Context, m is Matrix) returns map
{
    return doTaitBryanAngleAlgebra(
        m[2][2], m[2][1], -m[2][0],
        m[1][0], m[0][0],
        m[2][1], m[2][2]
        );
}

function getAnglesZXY(context is Context, m is Matrix) returns map
{
    return doTaitBryanAngleAlgebra(
        m[2][2], -m[2][0], m[2][1],
        -m[0][1], m[1][1],
        -m[2][0], m[2][2]
        );
}

function getDisplayString(context is Context, angles is map, precision is number, displayUnits is AngleDisplayUnits)
{
    const units = AngleDisplayEnumToUnit[displayUnits];
    const noDescription = "";
    const alphaInUnits = scalarToString(angles.alpha, units, precision, noDescription);
    const betaInUnits = scalarToString(angles.beta, units, precision, noDescription);
    const gammaInUnits = scalarToString(angles.gamma, units, precision, noDescription);

    const nickname = convertUnitToNickname(units);
    const result = [
            "alpha: ",
            alphaInUnits,
            ", beta: ",
            betaInUnits,
            ", gamma: ",
            gammaInUnits,
            " (" ~ nickname ~ ")",
        ];
    const resultStr = join(result, " ");
    return resultStr;
}
