"""Tier 1 detector: unusual incident patterns."""

from detectors.base import BaseDetector
from loki_client import LokiClient
from models import Finding, TimeWindow

QUERY = '{app="sim-steward", event="incident_detected"} | json'

BURST_WINDOW_SEC = 60
BURST_THRESHOLD = 5
DRIVER_WARN_THRESHOLD = 15
DRIVER_INFO_THRESHOLD = 10


class IncidentAnomalyDetector(BaseDetector):
    name = "incident_anomaly"

    def detect(self, loki: LokiClient, window: TimeWindow) -> list[Finding]:
        lines = loki.query_lines(QUERY, window.start_ns, window.end_ns)
        if not lines:
            return []

        findings: list[Finding] = []
        timestamps: list[float] = []
        driver_counts: dict[str, int] = {}

        for line in lines:
            fields = line.get("fields", {})
            driver = fields.get("display_name", "unknown")
            driver_counts[driver] = driver_counts.get(driver, 0) + 1

            ts = self._parse_timestamp(line.get("timestamp", ""))
            if ts is not None:
                timestamps.append(ts)

        total = len(lines)
        timestamps.sort()

        # Burst detection: 5+ incidents within 60s
        burst_count = self._find_burst(timestamps)
        if burst_count >= BURST_THRESHOLD:
            findings.append(
                Finding(
                    detector=self.name,
                    severity="info",
                    title=f"Incident burst: {burst_count} within {BURST_WINDOW_SEC}s",
                    summary=(
                        f"{burst_count} incidents detected within a {BURST_WINDOW_SEC}s "
                        f"window. Total incidents in period: {total}."
                    ),
                    evidence={
                        "burst_count": burst_count,
                        "burst_window_sec": BURST_WINDOW_SEC,
                        "total_incidents": total,
                        "driver_breakdown": driver_counts,
                    },
                    logql_query=QUERY,
                )
            )

        # Per-driver anomaly
        for driver, count in driver_counts.items():
            if count >= DRIVER_WARN_THRESHOLD:
                findings.append(
                    Finding(
                        detector=self.name,
                        severity="warn",
                        title=f"Driver incident spike: {driver} ({count} incidents)",
                        summary=(
                            f"Driver '{driver}' accumulated {count} incidents "
                            f"in the last {window.duration_sec}s."
                        ),
                        evidence={
                            "driver": driver,
                            "incident_count": count,
                            "total_incidents": total,
                            "driver_breakdown": driver_counts,
                        },
                        escalate_to_t2=True,
                        logql_query=QUERY,
                    )
                )
            elif count >= DRIVER_INFO_THRESHOLD:
                findings.append(
                    Finding(
                        detector=self.name,
                        severity="info",
                        title=f"Driver incidents elevated: {driver} ({count})",
                        summary=(
                            f"Driver '{driver}' has {count} incidents "
                            f"in the last {window.duration_sec}s."
                        ),
                        evidence={
                            "driver": driver,
                            "incident_count": count,
                            "total_incidents": total,
                        },
                        logql_query=QUERY,
                    )
                )

        return findings

    @staticmethod
    def _parse_timestamp(ts_raw: str) -> float | None:
        if not ts_raw:
            return None
        try:
            from datetime import datetime

            dt = datetime.fromisoformat(ts_raw.replace("Z", "+00:00"))
            return dt.timestamp()
        except (ValueError, TypeError):
            return None

    @staticmethod
    def _find_burst(timestamps: list[float]) -> int:
        """Find the largest cluster of incidents within BURST_WINDOW_SEC."""
        if not timestamps:
            return 0
        best = 0
        for i, start in enumerate(timestamps):
            count = 0
            for j in range(i, len(timestamps)):
                if timestamps[j] - start <= BURST_WINDOW_SEC:
                    count += 1
                else:
                    break
            if count > best:
                best = count
        return best
