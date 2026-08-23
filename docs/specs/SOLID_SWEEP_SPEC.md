# Solid Sweep — Feature Spec

Status (2026-08-21): **design stage, nothing built.** A generalized solid sweep: sweep a solid
tool body along a path under a rigid motion and emit the swept volume's **true envelope
boundary** as a watertight solid — not a discretized stack of transformed instances blended
together. This is the feature class Onshape lacks entirely, SolidWorks restricts to convex
analytic revolve/extrude tools (cut-only), and Fusion implements via the
Adsul–Machchhar–Sohoni framework on procedural surfaces.

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

**2.2 The kernel is the topology oracle; FS Newton is the corrector.** Global root finding
(how many contact loops exist at a station, which faces they cross, when loops are born/die)
is the expensive, failure-prone part of a pure-FS solver. `opCreateIsocline` with angle 0 on a
scratch instance computes the **exact instantaneous contact curve for translational motion**
and a high-quality seed + component census for general motion (the fixed-direction error is
`~|ω|·R_tool / |b'|`, well inside Newton's basin for `≲ 0.3`). Per-station isoclines run in a
`startFeature`/`abortFeature` scratch scope (the `curvePattern.fs` discipline); FS Newton then
corrects every sample against the true velocity field. PROBE FINDING (2026-08-21): a face
sitting at isocline angle 0 *everywhere* (cylinder wall viewed along its axis, flat cap viewed
across it — exactly the sliding case of §6.4) has no discrete isocline and can fail the whole
call, so the oracle must run **per face, after the sliding-face audit**, never on all faces of
the body at once. Harvested samples map back to the tool frame via the inverse instance
transform (verified live in probe 6, including scratch-scope cleanup). For rotation-dominant stations
(`|b'| → 0`) the oracle degenerates; there the solver falls back to coarse-grid seeding at the
first affected station plus temporal continuation (each station seeds from the previous).

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
- **Tool restrictions:** G¹ faces plus **convex** sharp edges (`evEdgeConvexity`):
  CONCAVE → `SWEEP_CONCAVE_EDGE` (concave edges contribute nothing generically and near-always
  produce self-intersection — sharp-features paper §8); VARIABLE → reject in v1 (later: split
  at convexity transitions). Sharp vertices with at most 3 incident faces (papers' assumption).
- **Simplicity:** required and checked (§10); non-simple input → named error with the
  offending t-range via `regenError(message, faultyParameters, entities)`.
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

**Periodic faces:** `evApproximateBSplineSurface` loops are documented-unreliable on
periodic faces. Strategy: detect with `evFacePeriodicity`; rewindow the seam
(`rewindowPeriodicSurfaceDirection`) so the funnel band avoids it, else pre-split the face at
an isoparametric curve (`opSplitFace` with an isocurve edge tool) before extraction. Component
flood-fill treats `u mod period` explicitly.

---

## 6. Envelope solver — `swEnvelopeMath.fs` + `swFunnelSolver.fs` (both pure)

**6.0 Solving strategy: coefficients before samples** (recalibrated by probe 2, 2026-08-21:
a pointwise surface-normal evaluation on the module's rational+units path measured **~1.1 ms
flat** — on a degree-1×3 surface — so the original blind-sampling budget of ~31K pointwise
evaluations is infeasible at 50–70 s. The solver is coefficient-first; pointwise evaluation is
the last resort, not the workhorse):

1. **Analytic faces solve in closed form — zero de Boor.** `evSurfaceDefinition` yields exact
   plane/cylinder/cone/sphere/torus for most real tool faces. Plane: `f` is independent of
   (u,v) — contact is all-or-nothing per face (sliding audit), contributions come only from
   its edges. Cylinder: `N` depends only on θ, and `f` is a degree-2 trigonometric polynomial
   in θ, linear in z — per-station contact curves are closed-form `atan2` solutions.
   Sphere/cone/torus reduce the same way (quadric-on-surface / per-meridian 1D forms).
2. **Freeform faces: put `f` itself in Bernstein form and operate on coefficients.** Using the
   UNNORMALIZED normal `S_u × S_v` (identical zero set on a regular surface),
   `f = ⟨A(t)·(S_u × S_v), A'(t)·S + b'(t)⟩` is a tensor polynomial in (u,v) per Bézier patch,
   and — because the motion columns are splines in t — an explicit trivariate spline whose
   coefficients are computed ONCE by control-net arithmetic (Bernstein products; rational
   surfaces contribute the numerator polynomial, same trick at higher degree). Then:
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
3. *Funnel components:* oracle census (§2.2) + adaptive coarse sign grid over `D × I`
   (point-in-trim-loop by 2D winding number); sign-change flood fill unions with the boundary
   curves from step 2. Interior components not touching the prism boundary (grazing islands)
   get their t-extremes refined by 3-variable Newton on `(f, f_u, f_v) = 0`.
4. *Sections:* per component and fitting station `t_j`, march the p-curve `f(·,·,t_j) = 0`
   between boundary anchors; resample at fixed fractions q of section arc length; re-Newton
   each resampled point onto `f = 0`. Output: an on-funnel (q,t) grid + its lift.
5. *Orientation:* one `∂f/∂t` sign per input co-edge orients all its generated co-edges
   (alternation rule, papers §5.3); outward normal of every grazing patch is the transported
   `A(t)·N`.

**6.4 Degeneracy audit** (named violations; v1 = detect and report):

- `SWEEP_NOT_GENERAL_POSITION_SLIDING` — `|f| < ε_f` over a whole patch for an interval: the
  face slides along itself (cylinder along its own axis, plane parallel to translation).
  **This is the common case on real parts, not a corner case.** Benign subcases handled:
  non-contributing faces skipped; stationary-profile faces route their boundary to the edge
  machinery. The rest reject with a targeted message ("split the motion", "nudge the path").
- `SWEEP_FUNNEL_TANGENT_TO_SLICE` — `|f_t| → 0` along a p-curve (swiveling curve crossing a
  station): section extraction splits at the tangency; marching is arc-length based and
  unaffected.
- `SWEEP_EDGE_SWEEP_SINGULARITY` — velocity parallel to a sharp edge's tangent (papers'
  Lemma 15); reject in v1.

---

## 7. Fitting & certification — `swEnvelopeFit.fs` (pure)

**7.1 The rectangle insight.** Parameterizing each funnel component by (q,t) turns the generic
component into **exactly the unit square**: q ∈ {0,1} iso-edges *are* the co-edge envelope
curves, t iso-edges *are* cap contact curves. Trim curves are absorbed into the
parameterization, so `opCreateBSplineSurface`'s single-closed-loop constraint is satisfied by
untrimmed rectangles — no boundary curves, no `opReplaceFace`. Components whose boundary
alternates lateral/cap arcs more than four times are split at loop vertices into
rectangle-able strips (strips share boundary sample arrays → exact seams). Fallback for
pathological components: fit an extended rectangle and trim topologically (EDIT_SURFACE
§2.3.1: imprint fitted boundary wires with `opSplitFace` edgeTools, then
`opDeleteFace{leaveOpen: true}`). Grazing islands: PROBE FINDING (2026-08-21) —
`opCreateBSplineSurface` accepts a bicubic patch with one boundary row fully collapsed to a
point (probe 4, all collapse fractions through 1.0 accepted), so the island policy is plain
pole-collapsed patches; the split-at-t-extremes fallback and `SWEEP_ISLAND_UNSUPPORTED` are
kept only as a safety net should a pole patch later misbehave downstream (knit, split).

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
   conservative stand-in and documented as such.)
2. *Contact topology events:* loop birth/death/merge per station, free from the oracle census.
3. *Global collision screen:* spatial hash (cell ≈ 5·ε_fit) over all contact-curve samples;
   close pairs from far-apart t flag global self-intersection.
4. *Sliding audit* (§6.4).

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
| Oracle | ~30 ops (isoclines, scratch scope) | — |
| Vertices + co-edges | 0 | ~4K curve/normal-spline evals |
| Funnel grid | 0 | ~3K order-2 surface evals |
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

---

## 12. Module layout and build order

```
custom-features/
  solidSweep.fs           — feature UI, orchestration, diagnostics mode (debug-draw contact
                            curves / funnel samples), per-stage timings
  swMotionSpline.fs       — §4   (imports splineRefinementUtils)
  swEnvelopeMath.fs       — §6.1–6.2, pure
  swFunnelSolver.fs       — §6.3–6.4, pure
  swSweepTopology.fs      — §8 topology walk, pure combinatorics
  swEnvelopeFit.fs        — §7, pure
  swSharpFeatures.fs      — §8 sharp geometry + trim domains
  swSweepEmit.fs          — §5 extraction, §2.2 oracle, §9 emission/knit/certification
  swSweepProbes.fs        — the live probes (§13, complete)
  bernsteinPolynomialUtils.fs — §6.0 Bernstein coefficient arithmetic (pure, dependency-free,
                            standalone so the published splineRefinementUtils never needs a
                            republish while the sweep is being refined)
  + one *Tester.fs per pure module, + solidSweepLiveTester.fs
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
5. Envelope math + funnel solver + testers, end-to-end on analytic fixtures. Includes the
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
6. Fitting module + the first certified live envelope patch.
7. Smooth-only watertight solid (e.g. an ellipsoid along a spline), volume/deviation checks.
8. Sharp features + the topology walk.
9. Feature UI, detectors, the live tester, publish chain.

After v1: twist / lock modes / closed paths, then the trimming work of §10.

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
6. **Isocline oracle** — RESOLVED with a finding (2026-08-21): viable on oblique directions
   (wires produced on a transformed scratch instance, samples harvested, scratch cleaned up);
   degenerate when a face sits at isocline angle 0 everywhere (cylinder wall along its axis,
   caps across it) → the oracle must run per face after the sliding audit (§2.2, §6.4).
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

- **The lean evaluator's real throughput** — re-run the throughput probe against the
  unit-stripped non-rational evaluator once it exists; the §11 budget assumes several-fold
  under the measured 1.1 ms. The probe can now run through the MCP harness: the eval response
  carries no server-side timing, so the autonomous method is client wall-clock of
  `test_feature` at N and 2N evaluations, differenced, over repeated runs; the owner's UI
  compute-time readout remains the gold standard.
- **Motion spline degree** — cubic vs quintic; decide from the motion tester's drift data.
- **Promoting `editSurface.fs`'s private emission floor to a shared module** vs replicating
  it (three consumers after this feature: editSurface, free-form deformation, sweep).

**Resolved:**

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
