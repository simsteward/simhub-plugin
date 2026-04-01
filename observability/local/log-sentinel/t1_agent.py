"""T1 — Fast triage agent.

Replaces the T1 half of analyst.py for v3.
Key changes over v2 Analyst.run_t1():
  - Accepts pre-built FeatureInvocations from InvocationBuilder
  - Injects BaselineManager context into anomaly prompt
  - Accepts optional T0 alert context for event-driven runs
  - Builds EvidencePackets for each anomaly via EvidenceBuilder
  - Pushes sentinel_evidence_packet events to Loki
  - T1Result carries invocations + evidence_packets + trigger metadata
"""

import logging
from dataclasses import dataclass, field

from analyst import _parse_json, _normalize_anomalies
from baseline import BaselineManager
from circuit_breaker import CircuitBreaker
from config import Config
from evidence import EvidenceBuilder, EvidencePacket
from loki_client import LokiClient
from ollama_client import OllamaClient
from prompts import (
    T1_SYSTEM, T1_SUMMARY_PROMPT, T1_ANOMALY_PROMPT_V3,
    build_stream_guide, format_log_sample, format_invocations,
)
from trace import FeatureInvocation

logger = logging.getLogger("sentinel.t1")


@dataclass
class T1Result:
    summary: str
    cycle_notes: str
    anomalies: list[dict]
    invocations: list[FeatureInvocation]
    evidence_packets: list[EvidencePacket]
    model: str
    summary_duration_ms: int
    anomaly_duration_ms: int
    summary_input_tokens: int
    summary_output_tokens: int
    anomaly_input_tokens: int
    anomaly_output_tokens: int
    trigger_source: str          # "scheduled" | "grafana_alert"
    alert_names: list[str]       # T0 alert names that triggered this run
    raw_summary_response: str = field(repr=False, default="")
    raw_anomaly_response: str = field(repr=False, default="")

    @property
    def needs_t2(self) -> bool:
        return any(a.get("needs_t2") for a in self.anomalies)

    @property
    def total_duration_ms(self) -> int:
        return self.summary_duration_ms + self.anomaly_duration_ms

    @property
    def total_input_tokens(self) -> int:
        return self.summary_input_tokens + self.anomaly_input_tokens

    @property
    def total_output_tokens(self) -> int:
        return self.summary_output_tokens + self.anomaly_output_tokens

    @property
    def tokens_per_sec(self) -> float:
        secs = self.total_duration_ms / 1000
        return round(self.total_output_tokens / secs, 2) if secs > 0 else 0.0


class T1Agent:
    def __init__(
        self,
        ollama: OllamaClient,
        loki: LokiClient,
        breaker: CircuitBreaker,
        config: Config,
        baseline: BaselineManager,
        evidence_builder: EvidenceBuilder,
    ):
        self.ollama = ollama
        self.loki = loki
        self.breaker = breaker
        self.config = config
        self.baseline = baseline
        self.evidence_builder = evidence_builder
        self._stream_guide = build_stream_guide()

    def run(
        self,
        start_ns: int,
        end_ns: int,
        counts: dict[str, int],
        sim_steward_sample: list[dict],
        claude_dev_sample: list[dict],
        claude_token_sample: list[dict],
        invocations: list[FeatureInvocation],
        alert_context: str = "",
        trigger_source: str = "scheduled",
        alert_names: list[str] | None = None,
    ) -> T1Result:
        window_minutes = max(1, int((end_ns - start_ns) / 1e9 / 60))
        counts_text = "\n".join(f"  {k}: {v}" for k, v in counts.items())

        samples = dict(
            sim_steward_sample=format_log_sample(sim_steward_sample),
            sim_steward_count=len(sim_steward_sample),
            claude_dev_sample=format_log_sample(claude_dev_sample),
            claude_dev_count=len(claude_dev_sample),
            claude_token_sample=format_log_sample(claude_token_sample),
            claude_token_count=len(claude_token_sample),
        )

        invocations_text = format_invocations(invocations)
        baseline_context = self.baseline.get_prompt_context()
        system = T1_SYSTEM.format(stream_guide=self._stream_guide)

        # Optional T0 alert context prefix — injected into both calls
        alert_prefix = ""
        if alert_context:
            alert_prefix = (
                f"ALERT CONTEXT (from Grafana):\n{alert_context}\n"
                "→ Focus investigation on this signal. Do not suppress even if recent history is quiet.\n\n"
            )

        # Call A: summary (/no_think — fast)
        summary_prompt = alert_prefix + T1_SUMMARY_PROMPT.format(
            window_minutes=window_minutes,
            counts=counts_text,
            **samples,
        )
        summary_text = ""
        cycle_notes = ""
        summary_ms = 0
        summary_in_tok = 0
        summary_out_tok = 0
        raw_summary = ""
        try:
            result = self.ollama.generate(
                self.config.ollama_model_fast,
                system + "\n\n" + summary_prompt,
                think=False,
            )
            raw_summary, summary_ms = result.text, result.duration_ms
            summary_in_tok, summary_out_tok = result.input_tokens, result.output_tokens
            self.breaker.record_success()
            parsed = _parse_json(raw_summary)
            summary_text = parsed.get("summary", "")
            cycle_notes = parsed.get("cycle_notes", "")
        except Exception as e:
            self.breaker.record_failure()
            logger.error("T1 summary call failed: %s", e)

        # Call B: anomaly scan (/think) — invocations + baseline context included
        anomaly_prompt = alert_prefix + T1_ANOMALY_PROMPT_V3.format(
            summary=summary_text or "(summary unavailable)",
            counts=counts_text,
            invocations_text=invocations_text,
            baseline_context=baseline_context,
            **samples,
        )
        anomalies = []
        anomaly_ms = 0
        anomaly_in_tok = 0
        anomaly_out_tok = 0
        raw_anomaly = ""
        try:
            result = self.ollama.generate(
                self.config.ollama_model_fast,
                system + "\n\n" + anomaly_prompt,
                think=True,
            )
            raw_anomaly, anomaly_ms = result.text, result.duration_ms
            anomaly_in_tok, anomaly_out_tok = result.input_tokens, result.output_tokens
            self.breaker.record_success()
            parsed = _parse_json(raw_anomaly)
            anomalies = _normalize_anomalies_v3(parsed.get("anomalies", []))
        except Exception as e:
            self.breaker.record_failure()
            logger.error("T1 anomaly call failed: %s", e)

        # Build evidence packets for each anomaly, push to Loki
        evidence_packets = []
        if anomalies:
            evidence_packets = self.evidence_builder.build_many(
                anomalies, invocations, start_ns, end_ns
            )
            for packet in evidence_packets:
                try:
                    self.loki.push_evidence_packet(packet, env=self.config.env_label)
                except Exception as e:
                    logger.warning("Failed to push evidence packet %s: %s", packet.anomaly_id, e)

        total_out = summary_out_tok + anomaly_out_tok
        logger.info(
            "T1 [%s]: %d invocations, %d anomalies (%d→T2), %d evidence packets, summary=%dms anomaly=%dms tokens=%d",
            trigger_source,
            len(invocations),
            len(anomalies),
            sum(1 for a in anomalies if a.get("needs_t2")),
            len(evidence_packets),
            summary_ms,
            anomaly_ms,
            total_out,
        )

        return T1Result(
            summary=summary_text,
            cycle_notes=cycle_notes,
            anomalies=anomalies,
            invocations=invocations,
            evidence_packets=evidence_packets,
            model=self.config.ollama_model_fast,
            summary_duration_ms=summary_ms,
            anomaly_duration_ms=anomaly_ms,
            summary_input_tokens=summary_in_tok,
            summary_output_tokens=summary_out_tok,
            anomaly_input_tokens=anomaly_in_tok,
            anomaly_output_tokens=anomaly_out_tok,
            trigger_source=trigger_source,
            alert_names=alert_names or [],
            raw_summary_response=raw_summary,
            raw_anomaly_response=raw_anomaly,
        )


# ── Helpers ───────────────────────────────────────────────────────────────────

def _normalize_anomalies_v3(raw: list) -> list[dict]:
    """Normalize v3 anomaly dicts from T1 LLM output (superset of v2 fields)."""
    if not isinstance(raw, list):
        return []
    valid = []
    for a in raw:
        if not isinstance(a, dict):
            continue
        valid.append({
            "id": str(a.get("id", "unknown"))[:64],
            "stream": a.get("stream", "unknown"),
            "event_type": str(a.get("event_type", ""))[:64],
            "description": str(a.get("description", ""))[:500],
            "severity": a.get("severity", "info") if a.get("severity") in ("info", "warn", "critical") else "info",
            "needs_t2": bool(a.get("needs_t2", False)),
            "hypothesis": str(a.get("hypothesis", ""))[:300],
            "confidence": float(a.get("confidence", 0.5)) if isinstance(a.get("confidence"), (int, float)) else 0.5,
            "trace_id": str(a.get("trace_id", ""))[:64],
            "suggested_logql": str(a.get("suggested_logql", ""))[:300],
        })
    return valid
