# Solid Sweep — Feature Spec

A generalized solid sweep: sweep a solid tool body along a path under a rigid motion and emit
the swept volume's **true envelope boundary** as a watertight solid — not a discretized stack of
transformed instances blended together. This is the feature class Onshape lacks entirely, SolidWorks restricts to convex
analytic revolve/extrude tools (cut-only), and Fusion implements via the
Adsul–Machchhar–Sohoni framework on procedural surfaces.

**Status (2026-08-23): build order steps 1–6 complete and live-validated; step 7 partial.
No solid body has been emitted yet — §9 is entirely unbuilt. §12.3 is the single work queue;
start there.** §12's build order records how each finished step was validated; §12.1 is the
dated audit of §6/§7 that produced the queue.

Related: [SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) (the module
whose evaluators, interpolation, and periodic machinery this is built on),
[EDIT_SURFACE_SPEC.md](EDIT_SURFACE_SPEC.md) (§2.1/§2.3.1 emission and trim doctrine reused
here), [T_SPLINE_SUPPORT_SPEC.md](T_SPLINE_SUPPORT_SPEC.md) (§5.5 knit doctrine reused here),
[FREE_FORM_DEFORMATION_SPEC.md](FREE_FORM_DEFORMATION_SPEC.md) (the refine-to-tolerance
certification loop this copies).

Decision record (2026-08-21, project owner):

1. Build the **full general simple-sweep pipeline** before shipping anything — no
   translation-only interim release. Translation and fixed-axis motions become validation
   fixtures and internal fast paths, not shipped tiers.
2. v1 **detects and rejects** non-simple (self-intersecting) sweeps with named errors carrying
   the offending time range. Trimming machinery is a later tier.
3. v1 motion scope: **path + roll-free frame** (kernel-matched transport) plus keep-orientation
   (pure translation). Twist, lock-direction/lock-faces, and closed paths deferred.

---

## 1. Reference material

In `whitepaper-references/`:

- `A_Computational_Framework_for_Boundary_Representat.pdf` — Adsul, Machchhar, Sohoni,
  *A Computational Framework for Boundary Representation of Solid Sweeps*, CAD&A 2014.
  The B-rep pipeline: funnels, correspondence, dimension-increasing Algorithm 1. NOTE: this
  scan's inline math is image-embedded; the constructs are restated in clean text in the
  sharp-features paper and in the arXiv paper below.
- `Incorporating Sharp Features in the General Solid Sweep Framework.pdf` — arXiv:1405.7457.
  Extends the framework from G¹ to G⁰ solids: cone of normals, sharp-edge prisms/funnels,
  sharp-vertex sign intervals, the two-dot-product trim criterion, Prop. 19 (convex sharp
  features are free of local self-intersection).
- `Local and Global Analysis of Parametric Solid Sweeps.pdf` — arXiv:1305.7351 (downloaded
  2026-08-21). Owns the trimming theory: the invariant μ separating local from global
  self-intersection, the general-position definition, funnel regularity proofs. Gates the
  future trimming tier (§10); v1 uses only its definitions.
- `The NURBS Book-342-650.pdf` — ch. 9 fitting theory behind the envelope fit stage.

Secondary (not in repo): Peternell, Pottmann, Steiner, Zhao, *Swept Volumes* — envelope
point/normal sampling + B-spline fitting output; precedent for replacing procedural surfaces
with fits. Wang, Jüttler, Zheng, Liu 2008 — double-reflection rotation-minimizing frames
(the motion module's pure-math fallback).

### 1.1 Notation

Motion `h(t) = (A(t), b(t))`, `A ∈ SO(3)`, over `t ∈ [t₀, t₁]`. Point trajectory
`γ_x(t) = A(t)·x + b(t)`, velocity `γ'_x(t) = A'(t)·x + b'(t)`. For a face with regular
parametrization `S(u,v)` and unit outward normal `N(u,v)`, the **envelope function** is

```
f(u,v,t) = ⟨ A(t)·N(u,v) , A'(t)·S(u,v) + b'(t) ⟩
```

Envelope candidates: the **grazing set** `{f = 0}`, the **ingress cap** (`t = t₀`, `f ≤ 0`),
and the **egress cap** (`t = t₁`, `f ≥ 0`). The **funnel** is the zero set of `f` in the prism
`D × I` (`D` = the face's UV trim domain). Under general position the funnel is a smooth
2-manifold and `Φ(u,v,t) = A(t)·S(u,v) + b(t)` restricted to it is a diffeomorphism onto the
contact set; **connected components of the funnel correspond one-to-one to envelope faces**.
Adjacency lifts from the input B-rep (papers' Theorems 11/17), so topology assembly is local
combinatorics over the input solid's entities, in dimension-increasing order.

A **simple sweep** has no self-intersection: envelope = ingress cap ∪ contact set ∪ egress
cap, and no trimming is needed. v1 is a simple-sweep implementation with detectors (§10).

---

## 2. Architecture

The framework splits into a numerical half (envelope functions, funnels, Newton marching,
adjacency lifting) and a kernel-representation half (the papers hand ACIS *procedural
surfaces* whose evaluators are the funnel parametrization). The numerical half ports to
FeatureScript nearly verbatim — it is sequential Newton + marching + combinatorics.
The procedural half cannot port: FeatureScript cannot register procedural surfaces with
Parasolid. The adaptation:

> Every procedural entity of the papers becomes a **tolerance-certified B-spline fit**, and
> the tolerance budget is engineered end-to-end. The funnel is the sampling chart for the fit.
> The lifting theorems are used unchanged — they are statements about topology, not
> representation.

Three structural decisions carry the design:

**2.1 The motion is itself B-splines.** `h(t)` is represented entrywise: the three columns of
`A(t)` and the translation `b(t)` as four 3D B-spline curves on one shared knot vector
(§4). Consequences: (a) `A'`, `A''` are exact spline derivatives — no chain rule through any
normalization; (b) for any fixed point `x`, `γ_x(t)` is an **exact B-spline curve** (control
points `A_j·x + b_j` — affine maps commute with the convex combinations of de Boor); (c) the
rigid transport of any B-spline edge is an **exact tensor-product B-spline surface** (control
net `Q_ij = A_j·P_i + b_j`, weights unchanged). So sharp-edge envelope faces, sharp-vertex
trajectory edges, and cap placement are **closed-form exact relative to the fitted motion** —
only smooth-face grazing patches need numerical fitting. This realizes in B-spline algebra the
same split the papers exploit ("the geometry of C^E is merely the sweep of a curve").
Cost: `A(t)` is only approximately orthogonal between samples; the drift is certified and
driven below 1e-9 (§4.1), far under the fit tolerance. Error framing: the envelope is computed
*exactly with respect to the fitted motion*; the motion deviates from the user's path intent
by a separately certified ε_motion.

**2.2 Coefficient certificates are the topology oracle.** Global root finding (how many
contact loops exist at a station, which faces they cross, when loops are born and die) is the
expensive, failure-prone part of a solver, and the original design handed it to the kernel:
`opCreateIsocline` at angle 0 on a scratch instance, per station, inside a
`startFeature`/`abortFeature` scope. **That is designed out as of 2026-08-23** — the census
carries it, in pure math, and the reasons are worth keeping because they are the same reasons
the coefficient-first turn of §6.0 paid off.

*The kernel route was viable and still wrong for the job.* Probe 6 proved it works (§13): wires
imprinted on a transformed instance, samples harvested, scratch cleaned up. But an isocline is
the contact curve **for a fixed direction**, so it answers the question the sweep asks only for
pure translation. Its error goes as `~|ω|·R_tool / |b'|`, which is exactly the wrong shape: it
degrades as rotation grows and **diverges at the rotation-dominant stations** the fallback was
then invented to cover. Worse, probe 6 found the degeneracy is not gradual — a face sitting at
isocline angle 0 *everywhere* (a cylinder wall viewed along its axis, a flat cap viewed across
it) has no discrete isocline and can fail the whole call. Those faces are §6.4's sliding case,
which is to say **the common case on real parts**. So the oracle needed a per-face audit in
front of it and a seeding fallback behind it, and neither of those is the oracle.

*What replaces it has no fixed-direction approximation anywhere.* `f` is a polynomial in
`(u, v, t)` per Bézier patch × t-span (§6.0.2), so an interval screen over a block that comes
back sign-definite is a **proof** that nothing grazes there, and subdivision turns that into a
certified cover of the whole zero set. §6.8's certified census compares that cover against the
census's sign grid and refines until the two agree, so "no component here" is proven rather than
sampled. `|b'| → 0` stops being a case at all: `f = ⟨A·N, A'·S + b'⟩` is as well-defined and as
polynomial when `b'` vanishes as when it dominates, and the certificate is the same certificate.
**Live-verified** on a fixture where `b'` is exactly zero — the case the fallback was invented for
— including the sliding companion the isocline call could not have survived at all: a plane
rotating about its own normal axis, whose every block coefficient comes out exactly zero (§6.8).

*Two things the kernel route did contribute, and they survive.* The per-face discipline it forced
is now where it belongs — the sliding audit (§6.4, exact and coefficient-based per §6.5) runs
before the census on every face. And FS Newton is still the corrector: the census locates
components, and every sample is polished against the true velocity field (§6.3 steps 1, 2, 4).
What is gone is the kernel call, its scratch scope, its ~30 ops (§11), and the seeding fallback
that existed only to cover the oracle's blind spot.

**2.3 Seams carry no independent error.** Adjacent output entities interpolate the **same
shared boundary sample arrays**: the co-edge strip function `g_side(s,t)` (§6.2) is
simultaneously the smooth face's funnel boundary and the sharp edge's lateral trim; cap trim
wires are built from the grazing fits' own boundary rows. Stitching is exact by construction
to fit tolerance, never by kernel tolerance absorption (T_SPLINE §5.5 doctrine: knit is
position-tolerant only; prove continuity on coefficients).

**Error budget:** `ε_total = ε_motion + ε_faceExtract + ε_envelopeFit + knit slop`, reported
per run as a ledger (the FFD "certified worst deviation" pattern). Floors: kernel linear
resolution ~1e-8 m; minimum patch edge ~1e-5 m (10³ × resolution, T_SPLINE §5.5); note
`evApproximateBSplineSurface`'s trim-curve tolerance is 10× its surface tolerance.

---

## 3. Scope of v1

- **Inputs:** one solid tool body; a path (edge chain). Path must be G¹
  (`SWEEP_PATH_NOT_G1` otherwise); open paths only.
- **Motion:** roll-free frame transport matched to the kernel's own sweeper (§4.2), or
  keep-orientation (pure translation). No twist, no lock modes, no closed paths in v1.
- **Face classes accepted** (settled 2026-08-23, §6.0.2, probe 8): the five named analytic classes
  `PLANE`, `CYLINDER`, `CONE`, `SPHERE`, `TORUS`; the two profile-driven classes `REVOLVED` and
  `EXTRUDED`; and freeform faces whose extracted net is non-rational. A face that is none of those
  *and* carries non-uniform weights — in practice a rational loft or boundary surface — raises
  `SWEEP_RATIONAL_FREEFORM_FACE` naming the face. There is no rational two-parameter coefficient
  path in v1 and no pointwise fallback for one.
- **Tool restrictions:** G¹ faces plus **convex** sharp edges (`evEdgeConvexity`):
  CONCAVE → `SWEEP_CONCAVE_EDGE` (concave edges contribute nothing generically and near-always
  produce self-intersection — sharp-features paper §8); VARIABLE → reject in v1 (later: split
  at convexity transitions). Sharp vertices with at most 3 incident faces (papers' assumption).
- **Simplicity:** required and checked (§10); non-simple input → named error with the
  offending t-range via `regenError(message, faultyParameters, entities)`. **Grazing islands are
  accepted only where the sweep's own t range clips them** — one surviving t-extreme or none
  (§7.4.1). An island that is born AND dies inside the sweep folds by construction, so it raises
  `SWEEP_ISLAND_UNSUPPORTED` rather than being emitted; resolving that case is trimming work
  (§10, post-v1).
- **Output:** a new solid body. Boolean application modes (add/remove/intersect) wrap later.

---

## 4. Motion module — `swMotionSpline.fs`

```
MotionSpline = {
    columnX, columnY, columnZ : BSplineCurve (3D, unitless),  // columns of A(t)
    translation : BSplineCurve (3D, meters),                  // b(t)
    // all four share one knot vector; degree 3 (5 if drift demands)
    orthogonalityDrift : number,          // certified sup |A(t)ᵀA(t) − I|
    stationFrames : array of CoordSystem, // raw kernel frames kept for exactly-rigid snapshots
    events : array of number              // t of path G² breaks → mandatory patch boundaries
}
```

**4.1 Fitting and drift certification.** Sample frames at stations spaced by path arc length
(`evPathTangentLines` gives arc-length parameterization across a multi-edge path), fit each
column and `b(t)` with `interpolateBSplineCurveThroughPoints(points, degree, parameters)`
(all twelve motion scalars in ONE 12-dimensional interpolation call — the O(n³) operator
build is the dominant fit cost and is shared, not paid four times). Evaluation is lean
basis+combine on the packed fit and its difference-net derivative splines (built once at
assembly) — the heavyweight rational evaluator path measured ~0.45 ms/call in profiling and
is avoided entirely; std `evaluateSpline` remains banned on anything rational (drops weights).
Certify `‖A(t)ᵀA(t) − I‖` at every knot-span midpoint; stations grow predictively (drift
scales as h⁴, so one rung predicts the count that meets tolerance — jump there with 20%
overshoot instead of blind doubling), with growth-aware plateau detection stopping honestly
at the shared sampling floor. Default tolerance **1e-6, budget-derived**: drift contributes
roughly drift × toolRadius of position error, so 1e-6 keeps the motion's share near 1e-7 m
for a 100 mm tool — an order under fit tolerance. (1e-9 was the original aspiration; see the
noise-floor finding in §4.2 for why it is unreachable on spline paths.) Cap placement and
instance transforms use `stationFrames` raw (exactly rigid), never spline evaluations.

**4.2 Frame source.** Double-reflection rotation-minimizing frames (Wang et al. 2008)
computed from the path tangents — one batched `evPathTangentLines` call plus pure math per
densification pass. No kernel scaffold in the default path at all, justified by the A/B
measurement below. `MotionFrameSource.KERNEL_SWEEP` (the `curvePattern.fs:381–473`
helper-sweep technique in a `startFeature`/`abortFeature` scratch scope) is retained purely
as a diagnostic mode.

MEASURED A/B (2026-08-21, same spline path, UI drawing off, both modes 4 rungs → 257
stations):

| Mode | Drift | Per-station ev-calls | Time |
| --- | --- | --- | --- |
| Double reflection | 3.861849112718474e-7 | 0 | 1.68 s |
| Kernel sweep | 3.861849112718474e-7 | 968 | 2.05 s |

Drift **identical to 15 digits** across genuinely different frame samples. The explanation
is structural: A(t) = S(t)·S(0)ᵀ is invariant under any *constant* twist of the frame field,
two RMF seeds differ by exactly a constant twist, and the kernel sweeper's frames match the
RMF to machine precision — so the frame source cannot affect the motion, the roll seed is
irrelevant, and the earlier "ribbon-normal noise" theory was wrong. The ~3.9e-7 drift floor
lives in the one input every source shares: the `evPathTangentLines` samples themselves.
Consequences: (1) the kernel scaffold was dropped from the default (it provably buys
nothing); (2) the 968 ev-calls cost only 0.37 s — the remaining ~1.6 s was the drift
certification calling the heavyweight rational evaluator ~1,400 times across the ladder, now
replaced by a lean basis+combine evaluation of the packed 12-D fit (one basis computation
per midpoint); (3) all twelve motion scalars are fitted in ONE 12-dimensional interpolation
call (the O(n³) operator build was being paid four times); (4) plateau detection (a doubling
improving drift < 4× stops densification) remains as the honest-stop guard against the
shared sampling floor. NOTE: `evRuledSurfaceBases` is module-private in `ruledSurface.fs`
and NOT importable — do not plan around it.

**4.3 Path policy.** G⁰ junctions rejected. G¹-but-not-G² junctions (line→arc) make `h` C¹
only: `A'` jumps, envelope faces get C⁰ station lines, and `opCreateBSplineSurface` (G1
required) would reject a fit spanning one — so junction t's are recorded in `events` and every
funnel component is split there into separately fitted patches.

**4.4 Exports.** `buildMotionSpline(context, id, pathQuery, options)` (the only context user);
pure: `motionAt(m, t) → {A, dA, ddA, b, db, ddb}` (raw unitless 3×3s / 3-vectors),
`trajectoryCurveOf(m, point) → BSplineCurve` (exact), `transportCurve(m, bSplineCurve) →
BSplineSurface` (exact edge-sweep surface), `motionSnapshotTransform(m, stationIndex) →
Transform` (raw frames).

---

## 5. Extraction — records built once in `swSweepEmit.fs`

```
ToolFaceRecord = {
    faceIndex, surface,          // unitless control net + knots + weights + degrees
    trimLoops,                   // 2D UV BSplineCurves: boundary + inner loops
    periodic : [bool, bool],     // evFacePeriodicity
    coEdges : array of indices, orientationSample
}
CoEdgeRecord = {
    edgeIndex, faceIndexLeft, faceIndexRight,
    convexity,                   // evEdgeConvexity
    curve3d : BSplineCurve,      // evCurveDefinition / evApproximateBSplineCurve
    sideNormalSplines : {left, right},  // fitted through evFaceTangentPlanesAtEdge samples
    edgeTangents,                // UNIT e'(s) at the same samples, edge default direction:
                                 // the tangent plane's own x axis, free from the call that
                                 // already runs for the normals. Shared by 6.4's
                                 // SWEEP_EDGE_SWEEP_SINGULARITY and 8's sharp-edge sheets
    uvCurveOnFace : {left, right}       // pcurves in each face's knot domain
}
VertexRecord = { point, adjacentEdges, coneNormals }
```

Sources: `evSurfaceDefinition` (exact, when B-spline or analytic — but note it reports
*storage*, not shape: spline-profile extrudes come back `EXTRUDED`; see EDIT_SURFACE §2.2) or
`evApproximateBSplineSurface` at ε_faceExtract, with its UV trim loops. GOTCHA (probe finding
2026-08-21): `evApproximateBSplineSurface`'s `tolerance` is a plain unitless number (meters
implied), NOT a ValueWithUnits — `1e-6 * meter` fails its precondition.
`evFaceTangentPlanesAtEdge` (batched, one call per side per edge) supplies the one-sided
normals that are exactly the sharp-edge cone data. Everything downstream of extraction is
context-free: **zero ev-calls in any hot loop**. All hot-loop math runs on unit-stripped
numbers (meters implied) — ValueWithUnits arithmetic in a 30K-iteration loop is the single
biggest FS slowdown.

**UV conventions (PROBE 7 RESOLVED 2026-08-22 — two conventions, not three):**

| Source | UV convention |
| --- | --- |
| Extracted `BSplineSurface` + our evaluators | the surface's own knot domain |
| `evFaceTangentPlane(s)` AND `evDistance` face parameters | one shared convention: normalized to the face's parameter-space bounding box (measured identical to ~1e-17 m on cylinder and freeform; planes/meshes return zero vectors from `evDistance`) |

All solver UV lives in the extracted spline's knot domain, evaluated with
`splineRefinementUtils` evaluators. Crossing into the kernel convention goes through **one
calibration helper** in `swSweepEmit.fs`, with probe-7-measured validity limits:

- Faces whose kernel geometry IS the extracted spline (freeform, exact `evSurfaceDefinition`
  extractions): the map is affine — measured identity to machine precision on a created
  bicubic patch (residual ~4e-17 m).
- Faces extracted by **approximation over analytic geometry**: NOT affine. Measured on a
  cylinder wall (r = 30 mm, `evApproximateBSplineSurface`, periodic rational cubic in u): the
  axial direction maps exactly affinely, but the circumferential spline parameter runs
  non-uniformly in the kernel's angle parameter — best-fit affine leaves a 4.8 mm 3D residual.
- The helper therefore probes sample points, fits the affine map, and **asserts the held-out
  residual**; on failure the crossing falls back to 3D point inversion (2D Newton on our own
  evaluators, seeded from the coarse grid — never `evDistance`). Analytic faces normally never
  hit the fallback: §6.0 extracts them exactly, where the kernel's native parameterization is
  known in closed form.
- **Point inversion doctrine (measured 2026-08-22, `swSweepEmit.fs` live self-test):** plain
  Newton on `(S−P)·S_u = (S−P)·S_v = 0` converges to whichever stationary point of the distance
  function owns the seed's basin — on a closed (periodic) wall it landed antipodally, 0.052 m
  wrong, from a 7×7 grid seed. The production inverter damps steps (halve until the 3D residual
  does not increase) and, when seedless, retries from the best three grid cells
  (`invertPointOnSurfaceFromGrid`). So hardened: 2.8e-16 m / 5 iterations on a clamped bicubic,
  3.9e-16 m / 6 iterations on the periodic wall. Curve-marching callers seed from the previous
  solution and skip the grid.
- **Extraction shape datum (2026-08-22):** `evApproximateBSplineSurface` of an elliptical
  extrude wall returns a degree 3×1, u-periodic, **rational** surface (13/4 knots at 1e-6).
  Approximated analytic-ish walls come back rational-periodic — the rational-correct evaluator
  path is mandatory everywhere, and the drop-weights hazard of std `evaluateSpline` stays fatal.

**Trim loops reach the solver through one converter,** `buildFaceTrimLoops` (§6.7). Neither
recorded shape is what the census consumes: the approximation path carries 2D B-spline trim
curves, the exact path carries pcurve samples on each co-edge, and the census masks against uv
polylines. The converter certifies the polylines it samples, discovers the loops by chaining
rather than trusting the kernel's grouping, and reports each loop's u winding so a closed face's
two kinds of loop stay distinguishable.

**Periodic faces:** `evApproximateBSplineSurface` loops are documented-unreliable on periodic
faces, which is why pooled chaining does not depend on the grouping. The seam itself needs
neither of the strategies this section used to propose: not rewindowing
(`rewindowPeriodicSurfaceDirection`), not pre-splitting at an isoparametric curve. The chainer
joins across the seam by nearest periodic image, the mask tests every periodic image of a node,
and component flood-fill treats `u mod period` explicitly — the same "the seam is absorbed, not
avoided" answer §7.5 reached for the fit.

---

## 6. Envelope solver — `swEnvelopeMath.fs` + `swFunnelSolver.fs` + `swAnalyticContact.fs` + `swOrientation.fs` (all pure)

**6.0 Solving strategy: coefficients before samples** (recalibrated by probe 2, 2026-08-21:
a pointwise surface-normal evaluation on the module's rational+units path measured **~1.1 ms
flat** — on a degree-1×3 surface — so the original blind-sampling budget of ~31K pointwise
evaluations is infeasible at 50–70 s. The solver is coefficient-first; pointwise evaluation is
the last resort, not the workhorse):

1. **Analytic faces solve in closed form — zero de Boor.** BUILT 2026-08-23, §6.5.
   `evSurfaceDefinition` yields exact
   plane/cylinder/cone/sphere/torus for most real tool faces. Plane: `f` is independent of
   (u,v) — contact is all-or-nothing per face (sliding audit), contributions come only from
   its edges. Cylinder: `N` depends only on θ, and `f` is a degree-2 trigonometric polynomial
   in θ, linear in z — per-station contact curves are closed-form `atan2` solutions.
   Sphere/cone/torus reduce the same way (quadric-on-surface / per-meridian 1D forms).
2. **Freeform faces: put `f` itself in Bernstein form and operate on coefficients.** Using the
   UNNORMALIZED normal `S_u × S_v` (identical zero set on a regular surface),
   `f = ⟨A(t)·(S_u × S_v), A'(t)·S + b'(t)⟩` is a tensor polynomial in (u,v) per Bézier patch,
   and — because the motion columns are splines in t — an explicit trivariate spline whose
   coefficients are computed ONCE by control-net arithmetic (Bernstein products). **Rational
   freeform is NOT handled here — see the decision below.** Then:
   (a) the convex-hull property rejects every patch×t-span whose coefficients have one sign —
   no evaluation ever happens where nothing grazes; (b) root isolation on the few live blocks
   uses subdivision / Bézier clipping, faster per digit than grid+Newton and fully
   deterministic; (c) events (`f_t` sign structure, §10 detectors) read off differentiated
   coefficient nets.
3. **Pointwise evaluation survives only for**: final Newton polish of isolated roots, the
   (q,t) fit grids on live patches (hundreds of points, not tens of thousands), and batched
   kernel certification. These run on a **lean evaluator**: unit-stripped, non-rational fast
   path (the measured 1.1 ms path pays for homogeneous 4-vectors and ValueWithUnits arithmetic
   that fitted non-rational data never needs — expected several-fold cheaper; re-measure with
   probe 2 once built).

**The rational-freeform decision (2026-08-23, probe 8 — §12.3 item 6 closed).** No polynomial
traces a circle, so every revolve and every conic reaches us in the weighted (divided) form, and a
ratio has no convex-hull property — which is the one thing the coefficient path is built on. The
question was whether to build a homogeneous-numerator route, screen rationals with a fattened
hull, or route them pointwise. **All three are unnecessary: the kernel names the shape.**
`SurfaceType` (query.fs) carries `REVOLVED` and `EXTRUDED` as first-class cases, and the sweep's
extraction had simply never asked — it tested `is BSplineSurface`, missed, and fell through to
approximation. Probe 8 measured what those two classes hand back:

- `evSurfaceDefinition` returns **`{ surfaceType }` and nothing else** for both — no axis, no
  profile, no direction. The class name is the entire payload.
- `evAxis(context, { "axis" : face })` returns the revolve axis. It throws on `EXTRUDED`; that
  direction comes from two `evFaceTangentPlanes` origins a full v-span apart.
- Cutting the face with a plane through the axis (`opPlane` + `opIntersectFaces`) returns the
  **exact generating profile**, not an approximation of it: a cubic profile came back degree 3 /
  5 control points / non-rational on the same knot vector it went in with, and a rational
  quarter-ellipse came back degree 2 / 3 control points / `isRational true` with weights
  `[1, 0.7071067811865476, 1]` — bit-for-bit the input. A full revolve yields two such edges, one
  per side of the axis. The extruded cross section recovers identically.

So `REVOLVED` and `EXTRUDED` become analytic classes in §6.5, and the rational problem collapses
by one dimension: the profile is a **curve**, and a one-parameter ratio clears its denominator into
an ordinary polynomial. The two-parameter rational case never arises for them. What is left after
that — a face that is no named class, no revolve, no extrusion, and genuinely weighted — is a
rational loft or boundary surface, and §3 rejects it by name in v1.

New pure utility this implies: Bernstein product and coefficient-net helpers (binomial-
convolution products, derivative nets, subdivision, root isolation) — these live in the new
standalone `custom-features/bernsteinPolynomialUtils.fs` (decided 2026-08-21), dependency-free
and deliberately NOT an addition to the published `splineRefinementUtils.fs`, so refining the
sweep never forces republish/version-bump cascades across that module's consumers.

**6.1 Functions.** Smooth face: `f(u,v,t)` as in §1.1 — held as coefficient nets per §6.0
where the face is freeform, closed-form where analytic; the pointwise form (via
`evaluateBSplineSurfaceDerivatives`/`Normal`, rational-correct, order 2 for `f_u, f_v` since
they need `N_u, N_v`) is the polish/fit-grid path only. `f_t = ⟨dA·N, v⟩ + ⟨A·N, ddA·S + ddb⟩`.

**6.2 The shared boundary object.** Co-edge strip function, per side of each edge `e(s)`:

```
g_side(s,t) = ⟨ A(t)·N_side(s) , A'(t)·e(s) + b'(t) ⟩
```

Its zero set is *simultaneously* (i) the smooth face's funnel boundary along that co-edge and
(ii) the adjacent sharp edge's lateral trim curve (the papers' `a₁ᴱ`, `a₂ᴱ` at cone parameter
α ∈ {0,1}). Computed **once** per co-edge side; the sample arrays are shared by both consumers
— this is what makes envelope stitching exact (§2.3). Sharp vertex: `s_i(t) = ⟨A·N_i, A'·Z + b'⟩`.

**6.3 Solver pipeline per input face** (dimension-increasing, papers' Algorithm 1):

1. *Vertices:* 1D Newton on `G(z,t) = 0` from sign-change brackets on the station grid
   (~5 iterations per root, |Δt| < 1e-12).
2. *Co-edge curves:* march `g_side = 0` in each (s,t) strip from one bounding vertex to the
   other — predictor along the zero-set tangent (⊥ ∇g, adaptive step), corrector 1–2 Newton
   steps along ∇g. Store (s,t) polyline + lifted 3D points.
3. *Funnel components:* the **certified census** of §6.8 — a coarse sign grid over `D × I`
   masked against the face's trim loops and its collapsed net boundaries (§6.7 — two ray
   directions, the on-boundary tolerance, and the pole mask), sign-change flood fill unioned
   with the boundary curves from step 2, and then the grid halved until the interval screen's
   dead certificates prove no component hid and the component set agrees with itself across one
   halving. No kernel call and no seed from outside (§2.2). Interior components not touching the prism boundary (grazing
   islands) get their t-extremes refined by 3-variable Newton on `(f, f_u, f_v) = 0`.
4. *Sections:* per component and fitting station `t_j`, march the p-curve `f(·,·,t_j) = 0`
   between boundary anchors; **audit the marched section for `f_t` tangencies and split it at
   each** (§6.9); then resample every piece at fixed fractions q of ITS OWN arc length, and
   re-Newton each resampled point onto `f = 0`. Output: an on-funnel (q,t) grid per piece + its
   lift. The split is what keeps q fractions from sliding along the section between stations.
5. *Orientation:* one `∂f/∂t` sign per input co-edge orients all its generated co-edges
   (alternation rule, papers §5.3); outward normal of every grazing patch is the transported
   `A(t)·N`. BUILT 2026-08-23 as its own module — §6.6.

**6.4 Degeneracy audit** (named violations; v1 = detect and report):

- `SWEEP_NOT_GENERAL_POSITION_SLIDING` — `|f| < ε_f` over a whole patch for an interval: the
  face slides along itself (cylinder along its own axis, plane parallel to translation).
  **This is the common case on real parts, not a corner case.** Benign subcases handled:
  non-contributing faces skipped; stationary-profile faces route their boundary to the edge
  machinery. The rest reject with a targeted message ("split the motion", "nudge the path").
- `SWEEP_FUNNEL_TANGENT_TO_SLICE` — `|f_t| → 0` along a p-curve (swiveling curve crossing a
  station): section extraction splits at the tangency; marching is arc-length based and
  unaffected. BUILT 2026-08-23, §6.9, with a third verdict the original two-way framing missed
  — a *stationary* section, where `f_t` is zero because the motion has no acceleration at all.
- `SWEEP_EDGE_SWEEP_SINGULARITY` — velocity parallel to a sharp edge's tangent (papers'
  Lemma 15); reject in v1. BUILT 2026-08-23, §6.9. Its solutions are isolated POINTS in (s,t),
  so a grid screens and Gauss-Newton decides; a grid alone would miss them.

**6.5 The analytic layer — `swAnalyticContact.fs` (2026-08-23, live PASS first try, 18 checks).**
§6.0 strategy 1 was specified from the start and never built: nothing consumed `record.analytic`
beyond the sliding audit, so the solver had no route at all for the faces real parts are made of
(a box is six planes, a cylinder two planes and a wall). Built now, pure, depending on nothing but
`std` — no spline evaluation, no Bézier decomposition, no de Boor anywhere.

**One identity carries the whole module.** Because `A(t)` is orthonormal to the motion module's
certified drift, the world-space inner product of §1.1 pulls back into the tool frame:

```
f = ⟨ A·N , A'·S + b' ⟩ = ⟨ N , W·S + c ⟩,     W = AᵀA',   c = Aᵀb'
```

Twelve numbers per station, and the motion never appears again. Push that once more into the
face's own orthonormal frame E = [e₁ e₂ e₃] (e₃ the axis or normal), folding the shape's origin
into the offset, and **every analytic class collapses to a single expression**:

```
f = ⟨ N_local , W_local·S_local + g_local ⟩,   W_local = EᵀWE,   g_local = Eᵀ(W·origin + c)
```

The five classes then differ *only* in how `S_local` and `N_local` are built from their own
parameters, which is what keeps this layer small enough to verify by hand. Note W is exactly skew
when A is exactly orthonormal (AᵀA = I differentiates to A'ᵀA + AᵀA' = 0), so **a rigid motion
contributes nothing to any quadratic-in-normal term** — measured at 5.1e-19 on a cylinder, which
is the module's sharpest self-check.

**Three things fall out that the freeform path has to work for.**

1. *Sliding (§6.4, "the common case on real parts") becomes exact and free.* f is identically zero
   over a face iff a handful of coefficients vanish — no sampling, and the tolerance sits on
   coefficients rather than on geometry. Verified on the two canonical cases: a cylinder
   translating along its own axis, and a plane translating parallel to itself.
2. *A conservative |f| bound over the whole face,* from the same coefficients — the analytic
   counterpart of the coefficient path's convex-hull rejection. A plane translating at 0.9 m/s
   along its normal is rejected with `minimumMagnitude` 0.9 without f being evaluated anywhere.
3. *Per-station contact curves in closed form.* Plane: f is linear, so contact is one straight
   line in (u,v). Cylinder and cone: f is LINEAR in the ruling parameter and a degree-2
   trigonometric polynomial in θ (exactly as §6.0 predicted), so the curve is the explicit graph
   `ruling = −A(θ)/B(θ)`, with the θ where B vanishes reported rather than sampled through — there
   the whole ruling either lies in the contact set or misses it entirely. Sphere and torus: one
   degree-2 trigonometric polynomial per meridian, hence at most four roots.

Supporting utility: real trigonometric polynomials `Σ aₖcos kθ + bₖ sin kθ` with exact
derivatives, an amplitude-sum bound, an identically-zero test, and a root solver that brackets on
a grid of 8 samples per harmonic and polishes with bisection-safeguarded Newton. It reports
`nearTangency` rather than resolving a merged pair, because a double root of f along a p-curve is
exactly the degeneracy §6.4 names `SWEEP_FUNNEL_TANGENT_TO_SLICE` — returning one root or three
would hide it.

**Measured** (`swAnalyticContactTester.fs`, selection-free, no geometry). The load-bearing check is
cross-path: every closed form against `evaluateAnalyticContactDirect`, which builds the world
point and normal and evaluates §1.1 without touching the pullback.
- All five classes vs the §1.1 definition: **1.1e-16 … 5.6e-16**.
- Cylinder `A(θ) + z·B(θ)` 5.6e-17; cone `A(θ) + ℓ·B(θ)` 1.1e-16; sphere and torus meridian
  polynomials 1.1e-16.
- Sliding: cylinder-along-axis and plane-parallel both detected; plane-along-normal not detected
  as sliding and its constant equals the normal speed to 5.6e-17.
- Rigid-motion skew consequence: cylinder second harmonic 5.1e-19, constant 3.4e-19.
- Cylinder under crossing translation: exactly **2** contact rulings, θ error 2.2e-14 against
  `atan2`. Sphere under translation: **24** roots over 12 meridians (two per meridian, the great
  circle ⊥ b′), latitude error 4.4e-16 against the closed-form `tan φ`.
- `cos 2θ`: 4 roots, worst error 2.8e-15. Bound 0.18193 against sampled maximum 0.18034 — valid
  and tight.

The station used for the cross-path check carries a nonzero rotation derivative and a
deliberately NON-orthonormal A, since the pullback identity holds for any A: exercising it away
from SO(3) separates an algebra error from an orthonormality assumption. That also **closes the
rotation-coverage gap for the contact function itself** — until now every step-7 measurement had
been a pure translation.

**6.5.1 Two more classes to build: `REVOLVED` and `EXTRUDED` (specified 2026-08-23, not yet
written).** Both are profile-driven rather than parameter-driven, so neither carries its shape in
`evSurfaceDefinition` — §6.0.2 records what probe 8 measured and how each one's generator is
recovered. Both then land on machinery this module already has.

*EXTRUDED* is the cheapest class in the feature. `S(u,v) = C(u) + v·d`, so `S_v = d` and the
unnormalized normal `C'(u) × d` does not depend on `v` at all. Pulled back through §6.5's identity,
`f = ⟨N_local, W_local·S_local + g_local⟩` is **linear in `v`** — the same ruling form the cylinder
and cone already solve, with the closed-form graph `v = −A(u)/B(u)` and the `u` where `B` vanishes
reported rather than sampled through. The only new work is that `A` and `B` come from a profile
curve's own coefficients rather than from `cos u`/`sin u`, so root isolation on them is polynomial
rather than trigonometric.

*REVOLVED* is the sphere/torus form generalized. In the axis frame
`S(u,θ) = (r(u)·cos θ, r(u)·sin θ, z(u))`, and `f` is a **degree-2 trigonometric polynomial in θ**
whose coefficients are built from `r, z, r', z'` at one `u` — exactly the shape the existing trig
utility takes, including its `nearTangency` report. Per-meridian solving is therefore unchanged;
what is new is that the coefficients come from evaluating a curve instead of a formula. A rational
profile is fine: clearing a one-parameter denominator leaves an ordinary polynomial, and the
denominator is strictly positive on the profile, so it cannot change the sign of `f`.

Cost note: recovering the profile is two kernel ops per revolved or extruded face (`opPlane` plus
`opIntersectFaces`), once at extraction, against zero pointwise surface evaluations for the whole
face afterwards. §11's extraction row absorbs them.

**6.6 The orientation layer — `swOrientation.fs` (2026-08-23, live PASS, 6 harness runs, every
code path exercised).**
§6.3 step 5 was one line of spec and no code, and §12.1 named it the gap that gates §9 harder
than the caps plumbing does: emission needs to know which way every patch faces, and knit needs
every shared co-edge traversed oppositely by its two owners. Built now as its own pure module
(payload discipline — the funnel solver is already at the harness size limit), depending only on
`swEnvelopeMath`, `swFunnelSolver` and `splineRefinementUtils`.

**One invariant carries every smooth-face case.** On the funnel the contact point's velocity lies
in the transported tangent plane — that is what `f = 0` *says* — so it has well-defined
coordinates there, and one scalar built from them decides everything:

```
A'S + b' = α (A·S_u) + β (A·S_v),        λ = f_t − α f_u − β f_v
```

Then for **any** chart of the funnel, the chart's parametric normal is a known multiple of the
transported normal `A·N` (with `N = S_u × S_v` unnormalized — the same normal `f` is built from):

```
project-to-(u,v) chart:   Ψ_u × Ψ_v  =  (λ / f_t) · A·N
the fit's (q,t) chart:    Φ_q × Φ_t  =  κ · λ     · A·N       where (u_q, v_q) = κ (−f_v, f_u)
```

The second follows from the first by one line of algebra on the section relation
`f_u u_q + f_v v_q = 0`; both were checked numerically against finite differences of the charts
themselves, on a generic freeform patch under a genuinely rotating motion, before the module was
written. `λ` is the papers' orientation determinant up to sign convention (framework paper's
Theorem 12 and Lemma 13); it is *not* assumed to have a fixed sign here — it is computed, and its
constancy is checked.

**Three consequences, and they are the whole module.**

1. **Patch orientation.** A fit is parameterized `u = station (t)`, `v = q`, so its net normal is
   `Φ_t × Φ_q = −κλ·A·N`: it faces **outward exactly when κλ < 0**. Nothing about the fit's
   accuracy enters — only which way the section march ran. So the fix is free and exact: reverse
   the grid's q rows before interpolating. (`reverseFitSurfaceQDirection` reverses an
   already-fitted clamped net instead, exactly; it refuses a v-periodic net, whose wrap padding
   is a different operation — those go the row route.)
2. **Co-edge orientation.** An envelope co-edge is traversed `sign(λ / f_t)` times its input
   co-edge's sense (framework paper Proposition 14). The input sense is already decided by
   extraction: swSweepEmit's "left" face is the one kept on the left when walking the edge's
   default direction, so the left face's co-edge runs +s and the right face's runs −s. λ keeps
   one sign on a component, so the `f_t` signs are what vary — and **at a fixed co-edge parameter
   the roots in t have alternating `f_t` signs by Rolle**, which is the papers' alternation rule.
   The partner co-edge on the sharp-edge face takes the opposite direction (sharp-features §7.2).
3. **The fold certificate, free.** `λ = 0` is exactly where the contact point stops moving — the
   chart folds and the envelope cusps there. So *λ holding one sign across a component* is a
   local-self-intersection certificate (§10 detector 1), produced by the orientation pass at no
   extra cost. A component whose λ flips is reported, never emitted.

**On the alternation rule: the module computes every sign instead of propagating one.** The
papers use alternation to avoid evaluating `∂f/∂t` more than once per input co-edge. Here it costs
one motion sample per branch sample, against strip marching that already found the roots — so the
rule becomes a **certificate** instead of a shortcut: two neighboring roots sharing a sign mean a
root was missed or two merged, which is exactly the degeneracy §6.4 names
`SWEEP_FUNNEL_TANGENT_TO_SLICE`. The same pass reports where a branch's own `f_t` sign flips
along it, which is a fold in the co-edge parameter and means the branch must be split there.

**Sharp features follow the sharp-features paper directly.** A sharp edge's envelope face is the
transported edge, so its parametric normal is `(A·e') × velocity`; the outward choice is whichever
of the two candidates lies inside the transported cone of normals, tested as a signed-angle
comparison in the plane ⊥ the transported tangent (every vector involved is in it). Neither
candidate inside the cone means the edge does not graze at that `(s,t)` — reported, not guessed. A
sharp-vertex trajectory edge is oriented by the paper's §7.1 test: `⟨A·d, n × w⟩ > 0` keeps the
face on the left, with `d = +e'(s₀)` at the start vertex and `−e'(s₁)` at the end.

**Caps need no orientation at all** — a cap face is a piece of the transported tool's own boundary,
so `opPattern` carries its outward normal. What §9 needs there is the *classification*, which is
the sign of `f`: the ingress cap keeps `f ≤ 0`, the egress cap `f ≥ 0` (§1.1), with samples inside
tolerance of the contact curve reported as undecidable rather than guessed.

**One implementation trap worth recording.** The uv q-direction difference must NEVER wrap the
sample index, even on a closed row: a tube fit's loop carries `u` **unwrapped** past the seam
(§7.5), so its last-to-first difference is a period-sized jump pointing the wrong way. It would
flip κ at exactly one column per station and report a spurious inconsistency on the one path
(§7c's ellipsoid) that is already live-validated. A non-wrapping adjacent difference is always
available and always right for a direction test, which is all κ's sign needs.

**Measured** (`swOrientationTester.fs`, two selection-free features, no geometry). The fixture is a
**parabolic cylinder** `S = (u, v, ½c u²)` under a translation with velocity `(1, 0, w_z(t))` — the
smallest surface on which the whole invariant is nontrivial and exact:
`N = (−cu, 0, 1)`, `f = w_z − cu`, `f_u = −c`, `f_t = w_z'`, `α = 1`, `β = 0`, `λ = w_z' + c`, and
the contact point's velocity is `(λ/c)·S_u` — so **λ = 0 is literally where the contact point
stops**, and one number dials the fold on and off.

- With `w_z` LINEAR (`c = −2`, `z₀ = −0.6`, `z₁ = −0.5`): λ and `f_t` against their closed forms,
  the outward normal against the transported unit normal, `(α, β)` against `(1, 0)`, and the
  (u,v) chart normal against `(λ/f_t)·A·N` by finite difference of the chart itself.
- The patch verdict flips with the q direction and nothing else; the grid certificate agrees with
  its own finite-difference cross-check at every sample, both ways round.
- With `w_z` QUADRATIC (`−2.1, 2.3, −2.1` in Bernstein): the co-edge strip at `v = 0` has exactly
  two roots per column at `t = ½ ∓ ½√((0.4 + 8s)/8.8)`, whose `f_t` signs must alternate — and
  whose **λ signs are opposite**, one sheet being the real envelope and the other occluded. That
  pair is the point of the second test: alternation alone orients nothing, it only says `f_t`
  flips. Both signs are needed.
- Sharp features: a convex ridge under a crossing translation is cone-selected correctly and
  flips with the edge parameterization while keeping the same outward normal; the same ridge
  plunging along its own bisector is reported outside the cone (it does not graze); the two ends
  of one sharp edge orient their vertex co-edges oppositely, which is what closes the loop.
- Net reversal is checked on a deliberately v-asymmetric RATIONAL net — points identical at
  mirrored parameters, parametric normal exactly negated.

Every expected number above was reproduced by an independent simulation of the fixtures before the
tester was written, so the live run confirmed the FeatureScript, not the mathematics.

**Live results (2026-08-23, three harness runs).** Self test 1: **PASS, 13 checks** — λ and `f_t`
against their closed forms at 8.9e-16, the outward normal at 1.6e-16, `(α, β)` at 1.1e-16, the
(u,v) chart normal against `(λ/f_t)·A·N` at 4.1e-12 relative by finite difference of the chart
itself (chart `(4.19999999998, 0, 4.99999999998)` against the exact `(4.2, 0, 5)`), the q direction
flipping the verdict both ways, the 63-sample grid certificate unanimous with **63/63**
finite-difference agreement in both q orders, cap classification exact, and rational net reversal
at 3.2e-16 / 6.7e-16. Self test 2: **PASS, 10 checks** — exactly 2 strip branches, roots 7.9e-14
off the closed form, `f_t` signs `[+1, −1]` with **21 alternation pairs and 0 violations**, the two
sheets' λ signs `[+1, −1]` (one real, one occluded), cone selection with margin 0.7071 flipping
with the edge parameterization, the non-grazing ridge reported outside the cone, and the two ends
of one edge orienting their vertex co-edges oppositely.

**Plumbed into the fit (`swEnvelopeFit.fs`).** All three fit shapes — rectangle, island, tube —
now run their (q,t) grid through `certifyFitGridOrientation` before interpolating and reverse q
when the net would otherwise face inward (`orientOutward`, default true; the flag exists to A/B
the pass, not to ship without it). Held-out certification rows are reversed in step, or every q
parameter they are checked against would be mirrored. The results carry `orientation` (the
certificate for the grid as marched) and `qReversed`.

Cost: the certificate spends one order-2 surface derivative plus one motion sample per grid
sample, deliberately at FULL grid density rather than on a stride. That is a small fraction of
what the fit already spends on the same grid — every station's section march evaluates the same
derivative at hundreds of predictor-corrector steps, and every resampled point is re-Newtoned —
so the pass rides along inside the fit's existing cost rather than adding a tier to it. If a
profile ever says otherwise, striding stations is the knob (λ's sign is constant per component,
so subsampling weakens the fold scan, not the outward verdict).

**The fit regression is clean and the reversal path is exercised (live PASS).** The step-6 fit self
test reproduces its recorded numbers to the last digit with orientation in the loop — ruled fixture
certified bound **8.723887064426035e-9** (recorded 8.7e-9), curved **4.533571042e-7** (recorded
4.5e-7) — so orientation moves no geometry, as it must. The test now also fits the SAME component
with its anchors SWAPPED, which reverses the section march and therefore κ: `qReversed` came back
`true` against the original's `false`, so the reversal and its held-out-row bookkeeping run live
whichever way a fixture falls, and the two certified deviations agree to **1.05e-16**.

**A REAL BUG THE LIVE RUN FOUND, and the sharpest lesson of this step.** Under a constant-velocity
translation `A'` and `b''` both vanish, so **`f_t` is identically zero** — and its raw
floating-point sign is then pure rounding noise. Measured on the ruled fixture: half the grid came
back `+1` and half exactly `0`, so `timeDerivativeSign == 0` fired on half the samples, and the
first version folded that into the `degenerate` flag. The whole certificate came back
`consistent: false` with every component green (λ consistent, outward, unanimous, 18/18 agreeing)
— a fixture that was in fact perfectly oriented, condemned by an irrelevant quantity. Two fixes,
both load-bearing:

1. **`f_t` is tested against a RELATIVE floor** (`stationaryTolerance`, default 1e-12, on
   `|f_t|` over the size of λ's own terms), so the stationary case resolves deterministically
   instead of by coin flip.
2. **A vanishing `f_t` is reported as `contactStationary`, not as degeneracy.** It is spec 7.7's
   profile-sweep case — the contact set does not move in the tool frame — and it costs the PATCH
   verdict nothing, because that verdict is `sign(κλ)` and never reads `f_t`. Only the CO-EDGE
   rule goes quiet, and it should: there is no `f_t` sign left to alternate.

After the fix the same fixture reports **36/36 difference agreement, consistent, and stationary
36/36**, while the curved fixture — genuinely varying velocity — reports **stationary 0/288**. That
pair is the regression test: it pins both sides of the discrimination. The general lesson is one
this module now carries in its header: *pure translation is the most common validation fixture and
the one case where the envelope degenerates, so every quantity must be checked for whether it is
even defined there before it is allowed to gate anything.*

**The closed-row half, and the fold certificate firing (live PASS).** Three more runs closed
what the rectangle path cannot reach. A third tester feature builds CLOSED-row grids on the
funnel solver's bump fixture — whose level sets are genuine loops — and covers: a collapsed pole
row being skipped (32 samples out of 40), a closed-row grid certifying with 32/32
finite-difference agreement, the closed-row reversal flipping the verdict while holding q's
origin, and **the λ-sign fold certificate firing** (fold margin 0.101, `consistent` false). Then
the production fits: the **island** fit reproduces its 2.1837e-4 deviation with **48/48 pole rows
skipped** and 48/48 difference agreement, and the **tube** fit — the only path whose rows carry u
UNWRAPPED past the seam — comes back **unanimous with 216/216 agreement, λ one-signed at fold
margin 0.9535, and `qReversed` true**: the closed-row reversal ran in a real fit, with the
certified deviation 1.988015957e-5 against the recorded 1.99e-5 and the q seam still C2 at
4.6e-18 / 1.7e-16 / 2.8e-15. Unanimity on the tube IS the seam check: a wrapping difference would
flip κ at exactly one column per station.

**THE ISLAND BUMP FIXTURE IS A LOCALLY SELF-INTERSECTING SWEEP — and orientation is what caught
it.** On that fixture α = 1 and β = 0, so `λ = w_z' + z_uu` with `z_uu = 0.8(1 − 2u) v(1 − v)`,
which changes sign at u = 0.5 — and the contact loop encircles u = 0.5. On the loop
`max |z_uu| = 0.2√(1 − 20 w_z)`, so λ keeps one sign only where `|w_z'|` exceeds that: true just
after birth and just before death, FALSE across t ≈ (0.39, 0.61). Verified independently by
finite-differencing the (q,t) chart around the loop at t = 0.6 — the chart normal's alignment with
`A·N` flips in exact lockstep with sign(λ) at all 12 samples. So the envelope of that bump folds
through its middle band, and **the step-6 island fit has been certifying a folded patch as good**,
because a deviation check cannot see a fold. That is precisely the class v1 must reject (§3, §10
detector 1). The island self test now asserts the fold IS reported. Two consequences worth
carrying: §7.4's island-emission plan needs a simple fixture to develop against, and this is the
third independent confirmation of the λ identity — the first on a closed loop.

**The fixture that was asked for does not exist** (§7.4.1, 2026-08-23): the fold is not this
bump's accident, it is what every two-pole island does. The emission developed against a CLIPPED
island instead — one pole, fold margin 0.570, one kernel face.

**One more fixture-design lesson.** At an island's birth `f(centre)` is exactly zero in closed
form but lands a few 1e-18 either side through de Boor. An exact `>= 0` pole test therefore misses
the pole on the negative side and hands back a tiny loop instead of the centre — the row is not
collapsed, the certificate does not skip it, and a caller that reverses only the non-pole rows
leaves the grid inconsistently ordered. Pole detection needs a tolerance; row reversal should
cover every row, since reversing a collapsed one is a no-op. Both cost one harness run to learn.

**Remaining live validation** — nothing below is claimed working until the owner's build says so:
nothing in the orientation layer itself — every function and both branches of every flip are
live. The one deliberate omission is `reverseFitSurfaceQDirection`'s v-periodic refusal, which is
a `throw`: a provoked throw surfaces as an INFO notice and hides the harness console (§12 harness
lesson), so it stays unit-tested by inspection. What is still untested is downstream: the §7c
ellipsoid LIVE test (kernel emission of an oriented net) and §9's caps and knit, which are the
first consumers that care whether the normal points out.

---

**6.7 The trim and degeneracy masks — the census's two inputs (2026-08-23, written and
simulation-verified; the live run is the last step).** The census masked its sign grid against "uv
polyline loops" from the first design, and nothing produced them: extraction records 2D B-spline
trim curves on the approximation path and pcurve *samples* on the exact path, and neither is a
polyline. This was audit item 1 of §12.1, and item 2 — the collapsed-boundary mask — is the same
plumbing, so both are built together. `buildFaceTrimLoops` in `swSweepEmit.fs` is the converter;
the masks themselves live with the census in `swFunnelSolver.fs`.

**Certified polylines, not sampled ones.** Each 2D trim curve is sampled uniformly inside every
distinct knot span, and the count per span DOUBLES until the held-out mid-parameter sample of every
chord sits within tolerance of that chord — §7.2's certification pattern one dimension down: the
points that decide the answer are never points the answer was built from. A degree-1 trim curve,
which is what the kernel returns for a straight boundary, certifies at one segment per span with a
bound of exactly zero, so a box costs nothing. On an exact rational circle the measured bound
tracks the equal-angle sagitta to a fixed factor of 1.11 — uniform-in-parameter subdivision leaves
the widest subsegment wider than the average, because a rational quadratic's parameter is not
proportional to angle — and falls with the square of the segment count to within 0.4 %.

**Loops are discovered, not trusted.** Every curve of every loop is pooled and chained by nearest
endpoint, reversing a segment when its far end is nearer; a chain that returns to its own head ends
that loop, and the next unused segment seeds the next. So boundary and holes separate themselves —
which matters, because `evApproximateBSplineSurface` documents outer and inner loops as *not
clearly defined* on a periodic face, and even-odd masking never needed to know which loop was
which. Growing forward only is enough for closed input (a cycle traversed forward from any of its
segments returns to that segment's head), which makes a stall genuine evidence of a gap upstream:
such a chain is returned OPEN, with the gap it stalled at, instead of closed across it.

**The seam is absorbed by the join, not by a heuristic.** With a period supplied, every join
comparison uses the nearest periodic image in u and each attached segment is slid onto that image.
Two things fall out. A trim curve running the full seam — ends one period apart, which a plain
distance reads as a period-wide gap — closes normally. And the resulting chain is already
unwrapped, because it accumulated its shifts from the joins themselves; its **winding** is then
read off the closure image, exact at any sample density, rather than reconstructed from a jump
heuristic. Folding *inside* one segment is a separate problem — an inverted pcurve whose edge
crosses the seam comes back folded mid-array — and `unwrapLoopU` fixes that per segment before any
joining.

**Winding is the distinction that matters on a closed face**, and the loops carry it rather than
leaving the census to guess. Winding zero means the loop closes on itself (a hole, or the boundary
of a face not closed in u) and its implicit closing segment is part of the polygon. Winding ±1
means the loop wraps the seam: its samples already span a period, its ends are the same point one
period apart, and there is no closing segment.

**Two ray directions, because a cyclic direction has no outside.** `uvPointInsideLoops` casts in +u
and is correct for a face that is not closed in u. On a closed face that ray never leaves the
domain, so `uvPointInsideLoopsCyclic` casts in **+v** and counts crossings against every periodic
image of the test point. One test then handles both loop kinds: a winding loop is walked without an
implicit closure and is crossed once from below — which is what makes "the band between two winding
trims" odd and everything outside it even — and a contractible loop straddling the seam is found
from both sides of it by the images. This also answers a question §5 left open: a periodic face's
trim set needs neither rewindowing nor pre-splitting, the same conclusion §7.5 reached for the fit.

**A node ON a trim loop counts as valid** (`trimBoundaryTolerance`, default 1e-6 of the smaller
domain span). The trim boundary belongs to the face, and a face that fills its whole surface has an
outer loop lying exactly on the domain rectangle — where an even-odd ray cast is a coin flip. It is
not a symmetric coin either: with the loop running through the nodes, the +u ray rejects the whole
u = 1 line and the whole v = 1 line while accepting u = 0 and v = 0, so the census would silently
delete two boundary cell rows. The boundary is exactly where co-edge components live, so this would
have removed the components §7.1 fits as rectangles. The distance sweep runs only on nodes the
crossing test rejected, so it costs the masked minority rather than every node.

**The degeneracy mask (audit item 2).** Extraction already reports `degenerate` — the collapsed
control-net boundaries of §7.8, a revolve's poles — and the census now consumes it. `S_u` vanishes
identically along a collapsed row, so the normal does and `f` with it: the pole line is a connected
zero set, every cell touching it classifies as sign-mixed, and every real component that reaches
the pole floods through it into every other one. Masking the pole's node line breaks that, and
components report `touchesDegenerateBoundary` separately from `touchesTrimBoundary` — a component
clipped by a pole is not trimmed, and it is not an island either. Which mask blocked a cell is a uv
question, so the node masks answer it directly and a cell can report both.

**Also in this pass:** the flood-fill queue is preallocated to the mixed-cell count and reused
across components. `append` copies, so growing one queue per component made the fill quadratic in
the component size, and every mixed cell is visited exactly once across all components.

**Two bugs the pre-run review caught, both about the same thing — a trim curve is allowed to
cross the whole seam in ONE chord.** A degree-1 uv line from `(uStart, v)` to `(uEnd, v)` is a
legitimate answer from the kernel, and it certifies to exactly two polyline points.

- *Unwrapping ran on it.* `unwrapLoopU`'s rule is "a step longer than half a period is a fold",
  and that chord is a step of a whole period. Unwrapping therefore collapsed it. The fix is a
  scope rule with a reason behind it: folding is an artifact of POINT INVERSION — a pcurve step
  whose seeded inversion is refused re-seeds from the in-domain grid, mid-array — while a kernel
  trim curve is continuous in the surface's own parameter space by construction. So unwrapping
  runs on the co-edge path only.
- *Closing dropped its tail.* The chainer dropped the repeated closing point on every closed
  loop, which is right for a loop that closes on itself and wrong for a winding one: a winding
  loop's tail is its head one period along, and dropping it deletes the segment covering the
  seam — leaving a stretch of u where the mask counts no crossings at all. Two points made it
  worse: the "three or more points" closure guard rejected the single-chord loop outright, so the
  face's whole mask was refused as open. Closure now admits a two-point chain when its ends sit a
  whole period apart, and the closing point comes off only when the winding is zero.

The second one is worth the space because of HOW it hid. The mask had only ever been tested on
hand-built loops, whose stored convention happened to be the one the mask wanted; the chainer's
output had only ever been tested for loop counts and windings. Neither test was wrong and the two
together still missed it. The fix is a fixture, not a rule: a chainer-output-into-cyclic-mask
round trip, at eight segments and at one, which lives in `swTrimLoopTester.fs` because that is the
only place both modules are visible.

**Simulation before the run, per the §6.5/§6.6 habit** (`scratchpad/trimplumbing.py`,
`censussim.py`): the chainer, both masks, the certified sampler, and a replica of the census were
written independently in Python, and every number the FeatureScript testers assert is one that
simulation produced. Two things it caught before a run was spent — a winding loop handed over
FOLDED telescopes back to winding zero, which is why the converter unwraps before it measures; and
the boundary coin flip above, which was found by simulating the live fixture rather than by
reasoning about it.

**Fixtures and what they pin.** `sweepEmitTrimLoopSelfTest` (pure, `swSweepEmit.fs`): a scrambled
square of degree-1 curves chaining into one exact 4-point loop; the rational circle's bound and its
second-order convergence at three tolerances (64 / 128 / 512 segments at 3.345e-4 / 8.374e-5 /
5.236e-6); a square-with-hole record converting to a 4-point and a 64-point loop at the default
tolerances; whole-seam trims closing as two winding loops both as single curves and
split-and-shuffled; a seam-straddling hole from two folded pieces chaining into one loop of span 0.1
that does NOT wind, and whole-seam trims at eight segments AND at one keeping every sample. `sweepFunnelMaskSelfTest` (pure, `swFunnelSolver.fs`): 13 cyclic band probes
including the wave's shape and the seam; 7 probes against a seam-straddling hole; a trim band that
excludes NOTHING reproducing the unmasked seam census cell for cell (80 cells, seam-crossing, not
trim-touching); a band cutting to v ∈ [0.3, 0.7] splitting that one wrapped component into two
trim-touching arcs of 8 cells each; and the degeneracy mask on a new **pole fixture** —
S(u,v) = v·(u, 1, c(u)) with c = 0.3u² − 0.2u³ under velocity (a(t), 0, 1), giving exactly
f = v·(1 − a(t)·c′(u)) and two contact sheets that share nothing but the pole line — where masking
turns one flooded 204-cell component into two 70-cell sheets.

**The live test is `swTrimLoopTester.fs`**, and it is what actually closes the audit item, because
the census had never run against a real face's trim domain. A parabolic sheet SPLIT by a projected
circle gives two faces on one surface — an annulus carrying the hole and the disc inside it — both
exactly extracted, so both take the co-edge pcurve path, the path with no other source. Every
number is closed form: S = (0.2u, 0.15v, 0.1u²) makes f = 0.03·(0.5 − u) under velocity (1, 0, 0.5),
so the contact set is the plane u = 0.5; and since u and v depend only on x and y, the projected
circle is the ellipse u = 0.5 + 0.15 cos θ, v = 0.5 + 0.2 sin θ. On a nine-node grid the only nodes
inside it are the five of a plus shape at the centre, so the hole spans the four middle v cell rows
of eight and cuts the slab into two equal halves. The assertions: the annulus gives two closed
loops and the disc one, 16 mask probes at 0.5 and 1.6 hole radii come out opposite on the two
faces, the four domain corners survive (the boundary-tolerance path), and the census goes from one
component to two equal trim-touching ones. The cell-count claim is written as a HALVING rather than
as 128 → 32 + 32, so it holds whatever u parameterization the kernel hands back.

**LIVE RESULTS (2026-08-23, all three features PASS).** Every asserted number came out as the
simulation produced it, to the last digit where the quantity is exact.

- `sweepEmitTrimLoopSelfTest` — **PASS first run.** Scrambled square: one loop, 4 points, gap 0,
  chord bound 0. Rational circle exact to 5.55e-17 at five parameters; 64 / 128 / 512 segments at
  bounds 3.34542908633e-4, 8.37407433923e-5, 5.23585155110e-6, sagitta ratios 1.1109 / 1.1122 /
  1.1125, second-order convergence factors 0.99875 and 0.99961. Square-with-hole record: loops of
  4 and 64 points, gap 0, longest chord 0.8. Whole-seam trims closed as two winding loops at both
  eight segments (sizes 9, 9) and ONE chord (sizes 2, 2). Seam-straddling hole: one loop, winding
  0, u span 0.10000000000000009. Folded ramp reads winding 0 folded, 1 unwrapped.
- `sweepFunnelMaskSelfTest` — **PASS first run.** Cyclic band 0 of 13 misclassified; seam-straddling
  hole 0 of 7. Seam fixture unmasked one 80-cell seam-crossing component; the band that excludes
  nothing reproduced it cell for cell (80 cells, seam true, trim false); the band cutting to
  v ∈ [0.3, 0.7] split it into two trim-touching 8-cell arcs, neither seam-crossing. Pole fixture:
  one flooded 204-cell component unmasked, two 70-cell sheets masked, both reporting the pole,
  zero islands.
- `swTrimLoopTester.fs` — **PASS, and it is what closes the audit item.** Chainer-to-mask round
  trip 0 of 30. Two faces, both BSPLINE **exact**, so both took the co-edge pcurve path as
  designed. Five edges, pcurve residuals 2.8e-17, 1.3e-14, 2.0e-14, 7.5e-17, 2.7e-15 — the
  inversions land on the surface at machine precision. Annulus: **2 closed loops**, 0 winding, 0
  open, closure gap **9.75e-14** (that gap IS the number of interest: two independently inverted
  pcurve ends of the same vertex, and they agree to 1e-13 in uv). Disc: 1 closed loop, gap 0.
  Chord bound 0 on both, because on this path the samples ARE the data. Mask: **0 of 16** probes
  misclassified at 0.5 and 1.6 hole radii, opposite on the two faces; **0 of 4** domain corners
  rejected, which is the boundary-tolerance path doing its job. Census on the annulus with its own
  loops: **one 128-cell slab at u = 0.5 became two 32-cell halves, both trim-touching** — exactly
  the closed form.

**Three things the live runs taught, none of which simulation could have.**

- **`ProjectionType` is not re-exported by `common.fs`** — `onshape/std/projectiontype.gen.fs`
  has to be imported explicitly. `AdjacencyType` and `BodyType` do come through, via `query.fs`.
- **Never `qEverything` in a harness test.** The harness Part Studio accumulates bodies from
  earlier runs, so `qBodyType(qEverything(BODY), SHEET)` read five faces where the fixture makes
  two. Scope to `qCreatedBy(<op id>, BODY)`.
- **`valueTolerance : 0` is wrong when the contact set lands ON a grid node**, and this fixture
  does exactly that by construction (f = 0.03(0.5 − u), nodes at multiples of 0.125). In closed
  form f is zero there; through the coefficient path it is ~1e-18 with a sign that is pure
  rounding, so each v node independently decided whether the cell columns either side of the line
  were sign-mixed, and the component came out a ragged **80 cells instead of 128**. This is the
  same failure shape as §6.6's `f_t` rounding-noise bug — *check whether a quantity is even
  DEFINED at zero before letting a sign gate anything* — one level up, on `f` itself. Passing
  1e-12 made the fixture read 128, but a number a caller guessed is not a fix; the general one is
  below.

**§6.7a The census derives its own sign tolerance — DONE (2026-08-23, live PASS).** No absolute
threshold can serve a whole face: the same number is a certificate on a block whose |f| runs to
1e-2 and pure noise on one that runs to 1e-14, and nothing upstream of a block knows which it is.
`screenEnvelopeBlockScaled` (swEnvelopeMath) takes the threshold from the block's **own** loose
value range — `ENVELOPE_RELATIVE_SIGN_TOLERANCE` (1e-12) times the larger end, floored by whatever
absolute number the caller supplies, default 0 — and returns it alongside the screen verdict, so
screening and every later sign test on that block agree on what zero means. 1e-12 is ~4500 machine
epsilons, above the cancellation a twelve-term sum of degree-elevated grid products can produce,
and (measured here) ten orders below a real block's range.

The census consumes it by storing the **sign** rather than the value at each node. The threshold a
value must clear to have a sign belongs to the block that produced it, and the block is known in
the fill loop and not in the cell loop — so signing at fill time is what makes a per-block
threshold expressible at all, and it costs no memory (the value grid becomes a sign grid). Dead
blocks fill their certified constant sign directly instead of a range bound. The returned record
carries `signTolerance : { minimum, maximum, zeroSignNodeCount }`.

**LIVE RESULT: `sweepFunnelCensusSelfTest` and `sweepFunnelMaskSelfTest` both PASS.** The
node-landing fixture — the same S(u, v) = (0.2u, 0.15v, 0.1u²) sheet the live test builds in the
kernel, now also a pure fixture (`nodeContactFixtureSurface`) so the case is covered without a
Part Studio — censuses as **one 128-cell slab** with **no tolerance supplied at all**, at a derived
threshold of **1.5e-14** and **81 zero-sign nodes of 729**, which is exactly the u = 0.5 plane
(9 v × 9 t). The threshold is 1e-12 × 0.015, and 0.015 is |f|ₘₐₓ — the interval bound is tight on
this fixture — leaving the ~1e-18 rounding noise four orders below it. `swTrimLoopTester.fs` no
longer passes a tolerance either. Every pre-existing count is unchanged: island 1 component,
trimmed 2 trim-touching caps, seam 2 open / 1 wrapped, cyclic masks 0 of 13 and 0 of 7, seam
fixture 80 cells, cutting band 2 × 8, pole fixture 204 unmasked and 70 + 70 masked. A derived
threshold only fires where the contact set actually lands on a node.

**The run also found a bug the last commit introduced and nothing had re-run.** `uvPointOnLoops`
fed its loop points to a `pointToSegmentSquaredDistance(point is Vector, ...)`, but the census
documents a trim loop as EITHER 2D Vectors (what `buildFaceTrimLoops` emits) or bare `[u, v]`
pairs (what every hand-built fixture uses), and Vector arithmetic accepts only the first — so the
census self test's trimmed case had been throwing since the boundary-tolerance rescue landed. The
distance is now component-wise on plain numbers with the endpoints indexed rather than typed:
accepts both shapes, and allocates nothing in what is the mask's inner loop.

**6.8 The certified census — the topology oracle, in pure math (2026-08-23, LIVE PASS, two
harness runs, both first try).** §2.2 records why the kernel isocline
oracle is gone. This is what carries its job.

**The census alone cannot tell "nothing here" from "too small to see".** It reads signs on a grid
and flood-fills the sign-mixed cells, so a contact loop narrower than a cell simply is not there
as far as it is concerned, and no amount of care inside the flood fill changes that. The kernel
oracle existed to answer that question from outside. The coefficient path answers it from inside:
`screenEnvelopeBlock`'s sign-definite verdict is a **proof** that `f` has no zero in a block
(every factor lies in its Bernstein hull, so the summed interval product contains `f`'s true
range), and `isolateEnvelopeCells` turns that proof into a **certified cover** — subdivide,
discard what is proven empty, and every zero of `f` is inside some surviving leaf.

**So the certificate is a comparison, not a computation.** `certifyCensusCoverage` runs the
isolation with its leaf floor set to the census's own cell size, marks every census cell that a
live leaf touches, flood-fills the marked cells into regions, and asks one question per region:
*does it contain a sign-mixed cell?* A region that does not is the only shape a missed component
can take — the zero set may be in there and the census reported nothing. Masked cells (trim,
poles) are excluded, because those are not part of the face. The reverse direction is a cross-path
check rather than a certificate: a sign-mixed cell outside every live cell would mean the
pointwise block evaluation and the interval screen disagree about where `f` can vanish, which is a
bug in one of them and not a topology finding, so `contradictions` is expected to be 0 always.

**Two halves, and only one of them is a proof.** Coverage cannot see *merging*: at a coarse enough
cell size two separate contact loops share one live region, one of them supplies the mixed cell,
and the region is certified while the census reports one component where there are two. So
`censusFunnelComponentsCertified` also requires the component set to **agree with itself at half
the cell size** — same count, same flags. That half is a convergence check, not a proof, and it
has a failure mode worth stating plainly rather than hiding: *two consecutive resolutions that are
both too coarse can agree with each other and be wrong together.* `minimumNodesPerPatch`
(default 9) is what keeps a caller away from it, and the test pins the case rather than describing
it. A refinement doubles the cells per direction (n nodes become 2n − 1), which keeps a
power-of-two node count aligned with the isolation's halving lattice so live leaves and census
cells stay one-to-one.

**The certificate is cheap, which is the reason it can be unconditional.** Measured in simulation
on the rotation fixture at 17/17/33: **3603 interval screens** (682 of them dead verdicts pruning
whole subtrees) against the **9537 node evaluations** of the census it is certifying. The
isolation is cheaper than the grid it checks because a dead cell costs one screen and buys back
everything below it — the same shape as §6.6's orientation pass riding along inside the fit.

**The rotation-dominant fixture, and why it is built the way it is.** `firstOrderRotationMotion`
(harness) is `A(t) = I + t [w]ₓ` with `b ≡ 0`, so `b'` is **exactly** zero — the `|b'| → 0` station
§2.2 used to hand to a fallback, and one where an isocline has no direction to be taken along even
in principle. `A` is the degree-1 Taylor polynomial of `exp(t [w]ₓ)`: exactly orthonormal at
t = 0, exactly `[w]ₓ` in its derivative there, drifting as O(t²|w|²) after — the regime §2.1
already accepts, and polynomial, which is what the coefficient path needs and what lets a test
assert against a closed form. The surface is a parabolic cylinder shifted off **both** parameter
origins, `S = (x, y, 0.3x²)` with `x = u + 1`, `y = v + 0.3`, giving exactly

```
f = ⟨A·N, A'·S⟩ = 0.6 x (y − t x),     N = S_u × S_v = (−0.6x, 0, 1)
```

so the contact set is the single line `v = t(u+1) − 0.3`, entering the unit domain at t = 0.15 and
still inside it at t = 1. Both offsets are load-bearing. `x` is kept away from zero or the zero set
would be that line *plus* the whole `u = 0` edge; `y` is offset by 0.3 so the birth t misses every
grid node, because on a node the birth cell's verdict comes down to the sign of a rounding error —
the first version of the fixture (offset 0.25, birth at t = 0.125 exactly) had precisely that knife
edge, and the value it asserted was one bit of noise wide.

**Predicted first, then measured live, per the §6.5/§6.6 habit** (`scratchpad/bern.py`, `envelope.py`, `census.py`,
`rotationstudy.py`, `certifiedloop.py`: the Bernstein grid arithmetic, patch factors, span
polynomials, screen, materialization, isolation, census, and the certificate, all rewritten
independently in Python). The replica was validated against the module before anything new was
asked of it: it reproduces the island fixture's `f` to **4.9e-17** and that fixture's recorded
live assertions — 4/4 contact anchors covered, 0 live cells outside the t-window. Then the live
run reproduced every simulated number below, so each figure is both predicted and measured; the
three small deltas are noted where they occur and all have the same cause.

- *Rotation fixture* (`sweepFunnelCertifiedCensusSelfTest`, **PASS, 12 checks**). `f` against
  `0.6x(y − tx)` at **1.1e-16** on the coefficient path and **2.8e-16** on the independent
  pointwise path; `translationDots` **exactly** zero in every coefficient, so `|b'|` is zero and
  not merely small; `W = AᵀA'` comes out `[[t, −1, 0], [1, t, 0], [0, 0, 0]]` as the algebra
  says. Certified after **one refinement**: 9/9/17 gives 1 component, **187** mixed cells, 276
  live, 0 unresolved, 0 contradictions; 17/17/33 gives 1 component, **763** mixed, **1120** live,
  and the flags agree, so it certifies — at **3610** interval screens, 685 of them dead. The
  component is born inside the range (`touchesTStart` false, `tMin` = 0.125, the cell below
  t = 0.15), alive at t = 1, and uv-boundary-touching throughout.
- *Sliding under rotation, both ways round.* The fixture grazes (one live non-sliding block). A
  **plane rotating about its own normal axis** slides: the interval screen's loose range is exactly
  `[0, 0]` and every materialized coefficient is **exactly** zero, not merely small — `f = ⟨N, w×S⟩`
  with `N ∥ w` is identically zero, and the coefficient path says so byte-exactly. That is the
  face class probe 6 found could fail the isocline call outright, now handled with no call at all.
The next three all live in `sweepFunnelCensusCertificateSelfTest` (**PASS, 5 checks**).

- *The island fixture.* Certifies after one refinement as exactly one island, and on both passes
  **every live cell is also a sign-mixed cell** (136 of 136, then 496 of 496) — on this fixture the
  interval screen is exactly as tight as the sign grid. 1840 screens, 424 dead.
- *Merging, caught.* The seam fixture's two lobes read as **ONE** component at 3/3/3, with
  coverage clean (0 unresolved) — the exact case coverage cannot see, and the reason the stability
  half is not optional. Refinement separates them at 5/5/5 and confirms two at 9/9/9.
- *The stability half's own limit, pinned.* The island fixture started at 3/3/5 certifies after one
  refinement at 5/5/9 with the island reported as **uv-touching** — wrong, and stable only because
  3/3/5 was wrong the same way. The test asserts that this happens, so the floor's justification
  is a fixture rather than a comment.

**The three deltas between prediction and measurement, all one cause.** At 9/9/17 the rotation
fixture came back 276 live cells against 275 predicted, and 3610 / 685 screens and dead verdicts
against 3603 / 682. The replica screens at an absolute zero threshold; the module screens at the
block's own derived sign tolerance (§6.7 — here 1e-12 × 2.22, so 2.2e-12), which leaves a handful
of sub-cells whose loose range straddles that threshold alive where the replica called them dead.
A live cell too many is the safe direction: the cover only gets larger, and every certificate
built on it stays valid. Every asserted figure — mixed cell counts, component counts and flags,
unresolved and contradiction counts, the refinement count — matched exactly.

**Fixtures** (`swFunnelSolver.fs`, both selection-free and context-free):
`sweepFunnelCertifiedCensusSelfTest` (the rotation-dominant station, its closed form, sliding both
ways, the certified census) and `sweepFunnelCensusCertificateSelfTest` (island tightness, the
merge the stability half catches, and the false-stable it does not). Split in two so each harness
payload stays inside size discipline — 66.8 KB and 61.9 KB minified, against the 37–66 KB range
every previous run has used.

**Remaining live validation:** none in this section — both features are live and every branch of
the loop (certify-on-first-refinement, merge-then-separate, and the coarse false-stable) ran. What
is untested is downstream: no production caller runs `censusFunnelComponentsCertified` yet, because
§6.3's pipeline is assembled per face by the orchestrator that §12 still lists as NOT BUILT.

**6.9 The two remaining §6.4 detectors — `swDegeneracy.fs` (2026-08-23, live PASS first try,
both features, 42 checks).** §6.4 named three degeneracies from the start and only the sliding
audit was ever built. The other two are built now, in their own pure module for the reason §6.6
gives: the funnel solver sits at the payload size that keeps a single-call harness run possible,
and both detectors reach a handful of declarations, so they cost 33 KB here against 230 KB there.

**Detector 2 needed a third verdict, and finding that out is the substance of this section.**
`SWEEP_FUNNEL_TANGENT_TO_SLICE` reads as a sign scan on `f_t` along a marched section, and the
threshold looks like a caller's parameter. It is neither, because of the most ordinary sweep there
is: a **constant-velocity translation has `f_t` identically zero on every section**, since
`f_t = ⟨A'N, v⟩ + ⟨AN, a⟩` and both terms vanish when `A' = 0` and `a = 0`. That is not a tangency
— it is a *stationary section*, the translational sweep whose envelope is the extrusion of one
contact curve — and a detector that split there would split at every point of a curve with no
tangency anywhere on it. So the audit returns three verdicts:

| verdict | what it means | what the caller does |
|---|---|---|
| `stationarySection` | `f_t` never rises above its own noise floor | nothing — do NOT split |
| `tangencies` | isolated zeros of `f_t`, refined onto the p-curve | split the section at each |
| `nearTangencies` | interior dips of \|`f_t`\| that never cross | report; never split |

**Two reference scales, because one of them collapses exactly where it is needed.** The relative
argument is §6.7a's — take the zero threshold from the data's own range, never from a number the
caller guessed — and the first reference is the Cauchy-Schwarz bound on `f_t` built from the same
four vectors, `|A'N||v| + |AN||a|`, which `evaluateEnvelopeGradientPointwise` now returns for free
alongside `f_t`. On the constant-velocity section **that bound is itself zero** (measured: exactly
0), so the ratio is 0/0 and any rounding noise in `f_t` reads as full scale. The audit therefore
carries a second reference, `valueScale = |AN||v|` — the size of `f` itself — divided by the motion
parameter's span, and calls the section stationary when `|f_t|` is under the relative floor against
**either**. The second reference is dimensionally `f` per unit `t` rather than a bound on `f_t`,
which is why the span is an explicit option instead of an assumption; the module's normalization
puts it at 1. Measured on the funnel solver's own slant section: `f_t` scale **0**, `f` scale
**1.0044**, floor 1.0e-12, largest `|f_t|` **0** → stationary. With only the first reference that
same section would have reported a tangency at every sample.

`nearTangencies` are reported and never resolved, for the reason §6.5's trig solver reports
`nearTangency` instead of returning one root or three: from a single section a merged pair and a
genuine near miss are the same picture, and choosing between them would be inventing topology.

**The split shares its seam as one value.** Adjacent pieces get the tangency's own uv as the last
element of the piece before it and the first of the piece after — §2.3's discipline, so the seam is
one number rather than two that agree to tolerance. A piece with fewer than two distinct points (a
tangency at the very start of a march, or two tangencies inside one step) is dropped and counted,
never emitted as a degenerate section.

**Detector 3's solutions are isolated points, which is why a grid cannot decide it.**
`SWEEP_EDGE_SWEEP_SINGULARITY` asks whether the velocity of a point on a sharp edge runs parallel
to the transported edge tangent — the sharp-edge sheet is `Φ(s,t) = A e(s) + b`, whose parametric
normal is `(A e') × v`, so this is exactly where §6.6's `orientSharpEdgeFace` has no normal to
return, measured here rather than only refused. Parallelism is **two** scalar conditions on a
two-parameter domain, so its solutions are generically isolated points, and a grid node lands on
one only by accident. The grid therefore **screens** — axis-neighbour local minima of the
normalized sine, plus the global minimum unconditionally — and Levenberg-damped Gauss-Newton on
the 3-residual, 2-unknown system `cross(unit A e', unit v) = 0` decides each candidate. The
residual's norm IS the sine, so the iteration that finds the point also measures how singular the
point it found is, and a candidate ruled out is recorded as ruled out rather than dropped.

The measure is the **normalized** sine `|A e' × v| / (|A e'||v|)`, never the raw cross product: the
raw one shrinks with the tool's scale and with the speed, so a slow sweep of a small part would
trip an absolute threshold everywhere. Two supporting pieces: extraction now records `edgeTangents`
(§5) from the tangent-plane call that already runs, so `e'` comes off the SAME shared samples as
the strip function; and because a co-edge's arc-length sample parameters and its spline's own
parameter are different parameterizations of one curve, each candidate's seed is obtained by
inverting its `edgePoint` onto the curve rather than by reusing the sample parameter.

**Measured** (`swDegeneracyTester.fs`, two selection-free features, no geometry).

*Detector 2 — the paraboloid-cubic patch* `S = (u, v, u²/2 + (v − ½)³/6)` at degrees (2,3) under a
translation with velocity `(1, 0, ½)` and acceleration `(0, 1, c)` at `t = ½`. With `A = I` the
whole audit reduces to `f = ½ − u` (so the p-curve is exactly `u = ½`) and
`f_t = c − (v − ½)²/2` (so `f_t` vanishes at `v = ½ ± √(2c)`). **One number moves the same fixture
through all three verdicts**, which is what stops a detector that fires on the wrong one from
blaming a change of geometry. The cubic in `v` is deliberate: it puts `(v − ½)²` into the normal, so
`f_t` has an interior extremum instead of being monotone, which is the only way one fixture can
produce both a crossing and a non-crossing dip.

- Fixture algebra: `f` against its closed form **5.6e-17**, `f_t` against its **1.7e-16**.
- `c = +0.03`: 21 marched points, **2 tangencies** at `v = 0.2550510257216817` and
  `0.7449489742783185` against `½ ∓ 0.2449489742783178` — worst `Δv` **7.8e-16**, `Δu` **0**,
  `|f_t|` **5.6e-17**, `|f|` **0**. Split into **3 pieces (7/11/7)**, 0 dropped, seams shared by
  exact equality, **0** `f_t` sign changes inside any piece, and a resample of all three at 7
  samples worst `|f|` **0**. Zero near tangencies — the samples flanking each tangency dip to 0.1 %
  of scale, and the guard is what keeps one event from being counted twice.
- `c = −0.03`: 0 tangencies, **1 near tangency** at `v = ½`, `|f_t|` 0.030000 (2.7 % of scale),
  1 piece.
- `c = −0.30`: 0 tangencies, 0 near tangencies, minimum `|f_t|` 0.300000 (25.5 % of scale) — one
  order clear of the near-tangency threshold, which is the separation the two cases exist for.
- The funnel solver's own slant section under constant velocity: **stationary**, numbers above.

*Detector 3 — the cubic edge* `e(s) = (s, s²/2, c s³/6)`, tangent `e'(s) = (1, s, c s²/2)`, under
velocity `b'(t) = (1, t, c t²/2 + ε(t − t*))`. Both have first component 1, so parallelism forces
`s = t` from the second component and then `ε(t − t*) = 0` from the third: **one isolated point** at
`(t*, t*)`, the generic case rather than a curve of degeneracies (which `ε = 0` would give, and
which would let a grid succeed for the wrong reason).

- The grid is deliberately 12 × 12 on `i/11`, so **no node lands on `t* = ½`**. Grid minimum sine
  **0.01360141**, four orders above the 1e-7 verdict threshold, and the grid-only audit correctly
  reports **not detected** — the test fails unless the refinement does the work.
- Spline `e'(s)` against its polynomial **1.3e-16**; the normalized sine over all **144 nodes**
  against its closed form **2.2e-16**; at the exact `(½, ½)`, sine **0** and cosine **1**.
- With the curve: 4 screened candidates → **1 singularity, 3 merged**, found at
  `(s, t) = (0.5, 0.5)` with refined sine **0**, from a candidate whose grid sine was 0.0447.
  Point-inversion residual **1.2e-16**. The merge is not cosmetic: without it the detector would
  have told the caller there were four singularities where there is one.
- The silent half, velocity `(0, 0.2, 1)` — whose x component 0 can never match the tangent's 1:
  minimum sine **0.8598**, 1 candidate, **1 ruled out**, not detected, 0 degenerate nodes.

**No regression in what these changes touched.** `evaluateEnvelopeGradientPointwise` gained two
keys and `correctOntoSection` gained a public overload, so `sweepFunnelPointwiseSelfTest` was
re-run: PASS, gradient against central differences 7.3e-12, section march worst `|f|` 2.8e-16.

---

## 7. Fitting & certification — `swEnvelopeFit.fs` (pure)

**7.1 The rectangle insight.** Parameterizing each funnel component by (q,t) turns the generic
component into **exactly the unit square**: q ∈ {0,1} iso-edges *are* the co-edge envelope
curves, t iso-edges *are* cap contact curves. Trim curves are absorbed into the
parameterization, so `opCreateBSplineSurface`'s single-closed-loop constraint is satisfied by
untrimmed rectangles — no boundary curves, no `opReplaceFace`. Components whose boundary
alternates lateral/cap arcs more than four times are split at loop vertices into
rectangle-able strips (strips share boundary sample arrays → exact seams) — BUILT and
live-validated 2026-08-23, §7.10, which also records what the split does *not* fix. Fallback for
pathological components: fit an extended rectangle and trim topologically (EDIT_SURFACE
§2.3.1: imprint fitted boundary wires with `opSplitFace` edgeTools, then
`opDeleteFace{leaveOpen: true}`). Grazing islands: PROBE FINDING (2026-08-21) —
`opCreateBSplineSurface` accepts a bicubic patch with one boundary row fully collapsed to a
point (probe 4, all collapse fractions through 1.0 accepted), so the island policy is plain
pole-collapsed patches. **Settled live in §7.4.1: what v1 emits is a CLIPPED island of one pole
or none, both accepted by the kernel as one face; an island with BOTH t-extremes always folds
(the λ argument there), so `SWEEP_ISLAND_UNSUPPORTED` is its only outcome, not a fallback.**

**7.2 Refine-to-tolerance loop** (the FFD `deformToTolerance` pattern):

1. Start N_q × N_t = 12 × 16 per component; `events` stations always included.
2. `interpolateBSplineSurfaceThroughGrid(liftedGrid, 3, 3)`.
3. Certify against **fresh envelope samples**: (q,t) midpoints of the grid, each
   Newton-converged onto `f = 0`, lifted, compared in pure math. The ground truth is the
   funnel itself at points the fit never saw.
4. Over tolerance → double the deficient direction, refit; cap ~60×60 control points with
   `SWEEP_FIT_BUDGET_HIT` (reported, like FFD).
5. `removeRedundantSurfaceKnots` cleanup with remaining slack.
6. Post-emission: one batched `evPointsDeviation` per patch set against span-midpoint samples
   (the `editSurface.fs` certification pattern — replicated, its helpers are private).

Considered and rejected as primary: `approximateSpline` per section + `loftBSplineSurface-
ThroughCurves` — the custom loft requires identical knot vectors across sections, which a
tolerance-driven per-section fit does not give; `makeSplinesCompatible` merges balloon counts.
Kept as a comparison harness in the tester.

**7.3 Build outcome (2026-08-23) — `custom-features/swEnvelopeFit.fs`, live-validated.**
Five harness runs. What the module does, and what each layer measured:

- *Stations* — uniform over the component's t range with event times merged EXACTLY: an event
  within a quarter-spacing of an interior station replaces it, otherwise it is inserted;
  endpoints never move.
- *Anchors* — `fixedUv` (a vertex) or `branch` (a co-edge strip-marching branch): the branch's
  (t, uv) samples are interpolated and then polished onto `f = 0` **along the sample segment**,
  by 1D Newton with the step capped at twice the segment length, so an anchor never leaves the
  shared boundary polyline that makes seams exact.
- *Rows* — each station's section is marched anchor to anchor and resampled at **2q − 1**
  arc-length fractions in ONE call: even fractions are the fit row, odd fractions are fresh
  q-midpoints held out for certification.
- *Certification, direction-resolved* — three families of fresh Newton-converged samples, each
  projected onto the fitted surface by damped closest-point inversion: q midpoints at stations
  measure the q direction, whole fresh sections at midpoint stations measure t, and their
  midpoints bound the rest. This is what tells the refinement loop WHICH direction is deficient.
- *Refinement* — the deficient direction doubles (n → 2n − 1) and the fit reruns;
  `SWEEP_FIT_BUDGET_HIT` reports the best surface rather than failing. Measured live: a 6×6
  start on the curved fixture grew to 21×11 in three rounds and certified at 7.2e-7.
- *Cleanup* — `removeRedundantSurfaceKnots` with the slack under tolerance; the reported
  `certifiedBound` is fit deviation + removal displacement, so the number stays honest.
- *Measured* (self tests, all PASS): ruled fixture 6×6, deviation 9.5e-11, removal 8.6e-9,
  certified bound 8.7e-9, analytic membership 5.0e-9; curved fixture 24×12, deviation 4.5e-7,
  analytic membership 4.2e-8. **First certified live patch: an 8×8 fit (deviation 2.0e-5,
  cleaned to a 7×6 control net) emitted through `opCreateBSplineSurface` and measured by
  `evPointsDeviation` at 5.05e-5 m against fresh envelope points at t stations the fit never
  used.**

**7.4 Islands: fit and EMISSION, both live (2026-08-23).** Pole-collapsed fitting
works and certifies — 8×8 grid, deviation 2.2e-4 with q and t balanced, **pole closure exact at
2.5e-16**, analytic envelope membership 2.7e-5 — with two corrections found live:

1. *The q direction of an island must be PERIODIC.* Fitting a closed section loop with a clamped
   v direction puts a tangent kink at the seam that dominated everything else: q deviation
   6.4e-3 against 2.7e-5 of true envelope error. Made periodic, q fell to 1.9e-4 and matched t
   (1.9e-4) — discretization only. Poles plus a periodic q is exactly how a NURBS sphere is
   represented. `splineRefinementUtils` has no periodic interpolator, so `swEnvelopeFit` builds
   one: a knot at every data parameter makes the collocation system square, the n unknown
   control points wrap, and the result is stored in the module's wrap-padded convention
   (n + degree control points, n + 2·degree + 1 knots) that its evaluators read directly.
   Validated on an analytic circle — interpolation 2.5e-16; C² across the seam (position
   8.3e-17, relative tangent 3.2e-16, relative curvature 3.1e-15); between-sample radius error
   2.09e-4 against cubic theory's h⁴/384 = 1.96e-4. Note the seam test must compare the domain
   END against the domain START, which for a periodic curve are the same point: sampling two
   nearby parameters either side instead measures the curve's own curvature across the gap and
   reads as a false kink (a 2e-6 gap on this circle reads 1.3e-5).
   Island rows therefore carry n DISTINCT points (no repeated closing point, which would make
   the system singular), resampled at 2q open-ended fractions so the last midpoint spans the
   wrap — the sample that caught this.
2. *`opCreateBSplineSurface` will not accept a whole island.* CANNOT_MAKE_BSPLINESURFACE for a
   net with BOTH u-boundary rows collapsed and v closed, whether v is declared periodic or
   converted to closed-clamped and declared non-periodic — the flag is not the problem, such a
   net has no non-degenerate boundary to form a face from. Probe 4's accepted case collapsed ONE
   boundary row of an open patch, a different shape. **So §7.1's split-at-t-extremes fallback is
   reinstated as the island emission route** — two single-pole caps sharing their mid-t loop
   sample row exactly, which is probe 4's validated shape and stitches by construction — with
   `SWEEP_ISLAND_UNSUPPORTED` behind it. **Superseded by §7.4.1** — read that: the two-pole case
   turns out to be unemittable for a reason that has nothing to do with the net's shape, and what
   v1 emits is a clipped island. The split itself is built and exact, on the shape that can carry
   it.

Also banked: `toClosedClampedSurfaceDirection` runs on homogeneous points, so a non-rational net
must be given a unit weights grid before conversion. Unit weights survive it exactly (knot
insertion rows sum to one), so they are dropped again and the emitted surface stays
non-rational.

**7.4.1 Every two-pole island folds — and that is what settles the emission (2026-08-23, two
live PASSes).** §6.6 found the bump fixture behind the step-6 island fit locally
self-intersecting through its middle band and asked for "a simple non-folding island fixture" to
develop the emission against. **No such fixture exists.** The argument is three lines and it
holds for any tool and any motion:

- At a t-extreme of a component the contact set is a single point where `f_u = f_v = 0`, so
  `λ = f_t − α f_u − β f_v` is exactly `f_t` there.
- An island's section is one closed loop at every interior station, so the sign of `f` *inside*
  that loop cannot change along the component. Both extremes therefore shrink the loop with the
  same inside sign, which forces the same definiteness of `f`'s (u,v) Hessian at both.
- With the definiteness equal, `f_t` must have OPPOSITE signs at the two extremes: one end is
  where the loop appears as t increases, the other where it disappears.

So `λ` takes both signs on any island with two poles, and λ one-signed is exactly the
local-self-intersection certificate (§6.6, §10 detector 1). Measured on the shipped fixture:
`f_t` = **−0.15999999999999998** and **+0.15999999999999992** at the two poles, with
**|λ − f_t| = 0** — bit-exact, not merely small. An independent Python recomputation had the
same ∓0.16 and put the fold band at t ∈ (0.385, 0.615), which the fixture's own comment
already recorded from the other direction.

**What islands v1 emits, then, is the CLIPPED ones**, and `fitIslandComponent` now takes
`tStart` / `tEnd` so a clip is a fit input rather than a special case. Three shapes, by how many
of the island's t-extremes survive the clip:

| poles | net | route | kernel |
|---|---|---|---|
| 0 | cylinder topology, two loop boundaries | the §7.7 tube shape | accepted (§7.7) |
| 1 | one collapsed row against one loop | probe 4's shape, v periodic | **accepted: ONE face, declared periodic** |
| 2 | both rows collapsed, v closed | none — always folded | refused, both declarations |

**Live, the one-pole cap (`sweepIslandCapLiveTest`, PASS 21 of 21).** Fixture: the same bump
surface under velocity (1, 0, 0.05 − 0.4t), so the island is born at t = 0 at the peak and the
fit is clipped at t = 0.0375 where `w_z` = 0.035 — inside the fold-free window, because
`λ = w_z' + z_uu` and max |z_uu| on the loop is `0.2 √(1 − 20 w_z)` = 0.1095 against |w_z'| = 0.4.
Fit 8×8, deviation **2.7687881792e-4**, q-limited with t at **9.02e-7**; **fold margin
0.5700296678928668** with λ one-signed at −1, 56 samples (one pole row skipped, exactly), 56/56
finite-difference agreement, zero degenerate, `contactStationary` false; pole closure
**3.14e-16**; the clipped end a genuine 0.548 m loop. Emitted as **one face with the periodic
flag on**, and kernel-certified against fresh envelope loops at t the fit never used:
**1.4297887676e-4 m** at their q midpoints — *below* the fit's own certified bound, which is the
right relationship — and **0 m** at the loops' own q fractions, which is this fit's t direction
being right to below what `evPointsDeviation` resolves. Analytic envelope membership 3.0e-8.

**The split shape works, exercised where it can be.** Cutting the fold-free cap at its middle
station gives exactly the two shapes the two-pole route would have needed — a 4×11 one-pole cap
and a 5×11 pole-free tube — and **both come back as one face each, sharing one loop row with a
control-row gap of exactly 0 m.** What buys that zero is prescribing the v parameters: both
halves interpolate the shared row against the WHOLE grid's averaged parameters, because a
clamped u interpolation already puts its end control row on its end data row, and two different
parameterizations of the same eight points are two different curves — **7.43e-5 m apart** on the
cap, 1.32e-5 m on the bump island. `interpolateFitGrid` therefore takes an optional v-parameter
override, and `splitIslandFitGrid` uses it.

**Handing the folded halves to the kernel anyway** (`requireFoldFree: false`) answers
CANNOT_MAKE_BSPLINESURFACE for both halves in both declarations — four refusals. Since the same
4×11 one-pole shape from a fold-free grid IS accepted, what the kernel is rejecting is the folded
geometry, not the split. That is the second and independent reason a two-pole island is never
emitted. `emitFitSurfacePatch` guards both attempts and REPORTS a refusal, which is how it must
behave for §9's degradation contract — found by the first run, where the unguarded second attempt
threw and took the whole test's output with it.

**Caught kernel notices suppress the console.** The MCP evaluator returns notices *instead of*
console output, so a run that provokes a caught `CANNOT_MAKE_BSPLINESURFACE` cannot also print
its verdict. `sweepIslandSplitLiveTest` therefore keeps the theorem, the fold certificate, the v1
refusal (which never reaches the kernel) and the split arithmetic — PASS 9 of 9, fit deviation
**2.1837417013741947e-4** against the 2.2e-4 on record — and leaves the kernel-refusal
measurement recorded here rather than re-run.

**One more thing the simulation got wrong first, worth keeping.** An island's loop radius grows
like √t out of a pole, so pole-clustered stations (t ∝ i²) looked like an obvious win — a
point-to-CURVE estimate said 3-10×. Point-to-SURFACE, which is what the fit measures, says the
opposite: 2.18e-4 uniform against 5.11e-4 clustered on the bump island. Chord-length
parameterization already absorbs the √t, and clustering only starves the middle. Not
implemented, and the reason is that the first estimate was measuring tangential drift along the
surface rather than distance to it — the same failure shape as §6.6's `f_t` rounding-noise bug.

**7.5 Tube components, and the periodic-seam question settled (2026-08-23, step 7).** A closed
smooth tool travelling roughly along its own parameterization axis makes a funnel component that
is neither a rectangle nor an island: a **closed section loop at every station, wrapping the
face's periodic u seam**, all the way to t₀ and t₁. The patch is a cylinder — q periodic, t
clamped, no poles — and its two t-edges are the contact loops the start and end caps get trimmed
to. It is the lateral surface of the first watertight swept solid, and `fitTubeComponent` is the
third component fitter, sharing stations, certification, and refinement with the other two.

*The seam needs no rewindow and no pre-split* — the question deferred out of steps 4 and 5 is
answered, and answered by §7.1's rectangle insight rather than by any new machinery. A section
march does not care where the face's seam is: `marchWrappingSectionLoop` carries u **unwrapped**
and wraps it only on the way into an evaluator, so one loop is one monotone strip of arc length
whatever the seam does, and the (q, t) reparameterization hands the kernel a rectangle that never
mentions u. Rewindowing would have invalidated the face's affine calibration (§5) for nothing.
What the seam does cost is one flag: q must be declared periodic, exactly as an island's q must
be (§7.4). Closure is tested against the seed's images u₀ + k·period for k ∈ {1, 0, −1}, wrap
first; k = 0 means the loop closed *without* wrapping — an island, not a tube — and is reported
rather than quietly fitted as one. Stations are seeded on a FIXED meridian (the island's fixed
+u ray, one dimension over) with each station's meridian search continuing from its
predecessor's crossing, which pins the q origin and keeps a multiply-cut meridian on one branch.

*Degenerate poles are a census problem, not a fit problem.* A surface of revolution's pole row is
a collapsed control row, so S_u × S_v vanishes identically there and f = ⟨A·n, velocity⟩ is
identically zero along the whole row **whatever the motion**. Those zeros are not contact, and a
funnel census that believes them connects every real component through the pole. Extraction now
reports them (`degenerateSplineBoundaries` → `record.degenerate`), and the fitter takes a
`vMargin` that both the meridian seed scan and the march respect — a march that reaches the
margin stops and reports `hitVBoundary`, because a component running into a pole or a face edge
is not a tube.

*Extraction and rationality (superseded by §7.8 — read that first).* `normalizeSurfaceDefinition`
flags *every* surface rational with a weight grid, so `extractToolFaceRecords` re-declares a
uniform-weight net non-rational and drops the weights (exact: a constant weight scale cancels in
the projective divide). The first attempt also re-read genuinely-rational nets through
`evApproximateBSplineSurface` with `forceNonRational` to satisfy the coefficient path; **§7.8
retracts that** — the forced conversion is unusable on periodic faces and is no longer requested
by default.

**Measured (2026-08-23, two harness runs, the second a PASS):** the fixture is an exact
non-rational barrel S(u,v) = (g(v)·cx(u), g(v)·cy(u), z(v)) — a periodic cubic profile of eight
control points on a 60 × 45 mm ellipse, g(v) = 0.5 + v − v², z(v) = 0.1v — chosen because g′ is
LINEAR, so its contact curve under translation solves in closed form:
v(u,t) = ½(1 + z′·B(u)/(A(u)·w_z)) with A = cx′cy − cy′cx, B = cy′w_x − cx′w_y. Velocity
(0 → 0.25, 0, 1) tilts the loop as t runs while holding v inside (0.26, 0.74).
- Wrapping march: uTravel **exactly one period**, section residual 2.3e-15, marched v against
  the closed form **9.0e-13**.
- Fit: 8 × 27 in two rounds, deviation 1.99e-5, direction-resolved as q 1.99e-5 / t 7.0e-7 —
  the refinement loop correctly doubled q and only q.
- Closed-form envelope membership at t values no station and no midpoint station used:
  **1.12e-5, below the certified bound** — the certification is honest, not self-referential.
- q seam C²: position 4.3e-19, relative tangent 1.2e-16, relative curvature 4.0e-14.

The first run FAILED at a 1e-5 target and was worth its cost: it showed the q error was **not
falling with q**, which an independent Python recomputation of the same periodic cubic
interpolation then explained — 2.2e-4 at q = 14, 2.2e-5 at q = 27, 3.8e-6 at q = 54, agreeing
with the harness to two digits. The barrel's tilted cubic-B-spline loop simply carries far more
curvature than the circle the first estimate assumed; the fitter was right and the target was
below what the q budget could reach. Target set to 5e-5, which the refinement loop clears in
exactly one doubling.

Knot cleanup is skipped for tube patches, as it is for islands: removal perturbs control rows and
here it would also have to preserve the wrap.

**7.6 The real ellipsoid (2026-08-23, live PASS first try).** Half an ellipse (50 × 30 mm
semi-axes) revolved about the global X axis, extracted at 1e-7 and marched at t = 0.6 under a
velocity along its own axis tilting sideways. Three things the fixture could not have told us:

1. *A revolve is not a `BSplineSurface`.* `evSurfaceDefinition` does not hand back a
   `BSplineSurface` for it at all, so it takes the approximation path on class alone — and comes
   back non-rational, poles found, calibration affine. The `dropUniformWeights` branch is still
   needed for the exact-BSPLINE faces that *are* stored as nets.
   **CORRECTED 2026-08-23 (probe 8): it is not class OTHER either — it is `SurfaceType.REVOLVED`,
   a named case in query.fs, and this spec asserted OTHER because the extraction code tested
   `is BSplineSurface`, missed, and never asked what the class actually was.** The approximation
   below is therefore a fallback we chose by omission, not one the kernel forced. §6.0.2 records
   what REVOLVED really carries and §6.5.1 specifies the analytic class that replaces this path.
2. *The kernel put the circumferential direction in V, not U*, with the poles as collapsed
   control ROWS at both ends of U (`degenerate` = true/true/false/false, periodic false/true).
   Nothing downstream may assume which direction a surface of revolution is periodic in;
   `transposeSurface` normalizes it for the tube marcher at no geometric cost.
3. *The forced non-rational approximation is enormous*: degree 3 × 3, **97 × 192 control
   points** at 1e-7. Harmless for the pointwise path — B-spline evaluation costs O(degree²) plus
   a span search, not O(control points) — but fatal to the coefficient path, which would
   decompose it into ~18,000 Bézier patches. §7.8 removes the cause: asked for the RATIONAL form
   the same ellipsoid is a 9 × 4 net that is *exact*, not approximated. The two-tolerance point
   still stands for whatever the census eventually needs, and for a single known-smooth closed
   tool (step 7's ellipsoid) the census can be skipped outright: one tube component, no islands,
   no trim.

Measured on the extracted net: uTravel exactly one period (2π — the kernel parameterizes the
circumferential direction by angle), section residual 8.6e-14, marched points **8.5e-9 m off the
analytic ellipsoid** and **4.6e-6 (sine) off the analytic contact plane**. The analytic anchor is
free here: the contact set of an ellipsoid under a translation is exactly its intersection with
the central plane ⟨∇F, w⟩ = 0, so every marched point can be checked against a closed form that
owes the module nothing.

**7.7 The lateral patch of a real swept solid (2026-08-23, live PASS first try).** The same
ellipsoid, extracted at 1e-6 (a 99 × 49 net — half the 1e-7 size, and the tolerance step 7 uses),
swept 180 mm along a STRAIGHT direction deliberately off its own axis (1, 0.35, 0 normalized).

**`opCreateBSplineSurface` ACCEPTS a v-periodic net: one face.** §7.4's CANNOT_MAKE_BSPLINESURFACE
was specific to the whole-island shape — both u rows collapsed AND v closed, a net with no
non-degenerate boundary. A tube patch is cylinder topology: two genuine boundary loops, one
closed direction, and the kernel takes it declared periodic.

Measured: fit 5 × 31 in two rounds, deviation **2.77e-6** — with the t direction at **2.5e-16**,
which is the right answer and a good check on the machinery, because a straight translation makes
the tube exactly linear in t and a cubic interpolant reproduces that identically. All the error is
q, as it was on the barrel. Emitted through `opCreateBSplineSurface` and measured by
`evPointsDeviation` against fresh envelope loops at t = 0.27 and 0.63 (stations the fit never
used): **8.0e-7 m**.

Banked from the same run: **`transposeSurface` normalizes on the way in**, which re-attaches a
unit weight grid and re-flags the net rational. Harmless to the pointwise path, fatal to the
coefficient path's guard — anything that transposes an extracted face must `dropUniformWeights`
again afterwards.

**Still open in step 7c:** the caps (tool copies at t₀/t₁, contact wires cut from the SAME
closed-clamped net the patch is emitted from, `opSplitFace` imprint, keep-side classification,
`opDeleteFace{leaveOpen}`), the knit, and the volume check. The straight-translation fixture above
was chosen so that check is exact: the swept volume of a convex tool under a straight translation
is a Minkowski sum with a segment, V = V_tool + A_silhouette·L, and for an ellipsoid with
Q = diag(a², b², b²) the silhouette area along unit d is π·a·b²·√(dᵀQ⁻¹d).

**7.8 `forceNonRational` retracted — asking the kernel to de-rationalize is a trap
(2026-08-23, owner-reported break, diagnosed and fixed live).** The step-4 extraction self test
started throwing out of `normalizeSurfaceDefinition` on fixture 3, the elliptical extrude:
*"U-periodic input matches the stored-form count but is neither wrap-padded nor
closed-clamped-with-coincident-end-rows. Refusing to guess."* The guard was right, and the cause
was `forceNonRational : true` on the approximation call — present since step 4, not introduced by
the step-7 refactor.

A probe asked the same wall for both forms at the same tolerance (1e-6):

| | net | u knots | end multiplicity | end-row gap | wrap relation |
|---|---|---|---|---|---|
| rational | 9 × 2 | `[0,0,0,0, .5,.5,.5, 1,1,1,1]` | 4 / 4 | **0 m** | closed-clamped ✓ |
| forced non-rational | 58 × 2 | every knot DOUBLED, range −0.03125 … 1.03125 | 2 / 2 | 2.0 mm | off by exactly one knot step ✗ |

The forced form is a *third* periodic spelling: knot count matching the clamped relation, C¹
doubled interior knots, a knot range overrunning the domain by a span at each end, and end control
rows that do not coincide. It is neither of the two conventions the module converts exactly, so
refusing it is correct — silently mis-reading it would have produced a subtly wrong surface.

**Nothing in the pointwise path ever needed the conversion.**
`evaluateBSplineSurfaceDerivatives` is NURBS Book A4.4 (RatSurfaceDerivs), so marching, meridian
seeding, lifting, point inversion, fitting, and certification are all rational-correct. Only
`swEnvelopeMath`'s coefficient path (§6.0) requires non-rational input, and §7.6 already concluded
that path wants its own extraction pass. So `forceNonRational` became an explicit fourth argument
to `extractToolFaceRecords`, **defaulting to false**, and a caller that turns it on owns the
spelling problem. The exact-BSPLINE branch no longer re-reads rational nets at all: an exact net
beats an approximation of it, and `dropUniformWeights` still catches the unit-weight case.

Dropping it was a strict improvement on every axis (live, one run):
- The elliptical extrude wall extracts again — 9 × 2, wrap-converted exactly, point-inversion
  round trip **5.6e-17 m**.
- The ellipsoid tool went from a 99 × 49 approximation to a **9 × 4 net that is EXACT** — 135×
  fewer control points, and the kernel's own representation of the revolve rather than a fit to it.
- The lateral envelope fit improved from 2.77e-6 to **8.02e-7** (t still exact at 1.2e-16) and its
  kernel-certified deviation from 8.0e-7 to **7.3e-7** — because the only error left is the (q, t)
  fit itself, with no extraction error underneath it.

Standing consequence for the coefficient path: extraction cannot currently hand it a non-rational
net for conic geometry, and forcing one is not the way to get it. That is an open item for the
census work, not a blocker for step 7, whose tube path is entirely pointwise.

**7.9 The rotating tube fixture — closing the "rotation through a FITTED patch" gap (2026-08-23).**
Every step-7 measurement up to here used a pure translation, which is also the one case where the
envelope degenerates to a profile sweep (§7.7) and where λ is constant by construction. §12.1
flagged a curved-path or rotating fixture through `fitTubeComponent` as the cheap way to close
that, and it was — but not in the obvious way.

**The obvious fixture is degenerate.** Rotation about the barrel's own axis at constant angular
speed plus translation along that axis is a HELIX, and a helix is a one-parameter subgroup of the
rigid motions. For any one-parameter subgroup the pullback velocity `A^T(A'p + b')` is
*independent of t*, so the contact set never moves and `f_t` vanishes identically — the sliding
case (§6.4 detector 1) wearing a rotation costume. Measured on the first attempt: contact-v travel
5e-4 over the whole sweep and `|f_t| ~ 1e-7`. Two changes break the group structure, and both are
needed: **θ′ varies with t** (so the rotation term K varies) and the translation carries a
component **perpendicular** to the rotation axis (so `β = A^T b'` turns under A). Travel goes from
5e-4 to **0.168**.

**Rotation about z is non-degenerate only because the profile is an ELLIPSE.** K carries the factor
`d/du (cx² + cy²)`, identically zero on a circle — that *is* the sliding case — and zero only at
the ellipse's four axis points.

**The contact curve stays closed form, and the reason is a structural one worth keeping.** With
`w = M S + β`, `M = A^T A'`, the v-degree of `f` is set by **M's third row**: the term
`g' P (M₂₀ g cx + M₂₁ g cy)` is *cubic* in v. Holding A's z column at ẑ makes that row exactly
zero — `ẑ·x' = ẑ·y' = 0` for columns that stay in the xy-plane — **with no rigidity assumption
anywhere**. Then, with `z'` the constant height derivative and `P = cx' cy − cy' cx`,

```
f / g = K g + L + g' P β_z
K = z' (m₀₀ cy' cx + m₀₁ cy' cy − m₁₀ cx' cx − m₁₁ cx' cy)
L = z' (cy' β_x − cx' β_y)
```

and since `g = 0.5 + v − v²`, `g' = 1 − 2v`, the condition `f = 0` is the **quadratic**
`−K v² + (K − 2R) v + (0.5K + L + R) = 0` with `R = P β_z`, whose `K → 0` limit is exactly the
straight-translation form `tubeFixtureExactV` already solved (verified against it to 5.6e-17).

**Why the closed form is exact regardless of drift.** `f = <A N, A'S + b'>` equals
`<N, A^T A' S + A^T b'>` by *transpose alone* — no orthogonality needed — so deriving the closed
form from the STORED columns matches the shipped `f` whatever the rotation's drift is.

**Drift is unavoidable, and that makes it the realistic input.** No non-constant polynomial curve
lies in SO(3): `p² + q² ≡ 1` forces p, q constant by a leading-term argument. So a non-rational
B-spline rotation ALWAYS drifts — which is precisely why `swMotionSpline` certifies
`orthogonalityDrift` instead of assuming exactness. Each column is a cubic Hermite of cos/sin per
span over eight spans in Bézier form (interior knot multiplicity 3), holding drift near **1.2e-6**,
forty times under the fit tolerance, with every motion spline still at degree 3.

**The numerical trap this caught, which would have read as "fine".** Solved with the textbook
quadratic formula the residual sat at **5.2e-12** — nine orders above float noise on an f of scale
1e-3, and small enough that nobody would have questioned it. The worst case is at the ellipse's
axis points, where the quadratic coefficient falls to 1.7e-12 while the linear one holds at 5.2e-4:
classic cancellation in the root near `−c/b`. Using the stable pair
`q = −(b + sign(b)√disc)/2`, roots `c/q` and `q/a`, the residual drops to **1.2e-19** — machine
precision — and `c/q` covers the `K = 0` case outright, so the separate linear branch disappears.

**Verified numbers (Python simulation and the FeatureScript agreeing to every printed digit).**
Rotation 25° with θ(t) = 25°(0.35t + 0.65t²), world velocity (0.04, 0, 0.15):

| quantity | FeatureScript | simulation |
|---|---|---|
| orthonormality drift | 1.225623174283541e-6 | 1.2256e-6 |
| worst \|cos − stored\| | 6.119556803518833e-7 | 6.1196e-7 |
| worst \|M third row\| | 3.55e-15 | 0 (literal ẑ) |
| worst \|f\| at the closed-form v | **1.355e-19** | 1.220e-19 |
| contact-set travel over t | 0.16825182578746772 | 0.1683 |
| worst \|v − 0.5\| | 0.3053913378256745 | 0.3054 |
| gap against the same motion with A = I | 0.17785263543683444 | 0.1779 |
| contact v at t=0, u=3 | 0.31523537978738914 | 0.315235 |

λ (defined `f_t − α f_u − β f_v`, §6.6) comes out **one-signed at fold margin 0.732** over a 48×17
grid, with `f_t` *not* one-signed — the isolated `f_t` zeros §7.5 already documented. That
one-signedness is the first non-trivial exercise of the fold certificate: on every earlier step-7
fixture λ was constant by construction. The same computation reproduces the existing barrel's
one-signedness (fold margin 0.788) as its gate, which is what confirmed the λ definition was being
read correctly — the first attempt used `f_t/|∇f|` and reported a sign change that does not exist.

**`|M third row|` is 3.55e-15, not 0.** Zero by construction, but the column arrives through a
derivative spline built from differences, so what survives is rounding rather than structure. The
assertion is bounded at 1e-13: tight enough to catch a genuinely nonzero entry, which would make f
cubic in v and void the closed form.

**LIVE RESULT: `sweepRotatingTubeSelfTest` PASSES 17 of 17 (2026-08-23), in a real Part Studio
through the kernel.** Two runs; the first came back 16 of 17 and the single failure was this
test's own tolerance, described below.

```
stored rotation: drift 1.129762009721702e-6, |cos - stored| 5.640472215961978e-7,
                 |M third row| 6.661338147750941e-16
|f| at the closed-form contact v: 1.3552527156068805e-19
contact set: travel 0.16825182578746772, worst |v - 0.5| 0.3021603359585632,
             gap against the same motion with A = I 0.17785263543683444
marched loop at t = 0.7: uTravel 8 (period 8), section residual 7.8150082027462e-14,
                         worst v vs the closed form 2.0218193785837002e-10
fit: 8x27 grid in 2 rounds, deviation 3.15184531808544e-5
     (q 3.15184531808544e-5, t 4.634490954537516e-7), section residual 9.884494007257778e-14,
     budgetHit false
orientation: 216 samples, lambda sign -1 consistent TRUE, worst fold margin 0.7515974602343038,
             unanimous TRUE, difference 216/216 agree, stationary 0, degenerate 0,
             consistent TRUE
closed-form envelope membership: 1.3109501005136909e-5
```

**What this closes.** The fit is `8x27` at **3.15e-5**, q-limited, with t at 4.6e-7 — the same
signature as the straight barrel (§7.5) but now under a rotation. The orientation certificate ran
**216 samples with λ one-signed at fold margin 0.752 and 216/216 difference-normal agreement, zero
degenerate** — so the fold certificate has now been exercised on a component where λ is *not*
constant by construction, which is exactly what §12.1's coverage gap asked for. `|f|` at the
closed-form contact v came back **1.3552527156068805e-19**, bit-identical to the standalone fixture
run, which is what confirms the quadratic and its stable root form are exact in the shipped code.

**The one real correction the run produced: a tolerance that was wrong for this fixture.** The
marched v sat 2.02e-10 off the closed form against an asserted 1e-11. That is not marcher error —
it is the section tolerance converted into a v error. The marcher stops at `|f| <= sectionTolerance`
(1e-13 here), and `Δv = |f| / |f_v|`; this fixture's contact function is FLAT in v because the
quadratic coefficient K is small, so `|f_v| ≈ 5e-4` and the floor sits at ~2e-10. The straight
barrel passes the same 1e-11 only because its steeper `f_v` hides the identical residual. Bound
corrected to 1e-9 with the derivation in the code. Tightening `sectionTolerance` instead would buy
nothing: the fit is q-limited at 3.15e-5, ten orders above this.

**How it was run, and the correction to §12.2's claim.** §12.2 recorded that the MCP evaluators run
in the server's own scratch document (confirmed again here: document `0d9800971a8d63e03c6c22e3`,
trojan studio `53ec560db346b51f8e77b47d`), so a same-document import of a development-document tab
cannot resolve there — and `put_featurescript` targets the development document but returns `null`
even for an undefined function, so it validates nothing. The conclusion drawn from that, that this
test needed a run in the development document, was **wrong**: inlining runs it in a real Part Studio
through the real kernel, which is all this test needs, since `fitTubeComponent` and everything under
it are pure. `tools/buildMcpPayload.py` now does the inlining mechanically — it walks the call graph
from a named feature over the sweep modules, emits only reachable declarations (constants first, so
the merge does not depend on top-level resolution order), and strips comments. For this test that is
**65 of 259 declarations, 1595 lines, 58 KB**, against 10952 lines for a naive concatenation. It
also reports duplicate top-level names across modules, and found none — which is the §12.2
consolidation paying for itself, since a merged payload cannot tolerate two copies of one function.


**7.10 Strip decomposition — the >4-alternation component (2026-08-23, live PASS, 38 checks
across two features).** §7.1 asserted the split in one sentence for five months; this is what it
turned out to be.

**What makes a component more than a rectangle, exactly.** Cap arcs live only at t₀ and t₁, and no
two of them can be adjacent around the boundary loop, so the alternation count is simply *the
number of side endpoints landing on the two caps* — 4 for a rectangle, 6 for a component whose
section is two arcs at one end and one at the other. Between those two states something must
change the arc count, and inside a trimmed domain the only generic way is a **tangency**: the
section curve touches the face boundary, and there the lateral branch's t(s) has an extremum. So
"split at loop vertices" is precisely "cut every lateral branch at its own interior t extrema, band
the t range at those times, and pair each band's sides into arcs".

**The four pieces, and the one that needed an oracle.** Sides (branches cut at refined extrema),
bands (t intervals between cut times), strips (a band's sides paired into arcs), seams (one marched
arc per cut time, resampled by both sides). Only the pairing needed a decision procedure: *which
two boundary points one section arc joins is not readable off their positions.* It is decided by
MARCHING, with the discriminator being that a march aimed at the wrong partner can still arrive —
by crawling along the domain boundary once its own arc has run out — so arriving is not enough, and
the interior of the march has to be checked for staying off the boundary. On the fixture below the
wrong-partner march does exactly that: it walks its own arc to the wall, crawls the forbidden
stretch, and lands on the intended target from the far side.

**Refining the cut time: search σ, not dt/dσ.** The extremum is refined by golden section on the
branch's own sample parameter, with each probe's t coming from 1D Newton on `f = 0` at that uv
(`f_t` does not vanish at a boundary tangency — the tangency is in uv, not in t — so that Newton is
well conditioned exactly where it is needed). Searching σ rather than the derivative is what buys
the precision: t is quadratic in σ near a smooth extremum, so a σ converged to 1e-9 pins t to
machine zero, and no second derivative of a piecewise-linear polyline is ever needed. Measured
against the fixture's closed form: **the refined merge time is 0.4999999999999995 against an exact
0.5.**

**Two bugs the first live run found, both in the same place.** The fixture is sampled symmetrically
about its own turn, which puts **two samples at exactly the same t** — and (a) a turn test reading
the two differences flanking a single sample never sees opposite signs across a zero difference, so
the one extremum the fixture has was missed entirely; (b) the monotonicity guard compared each step
against the side's *overall* rise, which for a side running from one t back to the same t is zero,
so a non-monotone side passed as monotone. Turns are now read off the SIGN RUNS of the differences
(plateaus skipped, the bracket spanning the last rising sample to the first falling one) and
monotonicity is "never both directions". The symmetric layout is the natural one for a fixture, and
it is the single case the obvious tests both miss.

**Sharing, and what "share the same sample arrays" is worth.** Three mechanisms, each verified by
exact equality rather than by tolerance:
- the two sides of a cut carry **the same appended sample** — one array element, so the corner
  where two strips meet on the face boundary is one number;
- `anchorUvAtStation` now returns a branch's own END SAMPLE when asked at that branch's end time,
  with no interpolation and no polish — without which two sides sharing a cut sample produce
  anchors that differ in the last bits (interpolating to fraction 1 and polishing along a different
  segment is not the identity);
- the seam arc is marched **once**, on whichever side of the cut has fewer arcs, and the strips on
  the other side resample that same polyline. Live: **the two legs' clipped rows cover 35 of the
  shared arc's 35 interior vertices, each vertex bit-identical and the runs consecutive** — a
  partition, not an agreement.
Also required, and easy to miss: `buildFitStations` now ASSIGNS its two endpoint stations rather
than computing them, because `tStart + (tEnd - tStart)` is not `tEnd` for every pair of doubles and
a seam station that does not compare *equal* cannot trigger the shared-sample path at all.

**The fixture, chosen so every answer is closed form.** S = (u, v, 0.1(u²/2 + 2u(v−½)²)) on the unit
square under velocity (1, 0, 0.1w(t)), w = 1.2 − 0.4t. The envelope function is exactly
f = 0.1(w(t) − u − 2(v−½)²), so every section is the parabola u = w(t) − 2(v−½)², peaking at u = w
on the v = ½ meridian. While w > 1 the peak is outside the domain and the u = 1 wall cuts the
section in TWO; at w = 1 the section is tangent to the wall; below it there is one arc. w(½) = 1, so
the component has two cap arcs at t = 0 and one at t = 1, and the wall branch's t(v) = ½ − 5(v−½)²
turns over at v = ½. Contact curve, membership residual, and the arc at any station are all
closed form — nothing the module under test supplies.

**Live results.**
- *Decomposition* (27 checks): alternations **6** (4 endpoints at t₀, 2 at t₁), 4 sides from 3
  branches, 1 interior cut time at 0.4999999999999995, 2 bands, **3 strips** (two legs + one
  trunk), 1 seam whose merged side is the single-arc band, marched to a 37-point arc. Fixture
  branch samples on `f = 0` to 6.9e-17. The rectangle CONTROL — the step-6 curved fixture, whose
  two branches are monotone — comes out 4 alternations, 2 sides, **1 strip, 0 seams**, no merge
  marks, no prescribed sections: the >4 test stays silent where it should.
- *Fits* (11 checks): leg **8×8, deviation 5.761e-4** (q 2.90e-5, t 5.761e-4), knot removal 4.02e-4,
  certified 9.78e-4, section residual 6.2e-14; trunk **6×16, deviation 1.299e-4** (q 1.299e-4, t
  **1.24e-8**), removal 4.80e-5, certified 1.78e-4, section residual 9.6e-16. Closed-form envelope
  membership **2.74e-5** and **1.48e-5**. Orientation on both: λ one-signed at fold margin 0.4286,
  **64/64 and 96/96** difference-normal agreement, neither q reversed — the merge does not confuse
  the orientation pass.
- *The seam, measured*: each patch's seam edge against the EXACT contact arc at the merge time —
  leg **1.73e-4**, trunk **1.30e-4**, both inside their own certified bounds. Two curves within d
  of one arc are within 2d of each other, so that is the seam statement without fitting the
  neighbour, and it avoids the trap of measuring against a marched polyline (whose chordal sagitta
  at this step size is ~9e-4, an order above what is being measured).

**The negative result, which is the useful part.** A merging strip's (q, t) chart has a
**square-root corner**: the merging arc's endpoint runs along the boundary like sqrt(t* − t). The
obvious remedy is to cluster stations quadratically toward the merge — uniform in sqrt(t* − t) — and
it does exactly what it claims for the q = 1 boundary curve *in isolation*: an independent
recomputation gives 28× better there at 8 stations (uniform 1.8e-3 / graded 6.4e-5, and 4.0e-4 /
1.0e-5 at 15). **It does not improve the strip's certified deviation at all**, because that is
dominated by held-out samples off that curve. Matched grids, same recomputation: 6×6 graded 1.4e-3
against uniform 8.5e-4; 8×8 graded 5.1e-4 against uniform 5.5e-4; 12×8 graded 1.6e-4 against
uniform 3.0e-4 — inconsistent in both directions. So the decomposition MARKS merge ends
(`mergeAtStart` / `mergeAtEnd`) and leaves the remedy to the caller; `buildFitStations` keeps the
clustering behind `gradeMergeEnds`, defaulted **off**, its station layout verified on its own (ends
exact, spacing closing up, uniform in sqrt to 1e-16). **Open:** what does fix a merging strip — a
reparameterization of the chart near the corner, or accepting that this one strip class certifies
an order coarser than its neighbours. Nothing downstream depends on the answer; §9's knit reads the
shared arrays, not the tolerance.

**And the practical consequence of that corner: do not point the refinement loop at a merging
strip.** Its deviation does not fall cleanly with grid size (uniform 6×6 8.5e-4, 8×8 5.5e-4, 12×8
3.0e-4, 15×8 2.1e-4, the graded series non-monotone), so a refine-to-tolerance loop given a target
the chart cannot reach doubles its way into the interpreter's step limit — which is exactly what
the first two live attempts at the fit test did, at 24×24. The self test fits FIXED grids and
reports the deviation, with scale-free assertions around it: whatever accuracy the fit reaches, the
patch must be the envelope to that accuracy and its seam edge must be on the shared arc to that
accuracy. The merged side has no such problem and converges normally (trunk 6×12 5.4e-4, 6×16
1.3e-4, 6×23 1.9e-5).

**Method note worth keeping.** The two step-budget blowups were paid for twice before the numbers
came from a **Python recomputation of the whole certification** — grid build, NURBS Book A9.1
interpolation with averaged knots, and the same three held-out sample families — rather than from
1D estimates of one boundary curve. It agrees with the live kernel to a few percent (leg 8×8
deviation 5.55e-4 simulated against 5.761e-4 live; trunk 6×16 1.305e-4 against 1.2989e-4; leg q
2.86e-5 against 2.90e-5), which is close enough to size grids and set targets offline for free. Its
own trap: a "robust" global-scan projector *replaced* a correctly seeded local one and made every
number worse, because a 101×101 parameter scan on a 1 m patch cannot resolve a distance below
~5e-3. The seeded projector was right; the check that settled it was that the fitted surface
reproduces its own data to 4.5e-16.


---

## 8. Sharp features — `swSharpFeatures.fs`

Convex edges only (§3). The funnel of a sharp edge lives in (s,t): a point is in it iff the
two adjacent faces' strip functions **differ in sign** (`g_left · g_right ≤ 0` — two dot
products, the papers' §4.1 criterion), both already computed (§6.2). Lateral boundaries ARE
the shared `g_side = 0` sample arrays → exact stitching with the neighboring smooth-face
patches by construction.

Geometry, two routes gated by certification:

- **The kernel-sweep route (v1 default):** `opSweep{profiles: the edge, path: the path}` — a
  kernel-exact edge-sweep sheet *if* kernel frames ≡ h, which the motion module guarantees by
  construction (§4.2). Gate anyway: `evPointsDeviation` of the sheet against ~50 analytic
  samples `Φᴱ(s,t) = A(t)·e(s) + b(t)`; on failure switch to the closed-form transport route.
  Trim in place (imprint the shared boundary wires, `opDeleteFace{leaveOpen}`).
- **The closed-form transport route (motion-agnostic, exact):** `transportCurve(m, curve3d)` —
  the closed-form tensor-product transport of §2.1, emitted via `opCreateBSplineSurface`;
  trims via the §7.1 fallback composite. Becomes the default when twist arrives (kernel twist
  convention need not match ours).

Sharp vertices: sub-intervals of I where any two `s_i(t)` differ in sign (endpoints by 1D
Newton); each interval's envelope edge is `trajectoryCurveOf(m, Z)` restricted — exact.
Which faces meet at such an edge is pure sign-pattern combinatorics (papers §6.2). At most
3 faces per vertex in v1.

Topology assembly (`swSweepTopology.fs`): the papers' loop walk — three cases for smooth-face
loops (cap-adjacent / vertex-in-cap / pull back through φ and use *input* adjacency), and
coordinate-matched iso-curve chaining for sharp-edge faces. All adjacency questions answered
on the input B-rep (Theorem 11/17): O(1) local lookups, never a geometric search.

---

## 9. Caps, knit, assembly — `swSweepEmit.fs`

1. **Caps:** tool copies at t₀/t₁ via `opPattern` with `motionSnapshotTransform` (raw kernel
   frames — exactly rigid). Contact wires: `opCreateBSplineCurve` through the t₀/t₁ boundary
   rows of the adjacent grazing fits (same points as the patch edges → coincident to fit
   tolerance). Imprint with `opSplitFace{faceTargets, edgeTools: wires}` — projection-based
   (`NORMAL_TO_TARGET`; probe 3 verified landing from 100 mm away, so wire proximity is a
   non-issue and only projection-direction validity matters) — and **never** use the
   grazing sheets as bodyTools: envelope and cap are *tangent* along contact curves, and
   tangent surface-surface intersection is the kernel's worst case (EDIT_SURFACE §2.3.1's
   "edges, not coincident bodies" lesson, with force). Classify kept faces by the sign of f at
   an interior sample (pure math from extraction records); `opDeleteFace{leaveOpen: true}` the
   rest.
2. **Knit:** `opBoolean` UNION (`allowSheets`) across grazing patches + sharp-edge sheets +
   caps, closed with `joinSurfaceBodiesWithAutoMatching(..., makeSolid = true, reconstructOp)`.
   Gap policy: seams coincide by shared sample arrays (§2.3); target ≤ 1e-6 m, never rely on
   knit tolerance to absorb geometry error; minimum patch edge 1e-5 m (merge slivers into
   neighbors during strip decomposition).
3. **Failure degradation:** on knit failure, deliver the certified open sheet set plus a
   warning naming the offending seam — never a silently wrong solid.

Id discipline: `getUnstableIncrementingId` for op ids in loops (a thrown op still registers
its id; hand-named ids in loops mask the real error).

---

## 10. Non-simple sweeps: v1 detection, future resolution

**Detectors (always on, cheap):**

1. *Cusp / local self-intersection screen:* the envelope is regular only while the motion's
   turning radius exceeds the tool's local normal-curvature radius against the contact
   direction. Screen from extraction-time `evFaceCurvatures` + motion data; flag stations
   where the margin flips sign. (The exact invariant μ is arXiv:1305.7351's; this screen is a
   conservative stand-in and documented as such.) **The orientation pass now supplies a
   sharper, sample-exact version of this for free** (§6.6): `λ = 0` is precisely where the
   contact point stops moving, so a funnel component whose λ changes sign folds. It is a
   certificate on the samples actually fitted, not a curvature estimate, and it is the reason
   the orientation pass is a gate on emission rather than a bookkeeping step. **It has fired live
   (2026-08-23):** the bump fixture behind the step-6 island fit folds through its middle band,
   and the certificate reports it while the deviation check passes the same patch — which is the
   whole argument for having it. **And one whole component class always trips it:** an island
   carrying BOTH its t-extremes folds for structural reasons (§7.4.1), whatever the tool and
   whatever the motion, so `SWEEP_ISLAND_UNSUPPORTED` on a two-pole island is a theorem rather
   than a conservative screen — and the kernel independently refuses such a patch.
2. *Contact topology events:* loop birth/death/merge per station, read off the certified
   census's component set (§6.8) rather than recomputed.
3. *Global collision screen:* spatial hash (cell ≈ 5·ε_fit) over all contact-curve samples;
   close pairs from far-apart t flag global self-intersection.
4. *The §6.4 degeneracy audit* — sliding (per-block range test, exact on analytic faces),
   `SWEEP_FUNNEL_TANGENT_TO_SLICE` per marched section, and `SWEEP_EDGE_SWEEP_SINGULARITY` per
   co-edge (§6.9). The first two are *reports the caller acts on* rather than outright
   rejections: a sliding face may be non-contributing, and a section tangency splits the
   section instead of failing the sweep. The edge-sweep singularity rejects in v1.

Any hit → `regenError` with the t-range and, where possible, the offending input entities
highlighted. v1 never emits a self-intersecting body.

**Future work — event decomposition:** split I at event times into K ≤ 32 intervals whose
sub-sweeps are simple; build each with this pipeline; **imprint-then-share** the interval caps
(contact wires imprinted on shared `opPattern` instances so adjacent shells meet along
identical existing edges — the kernel never solves a tangent intersection numerically); close
each with `opEnclose`; finish with ONE n-ary `opBoolean` UNION (V = ∪ Vᵢ exactly; the union
performs all global trimming). Rejected alternative: cellular decomposition + witness-point
classification (`decomposeIntoCells.fs` is exponential in its touching-graph cliques and
solids-only; witness raycasts cost thousands of ev-calls the union approach never pays).

**Future work — true trimming:** μ-seeded surface-surface intersection via
`opIntersectFaces` on flagged patch pairs, with the two regular branches of a cusp region
fitted separately (extended ~2 spans past the singular curve) and mutually trimmed by the
`mutualTrim.fs` composite. Gated on working through arXiv:1305.7351.

---

## 11. Performance budget & instrumentation

Reference load: 10-face tool, ~24 edges, 100 motion stations, ~40 fitting stations, ~12
funnel components.

| Stage | Kernel calls | Pure-math evaluations |
| --- | --- | --- |
| Extraction | ~60–110 ev (batched) | — |
| Motion | ~130 ev + 1 aborted helper sweep | drift ~200 |
| Vertices + co-edges | 0 | ~4K curve/normal-spline evals |
| Funnel grid | 0 | measured §6.8: 9537 coefficient node evals + 3610 interval screens |
| Sections | 0 | **~31K order-2 surface evals — the hot loop** |
| Fitting + certification | ≤ 12 batched evPointsDeviation | ~7K midpoint checks + 12 solves |
| Emission | ~40–70 ops | — |

MEASURED (probe 2, 2026-08-21): **~1.1 ms flat per pointwise surface-normal evaluation** on
the module's current rational+units path (20K evals → 21.82 s, 40K → 43.97 s; zero fixed
overhead), on a degree-1×3 surface. That kills the original blind-sampling plan (~31K order-2
evals → 50–70 s hot loop) and is why §6.0 makes the solver coefficient-first. Revised budget
shape: analytic faces ≈ free (closed forms); freeform faces pay one coefficient-net assembly
per Bézier patch × t-span plus subdivision only on live blocks; pointwise work shrinks to
root polish + fit grids (order hundreds per live patch) on a lean unit-stripped non-rational
evaluator (expected several-fold under 1.1 ms — re-measure with the throughput probe once
built; see the open question on the lean evaluator, §15). Rules:
certification loop owns every sample count; batched plural ev-forms only; per-stage timing
`println`s from day one; over-budget runs degrade to coarser tolerance with a warning, never
a timeout.

**The bar (owner, 2026-08-23).** Most standard-library features regen in tens of milliseconds.
Sitting through 20+ seconds for a basic solid sweep is not acceptable, and the end target is
**sub-second**; a dedicated optimization pass across the whole custom-feature stack is a planned
phase, not a hope. Development so far has deliberately taken the hardest cases first and has set
no performance discipline, so the numbers in this section describe what the current interpreted
math happens to cost — they are not a budget anyone agreed to.

Two consequences, both binding:

1. *The arithmetic does not currently close.* At the measured 1.1 ms, a 5 s ceiling buys about
   4,500 pointwise evaluations for the entire feature. The table above assumes roughly 45,000.
   Either the evaluator gets an order of magnitude faster or the evaluation count drops by one —
   guessing which is what §12.3 item 7 exists to stop.
2. *No unmeasured number goes in this section.* Every figure here must come from Onshape's own
   compute-time readout in the UI. MCP `test_feature` wall-clock measures a network round trip and
   is not a substitute; a figure derived that way is not a measurement and must not be recorded as
   one. Where a number is expected rather than observed, it is labelled as an expectation — as the
   lean-evaluator multiplier above is.

**Scheduling (owner, 2026-08-23).** This work is deferred to §12.3 tier 4 item 9, not cancelled.
The reasoning: the cost bites on worst cases rather than on the fixtures the queue is built from,
and the rewrite is cheaper after the tier-4 consolidation has collapsed the solvers into fewer
functions — the Newton–Raphson solvers in particular have not had the Toeplitz treatment
`bernsteinPolynomialUtils.fs` already got. The arithmetic above stands unchanged in the meantime,
which is the point of leaving it written down. **One trip-wire overrides the schedule:** a live
document taking ~100 s to rebuild trivial geometry stops the queue and starts item 9.

*Suspicion worth testing first (§12.3 tier 4 item 9a).* The 1.1 ms was measured on a **degree 1×3**
surface — eight basis products per call. Eight multiply-adds cannot cost a millisecond, so most of
that figure is almost certainly fixed per-call overhead: span search, basis-derivative table
construction, `makeArray`, map field reads. If so, unit-stripping attacks the wrong term and
**batching** is the lever — evaluate a whole (q,t) grid per call, hoist the span search and basis
tables per row and column, and phrase the blend as one `@matrixMultiply`. Sections, fit grids and
certification are all already grid-shaped callers.

---

## 12. Module layout and build order

```
custom-features/
  solidSweep.fs           — NOT BUILT. Feature UI, orchestration, diagnostics mode (debug-draw contact
                            curves / funnel samples), per-stage timings
  swMotionSpline.fs       — §4   (imports splineRefinementUtils)
  swEnvelopeMath.fs       — §6.1–6.2, pure
  swAnalyticContact.fs    — §6.5 closed-form contact for the five analytic classes, pure,
                            depends on nothing but std (+ swAnalyticContactTester.fs)
  swFunnelSolver.fs       — §6.3–6.4, the §6.7 masks and the §6.8 certified census, pure
  swOrientation.fs        — §6.6 orientation of every envelope entity class + the λ-sign fold
                            certificate, pure (+ swOrientationTester.fs)
  swDegeneracy.fs         — §6.9 the §6.4 detectors the funnel solver does not carry:
                            SWEEP_FUNNEL_TANGENT_TO_SLICE with the section split, and
                            SWEEP_EDGE_SWEEP_SINGULARITY with its Gauss-Newton refinement.
                            Pure; imports swEnvelopeMath + swFunnelSolver (+
                            swDegeneracyTester.fs)
  swSweepTopology.fs      — NOT BUILT. §8 topology walk, pure combinatorics
  swEnvelopeFit.fs        — §7, pure library + its self tests and live tests. Also §7.4.1's
                            ISLAND PATCH emission, which lives here rather than in swSweepEmit
                            because it hands the kernel the fit's own net and swEnvelopeFit
                            already imports swSweepEmit; §9's caps/knit/assembly stays there
  swSharpFeatures.fs      — NOT BUILT. §8 sharp geometry + trim domains
  swSweepEmit.fs          — §5 extraction, §9 emission/knit/certification
  swSweepProbes.fs        — the live probes (§13, complete)
  swTrimLoopTester.fs     — §6.7 the LIVE trim-mask test: extraction to census on one real
                            trimmed face (imports swSweepEmit + swFunnelSolver)
  bernsteinPolynomialUtils.fs — §6.0 Bernstein coefficient arithmetic (pure, dependency-free,
                            standalone so the published splineRefinementUtils never needs a
                            republish while the sweep is being refined)
  swTestHarness.fs        — §12.2 shared test scaffolding: the verdict reporter, the check
                            tally, and every fixture used by more than one module. Imported by
                            all nine modules that carry a test feature; imports only std, so it
                            sits under everything and closes no cycle.
  + one *Tester.fs per pure module, + solidSweepLiveTester.fs (NOT BUILT)
```

Build order (each step live-validated before the next):

1. Probes + this spec — **done** (all six probes resolved 2026-08-21, §13).
2. Bernstein polynomial utilities + tester — **done** (all 84 checks green live, 2026-08-21;
   `custom-features/bernsteinPolynomialUtils.fs`). The coefficient arithmetic everything in
   §6.0 sits on.
3. Motion module + tester — **done** (live on line/arc/spline, 2026-08-21;
   `custom-features/swMotionSpline.fs`. Worst case — free spline, 0.218 m: 0.30 s build,
   150 stations, 2 predictive rungs, drift 5.85e-7 vs the 1e-6 tolerance. Analytic paths,
   the common case, resolve far coarser. Full findings and the A/B measurement in §4).
4. Extraction records + the UV calibration helper. **DONE (2026-08-22, live self-test PASS on
   cylinder / bicubic patch / elliptical extrude / box):** `swSweepEmit.fs` — ToolFaceRecords
   (classification, periodicity, trim loops, unit-stripped splines, per-face UvCalibration
   with held-out assert), damped multi-seed point inversion (machine precision on clamped and
   periodic surfaces), CoEdgeRecords (curve class, convexity on two-sided edges only, kernel-
   probed left/right sides, one-sided normal sample arrays, pcurves by seeded inversion
   marching — measured 1e-15..6e-14 m on laminar spline and closed periodic edges), and
   VertexRecords (adjacency indices + deduplicated cone normals assembled from co-edge end
   samples at zero extra ev cost). Design refinements against the §5 sketch: sideNormalSplines
   became sideNormal SAMPLE arrays (the §2.3 shared-array doctrine — fitting belongs to strip
   assembly); sides are decided by the kernel's own `usingFaceOrientation` tangent probe. The
   **periodic-face seam strategy moves to step 5**, where the funnel band's actual seam
   requirements are concrete.
5. Envelope math + funnel solver + testers, end-to-end on analytic fixtures. The ANALYTIC
   CLOSED FORMS of §6.0 strategy 1 were specified here and built later, 2026-08-23: see §6.5
   (live PASS, 18 checks, all five classes at machine precision). Includes the
   periodic-face seam strategy deferred from step 4 (rewindow via `splineRefinementUtils` so
   the funnel band avoids the seam — a rewindow invalidates the face's affine calibration,
   falling back to 3D inversion crossings — else pre-split at an isocurve).
   **Envelope function layer DONE (2026-08-22, live self-test PASS first try):**
   `swEnvelopeMath.fs` — pure. The §1.1 function FACTORS as
   f = Σᵢⱼ (NᵢSⱼ)(u,v)·Mᵢⱼ(t) + Σᵢ Nᵢ(u,v)·Tᵢ(t) with Mᵢⱼ = ⟨Cᵢ, C′ⱼ⟩, Tᵢ = ⟨Cᵢ, b′⟩ (Cᵢ =
   A's columns): twelve uv coefficient grids per Bézier patch built ONCE (motion-independent),
   twelve cheap t-polynomials per motion span, and three screening levels — a near-free
   interval screen on precomputed ranges, the exact convex-hull screen on materialized blocks,
   and materialization itself only where screens fail. Measured live: factored vs independent
   pointwise path agrees to 5.4e-19 relative; f_t analytic vs central difference 7.1e-16;
   pure +Z translation killed 4/4 blocks at the LOOSE screen (zero materialization); tilted
   translation isolated a 2-live/2-dead grazing set.
   **OWNER PROFILE (2026-08-22, UI profiler — the only per-function timing source):** the
   original eager build measured **14.84 s** total self-test, **13.0 s in
   buildEnvelopePatchFactors** (48 patch builds ≈ 270 ms each), `multiplyBernsteinGrids`
   second-hottest at 468 calls — the harness round-trip wall clock had hidden all of it.
   REDESIGNED in response: (1) patch factors now store only S and N grids plus ranges; the
   loose screen runs on range(Nᵢ)⊗range(Sⱼ)⊗range(Mᵢⱼ) interval products — still a certificate,
   no grid arithmetic; (2) the nine Nᵢ·Sⱼ product grids moved to `buildEnvelopePatchProducts`,
   built lazily only for patches surviving the screen; (3) both Bernstein multiplies now hoist
   binomial rows and pre-weight inputs once, leaving bare multiply-add inner loops.
   **OWNER RE-PROFILE (2026-08-22): 14.84 s → 5.77 s, identical PASS at 5.4e-19.** Rows:
   buildEnvelopePatchFactors 13.0 s → 788 ms/12 calls; buildEnvelopePatchProducts 1.85 s/48;
   multiplyBernsteinGrids 1.87 s/549. New hotspot: materializeEnvelopeBlock 3.40 s/192 —
   generic scale-then-add chains cost an intermediate grid allocation, deep nested writes,
   and per-iteration size() per term (~350K low-level calls). FIXED (awaiting re-profile):
   `accumulateScaledBernsteinGrids` — one fused pass per t-coefficient over twelve flattened
   term grids with hoisted sizes and single-level row writes; scaleBernsteinGrid and
   addBernsteinGrids rewritten to local-row writes.
   **THIRD OWNER PROFILE (2026-08-22): 5.77 s → 3.77 s** (materialize 3.40 s → 960 ms, adds
   1.88 s → 95 ms); the fused kernel's own interpreted madds (~1 s / 1230 calls) became the
   floor. FINAL FORM (awaiting re-profile): materialization is now ONE NATIVE
   `@matrixMultiply` per block — patch products carry a flattened 12×(rows·cols) term matrix
   built once; each block multiplies its (t-coefficients × 12) weight matrix against it and
   slices rows back via native `subArray`. Doctrine (verified in the mirror): std Vector
   operators are themselves interpreted per-element loops — NOT a speed primitive; native
   speed lives only in @-builtins (`@matrixMultiply`, `@matrixSum`/`@matrixDifference`,
   `@matrixCwiseProduct`, `@subArray`, `@concatenateArrays`, `@resize`), so hot bulk kernels
   must be phrased as matrix products, with fused single-pass loops (hoisted sizes,
   local-row writes, no deep nested writes) as the fallback where no matmul shape exists.
   Applied MODULE-WIDE to `bernsteinPolynomialUtils` (fourth pass, same day): grid multiply =
   binomial cwise masks + per-kernel-row Toeplitz matmuls + native shifted sums; elevation =
   closed-form E·G·Eᵀ operators; differentiation = banded matrix products;
   add/subtract/scale = native matrix ops. Remaining interpreted loops are operator-matrix
   assembly (row-local, O(n)), range scans (pure reads), and short 1D t-polynomial
   arithmetic — all within doctrine. Note the 192 blind materializations are a workload
   stressor — the real solver materializes only screen survivors, and the planned
   funnel-solver subdivision runs FACTORED (split S/N grids and re-screen ranges) so most
   live-block work never materializes f at all.
   **FINAL PROFILE (2026-08-22): 1.88 s, PASS — 14.84 s → 5.77 → 3.77 → 1.88 across three
   doctrine passes, each gated by the 1e-19 consistency test. Performance chase closed;
   the envelope function layer is done.** Carry-forward note for the solver:
   `subdivideBernsteinGridU/V` still carried the original per-cell loops — converted to
   native split-operator matrix products at the start of the funnel-solver work (2026-08-22,
   93 tester checks green; see the funnel solver entry below).
   CONSTRAINT adopted: the coefficient path requires NON-RATIONAL patches (rational input
   would push bicubic blocks from 9×9×6 to ~19×19×6 via the homogeneous-numerator route);
   extraction therefore supplies freeform faces with `forceNonRational` (swSweepEmit change
   pending), and analytic faces never enter this path. Pointwise evaluators
   (`evaluateEnvelopePointwise`, `evaluateEnvelopeTimeDerivativePointwise`,
   `evaluateContactFunctionAtPoint`, `buildStripFunctionGrid`) are the polish/certification
   path and run on the module's own spline evaluators end to end.
   **Funnel solver DONE (2026-08-22, all four harness runs PASS first try):**
   `swFunnelSolver.fs` — pure, three selection-free self-test features (split so each MCP
   payload stays inside size discipline). Prerequisite landed first:
   `subdivideBernsteinGridU/V` converted to native split-operator matrix products (the de
   Casteljau split is a linear operator; left[k][j] = C(k,j)p^j(1-p)^(k-j),
   right[k][j] = C(n-k,j-k)p^(j-k)(1-p)^(n-j)), validated by the tester's 93 checks including
   new v-direction parity anchors. The layers, with live results:
   - *Sliding audit (§6.4)*: per-block screen → materialize → whole-tensor range test; live
     blocks keep their coefficient tensors for downstream reuse. Plane under in-plane
     translation slides (f materializes to exactly 0); normal translation is screen-dead.
   - *Factored cell isolation*: recursive (u, v, t) subdivision splitting the S/N grids
     (native) and the twelve t-polynomials per cell, re-screened with range products at every
     node — f is never materialized during the descent. Island fixture
     (z_u = 0.8·u(1-u)v(1-v) peaking at 0.05, w = (1, 0, wz(t)) with wz dipping below the
     peak exactly on t ∈ (0.3, 0.7)): 88 live cells from 367 screens at 1/8 resolution,
     covering all four exact contact anchors, zero cells outside the t-window.
   - *Island refinement*: 3-variable Newton on (f, f_u, f_v) = 0 with EXACT partials from
     differentiated coefficient nets of the materialized block — birth/death recovered at
     (0.5, 0.5, 0.3)/(0.5, 0.5, 0.7) to 1.1e-16 coordinate error, independent pointwise
     |f| = 1.4e-17.
   - *Vertex layer*: station-grid brackets + bisection-safeguarded Newton on g(t) (analytic
     g_t = ⟨A'n, A'p+b'⟩ + ⟨An, A''p+b''⟩, verified vs central differences to 1.9e-12);
     parabolic-velocity fixture roots at 0.3/0.7 to 1e-16. Sharp-vertex contact intervals
     from cone-normal sign patterns recover (0.3, 0.7) exactly.
   - *Co-edge strip marching*: per-column roots on the SHARED sample arrays chained into
     branches by linear prediction (first link 4×station spacing, then slope-aware windows —
     the tight first-link window was the one pre-run bug caught in review). Swinging-velocity
     circle fixture: exactly the analytic 3 branches (5/9/5 columns), roots vs
     t = (1+0.8·tan 2πs)/2 to 7e-13.
   - *Envelope gradient + section layer*: order-2 pointwise gradient (all four components vs
     central differences of the independent pointwise path, 7.3e-12 worst, rotation terms
     included), predictor-corrector section marching, 3D-arc-length resampling at fixed q
     fractions with re-Newton polish, rigid lift Φ = A·S + b. Slanted-line fixture
     (f = 0.24 − 0.3u − 0.05v): marched |f| ≤ 2.8e-16, resample residual 2.6e-16, lift vs
     hand value 3.5e-18.
   - *Funnel census*: coarse value grid filled block-wise (dead blocks take their certified
     screen sign, live blocks evaluate their materialized tensors — never pointwise splines),
     even-odd trim masking, 6-connected flood fill with explicit u-seam wrap. Island fixture:
     one component, isIsland true. Square trim mask [0.35, 0.65]²: the mid-life loop exits
     the valid square, splitting the shell into exactly 2 trim-touching caps as predicted
     node-by-node in design. Seam fixture (z_u = 0.8(u−0.5)²v(1−v), one lobe against each u
     edge): 2 components open, 1 wrapped with crossesUSeam — the deferred periodic-seam
     strategy's detection half; the rewindow-vs-presplit decision (calibration invalidated →
     3D inversion crossings) lives at fit assembly, keyed off that flag.
   Harness lesson banked: a DELIBERATELY provoked caught throw (the tester's
   identically-zero guard check) surfaces as an INFO notice and hides the console; the check
   is now gated behind a default-false parameter so harness payloads stay notice-clean.
   Payload discipline: four runs — bernstein+tester (49 KB), pointwise (37 KB, only the
   pointwise envelope functions inlined), factored and census (66/60 KB, comment-stripped by
   mechanical line filter, bases byte-identical) — assembled by sed from the repo files.
6. Fitting module + the first certified live envelope patch. **DONE (2026-08-23, five harness
   runs): `custom-features/swEnvelopeFit.fs` — stations with exact event merging, fixed and
   branch anchors polished along the boundary, (q, t) grid assembly, a pole-capable and
   periodic-capable grid interpolation, direction-resolved certification against held-out
   Newton-converged samples, the doubling refinement loop, knot cleanup, and live emission.
   Full findings and measured numbers in §7.3; the island periodic-q correction and the
   whole-island emission rejection in §7.4.** Four self-test features plus a live test (split
   because the interpreter's per-regeneration step budget cannot absorb more than about two
   certified fits in one feature — a real constraint on how these tests are packaged, hit twice).
   §7.4.1 adds two more: `sweepIslandCapLiveTest` (the clipped cap, its emission and both split
   halves) and `sweepIslandSplitLiveTest` (the two-pole fold theorem and the v1 refusal).
7. Smooth-only watertight solid (e.g. an ellipsoid along a spline), volume/deviation checks.
   Carried the two open items from §7.4: island caps by t-extreme split, and the periodic-seam
   rewindow decision the funnel census flags with `crossesUSeam`.
   **7a extraction rationality contract + pole detection — DONE (live PASS, §7.5/§7.6/§7.8;
   `forceNonRational` retracted in §7.8 after it broke the step-4 self test):**
   `extractToolFaceRecords` re-declares uniform-weight nets non-rational and re-reads genuinely
   rational ones through `evApproximateBSplineSurface{forceNonRational}`; every spline-bearing
   record now carries `degenerate` (`degenerateSplineBoundaries`), the collapsed control-net
   boundaries whose vanishing normal makes f identically zero.
   **7b tube component — DONE (2026-08-23, live PASS, numbers and the fixture in §7.5):**
   `fitTubeComponent` plus the wrapping section march, meridian seeding, and wrap-aware
   resampling in `swEnvelopeFit.fs`. **The periodic-seam question is settled — neither rewindow
   nor pre-split: the march carries u unwrapped and the (q, t) rectangle absorbs the seam.**
   **7c lateral patch on the real ellipsoid — DONE (live PASS, §7.7): a v-periodic tube net IS
   accepted by `opCreateBSplineSurface` (one face), fit deviation 2.77e-6 with t exact at 2.5e-16,
   kernel-certified at 8.0e-7 m.** Caps, knit, and the volume check are what remain of step 7.
   **7d orientation — DONE (2026-08-23, three harness runs, live PASS; §6.6):** `swOrientation.fs`
   plus `swOrientationTester.fs` (two selection-free features), and the pass is plumbed into
   all three fit shapes in `swEnvelopeFit.fs` — every grid is certified and its q direction
   reversed when the net would face inward, held-out certification rows reversed in step. This
   was §12.1's audit item 1, the one that gates §9 harder than the caps plumbing does. It also
   lands the λ-sign fold certificate that sharpens §10 detector 1. Live: both tester features PASS
   (13 + 10 checks) and the step-6 fit self test reproduces its recorded bounds to the last digit
   (8.723887064426035e-9 ruled, 4.533571042e-7 curved) with the anchor-swapped rerun proving the
   q-reversal path. **The live run also caught a real bug** — a constant-velocity translation makes
   `f_t` identically zero, so its sign was rounding noise and wrongly counted as degeneracy; `f_t`
   now gets a relative floor and a vanishing `f_t` reports `contactStationary` (§7.7's profile
   sweep) instead of condemning a perfectly oriented patch. **Closed out with three more runs:** a
   third tester feature (closed rows, collapsed poles, forced closed reversal, and the fold
   certificate FIRING), the island fit (48/48 pole rows skipped, fold reported), and the tube fit
   (216/216 agreement, unanimous across the unwrapped-u seam, `qReversed` true so the closed-row
   reversal ran in production, deviation 1.988015957e-5 against the recorded 1.99e-5). Every
   function and both branches of every flip are now live. **Finding: the bump fixture used by the
   island fit is a locally self-intersecting sweep through its middle band** — see §6.6; §7.4's
   island emission needs a simple fixture to develop against.
   **7e island emission — DONE (2026-08-23, live PASS 21 of 21 and 9 of 9; §7.4.1):** the fixture
   asked for above cannot exist — every two-pole island folds — so `fitIslandComponent` gained
   `tStart`/`tEnd` and v1 emits clipped islands. A one-pole cap is ONE kernel face at fold margin
   0.570, certified at 1.43e-4 against fresh envelope loops; the split shape emits as two faces
   sharing a control row exactly (0 m), through `interpolateFitGrid`'s new v-parameter override;
   a folded island is reported by `emitIslandPatches` and refused by the kernel independently.
   **7f caps, knit, and the volume check — NOT BUILT.** Across every sweep module the only kernel
   emission calls are `opCreateBSplineSurface` (single patches and island caps); no solid body has
   been produced. §12.3 item 7.
8. Sharp features + the topology walk — **NOT STARTED.** `swSharpFeatures.fs` and
   `swSweepTopology.fs` do not exist. §12.3 item 9.
9. Feature UI, detectors, the live tester, publish chain — **NOT STARTED.** `solidSweep.fs` and
   `solidSweepLiveTester.fs` do not exist. §12.3 item 10.
10. Trim the test scaffolding down to the core utilities plus the feature — **NOT STARTED**, and
    deliberately last. §12.3 item 11.

After v1: twist / lock modes / closed paths, then the trimming work of §10.

### 12.1 Foundation audit of steps 5–7 (2026-08-23)

Every §6/§7 construct the papers require, checked against what exists. Owner's directive: fill
these in before touching step 8 or the feature UI — no shortcuts toward the end goal.

Progress on that directive, in the order the items were taken:

- **orientation** (§6.6, 2026-08-23, live PASS over six runs) — moved into the table above.
- **census sign tolerance from the block's own range** (§6.7a, 2026-08-23, live PASS, both
  affected self tests) — the old item 5, and the smallest item in Tier 1. It also turned up a
  Vector-typing bug that had been silently failing the census self test since the previous commit,
  which is the argument for re-running a test after every change to what it covers rather than
  trusting the last recorded verdict.
- **§6.4 detectors 2 and 3** (§6.9, 2026-08-23, live PASS first try, both features, 42 checks) —
  Tier 1 item 1. The audit item read as two sign scans; the work turned out to be finding that
  detector 2 needs a THIRD verdict (a constant-velocity translation has `f_t` and its own scale
  both exactly zero, so a purely relative test is 0/0) and that detector 3's solutions are isolated
  points a grid cannot land on, so the grid screens and Gauss-Newton decides.
- **trim-loop plumbing and the census degeneracy mask** (§6.7, 2026-08-23, live PASS in three
  runs) — the old items 1 and 2, one piece of plumbing. Both self tests passed first run; the live
  test took three, and each failure taught something worth the cost (§6.7's three findings). The
  census has now run against a real face's trim domain, which is what the audit item asked for.

**Built and live-validated**

| Construct | Spec | Module |
|---|---|---|
| Bernstein coefficient arithmetic | §6.0 | `bernsteinPolynomialUtils.fs` |
| Motion as entrywise B-splines, exact transport | §2.1, §4 | `swMotionSpline.fs` |
| Extraction records: faces, co-edges, vertices, UV calibration, poles, rationality contract | §5, §7.8 | `swSweepEmit.fs` |
| Envelope function `f`, factored coefficient form, `f_t` | §6.1 | `swEnvelopeMath.fs` |
| **Analytic closed forms, all five classes** | **§6.0.1, §6.5** | **`swAnalyticContact.fs`** |
| Sliding audit (freeform tensor test; analytic exact) | §6.4 | `swFunnelSolver.fs`, §6.5 |
| Vertex roots + sharp-vertex sign intervals | §6.2, §6.3.1 | `swFunnelSolver.fs` |
| Co-edge strip marching on shared arrays | §6.2, §6.3.2 | `swFunnelSolver.fs` |
| Funnel census: flood fill, trim mask, u-seam wrap | §6.3.3 | `swFunnelSolver.fs` |
| Island t-extremes by 3-var Newton | §6.3.3 | `swFunnelSolver.fs` |
| Section marching, arc-length resample, rigid lift | §6.3.4 | `swFunnelSolver.fs` |
| Rectangle / island / tube fits, certification, refinement | §7.1–7.3, §7.5 | `swEnvelopeFit.fs` |
| **Strip decomposition: branch cuts at refined t extrema, bands, marched pairing, one shared seam arc, merge marks** (live PASS, 38 checks) | **§7.1, §7.10** | **`decomposeFunnelComponentIntoStrips`** in `swEnvelopeFit.fs` |
| **Island patch emission: clipped fits, one-pole and pole-free caps as kernel faces, the exact shared-row split, the folded-island refusal** (live PASS, 2 runs) | **§7.4.1** | **`emitIslandPatches`, `splitIslandFitGrid`** in `swEnvelopeFit.fs` |
| Periodic-seam resolution (no rewindow, no pre-split) | §7.5 | `swEnvelopeFit.fs` |
| **Orientation of all five entity classes, the alternation certificate, the λ-sign fold certificate** (live PASS, 6 runs, every path exercised) | **§6.3 step 5, §6.6** | **`swOrientation.fs`** + tester, plumbed into `swEnvelopeFit.fs` |
| **Trim-loop converter: certified polylines, pooled chaining, seam-aware joins, winding** (live PASS) | **§6.7** | **`buildFaceTrimLoops`** in `swSweepEmit.fs` |
| **Cyclic (+v) trim mask, on-boundary tolerance, degeneracy mask, `touchesDegenerateBoundary`** (live PASS) | **§6.7** | **`swFunnelSolver.fs`** |
| **The census running on a REAL extracted face's trim domain** (live PASS) | **§6.7** | **`swTrimLoopTester.fs`** |
| **`SWEEP_FUNNEL_TANGENT_TO_SLICE` + the section split; `SWEEP_EDGE_SWEEP_SINGULARITY` + its Gauss-Newton refinement** (live PASS first try, 42 checks) | **§6.4, §6.9** | **`swDegeneracy.fs`** + tester |

**What this audit found missing is now the work queue in §12.3** — one list, not two.
This section keeps only the record of what exists.

**Coverage gaps that are not missing code**

- ~~Rotation closed for the contact FUNCTION and the ORIENTATION INVARIANT but never through a
  FITTED patch.~~ **CLOSED 2026-08-23 by §7.9's rotating tube fixture: `sweepRotatingTubeSelfTest`
  PASSES 17 of 17 in a real Part Studio.** Fit 8x27 at 3.15e-5 under a genuine rotation, and the
  orientation certificate over 216 samples with λ one-signed at fold margin 0.752, 216/216
  difference agreement and zero degenerate samples — the fold certificate exercised where λ is not
  constant by construction. Three findings the "it is cheap" estimate did not anticipate: the
  obvious fixture (constant-rate rotation about its own axis plus axial translation) is a HELIX and
  so a one-parameter subgroup, giving a t-independent contact set — the sliding case in disguise;
  the textbook quadratic formula sat nine orders above float noise at the ellipse's axis points, a
  5.2e-12 residual innocuous enough to have been accepted; and λ had to be read from its actual
  definition (§6.6) rather than a plausible `f_t/|∇f|` proxy, which reported a sign change that
  does not exist.
- ~~The funnel census has only ever run on synthetic fixtures, never on an extracted face.~~
  CLOSED 2026-08-23 by §6.7's live test: extraction to census on a split parabolic sheet, one
  128-cell slab cut to two 32-cell halves by the face's own hole.
- The orientation pass itself has only run in simulation, never in Onshape (§6.6, build order
  step 7d). Its fixtures are closed-form, so the live run is confirming the FeatureScript.

### 12.2 One source per thing — the test scaffolding consolidation (2026-08-23)

The testers had grown by copy. `translationMotionFromQuadraticVelocity` existed in **four**
places (`swFunnelSolver`, `swEnvelopeFit`, and a hand-inlined variant in each of
`swOrientationTester` and `swTrimLoopTester` — one of which carried the comment "repeated here so
this tester stands alone"); `islandFixtureSurface` and `slantFixtureSurface` in two each;
`clampToRange` and `clampScalar` were the same three lines under two names; the wavy bicubic patch
net was duplicated between `swSweepEmit` and `swSweepProbes`. Worst by volume, **twenty-two sites**
built the identical verdict string, printed it under their own tag, and reported it — in three
different conventions, two of which counted failures and one of which did not.

**`swTestHarness.fs` is now the single home** for the verdict reporter, a check tally, and every
fixture used by more than one module. It imports only std, so it sits under the whole stack and
closes no cycle. **333 duplicated lines deleted**, plus twenty verdict blocks collapsed from seven
lines to two. `clampToRange` is simply exported from `swFunnelSolver` now, which `swEnvelopeFit`
already imports.

Two check conventions live there **on purpose**. `reportTestVerdict` takes the `(checks, failures)`
pair the existing tests already accumulate, so they shed their boilerplate without a single
assertion being touched — a rewrite of ~200 live-validated check sites would have been the riskiest
possible way to buy tidiness. `newCheckTally` / `checkThat` / `checkWithin` / `reportCheckTally` is
the convention for NEW tests: it counts failures as well as checks, so a failure reads "3 of 47"
instead of a concatenation whose length is the only clue. `sweepRotatingTubeSelfTest` (§7.9) is the
first user.

**The paste churn had a root cause, and the FeatureScript docs settle it.** For an import of a tab
in the SAME document, *"the version is populated automatically on commit to be the latest
microversion of the tab"* — so **only the tab id has ever mattered**, and the version string in an
offline source is a placeholder Onshape rewrites for you. Half of the per-session "fix the
ids/versions on paste" ritual was never necessary. Live evidence: the `motionmoduletester` tab
imports `swMotionSpline` at a microversion the offline file has never carried.

**Tab manifest** (this document, workspace `4eb9c6e3e3a274ceddf5d819`). Resolved ids only — the rest
stay placeholders rather than guesses:

| module | tab | eid |
|---|---|---|
| `swTestHarness.fs` | swTestHarness | `8dba215569bb1c9f8f1bf700` |
| `bernsteinPolynomialUtils.fs` | utils | `8b495c3bb1037b467ca1d02e` |
| `swMotionSpline.fs` | swmotionmodule | `e32b4de68532811bf7e189be` |
| `swMotionSplineTester.fs` | motionmoduletester | `35754889c28c661683860a10` |
| `swEnvelopeMath.fs` (+ owner's combined tab) | swEnvelopeMathTest | `eede4083ca591e1a7adb8440` |
| `swSweepProbes.fs` | probes | `7cb2b17cd02e2f2384ff4bf5` |
| `swDegeneracy.fs`, `swDegeneracyTester.fs` | NOT PASTED YET (new 2026-08-23) | — |

Tabs whose mapping is ambiguous from the outside — `swFunnelSolver` (`6c8bd019…`), `swSweepEmit`
(`c7bec815…`), `envelopeFitSelfTest` (`78fdcc6a…`), `extractionSelfTest` (`fc8917c5…`), `tester`
(`b96253cf…`), `scratchpad test` (`98c0f14f…`) — are deliberately NOT recorded, because the
combined tabs mean a name does not determine contents and reading each one to find out is not worth
the API calls. `swAnalyticContact`, `swOrientation` and their testers have no obvious tab at all.

**Two harness limits worth knowing.** MCP `test_feature` runs in **its own scratch document**, so a
same-document import can never resolve there — which is why the testers carry "for MCP harness runs
the payload inlines the module bodies". Cross-document imports (`splineRefinementUtils`, being
`documentId/versionId/tabId`) resolve fine anywhere, which is what made §7.9's fixture numerics
testable without touching the development document at all.

**One latent bug in the payload builder, found by tripping it (2026-08-23).**
`doc_start`'s fallback — the branch that picks up a plain `//` comment run above a declaration —
anchored its regex with `$` under `re.M`, where `$` matches at *every* line end rather than only at
the end of the text being searched. So it found the FIRST `//` run in the file and swallowed
everything from there down to the declaration: 2682 lines for a twenty-line corrector, which
dragged the whole Bernstein and census stack into a payload that reaches neither, at 231 KB against
33 KB. It stayed hidden because every function it applied to happened to carry a `/** */` block,
which the other branch handles correctly; giving one internal function a bare `}` above it was
enough to expose it. Fixed to `\Z`. Worth knowing because the failure mode is a payload that still
*works* — it is only enormous, so nothing but the size report says anything is wrong.

**`tools/buildMcpPayload.py` does the inlining mechanically**, so the harness limit costs nothing.
Given a feature name it walks the call graph over the sweep modules, emits only reachable
declarations — **constants first**, because a module may define them below the functions that read
them and the merged payload must not depend on top-level resolution order — and strips comments.
`sweepRotatingTubeSelfTest` comes out at 65 of 259 declarations, 1595 lines, 58 KB, against 10952
lines for a naive concatenation of the same modules. Reachability is read from CODE only: counting
identifiers that appear in doc comments pulled in most of the stack (2633 lines against 1174). It
also reports duplicate top-level names across modules and finds none, which is the consolidation
above paying for itself — a merged payload cannot tolerate two copies of one function, so the
duplication this section removed was also what made single-payload runs impossible.

### 12.3 Remaining roadmap — the work queue

Everything not yet built, in the order it should be taken. Each item names its spec section and
what makes it done. **Owner's standing directive: finish a tier before starting the next — no
shortcuts toward the end goal.** Both former *(scope call)* items were answered 2026-08-23 and are
now construction; every item below is build work.

> **NEXT SESSION: tier 2 item 4, caps / knit / assembly (§9)** — owner, 2026-08-23. This is the
> first solid body the project will ever emit and the largest single gap in it. Note the ordering
> exception the owner has taken deliberately: tier 1 item 3 (the `REVOLVED` / `EXTRUDED` analytic
> classes) is still open, and caps/knit runs ahead of it. Nothing in §9 depends on item 3 — the
> step-7 fixture is an ellipsoid on the pointwise tube path, which needs no analytic class — so
> the two are independent, but this is a departure from the finish-a-tier rule and is recorded as
> one rather than as an oversight.

**Tier 1 — foundation: the §6/§7 gaps from the §12.1 audit**

1. ~~**Strip decomposition (§7.1).**~~ **DONE 2026-08-23 (§7.10, live PASS, 38 checks across two
   features).** A 6-alternation component splits at its refined merge time (0.4999999999999995
   against an exact 0.5) into two legs and a trunk; both fit and certify (leg 8×8 at 5.76e-4,
   trunk 6×16 at 1.30e-4, closed-form membership 2.7e-5 / 1.5e-5); and the sharing is exact rather
   than numerical — one shared cut sample, `anchorUvAtStation` returning a branch's own end sample
   at its end time, and the two legs' rows covering **35 of the seam arc's 35 interior vertices**
   bit-identically. Two findings came out of it: the pairing needs a marched oracle with a
   boundary-crawl rejection, not a positional rule; and station grading toward a merge — the
   obvious fix for the chart's square-root corner — is measured NOT to help the certification, so
   merge ends are MARKED and the clustering is opt-in. What is left open is named in §7.10: what
   *does* fix a merging strip, which nothing downstream waits on.
2. ~~**Island emission (§7.1, §7.4).**~~ **DONE 2026-08-23 (§7.4.1, two live PASSes: 21 of 21
   and 9 of 9).** The fixture this was blocked on does not exist, and that is the answer: an
   island carrying both its t-extremes ALWAYS folds, because λ at an extreme is exactly `f_t`
   there and the two extremes carry opposite `f_t` signs (measured: ∓0.16 with |λ − f_t| = 0).
   So v1 emits CLIPPED islands, `fitIslandComponent` takes `tStart`/`tEnd`, and the one-pole cap
   is accepted by the kernel as ONE periodic face — fit 8×8 at 2.77e-4, fold margin 0.570,
   kernel-certified at 1.43e-4 against fresh envelope loops. The split shape is exercised on that
   fold-free cap: 4×11 and 5×11 halves, one face each, **shared control row gap exactly 0 m**
   (7.4e-5 m with each half's own v parameters, which is why `interpolateFitGrid` now takes a
   v-parameter override). A folded island is reported, never emitted — and the kernel refuses it
   independently, in both declarations.
3. **Rational freeform — scope call CLOSED 2026-08-23, now construction.** No numerator route, no
   fattened hull, no pointwise fallback. Probe 8 showed the kernel names `REVOLVED` and `EXTRUDED`
   and that an axial (or cross-sectional) cut returns the exact generating profile, weights
   included, so the two-parameter rational case collapses to a one-parameter one. Decision in
   §6.0.2, accepted-face list in §3, the two classes specified in §6.5.1, §7.6's "class OTHER"
   claim corrected. **What is left is building them** in `swAnalyticContact.fs` plus the
   extraction-side recognizer in `swSweepEmit.fs`. *Done when:* a revolved-spline face and an
   extruded-spline face each produce contact curves through the analytic layer, cross-checked
   against `evaluateAnalyticContactDirect` at the tolerances §6.5 reports for the other five
   classes, and neither face touches `evApproximateBSplineSurface` at all.

*Former tier-1 item 4, the lean evaluator, is* **deferred to tier 4 item 9 (owner, 2026-08-23)**:
the cost shows up on worst cases, not on the fixtures the queue is built from, and the reckoning is
cheaper once consolidation has collapsed the solvers into fewer functions. It is not cancelled and
the §11 arithmetic still stands; tier 4 carries the trip-wire that pulls it forward.

**Tier 2 — emission: the first solid**

4. **Caps, knit, assembly (§9)** — build order step 7e, and the largest single gap in the project.
   §9 already specifies all of it: cap copies by `opPattern` with `motionSnapshotTransform`,
   contact wires through the grazing fits' own t₀/t₁ boundary rows, imprint by `opSplitFace`
   (projection-based; **never** the grazing sheets as `bodyTools` — envelope and cap are tangent,
   and tangent surface-surface intersection is the kernel's worst case), face classification by
   the sign of f at an interior sample, `opBoolean` UNION plus
   `joinSurfaceBodiesWithAutoMatching`, and degradation to a certified open sheet set naming the
   offending seam rather than a silently wrong solid. *Done when:* the step-7 smooth-only fixture
   (an ellipsoid along a spline) comes out as ONE solid body with no sliver faces and passes
   §14's deviation and volume checks.
5. **Error-budget ledger (§2.3).** `ε_total = ε_motion + ε_faceExtract + ε_envelopeFit + knit
   slop`, reported per run. Each term is measured somewhere; nothing assembles them. *Done when:*
   one run prints the four terms and their sum, and the sum bounds that run's measured output
   deviation.

**Tier 3 — the feature**

6. **Sharp features + the topology walk** — build order step 8; `swSharpFeatures.fs` and
   `swSweepTopology.fs` do not exist yet. §8 specifies both: the two-dot-product funnel criterion
   on the shared `g_side` arrays, the kernel-sweep route gated by `evPointsDeviation` against
   analytic Φᴱ samples with the closed-form transport route as its fallback, sharp-vertex sign
   intervals by 1D Newton, and the papers' loop walk answered entirely on input-B-rep adjacency.
   *Done when:* a filleted block — smooth faces, convex sharp edges, 3-face vertices — emits one
   solid.
7. **Feature UI, detectors, live tester, publish chain** — build order step 9; `solidSweep.fs`
   and `solidSweepLiveTester.fs` do not exist yet. The §10 detectors wire in here as always-on
   gates, and §14's fixture matrix {sphere, cylinder, box, filleted block} × {line, arc, helix,
   free spline, cusp-inducing arc} is the acceptance suite — including the brute-union *rate*
   check, the one test that distinguishes a true envelope from a fine discretize-and-blend.

**Tier 4 — after the foundations are complete (owner, 2026-08-23)**

8. **Collapse the test scaffolding into the core utilities plus the feature.** §12.2 got the
   testers to one source per thing; this step removes most of them outright. Nine modules carry a
   test feature today, and the testers plus `swSweepProbes.fs` and `swTestHarness.fs` are roughly
   a third of the sweep line count. What ships is the pure utility modules plus `solidSweep.fs`;
   what survives of the tests is `solidSweepLiveTester.fs` (§14) plus any self-test defending an
   invariant the live suite cannot reach. **Not before tier 3 is done** — the per-module testers
   are the only thing standing behind the live-validated numbers recorded throughout this spec,
   and deleting them earlier would make every number here unreproducible.
9. **The performance reckoning (owner, 2026-08-23)** — tier 1 item 4 lands here, widened. It runs
   *after* item 8 deliberately: the same argument that makes consolidation worth doing makes this
   cheaper afterwards, because the Newton–Raphson solvers and the other hot utilities are easier to
   rewrite once they are fewer functions. Three parts.
   - *(9a) Diagnose the evaluator.* Split the 1.1 ms on one surface four ways — rational+units,
     non-rational+units, non-rational+plain-numbers, and one grid-batched call — plus a run at
     higher degree, to separate fixed per-call overhead from per-basis-product cost. §11 argues the
     overhead dominates and that the answer is a grid-batched evaluator, not the unit-stripped
     scalar one §6.0.3 assumed.
   - *(9b) Pull the Toeplitz trick through the rest of the stack.* `bernsteinPolynomialUtils.fs`
     already phrases Bernstein multiplication as binomial cwise masks plus per-kernel-row Toeplitz
     matrix products (§12.2) — that is the pattern, and the Newton–Raphson solvers and the other
     interpreted grid-op chains have not had it applied. The doctrine is unchanged: a hot kernel is
     one matrix product, never a deep nested write loop.
   - *(9c) Record real numbers.* §11's figures come off Onshape's UI compute-time readout only.
   **Trip-wire that pulls this forward regardless of tier:** a live document taking ~100 s to
   rebuild trivial geometry. That is a stop-work condition, not a backlog item.

---

## 13. Live probes (complete; `custom-features/swSweepProbes.fs`)

1. **Edge-of-solid sweep** — RESOLVED (2026-08-21): `opSweep` accepts a solid body's edge as
   `profiles` directly (1 sheet body produced); the kernel-sweep route needs no
   `opExtractWires` step (the extract-to-wire path also works, kept as fallback knowledge).
2. **Evaluator throughput** — RESOLVED (2026-08-21): 20K evals 21.82 s, 40K evals 43.97 s →
   ~1.1 ms/eval flat on the rational+units path, degree-1×3 surface. 4–10× over the assumed
   budget → solver redesigned coefficient-first (§6.0), §11 recalibrated.
3. **Imprint tolerance** — RESOLVED (2026-08-21): the imprint is *projection-based*
   (`NORMAL_TO_TARGET` projects the wire onto the face), verified landing from 100 mm away —
   distance is structurally irrelevant. Design consequence: cap wires within fit tolerance of
   the face have negligible projected displacement; the thing to watch is projection
   *direction* validity, not proximity.
4. **Near-degenerate emission** — RESOLVED (2026-08-21): all collapse fractions accepted
   through a full pole (1.0). Island policy = pole patches (§7.1).
5. **Knit closer A/B** — RESOLVED (2026-08-21): on a valid multi-sheet complex BOTH closers
   produced one solid (`opBoolean UNION makeSolid`: 1 solid; `joinSurfaceBodiesWithAutoMatching`:
   1 solid). The §9 chain stands, with either usable as the other's fallback. (First run had a
   single sheet selected — one-tool union is invalid by definition; probe now guards.)
6. **Isocline oracle** — RESOLVED with a finding (2026-08-21), and the finding is what
   retired the design (2026-08-23). Viable on oblique directions: wires produced on a transformed
   scratch instance, samples harvested, scratch cleaned up. Degenerate when a face sits at
   isocline angle 0 everywhere (cylinder wall along its axis, caps across it) — which is §6.4's
   sliding case, the common case on real parts. That, plus a fixed-direction error going as
   `|ω|·R_tool / |b'|` (worst exactly where a fallback was needed), is why the oracle is **not in
   the design any more**: §6.8's certified census answers the same question from coefficient
   certificates, with no kernel call, no scratch scope, and no special case at `|b'| → 0`. The
   probe stays in `swSweepProbes.fs` as the record of a route measured and declined.
7. **UV convention calibration** — RESOLVED (2026-08-22, first MCP-harness run, selection-free
   Self Test fixture): (a) the `DistanceResult` doc claim is TRUE — `evDistance` face
   parameters are exactly `evFaceTangentPlane`'s bbox-normalized parameters (origin mismatch
   7.8e-18 m cylinder / 3.0e-17 m freeform); (b) kernel→knot-domain is affine ONLY when the
   kernel geometry is the extracted spline itself — identity to ~4e-17 m on a created bicubic
   patch, but NOT affine across `evApproximateBSplineSurface` of a cylinder (axial exact,
   circumferential non-uniform; best-fit affine residual 4.8 mm on r = 30 mm). All 12 witness
   distances were exactly 0 m — the module evaluator agrees with the kernel to machine zero on
   both the rational periodic and freeform extractions. Design folded into §5. Remaining v1
   face classes (cone, torus, sphere) can be spot-checked with the same Self Test pattern if
   the exact-extraction path ever routes them through approximation.

8. **Named Surface Classes** (2026-08-23, run through the MCP harness as a selection-free lambda;
   the module feature `sweepProbeNamedSurfaceClasses` is the interactive variant, for pointing at
   faces on real parts). Closes §12.3 item 3. Measured on a revolved cubic profile, an extruded
   cubic section, and a revolved rational quarter-ellipse:
   - `evSurfaceDefinition` returns **`{ surfaceType }` and nothing else** for both `REVOLVED` and
     `EXTRUDED` — the class name is the entire payload, no axis, profile, or direction.
   - `evAxis(context, { "axis" : face })` returns the revolve axis; it **throws** on `EXTRUDED`,
     whose direction comes from two `evFaceTangentPlanes` origins a full v-span apart.
   - `opPlane` through the axis + `opIntersectFaces` returns the **exact generating profile**. The
     cubic came back degree 3 / 5 control points / non-rational on knots `[0,0,0,0,.5,1,1,1,1]`;
     the rational quarter-ellipse came back degree 2 / 3 control points / `isRational true` with
     weights `[1, 0.7071067811865476, 1]` — bit-for-bit the input. A full revolve yields two such
     edges, one per side of the axis; the extruded cross section recovers the same way, one edge.
   - `evApproximateBSplineSurface` returns `{ bSplineSurface, boundaryBSplineCurves,
     innerLoopBSplineCurves }` — the net is nested under `.bSplineSurface`, not at top level.

---

## 14. Validation

Pure testers (no geometry): analytic funnels (sphere → the contact curve is a great circle
band; cylinder under rotation; plane under translation exercises sliding detection); motion
drift and derivative-vs-finite-difference checks; event-bisection determinism;
exact-reconstruction anchors (Schoenberg–Whitney unisolvence, as in the spline spec).

`solidSweepLiveTester.fs` fixtures {sphere, cylinder, box, filleted block} × {line, arc,
helix, free spline, cusp-inducing arc}:

- **Kernel acceptance:** one SOLID body, no sliver faces.
- **Deviation certification:** dense **off-station** envelope samples vs output, one
  `evPointsDeviation`, ≤ tolerance.
- **Tangency:** at sampled t, scratch instance; `evDistance`(output, instance) ≈ 0 AND
  normal anti-parallelism at the witness UV.
- **Ground truth:** brute union of N and 2N transformed instances; assert output ⊇ brute
  (subtraction empty) AND the volume gap shrinks ~4× when N doubles — the *rate* check is
  what distinguishes a true envelope from a fine discretize+blend.
- **Volume monotonicity** under t-range extension; exact anchor for a convex tool on a
  straight segment: `V = V_tool + A_projected · L`.
- **The cusp fixture** errors with the documented message — never emits a self-intersecting
  body.

Live-in-Onshape is the only definition of done (repo doctrine); nothing here is declared
working without a passing build confirmed by the owner.

---

## 15. Open questions

Resolved questions move to the bottom with their answer; open ones stay on top.

**Open:**

- **The lean evaluator's real throughput** (§12.3 tier 4 item 9a; DEFERRED there by the owner
  2026-08-23, with a ~100 s-rebuild trip-wire that pulls it forward) — re-run the throughput probe; the
  §11 budget assumes several-fold under the measured 1.1 ms, and the arithmetic there shows the
  plan missing the performance bar by an order of magnitude if that assumption fails. **Amended
  2026-08-23: the MCP route is withdrawn.** The eval response carries no server-side timing, so
  client wall-clock of `test_feature` measures a network round trip rather than compute;
  differencing at N and 2N does not fix that, and a figure derived that way must not enter §11.
  The owner's UI compute-time readout is the only instrument. Also amended: §11 argues the thing
  to isolate first is fixed per-call overhead versus per-basis-product cost, which makes a
  grid-batched evaluator the likely answer rather than the unit-stripped scalar one §6.0.3 assumed.
- **Motion spline degree** — cubic vs quintic; decide from the motion tester's drift data.
- **Promoting `editSurface.fs`'s private emission floor to a shared module** vs replicating
  it (three consumers after this feature: editSurface, free-form deformation, sweep).

**Resolved:**

- **Rational freeform faces** (2026-08-23, probe 8) — answered by classification, not by new math.
  `SurfaceType.REVOLVED` and `SurfaceType.EXTRUDED` are named kernel cases the extraction had never
  asked about, and cutting such a face with a plane through its axis returns the exact generating
  profile, weights and knots included. Both become analytic classes (§6.5.1); a profile is
  one-parameter, so a rational one clears its denominator into an ordinary polynomial and the
  two-parameter rational case never arises. Genuinely rational freeform — a rational loft or
  boundary surface — is rejected by name in v1 (§3). Numbers in §13, decision in §6.0.2.
- **The `evDistance` face-parameter convention** (2026-08-22, probe 7 via the MCP harness) —
  it is `evFaceTangentPlane`'s bbox-normalized convention, exactly (mismatch ~1e-17 m);
  kernel→knot-domain is affine only when the kernel geometry is the extracted spline itself,
  and measurably NOT affine across an approximated cylinder (4.8 mm best-affine residual on
  r = 30 mm). Full numbers in §13; calibration-helper doctrine in §5.
- **Probe outcomes** (2026-08-21) — all six complete; recorded in §13 and folded into §2.2,
  §5, §6.0, §7.1, §9, §11.
- **Grazing island emission** (2026-08-21) — the kernel accepts pole-collapsed patches;
  islands emit as pole patches, split-and-report demoted to a safety net (§7.1).
- **Where the Bernstein utilities live** (2026-08-21) — a new standalone
  `custom-features/bernsteinPolynomialUtils.fs`, dependency-free. Deliberately NOT an addition
  to the published `splineRefinementUtils.fs`: the sweep is being developed in its own
  document, and touching the published module would force a republish + version bump across
  every consumer on each refinement.
