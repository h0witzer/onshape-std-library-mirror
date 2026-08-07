# Displacement Map — Tiled Texture via B-Spline Knot Refinement

**Spec for implementing the tiled/memoized path in `custom-features/displacementMap.fs`.**
Read this whole document before touching code. It exists because prior attempts kept
shipping lazy simplifications that the user rejected. The math here is not optional.

---

## 0. TL;DR

Tile one image as a repeating unit cell across a planar face. Do it by imagining **one
single uniform-knot B-spline surface** whose control-point grid is the image tiled `T×T`,
then **decompose that surface into small repeating pieces via B-spline knot refinement
(Boehm knot insertion)** so we never instantiate the giant surface but reproduce it
**exactly**. The pieces are memoized (`opPattern`) for performance and meet each other with
the surface's own `C^(D-1)` continuity (G2 for degree ≥ 3). Then knit + replaceFace.

Do **not** build one giant surface (kills regen). Do **not** hand-build fills from
extrapolated tangent handles (they warp out of bounds). Do **not** butt-join independently
clamped patches (only C0, kinks at seams). The knot-refinement decomposition is the agreed
solution.

---

## 0.5 DECISIONS LOCKED (2026-08-02) — these SUPERSEDE the exploratory parts below

The knot-refinement direction was reviewed against the code and confirmed sound (the §5.3 test
vector, the congruence-under-translation argument, and the default-clamped-knots claim all
check out). The following choices are now settled and are **implemented** in
`displacementMap.fs`:

1. **Per-tile single piece, NOT the four-piece cell/vfill/hfill/corner model.** Since we repeat
   ONE unit cell, every seam is identical, so a single wrap-aware seed tile reproduces them all.
   Extract one clamped tile of the conceptual uniform surface `S`, `opPattern` it (rigid
   translations), knit. The four-domain model in §4 is **not** used — it only mattered if fills
   needed independent bodies, and the blur (below) is baked into the one seed instead.
2. **Period `P` = imgCols / imgRows, pitch = period/N** (open question §7.1 → resolved to
   `P = imgCols`, and further to `pitch = period/N`, not `period/(N+1)`). One control point per
   pixel, uniform lattice, no seam column. This is the ONLY choice that keeps the lattice
   uniform across seams (no texture stretch) AND makes the pieces congruent.
3. **`blendWidth` UI + all inset/blend/subGrid/buildSeamFills machinery REMOVED** (open questions
   §7.2, §7.3 → resolved). The exact scaffold has no band.
4. **Blur is a FUTURE step, baked into the seed** (not separate fill bodies). When added, it is a
   **wrap-aware Gaussian blur of the seam-band pixel HEIGHTS**, applied inside `buildTileWindow`
   *before* the lift/extract. Because the tile repeats, one blurred seed serves every seam. The
   hook is marked `FUTURE (blur):` in the code at exactly that spot. Note the design constraint:
   to keep cells "untouched" and avoid a crease at the band edge, the blur must taper toward the
   raw pixel data at the band boundary — the band width is that taper room.

**Implemented functions (per-tile scaffold, blur off):** `positiveMod`, `buildTileWindow`
(window = one period + `degree` wrapped neighbours per edge), `extractionWeights` (Boehm
insertion run ONCE on unit-basis rows to build the per-axis clamp-extract as a sparse weight
table), `applyExtraction` (apply that table to a row of control-point Vectors), `refineTileSeed`
(tensor product: apply the column-axis map to every window row, then the row-axis map to every
resulting column), then `createBSplinePatch` → `opPattern` → `opBoolean` UNION.

**Perf note:** the extraction map depends only on `(degree, n)`, not on the data, and is the
same for every row/column, so it is computed once per axis instead of re-running knot insertion
per row/column. This replaced an earlier direct `insertKnot`/`extractClampedUniform` version
that cost ~4000 insertions / ~30 s of the seed build; the weight-table version is a few dozen
insertions. Most output CPs are pure identity (weight 1), so the apply step is nearly free.

**Still to verify in Onshape (cannot compile locally):** (a) `insertKnot` reproduces §5.3;
(b) one seed tile knits to its neighbours with a visibly smooth (curvature-continuous) seam and
no overshoot; (c) `opBoolean` UNION merges the coincident-edge copies into one body;
(d) `replaceFace` accepts the multi-face knit template — the top integration risk;
(e) regen time of the one-time `refineTileSeed` extraction is acceptable.

---

## 1. What the feature is

`displacementMap.fs` (originally by Evan Reese) displaces a face by an image heightmap: it
builds a B-spline surface whose control points are lifted off the face by per-pixel
grayscale height, then `replaceFace`s the target face with it. The **single-image path must
stay untouched.** We are adding a **tiled path**: repeat one small image as a unit cell to
cover a large face, cheaply.

The user's real job: a 307×307 CSV heightmap tiled across a ~19–20" planar face for a
client display. Performance and seam quality both matter — the client will see the seams.

---

## 2. Hard requirements (these are non-negotiable; the user repeated each one)

1. **Performance via memoization.** The tiling MUST decompose into small repeated pieces
   drawn as copies (`opPattern`). One giant surface "will blow this shit up." Patches +
   fills are REQUIRED, not optional.
2. **Genuine G2 (curvature) continuity at seams.** Not C0 (crease), not warped/overshooting.
   For a user degree ≥ 3 the seams must be curvature-continuous.
3. **Honor the user's input degree `D`.** Fills use the SAME degree the user picks (default
   5). Do NOT pin to cubic.
4. **Texture-scale invariance.** Pixel pitch and tile period are BOTH fixed. Changing the
   blend/fill width must NOT grow, shift, or compress the texture. (In the refinement model
   this is automatic — see §4 note — because the split location has zero geometric effect.)
5. **No geometric change vs. the conceptual giant surface.** The decomposed pieces must
   reproduce the single big surface exactly. This is the user's literal framing:
   > "no geometric change between the mega array and the many smaller ones, but massive
   > performance gains."
6. **Use the real pixel data.** Do not throw away surface information to make the math
   easier. All the height data is sitting in the control-point grid; the fills must be built
   from it, not from invented tangent handles.
7. **Planar only for now**, with dispatch hooks for cylinder/freeform (fall back to the
   single-surface path with an info message). replaceFace does the boundary trim for free —
   no separate trim step.
8. Keep the single-surface (non-tiled) path and its caching option intact and guarded behind
   `!tileImage`.

---

## 3. The mental model (the user's exact words, formalized)

> "Imagine that this whole surface were originally constructed out of a 1228 × 1228 array of
> knots, but there's a repeating pattern of 307 knots within that array. We don't want to
> explode the feature generation time so we have recognized that the repetitive cells can
> gain performance by drawing the smaller subsets of surface and filling the regions between
> them (rows and columns) as their own smaller (and also theoretically tileable) arrays of
> surface that result in no geometric change between the mega array and the many smaller
> ones."

Formalized:

- There is **one** tensor-product B-spline surface `S` of degree `D` (user input) in each
  parametric direction.
- Its control-point grid is the image tiled `T×T`: `G[i][j] = heightOffset(image[i mod P][j mod P])`
  placed at pixel-center positions. `P` = image size in that axis (period). `1228 = 4 × 307`
  ⇒ `T = 4`, `P = 307`.
- The knot vector is **uniform** (not clamped in the interior), so `S` is `C^(D-1)`
  continuous **everywhere**, including across every tile seam. That continuity is where G2
  comes from — for free — and it is inherently bounded (convex-hull property → no overshoot).
- We never build `S`. We build a small set of **clamped sub-surfaces extracted from `S`**
  that partition it. Because the pattern is `P`-periodic, there are only a few *distinct*
  pieces; we build one of each and `opPattern` the copies.

---

## 4. The decomposition — four memoized piece types

Partition the parameter domain of `S`, `P`-periodically, into (per the user's "cells + the
rows and columns between them"):

| Piece            | Covers                                   | Count built | Patterned to        |
|------------------|------------------------------------------|-------------|---------------------|
| **Cell**         | tile interior (both axes interior)       | 1           | every tile          |
| **Vertical fill**| column strip straddling a vertical seam  | 1           | every vertical seam |
| **Horizontal fill** | row strip straddling a horizontal seam| 1           | every horizontal seam |
| **Corner**       | the fill×fill patch at a 4-tile junction | 1           | every 4-way junction|

Each piece is a **clamped B-spline sub-surface extracted from `S` by knot refinement**, so:
- it reproduces `S` exactly on its sub-domain (requirement 5),
- it meets its neighbors with `S`'s `C^(D-1)` continuity (requirement 2),
- it is degree `D` (requirement 3),
- its control points are **convex combinations of real pixels** → inside the hull → no warp
  (requirement 6),
- it is identical to every other piece of its type (P-periodic) → memoizable (requirement 1).

**Note on why texture-invariance is automatic:** since every piece reproduces `S` exactly,
*where* we cut the cell/fill boundary has **zero geometric effect**. The "blend/fill width"
is now purely a performance/partition knob (how wide the fill strips are), not a geometric
one. The texture cannot shift or scale when it changes. This dissolves the whole earlier
"seam column / pitch = input/(N+1)" headache — but see §7 open question on `P`.

**Alternative worth mentioning to the user:** if separate fill *bodies* aren't required, the
simplest realization is **per-tile pieces** (split at every seam; one memoized tile piece;
adjacent tiles meet G2 directly, fills subsumed). It's fewer bodies and less code and equally
satisfies requirements 1–6. The user explicitly asked for cells + separate fills, so the
four-piece plan above is the default — but confirm they don't prefer per-tile pieces.

---

## 5. The core math — Boehm knot insertion + clamped extraction

This is the part that MUST be implemented correctly. It is standard B-spline refinement.

### 5.1 Single knot insertion (Boehm)

Insert a knot value `ubar` into a degree-`D` B-spline with control points `P[]` and knot
vector `U[]`. Find span `k` such that `U[k] <= ubar < U[k+1]`. New control points `Q[]`:

```
Q[i] = P[i]                                             for i <= k - D
Q[i] = alpha*P[i] + (1 - alpha)*P[i-1]                  for k-D+1 <= i <= k
        where alpha = (ubar - U[i]) / (U[i+D] - U[i])
Q[i] = P[i-1]                                           for i >= k+1
```

New knot vector = `U` with `ubar` inserted in sorted position. The curve is unchanged; every
`Q[i]` is a convex combination of `P[i-1], P[i]` (0 ≤ alpha ≤ 1). This is why nothing warps.

### 5.2 Clamped extraction over `[a, b]`

To cut out the clamped sub-spline that reproduces `S` on the knot interval `[a, b]`
(`a`, `b` are integer knot values of the uniform vector):

1. Insert `a` `D` times (uniform knots start at multiplicity 1 → reach multiplicity `D+1`).
2. Insert `b` `D` times (→ multiplicity `D+1`).
3. Let `ia` = **first** index where the refined knot vector equals `a`, `ibLast` = **last**
   index where it equals `b`.
4. The piece's knot vector = refinedKnots[`ia` .. `ibLast`]; its control points =
   refinedCP[`ia` .. `ia + cpCount - 1`], where `cpCount = (ibLast - ia + 1) - (D + 1)`.
5. Hand `opCreateBSplineSurface` those control points with **default clamped knots** — they
   match the extracted clamped knot vector (multiplicity `D+1` at both ends, uniform interior).

### 5.3 Validated tiny example (verify your `insertKnot` against this)

Degree `D = 2`, 6 control points, uniform knots `U = [0,1,2,3,4,5,6,7,8]`, domain `[2,6]`.
Extract the clamped piece over `[3, 5]`:

- Insert `3` twice and `5` twice → refined knots
  `[0,1,2,3,3,3,4,5,5,5,6,7,8]` (13 knots), 10 control points.
- `ia` = first index of `3` = **3**; `ibLast` = last index of `5` = **9**.
- `cpCount = (9 - 3 + 1) - (2 + 1) = 7 - 3 = 4`.
- Piece control points = refinedCP[3..6]; piece knots = `[3,3,3,4,5,5,5]` = clamped, degree 2,
  4 CPs. ✓ (`cpCount = D + (b - a) = 2 + 2 = 4`.)

If your implementation reproduces this, the 1D core is correct.

### 5.4 Tensor product (2D piece)

A 2D piece over column interval `[aC, bC]` and row interval `[aR, bR]` is extracted
separably:

1. Build the **window**: the giant control points over column indices `[aC-D .. bC+D]` and
   row indices `[aR-D .. bR+D]`. Read pixel heights straight from the image with `mod P`
   wrapping — you only ever touch one period plus `D` neighbors, never the whole giant grid.
2. For each **row** of the window, run §5.2 in the column direction → intermediate grid.
3. For each **column** of the intermediate grid, run §5.2 in the row direction → final piece
   control points.
4. `opCreateBSplineSurface` with degree `D` in both directions, default clamped knots.

The window→local knot mapping: put giant index `lo = a - D` at local index 0 with local
uniform knots `0,1,2,...`; then global knot value `k` maps to local `k - lo`. So local
`a' = D`, `b' = b - a + D`. Window length per axis = `(b - a) + 2D + 1`.

---

## 6. Assembly

- **Positions.** Giant control point `(i, j)` sits at
  `gridAnchor + (j + 0.5)*pitchC*colDir + (i + 0.5)*pitchR*rowDir + offset(i,j)*normal`,
  where `pitchC = periodC / P`, `pitchR = periodR / P`, and `offset` = remapped grayscale
  height (`blackValue`..`whiteValue`). The extracted (convex-combination) piece CPs inherit
  positions inside their window's span.
- **Frame** (already in the file): `evFaceTangentPlane` at face param (0.5, 0.5);
  `normal`, `rowDir = normalize(centerPlane.x)`, `colDir = normalize(cross(normal, rowDir))`;
  `evBox3d` in `coordSystem(origin, colDir, -normal)` gives spans + anchor.
- **Pattern.** One seed per piece type at tile (0,0); `opPattern` with
  `transform(tc*periodC*colDir + tr*periodR*rowDir)` over the appropriate `(tc, tr)` ranges
  (cells at every tile; vfills at every vertical seam; hfills at every horizontal seam;
  corners at every interior 4-way junction). Reuse the working `opPattern` shape from
  `custom-features/randomSurfacePattern.fs`.
- **Knit.** `opBoolean` UNION over all pieces — edges are coincident (they're pieces of one
  surface) so it knits to one body. Verify in Onshape that tolerance actually merges them.
- **replaceFace.** Existing path: `opReplaceFace` with template `qCreatedBy(id, FACE)`,
  try/catch `oppositeSense`, then `opDeleteBodies`. Untested with a multi-face template — may
  need adjustment.
- **Edge tiles** partially off the face: don't special-case them; `replaceFace` trims to the
  face for free.

---

## 7. Open questions — ALL RESOLVED (see §0.5)

> Resolved 2026-08-02: (1) `P = imgCols`, pitch = period/N. (2) Per-tile pieces, not four
> separate fills. (3) `blendWidth` removed; the fill-band idea returns only as future blur-taper
> room. The original text is kept below for context.


1. **Period `P` = `imgCols` or `imgCols + 1`?** The "1228 = 4 × 307" framing says `P =
   imgCols` (307), no explicit seam column — the B-spline degree smooths the image[P-1]→
   image[0] jump. But earlier the user was adamant about `pitch = cellSize/(N+1)` (19.25/308
   = 0.0625"). These differ by ~0.3%. Recommend `P = imgCols` (matches the latest framing and
   is cleaner); confirm. This is the ONE genuinely ambiguous parameter.
2. **Four separate fill bodies vs. per-tile pieces** (see §4 alternative). Default = four
   pieces per the user's explicit "rows and columns between them," but per-tile is simpler.
3. **Fill/blend-width UI knob.** In the refinement model it has no geometric effect (only
   picks the cell/fill split). Keep it as a perf knob, remove it, or auto-pick it? Recommend
   auto-pick a valid width from `D` and drop the user knob, unless they want the split
   exposed.

---

## 8. What NOT to do (every one of these was tried and rejected)

- ❌ **One giant surface.** "One surface will blow this shit up." Rejected 3×.
- ❌ **Cubic-Bezier fills from extrapolated tangent handles** (`Q1 = edge + scale*tangent`).
  The handle leaves the convex hull at high-contrast seams → surface flies out of bounds
  (warp). Rejected — "why throw away that surface info?"
- ❌ **Independently clamped patches butt-joined**, sharing one edge column. Position-
  continuous but only **C0** — tangent kinks at seams. Not good enough.
- ❌ **`opLoft` / `opBoundarySurface` / `opFillSurface` across the gutter.** The gutter is a
  ~300:1 thin ribbon between wavy 481-CP spline edges → `LOFT_FAILED`; kernel fills can't
  hold the pitch/parameterization anyway. Abandoned.
- ❌ **Pinning fills to cubic** when the user asked for degree `D`.
- ❌ **`qClosestTo` to pick a pattern instance's edge** — it returns ALL tied entities within
  tolerance, feeding ops a bad multi-edge profile. (If ever needed: select the FACE first via
  a deep-interior control point, then that face's `qLoopEdges`.)

---

## 9. Debugging discipline (this repo has guides — READ THEM: `AGENTS.md`, `docs/featurescript-guides/TRACKING_QUERIES.md`)

- **`try silent` SQUASHES all console reports** — never use it on unvalidated code. Use plain
  `try`/`catch` and surface the caught value: `reportFeatureWarning(context, id, "..." ~ error)`.
- Print with `println(msg)` — output goes to the **FeatureScript notices panel**, not the
  browser F12 console. Also `reportFeatureInfo`/`reportFeatureWarning` for the notice panel.
- Visuals: `debug(context, query_or_point, DebugColor.RED)`; count with
  `size(evaluateQuery(context, q))`. `DebugColor` enum: RED/GREEN/BLUE/CYAN/MAGENTA/YELLOW/
  BLACK/ORANGE.
- There is a "Debug seam fills" checkbox pattern already in the feature for printing
  dims/pitch/period and highlighting targets — reuse it to dump each piece's CP grid size,
  the extracted knot vectors, and to `debug()`-highlight seed vs. patterned pieces.
- FeatureScript rules: no function nesting; typed vars must be initialized; `box` is a
  reserved keyword (use `faceBox`); no abbreviated names; version stars = 3029 in this mirror,
  version numbers stripped from imports.

---

## 10. Verified Onshape ops / facts (from prior work)

- `opCreateBSplineSurface`, `bSplineSurface`, `controlPointMatrix` — `cp[i][j]`: down-column
  `i` = U, across-row `j` = V.
- `opPattern(context, id, {entities, transforms, instanceNames})` — preserves the seed;
  reference `randomSurfacePattern.fs`.
- `opBoolean` UNION knits surfaces with coincident edges.
- `evSurfaceDefinition(...).surfaceType` → `SurfaceType.PLANE` / `.CYLINDER`; `evPlane`,
  `evFaceTangentPlane`, `evBox3d` (`Box3d.minCorner`/`.maxCorner`), `evLength`.
- `coordSystem(origin, x, z)`, `transform(Vector)`, `qCreatedBy(id, EntityType.FACE)`,
  `qContainsPoint`, `qLoopEdges`, `isQueryEmpty`, `evaluateQuery`.
- Utility: `remap`, `min`, `max`, `ceil`, `floor`.

---

## 11. Current file state — RECONCILED & IMPLEMENTED (2026-08-02)

The mid-refactor mismatch described here has been **resolved**. The whole constructed-fill
machinery (`buildSeamFills`, `verticalSeamRow`, `horizontalSeamCol`, `cornerCP`, `subGrid`,
`cellControlPoints`, and the C0 patch/fill split) is **removed** and replaced by the per-tile
knot-refinement scaffold (§0.5): `buildTileWindow` → `refineTileSeed` (via `insertKnot` +
`extractClampedUniform`) → `createBSplinePatch` seed → `opPattern` → `opBoolean` UNION.

Surviving scaffolding (unchanged in role): the precondition UI (imageTable, face, flip, axes,
black/white values, degree, `tileImage` gate, Tiling group, `replaceFace`, `showCoord`) minus
the now-deleted `blendWidth`; the planar frame setup; `resolvePlanarGrid` (periodC/periodR/
uCount/vCount, aspect lock now `imgRows/imgCols`); the PLANE-vs-fallback dispatch in
`buildTiledDisplacement`; the `cellOverlapsFace` keep-test; and the untouched single-surface
path (`buildSingleDisplacement`) and its caching.

---

## 12. Implementation order (suggested)

1. `insertKnot(cp, knots, degree, ubar)` — Boehm; unit-check against §5.3.
2. `extractClamped(cp, knots, degree, a, b)` → 1D piece CPs.
3. `refinePiece(window2D, degree, aCol, bCol, aRow, bRow)` → 2D piece CPs (tensor).
4. Window builders reading the wrapped image for cell / vfill / hfill / corner.
5. `opCreateBSplineSurface` each seed; `debug()` + `println` the CP grids and knot vectors;
   have the user eyeball ONE seed cell before patterning.
6. `opPattern` each; `opBoolean` UNION; `replaceFace`.
7. Verify in Onshape (this math cannot be compiled locally — the user runs it): check the
   knit produces one body, check the seams are visibly curvature-continuous, check nothing
   overshoots, check regen time.

---

*Companion memory: `displacement-map-tiling.md`. Related: `std-library-browser-updater.md`
for how to publish std-library changes.*
