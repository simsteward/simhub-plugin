"""Log Sentinel main loop — scheduler + Tier 1 detectors + Tier 2 escalation."""

import logging
import time

import schedule

from config import Config
from loki_client import LokiClient
from grafana_client import GrafanaClient
from models import TimeWindow
from detectors.error_spike import ErrorSpikeDetector
from detectors.action_failure import ActionFailureDetector
from detectors.websocket_health import WebSocketHealthDetector
from detectors.silent_session import SilentSessionDetector
from detectors.flow_gap import FlowGapDetector
from detectors.stuck_user import StuckUserDetector
from detectors.incident_anomaly import IncidentAnomalyDetector
from flows.engine import FlowEngine
from investigator.chain import InvestigationChain

logger = logging.getLogger("sentinel")


class Sentinel:
    def __init__(self, config: Config):
        self.config = config
        self.loki = LokiClient(config.loki_url)
        self.grafana = GrafanaClient(
            config.grafana_url, config.grafana_user, config.grafana_password
        )
        self.flow_engine = FlowEngine("flows/definitions")

        self.investigator = None
        if config.t2_enabled:
            self.investigator = InvestigationChain(
                ollama_url=config.ollama_url,
                model_fast=config.ollama_model_fast,
                model_deep=config.ollama_model_deep,
                loki=self.loki,
            )

        self.detectors = [
            ErrorSpikeDetector(),
            ActionFailureDetector(),
            WebSocketHealthDetector(),
            SilentSessionDetector(),
            FlowGapDetector(self.flow_engine),
            StuckUserDetector(),
            IncidentAnomalyDetector(),
        ]
        self._cycle_count = 0

    def run_cycle(self):
        """Single detection + investigation cycle."""
        self._cycle_count += 1
        window = TimeWindow.from_now(self.config.lookback_sec)
        all_findings = []

        for detector in self.detectors:
            try:
                findings = detector.detect(self.loki, window)
                all_findings.extend(findings)
            except Exception as e:
                logger.error("Detector %s failed: %s", detector.name, e)

        escalated = 0
        for finding in all_findings:
            self.loki.push_finding(finding, self.config.env_label)

            if finding.severity in ("warn", "critical"):
                self.grafana.annotate(finding)

            if finding.escalate_to_t2 and self.investigator:
                escalated += 1
                try:
                    investigation = self.investigator.investigate(finding)
                    self.loki.push_investigation(investigation, self.config.env_label)
                    self.grafana.annotate_investigation(investigation)
                    logger.info(
                        "T2 investigation complete: %s [%s] confidence=%s model=%s",
                        finding.title,
                        investigation.investigation_id,
                        investigation.confidence,
                        investigation.model,
                    )
                except Exception as e:
                    logger.error(
                        "T2 investigation failed for %s: %s", finding.finding_id, e
                    )

        logger.info(
            "Cycle #%d complete: %d findings, %d escalated",
            self._cycle_count,
            len(all_findings),
            escalated,
        )

    def start(self):
        """Start the scheduled polling loop."""
        logger.info(
            "Sentinel started: poll every %ds, lookback %ds, T2 %s, models: fast=%s deep=%s",
            self.config.poll_interval_sec,
            self.config.lookback_sec,
            "enabled" if self.investigator else "disabled",
            self.config.ollama_model_fast,
            self.config.ollama_model_deep,
        )

        # Run once immediately on startup
        self.run_cycle()

        schedule.every(self.config.poll_interval_sec).seconds.do(self.run_cycle)
        while True:
            schedule.run_pending()
            time.sleep(1)
