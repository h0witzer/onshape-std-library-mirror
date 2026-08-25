// lib.mjs — the profiler tools' entry to Onshape.
//
// The session machinery is NOT duplicated here: `drawing-tools/lib.mjs` already owns it, and it
// is the same unmetered browser-session route (Playwright, session cookie, no API key, nothing
// counted against the Onshape quota). This file only re-points it at the sweep document and
// gives these tools their own output directory. Playwright resolves out of
// drawing-tools/node_modules because that is where the importing file lives.
export { openSession, apiGet, apiPost } from '../drawing-tools/lib.mjs';
// `export ... from` re-exports without binding locally, and the helpers below call these.
import { apiGet, apiPost } from '../drawing-tools/lib.mjs';

import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
export const OUT_DIR = join(HERE, 'out');

// The solid sweep development document (owner, 2026-08-24) is the default. OS_DID / OS_WID /
// OS_UTILS_EID point the same tooling at another document — these scripts are the unmetered
// route to ANY Onshape document, not just this one.
export const DID = process.env.OS_DID ?? '9576a1791057cd3c538a7826';
export const WID = process.env.OS_WID ?? '4eb9c6e3e3a274ceddf5d819';
export const UTILS_EID = process.env.OS_UTILS_EID ?? '5c27bfb0b1dfa896edb18563'; // SolidSweepUtils feature studio
export const OTHER_EID = 'eb066ca2f397f85c926d9bf7'; // the second tab the owner named

export const docUrl = (eid) => `https://cad.onshape.com/documents/${DID}/w/${WID}/e/${eid}`;

/**
 * Arm the Feature Studio's profiler on a named Part Studio and wait for the numbers to arrive.
 *
 * The toolbar widget is a TOOLGROUP. Its main button runs whichever tool was used last, and the
 * caret (`.os-caret`, ng-click="toggleDropdownMenu()") opens the four real commands: Monitor and
 * Profile, for each Part Studio. Monitoring is not profiling — it re-regenerates and surfaces
 * runtime notices, but only Profile carries per-function timings — so this always goes through
 * the caret and picks Profile by name rather than trusting the default tool.
 *
 * Profiling does NOT survive a page reload: the tool has to be clicked every run.
 *
 * Returns the total duration string the profiler puts on the first gutter row, or null on timeout.
 */
export async function startProfiling(page, psName = 'Part Studio 1', { waitMinutes = 12, onTick } = {}) {
  // RETRY rather than trust a fixed wait: the tool buttons are the last thing Angular renders and
  // every earlier readiness signal fires before them, so a single look loses a run to a page that
  // was merely slow. Matching on Monitor/Profile too, because the button's idle label is whichever
  // tool ran last and need not mention a Part Studio at all.
  var caret = null;
  for (let attempt = 0; attempt < 30 && !caret; attempt += 1) {
    if (attempt > 0) await page.waitForTimeout(3000);
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
  if (!caret) throw new Error('profiler toolgroup not found in the toolbar after 90s');
  await page.mouse.click(caret.x, caret.y);
  await page.waitForTimeout(2000);

  const item = await page.evaluate((wanted) => {
    for (const el of document.querySelectorAll('.os-menu-tool')) {
      if ((el.textContent ?? '').trim() !== `Profile ${wanted}`) continue;
      const r = el.getBoundingClientRect();
      return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
    }
    return null;
  }, psName);
  if (!item) throw new Error(`"Profile ${psName}" is not in the dropdown`);
  await page.mouse.click(item.x, item.y);

  // Read the PROFILER'S OWN badge, not the first thing on the page shaped like a duration. The
  // scan-everything version matched a part named "1S" and reported `profile total: 1S` forty
  // seconds before profiling finished, after which the table harvest timed out looking for a badge
  // that had not appeared yet. `.fs-profile-data-meta-marker > .regen-time` is the element that
  // exists only once there IS a profile, which is the condition actually being waited on.
  const readTotal = () => page.evaluate(() => {
    const marker = document.querySelector('.fs-profile-data-meta-marker');
    if (!marker) return null;
    const regen = marker.querySelector('.regen-time');
    const text = (regen?.textContent ?? '').trim();
    if (!text) return null;
    const match = /(\d+(?:\.\d+)?\s*(?:ms|s))/i.exec(text);
    return match ? match[1] : text;
  });
  const deadline = Date.now() + waitMinutes * 60_000;
  var pollCount = 0;
  while (Date.now() < deadline) {
    const t = await readTotal().catch(() => null);
    if (t) return t;
    await page.waitForTimeout(1000);
    if (++pollCount % 30 === 0) onTick?.(Math.round((deadline - Date.now()) / 60_000));
  }
  return null;
}

/**
 * Overwrite a Feature Studio tab with `contents`, then read it back and confirm.
 *
 * The note that these files must be pasted by hand is an MCP limit, not an Onshape one: a
 * `put_featurescript` call carries the file as a tool parameter, so a 512 KB write does not fit
 * in a message. A session POST body never passes through a model, so this route carries the
 * whole file for nothing. Onshape keeps a microversion per edit, so the tab's history is intact.
 *
 * Returns { status, verified, bytes }.
 */
export async function pushFeatureStudio(page, eid, contents) {
  const res = await apiPost(page, `/api/v14/featurestudios/d/${DID}/w/${WID}/e/${eid}`, { contents });
  await page.waitForTimeout(2500);
  const back = await apiGet(page, `/api/v14/featurestudios/d/${DID}/w/${WID}/e/${eid}`);
  const after = back.body?.contents ?? '';
  // Onshape REWRITES the version of a same-document import on commit - the tester's import of the
  // utils tab becomes that tab's own latest microversion - so a byte comparison reports a mismatch
  // on exactly that line even when the write was perfect. Normalising it out keeps `verified`
  // meaning "the code I sent is the code in the tab", which is the only thing it is for. A
  // same-document import is the one whose path is a bare element id.
  const norm = (t) => t
    .replace(/\r\n/g, '\n')
    .replace(/(import\(path\s*:\s*"[0-9a-f]{24}"\s*,\s*version\s*:\s*")[0-9a-f]{24}(")/g, '$1SAME_DOCUMENT$2');
  return { status: res.status, verified: norm(after) === norm(contents), bytes: after.length };
}

/**
 * The profiler's ranked table, read out of the expanded badge menu.
 * Times are INCLUSIVE and rows are per CALL SITE - see docs/ONSHAPE_PROFILER_SCRAPING.md.
 */
export async function harvestProfileTable(page) {
  const box = await page.locator('.fs-profile-data-meta-marker').first().boundingBox();
  if (!box) return null;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.waitForTimeout(3000);
  return page.evaluate(() => {
    const regen = document.querySelector('.regen-time')?.textContent?.trim() ?? null;
    const items = [];
    for (const el of document.querySelectorAll('.fs-profiled-line-menu-item')) {
      const timings = el.querySelector('.profile-timings')?.textContent?.trim() ?? '';
      let name = (el.textContent ?? '').trim();
      if (timings && name.endsWith(timings)) name = name.slice(0, -timings.length).trim();
      items.push({ name, timings });
    }
    return { regen, items };
  });
}

/**
 * Open the "FeatureScript notices" pane - the bottom panel that collects `println` output and
 * every feature's verdict.
 *
 * Two things make it findable only in this order. It is a navbar flyout,
 * `NavbarController.toggleNoticePane()`, CLOSED by default, so nothing it holds is in the DOM
 * until it is opened; and output only routes into a Feature Studio while that studio MONITORS or
 * PROFILES a Part Studio, which is what the Monitor tooltip means by "view runtime notices from
 * this Part Studio inside this document's Feature Studios". Open it before profiling.
 *
 * Clicked through Angular's own handler rather than by pixel: a pixel click on the toggle gets
 * swallowed and leaves the pane shut while reporting success.
 */
export async function openNoticePane(page) {
  const opened = await page.evaluate(() => {
    const el = document.querySelector('.notice-pane-toggle-button');
    if (!el) return false;
    const flyout = document.querySelector('.flyout-toggle-button');
    if (flyout && /os-expanded/.test(String(flyout.className))) return true; // already open
    el.click();
    return true;
  });
  await page.waitForTimeout(3500);
  return opened;
}

/**
 * Wait for the regeneration to ACTUALLY finish, rather than for a timer.
 *
 * Onshape prints `Result: Regeneration complete` into the notices pane, which is the closing
 * condition; inferring "probably done" from a line count that stopped changing cost 40 s of dead
 * waiting per run and could still stop early on a pane that had not started updating yet.
 *
 * `expect` is required alongside it because the completion line SURVIVES from the previous regen -
 * on its own it is satisfied the instant the pane opens. Together they mean "this run's output is
 * present and the run is over". `expect` must match something only this build prints; a shared
 * console tag is satisfied by the stale lines too.
 *
 * The poll is deliberately a cheap innerText test rather than `readFeatureScriptNotices`, which
 * scrolls every pane and costs ~150 ms a step - harvest once at the end, poll at a second.
 *
 * Returns { complete, waitedMs }.
 */
export async function waitForRegeneration(page, { expect = null, timeoutMs = 8 * 60_000, pollMs = 1000, onTick = null } = {}) {
  const deadline = Date.now() + timeoutMs;
  const started = Date.now();
  let ticks = 0;
  let stalls = 0;
  // page.evaluate has NO timeout of its own: if the page stops servicing JS the call blocks
  // indefinitely, the loop never reaches its own deadline test, and a 5-minute wait runs for 37.
  // Racing each poll against a timer is what makes `timeoutMs` mean anything. Measured 2026-08-24.
  const poll = () => Promise.race([
    page.evaluate((expectSource) => {
      const text = document.body.innerText ?? '';
      return {
        complete: /Regeneration complete/.test(text),
        expected: expectSource ? new RegExp(expectSource).test(text) : true,
      };
    }, expect ? expect.source : null),
    new Promise((resolve) => setTimeout(() => resolve({ stalled: true }), 15_000)),
  ]).catch(() => ({ stalled: true }));

  while (Date.now() < deadline) {
    const state = await poll();
    if (state.stalled) {
      stalls += 1;
      onTick?.(Math.round((Date.now() - started) / 1000), state);
      continue;
    }
    if (state.complete && state.expected) {
      // One short settle so the last line is rendered before the harvest reads it.
      await page.waitForTimeout(400);
      return { complete: true, waitedMs: Date.now() - started, stalls };
    }
    if (++ticks % 15 === 0) onTick?.(Math.round((Date.now() - started) / 1000), state);
    await page.waitForTimeout(pollMs);
  }
  return { complete: false, waitedMs: Date.now() - started, stalls };
}

/**
 * The notice pane's lines.
 *
 * Read through innerText and through SCROLLING, not by matching leaf elements. Two things make the
 * obvious version return nothing at all, and both cost a full profiling cycle to find:
 *
 *   * a notice line is not a leaf - it wraps its text in child spans, so a
 *     `childElementCount === 0` filter skips every one of them;
 *   * the pane only renders the lines near its scroll position, so whatever is below the fold is
 *     not in the DOM to be found. The profiler's own table IS all present at once
 *     (docs/ONSHAPE_PROFILER_SCRAPING.md); the notices are not, and assuming they behaved the same
 *     way is what made a passing run look like a silent one.
 *
 * So: harvest, scroll a pane-height, harvest again, union. Cheap, and it cannot miss a line that
 * ever rendered.
 */
export async function readFeatureScriptNotices(page) {
  return page.evaluate(async () => {
    const wanted = /^\[|VERDICT|Regeneration complete|^Result:/;
    const seen = new Set();
    const harvest = () => {
      for (const raw of (document.body.innerText ?? '').split('\n')) {
        const t = raw.trim();
        if (t && t.length <= 2000 && wanted.test(t)) seen.add(t);
      }
    };
    harvest();
    const panes = [...document.querySelectorAll('div')].filter((el) =>
      el.scrollHeight > el.clientHeight + 20 && /^\[|VERDICT/m.test(el.innerText ?? ''));
    for (const pane of panes) {
      const step = Math.max(120, pane.clientHeight - 40);
      for (let y = 0; y <= pane.scrollHeight + step; y += step) {
        pane.scrollTop = y;
        await new Promise((resolve) => setTimeout(resolve, 150));
        harvest();
      }
      pane.scrollTop = pane.scrollHeight;
      await new Promise((resolve) => setTimeout(resolve, 150));
      harvest();
    }
    return [...seen];
  });
}

/** Whatever the Feature Studio is complaining about - the first thing to read when a push breaks a build. */
export async function readNotices(page) {
  return page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('.ace_gutter-cell.ace_error, .ace_gutter-cell.ace_warning, .fs-error, .os-notice, [class*="error-message"]')) {
      const t = (el.textContent ?? '').trim();
      if (t) out.push(t.slice(0, 300));
    }
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      const t = (el.textContent ?? '').trim();
      if (/^(Unused declaration|.*expected.*|.*undefined.*)$/i.test(t) && t.length < 200) out.push(t);
    }
    return [...new Set(out)].slice(0, 25);
  });
}
