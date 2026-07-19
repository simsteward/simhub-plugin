import { env } from "cloudflare:test";
import schemaSql from "../schema.sql?raw";
import type { Env } from "../src/env";

export const testEnv = env as unknown as Env;

// Apply schema.sql to the test D1. Split on ';' — the schema uses no semicolons
// inside statements so this is safe here.
export async function applySchema(): Promise<void> {
  // Strip full-line comments first — some comments contain ';' which would
  // otherwise fracture statements when we split.
  const withoutComments = schemaSql
    .split("\n")
    .filter((line: string) => !line.trim().startsWith("--"))
    .join("\n");
  const statements = withoutComments
    .split(";")
    .map((s: string) => s.trim())
    .filter((s: string) => s.length > 0);
  for (const stmt of statements) {
    await testEnv.DB.prepare(stmt).run();
  }
  // Storage isn't guaranteed to reset between tests in this pool version, so
  // clear all rows to give each test a clean slate.
  const tables = [
    "user_tokens",
    "subscriptions",
    "users",
    "apps",
    "incidents",
    "incident_index_blobs",
    "SESSIONS",
    "DRIVERS",
    "INCIDENT_CAPTURES",
  ];
  for (const t of tables) {
    await testEnv.DB.prepare(`DELETE FROM ${t}`).run();
  }
}

async function sha256Hex(input: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(input));
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

// Seed a registered app and return its raw token.
export async function seedApp(appId = "test-app", rawToken = "raw-app-token"): Promise<string> {
  const hash = await sha256Hex(rawToken);
  await testEnv.DB.prepare(
    `INSERT INTO apps (app_id, token_hash, version_label, revoked_at, created_at)
     VALUES (?,?,?,NULL,?) ON CONFLICT(app_id) DO UPDATE SET token_hash=excluded.token_hash, revoked_at=NULL`,
  )
    .bind(appId, hash, "test", new Date().toISOString())
    .run();
  return rawToken;
}

// Seed a user + active subscription and an initial (unrevoked) user_token.
export async function seedUserWithToken(
  email: string,
  rawUserToken: string,
  tier = "free",
  status = "active",
): Promise<string> {
  const now = new Date().toISOString();
  const userId = crypto.randomUUID();
  await testEnv.DB.prepare(
    `INSERT INTO users (user_id, email, created_at) VALUES (?,?,?)`,
  )
    .bind(userId, email, now)
    .run();
  await testEnv.DB.prepare(
    `INSERT INTO subscriptions (user_id, tier, status, updated_at) VALUES (?,?,?,?)`,
  )
    .bind(userId, tier, status, now)
    .run();
  const tokenHash = await sha256Hex(rawUserToken);
  // Root mint: lineage_id = its own token_id, same convention as auth.ts devicePoll.
  const tokenId = crypto.randomUUID();
  await testEnv.DB.prepare(
    `INSERT INTO user_tokens (token_id, user_id, token_hash, lineage_id, created_at) VALUES (?,?,?,?,?)`,
  )
    .bind(tokenId, userId, tokenHash, tokenId, now)
    .run();
  return userId;
}

export function jsonRequest(url: string, method: string, body: unknown, headers: Record<string, string> = {}): Request {
  return new Request(url, {
    method,
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
}
