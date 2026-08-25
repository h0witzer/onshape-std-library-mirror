// harvest.mjs — run "Profile Part Studio N" against a Feature Studio and pull every per-function
// timing out of the editor into a ranked report.
//
// Where the numbers live, since it took four runs to find out: NOT on Ace's `.ace_gutter-cell`
// (those only carry fold widgets). Onshape paints its own overlay into `.content-div`, one
// `div.fs-profile-data-meta-marker` per profiled line, each holding an icon and a
// `<p class="regen-time">` with the duration. The marker's own tooltip attributes carry the
// detail the UI shows on hover — call counts and the like — so they are read straight off the
// element rather than by hovering 200 times.
//
// Ace virtualizes, so only the rendered lines exist at any moment: the file has to be walked.
//
// Usage:  node harvest.mjs
//         TAG=after-lever-c PS_NAME="Part Studio 1" node harvest.mjs
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { openSession, startProfiling, OUT_DIR, UTILS_EID, docUrl } from './lib.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const EID = process.env.FS_EID ?? UTILS_EID;
const PS_NAME = process.env.PS_NAME ?? 'Part Studio 1';
const WAIT_MINUTES = Number(process.env.WAIT_MINUTES ?? 14);
const TAG = process.env.TAG ?? 'profile';
// The local copy of the same source, used only to name the function each marked line sits in.
const SOURCE = process.env.SOURCE ?? join(HERE, '..', 'custom-features', 'solidSweepUtils.fs');

const stamp = () => new Date().toISOString().slice(11, 23);
const log = (l) => console.log(`${stamp()} ${l}`);

/** Seconds from "31.8s" / "412ms" / "1.2 s". */
const toSeconds = (text) => {
  const m = /^([\d.]+)\s*(ms|s)$/i.exec((text ?? '').trim());
  if (!m) return null;
  return Number(m[1]) * (m[2].toLowerCase() === 'ms' ? 1e-3 : 1);
};

/** line number (1-based) -> enclosing top-level function name, from the local source. */
async function functionIndex(path) {
  let text;
  try { text = await readFile(path, 'utf8'); } catch { return null; }
  const lines = text.split('\n');
  const owner = new Array(lines.length + 1).fill(null);
  let current = null;
  for (let i = 0; i < lines.length; i += 1) {
    const m = /^(?:export\s+)?(?:function|predicate)\s+(\w+)\s*\(/.exec(lines[i]);
    if (m) current = m[1];
    else if (/^\}/.test(lines[i])) { owner[i + 1] = current; current = null; continue; }
    owner[i + 1] = current;
  }
  return owner;
}

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  log(`opening the Feature Studio (${EID})`);
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(22_000);

  const total = await startProfiling(page, PS_NAME, {
    waitMinutes: WAIT_MINUTES,
    onTick: (left) => log(`  waiting for the profile... (${left} min left)`),
  });
  if (!total) throw new Error('the profile never came back');
  log(`profile total: ${total}`);
  await page.screenshot({ path: join(OUT_DIR, `${TAG}-top.png`) });

  // One full marker's markup, so the shape is on record rather than assumed.
  const sample = await page.evaluate(() => {
    const el = document.querySelector('.fs-profile-data-meta-marker');
    return el ? el.outerHTML.slice(0, 2500) : null;
  });
  await writeFile(join(OUT_DIR, `${TAG}-marker-sample.html`), sample ?? '(none)');

  const readMarkers = () => page.evaluate(() => {
    const editor = document.querySelector('.ace_editor')?.env?.editor;
    const out = [];
    for (const el of document.querySelectorAll('.fs-profile-data-meta-marker')) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      const time = el.querySelector('.regen-time')?.textContent?.trim() ?? null;
      // Bootstrap keeps the real tooltip text on the element, so no hover is needed.
      const attrs = {};
      for (const a of el.attributes) {
        if (/^(title|data-bs-original-title|data-bs-expanded-content|aria-label)$/i.test(a.name)) attrs[a.name] = a.value.slice(0, 1200);
      }
      for (const child of el.querySelectorAll('*')) {
        for (const a of child.attributes) {
          if (/^(title|data-bs-original-title|data-bs-expanded-content)$/i.test(a.name) && a.value) {
            attrs[`${child.tagName.toLowerCase()}:${a.name}`] = a.value.slice(0, 1200);
          }
        }
      }
      // The marker sits at the line's y; ask Ace which row that is.
      let row = null;
      try { row = editor?.renderer?.screenToTextCoordinates?.(400, r.y + r.height / 2)?.row ?? null; } catch { /* ignore */ }
      out.push({ line: row == null ? null : row + 1, time, y: Math.round(r.y), attrs });
    }
    return out;
  });

  const editorInfo = await page.evaluate(() => {
    const ed = document.querySelector('.ace_editor')?.env?.editor;
    return { hasApi: !!ed, lines: ed?.session?.getLength?.() ?? null };
  });
  log(`editor: ${JSON.stringify(editorInfo)}`);
  if (!editorInfo.hasApi) throw new Error('no Ace API handle; cannot walk the file deterministically');

  const byLine = new Map();
  const totalLines = editorInfo.lines;
  for (let line = 0; line < totalLines; line += 40) {
    await page.evaluate((n) => document.querySelector('.ace_editor').env.editor.scrollToLine(n, false, false), line);
    await page.waitForTimeout(80);
    for (const m of await readMarkers()) {
      if (m.line == null || byLine.has(m.line)) continue;
      byLine.set(m.line, m);
    }
    if (line % 2000 < 40) log(`  line ${line}/${totalLines}, markers found: ${byLine.size}`);
  }
  log(`markers harvested: ${byLine.size}`);

  const owner = await functionIndex(SOURCE);
  const rows = [...byLine.values()]
    .map((m) => ({ ...m, seconds: toSeconds(m.time), fn: owner?.[m.line] ?? null }))
    .sort((a, b) => (b.seconds ?? -1) - (a.seconds ?? -1));

  await writeFile(join(OUT_DIR, `${TAG}-markers.json`), JSON.stringify(rows, null, 2));

  const totalSeconds = toSeconds(total);
  console.log(`\n=== ${TAG}: profiler total ${total} ===`);
  console.log('   seconds   share  line     function');
  for (const r of rows.slice(0, 40)) {
    const share = r.seconds != null && totalSeconds ? `${(100 * r.seconds / totalSeconds).toFixed(1)}%` : '   -';
    console.log(`  ${String(r.time ?? '-').padStart(9)} ${share.padStart(6)}  ${String(r.line).padStart(6)}  ${r.fn ?? ''}`);
  }
  const attrKeys = new Set();
  for (const r of rows) for (const k of Object.keys(r.attrs ?? {})) attrKeys.add(k);
  console.log(`\ntooltip-bearing attributes seen on markers: ${[...attrKeys].join(', ') || '(none)'}`);
  const withText = rows.find((r) => Object.values(r.attrs ?? {}).some((v) => v && v.length > 3));
  if (withText) console.log(`example marker detail:\n  ${JSON.stringify(withText.attrs).slice(0, 900)}`);

  await writeFile(join(OUT_DIR, `${TAG}-summary.json`), JSON.stringify({
    at: new Date().toISOString(), featureStudio: EID, partStudio: PS_NAME,
    profilerTotal: total, profilerTotalSeconds: totalSeconds, markers: rows.length,
  }, null, 2));
  log(`  -> out/${TAG}-markers.json, out/${TAG}-summary.json, out/${TAG}-marker-sample.html`);
} finally {
  await browser.close();
}
