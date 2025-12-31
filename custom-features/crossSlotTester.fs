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
                        
                        // Now process individual pairs within the group to split the batched intersection
                        for (var aIdx in group.group1)
                        {
                            for (var bIdx in group.group2)
                            {
                                const bodyA = bodyInfo[aIdx];
                                const bodyB = bodyInfo[bIdx];
                                
                                // Check if this pair actually collides
                                const pairKey1 = toString(bodyA.body) ~ "_" ~ toString(bodyB.body);
                                const pairKey2 = toString(bodyB.body) ~ "_" ~ toString(bodyA.body);
                                
                                if (collidingPairs[pairKey1] == undefined && collidingPairs[pairKey2] == undefined)
                                {
                                    continue;
                                }
                                
                                println("  Processing collision pair " ~ aIdx ~ " x " ~ bIdx);
                                
                                // Calculate slot direction for this specific pair
                                const normalA = bodyA.primaryPlane.normal;
                                const normalB = bodyB.primaryPlane.normal;
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
                                
                                // Extract split tools from the batched intersection for this specific pair
                                // This extracts the intersection region between bodyA and bodyB from the mega-intersection
                                try
                                {
                                    const splitTools = extractPairSplitTools(context, 
                                                                            id + "pair" + intersectionCounter,
                                                                            batchedIntersection,
                                                                            bodyA, bodyB, 
                                                                            pairSlotDirection, 
                                                                            definition.showDebug);
                                    
                                    // Add split tools to the appropriate bodies' collections
                                    if (splitTools.toolForA != undefined)
                                    {
                                        splitToolsPerBody[aIdx] = append(splitToolsPerBody[aIdx], splitTools.toolForA);
                                    }
                                    if (splitTools.toolForB != undefined)
                                    {
                                        splitToolsPerBody[bIdx] = append(splitToolsPerBody[bIdx], splitTools.toolForB);
                                    }
                                }
                                catch (error)
                                {
                                    println("  ERROR extracting split tools: " ~ error);
                                }
                                
                                intersectionCounter += 1;
                            }
                        }
                        
                        // Clean up the batched intersection bodies after processing all pairs
                        try
                        {
                            opDeleteBodies(context, id + "cleanupBatch" + groupIndex, {
                                        "entities" : batchedIntersection
                                    });
                            println("  Cleaned up batched intersection bodies");
                        }
                        catch (error)
                        {
                            println("  WARNING: Could not clean up batched intersection: " ~ error);
                        }
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
    // Tools: Group 2 (original bodies, will be kept)
    // Targets: Copy of Group 1 (will be modified to contain intersection)
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
        // These bodies were created by the copy operation above
        const intersectionBodies = copyGroup1;
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

// Extract split tools from a batched intersection for a specific body pair
// This function takes the batched intersection and extracts/splits the relevant portion for one collision pair
// Uses SUBTRACT_COMPLEMENT for efficient extraction
//
// Inputs:
//  - batchedIntersection: Query for the batched intersection geometry (already computed)
//  - bodyA, bodyB: Info maps containing body, primaryFace, primaryPlane, index
//  - slotDirection: Vector indicating the direction for splitting the slot
//  - showDebug: Whether to highlight debug geometry
// Returns: Map with toolForA and toolForB queries
function extractPairSplitTools(context is Context, id is Id, batchedIntersection is Query,
                               bodyA is map, bodyB is map, slotDirection is Vector, showDebug is boolean) returns map
{
    // To extract the intersection specific to bodyA and bodyB from the batched intersection:
    // 1. Use SUBTRACT_COMPLEMENT with bodyA as tool and batchedIntersection as target
    //    This gives us the portions of batched intersection that overlap with bodyA
    // 2. Use SUBTRACT_COMPLEMENT with bodyB as tool on that result
    //    This gives us the intersection region specific to this pair
    // 3. Split that region along the slot direction
    
    println("    Extracting intersection for specific pair");
    
    // Copy the batched intersection once for this pair
    opPattern(context, id + "batchCopy", {
                "entities" : batchedIntersection,
                "transforms" : [identityTransform()],
                "instanceNames" : ["pairBatch"]
            });
    const batchCopy = qCreatedBy(id + "batchCopy", EntityType.BODY);
    
    // First SUBTRACT_COMPLEMENT: extract portions overlapping bodyA
    try
    {
        opBoolean(context, id + "extractA", {
                    "tools" : bodyA.body,
                    "targets" : batchCopy,
                    "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                    "keepTargets" : true,
                    "recomputeMatches" : true
                });
    }
    catch (error)
    {
        println("    WARNING: Could not extract intersection for body A: " ~ error);
        try { opDeleteBodies(context, id + "cleanup", { "entities" : batchCopy }); } catch {}
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // batchCopy now contains only the parts that overlap with bodyA
    // Second SUBTRACT_COMPLEMENT: extract portions overlapping bodyB
    try
    {
        opBoolean(context, id + "extractB", {
                    "tools" : bodyB.body,
                    "targets" : batchCopy,
                    "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                    "keepTargets" : true,
                    "recomputeMatches" : true
                });
    }
    catch (error)
    {
        println("    WARNING: Could not extract intersection for body B: " ~ error);
        try { opDeleteBodies(context, id + "cleanup", { "entities" : batchCopy }); } catch {}
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // batchCopy now contains only the intersection region for this specific pair
    const pairIntersection = batchCopy;
    const pairIntersectionBodies = evaluateQuery(context, pairIntersection);
    
    if (size(pairIntersectionBodies) == 0)
    {
        println("    No intersection found for this pair");
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    println("    Pair intersection: " ~ size(pairIntersectionBodies) ~ " bodies");
    
    // Now split the pair intersection as in the original function
    // Find the slot-direction-aligned edges
    var alignedEdges = [] as array;
    for (var intersectionBody in pairIntersectionBodies)
    {
        const bodyEdges = evaluateQuery(context, qGeometry(qOwnedByBody(intersectionBody, EntityType.EDGE), GeometryType.LINE));
        
        for (var edge in bodyEdges)
        {
            try
            {
                const edgeLine = evLine(context, { "edge" : edge });
                const alignment = abs(dot(edgeLine.direction, slotDirection));
                
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
                    
                    if (showDebug)
                    {
                        debug(context, edge, DebugColor.YELLOW);
                    }
                }
            }
            catch
            {
                // Edge might not be linear
            }
        }
    }
    
    println("    Slot-direction-aligned edges found: " ~ size(alignedEdges));
    
    if (size(alignedEdges) == 0)
    {
        println("    No aligned edges found - can't determine split plane");
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // Calculate split plane at the average position along slot direction
    var positionAccumulator = 0 * meter;
    for (var edgeInfo in alignedEdges)
    {
        positionAccumulator += dot(edgeInfo.position, slotDirection);
    }
    const avgPosition = positionAccumulator / size(alignedEdges);
    
    const splitPlaneOrigin = (slotDirection * avgPosition) / squaredNorm(slotDirection);
    const splitPlane = plane(splitPlaneOrigin, slotDirection);
    
    // Create split plane and split the intersection bodies
    opPlane(context, id + "splitPlane", {
                "plane" : splitPlane
            });
    const splitPlaneBody = qCreatedBy(id + "splitPlane", EntityType.BODY);
    
    if (showDebug)
    {
        debug(context, splitPlaneBody, DebugColor.CYAN);
    }
    
    // Split each intersection body
    var splitToolsForA = [] as array;
    var splitToolsForB = [] as array;
    var cellIndex = 0;
    
    for (var intersectionBody in pairIntersectionBodies)
    {
        try
        {
            opSplitPart(context, id + "split" + cellIndex, {
                        "targets" : intersectionBody,
                        "tool" : splitPlaneBody
                    });
            
            const splitBodies = qOwnerBody(qCreatedBy(id + "split" + cellIndex));
            const splitBodiesArray = evaluateQuery(context, splitBodies);
            
            if (size(splitBodiesArray) > 0)
            {
                // Find which split goes to which body based on slot direction
                const upperSplit = qFarthestAlong(splitBodies, slotDirection);
                const lowerSplit = qFarthestAlong(splitBodies, -slotDirection);
                
                if (!isQueryEmpty(context, upperSplit))
                {
                    splitToolsForA = append(splitToolsForA, upperSplit);
                    if (showDebug)
                    {
                        debug(context, upperSplit, DebugColor.MAGENTA);
                    }
                }
                
                if (!isQueryEmpty(context, lowerSplit))
                {
                    splitToolsForB = append(splitToolsForB, lowerSplit);
                    if (showDebug)
                    {
                        debug(context, lowerSplit, DebugColor.ORANGE);
                    }
                }
            }
            
            cellIndex += 1;
        }
        catch (error)
        {
            println("    ERROR splitting intersection body: " ~ error);
        }
    }
    
    // Clean up the split plane
    if (!isQueryEmpty(context, splitPlaneBody))
    {
        try
        {
            opDeleteBodies(context, id + "cleanupPlane", {
                        "entities" : splitPlaneBody
                    });
        }
        catch {}
    }
    
    println("    Split tools for body A: " ~ size(splitToolsForA));
    println("    Split tools for body B: " ~ size(splitToolsForB));
    
    // Return queries for the split tools
    return {
        "toolForA" : size(splitToolsForA) > 0 ? qUnion(splitToolsForA) : undefined,
        "toolForB" : size(splitToolsForB) > 0 ? qUnion(splitToolsForB) : undefined
    };
}
