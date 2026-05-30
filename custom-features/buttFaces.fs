FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");

/**
 * Replaces two selected faces with the mid-plane between them.
 */
annotation { "Feature Type Name" : "Butt Faces" }
export const buttFaces = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face 1", "Filter" : EntityType.FACE && ConstructionObject.NO && SketchObject.NO && ModifiableEntityOnly.YES }
        definition.face1 is Query;

        annotation { "Name" : "Face 2", "Filter" : EntityType.FACE && ConstructionObject.NO && SketchObject.NO && ModifiableEntityOnly.YES }
        definition.face2 is Query;
    }
    {
        // Get the plane of each selected face (only works for planar faces)
        var face1Plane = evPlane(context, { "face" : definition.face1 });
        var face2Plane = evPlane(context, { "face" : definition.face2 });

        // Negate face2Plane's normal so that two faces pointing toward each other
        // end up pointing the same way, producing the correct bisector direction
        face2Plane.normal = -face2Plane.normal;

        const midOrigin = 0.5 * (face1Plane.origin + face2Plane.origin);
        const lineOfIntersection = intersection(face1Plane, face2Plane);

        var midPlane;
        if (lineOfIntersection == undefined)
        {
            // Parallel faces: mid-plane shares the normal of face1Plane
            midPlane = plane(midOrigin, face1Plane.normal, face1Plane.x);
        }
        else
        {
            // Non-parallel faces: bisector plane through the line of intersection
            const midNormal = normalize(face1Plane.normal + face2Plane.normal);
            midPlane = plane(project(plane(lineOfIntersection.origin, midNormal), midOrigin), midNormal, lineOfIntersection.direction);
        }

        // Build a temporary construction plane at the mid-plane location to serve as the template
        const tempPlaneId = id + "tempMidPlane";
        opPlane(context, tempPlaneId, { "plane" : midPlane, "width" : 1 * meter, "height" : 1 * meter });
        const templateFace = qCreatedBy(tempPlaneId, EntityType.FACE);

        // Determine the template face's normal so oppositeSense can be set for each replacement
        const templateNormal = evFaceTangentPlane(context, { "face" : templateFace, "parameter" : vector(0.5, 0.5) }).normal;

        // Replace face 1
        const face1Normal = evFaceTangentPlane(context, { "face" : definition.face1, "parameter" : vector(0.5, 0.5) }).normal;
        opReplaceFace(context, id + "replace1", {
                    "replaceFaces" : definition.face1,
                    "templateFace" : templateFace,
                    "oppositeSense" : dot(face1Normal, templateNormal) < 0
                });

        // Replace face 2
        const face2Normal = evFaceTangentPlane(context, { "face" : definition.face2, "parameter" : vector(0.5, 0.5) }).normal;
        opReplaceFace(context, id + "replace2", {
                    "replaceFaces" : definition.face2,
                    "templateFace" : templateFace,
                    "oppositeSense" : dot(face2Normal, templateNormal) < 0
                });

        // Remove the temporary plane
        opDeleteBodies(context, id + "cleanup", { "entities" : qCreatedBy(tempPlaneId, EntityType.BODY) });
    });
