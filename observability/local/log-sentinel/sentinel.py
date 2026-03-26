"""Log Sentinel main loop — parallel detectors, async T2, dedup, circuit breakers, 100% logging."""

import logging
import queue
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor, as_completed

import schedule

from circuit_breaker import CircuitBreaker
from config import Config
from grafana_client import GrafanaClient
from loki_client import LokiClient
from models import Finding, TimeWindow
from query_cache import CycleQueryCache
from sentry_client import SentryClient

from detectors.error_spike import ErrorSpikeDetector
from detectors.action_failure import ActionFailureDetector
from detectors.websocket_health import WebSocketHealthDetector
from detectors.silent_session import SilentSessionDetector
from detectors.flow_gap import FlowGapDetector
from detectors.stuck_user import StuckUserDetector
from detectors.incident_anomaly import IncidentAnomalyDetector
from detectors.plugin_lifecycle import PluginLifecycleDetector
from detectors.resource_health import ResourceHealthDetector
from detectors.session_quality import SessionQualityDetector
from detectors.claude_session import ClaudeSessionDetector
from detectors.mcp_health import McpHealthDetector
from detectors.agent_loop import AgentLoopDetector
from detectors.tool_patterns import ToolPatternsDetector
from detectors.token_usage import TokenUsageDetector
from detectors.sentinel_health import SentinelHealthDetector

from flows.engine import FlowEngine
from investigator.chain import InvestigationChain

logger = logging.getLogger("sentinel")


class Sentinel:
    def __init__(self, config: Config):
        self.config = config
        self.loki = LokiClient(config.loki_url)
        self.grafana = GrafanaClient(config.grafana_url, config.grafana_user, config.grafana_password)
        self.sentry = SentryClient(config.sentry_dsn, config.env_label)
        self.cache = CycleQueryCache(self.loki)
        self.flow_engine = FlowEngine("flows/definitions")

        # Circuit breakers
        self.loki_breaker = CircuitBreaker("loki", failure_threshold=3, backoff_sec=60)
        self.ollama_breaker = CircuitBreaker("ollama", failure_threshold=3, backoff_sec=120)

        # In-memory stats for sentinel_health detector (avoids circular Loki query)
        self._stats = {
            "last_cycle_duration_ms": 0,
            "consecutive_detector_errors": 0,
            "last_t2_duration_ms": 0,
            "t2_queue_size": 0,
            "cycles_completed": 0,
        }

        # Detectors — app category
        self.detectors = [
            ErrorSpikeDetector(),
            ActionFailureDetector(),
            WebSocketHealthDetector(),
            SilentSessionDetector(),
            FlowGapDetector(self.flow_engine),
            StuckUserDetector(),
            IncidentAnomalyDetector(),
            PluginLifecycleDetector(),
            ResourceHealthDetector(),
            SessionQualityDetector(),
            # ops category
            ClaudeSessionDetector(),
            McpHealthDetector(),
            AgentLoopDetector(),
            ToolPatternsDetector(),
            TokenUsageDetector(),
            SentinelHealthDetector(self._stats),
        ]

        # T2 investigator
        self.investigator = None
        self._t2_queue: queue.Queue = queue.Queue()
        if config.t2_enabled:
            self.investigator = InvestigationChain(
                ollama_url=config.ollama_url,
                model_fast=config.ollama_model_fast,
                model_deep=config.ollama_model_deep,
                loki=self.loki,
            )

        # Dedup: fingerprint → last_seen_timestamp
        self._seen_fingerprints: dict[str, float] = {}
        # T2 dedup: fingerprint → last_investigated_time
        self._investigated_fingerprints: dict[str, float] = {}
        self._proactive_hash: str = ""

        self._cycle_count = 0

    # ── T1 Cycle ──

    def run_cycle(self):
        """Single T1 detection cycle with parallel execution and 100% logging."""
        cycle_id = str(uuid.uuid4())[:8]
        self._cycle_count += 1
        cycle_start = time.time()

        window = TimeWindow.from_now(self.config.lookback_sec)

        # Populate shared query cache (one Loki call per query key)
        if not self.loki_breaker.allow_request():
            logger.warning("Cycle #%d skipped: Loki circuit open", self._cycle_count)
            return

        try:
            self.cache.populate(window)
            self.loki_breaker.record_success()
        except Exception as e:
            self.loki_breaker.record_failure()
            logger.error("Cache populate failed: %s", e)
            return

        # Run all detectors in parallel
        all_findings: list[Finding] = []
        detector_errors = 0

        with ThreadPoolExecutor(max_workers=4) as pool:
            futures = {
                pool.submit(self._run_detector, det, cycle_id): det
                for det in self.detectors
            }
            for future in as_completed(futures):
                det = futures[future]
                try:
                    findings = future.result()
                    all_findings.extend(findings)
                except Exception as e:
                    detector_errors += 1
                    logger.error("Detector %s failed: %s", det.name, e)

        # Update stats for sentinel_health
        self._stats["consecutive_detector_errors"] = (
            self._stats["consecutive_detector_errors"] + detector_errors
            if detector_errors > 0 else 0
        )

        # Dedup and process findings — priority order: critical, warn, info
        all_findings.sort(key=lambda f: {"critical": 0, "warn": 1, "info": 2}.get(f.severity, 3))

        escalated = 0
        suppressed = 0
        for finding in all_findings:
            if self._is_duplicate(finding):
                suppressed += 1
                continue

            self.loki.push_finding(finding, self.config.env_label)

            if finding.severity in ("warn", "critical"):
                self.grafana.annotate(finding)

            # Critical findings → Sentry immediately
            if finding.severity == "critical":
                event_id = self.sentry.create_issue(finding)
                if event_id:
                    self.loki.push_sentry_event({
                        "finding_id": finding.finding_id,
                        "sentry_event_id": event_id,
                        "title": finding.title,
                        "level": "error",
                    }, self.config.env_label)

            # Escalate to T2 (non-blocking, with dedup)
            if finding.escalate_to_t2 and self.investigator:
                fp = finding.fingerprint
                last_inv = self._investigated_fingerprints.get(fp, 0)
                if time.time() - last_inv < 900:  # 15 min T2 dedup window
                    logger.debug("T2 dedup: skipping %s (investigated %ds ago)", fp[:8], int(time.time() - last_inv))
                else:
                    self._investigated_fingerprints[fp] = time.time()
                    escalated += 1
                    self._t2_queue.put(("escalation", finding))

        # Emit cycle metrics
        cycle_duration_ms = int((time.time() - cycle_start) * 1000)
        self._stats["last_cycle_duration_ms"] = cycle_duration_ms
        self._stats["cycles_completed"] = self._cycle_count
        self._stats["t2_queue_size"] = self._t2_queue.qsize()

        app_findings = sum(1 for f in all_findings if f.category == "app" and not self._is_duplicate(f))
        ops_findings = sum(1 for f in all_findings if f.category == "ops" and not self._is_duplicate(f))

        self.loki.push_cycle({
            "cycle_id": cycle_id,
            "cycle_num": self._cycle_count,
            "duration_ms": cycle_duration_ms,
            "finding_count": len(all_findings) - suppressed,
            "suppressed_count": suppressed,
            "escalated_count": escalated,
            "error_count": detector_errors,
            "app_findings": app_findings,
            "ops_findings": ops_findings,
            "cache_queries": self.cache.stats["queries"],
            "cache_lines": self.cache.stats["total_lines"],
        }, self.config.env_label)

        logger.info(
            "Cycle #%d: %d findings (%d suppressed), %d escalated, %d errors, %dms",
            self._cycle_count, len(all_findings) - suppressed, suppressed,
            escalated, detector_errors, cycle_duration_ms,
        )

    def _run_detector(self, detector, cycle_id: str) -> list[Finding]:
        """Run a single detector with timing and logging."""
        start = time.time()
        error_msg = None
        findings = []
        try:
            findings = detector.detect(self.cache)
        except Exception as e:
            error_msg = str(e)
            raise
        finally:
            duration_ms = int((time.time() - start) * 1000)
            self.loki.push_detector_run({
                "cycle_id": cycle_id,
                "detector": detector.name,
                "category": detector.category,
                "duration_ms": duration_ms,
                "finding_count": len(findings),
                "error": error_msg,
            }, self.config.env_label)
        return findings

    # ── Dedup ──

    def _is_duplicate(self, finding: Finding) -> bool:
        fp = finding.fingerprint
        now = time.time()
        last_seen = self._seen_fingerprints.get(fp)
        if last_seen and (now - last_seen) < self.config.dedup_window_sec:
            return True
        self._seen_fingerprints[fp] = now
        # Clean old entries
        cutoff = now - self.config.dedup_window_sec * 2
        self._seen_fingerprints = {
            k: v for k, v in self._seen_fingerprints.items() if v > cutoff
        }
        return False

    # ── T2 Background Thread ──

    def _t2_worker(self):
        """Background thread that processes T2 investigations from the queue."""
        logger.info("T2 worker started")
        while True:
            try:
                trigger, payload = self._t2_queue.get(timeout=5)
            except queue.Empty:
                continue

            if not self.ollama_breaker.allow_request():
                logger.warning("T2 skipped: Ollama circuit open")
                self._t2_queue.task_done()
                continue

            try:
                if trigger == "escalation":
                    investigation = self.investigator.investigate(payload)
                elif trigger == "proactive":
                    investigation = self.investigator.investigate_patterns(payload)
                else:
                    self._t2_queue.task_done()
                    continue

                self.ollama_breaker.record_success()

                # Push results
                self.loki.push_investigation(investigation, self.config.env_label)
                self.grafana.annotate_investigation(investigation)
                self.loki.push_t2_run({
                    "investigation_id": investigation.investigation_id,
                    "finding_id": investigation.finding.finding_id if trigger == "escalation" else "proactive",
                    "trigger": trigger,
                    "tier": f"t2_{'deep' if investigation.model == self.config.ollama_model_deep else 'fast'}",
                    "model": investigation.model,
                    "gather_duration_ms": investigation.gather_duration_ms,
                    "inference_duration_ms": investigation.inference_duration_ms,
                    "total_duration_ms": investigation.gather_duration_ms + investigation.inference_duration_ms,
                    "context_lines": investigation.context_lines_gathered,
                    "confidence": investigation.confidence,
                    "issue_type": investigation.issue_type,
                    "escalated_to_deep": investigation.model == self.config.ollama_model_deep,
                }, self.config.env_label)

                # T2 investigations → Sentry
                sentry_id = self.sentry.create_investigation_issue(investigation)
                if sentry_id:
                    self.loki.push_sentry_event({
                        "investigation_id": investigation.investigation_id,
                        "sentry_event_id": sentry_id,
                        "title": investigation.root_cause[:100],
                        "level": "error" if investigation.finding.severity == "critical" else "warning",
                    }, self.config.env_label)

                logger.info(
                    "T2 complete [%s]: %s confidence=%s model=%s type=%s",
                    trigger, investigation.investigation_id[:8],
                    investigation.confidence, investigation.model, investigation.issue_type,
                )
                self._stats["last_t2_duration_ms"] = investigation.gather_duration_ms + investigation.inference_duration_ms

            except Exception as e:
                self.ollama_breaker.record_failure()
                logger.error("T2 investigation failed: %s", e)
            finally:
                self._t2_queue.task_done()
                self._stats["t2_queue_size"] = self._t2_queue.qsize()

    def _t2_proactive_poll(self):
        """Periodically query L1 findings and ask T2 to analyze patterns."""
        import hashlib
        if not self.investigator:
            return
        window = TimeWindow.from_now(self.config.t2_proactive_interval_sec)
        findings = self.loki.query_lines(
            '{app="sim-steward", component="log-sentinel", event="sentinel_finding"} | json',
            window.start_ns, window.end_ns, limit=100,
        )
        if len(findings) >= 3:
            # Dedup: skip if same finding set as last poll
            fps = sorted(set(f.get("fingerprint", "") for f in findings))
            set_hash = hashlib.sha256("|".join(fps).encode()).hexdigest()[:16]
            if set_hash == self._proactive_hash:
                logger.debug("T2 proactive dedup: same finding set, skipping")
                return
            self._proactive_hash = set_hash
            logger.info("T2 proactive: analyzing %d recent findings", len(findings))
            self._t2_queue.put(("proactive", findings))

    # ── Lifecycle ──

    def start(self):
        """Start all loops."""
        logger.info(
            "Sentinel v2 started: %d detectors (app=%d ops=%d), poll %ds, lookback %ds, T2 %s, models: fast=%s deep=%s",
            len(self.detectors),
            sum(1 for d in self.detectors if d.category == "app"),
            sum(1 for d in self.detectors if d.category == "ops"),
            self.config.poll_interval_sec,
            self.config.lookback_sec,
            "enabled" if self.investigator else "disabled",
            self.config.ollama_model_fast,
            self.config.ollama_model_deep,
        )

        # Start T2 background worker
        if self.investigator:
            t2_thread = threading.Thread(target=self._t2_worker, daemon=True)
            t2_thread.start()

        # Run first cycle immediately
        self.run_cycle()

        # Schedule recurring
        schedule.every(self.config.poll_interval_sec).seconds.do(self.run_cycle)
        if self.investigator:
            schedule.every(self.config.t2_proactive_interval_sec).seconds.do(self._t2_proactive_poll)

        while True:
            schedule.run_pending()
            time.sleep(1)
