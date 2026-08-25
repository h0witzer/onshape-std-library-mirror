// eval-script.mjs — run a FeatureScript lambda from a file against a Part Studio, print everything.
//
// The unmetered replacement for the MCP's test_featurescript, and strictly better for debugging:
// the MCP returns notices INSTEAD of console, so a run that throws discards every println it
// produced. This prints status, console AND notices, always, console first.
//
// The /featurescript endpoint takes a BARE LAMBDA, not a module. The file must hold exactly:
//
//     function(context is Context, queries)
//     {
//         ...
//         return "whatever";
//     }
//
// with NO `FeatureScript 3044;` header, NO imports and NO `export const`. The standard library is
// already in scope. Helper `function` declarations and `defineFeature` do NOT parse here, so a
// module-level probe has to be restated as one self-contained lambda; to exercise a real feature,
// push it to a Feature Studio and insert it (push.mjs / insert-tests.mjs / notices.mjs) instead.
// `newId()` supplies operation ids — the lambda's second argument is queries, not an Id.
//
//   SCRIPT=probe.fs node eval-script.mjs
//   OS_DID=... OS_WID=... PS_EID=... SCRIPT=probe.fs node eval-script.mjs
import { readFileSync } from 'node:fs';
import { openSession, apiPost, DID, WID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID ?? 'f52300ae31a4fb83b720f044';
const SCRIPT_PATH = process.env.SCRIPT;

if (!SCRIPT_PATH) {
  console.error('SCRIPT=<path to a file holding one FeatureScript lambda> is required.');
  process.exit(2);
}

const script = readFileSync(SCRIPT_PATH, 'utf8').replace(/^﻿/, '');

// Catch the module form early: it parses as an expression here and the resulting ANTLR error
// ("extraneous input 'FeatureScript'") says nothing about the real cause.
if (/^\s*FeatureScript\s+\d+\s*;/.test(script)) {
  console.error('This endpoint evaluates a bare lambda, not a module.');
  console.error('Remove the "FeatureScript NNNN;" header, the imports and the "export const" —');
  console.error('leave only:  function(context is Context, queries) { ... }');
  process.exit(2);
}
if (!/function\s*\(/.test(script)) {
  console.error('No function literal found; the file must hold one lambda expression.');
  process.exit(2);
}

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(6000);
  const res = await apiPost(page, `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/featurescript`, {
    script,
    queries: {},
  });

  console.log(`status ${res.status}`);
  const body = res.body ?? {};

  // Console FIRST and unconditionally: a run that also produced notices is exactly the run whose
  // printlns matter most, and that is the case the MCP throws away.
  if (body.console) console.log(`\nconsole:\n${body.console}`);
  else console.log('\nconsole: (empty)');

  const notices = body.notices ?? [];
  console.log(`\n${notices.length} notice(s):`);
  for (const n of notices) {
    console.log(`  [${n.level ?? '?'}] ${n.message ?? JSON.stringify(n).slice(0, 400)}`);
  }

  if (body.result !== undefined) console.log(`\nresult: ${JSON.stringify(body.result).slice(0, 600)}`);
  if (typeof body === 'string') console.log(`\nbody:\n${body.slice(0, 1500)}`);
} finally {
  await browser.close();
}
