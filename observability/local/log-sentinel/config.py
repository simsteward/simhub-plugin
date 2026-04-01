"""Configuration from environment variables."""

import os


class Config:
    def __init__(self):
        self.loki_url = os.environ.get("LOKI_URL", "http://loki:3100")
        self.grafana_url = os.environ.get("GRAFANA_URL", "http://grafana:3000")
        self.grafana_user = os.environ.get("GRAFANA_USER", "admin")
        self.grafana_password = os.environ.get("GRAFANA_PASSWORD", "admin")
        self.ollama_url = os.environ.get("OLLAMA_URL", "http://host.docker.internal:11434")
        self.ollama_model_fast = os.environ.get("OLLAMA_MODEL_FAST", "qwen3:8b")
        self.ollama_model_deep = os.environ.get("OLLAMA_MODEL_DEEP", "qwen3:32b")
        self.poll_interval_sec = int(os.environ.get("SENTINEL_POLL_INTERVAL_SEC", "60"))
        self.lookback_sec = int(os.environ.get("SENTINEL_LOOKBACK_SEC", "300"))
        self.t2_enabled = os.environ.get("SENTINEL_T2_ENABLED", "true").lower() == "true"
        self.t2_proactive_interval_sec = int(os.environ.get("SENTINEL_T2_PROACTIVE_INTERVAL_SEC", "300"))
        self.dedup_window_sec = int(os.environ.get("SENTINEL_DEDUP_WINDOW_SEC", "300"))
        self.env_label = os.environ.get("SIMSTEWARD_LOG_ENV", "local")
        self.sentry_dsn = os.environ.get("SENTINEL_SENTRY_DSN", "")
        # v3 additions
        self.sentinel_mode = os.environ.get("SENTINEL_MODE", "dev")  # "dev" | "prod"
        self.t1_interval_sec = int(os.environ.get("SENTINEL_T1_INTERVAL_SEC", "300"))   # 5 min
        self.t2_interval_sec = int(os.environ.get("SENTINEL_T2_INTERVAL_SEC", "900"))   # 15 min
        self.t3_interval_sec = int(os.environ.get("SENTINEL_T3_INTERVAL_SEC", "7200"))  # 2h (dev default)
        self.merge_window_sec = int(os.environ.get("SENTINEL_MERGE_WINDOW_SEC", "10"))  # T0 batch window
        self.sentry_auth_token = os.environ.get("SENTRY_AUTH_TOKEN", "")
        self.sentry_org = os.environ.get("SENTRY_ORG", "")
        self.sentry_project = os.environ.get("SENTRY_PROJECT", "")
        self.baseline_path = os.environ.get("SENTINEL_BASELINE_PATH", "/data/baselines.json")

    @classmethod
    def from_env(cls):
        return cls()
