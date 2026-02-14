# Loki logs setup guide

This guide walks you from zero through configuring **Grafana Cloud Loki** and the **Sim Steward** plugin in SimHub so that health telemetry (heartbeats, status transitions, exceptions) is sent to Grafana Cloud and visible in dashboards or Explore.

**Prerequisites:** A Grafana Cloud account (free tier is enough) and SimHub with the Sim Steward plugin installed and deployed.

**What we’re doing:** The Sim Steward plugin pushes log events over HTTPS to Grafana Cloud Logs (Loki). You need your stack’s Loki push URL, Instance ID (User), and an API token with permission to write logs. You then enter these in the plugin’s **TELEMETRY (GRAFANA CLOUD)** settings and click **Connect telemetry** to verify.

---

## Part 1: Get Loki URL and credentials from Grafana Cloud

Grafana’s UI can vary slightly by region and plan. If a step doesn’t match your screen, look for **Send data** or **Details** under Logs.

### 1. Sign in and open your stack

1. Go to [grafana.com](https://grafana.com) and sign in.
2. Open your **Grafana Cloud** context (e.g. **My Account** or **Cloud** in the top navigation, or go directly to your stack URL such as `https://<yourstack>.grafana.net`).
3. Select your **stack** (the stack name or **Details** for the stack you want to use for Sim Steward).

The stack overview shows your services (Grafana, Logs, Metrics, etc.). Source: [Your Grafana Cloud Stack](https://grafana.com/docs/grafana-cloud/security-and-account-management/cloud-stacks/).

![Grafana Cloud stack overview](https://grafana.com/media/docs/grafana-cloud/screenshot-cloud-stack.png)

### 2. Open Logs connection details

1. On the stack overview, find the **Logs** (or **Grafana Cloud Logs**) tile or card.
2. Click **Details** (or **Send data** / **Send logs**) for Logs.
3. You should see connection information: **URL**, **User**, and a way to get or create an **API token**.

Select your stack from the overview, then click **Details** next to the Logs service:

![Stack overview – select stack and Details for Loki](https://grafana.com/media/docs/grafana-cloud/account-portal/screenshot-grafana-stack-instance-endpoint-loki-1.PNG)

The next page contains the hosted Loki connection details (URL, User, API token):

![Loki connection details – URL, User, and token](https://grafana.com/media/docs/grafana-cloud/account-portal/screenshot-grafana-stack-instance-endpoint-loki-2.PNG)

### 3. Copy the Loki push URL

- The **URL** shown is your Loki push endpoint. It must be the **full** URL ending with `/loki/api/v1/push`.
- Format: `https://logs-prod-<region>.grafana.net/loki/api/v1/push`  
  where `<region>` is your stack’s region (e.g. `us-central-0`, `eu-west-1`).
- **Copy this full URL.** The Sim Steward plugin does **not** append `/loki/api/v1/push`; the value you paste must include it.
- If the UI shows only a host (e.g. without the path), append `/loki/api/v1/push` yourself to form the **Loki URL** you will use in the plugin.

### 4. Copy the User (Instance ID)

- The **User** field is a **numeric** value (your Grafana Cloud Logs instance ID).
- Copy this number. You will paste it into **Loki Username** in the Sim Steward plugin.

### 5. Create or copy an API token

- In the same Logs connection area, look for **Generate now**, **Create token**, or **Copy** for an API token used to send data (e.g. “Send data” / “HTTP API”).
- Create or copy a token that has **logs write** permission (or a role such as “MetricsPublisher” or “Logs” that can write logs).
- Copy the token **immediately**; it may not be shown again.
- This value goes into **Loki API Key** in SimHub. Do not share it or commit it to version control.

### 6. Alternative: Pre-encoded Basic token

If you already have a single Base64-encoded string of `instanceId:apiKey` (from Grafana or another tool):

- Leave **Loki Username** empty in the plugin.
- Paste the Base64 string into **Loki API Key**. The plugin will send `Authorization: Basic <this value>`.

---

## Part 2: Configure the Sim Steward plugin in SimHub

Use the **exact** field names and labels below; they match the plugin’s **TELEMETRY (GRAFANA CLOUD)** section.

### 1. Open Sim Steward settings

1. Open **SimHub**.
2. Go to where plugins are listed (e.g. **Plugins** or **Sim Steward** in the sidebar or menu).
3. Open the **Sim Steward** plugin, then open its **settings** (gear icon or **Settings** tab).

### 2. Locate the section **TELEMETRY (GRAFANA CLOUD)**

Find the section header **TELEMETRY (GRAFANA CLOUD)**. All of the following controls are in this section.

### 3. Enable telemetry

- Under **Enabled**, check the box **Send health telemetry**.

### 4. Loki URL

- In the **Loki URL** text box, paste the full Loki push URL from Part 1 (e.g. `https://logs-prod-<region>.grafana.net/loki/api/v1/push`).
- The plugin does not append `/loki/api/v1/push`; the URL must include it.

### 5. Loki Username

- In **Loki Username**, paste the **numeric** Instance ID (User) you copied from Grafana Cloud. Do not include any spaces or non-numeric characters.

### 6. Loki API Key

- In **Loki API Key** (the password-style field), paste the API token from Part 1.
- After you leave the field or save settings, the key is stored encrypted. **Key Status** should change to **Stored**.
- To change the key later, paste a new value into **Loki API Key**. Use **Clear** only if you want to remove the stored key entirely.

### 7. Optional settings (defaults are fine for first run)

- **Flush Interval (sec):** How often batched events are sent (default **5**).
- **Heartbeat Interval (sec):** How often a heartbeat is enqueued when telemetry is active (default **2**; range 1–60).
- **Log to disk:** If enabled, the same log lines are written to a local file. Optional.
- **Log directory (optional):** Override directory for disk log files; empty uses the default (e.g. `%LocalAppData%\Sim Steward\logs`). Takes effect on next SimHub start.

### 8. Save and connect

1. Save SimHub settings (e.g. click **Save** or close the settings window if your setup auto-saves).
2. Click the **Connect telemetry** button.
3. **Connection** should change from **Disconnected** to **Connected** (or show “Last success (UTC): …”). The **Heartbeat** indicator (heart glyph) should reflect a successful push.
4. If something fails, the error text under **Connection** will describe the problem; see **Troubleshooting** below.

---

## Part 3: Verify and view logs

### In SimHub

- After clicking **Connect telemetry**, confirm **Connection** shows **Connected** and that no error message appears below the Connection row.

### In Grafana

1. Open your Grafana stack (e.g. `https://<yourstack>.grafana.net`).

2. **Option A – Dashboard**  
   Go to **Dashboards** → **Sim Steward** → **Sim Steward Plugin**. The dashboard shows plugin logs, heartbeats per minute, status transitions, and exceptions. Data may take up to one flush interval (default 5 s) plus ingestion delay to appear; the dashboard may use a 30 s refresh.

3. **Option B – Explore**  
   Go to **Explore**, select the **Loki** (or Grafana Cloud Logs) datasource, and run:

   ```logql
   {app="simsteward"}
   ```

   Widen the time range if you don’t see recent lines. Logs can take up to one flush interval (default 5 s) plus a few seconds to show up.

   Grafana Explore with a Loki datasource (label browser and query). For Sim Steward, use the query `{app="simsteward"}`. Source: [Loki query editor](https://grafana.com/docs/grafana/latest/datasources/loki/query-editor/).

   ![Grafana Explore – Loki query](https://grafana.com/static/img/docs/explore/Loki_label_browser.png)

For event types and LogQL examples, see [Plugin logging schema](../tech/plugin-logging-schema.md). For the dashboard and MCP usage, see [Grafana MCP](../tech/grafana-mcp.md) and the [plugin README Telemetry section](../../plugin/README.md#telemetry-grafana-cloud).

---

## Screenshot list

Screenshots for **Grafana Cloud** (Part 1) and **Grafana Explore** (Part 3) are sourced from official Grafana documentation and embedded above via their URLs. Screenshots for **SimHub** and the **Sim Steward** dashboard are application-specific and are not available from public docs; capture them locally if you want to add them to a fork or internal doc.

| # | Description | Source |
|---|-------------|--------|
| 1 | Grafana Cloud: stack overview with services (Logs, etc.) | [Grafana docs](https://grafana.com/docs/grafana-cloud/security-and-account-management/cloud-stacks/) – `screenshot-cloud-stack.png` (embedded in Part 1) |
| 2 | Stack: select stack and **Details** for Loki | [Grafana docs](https://grafana.com/docs/grafana-cloud/security-and-account-management/cloud-stacks/) – `screenshot-grafana-stack-instance-endpoint-loki-1.PNG` (embedded in Part 1) |
| 3 | Loki connection details: **URL**, **User**, token | [Grafana docs](https://grafana.com/docs/grafana-cloud/security-and-account-management/cloud-stacks/) – `screenshot-grafana-stack-instance-endpoint-loki-2.PNG` (embedded in Part 1) |
| 4 | Grafana Explore with Loki query (label browser) | [Loki query editor](https://grafana.com/docs/grafana/latest/datasources/loki/query-editor/) – `Loki_label_browser.png` (embedded in Part 3) |
| 5 | SimHub: Plugins list or sidebar with Sim Steward and way to open settings | Capture locally; save as `docs/guides/images/simhub-sim-steward-entry.png` if desired |
| 6 | SimHub: **TELEMETRY (GRAFANA CLOUD)** section (Loki URL, Username, API Key, Connect telemetry) | Capture locally; save as `docs/guides/images/simhub-telemetry-section.png` (redact secrets) |
| 7 | SimHub: Same section after **Connection** “Connected” | Capture locally; save as `docs/guides/images/simhub-telemetry-connected.png` |
| 8 | Grafana: **Dashboards** → **Sim Steward** → **Sim Steward Plugin** with data | Capture locally; save as `docs/guides/images/grafana-dashboard-simsteward.png` |

**Taking local screenshots (SimHub / your dashboard):** Redact the API token and, if desired, the full Loki URL. Use the filenames above so they can be added to the guide with `![Description](images/filename.png)`.

---

## Troubleshooting

### Connection stays Disconnected or shows an error after **Connect telemetry**

- Confirm **Loki URL** ends with `/loki/api/v1/push` and has no trailing space or typo.
- Confirm **Loki Username** is the numeric Instance ID only (no quotes or spaces).
- Confirm **Loki API Key** was pasted correctly and **Key Status** shows **Stored** (tab out of the field or save settings after pasting).
- Ensure the API token has permission to write logs (e.g. “MetricsPublisher” or “Logs” role).
- Check firewall or corporate proxy: the plugin sends HTTPS to `logs-prod-*.grafana.net`; that must be allowed.

### Key Status stays “Not set”

- Type or paste the API token into **Loki API Key**, then trigger save (e.g. tab to another field or click Save). The key is stored when the field is committed. **Clear** removes the stored key; re-paste to set it again.

### No data in Grafana

- Confirm **Send health telemetry** is checked.
- Wait at least one **Flush Interval** (default 5 s) plus a few seconds after **Connection** shows **Connected**.
- In Grafana **Explore**, use `{app="simsteward"}` and widen the time range (e.g. “Last 15 minutes”).
- Confirm there is no error message under **Connection** in the Sim Steward settings; if there is, fix that first (see above).

---

## Related docs

- [Plugin README – Telemetry (Grafana Cloud)](../../plugin/README.md#telemetry-grafana-cloud)
- [Plugin logging schema](../tech/plugin-logging-schema.md) – event types and LogQL examples
- [Grafana MCP](../tech/grafana-mcp.md) – dashboard and MCP usage
