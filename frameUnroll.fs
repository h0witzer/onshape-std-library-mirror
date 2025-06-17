FeatureScript 2679;

import(path : "onshape/std/common.fs", version : "2679.0");
import(path : "onshape/std/frameUtils.fs", version : "2679.0");
import(path : "onshape/std/frame.fs", version : "2679.0");
import(path : "onshape/std/frameAttributes.fs", version : "2679.0");
import(path : "onshape/std/cutlistMath.fs", version : "2679.0");
import(path : "onshape/std/geomOperations.fs", version : "2679.0");
import(path : "onshape/std/evaluate.fs", version : "2679.0");
import(path : "onshape/std/vector.fs", version : "2679.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2679.0");
import(path : "onshape/std/query.fs", version : "2679.0");
import(path : "onshape/std/deleteBodies.fs", version : "2679.0");
import(path : "onshape/std/transform.fs", version : "2679.0");

// Custom feature to use cut list definitions to unroll a frame into a straightened state for future frame layout script or possibly as a kirigami unfolding function
// Written by Derek Van Allen

annotation { "Feature Type Name" : "Unroll frame" }
export const frameUnroll = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Frames", "Filter" : EntityType.BODY && BodyType.SOLID }
        definition.frames is Query;

    }
    {
        var index = 0;
        for (var frame in evaluateQuery(context, definition.frames))
        {
            var cleanup = new box([]);
            const lenData = getCutlistLengthAndAngles(context, id + ("calc" ~ index), id + ("curve" ~ index), frame, cleanup);
            if (lenData.length == undefined)
            {
                reportFeatureWarning(context, id, "Unable to determine frame length");
                index += 1;
                continue;
            }

            const startFace = qFrameStartFace(frame);
            const plane = capFacePlane(context, startFace);
            if (plane == undefined)
            {
                reportFeatureWarning(context, id, "Start face not planar");
                index += 1;
                continue;
            }

            const extractId = id + ("extract" ~ index);
            opExtractSurface(context, extractId, { "faces" : startFace });
            const profileFace = qOwnedByBody(qCreatedBy(extractId, EntityType.BODY), EntityType.FACE);

            const extrudeId = id + ("unroll" ~ index);
            opExtrude(context, extrudeId, {
                        "entities" : profileFace,
                        "direction" : getFrameDirection(context, frame),
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : lenData.length
                    });

            copyEndFace(context, qFrameEndFace(frame),
                qCapEntity(extrudeId, CapType.END, EntityType.FACE),
                extrudeId);

            const newFrame = qCreatedBy(extrudeId, EntityType.BODY);
            const profileAttr = try silent(getFrameProfileAttribute(context, frame));
            const profileData = { "profileAttribute" : profileAttr };
            const newFrameData = {
                    "sweptEdges" : qNonCapEntity(extrudeId, EntityType.EDGE),
                    "sweptFaces" : qNonCapEntity(extrudeId, EntityType.FACE),
                    "startFace" : qCapEntity(extrudeId, CapType.START, EntityType.FACE),
                    "endFace" : qCapEntity(extrudeId, CapType.END, EntityType.FACE)
                };
            setFrameAttributes(context, newFrame, profileData, newFrameData);

            // opDeleteBodies(context, extrudeId + "cleanup", { "entities" : qCreatedBy(extractId, EntityType.BODY) });
            // opDeleteBodies(context, extrudeId + "temp", { "entities" : qUnion(cleanup[]) });
            // opDeleteBodies(context, extrudeId + "orig", { "entities" : frame });
            index += 1;
        }
    },
    {});



function capFacePlane(context is Context, faces is Query) returns Plane
{
    const faceArray = evaluateQuery(context, faces);
    if (size(faceArray) == 0)
        return undefined;
    var basePlane = try silent(evPlane(context, { "face" : faceArray[0] }));
    if (basePlane == undefined)
        return undefined;
    for (var i = 1; i < size(faceArray); i += 1)
    {
        const nextPlane = try silent(evPlane(context, { "face" : faceArray[i] }));
        if (nextPlane == undefined || !coplanarPlanes(basePlane, nextPlane))
            return undefined;
    }
    return basePlane;
}

// Determine the axis direction of a frame using edge or face data.
function getStraightCylinderDirectionLocal(context is Context, sweptFaceQuery is Query)
{
    const numSweptFaces = size(evaluateQuery(context, sweptFaceQuery));
    const cylinderFaces = evaluateQuery(context, qGeometry(sweptFaceQuery, GeometryType.CYLINDER));
    if (size(cylinderFaces) == 0 || size(cylinderFaces) != numSweptFaces)
    {
        return undefined;
    }
    const firstCylinder = evSurfaceDefinition(context, { "face" : cylinderFaces[0] });
    const dir = firstCylinder.coordSystem.zAxis;
    for (var i = 1; i < size(cylinderFaces) - 1; i += 1)
    {
        const other = evSurfaceDefinition(context, { "face" : cylinderFaces[i] });
        if (!parallelVectors(dir, other.coordSystem.zAxis))
        {
            return undefined;
        }
    }
    return dir;
}

// Return a robust sweep direction based on frame attributes.
function getFrameDirection(context is Context, frame is Query) returns Vector
{
    const startCenter = evApproximateCentroid(context, { "entities" : qFrameStartFace(frame) });
    const endCenter = evApproximateCentroid(context, { "entities" : qFrameEndFace(frame) });
    var nominal = normalize(endCenter - startCenter);

    const lineEdges = evaluateQuery(context, qGeometry(qFrameSweptEdge(frame), GeometryType.LINE));
    if (lineEdges != [])
    {
        const lineData = evEdgeTangentLine(context, { "edge" : lineEdges[0], "parameter" : 0 });
        return (dot(lineData.direction, nominal) < 0) ? -lineData.direction : lineData.direction;
    }

    const cylDir = getStraightCylinderDirectionLocal(context, qFrameSweptFace(frame));
    if (cylDir != undefined)
    {
        return (dot(cylDir, nominal) < 0) ? -cylDir : cylDir;
    }

    return nominal;
}

// Copy the geometry of the original end face to the new frame end.
function copyEndFace(context is Context, sourceFace is Query, targetFace is Query, baseId is Id)
{
    if (isQueryEmpty(context, sourceFace) || isQueryEmpty(context, targetFace))
        return;

    const srcPlane = capFacePlane(context, sourceFace);
    const dstPlane = capFacePlane(context, targetFace);
    if (srcPlane == undefined || dstPlane == undefined)
        return;

    const extract = baseId + "endExtract";
    opExtractSurface(context, extract, { "faces" : sourceFace });
    const extracted = qCreatedBy(extract, EntityType.BODY);

    const moveId = baseId + "endMove";
    opTransform(context, moveId, {
                "bodies" : extracted,
                "transform" : transform(srcPlane, dstPlane)
            });
    const movedFace = qOwnedByBody(qCreatedBy(moveId, EntityType.BODY), EntityType.FACE);

    // opReplaceFace(context, baseId + "endReplace", {
    //             "replaceFaces" : targetFace,
    //             "templateFace" : movedFace,
    //             "oppositeSense" : dot(srcPlane.normal, dstPlane.normal) < -TOLERANCE.zeroLength
    //         });

    opDeleteBodies(context, baseId + "endCleanup", { "entities" : qUnion([qCreatedBy(extract, EntityType.BODY), qCreatedBy(moveId, EntityType.BODY)]) });
}

