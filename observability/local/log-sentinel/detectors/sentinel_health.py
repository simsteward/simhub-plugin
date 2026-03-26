"""Self-monitoring detector for the sentinel itself — uses in-memory stats, not Loki."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class SentinelHealthDetector(BaseDetector):
    name = "sentinel_health"
    category = "ops"

    def __init__(self, stats_ref: dict):
        """Accept a mutable stats dict updated by sentinel.py each cycle.

        Expected keys:
            last_cycle_duration_ms, consecutive_detector_errors,
            last_t2_duration_ms, t2_queue_size, cycles_completed
        """
        self._stats = stats_ref

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        stats = self._stats

        cycle_ms = _safe_int(stats.get("last_cycle_duration_ms"))
        consec_errors = _safe_int(stats.get("consecutive_detector_errors"))
        t2_ms = _safe_int(stats.get("last_t2_duration_ms"))
        cycles_completed = _safe_int(stats.get("cycles_completed"))

        # Slow cycle
        if cycle_ms > 30_000:
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title=f"Slow cycle ({cycle_ms}ms)",
                summary=f"Last sentinel cycle took {cycle_ms}ms (>30s threshold)",
                category=self.category,
                evidence={"last_cycle_duration_ms": cycle_ms},
            ))

        # Consecutive detector errors
        if consec_errors > 2:
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title=f"Detector failures: {consec_errors} consecutive",
                summary=f"{consec_errors} consecutive detector errors — detectors may be broken",
                category=self.category,
                evidence={"consecutive_detector_errors": consec_errors},
            ))

        # Slow T2 investigation
        if t2_ms > 300_000:
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title="T2 investigation very slow (>5min)",
                summary=f"Last T2 investigation took {t2_ms}ms ({t2_ms / 60_000:.1f} min)",
                category=self.category,
                evidence={"last_t2_duration_ms": t2_ms},
            ))

        # Stalled polling: cycles have run before but none recently
        # The caller is expected to set "last_cycle_epoch_ms" in stats
        # when a cycle completes. If cycles_completed > 0 but no cycle
        # has landed recently, the sentinel main loop itself detects this
        # and sets "stalled" = True in the stats dict.
        if cycles_completed > 0 and stats.get("stalled"):
            findings.append(Finding(
                detector=self.name,
                severity="critical",
                title="Sentinel polling stalled",
                summary=f"Sentinel has completed {cycles_completed} cycles but appears stalled — no recent cycle",
                category=self.category,
                evidence={"cycles_completed": cycles_completed},
                escalate_to_t2=True,
            ))

        return findings


def _safe_int(val) -> int:
    try:
        return int(val)
    except (TypeError, ValueError):
        return 0
