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
import(path : "onshape/std/coordSystem.fs", version : "3044.0");   // WORLD_COORD_SYSTEM, toWorld, fromWorld
import(path : "onshape/std/transform.fs", version : "3044.0");
import(path : "onshape/std/matrix.fs", version : "3044.0");
import(path : "onshape/std/math.fs", version : "3044.0");
import(path : "onshape/std/debug.fs", version : "3044.0");
import(path : "onshape/std/approximationUtils.fs", version : "3044.0"); // MAX_DEGREE
import(path : "onshape/std/surfacetype.gen.fs", version : "3044.0");

// splineRefinementUtils.fs, imported as a SAME-DOCUMENT tab (bare tab id) exactly as
// editSurface.fs and splineRefinementTester.fs do, rather than as the separately published
// artifact the two tween features pin. Deliberate while this feature is co-developed with the
// module: module edits land here immediately, with no republish and no version-bump cascade. It
// becomes a cross-document import once this stops changing in lockstep with the module. The
// pinned cross-document form, for whenever that day comes:
// import(path : "eca0e7b6ed29c5239f39f868/36185a3777394c9ecb0bfc3e/9a2b77793cdc37bace6d915a", version : "db7f981bb900fa1e8c01effc");
import(path : "9a2b77793cdc37bace6d915a", version : "6b2f05959547b5e81628762a");

/*
 * FREE-FORM DEFORMATION — Sederberg & Parry's trivariate Bernstein lattice, rebuilt on the exact
 * refinement module. Full design: docs/specs/FREE_FORM_DEFORMATION_SPEC.md.
 *
 * This file REPLACES two earlier features that have been folded into it:
 *   - freeFormDeformation.fs        (point lattice, one point selectable at a time)
 *   - freeFormDeformationPlanes.fs  (a lattice degenerated to 1 span in two directions, so that
 *                                    whole cross-sectional "planes" could be dragged and rotated)
 *
 * They were two features only because of what the manipulator vocabulary could express when they
 * were written. Multi-select over control points — togglePointsManipulator, the same mechanism
 * editSurface.fs and routingCurve.fs use — collapses the distinction: a "plane" is a SELECTION of
 * lattice points, not a different kind of lattice. Selection scope (below) turns one click into a
 * point, a row, or a whole constant-index plane, and the full triad then translates and rotates
 * whatever is selected. Everything the planes feature could do is a scope choice here, on a lattice
 * that is no longer forced to 1 span in two directions.
 *
 * NAMING: the lattice axes are U, V and N, not Sederberg & Parry's S, T, U. The rest of this
 * repository's parameter-space work (editSurface.fs, splineRefinementUtils.fs, the tween features)
 * settled on UVN, and STU only ever existed because the 1986 paper needed three letters. The
 * collision this creates is real and is handled by NAMING, not by hoping: the deformed SURFACE also
 * has u and v parameters, so everything belonging to the lattice says so — `latticeSpanCountU`,
 * `latticeFlatIndex`, "lattice U" in the dialog — and everything belonging to a surface
 * stays `uDegree`, `uKnots`, `uParameter`. Where both appear in one function the comment says which
 * is which.
 *
 * COLOUR: lattice edges are drawn MAGENTA along U, CYAN along V, YELLOW along N. Magenta/cyan is
 * Onshape's own U/V isoparametric colouring, which editSurface.fs already follows for its control
 * net. Onshape has no third direction to be consistent with — a normal direction has no analog in
 * a surface parameter space — so yellow is this repository's own convention, chosen to sit in the
 * same subtractive-primary family as the other two rather than to collide with the red used for
 * error highlighting or the blue/green used by debug geometry elsewhere.
 *
 * WHAT ACTUALLY CHANGED, beyond the merge (all of it from
 * docs/specs/SPLINE_REFINEMENT_UTILITY_SPEC.md section 9.2):
 *
 *   1. THE SURFACE IS REFINED BEFORE IT IS DEFORMED. Both old features rebuilt the surface with
 *      the ORIGINAL knot vectors and the ORIGINAL control point count, which is the classic FFD
 *      ceiling: the deformation can only ever be as detailed as the net it was handed. A 4x4 Bezier
 *      patch in a 6x6x6 lattice has 16 degrees of freedom to express a field sampled far more
 *      finely, so most of the lattice did nothing and cranking its resolution up stopped helping.
 *      Knot refinement is the exact fix — the undeformed surface is bit-for-bit unchanged and gains
 *      the freedom to represent the field.
 *
 *   2. THE REFINEMENT LEVEL IS MEASURED, NOT GUESSED. Control-net deformation is exact only for an
 *      AFFINE map (spec section 3.2) and the trivariate Bernstein map is affine only in the
 *      degenerate case where no lattice point has been moved off its grid position. So refinement
 *      is what makes this CONVERGE, not merely what gives it more handles, and the feature runs the
 *      spec's section 9.1.1 loop: refine, deform, compare control nets for free, and certify the
 *      winner against the kernel with evPointsDeviation. The certified number is reported. Neither
 *      old feature could answer "how wrong is this?" at all.
 *
 *   3. DEGREE IS CHOSEN BY INTENT, NEVER BY TOLERANCE. A position tolerance cannot detect that
 *      degree 1 is wrong: refining a degree-1 surface converges in position while staying C0
 *      forever. Elevation happens once, up front, to the continuity the user asked for.
 *
 * WHAT IS DELIBERATELY NOT HERE: exact composition. FFD is the one deformation map in the family
 * that is POLYNOMIAL, so composing it with a Bezier patch is exactly representable — a bidegree
 * (p, q) patch through a tridegree (l, m, n) lattice is a bidegree (Dp, Dq) patch with
 * D = l + m + n. That is a genuine exact route with no tolerance anywhere, and it is unusable in
 * general: a bicubic patch through the smallest interesting lattice (2x2x2, D = 6) lands at degree
 * 18, past std's MAX_DEGREE of 15, and it climbs from there. It stays in the spec as the reason the
 * control point ceiling exists rather than as code.
 */

//==================================================================
//======================= Bounds and constants =====================
//==================================================================

/**
 * Spans per lattice direction. One span is the degenerate "no control in this direction" case and
 * is legal — it is exactly what the old planes feature forced on the two directions it was not
 * manipulating — so the floor is 1, not 2.
 *
 * The ceiling of 8 is a UI limit, not a mathematical one. Nine control points per direction is 729
 * clickable handles at 8x8x8, which is already past what anyone can pick through; the cost of the
 * lattice itself is trivial next to the surface refinement it provokes.
 */
export const LATTICE_SPAN_COUNT_BOUNDS =
{
            (unitless) : [1, 2, 8]
        } as IntegerBoundSpec;

/** Lattice point index, per direction. Bounded by the largest lattice the span bound allows. */
export const LATTICE_INDEX_BOUND =
{
            (unitless) : [0, 0, 8]
        } as IntegerBoundSpec;

/**
 * Ceiling on control points per surface direction after refinement.
 *
 * This is the budget the section 9.1.1 loop spends, and it exists because the loop's only move is
 * to refine UPWARD: nothing ever gives control points back. Refinement can legitimately push a
 * direction well past std's MAX_CONTROL_POINTS (100), which caps APPROXIMATION inputs rather than
 * what an exact refinement may produce, so this carries its own ceiling — the same reasoning
 * editSurface.fs's MAX_SURFACE_CONTROL_POINTS uses.
 */
export const REFINEMENT_BUDGET_BOUND =
{
            (unitless) : [4, 60, 400]
        } as IntegerBoundSpec;

/** How many times the section 9.1.1 loop may double a direction's span count before it gives up and
    reports what it actually achieved. Six doublings is a 64x refinement, far past where a lattice
    deformation stops converging for any other reason. */
export const REFINEMENT_ITERATION_BOUND =
{
            (unitless) : [1, 4, 8]
        } as IntegerBoundSpec;

/** Certification samples per surface direction. Every sample is a map evaluation plus a kernel
    closest-point projection, so this is a real cost that has to be bounded; 20x20 lands 400 probes
    at knot span midpoints, which is where the free control-net comparison is blind. */
const MAXIMUM_CERTIFICATION_SAMPLES_PER_DIRECTION = 20;

/** Automatic tolerance is the bounding box diagonal times this, per spec section 9.1.1's closing
    note, so the default behaves the same on a 5 mm bracket and a 5 m hull. */
const AUTOMATIC_TOLERANCE_FRACTION = 1e-4;

/**
 * A lattice direction whose bounding extent is below this fraction of the box diagonal is inflated
 * symmetrically to it.
 *
 * Without this a planar face gives a lattice with zero thickness, and the old code papered over the
 * resulting division by zero with a volume epsilon — which kept it from throwing but left every
 * control point pinned at parameter 0 in the flat direction, so pulling a flat face out of plane,
 * the single most obvious thing to want from FFD, did nothing. Inflating gives the flat direction a
 * real extent and parks the surface at its mid-parameter, where both boundary layers of lattice
 * points can pull on it.
 */
const DEGENERATE_DIRECTION_INFLATION = 0.1;

const LATTICE_POINTS_MANIPULATOR = "latticePointsManipulator";
const LATTICE_TRANSFORM_MANIPULATOR = "latticeTransformManipulator";

/**
 * The selection rotation is stored as a FLAT nine-value array, row by row, never as a Matrix.
 *
 * A precondition cannot hold a Transform, and `isAnything` inside an array parameter cannot hold a
 * nested array, so the flat form is the only one that round-trips — the same conclusion
 * routingCurve.fs and the old planes feature both reached. Keeping the identity in the same shape
 * means the default, the reset and the comparison all read the same data rather than one of them
 * quietly holding a 3x3.
 */
const IDENTITY_ROTATION = [1, 0, 0, 0, 1, 0, 0, 0, 1];

//==================================================================
//============================== Enums =============================
//==================================================================

/**
 * What one click on a lattice point selects.
 *
 * This enum is where freeFormDeformationPlanes.fs went. That feature existed to move a whole
 * cross-section at once, and bought it by forcing the lattice to 1 span in the two directions it
 * was not manipulating — so the "plane" was always a 2x2 grid of four points and the lattice could
 * never be a lattice. Here a plane is a SELECTION, the lattice keeps whatever spans the user asked
 * for in all three directions, and the same triad drives it.
 *
 * A ROW fixes two indices and varies the third. A PLANE fixes one index and varies the other two —
 * so PLANE_AT_N is a constant-N slab, spanned by U and V, and is the direct replacement for the old
 * feature's "U direction" (its Z-axis) plane set.
 */
export enum FFDSelectionScope
{
    annotation { "Name" : "Point" }
    POINT,
    annotation { "Name" : "Row along U" }
    ROW_ALONG_U,
    annotation { "Name" : "Row along V" }
    ROW_ALONG_V,
    annotation { "Name" : "Row along N" }
    ROW_ALONG_N,
    annotation { "Name" : "Plane at constant U" }
    PLANE_AT_U,
    annotation { "Name" : "Plane at constant V" }
    PLANE_AT_V,
    annotation { "Name" : "Plane at constant N" }
    PLANE_AT_N
}

/**
 * The continuity the deformed result is required to have, which fixes the degree it is elevated to
 * before any refinement happens. Spec section 9.1.1's table, verbatim.
 *
 * This is NOT a tolerance question and must not share a knob with one. With simple knots a degree-p
 * spline is C^(p-1), so a tolerance-only loop on a degree-1 input will happily converge in position
 * while remaining creased at every knot line — a finely faceted result that satisfies every numeric
 * check and looks like garbage.
 */
export enum FFDContinuity
{
    annotation { "Name" : "Curvature continuous (degree 3)" }
    CURVATURE,
    annotation { "Name" : "Tangent continuous (degree 2)" }
    TANGENT,
    annotation { "Name" : "Inherit from the face" }
    INHERIT
}

/**
 * Where the deformed surface goes.
 *
 * NEW_BODY is the default and is what both old features did: the full untrimmed B-spline surface
 * the deformed control net describes.
 *
 * REPLACE_FACE swaps the surface underneath the original face, which carries its perimeter, holes
 * and inner loops across for free. It is right for ONE face and is deliberately warned about for
 * several, because opReplaceFace re-derives the replaced face's boundary by intersecting with its
 * neighbours — and for a non-affine map, which every interesting FFD is, the intersection of two
 * deformed faces is NOT the deformation of their original intersection. Replacing a strip of
 * adjacent faces one at a time therefore trims each against neighbours that have not moved yet.
 * Spec section 9.1 works this through; the fix is per-face trimmed sheets plus a knit, which is what
 * NEW_BODY_TRIMMED provides.
 *
 * NEW_BODY_TRIMMED carries the face's own perimeter and holes onto a NEW body instead of replacing
 * anything, which is what makes it the gating step toward deforming a whole solid: deform every face
 * of a shell to its own trimmed sheet, then knit. REPLACE_FACE cannot get there because it retrims
 * against neighbours that have not moved yet; per-face trimmed sheets have no such coupling, because
 * each one's boundary comes from its OWN parameter space and never from an intersection.
 */
export enum FFDResult
{
    annotation { "Name" : "New surface body (untrimmed)" }
    NEW_BODY,
    annotation { "Name" : "New surface body (trimmed) [experimental]" }
    NEW_BODY_TRIMMED,
    annotation { "Name" : "Replace face" }
    REPLACE_FACE
}

//==================================================================
//========================= Feature definition =====================
//==================================================================

annotation { "Feature Type Name" : "Free-form deformation",
        "Feature Type Description" : "Deforms surfaces through a shared trivariate Bernstein control lattice, refining each surface first so the deformation converges.",
        "UIHint" : "NO_PREVIEW_PROVIDED",
        "Editing Logic Function" : "freeFormDeformationEditLogic",
        "Manipulator Change Function" : "onFreeFormDeformationManipulatorChange" }
export const freeFormDeformation = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Surfaces to deform",
                     "Filter" : EntityType.FACE && SketchObject.NO && ConstructionObject.NO && AllowMeshGeometry.NO }
        definition.surfacesToDeform is Query;

        // Same standing rule as editSurface.fs and the refinement module: a face that is already a
        // SPLINE is read exactly, and representing anything else as a B-spline is an approximation
        // the user opts into rather than one that happens silently. Expect to need it more often
        // than seems reasonable — surfaceType reports how the kernel STORES a surface, so an
        // extruded or revolved spline profile comes back EXTRUDED or REVOLVED even though it is a
        // B-spline surface mathematically, and those cases approximate essentially perfectly.
        annotation { "Name" : "Approximate non-spline faces" }
        definition.approximate is boolean;

        annotation { "Group Name" : "Lattice", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Lattice spans in U", "Description" : "Spans along the lattice's first axis" }
            isInteger(definition.latticeSpanCountU, LATTICE_SPAN_COUNT_BOUNDS);

            annotation { "Name" : "Lattice spans in V", "Description" : "Spans along the lattice's second axis" }
            isInteger(definition.latticeSpanCountV, LATTICE_SPAN_COUNT_BOUNDS);

            annotation { "Name" : "Lattice spans in N", "Description" : "Spans along the lattice's third axis" }
            isInteger(definition.latticeSpanCountN, LATTICE_SPAN_COUNT_BOUNDS);

            annotation { "Name" : "Orient lattice to a mate connector" }
            definition.orientLattice is boolean;

            if (definition.orientLattice)
            {
                annotation { "Name" : "Lattice orientation",
                             "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
                definition.latticeOrientation is Query;
            }
        }

        annotation { "Name" : "Edit lattice" }
        definition.editLattice is boolean;
        annotation { "Group Name" : "Edit lattice", "Driving Parameter" : "editLattice", "Collapsed By Default" : false }
        {
            if (definition.editLattice)
            {
                annotation { "Name" : "Selection scope" }
                definition.selectionScope is FFDSelectionScope;

                annotation { "Name" : "Selected lattice points", "Item name" : "point",
                             "Item label template" : "Lattice point (#uIndexValue, #vIndexValue, #nIndexValue)",
                             "Show labels only" : true,
                             "UIHint" : [UIHint.INITIAL_FOCUS, UIHint.PREVENT_ARRAY_REORDER, UIHint.ALLOW_ARRAY_FOCUS] }
                definition.selectedIndices is array;
                for (var selectedIndex in definition.selectedIndices)
                {
                    annotation { "Name" : "U index" }
                    isInteger(selectedIndex.uIndexValue, LATTICE_INDEX_BOUND);

                    annotation { "Name" : "V index" }
                    isInteger(selectedIndex.vIndexValue, LATTICE_INDEX_BOUND);

                    annotation { "Name" : "N index" }
                    isInteger(selectedIndex.nIndexValue, LATTICE_INDEX_BOUND);
                }

                // A REAL button — `isButton` is satisfied by the value staying undefined, which is
                // why `planarizeSelection` is absent from the defaults map below. Pressing it
                // reaches freeFormDeformationEditLogic as its `clickedButton` argument.
                annotation { "Name" : "Planarize selection",
                             "Description" : "Move every selected lattice point onto the least-squares plane through them. Acts once, on press, and writes ordinary point offsets: there is no persistent planar constraint afterwards." }
                isButton(definition.planarizeSelection);

                // The cumulative transform of the CURRENT selection, live until the selection
                // changes and then baked into the offsets below. See bakeSelectionTransform for why
                // the two-stage storage exists rather than one or the other alone. Stored
                // decomposed because a Transform is not a precondition type: a flat nine-value
                // rotation behind isAnything (nested arrays are illegal inside an array parameter)
                // plus three lengths, exactly the routingCurve.fs pattern.
                annotation { "Name" : "Selection rotation", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isAnything(definition.selectionRotation);

                annotation { "Name" : "Selection translation X", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isLength(definition.selectionTranslateX, ZERO_DEFAULT_LENGTH_BOUNDS);
                annotation { "Name" : "Selection translation Y", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isLength(definition.selectionTranslateY, ZERO_DEFAULT_LENGTH_BOUNDS);
                annotation { "Name" : "Selection translation Z", "UIHint" : UIHint.ALWAYS_HIDDEN }
                isLength(definition.selectionTranslateZ, ZERO_DEFAULT_LENGTH_BOUNDS);

                annotation { "Name" : "Lattice point offsets", "Item name" : "offset",
                             "Item label template" : "(#uIndex, #vIndex, #nIndex): #x;#y;#z",
                             "UIHint" : [UIHint.PREVENT_ARRAY_REORDER, UIHint.COLLAPSE_ARRAY_ITEMS] }
                definition.latticePointOffsets is array;
                for (var latticePointOffset in definition.latticePointOffsets)
                {
                    annotation { "Name" : "U index" }
                    isInteger(latticePointOffset.uIndex, LATTICE_INDEX_BOUND);

                    annotation { "Name" : "V index" }
                    isInteger(latticePointOffset.vIndex, LATTICE_INDEX_BOUND);

                    annotation { "Name" : "N index" }
                    isInteger(latticePointOffset.nIndex, LATTICE_INDEX_BOUND);

                    annotation { "Name" : "X offset" }
                    isLength(latticePointOffset.x, ZERO_DEFAULT_LENGTH_BOUNDS);
                    annotation { "Name" : "Y offset" }
                    isLength(latticePointOffset.y, ZERO_DEFAULT_LENGTH_BOUNDS);
                    annotation { "Name" : "Z offset" }
                    isLength(latticePointOffset.z, ZERO_DEFAULT_LENGTH_BOUNDS);
                }
            }
        }

        annotation { "Group Name" : "Accuracy", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Continuity", "Description" : "The degree the surface is elevated to before it is refined" }
            definition.continuity is FFDContinuity;

            annotation { "Name" : "Automatic tolerance",
                         "Description" : "Bounding box diagonal x 1e-4, so the default behaves the same at any scale" }
            definition.automaticTolerance is boolean;

            if (!definition.automaticTolerance)
            {
                annotation { "Name" : "Deviation tolerance" }
                isLength(definition.deviationTolerance, TOLERANCE_BOUND);
            }

            annotation { "Name" : "Maximum control points per direction" }
            isInteger(definition.refinementBudget, REFINEMENT_BUDGET_BOUND);

            annotation { "Name" : "Maximum refinement passes" }
            isInteger(definition.refinementIterations, REFINEMENT_ITERATION_BOUND);
        }

        annotation { "Name" : "Show lattice", "Default" : true }
        definition.showLattice is boolean;

        annotation { "Name" : "Result", "Default" : FFDResult.NEW_BODY }
        definition.result is FFDResult;

        annotation { "Name" : "Enable diagnostics" }
        definition.enableDiagnostics is boolean;
        annotation { "Group Name" : "Developer diagnostics", "Driving Parameter" : "enableDiagnostics",
                     "Collapsed By Default" : true }
        {
            if (definition.enableDiagnostics)
            {
                annotation { "Name" : "Show deformed control net" }
                definition.showControlNet is boolean;

                annotation { "Name" : "Print lattice information" }
                definition.printLatticeInfo is boolean;

                annotation { "Name" : "Print refinement details" }
                definition.printRefinementDetails is boolean;
            }
        }
    }
    {
        if (isQueryEmpty(context, definition.surfacesToDeform))
        {
            throw regenError("Select at least one surface to deform.", ["surfacesToDeform"]);
        }

        const facesToDeform = evaluateQuery(context, definition.surfacesToDeform);

        // Step 1 of the spec section 9.1 pipeline: read every face's B-spline definition, exactly
        // when the kernel already holds one. Every face is read BEFORE the lattice is built because
        // the lattice bounds all of them at once — that shared volume is the whole reason several
        // faces deform coherently rather than independently.
        const sourceSurfaces = readSourceSurfaces(context, definition, facesToDeform);
        const lattice = buildLattice(context, id, definition, sourceSurfaces);

        showLatticeManipulators(context, id, definition, lattice);
        if (definition.showLattice)
        {
            showLatticeCage(context, lattice);
        }
        if (definition.printLatticeInfo)
        {
            printLatticeInformation(lattice);
        }

        if (definition.result == FFDResult.REPLACE_FACE && size(facesToDeform) > 1)
        {
            reportFeatureWarning(context, id, "Replace face is being applied to " ~ size(facesToDeform) ~
                " faces one at a time. A lattice deformation is non-affine, so each face is retrimmed against " ~
                "neighbours that have not been deformed yet and the shared edges will not agree. Use it on a single " ~
                "face, or switch to \"New surface body (trimmed)\", where each face carries its own boundary from " ~
                "its own parameter space and the sheets can be knitted afterwards.", ["result"]);
        }

        // ONE id generator for every operation, across every face. Hand-naming operation ids is a
        // bug waiting to happen here in particular, because replaceTargetFace may perform one, two
        // or three operations depending on which sense the kernel accepts — and a thrown op STILL
        // REGISTERS ITS ID, so any retry reusing a name fails with "Duplicate id in context" and
        // masks the original error. The DISAMBIGUATED variant, so downstream references survive a
        // regeneration in which that count changed and therefore the operation ORDER did too; each
        // face disambiguates on itself, which is what keeps a multi-face selection's bodies
        // individually identifiable.
        const disambiguatedId = getDisambiguatedIncrementingId(context, id + "op");

        const tolerance = deformationTolerance(definition, lattice);
        // Compiled ONCE for every face and every refinement level, which is the point of it — see
        // compileLatticeForDeformation. Built after the manipulators and the cage, so it captures
        // the lattice in exactly the state those drew.
        const compiledLattice = compileLatticeForDeformation(lattice);
        var worstCertifiedDeviation = 0 * meter;
        var worstControlPointCount = 0;
        var anyBudgetHit = false;
        var anyTrimDropped = false;
        var anyElevationBlocked = false;
        var anyUniformized = false;

        for (var faceIndex = 0; faceIndex < size(sourceSurfaces); faceIndex += 1)
        {
            const face = facesToDeform[faceIndex];
            const idGenerator = function()
                {
                    return disambiguatedId(face);
                };

            const outcome = deformOneFace(context, id, idGenerator, definition, compiledLattice,
                sourceSurfaces[faceIndex], face, tolerance);
            anyTrimDropped = anyTrimDropped || outcome.trimDropped;
            anyElevationBlocked = anyElevationBlocked || outcome.elevationBlocked;
            anyUniformized = anyUniformized || outcome.uniformized;

            worstCertifiedDeviation = max(worstCertifiedDeviation, outcome.certifiedDeviation);
            worstControlPointCount = max(worstControlPointCount, outcome.controlPointCount);
            anyBudgetHit = anyBudgetHit || outcome.budgetHit;
        }

        // One report for the whole feature rather than one per face. The certified deviation is the
        // number that makes this trustworthy and is the single thing neither old FFD could produce.
        reportFeatureInfo(context, id, "Deformed " ~ size(sourceSurfaces) ~ " surface(s) through a " ~
            lattice.spanCountU ~ "x" ~ lattice.spanCountV ~ "x" ~ lattice.spanCountN ~ " lattice. Refined to at " ~
            "most " ~ worstControlPointCount ~ " control points per direction; certified worst deviation from the " ~
            "true deformed surface: " ~ toString(worstCertifiedDeviation) ~ " (tolerance " ~ toString(tolerance) ~ ").");

        if (anyBudgetHit)
        {
            reportFeatureWarning(context, id, "The refinement budget or pass limit was reached before the tolerance " ~
                "was met. The deviation reported above is what was actually achieved, not what was asked for. Raise " ~
                "\"Maximum control points per direction\", raise \"Maximum refinement passes\", or accept it.",
                ["refinementBudget", "refinementIterations"]);
        }

        if (anyUniformized)
        {
            reportFeatureInfo(context, id, "A closed (periodic) direction was re-fitted onto an even, arc-length " ~
                "knot vector before deforming. Two things make that necessary. A revolve arrives as circular arcs " ~
                "joined with only C0 continuity, smooth solely because its control points happen to be arranged for " ~
                "it - and a deformation does not preserve that arrangement, so the kernel would reject the deformed " ~
                "body as not smooth. Those arcs are also far from evenly parameterized, about 1.7 to 1 across each " ~
                "arc, which would leave the control net and the resulting u/v crowded in bands at the arc joins. The " ~
                "re-fit costs a measured deviation, included in the number reported above, and it changes the " ~
                "surface's u/v parameterization by design.");
        }

        if (anyElevationBlocked)
        {
            reportFeatureInfo(context, id, "A closed (periodic) direction was NOT elevated to the requested " ~
                "continuity, and kept its own degree instead. Elevation raises EVERY knot to full multiplicity, " ~
                "which leaves the surface only C0 at every knot line - exactly the state the re-fit above exists to " ~
                "get it out of, and one the kernel refuses once the deformation moves the control points. Elevating " ~
                "after the re-fit would undo it. A closed direction from a revolve is already an exact circle, so " ~
                "nothing is lost geometrically, and refinement still adds all the detail the deformation needs.");
        }

        if (anyTrimDropped)
        {
            reportFeatureWarning(context, id, "At least one face was emitted UNTRIMMED because preparing it changed the " ~
                "parameter space its trim loops were read against. This is the closed (periodic) case, and it has two " ~
                "causes: normalizing a closed direction re-cuts its knot vector, and re-fitting one onto a smoother " ~
                "knot vector shifts the parameter-to-point map by the deviation reported above. The second is not " ~
                "optional - without it the kernel rejects the deformed surface outright. Deform that face with " ~
                "\"Replace face\" instead, which carries the trim topologically and needs no parameter agreement.",
                ["result"]);
        }
    }, { "approximate" : false,
            "latticeSpanCountU" : 2, "latticeSpanCountV" : 2, "latticeSpanCountN" : 2,
            "orientLattice" : false,
            "editLattice" : false,
            "selectionScope" : FFDSelectionScope.POINT,
            "selectedIndices" : [],
            "selectionRotation" : IDENTITY_ROTATION,
            "selectionTranslateX" : 0 * meter, "selectionTranslateY" : 0 * meter, "selectionTranslateZ" : 0 * meter,
            "latticePointOffsets" : [],
            "continuity" : FFDContinuity.CURVATURE,
            "automaticTolerance" : true,
            "deviationTolerance" : 0.1 * millimeter,
            "refinementBudget" : 60,
            "refinementIterations" : 4,
            "showLattice" : true,
            "result" : FFDResult.NEW_BODY,
            "enableDiagnostics" : false,
            "showControlNet" : false,
            "printLatticeInfo" : false,
            "printRefinementDetails" : false });
// `planarizeSelection` is deliberately absent from the defaults above: isButton is satisfied by the
// value being undefined, so giving it a default turns the button into an ordinary parameter.

//==================================================================
//========================= Input processing =======================
//==================================================================

/**
 * Read every selected face as a normalized B-spline surface definition, together with its trim
 * loops when the result mode needs them.
 *
 * Exact for a face the kernel already stores as a SPLINE; an approximation, behind the explicit
 * toggle, for anything else. The error names the type it actually got — "this is an EXTRUDED face"
 * is something a user can reason about, where "not a spline" just reads as a refusal.
 *
 * TRIMMED MODE APPROXIMATES UNCONDITIONALLY, and the standing no-silent-approximation rule is
 * satisfied by saying so rather than by an exception. The reason is the MATCHED PAIR, transcribed
 * from editSurface.fs's readSurfaceAndTrim: the loops are 2D curves in the parameter space of the
 * surface that same call returned, and evSurfaceDefinition returns no loops at all. Pairing loops
 * with a surface from a different call is only sound if the two share a parameterization, which
 * nothing guarantees. Taking both from one call costs an approximation on the exact path and buys a
 * guarantee.
 *
 * The read is NOT conditioned on anything the manipulator path lacks, deliberately. Both the feature
 * body and latticeForManipulatorHandling go through here, and the two must agree: the lattice is
 * bounded by these control points, so reading exactly in one path and approximately in the other
 * would put the manipulator's handles somewhere the regeneration does not put them.
 *
 * @param context {Context}
 * @param definition {map} : the feature definition, for `approximate` and `result`
 * @param faces {array} : the evaluated faces, in selection order
 * @returns {array} : one map per face, in the same order, each `surface` {map} normalized plus
 *                    `outerLoop` {array} and `innerLoops` {array} (empty unless trimmed mode)
 */
function readSourceSurfaces(context is Context, definition is map, faces is array) returns array
{
    const wantTrim = definition.result == FFDResult.NEW_BODY_TRIMMED;
    var surfaces = [];
    for (var face in faces)
    {
        if (wantTrim)
        {
            const approximation = evApproximateBSplineSurface(context, { "face" : face });
            surfaces = append(surfaces, {
                        "surface" : normalizeSurfaceDefinition(approximation.bSplineSurface),
                        "outerLoop" : approximation.boundaryBSplineCurves == undefined ?
                            [] : approximation.boundaryBSplineCurves,
                        "innerLoops" : approximation.innerLoopBSplineCurves == undefined ?
                            [] : approximation.innerLoopBSplineCurves
                    });
            continue;
        }

        var surfaceDefinition = evSurfaceDefinition(context, { "face" : face });
        if (surfaceDefinition.surfaceType != SurfaceType.SPLINE)
        {
            if (!definition.approximate)
            {
                throw regenError("This is a " ~ surfaceDefinition.surfaceType ~
                        " face, which has no B-spline definition to read. Deforming it means approximating it as one " ~
                        "first — turn on \"Approximate non-spline faces\" to allow that. Extruded and revolved spline " ~
                        "profiles land here too and approximate essentially perfectly, because the target genuinely " ~
                        "is a B-spline surface.",
                    ["surfacesToDeform", "approximate"]);
            }
            surfaceDefinition = evApproximateBSplineSurface(context, { "face" : face }).bSplineSurface;
        }
        const normalized = normalizeSurfaceDefinition(surfaceDefinition);
        if (definition.printRefinementDetails == true)
        {
            println("=== FFD surface read ===");
            printSurfaceStructure("as read from the kernel", surfaceDefinition);
            printSurfaceStructure("after normalizeSurfaceDefinition", normalized);
        }
        surfaces = append(surfaces, {
                    "surface" : normalized,
                    "outerLoop" : [],
                    "innerLoops" : []
                });
    }
    return surfaces;
}

/**
 * The tolerance the section 9.1.1 loop drives against.
 *
 * Automatic is the lattice's own diagonal times AUTOMATIC_TOLERANCE_FRACTION, floored at
 * TOLERANCE.zeroLength. Scale-relative rather than absolute, so the same default is sensible on a
 * bracket and on a hull; the lattice diagonal is the right scale because it bounds every surface
 * being deformed by construction.
 *
 * @param definition {map} : the feature definition
 * @param lattice {map} : the built lattice, for its diagonal
 * @returns {ValueWithUnits} : a length
 */
function deformationTolerance(definition is map, lattice is map) returns ValueWithUnits
{
    if (!definition.automaticTolerance)
    {
        return definition.deviationTolerance;
    }
    return max(lattice.diagonal * AUTOMATIC_TOLERANCE_FRACTION, TOLERANCE.zeroLength * meter);
}

/**
 * The degree each direction is elevated to before any refinement, from the user's continuity
 * intent. Spec section 9.1.1: degree answers a question a tolerance cannot.
 *
 * A PERIODIC DIRECTION IS NEVER ELEVATED, and this is a hard constraint rather than a preference.
 *
 * Surface elevation goes through the module's `elevateHomogeneousPointsRaw`, which DELIBERATELY
 * skips the `removeKnots` simplification std's own `elevateBSpline` applies — removability is
 * decided from the actual point values, so two columns with the same knots but different points can
 * simplify to different knot vectors, and every column of a surface must land on ONE shared vector.
 * The documented price is an unminimized knot vector: after elevation every interior knot sits at
 * multiplicity `degree`, the Bezier-decomposed form.
 *
 * That price is affordable for an editor and NOT affordable here. Elevation is exact, so the
 * undeformed surface stays smooth; but the deformation then moves every control point independently
 * through a non-affine map, and a knot at full multiplicity is free to express a crease. On the
 * SEAM of a periodic direction that crease is fatal rather than cosmetic: the kernel checks closure
 * smoothness on emission and refuses the surface outright with
 * PERIODIC_BSPLINESURFACE_NOT_SMOOTH. The original FFD never elevated, which is precisely why it
 * never hit this.
 *
 * Skipping costs almost nothing real. A periodic direction that came from a revolve is a rational
 * conic — exactly circular and already smooth — so elevating it buys no geometric fidelity, and the
 * detail this feature actually needs comes from REFINEMENT, which inserts distinct knots at
 * multiplicity 1 and works correctly on periodic directions. The proper fix is joint knot removal
 * across a periodic surface's shared vector, which the module names as "the next piece, not an
 * impossibility" on `simplifySurfaceToControlPointCounts`; until that exists this is the honest
 * behaviour.
 *
 * @param continuity {FFDContinuity} : the requested continuity
 * @param sourceDegree {number} : the direction's current degree, used by INHERIT
 * @param isPeriodic {boolean} : whether this direction is closed
 * @returns {number} : the degree to elevate to, never below the source degree
 */
function targetDegreeForContinuity(continuity is FFDContinuity, sourceDegree is number,
    isPeriodic is boolean) returns number
{
    if (isPeriodic)
    {
        return sourceDegree;
    }
    if (continuity == FFDContinuity.CURVATURE)
    {
        return max(sourceDegree, 3);
    }
    if (continuity == FFDContinuity.TANGENT)
    {
        return max(sourceDegree, 2);
    }
    return sourceDegree;
}

/** Whether a direction was denied the elevation the user asked for because it is periodic, which is
    worth telling them about rather than silently downgrading. Compares the degree the continuity
    setting WOULD have asked for against what periodicity allowed.

    @param continuity {FFDContinuity}
    @param sourceDegree {number}
    @param isPeriodic {boolean}
    @returns {boolean} */
function periodicBlockedElevation(continuity is FFDContinuity, sourceDegree is number, isPeriodic is boolean) returns boolean
{
    return isPeriodic && targetDegreeForContinuity(continuity, sourceDegree, false) > sourceDegree;
}

//==================================================================
//====================== Lattice construction ======================
//==================================================================

/**
 * The lattice's coordinate system: world axes unless the user picked a mate connector to orient it.
 *
 * An oriented lattice is nearly free here because the parameter solve in deformPointThroughLattice
 * uses scalar triple products rather than assuming an axis-aligned box, so a rotated — or even a
 * sheared — axis triple works without a special case. The old features hardcoded world X/Y/Z, which
 * meant deforming anything whose natural directions were not the global ones started by fighting
 * the lattice.
 *
 * @param context {Context}
 * @param definition {map} : the feature definition
 * @returns {CoordSystem} : the frame the bounding box is measured in
 */
function latticeCoordSystem(context is Context, definition is map) returns CoordSystem
{
    if (definition.orientLattice && !isQueryEmpty(context, definition.latticeOrientation))
    {
        return evMateConnector(context, { "mateConnector" : definition.latticeOrientation });
    }
    return WORLD_COORD_SYSTEM;
}

/**
 * Build the lattice: bound every source surface, lay out the grid, apply the user's stored offsets,
 * then apply the live selection transform on top.
 *
 * The box bounds the CONTROL POINTS, not the surfaces. That is deliberate and is not an
 * approximation of the tighter box: the control hull contains the surface, so a lattice built on it
 * contains the surface too, and every control point is therefore inside the lattice with parameters
 * in [0, 1] by construction. Bounding the surfaces instead would leave control points outside the
 * box, where the Bernstein basis extrapolates and behaves nothing like it does inside.
 *
 * @param context {Context}
 * @param id {Id} : for out-of-range warnings
 * @param definition {map} : the feature definition
 * @param sourceSurfaces {array} : normalized surface definitions to bound
 * @returns {map} : the lattice, as documented on latticeStructure
 */
function buildLattice(context is Context, id is Id, definition is map, sourceSurfaces is array) returns map
{
    const frame = latticeCoordSystem(context, definition);
    const localBox = controlPointBoundsInFrame(sourceSurfaces, frame);
    var lattice = latticeStructure(frame, localBox, definition.latticeSpanCountU,
        definition.latticeSpanCountV, definition.latticeSpanCountN);

    if (!definition.editLattice)
    {
        return lattice;
    }

    const committed = latticePointsWithOffsets(lattice, definition.latticePointOffsets);
    if (committed.outOfRangeCount > 0)
    {
        reportFeatureWarning(context, id, committed.outOfRangeCount ~ " lattice point offset(s) are outside the " ~
            "current " ~ lattice.pointCountU ~ "x" ~ lattice.pointCountV ~ "x" ~ lattice.pointCountN ~ " lattice " ~
            "and were ignored. They are still stored, so raising the span counts back will restore them.",
            ["latticePointOffsets"]);
    }

    lattice.controlPoints = committed.points;
    lattice.controlPoints = applySelectionTransform(lattice, definition);
    return lattice;
}

/**
 * The axis-aligned bounds of every source surface's control points, measured in the lattice frame.
 *
 * @param sourceSurfaces {array} : readSourceSurfaces records, each with a `surface`
 * @param frame {CoordSystem} : the lattice frame
 * @returns {map} : `minCorner` and `maxCorner`, both Vectors in FRAME-LOCAL coordinates
 */
function controlPointBoundsInFrame(sourceSurfaces is array, frame is CoordSystem) returns map
{
    const worldToLocal = fromWorld(frame);
    var minCorner = undefined;
    var maxCorner = undefined;

    for (var sourceRecord in sourceSurfaces)
    {
        for (var row in sourceRecord.surface.controlPoints)
        {
            for (var worldPoint in row)
            {
                const localPoint = worldToLocal * worldPoint;
                if (minCorner == undefined)
                {
                    minCorner = localPoint;
                    maxCorner = localPoint;
                }
                else
                {
                    minCorner = vector(min(minCorner[0], localPoint[0]), min(minCorner[1], localPoint[1]),
                        min(minCorner[2], localPoint[2]));
                    maxCorner = vector(max(maxCorner[0], localPoint[0]), max(maxCorner[1], localPoint[1]),
                        max(maxCorner[2], localPoint[2]));
                }
            }
        }
    }

    if (minCorner == undefined)
    {
        throw "freeFormDeformation: no control points to bound.";
    }
    return { "minCorner" : minCorner, "maxCorner" : maxCorner };
}

/**
 * Lay out the undeformed lattice.
 *
 * The returned map is the currency of this whole file:
 *   frame                          {CoordSystem} : the lattice's own frame
 *   origin                         {Vector}      : world position of lattice parameters (0, 0, 0)
 *   axisU / axisV / axisN          {Vector}      : world edge vectors, magnitude included
 *   spanCountU / V / N             {number}      : spans per direction
 *   pointCountU / V / N            {number}      : spans + 1
 *   totalPointCount                {number}
 *   gridPoints                     {array}       : undeformed positions, flat, latticeFlatIndex order
 *   controlPoints                  {array}       : current positions, same order and length
 *   crossU / crossV / crossN       {Vector}      : precomputed axis cross products for the solve
 *   diagonal                       {ValueWithUnits} : box diagonal, the scale everything relative is measured against
 *   inflatedDirections             {array}       : names of directions that were degenerate and got inflated
 *
 * @param frame {CoordSystem} : the lattice frame
 * @param localBox {map} : `minCorner` / `maxCorner` in frame-local coordinates
 * @param spanCountU {number}
 * @param spanCountV {number}
 * @param spanCountN {number}
 * @returns {map} : the lattice
 */
function latticeStructure(frame is CoordSystem, localBox is map, spanCountU is number,
    spanCountV is number, spanCountN is number) returns map
{
    const rawExtent = localBox.maxCorner - localBox.minCorner;
    const diagonal = norm(rawExtent);
    // Floored so that even a fully degenerate input (every control point coincident) still yields a
    // lattice with non-zero volume, and the parameter solve therefore still has a denominator.
    const minimumExtent = max(diagonal * DEGENERATE_DIRECTION_INFLATION, TOLERANCE.zeroLength * meter);

    // Inflate any direction that is flat (or nearly so) symmetrically about its own centre, so the
    // surface sits at that direction's mid-parameter rather than pinned against a wall of it.
    //
    // Held as plain arrays of scalars rather than as Vectors: these are being edited one component
    // at a time, and a Vector is a typed value whose components carry units, so rebuilding it once
    // at the end is clearer than mutating one in place.
    var lowerBounds = [localBox.minCorner[0], localBox.minCorner[1], localBox.minCorner[2]];
    var extents = [rawExtent[0], rawExtent[1], rawExtent[2]];
    var inflatedDirections = [];
    const directionNames = ["U", "V", "N"];
    for (var axisIndex = 0; axisIndex < 3; axisIndex += 1)
    {
        if (extents[axisIndex] >= minimumExtent)
        {
            continue;
        }
        const centre = lowerBounds[axisIndex] + extents[axisIndex] / 2;
        lowerBounds[axisIndex] = centre - minimumExtent / 2;
        extents[axisIndex] = minimumExtent;
        inflatedDirections = append(inflatedDirections, directionNames[axisIndex]);
    }

    const localToWorld = toWorld(frame);
    const origin = localToWorld * vector(lowerBounds[0], lowerBounds[1], lowerBounds[2]);
    // Edge vectors, not directions: the magnitude is the box extent, which is what makes the
    // parameter solve below dimensionless and the parameters run 0 to 1 across the lattice.
    const axisU = frame.xAxis * extents[0];
    const axisV = yAxis(frame) * extents[1];
    const axisN = frame.zAxis * extents[2];

    const pointCountU = spanCountU + 1;
    const pointCountV = spanCountV + 1;
    const pointCountN = spanCountN + 1;
    const totalPointCount = pointCountU * pointCountV * pointCountN;

    var gridPoints = makeArray(totalPointCount, origin);
    for (var indexU = 0; indexU < pointCountU; indexU += 1)
    {
        for (var indexV = 0; indexV < pointCountV; indexV += 1)
        {
            for (var indexN = 0; indexN < pointCountN; indexN += 1)
            {
                gridPoints[indexU * pointCountV * pointCountN + indexV * pointCountN + indexN] =
                    origin + axisU * (indexU / spanCountU) + axisV * (indexV / spanCountV) + axisN * (indexN / spanCountN);
            }
        }
    }

    return {
            "frame" : frame,
            "origin" : origin,
            "axisU" : axisU,
            "axisV" : axisV,
            "axisN" : axisN,
            "spanCountU" : spanCountU,
            "spanCountV" : spanCountV,
            "spanCountN" : spanCountN,
            "pointCountU" : pointCountU,
            "pointCountV" : pointCountV,
            "pointCountN" : pointCountN,
            "totalPointCount" : totalPointCount,
            "gridPoints" : gridPoints,
            "controlPoints" : gridPoints,
            // Precomputed once here rather than per deformed control point: the parameter solve runs
            // for every control point of every surface at every refinement level, which is the
            // hottest loop in the feature.
            "crossU" : cross(axisV, axisN),
            "crossV" : cross(axisU, axisN),
            "crossN" : cross(axisU, axisV),
            "diagonal" : diagonal,
            "inflatedDirections" : inflatedDirections
        };
}

/**
 * The lattice's COMMITTED positions: the grid plus the user's stored per-point offsets, with the
 * live selection transform deliberately not applied.
 *
 * Always measured from `gridPoints` rather than from whatever is currently in `controlPoints`, so
 * calling it twice cannot apply the offsets twice. Three callers need this exact state — the
 * lattice build, the full triad's base frame, and the transform bake — and two of them are handed a
 * lattice whose control points have already moved.
 *
 * Offsets whose indices no longer exist, because the span counts were lowered after they were
 * authored, are COUNTED AND IGNORED rather than thrown on. Changing lattice resolution mid-edit is
 * a first-class FFD action, not a mistake, and refusing to regenerate until the user hand-deletes
 * stale rows would make the resolution control unusable. They stay in the definition, so raising
 * the span count back restores them. The count is returned rather than reported here because two of
 * the three callers are not in a position to warn.
 *
 * @param lattice {map}
 * @param latticePointOffsets {array} : the definition's offset records
 * @returns {map} : `points` {array}, flat in latticeFlatIndex order, and `outOfRangeCount` {number}
 */
function latticePointsWithOffsets(lattice is map, latticePointOffsets is array) returns map
{
    var points = lattice.gridPoints;
    var outOfRangeCount = 0;
    for (var offsetRecord in latticePointOffsets)
    {
        if (!cellIsInLattice(offsetRecord.uIndex, offsetRecord.vIndex, offsetRecord.nIndex, lattice))
        {
            outOfRangeCount += 1;
            continue;
        }
        const flatIndex = latticeFlatIndex(offsetRecord.uIndex, offsetRecord.vIndex, offsetRecord.nIndex, lattice);
        points[flatIndex] = points[flatIndex] + vector(offsetRecord.x, offsetRecord.y, offsetRecord.z);
    }
    return { "points" : points, "outOfRangeCount" : outOfRangeCount };
}

/**
 * Apply the live selection transform — the fullTriadManipulator's current state — on top of the
 * committed offsets.
 *
 * Why this is a separate stage from the offsets at all: a fullTriadManipulator reports one
 * CUMULATIVE transform relative to the base it was last handed, not an increment, so it cannot be
 * folded into per-point offsets on every drag frame without either composing inverses or re-reading
 * the geometry hundreds of times per drag. Keeping it live and baking it exactly once, when the
 * selection changes, does neither. bakeSelectionTransform performs that bake and is the only other
 * place this arithmetic appears — the two must stay in step, which is why both go through
 * selectionTransformInWorld.
 *
 * @param lattice {map} : with committed offsets already applied
 * @param definition {map}
 * @returns {array} : new control point positions, flat, latticeFlatIndex order
 */
function applySelectionTransform(lattice is map, definition is map) returns array
{
    var points = lattice.controlPoints;
    const selectedCells = validSelectedCells(definition.selectedIndices, lattice);
    if (size(selectedCells) == 0)
    {
        return points;
    }

    const worldTransform = selectionTransformInWorld(points, selectedCells, lattice, definition);
    if (worldTransform == undefined)
    {
        return points;
    }

    for (var cell in selectedCells)
    {
        const flatIndex = latticeFlatIndex(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue, lattice);
        points[flatIndex] = worldTransform * points[flatIndex];
    }
    return points;
}

/**
 * The selection transform, sandwiched into world space, or undefined when it is the identity.
 *
 * The base coordinate system sits at the centroid of the selection's positions BEFORE this
 * transform (grid plus committed offsets) and is oriented to the lattice's own axes, so rotating a
 * constant-N plane rotates it about its own centre in the lattice's frame rather than about a
 * world axis. That base is what showLatticeManipulators hands the manipulator, so the transform the
 * manipulator reports is expressed in exactly this frame.
 *
 * The sandwich, the stored transposed rotation, and the un-inverted translation together are
 * routingCurve.fs's convention, kept verbatim — see docs/featurescript-guides/TRIAD_MANIPULATOR_NOTES.md.
 * What matters is that the SAME reconstruction is used to drive the points and to redisplay the
 * manipulator, which is what makes a drag land where the handle went.
 *
 * @param points {array} : current lattice points, before this transform
 * @param selectedCells {array} : cells, already range-checked
 * @param lattice {map}
 * @param definition {map}
 * @returns {Transform} : world-space transform, or `undefined` if there is nothing to apply
 */
function selectionTransformInWorld(points is array, selectedCells is array, lattice is map, definition is map)
{
    const localTransform = storedSelectionTransform(definition);
    if (localTransform == undefined)
    {
        return undefined;
    }
    const base = selectionBaseCoordSystem(points, selectedCells, lattice);
    return toWorld(base) * localTransform * fromWorld(base);
}

/**
 * Reconstruct the stored selection transform, or undefined when it is the identity and there is
 * nothing to do.
 *
 * @param definition {map}
 * @returns {Transform} : the transform in the selection's local frame, or `undefined`
 */
function storedSelectionTransform(definition is map)
{
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
 * The frame the selection transform acts in: origin at the selection's centroid, axes along the
 * lattice's own directions.
 *
 * @param points {array} : lattice points to take the centroid of
 * @param selectedCells {array}
 * @param lattice {map}
 * @returns {CoordSystem}
 */
function selectionBaseCoordSystem(points is array, selectedCells is array, lattice is map) returns CoordSystem
{
    var centroid = WORLD_ORIGIN;
    for (var cell in selectedCells)
    {
        centroid += points[latticeFlatIndex(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue, lattice)];
    }
    centroid /= size(selectedCells);
    return coordSystem(centroid, normalize(lattice.axisU), normalize(lattice.axisN));
}

//==================================================================
//======================= The deformation map ======================
//==================================================================

/**
 * Flatten the lattice into the plain-number form the deformation map actually reads.
 *
 * THIS IS A PERFORMANCE STRUCTURE AND NOTHING ELSE. It holds exactly the numbers the lattice holds,
 * rearranged so that the innermost loop of the feature touches no map, no Vector and no
 * ValueWithUnits. `deformPointThroughLattice` runs once per surface control point per refinement
 * level, and its inner body once more per lattice point on top of that — a 4x4x4 lattice deforming a
 * 60x60 net over four refinement levels executes it something like half a million times, which is
 * where the regeneration time was going.
 *
 * Three separate costs are removed, and they are worth naming because each one looks free at the
 * call site:
 *
 *   1. THE FLAT INDEX. `latticeFlatIndex(indexU, indexV, indexN, lattice)` is a function call plus
 *      three map lookups, and it was in the innermost loop. But the loops run U, then V, then N, and
 *      N is the fastest-varying index of that very packing — so the flat index is simply a counter
 *      that increments once per iteration, and no arithmetic is needed at all.
 *
 *   2. THE UNITS. A `Vector * number` over lengths allocates three ValueWithUnits and carries a unit
 *      map through each one. Splitting the control points into three plain-number arrays makes the
 *      accumulation ordinary floating point, and the units are put back once, at the end, on the one
 *      vector that leaves the function.
 *
 *   3. THE DENOMINATORS. `dot(crossU, axisU)` is a property of the LATTICE, not of the point being
 *      solved, and the previous parameter solve recomputed all three of them — three dot products
 *      over united values — for every point it was handed.
 *
 * Units bookkeeping: crossU is an area and dot(crossU, axisU) a volume, so the ratio is a reciprocal
 * length, and dividing the point offsets by metre to match leaves the parameters unitless exactly as
 * they were. `meter` is FeatureScript's base length unit, so every division here is by one and no
 * value's bits change — the compiled lattice is bit-for-bit the lattice.
 *
 * @param lattice {map} : a fully built lattice, control points already final
 * @returns {map} : the compiled form, which is all the deformation map reads
 */
function compileLatticeForDeformation(lattice is map) returns map
{
    const pointCount = lattice.totalPointCount;
    var pointsX = makeArray(pointCount, 0);
    var pointsY = makeArray(pointCount, 0);
    var pointsZ = makeArray(pointCount, 0);
    for (var index = 0; index < pointCount; index += 1)
    {
        const point = lattice.controlPoints[index];
        pointsX[index] = point[0] / meter;
        pointsY[index] = point[1] / meter;
        pointsZ[index] = point[2] / meter;
    }

    const squareMetre = meter * meter;
    const cubicMetre = squareMetre * meter;
    return {
            "originX" : lattice.origin[0] / meter,
            "originY" : lattice.origin[1] / meter,
            "originZ" : lattice.origin[2] / meter,
            // The parameter solve's three numerator vectors and their three constant denominators.
            "crossUx" : lattice.crossU[0] / squareMetre,
            "crossUy" : lattice.crossU[1] / squareMetre,
            "crossUz" : lattice.crossU[2] / squareMetre,
            "crossVx" : lattice.crossV[0] / squareMetre,
            "crossVy" : lattice.crossV[1] / squareMetre,
            "crossVz" : lattice.crossV[2] / squareMetre,
            "crossNx" : lattice.crossN[0] / squareMetre,
            "crossNy" : lattice.crossN[1] / squareMetre,
            "crossNz" : lattice.crossN[2] / squareMetre,
            "denominatorU" : dot(lattice.crossU, lattice.axisU) / cubicMetre,
            "denominatorV" : dot(lattice.crossV, lattice.axisV) / cubicMetre,
            "denominatorN" : dot(lattice.crossN, lattice.axisN) / cubicMetre,
            "spanCountU" : lattice.spanCountU,
            "spanCountV" : lattice.spanCountV,
            "spanCountN" : lattice.spanCountN,
            "pointCountU" : lattice.pointCountU,
            "pointCountV" : lattice.pointCountV,
            "pointCountN" : lattice.pointCountN,
            "pointsX" : pointsX,
            "pointsY" : pointsY,
            "pointsZ" : pointsZ
        };
}

/**
 * Every Bernstein basis value of a given degree at one parameter, as an array indexed by basis
 * number.
 *
 * Built by the triangular recurrence rather than from the closed form
 * `C(n,i) * (1-t)^(n-i) * t^i` that both old features used. Two reasons, and the first is a
 * correctness one: the closed form evaluates `0^0` at both ends of the parameter range, which is
 * only a convention away from being undefined, and it computes three factorials per basis value —
 * so a degree-8 lattice was computing 8! repeatedly inside the innermost loop of the hottest
 * function in the feature. The recurrence is exact in the same arithmetic, allocates one array per
 * call instead of none per value, and is the standard way this is done.
 *
 * @param degree {number} : the Bernstein degree, which for a lattice direction is its span count
 * @param parameter {number} : unitless
 * @returns {array} : `degree + 1` unitless basis values, summing to 1
 */
function bernsteinBasisValues(degree is number, parameter is number) returns array
{
    var values = makeArray(degree + 1, 0);
    values[0] = 1;
    const oneMinus = 1 - parameter;

    for (var order = 1; order <= degree; order += 1)
    {
        var carried = 0;
        for (var index = 0; index < order; index += 1)
        {
            const saved = values[index];
            values[index] = carried + oneMinus * saved;
            carried = parameter * saved;
        }
        values[order] = carried;
    }
    return values;
}

/**
 * The deformation map itself: push one world point through the trivariate Bernstein volume.
 *
 *   X(u, v, n) = sum_i sum_j sum_k  B_i(u) B_j(v) B_k(n) P_ijk
 *
 * Accumulated in the tensor-product order — innermost over N, then V, then U — so the basis values
 * for the outer directions are computed once per slice instead of once per term.
 *
 * Note that an UNMOVED lattice reproduces the point exactly, because the Bernstein basis reproduces
 * linear functions and the grid positions are a linear function of the parameters. That is the
 * property that makes "no edits" mean "no change" without a special case, and it is worth knowing
 * when reading a result: a surface that moved under an unedited lattice indicates a broken frame,
 * not a tolerance problem.
 *
 * THE PARAMETER SOLVE IS INLINED HERE rather than kept as its own function, and that is the one
 * readability cost this optimization pass charged. It is Sederberg & Parry's inversion, exact and
 * direct rather than iterative because the lattice's axes are a linear frame: for each axis, the
 * scalar triple product against the other two projects out that axis's parameter and cancels the
 * other two. It works for any non-degenerate axis triple, including a rotated or sheared one, which
 * is what lets the lattice be oriented to a mate connector for free.
 *
 * No epsilon guard on the denominators. A denominator here is the lattice's signed volume, which
 * latticeStructure has already made non-zero by inflating any degenerate direction; guarding it a
 * second time would only hide a genuinely broken frame.
 *
 * @param worldPoint {Vector} : a point with length units
 * @param compiled {map} : from compileLatticeForDeformation, whose block comment explains the shape
 * @returns {Vector} : the deformed point, with length units
 */
function deformPointThroughLattice(worldPoint is Vector, compiled is map) returns Vector
{
    const fromOriginX = worldPoint[0] / meter - compiled.originX;
    const fromOriginY = worldPoint[1] / meter - compiled.originY;
    const fromOriginZ = worldPoint[2] / meter - compiled.originZ;

    const basisU = bernsteinBasisValues(compiled.spanCountU,
        (compiled.crossUx * fromOriginX + compiled.crossUy * fromOriginY + compiled.crossUz * fromOriginZ) /
        compiled.denominatorU);
    const basisV = bernsteinBasisValues(compiled.spanCountV,
        (compiled.crossVx * fromOriginX + compiled.crossVy * fromOriginY + compiled.crossVz * fromOriginZ) /
        compiled.denominatorV);
    const basisN = bernsteinBasisValues(compiled.spanCountN,
        (compiled.crossNx * fromOriginX + compiled.crossNy * fromOriginY + compiled.crossNz * fromOriginZ) /
        compiled.denominatorN);

    // Hoisted out of the loops: every one of these is a map lookup, and the innermost body below is
    // the most-executed statement in the feature.
    const pointCountU = compiled.pointCountU;
    const pointCountV = compiled.pointCountV;
    const pointCountN = compiled.pointCountN;
    const pointsX = compiled.pointsX;
    const pointsY = compiled.pointsY;
    const pointsZ = compiled.pointsZ;

    var accumulatedX = 0;
    var accumulatedY = 0;
    var accumulatedZ = 0;
    // N is the fastest-varying index of latticeFlatIndex's packing and these loops run in exactly
    // that order, so the flat index is a counter rather than a computation. This walks the control
    // point arrays strictly front to back.
    var flatIndex = 0;
    for (var indexU = 0; indexU < pointCountU; indexU += 1)
    {
        var sliceX = 0;
        var sliceY = 0;
        var sliceZ = 0;
        for (var indexV = 0; indexV < pointCountV; indexV += 1)
        {
            var rowX = 0;
            var rowY = 0;
            var rowZ = 0;
            for (var indexN = 0; indexN < pointCountN; indexN += 1)
            {
                const weightN = basisN[indexN];
                rowX += pointsX[flatIndex] * weightN;
                rowY += pointsY[flatIndex] * weightN;
                rowZ += pointsZ[flatIndex] * weightN;
                flatIndex += 1;
            }
            const weightV = basisV[indexV];
            sliceX += rowX * weightV;
            sliceY += rowY * weightV;
            sliceZ += rowZ * weightV;
        }
        const weightU = basisU[indexU];
        accumulatedX += sliceX * weightU;
        accumulatedY += sliceY * weightU;
        accumulatedZ += sliceZ * weightU;
    }
    return vector(accumulatedX, accumulatedY, accumulatedZ) * meter;
}

/**
 * Push a whole control net through the lattice. The only step in the pipeline that moves anything.
 *
 * Knots, degrees, weights and periodicity all pass through untouched, which is what makes this
 * exact for an affine lattice and an approximation — bounded by the section 9.1.1 loop — otherwise.
 *
 * PERIODICITY SURVIVES THIS BY CONSTRUCTION, with no special case. A periodic direction's stored
 * form carries `degree` rows that are literal copies of the first `degree`, and the map is a pure
 * function of position, so equal points deform to equal points and the overlap condition that makes
 * the surface closed cannot be broken here. (editSurface.fs has to work for this because a USER can
 * move one copy without its twin; a deformation map cannot.)
 *
 * THE GRID IS REBUILT ROW BY ROW rather than written into in place, and that is a performance
 * decision with a real magnitude behind it. FeatureScript values are immutable, so
 * `deformed.controlPoints[row][column] = point` is not a store — it rebuilds the row, then the grid,
 * then the map, once per control point. Assembling each row in a local array and assigning the
 * finished grid ONCE turns an O(rows x columns) sequence of structural rebuilds into one.
 *
 * @param surface {map} : a normalized B-spline surface definition
 * @param compiled {map} : from compileLatticeForDeformation
 * @returns {map} : the same definition with deformed control points
 */
function deformControlNet(surface is map, compiled is map) returns map
{
    const rowCount = size(surface.controlPoints);
    var deformedRows = makeArray(rowCount, 0);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        const sourceRow = surface.controlPoints[rowIndex];
        const columnCount = size(sourceRow);
        var deformedRow = makeArray(columnCount, WORLD_ORIGIN);
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            deformedRow[columnIndex] = deformPointThroughLattice(sourceRow[columnIndex], compiled);
        }
        deformedRows[rowIndex] = deformedRow;
    }

    var deformed = surface;
    deformed.controlPoints = deformedRows;
    return deformed;
}

//==================================================================
//======================= Deformation pipeline =====================
//==================================================================

/**
 * Steps 2 through 5 of the spec section 9.1 pipeline for one face: elevate, refine to tolerance,
 * deform, emit, certify, and optionally replace.
 *
 * @param context {Context}
 * @param id {Id} : the feature id, for reporting
 * @param idGenerator {function} : zero-argument disambiguated id source for every operation
 * @param definition {map}
 * @param compiledLattice {map} : from compileLatticeForDeformation
 * @param sourceRecord {map} : the face's readSourceSurfaces record — `surface`, `outerLoop`, `innerLoops`
 * @param face {Query} : the face itself, for REPLACE_FACE
 * @param tolerance {ValueWithUnits}
 * @returns {map} : `certifiedDeviation` {ValueWithUnits}, `controlPointCount` {number},
 *                  `budgetHit` {boolean}, `trimDropped` {boolean}
 */
function deformOneFace(context is Context, id is Id, idGenerator is function, definition is map,
    compiledLattice is map, sourceRecord is map, face is Query, tolerance is ValueWithUnits) returns map
{
    const sourceSurface = sourceRecord.surface;
    const domainBefore = surfaceParameterDomains(sourceSurface);
    const converged = deformToTolerance(context, id, definition, compiledLattice, sourceSurface, tolerance);

    if (definition.showControlNet)
    {
        showDeformedControlNet(context, converged.deformed);
    }

    // THE TRIM LOOPS SURVIVE THE WHOLE PIPELINE, and that is the property the trimmed mode rests on.
    // They are 2D curves in the surface's PARAMETER space, and nothing between the read and the
    // emission touches that parameterization: degree elevation raises every knot's multiplicity but
    // keeps the interval, knot insertion adds knots strictly inside it, and the deformation moves
    // control points while leaving knots, degrees and weights alone. So the loops need no
    // reprojection — exactly the invariance EDIT_SURFACE_SPEC.md section 2.3 records.
    //
    // The one direction that CAN break it is a periodic one, where normalizing a closed-clamped read
    // into the module's wrap form re-cuts the knot vector. Rather than reason about when that shifts
    // the domain, it is MEASURED, the same way editSurface.fs's trim probe measured it. A moved
    // domain means the loops describe a region of a parameter space that no longer exists, and
    // trimming with them would silently cut the wrong shape — so the trim is dropped and the caller
    // says so, which is the one outcome worse than untrimmed avoided.
    // Uniformizing a closed direction is the OTHER thing that invalidates the loops, and it does so
    // without moving the domain: the re-fit preserves the domain start and the period, so
    // `domainHeld` stays true. What it changes is the parameter-to-point map INSIDE that domain, and
    // not by a little — reparameterizing to arc length is the entire point of the step, so on a
    // revolve a given u moves by up to several degrees of arc. Trim curves are measured in that map
    // and would cut a visibly wrong shape. So a uniformized face is emitted untrimmed and the caller
    // says why. (This is a stronger reason than the domain check above, not a weaker one: a moved
    // domain is detectable, a re-mapped interior is not.)
    const domainHeld = parameterDomainsAgree(domainBefore, surfaceParameterDomains(converged.undeformed));
    const wantTrim = definition.result == FFDResult.NEW_BODY_TRIMMED &&
        (size(sourceRecord.outerLoop) > 0 || size(sourceRecord.innerLoops) > 0);
    const trimDropped = wantTrim && (!domainHeld || converged.uniformized);

    // Printed BEFORE the emission that may throw, so the structure of a surface the kernel is about
    // to reject is still on the console when it does.
    if (definition.printRefinementDetails == true)
    {
        println("=== FFD surface pipeline (one face) ===");
        printSurfaceStructure("after elevate + refine, undeformed", converged.undeformed);
        printSurfaceStructure("after deformation, module wrap form", converged.deformed);
        printSurfaceStructure("as handed to opCreateBSplineSurface", closedClampedForEmission(converged.deformed));
        println("  parameter domain held through the pipeline: " ~ domainHeld);
    }

    const templateId = wantTrim && !trimDropped ?
        emitTrimmedSurface(context, id, idGenerator, converged.deformed, sourceRecord) :
        emitSurface(context, idGenerator, converged.deformed);

    // Certify BEFORE any replace: REPLACE_FACE consumes the original face, and this measurement is
    // against the body just created, not against the original. Samples sit at knot span midpoints —
    // exactly where the free control-net comparison that drove the loop is blind — so this is the
    // step that catches a lattice feature finer than the span spacing.
    //
    // A trimmed sheet is not a measurable target (see certifiedDeviationOfDeformation), so one
    // untrimmed twin of the very same control net is built to measure, then disposed of. Its cost is
    // one create and one delete per face, paid only in trimmed mode.
    const certificationTwinId = wantTrim && !trimDropped ? idGenerator() : undefined;
    if (certificationTwinId != undefined)
    {
        opCreateBSplineSurface(context, certificationTwinId, { "bSplineSurface" : kernelSurface(converged.deformed) });
    }
    const certificationTarget = certificationTwinId == undefined ?
        qCreatedBy(templateId, EntityType.BODY) : qCreatedBy(certificationTwinId, EntityType.BODY);

    const certifiedDeviation = certifiedDeviationOfDeformation(context, converged.undeformed, compiledLattice,
        certificationTarget);

    if (certificationTwinId != undefined)
    {
        opDeleteBodies(context, idGenerator(), { "entities" : qCreatedBy(certificationTwinId, EntityType.BODY) });
    }

    if (definition.printRefinementDetails)
    {
        printRefinementDetails(converged, certifiedDeviation, tolerance);
    }
    // Two independent error sources against one tolerance, added by the triangle inequality: the
    // uniformization moved the surface before the deformation ran, and the certification below
    // cannot see that because it measures against the UNIFORMIZED surface's own true deformation.
    // Reporting only the certified half would understate the total whenever a closed face was
    // uniformized.
    const totalDeviation = certifiedDeviation + converged.uniformizationDeviation;
    if (totalDeviation > tolerance)
    {
        // `delta` is only a measurement if the loop actually compared two levels. When the surface
        // arrived already at the budget it never refined, so `delta` is still its initial zero —
        // reporting that as "within 0" would read as a converged result contradicting the very
        // warning it sits in.
        const controlNetClause = converged.passes == 0 ?
            "The surface was already at the control point budget, so no refinement pass ran and the free " ~
            "control-net measure never compared two levels. " :
            "The deformed surface is within " ~ toString(converged.delta) ~
            " by the control-net measure that drove refinement, but ";
        const uniformizationClause = converged.uniformized ?
            " Preparing the closed direction for deformation accounts for " ~
            toString(converged.uniformizationDeviation) ~ " of that." : "";
        reportFeatureWarning(context, id, controlNetClause ~ "the kernel certifies " ~
            toString(certifiedDeviation) ~ " against it, for a total of " ~ toString(totalDeviation) ~
            " above the tolerance of " ~ toString(tolerance) ~ "." ~ uniformizationClause ~ " The " ~
            "lattice has a feature finer than the current span spacing. Raise the refinement budget, or lower the " ~
            "lattice resolution.");
    }

    if (definition.result == FFDResult.REPLACE_FACE)
    {
        replaceTargetFace(context, id, idGenerator, face, templateId);
    }

    return {
            "certifiedDeviation" : totalDeviation,
            "controlPointCount" : max(size(converged.undeformed.controlPoints), size(converged.undeformed.controlPoints[0])),
            "budgetHit" : converged.budgetHit,
            "trimDropped" : trimDropped,
            "elevationBlocked" : converged.elevationBlocked,
            "uniformized" : converged.uniformized
        };
}

/** Both directions' parameter domains, for a before/after comparison across the pipeline.

    @param surface {map} : a normalized surface definition
    @returns {map} : `u` and `v`, each `knotDomain`'s `start`/`end` */
function surfaceParameterDomains(surface is map) returns map
{
    return {
            "u" : knotDomain(surface.uKnots, surface.uDegree),
            "v" : knotDomain(surface.vKnots, surface.vDegree)
        };
}

/** Whether two parameter domains are the same interval in both directions. Compared against
    KNOT_PARAMETER_TOLERANCE, the same threshold the module uses to decide two knots are one knot.

    @param first {map} : surfaceParameterDomains result
    @param second {map} : surfaceParameterDomains result
    @returns {boolean} */
function parameterDomainsAgree(first is map, second is map) returns boolean
{
    return abs(first.u.start - second.u.start) <= KNOT_PARAMETER_TOLERANCE &&
        abs(first.u.end - second.u.end) <= KNOT_PARAMETER_TOLERANCE &&
        abs(first.v.start - second.v.start) <= KNOT_PARAMETER_TOLERANCE &&
        abs(first.v.end - second.v.end) <= KNOT_PARAMETER_TOLERANCE;
}

/**
 * The spec section 9.1.1 loop: drive on the free control-net measure, refining until two successive
 * levels agree inside the tolerance.
 *
 * Elevation happens ONCE, before the loop, and never inside it. The order is load-bearing:
 * elevating after refining multiplies the control point count for nothing, because elevation adds
 * points in proportion to the segment count it is handed.
 *
 * Each pass doubles every direction's span count. `nextStoredControlPointCount` works in stored
 * counts so the same arithmetic covers a clamped direction and a periodic one, whose stored array
 * carries `degree` extra wrap rows.
 *
 * No work is wasted: every deformation either ships or becomes the baseline the next one is
 * compared against.
 *
 * @param context {Context}
 * @param id {Id}
 * @param definition {map}
 * @param compiledLattice {map} : from compileLatticeForDeformation
 * @param sourceSurface {map}
 * @param tolerance {ValueWithUnits}
 * @returns {map} : `undeformed` and `deformed` at the final level, `delta` {ValueWithUnits},
 *                  `passes` {number}, `budgetHit` {boolean}
 */
function deformToTolerance(context is Context, id is Id, definition is map, compiledLattice is map,
    sourceSurface is map, tolerance is ValueWithUnits) returns map
{
    // STEP ZERO, and the one that makes a revolve deformable at all: get every closed direction off
    // its C0 knots. A knot at multiplicity `degree` leaves the surface free to crease there, and the
    // deformation below WILL crease it, because a lattice is a nonlinear map and does not carry the
    // collinear control points that were holding the join smooth to collinear images. The kernel
    // then refuses the body — NOT_SMOOTH at the seam, NOT_G1 in the interior. Neither refinement nor
    // elevation can prevent it: both only ever RAISE multiplicity.
    //
    // It is a UNIFORMIZING REFIT rather than the exact cyclic projection, and the difference is
    // visible on every revolve. The projection removes the C0 knots while preserving the
    // parameterization it projects, and a revolve's closed direction is a pair of rational cubic
    // arcs whose parameter runs about 1.7 : 1 slower at the joins than mid-arc. Its net therefore
    // comes back deformable and BUNCHED, in two bands at the arc joins, which the refinement loop
    // below cannot undo: doubling gives every span exactly one arc-length cut, so the ratio is
    // preserved at every level. The refit lands the knots evenly in arc length and makes u and v
    // proportional to it, which is what stops the deformed body's control net — and every downstream
    // feature reading its u/v — from crowding at those two places. See the module's periodic
    // uniformization section.
    //
    // Half the budget, because this and the refinement loop below are independent error sources
    // against one tolerance and the certification cannot see this one: it measures the emitted body
    // against the deformed UNIFORMIZED surface, which is the right target for the loop and the wrong
    // one for this step. The caller adds the two.
    const uniformization = uniformizePeriodicSurfaceDirections(sourceSurface, 0.5 * tolerance);
    const preparedSource = uniformization.uniformized ? uniformization : sourceSurface;
    const uniformizationDeviation = uniformization.uniformized ? uniformization.deviation : 0 * meter;

    const uPeriodic = preparedSource.isUPeriodic == true;
    const vPeriodic = preparedSource.isVPeriodic == true;
    const targetUDegree = targetDegreeForContinuity(definition.continuity, preparedSource.uDegree, uPeriodic);
    const targetVDegree = targetDegreeForContinuity(definition.continuity, preparedSource.vDegree, vPeriodic);
    const elevationBlocked = periodicBlockedElevation(definition.continuity, preparedSource.uDegree, uPeriodic) ||
        periodicBlockedElevation(definition.continuity, preparedSource.vDegree, vPeriodic);

    if (definition.printRefinementDetails == true && uniformization.uniformized)
    {
        println("=== FFD periodic uniformization ===");
        printSurfaceStructure("after uniformizing closed directions", preparedSource);
        println("  measured uniformization deviation: " ~ toString(uniformizationDeviation) ~
            " (half-budget " ~ toString(0.5 * tolerance) ~ ")");
        if (uniformization.uniformizationCapped == true)
        {
            println("  WARNING: hit the control point ceiling before the tolerance");
        }
    }

    // Elevate only — the current counts are passed as the refinement targets, which
    // prepareSurfaceForDeformation treats as "already satisfied" and skips.
    var undeformed = prepareSurfaceForDeformation(preparedSource, targetUDegree, targetVDegree,
        size(preparedSource.controlPoints), size(preparedSource.controlPoints[0]));
    var deformed = deformControlNet(undeformed, compiledLattice);

    var delta = 0 * meter;
    var passes = 0;
    var budgetHit = false;

    while (true)
    {
        // Clamped up to the current count so that a direction already at the budget stays exactly
        // where it is while the other direction keeps refining, rather than being handed a target
        // below its own count.
        const nextUCount = max(size(undeformed.controlPoints),
            nextStoredControlPointCount(size(undeformed.controlPoints), undeformed.uDegree, definition.refinementBudget));
        const nextVCount = max(size(undeformed.controlPoints[0]),
            nextStoredControlPointCount(size(undeformed.controlPoints[0]), undeformed.vDegree, definition.refinementBudget));

        if (nextUCount <= size(undeformed.controlPoints) && nextVCount <= size(undeformed.controlPoints[0]))
        {
            // Both directions are at the ceiling: there is no finer level to compare against, so
            // the loop cannot certify convergence and says so rather than pretending.
            budgetHit = true;
            break;
        }

        // The operators are kept, not discarded, and that is what makes the comparison below cheap.
        // They carry THIS level's knot vectors onto the next level's, so the previous level's
        // already-deformed net can be lifted into the finer space by exactly the refinement that
        // created that space — instead of asking makeSurfacesShareKnotVectors to rediscover a
        // nesting relationship that is already in hand.
        const refinement = refineSurfaceToControlPointCountsWithOperators(undeformed, nextUCount, nextVCount, false);
        const nextUndeformed = refinement.surface;
        const nextDeformed = deformControlNet(nextUndeformed, compiledLattice);

        // Deform-then-refine, not refine-then-deform: this is the PREVIOUS level's deformation
        // expressed on the current level's knots, which is precisely what the two levels have to
        // disagree about for the loop to mean anything. (The two orders are equal only for an affine
        // lattice, which is the degenerate case where there is nothing to converge.)
        const previousOnSharedKnots = applySurfaceDirectionOperators(deformed, refinement.uOperator,
            refinement.vOperator);

        delta = maximumControlNetDeviation(previousOnSharedKnots, nextDeformed);
        undeformed = nextUndeformed;
        deformed = nextDeformed;
        passes += 1;

        if (delta <= tolerance)
        {
            break;
        }
        if (passes >= definition.refinementIterations)
        {
            budgetHit = true;
            break;
        }
    }

    return { "undeformed" : undeformed, "deformed" : deformed, "delta" : delta, "passes" : passes,
            "budgetHit" : budgetHit, "elevationBlocked" : elevationBlocked,
            "uniformizationDeviation" : uniformizationDeviation, "uniformized" : uniformization.uniformized,
            "uniformizationCapped" : uniformization.uniformizationCapped };
}

/**
 * The stored control point count one refinement level finer, capped by the budget.
 *
 * Works in SPANS and converts back, which is what makes one expression cover both a clamped
 * direction (`spans = stored - degree`) and a periodic one (`stored = fundamental + degree`, so
 * `spans = fundamental = stored - degree` as well). Refining to a count at or below the current one
 * is the loop's signal that this direction is finished.
 *
 * @param storedCount {number} : the direction's current stored control point count
 * @param degree {number}
 * @param budget {number} : the ceiling on stored control points for this direction
 * @returns {number} : the next stored count
 */
function nextStoredControlPointCount(storedCount is number, degree is number, budget is number) returns number
{
    const doubled = 2 * (storedCount - degree) + degree;
    return min(doubled, budget);
}

/**
 * The free measure the loop runs on: the largest distance between corresponding control points of
 * two deformed surfaces ALREADY ON A COMMON KNOT VECTOR.
 *
 * This bounds the true surface deviation without evaluating either surface. Both the polynomial
 * basis and the rational basis are non-negative and sum to 1, so the difference of two splines
 * sharing degree, knots AND weights is a convex combination of their control point differences:
 *
 *     ‖S_a(t) - S_b(t)‖ <= max_i ‖P_a,i - P_b,i‖
 *
 * THE SHARED VECTOR IS THE CALLER'S JOB, and used to be this function's: it called
 * makeSurfacesShareKnotVectors on the pair. That was correct and wasteful. The refinement loop is
 * the only caller, and it holds the operators that MADE the finer level, so lifting the coarse net
 * with those is exact and costs one application — where the general merge re-normalized both
 * surfaces, rediscovered a nesting it was already being handed, and refined the fine grid by nothing
 * at all. The mismatched-shape check below is what keeps that shortcut honest: a caller that lifted
 * the wrong grid, or forgot to, gets an error rather than a meaningless small number from comparing
 * points that do not correspond.
 *
 * The "and weights" clause is earned the same way it was before — the operators act on the
 * homogeneous grid, so the weights are refined by the identical arithmetic as the points.
 *
 * @param coarse {map} : the level-k deformed surface, lifted onto the level-(k+1) knot vectors
 * @param fine {map} : the deformed surface at level k+1
 * @returns {ValueWithUnits} : the bound, a length
 */
function maximumControlNetDeviation(coarse is map, fine is map) returns ValueWithUnits
{
    const gridA = coarse.controlPoints;
    const gridB = fine.controlPoints;
    if (size(gridA) != size(gridB) || size(gridA[0]) != size(gridB[0]))
    {
        throw "freeFormDeformation: the two refinement levels are not on a common knot vector (" ~
            size(gridA) ~ "x" ~ size(gridA[0]) ~ " against " ~ size(gridB) ~ "x" ~ size(gridB[0]) ~
            "). The coarse net must be lifted with the operators that produced the fine one.";
    }
    var worst = 0 * meter;
    for (var rowIndex = 0; rowIndex < size(gridA); rowIndex += 1)
    {
        // Fetched once per row rather than twice per cell: on the last refinement pass this grid is
        // the largest one the feature ever holds, and the row lookups alone were four per point.
        const rowA = gridA[rowIndex];
        const rowB = gridB[rowIndex];
        for (var columnIndex = 0; columnIndex < size(rowA); columnIndex += 1)
        {
            worst = max(worst, norm(rowA[columnIndex] - rowB[columnIndex]));
        }
    }
    return worst;
}

/**
 * The certification: the actual geometric error, measured by the kernel.
 *
 * Evaluate the REFINED UNDEFORMED definition at the midpoint of every knot span in u and v — pure
 * arithmetic through the module's own evaluator, and by construction in the same parameterization
 * as the definition, so there is no face-parameter normalization to mismatch the way
 * evFaceTangentPlanes would. Push those points through the lattice to get points that lie on the
 * TRUE deformed surface, then ask evPointsDeviation how far they are from the body actually built.
 *
 * WHAT IT MEASURES AGAINST IS THE CALLER'S CHOICE, and it has to be, because a TRIMMED body cannot
 * be measured against directly. The samples run over the whole parameter rectangle, but a trimmed
 * sheet only exists inside its loops, so every sample outside the trim would report its distance to
 * the nearest trim EDGE — a large number that measures the trim rather than the deformation, and one
 * that gets worse the more of the rectangle the face omits. Trimmed mode therefore hands in an
 * untrimmed twin instead. See deformOneFace, which builds and disposes of it.
 *
 * @param context {Context}
 * @param undeformed {map} : the refined undeformed surface
 * @param compiled {map} : from compileLatticeForDeformation
 * @param targetBodies {Query} : the untrimmed body the true points are measured against
 * @returns {ValueWithUnits} : the worst deviation, a length
 */
function certifiedDeviationOfDeformation(context is Context, undeformed is map, compiled is map,
    targetBodies is Query) returns ValueWithUnits
{
    const uSamples = spanMidpointParameters(undeformed.uKnots, undeformed.uDegree);
    const vSamples = spanMidpointParameters(undeformed.vKnots, undeformed.vDegree);
    if (size(uSamples) == 0 || size(vSamples) == 0)
    {
        return 0 * meter;
    }

    // Preallocated rather than appended to. The sample count is known exactly — up to 400 with
    // MAXIMUM_CERTIFICATION_SAMPLES_PER_DIRECTION at 20 — and `append` on an immutable array rebuilds
    // it, so growing one 400 times is quadratic in a loop whose per-item work is otherwise a surface
    // evaluation.
    var truePoints = makeArray(size(uSamples) * size(vSamples), WORLD_ORIGIN);
    var writeIndex = 0;
    for (var uParameter in uSamples)
    {
        for (var vParameter in vSamples)
        {
            truePoints[writeIndex] =
                deformPointThroughLattice(evaluateBSplineSurfacePoint(undeformed, uParameter, vParameter), compiled);
            writeIndex += 1;
        }
    }

    const deviations = evPointsDeviation(context, {
                "points" : truePoints,
                "topologies" : targetBodies,
                "allDeviations" : false
            });
    return size(deviations) == 0 ? 0 * meter : deviations[0].deviation;
}

/**
 * Knot span midpoints across a direction's domain, thinned to at most
 * MAXIMUM_CERTIFICATION_SAMPLES_PER_DIRECTION by taking an even stride.
 *
 * Midpoints specifically, not knots: a knot is where two levels of refinement agree by
 * construction, and the midpoint is where they are furthest from agreeing.
 *
 * @param knots {array} : a knot array
 * @param degree {number}
 * @returns {array} : sample parameters inside the domain
 */
function spanMidpointParameters(knots is array, degree is number) returns array
{
    const domain = knotDomain(knots, degree);
    var midpoints = [];
    for (var knotIndex = 0; knotIndex < size(knots) - 1; knotIndex += 1)
    {
        const spanStart = knots[knotIndex];
        const spanEnd = knots[knotIndex + 1];
        if (spanEnd - spanStart <= KNOT_PARAMETER_TOLERANCE)
        {
            continue;
        }
        if (spanEnd < domain.start || spanStart > domain.end)
        {
            continue;
        }
        const midpoint = (max(spanStart, domain.start) + min(spanEnd, domain.end)) / 2;
        midpoints = append(midpoints, midpoint);
    }

    if (size(midpoints) <= MAXIMUM_CERTIFICATION_SAMPLES_PER_DIRECTION)
    {
        return midpoints;
    }

    const stride = ceil(size(midpoints) / MAXIMUM_CERTIFICATION_SAMPLES_PER_DIRECTION);
    var thinned = [];
    for (var index = 0; index < size(midpoints); index += stride)
    {
        thinned = append(thinned, midpoints[index]);
    }
    return thinned;
}

//==================================================================
//============================ Emission ============================
//==================================================================

/** The module's wrap form converted to the CLOSED CLAMPED form the kernel takes back, in whichever
    directions are periodic. Split out of kernelSurface so the periodic diagnostic can print exactly
    what is about to be handed to opCreateBSplineSurface rather than an approximation of it.

    @param surface {map} : a normalized surface definition
    @returns {map} */
function closedClampedForEmission(surface is map) returns map
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
    return emitted;
}

/**
 * Build the kernel-facing BSplineSurface. Both non-obvious steps are inherited from
 * editSurface.fs / tweenSurfaces.fs rather than rediscovered:
 *
 * (1) A PERIODIC direction must be emitted in the CLOSED CLAMPED form, not the module's internal
 * wrap-padded one. That is the form the kernel itself returns for a revolve and provably accepts
 * back; handing it a wrap form earns a PERIODIC_BSPLINESURFACE_NOT_SMOOTH. `isPeriodic` stays TRUE
 * through the conversion — it is smoothness metadata about the closure, not a claim about the knot
 * array's shape.
 *
 * (2) The knot arrays need an explicit `is KnotArray` check. FeatureScript's typecheck types do not
 * propagate through array operations, so a knot array that has been through any module function
 * comes back a plain array and bSplineSurface rejects it.
 *
 * @param surface {map} : a normalized, deformed surface definition
 * @returns {BSplineSurface}
 */
function kernelSurface(surface is map) returns BSplineSurface
{
    const emitted = closedClampedForEmission(surface);

    // No "isRational" field: bSplineSurface DERIVES it from whether weights were supplied.
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

/** Create the untrimmed deformed surface and RETURN the id it was created under, so no caller has
    to reconstruct the string.

    @param context {Context}
    @param idGenerator {function} : zero-argument disambiguated id source
    @param surface {map} : the deformed surface definition
    @returns {Id} */
function emitSurface(context is Context, idGenerator is function, surface is map) returns Id
{
    const createId = idGenerator();
    opCreateBSplineSurface(context, createId, { "bSplineSurface" : kernelSurface(surface) });
    return createId;
}

/**
 * Create the deformed surface TRIMMED to the face's own loops, and return the id its sheet was
 * created under.
 *
 * The route is editSurface.fs's, transcribed rather than rediscovered, and
 * EDIT_SURFACE_SPEC.md section 2.3 records what was measured to arrive at it. The two dead ends are
 * worth restating so they are not retried here: outer and inner loops as separate entries of one
 * `boundaryBSplineCurves` array is refused with BSPLINESURFACE_BOUNDARY_NOT_SINGLE_CLOSED_LOOP, and
 * so is the classical keyhole splice, in BOTH orientations — the kernel will not take a self-touching
 * loop. `boundaryBSplineCurves` is the only loop input that exists and it is single-loop by contract;
 * `innerLoopBSplineCurves` is an OUTPUT of evApproximateBSplineSurface and nothing consumes it. Inner
 * loops cannot be carried through a B-spline surface definition at construction time.
 *
 * What the kernel WILL do is hold a multi-loop trimmed face, so the holes are made topologically
 * after construction: build each hole's own patch from its own loop, imprint the patch's EDGES onto
 * the sheet with opSplitFace, delete the patches, then opDeleteFace the enclosed faces with
 * leaveOpen. Edges as `edgeTools`, never the patches as `bodyTools` — that is what keeps this from
 * being a boolean on coincident surfaces. The patches share the sheet's surface exactly, so their
 * edges already lie ON it and the imprint computes nothing; it records a curve the face carries.
 *
 * The face to KEEP is found topologically, not geometrically: it is the one still adjacent to the
 * sheet's original outer boundary edges, captured before the imprint. Everything else on the body is
 * enclosed by an imprinted loop and is a hole. No point-in-polygon, and it holds for a hole of any
 * shape.
 *
 * @param context {Context}
 * @param id {Id} : the feature id, for warnings
 * @param idGenerator {function}
 * @param surface {map} : the deformed surface definition
 * @param sourceRecord {map} : the face's read record, for `outerLoop` and `innerLoops`
 * @returns {Id} : the id the trimmed sheet was created under
 */
function emitTrimmedSurface(context is Context, id is Id, idGenerator is function, surface is map,
    sourceRecord is map) returns Id
{
    const surfaceToCreate = kernelSurface(surface);
    const sheetId = idGenerator();

    if (size(sourceRecord.outerLoop) == 0)
    {
        // A face that is the whole surface has no outer loop to trim with; its holes, if any, still
        // imprint below.
        opCreateBSplineSurface(context, sheetId, { "bSplineSurface" : surfaceToCreate });
    }
    else
    {
        opCreateBSplineSurface(context, sheetId, {
                    "bSplineSurface" : surfaceToCreate,
                    "boundaryBSplineCurves" : sourceRecord.outerLoop
                });
    }

    if (size(sourceRecord.innerLoops) == 0)
    {
        return sheetId;
    }

    // Captured BEFORE the imprint, so they stay attributed to the sheet's own creation while the
    // imprinted edges belong to the split. That difference is what identifies the holes afterwards.
    const outerBoundaryEdges = qCreatedBy(sheetId, EntityType.EDGE);
    const sheetBody = qCreatedBy(sheetId, EntityType.BODY);

    var patchBodies = [];
    for (var loopIndex = 0; loopIndex < size(sourceRecord.innerLoops); loopIndex += 1)
    {
        const patchId = idGenerator();
        try
        {
            opCreateBSplineSurface(context, patchId, {
                        "bSplineSurface" : surfaceToCreate,
                        "boundaryBSplineCurves" : sourceRecord.innerLoops[loopIndex]
                    });
            patchBodies = append(patchBodies, qCreatedBy(patchId, EntityType.BODY));
            opSplitFace(context, idGenerator(), {
                        "faceTargets" : qOwnedByBody(sheetBody, EntityType.FACE),
                        "edgeTools" : qCreatedBy(patchId, EntityType.EDGE)
                    });
        }
        catch (holeError)
        {
            reportFeatureWarning(context, id, "Hole " ~ loopIndex ~ " could not be imprinted on a deformed face, so " ~
                "it is left filled: " ~ holeError);
        }
    }

    if (size(patchBodies) > 0)
    {
        opDeleteBodies(context, idGenerator(), { "entities" : qUnion(patchBodies) });
    }

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
        }
        catch (deleteError)
        {
            reportFeatureWarning(context, id, "Holes were imprinted on a deformed face but the enclosed faces could " ~
                "not be removed, so they are left filled: " ~ deleteError);
        }
    }

    return sheetId;
}

/**
 * Swap the deformed surface underneath the original face, which carries its perimeter, holes and
 * inner loops across for free. Transcribed from editSurface.fs, including both of its hard-won
 * details:
 *
 * The sense is COMPUTED, not guessed by retrying — std replaceFace.fs derives `oppositeSense` from
 * the sign of dot(targetNormal, templateNormal) at each face's parameter midpoint, and this does
 * the same. Only the replace itself is inside the try: an earlier version elsewhere also deleted
 * the template there, so a delete that threw was caught as a REPLACE failure and sent into a
 * flipped retry that then failed because the replace had in fact already worked.
 *
 * @param context {Context}
 * @param id {Id} : the feature id, for warnings
 * @param idGenerator {function}
 * @param face {Query} : the face being replaced
 * @param templateId {Id} : the id the deformed surface was created under
 */
function replaceTargetFace(context is Context, id is Id, idGenerator is function, face is Query, templateId is Id)
{
    const templateFace = qCreatedBy(templateId, EntityType.FACE);

    var oppositeSense = false;
    const targetPlane = try(evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) }));
    const templatePlane = try(evFaceTangentPlane(context, { "face" : templateFace, "parameter" : vector(0.5, 0.5) }));
    if (targetPlane != undefined && templatePlane != undefined)
    {
        oppositeSense = dot(targetPlane.normal, templatePlane.normal) < 0;
    }

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
        // One flipped retry, for a midpoint normal that was degenerate rather than wrong. A
        // fallback for an unreadable measurement, not a substitute for taking one.
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
                ") and with it flipped; the deformed surface has been left in place so it can be inspected. " ~
                "Computed: " ~ firstError ~ " Flipped: " ~ secondError);
        }
    }

    if (replaced)
    {
        try silent
        {
            opDeleteBodies(context, idGenerator(), { "entities" : qCreatedBy(templateId, EntityType.BODY) });
        }
    }
}

//==================================================================
//========================== Manipulators ==========================
//==================================================================

/**
 * The clickable lattice points, plus whichever drag handle the edit mode calls for.
 *
 * The points are shown at their CURRENT positions — offsets and live selection transform included —
 * so the handles are where the lattice actually is. The toggle manipulator is what replaces both old
 * features' single-selection points manipulator, and is the mechanism that made merging them
 * possible at all.
 *
 * When editing is off the points are still drawn, unselected, so that clicking one is a way to TURN
 * editing on. Same affordance editSurface.fs provides.
 *
 * @param context {Context}
 * @param id {Id}
 * @param definition {map}
 * @param lattice {map}
 */
function showLatticeManipulators(context is Context, id is Id, definition is map, lattice is map)
{
    if (!definition.editLattice)
    {
        addManipulators(context, id, {
                    (LATTICE_POINTS_MANIPULATOR) : togglePointsManipulator({
                                "points" : lattice.controlPoints,
                                "selectedIndices" : [],
                                "suppressedIndices" : []
                            })
                });
        return;
    }

    const selectedCells = validSelectedCells(definition.selectedIndices, lattice);
    var selectedFlatIndices = [];
    for (var cell in selectedCells)
    {
        selectedFlatIndices = append(selectedFlatIndices,
            latticeFlatIndex(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue, lattice));
    }

    addManipulators(context, id, {
                (LATTICE_POINTS_MANIPULATOR) : togglePointsManipulator({
                            "points" : lattice.controlPoints,
                            "selectedIndices" : selectedFlatIndices,
                            "suppressedIndices" : []
                        })
            });

    if (size(selectedCells) == 0)
    {
        return;
    }

    // ALWAYS the full triad, never a plain one. A fullTriadManipulator carries translation arrows as
    // well as rotation rings, so a translate-only mode was strictly less capable at no saving, and
    // putting the two behind a mode selector meant the rotation nobody can get at by any other means
    // was hidden behind a dropdown. It also cost a whole class of bug: two manipulators writing the
    // same selection through different storage had to be reconciled whenever the mode changed.
    showTransformManipulator(context, id, definition, lattice, selectedCells);
}

/**
 * The full triad, for translating and rotating the selection about its own centre in the lattice's
 * frame. This is the planes feature's manipulator, now driven by any selection rather than only by
 * a four-point plane.
 *
 * Its base has to be the frame the stored transform was measured in, which means the centroid of
 * the selection's positions BEFORE that transform — grid plus committed offsets. Handing it the
 * current (already transformed) centroid instead would compound the transform against itself on
 * every regeneration.
 *
 * @param context {Context}
 * @param id {Id}
 * @param definition {map}
 * @param lattice {map}
 * @param selectedCells {array}
 */
function showTransformManipulator(context is Context, id is Id, definition is map, lattice is map, selectedCells is array)
{
    const committedPoints = latticePointsWithOffsets(lattice, definition.latticePointOffsets).points;
    const base = selectionBaseCoordSystem(committedPoints, selectedCells, lattice);
    const stored = storedSelectionTransform(definition);

    addManipulators(context, id, {
                (LATTICE_TRANSFORM_MANIPULATOR) : fullTriadManipulator({
                            "base" : base,
                            "transform" : stored == undefined ? identityTransform() : stored,
                            "displayEditView" : true
                        })
            });
}

/**
 * Manipulator change handling.
 *
 * The cheap property worth noticing: the selection and translation branches need NO geometry. The
 * lattice's index layout follows from the span counts alone, and offsets are stored directly, so a
 * drag in TRANSLATE mode never reads a face. Only the TRANSFORM bake does, and only when the
 * selection actually changes — once per selection, not once per drag frame.
 *
 * THIS IS NOT THE LAST WORD ON THE DEFINITION. freeFormDeformationEditLogic runs after this returns,
 * with an `oldDefinition` that predates everything written here — so anything it copies wholesale out
 * of that old state silently reverts this function's work. bakeAgainstPreviousState documents the
 * split that keeps the two in step; it is worth re-reading before adding a branch here.
 *
 * @param context {Context}
 * @param definition {map}
 * @param newManipulators {map}
 * @returns {map} : the updated definition
 */
export function onFreeFormDeformationManipulatorChange(context is Context, definition is map, newManipulators is map) returns map
{
    const counts = latticePointCounts(definition);

    if (newManipulators[LATTICE_POINTS_MANIPULATOR] is map)
    {
        // A deliberate click on a lattice point turns editing on, the same way editSurface.fs and
        // both old FFD features treat it.
        definition.editLattice = true;

        // Any live transform belongs to the selection that is about to be replaced, so it is
        // committed to that selection's offsets before the selection changes underneath it.
        definition = bakeSelectionTransform(context, definition);
        definition.selectedIndices = expandSelection(definition,
            newManipulators[LATTICE_POINTS_MANIPULATOR].selectedIndices, counts);
    }

    if (newManipulators[LATTICE_TRANSFORM_MANIPULATOR] is map)
    {
        // Stored verbatim, in the same decomposition routingCurve.fs uses. It stays live — applied
        // at regeneration by applySelectionTransform — until the selection changes.
        const reported = newManipulators[LATTICE_TRANSFORM_MANIPULATOR].transform;
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

    return definition;
}

/**
 * Commit the live selection transform into per-point offsets and reset it to the identity.
 *
 * This is the counterpart of applySelectionTransform and MUST agree with it point for point — both
 * go through selectionTransformInWorld for exactly that reason. It is the one place in the
 * manipulator path that reads geometry, because the transform acts on world positions and those come
 * from the bounding box; it runs when the selection changes and when the dialog opens, never once per
 * drag frame.
 *
 * AN UNREADABLE LATTICE LEAVES THE TRANSFORM ALONE. A face selection that is empty, not yet
 * resolvable, or in need of the approximation toggle gives no geometry to bake against. The transform
 * is then KEPT rather than reset, because resetting it there does not defer the deformation, it
 * deletes it: the offsets never received it and the live parameters no longer hold it.
 *
 * This used to drop it instead, on the reasoning that a transform belonging to a selection that is
 * about to change should not latch onto the next one. That trade is the wrong way round. Reaching
 * this path at all requires the lattice to be unreadable, and an unreadable lattice is one the
 * feature body cannot regenerate either — so the misapplication it guards against can only happen in
 * a state the user is already being shown an error for, while the work it destroys is real and
 * silent. An empty SELECTION is a different matter and still resets: there is nothing for the
 * transform to act on, so it is dead weight that would otherwise attach itself to the next selection.
 *
 * @param context {Context}
 * @param definition {map}
 * @returns {map} : the updated definition, with the transform reset unless it could not be baked
 */
function bakeSelectionTransform(context is Context, definition is map) returns map
{
    if (storedSelectionTransform(definition) == undefined)
    {
        return definition;
    }

    const lattice = try silent(latticeForManipulatorHandling(context, definition));
    if (lattice == undefined)
    {
        return definition;
    }

    // An EMPTY selection falls through to the reset below rather than returning early: there is
    // nothing for the transform to act on, so it is dead weight, and leaving it live would attach it
    // to whatever gets selected next. selectionTransformInWorld is not called at all in that case —
    // its base is the selection's centroid, which needs at least one point to average.
    const selectedCells = validSelectedCells(definition.selectedIndices, lattice);
    if (size(selectedCells) > 0)
    {
        const worldTransform = selectionTransformInWorld(lattice.controlPoints, selectedCells, lattice, definition);
        for (var cell in selectedCells)
        {
            const flatIndex = latticeFlatIndex(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue, lattice);
            const moved = worldTransform * lattice.controlPoints[flatIndex];
            definition = addToPointOffset(definition, cell.uIndexValue, cell.vIndexValue, cell.nIndexValue,
                moved - lattice.controlPoints[flatIndex]);
        }
    }

    return resetSelectionTransform(definition);
}

/**
 * Rebuild the lattice with committed offsets applied but WITHOUT the live selection transform —
 * the state the stored transform is measured against.
 *
 * `try silent` is appropriate at the call site rather than here: this legitimately throws whenever
 * the face selection is not yet readable, which during interactive editing is an ordinary state and
 * not a diagnosable fault.
 *
 * @param context {Context}
 * @param definition {map}
 * @returns {map} : the lattice
 */
function latticeForManipulatorHandling(context is Context, definition is map) returns map
{
    const faces = evaluateQuery(context, definition.surfacesToDeform);
    const sourceSurfaces = readSourceSurfaces(context, definition, faces);
    const frame = latticeCoordSystem(context, definition);
    var lattice = latticeStructure(frame, controlPointBoundsInFrame(sourceSurfaces, frame),
        definition.latticeSpanCountU, definition.latticeSpanCountV, definition.latticeSpanCountN);
    lattice.controlPoints = latticePointsWithOffsets(lattice, definition.latticePointOffsets).points;
    return lattice;
}

/**
 * Add an increment to one lattice point's stored offset, creating the record if there is none.
 *
 * @param definition {map}
 * @param uIndex {number}
 * @param vIndex {number}
 * @param nIndex {number}
 * @param increment {Vector} : a displacement with length units
 * @returns {map} : the updated definition
 */
function addToPointOffset(definition is map, uIndex is number, vIndex is number, nIndex is number,
    increment is Vector) returns map
{
    for (var offsetIndex = 0; offsetIndex < size(definition.latticePointOffsets); offsetIndex += 1)
    {
        var offsetRecord = definition.latticePointOffsets[offsetIndex];
        if (offsetRecord.uIndex != uIndex || offsetRecord.vIndex != vIndex || offsetRecord.nIndex != nIndex)
        {
            continue;
        }
        offsetRecord.x += increment[0];
        offsetRecord.y += increment[1];
        offsetRecord.z += increment[2];
        definition.latticePointOffsets[offsetIndex] = offsetRecord;
        return definition;
    }

    definition.latticePointOffsets = append(definition.latticePointOffsets, {
                "uIndex" : uIndex,
                "vIndex" : vIndex,
                "nIndex" : nIndex,
                "x" : increment[0],
                "y" : increment[1],
                "z" : increment[2]
            });
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

/**
 * Turn what the toggle manipulator reported into the selection the scope actually implies.
 *
 * The expansion is applied to the DIFFERENCE, never to the whole reported set. Expanding everything
 * would make a plane scope impossible to break out of: deselecting one point of a plane would
 * immediately re-expand it from the points still selected, and the plane could never be reduced.
 * Newly added points expand to their row or plane; newly removed points remove theirs; points that
 * were already selected and still are stay exactly as they were.
 *
 * @param definition {map}
 * @param reportedFlatIndices {array} : what the manipulator reported
 * @param counts {map} : `u`, `v`, `n` point counts, from the span counts alone
 * @returns {array} : selection cells, in lattice index order
 */
function expandSelection(definition is map, reportedFlatIndices is array, counts is map) returns array
{
    // Keyed by cell rather than by flat index, and holding the CELL rather than a flag, so that a
    // cell can be read back out of the leftovers without having to parse its own key.
    var previousCellByKey = {};
    const previousSelection = definition.selectedIndices is array ? definition.selectedIndices : [];
    for (var cell in previousSelection)
    {
        previousCellByKey[cellKey(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue)] = cell;
    }

    var selected = {};
    var addedCells = [];
    for (var flatIndex in reportedFlatIndices)
    {
        const cell = cellFromFlatIndex(flatIndex, counts);
        const key = cellKey(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue);
        selected[key] = true;
        if (previousCellByKey[key] == undefined)
        {
            addedCells = append(addedCells, cell);
        }
        // Clearing the entry marks this cell as accounted for. Cleared keys still appear in keys(),
        // reading back as undefined, which is what the leftover sweep below relies on.
        previousCellByKey[key] = undefined;
    }

    // Whatever is left was selected before and is not now: the user deselected it.
    var removedCells = [];
    for (var key in keys(previousCellByKey))
    {
        if (previousCellByKey[key] != undefined)
        {
            removedCells = append(removedCells, previousCellByKey[key]);
        }
    }

    for (var cell in addedCells)
    {
        for (var member in scopeMembers(cell, definition.selectionScope, counts))
        {
            selected[cellKey(member.uIndexValue, member.vIndexValue, member.nIndexValue)] = true;
        }
    }
    for (var cell in removedCells)
    {
        for (var member in scopeMembers(cell, definition.selectionScope, counts))
        {
            selected[cellKey(member.uIndexValue, member.vIndexValue, member.nIndexValue)] = undefined;
        }
    }

    // Emitted in lattice index order rather than map key order, so the dialog's list is stable and
    // reads down the lattice instead of alphabetically by key.
    var result = [];
    for (var uIndex = 0; uIndex < counts.u; uIndex += 1)
    {
        for (var vIndex = 0; vIndex < counts.v; vIndex += 1)
        {
            for (var nIndex = 0; nIndex < counts.n; nIndex += 1)
            {
                if (selected[cellKey(uIndex, vIndex, nIndex)] == true)
                {
                    result = append(result,
                        { "uIndexValue" : uIndex, "vIndexValue" : vIndex, "nIndexValue" : nIndex });
                }
            }
        }
    }
    return result;
}

/**
 * Every lattice cell one click brings in, under the current scope.
 *
 * A ROW fixes two indices and varies the third; a PLANE fixes one and varies the other two. This
 * function is the whole of what used to be freeFormDeformationPlanes.fs's plane bookkeeping —
 * that feature built a `planeData` array of point-index lists at lattice construction time, which
 * is the same information derived once per click instead of stored.
 *
 * @param cell {map} : the clicked cell
 * @param scope {FFDSelectionScope}
 * @param counts {map} : `u`, `v`, `n` point counts
 * @returns {array} : cells
 */
function scopeMembers(cell is map, scope is FFDSelectionScope, counts is map) returns array
{
    if (scope == FFDSelectionScope.POINT)
    {
        return [cell];
    }

    var members = [];
    for (var uIndex = 0; uIndex < counts.u; uIndex += 1)
    {
        for (var vIndex = 0; vIndex < counts.v; vIndex += 1)
        {
            for (var nIndex = 0; nIndex < counts.n; nIndex += 1)
            {
                if (cellIsInScope(uIndex, vIndex, nIndex, cell, scope))
                {
                    members = append(members,
                        { "uIndexValue" : uIndex, "vIndexValue" : vIndex, "nIndexValue" : nIndex });
                }
            }
        }
    }
    return members;
}

/** Whether a lattice cell belongs to the scope anchored at `cell`.

    @param uIndex {number}
    @param vIndex {number}
    @param nIndex {number}
    @param cell {map} : the anchor cell
    @param scope {FFDSelectionScope}
    @returns {boolean} */
function cellIsInScope(uIndex is number, vIndex is number, nIndex is number, cell is map,
    scope is FFDSelectionScope) returns boolean
{
    if (scope == FFDSelectionScope.ROW_ALONG_U)
    {
        return vIndex == cell.vIndexValue && nIndex == cell.nIndexValue;
    }
    if (scope == FFDSelectionScope.ROW_ALONG_V)
    {
        return uIndex == cell.uIndexValue && nIndex == cell.nIndexValue;
    }
    if (scope == FFDSelectionScope.ROW_ALONG_N)
    {
        return uIndex == cell.uIndexValue && vIndex == cell.vIndexValue;
    }
    if (scope == FFDSelectionScope.PLANE_AT_U)
    {
        return uIndex == cell.uIndexValue;
    }
    if (scope == FFDSelectionScope.PLANE_AT_V)
    {
        return vIndex == cell.vIndexValue;
    }
    if (scope == FFDSelectionScope.PLANE_AT_N)
    {
        return nIndex == cell.nIndexValue;
    }
    return uIndex == cell.uIndexValue && vIndex == cell.vIndexValue && nIndex == cell.nIndexValue;
}

//==================================================================
//=========================== Edit logic ===========================
//==================================================================

/**
 * Editing logic.
 *
 * Every job here is the same job: the live selection transform is about to stop meaning what it
 * meant, so bake it into offsets while the state it was measured against still exists. The stored
 * numbers are relative to a base — the selection's centroid, in the lattice's frame — and ANYTHING
 * that moves that base silently redefines them.
 *
 * Three moments qualify, plus the Planarize button, which arrives as `clickedButton` and does its
 * own bake:
 *
 *   1. THE DIALOG IS OPENED (`oldDefinition == {}`). A transform left live when the dialog was last
 *      closed lives entirely in ALWAYS_HIDDEN parameters, so the surface comes back deformed while
 *      the "Lattice point offsets" list shows nothing that accounts for it. Baking on open is exactly
 *      geometry-preserving — it is the same arithmetic applySelectionTransform was already applying
 *      at every regeneration, through the same selectionTransformInWorld — so the only thing that
 *      changes is that the deformation becomes VISIBLE and editable as offsets.
 *
 *   2. THE SELECTION CHANGES through the dialog. The manipulator path has its own bake in
 *      onFreeFormDeformationManipulatorChange; this covers the array being edited by hand. It is why
 *      the array's reordering is turned off — the bake is positional.
 *
 *   3. THE LATTICE ITSELF CHANGES. Span counts, orientation and the face selection all move the grid
 *      the base is computed from, so a transform surviving one of those describes a different
 *      displacement afterwards than it did before.
 *
 * There used to be a fourth case, an edit-mode change, back when a translate-only manipulator could
 * take over from the full triad while a transform was still live. Making the full triad
 * unconditional deleted the mode and the whole class of bug with it: one manipulator now owns the
 * selection, so there is no second writer to reconcile against.
 *
 * @param context {Context}
 * @param id {Id}
 * @param oldDefinition {map}
 * @param definition {map}
 * @param isCreating {boolean}
 * @returns {map} : the updated definition
 */
export function freeFormDeformationEditLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    isCreating is boolean, clickedButton is string) returns map
{
    if (!definition.editLattice)
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

    // Cases 2 and 3.
    if (!selectionsMatch(oldDefinition.selectedIndices, definition.selectedIndices) ||
        latticeDefinitionChanged(context, oldDefinition, definition))
    {
        return bakeAgainstPreviousState(context, oldDefinition, definition);
    }
    return definition;
}

/**
 * Bake the live selection transform against the state it was authored in, and return the CURRENT
 * definition carrying the result with the transform cleared.
 *
 * Which definition supplies what is the entire content of this function, and getting it wrong is how
 * offsets go missing:
 *
 *   - the OLD definition supplies the SELECTION and the lattice geometry — span counts, orientation,
 *     faces — because those are what the stored numbers are measured against;
 *   - the CURRENT definition supplies the OFFSETS, because something may already have written to
 *     them this edit round and rolling that back would destroy it.
 *
 * The second point is not hypothetical, and it was the bug: onFreeFormDeformationManipulatorChange
 * bakes on its own before it swaps the selection in, and this editing logic then runs on top of that
 * with an `oldDefinition` predating the bake. Sourcing offsets from `oldDefinition` therefore
 * overwrote the freshly baked array with the pre-bake one every single time a selection change
 * arrived through the manipulator — the drag vanished from the list AND from the geometry, because
 * the manipulator had already reset the live transform that was carrying it.
 *
 * When the manipulator has already baked, the transform copied across is the identity, so
 * bakeSelectionTransform short-circuits and this is a no-op on the offsets it just wrote. That is
 * the intended interaction between the two paths rather than a coincidence.
 *
 * @param context {Context}
 * @param oldDefinition {map} : the state the transform was measured against
 * @param definition {map} : the current state, whose offsets are authoritative
 * @returns {map} : the current definition, offsets baked and transform reset — or untouched and the
 *                  transform still live, if the old lattice could not be read
 */
function bakeAgainstPreviousState(context is Context, oldDefinition is map, definition is map) returns map
{
    var previous = oldDefinition;
    previous.latticePointOffsets = definition.latticePointOffsets;
    previous.selectionRotation = definition.selectionRotation;
    previous.selectionTranslateX = definition.selectionTranslateX;
    previous.selectionTranslateY = definition.selectionTranslateY;
    previous.selectionTranslateZ = definition.selectionTranslateZ;

    const bakedPrevious = bakeSelectionTransform(context, previous);
    definition.latticePointOffsets = bakedPrevious.latticePointOffsets;

    // A transform still live on the way back out means the bake could not run — see
    // bakeSelectionTransform, which keeps it rather than deleting it. Resetting here would undo that
    // and lose the drag. It takes an unreadable OLD lattice to get here, and the old lattice is the
    // one that regenerated a moment ago, so in practice this is the already-erroring feature.
    if (storedSelectionTransform(bakedPrevious) != undefined)
    {
        return definition;
    }
    return resetSelectionTransform(definition);
}

/**
 * Whether anything that DEFINES the lattice has changed, and so whether a live selection transform
 * has stopped describing the displacement it described before.
 *
 * The base the transform is measured against is the selection's centroid in the lattice frame, and
 * every input listed here feeds it: the span counts decide where the grid points are, the
 * orientation decides the frame, and the face selection and its reading mode decide the bounding box
 * the grid is laid out inside. `result` is in the list because NEW_BODY_TRIMMED reads faces through
 * evApproximateBSplineSurface rather than evSurfaceDefinition, which is a different control net and
 * therefore a different box.
 *
 * @param context {Context}
 * @param oldDefinition {map}
 * @param definition {map}
 * @returns {boolean}
 */
function latticeDefinitionChanged(context is Context, oldDefinition is map, definition is map) returns boolean
{
    if (definition.latticeSpanCountU != oldDefinition.latticeSpanCountU ||
        definition.latticeSpanCountV != oldDefinition.latticeSpanCountV ||
        definition.latticeSpanCountN != oldDefinition.latticeSpanCountN ||
        definition.orientLattice != oldDefinition.orientLattice ||
        definition.approximate != oldDefinition.approximate ||
        definition.result != oldDefinition.result)
    {
        return true;
    }
    // Guarded rather than compared unconditionally: `latticeOrientation` is declared inside
    // `if (definition.orientLattice)`, so it is legitimately absent, and areQueriesEquivalent needs
    // two actual Queries. The toggle itself is already covered above.
    if (definition.orientLattice &&
        definition.latticeOrientation is Query && oldDefinition.latticeOrientation is Query &&
        !areQueriesEquivalent(context, definition.latticeOrientation, oldDefinition.latticeOrientation))
    {
        return true;
    }
    return definition.surfacesToDeform is Query && oldDefinition.surfacesToDeform is Query &&
        !areQueriesEquivalent(context, definition.surfacesToDeform, oldDefinition.surfacesToDeform);
}

/**
 * Flatten the selected lattice points onto the least-squares plane through them.
 *
 * A ONE-SHOT EDIT, not a constraint. It writes ordinary per-point offsets through the same
 * addToPointOffset every drag uses, so the result is indistinguishable from having dragged the
 * points there by hand and nothing keeps them coplanar afterwards. That is the honest behaviour for
 * a button: a persistent planar constraint would have to survive lattice resolution changes and
 * fight every subsequent drag, which is a different feature.
 *
 * The live selection transform is BAKED FIRST, because the button acts on where the points visibly
 * ARE and the transform is part of that. Baking also means the projection it writes cannot be
 * silently re-transformed on the next regeneration.
 *
 * DEGENERATE SELECTIONS NEED NO SPECIAL CASE. For collinear points the covariance matrix has rank 1,
 * so the fitted normal is perpendicular to the line and the plane therefore CONTAINS it — every
 * point is already on the plane and the projection moves nothing. A row scope pressing this button
 * is a no-op rather than an error, which is the right answer: a row is already as planar as a line
 * can be. Only a selection too small to span a plane at all is rejected outright.
 *
 * @param context {Context}
 * @param definition {map}
 * @returns {map} : the updated definition
 */
function planarizeSelection(context is Context, definition is map) returns map
{
    var baked = bakeSelectionTransform(context, definition);

    // Same `try silent` reasoning as bakeSelectionTransform: an unreadable face selection is an
    // ordinary interactive state, and the button doing nothing is better than the dialog throwing.
    const lattice = try silent(latticeForManipulatorHandling(context, baked));
    if (lattice == undefined)
    {
        return baked;
    }

    const selectedCells = validSelectedCells(baked.selectedIndices, lattice);
    if (size(selectedCells) < 3)
    {
        return baked;
    }

    var points = [];
    for (var cell in selectedCells)
    {
        points = append(points,
            lattice.controlPoints[latticeFlatIndex(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue, lattice)]);
    }

    const fitted = leastSquaresPlane(points);
    for (var cellIndex = 0; cellIndex < size(selectedCells); cellIndex += 1)
    {
        const cell = selectedCells[cellIndex];
        // The signed distance to the plane, removed along the normal — the shortest move that lands
        // the point on it, so the selection keeps its shape in plan as far as flattening allows.
        const displacement = -dot(points[cellIndex] - fitted.origin, fitted.normal) * fitted.normal;
        baked = addToPointOffset(baked, cell.uIndexValue, cell.vIndexValue, cell.nIndexValue, displacement);
    }
    return baked;
}

/**
 * The least-squares plane through a set of points: centroid for the origin, and for the normal the
 * eigenvector of the smallest eigenvalue of the covariance matrix `sum (p - o)(p - o)^t`.
 *
 * The derivation and the SVD route to it are std's own, in `editCurve.fs`'s `fitPlane` — minimizing
 * `sum (n . (p - o))^2` subject to `n . n = 1` makes `n` an eigenvector of that matrix by Lagrange
 * multipliers, and the smallest eigenvalue is the one that minimizes rather than maximizes. `fitPlane`
 * is private to editCurve.fs, so this is a transcription rather than a call; the SVD orders singular
 * values largest first, hence the LAST row of `transpose(u)`.
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
            first[index].vIndexValue != second[index].vIndexValue ||
            first[index].nIndexValue != second[index].nIndexValue)
        {
            return false;
        }
    }
    return true;
}

//==================================================================
//=========================== Diagnostics ==========================
//==================================================================

/**
 * Draw the lattice cage: every edge between adjacent lattice points, coloured by direction.
 *
 * MAGENTA along U, CYAN along V, YELLOW along N. Magenta and cyan are Onshape's own U/V
 * isoparametric colours, which editSurface.fs's control net already follows; yellow is this
 * repository's convention for the third direction, which the product has no analog for. Colour is
 * the only cue distinguishing the three directions once the cage has been deformed out of its
 * axis-aligned starting shape, so it is worth being consistent about.
 *
 * @param context {Context}
 * @param lattice {map}
 */
function showLatticeCage(context is Context, lattice is map)
{
    for (var indexU = 0; indexU < lattice.pointCountU; indexU += 1)
    {
        for (var indexV = 0; indexV < lattice.pointCountV; indexV += 1)
        {
            for (var indexN = 0; indexN < lattice.pointCountN; indexN += 1)
            {
                const point = lattice.controlPoints[latticeFlatIndex(indexU, indexV, indexN, lattice)];

                if (indexU + 1 < lattice.pointCountU)
                {
                    addDebugLine(context, point,
                        lattice.controlPoints[latticeFlatIndex(indexU + 1, indexV, indexN, lattice)],
                        DebugColor.MAGENTA); // along U
                }
                if (indexV + 1 < lattice.pointCountV)
                {
                    addDebugLine(context, point,
                        lattice.controlPoints[latticeFlatIndex(indexU, indexV + 1, indexN, lattice)],
                        DebugColor.CYAN); // along V
                }
                if (indexN + 1 < lattice.pointCountN)
                {
                    addDebugLine(context, point,
                        lattice.controlPoints[latticeFlatIndex(indexU, indexV, indexN + 1, lattice)],
                        DebugColor.YELLOW); // along N
                }
            }
        }
    }
}

/**
 * Draw the deformed control net, in the same U/V colours editSurface.fs uses, so a refinement level
 * can be read off the screen rather than inferred from the reported counts.
 *
 * EXPECT THIS TO BE ONE OF THE MOST EXPENSIVE THINGS THE FEATURE DOES when it is turned on, and
 * understand why before trying to fix it: the net it draws is the REFINED one, so it emits close to
 * two debug lines per control point — a 60x60 net is about seven thousand debug entities, each an
 * individual call. std offers no polyline debug entity to batch them into, so the cost is
 * irreducible at this resolution; what is reducible is the per-cell bookkeeping around it, which is
 * why the rows are hoisted below rather than re-indexed four times per point.
 *
 * The practical consequence, which belongs in a profile reading rather than in the code: a
 * regeneration timed with "Show deformed control net" on is not measuring the deformation. Turn it
 * off before drawing conclusions about anything else.
 *
 * @param context {Context}
 * @param surface {map} : a deformed surface definition
 */
function showDeformedControlNet(context is Context, surface is map)
{
    const grid = surface.controlPoints;
    const rowCount = size(grid);
    const columnCount = size(grid[0]);
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
    {
        const row = grid[rowIndex];
        const nextRow = rowIndex + 1 < rowCount ? grid[rowIndex + 1] : undefined;
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex += 1)
        {
            const point = row[columnIndex];
            if (nextRow != undefined && !tolerantEquals(point, nextRow[columnIndex]))
            {
                addDebugLine(context, point, nextRow[columnIndex], DebugColor.MAGENTA);
            }
            if (columnIndex + 1 < columnCount && !tolerantEquals(point, row[columnIndex + 1]))
            {
                addDebugLine(context, point, row[columnIndex + 1], DebugColor.CYAN);
            }
        }
    }
}

/**
 * Console dump of one surface's structural shape: degrees, periodicity, control point counts, and
 * the knot vectors with each distinct knot's MULTIPLICITY spelled out.
 *
 * Multiplicity is the number that matters and the one a raw knot dump hides. A direction is
 * C^(degree - multiplicity) at each interior knot, so a knot at multiplicity == degree is only C0
 * there — and on a PERIODIC seam that is the difference between a surface the kernel accepts and
 * PERIODIC_BSPLINESURFACE_NOT_SMOOTH. Printed at every stage of the pipeline so the stage that
 * changes it can be identified rather than guessed at.
 *
 * @param label {string} : which pipeline stage this is
 * @param surface {map} : any surface definition, raw or normalized
 */
function printSurfaceStructure(label is string, surface is map)
{
    println("  --- " ~ label ~ " ---");
    println("    uDegree " ~ surface.uDegree ~ ", vDegree " ~ surface.vDegree ~
        ", isUPeriodic " ~ (surface.isUPeriodic == true) ~ ", isVPeriodic " ~ (surface.isVPeriodic == true));
    println("    control points " ~ size(surface.controlPoints) ~ " x " ~ size(surface.controlPoints[0]) ~
        ", rational " ~ (surface.weights != undefined));
    println("    U knots (" ~ size(surface.uKnots) ~ "): " ~ describeKnotMultiplicities(surface.uKnots));
    println("    V knots (" ~ size(surface.vKnots) ~ "): " ~ describeKnotMultiplicities(surface.vKnots));
}

/** A knot vector as `value x multiplicity` runs, which is the form the smoothness question is
    actually asked in.

    @param knots {array}
    @returns {string} */
function describeKnotMultiplicities(knots is array) returns string
{
    var description = "";
    var index = 0;
    while (index < size(knots))
    {
        var multiplicity = 1;
        while (index + multiplicity < size(knots) &&
            abs(knots[index + multiplicity] - knots[index]) <= KNOT_PARAMETER_TOLERANCE)
        {
            multiplicity += 1;
        }
        description ~= (description == "" ? "" : ", ") ~ roundToPrecision(knots[index], 6) ~ " x" ~ multiplicity;
        index += multiplicity;
    }
    return description;
}

/** Console dump of the lattice's shape and framing.

    @param lattice {map} */
function printLatticeInformation(lattice is map)
{
    println("=== FFD lattice ===");
    println("  Origin (lattice parameters 0,0,0): " ~ toString(lattice.origin));
    println("  Axis U (magenta): " ~ toString(lattice.axisU));
    println("  Axis V (cyan):    " ~ toString(lattice.axisV));
    println("  Axis N (yellow):  " ~ toString(lattice.axisN));
    println("  Spans: " ~ lattice.spanCountU ~ " x " ~ lattice.spanCountV ~ " x " ~ lattice.spanCountN);
    println("  Points: " ~ lattice.pointCountU ~ " x " ~ lattice.pointCountV ~ " x " ~ lattice.pointCountN ~
        " = " ~ lattice.totalPointCount);
    println("  Box diagonal: " ~ toString(lattice.diagonal));
    if (size(lattice.inflatedDirections) > 0)
    {
        println("  Inflated degenerate direction(s): " ~ toString(lattice.inflatedDirections) ~
            " - the input is flat there, so the lattice was given thickness and the surface sits at mid-parameter.");
    }
    println("===================");
}

/** Console dump of what the section 9.1.1 loop actually did for one face.

    @param converged {map} : deformToTolerance's result
    @param certifiedDeviation {ValueWithUnits}
    @param tolerance {ValueWithUnits} */
function printRefinementDetails(converged is map, certifiedDeviation is ValueWithUnits, tolerance is ValueWithUnits)
{
    println("=== FFD refinement ===");
    println("  Passes: " ~ converged.passes ~ (converged.budgetHit ? " (stopped at a cap)" : ""));
    println("  Control net: " ~ size(converged.undeformed.controlPoints) ~ " x " ~
        size(converged.undeformed.controlPoints[0]) ~ " at degrees " ~ converged.undeformed.uDegree ~ ", " ~
        converged.undeformed.vDegree);
    println("  Control-net delta (drove the loop): " ~ toString(converged.delta));
    println("  Certified deviation (kernel):       " ~ toString(certifiedDeviation));
    println("  Tolerance:                          " ~ toString(tolerance));
    println("======================");
}

//==================================================================
//============================ Utilities ===========================
//==================================================================

/**
 * Lattice point counts derived from the span counts ALONE.
 *
 * This is what lets the manipulator handler map flat indices to cells without reading a single
 * face: the index layout is a function of the dialog's numbers, not of any geometry.
 *
 * @param definition {map}
 * @returns {map} : `u`, `v`, `n`
 */
function latticePointCounts(definition is map) returns map
{
    return {
            "u" : definition.latticeSpanCountU + 1,
            "v" : definition.latticeSpanCountV + 1,
            "n" : definition.latticeSpanCountN + 1
        };
}

/**
 * Lattice cell to flat array index. N varies fastest, then V, then U.
 *
 * This packing and cellFromFlatIndex are inverses, and are the only two places allowed to know it.
 *
 * @param uIndex {number}
 * @param vIndex {number}
 * @param nIndex {number}
 * @param lattice {map}
 * @returns {number}
 */
function latticeFlatIndex(uIndex is number, vIndex is number, nIndex is number, lattice is map) returns number
{
    return uIndex * lattice.pointCountV * lattice.pointCountN + vIndex * lattice.pointCountN + nIndex;
}

/** Flat array index back to a lattice cell. The inverse of latticeFlatIndex.

    @param flatIndex {number}
    @param counts {map} : `u`, `v`, `n`
    @returns {map} : `uIndexValue`, `vIndexValue`, `nIndexValue` */
function cellFromFlatIndex(flatIndex is number, counts is map) returns map
{
    const uIndex = floor(flatIndex / (counts.v * counts.n));
    const remainder = flatIndex - uIndex * counts.v * counts.n;
    const vIndex = floor(remainder / counts.n);
    return {
            "uIndexValue" : uIndex,
            "vIndexValue" : vIndex,
            "nIndexValue" : remainder - vIndex * counts.n
        };
}

/** A map key for a lattice cell. FeatureScript maps want a scalar key and a cell is three numbers.

    @param uIndex {number}
    @param vIndex {number}
    @param nIndex {number}
    @returns {string} */
function cellKey(uIndex is number, vIndex is number, nIndex is number) returns string
{
    return uIndex ~ "," ~ vIndex ~ "," ~ nIndex;
}

/** Whether a cell exists in the current lattice.

    @param uIndex {number}
    @param vIndex {number}
    @param nIndex {number}
    @param lattice {map}
    @returns {boolean} */
function cellIsInLattice(uIndex is number, vIndex is number, nIndex is number, lattice is map) returns boolean
{
    return uIndex >= 0 && uIndex < lattice.pointCountU &&
        vIndex >= 0 && vIndex < lattice.pointCountV &&
        nIndex >= 0 && nIndex < lattice.pointCountN;
}

/**
 * The selection, filtered to cells the current lattice actually has, and de-duplicated.
 *
 * Silent rather than loud: unlike a stale OFFSET, which is stored work the user would want to hear
 * about losing, a stale SELECTION is transient state with nothing in it worth recovering.
 *
 * The parameter is untyped because `selectedIndices` is declared inside a driven group, so it is
 * legitimately absent from a definition whose editing group has never been opened.
 *
 * @param selectedIndices {array} : the definition's selection, or `undefined`
 * @param lattice {map}
 * @returns {array} : cells
 */
function validSelectedCells(selectedIndices, lattice is map) returns array
{
    if (!(selectedIndices is array))
    {
        return [];
    }
    var seen = {};
    var cells = [];
    for (var cell in selectedIndices)
    {
        if (!cellIsInLattice(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue, lattice))
        {
            continue;
        }
        const key = cellKey(cell.uIndexValue, cell.vIndexValue, cell.nIndexValue);
        if (seen[key] == true)
        {
            continue;
        }
        seen[key] = true;
        cells = append(cells, cell);
    }
    return cells;
}
