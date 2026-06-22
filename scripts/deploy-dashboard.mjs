#!/usr/bin/env node
/**
 * Deploy a Grafana dashboard JSON to Grafana Cloud (upsert by uid).
 *
 * The live Cloud dashboards are NOT provisioned from this repo (that path only drives the
 * local Docker Grafana). This pushes the repo JSON to Cloud so the two never drift — re-run
 * after any dashboard edit.
 *
 * Usage:
 *   node scripts/deploy-dashboard.mjs                 # deploy the Claude Code dashboard
 *   node scripts/deploy-dashboard.mjs --file <path>   # deploy a specific dashboard JSON
 *   node scripts/deploy-dashboard.mjs --dry-run       # show what would be sent, push nothing
 *   npm run dash:deploy                               # same, with .env loaded via dotenv-cli
 *
 * Env (.env):
 *   GRAFANA_URL           e.g. https://simsteward.grafana.net
 *   GRAFANA_DEPLOY_TOKEN  Grafana service-account token with EDITOR scope (glsa_...).
 *                         The read-only GRAFANA_TOKEN (DataSourceReader) cannot write dashboards.
 *   GRAFANA_DASHBOARD_FOLDER_UID  (optional) target folder; omit to keep the dashboard's current folder.
 */

import fs from "node:fs";
import path from "node:path";
import os from "node:os";

const DEFAULT_DASHBOARD =
  "observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json";

// --- .env loading (so the script works standalone; npm run also loads via dotenv-cli) ---
function loadEnv() {
  const candidates = [
    path.join(process.cwd(), ".env"),
    path.join(os.homedir(), "dev", "sim-steward", "simhub-plugin", ".env"),
  ];
  for (const f of candidates) {
    try {
      for (const line of fs.readFileSync(f, "utf8").split(/\r?\n/)) {
        const t = line.replace(/#.*$/, "").trim();
        if (!t || !t.includes("=")) continue;
        const eq = t.indexOf("=");
        const k = t.slice(0, eq).trim();
        const v = t.slice(eq + 1).trim().replace(/^["']|["']$/g, "");
        if (k && !(k in process.env)) process.env[k] = v;
      }
      break;
    } catch { /* try next */ }
  }
}
loadEnv();

function parseArgs(argv) {
  const out = { file: DEFAULT_DASHBOARD, dryRun: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--help" || a === "-h") { console.log("See header comment for usage."); process.exit(0); }
    else if (a === "--dry-run") out.dryRun = true;
    else if (a === "--file" && argv[i + 1]) out.file = argv[++i];
    else { console.error(`Unknown argument: ${a}`); process.exit(1); }
  }
  return out;
}

async function main() {
  const { file, dryRun } = parseArgs(process.argv.slice(2));

  const base = (process.env.GRAFANA_URL || process.env.SIMSTEWARD_GRAFANA_BASE_URL || "").trim().replace(/\/$/, "");
  // Prefer an explicit deploy token; fall back to an elevated token if one is configured.
  // GRAFANA_API_TOKEN is DataSourceReader (read-only) — listed last so the 403 hint can guide.
  const TOKEN_SOURCES = ["GRAFANA_DEPLOY_TOKEN", "CURSOR_ELEVATED_GRAFANA_TOKEN", "GRAFANA_API_TOKEN"];
  const tokenKey = TOKEN_SOURCES.find((k) => (process.env[k] || "").trim());
  const token = tokenKey ? process.env[tokenKey].trim() : "";
  const folderUid = (process.env.GRAFANA_DASHBOARD_FOLDER_UID || "").trim();

  let dashboard;
  try { dashboard = JSON.parse(fs.readFileSync(file, "utf8")); }
  catch (e) { console.error(`Cannot read/parse dashboard JSON at ${file}: ${e.message}`); process.exit(1); }

  // Upsert semantics: match by uid, drop any numeric id so Cloud doesn't reject a cross-instance id.
  delete dashboard.id;

  const body = JSON.stringify({
    dashboard,
    overwrite: true,
    message: `repo deploy: ${path.basename(file)}`,
    ...(folderUid ? { folderUid } : {}),
  });

  console.log(`Dashboard : ${dashboard.uid} — "${dashboard.title}" (${dashboard.panels?.length ?? 0} panels)`);
  console.log(`Target    : ${base || "(GRAFANA_URL / SIMSTEWARD_GRAFANA_BASE_URL unset!)"}/api/dashboards/db`);
  console.log(`Auth      : ${tokenKey || "(no token found)"}`);
  console.log(`Mode      : ${dryRun ? "DRY RUN (push nothing)" : "PUSH"}\n`);

  if (dryRun) { console.log("Dry run complete. Re-run without --dry-run to deploy."); return; }

  if (!base) { console.error("Missing GRAFANA_URL."); process.exit(1); }
  if (!token) { console.error("Missing GRAFANA_DEPLOY_TOKEN (needs a service-account token with Editor scope)."); process.exit(1); }

  const res = await fetch(`${base}/api/dashboards/db`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body,
  });
  const text = await res.text();
  if (!res.ok) {
    console.error(`Grafana HTTP ${res.status}: ${text.slice(0, 500)}`);
    if (res.status === 401 || res.status === 403)
      console.error("\nHint: 401/403 = token lacks dashboard write. Create a service account with the Editor role and a token, set it as GRAFANA_DEPLOY_TOKEN.");
    process.exit(1);
  }
  let data; try { data = JSON.parse(text); } catch { data = {}; }
  console.log(`Deployed. version=${data.version ?? "?"}  status=${data.status ?? "ok"}`);
  if (data.url) console.log(`URL: ${base}${data.url}`);
}

main().catch((e) => { console.error(e); process.exit(1); });
