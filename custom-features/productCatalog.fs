FeatureScript 2543;
import(path : "onshape/std/common.fs", version : "2543.0");

ICON::import(path : "afdd37699452fc5ddc6bcac3", version : "e1e302c24d967564b34390bc");
PIPE_ICON::import(path : "69ae19cac148cbb228888704", version : "7093f4f2929ec9ae1f2bba2b");
COUPLER_ICON::import(path : "f289f9372945dcfc18c4c30c", version : "5f621c002dd0863ba5269e2a");
CROSS_ICON::import(path : "150cd5b504f0dd365fe569fa", version : "d14dd4b9c7ae94becf85f31a");
ELBOW45_ICON::import(path : "b268e5a7279d8aeb2f40cf28", version : "080cadb3e5593cf1aba4c2a4");
ELBOW90_ICON::import(path : "7162d67c77e91f7deb1b2310", version : "85b1744ba1ff2b11600fd3d4");
ELBOW_CUSTOM_ICON::import(path : "13e243eb117b880cb9f9ffdb", version : "026cefba8493cd61b559503e");
REDUCER_ICON::import(path : "7d4f9a97b0f57fc82303f360", version : "e8d96757ad00abfbaa3fcf5a");
TEE_ICON::import(path : "f885ebda2f502bddd0d729c0", version : "86cbaa8007e020acaa41f5ce");

PIPE_A::import(path : "33bb55f7cf19540c13744ee5/cd6cdfe5c324bbd939ba19a0/5f2225391621f6ecabefd463", version : "3244916499db6235de44e588");
PIPE_B::import(path : "33bb55f7cf19540c13744ee5/cd6cdfe5c324bbd939ba19a0/65db5bd2a935fbf0c9b703d7", version : "4ff689e5a2ee66ac4b60ae2f");
COUPLER::import(path : "15384a34b321026ed8da71db/172c2bc7cb020a87cf18100e/dec0ce0d797b49f4ca7516b0", version : "325af0f7a9f59496c796c9b2");
CROSS::import(path : "78fe9bc709068d2a6cdd8f3e/31cf3f4213c0d406cb1e868e/8464d69d8fb7fcd8ef26b251", version : "0b9bc58f570c624c25a4cee3");
ELBOW45::import(path : "d8019525bbc6ea41346b0df5/e7a4524e36a123ea6c2eb05f/d52d7c00bfd70ab24b1ebfcf", version : "c1a407e6089a1ded563921e5");
ELBOW90::import(path : "d8019525bbc6ea41346b0df5/e7a4524e36a123ea6c2eb05f/274abfd8ac5ff588c2496d81", version : "9ee13700eea67d5124e19938");
ELBOW_CUSTOM::import(path : "d8019525bbc6ea41346b0df5/e7a4524e36a123ea6c2eb05f/a434e11ec0a53560cb24953f", version : "f8339ef61c0c2b2592e7f2d1");
REDUCER::import(path : "d92b6bca7bb0aa0491df3bdb/bbd477794bbfe9d70ca7734e/215e3f24bf8e080f41b41798", version : "7b4cafdf62a9ae6fab92b71b");
TEE::import(path : "d31146bbec9ad9ca98cb3350/63b607c2c1240da9dd95ff02/ccd2d34f1c93ca0d3636e5ea", version : "512c42592d29127fb185be23");

export enum CatagoriesEnum
{
    annotation { "Name" : "|", "Icon" : PIPE_ICON::BLOB_DATA }
    PIPE,
    annotation { "Name" : "|", "Icon" : COUPLER_ICON::BLOB_DATA }
    COUPLER,
    annotation { "Name" : "|", "Icon" : CROSS_ICON::BLOB_DATA }
    CROSS,
    annotation { "Name" : "|", "Icon" : ELBOW45_ICON::BLOB_DATA }
    ELBOW45,
    annotation { "Name" : "|", "Icon" : ELBOW90_ICON::BLOB_DATA }
    ELBOW90,
    annotation { "Name" : "|", "Icon" : ELBOW_CUSTOM_ICON::BLOB_DATA }
    ELBOW_CUSTOM,
    annotation { "Name" : "|", "Icon" : REDUCER_ICON::BLOB_DATA }
    REDUCER,
    annotation { "Name" : "|", "Icon" : TEE_ICON::BLOB_DATA }
    TEE,
}

export enum PipeEnum
{
    annotation { "Name" : "Option A" }
    OPTION_A,
    annotation { "Name" : "Option B" }
    OPTION_B,
}

export enum CouplerEnum
{
    annotation { "Name" : "Option A" }
    OPTION_A,
}

export enum CrossEnum
{
    annotation { "Name" : "Option A" }
    OPTION_A,
}

export enum Elbow45Enum
{
    annotation { "Name" : "Option A" }
    OPTION_A,
}

export enum Elbow90Enum
{
    annotation { "Name" : "Option A" }
    OPTION_A,
}

export enum ElbowCustomEnum
{
    annotation { "Name" : "Option A" }
    OPTION_A,
}

export enum ReducerEnum
{
    annotation { "Name" : "Option A" }
    OPTION_A,
}

export enum TeeEnum
{
    annotation { "Name" : "Option A" }
    OPTION_A,
}

export enum ConfigurationType
{
    BOOL,
    LIST,
    LENGTH,
    ANGLE,
    INTEGER,
    REAL,
    TEXT
}

export enum GenericList
{
    A,
    B,
    C
}

enum ConfigData
{
    CONFIG_TYPE,
    DEFAULT_VALUE
}

enum SubCategoryData
{
    PART,
    CONFIGURATIONS
}

const CATALOG = {
        CatagoriesEnum.PIPE : {
            SubCategoryData.PART : {
                PipeEnum.OPTION_A : PIPE_A::build,
                PipeEnum.OPTION_B : PIPE_B::build },
            SubCategoryData.CONFIGURATIONS : {
                "od" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1.5 * inch },
                "id" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1 * inch },
                "length" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 10 * inch },
                "genericList" : { ConfigData.CONFIG_TYPE : ConfigurationType.LIST, ConfigData.DEFAULT_VALUE : GenericList.A }
            },
        },
        CatagoriesEnum.COUPLER : {
            SubCategoryData.PART : {
                CouplerEnum.OPTION_A : COUPLER::build
            },
            SubCategoryData.CONFIGURATIONS : {
                "od" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1.5 * inch },
                "id" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1 * inch },
                "length" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 4 * inch },
                "genericList" : { ConfigData.CONFIG_TYPE : ConfigurationType.LIST, ConfigData.DEFAULT_VALUE : GenericList.A }
            } },

        CatagoriesEnum.CROSS : {
            SubCategoryData.PART : {
                CrossEnum.OPTION_A : CROSS::build
            },
            SubCategoryData.CONFIGURATIONS : {
                "od" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1.5 * inch },
                "id" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1 * inch },
                "genericList" : { ConfigData.CONFIG_TYPE : ConfigurationType.LIST, ConfigData.DEFAULT_VALUE : GenericList.A }
            } },
        CatagoriesEnum.ELBOW45 : {
            SubCategoryData.PART : {
                Elbow45Enum.OPTION_A : ELBOW45::build
            },
            SubCategoryData.CONFIGURATIONS : {
                "od" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1.5 * inch },
                "id" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1 * inch },
                "radius" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 4 * inch },
                "genericList" : { ConfigData.CONFIG_TYPE : ConfigurationType.LIST, ConfigData.DEFAULT_VALUE : GenericList.A }
            } },
        CatagoriesEnum.ELBOW90 : {
            SubCategoryData.PART : {
                Elbow90Enum.OPTION_A : ELBOW90::build
            },
            SubCategoryData.CONFIGURATIONS : {
                "od" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1.5 * inch },
                "id" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1 * inch },
                "radius" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 4 * inch },
                "genericList" : { ConfigData.CONFIG_TYPE : ConfigurationType.LIST, ConfigData.DEFAULT_VALUE : GenericList.A }
            } },
        CatagoriesEnum.ELBOW_CUSTOM : {
            SubCategoryData.PART : {
                ElbowCustomEnum.OPTION_A : ELBOW_CUSTOM::build
            },
            SubCategoryData.CONFIGURATIONS : {
                "od" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1.5 * inch },
                "id" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1 * inch },
                "radius" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 4 * inch },
                "angle" : { ConfigData.CONFIG_TYPE : ConfigurationType.ANGLE, ConfigData.DEFAULT_VALUE : 25 * degree },
                "genericList" : { ConfigData.CONFIG_TYPE : ConfigurationType.LIST, ConfigData.DEFAULT_VALUE : GenericList.A }
            } },
        CatagoriesEnum.REDUCER : {
            SubCategoryData.PART : {
                ReducerEnum.OPTION_A : REDUCER::build
            },
            SubCategoryData.CONFIGURATIONS : {
                "od" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1.5 * inch },
                "id" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1 * inch },
                "od2" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 2.5 * inch },
                "id2" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 2 * inch },
                "length" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 4 * inch },
                "genericList" : { ConfigData.CONFIG_TYPE : ConfigurationType.LIST, ConfigData.DEFAULT_VALUE : GenericList.A }
            } },
        CatagoriesEnum.TEE : {
            SubCategoryData.PART : {
                TeeEnum.OPTION_A : TEE::build
            },
            SubCategoryData.CONFIGURATIONS : {
                "od" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1.5 * inch },
                "id" : { ConfigData.CONFIG_TYPE : ConfigurationType.LENGTH, ConfigData.DEFAULT_VALUE : 1 * inch },
                "genericList" : { ConfigData.CONFIG_TYPE : ConfigurationType.LIST, ConfigData.DEFAULT_VALUE : GenericList.A }
            } },
    };

annotation {
        "Feature Type Name" : "Product Catalog",
        "Feature Type Description" : "",
        "Icon" : ICON::BLOB_DATA,
        "Editing Logic Function" : "myEditLogic",
        "Feature Name Template" : "#featureName"
    }
export const productCatalog = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Category", "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.category is CatagoriesEnum;

        if (definition.category == CatagoriesEnum.PIPE)
        {
            annotation { "Name" : "Sub category enum" }
            definition.pipe is PipeEnum;
        }
        else if (definition.category == CatagoriesEnum.COUPLER)
        {
            annotation { "Name" : "Sub category enum" }
            definition.coupler is CouplerEnum;
        }
        else if (definition.category == CatagoriesEnum.CROSS)
        {
            annotation { "Name" : "Sub category enum" }
            definition.cross is CrossEnum;
        }
        else if (definition.category == CatagoriesEnum.ELBOW45)
        {
            annotation { "Name" : "Sub category enum" }
            definition.elbow45 is Elbow45Enum;
        }
        else if (definition.category == CatagoriesEnum.ELBOW90)
        {
            annotation { "Name" : "Sub category enum" }
            definition.elbow90 is Elbow90Enum;
        }
        else if (definition.category == CatagoriesEnum.ELBOW_CUSTOM)
        {
            annotation { "Name" : "Sub category enum" }
            definition.elbowCustom is ElbowCustomEnum;
        }
        else if (definition.category == CatagoriesEnum.REDUCER)
        {
            annotation { "Name" : "Sub category enum" }
            definition.reducer is ReducerEnum;
        }
        else if (definition.category == CatagoriesEnum.TEE)
        {
            annotation { "Name" : "Sub category enum" }
            definition.tee is TeeEnum;
        }

        annotation { "Name" : "Locations", "Filter" : EntityType.FACE || BodyType.MATE_CONNECTOR }
        definition.locations is Query;

        annotation { "Name" : "Replicate to matching faces" }
        definition.replicateToMatchingFaces is boolean;

        annotation {
                    "Name" : "Configurations",
                    "Item name" : "Configuration",
                    "Driven query" : "entities",
                    "Item label template" : "#name" }
        definition.configurations is array;
        for (var configuration in definition.configurations)
        {
            annotation { "Name" : "Entities", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1, "UIHint" : UIHint.ALWAYS_HIDDEN }
            configuration.entities is Query;

            annotation { "Name" : "Name", "UIHint" : UIHint.ALWAYS_HIDDEN }
            configuration.name is string;

            annotation { "Name" : "Type", "UIHint" : UIHint.ALWAYS_HIDDEN }
            configuration.configType is ConfigurationType;

            if (configuration.configType == ConfigurationType.BOOL)
            {
                annotation { "Name" : "." }
                configuration.bool is boolean;
            }
            else if (configuration.configType == ConfigurationType.LIST)
            {
                annotation { "Name" : "." }
                configuration.list is GenericList;
            }
            else if (configuration.configType == ConfigurationType.LENGTH)
            {
                annotation { "Name" : "." }
                isLength(configuration.length, LENGTH_BOUNDS);
            }
            else if (configuration.configType == ConfigurationType.ANGLE)
            {
                annotation { "Name" : "." }
                isAngle(configuration.angle, ANGLE_360_BOUNDS);
            }
            else if (configuration.configType == ConfigurationType.INTEGER)
            {
                annotation { "Name" : "." }
                isInteger(configuration.integer, POSITIVE_COUNT_BOUNDS);
            }
            else if (configuration.configType == ConfigurationType.REAL)
            {
                annotation { "Name" : "." }
                isReal(configuration.real, POSITIVE_REAL_BOUNDS);
            }
            else if (configuration.configType == ConfigurationType.TEXT)
            {
                annotation { "Name" : "." }
                configuration.text is string;
            }
        }
    }
    {
        var instantiator = newInstantiator(id + "inst", {});
        var configs = {};

        for (var config in definition.configurations)
        {
            var thisConfig = {};

            thisConfig[config.name] = switch (config.configType) {
                        ConfigurationType.BOOL : config.bool,
                        ConfigurationType.LIST : config.list,
                        ConfigurationType.LENGTH : config.length,
                        ConfigurationType.ANGLE : config.angle,
                        ConfigurationType.INTEGER : config.integer,
                        ConfigurationType.REAL : config.real,
                        ConfigurationType.TEXT : config.text,
                    };

            configs = mergeMaps(configs, thisConfig);
        }

        const subCategorySelection = switch (definition.category) {
                    CatagoriesEnum.PIPE : definition.pipe,
                    CatagoriesEnum.COUPLER : definition.coupler,
                    CatagoriesEnum.CROSS : definition.cross,
                    CatagoriesEnum.ELBOW45 : definition.elbow45,
                    CatagoriesEnum.ELBOW90 : definition.elbow90,
                    CatagoriesEnum.ELBOW_CUSTOM : definition.elbowCustom,
                    CatagoriesEnum.REDUCER : definition.reducer,
                    CatagoriesEnum.TEE : definition.tee,
                };

        const part = CATALOG[definition.category][SubCategoryData.PART][subCategorySelection];

        if (definition.replicateToMatchingFaces)
        {
            definition.locations = qUnion([definition.locations, qMatching(definition.locations)]);
        }

        var vectors;
        var evLocations = evaluateQuery(context, definition.locations);

        for (var location in evLocations)
        {
            var toPlane;

            if (!isQueryEmpty(context, qBodyType(location, BodyType.MATE_CONNECTOR)))
            {
                toPlane = evMateConnector(context, {
                                "mateConnector" : location
                            })->plane();
            }
            else
            {
                toPlane = evFaceTangentPlane(context, {
                            "face" : location,
                            "parameter" : vector(0.5, 0.5)
                        });
            }

            addInstance(instantiator, part, {
                        "configuration" : configs,
                        "transform" : transform(XY_PLANE, toPlane)
                    });
        }

        instantiate(context, instantiator);

        setFeatureComputedParameter(context, id, {
                    "name" : "featureName",
                    "value" : definition.category ~ "_" ~ subCategorySelection
                });
    });

export function myEditLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, specifiedParameters is map, hiddenBodies is Query) returns map
{
    if (size(oldDefinition.configurations) != size(definition.configurations))
    {
        definition.configurations = oldDefinition.configurations;
    }

    if (oldDefinition.category != definition.category)
    {
        // Clear the array
        definition.configurations = [];

        const configs = CATALOG[definition.category][SubCategoryData.CONFIGURATIONS];

        for (var config in configs)
        {
            var thisConfig = {};
            thisConfig.name = config.key;
            thisConfig.configType = config.value[ConfigData.CONFIG_TYPE];

            definition.configurations = append(definition.configurations, thisConfig);
        }
    }

    return definition;
}
