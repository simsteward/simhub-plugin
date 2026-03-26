"""Investigation chain: Gather (deterministic) -> Analyze (LLM) -> Extract (deterministic)."""

import json
import logging
import re
import time

import requests

from investigator.knowledge import SYSTEM_PROMPT
from investigator.prompts import GATHER_SPECS, INVESTIGATION_PROMPT, GatherQuery
from loki_client import LokiClient
from models import Finding, Investigation, TimeWindow

logger = logging.getLogger("sentinel.investigator")


class InvestigationChain:
    def __init__(
        self,
        ollama_url: str,
        model_fast: str,
        model_deep: str,
        loki: LokiClient,
    ):
        self.ollama_url = ollama_url.rstrip("/")
        self.model_fast = model_fast
        self.model_deep = model_deep
        self.loki = loki

    # ── Public entry point ──

    def investigate(self, finding: Finding) -> Investigation:
        """Run the full Gather -> Analyze -> Extract pipeline."""
        # 1. GATHER: run Loki queries for this detector type
        context_lines = self._gather(finding)

        # 2. FORMAT: assemble the prompt
        formatted_evidence = self._format_evidence(finding)
        formatted_context = self._format_context(context_lines)
        prompt = INVESTIGATION_PROMPT.format(
            detector=finding.detector,
            severity=finding.severity,
            title=finding.title,
            summary=finding.summary,
            formatted_evidence=formatted_evidence,
            formatted_context=formatted_context,
        )

        total_context_lines = sum(len(lines) for lines in context_lines.values())

        # 3. ANALYZE (fast model)
        logger.info(
            "Investigating finding %s with %s (%d context lines)",
            finding.finding_id[:8],
            self.model_fast,
            total_context_lines,
        )
        start_ms = _now_ms()
        raw_response = self._call_ollama(self.model_fast, SYSTEM_PROMPT, prompt)
        duration_ms = _now_ms() - start_ms

        # 4. EXTRACT structured fields
        parsed = self._extract(raw_response)
        model_used = self.model_fast

        # 5. ESCALATE if confidence is low and deep model is available
        if parsed["confidence"] == "low" and self.model_deep:
            logger.info(
                "Escalating finding %s to %s (confidence: low)",
                finding.finding_id[:8],
                self.model_deep,
            )
            start_ms = _now_ms()
            raw_response = self._call_ollama(self.model_deep, SYSTEM_PROMPT, prompt)
            duration_ms = _now_ms() - start_ms
            parsed = self._extract(raw_response)
            model_used = self.model_deep

        # 6. Return Investigation
        return Investigation(
            finding=finding,
            root_cause=parsed["root_cause"],
            correlation=parsed["correlation"],
            impact=parsed["impact"],
            recommendation=parsed["recommendation"],
            confidence=parsed["confidence"],
            raw_response=raw_response,
            model=model_used,
            inference_duration_ms=duration_ms,
            context_lines_gathered=total_context_lines,
        )

    # ── Gather phase ──

    def _gather(self, finding: Finding) -> dict[str, list[dict]]:
        """Run gather queries for this detector, return label -> log lines."""
        specs = GATHER_SPECS.get(finding.detector, [])
        if not specs:
            logger.debug("No gather specs for detector '%s'", finding.detector)
            return {}

        results: dict[str, list[dict]] = {}
        for spec in specs:
            window = TimeWindow.from_now(spec.lookback_sec)
            lines = self.loki.query_lines(
                spec.logql, window.start_ns, window.end_ns, limit=spec.limit
            )
            results[spec.label] = lines
            logger.debug(
                "Gather '%s': %d lines from %s",
                spec.label,
                len(lines),
                spec.logql[:80],
            )

        return results

    # ── Format helpers ──

    @staticmethod
    def _format_evidence(finding: Finding) -> str:
        """Format finding evidence as readable text."""
        if not finding.evidence:
            return "(no evidence attached)"

        parts = []
        for key, value in finding.evidence.items():
            if isinstance(value, list):
                for i, item in enumerate(value[:10], 1):
                    if isinstance(item, dict):
                        parts.append(f"  {key}[{i}]: {json.dumps(item)}")
                    else:
                        parts.append(f"  {key}[{i}]: {item}")
            elif isinstance(value, dict):
                parts.append(f"  {key}: {json.dumps(value)}")
            else:
                parts.append(f"  {key}: {value}")
        return "\n".join(parts)

    @staticmethod
    def _format_context(context_lines: dict[str, list[dict]]) -> str:
        """Format gathered Loki lines as a numbered list per label."""
        if not context_lines:
            return "(no additional context gathered)"

        sections = []
        for label, lines in context_lines.items():
            if not lines:
                sections.append(f"[{label}] (no results)")
                continue

            header = f"[{label}] ({len(lines)} lines):"
            numbered = []
            for i, line in enumerate(lines, 1):
                ts = line.get("timestamp", "??:??:??")
                # Extract HH:MM:SS from ISO timestamp
                if "T" in str(ts):
                    ts = str(ts).split("T")[-1][:8]
                level = line.get("level", "?")
                event = line.get("event", "")
                msg = line.get("message", "")[:120]
                numbered.append(f"[{i}] {ts} {level} {event}: {msg}")

            sections.append(header + "\n" + "\n".join(numbered))

        return "\n\n".join(sections)

    # ── Ollama call ──

    def _call_ollama(self, model: str, system_prompt: str, prompt: str) -> str:
        """Call Ollama generate API. Returns raw response text."""
        response = requests.post(
            f"{self.ollama_url}/api/generate",
            json={
                "model": model,
                "prompt": prompt,
                "system": system_prompt,
                "stream": False,
                "options": {
                    "temperature": 0.3,
                    "num_predict": 1024,
                    "top_p": 0.9,
                },
            },
            timeout=600,  # 10 min for 70B on CPU
        )
        response.raise_for_status()
        return response.json()["response"]

    # ── Extract phase ──

    @staticmethod
    def _extract(raw: str) -> dict:
        """Parse structured sections from LLM response."""
        sections = {
            "root_cause": "Unable to determine",
            "correlation": "Unable to determine",
            "impact": "Unable to determine",
            "recommendation": "Unable to determine",
            "confidence": "low",
        }

        patterns = {
            "root_cause": r"ROOT_CAUSE:\s*(.+?)(?=\nCORRELATION:|$)",
            "correlation": r"CORRELATION:\s*(.+?)(?=\nIMPACT:|$)",
            "impact": r"IMPACT:\s*(.+?)(?=\nRECOMMENDATION:|$)",
            "recommendation": r"RECOMMENDATION:\s*(.+?)(?=\nCONFIDENCE:|$)",
            "confidence": r"CONFIDENCE:\s*(low|medium|high)",
        }

        for key, pattern in patterns.items():
            match = re.search(pattern, raw, re.DOTALL | re.IGNORECASE)
            if match:
                sections[key] = match.group(1).strip()

        # Normalize confidence to one of the three valid values
        if sections["confidence"] not in ("low", "medium", "high"):
            sections["confidence"] = "low"

        return sections


def _now_ms() -> int:
    return int(time.time() * 1000)
