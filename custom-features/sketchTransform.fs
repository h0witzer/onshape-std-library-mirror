FeatureScript 2878;

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/coordSystem.fs", version : "2878.0");
import(path : "onshape/std/error.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/matrix.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/transform.fs", version : "2878.0");
import(path : "onshape/std/units.fs", version : "2878.0");
import(path : "onshape/std/valueBounds.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");

/**
 * Shear parameter bounds for skewing transformations.
 * Values typically range from -5 to 5 for reasonable skewing effects.
 */
const SHEAR_PARAMETER_BOUNDS =
{ 
    (unitless) : [-5, 0, 5]
} as RealBoundSpec;

/**
 * Barebones transform feature for scaling and skewing sketch-derived bodies.
 * 
 * This feature allows non-uniform scaling and skewing transformations on sketch entities
 * that have been converted to bodies. It provides similar functionality to existing transform
 * features but specifically designed for manipulating whole sketches.
 */
annotation { "Feature Type Name" : "Sketch Transform" }
export const sketchTransform = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Sketch bodies to transform", "Filter" : EntityType.BODY && SketchObject.YES }
        definition.sketchBodies is Query;

        annotation { "Name" : "Transform anchor", "Filter" : BodyType.MATE_CONNECTOR || (EntityType.VERTEX && SketchObject.NO), "MaxNumberOfPicks" : 1 }
        definition.anchor is Query;

        annotation { "Group Name" : "Scaling", "Name" : "Scale X" }
        isReal(definition.scaleX, SCALE_BOUNDS);

        annotation { "Group Name" : "Scaling", "Name" : "Scale Y" }
        isReal(definition.scaleY, SCALE_BOUNDS);

        annotation { "Group Name" : "Scaling", "Name" : "Scale Z" }
        isReal(definition.scaleZ, SCALE_BOUNDS);

        annotation { "Group Name" : "Skewing", "Name" : "Skew X by Y" }
        isReal(definition.skewXY, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Skewing", "Name" : "Skew X by Z" }
        isReal(definition.skewXZ, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Skewing", "Name" : "Skew Y by X" }
        isReal(definition.skewYX, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Skewing", "Name" : "Skew Y by Z" }
        isReal(definition.skewYZ, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Skewing", "Name" : "Skew Z by X" }
        isReal(definition.skewZX, SHEAR_PARAMETER_BOUNDS);

        annotation { "Group Name" : "Skewing", "Name" : "Skew Z by Y" }
        isReal(definition.skewZY, SHEAR_PARAMETER_BOUNDS);
    }
    {
        // Query the owner bodies of the selected sketch entities
        const bodiesToTransform = qOwnerBody(definition.sketchBodies);

        if (isQueryEmpty(context, bodiesToTransform))
        {
            throw regenError("Select at least one sketch body to transform", ["sketchBodies"]);
        }

        // Resolve the anchor coordinate system for the transformation
        const anchorSystem = resolveAnchorCoordinateSystem(context, definition, bodiesToTransform);

        // Build the transformation matrix combining scaling and skewing
        const localLinear = matrix([
                [definition.scaleX, definition.skewXY, definition.skewXZ],
                [definition.skewYX, definition.scaleY, definition.skewYZ],
                [definition.skewZX, definition.skewZY, definition.scaleZ]
            ]);

        // Create the local transformation and convert to world space
        const localTransform = transform(localLinear, vector(0, 0, 0) * meter);
        const worldTransform = toWorld(anchorSystem) * localTransform * fromWorld(anchorSystem);

        // Apply the transformation to the selected bodies
        opTransform(context, id + "transform", {
                    "bodies" : bodiesToTransform,
                    "transform" : worldTransform
                });
    }, {
            "sketchBodies" : qNothing(),
            "anchor" : qNothing(),
            "scaleX" : 1,
            "scaleY" : 1,
            "scaleZ" : 1,
            "skewXY" : 0,
            "skewXZ" : 0,
            "skewYX" : 0,
            "skewYZ" : 0,
            "skewZX" : 0,
            "skewZY" : 0
        });

/**
 * Determine the anchor coordinate system used to evaluate transformations in local space.
 * 
 * @param context - The current context
 * @param definition - The feature definition containing the anchor selection
 * @param bodiesToTransform - Query for the bodies being transformed
 * @returns CoordSystem - The coordinate system to use as the transformation anchor
 * 
 * Defaults to the approximate centroid of the selected bodies when no anchor is provided.
 * If a mate connector is selected, uses its coordinate system.
 * If a vertex is selected, creates a world-aligned coordinate system at that point.
 */
function resolveAnchorCoordinateSystem(context is Context, definition is map, bodiesToTransform is Query) returns CoordSystem
{
    const anchorSelection = evaluateQuery(context, definition.anchor);
    
    if (size(anchorSelection) > 1)
    {
        throw regenError(ErrorStringEnum.TOO_MANY_ENTITIES_SELECTED, ["anchor"]);
    }

    if (size(anchorSelection) == 1)
    {
        const selectedAnchor = anchorSelection[0];
        
        // Try to evaluate as a mate connector first
        const mateSystem = try silent(evMateConnector(context, { "mateConnector" : selectedAnchor }));
        if (mateSystem != undefined)
        {
            return mateSystem;
        }

        // If not a mate connector, treat as a vertex and create a world-aligned coordinate system
        const anchorPoint = evVertexPoint(context, { "vertex" : selectedAnchor });
        return coordSystem(anchorPoint, vector(1, 0, 0), vector(0, 1, 0));
    }

    // No anchor provided - use the approximate centroid of the bodies as the origin
    const fallbackOrigin = evApproximateCentroid(context, { "entities" : bodiesToTransform });
    return coordSystem(fallbackOrigin, vector(1, 0, 0), vector(0, 1, 0));
}
