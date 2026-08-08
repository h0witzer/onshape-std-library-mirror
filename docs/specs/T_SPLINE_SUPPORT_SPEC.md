# T-Spline Surface Support — Design Spec

Status (2026-08-08): **Design stage. Nothing is built.** Companion to
[SPLINE_REFINEMENT_UTILITY_SPEC.md](SPLINE_REFINEMENT_UTILITY_SPEC.md) — that module's Layers
1–2 are the numerical engine this pipeline extracts through, and its periodic machinery,
evaluator, and emission discipline all carry over directly. This document exists so the design
conversation (2026-08-08) is not lost between now and whenever this work starts; it records the
architecture, the mathematics at star points, and two decisions that are **committed now, not
open**:

1. **Full arbitrary-topology scope.** Star points (extraordinary vertices, valence ≠ 4) are in
   scope from the first design pass. The semi-regular subset ships first because it depends on
   less, not because it is the goal: *"I don't make a habit of limiting scope in service of
   something lesser."*
2. **Karčiauskas–Peters G² caps at star points.** The G¹ Gregory-patch route is rejected
   outright as this project's clamp-and-report — a friendlier name for settling. *"Shortcuts or
   approximations are an admission of failure."* See §2 for what "no approximations" precisely
   means at a point where finite polynomial exactness is mathematically impossible, because the
   answer is not hand-waving — it is a redefinition that makes the shipped construction exact.

Prerequisite state in the refinement module (all verified live per its spec): Layers 1–2, the
operator layer, Bezier decomposition, the periodic window machinery, and
`evaluateBSplineSurfacePoint`.

---

## 1. What is being built, and why the kernel does not forbid it

A T-spline is a spline surface whose control mesh (a **T-mesh**) permits T-junctions: a control
row may terminate partway across the surface instead of running border to border. Consequences,
in order of why they are wanted here:

- **Local refinement.** Control points can be added where detail is needed without propagating
  a full isoparametric line across the surface — exactly the per-patch adaptivity that
  SPLINE_REFINEMENT_UTILITY_SPEC §9.1.1 names as its honest ceiling ("adaptivity is per-U-span
  and per-V-span, never per-patch. Genuine local refinement needs T-splines and is out of
  scope"). This spec is that out-of-scope line item.
- **Sparse storage.** A T-mesh stores control data only where the shape has detail. As feature
  parameters in an Onshape document, that is the difference between a tractable and an
  intractable persisted representation.
- **Watertight multi-patch shapes** with far fewer control points than the equivalent global
  NURBS net, including closed and arbitrary-genus topology once star points are supported.

Parasolid has no native T-spline type, and never will from our side of the API. That is not a
blocker; it is the industry-standard architecture: every commercial T-spline system (Fusion 360's
sculpt environment being the visible one) evaluates and edits the sparse form, then **converts
to a complex of NURBS patches and knits** when the B-rep kernel needs the body. Conversion is
exact in the semi-regular regions — it is knot insertion, nothing else — so the kernel sees
ordinary B-spline faces and the document stores the sparse truth.

The pipeline in one line: **T-mesh (sparse, persisted) → exact NURBS extraction (regular
regions) → exact subdivision rings + G² caps (star points) → `opCreateBSplineSurface` per patch
→ `opBoolean` UNION / `joinSurfaceBodiesWithAutoMatching` into one sheet or solid body.**

### 1.1 Patent posture

The foundational Sederberg patents (filed 2003–2004 around the original T-spline and T-NURCC
papers, e.g. US 7,274,364) reached their 20-year expiry in 2023–2024. The two 2003/2004 SIGGRAPH
papers and everything downstream of them in the open academic literature are the implementation
sources. **Standing rule: implement from the pre-2011 papers and from academic literature
(Karčiauskas–Peters, Stam, Reif, the isogeometric Bézier-extraction line); do not transcribe
algorithmic specifics that first appear in post-2011 Autodesk patent filings** (analysis-suitable
refinement variants, watertight-conversion claims). Not legal advice — a discipline that keeps
the question easy to answer.

---

## 2. Doctrine: where exactness lives when the surface is not a polynomial

The refinement module's scope line is "every operation adds representational degrees of freedom
while leaving the geometry bit-for-bit unchanged." A star point challenges that doctrine, and
the resolution needs to be stated precisely because it is the intellectual core of this spec.

The surface has three zones:

| Zone | Region | Status of exactness |
| --- | --- | --- |
| **A** | Semi-regular T-mesh (every local neighborhood is a grid, T-junctions allowed) | **Exact.** Extraction to NURBS is a linear identity on basis functions — the same mathematics as Layer 2's refinement operator. |
| **B** | Transition rings around each star point | **Exact.** The subdivision limit surface away from the central point *is* piecewise bicubic; each ring is extracted bit-for-bit (§4.3). |
| **C** | The cap: an n-sided neighborhood of the star point itself, of a size **we choose** (shrunk geometrically by each ring level) | **Exact with respect to its own definition** — see below. |

Inside zone C no finite set of polynomial patches can equal the Catmull-Clark limit surface: the
limit is an infinite sequence of shrinking rings, and its curvature at the central point is
**generically either zero or unbounded** (§4.2). Chasing it with finite patches would be
approximating a defect. So this spec makes the same move the periodic redesign made when it
rejected clamp-and-report: refuse the bad ground truth rather than approximate it.

> **The shipped surface is DEFINED as zones A ∪ B ∪ C of our own construction.** The
> Catmull-Clark limit is rejected as ground truth inside the cap — its point of disagreement
> with us is precisely its curvature defect. The Karčiauskas–Peters cap is not an approximation
> of anything: its G² joints are **exact algebraic identities on Bézier coefficients**, and its
> curvature at the star point is a **designed quantity** carried by an explicit guide surface,
> where stationary subdivision's is an eigenvalue accident. Deviation between cap and CC-limit
> is a shape choice in a region whose size we control, not an error, and it is never reported
> as one.

Three consequences, each of which simplifies the engineering:

1. **Ring count is a shape/budget parameter, not an error tolerance.** There is no convergence
   loop driving rings until a deviation passes — the cap is G² at any ring depth. Rings are
   added to shrink the region governed by the guide (shape fidelity) and bounded by patch-count
   and kernel-resolution budgets (§5.5). This is the opposite of the deformation feature's
   certification loop, and the difference is exactly the difference between approximating a
   foreign surface and constructing one's own.
2. **Continuity is certified algebraically, in the tester, on coefficients** — not sampled and
   hoped. The kernel will not do it for us: knitting is position-tolerant only; G² across knit
   edges is our property to prove (§7, CAP-G2).
3. **The quality bar is concrete: beat Fusion's star points.** Fusion ships visible zebra-stripe
   pinching at extraordinary points — the stationary-subdivision curvature defect made visible.
   A designed-curvature cap does not have it. CAP-SHAPE (§7) is that claim as a test vector.

---

## 3. What the refinement module already supplies

The reason this project is plausible at custom-feature scale: the numerically treacherous half
is already built and live-verified (200+ checks). Mapping, existing export → role here:

| Existing (splineRefinementUtils.fs) | Role in this pipeline |
| --- | --- |
| Layer 1 knot arithmetic (`findKnotSpanIndex`, `knotMultiplicity`, `mergeKnotVectors`, …) | Used verbatim. T-spline local knot vectors are just short (degree+2) knot vectors through the same recurrences. |
| Layer 2 `knotRefinementOperator` (unit-basis coefficient rows → sparse linear map) | **The extraction engine.** Expressing one blending function over a refined knot grid is precisely what the operator builder computes; T-spline extraction and local refinement are this operation applied per anchor (§5.2). The isogeometric literature calls the assembled result the Bézier extraction operator — same object. |
| `decomposeSurfaceIntoBezierPatches`, `extractSubSurface`, tensor appliers | The valence-4 special case of extraction; also the merge machinery for assembling maximal NURBS regions (§5.2). |
| Periodic window machinery (§2.3 of that spec), seam rules, `rewindowPeriodicSurfaceDirection` | Closed T-mesh directions (cylinder/torus topology) emit as genuinely periodic faces through this, unchanged. The tile/operate/slice instinct also recurs structurally in ring extraction. |
| `evaluateBSplineSurfacePoint`, `bSplineBasisValues` | Zone A evaluation and the tester's arithmetic backbone. One small addition needed: a single-basis-function evaluator over an arbitrary local knot vector (§5.2). |
| `KnotInsertionAlgorithm.OSLO` (reserved, never used) | Finally earns its slot: regular-region Catmull-Clark subdivision **is** uniform midpoint knot refinement (Lane–Riesenfeld), i.e. whole-knot-set insertion — the Oslo flavor (§4.3). |
| Emission discipline from `displacementMap.fs` (untrimmed template + `opReplaceFace`, KnotArray casts, memoized heavy compute) | Zone A/B/C patches emit through the same pipeline; the memoized-unit-cell pattern covers extraction's one-time cost. |
| Perf memories ([[featurescript-append-perf]], [[featurescript-squarednorm-vs-norm]], [[featurescript-length-vector-gotchas]]) | Standing rules here too. Extraction is the hottest loop this repo will have run. |

**Deliberately NOT on this path:** degree elevation (T-splines here are bicubic, always — §10
D5), the tween compatibility family (`makeSurfacesCompatible`, `alignPeriodicSurfaceSeams`), and
the §9.1 deformation loop. Roughly half the refinement module by mass transfers, and it is the
half that took the most debugging to get exact.

---

## 4. The mathematics at a star point (recorded so the design survives the gap)

### 4.1 Why valence ≠ 4 is a different object, not a harder case

Every tensor-product construction — including T-spline blending functions, whose local knot
vectors are inferred by marching rays through the T-mesh — presumes two transverse knot
directions at every point. At an interior vertex of valence 3 or ≥ 5 no consistent local (s, t)
exists: a ray entering the vertex has no unique continuation, and no assignment of knot
intervals makes the neighborhood a grid. Blending functions are not hard to compute there; they
are **undefined by the construction**. This is why the original paper is titled "T-splines and
T-NURCCs": the T-spline covers semi-regular regions, and star-point neighborhoods are handed to
non-uniform Catmull-Clark subdivision. Every real T-spline system is this hybrid.

### 4.2 What the subdivision limit is, and its defect

Applying the Catmull-Clark rules to a star point's neighborhood reproduces a scaled copy of the
same neighborhood: the limit surface is an **infinite sequence of exact bicubic rings** — 3n
Bézier patches per subdivision level for valence n (each of the n EP-adjacent quads splits into
4, of which 3 are regular and 1 recurses) — spiraling into the limit point, polynomial
everywhere except that single point.

Behavior at the point is governed by the eigenstructure of the local subdivision matrix, with
sorted eigenvalues `1 = λ₀ > λ₁ = λ₂ > μ ≥ …`:

- The limit position is the projection onto the dominant left eigenvector.
- The paired subdominant eigenvectors span the tangent plane; Reif's criterion (equal real
  subdominant pair + regular, injective characteristic map) gives C¹. Standard CC satisfies it
  at all valences.
- Curvature is decided by μ against λ₁²: bounded nonzero curvature requires `μ = λ₁²` exactly.
  Standard CC misses at every valence ≠ 4, so curvature at a star point is generically **zero
  (flat spot) or unbounded** — and Peters–Reif showed that tuning eigenvalues to fix it buys
  flat spots or larger stencils instead. The zebra pinch at Fusion star points is this
  arithmetic made visible. This is the defect that justifies §2's rejection of the CC limit as
  ground truth inside the cap.

The contraction rate per ring level is λ₁ (exactly 1/2 at valence 4, larger — slower — as
valence rises), which is what makes high valences more expensive in rings and is one input to
the valence cutoff decision (§10, open question 3).

### 4.3 The two facts that make zones B and C cheap for this codebase

1. **Ring extraction is knot refinement.** In the regular part of each sector, one CC step is
   uniform midpoint insertion on a bicubic — Lane–Riesenfeld, the whole-knot-set-at-once Oslo
   flavor the operator layer already has an enum slot for. The only genuinely new arithmetic in
   zone B is the handful of irregular stencil rows touching the star point. Structurally this is
   the periodic machinery's tile/operate/slice move again: subdivide a locally regular window,
   slice exact bicubic rings out of it.
2. **Stam's exact evaluation (1998).** The limit surface has a closed-form pointwise evaluator:
   project the 2n+8-point local configuration onto the precomputed eigenbasis once per star
   point; then any parameter not exactly at the center lands in ring level m, sector k
   (`m` from a logarithm of the radial coordinate) and evaluates as an ordinary bicubic through
   per-valence coefficient tables. Pure arithmetic, no Context — the zone-B/C analog of
   `evaluateBSplineSurfacePoint`, and the certification backbone for every star-point vector
   in §7. The eigendata is fixed per valence and precomputed offline (§5.3).

---

## 5. New modules

Four source tabs plus testers, sequenced in §8. All follow house rules: std-library naming, no
abbreviations, functions below the feature/entry definitions, preallocated arrays, KnotArray
casts at every emission boundary, throw-with-named-violation over silent repair.

### 5.1 `tMeshUtils.fs` — topology layer (pure, no geometry, no Context)

The T-mesh representation and its laws. Entirely new code, entirely testable as fixed vectors.

- **Representation.** Faces, edges carrying **knot intervals** (the T-mesh stores parameter
  extents on edges, not global knot vectors), vertices, and anchors. FS maps and arrays; plain
  numbers throughout ([[featurescript-length-vector-gotchas]] — units stripped at the boundary).
- **Local knot vector inference.** Per anchor, march rays through the T-mesh collecting degree+2
  knots per direction (the Sederberg 2003/2004 construction). This is the sparse-storage payoff
  and the semantic heart of T-splines.
- **Validity rules, enforced by throwing with the violation named** (precedent:
  `normalizeSplineDefinition`'s explicit throw). The rules v1 enforces:
  - T-junction legality per the 2004 local-refinement paper (mismatched knot intervals across a
    face are a structural error, not a warning).
  - **Star-point buffer:** every extraordinary vertex is surrounded by two fully regular rings
    of quads (all interior vertices valence 4), with sector-uniform knot intervals on spoke and
    ring edges inside the buffer. Deliberately stricter than the T-NURCC minimum — uniformity
    inside the buffer is what keeps §4's eigenanalysis and §5.3's tables applicable verbatim.
  - **No T-junctions inside a buffer.** Local refinement that needs one must first subdivide the
    buffer as a whole (a legal, exact operation).
  - **No two star points with overlapping buffers** (in particular none adjacent). Overlapping
    subdivision footprints void both eigenanalyses.
  - **No crease incident to a star point** (v1). Semi-sharp creases change the subdivision
    matrix per configuration; a later phase may add the tables, the validity error keeps v1
    honest.
  - **Interior star points only** (v1). Boundary extraordinary vertices are a different local
    analysis — open question 4.

### 5.2 `tSplineExtraction.fs` — zone A, exact

Per face of the (buffered, valid) T-mesh: gather the anchors whose blending-function support
overlaps the face, refine each function's local knot vectors onto the face's tensor grid — each
refinement is a Layer 2 operator application on unit-basis rows — and accumulate the per-face
Bézier (or larger tensor) coefficient matrix. This is the Bézier-extraction formulation of
Scott/Borden/Verhoosel/Sederberg/Hughes 2011, which is the same linear-operator idea Layer 2
already implements; their paper is the cross-check for the assembled operator's shape.

- **One new low-level function** belongs in the refinement module, not here: evaluate/refine a
  **single** B-spline basis function over an arbitrary local knot vector (length degree+2).
  `bSplineBasisValues` computes all nonzero functions over a shared vector; this is its
  one-function sibling, and Layer 2's coefficient machinery already contains the arithmetic.
- **Rational honesty.** T-spline blending functions do not generally sum to 1; the surface is
  defined with rational normalization `Σ wᵢ Bᵢ Pᵢ / Σ wⱼ Bⱼ`. Only a **standard** T-mesh has
  `Σ Bᵢ ≡ 1` with unit weights. Extraction therefore always works in homogeneous coordinates
  (std `combinePointsAndWeights` / `separatePointsAndWeights`, per the refinement module's
  boundary rule), and **emits polynomial patches only when partition of unity is verified for
  that mesh** (STANDARD-DETECT, §7); otherwise the emitted patches are rational, exactly.
  Silently assuming PoU is this pipeline's equivalent of re-flagging a clamped curve periodic.
- **Maximal-region merging.** Per-Bézier-face emission would hand the kernel hundreds of faces.
  Where the extracted grid is tensor-consistent across T-mesh faces, merge into maximal NURBS
  regions before emission (the `mergeKnotVectors`/`extractSubSurface` machinery); closed
  directions emit as genuinely periodic faces through the existing §2.3 machinery. Default is
  merged emission; measure knit cost live before revisiting (open question 6).
- **Local refinement of the T-mesh itself** (adding an anchor without global propagation — the
  2004 algorithm with its violation-resolution loop) reuses the same per-function refinement
  primitive. It is required for the *authoring* story (§8, T4), not for extraction, and can land
  after extraction is proven.

### 5.3 `subdivisionKernel.fs` + `subdivisionEigenData.fs` — zone B

- **Irregular stencils.** The CC subdivision rows for the star point and its immediate
  neighbors; everything else in the buffer is the OSLO/midpoint refinement operator.
- **Ring extractor.** k levels of exact bicubic rings per star point, emitted per sector per
  level, then merged per sector across level bands where knot structure allows (§5.5 budget).
  RING-AGREEMENT (§7) keeps the stencil path and the operator path honest against each other —
  the same discipline as PERIODIC-OPERATOR.
- **Stam evaluator.** As §4.3. Needs per-valence tables: eigenvalues, eigenvector matrix and its
  inverse, and the three sub-patch bicubic coefficient matrices — a few thousand plain numbers
  per valence, embedded as consts in `subdivisionEigenData.fs` for valences {3, 5, 6, 7, 8, 9,
  10} initially (cutoff: open question 3).
- **Offline precomputation, certified in-FS.** FeatureScript will not run an eigensolver, and
  should not. The tables are generated once by a disposable offline script and are **certified
  inside the tester by their defining equations** — `‖A·v − λ·v‖² ≤ 1e-24` per pair,
  `‖V·V⁻¹ − I‖` likewise (EIGEN-RESIDUAL, §7; squared thresholds per
  [[featurescript-squarednorm-vs-norm]]). The certification removes all trust from the
  generator, so the generator is not versioned in this repo (scratch tooling; the repo's layout
  rules have no home for it and the residual vectors make one unnecessary). Stam's published
  data files are an independent cross-check during T2 bring-up.

### 5.4 `starPointCaps.fs` — zone C, the committed G² route

The Karčiauskas–Peters guided-surfacing construction, conceptually four steps:

1. **Characteristic map.** From the subdominant eigenvectors (already in
   `subdivisionEigenData.fs`), the natural local coordinates for the star-point neighborhood —
   the parameterization in which the limit surface's first-order behavior is linear.
2. **Guide surface.** A low-degree map in characteristic-map coordinates carrying the
   **designed second-order jet** at the center — position, tangent plane, and a chosen,
   bounded curvature. This step is the entire escape from §4.2's trap: curvature at the star
   point becomes an explicit design quantity instead of an eigenvalue accident. The guide's
   shape is informed by the surrounding zone B rings (it must follow what the T-mesh intends),
   and the K–P papers give the functionals that pick it.
3. **Cap patches.** n patches (one per sector; possibly a small macro-split — open question 1)
   of bi-degree 5 or 6, constructed to (a) join each other G² across sector boundaries under
   prescribed reparameterizations, (b) join the innermost zone-B ring G², and (c) interpolate
   the center with the guide's jet. The G² conditions are finite linear identities on Bézier
   coefficients — exact, checkable, and checked (CAP-G2, §7). The specific functionals and
   coefficient formulas are extracted from the papers at implementation time; **this spec pins
   the acceptance vectors (§7), so any construction that passes them is acceptable, and the
   papers named in §11 are where to start.** Karčiauskas and Peters publish companion
   implementations and Bézier data through Peters' SurfLab (BezierView and per-paper
   supplements) — fixture sources for CAP-G2 before our own construction exists.
4. **Emission.** Polynomial bi-5/bi-6 Bézier patches (rational only if the chosen variant
   demands it) → `opCreateBSplineSurface`, same as every other patch in the pipeline.

Baseline is the minimal bi-6 G² completion of the bicubic ring complex; the guided-subdivision
upgrade (deriving zone B's rings themselves from the guide, improving shape everywhere rather
than only inside the cap) is logged as open question 2, not v1.

### 5.5 Emission and knit — the kernel-facing floor

- **Knit is position-tolerant only.** `opBoolean` UNION on surfaces requires coincident or
  overlapping edges and joins them; it neither checks nor records G¹/G². Our continuity is
  proven in the tester on coefficients, never assumed from a successful knit. `makeSolid : true`
  when the T-mesh is closed; `joinSurfaceBodiesWithAutoMatching` is the assisted alternative
  (see its `@seealso` on `opBoolean`).
- **Ring floor.** Rings shrink by λ₁ per level; the kernel's linear resolution is ~1e-8 m and
  operations get fragile well above it. Budget rule: stop emitting rings when the smallest
  patch edge would drop below ~10³ × kernel resolution, and never exceed the patch-count budget
  (3n patches/level adds up — a valence-5 point at 4 levels is 60 ring patches before merging).
  Typical expectation: 3–6 levels, chosen by shape (§2 consequence 1), bounded by this floor.
- **Trimming.** Extraction and caps produce untrimmed patches. Where the T-spline body replaces
  or dresses existing geometry, the `opReplaceFace` route from `displacementMap.fs` carries trim
  loops for free — same pattern, already proven.
- **House emission rules apply:** `knotArray(...)` casts on every `uKnots`/`vKnots` handed to
  `bSplineSurface` / `opCreateBSplineSurface`; preallocation everywhere; scalar-on-left for
  length vectors; memoize the whole extraction as a unit keyed on the T-mesh data (the
  displacement-map pattern) so regeneration cost is paid once.

---

## 6. API sketch (indicative, not final)

Signatures at the level of certainty this stage supports — enough to see the layer boundaries.
Everything below is plain-number/map arithmetic except the emission entry points.

```featurescript
// ---------- tMeshUtils.fs ----------
export function normalizeTMeshDefinition(tMesh is map) returns map;      // validity or throw
export function tMeshAnchors(tMesh is map) returns array;                // anchor list
export function anchorLocalKnotVectors(tMesh is map, anchor is map) returns map;  // { uKnots, vKnots }
export function tMeshStarPoints(tMesh is map) returns array;             // EPs + their buffers
export function refineTMeshLocally(tMesh is map, faceIndex is number,
        uParameter is number, vParameter is number) returns map;         // 2004 algorithm (T4)

// ---------- splineRefinementUtils.fs (one addition) ----------
export function singleBasisFunctionValues(localKnots is array, degree is number,
        parameter is number) returns array;   // one function, arbitrary local knot vector

// ---------- tSplineExtraction.fs ----------
export function extractTSplineFacePatch(tMesh is map, faceIndex is number) returns map;
export function extractTSplineRegions(tMesh is map) returns array;       // maximal merged NURBS
export function isStandardTMesh(tMesh is map) returns boolean;           // PoU verified → polynomial emission

// ---------- subdivisionKernel.fs ----------
export function subdivideStarPointNeighborhood(neighborhood is map) returns map;
export function extractStarPointRings(neighborhood is map, levelCount is number) returns array;
export function evaluateStarPointLimit(neighborhood is map,
        uParameter is number, vParameter is number) returns Vector;      // Stam evaluator

// ---------- starPointCaps.fs ----------
export function buildStarPointGuide(neighborhood is map, ringPatches is array) returns map;
export function buildStarPointCap(guide is map, innermostRing is array) returns array; // n patches, G2
```

---

## 7. Validation vectors

House pattern: a no-geometry tester feature per module cluster (`tSplineTester.fs`, then
star-point vectors either in it or split out when it grows), fixed fixtures, pass/fail via
`reportFeatureInfo`/`println`, nothing declared done until verified live in Onshape.

In order of authority:

1. **REGULAR-PARITY.** A T-mesh with no T-junctions and no star points is a NURBS surface;
   extraction must reproduce `decomposeSurfaceIntoBezierPatches` / the existing tensor
   machinery **term for term** on the same input. The analog of vector 6 (displacementMap
   parity) — the new path must equal the proven path where they overlap, before anything novel
   is trusted.
2. **TJUNCTION-EXACT.** Fixtures from the 2004 paper's worked refinements: extraction across a
   T-junction agrees with direct blending-function summation (via `singleBasisFunctionValues`)
   at ~20 sample parameters to 1e-9, and every extracted patch's homogeneous coefficient rows
   are convex combinations (the §6 vector-3 invariant, inherited).
3. **STANDARD-DETECT.** `isStandardTMesh` true ⟺ sampled `Σ Bᵢ` ≡ 1 to 1e-12; a deliberately
   non-standard fixture must come out rational and still pass TJUNCTION-EXACT through the
   homogeneous path.
4. **EIGEN-RESIDUAL.** Every embedded eigenpair satisfies `squaredNorm(A·v − λ·v) ≤ 1e-24`,
   `V·V⁻¹ = I` to the same, per valence. This vector is what makes the offline generator
   disposable (§5.3).
5. **RING-AGREEMENT.** Level-(m+1) rings computed by direct stencil application equal level-m
   rings pushed through one OSLO refinement, exactly — the operator-vs-direct discipline
   (PERIODIC-OPERATOR's analog) keeping the two zone-B code paths honest.
6. **STAM-RING.** The Stam evaluator agrees with the extracted ring patches on ring domains to
   1e-9 — the evaluator certifies the geometry and the geometry certifies the evaluator.
7. **CAP-G2.** The committed vector. Across every cap↔cap and cap↔ring boundary, position,
   first, and second derivative jets agree under the prescribed reparameterization at sampled
   boundary parameters to 1e-9, **plus** the coefficient-level G² identities checked exactly
   where the construction states them. Bring-up fixtures from SurfLab companion data before our
   construction exists.
8. **CAP-SHAPE.** Principal curvatures sampled along shrinking loops around the star point
   converge to the guide's designed values — and the same sampling run on raw CC rings is
   recorded alongside, documenting the defect we are refusing (the anti-zebra vector, and the
   receipts for §2's quality-bar claim).
9. **KERNEL-ACCEPT.** Live only: full pipeline on fixture T-meshes (open, closed-one-direction,
   closed-both, one EP, several EPs) emits, knits to a single body, `makeSolid` closes the
   closed cases. The `BEZIER-SEAM` lesson says kernel acceptance is its own vector, never
   inferred from arithmetic passing.

**Standing rule (the valence analog of "degree 1 is never sufficient coverage"):** every
star-point vector runs valences **3, 5, 6, and 8** at minimum. Valence 3 flattens — it can hide
a curvature bug exactly the way degree-1 fixtures hid the clamped-extraction bug — and even
valences can hide sector-indexing errors that odd valences expose. One valence is never
sufficient coverage.

---

## 8. Phasing

Sequencing is dependency order, not scope negotiation — T1 shipping alone does not cap the
project at semi-regular (§ status block, decision 1).

- **T0 — groundwork.** Acquire the §11 papers into `whitepaper-references/`; build the offline
  eigendata generator (scratch tooling, §5.3); land EIGEN-RESIDUAL's design alongside the
  tables it certifies. No FS behavior ships.
- **T1 — semi-regular T-splines.** `tMeshUtils.fs` + `tSplineExtraction.fs` +
  `singleBasisFunctionValues` in the refinement module; vectors 1–3 green, then live. Ends with
  a working sparse-to-NURBS pipeline for T-meshes without star points — independently useful
  (local refinement for the deformation feature's per-patch adaptivity) and the extraction
  engine every later phase rides on.
- **T2 — subdivision kernel.** `subdivisionKernel.fs` + `subdivisionEigenData.fs`; vectors 4–6
  green, then live. Ends with exact rings and a certified Stam evaluator.
- **T3 — G² caps.** `starPointCaps.fs`; vectors 7–8 green, then vector 9 live. Ends with the
  full-topology pipeline.
- **T4 — authoring.** The feature(s) users touch: T-mesh from existing B-rep + local
  refinement, table/sketch-driven construction, or mesh import (open question 5 — FS dialogs
  cannot host a mesh editor, so authoring is derivation and refinement, not free-form
  sculpting UI). `refineTMeshLocally` lands here.

Per `AGENTS.md`: no phase is declared done from arithmetic alone — live verification in
Onshape, confirmed builds, every time.

---

## 9. Deliberately not in scope

- **Degrees other than bicubic.** Degree 3×3 is the T-spline literature's home ground and the
  only degree the subdivision hybrid supports cleanly. The refinement module keeps general
  degree; this pipeline does not.
- **Semi-sharp creases at star points** (v1 validity error, §5.1). Creases in regular regions
  are just knot multiplicity and come free.
- **Analysis-suitability machinery** (dual bases, linear-independence certification for IGA).
  We emit geometry; STANDARD-DETECT plus honest rational emission covers the geometric
  consequences. Revisit only if this pipeline ever feeds simulation.
- **Gregory G¹ caps** — rejected, decision 2. Recorded as the road not taken so nobody
  re-proposes it as a "pragmatic first step"; the pragmatic first step is T1, which has no caps
  at all.
- **Singular-parameterization EP schemes** (D-patches, Reif's TURBS) — considered and rejected:
  degenerate patch corners are legal in Parasolid but historically fragile under downstream
  operations (offset, fillet, shell), and the whole point of emitting to a B-rep kernel is that
  downstream operations work.
- **Approximating the CC limit inside the cap** — excluded by doctrine (§2), not by cost.

---

## 10. Decisions, and what remains

Nailed down (2026-08-08):

1. **Full arbitrary-topology scope committed** from the design stage. Phasing is dependency
   order only. (*"You know I'm all about the deep end… I don't make a habit of limiting scope
   in service of something lesser."*)
2. **Star-point route: Karčiauskas–Peters G² caps.** Gregory G¹ rejected outright — precedent
   is the periodic clamp-and-report reversal (SPLINE_REFINEMENT_UTILITY_SPEC §11 decision 4):
   this project does not ship a lesser construction as a lever to the broken past. (*"Shortcuts
   or approximations are an admission of failure."*)
3. **Construction-is-definition at star points** (§2). The CC limit is rejected as ground truth
   inside the cap because its curvature there is defective; cap-vs-CC deviation is a shape
   choice, never a reported error; ring count is a shape/budget parameter, not a tolerance.
4. **Eigendata is precomputed offline and certified in-FS by residual vectors** (EIGEN-RESIDUAL).
   The generator is disposable scratch tooling, not versioned here — the certification is what
   is versioned.
5. **Bicubic only** (§9).
6. **Validity violations throw with the violation named.** No silent mesh repair — the
   `normalizeSplineDefinition` precedent, applied to topology.
7. **Extraction always runs in homogeneous coordinates; polynomial emission only on verified
   partition of unity** (STANDARD-DETECT). Assuming standardness is this pipeline's
   clamp-and-reflag.
8. **Patent discipline** (§1.1): pre-2011 papers and academic literature only.
9. **v1 star points are interior-only, crease-free, buffered** (§5.1) — each relaxation is its
   own future decision, not a silent extension.

Open questions:

1. **Bi-5 vs bi-6, and patches per sector.** Resolve when the K–P functionals are worked
   through in T3; CAP-G2/CAP-SHAPE are construction-agnostic on purpose.
2. **Guided rings** (guide-derived zone B instead of raw CC rings) — shape upgrade over the
   whole neighborhood; evaluate after the baseline cap passes CAP-SHAPE.
3. **Valence cutoff.** Tables for {3, 5–10} initially; raising it is data, not code. High
   valence costs rings (λ₁ grows) and patches (3n/level) — decide from real models.
4. **Boundary star points.** Different local analysis; v1 throws. Lift when a real model needs
   it.
5. **Authoring input for T4** — derive-from-B-rep + local refinement vs table/sketch-driven vs
   mesh import. The one place UX research is needed before code.
6. **Merged vs per-Bézier emission default** — merged is the default on paper (§5.2); measure
   live knit cost at T1 and confirm.
7. **Star points on closed T-meshes** — interplay between EP buffers and the periodic seam
   machinery (a torus with EPs has both). Expected to compose (buffers are local, seams are
   global), but "expected to compose" is exactly the phrase the BEZIER-SEAM bug was hiding
   behind; it gets its own fixture in vector 9.

---

## 11. References

To be acquired into `whitepaper-references/` at T0 (only The NURBS Book is currently in the
repo):

- Sederberg, Zheng, Bakenov, Nasri — *T-splines and T-NURCCs*, SIGGRAPH 2003. The definition,
  and the spline/subdivision hybrid architecture.
- Sederberg, Cardon, Finnigan, North, Zheng, Lyche — *T-spline Simplification and Local
  Refinement*, SIGGRAPH 2004. Local refinement and T-spline→B-spline conversion; source of the
  TJUNCTION-EXACT fixtures.
- Stam — *Exact Evaluation of Catmull-Clark Subdivision Surfaces at Arbitrary Parameter
  Values*, SIGGRAPH 1998. The evaluator and the published per-valence data used as T2
  cross-check.
- Reif — *A unified approach to subdivision algorithms near extraordinary vertices*, CAGD 1995.
  The C¹ criterion.
- Peters, Reif — *Subdivision Surfaces*, Springer 2008. The curvature results behind §2 and
  §4.2 (μ vs λ₁², the impossibility results for stationary schemes).
- Karčiauskas, Peters — *Concentric tessellation maps and curvature continuous guided
  surfaces*, CAGD 2007. The guided-surfacing framework (characteristic map + guide).
- Karčiauskas, Peters — the G² completion line: *Biquintic G² surfaces via functionals*
  (CAGD, ≈2015) and *Minimal bi-6 G² completion of bicubic spline surfaces* (CAGD, ≈2016) —
  the committed cap constructions; exact citations to confirm at T0 acquisition. Peters'
  SurfLab distributes companion implementations and Bézier data (BezierView) — CAP-G2 bring-up
  fixtures.
- Scott, Borden, Verhoosel, Sederberg, Hughes — *Isogeometric finite element data structures
  based on Bézier extraction of T-splines*, IJNME 2011. The extraction-operator formulation
  matching Layer 2.
- Piegl, Tiller — *The NURBS Book*, ch. 5. Already in `whitepaper-references/`; governs the
  refinement module this pipeline stands on.
