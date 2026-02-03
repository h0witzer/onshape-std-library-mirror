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
export const debugSheetMetalBendReliefs = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
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
        
        // Get the sheet metal model - could be a body, face, or edge selection
        const smSelection = definition.sheetMetalPart;
        
        // Get the owner body to ensure we analyze the whole model
        const smModelBody = qOwnerBody(smSelection);
        const modelBodies = evaluateQuery(context, smModelBody);
        
        if (size(modelBodies) == 0)
        {
            throw regenError("No sheet metal part selected");
        }
        
        println("Selected sheet metal model: " ~ size(modelBodies) ~ " bodies");
        
        // Check what was actually selected
        var selectionType = "body";
        if (size(evaluateQuery(context, qEntityFilter(smSelection, EntityType.FACE))) > 0)
        {
            selectionType = "face";
        }
        else if (size(evaluateQuery(context, qEntityFilter(smSelection, EntityType.EDGE))) > 0)
        {
            selectionType = "edge";
        }
        else if (size(evaluateQuery(context, qEntityFilter(smSelection, EntityType.VERTEX))) > 0)
        {
            selectionType = "vertex";
        }
        println("Selection type: " ~ selectionType);
        
        // Get master/definition entities using the same approach as stitch cut bend
        // getSMDefinitionEntities with 2 parameters returns an array
        // We pass the selection (not owner body) to get the definition entities
        const definitionEntitiesArray = getSMDefinitionEntities(context, smSelection);
        
        if (size(definitionEntitiesArray) == 0)
        {
            println("WARNING: No definition entities found. Trying with owner body...");
            const definitionEntitiesArray2 = getSMDefinitionEntities(context, smModelBody);
            if (size(definitionEntitiesArray2) > 0)
            {
                println("Found " ~ size(definitionEntitiesArray2) ~ " entities via owner body");
            }
        }
        
        // Convert array to query and filter by entity type
        const definitionEntities = qUnion(definitionEntitiesArray);
        const masterEdges = qEntityFilter(definitionEntities, EntityType.EDGE);
        const masterFaces = qEntityFilter(definitionEntities, EntityType.FACE);
        const masterVertices = qEntityFilter(definitionEntities, EntityType.VERTEX);
        
        const masterEdgesEval = evaluateQuery(context, masterEdges);
        const masterFacesEval = evaluateQuery(context, masterFaces);
        const masterVerticesEval = evaluateQuery(context, masterVertices);
        
        println("Master body edges: " ~ size(masterEdgesEval));
        println("Master body faces: " ~ size(masterFacesEval));
        println("Master body vertices: " ~ size(masterVerticesEval));
        
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
                    // Get corner attribute - try multiple approaches
                    const cornerAttr = try silent(getCornerAttribute(context, vertex));
                    const cornerAttrPrimary = cornerInfo.primaryVertex != undefined ? 
                        try silent(getCornerAttribute(context, cornerInfo.primaryVertex)) : undefined;
                    
                    // Check if either the vertex or primaryVertex has corner attributes
                    const hasCornerAttr = (cornerAttr != undefined && cornerAttr.cornerStyle != undefined) ||
                                         (cornerAttrPrimary != undefined && cornerAttrPrimary.cornerStyle != undefined);
                    
                    // Use whichever attribute exists
                    const activeCornerAttr = (cornerAttr != undefined && cornerAttr.cornerStyle != undefined) ? 
                        cornerAttr : cornerAttrPrimary;
                    
                    // Categorize by corner type
                    if (cornerInfo.cornerType == SMCornerType.BEND_END)
                    {
                        bendEndCorners += 1;
                        
                        // Check for bend relief
                        if (hasCornerAttr)
                        {
                            cornersWithReliefs += 1;
                            debug(context, vertex, DebugColor.RED);
                            if (cornerInfo.primaryVertex != undefined && cornerInfo.primaryVertex != vertex)
                            {
                                debug(context, cornerInfo.primaryVertex, DebugColor.RED);
                            }
                            
                            if (definition.showBendReliefDetails)
                            {
                                println("\n  BEND_END corner with relief:");
                                println("    Vertex has attr: " ~ (cornerAttr != undefined && cornerAttr.cornerStyle != undefined));
                                println("    Primary vertex has attr: " ~ (cornerAttrPrimary != undefined && cornerAttrPrimary.cornerStyle != undefined));
                                if (activeCornerAttr != undefined)
                                {
                                    println("    Relief style: " ~ activeCornerAttr.cornerStyle.value);
                                    if (activeCornerAttr.bendReliefScale != undefined)
                                        println("    Relief scale: " ~ activeCornerAttr.bendReliefScale.value);
                                    if (activeCornerAttr.bendReliefDepthScale != undefined)
                                        println("    Relief depth scale: " ~ activeCornerAttr.bendReliefDepthScale.value);
                                    if (activeCornerAttr.bendReliefDepth != undefined)
                                        println("    Relief depth: " ~ activeCornerAttr.bendReliefDepth.value);
                                }
                            }
                        }
                        else
                        {
                            // BEND_END without relief
                            debug(context, vertex, DebugColor.ORANGE);
                            if (cornerInfo.primaryVertex != undefined && cornerInfo.primaryVertex != vertex)
                            {
                                debug(context, cornerInfo.primaryVertex, DebugColor.ORANGE);
                            }
                            
                            if (definition.showBendReliefDetails)
                            {
                                println("\n  BEND_END corner WITHOUT relief:");
                                println("    Vertex has attr: " ~ (cornerAttr != undefined));
                                println("    Primary vertex has attr: " ~ (cornerAttrPrimary != undefined));
                            }
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
