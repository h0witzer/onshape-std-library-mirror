FeatureScript 1660;
import(path : "onshape/std/common.fs", version : "1660.0");

export import(path : "onshape/std/mateconnectoraxistype.gen.fs", version : "1660.0");
export import(path : "onshape/std/rotationtype.gen.fs", version : "1660.0");

import(path : "4c21d0c3c89c0a81aadfdac6/339ad59968f272385a348f5e/c25d1032bab62fa47fdacc60", version : "050d96bfed867395804ec063");

/**
 * An enum defining the axis to orient along for a `pointDerive` feature.
 */
export enum PrimaryAxis
{
    annotation { "Name" : "Z axis" }
    Z,
    annotation { "Name" : "Y axis" }
    Y,
    annotation { "Name" : "X axis" }
    X
}

/**
 * Throws a [regenError] and marks the specified Part Studio reference parameter as faulty if no entities have been selected.
 *
 * @param parameterName :
 *          The name of the part studio reference parameter to check.
 *          @autocomplete `"myPartStudio"`
 * @param errorToReport {string} :
 *          The error to report.
 *          @autocomplete `ErrorStringEnum.IMPORT_DERIVED_NO_PARTS`
 */
export function verifyNonemptyStudioReference(context is Context, definition is map, parameterName is string, errorToReport is string)
{
    if (definition[parameterName].buildFunction == undefined)
    {
        throw regenError(errorToReport, [parameterName]);
    }
}

/**
 * Strips construction planes and sketch geometry from `partStudioData`.
 */
export function removePlanesAndSketches(partStudioData is PartStudioData) returns PartStudioData
{
    partStudioData.partQuery = partStudioData.partQuery->qSubtraction(
        qUnion([
                    partStudioData.partQuery->qSketchFilter(SketchObject.YES),
                    // qGeometry(GeometryType.PLANE) filters out construction planes for some reason (bug?)
                    partStudioData.partQuery->qConstructionFilter(ConstructionObject.YES)->qBodyType(BodyType.SHEET)
                ]));
    return partStudioData;
}

/**
 * Instantiates entities in a manner suitable for `opPointDerive`.
 *
 * @returns {Query} : A `Query` for the instantiated entities.
 */
export function instantiatePointDerivePartStudio(context is Context, id is Id, partStudioData is PartStudioData) returns Query
{
    partStudioData.partQuery = qUnion([
                partStudioData.partQuery,
                qMateConnectorsOfParts(partStudioData.partQuery)
            ]);
    try
    {
        const instantiator = newInstantiator(id + "import");
        // instantiator transform and disambiguation args cannot be used since we need to know where the mate connectors are first
        var importQuery = addInstance(instantiator, partStudioData);
        instantiate(context, instantiator);
        return importQuery;
    }

    throw regenError("Failed to import any entities. Ensure the selected part studio is not empty.");
}

/**
 * Derives entities in a part studio at one or more specified locations.
 *
 * @seealso [addPointDeriveManipulator]
 *
 * @param id : @autocomplete `id + "pointDerive"`
 * @param definition {{
 *          @field importQuery {Query} :
 *                  A `Query` for imported entities to use.
 *                  @seealso [instantiatePointDerivePartStudio]
 *          @field keepImports {boolean} : @optional
 *                  Whether to keep imported entities. Defaults to `false`.
 *          @field index {number} :
 *                  The index of the currently selected mate connector.
 *                  `index` is automatically clamped to the current number of mate connectors.
 *                  @autocomplete `getPointDeriveIndex(definition)`
 *          @field locations {array} :
 *                  An array of `CoordSystem`s to derive entities to.
 *                  A copy of the derived entities is automatically added to each input location.
 *          @field identities {array} : @optional
 *              An array of queries the same size as `locations` containing entities to use in disambiguating
 *              `locations`.
 *              @autocomplete `identities`
 *          @field transform {boolean} : @optional
 *              Whether to change the origin position with `translationX`, `translationY`, `translationZ`, `rotationType`, and `rotation`.
 *              Defaults to `false`.
 *          @field absoluteToWorld {boolean} : @optional
 *                  If `true`, transforms are applied relative to the world. If `false`, they are applied relative to `locations`.
 *                  Defaults to `false`.
 *          @field translationX {ValueWithUnits} : @requiredIf {`transform` is `true`}
 *                  Distance to move the resulting origin along the world X direction.
 *          @field translationY {ValueWithUnits} : @requiredIf {`transform` is `true`}
 *                  Distance to move the resulting origin along the world Y direction.
 *          @field translationZ {ValueWithUnits} : @requiredIf {`transform` is `true`}
 *                  Distance to move the resulting origin along the world Z direction.
 *          @field rotationType {RotationType} : @optional
 *                  The axis to rotate around. Does not change the origin position.
 *                  Defaults to `RotationType.ABOUT_Z`.
 *          @field rotation {ValueWithUnits} : @optional
 *                  The angle to rotate by.
 *                  Defaults to `0 * degree`.
 *          @field primaryAxis {PrimaryAxis} : @optional
 *                  The primary world axis to use when orienting derived parts.
 *                  Defaults to `PrimaryAxis.Z`.
 *                  @autocomplete `PrimaryAxis.Z`
 *          @field flipPrimaryAxis {boolean} : @optional
 *                  Whether to flip the primary axis of derived parts.
 *                  Defaults to `false`.
 *                  @autocomplete `false`
 *          @field secondaryAxisType {MateConnectorAxisType} : @optional
 *                  The secondary axis to use when orienting derived parts.
 *                  Defaults to `MateConnectorAxisType.PLUS_X`.
 *                  @autocomplete `MateConnectorAxisType.PLUS_X`
 * }}
 *
 * @returns {{
 *      @field points {array} :
 *              An array of 3D `Vector`s representing the final location of each mate connector in the derived entities,
 *              or `[]` if no such mate connectors exist (and no point manipulator should be created).
 *      @field index {number} :
 *              The index of the currently selected mate connector. May be different from the passed in `index` due
 *              to the need to clamp the index to the actual number of mate connectors attatched to derived entities.
 *      @field firstLocation {CoordSystem} :
 *              A `CoordSystem` representing the final position of the first coordinate system.
 * }}
 */
export const opPointDerive = function(context is Context, id is Id, definition is map) returns map
    precondition
    {
        definition.importQuery is Query;
        definition.keepImports is boolean || definition.keepImports is undefined;
        definition.index is number;
        definition.locations is array;
        for (var location in definition.locations)
        {
            location is CoordSystem;
        }

        definition.identities is array || definition.identities is undefined;
        if (definition.identities != undefined)
        {
            size(definition.identities) == size(definition.locations);
            for (var query in definition.identities)
            {
                query is Query;
            }
        }

        definition.transform is boolean || definition.transform is undefined;
        definition.absolute is undefined || definition.absolute is boolean;
        if (definition.transform == true)
        {
            definition.translationX is ValueWithUnits;
            definition.translationY is ValueWithUnits;
            definition.translationZ is ValueWithUnits;
            definition.rotationType is RotationType || definition.rotationType is undefined;
            definition.rotation is ValueWithUnits || definition.rotation is undefined;
        }

        definition.primaryAxis is PrimaryAxis || definition.primaryAxis is undefined;
        definition.flipPrimaryAxis is boolean || definition.flipPrimaryAxis is undefined;
        definition.secondaryAxisType is MateConnectorAxisType || definition.secondaryAxisType is undefined;
    }
    {
        definition = {
                    "keepImports" : false,
                    "identities" : makeArray(size(definition.locations), qNothing()),
                    "transform" : false,
                    "absoluteToWorld" : false,
                    "rotationType" : RotationType.ABOUT_Z,
                    "rotation" : 0 * degree,
                    "primaryAxis" : PrimaryAxis.Z,
                    "flipPrimaryAxis" : false,
                    "secondaryAxisType" : MateConnectorAxisType.PLUS_X
                }->mergeMaps(definition);

        const mateConnectors = definition.importQuery->qMateConnectorsOfParts();

        var baseTransform = computeTransform(context, definition);

        if (definition.transform)
        {
            if (!definition.absoluteToWorld)
            {
                baseTransform = getUserTransform(definition) * baseTransform;
            }
            else
            {
                const baseOffset = [definition.translationX, definition.translationY, definition.translationZ]->vector();
                var rotation = identityTransform();
                if (!tolerantEquals(definition.rotation, 0 * degree))
                {
                    var rotationLine;
                    if (definition.rotationType == RotationType.ABOUT_Z)
                    {
                        rotationLine = Z_AXIS;
                    }
                    else if (definition.rotationType == RotationType.ABOUT_Y)
                    {
                        rotationLine = Y_AXIS;
                    }
                    else if (definition.rotationType == RotationType.ABOUT_X)
                    {
                        rotationLine = X_AXIS;
                    }
                    rotation = rotationAround(rotationLine, definition.rotation);
                }
                baseTransform = rotation * baseTransform;
                
                definition.locations = mapArray(definition.locations, function(location)
                    {
                        location.origin += baseOffset;
                        return location;
                    });
            }
        }

        var pointTransform = identityTransform();
        var pointArray = [];
        if (!isQueryEmpty(context, mateConnectors))
        {
            pointArray = computeCSysArray(context, mateConnectors);

            definition.index = clamp(definition.index, 0, size(pointArray) - 1);

            // transform from pointArray point to center of pointArray
            pointTransform = fromWorld(pointArray[definition.index]);

            pointArray = mapArray(pointArray, function(cSys)
                {
                    return toWorld(definition.locations[0]) * baseTransform * pointTransform * cSys.origin;
                });
        }

        for (var i = 0; i < size(definition.locations); i += 1)
        {
            setExternalDisambiguation(context, id + "pattern" + unstableIdComponent(i), definition.identities[i]);
            opPattern(context, id + "pattern" + unstableIdComponent(i), {
                        "entities" : definition.importQuery,
                        "transforms" : [toWorld(definition.locations[i]) * baseTransform * pointTransform],
                        "instanceNames" : ["pattern"]
                    });
        }

        if (!definition.keepImports)
        {
            opDeleteBodies(context, id + "deleteImport", { "entities" : definition.importQuery });
        }

        return { "points" : pointArray, "index" : definition.index, "firstLocation" : definition.locations[0] };
    };

/**
 * Converts a query for mate connectors into an array of coordinate systems.
 */
function computeCSysArray(context is Context, mateConnectors is Query) returns array
{
    return mapArray(evaluateQuery(context, mateConnectors), function(mateConnector)
        {
            return evMateConnector(context, { "mateConnector" : mateConnector });
        });
}

/**
 * Computes the transform of the `primaryAxis` and `secondaryAxisType` options.
 */
function computeTransform(context is Context, definition is map) returns Transform
{
    var planeToUse;
    if (definition.primaryAxis == PrimaryAxis.Z)
    {
        planeToUse = XY_PLANE;
    }
    else if (definition.primaryAxis == PrimaryAxis.Y)
    {
        planeToUse = XZ_PLANE;
    }
    else if (definition.primaryAxis == PrimaryAxis.X)
    {
        planeToUse = YZ_PLANE;
    }

    var zAxis = WORLD_COORD_SYSTEM.zAxis;
    var xAxis = WORLD_COORD_SYSTEM.xAxis;

    // code based on the Onshape STD transform feature
    if (definition.secondaryAxisType != undefined)
    {
        if (definition.secondaryAxisType == MateConnectorAxisType.PLUS_Y)
        {
            xAxis = cross(zAxis, xAxis);
        }
        else if (definition.secondaryAxisType == MateConnectorAxisType.MINUS_X)
        {
            xAxis = -xAxis;
        }
        else if (definition.secondaryAxisType == MateConnectorAxisType.MINUS_Y)
        {
            xAxis = -cross(zAxis, xAxis);
        }
    }

    zAxis *= definition.flipPrimaryAxis ? -1 : 1;

    return toWorld(planeToCSys(planeToUse)) * toWorld(coordSystem(WORLD_ORIGIN, xAxis, zAxis));
}

function getUserTransform(definition is map) returns Transform
precondition
{
    definition.transform;
}
{
    const base = [definition.translationX, definition.translationY, definition.translationZ]->vector();
    var rotation = identityTransform();

    if (!tolerantEquals(definition.rotation, 0 * degree))
    {
        var rotationLine;
        if (definition.rotationType == RotationType.ABOUT_Z)
        {
            rotationLine = Z_AXIS;
        }
        else if (definition.rotationType == RotationType.ABOUT_Y)
        {
            rotationLine = Y_AXIS;
        }
        else if (definition.rotationType == RotationType.ABOUT_X)
        {
            rotationLine = X_AXIS;
        }
        rotation = rotationAround(rotationLine, definition.rotation);
    }
    return transform(base) * rotation;
}

/**
 * A constructor for `PartStudioData`. Can be used to convert a FeatureScript imported part studio into
 * `PartStudioData` which can be passed into custom features as the value of a Part Studio reference parameter.
 *
 * @eg ```
 * MyStudio::import(path : ..., version : ...); // top level namespace import
 * const partStudioData = partStudioData(MyStudio::build, { "length" : 0.5 * meter }); // function call
 * ```
 * @param buildFunction {function} : @autocomplete `MyStudio::build`
 *          The build function of an imported part studio.
 *          @ex `MyStudio::build` to import a part studio imported under the `MyStudio` namespace
 * @param configuration {map} : @optional
 *          The configuration to generate the part studio with.
 * @param partQuery {Query} : @optional
 *          A `Query` for entities in the imported part studio which should be imported.
 *          Defaults to `qEverything(EntityType.BODY)`.
 *          @ex `qEverything(EntityType.BODY)->qBodyType(BodyType.SOLID)` to import only solid parts
 */
export function partStudioData(buildFunction is function, configuration is map, partQuery is Query) returns PartStudioData
{
    return { "buildFunction" : buildFunction, "configuration" : configuration, "partQuery" : partQuery } as PartStudioData;
}

/**
 * An overload for `partStudioData`.
 */
export function partStudioData(buildFunction is function, configuration is map) returns PartStudioData
{
    return partStudioData(buildFunction, configuration, qEverything(EntityType.BODY));
}

/**
 * An overload for `partStudioData`.
 */
export function partStudioData(buildFunction is function, partQuery is Query) returns PartStudioData
{
    return partStudioData(buildFunction, {}, partQuery);
}

/**
 * An overload for `partStudioData`.
 */
export function partStudioData(buildFunction is function) returns PartStudioData
{
    return partStudioData(buildFunction, {}, qEverything(EntityType.BODY));
}

export const POINT_DERIVE_MANIPULATOR = "pointDeriveManipulator";

/**
 * Adds a point derive manipulator to the `context`.
 *
 * @param options {{
 *      @field manipulatorKey {string} :
 *              The key to use.
 *              @autocomplete `POINT_DERIVE_MANIPULATOR`
 *      @field points {array} :
 *              An array of points to add. If points equals `[]`, the point manipulator is skipped.
 *              @eg `pointDeriveResult.points`
 *      @field index {number} :
 *              The index of the currently selected point.
 *              @eg `pointDeriveResult.index`
 * }}
 */
export function addPointDeriveManipulator(context is Context, id is Id, definition is map, options is map)
precondition
{
    options.manipulatorKey is string;
    options.points is array;
    for (var point in options.points)
    {
        is3dLengthVector(point);
    }
    options.index is number;
}
{
    if (options.points == [])
    {
        return;
    }
    addManipulators(context, id, {
                (options.manipulatorKey) : pointsManipulator(options)
            });
}
