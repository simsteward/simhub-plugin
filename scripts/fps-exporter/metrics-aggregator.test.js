'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { MetricsAggregator } = require('./metrics-aggregator.js');

test('recordRow + renderPrometheusText: computes game_fps from MsBetweenPresents, labeled by output', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  for (let i = 0; i < 10; i++) {
    agg.recordRow({
      Application: 'iRacingSim64DX11.exe',
      VidPnSourceId: '0',
      MsBetweenPresents: '16.667',
      MsBetweenDisplayChange: '16.667',
      DisplayedTime: '16.667',
    }, 1000 + i * 10);
  }
  const text = agg.renderPrometheusText(1100);
  const match = text.match(/game_fps\{process="iRacingSim64DX11\.exe",output="0"\} ([\d.]+)/);
  assert.ok(match, 'game_fps line present with output label');
  assert.ok(Math.abs(parseFloat(match[1]) - 60) < 0.5, `expected ~60 fps, got ${match[1]}`);
});

test('recordRow: missing VidPnSourceId falls back to output "0"', () => {
  const agg = new MetricsAggregator(['chrome.exe']);
  agg.recordRow({ Application: 'chrome.exe', MsBetweenPresents: '16.667', MsBetweenDisplayChange: '16.667', DisplayedTime: '16.667' }, 1000);
  const text = agg.renderPrometheusText(1000);
  assert.ok(text.includes('frames_dropped_total{process="chrome.exe",output="0"} 0'));
});

test('recordRow: two outputs for the same process produce two distinct series', () => {
  const agg = new MetricsAggregator(['chrome.exe']);
  for (let i = 0; i < 5; i++) {
    agg.recordRow({ Application: 'chrome.exe', VidPnSourceId: '0', MsBetweenPresents: '10', MsBetweenDisplayChange: '10', DisplayedTime: '10' }, 1000 + i * 10);
    agg.recordRow({ Application: 'chrome.exe', VidPnSourceId: '1', MsBetweenPresents: '20', MsBetweenDisplayChange: '20', DisplayedTime: '20' }, 1000 + i * 10);
  }
  const text = agg.renderPrometheusText(1100);
  assert.ok(text.includes('game_fps{process="chrome.exe",output="0"} 100.00'));
  assert.ok(text.includes('game_fps{process="chrome.exe",output="1"} 50.00'));
});

test('renderPrometheusText: omits game_fps/display_fps for a process/output with no recent data', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  const text = agg.renderPrometheusText(1000);
  assert.ok(!text.includes('game_fps{'));
  assert.ok(!text.includes('display_fps{'));
});

test('renderPrometheusText: frames_dropped_total is not fabricated for a process/output never seen', () => {
  const agg = new MetricsAggregator(['chrome.exe']);
  const text = agg.renderPrometheusText(1000);
  assert.ok(!text.includes('frames_dropped_total{'), 'no series should exist until at least one row is seen for that (process, output)');
});

test('recordRow: DisplayedTime "NA" counts as a dropped frame and is excluded from display_fps', () => {
  const agg = new MetricsAggregator(['chrome.exe']);
  agg.recordRow({ Application: 'chrome.exe', VidPnSourceId: '0', MsBetweenPresents: '10', MsBetweenDisplayChange: '10', DisplayedTime: '10' }, 1000);
  agg.recordRow({ Application: 'chrome.exe', VidPnSourceId: '0', MsBetweenPresents: '10', MsBetweenDisplayChange: 'NA', DisplayedTime: 'NA' }, 1010);
  const text = agg.renderPrometheusText(1020);
  assert.ok(text.includes('frames_dropped_total{process="chrome.exe",output="0"} 1'));
});

test('recordRow: ignores processes not in the allowlist', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  agg.recordRow({ Application: 'notepad.exe', VidPnSourceId: '0', MsBetweenPresents: '16.667', MsBetweenDisplayChange: '16.667', DisplayedTime: '16.667' }, 1000);
  const text = agg.renderPrometheusText(1000);
  assert.ok(!text.includes('notepad.exe'));
});

test('recordRow + renderPrometheusText: prunes entries older than the 5s window', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  agg.recordRow({ Application: 'iRacingSim64DX11.exe', VidPnSourceId: '0', MsBetweenPresents: '16.667', MsBetweenDisplayChange: '16.667', DisplayedTime: '16.667' }, 1000);
  const text = agg.renderPrometheusText(7001);
  assert.ok(!text.includes('game_fps{'));
});

test('connector map: attaches a connector label when the output ID is known', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe'], { '0': 'DisplayPort', '1': 'HDMI' });
  agg.recordRow({ Application: 'iRacingSim64DX11.exe', VidPnSourceId: '1', MsBetweenPresents: '10', MsBetweenDisplayChange: '10', DisplayedTime: '10' }, 1000);
  const text = agg.renderPrometheusText(1000);
  assert.ok(text.includes('frames_dropped_total{process="iRacingSim64DX11.exe",output="1",connector="HDMI"} 0'));
});

test('connector map: omits the connector label when the output ID is unknown to the map', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe'], { '0': 'DisplayPort' });
  agg.recordRow({ Application: 'iRacingSim64DX11.exe', VidPnSourceId: '7', MsBetweenPresents: '10', MsBetweenDisplayChange: '10', DisplayedTime: '10' }, 1000);
  const text = agg.renderPrometheusText(1000);
  assert.ok(text.includes('frames_dropped_total{process="iRacingSim64DX11.exe",output="7"} 0'));
  assert.ok(!text.includes('connector='));
});

test('connector map: absent entirely means no connector label anywhere', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  agg.recordRow({ Application: 'iRacingSim64DX11.exe', VidPnSourceId: '0', MsBetweenPresents: '10', MsBetweenDisplayChange: '10', DisplayedTime: '10' }, 1000);
  const text = agg.renderPrometheusText(1000);
  assert.ok(!text.includes('connector='));
});
