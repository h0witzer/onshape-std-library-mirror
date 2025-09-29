FeatureScript 2695;
import(path : "onshape/std/common.fs", version : "2695.0");
import(path : "onshape/std/units.fs", version : "2695.0");
export import(path : "f24718439470cd9296f34fd3", version : "9f125589ecea58dfe983c806");
import(path : "df88be8643ff7d84d2720051/4f80fc42ce192ca8e614efee/41dd957a2ecc40be846e951e", version : "cdb27e067dc389f07ef5bab0");
import(path : "b9cc96e40d33ef8f43eb34e4/8071f953eda68cf1dc80fb55/b684b3564b223c2304d75940", version : "82faa11894d7466456a5de48");
import(path : "df88be8643ff7d84d2720051/4f80fc42ce192ca8e614efee/aa13ecb4243c96aa13d93020", version : "8deabfd7afdca4ab97f09644");
ICON::import(path : "178842cfbd493fd768ed6b54", version : "f11c0bb84c7736bc445fb659");

annotation {
        "Feature Type Name" : "Relative measure",
        "Feature Type Description" : "Reports displacement with respect to a measurement coordinate system. If no coordinate system is supplied the world coordinate system is used.",
        "Icon" : ICON::BLOB_DATA
    }
export const relativeMeasurementFromUi = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
                    "Name" : "Start location",
                    "Description" : "A mate connector or point defining the start location",
                    "Filter" : BodyType.MATE_CONNECTOR || EntityType.VERTEX,
                    "MaxNumberOfPicks" : 1
                }
        definition.src is Query;

        annotation {
                    "Name" : "End location",
                    "Description" : "A mate connector or point defining the destination coordinate system",
                    "Filter" : BodyType.MATE_CONNECTOR || EntityType.VERTEX,
                    "MaxNumberOfPicks" : 1
                }
        definition.dest is Query;

        annotation {
                    "Name" : "(Optional) Measurement coordinate system",
                    "Description" : "A mate connector defining the coordinate system in which to express the translation",
                    "Filter" : BodyType.MATE_CONNECTOR,
                    "MaxNumberOfPicks" : 1
                }
        definition.reportCs is Query;

        annotation { "Group Name" : "Display", "Collapsed By Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        {
            annotation { "Name" : "Show arrows", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.showArrows is boolean;

            annotation { "Name" : "Units", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.displayUnits is LinearDisplayUnits;

            annotation { "Name" : "Precision", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isInteger(definition.precision, DISPLAY_PRECISION_BOUNDS);
        }
    }
    {
        verify(!isQueryEmpty(context, definition.src), "Select a start point.", { "faultyParameters" : ["src"] });
        verify(!isQueryEmpty(context, definition.dest), "Select an end point.", { "faultyParameters" : ["dest"] });
        doOneRelativeMeasure(context, id, definition);
    });

function doOneRelativeMeasure(context is Context, id is Id, definition is map)
{
    var reportCs = undefined;
    if (definition.reportCs == undefined || isQueryEmpty(context, definition.reportCs))
    {
        //because we use the info area, this meeting is immediately swamped so we remove it.
        //reportFeatureInfo(context, id, "Reporting translation in world coordinate system.");
        reportCs = WORLD_COORD_SYSTEM;
    }
    else
    {
        reportCs = evMateConnector(context, {
                    "mateConnector" : definition.reportCs
                });
    }

    const from = extractOrigin(context, definition.src);
    const to = extractOrigin(context, definition.dest);
    const translationInReportCs = getRelativeTranslation(context, from, to, reportCs, definition.showArrows);
    const displayStr = getDisplayString(context, translationInReportCs, definition.precision, definition.displayUnits);
    reportFeatureInfo(context, id, displayStr);
}

function extractOrigin(context is Context, from is Query) returns Vector
{
    //first evaluate as mc
    //if it works, return origin
    var origin = undefined;
    try silent
    {
        const fromMc = evMateConnector(context, {
                    "mateConnector" : from
                });
        origin = fromMc.origin;
        return origin;
    }

    try silent
    {
        const vertex = evVertexPoint(context, {
                    "vertex" : from
                });
        return vertex;
    }

    verify(false, "Unable to extract location from query.", { "entities" : from });
}

export function getRelativeTranslation(context is Context, fromWCS is Vector, toWCS is Vector, reportCs is CoordSystem, showArrows is boolean) returns Vector
{
    //the vector that translates from one point to another is just a vector, and it can be expressed in any CS.
    //When we get the cs origins, we are getting them in world cys. that's fine. to get the translation in the src coordinate system we will just compute the dot products of that vector against the unit vectors.
    //vector from A to B is found by a simple equation B = A + (B-A)
    //so we compute the translation vector as B-A.
    var translationWCS = toWCS - fromWCS;

    const xRCS = dot(translationWCS, reportCs.xAxis);
    const yRCS = dot(translationWCS, yAxis(reportCs));
    const zRCS = dot(translationWCS, reportCs.zAxis);
    //takes the abs of all the values, then filters any zeros.
    const translationInReportCs = vector(xRCS, yRCS, zRCS);

    if (showArrows)
    {
        doArrowDisplay(context, fromWCS, reportCs, xRCS, yRCS, zRCS);
    }
    return translationInReportCs;
}

function doArrowDisplay(context is Context, fromWCS is Vector, reportCs is CoordSystem, xRCS is ValueWithUnits, yRCS is ValueWithUnits, zRCS is ValueWithUnits)
{
   
    const minLength = [xRCS, yRCS, zRCS]->mapArray(function(a)
                    {
                        return abs(a);
                    })->filter(function(a)
                {
                    return a->tolerantGreaterThan(0 * meter);
                })->min();
    if (minLength == undefined || tolerantEqualsZero(minLength)) {return;}    
    const arrowHeadLength = 0.1 * minLength;
    const xArrowWCS = arrow(line(fromWCS, reportCs.xAxis), xRCS, arrowHeadLength);
    const xEnd = fromWCS + reportCs.xAxis * xRCS;
    const yArrowWCS = arrow(line(xEnd, yAxis(reportCs)), yRCS, arrowHeadLength);
    const yEnd = xEnd + yRCS * yAxis(reportCs);
    const zArrowWCS = arrow(line(yEnd, reportCs.zAxis), zRCS, arrowHeadLength);
    const colorIter = toIterator([DebugColor.RED, DebugColor.GREEN, DebugColor.BLUE]);
    for (var arrow in [xArrowWCS, yArrowWCS, zArrowWCS])
    {
        const color = colorIter.next();
        if (!tolerantEqualsZero(norm(arrow.end - arrow.start)) && !tolerantEqualsZero(arrow.radius))
        {
            addDebugArrow(context, arrow.start, arrow.end, arrow.radius, color);
        }
    }
}

function getDisplayString(context is Context, dist is Vector, precision is number, displayUnits is LinearDisplayUnits)
{
    const units = LinearDisplayEnumToUnit[displayUnits];
    const noDescription = "";
    const xInUnits = scalarToString(dist[0], units, precision, noDescription);
    const yInUnits = scalarToString(dist[1], units, precision, noDescription);
    const zInUnits = scalarToString(dist[2], units, precision, noDescription);
    const nickname = convertUnitToNickname(units);
    const result = [
            "x: ",
            xInUnits,
            ", y: ",
            yInUnits,
            ", z: ",
            zInUnits,
            " (" ~ nickname ~ ")",
        ];
    const resultStr = join(result, "");
    return resultStr;
}
