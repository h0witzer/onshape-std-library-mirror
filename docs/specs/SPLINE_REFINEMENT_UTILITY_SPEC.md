# Spline Refinement Utility — Refactor Plan

Status (2026-08-07): **Layers 1–2 AND every curve-level Layer 3 entry point are verified
passing in Onshape — 78/78 checks, confirmed on a second live run after one bug fix.** Only the
eight surface-level hooks remain unimplemented.

`custom-features/splineRefinementUtils.fs`, FeatureScript 3044:
- **Verified passing in Onshape (78/78 checks, second live run):** Layers 1–2 (span
  arithmetic, the operator builder, apply + tensor apply, clamped extraction, uniform period
  extraction), direct insertion (`insertKnotOnce` / `refineKnotVector`), the pure surface
  evaluator (`evaluateBSplineSurfacePoint`), and every curve-level Layer 3 entry point —
  `normalizeSplineDefinition` (force-rational and genuine periodic clamping both),
  `mergeKnotVectors`, `refineSplineToControlPointCount`, `makeSplinesShareKnotVector`,
  `decomposeIntoBezierSegments`, `elevateSplineDegree` (four degree-pair cases including a
  repeated-interior-knot one), `makeSplinesCompatible`, `prepareSplineForDeformation` — plus
  the shared private helpers they're built from and one promoted to public API,
  `insertionsToReach`. This is the entire dependency chain §7 called out as "curves first."
  **Phase 3 (tweenCurves) and Phase 4's curve half are unblocked with zero remaining module
  risk.**
- **One bug found and fixed on the first live run of this batch:** `findKnotSpanIndex`'s
  backward scan had no upper bound, so a parameter exactly equal to an array's own reported
  domain end — which is exactly what `normalizeSplineDefinition`'s periodic clamp inserts at —
  walked past the last valid span into whatever trailing structure exists beyond the domain (a
  periodic wrap window's extra knots), indexing one past `buildRefinementCoefficients`'
  control point array. Fixed with the missing half of NURBS Book Algorithm A2.1's `FindSpan`
  (`u == U[n+1] -> n`), a one-sided special case (domain end only — the domain start side was
  already safe by construction, and `findEvaluationSpanIndex`, the separate evaluator-side span
  finder, never had this bug since its loop bounds were already correct). Confirmed fixed by
  the second live run; did not change any previously-passing test's behavior.
- **Still `HOOK(...)` stubs:** the eight surface-level entry points
  (`refineSurfaceToControlPointCounts` through `prepareSurfaceForDeformation`). Each one's doc
  comment names exactly which curve-level helper it generalizes and how — tensor-apply the
  same operators via `applyKnotRefinementOperatorDownColumns`/`AcrossRows` instead of the flat
  `applyKnotRefinementOperator` — since the hard math already exists and only needs a
  grid-shaped sibling.

`custom-features/splineRefinementTester.fs` runs vectors 1–6 (structural), evaluator sanity,
and eight curve-level vectors (normalization including genuine periodic clamping, merge,
refine-to-count, share-knot-vector, Bezier decomposition, degree elevation — vector 7, four
degree pairs — compatibility, and the elevate-then-refine composition). 78 checks, 0 failures,
confirmed live.

**Sequencing note:** §7 documents Phase 2 (tweenSurfaces) before Phase 3 (tweenCurves), on the
rationale that surfaces would exercise the module more thoroughly before trusting it. That
rationale is now moot — the curve-level run above already exercised
elevation/refinement/merge/periodic/rational across 78 checks. tweenCurves is fully unblocked
today; tweenSurfaces still needs the surface hooks first. Worth reconsidering the order (see
open question added to §11).

**One gap flagged, not resolved:** `normalizeSplineDefinition`'s periodic handling ports two
things — a narrow kernel-quirk fix (verbatim from std `editCurve.fs`
`cleanUpPeriodicBSplineDefinition`, for a specific single-overlapping-knot shape) and a new
genuine periodic-to-clamped conversion this module adds (running `clampedSegmentOperator` over
the spline's own domain — sound because Boehm insertion is a purely local array operation,
independent of the periodic flag). The genuine-clamping half has a tester vector. The
kernel-quirk half does not — its exact trigger shape could not be verified without a live
kernel-returned periodic curve to inspect, so the tester vector was designed to route around it
(construction with `knots[0] == 0` deliberately skips that branch). If a real periodic surface
from `evApproximateBSplineSurface` trips it, treat that branch as unverified.

**KnotArray discipline, worth restating here because it is easy to miss:** every curve-level
function above casts its returned `knots` with `knotArray(...)` before returning, even though
the Layer 1–2 operators underneath produce plain arrays throughout. This is not cosmetic —
FeatureScript's typecheck types don't propagate through array operations, so a plain array
does not implicitly satisfy `is KnotArray`, and `bSplineCurve`/`opCreateBSplineSurface` will
reject it. `tweenSurfaces.fs` already carries its own defensive cast for exactly this reason.
Every surface hook must do the same for `uKnots`/`vKnots`.

Consumers to migrate: `custom-features/displacementMap.fs`, `custom-features/tweenSurfaces.fs`,
`custom-features/tweenCurves.fs`.

**Scope: exact refinement.** Every operation here adds representational degrees of freedom
while leaving the geometry bit-for-bit unchanged — knot insertion, knot refinement, Bezier
decomposition, and **degree elevation**. The lossy inverses (knot removal, degree reduction,
approximation) are out; std already exports `removeKnots` and `approximateSpline` for those.
That line is what makes the module safe to call speculatively: refining never costs accuracy,
so a consumer can refine first and ask questions later.

Elevation was initially scoped out and is now **in** — see §3.1 for why that is forced rather
than convenient.

Reference: *The NURBS Book*, ch. 5 (Fundamental Geometric Algorithms) — the module is
essentially that chapter's exact half. A copy is in the repo at
`whitepaper-references/The NURBS Book-1-341.pdf`.

Related: [DISPLACEMENT_MAP_TILING_SPEC.md](DISPLACEMENT_MAP_TILING_SPEC.md) (§5.1–5.4 are the
mathematical reference for the insertion core, and §5.3 is its primary test vector).

---

## 1. What is actually duplicated

Seven sites in this repo need exact spline refinement. Almost none of them share code, and
they are **not** copies of one function — they are five different approaches to the same
problem, of which one is correct, which is why the behaviour diverges so widely. Three of them
avoid the mathematics entirely by sampling and refitting.

| Site | What it does | State |
| --- | --- | --- |
| `displacementMap.fs` `extractionWeights` / `applyExtraction` / `refineTileSeed` (lines 704–858) | Boehm single insertion, run **once on unit-basis coefficient rows** to build a sparse linear operator, then applied to every row and column of the control grid | **Correct and perf-tuned.** The reference implementation. |
| `tweenSurfaces.fs` `insertKnotBoehm` (line 1164) + `refineCurveControlPointCount` (1080) + `refineControlPointCount` (909) | Boehm single insertion, applied directly per isoparametric curve | **Broken** — see §2 |
| `tweenCurves.fs` `matchCPCount` (line 456) | Avoids knot insertion entirely: samples the curve, refits with `approximateSpline`, falls back to using raw sample points as control points | **Lossy by construction** — not shape-preserving |
| `tweenCurves.fs` + `tweenSurfaces.fs` `subdivideIntoBeziers` / `splitAtFirstKnot` / `elevateBSpline` | de Boor subdivision for degree elevation | Duplicated, but **correct**. `tweenCurves.fs` lines 295–388 are a byte-for-byte copy of std `editCurve.fs` lines 479–572 (verified by diff, 2026-08-07); `tweenSurfaces.fs` lines 1306–1482 are the same algorithm re-typed with longer variable names and the periodic branch dropped. |
| `konstantin-spline-expansion/3dSpiral.fs` lines 103–202 (Tweep's curve generator) | Uniform 3D arc-length **resampling** of a generated point list, then `opFitSpline` | Works, but is the sample-and-refit workaround again — see §1.1 |
| `opFlex.fs` `flexEdgePoints` / `flexEdge` (lines 865–933) | Split faces, sample every edge at a user-set step (default 0.5 mm), transform the points, `opFitSpline`, then `opLoft` / `opFillSurface` the subfaces back from the refit boundaries | Approximation at every stage — see §1.2 |
| `deformPascoe.fs` `createDeformedBSplineFace` (lines 5112–5180) | Deforms the control net of `evApproximateBSplineSurface` directly, keeping the knot vectors | **Right primitive, missing the refinement step** — see §1.2 |

### 1.1 Tweep / 3D Spiral — the same problem, solved a third way

`3dSpiral.fs` is the curve generator at the core of Tweep (`twistedSweep.fs:177` feeds its
sweep path into it). It has **no knot arithmetic** — `git log -S "knot"` over
`custom-features/konstantin-spline-expansion/` returns nothing, and the folder has never
contained the string.

But the endpoint problem it fixed is real and is in the same family. Commit `b78bf2b`,
*"Fix curvature discontinuities in 3D Spiral with uniform arc-length resampling"* (#54), is the
fix, and its commit trail is a tour of the wrong answers: derivative constraints at path
transitions → "*prevent over-constraining and uneven CP distribution*" → "*derivative
constraint approach not effective*" → arc-length resampling → "*fix wrap-around segment
handling in closed path resampling*" → "*remove destructive closed path processing*". The
surviving comment at line 104 names the cause outright: *"non-uniform CP density caused by
interaction between rotation and transformation."*

So: **control point distribution on a periodic loop**, fixed by redistributing sample points
before an interpolating fit. That is the identical failure class as `tweenCurves.matchCPCount`
— when you cannot control the control net directly, you resample and refit and hope the fitter
cooperates. `opFitSpline` chooses its own knots, so knot insertion was never available as a
lever there.

It becomes available if the spiral is built with `opCreateBSplineCurve` from a control net this
module hands it, instead of `opFitSpline` from points. Then the seam is periodic **by
construction** — there are no endpoints to look weird — and control point density is chosen
rather than negotiated. That is a rewrite of how the spiral is constructed, not a drop-in
replacement, so it is listed as a **downstream candidate in §9**, not a migration phase.

### 1.2 The two flex features — the case that sets the module's scope

Both live in this repo.

**`opFlex.fs`** is the tessellate-and-refill approach in full. `flexEntities` splits the input
into subfaces, samples **every** edge — including the temporary edges the split just created —
at `edgeSamplingStep` (default 0.5 mm, so a 500 mm edge is ~1000 samples), pushes each sample
through `convertFunction`, fits a spline through the results with `opFitSpline`, and then
rebuilds each subface with `opLoft` or `opFillSurface` from those refit boundaries
(lines 548–605). Nothing in the chain is exact: the sampling discards the analytic curve, the
fit approximates the samples, and the fill invents an interior that was never computed. The
sampling step is a user-visible magic number precisely because there is no principled value for
it. The output cannot be better than the tessellation, and the errors compound across three
stages.

**`deformPascoe.fs`** gets much closer, and the gap is instructive.
`createDeformedBSplineFace` does the right thing in outline — take
`evApproximateBSplineSurface`, push every control point through `deformPoint`, rebuild with
`opCreateBSplineSurface`, reusing `uKnots` / `vKnots` unchanged (lines 5112–5178). That is
control-net deformation, which is the correct foundation. Three things stop it working:

1. **No refinement before deforming.** The control net is whatever the kernel chose to
   represent the *undeformed* surface. A planar face comes back as a 2×2 net; a cylinder as a
   handful of points in one direction. Deforming those and keeping the knot vector means the
   result can only be as detailed as the original needed to be — bend a flat face through a
   curve and you get the bilinear shadow of the intended bend. This is the same ceiling as
   §9.2, and it is the missing mathematical foundation.
2. **No elevation, so smoothness is capped.** See §3.1 — this is the harder half. A degree-1
   direction cannot be made smooth by adding control points.
3. **Trimmed faces.** Line 5176 says it outright: *"opCreateBSplineSurface cannot recreate
   those holes in this fallback."* Boundary curves are deformed separately by
   `deformBSplineCurveArray` (also unrefined, so trim curves drift off the deformed surface),
   and the whole boundary step sits behind a `try silent` at line 5155 that swallows the
   failure before the check on the next line can report it.

Point 3 is not a math gap and this module does not fix it — but the repo already contains the
answer. `displacementMap.fs` builds an **untrimmed** template surface and lets `opReplaceFace`
carry the original face's trim loops over for free; per [[displacement-map-tiling]],
`opReplaceFace` accepts a many-face template against one target face, and trimming came free.
Deform-refine-replaceFace gets holes and inner loops without needing
`boundaryBSplineCurves` at all.

Together these are the strongest argument for the module: a flex/deform feature built on exact
refinement plus `opReplaceFace` would be shorter than either existing attempt and exact where
both are approximate. That is a **new feature**, not a migration phase — see §9.1.

The std library gives us the neighbours but not the piece we need: `nurbsUtils.fs` exports
`removeKnots`, `separatePointsAndWeights`, `combinePointsAndWeights`; `splineUtils.fs` exports
`elevateBezierDegree`, `approximateSpline`, `evaluateSpline`; `curveGeometry.fs` exports
`makeUniformKnotArray`, `padKnotArray`, `knotArrayIsCorrectSize`. **There is no knot insertion
or knot refinement anywhere in the standard library.** That gap is what every site above filled
independently.

---

## 2. Defects the refactor has to fix

These are the reasons "Tween Surfaces never quite worked". They are stated concretely so the
new module can carry regression tests for each.

### 2.1 `insertKnotBoehm` computes the wrong control point at index `k+1`

The textbook form (SPEC §5.1) is:

```
Q[i] = P[i]                                    i <= k - degree
Q[i] = alpha*P[i] + (1 - alpha)*P[i-1]         k - degree + 1 <= i <= k
Q[i] = P[i-1]                                  i >= k + 1
```

`tweenSurfaces.fs:1242-1258` handles `i == k + 1` with a *second blend* instead of the plain
copy `Q[k+1] = P[k]`, guarded by a comment claiming the case "shouldn't happen for single knot
insertion in a clamped B-spline". It is in fact the common case: for a clamped spline the only
span where `k + 1 == numControlPoints` is the **last** one, so every insertion anywhere else
takes the wrong branch.

Worked example — degree 2, `P0..P4`, `U = [0,0,0,1,2,3,3,3]`, insert `0.5` (span `k = 2`):

- correct: `Q3 = P2`
- produced: `alpha = (0.5 - U[3]) / (U[5] - U[3]) = -0.25` → `Q3 = 1.25*P2 - 0.25*P3`

A negative weight is an **extrapolation**, so the result leaves the control hull. This is the
mechanism behind warping and overshoot, and it fires on essentially every refinement.

### 2.2 No multiplicity guard

`refineCurveControlPointCount` distributes insertion parameters uniformly across the domain
with `startParam + (endParam - startParam) * i / (numToInsert + 1)`. For a uniformly
parameterized input those values land **exactly on existing interior knots**, silently raising
multiplicity. At multiplicity `degree` the surface loses tangent continuity along that
isoparametric line; at `degree + 1` it comes apart. Boehm's formula itself stays valid up to
`degree + 1` — clamped extraction (tiling spec §5.2) relies on exactly that — but a
*refinement* entry point must never exceed `degree`, and this code has no guard at all.

### 2.3 Periodic inputs are not handled

Onshape's `KnotArray` is always `numControlPoints + degree + 1` long
(`curveGeometry.fs:321`), but a **periodic** array carries wrap-around padding computed from
the marginal knot differences at the far end (`makePeriodicKnotArrayPadding`, line 469), and
`evCurveDefinition` can additionally hand back periodic curves with *overlapping control
points* (`size(knots) == size(controlPoints) + 2*degree + 1`). Consequences:

- `tweenSurfaces.fs` re-wraps each refined isoparametric curve with
  `isPeriodic : surface.isUPeriodic` while feeding it knots that no longer satisfy the periodic
  padding relation.
- Its `size(knots) != numControlPoints + degree + 1` guard throws outright on the
  overlapping-control-point form.
- `tweenSurfaces.fs`'s copy of `subdivideIntoBeziers` **dropped** the `knots[0] < 0`
  (overlapping-knot) branch that std `editCurve.fs` and `tweenCurves.fs` both keep.

`evApproximateBSplineSurface` returns periodic surfaces for cylinders, cones and revolves, so
this is not an edge case for a feature that takes arbitrary face picks.

### 2.4 Matching control point counts is not the same as making two splines compatible

Both tween features reduce compatibility to "same degree, same control point count". That is
insufficient: interpolating `CP1[i]` against `CP2[i]` is only meaningful when both splines
share a **knot vector**, because the index `i` must refer to the same basis function in both.

The two features paper over this differently, and both papers tear:

- `tweenSurfaces.fs:455-474` **interpolates the knot vectors themselves**
  (`knot = k1*(1-f) + k2*f`). Endpoints stay exact, but every intermediate fraction produces a
  surface that is not the average of anything.
- `tweenCurves.fs:184-190` calls `bSplineCurve({...})` **with no `knots` field at all**,
  so the result is re-parameterized to Onshape's default uniform knots regardless of what the
  inputs were.

The correct operation is a **knot vector merge**: normalize both domains to `[0,1]`, take the
union of interior knots at max multiplicity, refine each spline up to that common vector, then
interpolate. This is exactly what knot refinement exists for, and it is the single largest
functional payoff of having the utility.

### 2.5 `append` in hot loops

Every site outside `displacementMap.fs` builds arrays with `append` inside `O(n)` loops, which
is `O(n²)` — see [[featurescript-append-perf]]. `displacementMap.fs` was measured at 60 s → 20 s
after preallocation plus the operator rewrite; the new module inherits the preallocated style.

---

## 3. Architecture

Three layers. The middle one is the important idea and comes straight from `displacementMap.fs`.

### 3.1 Why degree elevation is forced, not optional

The two operations look interchangeable — both add control points, both preserve geometry —
and they are not:

> **Knot insertion adds control points. It never adds smoothness.**
> Only degree elevation raises the continuity ceiling.

Refining a degree-`p` spline gives more handles, but the surface remains piecewise degree `p`,
and its continuity class tops out at `C^(p-1)` no matter how many knots are inserted — a new
simple knot leaves the geometry, and its smoothness, exactly as it was; only raising a knot's
multiplicity lowers the guaranteed class. Refine a degree-1 spline as finely as you like and
you get a finer polyline, never a curve.

For a deformation tool this is decisive, because `evApproximateBSplineSurface` hands back
whatever degree was sufficient for the **undeformed** face: degree 1 across a ruled direction,
degree 1 in both directions for a planar face. Bending a planar face along a guide curve
requires at minimum degree 3 in the bend direction to be tangent-continuous, and the only way
to get there is elevation. `deformPascoe.fs` reuses `sourceSurface.uDegree` / `vDegree`
unchanged (line 5136–5137), which is why its output cannot be smooth regardless of how good the
rest of the pipeline is. No amount of knot refinement fixes it.

So the correct pre-deformation sequence is **elevate to the target continuity, then refine to
the target resolution** — in that order, since elevating after refining multiplies the control
point count by more than it needs to. Both halves must live in one module for a caller to
express that, which is why the scope moved.

Elevation also completes §2.4: two splines are compatible only when they share a degree **and**
a knot vector. The module owns both halves of compatibility or it owns neither.

### 3.2 Control-net manipulation is exact only for affine maps

This is the load-bearing fact for everything in §9, and it separates the two families of
consumer cleanly.

For a spline `S(t) = Σ N_i(t) P_i` the basis functions are non-negative and sum to 1, so for an
**affine** map `φ`:

```
φ(S(t)) = φ(Σ N_i(t) P_i) = Σ N_i(t) φ(P_i)
```

Deforming the control net *is* deforming the surface, exactly. For a **non-affine** `φ` the two
sides differ: `Σ N_i φ(P_i)` is a spline whose net happens to be the image of the original net,
which is not the image of the original surface.

That splits the consumers:

- **Tween is the affine case.** `(1-f)·A + f·B` over a shared knot vector is a linear blend, so
  once §2.4's compatibility is done the control-net interpolation is *exactly* the surface
  interpolation. There is no approximation anywhere in tween — only the compatibility question.
- **Every deformation worth having is the non-affine case.** Bend, twist, taper, flow along a
  surface — all non-affine. Control-net deformation approximates them.

The saving grace is that the approximation **converges under refinement**, because the control
polygon converges to the curve as knots are inserted (distance falls as `O(h²)` in the knot
spacing). So refinement is not merely "more handles" as §9.2 frames it for FFD — for a
non-affine map it is the mechanism that drives the error to zero, and it gives a *computable*
error bound: deviation between the deformed net and the true deformed surface, measurable and
refineable until under tolerance.

Contrast with `opFlex.fs`'s `edgeSamplingStep`, which has no error bound at all — you cannot
ask how wrong a 0.5 mm sampling step is without sampling finer and comparing. A refinement
tolerance is answerable. **That is the difference between the two approaches, stated precisely,
and it is worth putting in the feature's own header when it gets written.**

### Layer 1 — knot vector arithmetic (pure, no geometry)

Span lookup, multiplicity, domain, clamped/periodic classification, normalization, merge.
All plain numbers.

### Layer 2 — the refinement operator

> Knot refinement is a **fixed linear map on control points**. It depends only on
> `(knots, degree, parametersToInsert)` — never on the control point values.

So compute it once, symbolically, by running the insertion algorithm on unit-basis coefficient
rows; the accumulated coefficients *are* the weights. Represent the result sparsely:

```
{
    "degree"       : number,
    "inputCount"   : number,
    "outputCount"  : number,
    "knots"        : array,   // refined knot vector
    "rows"         : array    // rows[o] = [{ "index" : i, "weight" : w }, ...]
}
```

This buys three things at once:

1. **Reuse across a grid.** A tensor-product surface applies the same U-operator to every
   column and the same V-operator to every row. `displacementMap.fs` went from ~4000 insertions
   to a few dozen this way.
2. **Type agnosticism.** Weights are plain numbers, so the same operator applies to `Vector`s
   with length units, unitless parameter-space points, homogeneous 4-vectors for NURBS, or
   scalar height fields. Multiply **scalar-on-left** (`weight * point`) —
   see [[featurescript-length-vector-gotchas]].
3. **The algorithm seam.** Boehm is an implementation detail *inside* the operator builder.
   Swapping in Oslo / Lane–Riesenfeld (which inserts a whole knot set in one pass via the
   discrete B-spline recurrence, rather than `m` sequential rank-1 updates) changes nothing
   above this line. That is the "different flavors" requirement, satisfied structurally rather
   than by a parallel API.

Identity fast path: after a clamped refinement most output rows are a single term with
`weight == 1`, so they copy through with no arithmetic. `applyExtraction` already does this and
it stays.

### Layer 3 — task-level entry points

What the three features actually call. Each is a thin composition of layers 1–2.

---

## 4. Public API

Naming follows the house rule — explicit, no abbreviations, std-library readability.

```featurescript
// ---------- Layer 1: knot vector arithmetic ----------

export enum KnotInsertionAlgorithm { BOEHM }        // OSLO reserved for a future flavor

export const KNOT_PARAMETER_TOLERANCE = 1e-10;

/** Largest index k with knots[k] <= parameter < knots[k+1] and knots[k] < knots[k+1].
    Half-open and repeat-safe: repeated knots never yield a degenerate span. */
export function findKnotSpanIndex(knots is array, degree is number, parameter is number) returns number;

export function knotMultiplicity(knots is array, parameter is number) returns number;

export function knotDomain(knots is array, degree is number) returns map;   // { start, end }

export function isClampedKnotArray(knots is array, degree is number) returns boolean;

/** Accepts a BSplineCurve, a raw evCurveDefinition map, or a hand-built map. Strips the
    overlapping-control-point periodic form, forces a rational representation with unit
    weights, and guarantees size(knots) == size(controlPoints) + degree + 1.
    Port of the cleanUpPeriodicBSplineDefinition logic in std editCurve.fs. */
export function normalizeSplineDefinition(spline is map) returns map;

/** Union of two knot vectors over a shared domain, taking the max multiplicity of each
    distinct value. Both inputs must already be normalized to the same parameter range. */
export function mergeKnotVectors(knotsA is array, knotsB is array, degree is number) returns array;

// ---------- Layer 2: the operator ----------

export function knotRefinementOperator(knots is array, degree is number,
        parametersToInsert is array) returns map;
export function knotRefinementOperator(knots is array, degree is number,
        parametersToInsert is array, algorithm is KnotInsertionAlgorithm) returns map;

export function applyKnotRefinementOperator(operator is map, controlPoints is array) returns array;

/** Tensor helpers. grid[row][column]; "AcrossRows" refines within each row (the V/column
    direction), "DownColumns" refines within each column (the U/row direction). */
export function applyKnotRefinementOperatorAcrossRows(operator is map, grid is array) returns array;
export function applyKnotRefinementOperatorDownColumns(operator is map, grid is array) returns array;

/** Operator that clamps [startParameter, endParameter] to multiplicity degree+1 and slices
    out the sub-spline reproducing the input exactly on that interval. SPEC §5.2. */
export function clampedSegmentOperator(knots is array, degree is number,
        startParameter is number, endParameter is number) returns map;

/** The integer-uniform special case displacementMap.fs uses: knots 0,1,2,..., extract one
    period of spanCount spans out of spanCount + 2*degree + 1 control points.
    Exactly the current extractionWeights(degree, n). */
export function uniformPeriodExtractionOperator(degree is number, spanCount is number) returns map;

// ---------- Layer 3: task entry points ----------

/** Single Boehm insertion. Direct (non-operator) path for one-off use. Throws if the
    resulting multiplicity would exceed degree. */
export function insertKnotOnce(controlPoints is array, knots is array, degree is number,
        parameter is number) returns map;                       // { controlPoints, knots }

export function refineKnotVector(controlPoints is array, knots is array, degree is number,
        parametersToInsert is array) returns map;               // { controlPoints, knots }

/** Exact, shape-preserving control point count matching. Chooses insertion parameters at
    span midpoints of the sparsest spans rather than on a blind uniform grid, so it never
    lands on an existing knot. Replaces tweenSurfaces refineCurveControlPointCount and
    tweenCurves matchCPCount. */
export function refineSplineToControlPointCount(spline is map, targetCount is number) returns map;

/** Refine both splines onto mergeKnotVectors(...) so their control points are index-aligned
    and interpolation is meaningful. The correct primitive for a tween. */
export function makeSplinesShareKnotVector(splineA is map, splineB is map) returns map;   // { a, b }

/** Given knots and a superset mergedKnots (e.g. from mergeKnotVectors(knots, other, degree)),
    the flat list of parameters to insert into knots to reach mergedKnots exactly. Emerged
    during implementation as mergeKnotVectors' natural companion — makeSplinesShareKnotVector
    is built from it, and it is independently useful for a caller that already has a target
    knot vector in hand and wants a plain refineKnotVector call to reach it. */
export function insertionsToReach(knots is array, mergedKnots is array, degree is number) returns array;

export function makeSurfacesShareKnotVectors(surfaceA is map, surfaceB is map) returns map;

export function refineSurfaceToControlPointCounts(surface is map,
        targetUCount is number, targetVCount is number) returns map;

/** Guarantee at least minimumControlPointsPerSpan control points across every span of the
    given parameter partition, refining only where the requirement is not already met.
    Targeted refinement driven by an external resolution requirement — the FFD case (§9.2). */
export function refineSplineToSpanDensity(spline is map, spanBoundaries is array,
        minimumControlPointsPerSpan is number) returns map;

export function refineSurfaceToSpanDensity(surface is map, uSpanBoundaries is array,
        vSpanBoundaries is array, minimumControlPointsPerSpan is number) returns map;

/** Raise every interior knot to multiplicity degree, yielding independent Bezier segments.
    Knot-insertion route to the same result as the duplicated de Boor subdivideIntoBeziers.
    The entry point for per-patch algorithms — flattening, developable fitting (§9.3). */
export function decomposeIntoBezierSegments(spline is map) returns array;

export function decomposeSurfaceIntoBezierPatches(surface is map) returns array;

/** Exact sub-surface over a parameter rectangle. Tensor application of clampedSegmentOperator;
    the general form of what displacementMap.fs does to pull one tile. */
export function extractSubSurface(surface is map, uStart is number, uEnd is number,
        vStart is number, vEnd is number) returns map;

// ---------- Layer 3: degree elevation (§3.1) ----------

/** Exact degree elevation. Bezier-decompose via decomposeIntoBezierSegments, elevate each
    segment with the std elevateBezierDegree, recombine, then removeKnots to drop the
    multiplicity the recombination introduced. Same algorithm the three sites already share;
    one copy, with the periodic branch kept. */
export function elevateSplineDegree(spline is map, targetDegree is number) returns map;

export function elevateSurfaceDegrees(surface is map,
        targetUDegree is number, targetVDegree is number) returns map;

/** Elevate to targetDegree, THEN refine to targetControlPointCount — the correct order for
    preparing a surface to be deformed (§3.1). Either step is skipped when already satisfied. */
export function prepareSplineForDeformation(spline is map, targetDegree is number,
        targetControlPointCount is number) returns map;

export function prepareSurfaceForDeformation(surface is map,
        targetUDegree is number, targetVDegree is number,
        targetUCount is number, targetVCount is number) returns map;

/** Full compatibility: common degree AND common knot vector. Composes elevation with
    mergeKnotVectors. The complete form of what §2.4 says both tween features need. */
export function makeSplinesCompatible(splineA is map, splineB is map) returns map;   // { a, b }

export function makeSurfacesCompatible(surfaceA is map, surfaceB is map) returns map;

// ---------- Evaluation (pure de Boor, no Context) ----------

/** Evaluate a B-spline surface DEFINITION at raw knot-domain (u, v). Pure arithmetic,
    rational-aware, no geometry creation. Exists because std has no definition-side surface
    evaluator: evaluateSpline is BSplineCurve-only, and the face evaluators
    (evFaceTangentPlanes / evFaceCurvatures) require created geometry and re-normalize
    parameters to the face bounding box. Needed by the tester (vectors 4, 7) and by §9.1.1
    certification sampling. Documented here per the AGENTS.md manual-math rule: no std
    function does this job. Curves need no analog — std evaluateSpline covers them. */
export function evaluateBSplineSurfacePoint(surface is map, u is number, v is number) returns Vector;
```

Rational input is handled by converting to homogeneous coordinates with the std
`combinePointsAndWeights` / `separatePointsAndWeights` (`onshape/std/nurbsUtils.fs`) at the
Layer 3 boundary. Layers 1–2 stay unaware of weights. **Do not re-implement those two.**

Note on Bezier decomposition: raising every interior knot to multiplicity `degree` *is* knot
refinement, so `decomposeIntoBezierSegments` belongs here and subsumes the triplicated
`subdivideIntoBeziers` / `splitAtFirstKnot` de Boor block — and `elevateSplineDegree` is then
its first caller, which is how the whole elevation block collapses into one copy.

`makeSplinesShareKnotVector` and `makeSplinesCompatible` overlap deliberately: the former is
knot-vector-only for callers that have already matched degree, the latter is the full
operation. Both tween features want the latter.

---

## 5. Conventions the module pins down

These are the things each site guessed at differently, so state them once, in the header:

- **Knot array size is always `numControlPoints + degree + 1`**, clamped or periodic
  (`knotArrayIsCorrectSize`). The overlapping-control-point periodic form from
  `evCurveDefinition` is normalized away on entry.
- **Span rule:** largest `k` with `knots[k] <= u < knots[k+1]` **and** `knots[k] < knots[k+1]`.
  Half-open on the right, degenerate spans skipped. This is `displacementMap.fs`'s rule and it
  is what makes repeated insertion at the same parameter (needed for clamping) terminate
  correctly.
- **Multiplicity ceiling:** insertion is rejected once multiplicity would exceed `degree`.
  Clamping to `degree + 1` is reached via `clampedSegmentOperator`, which is allowed to go one
  higher by construction because it is slicing, not refining in place.
- **Periodic refinement produces a clamped result.** Inserting into a periodic knot array
  destroys the padding relation, so any Layer 3 entry point that refines a periodic spline
  either clamps it first and says so in its return map, or throws. Silently handing a broken
  periodic array to `bSplineCurve` is what 2.3 is about.
- **Sparse weight cutoff:** `1e-12`, matching the current `extractionWeights`.
- **Preallocate.** `makeArray` plus index assignment in every loop that grows with control
  point count. No `append` in the hot paths.
- **Std wrappers over direct `@` builtins.** Direct `@` calls do compile in custom features
  (repo precedent: `jelteQVPlus.fs` calls `@getQueryVariable` / `@setQueryVariable`,
  `kerf-bending/kerfBendingAnalytical.fs` calls `@size`), but builtins are undocumented and
  version-sensitive. Call the documented wrapper when one exists; any direct `@` call gets a
  comment saying why.

---

## 6. Validation

No local FeatureScript runtime exists, so validation is a **tester feature** plus fixed
vectors, per the repo's testing note in `AGENTS.md`.

`custom-features/splineRefinementTester.fs` — a no-geometry feature that runs the vectors and
reports pass/fail with `reportFeatureInfo` / `println`. Modeled on the existing
`smSplitEdgeTester.fs` / `sheetMetalQueriesTester.fs` pattern.

Vectors, in order of authority:

1. **SPEC §5.3.** Degree 2, 6 control points, `U = [0..8]`, extract `[3,5]`. Insert `3` twice
   and `5` twice → knots `[0,1,2,3,3,3,4,5,5,5,6,7,8]`, `ia = 3`, `ibLast = 9`, `cpCount = 4`,
   piece knots `[3,3,3,4,5,5,5]`. This already governs the shipping displacement map path.
2. **The §2.1 counterexample.** Degree 2, `U = [0,0,0,1,2,3,3,3]`, insert `0.5`, assert
   `Q3 == P2` exactly. This is the regression test for the tween surfaces bug and it must fail
   against today's `insertKnotBoehm`.
3. **Convex-combination invariant.** For any legal insertion every output row's weights are
   non-negative and sum to 1. Cheap, catches sign errors like 2.1 generically.
4. **Geometry preservation.** Evaluate the spline before and after refinement at ~20 sample
   parameters with `evaluateSpline` (std `splineUtils.fs`) and assert agreement to 1e-9.
5. **Operator vs. direct equivalence.** `applyKnotRefinementOperator(knotRefinementOperator(...))`
   equals `refineKnotVector(...)` to 1e-12 on random control points. This is the guard that
   keeps a future algorithm swap honest.
6. **`displacementMap.fs` parity.** `uniformPeriodExtractionOperator(degree, n)` must equal the
   current `extractionWeights(degree, n)` term for term, for `degree` 1–5 and `n` 4–16, before
   that feature is migrated.
7. **Elevation preserves geometry.** Same sample-and-compare as vector 4, run across
   `elevateSplineDegree` for every degree pair from 1→2 up to 3→7, on inputs with repeated
   interior knots and on periodic inputs. Elevation is where the existing implementations are
   correct but fragile, so this is the vector that protects the de-duplication.
   **Implemented** (four degree pairs including a repeated-interior-knot case; periodic input
   not yet covered — see the periodic-quirk gap noted above).
8. **Order independence where it should hold.** `elevate(refine(s))` and `refine(elevate(s))`
   must describe the same geometry (they will differ in control point count — that is §3.1's
   point, and the test asserts the geometry, not the net). **Not yet added.**

Also implemented and passing curve-level checks not in the original numbered list: force-rational
normalization, genuine periodic clamping (geometry-preservation form), `mergeKnotVectors`
identity/union/refine-to-merge properties, exact-count refinement, knot-vector sharing across
two differently-structured curves, Bezier decomposition against the parent curve, and the
`makeSplinesCompatible` / `prepareSplineForDeformation` compositions. See
`splineRefinementTester.fs`'s own header comment for the full current list — it is more
current than this section.

---

## 7. Migration, phased

**Originally decided: Tween Surfaces ships first.** It is the smaller lift, it has its own
audience, and — per §3.2 — it is the *affine* case, so it exercises the entire module
(elevation, refinement, knot merge, periodic handling, rational surfaces) with none of the
approximation questions the deformation work brings. If the module is wrong, tween shows it
immediately and cheaply. If tween is right, the only remaining unknown for §9.1 is the
deformation map itself.

**Superseded by how the work actually landed (2026-08-07):** the module was built curves-first
(Layers 1–2, then every curve-level entry point including elevation), and that batch is now
78/78 verified in Onshape. The "exercise the module before trusting it" rationale above is
already satisfied — by curves, not surfaces. Surfaces still need their own eight hooks written.
**Net effect: Phase 3 (tweenCurves) is ready to start today with zero remaining module risk;
Phase 2 (tweenSurfaces) is not, until the surface hooks exist.** Whether to now do Phase 3
before Phase 2 — inverting the original order — is an open question, see §11.

Ordered so that risk arrives late and the payoff arrives early; read the phases below as
depending on what's *actually* done (per §6) rather than assuming the numbering is chronology.

**Phase 1 — build and validate the module.** Write `splineRefinementUtils.fs` and
`splineRefinementTester.fs`, refinement side only (Layers 1–2 plus the non-elevation Layer 3).
Nothing else changes; nothing needs republishing. Ends when vectors 1–6 pass in Onshape.
**Done — verified in Onshape.**

**Phase 1b — elevation.** Add `decomposeIntoBezierSegments`, `elevateSplineDegree`,
`elevateSurfaceDegrees`, `makeSplinesCompatible`, and the `prepare*ForDeformation` pair. The
algorithm is lifted from the existing correct copies (std `editCurve.fs` is the cleanest, since
`tweenCurves.fs` is verbatim from it and keeps the periodic branch that `tweenSurfaces.fs`
dropped). Ends when vectors 7–8 pass. Kept separate from Phase 1 only so the refinement half
can be published and used while this lands. **Curve half done and verified
(`decomposeIntoBezierSegments`, `elevateSplineDegree`, `makeSplinesCompatible`,
`prepareSplineForDeformation`). Surface half (`elevateSurfaceDegrees`,
`prepareSurfaceForDeformation`) still stubbed.**

**Phase 2 — `tweenSurfaces.fs`, mechanical.** Delete `insertKnotBoehm`,
`refineCurveControlPointCount`, `refineControlPointCount`, `elevateSurfaceDegree`,
`subdivideIntoBeziers`, `splitAtFirstKnot`, `elevateBSplineCurve`, `makeUniformKnotVector`;
call `refineSurfaceToControlPointCounts` and `elevateSurfaceDegrees`. Keeps existing behaviour
and UI, fixes 2.1/2.2/2.3. This is the largest single-file deletion (~500 lines with elevation
folded in) and where the visible warping should stop. **Blocked on the surface hooks.**

**Phase 3 — `tweenCurves.fs`. Done (2026-08-07), NOT yet tested live.** Originally scoped as
"replace `matchCPCount`, delete the `editCurve.fs` copy block" — that turned out to be only
correct for *open* (or mixed-periodicity) curve pairs. What actually shipped, and why the
"delete ~250 lines" plan didn't survive contact with periodicity:

`makeSplinesCompatible` — which both replaces `matchCPCount`'s approximation with exact
refinement (Phase 3) AND fixes the knot-sharing bug (Phase 4) in one call — always clamps
periodic input, because genuine periodic-preserving refinement isn't something this module
implements (see the periodic policy in §5). Calling it on two genuinely periodic (closed)
curves would silently convert the tweened result from closed to open. So `tweenCurves()` now
branches on `bothPeriodic = curve1.isPeriodic && curve2.isPeriodic`:

- **Not both periodic** (the common case — open splines, arcs, lines, any mix of them): calls
  `makeSplinesCompatible`, which remaps both domains to `[0, 1]` before merging knots. This is
  what makes an arc-derived spline's native parameterization and a line's default `[0, 1]`
  domain compatible, not just same-degree-same-count. **Gets the full Phase 3 + Phase 4 fix.**
- **Both periodic**: keeps the *original* `elevateDegree` + `matchCPCount` pipeline completely
  unchanged, so closed curves stay closed. `elevateDegree`, `subdivideIntoBeziers`,
  `splitAtFirstKnot`, `elevateBSpline`, `matchCPCount`, `isBezier`,
  `computeControlPointFractions` are therefore **still present in the file, not deleted** — the
  original Phase 3 plan to delete them was wrong once periodicity was accounted for.
  **This path is untouched and gets none of the fixes.** A deliberately scoped gap, tracked
  here rather than silently left.

`isPeriodicTween` and the periodicity-mismatch warning now read from `originalIsPeriodic1/2`,
captured before any compatibility processing — not from the post-processing spline maps, which
are always non-periodic on the fixed path. Getting this wrong would have made even the warning
logic silently misreport periodicity for the exact curves it exists to warn about.

**Not yet run in Onshape.** Needs a live check against: an open arc-derived spline tweened with
a line; an open arc-derived spline tweened with a genuine multi-segment spline of a different
native parameter domain; and a closed/periodic pair (to confirm the untouched path still
behaves exactly as before).

**Phase 4 — the tween correctness fix (§2.4).** For `tweenCurves.fs`: **done for the
non-periodic path**, as part of Phase 3 above (`makeSplinesCompatible` does both at once — see
why in §7's Phase 3 entry). Periodic curves do not get it (same gap). For `tweenSurfaces.fs`:
still open — switch to `makeSurfacesCompatible` and drop the knot-interpolation block (lines
~455–530). **This is the phase that makes Tween Surfaces mean something at fractions other
than 0 and 1.** It changes results, so it wants its own before/after check on a real pair of
faces.

**Phase 5 — `displacementMap.fs`, last.** Replace `extractionWeights` / `applyExtraction` with
`uniformPeriodExtractionOperator` / `applyKnotRefinementOperator`, gated on vector 6 passing.
`refineTileSeed` becomes two operator builds plus the two tensor helpers. Pure de-duplication —
this feature already works and is perf-tuned, so the acceptance bar is "seed control points
identical and regen time not worse". The cached-seed format (`stripSeedForCache` /
`rehydrateTiledSeed`) must not change, or every saved instance re-solves.

Phases 2/3 and 5 are independent; 4 depends on 2, 3 and 1b. **Phase 4 is the ship point for
Tween Surfaces** — that is the end of the first deliverable.

The §9.1 deformation feature starts after that. It technically only depends on 1b and could be
started in parallel, but running it behind tween buys a module that has been exercised on real
surfaces first, which is worth more than the overlap saves.

---

## 8. Publishing and version pinning

An importable utility in Onshape means a published Feature Studio tab referenced by
document/element/version id, so the churn is real and worth planning — see
[[std-library-browser-updater]]. Precedent: `custom-features/spacing_utilities/spacingUtils.fs`,
imported by both best-fit pattern features as
`export import(path : "8ce820287d75ed2e92412d90", version : "...");//spacingUtils.fs`.

Order:

1. Publish `splineRefinementUtils.fs` as a new Feature Studio → record its document and version id.
2. Add the pinned import to each consumer as it migrates, with the `//splineRefinementUtils.fs`
   trailing comment the repo uses to make ids legible.
3. **Every republish of the module bumps its version id in all three consumers.** Batch module
   changes; do not republish per tweak.

Two notes:

- **FeatureScript version.** Author the module at **3029**, matching the newest custom features
  (`grainDirection.fs`, `tippyBucketPivot.fs`) and `AGENTS.md`. Each tab declares its own
  version and imports pin a *document* version, so consumers at 2679 (`tweenCurves.fs`) and
  2837 (`tweenSurfaces.fs`, `displacementMap.fs`) can import it without being bumped. If that
  turns out not to hold, the fallback is to author the module at 2679 — the module uses no
  post-2679 std API. *(`AGENTS.md` line 38 still says 3029 while the mirror was synced to 3044
  in `328eb99`; worth reconciling separately.)*
- **`displacementMap.fs` is Evan Reese's feature**, distributed by The Onsherpa and marked not
  for redistribution. The knot code being extracted from it is ours (the tiling refactor), but
  Phase 5 adds an outbound import to a document under our control. Confirm that is acceptable
  for a redistributed feature before doing it — or leave Phase 5 undone, which costs only the
  duplication.

---

## 9. What this unlocks downstream

De-duplication is the immediate reason to build the module; these are the reasons to build it
*properly*, and they are what justify the operator layer and the targeted-refinement entry
points over a bare `insertKnot` helper.

### 9.1 The deformation feature family

Decided: a **new feature**, not a fork of `deformPascoe.fs`. Its UI and section machinery are
~6000 lines of one particular interpretation of how flex should feel, and there is no reason to
inherit that when the deformation core is the part worth keeping.

**One pipeline, many maps.** Every deformation mode is the same five steps, differing only in
step 3:

1. `evApproximateBSplineSurface` the face (or `evSurfaceDefinition` when it is already a spline).
2. `prepareSurfaceForDeformation` — **elevate** to the continuity the map needs (degree 3
   minimum for a tangent-continuous bend), **then refine** until the control-net deviation is
   inside tolerance per §3.2. Both exact; the undeformed surface is unchanged.
3. Push every control point through **the deformation map**. The only step that moves anything.
4. `opCreateBSplineSurface` the untrimmed result.
5. `opReplaceFace` onto the original face, which carries the trim loops, holes and inner loops
   over for free.

Against `opFlex.fs` that removes the split, the per-edge sampling, the per-edge `opFitSpline`,
and the `opLoft` / `opFillSurface` rebuild — four approximation stages down to one that
converges and can be measured. Against `deformPascoe.fs` it adds step 2 and swaps
`boundaryBSplineCurves` trimming, which cannot express holes, for `opReplaceFace`, which
already handles them in `displacementMap.fs`.

**The map is the plug point.** A deformation map is a point function `Vector -> Vector` plus a
hint about how non-linear it is, so step 2 can pick a refinement level. That one interface
covers every mode worth wanting:

| Mode | Map |
| --- | --- |
| Flow along surface / wrap | Point → `(u, v, w)` relative to a source surface → re-placed at the target surface's `(u, v)`, offset `w` along the target normal |
| Bend along curve | Point → arc-length position plus offset in the curve's moving frame |
| Twist about axis | Rotation whose angle is a function of axial position |
| Taper / scale | Position-dependent scale about an axis |
| FFD lattice (§9.2) | Trivariate Bernstein evaluation at the point's `(s, t, u)` |

Modes compose: a bend and a twist applied together are the composition of two maps, evaluated
once per control point. Worth crediting — **`opFlex.fs` already had this architecture.** Its
taper / twist / deform checkboxes compose into a single `convertFunction` applied by
`convertPoint`. The composition idea was right. It was applied to tessellated sample points
instead of a control net, and that one choice is what made everything downstream approximate.

**UX target: generalize std Wrap's vocabulary rather than invent one.** `wrap.fs` already reads
as Flow Along Surface with the target restricted to cylinder and cone: `Tools` (source faces) →
`Target` (destination face), optional source/target **anchor point** pairing, `U shift` /
`V shift` / `Angle` for placement on the target, `Thickness` for solid output, and a
Solid / Surface / Split result type. Rhino's Flow Along Surface and Plasticity's deform expose
essentially the same controls. Lifting the cylinder/cone restriction on `Target` — exactly what
steps 1–5 make possible — gives people a feature they can pick up without being taught.

Flow along surface has the loudest demand so it is the mode to build first, but the map
interface should exist from day one so bend and twist are additions rather than rewrites.

This is a new feature, not a migration, and it starts after Tween Surfaces ships (§7).

#### 9.1.1 The refinement criterion for step 2

Degree and refinement answer **different questions** and must not share a knob.

**Degree is chosen by intent, never by tolerance.** A position tolerance cannot detect that
degree 1 is wrong: refining a degree-1 surface converges in *position* while staying C⁰
forever, so a tolerance-only loop will happily ship a finely faceted result that satisfies
every numeric check and looks like garbage. With simple knots a degree-`p` spline is
`C^(p-1)` — the same rule [DISPLACEMENT_MAP_TILING_SPEC.md](DISPLACEMENT_MAP_TILING_SPEC.md)
already relies on — so:

| Intent | Minimum degree |
| --- | --- |
| Position only (faceting acceptable) | inherit the source |
| Tangent continuous | 2 |
| Curvature continuous — the default | 3 |

Elevate to that, once, before refining. It is cheap and it is the difference between a smooth
result and a finely tessellated one.

**Refinement is chosen by measured tolerance.** Two independent measurements are available and
the criterion should use both — a free one to drive iteration and a kernel one to certify the
result.

What is actually available, stated precisely because it is easy to get wrong — the full
builtin call surface of the std mirror was enumerated to check this:

- **Nothing evaluates a B-spline *definition*.** `evaluateSpline` (and the `@evaluateSpline`
  builtin behind it) is curve-only, and no surface analog exists anywhere in std. Direct `@`
  calls are legal from custom features (§5), but there is no builtin to call. So the module
  carries its own `evaluateBSplineSurfacePoint` (§4) — ~30 lines of de Boor it needs for its
  tester anyway.
- **Faces can be evaluated** — `evFaceTangentPlanes` / `evFaceCurvatures` at parameter-space
  coordinates — but both require created geometry, compute normals or curvature frames when
  only points are needed, and take parameters **normalized to the face's parameter-space
  bounding box**, not raw knot values. Feeding them knot-domain parameters from a definition is
  a silent mismatch. Wrong tool for certification sampling.
- **`evPointsDeviation(context, {points, topologies, allDeviations, showDeviation})`** returns
  the distance from each point to the closest element of `topologies` — kernel-side, batched,
  max or per-point. This is the one kernel measurement worth paying for: closest-point
  projection onto a spline surface is genuinely hard to hand-roll, and `showDeviation`
  visualizes the result for free, which makes it a debugging affordance as well as a criterion.

The free measurement rests on:

> Two splines sharing degree, knot vector **and weights** satisfy
> `‖S_a(t) - S_b(t)‖ <= max_i ‖P_a,i - P_b,i‖` for all `t`.

Both the polynomial basis and the rational basis `R_i = w_i N_i / Σ w_j N_j` are non-negative
and sum to 1, so the difference of two such splines is a convex combination of the control
point differences. Comparing two surfaces therefore costs **`O(n)` arithmetic on control
points** — no evaluation, no kernel calls, no tessellation.

**The loop — drive free, certify with the kernel:**

1. Deform at the current refinement level → `S̃_k`.
2. Refine the *undeformed* surface one level finer, deform → `S̃_{k+1}`.
3. Lift `S̃_k` onto `S̃_{k+1}`'s knot vector with `knotRefinementOperator` — exact, and it makes
   the weights match too, since both are the original weights refined by composed operators.
4. `delta = max_i ‖P_k,i - P_{k+1,i}‖`. If `delta > tolerance`, `k += 1` and loop. This step is
   pure arithmetic — no kernel call, no extra map calls, effectively free.
5. **Certify.** Once `delta` passes, create `S̃_{k+1}` (needed anyway to ship it). Evaluate the
   *refined undeformed* definition at the midpoint of every knot span in u and v with the
   module's `evaluateBSplineSurfacePoint` — pure arithmetic, and by construction in the same
   parameterization as the definition, so there is no face-bbox normalization to mismatch.
   Push those points through the map to get points on the true deformed surface, and call
   `evPointsDeviation` against the created body. That is the actual geometric error, measured
   by the kernel, not an estimate.
6. If certification fails, the map has a feature finer than the sampling caught (§ below) —
   refine again and re-certify. If it passes, ship, and report the certified deviation.

No work is wasted in steps 1–4: every deform either ships or becomes the baseline for the next
comparison. The map calls form a geometric series, ~4/3 the cost of an oracle that knew the
right level in advance — worth stating because for flow-along-surface the map call is the
expensive part (a projection onto the source surface per control point).

Why both measurements rather than either alone:

- `delta` alone is a *proxy*. With `O(h²)` convergence `delta ≈ (3/4)·E_k`, so using it
  untouched over-refines ~3×, and it can be fooled (below).
- `evPointsDeviation` alone would need enough sampling per level to be trustworthy, and its
  samples are extra map calls that never become geometry.
- Driving on `delta` and certifying once with `evPointsDeviation` costs one kernel call and one
  sample batch for a **certified, reportable** number. `evPointsDeviation` also measures
  closest-point distance rather than parameter-matched distance, which is the deviation users
  actually care about — parameter-matched distance overcounts when the parameterization shifts
  tangentially without moving the surface.

Reporting the certified deviation in `reportFeatureInfo` is the whole difference from
`opFlex.fs`'s `edgeSamplingStep`, which cannot answer "how wrong is this?" at all.

**Seeding.** Doubling up from the raw approximation wastes the early iterations, so the map's
non-linearity hint should seed the first level. For flow along surface the natural hint is the
target's minimum curvature radius `R`: a span of arc length `s` deviates by about `s²/(8R)`,
so starting at `s ≈ sqrt(8·R·tolerance)` lands close to the answer immediately.

**Adaptivity, and its honest ceiling.** The `delta` per control point localizes the error, so
refinement can target the direction that needs it — a bend along U should not refine V. But a
tensor-product B-spline can only insert a knot across an entire isoparametric line, so
adaptivity is **per-U-span and per-V-span, never per-patch**. Genuine local refinement needs
T-splines and is out of scope. Directional adaptivity is the big win and should be in v1;
per-span-within-a-direction is a follow-on.

**The blind spot, and why certification closes it.** The map is only ever *applied* at control
points, so a deformation feature narrower than the current span spacing is invisible to
`delta` — two successive levels can agree closely while both are wrong. Seeding mitigates it;
the certification step in 5–6 **detects** it, because its samples sit at span midpoints —
exactly where the control-net comparison is blind. That turns a silent wrong answer into another
refinement pass. This is the main reason the kernel measurement earns its one call.

**Budget and honest reporting.** Cap the iterations and the total control point count. If a cap
binds before the tolerance is met, `reportFeatureWarning` with the deviation actually achieved
— which, thanks to step 5, is a real measured number rather than a guess. Silently stopping at
a cap and presenting the result as converged is the failure mode this whole document exists to
avoid.

**Default tolerance** should be scale-relative so it behaves on a 5 mm bracket and a 5 m hull
alike: bounding box diagonal × 1e-4, floored at `TOLERANCE.zeroLength`, with a length override
in the dialog.

### 9.2 Free-form deformation

`freeFormDeformation.fs` (and `freeFormDeformationPlanes.fs`) push each surface control point
through a trivariate Bernstein lattice and rebuild the surface with the **original knot vectors
and the original control point count** (`freeFormDeformation.fs:344-353`). That is the classic
FFD ceiling: **the deformation can only be as detailed as the surface's existing control net.**
Feed it a 4×4 Bezier patch and a 6×6×6 lattice and the extra lattice spans do nothing
representable — the surface has 16 degrees of freedom to express a field sampled far more
finely. Symptomatically this reads as "cranking up lattice resolution stops helping" or as a
deformation that looks subtly wrong near lattice cell boundaries.

Knot refinement is the standard fix, and it is exact: refine the control net **before**
deforming, and the undeformed surface is bit-for-bit unchanged while gaining the degrees of
freedom to represent the field. This is what `refineSurfaceToSpanDensity` exists for — the
caller passes the lattice cell boundaries in surface parameter space and a minimum control
point count per cell, and only under-resolved regions are refined. A blanket "double every
control point" would work too and cost far more.

Per §3.2 there is a second reason on top of the degrees-of-freedom one: the trivariate
Bernstein map is non-affine, so refinement is also what makes the deformation *converge*.
Today's FFD has no refinement step at all, so it has neither.

Structurally, FFD is §9.1's pipeline with a lattice map — it should share steps 1, 2, 4 and 5
rather than growing its own copy.

This also makes FFD's periodic handling honest: today it re-wraps with
`isUPeriodic : surfaceDefinition.isUPeriodic` and the untouched knot array, which is fine
because it never changes the knot vector. Once refinement enters, the §5 periodic policy
applies.

### 9.3 Surface flattening without the analysis tool

Flattening, unrolling and developable approximation all operate **per patch** and stitch. The
enabling primitive is exact subdivision: `decomposeSurfaceIntoBezierPatches` and
`extractSubSurface` cut a NURBS surface into pieces that reproduce the parent exactly, with no
approximation step and no round trip through a fitter. `displacementMap.fs` already proves the
tensor extraction works at scale — flattening wants the same operator with arbitrary parameter
bounds instead of one integer period.

Being explicit about the boundary: refinement supplies exact patch decomposition and controlled
parameterization. It does not supply the flattening itself — the distortion metric, the
strain-minimizing solve, the stitching. Those are a separate feature. What the utility removes
is the need to approximate the surface before you can begin.

### 9.4 Tweep / 3D Spiral

Per §1.1: replace `opFitSpline`-on-resampled-points with `opCreateBSplineCurve` from a control
net, so periodic loops close by construction and control point density is chosen rather than
negotiated with a fitter. Worth doing after the tween features prove the module out, since it
is a rewrite of the generator rather than a substitution.

---

## 10. Deliberately not in scope

- **Knot removal and degree reduction.** The lossy inverses. Std `nurbsUtils.fs` exports
  `removeKnots`, which `elevateSplineDegree` calls internally to drop the multiplicity that
  Bezier recombination introduces — that is a cleanup step inside an exact operation, not a
  standalone simplification service. A caller wanting to *simplify* a spline within a tolerance
  is asking for a different module.
- **Approximation and fitting.** `approximateSpline` stays where it is; the point of this
  module is to stop reaching for a fit when refinement is exact.
- **A second insertion algorithm.** `KnotInsertionAlgorithm` exists so Oslo can be added
  without touching callers. Adding it now would be speculation — Boehm is not currently the
  bottleneck anywhere, and the operator cache already removed the one place it was.

---

## 11. Decisions, and what remains

Nailed down (as of 2026-08-07):

1. **Ordering.** Tween Surfaces ships first (§7); the deformation feature follows. Modes ship
   progressively — flow along surface first — behind a map interface present from day one
   (§9.1).
2. **Phase 4 proceeds.** The knot-merge fix is the point of shipping tween. Its acceptance
   gate is a before/after check on a real pair of faces, because results change at
   intermediate fractions — that check is part of Phase 4, not a pre-condition for starting it.
3. **Elevation is in scope** (§3.1), and the §9 helpers — `refineSurfaceToSpanDensity`,
   `decomposeIntoBezierSegments`, `extractSubSurface`, `evaluateBSplineSurfacePoint` — build in
   Phase 1/1b rather than trickling in later. They are compositions over the operator layer,
   and front-loading them avoids a version-bump cascade across consumers when the deformation
   work starts.
4. **Periodic policy: clamp-and-report.** Refining a periodic spline clamps it first and says
   so in the returned map. Friendlier than throwing and matches what
   `evApproximateBSplineSurface` consumers actually hold; the flag keeps it honest.
5. **Loop ownership.** The module owns the pure parts of §9.1.1 — steps 1–4 and the evaluator.
   The feature owns certification (steps 5–6), which needs `Context` and created geometry.
   Promote the loop into the module when FFD wants it too.
6. **Certification sampling default:** the midpoint of every knot span per direction via
   `evaluateBSplineSurfacePoint`, capped. Density is now a build-time tuning parameter, not a
   soundness question — the blind spot is covered by *where* the samples sit, not how many
   there are.
7. **Phase 5 (displacementMap) happens**, last, gated on the term-for-term parity vector 6 —
   subject to the sign-off below.
8. **`@` builtin policy** (§5): call the documented wrapper when one exists; direct `@` calls
   are legal but get a justifying comment.

Both former sign-offs are resolved (2026-08-07):

1. **The Reese import is fine.** Evan is aware of and enthusiastic about the tiling work, will
   be credited when the revisions are posted, and may back-port changes into the main copy;
   the local `displacementMap.fs` is already heavily modified. Phase 5 stays sequenced last —
   after the core is proven — but no longer waits on permission.
2. **Authoring version is 3044.** `AGENTS.md` has been updated to match. Why the auto-updater
   left it at 3029 is an open chore tracked outside this spec.

**New open question (2026-08-07), not yet decided — this is the one the user needs to call:**

9. **Does Phase 3 (tweenCurves) jump ahead of Phase 2 (tweenSurfaces)?** Decision 1 above said
   tween SURFACES ships first; that was reasoned from "surfaces are the better proving ground
   for the module." The module has since been proven — by curves, 78/78 checks, live in
   Onshape — so that reasoning no longer favors either order. tweenCurves needs zero further
   module work; tweenSurfaces needs all eight surface hooks first. Two honest options: (a) do
   Phase 3 now for a fast, complete, real win, then write the surface hooks and do Phase 2; or
   (b) write the surface hooks first, preserving the original order, and ship both tween
   features close together. Recommendation: (a) — there is no remaining reason to wait, and
   shipping something real is worth more than symmetry with a plan written before either path
   was tried.
