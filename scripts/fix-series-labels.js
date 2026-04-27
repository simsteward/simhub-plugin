#!/usr/bin/env node
'use strict';
const fs = require('fs');

// For stat panels where only one series is visible (the last math expr),
// add a displayName override by refId so Grafana shows a real label instead of "B", "C", "Value #A"
const path = 'observability/local/grafana/provisioning/dashboards/claude-subscription-economics.json';
const dash = JSON.parse(fs.readFileSync(path, 'utf8'));

// Map of panel id → { refId: displayName } to add as overrides
const nameMap = {
  1:  { A: 'API Retail Cost' },           // True Cost
  2:  { B: 'Plan Cost (Prorated)' },      // Your Plan Cost
  3:  { C: 'VC Subsidy' },               // VC Subsidy
  4:  { C: 'Subsidy Multiplier' },        // Multiplier
  5:  { C: 'Cents per $1 API' },         // You Pay
  6:  { C: 'Annualized Subsidy' },        // Annualized
  30: { C: 'Plan Value %' },             // Gauge
  31: { C: 'Weekly % of Cap' },          // Weekly cap
  32: { A: 'Avg / Turn' },              // Avg cost/turn
  33: { C: 'Sessions' },                // Sessions
  40: { C: 'Remaining Value' },          // Remaining Plan Value
  41: { A: 'Projected Monthly' },        // Projected
  42: { C: 'Days Until Break-even' },   // Days
  44: { C: 'Weekly Gap' },              // Weekly gap (new)
  45: { C: 'Monthly Gap' },             // Monthly gap (new)
  46: { C: 'Unclaimed Budget' },        // Unclaimed (new)
};

let fixed = 0;
for (const panel of dash.panels) {
  const names = nameMap[panel.id];
  if (!names) continue;
  for (const [refId, displayName] of Object.entries(names)) {
    // Check if override already exists
    const existing = panel.fieldConfig?.overrides?.find(
      o => o.matcher?.id === 'byFrameRefID' && o.matcher?.options === refId
    );
    if (existing) {
      // Add displayName property if not already there
      if (!existing.properties.find(p => p.id === 'displayName')) {
        existing.properties.push({ id: 'displayName', value: displayName });
        fixed++;
      }
    } else {
      // Add new override
      if (!panel.fieldConfig) panel.fieldConfig = { defaults: {}, overrides: [] };
      if (!panel.fieldConfig.overrides) panel.fieldConfig.overrides = [];
      panel.fieldConfig.overrides.push({
        matcher: { id: 'byFrameRefID', options: refId },
        properties: [{ id: 'displayName', value: displayName }]
      });
      fixed++;
    }
  }
}

fs.writeFileSync(path, JSON.stringify(dash, null, 2));
JSON.parse(fs.readFileSync(path, 'utf8'));
console.log(`Fixed ${fixed} series label overrides. Total panels: ${dash.panels.length}`);
