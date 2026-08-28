# Scraping the Onshape FeatureScript profiler

How `profiler-tools/` gets per-function timings out of Onshape without an API key, and the five
wrong turns worth not repeating. Same unmetered browser-session route as the std-library mirror
updater and `drawing-tools/` — Playwright plus the session cookie, nothing counted against the
Onshape API quota.

Built 2026-08-24 to close the measurement loop on the solid sweep optimization work
(`docs/specs/SOLID_SWEEP_SPEC.md` §11).

## The loop

```bash
TAG=my-change BASELINE=after-fix node profiler-tools/report.mjs --push
```

One command: push the local file into the tab, read it back and refuse to continue unless it
matches byte for byte, open the notices pane, profile, and print **both** the build's own
reported numbers (verdict, deviation, seam gap, volume) and the ranked per-function table with a
diff column. `profiler-tools/README.md` has the rest of the entry points.

**Pushing source over the session is not metered and not size-limited.** The note elsewhere that
these files must be pasted by hand is an *MCP* constraint — `put_featurescript` carries the file
as a tool parameter, so 512 KB will not fit in a message. A session POST body never passes
through a model. Onshape keeps a microversion per edit, so the tab's history stays intact.

## What it is

The profiler is a **Feature Studio** feature, not a Part Studio one. Its results are per-function
inclusive times and call counts for the code in the Feature Studio you are looking at, gathered
while a chosen Part Studio regenerates.

```
node profiler-tools/report.mjs                              # profile Part Studio 1 from SolidSweepUtils
TAG=after-fix BASELINE=current node profiler-tools/report.mjs   # same, with a diff column
FS_EID=<tester eid> PS_NAME="Part Studio 2" node profiler-tools/report.mjs
```

Output lands in `profiler-tools/out/<TAG>.json` (full table) plus a ranked console summary.

## The five things that cost time to learn

**1. Monitoring is not profiling.** The toolbar widget is a *toolgroup*. Its main button runs
whichever tool was used last, and its label is `Monitor Part Studio 1` when idle. Clicking that
gives you a total regen time and nothing else. The real commands are behind the caret
(`.os-caret`, `ng-click="toggleDropdownMenu()"`): `Monitor`/`Profile` × `Part Studio 1`/`2`, all
four sharing `command-id="SET_WATCHED_PART_STUDIO"` and differing only in `command-details`.
`startProfiling()` in `lib.mjs` always goes through the caret and picks *Profile* by name, so it
cannot silently harvest a monitoring run.

**2. Profiling does not survive a page reload.** It has to be armed on every run.

**3. The results are not in the gutter.** `.ace_gutter-cell` carries Ace's fold widgets and
nothing else. Scrolling the whole file to collect per-line annotations harvests the *same sticky
badge* over and over — 311 identical "31.5s" rows, one per scroll step, which looks like data.

**4. The result is one collapsed badge.** `div.fs-profile-data-meta-marker`, pinned to the
editor's top-left, containing `<p class="regen-time">` and the give-away
`<p class="menu-description">Longest execution steps in this Feature Studio:</p>`.

**5. The table is a hover affordance.** Hovering that badge expands `.fs-profiled-line-menu`,
whose `.fs-profiled-line-menu-item` children each hold a name plus a
`.profile-timings` reading like `23.0s in 10851 calls`. **Every entry is in the DOM at once**
(~900 of them), so no scrolling, no per-row hovering, and no reverse-engineering of a payload is
needed. There is no REST endpoint to find: `/api/debug/d/{did}/timerflag` (`{"enableTimers":…}`)
is the only profiling-shaped path the client ever requests, and every neighbouring name 404s.

## Running a NEW test: inserting features, and the 409 that lies

A test whose value is its printlns has to run as a **feature in this document**. The MCP's
`test_feature` cannot do it — that runs in the server's own scratch document, where the tester's
same-document import of the utils tab does not resolve — so the feature list is written over the
session instead (`insert-tests.mjs`, unmetered like everything else here).

Two things about that, both learned the hard way on 2026-08-24.

**The namespace has to be read fresh.** A same-document custom feature is referenced as
`e<eid>::m<microversion>`, and the microversion moves every time any tab is written. `elements.mjs`
prints the current one; `insert-tests.mjs` reads it itself and deletes any stale copy of the feature
rather than leaving one pointing at the code it was inserted against.

**An inserted feature's parameters come from the parameter SPEC, not from `defineFeature`'s defaults
map.** A `BTMFeature` posted with `parameters: []` takes each value from its own annotation, so an
omitted boolean whose spec says `"Default" : true` arrives **true**, and one with no `"Default"`
arrives **false**. `defineFeature`'s defaults map - which does fill in for an MCP `test_feature` call
- never gets a say. Two consequences, both measured 2026-08-24:

- a boolean a programmatic insert must see as ON needs `"Default" : true` in its annotation, or the
  insert must send it. The symptom of neither is a test that returns in seconds having skipped
  everything it was meant to do;
- **a `!= false` guard cannot switch such a flag off** when nothing is sent at all. Once the spec
  default is true, omitting the whole parameter list leaves the flag ON, so an "only route A"
  profile silently runs both routes.

**The rule the evidence supports** (inferred from four runs, not an isolated experiment — treat it
as a working rule and send parameters explicitly when it matters):

| what the insert POSTs | what an unsent boolean becomes |
| --- | --- |
| `parameters: []` | the parameter spec's `"Default"` |
| a NON-EMPTY `parameters` array | **false**, whatever the spec default says |

So sending one flag implicitly clears the others. That is convenient for isolating a route — send
only the one you want — but it means a partial parameter list is never a partial override.

**`409 The change would result in the feature list becoming invalid` is usually not about the
feature you are adding.** The check runs against the list as it already stands, so:

1. the first feature you insert is accepted, because the list was valid before it;
2. if that feature then ERRORS at regeneration, the list is now invalid;
3. every later insert is refused with the 409 — naming nothing, blaming nothing.

So a 409 on the second insert means the FIRST one is broken. The way to turn the status back into a
per-feature verdict is `solo-insert.mjs`, which clears the tab before each insert: a feature accepted
alone but refused after another is in tells you which one errors.

And the reason a feature errors for no visible reason is worth stating, because it cost most of a
session: **a reserved-word collision in an imported element does not report as a syntax error
anywhere.** `const box = evBox3d(...)` — `box` is the mutable-box type of `new box(0)` — broke
`solidSweepUtils.fs`, and the only symptom was every feature in the tester failing to regenerate and
the 409 above. Suspect the imported element's compile before suspecting your own logic.

## The verdict, which matters more than the clock

`println` output and each feature's verdict land in the **FeatureScript notices** pane. Two
things have to be true before any of it is in the DOM, which is why it took several attempts:

0. **The pane renders only what is scrolled into view, and its lines are not leaf elements.**
   This is the opposite of the profiler table above, and assuming they behaved alike made two
   passing runs read as silent ones. A notice line wraps its text in child spans, so a
   `childElementCount === 0` filter skips every one; and the lines below the fold are not in the DOM
   to be found at all. `readFeatureScriptNotices` therefore harvests `innerText`, scrolls each
   scrollable pane a screen at a time, and unions the results.
1. **The pane is a navbar flyout, closed by default** — `NavbarController.toggleNoticePane()`, on
   `.notice-pane-toggle-button`. Click it through Angular's own handler; a pixel click on the
   toggle gets swallowed and leaves the pane shut while reporting success. Open it *before*
   profiling, since it collects only while open.
2. **Output only routes into a Feature Studio while that studio monitors or profiles a Part
   Studio.** This is exactly what the Monitor tooltip means by "view runtime notices from this
   Part Studio inside this document's Feature Studios". And only a REGENERATION produces any
   output: watching a Part Studio that is already up to date reads as zero lines, which looks
   identical to a broken selector. `run-tests.mjs` re-inserts the features first for exactly that
   reason — the delete is what forces the rebuild.

The pane's own container carries no class worth matching on, so read leaf text instead — that
also survives Onshape restyling it.

**This is not optional polish.** The first change this loop measured looked like a 3.6 s win and
was a silent regression: it moved the reported fit deviation from 4.638e-8 to 1.622e-7, a 3.5x
error, while still passing every check and while the stopwatch said it was faster. A timing-only
loop would have shipped it. See spec §11.6.

## Reading the numbers

- **Times are INCLUSIVE.** A caller's total contains its callees', which is why the top of the
  table reads as one call chain rather than as disjoint costs. Subtract to get self time.
- **Rows are per CALL SITE, not per function.** A function called from two places appears twice,
  with different call counts. Sum them for the function's true total — this is what makes
  `leanBasisDerivatives` look like two 7.4 s entries rather than one 14.8 s entry.
- **Profiled time is slower than a real regen** and is a *relative* measure only. The run that
  measured 23 s in the UI profiles at ~31 s. Never quote a profiled number as a build time; the
  spec's rule (§11) that only Onshape's own compute-time readout counts still holds.
- `Block`, `Assignment`, `For loop` and `makeArray` rows are the interpreter's own primitives.
  They are the honest cost of interpreted FeatureScript and are where allocation-heavy inner
  loops show up.

## Files

| file | what it does |
| --- | --- |
| `lib.mjs` | re-exports the session machinery from `drawing-tools/lib.mjs`; adds document ids and `startProfiling()` |
| `report.mjs` | **the tool** — push, arm, wait, expand, parse, rank, read the verdict, diff against a baseline |
| `push.mjs` | push and verify one or both tabs, no profiling |
| `sync.mjs` | read-only diff of a tab against its local file |
| `verdict.mjs` | what Part Studio 1 contains and how its features are doing |
| `explore.mjs`, `probe-debug.mjs`, `capture.mjs` | the discovery scripts, kept because the next UI change will need them again |
| `menu.mjs`, `inspect-gutter.mjs`, `expand.mjs`, `harvest.mjs` | the intermediate probes that found the toolgroup, the marker class, and the hover menu |

Session state is shared with `drawing-tools/.auth/`; Onshape sessions are short-lived, so expect
an assisted login most runs. Related: `docs/ONSHAPE_DRAWINGS_API.md`.
