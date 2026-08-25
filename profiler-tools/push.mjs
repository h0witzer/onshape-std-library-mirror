// push.mjs — put the local sources into their Onshape tabs and verify, without profiling.
// For the times you want the document current but do not want to wait three minutes for a run.
//
//   node push.mjs            both tabs
//   node push.mjs utils      just solidSweepUtils
//   node push.mjs tester     just solidSweepTester
//
// Any Feature Studio in any document, via the environment — this is the whole round trip for a
// custom feature, and the reason there is never a need to paste source into Onshape by hand:
//   OS_DID=.. OS_WID=.. FS_EID=<studio> SOURCE=<path to .fs> node push.mjs
import { readFile } from 'node:fs/promises';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { openSession, pushFeatureStudio, UTILS_EID, OTHER_EID, docUrl } from './lib.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const TARGETS = {
  utils: { eid: UTILS_EID, file: join(HERE, '..', 'custom-features', 'solidSweepUtils.fs') },
  tester: { eid: OTHER_EID, file: join(HERE, '..', 'custom-features', 'solidSweepTester.fs') },
};
if (process.env.FS_EID && process.env.SOURCE) {
  TARGETS.custom = { eid: process.env.FS_EID, file: process.env.SOURCE };
}
const which = process.argv[2] ?? (TARGETS.custom ? 'custom' : undefined);
const chosen = which ? [which] : Object.keys(TARGETS);

const { browser, page } = await openSession();
try {
  // Any document page will do; the API calls ride the session, not the view.
  await page.goto(docUrl(UTILS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(8000);
  let bad = 0;
  for (const key of chosen) {
    const t = TARGETS[key];
    if (!t) { console.log(`unknown target ${key}`); bad += 1; continue; }
    const local = await readFile(t.file, 'utf8');
    const res = await pushFeatureStudio(page, t.eid, local);
    console.log(`${key.padEnd(7)} ${basename(t.file).padEnd(22)} ${String(local.length).padStart(7)}B  status ${res.status}  ${res.verified ? 'VERIFIED' : 'MISMATCH'}`);
    if (!res.verified) bad += 1;
  }
  process.exitCode = bad ? 1 : 0;
} finally {
  await browser.close();
}
