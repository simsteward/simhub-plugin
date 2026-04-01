"""T3 — Synthesis agent.

Runs on a mode-dependent schedule (dev: 2h, prod: 4h) or on T2 critical escalation.
Answers: "What was the user trying to do, and did it work?"

What T3 does:
  1. Query Loki for T1 evidence packets + T2 investigations for the synthesis window
  2. Query Sentry for open issues + recent releases
  3. Build session narratives via NarrativeBuilder
  4. Run qwen3:32b /think for 7 synthesis passes (single LLM call)
  5. Update baselines.json via BaselineManager
  6. Emit sentinel_threshold_recommendation per drifted T0 threshold
  7. Push sentinel_synthesis + sentinel_narrative events to Loki

Mode differences:
  dev  — 2h cadence, focus: Claude sessions, tool usage, code activity
  prod — 4h cadence, focus: iRacing sessions, feature stability, user-facing errors
"""

import logging
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone

from analyst import _parse_json
from baseline import BaselineManager
from circuit_breaker import CircuitBreaker
from config import Config
from grafana_client import GrafanaClient
from loki_client import LokiClient
from narrative import NarrativeBuilder
from ollama_client import OllamaClient
from prompts import T3_SYSTEM, T3_SYNTHESIS_PROMPT, build_stream_guide
from sentry_client import SentryClient
from trace import FeatureInvocation

logger = logging.getLogger("sentinel.t3")

# Lookbacks per mode for pulling Loki evidence
_MODE_LOOKBACK = {
    "dev": 2 * 3600,
    "prod": 4 * 3600,
}


@dataclass
class T3Result:
    period_summary: str
    sessions_analyzed: int
    features_worked: list[str]
    features_failed: list[str]
    recurring_patterns: list[dict]
    cost_summary: dict
    regression_detected: bool
    regression_detail: str
    action_items: list[str]
    baselines_updated: bool
    threshold_recommendations: list[dict]
    session_narratives: list[dict]   # list of {session_id, narrative_text, ...}
    model: str
    inference_duration_ms: int
    raw_response: str = field(repr=False, default="")


class T3Agent:
    def __init__(
        self,
        ollama: OllamaClient,
        loki: LokiClient,
        grafana: GrafanaClient,
        sentry: SentryClient,
        baseline: BaselineManager,
        breaker: CircuitBreaker,
        config: Config,
    ):
        self.ollama = ollama
        self.loki = loki
        self.grafana = grafana
        self.sentry = sentry
        self.baseline = baseline
        self.breaker = breaker
        self.config = config
        self.narrative_builder = NarrativeBuilder()
        self._stream_guide = build_stream_guide()

    def run(
        self,
        end_ns: int | None = None,
        invocations: list[FeatureInvocation] | None = None,
        lookback_sec: int | None = None,
        trigger: str = "scheduled",
    ) -> T3Result:
        """
        Run T3 synthesis.

        invocations: if provided (e.g. from same-cycle T1 run), used for narratives.
                     Otherwise T3 uses only Loki-stored invocation summaries.
        trigger: "scheduled" | "t2_escalation"
        """
        if end_ns is None:
            end_ns = self.loki.now_ns()
        if lookback_sec is None:
            lookback_sec = _MODE_LOOKBACK.get(self.config.sentinel_mode, 7200)
        start_ns = end_ns - lookback_sec * 1_000_000_000

        mode = self.config.sentinel_mode
        window_description = _format_window(start_ns, end_ns, mode)

        # Step 1: load evidence from Loki
        evidence_packets = self._load_evidence_packets(start_ns, end_ns)
        investigations = self._load_investigations(start_ns, end_ns)

        # Step 2: Sentry context
        sentry_issues_text, sentry_releases_text = self._fetch_sentry_context()

        # Step 3: build session narratives
        session_narratives = []
        if invocations:
            all_anomalies = [ep for ep in evidence_packets]
            session_narratives = self.narrative_builder.build_all(
                invocations=invocations,
                anomaly_dicts=all_anomalies,
                t2_investigation_dicts=investigations,
            )

        narratives_text = _format_narratives_for_prompt(session_narratives)

        # Step 4: T3 LLM synthesis
        system = T3_SYSTEM.format(stream_guide=self._stream_guide)
        prompt = T3_SYNTHESIS_PROMPT.format(
            window_description=window_description,
            mode=mode,
            evidence_summary=_format_evidence_summary(evidence_packets),
            investigation_summary=_format_investigation_summary(investigations),
            sentry_issues=sentry_issues_text,
            recent_releases=sentry_releases_text,
            session_narratives=narratives_text,
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
            logger.error("T3 inference failed: %s", e)

        parsed = _parse_json(raw)

        # Step 5: update baselines
        baselines_updated = False
        threshold_recs = []
        try:
            self.baseline.compute_and_save(lookback_sec=lookback_sec)
            threshold_recs = self.baseline.get_threshold_recommendations()
            baselines_updated = True
            logger.info("T3: baselines updated, %d threshold recommendations", len(threshold_recs))
        except Exception as e:
            logger.warning("T3 baseline update failed: %s", e)

        result = T3Result(
            period_summary=parsed.get("period_summary", ""),
            sessions_analyzed=int(parsed.get("sessions_analyzed", len(session_narratives))),
            features_worked=parsed.get("features_worked", []),
            features_failed=parsed.get("features_failed", []),
            recurring_patterns=parsed.get("recurring_patterns", []),
            cost_summary=parsed.get("cost_summary", {}),
            regression_detected=bool(parsed.get("regression_detected", False)),
            regression_detail=parsed.get("regression_detail", ""),
            action_items=parsed.get("action_items", []),
            baselines_updated=baselines_updated,
            threshold_recommendations=threshold_recs,
            session_narratives=session_narratives,
            model=self.config.ollama_model_deep,
            inference_duration_ms=infer_ms,
            raw_response=raw,
        )

        # Step 6: push all outputs
        self._push_outputs(result, end_ns, trigger)
        self._annotate_grafana(result, trigger)

        logger.info(
            "T3 [%s/%s]: %d sessions, %d patterns, regression=%s, baselines=%s, %dms",
            mode, trigger,
            result.sessions_analyzed,
            len(result.recurring_patterns),
            result.regression_detected,
            result.baselines_updated,
            infer_ms,
        )
        return result

    # ── Private ───────────────────────────────────────────────────────────────

    def _load_evidence_packets(self, start_ns: int, end_ns: int) -> list[dict]:
        logql = '{app="sim-steward", event="sentinel_evidence_packet"}'
        try:
            return self.loki.query_lines(logql, start_ns, end_ns, limit=200)
        except Exception as e:
            logger.warning("T3 evidence packet load failed: %s", e)
            return []

    def _load_investigations(self, start_ns: int, end_ns: int) -> list[dict]:
        logql = '{app="sim-steward", event="sentinel_t2_investigation"}'
        try:
            return self.loki.query_lines(logql, start_ns, end_ns, limit=50)
        except Exception as e:
            logger.warning("T3 investigation load failed: %s", e)
            return []

    def _fetch_sentry_context(self) -> tuple[str, str]:
        issues_text = "(Sentry unavailable)"
        releases_text = "(no release data)"
        try:
            issues = self.sentry.search_issues(query="is:unresolved", limit=20)
            if issues:
                lines = [
                    f"  [{i.get('level', '?').upper()}] {i.get('title', '?')[:80]}"
                    f" (times_seen={i.get('count', '?')}, last={i.get('lastSeen', '?')[:10]})"
                    for i in issues
                ]
                issues_text = "\n".join(lines)
            else:
                issues_text = "(no open Sentry issues)"
        except Exception as e:
            logger.debug("T3 Sentry issues fetch failed: %s", e)

        try:
            releases = self.sentry.find_releases(limit=5)
            if releases:
                lines = [
                    f"  {r.get('version', '?')} released {r.get('dateCreated', '?')[:10]}"
                    for r in releases
                ]
                releases_text = "\n".join(lines)
            else:
                releases_text = "(no releases found)"
        except Exception as e:
            logger.debug("T3 Sentry releases fetch failed: %s", e)

        return issues_text, releases_text

    def _push_outputs(self, result: T3Result, end_ns: int, trigger: str) -> None:
        # Push synthesis summary
        try:
            self.loki.push_synthesis(result, trigger=trigger, env=self.config.env_label)
        except Exception as e:
            logger.warning("T3: failed to push synthesis to Loki: %s", e)

        # Push per-session narratives
        for narrative in result.session_narratives:
            try:
                self.loki.push_narrative(narrative, env=self.config.env_label)
            except Exception as e:
                logger.debug("T3: failed to push narrative for %s: %s", narrative.get("session_id"), e)

        # Push threshold recommendations
        for rec in result.threshold_recommendations:
            try:
                self.loki.push_threshold_recommendation(rec, env=self.config.env_label)
            except Exception as e:
                logger.debug("T3: failed to push threshold rec: %s", e)

    def _annotate_grafana(self, result: T3Result, trigger: str) -> None:
        try:
            regression_note = f" ⚠️ Regression: {result.regression_detail[:60]}" if result.regression_detected else ""
            self.grafana.annotate_raw(
                title=f"T3 Synthesis [{self.config.sentinel_mode}]: {result.sessions_analyzed} sessions",
                text=(
                    f"{result.period_summary[:200]}{regression_note}<br>"
                    f"Patterns: {len(result.recurring_patterns)} | "
                    f"Baselines updated: {result.baselines_updated} | "
                    f"Trigger: {trigger}"
                ),
                tags=["t3", "synthesis", self.config.sentinel_mode, trigger],
            )
        except Exception as e:
            logger.debug("T3 Grafana annotation failed: %s", e)


# ── Helpers ───────────────────────────────────────────────────────────────────

def _format_window(start_ns: int, end_ns: int, mode: str) -> str:
    start_dt = datetime.fromtimestamp(start_ns / 1e9, tz=timezone.utc)
    end_dt = datetime.fromtimestamp(end_ns / 1e9, tz=timezone.utc)
    return (
        f"{start_dt.strftime('%Y-%m-%d %H:%M')} – {end_dt.strftime('%H:%M')} UTC "
        f"({int((end_ns - start_ns) / 3.6e12):.0f}h window, mode={mode})"
    )


def _format_evidence_summary(packets: list[dict]) -> str:
    if not packets:
        return "  (none)"
    lines = []
    for p in packets[:20]:
        lines.append(
            f"  [{p.get('severity', '?').upper()}] {p.get('anomaly_description', '')[:80]}"
        )
    if len(packets) > 20:
        lines.append(f"  [... {len(packets) - 20} more]")
    return "\n".join(lines)


def _format_investigation_summary(investigations: list[dict]) -> str:
    if not investigations:
        return "  (none)"
    lines = []
    for inv in investigations[:10]:
        lines.append(
            f"  [{inv.get('confidence', '?')}] {inv.get('root_cause', '')[:80]}"
            f" (type={inv.get('issue_type', '?')})"
        )
    return "\n".join(lines)


def _format_narratives_for_prompt(session_narratives: list[dict]) -> str:
    if not session_narratives:
        return "  (no session narratives available — no invocations this window)"
    parts = []
    for n in session_narratives[:10]:
        parts.append(n.get("narrative_text", "")[:600])
    return "\n\n".join(parts)
