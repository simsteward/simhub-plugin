import os
import yaml
from models import FlowDefinition, FlowStep, FlowEvaluation, FlowGap


class FlowEngine:
    def __init__(self, definitions_dir: str):
        self.flows: dict[str, FlowDefinition] = {}
        self._load_definitions(definitions_dir)

    def _load_definitions(self, definitions_dir: str):
        """Load all .yml files from the definitions directory."""
        if not os.path.isdir(definitions_dir):
            return
        for filename in os.listdir(definitions_dir):
            if not filename.endswith((".yml", ".yaml")):
                continue
            path = os.path.join(definitions_dir, filename)
            with open(path) as f:
                raw = yaml.safe_load(f)
            flow = self._parse_definition(raw)
            self.flows[flow.name] = flow

    def _parse_definition(self, raw: dict) -> FlowDefinition:
        """Parse a YAML flow definition into a FlowDefinition object."""
        steps = []
        for s in raw.get("steps", []):
            steps.append(FlowStep(
                id=s["id"],
                event=s["event"],
                label=s.get("label", ""),
                filters=s.get("filters", {}),
                timeout_sec=s.get("timeout_sec", 0),
                optional=s.get("optional", False),
                next_steps=s.get("next", []),
            ))
        return FlowDefinition(
            name=raw["name"],
            display_name=raw.get("display_name", raw["name"]),
            description=raw.get("description", ""),
            source_doc=raw.get("source_doc", ""),
            steps=steps,
            expected_completion_sec=raw.get("expected_completion_sec", 0),
            gap_severity=raw.get("gap_severity", "warn"),
        )

    def evaluate(self, events: list[dict], flow_name: str = None) -> list[FlowEvaluation]:
        """
        Given a list of parsed log events (sorted by timestamp),
        evaluate all flows (or a specific one) and return evaluation results.
        """
        results = []
        flows_to_check = (
            [self.flows[flow_name]] if flow_name and flow_name in self.flows
            else list(self.flows.values())
        )
        for flow in flows_to_check:
            result = self._evaluate_flow(flow, events)
            results.append(result)
        return results

    def _evaluate_flow(self, flow: FlowDefinition, events: list[dict]) -> FlowEvaluation:
        """Walk events and match against flow steps. Identify gaps."""
        matched_steps = {}

        for event in events:
            event_name = event.get("event", "")
            event_fields = event.get("fields", {})

            for step in flow.steps:
                if step.id in matched_steps:
                    continue  # already matched
                if event_name != step.event:
                    continue
                # Check filters (compare as lowercase strings to handle YAML bool/int vs JSON string)
                if step.filters:
                    match = all(
                        str(event_fields.get(k, "")).lower() == str(v).lower()
                        or str(event.get(k, "")).lower() == str(v).lower()
                        for k, v in step.filters.items()
                    )
                    if not match:
                        continue
                matched_steps[step.id] = event

        # Find gaps: required steps not matched
        gaps = []
        for step in flow.steps:
            if not step.optional and step.id not in matched_steps:
                gaps.append(FlowGap(
                    step=step,
                    flow=flow,
                    description=f"Expected '{step.label}' ({step.event}) not found in log window",
                ))

        return FlowEvaluation(
            flow=flow,
            matched_steps=matched_steps,
            gaps=gaps,
        )
