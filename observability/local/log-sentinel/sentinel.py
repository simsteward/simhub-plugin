"""Log Sentinel v3 — main cycle orchestrator."""

import logging
import time
import uuid
from dataclasses import dataclass

import schedule

from baseline import BaselineManager
from circuit_breaker import CircuitBreaker
from config import Config
from evidence import EvidenceBuilder
from grafana_client import GrafanaClient
from loki_client import LokiClient
from ollama_client import OllamaClient
from sentry_client import SentryClient
from t1_agent import T1Agent, T1Result
from t2_agent import T2Agent, T2Result
from t3_agent import T3Agent
from timeline import TimelineBuilder
from trace import InvocationBuilder

logger = logging.getLogger("sentinel")


@dataclass
class CycleResult:
    cycle_id: str
    cycle_num: int
    window_minutes: int
    t1: T1Result | None
    timeline_event_count: int
    anomaly_count: int
    duration_ms: int
    error: str | None = None


class Sentinel:
    def __init__(self, config: Config):
        self.config = config

        self.loki = LokiClient(config.loki_url)
        self.ollama = OllamaClient(config.ollama_url)
        self.grafana = GrafanaClient(config.grafana_url, config.grafana_user, config.grafana_password)
        self.sentry = SentryClient(config.sentry_dsn, config.env_label)

        self.loki_breaker = CircuitBreaker("loki", failure_threshold=3, backoff_sec=60)
        self.ollama_breaker = CircuitBreaker("ollama", failure_threshold=3, backoff_sec=120)

        self.baseline = BaselineManager(self.loki, config.baseline_path)
        self.evidence_builder = EvidenceBuilder(self.loki)
        self.invocation_builder = InvocationBuilder()
        self.timeline_builder = TimelineBuilder(self.loki, self.loki_breaker)

        self.t1_agent = T1Agent(
            self.ollama, self.loki, self.ollama_breaker, config,
            self.baseline, self.evidence_builder,
        )
        self.t2_agent = T2Agent(
            self.ollama, self.loki, self.grafana, self.sentry,
            self.ollama_breaker, config,
        )
        self.t3_agent = T3Agent(
            self.ollama, self.loki, self.grafana, self.sentry,
            self.baseline, self.ollama_breaker, config,
        )

        self._cycle_num = 0
        self._trigger_dedup: dict[str, float] = {}  # alertname → last trigger time.time()
        self._stats = {
            "cycles_completed": 0,
            "total_anomalies": 0,
            "last_cycle_duration_ms": 0,
            "last_t1_duration_ms": 0,
            "last_t2_run_ts": 0,
            "last_t3_run_ts": 0,
        }

    # ── Public ───────────────────────────────────────────────────────────────

    def start(self):
        """Blocking schedule loop."""
        logger.info(
            "Sentinel v3 started: mode=%s t1=%ds t2=%ds t3=%ds fast=%s deep=%s",
            self.config.sentinel_mode,
            self.config.t1_interval_sec,
            self.config.t2_interval_sec,
            self.config.t3_interval_sec,
            self.config.ollama_model_fast,
            self.config.ollama_model_deep,
        )
        self.run_cycle()
        schedule.every(self.config.t1_interval_sec).seconds.do(self.run_cycle)
        schedule.every(self.config.t2_interval_sec).seconds.do(self.run_t2_cycle)
        schedule.every(self.config.t3_interval_sec).seconds.do(self.run_t3_cycle)
        while True:
            schedule.run_pending()
            time.sleep(1)

    def run_cycle(self) -> CycleResult:
        """T1 analysis cycle. Always returns CycleResult."""
        self._cycle_num += 1
        cycle_id = str(uuid.uuid4())[:8]
        cycle_start = time.time()

        end_ns = self.loki.now_ns()
        start_ns = end_ns - int(self.config.lookback_sec * 1e9)
        window_minutes = max(1, self.config.lookback_sec // 60)

        logger.info("Cycle #%d [%s] start: window=%dmin", self._cycle_num, cycle_id, window_minutes)

        t1 = None
        timeline_events = []
        error = None

        try:
            # 1. Gather
            counts, samples = self._gather(start_ns, end_ns)

            # 2. Build timeline + invocations
            timeline_events = self.timeline_builder.build(start_ns, end_ns)
            tl_stats = self.timeline_builder.get_stats(timeline_events)
            self.loki.push_timeline({
                **tl_stats,
                "cycle_id": cycle_id,
                "truncated": tl_stats["event_count"] > 60,
            }, self.config.env_label)

            invocations = self.invocation_builder.build(timeline_events)

            # 3. T1 analysis
            if not self.ollama_breaker.allow_request():
                logger.warning("T1 skipped: Ollama circuit open")
            else:
                t1 = self.t1_agent.run(
                    start_ns, end_ns, counts,
                    samples["sim-steward"],
                    samples["claude-dev-logging"],
                    samples["claude-token-metrics"],
                    invocations=invocations,
                    trigger_source="scheduled",
                )
                self.loki.push_analyst_run({
                    "cycle_id": cycle_id,
                    "tier": "t1",
                    "model": t1.model,
                    "think_mode": True,
                    "duration_ms": t1.total_duration_ms,
                    "summary_duration_ms": t1.summary_duration_ms,
                    "anomaly_duration_ms": t1.anomaly_duration_ms,
                    "anomaly_count": len(t1.anomalies),
                    "needs_t2_count": sum(1 for a in t1.anomalies if a.get("needs_t2")),
                    "evidence_packet_count": len(t1.evidence_packets),
                    "invocation_count": len(t1.invocations),
                    "window_minutes": window_minutes,
                    "trigger_source": t1.trigger_source,
                }, self.config.env_label)

        except Exception as e:
            error = str(e)
            logger.error("Cycle #%d error: %s", self._cycle_num, e)

        duration_ms = int((time.time() - cycle_start) * 1000)
        result = CycleResult(
            cycle_id=cycle_id,
            cycle_num=self._cycle_num,
            window_minutes=window_minutes,
            t1=t1,
            timeline_event_count=len(timeline_events),
            anomaly_count=len(t1.anomalies) if t1 else 0,
            duration_ms=duration_ms,
            error=error,
        )

        self.loki.push_cycle({
            "cycle_id": cycle_id,
            "cycle_num": self._cycle_num,
            "window_minutes": window_minutes,
            "t1_duration_ms": t1.total_duration_ms if t1 else 0,
            "anomaly_count": result.anomaly_count,
            "evidence_packet_count": len(t1.evidence_packets) if t1 else 0,
            "timeline_event_count": len(timeline_events),
            "total_duration_ms": duration_ms,
            "error": error,
        }, self.config.env_label)

        self._stats["cycles_completed"] = self._cycle_num
        self._stats["last_cycle_duration_ms"] = duration_ms
        self._stats["last_t1_duration_ms"] = t1.total_duration_ms if t1 else 0
        if t1:
            self._stats["total_anomalies"] += result.anomaly_count

        logger.info(
            "Cycle #%d complete: %d anomalies %d evidence_packets %dms",
            self._cycle_num, result.anomaly_count,
            len(t1.evidence_packets) if t1 else 0, duration_ms,
        )
        return result

    def run_t2_cycle(self) -> None:
        """Independent T2 investigation cycle — pulls evidence packets from Loki."""
        if not self.ollama_breaker.allow_request():
            logger.warning("T2 cycle skipped: Ollama circuit open")
            return
        logger.info("T2 cycle starting")
        try:
            result = self.t2_agent.run()
            self._stats["last_t2_run_ts"] = int(time.time())
            if result:
                logger.info(
                    "T2 cycle complete: confidence=%s sentry=%s %dms",
                    result.confidence, result.sentry_worthy, result.total_duration_ms,
                )
        except Exception as e:
            logger.error("T2 cycle error: %s", e)

    def run_t3_cycle(self) -> None:
        """Independent T3 synthesis cycle — runs on slow cadence."""
        if not self.ollama_breaker.allow_request():
            logger.warning("T3 cycle skipped: Ollama circuit open")
            return
        logger.info("T3 cycle starting (mode=%s)", self.config.sentinel_mode)
        try:
            result = self.t3_agent.run(trigger="scheduled")
            self._stats["last_t3_run_ts"] = int(time.time())
            logger.info(
                "T3 cycle complete: %d sessions, regression=%s, %dms",
                result.sessions_analyzed, result.regression_detected, result.inference_duration_ms,
            )
        except Exception as e:
            logger.error("T3 cycle error: %s", e)

    def trigger_cycle(
        self,
        alert_context: str,
        trigger_tier: str,
        alert_names: list[str],
        lookback_sec: int = 1800,
    ) -> None:
        """Alert-driven cycle — called from /trigger webhook, runs in background thread."""
        logger.info(
            "Trigger cycle: tier=%s alerts=%s lookback=%ds",
            trigger_tier, alert_names, lookback_sec,
        )
        end_ns = self.loki.now_ns()
        start_ns = end_ns - lookback_sec * 1_000_000_000

        try:
            counts, samples = self._gather(start_ns, end_ns)
            timeline_events = self.timeline_builder.build(start_ns, end_ns)
            invocations = self.invocation_builder.build(timeline_events)
        except Exception as e:
            logger.error("Trigger cycle gather failed: %s", e)
            return

        if not self.ollama_breaker.allow_request():
            logger.warning("Trigger cycle T1 skipped: Ollama circuit open")
            return

        t1 = None
        try:
            t1 = self.t1_agent.run(
                start_ns, end_ns, counts,
                samples["sim-steward"],
                samples["claude-dev-logging"],
                samples["claude-token-metrics"],
                invocations=invocations,
                alert_context=alert_context,
                trigger_source="grafana_alert",
                alert_names=alert_names,
            )
            logger.info(
                "Trigger T1 complete: %d anomalies, %d evidence_packets, %dms",
                len(t1.anomalies), len(t1.evidence_packets), t1.total_duration_ms,
            )
        except Exception as e:
            logger.error("Trigger cycle T1 failed: %s", e)

        # For t2-tier alerts, skip needs_t2 gate — escalate immediately
        if trigger_tier == "t2" and self.config.t2_enabled:
            if not self.ollama_breaker.allow_request():
                logger.warning("Trigger cycle T2 skipped: Ollama circuit open")
                return
            try:
                forced_ids = [ep.anomaly_id for ep in t1.evidence_packets] if t1 else None
                result = self.t2_agent.run(forced_packet_ids=forced_ids)
                self._stats["last_t2_run_ts"] = int(time.time())
                if result:
                    logger.info(
                        "Trigger T2 complete: confidence=%s sentry=%s %dms",
                        result.confidence, result.sentry_worthy, result.total_duration_ms,
                    )
            except Exception as e:
                logger.error("Trigger cycle T2 failed: %s", e)

    # ── Private ──────────────────────────────────────────────────────────────

    def _gather(self, start_ns: int, end_ns: int) -> tuple[dict, dict]:
        """Fetch counts and samples from all three Loki streams."""
        stream_queries = {
            "sim-steward":          '{app="sim-steward"} | json',
            "claude-dev-logging":   '{app="claude-dev-logging"} | json',
            "claude-token-metrics": '{app="claude-token-metrics"} | json',
        }

        counts = {}
        samples = {}

        if not self.loki_breaker.allow_request():
            logger.warning("Gather skipped: Loki circuit open")
            return {k: 0 for k in stream_queries}, {k: [] for k in stream_queries}

        try:
            for name, logql in stream_queries.items():
                counts[name] = self.loki.count(logql, start_ns, end_ns)
                samples[name] = self.loki.query_lines(logql, start_ns, end_ns, limit=100)
            self.loki_breaker.record_success()
        except Exception as e:
            self.loki_breaker.record_failure()
            logger.error("Gather failed: %s", e)
            for name in stream_queries:
                counts.setdefault(name, -1)
                samples.setdefault(name, [])

        return counts, samples
