'use strict';

const { spawn } = require('node:child_process');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

const { parseHeader, parseRow } = require('./presentmon-csv.js');
const { MetricsAggregator } = require('./metrics-aggregator.js');
const { INITIAL_BACKOFF_MS, backoffAfterExit } = require('./backoff.js');

const ALLOWLIST = ['iRacingSim64DX11.exe', 'chrome.exe'];
const PRESENTMON_PATH = path.join(os.homedir(), 'Tools', 'PresentMon', 'PresentMon.exe');
const METRICS_PORT = 9101;
const METRICS_HOST = '127.0.0.1';
const LOG_DIR = path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'FpsExporter');
const LOG_FILE = path.join(LOG_DIR, 'fps-exporter.log');

const aggregator = new MetricsAggregator(ALLOWLIST);

function log(message) {
  const line = `${new Date().toISOString()} ${message}\n`;
  process.stdout.write(line);
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    fs.appendFileSync(LOG_FILE, line);
  } catch (err) {
    process.stderr.write(`failed to write log file: ${err.message}\n`);
  }
}

function startPresentMon(backoffMs) {
  log(`starting PresentMon.exe (backoff was ${backoffMs}ms)`);
  const startedAt = Date.now();
  const child = spawn(PRESENTMON_PATH, [
    '--process_name', 'iRacingSim64DX11.exe',
    '--process_name', 'chrome.exe',
    '--output_stdout',
    '--no_csv',
    '--no_console_stats',
  ]);

  let headerCols = null;
  let carry = '';

  child.stdout.on('data', (chunk) => {
    carry += chunk.toString('utf8');
    const lines = carry.split('\n');
    carry = lines.pop();

    for (const rawLine of lines) {
      const line = rawLine.trim();
      if (line.length === 0) continue;

      if (headerCols === null) {
        headerCols = parseHeader(line);
        continue;
      }

      try {
        const row = parseRow(headerCols, line);
        aggregator.recordRow(row, Date.now());
      } catch (err) {
        log(`skipping unparseable row: ${err.message}`);
      }
    }
  });

  child.stderr.on('data', (chunk) => {
    log(`PresentMon stderr: ${chunk.toString('utf8').trim()}`);
  });

  child.on('exit', (code, signal) => {
    const runDurationMs = Date.now() - startedAt;
    log(`PresentMon.exe exited (code=${code}, signal=${signal}, ran for ${runDurationMs}ms)`);
    const nextBackoff = backoffAfterExit(backoffMs, runDurationMs);
    setTimeout(() => startPresentMon(nextBackoff), nextBackoff);
  });

  child.on('error', (err) => {
    log(`failed to spawn PresentMon.exe: ${err.message}`);
  });
}

function startMetricsServer() {
  const server = http.createServer((req, res) => {
    if (req.url === '/metrics') {
      const body = aggregator.renderPrometheusText(Date.now());
      res.writeHead(200, { 'Content-Type': 'text/plain; version=0.0.4' });
      res.end(body);
    } else {
      res.writeHead(404);
      res.end();
    }
  });

  server.on('error', (err) => {
    log(`metrics server failed to start: ${err.message}`);
    process.exit(1);
  });

  server.listen(METRICS_PORT, METRICS_HOST, () => {
    log(`metrics endpoint listening on http://${METRICS_HOST}:${METRICS_PORT}/metrics`);
  });
}

if (!fs.existsSync(PRESENTMON_PATH)) {
  log(`PresentMon.exe not found at ${PRESENTMON_PATH}`);
  process.exit(1);
}

startMetricsServer();
startPresentMon(INITIAL_BACKOFF_MS);
