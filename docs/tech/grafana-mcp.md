# Grafana MCP

The Grafana MCP server is used from Cursor to create and query Grafana resources: folders, dashboards, Loki log queries, and (with sufficient token scope) alert rules and datasources.

## Instance

- **Grafana URL:** https://wgutmann.grafana.net (this project’s Grafana Cloud stack).

## Configuration

Configure the Grafana MCP in Cursor (or your MCP host) with:

- **GRAFANA_URL** — `https://wgutmann.grafana.net`
- **GRAFANA_API_TOKEN** (or equivalent) — API token for authentication

Exact env var names depend on the MCP server implementation; use the token that can call Grafana’s HTTP API (folders, dashboards, datasources, etc.).

## Privileged token

To **create** resources (folders, dashboards, alert rules), the token must have at least **Editor** role. To create or modify **datasources**, the token needs **Admin**.

Recommendation: use a service account or API token with Editor (or Admin if you want the agent to manage datasources) for the MCP used by Cursor agents.

## Usage

Agents can use Grafana MCP to:

- Create folders and dashboards (e.g. Sim Steward Telemetry)
- Query Loki for logs (`{app="simsteward"}`)
- List datasources, search dashboards, create annotations

Plugin telemetry is pushed by the SimHub plugin to Loki (Grafana Cloud Logs); the MCP does not push data. See [plugin/README.md](../../plugin/README.md) for Telemetry (Grafana Cloud) configuration.

A **Sim Steward Plugin** dashboard (folder: Sim Steward, uid: `simsteward-plugin`) visualizes plugin logs: all events, heartbeats per minute, status transitions, and exceptions. Data appears when the plugin is running with Loki URL/credentials configured.
