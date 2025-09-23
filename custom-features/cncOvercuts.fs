/*    
    CNC Overcuts / Dogbones
    
    This is a custom feature for adding overcuts to all corners that need them 
    to make parts CNC machinable. 
    If desired the direction along which overcuts should be placed can be specified.
    
    Please take a look at the PDF documentation for how to use this custom feature. 
        
    Version history: 
    1.0     Jan 26 2023     First release
                                                
*/ 


FeatureScript 1948;
import(path : "onshape/std/common.fs", version : "1948.0");

Icon::import(path : "1ae2ec85950a835043678982", version : "b92f8dea69e5f5f8f9eee04b");
DescriptionImage::import(path : "0e4af2923798391877aae6e3", version : "5ac4e64a5a3d7dd57cce9f2c");


annotation { "Feature Type Name" : "CNC Overcuts+", "Icon" : Icon::BLOB_DATA,
        "Feature Type Description" : "Adds overcuts (dogbones) to all corners that need them to make the part(s) CNC machinable.",
        "Description Image" : DescriptionImage::BLOB_DATA }
export const overcuts = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Parts", "Filter" : EntityType.BODY && BodyType.SOLID }
        definition.parts is Query;

        annotation { "Name" : "Tool diameter", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        isLength(definition.toolDiameter, OVERCUT_DIAMETER_BOUNDS);

        annotation { "Name" : "Fillet overcuts", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        definition.applyFillet is boolean;

        if (definition.applyFillet)
        {
            annotation { "Name" : "Fillet radius", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
            isLength(definition.filletRadius, FILLET_RADIUS_BOUNDS);
        }

        annotation { "Name" : "Overcut direction (optional)", "MaxNumberOfPicks" : 1,
                    "Filter" : QueryFilterCompound.ALLOWS_DIRECTION }
        definition.direction is Query;
    }
    {
        for (var partIndex, part in evaluateQuery(context, definition.parts))
        {
            const partId = id + partIndex;
            addOvercuts(context, part, partId, definition);
        }
    });


// Add overcuts to all concave corners that are perpendicular to the CNC endmill orientation
function addOvercuts(context is Context, part is Query, id is Id, definition is map)
{
    // We assume that the largest face is the bottom face. This is the face that
    // will be on the platform of the 3 axis CNC machine.
    const bottomFace = qOwnedByBody(part, EntityType.FACE)->qGeometry(GeometryType.PLANE)->qLargest()->qNthElement(0);
    // If no bottom face is found (e.g. for a sphere), skip this part.
    if (isQueryEmpty(context, bottomFace))
    {
        // Optionally, report a warning or info to the user.
        // reportFeatureWarning(context, id, "Could not determine bottom face for a part. Skipping overcuts.");
        return;
    }
    const bottomPlane = evPlane(context, { "face" : bottomFace });
    // The endmill orientation is given by that face's normal
    const cncDir = bottomPlane.normal;

    // Returns either a 3d vector or 'undefined'
    const direction = extractDirection(context, definition.direction);

    // Find the top face (parallel to the bottom face, but maximally distant from it)
    const topFace = qOwnedByBody(part, EntityType.FACE)->qParallelPlanes(-cncDir)->qFarthestAlong(-cncDir)->qNthElement(0);
    // If no top face is found (e.g. for a part with only one planar face), skip this part.
    if (isQueryEmpty(context, topFace))
    {
        // Optionally, report a warning or info to the user.
        // reportFeatureWarning(context, id, "Could not determine top face for a part. Skipping overcuts.");
        return;
    }

    // get all edges aligned with the CNC endmill direction
    const edges = qOwnedByBody(part, EntityType.EDGE)
        ->qEdgeTopologyFilter(EdgeTopology.TWO_SIDED)
        ->qParallelEdges(cncDir);
    const concaveEdges = filter(evaluateQuery(context, edges), function(edge is Query)
        {
            return evEdgeConvexity(context, { "edge" : edge }) == EdgeConvexityType.CONCAVE;
        });

    // If there are no concave edges, there's nothing to do for this part.
    if (size(concaveEdges) == 0)
    {
        // Optionally, inform the user that no overcuts were needed for this part.
        // reportFeatureInfo(context, id, "No overcuts required for this part.");
        return; // Exit the function for this part and continue to the next.
    }

    const overcutCylinders = mapArrayIndices(concaveEdges, function(i)
        {
            const edge = concaveEdges[i];
            const overcutID = id + ("overcut" ~ i);
            return createOvercut(context, overcutID, edge, topFace, definition.toolDiameter, direction);
        });

    // It's good practice to check if overcutCylinders is empty before calling qUnion,
    // though the previous check for concaveEdges should prevent this specific case.
    if (size(overcutCylinders) > 0)
    {
        const subtractId = id + "subtract";
        booleanBodies(context, subtractId, {
                    "tools" : qUnion(overcutCylinders),
                    "targets" : part,
                    "operationType" : BooleanOperationType.SUBTRACTION
                });

        if (definition.applyFillet)
        {
            const edgesToFillet = qCreatedBy(subtractId, EntityType.EDGE)->qGeometry(GeometryType.LINE);
            // Check if there are edges to fillet before calling opFillet
            if (!isQueryEmpty(context, edgesToFillet))
            {
                opFillet(context, id + "fillet", {
                            "entities" : edgesToFillet,
                            "radius" : definition.filletRadius
                        });
            }
        }
    }
}


function createOvercut(context is Context, id is Id, edge is Query, topFace is Query,
    toolDiameter is ValueWithUnits, optionalOvercutDirection) returns Query
{
    const radius = 0.5 * toolDiameter;
    const topPlane = evPlane(context, { "face" : topFace });
    const bottomVertex = qAdjacent(edge, AdjacencyType.VERTEX, EntityType.VERTEX)->qFarthestAlong(-topPlane.normal)->qNthElement(0);
    const bottomVertexPos = evVertexPoint(context, { "vertex" : bottomVertex });

    // Extend the overcut all the way to the top of the part
    const topVertexPos = project(topPlane, bottomVertexPos);

    const faces = evaluateQuery(context, qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE));
    const faceNormals = mapArray(faces, function(face)
        {
            return evFaceNormalAtEdge(context, { "edge" : edge, "face" : face, "parameter" : 0.5 });
        });

    var offset = foldArray(faceNormals, zeroVector(3), function(summed, normal)
    {
        var weight = 1.0;
        if (optionalOvercutDirection != undefined)
        {
            weight = 1.0 - abs(dot(optionalOvercutDirection, normal));
        }
        return summed + weight * normal;
    });
    offset = normalize(offset) * radius;
    fCylinder(context, id, {
                "topCenter" : topVertexPos + offset,
                "bottomCenter" : bottomVertexPos + offset,
                "radius" : radius
            });
    return qCreatedBy(id, EntityType.BODY);
}


const OVERCUT_DIAMETER_BOUNDS =
{
            (inch) : [0, 0.25, 1.0],
        } as LengthBoundSpec;

const FILLET_RADIUS_BOUNDS =
{
            (inch) : [0, 0.25, 2.0],
            (millimeter) : 5
        } as LengthBoundSpec;

