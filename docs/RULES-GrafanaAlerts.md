# Grafana Alert Rules — Development Covenant

Every behavioral change to the plugin, dashboard, or LLM integration **must include a
corresponding Grafana alert review**. Silence is not the same as passing.

**Canonical spec:** `docs/superpowers/specs/2026-03-30-grafana-alerts-design.md`
**Alert YAML files:** `observability/local/grafana/provisioning/alerting/`

---

## Change → Domain Mapping

| Change type | Domain(s) to review |
|---|---|
| New action handler in `DispatchAction` | Domain 3 — check `action-failure-streak` thresholds |
| New iRacing SDK event handler | Domain 3 and/or Domain 7 — check incident/replay rules |
| New Claude API integration | Domains 4 + 5 — session health and cost rules |
| New MCP tool added | Domain 4 — `mcp-service-errors`, `tool-loop-detected` |
| New log event or field added | Check all domains — does it need a new alert? |
| Removing or renaming a log event | Search alert YAMLs for old name — alert will go **silent**, not fire |
| Changing cost fields in token metrics | Domain 5 — all cost threshold alerts |
| Changing session lifecycle events | Domains 3, 4, 8 — session start/end correlation |
| Sentinel code change | Domain 6 — self-health rules |
| Grafana dashboard change | Domain 8 — cross-stream rules may need annotation updates |

---

## Alert Silence ≠ Alert Passing

When you rename or remove a log event:
- The alert query will return **no data** (not 0)
- If `noDataState: OK` — the alert silently stops firing
- This is a **silent regression** — harder to detect than a real alert

Always check `noDataState` when modifying events that existing alerts depend on.

---

## Testing New Alerts

To verify an alert fires correctly before relying on it:

1. **Write a test event to Loki** via the gateway:
   ```bash
   curl -X POST http://localhost:3500/loki/api/v1/push \
     -H "Content-Type: application/json" \
     -d '{
       "streams": [{
         "stream": {"app": "sim-steward", "env": "local", "level": "ERROR"},
         "values": [["'"$(date +%s%N)"'", "{\"level\":\"ERROR\",\"event\":\"test\",\"message\":\"test alert\"}"]]
       }]
     }'
   ```

2. **Temporarily lower the threshold** in the alert rule to `0` and set the evaluation interval to `10s` in Grafana UI (do not commit this change).

3. **Verify the alert fires** in Grafana UI → Alerting → Alert Rules within the evaluation window.

4. **Verify the `/trigger` webhook** receives the payload:
   ```bash
   # Check log-sentinel logs
   docker compose logs log-sentinel --tail=20
   ```

5. **Restore the threshold** before committing any YAML changes.

---

## Alert Catalog Summary

| File | Domains | Count |
|---|---|---|
| `rules-infrastructure.yml` | 1+2: Infrastructure & Deploy Quality | 10 |
| `rules-iracing.yml` | 3+7: iRacing Session + Replay | 10 |
| `rules-claude-sessions.yml` | 4: Claude Code Session Health | 7 |
| `rules-token-cost.yml` | 5: Token & Cost Budget | 7 |
| `rules-sentinel-health.yml` | 6: Sentinel Self-Health | 7 |
| `rules-cross-stream.yml` | 8: Cross-Stream Correlation | 5 |
| **Total** | | **46** |

T2-tier alerts (skip `needs_t2` gate, escalate immediately):
`subagent-explosion`, `tool-loop-detected`, `session-cost-critical`, `daily-spend-critical`,
`ws-claude-coinflict`, `session-token-abandon`, `action-fail-session-fail`, `deploy-triple-signal`

---

## PR Checklist Addition

For any PR modifying plugin behavior, add to the review checklist:

- [ ] Reviewed Grafana alert domains for impacted change type (see table above)
- [ ] If log events were renamed/removed: verified no alert queries silently break
- [ ] If new log events added: considered whether a new alert rule is warranted
