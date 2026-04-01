"""Baseline manager — rolling stats from Loki → baselines.json.

T3 calls compute_and_save() to recompute baselines from the Loki window.
T1 calls load() + get_prompt_context() to inject baseline values into its prompt.
T3 calls get_threshold_recommendations() to surface T0 alert calibration suggestions.

No ML, no LLM — simple rolling math (mean, count rates, p95 where sample size allows).
"""

import json
import logging
import os
import statistics
from datetime import datetime, timezone

from loki_client import LokiClient

logger = logging.getLogger("sentinel.baseline")

DEFAULT_PATH = "/data/baselines.json"

# Metric definitions: key, logql, how to compute the value
_METRICS = [
    {
        "key": "sim_steward.error_rate.per_min",
        "logql": '{app="sim-steward"} | json | level="ERROR"',
        "compute": "rate_per_min",
        "description": "ERROR log rate (per minute)",
    },
    {
        "key": "sim_steward.action_count.per_session",
        "logql": '{app="sim-steward"} | json | event="action_dispatched"',
        "compute": "count_per_session",
        "description": "Actions dispatched per iRacing session",
    },
    {
        "key": "sim_steward.websocket_disconnect.per_hour",
        "logql": '{app="sim-steward"} | json | event="websocket_disconnect"',
        "compute": "rate_per_hour",
        "description": "WebSocket disconnects per hour",
    },
    {
        "key": "claude.cost_per_session.mean_usd",
        "logql": '{app="claude-token-metrics"} | json',
        "compute": "field_mean",
        "field": "cost_usd",
        "description": "Mean Claude session cost (USD)",
    },
    {
        "key": "claude.tool_calls.per_session",
        "logql": '{app="claude-dev-logging"} | json | event="tool_use"',
        "compute": "count_per_session",
        "description": "Tool calls per Claude session",
    },
    {
        "key": "claude.error_rate.per_min",
        "logql": '{app="claude-dev-logging"} | json | level="ERROR"',
        "compute": "rate_per_min",
        "description": "Claude session ERROR rate (per minute)",
    },
]

# Known T0 alert thresholds for recommendation comparison
# Format: alert_name → (baseline_key, window_minutes, current_threshold)
_ALERT_MAPPINGS = [
    ("error-spike-general", "sim_steward.error_rate.per_min", 10, 10),
    ("claude-error-spike", "claude.error_rate.per_min", 5, 5),
    ("websocket-disconnect-spike", "sim_steward.websocket_disconnect.per_hour", 5, 3),
]


class BaselineManager:
    def __init__(self, loki: LokiClient, baseline_path: str = DEFAULT_PATH):
        self.loki = loki
        self.path = baseline_path
        self._cache: dict = {}

    def load(self) -> dict:
        """Load baselines.json from disk. Returns empty dict if not found."""
        try:
            if os.path.exists(self.path):
                with open(self.path) as f:
                    self._cache = json.load(f)
                logger.info("Loaded baselines from %s (%d metrics)", self.path, len(self._cache))
            else:
                logger.info("No baselines.json at %s — starting fresh", self.path)
                self._cache = {}
        except Exception as e:
            logger.warning("Failed to load baselines: %s", e)
            self._cache = {}
        return self._cache

    def compute_and_save(self, lookback_sec: int = 86400) -> dict:
        """
        Query Loki over the lookback window, compute rolling metrics, write baselines.json.
        Preserves existing values for metrics where no new data is found.
        """
        end_ns = self.loki.now_ns()
        start_ns = end_ns - lookback_sec * 1_000_000_000
        updated = dict(self._cache)
        computed_count = 0

        for metric in _METRICS:
            try:
                value = self._compute_metric(metric, start_ns, end_ns, lookback_sec)
                if value is not None:
                    updated[metric["key"]] = round(value, 4)
                    computed_count += 1
                    logger.debug("Baseline %s = %.4f", metric["key"], value)
            except Exception as e:
                logger.warning("Baseline compute failed for %s: %s", metric["key"], e)

        # Persist
        try:
            dirpath = os.path.dirname(os.path.abspath(self.path))
            os.makedirs(dirpath, exist_ok=True)
            with open(self.path, "w") as f:
                json.dump(updated, f, indent=2)
            self._cache = updated
            logger.info(
                "Baselines saved to %s (%d computed, %d total)",
                self.path, computed_count, len(updated),
            )
        except Exception as e:
            logger.warning("Failed to save baselines: %s", e)

        return updated

    def get_prompt_context(self) -> str:
        """Format baseline values for injection into T1 LLM prompt."""
        if not self._cache:
            return "(no baseline data available yet — first run or no historical data)"

        lines = ["Historical baseline for this system (use these to judge what is anomalous):"]
        for key, value in sorted(self._cache.items()):
            metric = next((m for m in _METRICS if m["key"] == key), None)
            description = metric["description"] if metric else key.replace(".", " | ").replace("_", " ")
            lines.append(f"  {description}: {value}")
        lines.append(
            "Flag metrics that exceed baselines by 3x or more as anomalous. "
            "Use these values to calibrate 'high', 'normal', and 'low' thresholds."
        )
        return "\n".join(lines)

    def get_threshold_recommendations(self) -> list[dict]:
        """
        Compare computed baselines against known T0 alert thresholds.
        Returns recommendation dicts for alerts that appear mis-calibrated.
        Emitted by T3 as sentinel_threshold_recommendation events.
        """
        if not self._cache:
            return []

        recommendations = []
        for alert_name, baseline_key, window_minutes, current_threshold in _ALERT_MAPPINGS:
            baseline_val = self._cache.get(baseline_key)
            if baseline_val is None:
                continue

            # Suggested threshold: 5x the baseline rate scaled to the alert window
            suggested = round(baseline_val * window_minutes * 5, 1)
            if suggested <= 0:
                continue

            delta_pct = abs(suggested - current_threshold) / max(current_threshold, 0.001)
            if delta_pct < 0.25:
                continue  # Less than 25% difference — not worth recommending

            recommendations.append({
                "alert": alert_name,
                "current_threshold": current_threshold,
                "suggested_threshold": suggested,
                "basis": (
                    f"{baseline_key}={baseline_val:.3f}/min × {window_minutes}min window × 5x safety margin"
                ),
                "confidence": min(0.9, 0.5 + delta_pct * 0.2),
                "direction": "lower" if suggested < current_threshold else "higher",
            })

        return recommendations

    # ── Private ───────────────────────────────────────────────────────────

    def _compute_metric(
        self, metric: dict, start_ns: int, end_ns: int, lookback_sec: int
    ) -> float | None:
        lines = self.loki.query_lines(metric["logql"], start_ns, end_ns, limit=1000)
        if not lines:
            return None

        compute = metric.get("compute", "count")

        if compute == "rate_per_min":
            minutes = lookback_sec / 60
            return len(lines) / minutes if minutes > 0 else None

        elif compute == "rate_per_hour":
            hours = lookback_sec / 3600
            return len(lines) / hours if hours > 0 else None

        elif compute == "count_per_session":
            # Group by session_id, compute mean count per session
            sessions: dict[str, int] = {}
            no_session = 0
            for line in lines:
                sid = line.get("session_id")
                if sid:
                    sessions[sid] = sessions.get(sid, 0) + 1
                else:
                    no_session += 1
            if sessions:
                return statistics.mean(sessions.values())
            # Fallback: total / estimated sessions (assume 1 session per hour)
            estimated_sessions = max(1, lookback_sec / 3600)
            return len(lines) / estimated_sessions

        elif compute == "field_mean":
            field = metric.get("field", "")
            values = []
            for line in lines:
                v = line.get(field)
                try:
                    values.append(float(v))
                except (TypeError, ValueError):
                    pass
            return statistics.mean(values) if values else None

        else:
            return float(len(lines))
