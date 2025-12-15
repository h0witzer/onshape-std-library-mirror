FeatureScript 2837;
// Experimental feature: Create a surface on the edge of a solid and hide it using sheet metal annotations
// This explores whether sheet metal's hidden body concept can be adapted for Frame-like attributes

export import(path : "onshape/std/query.fs", version : "2837.0");

import(path : "onshape/std/attributes.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/error.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalAttribute.fs", version : "2837.0");
import(path : "onshape/std/sheetMetalUtils.fs", version : "2837.0");
import(path : "onshape/std/smobjecttype.gen.fs", version : "2837.0");
import(path : "onshape/std/string.fs", version : "2837.0");
import(path : "onshape/std/tool.fs", version : "2837.0");
import(path : "onshape/std/topologyUtils.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");

/**
 * Feature that creates a surface along an edge of a solid and hides it using sheet metal annotations.
 * This is an experiment to explore whether surfaces can be hidden from regular queries while maintaining
 * their existence and stored properties for later retrieval.
 */
annotation { "Feature Type Name" : "Hide Edge Surface (Experiment)" }
export const hideEdgeSurface = defineSheetMetalFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Edge to create surface along",
                    "Filter" : EntityType.EDGE && ConstructionObject.NO }
        definition.targetEdge is Query;

        annotation { "Name" : "Offset distance",
                    "Description" : "Distance to offset the surface from the edge" }
        isLength(definition.offsetDistance, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

        annotation { "Name" : "Custom property value",
                    "Description" : "A custom property to store in the sheet metal attribute" }
        definition.customProperty is string;
    }
    {
        // Validate that we have exactly one edge selected
        const edgeArray = evaluateQuery(context, definition.targetEdge);
        if (size(edgeArray) == 0)
        {
            throw regenError(ErrorStringEnum.CANNOT_RESOLVE_ENTITIES, ["targetEdge"]);
        }
        if (size(edgeArray) > 1)
        {
            throw regenError("Please select exactly one edge", ["targetEdge"]);
        }

        const selectedEdge = edgeArray[0];

        // Get the tangent line at the edge midpoint to determine surface direction
        const edgeTangentLine = evEdgeTangentLine(context, {
            "edge" : selectedEdge,
            "parameter" : 0.5
        });

        // Get adjacent faces to determine the normal direction for offset
        const adjacentFaces = evaluateQuery(context, qAdjacent(selectedEdge, AdjacencyType.EDGE, EntityType.FACE));
        
        var offsetDirection;
        if (size(adjacentFaces) > 0)
        {
            // Use the normal of the first adjacent face
            const facePlane = evFaceTangentPlane(context, {
                "face" : adjacentFaces[0],
                "parameter" : vector(0.5, 0.5)
            });
            offsetDirection = facePlane.normal;
        }
        else
        {
            // If no adjacent faces, use a default perpendicular direction
            // Create a perpendicular vector to the edge tangent
            const tangent = edgeTangentLine.direction;
            // Use cross product with Z-axis, or X-axis if tangent is close to Z
            if (abs(dot(tangent, vector(0, 0, 1))) < 0.9)
            {
                offsetDirection = normalize(cross(tangent, vector(0, 0, 1)));
            }
            else
            {
                offsetDirection = normalize(cross(tangent, vector(1, 0, 0)));
            }
        }

        // Create a ruled surface using opLoft between the edge and an offset path
        // We'll create an offset edge parallel to the original edge
        
        // Get edge endpoints to create the offset edge
        const edgeEndpoints = evaluateQuery(context, qVertexAdjacent(selectedEdge, EntityType.VERTEX));
        if (size(edgeEndpoints) < 2)
        {
            throw regenError("Edge must have at least two endpoints", ["targetEdge"]);
        }

        const point1 = evVertexPoint(context, {"vertex" : edgeEndpoints[0]});
        const point2 = evVertexPoint(context, {"vertex" : edgeEndpoints[1]});
        
        // Create offset points parallel to the original edge
        const offsetPoint1Final = point1 + offsetDirection * definition.offsetDistance;
        const offsetPoint2Final = point2 + offsetDirection * definition.offsetDistance;

        // Create a line between the offset points using opFitSpline
        // This creates a wire body with an edge that will be used for lofting
        opFitSpline(context, id + "offsetLine", {
            "points" : [offsetPoint1Final, offsetPoint2Final]
        });

        const offsetEdge = qCreatedBy(id + "offsetLine", EntityType.EDGE);

        // Now create a loft surface between the original edge and offset edge
        // This creates a ruled surface connecting the two parallel edges
        try
        {
            opLoft(context, id + "loftSurface", {
                "profileSubqueries" : [selectedEdge, offsetEdge],
                "bodyType" : ToolBodyType.SURFACE
            });
        }
        catch
        {
            throw regenError("Failed to create loft surface between edge and offset", ["targetEdge"]);
        }

        // Get the created surface body
        const createdSurface = qCreatedBy(id + "loftSurface", EntityType.BODY);
        const surfaceFaces = qOwnedByBody(createdSurface, EntityType.FACE);

        // Create a custom sheet metal attribute to store on the surface
        // We'll use a WALL attribute with custom properties
        const featureIdString = toAttributeId(id);
        var customAttribute = makeSMWallAttribute(featureIdString);
        
        // Add custom properties to the attribute
        customAttribute.customProperty = definition.customProperty;
        customAttribute.edgeId = toString(selectedEdge);
        customAttribute.offsetDistance = definition.offsetDistance;
        customAttribute.experimentType = "hiddenEdgeSurface";

        // Set the attribute on the surface faces
        setAttribute(context, {
            "entities" : surfaceFaces,
            "attribute" : customAttribute
        });

        // Call updateSheetMetalGeometry to finalize and hide the surface
        // This should hide the surface from regular queries
        updateSheetMetalGeometry(context, id, {
            "entities" : qUnion([surfaceFaces, qOwnedByBody(createdSurface, EntityType.EDGE)])
        });

        // Log confirmation that the surface was created and hidden
        println("Hidden edge surface created with custom property: " ~ definition.customProperty);
    });
