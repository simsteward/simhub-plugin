#!/usr/bin/env node
// ─────────────────────────────────────────────────────────────────────────────
//  drive-dashboard.mjs — Playwright dashboard driver
//
//  Purpose: exercise the PRODUCTION control path by driving the real dashboard UI
//  (reset → trigger) the same way a human would. This is the "full-stack" trigger
//  for a verified sweep; the lean WS-driven alternative is scripts/test-rig/run.js.
//
//  The monitor must already be connected (holding the WS open) BEFORE this runs —
//  the plugin cancels an in-progress build when the last dashboard client
//  disconnects, and this driver closes its tab as soon as it has triggered.
//
//  Usage:
//    node scripts/test-rig/harness/drive-dashboard.mjs [--base <url>] [--expect-speed <n>]
//
//  Flags:
//    --base <url>        default http://localhost:8888/Web/sim-steward-dash
//    --expect-speed <n>  optional; confirm #tr-sweep-speed reads this speed
//
//  Requires a local Playwright install (unlike the WS-side scripts):
//    npm i -D playwright && npx playwright install chromium
//
//  Exit codes:
//    0  reset + trigger dispatched (sweep handed off to the monitor)
//    2  Playwright not installed
//    1  a step failed (could not reach the dashboard / element)
// ─────────────────────────────────────────────────────────────────────────────

'use strict';

import fs   from 'node:fs';
import path from 'node:path';
import url  from 'node:url';

// ── guard: Playwright must be installed ──────────────────────────────────────
let chromium;
try {
  ({ chromium } = await import('playwright'));
} catch {
  console.error('Playwright not installed — run: npm i -D playwright && npx playwright install chromium ' +
                '(or use scripts/test-rig/run.js for WS-driven triggering)');
  process.exit(2);
}

// ── tiny arg parser ──────────────────────────────────────────────────────────
function parseArgs(argv) {
  const args = { base: 'http://localhost:8888/Web/sim-steward-dash', expectSpeed: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case '--base':         args.base = next(); break;
      case '--expect-speed': args.expectSpeed = Number(next()); break;
      case '-h':
      case '--help':         args.help = true; break;
      default:
        if (a.startsWith('--')) throw new Error(`unknown_arg:${a}`);
    }
  }
  return args;
}

const args = parseArgs(process.argv.slice(2));
if (args.help) {
  console.log('Usage: node drive-dashboard.mjs [--base url] [--expect-speed n]');
  process.exit(0);
}

const __dirname = path.dirname(url.fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, '..', '..', '..');
const SHOT_DIR  = path.join(REPO_ROOT, 'logs', 'test-rig');
fs.mkdirSync(SHOT_DIR, { recursive: true });

const log = (s) => console.log(new Date().toISOString(), s);

const TEST_RIG_URL = args.base.replace(/\/$/, '') + '/test-rig.html';
// The replay-incident-index page was merged into index.html's "Replay Index" tab —
// drive that tab directly instead of navigating to a separate page.
const INDEX_URL    = args.base.replace(/\/$/, '') + '/index.html';

// read session_time off the test-rig page (judge "at start" by time, NOT raw frame)
async function readSessionTime(page) {
  return page.evaluate(() => {
    // tr-session-time-ish: scrape any element text mentioning a session_time number
    const el = document.querySelector('[id*="session"], .meta, #tr-session-time');
    const txt = document.body ? document.body.innerText : '';
    const m = txt.match(/session[_ ]?time[^0-9-]*(-?\d+(?:\.\d+)?)/i);
    return m ? parseFloat(m[1]) : null;
  });
}

async function gotoWithRetry(page, url, label) {
  try {
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 20000 });
  } catch (e) {
    log('  [retry] reloading ' + label + ' (' + e.message + ')');
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 20000 });
  }
}

let browser;
try {
  browser = await chromium.launch({ headless: true });
  const ctx = await browser.newContext();
  const page = await ctx.newPage();

  // ── 1. open test-rig, reset to a paused start ──────────────────────────────
  log('[driver] opening test-rig: ' + TEST_RIG_URL);
  await gotoWithRetry(page, TEST_RIG_URL, 'test-rig.html');
  await page.waitForTimeout(2000); // let WS connect + first state tick land

  const timeBefore = await readSessionTime(page);
  log('[driver] session_time before reset: ' + (timeBefore ?? 'unknown'));

  // pause if currently playing (button toggles ▶ Play / ⏸ Pause)
  const playPause = page.locator('#tr-play-pause');
  try {
    const label = (await playPause.textContent({ timeout: 5000 })) || '';
    if (/pause/i.test(label)) {
      log('[driver] replay is playing — clicking #tr-play-pause to pause');
      await playPause.click();
      await page.waitForTimeout(500);
    }
  } catch (e) {
    log('  [warn] could not read #tr-play-pause state: ' + e.message);
  }

  // jump to start
  log('[driver] clicking #tr-jump-start (jump to replay start)');
  await page.locator('#tr-jump-start').click({ timeout: 10000 });
  await page.waitForTimeout(2000);

  // verify by session_time dropping (NOT raw frame — numbering is inverted)
  const timeAfter = await readSessionTime(page);
  log('[driver] session_time after jump-start: ' + (timeAfter ?? 'unknown'));
  if (timeBefore != null && timeAfter != null && timeAfter < timeBefore) {
    log('[driver] OK: session_time dropped (jumped toward start)');
  } else {
    log('  [warn] could not confirm session_time drop ' +
        '(before=' + timeBefore + ' after=' + timeAfter + ') — continuing');
  }

  // ── 2. open the dashboard, switch to the Replay Index tab, start the build ──
  log('[driver] opening dashboard: ' + INDEX_URL);
  await gotoWithRetry(page, INDEX_URL, 'index.html');
  await page.waitForTimeout(1500);
  log('[driver] clicking Replay Index tab');
  await page.locator('.log-tab[data-tab="replayindex"]').click({ timeout: 10000 });
  await page.waitForTimeout(300);
  log('[driver] clicking #ri-btn-start (start index build)');
  await page.locator('#ri-btn-start').click({ timeout: 10000 });
  await page.waitForTimeout(1000);

  // ── 3. back on test-rig, confirm sweep speed ───────────────────────────────
  log('[driver] reopening test-rig to confirm sweep speed');
  await gotoWithRetry(page, TEST_RIG_URL, 'test-rig.html');
  await page.waitForTimeout(2500); // let a progress tick arrive
  const sweepText = (await page.locator('#tr-sweep-speed').textContent({ timeout: 5000 })
    .catch(() => '')) || '';
  log('[driver] #tr-sweep-speed reads: "' + sweepText.trim() + '"');
  if (args.expectSpeed != null) {
    if (sweepText.includes(String(args.expectSpeed))) {
      log('[driver] OK: sweep speed matches --expect-speed ' + args.expectSpeed);
    } else {
      log('  [warn] sweep speed text does not yet show ' + args.expectSpeed +
          ' (may not have ticked yet) — the monitor will assert this authoritatively');
    }
  }

  // ── 4. screenshot + done ───────────────────────────────────────────────────
  const shot = path.join(SHOT_DIR, 'drive-dashboard_' +
    new Date().toISOString().replace(/[:.]/g, '-') + '.png');
  await page.screenshot({ path: shot, fullPage: true }).catch(() => {});
  log('[driver] screenshot -> ' + shot);
  log('[driver] trigger dispatched. Handing the sweep off to monitor.mjs (not waiting).');

  await browser.close();
  process.exit(0);
} catch (e) {
  log('[driver] FAILED: ' + e.message);
  try { if (browser) await browser.close(); } catch {}
  process.exit(1);
}
