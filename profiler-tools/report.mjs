// report.mjs — the optimization loop, in one command: optionally push the local source into the
// Feature Studio tab, run "Profile Part Studio N", and print the ranked per-function table with
// a diff against a previous run.
//
// Mechanism and its traps: docs/ONSHAPE_PROFILER_SCRAPING.md. The short version:
//   * `.os-caret` opens the toolgroup; MONITORING IS NOT PROFILING, so Profile is picked by name;
//   * profiling does not survive a reload, so it is armed every run;
//   * the result is one collapsed `.fs-profile-data-meta-marker`, and HOVERING it expands the
//     whole table into the DOM at once - there is no gutter annotation and no REST payload.
//
// Times are INCLUSIVE (a caller's total contains its callees') and rows are per CALL SITE, so
// the aggregate view sums duplicates. Profiled time runs ~35% slower than a plain regen; it is a
// relative measure and must never be quoted as a build time.
//
// Usage:
//   node report.mjs                                        profile what the tab already holds
//   node report.mjs --push                                 push custom-features/solidSweepUtils.fs first
//   TAG=after-alloc BASELINE=current node report.mjs --push
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  openSession, startProfiling, pushFeatureStudio, harvestProfileTable, readNotices,
  openNoticePane, readFeatureScriptNotices, OUT_DIR, UTILS_EID, docUrl,
} from './lib.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const EID = process.env.FS_EID ?? UTILS_EID;
const SOURCE = process.env.SOURCE ?? join(HERE, '..', 'custom-features', 'solidSweepUtils.fs');
const PS_NAME = process.env.PS_NAME ?? 'Part Studio 1';
const WAIT_MINUTES = Number(process.env.WAIT_MINUTES ?? 14);
const TAG = process.env.TAG ?? 'profile';
const BASELINE = process.env.BASELINE ?? null;
const TOP = Number(process.env.TOP ?? 30);
const PUSH = process.argv.includes('--push');
// EXPECT is a regex this profile's OWN output must contain, or the run is refused. A profiler can
// time a regeneration of a feature list you have already replaced, and then the table and the
// notices agree with each other while describing the wrong build entirely - measured 2026-08-24,
// where a stale profile reported a 90% improvement for a test that had not run. It must match
// something only the intended build prints; a shared console tag passes on the stale output.
const EXPECT = process.env.EXPECT ? new RegExp(process.env.EXPECT) : null;

const stamp = () => new Date().toISOString().slice(11, 23);
const log = (l) => console.log(`${stamp()} ${l}`);
const toSeconds = (t) => {
  const m = /^([\d.]+)\s*(ms|s)$/i.exec((t ?? '').trim());
  return m ? Number(m[1]) * (m[2].toLowerCase() === 'ms' ? 1e-3 : 1) : null;
};
const fmt = (s) => (s == null ? '-' : s >= 1 ? `${s.toFixed(2)}s` : `${Math.round(s * 1000)}ms`);

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(20_000);

  if (PUSH) {
    const local = await readFile(SOURCE, 'utf8');
    log(`pushing ${basename(SOURCE)} (${local.length} bytes) into tab ${EID}...`);
    const res = await pushFeatureStudio(page, EID, local);
    log(`  status ${res.status}, read back ${res.bytes} bytes, ${res.verified ? 'VERIFIED' : 'MISMATCH'}`);
    if (!res.verified) throw new Error('the tab does not match the local file; not profiling a wrong build');
    // Reload so the editor and the Part Studio both see the new source.
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(22_000);
  }

  // Open the notices pane BEFORE profiling: it is where println output lands, and it collects
  // only while open. Timing alone cannot tell a speedup from a regression on any change that
  // touches sampling, so every cycle now reads the verdict too.
  const paneOpen = await openNoticePane(page);
  log(`notices pane: ${paneOpen ? 'open' : 'NOT FOUND'}`);

  const total = await startProfiling(page, PS_NAME, {
    waitMinutes: WAIT_MINUTES,
    onTick: (left) => log(`  profiling... (${left} min left)`),
  });
  if (!total) {
    const notices = await readNotices(page);
    await page.screenshot({ path: join(OUT_DIR, `${TAG}-failed.png`) });
    console.log('\nNO PROFILE CAME BACK. Notices on the page:');
    for (const n of notices) console.log(`   ${n}`);
    throw new Error('no profile; see out/' + TAG + '-failed.png');
  }
  log(`profile total: ${total}`);

  const notices = await readFeatureScriptNotices(page);
  if (EXPECT && !notices.some((l) => EXPECT.test(l))) {
    await writeFile(join(OUT_DIR, `${TAG}-stale.json`), JSON.stringify({ notices }, null, 2));
    throw new Error(`this profile's notices do not match /${process.env.EXPECT}/ - it timed a ` +
      `DIFFERENT build or feature list. Refusing to report it; see out/${TAG}-stale.json.`);
  }
  const verdict = notices.find((l) => /VERDICT/.test(l)) ?? null;
  const measured = notices.filter((l) => /deviation|volume|seam gap|ledger/i.test(l));

  const harvest = await harvestProfileTable(page);
  await page.screenshot({ path: join(OUT_DIR, `${TAG}-menu.png`) });
  const rows = harvest.items.map((it) => {
    const m = /^([\d.]+\s*(?:ms|s))\s+in\s+([\d,]+)\s+calls?$/i.exec(it.timings);
    return { name: it.name, seconds: m ? toSeconds(m[1]) : null, calls: m ? Number(m[2].replace(/,/g, '')) : null };
  }).filter((r) => r.seconds != null);

  // Aggregate per FUNCTION; the profiler lists one row per call site.
  const agg = new Map();
  for (const r of rows) {
    const a = agg.get(r.name) ?? { name: r.name, seconds: 0, calls: 0, sites: 0 };
    a.seconds += r.seconds; a.calls += r.calls ?? 0; a.sites += 1;
    agg.set(r.name, a);
  }
  const functions = [...agg.values()].sort((a, b) => b.seconds - a.seconds);
  const totalSeconds = toSeconds(total);

  await writeFile(join(OUT_DIR, `${TAG}.json`), JSON.stringify({
    at: new Date().toISOString(), tag: TAG, pushed: PUSH, featureStudio: EID, partStudio: PS_NAME,
    regenLine: harvest.regen, profiledTotal: total, profiledTotalSeconds: totalSeconds,
    note: 'inclusive times; rows aggregated across call sites; profiled time is relative only',
    verdict, notices, functions, rows,
  }, null, 2));

  console.log(`\n--- what the build reported ---`);
  for (const l of measured) console.log(`  ${l.slice(0, 250)}`);
  console.log(`  ${verdict ?? 'NO VERDICT LINE FOUND'}`);
  if (verdict && /FAIL/i.test(verdict)) console.log('  *** THE BUILD FAILED ITS OWN CHECKS ***');

  let prev = null;
  let prevTotal = null;
  if (BASELINE) {
    try {
      const p = JSON.parse(await readFile(join(OUT_DIR, `${BASELINE}.json`), 'utf8'));
      const src = p.functions ?? [];
      prev = new Map(src.map((r) => [r.name, r]));
      prevTotal = p.profiledTotalSeconds;
      log(`baseline "${BASELINE}": ${p.profiledTotal}`);
    } catch (e) { log(`baseline "${BASELINE}" unusable: ${e.message}`); }
  }

  console.log(`\n===  ${TAG}  —  ${harvest.regen ?? total}  ===`);
  if (prevTotal) {
    const d = totalSeconds - prevTotal;
    console.log(`     total ${fmt(totalSeconds)} against baseline ${fmt(prevTotal)}   ${d >= 0 ? '+' : ''}${d.toFixed(2)}s  (${(100 * d / prevTotal).toFixed(1)}%)`);
  }
  console.log(`\n      time   share        calls   us/call  ${prev ? '   vs base  ' : ''}function`);
  for (const r of functions.slice(0, TOP)) {
    const share = totalSeconds ? `${(100 * r.seconds / totalSeconds).toFixed(1)}%` : '-';
    const per = r.calls ? `${(r.seconds / r.calls * 1e6).toFixed(0)}us` : '-';
    let delta = '';
    if (prev) {
      const b = prev.get(r.name);
      delta = b ? `${b.seconds - r.seconds > 0 ? '-' : '+'}${Math.abs(r.seconds - b.seconds).toFixed(2)}s`.padStart(10) + '  ' : '       new  ';
    }
    console.log(`  ${fmt(r.seconds).padStart(8)} ${share.padStart(6)} ${String(r.calls).padStart(12)} ${per.padStart(9)}  ${delta}${r.name}`);
  }
  if (prev) {
    const gone = [...prev.values()].filter((b) => !agg.has(b.name) && b.seconds > 0.3);
    if (gone.length) console.log(`\n  gone from the table: ${gone.map((g) => `${g.name} (${fmt(g.seconds)})`).join(', ')}`);
  }
  console.log(`\n  ${functions.length} functions / ${rows.length} call sites -> out/${TAG}.json`);
} finally {
  await browser.close();
}
