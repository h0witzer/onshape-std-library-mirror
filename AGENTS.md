# Contributor Guide

## Repository Layout — READ THIS BEFORE YOU CREATE ANY FILE

This repository was cleaned up once because two dozen loose markdown files had piled up in
the root and in `custom-features/` until neither directory was navigable. Do not undo that.
**Every markdown file you create goes in `docs/`. No exceptions, no "just this one."**

Where things live:

| Directory | Contents | May you add markdown here? |
| --- | --- | --- |
| Repository root | Mirrored Onshape standard library `.fs` files, `AGENTS.md`, upstream `README.md`/`README.pdf`, `LICENSE.txt` | **NO** |
| `custom-features/` | Custom FeatureScript source only | **NO** |
| `docs/` | Every spec, guide, plan, summary, and feature document | Yes — pick the right subfolder |

Rules, stated plainly because they have been broken before:

- **Never** write a spec, plan, design doc, implementation summary, refactoring summary, migration note, "lessons learned", status report, or README into the repository root or into `custom-features/`. The root is reserved for standard library content imported from Onshape plus this file. `custom-features/` is reserved for `.fs` source.
- **Never** drop scratch output — CSVs, PNGs, logs, exported data, test fixtures — into the root or `custom-features/`. If it is a work artifact and not source, it does not belong in either place.
- Do not create a new top-level directory to dodge these rules. If your document does not obviously fit an existing `docs/` subfolder, put it in the closest one and say so in your summary rather than inventing a new tree.
- One document per topic. If a document on the subject already exists in `docs/`, **update it** instead of writing `THING_V2.md`, `THING_FINAL.md`, or `THING_SUMMARY.md` next to it. Proliferating near-duplicate summaries is exactly how this got out of hand.
- See [docs/README.md](docs/README.md) for the index and for which subfolder a new document belongs in. If you add a document, add its one-line entry to that index in the same change.
- When a feature needs documentation, put it under `docs/` and reference it from the feature's header comment by its repo-relative path (for example `docs/specs/TIPPY_BUCKET_PIVOT_SPEC.md`). Do not park the doc beside the `.fs` file "so it's easy to find" — that is what the header reference and the index are for.
- If you move or rename anything under `docs/`, update every reference to it in the same change and leave the link check clean. Do not leave dangling paths for someone else to chase.
- Before working on sheet metal, ids, or geometry tracking, read the matching guide in `docs/featurescript-guides/`.

These rules are enforced by a pre-commit hook in `.githooks/pre-commit`, which rejects newly added markdown in the root or `custom-features/` and scratch artifacts (`.csv`, `.png`, `.jpg`, `.log`) in either. It is already configured in this clone; **a fresh clone must enable it once**:

```
git config core.hooksPath .githooks
```

If the hook blocks you, move the file into `docs/` — that is the fix. Do not reach for `--no-verify` to get a document committed into a directory it does not belong in.

## Dev Environment Tips
- All functions in this github are a mirror of the Onshape Standard Library functions with version numbers stripped from the imports
- The current version number of the Onshape standard library is 3044, replace the stars in the header with this
- For example "FeatureScript ✨;" should become "FeatureScript 3044;" and "import(path : "onshape/std/feature.fs", version : "✨");" should become "import(path : "onshape/std/feature.fs", version : "3044.0");"
- Look at the Onshape Standard Library documentation at https://cad.onshape.com/FsDoc/library.html for function applications, expected inputs and outputs, and general reference
- Verify every `op*` or `ev*` function against the mirrored library (for example by searching `geomOperations.fs` or `evaluate.fs`) before using it, and to avoid adding code that references functions that cannot be found in this repository or the official documentation
- Functions that exist in the custom features folder are by definition non-standard and will need to be noted explicitly where these references are being pulled from in the header of the code when they are used
- Browse https://cad.onshape.com/FsDoc/ for general Featurescript knowledge and in particular lexical reference
- Pay strong attention to the values and types used in Featurescript, there are many differences from other C-like languages that are optimized for parametric CAD to be aware of https://cad.onshape.com/FsDoc/variables.html
- These .fs files are not F Sharp or Javascript, Featurescript is a custom language developed for Onshape
- Naming convention for the features we are working with should be more explicit and less shorthand. Match the level of readability seen in the Onshape Standard Library functions
- Annotation names can only include printable ASCII characters on declaration. Non-ASCII characters might look cleaner and more readable but they will result in a build error. They're fine for reporting functions though.
- Don't use abbreviated naming convention for functions or counters, I can't read that shit, name things with clarity and relation to application like the Standard Library does. We can afford the extra vowels, we don't need to name variables "ctrl" when "evalutatedSurfaceControlPoints" is way more descriptive of what that thing is.
- Function nesting is not a thing in featurescript. It isn't possible to declare a function inside of the body of another function.
- Put the functions below the feature definition, I hate having to scroll to find my feature
- Bitshifting and bitmasking operations are not a thing in featurescript
- Variables with types must be initialized
- Pay attention to the way qAdjacent works with the modern adjacency types when used
- If you encounter a feature labeled "Under development, not for general use" in the header assume that the code is non-functional and does not represent valid featurescript development practice
- Add clear comments above functions explaining their function and intended purpose and defining inputs and outputs, make it easy for me to read what blocks of code perform a job and what that job is, listing the fields and supported data types here is immensely helpful
- When leaving comments, make sure the documentation remains contemporary to the current structure and function of the code. Documentation is supposed to explain what functions DO, not what they used to do or what they DON'T do.
- The two rules that get broken most often are enforced mechanically by `tools/fsLint.py`, which a `PreToolUse` hook in `.claude/settings.json` runs on every `.fs` write: a FeatureScript keyword used as an identifier (`box`, `type`, `operator`, `in`, `is`, `new` and the rest of the reserved list), and a comment that narrates history instead of behaviour. A violation is refused before it reaches the file, with the offending phrase quoted. Run `python tools/fsLint.py custom-features/*.fs` to sweep the whole tree; the standard library content in the repo root is skipped.
- **Name work by what it is, never by a code.** Say "the closed-form breakpoint solve" and "the construction-plane filter" — not a bare label. <!-- nameLint: allow --> A code means nothing to anyone who was not present when it was coined, it outlives that session as a referent nobody can resolve, and it hides the thing it names, so a queue whose entries are codes stops being readable as a plan. Enforced by `tools/nameLint.py` on every `.md`, `.fs`, `.py` and `.mjs` write through the same hook. Domain notation that resembles a code is allowed and listed in the script: continuity (`G1`, `C2`), NURBS Book algorithm numbers (`A2.3`), FeatureScript version tags (`V647`), axis names (`U0`), dimensions (`3D`). Section references are never flagged — a section number is a place in a document, not the name of a job. Numbering a list for ordering is fine; labelling the work is not. A line that must quote the bad form carries `nameLint: allow`, which keeps the exception visible.
- Prioritize solutions to problems that apply to more than the most trivial cases, for example when working on a feature that interacts with surface geometry don't assume a planar constraint unless it's explicitly clear that the function will only be called on planar geometry when another solution exists that would generalize to cylinders and cones
- Before working with Arrays in preconditions, look up the correct fields for implementation. isArray() is not a function that exists in the standard library
- Validate all other precondition examples with the standard library
- Try Silent blocks should only be used after a function has been thoroughly tested and validated. The try block is useful for graceful error handling but adding silent to this block will squash all reports to the console and make diagnostics exponentially harder with no information to go off of.
- Adding Silent to a try block does not fix the problem when an error is encountered, just continues the execution and hides the reporting state. 

### Tracking Queries — Reach For These Constantly

When an operation creates new entities and you need to know which is which, use a **tracking
query**. Never identify them by comparing geometry afterwards.

```featurescript
const tracker = startTracking(context, seedEdges);   // BEFORE the operations
opPattern(context, sectionId, { ... });
const copies = qIntersection([qCreatedBy(sectionId, EntityType.EDGE), tracker]);
```

`startTracking` (`feature.fs:575`) resolves to whatever later operations DERIVE from those
entities, so identity survives transforms, scaling, splits and booleans. Overloads take a plain
query; a map with `subquery` + `secondarySubquery` (entities derived from BOTH — how you name "the
edge where face A meets face B"); or `(sketchId, sketchEntityId)`. `startTrackingIdentity` tracks
the entity itself rather than its descendants. Idiom in the standard library: `curvePattern.fs:428`,
`boolean.fs:135`, `endcap.fs:188`, `extend.fs:338`, and the common
`qUnion([entity, startTracking(context, entity)])`.

This is the general answer to a whole class of problems — which face came from which profile, which
edge is the seam, which patterned instance is which — and it is badly under-used in this repo.
**Whenever the code is about to ask "which of these is the one I made earlier", that is a tracking
query.**

A geometric match that guesses wrong does not throw. It regenerates, green, with the wrong
geometry: measured here as a swept body coming out 15923.62 mm³ where 20401.18 was correct,
because a square with a bore has an outer loop and a hole sharing a centroid exactly.

### Why Not Dot Products?

**Avoid manual vector math when robust query, ev, or coordinate transformation functions exist.**

❌ **Wrong approach:**
```featurescript
// Don't do this
const normalDotZ = abs(dot(facePlane.normal, zAxis));
if (normalDotZ > 0.99) { ... }
```

✅ **Correct approach:**
```featurescript
// Use built-in query functions
const cutFaces = qFacesParallelToDirection(flatFaces, zDirection);
```

**Reasons:**
1. Query functions are optimized and tested
2. Avoid floating-point comparison errors
3. More maintainable and readable
4. Less error-prone

**Manual vector math is almost always a sign you have not tried hard enough to use the appropriate functions and must only be used after confirming that an appropriate query, ev, or coordinate transformation function does not exist, when it is relied on explain what the intention of the function was and why it was not found in the standard library**

## Live Onshape — The Browser Session Is The First Lever, Not The MCP

**Round-trip diagnostics go through the browser tooling. This is enforced by a `PreToolUse`
hook (`tools/mcpGate.py`) that denies the metered MCP tools and names the browser command to
run instead** — because a rule that only lives in markdown is a rule that gets skimmed past.

`profiler-tools/` and `drawing-tools/` drive Onshape over a logged-in browser session. Nothing
there is metered, and for diagnostics they report *more* than the MCP does:

| want | run |
| --- | --- |
| Evaluate a snippet, read the error | `SNIPPET='<expr>' node profiler-tools/eval-fs.mjs` |
| Push a local file into its tab | `node profiler-tools/push.mjs [utils\|tester]` |
| Push, rebuild, read every verdict | `node profiler-tools/run-tests.mjs <featureType>...` |
| Verdicts and printlns, no push | `node profiler-tools/notices.mjs` |
| Diff a tab against the local file | `node profiler-tools/sync.mjs` |
| Tab inventory and microversion | `node profiler-tools/elements.mjs` |
| Per-function timings | `TAG=x node profiler-tools/report.mjs --push` |

`profiler-tools/README.md` has the full table; the mechanism and its traps are in
`docs/ONSHAPE_PROFILER_SCRAPING.md` and `docs/ONSHAPE_DRAWINGS_API.md`.

**Why this matters beyond cost:** the MCP returns notices *instead of* console output, so any
run that throws discards every `println` it produced — the diagnostic is destroyed exactly when
it is needed. `eval-fs.mjs` prints notices **and** console together. Reaching for the MCP to
debug a failing build is both the expensive option and the blind one.

Sessions are short-lived, so expect an assisted login. **Ask the user to complete it** rather
than falling back to the MCP.

The MCP (`onshape-featurescript`, configured in `.mcp.json`) spends real API calls from a
metered account. Account queries (`get_api_usage`, `whoami`) stay open. Everything else is
gated; if the browser route genuinely cannot do the job, say so and ask the user to set
`ONSHAPE_MCP_OK=1`. Do not work around the gate any other way. On rate or quota errors, stop
and report — never retry-loop. Full rules: `docs/featurescript-guides/ONSHAPE_MCP_USAGE.md`.

Default to the mirrored standard library and FsDoc for anything that is a reference lookup. A
session that never touches live Onshape at all is a normal outcome.

## Testing Instructions
- Since there is no way to run Onshape in a localized environment here we will rely mostly on comparing code samples with existing functions in the standard library and against the reference docs to ensure consistency with the code base
- Debugging will be done largely via reports delivered via console log
- Leave comments for functional blocks of code to help track down errors
- Do not declare or report success without my confirmation that a build passes all checks. You have no testing environment so confidently declaring success without feedback is unhelpful and reinforces bad practices
