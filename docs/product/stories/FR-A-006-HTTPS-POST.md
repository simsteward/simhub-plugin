# HTTPS POST to Backend

**FR-IDs:** FR-A-006  
**Status:** Draft  
**Created:** 2026-02-11

## Description

As a driver, I need the plugin to send my incident data to the AI backend and bring back a ruling -- all in the background so I can keep racing. If the backend isn't available yet, the plugin should still work with mock responses for testing.

## Acceptance Criteria

- [ ] Plugin sends `Incident.SerializedPayload` (CSV) via HTTPS POST to a configurable endpoint URL
- [ ] Request format matches API contract (`docs/tech/api-design.md`): headers, content-type, body
- [ ] Plugin parses JSON response per API contract response schema (shortSummary, detailedReport, ruling, protestStatement, verdict)
- [ ] Parsed response is stored on `Incident.Ruling`; Status advances to `Ruled`
- [ ] Settings UI includes a field for Worker endpoint URL
- [ ] **Mock mode:** When URL is empty or set to "mock", return a hardcoded `StewardResponse` without making a network call
- [ ] **Timeout:** Requests time out after a configurable duration (default: 30 seconds)
- [ ] **Retry:** On transient failure (network error, 5xx), retry once after 5 seconds; on second failure, set Status to Error
- [ ] **Error handling:** On non-200 response or parse failure, set `Incident.Status = Error` and `Incident.ErrorMessage` with detail
- [ ] POST is fully async; does not block `DataUpdate` loop

## Subtasks

- [ ] Add static or injected `HttpClient` with timeout configuration
- [ ] Add plugin settings: `WorkerEndpointUrl` (string), `RequestTimeoutSeconds` (int, default 30)
- [ ] Implement `TelemetryPoster.PostAsync(Incident)` with correct headers and body per contract
- [ ] Parse JSON response into `StewardResponse` model (matching API contract response schema)
- [ ] Store `StewardResponse` on `Incident.Ruling`; advance Status to `Ruled`
- [ ] Implement mock mode: detect empty/mock URL, return hardcoded response
- [ ] Implement retry: on transient failure, wait 5s, retry once
- [ ] On final failure: set Status=Error, ErrorMessage=detail
- [ ] Wire into pipeline: Serialization completes -> POST fires automatically
- [ ] Expose `SimSteward.LastPostStatus` as SimHub property for debugging

## Dependencies

- FR-A-004-005-Telemetry-Serialization (provides `Incident.SerializedPayload`)
- API contract (`docs/tech/api-design.md`) for request and response schemas

## Notes

- The Worker lives in a separate private repo. This plugin must work end-to-end with mock mode until the Worker is deployed.
- Beta will add `X-License-Key` header; Alpha omits it.
- `StewardResponse` model fields: `shortSummary` (string), `detailedReport` (string), `ruling` (string), `protestStatement` (string), `verdict` (string enum: PlayerAtFault, RacingIncident, OpponentAtFault).
- Request cancellation: if the plugin is shutting down or session ends, cancel in-flight requests via `CancellationToken`.
