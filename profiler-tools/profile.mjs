// profile.mjs — drive the Feature Studio's "Profiling Part Studio 1" button and capture what
// comes back. The profiler is an EDITOR feature, not a Part Studio one: the button lives in the
// Feature Studio toolbar, and the results land as per-function times and hit counts in the
// editor's left gutter.
//
// This run does three things at once, because the first one is a discovery run and I do not yet
// know which of them will be the route that lasts:
//   1. records every /api/ response and websocket frame around the click, so the payload that
//      carries the timings can be found by reading rather than by guessing;
//   2. snapshots the gutter DOM before and after, so the rendered numbers are recoverable even
//      if the payload turns out to be protobuf on the socket;
//   3. screenshots the result, because a number that is only in a log has not been verified.
//
// Usage:  node profile.mjs                 (clicks the button itself)
//         MANUAL=1 node profile.mjs        (waits for you to click it)
import { mkdir, writeFile, appendFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, OUT_DIR, DID, WID, UTILS_EID, docUrl } from './lib.mjs';

const EID = process.env.FS_EID ?? UTILS_EID;      // the Feature Studio holding the code
const MANUAL = process.env.MANUAL === '1';
const WAIT_MINUTES = Number(process.env.WAIT_MINUTES ?? 8);
const BIG = 400_000;

const stamp = () => new Date().toISOString().slice(11, 23);

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  const logPath = join(OUT_DIR, 'profile.log');
  const bodiesPath = join(OUT_DIR, 'profile-bodies.jsonl');
  await writeFile(logPath, '');
  await writeFile(bodiesPath, '');
  const note = async (l) => { console.log(l); await appendFile(logPath, l + '\n'); };

  let armed = false; // only shout about traffic once the button has been pressed
  const record = async (entry) => appendFile(bodiesPath, JSON.stringify(entry) + '\n');

  page.on('response', async (res) => {
    const url = res.url();
    if (!/\/api\//.test(url)) return;
    const path = url.replace(/^https?:\/\/[^/]+/, '');
    const method = res.request().method();
    let body = null, bytes = 0;
    try { const b = await res.body(); bytes = b.length; if (bytes <= BIG) body = b.toString('utf8'); } catch { /* no body */ }
    const hot = /timer|profil|perf|telemetr|elapsed|duration/i.test(path.split('?')[0]) ||
      (body != null && /"(enableTimers|profile|timings?|elapsedMs|durationMs|hitCount|callCount|selfTime)"/i.test(body));
    if (armed && (hot || method !== 'GET')) {
      await note(`${stamp()} ${hot ? '**' : '  '} ${String(res.status()).padEnd(4)} ${method.padEnd(5)} ${String(bytes).padStart(9)}B  ${path.split('?')[0]}`);
      await record({ at: stamp(), method, path, status: res.status(), bytes, body: body?.slice(0, 40000) ?? null });
    }
  });
  page.on('request', async (req) => {
    if (req.method() === 'GET' || !/\/api\//.test(req.url())) return;
    const d = req.postData();
    if (armed && d) await record({ at: stamp(), direction: 'request', method: req.method(), path: req.url().replace(/^https?:\/\/[^/]+/, ''), body: d.slice(0, 40000) });
  });
  page.on('websocket', (ws) => {
    const grab = (dir) => async (f) => {
      if (!armed) return;
      const raw = typeof f.payload === 'string' ? Buffer.from(f.payload, 'utf8') : f.payload;
      if (!raw) return;
      const text = raw.toString('utf8').replace(/[^\x20-\x7e]+/g, ' ').replace(/\s+/g, ' ').trim();
      if (!/timer|profil|elapsed|duration|hitCount|callCount|selfTime|totalTime/i.test(text)) return;
      await note(`${stamp()} ** ws ${dir} ${raw.length}B  ${text.slice(0, 200)}`);
      await record({ at: stamp(), direction: `ws ${dir}`, bytes: raw.length, body: text.slice(0, 40000) });
    };
    ws.on('framereceived', grab('<-'));
    ws.on('framesent', grab('->'));
  });

  await note(`opening the Feature Studio (${EID})`);
  await page.setViewportSize({ width: 1920, height: 1200 });
  await page.goto(docUrl(EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(20_000); // the editor mounts well after domcontentloaded

  // ---------- find the button ----------
  const buttons = await page.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll('button, a, div[role="button"], span')) {
      const t = (el.textContent ?? '').trim();
      const label = [el.getAttribute?.('title'), el.getAttribute?.('aria-label')].filter(Boolean).join(' ');
      if (!/profil|monitor part studio/i.test(`${t} ${label}`)) continue;
      if (t.length > 60) continue;
      out.push({ tag: el.tagName, text: t, label, cls: String(el.className ?? '').slice(0, 80) });
    }
    return out;
  });
  await note(`profiler controls found in the toolbar: ${buttons.length}`);
  for (const b of buttons) await note(`   <${b.tag}> ${JSON.stringify(b.text)} ${b.label} .${b.cls}`);
  await writeFile(join(OUT_DIR, 'profile-buttons.json'), JSON.stringify(buttons, null, 2));
  await page.screenshot({ path: join(OUT_DIR, 'featurestudio-before.png') });

  armed = true;
  if (MANUAL) {
    await note(`\nMANUAL mode: press "Profiling Part Studio 1" yourself. Recording for ${WAIT_MINUTES} min.`);
  } else {
    // Idle label is "Monitor Part Studio 1"; it becomes "Profiling Part Studio 1" once armed.
    // The label is a <span class="os-tool-command-name"> inside Onshape's own toolbar widget,
    // not a <button>, so match the span and let Playwright walk up to whatever is clickable.
    const target = page.locator('.os-tool-command-name', { hasText: /Monitor Part Studio|Profiling Part Studio/ }).first();
    if (await target.count() === 0) {
      await note('\ncould not locate the profiling button - rerun with MANUAL=1 and press it by hand.');
    } else {
      await note(`\nclicking the profiling control...`);
      await target.click({ timeout: 15_000 }).catch((e) => note(`  click failed: ${e.message}`));
    }
  }

  // ---------- wait for the profile to come back, watching the gutter ----------
  const readGutter = () => page.evaluate(() => {
    // The profiler writes its numbers into the editor's left gutter. I do not yet know which
    // element carries the per-function detail, so take everything that looks like a duration
    // AND everything around it: class, title, the parent's title, and the ancestor chain's
    // classes. One of those is the handle the real scraper will use.
    const rows = [];
    const isDuration = (t) => /^\d+(\.\d+)?\s*(ms|s|us|µs|%)$/i.test(t);
    for (const el of document.querySelectorAll('*')) {
      if (el.childElementCount !== 0) continue;
      const t = (el.textContent ?? '').trim();
      const title = el.getAttribute('title') ?? '';
      const parentTitle = el.parentElement?.getAttribute('title') ?? '';
      const hasTiming = isDuration(t) || /\d+(\.\d+)?\s*(ms|s)/i.test(title + ' ' + parentTitle);
      if (!hasTiming) continue;
      const chain = [];
      let a = el;
      for (let i = 0; a && i < 4; i += 1, a = a.parentElement) {
        chain.push(`${a.tagName}.${String(a.className ?? '').split(/\s+/).slice(0, 3).join('.')}`);
      }
      rows.push({
        text: t,
        title: title.slice(0, 400),
        parentTitle: parentTitle.slice(0, 400),
        chain: chain.join(' < '),
      });
    }
    return rows;
  });

  const deadline = Date.now() + WAIT_MINUTES * 60_000;
  let gutter = [];
  while (Date.now() < deadline) {
    await page.waitForTimeout(15_000);
    if (page.isClosed()) break;
    gutter = await readGutter().catch(() => []);
    await note(`${stamp()}    waiting... gutter entries: ${gutter.length} (${Math.round((deadline - Date.now()) / 60_000)} min left)`);
    if (gutter.length > 0 && !MANUAL) break;
  }

  await note(`\ngutter entries captured: ${gutter.length}`);
  for (const g of gutter.slice(0, 30)) await note(`   ${g.text.padStart(9)}  ${g.title}`);
  await writeFile(join(OUT_DIR, 'gutter.json'), JSON.stringify(gutter, null, 2));
  await page.screenshot({ path: join(OUT_DIR, 'featurestudio-after.png') });
  await note('  -> out/profile.log, out/profile-bodies.jsonl, out/gutter.json, out/featurestudio-after.png');

  if (MANUAL) { await note('\nleaving the window open for 60s.'); await page.waitForTimeout(60_000); }
} finally {
  await browser.close();
}
