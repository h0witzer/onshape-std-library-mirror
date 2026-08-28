// lib.mjs — shared helpers for driving the Onshape Drawings REST API.
// Auth is ONLY ever a logged-in browser session (session cookie). No API key, no OAuth app,
// nothing that counts against the Onshape API quota. No secrets are stored in source.
import { chromium } from 'playwright';
import { existsSync } from 'node:fs';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));

export const AUTH_FILE = join(HERE, '.auth', 'onshape-state.json');
export const OUT_DIR = join(HERE, 'out');

// Same-origin requests run inside the page, so they ride the session cookie.
export const apiGet = (page, path) => page.evaluate(async (p) => {
  const r = await fetch(p, { credentials: 'same-origin', headers: { accept: 'application/json' } });
  return { status: r.status, body: await r.json().catch(() => null) };
}, path);

// State-changing calls are CSRF-guarded: the XSRF-TOKEN cookie must be echoed as a header,
// or Onshape answers 401 even with a perfectly valid session.
export const apiPost = (page, path, payload) => page.evaluate(async ({ p, b }) => {
  // Keep everything after the FIRST '=': the token is base64 and its '==' padding is part
  // of the value. Splitting on every '=' truncates it and the request comes back 401.
  const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
  const xsrf = raw.slice(raw.indexOf('=') + 1);
  const r = await fetch(p, {
    method: 'POST',
    credentials: 'same-origin',
    headers: {
      accept: 'application/json',
      'content-type': 'application/json',
      'x-xsrf-token': xsrf,
    },
    body: JSON.stringify(b),
  });
  const text = await r.text();
  let body = null;
  try { body = JSON.parse(text); } catch { body = text; }
  return { status: r.status, body };
}, { p: path, b: payload });

// The /modify endpoint is asynchronous: it returns a modification id to poll.
export async function waitForModify(page, mrid, { timeoutMs = 120_000, intervalMs = 1500 } = {}) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < deadline) {
    const r = await apiGet(page, `/api/v6/drawings/modify/status/${mrid}`);
    last = r.body;
    const state = last?.requestState ?? last?.state ?? last?.status;
    if (state && state !== 'ACTIVE') return { state, body: last };
    await page.waitForTimeout(intervalMs);
  }
  return { state: 'TIMEOUT', body: last };
}

// Open a headed browser backed by a live Onshape session. Reuses a saved session if valid;
// otherwise waits for a hand login (email / SSO / 2FA). Nothing is stored beyond the cookie.
export async function openSession({ loginTimeoutMs = 6 * 60_000 } = {}) {
  const browser = await chromium.launch({ headless: false });
  const context = await browser.newContext(existsSync(AUTH_FILE) ? { storageState: AUTH_FILE } : {});
  const page = await context.newPage();

  // Probe both API versions: the session endpoint is not present under every prefix,
  // and a 404 from the wrong one is indistinguishable from being logged out.
  const status = () => page.evaluate(async () => {
    for (const p of ['/api/v14/users/session', '/api/v6/users/session', '/api/users/session']) {
      try {
        const r = await fetch(p, { credentials: 'same-origin', headers: { accept: 'application/json' } });
        if (r.status === 200) return 200;
      } catch { /* keep trying */ }
    }
    return 0;
  });

  await page.goto('https://cad.onshape.com/documents', { waitUntil: 'domcontentloaded' });
  if ((await status()) !== 200) {
    console.log('Log in to Onshape in the window; I resume automatically once authenticated...');
    const deadline = Date.now() + loginTimeoutMs;
    let ticks = 0;
    while (Date.now() < deadline && (await status()) !== 200) {
      if (++ticks % 15 === 0) console.log(`  still waiting for login (${Math.round((deadline - Date.now()) / 1000)}s left)...`);
      await page.waitForTimeout(2000);
    }
  }
  if ((await status()) !== 200) { await browser.close(); throw new Error('Not authenticated within the time limit.'); }

  await mkdir(dirname(AUTH_FILE), { recursive: true });
  await context.storageState({ path: AUTH_FILE });
  console.log('Authenticated.');
  return { browser, context, page };
}
