FeatureScript 2945;
import(path : "onshape/std/common.fs", version : "2945.0");
import(path : "onshape/std/manipulator.fs", version : "2945.0");
import(path : "onshape/std/modifyFillet.fs", version : "2945.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "2945.0");
import(path : "onshape/std/splineUtils.fs", version : "2945.0");

// CADSharp branding / URL helper.
export import(path : "cbeb3dcf671e00785597bd76/144bf6a7fdc989e9e28ce5ea/a75ab01def146a42f55baa7f", version : "dc78e9b85c9f16ea9e131d3f");

icon::import(path : "80df7d9f4bbf673b129be122", version : "7ab3807e2158053b09dfe6b2");

export const DEFORM_SCALE_BOUNDS =
{
            (unitless) : [0.001, 1.0, 1000]
        } as RealBoundSpec;

export const DEFORM_FRACTION_BOUNDS =
{
            (unitless) : [0, 0.5, 1]
        } as RealBoundSpec;

export const DEFORM_GUIDE_INFLUENCE_BOUNDS =
{
            (unitless) : [0, 100.0, 100]
        } as RealBoundSpec;

export const DEFORM_SAMPLE_COUNT_BOUNDS =
{
            (unitless) : [3, 17, 200]
        } as IntegerBoundSpec;

export const DEFORM_POINT_QTY_BOUNDS =
{
            (unitless) : [2, 2, 200]
        } as IntegerBoundSpec;

export const DEFORM_EDGE_STEP_BOUNDS =
{
            (millimeter) : [0.05, 1.0, 1000],
            (inch) : 0.04
        } as LengthBoundSpec;

export const DEFORM_CENTER_OFFSET_BOUNDS =
{
            (millimeter) : [-1000000, 0, 1000000],
            (inch) : 0
        } as LengthBoundSpec;

export const DEFORM_SECTION_INDEX_BOUNDS =
{
            (unitless) : [0, 0, 10000]
        } as IntegerBoundSpec;

// Number of knot spans targeted per inter-section region in the path direction.
// A smoothStep blending between two adjacent cross-sections reaches its maximum
// curvature at roughly t=0.3 and t=0.7 of the inter-section span. Degree-3
// B-spline pieces need at least 4 spans per section interval (one per root of
// the second derivative of smoothStep, plus endpoints) to track the nonlinearity
// within acceptable tolerance. Increasing this gives higher accuracy at the cost
// of a larger control net.
const KNOTS_PER_SECTION_SPAN = 4;

// Minimum total path-direction spans regardless of section count (for deformations
// where a single section or no guide produces a simple uniform bend).
const MIN_PATH_KNOT_SPANS = 8;

const ATTR_TARGET_EDGES = "deformTargetEdges";
const ATTR_TARGET_FACES = "deformTargetFaces";
const ATTR_SOURCE_HOLE_FACES = "deformSourceHoleFaces";
const ATTR_SOURCE_HOLE_EDGES = "deformSourceHoleEdges";
const DEFAULT_POINT_QTY = 2;
const DEFAULT_EDGE_SAMPLE_STEP = 1.0 * millimeter;
const DEFAULT_GUIDE_SAMPLE_COUNT = 17;
const MAX_EDGE_SAMPLE_COUNT = 80;
const SURFACE_PARAMETER_EPSILON = 1e-6;

export enum DeformManipulatorMode
{
    Create,
    Insert,
    Delete
}

export enum DeformUiTab
{
    annotation { "Name" : "Inputs" }
    INPUTS,
    annotation { "Name" : "Transforms" }
    TRANSFORMS,
    annotation { "Name" : "Guides" }
    GUIDES,
    annotation { "Name" : "Settings" }
    SETTINGS
}

export enum DeformGuideInputMode
{
    annotation { "Name" : "Up to entities" }
    CROSS_SECTIONS,
    annotation { "Name" : "Boundary Volume" }
    BOUNDARY_VOLUME
}

export enum DeformType
{
    annotation { "Name" : "Rigid Profile" }
    RIGID_PROFILE,
    annotation { "Name" : "Lattice Cage" }
    LATTICE_CAGE
}

predicate deformCadsharpPredicate(definition is map)
{
    cadsharpUrlPredicate(definition);
}

annotation {
        "Feature Type Name" : "Deform",
        "Feature Type Description" : "Deforms solid, surface, and curve bodies along a path or onto a face with station-based cross-section rotation, scale, and guide fitting.",
        "UIHint" : "NO_PREVIEW_PROVIDED",
        "Manipulator Change Function" : "deformManipulatorChange",
        "Editing Logic Function" : "deformEditingLogic",
        "Icon" : icon::BLOB_DATA
    }
export const deform = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "New feature", "Default" : true, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.newFeature is boolean;

        annotation { "Name" : "Manipulator changed cross sections", "Default" : false, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.manipChangedCrossSections is boolean;

        annotation { "Name" : "Fillet re-apply explicitly enabled", "Default" : false, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.reapplyFilletsExplicit is boolean;

        annotation { "Name" : "Hole re-apply explicitly enabled", "Default" : false, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.reapplyHolesExplicit is boolean;

        annotation { "Name" : "Legacy pocket/protrusion exclusion enabled", "Default" : false, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.excludePocketsProtrusions is boolean;

        annotation { "Name" : "Legacy excluded pocket/protrusion faces", "Filter" : EntityType.FACE, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.excludedPocketProtrusionFaces is Query;

        annotation { "Name" : "Legacy auto start position", "Default" : true, "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.autoStartPosition is boolean;

        annotation { "Name" : "Menu", "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.uiTab is DeformUiTab;

        if (definition.uiTab == DeformUiTab.INPUTS)
        {
            annotation {
                        "Name" : "Entities",
                        "Description" : "Solid bodies, surface bodies, or wire/curve bodies to deform.",
                        "Filter" : EntityType.BODY && (BodyType.SOLID || BodyType.SHEET || BodyType.WIRE),
                        "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
                    }
            definition.entities is Query;

            annotation {
                        "Name" : "Path or Face",
                        "Description" : "Optional edge chain used as the target center path, or one face used as the target surface. If omitted, Deform uses a straight internal start/end path through the selected entities.",
                        "Filter" : ((EntityType.EDGE && ConstructionObject.NO && AllowMeshGeometry.NO) || (EntityType.FACE && ConstructionObject.NO && AllowMeshGeometry.NO)),
                        "MaxNumberOfPicks" : 100
                    }
            definition.path is Query;

            annotation {
                        "Name" : "Flip path/face direction",
                        "UIHint" : UIHint.OPPOSITE_DIRECTION
                    }
            definition.flipPath is boolean;

            annotation {
                        "Name" : "Stretch along path/face",
                        "Description" : "Scale the selected entities along the path or face so their full original length maps to the full target length.",
                        "Default" : false
                    }
            definition.stretchAlongPath is boolean;

            annotation {
                        "Name" : "Set start position",
                        "Description" : "Manually place the selected entities' start profile at a path or face parameter.",
                        "Default" : false
                    }
            definition.setStartPosition is boolean;

            annotation { "Group Name" : "Start position", "Driving Parameter" : "setStartPosition", "Collapsed By Default" : false }
            {

                if (definition.setStartPosition == true)
                {
                    annotation {
                                "Name" : "Start path parameter",
                                "Description" : "Manual path or face U parameter where the selected entities' start profile is placed.",
                                "Default" : 0.0
                            }
                    isReal(definition.startPathParameter, DEFORM_FRACTION_BOUNDS);
                }
            }

            annotation {
                        "Name" : "Bodies to transform (Optional)",
                        "Description" : "Bodies to reposition by the deformation field without rebuilding.",
                        "Filter" : EntityType.BODY && (BodyType.SOLID || BodyType.SHEET || BodyType.WIRE),
                        "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
                    }
            definition.rigidBodies is Query;

            annotation {
                        "Name" : "Pockets and protrusions",
                        "Description" : "Seed faces on pockets or protrusions to preserve by excluding the full feature island from the surface rebuild.",
                        "Filter" : EntityType.FACE && ConstructionObject.NO,
                        "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
                    }
            definition.pocketProtrusionSeedFaces is Query;

            annotation { "Group Name" : "Debug", "Collapsed By Default" : true }
            {
                annotation { "Name" : "Guide samples" }
                isInteger(definition.guideSampleCount, DEFORM_SAMPLE_COUNT_BOUNDS);

                annotation { "Name" : "Show samples" }
                definition.showSamples is boolean;
            }
        }

        if (definition.uiTab == DeformUiTab.TRANSFORMS)
        {
            annotation { "Name" : "Mode", "UIHint" : UIHint.HORIZONTAL_ENUM, "Description" : "Create edits transforms. Insert adds transforms between existing sections. Delete removes selected transforms." }
            definition.mode is DeformManipulatorMode;

            annotation {
                        "Name" : "Point qty",
                        "Description" : "Number of uniformly spaced transform points along the path.",
                        "Default" : 2
                    }
            isInteger(definition.pointQty, DEFORM_POINT_QTY_BOUNDS);

            annotation {
                        "Name" : "Transforms",
                        "Item name" : "Transform",
                        "Item label template" : "Point #index",
                        "UIHint" : UIHint.PREVENT_ARRAY_REORDER
                    }
            definition.crossSections is array;
            for (var section in definition.crossSections)
            {
                annotation { "Name" : "Point number", "Default" : 0, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isInteger(section.index, DEFORM_SECTION_INDEX_BOUNDS);

                annotation { "Name" : "Deformation type", "Default" : DeformType.RIGID_PROFILE, "UIHint" : [UIHint.HORIZONTAL_ENUM, UIHint.MATCH_LAST_ARRAY_ITEM] }
                section.deformType is DeformType;

                annotation {
                            "Name" : "Path/face fraction",
                            "Description" : "Station along the path or target face U direction, where 0 is the start and 1 is the end."
                        }
                isReal(section.station, DEFORM_FRACTION_BOUNDS);

                annotation { "Name" : "Rotation" }
                isAngle(section.rotation, ANGLE_360_ZERO_DEFAULT_BOUNDS);

                annotation { "Name" : "Scale" }
                isReal(section.scale, DEFORM_SCALE_BOUNDS);

                annotation {
                            "Name" : "Reference point",
                            "Description" : "Optional vertex or mate connector used as this cross section's manipulator zero point.",
                            "Filter" : QueryFilterCompound.ALLOWS_VERTEX || (EntityType.BODY && BodyType.MATE_CONNECTOR),
                            "MaxNumberOfPicks" : 1,
                            "UIHint" : [UIHint.MATCH_LAST_ARRAY_ITEM, UIHint.UNCONFIGURABLE]
                        }
                section.referenceVertex is Query;

                annotation { "Name" : "Reference initialized", "Default" : false, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                section.referenceInitialized is boolean;

                annotation { "Name" : "Reference X offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.referenceOffsetX, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Reference Y offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.referenceOffsetY, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Reference Z offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.referenceOffsetZ, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "dx", "Default" : 0 * meter, "UIHint" : UIHint.MATCH_LAST_ARRAY_ITEM }
                isLength(section.dx, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "dy", "Default" : 0 * meter, "UIHint" : UIHint.MATCH_LAST_ARRAY_ITEM }
                isLength(section.dy, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "dz", "Default" : 0 * meter, "UIHint" : UIHint.MATCH_LAST_ARRAY_ITEM }
                isLength(section.dz, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Stay on path", "Default" : false }
                section.stayOnPath is boolean;

                annotation { "Name" : "Center Y offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.centerOffsetY, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Center Z offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.centerOffsetZ, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 1 X offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner0OffsetX, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 1 Y offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner0OffsetY, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 1 Z offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner0OffsetZ, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 2 X offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner1OffsetX, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 2 Y offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner1OffsetY, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 2 Z offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner1OffsetZ, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 3 X offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner2OffsetX, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 3 Y offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner2OffsetY, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 3 Z offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner2OffsetZ, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 4 X offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner3OffsetX, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 4 Y offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner3OffsetY, DEFORM_CENTER_OFFSET_BOUNDS);

                annotation { "Name" : "Cage corner 4 Z offset", "Default" : 0 * meter, "UIHint" : [UIHint.ALWAYS_HIDDEN, UIHint.MATCH_LAST_ARRAY_ITEM] }
                isLength(section.cageCorner3OffsetZ, DEFORM_CENTER_OFFSET_BOUNDS);
            }
        }

        if (definition.uiTab == DeformUiTab.GUIDES)
        {
            annotation { "Name" : "Guide type", "UIHint" : UIHint.HORIZONTAL_ENUM }
            definition.guideMode is DeformGuideInputMode;

            if (definition.guideMode == DeformGuideInputMode.CROSS_SECTIONS)
            {
                annotation {
                            "Name" : "Guides",
                            "Item name" : "Guide",
                            "Item label template" : "Influence #influence%"
                        }
                definition.guides is array;
                for (var guide in definition.guides)
                {
                    annotation {
                                "Name" : "Guide geometry",
                                "Description" : "Guide edges, faces, surface bodies, solid bodies, or wire bodies used as a radial envelope before cross-section edits are applied.",
                                "Filter" : (EntityType.EDGE && ConstructionObject.NO && AllowMeshGeometry.NO) || EntityType.FACE || (EntityType.BODY && (BodyType.SOLID || BodyType.SHEET || BodyType.WIRE)),
                                "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE, UIHint.MATCH_LAST_ARRAY_ITEM]
                            }
                    guide.guideEntities is Query;

                    annotation { "Name" : "Influence", "Default" : 100.0, "UIHint" : UIHint.MATCH_LAST_ARRAY_ITEM }
                    isReal(guide.influence, DEFORM_GUIDE_INFLUENCE_BOUNDS);
                }
            }

            if (definition.guideMode == DeformGuideInputMode.BOUNDARY_VOLUME)
            {
                annotation {
                            "Name" : "Boundary volume",
                            "Description" : "Solid body whose faces define the deformation cage envelope.",
                            "Filter" : EntityType.BODY && BodyType.SOLID,
                            "MaxNumberOfPicks" : 1,
                            "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE
                        }
                definition.boundaryVolume is Query;
            }
        }

        if (definition.uiTab == DeformUiTab.SETTINGS)
        {
            annotation {
                        "Name" : "Re-apply holes",
                        "Description" : "Detect cylindrical hole faces, skip stretching those faces, then cut replacement holes after the outer body is rebuilt. This is slower on complex parts.",
                        "Default" : false
                    }
            definition.reapplyHoles is boolean;

            annotation {
                        "Name" : "Re-apply fillets",
                        "Description" : "Remove cylindrical fillets before deformation, then add them back to the deformed solid.",
                        "Default" : false
                    }
            definition.reapplyFillets is boolean;

            annotation {
                        "Name" : "Keep input bodies",
                        "Description" : "Keep the original input bodies instead of deleting them after the deformed bodies are created.",
                        "Default" : false
                    }
            definition.keepInputBodies is boolean;

            annotation { "Name" : "Create solids", "Default" : true }
            definition.keepSolid is boolean;

            annotation { "Name" : "Create surfaces", "Default" : false }
            definition.keepSurfaces is boolean;

            annotation { "Name" : "Create curves", "Default" : false }
            definition.keepCurves is boolean;

            annotation { "Name" : "Advanced debug logs", "Default" : false }
            definition.enableDebugLogs is boolean;
        }

        annotation { "Name" : "Active cross section", "UIHint" : UIHint.ALWAYS_HIDDEN }
        isInteger(definition.activeCrossSectionIndex, DEFORM_SECTION_INDEX_BOUNDS);

        deformCadsharpPredicate(definition);
    }
    {
        definition = normalizeDefinitionDefaults(definition);
        validateDeformInputs(context, definition);
        if (!(definition.crossSections is array) || size(definition.crossSections) == 0)
            definition.crossSections = defaultDeformCrossSectionsForDefinition(context, definition);
        definition = normalizeCrossSectionDefaults(definition);

        definition.pathData = buildPathData(context, definition);
        definition.sourceData = buildSourceData(context, definition, definition.pathData);
        definition.pathData = finalizePathData(definition.pathData, definition.sourceData);
        definition = localizeDefaultCrossSectionsForAnchoredMapping(definition);
        definition = applyCrossSectionReferenceVertices(context, definition, definition.pathData);
        definition = refreshDxDyDzFields(context, definition.pathData, definition);
        definition.guideProfile = buildGuideProfile(context, definition);
        showDeformProfilesIfEnabled(context, definition);
        updateTransformArrayLabels(context, id, definition);
        hideInactiveTransformParameters(context, id, definition);

        debugLog(definition, "Source radius=" ~ definition.sourceData.radius ~ ", source length=" ~ definition.sourceData.length ~ ", source stations=" ~ definition.sourceData.stationStart ~ " to " ~ definition.sourceData.stationEnd ~ ", cross sections=" ~ size(definition.crossSections));
        if (definition.enableDebugLogs == true)
            reportFeatureInfo(context, id, "Advanced Deform debug logs enabled. Failed rebuild boundaries and faces are highlighted red.");
        addDeformManipulators(context, id, definition);
        deformEntities(context, id, definition);
        deleteInputBodiesIfNeeded(context, id + "deleteInputs", definition);
        transformRigidBodies(context, id + "rigidBodies", definition);
    },
    {
            uiTab : DeformUiTab.INPUTS,
            flipPath : false,
            stretchAlongPath : false,
            autoStartPosition : true,
            setStartPosition : false,
            startPathParameter : 0.0,
            crossSections : [],
            guides : [],
            guideMode : DeformGuideInputMode.CROSS_SECTIONS,
            mode : DeformManipulatorMode.Create,
            pointQty : DEFAULT_POINT_QTY,
            reapplyHoles : false,
            reapplyHolesExplicit : false,
            reapplyFillets : false,
            reapplyFilletsExplicit : false,
            excludePocketsProtrusions : false,
            excludedPocketProtrusionFaces : qNothing(),
            pocketProtrusionSeedFaces : qNothing(),
            keepInputBodies : false,
            keepSolid : true,
            keepSurfaces : false,
            keepCurves : false,
            edgeSampleStep : DEFAULT_EDGE_SAMPLE_STEP,
            guideSampleCount : DEFAULT_GUIDE_SAMPLE_COUNT,
            showSamples : false,
            enableDebugLogs : false,
            activeCrossSectionIndex : 0,
            newFeature : true,
            manipChangedCrossSections : false
        });

function validateDeformInputs(context is Context, definition is map)
{
    if (definition.entities == undefined || evaluateQueryCount(context, definition.entities) == 0)
        throw regenError("Select entities to deform.", ["entities"]);
}

function debugLog(definition is map, message is string)
{
    if (definition.enableDebugLogs == true)
        println("DEFORM DEBUG: " ~ message);
}

function debugQueryCount(context is Context, definition is map, label is string, query is Query)
{
    if (definition.enableDebugLogs != true)
        return;

    var count = -1;
    try silent
    {
        count = evaluateQueryCount(context, query);
    }
    println("DEFORM DEBUG: " ~ label ~ " count=" ~ count);
}

function debugEntitiesIfEnabled(context is Context, definition is map, entities is Query, color)
{
    if (definition.enableDebugLogs == true)
        addDebugEntities(context, entities, color);
}

function showDeformProfilesIfEnabled(context is Context, definition is map)
{
    if (!(definition.crossSections is array) || size(definition.crossSections) == 0)
        return;

    for (var section in definition.crossSections)
    {
        drawBrightDebugClosedPolyline(context, deformedProfilePointsAtSection(context, definition, section), DebugColor.CYAN);
    }
}

function deformedProfilePointsAtSection(context is Context, definition is map, section is map) returns array
{
    const source = definition.sourceData;
    const station = clamp01(section.station);
    const sourceCenterOnAxis = source.origin + source.xAxis * sourceLocalXForStation(definition, station);
    const sourceCorners = [
            sourceCenterOnAxis + source.yAxis * source.minY + source.zAxis * source.minZ,
            sourceCenterOnAxis + source.yAxis * source.maxY + source.zAxis * source.minZ,
            sourceCenterOnAxis + source.yAxis * source.maxY + source.zAxis * source.maxZ,
            sourceCenterOnAxis + source.yAxis * source.minY + source.zAxis * source.maxZ
        ];

    var targetCorners = [];
    for (var point in sourceCorners)
    {
        targetCorners = append(targetCorners, deformPoint(context, definition, point));
    }
    return targetCorners;
}

function drawBrightDebugClosedPolyline(context is Context, points is array, color is DebugColor)
{
    if (size(points) < 2)
        return;

    for (var i = 0; i < size(points); i += 1)
    {
        const nextIndex = (i + 1) % size(points);
        addBrightDebugLine(context, points[i], points[nextIndex], color);
    }
}

function addBrightDebugLine(context is Context, startPoint is Vector, endPoint is Vector, color is DebugColor)
{
    addDebugLine(context, startPoint, endPoint, color);
    addDebugLine(context, startPoint, endPoint, color);
    addDebugLine(context, startPoint, endPoint, color);
}

function hideInactiveTransformParameters(context is Context, id is Id, definition is map)
{
    if (!(definition.crossSections is array) || size(definition.crossSections) == 0)
        return;

    const activeIndex = clampSectionIndex(definition.activeCrossSectionIndex, definition.crossSections);
    var hiddenIds = [];
    for (var i = 0; i < size(definition.crossSections); i += 1)
    {
        if (i != activeIndex)
            hiddenIds = append(hiddenIds, "crossSections[" ~ toString(i) ~ "]");
    }
    setFeatureHiddenParameters(context, id, hiddenIds);
}

function updateTransformArrayLabels(context is Context, id is Id, definition is map)
{
    if (!(definition.crossSections is array))
        return;

    for (var i = 0; i < size(definition.crossSections); i += 1)
    {
        setFeatureComputedParameter(context, id, {
                    "name" : "crossSections[" ~ toString(i) ~ "].index",
                    "value" : i + 1
                });

    }
}

export function deformEditingLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, specifiedParameters is map, hiddenBodies is Query) returns map
{
    definition = removeStaleDeformParameters(definition);
    try silent
    {
        definition = cadsharpUrlFunctionForPreExistingEditLogic(oldDefinition, definition);
    }
    definition = removeStaleDeformParameters(definition);

    if (specifiedParameters.reapplyHoles)
        definition.reapplyHolesExplicit = definition.reapplyHoles == true;
    if (specifiedParameters.reapplyFillets)
        definition.reapplyFilletsExplicit = definition.reapplyFillets == true;
    if (specifiedParameters.setStartPosition)
        definition.autoStartPosition = true;
    definition = normalizeDefinitionDefaults(definition);

    if (isCreating && definition.newFeature)
    {
        definition.newFeature = false;
        definition.crossSections = defaultDeformCrossSectionsForDefinition(context, definition);
        definition.activeCrossSectionIndex = 0;
        definition = refreshDxDyDzFieldsForEditing(context, definition);
        return definition;
    }

    if (!(definition.crossSections is array) || size(definition.crossSections) == 0)
    {
        definition.crossSections = defaultDeformCrossSectionsForDefinition(context, definition);
        definition.activeCrossSectionIndex = 0;
        definition.manipChangedCrossSections = false;
        definition = refreshDxDyDzFieldsForEditing(context, definition);
        return definition;
    }

    definition.activeCrossSectionIndex = clampSectionIndex(definition.activeCrossSectionIndex, definition.crossSections);
    definition = normalizeCrossSectionDefaults(definition);
    definition = syncCrossSectionEditingModes(context, oldDefinition, definition);

    const pointQtyChanged = specifiedParameters.pointQty || (oldDefinition.pointQty != undefined && definition.pointQty != oldDefinition.pointQty);
    const startPositionChanged = specifiedParameters.setStartPosition || specifiedParameters.startPathParameter ||
        (oldDefinition.setStartPosition != undefined && definition.setStartPosition != oldDefinition.setStartPosition) ||
        (oldDefinition.startPathParameter != undefined && definition.startPathParameter != oldDefinition.startPathParameter);
    const sourceSpanChanged = specifiedParameters.entities || specifiedParameters.path || specifiedParameters.flipPath || specifiedParameters.stretchAlongPath || startPositionChanged;
    if (pointQtyChanged || sourceSpanChanged)
    {
        const stationRange = sourceSpanChanged ? sourceStationRangeForDefinition(context, definition) : crossSectionStationRange(definition.crossSections);
        definition.crossSections = resampleCrossSectionsUniformly(definition.crossSections, definition.pointQty, stationRange);
        definition = normalizeCrossSectionDefaults(definition);
        definition.activeCrossSectionIndex = clampSectionIndex(definition.activeCrossSectionIndex, definition.crossSections);
        definition = updateCrossSectionReferencesForEditing(context, definition);
        definition = refreshDxDyDzFieldsForEditing(context, definition);
        definition.manipChangedCrossSections = false;
        return definition;
    }

    definition = applyDxDyDzEditsForEditing(context, oldDefinition, definition);

    if (oldDefinition.crossSections is array)
    {
        if (size(definition.crossSections) > size(oldDefinition.crossSections))
        {
            definition.pointQty = size(definition.crossSections);
            if (!definition.manipChangedCrossSections)
                definition.activeCrossSectionIndex = size(definition.crossSections) - 1;
            definition = updateCrossSectionReferencesForEditing(context, definition);
            definition = refreshDxDyDzFieldsForEditing(context, definition);
            return definition;
        }

        if (size(definition.crossSections) < size(oldDefinition.crossSections))
        {
            definition.pointQty = size(definition.crossSections);
            definition.activeCrossSectionIndex = clampSectionIndex(definition.activeCrossSectionIndex, definition.crossSections);
            definition = updateCrossSectionReferencesForEditing(context, definition);
            definition = refreshDxDyDzFieldsForEditing(context, definition);
            return definition;
        }

        if (!definition.manipChangedCrossSections)
        {
            for (var i = 0; i < min(size(oldDefinition.crossSections), size(definition.crossSections)); i += 1)
            {
                if (oldDefinition.crossSections[i] != definition.crossSections[i])
                {
                    definition.activeCrossSectionIndex = i;
                    if (referenceSelectionChanged(oldDefinition.crossSections[i].referenceVertex, definition.crossSections[i].referenceVertex))
                    {
                        var section = definition.crossSections[i];
                        section.referenceInitialized = false;
                        section.referenceOffsetX = 0 * meter;
                        section.referenceOffsetY = 0 * meter;
                        section.referenceOffsetZ = 0 * meter;
                        definition.crossSections[i] = section;
                    }
                    break;
                }
            }
        }
    }

    definition = updateCrossSectionReferencesForEditing(context, definition);
    definition = refreshDxDyDzFieldsForEditing(context, definition);
    definition.manipChangedCrossSections = false;
    return definition;
}

function normalizeDefinitionDefaults(definition is map) returns map
{
    definition = removeStaleDeformParameters(definition);
    if (definition.mode == undefined)
        definition.mode = DeformManipulatorMode.Create;
    if (definition.reapplyHoles == undefined)
        definition.reapplyHoles = false;
    if (definition.reapplyHolesExplicit == undefined)
        definition.reapplyHolesExplicit = false;
    if (definition.reapplyHolesExplicit != true)
        definition.reapplyHoles = false;
    if (definition.reapplyFillets == undefined)
        definition.reapplyFillets = false;
    if (definition.reapplyFilletsExplicit == undefined)
        definition.reapplyFilletsExplicit = false;
    if (definition.reapplyFilletsExplicit != true)
        definition.reapplyFillets = false;
    if (definition.pocketProtrusionSeedFaces == undefined)
        definition.pocketProtrusionSeedFaces = qNothing();
    if (definition.keepInputBodies == undefined)
        definition.keepInputBodies = false;
    if (definition.keepSolid == undefined)
        definition.keepSolid = true;
    if (definition.keepSurfaces == undefined)
        definition.keepSurfaces = false;
    if (definition.keepCurves == undefined)
        definition.keepCurves = false;
    if (definition.stretchAlongPath == undefined)
        definition.stretchAlongPath = false;
    if (definition.setStartPosition == undefined)
        definition.setStartPosition = definition.autoStartPosition != undefined ? definition.autoStartPosition == false : false;
    else if (definition.autoStartPosition == false)
        definition.setStartPosition = true;
    definition.autoStartPosition = true;
    if (definition.startPathParameter == undefined || definition.setStartPosition != true)
        definition.startPathParameter = 0.0;
    else
        definition.startPathParameter = clamp01(definition.startPathParameter);
    if (definition.edgeSampleStep == undefined)
        definition.edgeSampleStep = DEFAULT_EDGE_SAMPLE_STEP;
    if (definition.guideSampleCount == undefined)
        definition.guideSampleCount = DEFAULT_GUIDE_SAMPLE_COUNT;
    if (definition.showSamples == undefined)
        definition.showSamples = false;
    if (definition.pointQty == undefined)
        definition.pointQty = inferredPointQty(definition);

    definition.pointQty = max(DEFAULT_POINT_QTY, floor(definition.pointQty));
    return normalizeGuideDefaults(definition);
}

function removeStaleDeformParameters(definition is map) returns map
{
    definition.excludePocketsProtrusions = false;
    definition.excludedPocketProtrusionFaces = qNothing();
    return definition;
}

function inferredPointQty(definition is map) returns number
{
    if (definition.crossSections is array && size(definition.crossSections) > 0)
        return max(DEFAULT_POINT_QTY, size(definition.crossSections));
    return DEFAULT_POINT_QTY;
}

function updateCrossSectionReferencesForEditing(context is Context, definition is map) returns map
{
    var pathData;
    var sourceData;
    try silent
    {
        pathData = buildPathData(context, definition);
        sourceData = buildSourceData(context, definition, pathData);
        pathData = finalizePathData(pathData, sourceData);
        definition = applyCrossSectionReferenceVertices(context, definition, pathData);
    }
    return definition;
}

function applyDxDyDzEditsForEditing(context is Context, oldDefinition is map, definition is map) returns map
{
    if (!(definition.crossSections is array) || !(oldDefinition.crossSections is array))
        return refreshDxDyDzFieldsForEditing(context, definition);

    var pathData;
    var sourceData;
    var hasPathData = false;
    try silent
    {
        pathData = buildPathData(context, definition);
        sourceData = buildSourceData(context, definition, pathData);
        pathData = finalizePathData(pathData, sourceData);
        hasPathData = true;
    }
    if (!hasPathData)
        return definition;

    var dxDyDzChanged = false;
    for (var i = 0; i < min(size(oldDefinition.crossSections), size(definition.crossSections)); i += 1)
    {
        if (!sectionDxDyDzChanged(oldDefinition.crossSections[i], definition.crossSections[i]))
            continue;

        definition.crossSections[i] = applyDxDyDzToSection(context, pathData, definition.crossSections[i]);
        dxDyDzChanged = true;
    }

    if (dxDyDzChanged)
        definition = applyCrossSectionReferenceVertices(context, definition, pathData);

    return refreshDxDyDzFields(context, pathData, definition);
}

function refreshDxDyDzFieldsForEditing(context is Context, definition is map) returns map
{
    var pathData;
    var sourceData;
    var hasPathData = false;
    try silent
    {
        pathData = buildPathData(context, definition);
        sourceData = buildSourceData(context, definition, pathData);
        pathData = finalizePathData(pathData, sourceData);
        hasPathData = true;
    }
    if (!hasPathData)
        return definition;

    return refreshDxDyDzFields(context, pathData, definition);
}

function refreshDxDyDzFields(context is Context, pathData is map, definition is map) returns map
{
    if (!(definition.crossSections is array))
        return definition;

    for (var i = 0; i < size(definition.crossSections); i += 1)
    {
        const offsets = sectionDxDyDzValues(context, pathData, definition.crossSections[i]);
        var section = definition.crossSections[i];
        section.dx = offsets.dx;
        section.dy = offsets.dy;
        section.dz = offsets.dz;
        definition.crossSections[i] = section;
    }
    return definition;
}

function sectionDxDyDzValues(context is Context, pathData is map, section is map) returns map
{
    if (hasSectionReference(context, section))
    {
        return {
                "dx" : getSectionReferenceOffsetX(section),
                "dy" : getSectionReferenceOffsetY(section),
                "dz" : getSectionReferenceOffsetZ(section)
            };
    }

    return {
            "dx" : pathData.length * clamp01(section.station),
            "dy" : getSectionCenterOffsetY(section),
            "dz" : getSectionCenterOffsetZ(section)
        };
}

function applyDxDyDzToSection(context is Context, pathData is map, section is map) returns map
{
    const dx = getSectionDx(section);
    const dy = getSectionDy(section);
    const dz = getSectionDz(section);

    if (hasSectionReference(context, section))
    {
        section.referenceOffsetX = dx;
        section.referenceOffsetY = dy;
        section.referenceOffsetZ = dz;
        section.referenceInitialized = true;
        return section;
    }

    section.station = clamp01(dx / pathData.length);
    section.centerOffsetY = dy;
    section.centerOffsetZ = dz;
    return section;
}

function sectionDxDyDzChanged(oldSection is map, section is map) returns boolean
{
    return getSectionDx(oldSection) != getSectionDx(section) ||
        getSectionDy(oldSection) != getSectionDy(section) ||
        getSectionDz(oldSection) != getSectionDz(section);
}

function referenceSelectionChanged(oldSelection, newSelection) returns boolean
{
    if (oldSelection == undefined || newSelection == undefined)
        return oldSelection != newSelection;
    return oldSelection.subqueries != newSelection.subqueries;
}

function defaultDeformCrossSectionsForDefinition(context is Context, definition is map) returns array
{
    return defaultDeformCrossSections(definition.pointQty, sourceStationRangeForDefinition(context, definition));
}

function sourceStationRangeForDefinition(context is Context, definition is map) returns map
{
    var stationRange = { "start" : 0.0, "end" : 1.0 };
    try silent
    {
        const pathData = buildPathData(context, definition);
        const sourceData = buildSourceData(context, definition, pathData);
        stationRange = { "start" : sourceData.stationStart, "end" : sourceData.stationEnd };
    }
    return stationRange;
}

function defaultDeformCrossSections(pointQty is number, stationRange is map) returns array
{
    var sections = [];
    const count = max(2, floor(pointQty));
    const startStation = stationRange.start;
    const endStation = stationRange.end;
    for (var i = 0; i < count; i += 1)
    {
        sections = append(sections, defaultCrossSectionAt(startStation + (endStation - startStation) * i / (count - 1)));
    }
    return sections;
}

function resampleCrossSectionsUniformly(sections is array, pointQty is number, stationRange is map) returns array
{
    const count = max(2, floor(pointQty));
    if (size(sections) == 0)
        return defaultDeformCrossSections(count, stationRange);

    var result = [];
    const startStation = stationRange.start;
    const endStation = stationRange.end;
    for (var i = 0; i < count; i += 1)
    {
        result = append(result, makeCrossSectionAtStation(sections, startStation + (endStation - startStation) * i / (count - 1)));
    }
    return result;
}

function crossSectionStationRange(sections is array) returns map
{
    var minStation = 0.0;
    var maxStation = 1.0;
    if (size(sections) > 0)
    {
        minStation = sections[0].station;
        maxStation = sections[0].station;
    }
    for (var section in sections)
    {
        minStation = min(minStation, section.station);
        maxStation = max(maxStation, section.station);
    }
    return validStationRange(minStation, maxStation);
}

function validStationRange(startStation is number, endStation is number) returns map
{
    var rangeStart = clamp01(startStation);
    var rangeEnd = clamp01(endStation);
    if (rangeEnd < rangeStart)
    {
        const temp = rangeStart;
        rangeStart = rangeEnd;
        rangeEnd = temp;
    }
    if (rangeEnd - rangeStart < 1e-6)
    {
        rangeStart = max(0, rangeStart - 0.5);
        rangeEnd = min(1, rangeEnd + 0.5);
    }
    return { "start" : rangeStart, "end" : rangeEnd };
}

function defaultCrossSectionAt(station is number) returns map
{
    return {
            "index" : 0,
            "station" : station,
            "rotation" : 0 * degree,
            "scale" : 1.0,
            "deformType" : DeformType.RIGID_PROFILE,
            "referenceVertex" : qNothing(),
            "referenceInitialized" : false,
            "referenceOffsetX" : 0 * meter,
            "referenceOffsetY" : 0 * meter,
            "referenceOffsetZ" : 0 * meter,
            "dx" : 0 * meter,
            "dy" : 0 * meter,
            "dz" : 0 * meter,
            "stayOnPath" : false,
            "centerOffsetY" : 0 * meter,
            "centerOffsetZ" : 0 * meter,
            "cageCorner0OffsetX" : 0 * meter,
            "cageCorner0OffsetY" : 0 * meter,
            "cageCorner0OffsetZ" : 0 * meter,
            "cageCorner1OffsetX" : 0 * meter,
            "cageCorner1OffsetY" : 0 * meter,
            "cageCorner1OffsetZ" : 0 * meter,
            "cageCorner2OffsetX" : 0 * meter,
            "cageCorner2OffsetY" : 0 * meter,
            "cageCorner2OffsetZ" : 0 * meter,
            "cageCorner3OffsetX" : 0 * meter,
            "cageCorner3OffsetY" : 0 * meter,
            "cageCorner3OffsetZ" : 0 * meter
        };
}

function localizeDefaultCrossSectionsForAnchoredMapping(definition is map) returns map
{
    if (definition.pathData.hasPath != true || definition.stretchAlongPath == true)
        return definition;
    if (!(definition.crossSections is array) || size(definition.crossSections) < 2)
        return definition;
    if (abs(definition.sourceData.stationStart) < 1e-6 && abs(definition.sourceData.stationEnd - 1) < 1e-6)
        return definition;
    if (!crossSectionsAreUntouchedFullRangeDefaults(definition.crossSections))
        return definition;

    definition.crossSections = defaultDeformCrossSections(size(definition.crossSections), {
                "start" : definition.sourceData.stationStart,
                "end" : definition.sourceData.stationEnd
            });
    return normalizeCrossSectionDefaults(definition);
}

function crossSectionsAreUntouchedFullRangeDefaults(sections is array) returns boolean
{
    const count = size(sections);
    if (count < 2)
        return false;

    for (var i = 0; i < count; i += 1)
    {
        const expectedStation = i / (count - 1);
        if (abs(sections[i].station - expectedStation) > 1e-6)
            return false;
        if (!crossSectionIsUntouchedDefault(sections[i]))
            return false;
    }
    return true;
}

function crossSectionIsUntouchedDefault(section is map) returns boolean
{
    if (getSectionDeformType(section) != DeformType.RIGID_PROFILE)
        return false;
    if (abs(section.rotation) > 1e-8 * degree)
        return false;
    if (abs(section.scale - 1.0) > 1e-8)
        return false;
    if (abs(getSectionCenterOffsetY(section)) > 1e-8 * meter || abs(getSectionCenterOffsetZ(section)) > 1e-8 * meter)
        return false;

    for (var cornerIndex = 0; cornerIndex < 4; cornerIndex += 1)
    {
        if (abs(getCageCornerOffsetX(section, cornerIndex)) > 1e-8 * meter ||
            abs(getCageCornerOffsetY(section, cornerIndex)) > 1e-8 * meter ||
            abs(getCageCornerOffsetZ(section, cornerIndex)) > 1e-8 * meter)
            return false;
    }
    return true;
}

function normalizeCrossSectionDefaults(definition is map) returns map
{
    for (var i = 0; i < size(definition.crossSections); i += 1)
    {
        var section = definition.crossSections[i];
        section.index = i + 1;
        if (section.deformType == undefined)
            section.deformType = DeformType.RIGID_PROFILE;
        if (section.stayOnPath == undefined)
            section.stayOnPath = false;
        if (section.referenceVertex == undefined)
            section.referenceVertex = qNothing();
        if (section.referenceInitialized == undefined)
            section.referenceInitialized = false;
        if (section.referenceOffsetX == undefined)
            section.referenceOffsetX = 0 * meter;
        if (section.referenceOffsetY == undefined)
            section.referenceOffsetY = 0 * meter;
        if (section.referenceOffsetZ == undefined)
            section.referenceOffsetZ = 0 * meter;
        if (section.dx == undefined)
            section.dx = 0 * meter;
        if (section.dy == undefined)
            section.dy = 0 * meter;
        if (section.dz == undefined)
            section.dz = 0 * meter;
        if (section.centerOffsetY == undefined)
            section.centerOffsetY = 0 * meter;
        if (section.centerOffsetZ == undefined)
            section.centerOffsetZ = 0 * meter;
        section = normalizeCageDefaults(section);
        definition.crossSections[i] = section;
    }
    return definition;
}

function normalizeGuideDefaults(definition is map) returns map
{
    if (definition.uiTab == undefined)
        definition.uiTab = DeformUiTab.INPUTS;
    if (definition.guideMode == undefined)
        definition.guideMode = DeformGuideInputMode.CROSS_SECTIONS;
    if (definition.boundaryVolume == undefined)
        definition.boundaryVolume = qNothing();

    if (!(definition.guides is array))
    {
        if (definition.guides is Query)
            definition.guides = [{ "guideEntities" : definition.guides, "influence" : 100.0 }];
        else
            definition.guides = [];
    }

    for (var i = 0; i < size(definition.guides); i += 1)
    {
        var guide = definition.guides[i];
        if (guide.guideEntities == undefined && guide.entities != undefined)
            guide.guideEntities = guide.entities;
        if (guide.guideEntities == undefined)
            guide.guideEntities = qNothing();
        if (guide.influence == undefined)
            guide.influence = 100.0;
        definition.guides[i] = guide;
    }
    return definition;
}

function normalizeCageDefaults(section is map) returns map
{
    if (section.cageCorner0OffsetX == undefined)
        section.cageCorner0OffsetX = 0 * meter;
    if (section.cageCorner0OffsetY == undefined)
        section.cageCorner0OffsetY = 0 * meter;
    if (section.cageCorner0OffsetZ == undefined)
        section.cageCorner0OffsetZ = 0 * meter;
    if (section.cageCorner1OffsetX == undefined)
        section.cageCorner1OffsetX = 0 * meter;
    if (section.cageCorner1OffsetY == undefined)
        section.cageCorner1OffsetY = 0 * meter;
    if (section.cageCorner1OffsetZ == undefined)
        section.cageCorner1OffsetZ = 0 * meter;
    if (section.cageCorner2OffsetX == undefined)
        section.cageCorner2OffsetX = 0 * meter;
    if (section.cageCorner2OffsetY == undefined)
        section.cageCorner2OffsetY = 0 * meter;
    if (section.cageCorner2OffsetZ == undefined)
        section.cageCorner2OffsetZ = 0 * meter;
    if (section.cageCorner3OffsetX == undefined)
        section.cageCorner3OffsetX = 0 * meter;
    if (section.cageCorner3OffsetY == undefined)
        section.cageCorner3OffsetY = 0 * meter;
    if (section.cageCorner3OffsetZ == undefined)
        section.cageCorner3OffsetZ = 0 * meter;
    return section;
}

function syncCrossSectionEditingModes(context is Context, oldDefinition is map, definition is map) returns map
{
    if (!(oldDefinition.crossSections is array) || !(definition.crossSections is array))
        return definition;

    var pathData;
    var sourceData;
    var hasSourceData = false;
    try silent
    {
        pathData = buildPathData(context, definition);
        sourceData = buildSourceData(context, definition, pathData);
        pathData = finalizePathData(pathData, sourceData);
        hasSourceData = true;
    }

    for (var i = 0; i < min(size(oldDefinition.crossSections), size(definition.crossSections)); i += 1)
    {
        const oldSection = oldDefinition.crossSections[i];
        var section = definition.crossSections[i];

        const oldType = getSectionDeformType(oldSection);
        const newType = getSectionDeformType(section);
        if (oldType != newType)
        {
            if (newType == DeformType.LATTICE_CAGE)
            {
                section = resetCageOffsets(section);
            }
            else if (hasSourceData)
            {
                section = fitProfileSectionFromCage(context, pathData, sourceData, section);
                section = resetCageOffsets(section);
            }
        }

        definition.crossSections[i] = section;
    }

    return definition;
}

function fitProfileSectionFromCage(context is Context, pathData is map, sourceData is map, section is map) returns map
{
    const frame = sectionFrameAt(context, pathData, section);
    const p0 = sectionCurrentCageCornerPoint(context, pathData, sourceData, section, 0);
    const p1 = sectionCurrentCageCornerPoint(context, pathData, sourceData, section, 1);
    const p2 = sectionCurrentCageCornerPoint(context, pathData, sourceData, section, 2);
    const p3 = sectionCurrentCageCornerPoint(context, pathData, sourceData, section, 3);
    const center = (p0 + p1 + p2 + p3) / 4;
    const yVector = ((p1 + p2) / 2) - ((p0 + p3) / 2);
    const zVector = ((p2 + p3) / 2) - ((p0 + p1) / 2);

    var scale = section.scale;
    const sourceYSpan = sourceData.maxY - sourceData.minY;
    const sourceZSpan = sourceData.maxZ - sourceData.minZ;
    var scaleSamples = [];
    if (abs(sourceYSpan) > 1e-8 * meter && norm(yVector) > 1e-8 * meter)
        scaleSamples = append(scaleSamples, norm(yVector) / abs(sourceYSpan));
    if (abs(sourceZSpan) > 1e-8 * meter && norm(zVector) > 1e-8 * meter)
        scaleSamples = append(scaleSamples, norm(zVector) / abs(sourceZSpan));
    if (size(scaleSamples) == 1)
        scale = scaleSamples[0];
    else if (size(scaleSamples) > 1)
        scale = (scaleSamples[0] + scaleSamples[1]) / 2;

    section.scale = max(0.001, scale);
    if (norm(yVector) > 1e-8 * meter)
        section.rotation = signedAngleAroundAxis(frame.yAxis, normalize(yVector), frame.xAxis);

    const centerDelta = center - frame.origin;
    section.centerOffsetY = dot(centerDelta, frame.yAxis);
    section.centerOffsetZ = dot(centerDelta, frame.zAxis);
    section.deformType = DeformType.RIGID_PROFILE;
    return section;
}

function resetCageOffsets(section is map) returns map
{
    section.cageCorner0OffsetX = 0 * meter;
    section.cageCorner0OffsetY = 0 * meter;
    section.cageCorner0OffsetZ = 0 * meter;
    section.cageCorner1OffsetX = 0 * meter;
    section.cageCorner1OffsetY = 0 * meter;
    section.cageCorner1OffsetZ = 0 * meter;
    section.cageCorner2OffsetX = 0 * meter;
    section.cageCorner2OffsetY = 0 * meter;
    section.cageCorner2OffsetZ = 0 * meter;
    section.cageCorner3OffsetX = 0 * meter;
    section.cageCorner3OffsetY = 0 * meter;
    section.cageCorner3OffsetZ = 0 * meter;
    return section;
}

function getInsertCrossSectionCandidates(context is Context, pathData is map, sections is array) returns array
{
    var candidates = [];
    const ordered = sortCrossSectionsByStation(sections);

    if (size(ordered) == 0)
        return candidates;

    if (size(ordered) == 1)
    {
        const station = ordered[0].station <= 0.5 ? (ordered[0].station + 1) / 2 : ordered[0].station / 2;
        return [makeCrossSectionAtStation(sections, station)];
    }

    for (var i = 0; i < size(ordered) - 1; i += 1)
    {
        const a = ordered[i];
        const b = ordered[i + 1];
        if (b.station - a.station > 1e-6)
        {
            const station = (a.station + b.station) / 2;
            candidates = append(candidates, makeCrossSectionAtStation(sections, station));
        }
    }

    return candidates;
}

function makeCrossSectionAtStation(sections is array, station is number) returns map
{
    const state = crossSectionAt(sections, station);
    return {
            "index" : 0,
            "station" : station,
            "rotation" : state.rotation,
            "scale" : state.scale,
            "deformType" : state.deformType,
            "referenceVertex" : qNothing(),
            "referenceInitialized" : false,
            "referenceOffsetX" : 0 * meter,
            "referenceOffsetY" : 0 * meter,
            "referenceOffsetZ" : 0 * meter,
            "dx" : 0 * meter,
            "dy" : state.centerOffsetY,
            "dz" : state.centerOffsetZ,
            "stayOnPath" : false,
            "centerOffsetY" : state.centerOffsetY,
            "centerOffsetZ" : state.centerOffsetZ,
            "cageCorner0OffsetX" : state.cageCorner0OffsetX,
            "cageCorner0OffsetY" : state.cageCorner0OffsetY,
            "cageCorner0OffsetZ" : state.cageCorner0OffsetZ,
            "cageCorner1OffsetX" : state.cageCorner1OffsetX,
            "cageCorner1OffsetY" : state.cageCorner1OffsetY,
            "cageCorner1OffsetZ" : state.cageCorner1OffsetZ,
            "cageCorner2OffsetX" : state.cageCorner2OffsetX,
            "cageCorner2OffsetY" : state.cageCorner2OffsetY,
            "cageCorner2OffsetZ" : state.cageCorner2OffsetZ,
            "cageCorner3OffsetX" : state.cageCorner3OffsetX,
            "cageCorner3OffsetY" : state.cageCorner3OffsetY,
            "cageCorner3OffsetZ" : state.cageCorner3OffsetZ
        };
}

function insertCrossSectionSorted(sections is array, section is map) returns array
{
    return sortCrossSectionsByStation(append(sections, section));
}

function removeCrossSectionAt(sections is array, index is number) returns array
{
    var result = [];
    for (var i = 0; i < size(sections); i += 1)
    {
        if (i != index)
            result = append(result, sections[i]);
    }
    return result;
}

function sortCrossSectionsByStation(sections is array) returns array
{
    return sort(sections, function(a, b)
        {
            return a.station - b.station;
        });
}

function findMatchingCrossSectionIndex(sections is array, station is number) returns number
{
    for (var i = 0; i < size(sections); i += 1)
    {
        if (abs(sections[i].station - station) < 1e-8)
            return i;
    }
    return clampSectionIndex(0, sections);
}

function buildPathData(context is Context, definition is map) returns map
{
    var hasSelectedPath = false;
    try silent
    {
        hasSelectedPath = evaluateQueryCount(context, definition.path) > 0;
    }
    if (!hasSelectedPath)
    {
        return {
                "hasPath" : false,
                "length" : 1 * meter,
                "xAxis" : X_DIRECTION,
                "yAxis" : Y_DIRECTION,
                "zAxis" : Z_DIRECTION,
                "origin" : vector(0, 0, 0) * meter
            };
    }

    const selectedFaces = qEntityFilter(definition.path, EntityType.FACE);
    const selectedEdges = qEntityFilter(definition.path, EntityType.EDGE);
    const faceCount = evaluateQueryCount(context, selectedFaces);
    const edgeCount = evaluateQueryCount(context, selectedEdges);
    if (faceCount > 0)
    {
        if (faceCount != 1 || edgeCount > 0)
            throw regenError("Select either one target face or path edges, not both.", ["path"]);
        return buildSurfacePathData(context, definition, qNthElement(selectedFaces, 0));
    }

    var path = constructPath(context, definition.path);
    if (definition.flipPath)
        path = reverse(path);

    const pathLength = evPathLength(context, path);
    const startLine = evPathTangentLines(context, path, [0]).tangentLines[0];
    var zAxis = perpendicularVector(startLine.direction);

    try silent
    {
        const sketchPlane = evOwnerSketchPlane(context, { "entity" : definition.path });
        zAxis = sketchPlane.normal;
    }

    zAxis = safeNormalForDirection(zAxis, startLine.direction);
    const yAxis = normalize(cross(zAxis, startLine.direction));

    return {
            "hasPath" : true,
            "path" : path,
            "length" : pathLength,
            "xAxis" : startLine.direction,
            "yAxis" : yAxis,
            "zAxis" : zAxis,
            "origin" : startLine.origin
        };
}

function buildSurfacePathData(context is Context, definition is map, targetFace is Query) returns map
{
    const startU = definition.flipPath == true ? 1.0 : 0.0;
    const startFrame = surfaceFrameFromFaceParameter(context, targetFace, vector(startU, 0.5), definition.flipPath == true);
    const uLength = max(surfaceIsoLength(context, targetFace, true, 0.5), 1 * millimeter);
    const vLength = max(surfaceIsoLength(context, targetFace, false, 0.5), 1 * millimeter);
    var periodicity = [false, false];
    try silent
    {
        periodicity = evFacePeriodicity(context, { "face" : targetFace });
    }
    return {
            "hasPath" : true,
            "isSurface" : true,
            "face" : targetFace,
            "length" : 1 * meter,
            "xAxis" : startFrame.xAxis,
            "yAxis" : startFrame.yAxis,
            "zAxis" : startFrame.zAxis,
            "origin" : startFrame.origin,
            "uLength" : uLength,
            "vLength" : vLength,
            "flipSurfaceU" : definition.flipPath == true,
            "isUPeriodic" : periodicity[0],
            "isVPeriodic" : periodicity[1]
        };
}

function finalizePathData(pathData is map, sourceData is map) returns map
{
    if (pathData.isSurface == true)
    {
        pathData.length = sourceData.length;
        return pathData;
    }
    if (pathData.hasPath == true)
        return pathData;

    return {
            "hasPath" : false,
            "length" : sourceData.length,
            "xAxis" : sourceData.xAxis,
            "yAxis" : sourceData.yAxis,
            "zAxis" : sourceData.zAxis,
            "origin" : sourceData.origin
        };
}

function applyCrossSectionReferenceVertices(context is Context, definition is map, pathData is map) returns map
{
    for (var i = 0; i < size(definition.crossSections); i += 1)
    {
        var section = definition.crossSections[i];
        const referenceVertex = section.referenceVertex;
        if (referenceVertex == undefined)
            continue;
        if (evaluateQueryCount(context, referenceVertex) == 0)
            continue;

        const referencePoint = referencePointPosition(context, referenceVertex);
        const referenceStation = nearestPathParameter(context, pathData, referencePoint);
        const referenceFrame = pathFrameAt(context, pathData, referenceStation);

        if (section.referenceInitialized != true)
        {
            section.referenceOffsetX = 0 * meter;
            section.referenceOffsetY = 0 * meter;
            section.referenceOffsetZ = 0 * meter;
            section.referenceInitialized = true;
        }

        const offsetX = getSectionReferenceOffsetX(section);
        const offsetY = getSectionReferenceOffsetY(section);
        const offsetZ = getSectionReferenceOffsetZ(section);
        const sectionStation = clamp01(referenceStation + offsetX / pathData.length);
        const sectionFrame = pathFrameAt(context, pathData, sectionStation);
        const sectionCenter = referencePoint + referenceFrame.xAxis * offsetX + referenceFrame.yAxis * offsetY + referenceFrame.zAxis * offsetZ;
        const centerDelta = sectionCenter - sectionFrame.origin;

        section.station = sectionStation;
        if (section.stayOnPath == true)
        {
            section.centerOffsetY = 0 * meter;
            section.centerOffsetZ = 0 * meter;
        }
        else
        {
            section.centerOffsetY = dot(centerDelta, sectionFrame.yAxis);
            section.centerOffsetZ = dot(centerDelta, sectionFrame.zAxis);
        }
        definition.crossSections[i] = section;
        debugLog(definition, "reference section " ~ i ~ " station=" ~ section.station ~ ", centerOffsetY=" ~ section.centerOffsetY ~ ", centerOffsetZ=" ~ section.centerOffsetZ);
    }
    return definition;
}

function referencePointPosition(context is Context, referenceQuery is Query) returns Vector
{
    var vertexPoint;
    var isVertexPoint = false;
    try silent
    {
        vertexPoint = evVertexPoint(context, {
                    "vertex" : referenceQuery
                });
        isVertexPoint = true;
    }
    if (isVertexPoint)
        return vertexPoint;

    var mateConnector;
    var isMateConnector = false;
    try silent
    {
        mateConnector = evMateConnector(context, {
                    "mateConnector" : referenceQuery
                });
        isMateConnector = true;
    }
    if (isMateConnector)
        return mateConnector.origin;

    throw regenError("The reference point could not be evaluated.", ["crossSections"]);
}

function nearestPathParameter(context is Context, pathData is map, point is Vector) returns number
{
    if (pathData.hasPath != true)
        return clamp01(dot(point - pathData.origin, pathData.xAxis) / pathData.length);
    if (pathData.isSurface == true)
        return nearestSurfaceUParameter(context, pathData, point);

    var bestParameter = 0.0;
    var bestDistance = inf * meter;
    const sampleCount = 101;
    for (var i = 0; i < sampleCount; i += 1)
    {
        const parameter = i / (sampleCount - 1);
        const frame = pathFrameAt(context, pathData, parameter);
        const distance = norm(point - frame.origin);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestParameter = parameter;
        }
    }

    var step = 1.0 / (sampleCount - 1);
    for (var refinement = 0; refinement < 8; refinement += 1)
    {
        for (var direction in [-1, 1])
        {
            const candidateParameter = clamp01(bestParameter + direction * step);
            const candidateFrame = pathFrameAt(context, pathData, candidateParameter);
            const candidateDistance = norm(point - candidateFrame.origin);
            if (candidateDistance < bestDistance)
            {
                bestDistance = candidateDistance;
                bestParameter = candidateParameter;
            }
        }
        step = step / 2;
    }
    return bestParameter;
}

function buildSourceData(context is Context, definition is map, pathData is map) returns map
{
    const bounds = evBox3d(context, {
                "topology" : definition.entities,
                "tight" : false
            });
    const corners = boxCorners(bounds);
    const basis = sourceBasisForDefinition(context, definition, pathData, sourceAnchorSamples(context, definition.entities, bounds));

    var minX = inf * meter;
    var maxX = -inf * meter;
    var minY = inf * meter;
    var maxY = -inf * meter;
    var minZ = inf * meter;
    var maxZ = -inf * meter;

    for (var corner in corners)
    {
        const offset = corner - basis.origin;
        const x = dot(offset, basis.xAxis);
        const y = dot(offset, basis.yAxis);
        const z = dot(offset, basis.zAxis);
        minX = min(minX, x);
        maxX = max(maxX, x);
        minY = min(minY, y);
        maxY = max(maxY, y);
        minZ = min(minZ, z);
        maxZ = max(maxZ, z);
    }

    const sourceSpan = maxX - minX;
    if (sourceSpan <= 1e-8 * meter)
        throw regenError("The selected entities have no usable length in the path start direction.", ["entities"]);

    var origin;
    var length;
    var stationStart = 0.0;
    var stationEnd = 1.0;
    const anchorSourceStart = basis.usesManualStartPosition == true && definition.stretchAlongPath != true;
    if (pathData.isSurface == true)
    {
        origin = (definition.stretchAlongPath == true || anchorSourceStart) ? basis.origin + basis.xAxis * minX : basis.origin;
        length = definition.stretchAlongPath == true ? sourceSpan : pathData.uLength;
    }
    else if (pathData.hasPath == true)
    {
        origin = (definition.stretchAlongPath == true || anchorSourceStart) ? basis.origin + basis.xAxis * minX : basis.origin;
        if (definition.stretchAlongPath == true)
        {
            length = sourceSpan;
        }
        else
        {
            length = pathData.length;
        }
    }
    else
    {
        const centerY = (minY + maxY) / 2;
        const centerZ = (minZ + maxZ) / 2;
        origin = basis.origin + basis.xAxis * minX + basis.yAxis * centerY + basis.zAxis * centerZ;
        length = sourceSpan;
    }

    if (length <= 1e-8 * meter)
        throw regenError("The selected entities have no usable length in the path start direction.", ["entities"]);

    var sourceRadius = 0 * meter;
    var sourceMinX = inf * meter;
    var sourceMaxX = -inf * meter;
    var sourceMinY = inf * meter;
    var sourceMaxY = -inf * meter;
    var sourceMinZ = inf * meter;
    var sourceMaxZ = -inf * meter;
    for (var corner in corners)
    {
        const local = worldToLocal(corner, origin, basis.xAxis, basis.yAxis, basis.zAxis);
        sourceMinX = min(sourceMinX, local[0]);
        sourceMaxX = max(sourceMaxX, local[0]);
        sourceMinY = min(sourceMinY, local[1]);
        sourceMaxY = max(sourceMaxY, local[1]);
        sourceMinZ = min(sourceMinZ, local[2]);
        sourceMaxZ = max(sourceMaxZ, local[2]);
        sourceRadius = max(sourceRadius, norm(basis.yAxis * local[1] + basis.zAxis * local[2]));
    }
    if (sourceRadius <= 1e-8 * meter)
        sourceRadius = 1 * millimeter;
    if (sourceMaxY - sourceMinY <= 1e-8 * meter)
    {
        sourceMinY = -sourceRadius;
        sourceMaxY = sourceRadius;
    }
    if (sourceMaxZ - sourceMinZ <= 1e-8 * meter)
    {
        sourceMinZ = -sourceRadius;
        sourceMaxZ = sourceRadius;
    }

    if (pathData.hasPath == true && definition.stretchAlongPath != true)
    {
        const stationRange = sourceTargetStationRange(pathData, basis, sourceMinX, sourceMaxX);
        stationStart = stationRange.start;
        stationEnd = stationRange.end;
    }

    return {
            "origin" : origin,
            "length" : length,
            "xAxis" : basis.xAxis,
            "yAxis" : basis.yAxis,
            "zAxis" : basis.zAxis,
            "minX" : sourceMinX,
            "maxX" : sourceMaxX,
            "anchorStation" : basis.anchorStation,
            "anchorV" : basis.anchorV,
            "stationStart" : stationStart,
            "stationEnd" : stationEnd,
            "radius" : sourceRadius,
            "minY" : sourceMinY,
            "maxY" : sourceMaxY,
            "minZ" : sourceMinZ,
            "maxZ" : sourceMaxZ
        };
}

function sourceBasisForDefinition(context is Context, definition is map, pathData is map, corners is array) returns map
{
    if (pathData.isSurface == true)
        return surfaceSourceBasisForCorners(context, definition, pathData, corners);
    if (pathData.hasPath == true)
        return pathSourceBasisForCorners(context, definition, pathData, corners);

    return {
            "origin" : pathData.origin,
            "xAxis" : pathData.xAxis,
            "yAxis" : pathData.yAxis,
            "zAxis" : pathData.zAxis,
            "anchorStation" : 0.0,
            "anchorV" : 0.5,
            "usesManualStartPosition" : false
        };
}

function sourceAnchorSamples(context is Context, entities is Query, bounds is Box3d) returns array
{
    var samples = append(boxCorners(bounds), boxCenter(bounds));

    const bodyQuery = qEntityFilter(entities, EntityType.BODY);
    const faceQuery = qUnion([qEntityFilter(entities, EntityType.FACE), qOwnedByBody(bodyQuery, EntityType.FACE)]);
    const edgeQuery = qUnion([
                qEntityFilter(entities, EntityType.EDGE),
                qOwnedByBody(bodyQuery, EntityType.EDGE),
                qAdjacent(faceQuery, AdjacencyType.EDGE, EntityType.EDGE)
            ]);

    var edgeSampleCount = 0;
    for (var edge in evaluateQuery(context, edgeQuery))
    {
        if (edgeSampleCount >= 80)
            break;
        try silent
        {
            samples = append(samples, evEdgeTangentLine(context, {
                                "edge" : edge,
                                "parameter" : 0.5
                            }).origin);
            edgeSampleCount += 1;
        }
    }

    var faceSampleCount = 0;
    for (var face in evaluateQuery(context, faceQuery))
    {
        if (faceSampleCount >= 40)
            break;
        try silent
        {
            samples = append(samples, evFaceTangentPlane(context, {
                                "face" : face,
                                "parameter" : vector(0.5, 0.5)
                            }).origin);
            faceSampleCount += 1;
        }
    }

    return samples;
}

function pathSourceBasisForCorners(context is Context, definition is map, pathData is map, corners is array) returns map
{
    if (definition.setStartPosition == true)
    {
        const manualStation = manualStartPathParameter(definition);
        const manualFrame = pathFrameAt(context, pathData, manualStation);
        return {
                "origin" : manualFrame.origin,
                "xAxis" : manualFrame.xAxis,
                "yAxis" : manualFrame.yAxis,
                "zAxis" : manualFrame.zAxis,
                "anchorStation" : manualStation,
                "anchorV" : 0.5,
                "usesManualStartPosition" : true
            };
    }

    var anchorStation = 0.0;
    var anchorPoint = pathData.origin;
    var bestDistance = inf * meter;
    for (var corner in corners)
    {
        const station = nearestPathParameter(context, pathData, corner);
        const frameAtStation = pathFrameAt(context, pathData, station);
        const distance = norm(corner - frameAtStation.origin);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            anchorStation = station;
            anchorPoint = corner;
        }
    }

    const frame = pathFrameAt(context, pathData, anchorStation);
    return {
            "origin" : anchorPoint,
            "xAxis" : frame.xAxis,
            "yAxis" : frame.yAxis,
            "zAxis" : frame.zAxis,
            "anchorStation" : anchorStation,
            "anchorV" : 0.5,
            "usesManualStartPosition" : false
        };
}

function surfaceSourceBasisForCorners(context is Context, definition is map, pathData is map, corners is array) returns map
{
    if (definition.setStartPosition == true)
    {
        const manualU = manualStartPathParameter(definition);
        const manualV = 0.5;
        const manualFrame = surfaceFrameAt(context, pathData, manualU, manualV);
        return {
                "origin" : manualFrame.origin,
                "xAxis" : manualFrame.xAxis,
                "yAxis" : manualFrame.yAxis,
                "zAxis" : manualFrame.zAxis,
                "anchorStation" : manualU,
                "anchorV" : manualV,
                "usesManualStartPosition" : true
            };
    }

    var anchorU = 0.0;
    var anchorV = 0.5;
    var anchorPoint = pathData.origin;
    var bestDistance = inf * meter;
    for (var corner in corners)
    {
        const parameter = projectedSurfaceParameter(context, pathData, corner);
        if (parameter == undefined)
            continue;

        const frameAtParameter = surfaceFrameAt(context, pathData, parameter[0], parameter[1]);
        const distance = norm(corner - frameAtParameter.origin);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            anchorU = parameter[0];
            anchorV = parameter[1];
            anchorPoint = corner;
        }
    }

    const frame = surfaceFrameAt(context, pathData, anchorU, anchorV);
    return {
            "origin" : anchorPoint,
            "xAxis" : frame.xAxis,
            "yAxis" : frame.yAxis,
            "zAxis" : frame.zAxis,
            "anchorStation" : anchorU,
            "anchorV" : anchorV,
            "usesManualStartPosition" : false
        };
}

function manualStartPathParameter(definition is map) returns number
{
    if (definition.setStartPosition != true || definition.startPathParameter == undefined)
        return 0.0;
    return clamp01(definition.startPathParameter);
}

function sourceTargetStationRange(pathData is map, basis is map, sourceMinX, sourceMaxX) returns map
{
    var stationScale = pathData.length;
    if (pathData.isSurface == true)
        stationScale = pathData.uLength;
    const startStation = basis.anchorStation + sourceMinX / stationScale;
    const endStation = basis.anchorStation + sourceMaxX / stationScale;
    return validStationRange(startStation, endStation);
}

function pathStationRangeForCorners(context is Context, pathData is map, corners is array) returns map
{
    if (pathData.hasPath != true || size(corners) == 0)
        return { "start" : 0.0, "end" : 1.0 };
    if (pathData.isSurface == true)
        return { "start" : 0.0, "end" : 1.0 };

    var minStation = 1.0;
    var maxStation = 0.0;
    for (var corner in corners)
    {
        const station = nearestPathParameter(context, pathData, corner);
        minStation = min(minStation, station);
        maxStation = max(maxStation, station);
    }
    return validStationRange(minStation, maxStation);
}

function buildGuideProfile(context is Context, definition is map) returns array
{
    const sampleCount = definition.guideSampleCount;
    var profile = makeArray(sampleCount, undefined);
    const pathFrames = samplePathFrames(context, definition.pathData, sampleCount);

    if (definition.guideMode == DeformGuideInputMode.BOUNDARY_VOLUME)
    {
        if (definition.boundaryVolume == undefined)
            return profile;

        var hasBoundaryVolume = false;
        try silent
        {
            hasBoundaryVolume = evaluateQueryCount(context, definition.boundaryVolume) > 0;
        }
        if (!hasBoundaryVolume)
            return profile;

        return mergeGuideProfiles(profile, buildGuideProfileFromEntities(context, definition, definition.boundaryVolume, 1.0, pathFrames, "boundary volume", false));
    }

    if (!(definition.guides is array) || size(definition.guides) == 0)
        return profile;

    for (var guideIndex = 0; guideIndex < size(definition.guides); guideIndex += 1)
    {
        const guide = definition.guides[guideIndex];
        if (guide.guideEntities == undefined)
            continue;

        const influence = clamp01((guide.influence == undefined ? 100.0 : guide.influence) / 100);
        if (influence <= 0)
            continue;

        var hasGuideGeometry = false;
        try silent
        {
            hasGuideGeometry = evaluateQueryCount(context, guide.guideEntities) > 0;
        }
        if (!hasGuideGeometry)
            continue;

        profile = mergeGuideProfiles(profile, buildGuideProfileFromEntities(context, definition, guide.guideEntities, influence, pathFrames, "guide " ~ guideIndex, true));
    }

    return profile;
}

function buildGuideProfileFromEntities(context is Context, definition is map, guideEntities is Query, influence is number, pathFrames is array, label is string, useClosestSamples is boolean) returns array
{
    const sampleCount = size(pathFrames);
    var rawProfile = makeArray(sampleCount, undefined);
    const guideEdges = guideEdgesForSampling(guideEntities);
    const guideFaces = guideFacesForSampling(guideEntities);
    debugQueryCount(context, definition, label ~ " sample edges", guideEdges);
    debugQueryCount(context, definition, label ~ " sample faces", guideFaces);

    for (var guideEdge in evaluateQuery(context, guideEdges))
    {
        const guideLength = evLength(context, { "entities" : guideEdge });
        const edgeSampleCount = edgeSampleCountForLength(guideLength, definition.edgeSampleStep);
        const parameters = range(0, 1, edgeSampleCount);
        const tangents = evEdgeTangentLines(context, {
                    "edge" : guideEdge,
                    "parameters" : parameters,
                    "arcLengthParameterization" : true
                });

        for (var tangent in tangents)
        {
            rawProfile = addGuideSampleToProfileForMode(rawProfile, tangent.origin, pathFrames, definition, !useClosestSamples);
        }
    }

    rawProfile = addGuideFaceSamplesToProfile(context, definition, rawProfile, guideFaces, pathFrames, !useClosestSamples);

    var profile = makeArray(sampleCount, undefined);
    for (var profileIndex = 0; profileIndex < sampleCount; profileIndex += 1)
    {
        if (rawProfile[profileIndex] == undefined)
            continue;

        const station = profileIndex / max(1, sampleCount - 1);
        const baseEnvelope = sectionGuideEnvelope(definition.sourceData, crossSectionAt(definition.crossSections, station), pathFrames[profileIndex]);
        if (useClosestSamples)
            profile[profileIndex] = guideOffsetWithInfluence(baseEnvelope, rawProfile[profileIndex], influence);
        else
            profile[profileIndex] = guideEnvelopeWithInfluence(baseEnvelope, rawProfile[profileIndex], influence);
    }
    if (useClosestSamples)
        profile = smoothGuideOffsetProfile(profile);
    else
        profile = smoothGuideEnvelopeProfile(profile);
    return profile;
}

function mergeGuideProfiles(baseProfile is array, candidateProfile is array) returns array
{
    for (var i = 0; i < min(size(baseProfile), size(candidateProfile)); i += 1)
    {
        if (candidateProfile[i] == undefined)
            continue;
        if (baseProfile[i] == undefined)
        {
            baseProfile[i] = candidateProfile[i];
        }
        else
        {
            var envelope = baseProfile[i];
            envelope.minY = min(envelope.minY, candidateProfile[i].minY);
            envelope.maxY = max(envelope.maxY, candidateProfile[i].maxY);
            envelope.minZ = min(envelope.minZ, candidateProfile[i].minZ);
            envelope.maxZ = max(envelope.maxZ, candidateProfile[i].maxZ);
            if (candidateProfile[i].samples is array)
                envelope.samples = appendGuideSamples(envelope.samples, candidateProfile[i].samples);
            if (candidateProfile[i].hasGuideOffset == true)
            {
                if (envelope.hasGuideOffset == true)
                {
                    envelope.offsetY = (envelope.offsetY + candidateProfile[i].offsetY) / 2;
                    envelope.offsetZ = (envelope.offsetZ + candidateProfile[i].offsetZ) / 2;
                }
                else
                {
                    envelope.hasGuideOffset = true;
                    envelope.offsetY = candidateProfile[i].offsetY;
                    envelope.offsetZ = candidateProfile[i].offsetZ;
                }
            }
            baseProfile[i] = envelope;
        }
    }
    return baseProfile;
}

function addGuideSampleToProfile(profile is array, point is Vector, pathFrames is array, definition is map) returns array
{
    const nearestIndex = nearestPathFrameIndex(point, pathFrames);
    return addGuideSampleToProfileAtIndex(profile, point, pathFrames, definition, nearestIndex, 1.0, true);
}

function addGuideSampleToProfileForMode(profile is array, point is Vector, pathFrames is array, definition is map, spreadAcrossStations is boolean) returns array
{
    if (spreadAcrossStations != true)
        return addGuideSampleToProfile(profile, point, pathFrames, definition);

    const nearestIndex = nearestPathFrameIndex(point, pathFrames);
    const spreadRadius = max(2, ceil(size(pathFrames) / 8));
    const startIndex = max(0, nearestIndex - spreadRadius);
    const endIndex = min(size(pathFrames) - 1, nearestIndex + spreadRadius);
    for (var profileIndex = startIndex; profileIndex <= endIndex; profileIndex += 1)
    {
        const distance = abs(profileIndex - nearestIndex);
        const weight = smoothStep(1 - distance / (spreadRadius + 1));
        profile = addGuideSampleToProfileAtIndex(profile, point, pathFrames, definition, profileIndex, weight, false);
    }
    return profile;
}

function addGuideSampleToProfileAtIndex(profile is array, point is Vector, pathFrames is array, definition is map, profileIndex is number, influence is number, includeBaseEnvelope is boolean) returns array
{
    const nearestIndex = floor(max(0, min(size(pathFrames) - 1, profileIndex)));
    const frame = pathFrames[nearestIndex];
    const station = nearestIndex / max(1, size(pathFrames) - 1);
    const section = crossSectionAt(definition.crossSections, station);
    const baseEnvelope = sectionGuideEnvelope(definition.sourceData, section, frame);
    const center = frame.origin + frame.yAxis * getSectionCenterOffsetY(section) + frame.zAxis * getSectionCenterOffsetZ(section);
    const offset = point - center;
    const y = dot(offset, frame.yAxis);
    const z = dot(offset, frame.zAxis);
    var envelope = profile[nearestIndex];
    if (envelope == undefined)
    {
        if (includeBaseEnvelope == true)
            envelope = baseEnvelope;
        else
            envelope = emptyGuideEnvelope();
    }
    if (!(envelope.samples is array))
        envelope.samples = [];
    envelope.samples = append(envelope.samples, { "y" : y, "z" : z, "influence" : influence });
    var profileY = 0 * meter;
    var profileZ = 0 * meter;
    if (includeBaseEnvelope == true)
    {
        profileY = clampBetween(y, baseEnvelope.minY, baseEnvelope.maxY);
        profileZ = clampBetween(z, baseEnvelope.minZ, baseEnvelope.maxZ);
    }
    const sampleY = profileY + (y - profileY) * influence;
    const sampleZ = profileZ + (z - profileZ) * influence;
    envelope.minY = min(envelope.minY, sampleY);
    envelope.maxY = max(envelope.maxY, sampleY);
    envelope.minZ = min(envelope.minZ, sampleZ);
    envelope.maxZ = max(envelope.maxZ, sampleZ);
    profile[nearestIndex] = envelope;
    return profile;
}

function emptyGuideEnvelope() returns map
{
    return {
            "minY" : inf * meter,
            "maxY" : -inf * meter,
            "minZ" : inf * meter,
            "maxZ" : -inf * meter,
            "samples" : [],
            "hasGuideOffset" : false,
            "offsetY" : 0 * meter,
            "offsetZ" : 0 * meter
        };
}

function sectionGuideEnvelope(sourceData is map, section is map, frame is map) returns map
{
    var minY = inf * meter;
    var maxY = -inf * meter;
    var minZ = inf * meter;
    var maxZ = -inf * meter;
    for (var cornerIndex = 0; cornerIndex < 4; cornerIndex += 1)
    {
        const userOffset = cageCornerOffsetVector(section, cornerIndex, frame);
        const currentVector = sectionBaseCornerVector(sourceData, frame, section, cornerIndex, section.scale) + userOffset;
        const y = dot(currentVector, frame.yAxis);
        const z = dot(currentVector, frame.zAxis);
        minY = min(minY, y);
        maxY = max(maxY, y);
        minZ = min(minZ, z);
        maxZ = max(maxZ, z);
    }
    return {
            "minY" : minY,
            "maxY" : maxY,
            "minZ" : minZ,
            "maxZ" : maxZ,
            "samples" : [],
            "hasGuideOffset" : false,
            "offsetY" : 0 * meter,
            "offsetZ" : 0 * meter
        };
}

function guideOffsetWithInfluence(baseEnvelope is map, guideProfile is map, influence is number) returns map
{
    const offset = closestGuideOffsetComponents(baseEnvelope, guideProfile.samples, influence);

    return {
            "minY" : baseEnvelope.minY,
            "maxY" : baseEnvelope.maxY,
            "minZ" : baseEnvelope.minZ,
            "maxZ" : baseEnvelope.maxZ,
            "samples" : [],
            "hasGuideOffset" : true,
            "offsetY" : offset.y,
            "offsetZ" : offset.z
        };
}

function closestGuideOffsetComponents(profileEnvelope is map, samples is array, influence is number) returns map
{
    var bestSample;
    var bestProfileY = 0 * meter;
    var bestProfileZ = 0 * meter;
    var bestDistanceSquared = inf * meter * meter;
    for (var sample in samples)
    {
        const profileY = clampBetween(sample.y, profileEnvelope.minY, profileEnvelope.maxY);
        const profileZ = clampBetween(sample.z, profileEnvelope.minZ, profileEnvelope.maxZ);
        const dy = sample.y - profileY;
        const dz = sample.z - profileZ;
        const distanceSquared = dy * dy + dz * dz;
        if (distanceSquared < bestDistanceSquared)
        {
            bestDistanceSquared = distanceSquared;
            bestSample = sample;
            bestProfileY = profileY;
            bestProfileZ = profileZ;
        }
    }

    if (bestSample == undefined)
        return { "y" : 0 * meter, "z" : 0 * meter };

    return {
            "y" : (bestSample.y - bestProfileY) * influence,
            "z" : (bestSample.z - bestProfileZ) * influence
        };
}

function smoothGuideOffsetProfile(profile is array) returns array
{
    var result = profile;
    for (var i = 0; i < size(profile); i += 1)
    {
        if (profile[i] == undefined || profile[i].hasGuideOffset != true)
            continue;

        var offsetY = profile[i].offsetY * 2;
        var offsetZ = profile[i].offsetZ * 2;
        var weight = 2.0;
        const previous = nearestDefinedGuideEnvelope(profile, i - 1, -1);
        const next = nearestDefinedGuideEnvelope(profile, i + 1, 1);

        if (previous != undefined && previous.hasGuideOffset == true)
        {
            offsetY += previous.offsetY;
            offsetZ += previous.offsetZ;
            weight += 1.0;
        }
        if (next != undefined && next.hasGuideOffset == true)
        {
            offsetY += next.offsetY;
            offsetZ += next.offsetZ;
            weight += 1.0;
        }

        var smoothed = profile[i];
        smoothed.offsetY = offsetY / weight;
        smoothed.offsetZ = offsetZ / weight;
        result[i] = smoothed;
    }
    return result;
}

function smoothGuideEnvelopeProfile(profile is array) returns array
{
    var result = profile;
    for (var i = 0; i < size(profile); i += 1)
    {
        if (profile[i] == undefined || profile[i].hasGuideOffset == true)
            continue;

        var minY = profile[i].minY * 2;
        var maxY = profile[i].maxY * 2;
        var minZ = profile[i].minZ * 2;
        var maxZ = profile[i].maxZ * 2;
        var weight = 2.0;
        const previous = nearestDefinedGuideEnvelope(profile, i - 1, -1);
        const next = nearestDefinedGuideEnvelope(profile, i + 1, 1);

        if (previous != undefined && previous.hasGuideOffset != true)
        {
            minY += previous.minY;
            maxY += previous.maxY;
            minZ += previous.minZ;
            maxZ += previous.maxZ;
            weight += 1.0;
        }
        if (next != undefined && next.hasGuideOffset != true)
        {
            minY += next.minY;
            maxY += next.maxY;
            minZ += next.minZ;
            maxZ += next.maxZ;
            weight += 1.0;
        }

        var smoothed = profile[i];
        smoothed.minY = minY / weight;
        smoothed.maxY = maxY / weight;
        smoothed.minZ = minZ / weight;
        smoothed.maxZ = maxZ / weight;
        result[i] = smoothed;
    }
    return result;
}

function guideEnvelopeWithInfluence(baseEnvelope is map, envelope is map, influence is number) returns map
{
    return {
            "minY" : baseEnvelope.minY + (envelope.minY - baseEnvelope.minY) * influence,
            "maxY" : baseEnvelope.maxY + (envelope.maxY - baseEnvelope.maxY) * influence,
            "minZ" : baseEnvelope.minZ + (envelope.minZ - baseEnvelope.minZ) * influence,
            "maxZ" : baseEnvelope.maxZ + (envelope.maxZ - baseEnvelope.maxZ) * influence,
            "samples" : [],
            "hasGuideOffset" : false,
            "offsetY" : 0 * meter,
            "offsetZ" : 0 * meter
        };
}

function appendGuideSamples(target, samples is array) returns array
{
    if (!(target is array))
        target = [];
    for (var sample in samples)
    {
        target = append(target, sample);
    }
    return target;
}

function guideEdgesForSampling(guideEntities is Query) returns Query
{
    const guideEdges = qEntityFilter(guideEntities, EntityType.EDGE);
    const guideFaces = qEntityFilter(guideEntities, EntityType.FACE);
    const guideBodies = qEntityFilter(guideEntities, EntityType.BODY);
    const faceEdges = qAdjacent(guideFaces, AdjacencyType.EDGE, EntityType.EDGE);
    const bodyEdges = qOwnedByBody(guideBodies, EntityType.EDGE);
    return qUnion([guideEdges, faceEdges, bodyEdges]);
}

function guideFacesForSampling(guideEntities is Query) returns Query
{
    const guideFaces = qEntityFilter(guideEntities, EntityType.FACE);
    const guideBodies = qEntityFilter(guideEntities, EntityType.BODY);
    const bodyFaces = qOwnedByBody(guideBodies, EntityType.FACE);
    return qUnion([guideFaces, bodyFaces]);
}

function addGuideFaceSamplesToProfile(context is Context, definition is map, profile is array, guideFaces is Query, pathFrames is array, spreadAcrossStations is boolean) returns array
{
    const sampleParameters = [
            vector(0.25, 0.25),
            vector(0.5, 0.25),
            vector(0.75, 0.25),
            vector(0.25, 0.5),
            vector(0.5, 0.5),
            vector(0.75, 0.5),
            vector(0.25, 0.75),
            vector(0.5, 0.75),
            vector(0.75, 0.75)
        ];

    for (var guideFace in evaluateQuery(context, guideFaces))
    {
        for (var parameter in sampleParameters)
        {
            try silent
            {
                const point = evFaceTangentPlane(context, {
                                "face" : guideFace,
                                "parameter" : parameter
                            }).origin;
                profile = addGuideSampleToProfileForMode(profile, point, pathFrames, definition, spreadAcrossStations);
            }
        }
    }
    return profile;
}

function samplePathFrames(context is Context, pathData is map, sampleCount is number) returns array
{
    const parameters = range(0, 1, sampleCount);
    var frames = [];
    for (var parameter in parameters)
    {
        frames = append(frames, pathFrameAt(context, pathData, parameter));
    }
    return frames;
}

function sectionScaleManipulatorData(context is Context, pathData is map, sourceData is map, section is map) returns map
{
    const sectionFrame = sectionFrameAt(context, pathData, section);
    const manipulatorFrame = sectionManipulatorFrameAt(context, pathData, sourceData, section);
    var bestOffset = -inf * meter;
    var bestReference = sourceData.radius;
    var bestAdditive = 0 * meter;

    for (var cornerIndex = 0; cornerIndex < 4; cornerIndex += 1)
    {
        const baseVector = sectionBaseCornerVector(sourceData, sectionFrame, section, cornerIndex, 1.0);
        const baseProjection = dot(baseVector, manipulatorFrame.yAxis);
        const additiveProjection = dot(cageCornerOffsetVector(section, cornerIndex, sectionFrame), manipulatorFrame.yAxis);
        const currentProjection = baseProjection * section.scale + additiveProjection;
        if (currentProjection > bestOffset)
        {
            bestOffset = currentProjection;
            bestReference = baseProjection;
            bestAdditive = additiveProjection;
        }
    }

    if (abs(bestReference) <= 1e-8 * meter)
        bestReference = sourceData.radius;
    if (bestOffset <= 1e-8 * meter)
        bestOffset = max(sourceData.radius * section.scale, 0.001 * millimeter);

    return {
            "offset" : bestOffset,
            "reference" : abs(bestReference),
            "signedReference" : bestReference,
            "additive" : bestAdditive
        };
}

function sectionBaseCornerVector(sourceData is map, frame is map, section is map, cornerIndex is number, scale is number) returns Vector
{
    const y = cageCornerBaseY(sourceData, cornerIndex) * scale;
    const z = cageCornerBaseZ(sourceData, cornerIndex) * scale;
    const angle = section.rotation;
    const rotatedY = y * cos(angle) - z * sin(angle);
    const rotatedZ = y * sin(angle) + z * cos(angle);
    return frame.yAxis * rotatedY + frame.zAxis * rotatedZ;
}

function addDeformManipulators(context is Context, id is Id, definition is map)
{
    if (size(definition.crossSections) == 0)
        return;

    const activeIndex = clampSectionIndex(definition.activeCrossSectionIndex, definition.crossSections);
    var sectionPoints = [];
    for (var section in definition.crossSections)
    {
        sectionPoints = append(sectionPoints, sectionCenterPoint(context, definition.pathData, section));
    }

    if (definition.mode == DeformManipulatorMode.Insert)
    {
        reportFeatureInfo(context, id, "Click an insertion node to add a cross section.");
        const insertCandidates = getInsertCrossSectionCandidates(context, definition.pathData, definition.crossSections);
        var insertPoints = [];
        for (var candidate in insertCandidates)
        {
            insertPoints = append(insertPoints, sectionCenterPoint(context, definition.pathData, candidate));
        }
        if (size(insertPoints) > 0)
        {
            addManipulators(context, id, {
                        "insertSectionNodes" : pointsManipulator({
                                "points" : insertPoints,
                                "index" : -1
                            })
                    });
        }
        return;
    }

    if (definition.mode == DeformManipulatorMode.Delete)
    {
        reportFeatureInfo(context, id, "Click a cross section node to remove it.");
        addManipulators(context, id, {
                    "deleteSectionNodes" : pointsManipulator({
                            "points" : sectionPoints,
                            "index" : -1
                        })
                });
        return;
    }

    const activeSection = definition.crossSections[activeIndex];
    const activeProfileFrame = sectionManipulatorFrameAt(context, definition.pathData, definition.sourceData, activeSection);
    const scaleData = sectionScaleManipulatorData(context, definition.pathData, definition.sourceData, activeSection);
    var manipulators = {
        "sectionNodes" : pointsManipulator({
                "points" : sectionPoints,
                "index" : activeIndex
            }),
        "sectionMove" : fullTriadManipulator({
                "base" : coordSystem(activeProfileFrame.origin, activeProfileFrame.xAxis, activeProfileFrame.zAxis),
                "transform" : transform(vector(0, 0, 0) * meter),
                "displayEditView" : false
            }),
        "sectionScale" : linearManipulator({
                "base" : activeProfileFrame.origin,
                "direction" : activeProfileFrame.yAxis,
                "offset" : scaleData.offset,
                "minValue" : max(scaleData.reference * 0.001, 0.001 * millimeter),
                "maxValue" : max(scaleData.reference * 1000 + abs(scaleData.additive), definition.sourceData.radius * 1000),
                "primaryParameterId" : "crossSections"
            })
    };

    if (getSectionDeformType(activeSection) == DeformType.LATTICE_CAGE)
    {
        for (var cornerIndex = 0; cornerIndex < 4; cornerIndex += 1)
        {
            const cornerBase = sectionCageBasePoint(context, definition.pathData, definition.sourceData, activeSection, cornerIndex);
            manipulators["cageCorner" ~ cornerIndex] = fullTriadManipulator({
                        "base" : coordSystem(cornerBase, activeProfileFrame.xAxis, activeProfileFrame.zAxis),
                        "transform" : transform(cageCornerOffsetLocalVector(activeSection, cornerIndex)),
                        "displayEditView" : false
                    });
        }
    }

    addManipulators(context, id, manipulators);
}

export function deformManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    if (definition.crossSections == undefined || size(definition.crossSections) == 0)
        return definition;

    if (definition.activeCrossSectionIndex == undefined)
        definition.activeCrossSectionIndex = 0;
    if (definition.mode == undefined)
        definition.mode = DeformManipulatorMode.Create;

    const activeIndex = clampSectionIndex(definition.activeCrossSectionIndex, definition.crossSections);

    var pathData;
    var sourceData;
    try silent
    {
        pathData = buildPathData(context, definition);
        sourceData = buildSourceData(context, definition, pathData);
        pathData = finalizePathData(pathData, sourceData);
        definition = applyCrossSectionReferenceVertices(context, definition, pathData);
    }
    catch
    {
        return definition;
    }

    if (newManipulators["insertSectionNodes"] is map)
    {
        const insertIndex = newManipulators["insertSectionNodes"].index;
        const insertCandidates = getInsertCrossSectionCandidates(context, pathData, definition.crossSections);
        if (insertIndex >= 0 && insertIndex < size(insertCandidates))
        {
            definition.crossSections = insertCrossSectionSorted(definition.crossSections, insertCandidates[insertIndex]);
            definition.activeCrossSectionIndex = findMatchingCrossSectionIndex(definition.crossSections, insertCandidates[insertIndex].station);
            definition.uiTab = DeformUiTab.TRANSFORMS;
            definition.mode = DeformManipulatorMode.Create;
            definition.manipChangedCrossSections = true;
        }
        definition = refreshDxDyDzFields(context, pathData, definition);
        return definition;
    }

    if (newManipulators["deleteSectionNodes"] is map)
    {
        const deleteIndex = newManipulators["deleteSectionNodes"].index;
        if (deleteIndex >= 0 && deleteIndex < size(definition.crossSections) && size(definition.crossSections) > 1)
        {
            definition.crossSections = removeCrossSectionAt(definition.crossSections, deleteIndex);
            definition.activeCrossSectionIndex = clampSectionIndex(deleteIndex, definition.crossSections);
            definition.uiTab = DeformUiTab.TRANSFORMS;
            definition.mode = DeformManipulatorMode.Create;
            definition.manipChangedCrossSections = true;
        }
        definition = refreshDxDyDzFields(context, pathData, definition);
        return definition;
    }

    if (newManipulators["sectionNodes"] is map)
    {
        definition.activeCrossSectionIndex = clampSectionIndex(newManipulators["sectionNodes"].index, definition.crossSections);
        definition.uiTab = DeformUiTab.TRANSFORMS;
        definition.manipChangedCrossSections = true;
        definition = refreshDxDyDzFields(context, pathData, definition);
        return definition;
    }

    const section = definition.crossSections[activeIndex];
    const frame = pathFrameAt(context, pathData, section.station);
    const sectionFrame = sectionFrameAt(context, pathData, section);
    const sectionProfileFrame = sectionManipulatorFrameAt(context, pathData, sourceData, section);

    for (var cornerIndex = 0; cornerIndex < 4; cornerIndex += 1)
    {
        const manipulatorId = "cageCorner" ~ cornerIndex;
        if (newManipulators[manipulatorId] is map)
        {
            var cageSection = definition.crossSections[activeIndex];
            cageSection.deformType = DeformType.LATTICE_CAGE;
            const baseCornerPoint = sectionCageBasePoint(context, pathData, sourceData, cageSection, cornerIndex);
            var newCornerPoint = baseCornerPoint;
            if (newManipulators[manipulatorId].transform != undefined)
            {
                const cornerTranslation = newManipulators[manipulatorId].transform.translation;
                newCornerPoint = baseCornerPoint + sectionProfileFrame.xAxis * cornerTranslation[0] +
                    sectionProfileFrame.yAxis * cornerTranslation[1] + sectionProfileFrame.zAxis * cornerTranslation[2];
            }
            else
            {
                newCornerPoint = newManipulators[manipulatorId].offset;
            }
            cageSection = setCageCornerOffset(cageSection, cornerIndex, newCornerPoint - baseCornerPoint, sectionFrame);
            definition.crossSections[activeIndex] = cageSection;
            debugLog(definition, "cage corner " ~ cornerIndex ~ " stored offsets x=" ~ getCageCornerOffsetX(cageSection, cornerIndex) ~ ", y=" ~ getCageCornerOffsetY(cageSection, cornerIndex) ~ ", z=" ~ getCageCornerOffsetZ(cageSection, cornerIndex));
            definition.uiTab = DeformUiTab.TRANSFORMS;
            definition.manipChangedCrossSections = true;
            definition = refreshDxDyDzFields(context, pathData, definition);
            return definition;
        }
    }

    if (newManipulators["sectionMove"] is map)
    {
        var newCenter = sectionFrame.origin;
        var newRotation = section.rotation;
        var sectionMoveTransform;
        var hasSectionMoveTransform = false;
        var hasSectionProfileTilt = false;
        if (newManipulators["sectionMove"].transform != undefined)
        {
            sectionMoveTransform = newManipulators["sectionMove"].transform;
            hasSectionMoveTransform = true;
            hasSectionProfileTilt = triadHasProfileTilt(sectionMoveTransform);
            const moveTranslation = sectionMoveTransform.translation;
            newCenter = sectionFrame.origin + sectionProfileFrame.xAxis * moveTranslation[0] +
                sectionProfileFrame.yAxis * moveTranslation[1] + sectionProfileFrame.zAxis * moveTranslation[2];
            newRotation = section.rotation + triadRollAngle(sectionProfileFrame, sectionMoveTransform);
        }
        else
        {
            newCenter = newManipulators["sectionMove"].offset;
        }
        if (hasSectionReference(context, section))
        {
            const referencePoint = referencePointPosition(context, section.referenceVertex);
            const referenceStation = nearestPathParameter(context, pathData, referencePoint);
            const referenceFrame = pathFrameAt(context, pathData, referenceStation);
            const referenceDelta = newCenter - referencePoint;
            var referencedSection = definition.crossSections[activeIndex];
            referencedSection.referenceInitialized = true;
            referencedSection.referenceOffsetX = dot(referenceDelta, referenceFrame.xAxis);
            referencedSection.referenceOffsetY = dot(referenceDelta, referenceFrame.yAxis);
            referencedSection.referenceOffsetZ = dot(referenceDelta, referenceFrame.zAxis);
            referencedSection.rotation = newRotation;
            if (hasSectionMoveTransform && hasSectionProfileTilt)
                referencedSection = applyTriadRotationToSectionCage(context, pathData, sourceData, section, referencedSection, sectionProfileFrame, sectionMoveTransform);
            definition.crossSections[activeIndex] = referencedSection;
            definition = applyCrossSectionReferenceVertices(context, definition, pathData);
        }
        else
        {
            const stationDelta = dot(newCenter - frame.origin, frame.xAxis) / pathData.length;
            const newStation = clamp01(section.station + stationDelta);
            const newPathFrame = pathFrameAt(context, pathData, newStation);

            var movedSection = definition.crossSections[activeIndex];
            movedSection.referenceVertex = qNothing();
            movedSection.referenceInitialized = false;
            movedSection.referenceOffsetX = 0 * meter;
            movedSection.referenceOffsetY = 0 * meter;
            movedSection.referenceOffsetZ = 0 * meter;
            movedSection.station = newStation;
            movedSection.rotation = newRotation;
            if (section.stayOnPath == true)
            {
                movedSection.centerOffsetY = 0 * meter;
                movedSection.centerOffsetZ = 0 * meter;
            }
            else
            {
                const centerDelta = newCenter - newPathFrame.origin;
                movedSection.centerOffsetY = dot(centerDelta, newPathFrame.yAxis);
                movedSection.centerOffsetZ = dot(centerDelta, newPathFrame.zAxis);
            }
            if (hasSectionMoveTransform && hasSectionProfileTilt)
                movedSection = applyTriadRotationToSectionCage(context, pathData, sourceData, section, movedSection, sectionProfileFrame, sectionMoveTransform);
            definition.crossSections[activeIndex] = movedSection;
        }
        definition.uiTab = DeformUiTab.TRANSFORMS;
        definition.manipChangedCrossSections = true;
        definition = refreshDxDyDzFields(context, pathData, definition);
        return definition;
    }

    if (newManipulators["sectionScale"] is map)
    {
        var scaledSection = definition.crossSections[activeIndex];
        const scaleData = sectionScaleManipulatorData(context, pathData, sourceData, scaledSection);
        if (abs(scaleData.signedReference) > 1e-8 * meter)
            scaledSection.scale = max(0.001, (newManipulators["sectionScale"].offset - scaleData.additive) / scaleData.signedReference);
        else
            scaledSection.scale = max(0.001, abs(newManipulators["sectionScale"].offset) / sourceData.radius);
        if (getSectionDeformType(scaledSection) == DeformType.RIGID_PROFILE)
            scaledSection = resetCageOffsets(scaledSection);
        definition.crossSections[activeIndex] = scaledSection;
        definition.uiTab = DeformUiTab.TRANSFORMS;
        definition.manipChangedCrossSections = true;
        definition = refreshDxDyDzFields(context, pathData, definition);
        return definition;
    }

    definition.manipChangedCrossSections = false;
    return definition;
}

function deformEntities(context is Context, id is Id, definition is map)
{
    const sourceQueries = collectDeformSourceQueries(definition);
    const rebuildFaces = shouldRebuildFaces(definition);
    var sourceFaces = qNothing();

    if (rebuildFaces)
    {
        var solidPreparation = prepareSolidSourceFaces(context, id + "prepareSolids", definition, sourceQueries.solidBodies);
        definition = solidPreparation.definition;

        var exclusionPreparation = applySelectedFaceExclusions(context, definition, solidPreparation.solidSourceFaces);
        definition = exclusionPreparation.definition;

        sourceFaces = qUnion([exclusionPreparation.solidSourceFaces, qOwnedByBody(sourceQueries.sheetBodies, EntityType.FACE), sourceQueries.faceQuery]);
    }
    else
    {
        debugLog(definition, "solid and surface output disabled; skipping face preparation");
        if (definition.keepCurves == true)
            sourceFaces = qUnion([qOwnedByBody(sourceQueries.solidBodies, EntityType.FACE), qOwnedByBody(sourceQueries.sheetBodies, EntityType.FACE), sourceQueries.faceQuery]);
    }

    // Always eagerly deform wire bodies and directly-selected edges.
    var targetEdgeBodies = [];
    if (rebuildFaces || definition.keepCurves == true)
    {
        targetEdgeBodies = deformWireAndDirectEdges(context, id, definition, sourceQueries);
    }
    else
    {
        debugLog(definition, "curve output disabled; skipping edge deformation");
    }
    deformSelectedVertices(context, id, definition, sourceQueries.vertexQuery);

    if (!rebuildFaces)
    {
        debugLog(definition, "solid and surface output disabled; face rebuild skipped");
        deleteQueryArrayIfNeeded(context, id + "deleteCurves", targetEdgeBodies, definition.keepCurves);
        return;
    }

    // Rebuild faces via pure FFD B-spline surface deformation.
    const faceRebuildResult = rebuildSourceFacesFFD(context, id, definition, sourceFaces);
    joinDeformedOutputs(context, id, definition, sourceQueries);

    deleteSheetBodiesFromQueryArrayIfNeeded(context, id + "deleteSurfaces", faceRebuildResult.faceBodies, definition.keepSurfaces);
    deleteQueryArrayIfNeeded(context, id + "deleteCurves", targetEdgeBodies, definition.keepCurves);
}

function collectDeformSourceQueries(definition is map) returns map
{
    const bodyQuery = qEntityFilter(definition.entities, EntityType.BODY);
    return {
            "solidBodies" : qBodyType(bodyQuery, BodyType.SOLID),
            "sheetBodies" : qBodyType(bodyQuery, BodyType.SHEET),
            "wireBodies" : qBodyType(bodyQuery, BodyType.WIRE),
            "faceQuery" : qEntityFilter(definition.entities, EntityType.FACE),
            "directEdgeQuery" : qEntityFilter(definition.entities, EntityType.EDGE),
            "vertexQuery" : qEntityFilter(definition.entities, EntityType.VERTEX)
        };
}

function prepareSolidSourceFaces(context is Context, id is Id, definition is map, solidBodies is Query) returns map
{
    var solidSourceFaces = qOwnedByBody(solidBodies, EntityType.FACE);
    definition.reapplyHoleData = { "allFaces" : [], "byBody" : [] };
    definition.deformedHoleData = { "allFaces" : [], "byBody" : [] };
    definition.reapplyFilletData = { "allFaces" : [], "byBody" : [] };
    definition.reapplyBoundaryEdgesToSkip = qNothing();
    definition.pocketProtrusionFacesToExclude = qNothing();
    definition.hasPocketProtrusionFaceExclusions = false;
    definition.hasDeformedHoleCutters = false;
    definition.sourceFilletsRemovedForReapply = false;

    if (definition.keepSolid != true)
    {
        debugLog(definition, "solid output disabled; skipping solid hole and fillet preparation");
        return {
                "definition" : definition,
                "solidSourceFaces" : solidSourceFaces
            };
    }

    if (definition.reapplyFillets == true)
    {
        definition.reapplyFilletData = buildReapplyFilletData(context, definition, solidBodies);
        const filletFacesToRemove = queryUnionOrNothing(definition.reapplyFilletData.allFaces);
        if (queryHasEntities(context, filletFacesToRemove))
        {
            debugQueryCount(context, definition, "fillet faces detected for removal", filletFacesToRemove);
            definition.sourceFilletsRemovedForReapply = removeSourceFilletsForReapply(context, id + "removeFillets", definition);
            if (definition.sourceFilletsRemovedForReapply == true)
                definition.reapplyFilletData = resolveReapplyFilletEdgesAfterRemoval(context, definition);
        }
    }

    if (definition.reapplyHoles == true)
    {
        definition.reapplyHoleData = buildReapplyHoleData(context, definition, solidBodies);
        const holeFacesToSkip = queryUnionOrNothing(definition.reapplyHoleData.allFaces);
        const holeBoundaryEdgesToSkip = qAdjacent(holeFacesToSkip, AdjacencyType.EDGE, EntityType.EDGE);
        definition.reapplyBoundaryEdgesToSkip = qUnion([definition.reapplyBoundaryEdgesToSkip, holeBoundaryEdgesToSkip]);
        solidSourceFaces = qSubtraction(solidSourceFaces, holeFacesToSkip);
        debugQueryCount(context, definition, "re-applied hole faces skipped", holeFacesToSkip);
        debugQueryCount(context, definition, "re-applied hole boundary edges skipped", holeBoundaryEdgesToSkip);
    }
    else
    {
        definition.deformedHoleData = buildReapplyHoleData(context, definition, solidBodies);
        const deformedHoleFacesToSkip = queryUnionOrNothing(definition.deformedHoleData.allFaces);
        if (queryHasEntities(context, deformedHoleFacesToSkip))
        {
            definition.hasDeformedHoleCutters = true;
            const deformedHoleBoundaryEdgesToSkip = qAdjacent(deformedHoleFacesToSkip, AdjacencyType.EDGE, EntityType.EDGE);
            definition.reapplyBoundaryEdgesToSkip = qUnion([definition.reapplyBoundaryEdgesToSkip, deformedHoleBoundaryEdgesToSkip]);
            solidSourceFaces = qSubtraction(solidSourceFaces, deformedHoleFacesToSkip);
            debugQueryCount(context, definition, "deformed hole faces skipped", deformedHoleFacesToSkip);
            debugQueryCount(context, definition, "deformed hole boundary edges skipped", deformedHoleBoundaryEdgesToSkip);
        }
    }

    return {
            "definition" : definition,
            "solidSourceFaces" : solidSourceFaces
        };
}

function removeSourceFilletsForReapply(context is Context, id is Id, definition is map) returns boolean
{
    if (definition.keepInputBodies == true)
    {
        debugLog(definition, "source fillet removal skipped because Keep input bodies is enabled");
        return false;
    }

    if (definition.reapplyFilletData == undefined || !(definition.reapplyFilletData.byBody is array))
        return false;

    var groupCount = 0;
    var removedCount = 0;
    for (var bodyData in definition.reapplyFilletData.byBody)
    {
        for (var filletInfo in bodyData.fillets)
        {
            const groupId = id + ("group" ~ groupCount);
            var removed = tryDeleteFilletFacesForReapply(context, groupId, definition, filletInfo.faces);
            if (!removed)
            {
                debugLog(definition, "source fillet group " ~ groupCount ~ " delete/heal failed; trying individual faces");
                removed = tryDeleteFilletFacesIndividuallyForReapply(context, groupId + "faces", definition, filletInfo.faces);
            }

            if (removed)
                removedCount += 1;
            groupCount += 1;
        }
    }

    debugLog(definition, "source fillet removal completed groups removed=" ~ removedCount ~ " of " ~ groupCount);
    return removedCount > 0;
}

function tryDeleteFilletFacesForReapply(context is Context, id is Id, definition is map, filletFaces is Query) returns boolean
{
    if (!queryHasEntities(context, filletFaces))
        return true;

    var removed = false;
    try silent
    {
        opModifyFillet(context, id, {
                    "faces" : filletFaces,
                    "modifyFilletType" : ModifyFilletType.REMOVE_FILLET
                });
        removed = true;
    }

    if (removed || !queryHasEntities(context, filletFaces))
    {
        debugLog(definition, "source fillet faces deleted and healed before deformation");
        return true;
    }

    return false;
}

function tryDeleteFilletFacesIndividuallyForReapply(context is Context, id is Id, definition is map, filletFaces is Query) returns boolean
{
    var faces = [];
    try silent
    {
        faces = evaluateQuery(context, filletFaces);
    }

    if (size(faces) == 0)
        return !queryHasEntities(context, filletFaces);

    for (var faceIndex, face in faces)
    {
        tryDeleteFilletFacesForReapply(context, id + ("face" ~ faceIndex), definition, face);
    }

    if (!queryHasEntities(context, filletFaces))
        return true;

    debugLog(definition, "source fillet individual delete/heal left some fillet faces in place");
    return false;
}

function resolveReapplyFilletEdgesAfterRemoval(context is Context, definition is map) returns map
{
    if (definition.reapplyFilletData == undefined || !(definition.reapplyFilletData.byBody is array))
        return definition.reapplyFilletData;

    var byBody = [];
    for (var bodyIndex = 0; bodyIndex < size(definition.reapplyFilletData.byBody); bodyIndex += 1)
    {
        const bodyData = definition.reapplyFilletData.byBody[bodyIndex];
        const bodyEdges = qOwnedByBody(bodyData.body, EntityType.EDGE);
        var updatedFillets = [];
        for (var filletIndex = 0; filletIndex < size(bodyData.fillets); filletIndex += 1)
        {
            var filletInfo = bodyData.fillets[filletIndex];
            filletInfo.refilletEdges = resolveRefilletSourceEdges(context, definition, bodyEdges, filletInfo);
            debugQueryCount(context, definition, "resolved healed source edges for fillet " ~ filletIndex, queryUnionOrNothing(filletInfo.refilletEdges));
            updatedFillets = append(updatedFillets, filletInfo);
        }
        byBody = append(byBody, {
                    "body" : bodyData.body,
                    "fillets" : updatedFillets
                });
    }

    return {
            "allFaces" : definition.reapplyFilletData.allFaces,
            "byBody" : byBody
        };
}

function resolveRefilletSourceEdges(context is Context, definition is map, bodyEdges is Query, filletInfo is map) returns array
{
    var resolvedEdges = [];
    if (!(filletInfo.edgePoints is array))
        return resolvedEdges;

    for (var point in filletInfo.edgePoints)
    {
        var edge = qNothing();
        try silent
        {
            edge = qContainsPoint(bodyEdges, point);
            if (evaluateQueryCount(context, edge) == 0)
                edge = qClosestTo(bodyEdges, point);
        }
        if (queryHasEntities(context, edge))
            resolvedEdges = append(resolvedEdges, edge);
    }

    return resolvedEdges;
}

function applySelectedFaceExclusions(context is Context, definition is map, solidSourceFaces is Query) returns map
{
    const excludedFaces = computePocketProtrusionFacesToExclude(context, definition);

    var hasExcludedFaces = false;
    try silent
    {
        hasExcludedFaces = evaluateQueryCount(context, excludedFaces) > 0;
    }
    if (!hasExcludedFaces)
        return { "definition" : definition, "solidSourceFaces" : solidSourceFaces };

    definition.pocketProtrusionFacesToExclude = excludedFaces;
    definition.hasPocketProtrusionFaceExclusions = true;
    solidSourceFaces = qSubtraction(solidSourceFaces, excludedFaces);
    definition.reapplyBoundaryEdgesToSkip = qUnion([definition.reapplyBoundaryEdgesToSkip, qAdjacent(excludedFaces, AdjacencyType.EDGE, EntityType.EDGE)]);
    debugQueryCount(context, definition, "excluded pocket/protrusion faces", excludedFaces);
    debugQueryCount(context, definition, "excluded pocket/protrusion boundary edges", definition.reapplyBoundaryEdgesToSkip);

    return {
            "definition" : definition,
            "solidSourceFaces" : solidSourceFaces
        };
}

function computePocketProtrusionFacesToExclude(context is Context, definition is map) returns Query
{
    var exclusionQueries = [];

    if (definition.pocketProtrusionSeedFaces != undefined && queryHasEntities(context, definition.pocketProtrusionSeedFaces))
        exclusionQueries = append(exclusionQueries, resolvePocketProtrusionFacesFromSeeds(context, definition, definition.pocketProtrusionSeedFaces));

    return queryUnionOrNothing(exclusionQueries);
}

function resolvePocketProtrusionFacesFromSeeds(context is Context, definition is map, seedFaces is Query) returns Query
{
    var resolvedFaces = [];
    for (var seedFace in evaluateQuery(context, seedFaces))
    {
        if (queryArrayContains(context, resolvedFaces, seedFace))
            continue;

        if (definition.reapplyHoles == true)
        {
            const seedHoleFaces = holeFacesForPocketProtrusionSeed(context, seedFace);
            if (queryHasEntities(context, seedHoleFaces))
            {
                resolvedFaces = append(resolvedFaces, seedHoleFaces);
                continue;
            }
        }

        const featureFaces = growPocketProtrusionFaceIsland(context, seedFace, definition);
        resolvedFaces = appendQueryArray(resolvedFaces, featureFaces);
    }

    const result = queryUnionOrNothing(resolvedFaces);
    debugQueryCount(context, definition, "resolved pocket/protrusion faces from seeds", result);
    return result;
}

function holeFacesForPocketProtrusionSeed(context is Context, seedFace is Query) returns Query
{
    var surface;
    var isCylinderFace = false;
    try silent
    {
        surface = evSurfaceDefinition(context, {
                    "face" : seedFace
                });
        isCylinderFace = canBeCylinder(surface);
    }
    if (!isCylinderFace)
        return qNothing();

    var holeFaces = qNothing();
    var holeFaceCount = 0;
    try silent
    {
        holeFaces = qHoleFaces(seedFace);
        holeFaceCount = evaluateQueryCount(context, holeFaces);
    }
    if (holeFaceCount > 0)
        return holeFaces;

    if (isLikelyHoleCylinder(context, seedFace))
        return seedFace;

    return qNothing();
}

function growPocketProtrusionFaceIsland(context is Context, seedFace is Query, definition is map) returns array
{
    var ownerFaceCount = 0;
    try silent
    {
        ownerFaceCount = evaluateQueryCount(context, qOwnedByBody(qOwnerBody(seedFace), EntityType.FACE));
    }

    const convexBounded = growFaceIslandStoppingAtConvexity(context, seedFace, EdgeConvexityType.CONCAVE, ownerFaceCount);
    const concaveBounded = growFaceIslandStoppingAtConvexity(context, seedFace, EdgeConvexityType.CONVEX, ownerFaceCount);
    const selected = smallerUsefulFaceIsland(convexBounded, concaveBounded, ownerFaceCount);
    debugLog(definition, "pocket/protrusion seed island sizes: convex-bounded=" ~ size(convexBounded) ~ ", concave-bounded=" ~ size(concaveBounded) ~ ", selected=" ~ size(selected));
    return selected;
}

function growFaceIslandStoppingAtConvexity(context is Context, seedFace is Query, stopConvexity, ownerFaceCount is number) returns array
{
    var component = [seedFace];
    var frontier = [seedFace];
    var frontierIndex = 0;

    while (frontierIndex < size(frontier))
    {
        const currentFace = frontier[frontierIndex];
        frontierIndex += 1;

        const passEdges = qSubtraction(qLoopEdges(currentFace), qEdgeConvexityTypeFilter(qLoopEdges(currentFace), stopConvexity));
        for (var adjacentFace in evaluateQuery(context, qAdjacent(passEdges, AdjacencyType.EDGE, EntityType.FACE)))
        {
            if (queryArrayContains(context, component, adjacentFace))
                continue;

            component = append(component, adjacentFace);
            frontier = append(frontier, adjacentFace);
        }
    }

    if (ownerFaceCount > 0 && size(component) >= ownerFaceCount)
        return [seedFace];

    return component;
}

function smallerUsefulFaceIsland(first is array, second is array, ownerFaceCount is number) returns array
{
    const firstUseful = usefulFeatureIsland(first, ownerFaceCount);
    const secondUseful = usefulFeatureIsland(second, ownerFaceCount);

    if (firstUseful && secondUseful)
    {
        if (size(first) == 1 && size(second) > 1)
            return second;
        if (size(second) == 1 && size(first) > 1)
            return first;
        return size(first) <= size(second) ? first : second;
    }
    if (firstUseful)
        return first;
    if (secondUseful)
        return second;
    return size(first) <= size(second) ? first : second;
}

function usefulFeatureIsland(component is array, ownerFaceCount is number) returns boolean
{
    if (size(component) == 0)
        return false;
    if (ownerFaceCount > 0 && size(component) >= ownerFaceCount)
        return false;
    return true;
}

function queryArrayContains(context is Context, queries is array, candidate is Query) returns boolean
{
    if (size(queries) == 0)
        return false;
    return queryHasEntities(context, qIntersection(candidate, qUnion(queries)));
}

function queryHasEntities(context is Context, query is Query) returns boolean
{
    var hasEntities = false;
    try silent
    {
        hasEntities = evaluateQueryCount(context, query) > 0;
    }
    return hasEntities;
}

// Deforms wire body edges and directly-selected edges via B-spline control-net FFD,
// storing ATTR_TARGET_EDGES on each successfully deformed source edge.
// If a specific edge cannot be deformed (kernel error), it is skipped with a warning.
function deformWireAndDirectEdges(context is Context, id is Id, definition is map, sourceQueries is map) returns array
{
    const wireEdges = qOwnedByBody(sourceQueries.wireBodies, EntityType.EDGE);
    const edgeTable = evaluateQuery(context, qUnion([sourceQueries.directEdgeQuery, wireEdges]));

    debugQueryCount(context, definition, "solid bodies", sourceQueries.solidBodies);
    debugQueryCount(context, definition, "sheet bodies", sourceQueries.sheetBodies);
    debugQueryCount(context, definition, "wire bodies", sourceQueries.wireBodies);
    debugQueryCount(context, definition, "selected faces", sourceQueries.faceQuery);
    debugLog(definition, "wire and direct edge count=" ~ size(edgeTable));

    var targetEdgeBodies = [];
    for (var edgeIndex, edge in edgeTable)
    {
        const targetEdgeId = id + ("edge" ~ edgeIndex);
        var targetEdge = qNothing();
        var edgeSucceeded = false;
        try
        {
            targetEdge = deformEdge(context, targetEdgeId, definition, edge);
            edgeSucceeded = true;
        }
        if (!edgeSucceeded)
        {
            debugLog(definition, "edge " ~ edgeIndex ~ " could not be deformed via B-spline; skipping");
            reportFeatureWarning(context, targetEdgeId, "An edge could not be deformed. Check that the edge has a valid B-spline approximation.");
            continue;
        }
        targetEdgeBodies = append(targetEdgeBodies, qCreatedBy(targetEdgeId, EntityType.BODY));
        setAttribute(context, {
                    "entities" : edge,
                    "name" : ATTR_TARGET_EDGES,
                    "attribute" : targetEdge
                });
    }
    return targetEdgeBodies;
}

function deformSelectedVertices(context is Context, id is Id, definition is map, vertexQuery is Query)
{
    for (var vertexIndex, vertex in evaluateQuery(context, vertexQuery))
    {
        deformVertex(context, id + ("vertex" ~ vertexIndex), definition, vertex);
    }
}

function shouldRebuildFaces(definition is map) returns boolean
{
    return definition.keepSolid == true || definition.keepSurfaces == true;
}

// Rebuilds source faces using pure FFD B-spline surface deformation.
//
// Each face is deformed by obtaining its B-spline approximation, enriching the knot
// structure to match the deformation domain complexity, then applying deformPoint() to
// every control point and creating a new B-spline surface body. No edge wire bodies are
// created or required. If a face cannot be deformed, a per-face warning is emitted and
// the face is skipped.
//
// Returns a map: { "faceBodies" : array }
function rebuildSourceFacesFFD(context is Context, id is Id, definition is map, sourceFaces is Query) returns map
{
    var targetFaceBodies = [];

    for (var faceIndex, face in evaluateQuery(context, sourceFaces))
    {
        var targetFaces = qNothing();
        var faceSucceeded = false;
        try
        {
            targetFaces = createDeformedBSplineFace(context, id + ("face" ~ faceIndex) + "bspline", definition, face);
            faceSucceeded = evaluateQueryCount(context, targetFaces) > 0;
        }

        if (!faceSucceeded)
        {
            debugLog(definition, "face " ~ faceIndex ~ " B-spline deformation failed");
            debugEntitiesIfEnabled(context, definition, face, DebugColor.RED);
            reportFeatureWarning(context, id + ("face" ~ faceIndex), "A deformed face could not be reconstructed. Check that the face has a valid B-spline surface approximation.");
            continue;
        }

        debugLog(definition, "face " ~ faceIndex ~ " rebuilt by deformed B-spline approximation");
        setAttribute(context, {
                    "entities" : face,
                    "name" : ATTR_TARGET_FACES,
                    "attribute" : targetFaces
                });
        targetFaceBodies = append(targetFaceBodies, targetFaces);
    }
    return { "faceBodies" : targetFaceBodies };
}

function joinDeformedOutputs(context is Context, id is Id, definition is map, sourceQueries is map)
{
    if (definition.keepSolid == true)
    {
        joinTargetFaces(context, id + "joinSolids", definition, sourceQueries.solidBodies, true);
        joinTargetFaces(context, id + "joinSheets", definition, sourceQueries.sheetBodies, false);
        joinSelectedFaceTargets(context, id + "joinFaces", definition, sourceQueries.faceQuery);
    }
    else if (definition.keepSurfaces == true)
    {
        debugLog(definition, "solid output disabled; keeping deformed surfaces");
        joinTargetFaces(context, id + "joinSolidSurfaces", definition, sourceQueries.solidBodies, false);
        joinTargetFaces(context, id + "joinSheets", definition, sourceQueries.sheetBodies, false);
        joinSelectedFaceTargets(context, id + "joinFaces", definition, sourceQueries.faceQuery);
    }
}

function deleteQueryArrayIfNeeded(context is Context, id is Id, queries is array, keep is boolean)
{
    if (keep == true || size(queries) == 0)
        return;

    try silent
    {
        opDeleteBodies(context, id, {
                    "entities" : qUnion(queries)
                });
    }
}

function deleteSheetBodiesFromQueryArrayIfNeeded(context is Context, id is Id, queries is array, keep is boolean)
{
    if (keep == true || size(queries) == 0)
        return;

    try silent
    {
        opDeleteBodies(context, id, {
                    "entities" : qBodyType(qUnion(queries), BodyType.SHEET)
                });
    }
}

function deleteInputBodiesIfNeeded(context is Context, id is Id, definition is map)
{
    if (definition.keepInputBodies == true)
        return;

    var bodiesToDelete = qEntityFilter(definition.entities, EntityType.BODY);
    if (definition.rigidBodies != undefined)
        bodiesToDelete = qSubtraction(bodiesToDelete, definition.rigidBodies);

    var hasBodies = false;
    try silent
    {
        hasBodies = evaluateQueryCount(context, bodiesToDelete) > 0;
    }
    if (!hasBodies)
        return;

    try silent
    {
        opDeleteBodies(context, id, {
                    "entities" : bodiesToDelete
                });
    }
}

function transformRigidBodies(context is Context, id is Id, definition is map)
{
    if (definition.rigidBodies == undefined)
        return;

    var rigidBodies = [];
    try silent
    {
        rigidBodies = evaluateQuery(context, definition.rigidBodies);
    }
    if (size(rigidBodies) == 0)
        return;

    debugLog(definition, "transform-only body count=" ~ size(rigidBodies));
    for (var bodyIndex, body in rigidBodies)
    {
        var center;
        try silent
        {
            const bounds = evBox3d(context, {
                        "topology" : body,
                        "tight" : false
                    });
            const minCorner = bounds.minCorner;
            const maxCorner = bounds.maxCorner;
            center = vector((minCorner[0] + maxCorner[0]) / 2, (minCorner[1] + maxCorner[1]) / 2, (minCorner[2] + maxCorner[2]) / 2);
        }
        catch
        {
            debugLog(definition, "transform-only body " ~ bodyIndex ~ " skipped because its bounds could not be evaluated");
            continue;
        }

        var transformSucceeded = false;
        try silent
        {
            opTransform(context, id + ("body" ~ bodyIndex), {
                        "bodies" : body,
                        "transform" : rigidTransformAtPoint(context, definition, center)
                    });
            transformSucceeded = true;
        }

        if (transformSucceeded)
            debugLog(definition, "transform-only body " ~ bodyIndex ~ " moved rigidly from center " ~ center);
        else
            debugLog(definition, "opTransform failed for transform-only body " ~ bodyIndex);
    }
}

function rigidTransformAtPoint(context is Context, definition is map, point is Vector) returns Transform
{
    const source = definition.sourceData;
    const local = worldToLocal(point, source.origin, source.xAxis, source.yAxis, source.zAxis);
    var t = sourceStationForLocalX(definition, local[0]);
    if (t < 0)
        t = 0;
    else if (t > 1)
        t = 1;

    const section = crossSectionAt(definition.crossSections, t);
    const frame = pathFrameAt(context, definition.pathData, t);
    const targetPoint = deformPoint(context, definition, point);
    const rotatedZ = rotationAround(line(targetPoint, frame.xAxis), section.rotation).linear * frame.zAxis;
    const sourceFrame = coordSystem(point, source.xAxis, source.zAxis);
    const targetFrame = coordSystem(targetPoint, frame.xAxis, rotatedZ);
    return toWorld(targetFrame) * fromWorld(sourceFrame);
}

function buildReapplyFilletData(context is Context, definition is map, solidBodies is Query) returns map
{
    var allFilletFaces = [];
    var byBody = [];
    var processedFilletFaceGroups = [];

    for (var bodyIndex, body in evaluateQuery(context, solidBodies))
    {
        var bodyFillets = [];
        for (var face in evaluateQuery(context, qOwnedByBody(body, EntityType.FACE)))
        {
            if (size(processedFilletFaceGroups) > 0 && queryHasEntities(context, qIntersection(face, queryUnionOrNothing(processedFilletFaceGroups))))
                continue;

            var surface;
            var isCylinder = false;
            try silent
            {
                surface = evSurfaceDefinition(context, {
                            "face" : face
                        });
                isCylinder = canBeCylinder(surface);
            }

            if (!isCylinder)
                continue;

            var holeFaceCount = 0;
            try silent
            {
                holeFaceCount = evaluateQueryCount(context, qHoleFaces(face));
            }
            if (holeFaceCount > 0)
                continue;

            var filletFaces = qNothing();
            var filletFaceCount = 0;
            try silent
            {
                const equalRadiusFilletFaces = qFilletFaces(face, CompareType.EQUAL);
                filletFaces = qIntersection(equalRadiusFilletFaces, qTangentConnectedFaces(face));
                filletFaceCount = evaluateQueryCount(context, filletFaces);
            }
            if (filletFaceCount == 0)
                continue;

            const filletInfo = buildReapplyFilletInfo(context, filletFaces, face, surface);
            if (filletInfo == undefined)
                continue;

            bodyFillets = append(bodyFillets, filletInfo);
            allFilletFaces = append(allFilletFaces, filletFaces);
            processedFilletFaceGroups = append(processedFilletFaceGroups, filletFaces);
            debugLog(definition, "detected re-apply fillet on body " ~ bodyIndex ~ ", face count=" ~ filletFaceCount ~ ", boundary edge count=" ~ size(filletInfo.edges) ~ ", radius=" ~ filletInfo.radius);
        }

        byBody = append(byBody, {
                    "body" : body,
                    "fillets" : bodyFillets
                });
    }

    return {
            "allFaces" : allFilletFaces,
            "byBody" : byBody
        };
}

function buildReapplyFilletInfo(context is Context, filletFaces is Query, seedFilletFace is Query, cylinderSurface is Cylinder)
{
    const boundaryEdges = evaluateQuery(context, refilletBoundaryEdges(context, filletFaces, cylinderSurface.coordSystem.zAxis));
    if (size(boundaryEdges) < 1)
        return undefined;

    var facePoint;
    try silent
    {
        facePoint = evFaceTangentPlane(context, {
                        "face" : seedFilletFace,
                        "parameter" : vector(0.5, 0.5)
                    }).origin;
    }

    var axisPoint;
    const boundaryPoints = sampleFaceBoundaryPoints(context, filletFaces);
    if (size(boundaryPoints) > 0)
    {
        const axisDirection = cylinderSurface.coordSystem.zAxis;
        const axisOrigin = cylinderSurface.coordSystem.origin;
        var minParameter = inf * meter;
        var maxParameter = -inf * meter;
        for (var point in boundaryPoints)
        {
            const axisParameter = dot(point - axisOrigin, axisDirection);
            minParameter = min(minParameter, axisParameter);
            maxParameter = max(maxParameter, axisParameter);
        }
        axisPoint = axisOrigin + axisDirection * ((minParameter + maxParameter) / 2);
    }

    return {
            "faces" : filletFaces,
            "radius" : cylinderSurface.radius,
            "edges" : boundaryEdges,
            "edgePoints" : sampleEdgeMidpoints(context, boundaryEdges),
            "facePoint" : facePoint,
            "axisPoint" : axisPoint
        };
}

function refilletBoundaryEdges(context is Context, filletFaces is Query, axisDirection is Vector) returns Query
{
    var boundaryEdges = [];
    var axisAlignedEdges = [];
    for (var edge in evaluateQuery(context, qLoopEdges(filletFaces)))
    {
        const outsideFaces = qSubtraction(qAdjacent(edge, AdjacencyType.EDGE, EntityType.FACE), filletFaces);
        if (!queryHasEntities(context, outsideFaces))
            continue;

        boundaryEdges = append(boundaryEdges, edge);
        if (edgeAlignedWithDirection(context, edge, axisDirection))
            axisAlignedEdges = append(axisAlignedEdges, edge);
    }

    if (size(axisAlignedEdges) > 0)
        return queryUnionOrNothing(axisAlignedEdges);
    return queryUnionOrNothing(boundaryEdges);
}

function edgeAlignedWithDirection(context is Context, edge is Query, direction is Vector) returns boolean
{
    var aligned = false;
    try silent
    {
        const tangent = evEdgeTangentLine(context, {
                    "edge" : edge,
                    "parameter" : 0.5
                });
        aligned = abs(dot(normalize(tangent.direction), normalize(direction))) > 0.5;
    }
    return aligned;
}

function sampleEdgeMidpoints(context is Context, edges is array) returns array
{
    var points = [];
    for (var edge in edges)
    {
        try silent
        {
            points = append(points, evEdgeTangentLine(context, {
                                "edge" : edge,
                                "parameter" : 0.5
                            }).origin);
        }
    }
    return points;
}

function buildReapplyHoleData(context is Context, definition is map, solidBodies is Query) returns map
{
    var allHoleFaces = [];
    var byBody = [];
    var processedHoleFaceGroups = [];

    for (var bodyIndex, body in evaluateQuery(context, solidBodies))
    {
        var bodyHoles = [];
        for (var face in evaluateQuery(context, qOwnedByBody(body, EntityType.FACE)))
        {
            var surface;
            var isCylinder = false;
            try silent
            {
                surface = evSurfaceDefinition(context, {
                            "face" : face
                        });
                isCylinder = canBeCylinder(surface);
            }

            if (!isCylinder)
                continue;

            if (size(processedHoleFaceGroups) > 0 && evaluateQueryCount(context, qIntersection(face, queryUnionOrNothing(processedHoleFaceGroups))) > 0)
                continue;

            var holeFaces = qNothing();
            var holeFaceCount = 0;
            try silent
            {
                holeFaces = qHoleFaces(face);
                holeFaceCount = evaluateQueryCount(context, holeFaces);
            }
            if (holeFaceCount == 0)
            {
                if (!isLikelyHoleCylinder(context, face))
                    continue;
                holeFaces = face;
                holeFaceCount = 1;
            }

            const holeInfo = buildReapplyHoleInfo(context, definition, holeFaces, surface);
            if (holeInfo == undefined)
                continue;

            bodyHoles = append(bodyHoles, holeInfo);
            allHoleFaces = append(allHoleFaces, holeFaces);
            processedHoleFaceGroups = append(processedHoleFaceGroups, holeFaces);
            debugLog(definition, "detected " ~ (definition.reapplyHoles == true ? "re-apply" : "deformed") ~ " hole on body " ~ bodyIndex ~ ", face group count=" ~ holeFaceCount ~ ", radius=" ~ holeInfo.radius);
        }

        byBody = append(byBody, {
                    "body" : body,
                    "holes" : bodyHoles
                });
    }

    return {
            "allFaces" : allHoleFaces,
            "byBody" : byBody
        };
}

function buildReapplyHoleInfo(context is Context, definition is map, holeFace is Query, cylinderSurface is Cylinder)
{
    const axisDirection = cylinderSurface.coordSystem.zAxis;
    const axisOrigin = cylinderSurface.coordSystem.origin;
    const boundaryEdges = evaluateQuery(context, qLoopEdges(holeFace));
    const boundaryPoints = sampleFaceBoundaryPoints(context, holeFace);
    if (size(boundaryPoints) < 2)
    {
        debugLog(definition, "cylindrical hole face skipped because boundary samples could not be evaluated");
        return undefined;
    }

    var minParameter = inf * meter;
    var maxParameter = -inf * meter;
    for (var point in boundaryPoints)
    {
        const axisParameter = dot(point - axisOrigin, axisDirection);
        minParameter = min(minParameter, axisParameter);
        maxParameter = max(maxParameter, axisParameter);
    }

    if (maxParameter - minParameter < 1e-7 * meter)
    {
        debugLog(definition, "cylindrical hole face skipped because its axis span was too small");
        return undefined;
    }

    const sourceStart = axisOrigin + axisDirection * minParameter;
    const sourceEnd = axisOrigin + axisDirection * maxParameter;
    const targetStart = deformPoint(context, definition, sourceStart);
    const targetEnd = deformPoint(context, definition, sourceEnd);
    var targetDirection = targetEnd - targetStart;
    if (norm(targetDirection) < 1e-8 * meter)
    {
        debugLog(definition, "cylindrical hole face skipped because its deformed axis collapsed");
        return undefined;
    }
    targetDirection = normalize(targetDirection);

    const targetCenter = (targetStart + targetEnd) / 2;
    var deformedRadius = 0 * meter;
    for (var point in boundaryPoints)
    {
        const deformedPoint = deformPoint(context, definition, point);
        const axialPoint = targetCenter + targetDirection * dot(deformedPoint - targetCenter, targetDirection);
        deformedRadius = max(deformedRadius, norm(deformedPoint - axialPoint));
    }
    if (deformedRadius < cylinderSurface.radius * 0.25)
        deformedRadius = cylinderSurface.radius;

    const profileBoundaryEdges = holeBoundaryProfileEdges(context, boundaryEdges, axisOrigin, axisDirection, minParameter, maxParameter);
    const toolSetback = max(max(cylinderSurface.radius * 8, definition.sourceData.radius * 2), 10 * millimeter);
    const halfAxisSpan = max(norm(targetEnd - targetStart) / 2, cylinderSurface.radius * 2) + toolSetback;
    return {
            "faces" : holeFace,
            "topCenter" : targetCenter + targetDirection * halfAxisSpan,
            "bottomCenter" : targetCenter - targetDirection * halfAxisSpan,
            "radius" : cylinderSurface.radius,
            "deformedRadius" : deformedRadius,
            "boundaryEdges" : boundaryEdges,
            "profileBoundaryEdges" : profileBoundaryEdges,
            "toolLength" : halfAxisSpan * 2
        };
}

function holeBoundaryProfileEdges(context is Context, boundaryEdges is array, axisOrigin is Vector, axisDirection is Vector, minParameter, maxParameter) returns array
{
    const holeSpan = maxParameter - minParameter;
    if (size(boundaryEdges) < 2 || holeSpan <= 1e-8 * meter)
        return [];

    var startEdges = [];
    var endEdges = [];
    for (var edge in boundaryEdges)
    {
        var edgeMin = inf * meter;
        var edgeMax = -inf * meter;
        var sampled = false;
        try silent
        {
            const tangentLines = evEdgeTangentLines(context, {
                        "edge" : edge,
                        "parameters" : [0, 0.5, 1],
                        "arcLengthParameterization" : true
                    });
            for (var tangentLine in tangentLines)
            {
                const edgeParameter = dot(tangentLine.origin - axisOrigin, axisDirection);
                edgeMin = min(edgeMin, edgeParameter);
                edgeMax = max(edgeMax, edgeParameter);
                sampled = true;
            }
        }
        if (!sampled)
            continue;

        if (edgeMax - edgeMin > holeSpan * 0.25)
            continue;

        const edgeCenterParameter = (edgeMin + edgeMax) / 2;
        if (abs(edgeCenterParameter - minParameter) <= abs(edgeCenterParameter - maxParameter))
            startEdges = append(startEdges, edge);
        else
            endEdges = append(endEdges, edge);
    }

    if (size(startEdges) == 0 || size(endEdges) == 0)
        return [];

    return [startEdges, endEdges];
}

function isLikelyHoleCylinder(context is Context, face is Query) returns boolean
{
    var closedBoundaryCount = 0;
    for (var edge in evaluateQuery(context, qLoopEdges(face)))
    {
        var edgeIsClosed = false;
        try silent
        {
            edgeIsClosed = isClosed(context, edge);
        }
        if (edgeIsClosed)
            closedBoundaryCount += 1;
    }

    var concaveBoundaryCount = 0;
    try silent
    {
        concaveBoundaryCount = evaluateQueryCount(context, qEdgeConvexityTypeFilter(qLoopEdges(face), EdgeConvexityType.CONCAVE));
    }
    return closedBoundaryCount >= 1 && concaveBoundaryCount > 0;
}

function sampleFaceBoundaryPoints(context is Context, face is Query) returns array
{
    var points = [];
    for (var edge in evaluateQuery(context, qLoopEdges(face)))
    {
        try silent
        {
            const tangentLines = evEdgeTangentLines(context, {
                        "edge" : edge,
                        "parameters" : [0, 0.25, 0.5, 0.75],
                        "arcLengthParameterization" : true
                    });
            for (var tangentLine in tangentLines)
            {
                points = append(points, tangentLine.origin);
            }
        }
    }
    return points;
}

function deformVertex(context is Context, id is Id, definition is map, vertex is Query) returns Query
{
    const sourcePoint = evVertexPoint(context, { "vertex" : vertex });
    opPoint(context, id, {
                "point" : deformPoint(context, definition, sourcePoint)
            });
    return qCreatedBy(id);
}

function deformEdge(context is Context, id is Id, definition is map, edge is Query) returns Query
{
    // Pure FFD path: deform the B-spline control net directly.
    // If the kernel cannot produce a B-spline approximation or cannot create the result,
    // this edge cannot be deformed and the feature will report an error for it.
    const sourceCurve = evApproximateBSplineCurve(context, { "edge" : edge });
    const deformedCurve = deformBSplineCurve(context, definition, sourceCurve);
    opCreateBSplineCurve(context, id, { "bSplineCurve" : deformedCurve });
    return qCreatedBy(id, EntityType.EDGE);
}

function edgeSampleCountForLength(edgeLength, sampleStep) returns number
{
    return min(MAX_EDGE_SAMPLE_COUNT, max(3, ceil(edgeLength / sampleStep)));
}

function sourceStationForLocalX(definition is map, localX)
{
    if (definition.pathData.hasPath == true && definition.stretchAlongPath != true)
    {
        if (definition.pathData.isSurface == true)
            return surfaceParameterComponent(definition.sourceData.anchorStation + localX / definition.pathData.uLength, definition.pathData.isUPeriodic == true);
        return definition.sourceData.anchorStation + localX / definition.pathData.length;
    }
    return normalizedBetween(localX, definition.sourceData.minX, definition.sourceData.maxX);
}

function sourceLocalXForStation(definition is map, station is number)
{
    if (definition.pathData.hasPath == true && definition.stretchAlongPath != true)
    {
        var stationDelta = station - definition.sourceData.anchorStation;
        if (definition.pathData.isSurface == true)
        {
            if (definition.pathData.isUPeriodic == true)
                stationDelta = shortestPeriodicParameterDelta(stationDelta);
            return stationDelta * definition.pathData.uLength;
        }
        return stationDelta * definition.pathData.length;
    }

    return definition.sourceData.minX + (definition.sourceData.maxX - definition.sourceData.minX) * clamp01(station);
}

function shortestPeriodicParameterDelta(delta is number) returns number
{
    if (delta > 0.5)
        return delta - 1;
    if (delta < -0.5)
        return delta + 1;
    return delta;
}

function deformPoint(context is Context, definition is map, point is Vector) returns Vector
{
    const source = definition.sourceData;
    const local = worldToLocal(point, source.origin, source.xAxis, source.yAxis, source.zAxis);
    if (definition.pathData.isSurface == true)
        return deformPointToSurface(context, definition, local);

    var rawStation = sourceStationForLocalX(definition, local[0]);
    var t = rawStation;
    var overflow = 0 * meter;

    if (t < 0)
    {
        overflow = t * definition.pathData.length;
        t = 0;
    }
    else if (t > 1)
    {
        overflow = (t - 1) * definition.pathData.length;
        t = 1;
    }

    const section = crossSectionAt(definition.crossSections, t);
    const angle = section.rotation;

    const y = local[1] * section.scale;
    const z = local[2] * section.scale;
    const rotatedY = y * cos(angle) - z * sin(angle);
    const rotatedZ = y * sin(angle) + z * cos(angle);

    const frame = pathFrameAt(context, definition.pathData, t);
    const center = frame.origin + frame.yAxis * section.centerOffsetY + frame.zAxis * section.centerOffsetZ + frame.xAxis * overflow;
    return center + frame.yAxis * rotatedY + frame.zAxis * rotatedZ +
        guideCageOffsetAt(definition, section, frame, local, t) +
        cageOffsetAt(definition, section, frame, local);
}

function deformPointToSurface(context is Context, definition is map, local is Vector) returns Vector
{
    const source = definition.sourceData;
    if (definition.stretchAlongPath == true)
    {
        const t = normalizedBetween(local[0], source.minX, source.maxX);
        const section = crossSectionAt(definition.crossSections, t);
        const angle = section.rotation;
        const sourceCenterY = (source.minY + source.maxY) / 2;
        const y = (local[1] - sourceCenterY) * section.scale;
        const z = local[2] * section.scale;
        const rotatedY = y * cos(angle) - z * sin(angle);
        const rotatedZ = y * sin(angle) + z * cos(angle);
        const surfaceY = sourceCenterY + rotatedY + getSectionCenterOffsetY(section);
        const v = surfaceParameterComponent(normalizedBetween(surfaceY, source.minY, source.maxY), definition.pathData.isVPeriodic == true);
        const frame = surfaceFrameAt(context, definition.pathData, t, v);
        const center = frame.origin + frame.zAxis * getSectionCenterOffsetZ(section);

        return center + frame.zAxis * rotatedZ +
            guideCageOffsetAt(definition, section, frame, local, t) +
            cageOffsetAt(definition, section, frame, local);
    }

    var rawU = source.anchorStation + local[0] / definition.pathData.uLength;
    var u = surfaceParameterComponent(rawU, definition.pathData.isUPeriodic == true);
    var overflow = 0 * meter;
    if (definition.pathData.isUPeriodic != true)
    {
        if (rawU < 0)
            overflow = rawU * definition.pathData.uLength;
        else if (rawU > 1)
            overflow = (rawU - 1) * definition.pathData.uLength;
    }

    const section = crossSectionAt(definition.crossSections, u);
    const angle = section.rotation;
    const y = local[1] * section.scale;
    const z = local[2] * section.scale;
    const rotatedY = y * cos(angle) - z * sin(angle);
    const rotatedZ = y * sin(angle) + z * cos(angle);
    const rawV = source.anchorV + (rotatedY + getSectionCenterOffsetY(section)) / definition.pathData.vLength;
    const v = surfaceParameterComponent(rawV, definition.pathData.isVPeriodic == true);
    const frame = surfaceFrameAt(context, definition.pathData, u, v);
    const center = frame.origin + frame.xAxis * overflow + frame.zAxis * getSectionCenterOffsetZ(section);

    return center + frame.zAxis * rotatedZ +
        guideCageOffsetAt(definition, section, frame, local, u) +
        cageOffsetAt(definition, section, frame, local);
}

function crossSectionAt(sections is array, t is number) returns map
{
    var ordered = sort(sections, function(a, b)
    {
        return a.station - b.station;
    });

    if (size(ordered) == 1 || t <= ordered[0].station)
        return sectionState(ordered[0]);

    const lastIndex = size(ordered) - 1;
    if (t >= ordered[lastIndex].station)
        return sectionState(ordered[lastIndex]);

    for (var i = 0; i < lastIndex; i += 1)
    {
        const a = ordered[i];
        const b = ordered[i + 1];
        if (t >= a.station && t <= b.station)
        {
            const span = max(1e-8, b.station - a.station);
            const f = smoothStep((t - a.station) / span);
            return {
                    "deformType" : (getSectionDeformType(a) == DeformType.LATTICE_CAGE || getSectionDeformType(b) == DeformType.LATTICE_CAGE) ? DeformType.LATTICE_CAGE : DeformType.RIGID_PROFILE,
                    "rotation" : (1 - f) * a.rotation + f * b.rotation,
                    "scale" : (1 - f) * a.scale + f * b.scale,
                    "centerOffsetY" : (1 - f) * getSectionCenterOffsetY(a) + f * getSectionCenterOffsetY(b),
                    "centerOffsetZ" : (1 - f) * getSectionCenterOffsetZ(a) + f * getSectionCenterOffsetZ(b),
                    "cageCorner0OffsetX" : (1 - f) * getCageCornerOffsetX(a, 0) + f * getCageCornerOffsetX(b, 0),
                    "cageCorner0OffsetY" : (1 - f) * getCageCornerOffsetY(a, 0) + f * getCageCornerOffsetY(b, 0),
                    "cageCorner0OffsetZ" : (1 - f) * getCageCornerOffsetZ(a, 0) + f * getCageCornerOffsetZ(b, 0),
                    "cageCorner1OffsetX" : (1 - f) * getCageCornerOffsetX(a, 1) + f * getCageCornerOffsetX(b, 1),
                    "cageCorner1OffsetY" : (1 - f) * getCageCornerOffsetY(a, 1) + f * getCageCornerOffsetY(b, 1),
                    "cageCorner1OffsetZ" : (1 - f) * getCageCornerOffsetZ(a, 1) + f * getCageCornerOffsetZ(b, 1),
                    "cageCorner2OffsetX" : (1 - f) * getCageCornerOffsetX(a, 2) + f * getCageCornerOffsetX(b, 2),
                    "cageCorner2OffsetY" : (1 - f) * getCageCornerOffsetY(a, 2) + f * getCageCornerOffsetY(b, 2),
                    "cageCorner2OffsetZ" : (1 - f) * getCageCornerOffsetZ(a, 2) + f * getCageCornerOffsetZ(b, 2),
                    "cageCorner3OffsetX" : (1 - f) * getCageCornerOffsetX(a, 3) + f * getCageCornerOffsetX(b, 3),
                    "cageCorner3OffsetY" : (1 - f) * getCageCornerOffsetY(a, 3) + f * getCageCornerOffsetY(b, 3),
                    "cageCorner3OffsetZ" : (1 - f) * getCageCornerOffsetZ(a, 3) + f * getCageCornerOffsetZ(b, 3)
                };
        }
    }

    return sectionState(defaultCrossSectionAt(0.0));
}

function sectionState(section is map) returns map
{
    return {
            "deformType" : getSectionDeformType(section),
            "rotation" : section.rotation,
            "scale" : section.scale,
            "centerOffsetY" : getSectionCenterOffsetY(section),
            "centerOffsetZ" : getSectionCenterOffsetZ(section),
            "cageCorner0OffsetX" : getCageCornerOffsetX(section, 0),
            "cageCorner0OffsetY" : getCageCornerOffsetY(section, 0),
            "cageCorner0OffsetZ" : getCageCornerOffsetZ(section, 0),
            "cageCorner1OffsetX" : getCageCornerOffsetX(section, 1),
            "cageCorner1OffsetY" : getCageCornerOffsetY(section, 1),
            "cageCorner1OffsetZ" : getCageCornerOffsetZ(section, 1),
            "cageCorner2OffsetX" : getCageCornerOffsetX(section, 2),
            "cageCorner2OffsetY" : getCageCornerOffsetY(section, 2),
            "cageCorner2OffsetZ" : getCageCornerOffsetZ(section, 2),
            "cageCorner3OffsetX" : getCageCornerOffsetX(section, 3),
            "cageCorner3OffsetY" : getCageCornerOffsetY(section, 3),
            "cageCorner3OffsetZ" : getCageCornerOffsetZ(section, 3)
        };
}

function cageOffsetAt(definition is map, section is map, frame is map, local is Vector) returns Vector
{
    if (getSectionDeformType(section) != DeformType.LATTICE_CAGE)
        return vector(0, 0, 0) * meter;

    const source = definition.sourceData;
    const u = normalizedBetween(local[1], source.minY, source.maxY);
    const v = normalizedBetween(local[2], source.minZ, source.maxZ);
    const offset0 = cageCornerOffsetVector(section, 0, frame);
    const offset1 = cageCornerOffsetVector(section, 1, frame);
    const offset2 = cageCornerOffsetVector(section, 2, frame);
    const offset3 = cageCornerOffsetVector(section, 3, frame);
    return offset0 * (1 - u) * (1 - v) + offset1 * u * (1 - v) + offset2 * u * v + offset3 * (1 - u) * v;
}

function guideCageOffsetAt(definition is map, section is map, frame is map, local is Vector, station is number) returns Vector
{
    if (definition.guideProfile == undefined)
        return vector(0, 0, 0) * meter;

    const envelope = guideEnvelopeAt(definition.guideProfile, station);
    if (envelope == undefined)
        return vector(0, 0, 0) * meter;

    if (envelope.hasGuideOffset == true)
        return frame.yAxis * envelope.offsetY + frame.zAxis * envelope.offsetZ;

    const source = definition.sourceData;
    const u = normalizedBetween(local[1], source.minY, source.maxY);
    const v = normalizedBetween(local[2], source.minZ, source.maxZ);
    const offset0 = guideCornerOffsetVector(source, section, frame, envelope, 0);
    const offset1 = guideCornerOffsetVector(source, section, frame, envelope, 1);
    const offset2 = guideCornerOffsetVector(source, section, frame, envelope, 2);
    const offset3 = guideCornerOffsetVector(source, section, frame, envelope, 3);
    return offset0 * (1 - u) * (1 - v) + offset1 * u * (1 - v) + offset2 * u * v + offset3 * (1 - u) * v;
}

function guideCornerOffsetVector(sourceData is map, section is map, frame is map, envelope is map, cornerIndex is number) returns Vector
{
    const userOffset = cageCornerOffsetVector(section, cornerIndex, frame);
    const currentVector = sectionBaseCornerVector(sourceData, frame, section, cornerIndex, section.scale) + userOffset;
    const currentY = dot(currentVector, frame.yAxis);
    const currentZ = dot(currentVector, frame.zAxis);
    const guideY = guideCornerY(envelope, cornerIndex);
    const guideZ = guideCornerZ(envelope, cornerIndex);
    return frame.yAxis * (guideY - currentY) + frame.zAxis * (guideZ - currentZ);
}

function guideCornerY(envelope is map, cornerIndex is number)
{
    if (cornerIndex == 0 || cornerIndex == 3)
        return envelope.minY;
    return envelope.maxY;
}

function guideCornerZ(envelope is map, cornerIndex is number)
{
    if (cornerIndex == 0 || cornerIndex == 1)
        return envelope.minZ;
    return envelope.maxZ;
}

function normalizedBetween(value, minValue, maxValue) returns number
{
    const span = maxValue - minValue;
    if (abs(span) <= 1e-8 * meter)
        return 0.5;
    return clamp01((value - minValue) / span);
}

function sectionCageBasePoint(context is Context, pathData is map, sourceData is map, section is map, cornerIndex is number) returns Vector
{
    const frame = sectionFrameAt(context, pathData, section);
    const y = cageCornerBaseY(sourceData, cornerIndex) * section.scale;
    const z = cageCornerBaseZ(sourceData, cornerIndex) * section.scale;
    const angle = section.rotation;
    const rotatedY = y * cos(angle) - z * sin(angle);
    const rotatedZ = y * sin(angle) + z * cos(angle);
    return frame.origin + frame.yAxis * rotatedY + frame.zAxis * rotatedZ;
}

function cageCornerBaseY(sourceData is map, cornerIndex is number)
{
    if (cornerIndex == 0 || cornerIndex == 3)
        return sourceData.minY;
    return sourceData.maxY;
}

function cageCornerBaseZ(sourceData is map, cornerIndex is number)
{
    if (cornerIndex == 0 || cornerIndex == 1)
        return sourceData.minZ;
    return sourceData.maxZ;
}

function cageCornerOffsetVector(section is map, cornerIndex is number, frame is map) returns Vector
{
    return frame.xAxis * getCageCornerOffsetX(section, cornerIndex) +
        frame.yAxis * getCageCornerOffsetY(section, cornerIndex) +
        frame.zAxis * getCageCornerOffsetZ(section, cornerIndex);
}

function cageCornerOffsetLocalVector(section is map, cornerIndex is number) returns Vector
{
    return vector(
        getCageCornerOffsetX(section, cornerIndex),
        getCageCornerOffsetY(section, cornerIndex),
        getCageCornerOffsetZ(section, cornerIndex));
}

function setCageCornerOffset(section is map, cornerIndex is number, offset is Vector, frame is map) returns map
{
    const offsetX = dot(offset, frame.xAxis);
    const offsetY = dot(offset, frame.yAxis);
    const offsetZ = dot(offset, frame.zAxis);

    if (cornerIndex == 0)
    {
        section.cageCorner0OffsetX = offsetX;
        section.cageCorner0OffsetY = offsetY;
        section.cageCorner0OffsetZ = offsetZ;
    }
    else if (cornerIndex == 1)
    {
        section.cageCorner1OffsetX = offsetX;
        section.cageCorner1OffsetY = offsetY;
        section.cageCorner1OffsetZ = offsetZ;
    }
    else if (cornerIndex == 2)
    {
        section.cageCorner2OffsetX = offsetX;
        section.cageCorner2OffsetY = offsetY;
        section.cageCorner2OffsetZ = offsetZ;
    }
    else
    {
        section.cageCorner3OffsetX = offsetX;
        section.cageCorner3OffsetY = offsetY;
        section.cageCorner3OffsetZ = offsetZ;
    }
    return section;
}

function getSectionDeformType(section is map)
{
    if (section.deformType == undefined)
        return DeformType.RIGID_PROFILE;
    return section.deformType;
}

function getCageCornerOffsetX(section is map, cornerIndex is number)
{
    if (cornerIndex == 0 && section.cageCorner0OffsetX != undefined)
        return section.cageCorner0OffsetX;
    if (cornerIndex == 1 && section.cageCorner1OffsetX != undefined)
        return section.cageCorner1OffsetX;
    if (cornerIndex == 2 && section.cageCorner2OffsetX != undefined)
        return section.cageCorner2OffsetX;
    if (cornerIndex == 3 && section.cageCorner3OffsetX != undefined)
        return section.cageCorner3OffsetX;
    return 0 * meter;
}

function getCageCornerOffsetY(section is map, cornerIndex is number)
{
    if (cornerIndex == 0 && section.cageCorner0OffsetY != undefined)
        return section.cageCorner0OffsetY;
    if (cornerIndex == 1 && section.cageCorner1OffsetY != undefined)
        return section.cageCorner1OffsetY;
    if (cornerIndex == 2 && section.cageCorner2OffsetY != undefined)
        return section.cageCorner2OffsetY;
    if (cornerIndex == 3 && section.cageCorner3OffsetY != undefined)
        return section.cageCorner3OffsetY;
    return 0 * meter;
}

function getCageCornerOffsetZ(section is map, cornerIndex is number)
{
    if (cornerIndex == 0 && section.cageCorner0OffsetZ != undefined)
        return section.cageCorner0OffsetZ;
    if (cornerIndex == 1 && section.cageCorner1OffsetZ != undefined)
        return section.cageCorner1OffsetZ;
    if (cornerIndex == 2 && section.cageCorner2OffsetZ != undefined)
        return section.cageCorner2OffsetZ;
    if (cornerIndex == 3 && section.cageCorner3OffsetZ != undefined)
        return section.cageCorner3OffsetZ;
    return 0 * meter;
}

function smoothStep(value is number) returns number
{
    const t = clamp01(value);
    return t * t * t * (t * (t * 6 - 15) + 10);
}

function guideEnvelopeAt(profile is array, t is number)
{
    const count = size(profile);
    if (count == 0)
        return undefined;

    const raw = t * (count - 1);
    var lower = floor(raw);
    var upper = ceil(raw);
    lower = max(0, min(count - 1, lower));
    upper = max(0, min(count - 1, upper));

    var lowerEnvelope = nearestDefinedGuideEnvelope(profile, lower, -1);
    var upperEnvelope = nearestDefinedGuideEnvelope(profile, upper, 1);

    if (lowerEnvelope == undefined)
        lowerEnvelope = nearestDefinedGuideEnvelope(profile, lower, 1);
    if (upperEnvelope == undefined)
        upperEnvelope = nearestDefinedGuideEnvelope(profile, upper, -1);

    if (lowerEnvelope == undefined && upperEnvelope == undefined)
        return undefined;
    if (lowerEnvelope == undefined)
        return upperEnvelope;
    if (upperEnvelope == undefined)
        return lowerEnvelope;

    const f = raw - floor(raw);
    var hasGuideOffset = lowerEnvelope.hasGuideOffset == true || upperEnvelope.hasGuideOffset == true;
    var offsetY = 0 * meter;
    var offsetZ = 0 * meter;
    if (hasGuideOffset)
    {
        const lowerOffsetY = lowerEnvelope.hasGuideOffset == true ? lowerEnvelope.offsetY : 0 * meter;
        const lowerOffsetZ = lowerEnvelope.hasGuideOffset == true ? lowerEnvelope.offsetZ : 0 * meter;
        const upperOffsetY = upperEnvelope.hasGuideOffset == true ? upperEnvelope.offsetY : 0 * meter;
        const upperOffsetZ = upperEnvelope.hasGuideOffset == true ? upperEnvelope.offsetZ : 0 * meter;
        offsetY = (1 - f) * lowerOffsetY + f * upperOffsetY;
        offsetZ = (1 - f) * lowerOffsetZ + f * upperOffsetZ;
    }

    return {
            "minY" : (1 - f) * lowerEnvelope.minY + f * upperEnvelope.minY,
            "maxY" : (1 - f) * lowerEnvelope.maxY + f * upperEnvelope.maxY,
            "minZ" : (1 - f) * lowerEnvelope.minZ + f * upperEnvelope.minZ,
            "maxZ" : (1 - f) * lowerEnvelope.maxZ + f * upperEnvelope.maxZ,
            "samples" : [],
            "hasGuideOffset" : hasGuideOffset,
            "offsetY" : offsetY,
            "offsetZ" : offsetZ
        };
}

function nearestDefinedGuideEnvelope(profile is array, startIndex is number, direction is number)
{
    var i = startIndex;
    while (i >= 0 && i < size(profile))
    {
        if (profile[i] != undefined)
            return profile[i];
        i = i + direction;
    }
    return undefined;
}

function pathFrameAt(context is Context, pathData is map, t is number) returns map
{
    if (pathData.isSurface == true)
        return surfaceFrameAt(context, pathData, t, 0.5);

    if (pathData.hasPath != true)
    {
        return {
                "origin" : pathData.origin + pathData.xAxis * (pathData.length * clamp01(t)),
                "xAxis" : pathData.xAxis,
                "yAxis" : pathData.yAxis,
                "zAxis" : pathData.zAxis
            };
    }

    const line = evPathTangentLines(context, pathData.path, [t]).tangentLines[0];
    const xAxis = line.direction;
    const zAxis = safeNormalForDirection(pathData.zAxis, xAxis);
    const yAxis = normalize(cross(zAxis, xAxis));

    return {
            "origin" : line.origin,
            "xAxis" : xAxis,
            "yAxis" : yAxis,
            "zAxis" : zAxis
        };
}

function surfaceIsoLength(context is Context, targetFace is Query, alongU is boolean, fixedParameter is number)
{
    const sampleCount = 17;
    var previousPoint;
    var hasPreviousPoint = false;
    var totalLength = 0 * meter;

    for (var i = 0; i < sampleCount; i += 1)
    {
        const parameter = i / (sampleCount - 1);
        const faceParameter = alongU ? vector(parameter, fixedParameter) : vector(fixedParameter, parameter);
        const point = evFaceTangentPlaneSafe(context, targetFace, faceParameter).origin;
        if (hasPreviousPoint)
            totalLength += norm(point - previousPoint);
        previousPoint = point;
        hasPreviousPoint = true;
    }

    return totalLength;
}

function surfaceParameterRangeForCorners(context is Context, pathData is map, corners is array) returns map
{
    var uValues = [];
    var vValues = [];

    for (var corner in corners)
    {
        const parameter = projectedSurfaceParameter(context, pathData, corner);
        if (parameter == undefined)
            continue;

        uValues = append(uValues, parameter[0]);
        vValues = append(vValues, parameter[1]);
    }

    if (size(uValues) == 0 || size(vValues) == 0)
        return {
                "uStart" : 0.0,
                "uEnd" : 1.0,
                "vCenter" : 0.5
            };

    const uRange = parameterRangeForValues(uValues, pathData.isUPeriodic == true);
    const vRange = parameterRangeForValues(vValues, pathData.isVPeriodic == true);
    return {
            "uStart" : uRange.start,
            "uEnd" : uRange.end,
            "vCenter" : parameterRangeCenter(vRange, pathData.isVPeriodic == true)
        };
}

function projectedSurfaceParameter(context is Context, pathData is map, point is Vector)
{
    var distanceResult;
    var succeeded = false;
    try silent
    {
        distanceResult = evDistance(context, {
                    "side0" : point,
                    "side1" : pathData.face
                });
        succeeded = true;
    }
    if (!succeeded)
        return undefined;

    var u = distanceResult.sides[1].parameter[0];
    if (pathData.flipSurfaceU == true)
        u = 1 - u;
    const v = distanceResult.sides[1].parameter[1];
    return vector(
        surfaceParameterComponent(u, pathData.isUPeriodic == true),
        surfaceParameterComponent(v, pathData.isVPeriodic == true));
}

function parameterRangeForValues(values is array, isPeriodic is boolean) returns map
{
    if (size(values) == 0)
        return {
                "start" : 0.0,
                "end" : 1.0,
                "wraps" : false
            };

    var ordered = sort(values, function(a, b)
    {
        return a - b;
    });

    if (size(ordered) == 1)
    {
        const value = surfaceParameterComponent(ordered[0], isPeriodic == true);
        return {
                "start" : value,
                "end" : value,
                "wraps" : false
            };
    }

    if (isPeriodic != true)
    {
        return {
                "start" : clamp01(ordered[0]),
                "end" : clamp01(ordered[size(ordered) - 1]),
                "wraps" : false
            };
    }

    var largestGap = -1.0;
    var startIndex = 0;
    const lastIndex = size(ordered) - 1;
    for (var i = 0; i <= lastIndex; i += 1)
    {
        const current = ordered[i];
        const next = i == lastIndex ? ordered[0] + 1 : ordered[i + 1];
        const gap = next - current;
        if (gap > largestGap)
        {
            largestGap = gap;
            startIndex = (i + 1) % size(ordered);
        }
    }

    const endIndex = startIndex == 0 ? lastIndex : startIndex - 1;
    const rangeStart = surfaceParameterComponent(ordered[startIndex], true);
    const rangeEnd = surfaceParameterComponent(ordered[endIndex], true);
    return {
            "start" : rangeStart,
            "end" : rangeEnd,
            "wraps" : rangeStart > rangeEnd
        };
}

function parameterRangeCenter(range is map, isPeriodic is boolean) returns number
{
    if (isPeriodic == true && range.wraps == true)
        return surfaceParameterComponent((range.start + range.end + 1) / 2, true);
    return surfaceParameterComponent((range.start + range.end) / 2, isPeriodic);
}

function surfaceFrameAt(context is Context, pathData is map, u is number, v is number) returns map
{
    const rawU = pathData.flipSurfaceU == true ? 1 - u : u;
    const surfaceU = surfaceParameterComponent(rawU, pathData.isUPeriodic == true);
    const surfaceV = surfaceParameterComponent(v, pathData.isVPeriodic == true);
    return surfaceFrameFromFaceParameter(context, pathData.face, vector(surfaceU, surfaceV), pathData.flipSurfaceU == true);
}

function surfaceParameterComponent(value is number, isPeriodic is boolean) returns number
{
    if (isPeriodic == true)
    {
        var wrapped = value - floor(value);
        if (wrapped < 0)
            wrapped += 1;
        return wrapped;
    }
    return clamp01(value);
}

function surfaceFrameFromFaceParameter(context is Context, targetFace is Query, parameter is Vector, flipU is boolean) returns map
{
    const tangentPlane = evFaceTangentPlaneSafe(context, targetFace, parameter);
    var xAxis = tangentPlane.x;
    if (flipU == true)
        xAxis = -xAxis;
    const zAxis = tangentPlane.normal;
    const yAxis = normalize(cross(zAxis, xAxis));
    return {
            "origin" : tangentPlane.origin,
            "xAxis" : xAxis,
            "yAxis" : yAxis,
            "zAxis" : zAxis
        };
}

function evFaceTangentPlaneSafe(context is Context, targetFace is Query, parameter is Vector) returns Plane
{
    var tangentPlane;
    var succeeded = false;
    try silent
    {
        tangentPlane = evFaceTangentPlane(context, {
                    "face" : targetFace,
                    "parameter" : parameter
                });
        succeeded = true;
    }
    if (succeeded)
        return tangentPlane;

    const nudgedParameter = vector(
        clampBetween(parameter[0], SURFACE_PARAMETER_EPSILON, 1 - SURFACE_PARAMETER_EPSILON),
        clampBetween(parameter[1], SURFACE_PARAMETER_EPSILON, 1 - SURFACE_PARAMETER_EPSILON));
    try silent
    {
        tangentPlane = evFaceTangentPlane(context, {
                    "face" : targetFace,
                    "parameter" : nudgedParameter
                });
        succeeded = true;
    }
    if (succeeded)
        return tangentPlane;

    try silent
    {
        tangentPlane = evFaceTangentPlane(context, {
                    "face" : targetFace,
                    "parameter" : vector(0.5, 0.5)
                });
        succeeded = true;
    }
    if (succeeded)
        return tangentPlane;

    throw regenError("The selected target face could not be evaluated for surface deformation.", ["path"], targetFace);
}

function nearestSurfaceUParameter(context is Context, pathData is map, point is Vector) returns number
{
    var distanceResult;
    var succeeded = false;
    try silent
    {
        distanceResult = evDistance(context, {
                    "side0" : point,
                    "side1" : pathData.face
                });
        succeeded = true;
    }
    if (!succeeded)
        return 0.5;

    var u = distanceResult.sides[1].parameter[0];
    if (pathData.flipSurfaceU == true)
        u = 1 - u;
    return surfaceParameterComponent(u, pathData.isUPeriodic == true);
}

function sectionFrameAt(context is Context, pathData is map, section is map) returns map
{
    const frame = pathFrameAt(context, pathData, section.station);
    const center = frame.origin + frame.yAxis * getSectionCenterOffsetY(section) + frame.zAxis * getSectionCenterOffsetZ(section);
    return {
            "origin" : center,
            "xAxis" : frame.xAxis,
            "yAxis" : frame.yAxis,
            "zAxis" : frame.zAxis
        };
}

function sectionProfileFrameAt(context is Context, pathData is map, section is map) returns map
{
    const frame = sectionFrameAt(context, pathData, section);
    const axes = sectionProfileAxes(frame, section);
    return {
            "origin" : frame.origin,
            "xAxis" : axes.xAxis,
            "yAxis" : axes.yAxis,
            "zAxis" : axes.zAxis
        };
}

function sectionProfileAxes(frame is map, section is map) returns map
{
    const rotation = rotationAround(line(frame.origin, frame.xAxis), section.rotation);
    return {
            "xAxis" : frame.xAxis,
            "yAxis" : rotation.linear * frame.yAxis,
            "zAxis" : rotation.linear * frame.zAxis
        };
}

function sectionManipulatorFrameAt(context is Context, pathData is map, sourceData is map, section is map) returns map
{
    const fallbackFrame = sectionProfileFrameAt(context, pathData, section);
    if (getSectionDeformType(section) != DeformType.LATTICE_CAGE)
        return fallbackFrame;

    const p0 = sectionCurrentCageCornerPoint(context, pathData, sourceData, section, 0);
    const p1 = sectionCurrentCageCornerPoint(context, pathData, sourceData, section, 1);
    const p2 = sectionCurrentCageCornerPoint(context, pathData, sourceData, section, 2);
    const p3 = sectionCurrentCageCornerPoint(context, pathData, sourceData, section, 3);
    const yVector = ((p1 + p2) / 2) - ((p0 + p3) / 2);
    const zVector = ((p2 + p3) / 2) - ((p0 + p1) / 2);
    if (norm(yVector) < 1e-8 * meter || norm(zVector) < 1e-8 * meter)
        return fallbackFrame;

    var yAxis = normalize(yVector);
    var zProjected = zVector - yAxis * dot(zVector, yAxis);
    if (norm(zProjected) < 1e-8 * meter)
        return fallbackFrame;
    var zAxis = normalize(zProjected);

    var xAxis = normalize(cross(yAxis, zAxis));
    if (dot(xAxis, fallbackFrame.xAxis) < 0)
        xAxis = -xAxis;
    zAxis = normalize(cross(xAxis, yAxis));

    return {
            "origin" : fallbackFrame.origin,
            "xAxis" : xAxis,
            "yAxis" : yAxis,
            "zAxis" : zAxis
        };
}

function sectionCurrentCageCornerPoint(context is Context, pathData is map, sourceData is map, section is map, cornerIndex is number) returns Vector
{
    const frame = sectionFrameAt(context, pathData, section);
    return sectionCageBasePoint(context, pathData, sourceData, section, cornerIndex) + cageCornerOffsetVector(section, cornerIndex, frame);
}

function applyTriadRotationToSectionCage(context is Context, pathData is map, sourceData is map, oldSection is map, newSection is map, oldProfileFrame is map, triadTransform is Transform) returns map
{
    var cagedSection = newSection;
    cagedSection.deformType = DeformType.LATTICE_CAGE;
    const newFrame = sectionFrameAt(context, pathData, cagedSection);
    const newCenter = newFrame.origin;
    const triadLinear = transpose(triadTransform.linear);

    for (var cornerIndex = 0; cornerIndex < 4; cornerIndex += 1)
    {
        const oldCorner = sectionCurrentCageCornerPoint(context, pathData, sourceData, oldSection, cornerIndex);
        const oldLocal = worldToLocal(oldCorner, oldProfileFrame.origin, oldProfileFrame.xAxis, oldProfileFrame.yAxis, oldProfileFrame.zAxis);
        const newLocal = triadLinear * oldLocal;
        const targetCorner = newCenter + oldProfileFrame.xAxis * newLocal[0] + oldProfileFrame.yAxis * newLocal[1] + oldProfileFrame.zAxis * newLocal[2];
        const baseCorner = sectionCageBasePoint(context, pathData, sourceData, cagedSection, cornerIndex);
        cagedSection = setCageCornerOffset(cagedSection, cornerIndex, targetCorner - baseCorner, newFrame);
    }

    return cagedSection;
}

function sectionCenterPoint(context is Context, pathData is map, section is map) returns Vector
{
    return sectionFrameAt(context, pathData, section).origin;
}

function getSectionDx(section is map)
{
    if (section.dx == undefined)
        return 0 * meter;
    return section.dx;
}

function getSectionDy(section is map)
{
    if (section.dy == undefined)
        return 0 * meter;
    return section.dy;
}

function getSectionDz(section is map)
{
    if (section.dz == undefined)
        return 0 * meter;
    return section.dz;
}

function getSectionCenterOffsetY(section is map)
{
    if (section.centerOffsetY == undefined)
        return 0 * meter;
    return section.centerOffsetY;
}

function getSectionCenterOffsetZ(section is map)
{
    if (section.centerOffsetZ == undefined)
        return 0 * meter;
    return section.centerOffsetZ;
}

function hasSectionReference(context is Context, section is map) returns boolean
{
    if (section.referenceVertex == undefined)
        return false;

    var hasReference = false;
    try silent
    {
        hasReference = evaluateQueryCount(context, section.referenceVertex) > 0;
    }
    return hasReference;
}

function getSectionReferenceOffsetX(section is map)
{
    if (section.referenceOffsetX == undefined)
        return 0 * meter;
    return section.referenceOffsetX;
}

function getSectionReferenceOffsetY(section is map)
{
    if (section.referenceOffsetY == undefined)
        return 0 * meter;
    return section.referenceOffsetY;
}

function getSectionReferenceOffsetZ(section is map)
{
    if (section.referenceOffsetZ == undefined)
        return 0 * meter;
    return section.referenceOffsetZ;
}

function safeNormalForDirection(normal is Vector, direction is Vector) returns Vector
{
    var zAxis = normal - direction * dot(normal, direction);
    if (norm(zAxis) < 1e-8)
        zAxis = perpendicularVector(direction);
    return normalize(zAxis);
}

function triadRollAngle(profileFrame is map, triadTransform is Transform)
{
    const triadLinear = transpose(triadTransform.linear);
    const localYAxis = triadLinear * Y_DIRECTION;
    const targetYAxis = profileFrame.xAxis * localYAxis[0] + profileFrame.yAxis * localYAxis[1] + profileFrame.zAxis * localYAxis[2];
    return signedAngleAroundAxis(profileFrame.yAxis, targetYAxis, profileFrame.xAxis);
}

function triadHasProfileTilt(triadTransform is Transform) returns boolean
{
    const triadLinear = transpose(triadTransform.linear);
    const localXAxis = triadLinear * X_DIRECTION;
    return norm(localXAxis - X_DIRECTION) > 1e-8;
}

function signedAngleAroundAxis(fromDirection is Vector, toDirection is Vector, axis is Vector)
{
    var fromProjected = fromDirection - axis * dot(fromDirection, axis);
    var toProjected = toDirection - axis * dot(toDirection, axis);
    if (norm(fromProjected) < 1e-8 || norm(toProjected) < 1e-8)
        return 0 * degree;

    fromProjected = normalize(fromProjected);
    toProjected = normalize(toProjected);
    var angle = angleBetween(fromProjected, toProjected);
    if (dot(axis, cross(fromProjected, toProjected)) < 0)
        angle = -angle;
    return angle;
}

function nearestPathFrameIndex(point is Vector, frames is array) returns number
{
    var bestIndex = 0;
    var bestDistance = inf * meter;
    for (var i = 0; i < size(frames); i += 1)
    {
        const distance = norm(point - frames[i].origin);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestIndex = i;
        }
    }
    return bestIndex;
}

function worldToLocal(point is Vector, origin is Vector, xAxis is Vector, yAxis is Vector, zAxis is Vector) returns Vector
{
    const offset = point - origin;
    return vector(dot(offset, xAxis), dot(offset, yAxis), dot(offset, zAxis));
}

function clamp01(value is number) returns number
{
    return max(0, min(1, value));
}

function clampBetween(value, minValue, maxValue)
{
    return max(minValue, min(maxValue, value));
}

function clampSectionIndex(index is number, sections is array) returns number
{
    if (size(sections) == 0)
        return 0;
    return floor(max(0, min(size(sections) - 1, index)));
}

function queryUnionOrNothing(queries is array) returns Query
{
    if (size(queries) == 0)
        return qNothing();
    return qUnion(queries);
}

function boxCenter(bounds is Box3d) returns Vector
{
    const minCorner = bounds.minCorner;
    const maxCorner = bounds.maxCorner;
    return vector(
        (minCorner[0] + maxCorner[0]) / 2,
        (minCorner[1] + maxCorner[1]) / 2,
        (minCorner[2] + maxCorner[2]) / 2);
}

function boxCorners(bounds is Box3d) returns array
{
    const minCorner = bounds.minCorner;
    const maxCorner = bounds.maxCorner;
    return [
            vector(minCorner[0], minCorner[1], minCorner[2]),
            vector(maxCorner[0], minCorner[1], minCorner[2]),
            vector(minCorner[0], maxCorner[1], minCorner[2]),
            vector(maxCorner[0], maxCorner[1], minCorner[2]),
            vector(minCorner[0], minCorner[1], maxCorner[2]),
            vector(maxCorner[0], minCorner[1], maxCorner[2]),
            vector(minCorner[0], maxCorner[1], maxCorner[2]),
            vector(maxCorner[0], maxCorner[1], maxCorner[2])
        ];
}

function appendQueryArray(target is array, queries is array) returns array
{
    for (var query in queries)
    {
        target = append(target, query);
    }
    return target;
}

function createDeformedBSplineFace(context is Context, id is Id, definition is map, sourceFace is Query) returns Query
{
    const approximation = evApproximateBSplineSurface(context, {
                "face" : sourceFace
            });

    // Extract to a mutable plain-map form and refine the knot structure so that
    // no single B-spline span straddles more than one cross-section station boundary.
    var mutableSurface = extractMutableSurface(approximation.bSplineSurface);
    mutableSurface = refineKnotsForDeformationDomain(definition, mutableSurface);

    const rowCount = size(mutableSurface.controlPoints);
    var columnCount = 0;
    if (rowCount > 0)
        columnCount = size(mutableSurface.controlPoints[0]);

    var deformedControlPoints = [];
    debugLog(definition, "B-spline approximation: uDegree=" ~ mutableSurface.uDegree ~ ", vDegree=" ~ mutableSurface.vDegree ~ ", control net=" ~ rowCount ~ "x" ~ columnCount);

    for (var row in mutableSurface.controlPoints)
    {
        var deformedRow = [];
        for (var controlPoint in row)
        {
            deformedRow = append(deformedRow, deformPoint(context, definition, controlPoint));
        }
        deformedControlPoints = append(deformedControlPoints, deformedRow);
    }

    var surfaceDefinition = {
        "uDegree" : mutableSurface.uDegree,
        "vDegree" : mutableSurface.vDegree,
        "isUPeriodic" : mutableSurface.isUPeriodic,
        "isVPeriodic" : mutableSurface.isVPeriodic,
        "controlPoints" : controlPointMatrix(deformedControlPoints),
        "uKnots" : knotArray(mutableSurface.uKnots),
        "vKnots" : knotArray(mutableSurface.vKnots)
    };

    if (mutableSurface.weights != undefined)
        surfaceDefinition.weights = matrix(mutableSurface.weights);

    var operationDefinition = {
        "bSplineSurface" : bSplineSurface(surfaceDefinition)
    };

    if (approximation.boundaryBSplineCurves != undefined && size(approximation.boundaryBSplineCurves) > 0)
    {
        var deformedBoundaryCurves = [];
        try silent
        {
            deformedBoundaryCurves = deformBSplineCurveArray(context, definition, approximation.boundaryBSplineCurves);
        }
        if (size(deformedBoundaryCurves) > 0)
        {
            debugLog(definition, "B-spline approximation boundary curves=" ~ size(deformedBoundaryCurves));
            operationDefinition.boundaryBSplineCurves = deformedBoundaryCurves;
        }
        else
        {
            debugLog(definition, "B-spline boundary curve deformation failed; skipping untrimmed approximation");
            return qNothing();
        }
    }
    else
    {
        debugLog(definition, "B-spline approximation has no boundary curves");
    }
    if (approximation.innerLoopBSplineCurves != undefined && size(approximation.innerLoopBSplineCurves) > 0)
        debugLog(definition, "B-spline approximation has " ~ size(approximation.innerLoopBSplineCurves) ~ " inner loop(s); opCreateBSplineSurface cannot recreate those holes in this fallback");

    opCreateBSplineSurface(context, id, operationDefinition);
    return qCreatedBy(id, EntityType.BODY);
}

function deformBSplineCurveArray(context is Context, definition is map, curves is array) returns array
{
    var deformedCurves = [];
    for (var curve in curves)
    {
        deformedCurves = append(deformedCurves, deformBSplineCurve(context, definition, curve));
    }
    return deformedCurves;
}

function deformBSplineCurve(context is Context, definition is map, curve is map)
{
    var deformedControlPoints = [];
    for (var controlPoint in curve.controlPoints)
    {
        deformedControlPoints = append(deformedControlPoints, deformPoint(context, definition, controlPoint));
    }

    var curveDefinition = {
        "degree" : curve.degree,
        "controlPoints" : deformedControlPoints,
        "knots" : curve.knots,
        "isPeriodic" : curve.isPeriodic == undefined ? false : curve.isPeriodic
    };

    if (curve.isRational != undefined)
        curveDefinition.isRational = curve.isRational;
    if (curve.weights != undefined)
        curveDefinition.weights = curve.weights;

    return bSplineCurve(curveDefinition);
}

// ============================================================
// Knot insertion for deformation-domain locality (Boehm 1980)
// ============================================================

// Epsilon for detecting knot coincidence in parametric space (unitless)
const KNOT_COINCIDENCE_EPSILON = 1e-6;

// Threshold below which the source X extent is treated as degenerate (length units)
const MIN_SOURCE_X_SPAN = 1e-8 * meter;

// Threshold below which a knot range denominator is treated as zero (unitless)
const KNOT_RANGE_ZERO_THRESHOLD = 1e-10;

// Threshold below which a NURBS homogeneous weight is treated as zero (unitless)
const NURBS_WEIGHT_ZERO_THRESHOLD = 1e-12;

/**
 * Extracts the mutable plain-map representation of a BSplineSurface
 * returned by evApproximateBSplineSurface, ready for Boehm knot insertion.
 *
 * The returned map contains:
 *   controlPoints - 2D plain array of Vector3 (with length units), indexed [row][col]
 *   weights       - 2D plain array of unitless numbers indexed [row][col], or undefined
 *   uKnots        - plain array of numbers (padded knot vector)
 *   vKnots        - plain array of numbers (padded knot vector)
 *   uDegree, vDegree, isUPeriodic, isVPeriodic
 *
 * @param sourceSurface : BSplineSurface from evApproximateBSplineSurface
 * @returns {map} : Mutable surface representation
 */
function extractMutableSurface(sourceSurface) returns map
{
    const rowCount = size(sourceSurface.controlPoints);
    const colCount = rowCount > 0 ? size(sourceSurface.controlPoints[0]) : 0;

    // Control points to plain 2D array
    var controlPoints = makeArray(rowCount);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        controlPoints[rowIndex] = makeArray(colCount);
        for (var columnIndex = 0; columnIndex < colCount; columnIndex += 1)
            controlPoints[rowIndex][columnIndex] = sourceSurface.controlPoints[rowIndex][columnIndex];
    }

    // Weights to plain 2D array (or undefined for non-rational surfaces)
    var weights = undefined;
    if (sourceSurface.weights != undefined)
    {
        weights = makeArray(rowCount);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            weights[rowIndex] = makeArray(colCount);
            for (var columnIndex = 0; columnIndex < colCount; columnIndex += 1)
                weights[rowIndex][columnIndex] = sourceSurface.weights[rowIndex][columnIndex];
        }
    }

    // Knot vectors to plain arrays (preserving padded format from evApproximateBSplineSurface)
    const uKnotCount = size(sourceSurface.uKnots);
    var uKnots = makeArray(uKnotCount);
    for (var knotIndex = 0; knotIndex < uKnotCount; knotIndex += 1)
        uKnots[knotIndex] = sourceSurface.uKnots[knotIndex];

    const vKnotCount = size(sourceSurface.vKnots);
    var vKnots = makeArray(vKnotCount);
    for (var knotIndex = 0; knotIndex < vKnotCount; knotIndex += 1)
        vKnots[knotIndex] = sourceSurface.vKnots[knotIndex];

    return {
        "controlPoints" : controlPoints,
        "weights" : weights,
        "uKnots" : uKnots,
        "vKnots" : vKnots,
        "uDegree" : sourceSurface.uDegree,
        "vDegree" : sourceSurface.vDegree,
        "isUPeriodic" : sourceSurface.isUPeriodic,
        "isVPeriodic" : sourceSurface.isVPeriodic
    };
}

/**
 * Determines whether the u-parametric direction of the surface aligns with the deformation path.
 *
 * Computes how much the local-X coordinate (path axis in source space) varies when traversing
 * the u-direction (rows) versus the v-direction (columns). The direction with the greater
 * spread is path-aligned.
 *
 * Manual dot-product projection onto sourceData.xAxis is used intentionally here.
 * No query or ev* function exists to project an arbitrary set of in-memory control points
 * (plain Vector3 arrays, not geometry entities) onto a coordinate axis. The custom
 * worldToLocal helper in this file converts all three local coordinates at once and
 * requires all four axes (origin, xAxis, yAxis, zAxis), while here we only need the
 * x-component from a partial coordinate system. The projection dot(point - origin, xAxis)
 * is the standard and only correct way to extract local-X given a source coordinate frame.
 *
 * @param sourceData {map} : Source coordinate system (origin, xAxis) from definition.sourceData
 * @param controlPoints {array} : 2D plain array of Vector3 control points [row][col]
 * @returns {boolean} : true if u is path-aligned, false if v is
 */
function detectPathAlignedUDirection(sourceData is map, controlPoints is array) returns boolean
{
    const rowCount = size(controlPoints);
    if (rowCount < 2)
        return true;
    const colCount = size(controlPoints[0]);
    if (colCount < 2)
        return true;

    // Sum of local-X for the first and last row (measures X extent traversed in the u-direction)
    var firstRowLocalXSum = 0 * meter;
    var lastRowLocalXSum = 0 * meter;
    for (var columnIndex = 0; columnIndex < colCount; columnIndex += 1)
    {
        firstRowLocalXSum = firstRowLocalXSum + dot(controlPoints[0][columnIndex] - sourceData.origin, sourceData.xAxis);
        lastRowLocalXSum = lastRowLocalXSum + dot(controlPoints[rowCount - 1][columnIndex] - sourceData.origin, sourceData.xAxis);
    }
    const uDirectionXSpread = abs(lastRowLocalXSum - firstRowLocalXSum) / colCount;

    // Sum of local-X for the first and last column (measures X extent traversed in the v-direction)
    var firstColumnLocalXSum = 0 * meter;
    var lastColumnLocalXSum = 0 * meter;
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        firstColumnLocalXSum = firstColumnLocalXSum + dot(controlPoints[rowIndex][0] - sourceData.origin, sourceData.xAxis);
        lastColumnLocalXSum = lastColumnLocalXSum + dot(controlPoints[rowIndex][colCount - 1] - sourceData.origin, sourceData.xAxis);
    }
    const vDirectionXSpread = abs(lastColumnLocalXSum - firstColumnLocalXSum) / rowCount;

    return uDirectionXSpread >= vDirectionXSpread;
}

/**
 * Returns sorted station values that mark deformation complexity boundaries along the path.
 *
 * Collects interior cross-section station positions and guide profile sample positions.
 * These are used together with uniform knot insertion to ensure that each B-spline span
 * has a knot at or near every major transition point.
 *
 * @param definition {map} : Feature definition with crossSections and guideProfile
 * @returns {array} : Sorted array of station numbers in (0, 1) — endpoint stations excluded
 */
function collectDeformationBoundaryStations(definition is map) returns array
{
    var stations = [];

    // Interior cross-section station boundaries (0 and 1 are the parametric endpoints;
    // they map to knotMin/knotMax which already have knots by definition)
    if (definition.crossSections is array)
    {
        for (var section in definition.crossSections)
        {
            if (section.station is number)
            {
                const station = clamp01(section.station);
                if (station > KNOT_COINCIDENCE_EPSILON && station < 1 - KNOT_COINCIDENCE_EPSILON)
                    stations = append(stations, station);
            }
        }
    }

    // Guide profile interior sample positions
    if (definition.guideProfile is array)
    {
        const sampleCount = size(definition.guideProfile);
        if (sampleCount > 2)
        {
            for (var sampleIndex = 1; sampleIndex < sampleCount - 1; sampleIndex += 1)
                stations = append(stations, sampleIndex / (sampleCount - 1));
        }
    }

    return sort(stations, function(a, b) { return a - b; });
}

/**
 * Computes the sorted list of uniformly-spaced knot values needed to enrich a knot vector
 * to the target span count, supplemented by any deformation station boundaries that are
 * not already within KNOT_COINCIDENCE_EPSILON of an existing knot.
 *
 * Target span count is determined by:
 *   targetSpans = max(MIN_PATH_KNOT_SPANS, activeSectionCount * KNOTS_PER_SECTION_SPAN)
 * where activeSectionCount = max(1, number of interior station transitions + 1).
 *
 * The uniform grid ensures that each B-spline span subtends a small enough portion of the
 * deformation domain that the nonlinearity of deformPoint() is well-approximated by the
 * linear interpolation between deformed control points. Station boundaries are additionally
 * inserted so that section transitions always land on a knot, improving derivative continuity
 * at those transitions.
 *
 * There is no arbitrary upper cap: the correct number of insertions is determined entirely
 * by deformation complexity.
 *
 * @param definition {map} : Feature definition with crossSections and guideProfile
 * @param knotVector {array} : Padded knot vector (from evApproximateBSplineSurface)
 * @param degree {number} : B-spline degree for the direction being refined
 * @returns {array} : Sorted array of knot parameters to insert (may be empty)
 */
function computeEnrichedKnotInsertions(definition is map, knotVector is array, degree is number) returns array
{
    const knotCount = size(knotVector);
    if (knotCount < 2)
        return [];

    const knotMin = knotVector[0];
    const knotMax = knotVector[knotCount - 1];
    const knotRange = knotMax - knotMin;
    if (abs(knotRange) < KNOT_RANGE_ZERO_THRESHOLD)
        return [];

    // Count the current number of distinct internal knot spans (interior unique knot count + 1)
    var uniqueKnots = [knotMin];
    for (var knotIndex = 1; knotIndex < knotCount; knotIndex += 1)
    {
        if (knotVector[knotIndex] - uniqueKnots[size(uniqueKnots) - 1] > KNOT_COINCIDENCE_EPSILON)
            uniqueKnots = append(uniqueKnots, knotVector[knotIndex]);
    }
    // uniqueKnots includes both endpoints; spans = uniqueKnots.size - 1
    const currentSpanCount = size(uniqueKnots) - 1;

    // Determine the required total span count from deformation complexity
    const boundaryStations = collectDeformationBoundaryStations(definition);
    const activeSectionCount = max(1, size(boundaryStations) + 1);
    const targetSpanCount = max(MIN_PATH_KNOT_SPANS, activeSectionCount * KNOTS_PER_SECTION_SPAN);

    // Build the candidate insertion set as the union of:
    //   (a) uniformly spaced parameters to reach targetSpanCount
    //   (b) station boundary positions not already covered
    var candidates = [];

    // (a) Uniform grid
    if (targetSpanCount > currentSpanCount)
    {
        // We need targetSpanCount - currentSpanCount new distinct internal knots
        // Distribute them uniformly across [knotMin, knotMax]
        for (var spanIndex = 1; spanIndex < targetSpanCount; spanIndex += 1)
        {
            const targetParam = knotMin + knotRange * (spanIndex / targetSpanCount);
            if (targetParam <= knotMin + KNOT_COINCIDENCE_EPSILON)
                continue;
            if (targetParam >= knotMax - KNOT_COINCIDENCE_EPSILON)
                continue;
            candidates = append(candidates, targetParam);
        }
    }

    // (b) Station boundaries — insert even if targetSpanCount is already met
    for (var station in boundaryStations)
    {
        const targetParam = knotMin + knotRange * station;
        if (targetParam <= knotMin + KNOT_COINCIDENCE_EPSILON)
            continue;
        if (targetParam >= knotMax - KNOT_COINCIDENCE_EPSILON)
            continue;
        candidates = append(candidates, targetParam);
    }

    if (size(candidates) == 0)
        return [];

    // Deduplicate and filter out positions already present in the original knot vector
    candidates = sort(candidates, function(a, b) { return a - b; });
    var insertions = [];
    for (var candidateIndex = 0; candidateIndex < size(candidates); candidateIndex += 1)
    {
        const candidate = candidates[candidateIndex];

        // Skip if too close to the previous accepted insertion
        if (size(insertions) > 0 && candidate - insertions[size(insertions) - 1] <= KNOT_COINCIDENCE_EPSILON)
            continue;

        // Skip if already present in the original knot vector
        var alreadyPresent = false;
        for (var knotIndex = 0; knotIndex < knotCount; knotIndex += 1)
        {
            if (abs(knotVector[knotIndex] - candidate) <= KNOT_COINCIDENCE_EPSILON)
            {
                alreadyPresent = true;
                break;
            }
        }
        if (!alreadyPresent)
            insertions = append(insertions, candidate);
    }

    return insertions;
}

/**
 * Applies Boehm's knot insertion algorithm to a single 1D sequence of B-spline control points.
 *
 * Inserts the knot value tBar into the span [T[k], T[k+1]) of the padded knot vector,
 * producing one additional control point at the exact position on the original curve
 * (the shape is unchanged).
 *
 * For rational (NURBS) curves, blending is performed in homogeneous coordinates:
 *   P'_i = ((1 - blendingWeight) * w_{i-1} * P_{i-1} + blendingWeight * w_i * P_i) / w'_i
 *   w'_i  = (1 - blendingWeight) * w_{i-1} + blendingWeight * w_i
 *
 * Reference: Boehm, W. (1980). "Inserting New Knots into B-Spline Curves."
 *            Computer-Aided Design, 12(4), 199-201.
 *
 * @param controlRow {array} : lastControlPointIndex+1 Vector3 control points (with length units)
 * @param weightRow  : lastControlPointIndex+1 unitless weights, or undefined for non-rational
 * @param knotVector {array} : Padded knot vector of size lastControlPointIndex + degree + 2
 * @param degree {number} : B-spline degree (p)
 * @param tBar {number} : Knot value to insert
 * @returns {map} : { "controlRow", "weightRow", "knotVector" } with one extra element each
 */
function boehmKnotInsertionOnRow(controlRow is array, weightRow, knotVector is array, degree is number, tBar is number) returns map
{
    const lastControlPointIndex = size(controlRow) - 1;

    // Find knotSpanIndex: the largest index such that knotVector[knotSpanIndex] <= tBar < knotVector[knotSpanIndex+1]
    var knotSpanIndex = -1;
    for (var knotIndex = 0; knotIndex < size(knotVector) - 1; knotIndex += 1)
    {
        if (knotVector[knotIndex] <= tBar && tBar < knotVector[knotIndex + 1])
        {
            knotSpanIndex = knotIndex;
            break;
        }
    }
    if (knotSpanIndex < 0)
        return { "controlRow" : controlRow, "weightRow" : weightRow, "knotVector" : knotVector };

    // Produce lastControlPointIndex+2 new control points via Boehm blending
    var newControlRow = makeArray(lastControlPointIndex + 2);
    var newWeightRow = (weightRow != undefined) ? makeArray(lastControlPointIndex + 2) : undefined;

    for (var controlPointIndex = 0; controlPointIndex <= lastControlPointIndex + 1; controlPointIndex += 1)
    {
        if (controlPointIndex <= knotSpanIndex - degree)
        {
            // Left of the insertion zone: copy unchanged
            newControlRow[controlPointIndex] = controlRow[controlPointIndex];
            if (newWeightRow != undefined)
                newWeightRow[controlPointIndex] = weightRow[controlPointIndex];
        }
        else if (controlPointIndex >= knotSpanIndex + 1)
        {
            // Right of the insertion zone: shift the index by one
            newControlRow[controlPointIndex] = controlRow[controlPointIndex - 1];
            if (newWeightRow != undefined)
                newWeightRow[controlPointIndex] = weightRow[controlPointIndex - 1];
        }
        else
        {
            // Insertion zone [knotSpanIndex-degree+1, knotSpanIndex]: blend adjacent control points
            const blendDenominator = knotVector[controlPointIndex + degree] - knotVector[controlPointIndex];
            const blendingWeight = abs(blendDenominator) < KNOT_RANGE_ZERO_THRESHOLD ? 0.0 : (tBar - knotVector[controlPointIndex]) / blendDenominator;

            if (newWeightRow != undefined)
            {
                // Rational NURBS: blend in homogeneous coordinates
                const previousWeight = weightRow[controlPointIndex - 1];
                const currentWeight = weightRow[controlPointIndex];
                const newWeight = (1 - blendingWeight) * previousWeight + blendingWeight * currentWeight;
                newWeightRow[controlPointIndex] = newWeight;
                newControlRow[controlPointIndex] = abs(newWeight) < NURBS_WEIGHT_ZERO_THRESHOLD
                    ? controlRow[controlPointIndex - 1]
                    : ((1 - blendingWeight) * previousWeight * controlRow[controlPointIndex - 1] + blendingWeight * currentWeight * controlRow[controlPointIndex]) / newWeight;
            }
            else
            {
                // Non-rational: direct linear blend
                newControlRow[controlPointIndex] = (1 - blendingWeight) * controlRow[controlPointIndex - 1] + blendingWeight * controlRow[controlPointIndex];
            }
        }
    }

    // Insert tBar into the knot vector after position knotSpanIndex
    var newKnotVector = makeArray(size(knotVector) + 1);
    for (var knotIndex = 0; knotIndex <= knotSpanIndex; knotIndex += 1)
        newKnotVector[knotIndex] = knotVector[knotIndex];
    newKnotVector[knotSpanIndex + 1] = tBar;
    for (var knotIndex = knotSpanIndex + 1; knotIndex < size(knotVector); knotIndex += 1)
        newKnotVector[knotIndex + 1] = knotVector[knotIndex];

    return { "controlRow" : newControlRow, "weightRow" : newWeightRow, "knotVector" : newKnotVector };
}

/**
 * Inserts a single knot value into the u-direction of a mutable surface.
 *
 * Applies boehmKnotInsertionOnRow to each column of the control net (fixed v, varying u)
 * independently, adding one new row. The u-knot vector gains one element; the v-knot
 * vector and all degree/periodicity fields remain unchanged.
 *
 * @param mutableSurface {map} : Mutable surface from extractMutableSurface
 * @param tBar {number} : Knot value to insert in the u-direction
 * @returns {map} : Updated mutable surface with one additional control point row
 */
function insertSingleKnotU(mutableSurface is map, tBar is number) returns map
{
    const rowCount = size(mutableSurface.controlPoints);
    const colCount = size(mutableSurface.controlPoints[0]);
    const degree = mutableSurface.uDegree;
    const knotVector = mutableSurface.uKnots;

    // Apply insertion to each column (the u-direction sequence at each fixed v)
    var columnResults = makeArray(colCount);
    var newKnotVector = knotVector;

    for (var columnIndex = 0; columnIndex < colCount; columnIndex += 1)
    {
        var columnPoints = makeArray(rowCount);
        var columnWeights = (mutableSurface.weights != undefined) ? makeArray(rowCount) : undefined;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
        {
            columnPoints[rowIndex] = mutableSurface.controlPoints[rowIndex][columnIndex];
            if (columnWeights != undefined)
                columnWeights[rowIndex] = mutableSurface.weights[rowIndex][columnIndex];
        }

        const result = boehmKnotInsertionOnRow(columnPoints, columnWeights, knotVector, degree, tBar);
        columnResults[columnIndex] = result;
        newKnotVector = result.knotVector; // Identical for every column; captured once
    }

    // Reconstruct the 2D arrays from column results (new row count = rowCount + 1)
    const newRowCount = rowCount + 1;
    var newControlPoints = makeArray(newRowCount);
    var newWeights = (mutableSurface.weights != undefined) ? makeArray(newRowCount) : undefined;

    for (var rowIndex = 0; rowIndex < newRowCount; rowIndex += 1)
    {
        newControlPoints[rowIndex] = makeArray(colCount);
        if (newWeights != undefined)
            newWeights[rowIndex] = makeArray(colCount);
        for (var columnIndex = 0; columnIndex < colCount; columnIndex += 1)
        {
            newControlPoints[rowIndex][columnIndex] = columnResults[columnIndex].controlRow[rowIndex];
            if (newWeights != undefined)
                newWeights[rowIndex][columnIndex] = columnResults[columnIndex].weightRow[rowIndex];
        }
    }

    return {
        "controlPoints" : newControlPoints,
        "weights" : newWeights,
        "uKnots" : newKnotVector,
        "vKnots" : mutableSurface.vKnots,
        "uDegree" : mutableSurface.uDegree,
        "vDegree" : mutableSurface.vDegree,
        "isUPeriodic" : mutableSurface.isUPeriodic,
        "isVPeriodic" : mutableSurface.isVPeriodic
    };
}

/**
 * Inserts a single knot value into the v-direction of a mutable surface.
 *
 * Applies boehmKnotInsertionOnRow to each row of the control net (fixed u, varying v)
 * independently, adding one new column. The v-knot vector gains one element; the u-knot
 * vector and all degree/periodicity fields remain unchanged.
 *
 * @param mutableSurface {map} : Mutable surface from extractMutableSurface
 * @param tBar {number} : Knot value to insert in the v-direction
 * @returns {map} : Updated mutable surface with one additional control point column
 */
function insertSingleKnotV(mutableSurface is map, tBar is number) returns map
{
    const rowCount = size(mutableSurface.controlPoints);
    const degree = mutableSurface.vDegree;
    const knotVector = mutableSurface.vKnots;

    var newControlPoints = makeArray(rowCount);
    var newWeights = (mutableSurface.weights != undefined) ? makeArray(rowCount) : undefined;
    var newKnotVector = knotVector;

    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        var rowWeights = undefined;
        if (mutableSurface.weights != undefined)
        {
            const colCount = size(mutableSurface.controlPoints[rowIndex]);
            rowWeights = makeArray(colCount);
            for (var columnIndex = 0; columnIndex < colCount; columnIndex += 1)
                rowWeights[columnIndex] = mutableSurface.weights[rowIndex][columnIndex];
        }

        const result = boehmKnotInsertionOnRow(mutableSurface.controlPoints[rowIndex], rowWeights, knotVector, degree, tBar);
        newControlPoints[rowIndex] = result.controlRow;
        if (newWeights != undefined)
            newWeights[rowIndex] = result.weightRow;
        newKnotVector = result.knotVector;
    }

    return {
        "controlPoints" : newControlPoints,
        "weights" : newWeights,
        "uKnots" : mutableSurface.uKnots,
        "vKnots" : newKnotVector,
        "uDegree" : mutableSurface.uDegree,
        "vDegree" : mutableSurface.vDegree,
        "isUPeriodic" : mutableSurface.isUPeriodic,
        "isVPeriodic" : mutableSurface.isVPeriodic
    };
}

/**
 * Refines a mutable surface's knot structure to match the complexity of the deformation domain.
 *
 * Detects the parametric direction (u or v) that aligns with the deformation path, then inserts
 * knots uniformly at a density sufficient for the piecewise-polynomial B-spline pieces to
 * accurately track the nonlinear deformPoint() function within each span. The number of
 * insertions is determined automatically from the active section count; there is no arbitrary cap.
 *
 * Periodic directions are skipped because inserting into a periodic spline requires different
 * wrap-around logic that is rarely needed for path deformation.
 *
 * @param definition {map} : Feature definition (must include sourceData, crossSections, guideProfile, pathData)
 * @param mutableSurface {map} : Mutable surface from extractMutableSurface
 * @returns {map} : Refined mutable surface with path-aligned knots inserted
 */
function refineKnotsForDeformationDomain(definition is map, mutableSurface is map) returns map
{
    const isUPathAligned = detectPathAlignedUDirection(definition.sourceData, mutableSurface.controlPoints);
    const pathIsPeriodic = isUPathAligned ? (mutableSurface.isUPeriodic == true) : (mutableSurface.isVPeriodic == true);

    if (pathIsPeriodic)
    {
        debugLog(definition, "Knot insertion skipped: path-aligned direction is periodic");
        return mutableSurface;
    }

    const pathKnots = isUPathAligned ? mutableSurface.uKnots : mutableSurface.vKnots;
    const pathDegree = isUPathAligned ? mutableSurface.uDegree : mutableSurface.vDegree;
    const insertionValues = computeEnrichedKnotInsertions(definition, pathKnots, pathDegree);

    if (size(insertionValues) == 0)
    {
        debugLog(definition, "Knot insertion: knot density already sufficient for deformation domain");
        return mutableSurface;
    }

    // Apply insertions one at a time; each insertion updates the knot vector for subsequent ones
    var refined = mutableSurface;
    for (var tBar in insertionValues)
    {
        if (isUPathAligned)
            refined = insertSingleKnotU(refined, tBar);
        else
            refined = insertSingleKnotV(refined, tBar);
    }

    debugLog(definition, "Knot insertion: " ~ size(insertionValues) ~ " knot(s) added in " ~ (isUPathAligned ? "u" : "v") ~ "-direction; control net is now " ~ size(refined.controlPoints) ~ "x" ~ size(refined.controlPoints[0]));

    return refined;
}

function joinTargetFaces(context is Context, id is Id, definition is map, sourceBodies is Query, makeSolid is boolean)
{
    for (var bodyIndex, body in evaluateQuery(context, sourceBodies))
    {
        const targetFaces = getAttributes(context, {
                    "entities" : qOwnedByBody(body, EntityType.FACE),
                    "name" : ATTR_TARGET_FACES
                });
        if (size(targetFaces) == 0)
        {
            debugLog(definition, "body " ~ bodyIndex ~ " has no target faces to join");
            continue;
        }

        debugLog(definition, "joining body " ~ bodyIndex ~ " target face count=" ~ size(targetFaces) ~ ", makeSolid=" ~ makeSolid);
        var booleanSucceeded = false;
        var joinedBody = qNothing();
        try silent
        {
            opBoolean(context, id + ("boolean" ~ bodyIndex), {
                        "tools" : qUnion(targetFaces),
                        "operationType" : BooleanOperationType.UNION,
                        "makeSolid" : makeSolid,
                        "keepTools" : makeSolid && definition.keepSurfaces == true
                    });
            booleanSucceeded = true;
            joinedBody = joinedBooleanResultBody(context, definition, qCreatedBy(id + ("boolean" ~ bodyIndex), EntityType.BODY), qUnion(targetFaces), makeSolid, "body " ~ bodyIndex ~ " boolean join");
        }
        if (booleanSucceeded)
        {
            debugLog(definition, "body " ~ bodyIndex ~ " joined by opBoolean");
            const deformedHolesSubtracted = subtractDeformedHolesToJoinedBody(context, id + ("deformedHoles" ~ bodyIndex), definition, bodyIndex, joinedBody, makeSolid);
            const holesReapplied = reapplyHolesToJoinedBody(context, id + ("reapplyHoles" ~ bodyIndex), definition, bodyIndex, joinedBody, makeSolid);
            const filletsReapplied = reapplyFilletsToJoinedBody(context, id + ("reapplyFillets" ~ bodyIndex), definition, bodyIndex, joinedBody, makeSolid);
            if (!filletsReapplied)
                debugLog(definition, "body " ~ bodyIndex ~ " kept after successful join; some fillets could not be re-applied");
            if (!(deformedHolesSubtracted && holesReapplied) && makeSolid && tryJoinAutoExcludedTargetFaces(context, id + ("autoExcludeAfterReapply" ~ bodyIndex), definition, bodyIndex, body, targetFaces))
            {
                try silent
                {
                    opDeleteBodies(context, id + ("deleteFailedJoinedBody" ~ bodyIndex), {
                                "entities" : joinedBody
                            });
                }
            }
            continue;
        }
        debugLog(definition, "body " ~ bodyIndex ~ " opBoolean join failed");

        if (makeSolid)
        {
            var encloseSucceeded = false;
            try silent
            {
                opEnclose(context, id + ("enclose" ~ bodyIndex), {
                            "entities" : qUnion(targetFaces)
                        });
                encloseSucceeded = true;
                joinedBody = joinedBooleanResultBody(context, definition, qCreatedBy(id + ("enclose" ~ bodyIndex), EntityType.BODY), qUnion(targetFaces), makeSolid, "body " ~ bodyIndex ~ " enclose join");
            }
            if (encloseSucceeded)
            {
                debugLog(definition, "body " ~ bodyIndex ~ " joined by opEnclose");
                const enclosedDeformedHolesSubtracted = subtractDeformedHolesToJoinedBody(context, id + ("deformedHoles" ~ bodyIndex), definition, bodyIndex, joinedBody, makeSolid);
                const enclosedHolesReapplied = reapplyHolesToJoinedBody(context, id + ("reapplyHoles" ~ bodyIndex), definition, bodyIndex, joinedBody, makeSolid);
                const enclosedFilletsReapplied = reapplyFilletsToJoinedBody(context, id + ("reapplyFillets" ~ bodyIndex), definition, bodyIndex, joinedBody, makeSolid);
                if (!enclosedFilletsReapplied)
                    debugLog(definition, "body " ~ bodyIndex ~ " kept after successful enclose; some fillets could not be re-applied");
                if (!(enclosedDeformedHolesSubtracted && enclosedHolesReapplied) && makeSolid && tryJoinAutoExcludedTargetFaces(context, id + ("autoExcludeAfterReapply" ~ bodyIndex), definition, bodyIndex, body, targetFaces))
                {
                    try silent
                    {
                        opDeleteBodies(context, id + ("deleteFailedEnclosedBody" ~ bodyIndex), {
                                    "entities" : joinedBody
                                });
                    }
                }
                continue;
            }

            if (tryJoinAutoExcludedTargetFaces(context, id + ("autoExclude" ~ bodyIndex), definition, bodyIndex, body, targetFaces))
                continue;

            debugLog(definition, "body " ~ bodyIndex ~ " opEnclose failed; target faces do not enclose a valid region");
            debugEntitiesIfEnabled(context, definition, qUnion(targetFaces), DebugColor.RED);
            reportFeatureWarning(context, id, "A deformed solid could not be enclosed. Enable Advanced debug logs to see the rebuild stages.");
        }
        else
        {
            debugLog(definition, "sheet body " ~ bodyIndex ~ " target faces could not be unioned");
            debugEntitiesIfEnabled(context, definition, qUnion(targetFaces), DebugColor.RED);
            reportFeatureWarning(context, id, "Some deformed surface faces could not be joined.");
        }
    }
}

function tryJoinAutoExcludedTargetFaces(context is Context, id is Id, definition is map, bodyIndex is number, sourceBody is Query, originalTargetFaces is array) returns boolean
{
    var hasEdgesToExclude = false;
    const edgesToExclude = autoExcludeBoundaryEdges(context, definition);
    try silent
    {
        hasEdgesToExclude = evaluateQueryCount(context, edgesToExclude) > 0;
    }
    if (!hasEdgesToExclude)
        return false;

    // All faces are rebuilt via pure FFD B-spline deformation. Each successfully deformed
    // face has its result stored in ATTR_TARGET_FACES. Collect those bodies directly.
    var fallbackTargetFaces = [];
    for (var face in evaluateQuery(context, autoExcludeSourceFaces(context, definition, sourceBody)))
    {
        const existingTargetFaces = getAttributes(context, {
                    "entities" : face,
                    "name" : ATTR_TARGET_FACES
                });
        if (size(existingTargetFaces) > 0)
            fallbackTargetFaces = append(fallbackTargetFaces, existingTargetFaces[0]);
    }

    if (size(fallbackTargetFaces) == 0)
        return false;

    debugLog(definition, "body " ~ bodyIndex ~ " retrying solid with pocket/protrusion loops excluded");
    const fallbackFaces = qUnion(fallbackTargetFaces);
    var joined = false;
    var joinedBody = qNothing();
    try silent
    {
        opBoolean(context, id + "boolean", {
                    "tools" : fallbackFaces,
                    "operationType" : BooleanOperationType.UNION,
                    "makeSolid" : true,
                    "keepTools" : definition.keepSurfaces == true
                });
        joinedBody = joinedBooleanResultBody(context, definition, qCreatedBy(id + "boolean", EntityType.BODY), fallbackFaces, true, "auto-exclude boolean join");
        joined = true;
    }
    if (!joined)
    {
        try silent
        {
            opEnclose(context, id + "enclose", {
                        "entities" : fallbackFaces
                    });
            joinedBody = joinedBooleanResultBody(context, definition, qCreatedBy(id + "enclose", EntityType.BODY), fallbackFaces, true, "auto-exclude enclose join");
            joined = true;
        }
    }

    if (!joined)
    {
        debugLog(definition, "body " ~ bodyIndex ~ " auto-excluded solid fallback failed; leaving generated surfaces/curves");
        debugEntitiesIfEnabled(context, definition, fallbackFaces, DebugColor.RED);
        return false;
    }

    try silent
    {
        opDeleteBodies(context, id + "deleteOriginalSurfaces", {
                    "entities" : qUnion(originalTargetFaces)
                });
    }
    debugLog(definition, "body " ~ bodyIndex ~ " joined after auto-excluding pocket/protrusion loops");
    const fallbackDeformedHolesSubtracted = subtractDeformedHolesToJoinedBody(context, id + "deformedHoles", definition, bodyIndex, joinedBody, true);
    const fallbackHolesReapplied = reapplyHolesToJoinedBody(context, id + "reapplyHoles", definition, bodyIndex, joinedBody, true);
    const fallbackFilletsReapplied = reapplyFilletsToJoinedBody(context, id + "reapplyFillets", definition, bodyIndex, joinedBody, true);
    if (!(fallbackDeformedHolesSubtracted && fallbackHolesReapplied && fallbackFilletsReapplied))
        debugLog(definition, "body " ~ bodyIndex ~ " auto-excluded solid was created, but some hole or fillet re-apply operations still failed");
    return true;
}

function autoExcludeSourceFaces(context is Context, definition is map, sourceBody is Query) returns Query
{
    var faces = qOwnedByBody(sourceBody, EntityType.FACE);

    if (definition.reapplyHoleData != undefined && definition.reapplyHoleData.allFaces is array)
        faces = qSubtraction(faces, queryUnionOrNothing(definition.reapplyHoleData.allFaces));
    if (definition.deformedHoleData != undefined && definition.deformedHoleData.allFaces is array)
        faces = qSubtraction(faces, queryUnionOrNothing(definition.deformedHoleData.allFaces));

    if (definition.pocketProtrusionFacesToExclude != undefined)
    {
        try silent
        {
            if (evaluateQueryCount(context, definition.pocketProtrusionFacesToExclude) > 0)
                faces = qSubtraction(faces, definition.pocketProtrusionFacesToExclude);
        }
    }

    return faces;
}

function autoExcludeBoundaryEdges(context is Context, definition is map) returns Query
{
    var edgeQueries = [definition.reapplyBoundaryEdgesToSkip];

    if (definition.pocketProtrusionFacesToExclude != undefined)
    {
        try silent
        {
            if (evaluateQueryCount(context, definition.pocketProtrusionFacesToExclude) > 0)
                edgeQueries = append(edgeQueries, qAdjacent(definition.pocketProtrusionFacesToExclude, AdjacencyType.EDGE, EntityType.EDGE));
        }
    }

    return queryUnionOrNothing(edgeQueries);
}

function joinedBooleanResultBody(context is Context, definition is map, createdBody is Query, toolBodies is Query, makeSolid is boolean, label is string) returns Query
{
    debugQueryCount(context, definition, label ~ " created bodies", createdBody);
    if (queryHasEntities(context, createdBody))
        return createdBody;

    debugQueryCount(context, definition, label ~ " surviving tool bodies", toolBodies);
    if (makeSolid == true)
    {
        const solidToolBodies = qBodyType(toolBodies, BodyType.SOLID);
        debugQueryCount(context, definition, label ~ " surviving solid tool bodies", solidToolBodies);
        if (queryHasEntities(context, solidToolBodies))
            return solidToolBodies;
    }

    if (queryHasEntities(context, toolBodies))
        return toolBodies;
    return createdBody;
}

function reapplyFilletsToJoinedBody(context is Context, id is Id, definition is map, bodyIndex is number, targetBody is Query, makeSolid is boolean) returns boolean
{
    if (definition.reapplyFillets != true || !makeSolid)
        return true;
    if (definition.sourceFilletsRemovedForReapply != true)
    {
        debugLog(definition, "fillet re-apply skipped because source fillets were not removed");
        return true;
    }
    if (definition.reapplyFilletData == undefined || !(definition.reapplyFilletData.byBody is array))
        return true;
    if (bodyIndex >= size(definition.reapplyFilletData.byBody))
        return true;

    const filletInfos = definition.reapplyFilletData.byBody[bodyIndex].fillets;
    if (size(filletInfos) == 0)
        return true;

    debugLog(definition, "re-applying " ~ size(filletInfos) ~ " cylindrical fillet candidate(s) to body " ~ bodyIndex);
    var allSucceeded = true;
    for (var filletIndex, filletInfo in filletInfos)
    {
        const candidateEdges = filletCandidateEdgesForReapply(context, definition, targetBody, filletInfo);

        const filletEdges = queryUnionOrNothing(candidateEdges);
        if (evaluateQueryCount(context, filletEdges) == 0)
        {
            debugLog(definition, "re-apply fillet " ~ filletIndex ~ " skipped because no rebuilt boundary edges were found");
            allSucceeded = false;
            continue;
        }

        var filletSucceeded = tryApplyFilletToEdges(context, id + ("fillet" ~ filletIndex), definition, filletEdges, filletInfo.radius);
        if (!filletSucceeded)
        {
            debugLog(definition, "batch fillet failed for candidate " ~ filletIndex ~ "; trying candidate edges individually");
            for (var candidateIndex = 0; candidateIndex < size(candidateEdges); candidateIndex += 1)
            {
                if (tryApplyFilletToEdges(context, id + ("fillet" ~ filletIndex) + ("edge" ~ candidateIndex), definition, candidateEdges[candidateIndex], filletInfo.radius))
                    filletSucceeded = true;
            }
        }

        if (filletSucceeded)
            debugLog(definition, "re-applied fillet " ~ filletIndex ~ " radius=" ~ filletInfo.radius);
        else
        {
            debugLog(definition, "opFillet failed while re-applying fillet " ~ filletIndex ~ " on body " ~ bodyIndex);
            allSucceeded = false;
        }
    }
    return allSucceeded;
}

function filletCandidateEdgesForReapply(context is Context, definition is map, targetBody is Query, filletInfo is map) returns array
{
    var candidateEdges = [];
    const sourceEdges = filletSourceEdgesForReapply(filletInfo);
    if (queryHasEntities(context, sourceEdges))
    {
        const targetCurves = getAttributes(context, {
                    "entities" : sourceEdges,
                    "name" : ATTR_TARGET_EDGES
                });
        for (var targetCurve in targetCurves)
        {
            var targetPoint;
            var hasPoint = false;
            try silent
            {
                targetPoint = evEdgeTangentLine(context, {
                                "edge" : targetCurve,
                                "parameter" : 0.5
                            }).origin;
                hasPoint = true;
            }
            if (hasPoint)
            {
                const targetEdge = targetBodyEdgeNearPoint(context, targetBody, targetPoint);
                if (queryHasEntities(context, targetEdge))
                    candidateEdges = appendUniqueQuery(context, candidateEdges, targetEdge);
            }
        }
    }

    if (size(candidateEdges) > 0)
        return candidateEdges;

    if (filletInfo.edgePoints is array)
    {
        for (var sourcePoint in filletInfo.edgePoints)
        {
            const targetEdge = targetBodyEdgeNearPoint(context, targetBody, deformPoint(context, definition, sourcePoint));
            if (queryHasEntities(context, targetEdge))
                candidateEdges = appendUniqueQuery(context, candidateEdges, targetEdge);
        }
    }

    return candidateEdges;
}

function appendUniqueQuery(context is Context, queries is array, candidate is Query) returns array
{
    if (!queryArrayContains(context, queries, candidate))
        return append(queries, candidate);
    return queries;
}

function filletSourceEdgesForReapply(filletInfo is map) returns Query
{
    if (filletInfo.refilletEdges is array && size(filletInfo.refilletEdges) > 0)
        return queryUnionOrNothing(filletInfo.refilletEdges);
    if (filletInfo.edges is array && size(filletInfo.edges) > 0)
        return queryUnionOrNothing(filletInfo.edges);
    return qNothing();
}

function targetBodyEdgeNearPoint(context is Context, targetBody is Query, point is Vector) returns Query
{
    var targetEdge = qNothing();
    const bodyEdges = qOwnedByBody(targetBody, EntityType.EDGE);
    try silent
    {
        targetEdge = qContainsPoint(bodyEdges, point);
        if (evaluateQueryCount(context, targetEdge) == 0)
            targetEdge = qClosestTo(bodyEdges, point);
    }
    return targetEdge;
}

function tryApplyFilletToEdges(context is Context, id is Id, definition is map, filletEdges is Query, radius) returns boolean
{
    if (!queryHasEntities(context, filletEdges))
        return false;

    var filletSucceeded = false;
    try silent
    {
        opFillet(context, id, {
                    "entities" : filletEdges,
                    "radius" : radius,
                    "tangentPropagation" : false
                });
        filletSucceeded = true;
    }
    return filletSucceeded;
}

function reapplyHolesToJoinedBody(context is Context, id is Id, definition is map, bodyIndex is number, targetBody is Query, makeSolid is boolean) returns boolean
{
    if (definition.reapplyHoles != true || !makeSolid)
        return true;
    if (definition.reapplyHoleData == undefined || !(definition.reapplyHoleData.byBody is array))
        return true;
    if (bodyIndex >= size(definition.reapplyHoleData.byBody))
        return true;

    const holeInfos = definition.reapplyHoleData.byBody[bodyIndex].holes;
    if (size(holeInfos) == 0)
        return true;

    debugLog(definition, "re-applying " ~ size(holeInfos) ~ " cylindrical hole(s) to body " ~ bodyIndex);
    var allSucceeded = true;
    for (var holeIndex, holeInfo in holeInfos)
    {
        var holeSucceeded = subtractHoleCylinder(context, id + ("holeSubtract" ~ holeIndex), definition, targetBody, holeInfo.topCenter, holeInfo.bottomCenter, holeInfo.radius, holeInfo);
        var fallbackMethod = "";
        if (!holeSucceeded)
        {
            debugLog(definition, "Boolean cylinder cut failed for hole " ~ holeIndex ~ "; trying deformed loop loft cutter");
            holeSucceeded = subtractHoleLoftFromTargetEdges(context, id + ("holeSubtractLoft" ~ holeIndex), definition, targetBody, holeInfo);
            if (holeSucceeded)
                fallbackMethod = "deformed loop loft ";
        }
        if (!holeSucceeded)
        {
            const toolAxis = holeInfo.topCenter - holeInfo.bottomCenter;
            if (norm(toolAxis) > 1e-8 * meter)
            {
                const toolDirection = normalize(toolAxis);
                const toolCenter = (holeInfo.topCenter + holeInfo.bottomCenter) / 2;
                const fallbackHalfLength = max(norm(toolAxis) * 2, holeInfo.radius * 20);
                holeSucceeded = subtractHoleCylinder(context, id + ("holeSubtractExtended" ~ holeIndex), definition, targetBody, toolCenter + toolDirection * fallbackHalfLength, toolCenter - toolDirection * fallbackHalfLength, holeInfo.radius, holeInfo);
                if (holeSucceeded)
                    fallbackMethod = "extended ";
            }
        }

        if (holeSucceeded)
            debugLog(definition, "re-applied hole " ~ holeIndex ~ " by " ~ fallbackMethod ~ "Boolean subtraction, radius=" ~ holeInfo.radius ~ ", length=" ~ holeInfo.toolLength);
        else
        {
            debugLog(definition, "Boolean cylinder cut failed while re-applying hole " ~ holeIndex ~ " on body " ~ bodyIndex);
            allSucceeded = false;
        }
    }
    return allSucceeded;
}

function subtractDeformedHolesToJoinedBody(context is Context, id is Id, definition is map, bodyIndex is number, targetBody is Query, makeSolid is boolean) returns boolean
{
    if (definition.reapplyHoles == true || !makeSolid)
        return true;
    if (definition.deformedHoleData == undefined || !(definition.deformedHoleData.byBody is array))
        return true;
    if (bodyIndex >= size(definition.deformedHoleData.byBody))
        return true;

    const holeInfos = definition.deformedHoleData.byBody[bodyIndex].holes;
    if (size(holeInfos) == 0)
        return true;

    debugLog(definition, "subtracting " ~ size(holeInfos) ~ " deformed cylindrical hole cutter(s) from body " ~ bodyIndex);
    var allSucceeded = true;
    for (var holeIndex, holeInfo in holeInfos)
    {
        var holeSucceeded = subtractHoleLoftFromTargetEdges(context, id + ("hole" ~ holeIndex), definition, targetBody, holeInfo);
        var fallbackMethod = "";
        if (!holeSucceeded)
        {
            const cutRadius = deformedHoleCutRadius(holeInfo);
            debugLog(definition, "deformed hole loft cutter failed for hole " ~ holeIndex ~ "; trying deformed-axis cylinder cutter, radius=" ~ cutRadius);
            holeSucceeded = subtractHoleCylinderAlongStoredAxis(context, id + ("holeCylinder" ~ holeIndex), definition, targetBody, holeInfo, cutRadius);
            if (holeSucceeded)
                fallbackMethod = " by deformed-axis cylinder";
        }

        if (holeSucceeded)
        {
            debugLog(definition, "subtracted deformed hole " ~ holeIndex ~ fallbackMethod);
        }
        else
        {
            debugLog(definition, "deformed hole cutter failed for hole " ~ holeIndex ~ " on body " ~ bodyIndex);
            allSucceeded = false;
        }
    }
    return allSucceeded;
}

function deformedHoleCutRadius(holeInfo is map)
{
    if (holeInfo.deformedRadius != undefined)
        return max(holeInfo.radius, holeInfo.deformedRadius);
    return holeInfo.radius;
}

function subtractHoleCylinderAlongStoredAxis(context is Context, id is Id, definition is map, targetBody is Query, holeInfo is map, radius) returns boolean
{
    if (holeInfo.topCenter == undefined || holeInfo.bottomCenter == undefined)
        return false;

    if (subtractHoleCylinder(context, id, definition, targetBody, holeInfo.topCenter, holeInfo.bottomCenter, radius, holeInfo))
        return true;

    const toolAxis = holeInfo.topCenter - holeInfo.bottomCenter;
    if (norm(toolAxis) <= 1e-8 * meter)
        return false;

    const toolDirection = normalize(toolAxis);
    const toolCenter = (holeInfo.topCenter + holeInfo.bottomCenter) / 2;
    const fallbackHalfLength = max(norm(toolAxis) * 2, radius * 20);
    return subtractHoleCylinder(context, id + "Extended", definition, targetBody, toolCenter + toolDirection * fallbackHalfLength, toolCenter - toolDirection * fallbackHalfLength, radius, holeInfo);
}

function subtractHoleLoftFromTargetEdges(context is Context, id is Id, definition is map, targetBody is Query, holeInfo is map) returns boolean
{
    if (!(holeInfo.boundaryEdges is array) || size(holeInfo.boundaryEdges) < 2)
        return false;

    const targetProfiles = holeLoftTargetProfiles(context, definition, holeInfo);
    if (size(targetProfiles) != 2)
        return false;

    const booleanTargets = solidBooleanTargets(context, definition, targetBody);
    const toolId = id + "Tool";
    var solidLoftCreated = false;
    try silent
    {
        opLoft(context, toolId, {
                    "profileSubqueries" : targetProfiles,
                    "bodyType" : ToolBodyType.SOLID
                });
        solidLoftCreated = true;
    }
    if (solidLoftCreated)
    {
        debugLog(definition, "solid loft cutter created");
        debugQueryCount(context, definition, "solid loft cutter bodies", qCreatedBy(toolId, EntityType.BODY));
        var solidBooleanSucceeded = false;
        try silent
        {
            opBoolean(context, id + "Boolean", {
                        "tools" : qCreatedBy(toolId, EntityType.BODY),
                        "targets" : booleanTargets,
                        "operationType" : BooleanOperationType.SUBTRACTION,
                        "keepTools" : false
                    });
            solidBooleanSucceeded = true;
        }
        if (solidBooleanSucceeded)
        {
            tagSubtractedHoleTargets(context, id + "Boolean", definition, targetBody, holeInfo);
            return true;
        }

        debugLog(definition, "solid loft cutter boolean subtraction failed");
        try silent
        {
            opDeleteBodies(context, id + "DeleteFailedSolidTool", {
                        "entities" : qCreatedBy(toolId, EntityType.BODY)
                    });
        }
    }
    else
    {
        debugLog(definition, "solid loft cutter creation failed");
    }

    debugLog(definition, "trying enclosed surface cutter");
    const sideId = id + "SideSurface";
    const cap0Id = id + "Cap0";
    const cap1Id = id + "Cap1";
    const enclosedToolId = id + "EnclosedTool";

    var sideCreated = false;
    try silent
    {
        opLoft(context, sideId, {
                    "profileSubqueries" : targetProfiles,
                    "bodyType" : ToolBodyType.SURFACE
                });
        sideCreated = true;
    }
    if (!sideCreated)
    {
        debugLog(definition, "surface side loft cutter creation failed");
        return false;
    }
    debugLog(definition, "surface side loft cutter created");

    var cap0Created = false;
    try silent
    {
        opFillSurface(context, cap0Id, {
                    "edgesG0" : targetProfiles[0],
                    "edgesG1" : qNothing(),
                    "edgesG2" : qNothing(),
                    "guideVertices" : qNothing(),
                    "showIsocurves" : false
                });
        cap0Created = true;
    }
    if (!cap0Created)
    {
        debugLog(definition, "surface cutter cap 0 creation failed");
        try silent
        {
            opDeleteBodies(context, id + "DeleteFailedSurfaceSide", {
                        "entities" : qCreatedBy(sideId, EntityType.BODY)
                    });
        }
        return false;
    }

    var cap1Created = false;
    try silent
    {
        opFillSurface(context, cap1Id, {
                    "edgesG0" : targetProfiles[1],
                    "edgesG1" : qNothing(),
                    "edgesG2" : qNothing(),
                    "guideVertices" : qNothing(),
                    "showIsocurves" : false
                });
        cap1Created = true;
    }
    if (!cap1Created)
    {
        debugLog(definition, "surface cutter cap 1 creation failed");
        try silent
        {
            opDeleteBodies(context, id + "DeleteFailedSurfaceSideCap0", {
                        "entities" : qUnion([
                                qCreatedBy(sideId, EntityType.BODY),
                                qCreatedBy(cap0Id, EntityType.BODY)
                            ])
                    });
        }
        return false;
    }
    debugLog(definition, "surface cutter caps created");

    var enclosedCreated = false;
    try silent
    {
        opEnclose(context, enclosedToolId, {
                    "entities" : qUnion([
                            qCreatedBy(sideId, EntityType.BODY),
                            qCreatedBy(cap0Id, EntityType.BODY),
                            qCreatedBy(cap1Id, EntityType.BODY)
                        ])
                });
        enclosedCreated = true;
    }
    if (!enclosedCreated)
    {
        debugLog(definition, "surface cutter enclose failed");
        try silent
        {
            opDeleteBodies(context, id + "DeleteSurfaceCutterHelpers", {
                        "entities" : qUnion([
                                qCreatedBy(sideId, EntityType.BODY),
                                qCreatedBy(cap0Id, EntityType.BODY),
                                qCreatedBy(cap1Id, EntityType.BODY)
                            ])
                    });
        }
        return false;
    }
    debugLog(definition, "surface cutter enclosed solid created");
    debugQueryCount(context, definition, "enclosed surface cutter bodies", qCreatedBy(enclosedToolId, EntityType.BODY));

    var enclosedBooleanSucceeded = false;
    try silent
    {
        opBoolean(context, id + "EnclosedBoolean", {
                    "tools" : qCreatedBy(enclosedToolId, EntityType.BODY),
                    "targets" : booleanTargets,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "keepTools" : false
                });
        enclosedBooleanSucceeded = true;
    }

    try silent
    {
        opDeleteBodies(context, id + "DeleteSurfaceCutterHelpers", {
                    "entities" : qUnion([
                            qCreatedBy(sideId, EntityType.BODY),
                            qCreatedBy(cap0Id, EntityType.BODY),
                            qCreatedBy(cap1Id, EntityType.BODY)
                        ])
                });
    }

    if (enclosedBooleanSucceeded)
    {
        tagSubtractedHoleTargets(context, id + "EnclosedBoolean", definition, targetBody, holeInfo);
        return true;
    }

    debugLog(definition, "enclosed surface cutter boolean subtraction failed");
    try silent
    {
        opDeleteBodies(context, id + "DeleteFailedEnclosedTool", {
                    "entities" : qCreatedBy(enclosedToolId, EntityType.BODY)
                });
    }
    return false;
}

function holeLoftTargetProfiles(context is Context, definition is map, holeInfo is map) returns array
{
    if (holeInfo.profileBoundaryEdges is array && size(holeInfo.profileBoundaryEdges) == 2)
    {
        var targetProfiles = [];
        for (var profileEdges in holeInfo.profileBoundaryEdges)
        {
            const targetEdges = getAttributes(context, {
                        "entities" : qUnion(profileEdges),
                        "name" : ATTR_TARGET_EDGES
                    });
            debugLog(definition, "deformed loop loft cutter profile target edge count=" ~ size(targetEdges));
            if (size(targetEdges) == 0)
                return [];
            targetProfiles = append(targetProfiles, queryUnionOrNothing(targetEdges));
        }
        return targetProfiles;
    }

    const targetEdges = getAttributes(context, {
                "entities" : qUnion(holeInfo.boundaryEdges),
                "name" : ATTR_TARGET_EDGES
            });
    debugLog(definition, "deformed loop loft cutter target edge count=" ~ size(targetEdges));
    if (size(targetEdges) != 2)
        return [];

    return targetEdges;
}

function subtractHoleCylinder(context is Context, id is Id, definition is map, targetBody is Query, topCenter is Vector, bottomCenter is Vector, radius, holeInfo is map) returns boolean
{
    const toolId = id + "Tool";
    const booleanTargets = solidBooleanTargets(context, definition, targetBody);
    var toolCreated = false;
    debugLog(definition, "cylinder cutter attempt radius=" ~ radius ~ ", top=" ~ topCenter ~ ", bottom=" ~ bottomCenter);
    try silent
    {
        fCylinder(context, toolId, {
                    "topCenter" : topCenter,
                    "bottomCenter" : bottomCenter,
                    "radius" : radius
                });
        toolCreated = true;
    }
    if (!toolCreated)
    {
        debugLog(definition, "cylinder cutter creation failed");
        return false;
    }

    debugLog(definition, "cylinder cutter created");
    debugQueryCount(context, definition, "cylinder cutter bodies", qCreatedBy(toolId, EntityType.BODY));
    var booleanSucceeded = false;
    try silent
    {
        opBoolean(context, id + "Boolean", {
                    "tools" : qCreatedBy(toolId, EntityType.BODY),
                    "targets" : booleanTargets,
                    "operationType" : BooleanOperationType.SUBTRACTION,
                    "keepTools" : false
                });
        booleanSucceeded = true;
    }

    if (booleanSucceeded)
    {
        tagSubtractedHoleTargets(context, id + "Boolean", definition, targetBody, holeInfo);
        return true;
    }

    debugLog(definition, "cylinder cutter boolean subtraction failed");
    try silent
    {
        opDeleteBodies(context, id + "DeleteFailedTool", {
                    "entities" : qCreatedBy(toolId, EntityType.BODY)
                });
    }
    return false;
}

function tagSubtractedHoleTargets(context is Context, operationId is Id, definition is map, targetBody is Query, holeInfo is map)
{
    var createdFaces = qCreatedBy(operationId, EntityType.FACE);
    var createdEdges = qCreatedBy(operationId, EntityType.EDGE);
    debugQueryCount(context, definition, "subtracted hole created faces", createdFaces);
    debugQueryCount(context, definition, "subtracted hole created edges", createdEdges);

    if (!queryHasEntities(context, createdFaces))
        createdFaces = fallbackHoleTargetFaces(context, definition, targetBody, holeInfo);
    if (!queryHasEntities(context, createdEdges))
        createdEdges = fallbackHoleTargetEdges(context, definition, targetBody, holeInfo);

    if (holeInfo.faces != undefined && queryHasEntities(context, holeInfo.faces) && queryHasEntities(context, createdFaces))
    {
        setAttribute(context, {
                    "entities" : holeInfo.faces,
                    "name" : ATTR_TARGET_FACES,
                    "attribute" : createdFaces
                });
        setAttribute(context, {
                    "entities" : createdFaces,
                    "name" : ATTR_SOURCE_HOLE_FACES,
                    "attribute" : holeInfo.faces
                });
    }

    if ((holeInfo.boundaryEdges is array) && size(holeInfo.boundaryEdges) > 0 && queryHasEntities(context, createdEdges))
    {
        const sourceBoundaryEdges = qUnion(holeInfo.boundaryEdges);
        setAttribute(context, {
                    "entities" : sourceBoundaryEdges,
                    "name" : ATTR_TARGET_EDGES,
                    "attribute" : createdEdges
                });
        setAttribute(context, {
                    "entities" : createdEdges,
                    "name" : ATTR_SOURCE_HOLE_EDGES,
                    "attribute" : sourceBoundaryEdges
                });
    }
}

function fallbackHoleTargetFaces(context is Context, definition is map, targetBody is Query, holeInfo is map) returns Query
{
    if (holeInfo.topCenter == undefined || holeInfo.bottomCenter == undefined)
        return qNothing();

    const holeCenter = (holeInfo.topCenter + holeInfo.bottomCenter) / 2;
    const candidateFaces = qClosestTo(qOwnedByBody(targetBody, EntityType.FACE), holeCenter);
    debugQueryCount(context, definition, "subtracted hole fallback faces", candidateFaces);
    return candidateFaces;
}

function fallbackHoleTargetEdges(context is Context, definition is map, targetBody is Query, holeInfo is map) returns Query
{
    if (!(holeInfo.boundaryEdges is array) || size(holeInfo.boundaryEdges) == 0)
        return qNothing();

    var candidateEdges = [];
    for (var sourceEdge in holeInfo.boundaryEdges)
    {
        try silent
        {
            const sourcePoint = evEdgeTangentLine(context, {
                            "edge" : sourceEdge,
                            "parameter" : 0.5
                        }).origin;
            const targetPoint = deformPoint(context, definition, sourcePoint);
            candidateEdges = append(candidateEdges, qClosestTo(qOwnedByBody(targetBody, EntityType.EDGE), targetPoint));
        }
    }

    const result = queryUnionOrNothing(candidateEdges);
    debugQueryCount(context, definition, "subtracted hole fallback edges", result);
    return result;
}

function solidBooleanTargets(context is Context, definition is map, targetBody is Query) returns Query
{
    const solidTargets = qBodyType(targetBody, BodyType.SOLID);
    debugQueryCount(context, definition, "boolean target bodies", targetBody);
    debugQueryCount(context, definition, "boolean target solid bodies", solidTargets);

    var hasSolidTargets = false;
    try silent
    {
        hasSolidTargets = evaluateQueryCount(context, solidTargets) > 0;
    }
    if (hasSolidTargets)
        return solidTargets;
    return targetBody;
}

function joinSelectedFaceTargets(context is Context, id is Id, definition is map, sourceFaces is Query)
{
    const targetFaces = getAttributes(context, {
                "entities" : sourceFaces,
                "name" : ATTR_TARGET_FACES
            });
    if (size(targetFaces) <= 1)
    {
        debugLog(definition, "selected face target join skipped; target face count=" ~ size(targetFaces));
        return;
    }

    var booleanSucceeded = false;
    try silent
    {
        opBoolean(context, id + "boolean", {
                    "tools" : qUnion(targetFaces),
                    "operationType" : BooleanOperationType.UNION,
                    "makeSolid" : false,
                    "keepTools" : false
                });
        booleanSucceeded = true;
    }
    if (booleanSucceeded)
        debugLog(definition, "selected face targets joined by opBoolean");
    else
    {
        debugLog(definition, "selected face target opBoolean failed");
        debugEntitiesIfEnabled(context, definition, qUnion(targetFaces), DebugColor.RED);
    }
}
