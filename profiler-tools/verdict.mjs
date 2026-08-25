// verdict.mjs — read what Part Studio 1 actually builds and what its test reports.
// Levers that change sampling change ANSWERS, so a stopwatch is not enough to iterate on them
// autonomously; this is the correctness half of the loop.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, apiGet, OUT_DIR, DID, WID, docUrl } from './lib.mjs';

const PS = process.env.PS_EID ?? '2af67ff9259d8b115654d5e6';

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  const f = await apiGet(page, `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS}/features`);
  console.log(`features endpoint: ${f.status}`);
  const features = f.body?.features ?? [];
  console.log(`\n${features.length} feature(s) in Part Studio 1:`);
  for (const feat of features) {
    console.log(`  ${feat.message?.featureType ?? feat.typeName}  name=${JSON.stringify(feat.message?.name)}  id=${feat.message?.featureId}`);
  }
  await writeFile(join(OUT_DIR, 'partstudio-features.json'), JSON.stringify(f.body, null, 2));

  // Feature status carries the notices a feature reported - which is where reportFeatureInfo lands.
  const st = await apiGet(page, `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS}/featurescriptstate`);
  console.log(`\nfeaturescriptstate: ${st.status}`);
  if (st.status === 200) await writeFile(join(OUT_DIR, 'fs-state.json'), JSON.stringify(st.body, null, 2));

  for (const path of [
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS}/features/featurespecs`,
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS}/featurescriptstate`,
  ]) {
    const r = await apiGet(page, path);
    console.log(`  ${r.status}  ${JSON.stringify(r.body).length}B  ${path.split('/').slice(-1)[0]}`);
  }

  // And the rendered notices, which is what a human reads.
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(PS), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(35_000);
  const notices = await page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      const t = (el.textContent ?? '').trim();
      if (!t || t.length < 8 || t.length > 400) continue;
      if (!/PASS|FAIL|VERDICT|checks|deviation|volume|seam/i.test(t)) continue;
      out.push(t);
    }
    return [...new Set(out)];
  });
  console.log(`\nverdict-ish text on the Part Studio page (${notices.length}):`);
  for (const n of notices.slice(0, 30)) console.log(`   ${n.slice(0, 200)}`);
  await writeFile(join(OUT_DIR, 'verdict-text.json'), JSON.stringify(notices, null, 2));
  await page.screenshot({ path: join(OUT_DIR, 'partstudio-verdict.png') });
} finally {
  await browser.close();
}
