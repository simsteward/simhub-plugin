"""InvestigationChain — Tier 2 LLM-driven investigation of findings."""

import json
import logging
import re
import time

import requests

from investigator.knowledge import SYSTEM_PROMPT
from investigator.prompts import (
    GATHER_SPECS,
    INVESTIGATION_PROMPT,
    PATTERN_ANALYSIS_PROMPT,
)
from loki_client import LokiClient
from models import Finding, Investigation, TimeWindow

logger = logging.getLogger("sentinel.investigator")


class InvestigationChain:
    def __init__(self, ollama_url: str, model_fast: str, model_deep: str, loki: LokiClient):
        self.ollama_url = ollama_url.rstrip("/")
        self.model_fast = model_fast
        self.model_deep = model_deep
        self.loki = loki

    # ── Public API ──

    def investigate(self, finding: Finding) -> Investigation:
        """Gather context, call fast model, extract structured result. Escalate to deep model on low confidence."""
        gather_start = time.time()
        context_lines = self._gather(finding)
        gather_ms = int((time.time() - gather_start) * 1000)

        total_lines = sum(len(v) for v in context_lines.values())

        prompt = INVESTIGATION_PROMPT.format(
            title=finding.title,
            detector=finding.detector,
            severity=finding.severity,
            summary=finding.summary,
            evidence=self._format_evidence(finding),
            context=self._format_context(context_lines),
        )

        infer_start = time.time()
        raw = self._call_ollama(self.model_fast, SYSTEM_PROMPT, prompt)
        infer_ms = int((time.time() - infer_start) * 1000)

        parsed = self._extract(raw)

        # Escalate to deep model on low confidence
        model_used = self.model_fast
        if parsed.get("confidence", "low") == "low" and self.model_deep != self.model_fast:
            logger.info("Low confidence from fast model, escalating to %s", self.model_deep)
            deep_start = time.time()
            raw_deep = self._call_ollama(self.model_deep, SYSTEM_PROMPT, prompt)
            deep_ms = int((time.time() - deep_start) * 1000)
            parsed = self._extract(raw_deep)
            raw = raw_deep
            infer_ms += deep_ms
            model_used = self.model_deep

        return Investigation(
            finding=finding,
            root_cause=parsed.get("root_cause", "Unable to determine root cause."),
            correlation=parsed.get("correlation", "No correlations identified."),
            impact=parsed.get("impact", "Impact unknown."),
            recommendation=parsed.get("recommendation", "Investigate manually."),
            confidence=parsed.get("confidence", "low"),
            issue_type=parsed.get("issue_type", "unknown"),
            trigger="escalation",
            raw_response=raw,
            model=model_used,
            inference_duration_ms=infer_ms,
            gather_duration_ms=gather_ms,
            context_lines_gathered=total_lines,
        )

    def investigate_patterns(self, recent_findings: list[Finding]) -> Investigation:
        """Proactive T2: analyze recent findings for cross-cutting patterns."""
        summaries = "\n".join(
            f"- [{f.severity}] {f.detector}: {f.title} — {f.summary}"
            for f in recent_findings
        )

        prompt = PATTERN_ANALYSIS_PROMPT.format(
            count=len(recent_findings),
            window_min=5,
            finding_summaries=summaries,
        )

        infer_start = time.time()
        raw = self._call_ollama(self.model_fast, SYSTEM_PROMPT, prompt)
        infer_ms = int((time.time() - infer_start) * 1000)

        parsed = self._extract(raw)

        # Use first finding as the anchor
        anchor = recent_findings[0] if recent_findings else Finding(
            detector="pattern_analysis",
            severity="info",
            title="Pattern analysis",
            summary="No findings to analyze.",
        )

        return Investigation(
            finding=anchor,
            root_cause=parsed.get("root_cause", "No common root cause identified."),
            correlation=parsed.get("correlation", "No correlations identified."),
            impact=parsed.get("impact", "Impact unknown."),
            recommendation=parsed.get("recommendation", "Continue monitoring."),
            confidence=parsed.get("confidence", "low"),
            issue_type=parsed.get("issue_type", "unknown"),
            trigger="proactive",
            raw_response=raw,
            model=self.model_fast,
            inference_duration_ms=infer_ms,
            gather_duration_ms=0,
            context_lines_gathered=0,
        )

    # ── Ollama call ──

    def _call_ollama(self, model: str, system: str, prompt: str) -> str:
        """POST /api/generate to Ollama. Returns raw text response."""
        try:
            resp = requests.post(
                f"{self.ollama_url}/api/generate",
                json={
                    "model": model,
                    "system": system,
                    "prompt": prompt,
                    "stream": False,
                    "options": {
                        "temperature": 0.3,
                        "num_predict": 1024,
                    },
                },
                timeout=600,
            )
            if resp.status_code != 200:
                logger.warning("Ollama returned %d: %s", resp.status_code, resp.text[:200])
                return ""
            return resp.json().get("response", "")
        except requests.exceptions.Timeout:
            logger.warning("Ollama request timed out (model=%s)", model)
            return ""
        except Exception as e:
            logger.warning("Ollama call failed: %s", e)
            return ""

    # ── Extract structured fields from raw LLM output ──

    def _extract(self, raw: str) -> dict:
        """Parse ROOT_CAUSE, CORRELATION, IMPACT, RECOMMENDATION, CONFIDENCE, ISSUE_TYPE from raw text."""
        result = {}

        patterns = {
            "root_cause": r"ROOT_CAUSE:\s*(.+?)(?=\n(?:CORRELATION|IMPACT|RECOMMENDATION|CONFIDENCE|ISSUE_TYPE):|$)",
            "correlation": r"CORRELATION:\s*(.+?)(?=\n(?:IMPACT|RECOMMENDATION|CONFIDENCE|ISSUE_TYPE):|$)",
            "impact": r"IMPACT:\s*(.+?)(?=\n(?:RECOMMENDATION|CONFIDENCE|ISSUE_TYPE):|$)",
            "recommendation": r"RECOMMENDATION:\s*(.+?)(?=\n(?:CONFIDENCE|ISSUE_TYPE):|$)",
            "confidence": r"CONFIDENCE:\s*(low|medium|high)",
            "issue_type": r"ISSUE_TYPE:\s*(bug|config|performance|security|unknown)",
        }

        for key, pattern in patterns.items():
            match = re.search(pattern, raw, re.DOTALL | re.IGNORECASE)
            if match:
                result[key] = match.group(1).strip()

        # Normalize confidence
        confidence = result.get("confidence", "low").lower()
        if confidence not in ("low", "medium", "high"):
            confidence = "low"
        result["confidence"] = confidence

        # Normalize issue_type
        issue_type = result.get("issue_type", "unknown").lower()
        if issue_type not in ("bug", "config", "performance", "security", "unknown"):
            issue_type = "unknown"
        result["issue_type"] = issue_type

        return result

    # ── Gather context from Loki ──

    def _gather(self, finding: Finding) -> dict[str, list[dict]]:
        """Run GATHER_SPECS queries for the finding's detector, return label -> lines."""
        specs = GATHER_SPECS.get(finding.detector, [])
        if not specs:
            logger.debug("No gather specs for detector %s", finding.detector)
            return {}

        result: dict[str, list[dict]] = {}
        for spec in specs:
            window = TimeWindow.from_now(spec.lookback_sec)
            lines = self.loki.query_lines(spec.logql, window.start_ns, window.end_ns, limit=spec.limit)
            result[spec.label] = lines

        return result

    # ── Format helpers ──

    @staticmethod
    def _format_evidence(finding: Finding) -> str:
        """Format finding evidence as indented JSON."""
        if not finding.evidence:
            return "(no evidence attached)"
        try:
            return json.dumps(finding.evidence, indent=2, default=str)
        except (TypeError, ValueError):
            return str(finding.evidence)

    @staticmethod
    def _format_context(context_lines: dict[str, list[dict]]) -> str:
        """Format gathered context as numbered lists per label."""
        if not context_lines:
            return "(no additional context gathered)"

        sections = []
        for label, lines in context_lines.items():
            if not lines:
                sections.append(f"[{label}]: (0 lines)")
                continue
            numbered = "\n".join(f"  {i+1}. {json.dumps(line, default=str)}" for i, line in enumerate(lines[:50]))
            sections.append(f"[{label}] ({len(lines)} lines):\n{numbered}")

        return "\n\n".join(sections)
