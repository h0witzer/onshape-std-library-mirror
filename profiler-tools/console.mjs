// console.mjs — find where the FeatureScript console output lands, so a cycle can capture the
// test VERDICT alongside the timings. Levers B and C change sampling, so they change answers,
// and a stopwatch alone cannot tell a speedup from a regression.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, startProfiling, OUT_DIR, UTILS_EID, docUrl } from './lib.mjs';

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(process.env.FS_EID ?? UTILS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(22_000);

  const total = await startProfiling(page, 'Part Studio 1', {
    waitMinutes: 12, onTick: (l) => console.log(`  profiling... (${l} min left)`),
  });
  console.log(`total: ${total}`);

  const hits = await page.evaluate(() => {
    const out = { lines: [], containers: [] };
    for (const el of document.querySelectorAll('*')) {
      const t = (el.textContent ?? '').trim();
      if (!t) continue;
      if (el.childElementCount === 0 && /ASSEMBLY LIVE TEST|VERDICT|PASS:|FAIL/i.test(t) && t.length < 500) {
        out.lines.push(t);
      }
      const cls = String(el.className?.baseVal ?? el.className ?? '');
      if (/console|output|notice|log|message-panel/i.test(cls) && el.childElementCount > 0) {
        const r = el.getBoundingClientRect();
        out.containers.push({ cls: cls.slice(0, 110), kids: el.childElementCount,
          visible: r.width > 0 && r.height > 0, sample: t.slice(0, 160) });
      }
    }
    out.lines = [...new Set(out.lines)];
    return out;
  });
  console.log(`\nprintln-looking lines found: ${hits.lines.length}`);
  for (const l of hits.lines.slice(0, 25)) console.log(`   ${l.slice(0, 190)}`);
  console.log(`\nconsole-ish containers: ${hits.containers.length}`);
  for (const c of hits.containers.slice(0, 20)) console.log(`   .${c.cls} kids=${c.kids} visible=${c.visible} ${JSON.stringify(c.sample)}`);
  await writeFile(join(OUT_DIR, 'console-probe.json'), JSON.stringify(hits, null, 2));
  await page.screenshot({ path: join(OUT_DIR, 'console-probe.png') });
} finally {
  await browser.close();
}
