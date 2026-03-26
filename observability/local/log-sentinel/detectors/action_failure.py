"""Detect action failure rates and consecutive failure patterns."""

from collections import defaultdict

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class ActionFailureDetector(BaseDetector):
    name = "action_failure"
    category = "app"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        actions = cache.get("ss_actions")

        if not actions:
            return findings

        # --- Failure rate ---
        total = len(actions)
        failures = [a for a in actions if not _is_success(a)]
        fail_count = len(failures)

        if total >= 5 and fail_count > 0:
            rate = fail_count / total
            if rate > 0.20:
                severity = "critical" if rate > 0.50 else "warn"
                findings.append(Finding(
                    detector=self.name,
                    severity=severity,
                    title=f"Action failure rate {rate:.0%} ({fail_count}/{total})",
                    summary=f"{fail_count} of {total} actions failed ({rate:.0%})",
                    category=self.category,
                    evidence={
                        "total": total,
                        "failures": fail_count,
                        "rate": round(rate, 3),
                    },
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="action_result"} | json',
                ))

        # --- Consecutive same-action failures ---
        _check_consecutive(actions, findings, self)

        return findings


def _is_success(line: dict) -> bool:
    fields = line.get("fields", {})
    val = fields.get("success")
    if isinstance(val, bool):
        return val
    if isinstance(val, str):
        return val.lower() == "true"
    return True


def _check_consecutive(actions: list[dict], findings: list[Finding], detector: BaseDetector) -> None:
    """Detect 3+ consecutive failures of the same action+arg combo."""
    streak_action = None
    streak_count = 0

    for line in actions:
        fields = line.get("fields", {})
        combo = f"{fields.get('action', '?')}:{fields.get('arg', '')}"

        if not _is_success(line):
            if combo == streak_action:
                streak_count += 1
            else:
                streak_action = combo
                streak_count = 1
        else:
            # Success resets the streak
            if streak_count >= 3:
                findings.append(Finding(
                    detector=detector.name,
                    severity="warn",
                    title=f"Consecutive failures: {streak_action} x{streak_count}",
                    summary=f"Action {streak_action!r} failed {streak_count} times consecutively",
                    category=detector.category,
                    evidence={"action_combo": streak_action, "consecutive": streak_count},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="action_result"} | json',
                ))
            streak_action = None
            streak_count = 0

    # Check trailing streak
    if streak_count >= 3:
        findings.append(Finding(
            detector=detector.name,
            severity="warn",
            title=f"Consecutive failures: {streak_action} x{streak_count}",
            summary=f"Action {streak_action!r} failed {streak_count} times consecutively",
            category=detector.category,
            evidence={"action_combo": streak_action, "consecutive": streak_count},
            escalate_to_t2=True,
            logql_query='{app="sim-steward", event="action_result"} | json',
        ))
