'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { MetricsAggregator } = require('./metrics-aggregator.js');

test('recordRow + renderPrometheusText: computes game_fps from MsBetweenPresents', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  for (let i = 0; i < 10; i++) {
    agg.recordRow({
      Application: 'iRacingSim64DX11.exe',
      MsBetweenPresents: '16.667',
      MsBetweenDisplayChange: '16.667',
      DisplayedTime: '16.667',
    }, 1000 + i * 10);
  }
  const text = agg.renderPrometheusText(1100);
  const match = text.match(/game_fps\{process="iRacingSim64DX11\.exe"\} ([\d.]+)/);
  assert.ok(match, 'game_fps line present');
  assert.ok(Math.abs(parseFloat(match[1]) - 60) < 0.5, `expected ~60 fps, got ${match[1]}`);
});

test('renderPrometheusText: omits game_fps/display_fps for a process with no recent data', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  const text = agg.renderPrometheusText(1000);
  assert.ok(!text.includes('game_fps{process="iRacingSim64DX11.exe"}'));
  assert.ok(!text.includes('display_fps{process="iRacingSim64DX11.exe"}'));
});

test('renderPrometheusText: still emits frames_dropped_total 0 for a process with no drops yet', () => {
  const agg = new MetricsAggregator(['chrome.exe']);
  const text = agg.renderPrometheusText(1000);
  assert.ok(text.includes('frames_dropped_total{process="chrome.exe"} 0'));
});

test('recordRow: DisplayedTime "NA" counts as a dropped frame and is excluded from display_fps', () => {
  const agg = new MetricsAggregator(['chrome.exe']);
  agg.recordRow({ Application: 'chrome.exe', MsBetweenPresents: '10', MsBetweenDisplayChange: '10', DisplayedTime: '10' }, 1000);
  agg.recordRow({ Application: 'chrome.exe', MsBetweenPresents: '10', MsBetweenDisplayChange: 'NA', DisplayedTime: 'NA' }, 1010);
  const text = agg.renderPrometheusText(1020);
  assert.ok(text.includes('frames_dropped_total{process="chrome.exe"} 1'));
});

test('recordRow: ignores processes not in the allowlist', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  agg.recordRow({ Application: 'notepad.exe', MsBetweenPresents: '16.667', MsBetweenDisplayChange: '16.667', DisplayedTime: '16.667' }, 1000);
  const text = agg.renderPrometheusText(1000);
  assert.ok(!text.includes('notepad.exe'));
});

test('recordRow + renderPrometheusText: prunes entries older than the 5s window', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  agg.recordRow({ Application: 'iRacingSim64DX11.exe', MsBetweenPresents: '16.667', MsBetweenDisplayChange: '16.667', DisplayedTime: '16.667' }, 1000);
  const text = agg.renderPrometheusText(7001);
  assert.ok(!text.includes('game_fps{process="iRacingSim64DX11.exe"}'));
});
