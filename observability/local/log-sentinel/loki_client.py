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
        anomaly_count = cycle_data.get("anomaly_count", cycle_data.get("finding_count", 0))
        entry = {
            "level": "INFO",
            "message": f"Cycle #{cycle_data['cycle_num']}: {anomaly_count} anomalies",
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

    def push_analyst_run(self, run_data: dict, env: str = "local"):
        tier = run_data.get("tier", "t1")
        tokens_note = f" tokens={run_data.get('total_output_tokens', run_data.get('output_tokens', '?'))}" if run_data.get("total_output_tokens") or run_data.get("output_tokens") else ""
        entry = {
            "level": "INFO",
            "message": f"Analyst {tier}: model={run_data.get('model','?')} anomalies={run_data.get('anomaly_count', run_data.get('logql_queries_generated', '?'))} duration={run_data.get('duration_ms','?')}ms{tokens_note}",
            "component": "log-sentinel",
            "event": "sentinel_analyst_run",
            "domain": "system",
            **run_data,
        }
        self.push(entry, env)

    def push_tool_call(
        self,
        tool: str,
        tier: str,
        duration_ms: int,
        success: bool,
        env: str = "local",
        detail: str = "",
        cycle_id: str = "",
    ):
        """Push sentinel_tool_call — per external tool invocation (Loki, Sentry, Grafana)."""
        entry = {
            "level": "INFO" if success else "WARN",
            "message": f"Tool call: {tool} [{tier}] {duration_ms}ms {'ok' if success else 'failed'}",
            "component": "log-sentinel",
            "event": "sentinel_tool_call",
            "domain": "system",
            "tool": tool,
            "tier": tier,
            "duration_ms": duration_ms,
            "success": success,
            "detail": detail[:200] if detail else "",
            "cycle_id": cycle_id,
        }
        self.push(entry, env)

    def push_timeline(self, timeline_data: dict, env: str = "local"):
        entry = {
            "level": "INFO",
            "message": f"Timeline: {timeline_data.get('event_count', 0)} events, {timeline_data.get('session_count', 0)} sessions",
            "component": "log-sentinel",
            "event": "sentinel_timeline_built",
            "domain": "system",
            **timeline_data,
        }
        self.push(entry, env)

    def push_investigation_v2(self, t2_result, anomalies: list, env: str = "local"):
        from analyst import T2Result
        entry = {
            "level": "INFO",
            "message": f"Investigation [{t2_result.confidence}]: {t2_result.root_cause[:120]}",
            "component": "log-sentinel",
            "event": "sentinel_investigation",
            "domain": "system",
            "anomaly_ids": [a.get("id", "") for a in anomalies if a.get("needs_t2")],
            "root_cause": t2_result.root_cause,
            "issue_type": t2_result.issue_type,
            "confidence": t2_result.confidence,
            "correlation": t2_result.correlation,
            "impact": t2_result.impact,
            "recommendation": t2_result.recommendation,
            "logql_queries_used": t2_result.logql_queries_used,
            "logql_gather_duration_ms": t2_result.logql_gather_duration_ms,
            "inference_duration_ms": t2_result.inference_duration_ms,
            "sentry_worthy": t2_result.sentry_worthy,
            "model": t2_result.model,
        }
        self.push(entry, env)

    def annotate_raw(self, *args, **kwargs):
        """Stub — annotate_raw is called on grafana_client, not loki_client."""
        pass

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

    # ── v3 push helpers ──────────────────────────────────────────────────────

    def push_evidence_packet(self, packet, env: str = "local"):
        """Push sentinel_evidence_packet — T1's pre-assembled anomaly context."""
        entry = packet.to_loki_dict()
        self.push(entry, env)

    def push_t2_investigation(self, t2_result, packet_dicts: list, env: str = "local"):
        """Push sentinel_t2_investigation — T2's investigation result."""
        entry = {
            "level": "INFO",
            "message": f"T2 investigation [{t2_result.confidence}]: {t2_result.root_cause[:120]}",
            "component": "log-sentinel",
            "event": "sentinel_t2_investigation",
            "domain": "system",
            "root_cause": t2_result.root_cause,
            "issue_type": t2_result.issue_type,
            "confidence": t2_result.confidence,
            "correlation": t2_result.correlation,
            "impact": t2_result.impact,
            "recommendation": t2_result.recommendation,
            "sentry_worthy": t2_result.sentry_worthy,
            "sentry_fingerprint": t2_result.sentry_fingerprint,
            "sentry_event_id": t2_result.sentry_event_id or "",
            "evidence_packet_count": t2_result.evidence_packet_count,
            "anomaly_ids": [p.get("anomaly_id", "") for p in packet_dicts],
            "logql_queries_used": t2_result.logql_queries_used,
            "logql_gather_duration_ms": t2_result.logql_gather_duration_ms,
            "inference_duration_ms": t2_result.inference_duration_ms,
            "input_tokens": getattr(t2_result, "input_tokens", 0),
            "output_tokens": getattr(t2_result, "output_tokens", 0),
            "tokens_per_sec": getattr(t2_result, "tokens_per_sec", 0.0),
            "model": t2_result.model,
        }
        self.push(entry, env)

    def push_synthesis(self, t3_result, trigger: str = "scheduled", env: str = "local"):
        """Push sentinel_synthesis — T3's period synthesis summary."""
        entry = {
            "level": "INFO",
            "message": f"T3 synthesis [{trigger}]: {t3_result.sessions_analyzed} sessions, "
                       f"{len(t3_result.recurring_patterns)} patterns",
            "component": "log-sentinel",
            "event": "sentinel_synthesis",
            "domain": "system",
            "trigger": trigger,
            "period_summary": t3_result.period_summary[:500],
            "sessions_analyzed": t3_result.sessions_analyzed,
            "features_worked": t3_result.features_worked,
            "features_failed": t3_result.features_failed,
            "recurring_pattern_count": len(t3_result.recurring_patterns),
            "regression_detected": t3_result.regression_detected,
            "regression_detail": t3_result.regression_detail[:200],
            "action_items": t3_result.action_items[:5],
            "baselines_updated": t3_result.baselines_updated,
            "threshold_recommendation_count": len(t3_result.threshold_recommendations),
            "model": t3_result.model,
            "inference_duration_ms": t3_result.inference_duration_ms,
            "input_tokens": getattr(t3_result, "input_tokens", 0),
            "output_tokens": getattr(t3_result, "output_tokens", 0),
            "tokens_per_sec": getattr(t3_result, "tokens_per_sec", 0.0),
        }
        self.push(entry, env)

    def push_narrative(self, narrative_dict: dict, env: str = "local"):
        """Push sentinel_narrative — T3's per-session story."""
        entry = {
            "level": "INFO",
            "message": f"Session narrative: {narrative_dict.get('session_id', '?')[:12]}",
            "component": "log-sentinel",
            "event": "sentinel_narrative",
            "domain": "system",
            "session_id": narrative_dict.get("session_id", ""),
            "narrative_text": narrative_dict.get("narrative_text", "")[:1000],
            "features_worked": narrative_dict.get("features_worked", []),
            "features_failed": narrative_dict.get("features_failed", []),
            "invocation_count": narrative_dict.get("invocation_count", 0),
        }
        self.push(entry, env)

    def push_threshold_recommendation(self, rec: dict, env: str = "local"):
        """Push sentinel_threshold_recommendation — T3's threshold calibration advice."""
        entry = {
            "level": "INFO",
            "message": (
                f"Threshold recommendation: {rec.get('alert', '?')} "
                f"current={rec.get('current_threshold')} → suggested={rec.get('suggested_threshold')} "
                f"({rec.get('direction', '?')})"
            ),
            "component": "log-sentinel",
            "event": "sentinel_threshold_recommendation",
            "domain": "system",
            **rec,
        }
        self.push(entry, env)

    def push_trigger(self, alert_data: dict, env: str = "local"):
        """Push sentinel_trigger — per T0 webhook alert received."""
        entry = {
            "level": "INFO",
            "message": f"Trigger: {alert_data.get('alertname', '?')} [{alert_data.get('trigger_tier', '?')}]",
            "component": "log-sentinel",
            "event": "sentinel_trigger",
            "domain": "system",
            "trigger_source": "grafana_alert",
            **alert_data,
        }
        self.push(entry, env)
