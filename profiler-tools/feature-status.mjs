// feature-status.mjs — the ERROR MESSAGE behind a red feature, straight from the feature list.
//
// status.mjs reports which features are in ERROR; this reports WHY. The regeneration payload
// carries a featureStatus entry per feature, and the messages on it are the same text the dialog
// shows — which the notices pane does NOT print, because a regen error is not console output.
//
//   OS_DID=.. OS_WID=.. PS_EID=.. node feature-status.mjs
import { openSession, apiGet, DID, WID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID;
if (!PS_EID) {
  console.error('PS_EID=<part studio> is required.');
  process.exit(2);
}

const collect = (node, out, depth = 0) => {
  if (node == null || depth > 6) return out;
  if (Array.isArray(node)) {
    for (const item of node) collect(item, out, depth + 1);
    return out;
  }
  if (typeof node !== 'object') return out;
  if (node.message || node.messageId || node.featureStatus) {
    out.push({
      id: node.featureId ?? node.nodeId ?? '',
      status: node.featureStatus ?? '',
      message: node.message ?? node.messageId ?? '',
    });
  }
  for (const key of Object.keys(node)) collect(node[key], out, depth + 1);
  return out;
};

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(9000);

  for (const path of [
    `/api/v10/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`,
    `/api/v10/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/featurespecs`,
  ]) {
    const res = await apiGet(page, path);
    if (res.status !== 200) { console.log(`${path} -> ${res.status}`); continue; }
    const found = collect(res.body, []);
    const interesting = found.filter((f) => f.message || /ERROR|WARNING/.test(f.status));
    console.log(`\n${path.split('/').pop()}: ${interesting.length} entr(ies) with a message or a bad status`);
    for (const f of interesting.slice(0, 25)) {
      console.log(`  [${f.status || '-'}] ${f.id} ${String(f.message).slice(0, 300)}`);
    }
  }
} finally {
  await browser.close();
}
