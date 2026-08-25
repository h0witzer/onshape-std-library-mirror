// eval-fs.mjs — run a FeatureScript snippet against a Part Studio and print what it says.
//
// This is the diagnostic that returns an actual ERROR MESSAGE. A feature that fails at regeneration
// reports only `ERROR` through the feature-list API, and a compile-clean element with erroring
// features gives you nothing to read; evaluating the call directly hands back the exception text.
// Unmetered, same browser session as everything else here.
//
//   SNIPPET='stripAnalyticSurface(plane(vector(0,0,0)*meter, vector(0,0,1)))' node eval-fs.mjs
import { openSession, apiPost, DID, WID, UTILS_EID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID ?? 'f52300ae31a4fb83b720f044';
const SNIPPET = process.env.SNIPPET ?? 'true';

// The endpoint evaluates a BARE LAMBDA with the standard library already in scope. A module —
// version header, imports, `export const` — is parsed as an expression and fails on its first
// token. That also means an expression here cannot reach a Feature Studio's own functions; for
// those, push the studio and insert the feature (push.mjs / insert-tests.mjs / notices.mjs).
const script = `
function(context is Context, queries)
{
    return toString(${SNIPPET});
}
`;

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
  if (body.notices) {
    console.log(`\n${body.notices.length} notice(s):`);
    for (const n of body.notices) {
      console.log(`  [${n.level ?? '?'}] ${n.message ?? JSON.stringify(n).slice(0, 400)}`);
    }
  }
  if (body.console) console.log(`\nconsole:\n${body.console}`);
  if (body.result !== undefined) console.log(`\nresult: ${JSON.stringify(body.result).slice(0, 600)}`);
  if (!body.notices && !body.console && body.result === undefined) {
    console.log(JSON.stringify(body).slice(0, 1500));
  }
} finally {
  await browser.close();
}
