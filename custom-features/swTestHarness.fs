FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

/**
 * Shared scaffolding for the solid sweep test features (spec section 12).
 *
 * Everything here had two or more copies across the sweep modules and their testers. The
 * verdict block was the worst of it - twenty-two sites building the same PASS/FAIL string,
 * printing it under their own tag, and reporting it - and the motion fixture was the widest,
 * with four copies of one integration.
 *
 * Two check-recording conventions live here on purpose. reportTestVerdict takes the
 * (checks, failures) pair the existing tests already accumulate, so they lose their verdict
 * boilerplate without touching a single assertion. New tests use the tally functions instead,
 * which count failures as well as checks and so can report "3 of 47" rather than a
 * concatenation whose length is the only clue to how much went wrong.
 *
 * Same-document imports of this tab need only the TAB ID to be right: Onshape populates the
 * version field automatically on commit with the tab's latest microversion, so the placeholder
 * the offline sources carry there is never worth hand-editing.
 */

// ===================== Verdict reporting =====================

/**
 * Prints and reports the standard verdict: passSummary when failures is empty, otherwise
 * "FAIL:" followed by the accumulated notes. testName becomes the console tag.
 */
export function reportTestVerdict(context is Context, id is Id, testName is string,
    failures is string, passSummary is string)
{
    const verdict = failures == "" ? ("PASS: " ~ passSummary) : ("FAIL:" ~ failures);
    println("[" ~ testName ~ "] VERDICT: " ~ verdict);
    reportFeatureInfo(context, id, verdict);
}

/** As above, with the check count folded into the PASS line. */
export function reportTestVerdict(context is Context, id is Id, testName is string,
    checks is number, failures is string, passSummary is string)
{
    reportTestVerdict(context, id, testName, failures, checks ~ " checks - " ~ passSummary);
}

// ===================== Check tallies (the convention for new tests) =====================

/** An empty check tally. */
export function newCheckTally() returns map
{
    return { "checks" : 0, "failures" : 0, "notes" : "" };
}

/**
 * Records one check. A failure appends its message to the notes; a pass costs nothing but the
 * count, so a test can assert unconditionally and still report how much it verified.
 */
export function checkThat(tally is map, passed is boolean, failureMessage is string) returns map
{
    return {
            "checks" : tally.checks + 1,
            "failures" : tally.failures + (passed ? 0 : 1),
            "notes" : passed ? tally.notes : (tally.notes ~ " " ~ failureMessage)
        };
}

/** Records one check on a magnitude: passes when worst is within tolerance. */
export function checkWithin(tally is map, worst is number, tolerance is number,
    description is string) returns map
{
    return checkThat(tally, abs(worst) <= tolerance,
        description ~ " is " ~ worst ~ ", over the " ~ tolerance ~ " tolerance.");
}

/** Prints and reports a tally's verdict, with the failed-of-total count on a failure. */
export function reportCheckTally(context is Context, id is Id, testName is string,
    tally is map, passSummary is string)
{
    const verdict = tally.failures == 0 ?
        ("PASS: " ~ tally.checks ~ " checks - " ~ passSummary) :
        ("FAIL (" ~ tally.failures ~ " of " ~ tally.checks ~ " checks):" ~ tally.notes);
    println("[" ~ testName ~ "] VERDICT: " ~ verdict);
    reportFeatureInfo(context, id, verdict);
}

// ===================== Shared numeric helpers =====================

/**
 * The worst deviation of three rotation columns from orthonormality:
 * max |dot(ci, cj) - delta_ij|.
 */
export function orthonormalityDefect(columns is array) returns number
{
    var defect = abs(dot(columns[0], columns[0]) - 1);
    defect = max(defect, abs(dot(columns[1], columns[1]) - 1));
    defect = max(defect, abs(dot(columns[2], columns[2]) - 1));
    defect = max(defect, abs(dot(columns[0], columns[1])));
    defect = max(defect, abs(dot(columns[0], columns[2])));
    defect = max(defect, abs(dot(columns[1], columns[2])));
    return defect;
}

/** True when two coefficient arrays have the same size and agree elementwise within tolerance. */
export function coefficientsNear(actual is array, expected is array, tolerance is number) returns boolean
{
    if (size(actual) != size(expected))
    {
        return false;
    }
    for (var index = 0; index < size(actual); index += 1)
    {
        if (abs(actual[index] - expected[index]) > tolerance)
        {
            return false;
        }
    }
    return true;
}

/**
 * Reattaches meters to an array of unitless control points (for handing a module-side spline
 * to kernel-facing std functions).
 */
export function attachMeters(unitlessPoints is array) returns array
{
    var withUnits = makeArray(size(unitlessPoints), 0);
    for (var index = 0; index < size(unitlessPoints); index += 1)
    {
        withUnits[index] = unitlessPoints[index] * meter;
    }
    return withUnits;
}

// ===================== Shared motion fixtures =====================

export function identityRotationRows() returns array
{
    return [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
}

export function zeroRows() returns array
{
    return [[0, 0, 0], [0, 0, 0], [0, 0, 0]];
}

/** A motion station in the shape swEnvelopeMath evaluateMotionSample returns. */
export function constantMotionSample(rotationRows is array, rotationDerivativeRows is array,
    translationDerivative is Vector) returns map
{
    return {
            "rotation" : matrix(rotationRows),
            "rotationDerivative" : matrix(rotationDerivativeRows),
            "rotationSecondDerivative" : matrix(zeroRows()),
            "translation" : vector(0, 0, 0),
            "translationDerivative" : translationDerivative,
            "translationSecondDerivative" : vector(0, 0, 0)
        };
}

/**
 * A single-span cubic translation motion with identity rotation whose VELOCITY is the given
 * degree-2 Bernstein polynomial per component ([x0,x1,x2], [y...], [z...]): control points
 * integrate as P_{k+1} = P_k + q_k / 3 from the origin.
 */
export function translationMotionFromQuadraticVelocity(velocityX is array, velocityY is array,
    velocityZ is array, knots is array) returns map
{
    var controlPoints = makeArray(4);
    controlPoints[0] = vector(0, 0, 0);
    for (var k = 0; k < 3; k += 1)
    {
        controlPoints[k + 1] = controlPoints[k] + vector(velocityX[k], velocityY[k], velocityZ[k]) / 3;
    }
    var columns = makeArray(3);
    for (var columnIndex = 0; columnIndex < 3; columnIndex += 1)
    {
        var axis = vector(columnIndex == 0 ? 1 : 0, columnIndex == 1 ? 1 : 0, columnIndex == 2 ? 1 : 0);
        columns[columnIndex] = {
                "degree" : 3, "knots" : knots, "isRational" : false,
                "controlPoints" : [axis, axis, axis, axis]
            };
    }
    return {
            "columnX" : columns[0], "columnY" : columns[1], "columnZ" : columns[2],
            "translation" : { "degree" : 3, "knots" : knots, "isRational" : false, "controlPoints" : controlPoints }
        };
}

/** The single-span case of the above: knots [0, 0, 0, 0, 1, 1, 1, 1] over t in [0, 1]. */
export function translationMotionFromQuadraticVelocity(velocityX is array, velocityY is array,
    velocityZ is array) returns map
{
    return translationMotionFromQuadraticVelocity(velocityX, velocityY, velocityZ,
        [0, 0, 0, 0, 1, 1, 1, 1]);
}

/**
 * The constant-velocity case of the above over t in [0, 1]: the derivative is exactly
 * velocity everywhere. Unit-stripped, meters implied, matching the records extraction
 * produces.
 */
export function constantVelocityTranslationMotion(velocity is Vector) returns map
{
    return translationMotionFromQuadraticVelocity(
        [velocity[0], velocity[0], velocity[0]],
        [velocity[1], velocity[1], velocity[1]],
        [velocity[2], velocity[2], velocity[2]]);
}

// ===================== Shared surface fixtures =====================

/**
 * The island fixture: S = (u, v, z) with z = 0.8 (u^2/2 - u^3/3) v(1 - v), so that
 * z_u = 0.8 u(1-u) v(1-v) peaks at exactly 0.05 at the patch center. Degrees (3, 2),
 * single Bezier patch, non-rational.
 */
export function islandFixtureSurface() returns map
{
    const uCoefficients = [0, 0, 1 / 6, 1 / 6];
    const vCoefficients = [0, 0.5, 0];
    const greville3 = [0, 1 / 3, 2 / 3, 1];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(4);
    for (var i = 0; i < 4; i += 1)
    {
        var row = makeArray(3);
        for (var j = 0; j < 3; j += 1)
        {
            row[j] = vector(greville3[i], greville2[j], 0.8 * uCoefficients[i] * vCoefficients[j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 3, "vDegree" : 2,
            "uKnots" : [0, 0, 0, 0, 1, 1, 1, 1], "vKnots" : [0, 0, 0, 1, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The slant fixture: S = (u, v, 0.15 u^2 - 0.18 u + 0.05 u v), degrees (2, 1). Under
 * velocity (1, 0, 0.06) the envelope function is exactly f = 0.24 - 0.3 u - 0.05 v, which
 * makes it both the funnel solver section fixture and the fit module ruled fixture.
 */
export function slantFixtureSurface() returns map
{
    const zGrid = [[0, 0], [-0.09, -0.065], [-0.03, 0.02]];
    const greville2 = [0, 0.5, 1];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(2);
        for (var j = 0; j < 2; j += 1)
        {
            row[j] = vector(greville2[i], j, zGrid[i][j]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 1,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * A parabolic cylinder S = (u, v, 0.5 c u^2) as a degree (2, 1) Bezier patch: z_u = c u is
 * linear, so a translation whose velocity is polynomial in t makes the envelope function
 * polynomial in both, with contact on a single ruling.
 */
export function parabolicCylinderSurface(c is number) returns map
{
    const greville2 = [0, 0.5, 1];
    const zControls = [0, 0, 0.5 * c];
    var net = makeArray(3);
    for (var i = 0; i < 3; i += 1)
    {
        var row = makeArray(2);
        for (var j = 0; j < 2; j += 1)
        {
            row[j] = vector(greville2[i], j, zControls[i]);
        }
        net[i] = row;
    }
    return {
            "uDegree" : 2, "vDegree" : 1,
            "uKnots" : [0, 0, 0, 1, 1, 1], "vKnots" : [0, 0, 1, 1],
            "controlPoints" : net, "isRational" : false,
            "isUPeriodic" : false, "isVPeriodic" : false
        };
}

/**
 * The 4x4 control net of the wavy bicubic test patch, in meters: a mildly non-developable
 * freeform face with no analytic class, used wherever a fixture must NOT classify as one of
 * the five surface types.
 */
export function wavyBicubicPatchNet() returns array
{
    var rows = makeArray(4);
    for (var rowIndex = 0; rowIndex < 4; rowIndex += 1)
    {
        var row = makeArray(4);
        for (var columnIndex = 0; columnIndex < 4; columnIndex += 1)
        {
            row[columnIndex] = vector(0.1 + columnIndex * 0.01, rowIndex * 0.01,
                        0.005 * sin(90 * degree * columnIndex) + 0.004 * cos(60 * degree * rowIndex)) * meter;
        }
        rows[rowIndex] = row;
    }
    return rows;
}
