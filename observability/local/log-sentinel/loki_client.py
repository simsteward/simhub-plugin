"""Loki HTTP API client — query + push, with structured sentinel event helpers."""

import json
import logging
import time
from datetime import datetime, timezone

import requests

logger = logging.getLogger("sentinel.loki")


class LokiClient:
    def __init__(self, base_url: str, timeout: int = 5):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    # ── Time helpers ──

    @staticmethod
    def now_ns() -> int:
        return int(datetime.now(timezone.utc).timestamp() * 1e9)

    @staticmethod
    def now_minus_ms(offset_ms: int) -> int:
        return int((datetime.now(timezone.utc).timestamp() * 1000 - offset_ms) * 1e6)

    # ── Query API ──

    def count(self, logql: str, start_ns: int, end_ns: int) -> int:
        try:
            resp = requests.get(
                f"{self.base_url}/loki/api/v1/query_range",
                params={"query": logql, "start": str(start_ns), "end": str(end_ns), "limit": 1000, "direction": "forward"},
                timeout=self.timeout,
            )
            if resp.status_code != 200:
                return -1
            total = 0
            for stream in resp.json().get("data", {}).get("result", []):
                total += len(stream.get("values", []))
            return total
        except Exception as e:
            logger.warning("Loki count error: %s", e)
            return -1

    def query_lines(self, logql: str, start_ns: int, end_ns: int, limit: int = 1000) -> list[dict]:
        try:
            resp = requests.get(
                f"{self.base_url}/loki/api/v1/query_range",
                params={"query": logql, "start": str(start_ns), "end": str(end_ns), "limit": limit, "direction": "forward"},
                timeout=self.timeout,
            )
            if resp.status_code != 200:
                return []
            lines = []
            for stream in resp.json().get("data", {}).get("result", []):
                for pair in stream.get("values", []):
                    if len(pair) >= 2:
                        try:
                            lines.append(json.loads(pair[1]))
                        except (json.JSONDecodeError, TypeError):
                            pass
            return lines
        except Exception:
            return []

    # ── Push API ──

    def push(self, entry: dict, env: str = "local"):
        """Push a single log entry to Loki. Fire-and-forget."""
        try:
            ts_ns = str(int(time.time() * 1e9))
            stream_labels = {"app": "sim-steward", "env": env, "level": entry.get("level", "INFO")}
            for key in ("component", "event", "domain"):
                val = entry.get(key)
                if val:
                    stream_labels[key] = val
            payload = {"streams": [{"stream": stream_labels, "values": [[ts_ns, json.dumps(entry)]]}]}
            requests.post(f"{self.base_url}/loki/api/v1/push", json=payload, timeout=3)
        except Exception as e:
            logger.debug("Loki push error: %s", e)

    # ── Sentinel event helpers ──

    def push_finding(self, finding, env: str = "local"):
        entry = {
            "level": "WARN" if finding.severity in ("warn", "critical") else "INFO",
            "message": finding.title,
            "timestamp": finding.timestamp,
            "component": "log-sentinel",
            "event": "sentinel_finding",
            "domain": "system",
            "finding_id": finding.finding_id,
            "detector": finding.detector,
            "category": finding.category,
            "severity": finding.severity,
            "title": finding.title,
            "summary": finding.summary,
            "fingerprint": finding.fingerprint,
            "escalated_to_t2": finding.escalate_to_t2,
            "logql_query": finding.logql_query,
            "flow_context": finding.flow_context,
            **finding.evidence,
        }
        self.push(entry, env)

    def push_investigation(self, investigation, env: str = "local"):
        entry = {
            "level": "INFO",
            "message": f"Investigation: {investigation.root_cause[:120]}",
            "timestamp": investigation.timestamp,
            "component": "log-sentinel",
            "event": "sentinel_investigation",
            "domain": "system",
            "investigation_id": investigation.investigation_id,
            "finding_id": investigation.finding.finding_id,
            "detector": investigation.finding.detector,
            "category": investigation.finding.category,
            "trigger": investigation.trigger,
            "model": investigation.model,
            "confidence": investigation.confidence,
            "issue_type": investigation.issue_type,
            "root_cause": investigation.root_cause,
            "correlation": investigation.correlation,
            "impact": investigation.impact,
            "recommendation": investigation.recommendation,
            "inference_duration_ms": investigation.inference_duration_ms,
            "gather_duration_ms": investigation.gather_duration_ms,
            "context_lines_gathered": investigation.context_lines_gathered,
        }
        self.push(entry, env)

    def push_cycle(self, cycle_data: dict, env: str = "local"):
        entry = {
            "level": "INFO",
            "message": f"Cycle #{cycle_data['cycle_num']}: {cycle_data['finding_count']} findings, {cycle_data['escalated_count']} escalated",
            "component": "log-sentinel",
            "event": "sentinel_cycle",
            "domain": "system",
            **cycle_data,
        }
        self.push(entry, env)

    def push_detector_run(self, run_data: dict, env: str = "local"):
        entry = {
            "level": "ERROR" if run_data.get("error") else "INFO",
            "message": f"Detector {run_data['detector']}: {run_data['finding_count']} findings in {run_data['duration_ms']}ms",
            "component": "log-sentinel",
            "event": "sentinel_detector_run",
            "domain": "system",
            **run_data,
        }
        self.push(entry, env)

    def push_t2_run(self, t2_data: dict, env: str = "local"):
        entry = {
            "level": "INFO",
            "message": f"T2 {t2_data['tier']}: {t2_data['model']} confidence={t2_data.get('confidence', '?')} in {t2_data.get('total_duration_ms', '?')}ms",
            "component": "log-sentinel",
            "event": "sentinel_t2_run",
            "domain": "system",
            **t2_data,
        }
        self.push(entry, env)

    def push_sentry_event(self, sentry_data: dict, env: str = "local"):
        entry = {
            "level": "INFO",
            "message": f"Sentry issue: {sentry_data.get('title', '?')[:100]}",
            "component": "log-sentinel",
            "event": "sentinel_sentry_issue",
            "domain": "system",
            **sentry_data,
        }
        self.push(entry, env)
