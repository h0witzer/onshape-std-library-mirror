# profiler-tools

Drives the Onshape FeatureScript profiler and pushes source into tabs, over a logged-in browser
session. No API key, nothing metered. Full mechanism and every wrong turn:
`docs/ONSHAPE_PROFILER_SCRAPING.md`.

## The loop

```bash
# edit custom-features/solidSweepUtils.fs, then:
TAG=my-change BASELINE=after-fix node report.mjs --push
```

That single command pushes the local file into the tab, reads it back and refuses to continue
unless it matches byte for byte, opens the FeatureScript notices pane, runs
"Profile Part Studio 1", and prints:

- **what the build reported** — the test's own printlns and its VERDICT line, so a change that
  alters an ANSWER is visible, not just one that alters the clock;
- **the ranked per-function table** with call counts and a diff column against `BASELINE`.

Everything lands in `out/<TAG>.json`. Sessions are short-lived, so expect an assisted login.

## Other entry points

| command | what it does |
| --- | --- |
| `node report.mjs` | profile whatever the tab already holds |
| `node push.mjs [utils\|tester]` | push and verify, no profiling |
| `node sync.mjs` | read-only diff of tab against local file |
| `node verdict.mjs` | what Part Studio 1 contains and how its features are doing |
| `node run-tests.mjs <featureType>...` | the loop for a TEST: push the tester, re-insert the features (which forces the rebuild), profile, print the verdicts |
| `node notices.mjs` | verdicts and printlns without pushing or inserting: watch a Part Studio and read the notices pane |
| `node elements.mjs` | the document's tab inventory and its current microversion |
| `node new-partstudio.mjs` | create an empty Part Studio tab (`NAME=...`) |
| `node insert-tests.mjs <featureType>...` | insert test features from the tester tab into a Part Studio, replacing stale copies |
| `node solo-insert.mjs <featureType>...` | insert each one ALONE, clearing between, so the status is a per-feature verdict |
| `node status.mjs` | per-feature regen status for a Part Studio |

Environment: `FS_EID`, `SOURCE`, `PS_NAME`, `PS_EID`, `TAG`, `BASELINE`, `TOP`, `WAIT_MINUTES`,
`TOOL`, `NAME`, `KEEP_LAST`.

**A Part Studio that is already up to date answers Monitor with silence.** Output routes into a
Feature Studio only while it watches a Part Studio, and only a REGENERATION produces any - so
watching a build that has nothing to rebuild reads as zero notice lines, which looks exactly like a
broken selector. `run-tests.mjs` re-inserts the features first for that reason: the delete is what
forces the rebuild.

**A pushed tester tab reads back as MISMATCH, and that is correct.** The tester's same-document
import carries a `version`, and Onshape REWRITES it to the tab's own latest microversion on commit —
so the read-back differs from the local file on exactly that one line, with the byte count and line
count unchanged. `sync.mjs` shows which line diverged; anything beyond line 13 of the tester is a
real mismatch.

## Reading the output

- Times are **inclusive** — a caller's total contains its callees'. Subtract for self time.
- The table's rows are per **call site**; `report.mjs` aggregates them per function.
- Profiled time runs ~30% slower than a plain regen. It is a **relative** measure and must never
  be quoted as a build time — only Onshape's own compute-time readout counts for that.

## Discovery scripts

`explore.mjs`, `probe-debug.mjs`, `capture.mjs`, `menu.mjs`, `inspect-gutter.mjs`, `expand.mjs`,
`harvest.mjs`, `find-console.mjs`, `notices.mjs`. Kept deliberately: the next time Onshape
restyles this UI, these are what find it again.
