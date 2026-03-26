"""Log Sentinel — Flask health/status/manual-trigger + background sentinel loop."""

import logging
import threading

from flask import Flask, jsonify

from config import Config
from loki_handler import LokiHandler
from sentinel import Sentinel

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(name)-20s %(levelname)-5s %(message)s",
)

config = Config.from_env()

# Push process logs to Loki so they appear in Grafana
loki_handler = LokiHandler(config.loki_url, env=config.env_label)
loki_handler.setLevel(logging.INFO)
logging.getLogger().addHandler(loki_handler)

app = Flask(__name__)
sentinel = Sentinel(config)


@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok", "service": "log-sentinel"})


@app.route("/run", methods=["POST"])
def manual_run():
    """Trigger a detection cycle manually."""
    sentinel.run_cycle()
    return jsonify({"status": "ok", "message": "Cycle triggered"})


@app.route("/status", methods=["GET"])
def status():
    return jsonify(
        {
            "poll_interval_sec": config.poll_interval_sec,
            "lookback_sec": config.lookback_sec,
            "t2_enabled": config.t2_enabled,
            "detectors": [d.name for d in sentinel.detectors],
            "models": {
                "fast": config.ollama_model_fast,
                "deep": config.ollama_model_deep,
            },
            "cycles_completed": sentinel._cycle_count,
        }
    )


if __name__ == "__main__":
    t = threading.Thread(target=sentinel.start, daemon=True)
    t.start()
    app.run(host="0.0.0.0", port=8081, debug=False)
