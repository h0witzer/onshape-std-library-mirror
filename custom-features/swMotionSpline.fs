FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/sketch.fs", version : "3044.0");          // newSketchOnPlane, skLineSegment, skSolve
import(path : "onshape/std/path.fs", version : "3044.0");            // Path, constructPath, evPathTangentLines, evPathLength
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");   // line, knotArray, transform(Line, Line)

// Non-standard import: splineRefinementUtils.fs (custom-features/splineRefinementUtils.fs) -
// interpolateBSplineCurveThroughPoints, normalizeSplineDefinition, findEvaluationSpanIndex,
// bSplineBasisValues. Published pin below; in the sweep document swap for the same-document
// tab import if preferred. If the module is republished, bump the version id here like every
// other consumer.
import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs

/**
 * MOTION SPLINE - the rigid motion h(t) = (A(t), b(t)) of the solid sweep, represented
 * entrywise as B-splines. Spec: docs/specs/SOLID_SWEEP_SPEC.md section 4. Companion tester:
 * swMotionSplineTester.fs.
 *
 * STATUS (2026-08-21): LIVE-VALIDATED on line, arc, and spline paths. Worst case (free
 * spline, 0.218 m): 0.30 s build, 150 stations, 2 predictive rungs, drift 5.85e-7 against
 * the 1e-6 tolerance, all 221 tester checks green. Analytic paths (lines/arcs/helixes -
 * the common case) have constant per-segment drift behavior and resolve far coarser.
 *
 * WHAT THIS MODULE PRODUCES: a MotionSpline map holding
 *   - columnX / columnY / columnZ : the three columns of the rotation A(t), each a cubic
 *     B-spline of unitless 3-vectors, all four splines interpolated at the SAME station
 *     parameters (so they share one knot vector);
 *   - translation : b(t), same representation, meters implied (unitless numbers);
 *   - stationFrames : the raw sampled frames as CoordSystems - exactly rigid, used for
 *     kernel-facing snapshots (cap placement, instances), never the spline evaluations;
 *   - orthogonalityDrift : certified sup |A(t)^T A(t) - I| over all knot-span midpoints -
 *     station density grows at build time (one rung predicts the needed count via the h^4
 *     model) until this passes the tolerance;
 *   - events : arc-length fractions of interior edge junctions (potential curvature breaks;
 *     downstream fitting must place patch boundaries there);
 *   - stationParameters, pathLength, path, degree, keepOrientation.
 *
 * WHY ENTRYWISE B-SPLINES (spec section 2.1): A', A'' are exact spline derivatives, and the
 * rigid transport of any B-spline entity is EXACT in B-spline form (control-point transport),
 * so trajectories and edge-sweep surfaces come out closed-form relative to this motion. The
 * envelope is computed exactly with respect to the FITTED motion; the fit deviates from the
 * path intent by the separately certified drift.
 *
 * FRAME SOURCES (roll-free mode): double-reflection rotation-minimizing frames (Wang et al.
 * 2008) computed from the path tangents - one batched evPathTangentLines call plus pure math
 * per densification pass. The relative motion A(t) = S(t) * S(0)^T is invariant under any
 * constant twist of the frame field, so the roll seed cannot affect the motion, and the
 * kernel sweeper's own frames reproduce the same A(t) to machine precision (its sweep frames
 * are a rotation-minimizing field). MotionFrameSource.KERNEL_SWEEP samples frames off a
 * helper-sweep scaffold instead (the curvePattern.fs:381-473 technique, in a
 * startFeature/abortFeature scratch scope) and exists purely as an A/B diagnostic.
 * `frameSource` on the returned map records "DOUBLE_REFLECTION", "KERNEL_SWEEP", or
 * "IDENTITY" (keep-orientation mode).
 *
 * UNITS POLICY: everything inside the fitted splines is unitless (meters implied). Units are
 * reattached only at kernel-facing boundaries (stationFrames, motionSnapshotTransform).
 */

// The angle above which two consecutive path edges are declared tangent-DIScontinuous.
const PATH_TANGENT_BREAK_THRESHOLD = 0.1 * degree;

// Hard ceiling on station densification (build cost guard).
const MAXIMUM_STATION_COUNT = 260;

// Default certified drift ceiling. Budget-derived: drift contributes about drift * toolRadius
// of position error, so 1e-6 keeps the motion's share around 1e-7 m for a 100 mm tool - an
// order under the fit tolerance. The floor achievable on a given path is set by the
// evPathTangentLines samples, which every frame source shares.
const DEFAULT_DRIFT_TOLERANCE = 1e-6;

// A rung whose drift improvement falls below the h^4-predicted improvement divided by this
// factor means the build is at the shared sampling floor - stop densifying and report
// honestly.
const PLATEAU_IMPROVEMENT_FACTOR = 4;

/**
 * Selectable frame source for roll-free motion builds. AUTOMATIC (double reflection) is the
 * default; KERNEL_SWEEP samples the kernel sweeper's ribbon frames instead and exists as an
 * A/B diagnostic - both produce the same A(t), since a constant frame twist conjugates out
 * of S(t) * S(0)^T.
 */
export enum MotionFrameSource
{
    annotation { "Name" : "Automatic (double reflection)" }
    AUTOMATIC,
    annotation { "Name" : "Kernel sweep frames (diagnostic)" }
    KERNEL_SWEEP
}

/**
 * Builds the motion spline for a path. The only context-using entry point of this module.
 * Input map fields:
 *   pathEdges {Query} : edges forming one open, tangent-continuous path.
 *   keepOrientation {boolean} : true = pure translation (A = identity); false = roll-free
 *       frame transport.
 *   frameSource {MotionFrameSource} : optional, default AUTOMATIC (double reflection);
 *       KERNEL_SWEEP is an A/B diagnostic mode.
 *   initialStationCount {number} : optional, default 33 (grown predictively until drift passes).
 *   driftTolerance {number} : optional, default 1e-6 (unitless, sup |A^T A - I|; position
 *       cost is roughly drift times tool radius).
 * Output: the MotionSpline map described in the header.
 * Throws with the violation named on closed paths, tangent breaks, and scaffold failures.
 */
export function buildMotionSpline(context is Context, id is Id, definition is map) returns map
{
    const constructed = constructPath(context, definition.pathEdges, {});
    const path = constructed.path;
    if (path.closed)
    {
        throw "swMotionSpline: closed paths are not supported yet - pick an open edge chain.";
    }
    checkPathTangentContinuity(context, path);

    const pathLength = evPathLength(context, path);
    const junctionParameters = interiorJunctionParameters(context, path, pathLength);
    const driftTolerance = definition.driftTolerance == undefined ? DEFAULT_DRIFT_TOLERANCE : definition.driftTolerance;
    const initialStationCount = max(5, definition.initialStationCount == undefined ? 33 : definition.initialStationCount);
    var stationCount = initialStationCount;

    var rungCount = 0;
    var perStationEvaluationCalls = 0;
    var batchedTangentCalls = 0;

    if (definition.keepOrientation == true)
    {
        // Pure translation: identity rotation, origins straight off the path. Drift is
        // exactly zero; one pass, no scaffold.
        const stationParameters = mergedStationParameters(stationCount, junctionParameters);
        const tangentResult = evPathTangentLines(context, path, stationParameters);
        const stationSamples = keepOrientationSamples(tangentResult.tangentLines);
        var motion = assembleMotionSpline(stationSamples, stationParameters, junctionParameters,
            path, pathLength, true, driftTolerance);
        motion.frameSource = "IDENTITY";
        motion.buildDiagnostics = {
                "requestedMode" : "IDENTITY",
                "ladderRungs" : 1,
                "finalStationCount" : size(stationParameters),
                "perStationEvaluationCalls" : 0,
                "batchedTangentCalls" : 1
            };
        return motion;
    }

    // Roll-free mode. Double-reflection frames drive densification (one batched tangent call
    // plus pure math per rung). MotionFrameSource.KERNEL_SWEEP instead samples the kernel
    // sweeper's ribbon frames per station (2 ev-calls each) as an A/B diagnostic - both
    // sources produce the same A(t), since a constant frame twist conjugates out of
    // S(t) * S(0)^T.
    const mode = definition.frameSource == undefined ? MotionFrameSource.AUTOMATIC : definition.frameSource;
    var motion;

    if (mode == MotionFrameSource.KERNEL_SWEEP)
    {
        const scratchId = id + "frameScaffold";
        var built = false;
        startFeature(context, scratchId);
        try
        {
            const sweepFaces = buildFrameScaffold(context, scratchId, path);
            var previousDrift = -1;
            while (true)
            {
                const stationParameters = mergedStationParameters(stationCount, junctionParameters);
                const tangentResult = evPathTangentLines(context, path, stationParameters);
                batchedTangentCalls += 1;
                const stationSamples = rollFreeSamples(context, sweepFaces,
                    tangentResult.tangentLines, tangentResult.edgeIndices);
                perStationEvaluationCalls += 2 * size(stationParameters);
                rungCount += 1;
                motion = assembleMotionSpline(stationSamples, stationParameters, junctionParameters,
                    path, pathLength, false, driftTolerance);
                if (motion.orthogonalityDrift <= driftTolerance)
                {
                    break;
                }
                const plateaued = previousDrift > 0 &&
                    motion.orthogonalityDrift * PLATEAU_IMPROVEMENT_FACTOR > previousDrift;
                if (plateaued || stationCount * 2 - 1 > MAXIMUM_STATION_COUNT)
                {
                    break;
                }
                previousDrift = motion.orthogonalityDrift;
                stationCount = stationCount * 2 - 1;
            }
            built = true;
        }
        abortFeature(context, scratchId);
        if (!built)
        {
            throw "swMotionSpline: the frame scaffold failed (helper sweep or frame sampling threw) - " ~
                "see the console for the underlying error.";
        }
        motion.frameSource = "KERNEL_SWEEP";
    }
    else
    {
        var previousDrift = -1;
        var previousStationCount = -1;
        while (true)
        {
            const stationParameters = mergedStationParameters(stationCount, junctionParameters);
            const tangentResult = evPathTangentLines(context, path, stationParameters);
            batchedTangentCalls += 1;
            const stationSamples = doubleReflectionSamples(tangentResult.tangentLines, undefined);
            rungCount += 1;
            motion = assembleMotionSpline(stationSamples, stationParameters, junctionParameters,
                path, pathLength, false, driftTolerance);
            if (motion.orthogonalityDrift <= driftTolerance)
            {
                break;
            }
            // Plateau: the h^4 model predicts (growth ratio)^4 improvement per rung; falling
            // short of a quarter of that means the build is at the shared sampling floor.
            const expectedImprovement = previousStationCount > 0 ?
                ((stationCount / previousStationCount) ^ 4) : 1;
            const plateaued = previousDrift > 0 &&
                PLATEAU_IMPROVEMENT_FACTOR * previousDrift < expectedImprovement * motion.orthogonalityDrift;
            if (plateaued)
            {
                break;
            }
            // Drift scales as h^4, so one rung predicts the station count that meets the
            // tolerance: jump straight there with 20% overshoot, growing at least 30% so
            // progress is guaranteed.
            var nextCount = ceil(1.2 * stationCount * ((motion.orthogonalityDrift / driftTolerance) ^ 0.25));
            nextCount = max(nextCount, ceil(1.3 * stationCount));
            nextCount = min(nextCount, MAXIMUM_STATION_COUNT);
            if (nextCount <= stationCount)
            {
                break;
            }
            previousDrift = motion.orthogonalityDrift;
            previousStationCount = stationCount;
            stationCount = nextCount;
        }
        motion.frameSource = "DOUBLE_REFLECTION";
    }

    if (motion.orthogonalityDrift > driftTolerance)
    {
        throw "swMotionSpline: orthogonality drift " ~ motion.orthogonalityDrift ~ " on " ~
            motion.frameSource ~ " frames still exceeds " ~ driftTolerance ~ " at " ~
            size(motion.stationParameters) ~ " stations. Raise driftTolerance (position cost is " ~
            "roughly drift times tool radius), simplify the path, or try another frame source.";
    }
    motion.buildDiagnostics = {
            "requestedMode" : mode,
            "ladderRungs" : rungCount,
            "finalStationCount" : size(motion.stationParameters),
            "perStationEvaluationCalls" : perStationEvaluationCalls,
            "batchedTangentCalls" : batchedTangentCalls
        };
    return motion;
}

/**
 * Evaluates the motion and its first two derivatives at `t` in [0, 1]. Pure math, no context.
 * Three lean basis+combine evaluations on the packed fit and its difference-net derivative
 * splines (built once at assembly).
 * Output map: columns / columnVelocities / columnAccelerations (arrays of three unitless
 * Vectors - the columns of A, A', A'') and translation / translationVelocity /
 * translationAcceleration (unitless Vectors, meters implied).
 */
export function motionAt(motion is map, t is number) returns map
{
    const position = evaluatePackedPoint(motion.packedSpline, t);
    const velocity = evaluatePackedPoint(motion.packedVelocity, t);
    const acceleration = evaluatePackedPoint(motion.packedAcceleration, t);
    return {
            "columns" : [sliceVector3(position, 0), sliceVector3(position, 3), sliceVector3(position, 6)],
            "columnVelocities" : [sliceVector3(velocity, 0), sliceVector3(velocity, 3), sliceVector3(velocity, 6)],
            "columnAccelerations" : [sliceVector3(acceleration, 0), sliceVector3(acceleration, 3), sliceVector3(acceleration, 6)],
            "translation" : sliceVector3(position, 9),
            "translationVelocity" : sliceVector3(velocity, 9),
            "translationAcceleration" : sliceVector3(acceleration, 9)
        };
}

/**
 * Applies a motion sample to a unitless tool-frame point: A(t) * point + b(t).
 */
export function applyMotion(motionSample is map, toolPoint is Vector) returns Vector
{
    return motionSample.columns[0] * toolPoint[0] +
        motionSample.columns[1] * toolPoint[1] +
        motionSample.columns[2] * toolPoint[2] +
        motionSample.translation;
}

/**
 * The velocity of a tool-frame point under a motion sample: A'(t) * point + b'(t).
 */
export function motionVelocityAt(motionSample is map, toolPoint is Vector) returns Vector
{
    return motionSample.columnVelocities[0] * toolPoint[0] +
        motionSample.columnVelocities[1] * toolPoint[1] +
        motionSample.columnVelocities[2] * toolPoint[2] +
        motionSample.translationVelocity;
}

/**
 * The EXACT trajectory of a fixed tool-frame point as a B-spline curve: control points
 * Q_j = A_j * point + b_j on the motion's own knot vector (valid because all four motion
 * splines share that knot vector and B-spline bases are a partition of unity).
 * Input: the MotionSpline and a unitless point. Output: a non-rational spline map (unitless).
 */
export function trajectoryCurveOf(motion is map, toolPoint is Vector) returns map
{
    const count = size(motion.columnX.controlPoints);
    var trajectoryControlPoints = makeArray(count, vector(0, 0, 0));
    for (var index = 0; index < count; index += 1)
    {
        trajectoryControlPoints[index] = motion.columnX.controlPoints[index] * toolPoint[0] +
            motion.columnY.controlPoints[index] * toolPoint[1] +
            motion.columnZ.controlPoints[index] * toolPoint[2] +
            motion.translation.controlPoints[index];
    }
    return {
            "degree" : motion.degree,
            "isPeriodic" : false,
            "isRational" : false,
            "controlPoints" : trajectoryControlPoints,
            "knots" : knotArray(motion.columnX.knots)
        };
}

/**
 * The exactly-rigid placement of the tool at a station, from the RAW kernel frames (never the
 * spline fit): toWorld(frame_i) * inverse(toWorld(frame_0)). Kernel-facing (with units).
 */
export function motionSnapshotTransform(motion is map, stationIndex is number) returns Transform
{
    return toWorld(motion.stationFrames[stationIndex]) * inverse(toWorld(motion.stationFrames[0]));
}

// ===================== Private: path checks and stations =====================

/**
 * Throws (with the edge pair and angle named) if consecutive path edges meet with a tangent
 * break larger than PATH_TANGENT_BREAK_THRESHOLD.
 */
function checkPathTangentContinuity(context is Context, path is Path)
{
    var previousEndDirection;
    for (var edgeIndex = 0; edgeIndex < size(path.edges); edgeIndex += 1)
    {
        var endpointTangents = evEdgeTangentLines(context, {
                    "edge" : path.edges[edgeIndex],
                    "parameters" : [0, 1]
                });
        if (path.flipped[edgeIndex])
        {
            endpointTangents = [
                    line(endpointTangents[1].origin, -endpointTangents[1].direction),
                    line(endpointTangents[0].origin, -endpointTangents[0].direction)
                ];
        }
        if (edgeIndex > 0)
        {
            const breakAngle = angleBetween(previousEndDirection, endpointTangents[0].direction);
            if (breakAngle > PATH_TANGENT_BREAK_THRESHOLD)
            {
                throw "swMotionSpline: the path has a " ~ (breakAngle / degree) ~
                    " degree tangent break between edges " ~ edgeIndex ~ " and " ~ (edgeIndex + 1) ~
                    " - the motion needs a tangent-continuous path.";
            }
        }
        previousEndDirection = endpointTangents[1].direction;
    }
}

/**
 * Arc-length fractions of the interior edge junctions - potential curvature breaks that
 * downstream fitting must treat as mandatory patch boundaries (the motion's `events`).
 */
function interiorJunctionParameters(context is Context, path is Path, pathLength is ValueWithUnits) returns array
{
    if (size(path.edges) < 2)
    {
        return [];
    }
    var junctions = makeArray(size(path.edges) - 1, 0);
    var accumulated = 0;
    for (var edgeIndex = 0; edgeIndex < size(path.edges) - 1; edgeIndex += 1)
    {
        accumulated += evLength(context, { "entities" : path.edges[edgeIndex] }) / pathLength;
        junctions[edgeIndex] = accumulated;
    }
    return junctions;
}

/**
 * Uniform stations 0..1 at the given count, merged (sorted, deduplicated) with the junction
 * parameters so every junction is an interpolation node.
 */
function mergedStationParameters(stationCount is number, junctionParameters is array) returns array
{
    var stations = makeArray(stationCount + size(junctionParameters), 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        stations[index] = index / (stationCount - 1);
    }
    for (var index = 0; index < size(junctionParameters); index += 1)
    {
        stations[stationCount + index] = junctionParameters[index];
    }
    stations = sort(stations, function(a, b)
        {
            return a - b;
        });
    var deduplicated = [stations[0]];
    for (var index = 1; index < size(stations); index += 1)
    {
        if (stations[index] - deduplicated[size(deduplicated) - 1] > 1e-9)
        {
            deduplicated = append(deduplicated, stations[index]);
        }
    }
    return deduplicated;
}

// ===================== Private: frame sampling =====================

/**
 * A station sample: { frame (CoordSystem, with units), xAxis, yAxis, zAxis, origin (unitless
 * Vector) }. Keep-orientation mode: world axes at each path point.
 */
function keepOrientationSamples(tangentLines is array) returns array
{
    var samples = makeArray(size(tangentLines), 0);
    for (var index = 0; index < size(tangentLines); index += 1)
    {
        samples[index] = {
                "frame" : coordSystem(tangentLines[index].origin, vector(1, 0, 0), vector(0, 0, 1)),
                "xAxis" : vector(1, 0, 0),
                "yAxis" : vector(0, 1, 0),
                "zAxis" : vector(0, 0, 1),
                "origin" : tangentLines[index].origin / meter
            };
    }
    return samples;
}

/**
 * Builds the helper-sweep scaffold along the path (the curvePattern.fs technique): per edge,
 * sketch a tiny line perpendicular to the edge start (chained across edges by tracking the
 * previous sweep's end-cap vertex) and opSweep it along that edge. Must run inside an active
 * scratch scope; returns one swept-face Query per path edge.
 */
function buildFrameScaffold(context is Context, id is Id, path is Path) returns array
{
    var sweepFaces = makeArray(size(path.edges), 0);
    var previousEndTangent;
    var trackingQuery;
    for (var edgeIndex = 0; edgeIndex < size(path.edges); edgeIndex += 1)
    {
        var endpointTangents = evEdgeTangentLines(context, {
                    "edge" : path.edges[edgeIndex],
                    "parameters" : [0, 1]
                });
        if (path.flipped[edgeIndex])
        {
            endpointTangents = [
                    line(endpointTangents[1].origin, -endpointTangents[1].direction),
                    line(endpointTangents[0].origin, -endpointTangents[0].direction)
                ];
        }

        const sketchPlane = plane(endpointTangents[0].origin, endpointTangents[0].direction);
        var sketchPoint;
        if (edgeIndex == 0)
        {
            const profileHalfWidth = (10000 * TOLERANCE.zeroLength) * meter;
            sketchPoint = vector(0 * meter, profileHalfWidth);
        }
        else
        {
            // Transform the previous sweep's tracked end point into this edge's start plane,
            // so the profile is continuous across the junction.
            const junctionTransform = transform(previousEndTangent, endpointTangents[0]);
            const previousEndCapVertices = qCapEntity(id + ("sweep" ~ (edgeIndex - 1)), CapType.END, EntityType.VERTEX);
            const previousProfileEnd = evVertexPoint(context, {
                        "vertex" : qIntersection([previousEndCapVertices, trackingQuery])
                    });
            sketchPoint = worldToPlane(sketchPlane, junctionTransform * previousProfileEnd);
        }

        const sketchId = id + ("sketch" ~ edgeIndex);
        var sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
        skLineSegment(sketch, "line1", {
                    "start" : vector(0 * meter, 0 * meter),
                    "end" : sketchPoint
                });
        skSolve(sketch);

        trackingQuery = startTracking(context, {
                    "subquery" : sketchEntityQuery(sketchId, undefined, "line1.end"),
                    "secondarySubquery" : path.edges[edgeIndex]
                });

        opSweep(context, id + ("sweep" ~ edgeIndex), {
                    "profiles" : qCreatedBy(sketchId, EntityType.EDGE),
                    "path" : path.edges[edgeIndex]
                });
        sweepFaces[edgeIndex] = qCreatedBy(id + ("sweep" ~ edgeIndex), EntityType.FACE);

        previousEndTangent = endpointTangents[1];
    }
    return sweepFaces;
}

/**
 * Samples the kernel sweeper's roll-free frame at each station off the scaffold faces:
 * closest point on the swept ribbon -> tangent plane -> x forced to the projected path
 * tangent (the curvePattern anti-roll step). z = ribbon normal, y = z cross x.
 */
function rollFreeSamples(context is Context, sweepFaces is array, tangentLines is array, edgeIndices is array) returns array
{
    var samples = makeArray(size(tangentLines), 0);
    for (var index = 0; index < size(tangentLines); index += 1)
    {
        const ribbonFace = sweepFaces[edgeIndices[index]];
        const distanceResult = evDistance(context, {
                    "side0" : ribbonFace,
                    "side1" : tangentLines[index].origin,
                    "arcLengthParameterization" : false
                });
        var tangentPlane = evFaceTangentPlane(context, {
                    "face" : ribbonFace,
                    "parameter" : distanceResult.sides[0].parameter
                });
        tangentPlane.x = project(tangentPlane, tangentLines[index]).direction;

        const xAxis = tangentPlane.x;
        const zAxis = tangentPlane.normal;
        const yAxis = cross(zAxis, xAxis);
        samples[index] = {
                "frame" : coordSystem(tangentLines[index].origin, xAxis, zAxis),
                "xAxis" : xAxis,
                "yAxis" : yAxis,
                "zAxis" : zAxis,
                "origin" : tangentLines[index].origin / meter
            };
    }
    return samples;
}

/**
 * Rotation-minimizing frames by the double-reflection method (Wang, Juettler, Zheng, Liu
 * 2008), built purely from the station tangent lines. The roll-free frame source.
 * `initialNormalCandidate` (a unitless direction or undefined) seeds the frame roll; the
 * seed orients only the frames themselves - the relative motion A(t) is seed-invariant.
 */
function doubleReflectionSamples(tangentLines is array, initialNormalCandidate) returns array
{
    const stationCount = size(tangentLines);
    var tangents = makeArray(stationCount, 0);
    var positions = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        tangents[index] = tangentLines[index].direction;
        positions[index] = tangentLines[index].origin / meter;
    }

    var normal = initialNormalCandidate == undefined ? perpendicularVector(tangents[0]) : initialNormalCandidate;
    normal = normalize(normal - dot(normal, tangents[0]) * tangents[0]);

    var samples = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        const xAxis = tangents[index];
        const zAxis = normal;
        const yAxis = cross(zAxis, xAxis);
        samples[index] = {
                "frame" : coordSystem(tangentLines[index].origin, xAxis, zAxis),
                "xAxis" : xAxis,
                "yAxis" : yAxis,
                "zAxis" : zAxis,
                "origin" : positions[index]
            };

        if (index < stationCount - 1)
        {
            // First reflection: across the bisecting plane of the chord to the next station.
            const chord = positions[index + 1] - positions[index];
            const chordSquared = dot(chord, chord);
            var reflectedNormal = normal;
            var reflectedTangent = tangents[index];
            if (chordSquared > 0)
            {
                reflectedNormal = normal - (2 * dot(chord, normal) / chordSquared) * chord;
                reflectedTangent = tangents[index] - (2 * dot(chord, tangents[index]) / chordSquared) * chord;
            }
            // Second reflection: across the plane bisecting the reflected and true tangents.
            const tangentDifference = tangents[index + 1] - reflectedTangent;
            const differenceSquared = dot(tangentDifference, tangentDifference);
            if (differenceSquared > 0)
            {
                reflectedNormal = reflectedNormal -
                    (2 * dot(tangentDifference, reflectedNormal) / differenceSquared) * tangentDifference;
            }
            // Re-orthonormalize against the next tangent to shed roundoff.
            normal = normalize(reflectedNormal - dot(reflectedNormal, tangents[index + 1]) * tangents[index + 1]);
        }
    }
    return samples;
}

// ===================== Private: fitting and certification =====================

/**
 * Turns station samples into the MotionSpline map: rotation columns A_k = S_k * S_0^T and
 * translations b_k = origin_k - A_k * origin_0 per station, interpolated by cubic B-splines
 * at the station parameters, normalized, and drift-certified at every knot-span midpoint.
 */
function assembleMotionSpline(stationSamples is array, stationParameters is array, junctionParameters is array,
    path is Path, pathLength is ValueWithUnits, keepOrientation is boolean, driftTolerance is number) returns map
{
    const stationCount = size(stationSamples);
    const startSample = stationSamples[0];

    // All twelve motion scalars are fitted in ONE interpolation call on 12-dimensional
    // vectors, then unpacked - the interpolation operator build is the dominant O(n^3) cost
    // and this shares it across the four splines instead of paying it four times.
    var packedSamples = makeArray(stationCount, 0);
    var stationFrames = makeArray(stationCount, 0);
    for (var index = 0; index < stationCount; index += 1)
    {
        const sample = stationSamples[index];
        // Column i of A_k = S_k * (row i of S_0), with S = [x, y, z] as columns.
        const columnX = sample.xAxis * startSample.xAxis[0] + sample.yAxis * startSample.yAxis[0] + sample.zAxis * startSample.zAxis[0];
        const columnY = sample.xAxis * startSample.xAxis[1] + sample.yAxis * startSample.yAxis[1] + sample.zAxis * startSample.zAxis[1];
        const columnZ = sample.xAxis * startSample.xAxis[2] + sample.yAxis * startSample.yAxis[2] + sample.zAxis * startSample.zAxis[2];
        const translation = sample.origin -
            (columnX * startSample.origin[0] + columnY * startSample.origin[1] + columnZ * startSample.origin[2]);
        packedSamples[index] = vector([
                    columnX[0], columnX[1], columnX[2],
                    columnY[0], columnY[1], columnY[2],
                    columnZ[0], columnZ[1], columnZ[2],
                    translation[0], translation[1], translation[2]
                ]);
        stationFrames[index] = sample.frame;
    }

    const packedSpline = interpolateBSplineCurveThroughPoints(packedSamples, 3, stationParameters);
    const packedVelocity = differentiatedPackedSpline(packedSpline);
    const packedAcceleration = differentiatedPackedSpline(packedVelocity);
    const columnXSpline = normalizeSplineDefinition(unpackedComponentSpline(packedSpline, 0));
    const columnYSpline = normalizeSplineDefinition(unpackedComponentSpline(packedSpline, 3));
    const columnZSpline = normalizeSplineDefinition(unpackedComponentSpline(packedSpline, 6));
    const translationSpline = normalizeSplineDefinition(unpackedComponentSpline(packedSpline, 9));

    const drift = keepOrientation ? 0 : orthogonalityDriftAtSpanMidpoints(packedSpline);

    return {
            "columnX" : columnXSpline,
            "columnY" : columnYSpline,
            "columnZ" : columnZSpline,
            "translation" : translationSpline,
            "packedSpline" : packedSpline,
            "packedVelocity" : packedVelocity,
            "packedAcceleration" : packedAcceleration,
            "degree" : 3,
            "keepOrientation" : keepOrientation,
            "stationParameters" : stationParameters,
            "stationFrames" : stationFrames,
            "orthogonalityDrift" : drift,
            "driftTolerance" : driftTolerance,
            "events" : junctionParameters,
            "pathLength" : pathLength,
            "path" : path
        };
}

/**
 * Slices one 3-dimensional component spline out of the packed 12-dimensional fit
 * (components componentOffset .. componentOffset + 2 of each control point).
 */
function unpackedComponentSpline(packedSpline is map, componentOffset is number) returns map
{
    const count = size(packedSpline.controlPoints);
    var controlPoints = makeArray(count, 0);
    for (var index = 0; index < count; index += 1)
    {
        const packedPoint = packedSpline.controlPoints[index];
        controlPoints[index] = vector(packedPoint[componentOffset],
            packedPoint[componentOffset + 1],
            packedPoint[componentOffset + 2]);
    }
    return {
            "degree" : packedSpline.degree,
            "isPeriodic" : false,
            "isRational" : false,
            "controlPoints" : controlPoints,
            "knots" : knotArray(packedSpline.knots)
        };
}

/**
 * sup |A(t)^T A(t) - I| over every knot-span midpoint - the certification that the fitted
 * rotation stays rigid BETWEEN the exactly-rigid interpolation nodes. Evaluated directly on
 * the packed non-rational fit with one basis computation per midpoint.
 */
function orthogonalityDriftAtSpanMidpoints(packedSpline is map) returns number
{
    const knots = packedSpline.knots;
    const degree = packedSpline.degree;
    var worstDrift = 0;
    for (var knotIndex = degree; knotIndex < size(knots) - degree - 1; knotIndex += 1)
    {
        if (knots[knotIndex + 1] - knots[knotIndex] <= 1e-12)
        {
            continue;
        }
        const midpoint = 0.5 * (knots[knotIndex] + knots[knotIndex + 1]);
        const packedPoint = evaluatePackedPoint(packedSpline, midpoint);
        const columnX = vector(packedPoint[0], packedPoint[1], packedPoint[2]);
        const columnY = vector(packedPoint[3], packedPoint[4], packedPoint[5]);
        const columnZ = vector(packedPoint[6], packedPoint[7], packedPoint[8]);
        worstDrift = max(worstDrift, abs(dot(columnX, columnX) - 1));
        worstDrift = max(worstDrift, abs(dot(columnY, columnY) - 1));
        worstDrift = max(worstDrift, abs(dot(columnZ, columnZ) - 1));
        worstDrift = max(worstDrift, abs(dot(columnX, columnY)));
        worstDrift = max(worstDrift, abs(dot(columnX, columnZ)));
        worstDrift = max(worstDrift, abs(dot(columnY, columnZ)));
    }
    return worstDrift;
}

/**
 * The derivative of a packed non-rational spline as its own spline: standard B-spline
 * difference net (degree drops by one, one knot trimmed from each end).
 */
function differentiatedPackedSpline(spline is map) returns map
{
    const degree = spline.degree;
    const knots = spline.knots;
    const count = size(spline.controlPoints);
    var derivativeControlPoints = makeArray(count - 1, 0);
    for (var index = 0; index < count - 1; index += 1)
    {
        const spanWidth = knots[index + degree + 1] - knots[index + 1];
        derivativeControlPoints[index] = spanWidth <= 1e-15 ? 0 * spline.controlPoints[index] :
            (degree / spanWidth) * (spline.controlPoints[index + 1] - spline.controlPoints[index]);
    }
    var derivativeKnots = makeArray(size(knots) - 2, 0);
    for (var index = 0; index < size(knots) - 2; index += 1)
    {
        derivativeKnots[index] = knots[index + 1];
    }
    return {
            "degree" : degree - 1,
            "isPeriodic" : false,
            "isRational" : false,
            "controlPoints" : derivativeControlPoints,
            "knots" : knotArray(derivativeKnots)
        };
}

/**
 * Components offset .. offset + 2 of a packed value as a 3-vector.
 */
function sliceVector3(packedValue is Vector, offset is number) returns Vector
{
    return vector(packedValue[offset], packedValue[offset + 1], packedValue[offset + 2]);
}

/**
 * Point of the packed non-rational fit at `parameter`: one span lookup, one basis
 * computation, degree + 1 control-point combinations.
 */
function evaluatePackedPoint(packedSpline is map, parameter is number) returns Vector
{
    const degree = packedSpline.degree;
    const spanIndex = findEvaluationSpanIndex(packedSpline.knots, degree, parameter);
    const basisValues = bSplineBasisValues(packedSpline.knots, degree, spanIndex, parameter);
    var point = basisValues[0] * packedSpline.controlPoints[spanIndex - degree];
    for (var basisIndex = 1; basisIndex <= degree; basisIndex += 1)
    {
        point = point + basisValues[basisIndex] * packedSpline.controlPoints[spanIndex - degree + basisIndex];
    }
    return point;
}
