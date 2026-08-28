// sniff.mjs — observe how the real Onshape web client authenticates its POSTs.
// If it sends a header we can reproduce, the unmetered session route stays viable.
import { openSession } from './lib.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';
const PS  = 'd02af6edc49b9c78d7d55b65';

const { browser, page } = await openSession();
try {
  const seen = [];
  page.on('request', (req) => {
    const u = req.url();
    if (req.method() !== 'GET' && /\/api\//.test(u)) {
      seen.push({ method: req.method(), url: u.replace('https://cad.onshape.com', ''), headers: req.headers() });
    }
  });

  console.log('opening the Part Studio so the client issues its own POSTs...');
  await page.goto(`https://cad.onshape.com/documents/${DID}/w/${WID}/e/${PS}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(25000);

  console.log(`\ncaptured ${seen.length} non-GET /api/ requests`);
  const interesting = new Set();
  for (const r of seen.slice(0, 8)) {
    console.log(`\n${r.method} ${r.url.slice(0, 120)}`);
    for (const [k, v] of Object.entries(r.headers)) {
      if (/^(cookie|user-agent|accept|accept-|referer|origin|content-|sec-|priority)/i.test(k)) continue;
      console.log(`  ${k}: ${String(v).slice(0, 90)}`);
      interesting.add(k);
    }
  }
  console.log(`\ndistinct non-standard headers across all captures: ${[...interesting].join(', ') || '(none)'}`);
} finally {
  await browser.close();
}
