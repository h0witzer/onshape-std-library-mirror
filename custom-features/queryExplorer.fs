FeatureScript 1389;
import(path : "onshape/std/geometry.fs", version : "1389.0");
export import(path : "onshape/std/query.fs", version : "1389.0");
export import(path : "d3f4df7ed9c73a997830e981/9ca320bfa0c367881f72e43d/fbe970526c7c966766733d9f", version : "f4b04f32747184ab5434e913");

export enum QuerySeed
{
    annotation { "Name" : "Everything" }
    EVERYTHING,
    annotation { "Name" : "Select entities" }
    SELECTION,
    annotation { "Name" : "Select features" }
    FEATURE_SELECTION,
}

export enum EntityTypeSelection
{
    ANY,
    VERTEX,
    EDGE,
    FACE,
    BODY
}

export enum FilletFacesCompareType
{
    EQUAL,
    LESS_EQUAL,
    GREATER_EQUAL
}

export enum BodyTypeSelection
{
    ANY,
    POINT,
    WIRE,
    SHEET,
    SOLID
}

export enum DisplayResultWay
{
    REPORT,
    NOTICES,
    VARIABLE
}

export enum QueryFeatureSelection
{
    annotation { "Name" : "qSketchRegion" } // Require feature selection, but replace qCreatedBy with qSketchRegion, pass in entity type as usual
    SKETCH_REGION,
    annotation { "Name" : "qPatternInstances" } // same a qSketchRegion // feature
    PATTERN_INSTANCES,
    annotation { "Name" : "qCapEntity" } // feature
    CAP_ENTITY,
    annotation { "Name" : "qNonCapEntity" } // feature
    NON_CAP_ENTITY,
    annotation { "Name" : "qCreatedBy" } // feature
    CREATED_BY,
}

export enum QuerySelection
{
    annotation { "Name" : "NONE" }
    NONE,

    annotation { "Name" : "qSketchRegion" } // Require feature selection, but replace qCreatedBy with qSketchRegion, pass in entity type as usual
    SKETCH_REGION,
    annotation { "Name" : "qPatternInstances" } // same a qSketchRegion // feature
    PATTERN_INSTANCES,
    annotation { "Name" : "qCapEntity" } // feature
    CAP_ENTITY,
    annotation { "Name" : "qNonCapEntity" } // feature
    NON_CAP_ENTITY,
    annotation { "Name" : "qCreatedBy" } // feature
    CREATED_BY,
    annotation { "Name" : "qNthElement" }
    NTH_ELEMENT,

    // annotation { "Name" : "qEntityFilter" }
    // ENTITY_FILTER,


    //  annotation { "Name" : "qUnion" } // Maybe a separate feature can do the booleans between other queries???
    //  UNION,
    //  annotation { "Name" : "qIntersection" }
    //  INTERSECTION,
    //  annotation { "Name" : "qSubtraction" }
    //  SUBTRACTION,

    annotation { "Name" : "qOwnedByBody" }
    OWNED_BY_PART,
    annotation { "Name" : "qOwnerBody" }
    OWNER_PART,
    annotation { "Name" : "qAdjacent" }
    ADJACENT,

    //  annotation { "Name" : "qSomething?" } // Not yet implemented in standard library
    //  LOOP_AROUND_FACE,
    //  annotation { "Name" : "qSomething?" } // Not yet implemented in standard library
    //  SHELL_CONTAINING_FACE,

    annotation { "Name" : "qGeometry" }
    GEOMETRY,
    // annotation { "Name" : "qBodyType" }
    // BODY_TYPE,
    annotation { "Name" : "qParallelPlanes" }
    PLANE_NORMAL,

    //  annotation { "Name" : "qSomething?" } // Not yet implemented in standard library
    //  TANGENT_EDGES,
    //  annotation { "Name" : "qSomething?" } // Not yet implemented in standard library
    //  TANGENT_FACES,

    annotation { "Name" : "qConvexConnectedFaces" }
    CONVEX_CONNECTED_FACES,
    annotation { "Name" : "qConcaveConnectedFaces" }
    CONCAVE_CONNECTED_FACES,
    annotation { "Name" : "qTangentConnectedFaces" }
    TANGENT_CONNECTED_FACES,
    annotation { "Name" : "qLoopBoundedFaces" }
    LOOP_BOUNDED_FACES,
    annotation { "Name" : "qFaceOrEdgeBoundedFaces" }
    FACE_OR_EDGE_BOUNDED_FACES,
    annotation { "Name" : "qHoleFaces" }
    HOLE_FACES,
    annotation { "Name" : "qFilletFaces" }
    FILLET_FACES,
    annotation { "Name" : "qMatching" }
    PATTERN,
    annotation { "Name" : "qContainsPoint" }
    CONTAINS_POINT,
    annotation { "Name" : "qIntersectsLine" }
    INTERSECTS_LINE,
    annotation { "Name" : "qIntersectsPlane" }
    INTERSECTS_PLANE,
    annotation { "Name" : "qWithinRadius" }
    INTERSECTS_BALL,
    annotation { "Name" : "qClosestTo" }
    CLOSEST_TO,
    annotation { "Name" : "qFarthestAlong" }
    FARTHEST_ALONG,
    annotation { "Name" : "qLargest" }
    LARGEST,
    annotation { "Name" : "qSmallest" }
    SMALLEST,
    // annotation { "Name" : "qCoEdge" } // internal
    // COEDGE,
    annotation { "Name" : "qMateConnectorsOfParts" }
    MATE_CONNECTOR,
    annotation { "Name" : "qConstructionFilter" }
    CONSTRUCTION_FILTER,
    //  annotation { "Name" : "qSourceMesh" } // replace input? needs different filter? For now let's skip
    //  SOURCE_MESH,
    annotation { "Name" : "qActiveSheetMetalFilter" }
    ACTIVE_SM_FILTER,
    annotation { "Name" : "qCorrespondingInFlat" }
    CORRESPONDING_IN_FLAT,
    annotation { "Name" : "qPartsAttachedTo" }
    PARTS_ATTACHED_TO,
    annotation { "Name" : "qSheetMetalFlatFilter" }
    SM_FLAT_FILTER,
    annotation { "Name" : "qSketchFilter" }
    SKETCH_OBJECT_FILTER,
    annotation { "Name" : "qEdgeTopologyFilter" }
    EDGE_TOPOLOGY_FILTER,
    annotation { "Name" : "qCoincidesWithPlane" }
    COINCIDES_WITH_PLANE,
    annotation { "Name" : "qPlanesParallelToDirection" }
    PLANE_PARALLEL_DIRECTION,
    annotation { "Name" : "qFacesParallelToDirection" }
    FACE_PARALLEL_DIRECTION,
    annotation { "Name" : "qTangentConnectedEdges" }
    TANGENT_CONNECTED_EDGES,
    annotation { "Name" : "qLoopEdges" }
    LOOP_EDGES,
    annotation { "Name" : "qParallelEdges" }
    PARALLEL_EDGES,
    annotation { "Name" : "qContainedInCompositeParts" }
    CONTAINED_IN_COMPOSITE,
    annotation { "Name" : "qCompositePartsContaining" }
    COMPOSITE_CONTAINING,
    annotation { "Name" : "qUniqueVertices" }
    UNIQUE_VERTICES,

    //Not QueryType functions which return Query

    annotation { "Name" : "qAllNonMeshSolidBodies" }
    ALL_NON_MESH_SOLID_BODIES,
    annotation { "Name" : "qAllModifiableSolidBodies" }
    ALL_MODIFIABLE_SOLID_BODIES,
    annotation { "Name" : "qDependency" }
    DEPENDENCY,
    annotation { "Name" : "qLaminarDependency" }
    LAMINAR_DEPENDENCY,

    annotation { "Name" : "qFlattenedCompositeParts" }
    FLATTENED_COMPOSITE_PARTS,
    annotation { "Name" : "qConsumed" }
    CONSUMED,
    annotation { "Name" : "qMeshGeometryFilter" }
    MESH_GEOMETRY_FILTER,
    annotation { "Name" : "qModifiableEntityFilter" }
    MODIFABLE_ENTITY_FILTER,

    annotation { "Name" : "qHasAttribute" }
    HAS_ATTRIBUTE,
    annotation { "Name" : "qHasAttributeWithValue" }
    HAS_ATTRIBUTE_WITH_VALUE,
    annotation { "Name" : "qHasAttributeWithValueMatching" }
    HAS_ATTRIBUTE_WITH_VALUE_MATCHING,
}

export const NONNEGATIVE_COUNT_BOUNDS =
{
            (unitless) : [0, 0, 1e5]
        } as IntegerBoundSpec;

annotation { "Feature Type Name" : "Query Explorer", "Editing Logic Function" : "editFeatureLogic" }
export const queryExplorer = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Seed", "UIHint" : UIHint.SHOW_LABEL }
        definition.seed is QuerySeed;

        if (definition.seed == QuerySeed.SELECTION)
        {
            annotation { "Name" : "Selection", "Filter" : (EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY) && AllowFlattenedGeometry.YES }
            definition.seedQueryEntity is Query;
        }
        else if (definition.seed == QuerySeed.FEATURE_SELECTION)
        {
            annotation { "Name" : "Selection", "Filter" : (EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY) && AllowMeshGeometry.YES, "MaxNumberOfPicks" : 1 }
            definition.seedQueryFeature is FeatureList;
        }

        annotation { "Name" : "EntityType", "UIHint" : UIHint.SHOW_LABEL }
        definition.entityType is EntityTypeSelection;

        annotation { "Name" : "BodyType", "UIHint" : UIHint.SHOW_LABEL }
        definition.bodyType is BodyTypeSelection;

        annotation { "Group Name" : "Query filter 1", "Collapsed By Default" : false }
        {
            if (definition.seed == QuerySeed.SELECTION || definition.seed == QuerySeed.EVERYTHING)
            {
                annotation { "Name" : "Query" }
                definition.query1 is QuerySelection;

                if (definition.query1 == QuerySelection.PLANE_NORMAL || definition.query1 == QuerySelection.COINCIDES_WITH_PLANE || definition.query1 == QuerySelection.INTERSECTS_PLANE)
                {
                    annotation { "Name" : "Reference Plane", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
                    definition.plane1 is Query;
                }
                else if (definition.query1 == QuerySelection.PLANE_PARALLEL_DIRECTION || definition.query1 == QuerySelection.FACE_PARALLEL_DIRECTION || definition.query1 == QuerySelection.FARTHEST_ALONG)
                {
                    annotation { "Name" : "Direction", "Filter" : EntityType.EDGE || QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
                    definition.direction1 is Query;
                }
                else if (definition.query1 == QuerySelection.CONTAINS_POINT || definition.query1 == QuerySelection.CLOSEST_TO || definition.query1 == QuerySelection.INTERSECTS_BALL)
                {
                    annotation { "Name" : "Vertex", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
                    definition.vertex1 is Query;

                    if (definition.query1 == QuerySelection.INTERSECTS_BALL)
                    {
                        annotation { "Name" : "Radius" }
                        isLength(definition.radius1, NONNEGATIVE_LENGTH_BOUNDS);
                    }
                }
                else if (definition.query1 == QuerySelection.INTERSECTS_LINE)
                {
                    annotation { "Name" : "Line", "Filter" : EntityType.EDGE && GeometryType.LINE, "MaxNumberOfPicks" : 1 }
                    definition.line1 is Query;
                }
                else if (definition.query1 == QuerySelection.SM_FLAT_FILTER)
                {
                    annotation { "Name" : "Filter Flat", "UIHint" : UIHint.SHOW_LABEL }
                    definition.flatType1 is SMFlatType;
                }
                else if (definition.query1 == QuerySelection.GEOMETRY)
                {
                    annotation { "Name" : "Geometry Type", "UIHint" : UIHint.SHOW_LABEL }
                    definition.geometryType1 is GeometryType;
                }
                else if (definition.query1 == QuerySelection.EDGE_TOPOLOGY_FILTER)
                {
                    annotation { "Name" : "Edge Topology", "UIHint" : UIHint.SHOW_LABEL }
                    definition.edgeTopology1 is EdgeTopology;
                }
                else if (definition.query1 == QuerySelection.ACTIVE_SM_FILTER)
                {
                    annotation { "Name" : "Active sheet metal", "UIHint" : UIHint.SHOW_LABEL }
                    definition.activeSheetMetal1 is ActiveSheetMetal;
                }
                else if (definition.query1 == QuerySelection.CONSTRUCTION_FILTER)
                {
                    annotation { "Name" : "Construction Filter", "UIHint" : UIHint.SHOW_LABEL }
                    definition.constructionFilter1 is ConstructionObject;
                }
                else if (definition.query1 == QuerySelection.FILLET_FACES)
                {
                    annotation { "Name" : "Compare Type", "UIHint" : UIHint.SHOW_LABEL }
                    definition.compareType1 is FilletFacesCompareType;
                }
                else if (definition.query1 == QuerySelection.SKETCH_OBJECT_FILTER)
                {
                    annotation { "Name" : "Sketch Object Filter", "UIHint" : UIHint.SHOW_LABEL }
                    definition.sketchObjectFilter1 is SketchObject;
                }
                else if (definition.query1 == QuerySelection.OWNED_BY_PART)
                {
                    annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
                    definition.body1 is Query;
                }
                else if (definition.query1 == QuerySelection.NTH_ELEMENT)
                {
                    annotation { "Name" : "Index" }
                    isInteger(definition.nthIndex1, NONNEGATIVE_COUNT_BOUNDS);
                }
                else if (definition.query1 == QuerySelection.ADJACENT)
                {
                    annotation { "Name" : "Adjacency Type ", "UIHint" : UIHint.SHOW_LABEL }
                    definition.adjacencyType1 is AdjacencyType;
                    annotation { "Name" : "Entity Type ", "UIHint" : UIHint.SHOW_LABEL }
                    definition.adjacentEntityType1 is EntityTypeSelection;
                    
                }
                else if (definition.query1 == QuerySelection.MESH_GEOMETRY_FILTER)
                {
                    annotation { "Name" : "Mesh Geometry", "UIHint" : UIHint.SHOW_LABEL }
                    definition.meshGeometry1 is MeshGeometry;
                }
                else if (definition.query1 == QuerySelection.CONSUMED)
                {
                    annotation { "Name" : "Consumed", "UIHint" : UIHint.SHOW_LABEL }
                    definition.consumed1 is Consumed;
                }
                else if (definition.query1 == QuerySelection.HAS_ATTRIBUTE || definition.query1 == QuerySelection.HAS_ATTRIBUTE_WITH_VALUE || definition.query1 == QuerySelection.HAS_ATTRIBUTE_WITH_VALUE_MATCHING)
                {
                    annotation { "Name" : "Attribute name" }
                    definition.attributeName1 is string;
                    if (definition.query1 == QuerySelection.HAS_ATTRIBUTE_WITH_VALUE)
                    {
                        annotation { "Name" : "Attribute value (FS expression)" }
                        isAnything(definition.attributeValue1);
                    }
                    if (definition.query1 == QuerySelection.HAS_ATTRIBUTE_WITH_VALUE_MATCHING)
                    {
                        annotation { "Name" : "Criteria", "Item name" : "Criteria", "Item label template" : "#key : #value" }
                        definition.attributeValueMatches1 is array;
                        for (var match in definition.attributeValueMatches1)
                        {
                            annotation { "Name" : "Key" }
                            match.key is string;
                            annotation { "Name" : "Value (FS expression)" }
                            isAnything(match.value);
                        }
                    }
                }
            }
            else
            {
                annotation { "Name" : "Features" }
                definition.feature1 is QueryFeatureSelection;
                if (definition.feature1 == QueryFeatureSelection.SKETCH_REGION)
                {
                    annotation { "Name" : "Filter inner loops", "UIHint" : UIHint.SHOW_LABEL }
                    definition.filterInnerLoops1 is boolean;
                }
                else if (definition.feature1 == QueryFeatureSelection.CAP_ENTITY)
                {
                    annotation { "Name" : "Cap type", "UIHint" : UIHint.SHOW_LABEL }
                    definition.capType1 is CapType;
                }
                else if (definition.feature1 == QueryFeatureSelection.PATTERN_INSTANCES)
                {
                    annotation { "Name" : "Instance Names", "Item name" : "Name", "Item label template" : "Instance" }
                    definition.instanceNames1 is array;
                    for (var instance in definition.instanceNames1)
                    {
                        annotation { "Name" : "Instance name", "MinLength" : 1 }
                        instance.name1 is string;
                    }
                }
            }
        }
        if (definition.seed == QuerySeed.SELECTION || definition.seed == QuerySeed.EVERYTHING)
        {
            annotation { "Name" : "Query filter 2" }
            definition.addSecondFilter is boolean;

            if (definition.addSecondFilter)
            {
                annotation { "Group Name" : "Query filter 2", "Collapsed By Default" : false, "Driving Parameter" : "addSecondFilter" }
                {
                    if (definition.seed == QuerySeed.SELECTION || definition.seed == QuerySeed.EVERYTHING)
                    {
                        annotation { "Name" : "Query" }
                        definition.query2 is QuerySelection;

                        if (definition.query2 == QuerySelection.PLANE_NORMAL || definition.query2 == QuerySelection.COINCIDES_WITH_PLANE || definition.query2 == QuerySelection.INTERSECTS_PLANE)
                        {
                            annotation { "Name" : "Reference Plane", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
                            definition.plane2 is Query;
                        }
                        else if (definition.query2 == QuerySelection.PLANE_PARALLEL_DIRECTION || definition.query2 == QuerySelection.FACE_PARALLEL_DIRECTION || definition.query2 == QuerySelection.FARTHEST_ALONG)
                        {
                            annotation { "Name" : "Direction", "Filter" : EntityType.EDGE || QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
                            definition.direction2 is Query;
                        }
                        else if (definition.query2 == QuerySelection.CONTAINS_POINT || definition.query2 == QuerySelection.CLOSEST_TO || definition.query2 == QuerySelection.INTERSECTS_BALL)
                        {
                            annotation { "Name" : "Vertex", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
                            definition.vertex2 is Query;

                            if (definition.query2 == QuerySelection.INTERSECTS_BALL)
                            {
                                annotation { "Name" : "Radius" }
                                isLength(definition.radius2, NONNEGATIVE_LENGTH_BOUNDS);
                            }
                        }
                        else if (definition.query2 == QuerySelection.INTERSECTS_LINE)
                        {
                            annotation { "Name" : "Line", "Filter" : EntityType.EDGE && GeometryType.LINE, "MaxNumberOfPicks" : 1 }
                            definition.line2 is Query;
                        }
                        else if (definition.query2 == QuerySelection.SM_FLAT_FILTER)
                        {
                            annotation { "Name" : "Filter Flat", "UIHint" : UIHint.SHOW_LABEL }
                            definition.flatType2 is SMFlatType;
                        }
                        else if (definition.query2 == QuerySelection.GEOMETRY)
                        {
                            annotation { "Name" : "Geometry Type", "UIHint" : UIHint.SHOW_LABEL }
                            definition.geometryType2 is GeometryType;
                        }
                        else if (definition.query2 == QuerySelection.EDGE_TOPOLOGY_FILTER)
                        {
                            annotation { "Name" : "Edge Topology", "UIHint" : UIHint.SHOW_LABEL }
                            definition.edgeTopology2 is EdgeTopology;
                        }
                        else if (definition.query2 == QuerySelection.ACTIVE_SM_FILTER)
                        {
                            annotation { "Name" : "Active sheet metal", "UIHint" : UIHint.SHOW_LABEL }
                            definition.activeSheetMetal2 is ActiveSheetMetal;
                        }
                        else if (definition.query2 == QuerySelection.CONSTRUCTION_FILTER)
                        {
                            annotation { "Name" : "Construction Filter", "UIHint" : UIHint.SHOW_LABEL }
                            definition.constructionFilter2 is ConstructionObject;
                        }
                        else if (definition.query2 == QuerySelection.FILLET_FACES)
                        {
                            annotation { "Name" : "Compare Type", "UIHint" : UIHint.SHOW_LABEL }
                            definition.compareType2 is FilletFacesCompareType;
                        }
                        else if (definition.query2 == QuerySelection.SKETCH_OBJECT_FILTER)
                        {
                            annotation { "Name" : "Sketch Object Filter", "UIHint" : UIHint.SHOW_LABEL }
                            definition.sketchObjectFilter2 is SketchObject;
                        }
                        else if (definition.query2 == QuerySelection.OWNED_BY_PART)
                        {
                            annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
                            definition.body2 is Query;
                        }
                        else if (definition.query2 == QuerySelection.NTH_ELEMENT)
                        {
                            annotation { "Name" : "Index" }
                            isInteger(definition.nthIndex2, NONNEGATIVE_COUNT_BOUNDS);
                        }
                        else if (definition.query2 == QuerySelection.ADJACENT)
                        {
                            annotation { "Name" : "Adjacency Type ", "UIHint" : UIHint.SHOW_LABEL }
                            definition.adjacencyType2 is AdjacencyType;
                            annotation { "Name" : "Entity Type ", "UIHint" : UIHint.SHOW_LABEL }
                            definition.adjacentEntityType2 is EntityTypeSelection;
                        }
                        else if (definition.query2 == QuerySelection.MESH_GEOMETRY_FILTER)
                        {
                            annotation { "Name" : "Mesh Geometry", "UIHint" : UIHint.SHOW_LABEL }
                            definition.meshGeometry2 is MeshGeometry;
                        }
                        else if (definition.query2 == QuerySelection.CONSUMED)
                        {
                            annotation { "Name" : "Consumed", "UIHint" : UIHint.SHOW_LABEL }
                            definition.consumed2 is Consumed;
                        }
                    }
                }

                annotation { "Name" : "Query filter 3" }
                definition.addThirdFilter is boolean;

                if (definition.addThirdFilter == true)
                {
                    annotation { "Group Name" : "Query filter 3", "Collapsed By Default" : false, "Driving Parameter" : "addThirdFilter" }
                    {
                        if (definition.seed == QuerySeed.SELECTION || definition.seed == QuerySeed.EVERYTHING)
                        {
                            annotation { "Name" : "Query" }
                            definition.query3 is QuerySelection;

                            if (definition.query3 == QuerySelection.PLANE_NORMAL || definition.query3 == QuerySelection.COINCIDES_WITH_PLANE || definition.query3 == QuerySelection.INTERSECTS_PLANE)
                            {
                                annotation { "Name" : "Reference Plane", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
                                definition.plane3 is Query;
                            }
                            else if (definition.query3 == QuerySelection.PLANE_PARALLEL_DIRECTION || definition.query3 == QuerySelection.FACE_PARALLEL_DIRECTION || definition.query3 == QuerySelection.FARTHEST_ALONG)
                            {
                                annotation { "Name" : "Direction", "Filter" : EntityType.EDGE || QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
                                definition.direction3 is Query;
                            }
                            else if (definition.query3 == QuerySelection.CONTAINS_POINT || definition.query3 == QuerySelection.CLOSEST_TO || definition.query3 == QuerySelection.INTERSECTS_BALL)
                            {
                                annotation { "Name" : "Vertex", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
                                definition.vertex3 is Query;

                                if (definition.query3 == QuerySelection.INTERSECTS_BALL)
                                {
                                    annotation { "Name" : "Radius" }
                                    isLength(definition.radius3, NONNEGATIVE_LENGTH_BOUNDS);
                                }
                            }
                            else if (definition.query3 == QuerySelection.INTERSECTS_LINE)
                            {
                                annotation { "Name" : "Line", "Filter" : EntityType.EDGE && GeometryType.LINE, "MaxNumberOfPicks" : 1 }
                                definition.line3 is Query;
                            }
                            else if (definition.query3 == QuerySelection.SM_FLAT_FILTER)
                            {
                                annotation { "Name" : "Filter Flat", "UIHint" : UIHint.SHOW_LABEL }
                                definition.flatType3 is SMFlatType;
                            }
                            else if (definition.query3 == QuerySelection.GEOMETRY)
                            {
                                annotation { "Name" : "Geometry Type", "UIHint" : UIHint.SHOW_LABEL }
                                definition.geometryType3 is GeometryType;
                            }
                            else if (definition.query3 == QuerySelection.EDGE_TOPOLOGY_FILTER)
                            {
                                annotation { "Name" : "Edge Topology", "UIHint" : UIHint.SHOW_LABEL }
                                definition.edgeTopology3 is EdgeTopology;
                            }
                            else if (definition.query3 == QuerySelection.ACTIVE_SM_FILTER)
                            {
                                annotation { "Name" : "Active sheet metal", "UIHint" : UIHint.SHOW_LABEL }
                                definition.activeSheetMetal3 is ActiveSheetMetal;
                            }
                            else if (definition.query3 == QuerySelection.CONSTRUCTION_FILTER)
                            {
                                annotation { "Name" : "Construction Filter", "UIHint" : UIHint.SHOW_LABEL }
                                definition.constructionFilter3 is ConstructionObject;
                            }
                            else if (definition.query3 == QuerySelection.FILLET_FACES)
                            {
                                annotation { "Name" : "Compare Type", "UIHint" : UIHint.SHOW_LABEL }
                                definition.compareType3 is FilletFacesCompareType;
                            }
                            else if (definition.query3 == QuerySelection.SKETCH_OBJECT_FILTER)
                            {
                                annotation { "Name" : "Sketch Object Filter", "UIHint" : UIHint.SHOW_LABEL }
                                definition.sketchObjectFilter3 is SketchObject;
                            }
                            else if (definition.query3 == QuerySelection.OWNED_BY_PART)
                            {
                                annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
                                definition.body3 is Query;
                            }
                            else if (definition.query3 == QuerySelection.NTH_ELEMENT)
                            {
                                annotation { "Name" : "Index" }
                                isInteger(definition.nthIndex3, NONNEGATIVE_COUNT_BOUNDS);
                            }
                            else if (definition.query3 == QuerySelection.ADJACENT)
                            {
                                annotation { "Name" : "Adjacency Type ", "UIHint" : UIHint.SHOW_LABEL }
                                definition.adjacencyType3 is AdjacencyType;
                                annotation { "Name" : "Entity Type ", "UIHint" : UIHint.SHOW_LABEL }
                                definition.adjacentEntityType3 is EntityTypeSelection;
                            }
                            else if (definition.query3 == QuerySelection.MESH_GEOMETRY_FILTER)
                            {
                                annotation { "Name" : "Mesh Geometry", "UIHint" : UIHint.SHOW_LABEL }
                                definition.meshGeometry3 is MeshGeometry;
                            }
                            else if (definition.query3 == QuerySelection.CONSUMED)
                            {
                                annotation { "Name" : "Consumed", "UIHint" : UIHint.SHOW_LABEL }
                                definition.consumed3 is Consumed;
                            }
                        }
                    }

                    annotation { "Name" : "Query filter 4" }
                    definition.addFourthFilter is boolean;

                    if (definition.addFourthFilter == true)
                    {
                        annotation { "Group Name" : "Query filter 4", "Collapsed By Default" : false, "Driving Parameter" : "addFourthFilter" }
                        {
                            if (definition.seed == QuerySeed.SELECTION || definition.seed == QuerySeed.EVERYTHING)
                            {
                                annotation { "Name" : "Query" }
                                definition.query4 is QuerySelection;

                                if (definition.query4 == QuerySelection.PLANE_NORMAL || definition.query4 == QuerySelection.COINCIDES_WITH_PLANE || definition.query4 == QuerySelection.INTERSECTS_PLANE)
                                {
                                    annotation { "Name" : "Reference Plane", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
                                    definition.plane4 is Query;
                                }
                                else if (definition.query4 == QuerySelection.PLANE_PARALLEL_DIRECTION || definition.query4 == QuerySelection.FACE_PARALLEL_DIRECTION || definition.query4 == QuerySelection.FARTHEST_ALONG)
                                {
                                    annotation { "Name" : "Direction", "Filter" : EntityType.EDGE || QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
                                    definition.direction4 is Query;
                                }
                                else if (definition.query4 == QuerySelection.CONTAINS_POINT || definition.query4 == QuerySelection.CLOSEST_TO || definition.query4 == QuerySelection.INTERSECTS_BALL)
                                {
                                    annotation { "Name" : "Vertex", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
                                    definition.vertex4 is Query;

                                    if (definition.query4 == QuerySelection.INTERSECTS_BALL)
                                    {
                                        annotation { "Name" : "Radius" }
                                        isLength(definition.radius4, NONNEGATIVE_LENGTH_BOUNDS);
                                    }
                                }
                                else if (definition.query4 == QuerySelection.INTERSECTS_LINE)
                                {
                                    annotation { "Name" : "Line", "Filter" : EntityType.EDGE && GeometryType.LINE, "MaxNumberOfPicks" : 1 }
                                    definition.line4 is Query;
                                }
                                else if (definition.query4 == QuerySelection.SM_FLAT_FILTER)
                                {
                                    annotation { "Name" : "Filter Flat", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.flatType4 is SMFlatType;
                                }
                                else if (definition.query4 == QuerySelection.GEOMETRY)
                                {
                                    annotation { "Name" : "Geometry Type", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.geometryType4 is GeometryType;
                                }
                                else if (definition.query4 == QuerySelection.EDGE_TOPOLOGY_FILTER)
                                {
                                    annotation { "Name" : "Edge Topology", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.edgeTopology4 is EdgeTopology;
                                }
                                else if (definition.query4 == QuerySelection.ACTIVE_SM_FILTER)
                                {
                                    annotation { "Name" : "Active sheet metal", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.activeSheetMetal4 is ActiveSheetMetal;
                                }
                                else if (definition.query4 == QuerySelection.CONSTRUCTION_FILTER)
                                {
                                    annotation { "Name" : "Construction Filter", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.constructionFilter4 is ConstructionObject;
                                }
                                else if (definition.query4 == QuerySelection.FILLET_FACES)
                                {
                                    annotation { "Name" : "Compare Type", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.compareType4 is FilletFacesCompareType;
                                }
                                else if (definition.query4 == QuerySelection.SKETCH_OBJECT_FILTER)
                                {
                                    annotation { "Name" : "Sketch Object Filter", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.sketchObjectFilter4 is SketchObject;
                                }
                                else if (definition.query4 == QuerySelection.OWNED_BY_PART)
                                {
                                    annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
                                    definition.body4 is Query;
                                }
                                else if (definition.query4 == QuerySelection.NTH_ELEMENT)
                                {
                                    annotation { "Name" : "Index" }
                                    isInteger(definition.nthIndex4, NONNEGATIVE_COUNT_BOUNDS);
                                }
                                else if (definition.query4 == QuerySelection.ADJACENT)
                                {
                                    annotation { "Name" : "Adjacency Type ", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.adjacencyType4 is AdjacencyType;
                                    annotation { "Name" : "Entity Type ", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.adjacentEntityType4 is EntityTypeSelection;
                                }
                                else if (definition.query4 == QuerySelection.MESH_GEOMETRY_FILTER)
                                {
                                    annotation { "Name" : "Mesh Geometry", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.meshGeometry4 is MeshGeometry;
                                }
                                else if (definition.query4 == QuerySelection.CONSUMED)
                                {
                                    annotation { "Name" : "Consumed", "UIHint" : UIHint.SHOW_LABEL }
                                    definition.consumed4 is Consumed;
                                }
                            }
                        }

                        annotation { "Name" : "Query filter 5" }
                        definition.addFifthFilter is boolean;

                        if (definition.addFifthFilter == true)
                        {
                            annotation { "Group Name" : "Query filter 5", "Collapsed By Default" : false, "Driving Parameter" : "addFifthFilter" }
                            {
                                if (definition.seed == QuerySeed.SELECTION || definition.seed == QuerySeed.EVERYTHING)
                                {
                                    annotation { "Name" : "Query" }
                                    definition.query5 is QuerySelection;

                                    if (definition.query5 == QuerySelection.PLANE_NORMAL || definition.query5 == QuerySelection.COINCIDES_WITH_PLANE || definition.query5 == QuerySelection.INTERSECTS_PLANE)
                                    {
                                        annotation { "Name" : "Reference Plane", "Filter" : EntityType.FACE && GeometryType.PLANE, "MaxNumberOfPicks" : 1 }
                                        definition.plane5 is Query;
                                    }
                                    else if (definition.query5 == QuerySelection.PLANE_PARALLEL_DIRECTION || definition.query5 == QuerySelection.FACE_PARALLEL_DIRECTION || definition.query5 == QuerySelection.FARTHEST_ALONG)
                                    {
                                        annotation { "Name" : "Direction", "Filter" : EntityType.EDGE || QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
                                        definition.direction5 is Query;
                                    }
                                    else if (definition.query5 == QuerySelection.CONTAINS_POINT || definition.query5 == QuerySelection.CLOSEST_TO || definition.query5 == QuerySelection.INTERSECTS_BALL)
                                    {
                                        annotation { "Name" : "Vertex", "Filter" : EntityType.VERTEX, "MaxNumberOfPicks" : 1 }
                                        definition.vertex5 is Query;

                                        if (definition.query5 == QuerySelection.INTERSECTS_BALL)
                                        {
                                            annotation { "Name" : "Radius" }
                                            isLength(definition.radius5, NONNEGATIVE_LENGTH_BOUNDS);
                                        }
                                    }
                                    else if (definition.query5 == QuerySelection.INTERSECTS_LINE)
                                    {
                                        annotation { "Name" : "Line", "Filter" : EntityType.EDGE && GeometryType.LINE, "MaxNumberOfPicks" : 1 }
                                        definition.line5 is Query;
                                    }
                                    else if (definition.query5 == QuerySelection.SM_FLAT_FILTER)
                                    {
                                        annotation { "Name" : "Filter Flat", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.flatType5 is SMFlatType;
                                    }
                                    else if (definition.query5 == QuerySelection.GEOMETRY)
                                    {
                                        annotation { "Name" : "Geometry Type", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.geometryType5 is GeometryType;
                                    }
                                    else if (definition.query5 == QuerySelection.EDGE_TOPOLOGY_FILTER)
                                    {
                                        annotation { "Name" : "Edge Topology", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.edgeTopology5 is EdgeTopology;
                                    }
                                    else if (definition.query5 == QuerySelection.ACTIVE_SM_FILTER)
                                    {
                                        annotation { "Name" : "Active sheet metal", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.activeSheetMetal5 is ActiveSheetMetal;
                                    }
                                    else if (definition.query5 == QuerySelection.CONSTRUCTION_FILTER)
                                    {
                                        annotation { "Name" : "Construction Filter", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.constructionFilter5 is ConstructionObject;
                                    }
                                    else if (definition.query5 == QuerySelection.FILLET_FACES)
                                    {
                                        annotation { "Name" : "Compare Type", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.compareType5 is FilletFacesCompareType;
                                    }
                                    else if (definition.query5 == QuerySelection.SKETCH_OBJECT_FILTER)
                                    {
                                        annotation { "Name" : "Sketch Object Filter", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.sketchObjectFilter5 is SketchObject;
                                    }
                                    else if (definition.query5 == QuerySelection.OWNED_BY_PART)
                                    {
                                        annotation { "Name" : "Body", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
                                        definition.body5 is Query;
                                    }
                                    else if (definition.query5 == QuerySelection.NTH_ELEMENT)
                                    {
                                        annotation { "Name" : "Index" }
                                        isInteger(definition.nthIndex5, NONNEGATIVE_COUNT_BOUNDS);
                                    }
                                    else if (definition.query5 == QuerySelection.ADJACENT)
                                    {
                                        annotation { "Name" : "Adjacency Type ", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.adjacencyType5 is AdjacencyType;
                                        annotation { "Name" : "Entity Type ", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.adjacentEntityType5 is EntityTypeSelection;
                                    }
                                    else if (definition.query5 == QuerySelection.MESH_GEOMETRY_FILTER)
                                    {
                                        annotation { "Name" : "Mesh Geometry", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.meshGeometry5 is MeshGeometry;
                                    }
                                    else if (definition.query5 == QuerySelection.CONSUMED)
                                    {
                                        annotation { "Name" : "Consumed", "UIHint" : UIHint.SHOW_LABEL }
                                        definition.consumed5 is Consumed;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        if (definition.seed == QuerySeed.SELECTION || definition.seed == QuerySeed.EVERYTHING)
        {
            annotation { "Name" : "Units ", "UIHint" : UIHint.SHOW_LABEL }
            definition.queryUnits is QueryUnits;
        }

        annotation { "Name" : "How to show the result" }
        definition.displayResult is DisplayResultWay;

        if (definition.displayResult == DisplayResultWay.VARIABLE)
        {
            annotation { "Name" : "Final query" } // Either setFeatureComputedParameter or editing logic will set this
            definition.finalQuery is string;
        }

        annotation { "Name" : "Data", "UIHint" : UIHint.ALWAYS_HIDDEN }
        isAnything(definition.editingLogicData);
    }
    {
        var isQueryFeatureSelection = checkSelectionType(definition);

        if ((definition.seed == QuerySeed.SELECTION || definition.seed == QuerySeed.EVERYTHING) && isQueryFeatureSelection.result)
        {
            const message = "You cannot use a '" ~ isQueryFeatureSelection.functionName ~ "' function at 'Query filter " ~ isQueryFeatureSelection["number"] ~ "' with " ~ definition.seed ~ " type of 'Seed'. Use FEATURE_SELECTION instead.";
            reportFeatureInfo(context, id, message);

            // throw regenError("", ["QuerySeed"]);

            return;
        }

        var result;
        if (definition.editingLogicData == 0 || definition.editingLogicData.Query == undefined)
        {
            try
            {
                result = parseDefinition(context, definition);
            }
            catch (error)
            {
                println(error);
                reportFeatureInfo(context, id, error.customMessage);
                return;
            }
        }
        else
        {
            result = definition.editingLogicData;

        }

        if (!result.processed)
        {
            reportFeatureInfo(context, id, "This query type not implemented yet");
        }
        else
        {
            debug(context, result.outputQuery as Query);
            if (definition.displayResult == DisplayResultWay.NOTICES)
            {
                reportFeatureInfo(context, id, "Please open FeatureSctipt notices to see final query");
                println("▼▼▼▼ Final query ▼▼▼▼\n" ~ result.outputQueryString
                    ~ "\n▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲▲");
            }
            else if (definition.displayResult == DisplayResultWay.REPORT)
            {
                reportFeatureInfo(context, id, result.outputQueryString);
            }

            // println("result.queryType=" ~ result.queryType);

            // if (result.queryType != undefined && result.queryType != QuerySelection.NONE && definition.addSecondFilter != true)
            // {
            //     var queryHint = getQueryHelp(result.queryType);
            //     // var queryHint = getQueryHelp(QueryType.NTH_ELEMENT);
            //     if (queryHint != undefined)
            //     {
            //         reportFeatureInfo(context, id, queryHint);
            //     }
            // }
            // getQueryHelp(QuerySelection.NOTHING);
            // getQueryHelp(QueryType.NTH_ELEMENT);
        }
    });

// output might be qContainsPoint(qCoincidesWithPlane(qBodyType(seed1_REPLACE_ME, BodyType.SOLID), plane_REPLACE_ME), point_REPLACE_ME);

//
function checkSelectionType(definition is map)
{
    var toolTips = {
        QuerySelection.NON_CAP_ENTITY : "qNonCapEntity",
        QuerySelection.CAP_ENTITY : "qCapEntity",
        QuerySelection.SKETCH_REGION : "qSketchRegion",
        QuerySelection.PATTERN_INSTANCES : "qPatternInstances",
        QuerySelection.CREATED_BY : "qCreatedBy"
    };
    var queryFiltersCount = countQueryFilters(definition);
    for (var i = 1; i <= queryFiltersCount; i += 1)
    {
        if (definition["query" ~ i] == QuerySelection.SKETCH_REGION || definition["query" ~ i] == QuerySelection.PATTERN_INSTANCES || definition["query" ~ i] == QuerySelection.CAP_ENTITY ||
            definition["query" ~ i] == QuerySelection.NON_CAP_ENTITY || definition["query" ~ i] == QuerySelection.CREATED_BY)
        {
            return { "result" : true, "number" : i, "functionName" : toolTips[definition["query" ~ i]] };
        }
    }
    return { "result" : false };
}

export function editFeatureLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, specified is map) returns map
{
    try
    {
        var result = parseDefinition(context, definition);
        // println(result.outputQueryString);
        if (definition.displayResult == DisplayResultWay.VARIABLE)
        {
            definition.finalQuery = result.outputQueryString;
        }

        definition.editingLogicData = result;
    }
    catch (error)
    {
        definition.finalQuery = "";
        definition.editingLogicData = {};
    }
    return definition;
}

function parseDefinition(context is Context, definition is map) returns map
{
    var result = makeSeedQuery(context, definition);
    if (!(result.outputQuery is FeatureList)) // For feature lists, parseQuerySelection handles entity type and body type filters
    {
        if (result.filteredByEntity != true)
        {
            result = parseEntityType(context, definition, result.outputQuery, result.outputQueryString, result.queryType);
        }
        result = parseBodyType(context, definition, result.outputQuery, result.outputQueryString, result.queryType);
    }

    try
    {
        result = parseQuerySelection(context, definition, result.outputQuery, result.outputQueryString, result.queryType);
    }
    catch (error)
    {
        throw error;
    }

    const processed = result.processed;
    const queryType = result.queryType;

    var testFilters = checkSelectionType(definition);
    if (testFilters.result == true)
    {
        result.outputQueryString = '';
    }

    // println("parseDefinition result.queryType=" ~ result.queryType);

    return { "outputQuery" : result.outputQuery, "outputQueryString" : result.outputQueryString, "processed" : processed, "queryType" : queryType };
}

function makeSeedQuery(context is Context, definition is map) returns map
{
    var result = {};
    var entityTypeResult = EntityTypeSelectionConverter(definition.entityType);
    if (definition.seed == QuerySeed.EVERYTHING)
    {
        if (entityTypeResult.converted)
        {
            result.outputQuery = qEverything(entityTypeResult.result);
            result.outputQueryString = "qEverything(EntityType." ~ entityTypeResult.result ~ ")";
            result.filteredByEntity = true;
        }
        else
        {
            result.outputQuery = qEverything();
            result.outputQueryString = "qEverything()";
        }
    } else {
        result.outputQuery = definition.seed == QuerySeed.FEATURE_SELECTION ? definition.seedQueryFeature : definition.seedQueryEntity;
        result.outputQueryString = "seed1_REPLACE_ME";
    }

    result.queryType = definition.query1;
    return result;
}

function countQueryFilters(definition is map)
{
    var queryFilterCount = 1;
    if (definition.seed != QuerySeed.FEATURE_SELECTION)
    {
        if (definition.addSecondFilter == true)
        {
            queryFilterCount += 1;
            if (definition.addThirdFilter == true)
            {
                queryFilterCount += 1;
                if (definition.addFourthFilter == true)
                {
                    queryFilterCount += 1;
                    if (definition.addFifthFilter == true)
                    {
                        queryFilterCount += 1;
                    }
                }
            }
        }
    }

    return queryFilterCount;
}

function parseQuerySelection(context is Context, definition is map, outputQuery, outputQueryString is string, queryType) returns map
{
    var result = { "processed" : false, "outputQuery" : outputQuery, "outputQueryString" : outputQueryString, "queryType" : queryType, "filteredByEntity" : false };

    var queryFiltersCount = countQueryFilters(definition);

    for (var queryNumber = 1; queryNumber <= queryFiltersCount; queryNumber += 1)
    {
        if (definition.seed == QuerySeed.FEATURE_SELECTION && queryNumber == 1)
        {
            if (!result.processed)
            {
                result = parseFeatureQuerySeed(context, definition, result);
            }
        }
        else
        {
            result = parseQueryNone(context, definition, result, queryNumber);
            if (!result.processed)
            {
                result = parseQueryPlane(context, definition, result, queryNumber);
            }
            if (!result.processed)
            {
                result = parseQueryDirection(context, definition, result, queryNumber);
            }
            if (!result.processed)
            {
                result = parseQueryPoint(context, definition, result, queryNumber);
            }
            if (!result.processed)
            {
                result = parseQueryLine(context, definition, result, queryNumber);
            }
            if (!result.processed)
            {
                try
                {
                    result = parseQueryWithoutAdditionalLogic(context, definition, result, queryNumber);
                }
                catch (error)
                {
                    throw error;
                }
            }
        }
        if (queryNumber != queryFiltersCount)
        {
            result.processed = false;
        }
    }
    return result;
}

function parseFeatureQuerySeed(context is Context, definition is map, result is map) returns map
{
    if (size(result.outputQuery) == 0)
    {
        throw regenError("Select feature", ["seedQueryFeature"]);
    }
    var featureId = keys(result.outputQuery)[0];
    var entityType = EntityTypeSelectionConverter(definition.entityType);
    if (definition.feature1 == QueryFeatureSelection.CREATED_BY)
    {
        if (entityType.converted)
        {
            result.outputQuery = qCreatedBy(featureId, entityType.result);
            result.outputQueryString = "qCreatedBy(" ~ featureId ~ ", EntityType." ~ entityType.result ~ ")";
            result.filteredByEntity = true;
        }
        else
        {
            result.outputQuery = qCreatedBy(featureId);
            result.outputQueryString = "qCreatedBy(" ~ featureId ~ ")";
        }

        result.processed = true;
        result.queryType = QuerySelection.CREATED_BY;
    }
    if (definition.feature1 == QueryFeatureSelection.NON_CAP_ENTITY)
    {
        if (entityType.converted)
        {
            result.outputQuery = qNonCapEntity(featureId, entityType.result);
            result.outputQueryString = "qNonCapEntity(" ~ featureId ~ ", EntityType." ~ entityType.result ~ ")";
            result.filteredByEntity = true;
        }
        else
        {
            result.outputQuery = qNonCapEntity(featureId);
            result.outputQueryString = "qNonCapEntity(" ~ featureId ~ ")";
        }

        result.processed = true;
        result.queryType = QuerySelection.NON_CAP_ENTITY;
    }
    else if (definition.feature1 == QueryFeatureSelection.SKETCH_REGION)
    {
        result.outputQuery = qSketchRegion(featureId, definition.filterInnerLoops1);
        result.outputQueryString = "qSketchRegion(" ~ featureId ~ ", " ~ definition.filterInnerLoops1 ~ ")";
        result.processed = true;
        result.queryType = QuerySelection.SKETCH_REGION;

    }
    else if (definition.feature1 == QueryFeatureSelection.CAP_ENTITY)
    {
        if (entityType.converted)
        {
            result.outputQuery = qCapEntity(featureId, definition.capType1, entityType.result);
            result.outputQueryString = "qCapEntity(" ~ featureId ~ ", CapType." ~ definition.capType1 ~ ", EntityType." ~ entityType.result ~ ")";
            result.filteredByEntity = true;
        }
        else
        {
            result.outputQuery = qCapEntity(featureId, definition.capType1);
            result.outputQueryString = "qCapEntity(" ~ featureId ~ ", CapType." ~ definition.capType1 ~ ")";
        }

        result.processed = true;
        result.queryType = QuerySelection.CAP_ENTITY;
    }
    else if (definition.feature1 == QueryFeatureSelection.PATTERN_INSTANCES)
    {
        if (definition.entityType != EntityTypeSelection.ANY)
        {
            var filter = definition.entityType as EntityType;

            var arrayOfNames = mapArray(definition.instanceNames1, function(element)
            {
                return element.name1;
            });

            result.outputQuery = qPatternInstances(featureId, arrayOfNames, filter);
            result.outputQueryString = "qPatternInstances(" ~ featureId ~ "," ~ arrayOfNames ~ ", EntityType." ~ filter ~ ")";
            result.processed = true;
            result.filteredByEntity = true;
            result.queryType = QuerySelection.PATTERN_INSTANCES;
        }
        else
        {
            throw regenError("Please select other Entity type", ["entityType"]);
        }
    }
    const bodyTypeResult = parseBodyType(context, definition, result.outputQuery, result.outputQueryString, result.queryType);
    result.outputQuery = bodyTypeResult.outputQuery;
    result.outputQueryString = bodyTypeResult.outputQueryString;

    return result;
}

function parseQueryNone(context is Context, definition is map, result is map, queryNumber is number) returns map
{
    if (definition["query" ~ queryNumber] == QuerySelection.NONE)
    {
        result.processed = true;
    }
    return result;
}

function parseQueryWithoutAdditionalLogic(context is Context, definition is map, result is map, queryNumber is number) returns map
{
    var entityType = EntityTypeSelectionConverter(definition.entityType);
    if (definition["query" ~ queryNumber] == QuerySelection.COMPOSITE_CONTAINING)
    {
        result.outputQuery = qCompositePartsContaining(result.outputQuery);
        result.outputQueryString = "qCompositePartsContaining(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.CONTAINED_IN_COMPOSITE)
    {
        result.outputQuery = qContainedInCompositeParts(result.outputQuery);
        result.outputQueryString = "qContainedInCompositeParts(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.CONSUMED)
    {
        result.outputQuery = qConsumed(result.outputQuery, definition["consumed" ~ queryNumber]);
        result.outputQueryString = "qConsumed(" ~ result.outputQueryString ~ ", Consumed." ~ definition["consumed" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.FLATTENED_COMPOSITE_PARTS)
    {
        result.outputQuery = qFlattenedCompositeParts(result.outputQuery);
        result.outputQueryString = "qFlattenedCompositeParts(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.MODIFABLE_ENTITY_FILTER)
    {
        result.outputQuery = qModifiableEntityFilter(result.outputQuery);
        result.outputQueryString = "qModifiableEntityFilter(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.MESH_GEOMETRY_FILTER)
    {
        result.outputQuery = qMeshGeometryFilter(result.outputQuery, definition["meshGeometry" ~ queryNumber]);
        result.outputQueryString = "qMeshGeometryFilter(" ~ result.outputQueryString ~ ", MeshGeometry." ~ definition["meshGeometry" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    // else if (definition["query" ~ queryNumber] == QuerySelection.SOURCE_MESH)
    // {
    //     result.outputQuery = qSourceMesh(result.outputQuery);
    //     result.outputQueryString = "qSourceMesh(" ~ result.outputQueryString ~ ")";
    //     result.processed = true;
    // }
    else if (definition["query" ~ queryNumber] == QuerySelection.LAMINAR_DEPENDENCY)
    {
        result.outputQuery = qLaminarDependency(result.outputQuery);
        result.outputQueryString = "qLaminarDependency(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.DEPENDENCY)
    {
        result.outputQuery = qDependency(result.outputQuery);
        result.outputQueryString = "qDependency(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.ALL_MODIFIABLE_SOLID_BODIES)
    {
        result.outputQuery = qAllModifiableSolidBodies();
        result.outputQueryString = "qAllModifiableSolidBodies()";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.ALL_NON_MESH_SOLID_BODIES)
    {
        result.outputQuery = qAllNonMeshSolidBodies();
        result.outputQueryString = "qAllNonMeshSolidBodies()";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.CONSTRUCTION_FILTER)
    {
        result.outputQuery = qConstructionFilter(result.outputQuery, definition["constructionFilter" ~ queryNumber]);
        result.outputQueryString = "qConstructionFilter(" ~ result.outputQueryString ~ ", ConstructionObject." ~ definition["constructionFilter" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.ACTIVE_SM_FILTER)
    {
        result.outputQuery = qActiveSheetMetalFilter(result.outputQuery, definition["activeSheetMetal" ~ queryNumber]);
        result.outputQueryString = "qActiveSheetMetalFilter(" ~ result.outputQueryString ~ ", ActiveSheetMetal." ~ definition["activeSheetMetal" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.OWNER_PART)
    {
        result.outputQuery = qOwnerBody(result.outputQuery);
        result.outputQueryString = "qOwnerBody(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.EDGE_TOPOLOGY_FILTER)
    {
        result.outputQuery = qEdgeTopologyFilter(result.outputQuery, definition["edgeTopology" ~ queryNumber]);
        result.outputQueryString = "qEdgeTopologyFilter(" ~ result.outputQueryString ~ ", EdgeTopology." ~ definition["edgeTopology" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.GEOMETRY)
    {
        result.outputQuery = qGeometry(result.outputQuery, definition["geometryType" ~ queryNumber]);
        result.outputQueryString = "qGeometry(" ~ result.outputQueryString ~ ", GeometryType." ~ definition["geometryType" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.SM_FLAT_FILTER)
    {
        result.outputQuery = qSheetMetalFlatFilter(result.outputQuery, definition["flatType" ~ queryNumber]);
        result.outputQueryString = "qSheetMetalFlatFilter(" ~ result.outputQueryString ~ ", SMFlatType." ~ definition["flatType" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.CORRESPONDING_IN_FLAT)
    {
        result.outputQuery = qCorrespondingInFlat(result.outputQuery);
        result.outputQueryString = "qCorrespondingInFlat(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.CONVEX_CONNECTED_FACES)
    {
        result.outputQuery = qConvexConnectedFaces(result.outputQuery);
        result.outputQueryString = "qConvexConnectedFaces(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.PARTS_ATTACHED_TO)
    {
        result.outputQuery = qPartsAttachedTo(result.outputQuery);
        result.outputQueryString = "qPartsAttachedTo(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.CONCAVE_CONNECTED_FACES)
    {
        result.outputQuery = qConcaveConnectedFaces(result.outputQuery);
        result.outputQueryString = "qConcaveConnectedFaces(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.TANGENT_CONNECTED_FACES)
    {
        result.outputQuery = qTangentConnectedFaces(result.outputQuery);
        result.outputQueryString = "qTangentConnectedFaces(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.TANGENT_CONNECTED_EDGES)
    {
        result.outputQuery = qTangentConnectedEdges(result.outputQuery);
        result.outputQueryString = "qTangentConnectedEdges(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.LOOP_EDGES)
    {
        result.outputQuery = qLoopEdges(result.outputQuery);
        result.outputQueryString = "qLoopEdges(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.PARALLEL_EDGES)
    {
        result.outputQuery = qParallelEdges(result.outputQuery);
        result.outputQueryString = "qParallelEdges(qEntityFilter(" ~ result.outputQueryString ~ ", EntityType.EDGE))";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.LOOP_BOUNDED_FACES)
    {
        result.outputQuery = qLoopBoundedFaces(result.outputQuery);
        result.outputQueryString = "qLoopBoundedFaces(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.FACE_OR_EDGE_BOUNDED_FACES)
    {
        result.outputQuery = qFaceOrEdgeBoundedFaces(result.outputQuery);
        result.outputQueryString = "qFaceOrEdgeBoundedFaces(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.HOLE_FACES)
    {
        result.outputQuery = qHoleFaces(result.outputQuery);
        result.outputQueryString = "qHoleFaces(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.UNIQUE_VERTICES)
    {
        result.outputQuery = qUniqueVertices(result.outputQuery);
        result.outputQueryString = "qUniqueVertices(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.MATE_CONNECTOR)
    {
        result.outputQuery = qMateConnectorsOfParts(result.outputQuery);
        result.outputQueryString = "qMateConnectorsOfParts(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.PATTERN)
    {
        result.outputQuery = qMatching(result.outputQuery);
        result.outputQueryString = "qMatching(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.LARGEST)
    {
        result.outputQuery = qLargest(result.outputQuery);
        result.outputQueryString = "qLargest(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.SMALLEST)
    {
        result.outputQuery = qSmallest(result.outputQuery);
        result.outputQueryString = "qSmallest(" ~ result.outputQueryString ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.FILLET_FACES)
    {
        var filter = definition["compareType" ~ queryNumber] as CompareType;
        result.outputQuery = qFilletFaces(result.outputQuery, filter);
        result.outputQueryString = "qFilletFaces(" ~ result.outputQueryString ~ ", CompareType." ~ filter ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.SKETCH_OBJECT_FILTER)
    {
        result.outputQuery = qSketchFilter(result.outputQuery, definition["sketchObjectFilter" ~ queryNumber]);
        result.outputQueryString = "qSketchFilter(" ~ result.outputQueryString ~ ", SketchObject." ~ definition["sketchObjectFilter" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.OWNED_BY_PART)
    {
        result.outputQuery = qOwnedByBody(result.outputQuery, definition["body" ~ queryNumber]);
        result.outputQueryString = "qOwnedByBody(" ~ result.outputQueryString ~ ", Body_REPLACE_ME)";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.NTH_ELEMENT)
    {
        result.outputQuery = qNthElement(result.outputQuery, definition["nthIndex" ~ queryNumber]);
        result.outputQueryString = "qNthElement(" ~ result.outputQueryString ~ ", " ~ definition["nthIndex" ~ queryNumber] ~ ")";
        result.processed = true;
    }
    else if (definition["query" ~ queryNumber] == QuerySelection.ADJACENT)
    {
        const adjacentEntityType = EntityTypeSelectionConverter(definition["adjacentEntityType" ~ queryNumber]);
        if (adjacentEntityType.converted)
        {
            if (adjacentEntityType.result == EntityType.BODY)
            {
                throw regenError("Adjacency incompatible with BODY EntityType");
            }
            if (adjacentEntityType.result == EntityType.VERTEX && definition["adjacencyType" ~ queryNumber] == AdjacencyType.EDGE)
            {
                throw regenError("Entities cannot have EDGE AdjacencyType with vertices, use VERTEX AdjacencyType instead");
            }
            result.outputQuery = qAdjacent(result.outputQuery, definition["adjacencyType" ~ queryNumber], adjacentEntityType.result);
            result.outputQueryString = "qAdjacent(" ~ result.outputQueryString ~ ", AdjacencyType." ~ definition["adjacencyType" ~ queryNumber] ~ ", EntityType." ~ adjacentEntityType.result ~ ")";
            result.filteredByEntity = true;
        }
        else
        {
            result.outputQuery = qAdjacent(result.outputQuery, definition["adjacencyType" ~ queryNumber]);
            result.outputQueryString = "qAdjacent(" ~ result.outputQueryString ~ ", AdjacencyType." ~ definition["adjacencyType" ~ queryNumber] ~ ")";
        }
        result.processed = true;

    } else if (definition["query" ~ queryNumber] == QuerySelection.HAS_ATTRIBUTE) {
        result.outputQuery = qHasAttribute(result.outputQuery, definition["attributeName" ~ queryNumber]);
        result.outputQueryString = "qHasAttribute(" ~ result.outputQueryString ~ ", \"" ~ definition["attributeName" ~ queryNumber] ~ "\")";
        result.processed = true;
    } else if (definition["query" ~ queryNumber] == QuerySelection.HAS_ATTRIBUTE_WITH_VALUE) {
        const value = definition["attributeValue" ~ queryNumber];
        const valueString = value is string ? "\"" ~ value ~ "\"" : value;
        result.outputQuery = qHasAttributeWithValue(result.outputQuery, definition["attributeName" ~ queryNumber], value);
        result.outputQueryString = "qHasAttributeWithValue(" ~ result.outputQueryString ~ ", \"" ~ definition["attributeName" ~ queryNumber] ~ "\", " ~ valueString ~ ")";
        result.processed = true;
    } else if (definition["query" ~ queryNumber] == QuerySelection.HAS_ATTRIBUTE_WITH_VALUE_MATCHING) {
        var outputMap = {};
        for (var match in definition["attributeValueMatches" ~ queryNumber])
        {
            outputMap[match.key] = match.value;
        }
        result.outputQuery = qHasAttributeWithValueMatching(result.outputQuery, definition["attributeName" ~ queryNumber], outputMap);
        result.outputQueryString = "qHasAttributeWithValueMatching(" ~ result.outputQueryString ~ ", \"" ~ definition["attributeName" ~ queryNumber] ~ "\", " ~ outputMap ~ ")";
        result.processed = true;
    }
    return result;
}

function parseQueryLine(context is Context, definition is map, result is map, queryNumber is number) returns map
{
    if (definition["query" ~ queryNumber] == QuerySelection.INTERSECTS_LINE)
    {
        if (size(evaluateQuery(context, definition["line" ~ queryNumber])) == 0)
        {
            throw regenError("Select line", ["line" ~ queryNumber]);
        }
        var line = evLine(context, {
                "edge" : definition["line" ~ queryNumber]
            });
        result.outputQuery = qIntersectsLine(result.outputQuery, line);
        result.outputQueryString = "qIntersectsLine(" ~ result.outputQueryString ~ ", " ~ convertToFS(line, definition.queryUnits) ~ ")";
        result.processed = true;
    }
    return result;
}

function parseQueryPlane(context is Context, definition is map, result is map, queryNumber is number) returns map
{
    if (definition["query" ~ queryNumber] == QuerySelection.PLANE_NORMAL || definition["query" ~ queryNumber] == QuerySelection.INTERSECTS_PLANE || definition["query" ~ queryNumber] == QuerySelection.COINCIDES_WITH_PLANE)
    {
        if (size(evaluateQuery(context, definition["plane" ~ queryNumber])) == 0)
        {
            throw regenError("Select plane", ["plane" ~ queryNumber]);
        }
        const plane = evPlane(context, { "face" : definition["plane" ~ queryNumber] });

        if (definition["query" ~ queryNumber] == QuerySelection.PLANE_NORMAL)
        {
            result.outputQuery = qParallelPlanes(result.outputQuery, plane);
            result.outputQueryString = "qParallelPlanes(" ~ result.outputQueryString ~ ", " ~ convertToFS(plane, definition.queryUnits) ~ ")";
            result.processed = true;
        }
        else if (definition["query" ~ queryNumber] == QuerySelection.INTERSECTS_PLANE)
        {
            result.outputQuery = qIntersectsPlane(result.outputQuery, plane);
            result.outputQueryString = "qIntersectsPlane(" ~ result.outputQueryString ~ ", " ~ convertToFS(plane, definition.queryUnits) ~ ")";
            result.processed = true;
        }
        else if (definition["query" ~ queryNumber] == QuerySelection.COINCIDES_WITH_PLANE)
        {
            result.outputQuery = qCoincidesWithPlane(result.outputQuery, plane);
            result.outputQueryString = "qCoincidesWithPlane(" ~ result.outputQueryString ~ "," ~ convertToFS(plane, definition.queryUnits) ~ ")";
            result.processed = true;
        }
    }

    return result;
}

function parseQueryDirection(context is Context, definition is map, result is map, queryNumber is number) returns map
{
    if (definition["query" ~ queryNumber] == QuerySelection.PLANE_PARALLEL_DIRECTION || definition["query" ~ queryNumber] == QuerySelection.FACE_PARALLEL_DIRECTION || definition["query" ~ queryNumber] == QuerySelection.FARTHEST_ALONG)
    {
        if (size(evaluateQuery(context, definition["direction" ~ queryNumber])) == 0)
        {
            throw regenError("Select direction", ["direction" ~ queryNumber]);
        }
        const direction = extractDirection(context, definition["direction" ~ queryNumber]);

        if (definition["query" ~ queryNumber] == QuerySelection.PLANE_PARALLEL_DIRECTION)
        {
            result.outputQuery = qPlanesParallelToDirection(result.outputQuery, direction);
            result.outputQueryString = "qPlanesParallelToDirection(" ~ result.outputQueryString ~ ", " ~ convertToFS(direction, definition.queryUnits) ~ ")";
            result.processed = true;
        }
        else if (definition["query" ~ queryNumber] == QuerySelection.FACE_PARALLEL_DIRECTION)
        {
            result.outputQuery = qFacesParallelToDirection(result.outputQuery, direction);
            result.outputQueryString = "qFacesParallelToDirection(" ~ result.outputQueryString ~ ", " ~ convertToFS(direction, definition.queryUnits) ~ ")";
            result.processed = true;
        }
        else if (definition["query" ~ queryNumber] == QuerySelection.FARTHEST_ALONG)
        {
            result.outputQuery = qFarthestAlong(result.outputQuery, direction);
            result.outputQueryString = "qFarthestAlong(" ~ result.outputQueryString ~ ", " ~ convertToFS(direction, definition.queryUnits) ~ ")";
            result.processed = true;
        }
    }

    return result;
}

function parseQueryPoint(context is Context, definition is map, result is map, queryNumber is number) returns map
{
    if (definition["query" ~ queryNumber] == QuerySelection.CONTAINS_POINT || definition["query" ~ queryNumber] == QuerySelection.CLOSEST_TO || definition["query" ~ queryNumber] == QuerySelection.INTERSECTS_BALL)
    {
        if (size(evaluateQuery(context, definition["vertex" ~ queryNumber])) == 0)
        {
            throw regenError("Select vertex", ["vertex" ~ queryNumber]);
        }
        const point = evVertexPoint(context, { "vertex" : definition["vertex" ~ queryNumber] });

        if (definition["query" ~ queryNumber] == QuerySelection.CONTAINS_POINT)
        {
            result.outputQuery = qContainsPoint(result.outputQuery, point);
            result.outputQueryString = "qContainsPoint(" ~ result.outputQueryString ~ ", " ~ convertToFS(point, definition.queryUnits) ~ ")";
            result.processed = true;
        }
        else if (definition["query" ~ queryNumber] == QuerySelection.CLOSEST_TO)
        {
            result.outputQuery = qClosestTo(result.outputQuery, point);
            result.outputQueryString = "qClosestTo(" ~ result.outputQueryString ~ ",  " ~ convertToFS(point, definition.queryUnits) ~ ")";
            result.processed = true;
        }
        else if (definition["query" ~ queryNumber] == QuerySelection.INTERSECTS_BALL)
        {
            result.outputQuery = qWithinRadius(result.outputQuery, point, definition.radius1);
            result.outputQueryString = "qWithinRadius(" ~ result.outputQueryString ~ ", " ~ convertToFS(point, definition.queryUnits) ~ ", " ~ convertToFS(definition.radius1, definition.queryUnits) ~ ")";
            result.processed = true;
        }
    }

    return result;
}

function parseEntityType(context is Context, definition is map, outputQuery, outputQueryString is string, queryType) returns map
{
    if (definition.entityType != EntityTypeSelection.ANY)
    {
        outputQuery = qEntityFilter(outputQuery, definition.entityType as EntityType);
        outputQueryString = "qEntityFilter(" ~ outputQueryString ~ ", EntityType." ~ toString(definition.entityType) ~ ")";
    }

    return { "outputQuery" : outputQuery, "outputQueryString" : outputQueryString, "queryType" : queryType };
}

function parseBodyType(context is Context, definition is map, outputQuery, outputQueryString is string, queryType) returns map
{
    if (definition.bodyType != BodyTypeSelection.ANY)
    {
        outputQuery = qBodyType(outputQuery, definition.bodyType as BodyType);
        outputQueryString = "qBodyType(" ~ outputQueryString ~ ", BodyType." ~ toString(definition.bodyType) ~ ")";
    }

    return { "outputQuery" : outputQuery, "outputQueryString" : outputQueryString, "queryType" : queryType };
}

function getQueryHelp(queryType)
// function getQueryHelp(queryType is QueryType)
{
    const helpStringMap = {
            QuerySelection.CONTAINED_IN_COMPOSITE : "A query for each composite part containing 'Seed'",
            QuerySelection.CONTAINED_IN_COMPOSITE : "A query for each part contained in 'Seed'",
            QuerySelection.CONSUMED : "Depending on the value of 'Consumed', a query for filtering out all bodies (or their vertices, edges, and faces) consumed by closed composite parts or allowing only such entities",
            QuerySelection.FLATTENED_COMPOSITE_PARTS : "A query for non-composite entities in 'Seed' and constituents of composite parts in 'Seed'",
            QuerySelection.CREATED_BY : "A query for all the entities created by a feature or operation",
            QuerySelection.MODIFABLE_ENTITY_FILTER : "A geometry is considered not 'modifiable' if it is a in context entity",
            QuerySelection.MESH_GEOMETRY_FILTER : "Depending on 'Mesh Geometry', a query for filtering out all mesh entities or allowing only mesh entities matching a selected 'Seed'",
            //QuerySelection.SOURCE_MESH : "A query for each mesh that the mesh vertices in the 'Seed' belong to",
            QuerySelection.LAMINAR_DEPENDENCY : "A query for the true dependency of the selected entities, specifically for use with wire edges that have been created from laminar edges",
            QuerySelection.DEPENDENCY : "A query for the true dependency of the 'Seed'",
            QuerySelection.NON_CAP_ENTITY : "A query for vertex, edge, and face entities created by 'Selection' that are not cap entities",
            QuerySelection.NTH_ELEMENT : "A query for an element of a 'Seed' at a specified 'Index'",
            QuerySelection.SKETCH_REGION : "A query for all fully enclosed, 2D regions created by a 'Selection'",
            QuerySelection.CAP_ENTITY : "A query for start/end cap vertex, edge, and face entities created by 'Selection' feature. \nCap entities are produced by extrude, revolve, sweep, loft and thicken features",
            QuerySelection.PATTERN_INSTANCES : "A query for entities created by a specific instance or instances of an `opPattern` operation",
            QuerySelection.CORRESPONDING_IN_FLAT : "A query for entities in sheet metal flattened body corresponding to those in folded body defined by 'Seed'",
            QuerySelection.OWNED_BY_PART : "A query for all of the entities (faces, vertices, edges, and bodies) in a context which belong to a specified 'Body'",
            QuerySelection.OWNER_PART : "A query for each part that any entities in the 'Seed' belong to",
            QuerySelection.ADJACENT : "A query for entities that are adjacent to the given 'Seed' entities",
            QuerySelection.EDGE_TOPOLOGY_FILTER : "A query for edges of a 'Seed' which match a given 'Edge Topology'",
            QuerySelection.GEOMETRY : "A query for all entities of a specified 'Geometry Type' matching a 'Seed'",
            QuerySelection.CONSTRUCTION_FILTER : "A query for all construction entities, or all non-construction entities, matching a 'Construction Filter' selected 'Seed'",
            QuerySelection.ACTIVE_SM_FILTER : "A query for all entities belonging to an active sheet metal part, or all entities not belonging to an active sheet metal part, matching a 'Active sheet metal' selected 'Seed'",
            QuerySelection.SM_FLAT_FILTER : "A query for all entities belonging to a flattened sheet metal part, or all entities not belonging to a flattened sheet metal part, matching a 'Filter Flat' selected 'Seed'",
            QuerySelection.PARTS_ATTACHED_TO : "A query that filters out duplicate vertices. When duplicates are found, the vertex with the lowest deterministic ID is used",
            QuerySelection.SKETCH_OBJECT_FILTER : "A query for all sketch entities, or all non-sketch entities, matching a 'Sketch Object Filter' selected 'Seed'",
            QuerySelection.PLANE_NORMAL : "A query for all planar face entities that are parallel to the 'Reference Plane'",
            QuerySelection.PLANE_PARALLEL_DIRECTION : "A query for all planar faces that are parallel to the given direction vector (i.e., the plane normal is perpendicular to 'Direction')",
            QuerySelection.FACE_PARALLEL_DIRECTION : "A query for all faces that are parallel to the given 'Direction' vector",
            QuerySelection.CONVEX_CONNECTED_FACES : "A query for a set of faces connected via convex edges",
            QuerySelection.CONCAVE_CONNECTED_FACES : "A query for a set of faces connected via concave edges",
            QuerySelection.TANGENT_CONNECTED_FACES : "A query for a set of faces connected via tangent edges",
            QuerySelection.TANGENT_CONNECTED_EDGES : "A query that returns a tangent chain of edges with seed edges defined by a 'Seed'",
            QuerySelection.LOOP_EDGES : "A query for a set of edges defining a loop. If 'Seed' has laminar edges, the query will extend to include the laminar loops that contain the edges. For face selections in 'Seed' it returns the loops forming the outer boundary of joined faces.",
            QuerySelection.PARALLEL_EDGES : "A query that returns edges that are parallel to the edges in 'Seed'. Only edges from the owner bodies of 'Seed' are returned",
            QuerySelection.LOOP_BOUNDED_FACES : "Given a face and an edge, query for all faces bounded by the given face, on the side of the given edge",
            QuerySelection.FACE_OR_EDGE_BOUNDED_FACES : "Given a 'Seed' face and bounding entities, matches all adjacent faces 'inside the bounding entities, expanding from the 'Seed' face",
            QuerySelection.HOLE_FACES : "Given a single face inside a hole or hole-like geometry, returns all faces of that hole",
            QuerySelection.UNIQUE_VERTICES : "A query that filters out duplicate vertices in 'Seed'. When duplicates are found, the vertex with the lowest deterministic ID is used",
            QuerySelection.MATE_CONNECTOR : "A query for all mate connectors owned by the parts of a 'Seed'",
            QuerySelection.FILLET_FACES : "A query for fillet faces of radius equal to, less than and equal to, or greater than and equal to the 'Seed'",
            QuerySelection.PATTERN : "Matches any faces or edges within owner bodies of entities in 'Seed' which are geometrically identical (same size and shape) to the face or edge in 'Seed'",
            QuerySelection.CONTAINS_POINT : "A query for all 'Seed' entities (bodies, faces, edges, or points) containing a specified 'Vertex'",
            QuerySelection.INTERSECTS_LINE : "A query for all 'Seed' entities (bodies, faces, edges, or points) touching a specified infinite 'Line'",
            QuerySelection.INTERSECTS_PLANE : "A query for all 'Seed' entities (bodies, faces, edges, or points) touching a specified infinite 'Reference Plane'",
            QuerySelection.INTERSECTS_BALL : "A query for all 'Seed' entities (bodies, faces, edges or points) that are within a specified 'Radius' from a 'Vertex'",
            QuerySelection.COINCIDES_WITH_PLANE : "A query for all 'Seed' entities (bodies, faces, edges, or points) coinciding with a specified infinite 'Reference Plane'",
            QuerySelection.CLOSEST_TO : "A query for the 'Seed' entity closest to a 'Vertex'",
            QuerySelection.FARTHEST_ALONG : "A query for the 'Seed' entity farthest along a 'Direction' in world space",
            QuerySelection.LARGEST : "A query to find the largest entity (by length, area, or volume) within a 'Seed'. If 'Seed' contains entities of different dimensionality (e.g. solid bodies and faces), only entities of the highest dimension are considered",
            QuerySelection.SMALLEST : "A query to find the smallest entity (by length, area, or volume) within a 'Seed'. If 'Seed' contains entities of different dimensionality (e.g. solid bodies and faces), only entities of the highest dimension are considered"
        };
    var result = helpStringMap[queryType];

    // println("result=" ~ result);
    return result;

}

function EntityTypeSelectionConverter(selection is EntityTypeSelection)
{
    if (selection != EntityTypeSelection.ANY)
    {
        return { "converted" : true, "result" : selection as EntityType };
    }
    else
    {
        return { "converted" : false, "result" : selection };
    }
}

