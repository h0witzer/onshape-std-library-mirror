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
        
        // Build collision adjacency data structure
        // collisionNeighbors[bodyIndex] = array of body indices that collide with bodyIndex
        var collisionNeighbors = {} as map;
        for (var bodyIndex = 0; bodyIndex < size(bodyInfo); bodyIndex += 1)
        {
            collisionNeighbors[bodyIndex] = [];
        }
        
        var collidingPairs = {} as map;
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
                // Find the body indices for these bodies
                var toolBodyIndex = -1;
                var targetBodyIndex = -1;
                for (var i = 0; i < size(bodyInfo); i += 1)
                {
                    if (toString(bodyInfo[i].body) == toString(collision.toolBody))
                    {
                        toolBodyIndex = i;
                    }
                    if (toString(bodyInfo[i].body) == toString(collision.targetBody))
                    {
                        targetBodyIndex = i;
                    }
                }
                
                if (toolBodyIndex != -1 && targetBodyIndex != -1)
                {
                    // Add to adjacency lists
                    collisionNeighbors[toolBodyIndex] = append(collisionNeighbors[toolBodyIndex], targetBodyIndex);
                    collisionNeighbors[targetBodyIndex] = append(collisionNeighbors[targetBodyIndex], toolBodyIndex);
                    
                    // Store collision pair for later lookup
                    const pairKey = toString(collision.toolBody) ~ "_" ~ toString(collision.targetBody);
                    collidingPairs[pairKey] = true;
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
                    
                    if (batchedIntersection != undefined && !isQueryEmpty(context, batchedIntersection))
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
// Inputs:
//  - group1Query: Query for all bodies in Group 1 (non-colliding with each other)
//  - group2Query: Query for all bodies in Group 2 (neighbors of Group 1)
//  - showDebug: Whether to highlight debug geometry
// Returns: Query for the batched intersection geometry, or undefined if failed
function generateBatchedIntersection(context is Context, id is Id, group1Query is Query, group2Query is Query, showDebug is boolean) returns Query
{
    println("  Creating copies for batched intersection...");
    
    // Create copies of both groups
    opPattern(context, id + "copyGroup1", {
                "entities" : group1Query,
                "transforms" : [identityTransform()],
                "instanceNames" : ["group1Copy"]
            });
    const copyGroup1 = qCreatedBy(id + "copyGroup1", EntityType.BODY);
    
    opPattern(context, id + "copyGroup2", {
                "entities" : group2Query,
                "transforms" : [identityTransform()],
                "instanceNames" : ["group2Copy"]
            });
    const copyGroup2 = qCreatedBy(id + "copyGroup2", EntityType.BODY);
    
    if (showDebug)
    {
        debug(context, copyGroup1, DebugColor.BLUE);
        debug(context, copyGroup2, DebugColor.RED);
    }
    
    println("  Calculating batched intersection geometry...");
    
    // Perform single SUBTRACT_COMPLEMENT for entire group
    try
    {
        opBoolean(context, id + "batchIntersection", {
                    "tools" : copyGroup1,
                    "targets" : copyGroup2,
                    "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                    "keepTargets" : true,
                    "recomputeMatches" : true
                });
        
        const intersectionBodies = qCreatedBy(id + "batchIntersection", EntityType.BODY);
        const intersectionCount = size(evaluateQuery(context, intersectionBodies));
        println("  Batched intersection created " ~ intersectionCount ~ " bodies");
        
        if (intersectionCount > 0)
        {
            return intersectionBodies;
        }
        else
        {
            return undefined;
        }
    }
    catch (error)
    {
        println("  ERROR in batched intersection: " ~ error);
        return undefined;
    }
}

// Extract split tools from a batched intersection for a specific body pair
// This function takes the batched intersection and extracts/splits the relevant portion for one collision pair
// Unlike generateIntersectionSplitTools, this doesn't create new copies or run SUBTRACT_COMPLEMENT
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
    // 1. Copy bodyA and bodyB
    // 2. Intersect each with the batched intersection to get their specific overlap regions
    // 3. Split those regions as normal
    
    println("    Extracting intersection for specific pair");
    
    // Create temporary copies of bodyA and bodyB to intersect with batched result
    opPattern(context, id + "copyA", {
                "entities" : bodyA.body,
                "transforms" : [identityTransform()],
                "instanceNames" : ["pairCopyA"]
            });
    const copyA = qCreatedBy(id + "copyA", EntityType.BODY);
    
    opPattern(context, id + "copyB", {
                "entities" : bodyB.body,
                "transforms" : [identityTransform()],
                "instanceNames" : ["pairCopyB"]
            });
    const copyB = qCreatedBy(id + "copyB", EntityType.BODY);
    
    // Intersect copyA with batched intersection to get the portion that belongs to this pair
    try
    {
        opBoolean(context, id + "extractA", {
                    "tools" : copyA,
                    "targets" : batchedIntersection,
                    "operationType" : BooleanOperationType.INTERSECTION,
                    "keepTools" : false,
                    "recomputeMatches" : true
                });
    }
    catch (error)
    {
        println("    WARNING: Could not extract intersection for body A: " ~ error);
        // Clean up
        try { opDeleteBodies(context, id + "cleanup", { "entities" : qUnion([copyA, copyB]) }); } catch {}
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // Similarly intersect copyB with batched intersection
    try
    {
        opBoolean(context, id + "extractB", {
                    "tools" : copyB,
                    "targets" : batchedIntersection,
                    "operationType" : BooleanOperationType.INTERSECTION,
                    "keepTools" : false,
                    "recomputeMatches" : true
                });
    }
    catch (error)
    {
        println("    WARNING: Could not extract intersection for body B: " ~ error);
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // Now intersect the two extracted regions to get the actual pair intersection
    const extractedA = qCreatedBy(id + "extractA", EntityType.BODY);
    const extractedB = qCreatedBy(id + "extractB", EntityType.BODY);
    
    // Check if we got valid extractions
    if (isQueryEmpty(context, extractedA) || isQueryEmpty(context, extractedB))
    {
        println("    No overlap found for this specific pair");
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // Find the actual intersection between the two extractions
    try
    {
        opBoolean(context, id + "pairIntersect", {
                    "tools" : extractedA,
                    "targets" : extractedB,
                    "operationType" : BooleanOperationType.INTERSECTION,
                    "keepTools" : false,
                    "keepTargets" : true,
                    "recomputeMatches" : true
                });
    }
    catch (error)
    {
        println("    ERROR finding pair intersection: " ~ error);
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    const pairIntersection = qCreatedBy(id + "pairIntersect", EntityType.BODY);
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

// Generate intersection and split tools for a body pair
// Returns a map with toolForA and toolForB queries (the split halves to subtract from each body)
// Inputs:
//  - bodyA, bodyB: Info maps containing body, primaryFace, primaryPlane, index
//  - slotDirection: Vector indicating the direction for splitting the slot
//  - showDebug: Whether to highlight debug geometry
function generateIntersectionSplitTools(context is Context, id is Id, bodyA is map, bodyB is map, slotDirection is Vector, showDebug is boolean) returns map
{
    println("  Creating copies for intersection calculation...");
    
    // Step 1: Create copies of both bodies
    opPattern(context, id + "copyA", {
                "entities" : bodyA.body,
                "transforms" : [identityTransform()],
                "instanceNames" : ["copyA"]
            });
    const copyA = qCreatedBy(id + "copyA", EntityType.BODY);
    
    opPattern(context, id + "copyB", {
                "entities" : bodyB.body,
                "transforms" : [identityTransform()],
                "instanceNames" : ["copyB"]
            });
    const copyB = qCreatedBy(id + "copyB", EntityType.BODY);
    
    if (showDebug)
    {
        debug(context, copyA, DebugColor.BLUE);
        debug(context, copyB, DebugColor.RED);
    }
    
    println("  Calculating intersection geometry...");
    
    // Step 2: Find intersection using SUBTRACT_COMPLEMENT
    try
    {
        opBoolean(context, id + "intersection", {
                    "tools" : copyA,
                    "targets" : copyB,
                    "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                    "keepTargets" : true,
                    "recomputeMatches" : true
                });
    }
    catch (error)
    {
        println("  ERROR in intersection boolean: " ~ error);
        // Clean up
        opDeleteBodies(context, id + "cleanupCopies", {
                    "entities" : qUnion([copyA, copyB])
                });
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // Step 3: Check if intersection exists
    const intersectionBodies = evaluateQuery(context, copyB);
    println("  Intersection bodies found: " ~ size(intersectionBodies));
    
    if (size(intersectionBodies) == 0)
    {
        println("  No intersection found - bodies don't overlap");
        opDeleteBodies(context, id + "cleanupCopies", {
                    "entities" : qUnion([copyA, copyB])
                });
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // Step 4: Find the slot-direction-aligned edges in the intersection
    var alignedEdges = [] as array;
    for (var intersectionBody in intersectionBodies)
    {
        // Check body type for debugging
        const bodyType = evaluateQuery(context, qBodyType(intersectionBody, BodyType.SOLID));
        if (size(bodyType) > 0)
        {
            println("  Intersection body is SOLID");
        }
        else
        {
            println("  Intersection body is SHEET/SURFACE (not solid)");
        }
        
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
    
    println("  Slot-direction-aligned edges found: " ~ size(alignedEdges));
    
    if (size(alignedEdges) == 0)
    {
        println("  No aligned edges found - can't determine split plane");
        opDeleteBodies(context, id + "cleanupCopies", {
                    "entities" : qUnion([copyA, copyB])
                });
        return { "toolForA" : undefined, "toolForB" : undefined };
    }
    
    // Step 5: Calculate split plane at the average position along slot direction
    var positionAccumulator = 0 * meter;
    for (var edgeInfo in alignedEdges)
    {
        positionAccumulator += dot(edgeInfo.position, slotDirection);
    }
    const avgPosition = positionAccumulator / size(alignedEdges);
    
    const splitPlaneOrigin = (slotDirection * avgPosition) / squaredNorm(slotDirection);
    const splitPlane = plane(splitPlaneOrigin, slotDirection);
    
    println("  Split plane position along slot direction: " ~ avgPosition);
    
    // Step 6: Create split plane and split the intersection bodies
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
    
    for (var intersectionBody in intersectionBodies)
    {
        println("  Attempting to split intersection body " ~ cellIndex);
        
        // Debug: Check if we can even evaluate the intersection body
        try
        {
            const intersectionBodyArray = evaluateQuery(context, intersectionBody);
            println("  Intersection body evaluates to " ~ size(intersectionBodyArray) ~ " entity/entities");
        }
        catch (error)
        {
            println("  ERROR evaluating intersection body: " ~ error);
        }
        
        try
        {
            // NOTE: opSplitPart only works on solid bodies, not sheet bodies
            // If the intersection is a sheet body, this will create 0 bodies
            opSplitPart(context, id + "split" + cellIndex, {
                        "targets" : intersectionBody,
                        "tool" : splitPlaneBody
                    });
            
            // Try using qOwnerBody like waffleIt does
            const splitBodiesViaOwnerBody = qOwnerBody(qCreatedBy(id + "split" + cellIndex));
            const splitBodiesViaOwnerBodyArray = evaluateQuery(context, splitBodiesViaOwnerBody);
            println("  Split via qOwnerBody: " ~ size(splitBodiesViaOwnerBodyArray) ~ " bodies");
            
            const splitBodies = qCreatedBy(id + "split" + cellIndex, EntityType.BODY);
            const splitBodiesArray = evaluateQuery(context, splitBodies);
            println("  Split via qCreatedBy: " ~ size(splitBodiesArray) ~ " bodies");
            
            if (size(splitBodiesArray) == 0 && size(splitBodiesViaOwnerBodyArray) == 0)
            {
                println("  WARNING: Split created no bodies");
                println("  Possible causes:");
                println("    - Split plane doesn't intersect the body");
                println("    - Body is a sheet/surface (opSplitPart requires solid)");
                println("    - Geometric issue with split operation");
            }
            
            // Use whichever query found bodies
            const splitBodiesToUse = size(splitBodiesViaOwnerBodyArray) > 0 ? splitBodiesViaOwnerBody : splitBodies;
            
            // Find which split goes to which body based on slot direction
            const upperSplit = qFarthestAlong(splitBodiesToUse, slotDirection);
            const lowerSplit = qFarthestAlong(splitBodiesToUse, -slotDirection);
            
            println("  Upper split empty: " ~ isQueryEmpty(context, upperSplit));
            println("  Lower split empty: " ~ isQueryEmpty(context, lowerSplit));
            
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
            
            cellIndex += 1;
        }
        catch (error)
        {
            println("  ERROR splitting intersection body " ~ cellIndex ~ ": " ~ error);
        }
    }
    
    println("  Split tools for body A: " ~ size(splitToolsForA));
    println("  Split tools for body B: " ~ size(splitToolsForB));
    
    // Return the split tools for batched subtraction later
    // Don't delete the copies - they are the tools we'll use for subtraction
    // Only delete the split plane
    if (!isQueryEmpty(context, splitPlaneBody))
    {
        try
        {
            opDeleteBodies(context, id + "cleanupPlane", {
                        "entities" : splitPlaneBody
                    });
        }
        catch (error)
        {
            println("  WARNING: Plane cleanup failed: " ~ error);
        }
    }
    
    // Return queries for the split tools
    return {
        "toolForA" : size(splitToolsForA) > 0 ? qUnion(splitToolsForA) : undefined,
        "toolForB" : size(splitToolsForB) > 0 ? qUnion(splitToolsForB) : undefined
    };
}
