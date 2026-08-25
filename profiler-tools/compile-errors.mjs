// compile-errors.mjs — what a Feature Studio is complaining about, after a push that broke it.
//
// A push reporting VERIFIED only means the BYTES arrived; it says nothing about whether the element
// compiles. The symptom of a compile error is every feature in every importing element reporting
// ERROR with no message of its own, so this reads the markers off the studio itself.
//
//   FS_EID=<feature studio> node compile-errors.mjs
import { openSession, readNotices, UTILS_EID, docUrl } from './lib.mjs';

const EID = process.env.FS_EID ?? UTILS_EID;

const { browser, page } = await openSession();
try {
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(25_000);

  const notices = await readNotices(page);
  console.log(`${notices.length} notice(s) on ${EID}:`);
  for (const n of notices) console.log(`  ${n}`);

  // Ace marks the offending lines in the gutter; the annotation text is the actual message.
  const markers = await page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('.ace_gutter-cell')) {
      const cls = String(el.className);
      if (!/ace_error|ace_warning|ace_info/.test(cls)) continue;
      out.push({ line: (el.textContent ?? '').trim(), cls });
    }
    return out;
  });
  console.log(`\n${markers.length} gutter marker(s):`);
  for (const m of markers) console.log(`  line ${m.line}  ${m.cls}`);

  const tooltip = await page.evaluate(() => {
    const bits = [];
    for (const el of document.querySelectorAll('[class*="annotation"], [class*="tooltip"], .ace_tooltip')) {
      const t = (el.textContent ?? '').trim();
      if (t) bits.push(t.slice(0, 300));
    }
    return [...new Set(bits)];
  });
  if (tooltip.length) {
    console.log('\nannotation text:');
    for (const t of tooltip) console.log(`  ${t}`);
  }
} finally {
  await browser.close();
}
