FeatureScript 2909;
// Mate Connector Pattern on Curve
// Creates a pattern of mate connectors along a curve using flexible spacing options
// from the centralized spacingUtils module. Supports path-tangent and global-reference
// orientation modes, and can wrap all created connectors as a named query variable.

import(path : "onshape/std/common.fs", version : "2909.0");
import(path : "onshape/std/queryVariable.fs", version : "2909.0");

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
        // Curve path selection - accepts one or more connected edges forming a continuous path
        annotation { "Name" : "Path edges",
                    "Filter" : EntityType.EDGE,
                    "UIHint" : UIHint.SHOW_CREATE_SELECTION }
        definition.pathEdges is Query;

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
        // Build a continuous path from the selected edges
        const path = try(constructPath(context, definition.pathEdges));
        if (path == undefined)
        {
            throw regenError("Unable to build a continuous path from the selected edges. Ensure all edges are connected.", ["pathEdges"]);
        }

        const totalPathLength = evPathLength(context, path);

        // computeCurvePatternSpacing expects definition.edges for its length query,
        // so bridge pathEdges into that field before calling it
        var spacingDefinition = definition;
        spacingDefinition.edges = definition.pathEdges;
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
            definition
        );

        if (size(normalizedParameters) == 0)
        {
            throw regenError("No mate connectors can be placed with the specified spacing parameters. Adjust spacing, instance count, or offsets.");
        }

        // Evaluate tangent lines at each computed position along the path
        const tangentEvaluation = evPathTangentLines(context, path, normalizedParameters);
        const tangentLines = tangentEvaluation.tangentLines;

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

            if (definition.alignmentMode == MateConnectorAlignmentMode.PATH_TANGENT)
            {
                // Z-axis follows curve tangent; X-axis is an arbitrary perpendicular to that tangent
                connectorCoordinateSystem = coordSystem(
                    tangentLine.origin,
                    perpendicularVector(tangentLine.direction),
                    tangentLine.direction
                );
            }
            else
            {
                // Global reference mode: orientation from reference entity, origin at path position
                connectorCoordinateSystem = coordSystem(
                    tangentLine.origin,
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
 *
 * Returns:
 *   {array} - Array of normalized parameters in [0, 1] range, one per connector position
 */
function computeMateConnectorParameters(totalPathLength is ValueWithUnits, effectivePathLength is ValueWithUnits, startOffset is ValueWithUnits, endOffset is ValueWithUnits, instanceCount is number, definition is map) returns array
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
    }

    return normalizedParameters;
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
