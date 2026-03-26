"""Base detector interface for Tier 1."""

from abc import ABC, abstractmethod

from models import Finding
from query_cache import CycleQueryCache


class BaseDetector(ABC):
    name: str = "base"
    category: str = "app"  # "app" | "ops"

    @abstractmethod
    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        """Run detection logic against cached query results. Return findings."""
        ...
