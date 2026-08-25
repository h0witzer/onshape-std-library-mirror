// feature-errors.mjs — the actual error text behind a feature that reports ERROR.
//
// The feature-list API gives you the word `ERROR` and nothing else, and a compile-clean element with
// erroring features leaves you no message to read. The text lives in the Part Studio's own feature
// list, so this opens it and harvests the lines that look like FeatureScript diagnostics.
//
//   PS_EID=<part studio> node feature-errors.mjs
import { openSession, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID ?? 'f52300ae31a4fb83b720f044';

const { browser, page } = await openSession();
try {
  await page.setViewportSize({ width: 1920, height: 1400 });
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(30_000);

  const found = await page.evaluate(() => {
    const wanted = /does not match|Call .*\(|Cannot |cannot |expects|expected|Undefined|undefined variable|is not |Regeneration|error/i;
    const out = [];
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      const t = (el.textContent ?? '').trim();
      if (!t || t.length < 12 || t.length > 600) continue;
      if (!wanted.test(t)) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      out.push(t);
    }
    return [...new Set(out)];
  });
  console.log(`${found.length} diagnostic-looking line(s):`);
  for (const f of found) console.log(`  ${f}`);

  // Feature-list rows carry their status in the title attribute even when the text does not.
  const titles = await page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('[title]')) {
      const t = (el.getAttribute('title') ?? '').trim();
      if (t.length > 20 && /match|error|Cannot|expects/i.test(t)) out.push(t.slice(0, 400));
    }
    return [...new Set(out)];
  });
  if (titles.length) {
    console.log('\ntitle attributes:');
    for (const t of titles) console.log(`  ${t}`);
  }
} finally {
  await browser.close();
}
