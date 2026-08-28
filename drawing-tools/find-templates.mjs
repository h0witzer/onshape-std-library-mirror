// find-templates.mjs — locate a stock ISO / millimetre drawing template. Creating from a
// template whose style is metric is the clean fix for style-driven text rendering 25.4x too
// small on a millimetre sheet.
import { writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, apiGet, OUT_DIR } from './lib.mjs';

const queries = [
  '/api/v6/documents?q=drawing%20template&limit=20',
  '/api/v6/documents?q=Onshape%20Drawing%20Templates&limit=20',
  '/api/v6/documents?q=template&filter=4&limit=20',
  '/api/v6/documents?q=template&owner=onshape&limit=20',
];

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  const found = [];
  for (const q of queries) {
    const r = await apiGet(page, q);
    const items = r.body?.items ?? [];
    console.log(`${r.status}  ${items.length} results  ${decodeURIComponent(q)}`);
    for (const d of items) {
      console.log(`   ${d.name}  did=${d.id}  owner=${d.owner?.name ?? '?'}  public=${d.public}`);
      found.push(d);
    }
  }

  // For anything that looks like a template document, list its drawing elements.
  const seen = new Set();
  for (const d of found) {
    if (seen.has(d.id) || !/template/i.test(d.name ?? '')) continue;
    seen.add(d.id);
    const wid = d.defaultWorkspace?.id;
    if (!wid) continue;
    const el = await apiGet(page, `/api/v6/documents/d/${d.id}/w/${wid}/elements`);
    if (el.status !== 200) { console.log(`\n${d.name}: elements ${el.status}`); continue; }
    const draws = (el.body ?? []).filter((e) => e.dataType === 'onshape-app/drawing' || e.elementType === 'APPLICATION');
    console.log(`\n${d.name} (did=${d.id} wid=${wid}) — ${draws.length} drawing elements:`);
    for (const e of draws) console.log(`   ${e.name}  eid=${e.id}`);
    await writeFile(join(OUT_DIR, `template-${d.id}.json`), JSON.stringify(el.body, null, 2));
  }
} finally {
  await browser.close();
}
