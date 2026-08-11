# Free-Form Deformation — Consolidation and Refactor

Design document for `custom-features/freeFormDeformation.fs`.

This describes a **consolidation of two existing features into one**, rebuilt on
`custom-features/splineRefinementUtils.fs`. It is the first concrete instance of the deformation
pipeline that [SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) §9.1 describes,
and it implements the specific FFD guidance in that document's §9.2.

Status: implemented, **not yet verified against a live build**. Nothing here should be read as a
passing result.

---

## 1. What was consolidated, and why it was ever two features

Two features went in:

| File | What it was |
| --- | --- |
| `freeFormDeformation.fs` | The Sederberg & Parry lattice. One control point selectable at a time, dragged with a `triadManipulator`. |
| `freeFormDeformationPlanes.fs` | The same lattice **forced to 1 span in two directions**, so that a whole cross-sectional "plane" of four control points could be selected and driven with a `fullTriadManipulator`. |

The second existed for one reason, stated plainly in its own README: the first had a *"clunky UI when
manipulating many individual control points"*. Moving a cross-section meant dragging four points one
at a time and hoping they landed coplanar. Degenerating the lattice so that a plane was always
exactly four points, and giving that group a single 6-DOF handle, made cross-section work possible —
at the cost of making the lattice not a lattice.

**That trade is no longer necessary.** `togglePointsManipulator` — the multi-select mechanism
`editSurface.fs` and `routingCurve.fs` both use — lets a selection be an arbitrary set of points. A
"plane" stops being a different kind of lattice and becomes a *selection over the same lattice*:

- Everything the planes feature could do is a **selection scope** here.
- The lattice keeps whatever span counts the user asked for in all three directions, so a plane at
  constant N in a 4×3×5 lattice is a 4×3 grid of twelve points, not a forced 2×2 of four.
- Rotation of a selection about its own centre — the planes feature's distinguishing capability —
  applies to any selection, including a single row or a scattered handful.

The planes feature's other advertised benefit, *"stable indexing when adding or removing planes"*,
did not survive scrutiny and is not reproduced. Its plane transforms were keyed by plane index, so
changing the plane count silently repointed every stored transform at a different plane. This
feature keys edits by `(u, v, n)` cell, which has the same exposure when span counts change, and
handles it honestly instead: out-of-range edits are **counted, reported and ignored**, left in the
definition so that raising the count back restores them. Lattice resolution is a control users are
meant to turn, not a one-way door.

### 1.1 The selection scope model

| Scope | Fixes | Varies | Replaces |
| --- | --- | --- | --- |
| Point | u, v, n | — | old point-lattice behaviour |
| Row along U | v, n | u | — |
| Row along V | u, n | v | — |
| Row along N | u, v | n | — |
| Plane at constant U | u | v, n | planes feature, "S direction" |
| Plane at constant V | v | u, n | planes feature, "T direction" |
| Plane at constant N | n | u, v | planes feature, "U direction" |

Scope is applied to the **difference** between what the manipulator reported and what was already
selected, never to the whole reported set. Expanding the whole set would make a plane scope
impossible to break out of — deselecting one point would immediately be re-expanded from the points
still selected. Newly added points expand to their row or plane; newly removed points remove theirs;
points that were already selected and still are stay exactly as they were.

---

## 2. Naming: STU became UVN

Sederberg & Parry's 1986 paper labels the lattice axes `s`, `t`, `u`. Both old features followed it.
This one does not, for two reasons:

1. Every other piece of parameter-space work in this repository — `splineRefinementUtils.fs`,
   `editSurface.fs`, the tween features — settled on **UVN**. STU only ever existed because the
   paper needed three letters that were not already taken in 1986.
2. STU's third letter is `u`, which collides with a *surface* parameter far more confusingly than
   UVN's does.

**The collision UVN creates is real and is handled by naming, not by hope.** A deformed surface has
its own `u` and `v`. The rule in the code is absolute: anything belonging to the lattice says so in
its identifier — `latticeSpanCountU`, `latticeParametersOfPoint`, `latticeFlatIndex`, and "lattice
U" in the dialog — while anything belonging to a surface keeps the module's vocabulary unchanged:
`uDegree`, `uKnots`, `uParameter`. Where both appear in one function, the comment says which is
which.

### 2.1 Colour

| Lattice direction | Colour | Source of the convention |
| --- | --- | --- |
| U | Magenta | Onshape's own U isoparametric colour; `editSurface.fs` already follows it for its control net |
| V | Cyan | Onshape's own V isoparametric colour; same |
| N | **Yellow** | **This repository's own.** Onshape has no third parameter direction to be consistent with. |

Yellow is chosen deliberately rather than arbitrarily: it is the third subtractive primary, so it
sits in the same family as magenta and cyan and reads as a peer of them rather than as a highlight;
and it avoids red (error highlighting) and the blue/green used by debug geometry elsewhere in these
features.

Colour is the *only* cue distinguishing the three directions once the cage has been dragged out of
its axis-aligned starting shape, which is most of the time it is on screen, so it is worth being
consistent about.

---

## 3. The real defect: no refinement

Both old features rebuilt the deformed surface with the **original knot vectors and the original
control point count**. `freeFormDeformation.fs:344-353` in the pre-refactor file is explicit about
it: `uKnots` and `vKnots` are copied straight through from the source surface, and only the control
point *positions* change.

That is the classic FFD ceiling: **the deformation can only ever be as detailed as the control net
it was handed.**

- Feed it a 4×4 Bezier patch and a 6×6×6 lattice, and the surface has 16 degrees of freedom per
  coordinate to express a field sampled far more finely. Most of the lattice does nothing
  representable.
- Symptomatically this reads as *"cranking up lattice resolution stops helping"*, or as a
  deformation that looks subtly wrong away from the lattice corners.
- The old planes README's troubleshooting entry — *"Deformation looks faceted or rough → the output
  depends on the input surface control point density. Start with higher-quality input surfaces"* —
  is this defect, diagnosed correctly and then handed to the user as their problem.

Knot refinement is the standard fix and it is **exact**: refine the control net *before* deforming,
and the undeformed surface is bit-for-bit unchanged while gaining the degrees of freedom to
represent the field.

### 3.1 Refinement is what makes it converge, not just what adds handles

Per [SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) §3.2, deforming a control
net is exact **only for an affine map**. The trivariate Bernstein map is affine only in the
degenerate case where no lattice point has been moved off its grid position — that is, only when the
feature is doing nothing.

So for every deformation worth having, control-net deformation is an *approximation*, and refinement
is what drives its error to zero. The old features had no refinement step, so they had neither the
degrees of freedom nor the convergence, and no way to measure the shortfall.

---

## 4. The pipeline

§9.1's five steps, as applied here. Steps 1, 2, 4 and 5 are shared with the deformation family;
only step 3 is FFD-specific.

1. **Read.** `evSurfaceDefinition` when the face is already a `SPLINE` — exact, no tolerance.
   `evApproximateBSplineSurface` otherwise, behind an explicit *Approximate non-spline faces*
   toggle. Same standing rule `editSurface.fs` applies at its front door. Expect to need the toggle
   more often than seems reasonable: `surfaceType` reports how the kernel *stores* a surface, so an
   extruded or revolved spline profile comes back `EXTRUDED` or `REVOLVED` even though it is a
   B-spline surface mathematically. Those approximate essentially perfectly.
2. **Elevate, then refine.** `prepareSurfaceForDeformation`. Order is load-bearing — elevating after
   refining multiplies the control point count for nothing.
3. **Deform.** Push every control point through the lattice. The only step that moves anything.
4. **Emit.** `opCreateBSplineSurface`.
5. **Replace (optional).** `opReplaceFace` onto the original face, which carries its perimeter,
   holes and inner loops across for free.

### 4.1 Degree is chosen by intent, never by tolerance

§9.1.1's rule, exposed as a *Continuity* control:

| Intent | Degree elevated to |
| --- | --- |
| Curvature continuous (default) | 3 |
| Tangent continuous | 2 |
| Inherit from the face | source degree |

A position tolerance **cannot** detect that degree 1 is wrong. Refining a degree-1 surface converges
in position while remaining C⁰ forever, so a tolerance-only loop ships a finely faceted result that
satisfies every numeric check and looks like garbage. Elevation happens once, before the loop, and
never inside it.

### 4.2 The refinement loop

§9.1.1, verbatim: **drive free, certify with the kernel.**

1. Deform at the current level → `S̃_k`.
2. Refine the *undeformed* surface one level finer (every direction's span count doubles), deform →
   `S̃_{k+1}`.
3. Lift `S̃_k` onto `S̃_{k+1}`'s knot vectors with `makeSurfacesShareKnotVectors` — exact, and it
   makes the **weights** match too, since both are the original weights refined by composed
   operators.
4. `delta = max_i ‖P_k,i − P_{k+1,i}‖`. This bounds the true surface deviation with no evaluation at
   all, because both the polynomial and the rational basis are non-negative and sum to 1, so the
   difference of two splines sharing degree, knots and weights is a convex combination of their
   control point differences. If `delta > tolerance`, loop.
5. **Certify.** Evaluate the *refined undeformed* definition at the midpoint of every knot span in u
   and v with the module's `evaluateBSplineSurfacePoint` — pure arithmetic, and by construction in
   the same parameterization as the definition, so there is no face-parameter normalization to
   mismatch the way `evFaceTangentPlanes` would. Push those points through the lattice to get points
   on the **true** deformed surface, and call `evPointsDeviation` against the body just built.

The certified number is reported through `reportFeatureInfo`. **Neither old feature could answer
"how wrong is this?" at all**, which is the single largest practical difference.

Span midpoints specifically: a knot is where two refinement levels agree by construction, and the
midpoint is where they are furthest from agreeing — exactly where the free comparison in step 4 is
blind.

**Divergence from the spec, stated rather than hidden.** §9.1.1 step 6 calls for a *failed*
certification to trigger another refinement pass and a re-certify. This feature certifies once and
**warns** if the result is above tolerance, because re-certifying means deleting and re-creating the
body per pass. The loop is bounded by *Maximum control points per direction* and *Maximum refinement
passes*; when either binds, the warning says so and reports the deviation actually achieved. Silently
stopping at a cap and presenting the result as converged is the failure mode the whole refinement
spec exists to avoid.

### 4.3 Default tolerance

Scale-relative, so the same default behaves on a 5 mm bracket and a 5 m hull: **lattice bounding box
diagonal × 1e-4**, floored at `TOLERANCE.zeroLength`, with a length override in the dialog. The
lattice diagonal is the right scale because it bounds every surface being deformed by construction.

---

## 5. Exact composition, and why it is not the implementation

FFD is the one map in the deformation family that is **polynomial**. Bend, twist, taper and flow
along a surface are all transcendental or projection-defined; a Bernstein lattice is not. So
composition is exactly representable, with no tolerance anywhere — worth working out, because it is
the natural question to ask and the answer is what justifies the control point ceiling.

Let the surface be a Bezier patch of bidegree `(p, q)`, and the lattice have span counts `(l, m, n)`
— which are its Bernstein degrees per direction.

The lattice parameters `u`, `v`, `N` are **affine functionals of the 3D point**, so each is itself a
bidegree `(p, q)` function of the surface parameters. A Bernstein basis function `B_i,l(u)` is a
degree-`l` polynomial in `u`, hence bidegree `(lp, lq)` in the surface parameters. The product of the
three basis factors is therefore

> bidegree `((l + m + n)·p, (l + m + n)·q)`, i.e. **`(Dp, Dq)` with `D = l + m + n`**.

Worked through:

| Surface | Lattice | `D` | Exact composed degree | Within `MAX_DEGREE` (15)? |
| --- | --- | --- | --- | --- |
| Bicubic (3, 3) | 1×1×1 (trilinear) | 3 | 9 | Yes |
| Bilinear (1, 1) | 2×2×2 | 6 | 6 | Yes |
| Bicubic (3, 3) | 2×2×2 | 6 | 18 | **No** |
| Bicubic (3, 3) | 4×4×4 | 12 | 36 | **No** |

So exact composition is viable for a narrow band of cases and explodes immediately outside it. It
also produces a surface of a degree nobody wants downstream even when it fits. It stays here as the
reason a control point budget exists, and as a note for anyone who later wants an exact mode for the
small cases — the route would be `decomposeSurfaceIntoBezierPatches`, compose each patch, knit.

---

## 6. Implementation notes worth keeping

### 6.1 Bernstein evaluation by recurrence, not by factorials

Both old features evaluated `B_i,n(t) = C(n,i)·(1−t)^(n−i)·t^i` directly, computing three factorials
per basis value inside the innermost loop of the hottest function in the feature — a degree-8 lattice
recomputed `8!` repeatedly. It also evaluates `0^0` at both ends of the parameter range, which is a
convention away from being undefined.

Replaced with the standard triangular recurrence, which builds all `degree + 1` values at once, is
exact in the same arithmetic, and has neither problem.

### 6.2 Degenerate lattice directions are inflated, not epsilon-guarded

A planar face gives a bounding box with zero thickness. The old code met the resulting division by
zero with a volume epsilon: it stopped the throw, but pinned every control point at parameter 0 in
the flat direction, so **pulling a flat face out of plane — the single most obvious thing to want
from FFD — did nothing**.

Any direction whose extent is below 10% of the box diagonal is now inflated symmetrically about its
own centre to that value. The flat direction gets a real extent and the surface sits at its
mid-parameter, where both boundary layers of lattice points can pull on it. The parameter solve
needs no epsilon afterwards, because the lattice volume is non-zero by construction; guarding it a
second time would only hide a genuinely broken frame.

### 6.3 The lattice bounds control points, not surfaces

Deliberate, and not a lazy approximation of the tighter box. The control hull contains the surface,
so a lattice built on it contains the surface too, and **every control point is inside the lattice
with parameters in `[0, 1]` by construction**. Bounding the surfaces instead would leave control
points outside the box, where the Bernstein basis extrapolates and behaves nothing like it does
inside.

### 6.4 An oriented lattice is nearly free

The parameter solve is Sederberg & Parry's inversion by scalar triple product, which never assumed an
axis-aligned box — for each axis, the triple product against the other two projects out that axis's
parameter and cancels the other two. It works for any non-degenerate axis triple. So orienting the
lattice to a mate connector costs one `evMateConnector` call and a `fromWorld` on the bounding pass.
Both old features hardcoded world X/Y/Z, which meant deforming anything whose natural directions were
not the global ones started by fighting the lattice.

### 6.5 The selection transform is live, then baked

A `fullTriadManipulator` reports one **cumulative** transform relative to the base it was last
handed, not an increment. Folding that into per-point offsets on every drag frame would need either
composing transform inverses or re-reading the face geometry hundreds of times per drag.

So the transform has two stages:

- **Live.** Stored decomposed (flat nine-value rotation behind `isAnything`, plus three lengths —
  the only shape that round-trips through a precondition, as `routingCurve.fs` and the old planes
  feature both concluded) and applied at regeneration on top of the committed offsets.
- **Baked.** Committed into per-point offsets exactly once and reset to the identity. Four moments
  reach that state, all of them points at which the live transform is about to stop describing the
  displacement it described before: a selection change by click (the manipulator handler), a
  selection change through the dialog, a change to anything that *defines* the lattice — span counts,
  orientation, the faces — and the dialog being **opened**.

  The last two were added after the symptom "my lattice point offsets aren't showing up" was traced
  to them. A live transform is invisible: it lives entirely in `ALWAYS_HIDDEN` parameters, so a dialog
  closed while one is live reopens showing a deformed surface and an empty offsets list. Baking on
  open is exactly geometry-preserving — it is the same `selectionTransformInWorld` arithmetic
  `applySelectionTransform` was already applying at every regeneration — so the only thing that
  changes is that the deformation becomes visible and editable. Baking on a lattice change is a
  correctness fix rather than a cosmetic one: the base is the selection's centroid in the lattice
  frame, so changing the span counts under a live transform silently *redefines* it.

  **The bake must not be rolled back by the editing logic that follows it.** The manipulator handler
  bakes before it swaps the selection in, and Onshape then runs the editing logic function on top,
  with an `oldDefinition` that predates that bake. The old definition is the right source for the
  *selection* and the *lattice geometry* the transform was measured against, and the wrong source for
  the *offsets* — taking those from it too reverted the bake on every manipulator-driven selection
  change, so the drag disappeared from the list and from the geometry at once. This was the original
  form of the bug above, and `bakeAgainstPreviousState` exists to hold that split in one place.

  **A bake that cannot run keeps the transform** rather than resetting it. Resetting a transform that
  was never committed does not defer the deformation, it deletes it. Reaching that path needs an
  unreadable lattice, which is a state the feature body cannot regenerate from either — so the
  misapplication it would guard against can only occur where the user is already seeing an error,
  while the work it destroys is real and silent.

**The full triad is unconditional, and that is what keeps this to two doors.** An earlier version put
it behind an edit-mode selector opposite a translate-only `triadManipulator`, which was wrong twice
over: a `fullTriadManipulator` already carries translation arrows as well as rotation rings, so the
translate-only mode was strictly less capable at no saving, and the rotation — the one capability
nothing else in the feature provides — was hidden behind a dropdown most users never opened.

It also cost a bug. Two manipulators wrote the same selection through *different* storage: the full
triad through the live transform, the plain triad through per-point offsets. Only the full triad
writes the transform, but `applySelectionTransform` applies it whichever manipulator is showing, so
switching to translate without a bake left it live underneath a handle that could not see it — the
drag landing short by the transform's average displacement, and compounding on every subsequent drag
because the transform stayed live and kept being reapplied. A pure rotation hid it entirely, since
rotation about the selection centroid preserves that centroid and the average displacement is zero.

Making the triad unconditional deleted the mode, the second writer, and that whole class of bug at
once. **One manipulator owns the selection.**

The base coordinate system sits at the centroid of the selection's positions **before** the live
transform, oriented to the lattice's own axes — so rotating a constant-N plane rotates it about its
own centre in the lattice frame, and the manipulator does not compound its own transform against
itself on each regeneration.

The consequence worth noting: there is **one canonical storage**, per-point offsets keyed by
`(u, v, n)`. The full triad is a *tool that authors offsets*, not a second parallel state. The old
planes feature stored per-plane transforms as the primary record, which is why its state broke when
the plane count changed.

### 6.6 Periodicity survives deformation with no special case

A periodic direction's stored form carries `degree` rows that are literal copies of the first
`degree`. The deformation map is a pure function of position, so equal points deform to equal points
and the overlap condition that makes the surface closed cannot be broken. `editSurface.fs` has to
work for this because a *user* can move one copy without its twin; a deformation map cannot.

Refinement of a periodic direction goes through the module's periodic machinery
([SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) §2.3), and emission converts
to the closed-clamped form the kernel returns for a revolve and provably accepts back — handing it
the module's internal wrap form earns a `PERIODIC_BSPLINESURFACE_NOT_SMOOTH`.

### 6.7 Replace face is right for one face and warned about for several

`opReplaceFace` re-derives the replaced face's boundary by **intersecting with its neighbours**. For
a non-affine map — which every interesting FFD is — the intersection of two deformed faces is *not*
the deformation of their original intersection. Replacing a strip of adjacent faces one at a time
therefore retrims each against neighbours that have not been deformed yet: order-dependent and
subtly wrong.

The feature warns when Replace face is used with more than one face selected, and points at §6.8's
trimmed mode as the alternative: per-face trimmed sheets carry their own extracted UV loops and have
no cross-face coupling to get wrong, so they can be deformed independently and knitted afterwards.

### 6.8 Trimmed output, and why the loops need no reprojection

**The trim loops survive the entire pipeline unchanged.** They are 2D curves in the surface's
*parameter* space, and nothing between the read and the emission touches that parameterization:
degree elevation raises every knot's multiplicity but keeps the interval, knot insertion adds knots
strictly inside it, and the deformation moves control points while leaving knots, degrees and weights
alone. So a deformed surface can be trimmed with the loops read off the *undeformed* face, with no
reprojection and no resampling — this is exactly the invariance
[EDIT_SURFACE_SPEC.md](EDIT_SURFACE_SPEC.md) §2.3 records, and FFD is in a stronger position to use
it than editSurface was, because a deformation cannot reparameterize by construction.

The mechanism itself is transcribed from `editSurface.fs`, dead ends included: `boundaryBSplineCurves`
is single-loop by contract, multi-loop and the keyhole splice are both refused, and holes are
therefore made **topologically** — patch per hole, imprint its edges with `opSplitFace`, delete the
patches, `opDeleteFace` the enclosed faces with `leaveOpen`. See §2.3.1 there for the measurements.

Two consequences specific to putting this behind a deformation:

- **Trimmed mode approximates unconditionally**, because only `evApproximateBSplineSurface` returns
  loops and the loops must come from the *same call* as the surface they parameterize. The standing
  no-silent-approximation rule is met by saying so, not by an exception. Both the feature body and
  the manipulator path go through the one read, because the lattice is bounded by these control
  points — reading exactly in one and approximately in the other would put the handles somewhere the
  regeneration does not.
- **Certification needs an untrimmed twin.** The samples run over the whole parameter rectangle, but
  a trimmed sheet only exists inside its loops, so every sample outside the trim would report its
  distance to the nearest trim *edge* — measuring the trim rather than the deformation. Trimmed mode
  therefore builds a full untrimmed body of the same control net, certifies against that, and deletes
  it. A trim is a topological restriction of the surface, not a different surface, so certifying the
  twin certifies the sheet everywhere the sheet exists.

**The periodic escape hatch.** A periodic direction is the one place the invariance can fail, because
normalizing a closed-clamped read into the module's wrap form re-cuts the knot vector. Rather than
reason about when that shifts the domain, the feature **measures** it — domain before, domain after —
and drops the trim with a warning if it moved, exactly as `editSurface.fs`'s trim probe did. A moved
domain means the loops describe a region of a parameter space that no longer exists; cutting the
wrong shape silently is the one outcome worse than emitting untrimmed.

### 6.9 Planarize is a one-shot edit, not a constraint

The **Planarize selection** button is a real `isButton` parameter — satisfied by the value staying
undefined, which is why it is absent from the defaults map — reaching the editing logic as its
`clickedButton` argument. It fits the least-squares plane through the selected points (centroid for
the origin; for the normal, the smallest-eigenvalue eigenvector of `sum (p - o)(p - o)^t` via SVD, the
derivation and route both taken from std `editCurve.fs`'s private `fitPlane`) and projects each point
onto it along the normal.

It writes ordinary per-point offsets through the same `addToPointOffset` every drag uses, so the
result is indistinguishable from having dragged the points there by hand and **nothing keeps them
coplanar afterwards**. A persistent planar constraint would have to survive lattice resolution
changes and fight every subsequent drag; that is a different feature, and a button that silently
becomes a constraint is worse than one that does not.

Degenerate selections need no special case. For collinear points the covariance matrix has rank 1, so
the fitted normal is perpendicular to the line and the plane therefore *contains* it — every point is
already on the plane and the projection moves nothing. A row scope pressing the button is a no-op
rather than an error, which is the right answer: a row is already as planar as a line can be. Only a
selection of fewer than three points is rejected outright.

---

### 6.10 A deformable surface needs C1 by structure, not by arrangement

The hardest defect in this feature, and the one design constraint everything periodic follows from.

**The requirement.** A knot of multiplicity *m* in a degree-*d* direction leaves the surface
C^(d−m) there. At *m* = *d* the surface is C⁰: **free to crease**. Such a surface is G1 across that
knot only because its control points happen to be arranged for it — collinear, with matching weight
ratios. Nothing in the structure requires it, and an FFD is a nonlinear map, so it does not carry
collinear points to collinear images. The arrangement is destroyed the moment a lattice point moves,
and `opCreateBSplineSurface` refuses the body: `PERIODIC_BSPLINESURFACE_NOT_SMOOTH` when the offending
knot is the seam, `BSPLINESURFACE_NOT_G1` when it is interior. The kernel enforces G1 everywhere.

So: **FFD is only valid on a surface whose interior knots all have multiplicity ≤ d − 1.** Below that
the direction is C1 or better *for any control points whatsoever*, which is the only form of
smoothness a deformer can rely on.

A revolve violates this by construction. Onshape returns its closed direction as rational Bézier
arcs — the vase reads `vDegree 3` with `V knots: 0 x4, 0.5 x3, 1 x4`, every join at full
multiplicity.

**Why the obvious tools cannot establish it.** Knot *insertion* only ever raises multiplicity, so
refinement cannot help however fine it goes. Knot *removal* cannot either: the module withdrew its
periodic attempt after it returned a cylinder "as a bean" with a small reported deviation, and the
diagnosis was structural rather than a bug — A5.8 is a local recurrence anchored on control points it
assumes are fixed, which on a periodic spline are themselves images of points the true answer changes.
Exact removal is impossible regardless, a NURBS circle being genuinely C⁰ at its arc joins in
homogeneous space, smooth only through weight cancellation.

**What was ruled out along the way**, since each cost real time and the evidence is worth keeping:

| Hypothesis | Falsified by |
| --- | --- |
| Degree elevation leaves an unminimized knot vector and creases the seam | Failure reproduced identically under *Inherit*, which skips elevation |
| The wrap ↔ closed-clamped round trip corrupts the control net | Failure reproduced with refinement and elevation both off, on a net provably bit-identical to the original feature's — at a multiplicity-degree knot the clamp's Boehm coefficients collapse to α = 0, so the conversion copies rather than computes |
| Refinement causes it | Same run: no refinement, same failure |
| Relocating the seam onto a simple knot is sufficient | It moved *both* arc joins into the interior and the kernel answered `NOT_G1`. Necessary reasoning, insufficient fix; the code was removed once §6.11 subsumed it |

**The clinching observation** came from the user: a whole lattice *plane* dragged at once builds, a
single point does not. That is exactly what the theory predicts. Moving one point gives
`F = identity + B_i(u)B_j(v)B_k(n)·Δ`, which along a V-line is quadratic in *v* and destroys
collinearity. Moving a whole constant-N plane collapses by partition of unity to
`F = identity + B_k(n)·Δ` — **affine in v**, so collinearity survives exactly. It also explains why
the predecessor features never appeared to have this problem: the planes feature could only ever move
whole planes.

**Elevation therefore stays blocked on periodic directions**, now for a precisely stated reason:
elevation drives every knot back to full multiplicity and would undo §6.11 immediately. Nothing is
lost — a revolve's closed direction is already an exact circle, and the detail a deformation needs
comes from refinement, which inserts distinct knots at multiplicity 1.

### 6.11 Periodic simplification by cyclic projection (2026-08-10)

The fix, and a new module capability. Full derivation in
[SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) §2.2.0; the FFD-side summary:

`simplifySurfacePeriodicDirections` **projects** the control net onto a nested coarser space. Form
the common refinement `U_com = U_cur ∪ U_tgt` where `U_tgt` has every multiplicity 1; lift into it
exactly by periodic knot insertion; solve `min ‖A·Q − P_com‖₂` cyclically, `A` being the refinement
operator `S(U_tgt) → S(U_com)`.

**The surface is never evaluated** — no sample points, no interpolation conditions, no loft. It is
exact whenever the knots are genuinely removable (the residual is identically zero, since a
refinement operator between nested spaces is injective), and its error otherwise carries a
partition-of-unity sup bound computed from control points alone.

Three FFD-specific consequences:

1. **It runs as step zero of `deformToTolerance`**, before elevation, for the ordering reason in
   §6.10.
2. **Half the tolerance is allocated to it.** The kernel certification measures the emitted body
   against the *simplified* surface's own true deformation, so it structurally cannot see this
   error. The two are independent sources against one budget, added by the triangle inequality;
   `deformOneFace` reports the sum.
3. **A simplified face is emitted untrimmed.** The re-fit preserves the domain and the period, so
   `domainHeld` stays true, but the parameter-to-point map shifts by up to the deviation — and trim
   curves are measured in that map. Small, but cutting the wrong shape silently is worse than
   untrimmed. `Replace face` carries the trim topologically and is unaffected.

For a revolve the cost is negligible and the net gets *smaller*, not larger: a uniform periodic cubic
on a circle carries radial error about `r·(2π/n)⁴/384`, so n = 16 lands at 0.007 mm and n = 24 at
0.0014 mm on a 100 mm part, against a default tolerance of 0.055 mm.

### 6.12 …and then it has to be EVEN as well (2026-08-10)

§6.11 makes a revolve deformable. It leaves it **bunched**, and that is a separate defect with a
separate fix — now `uniformizePeriodicSurfaceDirections`, which step zero calls instead.

A projection preserves the parameterization it projects, and a revolve's two rational cubic half-arcs
run about **1.7 : 1 slower in parameter at the arc joints than mid-arc** (`t = 0.0625 → 16.3°`,
`0.125 → 36.9°`, `0.25 → 90°`). §6.11's target — the existing breakpoints bisected — is uniform in
*parameter*, so the net comes back deformable and ~1.7 : 1 crowded in two bands at θ = 0 and θ = 180.
**The refinement loop cannot heal it**, and that is the part worth remembering: doubling hands
`arcLengthSpanInsertions` a budget equal to the span count, so every span receives exactly one
arc-length cut and the ratio is preserved at every level, forever. The deformed body inherits the
bands, and so does every downstream feature that reads its `u`/`v`.

The refit lands the knots evenly in arc length *and* makes `u`/`v` proportional to it — on the
kernel's own cylinder numbers, the chord-spacing ratio at evenly spaced parameters goes from 1.98
to 1.0006, at 12 control points per period. Full derivation and the four measured wrong answers
behind it in [SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) §2.2.0b.

Three FFD-side consequences, replacing §6.11's:

1. **The tolerance accounting is unchanged** — still half the budget, still added to the certified
   deviation by the triangle inequality, still reported as `uniformizationDeviation`. What changed is
   that the number is now a *measured* distance to the original surface rather than a structural sup
   bound, so it is measured perpendicular-ish (a windowed minimum to the original curve, which
   ignores relabelling but stays honest at a corner) and on true isocurves rather than grid rows.
2. **`trimDropped` is now a stronger claim, not a weaker one.** §6.11's note said the parameter map
   shifts "by up to the deviation". It now shifts *by design* — on a revolve a given `u` moves by
   several degrees of arc — so a uniformized face must be emitted untrimmed. A held domain no longer
   implies a held map, and the domain check cannot see the difference.
3. **It is idempotent.** A direction already all-simple, evenly spaced and even in arc length inside
   2% is returned untouched, so a regeneration does not re-fit its own output and walk the surface
   away a tolerance at a time.

## 7. Not built

- **§9.1.1 step 6**: automatic re-refine on a failed certification. Currently certify-and-warn (§4.2).
- **C⁰ interior knots on a CLAMPED direction.** §6.11 solves this for periodic directions, where the
  hazard is unavoidable because a revolve is always delivered that way. A clamped direction can carry
  it identically — an elevated or Bezier-decomposed one creases under deformation just the same, and
  elevation guarantees the condition, which is why periodic directions skip elevation entirely
  (§6.10). It is latent rather than visible on a face with no interior knots, which most simple test
  faces are, so expect it on a spline face that already has them. **This is the single highest-value
  remaining fix for this feature**, and the projection generalizes directly: what a clamped direction
  needs on top is that its ends legitimately sit at multiplicity degree + 1 and must be excluded from
  the target's collapse. Doing it would also lift the periodic elevation block, since elevation
  followed by projection is well defined.
- **Directional adaptivity.** The loop refines both directions together. §9.1.1 notes that `delta`
  per control point localizes the error, so a bend along U should not refine V. This is the biggest
  remaining efficiency win and it is a change to the loop, not to anything around it.
- **Non-linearity seeding.** The loop starts from the elevated surface and doubles. §9.1.1's seeding
  argument says the map's non-linearity should pick the first level. For FFD the natural hint is the
  composed degree bound of §5, but an aggressive seed permanently bloats the output — the loop only
  ever refines *upward* — so a conservative start plus doubling was preferred over a clever guess.
- **Exact composition mode** for the small cases where §5's degree bound fits.
- **Lattice cell boundary refinement.** §9.2 suggests `refineSurfaceToSpanDensity` with the lattice
  cell boundaries mapped into surface parameter space. That presumes a *piecewise* lattice; a
  classic Sederberg & Parry lattice is a **single trivariate Bernstein polynomial over the whole
  volume**, with no interior cell boundaries and no C⁰ creases to resolve. If a B-spline lattice is
  ever added — which is what would make local lattice control possible — that guidance applies
  directly and `refineSurfaceToSpanDensity` is waiting for it.
- **Scaling in the triad.** `fullTriadManipulator` is translation and rotation (6-DOF). The old
  planes docs claimed scaling; the code never had it. A taper is authored by translating the corner
  points of a plane, or by moving two opposing rows.

---

## References

- Sederberg, T.W. and Parry, S.R., *Free-Form Deformation of Solid Geometric Models*, SIGGRAPH 1986.
- `whitepaper-references/"Free-Form Deformation of Parametric CAD Geometry.pdf"`
- [SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) — §3.2 (control-net
  deformation is exact only for affine maps), §9.1 (the deformation pipeline), §9.1.1 (the
  refinement criterion), §9.2 (FFD specifically).
- [EDIT_SURFACE_SPEC.md](EDIT_SURFACE_SPEC.md) — the multi-select control point interaction this
  feature's lattice editing follows.
- [../featurescript-guides/TRIAD_MANIPULATOR_NOTES.md](../featurescript-guides/TRIAD_MANIPULATOR_NOTES.md)
  — `triadManipulator` versus `fullTriadManipulator`, and the storage pattern for each.
- [../features/free-form-deformation/README.md](../features/free-form-deformation/README.md) — the
  user-facing guide.
