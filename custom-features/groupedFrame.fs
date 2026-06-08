FeatureScript 2625;
import(path : "onshape/std/geometry.fs", version : "2625.0");

/**
 * Grouped Frame feature - creates frame structures with multiple profile groups.
 * Each group can have its own profile sketch and path selections. Groups are ordered
 * by priority: earlier groups are treated as trim tools for later groups.
 * Automatic boolean trimming is applied so later groups are cut by all preceding groups.
 */
annotation {
        "Feature Type Name" : "Grouped Frame",
        "Editing Logic Function" : "groupedFrameEditLogic"
    }
export const groupedFrame = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
                    "Name" : "Frame groups",
                    "Item name" : "group",
                    "Item label template" : "Group [#groupIndex]",
                    "UIHint" : UIHint.COLLAPSE_ARRAY_ITEMS
                }
        definition.frameGroups is array;

        for (var group in definition.frameGroups)
        {
            annotation {
                        "Name" : "Group index",
                        "UIHint" : UIHint.ALWAYS_HIDDEN
                    }
            isInteger(group.groupIndex, { (unitless) : [1, 1, 100] } as IntegerBoundSpec);

            annotation {
                        "Library Definition" : "65dcc2a02c4ff1c239467ec9",
                        "Name" : "Sketch profile",
                        "Filter" : PartStudioItemType.SKETCH,
                        "MaxNumberOfPicks" : 1,
                        "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
                    }
            group.profileSketch is PartStudioData;

            annotation {
                        "Name" : "Selections",
                        "Description" : "Edges and faces that define sweep paths for this group",
                        "Filter" : ((EntityType.FACE && ConstructionObject.NO) || EntityType.EDGE || (EntityType.VERTEX && AllowEdgePoint.NO) || (EntityType.BODY && BodyType.WIRE && SketchObject.NO))
                    }
            group.selections is Query;

            annotation { "Name" : "Angle" }
            isAngle(group.angle, ANGLE_360_ZERO_DEFAULT_BOUNDS);

            annotation {
                        "Name" : "Mirror across Y axis",
                        "UIHint" : UIHint.OPPOSITE_DIRECTION
                    }
            group.mirrorProfile is boolean;

            annotation { "Name" : "Default corner type", "UIHint" : UIHint.SHOW_LABEL }
            group.defaultCornerType is FrameCornerType;

            if (group.defaultCornerType == FrameCornerType.BUTT || group.defaultCornerType == FrameCornerType.COPED_BUTT)
            {
                annotation {
                            "Name" : "Flip corner",
                            "UIHint" : UIHint.OPPOSITE_DIRECTION
                        }
                group.defaultButtFlip is boolean;
            }

            annotation { "Name" : "Merge tangent segments", "Default" : true }
            group.mergeTangentSegments is boolean;
        }

        annotation { "Name" : "Auto trim groups", "Default" : true }
        definition.autoTrim is boolean;
    }
    {
        verify(size(definition.frameGroups) >= 1, ErrorStringEnum.FRAME_SELECT_PATH, { "faultyParameters" : ["frameGroups"] });

        var groupBodies = []; // array of Query, one per group representing created bodies

        for (var groupIndex = 0; groupIndex < size(definition.frameGroups); groupIndex += 1)
        {
            const group = definition.frameGroups[groupIndex];
            const groupId = id + ("group" ~ groupIndex);
            const bodies = createOneGroup(context, groupId, group);
            groupBodies = append(groupBodies, bodies);
        }

        if (definition.autoTrim && size(groupBodies) >= 2)
        {
            doAutoTrim(context, id, groupBodies);
        }
    },
    {
            frameGroups : [],
            autoTrim : true
        });

/** @internal */
export function groupedFrameEditLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean) returns map
{
    // Auto-assign group index to new groups
    if (size(definition.frameGroups) > size(oldDefinition.frameGroups))
    {
        definition.frameGroups[size(definition.frameGroups) - 1].groupIndex = size(definition.frameGroups);
    }
    return definition;
}

function createOneGroup(context is Context, groupId is Id, group is map) returns Query
{
    verify(group.profileSketch.partQuery != undefined, ErrorStringEnum.FRAME_SELECT_PROFILE, {
                "faultyParameters" : ["profileSketch"] });

    var bodiesToDelete = new box([]);
    const profileData = getGroupProfile(context, groupId, group, bodiesToDelete);
    const sweepBodies = sweepGroupFrames(context, groupId, group, profileData, bodiesToDelete);

    // Clean up helper bodies
    if (bodiesToDelete[] != [])
    {
        opDeleteBodies(context, groupId + "cleanup", { "entities" : qUnion(bodiesToDelete[]) });
    }

    return sweepBodies;
}

function getGroupProfile(context is Context, groupId is Id, group is map, bodiesToDelete is box) returns map
{
    const profileId = groupId + "sketch";
    const instantiator = newInstantiator(profileId);

    var profileSketch = group.profileSketch;
    profileSketch.partQuery = profileSketch.partQuery->qSketchFilter(SketchObject.YES);

    try silent
    {
        addInstance(instantiator, profileSketch, {});
    }
    catch
    {
        throw regenError(ErrorStringEnum.FRAME_SELECT_PROFILE, ["profileSketch"]);
    }
    instantiate(context, instantiator);
    bodiesToDelete[] = append(bodiesToDelete[], qCreatedBy(profileId, EntityType.BODY));

    const facesCreated = evaluateQuery(context, qCreatedBy(profileId, EntityType.FACE));
    verify(facesCreated != [], ErrorStringEnum.FRAME_PROFILE_REGION);

    // Get outer faces from profile
    const allEdges = qAdjacent(qUnion(facesCreated), AdjacencyType.EDGE, EntityType.EDGE);
    const laminarEdges = allEdges->qEdgeTopologyFilter(EdgeTopology.ONE_SIDED);
    const outerFaces = qUnion([
                qAdjacent(laminarEdges, AdjacencyType.EDGE, EntityType.FACE),
                qAdjacent(laminarEdges, AdjacencyType.VERTEX, EntityType.FACE)
            ]);

    const surfaceId = groupId + "profileSurface";
    opExtractSurface(context, surfaceId, {
                "faces" : outerFaces,
                "redundancyType" : ExtractSurfaceRedundancyType.REMOVE_ALL_REDUNDANCY
            });
    const surfaces = evaluateQuery(context, qCreatedBy(surfaceId, EntityType.BODY));
    verify(size(surfaces) == 1, "Could not determine single frame profile.");
    const profileBody = surfaces[0];
    bodiesToDelete[] = append(bodiesToDelete[], profileBody);

    const profileFace = qOwnedByBody(profileBody, EntityType.FACE);
    verify(size(evaluateQuery(context, profileFace)) == 1, "Frame profile is not planar");

    const profilePlane = evPlane(context, { "face" : profileFace });
    const profileAttribute = getFrameProfileAttributeOrDefault(context, outerFaces, profileSketch.configuration);

    // Compute bounding box center for alignment
    const profileCS = coordSystem(profilePlane);
    const bb = evBox3d(context, { "topology" : profileBody, "cSys" : profileCS });
    const center2d = 0.5 * (bb.maxCorner + bb.minCorner);

    return {
            "profileBody" : profileBody,
            "profilePlane" : profilePlane,
            "profileAttribute" : profileAttribute,
            "center" : center2d
        };
}

function sweepGroupFrames(context is Context, groupId is Id, group is map, profileData is map, bodiesToDelete is box) returns Query
{
    verify(!isQueryEmpty(context, group.selections), ErrorStringEnum.FRAME_SELECT_PATH, { "faultyParameters" : ["selections"] });

    // Gather edges from selections (faces, edges, vertices, wire bodies)
    const edgesFromFacesQ = qEntityFilter(qAdjacent(qEntityFilter(group.selections, EntityType.FACE), AdjacencyType.EDGE), EntityType.EDGE);
    const allEdgesQ = qUnion([
                qEntityFilter(group.selections, EntityType.EDGE),
                edgesFromFacesQ,
                qOwnedByBody(qBodyType(qEntityFilter(group.selections, EntityType.BODY), BodyType.WIRE), EntityType.EDGE)
            ]);

    const paths = constructPaths(context, allEdgesQ, {});
    verify(size(paths) > 0, ErrorStringEnum.FRAME_BAD_PATH);

    var allSweepBodies = [];
    const sweepId = getUnstableIncrementingId(groupId + "path");

    for (var pathIndex = 0; pathIndex < size(paths); pathIndex += 1)
    {
        const path = paths[pathIndex];
        const pathBodies = sweepOnePath(context, groupId, sweepId, group, profileData, path, bodiesToDelete);
        allSweepBodies = concatenateArrays([allSweepBodies, pathBodies]);
    }

    return qUnion(allSweepBodies);
}

function sweepOnePath(context is Context, groupId is Id, createPathId is function, group is map, profileData is map, path is Path, bodiesToDelete is box) returns array
{
    const pathId = createPathId();
    const createSweepId = getUnstableIncrementingId(pathId);
    var sweepBodies = [];
    var previousFace = undefined;
    var previousEdgeEnd = undefined;

    for (var edgeIndex = 0; edgeIndex < size(path.edges); edgeIndex += 1)
    {
        const edge = path.edges[edgeIndex];
        const flipped = path.flipped[edgeIndex];
        const edgeId = createSweepId();

        if (edgeIndex == 0)
        {
            // Sweep starting edge using profile placement
            const edgeLine = evaluatePathEdge(context, edge, flipped, 0);
            const planeAtEdgeStart = getPlaneForEdge(context, edgeLine);
            const profilePlane = getProfilePlaneForGroup(profileData, group);
            const mirrorAndAngleTransform = getMirrorAndAngleTransformForGroup(group.mirrorProfile, edgeLine, planeAtEdgeStart, group.angle);
            const profileTransform = mirrorAndAngleTransform * transform(profilePlane, planeAtEdgeStart);

            const profileId = edgeId + "profile";
            opPattern(context, profileId, {
                        "entities" : profileData.profileBody,
                        "transforms" : [profileTransform],
                        "instanceNames" : ["1"]
                    });
            bodiesToDelete[] = append(bodiesToDelete[], qCreatedBy(profileId, EntityType.BODY));

            const sweepSubId = edgeId + "sweep";
            opSweep(context, sweepSubId, { "profiles" : qCreatedBy(profileId, EntityType.FACE), "path" : edge });

            const body = qCreatedBy(sweepSubId, EntityType.BODY);
            sweepBodies = append(sweepBodies, body);

            // Set frame attributes
            const startFace = qCapEntity(sweepSubId, CapType.START, EntityType.FACE);
            const endFace = qCapEntity(sweepSubId, CapType.END, EntityType.FACE);
            setFrameTopologyAttribute(context, startFace, frameTopologyAttributeForCapFace(true, true, false));
            setFrameTopologyAttribute(context, endFace, frameTopologyAttributeForCapFace(false, false, false));
            setFrameProfileAttribute(context, body, profileData.profileAttribute);

            previousFace = endFace;
            previousEdgeEnd = evaluatePathEdge(context, edge, flipped, 1);
        }
        else
        {
            // Sweep continuing edge by copying previous end face
            const edgeStart = evaluatePathEdge(context, edge, flipped, 0);

            opExtractSurface(context, edgeId + "extract", { "faces" : previousFace });
            const extractedBody = qCreatedBy(edgeId + "extract", EntityType.BODY);
            bodiesToDelete[] = append(bodiesToDelete[], extractedBody);

            const sweepProfileTransform = transform(
                line(previousEdgeEnd.origin, previousEdgeEnd.direction),
                line(edgeStart.origin, edgeStart.direction));

            opTransform(context, edgeId + "xform", {
                        "bodies" : extractedBody,
                        "transform" : sweepProfileTransform
                    });

            const faceToSweep = qOwnedByBody(extractedBody, EntityType.FACE);
            const sweepSubId = edgeId + "sweep";
            opSweep(context, sweepSubId, { "profiles" : faceToSweep, "path" : edge });

            const body = qCreatedBy(sweepSubId, EntityType.BODY);
            sweepBodies = append(sweepBodies, body);

            const startFace = qCapEntity(sweepSubId, CapType.START, EntityType.FACE);
            const endFace = qCapEntity(sweepSubId, CapType.END, EntityType.FACE);
            setFrameTopologyAttribute(context, startFace, frameTopologyAttributeForCapFace(true, false, false));
            setFrameTopologyAttribute(context, endFace, frameTopologyAttributeForCapFace(false, false, false));
            setFrameProfileAttribute(context, body, profileData.profileAttribute);

            // Create miter/butt corner between this segment and previous
            handleCorner(context, edgeId, group, previousFace, startFace, previousEdgeEnd, edgeStart, sweepBodies, edgeIndex, bodiesToDelete);

            previousFace = endFace;
            previousEdgeEnd = evaluatePathEdge(context, edge, flipped, 1);
        }
    }

    // Mark terminus faces
    if (size(sweepBodies) > 0 && !path.closed)
    {
        const firstBody = sweepBodies[0];
        const lastBody = last(sweepBodies);
        setTerminusAttributes(context, qFrameStartFace(firstBody), qFrameEndFace(lastBody));
    }

    // Create composites for tangent-connected segments if requested
    if (group.mergeTangentSegments && size(sweepBodies) >= 2)
    {
        createGroupComposites(context, groupId, sweepBodies, profileData);
    }

    return sweepBodies;
}

function handleCorner(context is Context, edgeId is Id, group is map, previousEndFace is Query,
    currentStartFace is Query, previousEdgeEnd is Line, currentEdgeStart is Line,
    sweepBodies is array, edgeIndex is number, bodiesToDelete is box)
{
    const angle = angleBetween(previousEdgeEnd.direction, currentEdgeStart.direction);
    if (tolerantEquals(angle, 0 * degree))
    {
        return; // Tangent, no corner needed
    }

    const cornerType = group.defaultCornerType;
    if (cornerType == FrameCornerType.NONE)
    {
        return;
    }

    if (cornerType == FrameCornerType.MITER)
    {
        // Create miter by splitting at bisecting plane
        const bisector = normalize(previousEdgeEnd.direction + currentEdgeStart.direction);
        const miterPlane = plane(currentEdgeStart.origin, bisector);

        try silent
        {
            const prevBody = sweepBodies[edgeIndex - 1];
            const currBody = sweepBodies[edgeIndex];

            opSplitPart(context, edgeId + "miterPrev", {
                        "targets" : prevBody,
                        "tool" : miterPlane
                    });
            // Keep the larger piece
            const prevPieces = qSplitBy(edgeId + "miterPrev", EntityType.BODY, true);
            const prevSmall = qSplitBy(edgeId + "miterPrev", EntityType.BODY, false);
            if (!isQueryEmpty(context, prevSmall))
            {
                bodiesToDelete[] = append(bodiesToDelete[], prevSmall);
            }

            opSplitPart(context, edgeId + "miterCurr", {
                        "targets" : currBody,
                        "tool" : miterPlane
                    });
            const currSmall = qSplitBy(edgeId + "miterCurr", EntityType.BODY, false);
            if (!isQueryEmpty(context, currSmall))
            {
                bodiesToDelete[] = append(bodiesToDelete[], currSmall);
            }
        }
    }
    else if (cornerType == FrameCornerType.BUTT)
    {
        // Butt joint: one beam extends fully, the other is cut flat
        const buttFlip = try(group.defaultButtFlip) == true;
        const cuttingNormal = buttFlip ? currentEdgeStart.direction : -previousEdgeEnd.direction;
        const cuttingPlane = plane(currentEdgeStart.origin, cuttingNormal);
        const targetBody = buttFlip ? sweepBodies[edgeIndex - 1] : sweepBodies[edgeIndex];

        try silent
        {
            opSplitPart(context, edgeId + "butt", {
                        "targets" : targetBody,
                        "tool" : cuttingPlane
                    });
            const smallPiece = qSplitBy(edgeId + "butt", EntityType.BODY, false);
            if (!isQueryEmpty(context, smallPiece))
            {
                bodiesToDelete[] = append(bodiesToDelete[], smallPiece);
            }
        }
    }
}

function createGroupComposites(context is Context, groupId is Id, sweepBodies is array, profileData is map)
{
    if (size(sweepBodies) < 2)
    {
        return;
    }
    const bodyQuery = qUnion(sweepBodies);
    const compositeId = groupId + "composite";
    try silent
    {
        opCreateCompositePart(context, compositeId, {
                    "bodies" : bodyQuery,
                    "closed" : true
                });
        const compositeBody = qCreatedBy(compositeId, EntityType.BODY)->qCompositePartTypeFilter(CompositePartType.CLOSED);
        setFrameProfileAttribute(context, compositeBody, profileData.profileAttribute);
    }
}

function doAutoTrim(context is Context, topLevelId is Id, groupBodies is array)
{
    // Groups are in priority order: group 0 is highest priority (never trimmed).
    // Each group n (n>0) is trimmed by all groups 0..(n-1).
    const trimId = getUnstableIncrementingId(topLevelId + "autoTrim");

    for (var i = 1; i < size(groupBodies); i += 1)
    {
        // Gather tools from all earlier groups
        var toolQueries = [];
        for (var j = 0; j < i; j += 1)
        {
            toolQueries = append(toolQueries, groupBodies[j]);
        }
        const tools = qUnion(toolQueries);
        const targets = groupBodies[i];

        if (isQueryEmpty(context, targets) || isQueryEmpty(context, tools))
        {
            continue;
        }

        // Track individual targets for attribute re-application
        const allTargets = evaluateQuery(context, targets);
        if (allTargets == [])
        {
            continue;
        }

        const currentTrimId = trimId();
        try silent
        {
            opBoolean(context, currentTrimId, {
                        "tools" : tools,
                        "targets" : targets,
                        "keepTools" : true,
                        "operationType" : BooleanOperationType.SUBTRACTION
                    });
        }
    }
}

// Utility: evaluate edge tangent at parameter
function evaluatePathEdge(context is Context, edge is Query, isFlipped is boolean, parameter is number) returns Line
{
    const param = isFlipped ? 1 - parameter : parameter;
    var edgeLine = evEdgeTangentLine(context, {
                "edge" : edge,
                "parameter" : param
            });
    edgeLine.direction = isFlipped ? -edgeLine.direction : edgeLine.direction;
    return edgeLine;
}

// Creates a plane with Z along the line direction, using heuristic for X
function getPlaneForEdge(context is Context, edgeLine is Line) returns Plane
{
    var xDir;
    if (abs(dot(edgeLine.direction, vector(0, 0, 1))) < 1 - TOLERANCE.zeroAngle)
    {
        xDir = cross(vector(0, 0, 1), edgeLine.direction);
    }
    else
    {
        xDir = cross(vector(0, 1, 0), edgeLine.direction);
    }
    xDir = normalize(xDir);
    return plane(edgeLine.origin, edgeLine.direction, xDir);
}

function getProfilePlaneForGroup(profileData is map, group is map) returns Plane
{
    const center = profileData.center;
    const profilePlane = profileData.profilePlane;
    const profilePlaneOrigin = planeToWorld(profilePlane, vector(center[0], center[1]));
    return plane(profilePlaneOrigin, profilePlane.normal, profilePlane.x);
}

function getMirrorAndAngleTransformForGroup(mirrorProfile is boolean, edgeLine is Line, planeAtEdgeStart is Plane, angle is ValueWithUnits) returns Transform
{
    if (mirrorProfile)
    {
        const mirrorPlane = plane(edgeLine.origin, planeAtEdgeStart.x);
        return rotationAround(edgeLine, -angle) * mirrorAcross(mirrorPlane);
    }
    else
    {
        return rotationAround(edgeLine, angle);
    }
}

function setTerminusAttributes(context is Context, startFace is Query, endFace is Query)
{
    if (!isQueryEmpty(context, startFace))
    {
        setFrameTopologyAttribute(context, startFace, frameTopologyAttributeForCapFace(true, true, false));
    }
    if (!isQueryEmpty(context, endFace))
    {
        setFrameTopologyAttribute(context, endFace, frameTopologyAttributeForCapFace(false, true, false));
    }
}
