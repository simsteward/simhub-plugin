"""Feature invocation model — groups timeline events into traceable user actions.

Three correlation strategies (applied in order):
  1. trace_id exact — events share a trace_id field (plugin + dashboard instrumented)
  2. temporal     — events cluster within 150ms with expected sequence patterns
  3. inferred     — fallback: group by session_id + 1-minute time bucket
"""

import logging
import uuid
from dataclasses import dataclass, field

from timeline import TimelineEvent

logger = logging.getLogger("sentinel.trace")

# Temporal grouping window (nanoseconds)
_TEMPORAL_WINDOW_NS = 150_000_000  # 150ms

# Events that anchor the start of a new invocation in temporal mode
_ANCHOR_EVENTS = {
    "dashboard_ui_event",
    "action_dispatched",
    "iracing_session_start",
    "iracing_replay_seek",
}

# Events that signal the end of an invocation
_TERMINAL_EVENTS = {
    "action_result",
    "iracing_session_end",
}

# Inferred grouping bucket (nanoseconds)
_BUCKET_NS = 60 * 1_000_000_000  # 1 minute


@dataclass
class FeatureInvocation:
    invocation_id: str              # trace_id if available, else generated UUID
    correlation_method: str         # "trace_id" | "temporal" | "inferred"
    start_ts_ns: int
    end_ts_ns: int
    action_type: str                # "replay_seek" | "incident_review" | "session_start" | etc.
    trigger_event: TimelineEvent    # first event in this invocation
    events: list[TimelineEvent]     # all events belonging to this invocation
    success: bool | None            # did the feature complete? None = unknown
    error: str | None               # error message if failed
    duration_ms: int
    streams_involved: list[str]     # which Loki streams contributed events

    def to_summary_dict(self) -> dict:
        """Compact serializable summary for Loki push and LLM context."""
        return {
            "invocation_id": self.invocation_id,
            "correlation_method": self.correlation_method,
            "action_type": self.action_type,
            "success": self.success,
            "error": self.error,
            "duration_ms": self.duration_ms,
            "event_count": len(self.events),
            "streams": self.streams_involved,
            "start_ts_ns": self.start_ts_ns,
            "end_ts_ns": self.end_ts_ns,
        }


class InvocationBuilder:
    """Groups a flat list of TimelineEvents into FeatureInvocation objects."""

    def build(self, events: list[TimelineEvent]) -> list[FeatureInvocation]:
        """
        Returns invocations built from the event list.
        Events are consumed across three passes; any event can only belong to one invocation.
        """
        remaining = list(events)
        invocations: list[FeatureInvocation] = []

        # Pass 1 — exact trace_id grouping
        trace_invocations, remaining = self._group_by_trace_id(remaining)
        invocations.extend(trace_invocations)

        # Pass 2 — temporal window grouping
        temporal_invocations, remaining = self._group_temporal(remaining)
        invocations.extend(temporal_invocations)

        # Pass 3 — inferred (session + time bucket)
        inferred_invocations = self._group_inferred(remaining)
        invocations.extend(inferred_invocations)

        logger.debug(
            "InvocationBuilder: %d events → %d invocations (%d trace_id, %d temporal, %d inferred)",
            len(events),
            len(invocations),
            len(trace_invocations),
            len(temporal_invocations),
            len(inferred_invocations),
        )
        return sorted(invocations, key=lambda i: i.start_ts_ns)

    # ── Pass 1: exact trace_id ─────────────────────────────────────────────

    def _group_by_trace_id(
        self, events: list[TimelineEvent]
    ) -> tuple[list[FeatureInvocation], list[TimelineEvent]]:
        groups: dict[str, list[TimelineEvent]] = {}
        leftover: list[TimelineEvent] = []

        for ev in events:
            tid = ev.raw.get("trace_id")
            if tid:
                groups.setdefault(tid, []).append(ev)
            else:
                leftover.append(ev)

        invocations = [
            _build_invocation(group, "trace_id", trace_id=tid)
            for tid, group in groups.items()
        ]
        return invocations, leftover

    # ── Pass 2: temporal window ────────────────────────────────────────────

    def _group_temporal(
        self, events: list[TimelineEvent]
    ) -> tuple[list[FeatureInvocation], list[TimelineEvent]]:
        if not events:
            return [], []

        sorted_events = sorted(events, key=lambda e: e.ts_ns)
        groups: list[list[TimelineEvent]] = []
        current: list[TimelineEvent] = []

        for ev in sorted_events:
            if not current:
                current = [ev]
                continue

            gap = ev.ts_ns - current[-1].ts_ns
            is_anchor = ev.event_type in _ANCHOR_EVENTS

            if is_anchor or gap > _TEMPORAL_WINDOW_NS:
                if current:
                    groups.append(current)
                current = [ev]
            else:
                current.append(ev)

        if current:
            groups.append(current)

        # Drop single-event groups with no action signal — too noisy
        meaningful = [g for g in groups if len(g) > 1 or g[0].event_type in _ANCHOR_EVENTS]
        leftover = [ev for g in groups if g not in meaningful for ev in g]

        invocations = [_build_invocation(g, "temporal") for g in meaningful]
        return invocations, leftover

    # ── Pass 3: inferred (session + time bucket) ───────────────────────────

    def _group_inferred(self, events: list[TimelineEvent]) -> list[FeatureInvocation]:
        if not events:
            return []

        buckets: dict[str, list[TimelineEvent]] = {}
        for ev in events:
            sid = ev.session_id or "no_session"
            bucket = ev.ts_ns // _BUCKET_NS
            key = f"{sid}:{bucket}"
            buckets.setdefault(key, []).append(ev)

        return [_build_invocation(group, "inferred") for group in buckets.values()]


# ── Helpers ────────────────────────────────────────────────────────────────

def _build_invocation(
    events: list[TimelineEvent],
    method: str,
    trace_id: str | None = None,
) -> FeatureInvocation:
    sorted_events = sorted(events, key=lambda e: e.ts_ns)
    start_ns = sorted_events[0].ts_ns
    end_ns = sorted_events[-1].ts_ns
    duration_ms = max(0, (end_ns - start_ns) // 1_000_000)

    # action_type: prefer action_dispatched.raw["action"], else trigger event_type
    action_type = "unknown"
    for ev in sorted_events:
        if ev.event_type == "action_dispatched":
            action_type = ev.raw.get("action") or ev.event_type
            break
    if action_type == "unknown":
        action_type = sorted_events[0].event_type or "unknown"

    # success / error: look for terminal events
    success: bool | None = None
    error: str | None = None
    for ev in sorted_events:
        if ev.event_type in _TERMINAL_EVENTS or ev.event_type.endswith("_result"):
            raw_success = ev.raw.get("success")
            raw_error = ev.raw.get("error")
            if raw_error:
                success = False
                error = str(raw_error)[:200]
                break
            if raw_success is not None:
                success = bool(raw_success)
                break

    streams = list({ev.stream for ev in sorted_events})

    return FeatureInvocation(
        invocation_id=trace_id or str(uuid.uuid4()),
        correlation_method=method,
        start_ts_ns=start_ns,
        end_ts_ns=end_ns,
        action_type=action_type,
        trigger_event=sorted_events[0],
        events=sorted_events,
        success=success,
        error=error,
        duration_ms=duration_ms,
        streams_involved=streams,
    )
