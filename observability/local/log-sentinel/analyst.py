"""LLM-driven analyst — T1 fast scan and T2 deep investigation."""

import json
import logging
import re
import time
from dataclasses import dataclass, field

from circuit_breaker import CircuitBreaker
from config import Config
from loki_client import LokiClient
from ollama_client import OllamaClient
from prompts import (
    T1_SYSTEM, T1_SUMMARY_PROMPT, T1_ANOMALY_PROMPT,
    T2_SYSTEM, T2_INVESTIGATION_PROMPT,
    LOGQL_GEN_SYSTEM, LOGQL_GEN_PROMPT,
    build_stream_guide, format_log_sample, format_logql_results,
)
from timeline import TimelineEvent

logger = logging.getLogger("sentinel.analyst")


@dataclass
class T1Result:
    summary: str
    cycle_notes: str
    anomalies: list[dict]
    model: str
    summary_duration_ms: int
    anomaly_duration_ms: int
    raw_summary_response: str
    raw_anomaly_response: str

    @property
    def needs_t2(self) -> bool:
        return any(a.get("needs_t2") for a in self.anomalies)

    @property
    def total_duration_ms(self) -> int:
        return self.summary_duration_ms + self.anomaly_duration_ms


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
    model: str
    inference_duration_ms: int
    logql_gather_duration_ms: int
    raw_response: str = field(repr=False)

    @property
    def total_duration_ms(self) -> int:
        return self.inference_duration_ms + self.logql_gather_duration_ms


class Analyst:
    def __init__(
        self,
        ollama: OllamaClient,
        loki: LokiClient,
        breaker: CircuitBreaker,
        config: Config,
    ):
        self.ollama = ollama
        self.loki = loki
        self.breaker = breaker
        self.config = config
        self._stream_guide = build_stream_guide()

    # ── T1 ──────────────────────────────────────────────────────────────────

    def run_t1(
        self,
        start_ns: int,
        end_ns: int,
        counts: dict[str, int],
        sim_steward_sample: list[dict],
        claude_dev_sample: list[dict],
        claude_token_sample: list[dict],
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

        system = T1_SYSTEM.format(stream_guide=self._stream_guide)

        # Call A: summary (/no_think — fast)
        summary_prompt = T1_SUMMARY_PROMPT.format(
            window_minutes=window_minutes,
            counts=counts_text,
            **samples,
        )
        summary_text = ""
        cycle_notes = ""
        summary_ms = 0
        raw_summary = ""
        try:
            raw_summary, summary_ms = self.ollama.generate(
                self.config.ollama_model_fast,
                system + "\n\n" + summary_prompt,
                think=False,
            )
            self.breaker.record_success()
            parsed = _parse_json(raw_summary)
            summary_text = parsed.get("summary", "")
            cycle_notes = parsed.get("cycle_notes", "")
        except Exception as e:
            self.breaker.record_failure()
            logger.error("T1 summary call failed: %s", e)

        # Call B: anomaly scan (/think — reasoning)
        anomaly_prompt = T1_ANOMALY_PROMPT.format(
            summary=summary_text or "(summary unavailable)",
            counts=counts_text,
            **samples,
        )
        anomalies = []
        anomaly_ms = 0
        raw_anomaly = ""
        try:
            raw_anomaly, anomaly_ms = self.ollama.generate(
                self.config.ollama_model_fast,
                system + "\n\n" + anomaly_prompt,
                think=True,
            )
            self.breaker.record_success()
            parsed = _parse_json(raw_anomaly)
            anomalies = _normalize_anomalies(parsed.get("anomalies", []))
        except Exception as e:
            self.breaker.record_failure()
            logger.error("T1 anomaly call failed: %s", e)

        logger.info(
            "T1 complete: %d anomalies (%d need T2), summary=%dms anomaly=%dms",
            len(anomalies),
            sum(1 for a in anomalies if a.get("needs_t2")),
            summary_ms,
            anomaly_ms,
        )

        return T1Result(
            summary=summary_text,
            cycle_notes=cycle_notes,
            anomalies=anomalies,
            model=self.config.ollama_model_fast,
            summary_duration_ms=summary_ms,
            anomaly_duration_ms=anomaly_ms,
            raw_summary_response=raw_summary,
            raw_anomaly_response=raw_anomaly,
        )

    # ── T2 ──────────────────────────────────────────────────────────────────

    def run_t2(
        self,
        t1_result: T1Result,
        timeline: list[TimelineEvent],
        start_ns: int,
        end_ns: int,
    ) -> T2Result:
        window_minutes = max(1, int((end_ns - start_ns) / 1e9 / 60))
        t2_anomalies = [a for a in t1_result.anomalies if a.get("needs_t2")]

        # Step 1: generate LogQL queries
        gather_start = time.time()
        queries = self._generate_logql_queries(t2_anomalies, window_minutes)

        # Step 2: execute queries
        logql_results = self._execute_logql_queries(queries, start_ns, end_ns)
        gather_ms = int((time.time() - gather_start) * 1000)

        # Step 3: build T2 prompt
        from timeline import TimelineBuilder
        # Use a simple formatter — timeline already built, just need text
        timeline_text = _format_timeline_for_prompt(timeline)

        anomaly_descriptions = "\n".join(
            f"- [{a.get('severity','?').upper()}] {a.get('id','?')}: {a.get('description','')}"
            for a in t2_anomalies
        )

        system = T2_SYSTEM.format(stream_guide=self._stream_guide)
        prompt = T2_INVESTIGATION_PROMPT.format(
            anomaly_descriptions=anomaly_descriptions,
            window_minutes=window_minutes,
            timeline_text=timeline_text,
            logql_results=format_logql_results(logql_results),
            logql_queries_list=json.dumps(queries),
        )

        # Step 4: T2 inference
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
        result = T2Result(
            root_cause=parsed.get("root_cause", "Unable to determine root cause."),
            issue_type=_normalize_issue_type(parsed.get("issue_type", "unknown")),
            confidence=_normalize_confidence(parsed.get("confidence", "low")),
            correlation=parsed.get("correlation", "No correlations identified."),
            impact=parsed.get("impact", "Impact unknown."),
            recommendation=parsed.get("recommendation", "Investigate manually."),
            logql_queries_used=queries,
            sentry_worthy=bool(parsed.get("sentry_worthy", False)),
            model=self.config.ollama_model_deep,
            inference_duration_ms=infer_ms,
            logql_gather_duration_ms=gather_ms,
            raw_response=raw,
        )

        logger.info(
            "T2 complete: confidence=%s sentry=%s gather=%dms infer=%dms queries=%d",
            result.confidence, result.sentry_worthy,
            gather_ms, infer_ms, len(queries),
        )
        return result

    # ── LogQL helpers ────────────────────────────────────────────────────────

    def _generate_logql_queries(
        self,
        anomalies: list[dict],
        window_minutes: int,
    ) -> list[str]:
        if not anomalies:
            return []

        # Seed with any suggested_logql from T1
        seeded = [a.get("suggested_logql", "") for a in anomalies if a.get("suggested_logql")]

        anomaly_descriptions = "\n".join(
            f"- {a.get('id','?')}: {a.get('description','')}" for a in anomalies[:5]
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
                # Combine seeded + generated, validate all
                combined = seeded + [q for q in generated if isinstance(q, str)]
                valid = [q.strip() for q in combined if _valid_logql(q)]
                return valid[:5]
        except Exception as e:
            logger.warning("LogQL gen failed: %s", e)

        # Fall back to seeded only
        return [q for q in seeded if _valid_logql(q)][:5]

    def _execute_logql_queries(
        self,
        queries: list[str],
        start_ns: int,
        end_ns: int,
    ) -> dict[str, list[dict]]:
        results = {}
        for query in queries:
            try:
                lines = self.loki.query_lines(query, start_ns, end_ns, limit=50)
                results[query] = lines
            except Exception as e:
                logger.warning("LogQL execute failed (%s): %s", query[:60], e)
                results[query] = []
        return results


# ── Helpers ──────────────────────────────────────────────────────────────────

def _parse_json(text: str) -> dict:
    """Extract and parse the first JSON object or array from text."""
    if not text:
        return {}
    # Try direct parse first
    text = text.strip()
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass
    # Find first {...} or [...] block
    for start_char, end_char in [('{', '}'), ('[', ']')]:
        start = text.find(start_char)
        end = text.rfind(end_char)
        if start != -1 and end > start:
            try:
                return json.loads(text[start:end + 1])
            except json.JSONDecodeError:
                pass
    return {}


def _normalize_anomalies(raw: list) -> list[dict]:
    if not isinstance(raw, list):
        return []
    valid = []
    for a in raw:
        if not isinstance(a, dict):
            continue
        valid.append({
            "id": str(a.get("id", "unknown"))[:64],
            "stream": a.get("stream", "unknown"),
            "description": str(a.get("description", ""))[:500],
            "severity": a.get("severity", "info") if a.get("severity") in ("info", "warn", "critical") else "info",
            "needs_t2": bool(a.get("needs_t2", False)),
            "suggested_logql": str(a.get("suggested_logql", ""))[:300],
        })
    return valid


def _normalize_confidence(v: str) -> str:
    return v if v in ("high", "medium", "low") else "low"


def _normalize_issue_type(v: str) -> str:
    valid = ("error_spike", "config", "regression", "user_behavior", "infra", "unknown")
    return v if v in valid else "unknown"


def _valid_logql(q: str) -> bool:
    q = q.strip()
    return bool(q) and q.startswith("{") and "|" in q


def _format_timeline_for_prompt(events: list[TimelineEvent], max_events: int = 60) -> str:
    """Minimal timeline formatter used by analyst (avoids circular import with TimelineBuilder)."""
    if not events:
        return "(no timeline events)"

    truncated = len(events) > max_events
    shown = events[-max_events:] if truncated else events

    lines = []
    for i, ev in enumerate(shown, 1):
        try:
            t = ev.ts_iso[11:19]
        except (IndexError, TypeError):
            t = "??:??:??"
        sid = f" session={ev.session_id[:8]}" if ev.session_id else ""
        lines.append(f"  [{i:03d}] {t}  {ev.stream:<25}  {ev.event_type}{sid}")

    if truncated:
        lines.append(f"  [... {len(events) - max_events} earlier events not shown]")

    return "\n".join(lines)
