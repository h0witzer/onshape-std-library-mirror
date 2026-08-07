FeatureScript 3029;
import(path : "onshape/std/common.fs", version : "3029.0");

/*
    Tippy Bucket Pivot
    ------------------
    Places a mate connector at the pivot location that makes a tipping ("tippy") bucket tip, using
    material-aware centers of mass for the empty and filled states, and drops construction points at
    both centers of mass so the placement can be measured.

    Design spec, including the derivation of the placement rule: docs/specs/TIPPY_BUCKET_PIVOT_SPEC.md.

    Mechanism: below the pivot the center of mass is a stable pendulum and the bucket holds its rest
    attitude; above the pivot the equilibrium is unstable and the bucket goes over. The pivot height
    therefore lies between the empty center of mass and the filled center of mass, and where it sits
    between them is the trip threshold. The default puts it where the empty bucket's hold-down torque
    equals the filled bucket's tip-over torque.

    Every density comes from an assigned material, the fill's included. Assign a material to the water
    body as you would to a structural part.

    Densities are read in editing logic because getProperty cannot be called on the current context
    during regeneration (properties.fs: "features are regenerated before any user-set properties are
    applied"). Each body's density is cached in a hidden field of the driven-query array item bound to
    that body. Density is shape-independent, so the cache stays valid through upstream geometry edits;
    volume and centroid are measured live every regeneration. A material reassigned while this dialog is
    closed is the one change the cache cannot see, which is what "Re-read materials" is for.

    Measurements come from ev* calls. The mass-averaging that combines them has no standard library
    equivalent, so that arithmetic is written out here.
*/

// Standard gravity. Cancels out of the balanced threshold, which is a ratio of torques, and is used to
// report torques in force units.
const GRAVITY_ACCELERATION = 9.80665 * meter / second ^ 2;

// Minimum rise in center of mass height between the empty and filled states for the bucket to be
// treated as able to tip. Also the minimum lateral center of mass shift for deriving the pivot axis.
const MINIMUM_CENTER_OF_MASS_RISE = 0.01 * millimeter;

// Sine of about one degree. A pivot axis closer to vertical than this cannot tip anything under gravity.
const MINIMUM_HORIZONTAL_AXIS_COMPONENT = 0.01745;

// Disagreement between the approximate and high accuracy mass totals that triggers a warning.
const MASS_ACCURACY_WARNING_FRACTION = 0.01;

// Clash types that mean two bodies share volume. The ABUT_* types are excluded: water modelled at its
// fill level rests against the bucket walls, and touching costs no double-counted mass.
const VOLUME_SHARING_CLASH_TYPES = [
        ClashType.INTERFERE,
        ClashType.TARGET_IN_TOOL,
        ClashType.TOOL_IN_TARGET
    ];

// Cached densities, in kilograms per cubic meter. The upper bound clears the densest engineering
// materials; osmium is about 22590.
const CACHED_DENSITY_BOUNDS =
{
    (unitless) : [0, 0, 1e6]
} as RealBoundSpec;

// Cached mass overrides in kilograms. Zero means no override is set on the part.
const CACHED_MASS_BOUNDS =
{
    (unitless) : [0, 0, 1e9]
} as RealBoundSpec;

// Where the pivot sits between the empty center of mass (0) and the filled center of mass (1). The
// interval is open: at 0 the empty bucket is neutrally stable, at 1 the full bucket cannot tip.
const PIVOT_HEIGHT_FRACTION_BOUNDS =
{
    (unitless) : [0.05, 0.5, 0.95]
} as RealBoundSpec;

/**
 * How the pivot height between the empty and filled centers of mass is chosen.
 * @value BALANCED : Where the empty bucket's hold-down torque equals the filled bucket's tip-over
 *      torque, which maximizes whichever of the two is weaker. Closed form.
 * @value CUSTOM_FRACTION : Set by hand. Lower trips on less water with a weaker reset, higher resets
 *      harder but needs more water.
 */
export enum PivotThresholdMode
{
    annotation { "Name" : "Balanced" }
    BALANCED,
    annotation { "Name" : "Custom fraction" }
    CUSTOM_FRACTION
}

annotation { "Feature Type Name" : "Tippy Bucket Pivot",
        "Editing Logic Function" : "tippyBucketPivotEditingLogic",
        "Feature Type Description" :
            "Places a mate connector at the pivot location that makes a tipping bucket tip.<br><br>" ~
            "Select every solid making up the bucket, plus the solid representing the water at the " ~
            "fill level where it should tip. <b>Every one of them needs a material assigned</b>, the " ~
            "water included, since all densities are read from materials. The mate connector's Z axis " ~
            "lies along the pivot axis, so an assembly revolute mate on it works directly.<br><br>" ~
            "Geometry changes are picked up automatically. <b>Press 'Re-read materials' after " ~
            "reassigning a material</b>, which this feature cannot see on its own." }
export const tippyBucketPivot = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // One array item per selected bucket solid. This presents as a single selection box: picking N
        // solids creates N collapsed rows, each labelled with the material read for it, so a part
        // missing a material is visible before regeneration. Each item's solidBody is a declared Query
        // parameter, which persists, so the density cached alongside it stays bound to that body.
        annotation { "Name" : "Bucket solids", "Item name" : "Part", "Driven query" : "solidBody",
                    "Item label template" : "#solidBody [#materialName]",
                    "Description" : "Every solid making up the bucket. Each needs a material assigned.",
                    "UIHint" : [UIHint.COLLAPSE_ARRAY_ITEMS, UIHint.PREVENT_ARRAY_REORDER] }
        definition.bucketParts is array;
        for (var bucketPart in definition.bucketParts)
        {
            annotation { "Name" : "Solid", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 1 }
            bucketPart.solidBody is Query;

            annotation { "Name" : "Material name", "Default" : "no material", "UIHint" : UIHint.ALWAYS_HIDDEN }
            bucketPart.materialName is string;

            annotation { "Name" : "Cached density", "UIHint" : UIHint.ALWAYS_HIDDEN }
            isReal(bucketPart.densityKilogramsPerCubicMeter, CACHED_DENSITY_BOUNDS);

            annotation { "Name" : "Cached mass override", "UIHint" : UIHint.ALWAYS_HIDDEN }
            isReal(bucketPart.massOverrideKilograms, CACHED_MASS_BOUNDS);

            annotation { "Name" : "Material was read", "UIHint" : UIHint.ALWAYS_HIDDEN }
            bucketPart.materialWasRead is boolean;
        }

        annotation { "Name" : "Fill solid", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 1,
                    "Description" : "The water at the fill level where the bucket should tip. Needs a " ~
                        "material assigned, same as the bucket solids; its density is read from that material." }
        definition.fillSolid is Query;

        // Shown read-only so the density in use is visible without opening anything. READ_ONLY
        // parameters cannot be edited in the dialog and are written by editing logic.
        annotation { "Name" : "Fill material", "Default" : "no material", "UIHint" : UIHint.READ_ONLY }
        definition.fillMaterialName is string;

        annotation { "Name" : "Cached fill density", "UIHint" : UIHint.ALWAYS_HIDDEN }
        isReal(definition.cachedFillDensity, CACHED_DENSITY_BOUNDS);

        annotation { "Name" : "Fill material was read", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.fillMaterialWasRead is boolean;

        // A momentary button: the parameter value stays undefined and holds no state, and the press
        // reaches editing logic as its clickedButton argument. Editing logic runs only on dialog
        // activity, so pressing this is what forces a fresh read of every material.
        annotation { "Name" : "Re-read materials",
                    "Description" : "Reads every assigned material again. Use it after changing a " ~
                        "material anywhere in this Part Studio; this feature caches densities and " ~
                        "cannot see that change on its own." }
        isButton(definition.refreshMaterials);

        annotation { "Group Name" : "Orientation", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Up direction",
                        "Filter" : QueryFilterCompound.ALLOWS_DIRECTION || BodyType.MATE_CONNECTOR,
                        "MaxNumberOfPicks" : 1,
                        "Description" : "Which way is up, opposite gravity. Defaults to global +Z, " ~
                            "the Top plane normal." }
            definition.upReference is Query;

            annotation { "Name" : "Flip up", "UIHint" : UIHint.OPPOSITE_DIRECTION }
            definition.flipUp is boolean;

            annotation { "Name" : "Pivot axis",
                        "Filter" : QueryFilterCompound.ALLOWS_DIRECTION || BodyType.MATE_CONNECTOR,
                        "MaxNumberOfPicks" : 1,
                        "Description" : "The axis the bucket rocks about. Left empty it is derived as " ~
                            "the horizontal normal of the center of mass shift, which is the plane the " ~
                            "bucket rocks in." }
            definition.pivotAxisReference is Query;

            annotation { "Name" : "Locate along axis on",
                        "Filter" : EntityType.VERTEX || (EntityType.FACE && GeometryType.PLANE) || BodyType.MATE_CONNECTOR,
                        "MaxNumberOfPicks" : 1,
                        "Description" : "Slides the mate connector along the pivot axis, for putting it " ~
                            "on a trunnion bearing face. Has no effect on the physics." }
            definition.axialLocationReference is Query;
        }

        annotation { "Group Name" : "Threshold", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Pivot height", "UIHint" : UIHint.SHOW_LABEL,
                        "Description" : "Balanced puts the pivot where the empty bucket's righting " ~
                            "torque equals the full bucket's tipping torque, which maximizes whichever " ~
                            "of the two is weaker." }
            definition.thresholdMode is PivotThresholdMode;

            if (definition.thresholdMode == PivotThresholdMode.CUSTOM_FRACTION)
            {
                annotation { "Name" : "Fraction from empty to filled CoM",
                            "Description" : "0 puts the pivot at the empty center of mass, 1 at the " ~
                                "filled one. Lower trips on less water but resets more weakly; higher " ~
                                "resets harder but needs more water to trip." }
                isReal(definition.pivotHeightFraction, PIVOT_HEIGHT_FRACTION_BOUNDS);
            }
        }

        annotation { "Group Name" : "Output", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Mate connector owner", "Filter" : EntityType.BODY && BodyType.SOLID,
                        "MaxNumberOfPicks" : 1,
                        "Description" : "The part the mate connector belongs to, so it travels into the " ~
                            "assembly with it. Defaults to the heaviest bucket solid." }
            definition.mateConnectorOwner is Query;

            annotation { "Name" : "Create center of mass points", "Default" : true,
                        "Description" : "Construction points at the empty CoM, the filled CoM and the " ~
                            "water centroid. Selectable and dimensionable, so the placement can be " ~
                            "measured." }
            definition.createCenterOfMassPoints is boolean;

            annotation { "Name" : "Show debug overlay",
                        "Description" : "Transient overlay of the centers of mass, the shift between " ~
                            "them and the pivot axis. Visible while this feature is selected." }
            definition.showDebugOverlay is boolean;

            annotation { "Name" : "Verify mass accuracy", "Default" : true,
                        "Description" : "Re-measures every part at high volume accuracy and warns if " ~
                            "the result disagrees with the tessellated mass properties by more than 1%, " ~
                            "since the pivot height inherits that error. Costs a high accuracy volume " ~
                            "measurement per part; turn it off if regeneration drags." }
            definition.verifyMassAccuracy is boolean;
        }
    }
    {
        // ---- 1. Validate the selections before spending anything on geometry -------------------------
        if (size(definition.bucketParts) == 0)
        {
            throw regenError("Select the solids that make up the bucket. Every one of them needs a " ~
                        "material assigned before this feature can place a pivot.", ["bucketParts"]);
        }

        var bucketBodyQueries = makeArray(size(definition.bucketParts));
        for (var partIndex = 0; partIndex < size(definition.bucketParts); partIndex += 1)
        {
            bucketBodyQueries[partIndex] = definition.bucketParts[partIndex].solidBody;
        }
        const bucketBodies = qUnion(bucketBodyQueries);

        const fillBody = qBodyType(qEntityFilter(definition.fillSolid, EntityType.BODY), BodyType.SOLID);
        if (isQueryEmpty(context, fillBody))
        {
            throw regenError("Select the solid representing the water in the bucket at the fill level " ~
                        "where it should tip.", ["fillSolid"]);
        }

        // A body selected as both structure and water would have its mass counted twice.
        const doubleCountedBodies = qIntersection([bucketBodies, fillBody]);
        if (!isQueryEmpty(context, doubleCountedBodies))
        {
            throw regenError("A solid is selected as both bucket structure and fill. Its mass would be " ~
                        "counted twice. Remove it from one of the two selections.",
                    ["fillSolid"], doubleCountedBodies);
        }

        // ---- 2. Material gate ----------------------------------------------------------------------
        // Throwing from the feature body lets regenError highlight the offending bodies in the
        // graphics area.
        var bodiesMissingMaterial = [];
        for (var bucketPart in definition.bucketParts)
        {
            if (!bucketPart.materialWasRead || bucketPart.densityKilogramsPerCubicMeter <= 0)
            {
                bodiesMissingMaterial = append(bodiesMissingMaterial, bucketPart.solidBody);
            }
        }
        if (size(bodiesMissingMaterial) > 0)
        {
            throw regenError("Assign a material to every bucket solid: " ~ size(bodiesMissingMaterial) ~
                        " of " ~ size(definition.bucketParts) ~ " have no material, or a material with " ~
                        "zero density. Pivot placement is mass critical and will not proceed on a guess.",
                    ["bucketParts"], qUnion(bodiesMissingMaterial));
        }

        if (!definition.fillMaterialWasRead || definition.cachedFillDensity <= 0)
        {
            throw regenError("Assign a material to the fill solid. Its density is read from that " ~
                        "material, the same as every bucket solid. Use a Water material, or whatever " ~
                        "the bucket actually catches.", ["fillSolid"], fillBody);
        }

        // ---- 3. Empty state mass properties --------------------------------------------------------
        // One call per body, because each body carries its own density.
        var bucketMassTotal = 0 * kilogram;
        var bucketMassMoment = kilogram * meter * vector(0, 0, 0);
        var heaviestPartMass = 0 * kilogram;
        var heaviestPartBody = definition.bucketParts[0].solidBody;
        var densityDrivenApproximateMass = 0 * kilogram;
        var densityDrivenPreciseMass = 0 * kilogram;
        var partsUsingMassOverride = 0;

        for (var bucketPart in definition.bucketParts)
        {
            const partDensity = bucketPart.densityKilogramsPerCubicMeter * kilogram / meter ^ 3;
            const partMassProperties = evApproximateMassProperties(context, {
                        "entities" : bucketPart.solidBody,
                        "density" : partDensity
                    });

            // An explicit mass override takes precedence over density times volume, which matters on
            // purchased and imported parts. The centroid comes from geometry either way, and the readout
            // reports how many parts this applied to.
            var partMass = partMassProperties.mass;
            if (bucketPart.massOverrideKilograms > 0)
            {
                partMass = bucketPart.massOverrideKilograms * kilogram;
                partsUsingMassOverride += 1;
            }
            else if (definition.verifyMassAccuracy)
            {
                // Accumulate both a tessellated and a high accuracy mass for the same bodies, so the
                // approximation residual can be reported. Parts using an override are excluded, their
                // mass not coming from volume.
                densityDrivenApproximateMass += partMassProperties.mass;
                densityDrivenPreciseMass += partDensity * evVolume(context, {
                            "entities" : bucketPart.solidBody,
                            "accuracy" : VolumeAccuracy.HIGH
                        });
            }

            bucketMassTotal += partMass;
            bucketMassMoment += partMass * partMassProperties.centroid;

            if (partMass > heaviestPartMass)
            {
                heaviestPartMass = partMass;
                heaviestPartBody = bucketPart.solidBody;
            }
        }

        if (bucketMassTotal <= 0 * kilogram)
        {
            throw regenError("The selected bucket solids have no mass. Check the assigned materials.",
                    ["bucketParts"], bucketBodies);
        }
        const emptyCenterOfMass = bucketMassMoment / bucketMassTotal;

        // ---- 4. Fill state -------------------------------------------------------------------------
        const fillDensity = definition.cachedFillDensity * kilogram / meter ^ 3;
        const fillMassProperties = evApproximateMassProperties(context, {
                    "entities" : fillBody,
                    "density" : fillDensity
                });
        const fillMass = fillMassProperties.mass;
        const fillCentroid = fillMassProperties.centroid;

        if (fillMass <= 0 * kilogram)
        {
            throw regenError("The fill solid has no mass. Check the density of its assigned material.",
                    ["fillSolid"], fillBody);
        }

        reportFillOverlap(context, id, fillBody, bucketBodies);

        const filledMass = bucketMassTotal + fillMass;
        const filledCenterOfMass = (bucketMassTotal * emptyCenterOfMass + fillMass * fillCentroid) / filledMass;

        // ---- 5. Resolve the frame ------------------------------------------------------------------
        // Global up in Onshape is +Z: the Top plane is the XY plane (defaultFeatures.fs:42), so its
        // normal is Z.
        var upDirection = vector(0, 0, 1);
        if (!isQueryEmpty(context, definition.upReference))
        {
            const extractedUpDirection = extractDirection(context, definition.upReference);
            if (extractedUpDirection == undefined)
            {
                throw regenError("Could not read a direction from the up reference. Pick a planar face, " ~
                            "a linear edge, a cylindrical face, or a mate connector.", ["upReference"]);
            }
            upDirection = normalize(extractedUpDirection);
        }
        if (definition.flipUp)
        {
            upDirection = -1 * upDirection;
        }

        const emptyHeight = dot(emptyCenterOfMass, upDirection);
        const filledHeight = dot(filledCenterOfMass, upDirection);
        const fillCentroidHeight = dot(fillCentroid, upDirection);
        const centerOfMassRise = filledHeight - emptyHeight;

        // Filling raises the center of mass only when the water sits above the empty center of mass.
        // Otherwise no pivot height can trip, and the fix is upstream of this feature.
        if (centerOfMassRise < MINIMUM_CENTER_OF_MASS_RISE)
        {
            throw regenError("This bucket cannot tip: filling it does not raise the center of mass. " ~
                        "The fill centroid sits " ~ formatMillimeters(emptyHeight - fillCentroidHeight) ~
                        " mm below the empty center of mass, so adding water pulls the center of mass " ~
                        "down instead of up and there is no pivot height that will trip. Fix it by " ~
                        "moving mass lower in the bucket (ballast in the base, lighter upper walls) or " ~
                        "by raising the water (deeper chamber, higher fill line). Empty CoM height " ~
                        formatMillimeters(emptyHeight) ~ " mm, fill centroid height " ~
                        formatMillimeters(fillCentroidHeight) ~ " mm, measured along the up direction.",
                    ["fillSolid"], qUnion([bucketBodies, fillBody]));
        }

        // The bucket rocks in the plane containing the center of mass shift, so the horizontal normal of
        // that shift is the pivot axis when none is given.
        const centerOfMassShift = filledCenterOfMass - emptyCenterOfMass;
        const horizontalShift = centerOfMassShift - centerOfMassRise * upDirection;

        var rawPivotAxisDirection = vector(0, 0, 0);
        if (!isQueryEmpty(context, definition.pivotAxisReference))
        {
            const extractedAxisDirection = extractDirection(context, definition.pivotAxisReference);
            if (extractedAxisDirection == undefined)
            {
                throw regenError("Could not read a direction from the pivot axis reference. Pick a " ~
                            "linear edge, a cylindrical face, a planar face, or a mate connector.",
                        ["pivotAxisReference"]);
            }
            rawPivotAxisDirection = extractedAxisDirection;
        }
        else
        {
            if (norm(horizontalShift) < MINIMUM_CENTER_OF_MASS_RISE)
            {
                throw regenError("The fill is laterally centered, so the tipping plane cannot be " ~
                            "derived from the center of mass shift. Select the pivot axis explicitly " ~
                            "under Orientation.", ["pivotAxisReference"]);
            }
            rawPivotAxisDirection = cross(upDirection, normalize(horizontalShift));
        }

        // Project the axis horizontal, so a near-horizontal pick is accepted as intended, and reject
        // only a genuinely vertical one.
        const verticalAxisComponent = dot(rawPivotAxisDirection, upDirection);
        const horizontalAxisComponent = rawPivotAxisDirection - verticalAxisComponent * upDirection;
        if (norm(horizontalAxisComponent) < MINIMUM_HORIZONTAL_AXIS_COMPONENT)
        {
            throw regenError("The pivot axis is within one degree of vertical. A vertical pivot axis " ~
                        "cannot produce tipping under gravity.", ["pivotAxisReference"]);
        }
        const pivotAxisDirection = normalize(horizontalAxisComponent);
        const tippingDirection = normalize(cross(pivotAxisDirection, upDirection));

        // ---- 6. Pivot placement --------------------------------------------------------------------
        var pivotHeightFraction = 0.5;
        if (definition.thresholdMode == PivotThresholdMode.BALANCED)
        {
            // Empty hold-down authority is proportional to bucketMassTotal * f, filled tip-over
            // authority to filledMass * (1 - f). Equalizing them maximizes the weaker of the two.
            pivotHeightFraction = filledMass / (bucketMassTotal + filledMass);
        }
        else
        {
            pivotHeightFraction = definition.pivotHeightFraction;
        }

        const pivotHeight = emptyHeight + pivotHeightFraction * centerOfMassRise;

        // Laterally the pivot sits on the vertical line through the empty center of mass. For a
        // symmetric two chamber tippy that is the symmetry plane, so the empty bucket hangs level and
        // the water's lateral offset picks the direction it goes over.
        const pivotLateralCoordinate = dot(emptyCenterOfMass, tippingDirection);

        // Position along the axis does not enter the physics, so it follows the empty center of mass
        // and can be slid out to a bearing face instead.
        var pivotAxialCoordinate = dot(emptyCenterOfMass, pivotAxisDirection);
        if (!isQueryEmpty(context, definition.axialLocationReference))
        {
            // One call covers a vertex, a planar face and a mate connector point body alike.
            const axialReferencePoint = evApproximateCentroid(context, {
                        "entities" : definition.axialLocationReference
                    });
            pivotAxialCoordinate = dot(axialReferencePoint, pivotAxisDirection);
        }

        const pivotPoint = pivotHeight * upDirection
            + pivotLateralCoordinate * tippingDirection
            + pivotAxialCoordinate * pivotAxisDirection;

        // ---- 7. Output geometry --------------------------------------------------------------------
        // Z lies along the pivot axis so an assembly revolute mate, which rotates about the mate
        // connector's Z, works on this connector directly. X along the tipping direction shows the
        // direction it goes over in the triad.
        var mateConnectorOwner = heaviestPartBody;
        if (!isQueryEmpty(context, definition.mateConnectorOwner))
        {
            mateConnectorOwner = definition.mateConnectorOwner;
        }
        opMateConnector(context, id + "pivotMateConnector", {
                    "coordSystem" : coordSystem(pivotPoint, tippingDirection, pivotAxisDirection),
                    "owner" : mateConnectorOwner
                });

        if (definition.createCenterOfMassPoints)
        {
            createNamedPoint(context, id + "emptyCenterOfMassPoint", emptyCenterOfMass, "CoM empty");
            createNamedPoint(context, id + "filledCenterOfMassPoint", filledCenterOfMass, "CoM filled");
            createNamedPoint(context, id + "fillCentroidPoint", fillCentroid, "CoM water");
        }

        if (definition.showDebugOverlay)
        {
            debug(context, emptyCenterOfMass, DebugColor.GREEN);
            debug(context, filledCenterOfMass, DebugColor.RED);
            debug(context, emptyCenterOfMass, filledCenterOfMass, DebugColor.MAGENTA);
            debug(context, pivotPoint, DebugColor.BLUE);
            debug(context, line(pivotPoint, pivotAxisDirection), DebugColor.BLUE);
        }

        // ---- 8. Readout ----------------------------------------------------------------------------
        if (definition.verifyMassAccuracy && densityDrivenPreciseMass > 0 * kilogram)
        {
            const massResidualFraction =
                abs(densityDrivenApproximateMass - densityDrivenPreciseMass) / densityDrivenPreciseMass;
            if (massResidualFraction > MASS_ACCURACY_WARNING_FRACTION)
            {
                reportFeatureWarning(context, id, "Mass properties are approximate to within " ~
                            roundToPrecision(massResidualFraction * 100, 2) ~ "% on this geometry " ~
                            "(tessellated mass " ~ formatKilograms(densityDrivenApproximateMass) ~
                            " kg against high accuracy volume mass " ~ formatKilograms(densityDrivenPreciseMass) ~
                            " kg). The pivot height carries the same uncertainty. Simplify or heal the " ~
                            "geometry if that matters at this scale.");
            }
        }

        const emptyHoldDownAuthority = bucketMassTotal * GRAVITY_ACCELERATION * pivotHeightFraction * centerOfMassRise;
        const filledTipOverAuthority = filledMass * GRAVITY_ACCELERATION * (1 - pivotHeightFraction) * centerOfMassRise;

        var readout = "Empty mass " ~ formatKilograms(bucketMassTotal) ~ " kg, fill mass " ~
            formatKilograms(fillMass) ~ " kg (" ~ definition.fillMaterialName ~ "), filled mass " ~
            formatKilograms(filledMass) ~ " kg." ~
            "\nCoM height along up: empty " ~ formatMillimeters(emptyHeight) ~ " mm, filled " ~
            formatMillimeters(filledHeight) ~ " mm, rise " ~ formatMillimeters(centerOfMassRise) ~ " mm." ~
            "\nPivot at " ~ roundToPrecision(pivotHeightFraction * 100, 1) ~ "% of that rise, height " ~
            formatMillimeters(pivotHeight) ~ " mm." ~
            "\nTorque coefficient (mass * g * lever, the torque at 90 deg of tilt): empty hold-down " ~
            formatNewtonMillimeters(emptyHoldDownAuthority) ~ " N-mm, filled tip-over " ~
            formatNewtonMillimeters(filledTipOverAuthority) ~ " N-mm." ~
            "\nTips toward " ~ formatDirection(tippingDirection) ~ ", about axis " ~ formatDirection(pivotAxisDirection) ~ ".";

        if (partsUsingMassOverride > 0)
        {
            readout ~= "\n" ~ partsUsingMassOverride ~ " part(s) used an explicit mass override instead " ~
                "of density times volume.";
        }

        reportFeatureInfo(context, id, readout);
    },
    {
            bucketParts : [],
            fillSolid : qNothing(),
            fillMaterialName : "no material",
            cachedFillDensity : 0,
            fillMaterialWasRead : false,
            upReference : qNothing(),
            flipUp : false,
            pivotAxisReference : qNothing(),
            axialLocationReference : qNothing(),
            thresholdMode : PivotThresholdMode.BALANCED,
            pivotHeightFraction : 0.5,
            mateConnectorOwner : qNothing(),
            createCenterOfMassPoints : true,
            showDebugOverlay : false,
            verifyMassAccuracy : true
        });
// refreshMaterials is absent from the defaults above: isButton requires the value to stay undefined.

/**
 * Editing logic. Reads the material of every selected body and caches its density into the array item
 * bound to that body, flagging anything it could not read so the feature body can throw with the
 * offending bodies highlighted. This is the only place materials can be read, since getProperty cannot
 * be called on the current context during regeneration.
 *
 * Every item is read on every invocation. getProperty is a metadata lookup with no geometry cost, so
 * any dialog activity refreshes the whole cache.
 *
 * @param clickedButton {string} : name of the button parameter pressed, if any.
 * @param definition : the incoming feature definition.
 * @returns {map} : the definition with cached density fields filled in.
 */
export function tippyBucketPivotEditingLogic(context is Context, id is Id, oldDefinition is map,
    definition is map, isCreating is boolean, specifiedParameters is map, clickedButton is string) returns map
{
    for (var partIndex = 0; partIndex < size(definition.bucketParts); partIndex += 1)
    {
        const cachedPart = readMassDataForBody(context, definition.bucketParts[partIndex].solidBody);
        definition.bucketParts[partIndex].materialName = cachedPart.materialName;
        definition.bucketParts[partIndex].densityKilogramsPerCubicMeter = cachedPart.densityKilogramsPerCubicMeter;
        definition.bucketParts[partIndex].massOverrideKilograms = cachedPart.massOverrideKilograms;
        definition.bucketParts[partIndex].materialWasRead = cachedPart.materialWasRead;
    }

    const cachedFill = readMassDataForBody(context, definition.fillSolid);
    definition.fillMaterialName = cachedFill.materialName;
    definition.cachedFillDensity = cachedFill.densityKilogramsPerCubicMeter;
    definition.fillMaterialWasRead = cachedFill.materialWasRead;

    // An explicit press logs what was read, since the reason to press it is to confirm that a material
    // change made outside this dialog has landed.
    if (clickedButton == "refreshMaterials")
    {
        println("Tippy Bucket Pivot re-read materials:");
        for (var bucketPart in definition.bucketParts)
        {
            println("  bucket solid: " ~ bucketPart.materialName ~ " at " ~
                bucketPart.densityKilogramsPerCubicMeter ~ " kg/m^3");
        }
        println("  fill solid: " ~ definition.fillMaterialName ~ " at " ~
            definition.cachedFillDensity ~ " kg/m^3");
    }

    return definition;
}

/**
 * Read the material density and mass override of a single body. Editing logic only.
 *
 * A body with no material returns materialWasRead false, and the error surfaces from the feature body
 * where the offending geometry can be highlighted. The try blocks report to the console.
 *
 * @param bodyQuery {Query} : a query resolving to a single solid body.
 * @returns {map} : {
 *      @field materialName {string} : the assigned material's name, or "no material".
 *      @field densityKilogramsPerCubicMeter {number} : 0 when no material is assigned.
 *      @field massOverrideKilograms {number} : 0 when no mass override is set.
 *      @field materialWasRead {boolean} : true only when a material with positive density was read.
 * }
 */
function readMassDataForBody(context is Context, bodyQuery is Query) returns map
{
    var massData = {
            "materialName" : "no material",
            "densityKilogramsPerCubicMeter" : 0,
            "massOverrideKilograms" : 0,
            "materialWasRead" : false
        };

    if (isQueryEmpty(context, bodyQuery))
    {
        return massData;
    }

    try
    {
        const assignedMaterial = getProperty(context, {
                    "entity" : bodyQuery,
                    "propertyType" : PropertyType.MATERIAL
                });
        // getProperty returns undefined for a property that is not set.
        if (assignedMaterial != undefined && assignedMaterial.density > 0 * kilogram / meter ^ 3)
        {
            massData.materialName = assignedMaterial.name;
            massData.densityKilogramsPerCubicMeter = assignedMaterial.density / (kilogram / meter ^ 3);
            massData.materialWasRead = true;
        }
    }
    catch (materialError)
    {
        // materialWasRead stays false, and the feature body reports which bodies these are.
    }

    try
    {
        const massOverride = getProperty(context, {
                    "entity" : bodyQuery,
                    "propertyType" : PropertyType.MASS_OVERRIDE
                });
        if (massOverride != undefined && massOverride is ValueWithUnits && massOverride > 0 * kilogram)
        {
            massData.massOverrideKilograms = massOverride / kilogram;
        }
    }
    catch (massOverrideError)
    {
        // massOverrideKilograms stays 0, meaning density times volume is used.
    }

    return massData;
}

/**
 * Create a construction point and name it so it reads as what it is in the parts list.
 *
 * setProperty is callable during regeneration. The naming is wrapped because it is cosmetic and the
 * point is placed either way.
 *
 * @param pointLocation {Vector} : where to put the point.
 * @param pointName {string} : name to apply to the resulting point body.
 */
function createNamedPoint(context is Context, id is Id, pointLocation is Vector, pointName is string)
{
    opPoint(context, id, { "point" : pointLocation });

    try
    {
        setProperty(context, {
                    "entities" : qCreatedBy(id, EntityType.BODY),
                    "propertyType" : PropertyType.NAME,
                    "value" : pointName
                });
    }
    catch (namingError)
    {
        // The point keeps its default name.
    }
}

/**
 * Warn when the fill solid and the bucket solids share volume, which would count that volume once as
 * structure and again as water and skew both centers of mass.
 *
 * Bodies that only touch are passed over: water modelled at its fill level rests against the bucket
 * walls, so abutting is how a correct fill solid looks. Only the clash types in
 * VOLUME_SHARING_CLASH_TYPES indicate shared volume.
 *
 * @param fillBody {Query} : the fill solid.
 * @param bucketBodies {Query} : the bucket structure solids.
 */
function reportFillOverlap(context is Context, id is Id, fillBody is Query, bucketBodies is Query)
{
    try
    {
        const clashes = evCollision(context, {
                    "tools" : fillBody,
                    "targets" : bucketBodies
                });

        var overlapCount = 0;
        for (var clash in clashes)
        {
            // `type` is a reserved word, so this field needs bracket access.
            if (isIn(clash['type'], VOLUME_SHARING_CLASH_TYPES))
            {
                overlapCount += 1;
            }
        }

        if (overlapCount > 0)
        {
            reportFeatureWarning(context, id, "The fill solid shares volume with the bucket solids in " ~
                        overlapCount ~ " place(s). That volume is counted twice, once as structure and " ~
                        "once as water, which skews both centers of mass. Model the water as the cavity " ~
                        "it fills, up against the walls rather than inside them.");
        }
    }
    catch (collisionError)
    {
        reportFeatureWarning(context, id, "Could not check whether the fill solid shares volume with " ~
                    "the bucket solids. Verify by hand that the water does not overlap the walls.");
    }
}

/** Format a length as millimeters to two decimals, for report strings. */
function formatMillimeters(value is ValueWithUnits) returns number
{
    return roundToPrecision(value / millimeter, 2);
}

/** Format a mass as kilograms to four decimals, for report strings. */
function formatKilograms(value is ValueWithUnits) returns number
{
    return roundToPrecision(value / kilogram, 4);
}

/** Format a torque as newton millimeters to two decimals, for report strings. */
function formatNewtonMillimeters(value is ValueWithUnits) returns number
{
    return roundToPrecision(value / newtonMillimeter, 2);
}

/** Format a unit vector as a readable triple, for report strings. */
function formatDirection(direction is Vector) returns string
{
    return "(" ~ roundToPrecision(direction[0], 3) ~ ", " ~ roundToPrecision(direction[1], 3) ~
        ", " ~ roundToPrecision(direction[2], 3) ~ ")";
}
