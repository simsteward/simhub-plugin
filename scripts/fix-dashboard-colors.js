#!/usr/bin/env node
// Fix color scheme and stat display consistency across all Grafana dashboards
//
// Changes:
// 1. #7c6ff7 → #c77dff (Grafana default purple → standard purple)
// 2. #4cc9f0 → #4a9eff (Grafana default cyan → standard blue)
// 3. colorMode: "thresholds" → "background-gradient" for stat panels
// 4. simsteward-log-sentinel.json stat panels:
//    - graphMode: "area" → "none"
//    - textMode: "value" → "value_and_name"

const fs = require('fs');
const path = require('path');

const dashDir = path.join(__dirname, '../observability/local/grafana/provisioning/dashboards');

function fixDashboard(filename, transforms) {
  const filepath = path.join(dashDir, filename);
  const json = JSON.parse(fs.readFileSync(filepath, 'utf8'));
  const changes = [];

  function walk(obj, parentKey) {
    if (Array.isArray(obj)) {
      obj.forEach((item, i) => walk(item, `[${i}]`));
      return;
    }
    if (obj && typeof obj === 'object') {
      for (const [key, val] of Object.entries(obj)) {
        for (const { match, replace, desc } of transforms) {
          if (key === match.key && val === match.value) {
            obj[key] = replace;
            changes.push(`${desc}: ${JSON.stringify(val)} → ${JSON.stringify(replace)}`);
          }
        }
        walk(val, key);
      }
    }
  }

  walk(json);

  if (changes.length > 0) {
    fs.writeFileSync(filepath, JSON.stringify(json, null, 2), 'utf8');
    console.log(`\n${filename}: ${changes.length} fix(es)`);
    const summary = {};
    for (const c of changes) {
      const key = c.split(':')[0];
      summary[key] = (summary[key] || 0) + 1;
    }
    for (const [k, n] of Object.entries(summary)) {
      console.log(`  ${k}: ${n}×`);
    }
  } else {
    console.log(`${filename}: no changes needed`);
  }

  return changes.length;
}

// Global color fixes — apply to all dashboards
const colorFixes = [
  { match: { key: 'color', value: '#7c6ff7' }, replace: '#c77dff', desc: '#7c6ff7→#c77dff (wrong purple)' },
  { match: { key: 'color', value: '#4cc9f0' }, replace: '#4a9eff', desc: '#4cc9f0→#4a9eff (wrong cyan)' },
];

// Stat display fixes — apply to all dashboards for consistency
const statDisplayFixes = [
  { match: { key: 'colorMode', value: 'thresholds' }, replace: 'background-gradient', desc: 'colorMode thresholds→background-gradient' },
];

// Sentinel-specific fixes: stat panels use area+value, should be none+value_and_name
const sentinelFixes = [
  { match: { key: 'graphMode', value: 'area' }, replace: 'none', desc: 'graphMode area→none' },
  { match: { key: 'textMode', value: 'value' }, replace: 'value_and_name', desc: 'textMode value→value_and_name' },
];

const allFiles = fs.readdirSync(dashDir).filter(f => f.endsWith('.json'));

let total = 0;
for (const file of allFiles) {
  const transforms = [...colorFixes, ...statDisplayFixes];
  if (file === 'simsteward-log-sentinel.json') {
    transforms.push(...sentinelFixes);
  }
  total += fixDashboard(file, transforms);
}

console.log(`\nTotal changes: ${total}`);
