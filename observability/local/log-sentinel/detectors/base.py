"""Base detector interface for Tier 1."""

from abc import ABC, abstractmethod

from loki_client import LokiClient
from models import Finding, TimeWindow


class BaseDetector(ABC):
    name: str = "base"

    @abstractmethod
    def detect(self, loki: LokiClient, window: TimeWindow) -> list[Finding]:
        """Run detection logic and return zero or more findings."""
        ...
