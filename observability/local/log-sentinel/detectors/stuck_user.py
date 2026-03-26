"""Detect stuck-user patterns — same action repeated rapidly."""

from collections import defaultdict

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class StuckUserDetector(BaseDetector):
    name = "stuck_user"
    category = "app"

    WINDOW_SEC = 30
    WARN_THRESHOLD = 4
    CRITICAL_THRESHOLD = 6

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        actions = cache.get("ss_actions")

        if not actions:
            return findings

        # Group by action+arg combo, collect timestamps
        combos: dict[str, list[float]] = defaultdict(list)
        for line in actions:
            fields = line.get("fields", {})
            combo = f"{fields.get('action', '?')}:{fields.get('arg', '')}"
            ts = _parse_ts(line)
            if ts is not None:
                combos[combo].append(ts)

        for combo, timestamps in combos.items():
            timestamps.sort()
            # Sliding window: count events within WINDOW_SEC
            max_in_window = _max_count_in_window(timestamps, self.WINDOW_SEC)

            if max_in_window >= self.CRITICAL_THRESHOLD:
                findings.append(Finding(
                    detector=self.name,
                    severity="critical",
                    title=f"Stuck user: {combo} x{max_in_window} in {self.WINDOW_SEC}s",
                    summary=f"Action {combo!r} repeated {max_in_window} times within {self.WINDOW_SEC}s — user likely stuck",
                    category=self.category,
                    evidence={"combo": combo, "count_in_window": max_in_window, "window_sec": self.WINDOW_SEC},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="action_result"} | json',
                ))
            elif max_in_window >= self.WARN_THRESHOLD:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"Stuck user: {combo} x{max_in_window} in {self.WINDOW_SEC}s",
                    summary=f"Action {combo!r} repeated {max_in_window} times within {self.WINDOW_SEC}s — possible stuck user",
                    category=self.category,
                    evidence={"combo": combo, "count_in_window": max_in_window, "window_sec": self.WINDOW_SEC},
                    escalate_to_t2=False,
                    logql_query='{app="sim-steward", event="action_result"} | json',
                ))

        return findings


def _parse_ts(line: dict) -> float | None:
    """Extract a numeric timestamp (epoch seconds) from a log line."""
    raw = line.get("timestamp")
    if raw is None:
        return None
    try:
        return float(raw)
    except (ValueError, TypeError):
        pass
    # Try ISO format
    try:
        from datetime import datetime, timezone
        dt = datetime.fromisoformat(str(raw).replace("Z", "+00:00"))
        return dt.timestamp()
    except Exception:
        return None


def _max_count_in_window(sorted_ts: list[float], window_sec: int) -> int:
    """Sliding window max count over sorted timestamps."""
    if not sorted_ts:
        return 0
    max_count = 0
    left = 0
    for right in range(len(sorted_ts)):
        while sorted_ts[right] - sorted_ts[left] > window_sec:
            left += 1
        max_count = max(max_count, right - left + 1)
    return max_count
