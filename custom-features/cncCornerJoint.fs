/*    
    CNC Corner Joint
    
    This is a custom feature for adding joints that can be cut with a CNC 
    connecting two boards meeting at a 90 degree angle. 
    The hammer and fingertip joints can only be used for boards meeting at a corner, 
    while the finger joint also works for boards meeting in a T shape. 
    
    The joint types are based on the "Board Corner Joints" from Jochen Gross' 
    "50 Digital Joints" collection. 
        Original (interactive CD): http://winterdienst.info/50-digital-wood-joints-by-jochen-gros/
        Poster PDF: https://content.instructables.com/FW1/4AF2/I2VLGSNJ/FW14AF2I2VLGSNJ.pdf    
    
    This custom feature is an update / fork of the "Board Corner Joint" FeatureScript created in 
    2019 by Aaron Hoover. Which in turn was based on @lemon1324's "Laser Joint" feature.
    Which itself was originally based on the "Box Joint" feature by Neil Cooke.
        Board Corner Joint: https://cad.onshape.com/documents/efc963b657ec24bd8613fb51
        Laser Joint: https://cad.onshape.com/documents/578830e4e4b0e65410f9c34e
        Box Joint: https://cad.onshape.com/documents/57612867e4b018f59e4d52ce

    The biggest differences from Aaron's implementation are: 
    - It should be more robust. The original implementation expected the intersecting bodies 
      to have been extruded in a specific way and would fail otherwise. 
    - Added adjustable positioning of the overcuts (dogbones), which can be helpful for hiding them.
    - Added option to fillet the overcuts (for smoother CNC toolpaths)
    - Made finger joints symmetric (both ends are either tenon or mortise)
    - Removed "fingertip tenons with key" joint type because it seemed broken to me.
    - Fixed tenon and mortise being reversed for hammer joints.
    - Simplified and modernized some of the code and removed dependencies.
    
    Version history: 
    1.0     Jan 12 2023     First 'official' release after branching from Aaron's original
                            2019 "Board Corner Joints" implementation.
    1.1     Jan 23 2023     Added option to specify multiple tenon and mortise parts. 
                            All tenon parts need to form 90 degree corners with all mortise parts.
    1.2     Jan 24 2023     Improved robustness, especially for through joints and lapped & secret joints.
                            Added "Edge offset / margin" parameter and updated PDF documentation.
                                                
*/ 

FeatureScript 1913;
import(path : "onshape/std/common.fs", version : "1913.0");

annotation { 
    "Feature Type Name" : "CNC Corner Joint",
    "Feature Type Description" : "Add joints that can be cut with a CNC connecting two boards meeting at a 90 degree angle"
}
export const myFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Select the type of frame corner joint" }
        definition.jointType is BoardCornerJointType;

        annotation { "Name" : "Operation type", "UIHint" : ["HORIZONTAL_ENUM", "REMEMBER_PREVIOUS_VALUE"],
                    "Description" : "<b>Single:</b> Create a joint between specified tenon and mortise parts. <br> <b>Multi:</b> Create joints between all pairs or tenon and mortise parts." }
        definition.operationType is SingleOrMulti;

        if (definition.operationType == SingleOrMulti.SINGLE)
        {
            annotation { "Name" : "Tenon part", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 1 }
            definition.tenonBody is Query;

            annotation { "Name" : "Mortise part", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 1 }
            definition.mortiseBody is Query;
        }
        else
        {
            annotation { "Name" : "Tenon part(s)", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 50 }
            definition.tenonBodies is Query;

            annotation { "Name" : "Mortise part(s)", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 50 }
            definition.mortiseBodies is Query;
        }

        annotation { "Name" : "Tool diameter", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        isLength(definition.overcutD, OVERCUT_DIAMETER_BOUNDS);

        annotation { "Name" : "Overcut position", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        isReal(definition.overcutPosition, OVERCUT_POSITION_BOUNDS);

        annotation { "Name" : "Fillet overcuts", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        definition.applyFillet is boolean;

        if (definition.applyFillet)
        {
            annotation { "Name" : "Fillet radius", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
            isLength(definition.filletRadius, FILLET_RADIUS_BOUNDS);
        }

        if (definition.jointType == BoardCornerJointType.FINGERTIP_TENONS ||
            definition.jointType == BoardCornerJointType.LAPPED_FINGERTIP_TENONS ||
            definition.jointType == BoardCornerJointType.SECRET_FINGERTIP_TENONS)
        {
            annotation { "Name" : "Alignment tenon min. width" }
            isLength(definition.alignmentTenonWidth, TENON_WIDTH_BOUNDS);
        }
        else
        {
            annotation { "Name" : "Number of tenons" }
            isInteger(definition.numTenons, TENON_BOUNDS);

            if (definition.jointType != BoardCornerJointType.HAMMER_TENONS &&
                definition.jointType != BoardCornerJointType.LAPPED_HAMMER_TENONS)
            {
                annotation { "Name" : "Toggle tenon/gap", "UIHint" : "OPPOSITE_DIRECTION" }
                definition.tenonSense is boolean;
            }
        }

        annotation { "Name" : "Edge offset / margin" }
        isLength(definition.edgeOffset, ZERO_DEFAULT_LENGTH_BOUNDS);


        if (definition.jointType == BoardCornerJointType.LAPPED_FINGER_TENONS || 
            definition.jointType == BoardCornerJointType.SECRET_FINGER_TENONS ||
            definition.jointType == BoardCornerJointType.LAPPED_FINGERTIP_TENONS || 
            definition.jointType == BoardCornerJointType.SECRET_FINGERTIP_TENONS ||
            definition.jointType == BoardCornerJointType.LAPPED_HAMMER_TENONS)
        {
            annotation { "Name" : "Lap Depth", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
            isLength(definition.lapDepth, LAP_DEPTH_BOUNDS);
        }

        annotation { "Name" : "Add allowance", "Default" : true, "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        definition.allowance is boolean;

        if (definition.allowance)
        {
            annotation { "Name" : "Allowance", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
            isLength(definition.allowanceVal, FIT_OFFSET_LENGTH_BOUNDS);

            annotation { "Name" : "Apply to tenon mating faces", "Default" : true,
                        "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
            definition.tenonFaceAllowance is boolean;

            if (definition.jointType != BoardCornerJointType.HAMMER_TENONS &&
                definition.jointType != BoardCornerJointType.LAPPED_HAMMER_TENONS)
            {
            annotation { "Name" : "Apply to mortise mating faces", "Default" : true,
                        "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
            definition.mortiseFaceAllowance is boolean;
            }
        }
    }
    // ============================================ BODY ==================================================
    {
        if (definition.operationType == SingleOrMulti.SINGLE)
        {
            createJoint(context, id, definition);
        }
        else
        {
            for (var mortiseIndex, mortiseBody in evaluateQuery(context, definition.mortiseBodies))
            {
                for (var tenonIndex, tenonBody in evaluateQuery(context, definition.tenonBodies))
                {
                    definition.mortiseBody = mortiseBody;
                    definition.tenonBody = tenonBody;
                    const jointId = id + mortiseIndex + tenonIndex;
                    createJoint(context, jointId, definition);
                }
            }
        }
    });

function createJoint(context is Context, id is Id, definition is map)
{
    // The current implementation of Hammer joints swaps tenon and mortise
    // This is a workaround to correct that.
    const itsHammerTime = isHammerJointType(definition.jointType);
    if (itsHammerTime)
    {
        const tmp = definition.tenonBody;
        definition.tenonBody = definition.mortiseBody;
        definition.mortiseBody = tmp;
        definition.mortiseFaceAllowance = true;
        definition.tenonSense = false;
    }    

    definition.tenonExtrudePlane = getPlaneInsideJoint(context, definition.tenonBody, definition.mortiseBody);
    definition.mortiseExtrudePlane = getPlaneInsideJoint(context, definition.mortiseBody, definition.tenonBody);
    definition.jointXAxis = normalize(cross(definition.tenonExtrudePlane.normal, definition.mortiseExtrudePlane.normal));
    
    const tenonGeometry = cutTenons(context, id + "tenons", definition);
    const mortiseGeometry = cutMortises(context, id + "mortises", definition);

    var facesToMove = mortiseGeometry.jointFaces;

    // Offset joint surfaces for allowances
    if (definition.allowance)
    {
        // Faces internal to the joint
        opOffsetFace(context, id + "mortiseOffsetJointFace", {
                    "moveFaces" : facesToMove,
                    "offsetDistance" : -1 * definition.allowanceVal
                });

        if (isFingerTipJointType(definition.jointType))
        {
            opOffsetFace(context, id + "fingertipOffsetFace", {
                        "moveFaces" : mortiseGeometry.fingertipFaces,
                        "offsetDistance" : -1 * definition.allowanceVal
                    });
        }

        const offsetTenonFaces = (!itsHammerTime && definition.tenonFaceAllowance) || (itsHammerTime && definition.mortiseFaceAllowance);
        const offsetMortiseFaces = (!itsHammerTime && definition.mortiseFaceAllowance) || (itsHammerTime && definition.tenonFaceAllowance);

        // Mating faces on the tenon part
        if (offsetTenonFaces)
        {
            if (size(evaluateQuery(context, tenonGeometry.faces)) > 0)
            {
                opOffsetFace(context, id + "tenonOffsetMatingFace", {
                            "moveFaces" : tenonGeometry.faces,
                            "offsetDistance" : -1 * definition.allowanceVal
                        });
            }
        }

        // Mating faces on the mortise part
        if (offsetMortiseFaces)
        {
            if (size(evaluateQuery(context, mortiseGeometry.matingFaces)) > 0)
            {
                opOffsetFace(context, id + "mortiseOffsetMatingFace", {
                            "moveFaces" : mortiseGeometry.matingFaces,
                            "offsetDistance" : -1 * definition.allowanceVal
                        });
            }
        }
    }

    if (size(evaluateQuery(context, tenonGeometry.edges)) > 0)
    {
        cornerOvercut(context, id + "tenonOvercut", {
                    "edges" : tenonGeometry.edges,
                    "D" : definition.overcutD,
                    "applyFillet" : definition.applyFillet,
                    "filletRadius" : definition.filletRadius,
                    "overcutPosition" : definition.overcutPosition,
                    "jointXAxis" : definition.jointXAxis,
                });
    }

    cornerOvercut(context, id + "mortiseOvercut", {
                "edges" : mortiseGeometry.edges,
                "D" : definition.overcutD,
                "ref_plane" : definition.mortiseExtrudePlane,
                "jointType" : definition.jointType,
                "applyFillet" : definition.applyFillet,
                "filletRadius" : definition.filletRadius,
                "overcutPosition" : definition.overcutPosition,
                "jointXAxis" : definition.jointXAxis,
            });

}

function cutTenons(context is Context, id is Id, definition is map) returns map
{
    opBoolean(context, id + "intersect", {
                "tools" : qUnion([definition.tenonBody, definition.mortiseBody]),
                "operationType" : BooleanOperationType.INTERSECTION,
                "keepTools" : true
            });

    const intersectBodies = qCreatedBy(id + "intersect", EntityType.BODY);
    const M = size(evaluateQuery(context, intersectBodies));

    var createdEdges = qNothing();
    var createdFaces = qNothing();
    var ftpFaces = qNothing();

    if (M == 0)
    {
        throw regenError("Operation requires two intersecting parts");
    }
    else
    {
        const cSys = coordSystem(definition.tenonExtrudePlane.origin, definition.jointXAxis, definition.tenonExtrudePlane.normal);
        var yMax = 0;

        const boundingEdge = qFarthestAlong(qIntersectsLine(qOwnedByBody(definition.tenonBody, EntityType.EDGE), line(cSys.origin, yAxis(cSys))), yAxis(cSys));
        const boundingLine = evLine(context, {
                    "edge" : boundingEdge
                });

        yMax = evDistance(context, {
                        "side0" : cSys.origin,
                        "side1" : boundingEdge,
                        "maximum" : false
                    }).distance;
        const sketchPlane is Plane = plane(cSys);
        definition.tenonCSys = cSys;
        // Make machinable slots in tenon part for each intersection
        for (var j = 0; j < M; j += 1)
        {
            const sketch = newSketchOnPlane(context, id + j + "sketch", {
                        "sketchPlane" : sketchPlane
                    });

            // Compute sections to cut out
            const gapRegions = drawJointGaps(context, id + j, definition, {
                        "sketch" : sketch,
                        "csys" : cSys,
                        "intersect" : qNthElement(intersectBodies, j),
                        "y_max" : yMax
                    });

            if (size(evaluateQuery(context, gapRegions)) > 0)
            {
                // Cut by extruding solids and then subtracting from tenon part
                if (definition.jointType == BoardCornerJointType.SECRET_FINGER_TENONS ||
                    definition.jointType == BoardCornerJointType.SECRET_FINGERTIP_TENONS ||
                    definition.jointType == BoardCornerJointType.LAPPED_HAMMER_TENONS)
                {
                    opExtrude(context, id + j + "extrude", {
                                "entities" : gapRegions,
                                "direction" : -evOwnerSketchPlane(context, { "entity" : gapRegions }).normal,
                                "startBound" : BoundingType.BLIND,
                                "endBound" : BoundingType.BLIND,
                                "startDepth" : 0 * meter,
                                "endDepth" : definition.lapDepth
                            });
                }
                else
                {
                    opExtrude(context, id + j + "extrude", {
                                "entities" : gapRegions,
                                "direction" : evOwnerSketchPlane(context, { "entity" : gapRegions }).normal,
                                "endBound" : BoundingType.THROUGH_ALL,
                                "startBound" : BoundingType.THROUGH_ALL
                            });
                }


                opBoolean(context, id + j + "subtract", {
                            "tools" : qCreatedBy(id + j + "extrude", EntityType.BODY),
                            "targets" : definition.tenonBody,
                            "operationType" : BooleanOperationType.SUBTRACTION,
                            "keepTools" : false
                        });

                opDeleteBodies(context, id + j + "deleteSketch", {
                            "entities" : qCreatedBy(id + j + "sketch", EntityType.BODY)
                        });

                //Find concave edges parallel to extrusion direction (machining axis) created by this cut
                const edges = qCreatedBy(id + j + "subtract", EntityType.EDGE)->qOwnedByBody(definition.tenonBody)
                    ->qParallelEdges(definition.tenonExtrudePlane.normal);
                const edgeFilter = mapArray(evaluateQuery(context, edges), function(x)
                    {
                        return evEdgeConvexity(context, {
                                        "edge" : x
                                    }) == EdgeConvexityType.CONCAVE ? x : qNothing();
                    });
                createdEdges = qUnion([createdEdges, qUnion(edgeFilter)]);

                // Find faces that will touch the large face of the mortise part
                var faces = qGeometry(qOwnedByBody(qCreatedBy(id + j + "subtract", EntityType.FACE), definition.tenonBody), GeometryType.PLANE)->qParallelPlanes(yAxis(cSys));
                createdFaces = qUnion([createdFaces, faces]);
            }
        }

        // Clean up the bodies created to check intersections
        opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : intersectBodies
                });
    }

    return { "edges" : createdEdges, "faces" : createdFaces, "fingertipFaces" : ftpFaces };
}

/*
 * Function cuts mortises into mortise part.
 * Assumes tenons have already been cut.
 * returns map containing:
 *      edges: concave edges created by the cut for optional overcut
 *      jointFaces: faces created by this operation internal to the joint for optional allowance
 *      matingFaces: faces created by this operation that mate with the face of the tab part
 *             for optional allowance
 */
function cutMortises(context is Context, id is Id, definition is map) returns map
{
    // create intersections of mortise part with tenon part
    opBoolean(context, id + "intersect", {
                "tools" : qUnion([definition.tenonBody, definition.mortiseBody]),
                "operationType" : BooleanOperationType.INTERSECTION,
                "keepTools" : true
            });

    // find the intersections between the mortise and tenon part
    const intersectBodies = qCreatedBy(id + "intersect", EntityType.BODY);
    const M = size(evaluateQuery(context, intersectBodies));

    const mortiseCSys = coordSystem(definition.mortiseExtrudePlane.origin, -definition.jointXAxis, definition.mortiseExtrudePlane.normal);
    const mortiseSketchPlane is Plane = plane(mortiseCSys);


    opBoolean(context, id + "subtract", {
                "tools" : intersectBodies,
                "targets" : definition.mortiseBody,
                "operationType" : BooleanOperationType.SUBTRACTION,
                "keepTools" : false
            });

    if (isFingerTipJointType(definition.jointType))
    {
        //Create a sketch to radius the ends of the mortise slots according the the tool diameter
        const fingertipSketch = newSketchOnPlane(context, id + "ftp_sketch", {
                    "sketchPlane" : mortiseSketchPlane
                });

        //Ends of the mortise slots are the smallest edges coincident with the inner face of the tenon body, parallel to the joint axis
        const edgesToRound = qCreatedBy(id + "subtract", EntityType.EDGE)
            ->qOwnedByBody(definition.mortiseBody)
            ->qParallelEdges(mortiseCSys.xAxis)
            ->qCoincidesWithPlane(definition.tenonExtrudePlane)
            ->qSmallest();

        const f = mapArray(evaluateQuery(context, edgesToRound), function(x)
            {
                return evEdgeConvexity(context, {
                                "edge" : x
                            }) == EdgeConvexityType.CONVEX ? x : qNothing();
            });

        var edges = qUnion(f);
        var i = 0;
        for (var edge in evaluateQuery(context, edges))
        {
            var center = worldToPlane(mortiseSketchPlane, evEdgeTangentLine(context, {
                            "edge" : edge,
                            "parameter" : 0.5
                        }).origin);
            skCircle(fingertipSketch, "circle" ~ i, {
                        "center" : center,
                        "radius" : definition.overcutD / 2
                    });
            i += 1;
        }

        skSolve(fingertipSketch);

        if (definition.jointType == BoardCornerJointType.FINGERTIP_TENONS)
        {
            opExtrude(context, id + "ftp_extrude", {
                        "entities" : qSketchRegion(id + "ftp_sketch"),
                        "direction" : -mortiseSketchPlane.normal,
                        "endBound" : BoundingType.THROUGH_ALL,
                        "startBound" : BoundingType.THROUGH_ALL
                    });
        }
        else
        {
            opExtrude(context, id + "ftp_extrude", {
                        "entities" : qSketchRegion(id + "ftp_sketch"),
                        "direction" : -mortiseSketchPlane.normal,
                        "startBound" : BoundingType.BLIND,
                        "endBound" : BoundingType.BLIND,
                        "startDepth" : 0 * meter,
                        "endDepth" : definition.lapDepth
                    });
        }

        opBoolean(context, id + "fingertips", {
                    "tools" : qCreatedBy(id + "ftp_extrude", EntityType.BODY),
                    "targets" : definition.mortiseBody,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "keepTools" : false
                });

        opDeleteBodies(context, id + "ftp_deleteSketch", {
                    "entities" : qCreatedBy(id + "ftp_sketch", EntityType.BODY)
                });

    }

    if (size(evaluateQuery(context, intersectBodies)) > 0)
    {
        opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : intersectBodies
                });
    }

    const edges = qCreatedBy(id + "subtract", EntityType.EDGE)->qOwnedByBody(definition.mortiseBody)
        ->qParallelEdges(mortiseCSys.zAxis);
    const filter = mapArray(evaluateQuery(context, edges), function(x)
        {
            return evEdgeConvexity(context, {
                            "edge" : x
                        }) == EdgeConvexityType.CONCAVE ? x : qNothing();
        });
    var createdEdges = qUnion(filter);

    // Find faces created by this cut, and then sort them into joint internal and mating faces
    const faces = qCreatedBy(id + "subtract", EntityType.FACE)->qOwnedByBody(definition.mortiseBody)->qGeometry(GeometryType.PLANE);
    // Get the set of (unique) normals for the faces
    var uniqueNormalsMap = {};
    for (var face in evaluateQuery(context, faces))
    {
        const plane = evPlane(context, { "face" : face });
        uniqueNormalsMap[plane.normal] = true;
    }
    const uniqueNormals = keys(uniqueNormalsMap);

    const jointInternalFaces = qUnion(mapArray(uniqueNormals, function(normal)
            {
                // Joint internal faces are those for which there is a face with opposite normal
                return faces->qParallelPlanes(-normal, false);
            }));
    const matingFaces = qSubtraction(faces, jointInternalFaces);

    var fFaces = qNothing();
    if (isFingerTipJointType(definition.jointType))
    {
        fFaces = qOwnedByBody(qUnion([qCreatedBy(id + "endFillets", EntityType.FACE), qCreatedBy(id + "fingertips", EntityType.FACE)]), definition.mortiseBody);
    }

    const mortiseGeometry is map = {
            "jointFaces" : jointInternalFaces,
            "matingFaces" : matingFaces,
            "fingertipFaces" : fFaces,
            "edges" : createdEdges
        };

    return mortiseGeometry;
}


function cornerOvercut(context is Context, id is Id, definition is map)
{
    const partBody = qOwnerBody(definition.edges);
    var cylinders = qNothing();

    const offsetDist = 0.5 * definition.D;

    const I = size(evaluateQuery(context, definition.edges));
    const mortise is boolean = (id[1] == "mortiseOvercut");

    for (var i = 0; i < I; i += 1)
    {
        const edge = qNthElement(definition.edges, i);
        const endpoints = evaluateQuery(context, qAdjacent(edge, AdjacencyType.VERTEX));

        // Calculate direction to offset cylinder from edge
        const faces = qAdjacent(edge, AdjacencyType.EDGE);
        var offsetDir = vector(0, 0, 0);
        const J = size(evaluateQuery(context, faces));
        for (var j = 0; j < J; j += 1)
        {
            const faceNormal = evFaceNormalAtEdge(context, {
                        "edge" : edge,
                        "face" : qNthElement(faces, j),
                        "parameter" : 0.5
                    });
            const normalXproj = abs(dot(definition.jointXAxis, faceNormal));
            const normalWeight = normalXproj * definition.overcutPosition + (1 - normalXproj) * (1 - definition.overcutPosition);

            offsetDir += normalWeight * faceNormal;
        }
        offsetDir = normalize(offsetDir);

        const topCenter = evVertexPoint(context, { "vertex" : endpoints[0] }) + offsetDir * offsetDist;
        var bottomCenter = evVertexPoint(context, { "vertex" : endpoints[1] }) + offsetDir * offsetDist;

        // If cutting overcuts for hammer tenons, ensure that overcuts for corners at edges of wider face propagate
        // through entire thickness.
        if (mortise && isHammerJointType(definition.jointType))
        {
            if (size(evaluateQuery(context, qCoincidesWithPlane(endpoints[0], definition.ref_plane))) > 0)
            {
                bottomCenter = bottomCenter + -definition.ref_plane.normal * evLength(context, {
                                "entities" : edge });
            }
        }

        // Create cylinder along edge
        fCylinder(context, id + ("cylinder" ~ i), {
                    "topCenter" : topCenter,
                    "bottomCenter" : bottomCenter,
                    "radius" : 0.5 * definition.D
                });

        // Keep a list of generated cylinders
        cylinders = qUnion([cylinders, qCreatedBy(id + ("cylinder" ~ i), EntityType.BODY)]);
    }

    // Subtract volume of all the cylinders from the mortise part
    opBoolean(context, id + "subtract", {
                "tools" : cylinders,
                "targets" : partBody,
                "operationType" : BooleanOperationType.SUBTRACTION
            });
    if (definition.applyFillet)
    {
        const edges = qGeometry(qCreatedBy(id + "subtract", EntityType.EDGE), GeometryType.LINE);
        opFillet(context, id + "fillet", {
                    "entities" : edges,
                    "radius" : definition.filletRadius
                });
    }

}

/*
 * Function sketches regions in which to cut the tenon part.
 * returns Query for all resulting sketch regions
 * (Copied from Laser Joint by Arul Suresh)
 */
function drawJointGaps(context is Context, id is Id, definition is map, parameters is map) returns Query
{
    // Set up information about the joint geometry for calculations
    const bound is Box3d = evBox3d(context, {
                "topology" : parameters.intersect,
                "cSys" : parameters.csys
            });

    const L = bound.maxCorner[0] - bound.minCorner[0];

    const info is map = {
            "L" : L,
            "N" : definition.numTenons,
            "sense" : definition.tenonSense,
            "Tmin" : definition.tenonMinW,
            "Tmax" : definition.tenonMaxW,
            "gapAdjust" : definition.gapAdjust,
            "Gmin" : definition.gapMinW,
            "xmin" : bound.minCorner[0],
            "xmax" : bound.maxCorner[0],
            "ymin" : bound.minCorner[1],
            "ymax" : parameters.y_max
        };


    // Get a list of X-coordinates
    const xl = generateXLocations(context, id, definition, info);

    // Depending on the tenon sense, draw rectangles in even or odd gaps between X-coordinates
    const i0 = (info.sense) ? 0 : 1;
    for (var i = i0; i < size(xl) - 1; i += 2)
    {
        if (isHammerJointType(definition.jointType))
        {
            const yMid = (info.ymax - info.ymin) / 2 + info.ymin;
            const hammerOverhang = 0.2 * (info.L - 2 * definition.edgeOffset) / (info.N * 1.6);
            //Take care of the end "half hammers"
            skRectangle(parameters.sketch, "rectangle" ~ i ~ "a", {
                        "firstCorner" : vector(xl[i] + hammerOverhang, yMid),
                        "secondCorner" : vector(xl[i + 1] - hammerOverhang, info.ymax)
                    });

            skRectangle(parameters.sketch, "rectangle" ~ i ~ "b", {
                        "firstCorner" : vector(xl[i], info.ymin),
                        "secondCorner" : vector(xl[i + 1], yMid)
                    });
        }
        else
        {
            skRectangle(parameters.sketch, "rectangle" ~ i, {
                        "firstCorner" : vector(xl[i], info.ymin),
                        "secondCorner" : vector(xl[i + 1], info.ymax)
                    });
            //Overcut the base of the gap by the tool radius
            if (isFingerTipJointType(definition.jointType))
            {
                var radius = abs(xl[i + 1] - xl[i]) / 2;
                if (tolerantEquals(2 * radius, definition.overcutD))
                {
                    skCircle(parameters.sketch, "circle1" ~ i, {
                                "center" : vector((xl[i] + xl[i + 1]) / 2, info.ymin),
                                "radius" : radius
                            });
                }
            }
        }
    }

    // Remove a bit of material from the 'bottoms' of the tenons to form the lap when the
    // tenons are subtracted from the mortise side
    if (definition.jointType == BoardCornerJointType.LAPPED_FINGER_TENONS ||
        definition.jointType == BoardCornerJointType.SECRET_FINGER_TENONS ||
        definition.jointType == BoardCornerJointType.LAPPED_FINGERTIP_TENONS ||
        definition.jointType == BoardCornerJointType.SECRET_FINGERTIP_TENONS)
    {
        skRectangle(parameters.sketch, "rectangle", {
                    "firstCorner" : vector(info.xmin, info.ymax),
                    "secondCorner" : vector(info.xmax, info.ymin + definition.lapDepth)
                });
    }

    skSolve(parameters.sketch);
    return qSketchRegion(id + "sketch");
}

/*
 * Function generates splitting x-coordinates between tenon/slot regions of the joint.
 * returns an array of x-coordinates
 * (Initially copied from Laser Joint by Arul Suresh)
 */
function generateXLocations(context is Context, id is Id, definition is map, info is map) returns array
{
    // Tenon spacing as set in feature
    // R is the number of possible sketch regions = #tenons + #slots
    //      #tenons = N
    //      #mortises = N
    var R;
    var K;
    const L = info.L - 2 * definition.edgeOffset;
    const x0 = info.xmin + definition.edgeOffset;
    if (isFingerTipJointType(definition.jointType))
    {
        K = definition.overcutD;
    }
    else if (isHammerJointType(definition.jointType))
    {
        R = 2 * info.N + 1;
        //Assumes 40% overlap of hammer tenons
        K = L / (info.N * 1.6);
    }
    else
    {
        R = 2 * info.N + (definition.tenonSense ? 1 : -1);
        K = L / R;
    }

    if (K < definition.overcutD)
    {
        throw regenError("Tenon width less than cutter diameter", ["overcutD"]);
    }

    // Generate the splitting points
    var xLocations;
    if (isFingerTipJointType(definition.jointType))
    {
        var workingWidth = L - 2 * definition.alignmentTenonWidth;
        if (workingWidth < 2 * definition.overcutD)
        {
            throw regenError("Insufficient width for fingertip tenons.");
        }
        var ftCount = floor(workingWidth / definition.overcutD) - floor(workingWidth / definition.overcutD) % 2;
        R = ftCount + 4;
        var pad = (workingWidth - ftCount * definition.overcutD) / 2;
        xLocations = zeroVector(R + 1);
        xLocations[0] = info.xmin;
        xLocations[1] = x0;
        for (var i = 0; i <= ftCount; i += 1)
        {
            xLocations[i + 2] = xLocations[1] + definition.alignmentTenonWidth + pad + i * definition.overcutD;
        }
        xLocations[R - 1] = xLocations[R - 2] + definition.alignmentTenonWidth + pad;
        xLocations[R] = xLocations[R - 1] + definition.edgeOffset;
    }
    else if (isHammerJointType(definition.jointType))
    {
        xLocations = zeroVector(R + 1);
        xLocations[0] = x0;
        for (var i = 1; i <= R; i += 1)
        {
            if (i % 2 == 1)
            {
                if (i == 1 || i == R)
                {
                    xLocations[i] = xLocations[i - 1] + 0.3 * K;
                }
                else
                {
                    xLocations[i] = xLocations[i - 1] + 0.6 * K;
                }
            }
            else
            {
                xLocations[i] = xLocations[i - 1] + 1 * K;
            }
        }
    }
    else
    {
        xLocations = zeroVector(R + 1);
        xLocations[0] = info.xmin;
        xLocations[R] = info.xmin + info.L;
        for (var i = 1; i < R; i += 1)
        {
            xLocations[i] = x0 + i * K;
        }
    }

    return xLocations;
}

function getPlaneInsideJoint(context is Context, body is Query, otherBody is Query) returns Plane 
{
    const otherCentroid = evApproximateCentroid(context, { "entities" : otherBody });
    // I assume that the largest face will either be the one laying on the CNC table, or the one on the opposite side
    const largestFacePlane = evPlane(context, { "face" : qOwnedByBody(body, EntityType.FACE)->qLargest() });
    const distToOtherCentroid = dot(largestFacePlane.normal, (otherCentroid - largestFacePlane.origin));
    var resultPlane = largestFacePlane;
    if (distToOtherCentroid < 0) {
        // The face is facing away from the other body, so to get the inside face, we are looking for the face on the opposite side
        const dir = -largestFacePlane.normal;
        const insideFace = qOwnedByBody(body, EntityType.FACE)->qParallelPlanes(dir)->qFarthestAlong(dir);
        resultPlane = evPlane(context, { "face" : insideFace });
    }
    return resultPlane;
}



export enum BoardCornerJointType
{
    annotation { "Name" : "Finger Tenons" }
    FINGER_TENONS,
    annotation { "Name" : "Lapped Finger Tenons" }
    LAPPED_FINGER_TENONS,
    annotation { "Name" : "Secret Finger Tenons" }
    SECRET_FINGER_TENONS,
    annotation { "Name" : "Fingertip Tenons" }
    FINGERTIP_TENONS,
    annotation { "Name" : "Lapped Fingertip Tenons" }
    LAPPED_FINGERTIP_TENONS,
    annotation { "Name" : "Secret Fingertip Tenons" }
    SECRET_FINGERTIP_TENONS,
    annotation { "Name" : "Hammer Tenons" }
    HAMMER_TENONS,
    annotation { "Name" : "Lapped Hammer Tenons" }
    LAPPED_HAMMER_TENONS
}

export enum SingleOrMulti
{
    annotation { "Name" : "Single Corner" }
    SINGLE,
    annotation { "Name" : "Multiple" }
    MULTI
}


predicate isFingerTipJointType(jointType is BoardCornerJointType)
{
    jointType == BoardCornerJointType.FINGERTIP_TENONS ||
        jointType == BoardCornerJointType.LAPPED_FINGERTIP_TENONS ||
        jointType == BoardCornerJointType.SECRET_FINGERTIP_TENONS;
}

predicate isHammerJointType(jointType is BoardCornerJointType)
{
    jointType == BoardCornerJointType.HAMMER_TENONS ||
        jointType == BoardCornerJointType.LAPPED_HAMMER_TENONS;
}

const OVERCUT_DIAMETER_BOUNDS =
{
            (inch) : [0, 0.25, 1.0],
        } as LengthBoundSpec;

const OVERCUT_POSITION_BOUNDS =
{
            (unitless) : [0, 0.5, 1.0],
        } as RealBoundSpec;


const FILLET_RADIUS_BOUNDS =
{
            (inch) : [0, 0.25, 2.0],
            (millimeter) : 5
        } as LengthBoundSpec;


const TENON_BOUNDS =
{
            (unitless) : [1, 3, 20],
        } as IntegerBoundSpec;

const FIT_OFFSET_LENGTH_BOUNDS =
{
            (inch) : [0, 0.001, 12],
            (millimeter) : 0.2
        } as LengthBoundSpec;

const TENON_WIDTH_BOUNDS =
{
            (inch) : [0, 0.5, 6]
        } as LengthBoundSpec;


const LAP_DEPTH_BOUNDS =
{
            (inch) : [0, 0.5, 3],
            (millimeter) : 10
        } as LengthBoundSpec;


