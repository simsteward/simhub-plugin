#!/usr/bin/env node
// Fix broken LogQL: | json | __error__="" FIELDS | → | json FIELDS | __error__="" |
// In the JSON file, quotes are escaped as \" so the pattern in raw bytes is:
//   | json | __error__=\"\" FIELDS |

const fs = require('fs');
const path = require('path');

const dashDir = path.join(__dirname, '../observability/local/grafana/provisioning/dashboards');
const targets = [
  'claude-code-overview.json',
  'claude-cache-context.json',
];

let totalFixed = 0;

for (const filename of targets) {
  const filepath = path.join(dashDir, filename);
  const original = fs.readFileSync(filepath, 'utf8');
  let content = original;

  // In the raw file bytes, JSON strings have escaped quotes: \"
  // The broken pattern (raw bytes): | json | __error__=\"\" FIELDLIST |
  // Where \" is literal backslash + double-quote (2 chars each)
  //
  // In JS regex:
  //   \\ matches a literal backslash
  //   "  matches a literal double-quote
  // So \\"  matches \"  (backslash + quote) in the file
  //
  // Pattern: | json | __error__=\"\" FIELDLIST |
  // Regex:   /\| json \| __error__=\\"\\"\s+([\w,\s]+?)\s+\|/g

  const broken = /\| json \| __error__=\\"\\"\s+([\w,\s]+?)\s+\|/g;

  let fixCount = 0;
  content = content.replace(broken, (_match, fields) => {
    fixCount++;
    // Replacement: | json FIELDLIST | __error__=\"\" |
    // In JS template literal: \\" = \ + " = \"
    return `| json ${fields.trim()} | __error__=\\"\\" |`;
  });

  if (fixCount > 0) {
    fs.writeFileSync(filepath, content, 'utf8');
    console.log(`Fixed ${filename}: ${fixCount} occurrences`);
    totalFixed += fixCount;
  } else {
    console.log(`No changes in ${filename} (0 matches — check quoting)`);

    // Debug: show what the __error__ pattern actually looks like in the file
    const idx = original.indexOf('__error__');
    if (idx !== -1) {
      const snippet = original.slice(idx - 10, idx + 30);
      console.log(`  Sample around __error__: ${JSON.stringify(snippet)}`);
    }
  }
}

console.log(`\nTotal fixed: ${totalFixed}`);
