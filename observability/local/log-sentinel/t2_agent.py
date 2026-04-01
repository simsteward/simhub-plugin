"""T2 — Deep investigation agent.

Replaces the T2 half of analyst.py for v3.
Key changes over v2 Analyst.run_t2():
  - Reads evidence packets from Loki (state store), not from T1Result directly
  - Queries Sentry for existing issues before forming recommendations
  - Produces sentinel_t2_investigation events to Loki
  - Creates Grafana annotation per investigation
  - Creates Sentry issue if sentry_worthy + high confidence + not already captured

Input flow:
  Loki {event="sentinel_evidence_packet"} (last 15 min)
  → SentryClient.search_issues() for each anomaly signature
  → qwen3:32b /think
  → LokiClient.push_t2_investigation()
  → GrafanaClient.annotate_raw()
  → SentryClient.capture_message() if warranted
"""

import json
import logging
import time
from dataclasses import dataclass, field

from analyst import _parse_json, _normalize_confidence, _normalize_issue_type, _valid_logql
from circuit_breaker import CircuitBreaker
from config import Config
from grafana_client import GrafanaClient
from loki_client import LokiClient
from ollama_client import OllamaClient
from prompts import (
    T2_EVIDENCE_SYSTEM, T2_EVIDENCE_PROMPT,
    build_stream_guide, format_evidence_packets_for_t2, format_logql_results,
    LOGQL_GEN_SYSTEM, LOGQL_GEN_PROMPT,
)
from sentry_client import SentryClient

logger = logging.getLogger("sentinel.t2")

# How far back to pull evidence packets from Loki
_EVIDENCE_LOOKBACK_SEC = 900  # 15 minutes


@dataclass
class T2Result:
    root_cause: str
    issue_type: str
    confidence: str
    correlation: str
    impact: str
    recommendation: str
    logql_queries_used: list[str]
    sentry_worthy: bool
    sentry_fingerprint: str
    evidence_packet_count: int
    sentry_event_id: str | None
    model: str
    inference_duration_ms: int
    logql_gather_duration_ms: int
    raw_response: str = field(repr=False, default="")

    @property
    def total_duration_ms(self) -> int:
        return self.inference_duration_ms + self.logql_gather_duration_ms


class T2Agent:
    def __init__(
        self,
        ollama: OllamaClient,
        loki: LokiClient,
        grafana: GrafanaClient,
        sentry: SentryClient,
        breaker: CircuitBreaker,
        config: Config,
    ):
        self.ollama = ollama
        self.loki = loki
        self.grafana = grafana
        self.sentry = sentry
        self.breaker = breaker
        self.config = config
        self._stream_guide = build_stream_guide()

    def run(
        self,
        end_ns: int | None = None,
        lookback_sec: int = _EVIDENCE_LOOKBACK_SEC,
        forced_packet_ids: list[str] | None = None,
    ) -> T2Result | None:
        """
        Run T2 investigation over recent evidence packets.

        forced_packet_ids: if set, only process these specific anomaly_ids
                           (used when T1 immediately escalates critical anomalies)
        """
        if end_ns is None:
            end_ns = self.loki.now_ns()
        start_ns = end_ns - lookback_sec * 1_000_000_000

        # Step 1: load evidence packets from Loki
        packet_dicts = self._load_evidence_packets(start_ns, end_ns, forced_packet_ids)
        if not packet_dicts:
            logger.info("T2: no evidence packets in window, skipping")
            return None

        # Step 2: read Sentry history for context
        sentry_context = self._fetch_sentry_context(packet_dicts)

        # Step 3: generate + execute targeted LogQL for additional evidence
        gather_start = time.time()
        queries = self._generate_logql_queries(packet_dicts, lookback_sec // 60)
        logql_results = self._execute_logql_queries(queries, start_ns, end_ns)
        gather_ms = int((time.time() - gather_start) * 1000)

        # Step 4: T2 inference
        system = T2_EVIDENCE_SYSTEM.format(stream_guide=self._stream_guide)
        prompt = T2_EVIDENCE_PROMPT.format(
            evidence_text=format_evidence_packets_for_t2(packet_dicts),
            sentry_context=sentry_context,
            logql_results=format_logql_results(logql_results),
        )

        raw = ""
        infer_ms = 0
        try:
            raw, infer_ms = self.ollama.generate(
                self.config.ollama_model_deep,
                system + "\n\n" + prompt,
                think=True,
            )
            self.breaker.record_success()
        except Exception as e:
            self.breaker.record_failure()
            logger.error("T2 inference failed: %s", e)

        parsed = _parse_json(raw)
        all_queries = queries + list(parsed.get("logql_queries_used", []))

        result = T2Result(
            root_cause=parsed.get("root_cause", "Unable to determine root cause."),
            issue_type=_normalize_issue_type(parsed.get("issue_type", "unknown")),
            confidence=_normalize_confidence(parsed.get("confidence", "low")),
            correlation=parsed.get("correlation", "No correlations identified."),
            impact=parsed.get("impact", "Impact unknown."),
            recommendation=parsed.get("recommendation", "Investigate manually."),
            logql_queries_used=all_queries,
            sentry_worthy=bool(parsed.get("sentry_worthy", False)),
            sentry_fingerprint=str(parsed.get("sentry_fingerprint", ""))[:100],
            evidence_packet_count=len(packet_dicts),
            sentry_event_id=None,
            model=self.config.ollama_model_deep,
            inference_duration_ms=infer_ms,
            logql_gather_duration_ms=gather_ms,
            raw_response=raw,
        )

        # Step 5: push investigation to Loki + Grafana
        self._push_investigation(result, packet_dicts, end_ns)
        self._annotate_grafana(result)

        # Step 6: create Sentry issue if warranted
        if result.sentry_worthy and result.confidence == "high":
            event_id = self._create_sentry_issue(result, packet_dicts)
            result.sentry_event_id = event_id

        logger.info(
            "T2 complete: confidence=%s sentry=%s packets=%d gather=%dms infer=%dms queries=%d",
            result.confidence, result.sentry_worthy,
            len(packet_dicts), gather_ms, infer_ms, len(all_queries),
        )
        return result

    # ── Private ───────────────────────────────────────────────────────────────

    def _load_evidence_packets(
        self,
        start_ns: int,
        end_ns: int,
        forced_ids: list[str] | None,
    ) -> list[dict]:
        logql = '{app="sim-steward", event="sentinel_evidence_packet"}'
        packets = self.loki.query_lines(logql, start_ns, end_ns, limit=100)
        if forced_ids:
            packets = [p for p in packets if p.get("anomaly_id") in forced_ids]
        # Dedup by anomaly_id, keep most recent
        seen: dict[str, dict] = {}
        for p in packets:
            aid = p.get("anomaly_id", "")
            if aid not in seen or p.get("assembled_at_ns", 0) > seen[aid].get("assembled_at_ns", 0):
                seen[aid] = p
        return list(seen.values())

    def _fetch_sentry_context(self, packet_dicts: list[dict]) -> str:
        if not packet_dicts:
            return "(no Sentry history available)"
        # Build a query from the most severe anomaly descriptions
        critical = [p for p in packet_dicts if p.get("severity") == "critical"]
        sample = (critical or packet_dicts)[:3]
        streams = list({p.get("detector_stream", "") for p in sample if p.get("detector_stream")})
        query = " ".join(streams) + " " + " ".join(
            p.get("anomaly_description", "")[:40] for p in sample
        )
        try:
            issues = self.sentry.search_issues(query=query.strip()[:200], limit=5)
            if not issues:
                return "(no matching Sentry issues found)"
            lines = []
            for issue in issues:
                lines.append(
                    f"  [{issue.get('level', '?').upper()}] {issue.get('title', '?')[:80]}"
                    f" (status={issue.get('status', '?')}, times_seen={issue.get('count', '?')})"
                )
                if issue.get("lastSeen"):
                    lines.append(f"    last_seen: {issue['lastSeen']}")
            return "\n".join(lines)
        except Exception as e:
            logger.debug("Sentry context fetch failed: %s", e)
            return "(Sentry unavailable)"

    def _generate_logql_queries(
        self,
        packet_dicts: list[dict],
        window_minutes: int,
    ) -> list[str]:
        # Seed with suggested_logql from evidence packets
        seeded = [
            p["suggested_logql"] for p in packet_dicts
            if p.get("suggested_logql") and _valid_logql(p["suggested_logql"])
        ]

        if not packet_dicts:
            return seeded[:5]

        anomaly_descriptions = "\n".join(
            f"- {p.get('anomaly_id', '?')}: {p.get('anomaly_description', '')[:80]}"
            for p in packet_dicts[:5]
        )
        prompt = LOGQL_GEN_SYSTEM + "\n\n" + LOGQL_GEN_PROMPT.format(
            anomaly_descriptions=anomaly_descriptions,
            window_minutes=window_minutes,
        )
        try:
            raw, _ = self.ollama.generate(
                self.config.ollama_model_fast,
                prompt,
                think=False,
                temperature=0.0,
            )
            generated = json.loads(raw) if raw.strip().startswith("[") else []
            if isinstance(generated, list):
                combined = seeded + [q for q in generated if isinstance(q, str)]
                return [q.strip() for q in combined if _valid_logql(q)][:5]
        except Exception as e:
            logger.debug("T2 LogQL gen failed: %s", e)

        return [q for q in seeded if _valid_logql(q)][:5]

    def _execute_logql_queries(
        self, queries: list[str], start_ns: int, end_ns: int
    ) -> dict[str, list[dict]]:
        results = {}
        for query in queries:
            try:
                lines = self.loki.query_lines(query, start_ns, end_ns, limit=50)
                results[query] = lines
            except Exception as e:
                logger.debug("T2 LogQL execute failed (%s): %s", query[:60], e)
                results[query] = []
        return results

    def _push_investigation(
        self, result: T2Result, packet_dicts: list[dict], end_ns: int
    ) -> None:
        try:
            self.loki.push_t2_investigation(result, packet_dicts, env=self.config.env_label)
        except Exception as e:
            logger.warning("Failed to push T2 investigation to Loki: %s", e)

    def _annotate_grafana(self, result: T2Result) -> None:
        try:
            severity_tag = "critical" if result.confidence == "high" and result.sentry_worthy else "investigation"
            self.grafana.annotate_raw(
                title=f"T2 Investigation [{result.confidence}]: {result.root_cause[:80]}",
                text=(
                    f"<b>Root cause:</b> {result.root_cause}<br>"
                    f"<b>Recommendation:</b> {result.recommendation}<br>"
                    f"<em>Type: {result.issue_type} | Packets: {result.evidence_packet_count} | "
                    f"Model: {result.model}</em>"
                ),
                tags=["t2", result.issue_type, result.confidence, severity_tag],
            )
        except Exception as e:
            logger.debug("T2 Grafana annotation failed: %s", e)

    def _create_sentry_issue(
        self, result: T2Result, packet_dicts: list[dict]
    ) -> str | None:
        try:
            streams = list({p.get("detector_stream", "") for p in packet_dicts if p.get("detector_stream")})
            fingerprint = result.sentry_fingerprint or f"t2.{result.issue_type}.{streams[0] if streams else 'unknown'}"
            return self.sentry.capture_behavioral_finding(
                title=result.root_cause[:120],
                issue_type=result.issue_type,
                recommendation=result.recommendation,
                confidence=result.confidence,
                fingerprint=fingerprint,
                context={
                    "root_cause": result.root_cause,
                    "correlation": result.correlation,
                    "impact": result.impact,
                    "evidence_packet_count": result.evidence_packet_count,
                    "model": result.model,
                },
            )
        except Exception as e:
            logger.warning("T2 Sentry issue creation failed: %s", e)
            return None
