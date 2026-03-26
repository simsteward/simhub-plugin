"""Grafana HTTP API client for annotations."""

import logging
import time

import requests

logger = logging.getLogger("sentinel.grafana")


class GrafanaClient:
    def __init__(self, base_url: str, user: str = "admin", password: str = "admin"):
        self.base_url = base_url.rstrip("/")
        self.auth = (user, password)

    def annotate(self, finding):
        try:
            requests.post(
                f"{self.base_url}/api/annotations",
                auth=self.auth,
                json={
                    "time": int(time.time() * 1000),
                    "tags": ["log-sentinel", finding.detector, finding.severity, finding.category],
                    "text": f"<b>[{finding.severity.upper()}] {finding.title}</b><br>{finding.summary}",
                },
                timeout=5,
            )
        except Exception as e:
            logger.debug("Grafana annotation error: %s", e)

    def annotate_investigation(self, investigation):
        try:
            requests.post(
                f"{self.base_url}/api/annotations",
                auth=self.auth,
                json={
                    "time": int(time.time() * 1000),
                    "tags": ["log-sentinel", "investigation", investigation.finding.detector, investigation.confidence, investigation.trigger],
                    "text": (
                        f"<b>Investigation: {investigation.finding.title}</b><br>"
                        f"<b>Root cause:</b> {investigation.root_cause}<br>"
                        f"<b>Recommendation:</b> {investigation.recommendation}<br>"
                        f"<em>Confidence: {investigation.confidence} | Model: {investigation.model} | Type: {investigation.issue_type}</em>"
                    ),
                },
                timeout=5,
            )
        except Exception as e:
            logger.debug("Grafana investigation annotation error: %s", e)
