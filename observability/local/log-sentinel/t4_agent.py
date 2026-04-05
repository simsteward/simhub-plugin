"""T4 — GitHub Issue filing agent.

No LLM inference. T4 reads T2 investigation results from Loki and formats them
into GitHub Issues using the gh CLI. Dedup via fingerprint label.

Flow per T4 run:
  1. Pull T2 investigations from Loki since last T4 run
  2. Filter: confidence in ("high", "very_high") AND sentry_worthy=True
  3. For each:
     a. search GitHub for open issue with label fingerprint:{sentry_fingerprint}
     b. if found → add comment with new evidence summary
     c. if not found → create new issue with full body + deep links
  4. Push sentinel_t4_submission to Loki per action taken
"""

import json
import logging
import time
import urllib.parse
from dataclasses import dataclass, field
from datetime import datetime, timezone

from config import Config
from github_client import GitHubClient
from loki_client import LokiClient

logger = logging.getLogger("sentinel.t4")

_HIGH_CONFIDENCE = {"high", "very_high"}


@dataclass
class T4Result:
    processed: int
    created: int
    commented: int
    skipped: int
    duration_ms: int
    submissions: list[dict] = field(default_factory=list)
    error: str | None = None


class T4Agent:
    def __init__(self, loki: LokiClient, github: GitHubClient, config: Config):
        self.loki = loki
        self.github = github
        self.config = config

    def run(self, since_ts_ns: int) -> T4Result:
        start = time.time()
        if not self.github.enabled:
            logger.info("T4: GitHub not configured (GITHUB_REPO not set), skipping")
            return T4Result(processed=0, created=0, commented=0, skipped=0,
                            duration_ms=0, error="github not configured")

        t2_entries = self.loki.get_t2_since(since_ts_ns)
        eligible = [
            e for e in t2_entries
            if e.get("confidence") in _HIGH_CONFIDENCE
            and e.get("sentry_worthy") in (True, "true", "True")
            and e.get("sentry_fingerprint")
        ]

        logger.info("T4: %d T2 entries, %d eligible for GitHub filing", len(t2_entries), len(eligible))

        created = commented = skipped = 0
        submissions = []

        for entry in eligible:
            fingerprint = entry.get("sentry_fingerprint", "")
            existing = self.github.find_open_issue(fingerprint)

            if existing:
                # Add a comment with a short update summary
                comment_body = self._build_comment(entry)
                ok = self.github.add_comment(existing["number"], comment_body)
                action = "commented" if ok else "comment_failed"
                if ok:
                    commented += 1
                else:
                    skipped += 1
                url = existing["url"]
                logger.info("T4: commented on #%d (%s)", existing["number"], fingerprint[:8])
            else:
                title = self._build_title(entry)
                body = self._build_body(entry)
                labels = self._build_labels(entry)
                result = self.github.create_issue(title, body, labels)
                if result:
                    action = "created"
                    created += 1
                    url = result["url"]
                    logger.info("T4: created issue %s (%s)", url, fingerprint[:8])
                else:
                    action = "create_failed"
                    skipped += 1
                    url = ""

            self.loki.push_t4_submission({
                "fingerprint": fingerprint,
                "url": url,
                "action": action,
                "title": self._build_title(entry),
                "t2_investigation_id": entry.get("t2_investigation_id", ""),
                "confidence": entry.get("confidence", ""),
            }, env=self.config.env_label)

            submissions.append({
                "fingerprint": fingerprint,
                "url": url,
                "action": action,
                "title": self._build_title(entry),
            })

        duration_ms = int((time.time() - start) * 1000)
        return T4Result(
            processed=len(eligible),
            created=created,
            commented=commented,
            skipped=skipped,
            duration_ms=duration_ms,
            submissions=submissions,
        )

    # ── Body builders ──────────────────────────────────────────────────────────

    def _build_title(self, e: dict) -> str:
        issue_type = e.get("issue_type", "unknown")
        root = e.get("root_cause", "Unknown issue")[:80]
        return f"[{issue_type}] {root}"

    def _build_labels(self, e: dict) -> list[str]:
        labels = ["sentinel", "sentinel-t4", "auto-filed"]
        issue_type = e.get("issue_type", "unknown")
        if issue_type in ("config", "error-spike", "performance", "unknown"):
            labels.append(issue_type if issue_type != "unknown" else "needs-triage")
        else:
            labels.append("needs-triage")
        conf = e.get("confidence", "")
        if conf in _HIGH_CONFIDENCE:
            labels.append(f"{conf.replace('_', '-')}-confidence")
        fp = e.get("sentry_fingerprint", "")
        if fp:
            labels.append(f"fingerprint:{fp}")
        return labels

    def _build_body(self, e: dict) -> str:
        grafana_url = self._grafana_link(e)
        loki_url = self._loki_link(e)
        logql_block = "\n".join(e.get("logql_queries_used", [])) or "(none recorded)"
        anomaly_ids = ", ".join(f"`{a}`" for a in (e.get("anomaly_ids") or [])) or "(none)"
        source_cycles = ", ".join(f"`{c}`" for c in (e.get("source_cycle_ids") or [])) or "(none)"
        iso_ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        infer_sec = int(e.get("inference_duration_ms", 0)) // 1000
        tps = e.get("tokens_per_sec", 0)
        try:
            tps_fmt = f"{float(tps):.1f}"
        except (TypeError, ValueError):
            tps_fmt = "?"

        return f"""## Summary
{e.get("root_cause", "")}

## Impact
{e.get("impact", "")}

## Correlation
{e.get("correlation", "")}

## Recommended Action
{e.get("recommendation", "")}

---

## Investigation Evidence

| Field | Value |
|---|---|
| Confidence | {e.get("confidence", "")} |
| Issue type | {e.get("issue_type", "")} |
| Evidence packets | {e.get("evidence_packet_count", "")} |
| Anomaly IDs | {anomaly_ids} |
| Inference time | {infer_sec}s |
| Model | {e.get("model", "")} @ {tps_fmt} tok/s |
| Input tokens | {e.get("input_tokens", "")} |
| Output tokens | {e.get("output_tokens", "")} |
| T2 investigation ID | `{e.get("t2_investigation_id", "")}` |
| Source cycles | {source_cycles} |

### LogQL queries used in investigation
```
{logql_block}
```

## Deep Links

- **[Grafana dashboard — time of investigation]({grafana_url})**
- **[Loki Explore — investigation logs]({loki_url})**

---
*Auto-filed by Sentinel T4 · {iso_ts}*
*Fingerprint: `{e.get("sentry_fingerprint", "")}`*"""

    def _build_comment(self, e: dict) -> str:
        iso_ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        grafana_url = self._grafana_link(e)
        return f"""**Sentinel T4 — new evidence ({iso_ts})**

| Field | Value |
|---|---|
| Confidence | {e.get("confidence", "")} |
| T2 investigation ID | `{e.get("t2_investigation_id", "")}` |
| Input tokens | {e.get("input_tokens", "")} |
| Evidence packets | {e.get("evidence_packet_count", "")} |

> {e.get("root_cause", "")[:300]}

[Grafana dashboard at time of investigation]({grafana_url})"""

    def _grafana_link(self, e: dict) -> str:
        base = self.config.grafana_url.rstrip("/")
        # Use timestamp from entry if available, else now
        try:
            ts_ms = int(float(e.get("timestamp", time.time())) * 1000)
        except (TypeError, ValueError):
            ts_ms = int(time.time() * 1000)
        lookback_ms = self.config.lookback_sec * 1000
        from_ms = ts_ms - lookback_ms - 300_000
        to_ms = ts_ms + 300_000
        return f"{base}/d/simsteward-log-sentinel/simsteward-sentinel?from={from_ms}&to={to_ms}&orgId=1"

    def _loki_link(self, e: dict) -> str:
        base = self.config.grafana_url.rstrip("/")
        queries = e.get("logql_queries_used", [])
        expr = queries[0] if queries else '{app="sim-steward"}'
        try:
            ts_ms = int(float(e.get("timestamp", time.time())) * 1000)
        except (TypeError, ValueError):
            ts_ms = int(time.time() * 1000)
        lookback_ms = self.config.lookback_sec * 1000
        from_ms = ts_ms - lookback_ms - 300_000
        to_ms = ts_ms + 300_000
        query_obj = {
            "datasource": "loki_local",
            "queries": [{"expr": expr, "refId": "A"}],
            "range": {"from": str(from_ms), "to": str(to_ms)},
        }
        encoded = urllib.parse.quote(json.dumps(query_obj), safe="")
        return f"{base}/explore?orgId=1&left={encoded}"
