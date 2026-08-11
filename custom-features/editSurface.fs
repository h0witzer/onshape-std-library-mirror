FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");
import(path : "onshape/std/evaluate.fs", version : "3044.0");
import(path : "onshape/std/geomOperations.fs", version : "3044.0");
import(path : "onshape/std/query.fs", version : "3044.0");
import(path : "onshape/std/vector.fs", version : "3044.0");
import(path : "onshape/std/units.fs", version : "3044.0");
import(path : "onshape/std/valueBounds.fs", version : "3044.0");
import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0");
import(path : "onshape/std/curveGeometry.fs", version : "3044.0");
import(path : "onshape/std/error.fs", version : "3044.0");
import(path : "onshape/std/containers.fs", version : "3044.0");
import(path : "onshape/std/manipulator.fs", version : "3044.0");
import(path : "onshape/std/coordSystem.fs", version : "3044.0");   // WORLD_ORIGIN, toWorld, fromWorld
import(path : "onshape/std/transform.fs", version : "3044.0");     // transform, identityTransform
import(path : "onshape/std/matrix.fs", version : "3044.0");        // matrix, svd, for the planarize fit
import(path : "onshape/std/math.fs", version : "3044.0");
import(path : "onshape/std/debug.fs", version : "3044.0");
import(path : "onshape/std/approximationUtils.fs", version : "3044.0"); // DEGREE_BOUND, MAX_DEGREE
import(path : "onshape/std/surfacetype.gen.fs", version : "3044.0");

// splineRefinementUtils.fs, imported as a SAME-DOCUMENT tab (bare tab id, matching what
// splineRefinementTester.fs does) rather than as the separately published artifact the two tween
// features pin. Deliberate while this feature is co-developed with the module: module edits land
// here immediately, with no republish and no version-bump cascade across consumers. It becomes a
// cross-document import only once this stops changing in lockstep with the module. The pinned
// cross-document form, for whenever that day comes:
// import(path : "eca0e7b6ed29c5239f39f868/36185a3777394c9ecb0bfc3e/9a2b77793cdc37bace6d915a", version : "db7f981bb900fa1e8c01effc");
import(path : "9a2b77793cdc37bace6d915a", version : "6b2f05959547b5e81628762a");

/*
 * EDIT SURFACE — the surface analog of std editCurve.fs.
 * Full design: docs/specs/EDIT_SURFACE_SPEC.md.
 *
 * The UI is a deliberate near-transcription of editCurve.fs: same parameter names, same group
 * structure, same driving-parameter pattern, same manipulator set, same edit-logic behaviours.
 * Someone who knows Edit curve should not have to learn anything to use this. Where this file
 * diverges it is because a surface genuinely differs from a curve, and every such spot says so.
 *
 * The three divergences, all forced:
 *   1. Control points are indexed by (u, v), not by a single integer. Stored as two fields so the
 *      dialog reads "Control point (2, 3)" rather than a flat index that silently means something
 *      different the moment the net's column count changes.
 *   2. Elevation takes a target degree PER DIRECTION.
 *   3. PERIODIC directions expose only the FUNDAMENTAL control points, never the stored wrap
 *      padding — see applyControlPointEdits. Showing a handle for a padding row would let a user
 *      break the overlap condition that makes the surface closed, i.e. hand the kernel a surface
 *      whose representation claims closure its data does not have.
 *
 * UVN (Control polygon) from editCurve is not implemented. On a curve the control polygon gives two
 * natural directions (toward the previous and next control point) plus a perpendicular; on a net
 * there are four in-net directions plus the normal, which is a different control scheme rather than
 * a port of this one. UVN (Tangent) IS implemented and is better defined here than on a curve,
 * because a surface's normal is unambiguous where a curve needs a curvature frame that degenerates
 * at inflections.
 *
 * TWO PIECES COME FROM freeFormDeformation.fs RATHER THAN FROM editCurve.fs, back-ported because a
 * control net wants them for exactly the reasons a deformation lattice does. Both act on the XYZ
 * mode's multi-point selection, which is the thing the two features have in common that a curve's
 * single control point does not.
 *
 *   A. THE SELECTION HANDLE IS A fullTriadManipulator, not a plain triad — rotation rings as well as
 *      translation arrows. Rotating a selected block about its own centre is what twists a row of
 *      control points or rolls a patch edge, and nothing else in the feature can express it.
 *      EDIT_SURFACE_SPEC.md section 3 called this out as the one deliberate extension over
 *      routingCurve.fs's template, which stores the rotation only to keep its triad oriented.
 *      It is unconditional, which is what keeps ONE writer on the selection: see
 *      FREE_FORM_DEFORMATION_SPEC.md section 6.5 for the class of bug two manipulators writing the
 *      same points through different storage produced there.
 *
 *   B. PLANARIZE SELECTION is a BUTTON that flattens the selected control points onto their own
 *      least-squares plane, once, writing ordinary point overrides. It is NOT editCurve.fs's
 *      `planarize`, which is a persistent toggle that flattens the WHOLE curve against a chosen
 *      reference plane; this is FFD's one-shot edit on a selection, and the difference is the whole
 *      answer to the "of no clear value" note this comment used to carry. Flattening an entire net
 *      is indeed of little value. Flattening the four points a user just dragged out of alignment,
 *      or a boundary row that needs to sit flat, is an everyday move.
 */

/**
 * An `IntegerBoundSpec` for control point indices, per direction. Mirrors editCurve.fs's bound of
 * the same name — the ceiling is the same because it is the same kind of quantity, even though a
 * net has two of them.
 */
/**
 * Refinement can legitimately push a direction well past std's MAX_CONTROL_POINTS (100), which is a
 * cap on APPROXIMATION inputs, not on how many control points an exact refinement may produce. A net
 * refined for editing detail passes 100 per direction without difficulty, so the index and target
 * bounds here use their own ceiling rather than borrowing that one.
 */
export const MAX_SURFACE_CONTROL_POINTS = 1000;

export const CONTROL_POINT_INDEX_BOUND =
{
            (unitless) : [0, 0, MAX_SURFACE_CONTROL_POINTS - 1]
        } as IntegerBoundSpec;

/**
 * Target degree per direction, minimum 1.
 *
 * std's `DEGREE_BOUND` starts at 2, which is right for curve approximation and wrong for surfaces.
 * Degree 1 in one direction is how a RULED surface is expressed, and a degree 1 x degree 3 patch is
 * an ordinary, useful thing to ask for — it is the natural form for developable strips. Forcing the
 * minimum to 2 elevates a planar or ruled input for nothing, adding control points and destroying
 * the very property that makes the surface developable.
 */
export const SURFACE_DEGREE_BOUND =
{
            (unitless) : [1, 3, MAX_DEGREE]
        } as IntegerBoundSpec;

/** Target control point count per direction, for the Refine group. Minimum 2, since a direction
    needs at least degree + 1 and degree >= 1. */
export const SURFACE_CONTROL_POINT_COUNT_BOUND =
{
            (unitless) : [2, 4, MAX_SURFACE_CONTROL_POINTS]
        } as IntegerBoundSpec;

/**
 * The tolerance the KERNEL APPROXIMATOR accepts. evApproximateBSplineSurface documents its
 * tolerance as "minimum 1e-8, maximum 1e-4" metres and throws outside that range.
 *
 * This is NOT the user-facing bound. Those are two different quantities and conflating them was a
 * real bug: a merge that smooths a fillet away deviates by millimetres or centimetres, so capping
 * the user's ACCEPTABLE deviation at 0.1 mm meant the "above the requested tolerance" warning could
 * never be satisfied by any achievable result. The user's tolerance now uses std's TOLERANCE_BOUND
 * (up to a metre, exactly what editCurve allows) and is clamped to this range only at the moment it
 * is handed to the kernel.
 */
const KERNEL_APPROXIMATION_TOLERANCE_MINIMUM = 1e-8 * meter;
const KERNEL_APPROXIMATION_TOLERANCE_MAXIMUM = 1e-4 * meter;

/** The user's tolerance, clamped into what the kernel approximator will accept. Clamping is right
    rather than throwing: a user asking to ACCEPT 20 mm of smoothing is not asking the reader to be
    sloppy, so the read stays as tight as the API allows and the loose tolerance governs the
    result instead. */
function kernelApproximationTolerance(definition is map) returns number
{
    const requested = definition.approximate ? definition.approximationTolerance : KERNEL_APPROXIMATION_TOLERANCE_MAXIMUM;
    return min(max(requested, KERNEL_APPROXIMATION_TOLERANCE_MINIMUM), KERNEL_APPROXIMATION_TOLERANCE_MAXIMUM) / meter;
}

/**
 * Edit point mode. Mirrors editCurve.fs's enum of the same name, minus UVN_CONTROL_POLYGON (see
 * the file header).
 */
export enum EditPointMode
{
    annotation { "Name" : "XYZ" }
    XYZ,
    annotation { "Name" : "UVN (Tangent)" }
    UVN_TANGENT
}

/**
 * Where the rebuilt surface goes. The two are different OUTPUTS, not a good option and a
 * compromised one:
 *
 * REPLACE_FACE swaps the surface underneath the original face, which keeps its perimeter, holes and
 * inner loops (see replaceTargetFace). This is the deliverable.
 *
 * NEW_BODY leaves the rebuilt surface as opCreateBSplineSurface made it: the FULL, UNTRIMMED
 * surface, covering its whole parameter range. That is not a failure to trim — it is the surface
 * the control net actually describes, and the one the handles sit on. Trimming here is not merely
 * unimplemented but unavailable; see replaceTargetFace.
 */
export enum EditSurfaceResult
{
    annotation { "Name" : "Replace face" }
    REPLACE_FACE,
    annotation { "Name" : "New surface body (untrimmed)" }
    NEW_BODY,
    annotation { "Name" : "New surface body (trimmed) [experimental]" }
    NEW_BODY_TRIMMED
}

const INDEX_MANIPULATOR = "indexManipulator";
const INDICES_MANIPULATOR = "indicesManipulator";
const SELECTION_TRANSFORM_MANIPULATOR = "selectionTransformManipulator";

const U_TANGENT_MANIPULATOR = "UTangentManipulator";
const V_TANGENT_MANIPULATOR = "VTangentManipulator";
const N_MANIPULATOR = "NTangentManipulator";
const LINEAR_MANIPULATORS = [U_TANGENT_MANIPULATOR, V_TANGENT_MANIPULATOR, N_MANIPULATOR];

/**
 * The live selection rotation is stored as a FLAT nine-value array, row by row, never as a Matrix.
 *
 * A precondition cannot hold a Transform, so the transform a fullTriadManipulator reports has to be
 * decomposed to be stored at all, and the flat form is the only rotation shape that round-trips —
 * the conclusion routingCurve.fs and freeFormDeformation.fs both reached. Keeping the identity in
 * the same shape means the default, the reset and the comparison all read the same data rather than
 * one of them quietly holding a 3x3.
 */
const IDENTITY_ROTATION = [1, 0, 0, 0, 1, 0, 0, 0, 1];

/**
 * A surface editing feature.
 */
annotation { "Feature Type Name" : "Edit surface",
        "Feature Type Description" : "Edits a face's B-spline control net directly.",
        "Editing Logic Function" : "editSurfaceEditLogic",
        "Manipulator Change Function" : "onEditSurfaceManipulatorChange" }
export const editSurface = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // The parameter id stays `face` although it now takes several, so studios built against
        // earlier versions keep resolving. Selecting more than one turns this into a merge.
        annotation { "Name" : "Faces", "Filter" : EntityType.FACE && ConstructionObject.NO && SketchObject.NO }
        definition.face is Query;

        annotation { "Name" : "Approximate" }
        definition.approximate is boolean;
        // Deliberately the SAME group, with the same controls, a single non-spline face already
        // needs. editCurve.fs takes exactly this line for its edge chain: there is no separate
        // "interpolate" or "merge" section anywhere in its UI — selecting several edges simply
        // requires Approximate to be on (EDIT_CURVE_MULTIPLE_EDGES otherwise) and reads degree,
        // maximum control points and tolerance from here. Merging faces therefore costs a user no
        // new buttons to learn, which is the whole point of following that lead.
        annotation { "Group Name" : "Approximation parameters", "Driving Parameter" : "approximate", "Collapsed By Default" : false }
        {
            if (definition.approximate)
            {
                annotation { "Name" : "U target degree", "Column Name" : "Approximation U degree" }
                isInteger(definition.approximationUDegree, SURFACE_DEGREE_BOUND);

                annotation { "Name" : "V target degree", "Column Name" : "Approximation V degree" }
                isInteger(definition.approximationVDegree, SURFACE_DEGREE_BOUND);

                annotation { "Name" : "Maximum U control points" }
                isInteger(definition.approximationMaxUCPs, { (unitless) : [4, 15, MAX_SURFACE_CONTROL_POINTS] } as IntegerBoundSpec);

                annotation { "Name" : "Maximum V control points" }
                isInteger(definition.approximationMaxVCPs, { (unitless) : [4, 15, MAX_SURFACE_CONTROL_POINTS] } as IntegerBoundSpec);

                annotation { "Name" : "Tolerance" }
                isLength(definition.approximationTolerance, TOLERANCE_BOUND);
            }
        }

        annotation { "Name" : "Elevate" }
        definition.elevate is boolean;
        annotation { "Group Name" : "Elevation parameters", "Driving Parameter" : "elevate", "Collapsed By Default" : false }
        {
            if (definition.elevate)
            {
                annotation { "Name" : "U target degree", "Column Name" : "Elevation U target degree" }
                isInteger(definition.uElevationDegree, SURFACE_DEGREE_BOUND);

                annotation { "Name" : "V target degree", "Column Name" : "Elevation V target degree" }
                isInteger(definition.vElevationDegree, SURFACE_DEGREE_BOUND);
            }
        }

        annotation { "Name" : "Refine" }
        definition.refine is boolean;
        annotation { "Group Name" : "Refinement parameters", "Driving Parameter" : "refine", "Collapsed By Default" : false }
        {
            if (definition.refine)
            {
                annotation { "Name" : "U control points", "Column Name" : "U control point target" }
                isInteger(definition.uControlPointCount, SURFACE_CONTROL_POINT_COUNT_BOUND);

                annotation { "Name" : "V control points", "Column Name" : "V control point target" }
                isInteger(definition.vControlPointCount, SURFACE_CONTROL_POINT_COUNT_BOUND);
            }
        }

        annotation { "Name" : "Edit control points" }
        definition.editControlPoints is boolean;
        annotation { "Group Name" : "Edit control points", "Driving Parameter" : "editControlPoints", "Collapsed By Default" : false }
        {
            if (definition.editControlPoints)
            {
                annotation { "Name" : "Edit mode" }
                definition.editPointMode is EditPointMode;

                if (definition.editPointMode == EditPointMode.XYZ)
                {
                    annotation { "Name" : "Control point indices", "Item name" : "index", "Item label template" : "Control point (#uIndexValue, #vIndexValue)", "Show labels only" : true, "UIHint" : [UIHint.INITIAL_FOCUS, UIHint.PREVENT_ARRAY_REORDER, UIHint.ALLOW_ARRAY_FOCUS] }
                    definition.selectedIndices is array;
                    for (var selectedIndex in definition.selectedIndices)
                    {
                        annotation { "Name" : "U index" }
                        isInteger(selectedIndex.uIndexValue, CONTROL_POINT_INDEX_BOUND);

                        annotation { "Name" : "V index" }
                        isInteger(selectedIndex.vIndexValue, CONTROL_POINT_INDEX_BOUND);
                    }

                    // A REAL button — `isButton` is satisfied by the value staying undefined, which
                    // is why `planarizeSelection` is absent from the defaults map below. Pressing it
                    // reaches editSurfaceEditLogic as its `clickedButton` argument. XYZ mode only,
                    // because it acts on a SELECTION and UVN mode has a single point.
                    annotation { "Name" : "Planarize selection",
                                 "Description" : "Move every selected control point onto the least-squares plane through them. Acts once, on press, and writes ordinary point overrides: there is no persistent planar constraint afterwards." }
                    isButton(definition.planarizeSelection);
                }
                else
                {
                    annotation { "Name" : "U index" }
                    isInteger(definition.selectedUIndex, CONTROL_POINT_INDEX_BOUND);

                    annotation { "Name" : "V index" }
                    isInteger(definition.selectedVIndex, CONTROL_POINT_INDEX_BOUND);
                }

                // The cumulative transform of the CURRENT XYZ selection, live until the selection
                // changes and then baked into the point overrides below. See bakeSelectionTransform
                // for why the two-stage storage exists rather than one or the other alone. Stored
                // decomposed because a Transform is not a precondition type: a flat nine-value
                // rotation behind isAnything plus three lengths, the routingCurve.fs pattern.
                //
                // Declared OUTSIDE the mode branch above so that a transform left live by XYZ mode
                // is still readable — and therefore still bakeable — after a switch to UVN mode.
                annotation { "Name" : "Selection rotation", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isAnything(definition.selectionRotation);

                annotation { "Name" : "Selection translation X", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isLength(definition.selectionTranslateX, ZERO_DEFAULT_LENGTH_BOUNDS);
                annotation { "Name" : "Selection translation Y", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isLength(definition.selectionTranslateY, ZERO_DEFAULT_LENGTH_BOUNDS);
                annotation { "Name" : "Selection translation Z", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isLength(definition.selectionTranslateZ, ZERO_DEFAULT_LENGTH_BOUNDS);

                annotation { "Name" : "Points overrides", "Item name" : "point override", "Item label template" : "(#uIndex, #vIndex): #x;#y;#z", "UIHint" : [UIHint.PREVENT_ARRAY_REORDER, UIHint.COLLAPSE_ARRAY_ITEMS] }
                definition.controlPointEdits is array;
                for (var controlPointEdit in definition.controlPointEdits)
                {
                    annotation { "Name" : "U index" }
                    isInteger(controlPointEdit.uIndex, CONTROL_POINT_INDEX_BOUND);

                    annotation { "Name" : "V index" }
                    isInteger(controlPointEdit.vIndex, CONTROL_POINT_INDEX_BOUND);

                    annotation { "Name" : "Reference", "Filter" : QueryFilterCompound.ALLOWS_VERTEX, "MaxNumberOfPicks" : 1 }
                    controlPointEdit.reference is Query;

                    annotation { "Name" : "X offset" }
                    isLength(controlPointEdit.x, ZERO_DEFAULT_LENGTH_BOUNDS);
                    annotation { "Name" : "Y offset" }
                    isLength(controlPointEdit.y, ZERO_DEFAULT_LENGTH_BOUNDS);
                    annotation { "Name" : "Z offset" }
                    isLength(controlPointEdit.z, ZERO_DEFAULT_LENGTH_BOUNDS);

                    annotation { "Name" : "Weight" }
                    isReal(controlPointEdit.weight, SCALE_BOUNDS);
                }
            }
        }

        annotation { "Name" : "Show details", "Default" : true }
        definition.showDetails is boolean;
        annotation { "Group Name" : "Show details", "Driving Parameter" : "showDetails", "Collapsed By Default" : false }
        {
            if (definition.showDetails)
            {
                annotation { "Name" : "U degree", "UIHint" : UIHint.READ_ONLY }
                isInteger(definition.surfaceUDegree, { (unitless) : [0, 0, MAX_DEGREE] } as IntegerBoundSpec);

                annotation { "Name" : "V degree", "UIHint" : UIHint.READ_ONLY }
                isInteger(definition.surfaceVDegree, { (unitless) : [0, 0, MAX_DEGREE] } as IntegerBoundSpec);

                annotation { "Name" : "U control points", "UIHint" : UIHint.READ_ONLY }
                isInteger(definition.surfaceNumUCPs, CONTROL_POINT_INDEX_BOUND);

                annotation { "Name" : "V control points", "UIHint" : UIHint.READ_ONLY }
                isInteger(definition.surfaceNumVCPs, CONTROL_POINT_INDEX_BOUND);
            }
        }

        annotation { "Name" : "Result", "Default" : EditSurfaceResult.REPLACE_FACE }
        definition.result is EditSurfaceResult;
    }
    {
        if (isQueryEmpty(context, definition.face))
        {
            throw regenError("Select a face to edit.", ["face"]);
        }

        const selectedFaces = evaluateQuery(context, definition.face);
        const isMerge = size(selectedFaces) > 1;

        // Following editCurve.fs exactly: several inputs is an approximation request, and refusing
        // it when Approximate is off is how that feature says so (EDIT_CURVE_MULTIPLE_EDGES). Same
        // rule, same group of controls — nothing new to learn for the multi-face case.
        if (isMerge && !definition.approximate)
        {
            throw regenError("Merging " ~ size(selectedFaces) ~ " faces into one patch smooths the creases between " ~
                    "them, which is an approximation — no single B-spline patch reproduces a crease exactly. Turn on " ~
                    "\"Approximate\" and set the target degrees and maximum control points there, the same way Edit " ~
                    "curve requires it for a chain of edges.",
                ["face", "approximate"]);
        }

        var extraction = undefined;
        var surface;
        var mergeResult = undefined;
        if (isMerge)
        {
            mergeResult = buildMergedSurface(context, definition, selectedFaces);
            surface = mergeResult.surface;
            // The merge reports its own deviations as one story; clearing this keeps the generic
            // single-face "control point count moved the surface" report from repeating half of it.
            surface.deviation = undefined;
        }
        else
        {
            extraction = readSurfaceAndTrim(context, definition);
            surface = normalizeSurfaceDefinition(extraction.surface);
        }

        // The trim loops live in the surface's PARAMETER space, so they stay valid through every
        // later step only for as long as the parameter DOMAIN does. Capture it now and compare
        // after — the experiment's whole point is to measure that rather than assume it.
        const domainBeforeEdits = surfaceDomains(surface);

        surface = prepareSurface(surface, definition);

        if (definition.editControlPoints)
        {
            // Three states of the net, and which is which matters:
            //   surfaceBeforeEdits — prepared, nothing applied. The frame the UVN manipulators and
            //                        the XYZ triad's axes are read off, so they stay put while a
            //                        drag changes the geometry underneath them.
            //   committedSurface   — the stored point overrides applied. This is what the live
            //                        selection transform is measured against, so it is the triad's
            //                        base and the state every bake reconstructs.
            //   surface            — committed plus the live transform. What gets emitted.
            const surfaceBeforeEdits = surface;
            const committedSurface = applyControlPointEdits(context, surface, definition.controlPointEdits);
            if (definition.editPointMode == EditPointMode.XYZ)
            {
                surface = applySelectionTransform(committedSurface, surfaceBeforeEdits, definition);
                showSelectionTransformManipulator(context, id, definition, committedSurface, surfaceBeforeEdits);
            }
            else
            {
                surface = committedSurface;
                showUVNTangentEditManipulators(context, id, surfaceBeforeEdits, surface, definition);
            }
        }
        showIndexManipulators(context, id, surface, definition);

        showControlNet(context, surface);
        updateSurfaceData(context, id, surface);

        // Simplification is the one step here that can MOVE the surface, so the price is always
        // stated rather than left for the user to discover.
        if (surface.deviation != undefined && surface.deviation > 0 * meter)
        {
            reportFeatureInfo(context, id, "Reducing the control point count moved the surface by up to " ~
                toString(surface.deviation) ~ ". Raise the targets to reduce that.");
        }
        // The other step that moves it, reported separately because the cause and the cure are
        // different — and because it changes the u/v parameterization, which no other step here does.
        if (surface.reparameterized == true)
        {
            reportFeatureInfo(context, id, "A closed (periodic) direction was re-fitted onto an even, arc-length " ~
                "knot vector, moving the surface by up to " ~ toString(surface.uniformizationDeviation) ~
                ". A kernel cylinder, cone or revolve arrives as rational Bezier arcs: the arc joints sit at " ~
                "multiplicity equal to the degree, which pulls control points into a tight cluster there, and the " ~
                "arcs run about 1.7 to 1 slower in parameter at those joints than mid-arc, which crowds the rest of " ~
                "the net and any u/v-driven feature into the same bands. Neither is fixable by adding or removing " ~
                "knots. This also re-maps u and v, by design.");
        }
        if (surface.uniformizationCapped == true)
        {
            reportFeatureWarning(context, id, "Evening out a closed (periodic) direction hit the control point " ~
                "ceiling before it reached the requested tolerance. The deviation reported is what was achieved, " ~
                "not what was asked for. Raise the tolerance.", ["approximationTolerance"]);
        }
        if (surface.periodicBudgetSkipped == true)
        {
            reportFeatureWarning(context, id, "A closed (periodic) direction is above the requested control point " ~
                "count and was left at its own count. Removing knots from a closed direction has to preserve the " ~
                "wrap, which is not built yet; refining a closed direction upward is unaffected and works.");
        }

        // ONE id generator for every operation this feature performs. Hand-naming operation ids is
        // a bug waiting to happen: a thrown op STILL REGISTERS ITS ID, so any retry that reuses a
        // name fails with "Duplicate id in context" and masks the original error — which is exactly
        // what happened here when the bridged-loop retry reused the first attempt's id. The
        // generator also removes the string coupling that made replaceTargetFace have to know what
        // emitSurface had called its body; ids are returned and passed now, never guessed.
        //
        // The DISAMBIGUATED variant, not the plain unstable one, because downstream references to
        // whatever this feature produces should survive a regeneration. Without external
        // disambiguation the created entities are identified by operation ORDER, and the order here
        // genuinely varies — a bridged loop that succeeds performs a different number of operations
        // than one that falls back — so order-based identity is exactly the wrong anchor.
        //
        // Everything this feature makes derives from the one face the user picked, so that face is
        // the disambiguator. Wrapping it in a zero-argument closure keeps every helper's signature
        // free of the query. (If the per-hole patches ever become a shipped path rather than a
        // diagnostic fallback, they deserve their own per-loop query instead of sharing this one.)
        const disambiguatedId = getDisambiguatedIncrementingId(context, id + "op");
        const idGenerator = function()
            {
                return disambiguatedId(definition.face);
            };

        if (isMerge)
        {
            const templateId = emitSurface(context, idGenerator, surface);

            // Certify BEFORE any replace: the original faces are the reference, and REPLACE_FACE
            // consumes them. Span-midpoint sampling — exactly where the control-net comparison is
            // blind — measured by the kernel against the true faces. This is the number that makes
            // the merge trustworthy, and it covers every stage at once: join gaps, seam smoothing,
            // and the control point budget.
            const certifiedDeviation = certifiedDeviationAgainstFaces(context, surface, definition.face);
            reportFeatureInfo(context, id, "Merged " ~ size(selectedFaces) ~ " faces across " ~ mergeResult.seamCount ~
                " seam(s): joined within " ~ toString(mergeResult.joinDeviation) ~ ", smoothing the seams moved the " ~
                "surface by up to " ~ toString(mergeResult.seamDeviation) ~ ", the control point budget by up to " ~
                toString(mergeResult.simplifyDeviation) ~ ". Certified worst deviation from the original faces: " ~
                toString(certifiedDeviation) ~ ".");
            if (certifiedDeviation > definition.approximationTolerance)
            {
                reportFeatureWarning(context, id, "The merged patch deviates from the original faces by " ~
                    toString(certifiedDeviation) ~ ", above the requested tolerance. Raise the maximum control " ~
                    "points, or accept the smoothing this implies.");
            }

            if (definition.result == EditSurfaceResult.REPLACE_FACE)
            {
                // All selected faces at once: opReplaceFace substitutes the surface beneath every
                // one of them and heals the shared boundaries against the UNSELECTED neighbours,
                // which do not move. If it refuses, the standard machinery warns and leaves the
                // merged sheet in place to inspect.
                replaceTargetFace(context, id, idGenerator, definition.face, templateId);
            }
            else if (definition.result == EditSurfaceResult.NEW_BODY_TRIMMED)
            {
                reportFeatureInfo(context, id, "A merged patch has no extracted trim loops yet, so the trimmed " ~
                    "result mode produced the untrimmed patch. Trimming the merge to the strip's outline is the " ~
                    "next piece.");
            }
        }
        else if (definition.result == EditSurfaceResult.NEW_BODY_TRIMMED)
        {
            emitTrimmedSurface(context, id, idGenerator, surface, extraction, domainBeforeEdits);
        }
        else
        {
            const templateId = emitSurface(context, idGenerator, surface);
            if (definition.result == EditSurfaceResult.REPLACE_FACE)
            {
                replaceTargetFace(context, id, idGenerator, definition.face, templateId);
            }
        }
    }, { "approximate" : false, "approximationUDegree" : 3, "approximationVDegree" : 3,
            "approximationMaxUCPs" : 15, "approximationMaxVCPs" : 15,
            "elevate" : false, "refine" : false, "editControlPoints" : false, "showDetails" : true,
            "editPointMode" : EditPointMode.XYZ, "selectedIndices" : [], "controlPointEdits" : [],
            "selectedUIndex" : 0, "selectedVIndex" : 0,
            "selectionRotation" : IDENTITY_ROTATION,
            "selectionTranslateX" : 0 * meter, "selectionTranslateY" : 0 * meter, "selectionTranslateZ" : 0 * meter,
            "result" : EditSurfaceResult.REPLACE_FACE });
// `planarizeSelection` is deliberately absent from the defaults above: isButton is satisfied by the
// value being undefined, so giving it a default turns the button into an ordinary parameter.
//
// The three selection-transform defaults ARE present, and they are what makes this change safe for
// features built against an earlier version: a stored definition with no live transform in it comes
// back as the identity, so nothing regenerates differently.

//==================================================================
//======================== Input Processing ========================
//==================================================================

/**
 * Read the face's B-spline definition, EXACTLY when the kernel already holds one.
 *
 * evSurfaceDefinition returns the true definition for a SPLINE face — no approximation, no
 * tolerance, nothing to justify. Every other surface type has no B-spline definition to return, so
 * representing it as one is an approximation, and this feature makes the user opt into that
 * explicitly rather than doing it silently. Same standing rule the refinement module is built on,
 * applied at the feature's front door.
 *
 * EXPECT NON-SPLINE MORE OFTEN THAN SEEMS REASONABLE. surfaceType reports how the kernel STORES the
 * surface, not what shape it is, so an extruded or revolved spline profile — which is exactly a
 * B-spline surface mathematically — comes back EXTRUDED or REVOLVED and lands here. Those cases
 * approximate essentially perfectly, since the target genuinely is a B-spline and the approximator
 * is only being asked to rediscover it. The error therefore names the type it actually got: "this
 * is an EXTRUDED face" is something a user can reason about, where "not a spline" just reads as a
 * refusal.
 */
function readSurfaceDefinition(context is Context, definition is map) returns map
{
    return readFaceSurface(context, definition, definition.face);
}

/** The same read for an EXPLICIT face — the merge reads each chain face through this, so every
    piece gets the exact-when-spline / approximate-behind-the-toggle treatment identically. */
function readFaceSurface(context is Context, definition is map, face is Query) returns map
{
    const surfaceDefinition = evSurfaceDefinition(context, { "face" : face });
    if (surfaceDefinition.surfaceType == SurfaceType.SPLINE)
    {
        return surfaceDefinition;
    }

    if (!definition.approximate)
    {
        throw regenError("This face is stored as " ~ toString(surfaceDefinition.surfaceType) ~
                ", not as a B-spline, so it has no exact control net to edit. Turn on " ~
                "\"Approximate\" to work on a B-spline approximation instead. Extruded and revolved " ~
                "faces built from spline profiles are B-splines in all but storage and approximate " ~
                "to within the tolerance below.",
            ["face", "approximate"]);
    }

    return evApproximateBSplineSurface(context, {
                    "face" : face,
                    "tolerance" : kernelApproximationTolerance(definition)
                }).bSplineSurface;
}

/**
 * Read the surface together with its trim loops, as a MATCHED PAIR.
 *
 * The pairing is the point, and it is why the trimmed path always goes through
 * evApproximateBSplineSurface even for a face that is already a spline. The loops it returns are 2D
 * curves in the parameter space of the surface IT returns. Pairing them with a different surface —
 * say the exact one from evSurfaceDefinition — is only sound if the two share a parameterization,
 * which nothing guarantees. Taking both from one call costs an approximation on the exact path and
 * buys a guarantee; the diagnostics report the tolerance so the cost is visible.
 *
 * Returns { surface, outerLoop, innerLoops }. outerLoop is an array of 2D BSplineCurves forming one
 * closed loop, possibly empty when the face is the whole surface. innerLoops is an array OF such
 * loops, one per hole — note the nesting, and note that each entry is individually a legal
 * `boundaryBSplineCurves` argument, which is what makes the per-hole patch strategy possible.
 */
function readSurfaceAndTrim(context is Context, definition is map) returns map
{
    if (definition.result != EditSurfaceResult.NEW_BODY_TRIMMED)
    {
        return { "surface" : readSurfaceDefinition(context, definition), "outerLoop" : [], "innerLoops" : [] };
    }

    const tolerance = kernelApproximationTolerance(definition);
    const approximation = evApproximateBSplineSurface(context, {
                "face" : definition.face,
                "tolerance" : tolerance
            });
    return {
            "tolerance" : tolerance,
            "surface" : approximation.bSplineSurface,
            "outerLoop" : approximation.boundaryBSplineCurves == undefined ? [] : approximation.boundaryBSplineCurves,
            "innerLoops" : approximation.innerLoopBSplineCurves == undefined ? [] : approximation.innerLoopBSplineCurves
        };
}

/**
 * Reduce toward the requested STORED control point counts, skipping any PERIODIC direction, and
 * always reporting the deviation the reduction cost (zero when nothing was reduced).
 *
 * simplifySurfaceToControlPointCounts THROWS on a periodic direction rather than pretending to
 * handle one: knot removal there has to preserve the wrap overlap P[i] == P[i+n], which needs the
 * wide-window construction periodic refinement uses, and that is not built. A feature cannot let
 * that reach the user as a raw module string — a cylinder asked for fewer control points than it
 * has is an ordinary thing to ask for, not a programming error — and it must not silently pretend
 * the budget was applied either. So the skip is recorded on the returned surface and the feature
 * body says so out loud. Refining a periodic direction UPWARD is unaffected and works.
 *
 * A count of 0 means "no request", matching the module's own convention.
 */
function reduceToControlPointCounts(surface is map, targetUCount is number, targetVCount is number) returns map
{
    const uPeriodic = surface.isUPeriodic == true;
    const vPeriodic = surface.isVPeriodic == true;
    const uExceeds = targetUCount > 0 && size(surface.controlPoints) > targetUCount;
    const vExceeds = targetVCount > 0 && size(surface.controlPoints[0]) > targetVCount;

    var result = surface;
    if ((uExceeds && !uPeriodic) || (vExceeds && !vPeriodic))
    {
        result = simplifySurfaceToControlPointCounts(surface, uPeriodic ? 0 : targetUCount, vPeriodic ? 0 : targetVCount);
    }
    else
    {
        result.deviation = 0 * meter;
    }
    result.periodicBudgetSkipped = (uExceeds && uPeriodic) || (vExceeds && vPeriodic);
    return result;
}

/**
 * Trim the read to its budget, then elevate and refine to the requested targets — the middle two
 * are one call to the module's prepareSurfaceForDeformation, which is exactly that composition and
 * skips whichever step is already satisfied.
 *
 * DEGREE AND CONTROL POINT COUNT ARE INDEPENDENT LEVERS and both are needed, which is why they are
 * separate groups rather than one "detail" slider. Degree sets the CONTINUITY ceiling: no amount of
 * refinement makes a degree-1 surface smoother than C0, so a flat sheet read at degree 1 stays
 * faceted however many control points it gets. Count sets the DETAIL: elevation alone on a flat
 * sheet resolves to a handful of control points, which is not enough to sculpt with. The order
 * matters too — elevating after refining multiplies the control point count for nothing, since
 * elevation adds points in proportion to the segment count it is handed.
 *
 * WHICH KNOB SETS THE COUNT, since two of them look like they might, and the answer is scoped by
 * WHO CHOOSES THE COUNT at each stage.
 *
 * For a SINGLE face nobody here chooses it. The kernel does: evApproximateBSplineSurface takes a
 * tolerance and picks its own control point count, so TOLERANCE is the lever that makes that read
 * richer, and the **Refine** group is the lever that adds editing handles on top — exactly, by knot
 * insertion. A cylindrical fillet that reads as six control points stays at six however high the
 * approximation maximum goes, because six is all the kernel needed for the tolerance it was given;
 * Refine is what takes it to a hundred.
 *
 * For a MERGE the feature does choose, so that is where "Maximum U/V control points" lives and is
 * spent — see buildMergedSurface, which now refines UP to it as well as trimming down to it, the
 * way editCurve's fitter spends its own budget.
 *
 * AN EARLIER VERSION APPLIED THAT MAXIMUM TO EVERY READ and it was wrong in a way worth recording:
 * with Approximate on (which a cylinder or a fillet REQUIRES, being no kind of spline) and the field
 * at its default of 15, every face read with more than fifteen control points was silently crushed
 * to fifteen. Details showed the wrong counts, the drawn net stopped matching the surface, and any
 * stored control point index past fourteen fell out of range — one cause, three broken-looking
 * symptoms. A control the user has not touched must not destroy their net.
 *
 * Refinement is EXACT — knot insertion only ever ADDS handles and never moves the surface. Reducing
 * is knot removal, which is lossy and returns the deviation it cost.
 *
 * The count controls are quoted in EDITABLE control points — the fundamental count, matching the
 * handles on screen and the "Show details" readout — while the module counts the stored array. For
 * a periodic direction those differ by the degree, so the padding is added back here. Note it uses
 * the TARGET degree, not the current one: elevation runs first and changes how much padding the
 * stored form carries.
 */
function prepareSurface(surface is map, definition is map) returns map
{
    // EVEN OUT ANY CLOSED DIRECTION FIRST, before a degree or a count is chosen off it. This is the
    // step that makes a cylinder, a cone or a revolve editable rather than merely readable, and it
    // replaces a claim this comment used to make in the opposite direction.
    //
    // The old claim: a kernel cylinder carries multiplicity == degree at its Bezier arc joints;
    // those knots are not redundant, because the CIRCLE is smooth there but its homogeneous curve —
    // the thing the knots actually describe — corners there; so they survive removeRedundantSurfaceKnots,
    // drag their neighbouring Greville abscissae in against them, and leave a tight cluster of
    // handles at each arc joint "that no placement can undo". Every word of that is true and the
    // conclusion drawn from it was wrong. It IS undoable — just not by insertion or removal, which
    // is all that had been tried. It takes a refit, and the second half of the same defect makes the
    // case unanswerable: a revolve's arcs are also parameterized about 1.7 : 1 slower at the joins
    // than mid-arc, so even a net with no multiple knots left comes out crowded in the same two
    // bands, and so does anything downstream reading the result's u/v.
    //
    // So: uniformize, which lands the knots evenly in arc length, drops every multiplicity to 1 and
    // makes u/v proportional to arc length. It is lossy where the old behaviour was exact, and the
    // deviation is reported rather than absorbed. Already-even directions are detected and skipped,
    // so this costs one profile scan on a surface that does not need it and nothing at all on an
    // open one. See the module's periodic uniformization section.
    //
    // GATED ON "Approximate", and that gate is the file's no-silent-approximation rule rather than a
    // convenience. A refit moves the surface, so it may only run where the user has said a moved
    // surface is acceptable and has named the number. The gate costs nothing in practice: a
    // cylinder, a cone and a revolve are not B-spline faces, so evSurfaceDefinition refuses them and
    // they can only be read through Approximate in the first place — every surface this fixes is
    // already on the approximate path. With it off the read stays exact, bunched net and all.
    const uniformization = definition.approximate ?
        uniformizePeriodicSurfaceDirections(surface, definition.approximationTolerance) : surface;
    const evened = uniformization.uniformized == true ? uniformization : surface;

    const targetUDegree = definition.elevate ? max(evened.uDegree, definition.uElevationDegree) : evened.uDegree;
    const targetVDegree = definition.elevate ? max(evened.vDegree, definition.vElevationDegree) : evened.vDegree;

    var prepared;
    if (!definition.refine)
    {
        prepared = prepareSurfaceForDeformation(evened, targetUDegree, targetVDegree, 0, 0);
    }
    else
    {
        // Clean the representation BEFORE adding to it, so refinement builds on the smallest exact
        // net rather than on whatever redundancy an earlier elevation or edit left behind. Tolerance
        // is deliberately tight: this step is meant to cost nothing.
        const cleaned = removeRedundantSurfaceKnots(evened, TOLERANCE.zeroLength * meter);

        const targetUCount = definition.uControlPointCount + (evened.isUPeriodic == true ? targetUDegree : 0);
        const targetVCount = definition.vControlPointCount + (evened.isVPeriodic == true ? targetVDegree : 0);

        // The target goes DOWN as well as up, which needs two different operations. Above the current
        // count it is knot INSERTION — exact, geometry untouched. Below it, knot REMOVAL, which is
        // lossy and reports what it cost. Elevation runs first either way, since it sets the degree
        // both work under.
        prepared = reduceToControlPointCounts(
            prepareSurfaceForDeformation(cleaned, targetUDegree, targetVDegree, targetUCount, targetVCount),
            targetUCount, targetVCount);
    }

    // Re-attached rather than left to survive the intervening calls: every step between here and the
    // read returns a map derived from its input, so these WOULD carry through, and depending on that
    // is exactly the kind of coupling that breaks the next time one of them rebuilds from scratch.
    prepared.uniformizationDeviation = uniformization.uniformized == true ? uniformization.deviation : 0 * meter;
    prepared.reparameterized = uniformization.uniformized == true;
    prepared.uniformizationCapped = uniformization.uniformizationCapped == true;
    return prepared;
}

//==================================================================
//=============== Merging several faces into one patch =============
//==================================================================
// The merge is ASSEMBLY plus MEASURED KNOT REMOVAL, on the faces' own B-spline definitions —
// nothing is sampled and nothing is projected (the projection-and-raycast predecessor died of
// its own precondition; see EDIT_SURFACE_SPEC section 5A.4). Feature-side work is topology and
// orientation only: order the faces into a strip along their shared edges, turn each piece so
// the chain runs +u with transverse directions aligned, and hand the ordered pieces to the
// module. concatenateBSplineSurfaces joins them exactly with each seam at multiplicity ==
// degree; removeSurfaceKnotLine then smooths each seam, returning the deviation that smoothing
// cost — which is the honest price of "merge these faces", measured rather than estimated.

/**
 * Which end of the chain to walk from, decided by GEOMETRY rather than by selection order.
 *
 * Once a start is picked the rest of the walk is forced — a strip is a path — so the only freedom in
 * the whole ordering is which of the two ends goes first. Reading that off `evaluateQuery` order,
 * which is what the first version did, made the merged patch depend on the order the user happened
 * to click the faces in. It should not, and the difference is not cosmetic: reversing the chain
 * reverses u, and neither the least-error greedy in the budget's knot removal nor the seam-healing
 * order is symmetric, so the two click orders produce genuinely different patches from identical
 * input.
 *
 * The tiebreak is the lexicographically smaller bounding-box minimum corner. Arbitrary, but TOTAL
 * and derived from the geometry, which is the entire requirement: the same faces give the same
 * patch however they were picked. (An exact tie on all three axes falls back to selection order,
 * which is unreachable for two distinct faces at the ends of a strip.)
 */
function canonicalChainStart(context is Context, faces is array, chainEnds is array) returns number
{
    const firstCorner = evBox3d(context, { "topology" : faces[chainEnds[0]] }).minCorner;
    const secondCorner = evBox3d(context, { "topology" : faces[chainEnds[1]] }).minCorner;
    for (var axis = 0; axis < 3; axis += 1)
    {
        if (abs(firstCorner[axis] - secondCorner[axis]) > TOLERANCE.zeroLength * meter)
        {
            return firstCorner[axis] < secondCorner[axis] ? chainEnds[0] : chainEnds[1];
        }
    }
    return chainEnds[0];
}

/**
 * Order the selected faces into a chain by their shared edges. Returns { orderedFaces, seamEdges },
 * seamEdges[i] joining orderedFaces[i] to orderedFaces[i + 1]. The result depends only on WHICH
 * faces were selected, never on the order they were selected in — see canonicalChainStart.
 *
 * Everything that is not a simple open chain throws BY NAME: branching (a face with three merge
 * neighbours), disconnection, closed rings (need the periodic concatenation path), and pairs
 * sharing more than one edge (the seam would be ambiguous).
 */
function orderFacesIntoChain(context is Context, faces is array) returns map
{
    const faceCount = size(faces);
    var neighborLists = makeArray(faceCount, []);
    var seamEdgeByPair = {};
    for (var i = 0; i < faceCount; i += 1)
    {
        for (var j = i + 1; j < faceCount; j += 1)
        {
            const shared = evaluateQuery(context, qIntersection([
                            qAdjacent(faces[i], AdjacencyType.EDGE, EntityType.EDGE),
                            qAdjacent(faces[j], AdjacencyType.EDGE, EntityType.EDGE)]));
            if (size(shared) == 0)
            {
                continue;
            }
            if (size(shared) > 1)
            {
                throw regenError("Two of the selected faces share " ~ size(shared) ~ " edges, so the seam between " ~
                        "them is ambiguous. The merge currently needs each adjacent pair to meet along exactly one " ~
                        "edge.", ["face"]);
            }
            neighborLists[i] = append(neighborLists[i], j);
            neighborLists[j] = append(neighborLists[j], i);
            seamEdgeByPair[cellKey(i, j)] = shared[0];
        }
    }

    var chainEnds = [];
    for (var i = 0; i < faceCount; i += 1)
    {
        if (size(neighborLists[i]) == 0)
        {
            throw regenError("The selected faces are not all connected - at least one face shares no edge with any " ~
                    "other. Select a connected strip of faces.", ["face"]);
        }
        if (size(neighborLists[i]) > 2)
        {
            throw regenError("One of the selected faces meets " ~ size(neighborLists[i]) ~ " other selected faces. " ~
                    "The merge currently handles a STRIP - a chain of faces each meeting at most two others. Grids " ~
                    "and junctions are a later phase.", ["face"]);
        }
        if (size(neighborLists[i]) == 1)
        {
            chainEnds = append(chainEnds, i);
        }
    }
    if (size(chainEnds) != 2)
    {
        throw regenError("The selected faces form a closed ring, which needs the periodic concatenation path - not " ~
                "built yet. Leave one face out to open the ring.", ["face"]);
    }

    var order = makeArray(faceCount, -1);
    order[0] = canonicalChainStart(context, faces, chainEnds);
    var previous = -1;
    for (var step = 1; step < faceCount; step += 1)
    {
        const current = order[step - 1];
        var next = neighborLists[current][0];
        if (next == previous)
        {
            next = neighborLists[current][size(neighborLists[current]) - 1];
        }
        order[step] = next;
        previous = current;
    }
    // A disconnected selection where every face HAS a neighbour (two separate strips) walks one
    // strip and leaves the other untouched — catch it by the repeat.
    var visited = {};
    for (var index in order)
    {
        if (visited[index] == true)
        {
            throw regenError("The selected faces form more than one separate strip. Merge one strip at a time.", ["face"]);
        }
        visited[index] = true;
    }

    var orderedFaces = makeArray(faceCount, 0);
    var seamEdges = makeArray(faceCount - 1, 0);
    for (var step = 0; step < faceCount; step += 1)
    {
        orderedFaces[step] = faces[order[step]];
        if (step > 0)
        {
            seamEdges[step - 1] = seamEdgeByPair[cellKey(min(order[step - 1], order[step]), max(order[step - 1], order[step]))];
        }
    }
    return { "orderedFaces" : orderedFaces, "seamEdges" : seamEdges };
}

/**
 * Read one chain face as the sub-surface its TRIM actually occupies, not its whole underlying
 * surface.
 *
 * This is the fix for the most common real failure: a trimmed planar face sits on a plane whose
 * parameter domain extends far past it, so the shared edge bounds the FACE while landing deep in
 * the SURFACE's interior — corner matching then reports the seam tens of millimetres from any
 * boundary and the merge refuses geometry that is perfectly mergeable. The discontinuity it was
 * objecting to lies outside where the faces are drawn, and outside where the patch is redrawn.
 *
 * `evApproximateBSplineSurface` returns the surface AND its trim loops as a matched pair, in one
 * parameter space. The loops' UV bounding box is the face's own parameter rectangle, and
 * extractSubSurface cuts to it EXACTLY. What comes back is four-sided with the face's real edges
 * on its boundaries, which is precisely what the strip merge needs.
 *
 * A face whose trim is not a UV-aligned rectangle (a circular face on a plane) gets its bounding
 * box, which is larger than the face — the seam then still will not reach a boundary and the merge
 * says so. That is the genuinely non-four-sided case, correctly refused rather than fudged.
 */
function readChainPieceSurface(context is Context, definition is map, face is Query) returns map
{
    const approximation = evApproximateBSplineSurface(context, {
                "face" : face,
                "tolerance" : kernelApproximationTolerance(definition)
            });
    const surface = normalizeSurfaceDefinition(approximation.bSplineSurface);

    const outerLoop = approximation.boundaryBSplineCurves;
    if (outerLoop == undefined || size(outerLoop) == 0)
    {
        return surface; // untrimmed: the face IS the surface
    }

    const extent = loopUVExtent(outerLoop);
    const uDomain = knotDomain(surface.uKnots, surface.uDegree);
    const vDomain = knotDomain(surface.vKnots, surface.vDegree);
    const uStart = max(extent.uMin, uDomain.start);
    const uEnd = min(extent.uMax, uDomain.end);
    const vStart = max(extent.vMin, vDomain.start);
    const vEnd = min(extent.vMax, vDomain.end);

    // A trim that already fills the domain needs no cut; extractSubSurface skips whole-domain
    // requests anyway, but checking here keeps a degenerate box from ever reaching it.
    if (uEnd - uStart <= KNOT_PARAMETER_TOLERANCE || vEnd - vStart <= KNOT_PARAMETER_TOLERANCE)
    {
        return surface;
    }
    return extractSubSurface(surface, uStart, uEnd, vStart, vEnd);
}

/** The two 3D endpoints of a seam edge. A closed seam edge has no vertices and no endpoints —
    that is the closed-transverse case the concatenation names as unbuilt, so say so here. */
function seamEndpoints(context is Context, seamEdge is Query) returns array
{
    const vertices = evaluateQuery(context, qAdjacent(seamEdge, AdjacencyType.VERTEX, EntityType.VERTEX));
    if (size(vertices) != 2)
    {
        throw regenError("A seam between two of the selected faces is a closed curve (it has " ~ size(vertices) ~
                " endpoints instead of 2). Closed-section strips need the periodic concatenation path, which is not " ~
                "built yet.", ["face"]);
    }
    return [evVertexPoint(context, { "vertex" : vertices[0] }), evVertexPoint(context, { "vertex" : vertices[1] })];
}

/** The four corners of a piece's parameter domain, evaluated from its own definition — the
    module's evaluator, no kernel. */
function surfaceCorners(piece is map) returns map
{
    const uDomain = knotDomain(piece.uKnots, piece.uDegree);
    const vDomain = knotDomain(piece.vKnots, piece.vDegree);
    return {
            "uStartVStart" : evaluateBSplineSurfacePoint(piece, uDomain.start, vDomain.start),
            "uStartVEnd" : evaluateBSplineSurfacePoint(piece, uDomain.start, vDomain.end),
            "uEndVStart" : evaluateBSplineSurfacePoint(piece, uDomain.end, vDomain.start),
            "uEndVEnd" : evaluateBSplineSurfacePoint(piece, uDomain.end, vDomain.end)
        };
}

/**
 * Which parameter boundary of `piece` carries the seam whose 3D endpoints are `endpointPair`?
 * Decided by corner matching: the seam boundary's two surface corners must coincide with the
 * edge's two endpoints (in either order). This is where the strip merge's four-sidedness
 * requirement is ENFORCED rather than assumed — a face whose shared edge does not bound its
 * surface fails here by name, not later as a mystery gap.
 */
function identifySeamBoundary(piece is map, endpointPair is array, tolerance is ValueWithUnits) returns map
{
    const corners = surfaceCorners(piece);
    const candidates = [
            { "boundary" : "U_START", "cornerA" : corners.uStartVStart, "cornerB" : corners.uStartVEnd },
            { "boundary" : "U_END", "cornerA" : corners.uEndVStart, "cornerB" : corners.uEndVEnd },
            { "boundary" : "V_START", "cornerA" : corners.uStartVStart, "cornerB" : corners.uEndVStart },
            { "boundary" : "V_END", "cornerA" : corners.uStartVEnd, "cornerB" : corners.uEndVEnd }
        ];
    var best = undefined;
    for (var candidate in candidates)
    {
        const forward = max(norm(candidate.cornerA - endpointPair[0]), norm(candidate.cornerB - endpointPair[1]));
        const crossed = max(norm(candidate.cornerA - endpointPair[1]), norm(candidate.cornerB - endpointPair[0]));
        const score = min(forward, crossed);
        if (best == undefined || score < best.score)
        {
            best = { "boundary" : candidate.boundary, "score" : score };
        }
    }
    if (best.score > tolerance)
    {
        throw regenError("A shared edge between the selected faces does not reach a parameter boundary of one " ~
                "face's surface (nearest boundary corners are " ~ toString(best.score) ~ " away). The strip merge " ~
                "needs four-sided pieces whose shared edges bound their surfaces - a trimmed face whose seam runs " ~
                "through its interior needs sub-surface extraction that is not wired in yet.", ["face"]);
    }
    return best;
}

/**
 * Read every chain face and turn each piece so the chain runs +u — piece k's u-end row meeting
 * piece k+1's u-start row — with all transverse (v) directions aligned. Pure reparameterization:
 * transposeSurface and reverseSurfaceDirection are exact, so orientation costs nothing.
 */
function orientedChainPieces(context is Context, definition is map, chain is map) returns array
{
    const pieceCount = size(chain.orderedFaces);
    const identificationTolerance = max(20 * definition.approximationTolerance, 1e-6 * meter);

    var seamPointPairs = makeArray(pieceCount - 1, 0);
    for (var index = 0; index < pieceCount - 1; index += 1)
    {
        seamPointPairs[index] = seamEndpoints(context, chain.seamEdges[index]);
    }

    var pieces = makeArray(pieceCount, 0);
    for (var k = 0; k < pieceCount; k += 1)
    {
        pieces[k] = readChainPieceSurface(context, definition, chain.orderedFaces[k]);

        // Piece 0 is oriented by its NEXT seam (which must land at u-end); every other piece by
        // its PREVIOUS seam (which must land at u-start).
        const anchorPair = k == 0 ? seamPointPairs[0] : seamPointPairs[k - 1];
        var identified = identifySeamBoundary(pieces[k], anchorPair, identificationTolerance);
        if (identified.boundary == "V_START" || identified.boundary == "V_END")
        {
            pieces[k] = transposeSurface(pieces[k]);
            identified.boundary = identified.boundary == "V_START" ? "U_START" : "U_END";
        }
        const wantsUEnd = k == 0;
        if (wantsUEnd ? identified.boundary == "U_START" : identified.boundary == "U_END")
        {
            pieces[k] = reverseSurfaceDirection(pieces[k], true);
        }

        // Transverse alignment: piece k's seam row must run the same way as piece k-1's. Piece 0
        // sets the chain's transverse sense; everyone after follows it.
        if (k > 0)
        {
            const previousCorners = surfaceCorners(pieces[k - 1]);
            const currentCorners = surfaceCorners(pieces[k]);
            if (norm(previousCorners.uEndVStart - currentCorners.uStartVStart) >
                norm(previousCorners.uEndVStart - currentCorners.uStartVEnd))
            {
                pieces[k] = reverseSurfaceDirection(pieces[k], false);
            }
        }

        // An interior piece must meet its NEXT neighbour on the boundary OPPOSITE its previous one.
        // A piece whose two seams sit on adjacent boundaries corners the chain in its own parameter
        // space, which a single tensor patch cannot represent.
        if (k > 0 && k < pieceCount - 1)
        {
            const nextIdentified = identifySeamBoundary(pieces[k], seamPointPairs[k], identificationTolerance);
            if (nextIdentified.boundary != "U_END")
            {
                throw regenError("Face " ~ (k + 1) ~ " of the chain meets its two neighbours on non-opposite sides " ~
                        "of its own parameterization, so the strip turns a corner in parameter space. A single " ~
                        "patch cannot represent that; merge the straight runs separately.", ["face"]);
            }
        }
    }
    return pieces;
}

/**
 * The merge: order, orient, concatenate, elevate to the requested degrees, smooth every seam,
 * enforce the control point budget. No reporting here — the manipulator handler calls this too,
 * so a merged patch can be EDITED like any other surface, and the handler has no feature to
 * report against. The feature body reports.
 *
 * Degree first, then smoothing, because they answer different questions (the standing doctrine):
 * the target degree sets the continuity CEILING at the seams — healing a seam to multiplicity 1
 * on a degree-p surface yields C^(p-1) there, so degree-1 walls merged at degree 1 keep their
 * crease no matter what, while the same walls elevated to degree 3 heal to curvature continuity.
 * Elevation is exact; the smoothing deviation and the budget deviation are measured and returned.
 */
function buildMergedSurface(context is Context, definition is map, faces is array) returns map
{
    const chain = orderFacesIntoChain(context, faces);
    var pieces = orientedChainPieces(context, definition, chain);

    // ARC-LENGTH DOMAIN SIZING, before assembly. Concatenation translates each piece's domain to
    // abut its neighbour but never rescales it, so pieces arrive carrying whatever parameter
    // lengths their faces happened to have — a metre-long wall and a two-millimetre fillet can
    // easily have comparable parameter ranges. Every later parameter-driven decision then
    // inherits that distortion, which is what put the fillet's control points where they had no
    // business being. Giving each piece a chain-domain length proportional to its physical extent
    // is an affine reparameterization: exact, no geometry moves.
    var chainStart = 0;
    for (var index = 0; index < size(pieces); index += 1)
    {
        const pieceLength = approximateDirectionArcLength(pieces[index], true) / meter;
        const chainEnd = chainStart + max(pieceLength, 1e-9);
        pieces[index] = rescaleSurfaceDirectionDomain(pieces[index], true, chainStart, chainEnd);
        chainStart = chainEnd;
    }

    const joinTolerance = max(4 * definition.approximationTolerance, 1e-7 * meter);
    var merged = concatenateBSplineSurfaces(pieces, true, joinTolerance);
    const seamParameters = merged.seamParameters;
    const joinDeviation = merged.joinDeviation;

    merged = elevateSurfaceDegrees(merged, max(merged.uDegree, definition.approximationUDegree),
            max(merged.vDegree, definition.approximationVDegree));

    // SPEND THE BUDGET BEFORE HEALING, NOT AFTER. This ordering is the whole difference between a
    // patch that hugs the original faces and one that does not, and getting it backwards produced
    // the reported result exactly: two faces of a cube merged at four or five control points give a
    // reasonable symmetric blend, and at six and up the patch hooks away from the cube instead of
    // approaching it, which is the opposite of what raising a control point budget should do.
    //
    // WHY. Knot removal is LOCAL: removing the knot at the seam recomputes the `degree` control
    // points on each side of it and nothing else. What varies is how much SURFACE those control
    // points govern. Heal first, on the bare elevated net — for two flat faces that is seven control
    // points over two spans — and those few points span the entire patch, so smoothing a ninety
    // degree crease drags the whole thing out of shape. Refining afterwards can never undo that:
    // insertion is exact, so it adds handles to a surface whose damage is already baked in. Refine
    // FIRST and the same removal touches control points confined to a narrow band around the corner,
    // so the wings stay on the original planes and only the corner rounds — and a bigger budget now
    // means a TIGHTER corner, which is the behaviour a user expects from the number they typed.
    //
    // Refining up to the budget also fixes the strip patch whose handles piled onto one region and
    // left the rest bare, and it costs nothing: insertion never moves the surface, and the added
    // knots land at even arc length (allocate-then-subdivide in arcLengthSpanInsertions), which is
    // precisely the long bare wall spans getting their share. It is what makes this field behave the
    // way editCurve's "Maximum control points" does, where a real fitter spends its budget rather
    // than only trimming against it.
    //
    // BALANCED, because this refinement feeds a LOSSY step. Knot removal reads the spacing on both
    // sides of the knot it removes, so a net carrying one extra knot on one side of the seam heals
    // lopsidedly — and unlike an asymmetric handle, a lopsided heal is in the GEOMETRY and shows up
    // as a surface artifact. The balanced overload refuses to serve half a group of equally-long
    // spans, so mirror-image spans are always refined together; it can come in under the target, and
    // the exact refinement after the heal makes that up where parity cannot hurt anything.
    //
    // The target is raised by what the healing is about to remove, so the patch LANDS on the budget
    // rather than finishing short of it: each seam gives up degree - 1 knots to reach multiplicity 1.
    const healingRemovals = size(seamParameters) * (merged.uDegree - 1);
    merged = refineSurfaceToControlPointCounts(merged, definition.approximationMaxUCPs + healingRemovals,
            definition.approximationMaxVCPs, true);

    // Heal each seam down to multiplicity 1: C^(degree-1) there, the smoothest a simple knot
    // allows. Elevation preserves continuity, so a C0 seam sits at multiplicity == degree
    // whatever degree the surface now carries.
    var seamDeviation = 0 * meter;
    for (var seamParameter in seamParameters)
    {
        merged = removeSurfaceKnotLine(merged, true, seamParameter, merged.uDegree - 1);
        seamDeviation = max(seamDeviation, merged.deviation);
    }

    // Land exactly on the budget. The refine covers a merge that arrived below it (nothing to heal,
    // or a transverse direction still short); the reduce covers one that arrived above it, and also
    // sweeps up the exactly-removable knots elevation left behind — surface elevation is deliberately
    // unminimized, see elevateSurfaceDegrees, and free removals always win the least-error greedy
    // selection. Through the shared reducer, so a chain with a periodic transverse direction reports
    // the skip instead of throwing the module's raw string.
    merged = refineSurfaceToControlPointCounts(merged, definition.approximationMaxUCPs, definition.approximationMaxVCPs);
    merged = reduceToControlPointCounts(merged, definition.approximationMaxUCPs, definition.approximationMaxVCPs);
    const simplifyDeviation = merged.deviation;

    return {
            "surface" : merged,
            "seamCount" : size(seamParameters),
            "joinDeviation" : joinDeviation,
            "seamDeviation" : seamDeviation,
            "simplifyDeviation" : simplifyDeviation
        };
}

/** Distinct-span midpoints of one direction, capped — the certification sampling sites, chosen
    because they are exactly where a control-net comparison is blind (spec section 9.1.1). */
function spanMidpointParameters(knots is array, degree is number, maximumCount is number) returns array
{
    var distinctValues = [];
    for (var index = degree; index < size(knots) - degree; index += 1)
    {
        if (size(distinctValues) == 0 || knots[index] - distinctValues[size(distinctValues) - 1] > 1e-10)
        {
            distinctValues = append(distinctValues, knots[index]);
        }
    }
    var midpoints = makeArray(size(distinctValues) - 1, 0);
    for (var index = 0; index < size(midpoints); index += 1)
    {
        midpoints[index] = (distinctValues[index] + distinctValues[index + 1]) / 2;
    }
    if (size(midpoints) <= maximumCount)
    {
        return midpoints;
    }
    var sampled = makeArray(maximumCount, 0);
    for (var index = 0; index < maximumCount; index += 1)
    {
        sampled[index] = midpoints[floor(index * (size(midpoints) - 1) / (maximumCount - 1))];
    }
    return sampled;
}

/** The kernel-measured worst distance from the surface (sampled at span midpoints) to the
    original faces — one evPointsDeviation call, closest-point projection, the honest number. */
function certifiedDeviationAgainstFaces(context is Context, surface is map, facesQuery is Query) returns ValueWithUnits
{
    const uParameters = spanMidpointParameters(surface.uKnots, surface.uDegree, 12);
    const vParameters = spanMidpointParameters(surface.vKnots, surface.vDegree, 12);
    var points = makeArray(size(uParameters) * size(vParameters), WORLD_ORIGIN);
    for (var i = 0; i < size(uParameters); i += 1)
    {
        for (var j = 0; j < size(vParameters); j += 1)
        {
            points[i * size(vParameters) + j] = evaluateBSplineSurfacePoint(surface, uParameters[i], vParameters[j]);
        }
    }
    return evPointsDeviation(context, { "points" : points, "topologies" : facesQuery })[0].deviation;
}

/** The parameter domain of both directions, as plain numbers, for before/after comparison. */
function surfaceDomains(surface is map) returns map
{
    return {
            "u" : knotDomain(surface.uKnots, surface.uDegree),
            "v" : knotDomain(surface.vKnots, surface.vDegree)
        };
}

/**
 * Re-read the surface the edits apply ON TOP OF, for the manipulator handler and the editing logic,
 * both of which run outside the feature body and so have no access to what it computed. Mirrors
 * editCurve.fs's computeBSplineBeforeEdit, including its tolerance for failure: if the read throws,
 * the caller gets `{}` and decides for itself what that means.
 *
 * THIS MUST PRODUCE THE SAME NET THE FEATURE BODY DOES, which is why it goes through
 * readSurfaceAndTrim rather than readSurfaceDefinition even though it never wants the loops. The
 * trimmed result mode reads through evApproximateBSplineSurface — a different call, and a different
 * control net, with its own count — so reading exactly here and approximately there would put the
 * handles somewhere the regeneration does not, and would compute a rotation bake against positions
 * the surface does not have. Same rule freeFormDeformation.fs states on readSourceSurfaces, and the
 * reason `result` is a bake trigger in preparedNetChanged.
 */
function computeSurfaceBeforeEdit(context is Context, definition is map) returns map
{
    if (isQueryEmpty(context, definition.face))
    {
        return {};
    }
    // If the read throws it means the face is not a spline and approximation is off, or the
    // approximation parameters are wrong. Both are ordinary states during interactive editing rather
    // than diagnosable faults, so failing quietly to {} is right — same call editCurve.fs makes.
    var surface;
    try
    {
        // Multi-face input is the merge: rebuild the SAME merged net the feature body built, so a
        // clicked handle maps to the right (u, v) on it. buildMergedSurface has no reporting for
        // exactly this reason — this caller has no feature to report against.
        const faces = evaluateQuery(context, definition.face);
        surface = size(faces) > 1
            ? buildMergedSurface(context, definition, faces).surface
            : normalizeSurfaceDefinition(readSurfaceAndTrim(context, definition).surface);
    }
    catch
    {
        return {};
    }
    return prepareSurface(surface, definition);
}

//==================================================================
//======================= Control point edit =======================
//==================================================================

/**
 * The number of INDEPENDENT control points per direction — the ones a user may move.
 *
 * For a clamped direction that is every stored row/column. For a PERIODIC direction the stored form
 * carries `degree` extra rows that are literal copies of the first `degree` (the overlap condition
 * P[i] == P[i + n], which is what tells the evaluator to wrap). Those copies are not independent
 * geometry, and exposing handles for them would let a user move a row without its twin — producing
 * a surface flagged closed whose data is not, which the kernel rejects as
 * PERIODIC_BSPLINESURFACE_NOT_SMOOTH if you are lucky and silently accepts as a creased "closed"
 * surface if you are not.
 */
function fundamentalControlPointCounts(surface is map) returns map
{
    return {
            "u" : surface.isUPeriodic == true ? size(surface.controlPoints) - surface.uDegree : size(surface.controlPoints),
            "v" : surface.isVPeriodic == true ? size(surface.controlPoints[0]) - surface.vDegree : size(surface.controlPoints[0])
        };
}

/**
 * Every STORED index that is a copy of the given FUNDAMENTAL index in one direction. For a clamped
 * direction that is the index itself; for a periodic one it is that index and every image of it one
 * period further along, because stored index r holds fundamental index r mod n by construction.
 *
 * Editing all images together is what makes the overlap condition unbreakable here rather than
 * something a later validation step has to catch.
 */
function storedImagesOfFundamentalIndex(fundamentalIndex is number, fundamentalCount is number, storedCount is number, isPeriodic is boolean) returns array
{
    if (!isPeriodic)
    {
        return [fundamentalIndex];
    }
    var images = [];
    for (var storedIndex = fundamentalIndex; storedIndex < storedCount; storedIndex += fundamentalCount)
    {
        images = append(images, storedIndex);
    }
    return images;
}

/** A map key for a (u, v) cell. FeatureScript maps want a scalar key, and the net is 2D. */
function cellKey(uIndex is number, vIndex is number) returns string
{
    return uIndex ~ "," ~ vIndex;
}

/**
 * Apply the user's offsets and weight overrides to the control net. Mirrors editCurve.fs's
 * editControlPoints, including both of its guards: out of bounds, and two edits targeting the same
 * control point.
 *
 * TAKES NO `id`, and that is what lets the manipulator and editing-logic paths call it. Both need
 * the COMMITTED net — the state the live selection transform is measured against — and neither has
 * a feature id to report against. Nothing here ever needed one: the two guards throw `regenError`,
 * which carries its own parameter names.
 */
function applyControlPointEdits(context is Context, surface is map, controlPointEdits is array) returns map
{
    const counts = fundamentalControlPointCounts(surface);
    const storedUCount = size(surface.controlPoints);
    const storedVCount = size(surface.controlPoints[0]);

    var editedCells = {};
    for (var i = 0; i < size(controlPointEdits); i += 1)
    {
        const controlPointEdit = controlPointEdits[i];
        if (controlPointEdit.uIndex >= counts.u || controlPointEdit.vIndex >= counts.v)
        {
            throw regenError("Control point edit (" ~ controlPointEdit.uIndex ~ ", " ~ controlPointEdit.vIndex ~
                    ") is out of bounds; this net has " ~ counts.u ~ " x " ~ counts.v ~ " editable control points.",
                ["controlPointEdits"]);
        }
        const key = cellKey(controlPointEdit.uIndex, controlPointEdit.vIndex);
        if (editedCells[key] == true)
        {
            throw regenError("Multiple edits targeting control point (" ~ controlPointEdit.uIndex ~ ", " ~
                    controlPointEdit.vIndex ~ ")", ["controlPointEdits"]);
        }
        editedCells[key] = true;

        var point = surface.controlPoints[controlPointEdit.uIndex][controlPointEdit.vIndex];
        if (!isQueryEmpty(context, controlPointEdit.reference))
        {
            point = evVertexPoint(context, { "vertex" : controlPointEdit.reference });
        }
        const offset = [controlPointEdit.x, controlPointEdit.y, controlPointEdit.z] as Vector;
        const editedPoint = point + offset;

        // Write the same value to every stored image of this fundamental cell, so a periodic
        // direction's overlap condition survives by construction.
        const rowImages = storedImagesOfFundamentalIndex(controlPointEdit.uIndex, counts.u, storedUCount, surface.isUPeriodic == true);
        const columnImages = storedImagesOfFundamentalIndex(controlPointEdit.vIndex, counts.v, storedVCount, surface.isVPeriodic == true);
        for (var row in rowImages)
        {
            for (var column in columnImages)
            {
                surface.controlPoints[row][column] = editedPoint;
                surface.weights[row][column] = controlPointEdit.weight;
            }
        }
    }
    return surface;
}

//==================================================================
//========================== Manipulators ==========================
//==================================================================

/** The editable control points, row-major, as the flat array every manipulator wants, plus the
    counts needed to map a flat index back to (u, v). */
function fundamentalControlPointArray(surface is map) returns map
{
    const counts = fundamentalControlPointCounts(surface);
    var points = makeArray(counts.u * counts.v, surface.controlPoints[0][0]);
    for (var uIndex = 0; uIndex < counts.u; uIndex += 1)
    {
        for (var vIndex = 0; vIndex < counts.v; vIndex += 1)
        {
            points[uIndex * counts.v + vIndex] = surface.controlPoints[uIndex][vIndex];
        }
    }
    return { "points" : points, "uCount" : counts.u, "vCount" : counts.v };
}

/** Flat manipulator index -> (u, v). The inverse of fundamentalControlPointArray's packing, and the
    only place outside it that is allowed to know the packing. */
function cellFromFlatIndex(flatIndex is number, vCount is number) returns map
{
    const uIndex = floor(flatIndex / vCount);
    return { "uIndexValue" : uIndex, "vIndexValue" : flatIndex - uIndex * vCount };
}

/** (u, v) -> flat manipulator index. */
function flatIndexFromCell(uIndex is number, vIndex is number, vCount is number) returns number
{
    return uIndex * vCount + vIndex;
}

/**
 * The handle for XYZ mode: a FULL triad — translation arrows and rotation rings — on the selection's
 * own centre, in the surface's own frame. Back-ported from freeFormDeformation.fs's
 * showTransformManipulator, and the extension over routingCurve.fs's template that
 * EDIT_SURFACE_SPEC.md section 3 asked for.
 *
 * ALWAYS the full triad, never a plain one. A fullTriadManipulator carries translation arrows as
 * well as rotation rings, so a translate-only mode is strictly less capable at no saving, and the
 * rotation — which nothing else in this feature can express — would be the thing hidden behind the
 * mode selector. It also costs a whole class of bug: two manipulators writing the same selection
 * through different storage (a live transform versus per-point overrides) have to be reconciled
 * whenever the mode changes, and FREE_FORM_DEFORMATION_SPEC.md section 6.5 records what that looked
 * like when they were not.
 *
 * Its base has to be the frame the stored transform was measured in, which is the COMMITTED net —
 * point overrides applied, live transform not. Handing it the already-transformed positions instead
 * would compound the transform against itself on every regeneration.
 *
 * @param context {Context}
 * @param id {Id}
 * @param definition {map}
 * @param committedSurface {map} : the net with point overrides applied and no live transform
 * @param surfaceBeforeEdits {map} : the prepared net with nothing applied, for the frame directions
 */
function showSelectionTransformManipulator(context is Context, id is Id, definition is map,
    committedSurface is map, surfaceBeforeEdits is map)
{
    if (!indicesAreValid(context, id, definition.selectedIndices, committedSurface))
    {
        return;
    }
    // Duplicates in the list need no warning of their own any more: validSelectedCells collapses
    // them, and one cell transformed once is exactly what a duplicate should mean. The old
    // translate-only triad had to refuse them because a repeated cell skewed the average it took.
    const selectedCells = validSelectedCells(definition.selectedIndices, fundamentalControlPointCounts(committedSurface));
    if (size(selectedCells) == 0)
    {
        return;
    }

    const base = selectionBaseCoordSystem(committedSurface, surfaceBeforeEdits, selectedCells);
    const stored = storedSelectionTransform(definition);

    addManipulators(context, id, {
                (SELECTION_TRANSFORM_MANIPULATOR) : fullTriadManipulator({
                            "base" : base,
                            "transform" : stored == undefined ? identityTransform() : stored,
                            "displayEditView" : true
                        })
            });
}

/**
 * The frame the live selection transform acts in: origin at the selection's centroid on the
 * COMMITTED net, axes from the surface's own frame at the selection's central control point.
 *
 * WHY THE SURFACE'S FRAME AND NOT THE WORLD'S. This is the editSurface answer to what
 * freeFormDeformation.fs solves by orienting its triad to the lattice's own axes: the useful
 * rotations are the ones the geometry defines. Rotating about the NORMAL twists a block of control
 * points in the surface; rotating about the u tangent rolls a patch edge. Both are unusable if the
 * rings are stuck to world X/Y/Z on a surface that is not axis-aligned.
 *
 * `coordSystem` requires its two named axes to be perpendicular, and they are, exactly: the normal
 * is `normalize(cross(uTangent, vTangent))`, which is perpendicular to the u tangent by
 * construction. The V TANGENT IS DELIBERATELY NOT USED as an axis — u and v are not orthogonal on a
 * general surface, so it would not be a legal frame, and orthogonalizing it would cost it the very
 * meaning that makes it worth showing. Its role stays with the UVN edit mode, which is three
 * independent one-dimensional drags rather than a triad and so is free to be oblique.
 *
 * THE DIRECTIONS COME FROM THE UNEDITED NET, the centroid from the committed one. The stored
 * transform is measured against this frame, so a frame that chased the edits would silently
 * reinterpret it on every regeneration. Preparing (approximate, elevate, refine) does move it — and
 * every one of those is a bake trigger in the editing logic, for exactly this reason.
 *
 * Falls back to world axes where the surface has no frame at all — a cone apex, a pole — which is
 * the only honest answer there and still leaves the triad usable.
 *
 * @param committedSurface {map} : the net the centroid is taken from
 * @param surfaceBeforeEdits {map} : the net the directions are taken from
 * @param selectedCells {array} : cells, already range-checked and de-duplicated
 * @returns {CoordSystem}
 */
function selectionBaseCoordSystem(committedSurface is map, surfaceBeforeEdits is map, selectedCells is array) returns CoordSystem
{
    var centroid = WORLD_ORIGIN;
    var uIndexSum = 0;
    var vIndexSum = 0;
    for (var cell in selectedCells)
    {
        centroid += committedSurface.controlPoints[cell.uIndexValue][cell.vIndexValue];
        uIndexSum += cell.uIndexValue;
        vIndexSum += cell.vIndexValue;
    }
    centroid /= size(selectedCells);

    const central = centralSelectedCell(selectedCells, uIndexSum / size(selectedCells), vIndexSum / size(selectedCells));
    // try silent, matching showUVNTangentEditManipulators' call to the same function: the only thing
    // it throws is "degenerate here", which is a shape the surface is allowed to have and which the
    // fallback below is the answer to.
    const frame = try silent(surfaceFrameAtControlPoint(surfaceBeforeEdits, central.uIndexValue, central.vIndexValue));
    if (frame == undefined)
    {
        return coordSystem(centroid, X_DIRECTION, Z_DIRECTION);
    }
    return coordSystem(centroid, frame.uDirection, frame.normal);
}

/**
 * The selected cell nearest the selection's average index, which is the one whose surface frame best
 * represents the whole selection.
 *
 * Ties break on the lower u then the lower v so that the frame does not depend on the ORDER the
 * cells were clicked in — a selection is a set, and a triad that reorients because the same points
 * were picked in a different sequence would reinterpret a live transform for no reason.
 *
 * @param selectedCells {array} : at least one cell
 * @param averageUIndex {number}
 * @param averageVIndex {number}
 * @returns {map} : the chosen cell
 */
function centralSelectedCell(selectedCells is array, averageUIndex is number, averageVIndex is number) returns map
{
    var best = selectedCells[0];
    var bestDistanceSquared = squaredIndexDistance(best, averageUIndex, averageVIndex);
    for (var cell in selectedCells)
    {
        const distanceSquared = squaredIndexDistance(cell, averageUIndex, averageVIndex);
        if (distanceSquared < bestDistanceSquared ||
            (distanceSquared == bestDistanceSquared &&
                    (cell.uIndexValue < best.uIndexValue ||
                        (cell.uIndexValue == best.uIndexValue && cell.vIndexValue < best.vIndexValue))))
        {
            best = cell;
            bestDistanceSquared = distanceSquared;
        }
    }
    return best;
}

/** A cell's squared distance from a point in INDEX space, which is unitless and has nothing to do
    with how far apart the control points are in model space.

    @param cell {map} : `uIndexValue`, `vIndexValue`
    @param averageUIndex {number}
    @param averageVIndex {number}
    @returns {number} */
function squaredIndexDistance(cell is map, averageUIndex is number, averageVIndex is number) returns number
{
    const uOffset = cell.uIndexValue - averageUIndex;
    const vOffset = cell.vIndexValue - averageVIndex;
    return uOffset * uOffset + vOffset * vOffset;
}

/**
 * Apply the live selection transform on top of the committed point overrides.
 *
 * Why this is a separate stage from the overrides at all: a fullTriadManipulator reports one
 * CUMULATIVE transform relative to the base it was last handed, not an increment, so it cannot be
 * folded into per-point overrides on every drag frame without either composing inverses or re-reading
 * the face hundreds of times per drag. Keeping it live and baking it exactly once, when the selection
 * changes, does neither. bakeSelectionTransformOnSurface performs that bake and is the only other
 * place this arithmetic appears — the two must stay in step, which is why both go through
 * selectionTransformInWorld.
 *
 * @param committedSurface {map} : the net with point overrides applied
 * @param surfaceBeforeEdits {map} : the prepared net, for the base frame's directions
 * @param definition {map}
 * @returns {map} : the net with the transform applied to the selected control points
 */
function applySelectionTransform(committedSurface is map, surfaceBeforeEdits is map, definition is map) returns map
{
    var surface = committedSurface;
    const counts = fundamentalControlPointCounts(surface);
    const selectedCells = validSelectedCells(definition.selectedIndices, counts);
    if (size(selectedCells) == 0)
    {
        return surface;
    }

    const worldTransform = selectionTransformInWorld(committedSurface, surfaceBeforeEdits, selectedCells, definition);
    if (worldTransform == undefined)
    {
        return surface;
    }

    const storedUCount = size(surface.controlPoints);
    const storedVCount = size(surface.controlPoints[0]);
    for (var cell in selectedCells)
    {
        const moved = worldTransform * surface.controlPoints[cell.uIndexValue][cell.vIndexValue];
        // Every stored image of the cell, so a periodic direction's overlap condition survives by
        // construction — the same rule applyControlPointEdits follows, for the same reason. Reading
        // before writing is safe because distinct fundamental cells have disjoint image sets.
        const rowImages = storedImagesOfFundamentalIndex(cell.uIndexValue, counts.u, storedUCount, surface.isUPeriodic == true);
        const columnImages = storedImagesOfFundamentalIndex(cell.vIndexValue, counts.v, storedVCount, surface.isVPeriodic == true);
        for (var row in rowImages)
        {
            for (var column in columnImages)
            {
                surface.controlPoints[row][column] = moved;
            }
        }
    }
    return surface;
}

/**
 * The selection transform, sandwiched into world space, or undefined when it is the identity.
 *
 * The sandwich, the stored transposed rotation, and the un-inverted translation together are
 * routingCurve.fs's convention, carried here through freeFormDeformation.fs unchanged — see
 * docs/featurescript-guides/TRIAD_MANIPULATOR_NOTES.md. What matters is that the SAME reconstruction
 * drives the control points and redisplays the manipulator, which is what makes a drag land where the
 * handle went.
 *
 * @param committedSurface {map}
 * @param surfaceBeforeEdits {map}
 * @param selectedCells {array} : cells, already range-checked
 * @param definition {map}
 * @returns {Transform} : world-space transform, or `undefined` if there is nothing to apply
 */
function selectionTransformInWorld(committedSurface is map, surfaceBeforeEdits is map, selectedCells is array,
    definition is map)
{
    const localTransform = storedSelectionTransform(definition);
    if (localTransform == undefined)
    {
        return undefined;
    }
    const base = selectionBaseCoordSystem(committedSurface, surfaceBeforeEdits, selectedCells);
    return toWorld(base) * localTransform * fromWorld(base);
}

/**
 * Reconstruct the stored selection transform, or undefined when it is the identity and there is
 * nothing to do.
 *
 * Every field is checked rather than assumed. These parameters live inside the "Edit control points"
 * group, so a definition whose editing group has never been opened legitimately does not carry them,
 * and every caller here is reached from paths that run with editing off.
 *
 * @param definition {map}
 * @returns {Transform} : the transform in the selection's local frame, or `undefined`
 */
function storedSelectionTransform(definition is map)
{
    if (!(definition.selectionTranslateX is ValueWithUnits) || !(definition.selectionTranslateY is ValueWithUnits) ||
        !(definition.selectionTranslateZ is ValueWithUnits))
    {
        return undefined;
    }
    const rotation = definition.selectionRotation;
    const translation = vector(definition.selectionTranslateX, definition.selectionTranslateY,
        definition.selectionTranslateZ);
    const translationIsZero = tolerantEquals(translation, WORLD_ORIGIN);

    if (!(rotation is array) || size(rotation) != 9)
    {
        return translationIsZero ? undefined : transform(identityMatrix(3), translation);
    }
    if (translationIsZero && rotationIsIdentity(rotation))
    {
        return undefined;
    }
    return transform(matrix([
                    [rotation[0], rotation[1], rotation[2]],
                    [rotation[3], rotation[4], rotation[5]],
                    [rotation[6], rotation[7], rotation[8]]
                ]), translation);
}

/** Whether a stored flat rotation is the identity, and so whether there is any rotation to apply.

    @param rotation {array} : nine unitless values, row by row
    @returns {boolean} */
function rotationIsIdentity(rotation is array) returns boolean
{
    for (var index = 0; index < 9; index += 1)
    {
        if (abs(rotation[index] - IDENTITY_ROTATION[index]) > 1e-10)
        {
            return false;
        }
    }
    return true;
}

/**
 * The selection, filtered to cells the current net actually has, and de-duplicated.
 *
 * Silent rather than loud, unlike the out-of-bounds check on a stored point OVERRIDE: an override is
 * work the user would want to hear about losing, where a stale selection is transient state with
 * nothing in it worth recovering. indicesAreValid still warns on the display path, so an out-of-range
 * selection is not invisible.
 *
 * The parameter is untyped because `selectedIndices` is declared inside a driven group, so it is
 * legitimately absent from a definition whose editing group has never been opened.
 *
 * @param selectedIndices {array} : the definition's selection, or `undefined`
 * @param counts {map} : `u` and `v` fundamental control point counts
 * @returns {array} : cells
 */
function validSelectedCells(selectedIndices, counts is map) returns array
{
    if (!(selectedIndices is array))
    {
        return [];
    }
    var seen = {};
    var cells = [];
    for (var cell in selectedIndices)
    {
        if (cell.uIndexValue < 0 || cell.uIndexValue >= counts.u ||
            cell.vIndexValue < 0 || cell.vIndexValue >= counts.v)
        {
            continue;
        }
        const key = cellKey(cell.uIndexValue, cell.vIndexValue);
        if (seen[key] == true)
        {
            continue;
        }
        seen[key] = true;
        cells = append(cells, cell);
    }
    return cells;
}

/**
 * Three linear manipulators along the surface's own frame at the selected control point. Mirrors
 * editCurve.fs's showUVNTangentEditManipulators, and is BETTER DEFINED here than there: a curve has
 * to borrow a curvature frame that degenerates at an inflection, while a surface's normal is
 * unambiguous everywhere it is not degenerate.
 *
 * U and V are the isoparametric tangents, which are NOT orthogonal in general. That is fine and is
 * not worth "fixing" by orthogonalizing: these are three independent one-dimensional drags, not an
 * orthogonal triad, exactly as in editCurve. Orthogonalizing would cost U and V their meaning as
 * "along the isoparametric line" and buy nothing.
 *
 * The frame comes from the surface BEFORE the edits, so it stays put while a point is dragged
 * instead of chasing the geometry the drag is changing — editCurve's reasoning, kept.
 */
function showUVNTangentEditManipulators(context is Context, id is Id, surfaceBeforeEdit is map, surface is map, definition is map)
{
    if (!indexIsValid(context, id, definition.selectedUIndex, definition.selectedVIndex, surface))
    {
        return;
    }

    const frame = try silent(surfaceFrameAtControlPoint(surfaceBeforeEdit, definition.selectedUIndex, definition.selectedVIndex));
    if (frame == undefined)
    {
        reportFeatureWarning(context, id, "The surface is degenerate at control point (" ~ definition.selectedUIndex ~
            ", " ~ definition.selectedVIndex ~ "), so it has no tangent frame there. Use XYZ mode for this point.",
            ["editPointMode"]);
        return;
    }

    const base = surface.controlPoints[definition.selectedUIndex][definition.selectedVIndex];

    addManipulators(context, id, {
                (U_TANGENT_MANIPULATOR) : linearManipulator({ "base" : base, "direction" : frame.uDirection, "offset" : 0 * meter })
            });
    addManipulators(context, id, {
                (V_TANGENT_MANIPULATOR) : linearManipulator({ "base" : base, "direction" : frame.vDirection, "offset" : 0 * meter })
            });
    addManipulators(context, id, {
                (N_MANIPULATOR) : linearManipulator({ "base" : base, "direction" : frame.normal, "offset" : 0 * meter })
            });
}

/**
 * The clickable control point dots. Mirrors editCurve.fs's pair of showIndexManipulators overloads:
 * a TOGGLE points manipulator (multi-select) in XYZ mode, a plain points manipulator (single
 * select) otherwise, and a plain one with nothing selected when editing is off, so the points are
 * still clickable as a way to TURN editing on.
 */
function showIndexManipulators(context is Context, id is Id, surface is map, definition is map)
{
    const packed = fundamentalControlPointArray(surface);

    if (!definition.editControlPoints)
    {
        addManipulators(context, id, {
                    (INDEX_MANIPULATOR) : pointsManipulator({ "points" : packed.points, "index" : -1 })
                });
    }
    else if (definition.editPointMode == EditPointMode.XYZ)
    {
        var selectedFlatIndices = [];
        for (var selectedIndex in definition.selectedIndices)
        {
            selectedFlatIndices = append(selectedFlatIndices,
                flatIndexFromCell(selectedIndex.uIndexValue, selectedIndex.vIndexValue, packed.vCount));
        }
        addManipulators(context, id, {
                    (INDICES_MANIPULATOR) : togglePointsManipulator({
                                "points" : packed.points,
                                "selectedIndices" : selectedFlatIndices,
                                "suppressedIndices" : []
                            })
                });
    }
    else
    {
        addManipulators(context, id, {
                    (INDEX_MANIPULATOR) : pointsManipulator({
                                "points" : packed.points,
                                "index" : flatIndexFromCell(definition.selectedUIndex, definition.selectedVIndex, packed.vCount)
                            })
                });
    }
}

/**
 * Manipulator change handling for surface editing. Mirrors editCurve.fs's
 * onEditCurveManipulatorChange one branch at a time, with the XYZ triad's branch replaced by the
 * live-transform storage a fullTriadManipulator needs.
 *
 * THIS IS NOT THE LAST WORD ON THE DEFINITION. editSurfaceEditLogic runs after this returns, with an
 * `oldDefinition` that predates everything written here — so anything it copies wholesale out of that
 * old state silently reverts this function's work. bakeAgainstPreviousState documents the split that
 * keeps the two in step; it is worth re-reading before adding a branch here.
 */
export function onEditSurfaceManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    // Read the surface ONCE for the whole handler and thread it through. editCurve.fs calls its
    // computeBSplineBeforeEdit once per multi-edit too; doing it per new control point would re-read
    // and re-normalize the face for every point in the selection. The bakes below take it as an
    // argument for the same reason: a bake reads geometry, and this is that geometry.
    const surfaceBeforeEdit = computeSurfaceBeforeEdit(context, definition);
    const packed = try silent(fundamentalControlPointArray(surfaceBeforeEdit));
    const vCount = packed == undefined ? 1 : packed.vCount;

    if (newManipulators[INDEX_MANIPULATOR] is map)
    {
        // If the user deliberately selects a control point, we turn on CP editing.
        definition.editControlPoints = true;
        const cell = cellFromFlatIndex(newManipulators[INDEX_MANIPULATOR].index, vCount);
        if (definition.editPointMode == EditPointMode.XYZ)
        {
            // Any live transform belongs to the selection that is about to be replaced, so it is
            // committed to that selection's overrides before the selection changes underneath it.
            definition = bakeSelectionTransformOnSurface(context, definition, surfaceBeforeEdit);
            definition.selectedIndices = [cell];
        }
        else
        {
            definition.selectedUIndex = cell.uIndexValue;
            definition.selectedVIndex = cell.vIndexValue;
        }
    }
    if (newManipulators[INDICES_MANIPULATOR] is map)
    {
        definition = bakeSelectionTransformOnSurface(context, definition, surfaceBeforeEdit);
        definition.selectedIndices = mapArray(newManipulators[INDICES_MANIPULATOR].selectedIndices,
            flatIndex => cellFromFlatIndex(flatIndex, vCount));
    }
    if (newManipulators[SELECTION_TRANSFORM_MANIPULATOR] is map)
    {
        // Stored verbatim, in the same decomposition routingCurve.fs uses. It stays live — applied
        // at regeneration by applySelectionTransform — until the selection changes.
        const reported = newManipulators[SELECTION_TRANSFORM_MANIPULATOR].transform;
        const transposedLinear = transpose(reported.linear);
        definition.selectionRotation = [
                transposedLinear[0][0], transposedLinear[0][1], transposedLinear[0][2],
                transposedLinear[1][0], transposedLinear[1][1], transposedLinear[1][2],
                transposedLinear[2][0], transposedLinear[2][1], transposedLinear[2][2]
            ];
        definition.selectionTranslateX = reported.translation[0];
        definition.selectionTranslateY = reported.translation[1];
        definition.selectionTranslateZ = reported.translation[2];
    }
    for (var linearManipulatorName in LINEAR_MANIPULATORS)
    {
        if (newManipulators[linearManipulatorName] is map)
        {
            definition = processSingleDirectionEdit(definition, newManipulators[linearManipulatorName], surfaceBeforeEdit);
        }
    }
    return definition;
}

/**
 * A drag along one of the UVN manipulators. Mirrors editCurve.fs's processSingleDirectionEdit: find
 * the existing edit for the selected point and ADD the offset to it, or create one if there is
 * none, seeding the new edit's weight from the surface so the drag does not silently reset it.
 */
function processSingleDirectionEdit(definition is map, manip is map, surfaceBeforeEdit is map) returns map
{
    const offset = manip.direction * manip.offset;
    for (var i = 0; i < size(definition.controlPointEdits); i += 1)
    {
        var edit = definition.controlPointEdits[i];
        if (edit.uIndex != definition.selectedUIndex || edit.vIndex != definition.selectedVIndex)
        {
            continue;
        }
        edit.x += offset[0];
        edit.y += offset[1];
        edit.z += offset[2];
        definition.controlPointEdits[i] = edit;
        return definition;
    }
    // We haven't found an existing edit, we add a new one.
    definition.controlPointEdits = append(definition.controlPointEdits,
        newEdit(definition.selectedUIndex, definition.selectedVIndex, offset,
            weightAt(surfaceBeforeEdit, definition.selectedUIndex, definition.selectedVIndex)));
    return definition;
}

/**
 * Commit the live selection transform into per-point overrides and reset it to the identity, given
 * the prepared net it was measured against.
 *
 * This is the counterpart of applySelectionTransform and MUST agree with it point for point — both
 * go through selectionTransformInWorld for exactly that reason. It runs when the selection changes
 * and when the dialog opens, never once per drag frame, which is the whole point of keeping the
 * transform live in between.
 *
 * AN UNREADABLE NET LEAVES THE TRANSFORM ALONE. A face selection that is empty, not yet resolvable,
 * or in need of the approximation toggle gives no geometry to bake against. The transform is then
 * KEPT rather than reset, because resetting it there does not defer the edit, it deletes it: the
 * overrides never received it and the live parameters no longer hold it. Reaching that path at all
 * requires a net the feature body cannot regenerate from either, so the user is already being shown
 * an error, while the work a reset would destroy is real and silent.
 *
 * An empty SELECTION is a different matter and still resets: there is nothing for the transform to
 * act on, so it is dead weight that would otherwise attach itself to the next selection.
 *
 * @param context {Context}
 * @param definition {map}
 * @param surfaceBeforeEdits {map} : the prepared net with nothing applied, or `{}` if unreadable
 * @returns {map} : the updated definition, with the transform reset unless it could not be baked
 */
function bakeSelectionTransformOnSurface(context is Context, definition is map, surfaceBeforeEdits is map) returns map
{
    if (storedSelectionTransform(definition) == undefined)
    {
        return definition;
    }
    if (surfaceBeforeEdits.controlPoints == undefined)
    {
        return definition;
    }

    // try silent because the feature body's call is the one that reports. applyControlPointEdits
    // throws on exactly two things — an override out of bounds, or two overrides on one control
    // point — and both are regenErrors the next regeneration raises against the right parameter.
    // This path has no feature to report against, so bailing is all it can honestly do.
    const committedSurface = try silent(applyControlPointEdits(context, surfaceBeforeEdits, definition.controlPointEdits));
    if (committedSurface == undefined)
    {
        return definition;
    }

    // An EMPTY selection falls through to the reset below rather than returning early.
    // selectionTransformInWorld is not called at all in that case — its base is the selection's
    // centroid, which needs at least one point to average.
    const selectedCells = validSelectedCells(definition.selectedIndices, fundamentalControlPointCounts(committedSurface));
    if (size(selectedCells) > 0)
    {
        const worldTransform = selectionTransformInWorld(committedSurface, surfaceBeforeEdits, selectedCells, definition);
        for (var cell in selectedCells)
        {
            const current = committedSurface.controlPoints[cell.uIndexValue][cell.vIndexValue];
            definition = addToControlPointEdit(definition, cell.uIndexValue, cell.vIndexValue,
                (worldTransform * current) - current,
                weightAt(surfaceBeforeEdits, cell.uIndexValue, cell.vIndexValue));
        }
    }

    return resetSelectionTransform(definition);
}

/** The same bake, for callers that do not already hold the prepared net. Reads it, which is the
    expensive part, so the manipulator handler uses the variant above instead.

    @param context {Context}
    @param definition {map}
    @returns {map} */
function bakeSelectionTransform(context is Context, definition is map) returns map
{
    if (storedSelectionTransform(definition) == undefined)
    {
        return definition;
    }
    return bakeSelectionTransformOnSurface(context, definition, computeSurfaceBeforeEdit(context, definition));
}

/**
 * Add a displacement to one control point's stored override, creating the record if there is none.
 *
 * Adding to `x`/`y`/`z` is right whether or not the record carries a reference vertex: the override
 * is a displacement FROM whatever the record's origin is, so adding to it moves the point by that
 * much either way.
 *
 * @param definition {map}
 * @param uIndex {number}
 * @param vIndex {number}
 * @param increment {Vector} : a displacement with length units
 * @param weight {number} : the weight a newly created record should carry
 * @returns {map} : the updated definition
 */
function addToControlPointEdit(definition is map, uIndex is number, vIndex is number, increment is Vector,
    weight is number) returns map
{
    for (var editIndex = 0; editIndex < size(definition.controlPointEdits); editIndex += 1)
    {
        var edit = definition.controlPointEdits[editIndex];
        if (edit.uIndex != uIndex || edit.vIndex != vIndex)
        {
            continue;
        }
        edit.x += increment[0];
        edit.y += increment[1];
        edit.z += increment[2];
        definition.controlPointEdits[editIndex] = edit;
        return definition;
    }

    definition.controlPointEdits = append(definition.controlPointEdits,
        newEdit(uIndex, vIndex, increment, weight));
    return definition;
}

/** Clear the live selection transform back to the identity.

    @param definition {map}
    @returns {map} */
function resetSelectionTransform(definition is map) returns map
{
    definition.selectionRotation = IDENTITY_ROTATION;
    definition.selectionTranslateX = 0 * meter;
    definition.selectionTranslateY = 0 * meter;
    definition.selectionTranslateZ = 0 * meter;
    return definition;
}

/** One control point edit record, shaped exactly like the precondition declares it. */
function newEdit(uIndex is number, vIndex is number, offset is Vector, weight is number) returns map
{
    return {
            "uIndex" : uIndex,
            "vIndex" : vIndex,
            "reference" : qNothing(),
            "x" : offset[0],
            "y" : offset[1],
            "z" : offset[2],
            "weight" : weight
        };
}

/** The weight a brand-new edit should carry: whatever the surface already has there, so creating an
    edit by dragging never silently changes the weight too. Falls back to 1, as editCurve does. */
function weightAt(surfaceBeforeEdit is map, uIndex is number, vIndex is number) returns number
{
    if (surfaceBeforeEdit.weights == undefined || uIndex >= size(surfaceBeforeEdit.weights) ||
        vIndex >= size(surfaceBeforeEdit.weights[0]))
    {
        return 1;
    }
    return surfaceBeforeEdit.weights[uIndex][vIndex];
}

//==================================================================
//=========================== Edit Logic ===========================
//==================================================================

/**
 * Editing logic for surface editing. Two jobs, one inherited and one that arrived with the full
 * triad.
 *
 * THE INHERITED JOB, from editCurve.fs's editCurveEditLogic: when the user changes an edit's
 * reference vertex, reset that edit's offset, because an offset measured from the old reference
 * means nothing from the new one. This is the reason array reordering is turned off for
 * controlPointEdits — the reset is positional.
 *
 * THE NEW JOB is the same job in every branch: the live selection transform is about to stop meaning
 * what it meant, so bake it into overrides while the state it was measured against still exists. The
 * stored numbers are relative to a base — the selection's centroid, in the surface's own frame — and
 * ANYTHING that moves that base silently redefines them. Four moments qualify, plus the Planarize
 * button, which arrives as `clickedButton` and does its own bake:
 *
 *   1. THE DIALOG IS OPENED (`oldDefinition == {}`). A transform left live when the dialog was last
 *      closed lives entirely in ALWAYS_HIDDEN parameters, so the surface comes back edited while the
 *      "Points overrides" list shows nothing that accounts for it. Baking on open is exactly
 *      geometry-preserving — the same selectionTransformInWorld arithmetic applySelectionTransform
 *      was already applying at every regeneration — so the only thing that changes is that the edit
 *      becomes VISIBLE and editable as overrides.
 *
 *   2. THE SELECTION CHANGES through the dialog. The manipulator path has its own bake in
 *      onEditSurfaceManipulatorChange; this covers the array being edited by hand.
 *
 *   3. THE PREPARED NET CHANGES. Approximate, elevate and refine all decide WHICH control points
 *      exist and where they sit, and the face selection decides what is being read at all, so a
 *      transform surviving one of those describes a different displacement afterwards than it did
 *      before. This is the same re-indexing hazard EDIT_SURFACE_SPEC.md section 4 fixes by ordering
 *      prepare before edit, seen from the transform's side.
 *
 *   4. THE EDIT MODE CHANGES. UVN mode drives a single point through the linear manipulators and
 *      never writes the transform, so a live one would sit there unowned until XYZ mode came back
 *      and re-applied it to whatever was selected by then.
 */
export function editSurfaceEditLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, clickedButton is string) returns map
{
    if (!definition.editControlPoints)
    {
        return definition;
    }

    // Case 1. There is no previous state to bake against, and none is needed: nothing has moved yet
    // this edit round, so the transform still means what it meant when it was stored and the current
    // definition IS the state it was measured in.
    if (oldDefinition == {})
    {
        return bakeSelectionTransform(context, definition);
    }

    // Checked before the tests below, not after: a button press is not a selection change those
    // tests would see, and planarizeSelection does its own bake anyway.
    if (clickedButton == "planarizeSelection")
    {
        return planarizeSelection(context, definition);
    }

    const referenceReset = resetEditWithChangedReference(context, oldDefinition, definition);
    if (referenceReset != undefined)
    {
        return referenceReset;
    }

    // Cases 2, 3 and 4.
    if (definition.editPointMode != oldDefinition.editPointMode ||
        !selectionsMatch(oldDefinition.selectedIndices, definition.selectedIndices) ||
        preparedNetChanged(context, oldDefinition, definition))
    {
        return bakeAgainstPreviousState(context, oldDefinition, definition);
    }
    return definition;
}

/**
 * The reference-vertex reset, unchanged in behaviour from when it was the whole of this feature's
 * editing logic: an offset is measured from its reference, so pointing the record at a different
 * vertex leaves the stored numbers describing nothing.
 *
 * Returns `undefined` rather than the definition when nothing changed, so the caller can tell "I
 * handled this round" from "carry on" — a reference change is not a selection change and must not
 * also provoke a bake.
 *
 * @param context {Context}
 * @param oldDefinition {map}
 * @param definition {map}
 * @returns {map} : the updated definition, or `undefined` if no reference changed
 */
function resetEditWithChangedReference(context is Context, oldDefinition is map, definition is map)
{
    const controlPointEditSize = size(definition.controlPointEdits);
    if (controlPointEditSize != size(oldDefinition.controlPointEdits))
    {
        return undefined;
    }
    for (var i = 0; i < controlPointEditSize; i += 1)
    {
        if (!areQueriesEquivalent(context, definition.controlPointEdits[i].reference, oldDefinition.controlPointEdits[i].reference))
        {
            definition.controlPointEdits[i].x = 0 * meter;
            definition.controlPointEdits[i].y = 0 * meter;
            definition.controlPointEdits[i].z = 0 * meter;
            return definition;
        }
    }
    return undefined;
}

/**
 * Bake the live selection transform against the state it was authored in, and return the CURRENT
 * definition carrying the result with the transform cleared.
 *
 * Which definition supplies what is the entire content of this function, and getting it wrong is how
 * overrides go missing:
 *
 *   - the OLD definition supplies the SELECTION and the net's geometry — the face, the approximation,
 *     elevation and refinement settings — because those are what the stored numbers are measured
 *     against;
 *   - the CURRENT definition supplies the OVERRIDES, because something may already have written to
 *     them this edit round and rolling that back would destroy it.
 *
 * The second point is not hypothetical: onEditSurfaceManipulatorChange bakes on its own before it
 * swaps the selection in, and this editing logic then runs on top of that with an `oldDefinition`
 * predating the bake. Sourcing overrides from `oldDefinition` would overwrite the freshly baked array
 * with the pre-bake one every time a selection change arrived through the manipulator — the drag
 * vanishing from the list AND from the geometry, because the manipulator had already reset the live
 * transform that was carrying it.
 *
 * When the manipulator has already baked, the transform copied across is the identity, so
 * bakeSelectionTransform short-circuits and this is a no-op on the overrides it just wrote. That is
 * the intended interaction between the two paths rather than a coincidence.
 *
 * @param context {Context}
 * @param oldDefinition {map} : the state the transform was measured against
 * @param definition {map} : the current state, whose overrides are authoritative
 * @returns {map} : the current definition, overrides baked and transform reset — or untouched and the
 *                  transform still live, if the old net could not be read
 */
function bakeAgainstPreviousState(context is Context, oldDefinition is map, definition is map) returns map
{
    var previous = oldDefinition;
    previous.controlPointEdits = definition.controlPointEdits;
    previous.selectionRotation = definition.selectionRotation;
    previous.selectionTranslateX = definition.selectionTranslateX;
    previous.selectionTranslateY = definition.selectionTranslateY;
    previous.selectionTranslateZ = definition.selectionTranslateZ;

    const bakedPrevious = bakeSelectionTransform(context, previous);
    definition.controlPointEdits = bakedPrevious.controlPointEdits;

    // A transform still live on the way back out means the bake could not run — see
    // bakeSelectionTransformOnSurface, which keeps it rather than deleting it. Resetting here would
    // undo that and lose the drag. It takes an unreadable OLD net to get here, and the old net is the
    // one that regenerated a moment ago, so in practice this is the already-erroring feature.
    if (storedSelectionTransform(bakedPrevious) != undefined)
    {
        return definition;
    }
    return resetSelectionTransform(definition);
}

/**
 * Whether anything that DEFINES the prepared net has changed, and so whether a live selection
 * transform has stopped describing the displacement it described before.
 *
 * Every input listed here feeds the net the transform's base was computed on: the face decides what
 * is read, Approximate decides whether it is read exactly and with what budget, and elevation and
 * refinement decide how many control points come out and where their Greville abscissae — and so the
 * base frame's directions — land.
 *
 * `result` is in the list because NEW_BODY_TRIMMED reads the face through evApproximateBSplineSurface
 * rather than evSurfaceDefinition, which is a different control net; see computeSurfaceBeforeEdit.
 *
 * @param context {Context}
 * @param oldDefinition {map}
 * @param definition {map}
 * @returns {boolean}
 */
function preparedNetChanged(context is Context, oldDefinition is map, definition is map) returns boolean
{
    if (definition.approximate != oldDefinition.approximate ||
        definition.elevate != oldDefinition.elevate ||
        definition.refine != oldDefinition.refine ||
        definition.result != oldDefinition.result)
    {
        return true;
    }
    // Each group's own parameters are only compared when the group is on, because they are declared
    // inside a driving-parameter group and a definition that has never had it on carries only the
    // defaults. The toggles themselves are already covered above.
    if (definition.approximate &&
        (definition.approximationUDegree != oldDefinition.approximationUDegree ||
                definition.approximationVDegree != oldDefinition.approximationVDegree ||
                definition.approximationMaxUCPs != oldDefinition.approximationMaxUCPs ||
                definition.approximationMaxVCPs != oldDefinition.approximationMaxVCPs ||
                definition.approximationTolerance != oldDefinition.approximationTolerance))
    {
        return true;
    }
    if (definition.elevate &&
        (definition.uElevationDegree != oldDefinition.uElevationDegree ||
                definition.vElevationDegree != oldDefinition.vElevationDegree))
    {
        return true;
    }
    if (definition.refine &&
        (definition.uControlPointCount != oldDefinition.uControlPointCount ||
                definition.vControlPointCount != oldDefinition.vControlPointCount))
    {
        return true;
    }
    return definition.face is Query && oldDefinition.face is Query &&
        !areQueriesEquivalent(context, definition.face, oldDefinition.face);
}

/** Whether two selections name the same cells in the same order.

    @param first {array}
    @param second {array}
    @returns {boolean} */
function selectionsMatch(first, second) returns boolean
{
    if (!(first is array) || !(second is array) || size(first) != size(second))
    {
        return false;
    }
    for (var index = 0; index < size(first); index += 1)
    {
        if (first[index].uIndexValue != second[index].uIndexValue ||
            first[index].vIndexValue != second[index].vIndexValue)
        {
            return false;
        }
    }
    return true;
}

/**
 * Flatten the selected control points onto the least-squares plane through them.
 *
 * A ONE-SHOT EDIT, not a constraint, and NOT editCurve.fs's `planarize` — that is a persistent toggle
 * flattening a whole curve against a chosen reference plane, where this is
 * freeFormDeformation.fs's button acting on a selection. It writes ordinary per-point overrides
 * through the same addToControlPointEdit every drag uses, so the result is indistinguishable from
 * having dragged the points there by hand and nothing keeps them coplanar afterwards. That is the
 * honest behaviour for a button: a persistent planar constraint would have to survive elevation and
 * refinement re-indexing the net and fight every subsequent drag, which is a different feature.
 *
 * The live selection transform is BAKED FIRST, because the button acts on where the points visibly
 * ARE and the transform is part of that. Baking also means the projection it writes cannot be
 * silently re-transformed on the next regeneration.
 *
 * DEGENERATE SELECTIONS NEED NO SPECIAL CASE. For collinear points the covariance matrix has rank 1,
 * so the fitted normal is perpendicular to the line and the plane therefore CONTAINS it — every point
 * is already on the plane and the projection moves nothing. Flattening a single net row is a no-op
 * rather than an error, which is the right answer: a row is already as planar as a line can be. Only
 * a selection too small to span a plane at all is rejected outright.
 *
 * @param context {Context}
 * @param definition {map}
 * @returns {map} : the updated definition
 */
function planarizeSelection(context is Context, definition is map) returns map
{
    var baked = bakeSelectionTransform(context, definition);

    const surfaceBeforeEdits = computeSurfaceBeforeEdit(context, baked);
    if (surfaceBeforeEdits.controlPoints == undefined)
    {
        return baked;
    }
    // Same try silent reasoning as the bake: an override that is out of bounds or doubled up is a
    // regenError the next regeneration raises against the right parameter, and the button doing
    // nothing is better than the dialog throwing.
    const committedSurface = try silent(applyControlPointEdits(context, surfaceBeforeEdits, baked.controlPointEdits));
    if (committedSurface == undefined)
    {
        return baked;
    }

    const selectedCells = validSelectedCells(baked.selectedIndices, fundamentalControlPointCounts(committedSurface));
    if (size(selectedCells) < 3)
    {
        return baked;
    }

    var points = [];
    for (var cell in selectedCells)
    {
        points = append(points, committedSurface.controlPoints[cell.uIndexValue][cell.vIndexValue]);
    }

    const fitted = leastSquaresPlane(points);
    for (var cellIndex = 0; cellIndex < size(selectedCells); cellIndex += 1)
    {
        const cell = selectedCells[cellIndex];
        // The signed distance to the plane, removed along the normal — the shortest move that lands
        // the point on it, so the selection keeps its shape in plan as far as flattening allows.
        const displacement = -dot(points[cellIndex] - fitted.origin, fitted.normal) * fitted.normal;
        baked = addToControlPointEdit(baked, cell.uIndexValue, cell.vIndexValue, displacement,
            weightAt(surfaceBeforeEdits, cell.uIndexValue, cell.vIndexValue));
    }
    return baked;
}

/**
 * The least-squares plane through a set of points: centroid for the origin, and for the normal the
 * eigenvector of the smallest eigenvalue of the covariance matrix `sum (p - o)(p - o)^t`.
 *
 * The derivation and the SVD route to it are std's own, in `editCurve.fs`'s `fitPlane` — minimizing
 * `sum (n . (p - o))^2` subject to `n . n = 1` makes `n` an eigenvector of that matrix by Lagrange
 * multipliers, and the smallest eigenvalue is the one that minimizes rather than maximizes.
 * `fitPlane` is private to editCurve.fs, so this is a transcription rather than a call; the SVD
 * orders singular values largest first, hence the LAST row of `transpose(u)`.
 *
 * Points are divided by `meter` before accumulating, because a Matrix holds plain numbers.
 *
 * @param points {array} : Vectors with length units, at least three of them
 * @returns {map} : `origin` {Vector} with length units, `normal` {Vector} unitless and unit length
 */
function leastSquaresPlane(points is array) returns map
{
    var centre = WORLD_ORIGIN;
    for (var point in points)
    {
        centre += point;
    }
    centre /= size(points);

    var covariance = zeroMatrix(3, 3);
    for (var point in points)
    {
        const offsetRow = matrix([(point - centre) / meter]);
        covariance = covariance + transpose(offsetRow) * offsetRow;
    }

    const uTransposed = transpose(svd(covariance).u);
    return { "origin" : centre, "normal" : normalize(uTransposed[2] as Vector) };
}

//==================================================================
//============================ Emission ============================
//==================================================================

/**
 * Build the kernel-facing BSplineSurface and create it.
 *
 * The two things here that are not obvious, both inherited from tweenSurfaces.fs rather than
 * rediscovered:
 *
 * (1) A PERIODIC direction must be emitted in the CLOSED CLAMPED form, not the module's internal
 * wrap-padded one. That is the form the kernel itself returns for a revolve and provably accepts
 * back (SPLINE_REFINEMENT_UTILITY_SPEC.md section 2.3.1); handing it a wrap form is how you earn a
 * PERIODIC_BSPLINESURFACE_NOT_SMOOTH. isPeriodic stays TRUE through the conversion — it is
 * smoothness metadata about the closure, not a claim about the knot array's shape.
 *
 * (2) The knot arrays need an explicit `is KnotArray` cast. FeatureScript's typecheck types do not
 * propagate through array operations, so a knot array that has been through any module function
 * comes back as a plain array and bSplineSurface rejects it.
 */
function kernelSurface(surface is map) returns BSplineSurface
{
    var emitted = surface;
    if (emitted.isUPeriodic == true)
    {
        emitted = toClosedClampedSurfaceDirection(emitted, true);
    }
    if (emitted.isVPeriodic == true)
    {
        emitted = toClosedClampedSurfaceDirection(emitted, false);
    }

    // No "isRational" field: bSplineSurface DERIVES it from whether weights were supplied and
    // ignores anything passed under that name.
    return bSplineSurface({
                "uDegree" : emitted.uDegree,
                "vDegree" : emitted.vDegree,
                "isUPeriodic" : emitted.isUPeriodic == true,
                "isVPeriodic" : emitted.isVPeriodic == true,
                "controlPoints" : controlPointMatrix(emitted.controlPoints),
                "weights" : matrix(emitted.weights),
                "uKnots" : emitted.uKnots is KnotArray ? emitted.uKnots : knotArray(emitted.uKnots),
                "vKnots" : emitted.vKnots is KnotArray ? emitted.vKnots : knotArray(emitted.vKnots)
            });
}

/** Create the untrimmed surface and RETURN the id it was created under, so the caller never has to
    reconstruct the string. */
function emitSurface(context is Context, idGenerator is function, surface is map) returns Id
{
    const createId = idGenerator();
    opCreateBSplineSurface(context, createId, { "bSplineSurface" : kernelSurface(surface) });
    return createId;
}

//==================================================================
//=================== Trimmed emission (experiment) ================
//==================================================================

/**
 * The UV bounding box of every control point in a loop, as plain numbers.
 *
 * This is the measurement that settles a question nothing in the docs answers: whether
 * evApproximateBSplineSurface's boundary curves are expressed in the surface's own KNOT domain or
 * normalized to [0, 1]. Feeding knot-domain parameters to something expecting normalized ones (or
 * the reverse) is a silent mismatch of exactly the kind already on record against
 * evFaceTangentPlanes, so it gets measured rather than assumed.
 */
function loopUVExtent(loop is array) returns map
{
    var uMin = undefined;
    var uMax = undefined;
    var vMin = undefined;
    var vMax = undefined;
    for (var curve in loop)
    {
        for (var controlPoint in curve.controlPoints)
        {
            uMin = uMin == undefined ? controlPoint[0] : min(uMin, controlPoint[0]);
            uMax = uMax == undefined ? controlPoint[0] : max(uMax, controlPoint[0]);
            vMin = vMin == undefined ? controlPoint[1] : min(vMin, controlPoint[1]);
            vMax = vMax == undefined ? controlPoint[1] : max(vMax, controlPoint[1]);
        }
    }
    return { "uMin" : uMin, "uMax" : uMax, "vMin" : vMin, "vMax" : vMax };
}

/** One line describing a loop's shape, for the diagnostic report. */
function describeLoop(loop is array) returns string
{
    if (size(loop) == 0)
    {
        return "empty";
    }
    var degrees = "";
    for (var curve in loop)
    {
        degrees ~= (degrees == "" ? "" : ",") ~ curve.degree ~ (curve.isRational ? "R" : "");
    }
    const extent = loopUVExtent(loop);
    return size(loop) ~ " curves (deg " ~ degrees ~ ", dim " ~ loop[0].dimension ~ "), u [" ~
        extent.uMin ~ ", " ~ extent.uMax ~ "] v [" ~ extent.vMin ~ ", " ~ extent.vMax ~ "]";
}

/**
 * EXPERIMENT — emit the surface WITH its trim, carrying the extracted loops through untouched.
 *
 * The architectural bet this tests, which is why it exists at all: a deformation map acts on 3D
 * control points and never touches the parameterization, so the UV trim loops of the ORIGINAL face
 * describe the DEFORMED face equally well, in the same coordinates. If that holds, trimming is a
 * one-time extraction problem rather than a per-deformation one, and the in/out question a
 * downstream trimming operation would have to re-resolve is already answered by the original
 * definition. It also removes the dependence on opReplaceFace, which cannot serve the deformation
 * engine's core anyway: it re-derives boundaries by INTERSECTING with neighbouring faces, and for a
 * non-affine map the intersection of two deformed faces is not the deformation of their original
 * intersection — so it would recompute, one face at a time and slightly wrongly, trims we already
 * know exactly.
 *
 * What this reports, because all of it is currently unmeasured:
 *   - the loop structure (how many loops, how many curves each, degrees, dimension);
 *   - the loops' UV extent against the surface's own knot domain, which settles whether the curves
 *     are in knot-domain or normalized coordinates;
 *   - whether the parameter domain SURVIVED normalization, elevation and editing, since the loops
 *     are only valid for as long as it does.
 *
 * What it builds:
 *   - the outer-trimmed sheet, from the outer loop;
 *   - one patch per inner loop, as its own body. Each hole's loop is individually a single closed
 *     loop, so it is a legal boundary in its own right — those patches are both a visual check that
 *     the holes land in the right place and the raw material for subtracting them.
 *   - a single all-loops-concatenated attempt, wrapped, purely to find out what the kernel does
 *     with it. The docs say one closed loop; this measures whether that is enforced.
 */
function emitTrimmedSurface(context is Context, id is Id, idGenerator is function, surface is map, extraction is map, domainBeforeEdits is map)
{
    const domainNow = surfaceDomains(surface);
    // A held domain is necessary and NOT sufficient, which is the one thing this check used to get
    // wrong. Uniformizing a closed direction re-maps parameter to point INSIDE a domain it preserves
    // exactly, so the four comparisons below all pass while every loop coordinate now names a
    // different place on the surface — and by more than a rounding, since arc-length
    // reparameterization moves a revolve's u by several degrees of arc. The flag prepareSurface sets
    // is the only evidence of that, so it is part of the verdict rather than a footnote to it.
    const domainHeld = abs(domainNow.u.start - domainBeforeEdits.u.start) < 1e-10 &&
        abs(domainNow.u.end - domainBeforeEdits.u.end) < 1e-10 &&
        abs(domainNow.v.start - domainBeforeEdits.v.start) < 1e-10 &&
        abs(domainNow.v.end - domainBeforeEdits.v.end) < 1e-10 &&
        surface.reparameterized != true;

    // Trimmed mode approximates unconditionally — evSurfaceDefinition returns no loops, so the
    // matched pair can only come from evApproximateBSplineSurface. Say so rather than let the
    // no-silent-approximation rule be quietly bypassed by a result mode.
    var report = "TRIM PROBE (surface + loops both approximated at tolerance " ~ extraction.tolerance ~
        " m, since only evApproximateBSplineSurface returns loops). Surface domain u [" ~
        domainNow.u.start ~ ", " ~ domainNow.u.end ~ "] v [" ~ domainNow.v.start ~ ", " ~ domainNow.v.end ~ "]. " ~
        "Parameter map preserved through normalize/uniformize/elevate/edit: " ~
        (domainHeld ? "YES" : (surface.reparameterized == true ?
                "NO - a closed direction was re-parameterized to arc length, loops are invalid" :
                "NO - loops are invalid")) ~ ". " ~
        "Outer loop: " ~ describeLoop(extraction.outerLoop) ~ ". Inner loops: " ~ size(extraction.innerLoops);
    for (var loopIndex = 0; loopIndex < size(extraction.innerLoops); loopIndex += 1)
    {
        report ~= " | #" ~ loopIndex ~ " " ~ describeLoop(extraction.innerLoops[loopIndex]);
    }
    reportFeatureInfo(context, id, report);

    const surfaceToCreate = kernelSurface(surface);

    // No holes: the ordinary case, and the one already confirmed working.
    if (size(extraction.innerLoops) == 0)
    {
        if (size(extraction.outerLoop) == 0)
        {
            opCreateBSplineSurface(context, idGenerator(), { "bSplineSurface" : surfaceToCreate });
        }
        else
        {
            opCreateBSplineSurface(context, idGenerator(), {
                        "bSplineSurface" : surfaceToCreate,
                        "boundaryBSplineCurves" : extraction.outerLoop
                    });
        }
        return;
    }

    // HOLES — the route, after two dead ends were measured rather than assumed.
    //
    // Dead end 1: outer + inner as separate loops in one boundaryBSplineCurves array. Refused with
    // BSPLINESURFACE_BOUNDARY_NOT_SINGLE_CLOSED_LOOP, exactly as documented.
    // Dead end 2: splicing them into one CLOSED loop with a bridge (the classical keyhole). Refused
    // with the same error in BOTH orientations, so it is not an orientation problem — the kernel
    // will not take a self-touching loop. That construction is gone; do not revive it.
    //
    // And there is no third door: `boundaryBSplineCurves` is the ONLY loop input in the entire
    // operation surface, it is single-loop by contract, and `innerLoopBSplineCurves` exists only as
    // an OUTPUT of evApproximateBSplineSurface. Inner loops cannot be carried through a B-spline
    // surface definition at construction time. That is a fact about the API, not a gap in effort.
    //
    // What the kernel WILL do is HOLD a multi-loop trimmed face — it just will not build one from
    // curves. So the hole is made topologically, after construction, and without a boolean:
    //
    //   1. build the outer-trimmed sheet;
    //   2. build each hole's own patch from that hole's loop (legal — one closed loop each);
    //   3. IMPRINT each patch's boundary EDGES onto the outer sheet with opSplitFace;
    //   4. delete the patch bodies, which were only ever tools;
    //   5. opDeleteFace the enclosed face with leaveOpen, leaving a genuine hole.
    //
    // Step 3 uses the patches' EDGES as `edgeTools`, NOT the patches as `bodyTools`. That
    // distinction is the whole reason this is not a boolean: the patches share their surface with
    // the target exactly, so intersecting them as bodies is a coincident-surface intersection —
    // degenerate and fragile. Their edges, by contrast, already lie exactly ON the target face,
    // because the kernel built them from these very UV curves against this very surface. So the
    // imprint computes nothing; it only records a curve the face already carries.
    const outerSheetId = idGenerator();
    opCreateBSplineSurface(context, outerSheetId, {
                "bSplineSurface" : surfaceToCreate,
                "boundaryBSplineCurves" : extraction.outerLoop
            });

    // Captured BEFORE the imprint: these stay attributed to the sheet's own creation, while the
    // imprinted edges will belong to the split. That difference is what identifies the hole later.
    const outerBoundaryEdges = qCreatedBy(outerSheetId, EntityType.EDGE);
    const sheetBody = qCreatedBy(outerSheetId, EntityType.BODY);

    var patchBodies = [];
    for (var loopIndex = 0; loopIndex < size(extraction.innerLoops); loopIndex += 1)
    {
        const patchId = idGenerator();
        try
        {
            opCreateBSplineSurface(context, patchId, {
                        "bSplineSurface" : surfaceToCreate,
                        "boundaryBSplineCurves" : extraction.innerLoops[loopIndex]
                    });
            patchBodies = append(patchBodies, qCreatedBy(patchId, EntityType.BODY));
            opSplitFace(context, idGenerator(), {
                        "faceTargets" : qOwnedByBody(sheetBody, EntityType.FACE),
                        "edgeTools" : qCreatedBy(patchId, EntityType.EDGE)
                    });
        }
        catch (holeError)
        {
            reportFeatureWarning(context, id, "Hole " ~ loopIndex ~ " could not be imprinted: " ~ holeError);
        }
    }

    if (size(patchBodies) > 0)
    {
        opDeleteBodies(context, idGenerator(), { "entities" : qUnion(patchBodies) });
    }

    // The face to keep is the one still carrying the sheet's ORIGINAL outer boundary; everything
    // else on the body is enclosed by an imprinted loop and is a hole. This is a topological test,
    // not a geometric guess — no point-in-polygon, no area comparison, and it holds for a hole of
    // any shape, convex or not.
    const keptFace = qIntersection(qOwnedByBody(sheetBody, EntityType.FACE),
        qAdjacent(outerBoundaryEdges, AdjacencyType.EDGE, EntityType.FACE));
    const holeFaces = qSubtraction(qOwnedByBody(sheetBody, EntityType.FACE), keptFace);

    if (!isQueryEmpty(context, holeFaces))
    {
        try
        {
            opDeleteFace(context, idGenerator(), {
                        "deleteFaces" : holeFaces,
                        "includeFillet" : false,
                        "capVoid" : false,
                        "leaveOpen" : true
                    });
            reportFeatureInfo(context, id, "TRIM PROBE: holes made by imprint + delete face — " ~
                size(evaluateQuery(context, holeFaces)) ~ " enclosed face(s) removed, no boolean used.");
        }
        catch (deleteError)
        {
            reportFeatureWarning(context, id, "Enclosed faces were imprinted but could not be deleted: " ~ deleteError);
        }
    }
    else
    {
        // If this ever fires, opSplitFace's documented remedy is `extendToCompletion : true`, which
        // extends imprinted edges until they complete the split. Not enabled pre-emptively: its
        // effect on an already-closed inner loop is unverified, and adding an untested flag is the
        // same error as assuming an op cannot do something, pointed the other way.
        reportFeatureWarning(context, id, "No enclosed face appeared after imprinting the inner loops, so no hole " ~
            "was made. The imprint likely did not split the face — check that the inner loop edges lie on it.");
    }
}

/**
 * Swap the target face's underlying surface for the one just created. The target keeps its trim
 * loops, holes and inner loops because opReplaceFace never rebuilds the topology — it substitutes
 * the surface beneath loops that already exist.
 *
 * WHY NOT opCreateBSplineSurface's `boundaryBSplineCurves`, which looks like the obvious answer: it
 * is documented as taking a boundary that "must form a single closed loop on the surface". One
 * loop. There is no inner-loop parameter, so a face with a hole cannot be expressed that way at all
 * — a prototype that passes trim curves reproduces perimeters and silently drops holes, which is
 * the documented ceiling of that call rather than a bug in the prototype. deformPascoe.fs hit the
 * same wall and said so at its line 5176.
 *
 * A second, subtler reason to avoid the trim-curve route even where it would work: those curves are
 * 2D splines in the surface's PARAMETER space, and composing one with the surface map does not
 * produce a low-degree B-spline — the exact composition is a much higher-degree spline. Any 3D trim
 * curve built from them by sampling is therefore an approximation.
 *
 * WHY THE TARGET MUST BE A FACE ON A REAL BODY, not an extracted copy of one. An earlier version
 * served NEW_BODY by opExtractSurface-ing a trimmed copy of the face and replacing the surface
 * underneath THAT, so one mechanism could cover both modes. The extraction half works — it does
 * carry holes over — but the replace fails, in both senses, with DIRECT_EDIT_REPLACE_FACE_FAILED.
 * The reason is structural: opReplaceFace is a DIRECT EDIT, and it re-derives the replaced face's
 * boundary by re-intersecting with the faces around it. A single-face sheet body's edges are all
 * FREE — no neighbours to intersect against, nothing to heal the boundary with. Not a tuning
 * problem; do not retry it.
 *
 * Every detail below is displacementMap.fs's, reused verbatim rather than re-derived, because each
 * one cost a real bug there:
 *
 * - Try one orientation, then the other. opReplaceFace cares which way the template's normal
 *   points and there is no cheap way to know in advance.
 * - PLAIN try/catch, NEVER `try silent`. `try silent` swallows the first failure WITHOUT running
 *   the catch, so the opposite-sense retry never fires and the template gets deleted anyway —
 *   which presents to the user as "replace face silently does nothing".
 * - A DISTINCT id for the retry, which the generator now guarantees. A thrown op still registers
 *   its id, so reusing the first one fails for a second, unrelated reason and buries the first.
 * - Delete the template only if the replace actually happened, so a failure leaves something
 *   visible to diagnose instead of an unchanged part and no explanation.
 */
function replaceTargetFace(context is Context, id is Id, idGenerator is function, face is Query, templateId is Id)
{
    const templateFace = qCreatedBy(templateId, EntityType.FACE);

    // COMPUTE the sense; do not guess it by retrying. std replaceFace.fs calls opReplaceFace ONCE
    // and derives oppositeSense in its editing logic from the sign of
    // dot(targetNormal, templateNormal), each read by evFaceTangentPlane at the face's parameter
    // midpoint. Multi-face targets AND multi-face templates are both natively supported there, so
    // replacing a whole strip with one surface needs no per-face loop — an earlier version here
    // invented one for a limitation the standard feature disproves.
    var oppositeSense = false;
    const targetPlane = try(evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) }));
    const templatePlane = try(evFaceTangentPlane(context, { "face" : templateFace, "parameter" : vector(0.5, 0.5) }));
    if (targetPlane != undefined && templatePlane != undefined)
    {
        oppositeSense = dot(targetPlane.normal, templatePlane.normal) < 0;
    }

    // ONLY the replace is inside the try. An earlier version also deleted the template here, so a
    // delete that threw — the template already consumed, the query already empty — was caught as a
    // REPLACE failure, sending it into the flipped retry, which then failed because the replace had
    // in fact already succeeded. The feature reported failure on work that had worked. Deciding
    // success from the wrong operation's exception is the bug; the fix is to scope the try to the
    // operation whose success is being decided.
    var replaced = false;
    try
    {
        opReplaceFace(context, idGenerator(), {
                    "replaceFaces" : face,
                    "templateFace" : templateFace,
                    "oppositeSense" : oppositeSense
                });
        replaced = true;
    }
    catch (firstError)
    {
        // One flipped retry, for the case where the normal comparison was degenerate rather than
        // wrong — a merged strip whose midpoint normal is not representative of the whole. This is
        // a fallback for an unreadable measurement, not a substitute for taking one.
        try
        {
            opReplaceFace(context, idGenerator(), {
                        "replaceFaces" : face,
                        "templateFace" : templateFace,
                        "oppositeSense" : !oppositeSense
                    });
            replaced = true;
        }
        catch (secondError)
        {
            reportFeatureWarning(context, id, "Replace face failed with the computed sense (" ~ oppositeSense ~
                ") and with it flipped; the rebuilt surface has been left in place so it can be inspected. " ~
                "Computed: " ~ firstError ~ " Flipped: " ~ secondError);
        }
    }

    // Tidying up the template is not part of deciding whether the replace worked, so its own
    // failure is silent — there is nothing for a user to do about it and nothing wrong with the
    // result if the body is already gone.
    if (replaced)
    {
        try silent
        {
            opDeleteBodies(context, idGenerator(), { "entities" : qCreatedBy(templateId, EntityType.BODY) });
        }
    }
}

//==================================================================
//=========================== Utilities ============================
//==================================================================

/**
 * The Greville abscissa of a control point in one direction — the parameter at which that control
 * point has the most influence, and therefore the parameter to evaluate a frame at. Same formula
 * editCurve.fs uses, with its observation intact: the knot array already carries the periodic
 * padding, so no special periodic handling is needed to COMPUTE it.
 *
 * It does need handling to USE it, though, which editCurve never had to do: for a periodic
 * direction the first few control points' Greville abscissae legitimately land BELOW the domain
 * start, out in the padding. Wrapping them forward by one period puts them back on the surface at
 * the geometrically identical location.
 */
function grevilleParameterInDomain(knots is array, degree is number, controlPointIndex is number, isPeriodic is boolean) returns number
{
    var sum = 0;
    for (var j = 1; j <= degree; j += 1)
    {
        sum += knots[controlPointIndex + j];
    }
    var parameter = sum / degree;

    const domain = knotDomain(knots, degree);
    if (isPeriodic)
    {
        const period = domain.end - domain.start;
        while (parameter < domain.start)
        {
            parameter += period;
        }
        while (parameter >= domain.end)
        {
            parameter -= period;
        }
        return parameter;
    }
    return min(max(parameter, domain.start), domain.end);
}

/**
 * The surface's own frame at a control point: the two isoparametric tangents and the normal, from
 * the module's exact derivative evaluation. Throws where the surface is degenerate (a cone apex or
 * pole), which the caller turns into a warning and a fall back to XYZ mode.
 */
function surfaceFrameAtControlPoint(surface is map, uIndex is number, vIndex is number) returns map
{
    const uParameter = grevilleParameterInDomain(surface.uKnots, surface.uDegree, uIndex, surface.isUPeriodic == true);
    const vParameter = grevilleParameterInDomain(surface.vKnots, surface.vDegree, vIndex, surface.isVPeriodic == true);
    const derivatives = evaluateBSplineSurfaceDerivatives(surface, uParameter, vParameter, 1, 1);
    const uTangent = derivatives[1][0];
    const vTangent = derivatives[0][1];
    const crossProduct = cross(uTangent, vTangent);
    if (squaredNorm(crossProduct) <= 1e-20 * squaredNorm(uTangent) * squaredNorm(vTangent))
    {
        throw "Degenerate surface frame";
    }
    return {
            "uDirection" : normalize(uTangent),
            "vDirection" : normalize(vTangent),
            "normal" : normalize(crossProduct)
        };
}

/**
 * Draw the control net, the surface analog of editCurve.fs's showPolyline: every net edge in both
 * directions, over the EDITABLE points only so a periodic net does not draw its padding twice.
 *
 * U net lines are MAGENTA and V lines are CYAN, matching how Onshape colours U and V isoparametric
 * curves elsewhere in the product. Colour is the only cue distinguishing the two directions on a
 * net, and a user who has learned it from surfacing tools should not have to relearn it here.
 */
function showControlNet(context is Context, surface is map)
{
    const counts = fundamentalControlPointCounts(surface);
    const closeU = surface.isUPeriodic == true;
    const closeV = surface.isVPeriodic == true;

    for (var uIndex = 0; uIndex < counts.u; uIndex += 1)
    {
        for (var vIndex = 0; vIndex < counts.v; vIndex += 1)
        {
            const point = surface.controlPoints[uIndex][vIndex];
            if (uIndex + 1 < counts.u || closeU)
            {
                const next = surface.controlPoints[(uIndex + 1) % counts.u][vIndex];
                if (!tolerantEquals(point, next))
                {
                    addDebugLine(context, point, next, DebugColor.MAGENTA); // along U
                }
            }
            if (vIndex + 1 < counts.v || closeV)
            {
                const next = surface.controlPoints[uIndex][(vIndex + 1) % counts.v];
                if (!tolerantEquals(point, next))
                {
                    addDebugLine(context, point, next, DebugColor.CYAN); // along V
                }
            }
        }
    }
}

/** Populate the read-only "Show details" fields. Mirrors editCurve.fs's updateCurveData. Counts are
    the EDITABLE ones, so they match the number of handles on screen rather than the stored arrays'
    padded size. */
function updateSurfaceData(context is Context, id is Id, surface is map)
{
    const counts = fundamentalControlPointCounts(surface);
    setFeatureComputedParameter(context, id, { "name" : "surfaceUDegree", "value" : surface.uDegree });
    setFeatureComputedParameter(context, id, { "name" : "surfaceVDegree", "value" : surface.vDegree });
    setFeatureComputedParameter(context, id, { "name" : "surfaceNumUCPs", "value" : counts.u });
    setFeatureComputedParameter(context, id, { "name" : "surfaceNumVCPs", "value" : counts.v });
}

/** Mirrors editCurve.fs's indexIsValid, for a (u, v) pair. */
function indexIsValid(context is Context, id is Id, uIndex is number, vIndex is number, surface is map) returns boolean
{
    const counts = fundamentalControlPointCounts(surface);
    if (uIndex < 0 || uIndex > counts.u - 1 || vIndex < 0 || vIndex > counts.v - 1)
    {
        reportFeatureWarning(context, id, "Control point index is out of range; this net has " ~ counts.u ~ " x " ~
            counts.v ~ " editable control points.", ["selectedUIndex", "selectedVIndex"]);
        return false;
    }
    return true;
}

/** Mirrors editCurve.fs's indicesAreValid, for (u, v) pairs. */
function indicesAreValid(context is Context, id is Id, indices is array, surface is map) returns boolean
{
    const counts = fundamentalControlPointCounts(surface);
    for (var index in indices)
    {
        if (index.uIndexValue < 0 || index.uIndexValue > counts.u - 1 ||
            index.vIndexValue < 0 || index.vIndexValue > counts.v - 1)
        {
            reportFeatureWarning(context, id, "Control point index is out of range; this net has " ~ counts.u ~ " x " ~
                counts.v ~ " editable control points.", ["selectedIndices"]);
            return false;
        }
    }
    return true;
}
