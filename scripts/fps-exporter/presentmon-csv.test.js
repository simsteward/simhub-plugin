'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseHeader, parseRow } = require('./presentmon-csv.js');

test('parseHeader: splits and trims column names', () => {
  const cols = parseHeader('Application,ProcessID,MsBetweenPresents,MsBetweenDisplayChange,DisplayedTime');
  assert.deepEqual(cols, ['Application', 'ProcessID', 'MsBetweenPresents', 'MsBetweenDisplayChange', 'DisplayedTime']);
});

test('parseRow: zips values with header into an object', () => {
  const cols = ['Application', 'ProcessID', 'MsBetweenPresents', 'MsBetweenDisplayChange', 'DisplayedTime'];
  const row = parseRow(cols, 'iRacingSim64DX11.exe,12345,16.683,16.683,16.683');
  assert.deepEqual(row, {
    Application: 'iRacingSim64DX11.exe',
    ProcessID: '12345',
    MsBetweenPresents: '16.683',
    MsBetweenDisplayChange: '16.683',
    DisplayedTime: '16.683',
  });
});

test('parseRow: preserves NA for dropped frames', () => {
  const cols = ['Application', 'DisplayedTime'];
  const row = parseRow(cols, 'chrome.exe,NA');
  assert.equal(row.DisplayedTime, 'NA');
});

test('parseRow: throws on column/value count mismatch', () => {
  const cols = ['Application', 'ProcessID'];
  assert.throws(() => parseRow(cols, 'chrome.exe'), /column count mismatch/);
});
