// drawing.mjs — Onshape Drawings API operations layered over the session-cookie transport.
import { writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { apiGet, apiPost, waitForModify, OUT_DIR } from './lib.mjs';

const V = '/api/v6';

export async function createDrawing(page, { did, wid, name, sourceElementId, partId, template }) {
  const payload = { drawingName: name };
  if (template) Object.assign(payload, template);
  if (sourceElementId) payload.elementId = sourceElementId;
  if (partId) payload.partId = partId;
  const r = await apiPost(page, `${V}/drawings/d/${did}/w/${wid}/create`, payload);
  if (r.status !== 200) throw new Error(`create failed ${r.status}: ${JSON.stringify(r.body).slice(0, 400)}`);
  return r.body.id;
}

// Submit one or more jsonRequests to /modify and wait for the async job to settle.
export async function modify(page, { did, wid, eid, description, jsonRequests }) {
  const r = await apiPost(page, `${V}/drawings/d/${did}/w/${wid}/e/${eid}/modify`, {
    description: description ?? 'api modification',
    jsonRequests,
  });
  if (r.status !== 200) throw new Error(`modify failed ${r.status}: ${JSON.stringify(r.body).slice(0, 600)}`);
  const mrid = r.body?.id ?? r.body?.requestId ?? r.body;
  const done = await waitForModify(page, mrid);
  if (done.state !== 'DONE') {
    throw new Error(`modify ${done.state}: ${JSON.stringify(done.body).slice(0, 600)}`);
  }
  return done.body;
}

// Export DRAWING_JSON, poll the translation, download the result. This is how view ids and
// geometry uniqueIds are recovered for attaching dimensions and balloon leaders.
export async function exportDrawingJson(page, { did, wid, eid, saveAs }) {
  const tr = await apiPost(page, `${V}/drawings/d/${did}/w/${wid}/e/${eid}/translations`, {
    formatName: 'DRAWING_JSON',
    storeInDocument: false,
  });
  if (tr.status !== 200) throw new Error(`translation request failed ${tr.status}: ${JSON.stringify(tr.body).slice(0, 300)}`);
  const tid = tr.body.id;

  let state = tr.body.requestState, info = tr.body;
  for (let i = 0; i < 80 && state === 'ACTIVE'; i++) {
    await page.waitForTimeout(1500);
    const s = await apiGet(page, `${V}/translations/${tid}`);
    info = s.body; state = info?.requestState;
  }
  if (state !== 'DONE') throw new Error(`translation ${state}: ${info?.failureReason ?? '?'}`);

  const fdid = info.resultExternalDataIds?.[0];
  if (!fdid) throw new Error('translation produced no external data id');

  const raw = await page.evaluate(async (p) => {
    const r = await fetch(p, { credentials: 'same-origin' });
    return { status: r.status, text: await r.text() };
  }, `${V}/documents/d/${did}/externaldata/${fdid}`);
  if (raw.status !== 200) throw new Error(`download failed ${raw.status}`);

  let parsed = null;
  try { parsed = JSON.parse(raw.text); } catch { /* keep raw */ }
  if (saveAs) {
    await mkdir(OUT_DIR, { recursive: true });
    await writeFile(join(OUT_DIR, saveAs), parsed ? JSON.stringify(parsed, null, 2) : raw.text);
    console.log(`  -> out/${saveAs}`);
  }
  return parsed ?? raw.text;
}

export async function deleteElement(page, { did, wid, eid }) {
  return page.evaluate(async ({ p, x }) => {
    const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
    const r = await fetch(p, { method: 'DELETE', credentials: 'same-origin', headers: { 'x-xsrf-token': raw.slice(raw.indexOf('=') + 1) } });
    return r.status;
  }, { p: `${V}/elements/d/${did}/w/${wid}/e/${eid}`, x: 1 });
}
