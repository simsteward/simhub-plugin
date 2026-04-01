"""Session narrative builder — used by T3 synthesis.

Turns a set of FeatureInvocations + T1/T2 findings into a human-readable
per-session story that answers: "What was the user trying to do, did it work?"

Output shape (returned as text block):
  NARRATIVE: <date>  <time_range>  [<session_id>]
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  <2-3 sentence prose of what happened>

  WORKED:   <feature> · <feature>
  FAILED:   <feature> (error)
  PATTERNS: <any recurring issue seen this session>
  ACTION:   <recommendation if any>
"""

import logging
from datetime import datetime, timezone

from trace import FeatureInvocation

logger = logging.getLogger("sentinel.narrative")


class NarrativeBuilder:
    def build(
        self,
        session_id: str,
        invocations: list[FeatureInvocation],
        anomaly_dicts: list[dict],
        t2_investigation_dicts: list[dict],
        pattern_matches: list[str] | None = None,
    ) -> str:
        """Build a narrative text block for a single session."""
        if not invocations:
            return f"NARRATIVE: session={session_id}\n  (no feature invocations recorded)"

        # Time range
        start_ns = min(inv.start_ts_ns for inv in invocations)
        end_ns = max(inv.end_ts_ns for inv in invocations)
        start_dt = datetime.fromtimestamp(start_ns / 1e9, tz=timezone.utc)
        end_dt = datetime.fromtimestamp(end_ns / 1e9, tz=timezone.utc)
        date_str = start_dt.strftime("%Y-%m-%d")
        time_range = f"{start_dt.strftime('%H:%M')}–{end_dt.strftime('%H:%M')}"

        # Classify worked vs failed
        worked = []
        failed = []
        for inv in invocations:
            if inv.success is False:
                failed.append(inv)
            elif inv.success is True:
                worked.append(inv)
            # success=None (unknown) counted as neither

        # Build prose summary
        prose = self._build_prose(invocations, worked, failed, anomaly_dicts)

        # Recommendation from T2 investigations or anomalies
        action = self._extract_action(t2_investigation_dicts, anomaly_dicts)

        # Pattern summary
        patterns_text = ""
        if pattern_matches:
            patterns_text = " · ".join(pattern_matches[:3])
        elif _has_recurring_issue(anomaly_dicts):
            patterns_text = f"{sum(1 for a in anomaly_dicts if a.get('severity') in ('warn', 'critical'))} anomalies flagged"

        # Assemble
        sep = "━" * 48
        lines = [
            f"NARRATIVE: {date_str}  {time_range}  [{session_id[:12]}]",
            sep,
            "",
            prose,
            "",
        ]

        if worked:
            worked_str = " · ".join(_action_label(inv) for inv in _dedupe_by_type(worked))
            lines.append(f"WORKED:   {worked_str}")
        if failed:
            failed_str = " · ".join(
                f"{_action_label(inv)} ({(inv.error or 'error')[:40]})"
                for inv in _dedupe_by_type(failed)
            )
            lines.append(f"FAILED:   {failed_str}")
        if patterns_text:
            lines.append(f"PATTERNS: {patterns_text}")
        if action:
            lines.append(f"ACTION:   {action[:200]}")

        return "\n".join(lines)

    def build_all(
        self,
        invocations: list[FeatureInvocation],
        anomaly_dicts: list[dict],
        t2_investigation_dicts: list[dict],
    ) -> list[dict]:
        """Group invocations by session_id, build a narrative per session.

        Returns list of dicts with keys: session_id, narrative_text, features_worked,
        features_failed, invocation_count.
        """
        # Group invocations by session_id
        sessions: dict[str, list[FeatureInvocation]] = {}
        for inv in invocations:
            sid = (inv.trigger_event.session_id if inv.trigger_event else None) or "no_session"
            sessions.setdefault(sid, []).append(inv)

        results = []
        for sid, session_invocations in sessions.items():
            # Filter anomalies + investigations for this session
            session_anomalies = _filter_for_session(anomaly_dicts, sid)
            session_t2 = _filter_for_session(t2_investigation_dicts, sid)

            text = self.build(
                session_id=sid,
                invocations=session_invocations,
                anomaly_dicts=session_anomalies,
                t2_investigation_dicts=session_t2,
            )
            worked = [inv.action_type for inv in session_invocations if inv.success is True]
            failed = [inv.action_type for inv in session_invocations if inv.success is False]

            results.append({
                "session_id": sid,
                "narrative_text": text,
                "features_worked": list(dict.fromkeys(worked)),  # dedupe, order-preserving
                "features_failed": list(dict.fromkeys(failed)),
                "invocation_count": len(session_invocations),
            })

        return results

    # ── Private ───────────────────────────────────────────────────────────────

    def _build_prose(
        self,
        all_invocations: list[FeatureInvocation],
        worked: list[FeatureInvocation],
        failed: list[FeatureInvocation],
        anomaly_dicts: list[dict],
    ) -> str:
        total = len(all_invocations)
        worked_count = len(worked)
        failed_count = len(failed)

        # Action type distribution
        type_counts: dict[str, int] = {}
        for inv in all_invocations:
            type_counts[inv.action_type] = type_counts.get(inv.action_type, 0) + 1

        top_types = sorted(type_counts.items(), key=lambda x: x[1], reverse=True)[:3]
        type_str = ", ".join(f"{name} (×{n})" for name, n in top_types)

        health_str = (
            "All recorded actions completed successfully."
            if failed_count == 0
            else f"{failed_count} of {total} action(s) failed."
        )

        anomaly_count = sum(1 for a in anomaly_dicts if a.get("severity") in ("warn", "critical"))
        anomaly_str = f" {anomaly_count} anomaly flags were raised." if anomaly_count else ""

        return (
            f"{total} feature invocation(s) recorded: {type_str}. "
            f"{health_str}{anomaly_str}"
        )

    def _extract_action(
        self,
        t2_dicts: list[dict],
        anomaly_dicts: list[dict],
    ) -> str:
        # Prefer T2 recommendation if available
        for t2 in t2_dicts:
            rec = t2.get("recommendation", "")
            if rec and rec not in ("Investigate manually.", ""):
                return rec[:200]
        # Fall back to critical anomaly hypothesis
        for a in anomaly_dicts:
            if a.get("severity") == "critical" and a.get("hypothesis"):
                return a["hypothesis"][:200]
        return ""


# ── Helpers ───────────────────────────────────────────────────────────────────

def _action_label(inv: FeatureInvocation) -> str:
    return inv.action_type.replace("_", " ").replace("-", " ").lower()


def _dedupe_by_type(invocations: list[FeatureInvocation]) -> list[FeatureInvocation]:
    seen: dict[str, FeatureInvocation] = {}
    for inv in invocations:
        seen.setdefault(inv.action_type, inv)
    return list(seen.values())


def _has_recurring_issue(anomaly_dicts: list[dict]) -> bool:
    return any(a.get("severity") in ("warn", "critical") for a in anomaly_dicts)


def _filter_for_session(items: list[dict], session_id: str) -> list[dict]:
    """Return items that mention this session_id, or all items if session is no_session."""
    if session_id == "no_session":
        return items
    return [
        item for item in items
        if not item.get("session_id") or item.get("session_id") == session_id
    ]
