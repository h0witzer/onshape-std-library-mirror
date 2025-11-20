FeatureScript 2796;

/*
    Chamlet (Chamfer/Fillet Hybrid)
    
    This custom feature automates the creation of printable chamfer/fillet hybrid features
    (chamlets) with draft angles optimized for 3D printing orientation. It implements a 
    workflow that creates truncated fillets with controlled draft angles.
    
    Workflow:
    1. Copy the target body
    2. Automatically determine which faces adjacent to selected edges need to translate
    3. Translate those faces in appropriate directions by offset = radius * (1 - cos(draftAngle))
    4. Apply fillet to the specified edges on the modified copy
    5. Boolean SUBTRACT_COMPLEMENT to preserve original body identity
    
    The offset distance is calculated as: offset = radius * (1 - cos(draftAngle))
    This ensures the resulting fillet surface has the specified draft angle relative to
    the printer Z direction, making it more printable without support material.
    
    Usage:
    - Select the body to modify
    - Select edges or faces where the chamlet will be applied (same as fillet feature)
    - Specify printer Z direction (build plate normal) - supports lines, axes, planes, mate connectors
    - Set draft angle (typical: 30-60 degrees)
    - Set fillet radius
    
    The feature automatically determines which faces need to move based on their orientation
    relative to the printer Z direction. Top-side and bottom-side fillets are handled automatically.
    
    Version: 1.1
    Author: Derek Van Allen and various vibe coding platforms
    Date: 2025
*/

import(path : "onshape/std/common.fs", version : "2796.0");
import(path : "onshape/std/query.fs", version : "2796.0");
import(path : "onshape/std/evaluate.fs", version : "2796.0");
import(path : "onshape/std/feature.fs", version : "2796.0");
import(path : "onshape/std/geomOperations.fs", version : "2796.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2796.0");
import(path : "onshape/std/transform.fs", version : "2796.0");
import(path : "onshape/std/valueBounds.fs", version : "2796.0");
import(path : "onshape/std/vector.fs", version : "2796.0");
import(path : "onshape/std/math.fs", version : "2796.0");
import(path : "onshape/std/error.fs", version : "2796.0");
import(path : "onshape/std/topologyUtils.fs", version : "2796.0");
import(path : "onshape/std/attributes.fs", version : "2796.0");

// Bounds for draft angle parameter - typical 3D printing draft angles
const DRAFT_ANGLE_BOUNDS = {
    (degree) : [0.1, 45, 89]
} as AngleBoundSpec;

// Bounds for fillet radius parameter
const FILLET_RADIUS_BOUNDS = {
    (meter) : [0.0001, 0.005, 0.5],
    (centimeter) : 0.5,
    (millimeter) : 5.0,
    (inch) : 0.2,
    (foot) : 0.015,
    (yard) : 0.005
} as LengthBoundSpec;

/**
 * Feature that creates printable chamfer/fillet hybrid features (chamlets).
 * This feature automates the process of creating truncated fillets with a specified
 * draft angle optimized for 3D printing orientation.
 */
annotation { "Feature Type Name" : "Chamlet",
             "Feature Type Description" : "Creates truncated fillets with draft angles optimized for 3D printing, or for fancy router profiles on furniture",
             "Filter Selector" : "allparts" }
export const printableChamlet = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Body to modify",
                     "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES,
                     "MaxNumberOfPicks" : 1 }
        definition.targetBody is Query;

        annotation { "Name" : "Entities to fillet",
                     "Filter" : ((ActiveSheetMetal.NO && ((EntityType.EDGE && EdgeTopology.TWO_SIDED) || EntityType.FACE))
                                 || (EntityType.EDGE && SheetMetalDefinitionEntityType.VERTEX))
                                 && ConstructionObject.NO && SketchObject.NO && ModifiableEntityOnly.YES,
                     "AdditionalBoxSelectFilter" : EntityType.EDGE }
        definition.entities is Query;

        annotation { "Name" : "Printer Z direction",
                     "Filter" : QueryFilterCompound.ALLOWS_DIRECTION || BodyType.MATE_CONNECTOR || (ConstructionObject.YES && SketchObject.NO && EntityType.FACE),
                     "MaxNumberOfPicks" : 1 }
        definition.printerZDirection is Query;

        annotation { "Name" : "Flip printer Z direction",
                     "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.flipPrinterZ is boolean;

        annotation { "Name" : "Draft angle",
                     "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isAngle(definition.draftAngle, DRAFT_ANGLE_BOUNDS);

        annotation { "Name" : "Fillet radius",
                     "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.filletRadius, FILLET_RADIUS_BOUNDS);

        annotation { "Name" : "Tangent propagation",
                     "Default" : true }
        definition.tangentPropagation is boolean;
    }
    {
        // Validate inputs
        verifyNonemptyQuery(context, definition, "targetBody", ErrorStringEnum.CANNOT_RESOLVE_ENTITIES);
        verifyNonemptyQuery(context, definition, "entities", ErrorStringEnum.CANNOT_RESOLVE_ENTITIES);
        verifyNonemptyQuery(context, definition, "printerZDirection", ErrorStringEnum.CANNOT_RESOLVE_ENTITIES);

        // Get printer Z direction vector
        var printerZVector = getPrinterZDirectionVector(context, definition);

        // Step 0: Mark the selected entities with an attribute so we can track them after copying
        // There are probably better ways to do tracking but this is the one that was easiest to describe to the AI
        const trackingAttribute = "chamletSelectedEntities_" ~ toAttributeId(id);
        setAttribute(context, {
            "entities" : definition.entities,
            "name" : trackingAttribute,
            "attribute" : { "selected" : true }
        });

        // Step 1: Create a copy of the target body using opPattern with identity transform
        const copiedBodyQuery = copyBodyForChamlet(context, id, definition.targetBody);

        // Step 2: Find the corresponding entities in the copied body using the attribute
        // Support both edges and faces - check what was selected
        var trackedEntitiesInCopy;
        const hasEdges = !isQueryEmpty(context, qEntityFilter(definition.entities, EntityType.EDGE));
        const hasFaces = !isQueryEmpty(context, qEntityFilter(definition.entities, EntityType.FACE));
        
        if (hasEdges && hasFaces)
        {
            // Both edges and faces selected
            trackedEntitiesInCopy = qUnion([
                qOwnedByBody(copiedBodyQuery, EntityType.EDGE)->qHasAttribute(trackingAttribute),
                qOwnedByBody(copiedBodyQuery, EntityType.FACE)->qHasAttribute(trackingAttribute)
            ]);
        }
        else if (hasEdges)
        {
            // Only edges
            trackedEntitiesInCopy = qOwnedByBody(copiedBodyQuery, EntityType.EDGE)
                                   ->qHasAttribute(trackingAttribute);
        }
        else
        {
            // Only faces
            trackedEntitiesInCopy = qOwnedByBody(copiedBodyQuery, EntityType.FACE)
                                   ->qHasAttribute(trackingAttribute);
        }
        
        // Step 3: Determine faces to move based on geometry of tracked entities
        // Find faces adjacent to tracked edges/faces that should translate to create the draft
        var facesToMove = determineFacesToMove(context, trackedEntitiesInCopy, copiedBodyQuery, printerZVector, hasEdges, hasFaces);

        // Step 4: Calculate the offset distance for face translation
        // The offset is optimized for a global Z shift irrespective of local face geometry
        //This means in non-90 degree cases the chamlet will be more aggressive at shifting than necessary but this is an easier and more performant implementation than a locally sensitive one.
        // For a chamlet effect: radius * (1 - cos(draftAngle))
        // Flipped the 90 to match the nomenclature of printer overhangs as well as usual draft conventions
        const offsetDistance = calculateOffsetDistance(90*degree-definition.draftAngle, definition.filletRadius);

        // Step 5: Perform move face operation to translate faces
        // Faces move in direction determined by their orientation relative to printer Z
        moveFacesForChamlet(context, id, facesToMove, offsetDistance);

        // Step 6: Apply fillet operation to the tracked entities in the modified copy
        applyFilletToCopy(context, id, trackedEntitiesInCopy, definition.filletRadius, definition.tangentPropagation);

        // Step 7: Perform boolean subtraction (SUBTRACT_COMPLEMENT) to preserve original body identity
        // This removes the filleted ramp from the original body, creating the chamlet effect
        performChamletBoolean(context, id, definition.targetBody, copiedBodyQuery);
        
        // Clean up: Remove the tracking attribute
        removeAttributes(context, {
            "entities" : qHasAttribute(trackingAttribute),
            "name" : trackingAttribute
        });
    },
    {
        flipPrinterZ : false,
        tangentPropagation : true
    });

/**
 * Extracts and normalizes the printer Z direction vector from the user selection.
 * Handles direction queries (lines, axes), mate connectors, and construction planes.
 *
 * @param context : The context object
 * @param definition : The feature definition containing printerZDirection and flipPrinterZ
 * @returns {Vector} : The normalized printer Z direction vector
 */
function getPrinterZDirectionVector(context is Context, definition is map) returns Vector
{
    var directionVector;
    
    // Try mate connector first
    if (!isQueryEmpty(context, definition.printerZDirection->qBodyType(BodyType.MATE_CONNECTOR)))
    {
        const mateConnector = evMateConnector(context, { "mateConnector" : definition.printerZDirection });
        directionVector = mateConnector.coordSystem.zAxis;
    }
    // Try construction plane
    else if (!isQueryEmpty(context, definition.printerZDirection->qConstructionFilter(ConstructionObject.YES)))
    {
        const plane = evPlane(context, { "face" : definition.printerZDirection });
        directionVector = plane.normal;
    }
    // Try extracting direction (lines, axes, edges, etc.)
    else
    {
        var direction = try(evAxis(context, { "axis" : definition.printerZDirection }));
        if (direction == undefined)
        {
            direction = try(evLine(context, { "edge" : definition.printerZDirection }));
        }
        if (direction == undefined)
        {
            throw regenError("Could not determine printer Z direction from selection");
        }
        directionVector = direction.direction;
    }
    
    directionVector = normalize(directionVector);
    
    if (definition.flipPrinterZ)
    {
        directionVector = -directionVector;
    }
    
    return directionVector;
}

/**
 * Creates a copy of the target body using opPattern with identity transform.
 * This copy will be modified and used as the tool for the final boolean operation.
 *
 * @param context : The context object
 * @param id : The feature id
 * @param targetBody : Query for the body to copy
 * @returns {Query} : Query for the copied body
 */
function copyBodyForChamlet(context is Context, id is Id, targetBody is Query) returns Query
{
    opPattern(context, id + "copyBody", {
        "entities" : targetBody,
        "transforms" : [identityTransform()],
        "instanceNames" : ["chamletCopy"]
    });
    
    // Return a query for the copied body using qCreatedBy
    return qCreatedBy(id + "copyBody", EntityType.BODY);
}

/**
 * Determines which faces should be translated to create the chamlet effect.
 * For tracked edges, finds adjacent faces and determines which ones need to move.
 * For tracked faces, finds their edges and determines adjacent faces to move.
 * The faces that are more parallel to the printer Z (vertical walls) should move,
 * creating a ramp for the fillet.
 *
 * @param context : The context object
 * @param trackedEntities : Query for tracked edges/faces in the copied body
 * @param copiedBody : Query for the copied body
 * @param printerZVector : The printer Z direction vector
 * @param hasEdges : Whether edges were selected
 * @param hasFaces : Whether faces were selected
 * @returns {map} : Map with "faceMoveData" array containing face and direction for each face
 */
function determineFacesToMove(context is Context, trackedEntities is Query, copiedBody is Query, printerZVector is Vector, hasEdges is boolean, hasFaces is boolean) returns map
{
    var faceMoveData = [];
    var processedFaces = {}; // Track faces we've already processed to avoid duplicates
    
    // Get edges to process - either directly selected or from selected faces
    var edgesToProcess;
    if (hasEdges && hasFaces)
    {
        // Both selected - get edges from both
        const directEdges = qEntityFilter(trackedEntities, EntityType.EDGE);
        const facesSelected = qEntityFilter(trackedEntities, EntityType.FACE);
        const edgesFromFaces = qAdjacent(facesSelected, AdjacencyType.EDGE, EntityType.EDGE);
        edgesToProcess = qUnion([directEdges, edgesFromFaces]);
    }
    else if (hasEdges)
    {
        edgesToProcess = trackedEntities;
    }
    else
    {
        // Only faces - get their edges
        edgesToProcess = qAdjacent(trackedEntities, AdjacencyType.EDGE, EntityType.EDGE);
    }
    
    // Process each edge individually
    for (var edge in evaluateQuery(context, edgesToProcess))
    {
        // Get the two faces adjacent to this edge
        const adjacentFaces = evaluateQuery(context, qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE));
        
        if (size(adjacentFaces) == 2)
        {
            // Get normals for both faces
            var faceNormals = [];
            var faces = [];
            
            for (var face in adjacentFaces)
            {
                var faceNormal;
                const facePlane = try silent(evPlane(context, { "face" : face }));
                if (facePlane != undefined)
                {
                    faceNormal = facePlane.normal;
                }
                else
                {
                    // For non-planar faces, use tangent plane at center
                    const tangentPlane = evFaceTangentPlane(context, {
                        "face" : face,
                        "parameter" : vector(0.5, 0.5)
                    });
                    faceNormal = tangentPlane.normal;
                }
                
                faces = append(faces, face);
                faceNormals = append(faceNormals, faceNormal);
            }
            
            // Determine which face is more vertical (parallel to printer Z)
            const dotProduct0 = abs(dot(faceNormals[0], printerZVector));
            const dotProduct1 = abs(dot(faceNormals[1], printerZVector));
            
            // The face with higher abs(dot product) is more vertical (more parallel to Z)
            // This is the face that should move to create the chamlet
            var faceToMove;
            var faceNormalToUse;
            
            if (dotProduct0 > dotProduct1)
            {
                faceToMove = faces[0];
                faceNormalToUse = faceNormals[0];
            }
            else
            {
                faceToMove = faces[1];
                faceNormalToUse = faceNormals[1];
            }
            
            // Check if we've already processed this face
            // Use transient query string representation for tracking
            const faceString = transientQueriesToStrings(faceToMove);
            if (processedFaces[faceString] == undefined)
            {
                // The face should move in its normal direction (outward from the edge)
                // to create space for the fillet ramp
                faceMoveData = append(faceMoveData, { 
                    "face" : faceToMove, 
                    "direction" : faceNormalToUse 
                });
                processedFaces[faceString] = true;
            }
        }
    }
    
    return { "faceMoveData" : faceMoveData };
}

/**
 * Calculates the offset distance for face translation based on draft angle and fillet radius.
 * The geometry requires that the face move by an amount such that the resulting fillet
 * surface has the desired draft angle from the printer Z direction.
 * 
 * For a printable chamlet, offset = radius * (1 - cos(draftAngle))
 * This creates a truncated fillet where the fillet surface has the specified draft angle.
 *
 * @param draftAngle : The desired draft angle for the chamlet
 * @param filletRadius : The radius of the fillet
 * @returns {ValueWithUnits} : The offset distance for face translation
 */
function calculateOffsetDistance(draftAngle is ValueWithUnits, filletRadius is ValueWithUnits) returns ValueWithUnits
precondition
{
    isAngle(draftAngle);
    isLength(filletRadius);
}
{
    // offset = radius * (1 - cos(draftAngle))
    // This ensures the fillet surface has the specified draft angle
    const offsetDistance = filletRadius * (1 - cos(draftAngle));
    return offsetDistance;
}

/**
 * Performs the move face operation to translate selected faces.
 * Each face moves in its determined direction by the calculated offset distance.
 *
 * @param context : The context object
 * @param id : The feature id
 * @param faceMoveData : Map containing face move data with faces and directions
 * @param offsetDistance : The distance to translate faces
 */
function moveFacesForChamlet(context is Context, id is Id, faceMoveData is map, offsetDistance is ValueWithUnits)
{
    const faceDataArray = faceMoveData.faceMoveData;
    
    if (size(faceDataArray) == 0)
    {
        return; // No faces to move
    }
    
    // Move each face in its determined direction
    var faceIndex = 0;
    for (var faceData in faceDataArray)
    {
        opMoveFace(context, id + ("moveFace" ~ faceIndex), {
            "moveFaces" : faceData.face,
            "transform" : transform(faceData.direction * offsetDistance)
        });
        faceIndex += 1;
    }
}

/**
 * Applies fillet operation to the modified copy body.
 * Uses opFillet with the specified radius and tangent propagation settings.
 *
 * @param context : The context object
 * @param id : The feature id
 * @param filletEntities : Query for edges to fillet
 * @param filletRadius : The fillet radius
 * @param tangentPropagation : Whether to propagate fillet along tangent edges
 */
function applyFilletToCopy(context is Context, id is Id, filletEntities is Query, 
                           filletRadius is ValueWithUnits, tangentPropagation is boolean)
{
    if (isQueryEmpty(context, filletEntities))
    {
        return; // No entities to fillet
    }
    
    try
    {
        opFillet(context, id + "fillet", {
            "entities" : filletEntities,
            "radius" : filletRadius,
            "tangentPropagation" : tangentPropagation
        });
    }
    catch (error)
    {
        // If fillet fails, report warning but continue - the boolean might still work
        const message = try(error.message as ErrorStringEnum);
        if (message != undefined)
        {
            reportFeatureWarning(context, id + "fillet", message);
        }
        else
        {
            reportFeatureWarning(context, id + "fillet", "Fillet operation failed");
        }
    }
}

/**
 * Performs the final boolean operation to create the chamlet.
 * Uses SUBTRACT_COMPLEMENT to preserve the original body's identity while removing
 * the filleted material. This is important for maintaining part references.
 *
 * @param context : The context object
 * @param id : The feature id
 * @param targetBody : Query for the original target body
 * @param copiedBody : Query for the modified copy body (tool)
 */
function performChamletBoolean(context is Context, id is Id, targetBody is Query, copiedBody is Query)
{
    opBoolean(context, id + "boolean", {
        "tools" : copiedBody,
        "targets" : targetBody,
        "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
        "keepTools" : false
    });
}
