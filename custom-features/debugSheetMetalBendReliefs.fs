FeatureScript 2878;
// Debug Sheet Metal Bend Reliefs Visualizer
// This feature visualizes bend relief corners and master body entities
// to help understand bend-to-wall placement and interaction.

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2878.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/attributes.fs", version : "2878.0");
export import(path : "onshape/std/smcornertype.gen.fs", version : "2878.0");
export import(path : "onshape/std/smjointtype.gen.fs", version : "2878.0");

/**
 * Debug feature to visualize bend relief corners and sheet metal structure.
 * Helps understand how bend reliefs are placed and interact with walls.
 */
annotation { "Feature Type Name" : "Debug Bend Reliefs" }
export const debugSheetMetalBendReliefs = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sheet metal part",
                    "Filter" : BodyType.SOLID && ActiveSheetMetal.YES,
                    "MaxNumberOfPicks" : 1 }
        definition.sheetMetalPart is Query;
        
        annotation { "Name" : "Show master body edges" }
        definition.showMasterEdges is boolean;
        
        annotation { "Name" : "Show corners" }
        definition.showCorners is boolean;
        
        annotation { "Name" : "Show bend relief details" }
        definition.showBendReliefDetails is boolean;
    }
    {
        println("\n========== DEBUG SHEET METAL BEND RELIEFS ==========");
        
        // Get the sheet metal model
        const smModel = definition.sheetMetalPart;
        const modelBodies = evaluateQuery(context, smModel);
        
        if (size(modelBodies) == 0)
        {
            throw regenError("No sheet metal part selected");
        }
        
        println("Selected sheet metal model: " ~ size(modelBodies) ~ " bodies");
        
        // Get master/definition entities - need to query edges/faces from the solid body
        // getSMDefinitionEntities returns arrays, we need to specify entity type
        const definitionEdgesArray = getSMDefinitionEntities(context, smModel, EntityType.EDGE);
        const definitionFacesArray = getSMDefinitionEntities(context, smModel, EntityType.FACE);
        const definitionVerticesArray = getSMDefinitionEntities(context, smModel, EntityType.VERTEX);
        
        println("Master body edges: " ~ size(definitionEdgesArray));
        println("Master body faces: " ~ size(definitionFacesArray));
        println("Master body vertices: " ~ size(definitionVerticesArray));
        
        // Convert arrays to queries for further processing
        const masterEdges = qUnion(definitionEdgesArray);
        const masterFaces = qUnion(definitionFacesArray);
        const masterVertices = qUnion(definitionVerticesArray);
        
        const masterEdgesEval = evaluateQuery(context, masterEdges);
        const masterFacesEval = evaluateQuery(context, masterFaces);
        const masterVerticesEval = evaluateQuery(context, masterVertices);
        
        // Visualize master body edges in BLUE
        if (definition.showMasterEdges)
        {
            for (var edge in masterEdgesEval)
            {
                debug(context, edge, DebugColor.BLUE);
            }
            println("Highlighted master edges in BLUE");
        }
        
        // Find and categorize edges by joint type
        var bendEdges = [];
        var ripEdges = [];
        var tangentEdges = [];
        var otherEdges = [];
        
        for (var edge in masterEdgesEval)
        {
            const jointAttr = try silent(getJointAttribute(context, edge));
            if (jointAttr != undefined)
            {
                if (jointAttr.jointType != undefined)
                {
                    if (jointAttr.jointType.value == SMJointType.BEND)
                    {
                        bendEdges = append(bendEdges, edge);
                        debug(context, edge, DebugColor.GREEN);
                    }
                    else if (jointAttr.jointType.value == SMJointType.RIP)
                    {
                        ripEdges = append(ripEdges, edge);
                        debug(context, edge, DebugColor.YELLOW);
                    }
                    else if (jointAttr.jointType.value == SMJointType.TANGENT)
                    {
                        tangentEdges = append(tangentEdges, edge);
                    }
                }
            }
        }
        
        println("\nEdge categorization:");
        println("  BEND edges (GREEN): " ~ size(bendEdges));
        println("  RIP edges (YELLOW): " ~ size(ripEdges));
        println("  TANGENT edges: " ~ size(tangentEdges));
        println("  Other edges: " ~ (size(masterEdgesEval) - size(bendEdges) - size(ripEdges) - size(tangentEdges)));
        
        // Find corners with bend relief attributes
        if (definition.showCorners)
        {
            println("\n========== ANALYZING CORNERS ==========");
            
            var cornersWithReliefs = 0;
            var bendEndCorners = 0;
            var openCorners = 0;
            var closedCorners = 0;
            var notCorners = 0;
            
            for (var vertex in masterVerticesEval)
            {
                const cornerInfo = try silent(evCornerType(context, { "vertex" : vertex }));
                
                if (cornerInfo != undefined && cornerInfo.cornerType != SMCornerType.NOT_A_CORNER)
                {
                    // Get corner attribute
                    const cornerAttr = try silent(getCornerAttribute(context, vertex));
                    
                    // Categorize by corner type
                    if (cornerInfo.cornerType == SMCornerType.BEND_END)
                    {
                        bendEndCorners += 1;
                        
                        // Check for bend relief
                        if (cornerAttr != undefined && cornerAttr.cornerStyle != undefined)
                        {
                            cornersWithReliefs += 1;
                            debug(context, vertex, DebugColor.RED);
                            
                            if (definition.showBendReliefDetails)
                            {
                                println("\n  BEND_END corner with relief:");
                                println("    Relief style: " ~ cornerAttr.cornerStyle.value);
                                if (cornerAttr.bendReliefScale != undefined)
                                    println("    Relief scale: " ~ cornerAttr.bendReliefScale.value);
                                if (cornerAttr.bendReliefDepthScale != undefined)
                                    println("    Relief depth scale: " ~ cornerAttr.bendReliefDepthScale.value);
                                if (cornerAttr.bendReliefDepth != undefined)
                                    println("    Relief depth: " ~ cornerAttr.bendReliefDepth.value);
                            }
                        }
                        else
                        {
                            // BEND_END without relief
                            debug(context, vertex, DebugColor.ORANGE);
                        }
                    }
                    else if (cornerInfo.cornerType == SMCornerType.OPEN_CORNER)
                    {
                        openCorners += 1;
                        debug(context, vertex, DebugColor.CYAN);
                    }
                    else if (cornerInfo.cornerType == SMCornerType.CLOSED_CORNER)
                    {
                        closedCorners += 1;
                        debug(context, vertex, DebugColor.MAGENTA);
                    }
                }
                else
                {
                    notCorners += 1;
                }
            }
            
            println("\n========== CORNER SUMMARY ==========");
            println("Total vertices: " ~ size(masterVerticesEval));
            println("  BEND_END corners: " ~ bendEndCorners);
            println("    └─ With bend reliefs (RED): " ~ cornersWithReliefs);
            println("    └─ Without reliefs (ORANGE): " ~ (bendEndCorners - cornersWithReliefs));
            println("  OPEN corners (CYAN): " ~ openCorners);
            println("  CLOSED corners (MAGENTA): " ~ closedCorners);
            println("  NOT_A_CORNER: " ~ notCorners);
        }
        
        // Analyze adjacent edges for bend relief corners
        if (definition.showBendReliefDetails && definition.showCorners)
        {
            println("\n========== BEND RELIEF ADJACENCY ANALYSIS ==========");
            
            for (var vertex in masterVerticesEval)
            {
                const cornerInfo = try silent(evCornerType(context, { "vertex" : vertex }));
                
                if (cornerInfo != undefined && cornerInfo.cornerType == SMCornerType.BEND_END)
                {
                    const cornerAttr = try silent(getCornerAttribute(context, vertex));
                    if (cornerAttr != undefined && cornerAttr.cornerStyle != undefined)
                    {
                        // Get adjacent edges
                        const adjacentEdges = evaluateQuery(context, qAdjacent(vertex, AdjacencyType.VERTEX, EntityType.EDGE));
                        
                        println("\nBEND_END with relief:");
                        println("  Adjacent edges: " ~ size(adjacentEdges));
                        
                        var hasBendEdge = false;
                        var hasRipEdge = false;
                        var hasWallEdge = false;
                        
                        for (var adjEdge in adjacentEdges)
                        {
                            const jointAttr = try silent(getJointAttribute(context, adjEdge));
                            if (jointAttr != undefined && jointAttr.jointType != undefined)
                            {
                                if (jointAttr.jointType.value == SMJointType.BEND)
                                {
                                    hasBendEdge = true;
                                    println("    - BEND edge");
                                }
                                else if (jointAttr.jointType.value == SMJointType.RIP)
                                {
                                    hasRipEdge = true;
                                    println("    - RIP edge");
                                }
                                else if (jointAttr.jointType.value == SMJointType.TANGENT)
                                {
                                    hasWallEdge = true;
                                    println("    - TANGENT (wall) edge");
                                }
                            }
                            else
                            {
                                hasWallEdge = true;
                                println("    - Wall edge (no joint attribute)");
                            }
                        }
                        
                        // Summarize configuration
                        if (hasBendEdge && hasWallEdge && !hasRipEdge)
                        {
                            println("  Configuration: BEND meets WALL (standard relief)");
                        }
                        else if (hasBendEdge && hasRipEdge)
                        {
                            println("  Configuration: BEND meets RIP (unusual)");
                        }
                        else
                        {
                            println("  Configuration: Other");
                        }
                    }
                }
            }
        }
        
        println("\n========== COLOR LEGEND ==========");
        println("Master edges: BLUE");
        println("BEND edges: GREEN");
        println("RIP edges: YELLOW");
        if (definition.showCorners)
        {
            println("BEND_END with relief: RED");
            println("BEND_END without relief: ORANGE");
            println("OPEN corners: CYAN");
            println("CLOSED corners: MAGENTA");
        }
        println("\n========== END DEBUG ==========\n");
    }, {
        showMasterEdges : true,
        showCorners : true,
        showBendReliefDetails : true
    });
