"""Loki HTTP API client — Python port of LokiQueryClient.cs + LokiPushClient.cs patterns."""

import json
import logging
import time
from datetime import datetime, timezone
from urllib.parse import quote

import requests

logger = logging.getLogger("sentinel.loki")


class LokiClient:
    def __init__(self, base_url: str, timeout: int = 5):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    # ── Time helpers (match LokiQueryClient.cs) ──

    @staticmethod
    def now_ns() -> int:
        return int(datetime.now(timezone.utc).timestamp() * 1e9)

    @staticmethod
    def now_minus_ms(offset_ms: int) -> int:
        return int((datetime.now(timezone.utc).timestamp() * 1000 - offset_ms) * 1e6)

    # ── Query API ──

    def count(self, logql: str, start_ns: int, end_ns: int) -> int:
        """Count matching log lines. Returns -1 on error (matches C# pattern)."""
        try:
            resp = requests.get(
                f"{self.base_url}/loki/api/v1/query_range",
                params={
                    "query": logql,
                    "start": str(start_ns),
                    "end": str(end_ns),
                    "limit": 1000,
                    "direction": "forward",
                },
                timeout=self.timeout,
            )
            if resp.status_code != 200:
                logger.warning("Loki query failed: %d %s", resp.status_code, resp.text[:200])
                return -1
            data = resp.json()
            total = 0
            for stream in data.get("data", {}).get("result", []):
                total += len(stream.get("values", []))
            return total
        except Exception as e:
            logger.warning("Loki count error: %s", e)
            return -1

    def query_lines(self, logql: str, start_ns: int, end_ns: int, limit: int = 1000) -> list[dict]:
        """Query Loki and return parsed JSON log objects. Returns empty list on error."""
        try:
            resp = requests.get(
                f"{self.base_url}/loki/api/v1/query_range",
                params={
                    "query": logql,
                    "start": str(start_ns),
                    "end": str(end_ns),
                    "limit": limit,
                    "direction": "forward",
                },
                timeout=self.timeout,
            )
            if resp.status_code != 200:
                return []
            data = resp.json()
            lines = []
            for stream in data.get("data", {}).get("result", []):
                for pair in stream.get("values", []):
                    if len(pair) >= 2:
                        try:
                            lines.append(json.loads(pair[1]))
                        except (json.JSONDecodeError, TypeError):
                            pass
            return lines
        except Exception:
            return []

    # ── Push API (port of LokiPushClient.cs) ──

    def push(self, entry: dict, env: str = "local"):
        """Push a single log entry to Loki. Fire-and-forget, never throws."""
        try:
            ts_ns = str(int(time.time() * 1e9))
            stream_labels = {
                "app": "sim-steward",
                "env": env,
                "level": entry.get("level", "INFO"),
            }
            for key in ("component", "event", "domain"):
                val = entry.get(key)
                if val:
                    stream_labels[key] = val

            payload = {
                "streams": [
                    {
                        "stream": stream_labels,
                        "values": [[ts_ns, json.dumps(entry)]],
                    }
                ]
            }
            requests.post(
                f"{self.base_url}/loki/api/v1/push",
                json=payload,
                timeout=3,
            )
        except Exception as e:
            logger.debug("Loki push error (fire-and-forget): %s", e)

    # ── Sentinel-specific push helpers ──

    def push_finding(self, finding, env: str = "local"):
        """Push a Finding as a structured log entry to Loki."""
        entry = {
            "level": "WARN" if finding.severity in ("warn", "critical") else "INFO",
            "message": finding.title,
            "timestamp": finding.timestamp,
            "component": "log-sentinel",
            "event": "sentinel_finding",
            "domain": "system",
            # Flat top-level fields — required for LogQL `| json severity` extraction
            "finding_id": finding.finding_id,
            "detector": finding.detector,
            "severity": finding.severity,
            "title": finding.title,
            "summary": finding.summary,
            "escalated_to_t2": finding.escalate_to_t2,
            "logql_query": finding.logql_query,
            "flow_context": finding.flow_context,
            **finding.evidence,
        }
        self.push(entry, env)

    def push_investigation(self, investigation, env: str = "local"):
        """Push an Investigation report as a structured log entry to Loki."""
        entry = {
            "level": "INFO",
            "message": f"Investigation: {investigation.root_cause[:120]}",
            "timestamp": investigation.timestamp,
            "component": "log-sentinel",
            "event": "sentinel_investigation",
            "domain": "system",
            "fields": {
                "investigation_id": investigation.investigation_id,
                "finding_id": investigation.finding.finding_id,
                "detector": investigation.finding.detector,
                "model": investigation.model,
                "confidence": investigation.confidence,
                "root_cause": investigation.root_cause,
                "correlation": investigation.correlation,
                "impact": investigation.impact,
                "recommendation": investigation.recommendation,
                "inference_duration_ms": investigation.inference_duration_ms,
                "context_lines_gathered": investigation.context_lines_gathered,
            },
        }
        self.push(entry, env)
