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

        annotation { "Name" : "Enable diagnostic logging", "Default" : false }
        definition.enableDiagnostics is boolean;

        annotation { "Name" : "Log alignment phase", "Default" : true }
        definition.logAlignment is boolean;

        annotation { "Name" : "Log boolean operations", "Default" : true }
        definition.logBooleanOps is boolean;

        annotation { "Name" : "Log tab application", "Default" : true }
        definition.logTabApplication is boolean;
    }
    {
        // this is not necessary but helps with correct error reporting in feature pattern
        checkNotInFeaturePattern(context, definition.tabFaces, ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);

        createTools(context, id + "extract", definition.tabFaces);

        if (definition.enableDiagnostics)
        {
            println("=== TAB FEATURE START ===");
            println("Tab faces query: " ~ definition.tabFaces);
            println("Union scope query: " ~ definition.booleanUnionScope);
            println("Subtract scope query: " ~ definition.booleanSubtractScope);
        }
        
        const unionEntities = try(getSMDefinitionEntities(context, definition.booleanUnionScope));
        if (definition.enableDiagnostics)
        {
            println("Union entities retrieved: " ~ size(unionEntities) ~ " entities");
        }
        if (unionEntities == undefined || unionEntities == [])
        {
            if (definition.enableDiagnostics)
            {
                println("ERROR: No union entities found");
            }
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
        if (definition.enableDiagnostics)
        {
            println("Subtract bodies count: " ~ size(subtractBodies));
        }
        var sheetMetalBodiesQuery = qUnion(concatenateArrays([subtractBodies, unionBodies]));
        const initialData = getInitialEntitiesAndAttributes(context, sheetMetalBodiesQuery);
        sheetMetalBodiesQuery = qUnion([startTracking(context, sheetMetalBodiesQuery), sheetMetalBodiesQuery]);

        // The deripping step breaks these queries otherwise.
        const unionEntityPersistantQuery = qUnion([unionEntityQuery, startTracking(context, unionEntityQuery)]);
        const associateChanges = startTracking(context, qOwnedByBody(sheetMetalBodiesQuery, EntityType.FACE));

        if (definition.enableDiagnostics && definition.logTabApplication)
        {
            println("Partitioning sheet metal queries by model");
        }
        const selectionsByModelId = partitionSheetMetalQueriesByModel(context, unionEntities);
        if (definition.enableDiagnostics && definition.logTabApplication)
        {
            println("Number of sheet metal models: " ~ size(selectionsByModelId));
        }
        var index = 0;
        var oneSuccess = false;
        for (var pair in selectionsByModelId)
        {
            if (definition.enableDiagnostics && definition.logTabApplication)
            {
                println("Processing model " ~ index ~ " with model ID: " ~ pair.key);
                println("Model has " ~ size(pair.value) ~ " union entities");
            }
            oneSuccess = applyTab(context, id + unstableIdComponent(index), definition, qCreatedBy(id + "extract", EntityType.FACE), pair.value, id) || oneSuccess;
            index += 1;
        }

        if (!oneSuccess)
        {
            if (definition.enableDiagnostics)
            {
                println("WARNING: No tabs were successfully applied");
            }
            reportFeatureInfo(context, id, ErrorStringEnum.SHEET_METAL_TAB_NO_EFFECT);
            setErrorEntities(context, id, { "entities" : definition.tabFaces });
        }
        else if (definition.enableDiagnostics)
        {
            println("SUCCESS: At least one tab was applied successfully");
        }

        if (definition.enableDiagnostics)
        {
            println("Deleting extracted tool bodies");
        }
        opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : qCreatedBy(id + "extract", EntityType.BODY)
                });

        if (definition.enableDiagnostics)
        {
            println("Assigning SM attributes to new or split entities");
        }
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, sheetMetalBodiesQuery, initialData, id);

        if (definition.enableDiagnostics)
        {
            println("Updating sheet metal geometry");
        }
        updateSheetMetalGeometry(context, id, {
                    "entities" : qUnion([toUpdate.modifiedEntities, unionEntityPersistantQuery]),
                    "deletedAttributes" : toUpdate.deletedAttributes,
                    "associatedChanges" : associateChanges });
        if (definition.enableDiagnostics)
        {
            println("=== TAB FEATURE COMPLETE ===");
        }
    }, { booleanSubtractScope : qNothing() });

function applyTab(context is Context, id is Id, definition is map, tabQuery is Query, unionEntities is array, rootId is Id) returns boolean
{
    if (definition.enableDiagnostics && definition.logTabApplication)
    {
        println("[applyTab] === Starting applyTab ===");
        println("[applyTab] Union entities count: " ~ size(unionEntities));
    }
    
    const tabBodies = evaluateQuery(context, qOwnerBody(tabQuery));
    if (definition.enableDiagnostics && definition.logTabApplication)
    {
        println("[applyTab] Tab bodies count: " ~ size(tabBodies));
    }
    if (tabBodies == [])
    {
        if (definition.enableDiagnostics && definition.logTabApplication)
        {
            println("[applyTab] No tab bodies found - FAILED");
        }
        return false;
    }

    const separatedSubtractQueries = separateSheetMetalQueries(context, definition.booleanSubtractScope);
    const unionQuery = qUnion(unionEntities);

    var oneSuccess = false;
    var index = 0;
    for (var tabBody in tabBodies)
    {
        if (definition.enableDiagnostics && definition.logTabApplication)
        {
            println("[applyTab] Processing tab body " ~ index);
        }
        const tabBodyQuery = qUnion([tabBody]);
        const trackedTabBody = qUnion([tabBodyQuery, startTracking(context, tabBodyQuery)]);
        const tabIndexComponent = unstableIdComponent(index);
        
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("[applyTab] Finding coincident sheet metal walls");
        }
        const coincidentWalls = findCoincidentSheetMetalWalls(context, id + tabIndexComponent + "align", trackedTabBody, unionQuery, definition);
        if (definition.enableDiagnostics && definition.logTabApplication)
        {
            const wallCount = size(evaluateQuery(context, coincidentWalls));
            println("[applyTab] Found " ~ wallCount ~ " coincident walls");
        }
        
        const coincidentGrouping = { "tabBody" : trackedTabBody, "walls" : coincidentWalls };
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[applyTab] Calling booleanOneTabGroup");
        }
        const status = booleanOneTabGroup(context, id + tabIndexComponent + "group", definition, coincidentGrouping, separatedSubtractQueries, rootId);
        if (definition.enableDiagnostics && definition.logTabApplication)
        {
            println("[applyTab] Status enum: " ~ status.statusEnum);
        }
        
        if (status.statusEnum != ErrorStringEnum.BOOLEAN_UNION_NO_OP)
        {
            if (definition.enableDiagnostics && definition.logTabApplication)
            {
                println("[applyTab] Tab body " ~ index ~ " succeeded");
            }
            oneSuccess = true;
        }
        else if (definition.enableDiagnostics && definition.logTabApplication)
        {
            println("[applyTab] Tab body " ~ index ~ " had no effect (BOOLEAN_UNION_NO_OP)");
        }
        index += 1;
    }
    if (definition.enableDiagnostics && definition.logTabApplication)
    {
        println("[applyTab] Overall success: " ~ oneSuccess);
        println("[applyTab] === applyTab complete ===");
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
function findCoincidentSheetMetalWalls(context is Context, id is Id, tabBody is Query, unionQuery is Query, definition is map) returns Query
{
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("[findCoincidentSheetMetalWalls] === PRE-ALIGNMENT PHASE ===");
        println("[findCoincidentSheetMetalWalls] Searching for initially coincident faces");
    }
    var coincidentFaces = collectCoincidentFaces(context, tabBody, unionQuery, definition);

    if (size(coincidentFaces) == 0)
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("[findCoincidentSheetMetalWalls] === MID-ALIGNMENT PHASE ===");
            println("[findCoincidentSheetMetalWalls] No coincident faces found, attempting alignment with opposite wall");
        }
        const aligned = tryAlignTabBodyWithOppositeWall(context, id + "offset", tabBody, unionQuery, definition);
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("[findCoincidentSheetMetalWalls] Alignment attempt result: " ~ aligned);
        }
        if (aligned)
        {
            if (definition.enableDiagnostics && definition.logAlignment)
            {
                println("[findCoincidentSheetMetalWalls] === POST-ALIGNMENT PHASE ===");
                println("[findCoincidentSheetMetalWalls] Re-collecting coincident faces after alignment");
            }
            coincidentFaces = collectCoincidentFaces(context, tabBody, unionQuery, definition);
        }
    }
    else if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("[findCoincidentSheetMetalWalls] Found " ~ size(coincidentFaces) ~ " coincident faces immediately, no alignment needed");
    }

    if (size(coincidentFaces) == 0)
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("[findCoincidentSheetMetalWalls] ERROR: No coincident faces found even after alignment attempt");
        }
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_PARALLEL_WALL, ["tabFaces"], qOwnedByBody(tabBody, EntityType.FACE));
    }

    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("[findCoincidentSheetMetalWalls] SUCCESS: Found " ~ size(coincidentFaces) ~ " total coincident faces");
    }
    const coincidentQuery = qUnion(coincidentFaces);
    return qUnion([coincidentQuery, startTracking(context, coincidentQuery)]);
}

/**
 * Collects all sheet metal wall faces that are currently coincident with the supplied tab body.
 */
function collectCoincidentFaces(context is Context, tabBody is Query, unionQuery is Query, definition is map) returns array
{
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("  [collectCoincidentFaces] Starting coincident face collection");
        const tabFaces = evaluateQuery(context, qOwnedByBody(tabBody, EntityType.FACE));
        println("  [collectCoincidentFaces] Tab has " ~ size(tabFaces) ~ " faces");
        const unionFaces = evaluateQuery(context, unionQuery);
        println("  [collectCoincidentFaces] Union has " ~ size(unionFaces) ~ " target faces");
    }
    
    const collisions = evCollision(context, {
                "tools" : qOwnedByBody(tabBody, EntityType.FACE),
                "targets" : unionQuery
            });

    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("  [collectCoincidentFaces] evCollision returned " ~ size(collisions) ~ " collision results");
    }
    var coincidentFaces = [];
    for (var collision in collisions)
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("  [collectCoincidentFaces] Collision type: " ~ collision["type"]);
        }
        if (collision["type"] == ClashType.NONE)
        {
            if (definition.enableDiagnostics && definition.logAlignment)
            {
                println("  [collectCoincidentFaces] Skipping NONE collision");
            }
            continue;
        }
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("  [collectCoincidentFaces] Adding coincident face to list");
        }
        coincidentFaces = append(coincidentFaces, collision.target);
    }
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("  [collectCoincidentFaces] Total coincident faces found: " ~ size(coincidentFaces));
    }
    return coincidentFaces;
}

/**
 * Offsets the supplied tab body if it is located on the opposite side of the sheet metal by exactly one thickness.
 */
function tryAlignTabBodyWithOppositeWall(context is Context, id is Id, tabBody is Query, unionQuery is Query, definition is map) returns boolean
{
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Starting alignment attempt");
    }
    const tabFaces = qOwnedByBody(tabBody, EntityType.FACE);
    var distanceResult = try(evDistance(context, { "side0" : tabFaces, "side1" : unionQuery }));
    if (!(distanceResult is DistanceResult))
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("    [tryAlignTabBodyWithOppositeWall] Distance result is not a DistanceResult - FAILED");
        }
        return false;
    }

    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Distance: " ~ distanceResult.distance);
    }
    if (distanceResult.distance <= TOLERANCE.zeroLength * meter)
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("    [tryAlignTabBodyWithOppositeWall] Distance too small (<=zero tolerance) - FAILED");
        }
        return false;
    }

    const modelParameters = try(getModelParameters(context, qOwnerBody(unionQuery)));
    if (modelParameters is undefined)
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("    [tryAlignTabBodyWithOppositeWall] Model parameters undefined - FAILED");
        }
        return false;
    }

    const totalThickness = modelParameters.frontThickness + modelParameters.backThickness;
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Model front thickness: " ~ modelParameters.frontThickness);
        println("    [tryAlignTabBodyWithOppositeWall] Model back thickness: " ~ modelParameters.backThickness);
        println("    [tryAlignTabBodyWithOppositeWall] Total thickness: " ~ totalThickness);
    }
    
    const thicknessDifference = abs(distanceResult.distance - totalThickness);
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Thickness difference: " ~ thicknessDifference);
        println("    [tryAlignTabBodyWithOppositeWall] Zero tolerance: " ~ (TOLERANCE.zeroLength * meter));
    }
    if (thicknessDifference > TOLERANCE.zeroLength * meter)
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("    [tryAlignTabBodyWithOppositeWall] Distance does not match thickness - FAILED");
        }
        return false;
    }

    const tabFaceArray = evaluateQuery(context, tabFaces);
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Tab face array size: " ~ size(tabFaceArray));
        println("    [tryAlignTabBodyWithOppositeWall] Distance result side0 index: " ~ distanceResult.sides[0].index);
    }
    if (distanceResult.sides[0].index >= size(tabFaceArray))
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("    [tryAlignTabBodyWithOppositeWall] Side index out of bounds - FAILED");
        }
        return false;
    }

    const referenceFace = tabFaceArray[distanceResult.sides[0].index];
    const tangentPlane = evFaceTangentPlane(context, {
                "face" : referenceFace,
                "parameter" : distanceResult.sides[0].parameter
            });
    if (tangentPlane is undefined)
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("    [tryAlignTabBodyWithOppositeWall] Tangent plane undefined - FAILED");
        }
        return false;
    }
    
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Tangent plane normal: " ~ tangentPlane.normal);
    }

    var directionVector = distanceResult.sides[1].point - distanceResult.sides[0].point;
    const directionMagnitude = norm(directionVector);
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Direction magnitude: " ~ directionMagnitude);
    }
    if (directionMagnitude <= TOLERANCE.zeroLength * meter)
    {
        if (definition.enableDiagnostics && definition.logAlignment)
        {
            println("    [tryAlignTabBodyWithOppositeWall] Direction magnitude too small - FAILED");
        }
        return false;
    }
    directionVector = directionVector / directionMagnitude;
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Normalized direction vector: " ~ directionVector);
    }

    const dotProduct = dot(directionVector, tangentPlane.normal);
    const offsetSign = (dotProduct >= 0) ? 1 : -1;
    const offsetDistance = offsetSign * totalThickness;
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Dot product: " ~ dotProduct);
        println("    [tryAlignTabBodyWithOppositeWall] Offset sign: " ~ offsetSign);
        println("    [tryAlignTabBodyWithOppositeWall] Offset distance: " ~ offsetDistance);
    }

    // Use MoveFaceType.OFFSET to correctly handle spline faces and cylinder faces
    // This is the proper approach mentioned in the move face feature
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Executing opOffsetFace");
    }
    opOffsetFace(context, id, {
                "moveFaces" : tabFaces,
                "offsetDistance" : offsetDistance,
                "mergeFaces" : true,
                "reFillet" : false
            });
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] opOffsetFace completed successfully");
    }

    // After offsetting from inside to outside (or vice versa), the surface orientation
    // is always flipped. The inside and outside faces of sheet metal always have opposite normals.
    // Always flip the orientation to ensure correct thickening direction.
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] Executing opFlipOrientation");
    }
    opFlipOrientation(context, id + "flip", {
                "bodies" : tabBody
            });
    if (definition.enableDiagnostics && definition.logAlignment)
    {
        println("    [tryAlignTabBodyWithOppositeWall] opFlipOrientation completed successfully");
        println("    [tryAlignTabBodyWithOppositeWall] Alignment SUCCESS");
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

function identifyEdgesForDeripping(context is Context, id is Id, tabBody is Query, partEntities is Query, definition is map) returns array
{
    try
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[identifyEdgesForDeripping] Identifying edges for deripping");
        }
        var edgesForDerip = [];
        const collisions = evCollision(context, {
                    "tools" : qOwnedByBody(tabBody, EntityType.FACE),
                    "targets" : partEntities
                });
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[identifyEdgesForDeripping] Found " ~ size(collisions) ~ " collisions");
        }
        for (var collision in collisions)
        {
            if (collision["type"] != ClashType.ABUT_NO_CLASS)
            {
                const smEdges = getSMDefinitionEntities(context, collision.target, EntityType.EDGE);
                if (definition.enableDiagnostics && definition.logBooleanOps)
                {
                    println("[identifyEdgesForDeripping] Adding " ~ size(smEdges) ~ " edges from collision target");
                }
                edgesForDerip = concatenateArrays([edgesForDerip, smEdges]);
            }
        }
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[identifyEdgesForDeripping] Total edges for derip: " ~ size(edgesForDerip));
        }
        return edgesForDerip;
    }
    catch
    {
        return [];
    }
}

function smSubtractTab(context is Context, id is Id, tab is Query, subtractFaces, definition is map)
{
    if (subtractFaces is undefined)
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[smSubtractTab] No subtract faces provided, skipping");
        }
        return;
    }
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[smSubtractTab] Subtracting tab from " ~ size(subtractFaces) ~ " faces");
    }
    var index = 0;
    for (var face in subtractFaces)
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[smSubtractTab] Processing face " ~ index);
        }
        const targetModelParameters = try(getModelParameters(context, qOwnerBody(face)));
        if (targetModelParameters is undefined)
        {
            if (definition.enableDiagnostics && definition.logBooleanOps)
            {
                println("[smSubtractTab] ERROR: Cannot get model parameters");
            }
            throw regenError(ErrorStringEnum.REGEN_ERROR);
        }
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[smSubtractTab] Creating boolean tool for face");
        }
        const tool = createBooleanToolsForFace(context, id + unstableIdComponent(index) + "tool", face, tab, targetModelParameters);
        if (tool != undefined)
        {
            if (definition.enableDiagnostics && definition.logBooleanOps)
            {
                println("[smSubtractTab] Executing boolean subtraction");
            }
            opBoolean(context, id + unstableIdComponent(index) + "booleanSubtract", {
                        "tools" : qCreatedBy(id + unstableIdComponent(index) + "tool", EntityType.FACE),
                        "targets" : face,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "localizedInFaces" : true,
                        "allowSheets" : true
                    });
            if (definition.enableDiagnostics && definition.logBooleanOps)
            {
                println("[smSubtractTab] Boolean subtraction completed");
            }
        }
        else if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[smSubtractTab] Tool creation returned undefined, skipping");
        }
        index += 1;
    }
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[smSubtractTab] Subtraction complete");
    }
}

function solidSubtractTab(context is Context, id is Id, tab is Query, targets, definition is map)
{
    if (targets is undefined)
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[solidSubtractTab] No targets provided, skipping");
        }
        return;
    }
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[solidSubtractTab] Executing solid boolean subtraction");
    }
    try(opBoolean(context, id, {
                    "tools" : tab,
                    "targets" : targets,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "allowSheets" : true
                }));
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[solidSubtractTab] Solid subtraction completed");
    }
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
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] === Starting subtractTab routine ===");
    }
    const unionBody = qOwnerBody(coincidentGrouping.walls);
    const modelParameters = try(getModelParameters(context, unionBody));
    if (modelParameters is undefined)
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[subtractTab] ERROR: Model parameters undefined");
        }
        throw regenError(ErrorStringEnum.REGEN_ERROR);
    }
    
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] Front thickness: " ~ modelParameters.frontThickness);
        println("[subtractTab] Back thickness: " ~ modelParameters.backThickness);
        println("[subtractTab] Calling opThicken");
    }

    callSubfeatureAndProcessStatus(rootId, opThicken, context, id + "thicken", {
                "entities" : qOwnedByBody(coincidentGrouping.tabBody, EntityType.FACE),
                "thickness1" : modelParameters.frontThickness,
                "thickness2" : modelParameters.backThickness
            });
    
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] opThicken completed");
    }

    const unionPartFaces = getSMCorrespondingInPart(context, coincidentGrouping.walls, EntityType.FACE);
    reportBooleanIssues(context, id + "union", qCreatedBy(id + "thicken", EntityType.BODY), unionPartFaces);

    const corresponding = getCorrespondingJointEntitiesInPart(context, qAdjacent(coincidentGrouping.walls, AdjacencyType.EDGE, EntityType.EDGE));
    const deripCandidates = identifyEdgesForDeripping(context, id + "identify", qCreatedBy(id + "thicken", EntityType.BODY), corresponding, definition);

    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] Attempting to derip " ~ size(deripCandidates) ~ " edges");
    }
    if (!deripEdges(context, id + "derip", qUnion(deripCandidates)))
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[subtractTab] ERROR: Deripping failed");
        }
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_NO_BEND, ["booleanUnionScope"]);
    }
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] Deripping successful");
    }

    const subtractFaces = qUnion([qOwnedByBody(qEntityFilter(subtractQueries.sheetMetalQueries, EntityType.BODY), EntityType.FACE), qEntityFilter(subtractQueries.sheetMetalQueries, EntityType.FACE)]);
    const unionComplementTracking = startTracking(context, qSubtraction(qOwnedByBody(qOwnerBody(coincidentGrouping.walls), EntityType.FACE), coincidentGrouping.walls));
    var subtractSMFaces = try(getSMDefinitionEntities(context, subtractFaces, EntityType.FACE));
    if (subtractSMFaces is undefined)
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[subtractTab] No SM subtract faces found");
        }
        subtractSMFaces = [];
    }
    else if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] Found " ~ size(subtractSMFaces) ~ " SM subtract faces");
    }

    if (size(subtractSMFaces) != 0 || !isQueryEmpty(context, subtractQueries.nonSheetMetalQueries))
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[subtractTab] Boolean offset: " ~ definition.booleanOffset);
        }
        if (definition.booleanOffset > 0 * meter)
        {
            if (definition.enableDiagnostics && definition.logBooleanOps)
            {
                println("[subtractTab] Applying offset to thickened tab");
            }
            const moveFaceDefinition = {
                    "moveFaces" : qCreatedBy(id + "thicken", EntityType.FACE),
                    "moveFaceType" : MoveFaceType.OFFSET,
                    "offsetDistance" : definition.booleanOffset,
                    "reFillet" : false };

            opOffsetFace(context, id + "move", moveFaceDefinition);
            if (definition.enableDiagnostics && definition.logBooleanOps)
            {
                println("[subtractTab] Offset applied");
            }
        }
        smSubtractTab(context, id + "sm", qCreatedBy(id + "thicken", EntityType.BODY), subtractSMFaces, definition);
        solidSubtractTab(context, id + "solid", qCreatedBy(id + "thicken", EntityType.BODY), subtractQueries.nonSheetMetalQueries, definition);
    }
    else if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] No subtraction needed");
    }

    if (modelParameters.minimalClearance > definition.booleanOffset && !isQueryEmpty(context, unionComplementTracking))
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[subtractTab] WARNING: Low clearance detected");
        }
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_LOW_CLEARANCE, ["booleanOffset"], getSMCorrespondingInPart(context, unionComplementTracking, EntityType.FACE));
    }

    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] Deleting thickened bodies");
    }
    try(opDeleteBodies(context, id + "deleteBodies", {
                    "entities" : qCreatedBy(id + "thicken", EntityType.BODY)
                }));
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[subtractTab] === subtractTab complete ===");
    }
}

/**
 * Handles the boolean union and subtraction operations for a tab body coincident with the supplied walls.
 */
function booleanOneTabGroup(context is Context, id is Id, definition is map, coincidentGrouping is map, subtractQueries is map, rootId is Id)
{
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[booleanOneTabGroup] === Starting boolean operations for tab group ===");
    }
    const wallBodies = qOwnerBody(coincidentGrouping.walls);

    var cornerBreakTracking;
    const fixCornerBreaks = isAtVersionOrLater(context, FeatureScriptVersionNumber.V723_REMAP_TAB_BREAKS);
    if (fixCornerBreaks)
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[booleanOneTabGroup] Collecting corner break tracking");
        }
        cornerBreakTracking = collectCornerBreakTracking(context, wallBodies);
    }

    subtractTab(context, id + "subtract", definition, subtractQueries, coincidentGrouping, rootId);

    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[booleanOneTabGroup] Creating tool pattern copy");
    }
    opPattern(context, id + "copyTool", {
                "entities" : coincidentGrouping.tabBody,
                "transforms" : [identityTransform()],
                "instanceNames" : ["1"]
            });

    const toolsQ = qCreatedBy(id + "copyTool", EntityType.BODY);
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        const toolCount = size(evaluateQuery(context, toolsQ));
        println("[booleanOneTabGroup] Tool count: " ~ toolCount);
        println("[booleanOneTabGroup] Attempting boolean union");
    }
    try
    {
        opBoolean(context, id + "boolean", {
                    "tools" : qUnion([wallBodies, toolsQ]),
                    "operationType" : BooleanOperationType.UNION,
                    "allowSheets" : true
                });
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[booleanOneTabGroup] Boolean union completed successfully");
        }
    }
    catch
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[booleanOneTabGroup] Boolean union failed, analyzing collision");
        }
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
    
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[booleanOneTabGroup] Deleting tool copy bodies");
    }
    try(opDeleteBodies(context, id + "unionDelete", { "entities" : qCreatedBy(id + "copyTool", EntityType.BODY) }));

    if (fixCornerBreaks)
    {
        if (definition.enableDiagnostics && definition.logBooleanOps)
        {
            println("[booleanOneTabGroup] Remapping corner breaks");
        }
        remapCornerBreaks(context, cornerBreakTracking);
    }

    const status = getFeatureStatus(context, id + "boolean");
    if (definition.enableDiagnostics && definition.logBooleanOps)
    {
        println("[booleanOneTabGroup] Boolean status: " ~ status);
        println("[booleanOneTabGroup] === booleanOneTabGroup complete ===");
    }
    return status;
}

/**
 * If multiple input faces share the same sheet metal model faces, only return one of those input faces.
 */
function filterSimilarSMFaces(context is Context, faces is Query) returns Query
{
    var filteredOutArray = [];
    const definitionFaceArray = try(getSMDefinitionEntities(context, faces, EntityType.FACE));
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
        var entityAssociations = try(getSMAssociationAttributes(context, wallQuery));
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
        const collisions = try(evCollision(context, {
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
    const sheetMetalBodies = try(getOwnerSMModel(context, qOwnerBody(definition.booleanUnionScope)));
    if (sheetMetalBodies is undefined || size(sheetMetalBodies) != 1)
        return definition;
    if (oldDefinition == {} || (tolerantEquals(definition.booleanOffset, 0 * meter) && isQueryEmpty(context, oldDefinition.booleanUnionScope)))
    {
        const modelParameters = try(getModelParameters(context, sheetMetalBodies[0]));
        if (!(modelParameters is undefined))
        {
            definition.booleanOffset = modelParameters.minimalClearance;
        }
    }
    return definition;
}
