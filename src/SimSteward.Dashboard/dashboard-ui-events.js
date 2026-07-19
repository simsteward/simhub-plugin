/**
 * Shared UI-event telemetry helper for all Sim Steward dashboard pages.
 * Extracted from index.html so test-rig.html stops hand-rolling a duplicate of this without the
 * Sentry breadcrumb (found during the dashboard alignment pass). replay-incident-index.html was
 * later merged into index.html's "Replay Index" tab.
 *
 * Contract: the including page must have a module-scope `ws` variable (a WebSocket or null),
 * matching the existing convention already used by each page's own `send()` function.
 */

/** Structured UI telemetry for Loki (plugin -> plugin-structured.jsonl; ingestion outside plugin). */
function sendDashboardUiEvent({ element_id, event_type, message, value }) {
  if (typeof Sentry !== 'undefined') {
    Sentry.addBreadcrumb({
      category: 'ui.' + event_type,
      message: message,
      data: { element_id },
      level: 'info',
    });
  }
  if (typeof ws === 'undefined' || !ws || ws.readyState !== WebSocket.OPEN) return false;
  const o = { action: 'log', event: 'dashboard_ui_event', element_id, event_type, message };
  if (value !== undefined && value !== null) o.value = value;
  try {
    ws.send(JSON.stringify(o));
    return true;
  } catch (err) {
    if (typeof Sentry !== 'undefined') Sentry.captureException(err);
    return false;
  }
}
