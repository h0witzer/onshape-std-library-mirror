// probe-post.mjs — isolate whether session-cookie POSTs work at all, and on which paths.
import { openSession } from './lib.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';
const ASM = '196d1dcc6c8f1cc346a97033';
const PS  = 'd02af6edc49b9c78d7d55b65';

const { browser, page } = await openSession();
try {
  const results = await page.evaluate(async ({ DID, WID, ASM, PS }) => {
    const xsrf = decodeURIComponent(document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN='))?.split('=')[1] ?? '');
    const attempts = [
      // Read-only POST: does any cookie-auth POST succeed?
      ['FS eval (read-only POST)', `/api/v6/partstudios/d/${DID}/w/${WID}/e/${PS}/featurescript`,
        { script: 'function(context is Context, queries) { return 1; }' }],
      // Drawing create, several path spellings.
      ['drawings create v6', `/api/v6/drawings/d/${DID}/w/${WID}/create`, { drawingName: 'probe', elementId: ASM }],
      ['drawings create v10', `/api/v10/drawings/d/${DID}/w/${WID}/create`, { drawingName: 'probe', elementId: ASM }],
      ['drawings create unversioned', `/api/drawings/d/${DID}/w/${WID}/create`, { drawingName: 'probe', elementId: ASM }],
      ['drawings create v14', `/api/v14/drawings/d/${DID}/w/${WID}/create`, { drawingName: 'probe', elementId: ASM }],
      // Generic element create (blob/tab) to test write permission separately.
      ['blobelements (write test)', `/api/v6/blobelements/d/${DID}/w/${WID}`, {}],
    ];
    const out = [];
    for (const [label, path, body] of attempts) {
      for (const useXsrf of [true, false]) {
        const headers = { accept: 'application/json', 'content-type': 'application/json' };
        if (useXsrf) { headers['X-XSRF-TOKEN'] = xsrf; headers['X-Requested-With'] = 'XMLHttpRequest'; }
        let status = 0, text = '', hdrs = {};
        try {
          const r = await fetch(path, { method: 'POST', credentials: 'same-origin', headers, body: JSON.stringify(body) });
          status = r.status; text = (await r.text()).slice(0, 300);
          r.headers.forEach((v, k) => { if (/auth|xsrf|csrf|www|error/i.test(k)) hdrs[k] = v; });
        } catch (e) { text = 'THREW ' + e.message; }
        out.push({ label, path, xsrf: useXsrf, status, hdrs, text });
      }
    }
    return { xsrfPresent: !!xsrf, xsrfLen: xsrf.length, out };
  }, { DID, WID, ASM, PS });

  console.log(`XSRF cookie present: ${results.xsrfPresent} (len ${results.xsrfLen})\n`);
  for (const r of results.out) {
    console.log(`${String(r.status).padEnd(4)} xsrf=${r.xsrf ? 'Y' : 'n'}  ${r.label}`);
    if (Object.keys(r.hdrs).length) console.log(`      hdrs: ${JSON.stringify(r.hdrs)}`);
    if (r.text) console.log(`      body: ${r.text.replace(/\s+/g, ' ')}`);
  }
} finally {
  await browser.close();
}
