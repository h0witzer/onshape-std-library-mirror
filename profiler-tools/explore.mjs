// explore.mjs — find out how Onshape's FeatureScript profiler is driven and where its results
// come from. Read-only: it opens the document, watches what the real client asks for, and
// probes candidate endpoints. Nothing is created, modified or deleted.
//
// This is deliberately a DISCOVERY script rather than a scraper. The endpoint is not documented
// and guessing it from the outside is how the drawings work lost its first afternoon; watching
// the client do it is faster and cannot be wrong.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, apiGet, OUT_DIR, DID, WID, docUrl } from './lib.mjs';

const WATCH_SECONDS = Number(process.env.WATCH_SECONDS ?? 45);
const save = async (name, data) => {
  await writeFile(join(OUT_DIR, name), typeof data === 'string' ? data : JSON.stringify(data, null, 2));
  console.log(`  -> out/${name}`);
};

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });

  // ---------- 1. What tabs does this document have, and which one is Part Studio 1? ----------
  console.log('\n[1] listing document elements...');
  const elements = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/elements?withThumbnails=false`);
  console.log(`  status ${elements.status}`);
  if (elements.status !== 200) {
    console.log('  cannot list elements - the session is probably not valid for this document.');
    await save('elements-error.json', elements);
  } else {
    await save('elements.json', elements.body);
    for (const e of elements.body ?? []) {
      console.log(`  ${e.elementType ?? e.dataType}  ${e.id}  ${JSON.stringify(e.name)}`);
    }
  }
  const partStudios = (elements.body ?? []).filter((e) => (e.elementType ?? '').toUpperCase() === 'PARTSTUDIO');
  const target = partStudios.find((e) => /part studio 1/i.test(e.name ?? '')) ?? partStudios[0];
  if (!target) {
    console.log('  no Part Studio found; stopping before the watch phase.');
  } else {
    console.log(`\n  profiling target: ${JSON.stringify(target.name)} (${target.id})`);

    // ---------- 2. Watch what the client asks for while the Part Studio loads ----------
    console.log(`\n[2] opening it and watching /api/ traffic for ${WATCH_SECONDS}s...`);
    const requests = [];
    const responses = [];
    page.on('request', (req) => {
      const u = req.url();
      if (!/\/api\//.test(u)) return;
      requests.push({ method: req.method(), path: u.replace('https://cad.onshape.com', '') });
    });
    page.on('response', async (res) => {
      const u = res.url();
      if (!/\/api\//.test(u)) return;
      if (!/profil|perf|timing|telemetr|stat/i.test(u)) return;
      responses.push({ status: res.status(), path: u.replace('https://cad.onshape.com', '') });
    });
    // Websocket frames are where a regen actually reports, so they are worth seeing too.
    const frames = [];
    page.on('websocket', (ws) => {
      console.log(`  websocket: ${ws.url().slice(0, 100)}`);
      ws.on('framereceived', (f) => {
        const d = typeof f.payload === 'string' ? f.payload : f.payload?.toString('utf8') ?? '';
        if (/profil|microversion|regen|featureScript/i.test(d)) frames.push(d.slice(0, 400));
      });
    });

    await page.goto(docUrl(target.id), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(WATCH_SECONDS * 1000);

    const paths = [...new Set(requests.map((r) => `${r.method} ${r.path.split('?')[0]}`))].sort();
    await save('api-paths.txt', paths.join('\n'));
    console.log(`  ${requests.length} /api/ requests, ${paths.length} distinct paths -> out/api-paths.txt`);
    const interesting = paths.filter((p) => /profil|perf|timing|telemetr|stat|regen/i.test(p));
    console.log(`  paths that look profiling-related: ${interesting.length ? '\n    ' + interesting.join('\n    ') : '(none)'}`);
    if (responses.length) await save('profiling-responses.json', responses);
    if (frames.length) await save('websocket-frames.txt', frames.join('\n---\n'));
    console.log(`  websocket frames mentioning regen/profiling: ${frames.length}`);

    // ---------- 3. Is there a profiler affordance in the UI at all? ----------
    console.log('\n[3] looking for profiler affordances in the DOM...');
    const ui = await page.evaluate(() => {
      const hits = [];
      const seen = new Set();
      for (const el of document.querySelectorAll('*')) {
        const label = [el.getAttribute?.('title'), el.getAttribute?.('aria-label'), el.id,
          el.className && typeof el.className === 'string' ? el.className : '']
          .filter(Boolean).join(' ');
        const text = (el.childElementCount === 0 ? el.textContent ?? '' : '').trim().slice(0, 80);
        const blob = `${label} ${text}`;
        if (!/profil/i.test(blob)) continue;
        const key = `${el.tagName}|${blob}`;
        if (seen.has(key)) continue;
        seen.add(key);
        hits.push({ tag: el.tagName, label: label.slice(0, 120), text });
      }
      return hits;
    });
    console.log(`  DOM nodes mentioning "profil": ${ui.length}`);
    for (const h of ui.slice(0, 25)) console.log(`    <${h.tag}> ${h.label} ${h.text}`);
    if (ui.length) await save('ui-profiler-nodes.json', ui);

    await page.setViewportSize({ width: 1920, height: 1200 });
    await page.screenshot({ path: join(OUT_DIR, 'partstudio.png') });
    console.log('  -> out/partstudio.png');
  }

  // ---------- 4. Probe endpoints that a profiler plausibly lives behind ----------
  console.log('\n[4] probing candidate endpoints (GET only, read-only)...');
  const eid = target?.id;
  const candidates = [
    '/api/v14/users/session',
    '/api/v14/users/sessioninfo',
    `/api/v14/documents/d/${DID}/w/${WID}/elements?withThumbnails=false`,
  ];
  if (eid) candidates.push(
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${eid}/features`,
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${eid}/featurespecs`,
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${eid}/profile`,
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${eid}/featurescriptprofile`,
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${eid}/performance`,
  );
  const probes = [];
  for (const p of candidates) {
    const r = await apiGet(page, p);
    const size = r.body == null ? 0 : JSON.stringify(r.body).length;
    console.log(`  ${String(r.status).padEnd(4)} ${size.toString().padStart(8)}B  ${p.split('?')[0]}`);
    probes.push({ path: p, status: r.status, bytes: size });
  }
  await save('endpoint-probes.json', probes);

  console.log('\nexploration done. Leaving the browser open for 20s in case you want to look.');
  await page.waitForTimeout(20000);
} finally {
  await browser.close();
}
