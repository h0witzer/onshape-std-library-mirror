FeatureScript 2796; /* Automatically generated version */
// This module is part of the FeatureScript Standard Library and is distributed under the MIT License.
// See the LICENSE tab for the license text.
// Copyright (c) 2013-Present PTC Inc.

export import(path : "onshape/std/query.fs", version : "2796.0");
export import(path : "onshape/std/edgeconvexitytype.gen.fs", version : "2796.0");
export import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2796.0");

import(path : "onshape/std/common.fs", version : "2796.0");
import(path : "onshape/std/debug.fs", version : "2796.0");
import(path : "onshape/std/feature.fs", version : "2796.0");
import(path : "onshape/std/featureList.fs", version : "2796.0");
import(path : "onshape/std/evaluate.fs", version : "2796.0");
import(path : "onshape/std/string.fs", version : "2796.0");
import(path : "onshape/std/containers.fs", version : "2796.0");
import(path : "onshape/std/error.fs", version : "2796.0");
import(path : "onshape/std/sketch.fs", version : "2796.0");
import(path : "onshape/std/variable.fs", version : "2796.0");

/**
 * Allowed selection types to create query variable.
 */
export enum SelectionType
{
    annotation { "Name" : "Selection" }
    SELECTION,
    annotation { "Name" : "Created by" }
    CREATED_BY,
    
    //added by Jelte for qCap Entities  1/12
    annotation { "Name" : "Cap entities" }
    CAP_ENTITY,
    //end added by Jelte
    
    annotation { "Name" : "Owned by" }
    OWNED_BY,
    annotation { "Name" : "Protrusion" }
    PROTRUSION,
    annotation { "Name" : "Pocket" }
    POCKET,
    annotation { "Name" : "Hole" }
    HOLE,
    annotation { "Name" : "Fillets" }
    FILLETS,
    annotation { "Name" : "Bounded faces" }
    BOUNDED_FACES,
    annotation { "Name" : "Loop/chain connected" }
    LOOP_CHAIN_CONNECTED,
    annotation { "Name" : "Parallel" }
    PARALLEL,
    
    //added by Jelte for qParallelish 1/6
    
    annotation { "Name" : "Parallelish edges" }
    TOLERANT_PARALLEL,
    
    //end added by Jelte
    
    annotation { "Name" : "Tangent connected" }
    TANGENT_CONNECTED,
    annotation { "Name" : "Matching" }
    MATCHING,
    annotation { "Name" : "All solid bodies" }
    ALL_SOLID_BODIES,
    annotation { "Name" : "Edge convexity" }
    EDGE_CONVEXITY
}

/**
 * @internal
 */
const SelectionTypeToLowercaseName = {
    SelectionType.SELECTION : "selection",
    SelectionType.CREATED_BY : "created by",
     
    //added by Jelte for qCap Entities 2/12
    SelectionType.CAP_ENTITY : "Cap entity",
    //end added by Jelte
    
    SelectionType.OWNED_BY : "owned by",
    SelectionType.PROTRUSION : "protrusion",
    SelectionType.POCKET : "pocket",
    SelectionType.HOLE : "hole",
    SelectionType.FILLETS : "fillets",
    SelectionType.BOUNDED_FACES : "bounded faces",
    SelectionType.LOOP_CHAIN_CONNECTED : "loop/chain connected",
    SelectionType.PARALLEL : "parallel",
    
    //added by Jelte for qParallelish 2/6
    
    SelectionType.TOLERANT_PARALLEL : "Parallelish edges",
    
    //end added by Jelte
    
    SelectionType.TANGENT_CONNECTED : "tangent connected",
    SelectionType.MATCHING : "matching",
    SelectionType.ALL_SOLID_BODIES : "all solid bodies",
    SelectionType.EDGE_CONVEXITY : "edge convexity"
};

/**
 * Some selection types accept either faces or edges, not both.
 * This enum allows picking which one.
 */
export enum SeedType
{
    annotation { "Name" : "Face" }
    FACE,
    annotation { "Name" : "Edge" }
    EDGE
}

/**
 * Subset of CompareType with just types allowed in qFilletFaces
 */
export enum FilletCompare
{
    annotation { "Name" : "Equal" }
    EQUAL,
    annotation { "Name" : "Less or equal" }
    LESS_EQUAL,
    annotation { "Name" : "Greater or equal" }
    GREATER_EQUAL
}

/**
 * Specifies the topological type of a body, similar to BodyType, with annotations.
 * @seealso [BodyType]
 */
export enum BodyTypeOptions
{
    annotation { "Name" : "Part" }
    SOLID,
    annotation { "Name" : "Surface" }
    SHEET,
    annotation { "Name" : "Curve" }
    WIRE,
    annotation { "Name" : "Point" }
    POINT,
    annotation { "Name" : "Mate connector" }
    MATE_CONNECTOR,
    annotation { "Name" : "Composite part" }
    COMPOSITE
}

/**
 * Predicate showing the selection type and the relevant queries/enum allowed by this type.
 */
export predicate initialQueryPredicate(definition is map)
{
    annotation { "Name" : "Selection type" }
    definition.selectionType is SelectionType;

    if (definition.selectionType == SelectionType.TANGENT_CONNECTED
        || definition.selectionType == SelectionType.MATCHING)
    {
        annotation { "Name" : "Entity type" }
        definition.seedType is SeedType;
    }

    if (definition.selectionType == SelectionType.SELECTION)
    {
        annotation { "Name" : "Selections", "Filter" : AllowMeshGeometry.YES && AllowFlattenedGeometry.YES, "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS }
        definition.selectionQuery is Query;
    }
    else if (definition.selectionType == SelectionType.CREATED_BY)
    {
        annotation { "Name" : "Created by features", "UIHint" : UIHint.ALLOW_FLAT_SKETCH_SELECTION }
        definition.createdByFeatures is FeatureList;
    }
    
            //added by Jelte for qCap Entities 3/12
    else if (definition.selectionType == SelectionType.CAP_ENTITY)
    {
        annotation { "Name" : "Created by features", "UIHint" : UIHint.ALLOW_FEATURE_SELECTION }
        definition.capEntityCreatedByFeatures is FeatureList;
    }
    //end added by Jelte)
    
    else if (definition.selectionType == SelectionType.OWNED_BY || definition.selectionType == SelectionType.EDGE_CONVEXITY)
    {
        annotation { "Name" : "Entities", "Filter" : EntityType.BODY && AllowFlattenedGeometry.YES && AllowMeshGeometry.YES }
        definition.seedBodies is Query;
    }
    else if (definition.selectionType == SelectionType.PROTRUSION
        || definition.selectionType == SelectionType.POCKET
        || definition.selectionType == SelectionType.HOLE
        || definition.selectionType == SelectionType.FILLETS
        || definition.selectionType == SelectionType.BOUNDED_FACES
        || (definition.selectionType == SelectionType.TANGENT_CONNECTED && definition.seedType == SeedType.FACE)
        || (definition.selectionType == SelectionType.MATCHING && definition.seedType == SeedType.FACE))
    {
        annotation { "Name" : "Faces", "Filter" : EntityType.FACE }
        definition.seedFaces is Query;
    }
    else if (definition.selectionType == SelectionType.LOOP_CHAIN_CONNECTED)
    {
        annotation { "Name" : "Edges or faces", "Filter" : EntityType.EDGE || EntityType.FACE }
        definition.seedEdgesOrFaces is Query;
    }
    else if (definition.selectionType == SelectionType.PARALLEL
        || (definition.selectionType == SelectionType.TANGENT_CONNECTED && definition.seedType == SeedType.EDGE)
        || (definition.selectionType == SelectionType.MATCHING && definition.seedType == SeedType.EDGE))
    {
        annotation { "Name" : "Edges", "Filter" : EntityType.EDGE }
        definition.seedEdges is Query;
    }
    if (definition.selectionType == SelectionType.CREATED_BY || definition.selectionType == SelectionType.OWNED_BY
            //added by Jelte for qCap Entities 4/12
            || definition.selectionType == SelectionType.CAP_ENTITY
            //end added by Jelte)
    )
    {
        annotation { "Name" : "Entity type" }
        definition.entityType is EntityType;
    }
    
    if (definition.selectionType == SelectionType.FILLETS)
    {
        annotation { "Name" : "Fillet compare type" }
        definition.filletCompareType is FilletCompare;
    }
    if (definition.selectionType == SelectionType.BOUNDED_FACES)
    {
        annotation { "Name" : "Bounds", "Filter" : EntityType.EDGE || EntityType.FACE }
        definition.boundedFacesBounds is Query;
    }
    if (definition.selectionType == SelectionType.EDGE_CONVEXITY)
    {
        annotation { "Name" : "Edge convexity type" }
        definition.edgeConvexityType is EdgeConvexityType;
    }
    
    //added by Jelte for qParallelish 3/6
    
    if (definition.selectionType == SelectionType.TOLERANT_PARALLEL)
    {
        annotation { "Name" : "Direction", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION }
        definition.direction is Query;    
        
        annotation { "Name" : "Angle tolerance", "Default" : 0 * degree }
        isAngle(definition.angleTolerance, ANGLE_STRICT_90_BOUNDS);
        
        annotation { "Name" : "Filter construction entities", "Default" : true }
        definition.filterConstructionParallel is boolean;
    }
    //end added by Jelte
          
          
    //added by Jelte for qCap Entities 5/12
    if (definition.selectionType == SelectionType.CAP_ENTITY)
    {
        annotation { "Name" : "Cap type", "Default" : CapType.EITHER}
        definition.capType is CapType;      
    }
    
        //End added by Jelte  
    
    if (definition.selectionType == SelectionType.CREATED_BY
    
        //added by Jelte for qCap Entities 6/12
         || definition.selectionType == SelectionType.CAP_ENTITY
        //end added by Jelte)
    )
    
    {        
        annotation { "Name" : "Filter construction entities", "Default" : true }
        definition.filterConstruction is boolean;

        annotation { "Name" : "Filter by body type", "Default" : false }
        definition.filterByBodyType is boolean;

        if (definition.filterByBodyType)
        {
            annotation { "Name" : "Body type" }
            definition.createdByBodyType is BodyTypeOptions;
        }
    }
    
    
}

/**
 * Same as initialQueryPredicate, needs to be separate because of naming restriction in array parameters.
 */
export predicate additionalQueryPredicate(addQ is map)
{
    annotation { "Name" : "Selection type" }
    addQ.addQselectionType is SelectionType;

    annotation { "Name" : "Selection type", "UIHint" : UIHint.ALWAYS_HIDDEN }
    addQ.addQlowercaseSelectionType is string;

    if (addQ.addQselectionType == SelectionType.TANGENT_CONNECTED
        || addQ.addQselectionType == SelectionType.MATCHING)
    {
        annotation { "Name" : "Entity type" }
        addQ.addQseedType is SeedType;
    }

    if (addQ.addQselectionType == SelectionType.SELECTION)
    {
        annotation { "Name" : "Selections", "Filter" : AllowMeshGeometry.YES && AllowFlattenedGeometry.YES, "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS }
        addQ.addQselectionQuery is Query;
    }
    else if (addQ.addQselectionType == SelectionType.CREATED_BY)
    {
        annotation { "Name" : "Created by features", "UIHint" : UIHint.ALLOW_FLAT_SKETCH_SELECTION }
        addQ.addQcreatedByFeatures is FeatureList;
    }
    
        
    //added by Jelte for qCap Entities 7/12
        else if (addQ.addQselectionType == SelectionType.CAP_ENTITY)
    {
        annotation { "Name" : "Created by features", "UIHint" : UIHint.ALLOW_FEATURE_SELECTION }
        addQ.addQcapEntityCreatedByFeatures is FeatureList;
    }
    //end added by Jelte) 


    else if (addQ.addQselectionType == SelectionType.OWNED_BY || addQ.addQselectionType == SelectionType.EDGE_CONVEXITY)
    {
        annotation { "Name" : "Entities", "Filter" : EntityType.BODY && AllowFlattenedGeometry.YES && AllowMeshGeometry.YES }
        addQ.addQseedBodies is Query;
    }
    else if (addQ.addQselectionType == SelectionType.PROTRUSION
        || addQ.addQselectionType == SelectionType.POCKET
        || addQ.addQselectionType == SelectionType.HOLE
        || addQ.addQselectionType == SelectionType.FILLETS
        || addQ.addQselectionType == SelectionType.BOUNDED_FACES
        || (addQ.addQselectionType == SelectionType.TANGENT_CONNECTED && addQ.addQseedType == SeedType.FACE)
        || (addQ.addQselectionType == SelectionType.MATCHING && addQ.addQseedType == SeedType.FACE))
    {
        annotation { "Name" : "Face", "Filter" : EntityType.FACE }
        addQ.addQseedFaces is Query;
    }
    else if (addQ.addQselectionType == SelectionType.LOOP_CHAIN_CONNECTED)
    {
        annotation { "Name" : "Edge or face", "Filter" : EntityType.EDGE || EntityType.FACE }
        addQ.addQseedEdgesOrFaces is Query;
    }
    else if (addQ.addQselectionType == SelectionType.PARALLEL
        || (addQ.addQselectionType == SelectionType.TANGENT_CONNECTED && addQ.addQseedType == SeedType.EDGE)
        || (addQ.addQselectionType == SelectionType.MATCHING && addQ.addQseedType == SeedType.EDGE))
    {
        annotation { "Name" : "Edge", "Filter" : EntityType.EDGE }
        addQ.addQseedEdges is Query;
    }
    if (addQ.addQselectionType == SelectionType.CREATED_BY || addQ.addQselectionType == SelectionType.OWNED_BY
         
         
        //added by Jelte for qCap Entities 8/12
         || addQ.addQselectionType == SelectionType.CAP_ENTITY
        //end added by Jelte)
    )
    {
        annotation { "Name" : "Entity type" }
        addQ.addQentityType is EntityType;
    }
    if (addQ.addQselectionType == SelectionType.FILLETS)
    {
        annotation { "Name" : "Fillet compare type" }
        addQ.addQfilletCompareType is FilletCompare;
    }
    if (addQ.addQselectionType == SelectionType.BOUNDED_FACES)
    {
        annotation { "Name" : "Bounds", "Filter" : EntityType.EDGE || EntityType.FACE }
        addQ.addQboundedFacesBounds is Query;
    }
    if (addQ.addQselectionType == SelectionType.EDGE_CONVEXITY)
    {
        annotation { "Name" : "Edge convexity type" }
        addQ.addQedgeConvexityType is EdgeConvexityType;
    }
    
    
        
    //added by Jelte for qParallelish 4/6
    
    if (addQ.addQselectionType == SelectionType.TOLERANT_PARALLEL)
    {
        annotation { "Name" : "Direction", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION }
        addQ.addQdirection is Query;    
        
        annotation { "Name" : "Angle tolerance", "Default" : 0 * degree }
        isAngle(addQ.addQangleTolerance, ANGLE_STRICT_90_BOUNDS);
        
        annotation { "Name" : "Filter construction entities", "Default" : true }
        addQ.addQfilterConstructionParallel is boolean;
    }
    //end added by Jelte
          
  
    
    
        //added by Jelte for qCap Entities 9/12
    if (addQ.addQselectionType == SelectionType.CAP_ENTITY)
    {
        annotation { "Name" : "Cap type", "Default" : CapType.EITHER}
        addQ.addQcapType is CapType;      
    }
    
        //End added by Jelte  
    
    if (addQ.addQselectionType == SelectionType.CREATED_BY
    
        
        //added by Jelte for qCap Entities 10/12
         || addQ.addQselectionType == SelectionType.CAP_ENTITY
        //end added by Jelte)
    
    )
    {        
        annotation { "Name" : "Filter construction entities", "Default" : true }
        addQ.addQfilterConstruction is boolean;

        annotation { "Name" : "Filter by body type", "Default" : false }
        addQ.addQfilterByBodyType is boolean;

        if (addQ.addQfilterByBodyType)
        {
            annotation { "Name" : "Body type" }
            addQ.addQcreatedByBodyType is BodyTypeOptions;
        }
    }
}

/**
 * Feature to create a query variable via calling `setQueryVariable`.
 * This variable may be used in featurescript via call `getQueryVariable` or in the UI by clicking the "Variable selection" dropdown in the feature dialog
 * or by selecting the query variable feature in the feature list.
 *
 * @param definition {{
 *      @field name {string} : The name of the feature. Must not belong to a non-query variable feature.
 *          If a query variable feature with this name exists, it will be overwritten after this feature.
 *      @field description {string} : Description of the variable. Maximum length of 256 characters.
 *
 *      @field selectionType {SelectionType} : The type of selection the initial query will hold.
 *      @field seedType {SeedType} : If the selection type allows edges or faces, selects the seed type.
 *      @field selectionQuery {Query} : If selectionType is SELECTION, query that will be contained in the variable.
 *      @field createdByFeatures {FeatureList} : If selectionType is CREATED_BY, features whose created entities will be contained in the variable.
 *      @field seedBodies {Query} : If selectionType is OWNED_BY or EDGE_CONVEXITY, bodies owning the entities that will be contained in the variable.
 *      @field seedFaces {Query} : If selectionType is PROTRUSION or POCKET or HOLE or FILLETS or BOUNDED_FACES, or TANGENT_CONNECTED or MATCHING and seedType is FACE,
 *          faces from which the selection is created.
 *      @field seedEdgesOrFaces {Query} : If selectionType is LOOP_CHAIN_CONNECTED, faces or edges from which the loops are computed.
 *      @field seedEdgesOrFaces {Query} : If selectionType is PARALLEL, or TANGENT_CONNECTED or MATCHING and seedType is EDGE, edges from which the selection is created.
 *      @field entityType {EntityType} : If selectionType is CREATED_BY or OWNED_BY, the entity type to include in the variable.
 *      @field filletCompareType {FilletCompare} : If selectionType is FILLETS, the type of fillets to include in the variable.
 *      @field boundedFacesBounds {Query} : If selectionType is BOUNDED_FACES, the faces or edges bounding the selection.
 *      @field edgeConvexityType {EdgeConvexityType} : If selectionType is EDGE_CONVEXITY, the convexity type of edges to include in the variable.
 *
 *      @field addAdditionalQueries {boolean} : Whether to include addition queries in the variable.
 *      @field additionalQueries {array} : An array of additional queries to include. Each item's content is analogous to what is contained in the original query.
 *          It also contains a `booleanOperation` field determining how to combine the additional query with the current query.
 *
 *      @field evaluateOnUse {boolean} : Whether to evaluate the variable when it is created or when it is used.
 *      @field showSelection {boolean} : Whether to highlight the entities contained in the created variable.
 * }}
 */
annotation { "Feature Type Name" : "Query variable+JS", "Feature Name Template" : "###name", "UIHint" : UIHint.NO_PREVIEW_PROVIDED,
        "Tooltip Template" : "###name #description" }
export const queryVariableJS = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Name", "UIHint" : [UIHint.UNCONFIGURABLE, UIHint.QUERY_VARIABLE_NAME], "MaxLength" : 10000 }
        definition.name is string;

        annotation { "Name" : "Description", "MaxLength" : 256, "Default" : "" }
        definition.description is string;

        initialQueryPredicate(definition);

        annotation { "Name" : "Add additional queries" }
        definition.addAdditionalQueries is boolean;

        annotation { "Group Name" : "Additional queries", "Driving Parameter" : "addAdditionalQueries", "Collapsed By Default" : false }
        {
            if (definition.addAdditionalQueries)
            {
                annotation { "Name" : "Additional queries", "Item name" : "additional query", "Item label template" : "#booleanOperation of #addQlowercaseSelectionType" }
                definition.additionalQueries is array;
                for (var addQ in definition.additionalQueries)
                {
                    annotation { "Name" : "Boolean operation", "UIHint" : UIHint.HORIZONTAL_ENUM }
                    addQ.booleanOperation is BooleanOperationType;

                    additionalQueryPredicate(addQ);
                }
            }
        }

        annotation { "Name" : "Evaluate on use", "Default" : false }
        definition.evaluateOnUse is boolean;

        annotation { "Name" : "Show selection", "Default" : true }
        definition.showSelection is boolean;
    }
    {
        if (definition.addAdditionalQueries)
        {
            for (var i = 0; i < size(definition.additionalQueries); i += 1)
            {
                setFeatureComputedParameter(context, id, {
                        "name" : faultyArrayParameterId("additionalQueries", i, "addQlowercaseSelectionType"),
                        "value" : SelectionTypeToLowercaseName[definition.additionalQueries[i].addQselectionType]
                });
            }
        }

        checkQueryVariableName(context, definition.name);

        var query = mapSelectionTypeToQuery(context, definition);
        
        if (definition.addAdditionalQueries)
        {
            for (var addQ in definition.additionalQueries)
            {
                const innerQuery = mapSelectionTypeToQuery(context, remapAdditionalQuery(addQ));

                query = switch (addQ.booleanOperation)
                    {
                        BooleanOperationType.UNION : qUnion(query, innerQuery),
                        BooleanOperationType.SUBTRACTION : qSubtraction(query, innerQuery),
                        BooleanOperationType.INTERSECTION : qIntersection(query, innerQuery)
                    };
            }
        }

        if (!definition.evaluateOnUse)
        {
            // This follows the queries through modifications, substitutions and naming.
            query = qUnion(makeRobustQueriesBatched(context, query));
        }

        setQueryVariable(context, definition.name, definition.description, query);

        if (definition.showSelection)
        {
            try silent
            {
                addDebugEntities(context, query, DebugColor.YELLOW);
            }
        }
        setHighlightedEntities(context, { "entities": query, "equivalentQueryPropagationOnly" : !definition.evaluateOnUse });
    }, { filterByBodyType : false });



function mapSelectionTypeToQuery(context is Context, definition is map) returns Query
{
    return switch (definition.selectionType)
        {
            SelectionType.SELECTION : definition.selectionQuery,
            SelectionType.CREATED_BY : createdBySelection(context, definition),
            
            //added by Jelte for Cap Filter 11/12
            SelectionType.CAP_ENTITY : capEntitiesCreatedBy(context, definition),
            //End added by jelte
            
            SelectionType.OWNED_BY : qOwnedByBody(definition.seedBodies, definition.entityType),
            SelectionType.PROTRUSION : qConvexConnectedFaces(definition.seedFaces),
            SelectionType.POCKET : qConcaveConnectedFaces(definition.seedFaces),
            SelectionType.HOLE : qHoleFaces(definition.seedFaces),
            SelectionType.FILLETS : qFilletFaces(definition.seedFaces, definition.filletCompareType as CompareType),
            SelectionType.BOUNDED_FACES : qFaceOrEdgeBoundedFaces(qUnion([definition.seedFaces, definition.boundedFacesBounds])),
            SelectionType.LOOP_CHAIN_CONNECTED : qLoopEdges(definition.seedEdgesOrFaces),
            SelectionType.PARALLEL : qParallelEdges(definition.seedEdges),
    
            //added by Jelte for qParallelish 5/6
            SelectionType.TOLERANT_PARALLEL : qTolerantParallelEdges(context, definition),
            //end added by Jelte
            
            SelectionType.TANGENT_CONNECTED : definition.seedType == SeedType.FACE ? qTangentConnectedFaces(definition.seedFaces) : qTangentConnectedEdges(definition.seedEdges),
            SelectionType.MATCHING : definition.seedType == SeedType.FACE ? qMatching(definition.seedFaces) : qMatching(definition.seedEdges),
            SelectionType.ALL_SOLID_BODIES : qAllSolidBodies(),
            SelectionType.EDGE_CONVEXITY : qEdgeConvexityTypeFilter(qOwnedByBody(definition.seedBodies, EntityType.EDGE), definition.edgeConvexityType)
        };
}

function filterSketchEdgesAndVerticesFromSheetDeprecated(context is Context, definition is map) returns Query
{
    var featureIds = [];
    for (var feature in definition.createdByFeatures)
    {
        featureIds = append(featureIds, feature.key);
    }
    // For sketches, there are two operations that create edges and vertices.
    // When we select an edge or a vertex from a sketch, it's always from the wire operation.
    // So, to avoid grabbing duplicate edges and vertices, we change the selected id to be the wire operation instead of the whole sketch.
    if ((definition.entityType == EntityType.EDGE || definition.entityType == EntityType.VERTEX) && containsSketch(context, definition.createdByFeatures))
    {
        for (var i = 0; i < size(featureIds); i += 1)
        {
            if (isIdForSketch(context, featureIds[i]))
            {
                featureIds[i] = featureIds[i] + makeId("wireOp");
            }
        }
    }
    var createdByQuery = [];
    for (var featureId in featureIds)
    {
        createdByQuery = append(createdByQuery, qCreatedBy(featureId, definition.entityType));
    }
    return qUnion(createdByQuery);
}

    //added by Jelte for qParallelish 6/6
function qTolerantParallelEdges(context is Context, definition is map) returns Query
{
    var tolerandParralelQuery = new box(qNothing());
    
    const direction = extractDirection(context, definition.direction);

    var allStraightEdges = qEverything(EntityType.EDGE)->qGeometry(GeometryType.LINE);//we query all straight lines
    
    if (definition.filterConstructionParallel)
    {
        allStraightEdges = allStraightEdges->qConstructionFilter(ConstructionObject.NO);  // optionally filter for construction geometry
    }
    
    const angleTolerance = definition.angleTolerance;
    
    
    for (var edge in evaluateQuery(context, allStraightEdges))
    {

        const edgeDirection = evLine(context, { "edge" : edge }).direction;
        
        var angle = angleBetween(direction, edgeDirection);
        
        if (angle > 90 * degree)
        {
            angle = abs(angle - 180 * degree);
        }

        if (angle <= angleTolerance)
        {
            tolerandParralelQuery[] = qUnion(tolerandParralelQuery[], edge);   
        }
    }
    
    return tolerandParralelQuery[];

}
    //end Added by Jelte



    //added by Jelte for Cap Filter 12/12
function capEntitiesCreatedBy(context is Context, definition is map) returns Query
{

    var createdByQuery = qNothing();
    
    for (var featureId, _ in definition.capEntityCreatedByFeatures)
        {
            createdByQuery = qUnion(createdByQuery, qCapEntity(featureId, definition.capType, definition.entityType)); 
        }
        
    if (isQueryEmpty(context, createdByQuery))
        {
        throw regenError("No cap entities found for the given feature and EntityType filter. note: Only extrude, revolve, sweep, loft and thicken features produce cap entities.");
         }
    
    if (definition.filterConstruction)
    {
        createdByQuery = createdByQuery->qConstructionFilter(ConstructionObject.NO);
    }
    if (definition.filterByBodyType)
    {
        createdByQuery = createdByQuery->qBodyType(definition.createdByBodyType as BodyType);
    }
    
    return createdByQuery;
        
}

    //end added by Jelte

function createdBySelection(context is Context, definition is map) returns Query
{
    var createdByQuery;
 
    if (isAtVersionOrLater(context, FeatureScriptVersionNumber.V2793_BETTER_QV_SKETCH_IMPRINT_FILTERING))
    {
        createdByQuery = qCreatedBy(definition.createdByFeatures, definition.entityType);
        // There is a much easier way to filter edges/vertices from imprints: we subtract sketch entities belonging to sheet bodies.
        if (definition.entityType == EntityType.EDGE || definition.entityType == EntityType.VERTEX)
        {
            const edgesOrVerticesInSheetInSketch = createdByQuery->qSketchFilter(SketchObject.YES)->qBodyType(BodyType.SHEET);
            createdByQuery = createdByQuery->qSubtraction(edgesOrVerticesInSheetInSketch);
        }
    }
    else
    {
        createdByQuery = filterSketchEdgesAndVerticesFromSheetDeprecated(context, definition);
    }
    if (definition.filterConstruction)
    {
        createdByQuery = createdByQuery->qConstructionFilter(ConstructionObject.NO);
    }
    if (definition.filterByBodyType)
    {
        createdByQuery = createdByQuery->qBodyType(definition.createdByBodyType as BodyType);
    }
    
    return createdByQuery;
}

function remapAdditionalQuery(definition is map) returns map
{
    var remapped = {};
    const prefix = "addQ";
    const offset = 4;
    for (var key, value in definition)
    {
        if (!startsWith(key, prefix))
        {
            continue;
        }
        remapped[substring(key, offset)] = value;
    }
    return remapped;
}

function checkQueryVariableName(context is Context, name is string)
{
    verifyVariableNameIsValid(name, "name");
    var exists = false;
    try silent
    {
        getVariable(context, name);
        exists = true;
    }
    if (exists)
    {
        throw regenError(ErrorStringEnum.QUERY_VARIABLE_NAME_ALREADY_USED_IN_NON_QUERY_VARIABLE, ["name"]);
    }
}


/**
 * Returns a query that was previously stored in a variable of the given name.
 */
export function getQueryVariable(context is Context, name is string) returns Query
{
    return @getQueryVariable(context, { "name" : name, "defaultValue" : qNothing() });
}

/**
 * Saves a query in a variable with the given name.
 */
export function setQueryVariable(context is Context, name is string, value is Query)
{
    return setQueryVariable(context, name, "", value);
}

/**
 * Saves a query in a variable with the given name and description.
 */
export function setQueryVariable(context is Context, name is string, description is string, value is Query)
{
    return @setQueryVariable(context, { "name" : name, "description" : description, "value" : value });
}
