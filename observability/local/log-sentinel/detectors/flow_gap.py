"""Detect expected-flow gaps by delegating to the FlowEngine."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class FlowGapDetector(BaseDetector):
    name = "flow_gap"
    category = "app"

    def __init__(self, flow_engine):
        self.flow_engine = flow_engine

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        all_events = cache.get("ss_all")

        # Filter out noise — host_resource_sample events don't participate in flows
        meaningful = [e for e in all_events if e.get("event") != "host_resource_sample"]

        if not meaningful:
            return findings

        evaluations = self.flow_engine.evaluate(meaningful)

        for evaluation in evaluations:
            for gap in evaluation.gaps:
                severity = evaluation.flow.gap_severity or "warn"
                findings.append(Finding(
                    detector=self.name,
                    severity=severity,
                    title=f"Flow gap: {evaluation.flow.display_name} — missing {gap.step.label}",
                    summary=gap.description or f"Expected step {gap.step.label!r} not found in flow {evaluation.flow.display_name!r}",
                    category=self.category,
                    evidence={
                        "flow": evaluation.flow.name,
                        "flow_display": evaluation.flow.display_name,
                        "missing_step": gap.step.id,
                        "missing_label": gap.step.label,
                        "matched_steps": list(evaluation.matched_steps.keys()),
                    },
                    escalate_to_t2=severity in ("warn", "critical"),
                    flow_context=evaluation.flow.name,
                    logql_query='{app="sim-steward"} | json',
                ))

        return findings
