
// This custom feature is owned by Evan Reese and distributed by The Onsherpa. It may not be redistributed without permission of the owner. Copyright © 2025 Evan Reese.

// TODO I'm still working on getting directions 2 and 3 to match previous features. What's different between them and direction 1, which seems to work?

FeatureScript 2837;
import(path : "onshape/std/geometry.fs", version : "2837.0");
export import(path : "onshape/std/patternUtils.fs", version : "2837.0");
import(path : "onshape/std/manipulator.fs", version : "2837.0");
import(path : "onshape/std/recordpatterntype.gen.fs", version : "2837.0");

icon::import(path : "20b2a3d05c3013e2f73b24d4", version : "3d19453df2c52d2777d8108f");
import(path : "c3fe41e654ffc2f052a38c8f/312092e1b28afbd4f1e894dd/3c37750af0cf716cb0ede1e0", version : "632ec68437f325e31ffbe8f9");


export enum DistanceType
{
    Distance,
    Measured
}

export enum SpacingType
{
    annotation { "Name" : "Spacing" }
    SPACING,
    annotation { "Name" : "Total length" }
    LENGTH,
}

export enum InstanceCountType
{
    annotation { "Name" : "Count" }
    COUNT,
    annotation { "Name" : "Target spacing" }
    TARGET_SPACING,
}

export enum RoundingType
{
    annotation { "Name" : "Exact spacing" }
    EXACT,
    annotation { "Name" : "Nearest value spacing" }
    ROUND,
    annotation { "Name" : "Maximum spacing" }
    UP,
    annotation { "Name" : "Minimum spacing" }
    DOWN,
}


annotation { "Feature Type Name" : "Linear pattern plus",
        "Icon" : icon::BLOB_DATA,
        "Filter Selector" : "allparts",
        "Editing Logic Function" : "linearPatternPlusEditingLogic",
        "Manipulator Change Function" : "linearPatternPlusManipulatorFunction",
    }
export const linearPatternPlus = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        patternTypePredicate(definition);

        annotation { "Name" : "Match previous feature settings", "Description" : "Pick a previous Linear Pattern Plus feature to match the directions and spacings. This may be useful if repeating multiple types of patterns in the same space. For example, a part pattern followed by a feature pattern." }
        definition.matchPrevious is boolean;

        if (!definition.matchPrevious)
        {
            annotation { "Group Name" : "First direction", "Collapsed By Default" : false }
            {

                annotation { "Name" : "Direction",
                            "Filter" : QueryFilterCompound.ALLOWS_DIRECTION,
                            "MaxNumberOfPicks" : 1 }
                definition.directionOne is Query;

                annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                definition.oppositeDirectionOne is boolean;

                annotation { "Name" : "Spacing type", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.SHOW_LABEL] }
                definition.spacingTypeOne is SpacingType;

                annotation { "Name" : "Distance type", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.SHOW_LABEL] }
                definition.distanceTypeOne is DistanceType;

                if (definition.distanceTypeOne == DistanceType.Distance)
                {
                    annotation { "Name" : "Distance" }
                    isLength(definition.distanceOne, PATTERN_OFFSET_BOUND);
                }

                if (definition.distanceTypeOne == DistanceType.Measured)
                {
                    annotation { "Name" : "Start entity", "Filter" : EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY, "MaxNumberOfPicks" : 1 }
                    definition.startEntityOne is Query;

                    annotation { "Name" : "End entity", "Filter" : EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY, "MaxNumberOfPicks" : 1 }
                    definition.endEntityOne is Query;

                    annotation { "Name" : "Offset distance", "UIHint" : ["DISPLAY_SHORT", "FIRST_IN_ROW"] }
                    definition.hasOffsetOne is boolean;

                    if (definition.hasOffsetOne)
                    {
                        annotation { "Name" : "Offset", "UIHint" : UIHint.DISPLAY_SHORT }
                        isLength(definition.offsetOne, NONNEGATIVE_LENGTH_BOUNDS);

                        annotation { "Name" : "Opposite offset direction", "Default" : false, "UIHint" : UIHint.OPPOSITE_DIRECTION }
                        definition.oppositeOffsetDirectionOne is boolean;
                    }
                }

                if (definition.spacingTypeOne != SpacingType.SPACING)
                {
                    annotation { "Name" : "Instance count type", " UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                    definition.instanceCountTypeOne is InstanceCountType;

                    if (definition.instanceCountTypeOne != InstanceCountType.COUNT)
                    {
                        annotation { "Name" : "Target spacing" }
                        isLength(definition.targetSpacingOne, LENGTH_BOUNDS);

                        annotation { "Name" : "Rounding type" }
                        definition.roundingTypeOne is RoundingType;
                    }
                }
                if (definition.instanceCountTypeOne == InstanceCountType.COUNT || definition.spacingTypeOne == SpacingType.SPACING)
                {
                    annotation { "Name" : "Instance count" }
                    isInteger(definition.countOne, PRIMARY_PATTERN_BOUNDS);
                }
                annotation { "Name" : "Centered" }
                definition.isCenteredOne is boolean;
            }

            annotation { "Name" : "Second direction", "Column Name" : "Has second direction" }
            definition.hasSecondDir is boolean;

            if (definition.hasSecondDir)
            {

                annotation { "Group Name" : "Second direction", "Collapsed By Default" : false, "Driving Parameter" : "hasSecondDir" }
                {

                    annotation { "Name" : "Direction",
                                "Filter" : QueryFilterCompound.ALLOWS_DIRECTION,
                                "MaxNumberOfPicks" : 1 }
                    definition.directionTwo is Query;

                    annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                    definition.oppositeDirectionTwo is boolean;

                    annotation { "Name" : "Spacing type", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.SHOW_LABEL] }
                    definition.spacingTypeTwo is SpacingType;

                    annotation { "Name" : "Distance type", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.SHOW_LABEL] }
                    definition.distanceTypeTwo is DistanceType;

                    if (definition.distanceTypeTwo == DistanceType.Distance)
                    {
                        annotation { "Name" : "Distance" }
                        isLength(definition.distanceTwo, PATTERN_OFFSET_BOUND);
                    }

                    if (definition.distanceTypeTwo == DistanceType.Measured)
                    {
                        annotation { "Name" : "Start entity", "Filter" : EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY, "MaxNumberOfPicks" : 1 }
                        definition.startEntityTwo is Query;

                        annotation { "Name" : "End entity", "Filter" : EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY, "MaxNumberOfPicks" : 1 }
                        definition.endEntityTwo is Query;

                        annotation { "Name" : "Offset distance", "UIHint" : ["DISPLAY_SHORT", "FIRST_IN_ROW"] }
                        definition.hasOffsetTwo is boolean;

                        if (definition.hasOffsetTwo)
                        {
                            annotation { "Name" : "Offset", "UIHint" : UIHint.DISPLAY_SHORT }
                            isLength(definition.offsetTwo, NONNEGATIVE_LENGTH_BOUNDS);

                            annotation { "Name" : "Opposite offset direction", "Default" : false, "UIHint" : UIHint.OPPOSITE_DIRECTION }
                            definition.oppositeOffsetDirectionTwo is boolean;
                        }
                    }

                    if (definition.spacingTypeTwo != SpacingType.SPACING)
                    {
                        annotation { "Name" : "Instance count type", " UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                        definition.instanceCountTypeTwo is InstanceCountType;

                        if (definition.instanceCountTypeTwo != InstanceCountType.COUNT)
                        {
                            annotation { "Name" : "Target spacing" }
                            isLength(definition.targetSpacingTwo, LENGTH_BOUNDS);

                            annotation { "Name" : "Rounding type" }
                            definition.roundingTypeTwo is RoundingType;
                        }
                    }
                    if (definition.instanceCountTypeTwo == InstanceCountType.COUNT || definition.spacingTypeTwo == SpacingType.SPACING)
                    {
                        annotation { "Name" : "Instance count" }
                        isInteger(definition.countTwo, PRIMARY_PATTERN_BOUNDS);
                    }
                    annotation { "Name" : "Centered" }
                    definition.isCenteredTwo is boolean;
                }
                annotation { "Name" : "Third direction", "Column Name" : "Has third direction" }
                definition.hasThirdDir is boolean;
            }

            if (definition.hasThirdDir && definition.hasSecondDir)
            {
                annotation { "Group Name" : "Third direction", "Collapsed By Default" : false, "Driving Parameter" : "hasThirdDir" }
                {
                    annotation { "Name" : "Direction",
                                "Filter" : QueryFilterCompound.ALLOWS_DIRECTION,
                                "MaxNumberOfPicks" : 1 }
                    definition.directionThree is Query;

                    annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                    definition.oppositeDirectionThree is boolean;

                    annotation { "Name" : "Spacing type", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.SHOW_LABEL] }
                    definition.spacingTypeThree is SpacingType;

                    annotation { "Name" : "Distance type", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.SHOW_LABEL] }
                    definition.distanceTypeThree is DistanceType;

                    if (definition.distanceTypeThree == DistanceType.Distance)
                    {
                        annotation { "Name" : "Distance" }
                        isLength(definition.distanceThree, PATTERN_OFFSET_BOUND);
                    }

                    if (definition.distanceTypeThree == DistanceType.Measured)
                    {
                        annotation { "Name" : "Start entity", "Filter" : EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY, "MaxNumberOfPicks" : 1 }
                        definition.startEntityThree is Query;

                        annotation { "Name" : "End entity", "Filter" : EntityType.VERTEX || EntityType.EDGE || EntityType.FACE || EntityType.BODY, "MaxNumberOfPicks" : 1 }
                        definition.endEntityThree is Query;

                        annotation { "Name" : "Offset distance", "UIHint" : ["DISPLAY_SHORT", "FIRST_IN_ROW"] }
                        definition.hasOffsetThree is boolean;

                        if (definition.hasOffsetThree)
                        {
                            annotation { "Name" : "Offset", "UIHint" : UIHint.DISPLAY_SHORT }
                            isLength(definition.offsetThree, NONNEGATIVE_LENGTH_BOUNDS);

                            annotation { "Name" : "Opposite offset direction", "Default" : false, "UIHint" : UIHint.OPPOSITE_DIRECTION }
                            definition.oppositeOffsetDirectionThree is boolean;
                        }
                    }

                    if (definition.spacingTypeThree != SpacingType.SPACING)
                    {
                        annotation { "Name" : "Instance count type", " UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
                        definition.instanceCountTypeThree is InstanceCountType;

                        if (definition.instanceCountTypeThree != InstanceCountType.COUNT)
                        {
                            annotation { "Name" : "Target spacing" }
                            isLength(definition.targetSpacingThree, LENGTH_BOUNDS);

                            annotation { "Name" : "Rounding type" }
                            definition.roundingTypeThree is RoundingType;
                        }
                    }
                    if (definition.instanceCountTypeThree == InstanceCountType.COUNT || definition.spacingTypeThree == SpacingType.SPACING)
                    {
                        annotation { "Name" : "Instance count" }
                        isInteger(definition.countThree, PRIMARY_PATTERN_BOUNDS);
                    }
                    annotation { "Name" : "Centered" }
                    definition.isCenteredThree is boolean;
                }

            }
        }

        if (definition.matchPrevious)
        {
            annotation { "Name" : "Liner pattern plus feature", "MaxNumberOfPicks" : 1 }
            definition.feature is FeatureList;
        }

        if (definition.patternType == PatternType.PART)
        {
            booleanPatternScopePredicate(definition);
        }

        if (definition.patternType == PatternType.FEATURE)
        {
            annotation { "Name" : "Apply per instance" }
            definition.fullFeaturePattern is boolean;
        }

        if (definition.patternType == PatternType.PART && definition.operationType == NewBodyOperationType.NEW)
        {
            annotation { "Name" : "Create composite part" }
            definition.composite is boolean;

            if (definition.composite)
            {
                annotation { "Name" : "Closed" }
                definition.closedComposite is boolean;
            }
        }

        annotation { "Name" : "Skip instances" }
        definition.skipInstances is boolean;

        annotation { "Group Name" : "Skip instances", "Driving Parameter" : "skipInstances", "Collapsed By Default" : false }
        {
            if (definition.skipInstances)
            {
                annotation { "Name" : "Instances to skip", "Item name" : "instance", "Item label template" : "(#index1, #index2, #index3)", "Show labels only" : true, "UIHint" : [UIHint.INITIAL_FOCUS, UIHint.PREVENT_ARRAY_REORDER, UIHint.ALLOW_ARRAY_FOCUS] }
                definition.skippedInstances is array;

                for (var instance in definition.skippedInstances)
                {
                    annotation { "Name" : "First direction" }
                    isInteger(instance.index1, { (unitless) : [-1e5, 0, 1e5] } as IntegerBoundSpec);

                    annotation { "Name" : "Second direction" }
                    isInteger(instance.index2, { (unitless) : [-1e5, 0, 1e5] } as IntegerBoundSpec);

                    annotation { "Name" : "Third direction" }
                    isInteger(instance.index3, { (unitless) : [-1e5, 0, 1e5] } as IntegerBoundSpec);
                }
            }
        }

    }
    {
        // Verify that direction queries don't reference mesh bodies
        if (!definition.matchPrevious)
        {
            verifyNoMesh(context, definition, "directionOne");
            if (definition.hasSecondDir)
            {
                verifyNoMesh(context, definition, "directionTwo");
            }
            if (definition.hasSecondDir && definition.hasThirdDir)
            {
                verifyNoMesh(context, definition, "directionThree");
            }
        }
        
        // a map to store variables to be later retrieved by the Extract Variables feature. These are also used by downstream Linear Pattern Plus features if "match previous feature settings" is selected.
        var embeddedVariables = {};

        // declaring variables to solve scope issues
        var oppositeDirectionOne;
        var countOne;
        var distanceOne;
        var directionOne;
        var directionQueryOne;

        var oppositeDirectionTwo;
        var hasSecondDir = false; // false default, and will change true later if needed
        var countTwo;
        var distanceTwo;
        var directionTwo;
        var directionQueryTwo;

        var oppositeDirectionThree;
        var hasThirdDir = false; // false default, and will change true later if needed
        var countThree;
        var distanceThree;
        var directionThree;
        var directionQueryThree;

        var toCleanUp = [];


        // getting and processing info from a previous feature to match
        if (definition.matchPrevious)
        {
            // get the info from last feature's variables
            var previousFeatureInfo;
            for (var key in definition.feature)
            {
                const featureID = key;
                try
                {
                    previousFeatureInfo = getVariable(context, toString(featureID));
                    println(previousFeatureInfo);
                }
                catch
                {
                    reportFeatureWarning(context, id, "Incompatible feature selected. Only other Linear Pattern Plus features may be used, and others are ignored.");
                }
            }

            countOne = previousFeatureInfo.countOne;
            distanceOne = previousFeatureInfo.spacingOne;
            directionOne = previousFeatureInfo["@directionOne"];
            oppositeDirectionOne = previousFeatureInfo["@oppositeDirectionOne"];

            opPlane(context, id + "directionQueryOneBody", {
                        "plane" : plane(vector(0, 0, 0) * inch, directionOne)
                    });

            directionQueryOne = qCreatedBy(id + "directionQueryOneBody", EntityType.FACE);
            toCleanUp = append(toCleanUp, qCreatedBy(id + "directionQueryOneBody", EntityType.BODY));

            if (previousFeatureInfo.countTwo is number)
            {
                countTwo = previousFeatureInfo.countTwo;
                distanceTwo = previousFeatureInfo.spacingTwo;
                directionTwo = previousFeatureInfo["@directionTwo"];
                hasSecondDir = true;

                oppositeDirectionTwo = previousFeatureInfo["@oppositeDirectionTwo"];

                opPlane(context, id + "directionQueryTwoBody", {
                            "plane" : plane(vector(0, 0, 0) * inch, directionTwo)
                        });

                directionQueryTwo = qCreatedBy(id + "directionQueryTwoBody", EntityType.FACE);
                toCleanUp = append(toCleanUp, qCreatedBy(id + "directionQueryTwoBody", EntityType.BODY));
            }

            if (previousFeatureInfo.countThree is number)
            {
                countThree = previousFeatureInfo.countThree;
                distanceThree = previousFeatureInfo.spacingThree;
                directionThree = previousFeatureInfo["@directionThree"];
                hasThirdDir = true;

                oppositeDirectionThree = previousFeatureInfo["@oppositeDirectionThree"];

                opPlane(context, id + "directionQueryThreeBody", {
                            "plane" : plane(vector(0, 0, 0) * inch, directionThree)
                        });

                directionQueryThree = qCreatedBy(id + "directionQueryThreeBody", EntityType.FACE);
                toCleanUp = append(toCleanUp, qCreatedBy(id + "directionQueryThreeBody", EntityType.BODY));
            }

            // carry the variables forward so this feature can be selected for extraction too.
            embeddedVariables = previousFeatureInfo;
        }

        // Processing standard UI inputs
        else
        {
            hasSecondDir = definition.hasSecondDir;
            hasThirdDir = definition.hasThirdDir;

            // getting the origin for all 3 main manipulators
            var manipOrigin;
            if (definition.patternType == PatternType.PART)
            {
                manipOrigin = evApproximateCentroid(context, { "entities" : evaluateQuery(context, definition.entities)[0] });
            }
            else if (definition.patternType == PatternType.FACE)
            {
                manipOrigin = evApproximateCentroid(context, { "entities" : evaluateQuery(context, definition.faces)[0] });
            }
            else if (definition.patternType == PatternType.FEATURE)
            {
                manipOrigin = evApproximateCentroid(context, { "entities" : qCreatedBy(definition.instanceFunction, EntityType.FACE) });
            }

            const measureUndefined = "Choose a start and end entity to measure for direction "; // text to use in regen errors later

            // Direction One calculations
            directionQueryOne = definition.directionOne;
            oppositeDirectionOne = definition.oppositeDirectionOne;

            var measuredDistanceOne = try(definition.distanceTypeOne == DistanceType.Measured ? evDistance(context, { "side0" : definition.startEntityOne, "side1" : definition.endEntityOne }).distance : 0 * inch);
            if (measuredDistanceOne == undefined)
                throw regenError(measureUndefined ~ "one.");

            var directionInfoOne = getCountAndDistance(
            definition.distanceOne,
            definition.hasOffsetOne,
            definition.oppositeDirectionOne,
            definition.spacingTypeOne,
            definition.distanceTypeOne,
            definition.offsetOne,
            definition.oppositeOffsetDirectionOne,
            definition.instanceCountTypeOne,
            definition.countOne,
            definition.targetSpacingOne,
            measuredDistanceOne,
            definition.roundingTypeOne
            );

            countOne = directionInfoOne.instanceCount;
            distanceOne = directionInfoOne.spacing;
            directionOne = extractDirection(context, definition.directionOne);

            // adding variables to embed for retrieval by the Extract Variables feature.
            embeddedVariables.countOne = countOne;
            embeddedVariables.spacingOne = distanceOne;
            embeddedVariables.lengthOne = distanceOne * (countOne - 1);

            // @ in the name hides this one, but we need it for matching previous feature settings
            embeddedVariables["@directionOne"] = directionOne;
            embeddedVariables["@oppositeDirectionOne"] = definition.oppositeDirectionOne;


            // TODO add remainder length to embedded variables
            // if (definition.spacingTypeOne != SpacingType.SPACING && definition.roundingTypeOne == RoundingType.EXACT)
            // {
            //     embeddedVariables.remainderLength = embeddedVariables.lengthOne;
            // }



            if (definition.distanceTypeOne != DistanceType.Measured)
            {
                var manipOffsetOne = definition.spacingTypeOne == SpacingType.SPACING ? distanceOne : (definition.roundingTypeOne == RoundingType.EXACT ? definition.distanceOne : distanceOne * (countOne - 1));

                if (definition.oppositeDirectionOne)
                    manipOffsetOne = -manipOffsetOne;

                var linearManipOne = linearManipulator({
                        "base" : manipOrigin,
                        "direction" : directionOne,
                        "offset" : manipOffsetOne,
                    });

                addManipulators(context, id, { "linearManipOne" : linearManipOne });
            }

            if (definition.distanceTypeOne == DistanceType.Measured && definition.hasOffsetOne)
            {
                var offsetManipOffsetOne = definition.oppositeOffsetDirectionOne ? -definition.offsetOne : definition.offsetOne;
                var offsetManipOriginOne = measuredDistanceOne * directionOne;

                var offsetManipOne = linearManipulator({
                        "base" : offsetManipOriginOne,
                        "direction" : -directionOne,
                        "offset" : offsetManipOffsetOne
                    });

                addManipulators(context, id, { "offsetManipOne" : offsetManipOne });
            }

            // Direction Two calculations
            directionQueryTwo = definition.directionTwo;
            oppositeDirectionTwo = definition.oppositeDirectionTwo;

            if (hasSecondDir)
            {
                var measuredDistanceTwo = try(definition.distanceTypeTwo == DistanceType.Measured ? evDistance(context, { "side0" : definition.startEntityTwo, "side1" : definition.endEntityTwo }).distance : 0 * inch);
                if (measuredDistanceTwo == undefined)
                    throw regenError(measureUndefined ~ "Two.");

                var directionInfoTwo = getCountAndDistance(
                definition.distanceTwo,
                definition.hasOffsetTwo,
                definition.oppositeDirectionTwo,
                definition.spacingTypeTwo,
                definition.distanceTypeTwo,
                definition.offsetTwo,
                definition.oppositeOffsetDirectionTwo,
                definition.instanceCountTypeTwo,
                definition.countTwo,
                definition.targetSpacingTwo,
                measuredDistanceTwo,
                definition.roundingTypeTwo
                );

                countTwo = directionInfoTwo.instanceCount;
                distanceTwo = directionInfoTwo.spacing;
                directionTwo = extractDirection(context, definition.directionTwo);

                // adding variables to embed for retrieval by the Extract Variables feature.
                embeddedVariables.countTwo = countTwo;
                embeddedVariables.spacingTwo = distanceTwo;
                embeddedVariables.lengthTwo = distanceTwo * (countTwo - 1);

                // @ in the name hides this one, but we need it for matching previous feature settings
                embeddedVariables["@directionTwo"] = directionTwo;
                embeddedVariables["@oppositeDirectionTwo"] = definition.oppositeDirectionTwo;

                if (definition.distanceTypeTwo != DistanceType.Measured)
                {
                    var manipOffsetTwo = definition.spacingTypeTwo == SpacingType.SPACING ? distanceTwo : (definition.roundingTypeTwo == RoundingType.EXACT ? definition.distanceTwo : distanceTwo * (countTwo - 1));

                    if (definition.oppositeDirectionTwo)
                        manipOffsetTwo = -manipOffsetTwo;

                    var linearManipTwo = linearManipulator({
                            "base" : manipOrigin,
                            "direction" : directionTwo,
                            "offset" : manipOffsetTwo,
                        });

                    addManipulators(context, id, { "linearManipTwo" : linearManipTwo });
                }

                if (definition.distanceTypeTwo == DistanceType.Measured && definition.hasOffsetTwo)
                {
                    var offsetManipOffsetTwo = definition.oppositeOffsetDirectionTwo ? -definition.offsetTwo : definition.offsetTwo;
                    var offsetManipOriginTwo = measuredDistanceTwo * directionTwo;

                    var offsetManipTwo = linearManipulator({
                            "base" : offsetManipOriginTwo,
                            "direction" : -directionTwo,
                            "offset" : offsetManipOffsetTwo
                        });

                    addManipulators(context, id, { "offsetManipTwo" : offsetManipTwo });
                }
            }

            // Direction Three calculations
            directionQueryThree = definition.directionThree;
            oppositeDirectionThree = definition.oppositeDirectionThree;

            if (hasSecondDir && hasThirdDir)
            {
                var measuredDistanceThree = try(definition.distanceTypeThree == DistanceType.Measured ? evDistance(context, { "side0" : definition.startEntityThree, "side1" : definition.endEntityThree }).distance : 0 * inch);
                if (measuredDistanceThree == undefined)
                    throw regenError(measureUndefined ~ "Three.");

                var directionInfoThree = getCountAndDistance(
                definition.distanceThree,
                definition.hasOffsetThree,
                definition.oppositeDirectionThree,
                definition.spacingTypeThree,
                definition.distanceTypeThree,
                definition.offsetThree,
                definition.oppositeOffsetDirectionThree,
                definition.instanceCountTypeThree,
                definition.countThree,
                definition.targetSpacingThree,
                measuredDistanceThree,
                definition.roundingTypeThree
                );

                countThree = directionInfoThree.instanceCount;
                distanceThree = directionInfoThree.spacing;
                directionThree = extractDirection(context, definition.directionThree);

                // adding variables to embed for retrieval by the Extract Variables feature.
                embeddedVariables.countThree = countThree;
                embeddedVariables.spacingThree = distanceThree;
                embeddedVariables.lengthThree = distanceThree * (countThree - 1);

                // @ in the name hides this one, but we need it for matching previous feature settings
                embeddedVariables["@directionThree"] = directionThree;
                embeddedVariables["@oppositeDirectionThree"] = definition.oppositeDirectionThree;

                if (definition.distanceTypeThree != DistanceType.Measured)
                {
                    var manipOffsetThree = definition.spacingTypeThree == SpacingType.SPACING ? distanceThree : (definition.roundingTypeThree == RoundingType.EXACT ? definition.distanceThree : distanceThree * (countThree - 1));

                    if (definition.oppositeDirectionThree)
                        manipOffsetThree = -manipOffsetThree;

                    var linearManipThree = linearManipulator({
                            "base" : manipOrigin,
                            "direction" : directionThree,
                            "offset" : manipOffsetThree,
                        });

                    addManipulators(context, id, { "linearManipThree" : linearManipThree });
                }

                if (definition.distanceTypeThree == DistanceType.Measured && definition.hasOffsetThree)
                {
                    var offsetManipOffsetThree = definition.oppositeOffsetDirectionThree ? -definition.offsetThree : definition.offsetThree;
                    var offsetManipOriginThree = measuredDistanceThree * directionThree;

                    var offsetManipThree = linearManipulator({
                            "base" : offsetManipOriginThree,
                            "direction" : -directionThree,
                            "offset" : offsetManipOffsetThree
                        });

                    addManipulators(context, id, { "offsetManipThree" : offsetManipThree });
                }

            }
        }

        // embedding variables for retrieval by the Extract Variables feature.
        embedVariableList(context, id, embeddedVariables);

        definition = adjustPatternDefinitionEntities(context, definition, false);

        var remainingTransform = getRemainderPatternTransform(context, { "references" : getReferencesForRemainderTransform(definition) });

        var withDirectionTransform = isFeaturePattern(definition.patternType) || isAtVersionOrLater(context, FeatureScriptVersionNumber.V518_MIRRORING_LIN_PATTERNS);


        // var targetTooBigOne = checkTargetSpacingAndDistance(context, id, distanceOne, definition.targetSpacingOne, "one");

        //Dir 1
        const result = try(computePatternOffset(context, directionQueryOne,
                oppositeDirectionOne, distanceOne, withDirectionTransform, remainingTransform));
        if (result == undefined)
            throw regenError(ErrorStringEnum.PATTERN_LINEAR_NO_DIR, ["directionOne"]);
        const offset1 = result.offset;
        const count1 = countOne;

        //Dir2, if any
        var offset2 = zeroVector(3) * meter;
        var count2 = 1;
        if (hasSecondDir)
        {
            count2 = countTwo;

            const result = try(computePatternOffset(context, directionQueryTwo,
                    oppositeDirectionTwo, distanceTwo, withDirectionTransform, remainingTransform));
            if (result != undefined)
            {
                offset2 = result.offset;
                if (parallelVectors(offset1, offset2))
                { //notify user that parallel directions are selected for dir1 and dir2
                    reportFeatureInfo(context, id, ErrorStringEnum.PATTERN_DIRECTIONS_PARALLEL);
                }
            }
            else if (count2 > 1)
            {
                //if count2 = 1, we don't need a direction (i.e. we keep the 1-directional solution),
                //so only complain about direction if the count for second direction is > 1.
                throw regenError(ErrorStringEnum.PATTERN_LINEAR_NO_DIR, ["directionTwo"]);
            }
        }
        else
        {
            hasThirdDir = false;
        }

        //Dir3, if any
        var offset3 = zeroVector(3) * meter;
        var count3 = 1;
        if (hasThirdDir)
        {
            count3 = countThree;

            const result = try(computePatternOffset(context, directionQueryThree,
                    oppositeDirectionThree, distanceThree, withDirectionTransform, remainingTransform));
            if (result != undefined)
            {
                offset3 = result.offset;
                if (parallelVectors(offset3, offset1))
                { //notify user that parallel directions are selected for dir1 and dir3
                    reportFeatureInfo(context, id, "Parallel directions selected for first and third direction.");
                }

                if (parallelVectors(offset3, offset2))
                { //notify user that parallel directions are selected for dir2 and dir3
                    reportFeatureInfo(context, id, "Parallel directions selected for second and third direction.");
                }
            }
            else if (count3 > 1)
            {
                //if count3 = 1, we don't need a direction (i.e. we keep the 1-directional solution),
                //so only complain about direction if the count for second direction is > 1.
                throw regenError(ErrorStringEnum.PATTERN_LINEAR_NO_DIR, ["directionThree"]);
            }
        }

        verifyPatternSize(context, id, count1 * count2 * count3);

        // Handle skip instances validation and manipulators
        var skippedIndicesSet = {};
        if (definition.skipInstances)
        {
            reportAnyInvalidEntriesThreeDirection(context, id, definition, count1, count2, count3, 
                definition.isCenteredOne, definition.isCenteredTwo, definition.isCenteredThree);

            // Build a set of skipped indices for fast lookup
            for (var instance in definition.skippedInstances)
            {
                const instanceKey = toString(instance.index1) ~ "," ~ toString(instance.index2) ~ "," ~ toString(instance.index3);
                skippedIndicesSet[instanceKey] = true;
            }
        }

        reportFeatureInfo(context, id, toString(count1 * count2 * count3) ~ " instances.");

        // Compute a vector of transforms
        // Adding just the values and mutating the transform rather than creating the translation from scratch on each iteration
        // is necessary for performance since it is in an inner loop bottleneck.
        var transforms = [];
        var instanceNames = [];
        var manipulatorPoints = [];
        const identity = identityMatrix(3);
        var instanceTransform = transform(identity, zeroVector(3) * meter);

        // If centered, create (count - 1) number of new instances on either side of the seed.
        var startIndex1 = definition.isCenteredOne ? 1 - count1 : 0;
        var startIndex2 = definition.isCenteredTwo ? 1 - count2 : 0;
        var startIndex3 = definition.isCenteredThree ? 1 - count3 : 0;


        // k for direction 3
        for (var k = startIndex3; k < count3; k += 1)
        {
            // instanceTransform.translation = offset3 * k + offset2 * startIndex2 + offset1 * startIndex1;

            const kName = k == 0 ? "" : ("k" ~ k);
            // j for direction 2
            for (var j = startIndex2; j < count2; j += 1)
            {
                const jName = j == 0 ? "" : ("j" ~ j);
                instanceTransform.translation = offset3 * k + offset2 * j + offset1 * startIndex1;

                // i for direction 1
                for (var i = startIndex1; i < count1; i += 1)
                {
                    // Check if this instance should be skipped
                    const instanceKey = toString(i) ~ "," ~ toString(j) ~ "," ~ toString(k);
                    const isSkipped = definition.skipInstances && skippedIndicesSet[instanceKey] == true;
                    
                    // skip recreating original (seed instance)
                    if (j != 0 || i != 0 || k != 0)
                    {
                        // Collect manipulator points for all non-seed instances for skip functionality
                        if (definition.skipInstances)
                        {
                            manipulatorPoints = append(manipulatorPoints, instanceTransform.translation);
                        }
                        
                        // Only add transform if not skipped
                        if (!isSkipped)
                        {
                            transforms = append(transforms, instanceTransform);
                            instanceNames = append(instanceNames, i ~ jName ~ kName);
                        }
                    }
                    instanceTransform.translation[0].value += offset1[0].value;
                    instanceTransform.translation[1].value += offset1[1].value;
                    instanceTransform.translation[2].value += offset1[2].value;

                }
                instanceTransform.translation[0].value += offset2[0].value;
                instanceTransform.translation[1].value += offset2[1].value;
                instanceTransform.translation[2].value += offset2[2].value;
            }
        }
        
        // Add manipulators for skip instances
        if (definition.skipInstances)
        {
            const instanceToIndex = function(instance)
                {
                    return gridCoordinatesToIndexThreeDirection(instance.index1, instance.index2, instance.index3, 
                        count1, count2, count3, 
                        definition.isCenteredOne, definition.isCenteredTwo, definition.isCenteredThree);
                };
            const isInstanceWithinRange = function(instance)
                {
                    return !isIndexOutsideRangeThreeDirection(instance.index1, instance.index2, instance.index3, 
                        count1, count2, count3, 
                        definition.isCenteredOne, definition.isCenteredTwo, definition.isCenteredThree);
                };
            addManipulators(context, id, { "points" : {
                        "points" : manipulatorPoints,
                        "selectedIndices" : mapArray(filter(definition.skippedInstances, isInstanceWithinRange), instanceToIndex),
                        "suppressedIndices" : [gridCoordinatesToIndexThreeDirection(0, 0, 0, count1, count2, count3, 
                            definition.isCenteredOne, definition.isCenteredTwo, definition.isCenteredThree)],
                        "manipulatorType" : ManipulatorType.TOGGLE_POINTS } as Manipulator });
        }

        definition.transforms = transforms;
        definition.instanceNames = instanceNames;
        definition.seed = definition.entities;
        
        definition.sketchPatternInfo = ErrorStringEnum.LINEAR_PATTERN_SKETCH_REAPPLY_INFO;

        applyPattern(context, id, definition, remainingTransform);
        
        // Record pattern metadata for downstream features
        var patternDirections = [offset1];
        if (count2 > 1)
        {
            patternDirections = append(patternDirections, offset2);
        }
        if (count3 > 1)
        {
            patternDirections = append(patternDirections, offset3);
        }
        setPatternData(context, id, RecordPatternType.LINEAR, patternDirections);

        if (definition.patternType == PatternType.PART && definition.composite && definition.operationType == NewBodyOperationType.NEW)
        {
            var newParts = qCreatedBy(id, EntityType.BODY);
            opCreateCompositePart(context, id + "compositePart1", {
                        "bodies" : qUnion([definition.entities, newParts]),
                        "closed" : definition.closedComposite
                    });
        }

        // delete query planes from "match previous feature"
        if (toCleanUp != [])
        {
            opDeleteBodies(context, id + "deleteBodies1", {
                        "entities" : qUnion(toCleanUp)
                    });
        }
    },

    { patternType : PatternType.PART, operationType : NewBodyOperationType.NEW, hasSecondDir : false,
            oppositeDirection : false, oppositeDirectionTwo : false, isCentered : false, isCenteredTwo : false, fullFeaturePattern : false,
            skipInstances : false, skippedInstances : [] }

    );

export function linearPatternPlusEditingLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, specifiedParameters is map, hiddenBodies is Query) returns map
{
    var measuredDistanceOne = oldDefinition.distanceTypeOne == DistanceType.Measured ? evDistance(context, { "side0" : oldDefinition.startEntityOne, "side1" : oldDefinition.endEntityOne }).distance : 0 * inch;

    var directionInfo = getCountAndDistance(
    oldDefinition.distanceOne,
    oldDefinition.hasOffsetOne,
    oldDefinition.oppositeDirectionOne,
    oldDefinition.spacingTypeOne,
    oldDefinition.distanceTypeOne,
    oldDefinition.offsetOne,
    oldDefinition.oppositeOffsetDirectionOne,
    oldDefinition.instanceCountTypeOne,
    oldDefinition.countOne,
    oldDefinition.targetSpacingOne,
    measuredDistanceOne,
    oldDefinition.roundingTypeOne
    );

    const countOne = directionInfo.instanceCount;
    const distanceOne = directionInfo.spacing;

    var measuredDistanceTwo = oldDefinition.distanceTypeTwo == DistanceType.Measured ? evDistance(context, { "side0" : oldDefinition.startEntityTwo, "side1" : oldDefinition.endEntityTwo }).distance : 0 * inch;

    var directionInfoTwo = getCountAndDistance(
    oldDefinition.distanceTwo,
    oldDefinition.hasOffsetTwo,
    oldDefinition.oppositeDirectionTwo,
    oldDefinition.spacingTypeTwo,
    oldDefinition.distanceTypeTwo,
    oldDefinition.offsetTwo,
    oldDefinition.oppositeOffsetDirectionTwo,
    oldDefinition.instanceCountTypeTwo,
    oldDefinition.countTwo,
    oldDefinition.targetSpacingTwo,
    measuredDistanceTwo,
    oldDefinition.roundingTypeTwo
    );

    const countTwo = directionInfoTwo.instanceCount;
    const distanceTwo = directionInfo.spacing;

    var measuredDistanceThree = oldDefinition.distanceTypeThree == DistanceType.Measured ? evDistance(context, { "side0" : oldDefinition.startEntityThree, "side1" : oldDefinition.endEntityThree }).distance : 0 * inch;
    var directionInfoThree = getCountAndDistance(
    oldDefinition.distanceThree,
    oldDefinition.hasOffsetThree,
    oldDefinition.oppositeDirectionThree,
    oldDefinition.spacingTypeThree,
    oldDefinition.distanceTypeThree,
    oldDefinition.offsetThree,
    oldDefinition.oppositeOffsetDirectionThree,
    oldDefinition.instanceCountTypeThree,
    oldDefinition.countThree,
    oldDefinition.targetSpacingThree,
    measuredDistanceThree,
    oldDefinition.roundingTypeThree
    );
    const countThree = directionInfoThree.instanceCount;
    const distanceThree = directionInfo.spacing;


    // direction one
    if (oldDefinition.spacingTypeOne == SpacingType.SPACING && definition.spacingTypeOne != SpacingType.SPACING)
    {
        definition.distanceOne = distanceOne * (countOne - 1);
        definition.targetSpacingOne = distanceOne;
    }

    if (oldDefinition.spacingTypeOne != SpacingType.SPACING && definition.spacingTypeOne == SpacingType.SPACING)
    {
        definition.distanceOne = distanceOne;
        definition.countOne = countOne;
    }

    if (oldDefinition.distanceTypeOne == DistanceType.Measured && definition.distanceTypeOne != DistanceType.Measured)
    {
        definition.distanceOne = definition.spacingTypeOne == SpacingType.SPACING ? distanceOne : distanceOne * (countOne - 1);
    }

    // direction two
    if (oldDefinition.spacingTypeTwo == SpacingType.SPACING && definition.spacingTypeTwo != SpacingType.SPACING)
    {
        definition.distanceTwo = distanceTwo * (countTwo - 1);
        definition.targetSpacingTwo = distanceTwo;
    }

    if (oldDefinition.spacingTypeTwo != SpacingType.SPACING && definition.spacingTypeTwo == SpacingType.SPACING)
    {
        definition.distanceTwo = distanceTwo;
        definition.countTwo = countTwo;
    }

    if (oldDefinition.distanceTypeTwo == DistanceType.Measured && definition.spacingTypeTwo == SpacingType.LENGTH)
    {
        definition.distanceTwo = distanceTwo * (countTwo - 1);
    }

    // direction three
    if (oldDefinition.spacingTypeThree == SpacingType.SPACING && definition.spacingTypeThree != SpacingType.SPACING)
    {
        definition.distanceThree = distanceThree * (countThree - 1);
        definition.targetSpacingThree = distanceThree;
    }

    if (oldDefinition.spacingTypeThree != SpacingType.SPACING && definition.spacingTypeThree == SpacingType.SPACING)
    {
        definition.distanceThree = distanceThree;
        definition.countThree = countThree;
    }

    if (oldDefinition.distanceTypeThree == DistanceType.Measured && definition.spacingTypeThree == SpacingType.LENGTH)
    {
        definition.distanceThree = distanceThree * (countThree - 1);
    }

    return definition;
}




/**
 * Used once per direction, this function calculates the number of instances, and spacing for them.
 */
export function getCountAndDistance(
    distance,
    hasOffset,
    oppositeDirection,
    dirType,
    distanceType,
    offset,
    oppositeOffsetDirection,
    instanceCountType,
    instanceCount,
    targetSpacing,
    measuredDistance,
    roundingType
) returns map
{
    var directionInfo = {};
    var spacing;

    if (distanceType == DistanceType.Measured)
    {
        // adds an offset to the measured distance
        distance = measuredDistance;
        if (hasOffset)
        {
            var offset = oppositeOffsetDirection ? -offset : offset;
            distance -= offset;
        }
    }

    if (dirType == SpacingType.LENGTH && instanceCountType == InstanceCountType.TARGET_SPACING)
    {
        if (roundingType == RoundingType.ROUND)
        {
            instanceCount = round(distance / targetSpacing) + 1;
        }
        if (roundingType == RoundingType.UP)
        {
            instanceCount = ceil(distance / targetSpacing) + 1;
        }
        if (roundingType == RoundingType.DOWN || roundingType == RoundingType.EXACT)
        {
            instanceCount = floor(distance / targetSpacing) + 1;
        }
    }

    if (dirType == SpacingType.SPACING)
    {
        spacing = distance;
    }

    else if (dirType == SpacingType.LENGTH)
    {
        spacing = distance / (instanceCount - 1);
    }

    if (dirType == SpacingType.LENGTH && instanceCountType == InstanceCountType.TARGET_SPACING && roundingType == RoundingType.EXACT)
    {
        spacing = targetSpacing;
    }

    directionInfo.instanceCount = instanceCount;
    directionInfo.spacing = spacing;
    directionInfo.targetLength = distance;
    // directionInfo.distanceTillNextInstance =

    return directionInfo;
}

/**
 * Manipulator change function for linearPatternPlus.
 * Handles updates from linear manipulators, offset manipulators, and skip instances toggle points.
 * 
 * @param context : The context for this operation
 * @param definition : The current feature definition
 * @param newManipulators : Map containing the updated manipulator values
 * @returns map : Updated definition with new manipulator values
 */
export function linearPatternPlusManipulatorFunction(context is Context, definition is map, newManipulators is map) returns map
{
    if (newManipulators["linearManipOne"] is map)
    {
        definition.oppositeDirectionOne = newManipulators["linearManipOne"].offset < 0;
        definition.distanceOne = abs(newManipulators["linearManipOne"].offset);
    }
    if (newManipulators["linearManipTwo"] is map)
    {
        definition.oppositeDirectionTwo = newManipulators["linearManipTwo"].offset < 0;
        definition.distanceTwo = abs(newManipulators["linearManipTwo"].offset);
    }
    if (newManipulators["linearManipThree"] is map)
    {
        definition.oppositeDirectionThree = newManipulators["linearManipThree"].offset < 0;
        definition.distanceThree = abs(newManipulators["linearManipThree"].offset);
    }
    if (newManipulators["offsetManipOne"] is map)
    {
        definition.oppositeOffsetDirectionOne = newManipulators["offsetManipOne"].offset < 0;
        definition.offsetOne = abs(newManipulators["offsetManipOne"].offset);
    }
    if (newManipulators["offsetManipTwo"] is map)
    {
        definition.oppositeOffsetDirectionTwo = newManipulators["offsetManipTwo"].offset < 0;
        definition.offsetTwo = abs(newManipulators["offsetManipTwo"].offset);
    }
    if (newManipulators["offsetManipThree"] is map)
    {
        definition.oppositeOffsetDirectionThree = newManipulators["offsetManipThree"].offset < 0;
        definition.offsetThree = abs(newManipulators["offsetManipThree"].offset);
    }
    
    // Handle skip instances manipulator changes
    if (newManipulators["points"] is map)
    {
        // Determine the instance counts to use
        var count1 = definition.countOne;
        var count2 = definition.hasSecondDir ? definition.countTwo : 1;
        var count3 = (definition.hasSecondDir && definition.hasThirdDir) ? definition.countThree : 1;
        
        const indexToInstance = function(index)
            {
                return indexToGridCoordinatesThreeDirection(index, count1, count2, count3, 
                    definition.isCenteredOne, definition.isCenteredTwo, definition.isCenteredThree);
            };
        const isInstanceOutsideRange = function(instance)
            {
                return isIndexOutsideRangeThreeDirection(instance.index1, instance.index2, instance.index3, 
                    count1, count2, count3, 
                    definition.isCenteredOne, definition.isCenteredTwo, definition.isCenteredThree);
            };

        const newInstances = mapArray(newManipulators["points"].selectedIndices, indexToInstance);
        const outInstances = filter(definition.skippedInstances, isInstanceOutsideRange);

        if (size(outInstances) == 0)
        {
            definition.skippedInstances = newInstances;
            return definition;
        }

        definition.skippedInstances = makeArray(size(newInstances) + size(outInstances));
        var newIndex = 0;
        var outIndex = 0;

        for (var i = 0; i < size(definition.skippedInstances); i += 1)
        {
            if (newIndex >= size(newInstances))
            {
                definition.skippedInstances[i] = outInstances[outIndex];
                outIndex += 1;
            }
            else if (outIndex >= size(outInstances))
            {
                definition.skippedInstances[i] = newInstances[newIndex];
                newIndex += 1;
            }
            else if (compareInstanceIndices(newInstances[newIndex], outInstances[outIndex]) < 0)
            {
                definition.skippedInstances[i] = newInstances[newIndex];
                newIndex += 1;
            }
            else
            {
                definition.skippedInstances[i] = outInstances[outIndex];
                outIndex += 1;
            }
        }
    }

    return definition;
}

export function checkTargetSpacingAndDistance(context is Context, id is Id, distance is ValueWithUnits, targetSpacing is ValueWithUnits, directionName is string) returns boolean
{
    var targetTooBig = targetSpacing > distance;
    if (targetTooBig)
    {
        regenError("Direction " ~ directionName ~ " target spacing is greater than the total pattern length.");
        // reportFeatureWarning(context, id, "Direction " ~ directionName ~ " target spacing is greater than the total pattern length.");
    }
    return targetTooBig;

}

/**
 * Compares two three-dimensional instance coordinates for sorting.
 * Orders by index3, then index2, then index1 (row-major order with third direction as outermost).
 * 
 * @param instance1 : First instance with index1, index2, index3 fields
 * @param instance2 : Second instance with index1, index2, index3 fields
 * @returns number : Negative if instance1 < instance2, positive if instance1 > instance2, 0 if equal
 */
function compareInstanceIndices(instance1 is map, instance2 is map) returns number
{
    if (instance1.index3 != instance2.index3)
    {
        return instance1.index3 - instance2.index3;
    }
    if (instance1.index2 != instance2.index2)
    {
        return instance1.index2 - instance2.index2;
    }
    return instance1.index1 - instance2.index1;
}

/**
 * Reports any invalid entries in the skipped instances array.
 * Checks for seed index (0,0,0) and indices outside the pattern range.
 * 
 * @param context : The context for this operation
 * @param id : The id of the feature
 * @param definition : The feature definition containing skippedInstances array
 */
function reportAnyInvalidEntriesThreeDirection(context is Context, id is Id, definition is map, count1 is number, count2 is number, count3 is number, isCentered1 is boolean, isCentered2 is boolean, isCentered3 is boolean)
{
    var hasSeedIndex = false;
    var hasOutsideRangeIndex = false;

    for (var instance in definition.skippedInstances)
    {
        if (instance.index1 == 0 && instance.index2 == 0 && instance.index3 == 0)
        {
            hasSeedIndex = true;
        }

        if (isIndexOutsideRangeThreeDirection(instance.index1, instance.index2, instance.index3, count1, count2, count3, isCentered1, isCentered2, isCentered3))
        {
            hasOutsideRangeIndex = true;
        }
    }

    if (hasSeedIndex)
    {
        reportFeatureInfo(context, id, ErrorStringEnum.PATTERN_SKIPPED_INSTANCES_SEED_INDEX);
    }
    else if (hasOutsideRangeIndex)
    {
        reportFeatureInfo(context, id, ErrorStringEnum.PATTERN_SKIPPED_INSTANCES_OUT_OF_RANGE_INDEX);
    }
}

/**
 * Converts a linear index to three-dimensional grid coordinates.
 * Used for manipulator point selection in skip instances.
 * 
 * @param index : The linear index to convert
 * @param instanceCount1 : Number of instances in first direction
 * @param instanceCount2 : Number of instances in second direction
 * @param instanceCount3 : Number of instances in third direction
 * @param isCentered1 : Whether first direction is centered
 * @param isCentered2 : Whether second direction is centered
 * @param isCentered3 : Whether third direction is centered
 * @returns map : Map containing index1, index2, and index3
 */
function indexToGridCoordinatesThreeDirection(index is number, instanceCount1 is number, instanceCount2 is number, instanceCount3 is number, isCentered1 is boolean, isCentered2 is boolean, isCentered3 is boolean) returns map
{
    const index1Max = isCentered1 ? 2 * instanceCount1 - 1 : instanceCount1;
    const index2Max = isCentered2 ? 2 * instanceCount2 - 1 : instanceCount2;
    const planarSize = index1Max * index2Max;
    
    const planarIndex = index % planarSize;
    
    return {
            "index1" : isCentered1 ? planarIndex % index1Max - instanceCount1 + 1 : planarIndex % index1Max,
            "index2" : isCentered2 ? floor(planarIndex / index1Max) - instanceCount2 + 1 : floor(planarIndex / index1Max),
            "index3" : isCentered3 ? floor(index / planarSize) - instanceCount3 + 1 : floor(index / planarSize)
        };
}

/**
 * Converts three-dimensional grid coordinates to a linear index.
 * Used for manipulator point selection in skip instances.
 * 
 * @param index1 : Index in first direction
 * @param index2 : Index in second direction
 * @param index3 : Index in third direction
 * @param instanceCount1 : Number of instances in first direction
 * @param instanceCount2 : Number of instances in second direction
 * @param instanceCount3 : Number of instances in third direction
 * @param isCentered1 : Whether first direction is centered
 * @param isCentered2 : Whether second direction is centered
 * @param isCentered3 : Whether third direction is centered
 * @returns number : The linear index
 */
function gridCoordinatesToIndexThreeDirection(index1 is number, index2 is number, index3 is number, instanceCount1 is number, instanceCount2 is number, instanceCount3 is number, isCentered1 is boolean, isCentered2 is boolean, isCentered3 is boolean) returns number
{
    const index1Max = isCentered1 ? 2 * instanceCount1 - 1 : instanceCount1;
    const index2Max = isCentered2 ? 2 * instanceCount2 - 1 : instanceCount2;

    const normalizedIndex1 = isCentered1 ? index1 + instanceCount1 - 1 : index1;
    const normalizedIndex2 = isCentered2 ? index2 + instanceCount2 - 1 : index2;
    const normalizedIndex3 = isCentered3 ? index3 + instanceCount3 - 1 : index3;

    return normalizedIndex1 + normalizedIndex2 * index1Max + normalizedIndex3 * index1Max * index2Max;
}

/**
 * Checks if a three-dimensional grid coordinate is outside the valid pattern range.
 * 
 * @param index1 : Index in first direction
 * @param index2 : Index in second direction
 * @param index3 : Index in third direction
 * @param instanceCount1 : Number of instances in first direction
 * @param instanceCount2 : Number of instances in second direction
 * @param instanceCount3 : Number of instances in third direction
 * @param isCentered1 : Whether first direction is centered
 * @param isCentered2 : Whether second direction is centered
 * @param isCentered3 : Whether third direction is centered
 * @returns boolean : True if the index is outside range
 */
function isIndexOutsideRangeThreeDirection(index1 is number, index2 is number, index3 is number, instanceCount1 is number, instanceCount2 is number, instanceCount3 is number, isCentered1 is boolean, isCentered2 is boolean, isCentered3 is boolean) returns boolean
{
    return index1 >= instanceCount1 || index1 < (isCentered1 ? -instanceCount1 + 1 : 0)
        || index2 >= instanceCount2 || index2 < (isCentered2 ? -instanceCount2 + 1 : 0)
        || index3 >= instanceCount3 || index3 < (isCentered3 ? -instanceCount3 + 1 : 0);
}

