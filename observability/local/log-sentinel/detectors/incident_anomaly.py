"""Detect incident anomalies — bursts and per-driver accumulation."""

from collections import Counter, defaultdict

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class IncidentAnomalyDetector(BaseDetector):
    name = "incident_anomaly"
    category = "app"

    BURST_WINDOW_SEC = 60
    BURST_THRESHOLD = 5
    DRIVER_WARN_THRESHOLD = 15
    DRIVER_INFO_THRESHOLD = 10

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        incidents = cache.get("ss_incidents")

        if not incidents:
            return findings

        # --- Burst detection: 5+ incidents within 60s ---
        timestamps = []
        for line in incidents:
            ts = _parse_ts(line)
            if ts is not None:
                timestamps.append(ts)

        timestamps.sort()
        max_burst = _max_count_in_window(timestamps, self.BURST_WINDOW_SEC)

        if max_burst >= self.BURST_THRESHOLD:
            findings.append(Finding(
                detector=self.name,
                severity="info",
                title=f"Incident burst: {max_burst} in {self.BURST_WINDOW_SEC}s",
                summary=f"{max_burst} incidents detected within a {self.BURST_WINDOW_SEC}s window",
                category=self.category,
                evidence={"burst_count": max_burst, "window_sec": self.BURST_WINDOW_SEC},
                escalate_to_t2=False,
                logql_query='{app="sim-steward", event="incident_detected"} | json',
            ))

        # --- Per-driver accumulation ---
        driver_counts: Counter[str] = Counter()
        for line in incidents:
            fields = line.get("fields", {})
            driver = fields.get("display_name") or fields.get("unique_user_id") or "unknown"
            driver_counts[str(driver)] += 1

        for driver, count in driver_counts.items():
            if count >= self.DRIVER_WARN_THRESHOLD:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"Driver {driver}: {count} incidents",
                    summary=f"Driver {driver!r} accumulated {count} incidents — exceeds warning threshold",
                    category=self.category,
                    evidence={"driver": driver, "incident_count": count},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="incident_detected"} | json',
                ))
            elif count >= self.DRIVER_INFO_THRESHOLD:
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title=f"Driver {driver}: {count} incidents",
                    summary=f"Driver {driver!r} accumulated {count} incidents",
                    category=self.category,
                    evidence={"driver": driver, "incident_count": count},
                    escalate_to_t2=False,
                    logql_query='{app="sim-steward", event="incident_detected"} | json',
                ))

        return findings


def _parse_ts(line: dict) -> float | None:
    raw = line.get("timestamp")
    if raw is None:
        return None
    try:
        return float(raw)
    except (ValueError, TypeError):
        pass
    try:
        from datetime import datetime
        dt = datetime.fromisoformat(str(raw).replace("Z", "+00:00"))
        return dt.timestamp()
    except Exception:
        return None


def _max_count_in_window(sorted_ts: list[float], window_sec: int) -> int:
    if not sorted_ts:
        return 0
    max_count = 0
    left = 0
    for right in range(len(sorted_ts)):
        while sorted_ts[right] - sorted_ts[left] > window_sec:
            left += 1
        max_count = max(max_count, right - left + 1)
    return max_count
