'use strict';

const WINDOW_MS = 5000;

class MetricsAggregator {
  constructor(allowlist) {
    this.allowlist = new Set(allowlist);
    this.presents = new Map();
    this.displayed = new Map();
    this.dropped = new Map();
    for (const proc of allowlist) {
      this.presents.set(proc, []);
      this.displayed.set(proc, []);
      this.dropped.set(proc, 0);
    }
  }

  recordRow(row, now) {
    const proc = row.Application;
    if (!this.allowlist.has(proc)) return;

    const msBetweenPresents = parseFloat(row.MsBetweenPresents);
    if (Number.isFinite(msBetweenPresents)) {
      this.presents.get(proc).push({ ts: now, ms: msBetweenPresents });
    }

    if (row.DisplayedTime === 'NA') {
      this.dropped.set(proc, this.dropped.get(proc) + 1);
    } else {
      const msBetweenDisplayChange = parseFloat(row.MsBetweenDisplayChange);
      if (Number.isFinite(msBetweenDisplayChange)) {
        this.displayed.get(proc).push({ ts: now, ms: msBetweenDisplayChange });
      }
    }

    this._prune(proc, now);
  }

  _prune(proc, now) {
    const cutoff = now - WINDOW_MS;
    this.presents.set(proc, this.presents.get(proc).filter((e) => e.ts >= cutoff));
    this.displayed.set(proc, this.displayed.get(proc).filter((e) => e.ts >= cutoff));
  }

  _avgFps(entries) {
    if (entries.length === 0) return null;
    const avgMs = entries.reduce((sum, e) => sum + e.ms, 0) / entries.length;
    if (avgMs <= 0) return null;
    return 1000 / avgMs;
  }

  renderPrometheusText(now) {
    const gameFpsLines = [];
    const displayFpsLines = [];
    const droppedLines = [];

    for (const proc of this.allowlist) {
      this._prune(proc, now);

      const gameFps = this._avgFps(this.presents.get(proc));
      if (gameFps !== null) {
        gameFpsLines.push(`game_fps{process="${proc}"} ${gameFps.toFixed(2)}`);
      }

      const displayFps = this._avgFps(this.displayed.get(proc));
      if (displayFps !== null) {
        displayFpsLines.push(`display_fps{process="${proc}"} ${displayFps.toFixed(2)}`);
      }

      droppedLines.push(`frames_dropped_total{process="${proc}"} ${this.dropped.get(proc)}`);
    }

    const lines = [];
    if (gameFpsLines.length > 0) lines.push('# TYPE game_fps gauge', ...gameFpsLines);
    if (displayFpsLines.length > 0) lines.push('# TYPE display_fps gauge', ...displayFpsLines);
    lines.push('# TYPE frames_dropped_total counter', ...droppedLines);

    return lines.join('\n') + '\n';
  }
}

module.exports = { MetricsAggregator, WINDOW_MS };
