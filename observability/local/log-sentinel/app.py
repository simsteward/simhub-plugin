"""Log Sentinel v2 — Flask health/status/manual-trigger + background sentinel loop."""

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

# Push process logs to Loki
loki_handler = LokiHandler(config.loki_url, env=config.env_label)
loki_handler.setLevel(logging.INFO)
logging.getLogger().addHandler(loki_handler)

app = Flask(__name__)
sentinel = Sentinel(config)


@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok", "service": "log-sentinel", "version": "2.0"})


@app.route("/run", methods=["POST"])
def manual_run():
    sentinel.run_cycle()
    return jsonify({"status": "ok", "message": "Cycle triggered"})


@app.route("/status", methods=["GET"])
def status():
    app_dets = [d.name for d in sentinel.detectors if d.category == "app"]
    ops_dets = [d.name for d in sentinel.detectors if d.category == "ops"]
    return jsonify({
        "version": "2.0",
        "poll_interval_sec": config.poll_interval_sec,
        "lookback_sec": config.lookback_sec,
        "t2_enabled": config.t2_enabled,
        "t2_proactive_interval_sec": config.t2_proactive_interval_sec,
        "dedup_window_sec": config.dedup_window_sec,
        "detectors": {"app": app_dets, "ops": ops_dets, "total": len(sentinel.detectors)},
        "models": {"fast": config.ollama_model_fast, "deep": config.ollama_model_deep},
        "sentry_enabled": sentinel.sentry.enabled,
        "stats": sentinel._stats,
        "circuit_breakers": {
            "loki": sentinel.loki_breaker.state,
            "ollama": sentinel.ollama_breaker.state,
        },
    })


if __name__ == "__main__":
    t = threading.Thread(target=sentinel.start, daemon=True)
    t.start()
    app.run(host="0.0.0.0", port=8081, debug=False)
