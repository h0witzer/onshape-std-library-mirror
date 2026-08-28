FeatureScript 3044;
import(path : "onshape/std/geometry.fs", version : "3044.0");
export import(path : "onshape/std/tool.fs", version : "3044.0");
export import(path : "onshape/std/profilecontrolmode.gen.fs", version : "3044.0");
export import(path : "onshape/std/sweeptwisttype.gen.fs", version : "3044.0");
export import(path : "onshape/std/sidegeometryrule.gen.fs", version : "3044.0");
export import(path : "onshape/std/manipulator.fs", version : "3044.0");
import(path : "onshape/std/boolean.fs", version : "3044.0");

/**
 * Tweep ("Tweakable Sweep"): a sweep with full twist control along the path.
 *
 * The twist is injected into the kernel sweep through a lock ribbon: a guide curve is
 * generated at a clearance radius around the path carrying the twist law, a ribbon
 * surface is lofted between the path and the guide, and `opSweep` runs with
 * `ProfileControlMode.LOCK_FACES` on the ribbon so the profile rolls with it. The guide
 * is constructed as an exact cubic interpolant of its station points rather than fitted
 * to them, so its control point count, knots, and parameterization all follow from the
 * station grid: clamped on an open path, periodic on a closed one. See
 * `createTwistGuide`.
 *
 * The twist law is a global amount (revolutions, pitch, or total angle) plus optional
 * per-vertex twist overrides, each pinning the absolute twist angle at one vertex of the
 * path. The law interpolates the pinned angles with a natural cubic spline in arc length;
 * with no overrides it is linear from zero at the path start to the global amount at the
 * end. Every override gets a draggable angular manipulator riding the ribbon edge, and a
 * top-level manipulator drives the total angle when the twist type is TWIST_ANGLE. The
 * override dialog and manipulator plumbing follow the vertex-override framework of the
 * standard library's ruled surface feature.
 */

// Manipulator key fragments. The change function parses these back out of the
// manipulator id to route a dragged angle to the right definition field.
const ANGLE_MANIPULATOR = "angleManipulator.";
const SCALE_MANIPULATOR = "scaleManipulator.";
// Separates a scale handle's override index from the reference length it was drawn against.
const REFERENCE_SEPARATOR = "@";
const OVERRIDE_MANIPULATOR = "O.";
const INDICES_MANIPULATOR = "vertexIndices";
const TOP_LEVEL_MANIPULATOR = "T.";

// Arc-length fraction within which an override vertex counts as sitting on a path
// endpoint, replacing that endpoint's implicit twist key.
const END_KEY_TOLERANCE = 1e-4;

// Arc-length fraction within which two station parameters are merged into one.
const STATION_PARAMETER_TOLERANCE = 1e-7;

// Station sampling. Density follows the total twist so the guide keeps its accuracy as
// the winding tightens; the bounds keep even an untwisted path well sampled and cap the
// control point count the guide curve hands to the kernel.
const STATIONS_PER_REVOLUTION = 24;

// The turning a single span is allowed to carry, bend and twist together. Stations are placed
// at equal increments of turning, so this sets accuracy directly rather than by proxy.
const TARGET_ANGULAR_STEP = 2 * PI * radian / STATIONS_PER_REVOLUTION;

// Reference grid the turning rate is measured on before the stations are placed.
const MONITOR_SAMPLE_COUNT = 257;

// Most sections a variable-scale loft will use. A loft runs smoothly between sections, so it
// needs fewer of them than the guide curve needs stations.
const MAX_LOFT_SECTIONS = 97;

// Scale bounds. The standard library's SCALE_BOUNDS stop at 1e-5 because a general transform
// cannot be singular, but a sweep may legitimately close to nothing: a section at zero is a point,
// and lofting to it is how a taper comes to a tip. Zero is therefore admitted here and handled by
// substituting a point for that section rather than by scaling a profile to nothing.
export const TWEEP_SCALE_BOUNDS =
{
    (unitless) : [0, 1, 1e5]
} as RealBoundSpec;

/**
 * An `IntegerBoundSpec` for path vertex indices. Vertices are counted along the path from its
 * start, so the ceiling only has to sit past any plausible path.
 */
export const TWEEP_VERTEX_INDEX_BOUND =
{
    (unitless) : [0, 0, 10000]
} as IntegerBoundSpec;

// Below this a section is treated as having closed to a point.
const POINT_SECTION_SCALE = 1e-9;

// Grid the profile is located against along the path. One evaluation, coarser than the monitor
// grid, since it only has to land in the right neighborhood before the tangent refines it.
const ANCHOR_SAMPLE_COUNT = 129;


const MINIMUM_STATION_COUNT = 65;
const MAXIMUM_STATION_COUNT = 1025;

// A uniform station this close to a twist key (as a fraction of the uniform spacing) is
// dropped in favor of the key, so no interpolation span collapses next to a full one.
const STATION_CROWDING_FRACTION = 0.25;

annotation { "Feature Type Name" : "Tweep",
        "Manipulator Change Function" : "tweepManipulator" }
export const twistedSweep = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Creation type", "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.bodyType is ExtendedToolBodyType;

        if (definition.bodyType == ExtendedToolBodyType.SOLID || definition.bodyType == ExtendedToolBodyType.THIN)
        {
            booleanStepTypePredicate(definition);
        }
        else
        {
            surfaceOperationTypePredicate(definition);
        }

        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            annotation { "Name" : "Faces and sketch regions to sweep",
                        "Filter" : (EntityType.FACE && GeometryType.PLANE) && ConstructionObject.NO }
            definition.profiles is Query;
        }
        else if (definition.bodyType == ExtendedToolBodyType.SURFACE)
        {
            annotation { "Name" : "Edges and sketch curves to sweep",
                        "Filter" : (EntityType.EDGE && ConstructionObject.NO) || (EntityType.BODY && BodyType.WIRE && SketchObject.NO)}
            definition.surfaceProfiles is Query;
        }
        else if (definition.bodyType == ExtendedToolBodyType.THIN)
        {
            annotation { "Name" : "Edges and sketch curves to sweep", "Filter" : (EntityType.EDGE || EntityType.FACE || (EntityType.BODY && BodyType.WIRE && SketchObject.NO)) && ConstructionObject.NO }
            definition.wallShape is Query;

            annotation { "Name" : "Mid plane", "Default" : false }
            definition.midplane is boolean;

            if (!definition.midplane)
            {
                annotation { "Name" : "Thickness 1" }
                isLength(definition.thickness1, ZERO_INCLUSIVE_OFFSET_BOUNDS);

                annotation { "Name" : "Flip wall", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.flipWall is boolean;

                annotation { "Name" : "Thickness 2" }
                isLength(definition.thickness2, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);
            }
            else
            {
                annotation { "Name" : "Thickness" }
                isLength(definition.thickness, ZERO_INCLUSIVE_OFFSET_BOUNDS);
            }
        }

        annotation { "Name" : "Sweep path", "Filter" : (EntityType.EDGE && ConstructionObject.NO) || (EntityType.BODY && BodyType.WIRE && SketchObject.NO) }
        definition.pathEdge is Query;

        if (definition.bodyType == ExtendedToolBodyType.THIN)
        {
            annotation { "Name" : "Trim ends", "Default" : true }
            definition.trimEnds is boolean;
        }

        annotation { "Name" : "Twist" }
        definition.hasTwist is boolean;

        annotation { "Group Name" : "Twist", "Driving Parameter" : "hasTwist", "Collapsed By Default" : false }
        {
            if (definition.hasTwist)
            {
                annotation { "Name" : "Twist type", "UIHint" : [UIHint.SHOW_LABEL, UIHint.REMEMBER_PREVIOUS_VALUE], "Default" : SweepTwistType.TURNS }
                definition.twistType is SweepTwistType;
                if (definition.twistType == SweepTwistType.TURNS)
                {
                    annotation { "Name" : "Revolutions", "Default" : 1 }
                    isReal(definition.turns, SWEEP_TURNS_BOUNDS);
                }
                if (definition.twistType == SweepTwistType.ANGLE)
                {
                    annotation { "Name" : "Rotation angle" }
                    isAngle(definition.angle, SWEEP_ANGLE_BOUNDS);
                }
                if (definition.twistType == SweepTwistType.PITCH)
                {
                    annotation { "Name" : "Pitch length" }
                    isLength(definition.pitch, SWEEP_PITCH_BOUNDS);
                }

                annotation { "Name" : "Twist opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
                definition.ccw is boolean;

            }
        }

        annotation { "Name" : "Scale", "UIHint" : [UIHint.DISPLAY_SHORT, UIHint.FIRST_IN_ROW] }
        definition.hasScale is boolean;

        if (definition.hasScale)
        {
            annotation { "Name" : "Scale factor", "UIHint" : UIHint.DISPLAY_SHORT, "Default" : 1.0 }
            isReal(definition.scaleFactor, TWEEP_SCALE_BOUNDS);
        }

        annotation { "Name" : "Smooth output" }
        definition.smoothOutput is boolean;

        // Vertices are picked by INDEX along the path, the way `editCurve` picks control points,
        // rather than one query per override. Clicking a vertex in the graphics toggles it into
        // this list, several can be held at once, and the list clears in one go -- where a query
        // per override meant a separate pick for every point being tweaked.
        annotation { "Name" : "Vertices", "Item name" : "vertex",
                    "Item label template" : "Vertex #indexValue", "Show labels only" : true,
                    "UIHint" : [UIHint.INITIAL_FOCUS, UIHint.PREVENT_ARRAY_REORDER, UIHint.ALLOW_ARRAY_FOCUS] }
        definition.selectedIndices is array;
        for (var selectedVertex in definition.selectedIndices)
        {
            annotation { "Name" : "Vertex index" }
            isInteger(selectedVertex.indexValue, TWEEP_VERTEX_INDEX_BOUND);
        }

        annotation { "Name" : "Overrides", "Item name" : "override",
                    "Item label template" : "Vertex #index",
                    "UIHint" : [UIHint.PREVENT_ARRAY_REORDER, UIHint.COLLAPSE_ARRAY_ITEMS] }
        definition.overrides is array;
        for (var override in definition.overrides)
        {
            annotation { "Name" : "Vertex index" }
            isInteger(override.index, TWEEP_VERTEX_INDEX_BOUND);

            annotation { "Name" : "Twist", "UIHint" : UIHint.DISPLAY_SHORT }
            override.overridesTwist is boolean;

            if (override.overridesTwist)
            {
                annotation { "Name" : "Added rotation" }
                isAngle(override.angleOverride, SWEEP_ANGLE_BOUNDS);

                annotation { "Name" : "Twist opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
                override.oppositeAngleOverride is boolean;
            }

            annotation { "Name" : "Scale", "UIHint" : [UIHint.DISPLAY_SHORT, UIHint.FIRST_IN_ROW] }
            override.overridesScale is boolean;

            if (override.overridesScale)
            {
                annotation { "Name" : "Scale factor", "UIHint" : UIHint.DISPLAY_SHORT, "Default" : 1.0 }
                isReal(override.scaleFactorOverride, TWEEP_SCALE_BOUNDS);
            }
        }

        if (definition.bodyType == ExtendedToolBodyType.SOLID || definition.bodyType == ExtendedToolBodyType.THIN)
        {
            booleanStepScopePredicate(definition);
        }
        else
        {
            surfaceJoinStepScopePredicate(definition);
        }
    }
   {
        // Convert the path query to individual edges when a wire body is selected to
        // avoid invalid edge evaluations in the downstream sweep logic.
        var sweepPath = definition.pathEdge;
        if (!isQueryEmpty(context, qEntityFilter(sweepPath, EntityType.BODY)))
        {
            sweepPath = qOwnedByBody(sweepPath, EntityType.EDGE);
        }


        // Resolve the profile query per body type. Thin sweeps also collect edges out of
        // any selected faces and wire bodies, and translate the thickness dialog fields.
        var profileQuery;
        if (definition.bodyType == ExtendedToolBodyType.SOLID)
        {
            profileQuery = definition.profiles;
        }
        else if (definition.bodyType == ExtendedToolBodyType.SURFACE)
        {
            profileQuery = definition.surfaceProfiles;
        }
        else if (definition.bodyType == ExtendedToolBodyType.THIN)
        {
            definition = setWallThickness(definition);

            const faces = qEntityFilter(definition.wallShape, EntityType.FACE);
            if (!isQueryEmpty(context, faces))
            {
                const extractedEdges = qAdjacent(faces, AdjacencyType.EDGE, EntityType.EDGE);
                definition.wallShape = qSubtraction(definition.wallShape, faces);
                definition.wallShape = qUnion([definition.wallShape, extractedEdges]);
            }

            const wireBodies = qEntityFilter(definition.wallShape, EntityType.BODY);
            if (!isQueryEmpty(context, wireBodies))
            {
                const extractedEdges = wireBodies->qOwnedByBody(EntityType.EDGE);
                definition.wallShape = qSubtraction(definition.wallShape, wireBodies);
                definition.wallShape = qUnion([definition.wallShape, extractedEdges]);
            }

            profileQuery = qConstructionFilter(definition.wallShape, ConstructionObject.NO);
        }

        const path = constructPath(context, sweepPath);
        validateOverrides(context, id, definition, path, evPathLength(context, path));
        const stations = buildTwistStations(context, definition, path, profileQuery);
        const twistRadius = computeTwistRadius(context, profileQuery, sweepPath, stations);
        const guidePoints = twistGuidePoints(stations, twistRadius);
        verifyGuideAdvances(context, path, stations, guidePoints);
        verifyGuideCloses(context, id, path, guidePoints, path.closed);
        const guide = {
                "points" : guidePoints,
                "parameters" : stations.parameters,
                "closed" : path.closed
            };

        try
        {
            buildTweep(context, id, definition, sweepPath, profileQuery, guide, stations, path);
            addTweepManipulators(context, id, definition, path, stations, twistRadius);
        }
        catch (error)
        {
            // Manipulators are added even when the build throws so the twist handles
            // stay draggable while the user repairs the inputs.
            try silent(addTweepManipulators(context, id, definition, path, stations, twistRadius));
            throw error;
        }

        const reconstructOp = function(innerId)
            {
                buildTweep(context, innerId, definition, sweepPath, profileQuery, guide, stations, path);
            };

        if (definition.bodyType == ExtendedToolBodyType.SOLID || definition.bodyType == ExtendedToolBodyType.THIN)
        {
            processNewBodyIfNeeded(context, id, definition, reconstructOp);
        }
        else if (definition.surfaceOperationType == NewSurfaceOperationType.ADD)
        {
            joinSurfaceBodiesWithAutoMatching(context, id, definition, false, reconstructOp);
        }
    },
    {
        bodyType : ExtendedToolBodyType.SOLID,
        operationType : NewBodyOperationType.NEW,
        twistType : SweepTwistType.TURNS,
        hasTwist : true,
        hasScale : false,
        scaleFactor : 1.0,
        surfaceOperationType : NewSurfaceOperationType.NEW,
        defaultSurfaceScope : true,
        overrides : [],
        selectedIndices : [],
        smoothOutput : false,
        trimEnds : true
    });

/**
 * Build the swept shape, then thicken it if this is a thin sweep.
 *
 * Two constructions live behind this, and which one runs is decided by whether the scale law
 * varies. A constant scale -- including a plain taper, which the kernel sweep takes as a single
 * factor -- goes through the lock ribbon and `opSweep`, the path that every twist-only case has
 * always used. A scale that changes along the path cannot be said in one number, so it is lofted
 * through profile sections instead; see `buildShapeByLoft`. Both leave the result under
 * `id + "shape"`, so everything downstream is shared.
 */
function buildTweep(context is Context, id is Id, definition is map, sweepPath is Query,
    profileQuery is Query, guide is map, stations is map, path is Path)
{
    // A thin sweep is built as a surface and given its walls afterwards.
    const shapeBodyType = definition.bodyType == ExtendedToolBodyType.THIN ?
        ExtendedToolBodyType.SURFACE : definition.bodyType;

    // Smooth output asks for the lofted construction whatever else is going on, because that is
    // what produces one unbroken surface around the sweep. Scale may leave no choice: the kernel
    // sweep takes a single factor and ramps it linearly from the path's start, so a law with any
    // shape to it -- or any scaling at all from a profile that is not at that start -- can only be
    // lofted. See `mustLoftForScale`.
    if (definition.smoothOutput || stations.mustLoftForScale)
    {
        buildShapeByLoft(context, id, definition, profileQuery, stations, shapeBodyType, path);
    }
    else
    {
        buildShapeBySweep(context, id, definition, sweepPath, profileQuery, guide, stations, shapeBodyType);
    }

    if (definition.bodyType == ExtendedToolBodyType.THIN)
    {
        const sheetBody = qBodyType(qCreatedBy(id + "shape", EntityType.BODY), BodyType.SHEET);

        var thickenDefinition = {
                "entities" : sheetBody,
                "thickness1" : definition.wallThickness_1,
                "thickness2" : definition.wallThickness_2
            };

        // Trim ends squares the wall off against the plane of the end station, instead of letting
        // the thickened sides run on past it at whatever angle the sweep left them. A closed path
        // has no ends to trim.
        const capFaces = definition.trimEnds && !stations.closed ?
            endCapPlaneFaces(context, id + "capPlanes", stations) : [];
        if (size(capFaces) > 0)
        {
            const sheetEdges = qOwnedByBody(sheetBody, EntityType.EDGE);
            thickenDefinition.sideGeometryRule = {
                    "type" : SideGeometryRule.SUPPLIED,
                    "sideSurfacesEdgeFaceGroups" : [
                        { "edges" : qIntersection([sheetEdges, capFaces[0].edges]), "face" : capFaces[0].face },
                        { "edges" : qIntersection([sheetEdges, capFaces[1].edges]), "face" : capFaces[1].face }
                    ]
                };
        }

        opThicken(context, id + "thicken", thickenDefinition);

        opDeleteBodies(context, id + "deleteSheet", { "entities" : sheetBody });
        for (var capFace in capFaces)
        {
            opDeleteBodies(context, id + ("deleteCapPlane" ~ capFace.index), { "entities" : capFace.body });
        }
    }
}

/**
 * A construction plane at each end of the path, with the swept edges that lie in it.
 *
 * `opThicken` will square a thin wall off against a supplied surface rather than running the sides
 * on at whatever angle the sweep left them, which is what "trim ends" asks for. The surface it
 * needs is just the plane of the end station -- and unlike the standard library's sweep, which has
 * to recover those planes from the geometry it produced, this already knows them: a station's
 * frame IS its plane, origin and tangent.
 *
 * @returns {array} : maps of `face`, `body`, `edges` and `index`, empty when either end has no
 *      edges lying in its plane.
 */
function endCapPlaneFaces(context is Context, id is Id, stations is map) returns array
{
    const lastStation = size(stations.parameters) - 1;
    var capFaces = [];
    for (var endNumber = 0; endNumber < 2; endNumber += 1)
    {
        const stationIndex = endNumber == 0 ? 0 : lastStation;
        const tangentLine = stations.tangentLines[stationIndex];
        const planeId = id + ("end" ~ endNumber);
        opPlane(context, planeId, {
                    "plane" : plane(tangentLine.origin, tangentLine.direction),
                    "width" : 1 * meter,
                    "height" : 1 * meter
                });
        capFaces = append(capFaces, {
                    "face" : qCreatedBy(planeId, EntityType.FACE),
                    "body" : qCreatedBy(planeId, EntityType.BODY),
                    "edges" : qCoincidesWithPlane(qEverything(EntityType.EDGE),
                            plane(tangentLine.origin, tangentLine.direction)),
                    "index" : endNumber
                });
    }
    return capFaces;
}

/**
 * The kernel sweep: a guide curve carrying the twist law, a ribbon lofted between it and the
 * path, and `opSweep` locked to that ribbon's faces so the profile rolls with it.
 *
 * Scale rides along as a single factor. The standard library's sweep offers it alongside
 * LOCK_FACES rather than inside the profile-control guard that excludes its own twist, so the
 * ribbon carries the rotation and `scaleFactor` carries the taper, independently. That factor is
 * a linear ramp from 1 at the start, which is exactly why a law with any shape to it has to go
 * the other way.
 */
function buildShapeBySweep(context is Context, id is Id, definition is map, sweepPath is Query,
    profileQuery is Query, guide is map, stations is map, shapeBodyType is ExtendedToolBodyType)
{
    createTwistGuide(context, id + "guideCurve", guide);
    const guideEdges = qCreatedBy(id + "guideCurve", EntityType.EDGE);

    const loftConnections = generatePathLengthLoftConnections(context, sweepPath, guideEdges);

    // The kernel route needs the same treatment as the lofted one, and needs it more: it is the
    // route a plain sweep takes, so it is where most failures actually land. The modeler's message
    // survives untouched -- it is the one that says WHAT went wrong -- and the path and profile go
    // red to say WHERE, which is what `sweep.fs` itself reds for a sweep that will not build.
    try silent
    {
        opLoft(context, id + "twistRibbon", {
                    "bodyType" : ExtendedToolBodyType.SURFACE,
                    "profileSubqueries" : [sweepPath, guideEdges],
                    "connections" : loftConnections
                });
    }
    catch
    {
        setErrorEntities(context, id, { "entities" : sweepPath });
        throw regenError("The twist guide could not be built along this path. Ease any tight bends, or reduce the twist.",
            ["pathEdge"], sweepPath);
    }

    try silent
    {
        opSweep(context, id + "shape", {
                    "bodyType" : shapeBodyType,
                    "path" : sweepPath,
                    "profiles" : profileQuery,
                    "profileControl" : ProfileControlMode.LOCK_FACES,
                    "lockFaces" : qCreatedBy(id + "twistRibbon", EntityType.FACE),
                    "hasScale" : stations.kernelScaleFactor != 1,
                    "scaleFactor" : stations.kernelScaleFactor
                });
    }
    catch (error)
    {
        setErrorEntities(context, id, { "entities" : qUnion([sweepPath, profileQuery]) });
        throw error;
    }

    opDeleteBodies(context, id + "cleanupRibbon", {
                "entities" : qUnion([
                            qCreatedBy(id + "guideCurve", EntityType.BODY),
                            qCreatedBy(id + "twistRibbon", EntityType.BODY)
                        ])
            });
}

/**
 * The lofted shape: the profile placed at each of a set of sections, carrying that station's
 * twist and scale, lofted through in order.
 *
 * This exists because a varying scale has nowhere to live in a kernel sweep, which accepts one
 * factor and ramps it linearly. Lofting instead makes the section at every key exact and lets the
 * surface run smoothly between them -- where sweeping each interval separately and gluing would
 * leave a crease at every join, since neighbouring segments would meet with different taper rates.
 *
 * A profile with a hole in it is built as a SHELL rather than as a solid minus its holes: every
 * loop of the profile is lofted as a SURFACE along the path, and those tubes are knitted to the
 * end sections, which are copies of the profile itself. Holes come along for free, because a copy
 * of the profile HAS them -- there is nothing to subtract and nothing to rebuild, so nothing is
 * approximated and no two pieces meet along a boundary they disagree about. A solid loft refuses a
 * profile with a hole, and refuses a wire outright; a surface loft takes the loops happily.
 *
 * A section whose scale has reached zero has no profile to place at all, so a point stands in for
 * it and every tube closes to that one apex.
 *
 * The sections are drawn from the same stations the guide uses, so they inherit the placement that
 * equidistributes turning: they crowd where the twist or the scale is doing something and thin out
 * where the path is quiet. They are subsampled to `MAX_LOFT_SECTIONS` because a loft interpolates
 * smoothly between sections and does not need one at every station.
 */
function buildShapeByLoft(context is Context, id is Id, definition is map, profileQuery is Query,
    stations is map, shapeBodyType is ExtendedToolBodyType, path is Path)
{
    // A RUN of sections that have all closed to a point is one point, not several. The stations
    // either side of a zero key can both evaluate to zero -- a vertex override and a path junction
    // at the same place resolve to parameters further apart than the station tolerance, so two
    // stations land beside each other and both collapse -- and a loft asked to run from a point to
    // a point has no profile to work from at all ("Could not create valid profiles from
    // selections", measured with sections 40 and 41 both points and a span of exactly [40..41]).
    const strided = loftSectionIndices(size(stations.parameters));
    var sectionIndices = [];
    var previousWasPoint = false;
    for (var stationIndex in strided)
    {
        const closesToPoint = stations.scaleValues[stationIndex] < POINT_SECTION_SCALE;
        if (closesToPoint && previousWasPoint)
        {
            continue;
        }
        sectionIndices = append(sectionIndices, stationIndex);
        previousWasPoint = closesToPoint;
    }

    const sectionSpans = loftSectionSpans(context, id, path, stations, sectionIndices, definition.smoothOutput);
    // Every section is the profile carried out of the station it was drawn at -- not out of the
    // first one. Stations before the anchor carry it backwards and stations after carry it
    // forwards, which is how the body comes to span the whole path from a profile part way along
    // it, the way the kernel sweep does. With the profile at the start the anchor is station 0 and
    // this is the identity it always was.
    const anchorFrame = stationFrame(stations, stations.anchorIndex);

    var placements = makeArray(size(sectionIndices));
    var pointProfiles = makeArray(size(sectionIndices), undefined);
    var helperBodies = [];
    for (var sectionNumber = 0; sectionNumber < size(sectionIndices); sectionNumber += 1)
    {
        const stationIndex = sectionIndices[sectionNumber];
        const scaleValue = stations.scaleValues[stationIndex];
        placements[sectionNumber] = toWorld(stationFrame(stations, stationIndex)) *
            scaleUniformly(max(scaleValue, POINT_SECTION_SCALE)) * fromWorld(anchorFrame);

        if (scaleValue < POINT_SECTION_SCALE)
        {
            const pointId = id + ("tip" ~ sectionNumber);
            opPoint(context, pointId, { "point" : stations.tangentLines[stationIndex].origin });
            pointProfiles[sectionNumber] = qCreatedBy(pointId, EntityType.VERTEX);
            helperBodies = append(helperBodies, qCreatedBy(pointId, EntityType.BODY));
        }
    }

    // A surface or thin sweep carries curves, which loft straight through as the one loop they are.
    if (definition.bodyType != ExtendedToolBodyType.SOLID)
    {
        opExtractWires(context, id + "curves", { "edges" : profileQuery });
        const curves = qCreatedBy(id + "curves", EntityType.BODY);
        helperBodies = append(helperBodies, curves);

        var curveProfiles = makeArray(size(placements));
        for (var sectionNumber = 0; sectionNumber < size(placements); sectionNumber += 1)
        {
            if (pointProfiles[sectionNumber] != undefined)
            {
                curveProfiles[sectionNumber] = pointProfiles[sectionNumber];
                continue;
            }
            const sectionId = id + ("curveSection" ~ sectionNumber);
            opPattern(context, sectionId, {
                        "entities" : curves,
                        "transforms" : [placements[sectionNumber]],
                        "instanceNames" : ["section"]
                    });
            curveProfiles[sectionNumber] = qCreatedBy(sectionId, EntityType.EDGE);
            helperBodies = append(helperBodies, qCreatedBy(sectionId, EntityType.BODY));
        }

        for (var spanNumber = 0; spanNumber < size(sectionSpans); spanNumber += 1)
        {
            // The modeler's own message is kept -- it is the one that says WHAT went wrong -- and
            // the red marks are added to say WHERE, which is the half it cannot know.
            try silent
            {
                opLoft(context, (id + "shape") + spanNumber, {
                            "bodyType" : shapeBodyType,
                            "profileSubqueries" : profilesOverSpan(curveProfiles, sectionSpans[spanNumber])
                        });
            }
            catch (error)
            {
                // The modeler's own message is kept -- it is the one that says WHAT went wrong --
                // and the geometry is attached to say WHERE, which is the half it cannot know.
                setErrorEntities(context, id, { "entities" :
                            spanErrorEntities(context, path, stations, profileQuery, sectionIndices,
                                sectionSpans[spanNumber]) });
                throw error;
            }
        }
        opDeleteBodies(context, id + "cleanupSections", { "entities" : qUnion(helperBodies) });
        return;
    }

    // A face is copied through its own sheet body: opPattern refuses a face that has a hole in it,
    // answering PATTERN_FACE_FAILED, while a sheet carrying that same face patterns without
    // complaint and its copy is identical to the last bit, hole and all.
    opExtractSurface(context, id + "sheets", { "faces" : profileQuery });
    const sheets = qCreatedBy(id + "sheets", EntityType.BODY);
    helperBodies = append(helperBodies, sheets);

    // Loop identity across the sections is carried by TRACKING QUERIES, started here, before
    // anything is patterned. A tracking query resolves to whatever later operations derive from
    // the entities it was given, so each loop's query finds precisely that loop's copy in every
    // section -- by descent, not by looking at the result and trying to tell the loops apart.
    // Geometry cannot always tell them apart: a square with a bore down its middle has an outer
    // loop and a hole that share a centroid exactly.
    const seedLoops = constructPaths(context,
            qAdjacent(qCreatedBy(id + "sheets", EntityType.FACE), AdjacencyType.EDGE, EntityType.EDGE), {});
    var loopTrackers = makeArray(size(seedLoops));
    for (var loopIndex = 0; loopIndex < size(seedLoops); loopIndex += 1)
    {
        loopTrackers[loopIndex] = startTracking(context, qUnion(seedLoops[loopIndex].edges));
    }

    // One copy of the whole profile per section: it carries every loop at once, and the trackers
    // sort out which edges are which afterwards.
    var sectionBodies = makeArray(size(placements), undefined);
    var sectionEdges = makeArray(size(placements), undefined);
    for (var sectionNumber = 0; sectionNumber < size(placements); sectionNumber += 1)
    {
        if (pointProfiles[sectionNumber] != undefined)
        {
            continue;
        }
        const sectionId = id + ("section" ~ sectionNumber);
        opPattern(context, sectionId, {
                    "entities" : sheets,
                    "transforms" : [placements[sectionNumber]],
                    "instanceNames" : ["section"]
                });
        sectionBodies[sectionNumber] = qCreatedBy(sectionId, EntityType.BODY);
        sectionEdges[sectionNumber] = qCreatedBy(sectionId, EntityType.EDGE);
        helperBodies = append(helperBodies, qCreatedBy(sectionId, EntityType.BODY));
    }

    // The caps are those copies at the two ends. A section that closed to a point needs no cap:
    // the tubes already meet there.
    var shellBodies = [];
    if (sectionBodies[0] != undefined)
    {
        shellBodies = append(shellBodies, sectionBodies[0]);
    }
    if (sectionBodies[size(placements) - 1] != undefined)
    {
        shellBodies = append(shellBodies, sectionBodies[size(placements) - 1]);
    }

    // One surface tube per loop, through that loop's own copies.
    for (var loopIndex = 0; loopIndex < size(loopTrackers); loopIndex += 1)
    {
        var tubeProfiles = makeArray(size(placements));
        for (var sectionNumber = 0; sectionNumber < size(placements); sectionNumber += 1)
        {
            tubeProfiles[sectionNumber] = pointProfiles[sectionNumber] != undefined ?
                pointProfiles[sectionNumber] :
                qIntersection([sectionEdges[sectionNumber], loopTrackers[loopIndex]]);
        }

        for (var spanNumber = 0; spanNumber < size(sectionSpans); spanNumber += 1)
        {
            const tubeId = id + ("tube" ~ loopIndex ~ "span" ~ spanNumber);
            try silent
            {
                opLoft(context, tubeId, {
                            "bodyType" : ExtendedToolBodyType.SURFACE,
                            "profileSubqueries" : profilesOverSpan(tubeProfiles, sectionSpans[spanNumber])
                        });
            }
            catch (error)
            {
                // The modeler's own message is kept -- it is the one that says WHAT went wrong --
                // and the geometry is attached to say WHERE, which is the half it cannot know.
                setErrorEntities(context, id, { "entities" :
                            spanErrorEntities(context, path, stations, profileQuery, sectionIndices,
                                sectionSpans[spanNumber]) });
                throw error;
            }
            shellBodies = append(shellBodies, qCreatedBy(tubeId, EntityType.BODY));
            helperBodies = append(helperBodies, qCreatedBy(tubeId, EntityType.BODY));
        }
    }

    // A knit that will not close is almost always a tube that came out wrong somewhere along the
    // path, so the ends of the sweep are marked as the stretch to look over.
    try silent
    {
        opEnclose(context, id + "shape", { "entities" : qUnion(shellBodies) });
    }
    catch
    {
        throw regenError("The swept surfaces would not knit into a solid. Sections that cross each other, or a profile too large for the path's curvature, are the usual causes.",
            ["pathEdge"], qUnion([qUnion(path.edges), profileQuery]));
    }
    opDeleteBodies(context, id + "cleanupSections", { "entities" : qUnion(helperBodies) });
}


/**
 * The station's frame with its twist applied: x along the path, z the transported normal rolled
 * about that tangent by the station's twist angle. This is the frame the profile rides in, and
 * the same rolled direction the guide curve is offset along.
 */
function stationFrame(stations is map, stationIndex is number) returns CoordSystem
{
    const tangent = stations.tangentLines[stationIndex].direction;
    const normal = stations.normals[stationIndex];
    const binormal = cross(tangent, normal);
    const twist = stations.twistValues[stationIndex];

    return coordSystem(stations.tangentLines[stationIndex].origin, tangent,
        cos(twist) * normal + sin(twist) * binormal);
}

/**
 * The geometry a failing span is built from: its own stretch of path, and the profile being swept.
 *
 * This is the pair the standard library reds for a sweep that will not build -- `sweep.fs` uses
 * `definition.path` and `definition.profiles` -- and between them they answer both halves of
 * "where": which part of the path, and what was being carried along it.
 */
function spanErrorEntities(context is Context, path is Path, stations is map, profileQuery is Query,
    sectionIndices is array, span is array) returns Query
{
    const fromParameter = stations.parameters[sectionIndices[span[0]]];
    const toParameter = stations.parameters[sectionIndices[span[size(span) - 1]]];
    return qUnion([pathEdgesCovering(context, path, stations.totalLength, fromParameter, toParameter),
                profileQuery]);
}

/** The profiles a span covers, in order. */
function profilesOverSpan(sectionProfiles is array, span is array) returns array
{
    var spanProfiles = makeArray(size(span));
    for (var index = 0; index < size(span); index += 1)
    {
        spanProfiles[index] = sectionProfiles[span[index]];
    }
    return spanProfiles;
}

/**
 * The runs of sections each loft covers.
 *
 * Smooth output is one run over the whole path, which is what makes the sweep a single unbroken
 * surface -- and it is the only mode a loft can refuse, because a run whose last section sits on
 * top of its first is a closed sequence and a loft will not build one.
 *
 * Otherwise the path is lofted a piece at a time and the pieces meet edge to edge, which knits
 * back into one body without a seam in the geometry, only in the surface parameterization. The
 * splits go where the path's own segments meet, since that is where its curvature is discontinuous
 * anyway. A closed path is split whatever its segment count, at the midpoint if it has no interior
 * junction to use -- a single edge looping back on itself would otherwise be one closed run again.
 *
 * @returns {array} : arrays of indices into `sectionIndices`, each a run for one loft.
 */
function loftSectionSpans(context is Context, topLevelId is Id, path is Path, stations is map,
    sectionIndices is array, smoothOutput is boolean) returns array
{
    const sectionCount = size(sectionIndices);
    var wholeRun = makeArray(sectionCount, 0);
    for (var index = 0; index < sectionCount; index += 1)
    {
        wholeRun[index] = index;
    }
    // A section that has closed to a point splits the run wherever it falls, smooth output or not.
    // A loft takes a point as its FIRST or LAST profile and nowhere else -- one sitting in the
    // middle answers "Could not create valid profiles from selections" -- and that restriction
    // matches the shape anyway: a sweep pinching to nothing and opening out again is two tapers
    // meeting at a tip, not one surface. Ending one run on the point and starting the next there
    // gives each loft the point at an end, and the tip is shared so the two still knit into one.
    var splits = [];
    for (var index = 1; index < sectionCount - 1; index += 1)
    {
        if (stations.scaleValues[sectionIndices[index]] < POINT_SECTION_SCALE)
        {
            splits = append(splits, index);
        }
    }

    // Smooth is otherwise one run over the whole path, and a closed path cannot have one: its last
    // section sits on top of its first, and a loft will not build a closed sequence. Rather than
    // refuse the sweep, it falls back to pieces and says so -- the same shape, carrying a
    // parameterization seam where the pieces meet instead of being one surface.
    if (smoothOutput && path.closed)
    {
        reportFeatureInfo(context, topLevelId,
            "Swept in pieces: a closed path cannot be one smooth surface, since the loft would have to close on itself.");
    }

    if (!smoothOutput || path.closed)
    {
        // Where the path's segments meet, as arc length fractions.
        var boundaries = [];
        var travelled = 0 * meter;
        for (var edgeIndex = 0; edgeIndex < size(path.edges) - 1; edgeIndex += 1)
        {
            travelled += evLength(context, { "entities" : path.edges[edgeIndex] });
            boundaries = append(boundaries, travelled / stations.totalLength);
        }
        if (path.closed && size(boundaries) == 0)
        {
            boundaries = [0.5];
        }

        // Cut at the section nearest each boundary. Stations land on those boundaries exactly when
        // buildTwistStations was given them, so "nearest" is normally "on".
        for (var boundary in boundaries)
        {
            var nearest = -1;
            var nearestDistance = 2;
            for (var index = 1; index < sectionCount - 1; index += 1)
            {
                const distance = abs(stations.parameters[sectionIndices[index]] - boundary);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = index;
                }
            }
            if (nearest > 0)
            {
                splits = append(splits, nearest);
            }
        }
    }

    if (size(splits) == 0)
    {
        return [wholeRun];
    }

    splits = sort(splits, function(first, second)
        {
            return first - second;
        });

    var spans = [];
    var runStart = 0;
    for (var split in splits)
    {
        // `split <= runStart` also drops duplicates, which a point landing on a path junction
        // produces honestly rather than by accident.
        if (split <= runStart || split > sectionCount - 1)
        {
            continue;
        }
        spans = append(spans, sectionRun(runStart, split));
        runStart = split;
    }
    if (runStart < sectionCount - 1)
    {
        spans = append(spans, sectionRun(runStart, sectionCount - 1));
    }
    return spans;
}

/** The consecutive section numbers from `from` to `to`, both included. */
function sectionRun(from is number, to is number) returns array
{
    var run = makeArray(to - from + 1, 0);
    for (var index = 0; index <= to - from; index += 1)
    {
        run[index] = from + index;
    }
    return run;
}

/**
 * Which stations become loft sections: every one when there are few enough, otherwise an evenly
 * strided subset with both ends kept. Striding a list that is already equidistributed in turning
 * preserves that property, so the sections stay concentrated where the shape is changing.
 */
function loftSectionIndices(stationCount is number) returns array
{
    if (stationCount <= MAX_LOFT_SECTIONS)
    {
        var everyStation = makeArray(stationCount, 0);
        for (var index = 0; index < stationCount; index += 1)
        {
            everyStation[index] = index;
        }
        return everyStation;
    }

    const stride = ceil((stationCount - 1) / (MAX_LOFT_SECTIONS - 1));
    var indices = [];
    for (var index = 0; index < stationCount - 1; index += stride)
    {
        indices = append(indices, index);
    }
    return append(indices, stationCount - 1);
}

/**
 * Radius at which the guide curve, and the twist manipulators riding it, orbit the path.
 *
 * Two things bound it. Clearance wants 1.5 times the distance from the path to the
 * furthest profile vertex, so the lock ribbon reaches past the swept material. The
 * path's tightest bend caps it: on a bend of radius R a guide at radius r advances only
 * (R - r)/R as fast as the path, so at r = R the guide stalls and beyond it the ribbon
 * folds. The cap is what keeps a profile that sits well off its path -- legal in a
 * sweep, and the case where the clearance figure balloons -- from demanding a guide
 * wider than the path can carry. Shrinking the guide costs nothing: the ribbon only
 * has to orient the profile, and may pass through it.
 */
function computeTwistRadius(context is Context, profileQuery is Query, sweepPath is Query, stations is map) returns ValueWithUnits
{
    var clearanceRadius = 1 * millimeter;

    const profileVertices = qAdjacent(profileQuery, AdjacencyType.VERTEX, EntityType.VERTEX);
    if (!isQueryEmpty(context, profileVertices))
    {
        var maxDistance = 0 * meter;
        const vertices = evaluateQuery(context, profileVertices);

        for (var vertex in vertices)
        {
            const distanceResult = evDistance(context, {
                        "side0" : vertex,
                        "side1" : sweepPath
                    });

            if (distanceResult.distance > maxDistance)
            {
                maxDistance = distanceResult.distance;
            }
        }

        if (maxDistance > 0 * meter)
        {
            clearanceRadius = maxDistance * 1.5;
        }
    }

    const foldFreeRadius = foldFreeGuideRadius(stations);
    return min(clearanceRadius, foldFreeRadius);
}

/**
 * Half the tightest radius of curvature along the path, which is the widest guide orbit
 * that still advances everywhere. Curvature is read off the station tangents already in
 * hand: the turn between neighboring tangents over the arc length between them. A path
 * with no measurable bend returns its own length, which no clearance radius reaches.
 */
function foldFreeGuideRadius(stations is map) returns ValueWithUnits
{
    var sharpestCurvature = 0 / meter;
    for (var stationIndex = 0; stationIndex < size(stations.parameters) - 1; stationIndex += 1)
    {
        const arcStep = stations.totalLength *
            (stations.parameters[stationIndex + 1] - stations.parameters[stationIndex]);
        if (arcStep <= 0 * meter)
        {
            continue;
        }
        const turn = norm(stations.tangentLines[stationIndex + 1].direction
                    - stations.tangentLines[stationIndex].direction);
        sharpestCurvature = max(sharpestCurvature, turn / arcStep);
    }

    if (sharpestCurvature * stations.totalLength < TOLERANCE.computational)
    {
        return stations.totalLength;
    }
    return 0.5 / sharpestCurvature;
}

// ---------------------------------------------------------------------------
// Twist law and station sampling
// ---------------------------------------------------------------------------

/**
 * Assemble everything the guide curve and the manipulators need: the station parameter
 * grid along the path, tangent lines and rotation-minimizing normals at each station,
 * the twist angle at each station, and the grid index of every override vertex.
 *
 * @returns {{
 *      @field parameters {array} : arc-length fractions of the stations, ascending.
 *      @field tangentLines {array} : path tangent [Line] per station.
 *      @field normals {array} : unit rotation-minimizing normal (unitless 3D vector) per station.
 *      @field twistValues {array} : twist angle per station.
 *      @field vertexStationIndices {array} : path vertex index -> station index.
 *      @field endOverridden {boolean} : whether an override pins the path end.
 * }}
 */
/**
 * The path edges covering a stretch of the path, as a query fit to hand to `regenError`.
 *
 * Highlighting real edges is what the standard library does -- `sweep.fs` reds `definition.path`
 * for a bad path and `definition.profiles` for a bad profile, `loft.fs` reds the guide entities --
 * and it beats a marker point on two counts: a whole edge is visible where a dot is easy to miss,
 * and it is the user's OWN geometry rather than something conjured to describe the problem.
 *
 * The tolerance means a stretch landing exactly on a junction lights both edges meeting there,
 * which is the honest answer about where a fault at a vertex lives.
 */
function pathEdgesCovering(context is Context, path is Path, totalLength is ValueWithUnits,
    fromParameter is number, toParameter is number) returns Query
{
    var covered = [];
    var startFraction = 0;
    for (var edgeIndex = 0; edgeIndex < size(path.edges); edgeIndex += 1)
    {
        const endFraction = startFraction +
            evLength(context, { "entities" : path.edges[edgeIndex] }) / totalLength;
        if (endFraction >= fromParameter - END_KEY_TOLERANCE &&
            startFraction <= toParameter + END_KEY_TOLERANCE)
        {
            covered = append(covered, path.edges[edgeIndex]);
        }
        startFraction = endFraction;
    }
    return size(covered) == 0 ? qNothing() : qUnion(covered);
}

/**
 * Show the given locations in red, the way the standard library marks the geometry behind a
 * failure.
 *
 * Points are built as real bodies, registered with `setErrorEntities`, then deleted again. The
 * registration is what makes this work: its display "is not rolled back even if the feature fails
 * and the entities themselves are rolled back" (error.fs), so the marks survive both the delete
 * and the throw that follows. `boolean.fs` reconstructs its failed bodies this same way.
 *
 * The whole thing is wrapped in `try silent` on purpose. A feature that throws while describing
 * why it threw reports the wrong failure entirely, and a missing red dot is a far smaller loss
 * than a misleading message.
 */
function markErrorPoints(context is Context, id is Id, points is array)
{
    if (size(points) == 0)
    {
        return;
    }
    try silent
    {
        const markId = id + "errorMarks";
        var marks = [];
        for (var pointIndex = 0; pointIndex < size(points); pointIndex += 1)
        {
            const pointId = markId + ("mark" ~ pointIndex);
            opPoint(context, pointId, { "point" : points[pointIndex] });
            marks = append(marks, qCreatedBy(pointId, EntityType.BODY));
        }
        const allMarks = qUnion(marks);
        setErrorEntities(context, id, { "entities" : allMarks });
        opDeleteBodies(context, markId + "delete", { "entities" : allMarks });
    }
}

/**
 * Check the overrides against the path they address before anything is built, so a bad index is
 * reported against the row that holds it rather than surfacing later as a geometry failure.
 *
 * `faultyArrayParameterId` is what puts the red on one ROW of the array rather than the whole
 * parameter, which is the difference between "an override is wrong" and "this override is wrong".
 */
function validateOverrides(context is Context, id is Id, definition is map, path is Path,
    totalLength is ValueWithUnits)
{
    if (size(definition.overrides) == 0)
    {
        return;
    }
    const vertexParameters = pathVertexParameters(context, path, totalLength);
    const vertexCount = size(vertexParameters);

    var seenAt = {};
    for (var overrideIndex = 0; overrideIndex < size(definition.overrides); overrideIndex += 1)
    {
        const vertexIndex = definition.overrides[overrideIndex].index;

        if (vertexIndex >= vertexCount)
        {
            throw regenError("Override " ~ (overrideIndex + 1) ~ " points at vertex " ~ vertexIndex ~
                ", but this path has vertices 0 to " ~ (vertexCount - 1) ~ ".",
                [faultyArrayParameterId("overrides", overrideIndex, "index")],
                qUnion(path.edges));
        }

        if (seenAt[vertexIndex] != undefined)
        {
            // The edges meeting at the contested vertex, so the red lands on the joint being
            // fought over rather than on a dot that could be any vertex on the path.
            throw regenError("Overrides " ~ (seenAt[vertexIndex] + 1) ~ " and " ~ (overrideIndex + 1) ~
                " both edit vertex " ~ vertexIndex ~ ", shown in red. Remove one of them.",
                [faultyArrayParameterId("overrides", overrideIndex, "index"),
                    faultyArrayParameterId("overrides", seenAt[vertexIndex], "index")],
                pathEdgesCovering(context, path, totalLength,
                    vertexParameters[vertexIndex], vertexParameters[vertexIndex]));
        }
        seenAt[vertexIndex] = overrideIndex;
    }
}

/**
 * Where each of the path's vertices falls, as an arc length fraction, in path order.
 *
 * This is the whole of what a vertex index means: index 0 is the path's start, the last index is
 * its end, and the interior ones are the joints between segments. A closed path has no separate
 * end -- its last vertex IS its first -- so it stops one short.
 *
 * Addressing overrides this way replaces a query pick per override, and with it the whole business
 * of deciding whether a picked vertex belongs to the path, or matching a coincident sketch point
 * onto one by position.
 */
function pathVertexParameters(context is Context, path is Path, totalLength is ValueWithUnits) returns array
{
    var parameters = [0];
    var travelled = 0 * meter;
    for (var edgeIndex = 0; edgeIndex < size(path.edges) - 1; edgeIndex += 1)
    {
        travelled += evLength(context, { "entities" : path.edges[edgeIndex] });
        parameters = append(parameters, travelled / totalLength);
    }
    if (!path.closed)
    {
        parameters = append(parameters, 1);
    }
    return parameters;
}

/**
 * Where along the path the profile sits, as an arc length fraction.
 *
 * A sweep does not have to start at the profile. Put the profile halfway along and the kernel
 * still sweeps the whole path, carrying the profile backwards as well as forwards from where it
 * was drawn -- a body that spans the path either way, measured and confirmed against `opSweep`.
 * The lofted construction has to be told the same thing, because it places its sections by mapping
 * the profile out of one station's frame, and mapping out of the start when the profile is not
 * there would slide the entire body off the end of the path by however far along it was.
 *
 * The profile is located by its bounding box center rather than its centroid: only the component
 * along the tangent is used, and every point of a profile shares that to first order, so the
 * lateral choice of proxy point does not matter -- while `evApproximateCentroid` carries an
 * explicit warning against driving geometry with it.
 *
 * The nearest point on the path is taken to be the one the profile belongs to. That is the reading
 * a sweep gives it too, and it is unambiguous whenever the profile is drawn on or near its path;
 * a profile held well to one side of a path that doubles back could in principle be nearer some
 * other pass, and there is no evidence in the model to say otherwise.
 */
function profileAnchorParameter(context is Context, path is Path, profileQuery is Query,
    totalLength is ValueWithUnits) returns number
{
    const profileBounds = evBox3d(context, { "topology" : profileQuery });
    const profilePoint = (profileBounds.minCorner + profileBounds.maxCorner) / 2;

    var sampleParameters = makeArray(ANCHOR_SAMPLE_COUNT, 0);
    for (var index = 0; index < ANCHOR_SAMPLE_COUNT; index += 1)
    {
        sampleParameters[index] = index / (ANCHOR_SAMPLE_COUNT - 1);
    }
    const tangentLines = evPathTangentLines(context, path, sampleParameters).tangentLines;

    var nearest = 0;
    var nearestDistance = squaredNorm(profilePoint - tangentLines[0].origin);
    for (var index = 1; index < ANCHOR_SAMPLE_COUNT; index += 1)
    {
        const distance = squaredNorm(profilePoint - tangentLines[index].origin);
        if (distance < nearestDistance)
        {
            nearestDistance = distance;
            nearest = index;
        }
    }

    // Refine off the grid: the profile belongs where the path's normal plane passes through it, so
    // step along the tangent by however far in front of or behind that sample the profile lies.
    const along = dot(profilePoint - tangentLines[nearest].origin, tangentLines[nearest].direction);
    return clamp(sampleParameters[nearest] + along / totalLength, 0, 1);
}

function buildTwistStations(context is Context, definition is map, path is Path,
    profileQuery is Query) returns map
{
    const totalLength = evPathLength(context, path);

    // Where the profile sits is needed before the laws are built, not after: the sweep holds the
    // profile fixed at that station, so both laws are measured from it.
    const anchorParameter = profileAnchorParameter(context, path, profileQuery, totalLength);

    // Where each path vertex falls, as an arc length fraction. This IS the index an override
    // names, so it is built before the laws that read those indices.
    const vertexParameters = pathVertexParameters(context, path, totalLength);

    const twistLaw = buildKeyedLaw(definition, vertexParameters, anchorParameter, true);
    const scaleLaw = buildKeyedLaw(definition, vertexParameters, anchorParameter, false);

    // Every path vertex earns a station: it is where a piecewise loft splits, and it is what an
    // override addresses by index.
    var requiredParameters = vertexParameters;

    // And one where the profile sits, so the loft has an exact frame to carry it out of.
    requiredParameters = append(requiredParameters, anchorParameter);

    const parameters = adaptiveStationParameters(context, path, twistLaw, scaleLaw, requiredParameters);
    const stationCount = size(parameters);

    // The station that landed on the anchor. Nearest rather than tolerance-matched: merging drops a
    // required parameter that coincides with one already placed, keeping the other copy, so what is
    // guaranteed is a station within tolerance of the anchor, not one holding its exact value.
    var anchorIndex = 0;
    var anchorDistance = abs(parameters[0] - anchorParameter);
    for (var stationIndex = 1; stationIndex < stationCount; stationIndex += 1)
    {
        const distance = abs(parameters[stationIndex] - anchorParameter);
        if (distance < anchorDistance)
        {
            anchorDistance = distance;
            anchorIndex = stationIndex;
        }
    }

    const tangentLines = evPathTangentLines(context, path, parameters).tangentLines;
    const normals = rotationMinimizingNormals(tangentLines);

    // The global twist, spread evenly along the path: the linear ramp the kernel sweep would apply
    // by itself, and what the twist law deviates FROM, so that a twist override left at its
    // default leaves the sweep exactly as the global had it. The scale has no equivalent here --
    // its global factor is already the far key of an absolute law.
    const globalTwistTotal = endTwistAngle(context, definition, totalLength) / radian;

    var twistValues = makeArray(stationCount, 0 * radian);
    var scaleValues = makeArray(stationCount, 1);
    var globalTwistValues = makeArray(stationCount, 0 * radian);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        const parameter = parameters[stationIndex];
        const globalTwistHere = globalTwistTotal * parameter;
        globalTwistValues[stationIndex] = globalTwistHere * radian;

        // Twist adds its global ramp in here, because the law carries only the deviation from it.
        // Scale does not: its keys are already the absolute factor at each station.
        twistValues[stationIndex] = (globalTwistHere +
                evaluateLocalLaw(twistLaw.parameters, twistLaw.values, twistLaw.slopes, parameter)) * radian;
        scaleValues[stationIndex] =
            evaluateLocalLaw(scaleLaw.parameters, scaleLaw.values, scaleLaw.slopes, parameter);
    }

    // The kernel's scale factor ramps from 1 at the START of the path, so it says what was meant
    // only when the profile is there to be the 1. With the profile part way along it cannot: asked
    // for a 1->2 taper on a path the profile straddles, `opSweep` built only the half BEFORE the
    // profile (measured, y -50..0 of a -50..+50 path), and through this feature's lock ribbon it
    // refuses outright with "Scale option is not allowed for multiprofile sweep". Any real scaling
    // from a profile that is not at the start is therefore lofted, where each section carries the
    // law's value at its own station and the anchoring already holds.
    // The whole of a kernel-expressible scale is the law's own far key: an exact key value rather
    // than an interpolated station value, so "no scale at all" stays exactly 1.
    const kernelScaleFactor = scaleLaw.values[size(scaleLaw.values) - 1];
    const scalesAtAll = scaleLaw.isVaried || kernelScaleFactor != 1;
    const anchoredAtStart = anchorParameter < END_KEY_TOLERANCE;

    // The station sitting on each path vertex, so a handle can be planted on every one of them.
    // Every vertex parameter was a required station parameter, so each of these is exact.
    var vertexStationIndices = makeArray(size(vertexParameters), 0);
    for (var vertexIndex = 0; vertexIndex < size(vertexParameters); vertexIndex += 1)
    {
        var nearestStation = 0;
        var nearestGap = abs(parameters[0] - vertexParameters[vertexIndex]);
        for (var stationIndex = 1; stationIndex < stationCount; stationIndex += 1)
        {
            const gap = abs(parameters[stationIndex] - vertexParameters[vertexIndex]);
            if (gap < nearestGap)
            {
                nearestGap = gap;
                nearestStation = stationIndex;
            }
        }
        vertexStationIndices[vertexIndex] = nearestStation;
    }

    return {
            "parameters" : parameters,
            "tangentLines" : tangentLines,
            "normals" : normals,
            "twistValues" : twistValues,
            "scaleValues" : scaleValues,
            "mustLoftForScale" : scaleLaw.isVaried || (scalesAtAll && !anchoredAtStart),
            "kernelScaleFactor" : kernelScaleFactor,
            "globalTwistValues" : globalTwistValues,
            "anchorIndex" : anchorIndex,
            "vertexParameters" : vertexParameters,
            "vertexStationIndices" : vertexStationIndices,
            "endOverridden" : twistLaw.endOverridden,
            "totalLength" : totalLength,
            "closed" : path.closed
        };
}

/**
 * Assemble one keyed law from the dialog: the twist law when `forTwist`, otherwise the scale law.
 *
 * Both are built the same way and that is deliberate, because they answer the same question.
 *
 * What this returns is the DEVIATION from the global amount, keyed at the overridden vertices and
 * at the identity everywhere else. It is not the total. Twist accumulates along a path, so an
 * override holding an absolute angle mid-path pins the whole law through that station -- drop one
 * into a sweep turning a full revolution and, at its default, that vertex becomes the one place
 * NOT twisted, with the turn crammed into the spans either side. Ruled surface never reads that
 * way because its angle is a per-vertex property: overriding one vertex tilts that vertex and
 * leaves its neighbours alone. A deviation is the same idea for a quantity that accumulates -- an
 * override at its default is a no-op, and what it holds is how much this vertex departs from the
 * twist the sweep would otherwise have there.
 *
 * Both implicit endpoint keys are therefore the identity for the quantity concerned: no extra
 * twist, or unit scale. An override sitting on either endpoint replaces that endpoint's implicit
 * key rather than fighting it.
 *
 * @returns {{
 *      @field parameters {array} : key positions, strictly ascending.
 *      @field values {array} : key values as plain numbers -- radians, or a scale factor.
 *      @field slopes {array} : the monotone slopes through those keys.
 *      @field overrideIndices {array} : the dialog array index each key came from, or -1.
 *      @field endOverridden {boolean} : whether an override replaced the far endpoint key.
 *      @field isVaried {boolean} : whether the law does anything a single number could not say.
 * }}
 */
function buildKeyedLaw(definition is map, vertexParameters is array,
    anchorParameter is number, forTwist is boolean) returns map
{
    const startValue = forTwist ? 0 : 1;

    // One selector serves both laws, and an item opts into each independently -- a vertex that
    // pins the twist need say nothing about the scale, and pinning it to the identity by accident
    // would be a real edit rather than a silent one.
    //
    // The top-level flag does NOT gate these. A vertex told to twist does it whether or not there
    // is a global twist to do it against, the same way ruled surface's vertex overrides answer to
    // nothing but themselves. A control that is ticked, holds a value, and quietly does nothing is
    // a bug in the dialog rather than a mode.
    var overrides = [];
    for (var overrideIndex = 0; overrideIndex < size(definition.overrides); overrideIndex += 1)
    {
        const item = definition.overrides[overrideIndex];
        if (forTwist ? item.overridesTwist : item.overridesScale)
        {
            overrides = append(overrides, { "item" : item, "sourceIndex" : overrideIndex });
        }
    }

    // Twist and scale part company here, over what the two quantities actually are.
    //
    // TWIST is relative by nature. There is no absolute roll for a profile, only roll relative to
    // something, and the sweep fixes that something by holding the profile as drawn. So the twist
    // law carries the DEVIATION from the global amount, both implicit endpoints at the identity,
    // and the global ramp is added in afterwards -- which is what lets a twist override left at
    // its default do nothing at all.
    //
    // SCALE is not relative. A factor at a station is a size, meaningful on its own, and nothing
    // in the geometry divides it back out again -- `fromWorld(anchorFrame)` is a coordinate system
    // and carries no scale. So scale keys are ABSOLUTE (owner's call): an override reading 2 means
    // the section IS twice size there, not twice whatever the taper would otherwise have given.
    // The implicit far key is the global factor, so with no overrides at all the law is exactly
    // the kernel's own ramp from 1. The cost of absolute is that a scale override left at its
    // default of 1 is a real edit -- it pins that vertex to unit size against a global taper.
    const endValue = forTwist ? startValue : (definition.hasScale ? definition.scaleFactor : 1);

    var keys = [];
    for (var overrideIndex = 0; overrideIndex < size(overrides); overrideIndex += 1)
    {
        const overrideItem = overrides[overrideIndex].item;
        if (overrideItem.index >= size(vertexParameters))
        {
            throw regenError("Override " ~ (overrides[overrideIndex].sourceIndex + 1) ~ " names vertex " ~
                overrideItem.index ~ ", but the path has only " ~ size(vertexParameters) ~
                " vertices (0 to " ~ (size(vertexParameters) - 1) ~ ").", ["overrides"]);
        }
        const fraction = vertexParameters[overrideItem.index];
        const value = forTwist ?
            (overrideItem.oppositeAngleOverride ? -overrideItem.angleOverride : overrideItem.angleOverride) / radian :
            overrideItem.scaleFactorOverride;
        keys = append(keys, {
                    "parameter" : fraction,
                    "value" : value,
                    "overrideIndex" : overrides[overrideIndex].sourceIndex
                });
    }
    keys = sort(keys, function(first, second)
        {
            return first.parameter - second.parameter;
        });
    for (var keyIndex = 0; keyIndex < size(keys) - 1; keyIndex += 1)
    {
        if (keys[keyIndex + 1].parameter - keys[keyIndex].parameter < END_KEY_TOLERANCE)
        {
            throw regenError("Two overrides pin the same location on the path. Remove one of them.",
                ["overrides"]);
        }
    }

    const startOverridden = size(keys) > 0 && keys[0].parameter < END_KEY_TOLERANCE;
    const endOverridden = size(keys) > 0 && keys[size(keys) - 1].parameter > 1 - END_KEY_TOLERANCE;
    var assembled = [];
    if (!startOverridden)
    {
        assembled = append(assembled, { "parameter" : 0, "value" : startValue, "overrideIndex" : -1 });
    }
    assembled = concatenateArrays([assembled, keys]);
    if (!endOverridden)
    {
        assembled = append(assembled, { "parameter" : 1, "value" : endValue, "overrideIndex" : -1 });
    }

    // A key nailing the PROFILE'S OWN STATION to the identity, and it is what makes an override
    // mean what it says. The sweep holds the profile exactly where it was drawn, so the roll it
    // actually applies at a station is this law's value there minus its value at the profile.
    // Without this key that subtracted constant follows the overrides around: pin 90 degrees at a
    // vertex near the profile and the profile's own value climbs with it, so the overridden vertex
    // nets to nothing while the entire rest of the sweep swings the other way instead. Measured on
    // one 90 degree override on one junction of a two-span path, moving only the profile: with the
    // profile at the path's start the junction rolled and the far end stayed square (20 x 10), and
    // with the profile ON that junction the junction stayed put and the far end rolled a quarter
    // turn (10 x 20). Keyed here, both read as "this vertex, that angle, measured from the
    // profile". The global amount is deliberately NOT nailed down this way -- it is a ramp along
    // the whole path and should stay evenly spread wherever the profile happens to sit.
    //
    // An override ON the profile's own station is the one thing this cannot rescue: a sweep passes
    // through the profile as drawn, so there is nothing for that station to rotate relative to.
    // Such a key is left to stand, and the rest of the sweep moves around it.
    //
    // The scale law takes no such key. Nothing divides the anchor's factor back out, so its keys
    // already say what they mean at every station, and pinning the anchor to 1 would flatten the
    // near half of a plain taper for no reason.
    var anchorAlreadyKeyed = false;
    for (var entry in assembled)
    {
        if (abs(entry.parameter - anchorParameter) < END_KEY_TOLERANCE)
        {
            anchorAlreadyKeyed = true;
        }
    }
    if (forTwist && !anchorAlreadyKeyed)
    {
        var withAnchor = [];
        var anchorPlaced = false;
        for (var entry in assembled)
        {
            if (!anchorPlaced && entry.parameter > anchorParameter)
            {
                withAnchor = append(withAnchor,
                    { "parameter" : anchorParameter, "value" : startValue, "overrideIndex" : -1 });
                anchorPlaced = true;
            }
            withAnchor = append(withAnchor, entry);
        }
        if (!anchorPlaced)
        {
            withAnchor = append(withAnchor,
                { "parameter" : anchorParameter, "value" : startValue, "overrideIndex" : -1 });
        }
        assembled = withAnchor;
    }

    var keyParameters = makeArray(size(assembled), 0);
    var keyValues = makeArray(size(assembled), 0);
    var overrideIndices = makeArray(size(assembled), -1);
    var closesToPoint = false;
    for (var keyIndex = 0; keyIndex < size(assembled); keyIndex += 1)
    {
        keyParameters[keyIndex] = assembled[keyIndex].parameter;
        keyValues[keyIndex] = assembled[keyIndex].value;
        overrideIndices[keyIndex] = assembled[keyIndex].overrideIndex;
        if (!forTwist && keyValues[keyIndex] < POINT_SECTION_SCALE)
        {
            closesToPoint = true;
        }
    }

    // What the flag has to answer is narrow: can the kernel sweep say this by itself? It takes one
    // scale factor and ramps it linearly from 1 at the path's start, so the only law within reach
    // is one sitting at the identity everywhere except the far endpoint -- a plain taper. Anything
    // pinned away from that end is out of reach, and so is a section that closes to nothing, which
    // no single factor expresses. Counting keys would not do here: the anchor key is the identity
    // by construction, and counting it would send every mid-path profile down the loft for nothing.
    var isVaried = closesToPoint;
    for (var keyIndex = 0; keyIndex < size(assembled); keyIndex += 1)
    {
        if (keyParameters[keyIndex] < 1 - END_KEY_TOLERANCE && keyValues[keyIndex] != startValue)
        {
            isVaried = true;
        }
    }

    return {
            "parameters" : keyParameters,
            "values" : keyValues,
            "slopes" : localLawSlopes(keyParameters, keyValues),
            "overrideIndices" : overrideIndices,
            "endOverridden" : endOverridden,
            "isVaried" : isVaried
        };
}

/**
 * The signed global twist over the whole path.
 *
 * Turns, angle and pitch mean here exactly what they mean in the standard library's sweep,
 * including its direction convention: `ccw` false is the negative sense, and a pitch that would
 * ask for more turns than `SWEEP_TURNS_BOUNDS` allows is refused with the count it computed.
 */
function endTwistAngle(context is Context, definition is map, totalLength is ValueWithUnits) returns ValueWithUnits
{
    if (!definition.hasTwist)
    {
        return 0 * radian;
    }

    var endTwist = 0 * radian;
    if (definition.twistType == SweepTwistType.TURNS)
    {
        endTwist = definition.turns * 2 * PI * radian;
    }
    else if (definition.twistType == SweepTwistType.ANGLE)
    {
        endTwist = definition.angle;
    }
    else if (definition.twistType == SweepTwistType.PITCH)
    {
        if (definition.pitch < TOLERANCE.zeroLength * meter)
        {
            throw regenError("Pitch must be greater than zero.", ["pitch"]);
        }
        endTwist = (totalLength / definition.pitch) * 2 * PI * radian;
        const maximumTurns = SWEEP_TURNS_BOUNDS[unitless][2];
        if (endTwist > maximumTurns * 2 * PI * radian)
        {
            throw regenError("Computed number of revolutions outside limit (between 0 and " ~ maximumTurns ~
                "). Current value is " ~ roundToPrecision(endTwist / (2 * PI * radian), 2) ~ ".", ["pitch"]);
        }
    }
    return definition.ccw ? endTwist : -endTwist;
}

/**
 * Slopes of a keyed law at its keys, by the Fritsch-Carlson monotone rule (PCHIP).
 *
 * The law has to be LOCAL. A key's influence must die at its neighbors, or a twist authored as
 * "one turn out, one turn back, then hold" cannot hold: a globally solved C-squared spline
 * distributes each key's curvature across the whole path, so the held run bows and the ends
 * overshoot the values that were asked for. Here every span sees only the keys that bound it.
 *
 * The rule is shape preserving as well as local, and that is what makes a held run exact. Where
 * neighboring keys carry the same value the secant is zero, the slope is set to zero, and the
 * cubic on that span is constant to the last bit. Where the law reverses, the slope at the
 * turning key is zero too, so the reversal is a clean stop rather than a swing past it. It also
 * never overshoots the keys, which is what keeps a scale law from dipping through zero between
 * two positive keys.
 *
 * Values are plain numbers: the sign tests and the harmonic mean read far better without units
 * riding along, so callers strip units on the way in and restore them on the way out.
 *
 * @param keyParameters {array} : strictly ascending positions.
 * @param keyValues {array} : one value per key.
 * @returns {array} : one slope per key, in value per unit parameter.
 */
function localLawSlopes(keyParameters is array, keyValues is array) returns array
{
    const keyCount = size(keyParameters);
    var slopes = makeArray(keyCount, 0);
    if (keyCount < 2)
    {
        return slopes;
    }

    var widths = makeArray(keyCount - 1, 0);
    var secants = makeArray(keyCount - 1, 0);
    for (var keyIndex = 0; keyIndex < keyCount - 1; keyIndex += 1)
    {
        widths[keyIndex] = keyParameters[keyIndex + 1] - keyParameters[keyIndex];
        secants[keyIndex] = (keyValues[keyIndex + 1] - keyValues[keyIndex]) / widths[keyIndex];
    }

    // Two keys are one span: matching the slopes to the secant makes it exactly linear, which is
    // what an untwisted or evenly twisted path should be.
    if (keyCount == 2)
    {
        slopes[0] = secants[0];
        slopes[1] = secants[0];
        return slopes;
    }

    for (var keyIndex = 1; keyIndex < keyCount - 1; keyIndex += 1)
    {
        const before = secants[keyIndex - 1];
        const after = secants[keyIndex];
        if (before * after <= 0)
        {
            // A turning point, or a flat run beside a sloped one. Either way the law stops here.
            slopes[keyIndex] = 0;
        }
        else
        {
            // Weighted harmonic mean of the two secants: it can never exceed three times the
            // smaller of them, which is the bound that rules overshoot out.
            const weightBefore = 2 * widths[keyIndex] + widths[keyIndex - 1];
            const weightAfter = widths[keyIndex] + 2 * widths[keyIndex - 1];
            slopes[keyIndex] = (weightBefore + weightAfter) /
                (weightBefore / before + weightAfter / after);
        }
    }

    slopes[0] = localLawEndSlope(secants[0], secants[1], widths[0], widths[1]);
    slopes[keyCount - 1] = localLawEndSlope(secants[keyCount - 2], secants[keyCount - 3],
            widths[keyCount - 2], widths[keyCount - 3]);
    return slopes;
}

/**
 * The end slope for the monotone rule: a one-sided three point estimate, then pulled back if it
 * disagrees in sign with the span it belongs to or reaches past three times its secant. Without
 * that clamp an end key can overshoot even though every interior key is well behaved.
 */
function localLawEndSlope(nearSecant is number, farSecant is number, nearWidth is number, farWidth is number) returns number
{
    var slope = ((2 * nearWidth + farWidth) * nearSecant - nearWidth * farSecant) / (nearWidth + farWidth);
    if (slope * nearSecant <= 0)
    {
        return 0;
    }
    if (nearSecant * farSecant <= 0 && abs(slope) > abs(3 * nearSecant))
    {
        return 3 * nearSecant;
    }
    return slope;
}

/**
 * The span of a keyed law containing `parameter`, and where inside it the parameter falls.
 *
 * @returns {{ @field index {number}, @field width {number}, @field fraction {number} }}
 */
function localLawSpan(keyParameters is array, parameter is number) returns map
{
    const keyCount = size(keyParameters);
    var spanIndex = keyCount - 2;
    for (var keyIndex = 0; keyIndex < keyCount - 1; keyIndex += 1)
    {
        if (parameter <= keyParameters[keyIndex + 1])
        {
            spanIndex = keyIndex;
            break;
        }
    }
    const width = keyParameters[spanIndex + 1] - keyParameters[spanIndex];
    var fraction = 0;
    if (width > 0)
    {
        fraction = min(max((parameter - keyParameters[spanIndex]) / width, 0), 1);
    }
    return { "index" : spanIndex, "width" : width, "fraction" : fraction };
}

/**
 * Evaluate a keyed law at one parameter, as a cubic Hermite over the span that contains it. Only
 * that span's two keys and their slopes are read, which is what keeps a key's effect inside its
 * own neighborhood.
 */
function evaluateLocalLaw(keyParameters is array, keyValues is array, slopes is array, parameter is number) returns number
{
    if (size(keyParameters) == 1)
    {
        return keyValues[0];
    }
    const span = localLawSpan(keyParameters, parameter);
    if (span.width <= 0)
    {
        return keyValues[span.index];
    }

    const fraction = span.fraction;
    const squared = fraction * fraction;
    const cubed = squared * fraction;

    return (2 * cubed - 3 * squared + 1) * keyValues[span.index]
        + (-2 * cubed + 3 * squared) * keyValues[span.index + 1]
        + ((cubed - 2 * squared + fraction) * slopes[span.index]
            + (cubed - squared) * slopes[span.index + 1]) * span.width;
}

/**
 * The rate of change of a keyed law at one parameter: the derivative of the cubic Hermite on the
 * span that contains it, in value per unit parameter.
 */
function localLawRate(keyParameters is array, keyValues is array, slopes is array, parameter is number) returns number
{
    if (size(keyParameters) < 2)
    {
        return 0;
    }
    const span = localLawSpan(keyParameters, parameter);
    if (span.width <= 0)
    {
        return 0;
    }

    const fraction = span.fraction;
    const squared = fraction * fraction;

    return (6 * squared - 6 * fraction) * (keyValues[span.index] - keyValues[span.index + 1]) / span.width
        + (3 * squared - 4 * fraction + 1) * slopes[span.index]
        + (3 * squared - 2 * fraction) * slopes[span.index + 1];
}

/**
 * Where to put the stations, so that every span carries the same amount of turning.
 *
 * A uniform grid sized by total twist gets the COUNT right and the PLACEMENT wrong. What the
 * guide curve has to resolve is not distance along the path but ANGLE: between two stations the
 * interpolated guide departs from the true swept helix by roughly the fourth power of the angle
 * turned across that span. Spread stations evenly along a path whose twist is concentrated -- a
 * turn out, a turn back, then a held straight run -- and the wound section is sampled at the same
 * rate as the section doing nothing, so the wound section overshoots while the straight one
 * spends stations it has no use for.
 *
 * Two things turn the guide, and both are measured here in the same currency, radians per unit
 * of path parameter:
 *
 *   - the twist law's own rate, which is the derivative of the Hermite law being interpolated;
 *   - the path's turning, which is how fast its tangent rotates, so a tight bend draws stations
 *     even where the twist is flat.
 *
 * Their sum is integrated along the path and stations are placed at equal increments of that
 * integral. Every span then turns by about `TARGET_ANGULAR_STEP` whatever mix of bend and twist
 * produced it, and the count follows from the total rather than being guessed. A baseline term
 * keeps a straight untwisted path on the uniform `MINIMUM_STATION_COUNT` grid it had before.
 */
function adaptiveStationParameters(context is Context, path is Path, twistLaw is map, scaleLaw is map,
    extraParameters is array) returns array
{
    var referenceParameters = makeArray(MONITOR_SAMPLE_COUNT, 0);
    for (var index = 0; index < MONITOR_SAMPLE_COUNT; index += 1)
    {
        referenceParameters[index] = index / (MONITOR_SAMPLE_COUNT - 1);
    }
    const referenceLines = evPathTangentLines(context, path, referenceParameters).tangentLines;

    // Turning rate at each reference sample. The baseline is what a featureless path still gets.
    const baselineRate = TARGET_ANGULAR_STEP * (MINIMUM_STATION_COUNT - 1);

    // Scale is measured against the law's OWN LARGEST value, not the local one. Dividing by the
    // local value is the obvious relative measure and it is a trap: a law that closes to a point
    // drives that denominator to zero, the rate diverges, and equidistributing a diverging monitor
    // banks every station into the last sliver of path. Measured before this: a 1->0 taper on a
    // curved path put all 1025 stations at parameter >= 0.997 and left the entire middle to one
    // straight ruling, losing 22% of the volume -- and it hid on straight paths, where a straight
    // ruling from the start section to the tip happens to be the right cone anyway. A tip needs no
    // extra resolution regardless: the profile vanishes there, so the absolute error vanishes too.
    // The law is monotone-interpolated and does not overshoot its keys, so the largest key value
    // is the largest the law ever takes.
    var largestScale = TOLERANCE.computational;
    for (var keyValue in scaleLaw.values)
    {
        largestScale = max(largestScale, abs(keyValue));
    }
    var turningRate = makeArray(MONITOR_SAMPLE_COUNT, 0 * radian);
    for (var index = 0; index < MONITOR_SAMPLE_COUNT; index += 1)
    {
        const twistRate = abs(localLawRate(twistLaw.parameters, twistLaw.values, twistLaw.slopes,
                    referenceParameters[index])) * radian;

        // Scale earns stations the same way, through the change it asks for: a law that doubles the
        // profile over a tenth of the path needs resolving as much as a bend does. One whole
        // relative change per unit parameter is rated as one radian of turning.
        const scaleRate = abs(localLawRate(scaleLaw.parameters, scaleLaw.values, scaleLaw.slopes,
                    referenceParameters[index])) / largestScale * radian;

        // Central difference of the tangent direction, one-sided at the ends.
        const before = max(index - 1, 0);
        const after = min(index + 1, MONITOR_SAMPLE_COUNT - 1);
        var pathRate = 0 * radian;
        if (after > before)
        {
            pathRate = angleBetween(referenceLines[before].direction, referenceLines[after].direction) /
                (referenceParameters[after] - referenceParameters[before]);
        }

        turningRate[index] = baselineRate + twistRate + scaleRate + pathRate;
    }

    // Cumulative turning, by trapezoid. Strictly increasing because the baseline is positive.
    var cumulativeTurning = makeArray(MONITOR_SAMPLE_COUNT, 0 * radian);
    for (var index = 1; index < MONITOR_SAMPLE_COUNT; index += 1)
    {
        const width = referenceParameters[index] - referenceParameters[index - 1];
        cumulativeTurning[index] = cumulativeTurning[index - 1] +
            (turningRate[index - 1] + turningRate[index]) * width / 2;
    }
    const totalTurning = cumulativeTurning[MONITOR_SAMPLE_COUNT - 1];

    var stationCount = ceil(totalTurning / TARGET_ANGULAR_STEP) + 1;
    stationCount = max(MINIMUM_STATION_COUNT, min(MAXIMUM_STATION_COUNT, stationCount));

    // Invert the cumulative curve: equal steps in turning, read back as path parameters.
    var placed = makeArray(stationCount, 0);
    var walkingIndex = 1;
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        const target = totalTurning * stationIndex / (stationCount - 1);
        while (walkingIndex < MONITOR_SAMPLE_COUNT - 1 && cumulativeTurning[walkingIndex] < target)
        {
            walkingIndex += 1;
        }
        const spanTurning = cumulativeTurning[walkingIndex] - cumulativeTurning[walkingIndex - 1];
        var withinSpan = 0;
        if (spanTurning > 0 * radian)
        {
            withinSpan = (target - cumulativeTurning[walkingIndex - 1]) / spanTurning;
        }
        withinSpan = min(max(withinSpan, 0), 1);
        placed[stationIndex] = referenceParameters[walkingIndex - 1] +
            withinSpan * (referenceParameters[walkingIndex] - referenceParameters[walkingIndex - 1]);
    }
    placed[0] = 0;
    placed[stationCount - 1] = 1;

    return mergeStationParameters(placed,
        concatenateArrays([twistLaw.parameters, scaleLaw.parameters, extraParameters]));
}

/**
 * Fold the twist key parameters into the placed stations. Keys always survive, so every pinned
 * angle lands exactly on a station; a placed station crowded against a key is dropped in favor of
 * it, measured against that station's own local spacing rather than a global one, since the
 * placement is deliberately not uniform.
 */
function mergeStationParameters(placedParameters is array, keyParameters is array) returns array
{
    const placedCount = size(placedParameters);
    var parameters = keyParameters;
    for (var index = 0; index < placedCount; index += 1)
    {
        const before = max(index - 1, 0);
        const after = min(index + 1, placedCount - 1);
        const localSpacing = (placedParameters[after] - placedParameters[before]) / max(after - before, 1);
        const crowdingTolerance = localSpacing * STATION_CROWDING_FRACTION;

        var crowded = false;
        for (var keyParameter in keyParameters)
        {
            if (abs(placedParameters[index] - keyParameter) < crowdingTolerance)
            {
                crowded = true;
                break;
            }
        }
        if (!crowded)
        {
            parameters = append(parameters, placedParameters[index]);
        }
    }

    parameters = sort(parameters, function(first, second)
        {
            return first - second;
        });

    var merged = [parameters[0]];
    for (var index = 1; index < size(parameters); index += 1)
    {
        if (parameters[index] - merged[size(merged) - 1] > STATION_PARAMETER_TOLERANCE)
        {
            merged = append(merged, parameters[index]);
        }
    }
    return merged;
}

/**
 * Rotation-minimizing normals along a chain of tangent lines, computed with the double
 * reflection method (Wang, Juettler, Zheng, Liu 2008): each step reflects the normal
 * across the bisecting plane of the chord to the next station, then across the plane
 * bisecting the reflected and true next tangents, transporting it with zero roll.
 * Standard library evaluators offer Frenet frames only, whose normals vanish on straight
 * segments and flip at inflections, so the transport is computed directly here (shared
 * technique with the solid sweep motion layer in custom-features/solidSweepUtils.fs).
 *
 * @param tangentLines {array} : path tangent [Line]s, in station order.
 * @returns {array} : one unit normal (unitless 3D vector) per station, each
 *      perpendicular to its station's tangent.
 */
function rotationMinimizingNormals(tangentLines is array) returns array
{
    const stationCount = size(tangentLines);
    var tangents = makeArray(stationCount, 0);
    var positions = makeArray(stationCount, 0);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        tangents[stationIndex] = tangentLines[stationIndex].direction;
        positions[stationIndex] = tangentLines[stationIndex].origin / meter;
    }

    var normal = perpendicularVector(tangents[0]);
    normal = normalize(normal - dot(normal, tangents[0]) * tangents[0]);

    var normals = makeArray(stationCount, 0);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        normals[stationIndex] = normal;

        if (stationIndex < stationCount - 1)
        {
            // First reflection: across the bisecting plane of the chord to the next station.
            const chord = positions[stationIndex + 1] - positions[stationIndex];
            const chordSquared = dot(chord, chord);
            var reflectedNormal = normal;
            var reflectedTangent = tangents[stationIndex];
            if (chordSquared > 0)
            {
                reflectedNormal = normal - (2 * dot(chord, normal) / chordSquared) * chord;
                reflectedTangent = tangents[stationIndex] - (2 * dot(chord, tangents[stationIndex]) / chordSquared) * chord;
            }
            // Second reflection: across the plane bisecting the reflected and true tangents.
            const tangentDifference = tangents[stationIndex + 1] - reflectedTangent;
            const differenceSquared = dot(tangentDifference, tangentDifference);
            if (differenceSquared > 0)
            {
                reflectedNormal = reflectedNormal -
                    (2 * dot(tangentDifference, reflectedNormal) / differenceSquared) * tangentDifference;
            }
            // Re-orthonormalize against the next tangent to shed roundoff.
            normal = normalize(reflectedNormal - dot(reflectedNormal, tangents[stationIndex + 1]) * tangents[stationIndex + 1]);
        }
    }
    return normals;
}

/**
 * Guide curve points: each station's path point pushed out by the twist radius along
 * the rotation-minimizing normal rolled by that station's twist angle. Positive twist
 * rotates right-handed about the path tangent.
 */
function twistGuidePoints(stations is map, twistRadius is ValueWithUnits) returns array
{
    const stationCount = size(stations.parameters);
    var guidePoints = makeArray(stationCount);
    for (var stationIndex = 0; stationIndex < stationCount; stationIndex += 1)
    {
        const tangent = stations.tangentLines[stationIndex].direction;
        const normal = stations.normals[stationIndex];
        const binormal = cross(tangent, normal);
        const twist = stations.twistValues[stationIndex];
        guidePoints[stationIndex] = stations.tangentLines[stationIndex].origin
            + twistRadius * (cos(twist) * normal + sin(twist) * binormal);
    }
    return guidePoints;
}

/**
 * Backstop against a guide that has folded back on itself: where the guide fails to
 * advance along the path, the lock ribbon crosses itself and the sweep that follows
 * either fails or produces a self-intersecting body. `computeTwistRadius` sizes the
 * guide to stay clear of this, so reaching here means the path turns more sharply than
 * the station sampling resolved.
 */
function verifyGuideAdvances(context is Context, path is Path, stations is map, guidePoints is array)
{
    // Every station where the guide stalls, not just the first: a path with two tight bends should
    // show both, or easing the one that got reported only uncovers the next.
    var stalls = [];
    for (var stationIndex = 0; stationIndex < size(guidePoints) - 1; stationIndex += 1)
    {
        const guideChord = guidePoints[stationIndex + 1] - guidePoints[stationIndex];
        if (dot(guideChord, stations.tangentLines[stationIndex].direction) <= 0 * meter)
        {
            stalls = append(stalls, stations.parameters[stationIndex]);
        }
    }
    if (size(stalls) == 0)
    {
        return;
    }
    // The edges carrying the tight stretch, rather than a dot on the path: the bend that has to be
    // eased is a piece of geometry, so that is what turns red.
    var tightEdges = [];
    for (var stallParameter in stalls)
    {
        tightEdges = append(tightEdges, pathEdgesCovering(context, path, stations.totalLength,
                stallParameter, stallParameter));
    }
    throw regenError("The sweep path turns too sharply for the twist guide to follow, along the edges shown in red. Ease the bend, reduce the twist, or split the path.",
        ["pathEdge"], qUnion(tightEdges));
}

/**
 * A closed path's last station sits on its first, so the guide can only be built as a
 * closed curve if the twist brings it back to where it started. That needs the twist to
 * complete whole turns, and needs the transported frame to close too -- around a
 * non-planar loop the rotation-minimizing frame returns rotated by its holonomy, which
 * leaves the same kind of gap. Either way the seam gap is real geometry rather than
 * roundoff, and closing the curve through it would misplace the profile.
 */
function verifyGuideCloses(context is Context, id is Id, path is Path, guidePoints is array, pathIsClosed is boolean)
{
    if (!pathIsClosed)
    {
        return;
    }
    if (!tolerantEquals(guidePoints[0], guidePoints[size(guidePoints) - 1]))
    {
        // Both: the path itself, so the seam is findable, and the two guide ends, whose separation
        // IS the shortfall and exists as no entity to highlight.
        markErrorPoints(context, id, [guidePoints[0], guidePoints[size(guidePoints) - 1]]);
        throw regenError("On a closed path the twist has to come back around to where it started, and it lands short by the gap between the two red marks. Set the twist to a whole number of revolutions.",
            ["twistType"], qUnion(path.edges));
    }
}

/**
 * Build the guide wire body from the station points: the periodic interpolant on a
 * closed path, the clamped one on an open path. Both pass exactly through every station.
 */
function createTwistGuide(context is Context, id is Id, guide is map)
{
    const guideCurve = guide.closed ?
        periodicGuideCurveThroughStations(guide.points, guide.parameters) :
        guideCurveThroughStations(guide.points, guide.parameters);

    opCreateBSplineCurve(context, id, { "bSplineCurve" : guideCurve });
}

/**
 * The twist guide for a closed path: a periodic cubic B-spline passing exactly through
 * every station point and closing on itself with no seam.
 *
 * The clamped construction cannot be reused here. Its spans are written out as Bezier
 * segments, which puts knots of multiplicity `degree` at every station, and a periodic
 * curve carrying that multiplicity at its seam is rejected as a non-smooth join. This
 * form instead keeps simple knots at the stations and solves for the control points
 * directly: collocating the cubic basis at each station gives one equation per station,
 * three control points wide, and periodicity wraps the first and last of those rows onto
 * each other. That wrap is the only structural difference from the clamped case -- a
 * cyclic tridiagonal system rather than a tridiagonal one, still solved in one pass.
 *
 * The stations run from the seam back to the seam, so the last one repeats the first and
 * is dropped; `verifyGuideCloses` has already established that it may be.
 *
 * @param guidePoints {array} : station points with length units, last repeating first.
 * @param stationParameters {array} : strictly increasing arc-length fractions.
 * @returns {BSplineCurve} : degree 3, non-rational, periodic, interpolating.
 */
function periodicGuideCurveThroughStations(guidePoints is array, stationParameters is array) returns BSplineCurve
{
    const degree = 3;
    const stationCount = size(guidePoints) - 1;
    if (stationCount < degree + 1)
    {
        throw regenError("The sweep path is too short to build a twist guide along.", ["pathEdge"]);
    }
    const period = stationParameters[size(stationParameters) - 1] - stationParameters[0];

    // Periodic knots: the station parameters, extended at both ends by wrapping a
    // degree's worth of them through one period.
    var knots = makeArray(stationCount + degree + 4, 0);
    for (var index = 0; index < degree; index += 1)
    {
        knots[index] = stationParameters[stationCount - degree + index] - period;
    }
    for (var index = 0; index < stationCount; index += 1)
    {
        knots[degree + index] = stationParameters[index];
    }
    for (var index = 0; index <= degree; index += 1)
    {
        knots[degree + stationCount + index] = stationParameters[index] + period;
    }

    // Collocate: at station i the nonzero cubic basis functions are those of control
    // points i, i+1 and i+2, so row i is banded three wide and slides by one per station.
    var lower = makeArray(stationCount, 0);
    var diagonal = makeArray(stationCount, 0);
    var upper = makeArray(stationCount, 0);
    var rightHandSide = makeArray(stationCount, 0 * guidePoints[0]);
    for (var index = 0; index < stationCount; index += 1)
    {
        const basisValues = bSplineBasisValues(knots, degree, index + degree, stationParameters[index]);
        lower[index] = basisValues[0];
        diagonal[index] = basisValues[1];
        upper[index] = basisValues[2];
        rightHandSide[index] = guidePoints[index];
    }

    // Rows are written against control points i..i+2, so the solution comes back shifted
    // by one from the control point it belongs to.
    const shiftedControlPoints = solveCyclicTridiagonal(lower, diagonal, upper, rightHandSide);

    var controlPoints = makeArray(stationCount + degree);
    for (var index = 0; index < stationCount; index += 1)
    {
        controlPoints[index] = shiftedControlPoints[(index + stationCount - 1) % stationCount];
    }
    // A periodic curve is stored with its first `degree` control points repeated at the end.
    for (var index = 0; index < degree; index += 1)
    {
        controlPoints[stationCount + index] = controlPoints[index];
    }

    return {
            "degree" : degree,
            "dimension" : 3,
            "isRational" : false,
            "isPeriodic" : true,
            "controlPoints" : controlPoints,
            "knots" : knotArray(knots)
        } as BSplineCurve;
}

/**
 * The `degree + 1` nonzero B-spline basis function values at `parameter`, which lies in
 * the knot span beginning at `knots[spanIndex]`. NURBS Book Algorithm A2.2: each degree
 * is built from the one below by the Cox-de Boor recurrence, carrying one term forward
 * so no basis function is evaluated twice and no denominator can vanish.
 *
 * @returns {array} : value of the basis function of control point `spanIndex - degree + k`
 *      at index `k`.
 */
function bSplineBasisValues(knots is array, degree is number, spanIndex is number, parameter is number) returns array
{
    var basisValues = makeArray(degree + 1, 0);
    var left = makeArray(degree + 1, 0);
    var right = makeArray(degree + 1, 0);
    basisValues[0] = 1;

    for (var order = 1; order <= degree; order += 1)
    {
        left[order] = parameter - knots[spanIndex + 1 - order];
        right[order] = knots[spanIndex + order] - parameter;
        var carried = 0;
        for (var index = 0; index < order; index += 1)
        {
            const denominator = right[index + 1] + left[order - index];
            const scaled = basisValues[index] / denominator;
            basisValues[index] = carried + right[index + 1] * scaled;
            carried = left[order - index] * scaled;
        }
        basisValues[order] = carried;
    }
    return basisValues;
}

/**
 * Solve a tridiagonal system by Thomas elimination. `lower[0]` and `upper[count - 1]`
 * are unused. The right hand side may be scalars or vectors; the solution follows it.
 */
function solveTridiagonal(lower is array, diagonal is array, upper is array, rightHandSide is array) returns array
{
    const count = size(diagonal);
    var workingDiagonal = diagonal;
    var workingRight = rightHandSide;
    for (var index = 1; index < count; index += 1)
    {
        const eliminationFactor = lower[index] / workingDiagonal[index - 1];
        workingDiagonal[index] = workingDiagonal[index] - eliminationFactor * upper[index - 1];
        workingRight[index] = workingRight[index] - eliminationFactor * workingRight[index - 1];
    }

    var solution = makeArray(count);
    solution[count - 1] = workingRight[count - 1] / workingDiagonal[count - 1];
    for (var index = count - 2; index >= 0; index -= 1)
    {
        solution[index] = (workingRight[index] - upper[index] * solution[index + 1]) / workingDiagonal[index];
    }
    return solution;
}

/**
 * Solve a tridiagonal system that wraps: `lower[0]` is the coefficient in the top right
 * corner and `upper[count - 1]` the one in the bottom left, as periodicity produces.
 *
 * Sherman-Morrison handles the two corners. Subtracting a rank one term from the matrix
 * leaves a plain tridiagonal one; solving that against both the real right hand side and
 * against the rank one term's own column, then combining, recovers the cyclic solution
 * in two Thomas passes rather than a general banded factorization.
 */
function solveCyclicTridiagonal(lower is array, diagonal is array, upper is array, rightHandSide is array) returns array
{
    const count = size(diagonal);
    const topRightCorner = lower[0];
    const bottomLeftCorner = upper[count - 1];
    // Any nonzero value works; the negated first diagonal keeps the modified matrix
    // comfortably away from a zero pivot.
    const shift = -diagonal[0];

    var modifiedDiagonal = diagonal;
    modifiedDiagonal[0] = diagonal[0] - shift;
    modifiedDiagonal[count - 1] = diagonal[count - 1] - topRightCorner * bottomLeftCorner / shift;

    const primarySolution = solveTridiagonal(lower, modifiedDiagonal, upper, rightHandSide);

    var rankOneColumn = makeArray(count, 0);
    rankOneColumn[0] = shift;
    rankOneColumn[count - 1] = bottomLeftCorner;
    const correction = solveTridiagonal(lower, modifiedDiagonal, upper, rankOneColumn);

    const weightedPrimary = primarySolution[0] + (topRightCorner / shift) * primarySolution[count - 1];
    const weightedCorrection = 1 + correction[0] + (topRightCorner / shift) * correction[count - 1];

    var solution = makeArray(count);
    for (var index = 0; index < count; index += 1)
    {
        solution[index] = primarySolution[index] - (weightedPrimary / weightedCorrection) * correction[index];
    }
    return solution;
}

/**
 * The twist guide as a cubic B-spline passing exactly through every station point, in
 * piecewise Bezier form.
 *
 * The curve is constructed rather than fitted: the C-squared cubic interpolant of the
 * station points is solved directly and each of its spans is written out as a Bezier
 * segment, so the control point count, the knots, and the parameterization are all
 * determined by the station grid instead of negotiated by a fitter. Parameterization is
 * the path's own arc-length fraction, which is strictly increasing by construction, so
 * coincident or near-coincident guide points -- which a chord-length parameterization
 * turns into a singular system, and which is how a tightly wound guide degenerates --
 * cannot make the interpolation ill-conditioned.
 *
 * @param guidePoints {array} : 3D points with length units, one per station.
 * @param stationParameters {array} : strictly increasing arc-length fractions, one per
 *      station, matching `guidePoints` in order.
 * @returns {BSplineCurve} : degree 3, non-rational, non-periodic, interpolating.
 */
function guideCurveThroughStations(guidePoints is array, stationParameters is array) returns BSplineCurve
{
    const stationCount = size(guidePoints);
    if (stationCount < 4)
    {
        throw regenError("The sweep path is too short to build a twist guide along.", ["pathEdge"]);
    }
    const secondDerivatives = interpolantSecondDerivatives(guidePoints, stationParameters);
    const spanCount = stationCount - 1;

    // Each span contributes its first three Bezier points; the final station closes the
    // last span, giving 3 * spanCount + 1 control points.
    var controlPoints = makeArray(3 * spanCount + 1);
    for (var spanIndex = 0; spanIndex < spanCount; spanIndex += 1)
    {
        const spanWidth = stationParameters[spanIndex + 1] - stationParameters[spanIndex];
        const spanChord = guidePoints[spanIndex + 1] - guidePoints[spanIndex];
        const curvatureScale = spanWidth * spanWidth / 18;

        controlPoints[3 * spanIndex] = guidePoints[spanIndex];
        controlPoints[3 * spanIndex + 1] = guidePoints[spanIndex] + spanChord / 3
            - curvatureScale * (2 * secondDerivatives[spanIndex] + secondDerivatives[spanIndex + 1]);
        controlPoints[3 * spanIndex + 2] = guidePoints[spanIndex + 1] - spanChord / 3
            - curvatureScale * (secondDerivatives[spanIndex] + 2 * secondDerivatives[spanIndex + 1]);
    }
    controlPoints[3 * spanCount] = guidePoints[stationCount - 1];

    // Clamped piecewise Bezier knots: the ends carry degree + 1 copies, every interior
    // station carries degree copies.
    var knots = makeArray(3 * spanCount + 5, 0);
    knots[0] = stationParameters[0];
    knots[1] = stationParameters[0];
    knots[2] = stationParameters[0];
    knots[3] = stationParameters[0];
    for (var stationIndex = 1; stationIndex < stationCount - 1; stationIndex += 1)
    {
        knots[3 * stationIndex + 1] = stationParameters[stationIndex];
        knots[3 * stationIndex + 2] = stationParameters[stationIndex];
        knots[3 * stationIndex + 3] = stationParameters[stationIndex];
    }
    const lastParameter = stationParameters[stationCount - 1];
    knots[3 * spanCount + 1] = lastParameter;
    knots[3 * spanCount + 2] = lastParameter;
    knots[3 * spanCount + 3] = lastParameter;
    knots[3 * spanCount + 4] = lastParameter;

    return {
            "degree" : 3,
            "dimension" : 3,
            "isRational" : false,
            "isPeriodic" : false,
            "controlPoints" : controlPoints,
            "knots" : knotArray(knots)
        } as BSplineCurve;
}

/**
 * Second derivatives of the C-squared cubic interpolant through `points` at
 * `parameters`, solved by tridiagonal elimination.
 *
 * End conditions are clamped to the derivative of the cubic through the four points
 * nearest each end, which tracks a smooth guide to fourth order there. A natural end
 * condition would instead force the guide's curvature to zero at the path ends, flatting
 * the twist in exactly the spans where the profile's start and end orientation is set.
 *
 * @param points {array} : length-valued points, one per station.
 * @param parameters {array} : strictly increasing, one per station.
 * @returns {array} : one length-valued second derivative per station.
 */
function interpolantSecondDerivatives(points is array, parameters is array) returns array
{
    const count = size(points);
    const startDerivative = endpointDerivative(points, parameters, [0, 1, 2, 3]);
    const endDerivative = endpointDerivative(points, parameters, [count - 1, count - 2, count - 3, count - 4]);

    var diagonal = makeArray(count, 0);
    var upper = makeArray(count, 0);
    var lower = makeArray(count, 0);
    var rightHandSide = makeArray(count, 0 * points[0]);

    const firstWidth = parameters[1] - parameters[0];
    diagonal[0] = 2 * firstWidth;
    upper[0] = firstWidth;
    rightHandSide[0] = 6 * ((points[1] - points[0]) / firstWidth - startDerivative);

    for (var index = 1; index < count - 1; index += 1)
    {
        const widthBefore = parameters[index] - parameters[index - 1];
        const widthAfter = parameters[index + 1] - parameters[index];
        lower[index] = widthBefore;
        diagonal[index] = 2 * (widthBefore + widthAfter);
        upper[index] = widthAfter;
        rightHandSide[index] = 6 * ((points[index + 1] - points[index]) / widthAfter
                    - (points[index] - points[index - 1]) / widthBefore);
    }

    const lastWidth = parameters[count - 1] - parameters[count - 2];
    lower[count - 1] = lastWidth;
    diagonal[count - 1] = 2 * lastWidth;
    rightHandSide[count - 1] = 6 * (endDerivative - (points[count - 1] - points[count - 2]) / lastWidth);

    // The system is diagonally dominant, so elimination needs no pivoting.
    for (var index = 1; index < count; index += 1)
    {
        const eliminationFactor = lower[index] / diagonal[index - 1];
        diagonal[index] -= eliminationFactor * upper[index - 1];
        rightHandSide[index] = rightHandSide[index] - eliminationFactor * rightHandSide[index - 1];
    }

    var secondDerivatives = makeArray(count, 0 * points[0]);
    secondDerivatives[count - 1] = rightHandSide[count - 1] / diagonal[count - 1];
    for (var index = count - 2; index >= 0; index -= 1)
    {
        secondDerivatives[index] = (rightHandSide[index] - upper[index] * secondDerivatives[index + 1]) / diagonal[index];
    }
    return secondDerivatives;
}

/**
 * The derivative at `indices[0]` of the cubic through the four listed stations, by Newton
 * divided differences. The indices run away from the end being estimated, so the same
 * formula serves the start (ascending) and the end (descending).
 */
function endpointDerivative(points is array, parameters is array, indices is array) returns Vector
{
    const firstParameter = parameters[indices[0]];
    const secondParameter = parameters[indices[1]];
    const thirdParameter = parameters[indices[2]];
    const fourthParameter = parameters[indices[3]];

    const firstDifference = (points[indices[1]] - points[indices[0]]) / (secondParameter - firstParameter);
    const secondPairDifference = (points[indices[2]] - points[indices[1]]) / (thirdParameter - secondParameter);
    const thirdPairDifference = (points[indices[3]] - points[indices[2]]) / (fourthParameter - thirdParameter);

    const secondDifference = (secondPairDifference - firstDifference) / (thirdParameter - firstParameter);
    const laterSecondDifference = (thirdPairDifference - secondPairDifference) / (fourthParameter - secondParameter);
    const thirdDifference = (laterSecondDifference - secondDifference) / (fourthParameter - firstParameter);

    return firstDifference + secondDifference * (firstParameter - secondParameter)
        + thirdDifference * (firstParameter - secondParameter) * (firstParameter - thirdParameter);
}

// ---------------------------------------------------------------------------
// Twist overrides and manipulators
// ---------------------------------------------------------------------------

/**
 * Add the angular manipulators: one per twist override, anchored at its path vertex with
 * the rotation axis along the path tangent and the zero reference on the untwisted
 * rotation-minimizing normal, plus a top-level manipulator at the path end driving the
 * total angle when the twist type is TWIST_ANGLE and no override pins the end.
 */
function addTweepManipulators(context is Context, id is Id, definition is map, path is Path, stations is map, twistRadius is ValueWithUnits)
{
    // Every path vertex, clickable, with the picked ones lit. The graphics selection and the
    // dialog's list are the same thing: clicking a vertex toggles it into the list, and clearing
    // the list unlights it. This is what replaces a query pick per override.
    const vertexCount = size(stations.vertexStationIndices);
    var vertexPoints = makeArray(vertexCount);
    for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex += 1)
    {
        vertexPoints[vertexIndex] = stations.tangentLines[stations.vertexStationIndices[vertexIndex]].origin;
    }
    var selectedVertices = [];
    for (var entry in definition.selectedIndices)
    {
        // A stale index outsits its path when segments are removed; drop it rather than let the
        // manipulator light a point that is not there.
        if (entry.indexValue < vertexCount)
        {
            selectedVertices = append(selectedVertices, entry.indexValue);
        }
    }
    addManipulators(context, id, {
                (INDICES_MANIPULATOR) : togglePointsManipulator({
                            "points" : vertexPoints,
                            "selectedIndices" : selectedVertices,
                            "suppressedIndices" : []
                        })
            });

    // Handles follow the SELECTION, not the overrides array. Selecting a vertex must be enough to
    // start editing it -- `editCurve` puts a manipulator on a point the moment it is picked and
    // creates the stored edit when you drag, and having to press "add override" first to make a
    // handle appear is the thing that made this feel worse than it. Vertices that already carry an
    // override keep their handles whether or not they are selected, so an existing edit is never
    // left ungrabbable.
    var handleVertices = {};
    for (var entry in definition.selectedIndices)
    {
        if (entry.indexValue < vertexCount)
        {
            handleVertices[entry.indexValue] = true;
        }
    }
    var storedOverrides = {};
    for (var overrideItem in definition.overrides)
    {
        if (overrideItem.index < vertexCount)
        {
            handleVertices[overrideItem.index] = true;
            storedOverrides[overrideItem.index] = overrideItem;
        }
    }

    for (var vertexIndex in keys(handleVertices))
    {
        const stationIndex = stations.vertexStationIndices[vertexIndex];
        const stored = storedOverrides[vertexIndex];

        // A vertex picked but not yet edited shows its handles at rest -- no twist, unit scale --
        // so the first drag reads as a change from where the sweep already is.
        const signedAngle = (stored != undefined && stored.overridesTwist) ?
            (stored.oppositeAngleOverride ? -stored.angleOverride : stored.angleOverride) : 0 * degree;
        const scaleFactor = (stored != undefined && stored.overridesScale) ? stored.scaleFactorOverride : 1;

        // The twist handle sweeps the DEVIATION, which is what dragging it edits, but it starts
        // from where the global twist already put the profile rather than from the untwisted
        // normal. So its arrow spans exactly the override's contribution and its tip still lands on
        // the real geometry. Rolling the zero reference this way, rather than drawing the total and
        // subtracting a reference in the change function, is what keeps the change function
        // arithmetic-free: `addTwistAngleManipulator` brings the drawn angle into one signed
        // revolution, and that wrap is not invertible once two angles have been added together.
        const globalHere = stations.globalTwistValues[stationIndex];
        const tangent = stations.tangentLines[stationIndex].direction;
        const normal = stations.normals[stationIndex];
        const rolledNormal = cos(globalHere) * normal + sin(globalHere) * cross(tangent, normal);

        addTwistAngleManipulator(context, id, OVERRIDE_MANIPULATOR ~ toString(vertexIndex),
            stations.tangentLines[stationIndex], rolledNormal, twistRadius, signedAngle);

        // No such trick needed for scale: the stored factor is already the absolute size at this
        // station, so the handle reaches the section's true size against the plain reference and
        // offset over reference is the factor itself.
        addScaleManipulator(context, id, OVERRIDE_MANIPULATOR ~ toString(vertexIndex),
            stationFrame(stations, stationIndex), profileScaleReference(context, definition),
            scaleFactor);
    }

    if (definition.hasTwist && definition.twistType == SweepTwistType.ANGLE &&
        !path.closed && !stations.endOverridden)
    {
        const endStationIndex = size(stations.parameters) - 1;
        const signedEndTwist = definition.ccw ? definition.angle : -definition.angle;
        addTwistAngleManipulator(context, id, TOP_LEVEL_MANIPULATOR ~ "-1",
            stations.tangentLines[endStationIndex], stations.normals[endStationIndex], twistRadius, signedEndTwist);
    }
}

/**
 * The length a scale handle's offset is measured against.
 *
 * A scale is unitless, but a linear manipulator drags a distance, so the two need a reference. It
 * has to be computable in the manipulator CHANGE function as well as where the handle is drawn,
 * and that function is given only the context and the definition -- no path, no stations. Half the
 * profile's bounding box diagonal is therefore the choice: it depends on the profile alone, it
 * does not move while the user drags, and it puts the handle at about the size of the thing being
 * scaled.
 */
function profileScaleReference(context is Context, definition is map) returns ValueWithUnits
{
    var profileQuery = definition.wallShape;
    if (definition.bodyType == ExtendedToolBodyType.SOLID)
    {
        profileQuery = definition.profiles;
    }
    else if (definition.bodyType == ExtendedToolBodyType.SURFACE)
    {
        profileQuery = definition.surfaceProfiles;
    }

    if (isQueryEmpty(context, profileQuery))
    {
        return 1 * centimeter;
    }
    const profileBounds = evBox3d(context, { "topology" : profileQuery });
    const halfDiagonal = norm(profileBounds.maxCorner - profileBounds.minCorner) / 2;
    return halfDiagonal > TOLERANCE.zeroLength * meter ? halfDiagonal : 1 * centimeter;
}

/**
 * One linear scale manipulator, sliding along the station's rolled normal.
 *
 * The offset IS the factor, read against `profileScaleReference`: at rest the handle stands one
 * reference out, and dragging it to twice that is scale 2. That gives the same drag-a-distance
 * feel as the ruled surface's handles while editing a unitless number.
 */
function addScaleManipulator(context is Context, id is Id, manipulatorId is string, frame is CoordSystem,
    reference is ValueWithUnits, scaleFactor is number)
{
    // The reference travels in the manipulator's own name. The change function is handed nothing
    // but the manipulators and the definition, so anything it would otherwise have to recompute
    // from geometry is a chance for it to disagree with what was drawn -- or to throw, which a
    // change function does silently, leaving the drag with no visible effect at all.
    const namedWithReference = manipulatorId ~ REFERENCE_SEPARATOR ~ (reference / millimeter);

    addManipulators(context, id, { (SCALE_MANIPULATOR ~ namedWithReference) : linearManipulator({
                        "base" : frame.origin,
                        "direction" : frame.zAxis,
                        "offset" : reference * scaleFactor,
                        "style" : ManipulatorStyleEnum.DEFAULT
                    }) });
}

/**
 * One angular twist manipulator: axis along the station tangent, arrow tip starting on
 * the untwisted normal at the twist radius, swept by the signed angle so the tip rides
 * the twist ribbon edge. The angle is brought into one signed revolution while keeping
 * its sign, matching the arrow direction convention of the ruled surface manipulators.
 */
function addTwistAngleManipulator(context is Context, id is Id, manipulatorId is string, tangentLine is Line, normal is Vector, twistRadius is ValueWithUnits, angle is ValueWithUnits)
{
    var adjustedAngle = angle % (2 * PI * radian);
    if (angle < 0)
    {
        adjustedAngle = adjustedAngle - 2 * PI * radian;
    }
    addManipulators(context, id, { (ANGLE_MANIPULATOR ~ manipulatorId) : angularManipulator({
                        "axisOrigin" : tangentLine.origin,
                        "axisDirection" : tangentLine.direction,
                        "rotationOrigin" : tangentLine.origin + twistRadius * normal,
                        "angle" : adjustedAngle,
                        "style" : ManipulatorStyleEnum.DEFAULT,
                        "minValue" : -2 * PI * radian,
                        "maxValue" : 2 * PI * radian,
                        "disableMinimumOffset" : true
                    }) });
}

/**
 * Write one dragged value onto every vertex the gesture should reach.
 *
 * A drag reaches the WHOLE current selection when the handle being dragged belongs to it, which is
 * what lets several vertices be set in one gesture -- pick four vertices, drag any one of their
 * handles, and all four take that value. A handle whose vertex is not in the selection edits only
 * itself, so a stray drag cannot quietly rewrite points the user is not looking at. A selected
 * vertex with no override yet gets one made for it, the way `editCurve` creates a control point
 * edit the first time a selected point is dragged.
 */
function writeOverrideEdit(definition is map, draggedVertex is number, edit is map) returns map
{
    if (draggedVertex < 0)
    {
        return definition;
    }

    var targets = {};
    var draggedIsSelected = false;
    for (var entry in definition.selectedIndices)
    {
        targets[entry.indexValue] = true;
        if (entry.indexValue == draggedVertex)
        {
            draggedIsSelected = true;
        }
    }
    if (!draggedIsSelected)
    {
        targets = { (draggedVertex) : true };
    }

    // Onto the overrides that already exist, clearing each target as it is served so that whatever
    // is left over is exactly the set needing a new entry.
    for (var overrideIndex = 0; overrideIndex < size(definition.overrides); overrideIndex += 1)
    {
        var item = definition.overrides[overrideIndex];
        if (targets[item.index] != true)
        {
            continue;
        }
        targets[item.index] = undefined;
        for (var field in keys(edit))
        {
            item[field] = edit[field];
        }
        definition.overrides[overrideIndex] = item;
    }

    for (var vertexIndex in keys(targets))
    {
        var item = {
                "index" : vertexIndex,
                "overridesTwist" : false,
                "angleOverride" : 0 * degree,
                "oppositeAngleOverride" : false,
                "overridesScale" : false,
                "scaleFactorOverride" : 1
            };
        for (var field in keys(edit))
        {
            item[field] = edit[field];
        }
        definition.overrides = append(definition.overrides, item);
    }
    return definition;
}

/**
 * Manipulator change function: routes a dragged angle back into the definition. The
 * manipulator key encodes whether it is the top-level twist handle or an override
 * handle, and the override's array index. Magnitude goes to the angle field and the
 * sign to the opposite-direction flag, so typed and dragged values stay consistent.
 */
export function tweepManipulator(context is Context, definition is map, newManipulators is map) returns map
{
    const angleRegex = ANGLE_MANIPULATOR ~ "(" ~ TOP_LEVEL_MANIPULATOR ~ "|" ~
        OVERRIDE_MANIPULATOR ~ ")(-?\\d+)";
    const scaleRegex = SCALE_MANIPULATOR ~ OVERRIDE_MANIPULATOR ~ "(-?\\d+)" ~
        REFERENCE_SEPARATOR ~ "([0-9.eE+-]+)";

    // Vertex picks in the graphics area. The toggle manipulator hands back the whole selected set
    // every time, so this mirrors it straight into the dialog's list.
    if (newManipulators[INDICES_MANIPULATOR] is map)
    {
        definition.selectedIndices = mapArray(newManipulators[INDICES_MANIPULATOR].selectedIndices,
            index => { "indexValue" : index });
    }

    for (var manipulatorKey in keys(newManipulators))
    {
        const manipulator = newManipulators[manipulatorKey];

        // A dragged scale handle: its offset over the reference it was drawn against is the factor.
        const scaleParsed = match(manipulatorKey, scaleRegex);
        if (scaleParsed.hasMatch)
        {
            const overrideIndex = stringToNumber(scaleParsed.captures[1]);
            const reference = stringToNumber(scaleParsed.captures[2]) * millimeter;
            if (overrideIndex >= 0 && reference > TOLERANCE.zeroLength * meter)
            {
                const bounds = TWEEP_SCALE_BOUNDS[unitless];
                definition = writeOverrideEdit(definition, overrideIndex, {
                            "overridesScale" : true,
                            "scaleFactorOverride" :
                                min(max(abs(manipulator.offset) / reference, bounds[0]), bounds[2])
                        });
            }
            continue;
        }

        const parsed = match(manipulatorKey, angleRegex);
        if (!parsed.hasMatch || manipulator.manipulatorType != ManipulatorType.ANGULAR)
        {
            continue;
        }
        if (parsed.captures[1] == TOP_LEVEL_MANIPULATOR)
        {
            definition.angle = abs(manipulator.angle);
            definition.ccw = manipulator.angle >= 0;
        }
        else
        {
            const overrideIndex = stringToNumber(parsed.captures[2]);
            definition = writeOverrideEdit(definition, overrideIndex, {
                        "overridesTwist" : true,
                        "angleOverride" : abs(manipulator.angle),
                        "oppositeAngleOverride" : manipulator.angle < 0
                    });
        }
    }
    return definition;
}

// ---------------------------------------------------------------------------
// Loft connection helpers
// ---------------------------------------------------------------------------

const ZERO_LENGTH_TOLERANCE = TOLERANCE.zeroLength * meter;
const RATIO_TOLERANCE = TOLERANCE.computational; // Dimensionless tolerance for ratio comparison

/**
 * Generate path length parameterized loft connections between two edge groups.
 * This ensures proper domain matching when one curve is segmented and the other is smooth.
 *
 * @param context : The context
 * @param edgeGroup1 : Query for the first edge group (typically the segmented sweep path)
 * @param edgeGroup2 : Query for the second edge group (typically the smooth guide curve)
 * @returns array : Array of connection maps for opLoft
 */
function generatePathLengthLoftConnections(context is Context, edgeGroup1 is Query, edgeGroup2 is Query) returns array
{
    // Construct ordered paths from the edge groups
    var path1 = constructPath(context, edgeGroup1);
    var path2 = constructPath(context, edgeGroup2);

    var totalLength1 = evPathLength(context, path1);
    var totalLength2 = evPathLength(context, path2);

    // Get all vertices from path1 (the segmented path)
    var allVertices1 = qAdjacent(edgeGroup1, AdjacencyType.VERTEX, EntityType.VERTEX);

    // Calculate path length ratios for all vertices in path1
    var pathRatios = [];
    for (var i = 0; i < evaluateQueryCount(context, allVertices1); i += 1)
    {
        var currentPoint = qNthElement(allVertices1, i);
        var pathLengthRatio = calculatePathLengthRatioForVertex(context, currentPoint, path1, totalLength1);
        pathRatios = append(pathRatios, pathLengthRatio);
    }

    // Remove duplicates and sort
    pathRatios = removeDuplicateRatios(pathRatios);
    pathRatios = sort(pathRatios, function(first, second)
        {
            return first - second;
        });

    // Create connections at all path length ratios
    var loftConnections = [];
    for (var ratio in pathRatios)
    {
        // Calculate edge and parameter on path2 (guide curve) for this ratio
        var edge2Info = calculateLocalEdgeParameterFromRatio(context, path2, ratio, totalLength2);

        // Find the edge in path1 that contains this ratio
        var edge1Info = calculateLocalEdgeParameterFromRatio(context, path1, ratio, totalLength1);

        // Create connection from edge1 to edge2
        var connectionMap = {
            "connectionEntities" : qUnion([edge1Info.edge, edge2Info.edge]),
            "connectionEdges" : [edge1Info.edge, edge2Info.edge],
            "connectionEdgeParameters" : [edge1Info.parameter, edge2Info.parameter]
        };

        loftConnections = append(loftConnections, connectionMap);
    }

    return loftConnections;
}

/**
 * Calculate the path length ratio (0 to 1) of a vertex position along a path.
 */
function calculatePathLengthRatioForVertex(context is Context, vertex is Query, path is Path, totalLength is ValueWithUnits) returns number
{
    // Find which edge in the ordered path contains this vertex
    var pathLengthBeforeEdge = 0 * meter;

    for (var i = 0; i < size(path.edges); i += 1)
    {
        var edge = path.edges[i];
        var edgeLength = evLength(context, {
                    "entities" : edge
                });

        // Check if vertex is on this edge by using evDistance
        var dist = evDistance(context, {
                    "side0" : vertex,
                    "side1" : edge,
                    "arcLengthParameterization" : true
                });

        // If the vertex is very close to this edge, it's on this edge
        if (dist.distance < ZERO_LENGTH_TOLERANCE)
        {
            var arcLengthParameter = dist.sides[1].parameter;
            var lengthAlongEdge = edgeLength * arcLengthParameter;

            // Account for edge flipping in the path
            if (path.flipped[i])
            {
                lengthAlongEdge = edgeLength - lengthAlongEdge;
            }

            var totalPathLengthToVertex = pathLengthBeforeEdge + lengthAlongEdge;
            return totalPathLengthToVertex / totalLength;
        }

        pathLengthBeforeEdge = pathLengthBeforeEdge + edgeLength;
    }

    // If we didn't find the vertex on any edge, return 0 (shouldn't happen)
    return 0;
}

/**
 * Calculate the local edge and parameter for a given global path ratio.
 */
function calculateLocalEdgeParameterFromRatio(context is Context, path is Path, globalRatio is number, totalLength is ValueWithUnits) returns map
{
    // Clamp the ratio to [0, 1] to handle numerical errors
    globalRatio = max(0.0, min(1.0, globalRatio));

    var targetPathLength = totalLength * globalRatio;
    var accumulatedLength = 0 * meter;

    // Find which edge contains this path length
    var edgeIndex = 0;
    for (var i = 0; i < size(path.edges); i += 1)
    {
        var edgeLength = evLength(context, {
                    "entities" : path.edges[i]
                });

        if (accumulatedLength + edgeLength >= targetPathLength || i == size(path.edges) - 1)
        {
            edgeIndex = i;
            break;
        }

        accumulatedLength += edgeLength;
    }

    // Calculate the local parameter on this edge
    var edgeLength = evLength(context, {
                "entities" : path.edges[edgeIndex]
            });

    var lengthIntoEdge = targetPathLength - accumulatedLength;
    var parameterOnEdge = lengthIntoEdge / edgeLength;

    // Account for edge flipping
    if (path.flipped[edgeIndex])
    {
        parameterOnEdge = 1.0 - parameterOnEdge;
    }

    // Clamp parameter to [0, 1] to avoid numerical errors
    parameterOnEdge = max(0.0, min(1.0, parameterOnEdge));

    return {
        "edge" : path.edges[edgeIndex],
        "parameter" : parameterOnEdge
    };
}

/**
 * Remove duplicate ratios from an array (within tolerance).
 */
function removeDuplicateRatios(ratios is array) returns array
{
    var unique = [];
    for (var ratio in ratios)
    {
        var isDuplicate = false;
        for (var existing in unique)
        {
            if (abs(ratio - existing) < RATIO_TOLERANCE)
            {
                isDuplicate = true;
                break;
            }
        }
        if (!isDuplicate)
        {
            unique = append(unique, ratio);
        }
    }
    return unique;
}

/**
 * Helper function to set wall thickness for thin sweeps
 * Converts thickness parameters to wallThickness_1 and wallThickness_2
 */
function setWallThickness(definition is map) returns map
{
    definition.wallThickness_1 = definition.thickness1;
    definition.wallThickness_2 = definition.thickness2;

    if (definition.midplane)
    {
        definition.wallThickness_1 = definition.thickness / 2;
        definition.wallThickness_2 = definition.wallThickness_1;
        return definition;
    }
    if (definition.flipWall)
    {
        definition.wallThickness_1 = definition.thickness2;
        definition.wallThickness_2 = definition.thickness1;
    }
    return definition;
}
