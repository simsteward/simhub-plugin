// Hook timing wrapper — logs wall-clock duration to a shared timing log file.
// Usage: node timed.js <label> <exe> [args...]
// The label should be "<source>/<hook-type>", e.g. "cs/pre-tool-use" or "loki/stop".
// Stdin is buffered and forwarded to the wrapped command unchanged.
// Output: one tab-separated line per hook in %TEMP%/claude-hook-timing/hook-timing.log:
//   <ISO timestamp>  <label>  <duration_ms>ms  [error:<msg>]

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

const TIMING_DIR = path.join(os.tmpdir(), 'claude-hook-timing');
const TIMING_LOG = path.join(TIMING_DIR, 'hook-timing.log');

try { fs.mkdirSync(TIMING_DIR, { recursive: true }); } catch {}

const [,, label, ...cmdAndArgs] = process.argv;
if (!label || cmdAndArgs.length === 0) {
  process.stderr.write(`timed.js: usage: node timed.js <label> <exe> [args...]\n`);
  process.exit(2);
}

const startTs = new Date().toISOString();
const startMs = Date.now();

const stdinChunks = [];
process.stdin.on('data', chunk => stdinChunks.push(chunk));
process.stdin.on('end', () => {
  const stdinData = Buffer.concat(stdinChunks);
  const [exe, ...args] = cmdAndArgs;

  const child = spawn(exe, args, {
    stdio: ['pipe', 'inherit', 'inherit'],
    windowsHide: true,
  });

  // Single-call end() lets Node drain backpressure correctly for large stdin payloads.
  child.stdin.end(stdinData);

  child.on('close', (code, signal) => {
    const durationMs = Date.now() - startMs;
    const suffix = signal ? `\tSIGNAL:${signal}` : '';
    try { fs.appendFileSync(TIMING_LOG, `${startTs}\t${label}\t${durationMs}ms${suffix}\n`); } catch {}
    // Preserve signal kills as a non-zero exit so callers see the failure.
    process.exit(code !== null ? code : (signal ? 1 : 0));
  });

  child.on('error', err => {
    const durationMs = Date.now() - startMs;
    try { fs.appendFileSync(TIMING_LOG, `${startTs}\t${label}\t${durationMs}ms\tERROR:${err.message}\n`); } catch {}
    process.exit(1);
  });
});
