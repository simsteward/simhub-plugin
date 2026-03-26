"""Tier 1 detector: missing steps in expected event flows."""

from detectors.base import BaseDetector
from loki_client import LokiClient
from models import Finding, FlowGap, TimeWindow

QUERY = '{app="sim-steward"} | json | event != "host_resource_sample"'


class FlowGapDetector(BaseDetector):
    name = "flow_gap"

    def __init__(self, flow_engine):
        """Accept a FlowEngine instance for evaluating event sequences."""
        self.flow_engine = flow_engine

    def detect(self, loki: LokiClient, window: TimeWindow) -> list[Finding]:
        lines = loki.query_lines(QUERY, window.start_ns, window.end_ns)
        if not lines:
            return []

        evaluations = self.flow_engine.evaluate(lines)
        findings: list[Finding] = []

        for evaluation in evaluations:
            if evaluation.complete:
                continue

            for gap in evaluation.gaps:
                severity = evaluation.flow.gap_severity or "warn"

                findings.append(
                    Finding(
                        detector=self.name,
                        severity=severity,
                        title=(
                            f"Flow gap in '{evaluation.flow.display_name}': "
                            f"missing '{gap.step.label}'"
                        ),
                        summary=(
                            gap.description
                            or (
                                f"Expected step '{gap.step.label}' "
                                f"(event: {gap.step.event}) was not observed "
                                f"in flow '{evaluation.flow.display_name}'."
                            )
                        ),
                        evidence={
                            "flow_name": evaluation.flow.name,
                            "flow_display_name": evaluation.flow.display_name,
                            "missing_step_id": gap.step.id,
                            "missing_step_event": gap.step.event,
                            "missing_step_label": gap.step.label,
                            "matched_steps": list(evaluation.matched_steps.keys()),
                            "total_gaps": len(evaluation.gaps),
                        },
                        flow_context=evaluation.flow.name,
                        logql_query=QUERY,
                    )
                )

        return findings
