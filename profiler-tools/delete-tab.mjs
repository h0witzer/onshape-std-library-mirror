// delete-tab.mjs — remove Part Studio tabs from the sweep document, by eid.
//
// Destructive and deliberately explicit: it names every tab it is about to delete, refuses an eid
// that is not in the document, and refuses the two Feature Studios and Part Studio 1 outright —
// Part Studio 1 is the timed fixture and its build time is the project's only real measurement.
// Everything is recoverable by rolling the document back regardless.
//
//   node delete-tab.mjs <eid> [<eid> ...]
import { openSession, apiGet, DID, WID } from './lib.mjs';

const PROTECTED = new Set([
  '2af67ff9259d8b115654d5e6', // Part Studio 1 - the timed fixture
  'f799a445f4f6036909ad0955', // Part Studio 2 - the owner's face probing
  '5c27bfb0b1dfa896edb18563', // SolidSweepUtils
  'eb066ca2f397f85c926d9bf7', // SolidSweepTester
]);

const WANTED = process.argv.slice(2);
if (!WANTED.length) throw new Error('name at least one element id to delete');

const apiDelete = (page, path) => page.evaluate(async (p) => {
  const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
  const xsrf = raw.slice(raw.indexOf('=') + 1);
  const r = await fetch(p, {
    method: 'DELETE',
    credentials: 'same-origin',
    headers: { accept: 'application/json', 'x-xsrf-token': xsrf },
  });
  return { status: r.status, body: await r.text().catch(() => '') };
}, path);

const { browser, page } = await openSession();
try {
  await page.goto('https://cad.onshape.com/documents', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);

  const before = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/elements`);
  const byId = new Map((before.body ?? []).map((e) => [e.id, e]));

  for (const eid of WANTED) {
    const element = byId.get(eid);
    if (!element) {
      console.log(`SKIP    ${eid} — not in this document`);
      continue;
    }
    if (PROTECTED.has(eid)) {
      console.log(`REFUSED ${eid} ${JSON.stringify(element.name)} — protected`);
      continue;
    }
    const res = await apiDelete(page, `/api/v6/elements/d/${DID}/w/${WID}/e/${eid}`);
    console.log(`${res.status < 300 ? 'DELETED' : 'FAILED '} ${eid} ${JSON.stringify(element.name)} — status ${res.status}${res.status >= 300 ? ` ${res.body.slice(0, 200)}` : ''}`);
    await page.waitForTimeout(2000);
  }

  const after = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/elements`);
  console.log('\ntabs now:');
  for (const e of after.body ?? []) console.log(`  ${e.elementType.padEnd(14)} ${e.id}  ${JSON.stringify(e.name)}`);
} finally {
  await browser.close();
}
