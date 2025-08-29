FeatureScript 2345;
import(path : "onshape/std/common.fs", version : "2345.0");
// import(path : "25ebea773d366be6664f4815/12c0a28b1298c125a73d06ff/080d5a9efae282f355ed4430", version : "cb00370169b2abbb81b5463e");
import(path : "39b2d90008a29f578fda528a/dc4904bdc8134686b66dd805/537c9b09eed10ef97b044633", version : "29432f573a1ca67b88e2480d");



descriptionImage::import(path : "c36ce5b95fec2e5776a3146a", version : "2575ae9c63a2e9d3886765b6");
featureIcon::import(path : "c6b366ffc3c19b81273b6b1e", version : "75171eec1627686037d44c08");


annotation { "Feature Type Name" : "Surface Eval",
        "Feature Type Description" : "Evaluate a surface/face.<br>" ~
        "This feature display the U-V control point grids, knots, and other details.<br>" ~
        "If the selected entity is not a bspline, then a bspline approximation is used.",
        "Description Image" : descriptionImage::BLOB_DATA,
        "Icon" : featureIcon::BLOB_DATA }
export const surfEval = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face to evaulate", "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO, "MaxNumberOfPicks" : 1 }
        definition.myFace is Query;

        annotation { "Name" : "Show control point grids" }
        definition.isCP is boolean;

        annotation { "Name" : "Show knots" }
        definition.isKnots is boolean;

        annotation { "Name" : "Show report", "Description" : "Display additional diagnostic info in the FeatureScript notices panel" }
        definition.isReport is boolean;
    }

    {
        verifyNonemptyQuery(context, definition, "myFace", "Select face to evaluate");

        var mySurf = evSurfaceDefinition(context, {
                "face" : definition.myFace
            });

        // If surface type is not SPLINE, then use approximation instead
        if (mySurf.surfaceType != SurfaceType.SPLINE)
        {
            mySurf = evApproximateBSplineSurface(context, {
                            "face" : definition.myFace
                        }).bSplineSurface;
        }

        const uDegree = mySurf.uDegree;
        const vDegree = mySurf.vDegree;
        const uCP = size(mySurf.controlPoints);
        const vCP = size(mySurf.controlPoints[0]);
        const uKnots = size(mySurf.uKnots);
        const vKnots = size(mySurf.vKnots);  
        const uInternalKnots = size(deduplicate(mySurf.uKnots)) - 2;
        const vInternalKnots = size(deduplicate(mySurf.vKnots)) - 2;            
        const uSpans = uInternalKnots + 1;
        const vSpans = vInternalKnots + 1;

        reportFeatureInfo(context, id, " Degree: u = " ~ uDegree ~ " v = " ~ vDegree ~ ", Spans: u = " ~ uSpans ~ " v = " ~ vSpans ~ ", CP's: u = " ~ uCP ~ " v = " ~ vCP);

        if (definition.isReport)
        {
            debug(context, "CP in u = " ~ uCP);
            debug(context, "CP in v = " ~ vCP);
            debug(context, "Rational = " ~ mySurf.isRational);
            debug(context, "uPeriodic = " ~ mySurf.isUPeriodic);
            debug(context, "vPeriodic = " ~ mySurf.isVPeriodic);
            debug(context, "uDegree = " ~ uDegree);
            debug(context, "vDegree = " ~ vDegree);
            debug(context, "uKnots = " ~ mySurf.uKnots);
            debug(context, "vKnots = " ~ mySurf.vKnots);
            debug(context, "uKnots size = " ~ uKnots);
            debug(context, "vKnots size = " ~ vKnots);
            debug(context, "uSpans = " ~ uSpans);
            debug(context, "vSpans = " ~ vSpans);
            debug(context, (uInternalKnots == 0 && vInternalKnots == 0) ? "Bezier patch" : "Multi-span");
        }

        if (definition.isCP)
        {
            displayUControlPolygon(context, mySurf.controlPoints);
            displayVControlPolygon(context, mySurf.controlPoints);
        }

        if (definition.isKnots)
        {
            displayKnots(context, id, mySurf, definition.myFace);
        }

    });


// Draws the *** U *** control polygon for the bSpline (and the CP grid points)
function displayUControlPolygon(context is Context, points is array)
{
    for (var j = 0; j < size(points); j += 1)
    {
        for (var k = 0; k < size(points[0]) - 1; k += 1)
        {
            addDebugPoint(context, points[j][k + 1], DebugColor.MAGENTA);
            addDebugLine(context, points[j][k], points[j][k + 1], DebugColor.RED);
        }

        addDebugPoint(context, points[j][0], DebugColor.MAGENTA);
    }
}

// Draws the *** V *** control polygon for the bSpline
function displayVControlPolygon(context is Context, points is array)
{
    for (var j = 0; j < size(points) - 1; j += 1)
    {
        for (var k = 0; k < size(points[0]); k += 1)
        {
            addDebugLine(context, points[j][k], points[j + 1][k], DebugColor.CYAN);
        }
    }
}


// Draws surface knots (these are isoparametric curves at knot locations)
function displayKnots(context is Context, id is Id, surf is map, face is Query)
{
    var uNames = [];
    var uCurveDef = [];
    var vNames = [];
    var vCurveDef = [];
    var cleaner = getCleaner(context, id + "cleanup");

    // Rescale the knots
    if (surf.vKnots == [])
    {
        return;
    }

    const startV = surf.vKnots[0];
    const domainLengthV = surf.vKnots[size(surf.vKnots) - 1] - startV;
    const paramScaleV = 1 / domainLengthV;

    var vKnotsScaled = [];

    for (var knotVIndex = 0; knotVIndex < size(surf.vKnots); knotVIndex += 1)
    {
        vKnotsScaled = append(vKnotsScaled, (surf.vKnots[knotVIndex] - startV) * paramScaleV);
    }

    if (surf.uKnots == [])
    {
        return;
    }

    const startU = surf.uKnots[0];
    const domainLengthU = surf.uKnots[size(surf.uKnots) - 1] - startU;
    const paramScaleU = 1 / domainLengthU;
    var uKnotsScaled = [];

    for (var knotUIndex = 0; knotUIndex < size(surf.uKnots); knotUIndex += 1)
    {
        uKnotsScaled = append(uKnotsScaled, (surf.uKnots[knotUIndex] - startU) * paramScaleU);
    }

    // Make a new bSpline surf in order to display the knots.
    opCreateBSplineSurface(context, id + "NEW", {
                "bSplineSurface" : surf,
            });

    const newSurf = qCreatedBy(id + "NEW", EntityType.FACE);
    cleaner.add(qOwnerBody(newSurf));

    // Prepare the name arrays for the isocurves
    for (var i = 0; i < size(surf.uKnots); i += 1)
    {
        var uName = "uCurve" ~ toString(i);
        uNames = append(uNames, uName);
    }

    for (var i = 0; i < size(surf.vKnots); i += 1)
    {
        var vName = "vCurve" ~ toString(i);
        vNames = append(vNames, vName);
    }

    // NOTE: Periodic surfaces are not currently supported
    try
    {
        if (surf.isUPeriodic == false)
        {
            const uDef = curveOnFaceDefinition(newSurf, FaceCurveCreationType.DIR1_ISO, uNames, uKnotsScaled);
            uCurveDef = append(uCurveDef, uDef);

            opCreateCurvesOnFace(context, id + "curvesOnFace_U", {
                        "curveDefinition" : uCurveDef
                    });

            cleaner.add(qCreatedBy(id + "curvesOnFace_U"));

            addDebugEntities(context, qCreatedBy(id + "curvesOnFace_U", EntityType.BODY), DebugColor.MAGENTA);
        }

        if (surf.isVPeriodic == false)
        {
            const vDef = curveOnFaceDefinition(newSurf, FaceCurveCreationType.DIR2_ISO, vNames, vKnotsScaled);
            vCurveDef = append(vCurveDef, vDef);

            opCreateCurvesOnFace(context, id + "curvesOnFace_V", {
                        "curveDefinition" : vCurveDef
                    });

            cleaner.add(qCreatedBy(id + "curvesOnFace_V"));

            addDebugEntities(context, qCreatedBy(id + "curvesOnFace_V", EntityType.BODY), DebugColor.BLUE);
        }

        //Clean up
        cleaner.delete();
    }
    catch
    {
        reportFeatureInfo(context, id, "Knot display is currently unavailable for periodic surfaces.");
    }
}
