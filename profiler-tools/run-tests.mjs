// run-tests.mjs — the loop for a TEST feature, in one session: push the tester, (re)insert the
// named features, force the regeneration, and print what they reported.
//
// Why it exists separately from report.mjs: that one is built around the profiler, whose question
// is how long a build took. A test's question is what it FOUND, and the two differ in one practical
// way - printlns route into a Feature Studio only while it watches a Part Studio, and only a
// regeneration produces any. A Part Studio that is already up to date answers Monitor with silence.
// Re-inserting the features is what guarantees a rebuild, so this does that on purpose rather than
// hoping the tool forces one.
//
// Parameters: an inserted BTMFeature carries only what you POST. The parameter spec's "Default"
// is applied by the UI at insertion time, and defineFeature's defaults map only covers an MCP
// test_feature call - so a boolean you do not send arrives as FALSE, not as undefined, and a test
// whose rows are switches silently runs none of them. BOOL_TRUE names the boolean parameter ids to
// send as true; SEMICOLONS separate GROUPS, and each group becomes its own insert of every named
// feature. That is how a test whose switches must not all be on at once gets run in full: the
// interpreter's step budget is per FEATURE EVALUATION, so three sweeps want three features.
//
// ENUMS names enum parameters the same way, as `parameterId=EnumType.MEMBER`, with `;` separating
// groups so one run can sweep a matrix of configurations. An omitted enum takes the parameter
// spec's first member, which is rarely the case you meant to test.
//
//   PS_EID=<eid> PS_NAME="Analytic Profile Tests" node run-tests.mjs featureTypeA featureTypeB
//   BOOL_TRUE="straight,compareSurrogate;spin,compareSurrogate;arc,compareSurrogate" \
//     node run-tests.mjs sweepSolidMotionMatrixLiveTest
//   ENUMS="pathType=SweepTestPathType.ARC,rotationType=SweepTestRotationType.TWO_AXIS" \
//     node run-tests.mjs sweepRotatingCubeLiveTest
//
// QUANTITIES names length, angle and integer parameters as `parameterId=expression`, the same
// text you would type in the dialog - "3", "0.06 m", "60 deg". Groups split on `;` as above, so
// one invocation can sweep a parameter and show how an answer converges.
//
//   QUANTITIES="patchStations=3;patchStations=5;patchStations=9" \
//     node run-tests.mjs sweepRotatingCubeLiveTest
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  openSession, apiGet, apiPost, pushFeatureStudio, openNoticePane, readFeatureScriptNotices, waitForRegeneration,
  OUT_DIR, DID, WID, OTHER_EID, docUrl,
} from './lib.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const PS_EID = process.env.PS_EID ?? 'ea13555529d7dd12c3b870d3';
const PS_NAME = process.env.PS_NAME ?? 'Analytic Profile Tests';
const TAG = process.env.TAG ?? 'run-tests';
const WAIT_MINUTES = Number(process.env.WAIT_MINUTES ?? 10);
// MONITOR, not Profile: this script reads verdicts and printlns, never the profiler's table, and
// monitoring routes output into the studio just as profiling does without the ~30% profiling
// overhead. Arming the tool is itself a regeneration, so the run costs two - one to arm, one from
// dirtying the feature list - and the arming one might as well be the cheap kind. Use report.mjs
// when the per-function table is what you want.
const TOOL = process.env.TOOL ?? 'Monitor';
const PUSH = !process.argv.includes('--no-push');
// --no-watch inserts and waits, but never arms Monitor. A watched Part Studio is a limited
// resource in a workspace - "too many clients watching Part Studios" is a real refusal - and a run
// whose output is going to be read as GEOMETRY rather than as printlns has no use for one.
const WATCH = !process.argv.includes('--no-watch');
// EXPECT is a regex the new run's output must contain before the wait may end. Without it the
// stability check cannot tell "stable because the regen finished" from "stable because the pane is
// still showing the LAST run's lines" - which silently reports a stale verdict as if it were this
// run's.
//
// It must match something ONLY THE NEW BUILD PRINTS, not merely the test's console tag: a tag is in
// the stale lines too, so gating on it passes immediately on exactly the output you are trying to
// avoid. Measured the hard way twice. A line the change itself introduced is the reliable choice.
const EXPECT = process.env.EXPECT ? new RegExp(process.env.EXPECT) : null;
const BOOL_GROUPS = (process.env.BOOL_TRUE ?? '')
  .split(';')
  .map((group) => group.split(',').map((s) => s.trim()).filter(Boolean))
  .filter((group, index, all) => group.length > 0 || all.length === 1);
// Each entry is `parameterId=EnumType.MEMBER`. A custom enum needs its TYPE NAME and the
// declaring element's namespace as well as the member, which is why the value carries both halves.
const ENUM_GROUPS = (process.env.ENUMS ?? '')
  .split(';')
  .map((group) => group.split(',').map((s) => s.trim()).filter(Boolean).map((entry) => {
    const [parameterId, qualified] = entry.split('=');
    const [enumName, value] = (qualified ?? '').split('.');
    if (!parameterId || !enumName || !value) throw new Error(`ENUMS entry "${entry}" is not parameterId=EnumType.MEMBER`);
    return { parameterId, enumName, value };
  }));
// A quantity parameter carries an EXPRESSION, not a number - the same string the dialog accepts -
// so lengths, angles and unitless integers all go through one parameter type.
const QUANTITY_GROUPS = (process.env.QUANTITIES ?? '')
  .split(';')
  .map((group) => group.split(',').map((s) => s.trim()).filter(Boolean).map((entry) => {
    const at = entry.indexOf('=');
    if (at < 1) throw new Error(`QUANTITIES entry "${entry}" is not parameterId=expression`);
    return { parameterId: entry.slice(0, at), expression: entry.slice(at + 1) };
  }));
const WANTED = process.argv.slice(2).filter((a) => !a.startsWith('--'));
if (!WANTED.length) throw new Error('name at least one feature type');

const featurePath = `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`;
const stamp = () => new Date().toISOString().slice(11, 19);
const log = (l) => console.log(`${stamp()} ${l}`);

const apiDelete = (page, path) => page.evaluate(async (p) => {
  const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
  const xsrf = raw.slice(raw.indexOf('=') + 1);
  const r = await fetch(p, {
    method: 'DELETE', credentials: 'same-origin',
    headers: { accept: 'application/json', 'x-xsrf-token': xsrf },
  });
  return { status: r.status };
}, path);

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(OTHER_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(8000);

  if (PUSH) {
    const local = await readFile(join(HERE, '..', 'custom-features', 'solidSweepTester.fs'), 'utf8');
    const res = await pushFeatureStudio(page, OTHER_EID, local);
    // The tester's same-document import has its version rewritten on commit, so a one-line
    // mismatch there is expected; anything larger is not.
    log(`pushed tester: status ${res.status}, ${res.bytes} bytes, ${res.verified ? 'identical' : 'differs (expect the import version line)'}`);
  }

  let caret = null;
  for (let attempt = 0; WATCH && attempt < 30 && !caret; attempt += 1) {
    await page.waitForTimeout(3000);
    caret = await page.evaluate(() => {
      for (const el of document.querySelectorAll('.os-tool-button')) {
        if (!/Part Studio|Monitor|Profile/.test(el.textContent ?? '')) continue;
        const c = el.querySelector('.os-caret');
        if (!c) continue;
        const r = c.getBoundingClientRect();
        if (r.width === 0) continue;
        return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
      }
      return null;
    });
  }
  if (WATCH && !caret) throw new Error('no monitor/profile toolgroup in the toolbar');
  if (WATCH) {
  const paneOpen = await openNoticePane(page);
  log(`notices pane: ${paneOpen ? 'open' : 'NOT FOUND'}`);

  await page.mouse.click(caret.x, caret.y);
  await page.waitForTimeout(2500);
  const item = await page.evaluate(({ wanted, tool }) => {
    const items = [];
    for (const el of document.querySelectorAll('.os-menu-tool')) {
      const text = (el.textContent ?? '').trim();
      items.push(text);
      if (text !== `${tool} ${wanted}`) continue;
      const r = el.getBoundingClientRect();
      return { x: r.x + r.width / 2, y: r.y + r.height / 2, items };
    }
    return { x: null, y: null, items };
  }, { wanted: PS_NAME, tool: TOOL });
  if (item.x == null) throw new Error(`"${TOOL} ${PS_NAME}" not in the dropdown; saw ${JSON.stringify(item.items)}`);
  await page.mouse.click(item.x, item.y);
  log(`clicked "${TOOL} ${PS_NAME}"`);
  } else {
    log('not watching: inserting only, output will not route to the notices pane');
  }


  // Only now dirty the feature list: the pane is open and the studio is watching, so the
  // regeneration this causes is the one that lands in it. Doing it the other way round means
  // racing your own trigger - the insert regenerates server-side at once, and the pane opens
  // afterwards onto output that has already gone.
  // Delete last-first, then re-insert: the delete is what forces the rebuild that produces output,
  // and the fresh namespace is what makes the feature run the code just pushed.
  const before = await apiGet(page, featurePath);
  for (const f of [...(before.body?.features ?? [])].reverse()) {
    if (!WANTED.includes(f.featureType)) continue;
    await apiDelete(page, `${featurePath}/featureid/${encodeURIComponent(f.featureId)}`);
    await page.waitForTimeout(1500);
  }
  const mv = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/currentmicroversion`);
  const namespace = `e${OTHER_EID}::m${mv.body?.microversion}`;
  const groupCount = Math.max(BOOL_GROUPS.length, ENUM_GROUPS.length, QUANTITY_GROUPS.length);
  for (let groupIndex = 0; groupIndex < groupCount; groupIndex += 1) {
    // A single group in one variable applies to EVERY group of the others, so sweeping one
    // parameter does not mean restating the configuration it is swept inside.
    const group = BOOL_GROUPS[groupIndex] ?? BOOL_GROUPS[0] ?? [];
    const enums = ENUM_GROUPS[groupIndex] ?? ENUM_GROUPS[0] ?? [];
    const quantities = QUANTITY_GROUPS[groupIndex] ?? QUANTITY_GROUPS[0] ?? [];
    const parameters = [
      ...group.map((parameterId) => ({
        btType: 'BTMParameterBoolean-144',
        parameterId,
        value: true,
      })),
      ...enums.map(({ parameterId, enumName, value }) => ({
        btType: 'BTMParameterEnum-145',
        parameterId,
        enumName,
        namespace,
        value,
      })),
      ...quantities.map(({ parameterId, expression }) => ({
        btType: 'BTMParameterQuantity-147',
        parameterId,
        expression,
      })),
    ];
    for (const featureType of WANTED) {
      const labels = [
        ...group,
        ...enums.map((e) => `${e.parameterId}=${e.value}`),
        ...quantities.map((q) => `${q.parameterId}=${q.expression}`),
      ];
      const suffix = labels.length ? ` [${labels.join(',')}]` : '';
      const res = await apiPost(page, featurePath, {
        feature: {
          btType: 'BTMFeature-134', featureType, name: `${featureType}${suffix}`, namespace,
          suppressed: false, parameters, subFeatures: [], returnAfterSubfeatures: false,
        },
      });
      log(`insert ${featureType}${suffix}: ${res.status}${res.status >= 400 ? ` ${JSON.stringify(res.body).slice(0, 200)}` : ''}`);
      await page.waitForTimeout(2500);
    }
  }

  const waited = !WATCH ? { complete: true, waitedMs: 0, stalls: 0 } : await waitForRegeneration(page, {
    expect: EXPECT,
    timeoutMs: WAIT_MINUTES * 60_000,
    onTick: (seconds) => log(`  waiting for "Regeneration complete"${EXPECT ? " + this build's own output" : ""}... (${seconds}s)`),
  });
  log(`regeneration ${waited.complete ? 'complete' : 'DID NOT COMPLETE'} after ${(waited.waitedMs / 1000).toFixed(1)}s` +
    `${waited.stalls ? `, ${waited.stalls} stalled poll(s) - the page stopped servicing JS` : ''}`);
  const lines = WATCH ? await readFeatureScriptNotices(page) : [];
  const sawExpected = !EXPECT || lines.some((l) => EXPECT.test(l));
  if (EXPECT && !sawExpected) {
    console.log(`
WARNING: never saw output matching /${process.env.EXPECT}/. Either the pane is` +
      ` showing a STALE regen, or this run inserted a different PARAMETER CONFIGURATION than the one` +
      ` EXPECT describes - note that this script re-inserts from its OWN BOOL_TRUE/BOOL_FALSE, so` +
      ` configuring the feature with insert-tests.mjs first and then running this without the same` +
      ` env silently replaces it. The lines below identify what actually ran.`);
  }

  await writeFile(join(OUT_DIR, `${TAG}.json`), JSON.stringify({ at: new Date().toISOString(), lines }, null, 2));
  await page.screenshot({ path: join(OUT_DIR, `${TAG}.png`) });
  console.log(`\n--- ${lines.length} notice line(s) ---`);
  for (const l of lines) console.log(l);
  const verdicts = lines.filter((l) => /VERDICT/.test(l));
  if (verdicts.length) {
    console.log('\n--- verdicts ---');
    for (const v of verdicts) console.log(v);
  }
} finally {
  await browser.close();
}
