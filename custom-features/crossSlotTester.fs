// Cross-Slot Generation Tester
// 
// This is a standalone test script to isolate and debug cross-slot generation functionality.
// It takes an arbitrary number of overlapping bodies and generates interlocking slots between them.
// 
// Assumptions:
// - The largest face on each body is the primary face (sheet body assumption)
// - Bodies are overlapping and need slots cut where they intersect
// - No assumptions about normalization or other preprocessing
//
// This script is heavily instrumented with debug information and entity highlighting
// to help diagnose and fix issues with cross-slot generation.
//
// OPTIMIZATION: Batched SUBTRACT_COMPLEMENT Operations
// Instead of running SUBTRACT_COMPLEMENT once per collision pair (which could be 40+ operations),
// this implementation groups bodies into mega-groups where:
//  - Group 1 contains bodies that don't collide with each other
//  - Group 2 contains all their neighbors (bodies that collide with Group 1)
// This allows running SUBTRACT_COMPLEMENT once per group instead of once per pair,
// dramatically reducing computational cost from O(pairs) to O(groups).

FeatureScript 2837;
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/topologyUtils.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");

annotation { "Feature Type Name" : "Cross-Slot Tester" }
export const crossSlotTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Bodies to slot", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 100 }
        definition.bodiesToSlot is Query;
        
        annotation { "Name" : "Show Debug Info" }
        definition.showDebug is boolean;
    }
    {
        println("=== CROSS-SLOT TESTER START ===");
        
        // Evaluate the bodies
        const bodies = evaluateQuery(context, definition.bodiesToSlot);
        println("Number of bodies to slot: " ~ size(bodies));
        
        if (size(bodies) < 2)
        {
            throw regenError("Need at least 2 bodies to generate cross-slots");
        }
        
        // Analyze each body to find its primary face (largest face)
        var bodyInfo = [] as array;
        var bodyCounter = 0;
        for (var body in bodies)
        {
            println("--- Analyzing Body " ~ bodyCounter ~ " ---");
            
            const bodyFaces = qOwnedByBody(body, EntityType.FACE);
            println("  Number of faces: " ~ size(evaluateQuery(context, bodyFaces)));
            
            // Use qLargest to find the largest face by area
            const largestFaceQuery = qLargest(bodyFaces);
            const largestFaceArray = evaluateQuery(context, largestFaceQuery);
            
            if (size(largestFaceArray) > 0)
            {
                const largestFace = largestFaceArray[0];
                
                // Get the plane of the largest face
                try
                {
                    const facePlane = evPlane(context, {
                                "face" : largestFace
                            });
                    println("  Primary face normal: " ~ facePlane.normal);
                    
                    // Highlight the primary face for debugging
                    if (definition.showDebug)
                    {
                        debug(context, largestFace, DebugColor.GREEN);
                    }
                    
                    bodyInfo = append(bodyInfo, {
                                "body" : body,
                                "primaryFace" : largestFace,
                                "primaryPlane" : facePlane,
                                "index" : bodyCounter
                            });
                }
                catch
                {
                    println("  WARNING: Could not get plane for largest face");
                }
            }
            else
            {
                println("  WARNING: No faces found on body");
            }
            
            bodyCounter += 1;
        }
        
        println("Successfully analyzed " ~ size(bodyInfo) ~ " bodies with primary faces");
        
        if (size(bodyInfo) < 2)
        {
            throw regenError("Need at least 2 bodies with planar primary faces");
        }
        
        // Use evCollision to find which bodies actually intersect
        // This is much more efficient than checking all pairs
        println("Detecting collisions between bodies...");
        
        // Build array of body queries
        var bodyQueries = [];
        for (var info in bodyInfo)
        {
            bodyQueries = append(bodyQueries, info.body);
        }
        const allBodies = qUnion(bodyQueries);
        
        const collisions = evCollision(context, {
                    "tools" : allBodies,
                    "targets" : allBodies
                });
        
        // Build a map from body query string to body index for efficient lookup
        var bodyToIndex = {} as map;
        for (var i = 0; i < size(bodyInfo); i += 1)
        {
            bodyToIndex[toString(bodyInfo[i].body)] = i;
        }
        
        // Build collision adjacency data structure
        // collisionNeighbors[bodyIndex] = array of body indices that collide with bodyIndex
        var collisionNeighbors = {} as map;
        for (var bodyIndex = 0; bodyIndex < size(bodyInfo); bodyIndex += 1)
        {
            collisionNeighbors[bodyIndex] = [];
        }
        
        var collidingPairs = {} as map;
        var processedPairs = {} as map;  // Track which pairs we've already added to avoid duplicates
        
        for (var collision in collisions)
        {
            // Skip self-collisions
            if (collision.toolBody == collision.targetBody)
            {
                continue;
            }
            
            const clashType = collision['type'];
            // Only process actual intersections (not just touching)
            if (clashType == ClashType.INTERFERE ||
                clashType == ClashType.TARGET_IN_TOOL ||
                clashType == ClashType.TOOL_IN_TARGET)
            {
                // Look up body indices using the map (O(1) instead of O(n))
                const toolBodyIndex = bodyToIndex[toString(collision.toolBody)];
                const targetBodyIndex = bodyToIndex[toString(collision.targetBody)];
                
                if (toolBodyIndex != undefined && targetBodyIndex != undefined)
                {
                    // Create a canonical pair key (always smaller index first) to avoid duplicates
                    const minIndex = toolBodyIndex < targetBodyIndex ? toolBodyIndex : targetBodyIndex;
                    const maxIndex = toolBodyIndex < targetBodyIndex ? targetBodyIndex : toolBodyIndex;
                    const canonicalPairKey = minIndex ~ "_" ~ maxIndex;
                    
                    // Only process each unique pair once
                    if (processedPairs[canonicalPairKey] == undefined)
                    {
                        processedPairs[canonicalPairKey] = true;
                        
                        // Add to adjacency lists (both directions)
                        collisionNeighbors[toolBodyIndex] = append(collisionNeighbors[toolBodyIndex], targetBodyIndex);
                        collisionNeighbors[targetBodyIndex] = append(collisionNeighbors[targetBodyIndex], toolBodyIndex);
                        
                        // Store collision pair for later lookup (both directions)
                        const pairKey1 = toString(collision.toolBody) ~ "_" ~ toString(collision.targetBody);
                        const pairKey2 = toString(collision.targetBody) ~ "_" ~ toString(collision.toolBody);
                        collidingPairs[pairKey1] = true;
                        collidingPairs[pairKey2] = true;
                    }
                }
            }
        }
        
        println("Found " ~ size(collisions) ~ " collision checks, " ~ size(collidingPairs) ~ " actual intersections");
        
        // Print collision counts for debugging
        for (var bodyIndex = 0; bodyIndex < size(bodyInfo); bodyIndex += 1)
        {
            println("Body " ~ bodyIndex ~ " collides with " ~ size(collisionNeighbors[bodyIndex]) ~ " other bodies");
        }
        
        // OPTIMIZED BATCHING: Group bodies into mega-groups for efficient SUBTRACT_COMPLEMENT operations
        // Algorithm: Start with body with most collisions, place it in Group 1, its neighbors in Group 2
        // Then find bodies that share Group 2 neighbors but don't collide with Group 1, add to Group 1
        // Repeat until all bodies are assigned
        println("=== Grouping bodies for batched operations ===");
        const bodyGroups = computeCollisionGroups(collisionNeighbors, size(bodyInfo));
        println("Created " ~ size(bodyGroups) ~ " body groups for processing");
        
        // Log group details for verification
        for (var gIdx = 0; gIdx < size(bodyGroups); gIdx += 1)
        {
            const group = bodyGroups[gIdx];
            println("Group " ~ gIdx ~ ": Group1=" ~ size(group.group1) ~ " bodies, Group2=" ~ size(group.group2) ~ " bodies");
        }
        
        // BATCHED APPROACH: Collect all split tools per body, then subtract all at once
        // This dramatically reduces boolean operation count
        println("=== Computing intersection geometries ===");
        
        // Maps to collect split tools for each body
        var splitToolsPerBody = {} as map;  // bodyIndex -> array of tool queries
        for (var bodyIndex = 0; bodyIndex < size(bodyInfo); bodyIndex += 1)
        {
            splitToolsPerBody[bodyIndex] = [];
        }
        
        var intersectionCounter = 0;
        var subtractComplementCounter = 0;
        
        // Process each body group
        for (var groupIndex = 0; groupIndex < size(bodyGroups); groupIndex += 1)
        {
            const group = bodyGroups[groupIndex];
            println("--- Processing Group " ~ groupIndex ~ " ---");
            println("  Group 1 (non-colliding): " ~ size(group.group1) ~ " bodies");
            println("  Group 2 (neighbors): " ~ size(group.group2) ~ " bodies");
            
            // For this group, we'll create a single batched intersection geometry
            // by combining all Group 1 bodies and all Group 2 bodies
            if (size(group.group1) > 0 && size(group.group2) > 0)
            {
                // Collect bodies for batch intersection
                var group1Bodies = [] as array;
                var group2Bodies = [] as array;
                
                for (var idx in group.group1)
                {
                    group1Bodies = append(group1Bodies, bodyInfo[idx].body);
                }
                for (var idx in group.group2)
                {
                    group2Bodies = append(group2Bodies, bodyInfo[idx].body);
                }
                
                // Create union queries for batch processing
                const group1Query = qUnion(group1Bodies);
                const group2Query = qUnion(group2Bodies);
                
                println("  Creating batched intersection for " ~ size(group1Bodies) ~ " x " ~ size(group2Bodies) ~ " bodies");
                
                // Perform single SUBTRACT_COMPLEMENT for entire group
                try
                {
                    const batchedIntersection = generateBatchedIntersection(context, id + "group" + groupIndex,
                                                                            group1Query, group2Query, definition.showDebug);
                    subtractComplementCounter += 1;
                    
                    if (!isQueryEmpty(context, batchedIntersection))
                    {
                        println("  Batched intersection created successfully");
                        
                        // Process the batched intersection bodies
                        // These contain all intersection regions for all Group 1 x Group 2 pairs
                        const intersectionCells = evaluateQuery(context, batchedIntersection);
                        println("  Processing " ~ size(intersectionCells) ~ " intersection cells");
                        
                        // Process each intersection cell - split and assign to appropriate bodies
                        var cellIndex = 0;
                        for (var intersectionCell in intersectionCells)
                        {
                            println("    Processing intersection cell " ~ cellIndex);
                            
                            // We need to determine which pair this cell belongs to
                            // For now, we'll iterate through all pairs and split based on their slot direction
                            // This is where the original logic applies - we split each intersection cell
                            
                            // For each Group 1 body, check which Group 2 bodies it collides with
                            for (var g1Idx in group.group1)
                            {
                                const body1Info = bodyInfo[g1Idx];
                                
                                for (var g2Idx in group.group2)
                                {
                                    const body2Info = bodyInfo[g2Idx];
                                    
                                    // Check if these two bodies actually collide
                                    const pairKey1 = toString(body1Info.body) ~ "_" ~ toString(body2Info.body);
                                    const pairKey2 = toString(body2Info.body) ~ "_" ~ toString(body1Info.body);
                                    
                                    if (collidingPairs[pairKey1] == undefined && collidingPairs[pairKey2] == undefined)
                                    {
                                        continue;
                                    }
                                    
                                    // Calculate slot direction for this pair
                                    const normalA = body1Info.primaryPlane.normal;
                                    const normalB = body2Info.primaryPlane.normal;
                                    var pairSlotDirection = cross(normalA, normalB);
                                    
                                    if (norm(pairSlotDirection) > TOLERANCE.zeroLength)
                                    {
                                        pairSlotDirection = normalize(pairSlotDirection);
                                    }
                                    else
                                    {
                                        // Bodies are parallel, use a perpendicular direction
                                        pairSlotDirection = perpendicularVector(normalA);
                                    }
                                    
                                    // Find aligned edges in this intersection cell
                                    var alignedEdges = [] as array;
                                    const bodyEdges = evaluateQuery(context, qGeometry(qOwnedByBody(intersectionCell, EntityType.EDGE), GeometryType.LINE));
                                    
                                    for (var edge in bodyEdges)
                                    {
                                        try
                                        {
                                            const edgeLine = evLine(context, { "edge" : edge });
                                            const alignment = abs(dot(edgeLine.direction, pairSlotDirection));
                                            
                                            if (alignment > 0.9999)
                                            {
                                                const edgeTangent = evEdgeTangentLine(context, {
                                                            "edge" : edge,
                                                            "parameter" : 0.5
                                                        });
                                                alignedEdges = append(alignedEdges, {
                                                            "edge" : edge,
                                                            "position" : edgeTangent.origin
                                                        });
                                            }
                                        }
                                        catch
                                        {
                                            // Edge might not be linear
                                        }
                                    }
                                    
                                    if (size(alignedEdges) == 0)
                                    {
                                        continue; // This cell doesn't match this pair's slot direction
                                    }
                                    
                                    // Calculate split plane at average position along slot direction
                                    var positionAccumulator = 0 * meter;
                                    for (var edgeInfo in alignedEdges)
                                    {
                                        positionAccumulator += dot(edgeInfo.position, pairSlotDirection);
                                    }
                                    const avgPosition = positionAccumulator / size(alignedEdges);
                                    
                                    const splitPlaneOrigin = (pairSlotDirection * avgPosition) / squaredNorm(pairSlotDirection);
                                    const splitPlane = plane(splitPlaneOrigin, pairSlotDirection);
                                    
                                    // Create split plane and split the intersection cell
                                    opPlane(context, id + "splitPlane" + cellIndex, {
                                                "plane" : splitPlane
                                            });
                                    const splitPlaneBody = qCreatedBy(id + "splitPlane" + cellIndex, EntityType.BODY);
                                    
                                    try
                                    {
                                        opSplitPart(context, id + "split" + cellIndex, {
                                                    "targets" : intersectionCell,
                                                    "tool" : splitPlaneBody
                                                });
                                        
                                        const splitBodies = qOwnerBody(qCreatedBy(id + "split" + cellIndex));
                                        
                                        // Assign split halves to appropriate bodies
                                        const toolForBody1 = qFarthestAlong(splitBodies, pairSlotDirection);
                                        const toolForBody2 = qFarthestAlong(splitBodies, -pairSlotDirection);
                                        
                                        if (!isQueryEmpty(context, toolForBody1))
                                        {
                                            splitToolsPerBody[g1Idx] = append(splitToolsPerBody[g1Idx], toolForBody1);
                                        }
                                        
                                        if (!isQueryEmpty(context, toolForBody2))
                                        {
                                            splitToolsPerBody[g2Idx] = append(splitToolsPerBody[g2Idx], toolForBody2);
                                        }
                                        
                                        intersectionCounter += 1;
                                        println("      Split and assigned tools for pair: body " ~ g1Idx ~ " x body " ~ g2Idx);
                                    }
                                    catch (error)
                                    {
                                        println("      WARNING: Could not split intersection cell: " ~ error);
                                    }
                                    
                                    // Clean up split plane
                                    try
                                    {
                                        opDeleteBodies(context, id + "cleanupPlane" ~ cellIndex, {
                                                    "entities" : splitPlaneBody
                                                });
                                    }
                                    catch {}
                                    
                                    // Only process this cell once
                                    break;
                                }
                                
                                // Break outer loop too if we processed this cell
                                if (size(alignedEdges) > 0)
                                {
                                    break;
                                }
                            }
                            
                            cellIndex += 1;
                        }
                        
                        // Note: We don't clean up the batched intersection - it's being used as slot tools
                    }
                }
                catch (error)
                {
                    println("  ERROR creating batched intersection: " ~ error);
                }
            }
        }
        
        println("=== Batched slot subtraction ===");
        println("Processed " ~ intersectionCounter ~ " intersection pairs");
        println("SUBTRACT_COMPLEMENT operations: " ~ subtractComplementCounter ~ " (optimized from " ~ intersectionCounter ~ ")");
        
        // Now perform batched subtraction for each body
        for (var bodyIndex = 0; bodyIndex < size(bodyInfo); bodyIndex += 1)
        {
            const toolArray = splitToolsPerBody[bodyIndex];
            if (size(toolArray) > 0)
            {
                println("Body " ~ bodyIndex ~ ": subtracting " ~ size(toolArray) ~ " slot tools");
                
                // Batch all tools into single subtraction
                const allTools = qUnion(toolArray);
                const targetBody = bodyInfo[bodyIndex].body;
                
                try
                {
                    opBoolean(context, id + "subtractSlots" + bodyIndex, {
                                "tools" : allTools,
                                "targets" : targetBody,
                                "operationType" : BooleanOperationType.SUBTRACTION,
                                "keepTools" : false,
                                "recomputeMatches" : true
                            });
                    println("  Slots cut successfully");
                }
                catch (error)
                {
                    println("  ERROR cutting slots: " ~ error);
                }
            }
        }
        
        println("=== CROSS-SLOT TESTER COMPLETE ===");
    });

// Compute collision groups for batched intersection operations
// Groups bodies such that:
//  - Group 1 contains bodies that don't collide with each other
//  - Group 2 contains all neighbors (bodies that collide with Group 1)
// This allows a single SUBTRACT_COMPLEMENT operation per group instead of per pair
//
// Inputs:
//  - collisionNeighbors: Map of bodyIndex -> array of colliding body indices
//  - totalBodies: Total number of bodies
// Returns: Array of group objects, each with {group1: array, group2: array}
function computeCollisionGroups(collisionNeighbors is map, totalBodies is number) returns array
{
    var groups = [] as array;
    var assignedBodies = {} as map;  // Track which bodies are already in a group
    
    // Initialize all bodies as unassigned
    for (var i = 0; i < totalBodies; i += 1)
    {
        assignedBodies[i] = false;
    }
    
    while (true)
    {
        // Find unassigned body with most collisions
        var maxCollisions = -1;
        var startBodyIndex = -1;
        
        for (var bodyIndex = 0; bodyIndex < totalBodies; bodyIndex += 1)
        {
            if (assignedBodies[bodyIndex] == false)
            {
                const collisionCount = size(collisionNeighbors[bodyIndex]);
                if (collisionCount > maxCollisions)
                {
                    maxCollisions = collisionCount;
                    startBodyIndex = bodyIndex;
                }
            }
        }
        
        // If no unassigned bodies remain, we're done
        if (startBodyIndex == -1)
        {
            break;
        }
        
        // Start a new group with this body
        var group1 = [startBodyIndex];
        var group2 = [] as array;
        
        // Add all neighbors of startBody to group2
        for (var neighborIdx in collisionNeighbors[startBodyIndex])
        {
            if (assignedBodies[neighborIdx] == false)
            {
                group2 = append(group2, neighborIdx);
            }
        }
        
        // Build a set of group1 bodies for fast lookup
        var group1Set = {} as map;
        group1Set[startBodyIndex] = true;
        
        // Iteratively try to add more bodies to group1
        // A body can be added to group1 if:
        //  1. It's not already assigned
        //  2. It shares at least one neighbor with group2
        //  3. It doesn't collide with any body in group1
        var addedNewBody = true;
        while (addedNewBody)
        {
            addedNewBody = false;
            
            // Build set of group2 bodies for fast lookup
            var group2Set = {} as map;
            for (var idx in group2)
            {
                group2Set[idx] = true;
            }
            
            for (var candidateIdx = 0; candidateIdx < totalBodies; candidateIdx += 1)
            {
                if (assignedBodies[candidateIdx] == true || group1Set[candidateIdx] == true || group2Set[candidateIdx] == true)
                {
                    continue;
                }
                
                // Check if candidate shares any neighbors with group2
                var sharesNeighbor = false;
                for (var neighborIdx in collisionNeighbors[candidateIdx])
                {
                    if (group2Set[neighborIdx] == true)
                    {
                        sharesNeighbor = true;
                        break;
                    }
                }
                
                if (!sharesNeighbor)
                {
                    continue;
                }
                
                // Check if candidate collides with any body in group1
                var collidesWithGroup1 = false;
                for (var neighborIdx in collisionNeighbors[candidateIdx])
                {
                    if (group1Set[neighborIdx] == true)
                    {
                        collidesWithGroup1 = true;
                        break;
                    }
                }
                
                if (!collidesWithGroup1)
                {
                    // Add candidate to group1
                    group1 = append(group1, candidateIdx);
                    group1Set[candidateIdx] = true;
                    
                    // Add its neighbors to group2 (if not already there and not in group1)
                    for (var neighborIdx in collisionNeighbors[candidateIdx])
                    {
                        if (group1Set[neighborIdx] != true && group2Set[neighborIdx] != true && assignedBodies[neighborIdx] == false)
                        {
                            group2 = append(group2, neighborIdx);
                            group2Set[neighborIdx] = true;
                        }
                    }
                    
                    addedNewBody = true;
                }
            }
        }
        
        // Mark all bodies in this group as assigned
        for (var idx in group1)
        {
            assignedBodies[idx] = true;
        }
        for (var idx in group2)
        {
            assignedBodies[idx] = true;
        }
        
        // Add this group to the result
        groups = append(groups, {
            "group1" : group1,
            "group2" : group2
        });
    }
    
    return groups;
}

// Generate batched intersection geometry for a group of bodies
// Performs a single SUBTRACT_COMPLEMENT operation for all Group 1 bodies vs all Group 2 bodies
// This is the key optimization that reduces the number of boolean operations
//
// Key insight: Copy GROUP 1 ONLY, use copy as targets, Group 2 as tools
// This preserves original bodies and the intersection results come from the copy operation
//
// Inputs:
//  - group1Query: Query for all bodies in Group 1 (non-colliding with each other)
//  - group2Query: Query for all bodies in Group 2 (neighbors of Group 1)
//  - showDebug: Whether to highlight debug geometry
// Returns: Query for the batched intersection geometry
function generateBatchedIntersection(context is Context, id is Id, group1Query is Query, group2Query is Query, showDebug is boolean) returns Query
{
    println("  Creating copy of Group 1 for batched intersection...");
    
    // ONLY copy Group 1 - this is the key optimization
    // We use the copy as targets in SUBTRACT_COMPLEMENT
    opPattern(context, id + "copyGroup1", {
                "entities" : group1Query,
                "transforms" : [identityTransform()],
                "instanceNames" : ["group1Copy"]
            });
    const copyGroup1 = qCreatedBy(id + "copyGroup1", EntityType.BODY);
    
    if (showDebug)
    {
        debug(context, copyGroup1, DebugColor.BLUE);
        debug(context, group2Query, DebugColor.RED);
    }
    
    println("  Calculating batched intersection geometry...");
    
    // Perform single SUBTRACT_COMPLEMENT for entire group
    // Tools: Group 2 (original bodies)
    // Targets: Copy of Group 1 (will be modified to contain intersection)
    // keepTargets: true preserves the modified target bodies
    try
    {
        opBoolean(context, id + "batchIntersection", {
                    "tools" : group2Query,
                    "targets" : copyGroup1,
                    "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                    "keepTargets" : true,
                    "recomputeMatches" : true
                });
        
        // The copyGroup1 bodies are now modified to contain only the intersection regions
        // These bodies were created by the copy operation AND potentially modified by SUBTRACT_COMPLEMENT
        // We need to query both qCreatedBy (from the pattern) and the result of the operation
        const intersectionBodies = qUnion([
            qCreatedBy(id + "copyGroup1", EntityType.BODY),
            qCreatedBy(id + "batchIntersection", EntityType.BODY)
        ]);
        const intersectionCount = size(evaluateQuery(context, intersectionBodies));
        println("  Batched intersection created " ~ intersectionCount ~ " bodies");
        
        return intersectionBodies;
    }
    catch (error)
    {
        println("  ERROR in batched intersection: " ~ error);
        // Return empty query on error
        return qNothing();
    }
}
