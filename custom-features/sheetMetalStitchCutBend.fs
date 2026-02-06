FeatureScript 2856;
// Sheet Metal Stitch Cut Bend Feature
// This feature combines logic from Modify Joint and Tab and Slot to create stitch cut bends.
// It allows changing any joint style into a stitch bend by splitting the edge into segments
// that are alternately assigned bend or rip attributes.

// Imports used in interface - enums must be exported for use in preconditions
export import(path : "onshape/std/smjointtype.gen.fs", version : "2856.0");
export import(path : "onshape/std/smjointstyle.gen.fs", version : "2856.0");

// Imports used internally
import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/smreliefstyle.gen.fs", version : "2856.0");
import(path : "onshape/std/query.fs", version : "2856.0");
import(path : "onshape/std/evaluate.fs", version : "2856.0");
import(path : "onshape/std/valueBounds.fs", version : "2856.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2856.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2856.0");
import(path : "onshape/std/feature.fs", version : "2856.0");
import(path : "onshape/std/attributes.fs", version : "2856.0");
import(path : "onshape/std/containers.fs", version : "2856.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2856.0");
import(path : "onshape/std/math.fs", version : "2856.0");
import(path : "onshape/std/modifyFillet.fs", version : "2856.0");
import(path : "onshape/std/string.fs", version : "2856.0");
import(path : "onshape/std/path.fs", version : "2856.0");
import(path : "onshape/std/geomOperations.fs", version : "2856.0");
import(path : "onshape/std/debug.fs", version : "2856.0");

// Import spacing utilities for bridge/stitch spacing logic
export import(path : "c51f6558b7346f455a634ff5/cf14633de6fca78124306ce9/8ce820287d75ed2e92412d90", version : "cf26b6d26aa41f8853237904"); // spacingUtils.fs

const EDGE_LENGTH_TOLERANCE = 1e-9 * meter;
const EDGE_PARAMETER_TOLERANCE = 1e-6;
const FRACTION_TOLERANCE = 1e-9;
const DEFAULT_BEND_RELIEF_DEPTH_SCALE = 1.5; // Default depth scale when not specified in model

export const BRIDGE_WIDTH_BOUNDS =
{
    (inch) : [1e-6, 0.25, 1e6]
} as LengthBoundSpec;

/**
 * Sheet Metal Stitch Cut Bend feature.
 * Modifies sheet metal joints by splitting edges into alternating bend/rip segments.
 * Bridges are the bend segments (connections), and stitches are the rip segments (cuts).
 * Uses spacing logic from Tab and Slot to control bridge placement and sizing.
 */
annotation { "Feature Type Name" : "Stitch cut bend",
        "Filter Selector" : "allparts",
        "Editing Logic Function" : "sheetMetalStitchCutBendEditLogic" }
export const sheetMetalStitchCutBend = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Joint edge selection - allows multiple edges for multi-edge processing
        annotation { "Name" : "Joint edges",
                    "Filter" : (SheetMetalDefinitionEntityType.FACE || SheetMetalDefinitionEntityType.EDGE) && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES }
        definition.entity is Query;

        // Bend parameters for bridge segments
        annotation { "Name" : "Use model bend radius", "Default" : true }
        definition.useDefaultRadius is boolean;
        if (!definition.useDefaultRadius)
        {
            annotation { "Name" : "Bend radius" }
            isLength(definition.radius, SM_BEND_RADIUS_BOUNDS);
        }
        annotation { "Name" : "Use model K Factor", "Default" : true }
        definition.useDefaultKFactor is boolean;
        if (!definition.useDefaultKFactor)
        {
            annotation { "Name" : "K Factor" }
            isReal(definition.kFactor, K_FACTOR_BOUNDS);
        }

        // Spacing parameters - bridge width is the width of each bend segment (connection)
        annotation { "Name" : "Bridge width" }
        isLength(definition.bridgeWidth, BRIDGE_WIDTH_BOUNDS);

        // Use centralized spacing predicate from spacingUtils
        curvePatternSpacingPredicate(definition);
        
        // Debug visualization
        annotation { "Name" : "Show debug entities", "Default" : true }
        definition.showDebug is boolean;
        
        // Relief edge retraction distance
        annotation { "Name" : "Relief edge retraction distance", "Default" : 0.1 * inch }
        isLength(definition.reliefEdgeRetraction, LENGTH_BOUNDS);
    }
    {
        // Validate sheet metal context
        checkNotInFeaturePattern(context, definition.entity, ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);

        if (!areEntitiesFromSingleActiveSheetMetalModel(context, definition.entity))
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["entity"]);
        }

        // Find all joint definition entities (edges)
        var jointEdgeEntities = findAllJointDefinitionEntities(context, definition.entity, EntityType.EDGE);
        var jointFaceEntities = findAllJointDefinitionEntities(context, definition.entity, EntityType.FACE);
        
        // Combine both edge and face entities
        var allJointEntities = concatenateArrays(jointEdgeEntities, jointFaceEntities);
        
        if (size(allJointEntities) == 0)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["entity"]);
        }
        
        // Face bends cannot be converted to stitch cut bends
        if (size(jointFaceEntities) > 0)
        {
            throw regenError("Cannot create stitch cut bend on face bends. Select edge joints only.", ["entity"]);
        }

        // CRITICAL: Get default values BEFORE any splitting operations
        // Once edges are split and attributes removed, we can't query the model anymore
        var defaultRadius;
        var defaultKFactor;
        var bendReliefParams;
        var sheetMetalThickness;
        
        if (definition.useDefaultRadius)
        {
            defaultRadius = getDefaultSheetMetalRadius(context, definition.entity);
        }
        if (definition.useDefaultKFactor)
        {
            defaultKFactor = getDefaultSheetMetalKFactor(context, definition.entity);
        }
        
        // Extract bend relief parameters from the model definition
        // These will be used to create subsegments if needed
        bendReliefParams = getBendReliefParameters(context, definition.entity);
        
        // Extract sheet metal thickness for bend relief sizing
        if (bendReliefParams != undefined)
        {
            try
            {
                var sheetMetalEntity = qUnion(getSMDefinitionEntities(context, definition.entity));
                var modelParameters = getModelParameters(context, qOwnerBody(sheetMetalEntity));
                sheetMetalThickness = modelParameters.frontThickness;
                if (sheetMetalThickness == undefined || abs(sheetMetalThickness) < EDGE_LENGTH_TOLERANCE)
                {
                    sheetMetalThickness = modelParameters.backThickness;
                }
                // If still not available, use a fallback
                if (sheetMetalThickness == undefined || abs(sheetMetalThickness) < EDGE_LENGTH_TOLERANCE)
                {
                    const defaultRad = definition.useDefaultRadius ? defaultRadius : definition.radius;
                    sheetMetalThickness = defaultRad * 0.1;
                }
            }
            catch
            {
                // If we can't get thickness, use a fallback based on bend radius
                const defaultRad = definition.useDefaultRadius ? defaultRadius : definition.radius;
                sheetMetalThickness = defaultRad * 0.1;
            }
        }

        // Collect all processed edges for final update
        var allProcessedEdges = [];
        
        // Process each joint entity independently
        var entityIndex = 0;
        for (var jointEntity in jointEdgeEntities)
        {
            const processedEdges = processJointEntity(context, id + ("entity" ~ entityIndex), 
                jointEntity, definition, defaultRadius, defaultKFactor, bendReliefParams, sheetMetalThickness, definition.reliefEdgeRetraction);
            allProcessedEdges = append(allProcessedEdges, processedEdges);
            entityIndex += 1;
        }
        
        // Update sheet metal geometry with all modified edges from all entities
        if (size(allProcessedEdges) > 0)
        {
            const allProcessedEdgesQuery = qUnion(allProcessedEdges);
            updateSheetMetalGeometry(context, id, { 
                "entities" : allProcessedEdgesQuery,
                "associatedChanges" : allProcessedEdgesQuery
            });
        }
    }, { 
        useDefaultRadius : true, 
        useDefaultKFactor : true 
    });

/**
 * Processes a single joint entity, splitting it into stitch cut bend segments.
 * Inputs:
 *   context - Evaluation context
 *   id - Operation ID for this specific joint entity
 *   jointEntity - Query for a single joint definition entity (edge)
 *   definition - Feature definition with parameters
 *   defaultRadius - Default bend radius for the model
 *   defaultKFactor - Default K-factor for the model
 *   bendReliefParams - Bend relief parameters from the model (optional)
 *   sheetMetalThickness - Sheet metal thickness (optional, for bend relief sizing)
 * Outputs: Query for all processed edges from this joint entity
 */
function processJointEntity(context is Context, id is Id, jointEntity is Query, 
    definition is map, defaultRadius, defaultKFactor, bendReliefParams, sheetMetalThickness, reliefEdgeRetraction) returns Query
{
    // Debug: Show the original master edges being processed
    if (definition.showDebug)
    {
        debug(context, jointEntity, DebugColor.MAGENTA);
    }
    
    // Get existing attribute to understand current joint state
    var existingAttribute = getJointAttribute(context, jointEntity);
    if (existingAttribute == undefined)
    {
        throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["entity"]);
    }

    // Calculate edge length and validate using the joint definition entity
    const selectedEdgesQuery = qEntityFilter(jointEntity, EntityType.EDGE);
    const selectedEdges = evaluateQuery(context, selectedEdgesQuery);
    if (size(selectedEdges) == 0)
    {
        throw regenError("Select at least one sheet metal edge", ["entity"]);
    }

    const orderedEdgeQuery = qUnion(selectedEdges);
    const path = try silent(constructPath(context, orderedEdgeQuery));
    if (path == undefined)
    {
        throw regenError("Unable to order the selected edges into a continuous chain", ["entity"], jointEntity);
    }

    // Debug: Show the ordered path edges
    if (definition.showDebug)
    {
        debug(context, qUnion(path.edges), DebugColor.CYAN);
    }

    const totalLength = evLength(context, {
                "entities" : jointEntity
            });

    if (totalLength <= EDGE_LENGTH_TOLERANCE)
    {
        throw regenError("Selected edge must have a measurable length", ["entity"]);
    }

    // Set edges for spacing calculation (spacing utilities expect definition.edges)
    // Use mergeMaps to prevent unintended side effects when processing multiple entities
    // by ensuring each entity gets its own definition map with the correct edges set
    var localDefinition = mergeMaps(definition, { "edges" : jointEntity });

    // Use centralized spacing calculation from spacingUtils
    // Note: This returns a map with computed spacing values
    localDefinition = computeCurvePatternSpacing(context, id, localDefinition);

    var bridgeCount = localDefinition.instanceCount;

    // Calculate domains for bridges (bend segments - the connections)
    // These domains represent where the bend segments will be positioned
    var bridgeDomains = []; // Array of {start, end} maps representing bridge positions as normalized parameters

    // Get offsets based on mode
    var startOffset = 0 * meter;
    var endOffset = 0 * meter;
    
    if (localDefinition.useOffsets == true)
    {
        if (!localDefinition.twoOffsets)
        {
            // Equal offsets mode: same offset on both ends
            startOffset = localDefinition.offset;
            endOffset = localDefinition.offset;
        }
        else
        {
            // Two offsets mode: different offsets on each end
            // oppositeDirection controls which offset goes to which end
            if (!localDefinition.oppositeDirection)
            {
                startOffset = localDefinition.offset1;
                endOffset = localDefinition.offset2;
            }
            else
            {
                // Flipped: swap which offset goes where
                startOffset = localDefinition.offset2;
                endOffset = localDefinition.offset1;
            }
        }
    }

    // Calculate bridge positions using spacing type
    // Bridges are the bend segments (connections), stitches between are rips (cuts)
    if (localDefinition.spacingType == CurvePatternSpacingType.EQUAL)
    {
        bridgeDomains = calculateEqualSpacedDomains(totalLength, localDefinition.bridgeWidth, bridgeCount, startOffset, endOffset, localDefinition.endMode);
    }
    else if (localDefinition.spacingType == CurvePatternSpacingType.DISTANCE)
    {
        bridgeDomains = calculateDistanceSpacedDomains(totalLength, localDefinition.bridgeWidth, localDefinition.distance, bridgeCount, startOffset, endOffset);
    }
    else if (localDefinition.spacingType == CurvePatternSpacingType.BESTFIT)
    {
        // For BESTFIT, instanceCount is computed by computeCurvePatternSpacing
        bridgeDomains = calculateEqualSpacedDomains(totalLength, localDefinition.bridgeWidth, bridgeCount, startOffset, endOffset, localDefinition.endMode);
    }

    if (bridgeCount == 0 || size(bridgeDomains) == 0)
    {
        throw regenError("No bridges can fit with the specified parameters", ["bridgeWidth", "instanceCount"]);
    }

    // Validate that bridge domains do not overlap
    if (!validateDomainsNoOverlap(bridgeDomains, FRACTION_TOLERANCE))
    {
        throw regenError("Resultant bridges would overlap. Adjust spacing parameters to avoid overlapping bridges.", ["bridgeWidth", "instanceCount"]);
    }

    // Determine if we need to create bend relief subsegments
    const createBendReliefSubsegments = shouldCreateBendReliefSubsegments(bendReliefParams);
    
    // If bend relief subsegments are needed, calculate their size and add them to the split parameters
    var bendReliefSubsegmentSize = 0 * meter;
    if (createBendReliefSubsegments)
    {
        bendReliefSubsegmentSize = calculateBendReliefSubsegmentSize(sheetMetalThickness, bendReliefParams);
    }
    
    // Create modified bridge domains that include bend relief subsegments
    // The subsegments should extend OUTSIDE the bridge boundaries into adjacent rip regions
    var allDomains = [];
    for (var bridgeDomain in bridgeDomains)
    {
        if (createBendReliefSubsegments && bendReliefSubsegmentSize > EDGE_LENGTH_TOLERANCE)
        {
            // Convert subsegment size to normalized parameter
            const subsegmentParam = bendReliefSubsegmentSize / totalLength;
            
            // Create relief subsegment BEFORE bridge start (extending into rip region)
            const startReliefStart = bridgeDomain.start - subsegmentParam;
            const startReliefEnd = bridgeDomain.start;
            
            // Create relief subsegment AFTER bridge end (extending into rip region)
            const endReliefStart = bridgeDomain.end;
            const endReliefEnd = bridgeDomain.end + subsegmentParam;
            
            // Validate relief segments are within bounds [0, 1]
            const startReliefValid = startReliefStart >= -FRACTION_TOLERANCE && startReliefEnd <= 1 + FRACTION_TOLERANCE;
            const endReliefValid = endReliefStart >= -FRACTION_TOLERANCE && endReliefEnd <= 1 + FRACTION_TOLERANCE;
            
            // Add start relief subsegment if it's within bounds
            if (startReliefValid && startReliefStart >= 0)
            {
                allDomains = append(allDomains, {
                    "start" : max(0, startReliefStart),
                    "end" : startReliefEnd,
                    "segmentType" : "bendRelief"
                });
            }
            
            // Add the main bridge domain (unchanged)
            allDomains = append(allDomains, {
                "start" : bridgeDomain.start,
                "end" : bridgeDomain.end,
                "segmentType" : "bend"
            });
            
            // Add end relief subsegment if it's within bounds
            if (endReliefValid && endReliefEnd <= 1)
            {
                allDomains = append(allDomains, {
                    "start" : endReliefStart,
                    "end" : min(1, endReliefEnd),
                    "segmentType" : "bendRelief"
                });
            }
        }
        else
        {
            // No bend relief subsegments needed, just use the original bridge
            allDomains = append(allDomains, {
                "start" : bridgeDomain.start,
                "end" : bridgeDomain.end,
                "segmentType" : "bend"
            });
        }
    }

    // Use mixInTracking pattern: union the original query with a tracking query
    const trackedEdges = qUnion([orderedEdgeQuery, startTracking(context, orderedEdgeQuery)]);

    // Split the edges at all domain boundaries
    const splitParameters = calculateSplitParametersFromDomains(allDomains);
    const splitInstructions = calculateEdgeSplitInstructionsFromParameters(context, path, splitParameters);

    if (size(splitInstructions) == 0)
    {
        throw regenError("Unable to calculate edge split locations", ["entity"]);
    }

    // Perform all split operations
    const splitOperationId = id + "splitAllEdges";
    var splitOperationIndex = 0;
    
    for (var instruction in splitInstructions)
    {
        const currentSplitId = splitOperationId + ("split" ~ toString(splitOperationIndex));
        
        try
        {
            opSplitEdges(context, currentSplitId, {
                        "edges" : instruction.edge,
                        "parameters" : [instruction.parameters]
                    });
        }
        catch
        {
            throw regenError("Failed to split the sheet metal edge at the requested locations", ["entity"], instruction.edge);
        }
        splitOperationIndex += 1;
    }

    // After splitting, identify which segments are bridges (bends) vs stitches (rips)
    const allEdgesAfterSplit = qEntityFilter(qUnion([orderedEdgeQuery, trackedEdges]), EntityType.EDGE);
    const allEdgesAfterSplitEval = evaluateQuery(context, allEdgesAfterSplit);
    
    if (size(allEdgesAfterSplitEval) == 0)
    {
        throw regenError("No edges found after splitting", ["entity"]);
    }

    // CRITICAL FIX: Split edges inherit BOTH association and definition attributes from parent
    // Both must be removed and reassigned uniquely for independent segments
    const allEdgesAfterSplitQuery = qUnion(allEdgesAfterSplitEval);
    
    // Step 1: Remove shared association attribute
    removeAttributes(context, {
        "entities" : allEdgesAfterSplitQuery,
        "attributePattern" : {} as SMAssociationAttribute
    });
    
    // Step 2: Remove shared definition attribute
    removeAttributes(context, {
        "entities" : allEdgesAfterSplitQuery,
        "attributePattern" : {} as SMAttribute
    });
    
    // Step 3: Assign unique association attributes to each segment
    assignSMAssociationAttributes(context, allEdgesAfterSplitQuery);

    // Identify segments by their domain type
    // Separate bend, bendRelief, and stitch (rip) segments
    var bendSegmentEdges = qNothing();
    var bendReliefSegmentEdges = qNothing();
    
    if (createBendReliefSubsegments)
    {
        // When bend relief subsegments exist, identify them separately
        // Filter allDomains to get only bend and bendRelief domains
        var bendDomains = [];
        var reliefDomains = [];
        
        for (var domain in allDomains)
        {
            if (domain.segmentType == "bend")
            {
                bendDomains = append(bendDomains, domain);
            }
            else if (domain.segmentType == "bendRelief")
            {
                reliefDomains = append(reliefDomains, domain);
            }
        }
        
        // Identify bend segments (main bridge segments without relief subsegments)
        if (size(bendDomains) > 0)
        {
            bendSegmentEdges = identifySegmentsByEdgeMidpoints(context, allEdgesAfterSplit, path, totalLength, bendDomains);
        }
        
        // Identify bend relief subsegments
        if (size(reliefDomains) > 0)
        {
            bendReliefSegmentEdges = identifySegmentsByEdgeMidpoints(context, allEdgesAfterSplit, path, totalLength, reliefDomains);
        }
    }
    else
    {
        // No bend relief subsegments, so bridge segments are just the original bridges
        bendSegmentEdges = identifySegmentsByEdgeMidpoints(context, allEdgesAfterSplit, path, totalLength, bridgeDomains);
    }

    // Stitch segments are everything that's not a bend or bend relief (the rip cuts between bridges)
    const stitchSegmentEdges = qSubtraction(qSubtraction(allEdgesAfterSplit, bendSegmentEdges), bendReliefSegmentEdges);

    // Validate we have segments
    const bendSegmentCount = size(evaluateQuery(context, bendSegmentEdges));
    const bendReliefSegmentCount = size(evaluateQuery(context, bendReliefSegmentEdges));
    const stitchCount = size(evaluateQuery(context, stitchSegmentEdges));

    if (bendSegmentCount == 0 && bendReliefSegmentCount == 0 && stitchCount == 0)
    {
        throw regenError("No edge segments found after splitting", ["entity"]);
    }

    // Debug visualization of classified segments
    if (definition.showDebug)
    {
        // Show bend segments in green
        if (bendSegmentCount > 0)
        {
            debug(context, bendSegmentEdges, DebugColor.GREEN);
        }
        // Show relief segments in red
        if (bendReliefSegmentCount > 0)
        {
            debug(context, bendReliefSegmentEdges, DebugColor.RED);
        }
        // Show stitch (rip) segments in yellow
        if (stitchCount > 0)
        {
            debug(context, stitchSegmentEdges, DebugColor.YELLOW);
        }
    }

    // Step 4: Apply unique definition attributes to each segment
    // Each segment now has its own unique association attribute from Step 3
    // Note: isFaceBend is false because face bends are rejected at lines 102-105 in the main function validation
    // Note: Use original definition (not localDefinition) to preserve bend parameters (radius, kFactor, etc.)
    
    // Apply BEND attributes to main bend segments
    if (bendSegmentCount > 0)
    {
        applyJointAttributesToSegments(context, id + "bends", bendSegmentEdges, existingAttribute, 
            SMJointType.BEND, definition, false, true, defaultRadius, defaultKFactor);
    }
    
    // Relief segments should have NO joint attributes - leave them as free edges
    // The sheet metal system will apply bend relief when it detects bend edges adjacent to free edges
    // This is the ONLY way sheet metal engine supports bend relief for stitch bends
    
    // Apply RIP attributes to stitch segments
    if (stitchCount > 0)
    {
        applyJointAttributesToSegments(context, id + "stitches", stitchSegmentEdges, existingAttribute, 
            SMJointType.RIP, definition, false, false, undefined, undefined);
    }
    
    // Extend sheet body in relief regions to create clearance for bend relief
    // Uses Move Face edge extension mechanism: clear SM attributes, then extend sheet body edges
    if (bendReliefSegmentCount > 0)
    {
        println("Relief segments created: " ~ bendReliefSegmentCount);
        println("Calling extendSheetBodyForReliefRegions...");
        extendSheetBodyForReliefRegions(context, id + "reliefExtension", bendReliefSegmentEdges, bendSegmentEdges, reliefEdgeRetraction, jointEntity);
    }
    
    return allEdgesAfterSplitQuery;
}

/**
 * Finds all joint definition entities (edges or faces) from a user selection.
 * Inputs:
 *   context - Evaluation context
 *   entity - User-selected query
 *   entityType - Type to filter for (EDGE or FACE)
 * Outputs: Array of individual entity queries, one per joint definition entity
 */
function findAllJointDefinitionEntities(context is Context, entity is Query, entityType is EntityType) returns array
{
    const entityQ = qUnion(getSMDefinitionEntities(context, entity));
    const sheetEntities = qEntityFilter(entityQ, entityType);
    const evaluatedEntities = evaluateQuery(context, sheetEntities);
    
    var result = [];
    for (var singleEntity in evaluatedEntities)
    {
        result = append(result, qUnion([singleEntity]));
    }
    return result;
}

/**
 * Creates a new edge bend attribute with proper metadata.
 * Inputs:
 *   context - Evaluation context
 *   id - Operation ID (unique per segment)
 *   jointEdge - Query for the joint edge
 *   existingAttribute - Existing joint attribute to reference
 *   radius - Bend radius
 *   useDefaultRadius - Whether using default radius
 *   kFactor - K-factor for bend calculation
 *   useDefaultKFactor - Whether using default K-factor
 * Outputs: New bend attribute with full metadata
 */
function createNewEdgeBendAttribute(context is Context, id is Id, jointEdge is Query,
    existingAttribute is SMAttribute,
    radius, useDefaultRadius is boolean,
    kFactor, useDefaultKFactor is boolean) returns SMAttribute
precondition
{
    isLength(radius);
}
{
    // Always create a new bend attribute with unique ID
    var bendAttribute = makeSMJointAttribute(toAttributeId(id));
    bendAttribute.jointType = {
            "value" : SMJointType.BEND,
            "canBeEdited" : false,
            "controllingFeatureId" : toAttributeId(id),
            "parameterIdInFeature" : "stitchJointType"
        };
    
    // Compute angle for this specific segment (geometry-dependent)
    const computedAngle = try silent(bendAngle(context, id + "angle", jointEdge, radius));
    if (computedAngle == undefined || abs(computedAngle) < TOLERANCE.zeroAngle * radian)
    {
        bendAttribute.angle = undefined;
    }
    else
    {
        bendAttribute.angle = { "value" : computedAngle, "canBeEdited" : false };
    }
    
    // Set radius with basic metadata
    bendAttribute.radius = {
            "value" : radius,
            "canBeEdited" : true,
            "isDefault" : useDefaultRadius
        };
    
    // Set k-factor with basic metadata (note: attribute field is 'k-factor' with hyphen)
    bendAttribute['k-factor'] = {
            "value" : kFactor,
            "canBeEdited" : true,
            "isDefault" : useDefaultKFactor
        };
    
    // If EITHER radius or k-factor are overridden (not default), set controlling metadata for BOTH
    // This ensures changes via sheet metal table modify this feature rather than creating separate ones
    if (!useDefaultRadius || !useDefaultKFactor)
    {
        const attributeId = toAttributeId(id);
        bendAttribute.radius.controllingFeatureId = attributeId;
        bendAttribute.radius.parameterIdInFeature = "radius";
        bendAttribute.radius.defaultIdInFeature = "useDefaultRadius";
        bendAttribute['k-factor'].controllingFeatureId = attributeId;
        bendAttribute['k-factor'].parameterIdInFeature = "kFactor";
        bendAttribute['k-factor'].defaultIdInFeature = "useDefaultKFactor";
    }
    
    return bendAttribute;
}

/**
 * Creates a new rip attribute with proper metadata.
 * Inputs:
 *   id - Operation ID (unique per segment)
 *   existingAttribute - Existing joint attribute to reference
 *   jointStyle - Rip joint style (EDGE, BUTT, BUTT2)
 * Outputs: New rip attribute with full metadata
 */
function createNewRipAttribute(id is Id, existingAttribute is SMAttribute, jointStyle is SMJointStyle) returns SMAttribute
{
    // Always create a new rip attribute with unique ID
    var ripAttribute = makeSMJointAttribute(toAttributeId(id));
    ripAttribute.jointType = {
            "value" : SMJointType.RIP,
            "canBeEdited" : false,
            "controllingFeatureId" : toAttributeId(id),
            "parameterIdInFeature" : "entity"
        };
    
    // Set joint style with metadata
    ripAttribute.jointStyle = {
            "value" : jointStyle,
            "canBeEdited" : false,
            "controllingFeatureId" : toAttributeId(id),
            "parameterIdInFeature" : "entity"
        };
    
    // Copy angle from existing attribute
    ripAttribute.angle = existingAttribute.angle;
    
    // Copy minimal clearance if available
    if (existingAttribute.minimalClearance != undefined)
    {
        ripAttribute.minimalClearance = existingAttribute.minimalClearance;
    }
    
    return ripAttribute;
}

/**
 * Applies joint attributes to a set of edge segments.
 * Creates appropriate bend or rip attributes with unique IDs and assigns them to the segments.
 * Inputs:
 *   context - Evaluation context
 *   id - Operation ID for this attribute assignment
 *   segmentEdges - Query for edge segments to modify
 *   existingAttribute - The original joint attribute from before splitting (for property reference)
 *   targetJointType - The joint type to apply (BEND or RIP)
 *   definition - Feature definition with parameters
 *   isFaceBend - Whether the original joint was a face bend
 *   isBridge - Whether these are bridge segments (true = bend) or stitch segments (false = rip)
 *   defaultRadius - Pre-fetched default radius (optional, only for BEND)
 *   defaultKFactor - Pre-fetched default k-factor (optional, only for BEND)
 */
function applyJointAttributesToSegments(context is Context, id is Id, segmentEdges is Query, 
    existingAttribute is SMAttribute, targetJointType is SMJointType, definition is map, 
    isFaceBend is boolean, isBridge is boolean, defaultRadius, defaultKFactor)
{
    // Get each individual edge segment
    const edges = evaluateQuery(context, segmentEdges);
    
    for (var i = 0; i < size(edges); i += 1)
    {
        const edge = edges[i];
        const edgeQuery = qUnion([edge]);
        
        // Create new attribute with unique ID based on target joint type
        var newAttribute;
        
        if (targetJointType == SMJointType.BEND)
        {
            if (isFaceBend)
            {
                throw regenError("Cannot create bend attributes on face bend segments", ["entity"]);
            }
            
            // Get radius and k-factor
            var radius;
            var kFactor;
            
            // Use pre-fetched defaults if available, otherwise get from definition
            if (definition.useDefaultRadius)
            {
                radius = defaultRadius;
            }
            else
            {
                radius = definition.radius;
            }
            
            if (definition.useDefaultKFactor)
            {
                kFactor = defaultKFactor;
            }
            else
            {
                kFactor = definition.kFactor;
            }
            
            // Create new BEND attribute with unique ID and full metadata
            newAttribute = createNewEdgeBendAttribute(context, id + ("bend" ~ i), edgeQuery,
                existingAttribute, radius, definition.useDefaultRadius,
                kFactor, definition.useDefaultKFactor);
        }
        else if (targetJointType == SMJointType.RIP)
        {
            if (isFaceBend)
            {
                throw regenError("Cannot create rip attributes on face bend segments", ["entity"]);
            }
            
            // Create new RIP attribute with unique ID and full metadata
            newAttribute = createNewRipAttribute(id + ("rip" ~ i), existingAttribute, SMJointStyle.EDGE);
        }
        else
        {
            throw regenError("Unsupported joint type for stitch cut bend: " ~ toString(targetJointType), ["entity"]);
        }
        
        if (!isEntityAppropriateForAttribute(context, edgeQuery, newAttribute))
        {
            throw regenError("Cannot assign " ~ toString(targetJointType) ~ " attribute to edge segment", ["entity"], edgeQuery);
        }
        
        // Set the new attribute on this edge
        setAttribute(context, {
            "entities" : edgeQuery,
            "attribute" : newAttribute
        });
    }
}

/**
 * Gets the default sheet metal bend radius from the model parameters.
 * Inputs:
 *   context - Evaluation context
 *   entity - Query for a sheet metal entity
 * Outputs: Default bend radius for the model
 */
function getDefaultSheetMetalRadius(context is Context, entity is Query)
{
    var sheetmetalEntity = qUnion(getSMDefinitionEntities(context, entity));
    var modelParameters = getModelParameters(context, qOwnerBody(sheetmetalEntity));
    return modelParameters.defaultBendRadius;
}

/**
 * Gets the default sheet metal K-factor from the model parameters.
 * Inputs:
 *   context - Evaluation context
 *   entity - Query for a sheet metal entity
 * Outputs: Default K-factor for the model
 */
function getDefaultSheetMetalKFactor(context is Context, entity is Query)
{
    var sheetmetalEntity = qUnion(getSMDefinitionEntities(context, entity));
    var modelParameters = getModelParameters(context, qOwnerBody(sheetmetalEntity));
    return modelParameters["k-factor"];
}

/**
 * Gets the bend relief parameters from the sheet metal model definition.
 * Inputs:
 *   context - Evaluation context
 *   entity - Query for a sheet metal entity
 * Outputs: Map containing bend relief parameters (style, depthScale, widthScale) or undefined if not found
 */
function getBendReliefParameters(context is Context, entity is Query)
{
    try
    {
        var sheetmetalEntity = qUnion(getSMDefinitionEntities(context, entity));
        var modelBody = qOwnerBody(sheetmetalEntity);
        
        // Get the model attribute directly to access bend relief parameters
        var modelAttributes = getAttributes(context, {
            "entities" : modelBody,
            "attributePattern" : asSMAttribute({ "objectType" : SMObjectType.MODEL })
        });
        
        if (size(modelAttributes) == 0)
        {
            return undefined;
        }
        
        var modelAttr = modelAttributes[0];
        
        // Extract bend relief parameters if they exist
        var bendReliefStyle = undefined;
        var bendReliefDepthScale = undefined;
        var bendReliefWidthScale = undefined;
        
        if (modelAttr.defaultBendReliefStyle != undefined)
        {
            bendReliefStyle = modelAttr.defaultBendReliefStyle;
        }
        
        if (modelAttr.defaultBendReliefDepthScale != undefined)
        {
            // Extract the value field from the map structure
            bendReliefDepthScale = modelAttr.defaultBendReliefDepthScale.value;
        }
        
        if (modelAttr.defaultBendReliefScale != undefined)
        {
            // Extract the value field from the map structure
            bendReliefWidthScale = modelAttr.defaultBendReliefScale.value;
        }
        
        return {
            "style" : bendReliefStyle,
            "depthScale" : bendReliefDepthScale,
            "widthScale" : bendReliefWidthScale
        };
    }
    catch
    {
        return undefined;
    }
}

/**
 * Checks if bend relief subsegments should be created based on the model's bend relief style.
 * Subsegments are needed for non-TEAR styles to allow bend relief geometry to be created.
 * NOTE: Sheet metal engine REQUIRES bend edges to be adjacent to free edges (null regions) for relief.
 * Corner attributes on vertices between bend/rip edges do NOT work.
 * Inputs:
 *   bendReliefParams - Map containing bend relief parameters from getBendReliefParameters
 * Outputs: Boolean indicating whether to create bend relief subsegments
 */
function shouldCreateBendReliefSubsegments(bendReliefParams) returns boolean
{
    if (bendReliefParams == undefined || bendReliefParams.style == undefined)
    {
        return false;
    }
    
    // Create subsegments for RECTANGLE and OBROUND styles
    // TEAR style doesn't need subsegments as it doesn't create additional geometry
    return (bendReliefParams.style == SMReliefStyle.RECTANGLE || 
            bendReliefParams.style == SMReliefStyle.OBROUND);
}

/**
 * Extends sheet body in relief regions to create clearance for bend relief.
 * Uses the Move Face edge extension mechanism:
 * 1. Clear SM attributes from edges to break associations
 * 2. Create construction geometry as extension targets
 * 3. Call opExtendSheetBody with edgeLimitOptions to extend/retract SM definition edges
 * This is how Move Face separates surfaces when breaking a rip joint.
 * Inputs:
 *   context - Evaluation context
 *   id - Feature ID for this operation
 *   reliefEdges - Query for relief segment edges
 *   bendEdges - Query for bend segment edges
 *   retractionDistance - Distance to retract edges by
 *   jointEntity - Query for the original joint entity edge
 */
function extendSheetBodyForReliefRegions(context is Context, id is Id, reliefEdges is Query, bendEdges is Query, retractionDistance, jointEntity is Query)
{
    // Get faces adjacent to the entire joint entity edge
    // Relief segments are new edge segments on the joint, but SM definition edges are on the faces
    const facesAdjacentToJoint = qAdjacent(jointEntity, AdjacencyType.EDGE, EntityType.FACE);
    
    // Get the SM definition entities from those faces
    const smDefinitionEntities = getSMDefinitionEntities(context, facesAdjacentToJoint);
    const smEdges = qEntityFilter(qUnion(smDefinitionEntities), EntityType.EDGE);
    const smEdgeList = evaluateQuery(context, smEdges);
    
    println("Relief edges query resolves to: " ~ size(evaluateQuery(context, reliefEdges)) ~ " edges");
    println("Found " ~ size(smEdgeList) ~ " SM edges on faces adjacent to joint");
    
    if (size(smEdgeList) == 0)
        return;
    
    // Build edge limit options array (like Move Face does)
    var edgeLimitOptions = [];
    
    for (var i = 0; i < size(smEdgeList); i += 1)
    {
        try
        {
            const smEdge = smEdgeList[i];
            
            println("Processing SM edge " ~ (i + 1) ~ " of " ~ size(smEdgeList));
            
            // Step 1: Clear SM attributes to break associations (like Move Face line 613)
            clearSmAttributes(context, smEdge);
            println("  Cleared SM attributes from edge");
            
            // Step 2: Find adjacent faces to determine which to extend
            const adjacentFaces = evaluateQuery(context, qAdjacent(smEdge, AdjacencyType.EDGE, EntityType.FACE));
            println("  Found " ~ size(adjacentFaces) ~ " adjacent faces");
            
            if (size(adjacentFaces) == 0)
                continue;
            
            // Get the first face as the one to extend
            const faceToExtend = adjacentFaces[0];
            
            // Step 3: Extract face as surface tool body (like Move Face line 571)
            const extractId = id + ("extract" ~ i);
            opExtractSurface(context, extractId, {
                "faces" : faceToExtend
            });
            const extractedSurface = qCreatedBy(extractId, EntityType.FACE);
            
            // Step 4: Offset the extracted surface to create limitEntity (like Move Face line 575)
            // Get face normal to determine offset direction
            const facePlane = evFaceTangentPlane(context, {
                "face" : faceToExtend,
                "parameter" : vector(0.5, 0.5)
            });
            
            // Calculate offset direction (inward, opposite of face normal for retraction)
            const offsetDirection = -facePlane.normal;
            
            // Offset the extracted surface
            const offsetId = id + ("offset" ~ i);
            opOffsetFace(context, offsetId, {
                "moveFaces" : extractedSurface,
                "offsetDistance" : retractionDistance,
                "direction" : offsetDirection
            });
            const limitEntity = qCreatedBy(offsetId, EntityType.FACE);
            
            // Step 5: Calculate helpPoint (center of offset face, like Move Face line 587)
            const helpPoint = facePlane.origin + offsetDirection * retractionDistance;
            
            // Step 6: Build edge limit option
            edgeLimitOptions = append(edgeLimitOptions, {
                "edge" : smEdge,
                "faceToExtend" : faceToExtend,
                "limitEntity" : limitEntity,
                "helpPoint" : helpPoint
            });
            
            println("  Created edgeLimitOption for edge " ~ (i + 1));
        }
        catch (error)
        {
            // If any step fails for this edge, skip it and continue
            // Some edges may not be extensible
            println("  ERROR processing edge " ~ (i + 1) ~ ": " ~ error);
        }
    }
    
    // Step 7: Call opExtendSheetBody with edge limit options (like Move Face line 1101-1106)
    if (size(edgeLimitOptions) > 0)
    {
        try
        {
            println("Calling opExtendSheetBody with " ~ size(edgeLimitOptions) ~ " edge limit options");
            sheetMetalExtendSheetBodyCall(context, id, {
                "entities" : qUnion(smEdgeList),
                "extendMethod" : ExtendSheetBoundingType.EXTEND_TO_SURFACE,
                "edgeLimitOptions" : edgeLimitOptions,
                "fence" : true  // Critical parameter from Move Face
            });
            println("Sheet body extension completed successfully");
        }
        catch (error)
        {
            // If extension fails, the cleared attributes still broke associations
            // which may be enough
            println("ERROR in opExtendSheetBody: " ~ error);
        }
    }
}


/**
 * Calculates the size of bend relief subsegments based on sheet metal thickness and relief parameters.
 * The subsegment should be large enough to accommodate the bend relief geometry.
 * According to sheet metal standards, bend relief size is based on material thickness, not bend radius.
 * Inputs:
 *   thickness - Sheet metal thickness
 *   bendReliefParams - Map containing bend relief parameters
 * Outputs: Length value for the subsegment size
 */
function calculateBendReliefSubsegmentSize(thickness, bendReliefParams) returns ValueWithUnits
precondition
{
    thickness == undefined || isLength(thickness);
}
{
    // If thickness is not provided, return a minimal size
    if (thickness == undefined)
    {
        return 0 * meter;
    }
    
    // Calculate based on bend relief depth scale
    // Bend relief depth is typically thickness * depthScale (not radius!)
    var depthScale = DEFAULT_BEND_RELIEF_DEPTH_SCALE;
    if (bendReliefParams != undefined && bendReliefParams.depthScale != undefined)
    {
        depthScale = bendReliefParams.depthScale;
    }
    
    // The bend relief depth is thickness * depthScale
    // Use the size directly without safety margin to avoid self-intersecting geometry
    const subsegmentSize = thickness * depthScale;
    
    return subsegmentSize;
}

/**
 * Converts domain definitions (start/end normalized parameters) into split parameters.
 * Each domain represents a bridge (bend segment), so we need to split at both the start and end of each domain.
 * Inputs:
 *   domains - Array of {start, end, segmentType} maps with normalized parameters (0 to 1)
 * Outputs: Array of split parameters
 */
function calculateSplitParametersFromDomains(domains is array) returns array
{
    var splitParameters = [];
    
    for (var domainIndex = 0; domainIndex < size(domains); domainIndex += 1)
    {
        const domain = domains[domainIndex];
        const prevDomain = (domainIndex > 0) ? domains[domainIndex - 1] : undefined;
        const nextDomain = (domainIndex < size(domains) - 1) ? domains[domainIndex + 1] : undefined;
        
        // Add start of this domain as a split point
        // For first domain, always add. For others, check if not adjacent to previous domain
        if (domainIndex == 0 || (prevDomain != undefined && abs(prevDomain.end - domain.start) > FRACTION_TOLERANCE))
        {
            splitParameters = append(splitParameters, domain.start);
        }
        else if (prevDomain != undefined)
        {
            // Domains are adjacent, add a single split at the junction
            splitParameters = append(splitParameters, domain.start);
        }
        
        // Add end of this domain as a split point
        // For last domain, always add. For others, only if there's a gap to next domain
        if (domainIndex == size(domains) - 1 || (nextDomain != undefined && abs(domain.end - nextDomain.start) > FRACTION_TOLERANCE))
        {
            splitParameters = append(splitParameters, domain.end);
        }
    }
    
    // Sort and remove duplicates
    splitParameters = sort(splitParameters, function(a, b) { return a - b; });
    
    var uniqueParameters = [];
    for (var param in splitParameters)
    {
        // Skip parameters at or near endpoints
        if (param <= FRACTION_TOLERANCE || param >= 1 - FRACTION_TOLERANCE)
        {
            continue;
        }
        // Skip duplicates
        if (size(uniqueParameters) == 0 || abs(param - uniqueParameters[size(uniqueParameters) - 1]) > FRACTION_TOLERANCE)
        {
            uniqueParameters = append(uniqueParameters, param);
        }
    }
    
    return uniqueParameters;
}

/**
 * Calculates edge split instructions from normalized split parameters along a path.
 * Converts normalized parameters (0-1 along entire path) to per-edge parameters.
 * Inputs:
 *   context - Evaluation context
 *   path - Ordered path of edges
 *   splitParameters - Array of normalized split parameters (0 to 1)
 * Outputs: Array of split instructions {edge, parameters} for opSplitEdges
 */
function calculateEdgeSplitInstructionsFromParameters(context is Context, path is Path, splitParameters is array) returns array
{
    const totalLength = evLength(context, {
                "entities" : qUnion(path.edges)
            });
    
    if (totalLength <= EDGE_LENGTH_TOLERANCE)
    {
        throw regenError("Selected edge chain must have a measurable length");
    }

    // Build cumulative fractions for each edge boundary
    var cumulativeFractions = [0];
    var accumulatedFraction = 0;
    for (var edgeIndex = 0; edgeIndex < size(path.edges); edgeIndex += 1)
    {
        const edge = path.edges[edgeIndex];
        const edgeLength = evLength(context, { "entities" : edge });
        if (edgeLength <= EDGE_LENGTH_TOLERANCE)
        {
            throw regenError("Edge chain contains a zero length edge");
        }
        accumulatedFraction += edgeLength / totalLength;
        if (edgeIndex == size(path.edges) - 1)
        {
            accumulatedFraction = 1; // Ensure last fraction is exactly 1
        }
        cumulativeFractions = append(cumulativeFractions, accumulatedFraction);
    }

    // Map split parameters to specific edges
    var edgeParameterMap = {};
    var orderedEdgeIndices = [];

    for (var fraction in splitParameters)
    {
        if (fraction <= FRACTION_TOLERANCE || fraction >= 1 - FRACTION_TOLERANCE)
        {
            continue; // Skip parameters at or near endpoints
        }

        // Find which edge this fraction falls on
        for (var boundaryIndex = 1; boundaryIndex < size(cumulativeFractions); boundaryIndex += 1)
        {
            const startFraction = cumulativeFractions[boundaryIndex - 1];
            const endFraction = cumulativeFractions[boundaryIndex];

            if (fraction < startFraction - FRACTION_TOLERANCE)
            {
                continue;
            }
            if (fraction > endFraction + FRACTION_TOLERANCE)
            {
                continue;
            }

            // Calculate normalized parameter within this edge (0 to 1)
            const normalizedRange = endFraction - startFraction;
            if (normalizedRange <= EDGE_PARAMETER_TOLERANCE)
            {
                throw regenError("Edge chain contains a segment that cannot be subdivided");
            }

            var edgeParameter = (fraction - startFraction) / normalizedRange;
            edgeParameter = max(0, min(1, edgeParameter));
            
            // Account for edge flip
            if (path.flipped[boundaryIndex - 1])
            {
                edgeParameter = 1 - edgeParameter;
            }

            // Add to map
            if (edgeParameterMap[boundaryIndex - 1] == undefined)
            {
                edgeParameterMap[boundaryIndex - 1] = [];
                orderedEdgeIndices = append(orderedEdgeIndices, boundaryIndex - 1);
            }
            edgeParameterMap[boundaryIndex - 1] = append(edgeParameterMap[boundaryIndex - 1], edgeParameter);
            break;
        }
    }

    // Build split instructions
    var splitInstructions = [];
    for (var edgeIndex in orderedEdgeIndices)
    {
        var parameters = edgeParameterMap[edgeIndex];
        
        // Sort parameters
        parameters = sort(parameters, function(a, b) { return a - b; });

        // Filter out parameters too close to endpoints or each other
        var filteredParameters = [];
        for (var parameter in parameters)
        {
            if (parameter <= EDGE_PARAMETER_TOLERANCE || parameter >= 1 - EDGE_PARAMETER_TOLERANCE)
            {
                continue;
            }
            if (size(filteredParameters) != 0 && abs(parameter - filteredParameters[size(filteredParameters) - 1]) < EDGE_PARAMETER_TOLERANCE)
            {
                continue;
            }
            filteredParameters = append(filteredParameters, parameter);
        }

        if (size(filteredParameters) == 0)
        {
            continue;
        }

        splitInstructions = append(splitInstructions, {
                    "edge" : path.edges[edgeIndex],
                    "parameters" : filteredParameters
                });
    }

    return splitInstructions;
}

/**
 * Identifies edges that fall within specified domains by checking edge midpoints.
 * For each edge, calculates its midpoint position along the edge chain and checks if it falls
 * within any domain.
 * Inputs:
 *   context - Evaluation context
 *   allSplitEdges - Query for all edges (after splitting)
 *   path - Original path before splitting (used for reference)
 *   totalLength - Total length of the edge chain
 *   domains - Array of {start, end} maps with normalized parameters for each domain
 * Outputs: Query containing all edges that fall within domains
 */
function identifySegmentsByEdgeMidpoints(context is Context, allSplitEdges is Query, path is Path, 
    totalLength is ValueWithUnits, domains is array) returns Query
{
    if (size(domains) == 0)
    {
        return qNothing();
    }

    // Get all split edges
    const edges = evaluateQuery(context, allSplitEdges);
    const edgeCount = size(edges);

    if (edgeCount == 0)
    {
        return qNothing();
    }

    // Try to construct a path from the split edges to get them in order
    const orderedPath = try silent(constructPath(context, qUnion(edges)));
    var orderedEdges = edges;
    if (orderedPath != undefined)
    {
        orderedEdges = orderedPath.edges;
    }

    // Calculate the normalized position (0 to 1) of each edge's midpoint along the chain
    var edgeMidpointParameters = [];
    var accumulatedLength = 0 * meter;

    for (var edge in orderedEdges)
    {
        const edgeLength = evLength(context, { "entities" : edge });
        const midpointParameter = (accumulatedLength + edgeLength / 2) / totalLength;
        edgeMidpointParameters = append(edgeMidpointParameters, midpointParameter);
        accumulatedLength += edgeLength;
    }

    // Check which edges fall within domains
    var domainEdgeQueries = [];

    for (var i = 0; i < size(orderedEdges); i += 1)
    {
        const edge = orderedEdges[i];
        const midpointParameter = edgeMidpointParameters[i];

        // Check if this parameter falls within any domain
        var isInDomain = false;
        for (var j = 0; j < size(domains); j += 1)
        {
            const domain = domains[j];
            if (midpointParameter >= domain.start && midpointParameter <= domain.end)
            {
                isInDomain = true;
                break;
            }
        }

        if (isInDomain)
        {
            domainEdgeQueries = append(domainEdgeQueries, edge);
        }
    }

    if (size(domainEdgeQueries) == 0)
    {
        return qNothing();
    }

    return qUnion(domainEdgeQueries);
}

/**
 * Editing logic function placeholder.
 * Can be implemented to provide dynamic UI updates based on user selections.
 */
export function sheetMetalStitchCutBendEditLogic(context is Context, id is Id, oldDefinition is map, 
    definition is map, isCreating is boolean, specifiedParameters is map, hiddenBodies is Query) returns map
{
    return definition;
}
