// capture.mjs — open Part Studio 1 and record everything the client does while a HUMAN drives
// the profiler by hand. The point is to learn the profiler's real request/response shape from
// the client that already knows it, instead of guessing endpoint names.
//
// It changes nothing: it only listens. Run it, do a normal profile run in the window it opens,
// and every /api/ call, every response body worth keeping and every websocket frame lands in
// out/ for reading afterwards.
//
// Usage:  node capture.mjs            (12 minute window)
//         CAPTURE_MINUTES=25 node capture.mjs
import { mkdir, writeFile, appendFile } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, OUT_DIR, DID, WID, docUrl } from './lib.mjs';

const PS = process.env.PS_EID ?? '2af67ff9259d8b115654d5e6'; // Part Studio 1
const MINUTES = Number(process.env.CAPTURE_MINUTES ?? 12);
const BIG = 400_000; // response bodies larger than this are recorded by size only

// Page-load chatter that has nothing to do with profiling; keeps the live log readable.
const NOISE = /\/(icon|thumbnails?|blobelements|applications|locales|inappmessages|notifications|comments|toolbar|keyboardshortcuts|globaltreenodes|metadata|workflow|deploymentinfo|companies|accounts|adminrole|capabilities|clientinfo|build|webServiceTest|elementLibrary|userpreferences|users\/(settings|preferences|session))/i;

const stamp = () => new Date().toISOString().slice(11, 23);

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });
  const logPath = join(OUT_DIR, 'capture.log');
  const bodiesPath = join(OUT_DIR, 'capture-bodies.jsonl');
  const framesPath = join(OUT_DIR, 'capture-frames.jsonl');
  await writeFile(logPath, '');
  await writeFile(bodiesPath, '');
  await writeFile(framesPath, '');

  const seenPaths = new Set();
  let calls = 0;
  let frames = 0;

  const note = async (line) => {
    console.log(line);
    await appendFile(logPath, line + '\n');
  };

  page.on('response', async (res) => {
    const url = res.url();
    if (!/\/api\//.test(url)) return;
    const path = url.replace(/^https?:\/\/[^/]+/, '');
    const method = res.request().method();
    const bare = path.split('?')[0];
    calls += 1;
    const quiet = NOISE.test(bare) && method === 'GET';

    let body = null;
    let bytes = 0;
    try {
      const buf = await res.body();
      bytes = buf.length;
      if (bytes <= BIG) body = buf.toString('utf8');
    } catch { /* redirects and preflights have no body */ }

    // Anything that smells of timing gets shouted about, whatever else it is.
    const hot = /timer|profil|perf|telemetr|elapsed|duration|regen|microversion/i.test(bare) ||
      (body != null && /"(enableTimers|profile|timings?|elapsed|durationMs|featureTime)"/i.test(body));

    if (hot || method !== 'GET' || !quiet) {
      const key = `${method} ${bare}`;
      const fresh = !seenPaths.has(key);
      seenPaths.add(key);
      if (hot || fresh || method !== 'GET') {
        await note(`${stamp()} ${hot ? '**' : '  '} ${String(res.status()).padEnd(4)} ${method.padEnd(5)} ${String(bytes).padStart(8)}B  ${bare}`);
      }
    }
    if (body != null && (hot || method !== 'GET')) {
      await appendFile(bodiesPath, JSON.stringify({
        at: stamp(), method, path, status: res.status(), bytes,
        body: body.length > 20000 ? body.slice(0, 20000) + '…[truncated]' : body,
      }) + '\n');
    }
  });

  page.on('request', async (req) => {
    if (req.method() === 'GET' || !/\/api\//.test(req.url())) return;
    const data = req.postData();
    if (!data) return;
    await appendFile(bodiesPath, JSON.stringify({
      at: stamp(), direction: 'request', method: req.method(),
      path: req.url().replace(/^https?:\/\/[^/]+/, ''),
      body: data.length > 20000 ? data.slice(0, 20000) + '…[truncated]' : data,
    }) + '\n');
  });

  page.on('websocket', (ws) => {
    note(`${stamp()}    websocket open  ${ws.url().slice(0, 110)}`);
    const record = (dir) => async (f) => {
      const raw = typeof f.payload === 'string' ? Buffer.from(f.payload, 'utf8') : f.payload;
      if (!raw) return;
      frames += 1;
      // The modelling socket is protobuf; keep the printable runs, which is where the field
      // names live, and the raw length so nothing is silently lost.
      const text = raw.toString('utf8').replace(/[^\x20-\x7e]+/g, ' ').replace(/\s+/g, ' ').trim();
      if (!/timer|profil|elapsed|duration|regen|featureTime|perf/i.test(text)) return;
      await note(`${stamp()} ** ws ${dir} ${raw.length}B  ${text.slice(0, 160)}`);
      await appendFile(framesPath, JSON.stringify({ at: stamp(), dir, bytes: raw.length, text: text.slice(0, 4000) }) + '\n');
    };
    ws.on('framereceived', record('<-'));
    ws.on('framesent', record('->'));
  });

  await note(`opening Part Studio 1 (${PS})`);
  await page.goto(docUrl(PS), { waitUntil: 'domcontentloaded' });

  console.log('');
  console.log('='.repeat(78));
  console.log('  RECORDING. Drive the profiler in the browser window exactly as you normally do.');
  console.log('  Lines marked ** are timing-shaped and are what I am hunting for.');
  console.log(`  Window: ${MINUTES} minutes. Close this or wait it out when the profile has come back.`);
  console.log('='.repeat(78));
  console.log('');

  const deadline = Date.now() + MINUTES * 60_000;
  while (Date.now() < deadline) {
    await page.waitForTimeout(30_000);
    if (page.isClosed()) break;
    console.log(`${stamp()}    ...still recording (${Math.round((deadline - Date.now()) / 60_000)} min left; ${calls} api calls, ${frames} ws frames)`);
  }

  await note(`\ncapture finished: ${calls} /api/ calls, ${frames} websocket frames`);
  await note(`distinct paths seen:\n  ${[...seenPaths].sort().join('\n  ')}`);
  console.log('\n  -> out/capture.log, out/capture-bodies.jsonl, out/capture-frames.jsonl');
} finally {
  await browser.close();
}
