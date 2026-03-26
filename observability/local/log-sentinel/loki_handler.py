"""Python logging handler that pushes log records to Loki."""

import json
import logging
import time
import threading

import requests


class LokiHandler(logging.Handler):
    def __init__(self, loki_url: str, env: str = "local", flush_interval: float = 2.0):
        super().__init__()
        self.loki_url = loki_url.rstrip("/")
        self.env = env
        self.flush_interval = flush_interval
        self._buffer = []
        self._lock = threading.Lock()
        self._start_flush_timer()

    def _start_flush_timer(self):
        self._timer = threading.Timer(self.flush_interval, self._flush_loop)
        self._timer.daemon = True
        self._timer.start()

    def _flush_loop(self):
        self._flush()
        self._start_flush_timer()

    def emit(self, record: logging.LogRecord):
        try:
            entry = {
                "level": record.levelname,
                "message": self.format(record),
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime(record.created)),
                "component": "log-sentinel",
                "event": "sentinel_log",
                "domain": "system",
                "logger": record.name,
                "func": record.funcName,
            }
            with self._lock:
                self._buffer.append(entry)
        except Exception:
            self.handleError(record)

    def _flush(self):
        with self._lock:
            if not self._buffer:
                return
            entries = self._buffer[:]
            self._buffer.clear()
        by_level = {}
        for e in entries:
            by_level.setdefault(e["level"], []).append(e)
        streams = []
        for level, group in by_level.items():
            values = [[str(int(time.time() * 1e9)), json.dumps(e)] for e in group]
            streams.append({
                "stream": {"app": "sim-steward", "env": self.env, "level": level, "component": "log-sentinel", "event": "sentinel_log", "domain": "system"},
                "values": values,
            })
        try:
            requests.post(f"{self.loki_url}/loki/api/v1/push", json={"streams": streams}, timeout=3)
        except Exception:
            pass
