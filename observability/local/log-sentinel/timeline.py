"""Cross-stream timeline builder — correlates events from all Loki streams."""

import logging
from dataclasses import dataclass, field
from datetime import datetime, timezone

from loki_client import LokiClient
from circuit_breaker import CircuitBreaker

logger = logging.getLogger("sentinel.timeline")

# Streams to query and their display names
STREAMS = [
    ("sim-steward",        '{app="sim-steward"} | json'),
    ("claude-dev-logging", '{app="claude-dev-logging"} | json'),
    ("claude-token-metrics", '{app="claude-token-metrics"} | json'),
]

# Events to exclude from the timeline (too noisy)
_SKIP_EVENTS = {"sentinel_log", "sentinel_cycle", "sentinel_analyst_run", "sentinel_timeline_built"}

# Temporal correlation window (nanoseconds)
_TEMPORAL_WINDOW_NS = 30 * 1_000_000_000


@dataclass
class TimelineEvent:
    ts_ns: int
    ts_iso: str
    stream: str
    event_type: str
    domain: str
    component: str
    message: str
    session_id: str | None
    subsession_id: str | None
    raw: dict = field(repr=False)


class TimelineBuilder:
    def __init__(self, loki: LokiClient, breaker: CircuitBreaker):
        self.loki = loki
        self.breaker = breaker

    def build(
        self,
        start_ns: int,
        end_ns: int,
        limit_per_stream: int = 200,
    ) -> list[TimelineEvent]:
        """Query all streams, merge and sort chronologically."""
        if not self.breaker.allow_request():
            logger.warning("Timeline build skipped: Loki circuit open")
            return []

        all_events: list[TimelineEvent] = []
        try:
            for stream_name, logql in STREAMS:
                lines = self.loki.query_lines(logql, start_ns, end_ns, limit=limit_per_stream)
                self.breaker.record_success()
                for line in lines:
                    ev = self._parse_event(stream_name, line)
                    if ev:
                        all_events.append(ev)
        except Exception as e:
            self.breaker.record_failure()
            logger.error("Timeline build error: %s", e)
            return all_events

        all_events.sort(key=lambda e: e.ts_ns)
        return all_events

    def _parse_event(self, stream: str, line: dict) -> TimelineEvent | None:
        event_type = line.get("event", "")
        if event_type in _SKIP_EVENTS:
            return None

        # Parse timestamp — prefer the log's own timestamp field, fallback to now
        ts_ns = 0
        ts_iso = line.get("timestamp", "")
        if ts_iso:
            try:
                dt = datetime.fromisoformat(ts_iso.replace("Z", "+00:00"))
                ts_ns = int(dt.timestamp() * 1e9)
            except (ValueError, TypeError):
                pass
        if not ts_ns:
            ts_ns = self.loki.now_ns()
            ts_iso = datetime.now(timezone.utc).isoformat()

        return TimelineEvent(
            ts_ns=ts_ns,
            ts_iso=ts_iso,
            stream=stream,
            event_type=event_type or "unknown",
            domain=line.get("domain", ""),
            component=line.get("component", ""),
            message=line.get("message", ""),
            session_id=line.get("session_id") or None,
            subsession_id=line.get("subsession_id") or None,
            raw=line,
        )

    def get_active_sessions(self, events: list[TimelineEvent]) -> list[str]:
        """Return distinct session_ids seen in the event list."""
        seen = []
        for ev in events:
            if ev.session_id and ev.session_id not in seen:
                seen.append(ev.session_id)
        return seen

    def to_prompt_text(self, events: list[TimelineEvent], max_events: int = 60) -> str:
        """Format timeline as human-readable numbered lines for LLM consumption."""
        if not events:
            return "(no events in this window)"

        truncated = len(events) > max_events
        shown = events[-max_events:] if truncated else events

        # Group by session_id
        sessions: dict[str, list[TimelineEvent]] = {}
        no_session: list[TimelineEvent] = []

        for ev in shown:
            if ev.session_id:
                sessions.setdefault(ev.session_id, []).append(ev)
            else:
                no_session.append(ev)

        lines = []
        counter = 1

        for sid, evts in sessions.items():
            # Find subsession if present
            sub = next((e.subsession_id for e in evts if e.subsession_id), None)
            header = f"SESSION {sid[:8]}"
            if sub:
                header += f" [subsession {sub}]"
            lines.append(header)
            for ev in evts:
                lines.append(_format_event_line(counter, ev))
                counter += 1
            lines.append("")

        if no_session:
            lines.append("CO-OCCURRING (no session correlation)")
            for ev in no_session:
                lines.append(_format_event_line(counter, ev))
                counter += 1

        if truncated:
            lines.append(
                f"\n[NOTE: {len(events) - max_events} earlier events not shown. "
                f"Earliest: {events[0].ts_iso}, Latest: {events[-1].ts_iso}]"
            )

        return "\n".join(lines)

    def get_stats(self, events: list[TimelineEvent]) -> dict:
        sessions = self.get_active_sessions(events)
        streams = list({e.stream for e in events})
        return {
            "event_count": len(events),
            "session_count": len(sessions),
            "streams_queried": streams,
        }


def _format_event_line(idx: int, ev: TimelineEvent) -> str:
    # Extract time portion only (HH:MM:SS)
    try:
        t = ev.ts_iso[11:19]
    except (IndexError, TypeError):
        t = "??:??:??"

    # Pick the most informative extra field from raw
    extra = _pick_extra(ev)
    extra_str = f"  {extra}" if extra else ""

    return (
        f"  [{idx:03d}] {t}  {ev.stream:<25}  {ev.event_type:<30}{extra_str}"
    )


def _pick_extra(ev: TimelineEvent) -> str:
    """Extract a short key=value summary from the raw event for the timeline."""
    raw = ev.raw
    candidates = [
        ("action", raw.get("action")),
        ("tool", raw.get("tool_name")),
        ("event_type", raw.get("hook_type")),
        ("track", raw.get("track_display_name")),
        ("driver", raw.get("display_name")),
        ("cost_usd", raw.get("cost_usd")),
        ("tokens", raw.get("total_tokens")),
        ("error", raw.get("error")),
        ("duration_ms", raw.get("duration_ms")),
    ]
    parts = [f"{k}={v}" for k, v in candidates if v is not None and v != ""]
    return "  ".join(parts[:3])
