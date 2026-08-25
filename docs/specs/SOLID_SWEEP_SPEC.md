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
only smooth-face grazing patches need numerical fitting. **CORRECTED 2026-08-24 (§7.0): read
"only FREEFORM smooth-face grazing patches".** That clause predates §6.5 and is false as written.
Analytic smooth faces are exact or one-dimensionally collapsed: a cylinder or cone under pure
translation contacts along whole FIXED rulings, so consequence (c) above emits its lateral patch by
control-point arithmetic with no fit at all; a plane's contact set is a straight line at every
station, so its patch is exactly ruled between two directrices the co-edge pass already computes;
and a sphere's contact is always a great circle, because W is skew and `⟨n, W n⟩` vanishes
identically. This realizes in B-spline algebra the
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
  direction comes from `evFaceTangentPlanes`. **The rule recorded here first — that the chords a
  full span apart agree along the extrusion and differ across it — is wrong** (§6.5.1): `C(u) + v·d`
  is translation-invariant in BOTH parameters, so both chord pairs always agree. The ruling is the
  parameter direction along which the NORMAL is constant, which is what the build uses.
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

**6.5.1 The two profile-driven classes: `REVOLVED` and `EXTRUDED` (built 2026-08-24).** Both are
profile-driven rather than parameter-driven, so neither carries its shape in `evSurfaceDefinition` —
§6.0.2 records what probe 8 measured and how each one's generator is recovered. Both land on
machinery this module already had, and the whole addition is one frame builder, two coefficient
builders, and a root search.

*EXTRUDED* is the cheapest class in the feature. `S(u,v) = C(u) + v·d`, so `S_v = d` and the
unnormalized normal `C'(u) × d` does not depend on `v` at all. Pulled back through §6.5's identity,
`f = ⟨N_local, W_local·S_local + g_local⟩` is **linear in `v`** — the same ruling form the cylinder
and cone already solve, with the closed-form graph `v = −A(u)/B(u)` and the `u` where `B` vanishes
reported rather than sampled through. The only new work is that `A` and `B` come from a profile
curve's own coefficients rather than from `cos u`/`sin u`, so root isolation on them is polynomial
rather than trigonometric: the grid is per knot SPAN rather than per harmonic (8 samples per span per
degree, `B` restricted to a span being a polynomial of degree at most `degree − 1`), bracketed on
sign change and polished by bisection-safeguarded Newton on the exact `dB/du`.

*REVOLVED* is the sphere/torus form generalized. In the axis frame
`S(u,θ) = (r(u)·cos θ, r(u)·sin θ, z(u))`, and `f` is a **degree-2 trigonometric polynomial in θ**
whose five coefficients are built from `r, z, r', z'` at one `u` — exactly the shape the existing trig
utility takes, including its `nearTangency` report. Per-meridian solving is therefore unchanged; what
is new is only that the coefficients come from evaluating a curve instead of a formula. A rational
profile is fine: clearing a one-parameter denominator leaves an ordinary polynomial, and the
denominator is strictly positive on the profile, so it cannot change the sign of `f`.

**Two decisions the build made that the specification had not.**

1. *Their normal is left UNNORMALIZED.* The five parameter-driven classes hand back a unit normal;
   these two hand back `S_u × S_v` as it comes. `|S_u × S_v|` depends on `u` alone, so it is a
   positive `u`-only rescaling of `f` that moves no root, and keeping it means every coefficient
   stays polynomial in the generator's derivatives — normalizing would put a square root of them
   under all five, and would divide by zero at a stationary point of the generator. The consequence
   to remember is that their `|f|` bounds bound the rescaled `f`.
2. *`B ≡ 0` gets its own answer instead of an empty one.* Under **any pure translation** `W` is zero,
   so `B = ⟨N, W e3⟩` vanishes identically, `f` does not depend on the ruling parameter, and the
   graph has no values anywhere. The contact set there is **whole rulings at the roots of `A`** —
   which is the extruded twin of the cylinder's two contact rulings under a crossing translation, and
   the common case on real parts (an extruded profile swept along a line). `solveAnalyticContactCurve`
   detects it and returns `form : "profileRulings"`. The five parameter-driven classes still leave
   that case to their caller, which is now the one asymmetry left in this layer.

**The extraction side.** `classifyProfileDrivenSurface` asks the question extraction had never asked
— `surfaceDefinition.surfaceType` against `SurfaceType.REVOLVED` and `EXTRUDED` — and
`recoverProfileDrivenFrame` turns a named class into a generator. It is reached through a
FIVE-argument `extractToolFaceRecords` taking an id source; the three- and four-argument overloads
pass `undefined` and route these faces to approximation exactly as before. **That default is now the
wrong way round** (§6.10): recognition is what the solver should route on, so the class must reach
the provider rather than being an opt-in an existing caller can decline. What the recognized face
gives up is the approximated net, and with it the co-edge pcurves inversion builds onto a net — the
same trade the five parameter-driven classes have always made, and one §7.0 removes for the ruled
classes by taking the directrices from the co-edge pass instead.

**Generator recovery is one op, and it needs no heuristics at all (revised 2026-08-24).** A revolved
face *is* its generating curve: the surface carries no information the profile does not, so the
generator should be read off, not reconstructed. `opCreateCurvesOnFace` (geomOperations.fs) does
exactly that — "for each specified surface parameter value, creates a new wire body following the
curve which keeps the surface parameter at that constant value" — with
`FaceCurveCreationType.DIR1_ISO` / `DIR2_ISO` and, critically, **`skipTrim : true`** so the curve is
the UNTRIMMED iso-curve of the underlying surface rather than the piece this face happens to span.
For a revolve the constant-θ iso-curve is the generator, exactly. For an extrusion one direction is
the exact cross section and the other is a `Line` whose direction is the ruling.

This supersedes the `opPlane` + `opIntersectFaces` recovery first built here, and it deletes every
heuristic that route needed, all of which existed only to place a cutting plane well:

- the rule that the revolve's cutting plane must pass through the face's bounding-box centre (so a
  partial revolve whose θ range misses an arbitrary perpendicular is not simply missed);
- the fallback to `perpendicularVector` when the box centre lies on the axis, as it does for a full
  revolve;
- picking which of the two returned profile edges lies on the `+e₁` side, which is what kept the
  frame's own `r(u)` non-negative;
- the four-tangent-plane normal-invariance test for the extrusion direction — now read directly off
  the ruling iso-curve's `Line`.

*The chord rule probe 8 proposed for the extrusion direction was wrong, and the reason is worth
keeping* even though the test it justified is gone: `S(u,v) = C(u) + v·d` is exactly
translation-invariant in BOTH parameters, so the two chords of either parameter direction always
agree and comparing them is vacuous. It was replaced by a normal-invariance test — `C'(u) × d` does
not depend on the ruling parameter — which measured a residual of exactly 0 on live faces, and is
now replaced in turn by simply reading the `Line`.

*And it removes an approximation nobody needed.* §7.6's ellipsoid was extracted as a 9 × 4 rational
net at 1e-7 (or a 99 × 49 non-rational one at 1e-6) and then marched. Its generator is one iso-curve:
an exact ellipse. Every §7 and §9 figure measured on that fixture, the 23 s of §11.3 included, was
paid for approximating a curve the kernel would have handed over exactly.

**What is refused rather than approximated.** A generator that comes back as a `Line` is converted
exactly (degree 1 on two endpoints). One that comes back as a `Circle` or `Ellipse` struct is
REFUSED: its exact rational spelling needs the edge's own angular trim, and an approximation there
would be an approximation the record then reports as exact. The refusal sentence goes into
`profileRecovery.refusal`, the face falls back to approximation, and nothing claims exactness. Probe
8 measured the rational quarter-ellipse coming back as a degree-2 rational **B-spline** rather than
as an `Ellipse`, so this is the one remaining hole in these two classes rather than their common
case.

Cost note: recovering the profile is two kernel ops per revolved or extruded face, once at
extraction, against zero pointwise surface evaluations for the whole face afterwards. §11's
extraction row absorbs them.

**Measured, both live PASS on the first run that compiled (2026-08-24).** Two features, in a Part
Studio of their own so the §9.4 timed fixture is untouched.

*`sweepAnalyticProfileContactSelfTest` — PASS, 19 checks*, selection-free, on hand-built generators
so the algebra is tested with no kernel recovery in front of it. The station carries a nonzero
rotation derivative and a deliberately NON-orthonormal `A`, for the same reason §6.5's five-class
test does.

- Both classes against the §1.1 definition: **1.4e-17** (REVOLVED), **4.2e-17** (EXTRUDED).
- The structures reproduce `f`: θ polynomial **1.4e-17**, `A(u) + v B(u)` **1.4e-17**.
- **A rational quarter-circle generator reproduces the SPHERE class exactly: 2.8e-17.** This is the
  load-bearing check of the whole addition — a revolved exact quarter circle *is* a sphere, so two
  classes that share no algebra (one evaluating a rational curve, one a formula) must agree, and the
  relation `f_revolved = −|C'(u)|·f_sphere` is exact rather than approximate. Its two supports: the
  frames agree to **6.2e-17** and the rational generator stays on its circle to **2.1e-17**.
- Sliding, from the coefficients with nothing sampled: a revolve **spinning about its own axis** and
  an extrusion **translating along its own direction** are both detected. Both cancel term by term —
  the first because `W` skew about `e₃` kills all five θ coefficients, the second because
  `N = C' × e₃` is perpendicular to `e₃` everywhere.
- Contact curves sit on `f = 0`: REVOLVED 40 samples at worst **2.6e-15**; EXTRUDED 49 graph samples
  at worst **1.8e-15** relative.
- Singular rulings are found rather than sampled through: **2** of them, at u = 0.16717 and 0.88110,
  with `|B|` there **3.8e-17**. The fixture makes that non-vacuous on purpose — a cross section whose
  tangent sweeps 315° forces any nonzero linear form in the tangent to vanish somewhere.
- Neither bound is exceeded. REVOLVED claims 0.0600067 against a sampled 0.0596996; EXTRUDED claims
  0.0748209 against a sampled 0.0748209 — **attained**, which is what a bound built from `|A| + |B|·v`
  should be on a class that is linear in `v`.

*`sweepAnalyticProfileLiveTest` — PASS, 30 checks*, on real kernel faces: a spline revolved 360°
about an axis it does not touch, and the same kind of spline extruded. What this proves that the self
test cannot is that the RECOVERED generator is the face's own — every figure is measured against the
kernel's geometry, not against more of our algebra.

- Both faces classify as their named class and **neither carries approximation output**: no `spline`,
  no trim loops, `splineIsExact` false. Item 3's "does not touch `evApproximateBSplineSurface`" is
  asserted, not assumed.
- Generators recovered: **degree 3 × 6, rational, out of plane 0 m** on both. The revolve returns
  **two** profile edges (one per side of the axis) and the extrusion one, exactly as probe 8
  measured — except that both came back RATIONAL where probe 8 recorded a non-rational cubic, which
  is the kernel's own spelling of a sketch fit spline and is why the rational path had to work
  before either class could.
- The extrusion-direction test residual is **0**: the normal is exactly constant along the ruling,
  which is the property the corrected rule is built on.
- Against the kernel face, over a 4 × 4 grid: worst point distance **0 m**, worst normal cross
  product **1.3e-15** (REVOLVED) and **4.0e-10** (EXTRUDED).
- The contact curves, checked against §1.1 evaluated with the KERNEL's own normals at the same
  points — the strongest statement this layer can make, that its contact set is the face's true
  grazing set: worst `|f|` **1.0e-13** (REVOLVED) and **1.4e-12** (EXTRUDED), at 0.3 m/s.
- And against their closed-form answers, since each station is built from the recovered frame so the
  answer is known in advance: the revolve's contact meridians land on θ = π/2 and 3π/2 to
  **3.6e-13**, and the extrusion's whole ruling lands on the parameter whose tangent the station
  translates along, off by **exactly 0**.
- Both live curves report `nearTangency`. That is correct and worth not mistaking for a fault: these
  stations are built to make `f` vanish along a whole line of the domain, so the grid does graze zero
  without crossing it — which is precisely the condition the flag exists to announce.

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

**6.10 The contact provider contract — route by class, sample last (2026-08-24).**

This section closes a defect that has now been recorded twice and fixed zero times. §6.0 strategy 1
promised "analytic faces solve in closed form — zero de Boor". §11's budget assumed "analytic faces
≈ free (closed forms)". §11.6, written *after* the profiling pass, measured that **87% of a build is
the spline evaluator "serving a numerical solve that a revolved face does not need"** and ranked
fixing it ahead of every other lever. And nothing in the solver, the fitter, or the emission path
has ever called the analytic layer.

**The audit, 2026-08-24, against the tree rather than against memory.** The analytic layer's only
non-test consumers are `recoverProfileDrivenFrame`, which *stores* a frame on a face record, and
`analyticFrameForFaceRecord`, which has **no caller in the module at all**. Every
contact-evaluating entry point — `evaluateAnalyticContact`, `analyticContactPullback`,
`solveAnalyticContactCurve`, `analyticContactBound` — is reached only from `solidSweepTester.fs`.
The fitting layer contains **zero** occurrences of `analytic`. `record.surfaceClass` is read nowhere
past extraction: the classification is computed and discarded. Two functions already route an
analytic face to nothing rather than solving it — `buildFaceTrimLoops` returns
`emptyTrimLoopResult("noSpline")` above a comment reading "spec section 6.5 solves it in closed
form", and `extractCoEdgeSide` silently yields no pcurve. And 57 signatures take
`strippedSurface is map` with **zero** occurrences of `strippedSurface ==` or `!=`, so no entry
point can even be handed a face without a control net.

**Why it persisted, which matters more than the fact.** §11.6 filed the routing work under §12.3
tier 1 item 3, whose done-when read "*each produce contact curves through the analytic layer,
cross-checked against `evaluateAnalyticContactDirect`*" — a tester agreement with no solver path in
it. That item closed 2026-08-24 having satisfied exactly that wording, so the highest-value lever in
this document became owned by a struck-through item. §12.3 is rewritten on one rule as a result:
**every done-when names a solver or emission path, never a tester agreement.**

**What the solver actually asks of a face.** Six questions, read off the call graph rather than
invented. The contract is these six and nothing more, which is what lets a provider be closed-form
without implementing a surface.

| question | sampled (today) | analytic | coefficient |
| --- | --- | --- | --- |
| can this region graze at all | sampled bound | `analyticContactBound` | `bernsteinGridRange` (already live) |
| does the face slide | `auditEnvelopeSliding` on tensors | `slidesEverywhere`, exact from coefficients | as today |
| contact curve at a station | `marchSectionCurve`, ≤400 steps × 8-iteration corrector | `solveAnalyticContactCurve` | Bézier clipping |
| co-edge boundary anchors | pcurve inversion onto a net | closed form on the shared `g_side` arrays (§6.2) | root isolation |
| `f_t` events and tangencies | sampled audit (§6.9) | differentiated closed form | differentiated coefficient nets |
| lift to 3D and orientation λ | order-2 evaluations | closed form | coefficient nets |

**Three providers, and the routing rule.** `analytic` for the seven classes of §6.5 and §6.5.1
(plane, cylinder, cone, sphere, torus, revolved, extruded) — zero de Boor, zero Newton on the tool
face. `coefficient` for non-rational freeform, per §6.0 strategy 2. `sampled` last, which is the
role §6.0 strategy 3 already assigns it: "final Newton polish of isolated roots, the (q,t) fit grids
on live patches (hundreds of points, not tens of thousands), and batched kernel certification". A
face routes by `record.surfaceClass`, which must therefore **survive past extraction** — today it
does not.

**This is a contract to define, not a pipeline to unpick.** `extractToolFaceRecords` is called only
from the tester, `solidSweepUtils.fs` contains no `defineFeature`, and `solidSweep.fs` does not
exist (§12.3 tier 3 item 7). The emission chain is assembled by hand inside test features. So there
is no shipped caller to break, and the contract can be the one `solidSweep.fs` is built against.

**Two representation-agnostic seams already exist in production code, and the contract is modelled
on them rather than invented.**

1. *The census already consumes an abstraction, not a surface.* `censusFunnelComponents`,
   `auditEnvelopeSliding` and `isolateEnvelopeCells` take `patchFactors`, and
   `refineBlockStationaryPoint` solves on coefficient grids with **no surface evaluation at all**.
   The chokepoint is the single producer `buildEnvelopePatchFactors`, which throws on rational input.
   Generalizing that producer into a provider is a smaller change than it looks.
2. *The sharp-edge and vertex path already solves contact with no surface whatsoever.*
   `marchStripZeroCurves(strippedMotion, normals is array, points is array, …)`,
   `findContactFunctionRoots`, `solveVertexContactIntervals` and `refineContactRoot` take **values**,
   not a net. This is the existing precedent for a contact solver whose signature does not name a
   representation, and the new provider signatures follow it.

**The coefficient provider is half-built already.** Sixteen Bernstein functions have production
callers and the convex-hull screen is live in the census via `bernsteinGridRange`. What is test-only
is the *second* half — `isolateBernsteinRoots`, `bernsteinExcludesZero`,
`bernsteinGridExcludesZero`. So §6.0 strategy 2 needs connecting, not writing.

**MEASURED 2026-08-24 — the routing lever, on the section-9.4 face.** Tier 0 item 0g's first half is
done: `sweepEllipsoidRouteAbLiveTest` runs both routes to the contact curve on the same ellipsoid,
the same straight translation, and the same five stations, and the route switches let each be
profiled alone.

*Correctness first, because a cost measured on a wrong answer is worthless.* The closed-form contact
points were judged by the SAMPLED route's own equation, in its parameters, with its evaluator:

- analytic contact points against the sampled route's envelope function: **worst |f| 2.28e-17**;
- the same points inverted onto the extracted 9 x 4 rational net: **residual 5.61e-16 m**, which also
  confirms section 7.8's claim that this net is the exact ellipsoid rather than an approximation of
  it;
- the two contact curves lie **4.64e-5 m** apart, against a comparison floor of **4.66e-4 m** — the
  sampled loop's own measured polyline sagitta. The gap is an order of magnitude BELOW what a
  point-to-polyline comparison can resolve, so the curves agree as closely as this test can see.
  (The floor is measured from each vertex's distance to the chord between its neighbours, not
  assumed: a first attempt gated against a made-up 1e-6 and failed on its own resolution.)

*Then the cost, from the profiler's per-function table (inclusive times, one call per station).*

| | sampled route | analytic route |
| --- | --- | --- |
| contact solve | `tubeLoopSamples` **10.70 s** (79.3%) | `solveAnalyticContactCurve` **827 ms** (85.3%) |
| whole regen | 13.50 s (with cross-checks) | **970 ms** |
| surface-evaluator calls | the whole of it | **none — `leanSurfaceDerivatives` is absent from the table** |
| generator recovery | `evApproximateBSplineSurface` | 13 ms for both extractions, iso-curve + exact conic |

**12.9x on the contact solve, and 92.8% off the regen, for an answer identical to 2.3e-17.** Section
11.6 predicted 87% from arithmetic; the measurement is 92.8%, so the model was right and slightly
conservative. The isolated analytic run reports **15 functions across 17 call sites** in total, which
is the whole shape of the claim: there is no hot loop to optimize because there is no loop.

Two honest limits on the figure. It is the CONTACT SOLVE, not a build — neither route fits, caps or
knits here, so it is not comparable to section 11.3's 23 s assembly. And profiled time runs ~30%
slow, so only Onshape's own compute-time readout may be quoted as a build time; what is quoted above
is the ratio and the call count, which is what section 11.1's ~15,000-evaluation model is stated in.

**Then the scan went too (2026-08-24, same fixture, gated profile).** Every analytic class produces a
degree-2 trigonometric polynomial, and its roots are available exactly: tangent half-angle to a
quartic, resolvent cubic with Viete's trigonometric branch. `solveDegreeTwoTrigRootsClosedForm`
replaced the scan-and-polish, which had been ~82% of the analytic solve.

| | before | after |
| --- | --- | --- |
| `solveAnalyticContactCurve`, 5 stations | 827 ms | **280 ms** (2.95x) |
| analytic route regen | 970 ms | **430 ms** (-55.7%) |
| against the marched `tubeLoopSamples` (10.70 s) | 12.9x | **38x** |

The scan is kept as the fallback for degree > 2 and as the ORACLE: `sweepTrigRootClosedFormSelfTest`
solves nine cases both ways and requires agreement, chosen to hit every branch of the reduction
rather than to look thorough - a double root, the root at pi that the substitution sends to
infinity, the biquadratic case where the factorization's `s` legitimately vanishes, degree 1, and no
roots at all. Measured: worst residual |P(root)| 2.2e-15, worst disagreement with the scan 7.1e-15,
and cos 2x's roots exact to 8.9e-16.

*`nearTangency` was rebuilt on the way, and is now better than either predecessor.* A double root is
`P = P' = 0`, so the derivative at each root decides it. The scan's heuristic OVER-reports - it fires
on simple roots it happens to sample near, measured on `cos 2x` and on a pure first harmonic - and
this function's own first version, which compared roots for proximity, UNDER-reported: a stable
quadratic solver returns coincident roots once, so `-1 + cos theta` has no second root to be near.
Under-reporting is the dangerous direction, because spec 6.4 turns this flag into a rejection.

Where the remaining 430 ms goes, for whoever takes this further:

- ~157 ms - the generator evaluated once per profile parameter inside the solve (310 calls);
- ~101 ms - **the same generator evaluated AGAIN by the lift**, at parameters the solve has already
  visited. A pure duplicate, and the same shape as section 11.2's lever A one layer down;
- ~120 ms - the closed-form root solving itself;
- ~28 ms - fixture, extraction and motion sampling.

So the next two targets are: carry the generator evaluation from the solve into the lift, and give
the generator a lean evaluator. ~500 microseconds for an order-1 rational de Boor on a degree-2,
five-control-point curve is the GENERAL `evaluateBSplineCurveDerivatives` doing general work, which
is section 6.0.3's argument for `leanSurfaceDerivatives` one dimension down.

**The analytic layer's own cost, and the two changes that fixed it (2026-08-24).** The owner's
report was that the analytic self tests still took 18 s, which the routing work could not explain -
they were already closed form. Two things were wrong underneath, and both are the same mistake the
surface path made first:

1. *The generator was read through the GENERAL evaluator.* `evaluateBSplineCurveDerivatives` from
   `splineRefinementUtils` measured **~470 microseconds** for an order-1 rational read of a degree-2,
   five-control-point curve - a fully general NURBS evaluator allocating Vectors and a binomial table
   for a five-point quadratic. `leanCurveDerivatives` is its curve twin: same A2.3 recurrence, same
   A4.2 quotient rule, same summation order, on plain-number triples with the control row hoisted
   and nothing allocated. Every generator read in this layer moved onto it. This is exactly what
   section 11.3 did for `leanSurfaceDerivatives`, one dimension down, and it had simply never been
   done for curves.
2. *The frames dispatched through an if-ladder.* Seven classes tested in sequence on every
   evaluation, which a REVOLVED frame walked past four times. Replaced by one typed frame per class
   and overload resolution - see the note on tagging below.

**Measured, gated profile, both self tests in one Part Studio: 18 s -> 1.62 s profiled** (a plain
regen is ~30% under that), 18 of 18 and 19 of 19 checks. What remains is 3,081 contact evaluations
at 327 microseconds - the tests' own dense grids - and that 327 is now FeatureScript call overhead
rather than arithmetic: a dispatch, a `{point, normal}` map, two Vectors and a dot product, where a
plane's actual work is three multiplies. The same trade is available once more (a flat-scalar
contact evaluator that allocates nothing) but it is TEST-grid cost, not production cost.

**On tagging, because two attempts got it wrong.** A FeatureScript value carries ONE type tag and
there is no subtyping. So:

- frames are TAGGED at construction (`as PlaneContactFrame`), which is what makes the specific
  overloads resolve - untagged maps match none of them;
- a tagged value still satisfies `is map`, so every consumer that does not branch on class simply
  takes a map;
- an UMBRELLA type (`AnalyticContactFrame` as a parameter type) cannot work at all, in either
  direction: it rejects tagged values for want of subtyping, and it was rejected for untagged ones
  too. The name survives only as a shared PREDICATE the seven specific ones call.

The error text says all of this in one line - `Call analyticContactPullback(map, PlaneContactFrame
(map)) does not match ... AnalyticContactFrame` - and it is worth reading twice: the value is
reported as tagged AND as a map, which is the whole rule.

**The invariant that keeps three providers honest.** Every provider answers the same six questions
about the same `f`, so any two that both apply to a face must agree, and the sampled provider always
applies. That makes cross-provider agreement on a shared fixture the standing regression check —
the same shape of check §6.5 used when it measured every closed form against
`evaluateAnalyticContactDirect` at 1.1e-16…5.6e-16, except now between production paths rather than
inside a test.

---

---

## 7. Fitting & certification — `swEnvelopeFit.fs` (pure)

**7.0 Emission by class: exact, collapsed, fitted (2026-08-24).** §7 was written on one assumption —
that every smooth face's envelope patch is obtained by fitting a `(q,t)` sample grid — and §2.1 states
it as doctrine: "sharp-edge envelope faces, sharp-vertex trajectory edges, and cap placement are
closed-form exact relative to the fitted motion — **only smooth-face grazing patches need numerical
fitting**." That sentence predates §6.5. It is now false, and the correction is not a speedup but a
change of kind: for several classes the envelope is *exactly* representable, and for several more the
two-dimensional fit collapses to one dimension.

**The unifying statement.** The contact curve's *shape* is closed-form per class (§6.10). So the
envelope is a swept curve of known type, and what has to be fitted is not a surface but **the motion
of that curve's few defining parameters**. Three rungs follow.

**Rung 1 — exact, no fit at all.**

- *Cylinder or cone under pure translation.* `W = AᵀA'` is zero, so the ruling coefficient `B`
  vanishes identically and contact is *whole rulings* at the roots of `A` (§6.5.1 decision 2). Each
  ruling is a **fixed** line in the tool frame, and §2.1(c) makes the rigid transport of a fixed
  B-spline curve an exact tensor-product B-spline: `Q_ij = A_j·P_i + b_j`, weights unchanged. The
  lateral patch is emitted by control-point arithmetic. Zero samples, zero fit, zero certification —
  the deviation is identically the motion's own ε_motion.
- Everything §2.1(c) already covers: sharp-edge envelope faces, sharp-vertex trajectory edges, cap
  placement.

**Rung 2 — collapsed, a 1-D fit instead of a 2-D grid.**

- *Plane, any rigid motion.* Pulled back through §6.5's identity, `f = g₃ + u·w₃₁ + v·w₃₂` is exactly
  linear in `(u,v)`, so the contact set at every station is a **straight line** in the face. Its
  image under the rigid map is a straight line in space, so the envelope patch is a one-parameter
  family of lines — **exactly ruled**, degree 1 in the ruling direction, and a tensor-product
  B-spline of degree `(deg_t, 1)` between two directrix curves. The directrices are where the contact
  line meets the face's trim boundary, which is to say **they are the co-edge envelope curves §6.2
  already computes once per co-edge and shares between consumers.** §7.1's own framing anticipates
  this without naming it: "q ∈ {0,1} iso-edges *are* the co-edge envelope curves". For a planar face
  the patch is therefore determined by data the pipeline already has, and the additional sampling is
  none.
- *Sphere, any rigid motion.* `W` is skew whenever `A` is orthonormal, so the quadratic-in-normal term
  vanishes *identically* — `⟨n, W n⟩ = 0` — and `f = ⟨n, g⟩` is linear in the unit normal. The contact
  set is therefore **always a great circle**, whatever the motion. The envelope is a family of circles
  of constant radius `R`; what is fitted is the circle's plane normal `g(t)/|g(t)|` and its centre —
  two curves in `t` — with the cross-section exactly a rational quadratic. This is the same skew
  identity §6.5 already measures on a cylinder at 5.1e-19, applied one class over.
- *Cylinder or cone with rotation present.* `B` no longer vanishes, so contact is the graph
  `ruling = −A(θ)/B(θ)`: a curve, not a line, and the ruling collapse does not apply. But the curve is
  closed form at every station, so the fit is driven by exact data with no marching.

**Rung 3 — fitted, freeform only.** Rational freeform is out of scope by §3 and §6.0.2. Non-rational
freeform keeps the `(q,t)` fit of §7.1–§7.5, driven by the coefficient provider rather than by
pointwise marching. §2's adaptation — "every procedural entity of the papers becomes a
tolerance-certified B-spline fit" — remains true here and *only* here.

**What this does to §2.1's sentence.** Replace "only smooth-face grazing patches need numerical
fitting" with: *only freeform smooth-face grazing patches need numerical fitting; analytic smooth
faces are exact or 1-D-collapsed by §7.0.*

**Proof obligations before any rung-1 or rung-2 route is relied on.** These claims are derived, not
measured, and this specification asserts them normatively on that basis; each carries the check that
must pass first.

| claim | check |
| --- | --- |
| plane envelope is exactly ruled | emitted patch's degree in the ruling direction is 1, and `evPointsDeviation` of fresh off-station contact points against the emitted face is at the motion's ε_motion, not at a fit tolerance |
| sphere contact is always a great circle | `⟨n, W n⟩` measured against 0 across a rotating station, to the 5.1e-19 §6.5 already reports for the same identity |
| cyl/cone under translation is exact transport | emitted control net compared entry-by-entry against `Q_ij = A_j·P_i + b_j`, expected bit-identical |
| the collapse loses nothing | for each rung-1 and rung-2 class, the emitted patch agrees with the sampled provider's fitted patch to within the fit's own certified bound |

---

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
   **STILL THE ACTIVE PATH as of 2026-08-24, and that is the defect §6.10 closes.** §6.5.1 built
   the class; nothing consumed it; so this fixture — and every §7 and §9 measurement taken on it,
   including the 23 s of §11.3 — was produced through the approximation this note calls a fallback
   chosen by omission. The generator is one untrimmed iso-curve away (§6.5.1), so no approximation
   of this face was ever necessary.
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

**Step 7c's open items — the caps, the knit, and the volume check — are DONE as of 2026-08-24
and live-validated; see §9.4. One correction this section earned from that run: the ellipsoid
extracted at 1e-7 comes back as a 9 × 4 RATIONAL net, the exact surface, not the 99 × 49
approximation 1e-6 produced. Ask tighter and the kernel stops approximating and hands over the
real thing.** The straight-translation fixture above
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

### 8.1 The polyhedral route — built 2026-08-25, in `solidSweepUtils.fs` under its own banner

**Status: LIVE. §8.3 has the measured runs** — a cube tumbling about three axes along a helix
emits 46 of 46 patches at 2.15e-7 m. The design below is what the algebra says; §8.3 is what the
kernel said back, including the two places where this section was wrong.

The whole envelope of a polyhedral tool reduces to **one scalar function of one variable**,
`g(p, n, t) = ⟨A(t)·n, A'(t)·p + b'(t)⟩` — the same contact function §6.2 already builds on the
co-edge sample arrays. Nothing on this route evaluates a surface, fits a section, or marches.
Three facts make that so:

1. **A plane face's contact set is a straight segment at every station.** Writing `W = AᵀA' = [ω]ₓ`
   (skew, because `A` is orthogonal), `f(p) = ⟨n, W·p⟩ + ⟨n, Aᵀb'⟩ = ⟨p, n × ω⟩ + ⟨n, Aᵀb'⟩` — affine
   in the surface point. So the zero set on the plane is a LINE, its intersection with a convex face
   is a segment, and the envelope patch that segment sweeps is **exactly ruled**. Its two directrices
   are strip-function roots on the face's own bounding co-edges: one 1-D root solve per bounding edge
   per station. *(This is §7.0's plane exactness result, and realizing it is also tier 0 rung 0d.)*
2. **A straight sharp edge's funnel span is a straight sub-segment of that edge.** Both `g_left` and
   `g_right` are affine in the edge parameter, so `{s : g_left·g_right ≤ 0}` is an interval whose ends
   are the same roots the two adjacent faces read. The sheet is again exactly ruled, between the two
   funnel-boundary curves.
3. **Every combinatorial change happens at a vertex.** A face's ruling changes which edge an end
   rides only when the contact line reaches one of that face's corners; an edge's funnel opens,
   closes, or changes source only when a root reaches one of its ends. Both are roots of `g` at a
   (corner point, adjacent face normal) pair.

**Each owner is split at its OWN breakpoints, never at the sweep's.** This is the one design decision
here that is not forced by the algebra and it is worth stating why: a cube has 24 (vertex, incident
face normal) pairs, so a sweep-wide timeline runs to tens of breakpoints, and splitting all 6 faces
and 12 edges at all of them would emit a few hundred slivers for a knit that needs a few dozen
sheets. A face's type is blind to what another face's contact line is doing. The sweep-wide timeline
still comes out for free: every face-corner pair *is* a (vertex, incident normal) pair, so merging the
per-face lists is that timeline.

**The freeze-once discipline is load-bearing here, not an optimization.** Every contact function is
scanned over the same stations, and the motion enters each scan only through `A`, `A'`, `b'`. The
breakpoint pass therefore builds ONE `buildMotionStationGrid` and reads it for every pair —
97 motion evaluations for the whole pass instead of 97 per pair. Without it the pass alone is
~14,000 motion samples on a cube, and `findEvaluationSpanIndex` is a backwards LINEAR scan, so at
the 250-plus control points a 96° turn needs that is millions of interpreted iterations and a real
risk of "Too many steps".

**The API** (all in `solidSweepUtils.fs`, banner *Sharp features and polyhedral emission*):

| Function | What it answers |
| --- | --- |
| `transportCurve(motion, curve)` | §2.1(c) exact tensor transport, `Q_ij = A_j·P_i + b_j`, rational-correct with `W_ij = w_i` |
| `sharpVertexTrajectoryEdge(motion, point, t0, t1)` | the vertex's exact trajectory restricted by `clampedSegmentOperator` |
| `interpolateCoEdgePoint` / `interpolateCoEdgeSample` | point, tangent and one-sided normal at any edge parameter — exact on a straight edge with planar sides |
| `findCoEdgeStripRootsAtTime` | where one face's contact line crosses one edge at time `t` |
| `sharpEdgeFunnelSpansAtTime` | the funnel `g_left·g_right ≤ 0` in the edge parameter, ends named `left`/`right`/`startVertex`/`endVertex` |
| `buildMotionStationGrid`, `findContactFunctionRootsOnGrid` | the frozen station grid and root scan every breakpoint pass shares |
| `planarFaceCornerPairs`, `sharpEdgeEndPairs` | an owner's own breakpoint generators |
| `planPolyhedralEnvelope` | the whole plan, pure math: patches, per-owner segments, skips with named reasons, sliding faces |
| `fitRuledEnvelopePatch`, `emitPolyhedralEnvelope` | the ruled fit and its emission, `getUnstableIncrementingId` per patch |
| `polyhedralCapContactCurves` | the §9 cap trim wires: one degree-1 segment per grazing plane face |
| `summarizePolyhedralPlan`, `summarizePolyhedralEmission` | the console |

**Two numerical points that cost a rewrite each to get right.**

*Root solving is false position, not bisection.* `g` is affine in `s` on a polyhedron, so the first
secant lands on the root and the solve costs one evaluation; Illinois halving of the stale endpoint
keeps it linearly convergent when an edge is genuinely curved. Bisection to 1e-13 would be forty
evaluations for the same answer.

*The zero floor at an edge END is slope-relative, not scale-relative.* A segment boundary is refined
in `t`, so at the boundary station the end sample's `|g|` is the time residual times `dg/dt` — a
vanishing fraction of a sample spacing, but not necessarily of the whole edge's `|g|`. Judged against
the global scale that root is missed, the crossing is reported nowhere, and the patch loses a
directrix at its own last station. `STRIP_ROOT_ENDPOINT_SLOPE_FLOOR` judges the two end samples
against `|Δg|` across the last sample interval instead.

**What the route does NOT do, and each has a named refusal rather than a guess:**

- **A sliding plane face** (`SWEEP_FACE_SLIDING`): `f ≡ 0` across the face, so there is no contact
  line to rule between. Its envelope contribution is the transported face itself, which is the
  `B ≡ 0` whole-rulings case of §6.5.1 one dimension up and is not built. Note the shape of it: under
  PURE TRANSLATION a plane face's `f = ⟨n, b'⟩` is constant, so it either slides entirely or never
  grazes at all — a prism swept along a line gets its whole lateral surface from EDGES, and that is
  correct, not a gap.
- **A non-convex edge** (`SWEEP_EDGE_NOT_CONVEX`) — §3's scope.
- **More than two boundary crossings on a face** (`SWEEP_FACE_CROSSING_COUNT`) or **a funnel that
  splits in two** (`SWEEP_EDGE_FUNNEL_SPLIT`) — both mean the segment's combinatorial type is not what
  it was planned as.
- **Curved sharp edges and non-planar faces.** `interpolateCoEdgePoint` is linear between shared
  samples, exact only on a straight edge; a non-planar face is refused outright (`SWEEP_FACE_NOT_PLANAR`)
  and belongs to the §7 grazing-fit route. The filleted block of tier 3 needs both routes running
  together, which this layer is built to allow — the sharp sheets take their lateral trim from the
  same `g_side = 0` arrays a grazing patch reads.
- **The papers' loop walk.** §9.4 settled that the kernel sews a 3.4e-8 m seam with a plain sheet
  UNION, so v1 hands the knit a body set and the explicit topology walk stays unbuilt. It is the
  fallback if the union proves insufficient on a real fixture, not a prerequisite.

**Seam exactness, honestly stated.** A face patch and the sharp-edge sheet next to it share a
directrix — the same `g = 0` curve — but each samples it in its own direction, the face patch at its
`t` stations and the edge sheet at the funnel span it solves per station. The two agree to fit
tolerance, not bit-identically, and the seam gap is what the knit is asked to absorb. Making them
bit-identical needs both patches to sample the shared curve at the same `t` values; that is possible
(both are parametrized by `t` on this route) and is the first refinement to make if the knit
complains.

### 8.2 The fixture — `Sweep Rotating Cube Live Test` in `solidSweepTester.fs`

Two dropdowns, per the owner's ask: **path type** {line, circular arc, helix, free-spline S-curve}
× **rotation** {none, one fixed axis, two fixed axes, three fixed axes, follow the path tangent,
follow the tangent and roll about it}. Plus cube size, a tilt off the world axes, travel, path radius
and sweep angle, total turn, samples per tool edge, stations per contact segment, and switches for
attempting closure and keeping the tool body.

The motion is built the same way for every combination: an **analytic** `(A, A', b, b')` sampler,
Hermite-interpolated into four cubic B-splines on one shared knot vector. Every derivative is closed
form — Rodrigues for the fixed-axis composition, the quotient rule on the path's own velocity and
acceleration for the tangent frame — so nothing is finite-differenced and the stored rotation carries
the §2.1 orthonormality bound the test asserts at 1e-9. The composition derivative for
`A = R_n···R_1` is `A' = Σᵢ ωᵢ·Sᵢ·Kᵢ·Tᵢ`, which is why a two- or three-axis tumble is a genuine
multi-axis motion and not one spin viewed from a tilted frame. Span count comes off the same
`2(h·ω)⁴/384 ≤ 1e-9` argument as `turningRotationSplines`, with the path's own turning entering the
budget for the two FOLLOW kinds.

The test asserts, in order: no degenerate frame node; stored rotation drift ≤ 1e-9; 6 faces / 12 edges
/ 8 vertices all classified PLANE; at least one patch and at least one SHARP-EDGE sheet planned; no
sliding face; every planned patch emitted; the shortest ruling over the 1e-5 m sliver floor; and the
emitted sheets within 1e-5 m of **fresh envelope points at times no station used** (0.37 and 0.71 of
each segment, at both ruling ends and the ruling midpoint — a ruled patch can be right at its
boundaries and wrong between them). With closure on it then runs §9 and asserts one solid whose
volume exceeds the tool's own.

One §9 change went in with it: **a cap handed no contact curve now skips the imprint** instead of
failing it. Its contact set lies entirely on edges the copy already carries, so every one of its
faces is wholly advancing or wholly retreating and the sign classification decides them all — which
is exactly the prism-under-pure-translation case.

### 8.3 The first live runs, 2026-08-25 — **a cube tumbling about three axes along a helix, 46 of 46 patches, 2.15e-7 m**

Run over the browser session (`profiler-tools/`, unmetered), Part Studio **Rotating Cube**
(`af9d4854634dea2f548899e6`), 60 mm cube tilted 12° off the world axes, 9 stations per segment.

| path × rotation | emitted | refused | refit | skipped | deviation vs fresh points | A(t) drift |
| --- | --- | --- | --- | --- | --- | --- |
| line × one axis | 24 / 24 | 0 | 0 | 0 | **0** (under kernel resolution) | 3.3e-10 |
| helix × three axes | 46 / 46 | 0 | 0 | 0 | **2.15e-7 m** | 5.8e-10 |
| arc × two axes | 41 / 42 | 1 | 4 | 0 | 1.9e-3 m | 5.9e-10 |
| free spline × follow + roll | 55 / 63 | 8 | 14 | 2 | 8.5e-3 m | 2.5e-9 |

The two failing rows' deviations are **dominated by the missing patches, not by fit error**: a fresh
envelope point belonging to a patch that never emitted measures its distance to the nearest *other*
sheet. Where nothing is missing the number is the fit's own, and it is 2.15e-7 m.

**The zero is real and was made to earn it.** A deviation of zero is what this measurement wants,
and an instrument that cannot fail returns it for free, so two checks stand behind it. The
*control*: the tool's own centre at mid-sweep, an inradius deep inside the swept volume, measured
against the same sheets by the same call — 0.0263 m, so the call is measuring. The *convergence*:
the same fixture at 2, 3, 5 and 9 stations gives 4.217e-3, 6.602e-5, 6.705e-7 and 0, with the fit
degree rising 1, 2, 3, 3 — a sequence that reaches the kernel's own ~1e-8 linear resolution and
stops.

#### What the runs found, and both were wrong assumptions in §8.1 rather than bugs

**1. A ruled patch can be a sliver in a direction nothing was measuring.** Three arc patches were
refused with `CANNOT_MAKE_BSPLINESURFACE`, which names neither the net nor the reason. Every number
about them looked healthy — ruling 75.6 mm, travel 22.7 mm, no fold (0.3° of directrix turn), net
extent normal, no parameter degeneracy. The measurement nobody had taken was the *transverse*
one: the ruling direction and the directrix travel direction are **2.5° apart**, so the strip is
**1.006 mm wide**, and interpolating it overshoots by **0.332 mm — 33% of its own width**, pushing
control points out of the sliver. Isolated by offering the kernel the same points four ways
(`profiler-tools/net-probe.mjs`): as a control net, accepted; as corners only, accepted; scaled a
hundredfold, accepted; **interpolated, refused**. Also learned there: the kernel rejects a degree-1
multi-span net outright — a crease is not a surface it will build — so "just drop the degree" is
not available.

*The fix is that the kernel is the acceptance oracle.* Fit, offer, and on a refusal refit at half
the stations and offer again, down to bilinear, whose control points ARE the data. An overshoot
threshold was tried first and is the wrong instrument: a healthy patch on the same fixture
overshoots by 4.6–4.9% of its transverse travel and a refused one by 33%, so a constant between
them is fitted to two numbers — and set at 5% it quietly knocked **most sound patches down to
bilinear**, which the parts list showed immediately. With the kernel deciding, the arc fixture
refits 4 patches of 42 and every other patch keeps all 9 stations.

**2. An edge's funnel does not only change at its ends.** §8.1 claimed an edge's own breakpoints are
the times its funnel's boundary reaches one of the edge's endpoints. That is incomplete: the funnel
is where the two adjacent faces' strip functions differ in sign, so it also closes when its two
boundary roots **meet in the edge's interior** — the moment the edge grazes at a single point rather
than along a span. No endpoint is involved, so no endpoint root marks it, and a segment planned
across one has no funnel at its own middle stations. That is what `SWEEP_EDGE_FUNNEL_UNSTABLE`
was reporting. `sharpEdgeFunnelTransitions` now watches the funnel itself across the shared station
grid and bisects every open/close transition; it costs no motion evaluations of its own, because it
reads the frozen grid. Skipped segments went to **zero** on three of the four combinations.

#### Still open

- **One arc patch and eight free-spline patches refuse at every station count down to bilinear.**
  These are the genuinely degenerate slivers; a patch the kernel will not build in any form needs
  merging into its neighbour (§9.2's sliver policy) rather than refitting.
- **Two free-spline segments hit `SWEEP_EDGE_FUNNEL_SPLIT`** — two disjoint funnel spans at the
  segment midpoint, which v1 does not rule between.
- **The free-spline drift is 2.5e-9 against the 1e-9 bar.** A frame that follows a free spline's
  tangent turns at a rate set by how near the tangent passes the projected reference, not by any
  angle the dialog asks for; `sweepTestMotionSpans` measures that rate off the analytic sampler
  rather than estimating it, and `sweepTestFrameReference` picks the least-aligned world axis, but
  the 512-span cap still binds. The run says so rather than failing silently.
- **Closure to a solid is untested on any of these** — every run above emits the open sheet set.

#### Tooling this needed, all in `profiler-tools/`

`shot.mjs` (screenshot a tab — through the view menu, never a keyboard shortcut: Onshape's
modelling keys live on the same letters and a stray `f` commits a fillet), `net-probe.mjs` (offer
the kernel a net several ways through the eval endpoint, which needs no watcher), `parts.mjs` and
`delete-feature.mjs`, plus `ENUMS`/`QUANTITIES` parameters and a `--no-watch` mode for
`run-tests.mjs`. **A watched Part Studio is a limited resource** — "too many clients watching Part
Studios in this workspace" is a real refusal, and anyone with the document open is already
spending one — so the run writes its headline onto the tool body's NAME and every emitted sheet
carries its own identity and net numbers there too. The parts list is one API call and always
works; the notices pane is not and does not.
---

### 8.4 Closing the shell, 2026-08-25 — **one solid, 45 faces, 1.009817e-3 m³, closed by enclose**

A cube tilted 12° off the world axes, 60 mm, turned about one axis along a 200 mm line, closes to
a single solid body. The lateral shell is 45 sheets, the caps are the tool's own retreating halves
at each end, and the census over the assembled shell reads **89 seam pairs of 183 free edges, 5
unmatched, worst pair 1.6e-8 m**.

#### One shared time partition, and only then are the seams seams

Patches were cut on **their own owner's breakpoints**. Two patches meet along a whole boundary
curve, and cut on different partitions they meet along PARTS of each other's boundaries instead -
so neither edge has a partner, and a knit has nothing to sew even where the surfaces coincide.
Measured on the line fixture: **40 of 108 free edges had no partner at all**.

Every owner is now cut on the union of every owner's breakpoints, and each extra cut is a
sub-interval of one the owner already had, so no patch spans a change in its own contact type;
sub-intervals where an owner has no contact are dropped by the midpoint tests that always decided
that. The cost is patch count - the line cube goes 24 → 42, the helix 46 → 82.

That alone is not enough. A clamped tensor fit's v = 0 boundary has control points equal to the
stage-one **u** interpolation of column 0, so it depends on that column's data and on the u
PARAMETERS - and averaged chord parameters are taken over the grid's own columns, which two
neighbouring patches do not share. The same curve was fitted twice, differently. The u parameters
are now the stations' own normalized times, which both sides agree on without knowing about each
other. This is §7.4's island-split finding in the other direction, and it moved the worst matched
seam from **5.9e-6 m to 1.6e-8 m**.

Accuracy improved with it rather than in spite of it: the helix at three axes went from 46 of 46
patches at 2.15e-7 m to **82 of 82 at 1.5e-8 m**, at the kernel's own resolution.

#### The union must be told where the seams are

`opBoolean` UNION over sheets is a SEW where it can find coincident edges and an INTERSECTION
where it cannot - and envelope patches meet tangentially, so a union left to find its own seams
attempts a tangent surface-surface intersection for every neighbouring pair, which is the kernel's
worst case. It does not refuse. It takes the regeneration down.

The standard library never asks for that: `joinSurfaceBodies` in boolean.fs hands `opBoolean` an
explicit `matches` array of coincident one-sided edge pairs, with `recomputeMatches` and
`eraseImprintedEdges`, and builds it by matching edge MIDPOINTS within
`TOLERANCE.booleanDefaultTolerance` (1e-5 m). `matchShellSeamEdges` does the same, one kernel call
per free edge and the pairing decided on distances between those points - which makes the census
its own diagnostic: an edge with no partner is a hole, and it is the only thing standing between a
certified shell and a solid.

With matches in hand the union still declined this shell and `opEnclose` closed it. That is a
measurement about the 5 unmatched edges, not a nuisance.

#### The ceiling is per feature evaluation, and it is not an operation count

Emitting the sheets, capping the ends and knitting the shell each run fine from a clear start and
take the regeneration down when stacked. The failure is not a refusal and not a catchable throw:
the feature dies whole, every body it made is rolled back, the parts list comes back empty and the
message is "Error regenerating" with no stage named. Where the boundary falls moves with the
fixture - the line cube at 42 patches reaches the trim inside the sweep; the helix at 82 does not
reach the contact wires.

What it is NOT, each ruled out by measurement:

- **not an operation count** - the helix survives 84 operations at the cap stage and the line dies
  at 48;
- **not the interpreter's step budget** - the cheapest settings the dialog allows die identically;
- **not which bodies are handed over** - naming the shell by an independent query, excluding the
  caps, and skipping the union each change nothing;
- **not the statement form** - `try(op(...))`, the expression form every boolean in boolean.fs
  uses, does not catch it either, and neither does reading the operation's status back.

So the pipeline is three features: **emit → cap and trim → knit**. Nothing has to cross the
boundary but the sheets themselves, because the motion is deterministic in the dialog's own
numbers and `sweepTestCubeFixture` rebuilds it from a shared parameter predicate. This is a
finding about the platform, not a design anyone would choose, and a v1 feature will have to be
frugal enough to do all three in one evaluation.

#### Still open

- **The helix's cap contact wires are fatal even from a clear start.** Two grazing faces per end,
  four `opCreateBSplineCurve` calls; the cap-copy stage before them passes. Emitting the segment as
  a cubic instead of a degree-1 line, and skipping zero-length segments, both leave it fatal.
  Without caps the helix shell knits to 0 solids, as it should.
- **Five free edges on the line fixture still have no partner**, and the union declines the shell
  because of them. The cap's kept-face boundary is made of whole tool edges while a lateral
  patch's t-boundary is a funnel SPAN of one - a sub-segment - so where a funnel does not cover a
  whole edge the two do not correspond one to one. The cap needs imprinting with the contact set
  even where that set lies along its own edges.
- **Building the caps in the closure feature rather than in the sweep changes the census** on the
  same fixture, 89 pairs / 5 unmatched against 77 / 29, and the second closes to two solids rather
  than one. Not yet explained.
- **A refit breaks the seams it touches.** A patch refitted at half the stations after a kernel
  refusal no longer shares its neighbour's u knots or degree; the line fixture refits two of 42.
  A refit has to be shared across a segment, or avoided.

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

### 9.4 Implementation (2026-08-23) and its first run (2026-08-24) — **LIVE PASS, the first solid**

The layer lives in `solidSweepUtils.fs` and its live test (`sweepSolidAssemblyLiveTest`, the §7.7
ellipsoid under the same straight translation) in `solidSweepTester.fs`.

**FIRST RUN: PASS, 14 of 14 checks, one solid body (2026-08-24, owner, UI).** The console, in
full, because every number in it answers something this section had open:

| | measured |
| --- | --- |
| tool extraction at 1e-7 | degree 3×3, **9 × 4 net, RATIONAL** — the kernel returned the EXACT surface, not an approximation |
| fit, 5 × 61 | deviation **4.638e-8** (all of it q; t at **1.59e-16**, the straight-translation exactness check again) |
| face normal vs the ellipsoid's outward direction | **1** — the convention the classification assumes is the kernel's |
| cap classification, both caps | signs **[−0.1109, +0.1163]**, 1 kept / 1 deleted, imprint 1 → 2 faces with 1 splitting edge |
| seam gap, both caps | **3.387e-8 m** |
| knit | **closed by UNION** — 3 sheets in, 1 body out, 1 solid |
| output | 3 faces, 2 edges, min face area 8.29e-3 m², min edge 0.208 m — no slivers |
| volume | 7.446161131e-4 m³ against the Minkowski anchor 7.446163362e-4 — relative error **3.0e-7** |
| deviation vs fresh off-station envelope points | **4.207e-8 m** |
| §2.3 ledger | motion 0 + faceExtract 1e-7 + fit 4.638e-8 + knit slop 3.387e-8 = **1.802e-7 m** |
| build time | **66 s** |

**Three things the run settled that no amount of reasoning would have.**

1. **The kernel sews a 3.4e-8 m seam with a plain sheet UNION.** `opBoolean` closed it; the
   `opEnclose` fallback was never needed. That is the question §9.4 was written around, and the
   answer is that this pipeline's seams are inside the kernel's sewing tolerance — at least at
   this width. The ceiling is still unmeasured.
2. **The ε_faceExtract floor does not apply to this tool, because there was no approximation.**
   Asked at 1e-7, extraction returned a 9 × 4 RATIONAL net — the exact ellipsoid, the same
   behaviour §7.8 found on the elliptical wall (ask for the rational form and the kernel hands
   back exact geometry; force non-rational and you get a 58-row approximation of it). So the
   caps and the fit were built against the *same* exact surface and the extraction term
   cancelled. The seam gap is then almost exactly the fit's own q error, which is what the two
   numbers show: 3.39e-8 against 4.64e-8. **The ledger's faceExtract term is the tolerance
   ASKED FOR, not the error achieved**, and it dominates a sum it has no business dominating —
   the one refinement §2.3's ledger still wants.
3. **The classification convention is right way round**, measured rather than assumed: outward
   agreement exactly 1, and the two caps' sign pairs are mirror images to 14 digits.

Left over from the run: 66 s for one ellipsoid on one straight translation, which is two thirds
of the way to §12.3's stop-work trip-wire. §11.1 has the call-count analysis of where it goes.



**The layer, function by function.** `sweepCapPlacement` (motion sample → `Transform` plus a
rigidity defect), `emitToolCapCopy` (one `opPattern` per cap so each copy answers to its own
`qCreatedBy`), `fitBoundaryContactCurve` / `kernelContactCurve` / `emitContactWire`,
`imprintContactWires`, `faceInteriorTangentPlane`, `envelopeContactSign`,
`capFaceClassification`, `trimCapToEnvelopeSide`, `knitSweptShell`, `measureSeamGap`,
`summarizeSolidQuality`, and the orchestrator `assembleSweptSolid` / `summarizeSweptSolid`.

**Four decisions the §9 sketch did not settle.**

1. **Cap placement is re-orthonormalized, not the raw A(t).** The sketch says `opPattern` with
   `motionSnapshotTransform` — raw kernel frames, exactly rigid — and that is right where
   station frames exist. The fixtures (and any caller holding only a stripped motion) have no
   station frames, so `sweepCapPlacement` takes an `evaluateMotionSample` map and runs modified
   Gram-Schmidt on its rotation columns. The motion module measures 5.85e-7 of orthonormality
   drift on its worst path — larger than the seam gaps this layer works to, so a cap placed by
   the fitted matrix would be sheared by more than the thing it is trying to meet. The distance
   the orthonormalization moves a unit tool axis rides out as `rigidityDefect`, which is exactly
   the ε_motion term of §2.3.

2. **The contact wire is the fit's own boundary CONTROL row**, not a curve re-interpolated
   through the boundary sample row. The fit's rows are stations, its columns are q, and the
   station direction is clamped, so control row 0 *is* the patch's t₀ boundary curve. Both the
   wire and the patch go through the same closed-clamped conversion, so they are the same curve
   bit for bit rather than two curves that agree to a fit tolerance.

3. **Two fallbacks, both reporting which route ran.** `trimCapToEnvelopeSide` takes
   `opDeleteFace{leaveOpen}` first (the sketch's route) and, only if that *throws* — which
   leaves the copy untouched, so the classified face queries still resolve — extracts the kept
   faces with `opExtractSurface` and drops the copy. `knitSweptShell` takes `opBoolean` UNION
   first and, if the shell does not close, asks `opEnclose` for the region the sheets bound.
   The second knit route is the one §10 already names for closing an interval sub-sweep; it
   asks a different question of the kernel than sewing does, and a body that encloses but does
   not sew is a *measurement about the seam gap*, not a nuisance. Both routes are named in the
   report (`trimmedBy`, `closedBy`), so a run always says how it closed.

4. **Grazing cap faces are refused, not assigned.** A face whose |⟨n, v⟩| falls under
   `CAP_FACE_SIGN_RELATIVE_FLOOR` (1e-6) times the local speed belongs to neither half — the
   tool slides along it — and `SWEEP_CAP_FACE_GRAZING` names the count rather than guessing a
   side. This is the §6.4 sliding audit arriving at emission: a cylinder swept along its own
   axis will trip it, and the answer for that case is a sliding-face policy, not a coin flip.

**The seam gap has a floor the §9 sketch did not account for: ε_faceExtract.** The caps are
copies of the EXACT kernel tool; the patch is fitted against the APPROXIMATED extraction of the
same face. Their difference does not cancel at the seam the way it cancels when a patch is
measured against its own extraction, so the extraction tolerance is a hard floor under the gap
the knit is asked to absorb — a 1e-6 extraction cannot produce a seam better than about 1e-6
however finely the loop is sampled. The live test therefore exposes BOTH terms as UI knobs
(contact loop samples, face extraction tolerance, defaulting to 61 and 1e-7) and prints the
whole §2.3 ledger, so one run measures the trade and a second can move along it without a
re-paste.

**Boundary snapping is NOT needed at this width** (the run above sewed), but it stays the
identified remedy if a future tool's seam does not close, not
a finer fit: read the imprinted cap edge back with `evApproximateBSplineCurve`, make it knot
compatible with the patch's q direction, and replace the patch's boundary control row with it.
The patch's boundary then *is* the cap's edge to the read-back tolerance, and the patch interior
moves by no more than the fit deviation it already carries. The obstacle to doing it now is that
a contact loop crossing the tool's own seam imprints as more than one edge, so the read-back is
a chain rather than a curve.

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

### 11.1 Where the 66 s goes — a call count, for the profiling pass to confirm or refute

The §9.4 run is the first end-to-end timing the project has: **66 s** (owner, UI readout — the
only instrument this project trusts, §15). No per-function attribution exists yet; that is tier 4
item 9, which the owner has pulled to the front of the queue. This subsection is the hypothesis
that pass should test first, and it is arithmetic off the code, not a measurement.

**Counting the evaluator calls in that run** (5 stations, q = 61, so 5 fit loops plus 4 held-out
certification loops):

| where | per loop | note |
| --- | --- | --- |
| `tubeSeedOnMeridian` | ~90 | 33 meridian samples, 48 bisections, ≤12 polish |
| `marchWrappingSectionLoop` | ~730 | ~244 steps to wrap one period (step = period/4q), each a tangent gradient plus 2-3 corrector gradients |
| `resampleWrappingLoopSection` | ~490 | 122 samples, each corrected and re-residualled |
| | **~1300 gradients + ~370 lifts** | × 9 loops ≈ **12k** |

plus ~305 orientation samples on the grid and ~800 `invertPointOnSurface` calls in
certification at ~4 iterations each — call it **~15,000 order-2 surface-derivative evaluations,
each paired with a motion sample** (itself four spline-curve evaluations).

66 s / 15,000 ≈ **4.4 ms per composite step**, which is ~5 spline evaluations, which is
**~0.9 ms per spline evaluation** — indistinguishable from probe 2's measured 1.1 ms
(§13). So the strong prediction is that essentially ALL of the 66 s is spline-evaluator call
overhead, that the section march's predictor-corrector is the single biggest consumer of it, and
that nothing in the caps/knit/assembly layer is worth profiling at all (it makes a few dozen
kernel calls, once).

If that holds, the lever is §11's own conclusion — a grid-batched evaluator, not a faster scalar
one — and the section march is where to apply it first: it evaluates one point at a time along a
curve whose next point it already predicts.


### 11.2 The five levers, ranked — the next session's menu

Written 2026-08-24 off the §11.1 count, before any profiling. Confirm with the UI profiler
first: §11.1 predicts lever A alone takes 66 s to roughly 20 s, and if it does not, the model is
wrong and D is the whole answer.

**A. Hoist the motion sample out of every fixed-t loop. Pure refactor, biggest single payoff.**
Not an estimate — a fact about the code: `evaluateEnvelopeGradientPointwise` and
`liftContactPoint` each call `evaluateMotionSample(strippedMotion, t)`, which is FOUR
`evaluateBSplineCurveDerivatives` calls, and **every section march, corrector, resample and lift
holds t fixed** — fourteen entry points take a `tGlobal` and loop under it. So four of the five
spline evaluations behind every marched point recompute a constant. Give the pointwise
evaluators an overload taking an already-computed sample and compute it once per station.

**B. Stop re-marching whole loops for certification. ~44% of the loop work.** The run marched
nine full loops: five for the grid and four held-out ones at midpoint stations, purely to
attribute deviation to the t direction. Attribution needs a maximum over enough samples, not a
complete 61-point loop with its own resample — a dozen points per held-out station would do.

**C. Decouple the march step from q.** `stepSize = period / (4 · qCount)` puts 244 predictor
steps in a loop that is then resampled to 122 points, each re-corrected onto f = 0 anyway. The
march owes the caller two things — stay on the branch, and close — and both survive a coarser,
curvature-adaptive step. The resample is what sets accuracy, not the march.

**D. Batch the evaluator where the points are known in advance.** §11's own conclusion, and the
only lever that attacks per-call overhead rather than call count. The march is sequential and
cannot batch, but `resampleWrappingLoopSection`'s 122 samples and certification's ~800 point
inversions can each be handed to the evaluator as a set.

**E. Measure the kernel's sewing ceiling and then drop q.** The passing run sewed a 3.4e-8 m seam
at q = 61; the ceiling is unknown, so q = 61 may be an order of magnitude more than the knit
needs. One run each at q = 31, 21, 16 finds the first q whose seam does not close — that halves
or quarters every loop in the pipeline and hands §9 the tolerance figure it is missing. Cheapest
experiment on this list: the knob is already in the test's dialog.


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


### 11.3 The optimization pass, 2026-08-24 — **66 s → 23 s, measured**

**MEASURED (owner, UI compute time, 2026-08-24): the §9.4 assembly fixture went from 66 s to
23 s on unchanged inputs, 14 of 14 checks, and every printed number came back bit-identical** —
fit deviation 4.6377850035287854e-8, worst t deviation 1.5704757352569647e-16, seam gap
3.3866437795138505e-8 m, volume 0.0007446161131418343 m³ (relative error 2.9954322556848367e-7),
solid deviation 4.206624663898588e-8 m. That is the pass's whole claim demonstrated at once: 2.9×
faster and not one digit different. §11.1's model — that essentially all of the 66 s was
spline-evaluator call overhead in the section march — is confirmed rather than refuted.

Still short of §11's bar. 23 s is not sub-second and is not tens of milliseconds; what this pass
bought is headroom away from the ~100 s stop-work trip-wire and a confirmed cost model to aim the
next pass at. A second round of changes (item 6 below) went in after the measurement and is
itself **untimed**.

What follows is the change list and the arithmetic behind each, kept because it is what the next
profiling pass reasons from.

Everything here is in `solidSweepUtils.fs` and, except where marked, changes **no answer at
all** — the same recurrences in the same order, or work that was being done twice being done
once. `sweepLeanEvaluatorLiveTest` is the gate that holds that claim.

**1. Lever A, the motion hoist — done, by freezing rather than by new overloads.** §11.2 proposed
giving the pointwise evaluators an overload that takes an already-computed motion sample. That
would have meant threading a new argument through fourteen entry points and every helper below
them. What went in instead: `evaluateMotionSample` now stamps its result with the `t` it was
taken at, and **returns a sample handed back to it unchanged**. A fixed-t function then freezes
once at the top and passes the sample down *in the stripped motion's place*, and every call site
underneath reads exactly as it did. Twenty-one functions freeze this way; the wrong-`t` case
throws rather than silently answering with the wrong station's state.

What it removes: four `evaluateBSplineCurveDerivatives` calls per pointwise evaluation, which on
the §9.4 run's ~12k gradients and ~3.3k lifts is on the order of 60,000 curve evaluations
replaced by 36.

**2. Lever D, but scalarized rather than batched — a lean evaluator inside the module.** §11.2's
D and §12.3's 9a both argue the per-call overhead dominates and that batching is the answer.
Batching is still the right answer for the grid-shaped callers and is not done. What is done is
the cheaper half of the same argument: splineRefinementUtils' general evaluator is written in
Vector algebra, so its inner statement `pointSum + blendValue * controlPoint` is two std operator
calls — each with a precondition and its own three-iteration interpreted loop — for three
multiply-adds of real work, and `dot` allocates a `@subArray` per call. One order-(2,2)
evaluation on the ellipsoid's 9×4 rational net runs about a hundred of those.

`leanSurfaceDerivatives` does the identical arithmetic on scalar accumulators: same A2.3 basis
recurrence (transcribed operation for operation), same summation order, same A4.4 quotient rule.
It computes only the **triangle** uOrder + vOrder ≤ 2 rather than the full rectangle, which is
exactly what the envelope gradient, the orientation sample and point inversion read, and A4.4 for
a triangle entry reads only triangle entries. It hoists the control-point ROW out of the inner
loop, which the general evaluator cannot do without changing its accumulation order and which
here does not, because for a fixed column the terms still arrive in ascending uBasisIndex.

The envelope gradient, the orientation sample, the lift, the contact function and
`invertPointOnSurface` were rewritten onto it, with their Matrix and Vector algebra written out
in components in the same association order.

**3. The corrector's last evaluation was being thrown away.** Not on §11.2's list, and worth
about a fifth of the march on its own. Every Newton corrector in the module ends by evaluating
the gradient at the point it is about to return and testing the residual — and its caller then
evaluates the gradient at that same point, because the march wants the tangent there and the
resample wants the residual there. `correctedUvAndGradient` / `correctedSectionUvAndGradient`
hand the converged gradient back, and the four callers take it. When the loop exhausts its
iterations instead of converging the gradient is returned as `undefined`, because then it belongs
to the point before the final step.

**4. Value-only reads stopped paying for second derivatives.** `f` reads no second derivative of
the surface, so the meridian scan and bisection in `tubeSeedOnMeridian` and the residual read in
`resampleWrappingLoopSection` now go through the order-1 triangle. Same number to the last bit —
the lean evaluator computes S, S_u and S_v the same way at either order.

**5. Two O(n²) polyline builds and one duplicated function.** `marchSectionCurve` and
`marchClosedSectionLoop` were growing their polylines with `append` in the loop, which copies the
whole array every step; both now preallocate against `maxSteps` and `subArray` at the end, as the
wrapping march already did. `correctUvOntoSection` and `correctOntoSection` were the same
function written out twice against the two knot-domain helpers — which return the same four
fields — and are now one. (`fitSurfaceKnotDomain` and `knotDomainOfStrippedSurface` are *also*
duplicates of each other; that one is still open.)

**6. A second round, after the 23 s measurement and therefore UNTIMED.** Three things the first
round left on the table, all still exact:

- *The gradient was computing four square roots per call that its hot callers never read.*
  Checked rather than assumed: the correctors, all three marches, all three resamples and the
  anchor polish read `value`, `uDerivative` and `vDerivative` and nothing else — only the
  tangency audit, its refinement and the branch-time search touch `tDerivative` and the two
  Cauchy–Schwarz scales. `evaluateSectionGradientPointwise` returns the three-field form and
  skips two matrix-vector products, two dot products and four `norm`s; the six-field
  `evaluateEnvelopeGradientPointwise` is unchanged and still serves the three callers that need
  it. Both come from one function with a `wantTimeTerms` flag, so they cannot drift apart.
- *`findSectionCrossingOnRay` was reading `.value` off a full order-2 gradient* for its scan and
  bisection. `f` reads no second derivative; it now goes through the order-1 evaluator.
- *Arc length was going through std Vector algebra.* The three `cumulativeLengths[i - 1] +
  norm(lifted[i] - lifted[i - 1])` accumulations — one per marched point, three sites — ran an
  interpreted `operator-` loop and a `dot` that allocates a `@subArray`. `distanceBetweenTriples`
  does the same arithmetic inline. The `sqrt` stays: this is the one measure in the module that
  genuinely needs a magnitude.

**The `norm` → `squaredNorm` audit (owner's prompt, 2026-08-24).** Every `norm` call in
`solidSweepUtils.fs` was classified. **No valid hot-path substitution remains**, and the reason
is that the comparisons had already been converted: `gradientNormSquared` in all three marches
and all three correctors, `closureImageShift`, `nearestImageSquaredDistance`,
`pointToSegmentSquaredDistance`, `longestChord`, `curveParameterNearestPoint`'s scan,
`invertPointOnSurface`'s `currentSquaredResidual` and `invertPointOnSurfaceFromGrid`'s lattice
all compare squared quantities already. What is left divides into three kinds, none of which can
be squared: **arc length** (the three accumulations above, plus the chord parameterizations),
where the running sum of distances is the answer; **normalization** (the marches' unit tangent,
`fitPatchOrientationAt`'s projection cosine, `outwardNormal`), where the square root is the
operation; and **reported figures** (`worstSeamGap`, `rigidityDefect`, `tangentialResidual`,
`worstDifferenceAlignment`, the singularity sine), where squaring would silently change a
number the spec quotes. The one adjacent finding the audit *did* produce is item 6's first
bullet — the hot path's four square roots were not the wrong *function*, they were work whose
result was discarded, and deleting them beats squaring them.

**What was NOT done, and why.**

- *Lever B, the held-out certification loops.* Still four full q = 61 loops marched purely to
  attribute deviation to t — about 44% of all loop work. It is not a one-line change: the
  held-out samples are inverted against `fitSurface.vParameters[qIndex]`, so a smaller
  certification q breaks that index correspondence and needs its own seeding rule (nearest fit v
  parameter at `round(j · qCount / certificationQ)`, or a grid seed). Worth doing, needs
  designing, and it weakens a certification rather than speeding up an identical answer — so it
  belongs in a session that can measure the result.
- *Lever C, decoupling the march step from q.* Exposed as `marchStepsPerSample` on
  `tubeLoopSamples` and `islandLoopSamples`, **defaulted to 2, which is what the code already
  did**. The march and its corrector are roughly three quarters of a station's surface
  evaluations and scale straight off this number, so 1 is the obvious experiment — but it is an
  experiment: a coarser polyline seeds the resample's corrector worse, buying some of the saving
  back, and it widens the closure test's radius. Turn it down and measure; do not assume.
- *Lever E, the sewing ceiling.* Untouched. Still the cheapest experiment on the list, and still
  the one that would tell §9 what tolerance it is actually working to.
- *Batching (D proper).* `resampleWrappingLoopSection`'s samples and certification's point
  inversions are still one call per point. The lean evaluator makes each call cheaper; it does
  not make them one call.

**The regression gate.** `sweepLeanEvaluatorLiveTest` in the tester checks all three exactness
claims against the code they replaced: the lean triangle against splineRefinementUtils' general
rectangle on four self-contained nets plus the extracted ellipsoid (asserted **equal**, not
close); the rewritten gradient against the pre-rewrite Vector-and-Matrix formula, which is kept
in the tester precisely because the module no longer has it; and a frozen motion against the same
evaluation re-sampled from the splines every time. It also checks the rational path against
geometry rather than against another implementation — the fixture circle's radius and its
tangent's perpendicularity to its own radius, which no weights-dropping evaluator can fake.

**What the next measuring run should report.** The §9.4 fixture again, for item 6's round, plus
`sweepLeanEvaluatorLiveTest`'s verdict. After that the queue is: lever C's knob at 1, lever E's
q sweep, then lever B — and only then 9a's four-way evaluator split, which is now a question
about what remains rather than about where the time went.


### 11.4 The first real profile, 2026-08-24 — the evaluator is 90%, and half of THAT is allocation

Onshape's FeatureScript profiler is now scraped rather than read off a screenshot:
`profiler-tools/report.mjs` arms it and writes the ranked per-function table with call counts.
Mechanism and its traps in `docs/ONSHAPE_PROFILER_SCRAPING.md`. Times below are **inclusive**,
aggregated across call sites, from a 31.1 s profiled regen of the §9.4 fixture (the same build
that measures 23 s in the UI — profiled time runs ~35% slow and is a relative measure only).

| seconds | share | calls | µs/call | function |
| --- | --- | --- | --- | --- |
| 27.94 | 90% | 20,712 | 1349 | `leanSurfaceDerivatives` |
| 22.70 | 73% | 9 | — | `tubeLoopSamples` (12.6 s in the 5 grid stations, **10.1 s in the 4 held-out ones**) |
| 20.25 | 65% | 3,872 | 5230 | `correctedUvAndGradient` |
| 19.90 | 64% | 10,862 | 1832 | `envelopeGradientAt` |
| 15.10 | 49% | 11 | — | `marchWrappingSectionLoop` |
| **14.80** | **48%** | **41,424** | **357** | **`leanBasisDerivatives`** |
| 8.48 | 27% | 11 | — | `resampleWrappingLoopSection` |
| **7.22** | **23%** | **850,379** | **8.5** | **`makeArray`** |
| 5.95 | 19% | 793 | 7507 | `invertPointOnSurface` |
| 5.37 | 17% | 7,193 | 746 | `leanSurfacePoint` |

**What it overturns.** §11.3 assumed the lean evaluator's cost was the control-net blending —
roughly 500 scalar multiply-adds against ~60 for the basis tables. It is the other way round:
`leanBasisDerivatives` is 14.8 s of `leanSurfaceDerivatives`' 27.9 s, and the blend and quotient
rule together are the remaining 13.1 s. The arithmetic count was right and irrelevant, because
**allocation dominates**: 850,379 `makeArray` calls at 8.5 µs is 23% of the whole regen, and
about 92% of those calls are inside `leanBasisDerivatives`, which allocates **nineteen arrays per
invocation** (a row-of-rows `ndu`, a row-of-rows derivative table, two distance arrays, and two
fresh ping-pong coefficient rows per basis function). Even the order-0 path pays it: a point
evaluation costs 746 µs against the order-2 evaluation's 1832 µs, and it does no derivative work
at all.

The interpreter's own primitives confirm the shape: `Assignment` 9.65 s over 17.7 M, `Block`
4.22 s over 4.78 M, `For loop` 3.06 s over 1.43 M, `Value access` 1.50 s over 3.52 M. Sub-µs
each — the cost is the count, and the allocation loops are a large part of the count.

**The levers, now ranked by measurement rather than by argument.**

1. **Flatten `leanBasisDerivatives`' allocations. Exact, and the largest free win.** One flat
   `(degree+1)²` array instead of a row-of-rows `ndu`; one flat derivative array; the ping-pong
   coefficient buffers hoisted out of the per-basis-function loop and re-zeroed instead of
   reallocated. Nineteen allocations per call becomes about four. Same recurrence, same
   summation order, so `sweepLeanEvaluatorLiveTest` still has to pass unchanged.
2. **A values-only path for order 0.** The 7,193 `leanSurfacePoint` calls build the full `ndu`
   lower triangle they never read; the simple values recurrence needs three arrays and half the
   writes.
3. **Lever B is now measured: the held-out certification loops are 10.1 s, a third of the
   regen.** §11.3 estimated ~44% of loop work and left it undone for want of a seeding rule.
   That rule is now clearly worth writing.
4. **Lever C.** The march is 15.1 s against the resample's 8.5 s, so `marchStepsPerSample = 1`
   attacks the bigger half. Still an experiment, and now a measurable one.

**What is NOT worth touching**, on the evidence: `findEvaluationSpanIndex` (0.71 s),
`applyRowsToTriple` (0.55 s over 72,591 calls), the orientation certification (0.68 s), and the
whole caps/knit/assembly layer, which does not appear in the table at all — exactly as §11.1
predicted.


### 11.5 The autonomous loop, and what it bought — 31.1 s → 23.7 s profiled

`profiler-tools/report.mjs --push` is now the whole cycle in one command: it writes the local
`solidSweepUtils.fs` into the Feature Studio tab over the browser session, reads it back and
refuses to profile unless the tab matches byte for byte, arms Profile, harvests the ranked table
and diffs it against a named earlier run. **The "these files must be pasted by hand" constraint
was an MCP limit, not an Onshape one** — `put_featurescript` carries the file as a tool parameter,
so 512 KB will not fit in a message, while a session POST body never passes through a model.
Onshape keeps a microversion per edit, so the tab's history stays intact (owner, 2026-08-24).

Three changes, each measured against the run before it:

| run | profiled total | change | what changed |
| --- | --- | --- | --- |
| `current` | 31.1 s | — | the state §11.4 profiled |
| `after-alloc` | 26.3 s | **−15.4%** | flat `leanBasisDerivatives` + values-only order-0 path + certification inversion tolerance |
| `after-fuse` | 23.7 s | −9.9% | unrolled U collapse, A4.4 fused into the V blend |
| `after-fix` | **25.2 s** | +6.3% | certification inversion tolerance corrected 1e-6 → 1e-9 |

**Cumulative −19.0%, with the build's output bit-identical to the pre-optimization run** — fit
deviation 4.6377850035287854e-8, worst t deviation 1.5704757352569647e-16, seam gap
3.3866437795138505e-8 m, volume 0.0007446161131418343 m³, solid deviation 4.206624663898588e-8 m,
VERDICT PASS on 14 checks. The last row is a deliberate step BACKWARDS in time to buy that: see
§11.6. The per-function attribution confirms each change hit what it aimed at rather than moving
cost around:

- `makeArray` 7.22 s → 2.42 s over 850,379 → 274,085 calls. The flattening removed 68% of the
  build's allocations.
- `leanBasisDerivatives` 14.80 s → 10.71 s (357 → 296 µs per call).
- `invertPointOnSurface` 5.95 s → 2.35 s, 7.5 → 3.0 ms per call, on the same 793 calls — the
  parameter tolerance was buying nothing, exactly as the stationarity argument said.
- `leanSurfaceDerivatives` 27.94 s → 20.70 s (1349 → 1143 µs per call), and its call count fell
  20,712 → 18,104 because the inversion now needs fewer Newton steps.
- `leanSurfacePoint` 5.37 s → 2.75 s, from the values-only basis path.
- One regression, accepted: `If` +0.44 s, from the `isRational` guards now inside the unrolled
  collapse. Hoisting the branch out would duplicate the loop body; it is not worth the fork yet.

**Where the remaining 23.7 s sits.** `leanSurfaceDerivatives` is still 87%, of which
`leanBasisDerivatives` is 10.71 s and the collapse-and-blend is ~10 s; `Assignment` is 8.68 s
over 16.4 M, which is the interpreted floor rather than a target. Per-call cost is now within
about 2x of the raw statement count, so **further gains have to come from fewer CALLS, not
cheaper ones** — which is levers B and C, and both change answers:

- **Lever B**, the held-out certification loops: `tubeLoopSamples` is 19.17 s over 9 calls,
  4 of which are held-out stations.
- **Lever C**, `marchStepsPerSample`: the march is 13.20 s against the resample's 6.78 s.

**The loop now reads the verdict too.** `println` output lands in the **FeatureScript notices**
pane, which is a navbar flyout (`NavbarController.toggleNoticePane()`), closed by default, and
which collects only while open and only while the Feature Studio monitors or profiles a Part
Studio. `report.mjs` opens it before arming and prints the test's own numbers alongside the
timings. That closes the correctness half: a change that alters an ANSWER is now visible in the
same run as one that alters the clock.


### 11.6 Is Newton the weak link? Measured: no — the projection is at 2.81 evaluations against a floor of 2

Raised 2026-08-24: replace Newton-Raphson with something that picks its method by geometric
context. The profile answers it, and the answer is that the SOLVER is not where the time goes.

**What the corrector actually costs.** 3,872 projections onto f = 0 per build — 2,715 from the
section march (13.0 s), 1,146 from the resample (4.65 s), 11 from the meridian seed. Across them,
`wrappedEnvelopeGradient` runs 10,862 times: **2.81 gradient evaluations per projection**.

The floor for this structure is **2**: one evaluation to compute the step, one to establish that
the residual is now inside tolerance — and no method can skip the second, because convergence is
not knowable without evaluating. So the corrector already runs at about 71% of the best any
solver could do here, and a perfect replacement could recover at most 29% of corrector
evaluations, which is roughly 12% of the build.

**And Newton is the right method for this shape.** One equation in two unknowns with an analytic
gradient, started from a predictor that is O(step²) away from the curve. The step taken is the
minimum-norm Newton step, `uv -= f * grad(f) / |grad(f)|²`, which is the exact projection for an
underdetermined system. Trust regions and line searches exist for hard global problems; this is a
point being pulled 1e-3 back onto a smooth level set it is already nearly on. Broyden or a secant
update would make iterations 2+ cheaper (an order-1 evaluation with a stale gradient instead of
order-2) at the cost of slower convergence — worth about 1.4 s in the resample, where the caller
does not need a fresh gradient, and worth nothing in the march, which reuses the corrector's
final gradient as its tangent.

**A worked example of why the verdict half matters.** §11.5's certification-inversion change was
argued from stationarity: at a closest point the distance is stationary in the parameter, so a
parameter error e costs only (e |S_u|)² / 2R. The loop measured it as a 3.6 s win. It was also
a silent 3.5x regression — the reported fit deviation moved from 4.638e-8 to 1.622e-7 — and it
still passed all 14 checks, because the bar is 1e-5.

The argument used the wrong denominator. For a target lying essentially ON the surface at
distance d, the distance along the surface is sqrt(d² + s²) ≈ d + s²/2d, so the error scales with
the DISTANCE, not the radius of curvature: s²/2d with d ≈ 5e-8, not s²/2R with R ≈ 3e-2. Six
orders of magnitude, which is exactly the gap between the prediction and the measurement. The
requirement is s ≪ d, so the tolerance is 1e-9 rather than 1e-6, the deviation returns
bit-identical, and 2.3 s of the original 3.6 s survives.

The general lesson, and the reason the notices pane was worth cracking: **a tolerance argument
about a quantity that is nearly zero cannot be checked by reasoning about the geometry it sits
on.** Only the run knows.

*One idea checked and rejected*: making the corrector's convergence test a value-only (order-1)
evaluation. It reads as a free win and is a net loss — 1.81 of the 2.81 evaluations are
non-converged, so a cheap pre-check gets paid for on every one of them and only saves on the last.

**Where the geometric-context argument does land.** *(Header corrected 2026-08-24: it read "and it
is already tier 1 item 3", which is how this lever came to be owned by nothing. Tier 1 item 3's
done-when was a tester cross-check against `evaluateAnalyticContactDirect`; it was satisfied and the
item closed on 2026-08-24 without a single solver call being routed. The lever is now §12.3's own
item — see §6.10.)* The march's
step is `period / (4q)` — a density inherited from the fit's sampling, not from the surface's
curvature. Two consequences, both measured:

1. *Adaptive-step continuation is the principled version of lever C.* Let the corrector's own
   iteration count drive the step: converge in one, grow it; take three, shrink it. That IS
   choosing by geometric context, and it self-tunes instead of guessing a constant. It changes
   the marched polyline, so it has to be paired with a tangent-aware resample — Hermite
   interpolation and arc length from the tangents the march ALREADY computes at every point,
   instead of straight chords — which is what decouples resample accuracy from march density
   properly rather than by turning a knob down.
2. *The real answer is not to solve numerically at all for this face.* The tool here is a
   SURFACE OF REVOLUTION, and §6.5.1's REVOLVED class collapses the two-parameter contact solve
   to a one-parameter closed form. The module already carries that machinery for the five
   analytic kinds — `analyticContactStructure`, `solveAnalyticContactCurve`,
   `analyticMeridianPolynomial`, the trig-polynomial root solver — and the profile now prices
   what recognizing one more class is worth: **87% of the build is the spline evaluator serving
   a numerical solve that a revolved face does not need.**

So the ranking the measurement supports is: **the contact provider contract of §6.10** (orders of
magnitude, for this and every revolved tool), then lever B (8.57 s of 23.7 s in held-out
certification), then adaptive stepping with a tangent-aware resample, and only then anything about
the solver itself. Note what consequence 2 does NOT say and was read as saying: recognizing the
class is not the work. The class was recognized on 2026-08-23 for five kinds and 2026-08-24 for two
more, and the 87% did not move, because recognition without routing changes nothing.

---

### 11.7 Figures relocated out of code comments (2026-08-24)

Kept here because the comments that carried them were rewritten to describe behaviour only
(AGENTS.md line 56, now enforced by `tools/fsLint.py`):

- **Self-test workload counters, baseline profile 2026-08-22:** the eager factor build measured
  13 s. The counters in the tester's Test 5 are sized against that figure and split per stage so
  the profiler attributes factor builds, lazy product builds, and materializations separately.
- **Interpreter step budget, 2026-08-22:** two certified fits plus a refinement loop in one
  feature trips "Too many steps", which is why fit refinement is a separate feature from the main
  self test.
- **Kernel refusals, 2026-08-23:** `opCreateBSplineSurface` answers CANNOT_MAKE_BSPLINESURFACE
  for a whole-island net (both u-boundary rows collapsed to poles, v closed) in both periodic
  declarations, and for both folded halves in both declarations — four refusals, all caught.
- **Island fit, 2026-08-23:** a clamped v direction on a closed section loop leaves a seam kink
  that dominates certified deviation, 6.4e-3 in q against 2.7e-5 of true envelope error; a
  periodic v direction removes it.
- **Seam smoothness sampling, 2026-08-23:** sampling either side of a seam instead of at the
  domain end measures the curve's own curvature across the gap — a 2e-6 parameter gap on the
  circle fixture reads 1.3e-5 of tangent change on a curve that is smooth there.
- **MCP harness, 2026-08-22:** any evaluation notice makes the harness return notices instead of
  the console, so the throw-guard check that provokes one is off by default.

---

## 12. Module layout and build order

**THREE FILES (owner, 2026-08-23).** The stack was developed as seventeen modules and is now
consolidated into the three it is meant to ship as. Nothing else is added: a new layer goes into
the utils file under its own banner, a new test goes into the tester and an old one is cycled out.

```
custom-features/
  solidSweepUtils.fs      — the whole library, one element: Bernstein arithmetic (§6.0), motion
                            (§4), the envelope function (§6.1–6.2), analytic contact (§6.5), the
                            funnel solver with its masks and certified census (§6.3–6.4,
                            §6.7–6.8), orientation and the fold certificate (§6.6), the
                            degeneracy detectors (§6.9), extraction with caps/knit/assembly
                            (§5, §9), and fitting with certification (§7). Imports std plus the
                            published splineRefinementUtils and nothing else. 11.8k lines.
  solidSweepTester.fs     — every self test, live test and fixture, one element (§14). Imports
                            solidSweepUtils. Meant to be CYCLED: a test earns its place by
                            defending an invariant current work can still break. 7.5k lines,
                            32 features.
  solidSweep.fs           — NOT BUILT. The feature itself: UI, orchestration, diagnostics mode
                            (debug-draw contact curves / funnel samples), per-stage timings.
                            Deliberately empty until §9 has a passing run — a feature built on
                            an unvalidated assembly path would be guesswork.
```

`tools/consolidateSweepStack.py` performed the merge and is kept for the record of how the split
was computed: a declaration belongs to the library if it is reachable from an exported library
declaration, everything else a test feature reaches is a fixture, and anything reachable from
neither is dead. That run reported 312 library declarations, 104 fixtures, 32 features, **zero
dead declarations and zero layering leaks**, and promoted exactly three helpers to `export`
because a fixture calls them across the element boundary. The merge was verified by declaration
parity (462 in, 462 out, none duplicated), by full identifier resolution in both files, and by
re-deriving the §9 assembly payload — 406 blocks, byte-identical but for the three renamed throw
prefixes and the one promoted export.

Retired in the same pass: `swSweepProbes.fs`. Every probe question is answered and the answers
are §13; the file also carried its own copies of three extraction helpers, which is exactly the
duplication this pass exists to end.

**Reading the dated entries below and elsewhere in this spec:** they name the module a piece of
work was done in — `swEnvelopeFit.fs`, `swSweepEmit.fs`, `swFunnelSolver.fs` and the rest. Those
files no longer exist; their contents are the correspondingly banner-headed sections of
`solidSweepUtils.fs`, and their test features are in `solidSweepTester.fs`. The names are left in
place because they are what the findings were recorded against.

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
   **7f caps, knit, and the volume check — DONE 2026-08-24, LIVE PASS.** §9.4 has the layer, the
   four decisions the §9 sketch left open, and the run: 14 of 14 checks, one solid body, closed
   by a sheet UNION at a 3.4e-8 m seam, volume within 3.0e-7 relative of the Minkowski anchor.
   **Step 7 is complete.** Its live test is `sweepSolidAssemblyLiveTest` in
   `solidSweepTester.fs`, with two UI knobs (contact loop samples, extraction tolerance) and the
   §2.3 ledger printed.
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

> **STATE, 2026-08-24 (latest first).**
>
> -2. **THE QUEUE IS REWRITTEN ON ONE RULE (owner, 2026-08-24): every done-when names a SOLVER OR
>    EMISSION PATH, never a tester agreement.** The rule exists because the opposite cost this
>    project its largest lever twice over. §6.0 strategy 1 specified closed-form analytic contact
>    from the start; §6.5 built it for five classes on 2026-08-23 and recorded in its own opening
>    that "nothing consumed `record.analytic`"; §6.5.1 built two more on 2026-08-24; §11.6 measured
>    that **87% of a build is the spline evaluator serving a solve a revolved face does not need**
>    and ranked routing it first — and an audit of the tree on 2026-08-24 found the layer still has
>    no caller outside `solidSweepTester.fs`. The mechanism was bookkeeping, not disagreement:
>    §11.6 filed the routing under tier 1 item 3, whose done-when was a cross-check against
>    `evaluateAnalyticContactDirect`, and closing that item retired the lever's only owner. **Tier 0
>    below is the routing work, and it is the front of the queue ahead of tier 3.** §6.10 is its
>    contract and §7.0 its emission rungs.
>
> -1. **Tier 1's items are built, but tier 1 item 3 did NOT deliver what §11.6 was pointing at.**
>    `REVOLVED` and `EXTRUDED` are live (§6.5.1, PASS 19/19 and 30/30), and the run measured that the
>    recovered generator IS the kernel face's own — worst point distance 0 m, worst normal cross
>    product 1.3e-15 over a grid, and a contact curve the kernel's own normals confirm to 1.0e-13.
>    What it did not do is route a single solver call: "every existing fixture, the timed one
>    included, still takes its old path byte for byte" was written as a compatibility guarantee and
>    is in fact the defect. Recognition without routing changes nothing, which is why the 87% did not
>    move when five classes were recognized and did not move again when two more were.
>
> 0. **THE OPTIMIZATION PASS IS MEASURED: 66 s → 23 s, bit-identical output** (owner, UI compute
>    time, 2026-08-24). The §9.4 fixture on unchanged inputs, 14 of 14, every printed figure
>    matching the pre-optimization run digit for digit. Lever A is in (by freezing motion samples
>    rather than by new overloads), a lean scalar surface evaluator replaced the Vector-algebra
>    one on every hot path, the Newton correctors stopped throwing away the gradient their caller
>    immediately recomputed, and two O(n²) polyline builds and one duplicated corrector went.
>    §11.1's model is confirmed: the 66 s was spline-evaluator overhead in the section march. A
>    SECOND round then went in and is **untimed** — the section-only gradient (the hot callers
>    never read `f_t` or the two scales, so four `norm`s per call were being discarded),
>    order-1 value reads on the ray search, and scalarized arc length. 23 s is still nowhere near
>    §11's bar; levers B, C and E are all open, and C is now a knob (`marchStepsPerSample`)
>    defaulted to today's value.
> 1. **THE FIRST SOLID EXISTS.** The §9 assembly test passed live, 14 of 14 checks: one solid
>    body, volume within 3.0e-7 relative of the Minkowski anchor, 4.2e-8 m off fresh envelope
>    points, no slivers, **closed by a plain sheet UNION at a 3.4e-8 m seam**. Numbers and the
>    three things it settled are in §9.4. Tier 2 is closed — item 5's ledger printed all four
>    terms in the same run.
> 2. **The consolidated stack builds clean** (owner, 2026-08-24). Both failures its first paste
>    produced were IMPORTS, not code: the merged header had lost the `export import` the motion
>    tester needs for its enum dialog parameter, and both elements needed `geometry.fs` rather
>    than `common.fs` alone — `ProjectionType`, which the imprint uses, is re-exported by
>    projectCurves.fs and splitpart.fs and by nothing common.fs reaches. **The lesson worth
>    keeping: an unresolved name degrades to a missing OPERATION, not to a compile error**, so it
>    presented as a cap that failed to draw and a closure that then failed — geometry symptoms
>    from an import cause.
> 3. **Performance is the only thing left on this fixture, and it is the front of the queue**
>    (owner): 66 s to sweep one ellipsoid along one straight line, two thirds of the way to the
>    ~100 s stop-work trip-wire. §11.1 counts the calls and predicts the whole of it is
>    spline-evaluator overhead in the section march; §11.2 ranks the five levers, of which the
>    first is a pure refactor that the code proves is worth ~80% of the march.
>
**Tier 0 — the contact provider contract: route by class, sample last. THE FRONT OF THE QUEUE
(owner, 2026-08-24), ahead of tier 3.** §6.10 is the contract, §7.0 the emission rungs. Every
done-when here names a production path; none is satisfiable by a tester agreement.

0a. **Carry the class to the solver.** `record.surfaceClass` is read nowhere past extraction today, so
    nothing downstream can route on it. Add provider selection at the point where the solver is
    handed a face, and make the recognizer the DEFAULT rather than a five-argument opt-in (§6.5.1).
    *Done when:* a face's provider is chosen from its class on the production path, and the
    three-argument `extractToolFaceRecords` no longer silently routes a `REVOLVED` face to
    approximation.

0b. **The analytic provider answers the census and the sections.** Wire the six questions of §6.10 for
    the seven analytic classes. The census is the easier half — it already consumes `patchFactors`
    rather than a surface, so it needs a second producer, not a rewrite; the sections replace
    `marchSectionCurve` (≤400 steps × an 8-iteration corrector) with `solveAnalyticContactCurve`.
    *Done when:* a `REVOLVED` tool face completes census and section extraction with **zero calls to
    `leanSurfaceDerivatives`**, asserted by a counter rather than inferred from a clock.

0c. **Rung 1 — exact emission, no fit.** Cylinder and cone under pure translation contact along whole
    FIXED rulings, so §2.1(c)'s `Q_ij = A_j·P_i + b_j` emits the lateral patch by control-point
    arithmetic. *Done when:* the emitted net is **bit-identical** to the transport arithmetic, and the
    face carries no fit, no certification, and no sample grid at all.

0d. **Rung 2 — collapsed emission.** Plane faces emit an exactly-ruled patch (degree 1 in the ruling
    direction) between two directrices taken from the co-edge pass, not from a new grid; spheres emit a
    circle family. *Done when:* for each class the emitted patch agrees with the sampled provider's
    fitted patch inside the fit's own certified bound, and the plane route consumes **no samples
    beyond the co-edge arrays §6.2 already builds**.

0e. **The coefficient provider for freeform.** Connect the half of §6.0 strategy 2 that is currently
    test-only — `isolateBernsteinRoots`, `bernsteinExcludesZero`, `bernsteinGridExcludesZero` — behind
    the same contract. Screening via `bernsteinGridRange` is already live in the census.
    *Done when:* a non-rational freeform face reaches its fit grid through coefficient root isolation
    with no pointwise marching, and §6.0 strategy 3's stated remit — "final Newton polish of isolated
    roots, the (q,t) fit grids on live patches (hundreds of points, not tens of thousands)" — is what
    the profile actually shows.

0f. ~~**Generator recovery by iso-curve.**~~ **DONE 2026-08-24.** One `opCreateCurvesOnFace` at
    `skipTrim : true`, the meridian being the iso-curve constant in the PERIODIC direction (measured:
    DIR1 gave the circumferential circle at theta spread 2.51 rad, DIR2 the meridian at exactly 0).
    Four heuristics deleted. It also needed something this queue did not anticipate: the ellipsoid's
    generator is an `Ellipse`, so the conic refusal had to go - `exactConicArcSpline` converts a conic
    arc to an EXACT rational quadratic (affine image of the circle construction, weights untouched,
    split at 90 degrees), and `conicEdgeAngularSpan` picks the arc by the edge's own midpoint because
    endpoints alone bound two arcs and on a meridian those are the r >= 0 and r <= 0 halves. Cost:
    13 ms for both extractions. ORIGINAL TEXT:  Replace the `opPlane` + `opIntersectFaces` recovery with one
    `opCreateCurvesOnFace` at `skipTrim : true` (§6.5.1). *Done when:* the recovered generator matches
    the plane-cut generator §6.5.1 already validates, the four placement heuristics are deleted, and
    the ellipsoid fixture extracts its exact ellipse instead of a 9 × 4 net.

0g. **The two proof fixtures.** These are the item's acceptance, not decoration. **HALF DONE
    2026-08-24: the ellipsoid contact-curve A/B passed, 8 checks — 12.9x on the solve, 92.8% off the
    regen, agreement 2.28e-17, and `leanSurfaceDerivatives` absent from the analytic route's profile
    entirely (see the measurement in §6.10).** What remains is the SOLID half, which needs 0a-0c
    wired: today's A/B calls the analytic layer from a test, so it proves the route is right without
    making the solver take it. And the box fixture is untouched.
    - *Ellipsoid A/B.* Identical inputs to §9.4 — same tool, same straight translation, same
      Minkowski volume anchor — through the analytic provider instead of the sampled one. The baseline
      to beat is recorded: **23 s, ~15,000 order-2 evaluations, volume relative error 3.0e-7, envelope
      deviation 4.207e-8 m, seam 3.387e-8 m.** *Done when:* the same answers come back with **zero
      tool-surface evaluations**, and the clock is read off Onshape's own compute-time readout.
    - *Box swept along a line.* Six planes, six exactly-ruled patches, no fitting anywhere. This needs
      the sharp-feature layer (tier 3 item 6) for a closed solid, so it is scoped to the six lateral
      patches until that lands, and is then re-run whole.

**Tier 1 — foundation: the §6/§7 gaps from the §12.1 audit. CLOSED 2026-08-24** — all three items
done, the fourth deferred to tier 4 by the owner. Tier 2 closed the same day.

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
3. ~~**Rational freeform — the two profile-driven analytic classes.**~~ **DONE 2026-08-24 (§6.5.1,
   two live PASSes: 19 of 19 and 30 of 30) — BUT SEE TIER 0. This item built the classes and did not
   route them; §11.6 was pointing at the routing, and closing this item retired that lever's only
   owner. Its done-when was a tester cross-check, which is the mistake tier 0's rule now forbids.** The done-when is met on both counts: a revolved-spline
   face and an extruded-spline face each produce contact curves through the analytic layer, agreeing
   with `evaluateAnalyticContactDirect` at **1.4e-17 / 4.2e-17** — tighter than the 1.1e-16…5.6e-16
   §6.5 reports for the other five classes — and **neither face touches
   `evApproximateBSplineSurface`**, which the live test asserts on the record rather than assuming.
   The load-bearing check is cross-class: a rational quarter-circle generator reproduces the SPHERE
   class exactly (2.8e-17) through algebra it shares nothing with. Numbers, the two design decisions
   the build had to make (unnormalized normals; whole rulings when `B ≡ 0`), and **two corrections to
   probe 8's recipe** — the revolve's cutting plane must pass through the face's box centre, and the
   chord rule for the extrusion direction is wrong because `C(u) + v·d` is translation-invariant in
   both parameters — are all in §6.5.1. What is NOT closed: a generator that comes back as a
   `Circle` or `Ellipse` struct is refused rather than converted, and the face falls back to
   approximation with the refusal recorded. Probe 8 and this run both measured the conic case coming
   back as a rational B-spline instead, so it is a hole rather than a common case.

*Former tier-1 item 4, the lean evaluator, is* **deferred to tier 4 item 9 (owner, 2026-08-23)**:
the cost shows up on worst cases, not on the fixtures the queue is built from, and the reckoning is
cheaper once consolidation has collapsed the solvers into fewer functions. It is not cancelled and
the §11 arithmetic still stands; tier 4 carries the trip-wire that pulls it forward.

**Tier 2 — emission: the first solid**

4. ~~**Caps, knit, assembly (§9).**~~ **DONE 2026-08-24 — the first solid body the project has
   ever emitted.** Live PASS, 14 of 14 checks, on the §7.7 ellipsoid under a straight
   translation: one solid, 3 faces, no slivers, volume relative error 3.0e-7 against the
   Minkowski-sum anchor, 4.207e-8 m deviation against fresh off-station envelope points, closed
   by `opBoolean` UNION at a 3.387e-8 m seam. Full console and findings in §9.4. What is NOT yet
   done under this item: the same fixture on a CURVED path (the straight one was chosen because
   only it has a closed-form volume anchor). The consolidated tester element reproduces the run
   once its imports are right (§12).

5. ~~**Error-budget ledger (§2.3).**~~ **DONE 2026-08-24, in the same run.** The assembly test
   prints `ε_motion + ε_faceExtract + ε_envelopeFit + knit slop` and their sum: 0 + 1e-7 +
   4.638e-8 + 3.387e-8 = **1.802e-7 m**, and the sum bounds that run's measured output deviation
   (4.207e-8 m), which is the item's own done-when. **One refinement it earned:** the
   faceExtract term is the tolerance *asked for*, not the error *achieved* — this run's
   extraction returned the exact rational surface, so its true term was ~0 while the printed
   term dominated the sum. A ledger that overstates by two orders of magnitude on the easy case
   will not be trusted on the hard one; the term should be measured (extracted surface against
   the kernel face, `evPointsDeviation` on a sample) rather than quoted.

**Tier 3 — the feature**

6. **Sharp features + the topology walk** — build order step 8. **THE POLYHEDRAL HALF IS LIVE
   2026-08-25** (§8.1, §8.2, §8.3): a cube tumbling about three axes along a helix emits 46 of 46
   patches at 2.15e-7 m against fresh envelope points, and a line sweep emits 24 of 24 at a
   deviation under the kernel's own resolution, with the convergence sequence and an interior-point
   control standing behind that zero. An arc sweep emits 41 of 42 and a free-spline sweep 55 of 63;
   both shortfalls are named in §8.3 and both deviations are dominated by the missing patches
   rather than by fit error. What it contains: the funnel criterion on the shared `g_side` arrays, the
   exact `transportCurve`, sharp-vertex trajectory edges by clamped segment extraction, per-owner
   contact breakpoints, exactly-ruled emission for both plane faces and straight sharp edges, cap
   trim wires, and a `Sweep Rotating Cube Live Test` driven by a path dropdown × a rotation
   dropdown. Nothing is measured yet. The route evaluates NO surface at all — the whole envelope of
   a polyhedron is the scalar contact function on the tool's own edges — which is why it also
   discharges tier 0 rung 0d for the plane class.
   *Still open under this item:* the degenerate slivers a kernel will not build at any station
   count (1 patch on the arc fixture, 8 on the free-spline one — these need MERGING into a
   neighbour per §9.2, not refitting), `SWEEP_EDGE_FUNNEL_SPLIT` on two free-spline segments,
   closure to a solid on any of these, curved sharp edges (`interpolateCoEdgePoint` is linear
   between shared samples, exact only on a straight edge), sliding plane faces
   (`SWEEP_FACE_SLIDING`, whose contribution is a transported face rather than a ruled patch),
   bit-identical rather than fit-tolerance seams between a face patch and the sharp sheet beside
   it, and the papers' loop walk — deliberately unbuilt while §9.4's plain sheet UNION holds.
   *Done when:* a filleted block — smooth faces, convex sharp edges, 3-face vertices — emits one
   solid, which needs the §7 grazing route and this one running together on the same tool.
7. **Feature UI, detectors, live tester, publish chain** — build order step 9; `solidSweep.fs`,
   the third file of §12, does not exist yet. The §10 detectors wire in here as always-on
   gates, and §14's fixture matrix {sphere, cylinder, box, filleted block} × {line, arc, helix,
   free spline, cusp-inducing arc} is the acceptance suite — including the brute-union *rate*
   check, the one test that distinguishes a true envelope from a fine discretize-and-blend.

**Tier 4 — after the foundations are complete (owner, 2026-08-23)**

8. ~~**Collapse the test scaffolding into the core utilities plus the feature.**~~ **DONE
   2026-08-23, PULLED FORWARD BY THE OWNER.** The tier note said "not before tier 3 is done",
   on the argument that the per-module testers are what stand behind the numbers in this spec.
   The consolidation kept that argument whole by MERGING rather than deleting: all 32 test
   features and all 104 fixtures survive in `solidSweepTester.fs` (34 features as of 2026-08-24,
   with S6.5.1's two added), so every recorded number is
   still reproducible, and only `swSweepProbes.fs` — answered questions, plus duplicated copies
   of three extraction helpers — was retired. Seventeen files became two (§12). The third file,
   `solidSweep.fs`, stays empty until §9 has a passing run.
9. **The performance reckoning — 66 s → 23 s MEASURED 2026-08-24 (§11.3), output bit-identical;
   a second untimed round followed.** Levers A and a scalarized half of D are in, exactly; B
   (held-out certification loops, ~44% of loop work, needs a seeding rule before it can shrink),
   C (`marchStepsPerSample`, knob added, default unchanged) and E (the kernel's sewing ceiling,
   still the cheapest experiment on the list) are open. 9a is now a question about what remains
   rather than about where the time went — §11.1's model was confirmed. Start from §11.1's call
   count and §11.2's ranked levers; the parts below stand unchanged. Tier 1 item 4 lands here,
   widened. It runs
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

## 13. Live probes (complete; the probe file is RETIRED — these answers are the record)

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
     whose direction comes from `evFaceTangentPlanes`. The chord rule this probe proposed for
     picking the ruling direction does not work and was replaced during the build by a
     normal-invariance test — see §6.5.1.
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

**Optimization regressions have their own gate.** `sweepLeanEvaluatorLiveTest` holds every
exactness claim §11.3 makes against the code it replaced — the lean evaluator against
splineRefinementUtils' general one (asserted *equal*, not close), the scalarized envelope
gradient against the Vector-and-Matrix formula it was rewritten from, and a frozen motion sample
against the same evaluation re-taken from the splines. The pre-rewrite formula lives in the
tester on purpose: a rewrite that claims to change nothing needs the thing it changed *from* kept
somewhere runnable, and the module is not that place.

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
