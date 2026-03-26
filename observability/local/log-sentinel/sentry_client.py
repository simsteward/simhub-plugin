"""Sentry SDK wrapper — create issues for critical findings and T2 investigations."""

import logging

logger = logging.getLogger("sentinel.sentry")

_sdk_available = False
try:
    import sentry_sdk
    _sdk_available = True
except ImportError:
    logger.warning("sentry-sdk not installed, Sentry integration disabled")


class SentryClient:
    def __init__(self, dsn: str, env: str = "local"):
        self.enabled = bool(dsn) and _sdk_available
        if self.enabled:
            sentry_sdk.init(
                dsn=dsn,
                environment=env,
                traces_sample_rate=0.0,
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
