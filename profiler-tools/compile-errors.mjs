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

  // The FeatureScript notices panel is CLOSED by default and is the only place compile warnings
  // are listed in full. Without opening it this script reported a clean studio while three
  // warnings sat in it. The toggle is the "{!}" button in the top bar; it carries no title or
  // aria-label to find it by, so it is clicked by position and the result is then verified.
  await page.mouse.click(1332, 20);
  await page.waitForTimeout(6000);
  const panelOpen = await page.evaluate(() =>
    [...document.querySelectorAll('*')].some((el) => el.childElementCount === 0 &&
      (el.textContent ?? '').trim() === 'FeatureScript notices'));
  console.log(`notices panel: ${panelOpen ? 'open' : 'NOT OPEN - warnings may be missed'}`);

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

  // The gutter only exists for lines Ace has SCROLLED INTO VIEW -- it virtualises the editor, so a
  // warning a thousand lines down has no cell to find and the marker count reads zero on a studio
  // that is visibly complaining. The notices panel lists every one, so that is what to read.
  const problems = await page.evaluate(() => {
    const leaves = [];
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      const t = (el.textContent ?? '').trim();
      if (!t || t.length > 120) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      leaves.push(t);
    }
    // Each notice is written as four leaves: message, line, column, element id. The line and the
    // column are SEPARATE nodes and the colon between them is styling rather than text, so looking
    // for "485:5" as one string finds nothing at all.
    const out = [];
    for (let i = 0; i + 3 < leaves.length; i += 1) {
      if (!/^\d+$/.test(leaves[i + 1]) || !/^\d+$/.test(leaves[i + 2])) continue;
      if (!/^[0-9a-f]{24}$/.test(leaves[i + 3])) continue;
      out.push(`${leaves[i]}  (line ${leaves[i + 1]}:${leaves[i + 2]})`);
    }
    return [...new Set(out)];
  });
  console.log(`
${problems.length} notice-panel problem(s):`);
  for (const row of problems) console.log(`  ${row}`);

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
