"""Tier 1 detector: action dispatch failure rate and consecutive failures."""

from detectors.base import BaseDetector
from loki_client import LokiClient
from models import Finding, TimeWindow

QUERY = '{app="sim-steward", event="action_result"} | json'


class ActionFailureDetector(BaseDetector):
    name = "action_failure"

    def detect(self, loki: LokiClient, window: TimeWindow) -> list[Finding]:
        lines = loki.query_lines(QUERY, window.start_ns, window.end_ns)
        if not lines:
            return []

        total = len(lines)
        failures = 0
        action_counts: dict[str, dict] = {}  # action -> {total, fail, errors}
        consecutive: dict[str, int] = {}  # action -> current consecutive fail streak
        max_consecutive: dict[str, int] = {}  # action -> max streak

        for line in lines:
            fields = line.get("fields", {})
            action = fields.get("action", "unknown")
            success = str(fields.get("success", "true")).lower() == "true"
            error = fields.get("error", "")

            if action not in action_counts:
                action_counts[action] = {"total": 0, "fail": 0, "errors": []}
                consecutive[action] = 0
                max_consecutive[action] = 0

            action_counts[action]["total"] += 1

            if not success:
                failures += 1
                action_counts[action]["fail"] += 1
                if error:
                    action_counts[action]["errors"].append(error[:200])
                consecutive[action] += 1
                if consecutive[action] > max_consecutive[action]:
                    max_consecutive[action] = consecutive[action]
            else:
                consecutive[action] = 0

        findings: list[Finding] = []

        # Overall failure rate check (need 5+ total actions)
        if total >= 5:
            rate = failures / total
            if rate > 0.20:
                severity = "critical" if rate > 0.50 else "warn"
                escalate = severity == "critical"

                per_action = {
                    a: {"total": c["total"], "failures": c["fail"]}
                    for a, c in action_counts.items()
                    if c["fail"] > 0
                }
                error_samples = []
                for c in action_counts.values():
                    error_samples.extend(c["errors"][:2])

                findings.append(
                    Finding(
                        detector=self.name,
                        severity=severity,
                        title=f"Action failure rate {rate:.0%} ({failures}/{total})",
                        summary=(
                            f"{failures} of {total} actions failed ({rate:.0%}) "
                            f"in the last {window.duration_sec}s."
                        ),
                        evidence={
                            "total_actions": total,
                            "failures": failures,
                            "failure_rate": round(rate, 3),
                            "per_action": per_action,
                            "error_samples": error_samples[:5],
                        },
                        escalate_to_t2=escalate,
                        logql_query=QUERY,
                    )
                )

        # Consecutive failure check (3+ same action in a row)
        for action, streak in max_consecutive.items():
            if streak >= 3:
                severity = "critical" if streak >= 6 else "warn"
                findings.append(
                    Finding(
                        detector=self.name,
                        severity=severity,
                        title=f"Consecutive failures: {action} ({streak}x)",
                        summary=(
                            f"Action '{action}' failed {streak} times consecutively."
                        ),
                        evidence={
                            "action": action,
                            "consecutive_failures": streak,
                            "errors": action_counts[action]["errors"][:5],
                        },
                        escalate_to_t2=True,
                        logql_query=QUERY,
                    )
                )

        return findings
