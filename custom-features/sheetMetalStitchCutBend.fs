FeatureScript 2856;
// Sheet Metal Stitch Cut Bend Feature
// This feature combines logic from Modify Joint and Tab and Slot to create stitch cut bends.
// It allows changing any joint style into a stitch bend by splitting the edge into segments
// that are alternately assigned bend or rip attributes.

// Imports used in interface - enums must be exported for use in preconditions
export import(path : "onshape/std/smjointtype.gen.fs", version : "2856.0");
export import(path : "onshape/std/smjointstyle.gen.fs", version : "2856.0");
export import(path : "onshape/std/smbendtype.gen.fs", version : "2856.0");

// Imports used internally
import(path : "onshape/std/common.fs", version : "2856.0");
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

// Import spacing utilities for bridge/stitch spacing logic
export import(path : "c51f6558b7346f455a634ff5/89645624be30e0ee0e2ad98d/8ce820287d75ed2e92412d90", version : "5d62715b8aae99049ee68c1a"); // spacingUtils.fs

const EDGE_LENGTH_TOLERANCE = 1e-9 * meter;
const EDGE_PARAMETER_TOLERANCE = 1e-6;
const FRACTION_TOLERANCE = 1e-9;

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
        // Joint edge selection - matches Modify Joint filter pattern
        annotation { "Name" : "Joint edge",
                    "Filter" : (SheetMetalDefinitionEntityType.FACE || SheetMetalDefinitionEntityType.EDGE) && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES,
                    "MaxNumberOfPicks" : 1 }
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
    }
    {
        // Validate sheet metal context
        checkNotInFeaturePattern(context, definition.entity, ErrorStringEnum.SHEET_METAL_NO_FEATURE_PATTERN);

        if (!areEntitiesFromSingleActiveSheetMetalModel(context, definition.entity))
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["entity"]);
        }

        // Find the joint definition entity
        var jointEntity = findJointDefinitionEntity(context, definition.entity, EntityType.EDGE);
        var isFaceBend = false;
        if (jointEntity == undefined)
        {
            // Not an edge, is it a face?
            jointEntity = findJointDefinitionEntity(context, definition.entity, EntityType.FACE);
            isFaceBend = true;
        }
        if (jointEntity == undefined)
        {
            throw regenError(ErrorStringEnum.SHEET_METAL_ACTIVE_JOIN_NEEDED, ["entity"]);
        }

        // Face bends cannot be converted to stitch cut bends
        if (isFaceBend)
        {
            throw regenError("Cannot create stitch cut bend on a face bend. Select an edge joint instead.", ["entity"]);
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

        const totalLength = evLength(context, {
                    "entities" : jointEntity
                });

        if (totalLength <= EDGE_LENGTH_TOLERANCE)
        {
            throw regenError("Selected edge must have a measurable length", ["entity"]);
        }

        // Set edges for spacing calculation (spacing utilities expect definition.edges)
        definition.edges = jointEntity;

        // Use centralized spacing calculation from spacingUtils
        definition = computeCurvePatternSpacing(context, id, definition);

        var bridgeCount = definition.instanceCount;

        // Calculate domains for bridges (bend segments - the connections)
        // These domains represent where the bend segments will be positioned
        var bridgeDomains = []; // Array of {start, end} maps representing bridge positions as normalized parameters

        // Get offsets based on mode
        var startOffset = 0 * meter;
        var endOffset = 0 * meter;
        
        if (definition.useOffsets == true)
        {
            if (!definition.twoOffsets)
            {
                // Equal offsets mode: same offset on both ends
                startOffset = definition.offset;
                endOffset = definition.offset;
            }
            else
            {
                // Two offsets mode: different offsets on each end
                // oppositeDirection controls which offset goes to which end
                if (!definition.oppositeDirection)
                {
                    startOffset = definition.offset1;
                    endOffset = definition.offset2;
                }
                else
                {
                    // Flipped: swap which offset goes where
                    startOffset = definition.offset2;
                    endOffset = definition.offset1;
                }
            }
        }

        // Calculate bridge positions using spacing type
        // Bridges are the bend segments (connections), stitches between are rips (cuts)
        if (definition.spacingType == CurvePatternSpacingType.EQUAL)
        {
            bridgeDomains = calculateEqualSpacedDomains(totalLength, definition.bridgeWidth, bridgeCount, startOffset, endOffset, definition.endMode);
        }
        else if (definition.spacingType == CurvePatternSpacingType.DISTANCE)
        {
            bridgeDomains = calculateDistanceSpacedDomains(totalLength, definition.bridgeWidth, definition.distance, bridgeCount, startOffset, endOffset);
        }
        else if (definition.spacingType == CurvePatternSpacingType.BESTFIT)
        {
            // For BESTFIT, instanceCount is computed by computeCurvePatternSpacing
            bridgeDomains = calculateEqualSpacedDomains(totalLength, definition.bridgeWidth, bridgeCount, startOffset, endOffset, definition.endMode);
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

        // Set up tracking for split operations - this is critical for attribute propagation
        // Get the sheet metal body that owns the joint edge
        const sheetMetalBody = qOwnerBody(jointEntity);
        const initialData = getInitialEntitiesAndAttributes(context, sheetMetalBody);
        const trackingSheetMetalModel = startTracking(context, sheetMetalBody);
        
        // Track the edges we're about to split
        const trackedEdges = qUnion([orderedEdgeQuery, startTracking(context, orderedEdgeQuery)]);

        // Split the edges at bridge boundaries (domains represent bridges, so we need both start and end points)
        const splitParameters = calculateSplitParametersFromDomains(bridgeDomains);
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
            try
            {
                opSplitEdges(context, splitOperationId + ("split" ~ toString(splitOperationIndex)), {
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

        // Bridge segments are the ones that fall within the calculated domains (the bend connections)
        const bridgeSegmentEdges = identifySegmentsByEdgeMidpoints(context, allEdgesAfterSplit, path, totalLength, bridgeDomains);

        // Stitch segments are everything else (the rip cuts between bridges)
        const stitchSegmentEdges = qSubtraction(allEdgesAfterSplit, bridgeSegmentEdges);

        // Validate we have both types of segments
        const bridgeSegmentCount = size(evaluateQuery(context, bridgeSegmentEdges));
        const stitchCount = size(evaluateQuery(context, stitchSegmentEdges));

        if (bridgeSegmentCount == 0 && stitchCount == 0)
        {
            throw regenError("No edge segments found after splitting", ["entity"]);
        }

        // Create attributes for bridge segments (the bend connections)
        // Use setAttribute directly on the new edges created by splitting
        if (bridgeSegmentCount > 0)
        {
            applyJointAttributesToSegments(context, id + "bridges", bridgeSegmentEdges, existingAttribute, 
                SMJointType.BEND, definition, isFaceBend, true);
        }

        // Create attributes for stitch segments (the rip/cut segments)
        if (stitchCount > 0)
        {
            applyJointAttributesToSegments(context, id + "stitches", stitchSegmentEdges, existingAttribute, 
                SMJointType.RIP, definition, isFaceBend, false);
        }

        // Use assignSMAttributesToNewOrSplitEntities to properly handle attribute propagation after splits
        // This is the key function that handles all the disambiguation and tracking for split entities
        const toUpdate = assignSMAttributesToNewOrSplitEntities(context, qUnion([trackingSheetMetalModel, sheetMetalBody]), initialData, id);
        
        // Update sheet metal geometry with the properly tracked entities
        updateSheetMetalGeometry(context, id, { 
            "entities" : toUpdate.modifiedEntities,
            "deletedAttributes" : toUpdate.deletedAttributes
        });
    }, { 
        useDefaultRadius : true, 
        useDefaultKFactor : true 
    });

/**
 * Finds the joint definition entity (edge or face) from a user selection.
 * Inputs:
 *   context - Evaluation context
 *   entity - User-selected query
 *   entityType - Type to filter for (EDGE or FACE)
 * Outputs: Query for the single definition entity, or undefined if not found
 */
function findJointDefinitionEntity(context is Context, entity is Query, entityType is EntityType)
{
    const entityQ = qUnion(getSMDefinitionEntities(context, entity));
    var sheetEntities = qEntityFilter(entityQ, entityType);
    if (size(evaluateQuery(context, sheetEntities)) != 1)
    {
        return undefined;
    }
    else
    {
        return sheetEntities;
    }
}

/**
 * Applies joint attributes to a set of edge segments.
 * Creates appropriate bend or rip attributes and assigns them to the segments.
 * Split edges inherit attributes from parent, so we must remove them before setting new ones.
 * The proper attribute propagation will be handled by assignSMAttributesToNewOrSplitEntities.
 * Inputs:
 *   context - Evaluation context
 *   id - Operation ID for this attribute assignment
 *   segmentEdges - Query for edge segments to modify
 *   existingAttribute - The original joint attribute from before splitting
 *   targetJointType - The joint type to apply (BEND or RIP only)
 *   definition - Feature definition with parameters
 *   isFaceBend - Whether the original joint was a face bend
 *   isBridge - Whether these are bridge segments (true = bend) or stitch segments (false = rip)
 */
function applyJointAttributesToSegments(context is Context, id is Id, segmentEdges is Query, 
    existingAttribute is SMAttribute, targetJointType is SMJointType, definition is map, 
    isFaceBend is boolean, isBridge is boolean)
{
    // Get each individual edge segment
    const edges = evaluateQuery(context, segmentEdges);
    
    for (var i = 0; i < size(edges); i += 1)
    {
        const edge = edges[i];
        const edgeQuery = qUnion([edge]);
        
        // Create new attribute based on target joint type
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
            
            if (definition.useDefaultRadius)
            {
                radius = getDefaultSheetMetalRadius(context, definition.entity);
            }
            else
            {
                radius = definition.radius;
            }
            
            if (definition.useDefaultKFactor)
            {
                kFactor = getDefaultSheetMetalKFactor(context, definition.entity);
            }
            else
            {
                kFactor = definition.kFactor;
            }
            
            newAttribute = createNewEdgeBendAttribute(context, id + ("bend" ~ toString(i)), edgeQuery, existingAttribute,
                radius, definition.useDefaultRadius, kFactor, definition.useDefaultKFactor);
        }
        else if (targetJointType == SMJointType.RIP)
        {
            if (isFaceBend)
            {
                throw regenError("Cannot create rip attributes on face bend segments", ["entity"]);
            }
            
            // Always use EDGE style for rip segments (stitches)
            var ripStyle = SMJointStyle.EDGE;
            newAttribute = createNewRipAttribute(context, edgeQuery, id + ("rip" ~ toString(i)), existingAttribute, ripStyle);
        }
        else
        {
            throw regenError("Unsupported joint type for stitch cut bend: " ~ toString(targetJointType), ["entity"]);
        }
        
        if (!isEntityAppropriateForAttribute(context, edgeQuery, newAttribute))
        {
            throw regenError("Cannot assign " ~ toString(targetJointType) ~ " attribute to edge segment", ["entity"], edgeQuery);
        }
        
        // Get the inherited attribute from THIS specific split edge
        // Each split edge inherits the parent's attribute, so we need to get it individually
        const edgeAttribute = getJointAttribute(context, edgeQuery);
        
        // Use replaceSMAttribute to replace the inherited attribute with the new one
        // This function handles remove + set internally and is scoped to this specific edge
        replaceSMAttribute(context, edgeAttribute, newAttribute);
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
 * Creates a new edge bend attribute from an existing joint attribute.
 * This follows the pattern from sheetMetalJoint.fs for proper attribute handling.
 * Inputs:
 *   context - Evaluation context
 *   id - Operation ID
 *   jointEdge - Query for the joint edge
 *   existingAttribute - Existing joint attribute to use as template
 *   radius - Bend radius
 *   useDefaultRadius - Whether using default radius
 *   kFactor - K-factor for bend calculation
 *   useDefaultKFactor - Whether using default K-factor
 * Outputs: New bend attribute
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
    var bendAttribute;
    if (existingAttribute.jointType.value != SMJointType.BEND)
    {
        // Creating a new bend from a non-bend joint
        // IMPORTANT: Use existingAttribute.attributeId to preserve the association
        bendAttribute = makeSMJointAttribute(existingAttribute.attributeId);
        bendAttribute.angle = existingAttribute.angle;
    }
    else
    {
        // Modifying an existing bend
        bendAttribute = existingAttribute;
    }

    // Check if walls are planar - if not, need to recompute angle
    const planarFacesQ = qGeometry(qAdjacent(jointEdge, AdjacencyType.EDGE, EntityType.FACE), GeometryType.PLANE);
    if (size(evaluateQuery(context, planarFacesQ)) != 2)
    {
        // If walls are non-planar bend angle depends on the radius and needs to be re-computed
        const angle = try silent(bendAngle(context, id, jointEdge, radius));
        if (angle == undefined || abs(angle) < TOLERANCE.zeroAngle * radian)
            throw regenError(ErrorStringEnum.SHEET_METAL_NO_0_ANGLE_BEND, ["entity"]);
        bendAttribute.angle = { "value" : angle, "canBeEdited" : false };
    }

    bendAttribute.jointType = {
            "value" : SMJointType.BEND,
            "controllingFeatureId" : toAttributeId(id),
            "parameterIdInFeature" : "jointType",
            "canBeEdited" : true
        };
    bendAttribute.bendType = {
            "value" : SMBendType.STANDARD,
            "canBeEdited" : false
        };
    bendAttribute.radius = {
            "value" : radius,
            "canBeEdited" : true,
            "isDefault" : useDefaultRadius
        };
    bendAttribute['k-factor'] = {
            "value" : kFactor,
            "canBeEdited" : true,
            "isDefault" : useDefaultKFactor
        };
    if (!useDefaultRadius || !useDefaultKFactor)
    {
        // If EITHER of the radius or k-factor are changed then we need to mark BOTH as being controlled by this feature
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
 * Creates a new rip attribute from an existing joint attribute.
 * This follows the pattern from sheetMetalRip.fs using the createRipAttribute utility.
 * Inputs:
 *   context - Evaluation context
 *   edge - Query for the edge to create rip on
 *   id - Operation ID
 *   existingAttribute - Existing joint attribute to use as template
 *   jointStyle - Rip joint style (EDGE, BUTT, BUTT2)
 * Outputs: New rip attribute
 */
function createNewRipAttribute(context is Context, edge is Query, id is Id, existingAttribute is SMAttribute, jointStyle is SMJointStyle) returns SMAttribute
{
    // Use the standard utility function which properly handles angle calculation
    // Pass attributeId from existing attribute to preserve association
    return createRipAttribute(context, edge, existingAttribute.attributeId, jointStyle, undefined);
}

/**
 * Converts domain definitions (start/end normalized parameters) into split parameters.
 * Each domain represents a bridge (bend segment), so we need to split at both the start and end of each domain.
 * Inputs:
 *   domains - Array of {start, end} maps with normalized parameters (0 to 1)
 * Outputs: Sorted array of unique split parameters
 */
function calculateSplitParametersFromDomains(domains is array) returns array
{
    var splitParameters = [];
    
    for (var domain in domains)
    {
        // Add both start and end of each domain (bridge) as split points
        splitParameters = append(splitParameters, domain.start);
        splitParameters = append(splitParameters, domain.end);
    }
    
    // Sort and deduplicate
    splitParameters = sort(splitParameters, function(a, b) { return a - b; });
    
    // Remove duplicates and edge parameters
    var uniqueParameters = [];
    for (var param in splitParameters)
    {
        if (param <= FRACTION_TOLERANCE || param >= 1 - FRACTION_TOLERANCE)
        {
            continue; // Skip parameters at or near endpoints
        }
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
