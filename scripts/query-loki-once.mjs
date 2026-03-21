#!/usr/bin/env node
/**
 * One-off Loki query_range against SIMSTEWARD_LOKI_URL (direct API, Basic auth).
 * Load .env via: npm run loki:query -- [options]   (uses dotenv-cli)
 *
 * Env: LOKI_QUERY_URL (optional override), SIMSTEWARD_LOKI_URL, SIMSTEWARD_LOKI_USER, SIMSTEWARD_LOKI_TOKEN
 */

function usage() {
  console.error(`Usage: node scripts/query-loki-once.mjs [--query LogQL] [--limit N] [--lookback SECONDS]
Defaults: query '{app="sim-steward"}' limit 50 lookback 3600`);
}

function parseArgs(argv) {
  const out = {
    query: '{app="sim-steward"}',
    limit: 50,
    lookbackSeconds: 3600,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--help" || a === "-h") {
      usage();
      process.exit(0);
    }
    if (a === "--query" && argv[i + 1]) {
      out.query = argv[++i];
      continue;
    }
    if (a === "--limit" && argv[i + 1]) {
      out.limit = Math.max(1, parseInt(argv[++i], 10) || 50);
      continue;
    }
    if (a === "--lookback" && argv[i + 1]) {
      out.lookbackSeconds = Math.max(1, parseInt(argv[++i], 10) || 3600);
      continue;
    }
    console.error(`Unknown argument: ${a}`);
    usage();
    process.exit(1);
  }
  return out;
}

function authHeader(user, token) {
  if (!user || !token) return {};
  const b = Buffer.from(`${user}:${token}`, "utf8").toString("base64");
  return { Authorization: `Basic ${b}` };
}

function hintOnStatus(status) {
  if (status === 401 || status === 403) {
    console.error(
      "\nHint: 401/403 often means the Cloud Access Policy token lacks Loki read (query), or user id is wrong. " +
        "For Grafana proxy path instead, set GRAFANA_URL to your stack (https://<slug>.grafana.net), " +
        "GRAFANA_API_TOKEN with datasource query scope, GRAFANA_LOKI_DATASOURCE_UID, and run: npm run obs:poll:grafana"
    );
  }
}

async function main() {
  const { query, limit, lookbackSeconds } = parseArgs(process.argv.slice(2));

  const base = (process.env.LOKI_QUERY_URL || process.env.SIMSTEWARD_LOKI_URL || "").trim().replace(/\/$/, "");
  const user = (process.env.SIMSTEWARD_LOKI_USER || "").trim();
  const token = (process.env.SIMSTEWARD_LOKI_TOKEN || "").trim();

  if (!base) {
    console.error("Missing SIMSTEWARD_LOKI_URL (or LOKI_QUERY_URL).");
    process.exit(1);
  }
  if (!user || !token) {
    console.error("Missing SIMSTEWARD_LOKI_USER or SIMSTEWARD_LOKI_TOKEN (Grafana Cloud requires both for Basic auth).");
    process.exit(1);
  }

  const endNs = BigInt(Date.now()) * 1_000_000n;
  const startNs = endNs - BigInt(lookbackSeconds) * 1_000_000_000n;

  const u = new URL(`${base}/loki/api/v1/query_range`);
  u.searchParams.set("query", query);
  u.searchParams.set("limit", String(limit));
  u.searchParams.set("start", startNs.toString());
  u.searchParams.set("end", endNs.toString());

  const res = await fetch(u, {
    headers: {
      Accept: "application/json",
      ...authHeader(user, token),
    },
  });

  const text = await res.text();
  if (!res.ok) {
    console.error(`Loki HTTP ${res.status}: ${text.slice(0, 500)}`);
    hintOnStatus(res.status);
    process.exit(1);
  }

  let data;
  try {
    data = JSON.parse(text);
  } catch {
    console.error("Invalid JSON from Loki");
    process.exit(1);
  }

  const results = data?.data?.result;
  if (!Array.isArray(results) || results.length === 0) {
    console.log(JSON.stringify({ streams: 0, message: "no streams (check time range and LogQL)" }, null, 2));
    return;
  }

  const lines = [];
  for (const stream of results) {
    const vals = stream.values || [];
    for (const [ts, line] of vals) {
      lines.push({ ts, line });
    }
  }
  lines.sort((a, b) => BigInt(a.ts) < BigInt(b.ts) ? -1 : 1);

  for (const { ts, line } of lines) {
    const sec = Number(BigInt(ts) / 1_000_000_000n);
    const t = new Date(sec * 1000).toISOString();
    console.log(`${t} ${line}`);
  }
  console.error(`-- ${lines.length} line(s), lookback ${lookbackSeconds}s --`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
