"""Detect host resource problems — CPU, memory, disk."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class ResourceHealthDetector(BaseDetector):
    name = "resource_health"
    category = "app"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        samples = cache.get("ss_resources")

        if not samples:
            return findings

        cpu_values: list[float] = []
        mem_values: list[float] = []

        for line in samples:
            fields = line.get("fields", {})
            cpu = _to_float(fields.get("cpu_percent"))
            mem = _to_float(fields.get("memory_percent"))

            if cpu is not None:
                cpu_values.append(cpu)
            if mem is not None:
                mem_values.append(mem)

        # CPU checks — use max observed value
        if cpu_values:
            peak_cpu = max(cpu_values)
            avg_cpu = sum(cpu_values) / len(cpu_values)

            if peak_cpu > 95:
                findings.append(Finding(
                    detector=self.name,
                    severity="critical",
                    title=f"CPU critical: {peak_cpu:.0f}% peak",
                    summary=f"CPU peaked at {peak_cpu:.0f}% (avg {avg_cpu:.0f}%) across {len(cpu_values)} samples",
                    category=self.category,
                    evidence={"peak_cpu": round(peak_cpu, 1), "avg_cpu": round(avg_cpu, 1), "samples": len(cpu_values)},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="host_resource_sample"} | json',
                ))
            elif peak_cpu > 80:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"CPU elevated: {peak_cpu:.0f}% peak",
                    summary=f"CPU peaked at {peak_cpu:.0f}% (avg {avg_cpu:.0f}%) across {len(cpu_values)} samples",
                    category=self.category,
                    evidence={"peak_cpu": round(peak_cpu, 1), "avg_cpu": round(avg_cpu, 1), "samples": len(cpu_values)},
                    escalate_to_t2=False,
                    logql_query='{app="sim-steward", event="host_resource_sample"} | json',
                ))

        # Memory checks
        if mem_values:
            peak_mem = max(mem_values)
            avg_mem = sum(mem_values) / len(mem_values)

            if peak_mem > 95:
                findings.append(Finding(
                    detector=self.name,
                    severity="critical",
                    title=f"Memory critical: {peak_mem:.0f}% peak",
                    summary=f"Memory peaked at {peak_mem:.0f}% (avg {avg_mem:.0f}%) across {len(mem_values)} samples",
                    category=self.category,
                    evidence={"peak_mem": round(peak_mem, 1), "avg_mem": round(avg_mem, 1), "samples": len(mem_values)},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="host_resource_sample"} | json',
                ))
            elif peak_mem > 85:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"Memory elevated: {peak_mem:.0f}% peak",
                    summary=f"Memory peaked at {peak_mem:.0f}% (avg {avg_mem:.0f}%) across {len(mem_values)} samples",
                    category=self.category,
                    evidence={"peak_mem": round(peak_mem, 1), "avg_mem": round(avg_mem, 1), "samples": len(mem_values)},
                    escalate_to_t2=False,
                    logql_query='{app="sim-steward", event="host_resource_sample"} | json',
                ))

        return findings


def _to_float(val) -> float | None:
    if val is None:
        return None
    try:
        return float(val)
    except (ValueError, TypeError):
        return None
