FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0");

// swAnalyticContact.fs - same-document element import; fix the id/version on paste. For MCP
// harness runs the payload inlines the module instead of resolving this line.
import(path : "0000000000000000000000aa", version : "0000000000000000000000bb"); //swAnalyticContact.fs
import(path : "8dba215569bb1c9f8f1bf700", version : "0000000000000000000000ff"); //swTestHarness.fs

/**
 * Self test for swAnalyticContact.fs (spec section 6.0 strategy 1). Selection-free: it builds
 * its own typed analytic surfaces and its own motion stations, so it needs no geometry and no
 * Part Studio state.
 *
 * The load-bearing check is CROSS-PATH: every closed form is compared against
 * evaluateAnalyticContactDirect, which builds the world-space point and normal and evaluates the
 * section 1.1 definition against the motion sample without touching the pullback. Agreement to
 * machine precision on all five classes is what certifies the pullback algebra.
 *
 * The motion station used for that check has BOTH a rotation derivative and a translation
 * derivative and a NON-orthonormal A on purpose: the pullback identity f = <N, W S + c> holds
 * for any A, so exercising it away from SO(3) separates an algebra error from an orthonormality
 * assumption. The rigid-motion consequence is then checked separately and exactly, where A is
 * the identity and A' is skew.
 */

annotation { "Feature Type Name" : "Sweep Analytic Contact Self Test" }
export const sweepAnalyticContactSelfTest = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        var failures = "";
        var checks = 0;

        // ---------- Cross-path consistency on all five classes ----------
        const generalSample = constantMotionSample(
                [[0.93, -0.21, 0.11], [0.24, 0.88, -0.17], [-0.08, 0.19, 0.97]],
                [[0.13, -0.44, 0.28], [0.51, 0.07, -0.33], [-0.22, 0.39, 0.05]],
                vector(0.31, -0.17, 0.44));
        const axis = normalize(vector(0.3, -0.5, 0.81));
        const inPlane = normalize(cross(axis, vector(0.11, 0.97, -0.2)));
        const testOrigin = vector(0.07, -0.13, 0.21) * meter;
        const testCoordSystem = coordSystem(testOrigin, inPlane, axis);

        const surfaces = [
                ["PLANE", plane(testOrigin, axis, inPlane), 2.7, 1.2],
                ["CYLINDER", cylinder(testCoordSystem, 0.037 * meter), 3.14159, 0.1],
                ["CONE", cone(testCoordSystem, 0.42 * radian), 3.14159, 0.2],
                ["SPHERE", sphere(testCoordSystem, 0.051 * meter), 3.14159, 1.4],
                ["TORUS", torus(testCoordSystem, 0.018 * meter, 0.06 * meter), 3.14159, 3.14159]
            ];
        var worstConsistency = 0;
        for (var entry in surfaces)
        {
            const frame = stripAnalyticSurface(entry[1]);
            const pullback = analyticContactPullback(generalSample, frame);
            var worstForClass = 0;
            for (var i = 0; i <= 8; i += 1)
            {
                for (var j = 0; j <= 8; j += 1)
                {
                    const u = -entry[2] + 2 * entry[2] * i / 8;
                    const v = -entry[3] + 2 * entry[3] * j / 8;
                    worstForClass = max(worstForClass,
                        abs(evaluateAnalyticContact(frame, pullback, u, v) -
                            evaluateAnalyticContactDirect(frame, generalSample, u, v)));
                }
            }
            println("[ANALYTIC SELF TEST] " ~ entry[0] ~ " closed form vs the section 1.1 definition: " ~
                worstForClass);
            worstConsistency = max(worstConsistency, worstForClass);
            checks += 1;
        }
        if (worstConsistency > 1e-14)
        {
            failures = failures ~ " closed form disagrees with the direct definition by " ~
                worstConsistency ~ ".";
        }

        // ---------- The trig structures reproduce f ----------
        // Cylinder and cone are LINEAR in their ruling parameter; sphere and torus reduce to one
        // degree-2 trigonometric polynomial per meridian. Both claims are checked against the
        // closed form, so a wrong harmonic collection cannot hide.
        const cylinderFrame = stripAnalyticSurface(cylinder(testCoordSystem, 0.037 * meter));
        const cylinderPullback = analyticContactPullback(generalSample, cylinderFrame);
        const cylinderStructure = analyticContactStructure(cylinderFrame, cylinderPullback, {});
        var worstCylinderStructure = 0;
        for (var i = 0; i <= 24; i += 1)
        {
            for (var j = 0; j <= 6; j += 1)
            {
                const theta = 2 * PI * i / 24;
                const z = -0.1 + 0.2 * j / 6;
                const structured = trigPolynomialValue(cylinderStructure.constantTerm, theta) +
                    z * trigPolynomialValue(cylinderStructure.rulingTerm, theta);
                worstCylinderStructure = max(worstCylinderStructure,
                    abs(structured - evaluateAnalyticContact(cylinderFrame, cylinderPullback, theta, z)));
            }
        }
        println("[ANALYTIC SELF TEST] cylinder A(theta) + z B(theta) vs closed form: " ~ worstCylinderStructure);
        checks += 1;
        if (worstCylinderStructure > 1e-15)
        {
            failures = failures ~ " cylinder ruled structure off by " ~ worstCylinderStructure ~ ".";
        }

        const coneFrame = stripAnalyticSurface(cone(testCoordSystem, 0.42 * radian));
        const conePullback = analyticContactPullback(generalSample, coneFrame);
        const coneStructure = analyticContactStructure(coneFrame, conePullback, {});
        var worstConeStructure = 0;
        for (var i = 0; i <= 24; i += 1)
        {
            for (var j = 0; j <= 6; j += 1)
            {
                const theta = 2 * PI * i / 24;
                const ruling = 0.02 + 0.18 * j / 6;
                const structured = trigPolynomialValue(coneStructure.constantTerm, theta) +
                    ruling * trigPolynomialValue(coneStructure.rulingTerm, theta);
                worstConeStructure = max(worstConeStructure,
                    abs(structured - evaluateAnalyticContact(coneFrame, conePullback, theta, ruling)));
            }
        }
        println("[ANALYTIC SELF TEST] cone A(theta) + l B(theta) vs closed form: " ~ worstConeStructure);
        checks += 1;
        if (worstConeStructure > 1e-15)
        {
            failures = failures ~ " cone ruled structure off by " ~ worstConeStructure ~ ".";
        }

        var worstMeridian = 0;
        for (var entry in [["SPHERE", sphere(testCoordSystem, 0.051 * meter)],
                    ["TORUS", torus(testCoordSystem, 0.018 * meter, 0.06 * meter)]])
        {
            const frame = stripAnalyticSurface(entry[1]);
            const pullback = analyticContactPullback(generalSample, frame);
            for (var i = 0; i <= 12; i += 1)
            {
                const theta = 2 * PI * i / 12;
                const meridian = analyticMeridianPolynomial(frame, pullback, theta);
                for (var j = 0; j <= 12; j += 1)
                {
                    const phi = -PI + 2 * PI * j / 12;
                    worstMeridian = max(worstMeridian, abs(trigPolynomialValue(meridian, phi) -
                                evaluateAnalyticContact(frame, pullback, theta, phi)));
                }
            }
        }
        println("[ANALYTIC SELF TEST] sphere and torus meridian polynomials vs closed form: " ~ worstMeridian);
        checks += 1;
        if (worstMeridian > 1e-15)
        {
            failures = failures ~ " meridian polynomial off by " ~ worstMeridian ~ ".";
        }

        // ---------- Sliding, exactly (spec 6.4) ----------
        // A cylinder translating along its own axis and a plane translating parallel to itself
        // are the two cases section 6.4 calls the common ones on real parts. Both must come out
        // EXACTLY zero from the coefficients, with no sampling anywhere.
        const axialSample = constantMotionSample(identityRotationRows(), zeroRows(), 0.25 * axis);
        const axialStructure = analyticContactStructure(cylinderFrame,
            analyticContactPullback(axialSample, cylinderFrame), {});
        println("[ANALYTIC SELF TEST] cylinder translating along its own axis slides: " ~
            axialStructure.slidesEverywhere);
        checks += 1;
        if (!axialStructure.slidesEverywhere)
        {
            failures = failures ~ " a cylinder translating along its own axis was not detected as sliding.";
        }

        const planeFrame = stripAnalyticSurface(plane(testOrigin, axis, inPlane));
        const parallelStructure = analyticContactStructure(planeFrame,
            analyticContactPullback(constantMotionSample(identityRotationRows(), zeroRows(), 0.25 * inPlane),
                planeFrame), {});
        println("[ANALYTIC SELF TEST] plane translating in its own plane slides: " ~
            parallelStructure.slidesEverywhere);
        checks += 1;
        if (!parallelStructure.slidesEverywhere)
        {
            failures = failures ~ " a plane translating parallel to itself was not detected as sliding.";
        }

        // A plane translating along its normal must NOT slide, and its constant is exactly the
        // normal speed - a closed-form value with no discretization in it at all.
        const normalSpeed = 0.31;
        const normalStructure = analyticContactStructure(planeFrame,
            analyticContactPullback(constantMotionSample(identityRotationRows(), zeroRows(), normalSpeed * axis),
                planeFrame), {});
        const constantError = abs(normalStructure.constant - normalSpeed);
        println("[ANALYTIC SELF TEST] plane translating along its normal: slides " ~
            normalStructure.slidesEverywhere ~ ", constant error " ~ constantError);
        checks += 1;
        if (normalStructure.slidesEverywhere || constantError > 1e-16)
        {
            failures = failures ~ " plane normal-translation constant error " ~ constantError ~ ".";
        }

        // ---------- The rigid-motion consequence ----------
        // A exactly orthonormal makes W = A^T A' skew, and a skew W contributes nothing to any
        // quadratic-in-normal term. For a cylinder that means the radius term drops out and the
        // contact curve is first-harmonic only. Checked on the coefficients, so it is exact.
        const skewSample = constantMotionSample(identityRotationRows(),
                [[0, -0.4, 0.25], [0.4, 0, -0.13], [-0.25, 0.13, 0]], vector(0.2, -0.1, 0.05));
        const skewStructure = analyticContactStructure(cylinderFrame,
            analyticContactPullback(skewSample, cylinderFrame), {});
        const secondHarmonic = max(abs(skewStructure.constantTerm.cosine[2]),
                abs(skewStructure.constantTerm.sine[2]));
        println("[ANALYTIC SELF TEST] skew W leaves the cylinder second harmonic at " ~ secondHarmonic ~
            " and its constant at " ~ abs(skewStructure.constantTerm.cosine[0]));
        checks += 1;
        if (secondHarmonic > 1e-17 || abs(skewStructure.constantTerm.cosine[0]) > 1e-17)
        {
            failures = failures ~ " skew W left a quadratic term of " ~ secondHarmonic ~ ".";
        }

        // ---------- Contact curves against closed-form answers ----------
        // Cylinder under a translation PERPENDICULAR to its axis: f = <d(theta), b'>, so contact
        // is the two rulings at theta = atan2(-b'1, b'2) mod pi. Nothing numerical about it.
        const crossSpeed = 0.4;
        const crossDirection = inPlane;
        const crossPullback = analyticContactPullback(
                constantMotionSample(identityRotationRows(), zeroRows(), crossSpeed * crossDirection), cylinderFrame);
        const crossStructure = analyticContactStructure(cylinderFrame, crossPullback, {});
        const crossRoots = solveTrigPolynomialRoots(crossStructure.constantTerm, 0, {});
        const expectedTheta = atan2(-crossStructure.constantTerm.cosine[1],
                crossStructure.constantTerm.sine[1]) / radian;
        var worstRootError = 0;
        for (var root in crossRoots.roots)
        {
            var difference = abs(root - expectedTheta);
            while (difference > PI + 1e-9)
            {
                difference -= PI;
            }
            worstRootError = max(worstRootError, min(difference, abs(difference - PI)));
        }
        println("[ANALYTIC SELF TEST] cylinder crossing translation: " ~ size(crossRoots.roots) ~
            " contact rulings, worst theta error " ~ worstRootError ~
            ", residual " ~ crossRoots.worstResidual);
        checks += 1;
        if (size(crossRoots.roots) != 2 || worstRootError > 1e-12)
        {
            failures = failures ~ " cylinder crossing-translation rulings: " ~
                size(crossRoots.roots) ~ " roots, worst error " ~ worstRootError ~ ".";
        }

        // Sphere under a translation: contact is the great circle perpendicular to b', so along
        // each meridian the latitude solves tan(phi) = -<d, b'> / <axis, b'> in closed form.
        const sphereFrame = stripAnalyticSurface(sphere(testCoordSystem, 0.051 * meter));
        const sphereVelocity = vector(0.22, -0.31, 0.17);
        const spherePullback = analyticContactPullback(
                constantMotionSample(identityRotationRows(), zeroRows(), sphereVelocity), sphereFrame);
        var worstSphereLatitude = 0;
        var sphereRootTotal = 0;
        for (var i = 0; i < 12; i += 1)
        {
            const theta = 2 * PI * i / 12;
            const meridian = analyticMeridianPolynomial(sphereFrame, spherePullback, theta);
            const solved = solveTrigPolynomialRoots(meridian, -PI, {});
            sphereRootTotal += size(solved.roots);
            const localVelocity = vector(dot(sphereFrame.basis[0], sphereVelocity),
                    dot(sphereFrame.basis[1], sphereVelocity), dot(sphereFrame.basis[2], sphereVelocity));
            const expected = atan2(-(localVelocity[0] * cos(theta * radian) +
                            localVelocity[1] * sin(theta * radian)), localVelocity[2]) / radian;
            for (var root in solved.roots)
            {
                var difference = abs(root - expected);
                worstSphereLatitude = max(worstSphereLatitude,
                    min(difference, min(abs(difference - PI), abs(difference - 2 * PI))));
            }
        }
        println("[ANALYTIC SELF TEST] sphere translation great circle: " ~ sphereRootTotal ~
            " roots over 12 meridians, worst latitude error " ~ worstSphereLatitude);
        checks += 1;
        if (worstSphereLatitude > 1e-12)
        {
            failures = failures ~ " sphere great-circle latitude error " ~ worstSphereLatitude ~ ".";
        }

        // ---------- Trig polynomial utilities ----------
        // cos(2x) has its four roots at the odd multiples of pi/4, exactly.
        const doubled = trigPolynomial([0, 0, 1], [0, 0, 0]);
        const doubledRoots = solveTrigPolynomialRoots(doubled, 0, {});
        var worstDoubled = 0;
        for (var index = 0; index < size(doubledRoots.roots); index += 1)
        {
            worstDoubled = max(worstDoubled, abs(doubledRoots.roots[index] - (0.25 + 0.5 * index) * PI));
        }
        println("[ANALYTIC SELF TEST] cos(2x) roots: " ~ size(doubledRoots.roots) ~ ", worst error " ~
            worstDoubled);
        checks += 1;
        if (size(doubledRoots.roots) != 4 || worstDoubled > 1e-14)
        {
            failures = failures ~ " cos(2x) gave " ~ size(doubledRoots.roots) ~ " roots, worst error " ~
                worstDoubled ~ ".";
        }

        // The bound must never be exceeded by an actual sample - that is the whole point of a
        // screen. Checked on the general cylinder's own coefficients.
        const boundClaim = trigPolynomialBound(cylinderStructure.constantTerm);
        var worstSampled = 0;
        for (var index = 0; index <= 720; index += 1)
        {
            worstSampled = max(worstSampled,
                abs(trigPolynomialValue(cylinderStructure.constantTerm, 2 * PI * index / 720)));
        }
        println("[ANALYTIC SELF TEST] trig bound " ~ boundClaim ~ " against sampled maximum " ~ worstSampled);
        checks += 1;
        if (worstSampled > boundClaim * (1 + 1e-12))
        {
            failures = failures ~ " a sample exceeded the trig bound (" ~ worstSampled ~ " > " ~
                boundClaim ~ ").";
        }

        // An identically zero polynomial must be reported as such, not handed back as roots.
        const zeroPolynomial = trigPolynomial([0, 0, 0], [0, 0, 0]);
        const zeroSolved = solveTrigPolynomialRoots(zeroPolynomial, 0, {});
        checks += 1;
        if (!zeroSolved.identicallyZero || size(zeroSolved.roots) != 0)
        {
            failures = failures ~ " an identically zero polynomial was not reported as sliding.";
        }

        // ---------- The free screen ----------
        // A plane translating fast along its normal cannot graze anywhere, and the bound proves
        // it without evaluating f at a single parameter.
        const screenBound = analyticContactBound(planeFrame,
            analyticContactPullback(constantMotionSample(identityRotationRows(), zeroRows(), 0.9 * axis), planeFrame),
            { "uMin" : -0.05, "uMax" : 0.05, "vMin" : -0.05, "vMax" : 0.05 }, {});
        println("[ANALYTIC SELF TEST] non-grazing plane screen: minimum |f| " ~
            screenBound.minimumMagnitude);
        checks += 1;
        if (screenBound.minimumMagnitude <= 0)
        {
            failures = failures ~ " the screen failed to reject a plane that cannot graze.";
        }

        reportTestVerdict(context, id, "ANALYTIC SELF TEST", checks, failures,
            ("all five analytic classes agree with the section 1.1 " ~
            "definition to machine precision, the ruled and meridian structures reproduce f, " ~
            "sliding and the rigid-motion skew consequence are exact, and the closed-form " ~
            "contact curves match their analytic answers."));
    });

