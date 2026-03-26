"""Shared Loki query cache — run common queries once per cycle, share results across detectors."""

import logging
import time

from loki_client import LokiClient
from models import TimeWindow

logger = logging.getLogger("sentinel.cache")

# Predefined query keys. Each maps to a LogQL query and the app stream it targets.
QUERIES = {
    # sim-steward (app detectors)
    "ss_all": '{app="sim-steward"} | json',
    "ss_errors": '{app="sim-steward", level="ERROR"} | json',
    "ss_actions": '{app="sim-steward", event="action_result"} | json',
    "ss_lifecycle": '{app="sim-steward"} | json | event=~"plugin_started|plugin_ready|plugin_stopped|iracing_connected|iracing_disconnected|bridge_starting|bridge_start_failed|deploy_marker"',
    "ss_ws": '{app="sim-steward"} | json | event=~"ws_client_connected|ws_client_disconnected|ws_client_rejected|bridge_start_failed"',
    "ss_incidents": '{app="sim-steward", event="incident_detected"} | json',
    "ss_digests": '{app="sim-steward", event="session_digest"} | json',
    "ss_resources": '{app="sim-steward", event="host_resource_sample"} | json',
    # claude-dev-logging (ops detectors)
    "claude_all": '{app="claude-dev-logging"} | json',
    "claude_lifecycle": '{app="claude-dev-logging", component="lifecycle"} | json',
    "claude_tools": '{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
    "claude_agents": '{app="claude-dev-logging", component="agent"} | json',
    "claude_errors": '{app="claude-dev-logging", level="ERROR"} | json',
    "claude_tokens": '{app="claude-token-metrics"} | json',
    # sentinel self-monitoring
    "sentinel_findings": '{app="sim-steward", component="log-sentinel", event="sentinel_finding"} | json',
    "sentinel_cycles": '{app="sim-steward", component="log-sentinel", event="sentinel_cycle"} | json',
    "sentinel_t2": '{app="sim-steward", component="log-sentinel", event="sentinel_t2_run"} | json',
}


class CycleQueryCache:
    """Runs all predefined queries once, caches results for detector access."""

    def __init__(self, loki: LokiClient):
        self.loki = loki
        self._cache: dict[str, list[dict]] = {}
        self._durations: dict[str, int] = {}

    def populate(self, window: TimeWindow, keys: list[str] | None = None):
        """Run queries and cache results. If keys=None, run all."""
        target_keys = keys or list(QUERIES.keys())
        self._cache.clear()
        self._durations.clear()

        for key in target_keys:
            logql = QUERIES.get(key)
            if not logql:
                continue
            start = time.time()
            try:
                lines = self.loki.query_lines(logql, window.start_ns, window.end_ns, limit=1000)
                self._cache[key] = lines
            except Exception as e:
                logger.warning("Cache query '%s' failed: %s", key, e)
                self._cache[key] = []
            self._durations[key] = int((time.time() - start) * 1000)

        total = sum(len(v) for v in self._cache.values())
        logger.info(
            "Cache populated: %d queries, %d total lines, %dms",
            len(self._cache), total, sum(self._durations.values()),
        )

    def get(self, key: str) -> list[dict]:
        """Get cached results for a query key. Returns empty list if not cached."""
        return self._cache.get(key, [])

    def get_by_severity(self, key: str) -> dict[str, list[dict]]:
        """Get cached results grouped by level: errors first, then warnings, then info."""
        lines = self.get(key)
        grouped = {"ERROR": [], "WARN": [], "INFO": [], "DEBUG": []}
        for line in lines:
            level = (line.get("level") or "INFO").upper()
            grouped.setdefault(level, []).append(line)
        return grouped

    def filter(self, key: str, **field_filters) -> list[dict]:
        """Filter cached results by field values."""
        lines = self.get(key)
        results = []
        for line in lines:
            fields = line.get("fields", {})
            match = all(
                fields.get(k) == v or line.get(k) == v
                for k, v in field_filters.items()
            )
            if match:
                results.append(line)
        return results

    @property
    def stats(self) -> dict:
        return {
            "queries": len(self._cache),
            "total_lines": sum(len(v) for v in self._cache.values()),
            "durations": self._durations,
        }
