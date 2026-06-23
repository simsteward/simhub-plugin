#!/usr/bin/env node
// ─────────────────────────────────────────────────────────────────────────────
//  scorecard.mjs — Offline cross-run diff
//
//  Purpose: compare preserved index files across runs (e.g. 1× / 2× / 16× from
//  the sweep-speed study). For each index it prints total + per-session counts +
//  detectionSource breakdown. Then, treating the FIRST index as TRUTH, for each
//  other index it reports:
//    - fingerprint overlap with truth (count and /total)
//    - extra fingerprints not in truth
//    - a fallback match rate keyed by (carIdx | sessionNum | round(sessionTimeMs/1000))
//      — robust to fingerprint instability across speeds.
//
//  Usage:
//    node scripts/test-rig/harness/scorecard.mjs <indexA.json> <indexB.json> [indexC.json ...] [--labels 1x,2x,16x]
//
//  Strips a leading UTF-8 BOM on read. Node 22+; no npm deps.
// ─────────────────────────────────────────────────────────────────────────────

'use strict';

import fs from 'node:fs';

// ── tiny arg parser ──────────────────────────────────────────────────────────
function parseArgs(argv) {
  const files = [];
  let labels = null;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--labels') { labels = argv[++i].split(',').map(s => s.trim()); }
    else if (a === '-h' || a === '--help') { return { help: true }; }
    else if (a.startsWith('--')) { throw new Error(`unknown_arg:${a}`); }
    else { files.push(a); }
  }
  return { files, labels };
}

const args = parseArgs(process.argv.slice(2));
if (args.help) {
  console.log('Usage: node scorecard.mjs <indexA.json> <indexB.json> [indexC.json ...] [--labels 1x,2x,16x]');
  process.exit(0);
}
if (!args.files || args.files.length < 2) {
  console.error('FATAL: need at least two index files to diff.');
  console.error('Usage: node scorecard.mjs <indexA.json> <indexB.json> [indexC.json ...] [--labels ...]');
  process.exit(2);
}

const log = (s) => console.log(s);

// ── load + normalize an index ────────────────────────────────────────────────
function loadIndex(file) {
  let t = fs.readFileSync(file, 'utf8');
  if (t.charCodeAt(0) === 0xFEFF) t = t.slice(1); // strip UTF-8 BOM
  const j = JSON.parse(t);
  const inc  = j.incidents || j.Incidents || [];
  const sess = j.sessions  || j.Sessions  || [];

  const incidents = inc.map(x => ({
    fingerprint:   x.fingerprint   ?? x.Fingerprint   ?? null,
    carIdx:        x.carIdx        ?? x.CarIdx        ?? null,
    sessionNum:    x.sessionNum    ?? x.SessionNum    ?? null,
    sessionTimeMs: x.sessionTimeMs ?? x.SessionTimeMs ?? null,
    detectionSource: x.detectionSource ?? x.DetectionSource ?? '?',
  }));
  const sessions = sess.map(s => ({
    sessionNum:  s.sessionNum  ?? s.SessionNum  ?? null,
    name:        s.name        ?? s.Name        ?? '',
    impactClass: s.impactClass ?? s.ImpactClass ?? '',
  }));
  return { file, incidents, sessions };
}

// fallback identity key, robust to fingerprint drift across speeds
function fallbackKey(x) {
  return `${x.carIdx}|${x.sessionNum}|${Math.round((x.sessionTimeMs ?? 0) / 1000)}`;
}

// ── load all + label ─────────────────────────────────────────────────────────
const indexes = args.files.map((f, i) => {
  let data;
  try { data = loadIndex(f); }
  catch (e) { console.error(`FATAL: failed to load ${f}: ${e.message}`); process.exit(2); }
  data.label = (args.labels && args.labels[i]) || `idx${i}`;
  return data;
});

// ── per-index summary ────────────────────────────────────────────────────────
log('========== PER-INDEX SUMMARY ==========');
for (const ix of indexes) {
  const per = {};
  for (const s of ix.sessions) {
    per[s.sessionNum] = ix.incidents.filter(x => x.sessionNum === s.sessionNum).length;
  }
  const srcs = {};
  for (const x of ix.incidents) srcs[x.detectionSource] = (srcs[x.detectionSource] || 0) + 1;

  log('');
  log(`[${ix.label}] ${ix.file}`);
  log('  total=' + ix.incidents.length);
  for (const s of ix.sessions) {
    log('  [' + s.sessionNum + '] ' + s.name + ' ' + s.impactClass +
        ' incidents=' + (per[s.sessionNum] ?? 0));
  }
  log('  detectionSource: ' + JSON.stringify(srcs));
}

// ── cross-index diff vs truth (first index) ──────────────────────────────────
const truth = indexes[0];
const truthFps = new Set(truth.incidents.map(x => x.fingerprint).filter(Boolean));
const truthKeys = new Map();
for (const x of truth.incidents) {
  const k = fallbackKey(x);
  truthKeys.set(k, (truthKeys.get(k) || 0) + 1);
}

log('');
log('========== DIFF vs TRUTH (' + truth.label + ') ==========');
log('truth total=' + truth.incidents.length +
    ' unique-fingerprints=' + truthFps.size);

for (let i = 1; i < indexes.length; i++) {
  const ix = indexes[i];
  const ixFps = ix.incidents.map(x => x.fingerprint).filter(Boolean);
  const ixFpSet = new Set(ixFps);

  let overlap = 0;
  for (const fp of ixFpSet) if (truthFps.has(fp)) overlap++;
  const extra = [...ixFpSet].filter(fp => !truthFps.has(fp));

  // fallback (carIdx|sessionNum|~1s) match rate against truth multiset
  const remaining = new Map(truthKeys);
  let fbMatched = 0;
  for (const x of ix.incidents) {
    const k = fallbackKey(x);
    const left = remaining.get(k) || 0;
    if (left > 0) { remaining.set(k, left - 1); fbMatched++; }
  }

  const denom = truthFps.size || 1;
  log('');
  log('--- ' + ix.label + ' vs ' + truth.label + ' ---');
  log('  total=' + ix.incidents.length + ' unique-fingerprints=' + ixFpSet.size);
  log('  fingerprint overlap with truth: ' + overlap + '/' + denom +
      ' (' + ((overlap / denom) * 100).toFixed(1) + '%)');
  log('  extra fingerprints not in truth: ' + extra.length);
  log('  fallback match (carIdx|sessionNum|~1s): ' + fbMatched + '/' + truth.incidents.length +
      ' (' + ((fbMatched / (truth.incidents.length || 1)) * 100).toFixed(1) + '%)');
}
