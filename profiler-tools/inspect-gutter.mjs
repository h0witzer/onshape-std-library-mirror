// inspect-gutter.mjs — the per-function numbers are NOT on Ace's `.ace_gutter-cell` (those only
// carry fold widgets). Find the layer Onshape actually paints them into, by starting from the
// one element whose text is a duration and describing everything around it.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, startProfiling, OUT_DIR, UTILS_EID, docUrl } from './lib.mjs';

const EID = process.env.FS_EID ?? UTILS_EID;
const WAIT_MINUTES = Number(process.env.WAIT_MINUTES ?? 10);
const stamp = () => new Date().toISOString().slice(11, 23);
const log = (l) => console.log(`${stamp()} ${l}`);

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(22_000);

  // Profiling does not survive a reload - it has to be armed every run.
  const badge = await startProfiling(page, process.env.PS_NAME ?? 'Part Studio 1', {
    waitMinutes: WAIT_MINUTES,
    onTick: (left) => log(`  waiting for the profile... (${left} min left)`),
  });
  log(`profiler total: ${JSON.stringify(badge)}`);

  // ---------- describe the badge's neighbourhood ----------
  const report = await page.evaluate(() => {
    const describe = (el) => ({
      tag: el.tagName,
      cls: String(el.className?.baseVal ?? el.className ?? '').slice(0, 140),
      id: el.id || undefined,
      title: (el.getAttribute?.('title') ?? '').slice(0, 200) || undefined,
      text: (el.childElementCount === 0 ? (el.textContent ?? '').trim() : '').slice(0, 120) || undefined,
      rect: (() => { const r = el.getBoundingClientRect(); return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) }; })(),
      children: el.childElementCount,
    });

    let badge = null;
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      if (/^\d+(\.\d+)?\s*(ms|s)$/i.test((el.textContent ?? '').trim())) { badge = el; break; }
    }
    const out = { ancestors: [], siblings: [], leftStrip: [], durations: [] };
    if (badge) {
      let a = badge;
      while (a && a !== document.body) { out.ancestors.push(describe(a)); a = a.parentElement; }
      const holder = badge.parentElement?.parentElement;
      if (holder) for (const s of holder.children) out.siblings.push(describe(s));
    }

    // Everything sitting in the left strip of the editor, which is where the flags render.
    for (const el of document.querySelectorAll('*')) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      if (r.x > 130) continue;
      const t = (el.childElementCount === 0 ? (el.textContent ?? '').trim() : '');
      const cls = String(el.className?.baseVal ?? el.className ?? '');
      if (!t && !/profil|perf|timing|notice|annot|flag|marker/i.test(cls)) continue;
      out.leftStrip.push(describe(el));
    }

    // And every duration-looking string anywhere on the page, with its class.
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      const t = (el.textContent ?? '').trim();
      if (!/^\d+(\.\d+)?\s*(ms|s|%)$/i.test(t)) continue;
      out.durations.push(describe(el));
    }
    return out;
  });

  console.log('\n--- ancestor chain of the badge ---');
  for (const a of report.ancestors) console.log(`  <${a.tag}> .${a.cls}  ${JSON.stringify(a.rect)} kids=${a.children} ${a.text ? 'text=' + JSON.stringify(a.text) : ''}`);
  console.log('\n--- siblings of the badge holder ---');
  for (const s of report.siblings.slice(0, 20)) console.log(`  <${s.tag}> .${s.cls} ${JSON.stringify(s.rect)} ${s.text ? 'text=' + JSON.stringify(s.text) : ''} ${s.title ? 'title=' + JSON.stringify(s.title) : ''}`);
  console.log(`\n--- left strip (x < 130): ${report.leftStrip.length} elements ---`);
  for (const s of report.leftStrip.slice(0, 40)) console.log(`  <${s.tag}> .${s.cls} ${JSON.stringify(s.rect)} ${s.text ? 'text=' + JSON.stringify(s.text) : ''} ${s.title ? 'title=' + JSON.stringify(s.title) : ''}`);
  console.log(`\n--- duration-looking texts: ${report.durations.length} ---`);
  for (const s of report.durations.slice(0, 30)) console.log(`  <${s.tag}> .${s.cls} ${JSON.stringify(s.rect)} text=${JSON.stringify(s.text)}`);

  await writeFile(join(OUT_DIR, 'gutter-inspect.json'), JSON.stringify(report, null, 2));
  await page.screenshot({ path: join(OUT_DIR, 'gutter-inspect.png') });
  console.log('\n  -> out/gutter-inspect.json, out/gutter-inspect.png');
} finally {
  await browser.close();
}
