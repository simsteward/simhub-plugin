"""FlowEngine — load YAML flow definitions and evaluate event sequences."""

import os

import yaml

from models import FlowDefinition, FlowEvaluation, FlowGap, FlowStep


class FlowEngine:
    def __init__(self, definitions_dir: str):
        self.flows: dict[str, FlowDefinition] = {}
        self._load_definitions(definitions_dir)

    def _load_definitions(self, definitions_dir: str):
        if not os.path.isdir(definitions_dir):
            return
        for f in os.listdir(definitions_dir):
            if not f.endswith((".yml", ".yaml")):
                continue
            with open(os.path.join(definitions_dir, f)) as fh:
                raw = yaml.safe_load(fh)
            flow = self._parse(raw)
            self.flows[flow.name] = flow

    def _parse(self, raw: dict) -> FlowDefinition:
        steps = [
            FlowStep(
                id=s["id"],
                event=s["event"],
                label=s.get("label", ""),
                filters=s.get("filters", {}),
                timeout_sec=s.get("timeout_sec", 0),
                optional=s.get("optional", False),
                next_steps=s.get("next", []),
            )
            for s in raw.get("steps", [])
        ]
        return FlowDefinition(
            name=raw["name"],
            display_name=raw.get("display_name", raw["name"]),
            description=raw.get("description", ""),
            source_doc=raw.get("source_doc", ""),
            steps=steps,
            expected_completion_sec=raw.get("expected_completion_sec", 0),
            gap_severity=raw.get("gap_severity", "warn"),
        )

    def evaluate(self, events: list[dict], flow_name: str | None = None) -> list[FlowEvaluation]:
        results = []
        if flow_name and flow_name in self.flows:
            targets = [self.flows[flow_name]]
        else:
            targets = list(self.flows.values())

        for flow in targets:
            matched: dict[str, dict] = {}
            for event in events:
                for step in flow.steps:
                    if step.id in matched:
                        continue
                    if event.get("event", "") != step.event:
                        continue
                    if step.filters:
                        fields = event.get("fields", {})
                        if not all(
                            str(fields.get(k, "")).lower() == str(v).lower()
                            or str(event.get(k, "")).lower() == str(v).lower()
                            for k, v in step.filters.items()
                        ):
                            continue
                    matched[step.id] = event

            gaps = [
                FlowGap(
                    step=s,
                    flow=flow,
                    description=f"Expected '{s.label}' ({s.event}) not found",
                )
                for s in flow.steps
                if not s.optional and s.id not in matched
            ]
            results.append(FlowEvaluation(flow=flow, matched_steps=matched, gaps=gaps))

        return results
