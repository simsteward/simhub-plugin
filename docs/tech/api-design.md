# API Design

API contract between the Sim Steward SimHub plugin (client) and the Cloudflare Worker (server). This repo owns the contract; the Worker repo implements it when built.

## Endpoint

| Property | Value |
|----------|-------|
| Method | POST |
| Path | `/incident` (or TBD when Worker is implemented) |
| Base URL | Configurable in plugin settings (e.g., `https://steward.example.com`) |
| Content-Type | `text/csv` (request), `application/json` (response) |

## Request: Telemetry Payload

**Content-Type:** `text/csv`

**Headers:**
- `Content-Type: text/csv; charset=utf-8`
- `X-Session-Num: {SessionNum}` (optional; can be in CSV metadata)
- `X-Session-Tick: {SessionTick}` (optional; can be in CSV metadata)
- Beta: `X-License-Key: {key}` (Alpha: omit)

**Body:** CSV with metadata row(s) followed by time-series data. Token Diet format (FR-A-009) for minimal token count.

### CSV Schema

**Metadata (required, first rows or header block):**
- `SessionNum` – iRacing session number
- `SessionTick` – Tick at incident trigger (for replay sync to IncidentTime - 30s)
- `IncidentTime` – Timestamp or tick when incident was detected

**Time-Series Rows (FR-A-004, Token Diet):**

| Column | Description | Unit/Format |
|--------|-------------|-------------|
| Time | Elapsed seconds from incident - 30 | float |
| Speed | Speed | mph or m/s (specify) |
| BrakePct | Brake pressure 0–100 | 0–100 |
| SteerPct | Steering -100 to 100 | -100 to 100 |
| Gap | Gap to car ahead | m |
| Overlap | Overlap with car alongside | m |

Sampling: ~20 Hz (50ms) recommended for 60s window ≈ 1200 rows. Adjust per token budget.

Example (minimal):

```csv
SessionNum,12345
SessionTick,987654
IncidentTime,987654
Time,Speed,BrakePct,SteerPct,Gap,Overlap
-30.0,125.3,0,2.1,12.5,0
-29.95,126.1,0,1.8,12.2,0
...
```

## Response: AI Ruling (FR-A-011)

**Content-Type:** `application/json`

**Status:** 200 OK on success; 4xx/5xx on error (plugin handles gracefully).

**Schema:**

```json
{
  "shortSummary": "string",
  "detailedReport": "string",
  "ruling": "string",
  "protestStatement": "string",
  "verdict": "PlayerAtFault | RacingIncident | OpponentAtFault"
}
```

| Field | Description |
|-------|-------------|
| shortSummary | 1-sentence synopsis |
| detailedReport | Chronological timeline of inputs and physics events |
| ruling | Clear verdict of fault (human-readable) |
| protestStatement | Formal text block ready for iRacing protest submission |
| verdict | Enum for UI grading (Red/Yellow/Skull per FR-A-014) |

**Verdict mapping (FR-A-014):**
- `OpponentAtFault` → Red
- `RacingIncident` → Yellow
- `PlayerAtFault` → Skull

## Error Response

On 4xx/5xx, body may be JSON:

```json
{
  "error": "string",
  "code": "optional"
}
```

Plugin should display error state and retain incident for retry or manual review.

## Mock for Development

Until the Worker exists, the plugin can:
1. Use a configurable URL (blank = no POST, or mock server)
2. Return mock JSON from a local stub or hardcoded sample
3. Validate request serialization against this contract
