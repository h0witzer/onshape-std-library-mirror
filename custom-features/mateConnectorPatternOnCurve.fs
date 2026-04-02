FeatureScript 2909;
// Mate Connector Pattern on Curve
// Creates a pattern of mate connectors along a curve using flexible spacing options
// from the centralized spacingUtils module. Supports free-form curve selection (CURVE mode)
// and face-boundary or edge-subset selection (FACE mode) with optional inward/outward path
// offset, path-tangent and global-reference orientation modes, and can wrap all created
// connectors as a named query variable.

import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/queryVariable.fs", version : "2909.0");
import(path : "onshape/std/offsetCurveOnFace.fs", version : "2909.0");

// Import spacing utilities for EQUAL / DISTANCE / BESTFIT curve pattern logic
// (same module used by onlyTabs.fs and sheetMetalStitchCutBend.fs)
export import(path : "c51f6558b7346f455a634ff5/0557c32c4fd52100d8f288b8/8ce820287d75ed2e92412d90", version : "12af7586d1c73aeacaba1581"); // spacingUtils.fs

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
 * @value FACE_NORMAL : Each connector's Z-axis follows the local face normal at the connector's
 *        exact position on the surface. The normal is evaluated via surface projection so it is
 *        correct for planar, cylindrical, conical, and any other curved surface. When multiple
 *        faces are selected each group independently evaluates its own face, so connectors on
 *        different faces (e.g. six faces of a cube, or a cylinder and its end caps) each align
 *        to their own local surface normal. Falls back to PATH_TANGENT when the face cannot be
 *        determined or surface projection fails.
 * @value GLOBAL_REFERENCE : All connectors share the orientation of a user-specified
 *        reference entity (mate connector or planar face), placed at each path position.
 */
export enum MateConnectorAlignmentMode
{
    annotation { "Name" : "Path tangent" }
    PATH_TANGENT,
    annotation { "Name" : "Face normal" }
    FACE_NORMAL,
    annotation { "Name" : "Global reference" }
    GLOBAL_REFERENCE
}

// ============================================================================
// FEATURE DEFINITION
// ============================================================================

annotation { "Feature Type Name" : "The Hole Shebang",
        "Feature Type Description" : "Creates a pattern of mate connectors along a curve with flexible spacing and alignment options. Output can be wrapped as a query variable. Does not create holes.",
        "Feature Name Template" : "The Hole Shebang#featureName" }
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

        // Skip instances - allows individual connectors to be excluded from placement
        // by their 1-based slot index in the full (unskipped) ordered sequence.
        annotation { "Name" : "Skip instances" }
        definition.skipInstances is boolean;

        if (definition.skipInstances)
        {
            annotation { "Name" : "Instances to skip",
                        "Item name" : "instance",
                        "Item label template" : "#index",
                        "Show labels only" : true,
                        "UIHint" : [UIHint.INITIAL_FOCUS, UIHint.PREVENT_ARRAY_REORDER, UIHint.ALLOW_ARRAY_FOCUS] }
            definition.skippedInstances is array;

            for (var instance in definition.skippedInstances)
            {
                annotation { "Name" : "Index" }
                isInteger(instance.index, { (unitless) : [1, 1, 1e5] } as IntegerBoundSpec);
            }
        }
    }
    {
        // ----------------------------------------------------------------
        // Resolve global reference orientation axes up-front (used across all groups)
        // ----------------------------------------------------------------
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

        // ----------------------------------------------------------------
        // Collect pattern groups.
        // Each group is a map with:
        //   edges     {Query} - edges forming this group's path
        //   faceQuery {Query} - the face this group belongs to, or qNothing() when unknown
        //
        // FACE mode, face(s) selected : one group per selected face (full boundary)
        // FACE mode, edges selected   : one group per connected-edge component
        // CURVE mode                  : single group from the user's edge selection
        // ----------------------------------------------------------------
        var patternGroups = [];

        if (definition.pathSelectionMode == PathSelectionMode.FACE)
        {
            const selectedFaces = evaluateQuery(context, qEntityFilter(definition.pathFace, EntityType.FACE));
            const selectedEdges = evaluateQuery(context, qEntityFilter(definition.pathFace, EntityType.EDGE));

            if (size(selectedFaces) > 0)
            {
                // Full boundary mode: one group per selected face
                for (var faceIndex = 0; faceIndex < size(selectedFaces); faceIndex += 1)
                {
                    const face = selectedFaces[faceIndex];
                    const boundaryEdges = qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE);
                    if (!isQueryEmpty(context, boundaryEdges))
                    {
                        patternGroups = append(patternGroups, { "edges" : boundaryEdges, "faceQuery" : face });
                    }
                }

                if (size(patternGroups) == 0)
                {
                    throw regenError("The selected face has no boundary edges.", ["pathFace"]);
                }
            }
            else if (size(selectedEdges) > 0)
            {
                // Edge subset mode: split by connectivity, one group per connected component
                const allSelectedEdgesQuery = qEntityFilter(definition.pathFace, EntityType.EDGE);
                const edgeComponents = connectedComponents(context, allSelectedEdgesQuery, AdjacencyType.VERTEX);

                for (var componentIndex = 0; componentIndex < size(edgeComponents); componentIndex += 1)
                {
                    patternGroups = append(patternGroups,
                        { "edges" : qUnion(edgeComponents[componentIndex]), "faceQuery" : qNothing() });
                }
            }
            else
            {
                throw regenError("Select a face or one or more connected edges to define the path.", ["pathFace"]);
            }

            if (size(patternGroups) > 1)
            {
                reportFeatureInfo(context, id,
                    size(patternGroups) ~ " separate patterns will be created, one per face or connected edge group.");
            }
        }
        else
        {
            // CURVE mode: single group, no associated face
            patternGroups = [{ "edges" : definition.pathEdges, "faceQuery" : qNothing() }];
        }

        // ----------------------------------------------------------------
        // Phase 1: Evaluate paths, compute tangent lines, resolve face normals.
        // All offset wire bodies are collected and deleted before connector creation
        // so they do not appear in qCreatedBy results used by the query variable.
        // ----------------------------------------------------------------
        var evaluatedGroups = [];
        var offsetWireBodies = [];

        for (var groupIndex = 0; groupIndex < size(patternGroups); groupIndex += 1)
        {
            const group = patternGroups[groupIndex];
            var activePathEdges is Query = group.edges;
            const groupFaceQuery is Query = group.faceQuery;

            // Build a path from the source edges to validate connectivity early and to use as
            // the base path when no offset is applied.
            const sourcePath = try(constructPath(context, activePathEdges));
            if (sourcePath == undefined)
            {
                if (definition.pathSelectionMode == PathSelectionMode.FACE)
                {
                    if (size(patternGroups) > 1)
                    {
                        throw regenError("Unable to build a continuous path from group " ~ (groupIndex + 1) ~
                            ". Ensure all edges in the group are connected and form a single chain.", ["pathFace"]);
                    }
                    else
                    {
                        throw regenError("Unable to build a continuous path from the selected face or edges. Ensure all selected edges are connected and form a single chain.", ["pathFace"]);
                    }
                }
                else
                {
                    throw regenError("Unable to build a continuous path from the selected edges. Ensure all edges are connected.", ["pathEdges"]);
                }
            }

            // finalPath starts as the source path and is replaced with the offset wire path
            // when an offset is applied.
            var finalPath = sourcePath;

            // Apply path offset when requested in FACE mode.
            // Calls the standard library offsetCurveOnFace feature function directly.
            // When a face was explicitly selected, groupFaceQuery is passed as targets so
            // the kernel knows which surface to project onto. For edge-only selections
            // groupFaceQuery is qNothing() and the kernel infers the surface from the edges.
            if (definition.pathSelectionMode == PathSelectionMode.FACE &&
                definition.useFaceNormalOffset && definition.faceNormalOffset > 0 * meter)
            {
                const offsetWireId = id + ("offsetWire" ~ groupIndex);
                const offsetResult = buildFacePathOffsetWire(
                    context,
                    offsetWireId,
                    activePathEdges,
                    groupFaceQuery,
                    definition.faceNormalOffset,
                    definition.faceNormalOffsetFlip
                );
                offsetWireBodies = append(offsetWireBodies, offsetResult.offsetWireBody);
                activePathEdges = offsetResult.offsetWireEdges;

                // Build path from the offset wire edges for spacing and tangent evaluation
                finalPath = try(constructPath(context, activePathEdges));
                if (finalPath == undefined)
                {
                    if (size(patternGroups) > 1)
                    {
                        throw regenError("Unable to build a continuous path from the offset wire in group " ~ (groupIndex + 1) ~
                            ". The offset distance may be too large for this geometry.", ["faceNormalOffset"]);
                    }
                    else
                    {
                        throw regenError("Unable to build a continuous path from the offset wire. The offset distance may be too large for this geometry.", ["faceNormalOffset"]);
                    }
                }
            }

            const totalPathLength = evPathLength(context, finalPath);

            // computeCurvePatternSpacing expects definition.edges for its length query,
            // so bridge activePathEdges into that field before calling it.
            // Each group uses its own scoped sub-id to avoid operation ID conflicts.
            var spacingDefinition = definition;
            spacingDefinition.edges = activePathEdges;
            spacingDefinition = computeCurvePatternSpacing(context, id + ("g" ~ groupIndex), spacingDefinition);

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
                finalPath.closed
            );

            if (size(normalizedParameters) == 0)
            {
                throw regenError("No mate connectors can be placed with the specified spacing parameters. Adjust spacing, instance count, or offsets.");
            }

            // Evaluate tangent lines at each computed position along the path
            const tangentEvaluation = evPathTangentLines(context, finalPath, normalizedParameters);

            // Store the face query so Phase 2 can evaluate the local face normal at each
            // connector position individually. This handles curved surfaces (cylinders, cones,
            // splines) correctly because the normal is queried per-point rather than once
            // for the whole group.
            evaluatedGroups = append(evaluatedGroups, {
                        "tangentLines" : tangentEvaluation.tangentLines,
                        "faceQuery"    : groupFaceQuery
                    });
        }

        // Delete all temporary offset wire bodies before creating mate connectors
        // so they do not pollute the qCreatedBy result used for query variable registration.
        for (var wireIndex = 0; wireIndex < size(offsetWireBodies); wireIndex += 1)
        {
            if (!isQueryEmpty(context, offsetWireBodies[wireIndex]))
            {
                opDeleteBodies(context, id + ("deleteOffsetWire" ~ wireIndex),
                    { "entities" : offsetWireBodies[wireIndex] });
            }
        }

        // ----------------------------------------------------------------
        // Phase 2: Create mate connectors from all evaluated groups.
        // A flat counter across groups ensures every opMateConnector call
        // receives a unique sub-id regardless of which group it belongs to.
        // globalInstanceSlot is a 1-based index over all candidate positions
        // (including skipped ones) so the user-facing instance numbers are
        // stable even when some are omitted.
        // ----------------------------------------------------------------

        // Pre-build a map keyed by string(index) for O(1) skip lookups.
        // Built once here so the inner loop does a single map access per slot.
        var skipSet = {};
        if (definition.skipInstances)
        {
            for (var skippedInstance in definition.skippedInstances)
            {
                skipSet[skippedInstance.index ~ ""] = true;
            }
        }

        var totalConnectorCount = 0;
        var globalInstanceSlot = 0;

        for (var groupIndex = 0; groupIndex < size(evaluatedGroups); groupIndex += 1)
        {
            const evaluatedGroup = evaluatedGroups[groupIndex];
            const tangentLines = evaluatedGroup.tangentLines;
            const groupFaceQuery = evaluatedGroup.faceQuery;

            for (var connectorIndex = 0; connectorIndex < size(tangentLines); connectorIndex += 1)
            {
                globalInstanceSlot += 1;

                // Skip this position when its slot index is listed in skippedInstances
                if (definition.skipInstances && skipSet[globalInstanceSlot ~ ""] == true)
                {
                    continue;
                }

                const tangentLine = tangentLines[connectorIndex];

                // The connector origin is taken directly from the tangent evaluation.
                // When a path offset was requested, this is already a point on the offset wire
                // (properly mitered/trimmed by @opOffsetCurveOnFace), so no further translation is needed.
                const connectorOrigin = tangentLine.origin;

                var connectorCoordinateSystem;

                if (definition.alignmentMode == MateConnectorAlignmentMode.PATH_TANGENT)
                {
                    // Z-axis follows the curve tangent; X-axis is an arbitrary perpendicular.
                    connectorCoordinateSystem = coordSystem(
                        connectorOrigin,
                        perpendicularVector(tangentLine.direction),
                        tangentLine.direction
                    );
                }
                else if (definition.alignmentMode == MateConnectorAlignmentMode.FACE_NORMAL)
                {
                    // Evaluate the local face normal at this specific connector position.
                    // evDistance projects the origin onto the face to get a UV parameter, then
                    // evFaceTangentPlane reads the surface normal at that UV. This works correctly
                    // for planar, cylindrical, conical, and all other surface types.
                    // evaluateFaceNormalAtPoint handles an empty groupFaceQuery by falling back
                    // to qClosestTo across all solid faces, so no guard is needed — the fallback
                    // correctly resolves face normals for edge-only selections where no explicit
                    // face was provided by the user.
                    // Falls back to PATH_TANGENT when face normal evaluation fails entirely.
                    const localFaceNormalPlane = evaluateFaceNormalAtPoint(context, groupFaceQuery, connectorOrigin);

                    if (localFaceNormalPlane != undefined)
                    {
                        // Z-axis = local face normal, X-axis = surface parameterization X direction.
                        // Each connector independently aligns to its own position on the surface.
                        connectorCoordinateSystem = coordSystem(
                            connectorOrigin,
                            localFaceNormalPlane.x,
                            localFaceNormalPlane.normal
                        );
                    }
                    else
                    {
                        // Fallback: no face available or face normal evaluation failed.
                        connectorCoordinateSystem = coordSystem(
                            connectorOrigin,
                            perpendicularVector(tangentLine.direction),
                            tangentLine.direction
                        );
                    }
                }
                else
                {
                    // GLOBAL_REFERENCE: all connectors share the orientation of the reference entity.
                    connectorCoordinateSystem = coordSystem(
                        connectorOrigin,
                        globalReferenceXAxis,
                        globalReferenceZAxis
                    );
                }

                opMateConnector(context, id + "mateConnector" + totalConnectorCount, {
                            "coordSystem" : connectorCoordinateSystem,
                            "owner" : ownerBodyQuery
                        });
                totalConnectorCount += 1;
            }
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
 * Evaluates the tangent plane of the face closest to the given 3D position.
 *
 * When faceQuery is non-empty (e.g., the user explicitly selected a face), the search
 * is restricted to that face set. When faceQuery is empty (edge-only selection with no
 * associated face), the search falls back to all solid-body faces in the context using
 * qClosestTo — a spatial proximity metric that does not require topological connectivity.
 * This correctly handles offset wire positions which are new bodies with no topological
 * link to the original part faces.
 *
 * Parameters:
 *   context   {Context} - The active context
 *   faceQuery {Query}   - Candidate faces to search (or qNothing() to search all solid faces)
 *   point     {Vector}  - The 3D world-space position to find the closest face to
 *
 * Returns:
 *   {Plane} - Tangent plane at the closest face point: normal = local face normal,
 *             x = surface parameterization X direction at that point.
 *             Returns undefined when no face is found or evaluation fails.
 */
function evaluateFaceNormalAtPoint(context is Context, faceQuery is Query, point is Vector) returns Plane
{
    // Pick the candidate face set: the explicitly provided face query when available,
    // otherwise all non-construction faces owned by solid bodies.
    var candidateFaces is Query = faceQuery;
    if (isQueryEmpty(context, candidateFaces))
    {
        candidateFaces = qOwnedByBody(qBodyType(qEverything(EntityType.BODY), BodyType.SOLID), EntityType.FACE);
    }

    // qClosestTo finds the spatially nearest face to the point without requiring
    // any topological connection between the point and the face.
    const closestFace = qClosestTo(candidateFaces, point);
    if (isQueryEmpty(context, closestFace))
    {
        return undefined;
    }

    // Project the point onto the closest face to obtain its UV parameter, then read
    // the local tangent plane at that UV. This works for planar, cylindrical, conical,
    // and all other analytic or spline surface types.
    const distResult = try(evDistance(context, {
                "side0" : closestFace,
                "side1" : point
            }));
    if (distResult == undefined)
    {
        return undefined;
    }

    return try(evFaceTangentPlane(context, {
                "face"      : closestFace,
                "parameter" : distResult.sides[0].parameter
            }));
}

/**
 * Calls the standard `offsetCurveOnFace` feature function to produce an offset wire body
 * from the given edges. This is the same function called by the built-in "Offset Curve"
 * feature, so the result is identical to what that feature produces for the same inputs.
 *
 * Parameters:
 *   context {Context}               - The active context
 *   wireOperationId {Id}            - A unique sub-ID for the offset wire operation
 *   sourceEdges {Query}             - The face boundary edges to offset from
 *   targetFace {Query}              - The face to use as the offset surface. Pass the
 *                                    explicitly selected face when available, or qNothing()
 *                                    to let the kernel infer from the edge topology.
 *   offsetDistance {ValueWithUnits} - The offset distance (must be positive)
 *   flipDirection {boolean}         - Passed as oppositeDirection to the standard feature
 *
 * Returns:
 *   {map} - A map with fields:
 *       offsetWireBody  {Query} - The created wire body (delete after sampling)
 *       offsetWireEdges {Query} - Edges of the first wire body, ready for constructPath
 */
function buildFacePathOffsetWire(context is Context, wireOperationId is Id, sourceEdges is Query, targetFace is Query, offsetDistance is ValueWithUnits, flipDirection is boolean) returns map
{
    // Call the standard library offsetCurveOnFace feature function directly.
    // The kernel operation @opOffsetCurveOnFace requires a non-empty targets set to determine
    // which surface to project the offset onto — this mirrors how the built-in Offset Curve
    // feature always requires an explicit face selection.
    // When an explicit face was provided by the user (FACE mode, face selected), pass it directly.
    // When only edges were selected (no associated face), derive the projection surface from the
    // topology of the source edges via qAdjacent so the kernel has the surface context it needs.
    const resolvedTargets = isQueryEmpty(context, targetFace) ?
        qAdjacent(sourceEdges, AdjacencyType.EDGE, EntityType.FACE) :
        targetFace;
    offsetCurveOnFace(context, wireOperationId, {
                "edges"              : sourceEdges,
                "distance"           : offsetDistance,
                "oppositeDirection"  : flipDirection,
                "offsetType"         : OffsetCurveType.GEODESIC,
                "targets"            : resolvedTargets
            });

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
