// notices.mjs — the verdict half of the loop, without the profiler.
//
// A test whose value is its printlns needs two things true at once: the FeatureScript notices pane
// open, and the Feature Studio MONITORING (or profiling) a Part Studio, because output only routes
// into a studio while it watches one. Monitoring is enough when the question is what the build
// FOUND rather than how long it took, and it avoids the profiler's ~30% overhead.
//
// It also dumps the toolbar buttons when the toolgroup cannot be found, which is the only part of
// this UI that moves when Onshape restyles it.
//
//   PS_NAME="Analytic Profile Tests" node notices.mjs
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, readFeatureScriptNotices, openNoticePane, waitForRegeneration, OUT_DIR, OTHER_EID, docUrl } from './lib.mjs';

const EID = process.env.FS_EID ?? OTHER_EID;
const PS_NAME = process.env.PS_NAME ?? 'Part Studio 1';
const TAG = process.env.TAG ?? 'notices';
const WAIT_MINUTES = Number(process.env.WAIT_MINUTES ?? 8);
const TOOL = process.env.TOOL ?? 'Monitor';

const stamp = () => new Date().toISOString().slice(11, 19);
const log = (l) => console.log(`${stamp()} ${l}`);

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });

  // Wait for the toolbar itself rather than for a fixed number of seconds: the tool buttons are
  // the last thing Angular renders, and every earlier readiness signal fires before them.
  let caret = null;
  for (let attempt = 0; attempt < 30 && !caret; attempt += 1) {
    await page.waitForTimeout(3000);
    caret = await page.evaluate(() => {
      for (const el of document.querySelectorAll('.os-tool-button')) {
        if (!/Part Studio|Monitor|Profile/.test(el.textContent ?? '')) continue;
        const c = el.querySelector('.os-caret');
        if (!c) continue;
        const r = c.getBoundingClientRect();
        if (r.width === 0) continue;
        return { x: r.x + r.width / 2, y: r.y + r.height / 2, label: (el.textContent ?? '').trim() };
      }
      return null;
    });
  }
  if (!caret) {
    const buttons = await page.evaluate(() => [...document.querySelectorAll('.os-tool-button')]
      .map((el) => (el.textContent ?? '').trim()).filter(Boolean));
    await page.screenshot({ path: join(OUT_DIR, `${TAG}-no-toolgroup.png`) });
    console.log(`toolbar buttons seen: ${JSON.stringify(buttons)}`);
    throw new Error(`no monitor/profile toolgroup; see out/${TAG}-no-toolgroup.png`);
  }
  log(`toolgroup: ${JSON.stringify(caret.label)}`);

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

  // Onshape prints "Result: Regeneration complete" when the build is over - wait for THAT, not for
  // a line count that stopped changing, which cost 40 s of dead waiting per run and could still
  // stop early on a pane that had not begun updating.
  const waited = await waitForRegeneration(page, {
    expect: process.env.EXPECT ? new RegExp(process.env.EXPECT) : null,
    timeoutMs: WAIT_MINUTES * 60_000,
    onTick: (seconds) => log(`  waiting for regeneration... (${seconds}s)`),
  });
  log(`regeneration ${waited.complete ? 'complete' : 'DID NOT COMPLETE'} after ${(waited.waitedMs / 1000).toFixed(1)}s`);
  const lines = await readFeatureScriptNotices(page);

  await writeFile(join(OUT_DIR, `${TAG}.json`), JSON.stringify({ at: new Date().toISOString(), lines }, null, 2));
  await page.screenshot({ path: join(OUT_DIR, `${TAG}.png`), fullPage: false });
  console.log(`\n--- ${lines.length} notice line(s) ---`);
  for (const l of lines) console.log(l);
} finally {
  await browser.close();
}
