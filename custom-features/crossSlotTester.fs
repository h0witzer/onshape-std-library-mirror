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

annotation { "Feature Type Name" : "Cross-Slot Tester" }
export const crossSlotTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Bodies to slot", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 100 }
        definition.bodiesToSlot is Query;
        
        annotation { "Name" : "Reference Frame", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
        definition.referenceFrame is Query;
        
        annotation { "Name" : "Show Debug Info" }
        definition.showDebug is boolean;
    }
    {
        println("=== CROSS-SLOT TESTER START ===");
        
        // Get the reference frame
        const referenceFrame = evMateConnector(context, {
                    "mateConnector" : definition.referenceFrame
                });
        println("Reference Frame: origin = " ~ referenceFrame.origin ~ ", zAxis = " ~ referenceFrame.zAxis);
        
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
            
            const bodyFaces = evaluateQuery(context, qOwnedByBody(body, EntityType.FACE));
            println("  Number of faces: " ~ size(bodyFaces));
            
            var largestFace = undefined;
            var largestArea = 0 * meter^2;
            
            for (var face in bodyFaces)
            {
                try
                {
                    const faceArea = evApproximateCentroid(context, {
                                "entities" : face
                            }).mass;
                    
                    if (faceArea > largestArea)
                    {
                        largestArea = faceArea;
                        largestFace = face;
                    }
                }
            }
            
            if (largestFace != undefined)
            {
                println("  Largest face area: " ~ largestArea);
                
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
                        setFeatureComputedParameter(context, id, {
                                    "name" : "body" ~ bodyCounter ~ "PrimaryFace",
                                    "value" : largestFace
                                });
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
            
            bodyCounter += 1;
        }
        
        println("Successfully analyzed " ~ size(bodyInfo) ~ " bodies with primary faces");
        
        if (size(bodyInfo) < 2)
        {
            throw regenError("Need at least 2 bodies with planar primary faces");
        }
        
        // Generate slots for each pair of bodies
        var slotCounter = 0;
        for (var bodyAIndex = 0; bodyAIndex < size(bodyInfo); bodyAIndex += 1)
        {
            for (var bodyBIndex = bodyAIndex + 1; bodyBIndex < size(bodyInfo); bodyBIndex += 1)
            {
                println("--- Generating Slot " ~ slotCounter ~ " (Body " ~ bodyAIndex ~ " x Body " ~ bodyBIndex ~ ") ---");
                
                const bodyA = bodyInfo[bodyAIndex];
                const bodyB = bodyInfo[bodyBIndex];
                
                // Check if the bodies intersect
                const slotId = id + "slot" ~ slotCounter;
                
                try
                {
                    generateSlotForBodyPair(context, slotId, bodyA, bodyB, referenceFrame, definition.showDebug);
                    println("  Slot generated successfully");
                }
                catch (error)
                {
                    println("  ERROR generating slot: " ~ error);
                }
                
                slotCounter += 1;
            }
        }
        
        println("=== CROSS-SLOT TESTER COMPLETE ===");
        println("Total slots attempted: " ~ slotCounter);
    });

// Generate a slot where two bodies intersect
// Inputs:
//  - bodyA, bodyB: Info maps containing body, primaryFace, primaryPlane, index
//  - referenceFrame: Coordinate system for splitting operations
//  - showDebug: Whether to highlight debug geometry
function generateSlotForBodyPair(context is Context, id is Id, bodyA is map, bodyB is map, referenceFrame is CoordSystem, showDebug is boolean)
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
        return;
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
        return;
    }
    
    // Step 4: Find the Z-aligned edges in the intersection
    var zAlignedEdges = [] as array;
    for (var intersectionBody in intersectionBodies)
    {
        const bodyEdges = evaluateQuery(context, qGeometry(qOwnedByBody(intersectionBody, EntityType.EDGE), GeometryType.LINE));
        
        for (var edge in bodyEdges)
        {
            try
            {
                const edgeLine = evLine(context, { "edge" : edge });
                const alignment = abs(dot(edgeLine.direction, referenceFrame.zAxis));
                
                if (alignment > 0.9999)
                {
                    const edgeTangent = evEdgeTangentLine(context, {
                                "edge" : edge,
                                "parameter" : 0.5
                            });
                    zAlignedEdges = append(zAlignedEdges, {
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
    
    println("  Z-aligned edges found: " ~ size(zAlignedEdges));
    
    if (size(zAlignedEdges) == 0)
    {
        println("  No Z-aligned edges found - can't determine split plane");
        opDeleteBodies(context, id + "cleanupCopies", {
                    "entities" : qUnion([copyA, copyB])
                });
        return;
    }
    
    // Step 5: Calculate split plane at the average Z position
    var zAccumulator = 0 * meter;
    for (var edgeInfo in zAlignedEdges)
    {
        zAccumulator += dot(edgeInfo.position, referenceFrame.zAxis);
    }
    const avgZ = zAccumulator / size(zAlignedEdges);
    
    const splitPlaneOrigin = (referenceFrame.zAxis * avgZ) / squaredNorm(referenceFrame.zAxis);
    const splitPlane = plane(splitPlaneOrigin, referenceFrame.zAxis);
    
    println("  Split plane Z position: " ~ avgZ);
    
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
        try
        {
            opSplitPart(context, id + "split" ~ cellIndex, {
                        "targets" : intersectionBody,
                        "tool" : splitPlaneBody
                    });
            
            const splitBodies = qCreatedBy(id + "split" ~ cellIndex, EntityType.BODY);
            
            // Find which split goes to which body
            const upperSplit = qFarthestAlong(splitBodies, referenceFrame.zAxis);
            const lowerSplit = qFarthestAlong(splitBodies, -referenceFrame.zAxis);
            
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
    
    // Step 7: Subtract the split tools from the original bodies
    if (size(splitToolsForA) > 0)
    {
        println("  Subtracting from body A...");
        try
        {
            opBoolean(context, id + "subtractA", {
                        "tools" : qUnion(splitToolsForA),
                        "targets" : bodyA.body,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "recomputeMatches" : true
                    });
            println("  Subtraction from body A successful");
        }
        catch (error)
        {
            println("  ERROR subtracting from body A: " ~ error);
        }
    }
    
    if (size(splitToolsForB) > 0)
    {
        println("  Subtracting from body B...");
        try
        {
            opBoolean(context, id + "subtractB", {
                        "tools" : qUnion(splitToolsForB),
                        "targets" : bodyB.body,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "recomputeMatches" : true
                    });
            println("  Subtraction from body B successful");
        }
        catch (error)
        {
            println("  ERROR subtracting from body B: " ~ error);
        }
    }
    
    // Step 8: Clean up helper geometry
    println("  Cleaning up helper geometry...");
    opDeleteBodies(context, id + "cleanup", {
                "entities" : qUnion([copyA, copyB, splitPlaneBody])
            });
}
