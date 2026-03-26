"""Data models for Log Sentinel findings and investigations."""

import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone


@dataclass
class TimeWindow:
    start_ns: int
    end_ns: int
    duration_sec: int

    @classmethod
    def from_now(cls, lookback_sec: int) -> "TimeWindow":
        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        end_ns = now_ms * 1_000_000
        start_ns = (now_ms - lookback_sec * 1000) * 1_000_000
        return cls(start_ns=start_ns, end_ns=end_ns, duration_sec=lookback_sec)


@dataclass
class Finding:
    detector: str
    severity: str  # "info" | "warn" | "critical"
    title: str
    summary: str
    evidence: dict = field(default_factory=dict)
    timestamp: str = field(default_factory=lambda: datetime.now(timezone.utc).isoformat())
    finding_id: str = field(default_factory=lambda: str(uuid.uuid4()))
    escalate_to_t2: bool = False
    flow_context: str = ""
    logql_query: str = ""


@dataclass
class Investigation:
    finding: Finding
    root_cause: str
    correlation: str
    impact: str
    recommendation: str
    confidence: str  # "low" | "medium" | "high"
    raw_response: str = ""
    model: str = ""
    investigation_id: str = field(default_factory=lambda: str(uuid.uuid4()))
    timestamp: str = field(default_factory=lambda: datetime.now(timezone.utc).isoformat())
    inference_duration_ms: int = 0
    context_lines_gathered: int = 0


@dataclass
class FlowStep:
    id: str
    event: str
    label: str
    filters: dict = field(default_factory=dict)
    timeout_sec: int = 0
    optional: bool = False
    next_steps: list = field(default_factory=list)


@dataclass
class FlowDefinition:
    name: str
    display_name: str
    description: str
    source_doc: str
    steps: list  # list[FlowStep]
    expected_completion_sec: int = 0
    gap_severity: str = "warn"


@dataclass
class FlowGap:
    step: FlowStep
    flow: FlowDefinition
    description: str = ""


@dataclass
class FlowEvaluation:
    flow: FlowDefinition
    matched_steps: dict = field(default_factory=dict)
    gaps: list = field(default_factory=list)  # list[FlowGap]

    @property
    def complete(self) -> bool:
        return len(self.gaps) == 0
