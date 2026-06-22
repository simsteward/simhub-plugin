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
  aggregateByImpactClass,
  validateSmokeIndex,
  buildSmokeReport,
  resolveSubsession,
} = require('./run.js');

// ── parseArgs ───────────────────────────────────────────────────────────────

test('parseArgs: --help short-circuits', () => {
  const r = parseArgs(['--help']);
  assert.equal(r.help, true);
});

test('parseArgs: allows missing --subsession (auto-detect)', () => {
  const r = parseArgs(['--scenario', 'sweep']);
  assert.equal(r.subsession, null);
  assert.equal(r.scenario, 'sweep');
});

test('parseArgs: rejects non-numeric subsession when supplied', () => {
  assert.throws(() => parseArgs(['--subsession', 'abc', '--scenario', 'sweep']),
    /invalid:--subsession/);
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

test('parseArgs: accepts all four scenarios (incl. smoke)', () => {
  assert.ok(SCENARIOS.includes('smoke'), 'smoke is registered');
  for (const s of SCENARIOS) {
    const r = parseArgs(['--subsession', '99999999', '--scenario', s]);
    assert.equal(r.scenario, s);
    assert.equal(r.subsession, 99999999);
    assert.ok(Number.isFinite(r.scenarioTimeoutMs), `default timeout for ${s}`);
  }
});

test('parseArgs: --teardown sets boolean', () => {
  const r = parseArgs([
    '--subsession', '12345678', '--scenario', 'sweep',
    '--teardown',
  ]);
  assert.equal(r.teardown, true);
});

test('parseArgs: --ws overrides default', () => {
  const r = parseArgs([
    '--subsession', '12345678', '--scenario', 'live-counters',
    '--ws', 'ws://1.2.3.4:9999',
  ]);
  assert.equal(r.wsUrl, 'ws://1.2.3.4:9999');
});

test('parseArgs: --scenario-timeout-ms overrides default', () => {
  const r = parseArgs([
    '--subsession', '12345678', '--scenario', 'sweep',
    '--scenario-timeout-ms', '2000',
  ]);
  assert.equal(r.scenarioTimeoutMs, 2000);
});

// ── resolveSubsession ───────────────────────────────────────────────────────

test('resolveSubsession: flag omitted, hello present → use hello', () => {
  const r = resolveSubsession({ flagValue: null, helloValue: 12345678 });
  assert.deepEqual(r, { ok: true, subsession: 12345678, source: 'hello' });
});

test('resolveSubsession: flag matches hello → proceed', () => {
  const r = resolveSubsession({ flagValue: 12345678, helloValue: 12345678 });
  assert.deepEqual(r, { ok: true, subsession: 12345678, source: 'flag' });
});

test('resolveSubsession: flag mismatches hello → abort', () => {
  const r = resolveSubsession({ flagValue: 999, helloValue: 12345678 });
  assert.equal(r.ok, false);
  assert.equal(r.error, 'subsession_mismatch');
  assert.equal(r.flag, 999);
  assert.equal(r.loaded, 12345678);
});

test('resolveSubsession: no hello → no_replay_loaded', () => {
  assert.deepEqual(resolveSubsession({ flagValue: null, helloValue: null }),
    { ok: false, error: 'no_replay_loaded' });
  assert.deepEqual(resolveSubsession({ flagValue: 12345678, helloValue: 0 }),
    { ok: false, error: 'no_replay_loaded' });
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

// ── smoke fixtures + helpers ────────────────────────────────────────────────

const VALID_SMOKE_INDEX = {
  sessions: [
    { sessionNum: 0, name: 'PRACTICE', type: 'Open Practice', impactClass: 'free' },
    { sessionNum: 1, name: 'QUALIFY',  type: 'Lone Qualify',  impactClass: 'partial' },
    { sessionNum: 2, name: 'RACE',     type: 'Race',          impactClass: 'full' },
  ],
  incidents: [
    { sessionNum: 0, fingerprint: 'a' },
    { sessionNum: 0, fingerprint: 'b' },
    { sessionNum: 1, fingerprint: 'c' },
    { sessionNum: 2, fingerprint: 'd' },
    { sessionNum: 2, fingerprint: 'e' },
    { sessionNum: 2, fingerprint: 'f' },
  ],
};

test('aggregateByImpactClass: sums incidents per session impactClass', () => {
  const t = aggregateByImpactClass(VALID_SMOKE_INDEX.incidents, VALID_SMOKE_INDEX.sessions);
  assert.deepEqual(t, { incidents: 6, free: 2, partial: 1, full: 3 });
});

test('aggregateByImpactClass: returns zeros on empty inputs', () => {
  assert.deepEqual(
    aggregateByImpactClass([], []),
    { incidents: 0, free: 0, partial: 0, full: 0 },
  );
  assert.deepEqual(
    aggregateByImpactClass(undefined, undefined),
    { incidents: 0, free: 0, partial: 0, full: 0 },
  );
});

test('validateSmokeIndex: passes on a valid index', () => {
  const r = validateSmokeIndex(VALID_SMOKE_INDEX);
  assert.equal(r.passed, true, `failures: ${JSON.stringify(r.failures)}`);
  assert.deepEqual(r.failures, []);
});

test('validateSmokeIndex: flags missing sessions array', () => {
  const r = validateSmokeIndex({ incidents: [] });
  assert.equal(r.passed, false);
  assert.ok(r.failures.includes('sessions_array_missing'));
});

test('validateSmokeIndex: flags empty sessions array', () => {
  const r = validateSmokeIndex({ sessions: [], incidents: [] });
  assert.equal(r.passed, false);
  assert.ok(r.failures.includes('sessions_array_empty'));
});

test('validateSmokeIndex: flags missing incidents array', () => {
  const r = validateSmokeIndex({
    sessions: [{ sessionNum: 0, name: 'P', type: 'Practice', impactClass: 'free' }],
  });
  assert.equal(r.passed, false);
  assert.ok(r.failures.includes('incidents_array_missing'));
});

test('validateSmokeIndex: flags invalid impactClass', () => {
  const r = validateSmokeIndex({
    sessions: [{ sessionNum: 0, impactClass: 'bogus' }],
    incidents: [],
  });
  assert.equal(r.passed, false);
  assert.ok(r.failures.some(f => f.startsWith('session_invalid_impact_class')),
    `failures: ${JSON.stringify(r.failures)}`);
});

test('validateSmokeIndex: flags orphan incident sessionNum', () => {
  const r = validateSmokeIndex({
    sessions: [{ sessionNum: 0, impactClass: 'free' }],
    incidents: [{ sessionNum: 0 }, { sessionNum: 99 }],
  });
  assert.equal(r.passed, false);
  assert.ok(r.failures.some(f => f === 'incident_orphan_sessionNum:99'),
    `failures: ${JSON.stringify(r.failures)}`);
});

test('buildSmokeReport: shape matches spec on valid input', () => {
  const r = buildSmokeReport(VALID_SMOKE_INDEX, 304211, 85355778);
  assert.equal(r.passed, true, `failures: ${JSON.stringify(r.failures)}`);
  assert.equal(r.sub_session_id, 85355778);
  assert.equal(r.duration_ms, 304211);
  assert.deepEqual(r.totals, { incidents: 6, free: 2, partial: 1, full: 3 });
  assert.equal(r.sessions.length, 3);
  assert.deepEqual(r.sessions[0], {
    sessionNum: 0, name: 'PRACTICE', type: 'Open Practice', impactClass: 'free', incidents: 2,
  });
  assert.deepEqual(r.sessions[2], {
    sessionNum: 2, name: 'RACE', type: 'Race', impactClass: 'full', incidents: 3,
  });
  assert.deepEqual(r.failures, []);
});

test('buildSmokeReport: passed=false populates failures[]', () => {
  const r = buildSmokeReport(
    { sessions: [{ sessionNum: 0, impactClass: 'bogus' }], incidents: [{ sessionNum: 99 }] },
    1234,
    7777,
  );
  assert.equal(r.passed, false);
  assert.ok(r.failures.length >= 2, `expected multiple failures, got: ${JSON.stringify(r.failures)}`);
  assert.equal(r.sub_session_id, 7777);
  assert.equal(r.duration_ms, 1234);
});
