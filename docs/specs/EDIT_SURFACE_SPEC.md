# Edit Surface — Feature Spec

Status (2026-08-08): **design stage, nothing built.** A direct-manipulation editor for a
B-spline surface's control net, the surface analog of std `editCurve.fs`, using the multi-point
editing manipulators `routingCurve.fs` established as its UX template.

Proposed as a deliberate detour from the [SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md)
§9.1 deformation feature. The detour pays for itself three ways — it is a useful tool on its own
terms, it is the first consumer of four module entry points that currently have none, and it
proves §9.1's steps 4–5 (the `opCreateBSplineSurface` → `opReplaceFace` round trip) with a
trivial "map" before the deformation feature depends on them with a hard one.

Related: [SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) (the module this
is built on and, not incidentally, the thing it tests), [T_SPLINE_SUPPORT_SPEC.md](T_SPLINE_SUPPORT_SPEC.md)
(§4's authoring story eventually wants exactly this UI over a T-mesh),
[DISPLACEMENT_MAP_TILING_SPEC.md](DISPLACEMENT_MAP_TILING_SPEC.md) (the working precedent for the
`opReplaceFace` round trip).

---

## 1. What this tests that the tweens structurally cannot

The module is 200+ tester checks green and both tween features are live. That is a narrower
guarantee than it sounds, and the gap is worth stating precisely, because it is the actual
argument for building this now rather than after §9.1.

**1.1 The tweens never emit onto an existing face.** Both call `opCreateBSplineSurface` and stop:
they produce a new surface body. Nothing in this repo has ever asked the module for a definition
that `opReplaceFace` will accept as a template for a *trimmed* face — §9.1's steps 4 and 5, and
the single thing that makes the deformation feature better than `deformPascoe.fs`, whose own
author admitted `opCreateBSplineSurface` "cannot recreate those holes". `displacementMap.fs`
proves the round trip works, but with surfaces it builds itself from a tiled unit cell, never
from a module-normalized definition carrying periodic flags and rebuilt knot vectors.

**1.2 A tween's output inherits its inputs' smoothness. A dragged control point does not.** This
is the sharp one. Tweening is a convex combination of two valid surfaces over a shared basis, so
the result is smooth *because both inputs were* — a seam that was G1 on both sides stays G1 under
any blend fraction. Every test the periodic emission path has ever seen has been pre-smoothed
data of exactly that kind. A user dragging one control point of a cylinder can hand
`toClosedClampedSurfaceDirection` a net that no blend could ever produce. The kernel's two
periodic rejections (`BSPLINESURFACE_NOT_G1`, `PERIODIC_BSPLINESURFACE_NOT_SMOOTH`, §2.3) are
about exactly this, and we have never deliberately provoked either one from the emission side.

**1.3 Four entry points have zero consumers.** `extractSubSurface`,
`decomposeSurfaceIntoBezierPatches`, `refineSurfaceToSpanDensity`, and
`prepareSurfaceForDeformation` are tester-green and otherwise dead code. Edit Surface's
"add control points", "raise degree" and "edit within a region" actions *are* those four
functions with a human driving them instead of a convergence loop — which is a better first
consumer than the loop, because a human notices a wrong answer immediately and a loop hides it
inside a tolerance.

**1.4 The derivative layer gets its first consumer.** `editCurve.fs` has
`getCurvatureFrameAtControlPointIndex` to orient its UVN manipulator per control point. The
surface analog is `evaluateBSplineSurfaceNormal` / `evaluateBSplineSurfaceCurvature` evaluated at
each control point's Greville abscissa — which is precisely the normal that flow-along-surface's
offset step will depend on, checked by eye on every single drag instead of once at the end of a
projection loop.

**1.5 Greville abscissae in two dimensions do not exist yet.** `editCurve.fs` has
`grevilleParameter`; the tensor-product version is where a control point's manipulator should
sit, and it is also how §9.1.1's directional adaptivity localizes error to a span. Unbuilt.

---

## 2. The pipeline

Five steps, of which steps 1, 4 and 5 are shared verbatim with §9.1 and step 3 is the only
difference:

1. **Read the surface.** `evSurfaceDefinition` when the face is already a B-spline — exact, no
   approximation. `evApproximateBSplineSurface` otherwise, behind an explicit toggle that says so,
   with the tolerance surfaced in the dialog. This mirrors `editCurve.fs`'s `approximate`
   parameter and its `inputCanBeModified` check, and it keeps the module's standing rule: never
   silently approximate.
2. **Prepare.** `elevateSurfaceDegrees` then `refineSurfaceToControlPointCounts` (or
   `refineSurfaceToSpanDensity` for the targeted case) — i.e. `prepareSurfaceForDeformation`.
   Exact, geometry unchanged, and the user's requested handle density.
3. **Edit.** Apply the control point offsets and weight overrides. The only step that moves
   anything, and the analog of §9.1's deformation map — except the "map" here is a lookup table
   the user typed with a triad instead of a `Vector -> Vector` function.
4. **`opCreateBSplineSurface`** the untrimmed result.
5. **`opReplaceFace`** onto the original face, which carries its trim loops, holes and inner
   loops over for free.

Step 5 needs `displacementMap.fs`'s hard-won detail, not a fresh implementation: the
try-then-retry-with-`oppositeSense` pattern, using **plain `try`/`catch` and never `try silent`**
(which swallows the first failure without running the catch, so the retry never fires and the
template gets deleted anyway — the long-standing "replace face does nothing" bug), and a
**distinct id for the retry**, since a thrown op still registers its id.

### 2.1 Trim preservation has exactly one viable mechanism

Confirmed against the kernel API and against a live E1 run (2026-08-08), because the obvious
alternative looks like it works and then silently drops holes.

`opCreateBSplineSurface` accepts a `boundaryBSplineCurves` field, and it is documented as taking a
boundary that **"must form a single closed loop on the surface"**. One loop. There is no
inner-loop parameter. So a face with a hole cannot be expressed through that call at all: a
prototype that passes the trim curves reproduces the perimeter and drops every hole, which is not a
bug in the prototype but the documented ceiling of the call. `deformPascoe.fs` hit the identical
wall and left a comment saying so at its line 5176.

There is a second reason to avoid the trim-curve route even where a single loop would suffice. Those
curves are 2D splines in the surface's **parameter** space, and composing one with the surface map
does not yield a low-degree B-spline — the exact composition is a much higher-degree spline. Any 3D
trim curve built from them by sampling is an approximation, of exactly the kind this project keeps
refusing.

**So: `opReplaceFace` is the mechanism, and it is the only one.** It never rebuilds topology — it
substitutes the surface underneath loops that already exist — so perimeter, holes and inner loops
all survive by construction rather than by reconstruction.

**`opReplaceFace` also requires its TARGET to be a face on a real body, not an extracted copy of
one.** This was tried and it fails — worth recording so it is not retried. Serving the "new surface
body" result by `opExtractSurface`-ing a trimmed copy of the face and replacing the surface
underneath *that*, so one mechanism could cover both modes, gets half way: the extraction genuinely
does carry holes over. The replace then fails in **both** senses with
`DIRECT_EDIT_REPLACE_FACE_FAILED`, and structurally so — `opReplaceFace` is a *direct edit*, which
re-derives the replaced face's boundary by re-intersecting it with the faces around it. A
single-face sheet body's edges are all **free**: there are no neighbours to intersect against, so
there is nothing to heal the boundary with. Not a tuning problem.

So the "new surface body" result does not get trimmed at all, and that is the right answer rather
than a concession. `opCreateBSplineSurface` produces the **full, untrimmed** surface, which is
precisely the surface the control net describes. A trimmed view would hide the part an editor most
needs to show, because the net extends past the trim boundary and — from E3 onward — so do the
handles that drive it. The two modes are therefore different outputs: trimmed-in-place, or the whole
underlying surface. Output defaults to replacing in place, per `editCurve.fs`.

### 2.3 The trim probe's results (2026-08-09)

Run against a planar face with one elliptical hole, in `NEW_BODY_TRIMMED` mode:

- **Outer loops trim correctly.** The core architectural bet holds: extracted UV loops fed straight
  back to `opCreateBSplineSurface` reproduce the perimeter.
- **Multi-loop is refused, and the error names the rule**:
  `BSPLINESURFACE_BOUNDARY_NOT_SINGLE_CLOSED_LOOP`. Documented behaviour, now measured. Do not
  retry passing outer and inner loops as separate entries of one array.
- **Inner loops build as their own patches and draw in the right place, but nothing subtracts them** —
  expected, since creating a patch is not cutting a hole.

**The bridged (keyhole) loop was tried and is dead.** Splicing outer and inner into one closed loop
with a bridge — a straight UV segment out to the hole, around it, back along the same segment — was
refused with `BSPLINESURFACE_BOUNDARY_NOT_SINGLE_CLOSED_LOOP` in **both** orientations. Not an
orientation problem: the kernel will not accept a self-touching loop. Do not revive it.

**There is no third door.** `boundaryBSplineCurves` is the only loop input in the entire operation
surface; it is single-loop by contract; and `innerLoopBSplineCurves` exists only as an *output* of
`evApproximateBSplineSurface`. `opCreateCurvesOnFace` is isoparametric-only and cannot take arbitrary
loops. **Inner loops cannot be carried through a B-spline surface definition at construction time** —
a fact about the API, not a gap in effort.

### 2.3.1 Holes: imprint and remove, still no boolean

The kernel will happily **hold** a multi-loop trimmed face; it just will not **build** one from
curves. So the hole is made topologically, after construction:

1. build the outer-trimmed sheet;
2. build each hole's own patch from that hole's loop (legal — one closed loop each);
3. **imprint** each patch's boundary *edges* onto the outer sheet with `opSplitFace`;
4. delete the patch bodies, which were only ever tools;
5. `opDeleteFace` the enclosed face with `leaveOpen : true`, leaving a genuine hole.

Step 3 passes the patches' **edges** as `edgeTools`, never the patches as `bodyTools`, and that
distinction is the whole reason this is not a boolean. The patches share their surface with the
target *exactly*, so intersecting them as bodies is a coincident-surface intersection — degenerate
and fragile. Their edges already lie exactly **on** the target face, because the kernel built them
from these very UV curves against this very surface, so the imprint computes nothing; it records a
curve the face already carries.

The face to keep is identified topologically: it is the one still carrying the sheet's *original*
outer boundary edges, captured before the imprint (the imprinted edges belong to the split op
instead). Everything else on the body is enclosed by an imprinted loop and is a hole. No
point-in-polygon, no area comparison, and it holds for a hole of any shape, convex or not.

**On the earlier claim that a boolean would cost the invariance property — that was overstated and is
withdrawn.** Features recompute from scratch every regeneration, so "redoing it per deformation" is
not an extra cost for any of these routes; it is just the build. What invariance actually buys is
that the trim *definition* — the UV loops — never needs recomputing, and that holds equally here.
The real reason to prefer imprint over boolean is robustness on coincident geometry, which is a
better reason and a narrower claim.

### 2.4 `evApproximateBSplineSurface`'s tolerance range is narrower than std's bound

Its tolerance is documented as **minimum 1e-8 m, maximum 1e-4 m**, and it throws outside that. std's
`TOLERANCE_BOUND` (which `editCurve.fs` uses) allows up to **1 metre** — correct for curve
approximation, and here it lets a user dial in a value the kernel will reject. Edit Surface uses its
own `SURFACE_APPROXIMATION_TOLERANCE_BOUND` with the real ceiling.

### 2.5 Degree and control point count are separate levers and both are needed

`editCurve.fs` exposes `approximationMaxCPs` alongside its target degree. The surface analog cannot
be an approximation input — `evApproximateBSplineSurface` has no control point parameter — so it is
a **Refine** step instead, `refineSurfaceToControlPointCounts` via `prepareSurfaceForDeformation`.
That is strictly better than re-approximating: refinement is exact, so it adds handles without
moving the surface. The `approximationMaxCPs` field is kept for UI fidelity and is the **merge's**
budget — spent as a target and enforced as a cap, since the merge is the only stage where the feature
itself chooses a count. See §5A.10 for the full knob model, and for the reverted attempt to make it a
universal ceiling.

The two are not interchangeable and neither substitutes for the other. **Degree sets the continuity
ceiling** — no amount of refinement makes a degree-1 surface smoother than C⁰, so a flat sheet read
at degree 1 stays faceted however many control points it gets (§9.1.1's rule, met in practice).
**Count sets the detail** — elevation alone on a flat sheet resolves to a handful of control points,
which is not enough to sculpt with. Order is elevate-then-refine, since elevating afterwards
multiplies the count for nothing.

One asymmetry worth stating: refinement only ever **adds**. Asking for fewer control points than the
input carries is not refinement but approximation, and belongs to the tolerance.

**Still open after the first E1 run.** The observed failure is consistent with the free-edge
explanation above, but that run only exercised the extracted-copy target, so one competing
explanation is not yet excluded: that `opReplaceFace` balks when the template is *geometrically
identical* to the target, which is E1's whole premise and never the case in `displacementMap.fs`
(whose template is displaced by construction). The two are separated by a single test — run
REPLACE_FACE mode against a face on a solid. Success confirms the free-edge reading; failure means
E1 cannot be verified through `opReplaceFace` at all and needs `evPointsDeviation` between the
rebuilt definition and the original face as its instrument instead.

### 2.2 Expect `surfaceType` to say non-spline more often than seems reasonable

`evSurfaceDefinition().surfaceType` reports how the kernel **stores** a surface, not what shape it
is. An extruded or revolved spline profile is exactly a B-spline surface mathematically and still
comes back `EXTRUDED` or `REVOLVED`, so it fails the "is it a spline" gate and needs the
approximation toggle — observed immediately in E1 testing. Those cases approximate essentially
perfectly, because the target genuinely is a B-spline and the approximator is only being asked to
rediscover it.

The gate stays opt-in (the standing no-silent-approximation rule, and `editCurve.fs` does the same),
but the error names the type it actually got. "This face is stored as EXTRUDED" is something a user
can reason about; "not a spline" just reads as a refusal.

---

## 3. UX: the Routing Curve template

`routingCurve.fs` (and `editCurve.fs`, which has the same machinery) establishes the pattern:

| Piece | Call | Role |
| --- | --- | --- |
| Selection | `togglePointsManipulator({points, selectedIndices, suppressedIndices})` | Click control points to toggle them in and out of the selection |
| Transform | `fullTriadManipulator({base, transform, displayEditView : true})` | One triad at the selection's centroid, dragging the whole selection |
| Storage | `selectedIndices` array param, `UIHint : [INITIAL_FOCUS, PREVENT_ARRAY_REORDER, ALLOW_ARRAY_FOCUS]` | Legible, focusable list of what is selected |
| Triad state | hidden `multiDx` / `multiDy` / `multiDz` / `multiRotationMatrix` | Where the triad sits and how it is oriented between drags |
| Handler | `multiTriadManipulatorHandler`'s shape | Recompute the triad's old position, read its new one, apply the delta per point |

**One deliberate extension over `routingCurve.fs`.** Its multi-triad stores the rotation
(`definition.multiRotationMatrix = transpose(manipulator.transform.linear)`) but applies only the
*translation* to the selected points — the rotation exists to keep the triad oriented as the user
left it. For a control net, applying the rotation about the selection centroid to the selected
block is a genuinely different and useful operation (twisting a row of control points, rolling a
patch edge) and is a strict superset of translate-only. Worth doing, worth calling out as a
divergence from the template rather than an accident.

### 3.1 The rotating triad and Planarize came back from FFD (2026-08-10)

Both were built for `freeFormDeformation.fs` first and back-ported here, which is the right
direction of travel: FFD's lattice points and this feature's control points are the same UI object —
a multi-select of draggable handles with per-point offsets behind them — so a mechanism that earns
its place on one earns it on the other. The two features now share the model rather than resembling
it, and the shared parts are documented once, in
[FREE_FORM_DEFORMATION_SPEC.md](FREE_FORM_DEFORMATION_SPEC.md) §6.5 and §6.9. Only what differs is
recorded here.

**The XYZ triad is now a `fullTriadManipulator`, unconditionally.** That closes the extension above:
the rotation is applied to the selection, not merely stored to keep the handle oriented. FFD's §6.5
argument for making it unconditional transfers whole — a full triad already carries translation
arrows, so a translate-only mode is strictly less capable at no saving, and having two manipulators
write one selection through two different storages is a bug generator rather than a choice.

**The live-then-baked storage transfers whole too.** A cumulative transform cannot be folded into
per-point overrides per drag frame, so it is kept live in `ALWAYS_HIDDEN` parameters and baked into
`controlPointEdits` exactly once, at every moment the base it was measured against is about to move.
Those moments are where this feature differs from FFD, because its base moves for different reasons:

| Bake trigger | FFD's version | Edit Surface's version |
| --- | --- | --- |
| Selection changes | click, or the dialog's list | same |
| Dialog opened | same | same |
| The handles are re-indexed | lattice span counts, orientation, faces, `result` | **Approximate / Elevate / Refine** and their parameters, the face selection, `result` |
| Mode change | n/a — one mode | **XYZ ↔ UVN**, since UVN never writes the transform |

The third row is §4's re-indexing hazard seen from the transform's side, and it is the sharper case
here than in FFD: a lattice keeps its shape when its span count changes, where elevation or
refinement changes *which control points exist at all*.

**The base frame is the one genuine divergence.** FFD orients its triad to the lattice's own axes,
which are a `CoordSystem` already. A control net has no such frame, so the triad takes the
**surface's** frame at the selection's central control point — u tangent for X, normal for Z, which
`coordSystem` accepts because `normalize(cross(uTangent, vTangent))` is perpendicular to the u
tangent exactly. The v tangent is deliberately not an axis: u and v are oblique in general, so it
would not be a legal frame, and orthogonalizing it costs it the isoparametric meaning that makes it
worth showing at all (§7.9's question, answered for the triad and left open for the UVN mode, which
is three independent 1-D drags and so is free to stay oblique). The directions come from the
*unedited* net and the origin from the *committed* one — a frame that chased the edits would
reinterpret the live transform on every regeneration. Where the surface is degenerate — a cone apex,
a pole — it falls back to world axes, which is the only honest answer and still leaves the handle
usable.

**One correctness fix came with it.** `computeSurfaceBeforeEdit` now reads through
`readSurfaceAndTrim` rather than `readSurfaceDefinition`, so the manipulator path and the feature
body build the same net in every result mode. They already had to agree for handle *placement*
(trimmed mode reads through `evApproximateBSplineSurface`, a different call with a different control
point count); a rotation bake makes it load-bearing, because the displacement it writes is computed
from where the points actually are and only a translation is position-independent.

---

## 4. The two-dimensional index problem

A control net is a grid; `togglePointsManipulator` takes a flat array and returns flat indices.
So the net is handed over flattened row-major and converted back at the boundary.

**Store `(uIndex, vIndex)` in the definition, not the flat index.** `"Control point (2, 3)"` is
readable in the feature dialog and in a diff; `#11` is not, and it silently means something
different the moment the column count changes. The flat index exists only in the two lines that
talk to the manipulator.

**Index stability is a real design constraint, not a detail.** Refinement and elevation both
change the net's dimensions, so an edit recorded against index `(2, 3)` means something different
before and after. `editCurve.fs` has the identical problem and resolves it by ordering:
approximate → elevate → edit, with edits expressed against the *prepared* curve. Edit Surface must
fix the same order — **prepare, then edit** — and be explicit in the UI that changing the
elevation or refinement parameters re-indexes the net. Anything cleverer (trying to migrate edits
across a refinement) is a trap: knot insertion changes *which* control points exist, so there is
no honest correspondence to migrate along.

---

## 5. Periodic surfaces: edit the fundamental net, never the stored one

The stored wrap form carries `degree` duplicate rows satisfying the overlap condition
`P[i] == P[i + n]` (§2.3). If the UI exposes stored indices and the user drags stored row 1 of a
cylinder, the overlap breaks, and what reaches the kernel is a surface whose representation
claims to be closed while its data is not.

**Express every edit against the FUNDAMENTAL net — `n` rows, one period — and let the module's
own emission rebuild the padding.** Then the overlap condition is unbreakable by construction
rather than enforced by a check that someone has to remember to write. It also makes the UI
honest: a cylinder with 6 stored rows really does have 4 independently movable ones, and showing
6 handles where 4 exist would be a lie about the geometry.

This does not make the periodic path safe — it makes it *well defined*. A user can still drag a
fundamental control point into a shape whose seam the kernel rejects, and that is exactly the
stress test §1.2 says we have never run. The feature should report those two kernel errors in
terms the user can act on rather than passing the raw enum through.

### 5.1 The handles on a revolve bunch, and it is not a placement problem (2026-08-10)

A note in `prepareSurface` used to say that a kernel cylinder's multiplicity-`degree` arc joints
"drag their neighbouring Greville abscissae in against them, and leave a tight pair at each arc joint
that no placement can undo — that is the price of the net being an exact circle." **Every clause of
that is true and the conclusion was wrong.** It *is* undoable; it just cannot be done by insertion or
removal, which is all that had been tried. Insertion only raises multiplicity and removal cannot take
those knots out at all, because a NURBS circle is genuinely C⁰ there in homogeneous space.

And the multiple knots are only half of it. The same two half-arcs are parameterized about
**1.7 : 1 slower at the joints than mid-arc**, so even a net with every multiplicity already at 1
comes out crowded in the same two bands — as does any downstream feature reading the result's `u`/`v`.
Refinement cannot fix that either: the doubling schedule gives every span exactly one arc-length cut,
so the ratio survives at every count.

`prepareSurface` therefore calls `uniformizePeriodicSurfaceDirections` **first**, before a degree or a
count is chosen off the surface — an arc-length refit onto uniform, all-simple knots. On the kernel's
own cylinder numbers the chord-spacing ratio at evenly spaced parameters goes from 1.98 to 1.0006.
Derivation in [SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) §2.2.0b.

Three feature-side rules come with it:

- **Gated on "Approximate".** The refit moves the surface, so it may only run where the user has said
  a moved surface is acceptable and has named the number — the standing no-silent-approximation rule.
  This costs nothing real: a cylinder, cone or revolve is not a B-spline face (§2.2), so it can only
  be read through Approximate in the first place. Every surface this fixes is already on that path.
- **Reported separately from the count reduction**, because the cause and the cure are different, and
  because it is the only step here that changes the `u`/`v` map at all.
- **It invalidates trim loops, and the domain check cannot see that.** The refit preserves the domain
  start and the period exactly, so the four comparisons in `emitTrimmedSurface` all pass while every
  loop coordinate now names a different place on the surface. `prepareSurface` sets a
  `reparameterized` flag and the probe's verdict reads it — a held domain no longer implies a held map.

---

## 5A. Merging several faces into one patch (E7)

The surface analog of Edit Curve's edge chain: select several faces, get one clean patch. Requested
2026-08-09. This is the largest single item in this spec and it is really **two different features
wearing one name**, which is the first thing to settle.

**Grounding: Edit Curve's chain is a FIT, not a merge.** With `approximate` off and more than one
edge it throws `EDIT_CURVE_MULTIPLE_EDGES`; the chain case runs `constructPath` →
`makeApproximationTarget` → `approximateSpline` with a tolerance and a `maxControlPoints` cap. So an
approximate surface merge is consistent with how the product already behaves here, not a concession.

### 5A.1 The exact case, and why it must be detected first

If every selected face is a piece of **one underlying surface** — a face split by a sketch, patches
of a surface that was divided, anything that came apart rather than being built separately — then
merging is exact: same surface, union of the trim regions, zero deviation. Fitting those faces
instead would manufacture error where none is necessary, which the standing bar forbids. So the
feature must test for this first (compare each face's `evSurfaceDefinition`) and take the exact
route when it applies.

### 5A.2 The general case is approximate BY NATURE, not by implementation

Merging a fillet and two walls into one smooth patch **changes the geometry**, necessarily: the
input is not smooth, and a single tensor-product patch of any degree cannot reproduce a crease. So
there is no exact construction being given up here — "smooth out several faces" is a request for
controlled deviation, and the only honest version measures and reports it.

That distinguishes it from the sampling this project has rejected elsewhere. `opFlex` samples and
cannot say how wrong it is; this samples and certifies against the original faces with
`evPointsDeviation` (§9.1.1's step 5, arriving here first).

### 5A.3 The pipeline, and the one genuinely new piece

1. **Region check.** Faces connected, forming a topological disc. Reject anything else by name
   rather than producing a fold.
2. **Parameterization base.** Default to the best-fit plane over the region; optionally let the user
   pick a reference face or plane — the same `Tools` → `Target` vocabulary Wrap uses and that
   §9.1's UX target already commits to. The base's `(x, y)` become `(u, v)`.
3. **Sample by ray, not by tessellation.** A regular grid over the base, `evRaycast` along the base
   normal at each node. Exact surface hits, no tessellation error, and a fold shows up as a missing
   or multiple hit — a detectable failure rather than a silent wrong answer.
4. **Global surface interpolation** through the grid. This is the ONE piece of new math and it is
   separable: interpolate each row in u, then interpolate the resulting coefficients in v, so it is
   `n` one-dimensional solves per direction rather than a 2D system. Each is a square banded solve
   that `matrix.fs`'s `inverse` covers.
5. **Simplify to the requested control point count** with `simplifySurfaceToControlPointCounts` —
   which is where the smoothing actually happens, and where the deviation gets measured. Interpolation
   reproduces the creases; removing degrees of freedom is what smooths them away, and the reported
   deviation is exactly the price of that smoothing. The lossy primitive built for §2.5 turns out to
   be the core of this feature rather than a side utility.
6. **Trim** to the region's outer boundary, per §2.1 and §2.3.1.
7. **Certify** with `evPointsDeviation` against the original faces, and report.

Steps 1–3, 6 and 7 are assembly over things that exist. Step 4 is new and belongs in the refinement
module with its own tester vectors before any feature calls it — the anchor being that interpolating
a grid sampled FROM a known B-spline surface must reproduce that surface.

### 5A.4 AUTOPSY: the projection-and-raycast pipeline is dead (2026-08-09, same day it shipped)

Live testing failed the raycast coverage check on essentially every real selection, and the failure
is structural, not a bug:

1. **The coverage test demanded that the selection's projected outline BE its bounding rectangle.**
   A tensor patch is rectangular in its parameters, so the pipeline sampled the bounding box and
   treated any missed ray as fatal. Almost nothing projects to its own bounding rectangle — **a
   single circular face fails**, because the box's corners lie outside the circle. The check was
   correct; the method's precondition is simply almost never satisfiable.
2. **The projection direction was extrinsic.** An area-weighted normal average has no relationship
   to the faces' own geometry; it degrades to grazing incidence as the selection wraps and fails at
   normal cancellation.
3. **Sampling discarded exactly what the inputs are rich in.** The faces already ARE B-splines with
   exactly known seams; raycasting threw that structure away and tried to rediscover it from points.

The code was removed, not shelved. Multi-face selection currently throws a named error pointing
here; the same Approximate controls drive the replacement, so the UI is unchanged.

### 5A.5 The replacement: assemble the raw definitions, then remove the seams

The merge is **assembly plus measured knot removal**, and its hardest pieces already existed:

1. **Order the faces into a strip** along their shared edges (topology, feature-side).
2. **Concatenate their B-spline definitions** in the chain direction —
   `concatenateBSplineSurfaces`. Exact: transverse directions are elevated and refined onto a
   common knot vector (insertion only), each piece keeps its own chain parameterization (domains
   translated, never rescaled), rational pieces are reconciled by a global projective rescale (the
   only weight change that alters nothing), and every seam becomes a knot at multiplicity = degree —
   a C0 joint that PRESERVES the kink. The composite is the honest representation of the unsmoothed
   chain, and the function returns `seamParameters` naming every kink.
3. **Smooth = remove the seam knots** — `removeSurfaceKnotLine`, the whole-knot-line removal built
   for simplification, aimed at the seams. Each removal raises continuity there by one order and
   returns the deviation it cost. This is the entire smoothing step, and its price is the measured
   number the feature reports.
4. **Certify** with `evPointsDeviation` against the original faces (feature-side, next).

Four-sidedness is intrinsic — two rails × the chain direction — so there is no projection, no
coverage rectangle, and no wrap limit anywhere in the pipeline.

**The exact case (§5A.1) is subsumed, not special-cased.** Pieces of one underlying surface
concatenate and seam-remove at exactly zero deviation, recovering the original surface and its
original knot vector — the pipeline reports 0 instead of taking a different path. This is also the
tester's anchor (`CONCAT`): split a known surface with `extractSubSurface`, reassemble, heal, and
require bit-level recovery.

Named limits, thrown rather than fudged: chain and transverse directions must be non-periodic (the
periodic transverse case needs the periodic knot-sharing path); and after the projective rescale the
two sides of a seam must agree in **weights** column-for-column, not just position — disagreement
means the faces parameterize their shared edge differently, which needs seam reparameterization
machinery that does not exist yet. Where sections must be manufactured rather than extracted
(non-isoparametric seams), `loftBSplineSurfaceThroughCurves` skins through compatible section
curves, with the unisolvence guarantee that lofting a surface's own isocurves at its own Greville
abscissae reproduces it exactly.

### 5A.7 Feature wiring (2026-08-09, after the assembly vectors ran green first-try)

The primitives are all live-verified, and the feature drives them. Same buttons as before, per the
Edit Curve rule: multiple faces + `Approximate` off → the editCurve-style gate; on → the merge.

**Feature-side work is topology and orientation only** — everything geometric is module calls:

- `orderFacesIntoChain`: adjacency by shared edges (`qIntersection` of the faces' edge sets), walk
  from an endpoint. Branching faces, disconnected selections, multi-strip selections, closed rings,
  and pairs sharing several edges each throw by name.
- `orientedChainPieces`: each piece turned so the chain runs +u and transverse directions align —
  `transposeSurface` / `reverseSurfaceDirection`, both exact. Which boundary carries a seam is
  decided by **corner matching** against the seam edge's endpoints (2 `evVertexPoint` calls per
  seam; all comparison module-side). This is also where four-sidedness is *enforced*: a face whose
  shared edge does not bound its surface throws by name, as does an interior face whose two seams
  sit on adjacent boundaries (the strip would turn a corner in parameter space).
- `buildMergedSurface`: concatenate → **elevate to the Approximation group's target degrees** →
  heal every seam to multiplicity 1 → enforce the max-control-point budget. Degree placement
  matters and follows the standing doctrine: degree sets the continuity ceiling at the seams, so
  degree-1 walls merged at degree 1 keep their crease, while the same walls at target degree 3 heal
  to curvature continuity. Elevation is exact; the seam and budget deviations are measured and
  returned. (Surface elevation is deliberately unminimized, so the budget pass also sweeps up the
  exactly-removable knots it leaves — free removals always win the least-error greedy.)
- **Certification**: the final definition sampled at span midpoints (the control-net-blind sites,
  §9.1.1), one `evPointsDeviation` call against the original faces, *before* any replace consumes
  them. One number covers join gaps, smoothing, and the budget together; exceeding the requested
  tolerance warns with the measured value.
- **Result modes**: REPLACE_FACE passes *all* selected faces to one `opReplaceFace` — the merged
  surface replaces the whole strip and heals against the unselected neighbours, which have not
  moved. NEW_BODY emits the untrimmed patch; NEW_BODY_TRIMMED reports that merges have no extracted
  loops yet and emits untrimmed.
- **Merged patches are editable**: the manipulator handler rebuilds the same merged net via the
  no-reporting `buildMergedSurface`, so control-point handles land on the merge and every editing
  mode composes with it.

### 5A.8 First live run: four fixes (2026-08-09)

**Parameter width is not arc length — the cause of BOTH reported control-point biases.** A rational
circle's Bezier-arc parameterization has strongly varying speed, so splitting the widest span *by
parameter* piles control points onto one side of a cylinder; and concatenation translates each
piece's domain without rescaling, so a metre-long wall and a two-millimetre fillet arrive with
parameter lengths unrelated to their size, distorting every later parameter-driven decision.

Fixed at both ends, and the licence to do so is worth stating: **knot placement is a pure heuristic
that cannot introduce error**, because insertion is exact wherever it lands. Numerical arc length is
therefore legitimate here and nowhere else in the module.
- `arcLengthSpanInsertions` splits the span of greatest **arc length** at its **arc-length**
  midpoint, from a chord-sum profile averaged over representative isocurves. Handles clamped and
  periodic directions uniformly (the domain end is the wrap image, so the wrap span competes like
  any other). `refineSurfaceToControlPointCounts` now uses it.
- `rescaleSurfaceDirectionDomain` + `approximateDirectionArcLength` size each strip piece's chain
  domain proportional to its physical extent before concatenation — an affine reparameterization,
  so no geometry moves. Vector `ARCLENGTH`, whose discriminating check is the ratio of largest to
  smallest angular gap between a refined cylinder's control points.

**Trimmed faces no longer refuse to merge.** A trimmed planar face sits on a plane whose domain
extends far past it, so the shared edge bounds the *face* while landing deep in the *surface* —
corner matching reported the seam 41 mm from any boundary and refused perfectly mergeable geometry.
`readChainPieceSurface` now reads each chain piece as the sub-surface its trim occupies: the matched
surface-and-loops pair from `evApproximateBSplineSurface`, then `extractSubSurface` to the outer
loop's UV bounding box (exact). What comes back is four-sided with the face's real edges on its
boundaries. A face whose trim is not a UV-aligned rectangle still gets refused — that is the
genuinely non-four-sided case.

**The user's tolerance and the kernel's are now separate quantities.** Conflating them was a real
bug: `evApproximateBSplineSurface` caps at 1e-4 m, so capping the user's *acceptable deviation*
there meant the "above the requested tolerance" warning could never be satisfied by any achievable
merge — a fillet smoothed away deviates by millimetres. The dialog tolerance now uses std's
`TOLERANCE_BOUND` (up to a metre, exactly what Edit Curve allows) and is clamped into the kernel's
range only where it is handed to the approximator.

**Replace face gained a per-face fallback.** All targets at once in both orientations first; then,
for a strip, face by face in order, so each replaced face becomes a settled neighbour for the next.
Partial success is reported honestly rather than left to be discovered. *(Later removed —
`replaceFace.fs` calls `opReplaceFace` **once** for many faces onto many faces, so the fallback was
inventing a limitation the standard library disproves. See §5A.9.)*

### 5A.9 Second live run: placement quantization and an inert control (2026-08-09)

**Arc-length placement was still quantized, which is the whole of "the knots go kind of wherever".**
`arcLengthSpanInsertions` measured the right quantity and then threw the precision away: splitting
the longest span at its arc-length midpoint, once per insertion, can only ever divide a span into
halves, quarters, eighths. Spreading twenty-one insertions over a cylinder's four equal quarter spans
therefore yields sixths and quarters — **a flat 2:1 spacing variation around the perimeter at almost
every target count**, no matter how exactly each individual split was located.

Replaced with *allocate, then subdivide*: decide how many pieces each existing span ends up cut into
(greedily, each insertion going to whichever span currently has the longest piece — the allocation
that minimizes the longest piece overall), then cut each span into that many **equal-arc-length**
pieces in one pass. Four equal spans and twenty-one insertions become 6/6/6/7, a ratio of 7:6.

Vector `ARCLENGTH` measures this on the two-arc rational cubic circle, whose wrap form has **two**
equal half spans. Twenty-four editable points is twenty pieces, ten per half — dead even, and ten is
not a power of two, so bisection cannot reach it either (it lands on six quarters and four eighths,
2:1). Threshold dropped 2.5 → 1.15. A second check uses a count that cannot split evenly *between*
the spans (fifteen points → eleven pieces → 6/5 → 1.2, threshold 1.35), since a target that happens
to divide could in principle hide a partial regression.

**What placement cannot fix, and why the cylinder still shows four tight pairs.** A control point
sits at its Greville abscissa — the average of `degree` consecutive knots — so at a knot of
multiplicity == degree one control point lands *on* the knot and its neighbours crowd in at a
fraction of the surrounding spacing. An exact rational circle **requires** those multiple knots: the
circle is smooth there, but the homogeneous curve the knots actually describe genuinely corners
there (for the standard quarter-arc quadratic, the outgoing and incoming homogeneous tangents differ
in their w component's sign, not by a positive scale factor), which is precisely why a full circle
needs several rational segments at all. `removeRedundantSurfaceKnots` correctly declines to touch
them, and the note in `prepareSurface` that called them redundant was wrong and is corrected. Even
knot spacing is achievable; a perfectly even *net* on an exact circle is not.

**Reducing a periodic direction no longer throws a raw module string.** `simplifySurfaceToControlPointCounts`
refuses periodic directions (knot removal there must preserve the wrap overlap; see the utility
spec). Asking a cylinder for fewer control points than it has is an ordinary request, not a
programming error, so `reduceToControlPointCounts` skips the periodic direction, reduces whatever
clamped direction it can, and records the skip for the feature body to warn about. The merge's
budget pass routes through the same helper.

### 5A.10 The merge must SPEND its budget, and must not depend on click order (2026-08-09)

**The strip patch's control points piled onto the fillet and left the walls bare, and refining the
placement could not have fixed it — there was nothing there to place.** Concatenation preserves each
piece's own knot density, because that is what makes it exact. A flat wall is degree 1 with two
control points and no interior knots; the fillet arrives from the approximator with a dozen. Joined,
the merged net carries nearly all its handles over the fillet. Arc-length domain sizing (§5A.8) fixed
the *units* the knots are quoted in, not *how many* of them exist — which is why that fix, though
correct, did not move this symptom.

`buildMergedSurface` now **refines up to `approximationMaxUCPs`/`VCPs`**, then reduces as before if it
is still over. Refining costs nothing — insertion is exact, the surface does not move — and with
allocate-then-subdivide placement the added knots land at even arc length, which is exactly the long
bare wall spans getting their share. It also makes the field behave the way Edit Curve's "Maximum
control points" does, where a real fitter spends its budget rather than only trimming against it.

### 5A.11 REFINE BEFORE HEALING — the ordering that decides whether the patch hugs its input

**Reported on two faces of a plain cube merged into one continuous patch:** four and five control
points give a reasonable, symmetric blend; six and up break symmetry and hook away from the cube
rather than approaching it. Raising a control point budget made the approximation *worse*, which is
backwards.

The first version of §5A.10 refined *after* seam healing. That is the wrong order, and the reason is
a property of knot removal worth stating on its own:

> **Knot removal is local in INDEX and global in EFFECT when the net is sparse.** Removing the seam
> knot recomputes the `degree` control points on each side of it and nothing else. What varies is how
> much *surface* those control points govern. Two flat faces elevated to degree 3 give seven control
> points over two spans, so those few points span the whole patch, and smoothing a 90° crease drags
> the entire thing out of shape. Refining afterwards can never undo it: insertion is exact, so it
> adds handles to a surface whose damage is already baked in.

Refine first and the same removal touches control points confined to a narrow band around the corner.
The wings stay on the original planes, only the corner rounds, and **a bigger budget now means a
tighter corner** — the behaviour a user expects from the number they typed, and the thing that makes
the seam deviation fall as the budget rises instead of staying pinned at whatever the bare elevated
net could manage.

Order is now: concatenate → elevate → **refine to budget + healing removals** → heal every seam →
refine/reduce to land exactly on the budget. The target is raised by what the healing is about to
give up (`seamCount × (degree − 1)` knots to reach multiplicity 1) so the patch lands on the budget
rather than finishing short of it. Degree 1 is unaffected: zero healing removals, crease preserved,
exactly as §5A.7's doctrine says.

### 5A.12 The one-sided lean was a mechanism, not arithmetic

I first wrote the leftover asymmetry off as unavoidable apportionment — one insertion cannot be split
between two equal spans, so somebody has to take it. That framing was wrong twice over, and the
correction is the useful part.

**It is a systematic directional bias, not a parity accident.** The allocation gives each insertion to
whichever span has the longest current piece, ties broken by array order — which means the **lower
index, every single time**. On a symmetric shape every span ties with its mirror, so *every*
allocation that cannot divide evenly lands on the same side, for the same reason, at every count.
That is what put knots preferentially on one side of a fillet strip, and it does not average out.

**And the asymmetry was reaching a lossy step, which is what made it an artifact rather than an
uneven net.** Knot removal reads the spacing on *both* sides of the knot it removes. A net with one
extra knot on one side of the seam heals lopsidedly, and a lopsided heal is in the geometry — it
survives as a surface defect. An asymmetric *handle*, by contrast, costs nothing at all, because
insertion is exact.

The fix separates those two facts rather than trying to enforce symmetry:

- `arcLengthSpanInsertions` now allocates **a whole tie group at a time**, with a relative tie
  tolerance (exact equality is the wrong test — mirror spans differ in the last bit after chord-sum
  quadrature, and a detector that misses by one ulp reintroduces the bias it exists to remove).
  Serving groups together makes the allocation invariant under any relabelling of tied spans,
  including the mirror relabelling.
- A `balancedOnly` overload of `refineSurfaceToControlPointCounts` **declines to serve half a group**,
  coming in under the target rather than picking a side. Callers that need the count use the plain
  overload; callers feeding a lossy step use this one.
- The merge refines **balanced before healing** and **exactly after**, so the parity remainder is
  always an exact insertion on an already-healed surface, where it cannot mark anything.

Result: the merged surface is symmetric at every budget, odd or even. Only the handle *count* carries
the parity, and handles are free.

Tester coverage is the palindrome property — `knotVectorIsPalindromic` on a mirror-symmetric fixture
(a parabola in u on `[0,0,0,0,1,1,2,2,2,2]`, structurally the elevated two-cube-face case). Both
overloads are asserted, including that the plain one *does* pick a side: a balanced default would
silently break every caller that asked for a number, and a flag that changed nothing would be worse
than no flag.

### 5A.13 The lean was in the removal kernel, and §5A.12 was still looking in the wrong place (2026-08-09)

**Reported again after all of the above shipped: merged faces of a cube still hook to one side, at
every budget, odd or even, and turning the cube into a prism makes it worse.** §5A.12 fixed real
asymmetries in *placement* and then guessed that the remainder lived in the least-error greedy inside
`simplifySurfaceToControlPointCounts` ("the same bug in a different function"). **That guess was
wrong and is withdrawn.** The greedy is fine. So is the allocator.

The lean is in `removeKnotFromPointArrays` — NURBS Book A5.8 — and it is a *completeness* bug rather
than a tie-breaking one. Full account in
[SPLINE_REFINEMENT_UTILITY_SPEC.md §2.2.1](SPLINE_REFINEMENT_UTILITY_SPEC.md); the short version is
that the algorithm computes **two** candidate values for the control point that survives a removal,
the book only ever runs it where the two agree, and this module runs it precisely where they do not.
Keeping either one dumps the entire removal error on one side. The midpoint halves it.

Why this presented as a merge problem specifically: the ambiguity appears when `degree − multiplicity`
is odd, and healing a seam at degree 3 goes 3 → 2 → 1, so **the final healing removal hits it every
single time**. Ordinary degree-3 simplification, with simple interior knots, has an odd window and
was never affected — which is exactly why the defect survived a green tester and looked like a
placement problem.

Why the prism made it obvious: the discarded answer is the one derived from the *shorter* side, so
which leg is longer decides whether the heal takes the good branch or the bad one. Feeding the same
crease in mirrored produced 1.48 against 0.74 worst deviation. It is now 0.62 either way.

The corner also got **2.9× closer to the true corner** on the symmetric two-face case, so this is an
accuracy fix that happens to produce symmetry, not a symmetry fix that costs accuracy. Nothing in the
merge pipeline changed; the ordering established in §5A.11 and the balanced refinement of §5A.12 are
both still right and still there.

**One measured characteristic worth knowing rather than fixing:** at a budget one control point above
the concatenated count (six, for two flat faces at degree 3), the balanced pre-heal refinement
correctly declines to split its only tie group and refines by nothing, so the corner heals blunt —
0.33 deviation against 0.17 at seven. It is symmetric and it improves monotonically with budget, and
the alternatives all buy sharpness with asymmetry. Raise the budget by one.

**Selection order changed the result, and it must not.** `orderFacesIntoChain` started its walk from
`chainEnds[0]` — the first chain end in `evaluateQuery` order, i.e. whichever face the user happened
to click first. Once a start is picked the rest of the walk is forced (a strip is a path), so that
was the *only* freedom in the ordering, and it is not cosmetic: reversing the chain reverses u, and
neither the least-error greedy in knot removal nor the seam-healing order is symmetric, so identical
input produced genuinely different patches. New `canonicalChainStart` picks the end with the
lexicographically smaller bounding-box minimum corner — arbitrary, but total and derived from the
geometry, which is the whole requirement.

**REVERTED FROM 5A.9: the approximation maximum does NOT apply to a single-face read.** Making it a
universal ceiling looked like the honest fix for the inert field and was destructive. With Approximate
on — which a cylinder or fillet *requires*, being no kind of spline — and the field at its default of
15, every face reading more than fifteen control points was silently crushed to fifteen. One cause,
three broken-looking symptoms: Show details reported the wrong counts, the drawn net stopped matching
the surface, and any stored control point index past fourteen fell out of range. A control the user
has not touched must not destroy their net.

The knob model that replaces it is scoped by **who chooses the count** at each stage:

| Stage | Who chooses | Lever |
|---|---|---|
| Single-face read | the kernel — `evApproximateBSplineSurface` has no count parameter and picks its own | **tolerance** |
| Single-face editing net | the user, explicitly | **Refine** U/V control points (exact insertion; also reduces) |
| Merge | the feature | Approximation **maximum control points**, now spent as a target and enforced as a cap |

A cylindrical fillet that reads as six control points stays at six however high the *maximum* goes,
because six is all the kernel needed for the tolerance it was given. Refine is what takes it to a
hundred. That is not the field being inert — it is the field being a maximum.

### 5A.6 What shipped for the OLD pipeline (2026-08-09), and the UI rule it follows — kept for the record

Step 4 landed in the module (`interpolateBSplineCurveThroughPoints`,
`interpolateBSplineSurfaceThroughGrid`, vector `INTERPOLATE`, green) and the feature now merges.

**No new UI, by instruction and by precedent.** `editCurve.fs` has no separate "interpolate" or
"merge" control anywhere: selecting several edges simply requires `Approximate` to be on — it throws
`EDIT_CURVE_MULTIPLE_EDGES` otherwise — and reads degree, maximum control points and tolerance from
the group that already exists. Edit Surface does exactly that. The Faces input takes several picks;
more than one with `Approximate` off is refused with a message in the same spirit; and the
Approximation group gained U/V target degree and U/V maximum control points, which are the surface
forms of controls Edit Curve already has. A user who knows Edit Curve presses the same buttons.

Pipeline as built: area-weighted average normal and centroid give the base frame → `evBox3d` in
that frame gives the sampling rectangle → `evRaycast` per grid node (real surface hits, no
tessellation) → interpolate → `simplifySurfaceToControlPointCounts` down to the maximum control
point setting → measure the worst deviation at every sample and report it, flagging when it exceeds
the requested tolerance.

Sampling is denser than the target net (2× the control point maximum, capped at 60 per direction):
interpolation reproduces whatever it is handed, and the reduction afterwards needs material to
smooth. The cap bounds the one matrix inverse per direction.

**Deliberately not yet done, and each says so at runtime rather than failing quietly:**

- **The merged patch is untrimmed and comes out as its own body.** There is no single face to replace
  and no extracted loop to trim with — the merge's outline lives in the base plane, not in any one
  face's parameter space. Trimming the merge to the region outline is the next piece.
- **The projected outline must fill the sampling rectangle.** A tensor-product patch is rectangular
  in its own parameters, so an L-shaped or notched selection leaves rays with nothing to hit; the
  feature counts the misses and says so instead of fabricating those nodes.
- **Planar projection limits how far a selection may wrap.** Normals that cancel are rejected by
  name; a selection spanning much more than a right angle will sample at grazing incidence and
  degrade before that. A user-picked reference face (§5A.3 step 2) is the fix and is not built.

## 6. Phasing

**Phase E1 — the round trip, no editing at all.** Read the face, normalize, emit, replace. A
feature that provably changes nothing is the entire point: it isolates steps 1, 4 and 5 from
every question about step 3. Run it on a planar face, a spline face, a trimmed face with a hole,
a revolve (periodic), and a cone-to-cylinder-shaped thing. **If this phase is not bit-stable, the
deformation feature was never going to work and we have found that out for the price of ~150
lines.**

**Phase E2 — prepare.** Add elevate and refine, still with no editing. Same identity check: the
surface must be geometrically unchanged after any amount of elevation and refinement, since both
are exact. Now `prepareSurfaceForDeformation` and `refineSurfaceToSpanDensity` have a consumer,
and the read-only "Show details" panel (degrees, control point counts, span counts, periodicity
per direction) starts earning its place.

**Phase E3 — single control point editing.** `pointsManipulator` + one triad, XYZ offsets and a
weight override, on clamped surfaces only. The narrowest thing that is actually an editor.

**Phase E4 — multi-point editing.** `togglePointsManipulator` + `fullTriadManipulator`, the §3
template including the rotation extension. *Shipped 2026-08-10, back-ported from
`freeFormDeformation.fs` along with Planarize; see §3.1 for what transferred and what did not.*

**Phase E5 — periodic surfaces**, per §5. Deliberately last: it is where the kernel says no, and
it wants the rest of the feature to be trustworthy before it becomes the variable under test.

**Phase E6 — normals and curvature display**, the §1.4 consumer of the derivative layer. Arguably
this could come earlier as a debugging affordance; it is listed last only because it is additive.

---

## 7. Decisions, and what remains

Settled by the arguments above, unless someone objects:

1. **Exact input by default.** `evSurfaceDefinition` when the face is a B-spline;
   `evApproximateBSplineSurface` only behind an explicit toggle. Consistent with the module's
   standing rule.
2. **Prepare-then-edit ordering**, fixed, with re-indexing made explicit in the UI (§4).
3. **Fundamental-net indexing for periodic directions** (§5).
4. **`opReplaceFace` by default**, new-body optional, reusing `displacementMap.fs`'s retry
   pattern verbatim including the `try silent` prohibition (§2).
5. **Rotation applied to the selection**, diverging from `routingCurve.fs` (§3).
6. **Phase E1 ships and is validated before any editing exists** (§6).

Open, and worth a decision before E3:

7. **Region-limited editing — does `extractSubSurface` belong in v1?** The case for: it is the
   fourth consumerless entry point, and "edit only this patch of a big surface" is a real
   workflow. The case against: it changes the feature from "edit a face" to "edit part of a
   face", which is a different mental model and needs its own UI for picking the rectangle.
   Leaning toward a later phase.
8. ~~**Is there a surface analog of `editCurve.fs`'s `planarize` worth having?** Fitting a control
   net to a plane is well defined but of unclear value. Probably not.~~ **Settled 2026-08-10: yes,
   but not that one.** The question assumed the *curve* feature's shape — a persistent toggle that
   flattens the whole thing against a chosen reference plane — and for a whole control net that
   really is of little value. `freeFormDeformation.fs` built a different shape and it is obviously
   worth having: a **button** that flattens the current *selection* onto its own least-squares plane,
   once, writing ordinary point overrides with nothing constrained afterwards. Flattening four points
   just dragged out of alignment, or a boundary row that needs to sit flat, is an everyday move.
   Back-ported verbatim, including the fit (§6.9 of [FREE_FORM_DEFORMATION_SPEC.md](FREE_FORM_DEFORMATION_SPEC.md),
   itself a transcription of std `editCurve.fs`'s private `fitPlane`) and the degenerate case:
   collinear points give a rank-1 covariance whose fitted plane *contains* the line, so pressing it
   on a single net row is a no-op rather than an error. Fewer than three points is refused outright.
   See §3.1.
9. **How much of `editCurve.fs`'s UVN edit mode carries over?** Its N direction is the curve
   normal; a surface's is unambiguous (§1.4), but its U and V would be the isoparametric
   tangents, which are not orthogonal in general. Whether to orthogonalize (and lose the
   isoparametric meaning) or keep them oblique (and have a non-orthogonal "triad") is a real
   question with no obvious answer.
