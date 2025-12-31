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
        
        // Build a map of which bodies collide with which
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
                // Store collision (use sorted pair to avoid duplicates)
                const toolIndex = collision.toolBody;
                const targetIndex = collision.targetBody;
                const pairKey = toString(toolIndex) ~ "_" ~ toString(targetIndex);
                collidingPairs[pairKey] = true;
            }
        }
        
        println("Found " ~ size(collisions) ~ " collision checks, " ~ size(collidingPairs) ~ " actual intersections");
        
        // BATCHED APPROACH: Group bodies by collision neighborhoods and perform single SUBTRACT_COMPLEMENT per body
        // This dramatically reduces boolean operation count
        println("=== Computing intersection geometries (BATCHED) ===");
        
        // Build collision graph: map each body to list of its neighbors
        var collisionGraph = {} as map;  // bodyIndex -> array of neighbor indices
        for (var bodyIndex = 0; bodyIndex < size(bodyInfo); bodyIndex += 1)
        {
            collisionGraph[bodyIndex] = [];
        }
        
        // Populate the collision graph
        for (var bodyAIndex = 0; bodyAIndex < size(bodyInfo); bodyAIndex += 1)
        {
            for (var bodyBIndex = bodyAIndex + 1; bodyBIndex < size(bodyInfo); bodyBIndex += 1)
            {
                const bodyA = bodyInfo[bodyAIndex];
                const bodyB = bodyInfo[bodyBIndex];
                
                // Check if this pair was detected as colliding
                const pairKey1 = toString(bodyA.body) ~ "_" ~ toString(bodyB.body);
                const pairKey2 = toString(bodyB.body) ~ "_" ~ toString(bodyA.body);
                
                if (collidingPairs[pairKey1] != undefined || collidingPairs[pairKey2] != undefined)
                {
                    // Add to collision graph (bidirectional)
                    collisionGraph[bodyAIndex] = append(collisionGraph[bodyAIndex], bodyBIndex);
                    collisionGraph[bodyBIndex] = append(collisionGraph[bodyBIndex], bodyAIndex);
                }
            }
        }
        
        // Maps to collect split tools for each body
        var splitToolsPerBody = {} as map;  // bodyIndex -> array of tool queries
        for (var bodyIndex = 0; bodyIndex < size(bodyInfo); bodyIndex += 1)
        {
            splitToolsPerBody[bodyIndex] = [];
        }
        
        var intersectionCounter = 0;
        var batchedOpsCount = 0;
        
        // PHASE 1: BATCHED SUBTRACT_COMPLEMENT - Process each body against ALL its neighbors at once
        for (var bodyIndex = 0; bodyIndex < size(bodyInfo); bodyIndex += 1)
        {
            const neighbors = collisionGraph[bodyIndex];
            if (size(neighbors) == 0)
            {
                continue;  // No collisions for this body
            }
            
            println("--- Batched Processing Body " ~ bodyIndex ~ " against " ~ size(neighbors) ~ " neighbors ---");
            batchedOpsCount += 1;
            
            const bodyInfo_i = bodyInfo[bodyIndex];
            
            // Create ONE copy of this body
            opPattern(context, id + "batchCopy" + bodyIndex, {
                        "entities" : bodyInfo_i.body,
                        "transforms" : [identityTransform()],
                        "instanceNames" : ["batchCopy" ~ bodyIndex]
                    });
            const bodyCopy = qCreatedBy(id + "batchCopy" + bodyIndex, EntityType.BODY);
            
            // Create copies of ALL neighbor bodies
            var neighborCopyQueries = [];
            for (var neighborIdx in neighbors)
            {
                const neighborInfo = bodyInfo[neighborIdx];
                opPattern(context, id + "batchCopy" + bodyIndex + "n" + neighborIdx, {
                            "entities" : neighborInfo.body,
                            "transforms" : [identityTransform()],
                            "instanceNames" : ["neighbor" ~ neighborIdx]
                        });
                const neighborCopy = qCreatedBy(id + "batchCopy" + bodyIndex + "n" + neighborIdx, EntityType.BODY);
                neighborCopyQueries = append(neighborCopyQueries, neighborCopy);
            }
            
            // Single SUBTRACT_COMPLEMENT with ALL neighbors
            const allNeighborCopies = qUnion(neighborCopyQueries);
            println("  Performing single SUBTRACT_COMPLEMENT against " ~ size(neighbors) ~ " neighbors");
            
            try
            {
                opBoolean(context, id + "batchIntersect" + bodyIndex, {
                            "tools" : bodyCopy,
                            "targets" : allNeighborCopies,
                            "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                            "keepTools" : false,
                            "keepTargets" : false
                        });
                
                // Get all intersection bodies created
                const intersectionBodies = evaluateQuery(context, qCreatedBy(id + "batchIntersect" + bodyIndex, EntityType.BODY));
                println("  Created " ~ size(intersectionBodies) ~ " intersection bodies");
                
                // Process each intersection body - need to determine which neighbor it came from
                // and calculate the appropriate slot direction
                for (var intIdx = 0; intIdx < size(intersectionBodies); intIdx += 1)
                {
                    const intersectionBody = intersectionBodies[intIdx];
                    
                    // Try to match this intersection to a neighbor by checking which neighbor it overlaps
                    var matchedNeighborIdx = undefined;
                    for (var neighborIdx in neighbors)
                    {
                        // Check if intersection overlaps with this neighbor
                        const neighborBody = bodyInfo[neighborIdx].body;
                        const testCollisions = evCollision(context, {
                                    "tools" : qUnion([intersectionBody]),
                                    "targets" : neighborBody
                                });
                        
                        if (size(testCollisions) > 0)
                        {
                            matchedNeighborIdx = neighborIdx;
                            break;
                        }
                    }
                    
                    if (matchedNeighborIdx == undefined)
                    {
                        println("  WARNING: Could not match intersection " ~ intIdx ~ " to a neighbor");
                        continue;
                    }
                    
                    println("  Processing intersection " ~ intIdx ~ " (Body " ~ bodyIndex ~ " x Body " ~ matchedNeighborIdx ~ ")");
                    
                    // Calculate slot direction for this specific pair
                    const normalA = bodyInfo_i.primaryPlane.normal;
                    const normalB = bodyInfo[matchedNeighborIdx].primaryPlane.normal;
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
                    
                    // Split this intersection and collect tools
                    try
                    {
                        const splitTools = splitIntersectionBody(context, id + "split" + bodyIndex + "int" + intIdx,
                                                                 qUnion([intersectionBody]), pairSlotDirection, definition.showDebug);
                        
                        // Add split tools to the appropriate bodies' collections
                        if (splitTools.toolForA != undefined)
                        {
                            splitToolsPerBody[bodyIndex] = append(splitToolsPerBody[bodyIndex], splitTools.toolForA);
                        }
                        if (splitTools.toolForB != undefined)
                        {
                            splitToolsPerBody[matchedNeighborIdx] = append(splitToolsPerBody[matchedNeighborIdx], splitTools.toolForB);
                        }
                        
                        intersectionCounter += 1;
                    }
                    catch (error)
                    {
                        println("    ERROR splitting intersection: " ~ error);
                    }
                }
            }
            catch (error)
            {
                println("  ERROR in batched SUBTRACT_COMPLEMENT: " ~ error);
            }
        }
        
        println("=== Batched slot subtraction ===");
        println("Processed " ~ intersectionCounter ~ " intersections using " ~ batchedOpsCount ~ " batched SUBTRACT_COMPLEMENT operations");
        
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

// Split a single intersection body into two halves for slotting
// This is a simplified version used in batched processing
// Returns a map with toolForA and toolForB queries (the split halves)
function splitIntersectionBody(context is Context, id is Id, intersectionBody is Query, slotDirection is Vector, showDebug is boolean) returns map
{
    // Find the slot-direction-aligned edges in the intersection
    var alignedEdges = [] as array;
    
    const intersectionBodiesArray = evaluateQuery(context, intersectionBody);
    for (var body in intersectionBodiesArray)
    {
        const bodyEdges = evaluateQuery(context, qGeometry(qOwnedByBody(body, EntityType.EDGE), GeometryType.LINE));
        
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
    
    if (size(alignedEdges) == 0)
    {
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
    
    // Create split plane and split the intersection body
    opPlane(context, id + "plane", {
                "plane" : splitPlane
            });
    const splitPlaneBody = qCreatedBy(id + "plane", EntityType.BODY);
    
    if (showDebug)
    {
        debug(context, splitPlaneBody, DebugColor.CYAN);
    }
    
    // Split each intersection body
    var splitToolsForA = [] as array;
    var splitToolsForB = [] as array;
    
    var cellIndex = 0;
    for (var intBody in intersectionBodiesArray)
    {
        try
        {
            opSplitPart(context, id + "split" + cellIndex, {
                        "targets" : intBody,
                        "tool" : splitPlaneBody
                    });
            
            const splitBodies = qCreatedBy(id + "split" + cellIndex, EntityType.BODY);
            const splitBodiesArray = evaluateQuery(context, splitBodies);
            
            if (size(splitBodiesArray) > 0)
            {
                // Find which split goes to which body based on slot direction
                const upperSplit = qFarthestAlong(splitBodies, slotDirection);
                const lowerSplit = qFarthestAlong(splitBodies, -slotDirection);
                
                if (!isQueryEmpty(context, upperSplit))
                {
                    splitToolsForA = append(splitToolsForA, upperSplit);
                }
                
                if (!isQueryEmpty(context, lowerSplit))
                {
                    splitToolsForB = append(splitToolsForB, lowerSplit);
                }
            }
            
            cellIndex += 1;
        }
        catch (error)
        {
            // Split failed, skip this body
        }
    }
    
    // Cleanup split plane
    if (!isQueryEmpty(context, splitPlaneBody))
    {
        try
        {
            opDeleteBodies(context, id + "cleanupPlane", {
                        "entities" : splitPlaneBody
                    });
        }
        catch
        {
            // Cleanup failed, not critical
        }
    }
    
    // Return queries for the split tools
    return {
        "toolForA" : size(splitToolsForA) > 0 ? qUnion(splitToolsForA) : undefined,
        "toolForB" : size(splitToolsForB) > 0 ? qUnion(splitToolsForB) : undefined
    };
}
