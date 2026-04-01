"""Log Sentinel v3 — Flask health/status/trigger + background sentinel loop."""

import logging
import threading
import time

from flask import Flask, jsonify, request

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
    return jsonify({"status": "ok", "service": "log-sentinel", "version": "3.0"})


@app.route("/run", methods=["POST"])
def manual_run():
    result = sentinel.run_cycle()
    return jsonify({
        "status": "ok",
        "cycle_id": result.cycle_id,
        "cycle_num": result.cycle_num,
        "window_minutes": result.window_minutes,
        "timeline_event_count": result.timeline_event_count,
        "anomaly_count": result.anomaly_count,
        "duration_ms": result.duration_ms,
        "summary": result.t1.summary if result.t1 else None,
        "anomalies": result.t1.anomalies if result.t1 else [],
        "evidence_packet_count": len(result.t1.evidence_packets) if result.t1 else 0,
        "error": result.error,
    })


@app.route("/run_t2", methods=["POST"])
def manual_run_t2():
    t = threading.Thread(target=sentinel.run_t2_cycle, daemon=True)
    t.start()
    return jsonify({"status": "accepted", "message": "T2 cycle started in background"})


@app.route("/run_t3", methods=["POST"])
def manual_run_t3():
    t = threading.Thread(target=sentinel.run_t3_cycle, daemon=True)
    t.start()
    return jsonify({"status": "accepted", "message": "T3 cycle started in background"})


@app.route("/trigger", methods=["POST"])
def grafana_trigger():
    """Receive Grafana alert webhook. Dedup, parse, and dispatch trigger_cycle()."""
    payload = request.get_json(silent=True) or {}
    alerts = payload.get("alerts", [])
    if not alerts:
        return jsonify({"status": "ignored", "reason": "no alerts"}), 200

    fired_names = []
    now = time.time()
    trigger_tier = "t1"
    alert_lines = []

    for alert in alerts:
        labels = alert.get("labels", {})
        annotations = alert.get("annotations", {})
        alertname = labels.get("alertname", "unknown")
        tier = labels.get("trigger_tier", "t1")
        severity = labels.get("severity", "warn")
        starts_at = alert.get("startsAt", "")

        # Dedup: skip if same alertname fired within dedup window
        last_ts = sentinel._trigger_dedup.get(alertname, 0)
        if now - last_ts < config.dedup_window_sec:
            continue

        sentinel._trigger_dedup[alertname] = now
        fired_names.append(alertname)
        if tier == "t2":
            trigger_tier = "t2"

        description = annotations.get("description", annotations.get("summary", ""))
        alert_lines.append(
            f"  Alert: {alertname} ({severity})\n"
            f"  Fired: {starts_at}\n"
            f"  {description}"
        )

    if not fired_names:
        return jsonify({"status": "deduped"}), 200

    alert_context = "\n".join(alert_lines)
    sentinel.loki.push_trigger(
        {
            "alertname": ",".join(fired_names),
            "trigger_tier": trigger_tier,
            "alert_count": len(fired_names),
        },
        env=config.env_label,
    )

    # Run in background — webhook must return fast
    t = threading.Thread(
        target=sentinel.trigger_cycle,
        args=(alert_context, trigger_tier, fired_names),
        daemon=True,
    )
    t.start()

    return jsonify({"status": "accepted", "alerts": fired_names, "tier": trigger_tier}), 202


@app.route("/status", methods=["GET"])
def status():
    return jsonify({
        "version": "3.0",
        "sentinel_mode": config.sentinel_mode,
        "t1_interval_sec": config.t1_interval_sec,
        "t2_interval_sec": config.t2_interval_sec,
        "t3_interval_sec": config.t3_interval_sec,
        "lookback_sec": config.lookback_sec,
        "t2_enabled": config.t2_enabled,
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
