FeatureScript 2752;
// This module is part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab for the license text.
// Copyright (c) 2013-Present PTC Inc.

import(path : "onshape/std/common.fs", version : "2752.0");
import(path : "onshape/std/query.fs", version : "2752.0");
import(path : "onshape/std/evaluate.fs", version : "2752.0");
import(path : "onshape/std/vector.fs", version : "2752.0");
import(path : "onshape/std/primitives.fs", version : "2752.0");
import(path : "onshape/std/matrix.fs", version : "2752.0");
import(path : "onshape/std/topologyUtils.fs", version : "2752.0");

/**
 * Feature that replaces a collection of faces with a cylindrical face
 * approximating the original geometry.
 */
annotation { "Feature Type Name" : "Replace with cylinder" }
export const replaceWithCylinder = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Faces to replace", "Filter" : (EntityType.FACE) && ConstructionObject.NO && SketchObject.NO && ModifiableEntityOnly.YES && AllowMeshGeometry.YES }
        definition.facesToReplace is Query;
    }
    {
        // Group the selected faces into connected components
        const faceGroups = connectedComponents(context, definition.facesToReplace, AdjacencyType.EDGE);

        // Run the replacement logic for each connected group
        var groupIndex = 0;
        for (var faceGroup in faceGroups)
        {
            replaceFaceGroupWithCylinder(context, id + groupIndex, faceGroup);
            groupIndex += 1;
        }
    });

function replaceFaceGroupWithCylinder(context is Context, groupId is Id, faceGroup is array)
{
    if (size(faceGroup) < 2)
    {
        throw regenError(ErrorStringEnum.INVALID_INPUT, ["facesToReplace"]);
    }

    // Collect sample points and normals from the face group
    var faceCentroids = [];
    var faceNormals = [];
    for (var face in faceGroup)
    {
        const centroid = evApproximateCentroid(context, { "entities" : face });
        const tangentPlane = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) });
        faceCentroids = append(faceCentroids, centroid);
        faceNormals = append(faceNormals, tangentPlane.normal);
    }

    // Determine cylinder axis direction using singular value decomposition of normals
    var normalRows = [];
    for (var normalVector in faceNormals)
    {
        normalVector = normalize(normalVector);
        normalRows = append(normalRows, [normalVector[0], normalVector[1], normalVector[2]]);
    }
    const svdResult = svd(matrix(normalRows));
    const numberOfFaces = size(faceNormals);
    const singularValues = [svdResult.s[0][0], numberOfFaces > 1 ? svdResult.s[1][1] : 0, numberOfFaces > 2 ? svdResult.s[2][2] : 0];
    var smallestValueIndex = 0;
    for (var valueIndex = 1; valueIndex < 3; valueIndex += 1)
    {
        if (singularValues[valueIndex] < singularValues[smallestValueIndex])
        {
            smallestValueIndex = valueIndex;
        }
    }
    var axisDirection = normalize(vector(svdResult.v[0][smallestValueIndex], svdResult.v[1][smallestValueIndex], svdResult.v[2][smallestValueIndex]));

    // Compute a point on the cylinder axis by least squares intersection of normal lines
    var leastSquaresMatrix = zeroMatrix(3, 3);
    var leastSquaresVector = vector(0, 0, 0) * meter;
    for (var faceIndex = 0; faceIndex < numberOfFaces; faceIndex += 1)
    {
        const faceNormal = normalize(faceNormals[faceIndex]);
        const faceCentroid = faceCentroids[faceIndex];
        const projectionMatrix = matrix([
            [1 - faceNormal[0] * faceNormal[0], -faceNormal[0] * faceNormal[1], -faceNormal[0] * faceNormal[2]],
            [-faceNormal[1] * faceNormal[0], 1 - faceNormal[1] * faceNormal[1], -faceNormal[1] * faceNormal[2]],
            [-faceNormal[2] * faceNormal[0], -faceNormal[2] * faceNormal[1], 1 - faceNormal[2] * faceNormal[2]]
        ]);
        leastSquaresMatrix += projectionMatrix;
        leastSquaresVector += projectionMatrix * faceCentroid;
    }
    const axisPoint = inverse(leastSquaresMatrix) * leastSquaresVector;

    // Calculate cylinder radius using face centroids
    var totalRadius = 0 * meter;
    for (var centroid in faceCentroids)
    {
        const relativePosition = centroid - axisPoint;
        const axialProjection = dot(axisDirection, relativePosition);
        const radialVector = relativePosition - axisDirection * axialProjection;
        totalRadius += norm(radialVector);
    }
    const radius = totalRadius / numberOfFaces;

    // Determine extents along the axis from the bounding box of the group
    const groupQuery = qUnion(faceGroup);
    const facesBoundingBox = evBox3d(context, { "topology" : groupQuery });
    const cornerX = [facesBoundingBox.minCorner[0], facesBoundingBox.maxCorner[0]];
    const cornerY = [facesBoundingBox.minCorner[1], facesBoundingBox.maxCorner[1]];
    const cornerZ = [facesBoundingBox.minCorner[2], facesBoundingBox.maxCorner[2]];
    var minimumProjection = undefined;
    var maximumProjection = undefined;
    for (var x in cornerX)
        for (var y in cornerY)
            for (var z in cornerZ)
            {
                const corner = vector(x, y, z);
                const projection = dot(axisDirection, corner - axisPoint);
                if (minimumProjection == undefined || projection < minimumProjection)
                {
                    minimumProjection = projection;
                }
                if (maximumProjection == undefined || projection > maximumProjection)
                {
                    maximumProjection = projection;
                }
            }
    if (abs(maximumProjection - minimumProjection) <= TOLERANCE.zeroLength * meter)
    {
        throw regenError(ErrorStringEnum.INVALID_INPUT, ["facesToReplace"]);
    }

    const bottomCenter = axisPoint + axisDirection * minimumProjection;
    const topCenter = axisPoint + axisDirection * maximumProjection;

    // Create a temporary cylinder that best fits the face group
    const temporaryCylinderId = groupId + "temporaryCylinder";
    fCylinder(context, temporaryCylinderId,
        {   "bottomCenter" : bottomCenter,
            "topCenter" : topCenter,
            "radius" : radius });
    const templateFace = qGeometry(qCreatedBy(temporaryCylinderId, EntityType.FACE), GeometryType.CYLINDER);

    // // Replace the faces in this group with the cylindrical face
    // opReplaceFace(context, groupId + "replace", { "replaceFaces" : groupQuery, "templateFace" : templateFace });

    // // Remove the temporary geometry
    // opDeleteBodies(context, groupId + "cleanup", { "entities" : qCreatedBy(temporaryCylinderId, EntityType.BODY) });
}
