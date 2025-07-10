FeatureScript 2656; // Use the latest FS version you are working with
import(path : "onshape/std/geometry.fs", version : "2656.0");

// Constants for debugging
const DEBUG_MODE = true;

annotation { "Feature Type Name" : "Developable to Extruded Surface" }
export const developableToExtrudedSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Solid Body to Process", "Filter" : BodyType.SOLID, "MaxNumberOfPicks" : 1 }
        definition.body is Query;
    }
    {
        if (isQueryEmpty(context, definition.body))
        {
            throw regenError("Please select a single solid body.");
        }

        const allFacesQuery = qOwnedByBody(definition.body, EntityType.FACE);
        const allFacesArray = evaluateQuery(context, allFacesQuery);

        if (DEBUG_MODE)
        {
            println("Processing body: " ~ definition.body ~ " with " ~ size(allFacesArray) ~ " faces.");
        }

        for (var i = 0; i < size(allFacesArray); i += 1)
        {
            const originalFaceQuery = allFacesArray[i];
            const faceIterationId = id + "faceIter" + i;

            var surfaceDef;
            try silent
            {
                surfaceDef = evSurfaceDefinition(context, { "face" : originalFaceQuery });
            }

            if (surfaceDef == undefined || surfaceDef.surfaceType == SurfaceType.EXTRUDED || surfaceDef.surfaceType == SurfaceType.PLANE)
            {
                if (DEBUG_MODE && surfaceDef != undefined)
                {
                    println("Skipping face " ~ i ~ ": Type " ~ surfaceDef.surfaceType);
                }
                else if (DEBUG_MODE)
                {
                    println("Skipping face " ~ i ~ ": Surface definition failed.");
                }
                continue;
            }

            if (surfaceDef.surfaceType != SurfaceType.OTHER && surfaceDef.surfaceType != SurfaceType.SPLINE)
            {
                if (DEBUG_MODE)
                {
                    println("Skipping face " ~ i ~ ": Type " ~ surfaceDef.surfaceType ~ " (not OTHER or SPLINE).");
                }
                continue;
            }

            if (DEBUG_MODE)
            {
                println("Evaluating Face " ~ i ~ " (Query: " ~ originalFaceQuery ~ "): Type " ~ surfaceDef.surfaceType);
            }

            var uIsoCurveForCheckQuery;
            var vIsoCurveForCheckQuery;
            var uIsoGeomDef;
            var vIsoGeomDef;
            var isULinear = false;
            var isVLinear = false;
            const uIsoCheckOpId = faceIterationId + "uIsoCheck";
            const vIsoCheckOpId = faceIterationId + "vIsoCheck";
            var tempBodiesToClean = [];

            // Check U-linearity
            try
            {
                opCreateCurvesOnFace(context, uIsoCheckOpId, {
                    "curveDefinition" : [
                        curveOnFaceDefinition(originalFaceQuery, FaceCurveCreationType.DIR1_ISO, ["checkUIso"], [0.5])
                    ]
                });
                uIsoCurveForCheckQuery = qCreatedBy(uIsoCheckOpId, EntityType.EDGE);
                if (!isQueryEmpty(context, uIsoCurveForCheckQuery))
                {
                    uIsoGeomDef = evCurveDefinition(context, { "edge" : uIsoCurveForCheckQuery });
                    if (uIsoGeomDef != undefined)
                    {
                        if (uIsoGeomDef.curveType == CurveType.LINE)
                        {
                            isULinear = true;
                            if (DEBUG_MODE) { println("Face " ~ i ~ " U-iso identified as LINE by evCurveDefinition. Def: " ~ uIsoGeomDef); }
                        }
                        else if (uIsoGeomDef.curveType == CurveType.SPLINE && uIsoGeomDef.degree == 1 && size(uIsoGeomDef.controlPoints) == 2)
                        {
                            isULinear = true;
                            if (DEBUG_MODE) { println("Face " ~ i ~ " U-iso identified as B-SPLINE (Degree 1, 2 CPs) - considering LINEAR. Def: " ~ uIsoGeomDef); }
                        }
                        else
                        {
                            if (DEBUG_MODE) { println("Face " ~ i ~ " U-iso (" ~ uIsoCurveForCheckQuery ~ ") is type " ~ uIsoGeomDef.curveType ~ " and not a linear B-spline. Def: " ~ uIsoGeomDef); }
                        }
                    } else if (DEBUG_MODE) { println("Face " ~ i ~ " U-iso: evCurveDefinition returned undefined.");}
                    tempBodiesToClean = append(tempBodiesToClean, qCreatedBy(uIsoCheckOpId, EntityType.BODY));
                } else if (DEBUG_MODE) {println("Face " ~ i ~ " U-iso check curve query is empty.");}
            } catch (e) { if (DEBUG_MODE) { println("ERROR during U-iso check for face " ~ i ~ ": " ~ e); } }

            // Check V-linearity
            try
            {
                opCreateCurvesOnFace(context, vIsoCheckOpId, {
                    "curveDefinition" : [
                        curveOnFaceDefinition(originalFaceQuery, FaceCurveCreationType.DIR2_ISO, ["checkVIso"], [0.5])
                    ]
                });
                vIsoCurveForCheckQuery = qCreatedBy(vIsoCheckOpId, EntityType.EDGE);
                if (!isQueryEmpty(context, vIsoCurveForCheckQuery))
                {
                    vIsoGeomDef = evCurveDefinition(context, { "edge" : vIsoCurveForCheckQuery });
                     if (vIsoGeomDef != undefined) { 
                        if (vIsoGeomDef.curveType == CurveType.LINE) 
                        {
                            isVLinear = true;
                            if (DEBUG_MODE) { println("Face " ~ i ~ " V-iso identified as LINE by evCurveDefinition. Def: " ~ vIsoGeomDef); }
                        }
                        else if (vIsoGeomDef.curveType == CurveType.SPLINE && vIsoGeomDef.degree == 1 && size(vIsoGeomDef.controlPoints) == 2) 
                        {
                            isVLinear = true;
                            if (DEBUG_MODE) { println("Face " ~ i ~ " V-iso identified as B-SPLINE (Degree 1, 2 CPs) - considering LINEAR. Def: " ~ vIsoGeomDef); }
                        }
                        else
                        {
                             if (DEBUG_MODE) { println("Face " ~ i ~ " V-iso (" ~ vIsoCurveForCheckQuery ~ ") is type " ~ vIsoGeomDef.curveType ~ " and not a linear B-spline. Def: " ~ vIsoGeomDef); }
                        }
                    } else if (DEBUG_MODE) { println("Face " ~ i ~ " V-iso: evCurveDefinition returned undefined.");}
                    tempBodiesToClean = append(tempBodiesToClean, qCreatedBy(vIsoCheckOpId, EntityType.BODY));
                } else if (DEBUG_MODE) {println("Face " ~ i ~ " V-iso check curve query is empty.");}
            } catch (e) { if (DEBUG_MODE) { println("ERROR during V-iso check for face " ~ i ~ ": " ~ e); } }

            var actualProfileEdgeQuery;
            var extrusionVector;
            var extrusionDepthValue;
            const profileCurveOpId = faceIterationId + "profileCurve";

            if (isULinear && !isVLinear)
            {
                if (DEBUG_MODE) { println("Face " ~ i ~ " is developable along U (linear U-curves). Profile will be V-curve."); }
                if (uIsoGeomDef != undefined)
                {
                    if (uIsoGeomDef.curveType == CurveType.LINE) { extrusionVector = uIsoGeomDef.direction; }
                    else if (uIsoGeomDef.curveType == CurveType.SPLINE && uIsoGeomDef.degree == 1 && size(uIsoGeomDef.controlPoints) == 2)
                        { extrusionVector = normalize(uIsoGeomDef.controlPoints[1] - uIsoGeomDef.controlPoints[0]); }
                    else if (DEBUG_MODE) { println("Face " ~ i ~ " U-iso was marked linear, but its geometry definition is unexpected for direction extraction. Def: " ~ uIsoGeomDef); }

                    if (extrusionVector != undefined) {
                        try {
                            opCreateCurvesOnFace(context, profileCurveOpId, { "curveDefinition" : [curveOnFaceDefinition(originalFaceQuery, FaceCurveCreationType.DIR2_ISO, ["profileV"], [0.5])] });
                            actualProfileEdgeQuery = qCreatedBy(profileCurveOpId, EntityType.EDGE);
                            extrusionDepthValue = evLength(context, { "entities" : uIsoCurveForCheckQuery });
                            if (!isQueryEmpty(context, actualProfileEdgeQuery)) { tempBodiesToClean = append(tempBodiesToClean, qCreatedBy(profileCurveOpId, EntityType.BODY)); }
                            else { actualProfileEdgeQuery = undefined; }
                        } catch (e) { if (DEBUG_MODE) { println("Error creating V-profile for U-linear face " ~ i ~ ": " ~ e); } actualProfileEdgeQuery = undefined; }
                    } else if (DEBUG_MODE) { println("Face " ~ i ~ ": Could not determine extrusionVector from U-iso."); }
                } else if (DEBUG_MODE) {println("Face " ~ i ~ ": uIsoGeomDef is undefined despite isULinear being true.");}
            }
            else if (isVLinear && !isULinear)
            {
                if (DEBUG_MODE) { println("Face " ~ i ~ " is developable along V (linear V-curves). Profile will be U-curve."); }
                if (vIsoGeomDef != undefined)
                {
                    if (vIsoGeomDef.curveType == CurveType.LINE) { extrusionVector = vIsoGeomDef.direction; }
                    else if (vIsoGeomDef.curveType == CurveType.SPLINE && vIsoGeomDef.degree == 1 && size(vIsoGeomDef.controlPoints) == 2)
                        { extrusionVector = normalize(vIsoGeomDef.controlPoints[1] - vIsoGeomDef.controlPoints[0]); }
                    else if (DEBUG_MODE) { println("Face " ~ i ~ " V-iso was marked linear, but its geometry definition is unexpected for direction extraction. Def: " ~ vIsoGeomDef); }

                    if (extrusionVector != undefined) {
                        try {
                            opCreateCurvesOnFace(context, profileCurveOpId, { "curveDefinition" : [curveOnFaceDefinition(originalFaceQuery, FaceCurveCreationType.DIR1_ISO, ["profileU"], [0.5])] }); // Changed to 0.5 for profile
                            actualProfileEdgeQuery = qCreatedBy(profileCurveOpId, EntityType.EDGE);
                            extrusionDepthValue = evLength(context, { "entities" : vIsoCurveForCheckQuery });
                            if (!isQueryEmpty(context, actualProfileEdgeQuery)) { tempBodiesToClean = append(tempBodiesToClean, qCreatedBy(profileCurveOpId, EntityType.BODY)); }
                            else { actualProfileEdgeQuery = undefined; }
                        } catch (e) { if (DEBUG_MODE) { println("Error creating U-profile for V-linear face " ~ i ~ ": " ~ e); } actualProfileEdgeQuery = undefined; }
                    } else if (DEBUG_MODE) { println("Face " ~ i ~ ": Could not determine extrusionVector from V-iso."); }
                } else if (DEBUG_MODE) {println("Face " ~ i ~ ": vIsoGeomDef is undefined despite isVLinear being true.");}
            }
            else
            {
                if (DEBUG_MODE) { println("Face " ~ i ~ " is not developable by XOR rule (isULinear: " ~ isULinear ~ ", isVLinear: " ~ isVLinear ~ ")."); }
            }

            if (actualProfileEdgeQuery != undefined && !isQueryEmpty(context, actualProfileEdgeQuery) &&
                extrusionVector != undefined && extrusionDepthValue != undefined && extrusionDepthValue > TOLERANCE.zeroLength * meter)
            {
                const tempExtrudeOpId = faceIterationId + "tempExtrude";
                var tempExtrudedFaceQuery;

                try
                {
                    if (DEBUG_MODE) { 
                        println("Attempting extrusion for face " ~ i ~ ". Profile: " ~ actualProfileEdgeQuery ~ ", Dir: " ~ extrusionVector ~ ", Depth: " ~ extrusionDepthValue); 
                        // debug(context, actualProfileEdgeQuery, DebugColor.RED); // Visualize profile
                        // debug(context, extrusionVector, DebugColor.GREEN); // This needs a point to draw from, e.g., evVertexPoint of profile
                    }
                    
                    opExtrude(context, tempExtrudeOpId, {
                        "entities" : actualProfileEdgeQuery,
                        "direction" : extrusionVector,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : extrusionDepthValue, 
                        "bodyType" : ToolBodyType.SURFACE
                    });
                    tempExtrudedFaceQuery = qNthElement(qOwnedByBody(qCreatedBy(tempExtrudeOpId, EntityType.BODY), EntityType.FACE), 0);

                    if (!isQueryEmpty(context, tempExtrudedFaceQuery))
                    {
                        if (DEBUG_MODE) { println("Extrusion successful for face " ~ i ~ ". New surface query: " ~ tempExtrudedFaceQuery); }
                        tempBodiesToClean = append(tempBodiesToClean, qCreatedBy(tempExtrudeOpId, EntityType.BODY));

                        var oppositeSenseForReplace = false;
                        try 
                        {
                            // CORRECTED PARAMETER FOR evFaceTangentPlane
                            const originalPlane = evFaceTangentPlane(context, {"face" : originalFaceQuery, "parameter" : vector(0.5, 0.5) });
                            const newPlane = evFaceTangentPlane(context, {"face" : tempExtrudedFaceQuery, "parameter" : vector(0.5, 0.5) });
                            
                            if (originalPlane != undefined && newPlane != undefined && originalPlane.normal != undefined && newPlane.normal != undefined)
                            {
                                if (dot(originalPlane.normal, newPlane.normal) < 0)
                                {
                                    oppositeSenseForReplace = true;
                                    if (DEBUG_MODE) { println("Normals are opposite for face " ~ i ~ ". Setting oppositeSense = true."); }
                                }
                            } else if (DEBUG_MODE) {println("Could not get tangent planes or normals for oppositeSense check for face " ~ i);}
                        } catch (e) { if (DEBUG_MODE) { println("ERROR during oppositeSense check for face " ~ i ~ ": " ~ e); } }

                        opReplaceFace(context, faceIterationId + "replaceOp", {
                            "bodies" : definition.body,
                            "replaceFaces" : originalFaceQuery,
                            "templateFace" : tempExtrudedFaceQuery,
                            "oppositeSense" : oppositeSenseForReplace
                        });
                        if (DEBUG_MODE) { println("opReplaceFace called for face " ~ i ~ "."); }
                    }
                    else
                    {
                        if (DEBUG_MODE) { println("Extrusion created no faces for original face: " ~ originalFaceQuery); }
                    }
                }
                catch (e)
                {
                    if (DEBUG_MODE) { println("ERROR during Extrude or Replace operation for face " ~ i ~ ". Error: " ~ e); }
                }
            }
            else
            {
                 if (DEBUG_MODE && (actualProfileEdgeQuery == undefined || isQueryEmpty(context, actualProfileEdgeQuery))) { println("Skipping reconstruction for face " ~ i ~ ": Profile invalid or empty."); }
                 else if (DEBUG_MODE && extrusionVector == undefined) {println("Skipping reconstruction for face " ~i ~ ": Extrusion vector undefined.");}
                 else if (DEBUG_MODE && (extrusionDepthValue == undefined || extrusionDepthValue <= TOLERANCE.zeroLength * meter)) {println("Skipping reconstruction for face " ~ i ~ ": Invalid extrusion depth " ~ extrusionDepthValue);}
                 else if (DEBUG_MODE) {println("Skipping reconstruction for face " ~ i ~ " for other reasons (Profile: " ~ actualProfileEdgeQuery ~ ", Vector: " ~ extrusionVector ~ ", Depth: " ~ extrusionDepthValue ~ ").");}
            }

            const finalCleanupQuery = qUnion(tempBodiesToClean);
            if (!isQueryEmpty(context, finalCleanupQuery))
            {
                opDeleteBodies(context, faceIterationId + "cleanup", { "entities" : finalCleanupQuery });
                if (DEBUG_MODE) { println("Cleaned up temporary bodies for face " ~ i ~ " using query: " ~ finalCleanupQuery); }
            }
            if (DEBUG_MODE) { println("Finished iteration for face " ~ i ~ ".");}
        }

        if (DEBUG_MODE) { println("Feature execution finished."); }
    });
