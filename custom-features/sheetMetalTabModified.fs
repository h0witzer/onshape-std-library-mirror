FeatureScript 2837;
// This module is part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab for the license text.
// Copyright (c) 2013-Present PTC Inc.
// Modified to accept surface bodies in addition to planar faces for Boss Display integration.
// Key modification: Line 36 precondition filter removes GeometryType.PLANE restriction,
// allowing non-planar surface bodies (e.g., cylindrical, conical surfaces) to be used as tab profiles.

// Imports used in interface
export import(path : "onshape/std/query.fs", version : "2837.0");
export import(path : "onshape/std/tool.fs", version : "2837.0");

// Imports used internally
import(path : "onshape/std/attributes.fs", version : "2837.0");
import(path : "onshape/std/boolean.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/moveFace.fs", version : "2837.0");
import(path : "onshape/std/transform.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/topologyUtils.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");

/**
 * Feature adding tabs to sheet metal faces using supplied surface geometry.
 *
 */
annotation { "Feature Type Name" : "Tab",
        "Editing Logic Function" : "sheetMetalTabEditingLogic" }
export const sheetMetalTab = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Tab profile", "Filter" : EntityType.FACE && ConstructionObject.NO }
        definition.tabFaces is Query;

        annotation { "Name" : "Flange to merge", "Filter" : SheetMetalDefinitionEntityType.FACE && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES }
        definition.booleanUnionScope is Query;

        annotation { "Name" : "Subtraction offset" }
        isLength(definition.booleanOffset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation { "Name" : "Subtraction scope", "Filter" : (SheetMetalDefinitionEntityType.FACE && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES) || (BodyType.SOLID && EntityType.BODY && ActiveSheetMetal.NO) }
        definition.booleanSubtractScope is Query;
    }
    {
        // this is not necessary but helps with correct error reporting in feature pattern
        checkNotInFeaturePattern(context, definition.tabFaces, ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);

        createTools(context, id + "extract", definition.tabFaces);

        const unionEntities = try silent(getSMDefinitionEntities(context, definition.booleanUnionScope));
        if (unionEntities == undefined || unionEntities == [])
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_WALL, ["booleanUnionScope"]);
        }
        const unionEntityQuery = qUnion(unionEntities);
        const unionBodies = evaluateQuery(context, qOwnerBody(unionEntityQuery));

        if (isAtVersionOrLater(context, FeatureScriptVersionNumber.V1235_TAB_MERGE_AND_SUBTRACT))
        {
            // Do not allow users to target the same flange for both merging and subtraction
            const subtractEntityQuery = qUnion(getSMDefinitionEntities(context, definition.booleanSubtractScope));
            const entitiesInBothUnionAndSubtract = evaluateQuery(context, qIntersection([unionEntityQuery, subtractEntityQuery]));
            if (entitiesInBothUnionAndSubtract != [])
            {
                throw regenError(ErrorStringEnum.SHEET_METAL_TAB_MERGE_AND_SUBTRACT_SAME_FLANGE, ["booleanUnionScope", "booleanSubtractScope"], qUnion(entitiesInBothUnionAndSubtract));
            }
        }

        var subtractBodies = getOwnerSMModel(context, definition.booleanSubtractScope);
        var sheetMetalBodiesQuery = qUnion(concatenateArrays([subtractBodies, unionBodies]));
        const initialData = getInitialEntitiesAndAttributes(context, sheetMetalBodiesQuery);
        sheetMetalBodiesQuery = qUnion([startTracking(context, sheetMetalBodiesQuery), sheetMetalBodiesQuery]);

        // The deripping step breaks these queries otherwise.
        const unionEntityPersistantQuery = qUnion([unionEntityQuery, startTracking(context, unionEntityQuery)]);
        const associateChanges = startTracking(context, qOwnedByBody(sheetMetalBodiesQuery, EntityType.FACE));

        const selectionsByModelId = partitionSheetMetalQueriesByModel(context, unionEntities);
        var index = 0;
        var oneSuccess = false;
        for (var pair in selectionsByModelId)
        {
            oneSuccess = applyTab(context, id + unstableIdComponent(index), definition, qCreatedBy(id + "extract", EntityType.FACE), pair.value, id) || oneSuccess;
            index += 1;
        }

        if (!oneSuccess)
        {
            reportFeatureInfo(context, id, ErrorStringEnum.SHEET_METAL_TAB_NO_EFFECT);
            setErrorEntities(context, id, { "entities" : definition.tabFaces });
        }

        opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : qCreatedBy(id + "extract", EntityType.BODY)
                });

        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, sheetMetalBodiesQuery, initialData, id);

        updateSheetMetalGeometry(context, id, {
                    "entities" : qUnion([toUpdate.modifiedEntities, unionEntityPersistantQuery]),
                    "deletedAttributes" : toUpdate.deletedAttributes,
                    "associatedChanges" : associateChanges });
    }, { booleanSubtractScope : qNothing() });

function applyTab(context is Context, id is Id, definition is map, tabQuery is Query, unionEntities is array, rootId is Id) returns boolean
{
    const tabBodies = evaluateQuery(context, qOwnerBody(tabQuery));
    if (tabBodies == [])
    {
        return false;
    }

    const separatedSubtractQueries = separateSheetMetalQueries(context, definition.booleanSubtractScope);
    const unionQuery = qUnion(unionEntities);

    var oneSuccess = false;
    var index = 0;
    for (var tabBody in tabBodies)
    {
        const tabBodyQuery = qUnion([tabBody]);
        const trackedTabBody = qUnion([tabBodyQuery, startTracking(context, tabBodyQuery)]);
        const tabIndexComponent = unstableIdComponent(index);
        const coincidentWalls = findCoincidentSheetMetalWalls(context, id + tabIndexComponent + "align", trackedTabBody, unionQuery);
        const coincidentGrouping = { "tabBody" : trackedTabBody, "walls" : coincidentWalls };
        const status = booleanOneTabGroup(context, id + tabIndexComponent + "group", definition, coincidentGrouping, separatedSubtractQueries, rootId);
        if (status.statusEnum != ErrorStringEnum.BOOLEAN_UNION_NO_OP)
        {
            oneSuccess = true;
        }
        index += 1;
    }
    return oneSuccess;
}

/**
 * Takes a sheet metal query and returns the results as a map where the keys are sheet metal model ids
 * and the values are arrays of queries.
 */
function partitionSheetMetalQueriesByModel(context is Context, selections is array) returns map
{
    var out = {};
    for (var selection in selections)
    {
        const withTracking = qUnion([selection, startTracking(context, selection)]);
        const id = getActiveSheetMetalId(context, selection);
        const existing = out[id];
        if (existing is undefined)
        {
            out[id] = [withTracking];
        }
        else
        {
            out[id] = append(existing, withTracking);
        }
    }
    return out;
}

/**
 * Locate the sheet metal walls that are coincident with the supplied tab body.
 */
function findCoincidentSheetMetalWalls(context is Context, id is Id, tabBody is Query, unionQuery is Query) returns Query
{
    var coincidentFaces = collectCoincidentFaces(context, tabBody, unionQuery);

    if (size(coincidentFaces) == 0)
    {
        const aligned = tryAlignTabBodyWithOppositeWall(context, id + "offset", tabBody, unionQuery);
        if (aligned)
        {
            coincidentFaces = collectCoincidentFaces(context, tabBody, unionQuery);
        }
    }

    if (size(coincidentFaces) == 0)
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_PARALLEL_WALL, ["tabFaces"], qOwnedByBody(tabBody, EntityType.FACE));
    }

    const coincidentQuery = qUnion(coincidentFaces);
    return qUnion([coincidentQuery, startTracking(context, coincidentQuery)]);
}

/**
 * Collects all sheet metal wall faces that are currently coincident with the supplied tab body.
 */
function collectCoincidentFaces(context is Context, tabBody is Query, unionQuery is Query) returns array
{
    const collisions = evCollision(context, {
                "tools" : qOwnedByBody(tabBody, EntityType.FACE),
                "targets" : unionQuery
            });

    var coincidentFaces = [];
    for (var collision in collisions)
    {
        if (collision["type"] == ClashType.NONE)
        {
            continue;
        }
        coincidentFaces = append(coincidentFaces, collision.target);
    }
    return coincidentFaces;
}

/**
 * Offsets the supplied tab body if it is located on the opposite side of the sheet metal by exactly one thickness.
 */
function tryAlignTabBodyWithOppositeWall(context is Context, id is Id, tabBody is Query, unionQuery is Query) returns boolean
{
    const tabFaces = qOwnedByBody(tabBody, EntityType.FACE);
    var distanceResult = try silent(evDistance(context, { "side0" : tabFaces, "side1" : unionQuery }));
    if (!(distanceResult is DistanceResult))
    {
        return false;
    }

    if (distanceResult.distance <= TOLERANCE.zeroLength * meter)
    {
        return false;
    }

    const modelParameters = try silent(getModelParameters(context, qOwnerBody(unionQuery)));
    if (modelParameters is undefined)
    {
        return false;
    }

    const totalThickness = modelParameters.frontThickness + modelParameters.backThickness;
    if (abs(distanceResult.distance - totalThickness) > TOLERANCE.zeroLength * meter)
    {
        return false;
    }

    const tabFaceArray = evaluateQuery(context, tabFaces);
    if (distanceResult.sides[0].index >= size(tabFaceArray))
    {
        return false;
    }

    const referenceFace = tabFaceArray[distanceResult.sides[0].index];
    const tangentPlane = evFaceTangentPlane(context, {
                "face" : referenceFace,
                "parameter" : distanceResult.sides[0].parameter
            });
    if (tangentPlane is undefined)
    {
        return false;
    }

    var directionVector = distanceResult.sides[1].point - distanceResult.sides[0].point;
    const directionMagnitude = norm(directionVector);
    if (directionMagnitude <= TOLERANCE.zeroLength * meter)
    {
        return false;
    }
    directionVector = directionVector / directionMagnitude;

    const offsetSign = (dot(directionVector, tangentPlane.normal) >= 0) ? 1 : -1;
    const offsetDistance = offsetSign * totalThickness;

    // Use MoveFaceType.OFFSET to correctly handle spline faces and cylinder faces
    // This is the proper approach mentioned in the move face feature
    const moveFaceDefinition = {
                "moveFaces" : tabFaces,
                "moveFaceType" : MoveFaceType.OFFSET,
                "offsetDistance" : offsetDistance,
                "mergeFaces" : true,
                "reFillet" : false
            };

    opOffsetFace(context, id, moveFaceDefinition);

    // After offsetting, use evCollision to check if the surface orientation needs to be flipped.
    // The ClashType will tell us if the tool (tab surface) normal points into or out of the target.
    // ABUT_TOOL_IN_TARGET means normals are opposite (need to flip)
    // ABUT_TOOL_OUT_TARGET means normals are aligned (no flip needed)
    const alignedTabFaces = qOwnedByBody(tabBody, EntityType.FACE);
    const collisions = try silent(evCollision(context, {
                "tools" : alignedTabFaces,
                "targets" : unionQuery
            }));
    
    if (collisions != undefined && size(collisions) > 0)
    {
        // Check collisions to determine orientation - process until we find a definitive clash type
        for (var collision in collisions)
        {
            if (collision["type"] == ClashType.ABUT_TOOL_IN_TARGET)
            {
                // Tool's normal points into target - normals are opposite, need to flip
                opFlipOrientation(context, id + "flip", {
                            "bodies" : tabBody
                        });
                break;
            }
            else if (collision["type"] == ClashType.ABUT_TOOL_OUT_TARGET)
            {
                // Tool's normal points out of target - normals are aligned, no flip needed
                break;
            }
            // For ABUT_NO_CLASS and other types, continue checking remaining collisions
            // as we may find a more specific clash type
        }
    }

    return true;
}

/**
 * Converts tools into sheet bodies.
 */
function createTools(context is Context, id is Id, tools is Query)
{
    const faces = evaluateQuery(context, tools);
    if (faces == [])
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_TAB, ["tabFaces"]);
    }
    if (isAtVersionOrLater(context, FeatureScriptVersionNumber.V696_REMOVE_ADDED_REDUNDANCY))
        opExtractSurface(context, id, { "faces" : tools, "redundancyType" : ExtractSurfaceRedundancyType.REMOVE_ALL_REDUNDANCY });
    else
        opExtractSurface(context, id, { "faces" : tools, "removeRedundant" : true });
}

function reportBooleanIssues(context is Context, id is Id, tabBody is Query, wallFaces is Query)
{
    const collisions = evCollision(context, {
                "tools" : tabBody,
                "targets" : wallFaces
            });

    if (size(collisions) == 0)
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_MERGE, ["tabFaces"], wallFaces);
    }
}

function identifyEdgesForDeripping(context is Context, id is Id, tabBody is Query, partEntities is Query) returns array
{
    try silent
    {
        var edgesForDerip = [];
        const collisions = evCollision(context, {
                    "tools" : qOwnedByBody(tabBody, EntityType.FACE),
                    "targets" : partEntities
                });
        for (var collision in collisions)
        {
            if (collision["type"] != ClashType.ABUT_NO_CLASS)
            {
                edgesForDerip = concatenateArrays([edgesForDerip, getSMDefinitionEntities(context, collision.target, EntityType.EDGE)]);
            }
        }
        return edgesForDerip;
    }
    catch
    {
        return [];
    }
}

function smSubtractTab(context is Context, id is Id, tab is Query, subtractFaces)
{
    if (subtractFaces is undefined)
    {
        return;
    }
    var index = 0;
    for (var face in subtractFaces)
    {
        const targetModelParameters = try silent(getModelParameters(context, qOwnerBody(face)));
        if (targetModelParameters is undefined)
            throw regenError(ErrorStringEnum.REGEN_ERROR);
        const tool = createBooleanToolsForFace(context, id + unstableIdComponent(index) + "tool", face, tab, targetModelParameters);
        if (tool != undefined)
        {
            opBoolean(context, id + unstableIdComponent(index) + "booleanSubtract", {
                        "tools" : qCreatedBy(id + unstableIdComponent(index) + "tool", EntityType.FACE),
                        "targets" : face,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "localizedInFaces" : true,
                        "allowSheets" : true
                    });
        }
        index += 1;
    }

}

function solidSubtractTab(context is Context, id is Id, tab is Query, targets)
{
    if (targets is undefined)
    {
        return;
    }
    try silent(opBoolean(context, id, {
                    "tools" : tab,
                    "targets" : targets,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "allowSheets" : true
                }));
}

/**
 * Given a query for sheet metal model faces. Return a query for all sheet metal part faces corresponding to joints
 * on the edges of the input faces.
 */
function getCorrespondingJointEntitiesInPart(context is Context, selection is Query) returns Query
{
    const evaluatedEdges = evaluateQuery(context, selection->qEdgeTopologyFilter(EdgeTopology.TWO_SIDED));
    var toCollectFaces = [];
    var toCollectEdges = [];
    for (var edge in evaluatedEdges)
    {
            const jointAttributes = getSmObjectTypeAttributes(context, edge, SMObjectType.JOINT);
            if (size(jointAttributes) == 0 ||
                jointAttributes[0].jointType == undefined ||
                jointAttributes[0].jointType.value != SMJointType.TANGENT)
            {
                toCollectFaces = append(toCollectFaces, edge);
            }
            else
            {
                toCollectEdges = append(toCollectEdges, edge);
            }
    }
    const nToFaces = size(toCollectFaces);
    const nToEdges = size(toCollectEdges);
    if (nToFaces == 0 && nToEdges == 0)
    {
        return qNothing();
    }
    const facesQ = (nToFaces == 0) ? qNothing() : getSMCorrespondingInPart(context, qUnion(toCollectFaces), EntityType.FACE);
    const edgesQ = (nToEdges == 0) ? qNothing() : getSMCorrespondingInPart(context, qUnion(toCollectEdges), EntityType.EDGE);
    return qUnion([facesQ, edgesQ]);
}

/**
 * Thicken the input sheet body based on the parameters of the sheet metal model that is the current union target and
 * subtract it from all subtraction targets.
 */
function subtractTab(context is Context, id is Id, definition is map, subtractQueries is map, coincidentGrouping is map, rootId is Id)
{
    const unionBody = qOwnerBody(coincidentGrouping.walls);
    const modelParameters = try silent(getModelParameters(context, unionBody));
    if (modelParameters is undefined)
        throw regenError(ErrorStringEnum.REGEN_ERROR);

    callSubfeatureAndProcessStatus(rootId, opThicken, context, id + "thicken", {
                "entities" : qOwnedByBody(coincidentGrouping.tabBody, EntityType.FACE),
                "thickness1" : modelParameters.frontThickness,
                "thickness2" : modelParameters.backThickness
            });

    const unionPartFaces = getSMCorrespondingInPart(context, coincidentGrouping.walls, EntityType.FACE);
    reportBooleanIssues(context, id + "union", qCreatedBy(id + "thicken", EntityType.BODY), unionPartFaces);

    const corresponding = getCorrespondingJointEntitiesInPart(context, qAdjacent(coincidentGrouping.walls, AdjacencyType.EDGE, EntityType.EDGE));
    const deripCandidates = identifyEdgesForDeripping(context, id + "identify", qCreatedBy(id + "thicken", EntityType.BODY), corresponding);

    if (!deripEdges(context, id + "derip", qUnion(deripCandidates)))
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_BEND, ["booleanUnionScope"]);
    }

    const subtractFaces = qUnion([qOwnedByBody(qEntityFilter(subtractQueries.sheetMetalQueries, EntityType.BODY), EntityType.FACE), qEntityFilter(subtractQueries.sheetMetalQueries, EntityType.FACE)]);
    const unionComplementTracking = startTracking(context, qSubtraction(qOwnedByBody(qOwnerBody(coincidentGrouping.walls), EntityType.FACE), coincidentGrouping.walls));
    var subtractSMFaces = try silent(getSMDefinitionEntities(context, subtractFaces, EntityType.FACE));
    if (subtractSMFaces is undefined)
    {
        subtractSMFaces = [];
    }

    if (size(subtractSMFaces) != 0 || !isQueryEmpty(context, subtractQueries.nonSheetMetalQueries))
    {
        if (definition.booleanOffset > 0 * meter)
        {
            const moveFaceDefinition = {
                    "moveFaces" : qCreatedBy(id + "thicken", EntityType.FACE),
                    "moveFaceType" : MoveFaceType.OFFSET,
                    "offsetDistance" : definition.booleanOffset,
                    "reFillet" : false };

            opOffsetFace(context, id + "move", moveFaceDefinition);
        }
        smSubtractTab(context, id + "sm", qCreatedBy(id + "thicken", EntityType.BODY), subtractSMFaces);
        solidSubtractTab(context, id + "solid", qCreatedBy(id + "thicken", EntityType.BODY), subtractQueries.nonSheetMetalQueries);
    }

    if (modelParameters.minimalClearance > definition.booleanOffset && !isQueryEmpty(context, unionComplementTracking))
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_LOW_CLEARANCE, ["booleanOffset"], getSMCorrespondingInPart(context, unionComplementTracking, EntityType.FACE));
    }

    try silent(opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : qCreatedBy(id + "thicken", EntityType.BODY)
                }));
}

/**
 * Handles the boolean union and subtraction operations for a tab body coincident with the supplied walls.
 */
function booleanOneTabGroup(context is Context, id is Id, definition is map, coincidentGrouping is map, subtractQueries is map, rootId is Id)
{
    const wallBodies = qOwnerBody(coincidentGrouping.walls);

    var cornerBreakTracking;
    const fixCornerBreaks = isAtVersionOrLater(context, FeatureScriptVersionNumber.V723_REMAP_TAB_BREAKS);
    if (fixCornerBreaks)
        cornerBreakTracking = collectCornerBreakTracking(context, wallBodies);

    subtractTab(context, id + "subtract", definition, subtractQueries, coincidentGrouping, rootId);

    opPattern(context, id + "copyTool", {
                "entities" : coincidentGrouping.tabBody,
                "transforms" : [identityTransform()],
                "instanceNames" : ["1"]
            });

    const toolsQ = qCreatedBy(id + "copyTool", EntityType.BODY);
    try
    {
        opBoolean(context, id + "boolean", {
                    "tools" : qUnion([wallBodies, toolsQ]),
                    "operationType" : BooleanOperationType.UNION,
                    "allowSheets" : true
                });
    }
    catch
    {
        const unionComplement = qSubtraction(qOwnedByBody(qOwnerBody(coincidentGrouping.walls), EntityType.FACE), coincidentGrouping.walls);
        const collisions = evCollision(context, {
                    "tools" : toolsQ,
                    "targets" : unionComplement
                });

        var errorGeom = [];
        for (var collision in collisions)
        {
           if (collision['type'] != ClashType.NONE)
           {
              errorGeom = append(errorGeom, collision.tool);
              errorGeom = append(errorGeom, getSMCorrespondingInPart(context, collision.target,  EntityType.FACE));
           }
        }
        if (size(errorGeom) > 0)
        {
           setErrorEntities(context, rootId, { "entities" : qUnion(errorGeom) });
        //   throw regenError(ErrorStringEnum.SHEET_METAL_TAB_COLLISION);
        }
        else
        {
            setErrorEntities(context, rootId, { "entities" : toolsQ});
            throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE);
        }
    }
    try silent(opDeleteBodies(context, id + "unionDelete", { "entities" : qCreatedBy(id + "copyTool", EntityType.BODY) }));

    if (fixCornerBreaks)
        remapCornerBreaks(context, cornerBreakTracking);

    return getFeatureStatus(context, id + "boolean");
}

/**
 * If multiple input faces share the same sheet metal model faces, only return one of those input faces.
 */
function filterSimilarSMFaces(context is Context, faces is Query) returns Query
{
    var filteredOutArray = [];
    const definitionFaceArray = try silent(getSMDefinitionEntities(context, faces, EntityType.FACE));
    if (definitionFaceArray is undefined || size(definitionFaceArray) == 0)
        return qNothing();
    for (var definitionFace in definitionFaceArray)
    {
        const attributes = getSMAssociationAttributes(context, definitionFace);
        if (size(attributes) != 1)
        {
            throw regenError(ErrorStringEnum.REGEN_ERROR);
        }
        const smPartFaces = evaluateQuery(context, qSubtraction(qAttributeFilter(qEverything(EntityType.FACE), attributes[0]), definitionFace));
        if (size(smPartFaces) == 2)
        {
            filteredOutArray = append(filteredOutArray, smPartFaces[0]);
        }
    }
    return qUnion(filteredOutArray);
}

/**
 * Editing logic.
 * Fills in offset distance with minimal gap and finds the default merge scopes.
 * @internal
 */
export function sheetMetalTabEditingLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    specifiedParameters is map, hiddenBodies is Query) returns map
{
    if (definition.tabFaces != oldDefinition.tabFaces && (!specifiedParameters.booleanUnionScope || !specifiedParameters.booleanSubtractScope))
    {
        const faces = evaluateQuery(context, definition.tabFaces);
        if (size(faces) == 0)
        {
            if (!specifiedParameters.booleanUnionScope)
            {
                definition.booleanUnionScope = qNothing();
            }
            if (!specifiedParameters.booleanSubtractScope)
            {
                definition.booleanSubtractScope = qNothing();
            }
            return definition;
        }
        createTools(context, id + "extractHeuristic", definition.tabFaces);
        const wallQuery = qAttributeQuery(asSMAttribute({ "objectType" : SMObjectType.WALL }));
        var entityAssociations = try silent(getSMAssociationAttributes(context, wallQuery));
        var allSMWalls = [];
        if (entityAssociations != undefined && size(entityAssociations) > 0)
        {
            for (var attribute in entityAssociations)
            {
                const visibleFaces = qSubtraction(qEverything(EntityType.FACE), qOwnedByBody(hiddenBodies, EntityType.FACE));
                const associatedEntities = evaluateQuery(context, qSubtraction(qAttributeFilter(visibleFaces, attribute), wallQuery));
                const ownerBody = getOwnerSMModel(context, qUnion(associatedEntities));
                if (ownerBody != [])
                {
                    const isActive = isSheetMetalModelActive(context, ownerBody[0]);
                    if (isActive != undefined && isActive)
                    {
                        allSMWalls = concatenateArrays([allSMWalls, associatedEntities]);
                    }
                }
            }
        }

        var union = [];
        var subtraction = [];
        const collisions = try silent(evCollision(context, {
                    "tools" : qCreatedBy(id + "extractHeuristic", EntityType.BODY),
                    "targets" : qUnion(allSMWalls)
                }));
        if (collisions != undefined)
        {
            for (var collision in collisions)
            {
                if (collision["type"] == ClashType.NONE)
                {
                    continue;
                }
                if (collision["type"] == ClashType.ABUT_NO_CLASS || collision["type"] == ClashType.ABUT_TOOL_IN_TARGET || collision["type"] == ClashType.ABUT_TOOL_OUT_TARGET)
                {
                    union = append(union, collision.target);
                }
                else
                {
                    subtraction = append(subtraction, collision.target);
                }
            }
        }

        if (!specifiedParameters.booleanUnionScope)
        {
            definition.booleanUnionScope = filterSimilarSMFaces(context, qUnion(union));
        }
        if (!specifiedParameters.booleanSubtractScope)
        {
            definition.booleanSubtractScope = filterSimilarSMFaces(context, qUnion(subtraction));
        }
    }
    const sheetMetalBodies = try silent(getOwnerSMModel(context, qOwnerBody(definition.booleanUnionScope)));
    if (sheetMetalBodies is undefined || size(sheetMetalBodies) != 1)
        return definition;
    if (oldDefinition == {} || (tolerantEquals(definition.booleanOffset, 0 * meter) && isQueryEmpty(context, oldDefinition.booleanUnionScope)))
    {
        const modelParameters = try silent(getModelParameters(context, sheetMetalBodies[0]));
        if (!(modelParameters is undefined))
        {
            definition.booleanOffset = modelParameters.minimalClearance;
        }
    }
    return definition;
}
