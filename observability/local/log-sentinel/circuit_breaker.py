"""Circuit breaker for dependency health (Loki, Ollama)."""

import logging
import time

logger = logging.getLogger("sentinel.circuit")


class CircuitBreaker:
    """Track consecutive failures and skip calls during backoff."""

    CLOSED = "closed"
    OPEN = "open"
    HALF_OPEN = "half_open"

    def __init__(self, name: str, failure_threshold: int = 3, backoff_sec: int = 60):
        self.name = name
        self.failure_threshold = failure_threshold
        self.backoff_sec = backoff_sec
        self.state = self.CLOSED
        self.consecutive_failures = 0
        self.last_failure_time = 0.0

    def allow_request(self) -> bool:
        if self.state == self.CLOSED:
            return True
        if self.state == self.OPEN:
            if time.time() - self.last_failure_time >= self.backoff_sec:
                self.state = self.HALF_OPEN
                logger.info("Circuit %s half-open, trying one request", self.name)
                return True
            return False
        # HALF_OPEN — allow one probe
        return True

    def record_success(self):
        if self.state != self.CLOSED:
            logger.info("Circuit %s closed (recovered)", self.name)
        self.state = self.CLOSED
        self.consecutive_failures = 0

    def record_failure(self):
        self.consecutive_failures += 1
        self.last_failure_time = time.time()
        if self.consecutive_failures >= self.failure_threshold:
            if self.state != self.OPEN:
                logger.warning(
                    "Circuit %s OPEN after %d failures, backing off %ds",
                    self.name, self.consecutive_failures, self.backoff_sec,
                )
            self.state = self.OPEN
