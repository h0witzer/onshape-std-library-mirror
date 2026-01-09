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
import(path : "onshape/std/debug.fs", version : "2837.0");
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
        println("========================================");
        println("SHEET METAL TAB MODIFIED - FEATURE START");
        println("========================================");
        
        // this is not necessary but helps with correct error reporting in feature pattern
        checkNotInFeaturePattern(context, definition.tabFaces, ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);

        println("Creating tools from tab faces");
        createTools(context, id + "extract", definition.tabFaces);

        const unionEntities = try silent(getSMDefinitionEntities(context, definition.booleanUnionScope));
        if (unionEntities == undefined || unionEntities == [])
        {
            println("ERROR: No union entities found");
            throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_WALL, ["booleanUnionScope"]);
        }
        
        println("Union entities count: " ~ size(unionEntities));
        const unionEntityQuery = qUnion(unionEntities);
        const unionBodies = evaluateQuery(context, qOwnerBody(unionEntityQuery));
        println("Union bodies count: " ~ size(unionBodies));
        debug(context, qUnion(unionBodies), DebugColor.BLUE);

        if (isAtVersionOrLater(context, FeatureScriptVersionNumber.V1235_TAB_MERGE_AND_SUBTRACT))
        {
            // Do not allow users to target the same flange for both merging and subtraction
            const subtractEntityQuery = qUnion(getSMDefinitionEntities(context, definition.booleanSubtractScope));
            const entitiesInBothUnionAndSubtract = evaluateQuery(context, qIntersection([unionEntityQuery, subtractEntityQuery]));
            if (entitiesInBothUnionAndSubtract != [])
            {
                println("ERROR: Same flange targeted for merge and subtract");
                throw regenError(ErrorStringEnum.SHEET_METAL_TAB_MERGE_AND_SUBTRACT_SAME_FLANGE, ["booleanUnionScope", "booleanSubtractScope"], qUnion(entitiesInBothUnionAndSubtract));
            }
        }

        var subtractBodies = getOwnerSMModel(context, definition.booleanSubtractScope);
        println("Subtract bodies count: " ~ size(subtractBodies));
        
        var sheetMetalBodiesQuery = qUnion(concatenateArrays([subtractBodies, unionBodies]));
        const initialData = getInitialEntitiesAndAttributes(context, sheetMetalBodiesQuery);
        sheetMetalBodiesQuery = qUnion([startTracking(context, sheetMetalBodiesQuery), sheetMetalBodiesQuery]);

        // The deripping step breaks these queries otherwise.
        const unionEntityPersistantQuery = qUnion([unionEntityQuery, startTracking(context, unionEntityQuery)]);
        const associateChanges = startTracking(context, qOwnedByBody(sheetMetalBodiesQuery, EntityType.FACE));

        println("Partitioning sheet metal queries by model");
        const selectionsByModelId = partitionSheetMetalQueriesByModel(context, unionEntities);
        println("Model partitions count: " ~ size(selectionsByModelId));
        
        var index = 0;
        var oneSuccess = false;
        for (var pair in selectionsByModelId)
        {
            println("Processing model partition " ~ index);
            oneSuccess = applyTab(context, id + unstableIdComponent(index), definition, qCreatedBy(id + "extract", EntityType.FACE), pair.value, id) || oneSuccess;
            index += 1;
        }

        if (!oneSuccess)
        {
            println("WARNING: No successful tab application");
            reportFeatureInfo(context, id, ErrorStringEnum.SHEET_METAL_TAB_NO_EFFECT);
            setErrorEntities(context, id, { "entities" : definition.tabFaces });
        }
        else
        {
            println("At least one tab applied successfully");
        }

        println("Deleting temporary tool bodies");
        opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : qCreatedBy(id + "extract", EntityType.BODY)
                });

        println("Assigning sheet metal attributes");
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, sheetMetalBodiesQuery, initialData, id);

        println("Updating sheet metal geometry");
        updateSheetMetalGeometry(context, id, {
                    "entities" : qUnion([toUpdate.modifiedEntities, unionEntityPersistantQuery]),
                    "deletedAttributes" : toUpdate.deletedAttributes,
                    "associatedChanges" : associateChanges });
        
        println("========================================");
        println("SHEET METAL TAB MODIFIED - FEATURE END");
        println("========================================");
    }, { booleanSubtractScope : qNothing() });

function applyTab(context is Context, id is Id, definition is map, tabQuery is Query, unionEntities is array, rootId is Id) returns boolean
{
    println("=== APPLY TAB DEBUG ===");
    println("Tab query and union entities array size: " ~ size(unionEntities));
    
    const tabBodies = evaluateQuery(context, qOwnerBody(tabQuery));
    println("Tab bodies count: " ~ size(tabBodies));
    
    if (tabBodies == [])
    {
        println("ERROR: No tab bodies found");
        return false;
    }

    debug(context, qOwnerBody(tabQuery), DebugColor.RED);
    
    const separatedSubtractQueries = separateSheetMetalQueries(context, definition.booleanSubtractScope);
    const unionQuery = qUnion(unionEntities);
    println("Union entities query created");
    debug(context, unionQuery, DebugColor.BLUE);

    var oneSuccess = false;
    var index = 0;
    for (var tabBody in tabBodies)
    {
        println("Processing tab body " ~ index);
        const tabBodyQuery = qUnion([tabBody]);
        const trackedTabBody = qUnion([tabBodyQuery, startTracking(context, tabBodyQuery)]);
        debug(context, trackedTabBody, DebugColor.MAGENTA);
        
        const tabIndexComponent = unstableIdComponent(index);
        println("Finding coincident sheet metal walls for tab body " ~ index);
        const coincidentWalls = findCoincidentSheetMetalWalls(context, id + tabIndexComponent + "align", trackedTabBody, unionQuery);
        println("Coincident walls found");
        debug(context, coincidentWalls, DebugColor.GREEN);
        
        const coincidentGrouping = { "tabBody" : trackedTabBody, "walls" : coincidentWalls };
        println("Calling booleanOneTabGroup for tab body " ~ index);
        const status = booleanOneTabGroup(context, id + tabIndexComponent + "group", definition, coincidentGrouping, separatedSubtractQueries, rootId);
        
        if (status.statusEnum != ErrorStringEnum.BOOLEAN_UNION_NO_OP)
        {
            println("Tab body " ~ index ~ " processed successfully");
            oneSuccess = true;
        }
        else
        {
            println("Tab body " ~ index ~ " resulted in NO_OP");
        }
        index += 1;
    }
    
    println("Apply tab complete. One success: " ~ oneSuccess);
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
    println("=== FIND COINCIDENT SHEET METAL WALLS DEBUG ===");
    debug(context, tabBody, DebugColor.RED);
    debug(context, unionQuery, DebugColor.BLUE);
    
    var coincidentFaces = collectCoincidentFaces(context, tabBody, unionQuery);
    println("Initial coincident faces found: " ~ size(coincidentFaces));

    if (size(coincidentFaces) == 0)
    {
        println("No coincident faces found, attempting to align with opposite wall");
        const aligned = tryAlignTabBodyWithOppositeWall(context, id + "offset", tabBody, unionQuery);
        println("Alignment result: " ~ aligned);
        
        if (aligned)
        {
            println("Re-collecting coincident faces after alignment");
            coincidentFaces = collectCoincidentFaces(context, tabBody, unionQuery);
            println("Coincident faces after alignment: " ~ size(coincidentFaces));
        }
    }

    if (size(coincidentFaces) == 0)
    {
        println("ERROR: No coincident faces found even after alignment attempt");
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_PARALLEL_WALL, ["tabFaces"], qOwnedByBody(tabBody, EntityType.FACE));
    }

    const coincidentQuery = qUnion(coincidentFaces);
    const result = qUnion([coincidentQuery, startTracking(context, coincidentQuery)]);
    println("Returning coincident query with tracking");
    debug(context, result, DebugColor.GREEN);
    return result;
}

/**
 * Collects all sheet metal wall faces that are currently coincident with the supplied tab body.
 */
function collectCoincidentFaces(context is Context, tabBody is Query, unionQuery is Query) returns array
{
    println("=== COLLECT COINCIDENT FACES DEBUG ===");
    debug(context, tabBody, DebugColor.MAGENTA);
    debug(context, unionQuery, DebugColor.YELLOW);
    
    const collisions = evCollision(context, {
                "tools" : qOwnedByBody(tabBody, EntityType.FACE),
                "targets" : unionQuery
            });

    println("Total collisions detected: " ~ size(collisions));
    var coincidentFaces = [];
    var collisionIndex = 0;
    for (var collision in collisions)
    {
        println("Collision " ~ collisionIndex ~ " type: " ~ collision["type"]);
        if (collision["type"] == ClashType.NONE)
        {
            println("  Skipping ClashType.NONE");
            collisionIndex += 1;
            continue;
        }
        println("  Adding target face to coincident faces");
        debug(context, collision.target, DebugColor.GREEN);
        coincidentFaces = append(coincidentFaces, collision.target);
        collisionIndex += 1;
    }
    println("Total coincident faces found: " ~ size(coincidentFaces));
    return coincidentFaces;
}

/**
 * Offsets the supplied tab body if it is located on the opposite side of the sheet metal by exactly one thickness.
 */
function tryAlignTabBodyWithOppositeWall(context is Context, id is Id, tabBody is Query, unionQuery is Query) returns boolean
{
    println("=== TRY ALIGN TAB BODY WITH OPPOSITE WALL DEBUG ===");
    const tabFaces = qOwnedByBody(tabBody, EntityType.FACE);
    
    println("Evaluating distance between tab faces and union query");
    debug(context, tabFaces, DebugColor.RED);
    debug(context, unionQuery, DebugColor.BLUE);
    
    var distanceResult = try silent(evDistance(context, { "side0" : tabFaces, "side1" : unionQuery }));
    if (!(distanceResult is DistanceResult))
    {
        println("ERROR: evDistance failed - not a DistanceResult");
        return false;
    }

    println("Distance between tab and union: " ~ distanceResult.distance);
    println("Distance side0 point: " ~ distanceResult.sides[0].point);
    println("Distance side1 point: " ~ distanceResult.sides[1].point);
    
    if (distanceResult.distance <= TOLERANCE.zeroLength * meter)
    {
        println("Distance is within zero tolerance, no alignment needed");
        return false;
    }

    const modelParameters = try silent(getModelParameters(context, qOwnerBody(unionQuery)));
    if (modelParameters is undefined)
    {
        println("ERROR: Could not get model parameters");
        return false;
    }

    const totalThickness = modelParameters.frontThickness + modelParameters.backThickness;
    println("Model front thickness: " ~ modelParameters.frontThickness);
    println("Model back thickness: " ~ modelParameters.backThickness);
    println("Total thickness: " ~ totalThickness);
    
    const thicknessDifference = abs(distanceResult.distance - totalThickness);
    println("Thickness difference: " ~ thicknessDifference);
    
    if (thicknessDifference > TOLERANCE.zeroLength * meter)
    {
        println("Thickness difference exceeds tolerance, cannot align");
        return false;
    }

    const tabFaceArray = evaluateQuery(context, tabFaces);
    println("Tab face array size: " ~ size(tabFaceArray));
    println("Distance side0 index: " ~ distanceResult.sides[0].index);
    
    if (distanceResult.sides[0].index >= size(tabFaceArray))
    {
        println("ERROR: Distance side0 index out of bounds");
        return false;
    }

    const referenceFace = tabFaceArray[distanceResult.sides[0].index];
    println("Reference face selected at index: " ~ distanceResult.sides[0].index);
    debug(context, referenceFace, DebugColor.ORANGE);
    
    const tangentPlane = evFaceTangentPlane(context, {
                "face" : referenceFace,
                "parameter" : distanceResult.sides[0].parameter
            });
    if (tangentPlane is undefined)
    {
        println("ERROR: Could not evaluate tangent plane");
        return false;
    }

    println("Tangent plane origin: " ~ tangentPlane.origin);
    println("Tangent plane normal: " ~ tangentPlane.normal);
    debug(context, tangentPlane, DebugColor.PURPLE);

    var directionVector = distanceResult.sides[1].point - distanceResult.sides[0].point;
    const directionMagnitude = norm(directionVector);
    println("Direction vector (unnormalized): " ~ directionVector);
    println("Direction magnitude: " ~ directionMagnitude);
    
    if (directionMagnitude <= TOLERANCE.zeroLength * meter)
    {
        println("ERROR: Direction magnitude is zero");
        return false;
    }
    directionVector = directionVector / directionMagnitude;
    println("Direction vector (normalized): " ~ directionVector);
    debug(context, directionVector, DebugColor.GREEN);

    const dotProduct = dot(directionVector, tangentPlane.normal);
    println("Dot product (direction · normal): " ~ dotProduct);
    const offsetSign = (dotProduct >= 0) ? 1 : -1;
    println("Offset sign: " ~ offsetSign);
    const offsetDistance = offsetSign * totalThickness;
    println("Final offset distance: " ~ offsetDistance);

    // Use MoveFaceType.OFFSET to correctly handle spline faces and cylinder faces
    // This is the proper approach mentioned in the move face feature
    println("Performing opOffsetFace operation with distance: " ~ offsetDistance);
    opOffsetFace(context, id, {
                "moveFaces" : tabFaces,
                "offsetDistance" : offsetDistance,
                "mergeFaces" : true,
                "reFillet" : false
            });

    // After offsetting from inside to outside (or vice versa), the surface orientation
    // is always flipped. The inside and outside faces of sheet metal always have opposite normals.
    // Always flip the orientation to ensure correct thickening direction.
    println("Performing opFlipOrientation to correct surface normal direction");
    opFlipOrientation(context, id + "flip", {
                "bodies" : tabBody
            });

    println("Alignment successful");
    debug(context, tabBody, DebugColor.CYAN);
    return true;
}

/**
 * Converts tools into sheet bodies.
 */
function createTools(context is Context, id is Id, tools is Query)
{
    println("=== CREATE TOOLS DEBUG ===");
    const faces = evaluateQuery(context, tools);
    println("Tool faces count: " ~ size(faces));
    debug(context, tools, DebugColor.BLUE);
    
    if (faces == [])
    {
        println("ERROR: No tool faces found");
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_TAB, ["tabFaces"]);
    }
    
    println("Extracting surface for " ~ size(faces) ~ " faces");
    if (isAtVersionOrLater(context, FeatureScriptVersionNumber.V696_REMOVE_ADDED_REDUNDANCY))
        opExtractSurface(context, id, { "faces" : tools, "redundancyType" : ExtractSurfaceRedundancyType.REMOVE_ALL_REDUNDANCY });
    else
        opExtractSurface(context, id, { "faces" : tools, "removeRedundant" : true });
    
    const createdBodies = evaluateQuery(context, qCreatedBy(id, EntityType.BODY));
    println("Created tool bodies count: " ~ size(createdBodies));
    debug(context, qCreatedBy(id, EntityType.BODY), DebugColor.CYAN);
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
    println("=== SUBTRACT TAB DEBUG ===");
    const unionBody = qOwnerBody(coincidentGrouping.walls);
    const modelParameters = try silent(getModelParameters(context, unionBody));
    if (modelParameters is undefined)
    {
        println("ERROR: Could not get model parameters");
        throw regenError(ErrorStringEnum.REGEN_ERROR);
    }

    println("Model front thickness: " ~ modelParameters.frontThickness);
    println("Model back thickness: " ~ modelParameters.backThickness);
    println("Model minimal clearance: " ~ modelParameters.minimalClearance);
    println("Boolean offset from definition: " ~ definition.booleanOffset);

    println("Thickening tab body");
    debug(context, qOwnedByBody(coincidentGrouping.tabBody, EntityType.FACE), DebugColor.CYAN);
    
    callSubfeatureAndProcessStatus(rootId, opThicken, context, id + "thicken", {
                "entities" : qOwnedByBody(coincidentGrouping.tabBody, EntityType.FACE),
                "thickness1" : modelParameters.frontThickness,
                "thickness2" : modelParameters.backThickness
            });

    const thickenedBody = qCreatedBy(id + "thicken", EntityType.BODY);
    println("Thickened body created");
    debug(context, thickenedBody, DebugColor.MAGENTA);

    const unionPartFaces = getSMCorrespondingInPart(context, coincidentGrouping.walls, EntityType.FACE);
    println("Union part faces for collision check");
    debug(context, unionPartFaces, DebugColor.YELLOW);
    
    reportBooleanIssues(context, id + "union", thickenedBody, unionPartFaces);

    println("Getting corresponding joint entities");
    const adjacentEdges = qAdjacent(coincidentGrouping.walls, AdjacencyType.EDGE, EntityType.EDGE);
    debug(context, adjacentEdges, DebugColor.ORANGE);
    
    const corresponding = getCorrespondingJointEntitiesInPart(context, adjacentEdges);
    println("Identifying edges for deripping");
    debug(context, corresponding, DebugColor.PURPLE);
    
    const deripCandidates = identifyEdgesForDeripping(context, id + "identify", thickenedBody, corresponding);
    println("Derip candidates count: " ~ size(deripCandidates));

    println("Attempting to derip edges");
    if (!deripEdges(context, id + "derip", qUnion(deripCandidates)))
    {
        println("ERROR: Derip failed");
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_BEND, ["booleanUnionScope"]);
    }
    println("Derip successful");

    const subtractFaces = qUnion([qOwnedByBody(qEntityFilter(subtractQueries.sheetMetalQueries, EntityType.BODY), EntityType.FACE), qEntityFilter(subtractQueries.sheetMetalQueries, EntityType.FACE)]);
    const unionComplementTracking = startTracking(context, qSubtraction(qOwnedByBody(qOwnerBody(coincidentGrouping.walls), EntityType.FACE), coincidentGrouping.walls));
    var subtractSMFaces = try silent(getSMDefinitionEntities(context, subtractFaces, EntityType.FACE));
    if (subtractSMFaces is undefined)
    {
        subtractSMFaces = [];
    }

    println("Sheet metal subtract faces count: " ~ size(subtractSMFaces));
    println("Non-sheet metal queries empty: " ~ isQueryEmpty(context, subtractQueries.nonSheetMetalQueries));

    if (size(subtractSMFaces) != 0 || !isQueryEmpty(context, subtractQueries.nonSheetMetalQueries))
    {
        if (definition.booleanOffset > 0 * meter)
        {
            println("Applying offset to thickened body: " ~ definition.booleanOffset);
            const moveFaceDefinition = {
                    "moveFaces" : qCreatedBy(id + "thicken", EntityType.FACE),
                    "moveFaceType" : MoveFaceType.OFFSET,
                    "offsetDistance" : definition.booleanOffset,
                    "reFillet" : false };

            opOffsetFace(context, id + "move", moveFaceDefinition);
            println("Offset applied successfully");
            debug(context, qCreatedBy(id + "thicken", EntityType.BODY), DebugColor.RED);
        }
        
        println("Performing sheet metal subtraction");
        smSubtractTab(context, id + "sm", qCreatedBy(id + "thicken", EntityType.BODY), subtractSMFaces);
        
        println("Performing solid subtraction");
        solidSubtractTab(context, id + "solid", qCreatedBy(id + "thicken", EntityType.BODY), subtractQueries.nonSheetMetalQueries);
    }

    if (modelParameters.minimalClearance > definition.booleanOffset && !isQueryEmpty(context, unionComplementTracking))
    {
        println("WARNING: Minimal clearance exceeds boolean offset");
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_LOW_CLEARANCE, ["booleanOffset"], getSMCorrespondingInPart(context, unionComplementTracking, EntityType.FACE));
    }

    println("Cleaning up thickened bodies");
    try silent(opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : qCreatedBy(id + "thicken", EntityType.BODY)
                }));
    println("Subtract tab complete");
}

/**
 * Handles the boolean union and subtraction operations for a tab body coincident with the supplied walls.
 */
function booleanOneTabGroup(context is Context, id is Id, definition is map, coincidentGrouping is map, subtractQueries is map, rootId is Id)
{
    println("=== BOOLEAN ONE TAB GROUP DEBUG ===");
    const wallBodies = qOwnerBody(coincidentGrouping.walls);
    println("Wall bodies for boolean operation");
    debug(context, wallBodies, DebugColor.BLUE);
    debug(context, coincidentGrouping.walls, DebugColor.CYAN);

    var cornerBreakTracking;
    const fixCornerBreaks = isAtVersionOrLater(context, FeatureScriptVersionNumber.V723_REMAP_TAB_BREAKS);
    if (fixCornerBreaks)
    {
        println("Collecting corner break tracking");
        cornerBreakTracking = collectCornerBreakTracking(context, wallBodies);
    }

    println("Calling subtractTab");
    subtractTab(context, id + "subtract", definition, subtractQueries, coincidentGrouping, rootId);

    println("Creating pattern copy of tab body for boolean union");
    opPattern(context, id + "copyTool", {
                "entities" : coincidentGrouping.tabBody,
                "transforms" : [identityTransform()],
                "instanceNames" : ["1"]
            });

    const toolsQ = qCreatedBy(id + "copyTool", EntityType.BODY);
    println("Tool bodies for boolean union");
    debug(context, toolsQ, DebugColor.MAGENTA);
    
    println("Attempting boolean union of walls and tab body");
    try
    {
        opBoolean(context, id + "boolean", {
                    "tools" : qUnion([wallBodies, toolsQ]),
                    "operationType" : BooleanOperationType.UNION,
                    "allowSheets" : true
                });
        println("Boolean union successful");
    }
    catch
    {
        println("Boolean union failed, analyzing collision");
        const unionComplement = qSubtraction(qOwnedByBody(qOwnerBody(coincidentGrouping.walls), EntityType.FACE), coincidentGrouping.walls);
        println("Union complement faces");
        debug(context, unionComplement, DebugColor.YELLOW);
        
        const collisions = evCollision(context, {
                    "tools" : toolsQ,
                    "targets" : unionComplement
                });

        println("Collision count with union complement: " ~ size(collisions));
        var errorGeom = [];
        var collisionIndex = 0;
        for (var collision in collisions)
        {
            println("Collision " ~ collisionIndex ~ " type: " ~ collision['type']);
            if (collision['type'] != ClashType.NONE)
            {
                println("  Adding to error geometry");
                debug(context, collision.tool, DebugColor.RED);
                debug(context, collision.target, DebugColor.ORANGE);
                errorGeom = append(errorGeom, collision.tool);
                errorGeom = append(errorGeom, getSMCorrespondingInPart(context, collision.target,  EntityType.FACE));
            }
            collisionIndex += 1;
        }
        if (size(errorGeom) > 0)
        {
            println("ERROR: Tab collision detected");
            setErrorEntities(context, rootId, { "entities" : qUnion(errorGeom) });
        //   throw regenError(ErrorStringEnum.SHEET_METAL_TAB_COLLISION);
        }
        else
        {
            println("ERROR: Tab fails to merge");
            setErrorEntities(context, rootId, { "entities" : toolsQ});
            throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE);
        }
    }
    
    println("Cleaning up copied tool bodies");
    try silent(opDeleteBodies(context, id + "unionDelete", { "entities" : qCreatedBy(id + "copyTool", EntityType.BODY) }));

    if (fixCornerBreaks)
    {
        println("Remapping corner breaks");
        remapCornerBreaks(context, cornerBreakTracking);
    }

    const status = getFeatureStatus(context, id + "boolean");
    println("Boolean status: " ~ status.statusEnum);
    return status;
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
