'use strict';

function parseHeader(line) {
  return line.split(',').map((s) => s.trim());
}

function parseRow(headerCols, line) {
  const values = line.split(',');
  if (values.length !== headerCols.length) {
    throw new Error(`column count mismatch: expected ${headerCols.length}, got ${values.length}`);
  }
  const row = {};
  for (let i = 0; i < headerCols.length; i++) {
    row[headerCols[i]] = values[i];
  }
  return row;
}

module.exports = { parseHeader, parseRow };
