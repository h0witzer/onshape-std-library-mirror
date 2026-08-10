# Documentation

All project documentation lives here. Nothing in this tree is part of the Onshape standard
library, and nothing here is imported by FeatureScript.

> **Keep this tree clean.** Markdown belongs in `docs/` and nowhere else — not in the
> repository root, not in `custom-features/`. This was cleaned up once after two dozen loose
> documents accumulated in both places; do not start that over. The full rules are in
> [AGENTS.md](../AGENTS.md) under *Repository Layout*. Read them before creating a file.
>
> A pre-commit hook (`.githooks/pre-commit`) enforces this. Enable it once per clone with
> `git config core.hooksPath .githooks`.

## Where things go

| Location | Contents | Markdown allowed? |
| --- | --- | --- |
| Repository root | Mirrored Onshape standard library `.fs` files, `AGENTS.md` (agent instructions), `README.md` / `README.pdf` (upstream mirror README), `LICENSE.txt` | **No** |
| `custom-features/` | Custom FeatureScript source only | **No** |
| `docs/` | Every spec, guide, and feature document | Yes |

When a custom feature needs documentation, add it under `docs/` and reference it from the
feature's header comment by its repo-relative path. If a document on the topic already
exists, update it rather than adding a second one beside it — and add a one-line entry to
the lists below whenever you add a genuinely new document.

## featurescript-guides/

Cross-cutting FeatureScript knowledge that applies to more than one feature. Read these
before writing new code in the same area.

- [FEATURESCRIPT_ID_CONCATENATION.md](featurescript-guides/FEATURESCRIPT_ID_CONCATENATION.md) — how `Id` and string concatenation operators (`~`, `+`) actually behave, and the rules for building ids safely.
- [TRACKING_QUERIES.md](featurescript-guides/TRACKING_QUERIES.md) — when `qCreatedBy` is not enough and how to carry references through operations that split, replace, or transform geometry.
- [SHEET_METAL_GOTCHAS.md](featurescript-guides/SHEET_METAL_GOTCHAS.md) — non-obvious requirements when building sheet metal features, including the `sheetMetalStart` naming requirement.
- [SHEET_METAL_QUERY_LESSONS.md](featurescript-guides/SHEET_METAL_QUERY_LESSONS.md) — sheet metal architecture, association attributes, query function selection, and mapping between flat and folded representations.
- [TRIAD_MANIPULATOR_NOTES.md](featurescript-guides/TRIAD_MANIPULATOR_NOTES.md) — the difference between `triadManipulator` and the full transform triad, and how to drive each.

## specs/

Design documents for work that is planned or in progress. These describe intent and
derivation; the code may not match them yet.

- [DISPLACEMENT_MAP_TILING_SPEC.md](specs/DISPLACEMENT_MAP_TILING_SPEC.md) — tiling one image as a memoized unit cell via per-tile Boehm knot refinement, for `custom-features/displacementMap.fs`.
- [FREE_FORM_DEFORMATION_SPEC.md](specs/FREE_FORM_DEFORMATION_SPEC.md) — consolidating the point-lattice and plane-manipulation FFD features into one `custom-features/freeFormDeformation.fs`: UVN naming, multi-select selection scopes, and the refinement-before-deformation pipeline that removes the classic FFD detail ceiling.
- [SPLINE_REFINEMENT_UTILITY_SPEC.md](specs/SPLINE_REFINEMENT_UTILITY_SPEC.md) — a shared `splineRefinementUtils.fs` module for exact B-spline refinement (knot insertion, Bezier decomposition, degree elevation), replacing duplicated and broken copies in `displacementMap.fs`, `tweenSurfaces.fs`, and `tweenCurves.fs`, and underpinning an exact flex/deform feature.
- [T_SPLINE_SUPPORT_SPEC.md](specs/T_SPLINE_SUPPORT_SPEC.md) — companion to the spline refinement spec: T-spline surfaces via exact NURBS extraction and knit, including full arbitrary topology — star points handled by exact subdivision rings plus Karčiauskas–Peters G² caps. Design stage; nothing built.
- [TIPPY_BUCKET_PIVOT_SPEC.md](specs/TIPPY_BUCKET_PIVOT_SPEC.md) — material-aware pivot placement from empty and filled centers of mass, for `custom-features/tippyBucketPivot.fs`.

## features/

Documentation tied to a single custom feature or feature group.

- [free-form-deformation/](features/free-form-deformation/) — lattice free-form deformation (`custom-features/freeFormDeformation.fs`): user guide to the UVN lattice, selection scopes, accuracy controls, and deformation recipes.
- [kerf-bending/](features/kerf-bending/) — analytical kerf bend spacing (`custom-features/kerf-bending/`): user README and project summary.
- [label-placement/](features/label-placement/) — research on deterministic label placement in non-convex planar faces, backing `custom-features/label-placement-experiment/`.
- [query-variable-plus/](features/query-variable-plus/) — how to extend Query Variable Plus (`custom-features/queryVariablePlus.fs`) with new query types.
- [sheet-metal-stitch-cut-bend/](features/sheet-metal-stitch-cut-bend/) — attribute handling for split edge domains in `custom-features/sheetMetalStitchCutBend.fs`.
- [spacing-utilities/](features/spacing-utilities/) — the consolidated `spacingUtils.fs` module (`custom-features/spacing_utilities/`): module README plus the refactoring, consolidation, and circular-pattern test-fix histories.
- [tessellated-loft/](features/tessellated-loft/) — standalone `opTessellatedLoft` feature (`custom-features/tessellatedLoft.fs`): user README and implementation summary.

## Documentation elsewhere in the repo

These are intentionally not in `docs/` — they belong to the thing they sit next to:

- `std-sync/README.md` — usage of the std-library sync tool.
- `non-featurescript-functions-reference/` — vendored third-party reference material, kept as-is.
- `whitepaper-references/` — source papers backing the algorithms used in custom features.
