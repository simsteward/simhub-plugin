'use strict';

const WINDOW_MS = 5000;
const DEFAULT_OUTPUT = '0';

function seriesKey(process, output) {
  return process + '|' + output;
}

class MetricsAggregator {
  constructor(allowlist, connectorMap) {
    this.allowlist = new Set(allowlist);
    this.connectorMap = connectorMap || {};
    this.presents = new Map();
    this.displayed = new Map();
    this.dropped = new Map();
    this.seen = new Map(); // seriesKey -> { process, output }
  }

  _ensure(key, process, output) {
    if (!this.seen.has(key)) {
      this.seen.set(key, { process, output });
      this.presents.set(key, []);
      this.displayed.set(key, []);
      this.dropped.set(key, 0);
    }
  }

  recordRow(row, now) {
    const process = row.Application;
    if (!this.allowlist.has(process)) return;

    const output = row.VidPnSourceId !== undefined && row.VidPnSourceId !== '' && row.VidPnSourceId !== 'NA'
      ? row.VidPnSourceId
      : DEFAULT_OUTPUT;
    const key = seriesKey(process, output);
    this._ensure(key, process, output);

    const msBetweenPresents = parseFloat(row.MsBetweenPresents);
    if (Number.isFinite(msBetweenPresents)) {
      this.presents.get(key).push({ ts: now, ms: msBetweenPresents });
    }

    if (row.DisplayedTime === 'NA') {
      this.dropped.set(key, this.dropped.get(key) + 1);
    } else {
      const msBetweenDisplayChange = parseFloat(row.MsBetweenDisplayChange);
      if (Number.isFinite(msBetweenDisplayChange)) {
        this.displayed.get(key).push({ ts: now, ms: msBetweenDisplayChange });
      }
    }

    this._prune(key, now);
  }

  _prune(key, now) {
    const cutoff = now - WINDOW_MS;
    this.presents.set(key, this.presents.get(key).filter((e) => e.ts >= cutoff));
    this.displayed.set(key, this.displayed.get(key).filter((e) => e.ts >= cutoff));
  }

  _avgFps(entries) {
    if (entries.length === 0) return null;
    const avgMs = entries.reduce((sum, e) => sum + e.ms, 0) / entries.length;
    if (avgMs <= 0) return null;
    return 1000 / avgMs;
  }

  _labels(process, output) {
    const connector = this.connectorMap[output];
    return connector
      ? `process="${process}",output="${output}",connector="${connector}"`
      : `process="${process}",output="${output}"`;
  }

  renderPrometheusText(now) {
    const gameFpsLines = [];
    const displayFpsLines = [];
    const droppedLines = [];

    for (const [key, { process, output }] of this.seen) {
      this._prune(key, now);
      const labels = this._labels(process, output);

      const gameFps = this._avgFps(this.presents.get(key));
      if (gameFps !== null) {
        gameFpsLines.push(`game_fps{${labels}} ${gameFps.toFixed(2)}`);
      }

      const displayFps = this._avgFps(this.displayed.get(key));
      if (displayFps !== null) {
        displayFpsLines.push(`display_fps{${labels}} ${displayFps.toFixed(2)}`);
      }

      droppedLines.push(`frames_dropped_total{${labels}} ${this.dropped.get(key)}`);
    }

    const lines = [];
    if (gameFpsLines.length > 0) lines.push('# TYPE game_fps gauge', ...gameFpsLines);
    if (displayFpsLines.length > 0) lines.push('# TYPE display_fps gauge', ...displayFpsLines);
    if (droppedLines.length > 0) lines.push('# TYPE frames_dropped_total counter', ...droppedLines);

    return lines.join('\n') + '\n';
  }
}

module.exports = { MetricsAggregator, WINDOW_MS };
