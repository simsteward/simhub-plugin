# Scaling log collection to many users

The **local** setup (file-tail + Alloy + Loki + Grafana in Docker) is ideal for one developer or one machine. It keeps the plugin free of network I/O and lets you run the full stack on your PC. It does **not** scale to many users: you don’t want 120 people each running Docker + Alloy + Loki + Grafana.

This doc outlines how to scalably capture logs from many SimSteward users (e.g. 120) without a heavy process on each machine.

## Current pipeline (local, single-user)

- Plugin writes **only to disk**: `plugin-structured.jsonl` (NDJSON) in the plugin data dir.
- **Alloy** (in Docker) tails that file and pushes to **Loki** (and optionally Grafana).
- No HTTP or network I/O inside the plugin; Alloy handles batching, retries, backpressure.

For 120 users, we need a **central** Loki (or Grafana Cloud) and a **lightweight** way to get each user’s logs there.

---

## Option A: Lightweight forwarder per user (recommended)

**Idea:** Each user runs a **small agent** that only tails `plugin-structured.jsonl` and pushes to a **central** Loki. No Loki or Grafana on the user’s machine.

| Component        | Where it runs | Role |
|-----------------|----------------|------|
| SimSteward plugin | Each user’s PC | Writes to `plugin-structured.jsonl` (unchanged). |
| Lightweight agent | Each user’s PC | Tails that file, batches, pushes to central Loki. |
| Loki + Grafana   | **One central** server or Grafana Cloud | Ingestion, storage, dashboards. |

**Agent choices (lightweight, not “heavy”):**

1. **Grafana Alloy (single binary)**  
   - No Docker required. User runs e.g. `alloy run config.alloy`.  
   - Config: file source for `plugin-structured.jsonl` → Loki remote write to your central URL.  
   - One process, low memory; no local Loki/Grafana.

2. **Vector** (or Fluent Bit)  
   - Same idea: `file` source → `loki` sink to central endpoint.  
   - Single binary, small footprint.

3. **Grafana Cloud “Alloy” / “Logs” agent**  
   - Grafana Cloud’s installer gives a preconfigured Alloy that sends to your Cloud Loki.  
   - User installs the agent, points it at their plugin data dir (or a copy of `plugin-structured.jsonl`); no local stack.

**Scaling:** 120 users = 120 lightweight agents (file tail + HTTP push) + 1 central Loki. The “heavy” part (Loki, Grafana, retention, queries) runs once in the center; user machines only run the plugin + a small forwarder.

**Operationally:** You ship an installer or script that (1) installs/updates the plugin, (2) installs and configures the agent to read from the SimSteward data dir and push to your central Loki URL (with auth token). No Docker needed on user PCs.

---

## Option B: Plugin pushes to central Loki (optional code path)

**Idea:** Add an **optional** HTTP push from the plugin to a **central** Loki URL (e.g. Grafana Cloud or your own Loki gateway). When `SIMSTEWARD_LOKI_URL` is set to that URL, the plugin batches and pushes in a **background thread** (so `DataUpdate()` still does no network I/O).

- **Pros:** No agent to install; just configure env/URL and token.  
- **Cons:** Plugin again does network I/O (in a worker thread), and you must handle backpressure, retries, and auth in the plugin. We previously moved this to Alloy to keep the plugin simple and avoid any push logic in-process.

So this is a trade-off: simpler deployment (no agent) vs. re-introducing push and failure handling in the plugin. If you choose this, the push path should be **strictly optional** and only used when a central URL is configured; local file-tail remains the default.

---

## Option C: Hybrid (file + optional push)

- Plugin **always** writes to `plugin-structured.jsonl` (current behavior).
- **If** `SIMSTEWARD_LOKI_URL` is set to a **central** URL, an optional background push (or a tiny bundled forwarder) sends the same lines to central Loki.
- Local-only users keep using file-tail + Alloy with no code change.

This gives flexibility but adds two code paths (file-only vs. file + push) or two deployment modes (agent vs. built-in push).

---

## Recommendation

- **Default (local/single-user):** Keep the current design: plugin → `plugin-structured.jsonl` → Alloy (or your local tailer) → Loki. No change.
- **Scalable (many users, e.g. 120):**
  - Run **one central** Loki (or Grafana Cloud).
  - On each user machine run a **lightweight forwarder** (Alloy binary or Vector) that:
    - Tails `plugin-structured.jsonl` from the SimSteward data dir.
    - Pushes to the central Loki endpoint (with auth).
  - No Docker, no full stack on user PCs; the “heavy” process is only in the center.

If you want to avoid installing an agent at all, Option B (optional plugin push to central URL in a background thread) is the alternative; then we’d design that path so it stays off the hot path and doesn’t block the plugin.

---

## Central Loki / Grafana Cloud

- **Grafana Cloud (Logs):** You get a Loki URL + token. Each user’s agent (or plugin push) sends to that URL. No self-hosted Loki.
- **Self-hosted Loki:** Run Loki (and optionally Grafana) on a single server or k8s; expose a push URL (with a gateway and token). All agents (or plugins) push to that URL.

In both cases, use a **single** Loki (or Cloud tenant) and many senders; scale the central side (retention, ingestion limits) according to 120 × expected log volume.

---

## Hundreds of drivers per session

Session-end data scales to hundreds of drivers by emitting **session_end_datapoints_session** (metadata once) and **session_end_datapoints_results** (one log line per chunk of 35 drivers). Stream count and labels are unchanged; only the number of log lines per session grows. Query patterns and how to merge chunks in Grafana are in **docs/GRAFANA-LOGGING.md** (§ LogQL reference, § Chunked session results).
