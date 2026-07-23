// std-sync/sync.mjs — update THIS custom repo's Standard Library reference files from the
// public upstream mirror. Pure git: no Onshape login, no API, no credentials.
//
// It overwrites the root-level std *.fs with the upstream mirror's, and removes any module
// upstream dropped. It NEVER touches custom-features/ or anything else in this repo.
//
// This is deliberately NOT the Onshape fetcher — that lives in the public mirror repo and
// maintains the mirror. This tool only consumes what the mirror already published.
//
// Usage:
//   node std-sync/sync.mjs            # sync to the newest upstream version, stage, review
//   node std-sync/sync.mjs --commit   # ...and commit automatically
//   node std-sync/sync.mjs <ref>      # sync to a specific upstream ref/commit instead of latest
import { execSync } from 'node:child_process';
import { writeFileSync, rmSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const UPSTREAM_URL = 'https://github.com/h0witzer/Onshape-Standard-Library-Mirror-2-Electric-Boogaloo.git';

const args = process.argv.slice(2);
const doCommit = args.includes('--commit');
const REF = args.find((a) => !a.startsWith('--')) || 'upstream/main';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..');
const git = (a) => execSync(`git ${a}`, { cwd: REPO, encoding: 'utf8', maxBuffer: 128 * 1024 * 1024 });

// 1. ensure the upstream remote points at the public mirror
if (!git('remote').split(/\r?\n/).includes('upstream')) { git(`remote add upstream ${UPSTREAM_URL}`); console.log('Added "upstream" remote.'); }
else git(`remote set-url upstream ${UPSTREAM_URL}`);

// 2. fetch it
console.log('Fetching upstream mirror...');
git('fetch upstream --quiet');

// 3. derive a version label from the most recent "Standard library N" commit reachable from REF
//    (the tip may be a tooling commit with no version, so scan the log rather than just HEAD)
let label = REF;
try {
  for (const s of git(`log ${REF} --format=%s -100`).split(/\r?\n/)) {
    const m = s.match(/Standard library (\d{3,6})/i);
    if (m) { label = m[1]; break; }
  }
} catch { /* keep REF */ }

// 4. root-level .fs on both sides
const upstreamFs = git(`ls-tree --name-only ${REF}`).split(/\r?\n/).filter((f) => f.endsWith('.fs'));
if (!upstreamFs.length) { console.error(`No root .fs found at ${REF} — aborting.`); process.exit(1); }
const currentFs = git('ls-files -- "*.fs"').split(/\r?\n/).filter((f) => f && !f.includes('/'));

// 5. write upstream's modules into the repo root
for (const f of upstreamFs) writeFileSync(join(REPO, f), git(`show ${REF}:${f}`));

// 6. drop modules upstream removed
const upstreamSet = new Set(upstreamFs);
const removed = currentFs.filter((f) => !upstreamSet.has(f));
for (const f of removed) { const p = join(REPO, f); if (existsSync(p)) rmSync(p); }

// 7. stage only the std files we touched (never custom-features/)
const touched = [...new Set([...upstreamFs, ...removed])];
for (let i = 0; i < touched.length; i += 100) git(`add -A -- ${touched.slice(i, i + 100).map((f) => `"${f}"`).join(' ')}`);

const stat = git('diff --cached --shortstat').trim();
console.log(`\nSynced ${upstreamFs.length} std modules from ${REF} (version ${label}).`);
if (removed.length) console.log(`Removed ${removed.length} module(s) no longer upstream: ${removed.join(', ')}`);
if (!stat) { console.log('Already up to date — no changes.'); process.exit(0); }
console.log(stat);

if (doCommit) { git(`commit -m "Sync std library to ${label} from upstream mirror"`); console.log(`\nCommitted: Sync std library to ${label} from upstream mirror`); }
else console.log(`\nReview the staged changes, then:\n  git commit -m "Sync std library to ${label} from upstream mirror"`);
