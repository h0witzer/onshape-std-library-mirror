
FeatureScript 2105;
import(path : "onshape/std/common.fs", version : "2105.0");
icon::import(path : "3a7b0d0d8c5e54099694be97", version : "6705c5d8481e27ee9718a0dc");
import(path : "9f4c9835d8018ff7dbdb5683/3584e597d32560ee4efa9f0f/c60f4a6e12f07acee647a2a1", version : "1cf490b478823266869ac18b");



export enum faceCurveType
{
    annotation { "Name" : "Grid" }
    equal,
    annotation { "Name" : "Individual" }
    parameters,
}


annotation { "Feature Type Name" : "Face curves",
        "Icon" : icon::BLOB_DATA,
        "Editing Logic Function" : "faceCurveEL",
        "Manipulator Change Function" : "faceCurveManipulatorChange" }
export const faceCurves = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "creation type", "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.creationType is faceCurveType;

        annotation { "Name" : "Face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.face is Query;

        if (definition.creationType == faceCurveType.equal)
        {
            annotation { "Name" : "U curves", "Default" : true, "UIHint" : UIHint.DISPLAY_SHORT }
            definition.uCurves is boolean;
            if (definition.uCurves)
            {
                annotation { "Name" : "U count", "UIHint" : UIHint.DISPLAY_SHORT }
                isInteger(definition.uCount, { (unitless) : [2, 5, 100000] } as IntegerBoundSpec);
            }

            annotation { "Name" : "V curves", "Default" : true, "UIHint" : UIHint.DISPLAY_SHORT }
            definition.vCurves is boolean;
            if (definition.vCurves)
            {
                annotation { "Name" : "V count", "UIHint" : UIHint.DISPLAY_SHORT }
                isInteger(definition.vCount, { (unitless) : [2, 5, 100000] } as IntegerBoundSpec);
            }
        }
        if (definition.creationType == faceCurveType.parameters)
        {
            annotation { "Name" : "Show arrows", "Default" : true, "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            definition.showArrows is boolean;

            annotation { "Name" : "Curves", "Item name" : "curve", "Item label template" : "#name" }
            definition.curves is array;
            for (var curve in definition.curves)
            {
                annotation { "Name" : "Curve name", "UIHint" : UIHint.ALWAYS_HIDDEN }
                curve.name is string;

                annotation { "Name" : "Reference vertex (optional)", "Filter" : QueryFilterCompound.ALLOWS_VERTEX, "MaxNumberOfPicks" : 1 }
                curve.vertex is Query;

                annotation { "Name" : "hasReference", "UIHint" : UIHint.ALWAYS_HIDDEN }
                curve.hasReference is boolean;

                if (!curve.hasReference)
                {
                    annotation { "Name" : "Location" }
                    isReal(curve.location, { (unitless) : [0, .5, 1] } as RealBoundSpec);
                }

                if (curve.hasReference)
                {
                    annotation { "Name" : "Location", "UIHint" : UIHint.READ_ONLY }
                    isReal(curve.locationPreview, { (unitless) : [0, .5, 1] } as RealBoundSpec);
                }

                annotation { "Name" : "flip direction", "UIHint" : [UIHint.OPPOSITE_DIRECTION_CIRCULAR, UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.MATCH_LAST_ARRAY_ITEM] }
                curve.flip is boolean;

            }

        }
        annotation { "Name" : "Split face" }
        definition.split is boolean;

        if (definition.split)
        {
            annotation { "Name" : "Keep curves", "Default" : false }
            definition.keepCurves is boolean;
        }

        if (!definition.split || (definition.split && definition.keepCurves))
        {
            annotation { "Name" : "Create composite", "Default" : true }
            definition.composite is boolean;
        }

    }
    {
        var uNames = [];
        var vNames = [];
        var curveDef = [];

        ///////// equal creation type /////////

        if (definition.creationType == faceCurveType.equal)
        {
            //equal spacing U curves
            if (definition.uCurves && definition.uCount != 0)
            {

                for (var i = 0; i < definition.uCount; i += 1)
                {
                    var uName = "uCurve" ~ toString(i);
                    uNames = append(uNames, uName);
                }

                var uDef = curveOnFaceDefinition(definition.face, FaceCurveCreationType.DIR2_AUTO_SPACED_ISO, uNames, definition.uCount);
                curveDef = append(curveDef, uDef);
            }

            // equal spacing V curves
            if (definition.vCurves && definition.vCount != 0)
            {
                for (var i = 0; i < definition.vCount; i += 1)
                {
                    var vName = "vCurve" ~ toString(i);
                    vNames = append(vNames, vName);
                }

                var vDef = curveOnFaceDefinition(definition.face, FaceCurveCreationType.DIR1_AUTO_SPACED_ISO, vNames, definition.vCount);
                curveDef = append(curveDef, vDef);
            }
        }
        
        ////////// creation by parameters /////////

        if (definition.creationType == faceCurveType.parameters)
        {
            var uLocations = [];
            var vLocations = [];
            for (var curve in definition.curves)
            {
                if (!isQueryEmpty(context, curve.vertex))
                {
                    const param = evDistance(context, {
                                    "side0" : definition.face,
                                    "side1" : curve.vertex
                                }).sides[0].parameter;

                    curve.location = curve.flip ? param[0] : param[1];


                }
                if (!curve.flip)
                {
                    //TODO tolerant comparrison instead of == for slightly better behavior. It's possible to drag a curve within tolerance of another and still get a failure.
                    if (isIn(curve.location, uLocations))
                        reportFeatureWarning(context, id, "Duplicate curves are ignored.");
                    else
                        uLocations = append(uLocations, curve.location);

                }
                else if (curve.flip)
                {
                    if (isIn(curve.location, uLocations))
                        reportFeatureWarning(context, id, "Duplicate curves are ignored.");
                    else
                        vLocations = append(vLocations, curve.location);
                }
            }
            if (size(uLocations) > 0)
            {
                for (var i = 0; i < size(uLocations); i += 1)
                {
                    var uName = "uCurve" ~ toString(i);
                    uNames = append(uNames, uName);
                }

                var uDef = curveOnFaceDefinition(definition.face, FaceCurveCreationType.DIR2_ISO, uNames, uLocations);
                curveDef = append(curveDef, uDef);
            }
            if (size(vLocations) > 0)
            {
                for (var i = 0; i < size(vLocations); i += 1)
                {
                    var vName = "vCurve" ~ toString(i);
                    vNames = append(vNames, vName);
                }

                var uDef = curveOnFaceDefinition(definition.face, FaceCurveCreationType.DIR1_ISO, vNames, vLocations);
                curveDef = append(curveDef, uDef);
            }
        }

        opCreateCurvesOnFace(context, id + "curvesOnFace", {
                    "curveDefinition" : curveDef
                });
        const curveBodies = qCreatedBy(id + "curvesOnFace", EntityType.BODY);
        const curveEdges = qCreatedBy(id + "curvesOnFace", EntityType.EDGE);

        // adding manipulators to individual edges
        var curveEdgesArray = evaluateQuery(context, curveEdges);
        if (definition.creationType == faceCurveType.parameters && definition.showArrows)
        {
            for (var i = 0; i < size(curveEdgesArray); i += 1)
            {
                var curve = curveEdgesArray[i];

                if (isQueryEmpty(context, definition.curves[i].vertex))
                {
                    // a point on the middle of the curve being edited
                    const manipBase = evEdgeTangentLine(context, {
                                    "edge" : curve,
                                    "parameter" : 0.5
                                }).origin;

                    // finding the face parameter (location) of that point
                    const faceParameter = evDistance(context, {
                                    "side0" : manipBase,
                                    "side1" : definition.face
                                }).sides[1].parameter;

                    const facePlane = evFaceTangentPlane(context, {
                                "face" : definition.face,
                                "parameter" : faceParameter
                            });

                    const manipDirection = !definition.curves[i].flip ? yAxis(facePlane) : facePlane.x;

                    var linearManip = linearManipulator({
                            "base" : manipBase,
                            "direction" : manipDirection,
                            "offset" : 0 * millimeter,
                            "style" : ManipulatorStyleEnum.TANGENTIAL
                        });

                    addManipulators(context, id, { "linearManip" ~ i : linearManip });
                }
            }
        }

        if (definition.split)
        {
            opSplitFace(context, id + "splitFace1", {
                        "faceTargets" : definition.face,
                        "edgeTools" : curveEdges
                    });

            if (!definition.keepCurves)
            {
                opDeleteBodies(context, id + "deleteBodies1", {
                            "entities" : curveBodies
                        });
            }
        }

        if ((definition.split && definition.keepCurves && definition.composite) || (!definition.split && definition.composite))
        {
            opCreateCompositePart(context, id + "compositePart1", {
                        "bodies" : curveBodies,
                        "closed" : true
                    });
        }






    });

export function faceCurveEL(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, specifiedParameters is map, hiddenBodies is Query) returns map
{
    if (definition != oldDefinition)
    {
        for (var i = 0; i < size(definition.curves); i += 1)
        {
            var curve = definition.curves[i];

            // setting the param and preview param if it has a reference or not
            definition.curves[i].hasReference = !isQueryEmpty(context, curve.vertex);

            if (definition.curves[i].hasReference)
            {
                const param = evDistance(context, {
                                "side0" : definition.face,
                                "side1" : curve.vertex
                            }).sides[0].parameter;

                definition.curves[i].location = curve.flip ? param[0] : param[1];
                definition.curves[i].locationPreview = curve.flip ? param[0] : param[1];
            }

            definition.curves[i].name = curve.flip ? "U" ~ (definition.curves[i].location->roundToPrecision(3)) : "V" ~ (definition.curves[i].location->roundToPrecision(3));
        }


        return definition;
    }
}

export function faceCurveManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    const key = keys(newManipulators)[0];
    const i = splitIntoCharacters(key)->last()->stringToNumber();
    const m = newManipulators[key];

    var position = m.base + m.direction * m.offset;

    const newFaceParam = evDistance(context, {
                    "side0" : definition.face,
                    "side1" : position
                }).sides[0].parameter;

    println(newFaceParam);


    definition.curves[i].location = definition.curves[i].flip ? newFaceParam[0] : newFaceParam[1];
    definition.curves[i].locationPreview = definition.curves[i].flip ? newFaceParam[0] : newFaceParam[1];

    // println(i);

    return definition;

}

