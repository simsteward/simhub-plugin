# Grafana Alert Rules — Development Covenant

Every behavioral change to the plugin or dashboard **must include a corresponding Grafana Cloud alert review**. Silence is not the same as passing.

**Alert rules location:** Grafana Cloud (provisioned directly; no local YAML).

---

## Change → Domain Mapping

| Change type | Domain(s) to review |
|---|---|
| New action handler in `DispatchAction` | Domain 3 — check `action-failure-streak` thresholds |
| New iRacing SDK event handler | Domain 3 and/or Domain 7 — check incident/replay rules |
| New Claude API integration | Domains 4 + 5 — session health and cost rules |
| New MCP tool added | Domain 4 — `mcp-service-errors`, `tool-loop-detected` |
| New log event or field added | Check all domains — does it need a new alert? |
| Removing or renaming a log event | Search Grafana Cloud alert rules for old name — alert will go **silent**, not fire |
| Changing cost fields in token metrics | Domain 5 — all cost threshold alerts |
| Changing session lifecycle events | Domains 3, 4, 8 — session start/end correlation |
| Grafana dashboard change | Domain 8 — cross-stream rules may need annotation updates |

---

## Alert Silence ≠ Alert Passing

When you rename or remove a log event:
- The alert query will return **no data** (not 0)
- If `noDataState: OK` — the alert silently stops firing
- This is a **silent regression** — harder to detect than a real alert

Always check `noDataState` when modifying events that existing alerts depend on.

---

## Alert Catalog Summary

| Domain | Description | Count |
|---|---|---|
| 1+2 | Infrastructure & Deploy Quality | 10 |
| 3+7 | iRacing Session + Replay | 10 |
| 4 | Claude Code Session Health | 7 |
| 5 | Token & Cost Budget | 7 |
| 8 | Cross-Stream Correlation | 5 |
| **Total** | | **39** |

(Domain 6 — Sentinel Self-Health — was removed alongside the Sentinel deletion.)

T2-tier alerts (escalate immediately):
`subagent-explosion`, `tool-loop-detected`, `session-cost-critical`, `daily-spend-critical`,
`ws-claude-coinflict`, `session-token-abandon`, `action-fail-session-fail`, `deploy-triple-signal`

---

## PR Checklist Addition

For any PR modifying plugin behavior, add to the review checklist:

- [ ] Reviewed Grafana alert domains for impacted change type (see table above)
- [ ] If log events were renamed/removed: verified no alert queries silently break in Grafana Cloud
- [ ] If new log events added: considered whether a new alert rule is warranted
