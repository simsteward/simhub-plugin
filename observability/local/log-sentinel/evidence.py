"""Evidence packet model — pre-assembles log context for T2 consumption.

T1 identifies an anomaly, then EvidenceBuilder:
  1. Finds which feature invocations contain the anomalous signal
  2. Builds a targeted LogQL query
  3. Pre-fetches up to 50 related log lines from Loki
  4. Packages everything into an EvidencePacket ready for T2

T2 receives EvidencePackets — it reasons over pre-assembled evidence,
not raw Loki queries. This dramatically improves T2 output quality.
"""

import logging
import time
import uuid
from dataclasses import dataclass, field

from loki_client import LokiClient
from trace import FeatureInvocation

logger = logging.getLogger("sentinel.evidence")

_MAX_LOG_LINES = 50


@dataclass
class EvidencePacket:
    anomaly_id: str
    anomaly_description: str
    severity: str                           # "info" | "warn" | "critical"
    detector_stream: str                    # which stream flagged it
    invocations: list[FeatureInvocation]    # invocations containing the anomaly
    related_log_lines: list[dict]           # pre-fetched raw log lines (capped at 50)
    suggested_logql: str                    # T1's suggested query for T2 to refine
    t1_hypothesis: str                      # T1's one-sentence best-guess root cause
    t1_confidence: float                    # 0.0 to 1.0
    assembled_at_ns: int
    logql_used: str                         # the actual query used to fetch related_log_lines

    def to_loki_dict(self) -> dict:
        """Serializable dict for push to Loki as sentinel_evidence_packet event."""
        return {
            "event": "sentinel_evidence_packet",
            "component": "log-sentinel",
            "domain": "system",
            "level": "WARN" if self.severity in ("warn", "critical") else "INFO",
            "message": f"[{self.severity.upper()}] {self.anomaly_description[:120]}",
            "anomaly_id": self.anomaly_id,
            "anomaly_description": self.anomaly_description,
            "severity": self.severity,
            "detector_stream": self.detector_stream,
            "t1_hypothesis": self.t1_hypothesis,
            "t1_confidence": self.t1_confidence,
            "suggested_logql": self.suggested_logql,
            "logql_used": self.logql_used,
            "related_lines_count": len(self.related_log_lines),
            "invocation_count": len(self.invocations),
            "invocation_ids": [inv.invocation_id for inv in self.invocations],
            "action_types": list({inv.action_type for inv in self.invocations}),
            "assembled_at_ns": self.assembled_at_ns,
        }

    def to_prompt_text(self) -> str:
        """Format evidence packet as text block for LLM (T2) consumption."""
        lines = [
            f"=== EVIDENCE PACKET {self.anomaly_id} ===",
            f"Severity: {self.severity.upper()}",
            f"Stream: {self.detector_stream}",
            f"Anomaly: {self.anomaly_description}",
            f"T1 hypothesis: {self.t1_hypothesis or '(none)'}",
            f"T1 confidence: {self.t1_confidence:.0%}",
            "",
        ]

        if self.invocations:
            lines.append(f"Affected invocations ({len(self.invocations)}):")
            for inv in self.invocations[:5]:
                status = "FAILED" if inv.success is False else ("OK" if inv.success else "?")
                lines.append(
                    f"  [{status}] {inv.action_type} via {inv.correlation_method} "
                    f"({inv.duration_ms}ms, {len(inv.events)} events)"
                )
                if inv.error:
                    lines.append(f"         error: {inv.error}")
            lines.append("")

        if self.related_log_lines:
            lines.append(f"Related log lines ({len(self.related_log_lines)}, capped at {_MAX_LOG_LINES}):")
            for log in self.related_log_lines[:_MAX_LOG_LINES]:
                ts = log.get("timestamp", "")[:19]
                evt = log.get("event", log.get("message", ""))[:60]
                lvl = log.get("level", "")
                err = log.get("error", "")
                suffix = f"  error={err[:60]}" if err else ""
                lines.append(f"  {ts}  [{lvl}] {evt}{suffix}")
            lines.append("")

        lines.append(f"Suggested LogQL for deeper investigation: {self.suggested_logql}")
        return "\n".join(lines)


class EvidenceBuilder:
    """Assembles EvidencePackets from T1 anomaly signals + feature invocations."""

    def __init__(self, loki: LokiClient):
        self.loki = loki

    def build(
        self,
        anomaly: dict,
        invocations: list[FeatureInvocation],
        start_ns: int,
        end_ns: int,
    ) -> EvidencePacket:
        """
        Build an EvidencePacket for a single T1 anomaly.

        anomaly dict shape (from T1 LLM output):
          id, description, severity, stream, event_type,
          hypothesis, confidence, suggested_logql, trace_id
        """
        anomaly_id = anomaly.get("id") or str(uuid.uuid4())[:8]
        stream = anomaly.get("stream", "sim-steward")
        event_type = anomaly.get("event_type", "")

        relevant = self._find_relevant_invocations(anomaly, invocations)
        logql = self._build_logql(anomaly, relevant, stream, event_type)

        try:
            lines = self.loki.query_lines(logql, start_ns, end_ns, limit=_MAX_LOG_LINES)
        except Exception as e:
            logger.warning("EvidenceBuilder Loki query failed: %s", e)
            lines = []

        suggested = anomaly.get("suggested_logql") or logql

        return EvidencePacket(
            anomaly_id=anomaly_id,
            anomaly_description=anomaly.get("description", anomaly.get("title", "")),
            severity=anomaly.get("severity", "warn"),
            detector_stream=stream,
            invocations=relevant,
            related_log_lines=lines,
            suggested_logql=suggested,
            t1_hypothesis=anomaly.get("hypothesis", ""),
            t1_confidence=float(anomaly.get("confidence", 0.5)),
            assembled_at_ns=int(time.time() * 1e9),
            logql_used=logql,
        )

    def build_many(
        self,
        anomalies: list[dict],
        invocations: list[FeatureInvocation],
        start_ns: int,
        end_ns: int,
    ) -> list[EvidencePacket]:
        """Build evidence packets for all anomalies. Skips on error."""
        packets = []
        for anomaly in anomalies:
            try:
                packet = self.build(anomaly, invocations, start_ns, end_ns)
                packets.append(packet)
            except Exception as e:
                logger.warning("Failed to build evidence for anomaly %s: %s", anomaly.get("id", "?"), e)
        return packets

    # ── Private ───────────────────────────────────────────────────────────

    def _find_relevant_invocations(
        self, anomaly: dict, invocations: list[FeatureInvocation]
    ) -> list[FeatureInvocation]:
        """Find invocations that contain signals matching this anomaly."""
        # Tier 1: exact trace_id match
        trace_id = anomaly.get("trace_id")
        if trace_id:
            matches = [inv for inv in invocations if inv.invocation_id == trace_id]
            if matches:
                return matches

        # Tier 2: invocations containing an event of the matching type/stream
        anomaly_event = anomaly.get("event_type", "")
        anomaly_stream = anomaly.get("stream", "")
        anomaly_severity = anomaly.get("severity", "")

        relevant = []
        for inv in invocations:
            for ev in inv.events:
                stream_match = anomaly_stream and ev.stream == anomaly_stream
                event_match = anomaly_event and ev.event_type == anomaly_event
                error_match = anomaly_severity == "critical" and (
                    ev.raw.get("level", "").upper() == "ERROR" or ev.raw.get("error")
                )
                if stream_match or event_match or error_match:
                    relevant.append(inv)
                    break

        if relevant:
            return relevant

        # Tier 3: failed invocations (best-effort for error anomalies)
        failed = [inv for inv in invocations if inv.success is False]
        if failed:
            return failed[:3]

        # Fallback: first 3 invocations
        return invocations[:3]

    def _build_logql(
        self,
        anomaly: dict,
        invocations: list[FeatureInvocation],
        stream: str,
        event_type: str,
    ) -> str:
        """Build a targeted LogQL query for fetching related log lines."""
        # Prefer trace_id query if available
        trace_ids = [
            inv.invocation_id
            for inv in invocations
            if inv.correlation_method == "trace_id"
        ]
        if len(trace_ids) == 1:
            return f'{{app="{stream}"}} | json | trace_id="{trace_ids[0]}"'

        # Event-type query
        if event_type:
            return f'{{app="{stream}"}} | json | event="{event_type}"'

        # Severity-based fallback
        severity = anomaly.get("severity", "warn")
        if severity == "critical":
            return f'{{app="{stream}"}} | json | level="ERROR"'

        return f'{{app="{stream}"}} | json'
