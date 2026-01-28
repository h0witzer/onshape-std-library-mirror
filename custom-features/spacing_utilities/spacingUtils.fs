FeatureScript 2856;
// Consolidated pattern spacing utilities module
// This module provides reusable spacing logic for both curve and circular patterns

import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/evaluate.fs", version : "2856.0");
import(path : "onshape/std/curveGeometry.fs", version : "2856.0");
import(path : "onshape/std/box.fs", version : "2856.0");
import(path : "onshape/std/feature.fs", version : "2856.0");
import(path : "onshape/std/valueBounds.fs", version : "2856.0");
import(path : "onshape/std/math.fs", version : "2856.0");
import(path : "onshape/std/patternUtils.fs", version : "2856.0");

// ============================================================================
// CURVE PATTERN SPACING UTILITIES
// ============================================================================

/**
 * Specifies the type of spacing between curve pattern instances.
 * @value EQUAL : Equal-spaced instances along the length of curve
 * @value DISTANCE : Instances spaced by custom distance
 * @value BESTFIT : Best fit spacing with automatic instance count calculation
 */
export enum CurvePatternSpacingType
{
    annotation { "Name" : "Equal spacing" }
    EQUAL,
    annotation { "Name" : "Distance" }
    DISTANCE,
    annotation { "Name" : "Best fit" }
    BESTFIT,
}

/**
 * Predicate for curve pattern spacing configuration.
 * Defines the UI fields and validation for spacing type, distance, instance count, and best-fit parameters.
 * 
 * @param definition {map} : The feature definition map to populate
 *      @field spacingType {CurvePatternSpacingType} : The type of spacing to use
 *      @field distance {ValueWithUnits} : Distance between instances (required if spacingType is DISTANCE)
 *      @field instanceCount {number} : Number of instances (required if spacingType is DISTANCE or EQUAL)
 *      @field actualPitchEqual {ValueWithUnits} : Read-only actual pitch for EQUAL spacing
 *      @field targetPitch {ValueWithUnits} : Target pitch for BESTFIT spacing
 *      @field actualPitch {ValueWithUnits} : Read-only actual pitch for BESTFIT spacing
 *      @field actualCount {number} : Read-only computed instance count for BESTFIT spacing
 *      @field doPitchCeiling {boolean} : Whether to round up (ceiling) the instance count for BESTFIT
 */
export predicate curvePatternSpacingPredicate(definition is map)
{
    annotation { "Name" : "Spacing type" }
    definition.spacingType is CurvePatternSpacingType;

    if (definition.spacingType == CurvePatternSpacingType.DISTANCE || definition.spacingType == CurvePatternSpacingType.EQUAL)
    {
        annotation { "Name" : "Instance count" }
        isInteger(definition.instanceCount, CURVE_PATTERN_BOUNDS);
    }

    if (definition.spacingType == CurvePatternSpacingType.DISTANCE)
    {
        annotation { "Name" : "Distance" }
        isLength(definition.distance, PATTERN_OFFSET_BOUND);
    }

    if (definition.spacingType == CurvePatternSpacingType.EQUAL)
    {
        annotation { "Name" : "Actual pitch", "UIHint" : UIHint.READ_ONLY }
        isAnything(definition.actualPitchEqual);
    }

    if (definition.spacingType == CurvePatternSpacingType.BESTFIT)
    {
        annotation { "Name" : "Target pitch" }
        isLength(definition.targetPitch, PATTERN_OFFSET_BOUND);

        annotation { "Name" : "Actual pitch", "UIHint" : UIHint.READ_ONLY }
        isAnything(definition.actualPitch);

        annotation { "Name" : "Instance count", "UIHint" : UIHint.READ_ONLY }
        isAnything(definition.actualCount);

        annotation { "Name" : "Pitch ceiling", "Default" : false }
        definition.doPitchCeiling is boolean;
    }

    // Offset controls for EQUAL and BESTFIT modes
    if (definition.spacingType == CurvePatternSpacingType.EQUAL || definition.spacingType == CurvePatternSpacingType.BESTFIT)
    {
        annotation { "Name" : "Use offsets", "Default" : false }
        definition.useOffsets is boolean;

        if (definition.useOffsets)
        {
            annotation { "Name" : "Offset 1" }
            isLength(definition.offset1, PATTERN_OFFSET_BOUND);

            annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION, "Default" : false }
            definition.oppositeDirection is boolean;

            if (definition.oppositeDirection)
            {
                annotation { "Name" : "Offset 2" }
                isLength(definition.offset2, PATTERN_OFFSET_BOUND);
            }
        }
    }
}

/**
 * Calculates and applies curve pattern spacing parameters based on the spacing type.
 * Updates the definition with computed values for instance count and actual pitch.
 * 
 * @param context {Context} : The context for this operation
 * @param id {Id} : The feature ID for setting computed parameters
 * @param definition {map} : The feature definition containing spacing configuration
 *      @field edges {Query} : The edges to pattern along
 *      @field spacingType {CurvePatternSpacingType} : The spacing type
 *      @field instanceCount {number} : Input/output instance count
 *      @field targetPitch {ValueWithUnits} : Target pitch for best-fit spacing
 *      @field doPitchCeiling {boolean} : Whether to use ceiling for rounding
 *      @field useOffsets {boolean} : Whether to use start/end offsets
 *      @field offset1 {ValueWithUnits} : First offset from curve end
 *      @field offset2 {ValueWithUnits} : Second offset from curve end (if oppositeDirection)
 *      @field oppositeDirection {boolean} : Whether to use different offsets at each end
 * 
 * @returns {map} : Updated definition with computed spacing parameters
 */
export function computeCurvePatternSpacing(context is Context, id is Id, definition is map) returns map
{
    const curveLength = evLength(context, {
                "entities" : definition.edges
            });

    // Calculate effective length accounting for offsets
    var effectiveLength = curveLength;
    if (definition.useOffsets)
    {
        const offset1 = definition.offset1;
        const offset2 = definition.oppositeDirection ? definition.offset2 : definition.offset1;
        effectiveLength = curveLength - offset1 - offset2;
    }

    var corrector = 0;
    try silent
    {
        const evaluatedCurve = evCurveDefinition(context, {
                    "edge" : definition.edges
                });

        corrector = (evaluatedCurve.curveType == CurveType.SPLINE && evaluatedCurve.isPeriodic) ? 0 : 1;
    }

    if (definition.spacingType == CurvePatternSpacingType.EQUAL)
    {
        const actualPitch = effectiveLength / (definition.instanceCount - corrector);

        setFeatureComputedParameter(context, id, {
                    "name" : "actualPitchEqual",
                    "value" : actualPitch
                });
    }

    if (definition.spacingType == CurvePatternSpacingType.BESTFIT)
    {
        const computedInstanceNumber = effectiveLength / definition.targetPitch + corrector;

        var integerComputedInstanceNumber;

        if (definition.doPitchCeiling)
        {
            integerComputedInstanceNumber = ceil(computedInstanceNumber);
        }
        else
        {
            integerComputedInstanceNumber = round(computedInstanceNumber);
        }

        definition.instanceCount = integerComputedInstanceNumber;

        const actualPitch = effectiveLength / (integerComputedInstanceNumber - corrector);

        setFeatureComputedParameter(context, id, {
                    "name" : "actualPitch",
                    "value" : actualPitch
                });

        setFeatureComputedParameter(context, id, {
                    "name" : "actualCount",
                    "value" : definition.instanceCount
                });
    }

    return definition;
}

// ============================================================================
// CIRCULAR PATTERN SPACING UTILITIES
// ============================================================================

/**
 * Specifies the type of spacing between circular pattern instances.
 * @value EQUAL : Equal-spaced instances around the axis
 * @value BESTFIT : Best fit spacing with automatic instance count calculation
 */
export enum CircularPatternSpacingType
{
    annotation { "Name" : "Equal spacing" }
    EQUAL,
    annotation { "Name" : "Best fit" }
    BESTFIT,
}

/**
 * Predicate for circular pattern spacing configuration.
 * Defines the UI fields and validation for spacing type, instance count, and best-fit parameters.
 * 
 * @param definition {map} : The feature definition map to populate
 *      @field spacingType {CircularPatternSpacingType} : The type of spacing to use
 *      @field instanceCount {number} : Number of instances (required if spacingType is EQUAL)
 *      @field oppositeDirection {boolean} : Switch direction of pattern around axis (for EQUAL)
 *      @field equalSpace {boolean} : Whether entire pattern lies within angle (for EQUAL)
 *      @field isCentered {boolean} : Whether to center pattern on seed (for EQUAL)
 *      @field targetPitch {ValueWithUnits} : Target pitch for BESTFIT spacing
 *      @field useReferencePoint {boolean} : Whether to use a custom reference point for radius
 *      @field referencePoint {Query} : Custom reference point for radius calculation
 *      @field actualPitch {ValueWithUnits} : Read-only actual pitch for BESTFIT spacing
 *      @field actualCount {number} : Read-only computed instance count for BESTFIT spacing
 *      @field doPitchCeiling {boolean} : Whether to round up (ceiling) the instance count for BESTFIT
 */
export predicate circularPatternSpacingPredicate(definition is map)
{
    annotation { "Name" : "Spacing type" }
    definition.spacingType is CircularPatternSpacingType;

    if (definition.spacingType == CircularPatternSpacingType.EQUAL)
    {
        annotation { "Name" : "Instance count" }
        isInteger(definition.instanceCount, CIRCULAR_PATTERN_BOUNDS);

        annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION_CIRCULAR }
        definition.oppositeDirection is boolean;

        annotation { "Name" : "Equal spacing", "Default" : true }
        definition.equalSpace is boolean;

        annotation { "Name" : "Centered" }
        definition.isCentered is boolean;
    }

    if (definition.spacingType == CircularPatternSpacingType.BESTFIT)
    {
        annotation { "Name" : "Target pitch" }
        isLength(definition.targetPitch, PATTERN_OFFSET_BOUND);

        annotation { "Name" : "Use reference point" }
        definition.useReferencePoint is boolean;

        if (definition.useReferencePoint)
        {
            annotation { "Name" : "Reference point", "Filter" : QueryFilterCompound.ALLOWS_VERTEX, "MaxNumberOfPicks" : 1 }
            definition.referencePoint is Query;
        }

        annotation { "Name" : "Actual pitch", "UIHint" : UIHint.READ_ONLY }
        isAnything(definition.actualPitch);

        annotation { "Name" : "Instance count", "UIHint" : UIHint.READ_ONLY }
        isAnything(definition.actualCount);

        annotation { "Name" : "Pitch ceiling", "Default" : false }
        definition.doPitchCeiling is boolean;
    }
}

/**
 * Calculates and applies circular pattern spacing parameters based on the spacing type.
 * Updates the definition with computed values for instance count and actual pitch.
 * 
 * @param context {Context} : The context for this operation
 * @param id {Id} : The feature ID for setting computed parameters
 * @param axisLine {Line} : The axis of rotation for the pattern
 * @param definition {map} : The feature definition containing spacing configuration
 *      @field patternType {PatternType} : The type of pattern (PART, FEATURE, or FACE)
 *      @field entities {Query} : The entities to pattern (for PART patterns)
 *      @field instanceFunction {FeatureList} : The features to pattern (for FEATURE patterns)
 *      @field angle {ValueWithUnits} : The angle for the pattern
 *      @field spacingType {CircularPatternSpacingType} : The spacing type
 *      @field instanceCount {number} : Input/output instance count
 *      @field targetPitch {ValueWithUnits} : Target pitch for best-fit spacing
 *      @field useReferencePoint {boolean} : Whether to use custom reference point
 *      @field referencePoint {Query} : Custom reference point vertex
 *      @field doPitchCeiling {boolean} : Whether to use ceiling for rounding
 * 
 * @returns {map} : Updated definition with computed spacing parameters
 */
export function computeCircularPatternSpacing(context is Context, id is Id, definition is map, axisLine is Line) returns map
{
    if (definition.spacingType == CircularPatternSpacingType.BESTFIT)
    {
        const entityBox = evBox3d(context, {
                    "topology" : isFeaturePattern(definition.patternType) ? qCreatedBy(definition.instanceFunction) : definition.entities,
                    "tight" : true
                });

        const referencePoint = !definition.useReferencePoint ? box3dCenter(entityBox) : evVertexPoint(context, {
                        "vertex" : definition.referencePoint
                    });

        const radius = evDistance(context, {
                        "side0" : referencePoint,
                        "side1" : axisLine
                    }).distance;

        const circumference = 2 * PI * radius * definition.angle / (360 * degree);

        const corrector = (definition.angle < 360 * degree) ? 1 : 0;
        const computedInstanceNumber = (circumference / definition.targetPitch) + corrector;

        var integerComputedInstanceNumber;
        if (definition.doPitchCeiling)
        {
            integerComputedInstanceNumber = ceil(computedInstanceNumber);
        }
        else
        {
            integerComputedInstanceNumber = round(computedInstanceNumber);
        }
        definition.instanceCount = integerComputedInstanceNumber;

        const actualPitch = circumference / (integerComputedInstanceNumber - corrector);

        setFeatureComputedParameter(context, id, {
                    "name" : "actualPitch",
                    "value" : actualPitch
                });

        setFeatureComputedParameter(context, id, {
                    "name" : "actualCount",
                    "value" : definition.instanceCount
                });
    }

    return definition;
}

// ============================================================================
// DOMAIN CALCULATION UTILITIES
// ============================================================================

/**
 * Calculates normalized domains for equal spacing distribution.
 * This function distributes instances evenly along a path with equal gaps between them.
 * Returns normalized parameters (0 to 1) for each instance location.
 * 
 * @param totalLength {ValueWithUnits} : The total length of the path
 * @param instanceWidth {ValueWithUnits} : The width of each instance
 * @param instanceCount {number} : The number of instances to distribute
 * @param startOffset {ValueWithUnits} : Optional offset from the start of the path (default: 0)
 * @param endOffset {ValueWithUnits} : Optional offset from the end of the path (default: 0)
 * 
 * @returns {array} : Array of {start, end} maps with normalized parameters (0 to 1) for each instance location
 */
export function calculateEqualSpacedDomains(totalLength is ValueWithUnits, instanceWidth is ValueWithUnits, instanceCount is number, startOffset is ValueWithUnits, endOffset is ValueWithUnits) returns array
{
    // Calculate normalized offsets
    const startOffsetParam = startOffset / totalLength;
    const endOffsetParam = endOffset / totalLength;
    const effectiveLength = 1 - startOffsetParam - endOffsetParam;
    
    const widthParam = instanceWidth / totalLength;
    const spacing = (effectiveLength - (instanceCount * widthParam)) / (instanceCount + 1);

    var domains = [];
    for (var i = 0; i < instanceCount; i += 1)
    {
        const start = startOffsetParam + spacing * (i + 1) + (widthParam * i);
        const end = start + widthParam;
        domains = append(domains, { "start" : start, "end" : end });
    }
    return domains;
}

/**
 * Calculates normalized domains for distance-based (pitch) spacing distribution.
 * This function places instances at fixed pitch intervals along a path.
 * Distance represents the pitch (center-to-center distance) between consecutive instances.
 * The first instance is positioned with its leading edge at the start of the path (or offset).
 * Returns normalized parameters (0 to 1) for each instance location.
 * 
 * @param totalLength {ValueWithUnits} : The total length of the path
 * @param instanceWidth {ValueWithUnits} : The width of each instance
 * @param distance {ValueWithUnits} : The pitch (center-to-center distance) between consecutive instances
 * @param instanceCount {number} : The number of instances that fit with the given pitch
 * @param startOffset {ValueWithUnits} : Optional offset from the start of the path (default: 0)
 * @param endOffset {ValueWithUnits} : Optional offset from the end of the path (default: 0)
 * 
 * @returns {array} : Array of {start, end} maps with normalized parameters (0 to 1) for each instance location
 */
export function calculateDistanceSpacedDomains(totalLength is ValueWithUnits, instanceWidth is ValueWithUnits, distance is ValueWithUnits, instanceCount is number, startOffset is ValueWithUnits, endOffset is ValueWithUnits) returns array
{
    // Calculate normalized offsets
    const startOffsetParam = startOffset / totalLength;
    const endOffsetParam = endOffset / totalLength;
    
    const widthParam = instanceWidth / totalLength;
    const pitchParam = distance / totalLength;
    const halfWidthParam = widthParam / 2;

    var domains = [];

    for (var i = 0; i < instanceCount; i += 1)
    {
        // Position instances by their centers at pitch intervals, starting from the offset
        // First instance center is at startOffset + instanceWidth/2 (instance starts at offset edge)
        const centerPos = startOffsetParam + halfWidthParam + (pitchParam * i);
        const start = centerPos - halfWidthParam;
        const end = centerPos + halfWidthParam;
        
        if (end > (1 - endOffsetParam))
        {
            break; // Don't exceed the path boundaries (accounting for end offset)
        }
        domains = append(domains, { "start" : start, "end" : end });
    }
    return domains;
}

/**
 * Validates that domains do not overlap.
 * Checks each consecutive pair of domains to ensure the end of one instance
 * does not exceed the start of the next instance (with a small tolerance).
 * 
 * @param domains {array} : Array of {start, end} maps with normalized parameters (0 to 1)
 * @param tolerance {number} : Optional tolerance for floating-point comparison (default: 1e-9)
 * 
 * @returns {boolean} : True if domains are valid (no overlaps), false if any domains overlap
 */
export function validateDomainsNoOverlap(domains is array, tolerance is number) returns boolean
{
    // No validation needed for 0 or 1 instances
    if (size(domains) <= 1)
    {
        return true;
    }
    
    // Check each consecutive pair of domains
    for (var i = 0; i < size(domains) - 1; i += 1)
    {
        const currentEnd = domains[i].end;
        const nextStart = domains[i + 1].start;
        
        // Domains overlap if the current end exceeds the next start by more than tolerance
        // The tolerance accounts for floating-point precision when instances are exactly touching
        if (currentEnd > nextStart + tolerance)
        {
            return false;
        }
    }
    
    return true;
}

// ============================================================================
// SHARED UTILITIES
// ============================================================================

/**
 * Helper function to check if a pattern type is a feature pattern.
 * Internal use only - standard library provides public version via patternUtils.fs
 * 
 * @param patternType {PatternType} : The pattern type to check
 * @returns {boolean} : True if the pattern type is FEATURE
 */
function isFeaturePattern(patternType) returns boolean
{
    return patternType == PatternType.FEATURE;
}
