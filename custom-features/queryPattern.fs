FeatureScript 2878;
import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/queryVariable.fs", version : "2878.0");

icon::import(path : "1b1876c4208ee0105bc5dc22", version : "8b4fcea5cec1f2bcc95aa71f");
image::import(path : "2423f73366a997651d42c6a3", version : "a8c55fd240b78a829c9517eb");


annotation { "Feature Type Name" : "Query pattern",
"Feature Type Description" : "Patterns feature operations on a per-query basis, allowing similar steps to be applied to dissimilar shapes. For example, extruding and filleting a bunch of faces that are all different shapes.",
"Icon" : icon::BLOB_DATA,
"Description Image" : image::BLOB_DATA}
export const queryPattern = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Seed query variable #" }
        definition.seedQueryVariableName is string;

        annotation { "Name" : "Target queries", "Filter" : EntityType.BODY || EntityType.FACE || EntityType.EDGE || EntityType.VERTEX }
        definition.targetQueries is Query;

        annotation { "Name" : "Features to loop", "UIHint" : UIHint.INITIAL_FOCUS_ON_EDIT }
        definition.featuresToLoop is FeatureList;
    }
    {


        const seedQuery = getQueryVariable(context, definition.seedQueryVariableName);

        const targetQueries = definition.targetQueries->qSubtraction(seedQuery);

        forEachEntity(context, id + "featurePattern", targetQueries, function(targetQuery is Query, id is Id)
            {

                setQueryVariable(context, definition.seedQueryVariableName, targetQuery);

                // Inline implementation of callSubfeatureAndProcessStatus pattern
                // Cannot use callSubfeatureAndProcessStatus directly because applyPattern requires
                // a 4th parameter (transform) that doesn't fit the standard 3-parameter signature
                const patternId = id + "pattern";
                var thrownError;
                try
                {
                    applyPattern(context, patternId, {
                                "patternType" : PatternType.FEATURE,
                                "instanceFunction" : definition.featuresToLoop,
                                "fullFeaturePattern" : true,
                                "transforms" : [identityTransform()],
                                "instanceNames" : ["instanceName"],
                                "sketchPatternInfo" : "Some sketch pattern info" //hidden parameter of new feature pattern
                            }, identityTransform());
                }
                catch (e)
                {
                    thrownError = e;
                }

                // Process any subfeature status and propagate errors to the top-level feature
                processSubfeatureStatus(context, id, {
                            "subfeatureId" : patternId,
                            "propagateErrorDisplay" : true
                        });

                // Re-throw feature error if it exists
                const featureError = getFeatureError(context, id);
                if (featureError != undefined)
                {
                    throw featureError;
                }

                // Re-throw any thrown error that did not create an ERROR level feature status
                if (thrownError != undefined)
                {
                    throw thrownError;
                }

            });
    });


// Do we need this as an input? Do we need to filter out other kinds of geometry from the "Target queries"? can be set by EL unless ambiguous.
// export enum InitialQueryType
// {
//     /* List of possible values */
// }
