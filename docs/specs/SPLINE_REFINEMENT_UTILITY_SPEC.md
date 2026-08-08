# Spline Refinement Utility — Refactor Plan

Status (2026-08-08, end of the periodic-conventions arc): **Both tween features are fully
migrated. Tween Surfaces is verified live through its hardest cases — self-pair, different
radii, cones-to-cones, and cone-to-cylinder (opposite traversal + phase offset), all building
correctly** through exact compatibility, exact periodic-preserving
refinement/elevation/knot-sharing, exact reversal, and exact two-sided seam alignment. 230+
tester checks passing. Three late discoveries closed this arc, each with its own subsection in
§2.3: the kernel's CLOSED-CLAMPED periodic convention (§2.3.1), the canonical wrap-form seam
invariant that reversal breaks (§2.3.2), and std `evaluateSpline` silently ignoring weights
(§2.3.3).

Remaining, in order: re-test the ROTATED periodic pairs live (the rewindow first-of-run fix in
§2.3.2 is the prime suspect for their intermittent failures — unverified); bump Tween Curves'
pinned module version and validate its rational-alignment path live (it shares every fix but
has not run since); Phase 5 (displacementMap migration); the deformation feature (§9.1), not
started.

`custom-features/splineRefinementUtils.fs`, FeatureScript 3044:
- **Verified passing in Onshape (200+ checks across many live runs):** Layers 1–2, direct
  insertion, the pure surface evaluator, every curve-level Layer 3 entry point, every
  surface-level Layer 3 entry point, and — the majority of the module's total size —
  **genuine periodic-preserving refinement, elevation, knot-sharing, reversal, and
  re-windowing, for both curves and surfaces, including the two-sided seam alignment surfaces
  need that curves do not.**
- **The periodic policy changed completely partway through, and it is the single biggest
  design shift in this module's history.** §11 decision 4 below ("clamp-and-report") is
  **superseded** — it is preserved in place, struck through, because the reasoning that
  replaced it is worth keeping visible. See the new §2.3 for what actually shipped and why
  clamping was rejected outright partway through implementation.
- **`findKnotSpanIndex` bug (2026-08-07, first live run of the curve batch):** fixed — see the
  unchanged paragraph below this block for the mechanism. Not touched since.
- **A second, more serious class of bug (2026-08-07, second half of the session):** an operator
  built to be applied once — the whole point of the operator layer is amortization across many
  point arrays — was being built for single-curve calls anyway, at O(M²) cost before a single
  point was touched. On a 300-control-point direction this was measured as an 8-second `throw`
  budget slowdown before profiling caught it. Fixed by using direct sequential insertion
  (`refineKnotVector`) everywhere a refinement is applied to exactly one array, reserving
  `knotRefinementOperator`/`periodicRefinementOperator` for the tensor (surface-grid) callers
  that actually amortize. See [[knot-refinement-utility]] memory for the full postmortem —
  worth reading before adding any new Layer 3 entry point, since the mistake is easy to repeat.
- **A third class, orthogonal to both: `squaredNorm` vs `norm` in every distance-COMPARISON
  site** (alignment search, best-shift selection). Measured 25% build-time reduction from this
  alone in one hot path. See [[featurescript-squarednorm-vs-norm]] — the short version is
  "compare distances, never take the sqrt," and the cross-correlation identity
  (`argmax(A·B) == argmin(|A-B|²)` under a fixed permutation) removes even the squaring in the
  inner loop of every alignment search in this module and in both tween features.
- **The hardest bug, and the reason §2.3 exists:** a revolve's circular direction is stored by
  the kernel as **Bezier arcs** — fundamental knots like `[0, 0.5, 0.5, 0.5]`, multiplicity 3 at
  the arc joint, equal to the degree. Every nonzero one-sided seam shift on a surface with this
  structure lands ON that joint, which the kernel rejects as a seam
  (`PERIODIC_BSPLINESURFACE_NOT_SMOOTH`) even though the same multiplicity is perfectly legal as
  an *interior* knot. No one-sided fix exists. The fix moves BOTH surfaces' seams by a split
  offset, each landing on a knot the fixed side can afford — see §2.3 and
  `alignPeriodicSurfaceSeams` in §4.

**One gap from the original curve-only pass is now closed, not just resolved:**
`normalizeSplineDefinition`'s old kernel-quirk port (verbatim from std `editCurve.fs`
`cleanUpPeriodicBSplineDefinition`) was found to actively corrupt canonical input — its
`knots[0] != 0` heuristic fires on every legitimately-padded periodic curve, since padding
always makes `knots[0] < 0`. It was first replaced with form recognition by COUNTING, and that
too proved insufficient — the stored-form and closed-clamped counts collide exactly, and a
fixture built on the count-based reading enshrined a convention that does not exist (§2.3.1).
What ships now is three-way discrimination by DATA (the wrap-padding relation on knot values;
clamped ends plus a coincident endpoint), with an explicit throw — naming the observed shape —
for anything else. No more silent reinterpretation of a shape nobody has inspected against a
live kernel, and no more recognition by any property two different shapes can share.

`custom-features/splineRefinementTester.fs` runs vectors 1–6 (structural), evaluator sanity,
the curve-level Layer 3 suite, the surface-level Layer 3 suite (including genuine periodic
preservation, not clamping), reversal, re-windowing, the periodic-vs-direct-insertion agreement
vectors, and — the vector that mattered most — **`BEZIER-SEAM`**: a fixture built from a real
Onshape revolve's actual control-point/knot numbers (fundamental knots `[0, 0.5, 0.5, 0.5]`,
rational, non-uniform weights), asserting that a one-sided seam shift ALWAYS lands on the C0
arc joint (a passing check that documents the bug's inevitability, not just its existence), and
that the two-sided fix produces matching multiplicity-1 seams on both sides with geometry
unmoved. 200+ checks, 0 failures, confirmed live. Every periodic vector rule: **a degree-1
fixture is never sufficient coverage on its own** — degree-1 control points lie ON the curve,
so a broken clamp-based extraction is value-neutral there, and a real bug survived two green
runs before a degree ≥ 2 vector caught it.

**Sequencing note, resolved:** §7 originally documented Phase 2 (tweenSurfaces) before Phase 3
(tweenCurves). §11's open question 9 asked whether to invert that once the module was proven by
curves alone; the answer was yes — tweenCurves shipped first, and by the time tweenSurfaces'
periodic story was built out, the curve-level periodic primitives were the direct template for
their surface-level counterparts (see §2.3). The order that actually happened: Phase 1 → 1b →
**Phase 3 (tweenCurves)** → periodic curve primitives → periodic SURFACE primitives (not in the
original plan at all — see §2.3) → **Phase 2 (tweenSurfaces)** → Phase 4 (both, done as part of
3 and 2 respectively, since `makeSplinesCompatible`/`makeSurfacesCompatible` fix 2.4 as a side
effect of being called at all).

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
mathematical reference for the insertion core, and §5.3 is its primary test vector);
[T_SPLINE_SUPPORT_SPEC.md](T_SPLINE_SUPPORT_SPEC.md) (the downstream pipeline that reuses
Layers 1–2, the periodic machinery, and the evaluator as its extraction engine — see §9.5).

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

### 2.3 Periodic inputs — genuinely handled, not clamped (superseding the original plan)

The original plan (§11 decision 4, struck through below) was **clamp a periodic input, say so
in the return map.** That plan shipped, was used for a while, and was then rejected outright —
not refined, rejected — after the person driving this project pushed back on exactly that
tradeoff: *"I want working code. Not fallbacks to known nonworking code... We're making things
better, not leaving silent levers to the broken past."* This section is the design that
replaced it, and the reasoning for why clamping was never actually acceptable.

**Why clamping is wrong, not just less convenient.** The stored periodic form is `n`
fundamental control points plus `degree` literal COPIES of the first `degree` (the OVERLAP
CONDITION, `P[i] == P[i+n]` — a constraint on control point VALUES, not just knot count; it is
what tells the evaluator "wrap here"), paired with knots satisfying
`knots[i+n] = knots[i] + PERIOD`. Clamped extraction computes NEW control points near a
boundary via ordinary Boehm insertion, and nothing about that computation has any reason to
satisfy the overlap condition. Re-flagging the clamped result as periodic afterward produces a
curve or surface with a seam: position may hold, tangent and curvature will not. For a tween,
that seam is exactly the visible defect — a "closed" surface with a crease where its two
sampled halves disagree.

**The construction that replaced it.** A periodic B-spline is a finite window onto an infinite
periodically-extended structure (knots and control points both repeat every PERIOD). Boehm
insertion and degree elevation are LOCAL — they touch only `degree` neighboring control points
around wherever they operate. So: tile the infinite structure into a finite window wide enough
that every relevant operation's local support stays clear of the window's own clamped edges,
run the ordinary clamped-only machinery on that window, then slice the core period back out.
The result is automatically overlap-consistent, because the infinite structure was never
actually broken — only sliced from a window wide enough that slicing introduces no error.

Two window shapes exist, deliberately not merged into one, because refinement and elevation
have genuinely different requirements:

- **Refinement** (`buildPeriodicWindow`): UNCLAMPED, margin measured in CONTROL POINTS
  (`2*degree + 2`). Boehm insertion needs nothing but locality, so the margin only has to cover
  the reach of one blend plus the seam-straddling points the final slice touches. This is also
  why refinement can use direct sequential insertion (`refineKnotVector`) instead of building an
  operator — see the perf note in the status block above.
- **Elevation** (`buildPeriodicWideClampedWindow`): CLAMPED, margin of whole PERIODS
  (`ceil((degree+1)/n)`, not hardcoded to 1 — this is what lets a "tight" periodic spline with
  fewer control points per period than its degree go through the same exact path instead of
  being rejected as degenerate). Bezier decomposition (which elevation goes through) rejects an
  unclamped knot array outright, and its trailing `removeKnots` pass reasons globally across the
  window, so every period must be an identical tile for its decisions to match across the wrap.

**The extraction step is a raw index SLICE, never a `clampedSegmentOperator` call** — this was
the one bug in the initial implementation that survived a live test run, because a
`clampedSegmentOperator`-based extraction happens to be value-neutral at degree 1 (degree-1
control points lie ON the curve) while being silently wrong at every degree ≥ 2. The
justification for the slice: the wide window represents the same function as the periodic
curve, with identically tiled interior knots, so by local linear independence of the B-spline
basis, any control point whose support lies strictly inside the window is uniquely determined
by that function — it MUST equal the infinite periodic structure's own point. Only the
outermost `degree + 1` points on each end can deviate, and a guard throws (rather than
returning something subtly wrong) if a slice would ever reach them. **Standing tester rule
because of this:** every periodic vector needs a degree ≥ 2 case; degree 1 is a hand-checkable
baseline, never sufficient coverage on its own.

**Sharing a knot vector between two periodic curves/surfaces** additionally requires rescaling
BOTH onto a canonical `[0,1)`-period domain FIRST (`remapKnotsToUnitDomain`), then merging
there. An earlier version merged in normalized space but mapped insertions back to each side's
own absolute period, which could never produce literally identical arrays for two curves with
different periods — fixed before it shipped, but worth noting since it is the kind of bug that
looks correct until you try two genuinely different periods against each other.

**Surfaces add a second, harder problem that curves do not have: SEAM alignment**, and it is
the reason this section grew far beyond the original one-paragraph "periodic inputs are not
handled." Two closed curves or surfaces have no shared notion of "where the seam is" — their
control nets can be rotated relative to one another by any amount, and no flip or swap can
correct a rotation. Get this wrong and `makeSurfacesCompatible` still produces a
knot-vector-valid result; it is just the WRONG correspondence, which blends control point (i,j)
of one surface against the geometrically unrelated (i,j) of the other. This is invisible until
the result fails a smoothness check the kernel actually enforces, which is exactly what
happened:

1. **The exact-comparison insight.** After `makeSurfacesCompatible`, both surfaces' control
   points are coefficients over the SAME basis, so comparing them is arithmetic on exact data —
   no evaluation, no sampling, no tolerance. By partition of unity, control-net distance bounds
   surface distance, so the discrete question "which cyclic shift lines these nets up" answers
   the continuous question "which seam alignment lines these surfaces up." An earlier version
   sampled rings of points around each candidate seam and correlated those; it worked, but it
   was rejected on sight as exactly the sludge the future deformation feature (§9.1) needs to
   avoid, and replaced with the exact version the same session. Both directions are searched
   JOINTLY (a torus tweened against a torus has two free seams, and the best pair is not
   generally the pair of individual bests), and reversal is folded into the same search rather
   than decided separately, because reversing a closed direction also moves where its seam
   lands.
2. **The seam CHOICE is not always realizable one-sided.** A revolve's circular direction is
   stored by the kernel as Bezier arcs — fundamental knots like `[0, 0.5, 0.5, 0.5]`,
   multiplicity 3 (equal to the degree) at the arc joint. Every nonzero seam position in that
   structure IS the arc joint, so re-windowing one surface alone to the needed offset always
   lands its seam on a knot the kernel will reject as non-smooth
   (`PERIODIC_BSPLINESURFACE_NOT_SMOOTH`) — while leaving it at zero offset instead produces a
   control-net correspondence that is actually wrong, which the kernel reports as
   `BSPLINESURFACE_NOT_G1`. Both are the same underlying problem; no one-sided seam choice
   escapes it. The fix (`alignPeriodicSurfaceSeams`) splits the required offset between BOTH
   surfaces' seams, searching for a pair of parameters that are each either absent from the
   knot vector or present exactly once — inserting there yields multiplicity exactly 1, which
   is smooth — then inserts, re-windows each side to its own seam, and re-shares. Both sides
   then contribute multiplicity 1 at the merged seam, satisfying the kernel's requirement, and
   the correspondence set up by the two re-windows survives because both are remapped from the
   same shared domain by the same period.

`evApproximateBSplineSurface` returns periodic surfaces for cylinders, cones and revolves, so
none of this is an edge case for a feature that takes arbitrary face picks — it is the
mainline, and it is now exact rather than clamped.

#### 2.3.1 The kernel's periodic convention is CLOSED CLAMPED — confirmed, converted exactly

Everything above operates on the wrap STORED form. What the kernel actually HANDS a feature is
neither that form nor the "clamped knots + degree-wide control point overlap" an earlier
comment claimed ("confirmed live") from partial data. A full raw control-grid dump
(2026-08-08) settled it: a revolve's circular direction arrives **CLOSED CLAMPED** — the full
circle as two rational cubic Bezier arcs `[P0, (r,2r), (−r,2r), (−r,0), (−r,−2r), (r,−2r), P0]`
with weights `[1, ⅓, ⅓, 1, ⅓, ⅓, 1]`, ordinary clamped knots `[0×4, .5×3, 1×4]`, exactly ONE
coincident point (last = first), and `isPeriodic` as metadata meaning "this closure is smooth."
The overlap-convention fixture was disproven by convex hull before the dump confirmed it: its
seven points all lay in `y ∈ [0, 2r]`, and no rational B-spline with non-negative weights can
leave its control points' convex hull under ANY knots — those points could never have traced a
circle reaching `y = −r`. They were the module's own corrupted output, mistaken for kernel
data. **Standing rule from that mistake: fixtures come from RAW dumps only, never from
post-processing views, and forms are discriminated by DATA, never by counts — the counts
collide exactly.**

Conversion in (`normalizeSplineDefinition` / `normalizeSurfaceDefinition`, form 3) is a pure
**modular gather** — no arithmetic on point values: `stored[j] = P[(j + 1 − degree) mod n]`
with `n = N − 1` fundamental points, fundamental knots `[seam × degree, interior verbatim]`.
It is the concatenate-three-periods-and-slice construction (NURBS Book §12.1, Algorithm A12.1
in its periodic-identification form) collapsed to closed form; the closed curve repeated
end-to-end IS its own periodic extension. Emission back out
(`toClosedClampedPeriodicForm` / `toClosedClampedSurfaceDirection`) is the exact inverse:
clamped extraction over one period, `isPeriodic` kept true — the kernel's own output
convention, which it provably accepts. A no-op round trip reproduces the kernel's arrays
bit-for-bit, and the tester asserts exactly that. Emission policy: tweenSurfaces always
clamp-emits periodic directions; tweenCurves clamp-emits only when the seam multiplicity has
reached the degree (a wrap form with seam multiplicity ≥ degree is rejected at creation as
`PERIODIC_BSPLINESURFACE_NOT_SMOOTH`; smooth-seam wrap emission remains valid and validated).

#### 2.3.2 The wrap form is not unique — the canonical seam invariant

The stored wrap form admits two spellings of the same modular knot structure: the seam's
multiplicity run can sit contiguously at the domain start (`{0×3, .5×3}`) or SPLIT across the
domain boundary (`{0×1, .5×3, 1×2}` — same structure, since `1 ≡ 0` mod period). Every
structure-comparing consumer (the knot-sharing run merge, `seamKnotMultiplicity`, clamped
emission) assumes the contiguous spelling. **REVERSAL manufactures the split one**: reflecting
`[seam×m, interior…]` mirrors `m − 1` seam copies to the far end whenever `m > 1`. The
pre-canonicalization symptom was a live cone-to-cylinder failure: the run merge, comparing by
literal value, saw the two spellings as different structures and demanded seam-image
insertions above the multiplicity cap. The mult-1 seams of every earlier REVERSE fixture could
not split, which kept this invisible until Bezier-arc structures arrived.

**Invariant now enforced at the normalization choke point**: no image of the seam value may
remain at the fundamental's tail (`seamImageTailCount`); violations are re-cut by a pure
window re-index (`recutPeriodicCycle`/`recutPeriodicKnots` — no arithmetic on point values,
domain shifts by up to one period, which is meaningless for a periodic direction — but READ
domains off results, never assume them). `reverseSpline` re-normalizes its output;
`reverseSurfaceDirection` (new, the reversal for normalized surfaces) does the same;
tweenSurfaces' legacy hand-rolled `1 − knot` flip remains valid only for the RAW
pre-normalization forms it predates. `rewindowPeriodicSpline` cuts at the FIRST knot of a
multiplicity run (it kept the LAST, splitting the run whenever an alignment landed exactly on
an arc joint — the prime suspect for rotated-pair failures, pending live re-test). The
REVERSE-CANONICAL(-SURFACE) tester vectors reproduce the live failure composite.

#### 2.3.3 std `evaluateSpline` ignores weights — never an oracle for rational content

Measured live to the last digit (2026-08-08): a rational `BSplineCurve` with a well-formed
`weights` array evaluates through `@evaluateSpline` as if every weight were 1 — the returned
points are the UNWEIGHTED control polygon's curve (the rational circle came back off-circle by
~30% of r, matching an unweighted de Boor replication exactly). No error, no warning;
invisible on unit-weight curves, garbage on rational ones. The original evidence was two call
shapes from one diagnostic dump; when challenged, a standing **KERNEL-WEIGHTS probe vector**
was added to the tester — an analytic quarter circle plus the closed-clamped circle on both
`isPeriodic` flags, with the kernel's result printed every run against three candidate
readings (standard rational, weights-ignored, and premultiplied-homogeneous, i.e. the kernel
expecting `w·P` as control points — which would make the builtin usable by changing the
feeding convention). The probe is println-only, never pass/fail: it measures kernel behavior,
not module correctness. Nothing anywhere in the std library calls `evaluateSpline` at all
(verified by grep of the full mirror), so no std consumer exists that would have caught this.
**First probe run (2026-08-08, 236/236 checks green): weights-ignored CONFIRMED in every
flavor** — |kernel − unweighted| ≤ 7e-18 m at every probed parameter across degree 2 and 3,
clamped and closed-clamped forms, `isPeriodic` false and true, with the rational reading off
by 3–19 mm and premultiplied by more. The probe stays in the tester permanently; it would
immediately show if a future Onshape version fixes the builtin.

**Final confirmation, fully independent (same day):** the tester probe was itself rejected as
evidence — same author wrote the tests and the fixtures — so a standalone instrument was
built: `custom-features/evaluateSplineWeightsProbe.fs`, std-imports-only, no fixtures, no hand
mathematics. It reads a USER-DRAWN rational curve via `evCurveDefinition`, feeds the kernel's
own returned object straight back into `evaluateSpline`, judges each returned point with
`evDistance` against the actual drawn edge, and renders the points green/red on screen. Result
on hand-drawn geometry: **points up to 22.6 mm off the curve, coinciding with the
weights-omitted evaluation to exactly 0.** Kernel against kernel, user's own geometry. The
probe feature is kept in `custom-features/` as a one-click re-verification against any future
Onshape version. It burned two tester runs on a
false mismatch (a rational curve and its knot-inserted refinement are the same true curve but
different unweighted polygons) and one wrong intermediate theory (that the kernel mis-evaluates
C⁰-seam wrap forms — retracted; the kernel's wrap evaluation matched exactly once weights were
out of the comparison). It also silently corrupted tweenCurves' periodic alignment sampling for
every rational input. Consequences shipped: the tester's rational comparisons run through the
module's own basis machinery, anchored analytically by an on-circle check of the raw fixture
before any form comparison is trusted; tweenCurves evaluates through
`evaluateNormalizedSplinePoint` (module basis, rational-aware); geometry CREATION
(`opCreateBSplineCurve`/`opCreateBSplineSurface`) respects weights fine — the defect is the
evaluation builtin only. The debugging pattern that cracked all three discoveries in this
section: a **failure-only diagnostic dump** (every intermediate array verbatim plus
multi-oracle evaluations, printed only on mismatch) — keep one in every deep-math vector.

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

/** Accepts a BSplineCurve, a raw evCurveDefinition map, or a hand-built map. Forces a
    rational representation with unit weights, and guarantees
    size(knots) == size(controlPoints) + degree + 1. PRESERVES periodicity (§2.3) — recognizes
    the stored and fundamental-only overlap conventions exactly, by counting, and throws on
    anything else rather than guessing. Does NOT port std editCurve.fs's
    cleanUpPeriodicBSplineDefinition heuristic (an earlier version did; it was found to corrupt
    canonical periodic input and was removed — see the status block). */
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

// ---------- Layer 3: genuine periodic-preserving operations (§2.3) ----------
// Every function below PRESERVES periodicity exactly — none of them clamp. This is the part of
// the module that did not exist in the original plan; §11 decision 4 ("clamp-and-report") is
// superseded by all of it.

/** Insert parametersToInsert into a periodic direction (STORED form), exactly, via the
    tile/operate/slice construction (§2.3). Direct sequential insertion under the hood — use
    this, not periodicRefinementOperator, for a single point array. */
export function refinePeriodicPoints(controlPoints is array, knots is array, degree is number,
        parametersToInsert is array) returns map;                 // { controlPoints, knots }

/** Operator form of refinePeriodicPoints: same map shape as knotRefinementOperator, so it
    drops into applyKnotRefinementOperator and both tensor appliers with no special-casing.
    Reuse this across every row/column of a surface's periodic direction; building it for one
    array is the O(M^2)-for-nothing mistake documented in the status block above. */
export function periodicRefinementOperator(knots is array, degree is number,
        parametersToInsert is array) returns map;

/** Elevate a periodic direction (STORED form) from degree to targetDegree, exactly. Runs the
    clamped-whole-period window (not the cheaper unclamped one refinement uses — see §2.3) and
    a removeKnots pass on it, which requires true 4D homogeneous points. */
export function elevatePeriodicPointsRaw(controlPoints is array, knots is array, degree is number,
        targetDegree is number) returns map;                      // { controlPoints, knots }

/** Reverse a spline's direction exactly: C'(t) = C(-t). Works on clamped and periodic input
    alike — control points are only permuted, never recomputed, so the overlap condition
    survives on a periodic input. The exact replacement for rotating a control point array
    in place, which only preserves geometry when the knot vector happens to be uniform. */
export function reverseSpline(spline is map) returns map;

/** Move a periodic spline's seam (domain start) to seamParameter, exactly. A pure relabelling
    of which window onto the infinite periodic structure is stored — inserts a knot at the new
    seam first (exact) if one is not already there. This is what makes seam alignment between
    two closed curves an exact operation rather than an approximate rotation. */
export function rewindowPeriodicSpline(spline is map, seamParameter is number) returns map;

/** Surface analog of rewindowPeriodicSpline, restricted to INTEGER fundamental indices — a
    pure re-index of the control grid and knot intervals, no knot insertion, no arithmetic on
    point values. Surfaces only ever need to align to the OTHER surface's control structure, so
    the n integer positions are exactly the candidates worth considering; snapping to them costs
    nothing and keeps the operation free. */
export function rewindowPeriodicSurfaceDirection(surface is map, isUDirection is boolean,
        startIndex is number) returns map;

/** The seam-alignment fix for the case rewindowPeriodicSurfaceDirection alone cannot solve
    (§2.3): moves BOTH surfaces' seams by a split offset, each landing on a knot safe for that
    surface, so both come out multiplicity-1 (smooth) at the seam. Requires that surfaceA and
    surfaceB already share a knot vector in the given direction (i.e. have already gone through
    makeSurfacesCompatible or makeSurfacesShareKnotVectors) — that shared basis is what makes
    relativeShift's meaning well-defined and what makes re-sharing afterward exact. */
export function alignPeriodicSurfaceSeams(surfaceA is map, surfaceB is map,
        isUDirection is boolean, relativeShift is number) returns map;    // { a, b, seamA, seamB }
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
  (`knotArrayIsCorrectSize`). Periodic input is normalized ON ENTRY to the canonical wrap
  STORED form via three-way discrimination by DATA (§2.3.1): wrap-padded kept (and
  seam-canonicalized, §2.3.2), closed-clamped converted by the modular gather,
  fundamental-only extended; anything else throws with the observed shape.
- **Span rule:** largest `k` with `knots[k] <= u < knots[k+1]` **and** `knots[k] < knots[k+1]`.
  Half-open on the right, degenerate spans skipped. This is `displacementMap.fs`'s rule and it
  is what makes repeated insertion at the same parameter (needed for clamping) terminate
  correctly.
- **Multiplicity ceiling:** insertion is rejected once multiplicity would exceed `degree`.
  Clamping to `degree + 1` is reached via `clampedSegmentOperator`, which is allowed to go one
  higher by construction because it is slicing, not refining in place.
- **Periodic refinement preserves periodicity exactly — it does NOT clamp.** Superseded from
  the original plan (§2.3, §11 decision 4 struck through). Every periodic Layer 3 entry point
  tiles the infinite periodic structure into a finite window, operates on the window with
  ordinary clamped-only machinery, and slices the core period back out via a raw index slice
  (never `clampedSegmentOperator` — see §2.3 for why that distinction is load-bearing, not
  stylistic). The only place clamping still happens is the deliberate, narrow
  mixed-periodicity case: blending a periodic direction against a genuinely open one, where
  "closed" has no shared meaning to preserve regardless of what the module can do.
- **Canonical wrap-form seam invariant (§2.3.2):** the seam's multiplicity run sits
  contiguously at the domain start; no image of the seam value at the fundamental's tail.
  Enforced at normalization; reversal and re-windowing re-canonicalize their outputs. Consumers
  may compare fundamental knot structure by literal value ONLY because this holds.
- **Rational evaluation never goes through std `evaluateSpline` (§2.3.3)** — the builtin
  silently ignores weights. Homogeneous accumulation over the module's own basis machinery is
  the rational evaluator, everywhere.
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
   covered separately, see below — degree elevation of a periodic input turned out to need its
   own bug fix, not just a test).
8. **Order independence where it should hold.** `elevate(refine(s))` and `refine(elevate(s))`
   must describe the same geometry (they will differ in control point count — that is §3.1's
   point, and the test asserts the geometry, not the net). **Not yet added.**

**Periodic vectors (§2.3), added after vector 7 exposed that periodic input needed a genuine
design, not a clamp:** `PERIODIC-REFINE` / `PERIODIC-ELEVATE` / `PERIODIC-SHARE` (curve-level,
each with a degree-1 AND a degree ≥ 2 case — degree 1 alone hid the extraction bug described in
§2.3), `REVERSE` / `REWINDOW` (the two exact reparameterizations), `TIGHT-PERIODIC` (fewer
control points per period than the degree), `PERIODIC-OPERATOR` (operator-vs-direct-insertion
agreement, exact, across four fixtures — this is what keeps the deliberate code duplication
between `refinePeriodicPoints` and `periodicRefinementOperator` honest), `SURFACE-PERIODIC` /
`SURFACE-REWINDOW` (the surface analogs), and `BEZIER-SEAM` (the vector that matters most: a
fixture built from a real Onshape revolve's actual numbers, proving both that a one-sided seam
fix is impossible for that structure and that the two-sided fix works). All passing live.

Also implemented and passing curve-level checks not in the original numbered list: force-rational
normalization, `mergeKnotVectors` identity/union/refine-to-merge properties, exact-count
refinement, knot-vector sharing across two differently-structured curves, Bezier decomposition
against the parent curve, and the `makeSplinesCompatible` / `prepareSplineForDeformation`
compositions. See `splineRefinementTester.fs`'s own header comment for the full current list —
it is more current than this section.

---

## 7. Migration, phased

**Originally decided: Tween Surfaces ships first.** It is the smaller lift, it has its own
audience, and — per §3.2 — it is the *affine* case, so it exercises the entire module
(elevation, refinement, knot merge, periodic handling, rational surfaces) with none of the
approximation questions the deformation work brings. If the module is wrong, tween shows it
immediately and cheaply. If tween is right, the only remaining unknown for §9.1 is the
deformation map itself.

**Superseded by how the work actually landed, and now fully resolved:** the module was built
curves-first (Layers 1–2, then every curve-level entry point including elevation), so tweenCurves
reached zero remaining module risk before tweenSurfaces did. §11's open question 9 asked whether
to invert the original order on that basis; the answer was yes. **Both phases are now done** —
see their entries below for what each actually required, which in both cases was substantially
more than "call the module" once periodic input was taken seriously.

Read the phase numbers below as labels, not chronology — Phase 3 finished before Phase 2 started
in earnest, and the periodic-surface primitives that Phase 2 needed did not exist anywhere in
the original plan; they were designed and built between the two phases, directly modeled on the
periodic-curve primitives Phase 3 had just proven out.

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

**Phase 3 — `tweenCurves.fs`. DONE and verified live (2026-08-07/08).** Went through two
distinct versions, and the difference between them is the whole point of §2.3.

*First version:* replaced the non-periodic path with `makeSplinesCompatible` and left the
both-periodic path on its original `elevateDegree` + `matchCPCount` pipeline entirely
untouched, on the grounds that `makeSplinesCompatible` clamped periodic input and clamping a
closed curve open was unacceptable. This was explicitly challenged: *"I'm now suspicious that
Tween Curves is [a fallback to working but incorrect]... Why can't we do exact knot sharing on
periodic directions without clamping and what consequence does this have on the resulting
geometry if applied?"* That question is what produced the periodic-preserving design in §2.3.

*What actually shipped:* `matchCPCount`, `elevateDegree`, `subdivideIntoBeziers`,
`splitAtFirstKnot`, `elevateBSpline`, `isBezier`, `computeControlPointFractions`, and the local
`cleanUpPeriodicBSplineDefinition` copy are **all deleted** (~215 lines) — there is no
approximation branch left in the file, periodic or not. `tweenCurves()` now: normalizes both
inputs → aligns (exact reversal via `reverseSpline` for open pairs; exact seam alignment via
`rewindowPeriodicSpline` for closed pairs, chosen from sampled geometry since control points
aren't comparable before a shared parameterization exists) → calls `makeSplinesCompatible`
(periodic-preserving all the way through now) → blends in homogeneous coordinates → emits the
SHARED knot vector (an earlier version emitted none, which silently forced the kernel's default
uniform knots — the exact same class of bug as `tweenSurfaces.fs`'s knot-interpolation hack
below). Alignment runs on sampled geometry using a sqrt-free cross-correlation
(`bestCyclicAlignment`, §2.3's perf note) rather than the raw distance search the feature
originally used.

**Phase 2 — `tweenSurfaces.fs`. DONE and verified live (2026-08-08).** Much larger than
"delete the broken insertion, call the module" turned out to require, for the same reason
Phase 3 grew: periodic surfaces (cylinders, cones, revolves) are the mainline case, not an edge
case, and getting them right needed the surface-level periodic primitives (§2.3) built from
scratch — nothing in the original eight-hook plan anticipated seam alignment as its own
problem.

`insertKnotBoehm` (the original `Q[k+1]` bug, §2.1), `elevateSurfaceDegree`,
`refineControlPointCount`, `refineCurveControlPointCount`, `subdivideIntoBeziers`,
`splitAtFirstKnot`, `elevateBSplineCurve`, `makeUniformKnotVector`, and `isSingleSegmentBezierCurve`
are all deleted — **1931 lines down to 808.** `createTweenedSurface` now: gets both B-spline
surfaces → aligns (UV swap decided by periodicity pattern when it's decisive, corner points
otherwise, both orientations tried and scored when a torus makes even periodicity silent) →
`makeSurfacesCompatible` → the exact net-based seam search (`bestPeriodicNetAlignment`, §2.3) →
`alignPeriodicSurfaceSeams` for the case one-sided re-windowing cannot solve → blend → emit the
shared knot vectors UNCHANGED (the old code interpolated the two surfaces' knot vectors
elementwise, which is a vector belonging to neither surface, and then unpadded the result before
handing it to `bSplineSurface` — for a periodic direction that discards the very structure that
makes it periodic; this is what made fraction=0 not reproduce the first surface unless both
inputs happened to share a parameterization already).

Two live bugs found and fixed AFTER the tester was green, both revolve-only (a periodic curve
pair can't exhibit either): `BSPLINESURFACE_NOT_G1` (seam correspondence wrong — fixed by seam
alignment existing at all) and `PERIODIC_BSPLINESURFACE_NOT_SMOOTH` (one-sided re-windowing
landing the seam on a revolve's Bezier-arc joint — fixed by `alignPeriodicSurfaceSeams`'s
two-sided split). Both are documented in full in §2.3, since they are the reason that section
is as long as it is.

**Phase 4 — the tween correctness fix (§2.4). DONE for both features, as a side effect of
Phase 3 and Phase 2 respectively** — `makeSplinesCompatible`/`makeSurfacesCompatible` fix 2.4
by construction (a shared knot vector is the entire point of calling them), so there was never
a separate Phase 4 change to make once Phases 2 and 3 were done properly. The knot-interpolation
hacks in both files are gone, not patched.

**Phase 5 — `displacementMap.fs`, still not started.** Replace `extractionWeights` /
`applyExtraction` with `uniformPeriodExtractionOperator` / `applyKnotRefinementOperator`, gated
on vector 6 passing. Nothing about this phase changed; it remains last, independent of
everything above, and the sign-off in §11 already clears it to proceed whenever picked up.

The §9.1 deformation feature is the only major piece of the original plan not started. Both
tween features are now the exact, real-surface exercise that was supposed to justify building
the module before starting on deformation — that condition is met.

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

### 9.5 T-spline surfaces

Spec'd in full as a companion document: [T_SPLINE_SUPPORT_SPEC.md](T_SPLINE_SUPPORT_SPEC.md).
The short version of the dependency: T-spline→NURBS extraction is knot refinement expressed as
a linear operator on basis functions — exactly what Layer 2 computes on unit-basis rows — so
this module's Layers 1–2, the Bezier decomposition entry points, the §2.3 periodic machinery,
and `evaluateBSplineSurfacePoint` are that pipeline's numerical core, already built and
verified. Elevation and the tween compatibility family are not on that path. It also closes
§9.1.1's honest ceiling ("genuine local refinement needs T-splines and is out of scope") — that
line item is the companion spec's reason to exist. One small addition lands in this module when
that work starts: a single-basis-function evaluator over an arbitrary local knot vector
(`singleBasisFunctionValues`), the one-function sibling of `bSplineBasisValues`.

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

1. **Ordering.** ~~Tween Surfaces ships first (§7)~~ — **superseded by open question 9's
   resolution below: Tween Curves shipped first instead, and both are now done.** The
   deformation feature follows both. Modes ship progressively — flow along surface first —
   behind a map interface present from day one (§9.1).
2. **Phase 4 proceeds.** The knot-merge fix is the point of shipping tween. Its acceptance
   gate is a before/after check on a real pair of faces, because results change at
   intermediate fractions — that check is part of Phase 4, not a pre-condition for starting it.
3. **Elevation is in scope** (§3.1), and the §9 helpers — `refineSurfaceToSpanDensity`,
   `decomposeIntoBezierSegments`, `extractSubSurface`, `evaluateBSplineSurfacePoint` — build in
   Phase 1/1b rather than trickling in later. They are compositions over the operator layer,
   and front-loading them avoids a version-bump cascade across consumers when the deformation
   work starts.
4. ~~**Periodic policy: clamp-and-report.** Refining a periodic spline clamps it first and says
   so in the returned map. Friendlier than throwing and matches what
   `evApproximateBSplineSurface` consumers actually hold; the flag keeps it honest.~~
   **SUPERSEDED (2026-08-07/08) — struck through rather than deleted, because the reasoning
   that replaced it is worth keeping visible.** Rejected outright, not refined, after direct
   pushback: *"I want working code. Not fallbacks to known nonworking code... We're making
   things better, not leaving silent levers to the broken past."* Clamping computes new control
   points at a boundary with no reason to satisfy the periodic overlap condition, so a
   clamped-then-reflagged result carries a seam — position holds, tangent and curvature do not.
   That is not "friendlier than throwing," it is a silent correctness bug with a friendly
   name. Replaced by genuine periodic-preserving refinement/elevation/sharing/reversal/
   re-windowing for both curves and surfaces, plus exact two-sided seam alignment for
   surfaces — see §2.3 for the full design and why each piece exists.
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

**Open question 9, RESOLVED (2026-08-08):** *Does Phase 3 (tweenCurves) jump ahead of Phase 2
(tweenSurfaces)?* — **Yes**, option (a) from the original framing. tweenCurves shipped first;
its periodic-preserving primitives then became the direct template for tweenSurfaces' own
periodic-surface primitives, which turned out not to exist anywhere in the original plan and
had to be designed from scratch (§2.3) — seam alignment on a closed surface is a genuinely
harder problem than on a closed curve, since a surface adds a UV-swap degree of freedom and,
for revolves specifically, a seam position that is not always realizable one-sided. Both phases
are now done (§7); this question has no remaining branches.

**New open question (2026-08-08), not yet decided:**

10. **Should `alignPeriodicSurfaceSeams`'s exact net-comparison technique (§2.3) be factored
    out and reused for §9.1.1's certification loop, or for the deformation feature's own
    alignment needs?** The insight — that two surfaces sharing a basis can be compared by
    control-point arithmetic alone, with no evaluation — is more general than seam alignment.
    It was discovered here first because tween's alignment problem forced it, but §9.1.1's
    `delta` step already does a restricted version of the same thing (lifting one refinement
    level onto another's knot vector and comparing control points). Worth a pass to see whether
    one shared primitive covers both before the deformation feature grows its own copy.
