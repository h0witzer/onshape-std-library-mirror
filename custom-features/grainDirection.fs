/*
    Set Grain Direction

    Companion feature to Auto Layout+. Marks the grain (alignment) direction on planar parts
    by imprinting a directional arrow "sigil" onto the largest planar face of each selected
    part. The arrow is imprinted with opSplitFace, creating a small arrow-shaped island face;
    a GrainDirectionAttribute is stamped on the arrow's long shaft edge (and on the island
    face for easy removal).

    Auto Layout+ later senses the presence of the sigil (via the attribute on the edge) and
    reads the grain axis live from that edge. All of the "which way does the grain run" logic
    lives here - Auto Layout does not know or care which mode produced a sigil. It simply locks
    the part's orientation to the grain axis, allowing only the arrow direction or its 180 flip.

    Grain modes:
        Shared direction - project one picked direction onto every selected part's face.
        Longest edge     - use the longest straight edge of each part's largest face.
        Compass          - aim each part's arrow at a single point/pole, from either the face
                           bounding-box center (default) or the face area centroid.

            1.0 - Initial version - Derek Van Allen
*/

FeatureScript 3029;
import(path : "onshape/std/geometry.fs", version : "3029.0");
// autoLayoutTypes.fs - GrainDirectionAttribute, SheetGrainAxis.
// NOTE: after re-publishing autoLayoutTypes.fs (it gains GrainDirectionAttribute), update the
// version id below (and the matching import in autoLayout.fs) to the new published version.
import(path : "bb79595d1ad4e6528fb60762", version : "20987b283a5fd1abb9b2d6f5");
// getLargestFace helper (same document Auto Layout uses, so the sigil lands on the exact face
// Auto Layout treats as the cutting plane).
import(path : "f4e7238da5afaf5a3f1498c0/7a207cd9ceffd98f8f03ad47/22d17eb94c85900576fbf53e", version : "d8911b6f752a07bc27cfc8dc");

// Typed presence marker shared with Auto Layout+.
const GRAIN_ATTRIBUTE = "GrainDirection" as GrainDirectionAttribute;

// Default arrow length as a fraction of the face's shorter in-plane dimension.
const DEFAULT_ARROW_SCALE = 0.4;

// Arrow is never allowed to exceed this fraction of the shorter face dimension, so the imprint
// stays comfortably inside the face boundary and always forms a closed interior island.
const MAX_ARROW_SCALE = 0.85;

// Below this the face is too small to carry a meaningful sigil.
const MIN_ARROW_LENGTH = 1e-4 * meter;

const ARROW_LENGTH_BOUNDS =
{
            (millimeter) : [1.0, 25.0, 1.0e5],
            (centimeter) : 2.5,
            (inch) : 1.0
        } as LengthBoundSpec;

export enum GrainOperationType
{
    annotation { "Name" : "Create sigils" }
    CREATE,
    annotation { "Name" : "Remove sigils" }
    REMOVE
}

export enum GrainMode
{
    annotation { "Name" : "Shared direction" }
    SHARED_DIRECTION,
    annotation { "Name" : "Longest edge" }
    LONGEST_EDGE,
    annotation { "Name" : "Compass (aim at pole)" }
    COMPASS
}

export enum CompassAnchor
{
    annotation { "Name" : "Bounding-box center" }
    BBOX_CENTER,
    annotation { "Name" : "Area centroid" }
    AREA_CENTROID
}

// Which face of each part receives the sigil.
export enum SigilTarget
{
    annotation { "Name" : "Largest planar face" }
    LARGEST_FACE,
    annotation { "Name" : "Selected faces" }
    SELECTED_FACES
}

annotation { "Feature Type Name" : "Set Grain Direction",
        "Feature Type Description" : "Marks grain direction on planar parts by imprinting a " ~
        "directional arrow sigil on the largest planar face, or on faces you pick. Auto Layout+ " ~
        "reads these sigils and locks each part's nesting orientation to the grain axis (allowing " ~
        "only the arrow direction or its 180 flip). Use 'Remove sigils' to strip the arrows and " ~
        "clean the parts." }
export const setGrainDirection = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Operation", "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.operationType is GrainOperationType;

        if (definition.operationType == GrainOperationType.CREATE)
        {
            annotation { "Name" : "Sigil placement", "UIHint" : UIHint.HORIZONTAL_ENUM }
            definition.sigilTarget is SigilTarget;

            if (definition.sigilTarget == SigilTarget.LARGEST_FACE)
            {
                // Body selection - window-drag friendly; each part gets its sigil on its largest
                // planar face. Kept separate from the face picker below so bulk part selection
                // doesn't require clicking individual faces.
                annotation { "Name" : "Parts", "Filter" : EntityType.BODY && BodyType.SOLID }
                definition.parts is Query;
            }
            else // SELECTED_FACES
            {
                // Explicit per-face selection for parts whose sigil must live on a non-largest face.
                annotation { "Name" : "Sigil faces", "Filter" : EntityType.FACE && GeometryType.PLANE && SketchObject.NO }
                definition.faces is Query;
            }

            annotation { "Name" : "Grain mode", "UIHint" : UIHint.SHOW_LABEL }
            definition.grainMode is GrainMode;

            if (definition.grainMode == GrainMode.SHARED_DIRECTION)
            {
                annotation { "Name" : "Direction reference",
                            "Filter" : QueryFilterCompound.ALLOWS_DIRECTION || BodyType.MATE_CONNECTOR,
                            "MaxNumberOfPicks" : 1 }
                definition.directionRef is Query;
            }

            if (definition.grainMode == GrainMode.COMPASS)
            {
                annotation { "Name" : "Pole (point)",
                            "Filter" : QueryFilterCompound.ALLOWS_VERTEX || (EntityType.BODY && BodyType.MATE_CONNECTOR),
                            "MaxNumberOfPicks" : 1 }
                definition.pole is Query;

                annotation { "Name" : "Aim from", "UIHint" : UIHint.SHOW_LABEL }
                definition.aimFrom is CompassAnchor;
            }

            annotation { "Name" : "Override arrow size", "UIHint" : "DISPLAY_SHORT" }
            definition.overrideArrowSize is boolean;

            if (definition.overrideArrowSize)
            {
                annotation { "Name" : "Arrow length", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
                isLength(definition.arrowLength, ARROW_LENGTH_BOUNDS);
            }
        }

        if (definition.operationType == GrainOperationType.REMOVE)
        {
            // Distinct parameter id from the create-mode "Parts" (same display name) - FS rejects
            // the same parameter id declared in two separate branches as a duplicate.
            annotation { "Name" : "Parts", "Filter" : EntityType.BODY && BodyType.SOLID }
            definition.removeParts is Query;
        }
    }
    {
        // === Remove mode: strip sigils and heal the selected parts clean ===
        if (definition.operationType == GrainOperationType.REMOVE)
        {
            const removeBodies = evaluateQuery(context, definition.removeParts);
            if (size(removeBodies) == 0)
                throw regenError("Select at least one part.", ["removeParts"]);
            clearSigil(context, id + "remove", qUnion(removeBodies));
            return;
        }

        // === Create mode ===
        // Resolve the shared grain inputs once, up front, so a bad selection fails fast.
        var refDir = undefined;
        if (definition.grainMode == GrainMode.SHARED_DIRECTION)
        {
            refDir = try silent(extractDirection(context, definition.directionRef));
            if (refDir == undefined)
                throw regenError("Could not read a direction from the reference.", ["directionRef"]);
        }

        var polePoint = undefined;
        if (definition.grainMode == GrainMode.COMPASS)
        {
            polePoint = extractPolePoint(context, definition.pole);
            if (polePoint == undefined)
                throw regenError("Could not read a point from the pole selection.", ["pole"]);
        }

        const arrowOverride = (definition.overrideArrowSize == true) ? definition.arrowLength : undefined;

        // Build the (body, face) targets - exactly one sigil face per part - per the placement mode.
        // Pre-existing sigils on the involved bodies are cleared up front so faces measure clean.
        var targets = [];    // array of { "body" : Query, "face" : Query }
        var ambiguousParts = 0;
        var skipped = 0;

        if (definition.sigilTarget == SigilTarget.SELECTED_FACES)
        {
            if (isQueryEmpty(context, definition.faces))
                throw regenError("Select at least one sigil face.", ["faces"]);

            clearSigil(context, id + "clear", qOwnerBody(definition.faces));

            // Group the selected faces by owner body. A part can hold only one sigil, so a body with
            // more than one selected face is ambiguous (which face wins?) - flag it and skip so the
            // user can correct the selection rather than get an arbitrary/brittle result.
            const ownerBodies = evaluateQuery(context, qOwnerBody(definition.faces));
            for (var b = 0; b < size(ownerBodies); b += 1)
            {
                const facesOnBody = evaluateQuery(context, qIntersection([
                            definition.faces,
                            qOwnedByBody(ownerBodies[b], EntityType.FACE)
                        ]));

                if (size(facesOnBody) > 1)
                {
                    ambiguousParts += 1;
                    continue;
                }
                if (size(facesOnBody) == 0)
                    continue;
                if (!isPlanarFace(context, facesOnBody[0]))
                {
                    skipped += 1;
                    continue;
                }

                targets = append(targets, { "body" : ownerBodies[b], "face" : facesOnBody[0] });
            }
        }
        else // LARGEST_FACE
        {
            if (isQueryEmpty(context, definition.parts))
                throw regenError("Select at least one part.", ["parts"]);

            clearSigil(context, id + "clear", definition.parts);

            const bodies = evaluateQuery(context, definition.parts);
            for (var b = 0; b < size(bodies); b += 1)
            {
                const face = largestPlanarFace(context, bodies[b]);
                if (isQueryEmpty(context, face))
                {
                    skipped += 1;
                    continue;
                }
                targets = append(targets, { "body" : bodies[b], "face" : face });
            }
        }

        // Stamp one sigil per target.
        for (var i = 0; i < size(targets); i += 1)
        {
            const face = targets[i].face;
            const fp = facePlaneAndCenter(context, face);

            const grainAxis = computeGrainAxis(context, {
                        "mode" : definition.grainMode,
                        "face" : face,
                        "plane" : fp.plane,
                        "center" : fp.center,
                        "refDir" : refDir,
                        "polePoint" : polePoint,
                        "aimFrom" : (definition.grainMode == GrainMode.COMPASS) ? definition.aimFrom : CompassAnchor.BBOX_CENTER
                    });

            if (grainAxis == undefined)
            {
                skipped += 1;
                continue;
            }

            // NOTE: during bring-up this is intentionally NOT wrapped in `try silent`, so any
            // imprint failure surfaces the real error instead of being hidden as a silent skip.
            // Re-wrap in `try silent` for per-part resilience once imprinting is confirmed working.
            const stamped = stampSigil(context, id + ("sigil" ~ i), {
                        "face" : face,
                        "plane" : fp.plane,
                        "center" : fp.center,
                        "dimMin" : fp.dimMin,
                        "grainAxis" : grainAxis,
                        "arrowOverride" : arrowOverride
                    });

            if (!stamped)
                skipped += 1;
        }

        if (ambiguousParts > 0)
            reportFeatureInfo(context, id, ambiguousParts ~ " part(s) had more than one face selected and were skipped - select a single sigil face per part.");

        if (skipped > 0)
            reportFeatureInfo(context, id, skipped ~ " part(s) skipped (no planar face, face too small, no clear area to place the sigil, or indeterminate grain direction).");
    });

// True if the face is planar.
function isPlanarFace(context is Context, face is Query) returns boolean
{
    return try silent(evPlane(context, { "face" : face })) != undefined;
}

// Returns the largest planar face of a body, or qNothing() if the largest face is not planar.
function largestPlanarFace(context is Context, body is Query) returns Query
{
    const face = getLargestFace(context, body);
    if (isQueryEmpty(context, face) || !isPlanarFace(context, face))
        return qNothing();

    return face;
}

// Returns { plane, center (world point at the face bbox center - used for DIRECTION and as the first
// placement try), dimMin (shorter in-plane extent, for arrow sizing) }.
function facePlaneAndCenter(context is Context, face is Query) returns map
{
    const facePlane = evPlane(context, { "face" : face });
    const cSys = coordSystem(facePlane.origin, facePlane.x, facePlane.normal);
    const bbox = evBox3d(context, { "topology" : face, "cSys" : cSys });

    const centerLocal = (bbox.minCorner + bbox.maxCorner) / 2;
    const centerWorld = toWorld(cSys, centerLocal);

    const dimX = abs(bbox.maxCorner[0] - bbox.minCorner[0]);
    const dimY = abs(bbox.maxCorner[1] - bbox.minCorner[1]);

    return {
        "plane" : facePlane,
        "center" : centerWorld,
        "dimMin" : (dimX < dimY) ? dimX : dimY
    };
}

// Computes the in-plane grain axis (unit vector) for one part, or undefined if indeterminate.
// Falls back to the longest face edge when the primary direction is parallel to the face normal.
function computeGrainAxis(context is Context, args is map)
{
    const n = normalize(args.plane.normal);

    var raw = undefined;
    if (args.mode == GrainMode.SHARED_DIRECTION)
    {
        raw = normalize(args.refDir);
    }
    else if (args.mode == GrainMode.LONGEST_EDGE)
    {
        raw = longestEdgeDir(context, args.face);
    }
    else // COMPASS
    {
        var anchor = args.center;
        if (args.aimFrom == CompassAnchor.AREA_CENTROID)
            anchor = evApproximateCentroid(context, { "entities" : args.face });

        const delta = args.polePoint - anchor;
        if (norm(delta) > TOLERANCE.zeroLength * meter)
            raw = normalize(delta);
    }

    const primary = projectIntoPlane(raw, n);
    if (primary != undefined)
        return primary;

    // Fallback: longest edge of the face.
    return projectIntoPlane(longestEdgeDir(context, args.face), n);
}

// Removes any component of dir along n and returns the normalized in-plane vector, or undefined
// if dir is undefined or effectively parallel to n.
function projectIntoPlane(dir, n is Vector)
{
    if (dir == undefined)
        return undefined;

    const inPlane = dir - (dot(dir, n) * n);
    if (norm(inPlane) < 1e-6)
        return undefined;

    return normalize(inPlane);
}

// Direction of the longest straight edge of a face, or undefined if none.
function longestEdgeDir(context is Context, face is Query)
{
    const edges = evaluateQuery(context, qGeometry(qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE), GeometryType.LINE));
    if (size(edges) == 0)
        return undefined;

    var best = undefined;
    var bestLen = 0 * meter;
    for (var e in edges)
    {
        const len = evLength(context, { "entities" : e });
        if (len > bestLen)
        {
            bestLen = len;
            best = evAxis(context, { "axis" : e }).direction;
        }
    }
    return best;
}

// Extracts a 3D point from a vertex or mate-connector pick, or undefined.
function extractPolePoint(context is Context, q is Query)
{
    var p = try silent(evVertexPoint(context, { "vertex" : q }));
    if (p == undefined)
    {
        const mc = try silent(evMateConnector(context, { "mateConnector" : q }));
        if (mc != undefined)
            p = mc.origin;
    }
    return p;
}

// The grain-notation symbol outline as 2D points in the arrow's local frame (+x = grain axis,
// centered on origin): a double-ended arrow with a single-sided (half) barb at each end on OPPOSITE
// sides, point-symmetric about the center. Six edges in three equal-length pairs - two long
// grain-parallel shaft edges (segments 0->1, 3->4), two short perpendicular barb steps (1->2, 4->5),
// two diagonal barb edges (2->3, 5->0). The grain axis is read from a long shaft edge. Tweak the
// three fractions to restyle. Shared by the imprint sketch and the placement footprint check.
function arrowPoints(L is ValueWithUnits) returns array
{
    const half = L / 2;          // half the total length (grain extent)
    const xNotch = 0.25 * L;     // where each barb/step meets the shaft, measured from center
    const shaftHalf = 0.05 * L;  // half the shaft-band thickness (also the far-tip offset)
    const headHalf = 0.11 * L;   // barb-tip spread from the grain axis

    return [
                vector(-half, shaftHalf),     // 0: left tip (upper)
                vector(xNotch, shaftHalf),    // 1: shaft top, inner end (right head base)
                vector(xNotch, headHalf),     // 2: right barb tip (points up)
                vector(half, -shaftHalf),     // 3: right tip (lower)
                vector(-xNotch, -shaftHalf),  // 4: shaft bottom, inner end (left head base)
                vector(-xNotch, -headHalf),   // 5: left barb tip (points down)
                vector(-half, shaftHalf)      // close back to 0
            ];
}

// Points that must all lie on the face material for the arrow to imprint cleanly: the outline
// vertices plus samples along the shaft centerline (the latter catch a hole the shaft would span,
// e.g. a plate with a central hole).
function footprintSamplePoints(L is ValueWithUnits) returns array
{
    const half = L / 2;
    const xNotch = 0.25 * L;
    const shaftHalf = 0.05 * L;
    const headHalf = 0.11 * L;
    const z = 0 * meter;

    return [
                vector(-half, shaftHalf),
                vector(xNotch, shaftHalf),
                vector(xNotch, headHalf),
                vector(half, -shaftHalf),
                vector(-xNotch, -shaftHalf),
                vector(-xNotch, -headHalf),
                vector(-half, z),
                vector(-half / 2, z),
                vector(z, z),
                vector(half / 2, z),
                vector(half, z)
            ];
}

// True if the arrow, centered at `centerWorld` and oriented along `g`, has every footprint sample
// on the face material (each sample's distance to the trimmed face is ~0). A sample landing in a
// hole or off the outer boundary reads a positive distance and fails the check.
function placementFits(context is Context, face is Query, centerWorld is Vector, g is Vector, normal is Vector, samples is array) returns boolean
{
    const arrowCSys = coordSystem(centerWorld, g, normal);
    for (var p in samples)
    {
        const world = toWorld(arrowCSys, vector(p[0], p[1], 0 * meter));
        if (evDistance(context, { "side0" : world, "side1" : face }).distance > TOLERANCE.zeroLength * meter)
            return false;
    }
    return true;
}

// Finds a placement point for the arrow whose full footprint lies on material. Tries the bbox
// center first (the common case). If that's in a void (L-shape, central hole), it draws a few
// isoparametric curves across the face - Onshape trims them to the material, so each resultant
// curve edge is a solid span and its midpoint is a naturally centered candidate with clearance
// along that span. This is much cheaper than blanket-sampling the face. Returns undefined if
// nothing fits (caller skips + reports the part).
function findSigilPlacement(context is Context, id is Id, args is map, g is Vector, L is ValueWithUnits)
{
    const face = args.face;
    const normal = args.plane.normal;
    const samples = footprintSamplePoints(L);

    // Fast path: the bbox center (keeps the sigil centered whenever the material allows).
    if (placementFits(context, face, args.center, g, normal, samples))
        return args.center;

    // Fallback: trimmed isoparametric curves -> material spans -> span midpoints.
    const curvesId = id + "curves";
    opCreateCurvesOnFace(context, curvesId, {
                "curveDefinition" : [
                    curveOnFaceDefinition(face, FaceCurveCreationType.DIR1_ISO, ["u1", "u2", "u3"], [0.25, 0.5, 0.75]),
                    curveOnFaceDefinition(face, FaceCurveCreationType.DIR2_ISO, ["v1", "v2", "v3"], [0.25, 0.5, 0.75])
                ]
            });

    // Read each span's midpoint (arc-length parameter 0.5) + length before deleting the temp curves.
    var spans = [];
    for (var e in evaluateQuery(context, qCreatedBy(curvesId, EntityType.EDGE)))
    {
        const mid = try silent(evEdgeTangentLine(context, { "edge" : e, "parameter" : 0.5 }).origin);
        if (mid != undefined)
            spans = append(spans, { "pt" : mid, "len" : evLength(context, { "entities" : e }) });
    }

    const curveBodies = qCreatedBy(curvesId, EntityType.BODY);
    if (!isQueryEmpty(context, curveBodies))
        opDeleteBodies(context, id + "delCurves", { "entities" : curveBodies });

    // Longest spans first (most clearance), verifying the actual oriented footprint fits.
    spans = sort(spans, function(a, b)
    {
        return b.len - a.len;
    });

    for (var s in spans)
    {
        if (placementFits(context, face, s.pt, g, normal, samples))
            return s.pt;
    }

    return undefined;
}

// Sketches the grain-notation symbol (a closed, double-ended, single-sided-barb arrow aligned to
// the grain axis), imprints it on the face with opSplitFace, and stamps the grain attribute on a
// long shaft edge and the island face. Returns true on success.
function stampSigil(context is Context, id is Id, args is map) returns boolean
{
    const facePlane = args.plane;
    const g = args.grainAxis;

    var L = (args.arrowOverride != undefined) ? args.arrowOverride : (DEFAULT_ARROW_SCALE * args.dimMin);
    const cap = MAX_ARROW_SCALE * args.dimMin;
    if (L > cap)
        L = cap;
    if (L < MIN_ARROW_LENGTH)
        return false;

    // Direction (g) was already computed from the bbox center; only the PLACEMENT moves. Find a spot
    // whose arrow footprint lies fully on material - this handles L-shapes / center-holed parts where
    // the bbox center sits in a void.
    const placement = findSigilPlacement(context, id + "place", args, g, L);
    if (placement == undefined)
        return false;

    // Sketch frame: origin at the placement point, local +x along the grain axis.
    const sketchCSys = coordSystem(placement, g, facePlane.normal);
    const sketchId = id + "sketch";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(sketchCSys) });

    skPolyline(sketch, "arrow", { "points" : arrowPoints(L), "constrained" : true });
    skSolve(sketch);

    // Imprint the arrow onto the face. The sketch edges lie ON the target face, so they must be
    // projected with an explicit direction (the face normal): the default NORMAL_TO_TARGET
    // projection is degenerate at zero distance and silently fails to split. This mirrors how
    // Onshape's own Split feature handles on-face sketch edge tools (setDirectionForEdgeTools).
    const splitId = id + "split";
    const splitResult = opSplitFace(context, splitId, {
                "faceTargets" : args.face,
                "edgeTools" : qCreatedBy(sketchId, EntityType.EDGE),
                "projectionType" : ProjectionType.DIRECTION,
                "direction" : facePlane.normal
            });

    // Clean up the construction sketch; the arrow now lives as an imprint on the part face, so the
    // floating sketch curves/region are no longer needed.
    const sketchBodies = qCreatedBy(sketchId, EntityType.BODY);
    if (!isQueryEmpty(context, sketchBodies))
        opDeleteBodies(context, id + "delSketch", { "entities" : sketchBodies });

    // Edges the split produced. qCreatedBy returns only NEW edges (not the face's pre-existing
    // boundary), so prefer it; fall back to the documented splittingEdges return (FS 2863+) only
    // if it comes back empty.
    var candidateEdges = qCreatedBy(splitId, EntityType.EDGE);
    if (isQueryEmpty(context, candidateEdges) && size(splitResult.splittingEdges) > 0)
        candidateEdges = qUnion(splitResult.splittingEdges);
    candidateEdges = qEntityFilter(candidateEdges, EntityType.EDGE);

    // The shaft = longest straight edge parallel to the grain axis. Cap the length at the arrow
    // size (with a hair of tolerance): the only grain-parallel edges the arrow creates are its
    // two shaft sides, so this also excludes any long boundary edge that might slip in.
    var shaft = undefined;
    var shaftLen = 0 * meter;
    for (var e in evaluateQuery(context, candidateEdges))
    {
        const dir = try silent(evAxis(context, { "axis" : e }).direction);
        if (dir == undefined || !parallelVectors(dir, g))
            continue;

        const len = evLength(context, { "entities" : e });
        if (len > L * 1.001)
            continue;

        if (len > shaftLen)
        {
            shaftLen = len;
            shaft = e;
        }
    }

    if (shaft == undefined)
        return false;

    setAttribute(context, { "entities" : shaft, "attribute" : GRAIN_ATTRIBUTE });

    // Also mark the arrow island face (the smaller of the two faces along the shaft edge) so
    // Remove mode can find and delete it directly.
    const adjacentFaces = evaluateQuery(context, qAdjacent(shaft, AdjacencyType.EDGE, EntityType.FACE));
    if (size(adjacentFaces) >= 2)
    {
        const a0 = evArea(context, { "entities" : adjacentFaces[0] });
        const a1 = evArea(context, { "entities" : adjacentFaces[1] });
        const island = (a0 < a1) ? adjacentFaces[0] : adjacentFaces[1];
        setAttribute(context, { "entities" : island, "attribute" : GRAIN_ATTRIBUTE });
    }

    return true;
}

// Deletes the arrow island face(s) on the given bodies and heals the coplanar imprint back
// flat, then clears any lingering grain attributes. Safe to call on bodies without a sigil.
function clearSigil(context is Context, id is Id, bodies is Query)
{
    const sigilFaces = qIntersection([
                qOwnedByBody(bodies, EntityType.FACE),
                qEntityFilter(qAttributeQuery(GRAIN_ATTRIBUTE), EntityType.FACE)
            ]);

    if (!isQueryEmpty(context, sigilFaces))
    {
        // Coplanar imprint island -> delete-and-heal merges it back into the parent face. If a
        // sigil cannot be healed for some reason, we still clear the attribute below so Auto
        // Layout stops treating the part as grained.
        try silent
        {
            opDeleteFace(context, id + "del", {
                        "deleteFaces" : sigilFaces,
                        "includeFillet" : false,
                        "capVoid" : false,
                        "leaveOpen" : false
                    });
        }
    }

    removeAttributes(context, {
                "entities" : qOwnedByBody(bodies),
                "attributePattern" : GRAIN_ATTRIBUTE
            });
}
