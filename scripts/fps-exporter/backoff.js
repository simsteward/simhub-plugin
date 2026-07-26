'use strict';

const INITIAL_BACKOFF_MS = 5000;
const MAX_BACKOFF_MS = 60000;
const HEALTHY_RUN_RESET_MS = 60000;

function nextBackoffMs(currentBackoffMs) {
  return Math.min(currentBackoffMs * 2, MAX_BACKOFF_MS);
}

function backoffAfterExit(currentBackoffMs, runDurationMs) {
  if (runDurationMs >= HEALTHY_RUN_RESET_MS) {
    return INITIAL_BACKOFF_MS;
  }
  return nextBackoffMs(currentBackoffMs);
}

module.exports = { INITIAL_BACKOFF_MS, MAX_BACKOFF_MS, HEALTHY_RUN_RESET_MS, nextBackoffMs, backoffAfterExit };
