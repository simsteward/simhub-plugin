"""Configuration from environment variables."""

import os


class Config:
    def __init__(self):
        self.loki_url = os.environ.get("LOKI_URL", "http://loki:3100")
        self.grafana_url = os.environ.get("GRAFANA_URL", "http://grafana:3000")
        self.grafana_user = os.environ.get("GRAFANA_USER", "admin")
        self.grafana_password = os.environ.get("GRAFANA_PASSWORD", "admin")
        self.ollama_url = os.environ.get("OLLAMA_URL", "http://host.docker.internal:11434")
        self.ollama_model_fast = os.environ.get("OLLAMA_MODEL_FAST", "deepseek-r1:8b")
        self.ollama_model_deep = os.environ.get("OLLAMA_MODEL_DEEP", "llama3.3:70b-instruct-q4_K_M")
        self.poll_interval_sec = int(os.environ.get("SENTINEL_POLL_INTERVAL_SEC", "60"))
        self.lookback_sec = int(os.environ.get("SENTINEL_LOOKBACK_SEC", "300"))
        self.t2_enabled = os.environ.get("SENTINEL_T2_ENABLED", "true").lower() == "true"
        self.env_label = os.environ.get("SIMSTEWARD_LOG_ENV", "local")

    @classmethod
    def from_env(cls):
        return cls()
