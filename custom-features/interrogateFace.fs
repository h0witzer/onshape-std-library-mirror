FeatureScript 2737;

import(path : "onshape/std/feature.fs", version : "2737.0");
import(path : "onshape/std/common.fs", version : "2737.0");
import(path : "onshape/std/query.fs", version : "2737.0");
import(path : "onshape/std/evaluate.fs", version : "2737.0");
import(path : "onshape/std/error.fs", version : "2737.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2737.0");

annotation { "Feature Type Name" : "Face Interrogator" }
export const faceInformation = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face", "Filter" : EntityType.FACE && AllowMeshGeometry.YES, "MaxNumberOfPicks" : 1 }
        definition.face is Query;
    }
    {
        const surfaceDefinition = evSurfaceDefinition(context, { "face" : definition.face });
        const message = describeSurface(context, definition.face, surfaceDefinition);
        reportFeatureInfo(context, id, message);
        displaySurfaceGeometry(context, definition.face, surfaceDefinition);
    });

export function describeSurface(context is Context, face is Query, surfaceDefinition) returns string
{
    var description = "";
    if (surfaceDefinition is Plane)
    {
        description = "Plane with normal " ~ surfaceDefinition.normal ~ " at " ~ surfaceDefinition.origin;
    }
    else if (surfaceDefinition is Cylinder)
    {
        description = "Cylinder with radius " ~ surfaceDefinition.radius ~ " and axis at " ~ surfaceDefinition.coordSystem.origin;
    }
    else if (surfaceDefinition is Cone)
    {
        description = "Cone with half angle " ~ surfaceDefinition.halfAngle ~ " at " ~ surfaceDefinition.coordSystem.origin;
    }
    else if (surfaceDefinition is Torus)
    {
        description = "Torus with major radius " ~ surfaceDefinition.radius ~ " and minor radius " ~ surfaceDefinition.minorRadius;
    }
    else if (surfaceDefinition is Sphere)
    {
        description = "Sphere with radius " ~ surfaceDefinition.radius ~ " at " ~ surfaceDefinition.coordSystem.origin;
    }
    else if (surfaceDefinition is BSplineSurface)
    {
        description = "B-spline surface";
    }
    else if (surfaceDefinition.surfaceType == SurfaceType.REVOLVED)
    {
        const axis is Line = evAxis(context, { "axis" : face });
        description = "Revolved surface about axis through " ~ axis.origin ~ " in direction " ~ axis.direction;
    }
    else if (surfaceDefinition.surfaceType == SurfaceType.EXTRUDED)
    {
        const extrudeInfo = evaluateExtrudedSurfaceDirection(context, face);
        description = "Extruded surface in direction " ~ extrudeInfo.direction;
    }
    else if (surfaceDefinition.surfaceType == SurfaceType.MESH)
    {
        description = "Mesh surface";
    }
    else if (surfaceDefinition.surfaceType == SurfaceType.SPLINE)
    {
        description = "Spline surface";
    }
    else
    {
        description = "Other surface of type " ~ surfaceDefinition.surfaceType;
    }
    return description;
}

/**
 * Evaluate an extruded surface to obtain its direction and a point on the surface.
 */
function evaluateExtrudedSurfaceDirection(context is Context, face is Query) returns map
{
    const tangentPlanes = evFaceTangentPlanes(context, {
                "face" : face,
                "parameters" : [ vector(0.5, 0), vector(0.5, 1) ]
        });
    const direction = normalize(tangentPlanes[1].origin - tangentPlanes[0].origin);
    return { "direction" : direction, "point" : tangentPlanes[0].origin };
}

/**
 * Display debug geometry representing the evaluated surface in the viewport.
 */
export function displaySurfaceGeometry(context is Context, face is Query, surfaceDefinition)
{
    const arrowLength = 0.05 * meter;
    const arrowRadius = 0.05 * arrowLength;

    if (surfaceDefinition is Plane)
    {
        debug(context, coordSystem(surfaceDefinition), DebugColor.RED, DebugColor.GREEN, DebugColor.BLUE);
    }
    else if (surfaceDefinition is Cylinder)
    {
        const cylinderAxisLine = line(surfaceDefinition.coordSystem.origin, surfaceDefinition.coordSystem.zAxis);
        debug(context, cylinderAxisLine, DebugColor.BLUE);
    }
    else if (surfaceDefinition is Cone)
    {
        const coneAxisLine = line(surfaceDefinition.coordSystem.origin, surfaceDefinition.coordSystem.zAxis);
        debug(context, coneAxisLine, DebugColor.BLUE);
    }
    else if (surfaceDefinition is Torus)
    {
        const torusAxisLine = line(surfaceDefinition.coordSystem.origin, surfaceDefinition.coordSystem.zAxis);
        debug(context, torusAxisLine, DebugColor.BLUE);
    }
    else if (surfaceDefinition is Sphere)
    {
        const sphereOrigin = surfaceDefinition.coordSystem.origin;
        debug(context, sphereOrigin);

    }
    else if (surfaceDefinition.surfaceType == SurfaceType.REVOLVED)
    {
        const axis is Line = evAxis(context, { "axis" : face });
        debug(context, axis, DebugColor.BLUE);
    }
    else if (surfaceDefinition.surfaceType == SurfaceType.EXTRUDED)
    {
        const extrudeInfo = evaluateExtrudedSurfaceDirection(context, face);
        addDebugArrow(context,
                      extrudeInfo.point,
                      extrudeInfo.point + extrudeInfo.direction * arrowLength,
                      arrowRadius,
                      DebugColor.YELLOW);
    }
    else if (surfaceDefinition.surfaceType == SurfaceType.MESH)
    {
        addDebugEntities(context, face, DebugColor.BLACK);
    }
    else if (surfaceDefinition.surfaceType == SurfaceType.SPLINE)
    {
        addDebugEntities(context, face, DebugColor.ORANGE);
    }
    else
    {
        addDebugEntities(context, face, DebugColor.BLACK);
    }
}
