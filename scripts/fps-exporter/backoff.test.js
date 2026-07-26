'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { INITIAL_BACKOFF_MS, MAX_BACKOFF_MS, backoffAfterExit } = require('./backoff.js');

test('backoffAfterExit: doubles backoff after a short-lived run (crash loop)', () => {
  const next = backoffAfterExit(INITIAL_BACKOFF_MS, 1000);
  assert.equal(next, INITIAL_BACKOFF_MS * 2);
});

test('backoffAfterExit: caps backoff at MAX_BACKOFF_MS', () => {
  const next = backoffAfterExit(MAX_BACKOFF_MS, 1000);
  assert.equal(next, MAX_BACKOFF_MS);
});

test('backoffAfterExit: resets to INITIAL_BACKOFF_MS after a healthy long run', () => {
  const next = backoffAfterExit(MAX_BACKOFF_MS, 120000);
  assert.equal(next, INITIAL_BACKOFF_MS);
});
