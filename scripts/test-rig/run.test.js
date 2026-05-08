// Unit tests for run.js pure logic — uses the built-in node:test runner.
// Run: node --test scripts/test-rig/run.test.js
//
// Skipped: live integration (requires iRacing + plugin running).

'use strict';

const { test }       = require('node:test');
const assert         = require('node:assert/strict');
const path           = require('node:path');
const os             = require('node:os');

const {
  parseArgs,
  evaluateAssertions,
  indexFilePath,
  SCENARIOS,
} = require('./run.js');

// ── parseArgs ───────────────────────────────────────────────────────────────

test('parseArgs: --help short-circuits', () => {
  const r = parseArgs(['--help']);
  assert.equal(r.help, true);
});

test('parseArgs: rejects missing --subsession', () => {
  assert.throws(() => parseArgs(['--scenario', 'sweep']),
    /missing_or_invalid:--subsession/);
});

test('parseArgs: rejects non-numeric subsession', () => {
  assert.throws(() => parseArgs(['--subsession', 'abc', '--scenario', 'sweep']),
    /missing_or_invalid:--subsession/);
});

test('parseArgs: rejects missing --scenario', () => {
  assert.throws(() => parseArgs(['--subsession', '12345678']),
    /missing_or_invalid:--scenario/);
});

test('parseArgs: rejects unknown scenario', () => {
  assert.throws(() => parseArgs(['--subsession', '12345678', '--scenario', 'bogus']),
    /missing_or_invalid:--scenario/);
});

test('parseArgs: rejects unknown flag', () => {
  assert.throws(
    () => parseArgs(['--subsession', '12345678', '--scenario', 'sweep', '--bogus']),
    /unknown_arg:--bogus/);
});

test('parseArgs: accepts all three scenarios', () => {
  for (const s of SCENARIOS) {
    const r = parseArgs(['--subsession', '99999999', '--scenario', s]);
    assert.equal(r.scenario, s);
    assert.equal(r.subsession, 99999999);
    assert.ok(Number.isFinite(r.scenarioTimeoutMs), `default timeout for ${s}`);
  }
});

test('parseArgs: --include-steam and --teardown set booleans', () => {
  const r = parseArgs([
    '--subsession', '12345678', '--scenario', 'sweep',
    '--include-steam', '--teardown',
  ]);
  assert.equal(r.includeSteam, true);
  assert.equal(r.teardown, true);
});

test('parseArgs: --no-reset and --ws override defaults', () => {
  const r = parseArgs([
    '--subsession', '12345678', '--scenario', 'live-counters',
    '--no-reset', '--ws', 'ws://1.2.3.4:9999',
  ]);
  assert.equal(r.noReset, true);
  assert.equal(r.wsUrl, 'ws://1.2.3.4:9999');
});

test('parseArgs: --reset-timeout-ms / --scenario-timeout-ms override defaults', () => {
  const r = parseArgs([
    '--subsession', '12345678', '--scenario', 'sweep',
    '--reset-timeout-ms', '1000',
    '--scenario-timeout-ms', '2000',
  ]);
  assert.equal(r.resetTimeoutMs, 1000);
  assert.equal(r.scenarioTimeoutMs, 2000);
});

// ── evaluateAssertions ──────────────────────────────────────────────────────

test('evaluateAssertions: empty list = ok=true', () => {
  const r = evaluateAssertions([]);
  assert.deepEqual(r, { pass_count: 0, fail_count: 0, ok: true });
});

test('evaluateAssertions: handles undefined', () => {
  const r = evaluateAssertions(undefined);
  assert.deepEqual(r, { pass_count: 0, fail_count: 0, ok: true });
});

test('evaluateAssertions: counts pass/fail correctly', () => {
  const r = evaluateAssertions([
    { name: 'a', passed: true  },
    { name: 'b', passed: false },
    { name: 'c', passed: true  },
  ]);
  assert.equal(r.pass_count, 2);
  assert.equal(r.fail_count, 1);
  assert.equal(r.ok, false);
});

test('evaluateAssertions: all-pass = ok=true', () => {
  const r = evaluateAssertions([
    { name: 'a', passed: true },
    { name: 'b', passed: true },
  ]);
  assert.equal(r.ok, true);
});

// ── indexFilePath ───────────────────────────────────────────────────────────

test('indexFilePath: builds LOCALAPPDATA path', () => {
  // Restore env after we mutate it.
  const orig = process.env.LOCALAPPDATA;
  try {
    process.env.LOCALAPPDATA = path.join('C:', 'Users', 'test', 'AppData', 'Local');
    const p = indexFilePath(12345678);
    // Use path.join so we get the platform separator that indexFilePath actually used.
    assert.equal(
      p,
      path.join(process.env.LOCALAPPDATA, 'SimSteward', 'replay-incident-index', '12345678.json'),
    );
  } finally {
    if (orig === undefined) delete process.env.LOCALAPPDATA;
    else process.env.LOCALAPPDATA = orig;
  }
});

test('indexFilePath: falls back when LOCALAPPDATA missing', () => {
  const orig = process.env.LOCALAPPDATA;
  try {
    delete process.env.LOCALAPPDATA;
    const p = indexFilePath(99999999);
    assert.ok(p.includes(path.join('AppData', 'Local')), `fallback path expected, got: ${p}`);
    assert.ok(p.endsWith(path.join('replay-incident-index', '99999999.json')));
    // Should be rooted under home dir
    assert.ok(p.startsWith(os.homedir()));
  } finally {
    if (orig === undefined) delete process.env.LOCALAPPDATA;
    else process.env.LOCALAPPDATA = orig;
  }
});
