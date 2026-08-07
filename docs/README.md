# Documentation

All project documentation lives here. Nothing in this tree is part of the Onshape standard
library, and nothing here is imported by FeatureScript.

## Where things go

| Location | Contents |
| --- | --- |
| Repository root | Mirrored Onshape standard library `.fs` files, `AGENTS.md` (agent instructions), `README.md` / `README.pdf` (upstream mirror README), `LICENSE.txt` |
| `custom-features/` | Custom FeatureScript source only — no markdown |
| `docs/` | Every spec, guide, and feature document |

When a custom feature needs documentation, add it under `docs/` and reference it from the
feature's header comment by its repo-relative path.

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
- [TIPPY_BUCKET_PIVOT_SPEC.md](specs/TIPPY_BUCKET_PIVOT_SPEC.md) — material-aware pivot placement from empty and filled centers of mass, for `custom-features/tippyBucketPivot.fs`.

## features/

Documentation tied to a single custom feature or feature group.

- [ffd-planes/](features/ffd-planes/) — plane-based free-form deformation (`custom-features/freeFormDeformationPlanes.fs`): user README, implementation summary, and a visual guide to the lattice model.
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
