# Cloud-Only Observability Design

**Date:** 2026-05-03  
**Status:** Approved — Phase 1 implemented as Loki + Sentry only. Component 2 (Cloudflare Worker + D1) deferred to Phase 2, gated on a concrete relational-query need that LogQL can't serve well. The plugin currently has no `/session-complete` POST; until Phase 2, session data is queried directly from Loki.  
**Environment:** Dev

## Goal

Eliminate all local docker infrastructure. The plugin writes directly to Grafana Cloud Loki and Sentry.io. Session data is stored in Cloudflare D1 via a Worker. Alerting is rule-based (Grafana + Sentry). No LLM analysis layer.

---

## Architecture

```
SimHub Plugin (C# .NET Framework 4.8)
  │
  ├─ PluginLogger.Flush() every 500ms
  │    ├─→ Grafana Cloud Loki (logs-prod-036.grafana.net)   [existing]
  │    └─→ SentrySdk.AddBreadcrumb() per entry              [new]
  │
  └─ On unhandled exception / plugin error
       └─→ SentrySdk.CaptureException() + breadcrumbs       [new]

Plugin → POST /session-complete
  └─→ Cloudflare Worker (workers.dev or custom domain)
       └─→ Cloudflare D1 (same schema as local SQLite)

Alerting
  ├─→ Grafana alert rules (46 rules, 8 domains — unchanged)
  └─→ Sentry alert rule (error rate threshold, configured in UI)
```

No local processes. No `npm run obs:up`. SimHub runs → data flows to cloud directly.

---

## Component 1: Sentry SDK (C# Plugin)

**NuGet:** `Sentry` ≥ 4.x — targets .NET Framework 4.8, no conflicts with Newtonsoft.Json.

### Init — `SimStewardPlugin.Init()`

```csharp
SentrySdk.Init(o => {
    o.Dsn = Environment.GetEnvironmentVariable("SIMSTEWARD_SENTRY_DSN") ?? "<hardcoded-fallback>";
    o.Release = typeof(SimStewardPlugin).Assembly.GetName().Version?.ToString();
    o.Environment = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_ENV") ?? "local";
    o.IsGlobalModeEnabled = true;   // required for .NET Framework
    o.AutoSessionTracking = false;  // iRacing session ≠ Sentry session
    o.MaxBreadcrumbs = 100;
});
```

### Breadcrumbs — `PluginLogger.Write()`

After enqueuing to `_ring`, add:

```csharp
SentrySdk.AddBreadcrumb(
    message: entry.Message,
    category: entry.Component ?? entry.Domain,
    level: entry.Level == "ERROR" ? BreadcrumbLevel.Error
         : entry.Level == "WARN"  ? BreadcrumbLevel.Warning
         : BreadcrumbLevel.Info,
    data: entry.Fields?.ToDictionary(k => k.Key, v => v.Value?.ToString())
);
```

The plugin ring buffer caps at 10,000; Sentry breadcrumbs cap at 100 — no memory concern.

### Capture points — `SimStewardPlugin.cs`

Three locations:
1. `DataUpdate()` top-level try/catch → `SentrySdk.CaptureException(ex)`
2. `DispatchAction()` error branch (already emits `action_result` with error) → add capture
3. `PluginLogger.WriteError` event handler (disk I/O failures) → subscribe and capture

### Shutdown — `SimStewardPlugin.End()`

```csharp
SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
```

Ensures in-flight envelopes drain before SimHub exits.

---

## Component 2: Cloudflare Worker + D1 (data-api migration)

Replaces the local Flask + SQLite `data-api` container with a Cloudflare Worker. Same HTTP contract — no plugin changes beyond the endpoint URL.

### Endpoints

- `GET /health` → `{ "status": "ok" }`
- `POST /session-complete` → upsert session, drivers, incidents, incident_captures into D1

### Auth

Worker checks `Authorization: Bearer <token>`. Token stored as a Worker secret (`wrangler secret put SIMSTEWARD_API_TOKEN`). Plugin sends via `SIMSTEWARD_DATA_API_TOKEN` env var.

### D1 Schema

Same tables as local SQLite: `sessions`, `drivers`, `incidents`, `incident_captures`. Migrations managed via `wrangler d1 migrations apply`.

### Deployment

TypeScript Worker, single file. Deployed to `data-api.simsteward.workers.dev` (or custom subdomain).

---

## Component 3: Deletions

```
observability/local/log-sentinel/      ← entire directory
observability/local/data-api/          ← entire directory
observability/local/docker-compose.yml
observability/local/                   ← directory becomes empty, remove
```

Scripts to remove from `package.json` and repo:
- `obs:up`, `obs:down`, `obs:wipe` npm scripts
- `run-simhub-local-observability.ps1`

---

## Component 4: Alerting

**Grafana:** 46 existing alert rules across 8 domains remain unchanged. Already provisioned in Grafana Cloud.

**Sentry:** One alert rule added via Sentry UI — fire when plugin error rate exceeds threshold in a 1h window. No code change required.

---

## Environment Variables

Additions to `.env.example` (on top of already-updated Loki vars):

```env
SIMSTEWARD_SENTRY_DSN=               # from Sentry project settings (simhub-plugin project)
SIMSTEWARD_DATA_API_URL=https://data-api.simsteward.workers.dev
SIMSTEWARD_DATA_API_TOKEN=           # Worker Bearer secret
```

---

## Out of Scope

- Migrating historical SQLite data to D1 (start fresh in D1)
- Changing Grafana alert rule logic
- Adding new Sentry projects (existing `simhub-plugin` project used)
- LLM-based log analysis (dropped in favour of rule-based alerting)
