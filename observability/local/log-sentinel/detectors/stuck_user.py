"""Tier 1 detector: user stuck in retry loops."""

from detectors.base import BaseDetector
from loki_client import LokiClient
from models import Finding, TimeWindow

QUERY = '{app="sim-steward", event="action_result"} | json'

RETRY_WINDOW_SEC = 30
WARN_THRESHOLD = 4
CRITICAL_THRESHOLD = 6


class StuckUserDetector(BaseDetector):
    name = "stuck_user"

    def detect(self, loki: LokiClient, window: TimeWindow) -> list[Finding]:
        lines = loki.query_lines(QUERY, window.start_ns, window.end_ns)
        if not lines:
            return []

        # Group by action+arg, tracking timestamps
        groups: dict[str, list[float]] = {}
        for line in lines:
            fields = line.get("fields", {})
            action = fields.get("action", "")
            arg = fields.get("arg", "")
            if not action:
                continue

            key = f"{action}|{arg}" if arg else action

            # Parse timestamp — try multiple formats
            ts_raw = line.get("timestamp", "")
            ts = self._parse_timestamp(ts_raw)
            if ts is not None:
                if key not in groups:
                    groups[key] = []
                groups[key].append(ts)

        findings: list[Finding] = []

        for key, timestamps in groups.items():
            if len(timestamps) < WARN_THRESHOLD:
                continue

            timestamps.sort()

            # Find clusters within RETRY_WINDOW_SEC
            best_count, best_start, best_end = self._find_cluster(timestamps)

            if best_count < WARN_THRESHOLD:
                continue

            parts = key.split("|", 1)
            action = parts[0]
            arg = parts[1] if len(parts) > 1 else ""
            span = best_end - best_start

            severity = "critical" if best_count >= CRITICAL_THRESHOLD else "warn"
            escalate = best_count >= CRITICAL_THRESHOLD

            findings.append(
                Finding(
                    detector=self.name,
                    severity=severity,
                    title=f"Retry loop: {action} repeated {best_count}x in {span:.0f}s",
                    summary=(
                        f"Action '{action}'"
                        + (f" with arg '{arg}'" if arg else "")
                        + f" was invoked {best_count} times within {span:.1f}s. "
                        f"User may be stuck."
                    ),
                    evidence={
                        "action": action,
                        "arg": arg,
                        "repeat_count": best_count,
                        "time_span_sec": round(span, 1),
                    },
                    escalate_to_t2=escalate,
                    logql_query=QUERY,
                )
            )

        return findings

    @staticmethod
    def _parse_timestamp(ts_raw: str) -> float | None:
        """Best-effort timestamp parse. Returns epoch seconds or None."""
        if not ts_raw:
            return None
        try:
            from datetime import datetime, timezone

            # ISO format: 2024-01-01T00:00:00.000Z
            dt = datetime.fromisoformat(ts_raw.replace("Z", "+00:00"))
            return dt.timestamp()
        except (ValueError, TypeError):
            return None

    @staticmethod
    def _find_cluster(timestamps: list[float]) -> tuple[int, float, float]:
        """Sliding window: find largest cluster within RETRY_WINDOW_SEC."""
        best_count = 0
        best_start = 0.0
        best_end = 0.0

        for i, start in enumerate(timestamps):
            count = 0
            end = start
            for j in range(i, len(timestamps)):
                if timestamps[j] - start <= RETRY_WINDOW_SEC:
                    count += 1
                    end = timestamps[j]
                else:
                    break
            if count > best_count:
                best_count = count
                best_start = start
                best_end = end

        return best_count, best_start, best_end
