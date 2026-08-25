// probe-debug.mjs — the Part Studio page asks for `/api/debug/d/{did}/timerflag` on load, which
// is the only profiling-shaped thing in 56 distinct paths. Read-only probe of that namespace.
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, apiGet, OUT_DIR, DID, WID } from './lib.mjs';

const PS = process.env.PS_EID ?? '2af67ff9259d8b115654d5e6'; // Part Studio 1

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  const results = [];
  const probe = async (p) => {
    const r = await apiGet(page, p);
    const text = r.body == null ? '' : JSON.stringify(r.body);
    console.log(`  ${String(r.status).padEnd(4)} ${String(text.length).padStart(9)}B  ${p}`);
    if (r.status === 200 && text.length < 600) console.log(`         ${text}`);
    results.push({ path: p, status: r.status, bytes: text.length, body: r.status === 200 ? r.body : undefined });
  };

  console.log('the flag the client itself asks for:');
  await probe(`/api/debug/d/${DID}/timerflag`);

  console.log('\nneighbours in the /api/debug namespace:');
  for (const p of [
    '/api/debug',
    '/api/debug/timerflag',
    `/api/debug/d/${DID}/w/${WID}/timerflag`,
    `/api/debug/d/${DID}/timers`,
    `/api/debug/d/${DID}/w/${WID}/e/${PS}/timers`,
    `/api/debug/d/${DID}/w/${WID}/e/${PS}/timerflag`,
    `/api/debug/d/${DID}/w/${WID}/e/${PS}/profile`,
    `/api/debug/d/${DID}/w/${WID}/e/${PS}/featurescriptprofile`,
    `/api/debug/d/${DID}/w/${WID}/e/${PS}/regenprofile`,
    `/api/debug/d/${DID}/w/${WID}/e/${PS}`,
  ]) await probe(p);

  console.log('\nprofiling-shaped names under the modelling namespaces:');
  for (const p of [
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS}/timers`,
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS}/featurescriptprofiling`,
    `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS}/regenerationprofile`,
    `/api/v14/documents/${DID}:${WID}/modelingServiceRequest`,
    '/api/v14/users/settings',
  ]) await probe(p);

  await writeFile(join(OUT_DIR, 'debug-probes.json'), JSON.stringify(results, null, 2));
  console.log('\n  -> out/debug-probes.json');
} finally {
  await browser.close();
}
