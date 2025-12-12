FeatureScript 1803;
import(path : "onshape/std/geometry.fs", version : "1803.0");
import(path : "4016d9abcc9505b014621304/bcc59ccee9da1411e283bc10/210ccca6a812702832fa8f08", version : "1b2d60e0f14e2d16bbabaa2b");
import(path : "4016d9abcc9505b014621304/bcc59ccee9da1411e283bc10/9515529043d4ad80b6c28970", version : "3da1199ef1574376cc52c2ff");
import(path : "4016d9abcc9505b014621304/bcc59ccee9da1411e283bc10/4a1660138125fff2692edd40", version : "83091f70860e24a596af802e");
import(path : "4016d9abcc9505b014621304/bcc59ccee9da1411e283bc10/03ff42c40ee658ac5067cb89", version : "e6f26bed7830f196f5401dc5");
import(path : "4016d9abcc9505b014621304/bcc59ccee9da1411e283bc10/0e8154f86e46ddd4e968b31a", version : "598231406d2964932770c328");

const SKEW_ANGLE_LIMIT = 89;
export const SKEW_ANGLE_BOUNDS = { (degree) : [-SKEW_ANGLE_LIMIT, 0, SKEW_ANGLE_LIMIT] } as AngleBoundSpec;
export const SECTION_WIDTH_BOUNDS = { (millimeter) : [0.001, 10, 100000] } as LengthBoundSpec;
export const SECTION_SPACE_BOUNDS = { (millimeter) : [0, 10, 100000] } as LengthBoundSpec;

const SECTION_SLICER_TIMER = "SectionSlicerTimer";
const ERR_SECTIONOFFSET_BOUNDS = "Section Offset must have magnitude less than the sum of Section Width and Section Space.";
const ERR_NO_SECTIONS = "Not enough room for sections; try adjusting Section Width, Section Space, and/or Section Offset.";

type SlicerCoords typecheck isSlicerCoords;
predicate isSlicerCoords(A)
{
    A is map;
    A.cSys is CoordSystem;
    isLength(A.extentX);    // Extent of target body along slicer coordinate system's X axis.
    isLength(A.extentY);
    isLength(A.extentZ);
    A.refXAxis is Vector;
}

type IntersectDatum typecheck isIntersectDatum;
predicate isIntersectDatum(A)
{
    A is map;
    isLength(A.z);
    isNonNegativeInteger(A.idxA);
    isNonNegativeInteger(A.idxB);
}

export function OnFeatureChange(context is Context, id is Id, oldDef is map, def is map, isCreating is boolean) returns map
{
    if (!def.uAxis_en)
        def.vAxis_en = false;
    if (!oldDef.vAxis_en && def.vAxis_en)
    {
        def.uAxis_skewAngle = 30*deg;
        def.vSection_offset = 0*mm;
        def.sectionSpace = max(def.sectionSpace, 3 * def.sectionWidth);
    }
    return def;
}

function ValidateParams(context, def is map)
{
    if (isQueryEmpty(context, def.target))
        throw regenError("No target body selected.", ["target"]);
    if (abs(def.xSection_offset) >= def.sectionWidth + def.sectionSpace)
        throw regenError("X-Axis " ~ ERR_SECTIONOFFSET_BOUNDS, ["xSection_offset"]);
    if (abs(def.uSection_offset) >= def.sectionWidth + def.sectionSpace)
        throw regenError("U-Axis " ~ ERR_SECTIONOFFSET_BOUNDS, ["uSection_offset"]);
    if (def.vAxis_en)
    {
        if (def.uAxis_skewAngle != 30*deg)
            throw regenError("In three-axis mode, the U-axis skew angle is restricted to 30 degrees; disable the V-axis to adjust this angle.", ["uAxis_skewAngle"]);
        if (def.sectionSpace <= 2 * def.sectionWidth)
            throw regenError("In three-axis mode, the section space must be greater than twice the section width to avoid regions where sections of all three axes intersect, and to avoid cutting sections of an axis into multiple disjoint bodies; increase the section space or disable the V axis.", ["sectionSpace"]);
        if (round(abs(def.vSection_offset) - def.sectionSpace / 2 + def.sectionWidth, .001*mm) > 0*mm)
            throw regenError("In three-axis mode, the V-axis section offset is restricted to values that prevent regions where sections of all three axes intersect; try reducing the magnitude of the V axis offset", ["vSection_offset"]);
    }
}

export function OnManipulatorChange(context is Context, def is map, manipulators is map) returns map
{
    const maxOffset = def.sectionWidth + def.sectionSpace - .01*mm;
    if (manipulators["xAxis_adjustAngle"] is Manipulator)
        def.xAxis_adjustAngle = manipulators["xAxis_adjustAngle"].angle;
    if (manipulators["xSection_offset"] is Manipulator)
        def.xSection_offset = clamp(manipulators["xSection_offset"].offset, -maxOffset, maxOffset);
    if (manipulators["uAxis_skewAngle"] is Manipulator)
        def.uAxis_skewAngle = clamp(manipulators["uAxis_skewAngle"].angle, -SKEW_ANGLE_LIMIT * deg, SKEW_ANGLE_LIMIT * deg);
    if (manipulators["uSection_offset"] is Manipulator)
        def.uSection_offset = clamp(manipulators["uSection_offset"].offset, -maxOffset, maxOffset);
    if (manipulators["vSection_offset"] is Manipulator)
    {
        const maxVOffset = def.sectionSpace - 2 * def.sectionWidth;
        def.vSection_offset = clamp(manipulators["vSection_offset"].offset, -maxVOffset, maxVOffset);
    }
    return def;
}

function DrawManipulators(context is Context, id is Id, SC is SlicerCoords, def is map)
{
    const offsetLimit = def.sectionWidth + def.sectionSpace - .1*mm;
    
    // X-Axis
    const xManipulatorPos = SC.cSys.origin + 0.6 * SC.extentX * SC.cSys.xAxis;
    addDebugArrow(context, SC.cSys.origin, xManipulatorPos, 0*mm, DebugColor.RED);
    const manipXAxisAdjustAngle is Manipulator = angularManipulator({
            "axisOrigin" : SC.cSys.origin,
            "axisDirection" : SC.cSys.zAxis,
            "rotationOrigin" : SC.cSys.origin + 0.6 * SC.extentX * SC.refXAxis,
            "angle" : def.xAxis_adjustAngle,
            "primaryParameterId" : "xAxis_adjustAngle"
    });
    const manipXSectionOffset is Manipulator = linearManipulator({
            "base" : xManipulatorPos,
            "direction" : SC.cSys.xAxis,
            "offset" : def.xSection_offset / 2,
            "minValue" : -offsetLimit,
            "maxValue" : offsetLimit,
            "primaryParameterId" : "xSection_offset"
    });
    addManipulators(context, id, { "xAxis_adjustAngle" : manipXAxisAdjustAngle, "xSection_offset" : manipXSectionOffset});
    
    // U-Axis
    if (def.uAxis_en)
    {
        const uManipulatorPos = SC.cSys.origin + 0.6 * SC.extentY * GetWorldAxisDir(SC, def.uAxis_skewAngle + 90*deg);
        addDebugArrow(context, SC.cSys.origin, uManipulatorPos, 0*mm, DebugColor.GREEN);
        const manipUSectionOffset is Manipulator = linearManipulator({
                "base" : uManipulatorPos,
                "direction" : GetWorldAxisDir(SC, def.uAxis_skewAngle + 90*deg),
                "offset" : def.uSection_offset,
                "primaryParameterId" : "uSection_offset"
        });
        addManipulators(context, id, { "uSection_offset" : manipUSectionOffset });
        if (!def.vAxis_en)
        {
            const manipUAxisSkewAngle is Manipulator = angularManipulator({
                "axisOrigin" : SC.cSys.origin,
                "axisDirection" : SC.cSys.zAxis,
                "rotationOrigin" : SC.cSys.origin + 0.6 * SC.extentY * yAxis(SC.cSys),
                "angle" : def.uAxis_skewAngle,
                "primaryParameterId" : "uAxis_skewAngle"
            });   
            addManipulators(context, id, { "uAxis_skewAngle" : manipUAxisSkewAngle });
        }
    }
    
    // V-Axis
    if (def.vAxis_en)
    {
        const vManipulatorPos = SC.cSys.origin + 0.6 * SC.extentY * GetWorldAxisDir(SC, 60*deg);
        addDebugArrow(context, SC.cSys.origin, vManipulatorPos, 0*mm, DebugColor.BLUE);
        const manipVSectionOffset is Manipulator = linearManipulator({
                "base" : vManipulatorPos,
                "direction" : GetWorldAxisDir(SC, 60*deg),
                "offset" : def.vSection_offset,
                "primaryParameterId" : "vSection_offset"
        });
        addManipulators(context, id, { "vSection_offset" : manipVSectionOffset });
    }
}

export function SectionSlicer_Main(context is Context, id is Id, def is map)
{
    startTimer(SECTION_SLICER_TIMER);

    ValidateParams(context, def);
    const SC = GetCoordSystem(context, id, def);
    DrawManipulators(context, id, SC, def);

    // Calc section points in world space.
    const wPx = GetSectionPoints(context, SC, def.sectionWidth, def.sectionSpace, def.xSection_offset, 0*deg, def.target);
    if (size(wPx) == 0)
        throw regenError("X-Axis: " ~ ERR_NO_SECTIONS, ["sectionWidth", "sectionSpace", "xSection_offset"]);
    const wPu = def.uAxis_en ? GetSectionPoints(context, SC, def.sectionWidth, def.sectionSpace, def.uSection_offset, 90*deg + def.uAxis_skewAngle, def.target) : [];
    if (def.uAxis_en && size(wPu) == 0)
        throw regenError("U-Axis: " ~ ERR_NO_SECTIONS, ["sectionWidth", "sectionSpace", "uSection_offset"]);
    const wPv = def.vAxis_en ? GetSectionPoints(context, SC, def.sectionWidth, def.sectionSpace,  (def.sectionSpace + def.sectionWidth) / 2 + def.xSection_offset + def.uSection_offset + def.vSection_offset, 60*deg, def.target) : [];
    if (def.vAxis_en && size(wPv) == 0)
        throw regenError("V-Axis " ~ ERR_NO_SECTIONS, ["sectionWidth", "sectionSpace", "vSection_offset"]);

    // Slice target into sections.
    const qSu = def.uAxis_en ? CreateAxisSections(context, id, SC, def.sectionWidth, def.uAxis_skewAngle + 90*deg, wPu, qBody(Copy(context, id, def.target)), "U") : qNothing();
    const qSv = def.vAxis_en ? CreateAxisSections(context, id, SC, def.sectionWidth, 60*deg, wPv, qBody(Copy(context, id, def.target)), "V") : qNothing();
    const qSx = CreateAxisSections(context, id, SC, def.sectionWidth, 0*deg, wPx, (def.keepTarget ? qBody(Copy(context, id, def.target)) : def.target), "X");
    const Sx = evaluateQuery(context, qSx);
    const Su = evaluateQuery(context, qSu);
    const Sv = evaluateQuery(context, qSv);
    
    // Cut Slots into sections.
    const qSplitToolTemplate = CreateSplitTool(context, id, plane(SC.cSys));
    if (def.uAxis_en)
        CutSlots(context, id, SC, def.sectionWidth, 0*deg, def.uAxis_skewAngle + 90*deg, def.reverseSlot, Sx, Su, wPx, wPu, qSplitToolTemplate);
    if (def.vAxis_en)
    {
        CutSlots(context, id, SC, def.sectionWidth, 0*deg, 60*deg, def.reverseSlot, Sx, Sv, wPx, wPv, qSplitToolTemplate);
        CutSlots(context, id, SC, def.sectionWidth, def.uAxis_skewAngle + 90*deg, 60*deg, def.reverseSlot, Su, Sv, wPu, wPv, qSplitToolTemplate);
    }
    Delete(context, id, qSplitToolTemplate);

    // Debug outputs.
    if (def.debug_coordSystem)
        debug(context, SC.cSys);
    if (def.debug_boundingBox)
        DebugBoundingBox(context, SC.cSys, vector(SC.extentX, SC.extentY, SC.extentZ), DebugColor.CYAN);
    {
        DebugPoints(context, wPx, DebugColor.RED);
        if (def.uAxis_en)
            DebugPoints(context, wPu, DebugColor.GREEN);
        if (def.vAxis_en)
            DebugPoints(context, wPv, DebugColor.BLUE);
    }
    if (def.debug_xSections)
        addDebugEntities(context, qSx, DebugColor.RED);
    if (def.uAxis_en && def.debug_uSections)
        addDebugEntities(context, qSu, DebugColor.GREEN);
    if (def.vAxis_en && def.debug_vSections)
        addDebugEntities(context, qSv, DebugColor.BLUE);
    if (def.debug_consoleOutput)
    {
        println("----- Section Slicer -----");
        println("   Sections: " ~ toString(size(wPx) + size(wPu) + size(wPv)) ~ " (X: " ~ toString(size(wPx)) ~ ", U: " ~ toString(size(wPu)) ~ ", V: " ~ toString(size(wPv)) ~ ")");
        printTimer(SECTION_SLICER_TIMER);
        println("----------");
    }
}

function GetCoordSystem(context is Context, id, def is map) returns SlicerCoords
{
    const csysPlane = isQueryEmpty(context, def.hPlane) ? GetTopPlane(context) : evPlane(context, { "face" : def.hPlane });
    const refXAxis = isQueryEmpty(context, def.xAxis_geometry) ? csysPlane.x : normalize(project(csysPlane, csysPlane.origin + extractDirection(context, def.xAxis_geometry) * mm));
    if (tolerantEquals(vector(0,0,0), refXAxis))
        throw regenError("Slicer X-Axis must not be normal to the Horizontal Plane.", ["hPlane", "xAxis_geometry"], def.xAxis_geometry);
    const csysXAxis = cos(def.xAxis_adjustAngle) * refXAxis + sin(def.xAxis_adjustAngle) * cross(csysPlane.normal, refXAxis);

    const rotatedCoordSys = coordSystem(WORLD_ORIGIN, csysXAxis, csysPlane.normal);
    const bounds = evBox3d(context, { "topology" : def.target, "cSys" : rotatedCoordSys });
    const csysOrigin = toWorld(rotatedCoordSys, box3dCenter(bounds));
    const cSys = coordSystem(csysOrigin, csysXAxis, csysPlane.normal);

    return {
        "cSys" : cSys,
        "extentX" : bounds.maxCorner[0] - bounds.minCorner[0],
        "extentY" : bounds.maxCorner[1] - bounds.minCorner[1],
        "extentZ" : bounds.maxCorner[2] - bounds.minCorner[2],
        "refXAxis" : refXAxis
    } as SlicerCoords;
}

// Get direction vector of slicer axis, in local coordinates.
function GetLocalAxisDir(axisAngle is ValueWithUnits) returns Vector
{
    return vector(cos(axisAngle), sin(axisAngle), 0);
}

// Get direction vector of slicer axis, in world coordinates.
function GetWorldAxisDir(SC is SlicerCoords, axisAngle is ValueWithUnits) returns Vector
{
    return SC.cSys.xAxis * cos(axisAngle) + yAxis(SC.cSys) * sin(axisAngle);
}

// Returns array of section center points in world coordinates.
function GetSectionPoints(context is Context, SC is SlicerCoords, sectionWidth is ValueWithUnits, sectionSpace is ValueWithUnits, sectionOffset is ValueWithUnits, axisAngle is ValueWithUnits, qTarget is Query) returns array
{
    const bounds = evBox3d(context, { "topology" : qTarget, "cSys" : cSysRotZ(SC.cSys, axisAngle) });
    const L = bounds.maxCorner[0] - bounds.minCorner[0];
    
    sectionOffset %= (sectionWidth + sectionSpace);
    const La = (L - sectionWidth) / 2 - sectionOffset;    // Positive offset moves center section in positive direction of axis.
    const Lb = (L - sectionWidth) / 2 + sectionOffset;
    const na = floor(La / (sectionWidth + sectionSpace));
    const nb = floor(Lb / (sectionWidth + sectionSpace));
    
    const lAxisDir = GetLocalAxisDir(axisAngle);
    var lPa = [];
    for (var i = 0; i < nb; i += 1)
        lPa = append(lPa, ((nb - i) * (sectionWidth + sectionSpace) - sectionOffset) * -lAxisDir);
    if (abs(sectionOffset) < (L - sectionWidth) / 2)
        lPa = append(lPa, sectionOffset * lAxisDir);
    for (var i = 0;  i < na; i += 1)
        lPa = append(lPa, ((i + 1) * (sectionWidth + sectionSpace) + sectionOffset) * lAxisDir);    
    
    var wPa = [];
    for (var i = 0; i < size(lPa); i += 1)
        wPa = append(wPa, toWorld(SC.cSys, lPa[i]));
    return wPa;
}

function CreateSplitTool(context is Context, id is Id, splitPlane is Plane) returns Query
{
    const sketchId = SketchRect(context, id, vector(1,1)*mm, -vector(1,1)*mm, splitPlane);
    const qSplitToolTemplate = Fill(context, id, qLoopEdges(qSketchRegion(sketchId)));
    Delete(context, id, qCreatedBy(sketchId));
    return qSplitToolTemplate;
}

// Return target body split into sections.
function SplitTarget(context is Context, id is Id, SC is SlicerCoords, sectionWidth is ValueWithUnits, axisAngle is ValueWithUnits, wPa is array, qTarget is Query) returns Query
{
    const wAxisDir = GetWorldAxisDir(SC, axisAngle);
    const qSplitToolTemplate = CreateSplitTool(context, id, plane(SC.cSys.origin, wAxisDir, SC.cSys.zAxis));
    
    // Pattern split tool template into positions for splitting target body.
    var Ta = [];
    for (var i = 0; i < size(wPa); i += 1)
    {
        const offset = sectionWidth / 2 * wAxisDir;
        Ta = append(Ta, transform(wPa[i] + offset - SC.cSys.origin));
        Ta = append(Ta, transform(wPa[i] - offset - SC.cSys.origin));
    }
    const qSplitTools = Pattern(context, id, qBody(qSplitToolTemplate), Ta);
    const splitTools = evaluateQuery(context, qSplitTools);
    
    // Use split tools to divide target body into sections and spaces.
    var qRemainingTarget = qTarget;
    var qSections = qNothing();
    for (var i = 0; i < size(wPa); i += 1)
    {
        // First split op separates a 'work body' - consisting of both section to be preserved and space to be removed - from 'remaining target body'.
        var splitId = nextFeatureId(context, id);
        SplitPart(context, splitId, false, splitTools[2 * i], qRemainingTarget);
        const qWorkBody = qSplitBy(splitId, EntityType.BODY, true);
        qRemainingTarget = qSubtraction(qTarget, qWorkBody);

        // Second split op separates 'work body' into section and space regions.
        if (isQueryEmpty(context, qWorkBody))
            continue;
        splitId = nextFeatureId(context, id);
        SplitPart(context, splitId, false, splitTools[2 * i + 1], qWorkBody);
        var qSection = qSplitBy(splitId, EntityType.BODY, false);
        
        // If section consists of multiple disjoint pieces, then keep only the largest one.
        const sectionPieces = evaluateQuery(context, qSection);
        if (size(sectionPieces) > 1)
        {
            var maxVol = 0*mm3;
            for (var i = 0; i < size(sectionPieces); i += 1)
            {
                if (maxVol < evVolume(context, { "entities" : sectionPieces[i], "accuracy" : VolumeAccuracy.HIGH }))
                    qSection = sectionPieces[i];
            }
        }
        qSections = qUnion(qSections, qSection);
    }
    
    // Remove spaces between sections, any leftover split tools, and split tool template.
    const qSpaces = qSubtraction(qTarget, qSections);
    Delete(context, id, qUnion(qSpaces, qSplitTools, qSplitToolTemplate));
    return qSections;
}

function ThickenAxisSections(context is Context, id is Id, SC is SlicerCoords, sectionWidth is ValueWithUnits, axisAngle is ValueWithUnits, wPa is array, qSections is Query) returns Query
{
    // Determine which face of each section to apply thickening to.
    const wAxisDir = GetWorldAxisDir(SC, axisAngle);
    const faceOffset = sectionWidth / 2 * wAxisDir;

    var qThickenTargets = qNothing();
    var sections = evaluateQuery(context, qSections);
    for (var i = 0; i < size(sections); i += 1)
    {
        const qFaces = qOwnedByBody(sections[i], EntityType.FACE);
        const qFace0 = qCoincidesWithPlane(qFaces, plane(wPa[i] + faceOffset, wAxisDir, SC.cSys.zAxis));
        const qFace1 = qCoincidesWithPlane(qFaces, plane(wPa[i] - faceOffset, wAxisDir, SC.cSys.zAxis));
        
        const isPlanarFace0 = !isQueryEmpty(context, qGeometry(qFace0, GeometryType.PLANE));
        const isPlanarFace1 = !isQueryEmpty(context, qGeometry(qFace1, GeometryType.PLANE));
    
        // Determine which face to use as thicken target. If either face is not planar, then use the planar face. If both faces are planar, use the face with greater area.
        var qThickenTarget = qNothing();
        if (!isPlanarFace0 && !isPlanarFace1)   // In future, could implement thickening center profile of the section.
            throw regenError("Section does not have planar faces on either side. Unable to thicken.");
        else if (!isPlanarFace0)
            qThickenTarget = qFace1;
        else if (!isPlanarFace1)
            qThickenTarget = qFace0;
        else
            qThickenTarget = evArea(context, { "entities" : qFace0 }) >= evArea(context, { "entities" : qFace1 }) ? qFace0 : qFace1;
        qThickenTargets = qUnion(qThickenTargets, qThickenTarget);
    }
    
    // Apply thicken operation to all thicken targets.
    const thickenId = nextFeatureId(context, id);
    opThicken(context, thickenId, { "entities" : qThickenTargets, "thickness1" : 0*mm, "thickness2" : sectionWidth });
    
    // Discard unmodified input sections and return thickened sections.
    Delete(context, id, qSections);
    return qCreatedBy(thickenId, EntityType.BODY);
}

// Returns set of sections sliced along specified axis.
function CreateAxisSections(context is Context, id is Id, SC is SlicerCoords, sectionWidth is ValueWithUnits, axisAngle is ValueWithUnits, wPa is array, qTarget is Query, axisName is string) returns Query
precondition
{   
    size(wPa) > 0;
    !isQueryEmpty(context, qTarget);
}
{
    var qSections = SplitTarget(context, id, SC, sectionWidth, axisAngle, wPa, qTarget);
    qSections = ThickenAxisSections(context, id, SC, sectionWidth, axisAngle, wPa, qSections);

    // Rename sections.
    const sections = evaluateQuery(context, qSections);
    for (var i = 0; i < size(wPa); i += 1)
        SetPropertyName(context, sections[i], axisName ~ toString(i));
    return qSections;
}

// Given two sets of sections A and B, generate data describing intersection points between their elements. Assume each element a∈A, b∈B is a query with a single body.
function GetIntersectData(context is Context, id is Id, SC is SlicerCoords, A is array, B is array) returns array
precondition
{
    size(A) > 0;
    size(B) > 0;
}
{
    var qIntersectBodies = qNothing();
    var intersectData = [];
    for (var i = 0; i < size(A); i += 1)
    for (var j = 0; j < size(B); j += 1)
    {
        if (size(evCollision(context, { "tools" : A[i], "targets" : B[j] })) == 0)
            continue;

        var qIntersectBody = qNothing();
        try { qIntersectBody = qBody(Intersect(context, id, true, qUnion(A[i], B[j]))); }
        catch { throw regenError("Failed to create intersect body between two sections. This may occur for sections of curved geometries at specific axes angles, and can often be avoided by slightly adjusting slicer settings."); }
        const lCenter = box3dCenter(evBox3d(context, { "topology" : qIntersectBody, "cSys" : SC.cSys }));
        qIntersectBodies = qUnion(qIntersectBodies, qIntersectBody);
        intersectData = append(intersectData, {
            "z" : lCenter[2],
            "wCenter" : toWorld(SC.cSys, lCenter),
            "idxA" : i,
            "idxB" : j
        } as IntersectDatum);
    }
    Delete(context, id, qIntersectBodies);
    return intersectData;
}

// Creates an irregular hexagonal prism for use in cutting slots into sections of two non-orthogonal axes.
function CreateCutTool(context is Context, id is Id, SC is SlicerCoords, sectionWidth is ValueWithUnits, axisAngle_A is ValueWithUnits, axisAngle_B is ValueWithUnits) returns Query
precondition
{
    isAngle(axisAngle_A);
    isAngle(axisAngle_B);
    axisAngle_A != axisAngle_B;
}
{
    const phi = axisAngle_B - axisAngle_A - 90*deg;
    const s = phi < 0*deg ? -1 : 1;
    const sec_phi = 1 / cos(phi);
    const sin_phi = sin(phi);
    const tan_phi = tan(phi);
    const points = [
            sectionWidth / 2 * vector(s, sec_phi + s * tan_phi),
            sectionWidth / 2 * vector(-s, sec_phi + s * tan_phi),
            sectionWidth / 2 * vector(-1 * (s + 2 * sin_phi), sec_phi - tan_phi * (s + 2 * sin_phi)),
            sectionWidth / 2 * vector(-s, -sec_phi - s * tan_phi),
            sectionWidth / 2 * vector(s, -sec_phi - s * tan_phi),
            sectionWidth / 2 * vector(1 * (s + 2 * sin_phi), -sec_phi + tan_phi * (s + 2 * sin_phi)),
            sectionWidth / 2 * vector(s, sec_phi + s * tan_phi)
        ];
    const sketchPlane = plane(SC.cSys.origin, SC.cSys.zAxis, GetWorldAxisDir(SC, axisAngle_A));
    const sketchId = SketchPolyline(context, id, points, sketchPlane);
    const qCutToolTemplate = Extrude(context, id, qSketchRegion(sketchId), SC.extentZ / 2, SC.extentZ / 2, RangeOptions.BLIND, RangeOptions.BLIND, SC.cSys.zAxis);
    Delete(context, id, qCreatedBy(sketchId));
    return qCutToolTemplate;
}

function CutSlots(context is Context, id is Id, SC is SlicerCoords, sectionWidth is ValueWithUnits, axisAngle_A is ValueWithUnits, axisAngle_B is ValueWithUnits, reverseSlot is boolean, A is array, B is array, wPa is array, wPb is array, qSplitToolTemplate is Query)
precondition
{
    !isQueryEmpty(context, qSplitToolTemplate);
    size(A) == size(wPa);
    size(B) == size(wPb);
}
{
    const wAxisNormalA = GetWorldAxisDir(SC, axisAngle_A + 90*deg);
    const wAxisNormalB = GetWorldAxisDir(SC, axisAngle_B + 90*deg);
    const intersectData = GetIntersectData(context, id, SC, A, B);
    const qCutToolTemplate = CreateCutTool(context, id, SC, sectionWidth, axisAngle_A, axisAngle_B);

    // Pattern cut tool template and split tool templates.
    var Tcut = [];
    var Tsplit = [];
    for (var i = 0; i < size(intersectData); i += 1)
    {
        // Split tool must be positioned vertically with respect to horizontal plane.
        Tsplit = append(Tsplit, transform(intersectData[i].z * SC.cSys.zAxis));
        // Cut tool must be positioned horizontally, at intersection point between two sections.
        const wLineA = line(wPa[intersectData[i].idxA], wAxisNormalA);
        const wLineB = line(wPb[intersectData[i].idxB], wAxisNormalB);
        const intersectPoint = intersection(wLineA, wLineB).intersection;
        Tcut = append(Tcut, transform(intersectPoint - SC.cSys.origin));
    }
    const qSplitTools = Pattern(context, id, qBody(qSplitToolTemplate), Tsplit);
    const splitTools = evaluateQuery(context, qSplitTools);
    const qCutTools = Pattern(context, id, qBody(qCutToolTemplate), Tcut);
    const cutTools = evaluateQuery(context, qCutTools);
    Delete(context, id, qCutToolTemplate);
    
    // Use each split tool to split its respective cut tool into upper and lower halves. Group cut tools by section.
    var qCutToolsA = makeArray(size(A), qNothing());
    var qCutToolsB = makeArray(size(B), qNothing());
    for (var i = 0; i < size(intersectData); i += 1)
    {
        const splitId = nextFeatureId(context, id);
        SplitPart(context, splitId, false, splitTools[i], cutTools[i]);
        qCutToolsA[intersectData[i].idxA] = qUnion(qCutToolsA[intersectData[i].idxA], qSplitBy(splitId, EntityType.BODY, !reverseSlot));
        qCutToolsB[intersectData[i].idxB] = qUnion(qCutToolsB[intersectData[i].idxB], qSplitBy(splitId, EntityType.BODY, reverseSlot));
    }
    
    // Apply cuts to each section.
    for (var i = 0; i < size(A); i += 1)
        if (!isQueryEmpty(context, qCutToolsA[i]))
            Subtract(context, id, false, qCutToolsA[i], A[i]);
    for (var i = 0; i < size(B); i += 1)
        if (!isQueryEmpty(context, qCutToolsB[i]))
            Subtract(context, id, false, qCutToolsB[i], B[i]);
}
