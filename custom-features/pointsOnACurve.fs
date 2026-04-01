FeatureScript 2796;
import(path : "onshape/std/common.fs", version : "2796.0");
IconNamespace::import(path : "c1c4a52b27216dd4bb27fe05", version : "4691b8341c86ab39a5c5b072");

// *************************************************************************************
// Created by SmartBench Software
// Visit www.smartbenchsoftware.com for Custom Features, Integrations, custom software and more.
// SmartBench Software is the leading providing of Onshape consulting services
// *************************************************************************************


annotation { "Feature Type Name" : "Points on a Curve", "Icon" : IconNamespace::BLOB_DATA, "Feature Type Description" : "Feature that allows the user to add points or mate connectors to a curve." }
export const pointsOnCurve = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Path", "Filter" : EntityType.EDGE }
        definition.edge is Query;

        annotation { "Name" : "Space length" }
        isLength(definition.spaceLength, LENGTH_BOUNDS);

        annotation { "Name" : "Start Offset" }
        isLength(definition.startOffset, ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation { "Name" : "Number of Vertices" }
        isInteger(definition.vertices, POSITIVE_COUNT_BOUNDS);

        annotation { "Name" : "Center Points" }
        definition.centerPoints is boolean;

        if (!definition.centerPoints)
        {
            annotation { "Name" : "Switch Point", "UIHint" : "OPPOSITE_DIRECTION" }
            definition.switchPoint is boolean;
        }

        annotation { "Name" : "Mate Connector" }
        definition.isMCTan is boolean;


    }
    {



        const edgeLength = evLength(context, {
                    "entities" : definition.edge
                });

        if (edgeLength < definition.spaceLength)
        {

            throw regenError("Spacing greater than path length", {
                        "faultyParameters" : ["spaceLength"]
                    });
        }

        var spacing = definition.spaceLength / edgeLength;
        var startOffset = definition.startOffset / edgeLength;
        var pointCount = min(definition.vertices, floor((edgeLength - definition.startOffset) / definition.spaceLength, 1));


        var parameterArray = range(startOffset, spacing * (pointCount - 1) + startOffset, pointCount);

        if (definition.centerPoints)
        {
            var halfSpacingFull = (spacing * (pointCount) / 2);
            parameterArray = range(0.5 + startOffset - halfSpacingFull, spacing * (pointCount - 1) + 0.5 + startOffset - halfSpacingFull, pointCount);

            for (var x in parameterArray)
            {
                if (x == ceil(size(parameterArray) / 2))
                {
                    parameterArray = append(parameterArray, 0.5);
                }
            }

        }



        if (definition.switchPoint)
        {

            parameterArray = mapArray(parameterArray, function(a)
                {
                    return 1.0 - a;
                });

        }

        const path = constructPath(context, definition.edge);
        const tanLines = evPathTangentLines(context, path, parameterArray);


        if (definition.isMCTan)
        {

            var createdPoints = qCreatedBy(id + "point", EntityType.VERTEX);


            for (var i = 0; i < size(tanLines.tangentLines); i += 1)
            {

                var currentTangentLineData = tanLines.tangentLines[i];
                var currentPointEntity = createdPoints[i];

                opMateConnector(context, id + "mc" + i, {

                            "coordSystem" : coordSystem(
                            currentTangentLineData.origin,
                            perpendicularVector(currentTangentLineData.direction),
                            currentTangentLineData.direction
                            ),
                            "owner" : currentPointEntity
                        });
            }
        }
        else
        {

            for (var i = 0; i < size(tanLines.tangentLines); i += 1)
            {

                var point = opPoint(context, id + "point" + i, {
                        "point" : tanLines.tangentLines[i].origin
                    });

            }
        }

    });
