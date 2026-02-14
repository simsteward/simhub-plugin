# OTLP Telemetry Integration Patch Plan

**Date:** 2026-02-14  
**Status:** Ready to apply (no edits made yet)  
**Build validation:** Passed (`dotnet build plugin/SimSteward.csproj`)

---

## 1. Root Cause Analysis

### Why OTLP Gateway Usage Fails

| Aspect | Current behavior | Expected (OTLP) |
|--------|------------------|-----------------|
| **Payload format** | Loki push JSON: `{"streams":[{"stream":{...},"values":[["<ns>","<line>"],...]}]}` | OTLP JSON: `{"resourceLogs":[{"resource":{...},"scopeLogs":[{"scope":{...},"logRecords":[...]}]}]}` |
| **Endpoint path** | User configures full URL (e.g. `/loki/api/v1/push` or `/otlp/v1/logs`) | OTLP expects `/otlp/v1/logs` or `/v1/logs` with OTLP payload |
| **Auth** | Basic auth already correct (username+apikey or pre-encoded token) | Same — no change needed |
| **Content-Type** | `application/json` | Same — no change needed |

**Conclusion:** The plugin sends Loki format to an OTLP endpoint. The server rejects the payload because it expects `resourceLogs`/`scopeLogs`/`logRecords`, not `streams`. Auth is fine; format is wrong.

---

## 2. File-by-File Edits

### 2.1 `plugin/src/Telemetry/LokiExporter.cs`

**Changes:**

1. Add OTLP endpoint detection and payload builder.
2. In `FlushAsync`, choose payload format based on URL path.
3. Improve error messages to always include HTTP code + response body.

#### 2.1.1 Add helper to detect OTLP endpoint (after line 19, before `_gate`)

```csharp
        private static bool IsOtlpEndpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string path = url.TrimEnd('/');
            return path.EndsWith("/otlp/v1/logs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)
                || path.IndexOf("/otlp/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
```

#### 2.1.2 Replace payload selection in FlushAsync (around line 159)

**Before:**
```csharp
                string payload = BuildLokiPushPayload(cfg, batch);
```

**After:**
```csharp
                bool useOtlp = IsOtlpEndpoint(cfg.LokiUrl);
                string payload = useOtlp ? BuildOtlpPushPayload(cfg, batch) : BuildLokiPushPayload(cfg, batch);
```

#### 2.1.3 Improve error message for non-2xx (line 200)

**Before:**
```csharp
                        LastError = $"Loki HTTP {(int)response.StatusCode} {response.StatusDescription} {body}".Trim();
```

**After:**
```csharp
                        string bodyPreview = string.IsNullOrWhiteSpace(body) ? "" : " " + body.Trim();
                        if (bodyPreview.Length > 200) bodyPreview = bodyPreview.Substring(0, 200) + "...";
                        LastError = $"HTTP {(int)response.StatusCode} {response.StatusDescription}{bodyPreview}".Trim();
```

#### 2.1.4 Improve WebException error message (around line 255)

**Before:**
```csharp
                    LastError = string.IsNullOrWhiteSpace(body) ? webEx.Message : $"{webEx.Message} {body}";
```

**After:**
```csharp
                    int statusCode = 0;
                    if (webEx.Response is HttpWebResponse hr) statusCode = (int)hr.StatusCode;
                    string bodyPreview = string.IsNullOrWhiteSpace(body) ? "" : " " + body.Trim();
                    if (bodyPreview.Length > 200) bodyPreview = bodyPreview.Substring(0, 200) + "...";
                    LastError = statusCode > 0
                        ? $"HTTP {statusCode} {webEx.Message}{bodyPreview}".Trim()
                        : (string.IsNullOrWhiteSpace(body) ? webEx.Message : $"{webEx.Message}{bodyPreview}").Trim();
```

#### 2.1.5 Add `BuildOtlpPushPayload` method (after `BuildLokiPushPayload`, before `AppendJsonProp`)

```csharp
        private static string BuildOtlpPushPayload(TelemetryConfig cfg, List<QueuedLine> lines)
        {
            // OTLP JSON: resourceLogs/scopeLogs/logRecords per OpenTelemetry proto.
            // SeverityNumber: 9=INFO, 17=ERROR. Body is stringValue.
            var sb = new StringBuilder(lines.Count * 160);
            sb.Append("{\"resourceLogs\":[{\"resource\":{\"attributes\":[");
            AppendOtlpAttr(sb, "service.name", "simsteward", true);
            sb.Append(",{\"key\":\"device_id\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.DeviceId ?? ""));
            sb.Append("\"}},{\"key\":\"install_id\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.InstallId ?? ""));
            sb.Append("\"}},{\"key\":\"plugin_version\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.PluginVersion ?? ""));
            sb.Append("\"}},{\"key\":\"schema\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.SchemaVersion ?? "1"));
            sb.Append("\"}}]},\"scopeLogs\":[{\"scope\":{\"name\":\"simsteward.plugin\",\"version\":\"1.0.0\"},\"logRecords\":[");

            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(",");
                int sev = lines[i].IsException ? 17 : 9;  // ERROR : INFO
                string sevText = lines[i].IsException ? "Error" : "Info";
                sb.Append("{\"timeUnixNano\":\"");
                sb.Append(lines[i].TimestampNs);
                sb.Append("\",\"observedTimeUnixNano\":\"");
                sb.Append(lines[i].TimestampNs);
                sb.Append("\",\"severityNumber\":");
                sb.Append(sev);
                sb.Append(",\"severityText\":\"");
                sb.Append(sevText);
                sb.Append("\",\"body\":{\"stringValue\":\"");
                sb.Append(JsonEscape(lines[i].Message));
                sb.Append("\"}}");
            }

            sb.Append("]}]}]}");
            return sb.ToString();
        }

        private static void AppendOtlpAttr(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append(",");
            sb.Append("{\"key\":\"");
            sb.Append(JsonEscape(key));
            sb.Append("\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(value ?? ""));
            sb.Append("\"}}");
        }
```

**Note:** The first call to `AppendOtlpAttr` passes `true`; the subsequent attributes are inlined. Simplify by inlining all attributes (the helper is optional). Revised minimal version without helper:

```csharp
        private static string BuildOtlpPushPayload(TelemetryConfig cfg, List<QueuedLine> lines)
        {
            var sb = new StringBuilder(lines.Count * 160);
            sb.Append("{\"resourceLogs\":[{\"resource\":{\"attributes\":[");
            sb.Append("{\"key\":\"service.name\",\"value\":{\"stringValue\":\"simsteward\"}}");
            sb.Append(",{\"key\":\"device_id\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.DeviceId ?? ""));
            sb.Append("\"}},{\"key\":\"install_id\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.InstallId ?? ""));
            sb.Append("\"}},{\"key\":\"plugin_version\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.PluginVersion ?? ""));
            sb.Append("\"}},{\"key\":\"schema\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.SchemaVersion ?? "1"));
            sb.Append("\"}}]},\"scopeLogs\":[{\"scope\":{\"name\":\"simsteward.plugin\",\"version\":\"1.0.0\"},\"logRecords\":[");

            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(",");
                int sev = lines[i].IsException ? 17 : 9;
                string sevText = lines[i].IsException ? "Error" : "Info";
                sb.Append("{\"timeUnixNano\":\"");
                sb.Append(lines[i].TimestampNs);
                sb.Append("\",\"observedTimeUnixNano\":\"");
                sb.Append(lines[i].TimestampNs);
                sb.Append("\",\"severityNumber\":");
                sb.Append(sev);
                sb.Append(",\"severityText\":\"");
                sb.Append(sevText);
                sb.Append("\",\"body\":{\"stringValue\":\"");
                sb.Append(JsonEscape(lines[i].Message));
                sb.Append("\"}}");
            }

            sb.Append("]}]}]}");
            return sb.ToString();
        }
```

---

### 2.2 `plugin/src/Settings/SettingsControl.xaml.cs`

**Change:** Update button label from "Authenticate / Connect" to "Connect telemetry" (or "Test heartbeat" when appropriate).

#### 2.2.1 In `RefreshTelemetryConnectionUi` (lines 96–104)

**Before:**
```csharp
            if (snapshot.State == TelemetryConnectionState.Connected || snapshot.State == TelemetryConnectionState.Connecting)
            {
                GrafanaTelemetryConnectButton.Content = "Click to disconnect";
            }
            else
            {
                GrafanaTelemetryConnectButton.Content = "Authenticate / Connect";
            }
```

**After:**
```csharp
            if (snapshot.State == TelemetryConnectionState.Connected || snapshot.State == TelemetryConnectionState.Connecting)
            {
                GrafanaTelemetryConnectButton.Content = "Click to disconnect";
            }
            else
            {
                GrafanaTelemetryConnectButton.Content = "Connect telemetry";
            }
```

---

### 2.3 `plugin/README.md`

**Change:** Update telemetry section wording.

#### 2.3.1 Loki URL description (around line 101)

**Before:**
```
- **Loki URL** — Full push URL. You must include the path `/loki/api/v1/push`; the plugin does not append it. For this project's stack (https://wgutmann.grafana.net), get the Loki push URL from **Grafana Cloud → your stack → Logs → Send data** (e.g. `https://logs-prod-<region>.grafana.net/loki/api/v1/push`).
```

**After:**
```
- **Loki URL** — Full push URL. Use either:
  - **Loki:** `https://logs-prod-<region>.grafana.net/loki/api/v1/push` (Loki push format)
  - **OTLP:** `https://otlp-gateway-prod-<region>.grafana.net/otlp/v1/logs` (OTLP JSON format)
  The plugin auto-detects OTLP when the URL path contains `/otlp/` or ends with `/v1/logs`. Get URLs from **Grafana Cloud → your stack → Logs** or **OpenTelemetry** connection tiles.
```

#### 2.3.2 Button description (around line 110)

**Before:**
```
After saving, click **Authenticate / Connect** to run a test push. Connection status appears next to the button.
```

**After:**
```
After saving, click **Connect telemetry** to run a test heartbeat push. Connection status reflects successful heartbeat delivery.
```

---

### 2.4 `plugin/src/Telemetry/TelemetryManager.cs`

**Change:** Update error message for missing credentials to be mode-agnostic.

#### 2.4.1 In `ConnectAndTestAsync` (line 79)

**Before:**
```csharp
                snapshot.LastError = "Missing Loki URL/username/API key";
```

**After:**
```csharp
                snapshot.LastError = "Missing URL or API key";
```

---

## 3. Auth Behavior (No Code Change)

Current auth logic is correct:

- **Username + API key:** `Basic base64(username:apikey)`
- **Pre-encoded token:** Username empty → `Basic <apikey>` (apikey is raw Base64)

Both work with Grafana OTLP Basic auth. No changes needed.

---

## 4. Connection Status Logic (No Change)

Status is already driven by successful heartbeat push:

- `LastSuccessUtc > MinValue` → Connected
- `LastError` non-empty → Error
- `_isConnecting` → Connecting

`ConnectAndTestAsync` enqueues a heartbeat and flushes; success sets `LastSuccessUtc`. No changes needed.

---

## 5. Build Validation

```powershell
cd c:\dev\sim-steward-plugin
dotnet build plugin/SimSteward.csproj --verbosity minimal
```

**Expected:** Build succeeded, 0 warnings, 0 errors.

---

## 6. Self-Review: Risks and Regressions

| Risk | Mitigation |
|------|------------|
| OTLP detection false positive (e.g. URL contains `/otlp/` in query) | Detection uses path; query params ignored. Low risk. |
| OTLP detection false negative (nonstandard path) | User can ensure URL ends with `/otlp/v1/logs` or `/v1/logs`. Document in README. |
| Loki users with existing URLs | Loki URLs unchanged; no regression. |
| JSON escaping in OTLP body | Reuse existing `JsonEscape`; handles `\`, `"`, `\n`, etc. |
| Large response body in error | Truncate to 200 chars to avoid UI overflow. |

---

## 7. Test Cases

| Scenario | Expected |
|----------|----------|
| Loki URL (`/loki/api/v1/push`) + valid creds | Loki payload sent; 200 → Connected |
| OTLP URL (`/otlp/v1/logs`) + valid creds | OTLP payload sent; 200 → Connected |
| OTLP URL + invalid creds | 401 + body in LastError; status Error |
| OTLP URL + wrong format (simulated) | 400 + body in LastError |
| Pre-encoded Basic token (username empty) | Auth header `Basic <token>`; works if token valid |
| Username + API key | Auth header `Basic base64(user:key)`; works |
| Button label when disconnected | "Connect telemetry" |
| Button label when connected | "Click to disconnect" |

---

## 8. Summary of Changed Files

| File | Edits |
|------|-------|
| `plugin/src/Telemetry/LokiExporter.cs` | Add `IsOtlpEndpoint`, `BuildOtlpPushPayload`; payload selection in FlushAsync; improve error messages |
| `plugin/src/Settings/SettingsControl.xaml.cs` | "Authenticate / Connect" → "Connect telemetry" |
| `plugin/README.md` | Update Loki URL and button descriptions |
| `plugin/src/Telemetry/TelemetryManager.cs` | "Missing Loki URL/username/API key" → "Missing URL or API key" |

---

## 9. Post-Apply Checklist

1. Run `dotnet build plugin/SimSteward.csproj` — must succeed.
2. Run `plugin/scripts/deploy-plugin.ps1` per delegation.mdc.
3. In SimHub: test with Loki URL and OTLP URL; verify Connected/Error and error text.
4. Update `docs/tech/plugin-logging-schema.md` if OTLP resource attributes should be documented (optional).
