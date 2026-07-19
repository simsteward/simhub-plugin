// Router entry. Hand-rolled dispatch on method + pathname — no framework needed
// at this route count.
import type { Env } from "./env";
import { json, errorJson } from "./http";
import { deviceStart, devicePoll, tokenExchange, approve } from "./auth";
import {
  sessionComplete,
  incidentsPush,
  putIncidentIndex,
  getIncidentIndex,
  getSession,
} from "./data";
import { createUser, patchSubscription, adminPage } from "./admin";

// Parses a trailing numeric path segment, e.g. /incident-index/12345 → 12345.
function trailingId(pathname: string, prefix: string): number | null {
  if (!pathname.startsWith(prefix)) return null;
  const rest = pathname.slice(prefix.length);
  if (!/^\d+$/.test(rest)) return null;
  return parseInt(rest, 10);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "") || "/";
    const method = request.method.toUpperCase();

    try {
      // --- Health (no auth) ---
      if (method === "GET" && path === "/health") return json({ status: "ok" });

      // --- Auth (app_token only) ---
      if (method === "POST" && path === "/auth/device/start") return deviceStart(request, env);
      if (method === "POST" && path === "/auth/device/poll") return devicePoll(request, env);
      if (method === "POST" && path === "/auth/token") return tokenExchange(request, env);

      // --- Approval page (Access-gated) ---
      if ((method === "GET" || method === "POST") && path === "/approve") {
        return approve(request, env);
      }

      // --- Data plane (Bearer access_token) ---
      if (method === "POST" && path === "/session-complete") return sessionComplete(request, env);
      if (method === "POST" && path === "/incidents/push") return incidentsPush(request, env);

      const indexId = trailingId(path, "/incident-index/");
      if (indexId !== null) {
        if (method === "PUT") return putIncidentIndex(request, env, indexId);
        if (method === "GET") return getIncidentIndex(request, env, indexId);
      }

      const sessionId = trailingId(path, "/session/");
      if (sessionId !== null && method === "GET") return getSession(request, env, sessionId);

      // --- Admin (Access-gated) ---
      if (method === "GET" && path === "/admin") return adminPage(request, env);
      if (method === "POST" && path === "/admin/users") return createUser(request, env);

      if (path.startsWith("/admin/subscriptions/")) {
        const userId = decodeURIComponent(path.slice("/admin/subscriptions/".length));
        // PATCH for API callers; POST for the HTML form (can't send PATCH).
        if ((method === "PATCH" || method === "POST") && userId) {
          return patchSubscription(request, env, userId);
        }
      }

      return errorJson("not_found", 404);
    } catch (err) {
      // Never leak internals to the caller; surface a generic 500 with a log
      // for debugging. Truncate the logged message — no code path in this
      // Worker embeds secrets/tokens in an exception message, but an unbounded
      // stack trace (or a pathological error object) is still unwanted log
      // volume, and truncation costs nothing when the message is already short.
      const errStr = String(err).slice(0, 200);
      console.error("unhandled_error", { path, method, error: errStr });
      return errorJson("internal_error", 500);
    }
  },
};
