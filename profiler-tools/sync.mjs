// sync.mjs — compare (and optionally update) a Feature Studio tab against the local file.
//
// The recorded constraint is that these two files are pasted by hand, because `put_featurescript`
// through the MCP costs its own size in output tokens and 512 KB does not fit in a message. That
// constraint does not apply to the browser-session route: the POST body never passes through a
// model, so the same unmetered path that reads the profile can carry the whole file.
//
// Default mode is READ ONLY and prints a diff summary. Writing needs --push, explicitly.
//
// Usage:  node sync.mjs                    # compare the tab against custom-features/solidSweepUtils.fs
//         node sync.mjs --push             # ...and overwrite the tab with the local file
//         FS_EID=<eid> SOURCE=<path> node sync.mjs
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { openSession, apiGet, apiPost, OUT_DIR, DID, WID, UTILS_EID } from './lib.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const EID = process.env.FS_EID ?? UTILS_EID;
const SOURCE = process.env.SOURCE ?? join(HERE, '..', 'custom-features', 'solidSweepUtils.fs');
const PUSH = process.argv.includes('--push');

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  const local = await readFile(SOURCE, 'utf8');
  console.log(`local  ${basename(SOURCE)}: ${local.length} bytes, ${local.split('\n').length} lines`);

  const got = await apiGet(page, `/api/v14/featurestudios/d/${DID}/w/${WID}/e/${EID}`);
  if (got.status !== 200) { console.log(`  GET failed: ${got.status}`); process.exit(1); }
  const remote = got.body?.contents ?? '';
  console.log(`remote tab ${EID}: ${remote.length} bytes, ${remote.split('\n').length} lines`);
  await writeFile(join(OUT_DIR, 'remote-tab.fs'), remote);

  const norm = (t) => t.replace(/\r\n/g, '\n');
  if (norm(local) === norm(remote)) {
    console.log('\nIDENTICAL - the tab already carries the local file.');
  } else {
    const a = norm(local).split('\n');
    const b = norm(remote).split('\n');
    let first = 0;
    while (first < a.length && first < b.length && a[first] === b[first]) first += 1;
    let tailA = a.length - 1;
    let tailB = b.length - 1;
    while (tailA > first && tailB > first && a[tailA] === b[tailB]) { tailA -= 1; tailB -= 1; }
    console.log(`\nDIFFERENT - first divergence at line ${first + 1}`);
    console.log(`  local : ${JSON.stringify(a[first]?.slice(0, 110))}`);
    console.log(`  remote: ${JSON.stringify(b[first]?.slice(0, 110))}`);
    console.log(`  local has ${a.length} lines, remote ${b.length}; differing region ends at local ${tailA + 1} / remote ${tailB + 1}`);
    console.log('  remote copy saved to out/remote-tab.fs');
  }

  if (!PUSH) {
    console.log('\nread-only (pass --push to overwrite the tab with the local file).');
  } else {
    console.log('\npushing the local file into the tab...');
    const res = await apiPost(page, `/api/v14/featurestudios/d/${DID}/w/${WID}/e/${EID}`, { contents: local });
    console.log(`  status ${res.status}`);
    // Never trust the status: read it back and compare, the same discipline the MCP route uses.
    await page.waitForTimeout(3000);
    const back = await apiGet(page, `/api/v14/featurestudios/d/${DID}/w/${WID}/e/${EID}`);
    const after = back.body?.contents ?? '';
    console.log(`  read back: ${after.length} bytes`);
    console.log(norm(after) === norm(local) ? '  VERIFIED - the tab now matches the local file.' : '  MISMATCH - the tab does NOT match; inspect before profiling.');
  }
} finally {
  await browser.close();
}
