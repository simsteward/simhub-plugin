// Small response helpers + a cheap KV-backed rate limiter.
import type { Env } from "./env";

export function json(body: unknown, status = 200, headers: HeadersInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });
}

export function errorJson(error: string, status: number): Response {
  return json({ error }, status);
}

export function html(body: string, status = 200, headers: HeadersInit = {}): Response {
  return new Response(body, {
    status,
    headers: { "Content-Type": "text/html; charset=utf-8", ...headers },
  });
}

export async function readJson<T>(request: Request): Promise<T | null> {
  try {
    return (await request.json()) as T;
  } catch {
    return null;
  }
}

// Reads a body as a plain object from either JSON or form-urlencoded content
// (the admin HTML forms submit the latter). Returns null on parse failure.
export async function readBody<T extends Record<string, unknown>>(
  request: Request,
): Promise<T | null> {
  const contentType = request.headers.get("Content-Type") ?? "";
  try {
    if (contentType.includes("application/x-www-form-urlencoded")) {
      const form = await request.formData();
      const obj: Record<string, unknown> = {};
      for (const [k, v] of form.entries()) obj[k] = v;
      return obj as T;
    }
    return (await request.json()) as T;
  } catch {
    return null;
  }
}

// True when the request came from an HTML form (wants an HTML redirect-ish reply
// rather than a JSON body).
export function wantsHtml(request: Request): boolean {
  return new URL(request.url).searchParams.get("_html") === "1";
}

// Fixed-window rate limit keyed in KV. Returns true if the caller is over limit.
// Cheap insurance against brute force, not a precise distributed limiter — the
// window is a coarse 60s bucket. `key` should already be namespaced by caller.
//
// NOTE: the KV counter is best-effort and eventually-consistent by design.
// KV reads can serve a slightly stale value and concurrent increments can race
// (read-modify-write is not atomic), so the effective limit may be exceeded
// under burst/concurrent load. That trade-off is intentional: this is a cheap
// brute-force speed bump, not a hard quota. Anything needing exact enforcement
// should use a Durable Object counter instead.
export async function isRateLimited(
  env: Env,
  key: string,
  max: number,
  windowSec = 60,
): Promise<boolean> {
  const bucket = Math.floor(Date.now() / 1000 / windowSec);
  const kvKey = `ratelimit:${key}:${bucket}`;
  const current = parseInt((await env.DEVICE_CODES.get(kvKey)) ?? "0", 10);
  if (current >= max) return true;
  await env.DEVICE_CODES.put(kvKey, String(current + 1), {
    expirationTtl: windowSec * 2,
  });
  return false;
}
