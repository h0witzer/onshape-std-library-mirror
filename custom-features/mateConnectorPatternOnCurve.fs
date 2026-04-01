FeatureScript 2909;
// Mate Connector Pattern on Curve
// Creates a pattern of mate connectors along a curve using flexible spacing options
// from the centralized spacingUtils module. Supports free-form curve selection (CURVE mode)
// and face-boundary or edge-subset selection (FACE mode) with optional inward/outward path
// offset, path-tangent and global-reference orientation modes, and can wrap all created
// connectors as a named query variable.

import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/queryVariable.fs", version : "2909.0");
import(path : "onshape/std/offsetcurvetype.gen.fs", version : "2909.0");

// Import spacing utilities for EQUAL / DISTANCE / BESTFIT curve pattern logic
// (same module used by onlyTabs.fs and sheetMetalStitchCutBend.fs)
export import(path : "c51f6558b7346f455a634ff5/cf14633de6fca78124306ce9/8ce820287d75ed2e92412d90", version : "cf26b6d26aa41f8853237904"); // spacingUtils.fs

// Tolerance for normalized path parameter comparisons (0.0 to 1.0 range).
// Prevents floating-point precision errors from excluding connectors positioned exactly at the end of the effective zone.
const NORMALIZED_PARAMETER_TOLERANCE = 1e-9;

// ============================================================================
// ENUMS
// ============================================================================

/**
 * Selects how the curve path is provided to the feature.
 * @value CURVE : The user picks one or more connected edges forming a free-form continuous curve.
 * @value FACE  : The user picks a single face (full boundary becomes the path) or one or more
 *               connected edges (used directly as the path). When a face is selected the optional
 *               inward/outward offset is also available.
 */
export enum PathSelectionMode
{
    annotation { "Name" : "Curve" }
    CURVE,
    annotation { "Name" : "Face" }
    FACE
}

/**
 * Controls how the orientation of each mate connector is derived.
 * @value PATH_TANGENT : Each connector's Z-axis follows the curve tangent at its position;
 *        X-axis is an arbitrary perpendicular computed from that tangent.
 * @value GLOBAL_REFERENCE : All connectors share the orientation of a user-specified
 *        reference entity (mate connector or planar face), placed at each path position.
 */
export enum MateConnectorAlignmentMode
{
    annotation { "Name" : "Path tangent" }
    PATH_TANGENT,
    annotation { "Name" : "Global reference" }
    GLOBAL_REFERENCE
}

// ============================================================================
// FEATURE DEFINITION
// ============================================================================

annotation { "Feature Type Name" : "Mate Connector Pattern on Curve",
        "Feature Type Description" : "Creates a pattern of mate connectors along a curve with flexible spacing and alignment options. Output can be wrapped as a query variable.",
        "Feature Name Template" : "Mate Connector Pattern#featureName" }
export const mateConnectorPatternOnCurve = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Path source selection - toggle between direct edge picking and face-boundary mode
        annotation { "Name" : "Path source",
                    "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.pathSelectionMode is PathSelectionMode;

        if (definition.pathSelectionMode == PathSelectionMode.CURVE)
        {
            // CURVE mode: user picks one or more connected edges forming a continuous free-form curve
            annotation { "Name" : "Path curve",
                        "Filter" : EntityType.EDGE,
                        "UIHint" : UIHint.SHOW_CREATE_SELECTION }
            definition.pathEdges is Query;
        }
        else
        {
            // FACE mode: user picks a face to use its full boundary, or picks one or more connected
            // edges on a face to pattern along a specific subset of the boundary.
            // When edges are selected, they are used directly as the path and no face boundary
            // expansion occurs. A face selection is still required when "Offset from path" is enabled.
            annotation { "Name" : "Face or edges",
                        "Description" : "Select a face to pattern along its boundary, or select connected edges to pattern along a specific subset of the boundary.",
                        "Filter" : EntityType.FACE || (EntityType.EDGE && ConstructionObject.NO),
                        "UIHint" : UIHint.SHOW_CREATE_SELECTION }
            definition.pathFace is Query;

            // Face path offset - shifts connector origins laterally within the face plane,
            // perpendicular to the curve tangent at each position (offset curve behavior)
            annotation { "Name" : "Offset from path" }
            definition.useFaceNormalOffset is boolean;

            if (definition.useFaceNormalOffset)
            {
                annotation { "Name" : "Offset distance",
                            "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                isLength(definition.faceNormalOffset, NONNEGATIVE_ZERO_DEFAULT_LENGTH_BOUNDS);

                annotation { "Name" : "Flip direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.faceNormalOffsetFlip is boolean;
            }
        }

        // Spacing configuration - uses centralized spacing predicate from spacingUtils
        // Provides EQUAL, DISTANCE, and BESTFIT spacing types with offset support
        curvePatternSpacingPredicate(definition);

        // Alignment mode determines how each connector is oriented
        annotation { "Name" : "Alignment mode",
                    "UIHint" : [UIHint.SHOW_LABEL, UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.alignmentMode is MateConnectorAlignmentMode;

        // Reference orientation selector - shown only in GLOBAL_REFERENCE mode
        if (definition.alignmentMode == MateConnectorAlignmentMode.GLOBAL_REFERENCE)
        {
            annotation { "Name" : "Reference orientation",
                        "Description" : "Select a mate connector or planar face whose orientation will be applied to all pattern instances.",
                        "Filter" : BodyType.MATE_CONNECTOR || (EntityType.FACE && GeometryType.PLANE),
                        "MaxNumberOfPicks" : 1 }
            definition.referenceOrientation is Query;
        }

        // Optional owner body to associate all created mate connectors with a specific body
        annotation { "Name" : "Specify owner body" }
        definition.specifyOwnerBody is boolean;

        if (definition.specifyOwnerBody)
        {
            annotation { "Name" : "Owner body",
                        "Filter" : EntityType.BODY && (BodyType.SOLID || GeometryType.MESH
                                || BodyType.SHEET || BodyType.WIRE || BodyType.COMPOSITE)
                                && AllowMeshGeometry.YES && ModifiableEntityOnly.YES,
                        "MaxNumberOfPicks" : 1 }
            definition.ownerBody is Query;
        }

        // Query variable output - wraps all created mate connectors as a named QV
        annotation { "Name" : "Create query variable" }
        definition.createQueryVariable is boolean;

        if (definition.createQueryVariable)
        {
            annotation { "Name" : "Query variable name",
                        "Default" : "mateConnectorPattern" }
            definition.queryVariableName is string;
        }
    }
    {
        // ----------------------------------------------------------------
        // Resolve path edges and any face-specific data based on selection mode
        // ----------------------------------------------------------------
        var activePathEdges is Query = qNothing();

        // When FACE mode with path offset is enabled, holds the query for the temporary offset wire
        // body created by buildFacePathOffsetWire. The body is deleted after tangent lines are extracted
        // so it does not appear in the final qCreatedBy result used for query variable registration.
        var offsetWireBody is Query = qNothing();

        if (definition.pathSelectionMode == PathSelectionMode.FACE)
        {
            // Determine whether the user selected a face (full-boundary mode) or edges (subset mode)
            // by inspecting what was actually put in the unified pathFace picker.
            const selectedFaces = evaluateQuery(context, qEntityFilter(definition.pathFace, EntityType.FACE));
            const selectedEdges = evaluateQuery(context, qEntityFilter(definition.pathFace, EntityType.EDGE));

            if (size(selectedFaces) > 0)
            {
                // Full boundary mode: collect every edge that borders the selected face.
                activePathEdges = qAdjacent(definition.pathFace, AdjacencyType.EDGE, EntityType.EDGE);

                if (isQueryEmpty(context, activePathEdges))
                {
                    throw regenError("The selected face has no boundary edges.", ["pathFace"]);
                }
            }
            else if (size(selectedEdges) > 0)
            {
                // Edge subset mode: use exactly the edges the user selected.
                activePathEdges = qEntityFilter(definition.pathFace, EntityType.EDGE);
            }
            else
            {
                throw regenError("Select a face or one or more connected edges to define the path.", ["pathFace"]);
            }

            // When a path offset is requested, use buildFacePathOffsetWire to build a true offset wire.
            // This delegates corner mitering and trimming to the kernel, matching the behaviour of the
            // standard "Offset curve" feature.
            // When a face was explicitly selected, use it directly as the offset surface target.
            // When only edges were selected, infer the containing face by finding the face that is
            // adjacent to every selected edge (the intersection of each edge's adjacent faces).
            if (definition.useFaceNormalOffset && definition.faceNormalOffset > 0 * meter)
            {
                var offsetTargetFace is Query = qNothing();
                if (size(selectedFaces) > 0)
                {
                    // Face was explicitly selected — use it as-is.
                    offsetTargetFace = qEntityFilter(definition.pathFace, EntityType.FACE);
                }
                else
                {
                    // Infer the containing face from the selected edges: find the face that
                    // is adjacent to ALL of the selected edges (the common bounding face).
                    const edgeList = evaluateQuery(context, activePathEdges);
                    if (size(edgeList) > 0)
                    {
                        var commonFaces = qAdjacent(edgeList[0], AdjacencyType.FACE, EntityType.FACE);
                        for (var edgeIndex = 1; edgeIndex < size(edgeList); edgeIndex += 1)
                        {
                            const nextEdgeFaces = qAdjacent(edgeList[edgeIndex], AdjacencyType.FACE, EntityType.FACE);
                            commonFaces = qIntersection([commonFaces, nextEdgeFaces]);
                        }
                        offsetTargetFace = commonFaces;
                    }

                    if (isQueryEmpty(context, offsetTargetFace))
                    {
                        throw regenError("Unable to infer a containing face from the selected edges for the offset. Ensure the edges all lie on the same face, or select the face explicitly.", ["pathFace"]);
                    }
                }

                const offsetResult = buildFacePathOffsetWire(
                    context,
                    id + "offsetWire",
                    activePathEdges,
                    offsetTargetFace,
                    definition.faceNormalOffset,
                    definition.faceNormalOffsetFlip
                );
                offsetWireBody = offsetResult.offsetWireBody;
                activePathEdges = offsetResult.offsetWireEdges;
            }
        }
        else
        {
            activePathEdges = definition.pathEdges;
        }

        // Build a continuous path from the resolved edges
        const path = try(constructPath(context, activePathEdges));
        if (path == undefined)
        {
            if (definition.pathSelectionMode == PathSelectionMode.FACE)
            {
                throw regenError("Unable to build a continuous path from the selected face or edges. Ensure all selected edges are connected and form a single chain.", ["pathFace"]);
            }
            else
            {
                throw regenError("Unable to build a continuous path from the selected edges. Ensure all edges are connected.", ["pathEdges"]);
            }
        }

        const totalPathLength = evPathLength(context, path);

        // computeCurvePatternSpacing expects definition.edges for its length query,
        // so bridge activePathEdges into that field before calling it
        var spacingDefinition = definition;
        spacingDefinition.edges = activePathEdges;
        spacingDefinition = computeCurvePatternSpacing(context, id, spacingDefinition);

        const instanceCount = spacingDefinition.instanceCount;

        // Resolve start and end offsets from the spacing definition
        var startOffset = 0 * meter;
        var endOffset = 0 * meter;

        if (definition.useOffsets == true)
        {
            if (!definition.twoOffsets)
            {
                startOffset = definition.offset;
                endOffset = definition.offset;
            }
            else
            {
                if (!definition.oppositeDirection)
                {
                    startOffset = definition.offset1;
                    endOffset = definition.offset2;
                }
                else
                {
                    // Flipped: swap which offset applies to which end
                    startOffset = definition.offset2;
                    endOffset = definition.offset1;
                }
            }
        }

        // Validate that offsets leave a positive effective length
        const effectivePathLength = totalPathLength - startOffset - endOffset;
        if (effectivePathLength <= 0 * meter)
        {
            throw regenError("The specified offsets exceed the total path length.", ["useOffsets"]);
        }

        // Compute normalized arc-length parameters (0 to 1) for each connector position
        const normalizedParameters = computeMateConnectorParameters(
            totalPathLength,
            effectivePathLength,
            startOffset,
            endOffset,
            instanceCount,
            definition,
            path.closed
        );

        if (size(normalizedParameters) == 0)
        {
            throw regenError("No mate connectors can be placed with the specified spacing parameters. Adjust spacing, instance count, or offsets.");
        }

        // Evaluate tangent lines at each computed position along the path
        const tangentEvaluation = evPathTangentLines(context, path, normalizedParameters);
        const tangentLines = tangentEvaluation.tangentLines;

        // If an offset wire was created to compute the path, delete it now before any further
        // qCreatedBy queries so it does not pollute the mate connector query variable output.
        if (!isQueryEmpty(context, offsetWireBody))
        {
            opDeleteBodies(context, id + "deleteOffsetWire", { "entities" : offsetWireBody });
        }

        // Resolve global reference orientation axes if applicable
        var globalReferenceXAxis = undefined;
        var globalReferenceZAxis = undefined;

        if (definition.alignmentMode == MateConnectorAlignmentMode.GLOBAL_REFERENCE)
        {
            const resolvedOrientation = resolveGlobalReferenceOrientation(context, definition.referenceOrientation);
            globalReferenceXAxis = resolvedOrientation.xAxis;
            globalReferenceZAxis = resolvedOrientation.zAxis;
        }

        // Determine owner body query - qNothing() if none is specified
        const ownerBodyQuery = definition.specifyOwnerBody ? definition.ownerBody : qNothing();

        // Create one mate connector at each computed position
        for (var connectorIndex = 0; connectorIndex < size(tangentLines); connectorIndex += 1)
        {
            const tangentLine = tangentLines[connectorIndex];
            var connectorCoordinateSystem;

            // The connector origin is taken directly from the tangent evaluation.
            // When a path offset was requested, this is already a point on the offset wire
            // (properly mitered/trimmed by @opOffsetCurveOnFace), so no further translation is needed.
            const connectorOrigin = tangentLine.origin;

            if (definition.alignmentMode == MateConnectorAlignmentMode.PATH_TANGENT)
            {
                // Z-axis follows curve tangent; X-axis is an arbitrary perpendicular to that tangent
                connectorCoordinateSystem = coordSystem(
                    connectorOrigin,
                    perpendicularVector(tangentLine.direction),
                    tangentLine.direction
                );
            }
            else
            {
                // Global reference mode: orientation from reference entity, origin at path position
                connectorCoordinateSystem = coordSystem(
                    connectorOrigin,
                    globalReferenceXAxis,
                    globalReferenceZAxis
                );
            }

            opMateConnector(context, id + "mateConnector" + connectorIndex, {
                        "coordSystem" : connectorCoordinateSystem,
                        "owner" : ownerBodyQuery
                    });
        }

        // Register created mate connectors as a named query variable if requested
        if (definition.createQueryVariable)
        {
            verifyVariableNameIsValid(definition.queryVariableName, "queryVariableName");

            var variableAlreadyExists = false;
            try silent
            {
                getVariable(context, definition.queryVariableName);
                variableAlreadyExists = true;
            }
            if (variableAlreadyExists)
            {
                throw regenError(ErrorStringEnum.QUERY_VARIABLE_NAME_ALREADY_USED_IN_NON_QUERY_VARIABLE,
                    ["queryVariableName"]);
            }

            const allCreatedMateConnectors = qCreatedBy(id, EntityType.BODY);
            setQueryVariable(context, definition.queryVariableName, allCreatedMateConnectors);
        }

        // Update the feature tree label to show the QV name when applicable
        const featureDisplaySuffix = definition.createQueryVariable ?
            " [QV: " ~ definition.queryVariableName ~ "]" :
            "";
        setFeatureComputedParameter(context, id, { "name" : "featureName", "value" : featureDisplaySuffix });
    },
    {});

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

/**
 * Computes normalized arc-length parameters (0.0 to 1.0) for each mate connector
 * along the path, based on the active spacing type and end mode.
 *
 * Parameters:
 *   totalPathLength {ValueWithUnits}     - Full arc length of the path
 *   effectivePathLength {ValueWithUnits} - Path length available after removing both offsets
 *   startOffset {ValueWithUnits}         - Physical distance offset from the path start
 *   endOffset {ValueWithUnits}           - Physical distance offset from the path end
 *   instanceCount {number}               - Number of mate connectors to place
 *   definition {map}                     - Feature definition with spacingType, endMode, distance
 *   pathIsClosed {boolean}               - Whether the path forms a closed loop
 *
 * Returns:
 *   {array} - Array of normalized parameters in [0, 1] range, one per connector position
 */
function computeMateConnectorParameters(totalPathLength is ValueWithUnits, effectivePathLength is ValueWithUnits, startOffset is ValueWithUnits, endOffset is ValueWithUnits, instanceCount is number, definition is map, pathIsClosed is boolean) returns array
{
    var normalizedParameters = [];
    const startNormalized = startOffset / totalPathLength;
    const endNormalized = 1 - (endOffset / totalPathLength);

    if (definition.spacingType == CurvePatternSpacingType.EQUAL ||
        definition.spacingType == CurvePatternSpacingType.BESTFIT)
    {
        if (definition.endMode == CurvePatternEndMode.GAP)
        {
            // GAP end mode: connectors distributed with equal gaps at both ends of the effective zone.
            // Each connector sits at the center of an evenly divided pitch slot.
            // Layout: |__gap__|connector|__gap__|connector|__gap__|
            const pitchLength = effectivePathLength / instanceCount;
            const pitchNormalized = pitchLength / totalPathLength;

            for (var instanceIndex = 0; instanceIndex < instanceCount; instanceIndex += 1)
            {
                normalizedParameters = append(normalizedParameters,
                    startNormalized + pitchNormalized * (instanceIndex + 0.5));
            }
        }
        else
        {
            // INSTANCE end mode: first connector at the start of the effective zone,
            // last connector at the end of the effective zone.
            // Layout: |connector|__gap__|connector|__gap__|connector|
            if (instanceCount == 1)
            {
                // Single connector sits at the start of the effective zone
                normalizedParameters = append(normalizedParameters, startNormalized);
            }
            else
            {
                const pitchLength = effectivePathLength / (instanceCount - 1);
                const pitchNormalized = pitchLength / totalPathLength;

                for (var instanceIndex = 0; instanceIndex < instanceCount; instanceIndex += 1)
                {
                    normalizedParameters = append(normalizedParameters,
                        startNormalized + pitchNormalized * instanceIndex);
                }

                // On a closed loop the last parameter (≈ startNormalized + 1.0) maps to the
                // same 3D point as the first, producing a duplicate connector. Respace: distribute
                // all instanceCount connectors with equal pitch across the full effective loop so
                // no two endpoints coincide.
                if (pathIsClosed && size(normalizedParameters) >= 2)
                {
                    const lastParam = normalizedParameters[size(normalizedParameters) - 1];
                    if (lastParam >= 1.0 - NORMALIZED_PARAMETER_TOLERANCE)
                    {
                        normalizedParameters = [];
                        const loopPitchNormalized = (effectivePathLength / instanceCount) / totalPathLength;
                        for (var instanceIndex = 0; instanceIndex < instanceCount; instanceIndex += 1)
                        {
                            normalizedParameters = append(normalizedParameters,
                                startNormalized + loopPitchNormalized * instanceIndex);
                        }
                    }
                }
            }
        }
    }
    else if (definition.spacingType == CurvePatternSpacingType.DISTANCE)
    {
        // DISTANCE mode: connectors placed at fixed pitch intervals beginning at the start offset.
        // Placement stops when the next position would exceed the effective end of the path.
        const pitchNormalized = definition.distance / totalPathLength;

        for (var instanceIndex = 0; instanceIndex < instanceCount; instanceIndex += 1)
        {
            const parameterAtInstance = startNormalized + pitchNormalized * instanceIndex;

            if (parameterAtInstance > endNormalized + NORMALIZED_PARAMETER_TOLERANCE)
            {
                break;
            }

            normalizedParameters = append(normalizedParameters, parameterAtInstance);
        }

        // On a closed loop the last placed instance may land back at the path start (param ≥ 1.0),
        // duplicating the first connector. Drop it.
        if (pathIsClosed && size(normalizedParameters) >= 2)
        {
            const lastParam = normalizedParameters[size(normalizedParameters) - 1];
            if (lastParam >= 1.0 - NORMALIZED_PARAMETER_TOLERANCE)
            {
                normalizedParameters = subArray(normalizedParameters, 0, size(normalizedParameters) - 1);
            }
        }
    }

    return normalizedParameters;
}

/**
 * Creates a true offset wire from a set of face boundary edges using the kernel
 * opOffsetCurveOnFace operation. Corner mitering and wire trimming are handled by
 * the kernel, producing the same result as the standard "Offset curve" feature.
 *
 * This helper is defined here so the feature is self-contained. The same function
 * is also exported from spacingUtils.fs for use by other features once spacingUtils
 * is next published.
 *
 * Parameters:
 *   context {Context}                  - The active context
 *   wireOperationId {Id}               - A unique sub-ID for the offset wire operation
 *   sourceEdges {Query}                - The face boundary edges to offset from
 *   targetFace {Query}                 - The face that the edges lie on, used by the kernel
 *                                       as the offset surface. May be explicitly user-selected
 *                                       or inferred automatically from the source edges.
 *   offsetDistance {ValueWithUnits}    - The offset distance (must be positive)
 *   flipDirection {boolean}            - When true the offset is in the opposite lateral direction
 *
 * Returns:
 *   {map} - A map with fields:
 *       offsetWireBody  {Query} - The created wire body (delete after sampling)
 *       offsetWireEdges {Query} - Edges of the first wire body, ready for constructPath
 */
function buildFacePathOffsetWire(context is Context, wireOperationId is Id, sourceEdges is Query, targetFace is Query, offsetDistance is ValueWithUnits, flipDirection is boolean) returns map
{
    try
    {
        @opOffsetCurveOnFace(context, wireOperationId, {
                    "edges"             : sourceEdges,
                    "oppositeDirection" : flipDirection,
                    "imprint"           : false,
                    "extend"            : false,
                    "distance"          : offsetDistance,
                    "offsetType"        : OffsetCurveType.EUCLIDEAN,
                    "targets"           : targetFace,
                    "roundedCorners"    : false
                });
    }

    const wireBodies = evaluateQuery(context, qCreatedBy(wireOperationId, EntityType.BODY));
    if (size(wireBodies) == 0)
    {
        throw regenError("Unable to build offset path. The offset distance may be too large for the selected geometry. Reduce the offset distance or disable the offset.", ["faceNormalOffset"]);
    }

    return {
        "offsetWireBody"  : qCreatedBy(wireOperationId, EntityType.BODY),
        "offsetWireEdges" : qOwnedByBody(wireBodies[0], EntityType.EDGE)
    };
}

/**
 * Resolves the orientation axes from a user-selected reference entity.
 * Supports mate connectors (preferred) and planar faces as orientation sources.
 * Used in GLOBAL_REFERENCE mode to provide a consistent orientation for all connectors.
 *
 * Parameters:
 *   context {Context}                       - The active context
 *   referenceOrientationQuery {Query}       - Query selecting a mate connector or planar face
 *
 * Returns:
 *   {map} - A map with fields:
 *       xAxis {Vector} - X-axis unit vector of the resolved orientation
 *       zAxis {Vector} - Z-axis unit vector of the resolved orientation
 */
function resolveGlobalReferenceOrientation(context is Context, referenceOrientationQuery is Query) returns map
{
    // Prefer mate connector if one is selected - use its full coordinate system orientation
    const resolvedMateConnectors = evaluateQuery(context, qBodyType(referenceOrientationQuery, BodyType.MATE_CONNECTOR));
    if (size(resolvedMateConnectors) > 0)
    {
        const referenceCsys = evMateConnector(context, { "mateConnector" : resolvedMateConnectors[0] });
        return { "xAxis" : referenceCsys.xAxis, "zAxis" : referenceCsys.zAxis };
    }

    // Fall back to planar face - use face normal as Z-axis and face X direction as X-axis
    const resolvedFaces = evaluateQuery(context, qEntityFilter(referenceOrientationQuery, EntityType.FACE));
    if (size(resolvedFaces) > 0)
    {
        const referencePlane = evPlane(context, { "face" : resolvedFaces[0] });
        return { "xAxis" : referencePlane.x, "zAxis" : referencePlane.normal };
    }

    throw regenError("Could not resolve a reference orientation. Select a mate connector or a planar face.",
        ["referenceOrientation"]);
}
