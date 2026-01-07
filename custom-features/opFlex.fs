FeatureScript 2837;
export import(path : "onshape/std/geometry.fs", version : "2837.0");

// Additional imports for FFD-based implementation
import(path : "onshape/std/common.fs", version : "2837.0");
import(path : "onshape/std/context.fs", version : "2837.0");
import(path : "onshape/std/coordSystem.fs", version : "2837.0");
import(path : "onshape/std/evaluate.fs", version : "2837.0");
import(path : "onshape/std/feature.fs", version : "2837.0");
import(path : "onshape/std/geomOperations.fs", version : "2837.0");
import(path : "onshape/std/math.fs", version : "2837.0");
import(path : "onshape/std/query.fs", version : "2837.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2837.0");
import(path : "onshape/std/units.fs", version : "2837.0");
import(path : "onshape/std/valueBounds.fs", version : "2837.0");
import(path : "onshape/std/vector.fs", version : "2837.0");
import(path : "onshape/std/containers.fs", version : "2837.0");
import(path : "onshape/std/box.fs", version : "2837.0");
import(path : "onshape/std/nurbsUtils.fs", version : "2837.0");
import(path : "onshape/std/debug.fs", version : "2837.0");
import(path : "onshape/std/transform.fs", version : "2837.0");

icon::import(path : "c624ed6117bc781df9db34a7", version : "8c1fdbdfcd870db45cc00916");
image::import(path : "2150aaa7a0e55c57536755d0", version : "24ccd4a440ee9c760daa3bc1");


/**
 * Taper types.
 */
export enum TaperType
{
    annotation { "Name" : "Scale uniformly" }
    TAPER_UNIFORMLY,
    annotation { "Name" : "Scale horizontally" }
    TAPER_HORIZONTALLY,
    annotation { "Name" : "Scale vertically" }
    TAPER_VERTICALLY
}


/**
 * Performs flex (taper, twist and deform) for a body, face, edge or vertex.
 * 
 * This feature uses Free-Form Deformation (FFD) with trivariate Bernstein polynomials
 * to provide high-quality, efficient surface deformation. The FFD approach replaces the
 * previous piecewise filling schema, providing:
 * - Better surface quality with smooth deformations
 * - Improved performance for complex shapes
 * - More generalizable to various surface types
 * 
 * Implementation references:
 * - Core FFD algorithm: custom-features/freeFormDeformation.fs
 * - Plane-based FFD: custom-features/freeFormDeformationPlanes.fs
 * - Whitepaper: whitepaper-references/"Free-Form Deformation of Parametric CAD Geometry.pdf"
 *
 * @param context {Context}
 * @param id : @autocomplete `id + "flex"`
 * @param definition {{
 *      @field entities {Query}:
 *              The parts to flex.
 *      @field baseLine {Query}:
 *              The base line edge.
 *      @field isBaseLineFlipped {boolean}:
 *              The base line direction.
 *
 *      @field edgeSamplingStep {ValueWithUnits}:
 *              Edge sampling step (deprecated in FFD implementation).
 *      @field faceSamplingStep {ValueWithUnits}:
 *              Face sampling step (deprecated in FFD implementation).
 *
 *      @field isTaper {boolean}:
 *              Taper control.
 *      @field taperType {TaperType}:       @requiredif {`isTaper` is true}
 *              Taper type.
 *      @field startScale {number}:         @requiredif {`isTaper` is true}
 *              Taper start scale.
 *      @field endScale {number}:           @requiredif {`isTaper` is true}
 *              Taper end scale.
 *      @field isScaleOverflow {boolean}:   @requiredif {`isTaper` is true}
 *              Taper overflow scale.
 *
 *      @field isTwist {boolean}:
 *              Twist control.
 *      @field startAngle {ValueWithUnits}: @requiredif {`isTwist` is true}
 *              Twist start angle.
 *      @field endAngle {ValueWithUnits}:   @requiredif {`isTwist` is true}
 *              Twist end angle.
 *
 *      @field isDeform {boolean}:
 *              Deform control.
 *      @field targetCurve {Query}:         @requiredif {`isDeform` is true}
 *              The target path.
 *
 *      @field isSoftenTransition {boolean}:
 *              Soften flex transition.
 *
 *
 * }}
 */
annotation {
        "Feature Type Name" : "Flex",
        "Icon" : icon::BLOB_DATA,
        "Description Image" : image::BLOB_DATA,
        "Feature Type Description" : "Flex (taper, twist or deform) selected entities.",
        "Editing Logic Function" : "onFlexFeatureChange" }
export const opFlex = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        /* Base entities ***************************************************************/
        annotation {
                    "Name" : "Base entities",
                    "Description" : "Bodies (solids), faces, edges or vertices to be flexed.",
                    "Filter" : ((EntityType.BODY && BodyType.SOLID) || EntityType.FACE || EntityType.EDGE || EntityType.VERTEX) && ConstructionObject.NO }
        definition.entities is Query;

        /* Base line *******************************************************************/
        annotation {
                    "Name" : "Base line (edge on sketch)",
                    "Description" : "Sketch edge that defines base x-axis and base plane.",
                    "Filter" : GeometryType.LINE, "MaxNumberOfPicks" : 1 }
        definition.baseLine is Query;

        annotation {
                    "Name" : "Base line direction",
                    "Description" : "Direction of base axis.",
                    "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.isBaseLineFlipped is boolean;

        /* Sampling ********************************************************************/
        annotation {
                    "Name" : "Edge sampling step",
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.edgeSamplingStep, { (millimeter) : [0.1, 0.5, 1000] } as LengthBoundSpec);

        annotation {
                    "Name" : "Face sampling step",
                    "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
        isLength(definition.faceSamplingStep, { (millimeter) : [0.1, 2, 1000] } as LengthBoundSpec);

        annotation {
                    "Name" : "Show samples" }
        definition.isShowSamples is boolean;

        /* Taper ***********************************************************************/
        annotation {
                    "Name" : "Taper" }
        definition.isTaper is boolean;
        if (definition.isTaper)
        {
            annotation {
                        "Group Name" : "Taper",
                        "Driving Parameter" :
                        "isTaper",
                        "Collapsed By Default" : false }
            {
                annotation {
                            "Name" : "Taper type" }
                definition.taperType is TaperType;

                annotation {
                            "Name" : "Width start scale",
                            "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE,
                            "Default" : 1.0 }
                isReal(definition.startScale, SCALE_BOUNDS);
                annotation {
                            "Name" : "Width end scale",
                            "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE,
                            "Default" : 1.0 }
                isReal(definition.endScale, SCALE_BOUNDS);

                annotation {
                            "Name" : "Scale overflow" }
                definition.isScaleOverflow is boolean;
            }
        }

        /* Twist ***********************************************************************/
        annotation {
                    "Name" : "Twist" }
        definition.isTwist is boolean;
        if (definition.isTwist)
        {
            annotation {
                        "Group Name" : "Twist",
                        "Driving Parameter" : "isTwist",
                        "Collapsed By Default" : false }
            {
                annotation {
                            "Name" : "Start angle",
                            "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
                isAngle(definition.startAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                annotation {
                            "Name" : "End angle",
                            "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
                isAngle(definition.endAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
            }
        }

        /* Deform **********************************************************************/
        annotation {
                    "Name" : "Deform" }
        definition.isDeform is boolean;
        if (definition.isDeform)
        {
            annotation {
                        "Group Name" : "Deform",
                        "Driving Parameter" : "isDeform",
                        "Collapsed By Default" : false }
            {
                annotation {
                            "Name" : "Target curve (edge on sketch)",
                            "Description" : "Sketch path that defines path and plane where entities are transformed.",
                            "Filter" : EntityType.EDGE && SketchObject.YES, "MaxNumberOfPicks" : 1 }
                definition.targetCurve is Query;
            }
        }

        /* Configurations **************************************************************/
        annotation {
                    "Name" : "Soften flex transition",
                    "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.isSoftenTransition is boolean;

        /* Debug ***********************************************************************/
        annotation {
                    "Name" : "Debug",
                // "UIHint" : UIHint.ALWAYS_HIDDEN
                }
        definition.isDebug is boolean;
        if (definition.isDebug)
        {
            annotation {
                        "Group Name" : "Debug",
                        "Driving Parameter" : "isDebug",
                        "Collapsed By Default" : false }
            {
                annotation { "Name" : "1 - Split faces and bodies with cube mesh" }
                definition.isDebug1 is boolean;
                annotation { "Name" : "2 - Translate edges of splitted faces into target space" }
                definition.isDebug2 is boolean;
                annotation { "Name" : "3 - Fill translated edges to create target faces" }
                definition.isDebug3 is boolean;
                annotation { "Name" : "4 - Join target faces to create faces and bodies" }
                definition.isDebug4 is boolean;
            }
        }
    }
    {
        /* Check input values **********************************************************/
        /* Illegal input */
        var emptyInput = [];
        if (isQueryEmpty(context, definition.entities) == true)
        {
            emptyInput = append(emptyInput, "entities");
        }
        if (isQueryEmpty(context, definition.baseLine) == true)
        {
            emptyInput = append(emptyInput, "baseLine");
        }
        if (size(emptyInput) > 0)
        {
            throw regenError(ErrorStringEnum.CANNOT_BE_EMPTY, emptyInput);
        }

        /* Adjust inputs */
        if (isQueryEmpty(context, definition.targetCurve) == true)
        {
            definition.isDeform = false;
        }
        if (definition.isDebug == false)
        {
            definition.isDebug1 = true;
            definition.isDebug2 = true;
            definition.isDebug3 = true;
            definition.isDebug4 = true;
        }
        if (definition.isTaper == false &&
            definition.isTwist == false &&
            definition.isDeform == false)
        {
            return;
        }


        /* Feature bahaviour ***********************************************************/
        /* Base path and base coordinate system */
        try
        {
            /* Base path and base plane */
            definition.basePath = constructPath(context, definition.baseLine);
            definition.basePathLength = evPathLength(context, definition.basePath);

            /* Base path direction (flipped) */
            definition.basePath.flipped[0] = definition.isBaseLineFlipped;

            /* First base path point (origin and direction) */
            var firstPoint = evPathTangentLines(context, definition.basePath, [0]).tangentLines[0];

            for (var i = 0; i < 1; i += 1)
            {
                /* Try create base coordinate system using base line and target sketch plane */
                try silent
                {
                    if (definition.isDeform == true)
                    {
                        var targetPlane = evOwnerSketchPlane(context, { "entity" : definition.targetCurve });
                        definition.baseCoordSys = coordSystem(firstPoint.origin, firstPoint.direction, targetPlane.normal);
                        break;
                    }
                }

                /* Try create base coordinate system using base line and sketch plane */
                try silent
                {
                    var basePlane = evOwnerSketchPlane(context, { "entity" : definition.baseLine });
                    definition.baseCoordSys = coordSystem(firstPoint.origin, firstPoint.direction, basePlane.normal);
                    break;
                }

                /* Base coordinate system using base line and arbitrary z-direction */
                var zDirection = perpendicularVector(firstPoint.direction);
                definition.baseCoordSys = coordSystem(firstPoint.origin, firstPoint.direction, zDirection);

                break;
            }

            /* Show base coordinate system */
            addDebugCoordinateSystem(context, definition.baseCoordSys, definition.basePathLength);
        }
        catch (error)
        {
            debug(context, definition.baseLine, DebugColor.RED);
            throw error;
        }

        /* Target path and target coordinate system */
        try
        {
            if (definition.isDeform == true)
            {
                definition.targetPath = constructPath(context, definition.targetCurve);
                definition.targetPathLength = evPathLength(context, definition.targetPath);

                /* Figure out first point and direction */
                var firstPoint = evPathTangentLines(context, definition.targetPath, [0]).tangentLines[0];

                /* Figure out target coordination system at the beginning of path */
                var targetPlane = evOwnerSketchPlane(context, { "entity" : definition.targetCurve });
                definition.targetCoordSys = coordSystem(firstPoint.origin, firstPoint.direction, targetPlane.normal);

                /* Show target coordinate system */
                addDebugCoordinateSystem(context, definition.targetCoordSys, definition.basePathLength);
            }
            else
            {
                /* Target path and plane same as base */
                definition.targetPath = definition.basePath;
                definition.targetPathLength = definition.basePathLength;
                definition.targetCoordSys = definition.baseCoordSys;
            }
        }
        catch (error)
        {
            debug(context, definition.targetCurve, DebugColor.RED);
            throw error;
        }


        /* Flex entities */
        definition.convertFunction = function(point)
            {
                return convertPoint(context, definition, point);
            };
        flexEntities(context, id, definition);
    }, {
            /* Default values */
            isBaseLineFlipped : false,
            edgeSamplingStep : 1.0 * millimeter,
            faceSamplingStep : 5.0 * millimeter,
            isShowSamples : false,
            isTaper : false,
            isTwist : false,
            isDeform : false,
            isSoftenTransition : true,
            isDebug : false
        });


/**
 * Editing logic function.
 */
export function onFlexFeatureChange(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, specifiedParameters is map, hiddenBodies is Query) returns map
{
    var newValue = undefined;

    /* Find out changed configuration */
    if (oldDefinition.isDebug1 != definition.isDebug1)
    {
        newValue = definition.isDebug1;
    }
    if (oldDefinition.isDebug2 != definition.isDebug2)
    {
        newValue = definition.isDebug2;
    }
    if (oldDefinition.isDebug3 != definition.isDebug3)
    {
        newValue = definition.isDebug3;
    }
    if (oldDefinition.isDebug4 != definition.isDebug4)
    {
        newValue = definition.isDebug4;
    }

    /* Propagate changed value */
    if (newValue == true)
    {
        if (definition.isDebug4 == true)
        {
            definition.isDebug3 = true;
        }
        if (definition.isDebug3 == true)
        {
            definition.isDebug2 = true;
        }
        if (definition.isDebug2 == true)
        {
            definition.isDebug1 = true;
        }
    }
    if (newValue == false)
    {
        if (definition.isDebug1 == false)
        {
            definition.isDebug2 = false;
        }
        if (definition.isDebug2 == false)
        {
            definition.isDebug3 = false;
        }
        if (definition.isDebug3 == false)
        {
            definition.isDebug4 = false;
        }
    }

    return definition;
}


/**
 * Flex (twist, taper, deform) given entities using FFD algorithm.
 * 
 * This replaces the previous piecewise filling approach with a cleaner FFD-based method:
 * 1. Extract B-spline surface representations
 * 2. Build FFD lattice around the surfaces
 * 3. Modify lattice control points based on taper/twist/deform settings
 * 4. Evaluate trivariate Bernstein polynomial to deform surfaces
 * 5. Create deformed surfaces directly
 */
function flexEntities(context is Context, id is Id, definition is map)
{
    if (definition.isDebug1 == false)
    {
        return;
    }

    /* Filter entities by type *****************************************************/
    var bodyFaces = evaluateQuery(context, qOwnedByBody(qEntityFilter(definition.entities, EntityType.BODY), EntityType.FACE));
    var selectedFaces = evaluateQuery(context, qEntityFilter(definition.entities, EntityType.FACE));
    var allFaces = concatenateArrays([bodyFaces, selectedFaces]);
    
    /* Handle edges and vertices separately (legacy support) **********************/
    var edgeTable = evaluateQuery(context, qEntityFilter(definition.entities, EntityType.EDGE));
    var vertexTable = evaluateQuery(context, qEntityFilter(definition.entities, EntityType.VERTEX));
    
    /* Process edges with legacy method if requested ******************************/
    if (size(edgeTable) > 0 && definition.isDebug2)
    {
        for (var edgeIndex = 0; edgeIndex < size(edgeTable); edgeIndex += 1)
        {
            try
            {
                var targetEdge = flexEdgeLegacy(context, id + "flexEdge" + edgeIndex, definition, edgeTable[edgeIndex]);
                if (definition.isShowSamples)
                {
                    addDebugEntities(context, targetEdge, DebugColor.BLUE);
                }
            }
            catch
            {
                reportFeatureWarning(context, id, "Translating edges failed.");
                addDebugEntities(context, edgeTable[edgeIndex], DebugColor.RED);
                return;
            }
        }
    }
    
    /* Process vertices with legacy method if requested ****************************/
    if (size(vertexTable) > 0 && definition.isDebug2)
    {
        for (var vertexIndex = 0; vertexIndex < size(vertexTable); vertexIndex += 1)
        {
            try
            {
                var targetVertex = flexVertexLegacy(context, id + "flexVertex" + vertexIndex, definition, vertexTable[vertexIndex]);
                addDebugEntities(context, targetVertex, DebugColor.BLACK);
            }
            catch
            {
                reportFeatureWarning(context, id, "Translating vertices failed.");
                addDebugEntities(context, vertexTable[vertexIndex], DebugColor.RED);
                return;
            }
        }
    }
    
    /* Process faces using FFD algorithm *******************************************/
    if (size(allFaces) == 0)
    {
        return;
    }
    
    if (definition.isDebug3 == false)
    {
        return;
    }
    
    try
    {
        flexFacesWithFFD(context, id, allFaces, definition);
    }
    catch (error)
    {
        reportFeatureWarning(context, id, "FFD deformation failed: " ~ error);
        for (var face in allFaces)
        {
            debug(context, face, DebugColor.RED);
        }
        return;
    }
}


/**
 * Convert point from the base coordinate system to the target coordinate system.
 */
function convertPoint(context is Context, definition is map, point is Vector) returns Vector
{
    /* Map to base coordinate system ***********************************************/
    point = fromWorld(definition.baseCoordSys, point);

    /* Calculate point relative position on base line ******************************/
    var pathPosition = point[0] / definition.basePathLength;
    var pathPositionOverflow = undefined;
    if (pathPosition < 0.0)
    {
        pathPosition = 0.0;
        pathPositionOverflow = point[0];
    }
    if (pathPosition > 1.0)
    {
        pathPosition = 1.0;
        pathPositionOverflow = point[0] - definition.basePathLength;
    }

    /* Calculate tool force (soften if requested) **********************************/
    var toolForce = pathPosition;
    if (definition.isSoftenTransition)
    {
        /* Use parabola to soften */
        if (pathPosition < 0.5)
        {
            toolForce = 2 * pathPosition * pathPosition;
        }
        else
        {
            toolForce = 1 - 2 * (1 - pathPosition) * (1 - pathPosition);
        }
    }

    /* Taper (in base coordinate system) *******************************************/
    if (definition.isTaper == true)
    {
        /* Calculate scale on current path position */
        var scale = (1.0 - toolForce) * definition.startScale + toolForce * definition.endScale;

        /* Scale point coordinates */
        var transform;
        if (definition.taperType == TaperType.TAPER_UNIFORMLY)
        {
            transform = scaleNonuniformly(1.0, scale, scale);
        }
        else if (definition.taperType == TaperType.TAPER_HORIZONTALLY)
        {
            transform = scaleNonuniformly(1.0, scale, 1.0);
        }
        else if (definition.taperType == TaperType.TAPER_VERTICALLY)
        {
            transform = scaleNonuniformly(1.0, 1.0, scale);
        }
        point = transform.linear * point;

        /* Scale overflow */
        if (definition.isScaleOverflow)
        {
            if (pathPositionOverflow != undefined)
            {
                pathPositionOverflow *= scale;
            }
        }
    }

    /* Twist (in base coordinate system) *******************************************/
    if (definition.isTwist == true)
    {
        /* Calculate angle on current path position */
        var angle = (1.0 - toolForce) * definition.startAngle + toolForce * definition.endAngle;

        /* Rotate point around x-axis */
        const transform = rotationAround(X_AXIS, angle);

        point = transform.linear * point;
    }

    /* Convert point coordinates into target path **********************************/
    /* Tangent at [pathPosition] */
    var targetTangent = evPathTangentLines(context, definition.targetPath, [pathPosition]).tangentLines[0];

    /* Normal at [pathPosition] */
    const rotMat = rotationMatrix3d(definition.targetCoordSys.zAxis, 90 * degree);
    const targetNormal = rotMat * targetTangent.direction;

    /* Use path extension if overflow */
    if (pathPositionOverflow != undefined)
    {
        targetTangent.origin += targetTangent.direction * pathPositionOverflow;
    }

    var targetPoint = targetTangent.origin + point[1] * targetNormal + point[2] * definition.targetCoordSys.zAxis;

    return targetPoint;
}


/**
 * Show coordinate system.
 */
function addDebugCoordinateSystem(context is Context, coordSystem is CoordSystem, length is ValueWithUnits)
{
    const ARROW_RADIUS = 0.05 * length;

    addDebugArrow(context, coordSystem.origin,
        coordSystem.origin + coordSystem.xAxis * length, ARROW_RADIUS, DebugColor.BLUE);
    addDebugArrow(context, coordSystem.origin,
        coordSystem.origin + yAxis(coordSystem) * length, ARROW_RADIUS, DebugColor.YELLOW);
    addDebugArrow(context, coordSystem.origin,
        coordSystem.origin + coordSystem.zAxis * length, ARROW_RADIUS, DebugColor.RED);

}


/**
 * FFD-based deformation of faces
 * 
 * This function replaces the piecewise surface filling approach with a direct
 * FFD deformation. It:
 * 1. Extracts B-spline surfaces from input faces
 * 2. Builds an FFD lattice along the base line
 * 3. Modifies lattice control points based on taper/twist/deform
 * 4. Deforms surfaces using trivariate Bernstein polynomials
 * 5. Creates deformed surfaces
 * 
 * @param context {Context} : The modeling context
 * @param id {Id} : Feature identifier
 * @param faces {array} : Array of face entities to deform
 * @param definition {map} : Feature definition with taper/twist/deform settings
 */
function flexFacesWithFFD(context is Context, id is Id, faces is array, definition is map)
{
    /* Extract B-spline surface representations ************************************/
    var surfaceDefinitions = [];
    var allControlPoints = [];
    
    for (var faceIndex = 0; faceIndex < size(faces); faceIndex += 1)
    {
        const inputFace = faces[faceIndex];
        
        // Get B-spline surface representation
        var surfaceDefinition = evSurfaceDefinition(context, {
            "face" : inputFace
        });
        
        // If not already a B-spline, approximate it
        if (surfaceDefinition.surfaceType != SurfaceType.SPLINE)
        {
            const approximation = evApproximateBSplineSurface(context, {
                "face" : inputFace
            });
            surfaceDefinition = approximation.bSplineSurface;
        }
        
        surfaceDefinitions = append(surfaceDefinitions, surfaceDefinition);
        
        // Collect all control points for bounding box calculation
        const controlPoints = surfaceDefinition.controlPoints;
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            for (var vIndex = 0; vIndex < size(controlPoints[0]); vIndex += 1)
            {
                allControlPoints = append(allControlPoints, controlPoints[uIndex][vIndex]);
            }
        }
    }
    
    /* Build FFD lattice along base line *******************************************/
    // Use a simple lattice with 2 spans along the base line direction
    // This provides control at start, middle, and end
    const lattice = buildFlexFFDLattice(allControlPoints, definition);
    
    /* Modify lattice based on taper/twist/deform settings ************************/
    modifyLatticeForFlex(lattice, definition);
    
    /* Deform each surface using the modified lattice ******************************/
    for (var surfaceIndex = 0; surfaceIndex < size(surfaceDefinitions); surfaceIndex += 1)
    {
        const surfaceDefinition = surfaceDefinitions[surfaceIndex];
        const controlPoints = surfaceDefinition.controlPoints;
        const weights = surfaceDefinition.weights;
        
        // Deform surface control points using FFD
        var deformedControlPoints = [];
        for (var uIndex = 0; uIndex < size(controlPoints); uIndex += 1)
        {
            var deformedRow = [];
            for (var vIndex = 0; vIndex < size(controlPoints[0]); vIndex += 1)
            {
                const originalPoint = controlPoints[uIndex][vIndex];
                
                // Convert to STU parametric space
                const stuCoords = convertWorldToSTUFlex(originalPoint, lattice);
                
                // Evaluate trivariate Bernstein polynomial to get deformed position
                const deformedPoint = evaluateTrivariateBernsteinFlex(stuCoords, lattice);
                
                deformedRow = append(deformedRow, deformedPoint);
            }
            deformedControlPoints = append(deformedControlPoints, deformedRow);
        }
        
        // Create the deformed B-spline surface
        const deformedSurfaceDefinition = bSplineSurface({
            "uDegree" : surfaceDefinition.uDegree,
            "vDegree" : surfaceDefinition.vDegree,
            "isUPeriodic" : surfaceDefinition.isUPeriodic,
            "isVPeriodic" : surfaceDefinition.isVPeriodic,
            "controlPoints" : controlPointMatrix(deformedControlPoints),
            "weights" : weights == undefined ? undefined : matrix(weights),
            "uKnots" : surfaceDefinition.uKnots,
            "vKnots" : surfaceDefinition.vKnots
        });
        
        // Create deformed surface
        opCreateBSplineSurface(context, id + ("surface" ~ surfaceIndex), {
            "bSplineSurface" : deformedSurfaceDefinition
        });
    }
}


/**
 * Builds an FFD lattice oriented along the base line for flex operations
 * 
 * The lattice is aligned with the base coordinate system and extends along
 * the base line direction with spans positioned for flex control.
 * 
 * @param allControlPoints {array} : Flat array of all surface control points
 * @param definition {map} : Feature definition with base coordinate system
 * @returns {map} : Lattice structure with control points and metadata
 */
function buildFlexFFDLattice(allControlPoints is array, definition is map) returns map
{
    /* Compute bounding box in base coordinate system ******************************/
    var minX = undefined;
    var maxX = undefined;
    var minY = undefined;
    var maxY = undefined;
    var minZ = undefined;
    var maxZ = undefined;
    
    for (var point in allControlPoints)
    {
        // Transform point to base coordinate system
        const localPoint = fromWorld(definition.baseCoordSys, point);
        
        if (minX == undefined || localPoint[0] < minX)
            minX = localPoint[0];
        if (maxX == undefined || localPoint[0] > maxX)
            maxX = localPoint[0];
        if (minY == undefined || localPoint[1] < minY)
            minY = localPoint[1];
        if (maxY == undefined || localPoint[1] > maxY)
            maxY = localPoint[1];
        if (minZ == undefined || localPoint[2] < minZ)
            minZ = localPoint[2];
        if (maxZ == undefined || localPoint[2] > maxZ)
            maxZ = localPoint[2];
    }
    
    const minCorner = vector(minX, minY, minZ);
    const maxCorner = vector(maxX, maxY, maxZ);
    
    /* Build lattice with 2 spans along X (base line), 1 span in Y and Z **********/
    const spanCountS = 2;  // 3 control points along base line for start/middle/end
    const spanCountT = 1;  // 2 control points in Y direction
    const spanCountU = 1;  // 2 control points in Z direction
    
    const controlPointCountS = spanCountS + 1;
    const controlPointCountT = spanCountT + 1;
    const controlPointCountU = spanCountU + 1;
    
    const totalControlPoints = controlPointCountS * controlPointCountT * controlPointCountU;
    
    // Compute axes in local (base) coordinate system
    const axisS = vector(maxCorner[0] - minCorner[0], 0 * meter, 0 * meter);
    const axisT = vector(0 * meter, maxCorner[1] - minCorner[1], 0 * meter);
    const axisU = vector(0 * meter, 0 * meter, maxCorner[2] - minCorner[2]);
    
    /* Initialize control points ***************************************************/
    var controlPoints = [];
    for (var indexS = 0; indexS < controlPointCountS; indexS += 1)
    {
        for (var indexT = 0; indexT < controlPointCountT; indexT += 1)
        {
            for (var indexU = 0; indexU < controlPointCountU; indexU += 1)
            {
                const fractionS = indexS / spanCountS;
                const fractionT = indexT / spanCountT;
                const fractionU = indexU / spanCountU;
                
                const localPosition = minCorner + axisS * fractionS + axisT * fractionT + axisU * fractionU;
                
                // Transform back to world coordinates
                const worldPosition = toWorld(definition.baseCoordSys, localPosition);
                
                controlPoints = append(controlPoints, worldPosition);
            }
        }
    }
    
    return {
        "minCorner" : minCorner,
        "maxCorner" : maxCorner,
        "axisS" : axisS,
        "axisT" : axisT,
        "axisU" : axisU,
        "spanCountS" : spanCountS,
        "spanCountT" : spanCountT,
        "spanCountU" : spanCountU,
        "controlPointCountS" : controlPointCountS,
        "controlPointCountT" : controlPointCountT,
        "controlPointCountU" : controlPointCountU,
        "totalControlPoints" : totalControlPoints,
        "controlPoints" : controlPoints,
        "baseCoordSys" : definition.baseCoordSys,
        "basePathLength" : definition.basePathLength,
        "crossS" : cross(axisT, axisU),
        "crossT" : cross(axisS, axisU),
        "crossU" : cross(axisS, axisT)
    };
}


/**
 * Modifies FFD lattice control points based on taper/twist/deform settings
 * 
 * This applies the flex transformations (taper, twist, deform) to the lattice
 * control points. Each control point is transformed using the convertPoint
 * function which encodes the taper/twist/deform logic.
 * 
 * @param lattice {map} : Lattice structure (modified in place)
 * @param definition {map} : Feature definition with transformation settings
 */
function modifyLatticeForFlex(lattice is map, definition is map)
{
    var modifiedControlPoints = [];
    
    for (var pointIndex = 0; pointIndex < lattice.totalControlPoints; pointIndex += 1)
    {
        const originalPoint = lattice.controlPoints[pointIndex];
        
        // Use the existing convertPoint function which applies taper/twist/deform
        const deformedPoint = definition.convertFunction(originalPoint);
        
        modifiedControlPoints = append(modifiedControlPoints, deformedPoint);
    }
    
    // Update lattice with modified control points
    lattice.controlPoints = modifiedControlPoints;
}


/**
 * Converts a 3D point in world space to parametric (S, T, U) coordinates in flex lattice
 * 
 * This is similar to the standard FFD conversion but uses the base coordinate system
 * for proper alignment with the flex operation.
 * 
 * @param worldPoint {Vector3} : Point in world coordinates
 * @param lattice {map} : Lattice structure
 * @returns {Vector3} : Parametric coordinates (S, T, U) in range [0, 1]
 */
function convertWorldToSTUFlex(worldPoint is Vector, lattice is map) returns Vector
{
    // Transform to base coordinate system first
    const localPoint = fromWorld(lattice.baseCoordSys, worldPoint);
    
    // Vector from minimum corner to the local point
    const minToLocal = localPoint - lattice.minCorner;
    
    // Use pre-computed cross products
    const crossS = lattice.crossS;
    const crossT = lattice.crossT;
    const crossU = lattice.crossU;
    
    // Compute parametric coordinates
    const epsilon = 1e-10 * meter * meter * meter;
    
    const numeratorS = dot(crossS, minToLocal);
    var denominatorS = dot(crossS, lattice.axisS);
    if (abs(denominatorS) < epsilon)
        denominatorS = epsilon;
    const paramS = numeratorS / denominatorS;
    
    const numeratorT = dot(crossT, minToLocal);
    var denominatorT = dot(crossT, lattice.axisT);
    if (abs(denominatorT) < epsilon)
        denominatorT = epsilon;
    const paramT = numeratorT / denominatorT;
    
    const numeratorU = dot(crossU, minToLocal);
    var denominatorU = dot(crossU, lattice.axisU);
    if (abs(denominatorU) < epsilon)
        denominatorU = epsilon;
    const paramU = numeratorU / denominatorU;
    
    return vector(paramS, paramT, paramU);
}


/**
 * Evaluates the trivariate Bernstein polynomial for flex FFD
 * 
 * Standard FFD evaluation using the lattice control points.
 * 
 * @param stuCoords {Vector3} : Parametric coordinates (s, t, u)
 * @param lattice {map} : Lattice structure with control points
 * @returns {Vector3} : Evaluated point in world space
 */
function evaluateTrivariateBernsteinFlex(stuCoords is Vector, lattice is map) returns Vector
{
    const paramS = stuCoords[0];
    const paramT = stuCoords[1];
    const paramU = stuCoords[2];
    
    var evaluatedPoint = vector(0 * meter, 0 * meter, 0 * meter);
    
    for (var indexS = 0; indexS < lattice.controlPointCountS; indexS += 1)
    {
        var intermediatePointT = vector(0 * meter, 0 * meter, 0 * meter);
        
        for (var indexT = 0; indexT < lattice.controlPointCountT; indexT += 1)
        {
            var intermediatePointU = vector(0 * meter, 0 * meter, 0 * meter);
            
            for (var indexU = 0; indexU < lattice.controlPointCountU; indexU += 1)
            {
                const linearIndex = getTernaryIndexFlex(indexS, indexT, indexU, lattice);
                const controlPoint = lattice.controlPoints[linearIndex];
                
                const basisU = bernsteinPolynomialFlex(lattice.spanCountU, indexU, paramU);
                
                intermediatePointU = intermediatePointU + controlPoint * basisU;
            }
            
            const basisT = bernsteinPolynomialFlex(lattice.spanCountT, indexT, paramT);
            
            intermediatePointT = intermediatePointT + intermediatePointU * basisT;
        }
        
        const basisS = bernsteinPolynomialFlex(lattice.spanCountS, indexS, paramS);
        
        evaluatedPoint = evaluatedPoint + intermediatePointT * basisS;
    }
    
    return evaluatedPoint;
}


/**
 * Computes the Bernstein basis function B_{i,n}(u) for flex FFD
 * 
 * @param degree {number} : Degree of the polynomial (n)
 * @param index {number} : Index of the basis function (i)
 * @param parameter {number} : Parameter value u in range [0, 1]
 * @returns {number} : Value of the Bernstein basis function
 */
function bernsteinPolynomialFlex(degree is number, index is number, parameter is number) returns number
{
    const binomialCoefficient = factorialFlex(degree) / (factorialFlex(index) * factorialFlex(degree - index));
    const termOneMinusU = (1 - parameter) ^ (degree - index);
    const termU = parameter ^ index;
    
    return binomialCoefficient * termOneMinusU * termU;
}


/**
 * Computes the factorial of a non-negative integer for flex FFD
 * 
 * @param n {number} : Non-negative integer
 * @returns {number} : n!
 */
function factorialFlex(n is number) returns number
{
    if (n <= 0)
        return 1;
    
    var result = 1;
    for (var i = n; i > 1; i -= 1)
    {
        result = result * i;
    }
    
    return result;
}


/**
 * Converts ternary lattice indices to linear index for flex FFD
 * 
 * @param indexS {number} : Index in S direction
 * @param indexT {number} : Index in T direction
 * @param indexU {number} : Index in U direction
 * @param lattice {map} : Lattice structure
 * @returns {number} : Linear index
 */
function getTernaryIndexFlex(indexS is number, indexT is number, indexU is number, lattice is map) returns number
{
    return indexS * lattice.controlPointCountT * lattice.controlPointCountU + 
           indexT * lattice.controlPointCountU + 
           indexU;
}


/**
 * Create target vertex (legacy method for edge/vertex support).
 */
function flexVertexLegacy(context is Context, id is Id, definition is map, vertex is Query) returns Query
{
    const sourcePoint = evVertexPoint(context, { "vertex" : vertex });
    const targetPoint = definition.convertFunction(sourcePoint);

    /* Create new vertex */
    opPoint(context, id, {
                "point" : targetPoint
            });

    return qCreatedBy(id);
}

/**
 * Create target edge (legacy method for edge support).
 */
function flexEdgeLegacy(context is Context, id is Id, definition is map, edge is Query) returns Query
{
    const targetPoints = flexEdgePointsLegacy(context, definition, edge);

    /* Create new edge (3D cubic spline through target points) */
    opFitSpline(context, id, { "points" : targetPoints });

    return qCreatedBy(id, EntityType.EDGE);
}

/**
 * Sample an edge and return target points (array) - legacy method.
 */
function flexEdgePointsLegacy(context is Context, definition is map, edge is Query) returns array
{
    /* Default values **************************************************************/
    if (definition.edgeSamplingStep == undefined)
    {
        definition.edgeSamplingStep = 0.5 * millimeter;
    }

    /* Sample count on edge ********************************************************/
    var edgeLength = evLength(context, { "entities" : edge });
    var sampleCount = round(edgeLength / definition.edgeSamplingStep);
    if (sampleCount % 2 == 0)
    {
        sampleCount += 1; // Make odd so that we have point on 0.5
    }
    if (sampleCount < 3)
    {
        sampleCount = 3;
    }

    /* Source edge tangents ********************************************************/
    const distributionArray = range(0, 1, sampleCount);
    var sourceTangents = evEdgeTangentLines(context, {
            "edge" : edge,
            "parameters" : distributionArray,
            "arcLengthParameterization" : true
        });

    /* evEdgeTangentLines does not return start/end vertices accurately,
       replace them with original */
    if (isClosed(context, edge) == false)
    {
        sourceTangents[0].origin = evVertexPoint(context, { "vertex" : qEdgeVertex(edge, true) });
        sourceTangents[size(sourceTangents) - 1].origin = evVertexPoint(context, { "vertex" : qEdgeVertex(edge, false) });
    }
    /* Convert source edge points to target coordinate system one by one ***********/
    const targetPoints = mapArray(sourceTangents, function(tangent)
        {
            var point = definition.convertFunction(tangent.origin);

            if (definition.isShowSamples == true)
            {
                addDebugPoint(context, tangent.origin, DebugColor.GREEN);
                addDebugPoint(context, point, DebugColor.BLUE);
            }

            return point;
        });

    return targetPoints;
}

/*
 * ============================================================================
 * DEPRECATED LEGACY FUNCTIONS 
 * ============================================================================
 * The functions below (splitEntities) are part of the old piecewise filling
 * approach and are no longer used in the FFD-based implementation.
 * They are kept here for reference but should not be called.
 * ============================================================================
 */

/**
 * DEPRECATED: Create subfaces by splitting entities (faces and bodies).
 * This function is no longer used with the FFD-based approach.
 */
function splitEntities(context is Context, id is Id, definition is map, entities is Query)
{
    /* Something to do? ************************************************************/
    if (isQueryEmpty(context, entities) == true)
    {
        return entities;
    }

    /* Calculate surface boundaries ************************************************/
    const entityBounds = evBox3d(context, {
                "topology" : entities,
                "tight" : false
            });

    /* Round to the closest boundary */
    var splitDistance = definition.faceSamplingStep;
    var surfaceSize = entityBounds.maxCorner - entityBounds.minCorner;
    surfaceSize[0] = ceil(surfaceSize[0] / splitDistance + 0.001) * splitDistance;
    surfaceSize[1] = ceil(surfaceSize[1] / splitDistance + 0.001) * splitDistance;
    surfaceSize[2] = ceil(surfaceSize[2] / splitDistance + 0.001) * splitDistance;

    /* Figure out middle point */
    var surfaceMiddle = entityBounds.minCorner + surfaceSize / 2;


    /* Create cutting planes *******************************************************/
    var planeIndex = 0;

    /* Planes with normal X */
    var x = 0 * millimeter;
    while (x <= surfaceSize[0])
    {
        opPlane(context, id + "plane" + planeIndex, {
                    "plane" : plane(vector(entityBounds.minCorner[0] + x, surfaceMiddle[1], surfaceMiddle[2]), X_DIRECTION),
                    "width" : surfaceSize[1],
                    "height" : surfaceSize[2]
                });

        x = x + splitDistance;
        planeIndex += 1;
    }

    /* Planes with normal Y */
    var y = 0 * millimeter;
    while (y <= surfaceSize[1])
    {
        opPlane(context, id + "plane" + planeIndex, {
                    "plane" : plane(vector(surfaceMiddle[0], entityBounds.minCorner[1] + y, surfaceMiddle[2]), Y_DIRECTION),
                    "width" : surfaceSize[0],
                    "height" : surfaceSize[2]
                });

        y = y + splitDistance;
        planeIndex += 1;
    }

    /* Planes with normal Z */
    var z = 0 * millimeter;
    while (z <= surfaceSize[2])
    {
        opPlane(context, id + "plane" + planeIndex, {
                    "plane" : plane(vector(surfaceMiddle[0], surfaceMiddle[1], entityBounds.minCorner[2] + z), Z_DIRECTION),
                    "width" : surfaceSize[0],
                    "height" : surfaceSize[1]
                });

        z = z + splitDistance;
        planeIndex += 1;
    }

    /* Split faces one by one ******************************************************/
    var totalSubFaceTable = [];

    for (var entityIndex, entity in evaluateQuery(context, entities))
    {
        /* Create temporary copy of the surfaces ***************************************/
        var bodies = evaluateQuery(context, qEntityFilter(entity, EntityType.BODY));
        if (size(bodies) > 0)
        {
            offsetSurface(context, id + ("offsetSurface" ~ entityIndex), {
                        "surfacesAndFaces" : qOwnedByBody(entity, EntityType.FACE),
                        "offset" : 0.0 * meter
                    });
        }
        else
        {
            offsetSurface(context, id + ("offsetSurface" ~ entityIndex), {
                        "surfacesAndFaces" : entity,
                        "offset" : 0.0 * meter
                    });
        }
        var subFaces = qCreatedBy(id + ("offsetSurface" ~ entityIndex), EntityType.FACE);

        /* Split entity into subfaces ****************************************************/
        opSplitFace(context, id + ("splitFace" ~ entityIndex), {
                    "faceTargets" : subFaces,
                    "planeTools" : qCreatedBy(id + "plane", EntityType.FACE),
                    "keepToolSurfaces" : false
                });

        /* Link parent entity to the subfaces */
        setAttribute(context, {
                    "entities" : entity,
                    "name" : "subFaces",
                    "attribute" : subFaces
                });

        /* Find quige points to the face */
        for (var subFace in evaluateQuery(context, subFaces))
        {
            var tangentPlanes = evFaceTangentPlanes(context, {
                    "face" : subFace,
                    "parameters" : [
                        vector(0.50, 0.50),
                        vector(0.50, 0.05),
                        vector(0.50, 0.95),
                        vector(0.05, 0.50),
                        vector(0.95, 0.50),
                    ]
                });

            setAttribute(context, {
                        "entities" : subFace,
                        "name" : "tangentPlanes",
                        "attribute" : tangentPlanes
                    });
        }

        /* Add sub faces into query */
        totalSubFaceTable = append(totalSubFaceTable, subFaces);
    }

    /* Delete cutting planes *******************************************************/
    opDeleteBodies(context, id + "deleteBodies", {
                "entities" : qUnion([qCreatedBy(id + "plane")])
            });

    return qUnion(totalSubFaceTable);
}


