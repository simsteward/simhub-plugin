"""Sentry SDK wrapper — create issues, read history, and capture behavioral findings.

v3 additions:
  - traces_sample_rate bumped to 1.0 (enable transactions)
  - search_issues() — REST API read for T2/T3 history queries
  - find_releases() — REST API read for T3 regression detection
  - capture_behavioral_finding() — T2 writes behavioral patterns not captured by SDK
"""

import logging
import requests

logger = logging.getLogger("sentinel.sentry")

_sdk_available = False
try:
    import sentry_sdk
    _sdk_available = True
except ImportError:
    logger.warning("sentry-sdk not installed, Sentry integration disabled")


class SentryClient:
    def __init__(
        self,
        dsn: str,
        env: str = "local",
        auth_token: str = "",
        org: str = "",
        project: str = "",
    ):
        self.enabled = bool(dsn) and _sdk_available
        self._auth_token = auth_token
        self._org = org
        self._project = project
        self._api_enabled = bool(auth_token and org and project)

        if self.enabled:
            sentry_sdk.init(
                dsn=dsn,
                environment=env,
                traces_sample_rate=1.0,
                send_default_pii=False,
            )
            logger.info("Sentry initialized (env=%s)", env)
        else:
            if dsn and not _sdk_available:
                logger.warning("Sentry DSN set but sentry-sdk not installed")
            elif not dsn:
                logger.info("Sentry disabled (no DSN)")

    def create_issue(self, finding) -> str | None:
        """Create Sentry issue for a critical finding. Returns event_id or None."""
        if not self.enabled:
            return None
        try:
            with sentry_sdk.new_scope() as scope:
                scope.set_tag("detector", finding.detector)
                scope.set_tag("category", finding.category)
                scope.set_tag("severity", finding.severity)
                scope.set_tag("issue_type", "unknown")
                scope.set_context("finding", {
                    "finding_id": finding.finding_id,
                    "fingerprint": finding.fingerprint,
                    "summary": finding.summary,
                    "logql_query": finding.logql_query,
                    "evidence": finding.evidence,
                })
                scope.fingerprint = [finding.detector, finding.fingerprint]
                event_id = sentry_sdk.capture_message(
                    f"[CRITICAL] {finding.title}",
                    level="error",
                    scope=scope,
                )
            logger.info("Sentry issue created for finding %s: %s", finding.finding_id[:8], event_id)
            return event_id
        except Exception as e:
            logger.warning("Sentry create_issue failed: %s", e)
            return None

    def capture_behavioral_finding(
        self,
        title: str,
        issue_type: str,
        recommendation: str,
        confidence: str,
        fingerprint: str,
        context: dict,
    ) -> str | None:
        """Create Sentry issue for a T2 behavioral finding (not captured by SDK).

        Only call this for patterns that wouldn't surface as clean exceptions:
        e.g. 'WebSocket always drops after 20min replay', 'incident detection stalls
        after session_num > 3'. Do NOT use for things already covered by SDK capture.
        """
        if not self.enabled:
            return None
        try:
            level = "error" if confidence == "high" else "warning"
            with sentry_sdk.new_scope() as scope:
                scope.set_tag("issue_type", issue_type)
                scope.set_tag("confidence", confidence)
                scope.set_tag("source", "t2_behavioral")
                scope.set_context("finding", {
                    "recommendation": recommendation,
                    **context,
                })
                scope.fingerprint = ["t2.behavioral", fingerprint]
                event_id = sentry_sdk.capture_message(
                    f"[T2] {title}",
                    level=level,
                    scope=scope,
                )
            logger.info("Sentry behavioral finding created: %s", event_id)
            return event_id
        except Exception as e:
            logger.warning("Sentry capture_behavioral_finding failed: %s", e)
            return None

    # ── REST API read methods ──────────────────────────────────────────────────

    def search_issues(self, query: str = "is:unresolved", limit: int = 10) -> list[dict]:
        """Search Sentry issues via REST API. Returns list of issue dicts."""
        if not self._api_enabled:
            return []
        try:
            resp = requests.get(
                f"https://sentry.io/api/0/projects/{self._org}/{self._project}/issues/",
                headers={"Authorization": f"Bearer {self._auth_token}"},
                params={"query": query, "limit": limit},
                timeout=10,
            )
            if resp.status_code == 200:
                return resp.json()
            logger.debug("Sentry search_issues HTTP %d: %s", resp.status_code, resp.text[:200])
        except Exception as e:
            logger.debug("Sentry search_issues failed: %s", e)
        return []

    def get_issue(self, issue_id: str) -> dict:
        """Fetch a single Sentry issue by ID."""
        if not self._api_enabled:
            return {}
        try:
            resp = requests.get(
                f"https://sentry.io/api/0/issues/{issue_id}/",
                headers={"Authorization": f"Bearer {self._auth_token}"},
                timeout=10,
            )
            if resp.status_code == 200:
                return resp.json()
        except Exception as e:
            logger.debug("Sentry get_issue failed: %s", e)
        return {}

    def find_releases(self, limit: int = 5) -> list[dict]:
        """Fetch recent releases for regression detection in T3."""
        if not self._api_enabled:
            return []
        try:
            resp = requests.get(
                f"https://sentry.io/api/0/projects/{self._org}/{self._project}/releases/",
                headers={"Authorization": f"Bearer {self._auth_token}"},
                params={"limit": limit},
                timeout=10,
            )
            if resp.status_code == 200:
                return resp.json()
        except Exception as e:
            logger.debug("Sentry find_releases failed: %s", e)
        return []

    def create_release(self, version: str) -> dict:
        """Create a Sentry release (called from deploy.ps1 via this client)."""
        if not self._api_enabled:
            return {}
        try:
            resp = requests.post(
                f"https://sentry.io/api/0/organizations/{self._org}/releases/",
                headers={"Authorization": f"Bearer {self._auth_token}"},
                json={"version": version, "projects": [self._project]},
                timeout=10,
            )
            if resp.status_code in (200, 201):
                return resp.json()
            logger.debug("Sentry create_release HTTP %d: %s", resp.status_code, resp.text[:200])
        except Exception as e:
            logger.warning("Sentry create_release failed: %s", e)
        return {}

    def create_investigation_issue(self, investigation) -> str | None:
        """Create Sentry issue for a T2 investigation report. Returns event_id or None."""
        if not self.enabled:
            return None
        try:
            finding = investigation.finding
            level = "error" if finding.severity == "critical" else "warning"
            with sentry_sdk.new_scope() as scope:
                scope.set_tag("detector", finding.detector)
                scope.set_tag("category", finding.category)
                scope.set_tag("severity", finding.severity)
                scope.set_tag("model", investigation.model)
                scope.set_tag("confidence", investigation.confidence)
                scope.set_tag("issue_type", investigation.issue_type)
                scope.set_tag("trigger", investigation.trigger)
                scope.set_context("investigation", {
                    "investigation_id": investigation.investigation_id,
                    "finding_id": finding.finding_id,
                    "root_cause": investigation.root_cause,
                    "correlation": investigation.correlation,
                    "impact": investigation.impact,
                    "recommendation": investigation.recommendation,
                    "inference_duration_ms": investigation.inference_duration_ms,
                })
                scope.set_context("finding", {
                    "title": finding.title,
                    "summary": finding.summary,
                    "evidence": finding.evidence,
                })
                scope.fingerprint = [finding.detector, investigation.root_cause[:50]]
                event_id = sentry_sdk.capture_message(
                    f"[T2] {investigation.root_cause[:120]}",
                    level=level,
                    scope=scope,
                )
            logger.info("Sentry investigation issue for %s: %s", investigation.investigation_id[:8], event_id)
            return event_id
        except Exception as e:
            logger.warning("Sentry create_investigation_issue failed: %s", e)
            return None
